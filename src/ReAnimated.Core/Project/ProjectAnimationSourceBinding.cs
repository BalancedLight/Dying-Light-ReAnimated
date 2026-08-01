using ReAnimated.Core.Domain;

namespace ReAnimated.Core.Project;

/// <summary>
/// Immutable source interpretation for an animation document. Target changes
/// never update this record; rebinding creates a new project animation.
/// </summary>
public sealed record ProjectAnimationSourceBinding
{
    public AnimationSourceKind Kind { get; init; }

    public Guid AssetId { get; init; }

    public AnimationSourceRoles Roles { get; init; }

    public string SourceRigSignature { get; init; } = string.Empty;

    /// <summary>
    /// Exact retail mesh identity used to partition an ANM2. Required for new
    /// local and retail ANM2 bindings; absent legacy bindings remain loadable
    /// but fail closed until Rebind Source creates a new document.
    /// </summary>
    public Guid? RetailSourceModelAssetId { get; init; }

    public AnimationTimingProvenance TimingProvenance { get; init; } =
        AnimationTimingProvenance.Manual30FpsFallback;

    public double? SourceRangeStartFrame { get; init; }

    public double? SourceRangeEndFrame { get; init; }

    public string? TimingDetail { get; init; }

    public Anm2TrackPartition? Partition { get; init; }

    public void Validate(
        IReadOnlyDictionary<Guid, ProjectAssetKind> assetKinds,
        string parameterName)
    {
        const AnimationSourceRoles knownRoles =
            AnimationSourceRoles.Body |
            AnimationSourceRoles.Facial |
            AnimationSourceRoles.Auxiliary;
        if (!Enum.IsDefined(Kind) ||
            Roles == AnimationSourceRoles.None ||
            (Roles & ~knownRoles) != 0 ||
            !Enum.IsDefined(TimingProvenance))
        {
            throw new ArgumentException(
                "Animation source binding contains an unsupported kind, role, or timing provenance.",
                parameterName);
        }

        bool invalidRange =
            SourceRangeStartFrame.HasValue !=
                SourceRangeEndFrame.HasValue;
        if (SourceRangeStartFrame is { } startFrame &&
            SourceRangeEndFrame is { } endFrame)
        {
            invalidRange = !double.IsFinite(startFrame) ||
                startFrame < 0.0 ||
                !double.IsFinite(endFrame) ||
                endFrame < startFrame;
        }

        if (invalidRange || TimingDetail is { Length: > 1_024 })
        {
            throw new ArgumentException(
                "Animation source timing range or provenance detail is invalid.",
                parameterName);
        }

        if (!assetKinds.TryGetValue(AssetId, out ProjectAssetKind sourceKind))
        {
            throw new ArgumentException(
                "Animation source binding refers to an unknown project asset.",
                parameterName);
        }

        ProjectAssetKind requiredKind = Kind == AnimationSourceKind.RetailAnm2
            ? ProjectAssetKind.RetailGameResource
            : ProjectAssetKind.SourceAnimation;
        if (sourceKind != requiredKind)
        {
            throw new ArgumentException(
                $"Animation source kind '{Kind}' requires a {requiredKind} project asset.",
                parameterName);
        }

        if (SourceRigSignature.Length != 64 ||
            SourceRigSignature.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "An animation source binding requires an exact source-rig SHA-256 signature.",
                parameterName);
        }

        bool anm2 = Kind is
            AnimationSourceKind.LocalAnm2 or
            AnimationSourceKind.RetailAnm2;
        if (anm2)
        {
            if (RetailSourceModelAssetId is not { } sourceModelId ||
                !assetKinds.TryGetValue(sourceModelId, out ProjectAssetKind modelKind) ||
                modelKind != ProjectAssetKind.RetailGameResource)
            {
                throw new ArgumentException(
                    "A new ANM2 source binding requires its exact retail source-model identity.",
                    parameterName);
            }

            Partition?.Validate();
            if (Partition is null)
            {
                throw new ArgumentException(
                    "A new ANM2 source binding requires its decoded track partition.",
                    parameterName);
            }
        }
        else if (RetailSourceModelAssetId is not null || Partition is not null)
        {
            throw new ArgumentException(
                "FBX source bindings cannot carry an ANM2 source-model or partition.",
                parameterName);
        }
    }
}
