using System.Collections.Immutable;
using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record CharacterMaterialTextureReference(int Index, uint SamplerState,
    uint TextureNameHash, uint LoadFlags, string? ResourceId)
{
    public CharacterTextureNameReceipt? NameSource { get; init; }
}

public sealed record CharacterMaterialReceipt
{
    public string ProviderResourceId { get; init; } = string.Empty;
    public string ProviderSha256 { get; init; } = string.Empty;
    public string MaterialName { get; init; } = string.Empty;
    public uint NameHash { get; init; }
    public int TableIndex { get; init; }
    public long PayloadOffset { get; init; }
    public int StoredByteLength { get; init; }
    public ushort TechniqueCount { get; init; }
    public ImmutableArray<CharacterMaterialTextureReference> Textures { get; init; } = [];

    public void Validate(CharacterResourceRecord resource, IReadOnlyCollection<CharacterResourceRecord> resources)
    {
        ProjectAssetReference.ValidateSha256(ProviderSha256, nameof(ProviderSha256));
        if(string.IsNullOrWhiteSpace(ProviderResourceId) || string.IsNullOrWhiteSpace(MaterialName) ||
            MaterialName.Length>4096 || MaterialName.Any(char.IsControl) || MaterialName.Contains(':') ||
            MaterialName.Contains('\\') || MaterialName.StartsWith('/') ||
            MaterialName.Split('/').Any(part=>part is "" or "." or "..") ||
            resource.LogicalName!=MaterialName || MaterialName!=MaterialName.Trim() ||
            MaterialName.Split('/')[^1].Any(value=>value>127) || NameHash!=ComputeNameHash(MaterialName.Split('/')[^1]) ||
            resource.EntryPath is null || resource.ByteLength<24 ||
            resource.ByteLength>1024*1024 || TableIndex<0 || PayloadOffset<16 ||
            StoredByteLength<resource.ByteLength || StoredByteLength>1024*1024 ||
            Textures.IsDefault || Textures.Length>256 ||
            !Textures.Select(value=>value.Index).SequenceEqual(Enumerable.Range(0,Textures.Length)))
            throw new ArgumentException("Material provenance is invalid.");
        var provider=resources.SingleOrDefault(value=>value.Id==ProviderResourceId);
        if(provider is null || provider.EntryPath is null || provider.ContentSha256!=ProviderSha256 ||
            provider.ByteLength>256L*1024*1024 || PayloadOffset+StoredByteLength>provider.ByteLength)
            throw new ArgumentException("The material provider is missing or changed.");
        foreach (var texture in Textures) texture.NameSource?.Validate(texture.TextureNameHash, provider);
        foreach(var texture in Textures.Where(value=>value.ResourceId is not null))
        {
            var dependency=resources.SingleOrDefault(value=>value.Id==texture.ResourceId);
            if(dependency is null || dependency.Subsystem!=CharacterSubsystem.Textures)
                throw new ArgumentException("A material texture reference is missing.");
        }
    }
    private static uint ComputeNameHash(string name)
    {
        uint crc=0x811C9DC5U^uint.MaxValue;
        foreach(char letter in name.ToLowerInvariant())
        {
            crc^=(byte)letter;
            for(int bit=0;bit<8;bit++)crc=(crc>>1)^(0xEDB88320U & unchecked((uint)-(int)(crc&1)));
        }
        return crc^uint.MaxValue;
    }

}

