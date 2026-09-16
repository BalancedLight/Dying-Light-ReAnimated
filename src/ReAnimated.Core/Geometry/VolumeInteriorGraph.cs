using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Geometry;

/// <summary>
/// An immutable six-neighbor graph over cells with a finite negative signed
/// field and an Interior location. Unknown, surface, and exterior cells are
/// never traversable.
/// </summary>
/// <remarks>
/// A complete field is required because treating unknown samples as empty
/// space could create an unsupported route. Path edge cost is
/// <c>stepLength * (1 + clearanceWeight * cellSize / max(averageClearance,
/// cellSize))</c>. Clearance and cell size use the same units, so the weight
/// is dimensionless; zero weight is physical-length shortest path. The
/// magnitude of each negative signed-field sample is used as a clearance
/// proxy; for union fields this is the grid's union-field magnitude rather
/// than an exact distance to every constituent surface.
/// These are lattice paths: interior cell centres do not certify continuous containment through
/// features smaller than a cell. Fitting must validate or refine such segments separately.
/// </remarks>
public sealed class VolumeInteriorGraph
{
    /// <summary>Maximum grid cells accepted before graph storage is allocated.</summary>
    public const int MaximumCellCount = 8_000_000;

    /// <summary>Maximum expanded nodes allowed by one path query.</summary>
    public const int MaximumVisitedNodes = 2_000_000;

    /// <summary>Maximum finite dimensionless clearance weight accepted by a path query.</summary>
    public const double MaximumClearanceWeight = 1_000_000.0;

    private readonly SourceVolumeGrid _grid;
    private readonly bool[] _traversable;
    private readonly double[] _clearance;

    private VolumeInteriorGraph(
        SourceVolumeGrid grid,
        bool[] traversable,
        double[] clearance,
        string inputFingerprint)
    {
        _grid = grid;
        _traversable = traversable;
        _clearance = clearance;
        SourceSha256 = grid.SourceSha256;
        GridInputFingerprint = grid.InputFingerprint;
        InputFingerprint = inputFingerprint;
    }

    public string SourceSha256 { get; }

    public string InputFingerprint { get; }

    public string GridInputFingerprint { get; }

    public int CellCount => _grid.CellCount;

    public int TraversableCellCount { get; private init; }

    /// <summary>
    /// Builds a bounded immutable graph and requires a complete signed field.
    /// </summary>
    public static VolumeInteriorGraph Build(
        SourceVolumeGrid grid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return BuildCore(grid, null, grid.InputFingerprint, cancellationToken);
    }

