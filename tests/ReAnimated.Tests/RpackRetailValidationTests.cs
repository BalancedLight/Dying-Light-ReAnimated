using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class RpackRetailValidationTests
{
    [Fact]
    public async Task InstalledCommonMeshesIndexesAndDecodesDemolisherWhenAvailable()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string pack = Path.Combine(
            install.DataPath,
            "common_meshes_PC.rpack");
        if (!File.Exists(pack))
        {
            return;
        }

        Rp6lArchive archive = await Rp6lArchive.OpenAsync(pack);
        Assert.True(archive.Resources.Count > 5_000);
        Assert.True(archive.Resources.Count(static resource =>
            resource.ResourceType == Rp6lResourceTypes.Mesh) > 5_000);
        Rp6lResourceDescriptor armored = Assert.IsType<Rp6lResourceDescriptor>(
            archive.FindResource(Rp6lResourceTypes.Mesh, "armored"));

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using Rp6lChunkCache cache = new(new Rp6lChunkCacheOptions
            {
                CacheDirectory = Path.Combine(directory, "cache"),
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes = 512L * 1024 * 1024,
            });
            Dl1MeshData mesh = await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                armored,
                cache);
            Assert.True(
                mesh.IsStructurallyValid,
                string.Join(
                    Environment.NewLine,
                    mesh.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.Equal(
                Dl1MeshContainerLayout.FiveItemSplitGpu,
                mesh.ContainerLayout);
            Assert.True(mesh.HasDecodedGeometry);
            Assert.True(mesh.Surfaces.Sum(
                static surface => surface.VertexCount) > 0);
            Assert.True(mesh.Surfaces.Sum(
                static surface => surface.IndexCount) > 0);
            Assert.Contains(
                mesh.Surfaces.SelectMany(
                    static surface => surface.Submeshes),
                static submesh =>
                    submesh.BonePaletteEntityIndexes.Count > 0);
            Assert.Equal(57, mesh.Hierarchy.Bones.Count);
            Assert.Equal(20, mesh.Hierarchy.Helpers.Count);
            Assert.Equal(19, mesh.Hierarchy.SkinnedMeshes.Count);
            Assert.Contains(
                mesh.Buffers,
                static buffer =>
                    buffer.Role == Dl1MeshBufferRole.VertexData);
            Assert.Contains(
                mesh.Buffers,
                static buffer =>
                    buffer.Role == Dl1MeshBufferRole.IndexData);
            Assert.True(mesh.HasDecodedMaterialSlotNames);
            Assert.False(mesh.HasResolvedMaterialResources);
            Assert.Contains(
                mesh.MaterialSlots,
                static slot =>
                    slot.BindingStatus ==
                    Dl1MaterialBindingStatus.DatabaseNameDecoded);
            Assert.DoesNotContain(
                mesh.MaterialSlots,
                static slot =>
                    slot.BindingStatus ==
                    Dl1MaterialBindingStatus.DeclaredSlotNameUnresolved);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
