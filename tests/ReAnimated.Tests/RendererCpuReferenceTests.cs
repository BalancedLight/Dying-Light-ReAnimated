using System.Numerics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererCpuReferenceTests
{
    [Fact]
    public void MorphsMeshLocalPositionBeforeNormalizedFourWeightSkinning()
    {
        MeshRenderData mesh = CreateMesh(
            new MeshVertex(
                new Vector3(1.0f, 0.0f, 0.0f),
                Vector3.UnitY,
                new Vector2(0.25f, 0.75f),
                new Vector4(1.0f, 3.0f, 0.0f, 0.0f),
                new Vector4(0.0f, 1.0f, 0.0f, 0.0f)),
            new Matrix4x4[]
            {
                Matrix4x4.Identity,
                Matrix4x4.Identity,
            }) with
        {
            MorphTargets =
            [
                new MorphTargetRenderData(
                    "jaw",
                    new Vector3[]
                    {
                        new Vector3(0.0f, 2.0f, 0.0f),
                        Vector3.Zero,
                        Vector3.Zero,
                    },
                    new Vector3[]
                    {
                        new Vector3(1.0f, 0.0f, 0.0f),
                        Vector3.Zero,
                        Vector3.Zero,
                    }),
            ],
        };
        SkeletonRenderData skeleton = CreateSkeleton(
            Matrix4x4.CreateTranslation(4.0f, 0.0f, 0.0f),
            Matrix4x4.CreateTranslation(0.0f, 8.0f, 0.0f));

        CpuDeformedVertex[] result =
            CpuMeshDeformationEvaluator.Evaluate(
                mesh,
                skeleton,
                [new MorphWeight("JAW", 0.5f)]);

        AssertVector(
            new Vector3(2.0f, 7.0f, 3.0f),
            result[0].Position);
        AssertVector(
            Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.0f)),
            result[0].Normal);
        Assert.Equal(
            new Vector2(0.25f, 0.75f),
            result[0].TextureCoordinate);
    }

    [Fact]
    public void StaticAndUnweightedSkinnedVerticesUseLocalToWorld()
    {
        Matrix4x4 rotation =
            Matrix4x4.CreateRotationZ(0.35f);
        Matrix4x4 localToWorld =
            Matrix4x4.CreateScale(2.0f, 3.0f, 4.0f)
            * rotation
            * Matrix4x4.CreateTranslation(5.0f, 6.0f, 7.0f);
        MeshVertex source = new(
            new Vector3(1.0f, 2.0f, 3.0f),
            Vector3.Normalize(new Vector3(1.0f, 2.0f, 3.0f)),
            Vector2.Zero,
            Vector4.Zero,
            new Vector4(99.0f));
        MeshRenderData staticMesh = CreateMesh(
            source,
            ReadOnlyMemory<Matrix4x4>.Empty,
            isSkinned: false) with
        {
            LocalToWorld = localToWorld,
        };
        MeshRenderData skinnedMesh = CreateMesh(
            source,
            new Matrix4x4[] { Matrix4x4.Identity }) with
        {
            LocalToWorld = localToWorld,
        };
        SkeletonRenderData skeleton =
            CreateSkeleton(Matrix4x4.CreateTranslation(100.0f, 0.0f, 0.0f));

        CpuDeformedVertex staticResult =
            CpuMeshDeformationEvaluator.Evaluate(
                staticMesh,
                null,
                [])[0];
        CpuDeformedVertex unweightedResult =
            CpuMeshDeformationEvaluator.Evaluate(
                skinnedMesh,
                skeleton,
                [])[0];

        Vector3 expectedPosition =
            Vector3.Transform(source.Position, localToWorld);
        Vector3 expectedNormal = Vector3.Normalize(
            Vector3.TransformNormal(
                new Vector3(
                    source.Normal.X / 2.0f,
                    source.Normal.Y / 3.0f,
                    source.Normal.Z / 4.0f),
                rotation));
        AssertVector(expectedPosition, staticResult.Position);
        AssertVector(expectedPosition, unweightedResult.Position);
        AssertVector(expectedNormal, staticResult.Normal);
        AssertVector(expectedNormal, unweightedResult.Normal);
    }

    [Fact]
    public void SingularScaleProducesDeterministicZeroNormal()
    {
        MeshRenderData mesh = CreateMesh(
            new MeshVertex(
                Vector3.Zero,
                Vector3.Normalize(new Vector3(1.0f, 1.0f, 1.0f)),
                Vector2.Zero,
                Vector4.Zero,
                Vector4.Zero),
            ReadOnlyMemory<Matrix4x4>.Empty,
            isSkinned: false) with
        {
            LocalToWorld =
                Matrix4x4.CreateScale(1.0f, 0.0f, 2.0f),
        };

        CpuDeformedVertex result =
            CpuMeshDeformationEvaluator.Evaluate(
                mesh,
                null,
                [])[0];

        Assert.Equal(Vector3.Zero, result.Normal);
    }

    [Fact]
    public void CompactPaletteUsesInverseTransposeForNonUniformBoneScale()
    {
        Vector3 sourceNormal =
            Vector3.Normalize(new Vector3(1.0f, 1.0f, 1.0f));
        Matrix4x4 rotation =
            Matrix4x4.CreateRotationZ(0.35f);
        MeshRenderData mesh = CreateMesh(
            new MeshVertex(
                new Vector3(0.5f, 0.25f, 0.75f),
                sourceNormal,
                Vector2.Zero,
                Vector4.UnitX,
                Vector4.Zero),
            new Matrix4x4[] { Matrix4x4.Identity }) with
        {
            LocalToWorld = Matrix4x4.Identity,
            SkinBoneIndices = new int[] { 299 },
        };
        BoneRenderData[] bones = Enumerable.Range(0, 300)
            .Select(index => new BoneRenderData(
                $"bone_{index}",
                -1,
                Matrix4x4.Identity,
                index == 299
                    ? Matrix4x4.CreateScale(1.6f, 0.4f, 0.8f)
                      * rotation
                    : Matrix4x4.Identity,
                false))
            .ToArray();
        var skeleton = new SkeletonRenderData(
            bones,
            Matrix4x4.Identity);

        CpuDeformedVertex result =
            CpuMeshDeformationEvaluator.Evaluate(
                mesh,
                skeleton,
                [])[0];

        AssertVector(
            Vector3.Transform(
                new Vector3(0.8f, 0.1f, 0.6f),
                rotation),
            result.Position);
        AssertVector(
            Vector3.Normalize(
                Vector3.TransformNormal(
                    new Vector3(
                        sourceNormal.X / 1.6f,
                        sourceNormal.Y / 0.4f,
                        sourceNormal.Z / 0.8f),
                    rotation)),
            result.Normal);
        Assert.True(
            Vector3.Distance(
                result.Normal,
                Vector3.Normalize(
                    Vector3.TransformNormal(
                        new Vector3(
                            sourceNormal.X * 1.6f,
                            sourceNormal.Y * 0.4f,
                            sourceNormal.Z * 0.8f),
                        rotation))) >
            0.5f);
    }

    [Fact]
    public void PerDrawPaletteUsesMappedBoneFromLargeSkeleton()
    {
        MeshRenderData mesh = CreateMesh(
            new MeshVertex(
                new Vector3(1, 2, 3),
                Vector3.UnitY,
                Vector2.Zero,
                Vector4.UnitX,
                Vector4.Zero),
            new Matrix4x4[]
            {
                Matrix4x4.Identity,
            }) with
        {
            SkinBoneIndices = new int[] { 299 },
        };
        BoneRenderData[] bones = Enumerable.Range(0, 300)
            .Select(index => new BoneRenderData(
                $"bone_{index}",
                -1,
                Matrix4x4.Identity,
                index == 299
                    ? Matrix4x4.CreateTranslation(4, 5, 6)
                    : Matrix4x4.Identity,
                false))
            .ToArray();
        var skeleton = new SkeletonRenderData(
            bones,
            Matrix4x4.Identity);

        CpuDeformedVertex result =
            CpuMeshDeformationEvaluator.Evaluate(
                mesh,
                skeleton,
                [])[0];

        AssertVector(
            new Vector3(5, 7, 12),
            result.Position);
    }

    [Fact]
    public void UsesTheSameActiveTargetCapAndUnclampedWeightsAsGpuPass()
    {
        MeshRenderData mesh = CreateMesh(
            new MeshVertex(
                Vector3.Zero,
                Vector3.UnitY,
                Vector2.Zero,
                Vector4.Zero,
                Vector4.Zero),
            ReadOnlyMemory<Matrix4x4>.Empty,
            isSkinned: false) with
        {
            MorphTargets = Enumerable.Range(0, 65)
                .Select(index => new MorphTargetRenderData(
                    $"morph_{index}",
                    new Vector3[]
                    {
                        Vector3.UnitX,
                        Vector3.Zero,
                        Vector3.Zero,
                    },
                    ReadOnlyMemory<Vector3>.Empty))
                .ToArray(),
        };
        MorphWeight[] weights = Enumerable.Range(0, 65)
            .Select(index => new MorphWeight($"morph_{index}", 2.0f))
            .ToArray();

        CpuDeformedVertex result =
            CpuMeshDeformationEvaluator.Evaluate(
                mesh,
                null,
                weights)[0];

        AssertVector(
            new Vector3(
                2.0f * MorphTargetSelection.MaximumActiveTargetCount,
                0.0f,
                3.0f),
            result.Position);
    }

    [Fact]
    public void RejectsTheSameInvalidMeshThatGpuValidationRejects()
    {
        MeshRenderData mesh = CreateMesh(
            new MeshVertex(
                Vector3.Zero,
                Vector3.UnitY,
                Vector2.Zero,
                Vector4.UnitX,
                new Vector4(5.0f, 0.0f, 0.0f, 0.0f)),
            new Matrix4x4[] { Matrix4x4.Identity });
        SkeletonRenderData skeleton =
            CreateSkeleton(Matrix4x4.Identity);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => CpuMeshDeformationEvaluator.Evaluate(
                mesh,
                skeleton,
                []));

        Assert.Contains(
            "bone index",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static MeshRenderData CreateMesh(
        MeshVertex first,
        ReadOnlyMemory<Matrix4x4> inverseBindMatrices,
        bool isSkinned = true) =>
        new(
            "cpu-reference",
            new MeshVertex[]
            {
                first,
                new MeshVertex(
                    Vector3.UnitX,
                    Vector3.UnitY,
                    Vector2.UnitX,
                    Vector4.Zero,
                    Vector4.Zero),
                new MeshVertex(
                    Vector3.UnitZ,
                    Vector3.UnitY,
                    Vector2.UnitY,
                    Vector4.Zero,
                    Vector4.Zero),
            },
            new uint[] { 0, 1, 2 },
            Matrix4x4.CreateTranslation(0.0f, 0.0f, 3.0f),
            inverseBindMatrices,
            isSkinned);

    private static SkeletonRenderData CreateSkeleton(
        params Matrix4x4[] worldTransforms) =>
        new(
            worldTransforms
                .Select((transform, index) => new BoneRenderData(
                    $"bone_{index}",
                    -1,
                    transform,
                    transform,
                    false))
                .ToArray(),
            Matrix4x4.Identity);

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(
            Vector3.Distance(expected, actual),
            0.0f,
            1.0e-5f);
    }
}
