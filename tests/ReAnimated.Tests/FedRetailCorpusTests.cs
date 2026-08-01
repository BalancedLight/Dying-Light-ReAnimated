using System.IO.Compression;
using ReAnimated.Codecs.Fed;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class FedRetailCorpusTests
{
    [Fact]
    public async Task InstalledPlayerFedCompatibilityIsExactAndWrongFamilyFailsClosed()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string data0Path = Path.Combine(
            install.InstallPath,
            "DW",
            "Data0.pak");
        string packPath = Path.Combine(
            install.DataPath,
            "common_cod_1_PC.rpack");
        if (!File.Exists(data0Path) ||
            !File.Exists(packPath))
        {
            return;
        }

        using ZipArchive data0 = ZipFile.OpenRead(data0Path);
        FedDocument playerFpp = ReadFed(
            data0,
            "data/characters/heroes/player_1_fpp/player_1_fpp.fed");
        FedDocument legacyPlayerTpp = ReadFed(
            data0,
            "data/characters/heroes/player_man_01_tpp/player_man_01_tpp.fed");
        Rp6lArchive archive =
            await Rp6lArchive.OpenAsync(packPath);
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(
                        directory,
                        "cache"),
                    MaximumMemoryBytes = 0,
                    MaximumMemoryEntryBytes = 0,
                    MaximumDiskBytes = 512L * 1024 * 1024,
                });
            Dl1MeshData fpp = await DecodeMeshAsync(
                archive,
                "player_1_fpp",
                cache);
            Dl1MeshData tpp = await DecodeMeshAsync(
                archive,
                "player_1_tpp",
                cache);

            Assert.NotNull(fpp.Rig);
            Assert.NotNull(tpp.Rig);
            FedExpression[] visibleFppExpressions =
                playerFpp.Expressions
                    .Where(static expression =>
                        expression.Weights.Count > 0)
                    .ToArray();
            Assert.Equal(5, visibleFppExpressions.Length);
            foreach (FedExpression expression in
                     visibleFppExpressions)
            {
                FedLayerBuildResult result =
                    FedDomainAdapter.CreateLayer(
                        playerFpp,
                        expression.Name,
                        fpp.Rig!,
                        compatibilityPolicy:
                            FedLayerCompatibilityPolicy
                                .RequireComplete);
                Assert.True(result.Compatibility.IsComplete);
                Assert.Equal(
                    expression.Weights.Count,
                    result.Compatibility.ResolvedWeightCount);
            }

            FedExpression incompatible =
                Assert.IsType<FedExpression>(
                    legacyPlayerTpp.FindExpression("smile"));
            Assert.NotEmpty(incompatible.Weights);
            InvalidOperationException exception =
                Assert.Throws<InvalidOperationException>(
                    () => FedDomainAdapter.CreateLayer(
                        legacyPlayerTpp,
                        incompatible.Name,
                        tpp.Rig!,
                        compatibilityPolicy:
                            FedLayerCompatibilityPolicy
                                .RequireComplete));
            Assert.Contains(
                "Accurate application requires a complete model-family match",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void InstalledFedCorpusParsesStrictlyWhenAvailable()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string data0 = Path.Combine(
            install.InstallPath,
            "DW",
            "Data0.pak");
        using ZipArchive archive = ZipFile.OpenRead(data0);
        ZipArchiveEntry[] fedEntries = archive.Entries
            .Where(static entry =>
                entry.FullName.EndsWith(
                    ".fed",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(fedEntries);
        int expressionCount = 0;
        int weightCount = 0;
        List<FedDiagnostic> diagnostics = [];
        foreach (ZipArchiveEntry entry in fedEntries)
        {
            using Stream stream = entry.Open();
            FedDocument document = FedReader.Read(
                stream,
                Path.GetFileNameWithoutExtension(entry.Name));
            expressionCount += document.Expressions.Count;
            weightCount += document.Expressions.Sum(static expression =>
                expression.Weights.Count);
            diagnostics.AddRange(document.Diagnostics);
        }

        Assert.True(expressionCount >= fedEntries.Length);
        Assert.True(weightCount > expressionCount);
        Assert.Contains(
            diagnostics,
            static diagnostic =>
                diagnostic.Code == "FED001" &&
                diagnostic.Message.Contains(
                    "SLEEPY",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static FedDocument ReadFed(
        ZipArchive archive,
        string path)
    {
        ZipArchiveEntry entry = Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry(path));
        using Stream stream = entry.Open();
        return FedReader.Read(
            stream,
            Path.GetFileNameWithoutExtension(entry.Name));
    }

    private static async Task<Dl1MeshData> DecodeMeshAsync(
        Rp6lArchive archive,
        string name,
        Rp6lChunkCache cache)
    {
        Rp6lResourceDescriptor resource =
            Assert.IsType<Rp6lResourceDescriptor>(
                archive.FindResource(
                    Rp6lResourceTypes.Mesh,
                    name));
        return await Dl1MeshResourceDecoder.DecodeAsync(
            archive,
            resource,
            cache);
    }
}
