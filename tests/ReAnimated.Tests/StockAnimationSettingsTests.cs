using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class StockAnimationSettingsTests
{
    [Fact]
    public async Task ModelEditorCapturesAndRestoresExplicitStockReferenceChoice()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var assets = new Dl1AssetWorkspace(Path.Combine(root,"assets.sqlite3"),Path.Combine(root,"cache"));
            await using var vm = new MainWindowViewModel(new JsonWorkspaceStateStore(Path.Combine(root,"state.json")),new NoDialogs(),assets);
            var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(),"generic.fbx");
            vm.Models.CommitProjectRestore(new PreparedModelsWorkspaceRestore(model,Path.Combine(root,"model.dlrmodel"),
                new ProjectModelsWorkspaceState { PackageAssetId=Guid.NewGuid(),PreviewMode=ProjectCustomModelPreviewMode.SourceFbx }));
            vm.Models.AnimationScriptAlias = "existing_bank";
            vm.Models.ReferenceExistingAnimationLibrary = true;
            var captured = vm.Models.CaptureProjectSession();
            Assert.True(captured.Model!.Package.Document.BuildSettings.ReferenceExistingAnimationLibrary);
            Assert.Contains("no local animation script",vm.Models.AnimationScriptAliasSummary);
            Assert.DoesNotContain("data/characters/animations/animscripts", vm.Models.DeploymentPathPreview, StringComparison.Ordinal);
            Assert.DoesNotContain("<clip>.anm2", vm.Models.DeploymentPathPreview, StringComparison.Ordinal);
            Assert.Contains("retained compiled model", vm.Models.DeploymentPathPreview, StringComparison.Ordinal);
            vm.Models.ReferenceExistingAnimationLibrary = false;
            vm.Models.RestoreProjectSession(captured);
            Assert.True(vm.Models.ReferenceExistingAnimationLibrary);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath)=>null;
        public string? ShowSaveProjectDialog(string suggestedName,string? currentPath)=>null;
        public void ShowOperationFailure(string title,string summary,string details)=>throw new InvalidOperationException(summary);
    }
}
