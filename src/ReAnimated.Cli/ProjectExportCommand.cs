using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
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
        ResolvedProjectAnimation resolvedAnimation = ResolveAnimation(
            project,
            args.Length >= 4 ? args[3] : null);
        ProjectAnimation animation = resolvedAnimation.Animation;
        ProjectAnimationSourceBinding sourceBinding =
            animation.SourceBinding ??
            throw new InvalidDataException(
                "The animation has no provable immutable source binding. Rebind it in the C# application before export.");
        Dl1AnimationExportParts parts = ResolveParts(
            args.Length == 5 ? args[4] : "body");
        ProjectAssetReference sourceAsset = resolvedAnimation.EmbeddedStack is null
            ? ResolveAsset(
                project,
                animation.SourceAssetId,
                RequiredAssetKind(sourceBinding.Kind))
            : ResolveAsset(
                project,
                animation.SourceAssetId,
                ProjectAssetKind.CustomModelSource);
        ProjectAssetReference targetAsset = ResolveModelAsset(
            project,
            animation.TargetAssetId
                ?? throw new InvalidOperationException(
                    "The animation has no saved target-model asset."));

        string actualInstallId =
            RetailAssetIdentity.CreateInstallId(installPath);
        string cacheDirectory =
            LocalApplicationPaths.CreateDefault()
                .RpackCacheDirectory;
        await using var cache = new Rp6lChunkCache(
            new Rp6lChunkCacheOptions
            {
                CacheDirectory = cacheDirectory,
            });
        string projectDirectory =
            Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException(
                "The project has no parent directory.");
        RigDefinition targetRig = await DecodeProjectModelRigAsync(
                targetAsset,
                resolvedAnimation.TargetModel,
                projectDirectory,
                installPath,
                actualInstallId,
                cache,
                cancellationToken)
            .ConfigureAwait(false);
        RigDefinition sourceRig;
        AnimationClip clip;
        if (sourceBinding.Kind == AnimationSourceKind.LocalFbx)
        {
            if (resolvedAnimation.EmbeddedStack is { } embeddedStack)
            {
                DecodedEmbeddedCustomModelSource decoded =
                    await DecodeEmbeddedCustomModelSourceAsync(
                            sourceAsset,
                            embeddedStack,
                            animation,
                            projectDirectory,
                            cancellationToken)
                        .ConfigureAwait(false);
                sourceRig = decoded.Rig;
                clip = decoded.Clip;
            }
            else
            {
                string sourcePath = ResolveContainedPath(
                    projectDirectory,
                    sourceAsset.RelativePath,
                    requireFile: true);
                await VerifyLocalAssetHashAsync(
                        sourceAsset,
                        sourcePath,
                        "authored FBX source",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        Path.GetExtension(sourcePath),
                        ".fbx",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "A local-FBX source binding does not refer to an FBX file.");
                }

                if (TryParseExternalFbxStackTimingDetail(
                        sourceBinding.TimingDetail,
                        out long stackObjectId,
                        out string stackFingerprint,
                        out FbxFacialSourceValueUnit facialSourceValueUnit,
                        out bool usesSelectedModelRig,
                        out Guid? selectedSourceModelId))
                {
                    ImmutableArray<FbxExternalAnimationImportResult> imported =
                        await FbxExternalAnimationImportService
                            .ImportFileSelectedAsync(
                                sourcePath,
                                [stackObjectId],
                                new FbxExternalAnimationImportOptions
                                {
                                    FacialSourceValueUnit =
                                        facialSourceValueUnit,
                                },
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    FbxExternalAnimationImportResult selected =
                        AssertSingleExternalStack(
                            imported,
                            stackObjectId,
                            stackFingerprint);
                    if (selected.Stack.Roles != sourceBinding.Roles)
                    {
                        throw new InvalidDataException(
                            "The selected external FBX stack roles differ from its immutable source binding.");
                    }

                    sourceRig = selected.SourceRig ??
                        (usesSelectedModelRig
                            ? selectedSourceModelId is null
                                ? targetRig
                                : await DecodeProjectModelByIdAsync(
                                        project,
                                        selectedSourceModelId,
                                        projectDirectory,
                                        installPath,
                                        actualInstallId,
                                        cache,
                                        cancellationToken)
                                    .ConfigureAwait(false)
                            : throw new InvalidDataException(
                                "The selected external FBX stack lost its embedded source rig."));
                    clip = selected.Clip;
                }
                else
                {
                    FbxCoreAnimationImportResult decoded =
                        await new FbxAnimationDecoder()
                            .DecodeFileAsync(
                                sourcePath,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    sourceRig = decoded.Rig;
                    clip = decoded.Clip;
                }
            }
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

            string profileId = animation.MimicProfileId ??
                throw new InvalidDataException(
                    "The saved facial FBX has no DL1 mimic profile.");
            Dl1MimicProfile profile =
                FbxFacialProjectReviewService
                    .IsTargetInventoryProfileId(profileId)
                    ? FbxFacialProjectReviewService
                        .CreateTargetInventoryProfile(targetRig)
                    : Dl1MimicProfileCodec.ReadBuiltInCommon46();
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
                    "The saved facial FBX mapping fingerprint no longer matches its exact target rig, body timing, and reviewed rows.");
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
                "The source or target-model rig signature differs from the saved variant contract.");
        }

        RetargetMap? mapping = null;
        DirectRigBinding? directBinding = null;
        string? mappingFingerprint = null;
        bool exactSameRig = string.Equals(
            sourceSignature,
            targetSignature,
            StringComparison.OrdinalIgnoreCase);
        switch (animation.BindingMode)
        {
            case ProjectAnimationBindingMode.ExactDirect:
                if (!exactSameRig || animation.DirectBinding is not null)
                {
                    throw new InvalidDataException(
                        "The exact-direct variant no longer uses one runtime rig identity.");
                }

                break;
            case ProjectAnimationBindingMode.CompatibleDirect:
                directBinding = animation.DirectBinding ??
                    throw new InvalidDataException(
                        "The compatible-direct variant has no persisted direct binding.");
                directBinding.ValidateFor(sourceRig, targetRig);
                if (!string.Equals(
                        directBinding.EvidenceFingerprint,
                        animation.BindingEvidenceFingerprint,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        directBinding.Policy,
                        animation.BindingPolicyVersion,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The compatible-direct binding evidence is stale.");
                }

                break;
            case ProjectAnimationBindingMode.Retarget:
                mapping = BuildMap(
                    sourceRig,
                    targetRig,
                    animation.BoneMappings,
                    animation.TargetBindReviews);
                mappingFingerprint =
                    RetargetMapFingerprint.Compute(
                        sourceSignature,
                        targetSignature,
                        targetAsset.ContentSha256 ??
                            throw new InvalidDataException(
                                "The target-model asset has no content fingerprint."),
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

                break;
            default:
                throw new InvalidDataException(
                    "The project contains an unsupported animation-binding mode.");
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
            rootMode,
            animation.RootBoneName,
            directRigBinding: directBinding);
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
                targetRig),
            directRigBinding: directBinding);
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

    private sealed record DecodedEmbeddedCustomModelSource(
        RigDefinition Rig,
        AnimationClip Clip);

    private sealed record ResolvedProjectAnimation(
        ProjectAnimation Animation,
        ProjectAnimationSource? Source,
        ProjectAnimationVariant? Variant,
        ProjectModelEntry? TargetModel,
        ProjectEmbeddedAnimationStackIdentity? EmbeddedStack);

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

    private static async Task<RigDefinition> DecodeProjectModelByIdAsync(
        DlraProject project,
        Guid? modelId,
        string projectDirectory,
        string installPath,
        string actualInstallId,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken)
    {
        ProjectModelEntry model = modelId is { } id
            ? project.Models.FirstOrDefault(candidate =>
                candidate.Id == id) ??
              throw new InvalidDataException(
                  "The external FBX source-model selection is no longer in the project model library.")
            : throw new InvalidDataException(
                "The external FBX source-model selection has no stable project-model ID.");
        ProjectAssetReference asset = ResolveModelAsset(
            project,
            model.AssetId);
        return await DecodeProjectModelRigAsync(
                asset,
                model,
                projectDirectory,
                installPath,
                actualInstallId,
                cache,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<RigDefinition> DecodeProjectModelRigAsync(
        ProjectAssetReference asset,
        ProjectModelEntry? model,
        string projectDirectory,
        string installPath,
        string actualInstallId,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken)
    {
        if (model is not null && model.AssetId != asset.Id)
        {
            throw new InvalidDataException(
                "The project-model entry disagrees with its model asset.");
        }

        if (asset.Kind == ProjectAssetKind.RetailGameResource)
        {
            RigDefinition rig = await DecodeRetailRigAsync(
                    asset,
                    installPath,
                    actualInstallId,
                    cache,
                    cancellationToken)
                .ConfigureAwait(false);
            if (model?.RigSignature is { } expectedSignature &&
                !string.Equals(
                    RigSignature.Compute(rig),
                    expectedSignature,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The decoded retail rig differs from its project-model contract.");
            }

            return rig;
        }

        if (asset.Kind != ProjectAssetKind.CustomModelSource)
        {
            throw new InvalidDataException(
                "A target or source-model binding must reference a retail or custom-model asset.");
        }

        string packagePath = ResolveContainedPath(
            projectDirectory,
            asset.RelativePath,
            requireFile: true);
        await VerifyLocalAssetHashAsync(
                asset,
                packagePath,
                "custom-model package",
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                Path.GetExtension(packagePath),
                ".dlrmodel",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A custom-model project asset does not refer to a .dlrmodel package.");
        }

        CustomModelPackage package = CustomModelPackageSerializer.Load(
            packagePath);
        Guid expectedModelId = ParseCustomModelResourceId(
            asset.ResourceId);
        if (package.Document.ModelId != expectedModelId)
        {
            throw new InvalidDataException(
                "The .dlrmodel identity differs from its project asset.");
        }

        FbxModelAuthoringImportResult decoded =
            FbxModelAuthoringImporter.ImportPackage(
                package,
                cancellationToken);
        if (model?.RigSignature is { } expectedContract &&
            !string.Equals(
                decoded.Package.Document.RigSignature,
                expectedContract,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The custom-model rig contract differs from its saved project-model entry.");
        }
        if (model?.MorphSignature is { } expectedMorphContract &&
            !string.Equals(
                decoded.Package.Document.MorphSignature,
                expectedMorphContract,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The custom-model morph contract differs from its saved project-model entry.");
        }

        if (decoded.Rig is null)
        {
            throw new InvalidDataException(
                "The custom-model target has no decodable rig.");
        }

        return decoded.Package.Document.CreateDl1AnimationRigDefinition();
    }

    private static async Task<DecodedEmbeddedCustomModelSource>
        DecodeEmbeddedCustomModelSourceAsync(
            ProjectAssetReference sourceAsset,
            ProjectEmbeddedAnimationStackIdentity embedded,
            ProjectAnimation animation,
            string projectDirectory,
            CancellationToken cancellationToken)
    {
        string packagePath = ResolveContainedPath(
            projectDirectory,
            sourceAsset.RelativePath,
            requireFile: true);
        await VerifyLocalAssetHashAsync(
                sourceAsset,
                packagePath,
                "embedded custom-model source",
                cancellationToken)
            .ConfigureAwait(false);
        CustomModelPackage package = CustomModelPackageSerializer.Load(
            packagePath);
        Guid expectedModelId = ParseCustomModelResourceId(
            sourceAsset.ResourceId);
        if (package.Document.ModelId != expectedModelId)
        {
            throw new InvalidDataException(
                "The embedded custom-model source identity differs from its project asset.");
        }

        CustomModelAnimationClip stack = package.Document.AnimationClips
            .FirstOrDefault(candidate => candidate.Id == embedded.ClipId) ??
            throw new InvalidDataException(
                "The selected embedded animation stack is missing from its .dlrmodel package.");
        AnimationSourceRoles actualRoles = ResolveEmbeddedRoles(stack);
        ProjectMorphSourceValueUnit actualFacialUnit =
            string.Equals(
                stack.FacialSourceValueUnit,
                "percent",
                StringComparison.Ordinal)
                ? ProjectMorphSourceValueUnit.Percent
                : ProjectMorphSourceValueUnit.Normalized;
        if (stack.FbxObjectId != embedded.FbxObjectId ||
            !string.Equals(
                stack.SourceFingerprint,
                embedded.StackFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            actualRoles != embedded.Roles ||
            (actualRoles & AnimationSourceRoles.Facial) != 0 &&
            (actualFacialUnit != embedded.FacialSourceValueUnit ||
             animation.FacialSourceValueUnit != actualFacialUnit))
        {
            throw new InvalidDataException(
                "The embedded custom-model stack object, fingerprint, roles, or facial unit differs from its immutable source identity.");
        }

        FbxModelAuthoringImportResult imported =
            FbxModelAuthoringImporter.ImportPackage(
                package,
                cancellationToken);
        if (imported.Rig is null)
        {
            throw new InvalidDataException(
                "The embedded custom-model animation source has no rig.");
        }

        RigDefinition rig = imported.Package.Document
            .CreateDl1AnimationRigDefinition();
        string rigSignature = RigSignature.Compute(rig);
        if (!string.Equals(
                rigSignature,
                embedded.SourceRigSignature,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                rigSignature,
                animation.SourceRigSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The embedded custom-model rig differs from its immutable source signature.");
        }
        if (!imported.AnimationClips.TryGetValue(
                embedded.ClipId,
                out AnimationClip? decodedClip))
        {
            throw new InvalidDataException(
                "The selected embedded custom-model stack can no longer be decoded.");
        }

        var clip = new AnimationClip(
            animation.Name,
            stack.FrameRate,
            decodedClip.FrameCount,
            decodedClip.TransformTracks,
            decodedClip.ScalarTracks,
            decodedClip.AuxiliaryTransformTracks);
        return new DecodedEmbeddedCustomModelSource(rig, clip);
    }

    private static AnimationSourceRoles ResolveEmbeddedRoles(
        CustomModelAnimationClip stack)
    {
        AnimationSourceRoles roles = AnimationSourceRoles.None;
        if (stack.HasSkeletalTracks)
        {
            roles |= AnimationSourceRoles.Body;
        }
        if (stack.HasMorphTracks)
        {
            roles |= AnimationSourceRoles.Facial;
        }
        return roles == AnimationSourceRoles.None
            ? AnimationSourceRoles.Auxiliary
            : roles;
    }

    private static Guid ParseCustomModelResourceId(string? resourceId)
    {
        string[] parts = resourceId?.Split(':') ?? [];
        if (parts.Length < 2 ||
            !string.Equals(
                parts[0],
                "custom-model",
                StringComparison.Ordinal) ||
            !Guid.TryParseExact(parts[1], "N", out Guid modelId) ||
            modelId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The custom-model project asset has no stable model identity.");
        }

        return modelId;
    }

    private static async Task VerifyLocalAssetHashAsync(
        ProjectAssetReference asset,
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        string expected = asset.ContentSha256 ??
            throw new InvalidDataException(
                $"The saved {description} has no SHA-256 fingerprint.");
        string actual = await ComputeSha256Async(
                path,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                actual,
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The saved {description} differs from its immutable project fingerprint.");
        }
    }

    private static bool TryParseExternalFbxStackTimingDetail(
        string? detail,
        out long stackObjectId,
        out string stackFingerprint,
        out FbxFacialSourceValueUnit sourceValueUnit,
        out bool usesSelectedModelRig,
        out Guid? selectedSourceModelId)
    {
        stackObjectId = 0;
        stackFingerprint = string.Empty;
        sourceValueUnit = FbxFacialSourceValueUnit.Unspecified;
        usesSelectedModelRig = false;
        selectedSourceModelId = null;
        string[] parts = detail?.Split('|') ?? [];
        bool versionOne = parts.Length == 7 &&
            string.Equals(
                parts[0],
                "external-fbx-stack-v1",
                StringComparison.Ordinal);
        bool versionTwo = parts.Length == 8 &&
            string.Equals(
                parts[0],
                "external-fbx-stack-v2",
                StringComparison.Ordinal);
        if ((!versionOne && !versionTwo) ||
            !long.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out stackObjectId) ||
            stackObjectId <= 0 ||
            parts[2].Length != 64 ||
            parts[2].Any(static value => !Uri.IsHexDigit(value)) ||
            !long.TryParse(
                parts[4],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _) ||
            !long.TryParse(
                parts[5],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _))
        {
            return false;
        }

        sourceValueUnit = parts[3] switch
        {
            "percent" => FbxFacialSourceValueUnit.Percent,
            "normalized" => FbxFacialSourceValueUnit.Normalized,
            _ => FbxFacialSourceValueUnit.Unspecified,
        };
        if (sourceValueUnit == FbxFacialSourceValueUnit.Unspecified ||
            parts[6] is not ("embedded-rig" or "selected-model-rig"))
        {
            sourceValueUnit = FbxFacialSourceValueUnit.Unspecified;
            return false;
        }

        stackFingerprint = parts[2].ToLowerInvariant();
        usesSelectedModelRig = parts[6] == "selected-model-rig";
        if (versionTwo &&
            (!usesSelectedModelRig ||
             !Guid.TryParseExact(
                 parts[7],
                 "N",
                 out Guid modelId) ||
             modelId == Guid.Empty))
        {
            stackObjectId = 0;
            stackFingerprint = string.Empty;
            sourceValueUnit = FbxFacialSourceValueUnit.Unspecified;
            usesSelectedModelRig = false;
            return false;
        }

        selectedSourceModelId = versionTwo
            ? Guid.ParseExact(parts[7], "N")
            : null;
        return true;
    }

    private static FbxExternalAnimationImportResult AssertSingleExternalStack(
        ImmutableArray<FbxExternalAnimationImportResult> imported,
        long expectedObjectId,
        string expectedFingerprint)
    {
        if (imported.Length != 1 ||
            imported[0].Stack.StackObjectId != expectedObjectId ||
            !string.Equals(
                imported[0].Stack.StackFingerprint,
                expectedFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The external FBX stack object identity or fingerprint changed since import.");
        }

        return imported[0];
    }

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

        ProjectAssetReference sourceModelAsset = ResolveModelAsset(
            project,
            binding.RetailSourceModelAssetId ??
                throw new InvalidDataException(
                    "The ANM2 source has no exact source-model binding."));
        RigDefinition sourceRig;
        if (sourceModelAsset.Id == decodedTargetAsset.Id)
        {
            sourceRig = decodedTargetRig;
        }
        else
        {
            ProjectModelEntry? sourceModel = project.Models
                .FirstOrDefault(model =>
                    model.AssetId == sourceModelAsset.Id);
            sourceRig = await DecodeProjectModelRigAsync(
                    sourceModelAsset,
                    sourceModel,
                    projectDirectory,
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
                "The exact ANM2 source-model rig differs from the immutable saved signature.");
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
                    row.Confidence,
                    row.IsLocked,
                    row.IsReviewed,
                    row.MappingKind,
                    row.TransferPolicy,
                    row.ComponentPolicy,
                    ParsePersistedEvidence(row.Evidence),
                    ToRuntimeReviewOrigin(row.ReviewOrigin),
                    row.ScorerVersion,
                    row.EvidenceFingerprint,
                    row.EffectiveTransformComponents);
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

    private static IEnumerable<MappingEvidence> ParsePersistedEvidence(
        string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return [];
        }

        return evidence
            .Split(" | ", StringSplitOptions.TrimEntries |
                          StringSplitOptions.RemoveEmptyEntries)
            .Select(static row =>
            {
                int separator = row.IndexOf(": ", StringComparison.Ordinal);
                if (separator > 0 &&
                    Enum.TryParse(
                        row.AsSpan(0, separator),
                        ignoreCase: false,
                        out MappingEvidenceKind kind))
                {
                    return new MappingEvidence(
                        kind,
                        row[(separator + 2)..]);
                }

                return new MappingEvidence(
                    MappingEvidenceKind.ManualSelection,
                    row);
            });
    }

    private static MappingReviewOrigin ToRuntimeReviewOrigin(
        ProjectMappingReviewOrigin origin) =>
        origin switch
        {
            ProjectMappingReviewOrigin.None =>
                MappingReviewOrigin.None,
            ProjectMappingReviewOrigin.Explicit =>
                MappingReviewOrigin.Explicit,
            ProjectMappingReviewOrigin.Assisted =>
                MappingReviewOrigin.Assisted,
            _ => throw new InvalidDataException(
                "A saved mapping contains an unsupported review origin."),
        };

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

    private static ResolvedProjectAnimation ResolveAnimation(
        DlraProject project,
        string? selector)
    {
        if (!project.AnimationVariants.IsEmpty)
        {
            ProjectAnimationVariant variant = ResolveSchema2Variant(
                project,
                selector);
            ProjectAnimationSource source = project.AnimationSources
                .FirstOrDefault(candidate =>
                    candidate.Id == variant.SourceId) ??
                throw new InvalidDataException(
                    $"Animation variant '{variant.Name}' refers to a missing immutable source.");
            ProjectModelEntry targetModel = project.Models
                .FirstOrDefault(candidate =>
                    candidate.Id == variant.TargetModelId) ??
                throw new InvalidDataException(
                    $"Animation variant '{variant.Name}' refers to a missing target model.");
            if (source.RequiresSourceRebind)
            {
                throw new InvalidDataException(
                    $"Animation source '{source.Name}' still requires an explicit source-rig rebind.");
            }

            return new ResolvedProjectAnimation(
                CreateSchema2RuntimeAnimation(
                    source,
                    variant,
                    targetModel),
                source,
                variant,
                targetModel,
                source.EmbeddedCustomModelStack);
        }

        if (project.Animations.IsEmpty)
        {
            throw new InvalidDataException(
                "The project contains no animation variants.");
        }
        if (string.IsNullOrWhiteSpace(selector))
        {
            ProjectAnimation selected =
                project.ActiveAnimationId is { } activeId
                    ? project.Animations.FirstOrDefault(animation =>
                        animation.Id == activeId) ??
                      project.Animations[0]
                    : project.Animations[0];
            return new ResolvedProjectAnimation(
                selected,
                null,
                null,
                null,
                null);
        }

        ProjectAnimation[] matches = Guid.TryParse(
                selector,
                out Guid id)
            ? project.Animations.Where(animation =>
                animation.Id == id).ToArray()
            : project.Animations.Where(animation =>
                string.Equals(
                    animation.Name,
                    selector,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                matches.Length == 0
                    ? $"Project animation '{selector}' was not found."
                    : $"Project animation selector '{selector}' is ambiguous; use its variant ID.");
        }

        return new ResolvedProjectAnimation(
            matches[0],
            null,
            null,
            null,
            null);
    }

    private static ProjectAnimationVariant ResolveSchema2Variant(
        DlraProject project,
        string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            Guid? preferred =
                project.Workflow.SelectedAnimationVariantId ??
                project.ActiveAnimationId;
            return preferred is { } preferredId
                ? project.AnimationVariants.FirstOrDefault(variant =>
                    variant.Id == preferredId) ??
                  project.AnimationVariants[0]
                : project.AnimationVariants[0];
        }

        ProjectAnimationVariant[] direct;
        if (Guid.TryParse(selector, out Guid id))
        {
            direct = project.AnimationVariants
                .Where(variant => variant.Id == id)
                .ToArray();
            if (direct.Length == 0)
            {
                ProjectAnimationSource? bySourceId =
                    project.AnimationSources.FirstOrDefault(source =>
                        source.Id == id);
                direct = bySourceId is null
                    ? []
                    : project.AnimationVariants.Where(variant =>
                        variant.SourceId == bySourceId.Id).ToArray();
            }
        }
        else
        {
            direct = project.AnimationVariants
                .Where(variant => string.Equals(
                    variant.Name,
                    selector,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (direct.Length == 0)
            {
                Guid[] sourceIds = project.AnimationSources
                    .Where(source => string.Equals(
                        source.Name,
                        selector,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(static source => source.Id)
                    .ToArray();
                direct = project.AnimationVariants
                    .Where(variant => sourceIds.Contains(variant.SourceId))
                    .ToArray();
            }
        }

        return direct.Length switch
        {
            1 => direct[0],
            0 => throw new InvalidDataException(
                $"Project animation variant or source '{selector}' was not found."),
            _ => throw new InvalidDataException(
                $"Project selector '{selector}' matches multiple target variants; use a variant ID."),
        };
    }

    private static ProjectAnimation CreateSchema2RuntimeAnimation(
        ProjectAnimationSource source,
        ProjectAnimationVariant variant,
        ProjectModelEntry targetModel)
    {
        ProjectAnimationSourceBinding binding =
            source.SourceBinding ??
            (source.EmbeddedCustomModelStack is { } embedded
                ? new ProjectAnimationSourceBinding
                {
                    Kind = AnimationSourceKind.LocalFbx,
                    AssetId = source.SourceAssetId,
                    Roles = embedded.Roles,
                    SourceRigSignature =
                        embedded.SourceRigSignature,
                    TimingProvenance =
                        AnimationTimingProvenance.EmbeddedFbx,
                    SourceRangeStartFrame = 0,
                    SourceRangeEndFrame = source.FrameCount - 1,
                    TimingDetail =
                        $"Embedded custom-model stack {embedded.FbxObjectId}",
                }
                : throw new InvalidDataException(
                    $"Animation source '{source.Name}' has no immutable runtime binding."));
        ProjectMorphSourceValueUnit? facialUnit =
            source.FacialSourceValueUnit ??
            (source.EmbeddedCustomModelStack is { } facialStack &&
             (facialStack.Roles & AnimationSourceRoles.Facial) != 0
                ? facialStack.FacialSourceValueUnit
                : null);
        return new ProjectAnimation
        {
            Id = variant.Id,
            VariantGroupId = source.Id,
            Name = variant.Name,
            SourceAssetId = source.SourceAssetId,
            SourceBinding = binding,
            MimicAssetId = source.MimicAssetId,
            FacialAnimationSourceBinding =
                source.FacialAnimationSourceBinding,
            FacialSourceAssetId = source.FacialSourceAssetId,
            FacialSourceValueUnit = facialUnit,
            FacialTiming = source.FacialTiming,
            TargetAssetId = targetModel.AssetId,
            TargetRigId = variant.TargetRigId,
            SourceRigSignature = source.SourceRigSignature,
            TargetRigSignature = variant.TargetRigSignature,
            MappingFingerprint = variant.MappingFingerprint,
            MimicProfileId = variant.MimicProfileId,
            MimicMappingFingerprint =
                variant.MimicMappingFingerprint,
            FrameRate = source.FrameRate,
            FrameCount = source.FrameCount,
            RootMotionMode = variant.RootMotionMode,
            RootBoneName = variant.RootBoneName,
            PreviewMotionAccumulationEnabled =
                variant.PreviewMotionAccumulationEnabled,
            BoneMappings = variant.BoneMappings,
            TargetBindReviews = variant.TargetBindReviews,
            EditLayers = variant.EditLayers,
            MorphBindings = variant.MorphBindings,
            MorphEditLayers = variant.MorphEditLayers,
            IkLayers = variant.IkLayers,
            Attachments = variant.Attachments,
        };
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

    private static ProjectAssetReference ResolveModelAsset(
        DlraProject project,
        Guid id)
    {
        ProjectAssetReference? asset = project.Assets
            .FirstOrDefault(candidate => candidate.Id == id);
        if (asset is null ||
            asset.Kind is not (
                ProjectAssetKind.RetailGameResource or
                ProjectAssetKind.CustomModelSource))
        {
            throw new InvalidDataException(
                $"Project model asset '{id}' is missing or has the wrong kind.");
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

}
