using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

/// <summary>
/// Pins the reason an exported animation RPack needs no loose <c>.scr</c>
/// beside it.
/// </summary>
/// <remarks>
/// The pack carries every animation script as a compiled type-322 resource, so
/// shipping the text form next to it is dead weight the engine never reads.
/// If this ever stopped being true, the RPack export would be shipping
/// animations whose scripts are missing - so it is asserted rather than
/// assumed.
/// </remarks>
public sealed class AnimationRpackSelfContainmentTests
{
    [Fact]
    public async Task BuiltPackCarriesItsAnimationScriptsAsType322()
    {
        AnimationScrSections sections = AnimationScrCodec.Build(
        [
            new AnimationScrSequence(
                "Thriller_zombie_man_a",
                "Thriller_zombie_man_a.anm2",
                0.0f,
                44.0f,
                30.0f),
        ]);

        byte[] pack = Rp6lAnimationLibraryCodec.Build(
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Thriller_zombie_man_a"] = [1, 2, 3, 4],
            },
            new Dictionary<string, Rp6lAnimationScript>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["anims_man_all_dlc60"] = new(
                    sections.RecordsAndNames,
                    sections.IndexAndNames),
            });

        string path = Path.Combine(
            Path.GetTempPath(),
            $"ReAnimated-rpack-{Guid.NewGuid():N}.rpack");
        await File.WriteAllBytesAsync(path, pack);
        try
        {
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);

            Assert.Contains(
                archive.Resources,
                resource =>
                    resource.ResourceType == Rp6lResourceTypes.Animation &&
                    resource.Name == "Thriller_zombie_man_a");
            Assert.Contains(
                archive.Resources,
                resource =>
                    resource.ResourceType ==
                        Rp6lResourceTypes.AnimationScript &&
                    resource.Name == "anims_man_all_dlc60");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SequenceNamesMustMatchTheirAnimationExactly()
    {
        // The compiled type-322 record holds a name and timing but no
        // animation reference, so the engine resolves a sequence to its
        // type-320 animation purely by name - and the script name table is
        // lowercase-only. A pack whose sequence names do not match its
        // animation names byte for byte builds and installs fine, then lists
        // nothing in the editor, which is exactly how this was missed.
        AnimationScrSections sections = AnimationScrCodec.Build(
        [
            new AnimationScrSequence(
                "thriller",
                "thriller.anm2",
                0.0f,
                44.0f,
                30.0f),
        ]);

        byte[] pack = Rp6lAnimationLibraryCodec.Build(
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["thriller"] = [1, 2, 3, 4],
            },
            new Dictionary<string, Rp6lAnimationScript>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["anims_man_all_dlc60"] = new(
                    sections.RecordsAndNames,
                    sections.IndexAndNames),
            });

        string path = Path.Combine(
            Path.GetTempPath(),
            $"ReAnimated-seqmatch-{Guid.NewGuid():N}.rpack");
        await File.WriteAllBytesAsync(path, pack);
        try
        {
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            string[] animations = [.. archive.Resources
                .Where(static resource =>
                    resource.ResourceType == Rp6lResourceTypes.Animation)
                .Select(static resource => resource.Name)];

            Rp6lAnimationLibrary library =
                await Rp6lAnimationLibraryCodec.ExtractAsync(path);
            foreach ((string _, Rp6lAnimationScript script) in
                     library.AnimationScripts)
            {
                ParsedAnimationScr parsed = AnimationScrCodec.Parse(
                    new AnimationScrSections(
                        script.HeaderSection,
                        script.BodySection));
                foreach (ParsedAnimationScrSequence sequence in
                         parsed.Sequences)
                {
                    Assert.Contains(sequence.Name, animations);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptNameTablesAreLowercaseOnly()
    {
        // This is why exported animation identities are normalised: a
        // mixed-case type-320 name could never be referenced by its script.
        AnimationScrSections sections = AnimationScrCodec.Build(
        [
            new AnimationScrSequence(
                "Thriller_Player",
                "Thriller_Player.anm2",
                0.0f,
                1.0f,
                30.0f),
        ]);

        ParsedAnimationScr parsed = AnimationScrCodec.Parse(sections);

        Assert.Equal(
            "thriller_player",
            Assert.Single(parsed.Sequences).Name);
    }

    [Fact]
    public void AnimationsAndScriptsCannotShareAName()
    {
        // The pack has one flat name space, which is exactly why two rows may
        // not both be exported as the same type-320 identity.
        AnimationScrSections sections = AnimationScrCodec.Build(
        [
            new AnimationScrSequence("clash", "clash.anm2", 0.0f, 1.0f, 30.0f),
        ]);

        Assert.Throws<ArgumentException>(() =>
            Rp6lAnimationLibraryCodec.Build(
                new Dictionary<string, byte[]>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["clash"] = [1, 2, 3, 4],
                },
                new Dictionary<string, Rp6lAnimationScript>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["clash"] = new(
                        sections.RecordsAndNames,
                        sections.IndexAndNames),
                }));
    }
}
