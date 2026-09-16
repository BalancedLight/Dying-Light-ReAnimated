using System.Collections.Immutable;

namespace ReAnimated.Core.Geometry;

/// <summary>
/// Computes source-index topology for a triangle mesh. The utility deliberately
/// uses only control-point IDs and triangle winding; it does not weld points,
/// repair input, or make any claim about geometry or autorig readiness.
/// </summary>
public static class SourceMeshTopology
{
    /// <summary>
    /// Maximum number of source control points accepted by the bounded
    /// topology pass. This generous fixed limit prevents hostile counts from
    /// causing allocations before validation.
    /// </summary>
    public const int MaximumControlPointCount = 4_000_000;

    /// <summary>
    /// Maximum number of source triangles accepted by the bounded topology
    /// pass. This generous fixed limit prevents hostile counts from causing
    /// allocations before validation.
    /// </summary>
    public const int MaximumTriangleCount = 8_000_000;

    /// <summary>
    /// Builds deterministic connected islands and edge diagnostics from source
    /// control-point indices.
    /// </summary>
    public static SourceMeshTopologyResult Build(
        int controlPointCount,
        IReadOnlyList<(int A, int B, int C)> triangles,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(controlPointCount);
        if (controlPointCount > MaximumControlPointCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(controlPointCount),
                controlPointCount,
                $"Source topology supports at most {MaximumControlPointCount:N0} control points.");
        }

        ArgumentNullException.ThrowIfNull(triangles);
        cancellationToken.ThrowIfCancellationRequested();

