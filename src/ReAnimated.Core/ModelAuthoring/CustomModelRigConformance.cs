using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Which corresponding segments the uniform conformance scale is solved from.
/// </summary>
public enum CustomModelConformanceScaleMode
{
    /// <summary>Every longitudinal segment, weighted by template length.</summary>
    Automatic = 0,

    /// <summary>
    /// Leg segments only. Locomotion clips plant the feet at template
    /// proportions, so a leg-accurate scale keeps a stylised character from
    /// floating or sinking.
    /// </summary>
    Leg = 1,

    Torso = 2,

    Arm = 3,

    /// <summary>An explicit value supplied by the author.</summary>
    Manual = 4,
}

/// <summary>
/// An author's explicit choice of which source bone owns a humanoid role when
/// several claimed it.
/// </summary>
public sealed record CustomModelConformanceRoleOverride
{
    public string Role { get; init; } = string.Empty;

    public string SourceBoneName { get; init; } = string.Empty;

    internal void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Role, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceBoneName, parameterName);
    }
}

/// <summary>
/// A manual world-space placement for one emitted bone, produced by the
/// wizard's refine stage.
/// </summary>
public sealed record CustomModelConformancePositionOverride
{
    public string BoneName { get; init; } = string.Empty;

    public Vector3D Position { get; init; }

    internal void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BoneName, parameterName);
        if (!Position.IsFinite)
        {
            throw new ArgumentException(
                $"Conformance override for '{BoneName}' must be finite.",
                parameterName);
        }
    }
}

/// <summary>
/// The authored settings of a DL1 rig conformance, persisted so the wizard can
/// be reopened and adjusted.
/// </summary>
/// <remarks>
/// <para>
/// Only the <em>decisions</em> are stored, never the conformed bone table. The
/// package already retains its source FBX bytes, so reopening re-imports the
/// model, re-resolves the template, and replays these settings. That keeps the
/// document small and keeps a conformance reproducible from its inputs.
/// </para>
/// <para>
/// <see cref="SourceFbxSha256"/> records which model the settings were solved
/// against. A different hash means the source changed underneath them and the
/// result must be reviewed rather than silently reused.
/// </para>
/// </remarks>
public sealed record CustomModelRigConformance
{
    /// <summary>Stable identity of the target skeleton these settings targeted.</summary>
    public string TemplateId { get; init; } = string.Empty;

    /// <summary>The bounded template profile, currently always <c>player</c>.</summary>
    public string TemplateProfileName { get; init; } = string.Empty;

    /// <summary>The retail resource the template was extracted from.</summary>
    public string TemplateSourceResourceName { get; init; } = string.Empty;

    /// <summary>
    /// SHA-256 of the retail resource payload the template came from. A
    /// different value means the installed game changed.
    /// </summary>
    public string TemplateFingerprint { get; init; } = string.Empty;

    /// <summary>SHA-256 of the source FBX these settings were solved against.</summary>
    public string SourceFbxSha256 { get; init; } = string.Empty;

    /// <summary>
    /// When true, source bones with no DL1 counterpart are removed and their
    /// weights fold into the nearest surviving ancestor.
    /// </summary>
    public bool DropExtraBones { get; init; }

    public ImmutableArray<CustomModelConformanceRoleOverride> RoleOverrides { get; init; } = [];

    public ImmutableArray<string> ExcludedSourceBones { get; init; } = [];

    public CustomModelConformanceScaleMode ScaleMode { get; init; } =
        CustomModelConformanceScaleMode.Automatic;

    /// <summary>
    /// The explicit scale used when <see cref="ScaleMode"/> is
    /// <see cref="CustomModelConformanceScaleMode.Manual"/>.
    /// </summary>
    public double? ManualScale { get; init; }

    /// <summary>
    /// 0 keeps the source model's own segment lengths; 1, the default, imposes
    /// DL1 rest proportions so stock clips play as authored.
    /// </summary>
    public double ConformanceStrength { get; init; } = 1.0;

    public ImmutableArray<CustomModelConformancePositionOverride> PositionOverrides { get; init; } = [];

    public void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TemplateId, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(TemplateProfileName, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(TemplateSourceResourceName, parameterName);
        ProjectAssetReference.ValidateSha256(TemplateFingerprint, parameterName);
        ProjectAssetReference.ValidateSha256(SourceFbxSha256, parameterName);

        if (!Enum.IsDefined(ScaleMode))
        {
            throw new ArgumentException(
                "The conformance scale mode is not supported.",
                parameterName);
        }

        if (RoleOverrides.IsDefault ||
            ExcludedSourceBones.IsDefault ||
            PositionOverrides.IsDefault)
        {
            throw new ArgumentException(
                "Conformance collections must be initialized.",
                parameterName);
        }

        if (!double.IsFinite(ConformanceStrength) ||
            ConformanceStrength is < 0.0 or > 1.0)
        {
            throw new ArgumentException(
                "The conformance strength must be between 0 and 1.",
                parameterName);
        }

        if (ScaleMode == CustomModelConformanceScaleMode.Manual)
        {
            if (ManualScale is not { } scale ||
                !double.IsFinite(scale) ||
                scale <= 0.0)
            {
                throw new ArgumentException(
                    "A manual conformance scale must be a positive finite number.",
                    parameterName);
            }
        }
        else if (ManualScale is not null)
        {
            throw new ArgumentException(
                "A manual scale is only valid with the manual scale mode.",
                parameterName);
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (CustomModelConformanceRoleOverride row in RoleOverrides)
        {
            row.Validate(parameterName);
            if (!roles.Add(row.Role))
            {
                throw new ArgumentException(
                    $"Conformance role override '{row.Role}' is duplicated.",
                    parameterName);
            }
        }

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in ExcludedSourceBones)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
            if (!excluded.Add(name))
            {
                throw new ArgumentException(
                    $"Excluded source bone '{name}' is duplicated.",
                    parameterName);
            }
        }

        var overrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomModelConformancePositionOverride row in PositionOverrides)
        {
            row.Validate(parameterName);
            if (!overrides.Add(row.BoneName))
            {
                throw new ArgumentException(
                    $"Conformance position override for '{row.BoneName}' is duplicated.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// True when these settings were solved against the given source model.
    /// </summary>
    public bool MatchesSource(string? sourceFbxSha256) =>
        !string.IsNullOrWhiteSpace(sourceFbxSha256) &&
        string.Equals(SourceFbxSha256, sourceFbxSha256, StringComparison.OrdinalIgnoreCase);
}
