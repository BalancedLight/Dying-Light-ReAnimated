using ReAnimated.Core.Project;
namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Explicit editable geometry revision, retaining the immutable source and original companion custody.</summary>
public sealed record ModelGeometryRevisionReference
{
    public const string PayloadEntryPath = "authoring/geometry.json";
    public string SourceSha256 { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
    public long PayloadLength { get; init; }
    public void Validate()
    {
        ProjectAssetReference.ValidateSha256(SourceSha256, nameof(SourceSha256));
        ProjectAssetReference.ValidateSha256(ContentSha256, nameof(ContentSha256));
        if (PayloadLength <= 0 || PayloadLength > 768L * 1024 * 1024) throw new ArgumentException("Geometry revision is missing or oversized.");
    }
}
