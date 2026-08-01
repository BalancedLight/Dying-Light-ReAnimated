using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Retargeting.Ik;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Evaluation;

public enum AnimationRootMode
{
    Recorded,
    InPlace,
    Bip01,
    MotionAccumulator,
}

public sealed record RetargetMappingBinding(
    string SourceRigSignature,
    string TargetRigSignature,
    string? TargetAssetFingerprint,
    string MappingFingerprint);

/// <summary>
/// The immutable in-memory animation-authoring document. Body and mimic data
/// share one rational timeline and are combined before entering the single
/// evaluator used by preview and export.
/// </summary>
public sealed class AnimationDocument
{
    public AnimationDocument(
        Guid id,
        string name,
        RigDefinition sourceRig,
        RigDefinition targetRig,
        AnimationClip bodyAnimation,
        AnimationClip? mimicAnimation,
        RetargetMap? mapping,
        AnimationRootMode rootMode,
        PreviewProfile previewProfile,
        IEnumerable<BoneEditLayer>? editLayers = null,
        IEnumerable<TwoBoneIkConstraint>? ikConstraints = null,
        IEnumerable<AttachmentBinding>? attachments = null,
        IEnumerable<MorphChannelBinding>? morphBindings = null,
        IEnumerable<MorphEditLayer>? morphEditLayers = null,
        IEnumerable<IkConstraintLayer>? ikLayers = null,
        Dl1AuthoringPolicy? dl1AuthoringPolicy = null,
        FacialClipTiming? facialTiming = null,
        bool previewMotionAccumulationEnabled = false)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "An animation document requires a stable identifier.",
                nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(bodyAnimation);
        ArgumentNullException.ThrowIfNull(previewProfile);
        ValidateMapping(sourceRig, targetRig, mapping);
        Dl1AuthoringPolicy resolvedDl1Policy =
            dl1AuthoringPolicy ??
            Dl1AuthoringPolicy.Create(
                sourceRig,
                targetRig,
                mapping,
                rootMode);
        resolvedDl1Policy.ValidateFor(sourceRig, targetRig);
        if (resolvedDl1Policy.RootMotion.Mode != rootMode)
        {
            throw new ArgumentException(
                "The DL1 authoring policy root mode differs from the animation document.",
                nameof(dl1AuthoringPolicy));
        }

        ImmutableArray<BoneEditLayer> edits =
            editLayers?.ToImmutableArray() ?? [];
        ImmutableArray<TwoBoneIkConstraint> ik =
            ikConstraints?.ToImmutableArray() ?? [];
        ImmutableArray<AttachmentBinding> attached =
            attachments?.ToImmutableArray() ?? [];
        ImmutableArray<MorphChannelBinding> facialBindings =
            morphBindings?.ToImmutableArray() ?? [];
        ImmutableArray<MorphEditLayer> facialLayers =
            morphEditLayers?.ToImmutableArray() ?? [];
        ImmutableArray<IkConstraintLayer> keyedIk =
            ikLayers?.ToImmutableArray() ?? [];
        if (edits.Select(static layer => layer.Id).Distinct().Count() !=
            edits.Length)
        {
            throw new ArgumentException(
                "Edit layer identifiers must be unique.",
                nameof(editLayers));
        }

        if (attached.Select(static binding => binding.Id).Distinct().Count() !=
            attached.Length)
        {
            throw new ArgumentException(
                "Attachment identifiers must be unique.",
                nameof(attachments));
        }

        if (facialLayers
            .Select(static layer => layer.Id)
            .Distinct()
            .Count() != facialLayers.Length)
        {
            throw new ArgumentException(
                "Facial layer identifiers must be unique.",
                nameof(morphEditLayers));
        }

        if (keyedIk
            .Select(static layer => layer.Id)
            .Distinct()
            .Count() != keyedIk.Length)
        {
            throw new ArgumentException(
                "IK layer identifiers must be unique.",
                nameof(ikLayers));
        }

