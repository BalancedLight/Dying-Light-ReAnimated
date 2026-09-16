using System.Collections.Immutable;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class SkinWeightCorrectionTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-000000000004");

    [Fact]
    public void SetInfluenceBlendsAndPreservesLockedFractionsIncludingZeroLocks()
    {
        var points = new[]
        {
            Point("body", 4, [(A, .2), (B, .6), (C, 0), (D, .2)], [A, C], []),
        };
        SkinWeightCorrectionResult result = SkinWeightCorrection.Compute(points,
            [new SkinWeightCorrectionSelection(0, .5)],
            new() { Kind = SkinWeightCorrectionKind.SetInfluence, TargetInfluence = B, TargetWeight = .8 });

        Assert.True(result.CanApply);
        SkinWeightCorrectionChange change = Assert.Single(result.Changes);
        Assert.Equal(.2, change.After.Single(row => row.HandleId == A).Weight, 12);
        Assert.DoesNotContain(change.After, row => row.HandleId == C);
        Assert.Equal(.7, change.After.Single(row => row.HandleId == B).Weight, 12);
        Assert.Equal(.1, change.After.Single(row => row.HandleId == D).Weight, 12);
        Assert.Equal(0, change.RemovedWeightBeforeNormalization, 12);
    }

    [Fact]
    public void SetInfluenceOnUnweightedPointCanCreateOneTargetRow()
    {
        var points = new[] { Point("body", 0, [], [], []) };
        SkinWeightCorrectionResult result = SkinWeightCorrection.Compute(points,
            [new SkinWeightCorrectionSelection(0, 1)],
            new() { Kind = SkinWeightCorrectionKind.SetInfluence, TargetInfluence = A, TargetWeight = 1 });

        GeneratedSkinInfluence row = Assert.Single(Assert.Single(result.Changes).After);
        Assert.Equal(A, row.HandleId);
        Assert.Equal(1, row.Weight, 12);
    }

    [Fact]
    public void NormalizeCapsStrongestUnlockedRowsWithGuidTieBreakAndReportsRemovedMass()
    {
        var points = new[] { Point("body", 0, [(A, 1d / 3), (C, 1d / 3), (B, 1d / 3)], [], []) };
        SkinWeightCorrectionResult result = SkinWeightCorrection.Compute(points,
            [new SkinWeightCorrectionSelection(0, 1)],
            new() { Kind = SkinWeightCorrectionKind.Normalize, MaximumInfluences = 2 });

        SkinWeightCorrectionChange change = Assert.Single(result.Changes);
        Assert.Equal(0.5, change.After.Single(row => row.HandleId == A).Weight, 12);
        Assert.Equal(0.5, change.After.Single(row => row.HandleId == B).Weight, 12);
        Assert.DoesNotContain(change.After, row => row.HandleId == C);
        Assert.Equal(1d / 3, change.RemovedWeightBeforeNormalization, 12);
        Assert.InRange(change.After.Sum(row => row.Weight), 1 - 1e-12, 1 + 1e-12);
    }

    [Fact]
    public void LockedTargetRefusesTransactionWithoutPartialChanges()
    {
        var points = new[] { Point("body", 0, [(A, .2), (B, .8)], [B], []) };
        SkinWeightCorrectionResult result = SkinWeightCorrection.Compute(points,
            [new SkinWeightCorrectionSelection(0, 1)],
            new() { Kind = SkinWeightCorrectionKind.SetInfluence, TargetInfluence = B, TargetWeight = 1 });

        Assert.False(result.CanApply);
        Assert.Empty(result.Changes);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "blocking-target-locked");
        Assert.Equal<GeneratedSkinInfluence>(points[0].Weights, [new(A, .2), new(B, .8)]);
    }

    [Fact]
    public void SmoothUsesJacobiSnapshotsAndDoesNotCrossComponents()
    {
        var points = new[]
        {
            Point("left", 0, [(A, 1)], [], [1]),
            Point("left", 1, [(B, 1)], [], [0]),
        };
        SkinWeightCorrectionResult result = SkinWeightCorrection.Compute(points,
            [new SkinWeightCorrectionSelection(0, 1), new SkinWeightCorrectionSelection(1, 1)],
            new() { Kind = SkinWeightCorrectionKind.Smooth, MaximumInfluences = 2 });

        Assert.True(result.CanApply);
        Assert.All(result.Changes, change => Assert.Equal(1, change.After.Sum(row => row.Weight), 12));
        Assert.Equal(.5, Assert.Single(result.Changes[0].After, row => row.HandleId == A).Weight, 12);
        Assert.Equal(.5, Assert.Single(result.Changes[0].After, row => row.HandleId == B).Weight, 12);
    }

    [Fact]
    public void ZeroStrengthSelectionIsANoopAndPreservesInputOrder()
    {
        var points = new[] { Point("body", 0, [(B, .4), (A, .6)], [], []) };
        SkinWeightCorrectionResult result = SkinWeightCorrection.Compute(points,
            [new SkinWeightCorrectionSelection(0, 0)],
            new() { Kind = SkinWeightCorrectionKind.Normalize });

        Assert.True(result.CanApply);
        Assert.Empty(result.Changes);
        Assert.Equal<GeneratedSkinInfluence>([new(B, .4), new(A, .6)], points[0].Weights);
    }

    [Fact]
    public void RejectsCrossComponentDuplicateAndNonFiniteInput()
    {
        var crossComponent = new[]
        {
            Point("left", 0, [(A, 1)], [], [1]),
            Point("right", 0, [(B, 1)], [], []),
        };
        Assert.Throws<ArgumentException>(() => SkinWeightCorrection.Compute(crossComponent, [], new() { Kind = SkinWeightCorrectionKind.Smooth }));
        var duplicate = new[] { Point("body", 0, [(A, .5), (A, .5)], [], []) };
        Assert.Throws<ArgumentException>(() => SkinWeightCorrection.Compute(duplicate, [], new() { Kind = SkinWeightCorrectionKind.Normalize }));
        var nonFinite = new[] { Point("body", 0, [(A, double.NaN)], [], []) };
        Assert.Throws<ArgumentException>(() => SkinWeightCorrection.Compute(nonFinite, [], new() { Kind = SkinWeightCorrectionKind.Normalize }));
    }

    [Fact]
    public void LockedPositiveRowsOverCapProduceActionableDiagnostic()
    {
        var points = new[] { Point("body", 0, [(A, .4), (B, .3), (C, .3)], [A, B], []) };
        SkinWeightCorrectionResult result = SkinWeightCorrection.Compute(points, [new SkinWeightCorrectionSelection(0, 1)],
            new() { Kind = SkinWeightCorrectionKind.Normalize, MaximumInfluences = 1 });

        Assert.False(result.CanApply);
        Assert.Empty(result.Changes);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "blocking-locked-influence-cap");
    }

    [Fact]
    public void CancellationIsHonoredDuringBoundedInputWork()
    {
        var points = Enumerable.Range(0, 2000).Select(index => Point("body", index, [(A, 1)], [], [])).ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SkinWeightCorrection.Compute(points, [], new() { Kind = SkinWeightCorrectionKind.Normalize }, cancellation.Token));
    }

    [Fact]
    public void InfluenceCapKeepsRequestedTargetExactAndReportsOnlyTruncation()
    {
        var points = new[] { Point("body", 0, [(A, .1), (B, .4), (C, .3), (D, .2)], [], []) };
        var result = SkinWeightCorrection.Compute(points, [new(0, 1)], new() {
            TargetInfluence = A, TargetWeight = .2, MaximumInfluences = 2,
        });
        var changed = Assert.Single(result.Changes);
        Assert.Equal(.2, changed.After.Single(w => w.HandleId == A).Weight);
        Assert.Equal(.8, changed.After.Single(w => w.HandleId == B).Weight);
        Assert.Equal(2, changed.After.Length);
        Assert.Equal((.3 + .2) * .8 / .9, changed.RemovedWeightBeforeNormalization, 12);
    }

    [Fact]
    public void AbsentZeroLockPreventsSmoothingLeakageAndSelectionOrderDoesNotMatter()
    {
        var points = new[] {
            Point("body", 0, [(A, 1)], [B], [1]),
            Point("body", 1, [(B, 1)], [], [0]),
        };
        var options = new SkinWeightCorrectionOptions { Kind = SkinWeightCorrectionKind.Smooth, SmoothingIterations = 2 };
        var first = SkinWeightCorrection.Compute(points, [new(0, 1), new(1, 1)], options);
        var reversed = SkinWeightCorrection.Compute(points, [new(1, 1), new(0, 1)], options);
        Assert.DoesNotContain(first.Changes, c => c.PointIndex == 0);
        Assert.Equal<GeneratedSkinInfluence>(first.Changes[0].After, reversed.Changes[0].After);
        Assert.All(first.Changes.SelectMany(c => c.After), w => Assert.True(w.Weight > 0));
    }

    private static SkinWeightCorrectionPoint Point(
        string component,
        int index,
        (Guid Id, double Weight)[] weights,
        Guid[] locked,
        int[] neighbors) =>
        new(component, index, weights.Select(row => new GeneratedSkinInfluence(row.Id, row.Weight)).ToImmutableArray(),
            locked.ToImmutableArray(), neighbors.ToImmutableArray());
}
