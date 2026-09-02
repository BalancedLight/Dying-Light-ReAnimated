using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Retargeting.Conformance;

/// <summary>
/// The anatomical region a measured segment belongs to. Regions are reported
/// separately because a stylised model routinely matches DL1 in one region and
/// not another, and a single averaged number would hide that.
/// </summary>
public enum RigScaleRegion
{
    Leg = 0,
    Torso = 1,
    Arm = 2,

    /// <summary>
    /// A cross-body span such as shoulder or hip width. Measured and reported,
    /// but deliberately excluded from the scale solve: width scales
    /// independently of limb length, and including it skews the fit.
    /// </summary>
    Width = 3,
}

/// <summary>
/// One corresponding bone segment measured on both rigs.
/// </summary>
public sealed record RigScaleSample(
    string FromRole,
    string ToRole,
    RigScaleRegion Region,
    double TemplateLength,
    double SourceLength,
    double Ratio);

/// <summary>Aggregate agreement for one anatomical region.</summary>
public sealed record RigRegionFit(
    RigScaleRegion Region,
    int SampleCount,
    double Ratio,
    double RelativeErrorAtSolvedScale);

/// <summary>
/// The solved placement of a DL1 template onto a source model.
/// </summary>
public sealed record RigLandmarkSolution
{
    /// <summary>
    /// Uniform scale applied to every template rest offset, from a
    /// length-weighted least-squares fit over longitudinal segments. Long
    /// bones dominate because they dominate visible distortion.
    /// </summary>
    public required double UniformScale { get; init; }

    /// <summary>
    /// Where the template's pelvis is pinned, in source model space. The
    /// template root is placed relative to this, never at the source root:
    /// DL1's <c>bip01</c> is co-located with <c>pelvis</c> at hip height,
    /// while a Character Creator <c>RL_BoneRoot</c> sits on the floor.
    /// </summary>
    public required Vector3D PelvisAnchor { get; init; }

    public required ImmutableArray<RigScaleSample> Samples { get; init; }

    public required ImmutableArray<RigRegionFit> RegionFits { get; init; }

    /// <summary>
    /// Length-weighted relative residual of the solved scale. This is the
    /// headline "how far from DL1 proportions is this model" number.
    /// </summary>
    public required double ProportionResidual { get; init; }

    /// <summary>
    /// Largest relative departure of any single longitudinal sample from the
    /// solved scale.
    /// </summary>
    public required double WorstSampleDeviation { get; init; }

    public bool IsScaleOverridden { get; init; }

    public bool IsAnchorOverridden { get; init; }

    public required string Evidence { get; init; }
}

public sealed record RigLandmarkOptions
{
    /// <summary>An explicit uniform scale, bypassing the segment solve.</summary>
    public double? ScaleOverride { get; init; }

    /// <summary>An explicit pelvis anchor in source model space.</summary>
    public Vector3D? PelvisAnchorOverride { get; init; }

    /// <summary>
    /// Restricts the scale solve to one region. Fitting on
    /// <see cref="RigScaleRegion.Leg"/> is the useful special case: locomotion
    /// clips plant the feet at template proportions, so a leg-accurate scale
    /// keeps a character from floating or sinking even when its torso and arms
    /// are stylised.
    /// </summary>
    public RigScaleRegion? RestrictScaleToRegion { get; init; }
}

/// <summary>
/// Solves the uniform scale and root placement that put a DL1 target skeleton
/// onto a specific source model.
/// </summary>
public static class RigLandmarkSolver
{
    private const double MinimumSegmentLength = 1e-4;

