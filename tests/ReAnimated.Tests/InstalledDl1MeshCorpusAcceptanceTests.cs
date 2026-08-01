using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using ReAnimated.Renderer.D3D11;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1MeshCorpusAcceptanceTests
{
    private const string RunEnvironmentVariable =
        "DLR_RUN_INSTALLED_MESH_CORPUS";
    private const string ReportEnvironmentVariable =
        "DLR_MESH_CORPUS_REPORT_PATH";
    private const string CacheEnvironmentVariable =
        "DLR_MESH_CORPUS_CACHE_PATH";
    private const int MaximumPresentationIssuesPerResource = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase),
        },
    };

    private readonly ITestOutputHelper _output;

    public InstalledDl1MeshCorpusAcceptanceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 7_200_000)]
    public async Task ConfiguredInstalledType272CorpusIsFullyClassified()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    RunEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"Opt-in corpus skipped; run tools\\validate_dl1_mesh_corpus.ps1 or set {RunEnvironmentVariable}=1.");
            return;
        }

        Dl1InstallLocation install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid)
            ?? throw new InvalidOperationException(
                "No complete Dying Light 1 Steam installation was discovered.");
        string reportPath = ResolveReportPath();
        string cachePath = ResolveCachePath();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        Dl1InstalledBuildFingerprint build =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(reportPath)
            ?? throw new InvalidOperationException(
                "The corpus report path has no parent directory."));
        Directory.CreateDirectory(cachePath);

        await using var cache = new Rp6lChunkCache(
            new Rp6lChunkCacheOptions
            {
                CacheDirectory = cachePath,
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes = 16L * 1024 * 1024 * 1024,
                CopyBufferBytes = 256 * 1024,
            });
        await using Dl1RetailProviderSet providers =
            Dl1RetailProviderSet.Create(
                install.InstallPath,
                cache);

        (int descriptorMeshCount,
            IReadOnlyList<CorpusPreflightFailure> preflightFailures) =
            await PreflightAsync(
                providers.RpackProvider.Sources,
                CancellationToken.None);
        _output.WriteLine(
            $"bounded descriptor preflight: {providers.RpackProvider.Sources.Count:N0} configured packs, {descriptorMeshCount:N0} type-{Rp6lResourceTypes.Mesh} resources, {preflightFailures.Count:N0} pack failures");
        _output.WriteLine($"report: {reportPath}");
        _output.WriteLine($"streaming disk cache: {cachePath}");

        var progress = new Progress<string>(
            message => _output.WriteLine(
                $"{DateTimeOffset.Now:HH:mm:ss} {message}"));
        var validator = new Dl1MeshCorpusValidator(
            cache,
            presentationValidator:
                ValidatePresentationAsync);
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<Dl1MeshCorpusPackResult> packs =
            await validator.ValidateAsync(
                providers.RpackProvider.Sources,
                async (partialPacks, cancellationToken) =>
                {
                    InstalledMeshCorpusReport checkpoint =
                        CreateReport(
                            complete: false,
                            install,
                            build,
                            startedUtc,
                            stopwatch.Elapsed,
                            descriptorMeshCount,
                            preflightFailures,
                            partialPacks);
                    await WriteReportAtomicAsync(
                        reportPath,
                        checkpoint,
                        cancellationToken);
                    int scanned = partialPacks.Sum(
                        static pack => pack.MeshResources.Count);
                    int blocked = partialPacks.Sum(
                        static pack => pack.MeshResources.Count(
                            static resource => !resource.Passed));
                    int presentationValidated =
                        partialPacks.Sum(static pack =>
                            pack.MeshResources.Count(
                                static resource =>
                                    resource.Presentation
                                        ?.Passed == true));
                    int renderMeshes = partialPacks.Sum(
                        static pack =>
                            pack.MeshResources.Sum(
                                static resource =>
                                    resource.Presentation
                                        ?.RenderMeshCount ?? 0));
                    int nonDisplayGeometry =
                        partialPacks.Sum(static pack =>
                            pack.MeshResources.Count(
                                static resource =>
                                    resource.Disposition ==
                                        Dl1MeshCorpusDisposition
                                            .NonDisplayGeometry));
                    _output.WriteLine(
                        $"checkpoint: {partialPacks.Count:N0}/{providers.RpackProvider.Sources.Count:N0} packs, {scanned:N0}/{descriptorMeshCount:N0} meshes, {presentationValidated:N0} presentation validated, {nonDisplayGeometry:N0} non-display geometry, {renderMeshes:N0} render draws, {blocked:N0} blocked, {scanned / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001):N1} meshes/s");
                },
                progress);
        stopwatch.Stop();

        InstalledMeshCorpusReport report = CreateReport(
            complete: true,
            install,
            build,
            startedUtc,
            stopwatch.Elapsed,
            descriptorMeshCount,
            preflightFailures,
            packs);
        await WriteReportAtomicAsync(
            reportPath,
            report,
            CancellationToken.None);

        _output.WriteLine(
            $"complete: {report.Summary.MeshResourceCount:N0} meshes in {stopwatch.Elapsed}; " +
            $"{report.Summary.GeometryDecodedCount:N0} geometry, " +
            $"{report.Summary.MetadataContainerCount:N0} metadata containers, " +
            $"{report.Summary.PresentationValidatedCount:N0} presentation validated, " +
            $"{report.Summary.PresentationRenderableCount:N0} renderable, " +
            $"{report.Summary.NonDisplayGeometryCount:N0} non-display geometry, " +
            $"{report.Summary.RenderMeshCount:N0} render draws, " +
            $"{report.Summary.BlockedCount:N0} blocked; " +
            $"{report.Summary.MeshesPerSecond:N1} meshes/s; " +
            $"peak working set {report.Summary.PeakWorkingSetBytes / (1024d * 1024):N1} MiB");

        Assert.True(
            report.PreflightFailures.Count == 0,
            string.Join(
                Environment.NewLine,
                report.PreflightFailures.Select(static failure =>
                    $"{failure.PackPath}: {failure.ErrorType}: {failure.Message}")));
        Assert.Equal(
            descriptorMeshCount,
            report.Summary.MeshResourceCount);
        Assert.Equal(
            report.Summary.GeometryDecodedCount,
            report.Summary.PresentationAttemptedCount);
        Assert.Equal(
            report.Summary.GeometryDecodedCount,
            report.Summary.PresentationValidatedCount);
        Assert.Equal(
            report.Summary.GeometryDecodedCount,
            report.Summary.PresentationRenderableCount +
            report.Summary.NonDisplayGeometryCount);
        Assert.True(
            report.Summary.RenderMeshCount > 0,
            "The presentation callback did not publish any render meshes.");
        Dl1MeshCorpusResourceResult[] containers =
            report.Packs
                .SelectMany(static pack =>
                    pack.MeshResources)
                .Where(static resource =>
                    resource.Disposition ==
                        Dl1MeshCorpusDisposition
                            .MetadataOnlyContainer)
                .OrderBy(static resource =>
                    resource.ResourceName,
                    StringComparer.Ordinal)
                .ToArray();
        Assert.Equal(
            ["scaffolding_system_e", "weapon_fists"],
            containers.Select(static resource =>
                resource.ResourceName));
        Assert.All(
            containers,
            static resource =>
                Assert.Null(resource.Presentation));
        var nonDisplayGeometry = report.Packs
            .SelectMany(pack =>
                pack.MeshResources
                    .Where(static resource =>
                        resource.Disposition ==
                            Dl1MeshCorpusDisposition
                                .NonDisplayGeometry)
                    .Select(resource => new
                    {
                        PackFileName = Path.GetFileName(
                            pack.PackPath),
                        Resource = resource,
                    }))
            .OrderBy(
                static item => item.PackFileName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item =>
                item.Resource.ResourceIndex)
            .ToArray();
        (
            string PackFileName,
            int ResourceIndex,
            string ResourceName
        )[] expectedNonDisplayGeometry =
        [
            ("common_meshes_PC.rpack", 67, "anim_helper_a"),
            ("common_meshes_PC.rpack", 474, "bush_a"),
            ("common_meshes_PC.rpack", 476, "bush_c"),
            ("common_meshes_PC.rpack", 478, "bush_d"),
            ("common_meshes_PC.rpack", 479, "bush_e"),
            ("common_meshes_PC.rpack", 480, "bush_f"),
            ("common_meshes_PC.rpack", 481, "bush_g"),
            (
                "common_meshes_PC.rpack",
                1_639,
                "horizon_billboard_smoke_a"
            ),
            (
                "common_meshes_PC.rpack",
                3_568,
                "shadowcaster"
            ),
            ("common_meshes_PC.rpack", 4_215, "snack"),
            ("engine_PC.rpack", 14, "NoMesh"),
            ("wasteland_final_PC.rpack", 25, "dlc_wl_cult_trim_a"),
            ("wasteland_final_PC.rpack", 26, "dlc_wl_cult_trim_d"),
            ("wasteland_final_PC.rpack", 27, "dlc_wl_cult_trim_e"),
            ("wasteland_PC.rpack", 37, "bush_a_trim"),
            ("wasteland_PC.rpack", 38, "bush_c_trim"),
            ("wasteland_PC.rpack", 39, "bush_f_trim"),
            ("wasteland_PC.rpack", 539, "dlc_wl_cult_trim_a"),
            ("wasteland_PC.rpack", 540, "dlc_wl_cult_trim_b"),
            ("wasteland_PC.rpack", 541, "dlc_wl_cult_trim_c"),
            ("wasteland_PC.rpack", 542, "dlc_wl_cult_trim_d"),
            ("wasteland_PC.rpack", 543, "dlc_wl_cult_trim_e"),
        ];
        Assert.Equal(
            expectedNonDisplayGeometry,
            nonDisplayGeometry.Select(static item =>
                (
                    item.PackFileName,
                    item.Resource.ResourceIndex,
                    item.Resource.ResourceName
                )));
        Assert.All(
            nonDisplayGeometry,
            static item =>
            {
                Assert.True(item.Resource.HasDecodedGeometry);
                Dl1MeshCorpusPresentationResult presentation =
                    Assert.IsType<
                        Dl1MeshCorpusPresentationResult>(
                        item.Resource.Presentation);
                Assert.Equal(
                    Dl1MeshCorpusPresentationDisposition
                        .ExplicitlyNonRenderable,
                    presentation.Disposition);
                Assert.Equal(0, presentation.RenderMeshCount);
                Assert.Empty(presentation.Issues);
            });
        Assert.True(
            report.Summary.BlockedCount == 0,
            BuildFailureMessage(report));
        Assert.True(
            report.Packs.All(static pack => pack.Passed),
            BuildFailureMessage(report));
    }

    [Theory]
    [InlineData("null.mat", -1)]
    [InlineData("DEFAULT.MAT", -1)]
    [InlineData("custom_zero.mat", 0)]
    public async Task ZeroTechniqueOnlyGeometryIsExplicitlyNonRenderable(
        string materialName,
        int resolvedTechniqueCount)
    {
        Dl1MeshData mesh = CreateSingleDrawMesh(
            materialName,
            resolvedTechniqueCount);
        var resource = new Rp6lResourceDescriptor(
            0,
            "zero_technique_only",
            Rp6lResourceTypes.Mesh,
            0,
            0,
            5,
            []);

        Dl1MeshCorpusPresentationResult result =
            await ValidatePresentationAsync(
                resource,
                mesh,
                CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(
            Dl1MeshCorpusPresentationDisposition
                .ExplicitlyNonRenderable,
            result.Disposition);
        Assert.Equal(0, result.RenderMeshCount);
        Assert.Empty(result.Issues);
    }

    private static ValueTask<Dl1MeshCorpusPresentationResult>
        ValidatePresentationAsync(
            Rp6lResourceDescriptor resource,
            Dl1MeshData mesh,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dl1MeshPreviewPayload preview =
            Dl1MeshPreviewAdapter.Convert(mesh);
        if (preview.Meshes.Count == 0 &&
            IsExplicitNonDisplayGeometry(mesh))
        {
            return ValueTask.FromResult(
                new Dl1MeshCorpusPresentationResult(
                    Dl1MeshCorpusPresentationDisposition
                        .ExplicitlyNonRenderable,
                    0,
                    0,
                    preview.Skeleton?.Bones.Count ?? 0,
                    []));
        }

        List<Dl1MeshCorpusIssue> issues = [];
        if (preview.Meshes.Count == 0)
        {
            string diagnostic = string.Join(
                " | ",
                preview.Diagnostics.Take(4));
            AddPresentationError(
                issues,
                "DL1PRESENT100",
                string.IsNullOrWhiteSpace(diagnostic)
                    ? $"Resource {resource.Index} '{resource.Name}' produced no preview meshes."
                    : $"Resource {resource.Index} '{resource.Name}' produced no preview meshes: {diagnostic}");
        }

        int skinnedRenderMeshCount = 0;
        for (int index = 0;
             index < preview.Meshes.Count;
             index++)
        {
            if ((index & 0x3F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            MeshRenderData renderMesh = preview.Meshes[index];
            if (renderMesh.IsSkinned)
            {
                skinnedRenderMeshCount++;
                if (renderMesh.SkinBoneIndices.IsEmpty)
                {
                    AddPresentationError(
                        issues,
                        "DL1PRESENT101",
                        $"Render mesh '{renderMesh.Id}' did not retain an explicit retail draw-palette mapping.");
                }
            }

            if (!RenderMeshValidation.TryValidate(
                    renderMesh,
                    preview.Skeleton,
                    out string? validationError))
            {
                AddPresentationError(
                    issues,
                    "DL1PRESENT102",
                    $"Render mesh '{renderMesh.Id}' is not renderable: {validationError}");
            }
        }

        return ValueTask.FromResult(
            new Dl1MeshCorpusPresentationResult(
                Dl1MeshCorpusPresentationDisposition
                    .Renderable,
                preview.Meshes.Count,
                skinnedRenderMeshCount,
                preview.Skeleton?.Bones.Count ?? 0,
                issues));
    }

    private static bool IsExplicitNonDisplayGeometry(
        Dl1MeshData mesh)
    {
        Dictionary<int, int> selectedLodByEntity =
            mesh.Surfaces
                .GroupBy(static surface =>
                    surface.EntityIndex)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Min(
                        static surface =>
                            surface.LodIndex));
        bool foundSelectedDraw = false;
        foreach (Dl1MeshSurface surface in mesh.Surfaces)
        {
            if (surface.LodIndex !=
                selectedLodByEntity[surface.EntityIndex])
            {
                continue;
            }

            if (surface.Submeshes.Count == 0)
            {
                foundSelectedDraw = true;
                if (!IsNonDisplayMaterialSlot(
                        mesh,
                        surface.MaterialSlotIndex))
                {
                    return false;
                }

                continue;
            }

            foreach (Dl1MeshSubmesh submesh in
                     surface.Submeshes)
            {
                foundSelectedDraw = true;
                if (!IsNonDisplayMaterialSlot(
                        mesh,
                        submesh.MaterialSlotIndex))
                {
                    return false;
                }
            }
        }

        return foundSelectedDraw;
    }

    private static bool IsNonDisplayMaterialSlot(
        Dl1MeshData mesh,
        int materialSlotIndex) =>
        Dl1PreviewMaterialPolicy.IsNonDisplayMaterial(
            mesh.MaterialSlots
                .FirstOrDefault(slot =>
                    slot.Index == materialSlotIndex));

    private static Dl1MeshData CreateSingleDrawMesh(
        string materialName,
        int resolvedTechniqueCount)
    {
        var entity = new CompactMeshEntity(
            0,
            "surface",
            0,
            new CompactBounds(0, 0, 0, 1, 1, 0),
            -1,
            CompactMeshEntityType.Mesh,
            0,
            1,
            CompactMatrix3x4.Identity,
            CompactMatrix3x4.Identity,
            0,
            0);
        var surface = new Dl1MeshSurface(
            "surface",
            0,
            0,
            0,
            new Dl1VertexLayout(
                12,
                [
                    new Dl1VertexElement(
                        Dl1VertexSemantic.Position,
                        0,
                        Dl1VertexElementFormat.Float3,
                        0,
                        0),
                ]),
            new Dl1MeshBufferSlice(3, 0, 36, 12),
            new Dl1MeshBufferSlice(4, 0, 6, 2),
            3,
            3,
            [
                CreateVertex(new Vector3(0, 0, 0)),
                CreateVertex(new Vector3(1, 0, 0)),
                CreateVertex(new Vector3(0, 1, 0)),
            ],
            [0, 1, 2],
            []);
        var slot = new Dl1MaterialSlot(
            0,
            materialName,
            null,
            null,
            resolvedTechniqueCount >= 0
                ? Dl1MaterialBindingStatus.Resolved
                : Dl1MaterialBindingStatus.DatabaseNameDecoded);
        if (resolvedTechniqueCount >= 0)
        {
            slot = slot with
            {
                ResolvedMaterial = new(
                    materialName,
                    0,
                    checked((ushort)resolvedTechniqueCount),
                    []),
            };
        }

        return new Dl1MeshData(
            "zero_technique_only",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            new CompactMeshDocument(
                1,
                1,
                0,
                [entity],
                []),
            null,
            [],
            [surface],
            [slot],
            [],
            [],
            []);

        static Dl1MeshVertex CreateVertex(
            Vector3 position) =>
            new(
                position,
                Vector3.UnitZ,
                new Vector4(1, 0, 0, 1),
                Vector2.Zero,
                Vector2.Zero,
                Vector4.One,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0));
    }

    private static void AddPresentationError(
        List<Dl1MeshCorpusIssue> issues,
        string code,
        string message)
    {
        if (issues.Count >=
            MaximumPresentationIssuesPerResource)
        {
            return;
        }

        if (issues.Count ==
            MaximumPresentationIssuesPerResource - 1)
        {
            issues.Add(
                new Dl1MeshCorpusIssue(
                    "DL1PRESENT199",
                    Dl1MeshCorpusIssueSeverity.Error,
                    "Additional presentation errors were omitted by the bounded callback limit."));
            return;
        }

        issues.Add(
            new Dl1MeshCorpusIssue(
                code,
                Dl1MeshCorpusIssueSeverity.Error,
                message));
    }

    private static async Task<(
        int MeshCount,
        IReadOnlyList<CorpusPreflightFailure> Failures)> PreflightAsync(
        IReadOnlyList<RpackSource> sources,
        CancellationToken cancellationToken)
    {
        int meshCount = 0;
        List<CorpusPreflightFailure> failures = [];
        foreach (RpackSource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Rp6lArchive archive = await Rp6lArchive.OpenAsync(
                    source.Path,
                    cancellationToken: cancellationToken);
                meshCount = checked(
                    meshCount +
                    archive.Resources.Count(static resource =>
                        resource.ResourceType ==
                            Rp6lResourceTypes.Mesh));
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                ArgumentException or
                OverflowException)
            {
                failures.Add(new CorpusPreflightFailure(
                    Path.GetFullPath(source.Path),
                    exception.GetType().Name,
                    exception.Message));
            }
        }

        return (meshCount, failures);
    }

    private static InstalledMeshCorpusReport CreateReport(
        bool complete,
        Dl1InstallLocation install,
        Dl1InstalledBuildFingerprint build,
        DateTimeOffset startedUtc,
        TimeSpan elapsed,
        int descriptorMeshCount,
        IReadOnlyList<CorpusPreflightFailure> preflightFailures,
        IReadOnlyList<Dl1MeshCorpusPackResult> packs)
    {
        int meshResourceCount = packs.Sum(
            static pack => pack.MeshResources.Count);
        int geometryCount = packs.Sum(static pack =>
            pack.MeshResources.Count(static resource =>
                resource.HasDecodedGeometry));
        int metadataCount = packs.Sum(static pack =>
            pack.MeshResources.Count(static resource =>
                resource.Disposition ==
                    Dl1MeshCorpusDisposition
                        .MetadataOnlyContainer));
        int blockedCount = packs.Sum(static pack =>
            pack.MeshResources.Count(static resource =>
                !resource.Passed));
        Dl1MeshCorpusResourceResult[] resources = packs
            .SelectMany(static pack => pack.MeshResources)
            .ToArray();
        int presentationAttemptedCount = resources.Count(
            static resource =>
                resource.Presentation is not null);
        int presentationValidatedCount = resources.Count(
            static resource =>
                resource.Presentation?.Passed == true);
        int presentationRenderableCount = resources.Count(
            static resource =>
                resource.Presentation is
                {
                    Passed: true,
                    Disposition:
                        Dl1MeshCorpusPresentationDisposition
                            .Renderable,
                });
        int nonDisplayGeometryCount = resources.Count(
            static resource =>
                resource.Disposition ==
                    Dl1MeshCorpusDisposition
                        .NonDisplayGeometry &&
                resource.Presentation is
                {
                    Passed: true,
                    Disposition:
                        Dl1MeshCorpusPresentationDisposition
                            .ExplicitlyNonRenderable,
                });
        int renderMeshCount = resources.Sum(
            static resource =>
                resource.Presentation?.RenderMeshCount ?? 0);
        int skinnedRenderMeshCount = resources.Sum(
            static resource =>
                resource.Presentation
                    ?.SkinnedRenderMeshCount ?? 0);
        int maximumSkeletonBoneCount = resources
            .Select(static resource =>
                resource.Presentation
                    ?.SkeletonBoneCount ?? 0)
            .DefaultIfEmpty()
            .Max();
        Dl1MeshCorpusIssue[] errors = resources
            .SelectMany(static resource => resource.Issues)
            .Where(static issue =>
                issue.Severity ==
                    Dl1MeshCorpusIssueSeverity.Error)
            .ToArray();
        Dl1MeshCorpusIssue[] warnings = resources
            .SelectMany(static resource => resource.Issues)
            .Where(static issue =>
                issue.Severity ==
                    Dl1MeshCorpusIssueSeverity.Warning)
            .ToArray();
        return new InstalledMeshCorpusReport(
            "dl-reanimated-dl1-type272-corpus-v2",
            complete,
            startedUtc,
            complete ? DateTimeOffset.UtcNow : null,
            install.InstallPath,
            build,
            new InstalledMeshCorpusSummary(
                packs.Count,
                descriptorMeshCount,
                meshResourceCount,
                geometryCount,
                metadataCount,
                blockedCount,
                presentationAttemptedCount,
                presentationValidatedCount,
                presentationRenderableCount,
                nonDisplayGeometryCount,
                renderMeshCount,
                skinnedRenderMeshCount,
                maximumSkeletonBoneCount,
                packs.Sum(static pack =>
                    pack.MeshResources.Sum(static resource =>
                        resource.VertexCount)),
                packs.Sum(static pack =>
                    pack.MeshResources.Sum(static resource =>
                        resource.IndexCount)),
                packs.Sum(static pack =>
                    pack.MeshResources.Sum(static resource =>
                        resource.MorphChannelCount)),
                packs.Sum(static pack =>
                    pack.MeshResources.Sum(static resource =>
                        resource.DecodedMorphChannelCount)),
                errors
                    .GroupBy(static issue => issue.Code)
                    .OrderBy(static group => group.Key)
                    .ToDictionary(
                        static group => group.Key,
                        static group => group.Count(),
                        StringComparer.Ordinal),
                packs
                    .SelectMany(static pack =>
                        pack.MeshResources.SelectMany(
                            resource =>
                                resource.Issues
                                    .Where(static issue =>
                                        issue.Severity ==
                                            Dl1MeshCorpusIssueSeverity
                                                .Error)
                                    .Select(issue =>
                                        (pack.PackPath,
                                            Resource: resource,
                                            issue.Code))))
                    .DistinctBy(static row =>
                        (row.PackPath,
                            row.Resource.ResourceIndex,
                            row.Resource.ResourceName,
                            row.Code))
                    .GroupBy(static row => row.Code)
                    .OrderBy(static group => group.Key)
                    .ToDictionary(
                        static group => group.Key,
                        static group => group.Count(),
                        StringComparer.Ordinal),
                warnings
                    .GroupBy(static issue => issue.Code)
                    .OrderBy(static group => group.Key)
                    .ToDictionary(
                        static group => group.Key,
                        static group => group.Count(),
                        StringComparer.Ordinal),
                meshResourceCount /
                    Math.Max(elapsed.TotalSeconds, 0.001),
                Process.GetCurrentProcess()
                    .PeakWorkingSet64),
            preflightFailures,
            packs);
    }

    private static async Task WriteReportAtomicAsync(
        string reportPath,
        InstalledMeshCorpusReport report,
        CancellationToken cancellationToken)
    {
        string temporaryPath = string.Concat(
            reportPath,
            ".",
            Guid.NewGuid().ToString("N"),
            ".tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    report,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(
                temporaryPath,
                reportPath,
                overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static string ResolveReportPath()
    {
        string? configured = Environment.GetEnvironmentVariable(
            ReportEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                FindRepositoryRoot(),
                "artifacts",
                "validation",
                "dl1-mesh-corpus-1.55.json")
            : Path.GetFullPath(configured);
    }

    private static string ResolveCachePath()
    {
        string? configured = Environment.GetEnvironmentVariable(
            CacheEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder
                        .LocalApplicationData),
                "DLReAnimated",
                "Cache",
                "Rp6lCorpus")
            : Path.GetFullPath(configured);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "DLReAnimated.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "DLReAnimated.slnx was not found above the test output directory.");
    }

    private static string BuildFailureMessage(
        InstalledMeshCorpusReport report)
    {
        string[] failures = report.Packs
            .SelectMany(pack => pack.Issues.Select(issue =>
                $"{Path.GetFileName(pack.PackPath)}: {issue.Code}: {issue.Message}").Concat(
                pack.MeshResources
                    .Where(static resource => !resource.Passed)
                    .SelectMany(resource =>
                        resource.Issues
                            .Where(static issue =>
                                issue.Severity ==
                                    Dl1MeshCorpusIssueSeverity.Error)
                            .Select(issue =>
                                $"{Path.GetFileName(pack.PackPath)}#{resource.ResourceIndex} '{resource.ResourceName}': {issue.Code}: {issue.Message}"))))
            .Take(100)
            .ToArray();
        return string.Join(
            Environment.NewLine,
            [
                $"{report.Summary.BlockedCount:N0} of {report.Summary.MeshResourceCount:N0} decoded type-272 resources are blocked. Full report: {ResolveReportPath()}",
                .. failures,
            ]);
    }

    private sealed record InstalledMeshCorpusReport(
        string Format,
        bool Complete,
        DateTimeOffset StartedUtc,
        DateTimeOffset? CompletedUtc,
        string InstallPath,
        Dl1InstalledBuildFingerprint Build,
        InstalledMeshCorpusSummary Summary,
        IReadOnlyList<CorpusPreflightFailure> PreflightFailures,
        IReadOnlyList<Dl1MeshCorpusPackResult> Packs);

    private sealed record InstalledMeshCorpusSummary(
        int PackCount,
        int DescriptorMeshResourceCount,
        int MeshResourceCount,
        int GeometryDecodedCount,
        int MetadataContainerCount,
        int BlockedCount,
        int PresentationAttemptedCount,
        int PresentationValidatedCount,
        int PresentationRenderableCount,
        int NonDisplayGeometryCount,
        int RenderMeshCount,
        int SkinnedRenderMeshCount,
        int MaximumSkeletonBoneCount,
        long VertexCount,
        long IndexCount,
        int MorphChannelCount,
        int DecodedMorphChannelCount,
        IReadOnlyDictionary<string, int> ErrorIssueCountsByCode,
        IReadOnlyDictionary<string, int>
            BlockedResourceCountsByCode,
        IReadOnlyDictionary<string, int> WarningIssueCountsByCode,
        double MeshesPerSecond,
        long PeakWorkingSetBytes);

    private sealed record CorpusPreflightFailure(
        string PackPath,
        string ErrorType,
        string Message);
}
