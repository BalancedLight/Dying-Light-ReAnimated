using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class AnimationScriptDependencyReaderTests
{
    [Fact]
    public void RetainsLiteralAndDynamicDependenciesWithoutReadingCommentsOrStringsAsCode()
    {
        const string source = """
            // !include("ignored.scr")
            /* AnimScriptAlias("also_ignored") */
            Label("AnimScriptAlias(\"ignored\")")
            !include("body.scr")
            !include(ChooseScript("night", "day"))
            AnimScriptAlias("generic_bank")
            AnimScriptAlias(BANK_NAME)
            """;
        var dependencies = AnimationScriptDependencyReader.Read(source);
        Assert.Equal(4, dependencies.Length);
        Assert.Equal("body.scr", dependencies[0].LiteralName);
        Assert.Null(dependencies[1].LiteralName);
        Assert.Equal("ChooseScript(\"night\", \"day\")", dependencies[1].ArgumentText);
        Assert.Equal("generic_bank", dependencies[2].LiteralName);
        Assert.Equal(AnimationSourceDependencyKind.AnimationScriptAlias, dependencies[2].Kind);
        Assert.Null(dependencies[3].LiteralName);
    }

    [Theory]
    [InlineData("!include(\"missing.scr\"")]
    [InlineData("!include \"missing.scr\"")]
    [InlineData("AnimScriptAlias(\"unterminated)")]
    [InlineData("/* !include(\"hidden.scr\")")]
    public void MalformedDependencyCallsFailExplicitly(string source) =>
        Assert.Throws<InvalidDataException>(() => AnimationScriptDependencyReader.Read(source));

    [Fact]
    public void ExpressionsWithLiteralsAreNotReducedToOneGuessedDependency()
    {
        var dependencies = AnimationScriptDependencyReader.Read("!include(\"base\" + SUFFIX)\nAnimScriptAlias(\"one\", \"two\")");
        Assert.All(dependencies, d => Assert.Null(d.LiteralName));
        Assert.Equal(2, dependencies.Length);
    }
}
