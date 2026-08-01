using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;
using ReAnimated.Core.Storage;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Ik;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Cli;

internal static class ProjectExportCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        if (args.Length is < 3 or > 5)
        {
            throw new ArgumentException(
                "Usage: DLReAnimated export-project <project.dlraproj> <dl1-install> <output-directory> [animation-id-or-name] [body|mimic|both]");
        }

        string projectPath = RequireExistingFile(args[0]);
        string installPath = Path.GetFullPath(args[1]);
        string outputDirectory = Path.GetFullPath(args[2]);
        if (!Directory.Exists(installPath))
        {
            throw new DirectoryNotFoundException(
                $"Dying Light 1 install was not found: {installPath}");
        }

        DlraProject project = ProjectSerializer.Load(projectPath);
        ProjectAnimation animation = ResolveAnimation(
            project,
            args.Length >= 4 ? args[3] : null);
        ProjectAnimationSourceBinding sourceBinding =
            animation.SourceBinding ??
            throw new InvalidDataException(
                "The animation has no provable immutable source binding. Rebind it in the C# application before export.");
        Dl1AnimationExportParts parts = ResolveParts(
            args.Length == 5 ? args[4] : "body");
        ProjectAssetReference sourceAsset = ResolveAsset(
            project,
            animation.SourceAssetId,
            RequiredAssetKind(sourceBinding.Kind));
        ProjectAssetReference targetAsset = ResolveAsset(
            project,
            animation.TargetAssetId
                ?? throw new InvalidOperationException(
                    "The animation has no saved retail target asset."),
            ProjectAssetKind.RetailGameResource);
        ProjectRetailAssetIdentity identity =
            targetAsset.RetailIdentity
            ?? throw new InvalidDataException(
                "The target asset has no retail identity.");

        string actualInstallId =
            RetailAssetIdentity.CreateInstallId(installPath);
        if (!string.Equals(
                actualInstallId,
                identity.InstallFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected DL1 installation does not match the project's retail install fingerprint.");
        }

        string packPath = ResolveContainedPath(
            installPath,
            identity.ProviderPack,
            requireFile: true);
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(
            packPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        int resourceIndex = identity.ResourceIndex
            ?? throw new InvalidDataException(
                "The retail target has no RP6L resource index.");
        if ((uint)resourceIndex >=
            (uint)archive.Resources.Count)
        {
            throw new InvalidDataException(
                "The saved retail resource index is outside its provider pack.");
        }

        Rp6lResourceDescriptor resource =
            archive.Resources[resourceIndex];
        if (resource.ResourceType != identity.ResourceType ||
            !string.Equals(
                resource.Name,
                identity.ResourceName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The retail target identity no longer matches its provider pack.");
        }

        string cacheDirectory =
            LocalApplicationPaths.CreateDefault()
                .RpackCacheDirectory;
        await using var cache = new Rp6lChunkCache(
            new Rp6lChunkCacheOptions
            {
                CacheDirectory = cacheDirectory,
            });
        await using (Stream stream =
                     await archive.OpenResourceStreamAsync(
                         resource,
                         cache,
                         cancellationToken).ConfigureAwait(false))
        {
            string actualTargetHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        stream,
                        cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(
                    actualTargetHash,
                    identity.ContentSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    actualTargetHash,
                    targetAsset.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The retail target content hash changed after the project was authored.");
            }
        }

        Dl1MeshData targetMesh =
            await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                resource,
                cache,
                cancellationToken).ConfigureAwait(false);
        RigDefinition targetRig = targetMesh.Rig
            ?? throw new InvalidDataException(
                "The selected retail resource has no decodable animation rig.");

        string projectDirectory =
            Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException(
                "The project has no parent directory.");
        RigDefinition sourceRig;
        AnimationClip clip;
        if (sourceBinding.Kind == AnimationSourceKind.LocalFbx)
        {
            string sourcePath = ResolveContainedPath(
                projectDirectory,
                sourceAsset.RelativePath,
                requireFile: true);
            string actualSourceHash =
                await ComputeSha256Async(
                    sourcePath,
                    cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    actualSourceHash,
                    sourceAsset.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The authored FBX source hash no longer matches the project.");
            }
            if (!string.Equals(
                    Path.GetExtension(sourcePath),
                    ".fbx",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A local-FBX source binding does not refer to an FBX file.");
            }

            FbxCoreAnimationImportResult decoded =
                await new FbxAnimationDecoder()
                    .DecodeFileAsync(
                        sourcePath,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            sourceRig = decoded.Rig;
            clip = decoded.Clip;
        }
        else
        {
            DecodedBoundAnm2Source decoded =
                await DecodeBoundAnm2SourceAsync(
                    project,
                    sourceBinding,
                    sourceAsset,
                    installPath,
                    actualInstallId,
                    projectDirectory,
                    targetAsset,
                    targetRig,
                    cache,
                    animation.FrameRate,
                    cancellationToken).ConfigureAwait(false);
            sourceRig = decoded.Rig;
            clip = decoded.CombinedClip;
        }
        if (clip.FrameRate != animation.FrameRate ||
            clip.FrameCount != animation.FrameCount)
        {
            throw new InvalidDataException(
                "The decoded source cadence or frame count differs from the saved project.");
        }

        AnimationClip synchronizedClip = clip;
        string? mimicSourceHash = null;
        string? facialSourceHash = null;
        if (animation.MimicAssetId is { } mimicAssetId)
        {
            if (animation.FacialAnimationSourceBinding is
                { } facialBinding)
            {
                ProjectAssetReference mimicAsset = ResolveAsset(
                    project,
                    mimicAssetId,
                    RequiredAssetKind(facialBinding.Kind));
                DecodedBoundAnm2Source facial =
                    await DecodeBoundAnm2SourceAsync(
                        project,
                        facialBinding,
                        mimicAsset,
                        installPath,
                        actualInstallId,
                        projectDirectory,
                        targetAsset,
                        targetRig,
                        cache,
                        animation.FacialTiming?.NativeFrameRate ??
                            animation.FrameRate,
                        cancellationToken).ConfigureAwait(false);
                FacialClipTiming timing = animation.FacialTiming ??
                    FacialClipTiming.ForClip(facial.FacialClip);
                synchronizedClip =
                    AnimationClipSynchronization.Synchronize(
                        clip,
                        facial.FacialClip,
                        timing);
                mimicSourceHash = facial.SourceSha256;
            }
            else
            {
                ProjectAssetReference mimicAsset = ResolveAsset(
                    project,
                    mimicAssetId,
                    ProjectAssetKind.SourceAnimation);
                if (mimicAsset.ContentSha256 is not
                    { } expectedMimicHash)
                {
                    throw new InvalidDataException(
                        "The saved mimic project asset has no SHA-256 fingerprint.");
                }

                string mimicSourcePath = ResolveContainedPath(
                    projectDirectory,
                    mimicAsset.RelativePath,
                    requireFile: true);
                SynchronizedMimicAnimation loaded =
                    await SynchronizedMimicAnm2Loader.LoadAsync(
                        mimicSourcePath,
                        expectedMimicHash,
                        targetRig,
                        clip,
                        animation.FrameRate,
                        animation.FrameCount,
                        cancellationToken).ConfigureAwait(false);
                FacialClipTiming timing = animation.FacialTiming ??
                    loaded.Timing;
                synchronizedClip =
                    AnimationClipSynchronization.Synchronize(
                        clip,
                        loaded.Mimic,
                        timing);
                mimicSourceHash = loaded.Sha256;
            }
        }
        else if (animation.FacialSourceAssetId is
        { } facialSourceAssetId)
        {
            ProjectAssetReference facialSourceAsset = ResolveAsset(
                project,
                facialSourceAssetId,
                ProjectAssetKind.SourceAnimation);
            if (facialSourceAsset.ContentSha256 is not
                { } expectedFacialSourceHash)
            {
                throw new InvalidDataException(
                    "The saved facial FBX project asset has no SHA-256 fingerprint.");
            }

            string facialSourcePath = ResolveContainedPath(
                projectDirectory,
                facialSourceAsset.RelativePath,
                requireFile: true);
            if (!string.Equals(
                    Path.GetExtension(facialSourcePath),
                    ".fbx",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The saved facial project asset is not an FBX file.");
            }

            facialSourceHash = await ComputeSha256Async(
                    facialSourcePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    facialSourceHash,
                    expectedFacialSourceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The authored facial FBX source hash no longer matches the project.");
            }

            ProjectMorphSourceValueUnit sourceValueUnit =
                animation.FacialSourceValueUnit ??
                throw new InvalidDataException(
                    "The saved facial FBX has no explicit source-value unit.");
            FbxFacialAnimationImportResult facial =
                await new FbxFacialAnimationDecoder()
                    .DecodeFileAsync(
                        facialSourcePath,
                        new FbxFacialAnimationImportOptions
                        {
                            SamplingFrameRate =
                                animation.FrameRate,
                            DefaultSourceValueUnit =
                                sourceValueUnit switch
                                {
                                    ProjectMorphSourceValueUnit
                                        .Normalized =>
                                        FbxFacialSourceValueUnit
                                            .Normalized,
                                    ProjectMorphSourceValueUnit
                                        .Percent =>
                                        FbxFacialSourceValueUnit
                                            .Percent,
                                    _ => throw new InvalidDataException(
                                        "The saved facial FBX source-value unit is unsupported."),
                                },
                        },
                        cancellationToken:
                            cancellationToken)
                    .ConfigureAwait(false);
            if (!facial.Clip.TransformTracks.IsEmpty ||
                facial.Clip.FrameRate != animation.FrameRate ||
                facial.Clip.FrameCount != animation.FrameCount)
            {
                throw new InvalidDataException(
                    "The decoded facial FBX is not a scalar-only clip on the exact saved body timeline.");
            }

            Dl1MimicProfile profile =
                Dl1MimicProfileCodec.ReadBuiltInCommon46();
            string profileId = animation.MimicProfileId ??
                throw new InvalidDataException(
                    "The saved facial FBX has no DL1 mimic profile.");
            if (!string.Equals(
                    profile.ProfileId,
                    profileId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The saved facial FBX uses unsupported mimic profile '{profileId}'.");
            }

            string expectedMappingFingerprint =
                animation.MimicMappingFingerprint ??
                throw new InvalidDataException(
                    "The saved facial FBX has no mapping fingerprint.");
            string actualMappingFingerprint =
                FbxFacialProjectReviewService
                    .ComputeMappingFingerprint(
                        profileId,
                        targetRig,
                        new AnimationTiming(
                            animation.FrameRate,
                            animation.FrameCount),
                        animation.MorphBindings);
            if (!string.Equals(
                    actualMappingFingerprint,
                    expectedMappingFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The saved facial FBX mapping fingerprint no longer matches its exact retail rig, body timing, and reviewed rows.");
            }

            synchronizedClip =
                AnimationClipSynchronization.Synchronize(
                    clip,
                    facial.Clip);
        }

        string sourceSignature = RigSignature.Compute(sourceRig);
        string targetSignature = RigSignature.Compute(targetRig);
        if (!string.Equals(
                sourceSignature,
                animation.SourceRigSignature,
                StringComparison.Ordinal) ||
            !string.Equals(
                targetSignature,
                animation.TargetRigSignature,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The source or retail target rig signature differs from the saved mapping.");
        }

        RetargetMap? mapping = null;
        string? mappingFingerprint = null;
        bool directSameRig = string.Equals(
            sourceSignature,
            targetSignature,
            StringComparison.OrdinalIgnoreCase);
        if (!directSameRig)
        {
            mapping = BuildMap(
                sourceRig,
                targetRig,
                animation.BoneMappings,
                animation.TargetBindReviews);
            mappingFingerprint =
                RetargetMapFingerprint.Compute(
                    sourceSignature,
                    targetSignature,
                    identity.ContentSha256,
                    mapping);
            if (!string.Equals(
                    mappingFingerprint,
                    animation.MappingFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The saved mapping fingerprint does not match its rigs, target asset, and mapping rows.");
            }

            RetargetMappingReviewReport mappingReview =
                RetargetMappingReview.Analyze(
                    sourceRig,
                    targetRig,
                    mapping);
            if (!mappingReview.IsReady)
            {
                string reasons = string.Join(
                    "; ",
                    mappingReview.Diagnostics
                        .Where(static diagnostic =>
                            diagnostic.Severity ==
                            CompatibilityDiagnosticSeverity.Error)
                        .Select(static diagnostic =>
                            diagnostic.Message));
                throw new InvalidDataException(
                    "The saved retarget mapping has not passed explicit review: " +
                    reasons);
            }
        }

        AnimationRootMode rootMode =
            animation.RootMotionMode switch
            {
                Dl1RootMotionMode.Recorded =>
                    AnimationRootMode.Recorded,
                Dl1RootMotionMode.InPlace =>
                    AnimationRootMode.InPlace,
                Dl1RootMotionMode.Bip01 =>
                    AnimationRootMode.Bip01,
                Dl1RootMotionMode.MotionAccumulator =>
                    AnimationRootMode.MotionAccumulator,
                _ => throw new InvalidDataException(
                    "The project contains an unknown root-motion mode."),
            };
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            sourceRig,
            targetRig,
            mapping,
            rootMode);
        var evaluation = new EvaluationRequest(
            sourceRig,
            targetRig,
            synchronizedClip,
            0,
            PreviewProfile.RawAuthoring,
            mapping,
            animation.EditLayers,
            purpose: EvaluationPurpose.Export,
            attachments: animation.Attachments,
            dl1AuthoringPolicy: policy,
            morphBindings: ProjectMorphBindingResolver.Resolve(
                animation.MorphBindings,
                targetRig,
                ProjectMorphBindingResolutionMode.Export),
            morphEditLayers: animation.MorphEditLayers,
            ikLayers: BuildIkLayers(
                animation,
                targetRig));
        var exporter = new Dl1AnimationExporter(
            new Anm2EvaluationAdapter(
                new AnimationEvaluator()));
        Dl1AnimationExportResult result = exporter.Export(
            new Dl1AnimationExportRequest
            {
                Evaluation = evaluation,
                Parts = parts,
            },
            cancellationToken);

        Directory.CreateDirectory(outputDirectory);
        string safeName = MakeSafeFileName(animation.Name);
        string? bodyPath = null;
        if (result.BodyAnm2 is not null)
        {
            bodyPath = Path.Combine(
                outputDirectory,
                safeName + ".anm2");
            await Rp6lAnimationLibraryCodec.WriteAtomicAsync(
                bodyPath,
                result.BodyAnm2,
                cancellationToken).ConfigureAwait(false);
        }

        string? mimicPath = null;
        if (result.MimicAnm2 is not null)
        {
            mimicPath = Path.Combine(
                outputDirectory,
                safeName + "_mimic.anm2");
            await Rp6lAnimationLibraryCodec.WriteAtomicAsync(
                mimicPath,
                result.MimicAnm2,
                cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                format =
                    "dl-reanimated-project-export-result-v1",
                projectPath,
                animation.Id,
                animation.Name,
                parts = parts.ToString(),
                bodyPath,
                mimicPath,
                sourceSignature,
                targetSignature,
                mappingFingerprint,
                mimicSourceHash,
                facialSourceHash,
                frameCount =
                    result.AuthoredSequence.Frames.Length,
                frameRate =
                    result.AuthoredSequence.FrameRate,
            },
            jsonOptions));
        return 0;
    }

    private sealed record DecodedBoundAnm2Source(
        RigDefinition Rig,
        AnimationClip CombinedClip,
        AnimationClip FacialClip,
        string SourceSha256);

    private sealed record RetailResourceHandle(
        ProjectRetailAssetIdentity Identity,
        Rp6lArchive Archive,
        Rp6lResourceDescriptor Resource);

    private static ProjectAssetKind RequiredAssetKind(
        AnimationSourceKind kind) =>
        kind switch
        {
            AnimationSourceKind.LocalFbx or
            AnimationSourceKind.LocalAnm2 =>
                ProjectAssetKind.SourceAnimation,
            AnimationSourceKind.RetailAnm2 =>
                ProjectAssetKind.RetailGameResource,
            _ => throw new InvalidDataException(
                "The project contains an unsupported animation source kind."),
        };

    private static async Task<DecodedBoundAnm2Source>
        DecodeBoundAnm2SourceAsync(
            DlraProject project,
            ProjectAnimationSourceBinding binding,
            ProjectAssetReference sourceAsset,
            string installPath,
            string actualInstallId,
            string projectDirectory,
            ProjectAssetReference decodedTargetAsset,
            RigDefinition decodedTargetRig,
            Rp6lChunkCache cache,
            FrameRate frameRate,
            CancellationToken cancellationToken)
    {
        if (binding.Kind is not (
                AnimationSourceKind.LocalAnm2 or
                AnimationSourceKind.RetailAnm2) ||
            binding.AssetId != sourceAsset.Id)
        {
            throw new InvalidDataException(
                "The ANM2 source binding disagrees with its project asset.");
        }

        ProjectAssetReference sourceModelAsset = ResolveAsset(
            project,
            binding.RetailSourceModelAssetId ??
                throw new InvalidDataException(
                    "The ANM2 source has no exact retail source-model binding."),
            ProjectAssetKind.RetailGameResource);
        RigDefinition sourceRig;
        if (RetailProjectAssetsMatch(
                sourceModelAsset,
                decodedTargetAsset))
        {
            sourceRig = decodedTargetRig;
        }
        else
        {
            sourceRig = await DecodeRetailRigAsync(
                    sourceModelAsset,
                    installPath,
                    actualInstallId,
                    cache,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string sourceSignature = RigSignature.Compute(sourceRig);
        if (!string.Equals(
                sourceSignature,
                binding.SourceRigSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The exact retail ANM2 source-model rig differs from the immutable saved signature.");
        }

        Anm2Clip raw;
        string sourceSha256;
        if (binding.Kind == AnimationSourceKind.LocalAnm2)
        {
            string sourcePath = ResolveContainedPath(
                projectDirectory,
                sourceAsset.RelativePath,
                requireFile: true);
            if (!string.Equals(
                    Path.GetExtension(sourcePath),
                    ".anm2",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A local-ANM2 source binding does not refer to an ANM2 file.");
            }

            sourceSha256 = await ComputeSha256Async(
                    sourcePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    sourceSha256,
                    sourceAsset.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The local ANM2 differs from its immutable project fingerprint.");
            }
            raw = await new Anm2Decoder().DecodeFileAsync(
                    sourcePath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            (raw, sourceSha256) =
                await DecodeRetailAnimationAsync(
                        sourceAsset,
                        installPath,
                        actualInstallId,
                        cache,
                        cancellationToken)
                    .ConfigureAwait(false);
        }

        Anm2PartitionedImportResult partitioned =
            Anm2TrackPartitioner.Partition(
                raw,
                sourceRig,
                frameRate,
                cancellationToken);
        if (partitioned.Partition.RequiresReview)
        {
            throw new InvalidDataException(
                "The ANM2 contains bone/morph descriptor collisions that still require review.");
        }
        if (binding.Partition is not { } expectedPartition ||
            !string.Equals(
                expectedPartition.Fingerprint,
                partitioned.Partition.Fingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The ANM2 track partition differs from its immutable saved source binding.");
        }

        return new DecodedBoundAnm2Source(
            sourceRig,
            partitioned.CombinedClip,
            partitioned.FacialClip,
            sourceSha256);
    }

    private static async Task<RigDefinition> DecodeRetailRigAsync(
        ProjectAssetReference asset,
        string installPath,
        string actualInstallId,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken)
    {
        RetailResourceHandle handle = await OpenRetailResourceAsync(
                asset,
                installPath,
                actualInstallId,
                cancellationToken)
            .ConfigureAwait(false);
        if (handle.Resource.ResourceType != Rp6lResourceTypes.Mesh)
        {
            throw new InvalidDataException(
                "The saved ANM2 source model is not a type-272 mesh resource.");
        }
        await VerifyRetailResourceHashAsync(
                asset,
                handle,
                cache,
                cancellationToken)
            .ConfigureAwait(false);
        Dl1MeshData mesh = await Dl1MeshResourceDecoder.DecodeAsync(
                handle.Archive,
                handle.Resource,
                cache,
                cancellationToken)
            .ConfigureAwait(false);
        return mesh.Rig ?? throw new InvalidDataException(
            "The saved ANM2 source model has no decodable rig.");
    }

    private static async Task<(Anm2Clip Clip, string Sha256)>
        DecodeRetailAnimationAsync(
            ProjectAssetReference asset,
            string installPath,
            string actualInstallId,
            Rp6lChunkCache cache,
            CancellationToken cancellationToken)
    {
        RetailResourceHandle handle = await OpenRetailResourceAsync(
                asset,
                installPath,
                actualInstallId,
                cancellationToken)
            .ConfigureAwait(false);
        if (handle.Resource.ResourceType !=
            Rp6lResourceTypes.Animation)
        {
            throw new InvalidDataException(
                "The saved retail animation is not a type-320 ANM2 resource.");
        }

        await using Stream stream =
            await handle.Archive.OpenResourceStreamAsync(
                handle.Resource,
                cache,
                cancellationToken).ConfigureAwait(false);
        byte[] payload = await ReadBoundedAsync(
                stream,
                Anm2Reader.DefaultMaximumPayloadBytes,
                cancellationToken)
            .ConfigureAwait(false);
        string sha256 = Convert.ToHexString(
                SHA256.HashData(payload))
            .ToLowerInvariant();
        VerifyRetailContentFingerprint(asset, handle.Identity, sha256);
        return (
            new Anm2Decoder().Decode(
                payload,
                handle.Resource.Name),
            sha256);
    }

    private static Task<RetailResourceHandle> OpenRetailResourceAsync(
        ProjectAssetReference asset,
        string installPath,
        string actualInstallId,
        CancellationToken cancellationToken)
    {
        ProjectRetailAssetIdentity identity = asset.RetailIdentity
            ?? throw new InvalidDataException(
                "A retail project asset has no immutable identity.");
        if (!string.Equals(
                identity.InstallFingerprint,
                actualInstallId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A retail animation/model source belongs to a different DL1 installation fingerprint.");
        }

        string packPath = ResolveContainedPath(
            installPath,
            identity.ProviderPack,
            requireFile: true);
        return OpenAsync();

        async Task<RetailResourceHandle> OpenAsync()
        {
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(
                    packPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            int resourceIndex = identity.ResourceIndex ??
                throw new InvalidDataException(
                    "A retail animation/model identity has no resource index.");
            if ((uint)resourceIndex >= (uint)archive.Resources.Count)
            {
                throw new InvalidDataException(
                    "A retail animation/model resource index is outside its provider pack.");
            }
            Rp6lResourceDescriptor resource =
                archive.Resources[resourceIndex];
            if (resource.ResourceType != identity.ResourceType ||
                !string.Equals(
                    resource.Name,
                    identity.ResourceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A retail animation/model identity no longer matches its provider pack.");
            }
            return new RetailResourceHandle(
                identity,
                archive,
                resource);
        }
    }

    private static async Task VerifyRetailResourceHashAsync(
        ProjectAssetReference asset,
        RetailResourceHandle handle,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken)
    {
        await using Stream stream =
            await handle.Archive.OpenResourceStreamAsync(
                handle.Resource,
                cache,
                cancellationToken).ConfigureAwait(false);
        string sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(
                    stream,
                    cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        VerifyRetailContentFingerprint(
            asset,
            handle.Identity,
            sha256);
    }

    private static void VerifyRetailContentFingerprint(
        ProjectAssetReference asset,
        ProjectRetailAssetIdentity identity,
        string actualSha256)
    {
        if (!string.Equals(
                actualSha256,
                identity.ContentSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                actualSha256,
                asset.ContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A retail animation/model resource changed after the project source binding was authored.");
        }
    }

    private static bool RetailProjectAssetsMatch(
        ProjectAssetReference first,
        ProjectAssetReference second)
    {
        if (first.RetailIdentity is not { } left ||
            second.RetailIdentity is not { } right)
        {
            return false;
        }
        return string.Equals(
                   first.ContentSha256,
                   second.ContentSha256,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   left.InstallFingerprint,
                   right.InstallFingerprint,
                   StringComparison.Ordinal) &&
               string.Equals(
                   left.ProviderId,
                   right.ProviderId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   left.ProviderPack,
                   right.ProviderPack,
                   StringComparison.OrdinalIgnoreCase) &&
               left.ResourceType == right.ResourceType &&
               left.ResourceIndex == right.ResourceIndex &&
               string.Equals(
                   left.ResourceName,
                   right.ResourceName,
                   StringComparison.OrdinalIgnoreCase) &&
               left.Precedence == right.Precedence;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        using var output = new MemoryStream();
        while (true)
        {
            int read = await stream.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    $"A retail ANM2 exceeds the bounded {maximumBytes:N0}-byte decode limit.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static RetargetMap BuildMap(
        RigDefinition source,
        RigDefinition target,
        ImmutableArray<ProjectBoneMapping> rows,
        ImmutableArray<ProjectTargetBindReview> targetBindReviews) =>
        new(
            source.Id,
            target.Id,
            rows.Select(row =>
            {
                int sourceIndex =
                    source.GetBoneIndex(row.SourceBoneName);
                int targetIndex =
                    target.GetBoneIndex(row.TargetBoneName);
                if (sourceIndex < 0 || targetIndex < 0)
                {
                    throw new InvalidDataException(
                        $"Mapping '{row.SourceBoneName}' -> '{row.TargetBoneName}' is absent from its saved rigs.");
                }

                BoneMappingMethod method = Enum.TryParse(
                    row.Method,
                    ignoreCase: true,
                    out BoneMappingMethod parsed)
                    ? parsed
                    : BoneMappingMethod.Manual;
                return new BoneMapEntry(
                    sourceIndex,
                    targetIndex,
                    method,
                    MappingConfidence(method),
                    row.IsLocked,
                    row.IsReviewed,
                    row.MappingKind,
                    row.TransferPolicy,
                    row.ComponentPolicy);
            }),
            targetBindReviews.Select(review =>
            {
                if ((uint)review.TargetBoneIndex >=
                        (uint)target.BoneCount ||
                    !string.Equals(
                        target.Bones[review.TargetBoneIndex].Name,
                        review.TargetBoneName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Reviewed target-bind row {review.TargetBoneIndex} ('{review.TargetBoneName}') does not match the saved target rig.");
                }

                return review.TargetBoneIndex;
            }));

    private static IkConstraintLayer[] BuildIkLayers(
        ProjectAnimation animation,
        RigDefinition rig)
    {
        Dictionary<string, TwoBoneIkChainDefinition> chains =
            rig.IkChains.ToDictionary(
                static chain => chain.Name,
                StringComparer.OrdinalIgnoreCase);
        return animation.IkLayers.Select(layer =>
        {
            if (!chains.TryGetValue(
                    layer.ChainName,
                    out TwoBoneIkChainDefinition? chain))
            {
                throw new InvalidDataException(
                    $"IK chain '{layer.ChainName}' is not validated for retail rig '{rig.Id}'.");
            }

            return new IkConstraintLayer(
                layer.Id,
                layer.Name,
                chain.RootBoneIndex,
                chain.JointBoneIndex,
                chain.EndBoneIndex,
                layer.Weight,
                layer.Keyframes.Select(static key =>
                    new IkConstraintKeyframe(
                        key.Frame,
                        key.Effector,
                        key.Pole,
                        key.EndOrientation)),
                layer.Enabled,
                layer.BakeToEditLayer);
        }).ToArray();
    }

    private static ProjectAnimation ResolveAnimation(
        DlraProject project,
        string? selector)
    {
        if (project.Animations.IsEmpty)
        {
            throw new InvalidDataException(
                "The project contains no animations.");
        }

        if (string.IsNullOrWhiteSpace(selector))
        {
            return project.Animations[0];
        }

        ProjectAnimation? match = Guid.TryParse(
            selector,
            out Guid id)
            ? project.Animations.FirstOrDefault(
                animation => animation.Id == id)
            : project.Animations.FirstOrDefault(
                animation => string.Equals(
                    animation.Name,
                    selector,
                    StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidDataException(
            $"Project animation '{selector}' was not found.");
    }

    private static ProjectAssetReference ResolveAsset(
        DlraProject project,
        Guid id,
        ProjectAssetKind expectedKind)
    {
        ProjectAssetReference? asset =
            project.Assets.FirstOrDefault(
                candidate => candidate.Id == id);
        if (asset is null || asset.Kind != expectedKind)
        {
            throw new InvalidDataException(
                $"Project asset '{id}' is missing or has the wrong kind.");
        }

        return asset;
    }

    private static Dl1AnimationExportParts ResolveParts(
        string value) =>
        value.ToLowerInvariant() switch
        {
            "body" => Dl1AnimationExportParts.Body,
            "mimic" => Dl1AnimationExportParts.Mimic,
            "both" =>
                Dl1AnimationExportParts.BodyAndMimic,
            _ => throw new ArgumentException(
                "Export parts must be body, mimic, or both."),
        };

    private static string ResolveContainedPath(
        string root,
        string relativePath,
        bool requireFile)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                "Project paths must remain relative.");
        }

        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(
            Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(
                fullRoot,
                StringComparison.OrdinalIgnoreCase) ||
            (requireFile && !File.Exists(fullPath)))
        {
            throw new FileNotFoundException(
                $"Project path '{relativePath}' is missing or escapes its root.",
                fullPath);
        }

        return fullPath;
    }

    private static string RequireExistingFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException(
                "Project file was not found.",
                fullPath);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(
                    stream,
                    cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static string MakeSafeFileName(string name)
    {
        HashSet<char> invalid =
            Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new(name
            .Trim()
            .Select(character => invalid.Contains(character)
                ? '_'
                : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe)
            ? "animation"
            : safe;
    }

    private static double MappingConfidence(
        BoneMappingMethod method) =>
        method switch
        {
            BoneMappingMethod.DescriptorHash => 1.0,
            BoneMappingMethod.ExactName => 1.0,
            BoneMappingMethod.NormalizedName => 0.95,
            BoneMappingMethod.Semantic => 0.9,
            BoneMappingMethod.Structural => 0.7,
            BoneMappingMethod.Manual => 1.0,
            BoneMappingMethod.Composed => 0.75,
            BoneMappingMethod.Distributed => 0.75,
            _ => 0.0,
        };
}
