using System.Collections.Immutable;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1GenericRelicPreloadTests
{
    private const string Body = "// retained\r\nBodyElement(_A,1,0,0.,5.,\"helper\")\r\n" +
        "AddRelics(\"PieceA\",MODE,\"piece.phx\",\"\",[0,0,0],[0,0,0])\r\n" +
        "AddRelicsWithDestroyedChild(\"PieceB\",MODE,\"piece.phx\",\"\",[0,0,0],[0,0,0]) // child\r\n";

    [Fact]
    public void ExplicitFlagSelectsBothGenericVectorsWithoutChangingSourceReferences()
    {
        string source = "ForceGenericRelics() // mode\r\n" + Body;
        var body = Dl1BodyElementsCodec.Read(source);
        var result = Dl1GenericRelicPreload.Select(body, new Dictionary<string, int>(StringComparer.Ordinal) { ["_A"] = 0 });
        Assert.True(body.IsValid);
        Assert.True(body.ForceGenericRelics);
        Assert.Equal(0, Assert.Single(body.ForceGenericRelicsCallIndexes));
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Selections.Length);
        Assert.Equal("piecea.msh", result.Selections[0].Name);
        Assert.Equal("pieceb.msh", result.Selections[1].Name);
        Assert.False(result.Selections[0].WithDestroyedChild);
        Assert.True(result.Selections[1].WithDestroyedChild);
        Assert.All(result.Selections, row =>
        {
            Assert.Equal(Dl1RelicPreloadSelection.Generic, row.Selection);
            Assert.Equal(0, row.BodyElementId);
            Assert.Equal("_A", row.BodyElementToken);
            Assert.Equal(body.Elements[0].CallIndex, row.BodyElementCallIndex);
        });
        Assert.Equal(source, body.Syntax.Write());
        Assert.Equal("PieceA", body.Relics[0].Name);
        Assert.Equal("PieceB", body.Relics[1].Name);
        Assert.DoesNotContain(body.References, row => row.Name.EndsWith(".msh", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericFlagAndIndexedHeadMetadataCoexistWithoutSourceRewrite()
    {
        string source = "ForceGenericRelics()\nDestroyedHeadParts(\"fragment_XX.msh\",4)\n" + Body;
        var body = Dl1BodyElementsCodec.Read(source);
        var result = Dl1GenericRelicPreload.Select(body, new Dictionary<string, int> { ["_A"] = 0 });
        Assert.True(result.IsComplete);
        Assert.Equal(4, Assert.Single(body.DestroyedHeadPartDeclarations).Count);
        Assert.Equal(4, body.IndexedMeshDependencies.Length);
        Assert.Equal("fragment_00.msh", body.IndexedMeshDependencies[0].Name);
        Assert.Equal("fragment_XX.msh", body.References.First(row => row.Kind == NativeCharacterReferenceKind.MeshResource).Name);
        Assert.Equal(source, body.Syntax.Write());
    }

    [Fact]
    public void AbsentFlagKeepsConditionalSelectionsWithNoInventedNames()
    {
        var body = Dl1BodyElementsCodec.Read(Body);
        var result = Dl1GenericRelicPreload.Select(body, new Dictionary<string, int> { ["_A"] = 0 });
        Assert.False(body.ForceGenericRelics);
        Assert.Empty(body.ForceGenericRelicsCallIndexes);
        Assert.All(result.Selections, row => { Assert.Equal(Dl1RelicPreloadSelection.Conditional, row.Selection); Assert.Null(row.Name); });
        Assert.Equal(Body, body.Syntax.Write());
    }

    [Theory]
    [InlineData("ForceGenericRelics(1)")]
    [InlineData("ForceGenericRelics(\"mode\")")]
    public void MalformedFlagCannotSelectGenericDependencies(string flag)
    {
        var body = Dl1BodyElementsCodec.Read(flag + "\n" + Body);
        var result = Dl1GenericRelicPreload.Select(body, new Dictionary<string, int> { ["_A"] = 0 });
        Assert.False(body.IsValid);
        Assert.False(body.ForceGenericRelics);
        Assert.All(result.Selections, row => { Assert.Equal(Dl1RelicPreloadSelection.Unresolved, row.Selection); Assert.Null(row.Name); });
        Assert.Contains(result.Diagnostics, row => row.Code == "body_statement_invalid" && row.IsError);
    }

    [Fact]
    public void FlagCasingIsAcceptedWhileSignatureDeclarationDoesNotActivateIt()
    {
        Assert.True(Dl1BodyElementsCodec.Read("forcegenericrelics()\n" + Body).ForceGenericRelics);
        Assert.False(Dl1BodyElementsCodec.Read("!ForceGenericRelics()\n" + Body).ForceGenericRelics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProvidedExactIdsExcludeOnlyTheFourSkippedSlots(bool forceGeneric)
    {
        string source = forceGeneric ? "ForceGenericRelics()\n" : string.Empty;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (int id in Enumerable.Range(7, 5))
        {
            string token = "_PART" + id;
            source += "BodyElement(" + token + ",1,0,0.,5.,\"helper\")\n" +
                "AddRelics(\"Piece" + id + "\",MODE,\"piece.phx\",\"\",[0,0,0],[0,0,0])\n";
            map.Add(token, id);
        }
        var result = Dl1GenericRelicPreload.Select(Dl1BodyElementsCodec.Read(source), map);
        Assert.Equal(4, result.Selections.Count(row => row.Selection == Dl1RelicPreloadSelection.Excluded));
        Assert.All(result.Selections.Where(row => row.Selection == Dl1RelicPreloadSelection.Excluded), row => Assert.Null(row.Name));
        var included = Assert.Single(result.Selections, row => row.Selection != Dl1RelicPreloadSelection.Excluded);
        Assert.Equal(forceGeneric ? Dl1RelicPreloadSelection.Generic : Dl1RelicPreloadSelection.Conditional, included.Selection);
        Assert.Equal(forceGeneric ? "piece11.msh" : null, included.Name);
    }

    [Fact]
    public void MissingCaseAliasedAndInvalidMappingsKeepUnresolvedCandidates()
    {
        var body = Dl1BodyElementsCodec.Read("ForceGenericRelics()\n" + Body);
        foreach (var map in new IReadOnlyDictionary<string, int>[]
        {
            ImmutableDictionary<string, int>.Empty,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["_a"] = 0 },
            new Dictionary<string, int> { ["_A"] = -1 },
            new Dictionary<string, int> { ["_A"] = 27 },
            new Dictionary<string, int> { ["_A"] = 1, ["_B"] = 1 },
        })
        {
            var result = Dl1GenericRelicPreload.Select(body, map);
            Assert.False(result.IsComplete);
            Assert.NotEmpty(result.Diagnostics);
            Assert.All(result.Selections, row => { Assert.Equal(Dl1RelicPreloadSelection.Unresolved, row.Selection); Assert.Null(row.Name); });
        }
    }

    [Fact]
    public void SymbolReaderUsesExactRequestedDeclarationsAndPreservesUnrelatedEnumsAndText()
    {
        const string source = "// definitions\r\n$_A(i,0) // selected\r\n$_B(i,7)\r\n$OTHER(i,0)\r\nFuture(9)\r\n";
        var result = Dl1BodyElementSymbolMap.Read(source, ["_A", "_B"]);
        Assert.True(result.IsComplete);
        Assert.Equal(source, result.Syntax.Write());
        Assert.Equal(0, result.Symbols["_A"]);
        Assert.Equal(7, result.Symbols["_B"]);
        Assert.Equal(2, result.Symbols.Count);
        Assert.Equal("_A", result.Declarations[0].Symbol);
        Assert.Equal(0, result.Declarations[0].CallIndex);
        Assert.Equal(1, result.Declarations[1].CallIndex);
        Assert.DoesNotContain("OTHER", result.Symbols.Keys);
        var missing = Dl1BodyElementSymbolMap.Read(source, ["_a"]);
        Assert.False(missing.IsComplete);
        Assert.Empty(missing.Symbols);
        Assert.Contains(missing.Diagnostics, row => row.Code == "body_symbol_missing");
    }

    [Theory]
    [InlineData("$_A(f,0)")]
    [InlineData("$_A(i,1.5)")]
    [InlineData("$_A(i,-1)")]
    [InlineData("$_A(i,27)")]
    [InlineData("$_A(i,0,1)")]
    [InlineData("Scope() { $_A(i,0) }")]
    [InlineData("$_A(i,0) $_A(i,1)")]
    public void MalformedOrDuplicateRequiredSymbolsDoNotProduceAMapping(string source)
    {
        var result = Dl1BodyElementSymbolMap.Read(source, ["_A"]);
        Assert.False(result.IsValid);
        Assert.Empty(result.Symbols);
        Assert.Equal(source, result.Syntax.Write());
        Assert.Contains(result.Diagnostics, row => row.IsError);
    }

    [Fact]
    public void CollidingRequestedSymbolIdsAreRemovedWithDiagnostics()
    {
        const string source = "$_A(i,0) $_B(i,0)";
        var result = Dl1BodyElementSymbolMap.Read(source, ["_A", "_B"]);
        Assert.False(result.IsValid);
        Assert.Empty(result.Symbols);
        Assert.Contains(result.Diagnostics, row => row.Code == "body_symbol_id_collision" && row.IsError);
        Assert.Equal(2, result.Declarations.Length);
        Assert.Equal(source, result.Syntax.Write());
    }
}
