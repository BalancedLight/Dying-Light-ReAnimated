using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Conformance;

public sealed record DerivedMotionOptions
{
    public int SampleMultiplier { get; init; } = 2;
    public double PositionTolerance { get; init; } = 1e-5;
    public double AngularTolerance { get; init; } = 1e-5;
    public double LinearTolerance { get; init; } = 1e-5;
    public long MaximumSampleKeys { get; init; } = 8_000_000;
    public string Name { get; init; } = "derived-motion";
}

public sealed record DerivedMotionReport(
    string Algorithm,
    int MappedTargetCount,
    int UnmappedTargetCount,
    int SampleMultiplier,
    long SourceFrameCount,
    long OutputFrameCount,
    FrameRate OutputFrameRate,
    double MaximumPositionError,
    /// <summary>Maximum angular error in radians.</summary>
    double MaximumAngularErrorRadians,
    double MaximumLinearError,
    double PositionTolerance,
    double AngularTolerance,
    double LinearTolerance,
    bool TolerancesExceeded,
    bool Unrepresentable,
    ImmutableArray<string> Diagnostics)
{
    public bool CanExport => !TolerancesExceeded && !Unrepresentable && Diagnostics.IsEmpty;
}

public sealed record DerivedMotionResult(AnimationClip? Clip, DerivedMotionReport Report);

/// <summary>
/// Derives target motion by carrying source animation through exact bind bases.
/// It produces reviewable sampled TRS, never additive or runtime IK motion.
/// </summary>
public static class DerivedMotionSolver
{
    private const string Algorithm = "global-bind-delta-v1";