    /// <summary>
    /// Builds a graph restricted to the explicitly supplied cell mask. The
    /// mask is validated in full; cells outside it remain non-traversable.
    /// </summary>
    public static VolumeInteriorGraph BuildForRegion(
        SourceVolumeGrid grid,
        IReadOnlySet<VolumeGridCoordinate> includedCells,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(includedCells);
        cancellationToken.ThrowIfCancellationRequested();
        if (grid.CellCount <= 0 || grid.CellCount > MaximumCellCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grid),
                grid.CellCount,
                $"The volume graph supports at most {MaximumCellCount:N0} cells.");
        }

        if (includedCells.Count > MaximumCellCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(includedCells),
                includedCells.Count,
                $"A region supports at most {MaximumCellCount:N0} cell coordinates.");
        }

        var selectedIndices = new List<int>(includedCells.Count);
        foreach (VolumeGridCoordinate coordinate in includedCells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCoordinate(grid, coordinate, nameof(includedCells));
            selectedIndices.Add(Index(grid, coordinate.X, coordinate.Y, coordinate.Z));
        }

        selectedIndices.Sort();
        var selected = new bool[grid.CellCount];
        foreach (int index in selectedIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selected[index] = true;
        }

        string inputFingerprint = CreateRegionFingerprint(grid.InputFingerprint, selectedIndices);
        return BuildCore(grid, selected, inputFingerprint, cancellationToken);
    }

    private static VolumeInteriorGraph BuildCore(
        SourceVolumeGrid grid,
        bool[]? selection,
        string inputFingerprint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!grid.HasCompleteField)
        {
            throw new InvalidDataException(
                "Interior paths require a complete volume field with at least one interior cell.");
        }

        if (grid.CellCount <= 0 || grid.CellCount > MaximumCellCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grid),
                grid.CellCount,
                $"The volume graph supports at most {MaximumCellCount:N0} cells.");
        }

        var traversable = new bool[grid.CellCount];
        var clearance = new double[grid.CellCount];
        int traversableCount = 0;
        for (int z = 0; z < grid.SizeZ; z++)
        {
            for (int y = 0; y < grid.SizeY; y++)
            {
                for (int x = 0; x < grid.SizeX; x++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int index = Index(grid, x, y, z);
                    if (selection is not null && !selection[index])
                    {
                        continue;
                    }

                    SourceVolumeGridCell cell = grid.GetCell(x, y, z);
                    if (cell.Location == SourceVolumeLocation.Interior &&
                        cell.SignedField is double signedField &&
                        double.IsFinite(signedField) &&
                        signedField < 0.0)
                    {
                        traversable[index] = true;
                        clearance[index] = -signedField;
                        traversableCount++;
                    }
                }
            }
        }

        return new VolumeInteriorGraph(grid, traversable, clearance, inputFingerprint)
        {
            TraversableCellCount = traversableCount,
        };
    }

    /// <summary>
    /// Finds a deterministic path between two exact grid cells. A null result
    /// means the traversable components are disconnected. The endpoints are
    /// never snapped to nearby cells.
    /// </summary>
    public SourceVolumeGridPath? FindPath(
        VolumeGridCoordinate start,
        VolumeGridCoordinate end,
        double clearanceWeight = 0.0,
        int maximumVisitedNodes = MaximumVisitedNodes,
        CancellationToken cancellationToken = default)
    {
        ValidateWeight(clearanceWeight);
        ValidateVisitBudget(maximumVisitedNodes);
        cancellationToken.ThrowIfCancellationRequested();
        int startIndex = ValidateEndpoint(start, nameof(start));
        int endIndex = ValidateEndpoint(end, nameof(end));

        if (startIndex == endIndex)
        {
            return new SourceVolumeGridPath(
                InputFingerprint,
                0.0,
                0.0,
                clearanceWeight,
                ImmutableArray.Create(start),
                ImmutableArray.Create(_grid.GetPosition(start.X, start.Y, start.Z)));
        }

        var bestCosts = new double[_traversable.Length];
        Array.Fill(bestCosts, double.PositiveInfinity);
        var parents = new int[_traversable.Length];
        Array.Fill(parents, -1);
        var queue = new PriorityQueue<int, PathPriority>();
        bestCosts[startIndex] = 0.0;
        queue.Enqueue(startIndex, new PathPriority(Heuristic(startIndex, endIndex), 0.0, startIndex));
        int visitedNodes = 0;

        while (queue.TryDequeue(out int current, out PathPriority priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (priority.Cost != bestCosts[current])
            {
                continue;
            }

            visitedNodes++;
            if (visitedNodes > maximumVisitedNodes)
            {
                throw new InvalidOperationException(
                    $"Volume path search exceeded the node visit budget of {maximumVisitedNodes:N0}.");
            }

            if (current == endIndex)
            {
                return BuildPath(endIndex, parents, bestCosts[endIndex], clearanceWeight, cancellationToken);
            }

            VolumeGridCoordinate coordinate = Coordinate(current);
            for (int neighborOrdinal = 0; neighborOrdinal < 6; neighborOrdinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryNeighbor(coordinate, neighborOrdinal, out VolumeGridCoordinate neighbor))
                {
                    continue;
                }

                int neighborIndex = Index(neighbor);
                if (!_traversable[neighborIndex])
                {
                    continue;
                }

                double candidateCost = priority.Cost + EdgeCost(current, neighborIndex, clearanceWeight);
                if (candidateCost < bestCosts[neighborIndex] ||
                    (candidateCost == bestCosts[neighborIndex] && current < parents[neighborIndex]))
                {
                    bestCosts[neighborIndex] = candidateCost;
                    parents[neighborIndex] = current;
                    queue.Enqueue(
                        neighborIndex,
                        new PathPriority(
                            candidateCost + Heuristic(neighborIndex, endIndex),
                            candidateCost,
                            neighborIndex));
                }
            }
        }

        return null;
    }

    private SourceVolumeGridPath BuildPath(
        int endIndex,
        int[] parents,
        double totalCost,
        double clearanceWeight,
        CancellationToken cancellationToken)
    {
        var reversed = new List<VolumeGridCoordinate>();
        int current = endIndex;
        while (current >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reversed.Add(Coordinate(current));
            current = parents[current];
        }

        reversed.Reverse();
        var cells = ImmutableArray.CreateBuilder<VolumeGridCoordinate>(reversed.Count);
        var positions = ImmutableArray.CreateBuilder<Vector3D>(reversed.Count);
        double physicalLength = 0.0;
        for (int index = 0; index < reversed.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VolumeGridCoordinate cell = reversed[index];
            cells.Add(cell);
            positions.Add(_grid.GetPosition(cell.X, cell.Y, cell.Z));
            if (index > 0)
            {
                physicalLength += _grid.CellSize;
            }
        }

        return new SourceVolumeGridPath(
            InputFingerprint,
            totalCost,
            physicalLength,
            clearanceWeight,
            cells.ToImmutable(),
            positions.ToImmutable());
    }

    private double EdgeCost(int current, int neighbor, double clearanceWeight)
    {
        double averageClearance = (_clearance[current] + _clearance[neighbor]) * 0.5;
        double factor = 1.0 + clearanceWeight * _grid.CellSize /
            Math.Max(averageClearance, _grid.CellSize);
        double cost = _grid.CellSize * factor;
        if (!double.IsFinite(cost))
        {
            throw new InvalidOperationException(
                "The requested clearance cost is outside the finite numeric range of the volume grid.");
        }

        return cost;
    }

    private double Heuristic(int current, int end)
    {
        VolumeGridCoordinate a = Coordinate(current);
        VolumeGridCoordinate b = Coordinate(end);
        return (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z)) * _grid.CellSize;
    }

    private int ValidateEndpoint(VolumeGridCoordinate coordinate, string parameterName)
    {
        if ((uint)coordinate.X >= (uint)_grid.SizeX ||
            (uint)coordinate.Y >= (uint)_grid.SizeY ||
            (uint)coordinate.Z >= (uint)_grid.SizeZ)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                coordinate,
                "The requested path endpoint is outside the volume grid.");
        }

        int index = Index(coordinate);
        if (!_traversable[index])
        {
            throw new InvalidDataException(
                $"The requested path endpoint ({coordinate.X}, {coordinate.Y}, {coordinate.Z}) is not a known interior cell.");
        }

        return index;
    }

    private VolumeGridCoordinate Coordinate(int index)
    {
        int x = index % _grid.SizeX;
        int plane = index / _grid.SizeX;
        int y = plane % _grid.SizeY;
        int z = plane / _grid.SizeY;
        return new VolumeGridCoordinate(x, y, z);
    }

    private bool TryNeighbor(
        VolumeGridCoordinate coordinate,
        int ordinal,
        out VolumeGridCoordinate neighbor)
    {
        int x = coordinate.X;
        int y = coordinate.Y;
        int z = coordinate.Z;
        switch (ordinal)
        {
            case 0: x--; break;
            case 1: x++; break;
            case 2: y--; break;
            case 3: y++; break;
            case 4: z--; break;
            default: z++; break;
        }

        if ((uint)x >= (uint)_grid.SizeX ||
            (uint)y >= (uint)_grid.SizeY ||
            (uint)z >= (uint)_grid.SizeZ)
        {
            neighbor = default;
            return false;
        }

        neighbor = new VolumeGridCoordinate(x, y, z);
        return true;
    }

    private static int Index(SourceVolumeGrid grid, int x, int y, int z) =>
        (z * grid.SizeY + y) * grid.SizeX + x;

    private int Index(VolumeGridCoordinate coordinate) =>
        (coordinate.Z * _grid.SizeY + coordinate.Y) * _grid.SizeX + coordinate.X;

    private static void ValidateWeight(double clearanceWeight)
    {
        if (!double.IsFinite(clearanceWeight) ||
            clearanceWeight < 0.0 ||
            clearanceWeight > MaximumClearanceWeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clearanceWeight),
                clearanceWeight,
                $"Clearance weight must be finite and between 0 and {MaximumClearanceWeight:N0}.");
        }
    }

    private static void ValidateVisitBudget(int maximumVisitedNodes)
    {
        if (maximumVisitedNodes <= 0 || maximumVisitedNodes > MaximumVisitedNodes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumVisitedNodes),
                maximumVisitedNodes,
                $"The visit budget must be between 1 and {MaximumVisitedNodes:N0}.");
        }
    }

    private static void ValidateCoordinate(
        SourceVolumeGrid grid,
        VolumeGridCoordinate coordinate,
        string parameterName)
    {
        if ((uint)coordinate.X >= (uint)grid.SizeX ||
            (uint)coordinate.Y >= (uint)grid.SizeY ||
            (uint)coordinate.Z >= (uint)grid.SizeZ)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                coordinate,
                "The selected region contains a coordinate outside the volume grid.");
        }
    }

    private static string CreateRegionFingerprint(
        string gridInputFingerprint,
        IReadOnlyList<int> sortedSelectedIndices)
    {
        var builder = new StringBuilder(
            "volume-interior-region-v1|" + gridInputFingerprint + "|");
        foreach (int index in sortedSelectedIndices)
        {
            builder.Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private readonly record struct PathPriority(
        double EstimatedCost,
        double Cost,
        int CellIndex) : IComparable<PathPriority>
    {
        public int CompareTo(PathPriority other)
        {
            int estimated = EstimatedCost.CompareTo(other.EstimatedCost);
            if (estimated != 0) return estimated;
            int cost = Cost.CompareTo(other.Cost);
            return cost != 0 ? cost : CellIndex.CompareTo(other.CellIndex);
        }
    }
}

/// <summary>An exact cell coordinate in a <see cref="SourceVolumeGrid"/>.</summary>
public readonly record struct VolumeGridCoordinate(int X, int Y, int Z);

/// <summary>An immutable path over exact source volume grid cells.</summary>
public sealed class SourceVolumeGridPath
{
    internal SourceVolumeGridPath(
        string inputFingerprint,
        double totalCost,
        double physicalLength,
        double clearanceWeight,
        ImmutableArray<VolumeGridCoordinate> cells,
        ImmutableArray<Vector3D> positions)
    {
        InputFingerprint = inputFingerprint;
        TotalCost = totalCost;
        PhysicalLength = physicalLength;
        ClearanceWeight = clearanceWeight;
        Cells = cells;
        Positions = positions;
    }

    public string InputFingerprint { get; }

    public double TotalCost { get; }

    public double PhysicalLength { get; }

    public double ClearanceWeight { get; }

    public ImmutableArray<VolumeGridCoordinate> Cells { get; }

    public ImmutableArray<VolumeGridCoordinate> Coordinates => Cells;

    public ImmutableArray<Vector3D> Positions { get; }
}
