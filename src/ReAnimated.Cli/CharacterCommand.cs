using System.Security.Cryptography;
using System.Globalization;
using ReAnimated.Core.Mathematics;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Cli;

internal static class CharacterCommand
{
    private const string Usage = """
        Usage:
          DLReAnimated character inspect <model.dlrmodel>
          DLReAnimated character shape-inputs <sphere|capsule|box> --spans <x,y,z> [--scale <factor>]
          DLReAnimated character body-hide <model.dlrmodel> --resource <body-id> --region <exact-token> --entity <exact-name> --reviewed --output <new-model.dlrmodel>
          DLReAnimated character assign-material <model.dlrmodel> --target-material <appended-guid> --use-material <retained-guid> --reviewed --output <new-model.dlrmodel>
          DLReAnimated character add-attachment <model.dlrmodel> --attachment <part.fbx> [--bone <exact-name>] --reviewed --output <new-model.dlrmodel>
          DLReAnimated character fit-bounds <model.dlrmodel> --bone <exact-name>
          DLReAnimated character set-bounds <model.dlrmodel> --bone <exact-name> --center <x,y,z> --size <x,y,z> --reviewed --output <new-model.dlrmodel>
          DLReAnimated character bounds-controls <model.dlrmodel> --bone <exact-name> --scale <factor> --output-dir <new-folder>
          DLReAnimated character export-effects <model.dlrmodel> --output <new.rpack> [--compression none|zlib]
          DLReAnimated character attach-source <model.dlrmodel> --archive <sources.zip> --member <exact-member> --virtual-name <resource-path> --subsystem <name> --expected-sha256 <hash> [--archive-sha256 <hash>] --reviewed --output <new-model.dlrmodel>
          DLReAnimated character export-neutral <model.dlrmodel> --surface <id> --output <face.obj>
          DLReAnimated character import-sculpt <target.dlrmodel> --reference <reference.dlrmodel> --target-surface <id> --reference-surface <id> --expression <name> --sculpt <face.obj|face.fbx> [--conflict reject|keep|replace] --reviewed --output <new-model.dlrmodel>
        """;

    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (args.Length == 1 && args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException(Usage);

        string command = args[0].ToLowerInvariant();
        if (command == "shape-inputs")
        {
            var values = ParseOptions(args[2..], ["spans", "scale"], false);
            Dl1RagdollShapeKind shape = args[1] switch
            {
                "sphere" => Dl1RagdollShapeKind.Sphere,
                "capsule" => Dl1RagdollShapeKind.Capsule,
                "box" => Dl1RagdollShapeKind.Box,
                _ => throw new ArgumentException("Choose sphere, capsule or box."),
            };
            if (!double.TryParse(values.GetValueOrDefault("scale", "1"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double scale))
                throw new ArgumentException("Scale must be a finite nonnegative number.");
            var result = Dl1RagdollShapeInputCalculator.Calculate(shape,
                ReadVector(Require(values, "spans")), scale);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                format = "dl-reanimated-ragdoll-shape-inputs-v1",
                shape = args[1], scale,
                paddedSpans = new[] { result.PaddedSpans.X, result.PaddedSpans.Y, result.PaddedSpans.Z },
                capsuleAxis = result.CapsuleAxis?.ToString(), result.Radius, result.CylinderLength,
            }, options));
            return 0;
        }
        string source = Path.GetFullPath(args[1]);
        switch (command)
        {
            case "inspect":
                ParseOptions(args[2..], [], false);
                await InspectAsync(source, options, token).ConfigureAwait(false);
                break;
            case "body-hide":
            {
                var values = ParseOptions(args[2..], ["resource", "region", "entity", "output"], true);
                if (!values.ContainsKey("reviewed"))
                    throw new ArgumentException("Review the body region and visibility target, then pass --reviewed.");
                string output = NewOutput(Require(values, "output"), ".dlrmodel", [source]);
                var model = await LoadModelAsync(source, token).ConfigureAwait(false);
                string resource = Require(values, "resource"), region = Require(values, "region"), entity = Require(values, "entity");
                _ = CharacterModelEntityInventory.RequireUnique(model, entity);
                var read = CharacterCompanionAuthoring.Read(model.Package, resource, CharacterCompanionFamily.BodyElements);
                var element = read.BodyElements!.Elements.SingleOrDefault(element => element.ElementToken == region)
                    ?? throw new ArgumentException("Select an exact body-region token from the source.");
                var result = CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(model.Package,
                    new(resource, read.Resource.ContentSha256!, element.CallIndex, element.ElementToken, element.HelperName,
                        entity, CharacterModelEntityInventory.UniqueNames(model), true));
                if (!result.Applied) throw new InvalidDataException(string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
                await SaveNewPackageAsync(result.Package, output, token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    format = "dl-reanimated-body-hide-addition-v1", resource, region, entity,
                    outputFile = Path.GetFileName(output),
                    outputSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(output, token).ConfigureAwait(false))),
                }, options));
                break;
            }
            case "assign-material":
            {
                var values = ParseOptions(args[2..], ["target-material", "use-material", "output"], true);
                if (!values.ContainsKey("reviewed"))
                    throw new ArgumentException("Review the retained material choice, then pass --reviewed.");
                if (!Guid.TryParse(Require(values, "target-material"), out Guid appended) ||
                    !Guid.TryParse(Require(values, "use-material"), out Guid retained))
                    throw new ArgumentException("Use exact material GUIDs from the inspection report.");
                string output = NewOutput(Require(values, "output"), ".dlrmodel", [source]);
                var model = await LoadModelAsync(source, token).ConfigureAwait(false);
                var revised = CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(model, appended, retained, reviewed: true);
                await SaveNewPackageAsync(revised.Package, output, token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    format = "dl-reanimated-accessory-material-assignment-v1",
                    removedMaterialId = appended, retainedMaterialId = retained,
                    outputFile = Path.GetFileName(output),
                    outputSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(output, token).ConfigureAwait(false))),
                }, options));
                break;
            }
            case "add-attachment":
            {
                var values = ParseOptions(args[2..], ["attachment", "bone", "output"], true);
                if (!values.ContainsKey("reviewed"))
                    throw new ArgumentException("Review the attachment and binding, then pass --reviewed.");
                string attachmentPath = Path.GetFullPath(Require(values, "attachment"));
                string output = NewOutput(Require(values, "output"), ".dlrmodel", [source, attachmentPath]);
                var model = await LoadModelAsync(source, token).ConfigureAwait(false);
                var attachment = await FbxModelAuthoringImporter.ImportFileAsync(attachmentPath,
                    new() { DecodeAnimationClips = false }, token).ConfigureAwait(false);
                var revised = CharacterGeometryAuthoring.AddAttachment(model, attachment,
                    values.GetValueOrDefault("bone"));
                await SaveNewPackageAsync(revised.Package, output, token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    format = "dl-reanimated-character-attachment-v1",
                    outputFile = Path.GetFileName(output),
                    outputSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(output, token).ConfigureAwait(false))),
                    sourceSha256 = model.Package.Document.Source.ContentSha256,
                    attachmentSha256 = attachment.Package.Document.Source.ContentSha256,
                    rigidBone = values.GetValueOrDefault("bone"),
                    originalSurfaceCount = model.Surfaces.Length,
                    addedSurfaces = revised.Surfaces.Skip(model.Surfaces.Length).Select(surface =>
                        new { surface.Id, surface.MeshName, surface.MaterialId }),
                }, options));
                break;
            }
            case "fit-bounds":
            {
                var values=ParseOptions(args[2..],["bone"],false);
                var model=await LoadModelAsync(source,token).ConfigureAwait(false);
                string bone=Require(values,"bone");
                var candidate=CharacterBoneBoundsAuthoring.Fit(model,bone);
                Console.WriteLine(JsonSerializer.Serialize(new{format="dl-reanimated-bone-bounds-candidate-v1",bone,
                    center=new[]{candidate.Center.X,candidate.Center.Y,candidate.Center.Z},
                    size=new[]{candidate.HalfExtents.X*2,candidate.HalfExtents.Y*2,candidate.HalfExtents.Z*2},units="meters"},options));
                break;
            }
            case "set-bounds":
            {
                var values=ParseOptions(args[2..],["bone","center","size","output"],true);
                if(!values.ContainsKey("reviewed"))throw new ArgumentException("Review the bounds, then pass --reviewed.");
                string output=NewOutput(Require(values,"output"),".dlrmodel",[source]);
                var model=await LoadModelAsync(source,token).ConfigureAwait(false);string bone=Require(values,"bone");
                var revised=CharacterBoneBoundsAuthoring.Apply(model,bone,ReadVector(Require(values,"center")),ReadVector(Require(values,"size")),reviewed:true);
                await SaveNewPackageAsync(revised.Package,output,token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new{format="dl-reanimated-bone-bounds-edit-v1",bone,outputFile=Path.GetFileName(output),
                    outputSha256=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(output,token).ConfigureAwait(false)))},options));
                break;
            }
            case "bounds-controls":
            {
                var values=ParseOptions(args[2..],["bone","scale","output-dir"],false);
                var model=await LoadModelAsync(source,token).ConfigureAwait(false);string bone=Require(values,"bone");
                if(!double.TryParse(Require(values,"scale"),NumberStyles.Float,CultureInfo.InvariantCulture,out double scale))throw new ArgumentException("Use a finite positive --scale.");
                var controls=CharacterBoneBoundsAuthoring.CreateAxisControls(model,bone,scale);
                string target=Path.GetFullPath(Require(values,"output-dir"));
                if(File.Exists(target)||Directory.Exists(target))throw new IOException("Choose a new control output directory.");
                string parent=Path.GetDirectoryName(target)??throw new ArgumentException("The controls need a parent directory.");Directory.CreateDirectory(parent);
                string stage=Path.Combine(parent,$".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
                var rows=new List<object>();
                try
                {
                    Directory.CreateDirectory(stage);
                    foreach(var control in controls)
                    {
                        token.ThrowIfCancellationRequested();string name=control.Name+".dlrmodel";
                        string path=Path.Combine(stage,name);CustomModelPackageSerializer.SaveAtomic(control.Model.Package,path);
                        var package=CustomModelPackageSerializer.Load(path);
                        if(!package.SourceFbx.AsSpan().SequenceEqual(model.Package.SourceFbx.AsSpan()) ||
                            JsonSerializer.Serialize(package.Document.MorphChannels,options)!=JsonSerializer.Serialize(model.Package.Document.MorphChannels,options) ||
                            package.CompanionPayloads.Count!=model.Package.CompanionPayloads.Count ||
                            model.Package.CompanionPayloads.Any(pair=>!package.CompanionPayloads.TryGetValue(pair.Key,out var original)||!original.AsSpan().SequenceEqual(pair.Value.AsSpan())))
                            throw new InvalidDataException("A bounds control changed retained character data.");
                        var bounds=package.Document.Bones.Single(value=>value.Name==bone).LocalBounds!.Value;
                        rows.Add(new{fileName=name,control.Axis,sha256=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path,token).ConfigureAwait(false))),
                            center=new[]{bounds.Center.X,bounds.Center.Y,bounds.Center.Z},halfExtents=new[]{bounds.HalfExtents.X,bounds.HalfExtents.Y,bounds.HalfExtents.Z}});
                    }
                    byte[] manifest=JsonSerializer.SerializeToUtf8Bytes(new{format="dl-reanimated-bone-bounds-controls-v1",bone,scale,units="meters",sourceSha256=model.Package.Document.Source.ContentSha256,controls=rows},options);
                    await File.WriteAllBytesAsync(Path.Combine(stage,"controls.json"),manifest,token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();Directory.Move(stage,target);
                }
                finally{if(Directory.Exists(stage))Directory.Delete(stage,recursive:true);}
                Console.WriteLine(JsonSerializer.Serialize(new{format="dl-reanimated-bone-bounds-controls-v1",bone,scale,controls=rows},options));
                break;
            }
            case "export-effects":
            {
                var values = ParseOptions(args[2..], ["output", "compression"], false);
                string output = NewOutput(Require(values, "output"), ".rpack", [source]);
                Rp6lCompression compression = values.GetValueOrDefault("compression", "none") switch
                {
                    "none" => Rp6lCompression.None,
                    "zlib" => Rp6lCompression.Zlib,
                    _ => throw new ArgumentException("--compression must be none or zlib."),
                };
                await ExportEffectsAsync(source, output, compression, options, token).ConfigureAwait(false);
                break;
            }
            case "attach-source":
            {
                var values = ParseOptions(args[2..],
                    ["archive", "member", "virtual-name", "subsystem", "expected-sha256", "archive-sha256", "output"], true);
                if (!values.ContainsKey("reviewed"))
                    throw new ArgumentException("Review the selected source, then pass --reviewed.");
                string archivePath = Path.GetFullPath(Require(values, "archive"));
                string member = Require(values, "member");
                string virtualName = Require(values, "virtual-name");
                string expectedHash = Require(values, "expected-sha256");
                CharacterSubsystem subsystem = Require(values, "subsystem") switch
                {
                    "geometry" => CharacterSubsystem.Geometry,
                    "rig" => CharacterSubsystem.Rig,
                    "skinning" => CharacterSubsystem.Skinning,
                    "materials" => CharacterSubsystem.Materials,
                    "textures" => CharacterSubsystem.Textures,
                    "lods" => CharacterSubsystem.Lods,
                    "morphs" => CharacterSubsystem.Morphs,
                    "variants" => CharacterSubsystem.Variants,
                    "facial-definitions" => CharacterSubsystem.FacialDefinitions,
                    "ragdoll" => CharacterSubsystem.Ragdoll,
                    "cloth" => CharacterSubsystem.Cloth,
                    "damage" => CharacterSubsystem.Damage,
                    "helpers" => CharacterSubsystem.Helpers,
                    "detached-parts" => CharacterSubsystem.DetachedParts,
                    _ => throw new ArgumentException("Choose a valid character --subsystem."),
                };
                string output = NewOutput(Require(values, "output"), ".dlrmodel", [source, archivePath]);
                var package = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var loaded = CustomModelPackageSerializer.Load(source);
                    token.ThrowIfCancellationRequested();
                    return loaded;
                }, token).ConfigureAwait(false);
                var revised = await CharacterSupplementalSourceAuthoring.AttachAsync(package, archivePath,
                    member, virtualName, subsystem, expectedHash, reviewed: true,
                    values.GetValueOrDefault("archive-sha256"), token).ConfigureAwait(false);
                await SaveNewPackageAsync(revised, output, token).ConfigureAwait(false);
                var resource = revised.Document.CharacterResources!.Resources[^1];
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    format = "dl-reanimated-character-source-attachment-v1",
                    outputFile = Path.GetFileName(output),
                    resource.Id, resource.LogicalName, resource.ContentSha256, resource.ByteLength,
                    subsystem = resource.Subsystem.ToString(),
                    status = resource.Status.ToString(),
                    resource.Required, resource.SupplementalSource,
                    blockers = revised.Document.CharacterResources.ExportBlockers,
                }, options));
                break;
            }
            case "export-neutral":
            {
                var values = ParseOptions(args[2..], ["surface", "output"], false);
                string surfaceId = Require(values, "surface");
                string output = NewOutput(Require(values, "output"), ".obj", [source]);
                var target = await LoadModelAsync(source, token).ConfigureAwait(false);
                var surface = RequireSurface(target, surfaceId);
                token.ThrowIfCancellationRequested();
                byte[] bytes = ManualMorphObjCodec.EncodeNeutral(surface);
                await WriteNewFileAsync(output, bytes, token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    format = "dl-reanimated-character-neutral-export-v1",
                    outputFile = Path.GetFileName(output),
                    surfaceId,
                    vertexCount = surface.Vertices.Length,
                    triangleCount = surface.Indices.Length / 3,
                    units = "meters",
                    outputSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                }, options));
                break;
            }
            case "import-sculpt":
            {
                var values = ParseOptions(args[2..],
                    ["reference", "target-surface", "reference-surface", "expression", "sculpt", "conflict", "output"], true);
                if (!values.ContainsKey("reviewed"))
                    throw new ArgumentException("Review the sculpt, then pass --reviewed.");
                string referencePath = Path.GetFullPath(Require(values, "reference"));
                string sculptPath = Path.GetFullPath(Require(values, "sculpt"));
                string targetSurfaceId = Require(values, "target-surface");
                string referenceSurfaceId = Require(values, "reference-surface");
                string expression = Require(values, "expression");
                MorphTransferConflict conflict = values.GetValueOrDefault("conflict", "reject") switch
                {
                    "reject" => MorphTransferConflict.Reject,
                    "keep" => MorphTransferConflict.KeepExisting,
                    "replace" => MorphTransferConflict.ReplaceExisting,
                    _ => throw new ArgumentException("--conflict must be reject, keep or replace."),
                };
                string output = NewOutput(Require(values, "output"), ".dlrmodel", [source, referencePath, sculptPath]);
                var target = await LoadModelAsync(source, token).ConfigureAwait(false);
                var reference = await LoadModelAsync(referencePath, token).ConfigureAwait(false);
                RequireSurface(target, targetSurfaceId);
                RequireSurface(reference, referenceSurfaceId);
                var deltas = await CharacterManualExpressionAuthoring.ReadDeltasAsync(target, targetSurfaceId, sculptPath, token)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var revised = CharacterManualExpressionAuthoring.Apply(target, reference, targetSurfaceId, referenceSurfaceId,
                    expression, deltas, conflict, reviewed: true);
                token.ThrowIfCancellationRequested();
                await SaveNewPackageAsync(revised.Package, output, token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    format = "dl-reanimated-character-sculpt-import-v1",
                    outputFile = Path.GetFileName(output),
                    expression,
                    targetSurfaceId,
                    referenceSurfaceId,
                    conflict = values.GetValueOrDefault("conflict", "reject"),
                    keptExisting = ReferenceEquals(target, revised),
                    sourceSha256 = revised.Package.Document.Source.ContentSha256,
                    reviews = revised.Package.Document.MorphAuthoringRecords.Select(ReviewSummary),
                }, options));
                break;
            }
            default:
                throw new ArgumentException("Unknown character command. " + Usage);
        }
        return 0;
    }

    private static async Task InspectAsync(string source, JsonSerializerOptions options, CancellationToken token)
    {
        var model = await LoadModelAsync(source, token).ConfigureAwait(false);
        var document = model.Package.Document;
        var inventory = document.CharacterResources;
        var blockers = (inventory?.ExportBlockers ?? [])
            .AddRange(MorphAuthoringEvidence.ExportBlockers(model))
            .AddRange(CharacterBodyRegionAuthoring.ExportBlockers(model))
            .Distinct(StringComparer.Ordinal).ToArray();
        token.ThrowIfCancellationRequested();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            format = "dl-reanimated-character-inspection-v1",
            sourceFile = Path.GetFileName(source),
            document.ModelId,
            document.Name,
            sourceKind = document.Source.Kind.ToString(),
            sourceSha256 = document.Source.ContentSha256,
            surfaces = model.Surfaces.Select(surface => new
            {
                surface.Id,
                surface.MeshName,
                surface.MaterialId,
                vertexCount = surface.Vertices.Length,
                triangleCount = surface.Indices.Length / 3,
                surface.IsSkinned,
                morphs = surface.MorphTargets.Select(morph => new
                {
                    morph.Name, morph.DescriptorHash,
                    targetSlot = document.MorphChannels.Single(channel => channel.Name == morph.Name).Index,
                    affectedVertexCount = morph.PositionDeltas.Count(delta => delta.LengthSquared > 0),
                }),
            }),
            materials = document.Materials.Select(material => new
            {
                material.Id, material.Name, material.ExistingDl1MaterialReference,
                originalSlot = document.CharacterResources is { } resources &&
                    resources.OriginalMaterials.Take(resources.OriginalMaterialSlotCount)
                        .Any(original => string.Equals(original.Name, material.ExistingDl1MaterialReference, StringComparison.Ordinal)),
            }),
            morphs = document.MorphChannels.Select(channel => new
            {
                channel.Name, channel.DescriptorHash, targetSlot = channel.Index,
            }),
            lodBindings = inventory?.MorphBindings ?? [],
            lods = model.SourceCharacterLods,
            fbxLods = model.SourceLodGroups.Select(group => new
            {
                group.GroupName,
                levels = group.Levels.Select(level => new
                {
                    level.LevelIndex, level.ModelName,
                    geometries = level.Geometries.Select(geometry => new { geometry.GeometryName, geometry.GeometryObjectId }),
                }),
            }),
            bounds = document.CreateEffectiveBones().Select(bone => new
            {
                bone.Index, bone.Name, bone.ParentIndex,
                center = bone.LocalBounds is { } bounds ? new[] { bounds.Center.X, bounds.Center.Y, bounds.Center.Z } : null,
                halfExtents = bone.LocalBounds is { } size ? new[] { size.HalfExtents.X, size.HalfExtents.Y, size.HalfExtents.Z } : null,
            }),
            subsystems = inventory?.Subsystems.Select(system => new
            {
                subsystem = system.Subsystem.ToString(), status = system.Status.ToString(), system.Detail,
            }),
            resources = inventory?.Resources.Select(resource => new
            {
                resource.Id, resource.LogicalName, resource.ContentSha256, resource.ByteLength,
                subsystem = resource.Subsystem.ToString(), status = resource.Status.ToString(),
                resource.Required, resource.IsOriginalArchive, hasPayload = resource.EntryPath is not null,
                resource.Detail, resource.ReferencedBy, resource.SupplementalSource, resource.PackedEffect, resource.Material, resource.NativeResource,
            }),
            reviews = new
            {
                expressions = document.MorphAuthoringRecords.Select(ReviewSummary),
                actors = inventory?.ActorSourceReviews.Select(review => new
                {
                    review.ActorResourceId, review.ExactModelName, review.ContentSha256,
                    review.ModelDeclarationCallIndex, review.ActorScopeCallIndex,
                    referenceCount = review.ReviewedReferences.Length,
                }),
                bodyRegions = inventory?.BodyRegionReviews.Select(review => new
                {
                    review.BodyResourceId, review.BodySourceSha256, review.ElementToken, review.HelperName, review.Accepted,
                    review.HelperFrameSha256, review.GeometrySha256,
                    review.ArtistGeometryReviewed, review.RelationshipsReviewed,
                    review.CutCapSurfaceIds, review.DetachedAssets, review.PhysicsAssets, review.EffectAssets,
                }),
            },
            validation = new
            {
                dependencyComplete = inventory?.IsDependencyComplete ?? false,
                runtimeAccepted = inventory?.IsGameReady ?? false,
                compiledSemanticSha256 = inventory?.CompiledSemanticSha256, loadedResourceSha256 = inventory?.LoadedResourceSha256,
                scenarios = inventory?.VerifiedPlayerScenarios ?? [],
            },
            blockers,
        }, options));
    }

    private static async Task ExportEffectsAsync(string source, string output, Rp6lCompression compression,
        JsonSerializerOptions options, CancellationToken token)
    {
        var package = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var loaded = CustomModelPackageSerializer.Load(source);
            token.ThrowIfCancellationRequested();
            _ = CustomModelPackageSerializer.Serialize(loaded);
            token.ThrowIfCancellationRequested();
            return loaded;
        }, token).ConfigureAwait(false);
        var inventory = package.Document.CharacterResources
            ?? throw new InvalidOperationException("The model has no character resource inventory.");
        var resources = inventory.Resources.Where(resource => resource.Required && !resource.IsOriginalArchive &&
            Path.GetExtension(resource.LogicalName).Equals(".fx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(resource => resource.LogicalName, StringComparer.Ordinal).ToArray();
        if (resources.Length == 0) throw new InvalidOperationException("The model has no required effects.");
        if (resources.Length > Rp6lEffectBundleDecoder.MaximumDefinitions)
            throw new InvalidDataException("Too many required effects.");
        var definitions=CharacterEffectResourceAuthoring.ReadRequiredDefinitions(package,token);
        int sourceBundleCount=resources.Select(resource=>resource.PackedEffect!.BundleResourceId).Distinct(StringComparer.Ordinal).Count();
        token.ThrowIfCancellationRequested();
        var cacheDirectory = Directory.CreateTempSubdirectory("dl-reanimated-character-effects-");
        try
        {
            await using var cache = new Rp6lChunkCache(new()
            {
                CacheDirectory = cacheDirectory.FullName,
                MaximumMemoryBytes = Rp6lEffectBundleDecoder.MaximumBytes,
                MaximumMemoryEntryBytes = Rp6lEffectBundleDecoder.MaximumBytes,
                MaximumDiskBytes = 128L * 1024 * 1024,
            });
            var result = await Rp6lEffectBundleWriter.WriteNewAsync(output, definitions,
                compression, cache, token).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                format = "dl-reanimated-character-effect-export-v1",
                outputFile = Path.GetFileName(result.Path),
                definitionCount = result.Readback.Definitions.Length,
                sourceBundleCount,
                compression = compression.ToString(),
                result.ArchiveSha256,
                result.Readback.PayloadSha256,
                result.Readback.ArchiveByteLength,
                resource = new
                {
                    result.Readback.Resource.Name, result.Readback.Resource.ResourceType,
                    result.Readback.Item.Index, result.Readback.Item.StorageGroupId,
                    result.Readback.Chunk.Flags, result.Readback.Chunk.Category,
                },
                definitions = result.Readback.Definitions.Select(definition => new
                {
                    definition.Name, definition.Kind, definition.ContentSha256, definition.TextByteLength,
                }),
            }, options));
        }
        finally
        {
            if (cacheDirectory.Exists) cacheDirectory.Delete(recursive: true);
        }
    }

    private static object ReviewSummary(MorphAuthoringRecord review) => new
    {
        review.Name, review.DescriptorHash, review.Accepted,
        method = review.Method.ToString(), conflict = review.ConflictChoice.ToString(),
        review.OriginalSourceChannelIndex, review.TargetChannelSlot,
        review.ReferenceSurfaceId, review.TargetSurfaceId,
        review.ReferenceSourceSha256, review.TargetNeutralFingerprint, review.AuthoredExpressionSha256,
        review.ReviewedRegionName,
        regionVertexCount = review.ReviewedTargetRegion.Length,
        landmarkCount = review.ReviewedLandmarks.Length,
        trianglePairCount = review.ReviewedTriangles.Length,
        lockedVertexCount = review.LockedTargetVertices.Length,
    };

    private static Dictionary<string, string> ParseOptions(string[] args, string[] allowed, bool allowReviewed)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (!option.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unexpected argument: " + option);
            string name = option[2..];
            if (name == "reviewed" && allowReviewed)
            {
                if (!values.TryAdd(name, "true")) throw new ArgumentException("Duplicate option: " + option);
                continue;
            }
            if (!allowed.Contains(name, StringComparer.Ordinal))
                throw new ArgumentException("Unknown option: " + option);
            if (values.ContainsKey(name)) throw new ArgumentException("Duplicate option: " + option);
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i]))
                throw new ArgumentException("Missing value for " + option + ".");
            values.Add(name, args[i]);
        }
        return values;
    }

    private static Vector3D ReadVector(string value)
    {
        string[] fields=value.Split(',');
        if(fields.Length!=3)throw new ArgumentException("Use three comma-separated coordinates.");
        double Number(string text)=>double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out double number)&&double.IsFinite(number)?number:throw new ArgumentException("Coordinates must be finite numbers.");
        return new(Number(fields[0]),Number(fields[1]),Number(fields[2]));
    }

    private static string Require(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) ? value : throw new ArgumentException("Missing --" + name + ".");

    private static FbxModelSurface RequireSurface(FbxModelAuthoringImportResult model, string id) =>
        model.Surfaces.SingleOrDefault(surface => surface.Id == id)
        ?? throw new ArgumentException("Surface not found: " + id);

    private static async Task<FbxModelAuthoringImportResult> LoadModelAsync(string path, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var package = CustomModelPackageSerializer.Load(path);
            token.ThrowIfCancellationRequested();
            var model = FbxModelAuthoringImporter.ImportPackage(package);
            token.ThrowIfCancellationRequested();
            return model;
        }, token).ConfigureAwait(false);
    }

    private static string NewOutput(string path, string extension, string[] inputs)
    {
        string output = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(output), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must use " + extension + ".");
        if (inputs.Any(input => string.Equals(Path.GetFullPath(input), output, StringComparison.OrdinalIgnoreCase)) ||
            File.Exists(output) || Directory.Exists(output))
            throw new IOException("Choose a new output file.");
        return output;
    }

    private static async Task WriteNewFileAsync(string output, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 65536, options: FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task SaveNewPackageAsync(CustomModelPackage package, string output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                CustomModelPackageSerializer.SaveAtomic(package, temporary);
                token.ThrowIfCancellationRequested();
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}