    /// <summary>
    /// Corresponding segments measured on both rigs, in shared humanoid roles.
    /// Distances are taken between joint positions rather than along the
    /// hierarchy, so intermediate helper rows on either side never change a
    /// measurement.
    /// </summary>
    private static readonly (string From, string To, RigScaleRegion Region)[] Segments =
    [
        ("leg.left.upper", "leg.left.lower", RigScaleRegion.Leg),
        ("leg.left.lower", "foot.left", RigScaleRegion.Leg),
        ("leg.right.upper", "leg.right.lower", RigScaleRegion.Leg),
        ("leg.right.lower", "foot.right", RigScaleRegion.Leg),
        ("body.pelvis", "foot.left", RigScaleRegion.Leg),
        ("body.pelvis", "foot.right", RigScaleRegion.Leg),
        ("foot.left", "toe.left", RigScaleRegion.Leg),
        ("foot.right", "toe.right", RigScaleRegion.Leg),

        ("body.pelvis", "body.spine.1", RigScaleRegion.Torso),
        ("body.spine.1", "body.spine.2", RigScaleRegion.Torso),
        ("body.spine.2", "body.neck.0", RigScaleRegion.Torso),
        ("body.neck.0", "body.head", RigScaleRegion.Torso),
        ("body.pelvis", "body.head", RigScaleRegion.Torso),

        ("arm.left.upper", "arm.left.lower", RigScaleRegion.Arm),
        ("arm.left.lower", "hand.left", RigScaleRegion.Arm),
        ("arm.right.upper", "arm.right.lower", RigScaleRegion.Arm),
        ("arm.right.lower", "hand.right", RigScaleRegion.Arm),
        ("arm.left.clavicle", "arm.left.upper", RigScaleRegion.Arm),
        ("arm.right.clavicle", "arm.right.upper", RigScaleRegion.Arm),

        ("arm.left.upper", "arm.right.upper", RigScaleRegion.Width),
        ("leg.left.upper", "leg.right.upper", RigScaleRegion.Width),
        ("arm.left.clavicle", "arm.right.clavicle", RigScaleRegion.Width),
    ];

    public static RigLandmarkSolution Solve(
        Dl1RigTemplate template,
        RigDefinition sourceRig,
        RigCorrespondence correspondence,
        RigLandmarkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(correspondence);
        options ??= new RigLandmarkOptions();

        ImmutableArray<TransformMatrix> sourceGlobals =
            sourceRig.CreateBindPose().GlobalMatrices;
        Dictionary<string, (int Template, int Source)> byRole =
            BuildRoleIndex(correspondence);

        var samples = ImmutableArray.CreateBuilder<RigScaleSample>();
        foreach ((string from, string to, RigScaleRegion region) in Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byRole.TryGetValue(from, out (int Template, int Source) a) ||
                !byRole.TryGetValue(to, out (int Template, int Source) b))
            {
                continue;
            }

            double templateLength = Vector3D.Distance(
                template[a.Template].GlobalRestMatrix.Translation,
                template[b.Template].GlobalRestMatrix.Translation);
            double sourceLength = Vector3D.Distance(
                sourceGlobals[a.Source].Translation,
                sourceGlobals[b.Source].Translation);
            if (!double.IsFinite(templateLength) ||
                !double.IsFinite(sourceLength) ||
                templateLength < MinimumSegmentLength ||
                sourceLength < MinimumSegmentLength)
            {
                continue;
            }

            samples.Add(new RigScaleSample(
                from,
                to,
                region,
                templateLength,
                sourceLength,
                sourceLength / templateLength));
        }

        ImmutableArray<RigScaleSample> measured = samples.ToImmutable();
        ImmutableArray<RigScaleSample> fitting = measured
            .Where(sample =>
                sample.Region != RigScaleRegion.Width &&
                (options.RestrictScaleToRegion is not { } region ||
                 sample.Region == region))
            .ToImmutableArray();

