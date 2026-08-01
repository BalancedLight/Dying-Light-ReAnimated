using System.IO;
using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// Deterministic CPU reference for the D3D11 mesh vertex shader. Morph targets
/// are accumulated in mesh-local space before the same normalized four-weight
/// position and inverse-transpose normal palettes used by the GPU pass. This is
/// intentionally a validation path, not a second authoring evaluator.
/// </summary>
public static class CpuMeshDeformationEvaluator
{
    public static CpuDeformedVertex[] Evaluate(
        MeshRenderData mesh,
        SkeletonRenderData? skeleton,
        IReadOnlyList<MorphWeight> morphWeights)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(morphWeights);
        if (!RenderMeshValidation.TryValidate(
                mesh,
                skeleton,
                out string? validationError))
        {
            throw new InvalidDataException(validationError);
        }

        ActiveMorphTarget[] activeMorphTargets =
            MorphTargetSelection.Select(mesh, morphWeights);
        Matrix4x4[]? skinPalette = mesh.IsSkinned
            ? GpuSkinningPalette.Build(mesh, skeleton!)
            : null;
        Matrix4x4[]? skinNormalPalette = skinPalette is null
            ? null
            : NormalTransformMatrix.CreatePalette(skinPalette);
        Matrix4x4 localToWorldNormal =
            NormalTransformMatrix.CreateOrZero(mesh.LocalToWorld);
        ReadOnlySpan<MeshVertex> sourceVertices = mesh.Vertices.Span;
        CpuDeformedVertex[] result =
            new CpuDeformedVertex[sourceVertices.Length];
        for (int vertexIndex = 0;
             vertexIndex < sourceVertices.Length;
             vertexIndex++)
        {
            MeshVertex source = sourceVertices[vertexIndex];
            result[vertexIndex] = EvaluateVertex(
                mesh,
                source,
                vertexIndex,
                activeMorphTargets,
                skinPalette,
                skinNormalPalette,
                localToWorldNormal);
        }

