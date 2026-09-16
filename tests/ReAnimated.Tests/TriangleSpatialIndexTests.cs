using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class TriangleSpatialIndexTests
{
    [Fact]
    public void ClosestPointMatchesIndependentExhaustiveReferenceAndRetainsOrdinals()
    {
        Vector3D[] positions =
        [
            new(0, 0, 0), new(2, 0, 0), new(0, 2, 0),
            new(0, 0, 4), new(2, 0, 4), new(0, 2, 4),
        ];
        (int A, int B, int C)[] triangles =
        [
            (0, 1, 2),
            (3, 4, 5),
        ];
        TriangleSpatialIndex index = TriangleSpatialIndex.Build(positions, triangles);
        Vector3D query = new(0.4, 0.7, 1.1);

        TriangleClosestPoint? actual = index.FindClosestPoint(query, 10.0);
        (int ordinal, Vector3D point, double distanceSquared) expected =
            ExhaustiveClosest(query, positions, triangles);

        Assert.True(actual.HasValue);
        Assert.Equal(expected.ordinal, actual.Value.TriangleOrdinal);
        Assert.Equal(expected.point, actual.Value.Point);
        Assert.Equal(expected.distanceSquared, actual.Value.DistanceSquared, 10);
    }

    [Fact]
    public void RayQueryMatchesExhaustiveReferenceIsTwoSidedAndNormalizesDirection()
    {
        Vector3D[] positions =
        [
            new(-1, -1, 2), new(1, -1, 2), new(-1, 1, 2),
            new(-1, -1, 5), new(1, -1, 5), new(-1, 1, 5),
        ];
        (int A, int B, int C)[] triangles =
        [
            (0, 2, 1),
            (3, 4, 5),
        ];
        TriangleSpatialIndex index = TriangleSpatialIndex.Build(positions, triangles);

        TriangleRayHit? actual = index.FindNearestRayHit(
            new(0, 0, 0),
            new(0, 0, 2),
            10.0);
        Assert.True(actual.HasValue);
        Assert.Equal(0, actual.Value.TriangleOrdinal);
        Assert.Equal(2.0, actual.Value.Distance, 10);
        Assert.Equal(new Vector3D(0, 0, 2), actual.Value.Point);
        Assert.Equal(
            ExhaustiveRayDistance(
                new(0, 0, 0),
                new(0, 0, 2),
                positions,
                triangles),
            actual.Value.Distance,
            10);
    }

    [Fact]
    public void ChoosesLowestOrdinalForExactClosestAndRayTies()
    {
        Vector3D[] positions =
        [
            new(-1, -1, 2), new(1, -1, 2), new(-1, 1, 2),
            new(-1, -1, 2), new(1, -1, 2), new(-1, 1, 2),
        ];
        TriangleSpatialIndex index = TriangleSpatialIndex.Build(
            positions,
            [(0, 1, 2), (3, 5, 4)]);

        TriangleClosestPoint? closestResult = index.FindClosestPoint(new(0, 0, 2), 0.0);
        Assert.True(closestResult.HasValue);
        TriangleClosestPoint closest = closestResult.Value;
        Assert.Equal(0, closest.TriangleOrdinal);
        TriangleRayHit? hitResult = index.FindNearestRayHit(
            new(0, 0, 0),
            new(0, 0, 1),
            10.0);
        Assert.True(hitResult.HasValue);
        TriangleRayHit hit = hitResult.Value;
        Assert.Equal(0, hit.TriangleOrdinal);
    }

    [Fact]
    public void HandlesDegenerateAndEmptyInputsWithoutNonFiniteResults()
    {
        TriangleSpatialIndex index = TriangleSpatialIndex.Build(
            [new(0, 0, 0), new(2, 0, 0), new(1, 0, 0), new(0, 0, 2)],
            [(0, 1, 2), (0, 0, 0)]);

        TriangleClosestPoint? closest = index.FindClosestPoint(new(1, 1, 0), 10.0);
        Assert.True(closest.HasValue);
        Assert.Equal(0, closest.Value.TriangleOrdinal);
        Assert.True(double.IsFinite(closest.Value.Distance));
        Assert.Null(index.FindNearestRayHit(new(0, 1, 0), new(0, 0, 1), 10.0));

        TriangleSpatialIndex empty = TriangleSpatialIndex.Build(
            [new(0, 0, 0)],
            []);
        Assert.Null(empty.FindClosestPoint(Vector3D.Zero, double.PositiveInfinity));
        Assert.Null(empty.FindNearestRayHit(Vector3D.Zero, Vector3D.UnitZ, 1.0));
    }

    [Fact]
    public void EnforcesFiniteIndicesAndDistanceBounds()
    {
        Assert.Throws<ArgumentException>(() =>
            TriangleSpatialIndex.Build([new(double.NaN, 0, 0)], []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TriangleSpatialIndex.Build([Vector3D.Zero], [(0, 1, 0)]));

        TriangleSpatialIndex index = TriangleSpatialIndex.Build(
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            [(0, 1, 2)]);
        Assert.Throws<ArgumentException>(() =>
            index.FindClosestPoint(new(double.PositiveInfinity, 0, 0), 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            index.FindClosestPoint(Vector3D.Zero, -1.0));
        Assert.Throws<ArgumentException>(() =>
            index.FindNearestRayHit(Vector3D.Zero, Vector3D.Zero, 1.0));
    }

    [Fact]
    public void RejectsOverLimitCountsBeforeIndexing()
    {
        var triangles = new CountOnlyTriangles(TriangleSpatialIndex.MaximumTriangleCount + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TriangleSpatialIndex.Build([], triangles));
        Assert.Equal(0, triangles.IndexerCalls);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TriangleSpatialIndex.Build(
                new CountOnlyPositions(TriangleSpatialIndex.MaximumControlPointCount + 1),
                []));
    }

    [Fact]
    public void HonorsCancellationDuringBuildAndQueries()
    {
        using var buildCancellation = new CancellationTokenSource();
        var triangles = new CancellingTriangles(buildCancellation);
        Assert.Throws<OperationCanceledException>(() =>
            TriangleSpatialIndex.Build(
                [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)],
                triangles,
                buildCancellation.Token));

        TriangleSpatialIndex index = TriangleSpatialIndex.Build(
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            [(0, 1, 2)]);
        using var queryCancellation = new CancellationTokenSource();
        queryCancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            index.FindClosestPoint(Vector3D.Zero, 1.0, queryCancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            index.FindNearestRayHit(Vector3D.Zero, Vector3D.UnitZ, 1.0, queryCancellation.Token));
    }

    [Fact]
    public void AcceptsSmallNonZeroRayDirectionsAndRejectsUnsupportedCoordinates()
    {
        TriangleSpatialIndex index = TriangleSpatialIndex.Build(
            [new(0, 0, 2), new(1, 0, 2), new(0, 1, 2)],
            [(0, 1, 2)]);

        TriangleRayHit? hit = index.FindNearestRayHit(
            Vector3D.Zero,
            new(0, 0, 1e-300),
            10.0);
        Assert.True(hit.HasValue);
        Assert.Equal(2.0, hit.Value.Distance, 10);

        TriangleSpatialIndex tinyIndex = TriangleSpatialIndex.Build(
            [new(0, 0, 0), new(1e-7, 0, 0), new(0, 1e-7, 0)],
            [(0, 1, 2)]);
        TriangleRayHit? tinyHit = tinyIndex.FindNearestRayHit(
            new(2e-8, 2e-8, 1),
            -Vector3D.UnitZ,
            10.0);
        Assert.True(tinyHit.HasValue);
        Assert.Equal(1.0, tinyHit.Value.Distance, 10);
        TriangleClosestPoint? tinyClosest = tinyIndex.FindClosestPoint(new(2e-8, 2e-8, 1), 10);
        Assert.True(tinyClosest.HasValue);
        Assert.True((tinyClosest.Value.Point - new Vector3D(2e-8, 2e-8, 0)).Length < 1e-18);

        Assert.Throws<ArgumentException>(() =>
            TriangleSpatialIndex.Build(
                [new(TriangleSpatialIndex.MaximumAbsoluteCoordinate * 2, 0, 0)],
                []));
    }

    [Fact]
    public void FindsAllHitsInDistanceOrdinalOrderWithoutDeduplicatingTriangles()
    {
        Vector3D[] positions =
        [
            new(-1, -1, 2), new(1, -1, 2), new(-1, 1, 2),
            new(-1, -1, 5), new(1, -1, 5), new(-1, 1, 5),
        ];
        TriangleSpatialIndex index = TriangleSpatialIndex.Build(
            positions,
            [(0, 1, 2), (3, 5, 4), (0, 2, 1)]);

        ImmutableArray<TriangleRayHit> hits = index.FindRayHits(
            Vector3D.Zero,
            new(0, 0, 2),
            10.0);

        Assert.Equal(3, hits.Length);
        Assert.Equal(0, hits[0].TriangleOrdinal);
        Assert.Equal(2, hits[1].TriangleOrdinal);
        Assert.Equal(1, hits[2].TriangleOrdinal);
        Assert.Equal(2.0, hits[0].Distance, 10);
        Assert.Equal(5.0, hits[2].Distance, 10);
        TriangleRayHit? nearest = index.FindNearestRayHit(
            Vector3D.Zero,
            new(0, 0, 2),
            10.0);
        Assert.True(nearest.HasValue);
        Assert.Equal(nearest.Value, hits[0]);
    }

    [Fact]
    public void EnforcesAllHitsDistanceAndHitBudgets()
    {
        TriangleSpatialIndex index = TriangleSpatialIndex.Build(
            [new(-1, -1, 2), new(1, -1, 2), new(-1, 1, 2)],
            [(0, 1, 2)]);

        Assert.Empty(index.FindRayHits(Vector3D.Zero, Vector3D.UnitZ, 1.0));
        Assert.Single(index.FindRayHits(Vector3D.Zero, Vector3D.UnitZ, 2.0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            index.FindRayHits(Vector3D.Zero, Vector3D.UnitZ, 2.0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            index.FindRayHits(Vector3D.Zero, Vector3D.UnitZ, 2.0, TriangleSpatialIndex.MaximumRayHits + 1));

        TriangleSpatialIndex duplicateIndex = TriangleSpatialIndex.Build(
            [new(-1, -1, 2), new(1, -1, 2), new(-1, 1, 2)],
            [(0, 1, 2), (0, 2, 1), (0, 1, 2)]);
        InvalidOperationException overflow = Assert.Throws<InvalidOperationException>(() =>
            duplicateIndex.FindRayHits(Vector3D.Zero, Vector3D.UnitZ, 2.0, 2));
        Assert.Contains("budget", overflow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllHitsHandlesEmptyInvalidAndCancelledQueries()
    {
        TriangleSpatialIndex empty = TriangleSpatialIndex.Build([], []);
        Assert.Empty(empty.FindRayHits(Vector3D.Zero, Vector3D.UnitZ, 10.0));
        Assert.Throws<ArgumentException>(() =>
            empty.FindRayHits(new(double.NaN, 0, 0), Vector3D.UnitZ, 10.0));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            empty.FindRayHits(Vector3D.Zero, Vector3D.UnitZ, 10.0, cancellationToken: cancellation.Token));
    }

    private static (int Ordinal, Vector3D Point, double DistanceSquared) ExhaustiveClosest(
        Vector3D query,
        Vector3D[] positions,
        (int A, int B, int C)[] triangles)
    {
        int bestOrdinal = -1;
        Vector3D bestPoint = default;
        double bestDistanceSquared = double.PositiveInfinity;
        for (int ordinal = 0; ordinal < triangles.Length; ordinal++)
        {
            (int a, int b, int c) = triangles[ordinal];
            Vector3D point = ExhaustiveClosestPointOnTriangle(
                query,
                positions[a],
                positions[b],
                positions[c]);
            double distanceSquared = (point - query).LengthSquared;
            if (distanceSquared < bestDistanceSquared ||
                (distanceSquared == bestDistanceSquared && ordinal < bestOrdinal))
            {
                bestOrdinal = ordinal;
                bestPoint = point;
                bestDistanceSquared = distanceSquared;
            }
        }

        return (bestOrdinal, bestPoint, bestDistanceSquared);
    }

    private static double ExhaustiveRayDistance(
        Vector3D origin,
        Vector3D direction,
        Vector3D[] positions,
        (int A, int B, int C)[] triangles)
    {
        Vector3D unit = direction.Normalized();
        double best = double.PositiveInfinity;
        for (int ordinal = 0; ordinal < triangles.Length; ordinal++)
        {
            (int a, int b, int c) = triangles[ordinal];
            Vector3D edgeA = positions[b] - positions[a];
            Vector3D edgeB = positions[c] - positions[a];
            Vector3D p = Vector3D.Cross(unit, edgeB);
            double determinant = Vector3D.Dot(edgeA, p);
            if (Math.Abs(determinant) <= 1e-12)
            {
                continue;
            }

            double inverse = 1.0 / determinant;
            Vector3D fromA = origin - positions[a];
            double bWeight = Vector3D.Dot(fromA, p) * inverse;
            Vector3D q = Vector3D.Cross(fromA, edgeA);
            double cWeight = Vector3D.Dot(unit, q) * inverse;
            double distance = Vector3D.Dot(edgeB, q) * inverse;
            if (bWeight >= 0 && cWeight >= 0 && bWeight + cWeight <= 1 && distance >= 0)
            {
                best = Math.Min(best, distance);
            }
        }

        return best;
    }

    private static Vector3D ExhaustiveClosestPointOnTriangle(
        Vector3D point,
        Vector3D a,
        Vector3D b,
        Vector3D c)
    {
        Vector3D ab = b - a;
        Vector3D ac = c - a;
        Vector3D normal = Vector3D.Cross(ab, ac);
        double normalSquared = normal.LengthSquared;
        if (normalSquared <= 1e-24)
        {
            return ClosestSegment(point, a, b);
        }

        double distanceToPlane = Vector3D.Dot(point - a, normal) / normalSquared;
        Vector3D projection = point - (normal * distanceToPlane);
        Vector3D v0 = b - a;
        Vector3D v1 = c - a;
        Vector3D v2 = projection - a;
        double d00 = Vector3D.Dot(v0, v0);
        double d01 = Vector3D.Dot(v0, v1);
        double d11 = Vector3D.Dot(v1, v1);
        double d20 = Vector3D.Dot(v2, v0);
        double d21 = Vector3D.Dot(v2, v1);
        double denominator = d00 * d11 - d01 * d01;
        double weightB = (d11 * d20 - d01 * d21) / denominator;
        double weightC = (d00 * d21 - d01 * d20) / denominator;
        if (weightB >= 0 && weightC >= 0 && weightB + weightC <= 1)
        {
            return projection;
        }

        Vector3D abPoint = ClosestSegment(point, a, b);
        Vector3D acPoint = ClosestSegment(point, a, c);
        Vector3D bcPoint = ClosestSegment(point, b, c);
        Vector3D best = abPoint;
        if ((acPoint - point).LengthSquared < (best - point).LengthSquared) best = acPoint;
        if ((bcPoint - point).LengthSquared < (best - point).LengthSquared) best = bcPoint;
        return best;
    }

    private static Vector3D ClosestSegment(Vector3D point, Vector3D a, Vector3D b)
    {
        Vector3D edge = b - a;
        double lengthSquared = edge.LengthSquared;
        double amount = lengthSquared == 0 ? 0 : Math.Clamp(Vector3D.Dot(point - a, edge) / lengthSquared, 0, 1);
        return a + (edge * amount);
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
                throw new InvalidOperationException();
            }
        }

        public IEnumerator<(int A, int B, int C)> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountOnlyPositions : IReadOnlyList<Vector3D>
    {
        public CountOnlyPositions(int count) => Count = count;
        public int Count { get; }
        public Vector3D this[int index] => throw new InvalidOperationException();
        public IEnumerator<Vector3D> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancellingTriangles : IReadOnlyList<(int A, int B, int C)>
    {
        private readonly CancellationTokenSource _source;
        public CancellingTriangles(CancellationTokenSource source) => _source = source;
        public int Count => 2;
        public (int A, int B, int C) this[int index]
        {
            get
            {
                _source.Cancel();
                return (0, 1, 2);
            }
        }

        public IEnumerator<(int A, int B, int C)> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
