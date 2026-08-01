using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.DL1.Assets.Materials;

public sealed record Dl1MaterialTextureResolverOptions
{
    public static Dl1MaterialTextureResolverOptions Default { get; } = new();

    public long MaximumPreviewBytesPerMesh { get; init; } =
        128L * 1024 * 1024;

    public Dl1MaterialPackLimits MaterialPackLimits { get; init; } =
        Dl1MaterialPackLimits.Default;

    public Dl1TexturePreviewLimits TextureLimits { get; init; } =
        Dl1TexturePreviewLimits.Default;

    internal void Validate()
    {
        if (MaximumPreviewBytesPerMesh <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumPreviewBytesPerMesh));
        }

        ArgumentNullException.ThrowIfNull(MaterialPackLimits);
        ArgumentNullException.ThrowIfNull(TextureLimits);
        MaterialPackLimits.Validate();
        TextureLimits.Validate();
    }
}

/// <summary>
/// Resolves compact-mesh material database names through the retail ABDM pack,
/// then resolves texture-name hashes to catalog identities. Hash collisions,
/// unknown layouts, and unsupported texture formats stay unresolved.
/// </summary>
public sealed class Dl1MaterialTextureResolver
{
    private readonly RpackAssetProvider _rpackProvider;
    private readonly Rp6lChunkCache _chunkCache;
    private readonly string _materialPackPath;
    private readonly Dl1MaterialTextureResolverOptions _options;
    private readonly Dictionary<uint, RetailAssetRecord[]> _texturesByHash;

