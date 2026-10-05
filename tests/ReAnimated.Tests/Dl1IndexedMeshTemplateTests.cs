using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1IndexedMeshTemplateTests
{
    [Theory]
    [InlineData("piece_xx.msh", "piece_00.msh", "piece_03.msh")]
    [InlineData("piece_XX.msh", "piece_00.msh", "piece_03.msh")]
    [InlineData("first_XX.msh-second_xx.msh", "first_XX.msh-second_00.msh", "first_XX.msh-second_03.msh")]
    [InlineData("piece_xx.msh?skin=alternate", "piece_00.msh", "piece_03.msh")]
    public void ExpansionPreservesTemplateAndCountWithExactIndexedNames(string template, string first, string last)
    {
        var rows = Dl1IndexedMeshTemplate.Expand(template, 4);
        Assert.Equal(4, rows.Length);
        Assert.Equal(first, rows[0].Name);
        Assert.Equal(last, rows[3].Name);
        Assert.Equal(Enumerable.Range(0, 4), rows.Select(row => row.Index));
        Assert.All(rows, row => { Assert.Equal(template, row.Template); Assert.Equal(4, row.Count); });
    }

    [Fact]
    public void ZeroCountDoesNotInvokeTemplateExpansionAndMaximumCountEndsAtNinetyNine()
    {
        Assert.Empty(Dl1IndexedMeshTemplate.Expand("piece_XX.msh", 0));
        Assert.Empty(Dl1IndexedMeshTemplate.Expand("plain.msh", 0));
        Assert.Equal("piece_99.msh", Dl1IndexedMeshTemplate.Expand("piece_XX.msh", 100)[^1].Name);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("1.5")]
    [InlineData("4.0")]
    [InlineData("4e0")]
    [InlineData("NaN")]
    [InlineData("2147483648")]
    public void InvalidCountLiteralsAreExplicitlyRejected(string literal) =>
        Assert.Throws<FormatException>(() => Dl1IndexedMeshTemplate.ParseCount(literal));

    [Theory]
    [InlineData("plain.msh")]
    [InlineData("piece_Xx.msh")]
    [InlineData("piece_XX")]
    [InlineData("")]
    [InlineData("piece_XX.msh\n")]
    public void UnsupportedTemplatesCannotBecomeLiteralDependencies(string template) =>
        Assert.Throws<FormatException>(() => Dl1IndexedMeshTemplate.Expand(template, 4));

    [Fact]
    public void BodyReaderKeepsOriginalReferenceAndSourceWhileAddingTypedDependencies()
    {
        const string source = "// keep\r\nDestroyedHeadParts(\"piece_XX.msh?variant=one\", 4) /* original */\r\nFuture(9)\r\n";
        var body = Dl1BodyElementsCodec.Read(source);
        Assert.True(body.IsValid);
        Assert.Equal(source, body.Syntax.Write());
        var declaration = Assert.Single(body.DestroyedHeadPartDeclarations);
        Assert.Equal(0, declaration.CallIndex);
        Assert.Equal("piece_XX.msh?variant=one", declaration.Template);
        Assert.Equal(4, declaration.Count);
        Assert.Equal("piece_XX.msh?variant=one", Assert.Single(body.References).Name);
        Assert.Equal(4, body.IndexedMeshDependencies.Length);
        Assert.All(body.IndexedMeshDependencies, row => Assert.Equal(declaration.CallIndex, row.CallIndex));
        Assert.Equal("piece_00.msh", body.IndexedMeshDependencies[0].Name);
        Assert.Equal("piece_03.msh", body.IndexedMeshDependencies[3].Name);
        Assert.Empty(Dl1BodyElementsCodec.Read("DestroyedHeadParts(\"piece_XX.msh\", 0)").IndexedMeshDependencies);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("1.5")]
    public void InvalidBodyCountsRetainSourceWithDiagnosticsAndNoDerivedDependencies(string literal)
    {
        string source = "DestroyedHeadParts(\"piece_XX.msh\", " + literal + ") // retained\n";
        var body = Dl1BodyElementsCodec.Read(source);
        Assert.False(body.IsValid);
        Assert.Equal(source, body.Syntax.Write());
        Assert.Empty(body.IndexedMeshDependencies);
        Assert.Contains(body.Diagnostics, row => row.Code == "body_statement_invalid" && row.IsError);
    }
}