    public static DerivedMotionResult Derive(
        RigDefinition sourceRig,
        ImmutableArray<TransformMatrix> sourceExactBindLocals,
        AnimationClip sourceClip,
        RigDefinition targetRig,
        ImmutableArray<TransformMatrix> targetExactBindLocals,
        IReadOnlyList<int?> sourceIndexByTargetIndex,
        DerivedMotionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(sourceClip);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(sourceIndexByTargetIndex);
        options ??= new();
        ValidateInputs(sourceRig, sourceExactBindLocals, sourceClip, targetRig, targetExactBindLocals, sourceIndexByTargetIndex, options);
        cancellationToken.ThrowIfCancellationRequested();

        long outputFrameCount;
        try { outputFrameCount = checked((sourceClip.FrameCount - 1) * (long)options.SampleMultiplier + 1); }
        catch (OverflowException) { return Failure(options, sourceClip, "Output frame count overflowed the supported range."); }
        long transformKeys;
        try { transformKeys = checked(outputFrameCount * targetRig.BoneCount); }
        catch (OverflowException) { return Failure(options, sourceClip, "Derived motion key count overflowed the supported range."); }
        long scalarKeys = sourceClip.ScalarTracks.Sum(track => checked((long)track.Keyframes.Length));
        long auxiliaryKeys = sourceClip.AuxiliaryTransformTracks.Sum(track => checked((long)track.Keyframes.Length));
        if (transformKeys + scalarKeys + auxiliaryKeys > options.MaximumSampleKeys)
            return Failure(options, sourceClip, "Derived motion exceeds the sampled-key budget.");

        ImmutableArray<TransformMatrix> sourceBindGlobals = Globals(sourceRig.Bones, sourceExactBindLocals);
        ImmutableArray<TransformMatrix> targetBindGlobals = Globals(targetRig.Bones, targetExactBindLocals);
        var sourceTracks = sourceClip.TransformTracks.ToDictionary(static track => track.BoneIndex);
        var targetKeys = new List<TransformKeyframe>[targetRig.BoneCount];
        var targetLocalSamples = new List<TransformMatrix?>[targetRig.BoneCount];
        for (int target = 0; target < targetRig.BoneCount; target++)
        {
            targetKeys[target] = [];
            targetLocalSamples[target] = [];
        }
        var dynamic = new bool[targetRig.BoneCount];
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        double maxPosition = 0, maxAngular = 0, maxLinear = 0;
        for (long outputFrame = 0; outputFrame < outputFrameCount; outputFrame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceFrame = outputFrame / (double)options.SampleMultiplier;
            ImmutableArray<TransformMatrix> sourceGlobals = EvaluateGlobals(sourceRig, sourceExactBindLocals, sourceTracks, sourceFrame);
            TransformMatrix[] targetGlobals = DesiredTargetGlobals(targetRig, sourceGlobals, sourceBindGlobals, targetBindGlobals, targetExactBindLocals, sourceIndexByTargetIndex);
            for (int target = 0; target < targetRig.BoneCount; target++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int parent = targetRig.Bones[target].ParentIndex;
                TransformMatrix local = parent < 0 ? targetGlobals[target] : targetGlobals[parent].InvertedAffine() * targetGlobals[target];
                if (!local.IsFinite || Math.Abs(local.LinearDeterminant) <= 1e-12)
                    return Failure(options, sourceClip, $"Target bone {target} produced a non-finite or singular local frame.", unrepresentable: true,
                        mapped: sourceIndexByTargetIndex.Count(i => i is not null), unmapped: sourceIndexByTargetIndex.Count(i => i is null), outputFrameCount);
                targetLocalSamples[target].Add(local.NearlyEquals(targetExactBindLocals[target], 1e-10) ? null : local);
            }
        }

        for (int target = 0; target < targetRig.BoneCount; target++)
        {
            dynamic[target] = targetLocalSamples[target].Any(static sample => sample is not null);
            if (!dynamic[target]) continue;
            for (int index = 0; index < targetLocalSamples[target].Count; index++)
            {
                TransformMatrix local = targetLocalSamples[target][index] ?? targetExactBindLocals[target];
                TransformTRS trs;
                try { trs = local.Decompose(); }
                catch (InvalidOperationException)
                {
                    return Failure(options, sourceClip, $"Target bone {target} produced dynamic shear or an unrepresentable local frame.", unrepresentable: true,
                        mapped: sourceIndexByTargetIndex.Count(i => i is not null), unmapped: sourceIndexByTargetIndex.Count(i => i is null), outputFrameCount);
                }
                if (!trs.ToMatrix().NearlyEquals(local, 1e-8))
                    return Failure(options, sourceClip, $"Target bone {target} produced a local frame that cannot round-trip through TRS.", unrepresentable: true,
                        mapped: sourceIndexByTargetIndex.Count(i => i is not null), unmapped: sourceIndexByTargetIndex.Count(i => i is null), outputFrameCount);
                targetKeys[target].Add(new TransformKeyframe(index, trs));
            }
        }

        for (long outputFrame = 0; outputFrame < outputFrameCount; outputFrame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceFrame = outputFrame / (double)options.SampleMultiplier;
            ImmutableArray<TransformMatrix> sampledSource = EvaluateGlobals(sourceRig, sourceExactBindLocals, sourceTracks, sourceFrame);
            TransformMatrix[] desired = DesiredTargetGlobals(targetRig, sampledSource, sourceBindGlobals, targetBindGlobals, targetExactBindLocals, sourceIndexByTargetIndex);
            Measure(targetRig, targetExactBindLocals, targetKeys, dynamic, outputFrame, desired,
                ref maxPosition, ref maxAngular, ref maxLinear, cancellationToken);
            if (outputFrame + 1 < outputFrameCount)
            {
                double midpointOutputFrame = outputFrame + .5;
                double midpoint = midpointOutputFrame / options.SampleMultiplier;
                ImmutableArray<TransformMatrix> midpointSource = EvaluateGlobals(sourceRig, sourceExactBindLocals, sourceTracks, midpoint);
                TransformMatrix[] midpointDesired = DesiredTargetGlobals(targetRig, midpointSource, sourceBindGlobals, targetBindGlobals, targetExactBindLocals, sourceIndexByTargetIndex);
                Measure(targetRig, targetExactBindLocals, targetKeys, dynamic, midpointOutputFrame, midpointDesired,
                    ref maxPosition, ref maxAngular, ref maxLinear, cancellationToken);
            }
        }

        bool exceeded = maxPosition > options.PositionTolerance || maxAngular > options.AngularTolerance || maxLinear > options.LinearTolerance;
        if (exceeded) diagnostics.Add("Reconstructed mapped globals exceeded one or more configured error tolerances.");
        if (exceeded)
            return new(null, Report(options, sourceClip, outputFrameCount, sourceIndexByTargetIndex, maxPosition, maxAngular, maxLinear, false, diagnostics));

        ImmutableArray<TransformTrack> outputTracks = targetKeys
            .Select((keys, index) => dynamic[index] ? new TransformTrack(index, keys) : null)
            .Where(static track => track is not null)
            .Select(static track => track!)
            .ToImmutableArray();
        ImmutableArray<ScalarTrack> scalars = sourceClip.ScalarTracks.Select(track => new ScalarTrack(track.ChannelName,
            track.Keyframes.Select(key => new ScalarKeyframe(key.Frame * options.SampleMultiplier, key.Value)))).ToImmutableArray();
        ImmutableArray<AuxiliaryTransformTrack> auxiliary = sourceClip.AuxiliaryTransformTracks.Select(track =>
            new AuxiliaryTransformTrack(track.Descriptor, track.Keyframes.Select(key => new TransformKeyframe(key.Frame * options.SampleMultiplier, key.Value)))).ToImmutableArray();
        AnimationClip clip = new(options.Name, new FrameRate(checked(sourceClip.FrameRate.Numerator * options.SampleMultiplier), sourceClip.FrameRate.Denominator),
            outputFrameCount, outputTracks, scalars, auxiliary);
        return new(clip, Report(options, sourceClip, outputFrameCount, sourceIndexByTargetIndex, maxPosition, maxAngular, maxLinear, false, diagnostics));
    }

