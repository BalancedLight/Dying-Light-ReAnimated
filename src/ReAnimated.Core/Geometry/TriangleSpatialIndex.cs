using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Geometry;

/// <summary>
/// A bounded, CPU-only triangle index over source control-point positions.
/// Triangle ordinals are the original positions in the input triangle list.
/// </summary>
/// <remarks>
/// The index uses an iterative median-split AABB tree. Closest-point queries
/// include degenerate triangles as segments or points. Ray queries are
/// two-sided and consider only triangles with non-zero area; degenerate source
/// triangles remain available to closest-point queries. Ray distance is in
/// world units along a normalized copy of the supplied direction, independent
/// of that direction's original magnitude. Ties are resolved by lower source
/// triangle ordinal. Coordinates are finite and bounded to
/// <see cref="MaximumAbsoluteCoordinate"/> so intermediate squared distances
/// remain representable.
/// </remarks>
public sealed class TriangleSpatialIndex
{
    /// <summary>Maximum source control-point count accepted before allocation.</summary>
    public const int MaximumControlPointCount = 4_000_000;

    /// <summary>Maximum source triangle count accepted before allocation.</summary>
    public const int MaximumTriangleCount = 8_000_000;

    /// <summary>Maximum number of ray hits returned by one all-hits query.</summary>
    public const int MaximumRayHits = 4_096;

    /// <summary>
    /// Maximum absolute coordinate supported by the numerically bounded CPU
    /// queries. Larger finite values are rejected before indexing or querying.
    /// </summary>
    public const double MaximumAbsoluteCoordinate = 1e75;

    private const int LeafTriangleCount = 8;
    private const double DegenerateAreaTolerance = 1e-24;

    private readonly ImmutableArray<Vector3D> _positions;
    private readonly ImmutableArray<Triangle> _triangles;
    private readonly ImmutableArray<int> _triangleOrder;
    private readonly ImmutableArray<BvhNode> _nodes;

    private TriangleSpatialIndex(
        ImmutableArray<Vector3D> positions,
        ImmutableArray<Triangle> triangles,
        ImmutableArray<int> triangleOrder,
        ImmutableArray<BvhNode> nodes)
    {
        _positions = positions;
        _triangles = triangles;
        _triangleOrder = triangleOrder;
        _nodes = nodes;
    }

    public int ControlPointCount => _positions.Length;

    public int TriangleCount => _triangles.Length;

    public int NodeCount => _nodes.Length;

    /// <summary>
    /// Builds a deterministic AABB tree. Input positions and indexed triangles
    /// are copied so later caller mutations cannot change query results.
    /// </summary>
    public static TriangleSpatialIndex Build(
        IReadOnlyList<Vector3D> positions,
        IReadOnlyList<(int A, int B, int C)> triangles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(triangles);
        cancellationToken.ThrowIfCancellationRequested();

        int positionCount = positions.Count;
        if (positionCount < 0 || positionCount > MaximumControlPointCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(positions),
                positionCount,
                $"Triangle spatial indexing supports at most {MaximumControlPointCount:N0} control points.");
        }

