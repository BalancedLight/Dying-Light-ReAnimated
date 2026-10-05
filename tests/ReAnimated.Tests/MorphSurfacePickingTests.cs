using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class MorphSurfacePickingTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void NearestTwoSidedRayHitUsesCurrentTriangleOrdinalAndWorldDistance()
    {
        FbxModelSurface surface = Surface(
            [new(0, 0, 2), new(2, 0, 2), new(0, 2, 2),
             new(0, 0, 5), new(2, 0, 5), new(0, 2, 5)],
            [0, 1, 2, 3, 5, 4]);
        MorphSurfacePicking picker = MorphSurfacePicking.Build(surface);

        MorphSurfacePickHit front = Assert.IsType<MorphSurfacePickHit>(picker.Pick(new(0.2, 0.2, 0), new(0, 0, 50)));
        Assert.Equal("face", picker.SurfaceId);
        Assert.Equal(0, front.TriangleIndex);
        Assert.Equal(0, front.VertexIndex);
        Assert.Equal(2.0, front.Distance, 10);
        Assert.Equal(new Vector3D(0.2, 0.2, 2), front.Position);

        MorphSurfacePickHit back = Assert.IsType<MorphSurfacePickHit>(picker.Pick(new(0.2, 0.2, 6), new(0, 0, -2)));
        Assert.Equal(1, back.TriangleIndex);
        Assert.Equal(3, back.VertexIndex);
        Assert.Equal(1.0, back.Distance, 10);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void VertexTieChoosesLowerRenderVertexIndex()
    {
        MorphSurfacePicking picker = MorphSurfacePicking.Build(Surface(
            [new(0, 0, 2), new(2, 0, 2), new(0, 2, 2)], [0, 1, 2]));
        MorphSurfacePickHit hit = Assert.IsType<MorphSurfacePickHit>(picker.Pick(new(1, 0, 0), Vector3D.UnitZ));
        Assert.Equal(0, hit.TriangleIndex);
        Assert.Equal(0, hit.VertexIndex);
        Assert.Equal(new Vector3D(1, 0, 2), hit.Position);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void SplitCornerReturnsRenderedVertexRatherThanOriginalControlPoint()
    {
        FbxModelSurface surface = Surface(
            [new(0, 0, 2), new(2, 0, 2), new(0, 2, 2),
             new(0, 0, 2), new(-2, 0, 2), new(0, -2, 2)],
            [0, 1, 2, 3, 4, 5]) with
        {
            SourceGeometry = new GeometrySourceComponent("original", [new(0, 0, 2), new(2, 0, 2),
                new(0, 2, 2), new(-2, 0, 2), new(0, -2, 2)]),
            SourceCorners = [new(0, 0), new(1, 1), new(2, 2), new(0, 3), new(3, 4), new(4, 5)],
        };
        MorphSurfacePicking picker = MorphSurfacePicking.Build(surface);
        MorphSurfacePickHit hit = Assert.IsType<MorphSurfacePickHit>(picker.Pick(new(-0.1, -0.1, 0), Vector3D.UnitZ));
        Assert.Equal(1, hit.TriangleIndex);
        Assert.Equal(3, hit.VertexIndex);
        Assert.Equal(0, surface.SourceCorners[hit.VertexIndex].ControlPointIndex);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void MissAndInvalidRaysFailWithoutChangingPicker()
    {
        MorphSurfacePicking picker = MorphSurfacePicking.Build(Surface(
            [new(0, 0, 2), new(2, 0, 2), new(0, 2, 2)], [0, 1, 2]));
        Assert.Null(picker.Pick(new(3, 3, 0), Vector3D.UnitZ));
        Assert.Null(picker.Pick(new(0.2, 0.2, 0), -Vector3D.UnitZ));
        Assert.Throws<ArgumentException>(() => picker.Pick(new(double.NaN, 0, 0), Vector3D.UnitZ));
        Assert.Throws<ArgumentException>(() => picker.Pick(Vector3D.Zero, Vector3D.Zero));
        Assert.Equal(1, picker.TriangleCount);
        Assert.Equal(3, picker.VertexCount);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void BuildRejectsMissingMalformedDegenerateOrOutOfBoundTopology()
    {
        FbxModelSurface valid = Surface([new(0, 0, 2), new(2, 0, 2), new(0, 2, 2)], [0, 1, 2]);
        Assert.Throws<InvalidDataException>(() => MorphSurfacePicking.Build(valid with { Indices = [] }));
        Assert.Throws<InvalidDataException>(() => MorphSurfacePicking.Build(valid with { Indices = [0, 1] }));
        Assert.Throws<InvalidDataException>(() => MorphSurfacePicking.Build(valid with { Indices = [0, 1, 3] }));
        Assert.Throws<InvalidDataException>(() => MorphSurfacePicking.Build(valid with { Indices = [0, 0, 2] }));
        Assert.Throws<InvalidDataException>(() => MorphSurfacePicking.Build(Surface(
            [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0)], [0, 1, 2])));
        Assert.Throws<InvalidDataException>(() => MorphSurfacePicking.Build(Surface(
            [new(double.PositiveInfinity, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2])));
        Assert.Throws<InvalidDataException>(() => MorphSurfacePicking.Build(Surface(
            [new(TriangleSpatialIndex.MaximumAbsoluteCoordinate * 2, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2])));
    }

    private static FbxModelSurface Surface(ImmutableArray<Vector3D> positions, ImmutableArray<uint> indices) =>
        new("face", "Face", Guid.NewGuid(), positions.Select(position =>
            new FbxModelVertex(position, Vector3D.UnitZ, 0, 0, [], [])).ToImmutableArray(),
            indices, [], [], IsSkinned: false);
}
