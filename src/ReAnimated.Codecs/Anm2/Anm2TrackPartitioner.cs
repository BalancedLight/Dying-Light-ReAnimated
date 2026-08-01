using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Anm2;

public sealed record Anm2PartitionedImportResult(
    AnimationClip CombinedClip,
    AnimationClip BodyClip,
    AnimationClip FacialClip,
    Anm2TrackPartition Partition,
    ImmutableArray<int> BindFallbackBoneIndices)
{
    public AuxiliaryTransformTrack? MotionAccumulator =>
        CombinedClip.AuxiliaryTransformTracks.FirstOrDefault(
            static track => track.Descriptor ==
                Anm2TrackPartitioner.MotionAccumulatorDescriptor);
}

/// <summary>
/// Classifies and imports every track from one bulk ANM2 decode. Classification
/// uses only the immutable source rig and its exact morph inventory; no target
/// rig or filename heuristic participates.
/// </summary>
public static class Anm2TrackPartitioner
{
    public const uint MotionAccumulatorDescriptor = 0xCCC3CDDF;

    public static Anm2PartitionedImportResult Partition(
        Anm2Clip source,
        RigDefinition sourceRig,
        FrameRate frameRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceRig);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<uint, int> sourceCounts = source.TrackDescriptors
            .GroupBy(static descriptor => descriptor)
            .ToDictionary(static group => group.Key, static group => group.Count());
        Dictionary<uint, ImmutableArray<int>> bonesByDescriptor = sourceRig.Bones
            .Where(static bone => bone.DescriptorHash.HasValue)
            .GroupBy(static bone => bone.DescriptorHash!.Value)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static bone => bone.Index).ToImmutableArray());
        Dictionary<uint, ImmutableArray<MorphChannelDefinition>> morphsByDescriptor =
            sourceRig.MorphChannels
                .Where(static morph => morph.DescriptorHash.HasValue)
                .GroupBy(static morph => morph.DescriptorHash!.Value)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.ToImmutableArray());

        var body = ImmutableArray.CreateBuilder<uint>();
        var morph = ImmutableArray.CreateBuilder<uint>();
        var auxiliary = ImmutableArray.CreateBuilder<uint>();
        var unresolved = ImmutableArray.CreateBuilder<uint>();
        var ambiguous = ImmutableArray.CreateBuilder<uint>();
        var classifications = new Dictionary<uint, TrackRole>();
        foreach (uint descriptor in source.TrackDescriptors.Distinct())
        {
            bool duplicateSource = sourceCounts[descriptor] != 1;
            bool uniqueBone = bonesByDescriptor.TryGetValue(
                descriptor,
                out ImmutableArray<int> boneRows) &&
                boneRows.Length == 1;
            bool uniqueMorph = morphsByDescriptor.TryGetValue(
                descriptor,
                out ImmutableArray<MorphChannelDefinition> morphRows) &&
                morphRows.Length == 1;
            bool ambiguousRig =
                (bonesByDescriptor.TryGetValue(descriptor, out boneRows) &&
                 boneRows.Length != 1) ||
                (morphsByDescriptor.TryGetValue(descriptor, out morphRows) &&
                 morphRows.Length != 1);

            TrackRole role;
            if (duplicateSource ||
                ambiguousRig ||
                (uniqueBone && uniqueMorph))
            {
                role = TrackRole.Ambiguous;
                ambiguous.Add(descriptor);
            }
            else if (descriptor == MotionAccumulatorDescriptor)
            {
                role = TrackRole.Auxiliary;
                auxiliary.Add(descriptor);
            }
            else if (uniqueBone)
            {
                role = TrackRole.Body;
                body.Add(descriptor);
            }
            else if (uniqueMorph)
            {
                role = TrackRole.Morph;
                morph.Add(descriptor);
            }
            else
            {
                role = TrackRole.Unresolved;
                unresolved.Add(descriptor);
            }

            classifications.Add(descriptor, role);
        }

        ImmutableArray<Anm2Frame> frames =
            Anm2SemanticDecoder.DecodeAllFrames(
                source,
                cancellationToken: cancellationToken);
        var transformTracks = ImmutableArray.CreateBuilder<TransformTrack>();
        var scalarTracks = ImmutableArray.CreateBuilder<ScalarTrack>();
        var auxiliaryTracks = ImmutableArray.CreateBuilder<AuxiliaryTransformTrack>();
        var mappedBones = new HashSet<int>();
        for (int trackIndex = 0;
             trackIndex < source.TrackDescriptors.Length;
             trackIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint descriptor = source.TrackDescriptors[trackIndex];
            switch (classifications[descriptor])
            {
                case TrackRole.Body:
                {
                    int boneIndex = bonesByDescriptor[descriptor][0];
                    mappedBones.Add(boneIndex);
                    transformTracks.Add(new TransformTrack(
                        boneIndex,
                        BuildTransformKeys(frames, trackIndex)));
                    break;
                }
                case TrackRole.Morph:
                    scalarTracks.Add(new ScalarTrack(
                        morphsByDescriptor[descriptor][0].Name,
                        BuildScalarKeys(frames, trackIndex)));
                    break;
                case TrackRole.Auxiliary:
                    auxiliaryTracks.Add(new AuxiliaryTransformTrack(
                        descriptor,
                        BuildTransformKeys(frames, trackIndex)));
                    break;
            }
        }

        string name = string.IsNullOrWhiteSpace(source.Name)
            ? "DL1 ANM2"
            : source.Name;
        ImmutableArray<TransformTrack> bodyTracks = transformTracks.ToImmutable();
        ImmutableArray<ScalarTrack> facialTracks = scalarTracks.ToImmutable();
        ImmutableArray<AuxiliaryTransformTrack> auxTracks = auxiliaryTracks.ToImmutable();
        var partition = new Anm2TrackPartition
        {
            BodyDescriptors = body.ToImmutable(),
            MorphDescriptors = morph.ToImmutable(),
            AuxiliaryDescriptors = auxiliary.ToImmutable(),
            UnresolvedDescriptors = unresolved.ToImmutable(),
            AmbiguousDescriptors = ambiguous.ToImmutable(),
            Fingerprint = ComputeFingerprint(
                source.TrackDescriptors,
                body,
                morph,
                auxiliary,
                unresolved,
                ambiguous),
        };
        partition.Validate();
        var bodyClip = new AnimationClip(
            name,
            frameRate,
            source.Header.FrameCount,
            bodyTracks,
            auxiliaryTransformTracks: auxTracks);
        var facialClip = new AnimationClip(
            name + " / facial",
            frameRate,
            source.Header.FrameCount,
            scalarTracks: facialTracks);
        var combined = new AnimationClip(
            name,
            frameRate,
            source.Header.FrameCount,
            bodyTracks,
            facialTracks,
            auxTracks);
        ImmutableArray<int> bindFallbacks = sourceRig.Bones
            .Where(bone => !mappedBones.Contains(bone.Index))
            .Select(static bone => bone.Index)
            .ToImmutableArray();
        return new Anm2PartitionedImportResult(
            combined,
            bodyClip,
            facialClip,
            partition,
            bindFallbacks);
    }

    private static TransformKeyframe[] BuildTransformKeys(
        ImmutableArray<Anm2Frame> frames,
        int trackIndex)
    {
        var keys = new TransformKeyframe[frames.Length];
        QuaternionD? previousRotation = null;
        for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            Anm2TrackFrame values = frames[frameIndex].Tracks[trackIndex];
            TransformTRS transform = new(
                new Vector3D(
                    values.TranslationX,
                    values.TranslationY,
                    values.TranslationZ),
                Anm2DomainAdapter.QuaternionFromCayley(
                    values.RotationX,
                    values.RotationY,
                    values.RotationZ),
                new Vector3D(values.ScaleX, values.ScaleY, values.ScaleZ));
            if (previousRotation is { } previous &&
                QuaternionD.Dot(previous, transform.Rotation) < 0.0)
            {
                transform = transform with
                {
                    Rotation = -transform.Rotation,
                };
            }

            keys[frameIndex] = new TransformKeyframe(frameIndex, transform);
            previousRotation = transform.Rotation;
        }

        return keys;
    }

    private static ScalarKeyframe[] BuildScalarKeys(
        ImmutableArray<Anm2Frame> frames,
        int trackIndex)
    {
        var keys = new ScalarKeyframe[frames.Length];
        for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            keys[frameIndex] = new ScalarKeyframe(
                frameIndex,
                frames[frameIndex].Tracks[trackIndex].TranslationX);
        }

        return keys;
    }

    private static string ComputeFingerprint(
        ImmutableArray<uint> sourceOrder,
        IEnumerable<uint> body,
        IEnumerable<uint> morph,
        IEnumerable<uint> auxiliary,
        IEnumerable<uint> unresolved,
        IEnumerable<uint> ambiguous)
    {
        var canonical = new StringBuilder("dl1-anm2-partition-v1\n");
        Append("source", sourceOrder);
        Append("body", body);
        Append("morph", morph);
        Append("auxiliary", auxiliary);
        Append("unresolved", unresolved);
        Append("ambiguous", ambiguous);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.ASCII.GetBytes(canonical.ToString())))
            .ToLowerInvariant();

        void Append(string role, IEnumerable<uint> descriptors)
        {
            canonical.Append(role).Append(':');
            foreach (uint descriptor in descriptors)
            {
                canonical.Append(descriptor.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(',');
            }

            canonical.Append('\n');
        }
    }

    private enum TrackRole
    {
        Body,
        Morph,
        Auxiliary,
        Unresolved,
        Ambiguous,
    }
}