        Id = id;
        Name = name;
        SourceRig = sourceRig;
        TargetRig = targetRig;
        BodyAnimation = bodyAnimation;
        MimicAnimation = mimicAnimation;
        Mapping = mapping;
        RootMode = rootMode;
        PreviewProfile = previewProfile;
        EditLayers = edits;
        IkConstraints = ik;
        Attachments = attached;
        MorphBindings = facialBindings;
        MorphEditLayers = facialLayers;
        IkLayers = keyedIk;
        Dl1AuthoringPolicy = resolvedDl1Policy;
        FacialTiming = facialTiming;
        PreviewMotionAccumulationEnabled =
            previewMotionAccumulationEnabled;
        SynchronizedAnimation = facialTiming is null
            ? AnimationClipSynchronization.Synchronize(
                bodyAnimation,
                mimicAnimation)
            : AnimationClipSynchronization.Synchronize(
                bodyAnimation,
                mimicAnimation,
                facialTiming);

        string sourceSignature = RigSignature.Compute(sourceRig);
        string targetSignature = RigSignature.Compute(targetRig);
        MappingBinding = new RetargetMappingBinding(
            sourceSignature,
            targetSignature,
            targetRig.SourceAssetFingerprint?.ContentSha256,
            RetargetMapFingerprint.Compute(
                sourceSignature,
                targetSignature,
                targetRig.SourceAssetFingerprint?.ContentSha256,
                mapping));
    }

    public Guid Id { get; }

    public string Name { get; }

    public RigDefinition SourceRig { get; }

    public RigDefinition TargetRig { get; }

    public AnimationClip BodyAnimation { get; }

    public AnimationClip? MimicAnimation { get; }

    public AnimationClip SynchronizedAnimation { get; }

    public RetargetMap? Mapping { get; }

    public RetargetMappingBinding MappingBinding { get; }

    public AnimationRootMode RootMode { get; }

    public PreviewProfile PreviewProfile { get; }

    public ImmutableArray<BoneEditLayer> EditLayers { get; }

    public ImmutableArray<TwoBoneIkConstraint> IkConstraints { get; }

    public ImmutableArray<AttachmentBinding> Attachments { get; }

    public ImmutableArray<MorphChannelBinding> MorphBindings { get; }

    public ImmutableArray<MorphEditLayer> MorphEditLayers { get; }

    public ImmutableArray<IkConstraintLayer> IkLayers { get; }

    public Dl1AuthoringPolicy Dl1AuthoringPolicy { get; }

    public FacialClipTiming? FacialTiming { get; }

    public bool PreviewMotionAccumulationEnabled { get; }

    public EvaluationRequest CreateEvaluationRequest(
        double timeSeconds,
        EvaluationPurpose purpose,
        PlaybackMode playbackMode = PlaybackMode.Clamp,
        Dl1PreviewInputs? dl1PreviewInputs = null) =>
        new(
            sourceRig: SourceRig,
            targetRig: TargetRig,
            clip: SynchronizedAnimation,
            timeSeconds: timeSeconds,
            previewProfile: PreviewProfile,
            retargetMap: Mapping,
            editLayers: EditLayers,
            ikConstraints: IkConstraints,
            playbackMode: playbackMode,
            purpose: purpose,
            attachments: Attachments,
            dl1AuthoringPolicy: Dl1AuthoringPolicy,
            morphBindings: MorphBindings,
            morphEditLayers: MorphEditLayers,
            ikLayers: IkLayers,
            dl1PreviewInputs: dl1PreviewInputs,
            previewMotionAccumulationEnabled:
                PreviewMotionAccumulationEnabled);

    private static void ValidateMapping(
        RigDefinition source,
        RigDefinition target,
        RetargetMap? mapping)
    {
        if (mapping is null)
        {
            return;
        }

        if (!string.Equals(
                mapping.SourceRigId,
                source.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                mapping.TargetRigId,
                target.Id,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The retarget map is not bound to this source and target rig.",
                nameof(mapping));
        }

        foreach (BoneMapEntry entry in mapping.Entries)
        {
            if (entry.SourceBoneIndex >= source.BoneCount ||
                entry.TargetBoneIndex >= target.BoneCount)
            {
                throw new ArgumentException(
                    "The retarget map contains a bone outside its bound rigs.",
                    nameof(mapping));
            }
        }
    }

}
