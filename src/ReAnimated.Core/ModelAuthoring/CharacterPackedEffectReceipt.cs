using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record CharacterPackedEffectReceipt
{
    public string BundleResourceId { get; init; } = string.Empty;
    public string BundleSha256 { get; init; } = string.Empty;
    public string StoredName { get; init; } = string.Empty;
    public sbyte Kind { get; init; }
    public int ResourceIndex { get; init; }
    public int ItemIndex { get; init; }
    public int ChunkIndex { get; init; }
    public int EntryOffset { get; init; }
    public int EntryByteLength { get; init; }
    public int TextOffset { get; init; }
    public int TextByteLength { get; init; }
    public void Validate(CharacterResourceRecord resource,IReadOnlyCollection<CharacterResourceRecord> resources)
    {
        ProjectAssetReference.ValidateSha256(BundleSha256,nameof(BundleSha256));
        if(string.IsNullOrWhiteSpace(BundleResourceId) || string.IsNullOrWhiteSpace(StoredName) || StoredName.Length>4096 || StoredName.Any(char.IsControl) || StoredName!=StoredName.Trim() ||
            StoredName.StartsWith('/') || StoredName.Contains('\\') || StoredName.Contains(':') ||
            StoredName.Split('/').Any(part=>part is "" or "." or "..") ||
            ResourceIndex<0 || ItemIndex<0 || ChunkIndex<0 || EntryOffset<0 || EntryByteLength<3 ||
            TextOffset<=EntryOffset || TextByteLength<0 || TextByteLength>4*1024*1024 ||
            EntryOffset+(long)EntryByteLength>64L*1024*1024 || TextOffset+((long)TextByteLength)+1 != EntryOffset+((long)EntryByteLength) ||
            resource.ByteLength!=TextByteLength || resource.EntryPath is null || resource.ContentSha256 is null ||
            resource.LogicalName!=StoredName+".fx")
            throw new ArgumentException("Packed effect provenance is invalid.");
        var bundle=resources.SingleOrDefault(candidate=>candidate.Id==BundleResourceId);
        if(bundle is null || bundle.EntryPath is null || bundle.ByteLength>64L*1024*1024 || bundle.ContentSha256!=BundleSha256 ||
            EntryOffset+((long)EntryByteLength)>=bundle.ByteLength)
            throw new ArgumentException("The original effect bundle is missing or changed.");
    }
}