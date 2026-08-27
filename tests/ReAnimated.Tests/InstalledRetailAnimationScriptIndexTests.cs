using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Scripts;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledRetailAnimationScriptIndexTests
{
    private const string RunEnvironmentVariable =
        "DLR_RUN_INSTALLED_ANIMATION_SCRIPT_INDEX";

    private readonly ITestOutputHelper _output;

    public InstalledRetailAnimationScriptIndexTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Gate", "DL1InstalledAnimationScriptIndex")]
    public void InstalledStockAnimationScriptTreeIsIndexedWithItsIncludeGraph()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RunEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"NOT EXERCISED: set {RunEnvironmentVariable}=1.");
            return;
        }

        Dl1InstallLocation install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static candidate => candidate.IsValid)
            ?? throw new InvalidOperationException(
                "No complete Steam Dying Light 1 installation was discovered.");

        Dl1RetailAnimationScriptIndex index =
            Dl1RetailAnimationScriptIndex.Build(install.InstallPath);

        _output.WriteLine(
            $"scripts={index.Scripts.Length} roots={index.Roots.Length} " +
            $"seqTracks={index.Scripts.Sum(script => script.SeqTrackCount)}");

        // The stock tree is substantial; a handful of entries would mean the
        // prefix filter stopped matching.
        Assert.True(
            index.Scripts.Length >= 190,
            $"Expected the stock animscript tree; found {index.Scripts.Length}.");
        Assert.True(
            index.Scripts.Sum(script => script.SeqTrackCount) >= 10_000,
            "Expected the stock SeqTrack corpus to parse.");

        // The scripts a character pack is registered against.
        Assert.True(index.Contains("anims_player"));
        Assert.True(index.Contains("anims_man_all"));
        Assert.True(index.Contains("anims_man_zombie"));

        // anims_man_all comments out its Anims_Player include and pulls in the
        // zombie tree instead - the include parser has to reflect both.
        Assert.True(
            index.TryGet("anims_man_all", out Dl1RetailAnimationScript manAll));
        Assert.Contains(
            "Anims_Man_Zombie",
            manAll.ScriptIncludes,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Anims_Player",
            manAll.ScriptIncludes,
            StringComparer.OrdinalIgnoreCase);

        string[] closure = [.. index.ResolveIncludeClosure("anims_man_all")];
        Assert.Contains(
            "anims_man_all",
            closure,
            StringComparer.OrdinalIgnoreCase);
        Assert.True(
            closure.Length > 5,
            "Expected anims_man_all to reach a real include closure.");

        // Stock scripts carry the event rows the binary writer cannot encode.
        Assert.Contains(index.Scripts, script => script.HasEventBlocks);

        // anims_man_all is not a root: the combined anims_player_man_all
        // includes it. That is exactly why the picker ranks by the curated
        // PreferredRoots list first rather than by the include graph alone.
        Assert.Contains(
            index.Roots,
            script => string.Equals(
                script.ResourceName,
                "anims_player_man_all",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            index.Roots,
            script => string.Equals(
                script.ResourceName,
                "anims_man_all",
                StringComparison.OrdinalIgnoreCase));

        // Every stock script must parse; a file this parser gives up on would
        // silently report zero SeqTracks.
        Assert.Equal(
            11_699,
            index.Scripts.Sum(script => script.SeqTrackCount));
    }

    [Fact]
    [Trait("Gate", "DL1InstalledAnimationScriptIndex")]
    public void SuggestedScriptsForKnownFamiliesExistInTheStockTree()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RunEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"NOT EXERCISED: set {RunEnvironmentVariable}=1.");
            return;
        }

        Dl1InstallLocation install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static candidate => candidate.IsValid)
            ?? throw new InvalidOperationException(
                "No complete Steam Dying Light 1 installation was discovered.");
        Dl1RetailAnimationScriptIndex index =
            Dl1RetailAnimationScriptIndex.Build(install.InstallPath);

        // A suggestion naming a script that does not ship would be worse than
        // no suggestion at all.
        foreach (Dl1RigFamily family in Enum.GetValues<Dl1RigFamily>())
        {
            Dl1AnimationScriptSuggestion? suggestion =
                Dl1AnimationScriptFamilyDefaults.Suggest(
                    family,
                    Dl1MeshPerspective.ThirdPerson);
            if (suggestion is null)
            {
                continue;
            }

            Assert.True(
                index.Contains(suggestion.ResourceName),
                $"{family} suggests '{suggestion.ResourceName}', which is not installed.");
        }

        foreach (string root in Dl1AnimationScriptFamilyDefaults.PreferredRoots)
        {
            Assert.True(
                index.Contains(root),
                $"Preferred root '{root}' is not installed.");
        }
    }

    [Fact]
    public void UnclassifiedFamiliesGetNoSuggestion()
    {
        Assert.Null(Dl1AnimationScriptFamilyDefaults.Suggest(
            Dl1RigFamily.Unknown,
            Dl1MeshPerspective.Unknown));
    }

    [Fact]
    public void SuggestionsStateTheyAreNotDerivedFromRetailData()
    {
        Dl1AnimationScriptSuggestion suggestion = Assert.IsType<
            Dl1AnimationScriptSuggestion>(
            Dl1AnimationScriptFamilyDefaults.Suggest(
                Dl1RigFamily.Player,
                Dl1MeshPerspective.FirstPerson));

        Assert.Equal("anims_player", suggestion.ResourceName);
        Assert.Contains(
            "Suggested",
            suggestion.Basis,
            StringComparison.Ordinal);
        Assert.Contains(
            "Retail data does not record",
            suggestion.Basis,
            StringComparison.Ordinal);
    }
}
