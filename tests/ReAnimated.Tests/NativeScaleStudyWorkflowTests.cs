using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;

namespace ReAnimated.Tests;

public sealed class NativeScaleStudyWorkflowTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public async Task SourceTrialsAreExplicitAndSavedApartFromTheControl()
    {
        string source=Path.Combine(_directory,"control.pre"), output=Path.Combine(_directory,"study.pre");
        await File.WriteAllTextAsync(source,Dl1HumanAiScaleExperimentsTests.Source);
        byte[] before=await File.ReadAllBytesAsync(source);
        using var vm=new NativeScaleStudyViewModel(new Dialogs(source,output));
        Assert.False(vm.CanPrepare); Assert.False(vm.CanSave);
        await vm.LoadSourceCommand.ExecuteAsync(null);
        Assert.Equal("control",vm.SelectedPreset);Assert.True(vm.CanPrepare);
        await vm.PrepareCommand.ExecuteAsync(null);
        Assert.True(vm.CanSave,vm.Status);Assert.Equal(3,vm.Trials.Count);
        Assert.Contains(vm.ControlFields,f=>f.Name=="MeshName"&&f.LiteralValue=="generic_actor.msh");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(File.Exists(output),vm.Status);Assert.True(File.Exists(output+".study.json"));
        Assert.Equal(before,await File.ReadAllBytesAsync(source));
        Assert.Contains("unverified",await File.ReadAllTextAsync(output+".study.json"),StringComparison.Ordinal);
        vm.FirstScale=.75;Assert.False(vm.CanSave);Assert.Empty(vm.Trials);
        vm.Dispose();Assert.False(vm.CanPrepare);Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task ChangedSourceOrExistingDestinationCannotBeOverwritten()
    {
        string source=Path.Combine(_directory,"control.pre"),output=Path.Combine(_directory,"study.pre");
        await File.WriteAllTextAsync(source,Dl1HumanAiScaleExperimentsTests.Source);
        using var vm=new NativeScaleStudyViewModel(new Dialogs(source,output));
        await vm.LoadSourceCommand.ExecuteAsync(null);await vm.PrepareCommand.ExecuteAsync(null);
        await File.AppendAllTextAsync(source,"\n// later user edit");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(File.Exists(output));Assert.Contains("changed on disk",vm.Status,StringComparison.Ordinal);
        await vm.LoadSourceCommand.ExecuteAsync(null);await vm.PrepareCommand.ExecuteAsync(null);
        await File.WriteAllTextAsync(output,"existing user file");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("existing user file",await File.ReadAllTextAsync(output));
        Assert.False(File.Exists(output+".study.json"));
    }

    private sealed class Dialogs(string source,string output):IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath)=>null;
        public string? ShowSaveProjectDialog(string suggestedName,string? currentPath)=>null;
        public string? ShowOpenScaleStudySourceDialog()=>source;
        public string? ShowSaveScaleStudyDialog()=>output;
    }
    public void Dispose()=>RpackTestData.DeleteTemporaryDirectory(_directory);
}
