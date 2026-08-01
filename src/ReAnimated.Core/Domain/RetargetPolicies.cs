namespace ReAnimated.Core.Domain;

/// <summary>
/// Distinguishes the primary skeletal solve from target-specific helper rows
/// that are evaluated after the body pose.
/// </summary>
public enum RetargetMappingKind
{
    Bone = 0,
    HelperOverride = 1,
}

/// <summary>
/// Selects the transform basis used to move a source track onto a target
/// track. The first value preserves the behavior of projects created before
/// policy-aware helper retargeting was introduced.
/// </summary>
public enum RetargetTransferPolicy
{
    GlobalBindBasis = 0,
    RestRelative = 1,
    RotationDelta = 2,
    CopyLocal = 3,
    Bind = 4,
    /// <summary>
    /// Transfers the source bone's bind-relative rotation in model space,
    /// then resolves it back through the evaluated target parent. This keeps
    /// target translations and scales while accounting for different local
    /// bone axes between otherwise corresponding rigs.
    /// </summary>
    GlobalRotationDelta = 5,
    /// <summary>
    /// Reconstructs a humanoid bone from mapped anatomical joint directions in
    /// the animated body frame, then resolves the desired model-space
    /// orientation through the target hierarchy. This is the DL1 cross-rig
    /// body policy used when both rigs expose the required semantic chain.
    /// </summary>
    AnatomicalDirection = 6,
}

/// <summary>
/// Declares which local TRS components a mapping row owns. Components outside
/// the policy remain at the target track's bind value.
/// </summary>
public enum RetargetComponentPolicy
{
    FullTransform = 0,
    Rotation = 1,
    Translation = 2,
    RotationTranslation = 3,
    Scale = 4,
}
