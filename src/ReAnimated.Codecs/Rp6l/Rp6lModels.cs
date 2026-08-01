namespace ReAnimated.Codecs.Rp6l;

public enum Rp6lCompression
{
    None = 0,
    Zlib = 1,
    Lzma = 2,
    Unknown = 255,
}

public sealed record Rp6lLimits
{
    public static Rp6lLimits Default { get; } = new();

    public int MaximumTableCount { get; init; } = 2_000_000;

    public int MaximumNameBlobBytes { get; init; } = 64 * 1024 * 1024;

    public int MaximumTableBytes { get; init; } = 256 * 1024 * 1024;

    public long MaximumLogicalChunkBytes { get; init; } = uint.MaxValue;

    public long MaximumStoredChunkBytes { get; init; } = uint.MaxValue;

    public int MaximumItemBytes { get; init; } = 256 * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumTableCount <= 0 ||
            MaximumNameBlobBytes <= 0 ||
            MaximumTableBytes <= 0 ||
            MaximumLogicalChunkBytes <= 0 ||
            MaximumStoredChunkBytes <= 0 ||
            MaximumItemBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Rp6lLimits),
                "All RP6L limits must be positive.");
        }
    }
}

public sealed record Rp6lHeader(
    int Version,
    int CompressionFlags,
    int ItemCount,
    int ChunkCount,
    int ResourceCount,
    int NameBlobSize,
    int NameCount,
    int Unknown);

public sealed record Rp6lChunkDescriptor(
    int Index,
    ushort Flags,
    ushort Category,
    long Offset,
    long LogicalSize,
    int PackedSize,
    ushort Unknown0,
    ushort Unknown1,
    Rp6lCompression Compression)
{
    public long StoredSize => PackedSize > 0 ? PackedSize : LogicalSize;

    public bool IsCompressed => PackedSize > 0;

    /// <summary>
    /// Some retail tail chunks serialize item offsets as archive-relative
    /// values. This bias is subtracted while parsing so public item offsets
    /// remain relative to the logical chunk stream.
    /// </summary>
    public long ItemOffsetBias { get; init; }
}

public sealed record Rp6lItemDescriptor(
    int Index,
    int ChunkIndex,
    byte Flags,
    short StorageGroupId,
    long Offset,
    int SizeOrHash,
    int Unknown)
{
    public bool HasReadableSize => SizeOrHash >= 0;

    /// <summary>
    /// Compatibility alias for the formerly inferred field name. Retail DL1
    /// mesh item roles are positional; this value is not a payload type.
    /// </summary>
    public short LogicalType => StorageGroupId;
}

public sealed record Rp6lResourceDescriptor(
    int Index,
    string Name,
    short ResourceType,
    int NameIndex,
    int FirstItemIndex,
    int ItemCount,
    IReadOnlyList<Rp6lItemDescriptor> Items)
{
    public byte PayloadType => unchecked((byte)ResourceType);
}

public static class Rp6lResourceTypes
{
    public const short BuilderInformation = -32257;
    public const short Mesh = 272;
    public const short Skin = 274;
    public const short Animation = 320;
    public const short AnimationScript = 322;
    public const short Texture = 8480;
}
