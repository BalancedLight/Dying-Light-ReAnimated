using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public static class CharacterGeometryAuthoring
{
    public static FbxModelAuthoringImportResult AddAttachment(FbxModelAuthoringImportResult model,
        FbxModelAuthoringImportResult attachment, string? rigidBoneName = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(attachment);
        var doc = model.Package.Document;
        doc.Validate();
        attachment.Package.Document.Validate();
        if (model.Surfaces.IsDefault || model.SourceCharacterLods.IsDefault ||
            attachment.Surfaces.IsDefaultOrEmpty || attachment.SourceCharacterLods.IsDefault)
            throw new InvalidDataException("The target and attachment surface inventories must be initialized.");
        var incomingMaterialIds = attachment.Package.Document.Materials.Select(material => material.Id).ToHashSet();
        if (incomingMaterialIds.Count != attachment.Package.Document.Materials.Length)
            throw new InvalidDataException("Attachment material identities are duplicated.");
        var surfaceIds = model.Surfaces.Select(surface => surface.Id).ToHashSet(StringComparer.Ordinal);
        var geometryIds = model.Surfaces.Where(surface => surface.SourceGeometry is not null)
            .Select(surface => surface.SourceGeometry!.Id).ToHashSet(StringComparer.Ordinal);
        var incomingSurfaceIds = new HashSet<string>(StringComparer.Ordinal);
        var incomingGeometry = new Dictionary<string, GeometrySourceComponent>(StringComparer.Ordinal);
        var geometryOrigins = new Dictionary<string, bool>(StringComparer.Ordinal);
        string namespacePrefix = "attachment:" + attachment.Package.Document.Source.ContentSha256.ToLowerInvariant() + ":";
        foreach (var surface in attachment.Surfaces)
        {
            if (string.IsNullOrWhiteSpace(surface.Id) || string.IsNullOrWhiteSpace(surface.MeshName) ||
                !incomingSurfaceIds.Add(surface.Id) || !surfaceIds.Add(namespacePrefix + surface.Id))
                throw new InvalidDataException("Attachment surface identities are missing, duplicated, or already present.");
            if (!incomingMaterialIds.Contains(surface.MaterialId))
                throw new InvalidDataException("An attachment surface references a material outside its material inventory.");
            if (surface.Vertices.IsDefaultOrEmpty || surface.Indices.IsDefaultOrEmpty ||
                surface.Indices.Length % 3 != 0 || surface.Indices.Any(index => index >= surface.Vertices.Length))
                throw new InvalidDataException("An attachment surface has invalid triangle geometry.");
            if (surface.PaletteBoneIndices.IsDefault || surface.InverseBindMatrices.IsDefault ||
                surface.PaletteBoneIndices.Length != surface.InverseBindMatrices.Length ||
                surface.Vertices.Any(vertex => !vertex.Position.IsFinite || !vertex.Normal.IsFinite ||
                    vertex.BoneIndices.IsDefault || vertex.BoneWeights.IsDefault ||
                    vertex.BoneIndices.Length != vertex.BoneWeights.Length ||
                    vertex.BoneWeights.Any(weight => !double.IsFinite(weight) || weight < 0)))
                throw new InvalidDataException("An attachment surface has invalid positions or binding data.");
            string componentId = surface.SourceGeometry?.Id ?? surface.Id;
            bool explicitGeometry = surface.SourceGeometry is not null;
            if (geometryOrigins.TryGetValue(componentId, out bool priorOrigin) && priorOrigin != explicitGeometry)
                throw new InvalidDataException("An attachment geometry identity mixes source and generated components.");
            geometryOrigins[componentId] = explicitGeometry;
            if (surface.SourceGeometry is { } geometry)
            {
                if (string.IsNullOrWhiteSpace(geometry.Id) || geometry.ControlPoints.IsDefault ||
                    geometry.ControlPoints.Any(point => !point.IsFinite) ||
                    geometryIds.Contains(namespacePrefix + geometry.Id))
                    throw new InvalidDataException("An attachment geometry identity is invalid or already present.");
                if (incomingGeometry.TryGetValue(geometry.Id, out var previous) &&
                    !previous.ControlPoints.SequenceEqual(geometry.ControlPoints))
                    throw new InvalidDataException("Attachment surfaces disagree on their shared source geometry.");
                incomingGeometry[geometry.Id] = geometry;
            }
            else if (geometryIds.Contains(namespacePrefix + surface.Id))
                throw new InvalidDataException("An attachment geometry identity is already present.");
        }

        var targetBones = doc.CreateEffectiveBones();
        if (rigidBoneName is null && targetBones.Length > 0 && attachment.Surfaces.Any(surface =>
            !surface.IsSkinned || surface.Vertices.Any(vertex => !vertex.BoneWeights.Any(weight => weight > 0))))
            throw new InvalidDataException("Choose an exact bone for a static or unweighted attachment.");
        var sourceBones = attachment.Package.Document.CreateEffectiveBones();
        int rigid = rigidBoneName is null ? -1 : Array.FindIndex(targetBones.ToArray(), b => b.Name == rigidBoneName);
        if (rigidBoneName is not null && rigid < 0) throw new InvalidDataException("Attachment bone is missing.");
        var rawAttachmentLayout = FbxModelLodLayout.Create(attachment);
        var layoutBuilder = ImmutableArray.CreateBuilder<FbxModelLodNodeLayout>();
        foreach (var group in rawAttachmentLayout.GroupBy(node => node.Name, StringComparer.OrdinalIgnoreCase))
        {
            var nodes = group.ToArray();
            if (nodes.Length == 1) { layoutBuilder.Add(nodes[0]); continue; }
            var draws = nodes.SelectMany(node => node.Levels.SelectMany(level => level.SurfaceIndexes)).ToArray();
            if (nodes.Any(node => node.Name != nodes[0].Name || node.Levels.Length != 1 || node.Levels[0].LodIndex != 0) ||
                draws.Select(index => attachment.Surfaces[index].SourceGeometry?.Id ?? attachment.Surfaces[index].Id)
                    .Distinct(StringComparer.Ordinal).Count() != 1)
                throw new InvalidDataException("Distinct attachment LOD entities cannot share one name.");
            layoutBuilder.Add(new(nodes[0].Name, [new(0, draws.ToImmutableArray())]));
        }
        var attachmentLayout = layoutBuilder.ToImmutable();
        var entityNames = targetBones.Select(bone => bone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in FbxModelLodLayout.Create(model))
            entityNames.Add(node.Name);
        foreach (var node in attachmentLayout)
            if (!entityNames.Add(node.Name))
                throw new InvalidDataException("An attachment LOD entity name overlaps the target hierarchy or another LOD.");
        var existingNegativeIndexes = model.SourceCharacterLods.Where(node => node.SourceEntityIndex < 0)
            .Select(node => node.SourceEntityIndex).ToArray();
        if (existingNegativeIndexes.Distinct().Count() != existingNegativeIndexes.Length)
            throw new InvalidDataException("Target attachment entity indexes are duplicated.");
        long nextEntityIndex = (long)existingNegativeIndexes.DefaultIfEmpty(0).Min() - 1;
        if (nextEntityIndex - attachmentLayout.Length + 1 < int.MinValue)
            throw new InvalidDataException("The attachment entity index budget is exhausted.");
        var names = model.Surfaces.Select(s => s.MeshName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var attachmentOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var materialIds = doc.Materials.Select(m => m.Id).ToHashSet();
        if (attachment.Package.Document.Materials.Any(m => materialIds.Contains(m.Id)))
            throw new InvalidDataException("Attachment material identities conflict; import a distinct attachment source.");
        var globals = Globals(targetBones);
        var sourceGlobals = Globals(sourceBones);
        string prefix = namespacePrefix;
        var extras = attachment.Surfaces.Select(s =>
        {
            if (names.Contains(s.MeshName)) throw new InvalidDataException($"Attachment mesh '{s.MeshName}' conflicts with an existing mesh.");
            string owner = s.SourceGeometry?.Id ?? s.Id;
            if (attachmentOwners.TryGetValue(s.MeshName, out var previousOwner) && previousOwner != owner)
                throw new InvalidDataException("Distinct attachment components cannot share the same mesh entity name.");
            attachmentOwners[s.MeshName] = owner;
            int Map(int old)
            {
                if ((uint)old >= (uint)sourceBones.Length) throw new InvalidDataException("Attachment palette is invalid.");
                int index = Array.FindIndex(targetBones.ToArray(), b => b.Name == sourceBones[old].Name);
                if (index < 0 || globals[index] != sourceGlobals[old])
                    throw new InvalidDataException("Skinned attachment bind frames differ; review and transfer its binding before attachment.");
                return index;
            }
            var vertices = s.Vertices.Select(v => rigid >= 0 ? v with { BoneIndices = [0], BoneWeights = [1] } : v).ToImmutableArray();
            var palette = rigid >= 0 ? ImmutableArray.Create(rigid) : s.PaletteBoneIndices.Select(Map).ToImmutableArray();
            return s with { Id = prefix + s.Id, SourceGeometry = s.SourceGeometry is { } g ? g with { Id = prefix + g.Id } :
                new GeometrySourceComponent(prefix + s.Id, s.Vertices.Select(v => v.Position).ToImmutableArray()),
                Vertices = vertices, PaletteBoneIndices = palette, InverseBindMatrices = palette.Select(i => globals[i].InvertedAffine()).ToImmutableArray(),
                IsSkinned = rigid >= 0 || s.IsSkinned };
        }).ToImmutableArray();
        var texturePayloads = model.Package.TexturePayloads;
        foreach (var (path, bytes) in attachment.Package.TexturePayloads)
        {
            if (texturePayloads.TryGetValue(path, out var existing) && !existing.SequenceEqual(bytes))
                throw new InvalidDataException("Attachment texture entry conflicts with original character data.");
            texturePayloads = texturePayloads.SetItem(path, bytes);
        }
        var combinedSurfaces = model.Surfaces.AddRange(extras);
        var channels = doc.MorphChannels.ToBuilder();
        foreach (var added in attachment.Package.Document.MorphChannels)
        {
            var existing = channels.FirstOrDefault(c => c.Name == added.Name);
            if (existing is not null)
            {
                if (existing.DescriptorHash != added.DescriptorHash) throw new InvalidDataException("Attachment expression name conflicts with an original descriptor.");
                continue;
            }
            if (channels.Any(c => c.DescriptorHash == added.DescriptorHash)) throw new InvalidDataException("Attachment descriptor conflicts with an original expression name.");
            channels.Add(added with { Index = channels.Count });
        }
        var attachmentLods = attachmentLayout.Select((node,index) => new CharacterLodNode(node.Name,checked((int)(nextEntityIndex-index)),
            node.Levels.Select(level => new CharacterLodLevel(level.LodIndex,level.SurfaceIndexes.Select(i => prefix+attachment.Surfaces[i].Id).ToImmutableArray())).ToImmutableArray())).ToImmutableArray();
        var revised = model with { Surfaces = combinedSurfaces, SourceCharacterLods = model.SourceCharacterLods.AddRange(attachmentLods), Package = model.Package with
            { TexturePayloads = texturePayloads, Document = doc with { Materials = doc.Materials.AddRange(attachment.Package.Document.Materials),
                Meshes = doc.Meshes.AddRange(attachment.Package.Document.Meshes), MorphChannels = channels.ToImmutable(),
                MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(channels.ToImmutable(),combinedSurfaces), LastBuildReceipt = null } } };
        return ModelGeometryRevisionCodec.Capture(revised);
    }

    public static FbxModelAuthoringImportResult SetExpression(FbxModelAuthoringImportResult model, string surfaceId,
        string exactName, uint descriptor, ImmutableArray<Vector3D> deltas, MorphTransferConflict conflict, bool reviewed)
    {
        if (!reviewed) throw new InvalidOperationException("Review the expression before applying it.");
        int surfaceIndex = Array.FindIndex(model.Surfaces.ToArray(), s => s.Id == surfaceId);
        if (surfaceIndex < 0) throw new InvalidDataException("Expression surface is missing.");
        var surface = model.Surfaces[surfaceIndex];
        if (deltas.Length != surface.Vertices.Length || deltas.Any(d => !d.IsFinite)) throw new InvalidDataException("Expression topology or deltas are invalid.");
        var doc = model.Package.Document;
        var channel = doc.MorphChannels.SingleOrDefault(c => c.Name == exactName);
        if (doc.MorphChannels.Any(c => c.DescriptorHash == descriptor && c.Name != exactName)) throw new InvalidDataException("Expression descriptor conflicts with another channel.");
        int existingIndex = Array.FindIndex(surface.MorphTargets.ToArray(), m => m.Name == exactName);
        if (existingIndex >= 0)
        {
            if (conflict == MorphTransferConflict.KeepExisting) return model;
            if (conflict != MorphTransferConflict.ReplaceExisting) throw new InvalidOperationException("Choose keep or replace for the existing expression.");
        }
        if (deltas.All(d => d.LengthSquared == 0)) throw new InvalidDataException("A zero-delta placeholder cannot be accepted as a recreated expression.");
        if (channel is not null && channel.DescriptorHash != descriptor) throw new InvalidDataException("Original expression descriptor must remain unchanged.");
        long channelId = channel?.BlendShapeChannelObjectId ?? doc.MorphChannels.Select(c => c.BlendShapeChannelObjectId).DefaultIfEmpty(0).Max() + 1;
        long shapeId = channel?.ShapeObjectId ?? channelId;
        var morph = new FbxModelMorphTarget(exactName, descriptor, channelId, shapeId, deltas);
        var targets = existingIndex >= 0 ? surface.MorphTargets.SetItem(existingIndex, morph) : surface.MorphTargets.Add(morph);
        var surfaces = model.Surfaces.SetItem(surfaceIndex, surface with { MorphTargets = targets });
        var channels = channel is null ? doc.MorphChannels.Add(new() { Index = doc.MorphChannels.Length, Name = exactName,
            DescriptorHash = descriptor, BlendShapeChannelObjectId = channelId, ShapeObjectId = shapeId, GeometryObjectIds = [channelId] }) : doc.MorphChannels;
        var inventory=doc.CharacterResources;
        if(inventory is not null)
        {
            var mappings=inventory.MorphBindings.Select(b=>b.Name==exactName && b.DescriptorHash==descriptor?b with {TargetChannelSlot=channels.Single(c=>c.Name==exactName).Index}:b).ToImmutableArray();
            inventory=inventory with {MorphBindings=mappings,Resources=inventory.Resources.Where(r=>!(r.Subsystem==CharacterSubsystem.Morphs && r.Id.StartsWith("unresolved:reference-morph:",StringComparison.Ordinal) && r.LogicalName==exactName && mappings.Where(b=>b.Name==exactName).All(b=>b.TargetChannelSlot>=0))).ToImmutableArray(),Subsystems=inventory.Subsystems.Select(s=>s.Subsystem==CharacterSubsystem.Morphs && mappings.All(b=>b.TargetChannelSlot>=0)
                ? s with {Status=CharacterDependencyStatus.Decoded,Detail="Original vocabulary has explicit authored target channel slots. Expression and FED/mimic validation remain required."}:s).ToImmutableArray()};
        }
        var updated = doc with { CharacterResources=inventory, MorphChannels = channels, MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(channels, surfaces), LastBuildReceipt = null };
        return ModelGeometryRevisionCodec.Capture(model with { Surfaces = surfaces, Package = model.Package with { Document = updated } });
    }
    public static MorphReferenceProfile CreateMorphProfile(FbxModelAuthoringImportResult model, string surfaceId)
    {
        var s = model.Surfaces.Single(s => s.Id == surfaceId);
        return MorphDeformationTransfer.CreateProfile(model.Package.Document.Source.ContentSha256, model.Package.Document.RigSignature,
            model.Package.Document.MorphChannels.Select(c => new MorphReferenceChannel(c.Index, c.Name, c.DescriptorHash, c.BlendShapeChannelObjectId, c.ShapeObjectId)),
            [new MorphReferenceSurface(s.MeshName, s.Id, 0, s.Vertices.Select(v => v.Position).ToImmutableArray(), s.Indices.Select(i => checked((int)i)).ToImmutableArray())]);
    }
    private static TransformMatrix[] Globals(ImmutableArray<CustomModelBone> bones)
    {
        var globals = new TransformMatrix[bones.Length];
        foreach (var b in bones) globals[b.Index] = b.ParentIndex < 0 ? b.ExactLocalBindMatrix : globals[b.ParentIndex] * b.ExactLocalBindMatrix;
        return globals;
    }
}
