using System.Collections.Immutable;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1SkinDefinitionCodecTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReaderRetainsUnknownSourceAndClassifiesKnownSkinCommands()
    {
        const string source = """
            !include("surface.def")
            Skin("Default")
            {
                Replace("base.mat", "variant.mat")
                Hide("arm")
                Show("cap")
                ShowAll()
                ReplaceSurface("outer", "inner", "")
                FutureCommand("opaque")
            }
            Group()
            {
                UseSkin("Default", 1)
                IncludeSkin("Default")
            }
            """;

        Dl1SkinDefinitionDocument read = Dl1SkinDefinitionCodec.Read(source);

        Assert.True(read.IsValid);
        Assert.True(read.RequiresReview);
        Assert.Equal(source, read.Write());
        Dl1SkinBlock skin = Assert.Single(read.Skins);
        Assert.Equal("Default", skin.Name);
        Assert.Equal(new Dl1SkinMaterialReplacement("base.mat", "variant.mat", 2),
            Assert.Single(skin.MaterialReplacements));
        Assert.Equal(["arm", "cap"], skin.EntityVisibility.Select(static entity => entity.EntityName));
        Assert.True(skin.EntityVisibility[0].Hidden);
        Assert.False(skin.EntityVisibility[1].Hidden);
        Assert.Equal("outer", Assert.Single(skin.SurfaceReplacements).OriginalSurface);
        Assert.Contains("FutureCommand", skin.UnclassifiedCalls);
        Assert.Contains(read.References, static reference => reference.Kind == Dl1SkinReferenceKind.UseSkin &&
            reference.Name == "Default" && reference.SelectionIndex == 1);
        Assert.Contains(read.References, static reference => reference.Kind == Dl1SkinReferenceKind.IncludeSkin);
        Assert.Contains(read.References, static reference => reference.Kind == Dl1SkinReferenceKind.SourceInclude);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void CanonicalWriterRoundTripsSupportedNamedFields()
    {
        var definition = new Dl1SkinGenerationDefinition("Default",
            (ushort)(Dl1SkinSourceFeatures.ShowAll | Dl1SkinSourceFeatures.MaterialReplacements),
            [new("base.mat", "variant.mat")],
            [new("arm", true, 0xC001), new("cap", false, 0x0002)],
            0, 0)
        {
            VerifiedSourceFeatures = Dl1SkinSourceFeatures.ShowAll |
                Dl1SkinSourceFeatures.MaterialReplacements,
            ExpectedTagBytes = [0, 0, 0, 0, 0, 0, 0, 0],
            ExpectedCompiledColors = [0, 0, 0, 255, 0, 0, 0, 255],
            OptionalStringsAndGroupsVerifiedAbsent = true,
        };

        Dl1SkinDefinitionDocument source = Dl1SkinDefinitionCodec.GenerateCanonical([definition], "surface.def");
        Dl1SkinDefinitionDocument reread = Dl1SkinDefinitionCodec.Read(source.Write());

        Assert.True(reread.IsValid);
        Assert.False(reread.RequiresReview);
        Assert.Contains("Replace(\"base.mat\", \"variant.mat\")", source.Write(), StringComparison.Ordinal);
        Assert.Contains("Hide(\"arm\")", source.Write(), StringComparison.Ordinal);
        Assert.Contains("Show(\"cap\")", source.Write(), StringComparison.Ordinal);
        Assert.Equal(definition.VerifiedSourceFeatures!.Value, Assert.Single(reread.Skins).Features);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReviewedNewSkinColorAndSurfaceRowsEmitMatchingSourceCommands()
    {
        var alpha = new Dl1SkinGenerationDefinition("primary", 0x4C0,
            [new("base.mat", "variant.mat")], [], 1, 0)
        {
            VerifiedSourceFeatures = Dl1SkinSourceFeatures.EnableNewSkins |
                Dl1SkinSourceFeatures.MaterialReplacements | Dl1SkinSourceFeatures.Color0Alpha,
            VerifiedEnableNewSkins = true,
            ExpectedTagBytes = [0, 0, 0, 0, 0, 0, 0, 0],
            ExpectedCompiledColors = [0, 0, 0, 15, 0, 0, 0, 255],
            OptionalStringsAndGroupsVerifiedAbsent = true,
            VerifiedSurfaceReplacements = [new("Water", "Flesh", "", 3, 10, 0)],
        };
        var rgb = new Dl1SkinGenerationDefinition("secondary", 0x540, [], [], 0, 0)
        {
            VerifiedSourceFeatures = Dl1SkinSourceFeatures.EnableNewSkins |
                Dl1SkinSourceFeatures.Color0Rgb | Dl1SkinSourceFeatures.Color0Alpha,
            VerifiedEnableNewSkins = true,
            ExpectedTagBytes = [0, 0, 0, 0, 0, 0, 0, 0],
            ExpectedCompiledColors = [3, 2, 1, 15, 0, 0, 0, 255],
            OptionalStringsAndGroupsVerifiedAbsent = true,
        };

        Dl1SkinDefinitionDocument written = Dl1SkinDefinitionCodec.GenerateCanonical([alpha, rgb]);

        Assert.Equal(1, written.Write().Split("EnableNewSkins()", StringSplitOptions.None).Length - 1);
        Assert.Contains("ColorAlpha(0, 15)", written.Write(), StringComparison.Ordinal);
        Assert.Contains("ColorIA(0, 1, 2, 3, 15)", written.Write(), StringComparison.Ordinal);
        Assert.Contains("ReplaceSurface(\"Water\", \"Flesh\", \"\")", written.Write(), StringComparison.Ordinal);
        Assert.Equal(2, written.Skins.Length);
        Assert.Single(written.Skins[0].SurfaceReplacements);
        Assert.False(written.RequiresReview);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void CanonicalWriterRejectsUnknownOrOpaqueCompiledFields()
    {
        var valid = new Dl1SkinGenerationDefinition("Default", 1, [], [], 0, 0)
        {
            VerifiedSourceFeatures = Dl1SkinSourceFeatures.ShowAll,
            ExpectedTagBytes = [0, 0, 0, 0, 0, 0, 0, 0],
            ExpectedCompiledColors = [0, 0, 0, 255, 0, 0, 0, 255],
            OptionalStringsAndGroupsVerifiedAbsent = true,
        };

        Assert.Throws<InvalidDataException>(() => Dl1SkinDefinitionCodec.GenerateCanonical(
            [valid with { RawFeatures = 0x4000 }]));
        Assert.Throws<InvalidDataException>(() => Dl1SkinDefinitionCodec.GenerateCanonical(
            [valid with { RawFeatures = 0x0081 }]));
        Assert.Throws<InvalidDataException>(() => Dl1SkinDefinitionCodec.GenerateCanonical(
            [valid with { SurfaceOverrideCount = 1 }]));
        Assert.Throws<InvalidDataException>(() => Dl1SkinDefinitionCodec.GenerateCanonical(
            [valid with { RandomizedChildCount = 1 }]));
        Assert.Throws<InvalidDataException>(() => Dl1SkinDefinitionCodec.GenerateCanonical(
            [valid with { EntityVisibility = [new("arm", true, 0x8001)] }]));
        Assert.Throws<InvalidDataException>(() => Dl1SkinDefinitionCodec.GenerateCanonical(
            [valid with { MaterialReplacements = [new("base.mat", "variant.mat")],
                VerifiedSourceFeatures = Dl1SkinSourceFeatures.None }]));
    }
}
