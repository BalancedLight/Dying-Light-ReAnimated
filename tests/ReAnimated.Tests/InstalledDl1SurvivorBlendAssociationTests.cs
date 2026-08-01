using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1SurvivorBlendAssociationTests
{
    private const string RunEnvironmentVariable =
        "DLR_RUN_SURVIVOR_BLEND_ASSOCIATION";

    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private readonly ITestOutputHelper _output;

    public InstalledDl1SurvivorBlendAssociationTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 600_000)]
    public async Task InstalledNoBlendDeclarationsUseFiniteEntityWorldPath()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    RunEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static candidate =>
                candidate.IsValid);
        if (install is null)
        {
            return;
        }

        Dl1InstalledBuildFingerprint fingerprint =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        Assert.Equal(
            ValidatedBuildFingerprint,
            fingerprint.BuildFingerprint,
            ignoreCase: true);

        InstalledControl[] controls =
        [
            new(
                Path.Combine(
                    install.InstallPath,
                    "DW_DLC17",
                    "Data",
                    "wasteland_final_PC.rpack"),
                43,
                "survivor_b",
                EntityCount: 525,
                SurfaceCount: 366,
                NoBlendSubmeshCount: 429,
                SinglePaletteCount: 85,
                MultiPaletteCount: 344,
                ExactShadowCount: 40,
                VisibleNoBlendCount: 389,
                VisibleSurfaceLodCount: 305),
            new(
                Path.Combine(
                    install.InstallPath,
                    "DW_DLC17",
                    "Data",
                    "wasteland_final_PC.rpack"),
                45,
                "survivor_woman_b",
                EntityCount: 353,
                SurfaceCount: 134,
                NoBlendSubmeshCount: 175,
                SinglePaletteCount: 48,
                MultiPaletteCount: 127,
                ExactShadowCount: 24,
                VisibleNoBlendCount: 151,
                VisibleSurfaceLodCount: 96),
            new(
                Path.Combine(
                    install.DataPath,
                    "common_meshes_PC.rpack"),
                4_353,
                "survivor_dr_zaebo_a",
                EntityCount: 94,
                SurfaceCount: 8,
                NoBlendSubmeshCount: 1,
                SinglePaletteCount: 0,
                MultiPaletteCount: 1,
                ExactShadowCount: 1,
                VisibleNoBlendCount: 0,
                VisibleSurfaceLodCount: 0),
            new(
                Path.Combine(
                    install.DataPath,
                    "common_meshes_PC.rpack"),
                5_168,
                "zere_cin",
                EntityCount: 165,
                SurfaceCount: 8,
                NoBlendSubmeshCount: 1,
                SinglePaletteCount: 0,
                MultiPaletteCount: 1,
                ExactShadowCount: 1,
                VisibleNoBlendCount: 0,
                VisibleSurfaceLodCount: 0),
        ];

        string temporaryDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory =
                        Path.Combine(
                            temporaryDirectory,
                            "cache"),
                    MaximumMemoryBytes = 0,
                    MaximumMemoryEntryBytes = 0,
                    MaximumDiskBytes =
                        2L * 1024 * 1024 * 1024,
                });
            foreach (InstalledControl control in controls)
            {
                await AssertControlAsync(
                    control,
                    cache);
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                temporaryDirectory);
        }
    }

    private async Task AssertControlAsync(
        InstalledControl control,
        Rp6lChunkCache cache)
    {
        Rp6lArchive archive =
            await Rp6lArchive.OpenAsync(control.PackPath);
        Rp6lResourceDescriptor resource =
            archive.Resources[control.ResourceIndex];
        Assert.Equal(control.ResourceName, resource.Name);

        Dl1MeshData mesh =
            await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                resource,
                cache);
        Assert.Equal(
            control.EntityCount,
            mesh.Hierarchy.Entities.Count);
        Assert.Equal(
            control.SurfaceCount,
            mesh.Surfaces.Count);
        IReadOnlyList<CompactMatrix3x4> worldMatrices =
            mesh.Hierarchy.ReconstructGlobalMatrices();

        NoBlendRow[] rows = mesh.Surfaces
            .Where(surface =>
                mesh.Hierarchy.Entities[surface.EntityIndex]
                    .EntityType.HasFlag(
                        CompactMeshEntityType.SkinnedMesh) &&
                HasNeitherBlendStream(surface))
            .SelectMany(surface =>
                surface.Submeshes
                    .Where(static submesh =>
                        submesh.BonePaletteEntityIndexes.Count > 0)
                    .Select(submesh => new NoBlendRow(
                        surface,
                        submesh,
                        IsExactShadowCaster(
                            mesh,
                            submesh))))
            .ToArray();

        Assert.Equal(
            control.NoBlendSubmeshCount,
            rows.Length);
        Assert.Equal(
            control.SinglePaletteCount,
            rows.Count(static row =>
                row.Submesh.BonePaletteEntityIndexes.Count == 1));
        Assert.Equal(
            control.MultiPaletteCount,
            rows.Count(static row =>
                row.Submesh.BonePaletteEntityIndexes.Count > 1));
        Assert.Equal(
            control.ExactShadowCount,
            rows.Count(static row => row.IsExactShadowCaster));
        NoBlendRow[] visibleRows = rows
            .Where(static row => !row.IsExactShadowCaster)
            .ToArray();
        Assert.Equal(
            control.VisibleNoBlendCount,
            visibleRows.Length);
        Assert.Equal(
            control.VisibleSurfaceLodCount,
            visibleRows
                .Select(static row => (
                    row.Surface.EntityIndex,
                    row.Surface.LodIndex))
                .Distinct()
                .Count());

        Assert.All(
            rows,
            row =>
            {
                CompactMeshEntity entity =
                    mesh.Hierarchy.Entities[
                        row.Surface.EntityIndex];
                CompactMatrix3x4 world =
                    worldMatrices[row.Surface.EntityIndex];
                Assert.True(world.IsFinite);
                Assert.Equal(
                    Dl1SkinBindingMode
                        .StaticEntityTransformIgnoredPalette,
                    row.Submesh.SkinBindingMode);
                Assert.Equal(
                    row.Submesh.SkinBindingMode,
                    Dl1SkinBindingPolicy.Classify(
                        row.Surface.VertexLayout,
                        row.Surface.Vertices,
                        row.Surface.Indices,
                        row.Submesh,
                        entity,
                        world));
                Assert.All(
                    row.Surface.Vertices,
                    static vertex =>
                        Assert.Equal(
                            System.Numerics.Vector4.Zero,
                            vertex.BlendWeights));
            });

        Assert.Contains(
            mesh.Diagnostics,
            diagnostic =>
                diagnostic.Code == "DL1MESH016" &&
                diagnostic.Message.Contains(
                    control.NoBlendSubmeshCount.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture),
                    StringComparison.Ordinal));

        var validator = new Dl1MeshCorpusValidator(
            cache,
            new Dl1MeshCorpusValidationOptions
            {
                MaximumIssuesPerResource = 2_048,
            });
        Dl1MeshCorpusResourceResult result =
            validator.ValidateDecodedMesh(
                resource,
                mesh);
        Assert.DoesNotContain(
            result.Issues,
            static issue =>
                issue.Code is
                    "DL1CORPUS054" or
                    "DL1CORPUS055");
        if (control.VisibleNoBlendCount > 0)
        {
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS066" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning);
        }

        if (control.ExactShadowCount > 0)
        {
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS058" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning);
        }

        _output.WriteLine(
            $"{Path.GetFileName(control.PackPath)}#{control.ResourceIndex} {control.ResourceName}: noBlend={rows.Length}, formerlySingle={control.SinglePaletteCount}, multi={control.MultiPaletteCount}, exactShadow={control.ExactShadowCount}, visible={visibleRows.Length}, nonFiniteWorld=0. Skin/variant visibility was not inferred.");
    }

    private static bool HasNeitherBlendStream(
        Dl1MeshSurface surface) =>
        !surface.VertexLayout.Elements.Any(static element =>
            element.Semantic ==
                Dl1VertexSemantic.BlendWeights) &&
        !surface.VertexLayout.Elements.Any(static element =>
            element.Semantic ==
                Dl1VertexSemantic.BlendIndices);

    private static bool IsExactShadowCaster(
        Dl1MeshData mesh,
        Dl1MeshSubmesh submesh) =>
        Dl1PreviewMaterialPolicy
            .IsExactMissingBlendShadowCaster(
                mesh.MaterialSlots
                    .FirstOrDefault(slot =>
                        slot.Index ==
                            submesh.MaterialSlotIndex));

    private sealed record NoBlendRow(
        Dl1MeshSurface Surface,
        Dl1MeshSubmesh Submesh,
        bool IsExactShadowCaster);

    private sealed record InstalledControl(
        string PackPath,
        int ResourceIndex,
        string ResourceName,
        int EntityCount,
        int SurfaceCount,
        int NoBlendSubmeshCount,
        int SinglePaletteCount,
        int MultiPaletteCount,
        int ExactShadowCount,
        int VisibleNoBlendCount,
        int VisibleSurfaceLodCount);
}
