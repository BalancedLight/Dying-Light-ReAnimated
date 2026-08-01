using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class Rp6lAnimationLibraryTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-Rp6lTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task AnimationLibraryCreatesExtractsAndAppendsAtomically()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(_temporaryDirectory, "common_anims_sp_pc.rpack");
        byte[] first = Rp6lAnimationLibraryCodec.Build(
            new Dictionary<string, byte[]>
            {
                ["clip_a"] = "ANM2-A"u8.ToArray(),
            },
            new Dictionary<string, Rp6lAnimationScript>
            {
                ["anims_man_all_DLC60"] =
                    new("HEADER-A"u8.ToArray(), "BODY-A"u8.ToArray()),
            });
        await Rp6lAnimationLibraryCodec.WriteAtomicAsync(path, first);

        Rp6lAnimationLibrary initial =
            await Rp6lAnimationLibraryCodec.ExtractAsync(path);

        Assert.Equal("ANM2-A"u8.ToArray(), initial.Animations["clip_a"]);
        Assert.Equal(
            "HEADER-A"u8.ToArray(),
            initial.AnimationScripts["anims_man_all_DLC60"].HeaderSection);
        Assert.Equal(
            "BODY-A"u8.ToArray(),
            initial.AnimationScripts["anims_man_all_DLC60"].BodySection);

        await Rp6lAnimationLibraryCodec.AppendAtomicAsync(
            path,
            path,
            new Dictionary<string, byte[]>
            {
                ["clip_b"] = "ANM2-B"u8.ToArray(),
            },
            new Dictionary<string, Rp6lAnimationScript>
            {
                ["anims_player_dlc60"] =
                    new("HEADER-B"u8.ToArray(), "BODY-B"u8.ToArray()),
            });

        Rp6lAnimationLibrary appended =
            await Rp6lAnimationLibraryCodec.ExtractAsync(path);
        Assert.Equal(["clip_a", "clip_b"], appended.Animations.Keys.Order().ToArray());
        Assert.Equal(
            ["anims_man_all_DLC60", "anims_player_dlc60"],
            appended.AnimationScripts.Keys.Order().ToArray());
        Assert.Empty(Directory.EnumerateFiles(_temporaryDirectory, "*.tmp"));
    }

    [Fact]
    public void AnimationLibraryOutputIsDeterministicAndRejectsUnsafeNames()
    {
        var forward = new Dictionary<string, byte[]>
        {
            ["z_clip"] = [3, 4],
            ["a_clip"] = [1, 2],
        };
        var reverse = new Dictionary<string, byte[]>
        {
            ["a_clip"] = [1, 2],
            ["z_clip"] = [3, 4],
        };

        byte[] first = Rp6lAnimationLibraryCodec.Build(
            forward,
            new Dictionary<string, Rp6lAnimationScript>());
        byte[] second = Rp6lAnimationLibraryCodec.Build(
            reverse,
            new Dictionary<string, Rp6lAnimationScript>());

        Assert.Equal(first, second);
        Assert.Throws<ArgumentException>(() =>
            Rp6lAnimationLibraryCodec.Build(
                new Dictionary<string, byte[]>
                {
                    ["bad\nname"] = [1],
                },
                new Dictionary<string, Rp6lAnimationScript>()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