        int triangleCount = triangles.Count;
        if (triangleCount < 0 || triangleCount > MaximumTriangleCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(triangles),
                triangleCount,
                $"Source topology supports at most {MaximumTriangleCount:N0} triangles.");
        }

        var sourceTriangles = new Triangle[triangleCount];
        var trianglesByControlPoint = new List<int>?[controlPointCount];
        for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int a, int b, int c) = triangles[triangleIndex];
            ValidateControlPoint(a, controlPointCount, triangleIndex, nameof(triangles));
            ValidateControlPoint(b, controlPointCount, triangleIndex, nameof(triangles));
            ValidateControlPoint(c, controlPointCount, triangleIndex, nameof(triangles));
            if (a == b || a == c || b == c)
            {
                throw new ArgumentException(
                    $"Triangle {triangleIndex} repeats a source control-point corner.",
                    nameof(triangles));
            }

            sourceTriangles[triangleIndex] = new Triangle(a, b, c);
            AddTriangleToControlPoint(a, triangleIndex, trianglesByControlPoint);
            AddTriangleToControlPoint(b, triangleIndex, trianglesByControlPoint);
            AddTriangleToControlPoint(c, triangleIndex, trianglesByControlPoint);
        }

        ImmutableArray<SourceMeshTopologyIsland> islands =
            BuildIslands(sourceTriangles, trianglesByControlPoint, cancellationToken);
        ImmutableArray<int> isolatedControlPointIds =
            BuildIsolatedControlPoints(trianglesByControlPoint, cancellationToken);

        Dictionary<EdgeKey, EdgeBuilder> edgeBuilders =
            BuildEdges(sourceTriangles, cancellationToken);
        var edges = new List<SourceMeshTopologyEdge>(edgeBuilders.Count);
        foreach (KeyValuePair<EdgeKey, EdgeBuilder> pair in edgeBuilders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            edges.Add(pair.Value.Create(pair.Key));
        }

        edges.Sort(static (left, right) =>
        {
            int aComparison = left.SourceControlPointA.CompareTo(right.SourceControlPointA);
            return aComparison != 0
                ? aComparison
                : left.SourceControlPointB.CompareTo(right.SourceControlPointB);
        });

        var boundaryEdges = new List<SourceMeshTopologyEdge>();
        var nonManifoldEdges = new List<SourceMeshTopologyEdge>();
        var windingConflictEdges = new List<SourceMeshTopologyEdge>();
        foreach (SourceMeshTopologyEdge edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edge.Incidents.Length == 1)
            {
                boundaryEdges.Add(edge);
            }

            if (edge.Incidents.Length > 2)
            {
                nonManifoldEdges.Add(edge);
            }

            if (edge.HasWindingConflict)
            {
                windingConflictEdges.Add(edge);
            }
        }

        return new SourceMeshTopologyResult(
            islands,
            isolatedControlPointIds,
            ImmutableArray.CreateRange(edges),
            ImmutableArray.CreateRange(boundaryEdges),
            ImmutableArray.CreateRange(nonManifoldEdges),
            ImmutableArray.CreateRange(windingConflictEdges));
    }

    private static void ValidateControlPoint(
        int controlPoint,
        int controlPointCount,
        int triangleIndex,
        string parameterName)
    {
        if ((uint)controlPoint >= (uint)controlPointCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                controlPoint,
                $"Triangle {triangleIndex} references source control point {controlPoint}, " +
                $"but the valid range is 0 through {controlPointCount - 1}.");
        }
    }

    private static void AddTriangleToControlPoint(
        int controlPoint,
        int triangleIndex,
        List<int>?[] trianglesByControlPoint)
    {
        (trianglesByControlPoint[controlPoint] ??= new List<int>(1)).Add(triangleIndex);
    }

    private static ImmutableArray<SourceMeshTopologyIsland> BuildIslands(
        Triangle[] triangles,
        List<int>?[] trianglesByControlPoint,
        CancellationToken cancellationToken)
    {
        var visited = new bool[triangles.Length];
        var expandedControlPoints = new bool[trianglesByControlPoint.Length];
        var islands = new List<SourceMeshTopologyIsland>();
        var queue = new Queue<int>();

        for (int seed = 0; seed < triangles.Length; seed++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited[seed])
            {
                continue;
            }

            visited[seed] = true;
            queue.Enqueue(seed);
            var triangleIndices = new List<int>();
            var controlPointIds = new HashSet<int>();
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int triangleIndex = queue.Dequeue();
                Triangle triangle = triangles[triangleIndex];
                triangleIndices.Add(triangleIndex);
                controlPointIds.Add(triangle.A);
                controlPointIds.Add(triangle.B);
                controlPointIds.Add(triangle.C);

                EnqueueAdjacent(triangle.A);
                EnqueueAdjacent(triangle.B);
                EnqueueAdjacent(triangle.C);

                void EnqueueAdjacent(int controlPoint)
                {
                    if (expandedControlPoints[controlPoint])
                    {
                        return;
                    }

                    expandedControlPoints[controlPoint] = true;
                    List<int> adjacent = trianglesByControlPoint[controlPoint]!;
                    for (int adjacentIndex = 0; adjacentIndex < adjacent.Count; adjacentIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int adjacentTriangle = adjacent[adjacentIndex];
                        if (!visited[adjacentTriangle])
                        {
                            visited[adjacentTriangle] = true;
                            queue.Enqueue(adjacentTriangle);
                        }
                    }
                }
            }

            triangleIndices.Sort();
            var sortedControlPointIds = controlPointIds.ToList();
            sortedControlPointIds.Sort();
            islands.Add(new SourceMeshTopologyIsland(
                ImmutableArray.CreateRange(sortedControlPointIds),
                ImmutableArray.CreateRange(triangleIndices)));
        }

        return ImmutableArray.CreateRange(islands);
    }

    private static ImmutableArray<int> BuildIsolatedControlPoints(
        List<int>?[] trianglesByControlPoint,
        CancellationToken cancellationToken)
    {
        var isolated = new List<int>();
        for (int controlPoint = 0; controlPoint < trianglesByControlPoint.Length; controlPoint++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trianglesByControlPoint[controlPoint] is null)
            {
                isolated.Add(controlPoint);
            }
        }

        return ImmutableArray.CreateRange(isolated);
    }

    private static Dictionary<EdgeKey, EdgeBuilder> BuildEdges(
        Triangle[] triangles,
        CancellationToken cancellationToken)
    {
        var edges = new Dictionary<EdgeKey, EdgeBuilder>();
        for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Triangle triangle = triangles[triangleIndex];
            AddEdge(triangle.A, triangle.B, triangleIndex);
            AddEdge(triangle.B, triangle.C, triangleIndex);
            AddEdge(triangle.C, triangle.A, triangleIndex);

            void AddEdge(int from, int to, int sourceTriangleIndex)
            {
                int a;
                int b;
                bool isForward;
                if (from < to)
                {
                    a = from;
                    b = to;
                    isForward = true;
                }
                else
                {
                    a = to;
                    b = from;
                    isForward = false;
                }

                var key = new EdgeKey(a, b);
                if (!edges.TryGetValue(key, out EdgeBuilder? builder))
                {
                    builder = new EdgeBuilder();
                    edges.Add(key, builder);
                }

                builder.Add(sourceTriangleIndex, isForward);
            }
        }

        return edges;
    }

    private readonly record struct Triangle(int A, int B, int C);

    private readonly record struct EdgeKey(int A, int B);

    private sealed class EdgeBuilder
    {
        private readonly List<SourceMeshTopologyEdgeIncident> _incidents = [];

        public void Add(int triangleIndex, bool isForward) =>
            _incidents.Add(new SourceMeshTopologyEdgeIncident(triangleIndex, isForward));

        public SourceMeshTopologyEdge Create(EdgeKey key) =>
            new(key.A, key.B, ImmutableArray.CreateRange(_incidents));
    }
}

