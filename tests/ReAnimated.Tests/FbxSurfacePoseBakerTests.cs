using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class FbxSurfacePoseBakerTests
{
    [Fact]
    public void LegacyMorphEvaluationUsesTheSameUsableInfluencesAsItsBaseVertex()
    {
        var source=Surface();
        var malformed=source with{Vertices=source.Vertices.SetItem(0,source.Vertices[0] with{BoneIndices=[0,99,1],BoneWeights=[2,-5]})};
        var clean=source with{Vertices=source.Vertices.SetItem(0,source.Vertices[0] with{BoneIndices=[0],BoneWeights=[1]})};
        TransformMatrix[] transforms=[TransformMatrix.CreateScale(new(2,.5,1)),TransformMatrix.Identity];
        var legacy=FbxSurfacePoseBaker.BakeLegacy(malformed,transforms);
        var expected=FbxSurfacePoseBaker.Bake(clean,transforms);
        Assert.True((legacy.Vertices[0].Position-expected.Vertices[0].Position).Length<1e-10);
        Assert.Equal(malformed.Vertices[0].BoneIndices,legacy.Vertices[0].BoneIndices);
        Assert.Equal(malformed.Vertices[0].BoneWeights,legacy.Vertices[0].BoneWeights);
        for(int i=0;i<legacy.MorphTargets.Length;i++)
        {
            Assert.True((legacy.MorphTargets[i].PositionDeltas[0]-expected.MorphTargets[i].PositionDeltas[0]).Length<1e-10);
            Assert.True((legacy.MorphTargets[i].NormalDeltas[0]-expected.MorphTargets[i].NormalDeltas[0]).Length<1e-10);
        }
        Assert.Throws<InvalidDataException>(()=>FbxSurfacePoseBaker.Bake(malformed,transforms));
    }
    [Fact]
    public void BakeUsesDrawSlotAffineMatricesAndPreservesSurfaceIdentity()
    {
        FbxModelSurface surface = Surface();
        TransformMatrix[] transforms =
        [
            TransformMatrix.CreateTranslation(new(.3, -.2, .1)) * TransformMatrix.CreateScale(new(2, 1, .5)),
            TransformMatrix.CreateTranslation(new(-.1, .4, .2)) * TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitZ, .4)) * TransformMatrix.CreateScale(new(.7, 1.4, 1.1)),
        ];
        FbxModelSurface baked = FbxSurfacePoseBaker.Bake(surface, transforms);
        Assert.Equal(surface.Indices.ToArray(), baked.Indices.ToArray());
        Assert.Equal(surface.SourceCorners.ToArray(), baked.SourceCorners.ToArray());
        Assert.Equal(surface.SourceTriangles.ToArray(), baked.SourceTriangles.ToArray());
        Assert.Equal(surface.PaletteBoneIndices.ToArray(), baked.PaletteBoneIndices.ToArray());
        Assert.Equal(surface.InverseBindMatrices.ToArray(), baked.InverseBindMatrices.ToArray());
        Assert.Equal(surface.MaterialId, baked.MaterialId);
        Assert.Equal(surface.Id, baked.Id);
        Assert.Equal(surface.Vertices.Select(static vertex => vertex.BoneIndices.ToArray()), baked.Vertices.Select(static vertex => vertex.BoneIndices.ToArray()));
        Assert.Equal(surface.Vertices.Select(static vertex => vertex.BoneWeights.ToArray()), baked.Vertices.Select(static vertex => vertex.BoneWeights.ToArray()));
        Assert.All(baked.Vertices, static vertex => Assert.Equal(1, vertex.Normal.Length, 12));
    }

    [Fact]
    public void SimultaneousSignedMorphsMatchIndependentWeightedInverseTransposeEvaluation()
    {
        FbxModelSurface surface = Surface();
        TransformMatrix[] transforms =
        [
            TransformMatrix.CreateScale(new(2, 1, .5)),
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitX, .7)) * TransformMatrix.CreateScale(new(.6, 1.7, 1.2)),
        ];
        FbxModelSurface baked = FbxSurfacePoseBaker.Bake(surface, transforms);
        double[] morphWeights = [.65, -.35];
        for (int index = 0; index < surface.Vertices.Length; index++)
        {
            FbxModelVertex vertex = surface.Vertices[index];
            double total = vertex.BoneWeights.Sum();
            Vector3D expectedPosition = Vector3D.Zero;
            Vector3D expectedNormal = Vector3D.Zero;
            Vector3D localPosition = vertex.Position;
            Vector3D localNormal = vertex.Normal;
            for (int morphIndex = 0; morphIndex < surface.MorphTargets.Length; morphIndex++)
            {
                localPosition += surface.MorphTargets[morphIndex].PositionDeltas[index] * morphWeights[morphIndex];
                localNormal += surface.MorphTargets[morphIndex].NormalDeltas[index] * morphWeights[morphIndex];
            }
            for (int influence = 0; influence < vertex.BoneIndices.Length; influence++)
            {
                int slot = vertex.BoneIndices[influence];
                double weight = vertex.BoneWeights[influence] / total;
                expectedPosition += transforms[slot].TransformPoint(localPosition) * weight;
                expectedNormal += InverseTranspose(transforms[slot], localNormal) * weight;
            }
            expectedNormal = expectedNormal.Normalized();
            Vector3D bakedPosition = baked.Vertices[index].Position;
            for (int morphIndex = 0; morphIndex < baked.MorphTargets.Length; morphIndex++)
                bakedPosition += baked.MorphTargets[morphIndex].PositionDeltas[index] * morphWeights[morphIndex];
            Assert.InRange((expectedPosition - bakedPosition).Length, 0, 1e-12);
            Vector3D bakedNormal = baked.Vertices[index].Normal;
            for (int morphIndex = 0; morphIndex < baked.MorphTargets.Length; morphIndex++)
                bakedNormal += baked.MorphTargets[morphIndex].NormalDeltas[index] * morphWeights[morphIndex];
            Assert.InRange((expectedNormal - bakedNormal.Normalized()).Length, 0, 1e-12);
        }
    }

    [Fact]
    public void UnnormalizedPositiveWeightsMatchTheirNormalizedEquivalent()
    {
        FbxModelSurface unnormalized = Surface() with
        {
            Vertices = Surface().Vertices.SetItem(0, Surface().Vertices[0] with { BoneWeights = [1, 3] }),
        };
        FbxModelSurface normalized = unnormalized with
        {
            Vertices = unnormalized.Vertices.SetItem(0, unnormalized.Vertices[0] with { BoneWeights = [.25, .75] }),
        };
        TransformMatrix[] transforms =
        [
            TransformMatrix.CreateScale(new(2, 1, .5)),
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, .5)) * TransformMatrix.CreateScale(new(.7, 1.3, 1.1)),
        ];
        FbxModelSurface first = FbxSurfacePoseBaker.Bake(unnormalized, transforms);
        FbxModelSurface second = FbxSurfacePoseBaker.Bake(normalized, transforms);
        Assert.Equal(second.Vertices[0].Position, first.Vertices[0].Position);
        Assert.Equal(second.Vertices[0].Normal, first.Vertices[0].Normal);
        Assert.Equal(second.MorphTargets.Select(morph => morph.PositionDeltas[0]).ToArray(), first.MorphTargets.Select(morph => morph.PositionDeltas[0]).ToArray());
        Assert.Equal(second.MorphTargets.Select(morph => morph.NormalDeltas[0]).ToArray(), first.MorphTargets.Select(morph => morph.NormalDeltas[0]).ToArray());
    }

    [Fact]
    public void StaticSurfacesPassThroughAndAbsentMorphNormalsRemainAbsent()
    {
        FbxModelSurface surface = Surface() with { IsSkinned = false };
        Assert.Same(surface, FbxSurfacePoseBaker.Bake(surface, [TransformMatrix.Identity]));
        FbxModelSurface noNormalMorph = Surface() with
        {
            MorphTargets = [new FbxModelMorphTarget("position-only", 9, 1, 2,
                Surface().Vertices.Select(static _ => new Vector3D(.01, 0, 0)).ToImmutableArray())],
        };
        FbxModelSurface baked = FbxSurfacePoseBaker.Bake(noNormalMorph, [TransformMatrix.Identity, TransformMatrix.Identity]);
        Assert.True(baked.MorphTargets[0].NormalDeltas.IsDefaultOrEmpty);
    }

    [Fact]
    public void SingularPaletteAndCollapsedNormalFailClosed()
    {
        FbxModelSurface surface = Surface();
        Assert.Throws<InvalidDataException>(() => FbxSurfacePoseBaker.Bake(surface,
            [TransformMatrix.CreateScale(new(0, 1, 1)), TransformMatrix.Identity]));
        TransformMatrix opposite = TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitX, Math.PI));
        FbxModelSurface collapsed = surface with
        {
            Vertices = surface.Vertices.Select(vertex => vertex with { BoneWeights = [.5, .5] }).ToImmutableArray(),
        };
        Assert.Throws<InvalidDataException>(() => FbxSurfacePoseBaker.Bake(collapsed, [TransformMatrix.Identity, opposite]));
    }

    private static FbxModelSurface Surface()
    {
        ImmutableArray<FbxModelVertex> vertices =
        [
            new(new(.2, .3, .4), new Vector3D(.2, .9, .1).Normalized(), .1, .2, [0, 1], [.25, .75]),
            new(new(-.2, .1, .3), new Vector3D(-.4, .7, .2).Normalized(), .3, .4, [0, 1], [.6, .4]),
            new(new(.1, -.2, .1), new Vector3D(.1, .8, -.3).Normalized(), .5, .6, [1], [1]),
        ];
        return new("surface", "mesh", Guid.Parse("10000000-0000-0000-0000-000000000001"), vertices,
            [0, 1, 2], [11, 12], [TransformMatrix.Identity, TransformMatrix.Identity], true)
        {
            SourceGeometry = new GeometrySourceComponent("component", vertices.Select(static vertex => vertex.Position).ToImmutableArray()),
            SourceCorners = [new(0, 0), new(1, 1), new(2, 2)],
            SourceTriangles = [new(3, 0)],
            MorphTargets =
            [
                new FbxModelMorphTarget("morph-a", 17, 1, 2,
                    [new(.01, .02, 0), new(-.02, .01, .01), new(.01, 0, -.02)])
                {
                    NormalDeltas = [new(.03, -.01, .02), new(-.01, .02, 0), new(.02, 0, .01)],
                },
                new FbxModelMorphTarget("morph-b", 31, 1, 3,
                    [new(-.02, .01, .01), new(.01, -.01, .02), new(0, .02, .01)])
                {
                    NormalDeltas = [new(-.02, .01, 0), new(.01, -.01, .02), new(0, .03, -.01)],
                },
            ],
        };
    }

    private static Vector3D InverseTranspose(TransformMatrix transform, Vector3D direction)
    {
        TransformMatrix inverse = transform.InvertedAffine();
        return new(
            inverse.M11 * direction.X + inverse.M21 * direction.Y + inverse.M31 * direction.Z,
            inverse.M12 * direction.X + inverse.M22 * direction.Y + inverse.M32 * direction.Z,
            inverse.M13 * direction.X + inverse.M23 * direction.Y + inverse.M33 * direction.Z);
    }
}
