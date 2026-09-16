using System.Collections.Immutable;

namespace ReAnimated.Retargeting.Geometry;

public enum SkinWeightCorrectionKind { SetInfluence, Normalize, Smooth }

/// <summary>A source control point, its weights, locks, and point-list neighbor ordinals.</summary>
public sealed record SkinWeightCorrectionPoint(
    string ComponentId,
    int ControlPointIndex,
    ImmutableArray<GeneratedSkinInfluence> Weights,
    ImmutableArray<Guid> LockedInfluences,
    ImmutableArray<int> Neighbors);

public sealed record SkinWeightCorrectionSelection(int PointIndex, double Strength);

public sealed record SkinWeightCorrectionOptions
{
    public SkinWeightCorrectionKind Kind { get; init; }
    public Guid? TargetInfluence { get; init; }
    public double TargetWeight { get; init; } = 1;
    public int MaximumInfluences { get; init; } = 4;
    public int SmoothingIterations { get; init; } = 1;
}

public sealed record SkinWeightCorrectionChange(
    int PointIndex,
    ImmutableArray<GeneratedSkinInfluence> Before,
    ImmutableArray<GeneratedSkinInfluence> After,
    double RemovedWeightBeforeNormalization);

/// <summary>
/// Diagnostics with a <c>blocking-</c> code cannot be applied. The calculator
/// returns no changes whenever a blocking diagnostic is produced.
/// </summary>
public sealed record SkinWeightCorrectionResult(
    ImmutableArray<SkinWeightCorrectionChange> Changes,
    ImmutableArray<SkinBindingDiagnostic> Diagnostics)
{
    public bool CanApply => !Diagnostics.Any(static diagnostic => diagnostic.Code.StartsWith("blocking-", StringComparison.Ordinal));
}

/// <summary>Pure, bounded, deterministic candidate weight operations.</summary>
public static class SkinWeightCorrection
{
    private const int MaximumPoints = 250_000;
    private const int MaximumRowsPerPoint = 256;
    private const long MaximumWeightRows = 4_000_000;
    private const long MaximumNeighborIterations = 32_000_000;
    private const double SumTolerance = 1e-12;

    private sealed class WorkingPoint
    {
        public Guid[] Ids;
        public double[] Weights;
        public double RemovedWeight;
        public WorkingPoint(Guid[] ids, double[] weights) { Ids = ids; Weights = weights; }
        public WorkingPoint Clone() => new((Guid[])Ids.Clone(), (double[])Weights.Clone());
    }

