using System.Collections.Immutable;
using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Providers;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class GuidedPreviewReviewDraftTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(), $"ReAnimated-GuidedReview-{Guid.NewGuid():N}");

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public async Task NonReadyGuidedDraftRetainsActivePairAndReviewRows()
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

        FbxModelAuthoringImportResult model =
            FbxSkinWeightAuthoringTests.Model();
        RigDefinition target = Assert.IsType<RigDefinition>(model.Rig);
        Assert.Equal(3, target.BoneCount);
        var source = new RigDefinition(
            "synthetic:source",
            "Synthetic source",
            [new BoneDefinition(
                0,
                "root",
                -1,
                target.Bones[0].LocalBindPose,
                BoneKind.Root)]);
        var clip = new AnimationClip(
            "Synthetic stock motion",
            new FrameRate(30, 1),
            2,
            [new TransformTrack(
                0,
                [
                    new TransformKeyframe(0, TransformTRS.Identity),
                    new TransformKeyframe(
                        1,
                        new TransformTRS(
                            new(0.1, 0, 0),
                            ReAnimated.Core.Mathematics.QuaternionD.Identity,
                            ReAnimated.Core.Mathematics.Vector3D.One)),
                ])]);
        RetargetMap mapping = new(
            source.Id,
            target.Id,
            [new BoneMapEntry(
                0,
                0,
                BoneMappingMethod.ExactName,
                1.0)]);

        Guid sourceAssetId = Guid.NewGuid();
        Guid targetAssetId = Guid.NewGuid();
        Guid modelEntryId = Guid.NewGuid();
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
            RelativePath = "Models/synthetic-target.dlrmodel",
            ResourceId = model.Package.Document.ModelId.ToString("N"),
            ContentSha256 = new string('b', 64),
        };
        ProjectModelEntry modelEntry = new()
        {
            Id = modelEntryId,
            AssetId = targetAssetId,
            Name = "Synthetic target",
            RigSignature = RigSignature.Compute(target),
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
                SourceRigSignature = RigSignature.Compute(source),
                Roles = AnimationSourceRoles.Body,
            },
            TargetAssetId = targetAssetId,
            TargetRigId = target.Id,
            SourceRigSignature = RigSignature.Compute(source),
            TargetRigSignature = RigSignature.Compute(target),
            SourceAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(source),
            TargetAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(target),
            BindingMode = ProjectAnimationBindingMode.Retarget,
            FrameRate = clip.FrameRate,
            FrameCount = clip.FrameCount,
            BoneMappings = [new ProjectBoneMapping
            {
                SourceBoneName = "root",
                TargetBoneName = "root",
                Confidence = 1.0,
                Method = BoneMappingMethod.ExactName.ToString(),
                MappingKind = RetargetMappingKind.Bone,
                IsReviewed = true,
            }],
        };
        DlraProject project = DlraProject.Create("Guided review draft") with
        {
            Assets = [sourceAsset, targetAsset],
            Models = [modelEntry],
            Animations = [animation],
            ActiveAnimationId = animation.Id,
        };

        object sourceSession = CreatePrivateRecord(
            "ImportedAnimationSession",
            source,
            clip,
            "synthetic://motion",
            "Synthetic");
        CustomModelPreviewSession previewSession =
            CustomModelPreviewAdapter.CreateSession(
                model,
                CustomModelPreviewMode.SourceFbx);
        object customTarget = CreatePrivateRecord(
            "PreparedCustomTarget",
            target,
            Array.Empty<MeshRenderData>(),
            CorePreviewAdapter.ToRenderSkeleton(target.CreateBindPose()),
            targetAsset,
            previewSession,
            CustomModelPreviewMode.SourceFbx,
            CustomModelPreviewMode.SourceFbx,
            ImmutableArray<string>.Empty);
        SetField(viewModel, "_animationTransitionGeneration", 1L);
        SetField(viewModel, "_hasGuidedPreviewReviewDraft", true);
        typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.IsBusy))!
            .SetValue(viewModel, true);

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

        Assert.True(viewModel.HasGuidedPreviewReviewDraft);
        Assert.True(viewModel.HasPlaybackAnimation);
        Assert.Equal(animation.Name, viewModel.ActiveAnimationLabel);
        Assert.Equal(
            TargetBindingStatus.NeedsReview,
            viewModel.ActiveTargetBindingStatus);
        Assert.Equal(2, viewModel.RequiredTargetBindReviews.Count);
        Assert.All(
            viewModel.RequiredTargetBindReviews,
            row => Assert.Contains(
                "Retained weighted source points:",
                row.ContextSummary,
                StringComparison.Ordinal));
        Assert.False(
            viewModel.OpenGuidedMappingReviewCommand.CanExecute(null));
        Assert.False(
            viewModel.ApplyGuidedPreviewReviewCommand.CanExecute(null));
        typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.IsBusy))!
            .SetValue(viewModel, false);
        typeof(MainWindowViewModel).GetMethod(
            "NotifyMappingCommands",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, null);
        Assert.True(
            viewModel.OpenGuidedMappingReviewCommand.CanExecute(null));
        Assert.True(
            viewModel.ApplyGuidedPreviewReviewCommand.CanExecute(null));
        Assert.False(
            RetargetMappingReview.Analyze(source, target, mapping).IsReady);
        DlraProject committedProject = (DlraProject)typeof(MainWindowViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        Assert.Equal(animation.Id, committedProject.ActiveAnimationId);
        Assert.Contains(
            committedProject.AnimationVariants,
            variant => variant.Id == animation.Id &&
                variant.TargetModelId == modelEntryId);

        string acceptedBone = viewModel.RequiredTargetBindReviews[0].TargetBone;
        viewModel.RequiredTargetBindReviews[0].IsReviewed = true;
        Assert.True(
            viewModel.ApplyGuidedPreviewReviewCommand.CanExecute(null));
        viewModel.ApplyGuidedPreviewReviewCommand.Execute(null);
        Assert.True(viewModel.HasGuidedPreviewReviewDraft);
        Assert.Equal(1, viewModel.RequiredTargetBindReviews.Count(
            row => row.IsReviewed));
        DlraProject partiallyReviewedProject =
            (DlraProject)typeof(MainWindowViewModel)
                .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel)!;
        ProjectAnimation persistedAnimation =
            partiallyReviewedProject.Animations.Single(
                row => row.Id == animation.Id);
        Assert.Contains(
            acceptedBone,
            persistedAnimation.TargetBindReviews.Select(
                static row => row.TargetBoneName));
        Assert.NotNull(
            typeof(MainWindowViewModel).GetMethod(
                "DescribeAnimationExportBlock",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, null));
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
