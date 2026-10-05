using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record CharacterTextureNameReceipt
{
    public string Name { get; init; } = string.Empty;
    public int TableIndex { get; init; }
    public long PayloadOffset { get; init; }
    public int ByteLength { get; init; }
    public string PayloadSha256 { get; init; } = string.Empty;

    public void Validate(uint nameHash, CharacterResourceRecord provider)
    {
        ProjectAssetReference.ValidateSha256(PayloadSha256, nameof(PayloadSha256));
        if (Name.Length is < 1 or > 4096 || Name != Name.Trim() || Name.Any(c => c > 127 || char.IsControl(c)) ||
            Name.Contains(':') || Name.Contains('\\') || Name.StartsWith('/') ||
            Name.Split('/').Any(part => part is "" or "." or "..") ||
            !Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || ComputeHash(Name) != nameHash ||
            TableIndex < 0 || PayloadOffset < 16 || (ByteLength != Name.Length + 1 && ByteLength != ((Name.Length + 4) & ~3)) ||
            PayloadOffset + ByteLength > provider.ByteLength)
            throw new ArgumentException("Texture string provenance is invalid.");
    }

    internal static uint ComputeHash(string name)
    {
        uint crc = 0x811C9DC5U ^ uint.MaxValue;
        foreach (char letter in name.ToLowerInvariant().Split('/')[^1])
        {
            crc ^= (byte)letter;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320U & unchecked((uint)-(int)(crc & 1)));
        }
        return crc ^ uint.MaxValue;
    }
}