        double solvedScale = SolveWeightedScale(fitting);
        double scale = options.ScaleOverride ?? solvedScale;
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            throw new InvalidOperationException(
                "The solved rig conformance scale is not a positive finite number.");
        }

        return new RigLandmarkSolution
        {
            UniformScale = scale,
            PelvisAnchor = options.PelvisAnchorOverride ??
                ResolveAnchor(byRole, sourceGlobals, sourceRig),
            Samples = measured,
            RegionFits = BuildRegionFits(measured, scale),
            ProportionResidual = ComputeWeightedResidual(fitting, scale),
            WorstSampleDeviation = fitting.IsEmpty
                ? 0.0
                : fitting.Max(sample => Math.Abs(sample.Ratio - scale)) / scale,
            IsScaleOverridden = options.ScaleOverride.HasValue,
            IsAnchorOverridden = options.PelvisAnchorOverride.HasValue,
            Evidence = BuildEvidence(fitting, solvedScale, options),
        };
    }

    /// <summary>
    /// Least-squares scale minimising the summed squared length error,
    /// which is <c>sum(t*s) / sum(t*t)</c> over template lengths <c>t</c> and
    /// source lengths <c>s</c>. Long bones therefore carry more weight than
    /// short ones, matching how much each contributes to visible distortion
    /// when a stock clip imposes template proportions.
    /// </summary>
    private static double SolveWeightedScale(ImmutableArray<RigScaleSample> samples)
    {
        double numerator = 0.0;
        double denominator = 0.0;
        foreach (RigScaleSample sample in samples)
        {
            numerator += sample.TemplateLength * sample.SourceLength;
            denominator += sample.TemplateLength * sample.TemplateLength;
        }

        return denominator > 1e-12 ? numerator / denominator : 1.0;
    }

    private static double ComputeWeightedResidual(
        ImmutableArray<RigScaleSample> samples,
        double scale)
    {
        double weighted = 0.0;
        double total = 0.0;
        foreach (RigScaleSample sample in samples)
        {
            double expected = sample.TemplateLength * scale;
            weighted += sample.TemplateLength *
                Math.Abs(sample.SourceLength - expected) /
                Math.Max(expected, 1e-9);
            total += sample.TemplateLength;
        }

        return total > 1e-12 ? weighted / total : 0.0;
    }

    private static ImmutableArray<RigRegionFit> BuildRegionFits(
        ImmutableArray<RigScaleSample> samples,
        double scale)
    {
        var fits = ImmutableArray.CreateBuilder<RigRegionFit>();
        foreach (RigScaleRegion region in Enum.GetValues<RigScaleRegion>())
        {
            ImmutableArray<RigScaleSample> inRegion = samples
                .Where(sample => sample.Region == region)
                .ToImmutableArray();
            if (inRegion.IsEmpty)
            {
                continue;
            }

            double regionScale = SolveWeightedScale(inRegion);
            fits.Add(new RigRegionFit(
                region,
                inRegion.Length,
                regionScale,
                Math.Abs(regionScale - scale) / scale));
        }

        return fits.ToImmutable();
    }

    private static string BuildEvidence(
        ImmutableArray<RigScaleSample> fitting,
        double solvedScale,
        RigLandmarkOptions options)
    {
        if (fitting.IsEmpty)
        {
            return "no corresponding segments were measurable; the scale defaulted to 1.0";
        }

        string restriction = options.RestrictScaleToRegion is { } region
            ? $" restricted to {region.ToString().ToLowerInvariant()} segments"
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"length-weighted least-squares fit over {fitting.Length} segments{restriction} = {solvedScale:F4}");
    }

    private static Dictionary<string, (int Template, int Source)> BuildRoleIndex(
        RigCorrespondence correspondence)
    {
        var byRole = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (RigCorrespondenceRow row in correspondence.Rows)
        {
            if (row.Disposition == RigBoneDisposition.Mapped &&
                row.Role is { } role &&
                row.TemplateIndex >= 0 &&
                row.SourceBoneIndex >= 0)
            {
                byRole[role] = (row.TemplateIndex, row.SourceBoneIndex);
            }
        }

        return byRole;
    }

    /// <summary>
    /// Prefers the source bone mapped to the pelvis, falling back to the root.
    /// Anchoring on the pelvis is what keeps a floor-origin source root from
    /// dragging the whole DL1 hierarchy down to the ground plane.
    /// </summary>
    private static Vector3D ResolveAnchor(
        Dictionary<string, (int Template, int Source)> byRole,
        ImmutableArray<TransformMatrix> sourceGlobals,
        RigDefinition sourceRig)
    {
        if (byRole.TryGetValue("body.pelvis", out (int Template, int Source) pelvis))
        {
            return sourceGlobals[pelvis.Source].Translation;
        }

        if (byRole.TryGetValue("body.root", out (int Template, int Source) root))
        {
            return sourceGlobals[root.Source].Translation;
        }

        return sourceRig.BoneCount > 0
            ? sourceGlobals[0].Translation
            : Vector3D.Zero;
    }
}
