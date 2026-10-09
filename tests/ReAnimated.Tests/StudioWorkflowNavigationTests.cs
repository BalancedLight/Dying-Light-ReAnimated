using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class StudioWorkflowNavigationTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void NormalRiggedSetupStartsOnlyAfterChoosingAnExplicitAction()
    {
        using var workspace = Workspace(FbxSkinWeightAuthoringTests.Model());
        var wizard = workspace.Conformance;
        Assert.True(wizard.IsNormalRiggedSetup);
        Assert.Null(wizard.SelectedStudioEntry);
        Assert.Null(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession);
        Assert.True(wizard.StartAdaptStudioCommand.CanExecute(null));

        wizard.StartAdaptStudioCommand.Execute(null);

        Assert.Equal(
            RigStudioEntryPath.AdaptExistingRig,
            workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.EntryPath);
        Assert.Equal(RigStudioStage.Detect, wizard.StudioStage);
        Assert.False(wizard.StartRepairStudioCommand.CanExecute(null));
    }

    [Fact]
    public void LegacyNavigationIsViewOnlyAndOffersAllSevenStages()
    {
        using var workspace = Workspace(FbxSkinWeightAuthoringTests.Model());
        var before = workspace.CaptureProjectSession().Model!;
        string fingerprint = Fingerprint(before);
        Assert.Equal(Enum.GetValues<RigStudioStage>(), workspace.Conformance.StudioStages.Select(s => s.Stage));
        Assert.Equal(7, workspace.Conformance.StudioFacetHistory.Count);
        foreach (var stage in Enum.GetValues<RigStudioStage>()) workspace.Conformance.StudioStage = stage;
        var after = workspace.CaptureProjectSession().Model!;
        Assert.Null(after.Package.Document.RiggingSession);
        Assert.Equal(fingerprint, Fingerprint(after));
        Assert.False(workspace.Conformance.RecordStudioReviewCommand.CanExecute(null));
    }

    [Fact]
    public void SavedNavigationAndReviewKeepBuildInputsWhileAuthoringEditsInvalidateReviews()
    {
        using var workspace = Workspace(FbxSkinWeightAuthoringTests.Model());
        var wizard = workspace.Conformance;
        Assert.False(wizard.StartStudioCommand.CanExecute(null));
        Assert.Contains("Choose Repair or Adapt", wizard.StudioStartReason, StringComparison.Ordinal);
        wizard.SelectedStudioEntry = wizard.StudioEntryChoices.Single(c => c.Path == RigStudioEntryPath.AdaptExistingRig);
        Assert.True(wizard.StartStudioCommand.CanExecute(null));
        Assert.True(wizard.OpenRiggedCheckCommand.CanExecute(null));
        wizard.OpenRiggedCheckCommand.Execute(null);
        Assert.Equal(RigStudioStage.Fit, wizard.StudioStage);
        Assert.Equal(RigConformanceStage.Verify, wizard.Stage);
        wizard.StudioStage = RigStudioStage.Import;
        Assert.Contains("Ready to start", wizard.StudioStartReason, StringComparison.Ordinal);
        Assert.Empty(wizard.ChannelPolicies);
        wizard.StartStudioCommand.Execute(null);
        var initial = workspace.CaptureProjectSession().Model!;
        Assert.Equal(initial.Package.Document.Bones.Length, wizard.ChannelPolicies.Count);
        Assert.True(wizard.HasChannelPolicies);
        Assert.Contains("already saved", wizard.StudioStartReason, StringComparison.Ordinal);
        Assert.Contains("No runtime capability profile", wizard.StudioProfileSummary, StringComparison.Ordinal);
        var token = initial.Package.Document.RiggingSession!.CreateJobToken();
        string fingerprint = Fingerprint(initial);
        wizard.RecordStudioReviewCommand.Execute(null);
        wizard.StudioStage = RigStudioStage.Skin;
        var navigated = workspace.CaptureProjectSession().Model!;
        Assert.True(navigated.Package.Document.RiggingSession!.Matches(token));
        Assert.Equal(RigStudioStage.Skin, navigated.Package.Document.RiggingSession.Stage);
        Assert.NotNull(navigated.Package.Document.RiggingSession.Stages.Single(s => s.Stage == RigStudioStage.Import).ReviewedUtc);
        Assert.Empty(navigated.Package.Document.RiggingSession.ValidationHistory);
        Assert.Equal(fingerprint, Fingerprint(navigated));
        string path = Path.Combine(_directory, "workflow.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(navigated.Package, path);
        using var reopened = Workspace(FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path)));
        Assert.Equal(RigStudioStage.Skin, reopened.Conformance.StudioStage);
        wizard.RecordStudioReviewCommand.Execute(null);
        wizard.BodyComponents[0].UseForAnatomy = false;
        wizard.SaveBodyComponentsCommand.Execute(null);
        var changed = workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!;
        Assert.All(changed.Stages, s => Assert.Null(s.ReviewedUtc));
        Assert.Equal(RigStudioStage.Skin, changed.Stage);
    }

    [Fact]
    public async Task DetectionDraftSurvivesBacktrackingAndExplicitSessionStart()
    {
        using var workspace = Workspace(GeneratedBodyWorkflowTests.Source());
        var wizard = workspace.Conformance;
        wizard.BodyDetectionResolution = 32;
        await wizard.DetectBodyCommand.ExecuteAsync(null);
        var detection = wizard.BodyDetection;
        Assert.NotNull(detection);
        Assert.Equal(RigStudioStage.Detect, wizard.StudioStage);
        wizard.StudioStage = RigStudioStage.Import;
        Assert.Same(detection, wizard.BodyDetection);
        wizard.StartStudioCommand.Execute(null);
        Assert.True(wizard.HasStudioSession);
        Assert.Same(detection, wizard.BodyDetection);
        wizard.StudioStage = RigStudioStage.Fit;
        Assert.True(wizard.UseBodyGuidesCommand.CanExecute(null));
        wizard.UseBodyGuidesCommand.Execute(null);
        var current = workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!;
        Assert.Equal(18, current.Landmarks.Length);
        Assert.Equal(RigStudioStage.Fit, current.Stage);
    }

    [Fact]
    public void ReopenedAutoRigSessionKeepsItsAutoRigEntryChoice()
    {
        using var workspace = Workspace(GeneratedBodyWorkflowTests.Source());
        var wizard = workspace.Conformance;
        Assert.Single(wizard.StudioEntryChoices);
        Assert.Equal(RigStudioEntryPath.AutoRigBiped, wizard.SelectedStudioEntry!.Path);
        wizard.StartStudioCommand.Execute(null);
        string path = Path.Combine(_directory, "autorig-workflow.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(workspace.CaptureProjectSession().Model!.Package, path);

        using var reopened = Workspace(FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path)));
        Assert.Equal(RigStudioEntryPath.AutoRigBiped, reopened.Conformance.SelectedStudioEntry!.Path);
        Assert.Equal(new[] { RigStudioEntryPath.AutoRigBiped }, reopened.Conformance.StudioEntryChoices.Select(choice => choice.Path).ToArray());
    }

    [Fact]
    public async Task PendingWeightCorrectionSurvivesNavigationAndSessionStart()
    {
        using var workspace = Workspace(FbxSkinWeightAuthoringTests.Model());
        var wizard = workspace.Conformance;
        await wizard.InspectWeightsCommand.ExecuteAsync(null);
        wizard.SelectedWeightInfluence = wizard.WeightInfluences.Single(i => i.Name == "joint_a");
        wizard.WeightTargetValue = .5;
        await wizard.PreviewWeightCorrectionCommand.ExecuteAsync(null);
        Assert.True(wizard.ApplyWeightCorrectionCommand.CanExecute(null));
        wizard.StudioStage = RigStudioStage.Import;
        wizard.SelectedStudioEntry = wizard.StudioEntryChoices.Single(c => c.Path == RigStudioEntryPath.RepairExistingRig);
        wizard.StartStudioCommand.Execute(null);
        wizard.StudioStage = RigStudioStage.VerifyAndExport;
        Assert.True(wizard.ApplyWeightCorrectionCommand.CanExecute(null));
        wizard.StudioStage = RigStudioStage.Skin;
        await wizard.ApplyWeightCorrectionCommand.ExecuteAsync(null);
        var model = workspace.CaptureProjectSession().Model!;
        var weights = FbxSkinWeightAuthoring.Inspect(model);
        var influence = weights.Influences.Single(i => i.Name == "joint_a");
        Assert.Equal(.5, weights.Points[0].Weights.Single(w => w.HandleId == influence.EntityId).Weight);
    }

    [Fact]
    public void NavigationAndHistoryEpochsDoNotChangeCompilerInputsButPoliciesDo()
    {
        var model = FbxSkinWeightAuthoringTests.Model();
        var session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.RepairExistingRig);
        model = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session } } };
        string initial = Fingerprint(model);
        var navigated = RiggingSessions.Navigate(session, RigStudioStage.VerifyAndExport);
        navigated = RiggingSessions.RecordReview(navigated, RigStudioStage.VerifyAndExport, navigated.ComputeInputFingerprint(), DateTimeOffset.UtcNow);
        navigated = RiggingSessions.RestoreForUndo(navigated, navigated);
        var metadata = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = navigated } } };
        Assert.Equal(initial, Fingerprint(metadata));
        var policies = Dl1BoneScriptPolicyTests.WithPolicies(navigated);
        Assert.NotEqual(initial, Fingerprint(metadata with { Package = metadata.Package with { Document = metadata.Package.Document with { RiggingSession = policies } } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RiggingSessions.Navigate(session, (RigStudioStage)99));
    }

    [Fact]
    public void BuildSnapshotSurvivesOnlyWorkflowMetadataChanges()
    {
        using var workspace = Workspace(FbxSkinWeightAuthoringTests.Model());
        var wizard = workspace.Conformance;
        wizard.SelectedStudioEntry = wizard.StudioEntryChoices.Single(c => c.Path == RigStudioEntryPath.RepairExistingRig);
        wizard.StartStudioCommand.Execute(null);
        var built = workspace.CaptureProjectSession().Model!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        long revision = (long)typeof(ModelsWorkspaceViewModel).GetField("_authoringRevision", flags)!.GetValue(workspace)!;
        var predicate = typeof(ModelsWorkspaceViewModel).GetMethod("BuildSnapshotStillCurrent", flags)!;
        wizard.StudioStage = RigStudioStage.VerifyAndExport;
        wizard.RecordStudioReviewCommand.Execute(null);
        Assert.True((bool)predicate.Invoke(workspace, [built, revision, "workflow_fixture"])!);
        wizard.BodyComponents[0].UseForAnatomy = false;
        wizard.SaveBodyComponentsCommand.Execute(null);
        Assert.False((bool)predicate.Invoke(workspace, [built, revision, "workflow_fixture"])!);
    }

    private static string Fingerprint(FbxModelAuthoringImportResult model) => Dl1OfficialModelCompiler.CalculateInputFingerprint(model, "workflow_fixture", "default", null);

    [Fact]
    public void SavingComponentsRespectsTheExplicitEntryChoice()
    {
        using var workspace = Workspace(FbxSkinWeightAuthoringTests.Model());
        var wizard = workspace.Conformance;
        Assert.NotEmpty(workspace.CaptureProjectSession().Model!.Package.Document.Bones);
        Assert.Null(wizard.SelectedStudioEntry);
        wizard.BodyComponents[0].Kind = RigGeometryComponentKind.Body;
        wizard.SaveBodyComponentsCommand.Execute(null);
        Assert.Null(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession);
        Assert.Contains("Choose Repair or Adapt", wizard.BodyAuthoringStatus, StringComparison.Ordinal);
        wizard.SelectedStudioEntry = wizard.StudioEntryChoices.Single(c => c.Path == RigStudioEntryPath.AdaptExistingRig);
        wizard.SaveBodyComponentsCommand.Execute(null);
        Assert.Equal(RigStudioEntryPath.AdaptExistingRig, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.EntryPath);
        Assert.Equal(RigGeometryComponentKind.Body, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Components[0].Kind);
    }

    [Fact]
    public void ExistingRigWorkflowCanSwitchEntryPathAndUndoWithoutLosingComponentClassification()
    {
        using var workspace = Workspace(FbxSkinWeightAuthoringTests.Model());
        var wizard = workspace.Conformance;
        wizard.SelectedStudioEntry = wizard.StudioEntryChoices.Single(c => c.Path == RigStudioEntryPath.RepairExistingRig);
        wizard.BodyComponents[0].Kind = RigGeometryComponentKind.Body;
        wizard.BodyComponents[0].UseForAnatomy = false;
        wizard.SaveBodyComponentsCommand.Execute(null);

        foreach (var stage in Enum.GetValues<RigStudioStage>())
        {
            wizard.StudioStage = stage;
            wizard.RecordStudioReviewCommand.Execute(null);
        }
        var before = workspace.CaptureProjectSession().Model!;
        var beforeSession = before.Package.Document.RiggingSession!;
        var beforeToken = beforeSession.CreateJobToken();
        var component = beforeSession.Components[0];

        wizard.SelectedStudioEntry = wizard.StudioEntryChoices.Single(c => c.Path == RigStudioEntryPath.AdaptExistingRig);
        var adapted = workspace.CaptureProjectSession().Model!;
        var adaptedSession = adapted.Package.Document.RiggingSession!;
        Assert.Equal(RigStudioEntryPath.AdaptExistingRig, adaptedSession.EntryPath);
        Assert.Equal(RigMotionStrategy.PreserveAnatomyMapped, adaptedSession.Recipe.MotionStrategy);
        Assert.Equal(beforeSession.Id, adaptedSession.Id);
        Assert.Equal(beforeSession.OwnerModelId, adaptedSession.OwnerModelId);
        Assert.Equal(beforeSession.SourceSha256, adaptedSession.SourceSha256);
        Assert.Equal(component, adaptedSession.Components[0]);
        Assert.False(adaptedSession.Matches(beforeToken));
        Assert.NotNull(adaptedSession.Stages.Single(s => s.Stage == RigStudioStage.Import).ReviewedUtc);
        Assert.All(adaptedSession.Stages.Where(s => (int)s.Stage >= (int)RigStudioStage.Detect), s => Assert.Null(s.ReviewedUtc));
        Assert.Equal(component.Kind, wizard.BodyComponents[0].Kind);
        Assert.Equal(component.UseForAnatomy, wizard.BodyComponents[0].UseForAnatomy);

        Assert.True(workspace.UndoHelperEditCommand.CanExecute(null));
        workspace.UndoHelperEditCommand.Execute(null);
        var undone = workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!;
        Assert.Equal(RigStudioEntryPath.RepairExistingRig, undone.EntryPath);
        Assert.Equal(component, undone.Components[0]);
        Assert.Equal(beforeSession.Stages, undone.Stages);
    }
    private static ModelsWorkspaceViewModel Workspace(FbxModelAuthoringImportResult model)
    {
        var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        RigConformanceTestSchedulers.UseImmediate(workspace);
        workspace.CommitProjectRestore(new(model, "workflow.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        return workspace;
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
