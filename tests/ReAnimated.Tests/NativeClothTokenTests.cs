using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class NativeClothTokenTests
{
    [Fact]
    public void RenamesOnlySelectedLiteralAndPreservesTriviaAndNestedArguments()
    {
        const string text = "// \\\"old\\\"\r\nUnknown( Nested(\"old\", 7), /* \"old\", */ [1,2], \"old\")";
        var syntax = Dl1ClothCodec.Parse(text);
        var tokens = syntax.Commands.Single().QuotedArguments;
        Assert.Equal([0, 2], tokens.Select(t => t.ArgumentIndex));
        var updated = syntax.ReplaceQuotedArguments(new Dictionary<NativeClothQuotedArgument, string> { [tokens[1]] = "new" });
        Assert.Equal(text[..tokens[1].Start] + "\"new\"" + text[(tokens[1].Start + tokens[1].Length)..], updated.Write());
        Assert.Equal(text, syntax.Write());
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("slash\\quote\"combined")]
    [InlineData("\u03a9")]
    public void ReplacementEscapesAndReparsesLiteral(string value)
    {
        var syntax = Dl1ClothCodec.Parse("Unknown(\"old\")");
        var token = syntax.Commands.Single().QuotedArguments.Single();
        var updated = syntax.ReplaceQuotedArguments(new Dictionary<NativeClothQuotedArgument, string> { [token] = value });
        Assert.Equal(value, updated.Commands.Single().QuotedArguments.Single().Value);
    }

    [Fact]
    public void StaleOrForgedTokensCannotChangeUnrelatedText()
    {
        var syntax = Dl1ClothCodec.Parse("Unknown(\"old\")");
        var token = syntax.Commands.Single().QuotedArguments.Single();
        Assert.Throws<ArgumentException>(() => syntax.ReplaceQuotedArguments(
            new Dictionary<NativeClothQuotedArgument, string> { [token with { Start = 0 }] = "new" }));
        Assert.Throws<ArgumentException>(() => syntax.ReplaceQuotedArguments(
            new Dictionary<NativeClothQuotedArgument, string> { [token with { Value = "another" }] = "new" }));
    }

    [Fact]
    public void ConcatenatedNativeBoneExpressionIsNotMistakenForLiteral()
    {
        const string text = "MeshPartCloth(){BonesGridSize(1,1) Bone(0,0,\"root\" + \"suffix\",1,0,0)}";
        var parsed = Dl1ClothCodec.ReadPhx(text);
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Diagnostics, d => d.Code == "native_statement_invalid" && d.IsError);
    }

    [Fact]
    public void EmptyReplacementIsAnExactNoOp()
    {
        var syntax = Dl1ClothCodec.Parse("/* unchanged */ Unknown(\"old\")\r\n");
        Assert.Same(syntax, syntax.ReplaceQuotedArguments(new Dictionary<NativeClothQuotedArgument, string>()));
    }
}
