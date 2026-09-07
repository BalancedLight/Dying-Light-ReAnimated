using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FacialPresetPreviewTests
{
    private static FacialFppViewModel Create()
    {
        FacialFppViewModel vm = new();
        vm.ReplaceMorphs([new("eye_l"), new("eye_r"), new("expression"), new("speech_expression"), new("open")]);
        vm.LoadFacialLibrary(new()
        {
            Controls = [new() { MorphName = "eye_l", Group = FacialControlGroup.Eyes, PartnerName = "eye_r" },
                new() { MorphName = "eye_r", Group = FacialControlGroup.Eyes, PartnerName = "eye_l" }],
            Presets = [new() { Name = "Smile", Weights = ImmutableDictionary<string,double>.Empty.Add("expression", 1),
                SpeechWeights = ImmutableDictionary<string,double>.Empty.Add("speech_expression", 1) },
                new() { Name = "Neutral" }],
        });
        return vm;
    }

    [Fact]
    public void RepeatedPresetSelectionReplacesRatherThanAccumulates()
    {
        FacialFppViewModel vm = Create();
        int saves = 0;
        vm.FacialLibraryChanged += (_, _) => saves++;
        vm.SelectedExpression = "Smile";
        vm.PreviewExpressionCommand.Execute(null);
        vm.PreviewExpressionCommand.Execute(null);
        Assert.Equal(1, vm.Morphs.Single(x => x.Name == "expression").Weight);
        vm.PresetIntensity = .4;
        Assert.Equal(.4f, vm.Morphs.Single(x => x.Name == "expression").Weight);
        vm.SelectedExpression = "Neutral";
        Assert.All(vm.Morphs, x => Assert.Equal(0, x.Weight));
        Assert.Equal(0, saves);
    }

    [Fact]
    public void LinkedSidesCanBeEditedIndependently()
    {
        FacialFppViewModel vm = Create();
        vm.Morphs[0].Weight = .7f;
        Assert.Equal(.7f, vm.Morphs[1].Weight);
        vm.LinkSides = false;
        vm.Morphs[0].Weight = .2f;
        Assert.Equal(.7f, vm.Morphs[1].Weight);
        vm.SelectedFacialGroup = "Eyes";
        Assert.Equal(2, vm.VisibleMorphs.Count);
    }

    [Fact]
    public void SpeechUsesAuthoredSafePresetThenReturnsToOrdinaryExpression()
    {
        FacialFppViewModel vm = Create();
        vm.SelectedExpression = "Smile";
        vm.SetSpeechPreview(new Dictionary<string,double> { ["open"] = .6 });
        Assert.Equal(0, vm.Morphs.Single(x => x.Name == "expression").Weight);
        Assert.Equal(1, vm.Morphs.Single(x => x.Name == "speech_expression").Weight);
        Assert.Equal(.6f, vm.Morphs.Single(x => x.Name == "open").Weight);
        vm.SetSpeechPreview(null);
        Assert.Equal(1, vm.Morphs.Single(x => x.Name == "expression").Weight);
        Assert.Equal(0, vm.Morphs.Single(x => x.Name == "open").Weight);
        vm.ResetMorphsCommand.Execute(null);
        Assert.All(vm.Morphs, x => Assert.Equal(0, x.Weight));
    }

    [Fact]
    public void SaveIsExplicitAndNotifiesPersistenceOwnerOnce()
    {
        FacialFppViewModel vm = Create();
        vm.Morphs[0].Weight = .3f;
        int saves = 0;
        vm.FacialLibraryChanged += (_, _) => saves++;
        vm.PresetName = "Captured";
        vm.SaveExpression();
        Assert.Equal(1, saves);
        Assert.Contains(vm.FacialLibrary.Presets, x => x.Name == "Captured");
        Assert.Equal(.3f, (float)vm.FacialLibrary.Presets.Single(x => x.Name == "Captured").Weights["eye_l"]);
    }

    [Fact]
    public void BlinkDoesNotChangeArbitraryEyeShapeWhenBlinkIsMissing()
    {
        FacialFppViewModel vm = Create();
        vm.PreviewBlinkCommand.Execute(null);
        Assert.All(vm.Morphs, x => Assert.Equal(0, x.Weight));
    }

    [Fact]
    public void LibraryRejectsMissingAndNonfiniteTargets()
    {
        FacialPresetLibrary missing = new() { Presets = [new() { Name = "A", Weights = ImmutableDictionary<string,double>.Empty.Add("missing", 1) }] };
        Assert.Throws<ArgumentException>(() => missing.Validate(["present"]));
        Assert.Throws<ArgumentException>(() => (missing with { Presets = [new() { Name = "A", Weights = ImmutableDictionary<string,double>.Empty.Add("present", double.NaN) }] }).Validate(["present"]));
    }

    [Fact]
    public void EvaluatedAnimationDoesNotClearPresetSelectionOrMirrorSides()
    {
        FacialFppViewModel vm = Create();
        vm.SelectedExpression = "Smile";
        vm.SynchronizeEvaluatedWeights(new Dictionary<string, double> { ["eye_l"] = .1, ["eye_r"] = .7 });
        Assert.Equal("Smile", vm.SelectedExpression);
        Assert.Equal(.1f, vm.Morphs[0].Weight);
        Assert.Equal(.7f, vm.Morphs[1].Weight);
        vm.RefreshActiveExpressionPreview();
        Assert.Equal(1, vm.Morphs.Single(x => x.Name == "expression").Weight);
    }

    [Fact]
    public void ExternalPresetReplacesSelectionAndReleaseFollowsAuthoredAnimation()
    {
        FacialFppViewModel vm = Create();
        vm.SelectedExpression = "Smile";
        vm.PreviewWeights(new Dictionary<string,double> { ["eye_l"] = .4 });
        Assert.Null(vm.SelectedExpression);
        vm.RefreshActiveExpressionPreview();
        Assert.Equal(0, vm.Morphs.Single(x => x.Name == "expression").Weight);
        vm.ReleasePreviewOverrides();
        vm.SynchronizeEvaluatedWeights(new Dictionary<string,double> { ["eye_l"] = .8 });
        vm.SetSpeechPreview(new Dictionary<string,double> { ["open"] = .5 });
        Assert.Equal(.8f, vm.Morphs[0].Weight);
        Assert.Equal(.5f, vm.Morphs.Single(x => x.Name == "open").Weight);
    }

    [Fact]
    public void SameImmutableModelKeepsWorkingPresetsAndPoseAcrossFreshControlRows()
    {
        FacialFppViewModel vm = Create();
        FacialPresetLibrary source = vm.FacialLibrary;
        vm.LoadModelFacialLibrary(source, "model/source/package-a");
        vm.SelectedExpression = "Smile";
        vm.PresetName = "Working pose";
        vm.SaveExpression();
        vm.ReplaceMorphs(vm.Morphs.Select(x => new MorphChannelViewModel(x.Name)).ToArray());
        vm.LoadModelFacialLibrary(source with { }, "model/source/package-a");
        Assert.Contains(vm.FacialLibrary.Presets, x => x.Name == "Working pose");
        Assert.Equal("Working pose", vm.SelectedExpression);
        Assert.Equal(1, vm.Morphs.Single(x => x.Name == "expression").Weight);
        vm.LoadModelFacialLibrary(source, "model/source/package-b");
        Assert.DoesNotContain(vm.FacialLibrary.Presets, x => x.Name == "Working pose");
        Assert.Null(vm.SelectedExpression);
    }
}
