using System.Text;
using ReAnimated.Codecs.Fed;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class FedReaderTests
{
    [Fact]
    public void ExpressionBecomesNonDestructiveMappedFacialLayer()
    {
        var document = new FedDocument(
            "face",
            [
                new FedExpression(
                    "HAPPY",
                    [
                        new FedMorphWeight("source_smile", 0.6f),
                        new FedMorphWeight("missing", 0.2f),
                    ]),
            ],
            []);
        var rig = new RigDefinition(
            "npc_male",
            "NPC Male",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity),
            ],
            [
                new MorphChannelDefinition(0, "morph_smile"),
            ]);

        FedLayerBuildResult result = FedDomainAdapter.CreateLayer(
            document,
            "happy",
            rig,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_smile"] = "morph_smile",
            });

        Assert.Equal("FED: HAPPY", result.Layer.Name);
        Assert.Equal(
            MorphEditLayerScope.AuthoredExportable,
            result.Layer.Scope);
        Assert.Equal(
            0.6,
            result.Layer.Tracks[0].Keyframes[0].Value,
            5);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "FED101");
        Assert.False(result.Compatibility.IsComplete);
        Assert.Equal(2, result.Compatibility.SourceWeightCount);
        Assert.Equal(1, result.Compatibility.ResolvedWeightCount);
        Assert.Equal(1, result.Compatibility.ResolvedTargetCount);
        Assert.Equal(
            "missing",
            Assert.Single(
                result.Compatibility
                    .MissingSourceMorphNames));
    }

    [Fact]
    public void AccurateFedApplicationRequiresEverySourceRowToResolve()
    {
        var document = new FedDocument(
            "face",
            [
                new FedExpression(
                    "HAPPY",
                    [
                        new FedMorphWeight("morph_smile", 0.6f),
                        new FedMorphWeight("wrong_family", 0.2f),
                    ]),
            ],
            []);
        var rig = new RigDefinition(
            "npc_male",
            "NPC Male",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity),
            ],
            [
                new MorphChannelDefinition(0, "morph_smile"),
            ]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => FedDomainAdapter.CreateLayer(
                    document,
                    "happy",
                    rig,
                    compatibilityPolicy:
                        FedLayerCompatibilityPolicy
                            .RequireComplete));

        Assert.Contains(
            "resolves 1 of 2 rows",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "wrong_family",
            exception.Message,
            StringComparison.Ordinal);

        FedLayerBuildResult complete =
            FedDomainAdapter.CreateLayer(
                document,
                "happy",
                rig,
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["wrong_family"] = "morph_smile",
                },
                compatibilityPolicy:
                    FedLayerCompatibilityPolicy.RequireComplete);
        Assert.True(complete.Compatibility.IsComplete);
        Assert.Equal(2, complete.Compatibility.ResolvedWeightCount);
        Assert.Equal(1, complete.Compatibility.ResolvedTargetCount);
        Assert.Equal(
            0.8,
            complete.Layer.Tracks[0].Keyframes[0].Value,
            5);
    }

    [Fact]
    public void ReadsStrictExpressionWeights()
    {
        byte[] bytes = BuildFed(
            ("blink", [("morph_l_eye_close", 1.25f), ("morph_r_eye_close", -0.5f)]),
            ("snarl", [("morph_lips_l_up", 0.75f)]));
        using MemoryStream stream = new(bytes);
        FedDocument document = FedReader.Read(stream, "human");
        Assert.Equal(2, document.Expressions.Count);
        Assert.Equal(1.25f, document.Expressions[0].Weights[0].Weight);
        Assert.Equal(-0.5f, document.Expressions[0].Weights[1].Weight);
        Assert.Equal("snarl", document.FindExpression("SNARL")?.Name);
    }

    [Fact]
    public void RejectsTruncationAndTrailingBytesButPreservesDuplicates()
    {
        byte[] valid = BuildFed(
            ("blink", [("eye", 1f)]));
        using MemoryStream truncated = new(valid[..^1]);
        Assert.Throws<EndOfStreamException>(
            () => FedReader.Read(truncated, "truncated"));

        byte[] duplicate = BuildFed(
            ("blink", [("eye", 1f)]),
            ("BLINK", [("eye", 0f)]));
        using MemoryStream duplicateStream = new(duplicate);
        FedDocument duplicates = FedReader.Read(
            duplicateStream,
            "duplicate");
        Assert.Equal("blink", duplicates.FindExpression("blink")?.Name);
        Assert.Contains(
            duplicates.Diagnostics,
            static diagnostic => diagnostic.Code == "FED001");

        using MemoryStream rejectedDuplicateStream = new(duplicate);
        Assert.Throws<InvalidDataException>(
            () => FedReader.Read(
                rejectedDuplicateStream,
                "duplicate",
                new FedLimits
                {
                    RejectDuplicateNames = true,
                }));

        using MemoryStream trailing = new([.. valid, 0xFF]);
        Assert.Throws<InvalidDataException>(
            () => FedReader.Read(trailing, "trailing"));
    }

    private static byte[] BuildFed(
        params (string Name, (string Morph, float Weight)[] Weights)[] expressions)
    {
        using MemoryStream output = new();
        using BinaryWriter writer = new(
            output,
            Encoding.UTF8,
            leaveOpen: true);
        writer.Write(expressions.Length);
        foreach ((string name, (string Morph, float Weight)[] weights) in expressions)
        {
            WriteString(writer, name);
            writer.Write(weights.Length);
            foreach ((string morph, float weight) in weights)
            {
                WriteString(writer, morph);
                writer.Write(weight);
            }
        }

        return output.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
