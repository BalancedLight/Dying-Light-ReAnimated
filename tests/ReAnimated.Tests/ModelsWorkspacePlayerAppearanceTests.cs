using System.Text;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspacePlayerAppearanceTests
{
    private const string AppearanceSource = "sub appearances() { Character(\"hero\") { Appearance(\"h\", \"b\", \"first\") { MeshFpp(\"old_fpp.msh\"); MeshTpp(\"old_tpp.msh\"); Skin(\"default\"); Default(); AvailableOnStart(); AvailableOnPrologue(true); } Appearance(\"h\", \"b\", \"second\") { MeshFpp(\"other_fpp.msh\"); MeshTpp(\"other_tpp.msh\"); Skin(\"default\"); } } sub unlock() { PlayerLevel(\"Status\", 3, \"hero\", \"first\"); PlayerLevel(\"Status\", 0, \"hero\", \"second\"); UnknownCondition(\"hero\", \"first\", \"preserve\"); } }\r\n";

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task UiSelectionExportsOneAppearanceAndPreservesSource()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string source = Path.Combine(root, "source.scr");
            string output = Path.Combine(root, "bound.scr");
            await File.WriteAllTextAsync(source, AppearanceSource, new UTF8Encoding(true));
            var dialogs = new AppearanceDialogs(source, output);
            using var viewModel = await CreateModelAsync(root, dialogs);
            byte[] sourceBytes = await File.ReadAllBytesAsync(source);
            viewModel.ResourceName = "character";
            viewModel.OpenPlayerAppearanceCommand.Execute(null);
            Assert.Equal(2, viewModel.PlayerAppearanceChoices.Count);
            Assert.Null(viewModel.SelectedPlayerAppearance);
            Assert.False(viewModel.ExportPlayerAppearanceCommand.CanExecute(null));
            viewModel.SelectedPlayerAppearance = viewModel.PlayerAppearanceChoices[0];
            Assert.True(viewModel.ExportPlayerAppearanceCommand.CanExecute(null));
            Assert.Contains("AvailableOnStart", viewModel.PlayerAppearanceBindingSummary, StringComparison.Ordinal);
            Assert.Contains("AvailableOnPrologue", viewModel.PlayerAppearanceBindingSummary, StringComparison.Ordinal);
            Assert.Contains("Default", viewModel.PlayerAppearanceBindingSummary, StringComparison.Ordinal);
            Assert.Contains("PlayerLevel", viewModel.PlayerAppearanceBindingSummary, StringComparison.Ordinal);
            Assert.Contains("Status level 3", viewModel.PlayerAppearanceBindingSummary, StringComparison.Ordinal);
            Assert.Contains("does not select or equip", viewModel.PlayerAppearanceBindingSummary, StringComparison.Ordinal);
            viewModel.SelectedPlayerAppearance = viewModel.PlayerAppearanceChoices[1];
            Assert.Contains("Status level 0", viewModel.PlayerAppearanceBindingSummary, StringComparison.Ordinal);
            viewModel.SelectedPlayerAppearance = viewModel.PlayerAppearanceChoices[0];
            viewModel.ExportPlayerAppearanceCommand.Execute(null);
            Assert.Contains("Loaded 2 appearance(s) from source.scr", viewModel.PlayerAppearanceSourceStatus, StringComparison.Ordinal);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
            byte[] outputBytes = await File.ReadAllBytesAsync(output);
            Assert.True(outputBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            string expected = AppearanceSource.Replace("MeshFpp(\"old_fpp.msh\")", "MeshFpp(\"character_fpp.msh\")", StringComparison.Ordinal)
                .Replace("MeshTpp(\"old_tpp.msh\")", "MeshTpp(\"character_tpp.msh\")", StringComparison.Ordinal);
            Assert.Equal(expected, await File.ReadAllTextAsync(output));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task PlayerAppearanceReadFailureRetainsLocalDiagnostic()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string source = Path.Combine(root, "invalid.scr");
            await File.WriteAllTextAsync(source, "Character(\"broken\") {");
            var dialogs = new AppearanceDialogs(source, Path.Combine(root, "out.scr"));
            using var viewModel = await CreateModelAsync(root, dialogs);

            viewModel.OpenPlayerAppearanceCommand.Execute(null);

            Assert.Empty(viewModel.PlayerAppearanceChoices);
            Assert.Contains("could not be opened", viewModel.PlayerAppearanceSourceStatus, StringComparison.Ordinal);
            Assert.Contains("could not be opened", viewModel.BuildStatus, StringComparison.Ordinal);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task PlayerAppearanceCancellationAndModelClearUpdateLocalStatus()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var dialogs = new AppearanceDialogs(null, Path.Combine(root, "out.scr"));
            using var viewModel = await CreateModelAsync(root, dialogs);

            viewModel.OpenPlayerAppearanceCommand.Execute(null);
            Assert.Contains("selection canceled", viewModel.PlayerAppearanceSourceStatus, StringComparison.Ordinal);

            viewModel.ClearProjectSession();
            Assert.Equal(
                "Open the current Player appearance script to inspect available appearances.",
                viewModel.PlayerAppearanceSourceStatus);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task UiExportRejectsChangedSourceAndExistingDestination()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string source = Path.Combine(root, "source.scr");
            string output = Path.Combine(root, "bound.scr");
            await File.WriteAllTextAsync(source, AppearanceSource);
            var dialogs = new AppearanceDialogs(source, output);
            using var viewModel = await CreateModelAsync(root, dialogs);
            viewModel.OpenPlayerAppearanceCommand.Execute(null);
            viewModel.SelectedPlayerAppearance = viewModel.PlayerAppearanceChoices[0];
            await File.AppendAllTextAsync(source, "// another author changed the source\r\n");
            viewModel.ExportPlayerAppearanceCommand.Execute(null);
            Assert.False(File.Exists(output));
            Assert.Contains("source appearance script changed", viewModel.BuildStatus, StringComparison.Ordinal);
            viewModel.OpenPlayerAppearanceCommand.Execute(null);
            viewModel.SelectedPlayerAppearance = viewModel.PlayerAppearanceChoices[0];
            await File.WriteAllTextAsync(output, "preserve existing output");
            viewModel.ExportPlayerAppearanceCommand.Execute(null);
            Assert.Equal("preserve existing output", await File.ReadAllTextAsync(output));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }

    private static async Task<ModelsWorkspaceViewModel> CreateModelAsync(string root, AppearanceDialogs dialogs)
    {
        string modelPath = Path.Combine(root, "generic.fbx");
        await File.WriteAllBytesAsync(modelPath, BlenderFbxStrictValidationTests.CreateValidModelFixture());
        var viewModel = new ModelsWorkspaceViewModel(dialogs, static _ => { }, static _ => Task.CompletedTask, static () => null);
        await viewModel.ImportPathAsync(modelPath, CustomModelRigMode.ExactFbxRig);
        Assert.True(viewModel.HasModel);
        return viewModel;
    }

    private sealed class AppearanceDialogs(string? source, string output) : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public string? ShowOpenPlayerAppearanceScriptDialog(string? initialPath) => source;
        public string? ShowSavePlayerAppearanceScriptDialog(string suggestedName, string? initialPath) => output;
    }
}
