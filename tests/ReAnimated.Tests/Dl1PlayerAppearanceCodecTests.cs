using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1PlayerAppearanceCodecTests
{
    private const string Source = """
        // Character("fake") { Appearance("x", "x", "fake") {} }
        sub appearances()
        {
            Character("hero") {
                Appearance("head", "body", "selected") {
                    MeshFpp("old_fpp.msh"); // preserve comment
                    MeshTpp("old_tpp.msh");
                    Skin("old_skin");
                    Default(); AvailableOnStart(); FutureSetting("old_fpp.msh");
                }
                Appearance("head", "other", "unselected") {
                    MeshFpp("other_fpp.msh"); MeshTpp("other_tpp.msh"); Skin("other_skin");
                }
            }
            Character("second") {
                Appearance("head", "body", "selected") {
                    MeshFpp("second_fpp.msh"); MeshTpp("second_tpp.msh"); Skin("second_skin");
                }
            }
        }
        """;

    [Fact]
    public void BindingPreservesUnselectedAppearancesFlagsCommentsAndUnknownCalls()
    {
        string source = Source.Replace("\n", "\r\n", StringComparison.Ordinal);
        var read = Dl1PlayerAppearanceCodec.Read(source);
        Assert.Equal(3, read.Appearances.Length);
        var changed = read.Bind("hero", "selected", "new_fpp.msh", "new_tpp.msh", "new_skin");
        string expected = source.Replace("MeshFpp(\"old_fpp.msh\")", "MeshFpp(\"new_fpp.msh\")", StringComparison.Ordinal)
            .Replace("MeshTpp(\"old_tpp.msh\")", "MeshTpp(\"new_tpp.msh\")", StringComparison.Ordinal)
            .Replace("Skin(\"old_skin\")", "Skin(\"new_skin\")", StringComparison.Ordinal);
        Assert.Equal(expected, changed.Syntax.Write());
        Assert.Equal(expected, changed.Bind("hero", "selected", "new_fpp.msh", "new_tpp.msh", "new_skin").Syntax.Write());
        Assert.Equal(source, read.Syntax.Write());
    }

    [Theory]
    [InlineData("Appearance(\"head\", \"body\", \"selected\") { MeshFpp(\"f.msh\"); MeshTpp(\"t.msh\"); Skin(\"s\"); }")]
    [InlineData("Character(\"hero\") { Appearance(\"h\", \"b\", \"a\") { MeshFpp(\"f.msh\"); MeshFpp(\"f.msh\"); MeshTpp(\"t.msh\"); Skin(\"s\"); } }")]
    [InlineData("Character(\"hero\") { Appearance(\"h\", \"b\", \"a\") { MeshFpp(\"f.msh\"); MeshTpp(\"t.msh\"); } }")]
    public void InvalidOrAmbiguousBindingsAreRejected(string text) =>
        Assert.Throws<InvalidDataException>(() => Dl1PlayerAppearanceCodec.Read(text));

    [Fact]
    public void MissingSelectionAndInvalidResourceCannotRewriteSource()
    {
        var read = Dl1PlayerAppearanceCodec.Read(Source);
        Assert.Throws<InvalidDataException>(() => read.Bind("hero", "absent", "f.msh", "t.msh", "default"));
        Assert.Throws<ArgumentException>(() => read.Bind("hero", "selected", "../f.msh", "t.msh", "default"));
        Assert.Throws<ArgumentException>(() => read.Bind("hero", "selected", "f.rpack", "t.msh", "default"));
        Assert.Equal(Source, read.Syntax.Write());
    }

    [Fact]
    public void ReadsSourceAvailabilityAndOnlyMatchingUnlockCalls()
    {
        const string source = """
            Character("hero") {
                Appearance("head", "default", "look") {
                    MeshFpp("f.msh"); MeshTpp("t.msh"); Skin("s");
                    Default(); AvailableOnStart(); AvailableOnPrologue(true); UnknownCondition();
                }
                Appearance("head", "training", "training") {
                    MeshFpp("f2.msh"); MeshTpp("t2.msh"); Skin("s2");
                }
            }
            sub unlock() {
                PlayerLevel("Status", 3, "hero", "look");
                PlayerLevel("Status", 0, "hero", "training");
                Chapter(4, "hero", "look");
                Item("starter", "hero", "training");
                UnknownCondition("hero", "look", "preserve");
            }
            """;

        Dl1PlayerAppearanceDocument document =
            Dl1PlayerAppearanceCodec.Read(source);
        Dl1PlayerAppearance selected =
            Assert.Single(document.Appearances.Where(row =>
            row.AppearanceId == "look"));

        Assert.True(selected.Availability.AvailableOnStart);
        Assert.True(selected.Availability.AvailableOnPrologue);
        Assert.True(selected.Availability.IsDefault);
        Assert.Equal(
            ["PlayerLevel", "Chapter", "UnknownCondition"],
            selected.Availability.Unlocks.Select(unlock => unlock.Name));
        Assert.Contains(
            selected.Availability.Unlocks,
            unlock => unlock.Name == "PlayerLevel" && unlock.Arguments.Contains("3"));
        Dl1PlayerAppearance training = Assert.Single(document.Appearances.Where(row =>
            row.AppearanceId == "training"));
        Assert.Contains(
            training.Availability.Unlocks,
            unlock => unlock.Name == "PlayerLevel" && unlock.Arguments.Contains("0"));
        string rewritten = document.Bind(
            "hero",
            "look",
            "new_fpp.msh",
            "new_tpp.msh",
            "new_skin").Syntax.Write();
        Assert.Contains("UnknownCondition()", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailabilityFlagsRequireRecognizedNativeArguments()
    {
        const string source = """
            Character("hero") {
                Appearance("h", "b", "true") {
                    MeshFpp("f.msh"); MeshTpp("t.msh"); Skin("s");
                    AvailableOnStart(); AvailableOnPrologue(true);
                }
                Appearance("h", "b", "false") {
                    MeshFpp("f2.msh"); MeshTpp("t2.msh"); Skin("s2");
                    AvailableOnStart(false); AvailableOnPrologue(false);
                }
                Appearance("h", "b", "malformed") {
                    MeshFpp("f3.msh"); MeshTpp("t3.msh"); Skin("s3");
                    AvailableOnStart(true); Default(true); AvailableOnPrologue(); AvailableOnPrologue("maybe");
                }
            }
            """;

        Dl1PlayerAppearance[] appearances =
            Dl1PlayerAppearanceCodec.Read(source).Appearances.ToArray();
        Assert.True(appearances[0].Availability.AvailableOnStart);
        Assert.True(appearances[0].Availability.AvailableOnPrologue);
        Assert.False(appearances[1].Availability.AvailableOnStart);
        Assert.False(appearances[1].Availability.AvailableOnPrologue);
        Assert.False(appearances[2].Availability.AvailableOnStart);
        Assert.False(appearances[2].Availability.IsDefault);
        Assert.False(appearances[2].Availability.AvailableOnPrologue);
    }
}
