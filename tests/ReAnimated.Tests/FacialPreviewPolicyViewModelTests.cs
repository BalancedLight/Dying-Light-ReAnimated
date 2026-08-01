using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class FacialPreviewPolicyViewModelTests
{
    [Fact]
    public void LiveSlidersApplyDl1DisplayPolicyWithoutChangingAuthoredValues()
    {
        MorphChannelViewModel[] sliders = Enumerable.Range(0, 66)
            .Select(index => new MorphChannelViewModel($"Morph{index:D2}")
            {
                Weight = index == 0 ? 0.001f : 1.25f,
            })
            .ToArray();
        RigDefinition rig = CreateMorphRig(66);

        MorphWeight[] display =
            MainWindowViewModel.CreatePreviewMorphWeights(
                sliders,
                rig,
                PreviewProfile.ThirdPersonAuthoring,
                0);

        Assert.Equal(64, display.Length);
        Assert.DoesNotContain(
            display,
            static weight => weight.Name == "Morph00");
        Assert.DoesNotContain(
            display,
            static weight => weight.Name == "Morph65");
        Assert.Equal(0.001f, sliders[0].Weight);
        Assert.Equal(1.25f, sliders[^1].Weight);
    }

    [Fact]
    public void LiveSlidersRemainUnmodifiedInRawPreview()
    {
        var slider = new MorphChannelViewModel("Morph00")
        {
            Weight = 1.25f,
        };
        RigDefinition rig = CreateMorphRig(1);

        MorphWeight display = Assert.Single(
            MainWindowViewModel.CreatePreviewMorphWeights(
                [slider],
                rig,
                PreviewProfile.RawAuthoring,
                0));

        Assert.Equal(1.25f, display.Weight);
        Assert.Equal(1.25f, slider.Weight);
    }

    private static RigDefinition CreateMorphRig(int count) =>
        new(
            "face",
            "Face",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity,
                    descriptorHash: 1),
            ],
            Enumerable.Range(0, count).Select(index =>
                new MorphChannelDefinition(
                    index,
                    $"Morph{index:D2}",
                    checked((uint)(1000 + index)))));
}
