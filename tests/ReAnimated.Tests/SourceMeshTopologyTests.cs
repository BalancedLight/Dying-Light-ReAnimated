using ReAnimated.Core.Geometry;

namespace ReAnimated.Tests;

public sealed class SourceMeshTopologyTests
{
    [Fact]
    public void BuildsDeterministicIslandsFromSharedControlPointsAndReportsUnusedPoints()
    {
        SourceMeshTopologyResult result = SourceMeshTopology.Build(
            10,
            [
                (3, 4, 5),
                (5, 6, 7),
                (0, 1, 2),
                (2, 8, 0),
            ]);

        Assert.Equal(2, result.Islands.Length);
        Assert.Equal<int>([3, 4, 5, 6, 7], result.Islands[0].SourceControlPointIds);
        Assert.Equal<int>([0, 1], result.Islands[0].TriangleIndices);
        Assert.Equal<int>([0, 1, 2, 8], result.Islands[1].SourceControlPointIds);
        Assert.Equal<int>([2, 3], result.Islands[1].TriangleIndices);
        Assert.Equal<int>([9], result.IsolatedControlPointIds);
        Assert.Equal<int>(result.IsolatedControlPointIds, result.UnusedControlPointIds);
    }

    [Fact]
    public void ReportsBoundaryNonManifoldAndWindingConflictEdges()
    {
        SourceMeshTopologyResult result = SourceMeshTopology.Build(
            5,
            [
                (0, 1, 2),
                (0, 2, 3),
                (0, 1, 3),
                (0, 2, 4),
            ]);

        SourceMeshTopologyEdge nonManifold = Assert.Single(
            result.NonManifoldEdges,
            edge => edge.A == 0 && edge.B == 2);
        Assert.Equal<int>([0, 1, 3], nonManifold.TriangleIndices);
        Assert.Contains(
            result.WindingConflictEdges,
            edge => edge.A == 0 && edge.B == 1);
        Assert.Contains(
            result.WindingConflictEdges,
            edge => edge.A == 0 && edge.B == 2);
        Assert.Equal(
            result.Edges.Count(edge => edge.Incidents.Length == 1),
            result.BoundaryEdges.Length);
        Assert.DoesNotContain(
            result.BoundaryEdges,
            edge => edge.A == 0 && edge.B == 2);
    }

    [Fact]
    public void UsesCanonicalEdgeOrderAndOppositeWindingIsConsistent()
    {
        SourceMeshTopologyResult result = SourceMeshTopology.Build(
            4,
            [
                (2, 1, 0),
                (0, 1, 3),
            ]);

        SourceMeshTopologyEdge shared = Assert.Single(
            result.Edges,
            edge => edge.A == 0 && edge.B == 1);
        Assert.Equal<int>([0, 1], shared.TriangleIndices);
        Assert.False(shared.HasWindingConflict);
    }

    [Fact]
    public void RejectsOutOfRangeControlPoints()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SourceMeshTopology.Build(3, [(0, 1, 3)]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SourceMeshTopology.Build(3, [(-1, 1, 2)]));
    }

    [Fact]
    public void RejectsCountsBeforeAllocatingTopologyStorage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SourceMeshTopology.Build(
                SourceMeshTopology.MaximumControlPointCount + 1,
                []));

        var overLimit = new CountOnlyTriangles(SourceMeshTopology.MaximumTriangleCount + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SourceMeshTopology.Build(0, overLimit));
        Assert.Equal(0, overLimit.IndexerCalls);
    }

    [Fact]
    public void RejectsRepeatedTriangleCorners()
    {
        Assert.Throws<ArgumentException>(() =>
            SourceMeshTopology.Build(3, [(0, 0, 1)]));
        Assert.Throws<ArgumentException>(() =>
            SourceMeshTopology.Build(3, [(0, 1, 0)]));
        Assert.Throws<ArgumentException>(() =>
            SourceMeshTopology.Build(3, [(2, 1, 1)]));
    }

    [Fact]
    public void HonorsCancellationBeforeTraversal()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SourceMeshTopology.Build(3, [(0, 1, 2)], cancellation.Token));
    }

    [Fact]
    public void HonorsCancellationDuringInputTraversal()
    {
        using var cancellation = new CancellationTokenSource();
        var triangles = new CancellingTriangles(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            SourceMeshTopology.Build(4, triangles, cancellation.Token));
    }

    [Fact]
    public void TraversesLargeSharedControlPointFanWithoutRepeatedAdjacencyScans()
    {
        const int triangleCount = 12_000;
        var triangles = new (int A, int B, int C)[triangleCount];
        for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            triangles[triangleIndex] = (0, triangleIndex + 1, triangleIndex + 2);
        }

        SourceMeshTopologyResult result = SourceMeshTopology.Build(
            triangleCount + 2,
            triangles);

        SourceMeshTopologyIsland island = Assert.Single(result.Islands);
        Assert.Equal(triangleCount, island.TriangleIndices.Length);
        Assert.Equal(triangleCount + 2, island.SourceControlPointIds.Length);
        Assert.Equal(triangleCount + 2, result.BoundaryEdges.Length);
    }

    [Fact]
    public void EmptyTrianglesLeaveEveryControlPointIsolated()
    {
        SourceMeshTopologyResult result = SourceMeshTopology.Build(4, []);

        Assert.Empty(result.Islands);
        Assert.Equal<int>([0, 1, 2, 3], result.IsolatedControlPointIds);
        Assert.Empty(result.Edges);
        Assert.Empty(result.BoundaryEdges);
        Assert.Empty(result.NonManifoldEdges);
        Assert.Empty(result.WindingConflictEdges);
    }

    private sealed class CancellingTriangles : IReadOnlyList<(int A, int B, int C)>
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingTriangles(CancellationTokenSource cancellation) =>
            _cancellation = cancellation;

        public int Count => 3;

        public (int A, int B, int C) this[int index]
        {
            get
            {
                if (index == 2)
                {
                    _cancellation.Cancel();
                }

                return index switch
                {
                    0 => (0, 1, 2),
                    1 => (1, 2, 3),
                    _ => (0, 2, 3),
                };
            }
        }

        public IEnumerator<(int A, int B, int C)> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class CountOnlyTriangles : IReadOnlyList<(int A, int B, int C)>
    {
        public CountOnlyTriangles(int count) => Count = count;

        public int Count { get; }

        public int IndexerCalls { get; private set; }

        public (int A, int B, int C) this[int index]
        {
            get
            {
                IndexerCalls++;
                throw new InvalidOperationException("The over-limit input must be rejected before indexing.");
            }
        }

        public IEnumerator<(int A, int B, int C)> GetEnumerator() =>
            throw new InvalidOperationException("The over-limit input must be rejected before enumeration.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