        return result;
    }

    /// <summary>
    /// Measures the current morphed/skinned mesh without allocating a second
    /// deformed-vertex array. Invalid or non-finite geometry is rejected so an
    /// editor bounds overlay cannot silently display a partial box.
    /// </summary>
    public static bool TryMeasureBounds(
        MeshRenderData mesh,
        SkeletonRenderData? skeleton,
        IReadOnlyList<MorphWeight> morphWeights,
        out CpuDeformedBounds bounds,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(morphWeights);
        if (!RenderMeshValidation.TryValidate(
                mesh,
                skeleton,
                out error))
        {
            bounds = default;
            return false;
        }

        ActiveMorphTarget[] activeMorphTargets =
            MorphTargetSelection.Select(mesh, morphWeights);
        Matrix4x4[]? skinPalette = mesh.IsSkinned
            ? GpuSkinningPalette.Build(mesh, skeleton!)
            : null;
        Matrix4x4[]? skinNormalPalette = skinPalette is null
            ? null
            : NormalTransformMatrix.CreatePalette(skinPalette);
        Matrix4x4 localToWorldNormal =
            NormalTransformMatrix.CreateOrZero(mesh.LocalToWorld);
        Vector3 minimum = new(
            float.PositiveInfinity,
            float.PositiveInfinity,
            float.PositiveInfinity);
        Vector3 maximum = new(
            float.NegativeInfinity,
            float.NegativeInfinity,
            float.NegativeInfinity);
        ReadOnlySpan<MeshVertex> sourceVertices = mesh.Vertices.Span;
        for (int vertexIndex = 0;
             vertexIndex < sourceVertices.Length;
             vertexIndex++)
        {
            Vector3 position = EvaluateVertex(
                mesh,
                sourceVertices[vertexIndex],
                vertexIndex,
                activeMorphTargets,
                skinPalette,
                skinNormalPalette,
                localToWorldNormal).Position;
            if (!IsFinite(position))
            {
                bounds = default;
                error =
                    $"Mesh '{mesh.Id}' produced a non-finite deformed position.";
                return false;
            }

            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        bounds = new CpuDeformedBounds(minimum, maximum);
        error = null;
        return true;
    }

    private static CpuDeformedVertex EvaluateVertex(
        MeshRenderData mesh,
        MeshVertex source,
        int vertexIndex,
        IReadOnlyList<ActiveMorphTarget> activeMorphTargets,
        Matrix4x4[]? skinPalette,
        Matrix4x4[]? skinNormalPalette,
        Matrix4x4 localToWorldNormal)
    {
        Vector3 localPosition = source.Position;
        Vector3 localNormal = source.Normal;
        foreach (ActiveMorphTarget active in activeMorphTargets)
        {
            localPosition +=
                active.Target.PositionDeltas.Span[vertexIndex]
                * active.Weight;
            if (!active.Target.NormalDeltas.IsEmpty)
            {
                localNormal +=
                    active.Target.NormalDeltas.Span[vertexIndex]
                    * active.Weight;
            }
        }

        (Vector3 worldPosition, Vector3 worldNormal) =
            skinPalette is null ||
            WeightSum(source.BoneWeights) <= 1.0e-6f
                ? TransformStatic(
                    localPosition,
                    localNormal,
                    mesh.LocalToWorld,
                    localToWorldNormal)
                : TransformSkinned(
                    localPosition,
                    localNormal,
                    source.BoneWeights,
                    source.BoneIndices,
                    skinPalette,
                    skinNormalPalette!);
        return new CpuDeformedVertex(
            worldPosition,
            NormalizeOrZero(worldNormal),
            source.TextureCoordinate);
    }

    private static (Vector3 Position, Vector3 Normal) TransformStatic(
        Vector3 position,
        Vector3 normal,
        Matrix4x4 localToWorld,
        Matrix4x4 localToWorldNormal) =>
        (
            Vector3.Transform(position, localToWorld),
            Vector3.TransformNormal(normal, localToWorldNormal)
        );

    private static (Vector3 Position, Vector3 Normal) TransformSkinned(
        Vector3 position,
        Vector3 normal,
        Vector4 weights,
        Vector4 indices,
        Matrix4x4[] skinPalette,
        Matrix4x4[] skinNormalPalette)
    {
        float weightSum = WeightSum(weights);
        Vector4 normalizedWeights = weights / weightSum;
        int indexX = ToPaletteIndex(indices.X, skinPalette.Length);
        int indexY = ToPaletteIndex(indices.Y, skinPalette.Length);
        int indexZ = ToPaletteIndex(indices.Z, skinPalette.Length);
        int indexW = ToPaletteIndex(indices.W, skinPalette.Length);

        Vector3 worldPosition =
            Vector3.Transform(position, skinPalette[indexX])
            * normalizedWeights.X
            + Vector3.Transform(position, skinPalette[indexY])
            * normalizedWeights.Y
            + Vector3.Transform(position, skinPalette[indexZ])
            * normalizedWeights.Z
            + Vector3.Transform(position, skinPalette[indexW])
            * normalizedWeights.W;
        Vector3 worldNormal =
            Vector3.TransformNormal(normal, skinNormalPalette[indexX])
            * normalizedWeights.X
            + Vector3.TransformNormal(normal, skinNormalPalette[indexY])
            * normalizedWeights.Y
            + Vector3.TransformNormal(normal, skinNormalPalette[indexZ])
            * normalizedWeights.Z
            + Vector3.TransformNormal(normal, skinNormalPalette[indexW])
            * normalizedWeights.W;
        return (worldPosition, worldNormal);
    }

    private static int ToPaletteIndex(float value, int paletteCount)
    {
        int rounded = checked((int)MathF.Round(value));
        return Math.Clamp(rounded, 0, paletteCount - 1);
    }

    private static float WeightSum(Vector4 weights) =>
        weights.X + weights.Y + weights.Z + weights.W;

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) &&
               lengthSquared > 1.0e-20f
            ? value / MathF.Sqrt(lengthSquared)
            : Vector3.Zero;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);
}

public readonly record struct CpuDeformedVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TextureCoordinate);

public readonly record struct CpuDeformedBounds(
    Vector3 Minimum,
    Vector3 Maximum)
{
    public Vector3 Center => (Minimum + Maximum) * 0.5f;

    public Vector3 Size => Maximum - Minimum;
}
