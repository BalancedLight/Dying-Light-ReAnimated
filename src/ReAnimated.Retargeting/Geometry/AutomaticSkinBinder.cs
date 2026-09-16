using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

/// <summary>
/// Bounded volume-geodesic candidate binding. Distances travel through known interior cells only;
/// selected region barriers, point eligibility and fixed fractions are hard constraints. No source
/// positions, topology, existing weights or morphs are mutated. Six-neighbor paths remain a sampled
/// approximation, not a certificate of sub-cell separation or release-quality deformation.
/// </summary>
public static class AutomaticSkinBinder
{
    public static string ComputeInputFingerprint(SourceVolumeGrid grid, IReadOnlyList<SkinBindingHandle> handles,
        IReadOnlyList<SkinBindingPoint> points, AutomaticSkinBindingOptions? options = null)
    {
        options ??= new(); Validate(grid, handles, points, options);
        return Fingerprint(grid.InputFingerprint, handles.OrderBy(static h => h.Id).ToArray(), points, options);
    }

    public static string ComputeFixedInputFingerprint(string sourceSha256, IReadOnlyList<SkinBindingHandle> handles, IReadOnlyList<SkinBindingPoint> points)
    {
        if (sourceSha256.Length != 64 || sourceSha256.Any(static c => !Uri.IsHexDigit(c)) || points.Count is <= 0 or > 250_000 || handles.Count is <= 0 or > 128)
            throw new ArgumentException("Fixed binding source identity or input count is invalid.");
        var ids = handles.Select(static h => h.Id).ToHashSet();
        if (ids.Contains(Guid.Empty) || ids.Count != handles.Count || handles.Any(static h => !h.Start.IsFinite || !h.End.IsFinite || h.AllowedRegions.IsDefault) ||
            points.Select(static p => (p.ComponentId, p.ControlPointIndex)).Distinct().Count() != points.Count ||
            points.Any(p => string.IsNullOrWhiteSpace(p.ComponentId) || p.ControlPointIndex < 0 || !p.Position.IsFinite || p.FixedInfluences.IsDefaultOrEmpty ||
                p.AllowedHandles.IsDefault ||
                p.FixedInfluences.Select(static f => f.HandleId).Distinct().Count() != p.FixedInfluences.Length ||
                p.FixedInfluences.Any(f => !ids.Contains(f.HandleId) || !double.IsFinite(f.Weight) || f.Weight <= 0 || f.Weight > 1 || !p.AllowedHandles.IsEmpty && !p.AllowedHandles.Contains(f.HandleId)) ||
                Math.Abs(p.FixedInfluences.Sum(static f => f.Weight) - 1) > 1e-12 || p.FixedInfluences.Length > 8))
            throw new ArgumentException("Every fixed binding point must have a complete, finite declared assignment.");
        return Fingerprint("explicit-fixed-v1:" + sourceSha256, handles.OrderBy(static h => h.Id).ToArray(), points, new());
    }

    public static AutomaticSkinBindingResult BindFixed(string sourceSha256, IReadOnlyList<SkinBindingHandle> handles,
        IReadOnlyList<SkinBindingPoint> points, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fingerprint = ComputeFixedInputFingerprint(sourceSha256, handles, points);
        var rows = points.Select(p => {
            cancellationToken.ThrowIfCancellationRequested();
            return new SkinBindingPointResult(p.ComponentId, p.ControlPointIndex,
                p.FixedInfluences.Select(static w => new GeneratedSkinInfluence(w.HandleId, w.Weight)).ToImmutableArray(), null, null, 0, 0);
        }).ToImmutableArray();
        return new(sourceSha256, null, fingerprint, rows, [], [], 0) { Origin = SkinBindingOrigin.ExplicitFixed };
    }

