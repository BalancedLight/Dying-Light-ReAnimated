using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FbxSourceGeometryAnalysisTests
{
    [Fact]
    public void CoordinateProvenanceIncludesUnitsPlacementAndReflectionWithoutApplyingConversionAgain()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceCoordinateFixture(), "coordinates.fbx");
        var geometry = Assert.Single(FbxSourceGeometryAnalysis.Build(model).Components).Geometry;
        var coordinates = Assert.IsType<GeometrySourceCoordinates>(geometry.Coordinates);
        Assert.Equal(0.1, coordinates.MetersPerSourceUnit, 12);
        Vector3D[] original = [Vector3D.Zero, Vector3D.UnitX, Vector3D.UnitY];
        for (int i = 0; i < original.Length; i++)
        {
            var converted = coordinates.SourceToAuthoring.TransformPoint(original[i]);
            Assert.True((converted - geometry.ControlPoints[i]).Length < 1e-12);
        }
        var surface = model.Surfaces[0];
        Assert.Equal<int>([0, 2, 1], surface.SourceCorners.Select(c => c.ControlPointIndex));
        for (int i = 0; i < surface.Vertices.Length; i++)
            Assert.Equal(geometry.ControlPoints[surface.SourceCorners[i].ControlPointIndex], surface.Vertices[i].Position);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ProvenanceSurvivesTriangulationAndMaterialPartitioning(bool quad, bool splitMaterials)
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(quad, splitMaterials), "generic.fbx");
        Assert.Equal(splitMaterials ? 2 : 1, model.Surfaces.Length);
        var geometry = model.Surfaces[0].SourceGeometry!;
        Assert.Equal(4, geometry.ControlPoints.Length);
        foreach (var surface in model.Surfaces)
        {
            Assert.Same(geometry, surface.SourceGeometry);
            Assert.Equal(surface.Vertices.Length, surface.SourceCorners.Length);
            Assert.Equal(surface.Indices.Length / 3, surface.SourceTriangles.Length);
            for (int i = 0; i < surface.Vertices.Length; i++)
                Assert.Equal(geometry.ControlPoints[surface.SourceCorners[i].ControlPointIndex], surface.Vertices[i].Position);
        }
        var analysis = FbxSourceGeometryAnalysis.Build(model);
        var component = Assert.Single(analysis.Components);
        Assert.Equal(2, component.Triangles.Length);
        Assert.Single(component.Topology.Islands);
        Assert.Equal(4, component.Topology.BoundaryEdges.Length);
        Assert.Equal(quad ? 1 : 2, component.Triangles.Select(t => t.Source.PolygonIndex).Distinct().Count());
        if (quad) Assert.Equal([0, 1], component.Triangles.Select(t => t.Source.TriangleInPolygon));
        else
        {
            var repeated = model.Surfaces.SelectMany(s => s.SourceCorners).Where(c => c.ControlPointIndex == 0).ToArray();
            Assert.Equal(2, repeated.Length);
            Assert.NotEqual(repeated[0].PolygonVertexIndex, repeated[1].PolygonVertexIndex);
        }
    }

    [Fact]
    public void AnalysisRespectsAnatomySelectionWithoutChangingSourceData()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic.fbx");
        var session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.RepairExistingRig);
        Assert.Equal(session.Components[0].Id, model.Surfaces[0].SourceGeometry!.Id);
        session = session with { Components = session.Components.SetItem(0, session.Components[0] with { UseForAnatomy = false }) };
        var selected = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session } } };
        Assert.Empty(FbxSourceGeometryAnalysis.Build(selected).Components);
        Assert.Single(FbxSourceGeometryAnalysis.Build(selected, anatomyOnly: false).Components);
        Assert.Same(model.Surfaces[0], selected.Surfaces[0]);
        var stale = selected with { Package = selected.Package with { Document = selected.Package.Document with {
            RiggingSession = session with { SourceSha256 = new string('a', 64) } } } };
        Assert.Throws<InvalidDataException>(() => FbxSourceGeometryAnalysis.Build(stale));
    }

    [Fact]
    public void InvalidOrDuplicateProvenanceCannotSilentlyBecomeGeometry()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic.fbx");
        var surface = model.Surfaces[0];
        Assert.Throws<InvalidDataException>(() => FbxSourceGeometryAnalysis.Build(model with { Surfaces = [surface with { SourceCorners = [] }] }));
        Assert.Throws<InvalidDataException>(() => FbxSourceGeometryAnalysis.Build(model with { Surfaces = [surface, surface] }));
        Assert.Throws<InvalidDataException>(() => FbxSourceGeometryAnalysis.Build(model with { Surfaces = [surface with { SourceGeometry = null }] }));
        Assert.ThrowsAny<OperationCanceledException>(() => FbxSourceGeometryAnalysis.Build(model, cancellationToken: new(true)));
    }

    [Fact]
    public void SurfaceOrderDoesNotChangeSourceTriangleIdentity()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(false, true), "generic.fbx");
        var first = FbxSourceGeometryAnalysis.Build(model).Components[0];
        var second = FbxSourceGeometryAnalysis.Build(model with { Surfaces = model.Surfaces.Reverse().ToImmutableArray() }).Components[0];
        Assert.Equal<SourceGeometryAnalysisTriangle>(first.Triangles, second.Triangles);
        Assert.Throws<InvalidDataException>(() => FbxSourceGeometryAnalysis.Build(model with { Surfaces = [model.Surfaces[0]] }));
    }

    [Fact]
    public void CoincidentTrianglesWithDifferentSourcePointsRemainSeparateIslands()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(false, false, true), "generic.fbx");
        var component = Assert.Single(FbxSourceGeometryAnalysis.Build(model).Components);
        Assert.Equal(component.Geometry.ControlPoints[0], component.Geometry.ControlPoints[3]);
        Assert.Equal(2, component.Topology.Islands.Length);
        Assert.Equal(6, component.Topology.BoundaryEdges.Length);
    }
}
