using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Ik;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Evaluation;

public enum EvaluationPurpose
{
    Export,
    Preview,
}

public enum EvaluationDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record EvaluationDiagnostic(
    string Code,
    EvaluationDiagnosticSeverity Severity,
    string Message);

public enum Dl1PreviewStageStatus
{
    Applied,
    Fallback,
    Bypassed,
    Disabled,
    Unavailable,
}

public sealed record Dl1PreviewStageReport(
    string StageId,
    bool Requested,
    Dl1PreviewStageStatus Status,
    string Message);

public enum EvaluatedCameraSource
{
    ProfileBone,
    Dl1FppEyeCamera,
    Dl1MovieReferenceCamera,
}

public sealed record EvaluatedCamera(
    TransformMatrix WorldTransform,
    CameraLens Lens,
    bool IsFirstPerson,
    EvaluatedCameraSource Source = EvaluatedCameraSource.ProfileBone,
    Dl1ProjectionParameters? HandsProjection = null);

public sealed record EvaluatedCameraHelper(
    string Role,
    string BoneName,
    int BoneIndex,
    TransformMatrix WorldTransform);

public sealed record EvaluatedAttachment(
    Guid BindingId,
    Guid AssetId,
    string Name,
    TransformMatrix WorldTransform,
    AttachmentScope Scope);

/// <summary>
/// Immutable input to the single animation evaluation path used by export and preview.
/// </summary>
public sealed class EvaluationRequest
{
    public EvaluationRequest(
        RigDefinition sourceRig,
        RigDefinition targetRig,
        AnimationClip clip,
        double timeSeconds,
        PreviewProfile previewProfile,
        RetargetMap? retargetMap = null,
        IEnumerable<BoneEditLayer>? editLayers = null,
        IEnumerable<TwoBoneIkConstraint>? ikConstraints = null,
        PlaybackMode playbackMode = PlaybackMode.Clamp,
        EvaluationPurpose purpose = EvaluationPurpose.Preview,
        IEnumerable<AttachmentBinding>? attachments = null,
        Dl1AuthoringPolicy? dl1AuthoringPolicy = null,
        IEnumerable<MorphChannelBinding>? morphBindings = null,
        IEnumerable<MorphEditLayer>? morphEditLayers = null,
        IEnumerable<IkConstraintLayer>? ikLayers = null,
        Dl1PreviewInputs? dl1PreviewInputs = null,
        bool previewMotionAccumulationEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(previewProfile);
        if (!double.IsFinite(timeSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(timeSeconds));
        }

        SourceRig = sourceRig;
        TargetRig = targetRig;
        Clip = clip;
        TimeSeconds = timeSeconds;
        PreviewProfile = previewProfile;
        RetargetMap = retargetMap;
        EditLayers = editLayers?.ToImmutableArray() ?? [];
        IkConstraints = ikConstraints?.ToImmutableArray() ?? [];
        PlaybackMode = playbackMode;
        Purpose = purpose;
        Attachments = attachments?.ToImmutableArray() ?? [];
        Dl1AuthoringPolicy = dl1AuthoringPolicy;
        MorphBindings = morphBindings?.ToImmutableArray() ?? [];
        MorphEditLayers = morphEditLayers?.ToImmutableArray() ?? [];
        IkLayers = ikLayers?.ToImmutableArray() ?? [];
        Dl1PreviewInputs = dl1PreviewInputs ?? Dl1PreviewInputs.Empty;
        PreviewMotionAccumulationEnabled = previewMotionAccumulationEnabled;
    }

    public RigDefinition SourceRig { get; }

    public RigDefinition TargetRig { get; }

    public AnimationClip Clip { get; }

    public double TimeSeconds { get; }

    public PreviewProfile PreviewProfile { get; }

    public RetargetMap? RetargetMap { get; }

    public ImmutableArray<BoneEditLayer> EditLayers { get; }

    public ImmutableArray<TwoBoneIkConstraint> IkConstraints { get; }

    public PlaybackMode PlaybackMode { get; }

    public EvaluationPurpose Purpose { get; }

    public ImmutableArray<AttachmentBinding> Attachments { get; }

    public Dl1AuthoringPolicy? Dl1AuthoringPolicy { get; }

    public ImmutableArray<MorphChannelBinding> MorphBindings { get; }

    public ImmutableArray<MorphEditLayer> MorphEditLayers { get; }

    public ImmutableArray<IkConstraintLayer> IkLayers { get; }

    public Dl1PreviewInputs Dl1PreviewInputs { get; }

    public bool PreviewMotionAccumulationEnabled { get; }
}

