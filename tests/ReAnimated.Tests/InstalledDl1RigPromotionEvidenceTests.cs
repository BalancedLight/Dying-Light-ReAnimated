using System.Text.Json;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Meshes;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1RigPromotionEvidenceTests
{
    private readonly ITestOutputHelper _output;

    public InstalledDl1RigPromotionEvidenceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 600_000)]
    public async Task ClassifyBlockedNonTrsEntitiesByEffectiveSkinUse()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "DLR_RUN_INSTALLED_RIG_PROMOTION_EVIDENCE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        string? configuredReportPath =
            Environment.GetEnvironmentVariable(
                "DLR_MESH_CORPUS_REPORT_PATH");
        string reportPath =
            string.IsNullOrWhiteSpace(configuredReportPath)
                ? Path.GetFullPath(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "..",
                        "..",
                        "..",
                        "..",
                        "..",
                        "artifacts",
                        "validation",
                        "dl1-mesh-corpus-1.55.json"))
                : Path.GetFullPath(configuredReportPath);
        using JsonDocument report = JsonDocument.Parse(
            await File.ReadAllBytesAsync(reportPath));
        string cacheRoot =
            RpackTestData.CreateTemporaryDirectory();
        int resourceCount = 0;
        int effectiveNonTrsCount = 0;
        int declaredOnlyNonTrsCount = 0;
        int outsidePaletteNonTrsCount = 0;
        int unresolvedBindingCount = 0;
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory =
                        Path.Combine(cacheRoot, "cache"),
                    MaximumMemoryBytes = 0,
                    MaximumMemoryEntryBytes = 0,
                    MaximumDiskBytes =
                        8L * 1024 * 1024 * 1024,
                });
            foreach (JsonElement packRow in report.RootElement
                         .GetProperty("packs")
                         .EnumerateArray())
            {
                JsonElement[] resources = packRow
                    .GetProperty("meshResources")
                    .EnumerateArray()
                    .Where(HasRawBindPoseRigPromotionBoundary)
                    .ToArray();
                if (resources.Length == 0)
                {
                    continue;
                }

                Rp6lArchive archive =
                    await Rp6lArchive.OpenAsync(
                        packRow.GetProperty("packPath")
                            .GetString()!);
                foreach (JsonElement resourceRow in resources)
                {
                    int resourceIndex = resourceRow
                        .GetProperty("resourceIndex")
                        .GetInt32();
                    Rp6lResourceDescriptor resource =
                        archive.Resources.Single(candidate =>
                            candidate.Index == resourceIndex);
                    Dl1MeshData mesh =
                        await Dl1MeshResourceDecoder.DecodeAsync(
                            archive,
                            resource,
                            cache);
                    Dl1RigPromotionAnalysis analysis =
                        Dl1RigPromotionPolicy.Analyze(
                            mesh.Hierarchy,
                            mesh.Surfaces);
                    HashSet<int> declared = analysis
                        .DeclaredPaletteEntityIndexes
                        .ToHashSet();
                    HashSet<int> effective = analysis
                        .EffectiveSkinEntityIndexes
                        .ToHashSet();
                    string[] rows = analysis.NonTrsEntityIndexes
                        .Select(index =>
                        {
                            string use = effective.Contains(index)
                                ? "effective"
                                : declared.Contains(index)
                                    ? "declared-only"
                                    : "outside-palette";
                            switch (use)
                            {
                                case "effective":
                                    effectiveNonTrsCount++;
                                    break;
                                case "declared-only":
                                    declaredOnlyNonTrsCount++;
                                    break;
                                default:
                                    outsidePaletteNonTrsCount++;
                                    break;
                            }

                            return $"{index}:{mesh.Hierarchy.Entities[index].Name}:{mesh.Hierarchy.Entities[index].EntityType}:{use}";
                        })
                        .ToArray();
                    if (analysis.HasUnresolvedSkinBindings)
                    {
                        unresolvedBindingCount++;
                    }

                    resourceCount++;
                    _output.WriteLine(
                        $"{Path.GetFileName(archive.Path)}#{resource.Index} '{resource.Name}': {string.Join(", ", rows)} unresolvedSkin={analysis.HasUnresolvedSkinBindings}");
                }
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(cacheRoot);
        }

        _output.WriteLine(
            $"SUMMARY resources={resourceCount} effectiveNonTrs={effectiveNonTrsCount} declaredOnlyNonTrs={declaredOnlyNonTrsCount} outsidePaletteNonTrs={outsidePaletteNonTrsCount} unresolvedBindings={unresolvedBindingCount}");
        Assert.Equal(44, resourceCount);
        Assert.Equal(0, effectiveNonTrsCount);
        Assert.Equal(0, declaredOnlyNonTrsCount);
        Assert.Equal(48, outsidePaletteNonTrsCount);
        Assert.Equal(0, unresolvedBindingCount);
    }

    private static bool HasRawBindPoseRigPromotionBoundary(
        JsonElement resource) =>
        resource.GetProperty("issues")
            .EnumerateArray()
            .Any(issue =>
                string.Equals(
                    issue.GetProperty("code").GetString(),
                    "DL1CORPUS043",
                    StringComparison.Ordinal) &&
                string.Equals(
                    issue.GetProperty("severity").GetString(),
                    "warning",
                    StringComparison.OrdinalIgnoreCase));
}
