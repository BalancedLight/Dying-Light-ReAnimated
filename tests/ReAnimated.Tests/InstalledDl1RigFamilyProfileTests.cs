using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1RigFamilyProfileTests
{
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private static readonly InstalledFamilyControl[] CommonMeshControls =
    [
        new(
            "jade",
            Dl1RigFamily.GenericNpc,
            173,
            18,
            185,
            15,
            20,
            46,
            4,
            "fe268144f8054f51e488db44dc5f49f05d81a57d7b2c7a4c18b0481d48354980"),
        new(
            "rais",
            Dl1RigFamily.GenericNpc,
            95,
            20,
            109,
            13,
            20,
            46,
            2,
            "53be5c496f508225eb0ab4c97db41dcd19472d5b954312039f7802e06c152086"),
        new(
            "zombie_prime",
            Dl1RigFamily.GenericInfected,
            65,
            20,
            79,
            16,
            24,
            0,
            4,
            "50a669d0c22ac82edb2f449b7a7ef10c9a59bef92aa7cc1f1be5185925a3adb4"),
        new(
            "zombie_voleteile_blue",
            Dl1RigFamily.Volatile,
            87,
            10,
            97,
            10,
            16,
            15,
            4,
            "468b9a24b2130781a9de47d2dfc58644ecc2f3a8a0829474da9edb2c1bd30dbb"),
        new(
            "zombie_screamer",
            Dl1RigFamily.Screamer,
            68,
            12,
            80,
            4,
            10,
            0,
            2,
            "dd27a9f327196524b84381656588478367bfd955d8e55dba7c40b187569ce28c"),
        new(
            "armored",
            Dl1RigFamily.Demolisher,
            57,
            20,
            77,
            19,
            22,
            15,
            2,
            "dc830e05f54d04e9b019f2839d45ac2c22d9ea2479e98aad2bd1f75be6aa8cda"),
        new(
            "zombie_goon",
            Dl1RigFamily.Goon,
            114,
            20,
            128,
            18,
            24,
            16,
            6,
            "fcddc2ef8a4fa8d3366a90fc031a23749f4bfe589e8bac040b47a7664aa3fe8e"),
    ];

    private readonly ITestOutputHelper _output;

    public InstalledDl1RigFamilyProfileTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task InstalledBuildExposesAndClassifiesBoundedFamilyControls()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string commonMeshesPath = Path.Combine(
            install.DataPath,
            "common_meshes_PC.rpack");
        string commonCodPath = Path.Combine(
            install.DataPath,
            "common_cod_1_PC.rpack");
        if (!File.Exists(commonMeshesPath) ||
            !File.Exists(commonCodPath))
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
                $"Installed control skipped: build {build.BuildFingerprint} " +
                $"does not match validated 1.55 fingerprint " +
                $"{ValidatedBuildFingerprint}.");
            return;
        }

        Rp6lArchive commonMeshes =
            await Rp6lArchive.OpenAsync(commonMeshesPath);
        Rp6lArchive commonCod =
            await Rp6lArchive.OpenAsync(commonCodPath);
        Rp6lResourceDescriptor[] playerPerspectiveRows =
            commonCod.Resources
                .Where(static resource =>
                    resource.ResourceType == Rp6lResourceTypes.Mesh &&
                    resource.Name.StartsWith(
                        "player_",
                        StringComparison.OrdinalIgnoreCase) &&
                    (resource.Name.Contains(
                         "_fpp",
                         StringComparison.OrdinalIgnoreCase) ||
                     resource.Name.Contains(
                         "_tpp",
                         StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        Assert.Equal(30, playerPerspectiveRows.Length);
        Assert.Equal(
            15,
            playerPerspectiveRows.Count(static row =>
                row.Name.Contains(
                    "_fpp",
                    StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            15,
            playerPerspectiveRows.Count(static row =>
                row.Name.Contains(
                    "_tpp",
                    StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(
            playerPerspectiveRows,
            static row => row.Name == "player_1_fpp");
        Assert.Contains(
            playerPerspectiveRows,
            static row => row.Name == "player_1_tpp");
        _output.WriteLine(
            $"player perspective resource rows: {playerPerspectiveRows.Length}");
        foreach (string name in playerPerspectiveRows
                     .Select(static row => row.Name)
                     .OrderBy(
                         static name => name,
                         StringComparer.OrdinalIgnoreCase))
        {
            _output.WriteLine($"player row: {name}");
        }

        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 0,
                    MaximumMemoryEntryBytes = 0,
                    MaximumDiskBytes = 768L * 1024 * 1024,
                });
            var classifier =
                new Dl1RetailMeshClassificationService();
            List<Dl1RetailMeshProfile> profiles = [];
            foreach (InstalledFamilyControl control in CommonMeshControls)
            {
                Rp6lResourceDescriptor resource =
                    Assert.IsType<Rp6lResourceDescriptor>(
                        commonMeshes.FindResource(
                            Rp6lResourceTypes.Mesh,
                            control.ResourceName));
                Dl1MeshData mesh =
                    await Dl1MeshResourceDecoder.DecodeAsync(
                        commonMeshes,
                        resource,
                        cache);
                Dl1RetailMeshProfile profile = classifier.Classify(
                    CreateAsset(commonMeshes, resource),
                    mesh);
                profiles.Add(profile);
                int skinPaletteCount = mesh.Surfaces
                    .SelectMany(static surface => surface.Submeshes)
                    .Count(static submesh =>
                        submesh.BonePaletteEntityIndexes.Count > 0);

                _output.WriteLine(
                    $"{control.ResourceName}: " +
                    $"valid={mesh.IsStructurallyValid}, " +
                    $"classified={profile.RigFamily}, " +
                    $"hierarchy bones={mesh.Hierarchy.Bones.Count}, " +
                    $"helpers={mesh.Hierarchy.Helpers.Count}, " +
                    $"rig nodes={mesh.Rig?.BoneCount ?? 0}, " +
                    $"surfaces={mesh.Surfaces.Count}, " +
                    $"skin palettes={skinPaletteCount}, " +
                    $"morphs={mesh.MorphTargets.Count}, " +
                    $"variants={profile.VariantNames.Count}, " +
                    $"signature={profile.RigSignature}");
                foreach (Dl1MeshDiagnostic diagnostic in
                         mesh.Diagnostics.Where(static diagnostic =>
                             diagnostic.Severity ==
                                 Dl1MeshDiagnosticSeverity.Error))
                {
                    _output.WriteLine(
                        $"  {diagnostic.Code}: {diagnostic.Message}");
                }
                Assert.True(
                    mesh.IsStructurallyValid,
                    string.Join(
                        Environment.NewLine,
                        mesh.Diagnostics.Select(static diagnostic =>
                            $"{diagnostic.Code}: {diagnostic.Message}")));
                Assert.Equal(
                    Dl1MeshGeometryKind.Skinned,
                    profile.GeometryKind);
                Assert.Equal(control.Family, profile.RigFamily);
                Assert.InRange(
                    profile.RigFamilyConfidence,
                    Dl1ClassificationConfidence.Medium,
                    Dl1ClassificationConfidence.High);
                Assert.Equal(
                    Dl1RetailSourceScope.BaseGame,
                    profile.SourceScope);
                Assert.Equal(
                    control.HierarchyBoneCount,
                    mesh.Hierarchy.Bones.Count);
                Assert.Equal(
                    control.HierarchyHelperCount,
                    mesh.Hierarchy.Helpers.Count);
                Assert.Equal(
                    control.RigNodeCount,
                    mesh.Rig!.BoneCount);
                Assert.Equal(
                    control.SurfaceCount,
                    mesh.Surfaces.Count);
                Assert.Equal(
                    control.SkinPaletteCount,
                    skinPaletteCount);
                Assert.Equal(
                    control.MorphCount,
                    mesh.MorphTargets.Count);
                Assert.Equal(
                    control.VariantCount,
                    profile.VariantNames.Count);
                Assert.Equal(
                    control.RigSignature,
                    profile.RigSignature);
            }

            Assert.Equal(
                CommonMeshControls.Length,
                profiles.Count);
            Assert.Equal(
                2,
                profiles.Count(static profile =>
                    profile.RigFamily == Dl1RigFamily.GenericNpc));
            Assert.Equal(
                1,
                profiles.Count(static profile =>
                    profile.RigFamily ==
                        Dl1RigFamily.GenericInfected));
            Assert.Single(
                profiles,
                static profile =>
                    profile.RigFamily == Dl1RigFamily.Volatile);
            Assert.Single(
                profiles,
                static profile =>
                    profile.RigFamily == Dl1RigFamily.Screamer);
            Assert.Single(
                profiles,
                static profile =>
                    profile.RigFamily == Dl1RigFamily.Demolisher);
            Assert.Single(
                profiles,
                static profile =>
                    profile.RigFamily == Dl1RigFamily.Goon);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static RetailAssetRecord CreateAsset(
        Rp6lArchive archive,
        Rp6lResourceDescriptor resource)
    {
        long length = resource.Items
            .Where(static item => item.HasReadableSize)
            .Sum(static item => (long)item.SizeOrHash);
        RetailAssetLogicalId logical =
            RetailAssetLogicalId.Rpack(
                resource.ResourceType,
                resource.Name);
        return new RetailAssetRecord(
            RetailAssetId.Create(
                logical,
                RetailAssetIdentity.CreateInstallId(
                    Path.GetDirectoryName(archive.Path)
                    ?? archive.Path),
                "dl1-rpacks",
                resource.Index,
                10_000,
                archive.CacheIdentity),
            resource.Name,
            new RetailAssetSource(
                "dl1-rpacks",
                RetailAssetSourceKind.Rpack,
                10_000,
                archive.Path,
                $"{resource.Name}#{resource.Index}",
                resource.Index,
                length,
                archive.File.Length,
                archive.File.LastWriteTimeUtc));
    }

    private sealed record InstalledFamilyControl(
        string ResourceName,
        Dl1RigFamily Family,
        int HierarchyBoneCount,
        int HierarchyHelperCount,
        int RigNodeCount,
        int SurfaceCount,
        int SkinPaletteCount,
        int MorphCount,
        int VariantCount,
        string RigSignature);
}
