using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class InstalledPlayerPerspectiveRetargetTests
{
    [Fact]
    public async Task PlayerTppToFppKeepsMatchingFingerChainsInBindBasisPolicy()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string packPath = Path.Combine(
            install.DataPath,
            "common_cod_1_PC.rpack");
        if (!File.Exists(packPath))
        {
            return;
        }

        Rp6lArchive archive = await Rp6lArchive.OpenAsync(packPath);
        Rp6lResourceDescriptor? sourceResource = archive.FindResource(
            Rp6lResourceTypes.Mesh,
            "player_1_tpp");
        Rp6lResourceDescriptor? targetResource = archive.FindResource(
            Rp6lResourceTypes.Mesh,
            "player_1_fpp");
        if (sourceResource is null || targetResource is null)
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
                    MaximumMemoryBytes = 0,
                    MaximumMemoryEntryBytes = 0,
                    MaximumDiskBytes = 2L * 1024 * 1024 * 1024,
                });
            Dl1MeshData sourceMesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    sourceResource,
                    cache);
            Dl1MeshData targetMesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    targetResource,
                    cache);
            RigDefinition sourceRig = Assert.IsType<RigDefinition>(
                sourceMesh.Rig);
            RigDefinition targetRig = Assert.IsType<RigDefinition>(
                targetMesh.Rig);
            RetargetMap map = RetargetMapBuilder.CreateSuggested(
                sourceRig,
                targetRig);

            Dictionary<string, BoneDefinition> sourceByName =
                sourceRig.Bones.ToDictionary(
                    static bone => bone.Name,
                    StringComparer.OrdinalIgnoreCase);
            BoneDefinition[] matchingTargetFingers = targetRig.Bones
                .Where(bone =>
                    bone.Name.Contains(
                        "finger",
                        StringComparison.OrdinalIgnoreCase) &&
                    sourceByName.ContainsKey(bone.Name))
                .ToArray();

            Assert.NotEmpty(matchingTargetFingers);
            foreach (BoneDefinition targetBone in matchingTargetFingers)
            {
                BoneMapEntry entry = Assert.IsType<BoneMapEntry>(
                    map.Entries.SingleOrDefault(candidate =>
                        candidate.TargetBoneIndex == targetBone.Index));
                Assert.Equal(
                    sourceByName[targetBone.Name].Index,
                    entry.SourceBoneIndex);
                Assert.Equal(
                    RetargetTransferPolicy.GlobalBindBasis,
                    entry.TransferPolicy);
                Assert.Equal(
                    RetargetComponentPolicy.FullTransform,
                    entry.ComponentPolicy);
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
