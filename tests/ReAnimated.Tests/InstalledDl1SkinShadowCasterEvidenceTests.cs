using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class InstalledDl1SkinShadowCasterEvidenceTests
{
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    [Theory(Timeout = 180_000)]
    [InlineData(
        "zombie_screamer",
        5_198,
        "cd28ea77ef5d29d3461af577753dc7f08f08103d929b5ab3c0bc487f24ea6c6e",
        "sc")]
    [InlineData(
        "zombie_voleteile",
        5_200,
        "06ae029e4f22ba2fa098b28477c8f3012e34da232f903b010f514fe52f988471",
        "body_sc,head_sc")]
    public async Task DefaultSkinRetainsDeclaredShadowCasterDrawClass(
        string resourceName,
        int resourceIndex,
        string expectedResourceHash,
        string shadowSurfaceNames)
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
            return;
        }

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 64L * 1024 * 1024,
                    MaximumMemoryEntryBytes = 32 * 1024 * 1024,
                    MaximumDiskBytes = 256L * 1024 * 1024,
                });
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(
                Path.Combine(
                    install.DataPath,
                    "common_meshes_PC.rpack"));
            Rp6lResourceDescriptor resource =
                archive.Resources[resourceIndex];
            Assert.Equal(resourceName, resource.Name);
            await using (Stream stream =
                         await archive.OpenResourceStreamAsync(
                             resource,
                             cache))
            {
                Assert.Equal(
                    expectedResourceHash,
                    Convert.ToHexString(
                            await SHA256.HashDataAsync(stream))
                        .ToLowerInvariant());
            }

            Dl1MeshData mesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);
            Assert.Equal("Default", mesh.AppliedSkinName);
            string[] expectedSurfaceNames =
                shadowSurfaceNames.Split(',');
            Dl1MeshSurface[] shadowSurfaces = mesh.Surfaces
                .Where(surface =>
                    expectedSurfaceNames.Contains(
                        surface.Name,
                        StringComparer.Ordinal))
                .ToArray();
            Assert.NotEmpty(shadowSurfaces);
            Assert.Equal(
                expectedSurfaceNames.Order(),
                shadowSurfaces
                    .Select(static surface => surface.Name)
                    .Distinct()
                    .Order());

            Dl1MaterialSlot shadowSlot = Assert.Single(
                shadowSurfaces
                    .SelectMany(static surface =>
                        surface.Submeshes.Count == 0
                            ? [surface.MaterialSlotIndex]
                            : surface.Submeshes.Select(
                                static submesh =>
                                    submesh.MaterialSlotIndex))
                    .Distinct()
                    .Select(index =>
                        mesh.MaterialSlots.Single(slot =>
                            slot.Index == index)));
            Assert.Equal(
                "shadow_caster_zombie.mat",
                shadowSlot.DatabaseName);
            Assert.Equal(
                "SHADOW_CASTER.MAT",
                shadowSlot.DeclaredDatabaseName);
            Assert.False(
                Dl1PreviewMaterialPolicy.IsNonDisplayShadowCaster(
                    shadowSlot.DatabaseName));
            Assert.True(
                Dl1PreviewMaterialPolicy.IsNonDisplayShadowCaster(
                    shadowSlot));

            Dl1MeshPreviewPayload preview =
                Dl1MeshPreviewAdapter.Convert(mesh);
            Assert.DoesNotContain(
                preview.Meshes,
                renderMesh => expectedSurfaceNames.Any(
                    surfaceName => renderMesh.Id.Contains(
                        $"/{surfaceName}/",
                        StringComparison.Ordinal)));
            Assert.NotEmpty(preview.Meshes);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
