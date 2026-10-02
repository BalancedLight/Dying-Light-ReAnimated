using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Cli;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Providers;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class GuidedPreviewReviewPersistenceTests : IDisposable
{
    private const int ExtraBoneCount = 8;
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(), $"GuidedReviewPersistence-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public async Task AllIndividualBindReviewsPersistAndApplyFinalPlayback(
        bool saveModelsWorkspace,
        bool bulkPreserve,
        bool pendingModelPreview)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "workspace.json")),
            new NoDialogs(),
            assets);

        FbxModelAuthoringImportResult imported =
            CustomModelPreviewSessionTests.CreateModel(
                flipTextureCoordinateV: false);
        ImmutableArray<CustomModelBone> bones = CreateTargetBones();
        CustomModelDocument document = imported.Package.Document with
        {
            Bones = bones,
            RigSignature = CustomModelContractSignatures.ComputeRig(bones),
            Source = imported.Package.Document.Source with
            {
                ContentSha256 = Convert.ToHexStringLower(
                    SHA256.HashData(imported.Package.SourceFbx.AsSpan())),
            },
        };
        RigDefinition targetRig = document.CreateRigDefinition();
        imported = imported with
        {
            Package = imported.Package with { Document = document },
            Rig = targetRig,
        };
        RigDefinition sourceRig = CreateSourceRig();
        AnimationClip clip = CreateMovingClip(sourceRig);
        RetargetMap mapping = new(
            sourceRig.Id,
            targetRig.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1.0),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.ExactName,
                    1.0),
            ]);
        Assert.NotEqual(
            sourceRig.Bones[1].LocalBindPose,
            targetRig.Bones[1].LocalBindPose);
        ImmutableArray<ProjectBoneMapping> mappedRows =
            mapping.Entries.Select(entry => new ProjectBoneMapping
            {
                SourceBoneName = sourceRig.Bones[entry.SourceBoneIndex].Name,
                TargetBoneName = targetRig.Bones[entry.TargetBoneIndex].Name,
                Confidence = entry.Confidence,
                Method = entry.Method.ToString(),
                Evidence = "No mapping evidence recorded.",
                ScorerVersion = "unscored-v1",
                EvidenceFingerprint = new string('0', 64),
                MappingKind = entry.MappingKind,
                TransferPolicy = entry.TransferPolicy,
                ComponentPolicy = entry.ComponentPolicy,
                TransformComponents = entry.TransformComponents,
            }).ToImmutableArray();
        Guid sourceAssetId = Guid.NewGuid();
        Guid targetAssetId = Guid.NewGuid();
        Guid targetModelId = Guid.NewGuid();
        ProjectAssetReference sourceAsset = new()
        {
            Id = sourceAssetId,
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "Sources/synthetic-motion.anm2",
            ContentSha256 = new string('a', 64),
        };
        ProjectAssetReference targetAsset = new()
        {
            Id = targetAssetId,
            Kind = ProjectAssetKind.CustomModelSource,
            RelativePath = "Sources/synthetic-target.dlrmodel",
            ResourceId = $"custom-model:{document.ModelId:N}:synthetic-target",
            ContentSha256 = new string('b', 64),
        };
        ProjectModelEntry targetModel = new()
        {
            Id = targetModelId,
            AssetId = targetAssetId,
            Name = "Synthetic target",
            RigSignature = RigSignature.Compute(targetRig),
            AuthoringRigContractSignature = document.RigSignature,
            AnimationSkeletonSignature = AnimationSkeletonSignature.Compute(targetRig),
        };
        ProjectAnimation animation = new()
        {
            Id = Guid.NewGuid(),
            Name = clip.Name,
            SourceAssetId = sourceAssetId,
            SourceBinding = new ProjectAnimationSourceBinding
            {
                Kind = AnimationSourceKind.LocalFbx,
                AssetId = sourceAssetId,
                SourceRigSignature = RigSignature.Compute(sourceRig),
                Roles = AnimationSourceRoles.Body,
            },
            TargetAssetId = targetAssetId,
            TargetRigId = targetRig.Id,
            SourceRigSignature = RigSignature.Compute(sourceRig),
            TargetRigSignature = RigSignature.Compute(targetRig),
            SourceAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(sourceRig),
            TargetAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(targetRig),
            BindingMode = ProjectAnimationBindingMode.Retarget,
            FrameRate = clip.FrameRate,
            FrameCount = clip.FrameCount,
            BoneMappings = mappedRows,
        };
        Guid completedAnimationId = Guid.NewGuid();
        ImmutableArray<ProjectBoneMapping> priorReviewedMappings =
            mappedRows.Select(row => row with
            {
                Evidence = "Prior explicit mapping review.",
                IsLocked = true,
                IsReviewed = true,
                ReviewOrigin = ProjectMappingReviewOrigin.Explicit,
                ScorerVersion = "synthetic-explicit-review-v1",
                EvidenceFingerprint = new string('c', 64),
            }).ToImmutableArray();
        ImmutableArray<ProjectTargetBindReview> priorBindReviews =
            Enumerable.Range(2, ExtraBoneCount)
                .Select(index => new ProjectTargetBindReview
                {
                    TargetBoneIndex = index,
                    TargetBoneName = targetRig.Bones[index].Name,
                })
                .ToImmutableArray();
        RetargetMap priorReviewedMap = ProjectExportCommand.BuildMap(
            sourceRig,
            targetRig,
            priorReviewedMappings,
            priorBindReviews);
        string priorMappingFingerprint = RetargetMapFingerprint.Compute(
            RigSignature.Compute(sourceRig),
            RigSignature.Compute(targetRig),
            targetAsset.ContentSha256,
            priorReviewedMap);
        ProjectAnimation completedAnimation = animation with
        {
            Id = completedAnimationId,
            BoneMappings = priorReviewedMappings,
            TargetBindReviews = priorBindReviews,
            MappingFingerprint = priorMappingFingerprint,
        };
        DlraProject project = DlraProject.Create("Guided review persistence") with
        {
            Assets = [sourceAsset, targetAsset],
            Models = [targetModel],
            ModelsWorkspace = new ProjectModelsWorkspaceState
            {
                PackageAssetId = targetAssetId,
            },
            Animations = [completedAnimation, animation],
            ActiveAnimationId = animation.Id,
        };

        object sourceSession = CreatePrivateRecord(
            "ImportedAnimationSession",
            sourceRig,
            clip,
            "synthetic://motion",
            "Synthetic");
        CustomModelPreviewSession previewSession =
            CustomModelPreviewAdapter.CreateSession(
                imported,
                CustomModelPreviewMode.SourceFbx);
        object customTarget = CreatePrivateRecord(
            "PreparedCustomTarget",
            targetRig,
            Array.Empty<MeshRenderData>(),
            CorePreviewAdapter.ToRenderSkeleton(targetRig.CreateBindPose()),
            targetAsset,
            previewSession,
            CustomModelPreviewMode.SourceFbx,
            CustomModelPreviewMode.SourceFbx,
            ImmutableArray<string>.Empty);
        SetField(viewModel, "_animationTransitionGeneration", 1L);
        SetField(viewModel, "_hasGuidedPreviewReviewDraft", true);
        SetProperty(viewModel, nameof(MainWindowViewModel.IsBusy), true);
        object transition = CreatePrivateRecord(
            "PreparedAnimationTransition",
            1L,
            animation,
            sourceSession,
            Array.Empty<MeshRenderData>(),
            null,
            null,
            customTarget,
            null,
            clip,
            mapping,
            TargetBindingStatus.NeedsReview,
            null);
        typeof(MainWindowViewModel).GetMethod(
            "CommitPreparedAnimationTransition",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [transition, project, false, true, false, false]);
        SetField(
            viewModel,
            "_guidedPreviewDraftIdentity",
            CreatePrivateRecord(
                "GuidedPreviewDraftIdentity",
                animation.Id,
                animation.SourceAssetId,
                sourceAsset.ContentSha256!,
                targetAssetId,
                targetAsset.ContentSha256!,
                RigSignature.Compute(sourceRig),
                RigSignature.Compute(targetRig),
                clip));
        SetProperty(viewModel, nameof(MainWindowViewModel.IsBusy), false);
        typeof(MainWindowViewModel).GetMethod(
            "NotifyMappingCommands",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, null);

        Assert.True(viewModel.HasGuidedPreviewReviewDraft);
        Assert.Equal(ExtraBoneCount, viewModel.RequiredTargetBindReviews.Count);
        Assert.All(
            viewModel.RequiredTargetBindReviews,
            static row => Assert.True(row.CanPreserveWithParent));
        Assert.True(viewModel.ApplyGuidedPreviewReviewCommand.CanExecute(null));
        viewModel.Models.SetGuidedPreviewReviewContext(viewModel);
        SetProperty(viewModel.Models, "GuidedStep", GuidedModelSetupStep.Preview);
        string[] targetBindNames = viewModel.RequiredTargetBindReviews
            .Select(static row => row.TargetBone)
            .ToArray();
        Assert.Equal(ExtraBoneCount, targetBindNames.Distinct().Count());

        int partialChoiceCount = bulkPreserve ? 0 : ExtraBoneCount / 2;
        for (int index = 0; index < partialChoiceCount; index++)
        {
            TargetBindReviewViewModel current =
                Assert.Single(viewModel.RequiredTargetBindReviews,
                    row => string.Equals(
                        row.TargetBone,
                        targetBindNames[index],
                        StringComparison.Ordinal));
            current.IsReviewed = true;

            Assert.True(viewModel.HasGuidedPreviewReviewDraft);
            Assert.Equal(index + 1, viewModel.RequiredTargetBindReviews.Count(
                static row => row.IsReviewed));
            Assert.True(viewModel.ApplyGuidedPreviewReviewCommand.CanExecute(null));
        }

        Assert.Equal("Keep extra bones and play", viewModel.Models.GuidedPrimaryLabel);
        int originalAnimationCount = GetProject(viewModel).Animations.Length;
        int originalSourceCount = GetProject(viewModel).AnimationSources.Length;
        int originalVariantCount = GetProject(viewModel).AnimationVariants.Length;
        Guid originalAnimationId = GetActiveProjectAnimation(viewModel).Id;
        Assert.Equal(
            ExtraBoneCount,
            GetProject(viewModel).Animations.Single(row =>
                row.Id == completedAnimationId).TargetBindReviews.Length);

        // Keep the partial-review assertion on the explicit advanced command.
        viewModel.ApplyGuidedPreviewReviewCommand.Execute(null);
        Assert.True(viewModel.HasGuidedPreviewReviewDraft);
        Assert.Contains("Review remains", viewModel.Models.GuidedStatus);
        Assert.Equal(partialChoiceCount, viewModel.RequiredTargetBindReviews.Count(
            static row => row.IsReviewed));
        Assert.Contains("Review remains", viewModel.Models.GuidedStatus);
        Assert.Equal(originalAnimationCount, GetProject(viewModel).Animations.Length);
        Assert.Equal(originalSourceCount, GetProject(viewModel).AnimationSources.Length);
        Assert.Equal(originalVariantCount, GetProject(viewModel).AnimationVariants.Length);
        Assert.Equal(originalAnimationId, GetActiveProjectAnimation(viewModel).Id);
        Assert.Equal(
            ExtraBoneCount,
            GetProject(viewModel).Animations.Single(row =>
                row.Id == completedAnimationId).TargetBindReviews.Length);

        if (bulkPreserve)
        {
            Assert.Equal(0, viewModel.RequiredTargetBindReviews.Count(
                static row => row.IsReviewed));
            viewModel.Models.SetGuidedPreviewHandler(
                static () => Task.FromResult(false));
            if (pendingModelPreview)
            {
                TaskCompletionSource<object?> previewGate =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                SetField(viewModel, "_automaticAssetPreviewTask", previewGate.Task);
                SetProperty(viewModel, nameof(MainWindowViewModel.IsBusy), true);
                Task pendingPrimary =
                    viewModel.Models.GuidedPrimaryCommand.ExecuteAsync(null);
                await Task.Yield();
                Assert.False(pendingPrimary.IsCompleted);
                Assert.Equal(0, viewModel.RequiredTargetBindReviews.Count(
                    static row => row.IsReviewed));
                SetProperty(viewModel, nameof(MainWindowViewModel.IsBusy), false);
                previewGate.SetResult(null);
                await pendingPrimary;
            }
            else
            {
                await viewModel.Models.GuidedPrimaryCommand.ExecuteAsync(null);
            }
            Assert.False(viewModel.HasGuidedPreviewReviewDraft);
            Assert.True(viewModel.Timeline.IsPlaying);
            Assert.NotNull(GetPublishedFrameFor(viewModel, originalAnimationId));
        }

        for (int index = bulkPreserve ? targetBindNames.Length : partialChoiceCount;
             index < targetBindNames.Length;
             index++)
        {
            TargetBindReviewViewModel current =
                Assert.Single(viewModel.RequiredTargetBindReviews,
                    row => string.Equals(
                        row.TargetBone,
                        targetBindNames[index],
                        StringComparison.Ordinal));
            current.IsReviewed = true;

            Assert.True(viewModel.HasGuidedPreviewReviewDraft);
            Assert.Equal(index + 1, viewModel.RequiredTargetBindReviews.Count(
                static row => row.IsReviewed));
        }

        if (bulkPreserve)
        {
            Assert.Equal("Build model package…", viewModel.Models.GuidedPrimaryLabel);
        }
        else
        {
            Assert.Equal("Use reviewed choices and play", viewModel.Models.GuidedPrimaryLabel);
        }
        ProjectAnimation savedBeforeApply = GetActiveProjectAnimation(viewModel);
        Assert.Equal(
            ExtraBoneCount,
            savedBeforeApply.TargetBindReviews.Length);
        Assert.Equal(
            targetBindNames.Order(StringComparer.Ordinal),
            savedBeforeApply.TargetBindReviews
                .Select(static row => row.TargetBoneName)
                .Order(StringComparer.Ordinal));
        RetargetMap reviewedMap = new(
            sourceRig.Id,
            targetRig.Id,
            mapping.Entries,
            targetBindNames.Select(targetRig.GetBoneIndex));
        Assert.True(RetargetMappingReview.Analyze(
            sourceRig,
            targetRig,
            reviewedMap).IsReady);

        if (!bulkPreserve)
        {
            await viewModel.Models.GuidedPrimaryCommand.ExecuteAsync(null);
        }
        Assert.False(viewModel.HasGuidedPreviewReviewDraft);
        Assert.True(viewModel.Models.IsGuidedExport);
        Assert.True(viewModel.Timeline.IsPlaying);
        Assert.Equal(TargetBindingStatus.Ready, viewModel.ActiveTargetBindingStatus);
        Assert.Equal(originalAnimationId, GetActiveProjectAnimation(viewModel).Id);
        Assert.Equal(originalAnimationCount, GetProject(viewModel).Animations.Length);
        Assert.Equal(originalSourceCount, GetProject(viewModel).AnimationSources.Length);
        Assert.Equal(originalVariantCount, GetProject(viewModel).AnimationVariants.Length);
        Assert.NotNull(GetPublishedFrameFor(viewModel, originalAnimationId));
        Assert.Equal(
            ExtraBoneCount,
            GetProject(viewModel).Animations.Single(row =>
                row.Id == completedAnimationId).TargetBindReviews.Length);

        ProjectAnimation beforeSerialization =
            GetActiveProjectAnimation(viewModel);
        ProjectAssetReference targetBeforeSerialization =
            GetProject(viewModel).Assets.Single(
                asset => asset.Id == beforeSerialization.TargetAssetId);
        Assert.Equal(
            RigSignature.Compute(sourceRig),
            beforeSerialization.SourceRigSignature);
        Assert.Equal(
            RigSignature.Compute(targetRig),
            beforeSerialization.TargetRigSignature);
        Assert.False(string.IsNullOrWhiteSpace(
            targetBeforeSerialization.ContentSha256));
        Assert.Equal(
            targetAsset.ContentSha256,
            targetBeforeSerialization.ContentSha256);
        RetargetMap appCanonicalMap = BuildPersistedReviewMap(
            sourceRig,
            targetRig,
            beforeSerialization.BoneMappings,
            beforeSerialization.TargetBindReviews);
        RetargetMap cliRowsBeforeSerialization =
            ProjectExportCommand.BuildMap(
                sourceRig,
                targetRig,
                beforeSerialization.BoneMappings,
                beforeSerialization.TargetBindReviews);
        Assert.True(
            cliRowsBeforeSerialization.ReviewedTargetBindBoneIndices
                .SequenceEqual(
                    appCanonicalMap.ReviewedTargetBindBoneIndices));
        string cliCanonicalFingerprint = RetargetMapFingerprint.Compute(
            RigSignature.Compute(sourceRig),
            RigSignature.Compute(targetRig),
            targetBeforeSerialization.ContentSha256,
            cliRowsBeforeSerialization);
        string appCanonicalFingerprint = RetargetMapFingerprint.Compute(
            RigSignature.Compute(sourceRig),
            RigSignature.Compute(targetRig),
            targetBeforeSerialization.ContentSha256,
            appCanonicalMap);
        Assert.Equal(cliCanonicalFingerprint, appCanonicalFingerprint);
        Assert.Equal(
            beforeSerialization.MappingFingerprint,
            appCanonicalFingerprint);

        DlraProject reviewedProject = GetProject(viewModel);
        DlraProject changedPackage = reviewedProject with
        {
            Assets = reviewedProject.Assets.Select(asset => asset.Id == targetAssetId
                ? asset with { ContentSha256 = new string('d', 64) }
                : asset).ToImmutableArray(),
        };
        DlraProject staleReview = reviewedProject with
        {
            Animations = reviewedProject.Animations.Select(row => row.Id == originalAnimationId
                ? row with { MappingFingerprint = new string('f', 64) }
                : row).ToImmutableArray(),
        };
        Assert.Same(changedPackage,
            MainWindowViewModel.ReconcileReviewedMappingAfterTargetPersistence(
                staleReview, changedPackage, originalAnimationId, sourceRig, targetRig));
        DlraProject changedRows = changedPackage with
        {
            Animations = changedPackage.Animations.Select(row => row.Id == originalAnimationId
                ? row with
                {
                    BoneMappings = row.BoneMappings.SetItem(0,
                        row.BoneMappings[0] with { Evidence = "Different mapping provenance." }),
                }
                : row).ToImmutableArray(),
        };
        Assert.Same(changedRows,
            MainWindowViewModel.ReconcileReviewedMappingAfterTargetPersistence(
                reviewedProject, changedRows, originalAnimationId, sourceRig, targetRig));
        DlraProject changedContract = changedPackage with
        {
            Models = changedPackage.Models.Select(model => model.Id == targetModelId
                ? model with { AuthoringRigContractSignature = new string('e', 64) }
                : model).ToImmutableArray(),
        };
        Assert.Same(changedContract,
            MainWindowViewModel.ReconcileReviewedMappingAfterTargetPersistence(
                reviewedProject, changedContract, originalAnimationId, sourceRig, targetRig));
        DlraProject changedSource = changedPackage with
        {
            Assets = changedPackage.Assets.Select(asset => asset.Id == sourceAssetId
                ? asset with { ContentSha256 = new string('e', 64) }
                : asset).ToImmutableArray(),
        };
        Assert.Same(changedSource,
            MainWindowViewModel.ReconcileReviewedMappingAfterTargetPersistence(
                reviewedProject, changedSource, originalAnimationId, sourceRig, targetRig));

        string savedPath = Path.Combine(
            _temporaryDirectory, "reviewed-project.dlraproj");
        if (saveModelsWorkspace)
        {
            SetField(viewModel.Models, "_model", imported);
            SetProperty(viewModel, nameof(MainWindowViewModel.ProjectPath), savedPath);
            await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
            Assert.True(File.Exists(savedPath), viewModel.StatusText);
            // The save rebases the pending package identity. Export controls must
            // describe that final project without requiring another UI mutation.
            var savedExport = Assert.Single(viewModel.ExportVariants,
                row => row.AnimationId == originalAnimationId);
            Assert.Equal(viewModel.CurrentProject.Models.Single(model => model.Id == targetModelId).Name,
                savedExport.TargetModel);
            Assert.DoesNotContain("Target fingerprint or rig signature is stale", savedExport.Readiness,
                StringComparison.Ordinal);
        }
        else
        {
            savedPath = ProjectSerializer.SaveAtomic(GetProject(viewModel), savedPath);
        }
        DlraProject reloaded = ProjectSerializer.Load(savedPath);
        ProjectAnimation reloadedAnimation = reloaded.Animations.Single(
            row => row.Id == originalAnimationId);
        ProjectAnimation reloadedCompletedAnimation =
            reloaded.Animations.Single(
                row => row.Id == completedAnimationId);
        ProjectAnimationVariant reloadedVariant =
            reloaded.AnimationVariants.Single(
                row => row.Id == originalAnimationId);
        ProjectAssetReference reloadedTarget = reloaded.Assets.Single(
            asset => asset.Id == reloadedAnimation.TargetAssetId);
        Assert.Equal(
            RigSignature.Compute(sourceRig),
            reloadedAnimation.SourceRigSignature);
        Assert.Equal(
            RigSignature.Compute(targetRig),
            reloadedAnimation.TargetRigSignature);
        if (saveModelsWorkspace)
        {
            Assert.NotEqual(
                targetBeforeSerialization.ContentSha256,
                reloadedTarget.ContentSha256);
        }
        else
        {
            Assert.Equal(
                targetBeforeSerialization.ContentSha256,
                reloadedTarget.ContentSha256);
        }
        Assert.Equal(
            reloadedAnimation.MappingFingerprint,
            reloadedVariant.MappingFingerprint);
        Assert.Equal(
            priorMappingFingerprint,
            reloadedCompletedAnimation.MappingFingerprint);
        Assert.NotEqual(
            reloadedAnimation.MappingFingerprint,
            reloadedCompletedAnimation.MappingFingerprint);
        Assert.Equal(
            ProjectSerializer.NormalizeMappingProvenanceForPersistence(
                completedAnimation.BoneMappings).ToArray(),
            reloadedCompletedAnimation.BoneMappings.ToArray());
        Assert.Equal(
            completedAnimation.TargetBindReviews.ToArray(),
            reloadedCompletedAnimation.TargetBindReviews.ToArray());
        Assert.Equal(
            ExtraBoneCount,
            reloadedCompletedAnimation.TargetBindReviews.Length);
        RetargetMap exportMap = ProjectExportCommand.BuildMap(
            sourceRig,
            targetRig,
            reloadedAnimation.BoneMappings,
            reloadedAnimation.TargetBindReviews);
        RetargetMap appCanonicalReloadedMap = BuildPersistedReviewMap(
            sourceRig,
            targetRig,
            reloadedAnimation.BoneMappings,
            reloadedAnimation.TargetBindReviews);
        string exportFingerprint = RetargetMapFingerprint.Compute(
            RigSignature.Compute(sourceRig),
            RigSignature.Compute(targetRig),
            reloadedTarget.ContentSha256,
            exportMap);
        Assert.Equal(
            exportFingerprint,
            RetargetMapFingerprint.Compute(
                RigSignature.Compute(sourceRig),
                RigSignature.Compute(targetRig),
                reloadedTarget.ContentSha256,
                appCanonicalReloadedMap));
        Assert.Equal(
            reloadedAnimation.MappingFingerprint,
            exportFingerprint);
        Assert.True(RetargetMappingReview.Analyze(
            sourceRig,
            targetRig,
            exportMap).IsReady);
    }

    private static ImmutableArray<CustomModelBone> CreateTargetBones()
    {
        ImmutableArray<CustomModelBone>.Builder bones =
            ImmutableArray.CreateBuilder<CustomModelBone>(ExtraBoneCount + 2);
        bones.Add(Bone(0, 1, "root", -1, BoneKind.Root));
        for (int index = 0; index < ExtraBoneCount; index++)
        {
            bones.Add(Bone(
                index + 2,
                index + 3,
                $"branch_{index + 1:D2}",
                1,
                BoneKind.Deform));
        }

        bones.Insert(
            1,
            Bone(1, 2, "mapped_anchor", 0, BoneKind.Deform) with
            {
                LocalBindTransform = new TransformTRS(
                    new Vector3D(0.35, 0, 0),
                    QuaternionD.Identity,
                    Vector3D.One),
                ExactLocalBindMatrix = TransformMatrix.CreateTranslation(
                    new Vector3D(0.35, 0, 0)),
            });

        return bones.MoveToImmutable();
    }

    private static CustomModelBone Bone(
        int index,
        long objectId,
        string name,
        int parentIndex,
        BoneKind kind) => new()
    {
        Index = index,
        FbxObjectId = objectId,
        Name = name,
        ParentIndex = parentIndex,
        Kind = kind,
        IsWeighted = index > 0,
        LocalBindTransform = index == 0
            ? TransformTRS.Identity
            : new TransformTRS(
                new Vector3D(0, 1, 0),
                QuaternionD.Identity,
                Vector3D.One),
        ExactLocalBindMatrix = index == 0
            ? TransformMatrix.Identity
            : TransformMatrix.CreateTranslation(new Vector3D(0, 1, 0)),
    };

    private static RigDefinition CreateSourceRig() => new(
        "synthetic:source",
        "Synthetic source",
        [
            new BoneDefinition(
                0,
                "root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "mapped_anchor",
                0,
                new TransformTRS(
                    new Vector3D(0.1, 0, 0),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Deform),
        ]);

    private static AnimationClip CreateMovingClip(RigDefinition sourceRig) => new(
        "Synthetic stock motion",
        new FrameRate(30, 1),
        2,
        [
            new TransformTrack(
                0,
                [
                    new TransformKeyframe(0, sourceRig.Bones[0].LocalBindPose),
                    new TransformKeyframe(
                        1,
                        new TransformTRS(
                            new Vector3D(0.1, 0, 0),
                            QuaternionD.Identity,
                            Vector3D.One)),
                ]),
            new TransformTrack(
                1,
                [
                    new TransformKeyframe(0, sourceRig.Bones[1].LocalBindPose),
                    new TransformKeyframe(
                        1,
                        sourceRig.Bones[1].LocalBindPose with
                        {
                            Translation = new Vector3D(0.15, 0, 0),
                        }),
                ]),
        ]);

    private static ProjectAnimation GetActiveProjectAnimation(
        MainWindowViewModel owner)
    {
        Guid activeId = (Guid)typeof(MainWindowViewModel)
            .GetField("_activeAnimationId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
        return GetProject(owner).Animations.Single(animation =>
            animation.Id == activeId);
    }

    private static DlraProject GetProject(MainWindowViewModel owner) =>
        (DlraProject)typeof(MainWindowViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;

    private static RetargetMap BuildPersistedReviewMap(
        RigDefinition source,
        RigDefinition target,
        IEnumerable<ProjectBoneMapping> mappings,
        IEnumerable<ProjectTargetBindReview> targetBindReviews) =>
        (RetargetMap)typeof(MainWindowViewModel).GetMethod(
            "BuildPersistedReviewMapping",
            BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [source, target, mappings, targetBindReviews])!;

    private static object GetPublishedFrameFor(
        MainWindowViewModel owner,
        Guid animationId)
    {
        object? pair = typeof(MainWindowViewModel)
            .GetField("_lastPreviewFramePair", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner);
        Assert.NotNull(pair);
        object token = pair!.GetType().GetProperty("Token")!.GetValue(pair)!;
        Assert.Equal(
            animationId,
            token.GetType().GetProperty("AnimationId")!.GetValue(token));
        return pair;
    }

    private static object CreatePrivateRecord(
        string name,
        params object?[] arguments)
    {
        Type type = typeof(MainWindowViewModel).GetNestedType(
            name,
            BindingFlags.NonPublic)!;
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null)!;
    }

    private static void SetField(
        object owner,
        string name,
        object? value) =>
        owner.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, value);

    private static void SetProperty(
        object owner,
        string name,
        object? value) =>
        owner.GetType().GetProperty(name)!
            .SetValue(owner, value);

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }
}
