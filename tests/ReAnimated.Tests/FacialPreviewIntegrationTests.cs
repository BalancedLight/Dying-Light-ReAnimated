using System.Collections.Immutable;
using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fed;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FacialPreviewIntegrationTests
{
    [Fact]
    public async Task DesktopHooksInitializePreviewAndSpeechWithoutAuthoringLayers()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using Dl1AssetWorkspace assets = new(Path.Combine(directory, "assets.sqlite3"), Path.Combine(directory, "cache"));
            await using MainWindowViewModel vm = new(new JsonWorkspaceStateStore(Path.Combine(directory, "state.json")), new NoDialogs(), assets);
            Assert.NotNull(vm.PreviewFedExpressionCommand);
            Assert.NotNull(vm.LoadSpeechExchangeCommand);
            Assert.NotNull(vm.ExportFacialPresetsCommand);
            RigDefinition rig = new("face", "Face", [new BoneDefinition(0, "root", -1, TransformTRS.Identity)],
                [new MorphChannelDefinition(0, "open"), new MorphChannelDefinition(1, "expression"), new MorphChannelDefinition(2, "expression_speech")]);
            typeof(MainWindowViewModel).GetField("_targetRig", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, rig);
            vm.FacialFpp.ReplaceMorphs(rig.MorphChannels.Select(x => new MorphChannelViewModel(x.Name)));
            vm.LoadFacialModelLibrary(new FacialPresetLibrary { Presets = [new FacialPresetDefinition
            {
                Name = "Happy", Weights = ImmutableDictionary<string,double>.Empty.Add("expression", 1),
                SpeechWeights = ImmutableDictionary<string,double>.Empty.Add("expression_speech", 1),
            }] });
            vm.FacialFpp.SelectedExpression = "Happy";
            vm.Timeline.FramesPerSecond = 20;
            vm.LoadSpeechExchange(new SpeechCurveExchange(1, new("line.spb", new string('b',64), 120, 1, 1), 254,
                "sample/curveValueMaximum*maximumWeight", [new("line",4,3,.05,[new("open",.8,[0,254,0])])]));
            vm.Timeline.CurrentFrame = 1;
            MethodInfo update = typeof(MainWindowViewModel).GetMethod("UpdateFacialSpeechPreview", BindingFlags.Instance | BindingFlags.NonPublic)!;
            update.Invoke(vm, null);
            update.Invoke(vm, null);
            Assert.Equal(.8f, vm.FacialFpp.Morphs.Single(x => x.Name == "open").Weight);
            Assert.Equal(1, vm.FacialFpp.Morphs.Single(x => x.Name == "expression_speech").Weight);
            Assert.Equal(0, vm.FacialFpp.Morphs.Single(x => x.Name == "expression").Weight);
            Assert.Equal("Happy", vm.FacialFpp.SelectedExpression);
            Assert.Empty(vm.CurrentProject.Animations);
            vm.EnableSpeechPreview = false;
            Assert.Equal(0, vm.FacialFpp.Morphs.Single(x => x.Name == "open").Weight);
            Assert.Equal(1, vm.FacialFpp.Morphs.Single(x => x.Name == "expression").Weight);
            FacialPresetLibrary library = vm.FacialFpp.FacialLibrary;
            RigDefinition noFace = new("hands", "Hands", [new BoneDefinition(0, "root", -1, TransformTRS.Identity)]);
            typeof(MainWindowViewModel).GetField("_targetRig", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, noFace);
            vm.FacialFpp.ReplaceMorphs([]);
            vm.LoadFacialModelLibrary(null);
            vm.EnableSpeechPreview = true;
            Assert.Contains("no facial targets", vm.SpeechPreviewStatus);
            Assert.Null(typeof(MainWindowViewModel).GetField("_speechPreviewLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));
            typeof(MainWindowViewModel).GetField("_targetRig", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, rig);
            vm.FacialFpp.ReplaceMorphs(rig.MorphChannels.Select(x => new MorphChannelViewModel(x.Name)));
            vm.LoadFacialModelLibrary(library);
            Assert.NotNull(typeof(MainWindowViewModel).GetField("_speechPreviewLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));
            Assert.Equal(.8f, vm.FacialFpp.Morphs.Single(x => x.Name == "open").Weight);
            vm.Timeline.IsPlaying = true;
            vm.Timeline.IsPlaying = false;
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => throw new InvalidOperationException("Unexpected file dialog.");
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => throw new InvalidOperationException("Unexpected file dialog.");
        public void ShowOperationFailure(string title, string summary, string details) => throw new InvalidOperationException(summary);
    }
}
