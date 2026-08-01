using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Evaluation;

public sealed class PreviewProceduralContext
{
    public PreviewProceduralContext(
        SkeletonPose authoredPose,
        SkeletonPose displayPose,
        double sampleFrame,
        PreviewProfile previewProfile,
        ImmutableDictionary<string, double> morphWeights)
    {
        ArgumentNullException.ThrowIfNull(authoredPose);
        ArgumentNullException.ThrowIfNull(displayPose);
        ArgumentNullException.ThrowIfNull(previewProfile);
        ArgumentNullException.ThrowIfNull(morphWeights);

        AuthoredPose = authoredPose;
        DisplayPose = displayPose;
        SampleFrame = sampleFrame;
        PreviewProfile = previewProfile;
        MorphWeights = morphWeights;
    }

    public SkeletonPose AuthoredPose { get; }

    public SkeletonPose DisplayPose { get; }

    public double SampleFrame { get; }

    public PreviewProfile PreviewProfile { get; }

    public ImmutableDictionary<string, double> MorphWeights { get; }
}

public sealed class PreviewProceduralResult
{
    public PreviewProceduralResult(
        SkeletonPose displayPose,
        IEnumerable<EvaluationDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(displayPose);
        DisplayPose = displayPose;
        Diagnostics = diagnostics?.ToImmutableArray() ?? [];
    }

    public SkeletonPose DisplayPose { get; }

    public ImmutableArray<EvaluationDiagnostic> Diagnostics { get; }
}

/// <summary>
/// A display-only procedural stage. It is never invoked by export evaluation.
/// </summary>
public interface IPreviewProceduralStage
{
    string Id { get; }

    bool IsEnabled(PreviewProfile profile);

    PreviewProceduralResult Apply(PreviewProceduralContext context);
}

/// <summary>
/// Concrete preview compensation for a versioned DL1 profile offset.
/// </summary>
public sealed class ConstantBoneOffsetPreviewStage : IPreviewProceduralStage
{
    public ConstantBoneOffsetPreviewStage(
        string id,
        int boneIndex,
        TransformTRS additiveOffset,
        AuthoringPreviewFidelity requiredFeature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(boneIndex);
        if (!additiveOffset.IsFinite)
        {
            throw new ArgumentException("The procedural offset must be finite.", nameof(additiveOffset));
        }

        Id = id;
        BoneIndex = boneIndex;
        AdditiveOffset = additiveOffset.Normalized();
        RequiredFeature = requiredFeature;
    }

    public string Id { get; }

    public int BoneIndex { get; }

    public TransformTRS AdditiveOffset { get; }

    public AuthoringPreviewFidelity RequiredFeature { get; }

    public bool IsEnabled(PreviewProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Fidelity.HasFlag(RequiredFeature) &&
               (profile.ProceduralToggles.IsEmpty ||
                profile.ProceduralToggles.Contains(Id, StringComparer.Ordinal));
    }

    public PreviewProceduralResult Apply(PreviewProceduralContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if ((uint)BoneIndex >= (uint)context.DisplayPose.Rig.BoneCount)
        {
            return new PreviewProceduralResult(
                context.DisplayPose,
                [
                    new(
                        "preview_procedural_bone_missing",
                        EvaluationDiagnosticSeverity.Error,
                        $"Preview stage '{Id}' refers to missing bone index {BoneIndex}."),
                ]);
        }

        TransformTRS current = context.DisplayPose.LocalTransforms[BoneIndex];
        TransformTRS adjusted = new(
            current.Translation + AdditiveOffset.Translation,
            (current.Rotation * AdditiveOffset.Rotation).Normalized(),
            Vector3D.ComponentMultiply(current.Scale, AdditiveOffset.Scale));
        return new PreviewProceduralResult(
            context.DisplayPose.WithLocalTransform(BoneIndex, adjusted));
    }
}