        int triangleCount = triangles.Count;
        if (triangleCount < 0 || triangleCount > MaximumTriangleCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(triangles),
                triangleCount,
                $"Triangle spatial indexing supports at most {MaximumTriangleCount:N0} triangles.");
        }

        var positionArray = new Vector3D[positionCount];
        for (int index = 0; index < positionCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Vector3D position = positions[index];
            if (!IsSupportedCoordinate(position))
            {
                throw new ArgumentException(
                    $"Source control point {index} must have finite coordinates " +
                    $"within +/-{MaximumAbsoluteCoordinate:E1}.",
                    nameof(positions));
            }

            positionArray[index] = position;
        }

        var sourceTriangles = new Triangle[triangleCount];
        for (int ordinal = 0; ordinal < triangleCount; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int a, int b, int c) = triangles[ordinal];
            ValidateIndex(a, positionCount, ordinal, nameof(triangles));
            ValidateIndex(b, positionCount, ordinal, nameof(triangles));
            ValidateIndex(c, positionCount, ordinal, nameof(triangles));
            sourceTriangles[ordinal] = new Triangle(
                ordinal,
                a,
                b,
                c,
                Bounds.FromTriangle(positionArray[a], positionArray[b], positionArray[c]));
        }

        var order = new int[triangleCount];
        for (int ordinal = 0; ordinal < triangleCount; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order[ordinal] = ordinal;
        }

        List<BvhNode> nodes = BuildNodes(sourceTriangles, order, cancellationToken);
        return new TriangleSpatialIndex(
            ImmutableArray.CreateRange(positionArray),
            ImmutableArray.CreateRange(sourceTriangles),
            ImmutableArray.CreateRange(order),
            ImmutableArray.CreateRange(nodes));
    }

    /// <summary>
    /// Finds the closest point on any indexed triangle within
    /// <paramref name="maximumDistance"/>. A positive infinity bound is
    /// allowed. Returns null when no finite candidate is within the bound.
    /// </summary>
    public TriangleClosestPoint? FindClosestPoint(
        Vector3D point,
        double maximumDistance,
        CancellationToken cancellationToken = default)
    {
        ValidatePoint(point, nameof(point));
        ValidateDistance(maximumDistance, nameof(maximumDistance));
        cancellationToken.ThrowIfCancellationRequested();
        if (_nodes.IsEmpty)
        {
            return null;
        }

        double bestDistanceSquared = maximumDistance == double.PositiveInfinity
            ? double.PositiveInfinity
            : maximumDistance * maximumDistance;
        TriangleClosestPoint? best = null;
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BvhNode node = _nodes[pending.Pop()];
            if (node.Bounds.DistanceSquared(point) > bestDistanceSquared)
            {
                continue;
            }

            if (node.IsLeaf)
            {
                for (int offset = 0; offset < node.Count; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Triangle triangle = _triangles[_triangleOrder[node.Start + offset]];
                    ClosestCandidate candidate = ClosestPointOnTriangle(
                        point,
                        _positions[triangle.A],
                        _positions[triangle.B],
                        _positions[triangle.C]);
                    if (!double.IsFinite(candidate.DistanceSquared) ||
                        candidate.DistanceSquared > bestDistanceSquared ||
                        (best is not null &&
                         candidate.DistanceSquared == bestDistanceSquared &&
                         triangle.Ordinal > best.Value.TriangleOrdinal))
                    {
                        continue;
                    }

                    if (best is null ||
                        candidate.DistanceSquared < best.Value.DistanceSquared ||
                        (candidate.DistanceSquared == best.Value.DistanceSquared &&
                         triangle.Ordinal < best.Value.TriangleOrdinal))
                    {
                        bestDistanceSquared = candidate.DistanceSquared;
                        best = new TriangleClosestPoint(
                            triangle.Ordinal,
                            candidate.Point,
                            Math.Sqrt(candidate.DistanceSquared),
                            candidate.DistanceSquared,
                            candidate.BarycentricA,
                            candidate.BarycentricB,
                            candidate.BarycentricC);
                    }
                }

                continue;
            }

            PushChildrenByDistance(node, point, pending);
        }

        return best;
    }

    /// <summary>
    /// Finds the nearest two-sided ray hit within the supplied distance bound.
    /// The direction is normalized internally; zero and non-finite directions
    /// are rejected. Degenerate triangles are skipped for ray intersection.
    /// </summary>
    public TriangleRayHit? FindNearestRayHit(
        Vector3D origin,
        Vector3D direction,
        double maximumDistance,
        CancellationToken cancellationToken = default)
    {
        ValidatePoint(origin, nameof(origin));
        ValidatePoint(direction, nameof(direction));
        ValidateDistance(maximumDistance, nameof(maximumDistance));
        if (!TryNormalizeRayDirection(direction, out Vector3D unitDirection))
        {
            throw new ArgumentException("Ray direction must be non-zero.", nameof(direction));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_nodes.IsEmpty)
        {
            return null;
        }

        double bestDistance = maximumDistance;
        TriangleRayHit? best = null;
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BvhNode node = _nodes[pending.Pop()];
            double nodeEntry = node.Bounds.RayEntryDistance(origin, unitDirection, bestDistance);
            if (nodeEntry > bestDistance)
            {
                continue;
            }

            if (node.IsLeaf)
            {
                for (int offset = 0; offset < node.Count; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Triangle triangle = _triangles[_triangleOrder[node.Start + offset]];
                    if (!TryIntersectTriangle(
                            origin,
                            unitDirection,
                            _positions[triangle.A],
                            _positions[triangle.B],
                            _positions[triangle.C],
                            bestDistance,
                            out double distance,
                            out double barycentricA,
                            out double barycentricB,
                            out double barycentricC))
                    {
                        continue;
                    }

                    if (best is null ||
                        distance < best.Value.Distance ||
                        (distance == best.Value.Distance &&
                         triangle.Ordinal < best.Value.TriangleOrdinal))
                    {
                        bestDistance = distance;
                        best = new TriangleRayHit(
                            triangle.Ordinal,
                            origin + (unitDirection * distance),
                            distance,
                            barycentricA,
                            barycentricB,
                            barycentricC);
                    }
                }

                continue;
            }

            PushChildrenByRayEntry(node, origin, unitDirection, bestDistance, pending);
        }

        return best;
    }

    /// <summary>
    /// Finds every two-sided ray hit within the supplied distance bound. The
    /// normalized direction and world-unit distance semantics match
    /// <see cref="FindNearestRayHit"/>. Hits retain duplicate coplanar and
    /// shared-edge triangle identities and are ordered by distance, then by
    /// original triangle ordinal. Exceeding <paramref name="maximumHits"/>
    /// throws rather than truncating the result.
    /// </summary>
    public ImmutableArray<TriangleRayHit> FindRayHits(
        Vector3D origin,
        Vector3D direction,
        double maximumDistance,
        int maximumHits = MaximumRayHits,
        CancellationToken cancellationToken = default)
    {
        ValidatePoint(origin, nameof(origin));
        ValidatePoint(direction, nameof(direction));
        ValidateDistance(maximumDistance, nameof(maximumDistance));
        if (maximumHits <= 0 || maximumHits > MaximumRayHits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumHits),
                maximumHits,
                $"The ray hit budget must be between 1 and {MaximumRayHits:N0}.");
        }

        if (!TryNormalizeRayDirection(direction, out Vector3D unitDirection))
        {
            throw new ArgumentException("Ray direction must be non-zero.", nameof(direction));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_nodes.IsEmpty)
        {
            return [];
        }

        var hits = new List<TriangleRayHit>(Math.Min(maximumHits, _triangles.Length));
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BvhNode node = _nodes[pending.Pop()];
            double nodeEntry = node.Bounds.RayEntryDistance(origin, unitDirection, maximumDistance);
            if (nodeEntry > maximumDistance)
            {
                continue;
            }

            if (node.IsLeaf)
            {
                for (int offset = 0; offset < node.Count; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Triangle triangle = _triangles[_triangleOrder[node.Start + offset]];
                    if (!TryIntersectTriangle(
                            origin,
                            unitDirection,
                            _positions[triangle.A],
                            _positions[triangle.B],
                            _positions[triangle.C],
                            maximumDistance,
                            out double distance,
                            out double barycentricA,
                            out double barycentricB,
                            out double barycentricC))
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (hits.Count >= maximumHits)
                    {
                        throw new InvalidOperationException(
                            $"Ray hit count exceeded the configured budget of {maximumHits:N0}; " +
                            "increase the bound or narrow the query.");
                    }

                    hits.Add(new TriangleRayHit(
                        triangle.Ordinal,
                        origin + (unitDirection * distance),
                        distance,
                        barycentricA,
                        barycentricB,
                        barycentricC));
                }

                continue;
            }

            PushChildrenByRayEntry(node, origin, unitDirection, maximumDistance, pending);
        }

        SortRayHits(hits, cancellationToken);
        return ImmutableArray.CreateRange(hits);
    }

    public bool TryFindClosestPoint(
        Vector3D point,
        double maximumDistance,
        out TriangleClosestPoint result,
        CancellationToken cancellationToken = default)
    {
        TriangleClosestPoint? candidate = FindClosestPoint(point, maximumDistance, cancellationToken);
        if (candidate is null)
        {
            result = default;
            return false;
        }

        result = candidate.Value;
        return true;
    }

    public bool TryFindNearestRayHit(
        Vector3D origin,
        Vector3D direction,
        double maximumDistance,
        out TriangleRayHit result,
        CancellationToken cancellationToken = default)
    {
        TriangleRayHit? candidate = FindNearestRayHit(
            origin,
            direction,
            maximumDistance,
            cancellationToken);
        if (candidate is null)
        {
            result = default;
            return false;
        }

        result = candidate.Value;
        return true;
    }

    private static void ValidateIndex(
        int index,
        int positionCount,
        int triangleOrdinal,
        string parameterName)
    {
        if ((uint)index >= (uint)positionCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                index,
                $"Triangle {triangleOrdinal} references control point {index}, " +
                $"but the valid range is 0 through {positionCount - 1}.");
        }
    }

    private static void ValidatePoint(Vector3D point, string parameterName)
    {
        if (!IsSupportedCoordinate(point))
        {
            throw new ArgumentException(
                $"The query point must have finite coordinates within +/-{MaximumAbsoluteCoordinate:E1}.",
                parameterName);
        }
    }

    private static bool IsSupportedCoordinate(Vector3D value) =>
        value.IsFinite &&
        Math.Abs(value.X) <= MaximumAbsoluteCoordinate &&
        Math.Abs(value.Y) <= MaximumAbsoluteCoordinate &&
        Math.Abs(value.Z) <= MaximumAbsoluteCoordinate;

    private static bool TryNormalizeRayDirection(Vector3D direction, out Vector3D normalized)
    {
        double maximumComponent = Math.Max(
            Math.Abs(direction.X),
            Math.Max(Math.Abs(direction.Y), Math.Abs(direction.Z)));
        if (maximumComponent == 0.0 || !double.IsFinite(maximumComponent))
        {
            normalized = Vector3D.Zero;
            return false;
        }

        Vector3D scaled = direction / maximumComponent;
        double scaledLength = scaled.Length;
        if (!double.IsFinite(scaledLength) || scaledLength == 0.0)
        {
            normalized = Vector3D.Zero;
            return false;
        }

        normalized = scaled / scaledLength;
        return normalized.IsFinite;
    }

    private static void ValidateDistance(double distance, string parameterName)
    {
        if (double.IsNaN(distance) || distance < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                distance,
                "The distance bound must be non-negative or positive infinity.");
        }
    }

    private static void SortRayHits(List<TriangleRayHit> hits, CancellationToken cancellationToken)
    {
        try
        {
            hits.Sort((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int distanceComparison = left.Distance.CompareTo(right.Distance);
                return distanceComparison != 0
                    ? distanceComparison
                    : left.TriangleOrdinal.CompareTo(right.TriangleOrdinal);
            });
        }
        catch (InvalidOperationException exception)
            when (cancellationToken.IsCancellationRequested &&
                  exception.InnerException is OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static List<BvhNode> BuildNodes(
        Triangle[] triangles,
        int[] order,
        CancellationToken cancellationToken)
    {
        var nodes = new List<BvhNode>(triangles.Length == 0 ? 0 : Math.Min(triangles.Length * 2, 1_048_576));
        if (triangles.Length == 0)
        {
            return nodes;
        }

        nodes.Add(default);
        var pending = new Stack<BuildWork>();
        pending.Push(new BuildWork(0, triangles.Length, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildWork work = pending.Pop();
            Bounds bounds = ComputeBounds(triangles, order, work.Start, work.Count, cancellationToken);
            if (work.Count <= LeafTriangleCount)
            {
                nodes[work.NodeIndex] = new BvhNode(bounds, work.Start, work.Count, -1, -1);
                continue;
            }

            int axis = bounds.LongestAxis;
            try
            {
                Array.Sort(
                    order,
                    work.Start,
                    work.Count,
                    Comparer<int>.Create((left, right) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int comparison = Component(triangles[left].Centroid, axis).CompareTo(
                            Component(triangles[right].Centroid, axis));
                        return comparison != 0
                            ? comparison
                            : triangles[left].Ordinal.CompareTo(triangles[right].Ordinal);
                    }));
            }
            catch (InvalidOperationException exception)
                when (cancellationToken.IsCancellationRequested &&
                      exception.InnerException is OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            int leftCount = work.Count / 2;
            int rightStart = work.Start + leftCount;
            int leftNodeIndex = nodes.Count;
            nodes.Add(default);
            int rightNodeIndex = nodes.Count;
            nodes.Add(default);
            nodes[work.NodeIndex] = new BvhNode(
                bounds,
                work.Start,
                work.Count,
                leftNodeIndex,
                rightNodeIndex);

            pending.Push(new BuildWork(rightStart, work.Count - leftCount, rightNodeIndex));
            pending.Push(new BuildWork(work.Start, leftCount, leftNodeIndex));
        }

        return nodes;
    }

    private static double Component(Vector3D value, int axis) =>
        axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z,
        };

    private static Bounds ComputeBounds(
        Triangle[] triangles,
        int[] order,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        Bounds bounds = triangles[order[start]].Bounds;
        for (int offset = 1; offset < count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bounds = Bounds.Union(bounds, triangles[order[start + offset]].Bounds);
        }

        return bounds;
    }

    private void PushChildrenByDistance(BvhNode node, Vector3D point, Stack<int> pending)
    {
        BvhNode left = _nodes[node.Left];
        BvhNode right = _nodes[node.Right];
        double leftDistance = left.Bounds.DistanceSquared(point);
        double rightDistance = right.Bounds.DistanceSquared(point);
        if (leftDistance <= rightDistance)
        {
            pending.Push(node.Right);
            pending.Push(node.Left);
        }
        else
        {
            pending.Push(node.Left);
            pending.Push(node.Right);
        }
    }

    private void PushChildrenByRayEntry(
        BvhNode node,
        Vector3D origin,
        Vector3D direction,
        double maximumDistance,
        Stack<int> pending)
    {
        BvhNode left = _nodes[node.Left];
        BvhNode right = _nodes[node.Right];
        double leftEntry = left.Bounds.RayEntryDistance(origin, direction, maximumDistance);
        double rightEntry = right.Bounds.RayEntryDistance(origin, direction, maximumDistance);
        if (leftEntry <= rightEntry)
        {
            if (rightEntry <= maximumDistance) pending.Push(node.Right);
            if (leftEntry <= maximumDistance) pending.Push(node.Left);
        }
        else
        {
            if (leftEntry <= maximumDistance) pending.Push(node.Left);
            if (rightEntry <= maximumDistance) pending.Push(node.Right);
        }
    }

    private static ClosestCandidate ClosestPointOnTriangle(
        Vector3D point,
        Vector3D a,
        Vector3D b,
        Vector3D c)
    {
        Vector3D ab = b - a;
        Vector3D ac = c - a;
        Vector3D bc = c - b;
        double edgeScale = MaximumAbsComponent(ab, ac, bc);
        if (edgeScale == 0.0)
        {
            ClosestCandidate best = ClosestPointOnSegment(point, a, b, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0);
            ClosestCandidate candidate = ClosestPointOnSegment(point, a, c, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0);
            best = ChooseClosest(best, candidate);
            candidate = ClosestPointOnSegment(point, b, c, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);
            return ChooseClosest(best, candidate);
        }

        Vector3D scaledAb = ab / edgeScale;
        Vector3D scaledAc = ac / edgeScale;
        double normalSquared = Vector3D.Cross(scaledAb, scaledAc).LengthSquared;
        if (normalSquared <= DegenerateAreaTolerance)
        {
            ClosestCandidate best = ClosestPointOnSegment(point, a, b, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0);
            ClosestCandidate candidate = ClosestPointOnSegment(point, a, c, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0);
            best = ChooseClosest(best, candidate);
            candidate = ClosestPointOnSegment(point, b, c, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);
            return ChooseClosest(best, candidate);
        }

        Vector3D ap = (point - a) / edgeScale;
        double d1 = Vector3D.Dot(scaledAb, ap);
        double d2 = Vector3D.Dot(scaledAc, ap);
        if (d1 <= 0.0 && d2 <= 0.0)
        {
            return Candidate(a, point, 1.0, 0.0, 0.0);
        }

        Vector3D bp = (point - b) / edgeScale;
        double d3 = Vector3D.Dot(scaledAb, bp);
        double d4 = Vector3D.Dot(scaledAc, bp);
        if (d3 >= 0.0 && d4 <= d3)
        {
            return Candidate(b, point, 0.0, 1.0, 0.0);
        }

        double vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
        {
            double amount = d1 / (d1 - d3);
            return Candidate(a + (ab * amount), point, 1.0 - amount, amount, 0.0);
        }

        Vector3D cp = (point - c) / edgeScale;
        double d5 = Vector3D.Dot(scaledAb, cp);
        double d6 = Vector3D.Dot(scaledAc, cp);
        if (d6 >= 0.0 && d5 <= d6)
        {
            return Candidate(c, point, 0.0, 0.0, 1.0);
        }

        double vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
        {
            double amount = d2 / (d2 - d6);
            return Candidate(a + (ac * amount), point, 1.0 - amount, 0.0, amount);
        }

        double va = (d3 * d6) - (d5 * d4);
        if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0)
        {
            double amount = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return Candidate(b + (bc * amount), point, 0.0, 1.0 - amount, amount);
        }

        double denominator = 1.0 / (va + vb + vc);
        double barycentricB = vb * denominator;
        double barycentricC = vc * denominator;
        return Candidate(
            a + (ab * barycentricB) + (ac * barycentricC),
            point,
            1.0 - barycentricB - barycentricC,
            barycentricB,
            barycentricC);
    }

    private static ClosestCandidate ClosestPointOnSegment(
        Vector3D point,
        Vector3D start,
        Vector3D end,
        double startA,
        double startB,
        double startC,
        double endA,
        double endB,
        double endC)
    {
        Vector3D segment = end - start;
        double edgeScale = MaximumAbsComponent(segment);
        Vector3D scaledSegment = edgeScale == 0.0 ? Vector3D.Zero : segment / edgeScale;
        Vector3D scaledToPoint = edgeScale == 0.0 ? Vector3D.Zero : (point - start) / edgeScale;
        double lengthSquared = scaledSegment.LengthSquared;
        double amount = lengthSquared <= 0.0
            ? 0.0
            : Math.Clamp(Vector3D.Dot(scaledToPoint, scaledSegment) / lengthSquared, 0.0, 1.0);
        return Candidate(
            start + (segment * amount),
            point,
            startA + ((endA - startA) * amount),
            startB + ((endB - startB) * amount),
            startC + ((endC - startC) * amount));
    }

    private static ClosestCandidate ChooseClosest(
        ClosestCandidate first,
        ClosestCandidate second) =>
        second.DistanceSquared < first.DistanceSquared ? second : first;

    private static ClosestCandidate Candidate(
        Vector3D point,
        Vector3D queryPoint,
        double barycentricA,
        double barycentricB,
        double barycentricC) =>
        new(
            point,
            (point - queryPoint).LengthSquared,
            barycentricA,
            barycentricB,
            barycentricC);

    private static double MaximumAbsComponent(Vector3D value)
    {
        return Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
    }

    private static double MaximumAbsComponent(Vector3D first, Vector3D second)
    {
        return Math.Max(MaximumAbsComponent(first), MaximumAbsComponent(second));
    }

    private static double MaximumAbsComponent(Vector3D first, Vector3D second, Vector3D third)
    {
        return Math.Max(MaximumAbsComponent(first, second), MaximumAbsComponent(third));
    }

    private static bool TryIntersectTriangle(
        Vector3D origin,
        Vector3D direction,
        Vector3D a,
        Vector3D b,
        Vector3D c,
        double maximumDistance,
        out double distance,
        out double barycentricA,
        out double barycentricB,
        out double barycentricC)
    {
        Vector3D ab = b - a;
        Vector3D ac = c - a;
        double edgeScale = MaximumAbsComponent(ab, ac);
        if (edgeScale == 0.0)
        {
            distance = 0.0;
            barycentricA = 0.0;
            barycentricB = 0.0;
            barycentricC = 0.0;
            return false;
        }

        Vector3D scaledAb = ab / edgeScale;
        Vector3D scaledAc = ac / edgeScale;
        Vector3D pVector = Vector3D.Cross(direction, scaledAc);
        double determinant = Vector3D.Dot(scaledAb, pVector);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) <= DegenerateAreaTolerance)
        {
            distance = 0.0;
            barycentricA = 0.0;
            barycentricB = 0.0;
            barycentricC = 0.0;
            return false;
        }

        double inverseDeterminant = 1.0 / determinant;
        Vector3D fromA = (origin - a) / edgeScale;
        double baryB = Vector3D.Dot(fromA, pVector) * inverseDeterminant;
        if (baryB < 0.0 || baryB > 1.0)
        {
            distance = 0.0;
            barycentricA = 0.0;
            barycentricB = 0.0;
            barycentricC = 0.0;
            return false;
        }

        Vector3D qVector = Vector3D.Cross(fromA, scaledAb);
        double baryC = Vector3D.Dot(direction, qVector) * inverseDeterminant;
        if (baryC < 0.0 || baryB + baryC > 1.0)
        {
            distance = 0.0;
            barycentricA = 0.0;
            barycentricB = 0.0;
            barycentricC = 0.0;
            return false;
        }

        double candidateDistance = edgeScale * Vector3D.Dot(scaledAc, qVector) * inverseDeterminant;
        if (!double.IsFinite(candidateDistance) ||
            candidateDistance < 0.0 ||
            candidateDistance > maximumDistance)
        {
            distance = 0.0;
            barycentricA = 0.0;
            barycentricB = 0.0;
            barycentricC = 0.0;
            return false;
        }

        distance = candidateDistance;
        barycentricA = 1.0 - baryB - baryC;
        barycentricB = baryB;
        barycentricC = baryC;
        return true;
    }

    private readonly record struct Triangle(
        int Ordinal,
        int A,
        int B,
        int C,
        Bounds Bounds)
    {
        public Vector3D Centroid =>
            (Bounds.Min + Bounds.Max) / 2.0;
    }

    private readonly record struct BuildWork(int Start, int Count, int NodeIndex);

    private readonly record struct BvhNode(
        Bounds Bounds,
        int Start,
        int Count,
        int Left,
        int Right)
    {
        public bool IsLeaf => Left < 0;
    }

    private readonly record struct ClosestCandidate(
        Vector3D Point,
        double DistanceSquared,
        double BarycentricA,
        double BarycentricB,
        double BarycentricC);

    private readonly record struct Bounds(Vector3D Min, Vector3D Max)
    {
        public static Bounds FromTriangle(Vector3D a, Vector3D b, Vector3D c) =>
            new(
                new Vector3D(
                    Math.Min(a.X, Math.Min(b.X, c.X)),
                    Math.Min(a.Y, Math.Min(b.Y, c.Y)),
                    Math.Min(a.Z, Math.Min(b.Z, c.Z))),
                new Vector3D(
                    Math.Max(a.X, Math.Max(b.X, c.X)),
                    Math.Max(a.Y, Math.Max(b.Y, c.Y)),
                    Math.Max(a.Z, Math.Max(b.Z, c.Z))));

        public static Bounds Union(Bounds left, Bounds right) =>
            new(
                new Vector3D(
                    Math.Min(left.Min.X, right.Min.X),
                    Math.Min(left.Min.Y, right.Min.Y),
                    Math.Min(left.Min.Z, right.Min.Z)),
                new Vector3D(
                    Math.Max(left.Max.X, right.Max.X),
                    Math.Max(left.Max.Y, right.Max.Y),
                    Math.Max(left.Max.Z, right.Max.Z)));

        public int LongestAxis
        {
            get
            {
                Vector3D extent = Max - Min;
                return extent.Y > extent.X && extent.Y >= extent.Z
                    ? 1
                    : extent.Z > extent.X && extent.Z > extent.Y
                        ? 2
                        : 0;
            }
        }

        public double DistanceSquared(Vector3D point)
        {
            double x = point.X < Min.X ? Min.X - point.X : point.X > Max.X ? point.X - Max.X : 0.0;
            double y = point.Y < Min.Y ? Min.Y - point.Y : point.Y > Max.Y ? point.Y - Max.Y : 0.0;
            double z = point.Z < Min.Z ? Min.Z - point.Z : point.Z > Max.Z ? point.Z - Max.Z : 0.0;
            return (x * x) + (y * y) + (z * z);
        }

        public double RayEntryDistance(Vector3D origin, Vector3D direction, double maximumDistance)
        {
            double minimum = 0.0;
            double maximum = maximumDistance;
            if (!UpdateSlab(origin.X, direction.X, Min.X, Max.X, ref minimum, ref maximum) ||
                !UpdateSlab(origin.Y, direction.Y, Min.Y, Max.Y, ref minimum, ref maximum) ||
                !UpdateSlab(origin.Z, direction.Z, Min.Z, Max.Z, ref minimum, ref maximum))
            {
                return double.PositiveInfinity;
            }

            return minimum;
        }

        private static bool UpdateSlab(
            double origin,
            double direction,
            double minimumBound,
            double maximumBound,
            ref double minimum,
            ref double maximum)
        {
            if (direction == 0.0)
            {
                return origin >= minimumBound && origin <= maximumBound;
            }

            double inverseDirection = 1.0 / direction;
            double near = (minimumBound - origin) / direction;
            double far = (maximumBound - origin) / direction;
            if (near > far)
            {
                (near, far) = (far, near);
            }

            minimum = Math.Max(minimum, near);
            maximum = Math.Min(maximum, far);
            return minimum <= maximum;
        }
    }
}

/// <summary>Closest point query result with source triangle identity.</summary>
public readonly record struct TriangleClosestPoint(
    int TriangleOrdinal,
    Vector3D Point,
    double Distance,
    double DistanceSquared,
    double BarycentricA,
    double BarycentricB,
    double BarycentricC)
{
    public int TriangleIndex => TriangleOrdinal;
}

/// <summary>Nearest two-sided ray hit with source triangle identity.</summary>
public readonly record struct TriangleRayHit(
    int TriangleOrdinal,
    Vector3D Point,
    double Distance,
    double BarycentricA,
    double BarycentricB,
    double BarycentricC)
{
    public int TriangleIndex => TriangleOrdinal;
}
