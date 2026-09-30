using System.Collections.Immutable;
using System.IO;
using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// Measures changes in distances between connected deform-bone origins.
/// This is a preview-space geometry diagnostic, not a claim about the native
/// engine's BSCR channel composition or cloth solver.
/// </summary>
public static class SkeletonSegmentLengthDriftEvaluator
{
    private const float MinimumBindLengthMetres = 0.001f;

    public static SkeletonSegmentLengthDriftReport Compare(
        SkeletonRenderData bind,
        SkeletonRenderData posed)
    {
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(posed);
        if (bind.Bones.Count != posed.Bones.Count)
            throw new ArgumentException("Bind and posed skeletons have different node counts.", nameof(posed));

        var rows = ImmutableArray.CreateBuilder<SkeletonSegmentLengthDrift>();
        for (int index = 0; index < bind.Bones.Count; index++)
        {
            BoneRenderData rest = bind.Bones[index];
            BoneRenderData animated = posed.Bones[index];
            if (!string.Equals(rest.Name, animated.Name, StringComparison.Ordinal) ||
                rest.ParentIndex != animated.ParentIndex || rest.Role != animated.Role)
                throw new ArgumentException("Bind and posed skeleton identities differ.", nameof(posed));
            int parent = rest.ParentIndex;
            if (parent >= bind.Bones.Count)
                throw new ArgumentException("A skeleton segment has an invalid parent index.", nameof(bind));
            if (parent < 0 || rest.Role != BoneRenderRole.Deform ||
                bind.Bones[parent].Role != BoneRenderRole.Deform)
                continue;

            Vector3 restOrigin = Origin(rest.WorldTransform);
            Vector3 restParent = Origin(bind.Bones[parent].WorldTransform);
            Vector3 posedOrigin = Origin(animated.WorldTransform);
            Vector3 posedParent = Origin(posed.Bones[parent].WorldTransform);
            float restLength = Vector3.Distance(restOrigin, restParent);
            float posedLength = Vector3.Distance(posedOrigin, posedParent);
            if (!float.IsFinite(restLength) || !float.IsFinite(posedLength))
                throw new InvalidDataException("A skeleton segment has a non-finite world origin.");
            if (restLength < MinimumBindLengthMetres)
                continue;

            double driftPercent = 100.0 * Math.Abs(posedLength - restLength) / restLength;
            rows.Add(new SkeletonSegmentLengthDrift(
                rest.Name,
                bind.Bones[parent].Name,
                restLength,
                posedLength,
                driftPercent));
        }

        ImmutableArray<SkeletonSegmentLengthDrift> measured = rows.ToImmutable();
        SkeletonSegmentLengthDrift? worst = measured.IsEmpty
            ? null
            : measured.MaxBy(static row => row.AbsoluteDriftPercent);
        if (worst is not null && worst.AbsoluteDriftPercent <= 1e-6)
            worst = null;
        return new SkeletonSegmentLengthDriftReport(
            measured.Length,
            worst?.AbsoluteDriftPercent ?? 0.0,
            worst?.BoneName,
            measured);
    }

    private static Vector3 Origin(Matrix4x4 transform) =>
        new(transform.M41, transform.M42, transform.M43);
}

public sealed record SkeletonSegmentLengthDrift(
    string BoneName,
    string ParentName,
    double BindLengthMetres,
    double PosedLengthMetres,
    double AbsoluteDriftPercent);

public sealed record SkeletonSegmentLengthDriftReport(
    int ComparableSegments,
    double MaximumAbsoluteDriftPercent,
    string? WorstBoneName,
    ImmutableArray<SkeletonSegmentLengthDrift> Segments);
