using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Materials;

namespace ReAnimated.DL1.Assets.Meshes;

public sealed record CharacterExternalResource(string Id,string LogicalName,string ProviderIdentity,ImmutableArray<byte> Payload,CharacterSubsystem Subsystem);

public sealed record Dl1CharacterImportOptions
{
    /// <summary>Explicitly selected companion roots. Filename similarity is never proof of a relationship.</summary>
    public ImmutableArray<RetailAssetLogicalId> CompanionRoots { get; init; } = [];
    public ImmutableArray<CharacterSubsystem> VerifiedNotApplicable { get; init; } = [];
    public ImmutableArray<CharacterExternalResource> ExternalResources { get; init; } = [];
    public ImmutableArray<string> VerifiedMaterialNames { get; init; } = [];
    public string? MaterialProviderResourceId { get; init; }
    public int MaximumResources { get; init; } = 4096;
    public long MaximumTotalBytes { get; init; } = 768L * 1024 * 1024;
}

/// <summary>Direct retail-to-editable import with original payload custody and a bounded dependency closure.</summary>
public static partial class Dl1CharacterImporter
{
    public static async Task<FbxModelAuthoringImportResult> ImportAsync(Dl1MeshData mesh, RetailAssetRecord root,
        IRetailAssetCatalog catalog, Dl1CharacterImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (!mesh.IsStructurallyValid || mesh.Rig is null || !mesh.HasDecodedGeometry)
            throw new InvalidDataException("Complete character import requires decoded geometry and a valid authoring hierarchy.");
        if (options.MaximumResources is <= 0 or > 65536 || options.MaximumTotalBytes is <= 0 or > CustomModelPackageSerializer.MaximumPackageBytes)
            throw new ArgumentException("Character dependency bounds are invalid.", nameof(options));
        if(!options.VerifiedMaterialNames.IsDefaultOrEmpty &&
            !options.ExternalResources.Any(resource=>resource.Subsystem==CharacterSubsystem.Materials))
            throw new InvalidDataException("Verified material bindings require custody of their original database.");
        var resources = ImmutableArray.CreateBuilder<CharacterResourceRecord>();
        var payloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        long externalBytes=0;
        if(mesh.OriginalResourceMetadataJson is { } metadata)
        {
            byte[] original=Encoding.UTF8.GetBytes(metadata);
            if(original.Length>1024*1024 || original.Length>options.MaximumTotalBytes)throw new InvalidDataException("Original archive metadata exceeds its custody bound.");
            string hash=Convert.ToHexStringLower(SHA256.HashData(original));
            string entry=$"character/resources/native-metadata-{hash}.json";
            payloads.Add(entry,ImmutableArray.Create(original));externalBytes=original.Length;
            resources.Add(new() {Id="native-metadata:"+root.Id.LogicalId.StableKey,LogicalName="original-native-resource-metadata.json",ProviderIdentity=root.Id.ProviderId,
                SourceFingerprint=root.Id.SourceFingerprint,ContentSha256=hash,EntryPath=entry,ByteLength=original.Length,Subsystem=CharacterSubsystem.Geometry,
                Status=CharacterDependencyStatus.Preserved,ReferencedBy=[root.Id.LogicalId.StableKey],Detail="Original archive header, item/chunk boundaries, opaque hashes and unknown descriptor fields retained without local paths."});
        }
        foreach(var external in options.ExternalResources)
        {
            if(external.Payload.IsDefaultOrEmpty || external.Payload.Length>options.MaximumTotalBytes-externalBytes)
                throw new InvalidDataException("Original external resource exceeds the character import bound.");
            externalBytes+=external.Payload.Length;
            string hash=Convert.ToHexStringLower(SHA256.HashData(external.Payload.AsSpan()));
            string entry=$"character/resources/external-{resources.Count:D5}-{hash}.bin";
            payloads.Add(entry,external.Payload);
            resources.Add(new() {Id=external.Id,LogicalName=external.LogicalName,ProviderIdentity=external.ProviderIdentity,
                SourceFingerprint=hash,ContentSha256=hash,EntryPath=entry,ByteLength=external.Payload.Length,Subsystem=external.Subsystem,
                Status=CharacterDependencyStatus.Preserved,ReferencedBy=[root.Id.LogicalId.StableKey],Detail="Original shared resource retained with content identity."});
        }
        if(options.MaterialProviderResourceId is { } materialProviderId &&
            !resources.Any(resource=>resource.Id==materialProviderId && resource.Subsystem==CharacterSubsystem.Materials && resource.EntryPath is not null))
            throw new InvalidDataException("The selected character material provider is missing.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(RetailAssetRecord Asset, CharacterSubsystem Subsystem, string Parent)>();
        queue.Enqueue((root, CharacterSubsystem.Geometry, ""));
        foreach (var id in options.CompanionRoots) Resolve(id,
            id.Namespace==RetailAssetNamespace.RpackResource && id.ResourceType==ReAnimated.Codecs.Rp6l.Rp6lResourceTypes.Mesh
                ? CharacterSubsystem.DetachedParts:Classify(id.Name), root.Id.LogicalId.StableKey);
        long total = externalBytes;
        var declaredMaterialNames = mesh.OriginalMaterialDatabase.Entries.Select(material => material.DatabaseName)
            .Concat(mesh.MaterialSlots.Where(slot => slot.MaterialResourceName is not null).Select(slot => slot.MaterialResourceName!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string materialName in declaredMaterialNames)
        {
            if (options.MaterialProviderResourceId is not null)
                await ResolveNativeMaterialAsync(materialName, root.Id.LogicalId.StableKey).ConfigureAwait(false);
            else if (!options.VerifiedMaterialNames.Contains(materialName, StringComparer.OrdinalIgnoreCase))
                ResolveName(materialName, CharacterSubsystem.Materials, root.Id.LogicalId.StableKey);
        }
        foreach (var material in mesh.MaterialSlots)
        {
            if (material.MaterialResourceName is { } name)
            {
                if (options.MaterialProviderResourceId is null && !options.VerifiedMaterialNames.Contains(name,StringComparer.OrdinalIgnoreCase)) ResolveName(name, CharacterSubsystem.Materials, root.Id.LogicalId.StableKey);
                else if(!options.ExternalResources.Any(r=>r.Subsystem==CharacterSubsystem.Materials)) throw new InvalidDataException("Verified material bindings require custody of their original database.");
            }
            else Missing($"material-slot:{material.Index}", CharacterSubsystem.Materials, root.Id.LogicalId.StableKey, "Material identity was not resolved.");
            foreach (var texture in material.ResolvedMaterial?.TextureBindings ?? [])
                if (texture.AssetId is { } id) Resolve(id.LogicalId, CharacterSubsystem.Textures, root.Id.LogicalId.StableKey);
                else Missing(texture.ResourceName ?? $"texture:{texture.TextureNameHash:X8}", CharacterSubsystem.Textures, root.Id.LogicalId.StableKey, "Texture identity was not resolved.");
        }
        ImmutableArray<byte> rootBytes = [];
        while (queue.TryDequeue(out var row))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = row.Asset.Id.LogicalId.StableKey;
            if (!visited.Add(id))
            {
                int previous=Array.FindIndex(resources.ToArray(),r=>r.Id==id);
                if(previous>=0 && row.Parent.Length>0 && !resources[previous].ReferencedBy.Contains(row.Parent,StringComparer.Ordinal))
                    resources[previous]=resources[previous] with {ReferencedBy=resources[previous].ReferencedBy.Add(row.Parent)};
                continue;
            }
            if (visited.Count > options.MaximumResources) throw new InvalidDataException("Character dependency closure exceeds its resource limit.");
            RetailEmbeddedEffectCustody? effectCustody=null;
            if(row.Asset.Source.Kind==RetailAssetSourceKind.RpackEmbeddedEffect)
            {
                if(catalog is not IRetailEmbeddedEffectCatalog embedded)
                    throw new InvalidDataException("Embedded effect provenance is unavailable.");
                effectCustody=await embedded.ReadEmbeddedCustodyAsync(row.Asset,cancellationToken).ConfigureAwait(false);
            }
            RetailRpackResourceCustody? nativeCustody=null;
            if(row.Asset.Source.Kind==RetailAssetSourceKind.Rpack && (row.Asset.Id.ResourceType==ReAnimated.Codecs.Rp6l.Rp6lResourceTypes.Texture ||
                (row.Subsystem==CharacterSubsystem.DetachedParts || row.Asset.Id.LogicalId==root.Id.LogicalId) && row.Asset.Id.ResourceType==ReAnimated.Codecs.Rp6l.Rp6lResourceTypes.Mesh))
            {
                if(catalog is not IRetailRpackResourceCatalog native)
                    throw new InvalidDataException("Complete native resource provenance is unavailable.");
                nativeCustody=await native.ReadRpackResourceCustodyAsync(row.Asset,cancellationToken).ConfigureAwait(false);
            }
            await using Stream stream=effectCustody is not null
                ? new MemoryStream(effectCustody.BundlePayload.AsSpan(effectCustody.Definition.TextOffset,effectCustody.Definition.TextByteLength).ToArray(),writable:false)
                : nativeCustody is not null
                    ? new MemoryStream(nativeCustody.Items.SelectMany(item=>item.Payload).ToArray(),writable:false)
                    : await catalog.OpenReadAsync(row.Asset,cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                total = checked(total + read);
                if (total > options.MaximumTotalBytes) throw new InvalidDataException("Character dependency payloads exceed the bounded import limit.");
                output.Write(buffer, 0, read);
            }
            ImmutableArray<byte> bytes = ImmutableArray.Create(output.ToArray());
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
            string entry = $"character/resources/{resources.Count:D5}-{hash}.bin";
            payloads.Add(entry, bytes);
            int sourceRecordIndex=resources.Count;
            resources.Add(new() { Id = id, LogicalName = row.Asset.Id.Name, ProviderIdentity = row.Asset.Id.ProviderId,
                SourceFingerprint = row.Asset.Id.SourceFingerprint, ContentSha256 = hash, EntryPath = entry, ByteLength = bytes.Length,
                Subsystem = row.Subsystem, Status = CharacterDependencyStatus.Preserved, ReferencedBy = row.Parent.Length == 0 ? [] : [row.Parent],
                Detail = "Original resource bytes retained; unknown records remain opaque." });
            if(nativeCustody is { } textureCustody)
            {
                if(hash!=textureCustody.ContentSha256 || textureCustody.Asset!=row.Asset)
                    throw new InvalidDataException("Native resource custody differs from the selected source.");
                long itemOffset=0;
                var nativeItems=ImmutableArray.CreateBuilder<CharacterNativeItemReceipt>();
                foreach(var item in textureCustody.Items)
                {
                    var chunk=textureCustody.Chunks.Single(value=>value.Descriptor.Index==item.Descriptor.ChunkIndex);
                    nativeItems.Add(new(item.Descriptor.Index,item.Descriptor.ChunkIndex,item.Descriptor.Flags,item.Descriptor.StorageGroupId,item.Descriptor.Unknown,
                        chunk.Descriptor.Flags,chunk.Descriptor.Category,chunk.Descriptor.Unknown0,chunk.Descriptor.Unknown1,itemOffset,item.Payload.Length,item.ContentSha256,chunk.StoredSha256));
                    itemOffset=checked(itemOffset+item.Payload.Length);
                }
                resources[sourceRecordIndex]=resources[sourceRecordIndex] with {NativeResource=new(){HeaderVersion=textureCustody.Header.Version,
                    HeaderUnknown=textureCustody.Header.Unknown,ResourceName=row.Asset.Id.Name,ResourceType=textureCustody.Resource.ResourceType,
                    SourceResourceIndex=textureCustody.Resource.Index,Items=nativeItems.ToImmutable()}};
            }
            if(nativeCustody is {Resource.ResourceType:ReAnimated.Codecs.Rp6l.Rp6lResourceTypes.Mesh} detached)
            {
                try
                {
                    if(detached.Items.Length<5)throw new InvalidDataException("Detached geometry has no complete vertex/index payloads.");
                    var detachedGeometry=CompiledMeshGeometryDecoder.Decode(detached.Items[0].Payload.ToArray(),detached.Items[1].Payload.ToArray(),
                        detached.Items[3].Payload.ToArray(),detached.Items[4].Payload.ToArray(),retailResourceName:detached.Resource.Name,cancellationToken:cancellationToken);
                    if(!detachedGeometry.MaterialDatabase.HasCompleteSlotNames || detachedGeometry.Diagnostics.Any(value=>value.Severity==CompactMeshDiagnosticSeverity.Error))
                        throw new InvalidDataException("Detached mesh material inventory could not be decoded completely.");
                    foreach(var material in detachedGeometry.MaterialDatabase.Entries)
                    {
                        if(string.IsNullOrWhiteSpace(material.DatabaseName))
                            Missing($"detached-material-slot:{id}:{material.Index}",CharacterSubsystem.Materials,id,"The detached material name is missing.");
                        else await ResolveNativeMaterialAsync(material.DatabaseName,id).ConfigureAwait(false);
                    }
                }
                catch(InvalidDataException exception)
                {resources[sourceRecordIndex]=resources[sourceRecordIndex] with {Status=CharacterDependencyStatus.Unsupported,Detail=exception.Message};}
            }
            if(row.Asset.Source.Kind == RetailAssetSourceKind.RpackEmbeddedEffect)
            {
                var custody=effectCustody!;
                string bundleHash=Convert.ToHexStringLower(SHA256.HashData(custody.BundlePayload.AsSpan()));
                string bundleId="effect-bundle:"+custody.Parent.Id.StableKey+":"+bundleHash;
                string bundleEntry="character/resources/effect-bundle-"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bundleId)))+".bin";
                int effectIndex=sourceRecordIndex;
                if(!resources.Any(resource=>resource.Id==bundleId))
                {
                    if(resources.Count>=options.MaximumResources) throw new InvalidDataException("Character resources exceed the import limit.");
                    total=checked(total+custody.BundlePayload.Length);
                    if(total>options.MaximumTotalBytes) throw new InvalidDataException("Character dependency payloads exceed the import limit.");
                    payloads[bundleEntry]=custody.BundlePayload;
                    resources.Add(new(){Id=bundleId,LogicalName="original-effect-bundle-"+bundleHash+".bin",
                        ProviderIdentity=custody.Parent.Id.ProviderId,SourceFingerprint=custody.Parent.Id.SourceFingerprint,
                        ContentSha256=bundleHash,EntryPath=bundleEntry,ByteLength=custody.BundlePayload.Length,
                        Subsystem=CharacterSubsystem.Damage,Status=CharacterDependencyStatus.Preserved,
                        IsOriginalArchive=true,Required=false,ReferencedBy=[id],Detail="Original gathered effect bundle."});
                }
                if(hash!=custody.Definition.ContentSha256 || !bytes.AsSpan().SequenceEqual(
                    custody.BundlePayload.AsSpan(custody.Definition.TextOffset,custody.Definition.TextByteLength)))
                    throw new InvalidDataException("Embedded effect source differs from its gathered bundle.");
                resources[effectIndex]=resources[effectIndex] with {PackedEffect=new()
                {
                    BundleResourceId=bundleId,BundleSha256=bundleHash,StoredName=custody.Definition.Name,Kind=custody.Definition.Kind,
                    ResourceIndex=custody.Resource.Index,ItemIndex=custody.Item.Index,ChunkIndex=custody.Chunk.Index,
                    EntryOffset=custody.Definition.EntryOffset,EntryByteLength=custody.Definition.EntryByteLength,
                    TextOffset=custody.Definition.TextOffset,TextByteLength=custody.Definition.TextByteLength,
                }};
            }
            if (id == root.Id.LogicalId.StableKey) rootBytes = bytes;
            if (row.Asset.Id.Namespace == RetailAssetNamespace.VirtualFile && IsTextScript(row.Asset.Id.Name))
            {
                try
                {
                    string text = new UTF8Encoding(false,true).GetString(bytes.AsSpan());
                    var syntax=NativeCharacterScriptCodec.Parse(text);
                    bool cloth=row.Subsystem==CharacterSubsystem.Cloth || syntax.Calls.Any(c=>c.Name=="!include" && c.QuotedArguments.Any(a=>Path.GetFileName(a.Value).Equals("MeshPartCloth.def",StringComparison.OrdinalIgnoreCase)));
                    if(cloth && resources[sourceRecordIndex].Subsystem==CharacterSubsystem.Ragdoll) resources[sourceRecordIndex]=resources[sourceRecordIndex] with {Subsystem=CharacterSubsystem.Cloth};
                    bool effectSource=Path.GetExtension(row.Asset.Id.Name).Equals(".fx",StringComparison.OrdinalIgnoreCase);
                    var consumedMaterials=effectSource?syntax.Calls.Where(call=>call.Name.Equals("Material",StringComparison.OrdinalIgnoreCase))
                        .Select(call=>call.QuotedArguments.FirstOrDefault(argument=>argument.ArgumentIndex==0)?.Value)
                        .Where(name=>name is not null && Path.GetExtension(name).Equals(".mat",StringComparison.OrdinalIgnoreCase))
                        .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray():[];
                    foreach(string materialName in consumedMaterials)
                        await ResolveNativeMaterialAsync(materialName,id).ConfigureAwait(false);
                    var sourceReferences = new List<string>();
                    if (Path.GetExtension(row.Asset.Id.Name).Equals(".bel", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = Dl1BodyElementsCodec.Read(text);
                        if (body.ForceGenericRelics)
                            sourceReferences.AddRange(await GatherGenericMeshesAsync(body, row.Asset, id).ConfigureAwait(false));
                    }
                    foreach (NativeCharacterCall call in syntax.Calls)
                    {
                        if (call.Name == "DestroyedHeadParts")
                        {
                            if (call.Arguments.Length != 2 || call.QuotedArguments.FirstOrDefault(argument => argument.ArgumentIndex == 0) is not { } template)
                                throw new FormatException("DestroyedHeadParts requires a quoted template and integer count.");
                            foreach (Dl1IndexedMeshName dependency in Dl1IndexedMeshTemplate.Expand(template.Value,
                                Dl1IndexedMeshTemplate.ParseCount(call.Arguments[1])))
                                sourceReferences.Add(dependency.Name);
                        }
                        else sourceReferences.AddRange(call.QuotedArguments.Select(argument => argument.Value));
                    }
                    foreach(var reference in sourceReferences.Where(value=>
                        IsReferencedResource(value) || effectSource && Path.GetExtension(value).Equals(".dds",StringComparison.OrdinalIgnoreCase)))
                    {
                        var subsystem=Path.GetExtension(reference).ToLowerInvariant() switch
                            {".mat"=>CharacterSubsystem.Materials,".dds"=>CharacterSubsystem.Textures,_=>Classify(reference)};
                        if(subsystem==CharacterSubsystem.Materials && (consumedMaterials.Contains(reference,StringComparer.OrdinalIgnoreCase) ||
                            options.VerifiedMaterialNames.Contains(reference,StringComparer.OrdinalIgnoreCase))) continue;
                        if(cloth && Path.GetExtension(reference).Equals(".phx",StringComparison.OrdinalIgnoreCase)) subsystem=CharacterSubsystem.Cloth;
                        if(Path.GetExtension(reference).Equals(".def",StringComparison.OrdinalIgnoreCase)) subsystem=resources[sourceRecordIndex].Subsystem;
                        ResolveName(reference,subsystem,id);
                    }
                }
                catch(Exception e) when(e is FormatException or DecoderFallbackException)
                {
                    resources[sourceRecordIndex]=resources[sourceRecordIndex] with {Status=CharacterDependencyStatus.Unsupported,Detail="Original source retained; dependency syntax or encoding is unsupported: "+e.Message};
                }
            }
        }
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(rootBytes.AsSpan()));
        var bones = mesh.Rig.Bones.Select(b =>
        {
            var entity = mesh.Hierarchy.Entities[b.Index];
            return new CustomModelBone { Index = b.Index, FbxObjectId = b.Index + 1L, Name = b.Name, ParentIndex = b.ParentIndex,
                LocalBindTransform = b.LocalBindPose, ExactLocalBindMatrix = Matrix(entity.LocalMatrix), Kind = b.Kind,
                IsWeighted = mesh.Surfaces.SelectMany(s => s.Submeshes).Any(s => s.BonePaletteEntityIndexes.Contains((short)b.Index)),
                LocalBounds = new(new(entity.Bounds.CenterX, entity.Bounds.CenterY, entity.Bounds.CenterZ), new(entity.Bounds.HalfX, entity.Bounds.HalfY, entity.Bounds.HalfZ)) };
        }).ToImmutableArray();
        var globals = new TransformMatrix[bones.Length];
        foreach (var b in bones) globals[b.Index] = b.ParentIndex < 0 ? b.ExactLocalBindMatrix : globals[b.ParentIndex] * b.ExactLocalBindMatrix;
        var previews = ImmutableDictionary.CreateBuilder<string,ImmutableArray<byte>>(StringComparer.Ordinal);
        var materials = mesh.MaterialSlots.Select(s => new CustomModelMaterial { Id = StableId(sourceHash, $"material:{s.Index}"),
            Name = s.DeclaredDatabaseName ?? s.DatabaseName, ExistingDl1MaterialReference = s.DeclaredDatabaseName ?? s.MaterialResourceName ?? s.DatabaseName,
            Textures=s.ResolvedMaterial?.BaseColorPreview is { } preview ? [CreateBaseColorPreview(preview)] : [] }).ToImmutableArray();
        CustomModelTextureBinding CreateBaseColorPreview(ReAnimated.DL1.Assets.Materials.Dl1TexturePreviewData preview)
        {
            var bytes=CreateDdsPreview(preview);
            string hash=Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
            string path=$"textures/stock-preview-{hash}.dds";
            previews[path]=bytes;
            return new() {Id=StableId(sourceHash,path),Semantic=CustomModelTextureSemantic.BaseColor,SourceKind=CustomModelTextureSourceKind.ExistingDl1Material,
                ColorSpace=CustomModelTextureColorSpace.Srgb,DisplayName=preview.ResourceName+".dds",OriginalReference=preview.AssetId.LogicalId.StableKey,
                PackageEntryPath=path,ContentSha256=hash,MediaType="image/vnd-ms.dds"};
        }
        var surfaces = ImmutableArray.CreateBuilder<FbxModelSurface>();
        var bindings = ImmutableArray.CreateBuilder<CharacterMorphBinding>();
        var worlds = mesh.Hierarchy.ReconstructGlobalMatrices();
        foreach (var s in mesh.Surfaces)
        {
            var geometry = new GeometrySourceComponent($"entity:{s.EntityIndex}/lod:{s.LodIndex}", s.Vertices.Select(v => Vector(v.Position)).ToImmutableArray())
                { Coordinates = new(1, TransformMatrix.Identity) };
            var parts = s.Submeshes.Count == 0 ? new[] { new Dl1MeshSubmesh(0, 0, s.Indices.Count, s.MaterialSlotIndex, []) } : s.Submeshes;
            foreach (var part in parts)
            {
                bool skinned = mesh.Hierarchy.Entities[s.EntityIndex].EntityType.HasFlag(CompactMeshEntityType.SkinnedMesh) &&
                    part.SkinBindingMode != Dl1SkinBindingMode.StaticEntityTransformIgnoredPalette;
                if (skinned && part.SkinBindingMode is not (Dl1SkinBindingMode.ExplicitVertexWeights or Dl1SkinBindingMode.RigidIndexedPalette))
                    throw new InvalidDataException($"Unresolved skin binding in '{s.Name}'.");
                var palette = skinned ? part.BonePaletteEntityIndexes.Select(i => (int)i).ToImmutableArray() : [];
                if (palette.Any(i => i < 0 || i >= bones.Length)) throw new InvalidDataException("Skin palette references a missing authoring bone.");
                var remap = new Dictionary<ushort, uint>();
                var vertices = ImmutableArray.CreateBuilder<FbxModelVertex>();
                var corners = ImmutableArray.CreateBuilder<GeometrySourceCorner>();
                var indices = ImmutableArray.CreateBuilder<uint>();
                for (int i = 0; i < part.IndexCount; i++)
                {
                    ushort sourceIndex = s.Indices[part.FirstIndex + i];
                    if (!remap.TryGetValue(sourceIndex, out uint index))
                    {
                        index = (uint)vertices.Count; remap.Add(sourceIndex, index);
                        var v = s.Vertices[sourceIndex];
                        double[] weights = part.SkinBindingMode == Dl1SkinBindingMode.RigidIndexedPalette ? [1, 0, 0, 0] : [v.BlendWeights.X, v.BlendWeights.Y, v.BlendWeights.Z, v.BlendWeights.W];
                        int[] local = [v.LocalBlendIndices.X, v.LocalBlendIndices.Y, v.LocalBlendIndices.Z, v.LocalBlendIndices.W];
                        var active = Enumerable.Range(0, 4).Where(k => skinned && weights[k] > 0).ToArray();
                        if (active.Any(k => local[k] >= palette.Length)) throw new InvalidDataException("A vertex weight is outside its original palette.");
                        var position = Vector(v.Position); var normal = Vector(v.Normal);
                        if (!skinned) { var placement = Matrix(worlds[s.EntityIndex]); position = placement.TransformPoint(position); normal = placement.TransformDirection(normal).Normalized(); }
                        vertices.Add(new(position, normal, v.TextureCoordinate0.X, v.TextureCoordinate0.Y,
                            active.Select(k => local[k]).ToImmutableArray(), active.Select(k => weights[k]).ToImmutableArray()));
                        corners.Add(new(sourceIndex, sourceIndex));
                    }
                    indices.Add(index);
                }
                var targets = ImmutableArray.CreateBuilder<FbxModelMorphTarget>();
                string surfaceId = $"{s.EntityIndex}/{s.LodIndex}/{part.Index}";
                foreach (var m in mesh.MorphTargets)
                {
                    var sets = m.Bindings.Where(b => b.EntityIndex == s.EntityIndex && b.LodIndex == s.LodIndex).SelectMany(b => b.PositionDeltaSets).ToArray();
                    if (sets.Length == 0) continue;
                    if (sets.Length != 1 || sets[0].PositionDeltas.Count != s.VertexCount) throw new InvalidDataException("Original morph payload is ambiguous or incomplete.");
                    var deltas = new Vector3D[vertices.Count];
                    foreach (var (sourceIndex, index) in remap) deltas[index] = Vector(sets[0].PositionDeltas[sourceIndex]);
                    uint descriptor = Dl1NameHash.Compute(m.Name);
                    targets.Add(new(m.Name, descriptor, m.Index + 1L, m.Index + 1L, deltas.ToImmutableArray()));
                    bindings.Add(new(m.Index, m.Index, m.Name, descriptor, surfaceId, s.EntityIndex, s.LodIndex, s.VertexCount, m.PayloadStatus.ToString()));
                }
                var material = materials.FirstOrDefault(m => m.Id == StableId(sourceHash, $"material:{part.MaterialSlotIndex}")) ?? throw new InvalidDataException("Surface material slot is missing.");
                surfaces.Add(new(surfaceId, s.Name, material.Id, vertices.ToImmutable(), indices.ToImmutable(), palette,
                    palette.Select(i => globals[i].InvertedAffine()).ToImmutableArray(), skinned) { SourceGeometry = geometry,
                    SourceCorners = corners.ToImmutable(), SourceTriangles = Enumerable.Range(0, part.IndexCount / 3).Select(i => new GeometrySourceTriangle(part.FirstIndex / 3 + i, 0)).ToImmutableArray(), MorphTargets = targets.ToImmutable() });
            }
        }
        var weighted=surfaces.SelectMany(surface=>surface.Vertices.SelectMany(v=>v.BoneIndices.Select((local,index)=>(local,index))
            .Where(pair=>v.BoneWeights[pair.index]>0).Select(pair=>surface.PaletteBoneIndices[pair.local]))).ToHashSet();
        bones=bones.Select(b=>b with {IsWeighted=weighted.Contains(b.Index)}).ToImmutableArray();
        var channels = mesh.MorphTargets.OrderBy(m => m.Index).Select((m, slot) => new CustomModelMorphChannel { Index = slot,
            Name = m.Name, DescriptorHash = Dl1NameHash.Compute(m.Name), BlendShapeChannelObjectId = m.Index + 1L, ShapeObjectId = m.Index + 1L,
            GeometryObjectIds = m.EntityIndexes.Select(e => e + 1L).DefaultIfEmpty(1).ToImmutableArray() }).ToImmutableArray();
        var reviews = Enum.GetValues<CharacterSubsystem>().Select(subsystem =>
        {
            if (options.VerifiedNotApplicable.Contains(subsystem)) return new CharacterSubsystemReview(subsystem, CharacterDependencyStatus.NotApplicable, "Explicitly reviewed as not applicable.");
            if (subsystem is CharacterSubsystem.Geometry or CharacterSubsystem.Rig or CharacterSubsystem.Skinning or CharacterSubsystem.Lods or CharacterSubsystem.Helpers)
                return new(subsystem, CharacterDependencyStatus.Decoded, "Original decoded data retained alongside immutable resource bytes.");
            if (subsystem == CharacterSubsystem.Morphs) return new(subsystem, mesh.MorphTargets.All(m => m.PayloadStatus == Dl1MorphPayloadStatus.VertexDeltasDecoded)
                ? CharacterDependencyStatus.Decoded : CharacterDependencyStatus.Unsupported, "Exact source channels and per-LOD bindings retained; unresolved payloads block complete export.");
            if(subsystem==CharacterSubsystem.Variants && mesh.SkinDefinitions.Count>0)
                return new(subsystem,CharacterDependencyStatus.Unsupported,"Original variant names, raw flags and visibility/material overrides are retained. Source-writer translation must be verified before complete export.");
            if (subsystem == CharacterSubsystem.FacialDefinitions &&
                resources.Any(r => r.Subsystem == subsystem && r.EntryPath is not null) &&
                !resources.Any(r => r.Subsystem == subsystem && r.EntryPath is not null &&
                    Path.GetExtension(r.LogicalName).Equals(".fed", StringComparison.OrdinalIgnoreCase)))
                return new(subsystem, CharacterDependencyStatus.Ambiguous,
                    "Global mimic source is retained; the selected actor's mimic set and model-associated FED require review.");
            bool found = resources.Any(r => r.Subsystem == subsystem && r.EntryPath is not null);
            return new(subsystem, found ? CharacterDependencyStatus.Preserved : CharacterDependencyStatus.Missing,
                found ? "Original dependencies retained; native semantics require separate review." : "Select and resolve the original companion root; absence has not been guessed as not applicable.");
        }).ToImmutableArray();
        var inventory = new CharacterResourceInventory { RootResourceId = root.Id.LogicalId.StableKey, Resources = resources.ToImmutable(),
            MorphBindings = bindings.Select(b=>b with {TargetChannelSlot=channels.Single(c=>c.Name==b.Name).Index}).ToImmutableArray(), VariantNames = mesh.VariantNames.ToImmutableArray(), Subsystems = reviews,
            OriginalAppliedSkinName=mesh.AppliedSkinName,
            OriginalMaterialSlotCount=mesh.OriginalMaterialDatabase.DeclaredSlotCount,
            OriginalMaterials=mesh.OriginalMaterialDatabase.Entries.Select(m=>new CharacterOriginalMaterial(m.Index,m.DatabaseName,m.RawLoadValue)).ToImmutableArray(),
            OriginalEntities=mesh.Hierarchy.Entities.Select(e=>new CharacterOriginalEntity(e.Index,e.Name)).ToImmutableArray(),
            SkinVariants=mesh.SkinDefinitions.Select(v=>new CharacterSkinVariant(v.Name,v.RawFeatures,
                v.MaterialOverrides.Select(m=>new CharacterSkinMaterialOverride(m.TargetMaterialSlotIndex,m.ReplacementMaterialDatabaseEntryIndex)).ToImmutableArray(),
                v.EntityOverrides.Select(e=>new CharacterSkinEntityOverride(e.EntityIndex,e.RawValue)).ToImmutableArray(),v.SurfaceOverrideCount,v.RandomizedChildCount)
                {TagBytes=v.TagBytes.ToImmutableArray(),ColorBytes=v.ColorBytes.ToImmutableArray(),MorphsPreset=v.MorphsPreset,Character0=v.Character0,Character1=v.Character1,
                    SurfaceOverrides=v.SurfaceOverrides.Select(s=>new CharacterSkinSurfaceOverride(s.OriginalSurfaceId,s.ReplacementSurfaceId,s.Flags)).ToImmutableArray()}).ToImmutableArray(),
            OriginalBuffers=mesh.Buffers.Select(b=>new CharacterOriginalBuffer(b.ItemIndex,b.ResourceItemSlot,b.StorageGroupId,b.LogicalLength,b.Role.ToString())).ToImmutableArray(),
            DecodedSha256 = new string('0', 64), DecodedByteLength = 1 };
        if(!inventory.SkinVariants.IsEmpty)
        {
            try
            {
                _ = Dl1SkinDefinitionCodec.GenerateCanonical(Dl1CharacterSkinAuthoring.FromInventory(inventory));
                inventory=inventory with {Subsystems=inventory.Subsystems.Select(s=>s.Subsystem==CharacterSubsystem.Variants
                    ? s with {Status=CharacterDependencyStatus.Decoded,Detail="Original named skins and supported material/visibility source mappings retained. Compiled readback and Player behavior remain required."}:s).ToImmutableArray()};
            }
            catch(InvalidDataException) { /* Original opaque records remain preserved and export-blocking. */ }
        }
        var doc = new CustomModelDocument { ModelId = StableId(sourceHash, "character"), Name = mesh.ResourceName, RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new() { Kind = CustomModelSourceKind.StockCharacter, OriginalFileName = mesh.ResourceName + ".skn", ContentSha256 = sourceHash,
                EmbeddedEntryPath = "source/character.bin" }, Bones = bones, MorphChannels = channels, Materials = materials, CharacterResources = inventory,
            RigSignature = CustomModelContractSignatures.ComputeRig(bones), MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(channels, surfaces.ToImmutable()),
            BuildSettings = new() { ResourceName = mesh.ResourceName, CharacterId = mesh.ResourceName, FlipTextureCoordinateV = false },
            Meshes = surfaces.Select(s => new CustomModelMeshPart { Name = s.MeshName, GeometryObjectId = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(s.SourceGeometry!.Id)), 0),
                ControlPointCount = s.SourceGeometry.ControlPoints.Length, ExpandedVertexCount = s.Vertices.Length, TriangleCount = s.Indices.Length / 3,
                PolygonCount = s.Indices.Length / 3, MaterialSlotCount = 1, MaximumRetainedInfluences = s.Vertices.Max(v => v.BoneIndices.Length), RequiredPaletteSize = s.PaletteBoneIndices.Length }).ToImmutableArray() };
        var lods = mesh.Surfaces.GroupBy(s => s.EntityIndex).Select(g => new CharacterLodNode(g.First().Name, g.Key,
            g.GroupBy(s => s.LodIndex).OrderBy(l => l.Key).Select(l => new CharacterLodLevel(l.Key,
                surfaces.Where(s => s.SourceGeometry!.Id == $"entity:{g.Key}/lod:{l.Key}").Select(s => s.Id).ToImmutableArray())).ToImmutableArray())).ToImmutableArray();
        var decoded = DecodedCharacterSnapshotCodec.Encode(new(doc, surfaces.ToImmutable(), []) { CharacterLods = lods });
        inventory = inventory with { DecodedSha256 = Convert.ToHexStringLower(SHA256.HashData(decoded.AsSpan())), DecodedByteLength = decoded.Length };
        var package = new CustomModelPackage(doc with { CharacterResources = inventory }, rootBytes, previews.ToImmutable())
            { DecodedCharacterPayload = decoded, CompanionPayloads = payloads.ToImmutable() };
        return FbxModelAuthoringImporter.ImportPackage(package, cancellationToken);

        async Task ResolveNativeMaterialAsync(string name,string parent)
        {
            if(options.MaterialProviderResourceId is not { } providerId)
            {Missing(name,CharacterSubsystem.Materials,parent,"Select the original material provider.");return;}
            string materialId="material:"+name.ToLowerInvariant();
            int previous=Array.FindIndex(resources.ToArray(),resource=>resource.Id==materialId);
            if(previous>=0)
            {
                if(!resources[previous].ReferencedBy.Contains(parent,StringComparer.Ordinal))
                    resources[previous]=resources[previous] with {ReferencedBy=resources[previous].ReferencedBy.Add(parent)};
                return;
            }
            var provider=resources.Single(resource=>resource.Id==providerId);
            var resolution=await Dl1CharacterMaterialDependencyResolver.ResolveAsync(payloads[provider.EntryPath!],[name],catalog,cancellationToken).ConfigureAwait(false);
            if(resolution.ProviderSha256!=provider.ContentSha256 || resolution.ProviderByteLength!=provider.ByteLength)
                throw new InvalidDataException("The selected material provider identity changed.");
            var material=resolution.Materials.Single();
            if(material.Material is not { } receipt)
            {
                Missing(name,CharacterSubsystem.Materials,parent,string.Join("; ",resolution.Findings.Select(finding=>finding.Detail)));
                if(material.Status!=Dl1CharacterMaterialDependencyStatus.MissingMaterial)
                {
                    int missing=Array.FindIndex(resources.ToArray(),resource=>resource.Id=="unresolved:"+CharacterSubsystem.Materials+":"+name);
                    resources[missing]=resources[missing] with {Status=CharacterDependencyStatus.Unsupported};
                }
                return;
            }
            if(resources.Count>=options.MaximumResources) throw new InvalidDataException("Character resources exceed the import limit.");
            total=checked(total+receipt.PayloadBytes.Length);
            if(total>options.MaximumTotalBytes) throw new InvalidDataException("Character dependency payloads exceed the import limit.");
            var textureReferences=ImmutableArray.CreateBuilder<CharacterMaterialTextureReference>();
            foreach(var texture in material.Textures)
            {
                string? textureId=null;
                if(texture.SelectedTextureId is { } selectedId)
                {
                    var selected=catalog.GetCandidates(selectedId.LogicalId).SingleOrDefault(asset=>asset.Id==selectedId)
                        ?? throw new InvalidDataException("The selected material texture identity disappeared.");
                    textureId=selected.Id.LogicalId.StableKey;
                    queue.Enqueue((selected,CharacterSubsystem.Textures,materialId));
                }
                else
                {
                    string token=$"material-texture:{name}:0x{texture.TextureNameHash:X8}";
                    Missing(token,CharacterSubsystem.Textures,materialId,texture.TextureName is { } exactName
                        ? "Texture resource is missing: " + exactName : "The material texture hash has no unique catalog identity.");
                    if(texture.Status==Dl1CharacterMaterialDependencyStatus.TextureHashCollision)
                    {
                        int missing=Array.FindIndex(resources.ToArray(),resource=>resource.Id=="unresolved:"+CharacterSubsystem.Textures+":"+token);
                        resources[missing]=resources[missing] with {Status=CharacterDependencyStatus.Ambiguous};
                    }
                }
                textureReferences.Add(new(texture.TextureIndex,texture.SamplerState,texture.TextureNameHash,texture.LoadFlags,textureId) { NameSource=texture.NameSource });
            }
            string entry=$"character/resources/material-{resources.Count:D5}-{receipt.PayloadSha256}.bin";
            payloads.Add(entry,receipt.PayloadBytes);
            resources.Add(new(){Id=materialId,LogicalName=name,ProviderIdentity=provider.ProviderIdentity,
                SourceFingerprint=provider.SourceFingerprint,ContentSha256=receipt.PayloadSha256,EntryPath=entry,ByteLength=receipt.LogicalByteLength,
                Subsystem=CharacterSubsystem.Materials,Status=CharacterDependencyStatus.Decoded,ReferencedBy=[parent],Detail="Original material payload and texture references.",
                Material=new(){ProviderResourceId=providerId,ProviderSha256=receipt.ProviderSha256,MaterialName=name,NameHash=receipt.NameHash,
                    TableIndex=receipt.TableIndex,PayloadOffset=receipt.Offset,StoredByteLength=receipt.StoredSize,TechniqueCount=receipt.TechniqueCount,Textures=textureReferences.ToImmutable()}});
        }

        async Task<ImmutableArray<string>> GatherGenericMeshesAsync(
            Dl1BodyElementsDocument body, RetailAssetRecord bel, string bodyId)
        {
            if (!body.IsValid) throw new FormatException("Generic relic BEL source is malformed.");
            var required = body.Elements.Select(element => element.ElementToken).Distinct(StringComparer.Ordinal).ToArray();
            var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
            var visitedIncludes = new HashSet<string>(StringComparer.Ordinal);
            var includes = new Queue<(RetailAssetRecord Asset, int Depth)>();
            long readBytes = 0;
            AddIncludes(body.Syntax, bel, 0);
            while (includes.TryDequeue(out var next))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visitedIncludes.Add(next.Asset.Id.StableKey)) continue;
                if (next.Depth > 16 || visitedIncludes.Count > options.MaximumResources)
                    throw new FormatException("Generic relic symbol include closure exceeds its bounds.");
                await using var stream = await catalog.OpenReadAsync(next.Asset, cancellationToken).ConfigureAwait(false);
                using var memory = new MemoryStream();
                byte[] buffer = new byte[16384];
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (memory.Length + count > NativeCharacterScriptCodec.MaximumCharacters ||
                        readBytes + count > 64L * 1024 * 1024 || total + readBytes + count > options.MaximumTotalBytes)
                        throw new FormatException("Generic relic symbol sources exceed their read bounds.");
                    memory.Write(buffer, 0, count);
                    readBytes += count;
                }
                byte[] payload = memory.ToArray();
                string hash = Convert.ToHexStringLower(SHA256.HashData(payload));
                if (next.Asset.Id.ContentFingerprint is { } expected && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Generic relic symbol source changed from its exact catalog identity.");
                var map = Dl1BodyElementSymbolMap.Read(new UTF8Encoding(false, true).GetString(payload), required);
                if (map.Diagnostics.Any(diagnostic => diagnostic.Code != "body_symbol_missing"))
                    throw new FormatException("Generic relic symbol source declarations are invalid or ambiguous.");
                foreach (var symbol in map.Symbols)
                    if (!symbols.TryAdd(symbol.Key, symbol.Value))
                        throw new FormatException("Generic relic symbols have multiple effective declarations.");
                Resolve(next.Asset.Id.LogicalId, CharacterSubsystem.Damage, bodyId);
                AddIncludes(map.Syntax, next.Asset, next.Depth);
            }
            var selected = Dl1GenericRelicPreload.Select(body, symbols);
            if (!selected.IsComplete)
                throw new FormatException("Generic relic body symbols are missing or ambiguous.");
            return selected.Selections.Where(selection => selection.Selection == Dl1RelicPreloadSelection.Generic)
                .Select(selection => selection.Name!).Distinct(StringComparer.Ordinal).ToImmutableArray();

            void AddIncludes(NativeCharacterScriptDocument syntax, RetailAssetRecord declaring, int depth)
            {
                foreach (var call in syntax.Calls.Where(call => call.Name == "!include"))
                {
                    string? request = call.QuotedArguments.FirstOrDefault(argument => argument.ArgumentIndex == 0)?.Value;
                    if (request is null) throw new FormatException("Generic relic include is not an exact quoted declaration.");
                    string name;
                    try { name = CharacterVirtualReferencePath.Resolve(request, declaring.Id.Name).CanonicalName; }
                    catch (Exception error) when (error is ArgumentException or InvalidDataException)
                    { throw new FormatException("Generic relic include path is unsafe.", error); }
                    var logical = RetailAssetLogicalId.VirtualFile(name);
                    var selected = catalog.Resolve(logical);
                    if (selected is null || catalog.GetCandidates(logical).Count == 0)
                        throw new FormatException("The exact generic relic include source is missing.");
                    includes.Enqueue((selected, depth + 1));
                }
            }
        }

        void Missing(string name, CharacterSubsystem subsystem, string parent, string detail)
        {
            string id = "unresolved:" + subsystem + ":" + name;
            if (resources.Any(r => r.Id == id)) return;
            resources.Add(new() { Id = id, LogicalName = name, Subsystem = subsystem, Status = CharacterDependencyStatus.Missing, Detail = detail, ReferencedBy = [parent] });
        }
        void Resolve(RetailAssetLogicalId id, CharacterSubsystem subsystem, string parent)
        {
            var candidates = catalog.GetCandidates(id);
            if (candidates.Count == 0) { Missing(id.Name, subsystem, parent, "Referenced dependency is absent from the catalog."); return; }
            var winner = catalog.Resolve(id) ?? throw new InvalidDataException("Catalog winner disappeared.");
            queue.Enqueue((winner, subsystem, parent));
        }
        void ResolveName(string name, CharacterSubsystem subsystem, string parent)
        {
            var declaring = resources.FirstOrDefault(resource => resource.Id == parent);
            bool packedFx=declaring?.PackedEffect is not null;
            CharacterVirtualReferencePathResult resolved;
            try { resolved = CharacterVirtualReferencePath.Resolve(name, packedFx || declaring is null || parent == root.Id.LogicalId.StableKey ? null : declaring.LogicalName); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            {
                Missing(name, subsystem, parent, "Reference path is unsafe: " + error.Message);
                int unsafeIndex=Array.FindIndex(resources.ToArray(),resource=>resource.Id=="unresolved:"+subsystem+":"+name);
                resources[unsafeIndex]=resources[unsafeIndex] with {Status=CharacterDependencyStatus.Unsupported};
                return;
            }
            string normalized = resolved.CanonicalName;
            var exact = catalog.Assets.Where(asset => asset.Id.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length == 0 && !packedFx && !resolved.RequiresExactLookup && resolved.IsRelative)
                exact=catalog.Assets.Where(asset=>asset.Id.Name.Equals(name.Replace('\\','/'),StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length == 0 && !packedFx && !resolved.RequiresExactLookup)
                exact = catalog.Assets.Where(asset => Path.GetFileName(asset.Id.Name).Equals(Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase)).ToArray();
            if(exact.Length==0 && !resolved.RequiresExactLookup && Path.GetExtension(normalized).ToLowerInvariant() is ".msh" or ".skn" or ".msh_obj")
                exact=catalog.Assets.Where(a=>a.Id.Namespace==RetailAssetNamespace.RpackResource && a.Id.ResourceType==ReAnimated.Codecs.Rp6l.Rp6lResourceTypes.Mesh &&
                    a.Id.Name.Equals(Path.GetFileNameWithoutExtension(normalized),StringComparison.OrdinalIgnoreCase)).ToArray();
            var logicalMatches = exact.Select(asset=>asset.Id.LogicalId).Distinct().ToArray();
            if (logicalMatches.Length == 1)
            {
                var selected = catalog.Resolve(logicalMatches[0]) ?? throw new InvalidDataException("Catalog dependency winner disappeared.");
                if (resolved.RequiresExactLookup && resolved.IsRelative && declaring is not null && selected.Id.ProviderId != declaring.ProviderIdentity)
                {
                    Missing(normalized, subsystem, parent, "The exact source-relative path crosses the declaring provider; select and review its exact identity.");
                    int crossProviderIndex=Array.FindIndex(resources.ToArray(),resource=>resource.Id=="unresolved:"+subsystem+":"+normalized);
                    resources[crossProviderIndex]=resources[crossProviderIndex] with {Status=CharacterDependencyStatus.Ambiguous};
                }
                else queue.Enqueue((selected, subsystem, parent));
            }
            else if (exact.Length == 0) Missing(normalized, subsystem, parent, "Referenced dependency was not found.");
            else { Missing(normalized, subsystem, parent, "Reference matches multiple logical resources; select an exact identity.");
                int ambiguous=Array.FindIndex(resources.ToArray(),r=>r.Id=="unresolved:"+subsystem+":"+normalized);
                resources[ambiguous] = resources[ambiguous] with { Status = CharacterDependencyStatus.Ambiguous }; }
        }
    }

    private static ImmutableArray<byte> CreateDdsPreview(ReAnimated.DL1.Assets.Materials.Dl1TexturePreviewData texture)
    {
        int blockBytes=texture.Format==ReAnimated.DL1.Assets.Materials.Dl1PreviewTextureFormat.Bc1Unorm?8:16;
        int length=checked(Math.Max(1,(texture.Width+3)/4)*Math.Max(1,(texture.Height+3)/4)*blockBytes);
        if(texture.Width<=0 || texture.Height<=0 || texture.BaseMipBytes.Length!=length) throw new InvalidDataException("Stock preview BC payload is invalid.");
        using var output=new MemoryStream(); using var writer=new BinaryWriter(output,Encoding.ASCII,true);
        writer.Write("DDS "u8);writer.Write(124);writer.Write(0x000A1007);writer.Write(texture.Height);writer.Write(texture.Width);
        writer.Write(length);writer.Write(0);writer.Write(1);for(int i=0;i<11;i++)writer.Write(0);
        writer.Write(32);writer.Write(4);writer.Write(texture.Format switch
            {ReAnimated.DL1.Assets.Materials.Dl1PreviewTextureFormat.Bc1Unorm=>0x31545844,
             ReAnimated.DL1.Assets.Materials.Dl1PreviewTextureFormat.Bc2Unorm=>0x33545844,
             ReAnimated.DL1.Assets.Materials.Dl1PreviewTextureFormat.Bc3Unorm=>0x35545844,_=>throw new InvalidDataException("Unsupported stock preview format.")});
        for(int i=0;i<5;i++)writer.Write(0);writer.Write(0x1000);for(int i=0;i<4;i++)writer.Write(0);
        writer.Write(texture.BaseMipBytes.Span);writer.Flush();return ImmutableArray.Create(output.ToArray());
    }

    private static Vector3D Vector(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    private static TransformMatrix Matrix(CompactMatrix3x4 m) => new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,0,0,0,1);
    private static Guid StableId(string source, string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(source + ":" + name)).AsSpan(0,16));
    private static bool IsTextScript(string name) => Path.GetExtension(name).ToLowerInvariant() is ".scr" or ".phx" or ".bel" or ".def" or ".mpcloth" or ".ascr" or ".bscr" or ".fx";
    private static CharacterSubsystem Classify(string name) =>
        Path.GetFileName(name.Replace('\\', '/')).Equals("FaceMimic.scr", StringComparison.OrdinalIgnoreCase)
            ? CharacterSubsystem.FacialDefinitions : Path.GetExtension(name).ToLowerInvariant() switch
    { ".bel" => CharacterSubsystem.Damage, ".fed" => CharacterSubsystem.FacialDefinitions, ".chr" => CharacterSubsystem.Variants,
      ".pre" => CharacterSubsystem.Helpers,
      ".phx" => CharacterSubsystem.Ragdoll, ".mpcloth" => CharacterSubsystem.Cloth, ".msh" or ".skn" or ".msh_obj" => CharacterSubsystem.DetachedParts,
      ".fx" => CharacterSubsystem.Damage, _ => CharacterSubsystem.Damage };
    private static bool IsReferencedResource(string name)=>Path.GetExtension(name).ToLowerInvariant() is
        ".scr" or ".phx" or ".bel" or ".def" or ".mpcloth" or ".fed" or ".chr" or ".fx" or ".msh" or ".msh_obj" or ".skn" or ".ascr" or ".bscr";
}