    private static void ValidateInputs(RigDefinition sourceRig, ImmutableArray<TransformMatrix> sourceLocals,
        AnimationClip clip, RigDefinition targetRig, ImmutableArray<TransformMatrix> targetLocals,
        IReadOnlyList<int?> map, DerivedMotionOptions options)
    {
        if (sourceLocals.Length != sourceRig.BoneCount || targetLocals.Length != targetRig.BoneCount || map.Count != targetRig.BoneCount)
            throw new ArgumentException("Exact bind locals and mapping must cover both rigs.");
        if (options.SampleMultiplier is < 1 or > 8 || options.MaximumSampleKeys <= 0 ||
            !double.IsFinite(options.PositionTolerance) || options.PositionTolerance < 0 ||
            !double.IsFinite(options.AngularTolerance) || options.AngularTolerance < 0 ||
            !double.IsFinite(options.LinearTolerance) || options.LinearTolerance < 0 || string.IsNullOrWhiteSpace(options.Name))
            throw new ArgumentException("Derived motion options are invalid.", nameof(options));
        if (sourceLocals.Any(static local => !local.IsFinite || Math.Abs(local.LinearDeterminant) <= 1e-12) ||
            targetLocals.Any(static local => !local.IsFinite || Math.Abs(local.LinearDeterminant) <= 1e-12))
            throw new ArgumentException("Exact bind locals must be finite and nonsingular.");
        if (clip.TransformTracks.Any(track => (uint)track.BoneIndex >= (uint)sourceRig.BoneCount))
            throw new ArgumentException("The source clip contains a transform track outside the source rig.", nameof(clip));
        foreach (int? source in map)
            if (source is { } index && (uint)index >= (uint)sourceRig.BoneCount)
                throw new ArgumentException("The target mapping contains an invalid source bone index.", nameof(map));
    }

    private static ImmutableArray<TransformMatrix> Globals(ImmutableArray<BoneDefinition> bones, ImmutableArray<TransformMatrix> locals)
    {
        var globals = ImmutableArray.CreateBuilder<TransformMatrix>(bones.Length);
        for (int index = 0; index < bones.Length; index++)
            globals.Add(bones[index].ParentIndex < 0 ? locals[index] : globals[bones[index].ParentIndex] * locals[index]);
        return globals.MoveToImmutable();
    }

    private static ImmutableArray<TransformMatrix> EvaluateGlobals(RigDefinition rig, ImmutableArray<TransformMatrix> bindLocals,
        Dictionary<int, TransformTrack> tracks, double frame)
    {
        var globals = ImmutableArray.CreateBuilder<TransformMatrix>(rig.BoneCount);
        for (int index = 0; index < rig.BoneCount; index++)
        {
            TransformMatrix local = tracks.TryGetValue(index, out TransformTrack? track) ? track.Sample(frame).ToMatrix() : bindLocals[index];
            globals.Add(rig.Bones[index].ParentIndex < 0 ? local : globals[rig.Bones[index].ParentIndex] * local);
        }
        return globals.MoveToImmutable();
    }

    private static TransformMatrix[] DesiredTargetGlobals(RigDefinition targetRig,
        ImmutableArray<TransformMatrix> sourceGlobals, ImmutableArray<TransformMatrix> sourceBind,
        ImmutableArray<TransformMatrix> targetBindGlobals, ImmutableArray<TransformMatrix> targetBindLocals,
        IReadOnlyList<int?> map)
    {
        var result = new TransformMatrix[targetRig.BoneCount];
        for (int target = 0; target < targetRig.BoneCount; target++)
        {
            if (map[target] is { } source)
                result[target] = sourceGlobals[source] * sourceBind[source].InvertedAffine() * targetBindGlobals[target];
            else
                result[target] = targetRig.Bones[target].ParentIndex < 0
                    ? targetBindLocals[target]
                    : result[targetRig.Bones[target].ParentIndex] * targetBindLocals[target];
        }
        return result;
    }

