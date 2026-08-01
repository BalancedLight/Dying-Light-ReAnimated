using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.DL1.Assets.Catalog;

public enum RetailAssetNamespace
{
    VirtualFile = 0,
    RpackResource = 1,
}

public enum RetailAssetSourceKind
{
    Rpack = 0,
    ZipPak = 1,
    LooseFile = 2,
    GeneratedOverride = 3,
}

/// <summary>
/// The logical lookup key used to resolve precedence conflicts.
/// </summary>
public readonly record struct RetailAssetLogicalId
{
    private RetailAssetLogicalId(
        RetailAssetNamespace assetNamespace,
        int resourceType,
        string normalizedName)
    {
        Namespace = assetNamespace;
        ResourceType = resourceType;
        Name = normalizedName;
    }

    public RetailAssetNamespace Namespace { get; }

    public int ResourceType { get; }

    public string Name { get; }

    public string StableKey => Namespace == RetailAssetNamespace.RpackResource
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"rpack:{ResourceType}:{Name}")
        : string.Concat("file:", Name);

    public static RetailAssetLogicalId Rpack(
        short resourceType,
        string name) =>
        new(
            RetailAssetNamespace.RpackResource,
            resourceType,
            NormalizeName(name, isPath: false));

    public static RetailAssetLogicalId VirtualFile(string virtualPath) =>
        new(
            RetailAssetNamespace.VirtualFile,
            0,
            NormalizeName(virtualPath, isPath: true));

    public override string ToString() => StableKey;

    private static string NormalizeName(string value, bool isPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value
            .Trim()
            .Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace(
                "//",
                "/",
                StringComparison.Ordinal);
        }

        normalized = normalized.TrimStart('/');
        if (normalized.Length == 0 ||
            normalized.Contains('\0') ||
            (isPath &&
             (Path.IsPathRooted(normalized) ||
              normalized.Split('/').Any(static part =>
                  part is "." or ".."))))
        {
            throw new InvalidDataException(
                $"Retail asset name '{value}' is unsafe.");
        }

        return normalized.ToLowerInvariant();
    }
}

/// <summary>
/// A physical retail asset identity. It deliberately includes install,
/// provider, stable source row, precedence, and source/content fingerprints;
/// name alone is never treated as physical identity.
/// </summary>
public readonly record struct RetailAssetId
{
    private RetailAssetId(
        RetailAssetLogicalId logicalId,
        string installId,
        string providerId,
        long sourceIndex,
        int precedence,
        string sourceFingerprint,
        string? contentFingerprint)
    {
        LogicalId = logicalId;
        InstallId = installId;
        ProviderId = providerId;
        SourceIndex = sourceIndex;
        Precedence = precedence;
        SourceFingerprint = sourceFingerprint;
        ContentFingerprint = contentFingerprint;
    }

    public RetailAssetLogicalId LogicalId { get; }

    public string InstallId { get; }

    public string ProviderId { get; }

    public long SourceIndex { get; }

    public int Precedence { get; }

    public string SourceFingerprint { get; }

    /// <summary>
    /// SHA-256 of decoded asset content when a provider can obtain it without
    /// defeating bounded catalog enumeration; otherwise null.
    /// </summary>
    public string? ContentFingerprint { get; }

    public RetailAssetNamespace Namespace => LogicalId.Namespace;

    public int ResourceType => LogicalId.ResourceType;

    public string Name => LogicalId.Name;

    public string StableKey => string.Create(
        CultureInfo.InvariantCulture,
        $"{LogicalId.StableKey}|install:{InstallId}|provider:{ProviderId}|source:{SourceIndex}|precedence:{Precedence}|snapshot:{SourceFingerprint}|content:{ContentFingerprint ?? "pending"}");

    public static RetailAssetId Create(
        RetailAssetLogicalId logicalId,
        string installId,
        string providerId,
        long sourceIndex,
        int precedence,
        string sourceFingerprint,
        string? contentFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);
        if (logicalId.Name is null)
        {
            throw new ArgumentException(
                "A logical asset ID is required.",
                nameof(logicalId));
        }

        return new RetailAssetId(
            logicalId,
            installId.Trim(),
            providerId.Trim(),
            sourceIndex,
            precedence,
            sourceFingerprint.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(contentFingerprint)
                ? null
                : contentFingerprint.Trim().ToLowerInvariant());
    }

    public override string ToString() => StableKey;
}

public static class RetailAssetIdentity
{
    public static string CreateInstallId(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        return HashText(
            Path.GetFullPath(installPath)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                .ToUpperInvariant());
    }

    public static string CreateSourceFingerprint(params object?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        StringBuilder value = new();
        foreach (object? part in parts)
        {
            value.Append(part switch
            {
                DateTime dateTime =>
                    dateTime.ToUniversalTime().Ticks.ToString(
                        CultureInfo.InvariantCulture),
                IFormattable formattable =>
                    formattable.ToString(
                        null,
                        CultureInfo.InvariantCulture),
                _ => part?.ToString() ?? "<null>",
            })
                .Append('\0');
        }

        return HashText(value.ToString());
    }

    private static string HashText(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

public sealed record RetailAssetSource(
    string ProviderId,
    RetailAssetSourceKind Kind,
    int Priority,
    string ContainerPath,
    string EntryPath,
    int? ResourceIndex,
    long Length,
    long SourceLength,
    DateTime SourceLastWriteTimeUtc);

public sealed record RetailAssetRecord(
    RetailAssetId Id,
    string DisplayName,
    RetailAssetSource Source);

internal static class RetailAssetRecordValidator
{
    public static void Validate(
        string providerId,
        RetailAssetRecord asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(asset);
        if (!string.Equals(
                providerId,
                asset.Source.ProviderId,
                StringComparison.Ordinal) ||
            !string.Equals(
                asset.Id.ProviderId,
                asset.Source.ProviderId,
                StringComparison.Ordinal) ||
            asset.Id.Precedence != asset.Source.Priority ||
            asset.Source.ResourceIndex is { } resourceIndex &&
            asset.Id.SourceIndex != resourceIndex ||
            asset.Source.Length < 0 ||
            asset.Source.SourceLength < 0 ||
            string.IsNullOrWhiteSpace(asset.DisplayName) ||
            string.IsNullOrWhiteSpace(asset.Source.ContainerPath) ||
            string.IsNullOrWhiteSpace(asset.Source.EntryPath))
        {
            throw new InvalidDataException(
                $"Provider '{providerId}' returned an invalid asset record.");
        }
    }
}

public interface IRetailAssetProvider
{
    string ProviderId { get; }

    IAsyncEnumerable<RetailAssetRecord> EnumerateAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenReadAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default);
}

public interface IRetailAssetCatalog
{
    IReadOnlyList<RetailAssetRecord> Assets { get; }

    IReadOnlyList<RetailAssetConflict> Conflicts { get; }

    RetailAssetRecord? Resolve(RetailAssetLogicalId id);

    IReadOnlyList<RetailAssetRecord> GetCandidates(RetailAssetLogicalId id);

    IReadOnlyList<RetailAssetRecord> Search(
        string text,
        int maximumResults = 500);

    ValueTask<Stream> OpenReadAsync(
        RetailAssetLogicalId id,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenReadAsync(
        RetailAssetId id,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenReadAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default);
}

public sealed record RetailAssetConflict(
    RetailAssetLogicalId Id,
    RetailAssetRecord Winner,
    IReadOnlyList<RetailAssetRecord> Shadowed);
