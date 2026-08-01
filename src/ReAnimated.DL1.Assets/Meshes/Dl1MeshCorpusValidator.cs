using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.DL1.Assets.Meshes;

public enum Dl1MeshCorpusDisposition
{
    GeometryDecoded,
    NonDisplayGeometry,
    MetadataOnlyContainer,
    Blocked,
}

public enum Dl1MeshCorpusIssueSeverity
{
    Warning,
    Error,
}

public sealed record Dl1MeshCorpusIssue(
    string Code,
    Dl1MeshCorpusIssueSeverity Severity,
    string Message);

public enum Dl1MeshCorpusPresentationDisposition
{
    Renderable,
    ExplicitlyNonRenderable,
}

/// <summary>
/// Bounded presentation facts supplied by an optional higher-layer callback.
/// The assets assembly deliberately knows nothing about App or renderer
/// contracts; callers perform their own adapter and render validation and
/// return only these stable counts and diagnostics.
/// </summary>
public sealed record Dl1MeshCorpusPresentationResult(
    Dl1MeshCorpusPresentationDisposition Disposition,
    int RenderMeshCount,
    int SkinnedRenderMeshCount,
    int SkeletonBoneCount,
    IReadOnlyList<Dl1MeshCorpusIssue> Issues)
{
    public bool Passed =>
        Disposition switch
        {
            Dl1MeshCorpusPresentationDisposition.Renderable =>
                RenderMeshCount > 0,
            Dl1MeshCorpusPresentationDisposition
                .ExplicitlyNonRenderable =>
                RenderMeshCount == 0 &&
                SkinnedRenderMeshCount == 0,
            _ => false,
        } &&
        SkinnedRenderMeshCount >= 0 &&
        SkinnedRenderMeshCount <= RenderMeshCount &&
        SkeletonBoneCount >= 0 &&
        Issues is not null &&
        Issues.All(static issue =>
            issue.Severity != Dl1MeshCorpusIssueSeverity.Error);
}

public delegate ValueTask<Dl1MeshCorpusPresentationResult>
    Dl1MeshCorpusPresentationValidator(
        Rp6lResourceDescriptor resource,
        Dl1MeshData mesh,
        CancellationToken cancellationToken);

public sealed record Dl1MeshCorpusResourceResult(
    int ResourceIndex,
    string ResourceName,
    int ItemCount,
    Dl1MeshCorpusDisposition Disposition,
    bool HasDecodedGeometry,
    bool IsSkinned,
    int EntityCount,
    int BoneCount,
    int HelperCount,
    int SurfaceCount,
    int LodCount,
    long VertexCount,
    long IndexCount,
    int MaterialSlotCount,
    int DecodedMaterialSlotCount,
    int SkinPaletteCount,
    int MorphChannelCount,
    int DecodedMorphChannelCount,
    IReadOnlyList<Dl1MeshCorpusIssue> Issues)
{
    public Dl1MeshCorpusPresentationResult? Presentation { get; init; }

    public bool Passed =>
        Disposition != Dl1MeshCorpusDisposition.Blocked &&
        (Presentation is null || Presentation.Passed) &&
        Issues.All(static issue =>
            issue.Severity != Dl1MeshCorpusIssueSeverity.Error);
}

public sealed record Dl1MeshCorpusPackResult(
    string PackPath,
    int Priority,
    string? ArchiveFingerprint,
    long ArchiveLength,
    int ResourceCount,
    int MeshResourceCount,
    IReadOnlyList<Dl1MeshCorpusResourceResult> MeshResources,
    IReadOnlyList<Dl1MeshCorpusIssue> Issues)
{
    public bool Passed =>
        Issues.All(static issue =>
            issue.Severity != Dl1MeshCorpusIssueSeverity.Error) &&
        MeshResources.All(static resource => resource.Passed);
}

public sealed record Dl1MeshCorpusValidationOptions
{
    public int MaximumPackCount { get; init; } = 4_096;

    public int MaximumMeshResourceCount { get; init; } = 100_000;

    public int MaximumIssuesPerResource { get; init; } = 128;

    internal void Validate()
    {
        if (MaximumPackCount <= 0 ||
            MaximumMeshResourceCount <= 0 ||
            MaximumIssuesPerResource <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Dl1MeshCorpusValidationOptions),
                "Mesh-corpus validation limits must be positive.");
        }
    }
}

public sealed class Dl1MeshCorpusValidator
{
    private readonly Rp6lChunkCache _chunkCache;
    private readonly Dl1MeshCorpusValidationOptions _options;
    private readonly Dl1MeshCorpusPresentationValidator?
        _presentationValidator;

    public Dl1MeshCorpusValidator(
        Rp6lChunkCache chunkCache,
        Dl1MeshCorpusValidationOptions? options = null,
        Dl1MeshCorpusPresentationValidator? presentationValidator = null)
    {
        ArgumentNullException.ThrowIfNull(chunkCache);
        _chunkCache = chunkCache;
        _options = options ?? new Dl1MeshCorpusValidationOptions();
        _options.Validate();
        _presentationValidator = presentationValidator;
    }

