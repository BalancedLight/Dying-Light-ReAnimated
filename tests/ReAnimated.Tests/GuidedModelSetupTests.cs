using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class GuidedModelSetupTests
{
    [Theory]
    [InlineData(CustomModelWorkflowMode.Character, true)]
    [InlineData(CustomModelWorkflowMode.OriginalRig, false)]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void SerializedWorkflowModeSurvivesPackageImportAndSelectsGuidedMode(
        CustomModelWorkflowMode workflowMode,
        bool usesDl1Rig)
    {
        FbxModelAuthoringImportResult source = FbxModelAuthoringImporter.Import(
            RigidPropWorkflowTests.CreateDoorFixture(), "workflow-model.fbx");
        source = source with
        {
            Package = source.Package with
            {
                Document = source.Package.Document with { WorkflowMode = workflowMode },
            },
        };
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "workflow-model.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(source.Package, path);

            CustomModelPackage loaded = CustomModelPackageSerializer.Load(path);
            FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(loaded);
            using ModelsWorkspaceViewModel workspace = CreateWorkspace(model: reopened);

            Assert.Equal(workflowMode, reopened.Package.Document.WorkflowMode);
            Assert.Equal(usesDl1Rig, workspace.GuidedUsesDl1Rig);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task KeepOriginalDoesNotRelabelAnAppliedFit()
    {
        using var workspace = CreateWorkspace(withModel: true);
        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        var fitted = workspace.CaptureProjectSession().Model!;
        long revision = workspace.PersistenceRevision;
        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.KeepOriginal);
        Assert.Same(fitted, workspace.CaptureProjectSession().Model);
        Assert.Equal(revision, workspace.PersistenceRevision);
        Assert.True(workspace.GuidedUsesDl1Rig);
        Assert.Equal(CustomModelWorkflowMode.Character, fitted.Package.Document.WorkflowMode);
        Assert.Contains("Undo the fit", workspace.GuidedStatus);

        var legacy = fitted with { Package = fitted.Package with { Document = fitted.Package.Document with
        { WorkflowMode = CustomModelWorkflowMode.OriginalRig } } };
        using var reopened = CreateWorkspace(model: legacy);
        Assert.True(reopened.GuidedUsesDl1Rig);
        Assert.True(reopened.IsGuidedPreview);
    }

    [Fact]
    public async Task OriginalPropStartsWithItsIncludedAuthoredClipAndSkipsCharacterFitAdvice()
    {
        var source = FbxModelAuthoringImporter.Import(RigidPropWorkflowTests.CreateDoorFixture(), "generic-door.fbx");
        source = source with { Package = source.Package with { Document = source.Package.Document with
        { AnimationClips = source.Package.Document.AnimationClips.Select(static clip => clip with { Included = false }).ToImmutableArray() } } };
        source = FbxAuthoredAnimationAuthoring.Create(source, "open", 1, new FrameRate(30, 1));
        using var workspace = CreateWorkspace(model: source);
        Assert.Equal("open", workspace.SelectedAnimation!.DisplayName);
        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.KeepOriginal);
        Assert.False(workspace.ShowGuidedFitReview);
        Assert.Equal("Original rig", workspace.GuidedRigSummary);
        Assert.True(workspace.GuidedPrimaryCommand.CanExecute(null));
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.True(workspace.IsGuidedPreview);
        Assert.DoesNotContain("stock", workspace.GuidedStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Preview animation", workspace.GuidedPrimaryLabel);
    }
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void EmptyWorkspaceStartsWithOneImportAction()
    {
        using var workspace = CreateWorkspace();
        Assert.True(workspace.IsGuidedImport);
        Assert.Equal("Import FBX…", workspace.GuidedPrimaryLabel);
        Assert.False(workspace.GuidedBackCommand.CanExecute(null));
        Assert.False(workspace.Conformance.IsAdvancedSetupMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task RestoredAppliedFitResumesPreviewOnlyWhenItsRigStillMatches(bool stale)
    {
        using var original = CreateWorkspace(withModel: true);
        await original.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);
        await original.GuidedPrimaryCommand.ExecuteAsync(null);
        FbxModelAuthoringImportResult applied = original.CaptureProjectSession().Model!;
        Assert.NotNull(applied.Package.Document.RigConformance?.AppliedOutputRigSignature);
        if (stale)
        {
            applied = applied with { Package = applied.Package with { Document = applied.Package.Document with
            {
                RigConformance = applied.Package.Document.RigConformance! with { AppliedOutputRigSignature = new string('0', 64) },
            } } };
        }
        using var restored = CreateWorkspace(model: applied);
        Assert.Equal(stale ? GuidedModelSetupStep.Adjust : GuidedModelSetupStep.Preview, restored.GuidedStep);
        Assert.Same(applied, restored.CaptureProjectSession().Model);
        if (!stale)
        {
            Assert.Contains("saved Dying Light fit is restored", restored.GuidedStatus, StringComparison.Ordinal);
            Assert.Equal("Play a Dying Light animation", restored.GuidedPrimaryLabel);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task OriginalSkeletonAdvancesWithoutApplyingAFit()
    {
        using var workspace = CreateWorkspace(withModel: true);
        long revision = workspace.PersistenceRevision;
        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.KeepOriginal);
        Assert.False(workspace.GuidedUsesDl1Rig);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.True(workspace.IsGuidedPreview);
        Assert.True(workspace.PersistenceRevision > revision);
        Assert.Equal(CustomModelWorkflowMode.OriginalRig,
            workspace.CaptureProjectSession().Model!.Package.Document.WorkflowMode);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task OriginalRigChoicePersistsAndPreviewsItsOwnClipAfterRestore()
    {
        FbxModelAuthoringImportResult source = WithoutDerivedMotions(
            CreateModelWithAnimationTakes());
        using var first = CreateWorkspace(model: source);
        await first.BeginGuidedSetupAsync(CustomModelSkeletonChoice.KeepOriginal);
        FbxModelAuthoringImportResult saved = first.CaptureProjectSession().Model!;
        Assert.Equal(CustomModelWorkflowMode.OriginalRig, saved.Package.Document.WorkflowMode);

        using var restored = CreateWorkspace(model: saved);
        int stockPreviewCalls = 0;
        restored.SetGuidedPreviewHandler(() =>
        {
            stockPreviewCalls++;
            return Task.FromResult(true);
        });

        Assert.False(restored.GuidedUsesDl1Rig);
        await restored.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.True(restored.IsGuidedPreview);
        Assert.Equal("Preview animation", restored.GuidedPrimaryLabel);

        await restored.GuidedPrimaryCommand.ExecuteAsync(null);

        Assert.True(restored.IsGuidedExport);
        Assert.Equal(0, stockPreviewCalls);
        Assert.NotNull(restored.SelectedAnimation?.DecodedClip);
        Assert.NotNull(restored.Viewport.SceneSource.CaptureFrame().Skeleton);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task AutomaticFitPreservesProportionsAndFailedMotionStaysAtPreview()
    {
        using var workspace = CreateWorkspace(withModel: true);
        workspace.SetGuidedPreviewHandler(static () => Task.FromResult(false));
        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);
        Assert.True(workspace.Conformance.CanApplyGuidedFit, workspace.Conformance.GuidedFitBlockReason + " / " + workspace.Conformance.SolveStatus);
        Assert.Equal(0, workspace.Conformance.ConformanceStrength);
        Assert.True(workspace.IsGuidedAdjust);
        long revision = workspace.PersistenceRevision;
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.True(workspace.PersistenceRevision > revision, workspace.BuildStatus + " / " + workspace.GuidedStatus);
        Assert.True(workspace.IsGuidedPreview);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.True(workspace.IsGuidedPreview);
        Assert.False(workspace.IsGuidedExport);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task SuccessfulMotionCanProceedToExportAndBackToAdjust()
    {
        using var workspace = CreateWorkspace(withModel: true);
        int previewCalls = 0;
        workspace.SetGuidedPreviewHandler(() => { previewCalls++; return Task.FromResult(true); });
        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.KeepOriginal);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.Equal(1, previewCalls);
        Assert.True(workspace.IsGuidedExport);
        await workspace.GuidedBackCommand.ExecuteAsync(null);
        Assert.True(workspace.IsGuidedPreview);
        await workspace.GuidedBackCommand.ExecuteAsync(null);
        Assert.True(workspace.IsGuidedAdjust);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task BackFromFailedPreviewRefreshesTheAppliedRigAndAdjustmentStatus()
    {
        using var workspace = CreateWorkspace(withModel: true);
        workspace.SetGuidedPreviewHandler(static () => Task.FromResult(false));
        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        workspace.SetGuidedPreviewFailure("A test clip could not be loaded.");
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.Contains("test clip", workspace.GuidedStatus, StringComparison.Ordinal);

        await workspace.GuidedBackCommand.ExecuteAsync(null);

        Assert.True(workspace.IsGuidedAdjust);
        Assert.True(workspace.Conformance.IsEditingAppliedOutputRig);
        Assert.True(workspace.Conformance.CanApplyGuidedFit, workspace.Conformance.GuidedFitBlockReason);
        Assert.StartsWith("Ready.", workspace.GuidedStatus, StringComparison.Ordinal);
        Assert.False(workspace.IsGuidedBusy);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task UnriggedModelShowsEditableUnapprovedGuidesBeforePrimaryAction()
    {
        var source = AnatomicalDetectionWorkflowTests.CreateUnriggedModel();
        source = source with
        {
            Package = source.Package with
            {
                Document = source.Package.Document with { RiggingSession = null },
            },
        };
        using var workspace = CreateWorkspace(model: source);
        workspace.Conformance.BodyDetectionResolution = 48;

        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);

        Assert.True(workspace.IsGuidedUnriggedPreparation);
        Assert.True(workspace.IsGuidedAdjust);
        Assert.True(workspace.GuidedBodyProposals.Count > 0, workspace.GuidedStatus + " / " + workspace.GuidedBodyDetectionStatus);
        Assert.Contains(workspace.GuidedBodyProposals, choice => choice.Label == "Pelvis");
        Assert.False(workspace.Conformance.CanApplyGuidedFit);
        Assert.True(workspace.Conformance.HasSavedBodyGuide);
        Assert.True(workspace.GuidedDraftBodyGuidesAreUnapproved);
        Assert.Contains("Review the draft body guides", workspace.GuidedStatus, StringComparison.Ordinal);
        Assert.False(workspace.ShowGuidedJointControls);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task GuidedPrimaryBuildsBindsAndAppliesAnUnriggedModelWithSavedGuides()
    {
        var source = GeneratedBodyWorkflowTests.WithFixtureGuides(GeneratedBodyWorkflowTests.Source());
        using var workspace = CreateWorkspace(model: source);
        workspace.Conformance.BodyDetectionResolution = 32;
        long revision = workspace.PersistenceRevision;

        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);
        Assert.True(workspace.IsGuidedUnriggedPreparation);
        Assert.True(workspace.Conformance.HasSavedBodyGuide);

        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);

        string mappings = string.Join("; ", workspace.Conformance.GuidedMissingMappings.Select(row =>
            $"{row.Role}: {string.Join(", ", row.Candidates)} (selected: {row.SelectedSourceName})"));
        Assert.True(workspace.PersistenceRevision > revision);
        var applied = workspace.CaptureProjectSession().Model!;
        var session = applied.Package.Document.RiggingSession!;
        string rigBones = string.Join("; ", applied.Rig!.Bones.Select(bone => $"{bone.Name}={bone.SemanticRole}"));
        string entities = string.Join("; ", session.Recipe.Entities.Select(entity =>
            $"{entity.EntityId}:{entity.NativeName}:{entity.Kind}"));
        string assignments = string.Join("; ", session.Recipe.Assignments.Select(assignment =>
            $"{assignment.RoleId}={assignment.EntityId}"));
        Assert.True(workspace.IsGuidedPreview,
            workspace.GuidedStatus + " / " + workspace.Conformance.BodyAuthoringStatus + " / " + mappings +
            " / bones: " + rigBones + " / entities: " + entities + " / assignments: " + assignments);
        Assert.NotNull(applied.Package.Document.RiggingSession!.BindingBackend);
        Assert.NotNull(applied.Package.Document.RigConformance);
        Assert.True(source.Package.SourceFbx.AsSpan().SequenceEqual(applied.Package.SourceFbx.AsSpan()));
        Assert.Equal(source.Surfaces.SelectMany(static surface => surface.SourceGeometry!.ControlPoints),
            applied.Surfaces.SelectMany(static surface => surface.SourceGeometry!.ControlPoints));
        Assert.Equal(source.Package.Document.MorphChannels, applied.Package.Document.MorphChannels);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task GuidedPrimaryDoesNotAdvanceWhenGeneratedRigCannotBindSourceGeometry()
    {
        var source = GeneratedBodyWorkflowTests.WithFixtureGuides(GeneratedBodyWorkflowTests.Source());
        using var workspace = CreateWorkspace(model: source);
        workspace.Conformance.BodyDetectionResolution = 48;

        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);
        Assert.True(workspace.Conformance.HasSavedBodyGuide);
        foreach (var component in workspace.Conformance.BodyComponents)
            component.BindingMode = ReAnimated.Core.ModelAuthoring.RigComponentBindingMode.KeepSource;
        workspace.Conformance.SaveBodyComponentsCommand.Execute(null);

        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);

        Assert.True(workspace.IsGuidedAdjust);
        Assert.True(workspace.Conformance.HasGeneratedBodyRig);
        Assert.Null(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.BindingBackend);
        Assert.Contains("Choose automatic or rigid binding", workspace.GuidedStatus, StringComparison.Ordinal);
        Assert.Null(workspace.CaptureProjectSession().Model!.Package.Document.RigConformance);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void NewGuidedImportExcludesDecodedPoseOnlyTakesButPreservesSavedSelectionsAndSource()
    {
        FbxModelAuthoringImportResult source = CreateModelWithAnimationTakes();
        ImmutableArray<byte> originalFbx = source.Package.SourceFbx;

        FbxModelAuthoringImportResult guidedImport = ModelsWorkspaceViewModel.ApplyGuidedImportAnimationDefaults(
            source, isNewGuidedImport: true);
        var pose = guidedImport.Package.Document.AnimationClips.Single(static clip => clip.SourceName == "pose");
        var moving = guidedImport.Package.Document.AnimationClips.Single(static clip => clip.SourceName == "walk");
        var derived = guidedImport.Package.Document.AnimationClips.Single(static clip => clip.SourceName == "derived");
        Assert.False(pose.Included);
        Assert.True(moving.Included);
        Assert.True(derived.Included);
        Assert.Equal(originalFbx, guidedImport.Package.SourceFbx);
        Assert.Equal(guidedImport.Package.Document.AnimationClips,
            ModelsWorkspaceViewModel.GetGuidedPackageAnimationSelections(guidedImport));

        FbxModelAuthoringImportResult savedSelection = ModelsWorkspaceViewModel.ApplyGuidedImportAnimationDefaults(
            source, isNewGuidedImport: false);
        Assert.True(savedSelection.Package.Document.AnimationClips.Single(static clip => clip.SourceName == "pose").Included);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void GuidedPackageAliasPreservesSavedAndStockBankChoicesAndNamesSelectedTakes()
    {
        FbxModelAuthoringImportResult source = CreateModelWithAnimationTakes();
        ImmutableArray<CustomModelAnimationClip> selections = source.Package.Document.AnimationClips;

        Assert.Equal("guided_character", ModelsWorkspaceViewModel.ResolveGuidedPackageAnimationAlias(
            selections, savedAlias: null, referenceExistingAnimationLibrary: false, resourceName: "guided_character"));
        Assert.Equal("saved_bank", ModelsWorkspaceViewModel.ResolveGuidedPackageAnimationAlias(
            selections, "saved_bank", referenceExistingAnimationLibrary: false, resourceName: "guided_character"));
        Assert.Equal("saved_stock_bank", ModelsWorkspaceViewModel.ResolveGuidedPackageAnimationAlias(
            selections, "saved_stock_bank", referenceExistingAnimationLibrary: true, resourceName: "guided_character"));
        Assert.Null(ModelsWorkspaceViewModel.ResolveGuidedPackageAnimationAlias(
            selections, savedAlias: null, referenceExistingAnimationLibrary: true, resourceName: "guided_character"));

        var poseOnlySelections = selections.Select(selection => selection.SourceName == "pose"
            ? selection with { Included = true }
            : selection with { Included = false }).ToImmutableArray();
        Assert.Equal("guided_character", ModelsWorkspaceViewModel.ResolveGuidedPackageAnimationAlias(
            poseOnlySelections, savedAlias: null, referenceExistingAnimationLibrary: false, resourceName: "guided_character"));
        Assert.Equal("saved_bank", ModelsWorkspaceViewModel.ResolveGuidedPackageAnimationAlias(
            poseOnlySelections, "saved_bank", referenceExistingAnimationLibrary: false, resourceName: "guided_character"));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task GuidedExportDoesNotRejectAnExplicitlyIncludedPoseOnlyTake()
    {
        FbxModelAuthoringImportResult source = CreateModelWithAnimationTakes();
        source = source with
        {
            Package = source.Package with
            {
                Document = source.Package.Document with
                {
                    AnimationClips = source.Package.Document.AnimationClips
                        .Where(static selection => selection.DerivedMotion is null).ToImmutableArray(),
                },
            },
        };
        // The canceled output picker must be reached without an installed compiler.
        // Only file existence is checked before the picker; this file is never executed.
        var dialogs = new NoDialogs(typeof(GuidedModelSetupTests).Assembly.Location);
        using var workspace = CreateWorkspace(model: source, fileDialogs: dialogs);
        workspace.SelectModelCompilerCommand.Execute(null);
        workspace.AnimationScriptAlias = "pose_bank";
        workspace.SetGuidedPreviewHandler(static () => Task.FromResult(true));

        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.KeepOriginal);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.True(workspace.IsGuidedExport);

        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);

        Assert.True(workspace.IsGuidedExport);
        Assert.DoesNotContain("pose-only", workspace.GuidedStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("pose_bank", workspace.AnimationScriptAlias);
        Assert.Equal(1, dialogs.OutputDirectoryRequests);
        Assert.Contains("parent folder for the complete DL1 model package", workspace.GuidedStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task GuidedExportStaysOnExportUntilEmittedChannelPoliciesAreReviewed()
    {
        FbxModelAuthoringImportResult source = GeneratedBodyWorkflowTests.WithFixtureGuides(GeneratedBodyWorkflowTests.Source());
        FbxModelAuthoringImportResult generated = FbxGeneratedBodyBinding.Generate(source);
        using var workspace = CreateWorkspace(model: generated);
        workspace.SetGuidedPreviewHandler(static () => Task.FromResult(true));

        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);

        Assert.True(workspace.IsGuidedExport);
        Assert.True(workspace.GuidedChannelPolicyReviewRequired);
        Assert.True(workspace.GuidedUnresolvedChannelDecisionCount > 0);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.True(workspace.IsGuidedExport);
        Assert.Contains("emitted node(s) require explicit POS/ROT/SCL", workspace.GuidedStatus, StringComparison.Ordinal);
        Assert.Contains("Review exact stock matches", workspace.GuidedChannelPolicyReviewSummary, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void DecodedPoseOnlyStackDoesNotCreateAutomaticProjectAnimation()
    {
        var asset = new ProjectAssetReference { Kind = ProjectAssetKind.CustomModelSource,
            RelativePath = "Sources/generic-model.dlrmodel" };
        var model = new ProjectModelEntry { AssetId = asset.Id, Name = "Generic model" };
        var selection = new ReAnimated.Core.ModelAuthoring.CustomModelAnimationClip
        {
            Id = Guid.NewGuid(), FbxObjectId = 100, SourceName = "Rest pose", DisplayName = "Rest pose",
            FrameRate = new ReAnimated.Core.Domain.FrameRate(30, 1), FrameCount = 2,
            SourceFingerprint = new string('a', 64), HasSkeletalTracks = true,
        };
        var payload = new ModelsWorkspacePersistencePayload(Guid.NewGuid(), "generic-model.dlrmodel", [],
            new string('b', 64), new string('c', 64), new string('d', 64), null, null, new string('e', 64),
            null, null, 0, "generic-rig", [new ModelsWorkspaceEmbeddedStackPayload(selection, true, false)],
            selection.Id, ProjectCustomModelPreviewMode.Dl1Output, true, true, true, true, true);
        var project = ReAnimated.Core.Project.DlraProject.Create("Generic project") with { Assets = [asset], Models = [model] };
        var reconciled = MainWindowViewModel.ReconcileEmbeddedCustomModelStacks(project, asset, model, payload);
        Assert.Empty(reconciled.AnimationSources);
        Assert.Empty(reconciled.AnimationVariants);
        Assert.Equal(selection, Assert.Single(payload.EmbeddedStacks).Selection);
    }

    private static ModelsWorkspaceViewModel CreateWorkspace(bool withModel = false, FbxModelAuthoringImportResult? model = null,
        IProjectFileDialogService? fileDialogs = null)
    {
        var workspace = new ModelsWorkspaceViewModel(fileDialogs ?? new NoDialogs(), static _ => { },
            static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (profile, _) => Task.FromResult(RigConformanceWizardTests.CreateResolution(profile)),
            captureAuthoredLayer: static (model, _) => model);
        RigConformanceTestSchedulers.UseImmediate(workspace);
        if (withModel || model is not null) workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            model ?? RigConformanceWizardTests.CreateModel(), "generic-model.dlrmodel",
            new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        return workspace;
    }

    private static FbxModelAuthoringImportResult CreateModelWithAnimationTakes()
    {
        var pose = new AnimationClip("pose", new FrameRate(30, 1), 2,
            [new TransformTrack(0, [
                new TransformKeyframe(0, TransformTRS.Identity),
                new TransformKeyframe(1, TransformTRS.Identity),
            ])]);
        var moving = new AnimationClip("walk", new FrameRate(30, 1), 2,
            [new TransformTrack(0, [
                new TransformKeyframe(0, TransformTRS.Identity),
                new TransformKeyframe(1, TransformTRS.Identity with { Translation = new Vector3D(0.02, 0, 0) }),
            ])]);
        Guid poseId = Guid.NewGuid();
        Guid movingId = Guid.NewGuid();
        var derived = new CustomModelAnimationClip
        {
            Id = Guid.NewGuid(), SourceName = "derived", DisplayName = "derived", FbxObjectId = 0,
            Included = true, FrameRate = new FrameRate(30, 1), StartFrame = 0, FrameCount = 2,
            SourceFingerprint = new string('b', 64), HasSkeletalTracks = true,
            DerivedMotion = new DerivedMotionReference
            {
                SourceClipId = Guid.NewGuid(), SourceClipFingerprint = new string('c', 64),
                SourceFileSha256 = new string('d', 64), SourceRigSignature = new string('e', 64),
                TargetRigSignature = new string('f', 64), AlgorithmId = "generic-derived-motion",
                SampleMultiplier = 1, PayloadSha256 = new string('a', 64), PayloadLength = 1,
            },
        };
        var selections = ImmutableArray.Create(
            new CustomModelAnimationClip
            {
                Id = poseId, SourceName = "pose", DisplayName = "pose", FbxObjectId = 10,
                Included = true, FrameRate = new FrameRate(30, 1), StartFrame = 0, FrameCount = 2,
                SourceFingerprint = new string('1', 64), HasSkeletalTracks = true,
            },
            new CustomModelAnimationClip
            {
                Id = movingId, SourceName = "walk", DisplayName = "walk", FbxObjectId = 11,
                Included = true, FrameRate = new FrameRate(30, 1), StartFrame = 0, FrameCount = 2,
                SourceFingerprint = new string('2', 64), HasSkeletalTracks = true,
            },
            derived);
        FbxModelAuthoringImportResult basis = RigConformanceWizardTests.CreateModel();
        return basis with
        {
            Package = basis.Package with
            {
                Document = basis.Package.Document with { AnimationClips = selections },
            },
            AnimationClips = ImmutableDictionary<Guid, AnimationClip>.Empty
                .Add(poseId, pose)
                .Add(movingId, moving),
        };
    }

    private static FbxModelAuthoringImportResult WithoutDerivedMotions(
        FbxModelAuthoringImportResult model)
    {
        ImmutableArray<CustomModelAnimationClip> sources = model.Package.Document.AnimationClips
            .Where(static clip => clip.DerivedMotion is null)
            .ToImmutableArray();
        HashSet<Guid> sourceIds = sources.Select(static clip => clip.Id).ToHashSet();
        return model with
        {
            Package = model.Package with
            {
                Document = model.Package.Document with { AnimationClips = sources },
                DerivedAnimationPayloads = ImmutableDictionary<Guid, ImmutableArray<byte>>.Empty,
            },
            AnimationClips = model.AnimationClips
                .Where(pair => sourceIds.Contains(pair.Key))
                .ToImmutableDictionary(),
        };
    }

    private sealed class NoDialogs(string? compilerExecutablePath = null) : IProjectFileDialogService
    {
        public int OutputDirectoryRequests { get; private set; }

        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public string? ShowOpenDl1DeveloperToolsCompilerDialog(string? initialPath) => compilerExecutablePath;
        public string? ShowSelectCustomModelOutputDirectory(string? initialPath)
        {
            OutputDirectoryRequests++;
            return null;
        }
    }
}
