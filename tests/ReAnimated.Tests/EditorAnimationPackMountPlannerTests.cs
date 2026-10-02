using System.Buffers.Binary;
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
        Assert.False(plan.ReplacesUnownedPack);
        Assert.Equal(2, plan.PreservedResourceCount);
        string mergedPath = Path.Combine(_root, "merged.rpack");
        await File.WriteAllBytesAsync(mergedPath, plan.Payload);
        Rp6lAnimationLibrary merged = await Rp6lAnimationLibraryCodec.ExtractAsync(mergedPath);
        Assert.Equal(["new_clip", "old_clip"], merged.Animations.Keys.Order().ToArray());
        Assert.Equal(["new_bank", "old_bank"], merged.AnimationScripts.Keys.Order().ToArray());
    }

    [Fact]
    public async Task ExplicitReplacementMergesUnownedCanonicalPackAndRetainsInventory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        byte[] oldPack = BuildPack("old_clip", "old_bank", "OLD"u8.ToArray());
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "data", "common_anims_sp_PC.rpack"), oldPack);
        var animations = new Dictionary<string, byte[]> { ["new_clip"] = "NEW"u8.ToArray() };
        var scripts = new Dictionary<string, Rp6lAnimationScript>
        {
            ["new_bank"] = new("HEADER"u8.ToArray(), "BODY"u8.ToArray()),
        };
        byte[] candidate = Rp6lAnimationLibraryCodec.Build(animations, scripts);

        EditorAnimationPackMountPlan plan = await EditorAnimationPackMountPlanner.PrepareAsync(
            _root,
            Guid.NewGuid(),
            candidate,
            animations,
            scripts,
            allowUnownedReplacement: true);

        Assert.False(plan.ReplacesOwnedPack);
        Assert.True(plan.ReplacesUnownedPack);
        Assert.Equal(2, plan.PreservedResourceCount);
        string mergedPath = Path.Combine(_root, "merged-explicit.rpack");
        await File.WriteAllBytesAsync(mergedPath, plan.Payload);
        Rp6lAnimationLibrary merged = await Rp6lAnimationLibraryCodec.ExtractAsync(mergedPath);
        Assert.Equal(["new_clip", "old_clip"], merged.Animations.Keys.Order().ToArray());
        Assert.Equal(["new_bank", "old_bank"], merged.AnimationScripts.Keys.Order().ToArray());
    }

    [Fact]
    public async Task ExplicitReplacementRefusesUnknownOrMalformedExistingPack()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "data", "common_anims_sp_PC.rpack"),
            [0x52, 0x50, 0x36, 0x4C, 0x01, 0x02, 0x03]);
        byte[] candidate = BuildPack("new_clip", "new_bank", "NEW"u8.ToArray());

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EditorAnimationPackMountPlanner.PrepareAsync(
                _root,
                Guid.NewGuid(),
                candidate,
                new Dictionary<string, byte[]> { ["new_clip"] = "NEW"u8.ToArray() },
                new Dictionary<string, Rp6lAnimationScript>
                {
                    ["new_bank"] = new("HEADER"u8.ToArray(), "BODY"u8.ToArray()),
                },
                allowUnownedReplacement: true));
        Assert.Contains("canonical animation pack", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [0x52, 0x50, 0x36, 0x4C, 0x01, 0x02, 0x03],
            await File.ReadAllBytesAsync(
                Path.Combine(_root, "data", "common_anims_sp_PC.rpack")));
    }

    [Fact]
    public async Task ExplicitReplacementRefusesStructurallyValidUnknownResourceType()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        byte[] original = BuildPack("old_clip", "old_bank", "OLD"u8.ToArray());
        string path = Path.Combine(_root, "data", "common_anims_sp_PC.rpack");
        byte[] unknown = await MutateAnimationResourceTypeAsync(original, path);
        Assert.NotEqual(original, unknown);

        byte[] candidate = BuildPack("new_clip", "new_bank", "NEW"u8.ToArray());
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EditorAnimationPackMountPlanner.PrepareAsync(
                _root,
                Guid.NewGuid(),
                candidate,
                new Dictionary<string, byte[]> { ["new_clip"] = "NEW"u8.ToArray() },
                new Dictionary<string, Rp6lAnimationScript>
                {
                    ["new_bank"] = new("HEADER"u8.ToArray(), "BODY"u8.ToArray()),
                },
                allowUnownedReplacement: true));

        Assert.Contains("unknown or unpreservable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unknown, await File.ReadAllBytesAsync(path));
    }

    private static async Task<byte[]> MutateAnimationResourceTypeAsync(
        byte[] original,
        string path)
    {
        for (int offset = 0; offset + 2 <= original.Length; offset++)
        {
            if (BinaryPrimitives.ReadInt16LittleEndian(original.AsSpan(offset, 2)) !=
                Rp6lResourceTypes.Animation)
                continue;
            byte[] candidate = original.ToArray();
            BinaryPrimitives.WriteInt16LittleEndian(
                candidate.AsSpan(offset, 2), Rp6lResourceTypes.Texture);
            await File.WriteAllBytesAsync(path, candidate);
            try
            {
                Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
                if (archive.Resources.Any(resource =>
                        resource.ResourceType == Rp6lResourceTypes.Texture))
                    return candidate;
            }
            catch (InvalidDataException)
            {
            }
        }

        await File.WriteAllBytesAsync(path, original);
        throw new InvalidOperationException(
            "The synthetic RP6L fixture did not expose a mutable animation resource descriptor.");
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
