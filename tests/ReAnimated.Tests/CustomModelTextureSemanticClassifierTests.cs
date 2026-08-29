using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CustomModelTextureSemanticClassifierTests
{
    [Theory]
    [InlineData("DiffuseColor", "body_alpha", CustomModelTextureSemantic.BaseColor, null)]
    [InlineData("TransparentColor", "base_color", CustomModelTextureSemantic.Mask, null)]
    [InlineData("", "skin_metallicity", CustomModelTextureSemantic.BaseColor, null)]
    [InlineData("", "tok_bodyNormal", CustomModelTextureSemantic.Normal, "normal")]
    public void ConnectionPropertyIsAuthoritativeAndFallbackUsesWholeTokens(
        string propertyName,
        string textureName,
        CustomModelTextureSemantic expected,
        string? expectedToken)
    {
        CustomModelTextureSemantic actual =
            FbxModelAuthoringImporter.ClassifyTextureSemantic(
                propertyName,
                textureName,
                out string? inferredToken);

        Assert.Equal(expected, actual);
        Assert.Equal(expectedToken, inferredToken);
    }
}
