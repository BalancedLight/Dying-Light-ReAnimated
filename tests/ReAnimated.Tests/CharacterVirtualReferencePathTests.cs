using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class CharacterVirtualReferencePathTests
{
    [Theory]
    [InlineData("../../surface.def", "data/characters/actors/hero.scr", "data/surface.def")]
    [InlineData("..\\..\\surface.def", "data/characters/actors/hero.scr", "data/surface.def")]
    [InlineData("./shared.def", "data/characters/hero.scr", "data/characters/shared.def")]
    [InlineData("../shared.def", "data/characters/hero.scr", "data/shared.def")]
    [Trait("ValidationTier", "Hermetic")]
    public void ExplicitRelativeReferencesNormalizeAgainstDeclaringVirtualDirectory(
        string request, string declaring, string expected)
    {
        CharacterVirtualReferencePathResult resolved = CharacterVirtualReferencePath.Resolve(request, declaring);
        Assert.Equal(expected, resolved.CanonicalName);
        Assert.True(resolved.IsRelative);
        Assert.True(resolved.RequiresExactLookup);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void BareFilenameStaysSourceRelativeButMayUseLegacyCandidateFallback()
    {
        CharacterVirtualReferencePathResult resolved = CharacterVirtualReferencePath.Resolve(
            "shared.def", "data/characters/hero.scr");
        Assert.Equal("data/characters/shared.def", resolved.CanonicalName);
        Assert.True(resolved.IsRelative);
        Assert.False(resolved.RequiresExactLookup);
        Assert.Equal("shared.def", CharacterVirtualReferencePath.Resolve("shared.def").CanonicalName);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void PlainSlashPathRemainsRootRelativeForCatalogCompatibility()
    {
        CharacterVirtualReferencePathResult resolved = CharacterVirtualReferencePath.Resolve(
            "data/effects/impact.fx", "data/characters/hero.scr");
        Assert.Equal("data/effects/impact.fx", resolved.CanonicalName);
        Assert.False(resolved.IsRelative);
        Assert.True(resolved.RequiresExactLookup);
    }

    [Theory]
    [InlineData("../../../surface.def", "data/characters/hero.scr")]
    [InlineData("../surface.def", "hero.scr")]
    [InlineData("../../surface.def", null)]
    [InlineData("./", "data/characters/hero.scr")]
    [InlineData("..", "data/characters/hero.scr")]
    [InlineData("data/../surface.def", "data/characters/hero.scr")]
    [Trait("ValidationTier", "Hermetic")]
    public void RootEscapeAndAmbiguousDotSyntaxFailClosed(string request, string? declaring)
    {
        Assert.Throws<InvalidDataException>(() => CharacterVirtualReferencePath.Resolve(request, declaring));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/data/shared.def")]
    [InlineData("\\\\server\\share\\shared.def")]
    [InlineData("C:\\data\\shared.def")]
    [InlineData("data//shared.def")]
    [InlineData("data/shared.def/")]
    [InlineData("data/\tshared.def")]
    [Trait("ValidationTier", "Hermetic")]
    public void AbsoluteEmptyAndMalformedRequestPathsAreRejected(string request)
    {
        Assert.Throws<InvalidDataException>(() => CharacterVirtualReferencePath.Resolve(request,
            "data/characters/hero.scr"));
    }

    [Theory]
    [InlineData("/data/characters/hero.scr")]
    [InlineData("data//characters/hero.scr")]
    [InlineData("data/../characters/hero.scr")]
    [InlineData("C:\\data\\characters\\hero.scr")]
    [Trait("ValidationTier", "Hermetic")]
    public void DeclaringPathMustAlreadyBeCanonical(string declaring)
    {
        Assert.Throws<InvalidDataException>(() => CharacterVirtualReferencePath.Resolve("shared.def", declaring));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void OversizedRequestFailsBeforeConstructingCatalogIdentity()
    {
        Assert.Throws<InvalidDataException>(() => CharacterVirtualReferencePath.Resolve(
            new string('a', 4097), "data/characters/hero.scr"));
    }
}