    public static SkinWeightCorrectionResult Compute(
        IReadOnlyList<SkinWeightCorrectionPoint> points,
        IReadOnlyList<SkinWeightCorrectionSelection> selection,
        SkinWeightCorrectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(points, selection, options, cancellationToken);
        var selected = selection.ToDictionary(static row => row.PointIndex);
        var working = new WorkingPoint[points.Count];
        for (int index = 0; index < points.Count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            working[index] = new(points[index].Weights.Select(static row => row.HandleId).ToArray(), points[index].Weights.Select(static row => row.Weight).ToArray());
        }

        ImmutableArray<SkinBindingDiagnostic> diagnostics = options.Kind switch
        {
            SkinWeightCorrectionKind.SetInfluence => ApplySet(points, selected, options, working, cancellationToken),
            SkinWeightCorrectionKind.Normalize => ApplyNormalize(points, selected, options, working, cancellationToken),
            SkinWeightCorrectionKind.Smooth => ApplySmooth(points, selected, options, working, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        if (!diagnostics.IsEmpty) return new([], diagnostics);
        var changes = ImmutableArray.CreateBuilder<SkinWeightCorrectionChange>();
        for (int index = 0; index < points.Count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!selected.ContainsKey(index) || SameWeights(points[index].Weights, working[index])) continue;
            changes.Add(new(index, points[index].Weights, ToImmutable(working[index]), working[index].RemovedWeight));
        }
        return new(changes.ToImmutable(), []);
    }

    private static ImmutableArray<SkinBindingDiagnostic> ApplySet(IReadOnlyList<SkinWeightCorrectionPoint> points, IReadOnlyDictionary<int, SkinWeightCorrectionSelection> selected, SkinWeightCorrectionOptions options, WorkingPoint[] working, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<SkinBindingDiagnostic>();
        Guid target = options.TargetInfluence!.Value;
        foreach ((int index, SkinWeightCorrectionSelection choice) in OrderedSelection(selected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (choice.Strength == 0) continue;
            SkinWeightCorrectionPoint source = points[index];
            WorkingPoint point = working[index];
            if (source.LockedInfluences.Contains(target))
            {
                diagnostics.Add(Block("target-locked", "The requested target influence is locked and cannot be changed.", source, target));
                break;
            }
            double lockedSum = LockedSum(source, point);
            if (lockedSum > 1 + SumTolerance)
            {
                diagnostics.Add(Block("locked-fractions-exceed-one", "Locked influence fractions leave no valid normalized budget.", source));
                break;
            }
            int targetRow = IndexOf(point.Ids, target);
            double oldTarget = targetRow < 0 ? 0 : point.Weights[targetRow];
            double available = Math.Max(0, 1 - lockedSum);
            double desiredTarget = Math.Clamp(oldTarget + (options.TargetWeight - oldTarget) * choice.Strength, 0, 1);
            if (desiredTarget > available + SumTolerance)
            {
                diagnostics.Add(Block("target-budget", "The requested target fraction is incompatible with locked fractions.", source, target));
                break;
            }
            desiredTarget = Math.Min(desiredTarget, available);
            if (targetRow < 0 && desiredTarget > 0)
            {
                point.Ids = Append(point.Ids, target);
                point.Weights = Append(point.Weights, 0);
                targetRow = point.Ids.Length - 1;
            }
            int[] otherRows = Enumerable.Range(0, point.Ids.Length).Where(row => !source.LockedInfluences.Contains(point.Ids[row]) && point.Ids[row] != target).ToArray();
            double otherBudget = available - desiredTarget;
            double otherSum = otherRows.Sum(row => Math.Max(0, point.Weights[row]));
            if (otherBudget > SumTolerance && otherSum <= SumTolerance)
            {
                diagnostics.Add(Block("redistribution-empty", "No unlocked contributor can receive the remaining normalized budget.", source, target));
                break;
            }
            for (int row = 0; row < point.Ids.Length; row++)
            {
                if (source.LockedInfluences.Contains(point.Ids[row])) continue;
                point.Weights[row] = point.Ids[row] == target ? desiredTarget : otherSum <= SumTolerance ? 0 : otherBudget * Math.Max(0, point.Weights[row]) / otherSum;
            }
            if (!TryNormalizeWithCap(source, point, options.MaximumInfluences, target, out SkinBindingDiagnostic? diagnostic))
            {
                diagnostics.Add(diagnostic!);
                break;
            }
        }
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<SkinBindingDiagnostic> ApplyNormalize(IReadOnlyList<SkinWeightCorrectionPoint> points, IReadOnlyDictionary<int, SkinWeightCorrectionSelection> selected, SkinWeightCorrectionOptions options, WorkingPoint[] working, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<SkinBindingDiagnostic>();
        foreach ((int index, SkinWeightCorrectionSelection choice) in OrderedSelection(selected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (choice.Strength == 0) continue;
            SkinWeightCorrectionPoint source = points[index];
            WorkingPoint before = working[index].Clone();
            if (!TryNormalizeWithCap(source, working[index], options.MaximumInfluences, null, out SkinBindingDiagnostic? diagnostic))
            {
                diagnostics.Add(diagnostic!);
                break;
            }
            if (choice.Strength < 1)
            {
                for (int row = 0; row < working[index].Weights.Length; row++) working[index].Weights[row] = before.Weights[row] + (working[index].Weights[row] - before.Weights[row]) * choice.Strength;
                if (!TryNormalizeWithCap(source, working[index], options.MaximumInfluences, null, out diagnostic))
                {
                    diagnostics.Add(diagnostic!);
                    break;
                }
            }
        }
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<SkinBindingDiagnostic> ApplySmooth(IReadOnlyList<SkinWeightCorrectionPoint> points, IReadOnlyDictionary<int, SkinWeightCorrectionSelection> selected, SkinWeightCorrectionOptions options, WorkingPoint[] working, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<SkinBindingDiagnostic>();
        long lookupWork = 0;
        for (int iteration = 0; iteration < options.SmoothingIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkingPoint[] snapshot = working.Select(static row => row.Clone()).ToArray();
            foreach ((int index, SkinWeightCorrectionSelection choice) in OrderedSelection(selected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (choice.Strength == 0) continue;
                SkinWeightCorrectionPoint source = points[index];
                WorkingPoint current = working[index];
                Guid[] ids = current.Ids.Concat(source.Neighbors.SelectMany(neighbor => snapshot[neighbor].Ids)).Concat(source.LockedInfluences).Distinct().OrderBy(static id => id).ToArray();
                var values = new double[ids.Length];
                int contributors = source.Neighbors.Length + 1;
                for (int row = 0; row < ids.Length; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lookupWork = checked(lookupWork + snapshot[index].Ids.Length);
                    foreach (int neighbor in source.Neighbors) lookupWork = checked(lookupWork + snapshot[neighbor].Ids.Length);
                    if (lookupWork > MaximumNeighborIterations) throw new InvalidOperationException("Smoothing exceeds its bounded influence-lookup budget; select fewer points or iterations.");
                    double average = WeightFor(snapshot[index], ids[row]) / contributors;
                    foreach (int neighbor in source.Neighbors) average += WeightFor(snapshot[neighbor], ids[row]) / contributors;
                    double old = WeightFor(snapshot[index], ids[row]);
                    values[row] = old + (average - old) * choice.Strength;
                    if (source.LockedInfluences.Contains(ids[row]))
                        values[row] = old;
                }
                current.Ids = ids;
                current.Weights = values;
                if (!TryNormalizeWithCap(source, current, options.MaximumInfluences, null, out SkinBindingDiagnostic? diagnostic))
                {
                    diagnostics.Add(diagnostic!);
                    return diagnostics.ToImmutable();
                }
            }
        }
        return diagnostics.ToImmutable();
    }

    private static bool TryNormalizeWithCap(SkinWeightCorrectionPoint source, WorkingPoint point, int maximumInfluences, Guid? forcedId, out SkinBindingDiagnostic? diagnostic)
    {
        diagnostic = null;
        double lockedSum = LockedSum(source, point);
        if (lockedSum > 1)
        {
            diagnostic = Block("locked-fractions-exceed-one", "Locked influence fractions leave no valid normalized budget.", source);
            return false;
        }
        int lockedPositive = Enumerable.Range(0, point.Ids.Length).Count(row => source.LockedInfluences.Contains(point.Ids[row]) && point.Weights[row] > 0);
        if (lockedPositive > maximumInfluences)
        {
            diagnostic = Block("locked-influence-cap", "Positive locked influences already exceed the configured maximum.", source);
            return false;
        }
        int slots = maximumInfluences - lockedPositive;
        int[] eligible = Enumerable.Range(0, point.Ids.Length).Where(row => !source.LockedInfluences.Contains(point.Ids[row]) && point.Weights[row] > 0).OrderByDescending(row => point.Weights[row]).ThenBy(row => point.Ids[row]).ToArray();
        var retained = new HashSet<int>();
        if (forcedId is Guid forced)
        {
            int row = IndexOf(point.Ids, forced);
            if (row >= 0 && point.Weights[row] > 0) retained.Add(row);
        }
        if (retained.Count > slots)
        {
            diagnostic = Block("influence-cap", "A requested positive influence cannot fit beside the locked influence cap.", source, forcedId);
            return false;
        }
        foreach (int row in eligible)
        {
            if (retained.Count >= slots) break;
            retained.Add(row);
        }
        double available = Math.Max(0, 1 - lockedSum);
        int forcedRow = forcedId is { } chosen ? IndexOf(point.Ids, chosen) : -1;
        double forcedWeight = forcedRow < 0 ? 0 : point.Weights[forcedRow];
        double distributable = Math.Max(0, available - forcedWeight);
        double retainedSum = retained.Where(row => row != forcedRow).Sum(row => point.Weights[row]);
        if (distributable > SumTolerance && retainedSum <= 0)
        {
            diagnostic = Block("normalization-empty", "No unlocked positive contributor can receive the remaining normalized budget.", source, forcedId);
            return false;
        }
        for (int row = 0; row < point.Ids.Length; row++)
        {
            if (source.LockedInfluences.Contains(point.Ids[row])) continue;
            if (!retained.Contains(row)) point.RemovedWeight += point.Weights[row];
            if (row == forcedRow) continue;
            point.Weights[row] = retained.Contains(row) && retainedSum > 0 ? distributable * (point.Weights[row] / retainedSum) : 0;
        }
        var adjustable = retained.Where(row => row != forcedRow).OrderBy(row => point.Ids[row]).ToArray();
        if (adjustable.Length > 0)
        {
            double residual = available - Enumerable.Range(0, point.Ids.Length).Where(row => !source.LockedInfluences.Contains(point.Ids[row])).Sum(row => point.Weights[row]);
            int adjust = adjustable[^1];
            if (point.Weights[adjust] + residual >= -SumTolerance) point.Weights[adjust] = Math.Max(0, point.Weights[adjust] + residual);
        }
        if (point.Weights.Any(static w => !double.IsFinite(w) || w < 0 || w > 1) || Math.Abs(point.Weights.Sum() - 1) > SumTolerance)
        { diagnostic = Block("invalid-normalized-result", "These weights cannot produce a finite normalized result while preserving the locks.", source); return false; }
        return true;
    }

    private static IEnumerable<(int Index, SkinWeightCorrectionSelection Choice)> OrderedSelection(IReadOnlyDictionary<int, SkinWeightCorrectionSelection> selected) => selected.OrderBy(static row => row.Key).Select(static row => (row.Key, row.Value));
    private static double LockedSum(SkinWeightCorrectionPoint source, WorkingPoint point) => Enumerable.Range(0, point.Ids.Length).Where(row => source.LockedInfluences.Contains(point.Ids[row])).Sum(row => point.Weights[row]);
    private static double WeightFor(WorkingPoint point, Guid id) { int index = IndexOf(point.Ids, id); return index < 0 ? 0 : point.Weights[index]; }
    private static int IndexOf(Guid[] ids, Guid id) { for (int index = 0; index < ids.Length; index++) if (ids[index] == id) return index; return -1; }
    private static Guid[] Append(Guid[] values, Guid value) { var result = new Guid[values.Length + 1]; values.CopyTo(result, 0); result[^1] = value; return result; }
    private static double[] Append(double[] values, double value) { var result = new double[values.Length + 1]; values.CopyTo(result, 0); result[^1] = value; return result; }
    private static ImmutableArray<GeneratedSkinInfluence> ToImmutable(WorkingPoint point) { var result = ImmutableArray.CreateBuilder<GeneratedSkinInfluence>(); for (int index = 0; index < point.Ids.Length; index++) if (point.Weights[index] > 0) result.Add(new(point.Ids[index], point.Weights[index])); return result.ToImmutable(); }
    private static bool SameWeights(ImmutableArray<GeneratedSkinInfluence> before, WorkingPoint after) { var rows = before.Where(static row => row.Weight > 0).ToDictionary(static row => row.HandleId, static row => row.Weight); if (rows.Count != after.Weights.Count(static w => w > 0)) return false; for (int index = 0; index < after.Ids.Length; index++) if (after.Weights[index] > 0 && (!rows.TryGetValue(after.Ids[index], out double weight) || weight != after.Weights[index])) return false; return true; }
    private static SkinBindingDiagnostic Block(string code, string message, SkinWeightCorrectionPoint point, Guid? handleId = null) => new("blocking-" + code, message, point.ComponentId, point.ControlPointIndex, handleId);

    private static void Validate(IReadOnlyList<SkinWeightCorrectionPoint> points, IReadOnlyList<SkinWeightCorrectionSelection> selection, SkinWeightCorrectionOptions options, CancellationToken cancellationToken)
    {
        if (points.Count > MaximumPoints) throw new ArgumentException("The point count exceeds the correction budget.", nameof(points));
        if (!Enum.IsDefined(options.Kind)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.TargetInfluence is Guid target && target == Guid.Empty) throw new ArgumentException("Target influence cannot be empty.", nameof(options));
        if (!double.IsFinite(options.TargetWeight) || options.TargetWeight is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumInfluences is < 1 or > MaximumRowsPerPoint) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.SmoothingIterations is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Kind == SkinWeightCorrectionKind.SetInfluence && options.TargetInfluence is null) throw new ArgumentException("SetInfluence requires a target influence.", nameof(options));
        var keys = new HashSet<(string Component, int Index)>(); var allInfluences = new HashSet<Guid>(); long rows = 0, edges = 0;
        for (int index = 0; index < points.Count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            SkinWeightCorrectionPoint point = points[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(point.ComponentId, nameof(points));
            if (point.ControlPointIndex < 0 || !keys.Add((point.ComponentId, point.ControlPointIndex))) throw new ArgumentException("Point identities must be unique and nonnegative.", nameof(points));
            if (point.Weights.IsDefault || point.LockedInfluences.IsDefault || point.Neighbors.IsDefault) throw new ArgumentException("Point collections must be initialized.", nameof(points));
            if (point.Weights.Length > MaximumRowsPerPoint || point.LockedInfluences.Length > MaximumRowsPerPoint) throw new ArgumentException("A point exceeds the influence row budget.", nameof(points));
            rows = checked(rows + point.Weights.Length); if (rows > MaximumWeightRows) throw new ArgumentException("The total influence row count exceeds the correction budget.", nameof(points));
            var ids = new HashSet<Guid>();
            foreach (GeneratedSkinInfluence influence in point.Weights)
                if (influence.HandleId == Guid.Empty || !ids.Add(influence.HandleId) || !double.IsFinite(influence.Weight) || influence.Weight < 0) throw new ArgumentException("Influences require unique nonnegative finite weights and stable IDs.", nameof(points));
            if (!double.IsFinite(point.Weights.Sum(static w => w.Weight))) throw new ArgumentException("The total point weight must be finite.", nameof(points));
            allInfluences.UnionWith(ids);
            allInfluences.UnionWith(point.LockedInfluences);
            if (allInfluences.Count > 4096) throw new ArgumentException("The influence identity count exceeds the supported rig budget.", nameof(points));
            var locks = new HashSet<Guid>();
            foreach (Guid id in point.LockedInfluences)
                if (id == Guid.Empty || !locks.Add(id)) throw new ArgumentException("Locked influences must be unique stable IDs; an absent influence locks zero.", nameof(points));
            edges = checked(edges + point.Neighbors.Length); if (edges > 4_000_000) throw new ArgumentException("The total neighbor edge count exceeds the correction budget.", nameof(points));
            var neighbors = new HashSet<int>();
            foreach (int neighbor in point.Neighbors)
                if (neighbor < 0 || neighbor >= points.Count || neighbor == index || !neighbors.Add(neighbor) || !string.Equals(points[neighbor].ComponentId, point.ComponentId, StringComparison.Ordinal)) throw new ArgumentException("Neighbors must be valid distinct same-component point indices.", nameof(points));
        }
        if (options.Kind == SkinWeightCorrectionKind.Smooth && (edges + rows + points.Count) * options.SmoothingIterations > MaximumNeighborIterations) throw new ArgumentException("Smoothing exceeds its bounded neighbor-iteration budget.", nameof(options));
        var selected = new HashSet<int>();
        foreach (SkinWeightCorrectionSelection choice in selection)
            if (choice.PointIndex < 0 || choice.PointIndex >= points.Count || !selected.Add(choice.PointIndex) || !double.IsFinite(choice.Strength) || choice.Strength is < 0 or > 1) throw new ArgumentException("Selections require unique valid point indices and strengths from zero through one.", nameof(selection));
    }
}
