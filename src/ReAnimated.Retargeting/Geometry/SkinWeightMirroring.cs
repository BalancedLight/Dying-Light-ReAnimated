using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

public sealed record SkinWeightMirrorMap(ImmutableArray<int> Counterparts, ImmutableArray<double?> Distances,
    ImmutableArray<SkinBindingDiagnostic> Diagnostics);

/// <summary>Reciprocal source-point correspondence within each component and an explicit model-space plane.</summary>
public static class SkinWeightMirroring
{
    public static SkinWeightMirrorMap Build(IReadOnlyList<SkinWeightCorrectionPoint> points, IReadOnlyList<Vector3D> positions,
        Vector3D origin, Vector3D normal, double tolerance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points); ArgumentNullException.ThrowIfNull(positions);
        if (points.Count != positions.Count || points.Count > 250_000 || !origin.IsFinite || !normal.IsFinite ||
            !double.IsFinite(normal.Length) || normal.Length <= 0 || !double.IsFinite(tolerance) || tolerance <= 0)
            throw new ArgumentException("Mirror correspondence requires bounded matching source points, a finite plane and positive tolerance.");
        var unit = normal.Normalized();
        var bins = new Dictionary<(string Component, long X, long Y, long Z), List<int>>();
        var keys = new HashSet<(string, int)>();
        for (int i = 0; i < points.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!keys.Add((points[i].ComponentId, points[i].ControlPointIndex))) throw new ArgumentException("Mirror source point identities must be unique.", nameof(points));
            var cell = Cell(positions[i], points[i].ComponentId);
            if (!bins.TryGetValue(cell, out var contents)) bins.Add(cell, contents = []);
            contents.Add(i);
        }
        var nearest = Enumerable.Repeat(-1, points.Count).ToArray();
        var distances = new double?[points.Count];
        var diagnostics = ImmutableArray.CreateBuilder<SkinBindingDiagnostic>();
        long comparisons = 0;
        for (int i = 0; i < points.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mirrored = positions[i] - unit * (2 * Vector3D.Dot(positions[i] - origin, unit));
            var cell = Cell(mirrored, points[i].ComponentId);
            double best = double.PositiveInfinity; int bestIndex = -1; bool ambiguous = false;
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            {
                if (!bins.TryGetValue((cell.Component, checked(cell.X + x), checked(cell.Y + y), checked(cell.Z + z)), out var candidates)) continue;
                foreach (int candidate in candidates)
                {
                    if (++comparisons > 16_000_000) throw new InvalidOperationException("Mirror correspondence exceeds its candidate budget; reduce tolerance or separate overlapping components.");
                    if ((comparisons & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    double distance = (positions[candidate] - mirrored).Length;
                    if (!double.IsFinite(distance) || distance > tolerance) continue;
                    double tie = Math.Max(1e-14, tolerance * 1e-9);
                    if (distance < best - tie) { best = distance; bestIndex = candidate; ambiguous = false; }
                    else if (Math.Abs(distance - best) <= tie) ambiguous = true;
                }
            }
            if (bestIndex >= 0 && !ambiguous) { nearest[i] = bestIndex; distances[i] = best; }
            else diagnostics.Add(new(ambiguous ? "mirror-ambiguous" : "mirror-unmatched", ambiguous ?
                "Several source points are equally close to the reflected position." : "No counterpart falls within the mirror tolerance.", points[i].ComponentId, points[i].ControlPointIndex));
        }
        var counterparts = Enumerable.Repeat(-1, points.Count).ToArray();
        for (int i = 0; i < points.Count; i++)
        {
            int match = nearest[i];
            if (match >= 0 && nearest[match] == i) counterparts[i] = match;
            else if (match >= 0) diagnostics.Add(new("mirror-nonreciprocal", "The nearest counterpart does not map back to this source point.", points[i].ComponentId, points[i].ControlPointIndex));
        }
        return new(counterparts.ToImmutableArray(), distances.ToImmutableArray(), diagnostics.ToImmutable());

        (string Component, long X, long Y, long Z) Cell(Vector3D position, string component)
        {
            if (!position.IsFinite) throw new ArgumentException("Mirror positions must be finite.", nameof(positions));
            return (component, Coordinate(position.X), Coordinate(position.Y), Coordinate(position.Z));
        }
        long Coordinate(double coordinate)
        {
            double value = Math.Floor(coordinate / tolerance);
            if (!double.IsFinite(value) || value <= long.MinValue + 2.0 || value >= long.MaxValue - 2.0)
                throw new ArgumentException("The mirror tolerance cannot represent these model coordinates.", nameof(tolerance));
            return checked((long)value);
        }
    }

    /// <summary>Sets paired influence targets from the same original weights, including overlapping/centreline requests.</summary>
    public static SkinWeightCorrectionResult SetMirrored(IReadOnlyList<SkinWeightCorrectionPoint> points,
        IReadOnlyList<SkinWeightCorrectionSelection> selection, SkinWeightMirrorMap map,
        Guid influence, Guid counterpartInfluence, double targetWeight, CancellationToken cancellationToken = default)
    {
        if (influence == Guid.Empty || counterpartInfluence == Guid.Empty || map.Counterparts.Length != points.Count ||
            !double.IsFinite(targetWeight) || targetWeight is < 0 or > 1) throw new ArgumentException("Mirrored correction needs a current point map, two explicit influences and a weight from zero to one.");
        var requests = new Dictionary<int, Dictionary<Guid, double>>();
        var selected = new HashSet<int>();
        foreach (var choice in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)choice.PointIndex >= (uint)points.Count || !selected.Add(choice.PointIndex) || !double.IsFinite(choice.Strength) || choice.Strength is < 0 or > 1)
                throw new ArgumentException("Mirror selections must have distinct valid point indices and strengths.", nameof(selection));
            if (choice.Strength == 0) continue;
            int partner = map.Counterparts[choice.PointIndex];
            if ((uint)partner >= (uint)points.Count || map.Counterparts[partner] != choice.PointIndex || points[partner].ComponentId != points[choice.PointIndex].ComponentId)
                return new([], [new("blocking-mirror-unmatched", $"Source point {points[choice.PointIndex].ControlPointIndex} has no unambiguous reciprocal counterpart; adjust the plane, tolerance or selection.", points[choice.PointIndex].ComponentId, points[choice.PointIndex].ControlPointIndex)]);
            Add(choice.PointIndex, influence, choice.Strength);
            Add(partner, counterpartInfluence, choice.Strength);
        }
        var changes = ImmutableArray.CreateBuilder<SkinWeightCorrectionChange>();
        foreach (var (index, targets) in requests.OrderBy(static r => r.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = points[index];
            var working = original with { Neighbors = [] };
            double removed = 0;
            foreach (var (target, strength) in targets.OrderBy(static t => t.Key))
            {
                double old = original.Weights.Where(w => w.HandleId == target).Sum(static w => w.Weight);
                double desired = old + (targetWeight - old) * strength;
                double current = working.Weights.Where(w => w.HandleId == target).Sum(static w => w.Weight);
                var correction = SkinWeightCorrection.Compute([working], desired == current ? [] : [new(0, 1)], new() { TargetInfluence = target, TargetWeight = desired }, cancellationToken);
                if (!correction.CanApply) return new([], correction.Diagnostics);
                if (!correction.Changes.IsEmpty)
                {
                    var result = correction.Changes[0]; removed += result.RemovedWeightBeforeNormalization;
                    working = working with { Weights = result.After };
                }
                working = working with { LockedInfluences = working.LockedInfluences.Contains(target) ? working.LockedInfluences : working.LockedInfluences.Add(target) };
            }
            var before = original.Weights.Where(static w => w.Weight > 0).OrderBy(static w => w.HandleId);
            if (!before.SequenceEqual(working.Weights.Where(static w => w.Weight > 0).OrderBy(static w => w.HandleId)))
                changes.Add(new(index, original.Weights, working.Weights, removed));
        }
        return new(changes.ToImmutable(), []);

        void Add(int index, Guid target, double strength)
        {
            if (!requests.TryGetValue(index, out var targets)) requests.Add(index, targets = []);
            targets[target] = Math.Max(targets.GetValueOrDefault(target), strength);
        }
    }
}
