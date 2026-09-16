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
        wizard.SelectedStudioEntry = wizard.StudioEntryChoices.Single(c => c.Path == RigStudioEntryPath.AdaptExistingRig);
        wizard.StartStudioCommand.Execute(null);
        var initial = workspace.CaptureProjectSession().Model!;
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
        wizard.SelectedStudioEntry = wizard.StudioEntryChoices.Single(c => c.Path == RigStudioEntryPath.AdaptExistingRig);
        wizard.BodyComponents[0].Kind = RigGeometryComponentKind.Body;
        wizard.SaveBodyComponentsCommand.Execute(null);
        Assert.Equal(RigStudioEntryPath.AdaptExistingRig, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.EntryPath);
    }
    private static ModelsWorkspaceViewModel Workspace(FbxModelAuthoringImportResult model)
    {
        var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
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
