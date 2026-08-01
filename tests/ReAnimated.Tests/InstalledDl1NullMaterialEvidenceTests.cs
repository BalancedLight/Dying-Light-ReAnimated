using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Materials;
using ReAnimated.DL1.Assets.Meshes;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1NullMaterialEvidenceTests
{
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private readonly ITestOutputHelper _output;

    public InstalledDl1NullMaterialEvidenceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 180_000)]
    [Trait("Category", "Installed")]
    public async Task InstalledNullMaterialsPublishNoPreviewDraws()
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
                $"Installed null-material controls skipped for build {build.BuildFingerprint}.");
            return;
        }

        string materialPack = Path.Combine(
            install.DataPath,
            "optimized_dx11.mp");
        Assert.True(File.Exists(materialPack), materialPack);
        await using (Dl1MaterialPackReader materialReader =
                     await Dl1MaterialPackReader.OpenAsync(
                         materialPack))
        {
            Dl1MaterialPackMaterialRecord nullMaterial =
                Assert.IsType<Dl1MaterialPackMaterialRecord>(
                    await materialReader.ReadMaterialAsync(
                        "null.mat"));
            Dl1MaterialPackMaterialRecord defaultMaterial =
                Assert.IsType<Dl1MaterialPackMaterialRecord>(
                    await materialReader.ReadMaterialAsync(
                        "default.mat"));
            Dl1MaterialPackMaterialRecord shadowCaster =
                Assert.IsType<Dl1MaterialPackMaterialRecord>(
                    await materialReader.ReadMaterialAsync(
                        "shadow_caster.mat"));

            Assert.Equal((ushort)0, nullMaterial.TechniqueCount);
            Assert.Empty(nullMaterial.Textures);
            Assert.Equal((ushort)0, defaultMaterial.TechniqueCount);
            Assert.Empty(defaultMaterial.Textures);
            Assert.Equal((ushort)1, shadowCaster.TechniqueCount);
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
            Dl1MeshPreviewPayload player1Fpp =
                await DecodePreviewAsync(
                    Path.Combine(
                        install.DataPath,
                        "common_cod_1_PC.rpack"),
                    5,
                    "player_1_fpp",
                    Dl1MeshPreviewAdapter
                        .ValidatedPlayer1FppResourceSha256,
                    cache);
            Dl1MeshPreviewPayload player1Tpp =
                await DecodePreviewAsync(
                    Path.Combine(
                        install.DataPath,
                        "common_cod_1_PC.rpack"),
                    6,
                    "player_1_tpp",
                    "45dff339c74711f55d21274030eb73ab18aba71c6949ef2352f696a6e6fd3b2e",
                    cache);
            Dl1MeshPreviewPayload player11Fpp =
                await DecodePreviewAsync(
                    Path.Combine(
                        install.DataPath,
                        "common_cod_2_PC.rpack"),
                    5,
                    "player_11_fpp",
                    "f5d67276cc9ce20be70767cdb1bf2fc357b74415dbe063462710663b89c5d363",
                    cache);
            Dl1MeshPreviewPayload player11Tpp =
                await DecodePreviewAsync(
                    Path.Combine(
                        install.DataPath,
                        "common_cod_2_PC.rpack"),
                    6,
                    "player_11_tpp",
                    "89315c41806d721a0f058e1f21fec0a01e5c13e7343b91139fd0e370ed321b79",
                    cache);
            Dl1MeshData door =
                await DecodeMeshAsync(
                    Path.Combine(
                        install.DataPath,
                        "common_meshes_PC.rpack"),
                    70,
                    "anim_slums_door_a",
                    "2bdeabeda4b3d6fd6b8408dcf4a26d4f96b536862e05d6a7fbf103ab6ce8f848",
                    cache);

            AssertNullDrawOmitted(
                player1Fpp,
                "player_1_fpp/player_1_hand_l_fpp_decal/lod0/part0");
            AssertNullDrawOmitted(
                player1Fpp,
                "player_1_fpp/watch/lod0/part2");
            AssertNullDrawOmitted(
                player1Tpp,
                "player_1_tpp/player_4_head/lod0/part1");
            AssertNullDrawOmitted(
                player1Tpp,
                "player_1_tpp/watch/lod0/part2");
            AssertNullDrawOmitted(
                player11Fpp,
                "player_11_fpp/watch/lod0/part2");
            AssertNullDrawOmitted(
                player11Tpp,
                "player_11_tpp/player_4_head/lod0/part1");
            AssertNullDrawOmitted(
                player11Tpp,
                "player_11_tpp/watch/lod0/part2");

            AssertDoorProxyPartsOmitted(door);

            _output.WriteLine(
                "DL1 1.55 null.mat has zero techniques; exact player head/decal/watch parts and the door's 740-triangle null proxy publish no preview draw.");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static void AssertNullDrawOmitted(
        Dl1MeshPreviewPayload preview,
        string renderId)
    {
        Assert.DoesNotContain(
            preview.Meshes,
            mesh => mesh.Id.Equals(
                renderId,
                StringComparison.Ordinal));
        string surfaceName =
            renderId.Split('/')[1];
        Assert.Contains(
            preview.Diagnostics,
            diagnostic =>
                diagnostic.Contains(
                    $"Surface '{surfaceName}'",
                    StringComparison.Ordinal) &&
                diagnostic.Contains(
                    "null.mat",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertDoorProxyPartsOmitted(
        Dl1MeshData door)
    {
        Dl1MaterialSlot nullProxySlot =
            Assert.Single(
                door.MaterialSlots,
                static slot => slot.Index == 0);
        Assert.Equal("null.mat", nullProxySlot.DatabaseName);
        Assert.Equal(
            "METAL_DOOR_BB.MAT",
            nullProxySlot.DeclaredDatabaseName);
        Assert.Equal(
            4,
            nullProxySlot.SkinReplacementDatabaseEntryIndex);
        Assert.True(
            Dl1PreviewMaterialPolicy.IsNonDisplayMaterial(
                nullProxySlot));

        Dl1MaterialSlot shadowProxySlot =
            Assert.Single(
                door.MaterialSlots,
                static slot => slot.Index == 1);
        Assert.Equal(
            "shadow_caster.mat",
            shadowProxySlot.DatabaseName);
        Assert.True(
            Dl1PreviewMaterialPolicy.IsNonDisplayMaterial(
                shadowProxySlot));

        Dl1MaterialSlot slabSlot =
            Assert.Single(
                door.MaterialSlots,
                static slot => slot.Index == 2);
        Assert.Equal(
            "metal_door_b.mat",
            slabSlot.DatabaseName);
        Assert.False(
            Dl1PreviewMaterialPolicy.IsNonDisplayMaterial(
                slabSlot));

        Dl1MaterialSlot handleSlot =
            Assert.Single(
                door.MaterialSlots,
                static slot => slot.Index == 3);
        Assert.Equal(
            "metal_door_a.mat",
            handleSlot.DatabaseName);
        Assert.False(
            Dl1PreviewMaterialPolicy.IsNonDisplayMaterial(
                handleSlot));

        Dl1MeshSurface surface =
            Assert.Single(
                door.Surfaces,
                static item =>
                    item.Name == "metal_door_a" &&
                    item.LodIndex == 0);
        Assert.Collection(
            surface.Submeshes,
            part =>
            {
                Assert.Equal(0, part.Index);
                Assert.Equal(0, part.MaterialSlotIndex);
                Assert.Equal(740, part.IndexCount / 3);
            },
            part =>
            {
                Assert.Equal(1, part.Index);
                Assert.Equal(1, part.MaterialSlotIndex);
                Assert.Equal(4, part.IndexCount / 3);
            },
            part =>
            {
                Assert.Equal(2, part.Index);
                Assert.Equal(2, part.MaterialSlotIndex);
                Assert.Equal(364, part.IndexCount / 3);
            },
            part =>
            {
                Assert.Equal(3, part.Index);
                Assert.Equal(3, part.MaterialSlotIndex);
                Assert.Equal(32, part.IndexCount / 3);
            });

        Dl1MeshPreviewPayload preview =
            Dl1MeshPreviewAdapter.Convert(
                door,
                "2bdeabeda4b3d6fd6b8408dcf4a26d4f96b536862e05d6a7fbf103ab6ce8f848");
        Assert.DoesNotContain(
            preview.Meshes,
            static mesh =>
                mesh.Id.EndsWith(
                    "/part0",
                    StringComparison.Ordinal) ||
                mesh.Id.EndsWith(
                    "/part1",
                    StringComparison.Ordinal));
        Assert.Contains(
            preview.Meshes,
            static mesh =>
                mesh.Id.EndsWith(
                    "/part2",
                    StringComparison.Ordinal));
        Assert.Contains(
            preview.Meshes,
            static mesh =>
                mesh.Id.EndsWith(
                    "/part3",
                    StringComparison.Ordinal));
        Assert.Equal(
            396,
            preview.Meshes.Sum(
                static mesh => mesh.Indices.Length / 3));
        Assert.Contains(
            preview.Diagnostics,
            static diagnostic =>
                diagnostic.Contains(
                    "Surface 'metal_door_a' submesh 0",
                    StringComparison.Ordinal) &&
                diagnostic.Contains(
                    "null.mat",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<Dl1MeshPreviewPayload>
        DecodePreviewAsync(
            string packPath,
            int resourceIndex,
            string expectedName,
            string expectedResourceSha256,
            Rp6lChunkCache cache)
    {
        Assert.True(File.Exists(packPath), packPath);
        Rp6lArchive archive =
            await Rp6lArchive.OpenAsync(packPath);
        Rp6lResourceDescriptor resource =
            archive.Resources[resourceIndex];
        Assert.Equal(expectedName, resource.Name);
        string resourceSha256;
        await using (Stream stream =
                     await archive.OpenResourceStreamAsync(
                         resource,
                         cache))
        {
            resourceSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream))
                .ToLowerInvariant();
        }

        Assert.Equal(
            expectedResourceSha256,
            resourceSha256);
        Dl1MeshData mesh =
            await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                resource,
                cache);
        return Dl1MeshPreviewAdapter.Convert(
            mesh,
            resourceSha256);
    }

    private static async Task<Dl1MeshData>
        DecodeMeshAsync(
            string packPath,
            int resourceIndex,
            string expectedName,
            string expectedResourceSha256,
            Rp6lChunkCache cache)
    {
        Assert.True(File.Exists(packPath), packPath);
        Rp6lArchive archive =
            await Rp6lArchive.OpenAsync(packPath);
        Rp6lResourceDescriptor resource =
            archive.Resources[resourceIndex];
        Assert.Equal(expectedName, resource.Name);
        string resourceSha256;
        await using (Stream stream =
                     await archive.OpenResourceStreamAsync(
                         resource,
                         cache))
        {
            resourceSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream))
                .ToLowerInvariant();
        }

        Assert.Equal(
            expectedResourceSha256,
            resourceSha256);
        return await Dl1MeshResourceDecoder.DecodeAsync(
            archive,
            resource,
            cache);
    }
}
