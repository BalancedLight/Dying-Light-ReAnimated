using System.Collections.Immutable;
using System.Text;
using ReAnimated.Codecs.Fed;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class Dl1NativeCompanionWriterTests
{
    internal const string PhysicsText = "!include(\"MeshPartCloth.def\")\nMeshPartCloth()\n{\n" +
        "    BonesGridSize(1, 2)\n    Bone(0, 0, \"root\", 1, 0, 1)\n" +
        "    Bone(0, 1, \"\", 0, 0, 0)\n    Mode3D(1) // preserve authored native mode\n}\n";

    internal static SecondaryMotionDefinition NativeSetup => new()
    {
        NativeSources =
        [
            new() { Kind = NativeClothSourceKind.Phx, ResourceName = "panel.phx", Text = PhysicsText },
            new() { Kind = NativeClothSourceKind.MpCloth, ResourceName = "old_model.mpcloth",
                Text = "// authored binding flags\nMeshPartCloth(\"panel.phx\", 0, 1)\n" },
        ],
    };

    [Fact]
    public void FacialPresetsKeepNamesSignedWeightsAndCompleteResetRows()
    {
        var model = new CustomModelDocument
        {
            MorphChannels = [new() { Name = "Eye" }, new() { Name = "Mouth" }],
            FacialPresets = new()
            {
                Presets = [new() { Name = "normal", Weights = Weights(("eye", -.25)),
                    SpeechWeights = Weights(("MOUTH", 1.4)) }, new() { Name = "_NONE" }],
            },
        };
        Dl1NativeCompanionBuild result = Dl1NativeCompanionWriter.Build(model, "test_model", []);
        FedDocument fed = FedReader.Read(new MemoryStream(result.Files["test_model.fed"]), "test_model");
        Assert.Equal(["normal", "normal speech", "_NONE"], fed.Expressions.Select(pose => pose.Name));
        Assert.All(fed.Expressions, pose => Assert.Equal(["Eye", "Mouth"], pose.Weights.Select(row => row.MorphName)));
        Assert.Equal(-.25f, fed.Expressions[0].Weights[0].Weight);
        Assert.Equal(0f, fed.Expressions[0].Weights[1].Weight);
        Assert.Equal(0f, fed.Expressions[1].Weights[0].Weight);
        Assert.Equal(1.4f, fed.Expressions[1].Weights[1].Weight);
        Assert.All(fed.Expressions[2].Weights, row => Assert.Equal(0f, row.Weight));
    }

    [Fact]
    public void FacialSuffixCollisionFailsBeforeOutput()
    {
        var model = new CustomModelDocument
        {
            MorphChannels = [new() { Name = "eye" }],
            FacialPresets = new() { Presets = [new() { Name = "normal", SpeechWeights = Weights(("eye", 1)) },
                new() { Name = "normal speech" }] },
        };
        Assert.Throws<InvalidDataException>(() => Dl1NativeCompanionWriter.Build(model, "test_model", []));
    }

    [Fact]
    public void FacialInventoryCanExceed64ButActivePoseCannot()
    {
        var channels = Enumerable.Range(0, 100).Select(index => new CustomModelMorphChannel { Name = "m" + index }).ToImmutableArray();
        var model = new CustomModelDocument { MorphChannels = channels,
            FacialPresets = new() { Presets = [new() { Name = "normal", Weights = Weights(("m0", 1)) }] } };
        Assert.Single(Dl1NativeCompanionWriter.Build(model, "test_model", []).Files);
        model = model with { FacialPresets = new() { Presets = [new() { Name = "over_limit",
            Weights = channels.Take(65).ToImmutableDictionary(channel => channel.Name, _ => 1d) }] } };
        Assert.Throws<InvalidDataException>(() => Dl1NativeCompanionWriter.Build(model, "test_model", []));
    }

    [Fact]
    public void RenamingModelNamespacesPhysicsWithoutChangingCoefficientsOrFlags()
    {
        var model = new CustomModelDocument { SecondaryMotion = NativeSetup };
        Dl1NativeCompanionBuild first = Dl1NativeCompanionWriter.Build(model, "first", ["root"]);
        Dl1NativeCompanionBuild second = Dl1NativeCompanionWriter.Build(model, "second", ["root"]);
        Assert.Equal(PhysicsText, Encoding.UTF8.GetString(first.Files["first_000.phx"]));
        Assert.Equal(first.Files["first_000.phx"], second.Files["second_000.phx"]);
        string wrapper = Encoding.UTF8.GetString(second.Files["second.mpcloth"]);
        Assert.StartsWith("// authored binding flags\n", wrapper, StringComparison.Ordinal);
        NativeClothBinding binding = Assert.Single(Dl1ClothCodec.ReadMpCloth(wrapper, ["second_000.phx"]).Bindings);
        Assert.Equal(0, binding.Enabled);
        Assert.Equal(1, binding.Flag);
        Assert.Contains(second.Notes, note => note.Contains("not re-fitted", StringComparison.Ordinal));
    }

    [Fact]
    public void MultipleBindingsAreRewrittenAndUnknownStatementsRetained()
    {
        var setup = NativeSetup with { NativeSources = [NativeSetup.NativeSources[0],
            NativeSetup.NativeSources[0] with { ResourceName = "strand.phx" },
            NativeSetup.NativeSources[1] with { Text = "MeshPartCloth(\"panel.phx\", 1, 1)\nUnknown(2)\nMeshPartCloth(\"strand.phx\", 1, 0)" }] };
        var built = Dl1NativeCompanionWriter.Build(new() { SecondaryMotion = setup }, "test_model", ["root"]);
        string text = Encoding.UTF8.GetString(built.Files["test_model.mpcloth"]);
        Assert.Contains("Unknown(2)", text, StringComparison.Ordinal);
        Assert.Equal(["test_model_000.phx", "test_model_001.phx"], Dl1ClothCodec.ReadMpCloth(text).Bindings.Select(binding => binding.ResourceName));
    }

    [Theory]
    [InlineData("missing_resource")]
    [InlineData("missing_bone")]
    [InlineData("unpackaged_include")]
    [InlineData("duplicate_binding")]
    [InlineData("missing_wrapper")]
    public void InvalidNativeClosureFails(string scenario)
    {
        var sources = NativeSetup.NativeSources;
        sources = scenario switch
        {
            "missing_resource" => sources.SetItem(1, sources[1] with { Text = "MeshPartCloth(\"missing.phx\", 1, 1)" }),
            "missing_bone" => sources.SetItem(0, sources[0] with { Text = PhysicsText.Replace("\"root\"", "\"missing\"", StringComparison.Ordinal) }),
            "unpackaged_include" => sources.SetItem(0, sources[0] with { Text = PhysicsText + "!include(\"external.phx\")" }),
            "duplicate_binding" => sources.SetItem(1, sources[1] with { Text = sources[1].Text + sources[1].Text }),
            _ => [sources[0]],
        };
        Assert.Throws<InvalidDataException>(() => Dl1NativeCompanionWriter.Build(
            new() { SecondaryMotion = NativeSetup with { NativeSources = sources } }, "test_model", ["root"]));
    }

    [Fact]
    public void PreviewOnlySetupIsExplicitAndDoesNotGenerateNativeCoefficients()
    {
        var setup = new SecondaryMotionDefinition { Groups = [new() { Name = "strand",
            Particles = [new() { ReferenceBoneName = "root", Fixed = true }] }] };
        var result = Dl1NativeCompanionWriter.Build(new() { SecondaryMotion = setup }, "test_model", ["root"]);
        Assert.Empty(result.Files);
        Assert.Contains(result.Notes, note => note.Contains("preview settings only", StringComparison.Ordinal));
    }

    private static ImmutableDictionary<string, double> Weights(params (string Name, double Value)[] entries) =>
        entries.ToImmutableDictionary(entry => entry.Name, entry => entry.Value);
}
