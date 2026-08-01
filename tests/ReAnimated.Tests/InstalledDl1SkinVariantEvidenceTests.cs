using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1SkinVariantEvidenceTests
{
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private readonly ITestOutputHelper _output;

    public InstalledDl1SkinVariantEvidenceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 180_000)]
    public async Task DefaultSkinsControlPlayerVisibilityAndMaterialsWhenAvailable()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        Dl1InstalledBuildFingerprint build =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        if (!string.Equals(
                build.BuildFingerprint,
                ValidatedBuildFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine(
                $"Installed skin controls skipped for build {build.BuildFingerprint}.");
            return;
        }

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 128L * 1024 * 1024,
                    MaximumMemoryEntryBytes = 64 * 1024 * 1024,
                    MaximumDiskBytes = 512L * 1024 * 1024,
                });
            (Dl1MeshData Player11, string Player11Hash) player11 =
                await DecodeControlAsync(
                    Path.Combine(
                        install.DataPath,
                        "common_cod_2_PC.rpack"),
                    resourceIndex: 6,
                    "player_11_tpp",
                    "89315c41806d721a0f058e1f21fec0a01e5c13e7343b91139fd0e370ed321b79",
                    cache);
            (Dl1MeshData Player1Fpp, string Player1FppHash)
                player1Fpp = await DecodeControlAsync(
                    Path.Combine(
                        install.DataPath,
                        "common_cod_1_PC.rpack"),
                    resourceIndex: 5,
                    "player_1_fpp",
                    Dl1MeshPreviewAdapter
                        .ValidatedPlayer1FppResourceSha256,
                    cache);
            (Dl1MeshData Player1Tpp, string Player1TppHash)
                player1Tpp = await DecodeControlAsync(
                    Path.Combine(
                        install.DataPath,
                        "common_cod_1_PC.rpack"),
                    resourceIndex: 6,
                    "player_1_tpp",
                    "45dff339c74711f55d21274030eb73ab18aba71c6949ef2352f696a6e6fd3b2e",
                    cache);

            Assert.Equal("Default", player11.Player11.AppliedSkinName);
            Assert.Equal(
                ["mask", "unturned_head"],
                HiddenEntityNames(player11.Player11));
            Assert.Equal(
                "player_6_boots.mat",
                player11.Player11.MaterialSlots[7].DatabaseName);
            Assert.Equal(
                "player_3_hands_tpp.mat",
                player11.Player11.MaterialSlots[8].DatabaseName);
            Assert.Equal(
                "player_legs_a.mat",
                player11.Player11.MaterialSlots[9].DatabaseName);
            Dl1MeshPreviewPayload player11Preview =
                Dl1MeshPreviewAdapter.Convert(
                    player11.Player11,
                    player11.Player11Hash);
            Assert.DoesNotContain(
                player11Preview.Meshes,
                static mesh => mesh.Id.Contains(
                    "/unturned_head/",
                    StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                player11Preview.Meshes,
                static mesh => mesh.Id.Contains(
                    "/mask/",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                player11Preview.Meshes,
                static mesh => mesh.Id.Contains(
                    "/player_4_head/",
                    StringComparison.OrdinalIgnoreCase));

            Assert.Equal(
                "player_5_hands_fpp.mat",
                player1Fpp.Player1Fpp.MaterialSlots[7].DatabaseName);
            Assert.Equal(
                "player_1_glove.mat",
                player1Fpp.Player1Fpp.MaterialSlots[10].DatabaseName);
            Assert.Equal(
                "watch.mat",
                player1Fpp.Player1Fpp.MaterialSlots[18].DatabaseName);
            Assert.Equal(
                "Default",
                player1Fpp.Player1Fpp.AppliedSkinName);
            Assert.Empty(
                player1Fpp.Player1Fpp.SkinHiddenEntityIndexes);
            Dl1MeshSurface flashlight = Assert.Single(
                player1Fpp.Player1Fpp.Surfaces,
                static surface =>
                    surface.Name.Equals(
                        "flashlight",
                        StringComparison.OrdinalIgnoreCase) &&
                    surface.LodIndex == 0);
            Assert.Equal(3_480, flashlight.IndexCount);
            Dl1MeshPreviewPayload player1FppPreview =
                Dl1MeshPreviewAdapter.Convert(
                    player1Fpp.Player1Fpp,
                    player1Fpp.Player1FppHash);
            Assert.DoesNotContain(
                player1FppPreview.Meshes,
                static mesh => mesh.Id.Contains(
                    "/flashlight/",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                player1FppPreview.Diagnostics,
                static diagnostic =>
                    diagnostic.Contains(
                        "stock FPP authoring subset",
                        StringComparison.Ordinal));

            Assert.Equal(
                ["cult_arm_belt", "mask"],
                HiddenEntityNames(player1Tpp.Player1Tpp));
            Assert.Equal(
                "survivor_torso_flashlight.mat",
                player1Tpp.Player1Tpp.MaterialSlots[3].DatabaseName);
            Assert.Equal(
                "player_5_tpp.mat",
                player1Tpp.Player1Tpp.MaterialSlots[9].DatabaseName);
            Assert.Equal(
                "player_eyes.mat",
                player1Tpp.Player1Tpp.MaterialSlots[14].DatabaseName);

            _output.WriteLine(
                "DL1 1.55 Default skin controls decoded exactly: " +
                $"player_11 hidden={string.Join(',', HiddenEntityNames(player11.Player11))}; " +
                $"player_1_fpp hands={player1Fpp.Player1Fpp.MaterialSlots[7].DatabaseName}; " +
                $"player_1_tpp hands={player1Tpp.Player1Tpp.MaterialSlots[9].DatabaseName}.");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static async Task<(Dl1MeshData Mesh, string ResourceHash)>
        DecodeControlAsync(
            string packPath,
            int resourceIndex,
            string expectedName,
            string expectedResourceHash,
            Rp6lChunkCache cache)
    {
        Assert.True(File.Exists(packPath), packPath);
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(packPath);
        Rp6lResourceDescriptor resource =
            archive.Resources[resourceIndex];
        Assert.Equal(expectedName, resource.Name);
        string resourceHash;
        await using (Stream stream =
                     await archive.OpenResourceStreamAsync(
                         resource,
                         cache))
        {
            resourceHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream))
                .ToLowerInvariant();
        }

        Assert.Equal(expectedResourceHash, resourceHash);
        Dl1MeshData mesh =
            await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                resource,
                cache);
        Assert.True(
            mesh.IsStructurallyValid,
            string.Join(
                Environment.NewLine,
                mesh.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        return (mesh, resourceHash);
    }

    private static string[] HiddenEntityNames(Dl1MeshData mesh) =>
        mesh.SkinHiddenEntityIndexes
            .Select(index => mesh.Hierarchy.Entities[index].Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
