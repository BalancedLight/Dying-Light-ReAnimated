using ReAnimated.Codecs.ProjectArtifacts;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class EditorAnimationPackMountPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-EditorPackMount-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExistingUnownedPackIsRejectedBeforeReplacement()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        byte[] oldPack = BuildPack("old_clip", "old_bank", "OLD"u8.ToArray());
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "data", "common_anims_sp_PC.rpack"),
            oldPack);
        byte[] newPack = BuildPack("new_clip", "new_bank", "NEW"u8.ToArray());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EditorAnimationPackMountPlanner.PrepareAsync(
                _root,
                Guid.NewGuid(),
                newPack,
                new Dictionary<string, byte[]> { ["new_clip"] = "NEW"u8.ToArray() },
                new Dictionary<string, Rp6lAnimationScript>
                {
                    ["new_bank"] = new("HEADER"u8.ToArray(), "BODY"u8.ToArray()),
                }));

        Assert.Contains("no matching active receipt", error.Message, StringComparison.Ordinal);
        Assert.Equal(oldPack, await File.ReadAllBytesAsync(
            Path.Combine(_root, "data", "common_anims_sp_PC.rpack")));
    }

    [Fact]
    public async Task OwnedPackRetainsOtherResourcesWhenOneBankChanges()
    {
        Directory.CreateDirectory(_root);
        Guid owner = Guid.NewGuid();
        byte[] oldPack = BuildPack("old_clip", "old_bank", "OLD"u8.ToArray());
        await ProjectArtifactTransactionService.CommitAsync(
            new ProjectArtifactTransactionRequest
            {
                ProjectRoot = _root,
                OwnerProjectId = owner,
                Artifacts =
                [
                    new ProjectArtifactWrite(
                        EditorAnimationPackMountPlanner.RelativePath,
                        oldPack),
                ],
            });
        var replacementAnimations = new Dictionary<string, byte[]>
        {
            ["new_clip"] = "NEW"u8.ToArray(),
        };
        var replacementScripts = new Dictionary<string, Rp6lAnimationScript>
        {
            ["new_bank"] = new("HEADER"u8.ToArray(), "BODY"u8.ToArray()),
        };
        byte[] newPack = Rp6lAnimationLibraryCodec.Build(
            replacementAnimations,
            replacementScripts);

        EditorAnimationPackMountPlan plan =
            await EditorAnimationPackMountPlanner.PrepareAsync(
                _root,
                owner,
                newPack,
                replacementAnimations,
                replacementScripts);

        Assert.True(plan.ReplacesOwnedPack);
        Assert.Equal(2, plan.PreservedResourceCount);
        string mergedPath = Path.Combine(_root, "merged.rpack");
        await File.WriteAllBytesAsync(mergedPath, plan.Payload);
        Rp6lAnimationLibrary merged = await Rp6lAnimationLibraryCodec.ExtractAsync(mergedPath);
        Assert.Equal(["new_clip", "old_clip"], merged.Animations.Keys.Order().ToArray());
        Assert.Equal(["new_bank", "old_bank"], merged.AnimationScripts.Keys.Order().ToArray());
    }

    private static byte[] BuildPack(string clip, string script, byte[] payload) =>
        Rp6lAnimationLibraryCodec.Build(
            new Dictionary<string, byte[]> { [clip] = payload },
            new Dictionary<string, Rp6lAnimationScript>
            {
                [script] = new("HEADER"u8.ToArray(), "BODY"u8.ToArray()),
            });

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