/// <summary>
/// Renderer- and exporter-facing evaluated state. <see cref="AuthoredPose"/> is
/// the only exportable pose. <see cref="DisplayPose"/> may include preview-only
/// layers and must never be serialized into animation output.
/// </summary>
public sealed class EvaluationFrame
{
    public EvaluationFrame(
        double sampleFrame,
        SkeletonPose authoredPose,
        SkeletonPose displayPose,
        ImmutableDictionary<string, double> authoredMorphWeights,
        ImmutableDictionary<string, double> displayMorphWeights,
        PreviewProfile previewProfile,
        EvaluatedCamera? camera,
        ImmutableArray<EvaluatedAttachment> authoredAttachments,
        ImmutableArray<EvaluatedAttachment> displayAttachments,
        CompatibilityReport? compatibility,
        IEnumerable<EvaluationDiagnostic> diagnostics,
        IEnumerable<Dl1PreviewStageReport>? dl1PreviewStages = null,
        IEnumerable<EvaluatedCameraHelper>? cameraHelpers = null,
        SkeletonPose? rawSourcePose = null,
        TransformTRS? auxiliaryMotion = null,
        TransformMatrix? actorWorldTransform = null,
        ImmutableDictionary<string, double>? rawSourceMorphWeights = null)
    {
        ArgumentNullException.ThrowIfNull(authoredPose);
        ArgumentNullException.ThrowIfNull(displayPose);
        ArgumentNullException.ThrowIfNull(authoredMorphWeights);
        ArgumentNullException.ThrowIfNull(displayMorphWeights);
        ArgumentNullException.ThrowIfNull(previewProfile);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (authoredAttachments.IsDefault || displayAttachments.IsDefault)
        {
            throw new ArgumentException("Attachment results must be initialized.");
        }

        SampleFrame = sampleFrame;
        AuthoredPose = authoredPose;
        DisplayPose = displayPose;
        AuthoredMorphWeights = authoredMorphWeights;
        DisplayMorphWeights = displayMorphWeights;
        PreviewProfile = previewProfile;
        Camera = camera;
        AuthoredAttachments = authoredAttachments;
        DisplayAttachments = displayAttachments;
        Compatibility = compatibility;
        Diagnostics = diagnostics.ToImmutableArray();
        Dl1PreviewStages = dl1PreviewStages?.ToImmutableArray() ?? [];
        CameraHelpers = cameraHelpers?.ToImmutableArray() ?? [];
        RawSourcePose = rawSourcePose ?? authoredPose;
        RawSourceMorphWeights = rawSourceMorphWeights ??
            authoredMorphWeights;
        AuxiliaryMotion = auxiliaryMotion;
        ActorWorldTransform = actorWorldTransform ?? TransformMatrix.Identity;
    }

    public double SampleFrame { get; }

    public SkeletonPose AuthoredPose { get; }

    public SkeletonPose DisplayPose { get; }

    /// <summary>
    /// Exact decoded source pose before retargeting, edits, IK, and DL1 policy.
    /// </summary>
    public SkeletonPose RawSourcePose { get; }

    public ImmutableDictionary<string, double>
        RawSourceMorphWeights
    { get; }

    public TransformTRS? AuxiliaryMotion { get; }

    /// <summary>
    /// Preview-only actor/world placement derived from auxiliary accumulation.
    /// It never alters skeletal locals or exported animation tracks.
    /// </summary>
    public TransformMatrix ActorWorldTransform { get; }

    public ImmutableDictionary<string, double> AuthoredMorphWeights { get; }

    public ImmutableDictionary<string, double> DisplayMorphWeights { get; }

    /// <summary>
    /// Renderer-facing compatibility alias. Export evaluations have identical
    /// authored and display dictionaries.
    /// </summary>
    public ImmutableDictionary<string, double> MorphWeights =>
        DisplayMorphWeights;

    public PreviewProfile PreviewProfile { get; }

    public EvaluatedCamera? Camera { get; }

    public ImmutableArray<EvaluatedAttachment> AuthoredAttachments { get; }

    public ImmutableArray<EvaluatedAttachment> DisplayAttachments { get; }

    public CompatibilityReport? Compatibility { get; }

    public ImmutableArray<EvaluationDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Explicit DL1 preview-only stage state for toggle and availability UI.
    /// Export evaluations always return an empty collection.
    /// </summary>
    public ImmutableArray<Dl1PreviewStageReport> Dl1PreviewStages { get; }

    /// <summary>
    /// Evaluated player-camera helper transforms. These are display metadata
    /// only and are never animation export tracks.
    /// </summary>
    public ImmutableArray<EvaluatedCameraHelper> CameraHelpers { get; }
}

public interface IAnimationEvaluator
{
    EvaluationFrame Evaluate(EvaluationRequest request);
}
