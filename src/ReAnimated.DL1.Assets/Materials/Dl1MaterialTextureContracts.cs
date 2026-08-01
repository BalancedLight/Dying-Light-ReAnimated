using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Materials;

public enum Dl1MaterialTextureSemantic
{
    Unknown,
    BaseColor,
    Normal,
    Specular,
    Mask,
    Gradient,
}

public enum Dl1PreviewTextureFormat
{
    Bc1Unorm,
    Bc2Unorm,
    Bc3Unorm,
}

public sealed record Dl1TexturePreviewData(
    RetailAssetId AssetId,
    string ResourceName,
    int Width,
    int Height,
    int MipCount,
    Dl1PreviewTextureFormat Format,
    int RowPitch,
    ReadOnlyMemory<byte> BaseMipBytes);

public sealed record Dl1MaterialTextureBinding(
    uint SamplerState,
    uint TextureNameHash,
    uint LoadFlags,
    string? ResourceName,
    RetailAssetId? AssetId,
    Dl1MaterialTextureSemantic Semantic,
    Dl1TexturePreviewData? Preview);

public sealed record Dl1ResolvedMaterial(
    string ResourceName,
    uint NameHash,
    ushort TechniqueCount,
    IReadOnlyList<Dl1MaterialTextureBinding> TextureBindings)
{
    public Dl1TexturePreviewData? BaseColorPreview =>
        TextureBindings
            .FirstOrDefault(static binding =>
                binding.Semantic == Dl1MaterialTextureSemantic.BaseColor
                && binding.Preview is not null)
            ?.Preview;
}