    public static AutomaticSkinBindingResult Bind(SourceVolumeGrid grid, IReadOnlyList<SkinBindingHandle> handles,
        IReadOnlyList<SkinBindingPoint> points, AutomaticSkinBindingOptions? options = null,
        IProgress<int>? completedHandles = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid); ArgumentNullException.ThrowIfNull(handles); ArgumentNullException.ThrowIfNull(points);
        cancellationToken.ThrowIfCancellationRequested(); options ??= new();
        Validate(grid, handles, points, options);
        var orderedHandles = handles.OrderBy(static h => h.Id).ToArray();
        var cells = InteriorCells(grid, cancellationToken);
        var samples = new int[points.Count];
        var offsets = new double[points.Count];
        var automatic = new List<(Guid Id, double Score)>[points.Count];
        var scoreSums = new double[points.Count];
        var fixedSums = new double[points.Count];
        for (int p = 0; p < points.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixedSums[p] = points[p].FixedInfluences.OrderBy(static f => f.HandleId).Sum(static f => f.Weight);
            var eligible = orderedHandles.Where(h => !points[p].FixedInfluences.Any(f => f.HandleId == h.Id) &&
                (points[p].AllowedHandles.IsEmpty || points[p].AllowedHandles.Contains(h.Id))).ToArray();
            HashSet<int>? sampleRegions = eligible.Any(static h => h.AllowedRegions.IsEmpty) ? null : eligible.SelectMany(static h => h.AllowedRegions).ToHashSet();
            samples[p] = ClosestInterior(grid, cells, points[p].Position, options.SurfaceSearchRadiusCells, options.CellRegions, sampleRegions, out offsets[p]);
            automatic[p] = new(options.MaximumInfluences);
        }
        var distances = new double[grid.CellCount];
        var handleResults = ImmutableArray.CreateBuilder<SkinBindingHandleResult>(handles.Count);
        var diagnostics = ImmutableArray.CreateBuilder<SkinBindingDiagnostic>();
        long expanded = 0;
        foreach (var handle in orderedHandles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Fill(distances, double.PositiveInfinity);
            var queue = new PriorityQueue<int, (double Distance, int Cell)>();
            var regions = handle.AllowedRegions.ToHashSet();
            bool Allowed(int cell) => cells[cell] && (regions.Count == 0 || regions.Contains(options.CellRegions[cell]));
            int seedCount = 0, insideSamples = 0;
            double length = (handle.End - handle.Start).Length;
            double count = Math.Ceiling(length / (grid.CellSize * .5));
            if (!double.IsFinite(count) || count > 100_000) throw new ArgumentException("An influence segment exceeds the sampling budget.", nameof(handles));
            int segmentSteps = Math.Max(1, (int)count);
            for (int step = 0; step <= segmentSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var point = Vector3D.Lerp(handle.Start, handle.End, (double)step / segmentSteps);
                int cell = ContainingCell(grid, point);
                if (cell < 0 || !Allowed(cell)) continue;
                insideSamples++;
                double distance = DistanceToSegment(Position(grid, cell), handle.Start, handle.End);
                if (distance >= distances[cell]) continue;
                if (double.IsPositiveInfinity(distances[cell])) seedCount++;
                distances[cell] = distance;
                queue.Enqueue(cell, (distance, cell));
            }
            int reached = 0;
            while (queue.TryDequeue(out int cell, out var priority))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (priority.Distance != distances[cell]) continue;
                if (++expanded > options.MaximumExpandedCells) throw new InvalidOperationException("Binding exceeded its expanded-cell budget; no partial binding was published.");
                reached++;
                int x = cell % grid.SizeX, yz = cell / grid.SizeX, y = yz % grid.SizeY, z = yz / grid.SizeY;
                if (x > 0) Visit(cell - 1);
                if (x + 1 < grid.SizeX) Visit(cell + 1);
                if (y > 0) Visit(cell - grid.SizeX);
                if (y + 1 < grid.SizeY) Visit(cell + grid.SizeX);
                if (z > 0) Visit(cell - grid.SizeX * grid.SizeY);
                if (z + 1 < grid.SizeZ) Visit(cell + grid.SizeX * grid.SizeY);
                void Visit(int neighbor)
                {
                    if (!Allowed(neighbor)) return;
                    double distance = priority.Distance + grid.CellSize;
                    if (distance >= distances[neighbor]) return;
                    distances[neighbor] = distance; queue.Enqueue(neighbor, (distance, neighbor));
                }
            }
            handleResults.Add(new(handle.Id, seedCount, segmentSteps + 1, insideSamples, reached));
            if (seedCount == 0) diagnostics.Add(new("influence-no-interior-seed", "The influence has no seed inside its permitted volume; review its placement or regions.", HandleId: handle.Id));
            else if (insideSamples < segmentSteps + 1) diagnostics.Add(new("influence-partial-volume", "Part of the influence lies outside its permitted volume; review the segment before accepting deformation.", HandleId: handle.Id));
            for (int p = 0; p < points.Count; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var point = points[p];
                int sample = samples[p];
                if (sample < 0 || fixedSums[p] >= 1 || point.FixedInfluences.Any(f => f.HandleId == handle.Id) ||
                    !point.AllowedHandles.IsEmpty && !point.AllowedHandles.Contains(handle.Id) || !double.IsFinite(distances[sample])) continue;
                double metricDistance = (distances[sample] + offsets[p]) / grid.CellSize;
                double score = Math.Pow(1 / Math.Max(.25, metricDistance), options.DistancePower);
                if (!(score > 0) || !double.IsFinite(score)) continue;
                scoreSums[p] += score;
                int available = options.MaximumInfluences - point.FixedInfluences.Count(static f => f.Weight > 0);
                if (available == 0) continue;
                var row = automatic[p];
                row.Add((handle.Id, score));
                row.Sort(static (a, b) => a.Score == b.Score ? a.Id.CompareTo(b.Id) : b.Score.CompareTo(a.Score));
                if (row.Count > available) row.RemoveAt(row.Count - 1);
            }
            completedHandles?.Report(handleResults.Count);
            cancellationToken.ThrowIfCancellationRequested();
        }
        var results = ImmutableArray.CreateBuilder<SkinBindingPointResult>(points.Count);
        for (int p = 0; p < points.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = points[p];
            double remaining = 1 - fixedSums[p], retainedScore = automatic[p].Sum(static f => f.Score);
            var weights = point.FixedInfluences.Where(static f => f.Weight > 0).Select(static f => new GeneratedSkinInfluence(f.HandleId, f.Weight)).ToList();
            if (retainedScore > 0)
                weights.AddRange(automatic[p].Select(f => new GeneratedSkinInfluence(f.Id, remaining * f.Score / retainedScore)));
            double unbound = retainedScore > 0 ? 0 : remaining;
            double removed = scoreSums[p] > 0 ? remaining * Math.Clamp(1 - retainedScore / scoreSums[p], 0, 1) : 0;
            int sample = samples[p];
            if (unbound > 1e-12) diagnostics.Add(new("unbound-control-point", sample < 0
                ? "No interior sample is close enough. Use an explicit rigid assignment or review detached geometry and volume resolution."
                : "No eligible influence reaches this sample with available influence slots. Review barriers, fixed fractions or component assignment.", point.ComponentId, point.ControlPointIndex));
            if (removed > 1e-6) diagnostics.Add(new("influence-truncation", $"Influence limit removed {removed.ToString("G5", CultureInfo.InvariantCulture)} of the provisional automatic fraction before renormalization.", point.ComponentId, point.ControlPointIndex));
            results.Add(new(point.ComponentId, point.ControlPointIndex, weights.OrderByDescending(static w => w.Weight).ThenBy(static w => w.HandleId).ToImmutableArray(),
                sample < 0 ? null : Coordinate(grid, sample), sample < 0 ? null : offsets[p], unbound, removed));
        }
        return new(grid.SourceSha256, grid.InputFingerprint, Fingerprint(grid.InputFingerprint, orderedHandles, points, options),
            results.MoveToImmutable(), handleResults.MoveToImmutable(), diagnostics.ToImmutable(), expanded);
    }

    private static void Validate(SourceVolumeGrid grid, IReadOnlyList<SkinBindingHandle> handles, IReadOnlyList<SkinBindingPoint> points, AutomaticSkinBindingOptions options)
    {
        if (!grid.HasCompleteField) throw new ArgumentException("Binding requires a complete volume field.", nameof(grid));
        if (options.MaximumInfluences is < 1 or > 8 || !double.IsFinite(options.DistancePower) || options.DistancePower is < .5 or > 8 ||
            !double.IsFinite(options.SurfaceSearchRadiusCells) || options.SurfaceSearchRadiusCells is <= 0 or > 4 ||
            options.MaximumGridCells is < 1 or > 8_000_000 || options.MaximumPoints is < 1 or > 1_000_000 ||
            options.MaximumHandles is < 1 or > 256 || options.MaximumExpandedCells is < 1 or > 100_000_000 || options.CellRegions.IsDefault ||
            options.MaximumPointHandlePairs is < 1 or > 256_000_000 || options.MaximumGridHandlePairs is < 1 or > 512_000_000)
            throw new ArgumentException("Binding limits or settings are invalid.", nameof(options));
        if (grid.CellCount > options.MaximumGridCells || handles.Count == 0 || handles.Count > options.MaximumHandles || points.Count == 0 || points.Count > options.MaximumPoints)
            throw new ArgumentException("Binding input exceeds its cell, influence or point budget.");
        if ((long)points.Count * handles.Count > options.MaximumPointHandlePairs || (long)grid.CellCount * handles.Count > options.MaximumGridHandlePairs)
            throw new ArgumentException("Binding exceeds its total point/influence or grid/influence work budget.");
        if (!options.CellRegions.IsEmpty && options.CellRegions.Length != grid.CellCount) throw new ArgumentException("Region labels must match the supplied grid.", nameof(options));
        var ids = new HashSet<Guid>();
        foreach (var handle in handles)
        {
            if (handle.Id == Guid.Empty || !ids.Add(handle.Id) || !handle.Start.IsFinite || !handle.End.IsFinite ||
                !(handle.End - handle.Start).IsFinite || !double.IsFinite((handle.End - handle.Start).Length) || handle.AllowedRegions.IsDefault ||
                handle.AllowedRegions.Distinct().Count() != handle.AllowedRegions.Length || !handle.AllowedRegions.IsEmpty && options.CellRegions.IsEmpty)
                throw new ArgumentException("Influence identity, segment or region selection is invalid.", nameof(handles));
        }
        var sourceIds = new HashSet<(string, int)>();
        foreach (var point in points)
        {
            if (string.IsNullOrWhiteSpace(point.ComponentId) || point.ControlPointIndex < 0 || !sourceIds.Add((point.ComponentId, point.ControlPointIndex)) ||
                !point.Position.IsFinite || point.FixedInfluences.IsDefault || point.AllowedHandles.IsDefault ||
                point.FixedInfluences.Any(f => !ids.Contains(f.HandleId) || !double.IsFinite(f.Weight) || f.Weight is < 0 or > 1) ||
                point.FixedInfluences.Select(static f => f.HandleId).Distinct().Count() != point.FixedInfluences.Length ||
                point.FixedInfluences.OrderBy(static f => f.HandleId).Sum(static f => f.Weight) > 1 || point.FixedInfluences.Count(static f => f.Weight > 0) > options.MaximumInfluences ||
                point.AllowedHandles.Any(id => !ids.Contains(id)) || point.AllowedHandles.Distinct().Count() != point.AllowedHandles.Length ||
                !point.AllowedHandles.IsEmpty && point.FixedInfluences.Any(f => f.Weight > 0 && !point.AllowedHandles.Contains(f.HandleId)))
                throw new ArgumentException("Source point identity, eligibility or fixed influences are invalid.", nameof(points));
        }
    }

    private static bool[] InteriorCells(SourceVolumeGrid grid, CancellationToken cancellationToken)
    {
        var cells = new bool[grid.CellCount];
        for (int z = 0; z < grid.SizeZ; z++)
        for (int y = 0; y < grid.SizeY; y++)
        for (int x = 0; x < grid.SizeX; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cell = grid.GetCell(x, y, z);
            cells[(z * grid.SizeY + y) * grid.SizeX + x] = cell.Location == SourceVolumeLocation.Interior && cell.SignedField is < 0;
        }
        return cells;
    }

    private static int ClosestInterior(SourceVolumeGrid grid, bool[] interior, Vector3D position, double radiusCells,
        ImmutableArray<int> labels, HashSet<int>? allowedRegions, out double distance)
    {
        distance = double.PositiveInfinity;
        var relative = (position - grid.Min) / grid.CellSize;
        if (!relative.IsFinite || relative.X < -radiusCells || relative.Y < -radiusCells || relative.Z < -radiusCells ||
            relative.X > grid.SizeX + radiusCells || relative.Y > grid.SizeY + radiusCells || relative.Z > grid.SizeZ + radiusCells) return -1;
        int radius = (int)Math.Ceiling(radiusCells), selected = -1;
        int x0 = (int)Math.Floor(relative.X), y0 = (int)Math.Floor(relative.Y), z0 = (int)Math.Floor(relative.Z);
        for (int z = Math.Max(0, z0 - radius); z <= Math.Min(grid.SizeZ - 1, z0 + radius); z++)
        for (int y = Math.Max(0, y0 - radius); y <= Math.Min(grid.SizeY - 1, y0 + radius); y++)
        for (int x = Math.Max(0, x0 - radius); x <= Math.Min(grid.SizeX - 1, x0 + radius); x++)
        {
            int cell = (z * grid.SizeY + y) * grid.SizeX + x;
            if (!interior[cell] || allowedRegions is not null && (allowedRegions.Count == 0 || !allowedRegions.Contains(labels[cell]))) continue;
            double candidate = (position - grid.GetPosition(x, y, z)).Length;
            if (candidate <= grid.CellSize * radiusCells && candidate < distance) { selected = cell; distance = candidate; }
        }
        return selected;
    }

    private static int ContainingCell(SourceVolumeGrid grid, Vector3D position)
    {
        var relative = (position - grid.Min) / grid.CellSize;
        if (!relative.IsFinite || relative.X < 0 || relative.Y < 0 || relative.Z < 0 || relative.X >= grid.SizeX || relative.Y >= grid.SizeY || relative.Z >= grid.SizeZ) return -1;
        return ((int)relative.Z * grid.SizeY + (int)relative.Y) * grid.SizeX + (int)relative.X;
    }
    private static VolumeGridCoordinate Coordinate(SourceVolumeGrid grid, int cell) => new(cell % grid.SizeX, cell / grid.SizeX % grid.SizeY, cell / (grid.SizeX * grid.SizeY));
    private static Vector3D Position(SourceVolumeGrid grid, int cell) { var p = Coordinate(grid, cell); return grid.GetPosition(p.X, p.Y, p.Z); }
    private static double DistanceToSegment(Vector3D p, Vector3D start, Vector3D end)
    {
        var delta = end - start;
        double length = delta.Length;
        if (length == 0) return (p - start).Length;
        var direction = delta / length;
        return (p - (start + direction * Math.Clamp(Vector3D.Dot(p - start, direction), 0, length))).Length;
    }

    private static string Fingerprint(string domainFingerprint, SkinBindingHandle[] handles, IReadOnlyList<SkinBindingPoint> points, AutomaticSkinBindingOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        void Number(double value) => Add(value.ToString("R", CultureInfo.InvariantCulture));
        void Point(Vector3D value) { Number(value.X); Number(value.Y); Number(value.Z); }
        Add(AutomaticSkinBindingResult.BackendId); Add(AutomaticSkinBindingResult.BackendVersion); Add(domainFingerprint);
        Number(options.MaximumInfluences); Number(options.DistancePower); Number(options.SurfaceSearchRadiusCells);
        Number(options.MaximumGridCells); Number(options.MaximumHandles); Number(options.MaximumPoints);
        Number(options.MaximumExpandedCells); Number(options.MaximumGridHandlePairs); Number(options.MaximumPointHandlePairs);
        foreach (int region in options.CellRegions) Number(region);
        Add("handles");
        foreach (var handle in handles)
        { Add(handle.Id.ToString("N")); Point(handle.Start); Point(handle.End); foreach (int region in handle.AllowedRegions.Order()) Number(region); Add("end-handle"); }
        Add("points");
        foreach (var point in points)
        {
            Add(point.ComponentId); Number(point.ControlPointIndex); Point(point.Position);
            foreach (var fixedWeight in point.FixedInfluences.OrderBy(static f => f.HandleId)) { Add(fixedWeight.HandleId.ToString("N")); Number(fixedWeight.Weight); }
            Add("allowed"); foreach (var id in point.AllowedHandles.Order()) Add(id.ToString("N")); Add("end-point");
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