/// <summary>
/// Immutable topology observations for a source triangle mesh.
/// </summary>
public sealed class SourceMeshTopologyResult
{
    internal SourceMeshTopologyResult(
        ImmutableArray<SourceMeshTopologyIsland> islands,
        ImmutableArray<int> isolatedControlPointIds,
        ImmutableArray<SourceMeshTopologyEdge> edges,
        ImmutableArray<SourceMeshTopologyEdge> boundaryEdges,
        ImmutableArray<SourceMeshTopologyEdge> nonManifoldEdges,
        ImmutableArray<SourceMeshTopologyEdge> windingConflictEdges)
    {
        Islands = islands;
        IsolatedControlPointIds = isolatedControlPointIds;
        Edges = edges;
        BoundaryEdges = boundaryEdges;
        NonManifoldEdges = nonManifoldEdges;
        WindingConflictEdges = windingConflictEdges;
    }

    public ImmutableArray<SourceMeshTopologyIsland> Islands { get; }

    public ImmutableArray<int> IsolatedControlPointIds { get; }

    /// <summary>Alias for <see cref="IsolatedControlPointIds"/>.</summary>
    public ImmutableArray<int> UnusedControlPointIds => IsolatedControlPointIds;

    /// <summary>Alias for <see cref="IsolatedControlPointIds"/>.</summary>
    public ImmutableArray<int> IsolatedUnusedControlPointIds => IsolatedControlPointIds;

    public ImmutableArray<SourceMeshTopologyEdge> Edges { get; }

    public ImmutableArray<SourceMeshTopologyEdge> BoundaryEdges { get; }

    public ImmutableArray<SourceMeshTopologyEdge> NonManifoldEdges { get; }

    public ImmutableArray<SourceMeshTopologyEdge> WindingConflictEdges { get; }
}

/// <summary>
/// An immutable connected component of triangles, joined by shared source
/// control-point IDs.
/// </summary>
public sealed class SourceMeshTopologyIsland
{
    internal SourceMeshTopologyIsland(
        ImmutableArray<int> sourceControlPointIds,
        ImmutableArray<int> triangleIndices)
    {
        SourceControlPointIds = sourceControlPointIds;
        TriangleIndices = triangleIndices;
    }

    public ImmutableArray<int> SourceControlPointIds { get; }

    /// <summary>Alias for <see cref="SourceControlPointIds"/>.</summary>
    public ImmutableArray<int> ControlPointIds => SourceControlPointIds;

    public ImmutableArray<int> TriangleIndices { get; }
}

/// <summary>
/// One undirected source control-point edge and all triangle incidences.
/// </summary>
public sealed class SourceMeshTopologyEdge
{
    internal SourceMeshTopologyEdge(
        int sourceControlPointA,
        int sourceControlPointB,
        ImmutableArray<SourceMeshTopologyEdgeIncident> incidents)
    {
        SourceControlPointA = sourceControlPointA;
        SourceControlPointB = sourceControlPointB;
        Incidents = incidents;

        bool hasForwardIncident = false;
        bool hasReverseIncident = false;
        bool windingConflict = false;
        foreach (SourceMeshTopologyEdgeIncident incident in incidents)
        {
            if (incident.IsForward)
            {
                windingConflict = hasForwardIncident;
                hasForwardIncident = true;
            }
            else
            {
                windingConflict = hasReverseIncident;
                hasReverseIncident = true;
            }

            if (windingConflict)
            {
                break;
            }
        }

        HasWindingConflict = windingConflict;
    }

    public int SourceControlPointA { get; }

    public int SourceControlPointB { get; }

    public int A => SourceControlPointA;

    public int B => SourceControlPointB;

    public int ControlPointA => SourceControlPointA;

    public int ControlPointB => SourceControlPointB;

    public ImmutableArray<int> SourceControlPointIds =>
        ImmutableArray.Create(SourceControlPointA, SourceControlPointB);

    public ImmutableArray<SourceMeshTopologyEdgeIncident> Incidents { get; }

    public ImmutableArray<int> TriangleIndices =>
        Incidents.Select(static incident => incident.TriangleIndex).ToImmutableArray();

    public bool HasWindingConflict { get; }
}

/// <summary>
/// One triangle incidence of an undirected source edge. <see cref="IsForward"/>
/// is true when the triangle traverses the canonical A-to-B direction.
/// </summary>
public readonly record struct SourceMeshTopologyEdgeIncident(
    int TriangleIndex,
    bool IsForward);
