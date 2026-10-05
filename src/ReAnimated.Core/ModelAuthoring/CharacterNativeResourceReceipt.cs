using System.Collections.Immutable;
using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record CharacterNativeItemReceipt(int SourceItemIndex, int SourceChunkIndex,
    byte Flags, short StorageGroupId, int Unknown, ushort ChunkFlags, ushort ChunkCategory,
    ushort ChunkUnknown0, ushort ChunkUnknown1, long PayloadOffset, int ByteLength,
    string ContentSha256, string StoredChunkSha256);

public sealed record CharacterNativeResourceReceipt
{
    public int HeaderVersion { get; init; }
    public int HeaderUnknown { get; init; }
    public string ResourceName { get; init; } = string.Empty;
    public short ResourceType { get; init; }
    public int SourceResourceIndex { get; init; }
    public ImmutableArray<CharacterNativeItemReceipt> Items { get; init; } = [];

    public void Validate(CharacterResourceRecord resource)
    {
        if(HeaderVersion!=1 || ResourceType is not (8480 or 272) || SourceResourceIndex<0 ||
            string.IsNullOrWhiteSpace(ResourceName) || ResourceName.Length>4096 || ResourceName.Any(char.IsControl) ||
            ResourceName.Contains(':') || ResourceName.Contains('\\') || ResourceName.StartsWith('/') ||
            ResourceName.Split('/').Any(part=>part is "" or "." or "..") || resource.LogicalName!=ResourceName ||
            resource.EntryPath is null || resource.ByteLength>64L*1024*1024 || Items.IsDefaultOrEmpty || Items.Length>256 ||
            Items.Select(item=>item.SourceItemIndex).Distinct().Count()!=Items.Length)
            throw new ArgumentException("Native resource provenance is invalid.");
        long offset=0;
        foreach(var item in Items)
        {
            ProjectAssetReference.ValidateSha256(item.ContentSha256,nameof(Items));
            ProjectAssetReference.ValidateSha256(item.StoredChunkSha256,nameof(Items));
            if(item.SourceItemIndex<0 || item.SourceChunkIndex<0 || item.ByteLength<0 || item.PayloadOffset!=offset)
                throw new ArgumentException("Native resource item extent is invalid.");
            offset=checked(offset+item.ByteLength);
        }
        if(offset!=resource.ByteLength) throw new ArgumentException("Native resource payload length changed.");
    }
}