    public Dl1MaterialTextureResolver(
        RetailAssetCatalog catalog,
        RpackAssetProvider rpackProvider,
        Rp6lChunkCache chunkCache,
        string materialPackPath,
        Dl1MaterialTextureResolverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rpackProvider);
        ArgumentNullException.ThrowIfNull(chunkCache);
        ArgumentException.ThrowIfNullOrWhiteSpace(materialPackPath);
        _rpackProvider = rpackProvider;
        _chunkCache = chunkCache;
        _materialPackPath = Path.GetFullPath(materialPackPath);
        _options = options ?? Dl1MaterialTextureResolverOptions.Default;
        _options.Validate();
        _texturesByHash = BuildTextureHashIndex(catalog);
    }

    public async Task<Dl1MeshData> ResolveAsync(
        Dl1MeshData mesh,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.MaterialSlots.Count == 0)
        {
            return mesh;
        }

        List<Dl1MeshDiagnostic> diagnostics =
            mesh.Diagnostics.ToList();
        Dl1MaterialPackReader reader;
        try
        {
            reader = await Dl1MaterialPackReader.OpenAsync(
                _materialPackPath,
                _options.MaterialPackLimits,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            IsLocalAssetFailure(exception))
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MAT001",
                Dl1MeshDiagnosticSeverity.Warning,
                $"Retail material pack resolution is unavailable: {exception.Message}"));
            return mesh with { Diagnostics = diagnostics };
        }

        await using (reader.ConfigureAwait(false))
        {
            long remainingPreviewBytes =
                _options.MaximumPreviewBytesPerMesh;
            Dictionary<RetailAssetId, Dl1TexturePreviewData> previewCache = [];
            HashSet<RetailAssetId> failedPreviews = [];
            Dl1MaterialSlot[] slots =
                new Dl1MaterialSlot[mesh.MaterialSlots.Count];
            int resolvedMaterialCount = 0;
            int previewCount = 0;
            for (int slotIndex = 0;
                 slotIndex < mesh.MaterialSlots.Count;
                 slotIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dl1MaterialSlot slot = mesh.MaterialSlots[slotIndex];
                if (slot.ResolvedMaterial is not null)
                {
                    slots[slotIndex] = slot;
                    resolvedMaterialCount++;
                    if (slot.ResolvedMaterial.BaseColorPreview is not null)
                    {
                        previewCount++;
                    }

                    continue;
                }

                if (slot.BindingStatus is
                    Dl1MaterialBindingStatus.DeclaredSlotNameUnresolved or
                    Dl1MaterialBindingStatus.SyntheticSurfaceSlot)
                {
                    slots[slotIndex] = slot;
                    continue;
                }

                Dl1MaterialPackMaterialRecord? material;
                try
                {
                    material = await reader.ReadMaterialAsync(
                        slot.DatabaseName,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    IsLocalAssetFailure(exception))
                {
                    diagnostics.Add(new Dl1MeshDiagnostic(
                        "DL1MAT002",
                        Dl1MeshDiagnosticSeverity.Warning,
                        $"Material slot '{slot.DatabaseName}' is malformed and remains unresolved: {exception.Message}",
                        slot.Index));
                    slots[slotIndex] = slot;
                    continue;
                }

                if (material is null)
                {
                    diagnostics.Add(new Dl1MeshDiagnostic(
                        "DL1MAT002",
                        Dl1MeshDiagnosticSeverity.Warning,
                        $"Material slot '{slot.DatabaseName}' is not present in the selected retail material pack.",
                        slot.Index));
                    slots[slotIndex] = slot;
                    continue;
                }

                List<Dl1MaterialTextureBinding> bindings =
                    new(material.Textures.Count);
                bool hasBaseColorPreview = false;
                foreach (Dl1MaterialPackTextureRecord texture in
                         material.Textures)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RetailAssetRecord? textureAsset =
                        ResolveTextureIdentity(
                            texture.TextureNameHash,
                            slot,
                            diagnostics);
                    Dl1MaterialTextureSemantic semantic =
                        textureAsset is null
                            ? Dl1MaterialTextureSemantic.Unknown
                            : Dl1MaterialTextureClassifier.Classify(
                                textureAsset.DisplayName,
                                material.ResourceName);
                    Dl1TexturePreviewData? preview = null;
                    if (!hasBaseColorPreview
                        && semantic ==
                        Dl1MaterialTextureSemantic.BaseColor
                        && textureAsset is not null)
                    {
                        bool wasCached =
                            previewCache.ContainsKey(textureAsset.Id);
                        preview = await TryDecodePreviewAsync(
                            textureAsset,
                            slot,
                            previewCache,
                            failedPreviews,
                            remainingPreviewBytes,
                            diagnostics,
                            cancellationToken).ConfigureAwait(false);
                        if (preview is not null)
                        {
                            if (!wasCached)
                            {
                                remainingPreviewBytes = checked(
                                    remainingPreviewBytes
                                    - preview.BaseMipBytes.Length);
                            }

                            hasBaseColorPreview = true;
                            previewCount++;
                        }
                    }

                    bindings.Add(new Dl1MaterialTextureBinding(
                        texture.SamplerState,
                        texture.TextureNameHash,
                        texture.LoadFlags,
                        textureAsset?.DisplayName,
                        textureAsset?.Id,
                        semantic,
                        preview));
                }

                Dl1ResolvedMaterial resolved = new(
                    material.ResourceName,
                    material.NameHash,
                    material.TechniqueCount,
                    bindings);
                slots[slotIndex] = slot with
                {
                    MaterialResourceName = material.ResourceName,
                    BindingStatus = Dl1MaterialBindingStatus.Resolved,
                    ResolvedMaterial = resolved,
                };
                resolvedMaterialCount++;
            }

            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MAT005",
                Dl1MeshDiagnosticSeverity.Information,
                $"Resolved {resolvedMaterialCount} of {slots.Length} material slots and decoded {previewCount} base-color previews from bounded retail data."));
            return mesh with
            {
                MaterialSlots = slots,
                Diagnostics = diagnostics,
            };
        }
    }

    private async Task<Dl1TexturePreviewData?> TryDecodePreviewAsync(
        RetailAssetRecord asset,
        Dl1MaterialSlot slot,
        Dictionary<RetailAssetId, Dl1TexturePreviewData> previewCache,
        HashSet<RetailAssetId> failedPreviews,
        long remainingPreviewBytes,
        List<Dl1MeshDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (previewCache.TryGetValue(
                asset.Id,
                out Dl1TexturePreviewData? cached))
        {
            return cached;
        }

        if (failedPreviews.Contains(asset.Id)
            || remainingPreviewBytes <= 0)
        {
            return null;
        }

        try
        {
            Rp6lArchive archive =
                await _rpackProvider.GetArchiveAsync(
                    asset.Source.ContainerPath,
                    cancellationToken).ConfigureAwait(false);
            ValidateTextureSnapshot(asset, archive);
            int resourceIndex = asset.Source.ResourceIndex
                ?? throw new InvalidDataException(
                    "The texture catalog row has no resource index.");
            if (resourceIndex < 0
                || resourceIndex >= archive.Resources.Count)
            {
                throw new InvalidDataException(
                    "The texture catalog row points outside its RP6L archive.");
            }

            Rp6lResourceDescriptor resource =
                archive.Resources[resourceIndex];
            int boundedBytes = checked((int)Math.Min(
                Math.Min(
                    remainingPreviewBytes,
                    _options.TextureLimits.MaximumBaseMipBytes),
                int.MaxValue));
            Dl1TexturePreviewData preview =
                await Dl1TexturePreviewDecoder.DecodeBaseMipAsync(
                    asset,
                    archive,
                    resource,
                    _chunkCache,
                    _options.TextureLimits with
                    {
                        MaximumBaseMipBytes = boundedBytes,
                    },
                    cancellationToken).ConfigureAwait(false);
            previewCache.Add(asset.Id, preview);
            return preview;
        }
        catch (Exception exception) when (
            IsLocalAssetFailure(exception))
        {
            failedPreviews.Add(asset.Id);
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MAT004",
                Dl1MeshDiagnosticSeverity.Warning,
                $"Texture '{asset.DisplayName}' for material '{slot.DatabaseName}' has no bounded BC preview: {exception.Message}",
                slot.Index));
            return null;
        }
    }

    private RetailAssetRecord? ResolveTextureIdentity(
        uint textureHash,
        Dl1MaterialSlot slot,
        List<Dl1MeshDiagnostic> diagnostics)
    {
        if (!_texturesByHash.TryGetValue(
                textureHash,
                out RetailAssetRecord[]? candidates))
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MAT003",
                Dl1MeshDiagnosticSeverity.Warning,
                $"Material '{slot.DatabaseName}' references unknown texture hash 0x{textureHash:X8}.",
                slot.Index));
            return null;
        }

        if (candidates.Length != 1)
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MAT003",
                Dl1MeshDiagnosticSeverity.Warning,
                $"Material '{slot.DatabaseName}' texture hash 0x{textureHash:X8} collides across {candidates.Length} catalog names and was not guessed.",
                slot.Index));
            return null;
        }

        return candidates[0];
    }

    private void ValidateTextureSnapshot(
        RetailAssetRecord asset,
        Rp6lArchive archive)
    {
        if (asset.Source.Kind != RetailAssetSourceKind.Rpack
            || !string.Equals(
                asset.Source.ProviderId,
                _rpackProvider.ProviderId,
                StringComparison.Ordinal)
            || asset.Source.SourceLength != archive.File.Length
            || asset.Source.SourceLastWriteTimeUtc !=
            archive.File.LastWriteTimeUtc
            || !string.Equals(
                asset.Id.SourceFingerprint,
                archive.CacheIdentity,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "The texture source changed after cataloging or belongs to another provider.");
        }
    }

    private static Dictionary<uint, RetailAssetRecord[]>
        BuildTextureHashIndex(RetailAssetCatalog catalog)
    {
        Dictionary<uint, List<RetailAssetRecord>> grouped = [];
        foreach (RetailAssetRecord asset in catalog.Assets)
        {
            if (asset.Id.Namespace != RetailAssetNamespace.RpackResource
                || asset.Id.ResourceType != Rp6lResourceTypes.Texture)
            {
                continue;
            }

            uint hash;
            try
            {
                hash = Dl1ResourceNameHash.ComputeTextureResource(
                    asset.DisplayName);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!grouped.TryGetValue(
                    hash,
                    out List<RetailAssetRecord>? rows))
            {
                rows = [];
                grouped.Add(hash, rows);
            }

            rows.Add(asset);
        }

        return grouped.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value
                .GroupBy(
                    static asset => asset.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(
                    static asset => asset.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static bool IsLocalAssetFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or OverflowException;
}
