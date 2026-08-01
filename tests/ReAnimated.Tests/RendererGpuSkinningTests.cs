using System.Numerics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererGpuSkinningTests
{
    [Fact]
    public void PaletteComposesInverseBindPoseAndMeshTransforms()
    {
        MeshRenderData mesh = CreateTriangle(
            inverseBindMatrices:
            new Matrix4x4[]
            {
                Matrix4x4.CreateTranslation(-2.0f, 0.0f, 0.0f),
            }) with
        {
            LocalToWorld = Matrix4x4.CreateTranslation(0.0f, 0.0f, 4.0f),
        };
        SkeletonRenderData skeleton = new(
            [
                new BoneRenderData(
                    "root",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.CreateTranslation(5.0f, 0.0f, 0.0f),
                    false),
            ],
            Matrix4x4.CreateTranslation(0.0f, 3.0f, 0.0f));

        Matrix4x4[] palette = GpuSkinningPalette.Build(mesh, skeleton);

        Assert.Single(palette);
        Vector3 transformed = Vector3.Transform(Vector3.Zero, palette[0]);
        Assert.Equal(new Vector3(3.0f, 3.0f, 4.0f), transformed);
    }

    [Fact]
    public void PerDrawPaletteMapsIntoLargerSkeletonWithoutTruncation()
    {
        BoneRenderData[] bones = Enumerable.Range(0, 300)
            .Select(index => new BoneRenderData(
                $"bone_{index}",
                -1,
                Matrix4x4.Identity,
                index == 299
                    ? Matrix4x4.CreateTranslation(7, 8, 9)
                    : Matrix4x4.Identity,
                false))
            .ToArray();
        var skeleton = new SkeletonRenderData(
            bones,
            Matrix4x4.Identity);
        MeshRenderData mesh = CreateTriangle(
            new Matrix4x4[]
            {
                Matrix4x4.Identity,
            }) with
        {
            SkinBoneIndices = new int[] { 299 },
        };

        Assert.True(
            RenderMeshValidation.TryValidate(
                mesh,
                skeleton,
                out string? error),
            error);
        Matrix4x4 mapped =
            Assert.Single(
                GpuSkinningPalette.Build(
                    mesh,
                    skeleton));

        Assert.Equal(
            new Vector3(7, 8, 9),
            Vector3.Transform(
                Vector3.Zero,
                mapped));
    }

    [Theory]
    [InlineData(2, 1, 300, "inverse-bind")]
    [InlineData(1, 1, 300, "outside")]
    [InlineData(257, 257, 300, "256")]
    public void InvalidPerDrawPaletteFailsClosed(
        int inverseBindCount,
        int mappedBoneCount,
        int skeletonBoneCount,
        string expectedError)
    {
        Matrix4x4[] inverseBinds =
            Enumerable.Repeat(
                    Matrix4x4.Identity,
                    inverseBindCount)
                .ToArray();
        int[] map = Enumerable.Range(0, mappedBoneCount)
            .ToArray();
        if (inverseBindCount == 1 &&
            mappedBoneCount == 1)
        {
            map[0] = skeletonBoneCount;
        }

        MeshRenderData mesh = CreateTriangle(
            inverseBinds) with
        {
            SkinBoneIndices = map,
        };
        var skeleton = new SkeletonRenderData(
            Enumerable.Range(0, skeletonBoneCount)
                .Select(index => new BoneRenderData(
                    $"bone_{index}",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                    false))
                .ToArray(),
            Matrix4x4.Identity);

        Assert.False(
            RenderMeshValidation.TryValidate(
                mesh,
                skeleton,
                out string? error));
        Assert.Contains(
            expectedError,
            error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationRejectsWeightedOutOfRangeBoneIndex()
    {
        MeshRenderData mesh = CreateTriangle(
            inverseBindMatrices:
            new Matrix4x4[]
            {
                Matrix4x4.Identity,
            },
            boneIndex: 7.0f);
        SkeletonRenderData skeleton = new(
            [
                new BoneRenderData(
                    "root",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                    false),
            ],
            Matrix4x4.Identity);

        bool valid = RenderMeshValidation.TryValidate(
            mesh,
            skeleton,
            out string? error);

        Assert.False(valid);
        Assert.Contains("bone index", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CameraMathProducesFiniteProjectionForDegenerateInput()
    {
        RenderCamera camera = new(
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            999.0f,
            0.0f,
            0.0f);

        Matrix4x4 viewProjection =
            RenderCameraMath.CreateViewProjection(camera, 0, 0);

        Assert.All(
            new[]
            {
                viewProjection.M11,
                viewProjection.M22,
                viewProjection.M33,
                viewProjection.M44,
            },
            value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void MorphSelectionKeepsFirstSixtyFourActiveTargetsInRetailOrder()
    {
        MeshRenderData mesh = CreateTriangle(
            new Matrix4x4[] { Matrix4x4.Identity }) with
        {
            MorphTargets = Enumerable.Range(0, 70)
                .Select(index => new MorphTargetRenderData(
                    $"morph_{index}",
                    new Vector3[3],
                    ReadOnlyMemory<Vector3>.Empty))
                .ToArray(),
        };
        MorphWeight[] weights = Enumerable.Range(0, 70)
            .Select(index => new MorphWeight(
                $"MORPH_{index}",
                70.0f - index))
            .ToArray();

        ActiveMorphTarget[] active =
            MorphTargetSelection.Select(mesh, weights);

        Assert.Equal(
            MorphTargetSelection.MaximumActiveTargetCount,
            active.Length);
        Assert.Equal("morph_0", active[0].Target.Name);
        Assert.Equal(0, active[0].TargetIndex);
        Assert.Equal(70.0f, active[0].Weight);
        Assert.Equal("morph_63", active[^1].Target.Name);
        Assert.Equal(63, active[^1].TargetIndex);
    }

    [Fact]
    public void MorphSelectionUsesRuntimeActivityThresholdWithoutClamping()
    {
        MeshRenderData mesh = CreateTriangle(
            new Matrix4x4[] { Matrix4x4.Identity }) with
        {
            MorphTargets =
            [
                new MorphTargetRenderData(
                    "below",
                    new Vector3[3],
                    ReadOnlyMemory<Vector3>.Empty),
                new MorphTargetRenderData(
                    "edge",
                    new Vector3[3],
                    ReadOnlyMemory<Vector3>.Empty),
                new MorphTargetRenderData(
                    "raw",
                    new Vector3[3],
                    ReadOnlyMemory<Vector3>.Empty),
            ],
        };

        ActiveMorphTarget[] active = MorphTargetSelection.Select(
            mesh,
            [
                new MorphWeight("below", 0.0009f),
                new MorphWeight("edge", 0.001f),
                new MorphWeight("raw", 12.5f),
            ]);

        ActiveMorphTarget selected = Assert.Single(active);
        Assert.Equal("raw", selected.Target.Name);
        Assert.Equal(12.5f, selected.Weight);
    }

    private static MeshRenderData CreateTriangle(
        ReadOnlyMemory<Matrix4x4> inverseBindMatrices,
        float boneIndex = 0.0f)
    {
        MeshVertex[] vertices =
        [
            new(
                Vector3.Zero,
                Vector3.UnitY,
                Vector2.Zero,
                Vector4.UnitX,
                new Vector4(boneIndex, 0.0f, 0.0f, 0.0f)),
            new(
                Vector3.UnitX,
                Vector3.UnitY,
                Vector2.UnitX,
                Vector4.UnitX,
                new Vector4(boneIndex, 0.0f, 0.0f, 0.0f)),
            new(
                Vector3.UnitZ,
                Vector3.UnitY,
                Vector2.UnitY,
                Vector4.UnitX,
                new Vector4(boneIndex, 0.0f, 0.0f, 0.0f)),
        ];
        return new MeshRenderData(
            "triangle",
            vertices,
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            inverseBindMatrices,
            true);
    }
}