    private static void Measure(RigDefinition targetRig, ImmutableArray<TransformMatrix> targetBind,
        List<TransformKeyframe>[] keys, bool[] dynamic, double outputFrame,
        TransformMatrix[] desired,
        ref double maxPosition, ref double maxAngular, ref double maxLinear, CancellationToken token)
    {
        var actual = new TransformMatrix[targetRig.BoneCount];
        for (int target = 0; target < targetRig.BoneCount; target++)
        {
            token.ThrowIfCancellationRequested();
            TransformMatrix local = dynamic[target] ? Sample(keys[target], outputFrame).ToMatrix() : targetBind[target];
            actual[target] = targetRig.Bones[target].ParentIndex < 0 ? local : actual[targetRig.Bones[target].ParentIndex] * local;
            maxPosition = Math.Max(maxPosition, (actual[target].Translation - desired[target].Translation).Length);
            maxAngular = Math.Max(maxAngular, AngularError(actual[target], desired[target]));
            maxLinear = Math.Max(maxLinear, LinearError(actual[target], desired[target]));
        }
    }

    private static double AngularError(TransformMatrix left, TransformMatrix right)
    {
        double maximum = 0;
        foreach ((Vector3D a, Vector3D b) in new[] { (Axis(left, 0), Axis(right, 0)), (Axis(left, 1), Axis(right, 1)), (Axis(left, 2), Axis(right, 2)) })
            if (a.TryNormalize(out Vector3D an) && b.TryNormalize(out Vector3D bn)) maximum = Math.Max(maximum, Math.Acos(Math.Clamp(Vector3D.Dot(an, bn), -1, 1)));
        return maximum;
    }

    private static double LinearError(TransformMatrix left, TransformMatrix right) => new[]
    {
        Math.Abs(left.M11-right.M11), Math.Abs(left.M12-right.M12), Math.Abs(left.M13-right.M13),
        Math.Abs(left.M21-right.M21), Math.Abs(left.M22-right.M22), Math.Abs(left.M23-right.M23),
        Math.Abs(left.M31-right.M31), Math.Abs(left.M32-right.M32), Math.Abs(left.M33-right.M33),
    }.Max();

    private static Vector3D Axis(TransformMatrix matrix, int axis) => axis switch
    {
        0 => new(matrix.M11, matrix.M21, matrix.M31),
        1 => new(matrix.M12, matrix.M22, matrix.M32),
        _ => new(matrix.M13, matrix.M23, matrix.M33),
    };

    private static TransformTRS Sample(List<TransformKeyframe> keys, double frame)
    {
        if (frame <= keys[0].Frame) return keys[0].Value;
        if (frame >= keys[^1].Frame) return keys[^1].Value;
        int lowerIndex = Math.Clamp((int)Math.Floor(frame), 0, keys.Count - 1);
        int upperIndex = Math.Min(lowerIndex + 1, keys.Count - 1);
        TransformKeyframe lower = keys[lowerIndex];
        TransformKeyframe next = keys[upperIndex];
        return TransformTRS.Interpolate(lower.Value, next.Value, (frame - lower.Frame) / (next.Frame - lower.Frame));
    }

    private static DerivedMotionResult Failure(DerivedMotionOptions options, AnimationClip sourceClip, string diagnostic,
        bool unrepresentable = false, int mapped = 0, int unmapped = 0, long outputFrameCount = 0)
    {
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        diagnostics.Add(diagnostic);
        return new(null, Report(options, sourceClip, outputFrameCount, Array.Empty<int?>(), 0, 0, 0, unrepresentable,
            diagnostics, mapped, unmapped));
    }

    private static DerivedMotionReport Report(DerivedMotionOptions options, AnimationClip sourceClip, long outputFrameCount,
        IReadOnlyList<int?> map, double position, double angular, double linear, bool unrepresentable,
        ImmutableArray<string>.Builder diagnostics, int? mapped = null, int? unmapped = null)
    {
        int mappedCount = mapped ?? map.Count(index => index is not null);
        int unmappedCount = unmapped ?? map.Count - mappedCount;
        return new(Algorithm, mappedCount, unmappedCount, options.SampleMultiplier, sourceClip.FrameCount, outputFrameCount,
            new FrameRate(checked(sourceClip.FrameRate.Numerator * options.SampleMultiplier), sourceClip.FrameRate.Denominator),
            position, angular, linear, options.PositionTolerance, options.AngularTolerance, options.LinearTolerance,
            position > options.PositionTolerance || angular > options.AngularTolerance || linear > options.LinearTolerance,
            unrepresentable, diagnostics.ToImmutable());
    }
}
