using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record CharacterSupplementalSourceReceipt
{
    public const long MaximumArchiveBytes = 16L * 1024 * 1024 * 1024;
    public const int MaximumMemberBytes = 64 * 1024 * 1024;
    public string ArchiveSha256 { get; init; } = string.Empty;
    public long ArchiveByteLength { get; init; }
    public string MemberName { get; init; } = string.Empty;
    public string VirtualName { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public bool Reviewed { get; init; }

    public void Validate(CharacterResourceRecord resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ProjectAssetReference.ValidateSha256(ArchiveSha256, nameof(ArchiveSha256));
        ProjectAssetReference.ValidateSha256(ContentSha256, nameof(ContentSha256));
        ValidatePortablePath(MemberName);
        ValidatePortablePath(VirtualName);
        if (!string.Equals(Path.GetExtension(MemberName), Path.GetExtension(VirtualName), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Supplemental source extensions do not match.");
        if (!Reviewed || ArchiveByteLength <= 0 || ArchiveByteLength > MaximumArchiveBytes ||
            ByteLength <= 0 || ByteLength > MaximumMemberBytes ||
            resource.LogicalName != VirtualName || resource.ByteLength != ByteLength ||
            !string.Equals(resource.ContentSha256, ContentSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resource.SourceFingerprint, ArchiveSha256, StringComparison.OrdinalIgnoreCase) ||
            resource.ProviderIdentity != "zip-sha256:" + ArchiveSha256.ToLowerInvariant() ||
            resource.EntryPath is null)
            throw new ArgumentException("Supplemental source provenance does not match its resource.");
    }

    public static void ValidatePortablePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 4096 || value != value.Trim() || value.Any(char.IsControl) ||
            value.StartsWith('/') || value.Contains('\\') || value.Contains(':') ||
            value.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." ||
                segment != segment.Trim()))
            throw new ArgumentException("Use a canonical relative path with forward slashes.");
    }
}