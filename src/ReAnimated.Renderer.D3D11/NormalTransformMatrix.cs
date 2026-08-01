using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// Builds row-vector normal transforms from position transforms. Normals are
/// covectors, so non-uniform scale requires the inverse transpose of the
/// position matrix instead of the position matrix itself.
/// </summary>
public static class NormalTransformMatrix
{
    public static Matrix4x4 CreateOrZero(
        Matrix4x4 positionTransform)
    {
        if (!IsFinite(positionTransform) ||
            !Matrix4x4.Invert(
                positionTransform,
                out Matrix4x4 inverse) ||
            !IsFinite(inverse))
        {
            // A singular transform has no mathematically defined normal
            // transform. Returning zero keeps the preview deterministic and
            // prevents an invalid normal from contaminating the entire draw.
            return default;
        }

        Matrix4x4 inverseTranspose =
            Matrix4x4.Transpose(inverse);
        return new Matrix4x4(
            inverseTranspose.M11,
            inverseTranspose.M12,
            inverseTranspose.M13,
            0.0f,
            inverseTranspose.M21,
            inverseTranspose.M22,
            inverseTranspose.M23,
            0.0f,
            inverseTranspose.M31,
            inverseTranspose.M32,
            inverseTranspose.M33,
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            1.0f);
    }

    public static Matrix4x4[] CreatePalette(
        ReadOnlySpan<Matrix4x4> positionTransforms)
    {
        Matrix4x4[] normalTransforms =
            new Matrix4x4[positionTransforms.Length];
        for (int index = 0;
             index < positionTransforms.Length;
             index++)
        {
            normalTransforms[index] =
                CreateOrZero(positionTransforms[index]);
        }

        return normalTransforms;
    }

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) &&
        float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) &&
        float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) &&
        float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) &&
        float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) &&
        float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) &&
        float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) &&
        float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) &&
        float.IsFinite(value.M44);
}
