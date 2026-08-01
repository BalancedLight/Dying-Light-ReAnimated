using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class MorphEvaluatorTests
{
    [Fact]
    public void Dl1ProfileLimitsDisplayWithoutChangingAuthoredWeights()
    {
        RigDefinition rig = CreateMorphRig(66);
        ImmutableDictionary<string, double> sampled = Enumerable.Range(0, 66)
            .ToImmutableDictionary(
                static index => $"Morph{index:D2}",
                static index => index == 0 ? 0.001 : 1.25,
                StringComparer.OrdinalIgnoreCase);

        MorphEvaluationResult result = MorphEvaluator.Evaluate(
            sampled,
            rig,
            0,
            PreviewProfile.ThirdPersonAuthoring,
            EvaluationPurpose.Preview);

        Assert.Equal(66, result.AuthoredWeights.Count);
        Assert.Equal(64, result.DisplayWeights.Count);
        Assert.Equal(1.25, result.AuthoredWeights["Morph65"]);
        Assert.False(result.DisplayWeights.ContainsKey("Morph00"));
        Assert.False(result.DisplayWeights.ContainsKey("Morph65"));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "morph_runtime_active_limit");
    }

    [Fact]
    public void FedStyleLayerIsNonDestructiveAndPreviewLayerDoesNotExport()
    {
        RigDefinition rig = CreateMorphRig(1);
        var authoredFedLayer = new MorphEditLayer(
            Guid.NewGuid(),
            "FED Happy",
            MorphEditBlendMode.Additive,
            MorphEditLayerScope.AuthoredExportable,
            0.5,
            [
                new MorphEditTrack(
                    "Morph00",
                    [new ScalarKeyframe(0, 0.8)]),
            ]);
        var previewBlink = new MorphEditLayer(
            Guid.NewGuid(),
            "Preview blink",
            MorphEditBlendMode.Override,
            MorphEditLayerScope.PreviewOnly,
            1,
            [
                new MorphEditTrack(
                    "Morph00",
                    [new ScalarKeyframe(0, 1)]),
            ]);

        MorphEvaluationResult preview = MorphEvaluator.Evaluate(
            new Dictionary<string, double>
            {
                ["Morph00"] = 0.1,
            },
            rig,
            0,
            PreviewProfile.ThirdPersonAuthoring,
            EvaluationPurpose.Preview,
            layers: [authoredFedLayer, previewBlink]);
        MorphEvaluationResult export = MorphEvaluator.Evaluate(
            new Dictionary<string, double>
            {
                ["Morph00"] = 0.1,
            },
            rig,
            0,
            PreviewProfile.ThirdPersonAuthoring,
            EvaluationPurpose.Export,
            layers: [authoredFedLayer, previewBlink]);

        Assert.Equal(0.5, preview.AuthoredWeights["Morph00"], 8);
        Assert.Equal(1, preview.DisplayWeights["Morph00"], 8);
        Assert.Equal(0.5, export.AuthoredWeights["Morph00"], 8);
        Assert.Equal(0.5, export.DisplayWeights["Morph00"], 8);
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
