using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class SourceGeometrySpatialQueriesTests
{
    [Fact]
    public void HiddenSurfaceCanBeQueriedThroughAComponentRestrictionWithoutChangingRenderData()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSeparatedComponentFixture(), "components.fbx");
        var analysis = FbxSourceGeometryAnalysis.Build(model);
        Assert.Equal(2, analysis.Components.Length);
        var front = analysis.Components.Single(c => c.Geometry.Id == "fbx:200:201");
        var rear = analysis.Components.Single(c => c.Geometry.Id != front.Geometry.Id);
        var queries = SourceGeometrySpatialQueries.Build(analysis);
        var rearCenter = Center(rear);
        var frontCenter = Center(front);
        var direction = frontCenter - rearCenter;
        var origin = frontCenter + direction;
        var allHit = Assert.IsType<SourceGeometrySurfaceHit>(queries.FindNearestRayHit(origin, -direction, double.PositiveInfinity));
        Assert.Equal(front.Geometry.Id, allHit.ComponentId);
        var rearHit = Assert.IsType<SourceGeometrySurfaceHit>(queries.FindNearestRayHit(origin, -direction, double.PositiveInfinity, rear.Geometry.Id));
        Assert.Equal(rear.Geometry.Id, rearHit.ComponentId);
        Assert.True((rearCenter - rearHit.Point).Length < 1e-10);
        Assert.True(rearHit.Distance > allHit.Distance);
        Assert.Equal(rear.Triangles[0], rearHit.Triangle);
        Assert.True((Reconstruct(rear, rearHit) - rearHit.Point).Length < 1e-10);
        Assert.Equal(model.Package.Document.Source.ContentSha256, queries.SourceSha256);
        var closest = Assert.IsType<SourceGeometrySurfaceHit>(queries.FindClosestPoint(origin, double.PositiveInfinity, rear.Geometry.Id));
        Assert.True((closest.Point - rearCenter).Length < 1e-10);
        Assert.Null(queries.FindClosestPoint(origin, 0, rear.Geometry.Id));
        Assert.Throws<ArgumentException>(() => queries.FindClosestPoint(origin, 1, "missing"));
        Assert.Same(model.Surfaces[0].SourceGeometry, analysis.Components.Single(c => c.Geometry.Id == model.Surfaces[0].SourceGeometry!.Id).Geometry);

        var session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.RepairExistingRig);
        session = session with { Components = session.Components.Select(c => c.Id == front.Geometry.Id ? c with { UseForAnatomy = false } : c).ToImmutableArray() };
        var selected = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session } } };
        var anatomy = SourceGeometrySpatialQueries.Build(FbxSourceGeometryAnalysis.Build(selected));
        Assert.Equal(1, anatomy.ComponentCount);
        Assert.Equal(rear.Geometry.Id, anatomy.FindNearestRayHit(origin, -direction, double.PositiveInfinity)!.ComponentId);
        Assert.Throws<ArgumentException>(() => anatomy.FindClosestPoint(origin, 1, front.Geometry.Id));
        Assert.Equal(2, SourceGeometrySpatialQueries.Build(FbxSourceGeometryAnalysis.Build(selected, anatomyOnly: false)).ComponentCount);
        Assert.Equal<FbxModelSurface>(model.Surfaces, selected.Surfaces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransformedAndPartitionedSourceQueriesRetainTriangleIdentity(bool transformed)
    {
        var model = FbxModelAuthoringImporter.Import(transformed ? BlenderFbxStrictValidationTests.CreateSourceCoordinateFixture() :
            BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(false, true), "partitions.fbx");
        var analysis = FbxSourceGeometryAnalysis.Build(model);
        var component = Assert.Single(analysis.Components);
        var queries = SourceGeometrySpatialQueries.Build(analysis);
        foreach (var triangle in component.Triangles)
        {
            var positions = component.Geometry.ControlPoints;
            var center = (positions[triangle.A] + positions[triangle.B] + positions[triangle.C]) / 3;
            var hit = Assert.IsType<SourceGeometrySurfaceHit>(queries.FindClosestPoint(center, 1));
            Assert.Equal(triangle.Source, hit.Triangle.Source);
            Assert.True((Reconstruct(component, hit) - center).Length < 1e-10);
        }
        var reordered = SourceGeometrySpatialQueries.Build(FbxSourceGeometryAnalysis.Build(model with { Surfaces = model.Surfaces.Reverse().ToImmutableArray() }));
        Assert.Equal(queries.FindClosestPoint(Vector3D.Zero, 1), reordered.FindClosestPoint(Vector3D.Zero, 1));
        Assert.ThrowsAny<OperationCanceledException>(() => queries.FindClosestPoint(Vector3D.Zero, 1, cancellationToken: new(true)));
    }

    [Fact]
    public void EmptyAnalysisStillValidatesQueriesAndCancellation()
    {
        var analysis = new SourceGeometryAnalysis(new string('a', 64), []);
        var queries = SourceGeometrySpatialQueries.Build(analysis);
        Assert.Null(queries.FindClosestPoint(Vector3D.Zero, 1));
        Assert.Null(queries.FindNearestRayHit(Vector3D.Zero, Vector3D.UnitX, 1));
        Assert.Throws<ArgumentException>(() => queries.FindNearestRayHit(Vector3D.Zero, Vector3D.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => queries.FindClosestPoint(Vector3D.Zero, -1));
        Assert.ThrowsAny<OperationCanceledException>(() => SourceGeometrySpatialQueries.Build(analysis, new(true)));
    }

    private static Vector3D Center(SourceGeometryComponentAnalysis component)
    {
        var triangle = component.Triangles[0];
        var positions = component.Geometry.ControlPoints;
        return (positions[triangle.A] + positions[triangle.B] + positions[triangle.C]) / 3;
    }

    private static Vector3D Reconstruct(SourceGeometryComponentAnalysis component, SourceGeometrySurfaceHit hit) =>
        component.Geometry.ControlPoints[hit.Triangle.A] * hit.BarycentricWeights.X +
        component.Geometry.ControlPoints[hit.Triangle.B] * hit.BarycentricWeights.Y +
        component.Geometry.ControlPoints[hit.Triangle.C] * hit.BarycentricWeights.Z;
}