    public async Task<IReadOnlyList<Dl1MeshCorpusPackResult>> ValidateAsync(
        IEnumerable<RpackSource> sources,
        Func<IReadOnlyList<Dl1MeshCorpusPackResult>,
            CancellationToken,
            Task>? checkpoint = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        RpackSource[] stableSources = sources
            .OrderByDescending(static source => source.Priority)
            .ThenBy(static source =>
                source.Path,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (stableSources.Length > _options.MaximumPackCount)
        {
            throw new InvalidDataException(
                $"The corpus contains {stableSources.Length:N0} packs, above the bounded {_options.MaximumPackCount:N0}-pack limit.");
        }

        List<Dl1MeshCorpusPackResult> results =
            new(stableSources.Length);
        int totalMeshResources = 0;
        foreach (RpackSource source in stableSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Opening {source.Path}");
            Dl1MeshCorpusPackResult pack;
            try
            {
                Rp6lArchive archive = await Rp6lArchive.OpenAsync(
                    source.Path,
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                int meshCount = archive.Resources.Count(static resource =>
                    resource.ResourceType == Rp6lResourceTypes.Mesh);
                totalMeshResources = checked(
                    totalMeshResources + meshCount);
                if (totalMeshResources >
                    _options.MaximumMeshResourceCount)
                {
                    throw new InvalidDataException(
                        $"The corpus contains more than the bounded {_options.MaximumMeshResourceCount:N0} type-{Rp6lResourceTypes.Mesh} resources.");
                }

                pack = await ValidateArchiveAsync(
                    source,
                    archive,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsLocalFailure(exception, cancellationToken))
            {
                pack = new Dl1MeshCorpusPackResult(
                    Path.GetFullPath(source.Path),
                    source.Priority,
                    null,
                    File.Exists(source.Path)
                        ? new FileInfo(source.Path).Length
                        : 0,
                    0,
                    0,
                    [],
                    [
                        new Dl1MeshCorpusIssue(
                            "DL1CORPUS001",
                            Dl1MeshCorpusIssueSeverity.Error,
                            $"{exception.GetType().Name}: {exception.Message}"),
                    ]);
            }

            results.Add(pack);
            if (checkpoint is not null)
            {
                await checkpoint(
                    results.ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    public Dl1MeshCorpusResourceResult ValidateDecodedMesh(
        Rp6lResourceDescriptor resource,
        Dl1MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(mesh);
        if (resource.ResourceType != Rp6lResourceTypes.Mesh)
        {
            throw new ArgumentException(
                "Corpus validation accepts only type-272 mesh resources.",
                nameof(resource));
        }

        List<Dl1MeshCorpusIssue> issues = [];
        bool rawBindPosePreviewOnly =
            mesh.Rig is null &&
            mesh.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "DL1MESH014" &&
                diagnostic.Severity ==
                    Dl1MeshDiagnosticSeverity.Error) &&
            Dl1RigPromotionPolicy
                .CanPublishRawBindPosePreview(
                    mesh.Hierarchy,
                    mesh.Surfaces);
        foreach (Dl1MeshDiagnostic diagnostic in mesh.Diagnostics)
        {
            Dl1MeshCorpusIssueSeverity? severity =
                diagnostic.Severity switch
                {
                    Dl1MeshDiagnosticSeverity.Error
                        when diagnostic.Code == "DL1MESH014" &&
                             rawBindPosePreviewOnly =>
                        Dl1MeshCorpusIssueSeverity.Warning,
                    Dl1MeshDiagnosticSeverity.Error =>
                        Dl1MeshCorpusIssueSeverity.Error,
                    Dl1MeshDiagnosticSeverity.Warning =>
                        Dl1MeshCorpusIssueSeverity.Warning,
                    _ => null,
                };
            if (severity is not null)
            {
                AddIssue(
                    issues,
                    diagnostic.Code,
                    severity.Value,
                    diagnostic.Message);
            }
        }

        Dl1MeshCorpusDisposition disposition;
        if (mesh.ContainerLayout ==
            Dl1MeshContainerLayout.ThreeItemMetadataOnly)
        {
            disposition =
                Dl1MeshCorpusDisposition.MetadataOnlyContainer;
            if (resource.ItemCount != 3 ||
                mesh.Surfaces.Count != 0 ||
                mesh.HasDecodedGeometry)
            {
                AddError(
                    issues,
                    "DL1CORPUS010",
                    "A metadata-only container must have exactly three items and no decoded geometry.");
            }
        }
        else if (mesh.Surfaces.Count == 0)
        {
            disposition = Dl1MeshCorpusDisposition.Blocked;
            AddError(
                issues,
                "DL1CORPUS011",
                "A split-GPU mesh has no decoded surfaces and is not a proven metadata-only container.");
        }
        else
        {
            disposition = Dl1MeshCorpusDisposition.GeometryDecoded;
            ValidateGeometry(mesh, issues);
        }

        if (!mesh.Hierarchy.IsStructurallyValid)
        {
            AddError(
                issues,
                "DL1CORPUS012",
                "The compact hierarchy is structurally invalid.");
        }

        ValidateHierarchy(
            mesh,
            issues,
            rawBindPosePreviewOnly);
        ValidateSkinning(mesh, issues);
        ValidateMorphs(mesh, issues);
        if (issues.Any(static issue =>
                issue.Severity == Dl1MeshCorpusIssueSeverity.Error))
        {
            disposition = Dl1MeshCorpusDisposition.Blocked;
        }

        return CreateResourceResult(
            resource,
            mesh,
            disposition,
            issues);
    }

    public async ValueTask<Dl1MeshCorpusResourceResult>
        ValidateDecodedMeshPresentationAsync(
            Rp6lResourceDescriptor resource,
            Dl1MeshData mesh,
            CancellationToken cancellationToken = default)
    {
        Dl1MeshCorpusResourceResult structural =
            ValidateDecodedMesh(resource, mesh);
        if (_presentationValidator is null ||
            !structural.HasDecodedGeometry ||
            structural.Disposition ==
                Dl1MeshCorpusDisposition.MetadataOnlyContainer)
        {
            return structural;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Dl1MeshCorpusPresentationResult presentation;
        try
        {
            presentation =
                await _presentationValidator(
                        resource,
                        mesh,
                        cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The presentation validator returned no result.");
        }
        catch (Exception exception) when (
            IsLocalFailure(exception, cancellationToken))
        {
            presentation =
                new Dl1MeshCorpusPresentationResult(
                    Dl1MeshCorpusPresentationDisposition
                        .Renderable,
                    0,
                    0,
                    0,
                    [
                        new Dl1MeshCorpusIssue(
                            "DL1PRESENT002",
                            Dl1MeshCorpusIssueSeverity.Error,
                            $"{exception.GetType().Name}: {exception.Message}"),
                    ]);
        }

        List<Dl1MeshCorpusIssue> presentationIssues =
            presentation.Issues?.ToList() ??
            [
                new Dl1MeshCorpusIssue(
                    "DL1PRESENT003",
                    Dl1MeshCorpusIssueSeverity.Error,
                    "The presentation validator returned no diagnostics collection."),
            ];
        if (presentation.Disposition ==
                Dl1MeshCorpusPresentationDisposition.Renderable &&
            presentation.RenderMeshCount <= 0)
        {
            AddError(
                presentationIssues,
                "DL1PRESENT001",
                "A geometry-bearing resource produced no render meshes.");
        }

        if (presentation.Disposition ==
                Dl1MeshCorpusPresentationDisposition
                    .ExplicitlyNonRenderable &&
            presentation.RenderMeshCount != 0)
        {
            AddError(
                presentationIssues,
                "DL1PRESENT003",
                "An explicitly non-renderable resource cannot report render meshes.");
        }

        if (!Enum.IsDefined(presentation.Disposition) ||
            presentation.RenderMeshCount < 0 ||
            presentation.SkinnedRenderMeshCount < 0 ||
            presentation.SkinnedRenderMeshCount >
                presentation.RenderMeshCount ||
            presentation.SkeletonBoneCount < 0)
        {
            AddError(
                presentationIssues,
                "DL1PRESENT003",
                "The presentation validator returned invalid mesh or skeleton counts.");
        }

        Dl1MeshCorpusIssue[] boundedPresentationIssues =
            BoundIssues(presentationIssues);
        presentation = presentation with
        {
            Issues = boundedPresentationIssues,
        };
        Dl1MeshCorpusIssue[] combinedIssues = BoundIssues(
            [
                .. boundedPresentationIssues,
                .. structural.Issues,
            ]);
        bool presentationFailed = !presentation.Passed;
        return structural with
        {
            Disposition = presentationFailed ||
                structural.Disposition ==
                    Dl1MeshCorpusDisposition.Blocked
                ? Dl1MeshCorpusDisposition.Blocked
                : presentation.Disposition ==
                    Dl1MeshCorpusPresentationDisposition
                        .ExplicitlyNonRenderable
                    ? Dl1MeshCorpusDisposition
                        .NonDisplayGeometry
                    : structural.Disposition,
            Issues = combinedIssues,
            Presentation = presentation,
        };
    }

    private async Task<Dl1MeshCorpusPackResult> ValidateArchiveAsync(
        RpackSource source,
        Rp6lArchive archive,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Rp6lResourceDescriptor[] resources = archive.Resources
            .Where(static resource =>
                resource.ResourceType == Rp6lResourceTypes.Mesh)
            .ToArray();
        List<Dl1MeshCorpusResourceResult> results =
            new(resources.Length);
        for (int index = 0; index < resources.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rp6lResourceDescriptor resource = resources[index];
            if ((index & 0x3F) == 0)
            {
                progress?.Report(
                    $"{Path.GetFileName(source.Path)}: type-272 {index:N0}/{resources.Length:N0}");
            }

            try
            {
                Dl1MeshData mesh =
                    await Dl1MeshResourceDecoder.DecodeAsync(
                        archive,
                        resource,
                        _chunkCache,
                        cancellationToken).ConfigureAwait(false);
                results.Add(
                    await ValidateDecodedMeshPresentationAsync(
                            resource,
                            mesh,
                            cancellationToken)
                        .ConfigureAwait(false));
            }
            catch (Exception exception) when (
                IsLocalFailure(exception, cancellationToken))
            {
                results.Add(new Dl1MeshCorpusResourceResult(
                    resource.Index,
                    resource.Name,
                    resource.ItemCount,
                    Dl1MeshCorpusDisposition.Blocked,
                    false,
                    false,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    [
                        new Dl1MeshCorpusIssue(
                            "DL1CORPUS002",
                            Dl1MeshCorpusIssueSeverity.Error,
                            $"{exception.GetType().Name}: {exception.Message}"),
                    ]));
            }
        }

        return new Dl1MeshCorpusPackResult(
            archive.Path,
            source.Priority,
            archive.CacheIdentity,
            archive.File.Length,
            archive.Resources.Count,
            resources.Length,
            results,
            []);
    }

    private void ValidateGeometry(
        Dl1MeshData mesh,
        List<Dl1MeshCorpusIssue> issues)
    {
        HashSet<(int EntityIndex, int LodIndex)> surfaceKeys = [];
        foreach (Dl1MeshSurface surface in mesh.Surfaces)
        {
            if (!surfaceKeys.Add((surface.EntityIndex, surface.LodIndex)))
            {
                AddError(
                    issues,
                    "DL1CORPUS020",
                    $"Entity {surface.EntityIndex} LOD {surface.LodIndex} has duplicate surfaces.");
            }

            if (surface.EntityIndex < 0 ||
                surface.EntityIndex >= mesh.Hierarchy.Entities.Count)
            {
                AddError(
                    issues,
                    "DL1CORPUS021",
                    $"Surface '{surface.Name}' references hierarchy entity {surface.EntityIndex} outside {mesh.Hierarchy.Entities.Count} entities.");
            }

            if (surface.LodIndex < 0)
            {
                AddError(
                    issues,
                    "DL1CORPUS022",
                    $"Surface '{surface.Name}' has a negative LOD index.");
            }

            if (surface.VertexCount <= 0 ||
                surface.VertexCount != surface.Vertices.Count ||
                surface.VertexLayout.Stride <= 0 ||
                surface.VertexBuffer.ByteLength !=
                    (long)surface.VertexCount *
                    surface.VertexLayout.Stride)
            {
                AddError(
                    issues,
                    "DL1CORPUS023",
                    $"Surface '{surface.Name}' has inconsistent vertex topology or buffer bounds.");
            }

            if (surface.IndexCount <= 0 ||
                surface.IndexCount % 3 != 0 ||
                surface.IndexCount != surface.Indices.Count ||
                surface.IndexBuffer.ByteLength !=
                    (long)surface.IndexCount * sizeof(ushort))
            {
                AddError(
                    issues,
                    "DL1CORPUS024",
                    $"Surface '{surface.Name}' does not contain a bounded triangle-list index buffer.");
            }

            if (surface.Indices.Any(index =>
                    index >= surface.VertexCount))
            {
                AddError(
                    issues,
                    "DL1CORPUS025",
                    $"Surface '{surface.Name}' contains an out-of-range vertex index.");
            }

            if (!surface.VertexLayout.Elements.Any(static element =>
                    element.Semantic == Dl1VertexSemantic.Position &&
                    element.Format is
                        Dl1VertexElementFormat.Float3 or
                        Dl1VertexElementFormat.Half4))
            {
                AddError(
                    issues,
                    "DL1CORPUS026",
                    $"Surface '{surface.Name}' has no decoded float3 or half4 position declaration.");
            }

            ValidateVertices(mesh, surface, issues);
            ValidateSubmeshes(mesh, surface, issues);
        }

        foreach (IGrouping<int, Dl1MeshSurface> group in
                 mesh.Surfaces.GroupBy(static surface =>
                     surface.EntityIndex))
        {
            int[] lods = group
                .Select(static surface => surface.LodIndex)
                .Order()
                .ToArray();
            if (!lods.SequenceEqual(
                    Enumerable.Range(0, lods.Length)))
            {
                AddError(
                    issues,
                    "DL1CORPUS027",
                    $"Entity {group.Key} has non-contiguous LOD indexes [{string.Join(", ", lods)}].");
            }
        }
    }

    private void ValidateVertices(
        Dl1MeshData mesh,
        Dl1MeshSurface surface,
        List<Dl1MeshCorpusIssue> issues)
    {
        HashSet<ushort> referencedVertices =
            surface.Indices.ToHashSet();
        HashSet<ushort> nonDisplayOnlyVertices =
            BuildNonDisplayOnlyVertexSet(mesh, surface);
        _ = Dl1RetailStockGeometryPolicy
            .TryGetRawGpuNonFiniteUv0Vertices(
                mesh,
                surface,
                out HashSet<ushort> stockRawGpuUvVertices,
                out string stockRawGpuPolicyLabel);
        Dictionary<string, (int Count, int FirstIndex)>
            referencedNonFinite = new(StringComparer.Ordinal);
        Dictionary<string, (int Count, int FirstIndex)>
            nonDisplayNonFinite = new(StringComparer.Ordinal);
        Dictionary<string, (int Count, int FirstIndex)>
            stockRawGpuNonFinite = new(StringComparer.Ordinal);
        Dictionary<string, (int Count, int FirstIndex)>
            unreferencedNonFinite = new(StringComparer.Ordinal);
        for (int vertexIndex = 0;
             vertexIndex < surface.Vertices.Count;
             vertexIndex++)
        {
            Dl1MeshVertex vertex =
                surface.Vertices[vertexIndex];
            string? attribute =
                !IsFinite(vertex.Position) ? "position" :
                !IsFinite(vertex.Normal) ? "normal" :
                !IsFinite(vertex.Tangent) ? "tangent" :
                !IsFinite(vertex.TextureCoordinate0) ? "UV0" :
                !IsFinite(vertex.TextureCoordinate1) ? "UV1" :
                !IsFinite(vertex.Color) ? "color" :
                !IsFinite(vertex.BlendWeights) ? "blend weights" :
                null;
            if (attribute is not null)
            {
                Dictionary<string, (int Count, int FirstIndex)>
                    destination;
                if (vertexIndex <= ushort.MaxValue &&
                    referencedVertices.Contains(
                        checked((ushort)vertexIndex)))
                {
                    ushort referencedIndex =
                        checked((ushort)vertexIndex);
                    destination = attribute switch
                    {
                        "UV0" when
                            nonDisplayOnlyVertices.Contains(
                                referencedIndex) =>
                            nonDisplayNonFinite,
                        "UV0" when
                            stockRawGpuUvVertices.Contains(
                                referencedIndex) =>
                            stockRawGpuNonFinite,
                        _ => referencedNonFinite,
                    };
                }
                else
                {
                    destination = unreferencedNonFinite;
                }

                destination[attribute] =
                    destination.TryGetValue(
                        attribute,
                        out (int Count, int FirstIndex) existing)
                    ? (existing.Count + 1, existing.FirstIndex)
                    : (1, vertexIndex);
            }
        }

        foreach ((string attribute, (int count, int firstIndex))
                 in referencedNonFinite)
        {
            AddError(
                issues,
                "DL1CORPUS028",
                $"Surface '{surface.Name}' has {count:N0} referenced vertices with non-finite {attribute} data; the first is vertex {firstIndex}.");
        }

        foreach ((string attribute, (int count, int firstIndex))
                 in nonDisplayNonFinite)
        {
            AddIssue(
                issues,
                "DL1CORPUS034",
                Dl1MeshCorpusIssueSeverity.Warning,
                $"Surface '{surface.Name}' has {count:N0} referenced vertices with non-finite {attribute} data used exclusively by validated non-display material draw parts; the first is vertex {firstIndex}. Raw vertex values are retained and those parts are excluded from preview topology.");
        }

        foreach ((string attribute, (int count, int firstIndex))
                 in stockRawGpuNonFinite)
        {
            AddIssue(
                issues,
                "DL1CORPUS035",
                Dl1MeshCorpusIssueSeverity.Warning,
                $"Surface '{surface.Name}' has {count:N0} referenced vertices with non-finite {attribute} data matching the exact content-fingerprinted stock DL1 1.55 raw-GPU anomaly '{stockRawGpuPolicyLabel}'; the first is vertex {firstIndex}. Raw +/-Infinity values are retained and published unchanged. The neutral base-color preview is fidelity-limited because the exact retail material technique is not emulated.");
        }

        foreach ((string attribute, (int count, int firstIndex))
                 in unreferencedNonFinite)
        {
            AddIssue(
                issues,
                "DL1CORPUS029",
                Dl1MeshCorpusIssueSeverity.Warning,
                $"Surface '{surface.Name}' has {count:N0} unreferenced vertices with non-finite {attribute} data; the first is vertex {firstIndex}, and all are excluded from preview topology.");
        }
    }

    private static HashSet<ushort> BuildNonDisplayOnlyVertexSet(
        Dl1MeshData mesh,
        Dl1MeshSurface surface)
    {
        HashSet<ushort> nonDisplayVertices = [];
        HashSet<ushort> visibleVertices = [];
        if (surface.Submeshes.Count == 0)
        {
            HashSet<ushort> destination =
                IsPreviewNonDisplayMaterial(
                    mesh,
                    surface.MaterialSlotIndex)
                    ? nonDisplayVertices
                    : visibleVertices;
            destination.UnionWith(surface.Indices);
        }
        else
        {
            foreach (Dl1MeshSubmesh submesh in surface.Submeshes)
            {
                if (submesh.FirstIndex < 0 ||
                    submesh.IndexCount <= 0 ||
                    submesh.FirstIndex >
                        surface.Indices.Count ||
                    submesh.IndexCount >
                        surface.Indices.Count -
                        submesh.FirstIndex)
                {
                    continue;
                }

                HashSet<ushort> destination =
                    IsPreviewNonDisplayMaterial(
                        mesh,
                        submesh.MaterialSlotIndex)
                        ? nonDisplayVertices
                        : visibleVertices;
                for (int indexOffset = submesh.FirstIndex;
                     indexOffset <
                     submesh.FirstIndex + submesh.IndexCount;
                     indexOffset++)
                {
                    destination.Add(
                        surface.Indices[indexOffset]);
                }
            }
        }

        nonDisplayVertices.ExceptWith(visibleVertices);
        return nonDisplayVertices;
    }

    private void ValidateSubmeshes(
        Dl1MeshData mesh,
        Dl1MeshSurface surface,
        List<Dl1MeshCorpusIssue> issues)
    {
        if (surface.Submeshes.Count == 0)
        {
            AddError(
                issues,
                "DL1CORPUS030",
                $"Surface '{surface.Name}' has no decoded submeshes.");
            return;
        }

        foreach (Dl1MeshSubmesh submesh in surface.Submeshes)
        {
            if (submesh.FirstIndex < 0 ||
                submesh.IndexCount <= 0 ||
                submesh.IndexCount % 3 != 0 ||
                submesh.FirstIndex > surface.IndexCount ||
                submesh.IndexCount >
                    surface.IndexCount - submesh.FirstIndex)
            {
                AddError(
                    issues,
                    "DL1CORPUS031",
                    $"Surface '{surface.Name}' submesh {submesh.Index} has an invalid triangle range.");
            }

            if (submesh.MaterialSlotIndex >=
                    mesh.MaterialSlots.Count ||
                submesh.MaterialSlotIndex < -1)
            {
                AddError(
                    issues,
                    "DL1CORPUS032",
                    $"Surface '{surface.Name}' submesh {submesh.Index} references material slot {submesh.MaterialSlotIndex} outside {mesh.MaterialSlots.Count} slots.");
            }
        }

        if (mesh.MaterialSlots.Any(static slot =>
                slot.BindingStatus ==
                    Dl1MaterialBindingStatus
                        .DeclaredSlotNameUnresolved))
        {
            AddError(
                issues,
                "DL1CORPUS033",
                "One or more declared material-slot names remain unresolved.");
        }
    }

    private void ValidateHierarchy(
        Dl1MeshData mesh,
        List<Dl1MeshCorpusIssue> issues,
        bool rawBindPosePreviewOnly)
    {
        foreach (CompactMeshEntity entity in mesh.Hierarchy.Entities)
        {
            if (!entity.Bounds.IsFinite ||
                !entity.LocalMatrix.IsFinite ||
                (!entity.ReferenceMatrix.IsFinite &&
                 !entity.IsPlainStaticRoot))
            {
                AddError(
                    issues,
                    "DL1CORPUS040",
                    $"Hierarchy entity {entity.Index} ('{entity.Name}') has non-finite bounds or bind transforms.");
            }
        }

        if (mesh.Hierarchy.Bones.Count > 0 && mesh.Rig is null)
        {
            if (rawBindPosePreviewOnly)
            {
                AddIssue(
                    issues,
                    "DL1CORPUS043",
                    Dl1MeshCorpusIssueSeverity.Warning,
                    "The bone-bearing hierarchy is validated for exact raw bind-pose matrix preview only. Its non-TRS rows are outside every decoded skin palette; retargeting, animation evaluation, bone editing, and export remain disabled because no authoring rig was fabricated.");
            }
            else
            {
                AddError(
                    issues,
                    "DL1CORPUS041",
                    "A bone-bearing hierarchy has no derived authoring rig.");
            }
        }

        if (mesh.Rig is not null &&
            mesh.Rig.BoneCount <= 0)
        {
            AddError(
                issues,
                "DL1CORPUS042",
                "The derived authoring rig is empty.");
        }
    }

    private void ValidateSkinning(
        Dl1MeshData mesh,
        List<Dl1MeshCorpusIssue> issues)
    {
        IReadOnlyList<CompactMatrix3x4>? worldMatrices =
            mesh.Hierarchy.IsStructurallyValid
                ? mesh.Hierarchy.ReconstructGlobalMatrices()
                : null;
        foreach (Dl1MeshSurface surface in mesh.Surfaces)
        {
            foreach (Dl1MeshSubmesh submesh in surface.Submeshes)
            {
                IReadOnlyList<short> palette =
                    submesh.BonePaletteEntityIndexes;
                if (palette.Count == 0)
                {
                    continue;
                }

                int animationEntityCount =
                    mesh.Hierarchy.AnimationEntityCountCandidate;
                foreach (short entityIndex in palette)
                {
                    if (entityIndex < 0 ||
                        entityIndex >= animationEntityCount)
                    {
                        AddError(
                            issues,
                            "DL1CORPUS050",
                            $"Surface '{surface.Name}' submesh {submesh.Index} references skin-palette entity {entityIndex} outside the {animationEntityCount}-entity animation hierarchy.");
                    }
                }

                if (surface.EntityIndex < 0 ||
                    surface.EntityIndex >=
                        mesh.Hierarchy.Entities.Count)
                {
                    continue;
                }

                CompactMeshEntity surfaceEntity =
                    mesh.Hierarchy.Entities[surface.EntityIndex];
                if (!surfaceEntity.EntityType.HasFlag(
                        CompactMeshEntityType.SkinnedMesh))
                {
                    continue;
                }

                bool hasBlendWeights =
                    surface.VertexLayout.Elements.Any(static element =>
                        element.Semantic ==
                            Dl1VertexSemantic.BlendWeights &&
                        element.Format ==
                            Dl1VertexElementFormat
                                .Byte4Normalized);
                bool hasBlendIndices =
                    surface.VertexLayout.Elements.Any(static element =>
                        element.Semantic ==
                            Dl1VertexSemantic.BlendIndices &&
                        element.Format ==
                            Dl1VertexElementFormat.Byte4);
                Dl1SkinBindingMode expectedBindingMode =
                    Dl1SkinBindingPolicy.Classify(
                        surface.VertexLayout,
                        surface.Vertices,
                        surface.Indices,
                        submesh,
                        surfaceEntity,
                        worldMatrices is not null &&
                        (uint)surface.EntityIndex <
                            (uint)worldMatrices.Count
                            ? worldMatrices[surface.EntityIndex]
                            : null);
                if (submesh.SkinBindingMode !=
                    expectedBindingMode)
                {
                    AddError(
                        issues,
                        "DL1CORPUS057",
                        $"Skinned surface '{surface.Name}' submesh {submesh.Index} declares {submesh.SkinBindingMode}, but its palette and vertex declaration require {expectedBindingMode}.");
                    continue;
                }

                if (hasBlendWeights != hasBlendIndices)
                {
                    AddError(
                        issues,
                        "DL1CORPUS056",
                        $"Skinned surface '{surface.Name}' submesh {submesh.Index} declares only one of the required blend-weight and blend-index streams.");
                    continue;
                }

                if (!hasBlendWeights)
                {
                    if (IsPreviewNonDisplayMaterial(
                            mesh,
                            submesh.MaterialSlotIndex))
                    {
                        AddIssue(
                            issues,
                            "DL1CORPUS058",
                            Dl1MeshCorpusIssueSeverity.Warning,
                            $"Skinned surface '{surface.Name}' submesh {submesh.Index} omits blend streams despite having {palette.Count} palette entities, but its validated non-display material is excluded from visible preview topology. The serialized palette remains decoded, but the omitted preview draw is not published as authorable skinning.");
                    }
                    else if (expectedBindingMode ==
                             Dl1SkinBindingMode
                                 .StaticEntityTransformIgnoredPalette)
                    {
                        AddIssue(
                            issues,
                            "DL1CORPUS066",
                            Dl1MeshCorpusIssueSeverity.Warning,
                            $"Skinned surface '{surface.Name}' submesh {submesh.Index} has no blend streams and retains {palette.Count} palette entries. Its finite hierarchy-element world matrix matches the named-runtime path which skips skinning setup, ignores the palette, and submits the entity/world transform as a non-skinned draw. The part is previewable but is not authorable skinning.");
                    }
                    else
                    {
                        AddError(
                            issues,
                            "DL1CORPUS055",
                            $"Skinned surface '{surface.Name}' submesh {submesh.Index} omits blend streams despite having {palette.Count} palette entities; no runtime fallback has been proven.");
                    }

                    continue;
                }

                if (expectedBindingMode ==
                    Dl1SkinBindingMode.RigidIndexedPalette)
                {
                    AddIssue(
                        issues,
                        "DL1CORPUS059",
                        Dl1MeshCorpusIssueSeverity.Warning,
                        $"Skinned surface '{surface.Name}' submesh {submesh.Index} uses the corpus-inferred DL1 1.55 rigid indexed-palette encoding: every referenced serialized weight is zero, local X selects a valid palette entry, and local Y/Z/W are zero. Consumers materialize an implicit unit X weight without changing the decoded vertex values; this rule is not yet live-game validated.");
                    continue;
                }

                int end = Math.Min(
                    surface.IndexCount,
                    checked(submesh.FirstIndex + submesh.IndexCount));
                bool reportedZeroWeight = false;
                for (int indexOffset = submesh.FirstIndex;
                     indexOffset < end;
                     indexOffset++)
                {
                    int vertexIndex = surface.Indices[indexOffset];
                    Dl1MeshVertex vertex =
                        surface.Vertices[vertexIndex];
                    Span<float> weights =
                    [
                        vertex.BlendWeights.X,
                        vertex.BlendWeights.Y,
                        vertex.BlendWeights.Z,
                        vertex.BlendWeights.W,
                    ];
                    Span<byte> indexes =
                    [
                        vertex.LocalBlendIndices.X,
                        vertex.LocalBlendIndices.Y,
                        vertex.LocalBlendIndices.Z,
                        vertex.LocalBlendIndices.W,
                    ];
                    float sum = 0;
                    for (int component = 0;
                         component < weights.Length;
                         component++)
                    {
                        float weight = weights[component];
                        if (weight < 0 || weight > 1)
                        {
                            AddError(
                                issues,
                                "DL1CORPUS051",
                                $"Surface '{surface.Name}' has an out-of-range skin weight.");
                            break;
                        }

                        if (weight > 0 &&
                            indexes[component] >= palette.Count)
                        {
                            AddError(
                                issues,
                                "DL1CORPUS052",
                                $"Surface '{surface.Name}' uses local palette index {indexes[component]} outside {palette.Count} entries.");
                            break;
                        }

                        sum += weight;
                    }

                    if ((!float.IsFinite(sum) ||
                         MathF.Abs(sum - 1) > 0.02f) &&
                        !reportedZeroWeight)
                    {
                        reportedZeroWeight = true;
                        AddError(
                            issues,
                            "DL1CORPUS053",
                            $"Surface '{surface.Name}' submesh {submesh.Index} referenced vertex {vertexIndex} has weights ({vertex.BlendWeights.X}, {vertex.BlendWeights.Y}, {vertex.BlendWeights.Z}, {vertex.BlendWeights.W}) summing to {sum}; local indexes are ({vertex.LocalBlendIndices.X}, {vertex.LocalBlendIndices.Y}, {vertex.LocalBlendIndices.Z}, {vertex.LocalBlendIndices.W}) and palette[0] is {palette[0]}.");
                    }

                    if (issues.Count >=
                        _options.MaximumIssuesPerResource)
                    {
                        return;
                    }
                }
            }
        }
    }

    private static bool IsPreviewNonDisplayMaterial(
        Dl1MeshData mesh,
        int materialSlotIndex) =>
        Dl1PreviewMaterialPolicy.IsNonDisplayMaterial(
            FindMaterialSlot(
                mesh,
                materialSlotIndex));

    private static Dl1MaterialSlot? FindMaterialSlot(
        Dl1MeshData mesh,
        int materialSlotIndex) =>
        mesh.MaterialSlots
            .FirstOrDefault(slot =>
                slot.Index == materialSlotIndex);

    private void ValidateMorphs(
        Dl1MeshData mesh,
        List<Dl1MeshCorpusIssue> issues)
    {
        Dictionary<(int EntityIndex, int LodIndex), Dl1MeshSurface>
            surfaces = mesh.Surfaces.ToDictionary(
                static surface =>
                    (surface.EntityIndex, surface.LodIndex));
        foreach (Dl1MorphTarget target in mesh.MorphTargets)
        {
            if (target.PayloadStatus ==
                Dl1MorphPayloadStatus.ChannelOnly)
            {
                AddIssue(
                    issues,
                    "DL1CORPUS064",
                    Dl1MeshCorpusIssueSeverity.Warning,
                    $"Morph channel {target.Index} ('{target.Name}') declares no node/LOD vertex-delta binding and is classified as channel inventory only.");
            }
            else if (target.PayloadStatus ==
                     Dl1MorphPayloadStatus
                         .NodeLodBindingDecoded)
            {
                AddError(
                    issues,
                    "DL1CORPUS065",
                    $"Morph target {target.Index} ('{target.Name}') has a node/LOD binding but no decoded vertex-delta payload.");
            }
            else if (target.PayloadStatus ==
                Dl1MorphPayloadStatus.VertexDeltasUnresolved)
            {
                AddError(
                    issues,
                    "DL1CORPUS060",
                    $"Morph target {target.Index} ('{target.Name}') has unresolved vertex deltas.");
            }

            foreach (Dl1MorphBinding binding in target.Bindings)
            {
                if (!surfaces.TryGetValue(
                        (binding.EntityIndex, binding.LodIndex),
                        out Dl1MeshSurface? surface) ||
                    surface.VertexCount != binding.VertexCount)
                {
                    AddError(
                        issues,
                        "DL1CORPUS061",
                        $"Morph target {target.Index} binding entity {binding.EntityIndex} LOD {binding.LodIndex} does not match a decoded surface.");
                    continue;
                }

                if (binding.DeltaByteStride != sizeof(short) * 4 ||
                    binding.PositionDeltaSets.Count !=
                        binding.LocalTargetIndexes.Count)
                {
                    AddError(
                        issues,
                        "DL1CORPUS062",
                        $"Morph target {target.Index} has an unsupported delta stride or incomplete local-target mapping.");
                }

                foreach (Dl1MorphPositionDeltaSet set in
                         binding.PositionDeltaSets)
                {
                    if (set.PositionDeltas.Count !=
                            binding.VertexCount ||
                        set.PositionDeltas.Any(static delta =>
                            !IsFinite(delta)))
                    {
                        AddError(
                            issues,
                            "DL1CORPUS063",
                            $"Morph target {target.Index} local target {set.LocalTargetIndex} has incomplete or non-finite deltas.");
                    }
                }
            }
        }
    }

    private Dl1MeshCorpusResourceResult CreateResourceResult(
        Rp6lResourceDescriptor resource,
        Dl1MeshData mesh,
        Dl1MeshCorpusDisposition disposition,
        List<Dl1MeshCorpusIssue> issues)
    {
        Dl1MeshCorpusIssue[] boundedIssues =
            BoundIssues(issues);

        return new Dl1MeshCorpusResourceResult(
            resource.Index,
            resource.Name,
            resource.ItemCount,
            disposition,
            mesh.HasDecodedGeometry,
            mesh.IsSkinned,
            mesh.Hierarchy.Entities.Count,
            mesh.Hierarchy.Bones.Count,
            mesh.Hierarchy.Helpers.Count,
            mesh.Surfaces.Count,
            mesh.Surfaces
                .Select(static surface => surface.LodIndex)
                .Distinct()
                .Count(),
            mesh.Surfaces.Sum(static surface =>
                (long)surface.VertexCount),
            mesh.Surfaces.Sum(static surface =>
                (long)surface.IndexCount),
            mesh.MaterialSlots.Count,
            mesh.MaterialSlots.Count(static slot =>
                slot.BindingStatus is
                    Dl1MaterialBindingStatus.DatabaseNameDecoded or
                    Dl1MaterialBindingStatus.Resolved),
            mesh.Surfaces
                .SelectMany(static surface => surface.Submeshes)
                .Count(static submesh =>
                    submesh.BonePaletteEntityIndexes.Count > 0),
            mesh.MorphTargets.Count,
            mesh.MorphTargets.Count(static target =>
                target.PayloadStatus ==
                    Dl1MorphPayloadStatus.VertexDeltasDecoded),
            boundedIssues);
    }

    private Dl1MeshCorpusIssue[] BoundIssues(
        List<Dl1MeshCorpusIssue> issues)
    {
        Dl1MeshCorpusIssue[] boundedIssues = issues
            .Take(_options.MaximumIssuesPerResource)
            .ToArray();
        if (issues.Count > boundedIssues.Length)
        {
            boundedIssues =
            [
                .. boundedIssues,
                new Dl1MeshCorpusIssue(
                    "DL1CORPUS099",
                    Dl1MeshCorpusIssueSeverity.Warning,
                    $"{issues.Count - boundedIssues.Length:N0} additional issues were omitted by the bounded report limit."),
            ];
        }

        return boundedIssues;
    }

    private void AddError(
        List<Dl1MeshCorpusIssue> issues,
        string code,
        string message) =>
        AddIssue(
            issues,
            code,
            Dl1MeshCorpusIssueSeverity.Error,
            message);

    private void AddIssue(
        List<Dl1MeshCorpusIssue> issues,
        string code,
        Dl1MeshCorpusIssueSeverity severity,
        string message)
    {
        if (issues.Count <= _options.MaximumIssuesPerResource &&
            !issues.Any(issue =>
                issue.Code == code &&
                issue.Severity == severity &&
                string.Equals(
                    issue.Message,
                    message,
                    StringComparison.Ordinal)))
        {
            issues.Add(new Dl1MeshCorpusIssue(
                code,
                severity,
                message));
        }
    }

    private static bool IsLocalFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException &&
            cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            InvalidOperationException or
            OverflowException or
            NotSupportedException;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}
