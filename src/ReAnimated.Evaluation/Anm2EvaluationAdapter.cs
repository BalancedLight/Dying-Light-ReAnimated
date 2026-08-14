using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Evaluation;

public readonly record struct Dl1Anm2TrackSample(
    uint DescriptorHash,
    int BoneIndex,
    TransformTRS LocalTransform);

public readonly record struct Dl1Anm2MorphSample(
    uint DescriptorHash,
    int MorphIndex,
    double Value);

public sealed record Dl1Anm2AuthoringFrame(
    int FrameIndex,
    ImmutableArray<Dl1Anm2TrackSample> Tracks,
    ImmutableArray<Dl1Anm2MorphSample> Morphs);

/// <summary>
/// Storage-neutral authored frames ready for the ANM2 codec boundary.
/// Quaternion-to-DL1 rotation-vector conversion remains the codec's responsibility.
/// </summary>
public sealed record Dl1Anm2AuthoringSequence(
    string Name,
    FrameRate FrameRate,
    ImmutableArray<Dl1Anm2AuthoringFrame> Frames);

public interface IAnm2EvaluationAdapter
{
    Dl1Anm2AuthoringSequence SampleAuthoredFrames(
        EvaluationRequest requestTemplate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Samples only exportable authored state; preview layers and procedural stages
/// are excluded by construction.
/// </summary>
public sealed class Anm2EvaluationAdapter : IAnm2EvaluationAdapter
{
    private readonly IAnimationEvaluator _evaluator;

    public Anm2EvaluationAdapter(IAnimationEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public Dl1Anm2AuthoringSequence SampleAuthoredFrames(
        EvaluationRequest requestTemplate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestTemplate);
        if (requestTemplate.Clip.FrameCount > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                "DL1 ANM2 cannot contain more than 65,535 frames.");
        }

        ValidateDescriptorInventory(
            requestTemplate.TargetRig,
            requestTemplate.Clip,
            requestTemplate.MorphBindings);
        var frames = ImmutableArray.CreateBuilder<Dl1Anm2AuthoringFrame>(
            checked((int)requestTemplate.Clip.FrameCount));
        for (int frameIndex = 0;
             frameIndex < requestTemplate.Clip.FrameCount;
             frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double seconds = requestTemplate.Clip.FrameRate.SecondsForFrame(frameIndex);
            var exportRequest = new EvaluationRequest(
                requestTemplate.SourceRig,
                requestTemplate.TargetRig,
                requestTemplate.Clip,
                seconds,
                requestTemplate.PreviewProfile,
                requestTemplate.RetargetMap,
                requestTemplate.EditLayers,
                requestTemplate.IkConstraints,
                PlaybackMode.Clamp,
                EvaluationPurpose.Export,
                requestTemplate.Attachments,
                requestTemplate.Dl1AuthoringPolicy,
                requestTemplate.MorphBindings,
                requestTemplate.MorphEditLayers,
                requestTemplate.IkLayers);
            EvaluationFrame evaluated = _evaluator.Evaluate(exportRequest);

            var tracks = ImmutableArray.CreateBuilder<Dl1Anm2TrackSample>();
            foreach (BoneDefinition bone in requestTemplate.TargetRig.Bones)
            {
                if (bone.DescriptorHash is uint descriptor)
                {
                    tracks.Add(
                        new(
                            descriptor,
                            bone.Index,
                            evaluated.AuthoredPose.LocalTransforms[bone.Index]));
                }
            }

            var morphs = ImmutableArray.CreateBuilder<Dl1Anm2MorphSample>();
            foreach (MorphChannelDefinition morph in requestTemplate.TargetRig.MorphChannels)
            {
                if (!evaluated.MorphWeights.TryGetValue(morph.Name, out double value))
                {
                    continue;
                }

                morphs.Add(
                    new(
                        morph.DescriptorHash!.Value,
                        morph.Index,
                        value));
            }

            frames.Add(
                new Dl1Anm2AuthoringFrame(
                    frameIndex,
                    tracks.ToImmutable(),
                    morphs.ToImmutable()));
        }

        return new Dl1Anm2AuthoringSequence(
            requestTemplate.Clip.Name,
            requestTemplate.Clip.FrameRate,
            frames.MoveToImmutable());
    }

    private static void ValidateDescriptorInventory(
        RigDefinition rig,
        AnimationClip clip,
        ImmutableArray<MorphChannelBinding> bindings)
    {
        ValidateTargetDescriptorInventory(rig);

        Dictionary<string, MorphChannelDefinition> morphs = rig.MorphChannels
            .ToDictionary(static morph => morph.Name, StringComparer.OrdinalIgnoreCase);
        HashSet<string> boundSources = bindings
            .Select(static binding => binding.SourceChannel)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (MorphChannelBinding binding in bindings)
        {
            if (!morphs.TryGetValue(
                    binding.TargetMorph,
                    out MorphChannelDefinition? target))
            {
                throw new InvalidOperationException(
                    $"Morph binding target '{binding.TargetMorph}' is absent from the target rig inventory.");
            }

            if (!target.DescriptorHash.HasValue)
            {
                throw new InvalidOperationException(
                    $"Morph binding target '{binding.TargetMorph}' lacks a DL1 descriptor hash.");
            }
        }

        foreach (ScalarTrack track in clip.ScalarTracks)
        {
            if (!bindings.IsEmpty)
            {
                if (boundSources.Contains(track.ChannelName))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Animated morph source '{track.ChannelName}' has no export binding.");
            }

            if (!morphs.TryGetValue(track.ChannelName, out MorphChannelDefinition? morph))
            {
                throw new InvalidOperationException(
                    $"Animated morph '{track.ChannelName}' is absent from the target rig inventory.");
            }

            if (!morph.DescriptorHash.HasValue)
            {
                throw new InvalidOperationException(
                    $"Animated morph '{track.ChannelName}' lacks a DL1 descriptor hash.");
            }
        }
    }

    /// <summary>
    /// Validates the complete descriptor namespace used by DL1 ANM2 output.
    /// This is public so UI readiness and transaction preflight use exactly
    /// the same fail-closed rule as the final encoder.
    /// </summary>
    public static void ValidateTargetDescriptorInventory(RigDefinition rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        BoneDefinition[] missingRequired = rig.Bones
            .Where(static bone => bone.RequiredForExport && bone.DescriptorHash is null)
            .ToArray();
        if (missingRequired.Length > 0)
        {
            throw new InvalidOperationException(
                "Required target bones lack authoritative DL1 descriptors: " +
                string.Join(", ", missingRequired.Select(static bone => bone.Name)));
        }

        var descriptorOwners = new Dictionary<uint, string>();
        foreach (BoneDefinition bone in rig.Bones)
        {
            if (bone.DescriptorHash is not { } descriptor)
            {
                continue;
            }

            if (!descriptorOwners.TryAdd(descriptor, $"bone '{bone.Name}'"))
            {
                throw new InvalidOperationException(
                    $"Target rig entities {descriptorOwners[descriptor]} and bone '{bone.Name}' " +
                    $"collide at DL1 descriptor 0x{descriptor:X8}; descriptors must be unique for ANM2 export.");
            }
        }

        foreach (MorphChannelDefinition morph in rig.MorphChannels)
        {
            if (morph.DescriptorHash is not { } descriptor)
            {
                continue;
            }

            if (!descriptorOwners.TryAdd(descriptor, $"morph '{morph.Name}'"))
            {
                throw new InvalidOperationException(
                    $"Target rig entities {descriptorOwners[descriptor]} and morph '{morph.Name}' " +
                    $"collide at DL1 descriptor 0x{descriptor:X8}; descriptors must be unique for ANM2 export.");
            }
        }

    }
}
