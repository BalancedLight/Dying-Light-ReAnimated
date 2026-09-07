using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1ClothCodecTests
{
    private static readonly double[] ExpectedStiffness = [700, 300];
    private const string Source = """
        // An independent synthetic fixture, never a retail character extract.
        !include("MeshPartCloth.def")
        MeshPartCloth() {
          BonesGridSize(1, 3)
          $DIST(f, -1)
          Bone(0, 0, "anchor", 1, DIST, DIST)
          Bone(0, 1, "tail", 0, DIST, DIST)
          Bone(0, 2, "", 0, DIST, DIST)
          StructuralStiffness(700, 300)
          AnimationMoveMul(0.6)
          CollisionSphereShift("anchor", [0.1, 0, 0], 0.2)
          FutureNativeCall("retain // this", [1, 2, 3])
        }
        """;

    [Fact]
    public void ParsesGridMacrosAndParametersWithoutGuessingCollisionCenters()
    {
        NativePhxDocument parsed = Dl1ClothCodec.ReadPhx(Source, ["anchor", "tail"]);
        Assert.True(parsed.IsValid);
        Assert.Equal(3, parsed.Nodes.Length);
        Assert.Equal(-1, parsed.Nodes[1].DownDistance);
        Assert.Equal(ExpectedStiffness, parsed.Parameters["StructuralStiffness"]);
        Assert.Contains(parsed.Diagnostics, d => d.Code == "native_collision_bounds_required");
        Assert.Contains(parsed.Diagnostics, d => d.Code == "native_virtual_endpoint");
        Assert.Contains(parsed.Diagnostics, d => d.Code == "native_statement_unsupported");
        Assert.Equal(Source, parsed.Syntax.Write());
    }

    [Fact]
    public void EditingOneCommandPreservesUnknownSourceAndCommentsByteForByte()
    {
        var syntax = Dl1ClothCodec.Parse(Source);
        int index = syntax.Commands.IndexOf(syntax.Commands.Single(c => c.Name == "AnimationMoveMul"));
        string changed = syntax.ReplaceCommand(index, "AnimationMoveMul(0.25)").Write();
        Assert.Equal(Source.Replace("AnimationMoveMul(0.6)", "AnimationMoveMul(0.25)", StringComparison.Ordinal), changed);
        Assert.Throws<ArgumentException>(() => syntax.ReplaceCommand(index, "One() Two()"));
    }

    [Fact]
    public void AuthoringRoundTripsNativeCoordinatesAndBindingFlags()
    {
        string phx = Dl1ClothCodec.WritePhx(1, 2,
            [new(0, 0, "root", 1, -1, -1), new(0, 1, "tip", 0, -1, -1)],
            ["GravityMul(0.5)", "Mass(1)", "CollisionCapsuleBetween(\"root\", 0, \"tip\", 0, 0.1)"]);
        NativePhxDocument parsed = Dl1ClothCodec.ReadPhx(phx, ["root", "tip"]);
        Assert.True(parsed.IsValid);
        Assert.Equal(0.5, Assert.Single(parsed.Parameters["GravityMul"]));
        string binding = Dl1ClothCodec.WriteMpCloth([new("fabric.phx", 1, 0), new("strand.phx", 1, 1)]);
        NativeMpClothDocument reopened = Dl1ClothCodec.ReadMpCloth(binding, ["fabric.phx", "strand.phx"]);
        Assert.True(reopened.IsValid);
        Assert.Equal(1, reopened.Bindings[1].Flag);
        Assert.Equal(binding, reopened.Syntax.Write());
    }

    [Fact]
    public void UnknownBindingsSurviveAndMissingResourcesAreExplicitErrors()
    {
        const string text = "MeshPartCloth(\"missing.phx\", 1, 0)\nUnrecognized(7)\n";
        var parsed = Dl1ClothCodec.ReadMpCloth(text, ["available.phx"]);
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Diagnostics, d => d.Code == "native_binding_invalid");
        Assert.Contains(parsed.Diagnostics, d => d.Code == "native_statement_unsupported");
        Assert.Equal(text, parsed.Syntax.Write());
    }

    [Fact]
    public void IncompleteDuplicateUnboundAndUnresolvedGridsFailValidation()
    {
        Assert.False(Dl1ClothCodec.ReadPhx(Source.Replace("BonesGridSize(1, 3)", "BonesGridSize(2, 3)", StringComparison.Ordinal)).IsValid);
        Assert.False(Dl1ClothCodec.ReadPhx(Source.Replace("Bone(0, 1", "Bone(0, 0", StringComparison.Ordinal)).IsValid);
        Assert.False(Dl1ClothCodec.ReadPhx(Source, ["anchor"]).IsValid);
        Assert.False(Dl1ClothCodec.ReadPhx(Source.Replace("$DIST(f, -1)", "$DIST(f, unknown)", StringComparison.Ordinal)).IsValid);
    }

    [Fact]
    public void ClipSeamRetainsSourceAndTargetCellsAndCompletesWrappedGrid()
    {
        string text = Dl1ClothCodec.WritePhx(3, 2,
            [new(0, 0, "left_root", 1, -1, -1), new(0, 1, "left_tip", 0, -1, -1),
             new(1, 0, "right_root", 1, -1, -1), new(1, 1, "right_tip", 0, -1, -1)],
            ["Clip(2, 0, 0, 0, -1, -1)", "Clip(2, 1, 0, 1, -1, -1)"]);
        NativePhxDocument parsed = Dl1ClothCodec.ReadPhx(text);
        Assert.True(parsed.IsValid);
        Assert.Equal(new NativeClothClip(2, 1, 0, 1, -1, -1), parsed.Clips[1]);
        Assert.Equal(text, parsed.Syntax.Write());
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Code == "native_statement_unsupported");
    }

    [Theory]
    [InlineData("Clip(1, 0, 1, 0, -1, -1)", "Clip(2, 0, 0, 0, -1, -1)")]
    [InlineData("Clip(1, 0, 3, 0, -1, -1)", "Clip(2, 0, 0, 0, -1, -1)")]
    [InlineData("Clip(1, 0, 2, 0, -1, -1)", "Clip(2, 0, 1, 0, -1, -1)")]
    [InlineData("Clip(1, 0, 0, 0, -1, -1)", "Clip(1, 0, 0, 0, -1, -1)")]
    public void ClipSelfLinksOutOfBoundsCyclesAndDuplicatesFail(string first, string second)
    {
        string text = "MeshPartCloth() { BonesGridSize(3, 1) Bone(0, 0, \"root\", 1, -1, -1) " + first + " " + second + " }";
        Assert.False(Dl1ClothCodec.ReadPhx(text).IsValid);
    }

    [Theory]
    [InlineData("MeshPartCloth(\"bad)")]
    [InlineData("MeshPartCloth() { Bone(1, 2)")]
    [InlineData("/* missing end")]
    [InlineData("MeshPartCloth() }")]
    public void MalformedSyntaxIsRejected(string text) => Assert.Throws<FormatException>(() => Dl1ClothCodec.Parse(text));
}
