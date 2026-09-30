using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class RigSetupWorkflowTests
{
    internal static RigSetupPreset Setup()
    {
        var source = StructuralHelperAuthoringTests.Source();
        var recipe = source.Package.Document.RiggingSession!.Recipe;
        var candidate = FbxCapabilityProfileAuthoring.Preview(source, CapabilityProfileWorkflowTests.Profile(), ["partial"],
            [new("marker", recipe.Entities.Single(e => e.NativeName == "normal_marker_2").EntityId)], recipe.AssetRoles).Candidate;
        return RigSetupPresetSerializer.Capture(candidate.Package.Document, "Reusable structural setup");
    }

    [Fact]
    public async Task ReviewedSetupAppliesAsOneUndoableEditAndSurvivesStageNavigation()
    {
        var source = StructuralHelperAuthoringTests.Source();
        using var workspace = new ModelsWorkspaceViewModel(new Dialogs(), _ => { }, _ => Task.CompletedTask, () => null);
        workspace.CommitProjectRestore(new(source, "target.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        var wizard = workspace.Conformance;
        wizard.StudioStage = RigStudioStage.HelpersAndHooks;
        wizard.SetSetupDraft(Setup());
        Assert.All(wizard.SetupMappings, r => Assert.NotNull(r.Destination));
        await wizard.PreviewSetupCommand.ExecuteAsync(null);
        Assert.False(wizard.CanApplySetup);
        Assert.NotEmpty(wizard.SetupReview);
        wizard.SetupReviewed = true;
        wizard.StudioStage = RigStudioStage.VerifyAndExport;
        wizard.StudioStage = RigStudioStage.HelpersAndHooks;
        Assert.True(wizard.CanApplySetup);
        wizard.ApplySetupCommand.Execute(null);
        var applied = workspace.CaptureProjectSession().Model!;
        Assert.Equal(Setup().Profile.Identity, applied.Package.Document.RiggingSession!.Recipe.Profile);
        Assert.Same(source.Rig, applied.Rig);
        Assert.Equal(source.Surfaces, applied.Surfaces);
        Assert.Equal(source.AnimationClips, applied.AnimationClips);
        Assert.Empty(applied.Package.Document.RiggingSession.ValidationHistory);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Null(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.Profile);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.NotNull(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.Profile);
    }

    [Fact]
    public async Task MappingChangesAndNewModelsInvalidatePreviewApproval()
    {
        var wizard = new RigConformanceWizardViewModel((_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "No native reference needed")), _ => { });
        wizard.SetModel(StructuralHelperAuthoringTests.Source()); wizard.SetSetupDraft(Setup());
        await wizard.PreviewSetupCommand.ExecuteAsync(null); wizard.SetupReviewed = true;
        Assert.True(wizard.CanApplySetup);
        wizard.SetupMappings[0].Destination = null;
        Assert.False(wizard.CanApplySetup); Assert.False(wizard.CanPreviewSetup);
        wizard.SetSetupDraft(Setup());
        Task preview = wizard.PreviewSetupCommand.ExecuteAsync(null);
        wizard.SetModel(StructuralHelperAuthoringTests.Source());
        await preview;
        Assert.False(wizard.CanApplySetup); Assert.Empty(wizard.SetupMappings);
    }

    [Fact]
    public async Task SetupFileUsesDialogPathsAndReloadsWithoutDonorPackage()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var source = StructuralHelperAuthoringTests.Source();
            var setup = Setup();
            var mapping = FbxRigSetupTransfer.Propose(source, setup).ToDictionary(m => m.Key, m => m.DestinationEntityId!.Value);
            var donor = FbxRigSetupTransfer.Preview(source, setup, mapping).Candidate;
            var wizard = new RigConformanceWizardViewModel((_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "No native reference needed")), _ => { });
            wizard.SetModel(donor);
            string path = Path.Combine(directory, "shared.dlrsetup");
            wizard.SetSetupPickers(() => path, () => path);
            await wizard.SaveSetupCommand.ExecuteAsync(null);
            Assert.True(File.Exists(path));
            wizard.SetModel(StructuralHelperAuthoringTests.Source());
            await wizard.LoadSetupCommand.ExecuteAsync(null);
            Assert.NotEmpty(wizard.SetupMappings); Assert.True(wizard.CanPreviewSetup);
            Assert.False(wizard.CanApplySetup);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private sealed class Dialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
