namespace ReAnimated.DL1.Assets.Materials;

/// <summary>
/// Classifies DL1 texture-resource names without inventing shader semantics.
/// Retail variants commonly append a qualifier after the ordinary
/// <c>_dif</c>, <c>_nrm</c>, <c>_spc</c>, <c>_msk</c>, or <c>_grd</c>
/// token, so both a terminal token and an underscore-delimited token are
/// accepted.
/// </summary>
public static class Dl1MaterialTextureClassifier
{
    public static Dl1MaterialTextureSemantic Classify(
        string resourceName,
        string materialResourceName)
    {
        string normalized =
            Dl1ResourceNameHash.NormalizeFileName(resourceName);
        string materialName =
            Dl1ResourceNameHash.NormalizeFileName(
                materialResourceName);
        string materialStem = materialName.EndsWith(
            ".mat",
            StringComparison.Ordinal)
            ? materialName[..^4]
            : materialName;
        return normalized switch
        {
            _ when HasToken(normalized, "clr")
                || HasToken(normalized, "dif")
                || HasToken(normalized, "diff")
                || normalized.Equals(
                    materialStem,
                    StringComparison.Ordinal) =>
                Dl1MaterialTextureSemantic.BaseColor,
            _ when HasToken(normalized, "nrm") =>
                Dl1MaterialTextureSemantic.Normal,
            _ when HasToken(normalized, "spc") =>
                Dl1MaterialTextureSemantic.Specular,
            _ when HasToken(normalized, "msk") =>
                Dl1MaterialTextureSemantic.Mask,
            _ when HasToken(normalized, "grd") =>
                Dl1MaterialTextureSemantic.Gradient,
            _ => Dl1MaterialTextureSemantic.Unknown,
        };
    }

    private static bool HasToken(
        string resourceName,
        string token)
    {
        string marker = string.Concat("_", token);
        return resourceName.EndsWith(
                marker,
                StringComparison.Ordinal)
            || resourceName.Contains(
                string.Concat(marker, "_"),
                StringComparison.Ordinal);
    }
}
