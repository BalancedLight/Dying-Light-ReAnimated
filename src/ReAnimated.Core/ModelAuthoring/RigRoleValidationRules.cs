using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Executable declared constraints; their presence never certifies native evidence.</summary>
public sealed record RigRoleValidationRules
{
    public RigRoleEditCheck? Edits { get; init; }
    public RigRoleFrameCheck? Frame { get; init; }
    public RigRoleBoundsCheck? Bounds { get; init; }
    public RigRoleChannelCheck? Channels { get; init; }
    public RigRoleRetentionCheck? Retention { get; init; }
    public void Validate()
    { Frame?.Validate(); Bounds?.Validate(); Channels?.Validate(); Retention?.Validate(); }
}

public sealed record RigRoleFrameCheck
{
    public ImmutableArray<RigFramePolicy> AllowedPolicies { get; init; } = [];
    public bool PreserveSourceGlobal { get; init; }
    public bool RequireOrthonormal { get; init; }
    public string? OriginRoleId { get; init; }
    /// <summary>Offset in the origin role's prepared local coordinates, in authoring metres.</summary>
    public Vector3D OriginOffset { get; init; }
    public string? DirectionRoleId { get; init; }
    public Vector3D LocalAxis { get; init; } = Vector3D.UnitX;
    public double PositionToleranceMetres { get; init; } = 1e-4;
    public double MatrixTolerance { get; init; } = 1e-5;
    public double AngularToleranceDegrees { get; init; } = 1;
    public IEnumerable<string> GetReferenceRoleIds() => new[] { OriginRoleId, DirectionRoleId }.OfType<string>().Distinct(StringComparer.Ordinal);
    public void Validate()
    {
        RigRuleChecks.Values(AllowedPolicies, nameof(AllowedPolicies));
        RigContractRules.OptionalText(OriginRoleId, nameof(OriginRoleId));
        RigContractRules.OptionalText(DirectionRoleId, nameof(DirectionRoleId));
        if (!OriginOffset.IsFinite || !LocalAxis.TryNormalize(out _) ||
            !double.IsFinite(PositionToleranceMetres) || PositionToleranceMetres <= 0 ||
            !double.IsFinite(MatrixTolerance) || MatrixTolerance <= 0 || MatrixTolerance > .01 ||
            !double.IsFinite(AngularToleranceDegrees) || AngularToleranceDegrees <= 0 || AngularToleranceDegrees > 180)
            throw new ArgumentException("Frame checks require finite offsets, nonzero axes and positive bounded tolerances.");
        if (OriginRoleId is null && OriginOffset != Vector3D.Zero)
            throw new ArgumentException("An origin offset needs its reference role.");
    }
}

public sealed record RigRoleBoundsCheck
{
    public ImmutableArray<RigBoundsPolicy> AllowedPolicies { get; init; } = [];
    public Vector3D MinimumHalfExtents { get; init; }
    public Vector3D? MaximumHalfExtents { get; init; }
    public double ToleranceMetres { get; init; } = 1e-5;
    public void Validate()
    {
        RigRuleChecks.Values(AllowedPolicies, nameof(AllowedPolicies));
        var min = MinimumHalfExtents;
        if (!min.IsFinite || min.X < 0 || min.Y < 0 || min.Z < 0 ||
            MaximumHalfExtents is { } max && (!max.IsFinite || max.X < min.X || max.Y < min.Y || max.Z < min.Z) ||
            !double.IsFinite(ToleranceMetres) || ToleranceMetres <= 0)
            throw new ArgumentException("Bounds checks require finite ordered nonnegative extents and a positive tolerance.");
    }
}

public sealed record RigRoleChannelCheck
{
    public RigAnimationComponents EmittedMask { get; init; }
    public ImmutableArray<RigComponentOwner> PositionOwners { get; init; } = [];
    public ImmutableArray<RigComponentOwner> RotationOwners { get; init; } = [];
    public ImmutableArray<RigComponentOwner> ScaleOwners { get; init; } = [];
    public void Validate()
    {
        if ((EmittedMask & ~(RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale)) != 0)
            throw new ArgumentException("Unknown emitted animation components.");
        RigRuleChecks.Values(PositionOwners, nameof(PositionOwners)); RigRuleChecks.Values(RotationOwners, nameof(RotationOwners)); RigRuleChecks.Values(ScaleOwners, nameof(ScaleOwners));
        if (PositionOwners.Contains(RigComponentOwner.Unknown) || RotationOwners.Contains(RigComponentOwner.Unknown) || ScaleOwners.Contains(RigComponentOwner.Unknown))
            throw new ArgumentException("Executable channel checks need resolved ownership declarations.");
    }
}

public sealed record RigRoleRetentionCheck
{
    public bool MustEmit { get; init; } = true;
    public RigAnimationLod AnimationLod { get; init; }
    public void Validate() => RigContractRules.Defined(AnimationLod, nameof(AnimationLod));
}

internal static class RigRuleChecks
{
    internal static void Values<T>(ImmutableArray<T> values, string name) where T : struct, Enum
    {
        if (values.IsDefaultOrEmpty || values.Distinct().Count() != values.Length)
            throw new ArgumentException("Rule values must be nonempty and unique.", name);
        foreach (T value in values) RigContractRules.Defined(value, name);
    }
}
