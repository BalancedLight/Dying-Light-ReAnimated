using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Anm2;

public sealed record Anm2DomainImportResult(
    AnimationClip Clip,
    ImmutableArray<uint> UnmappedDescriptors,
    ImmutableArray<int> BindFallbackBoneIndices)
{
    public Anm2TrackPartition? Partition { get; init; }

    public AnimationClip? FacialClip { get; init; }
}

/// <summary>
/// Converts DL1 ANM2 sampler values to and from the format-neutral authoring domain.
/// Rotation conversion is explicit: ANM2 stores a Cayley XYZ vector while Core uses
/// an XYZW quaternion.
/// </summary>
public static class Anm2DomainAdapter
{
    public static Anm2DomainImportResult ImportBody(
        Anm2Clip source,
        RigDefinition rig,
        FrameRate frameRate)
    {
        Anm2PartitionedImportResult imported =
            Anm2TrackPartitioner.Partition(source, rig, frameRate);
        ThrowIfAmbiguous(imported.Partition);
        return new Anm2DomainImportResult(
            imported.CombinedClip,
            imported.Partition.UnresolvedDescriptors,
            imported.BindFallbackBoneIndices)
        {
            Partition = imported.Partition,
            FacialClip = imported.FacialClip,
        };
    }

    public static AnimationClip ImportMimic(
        Anm2Clip source,
        RigDefinition rig,
        FrameRate frameRate,
        bool retainUnknownDescriptors = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rig);
        Dictionary<uint, MorphChannelDefinition> morphs = BuildUniqueMorphDescriptorMap(rig);
        ImmutableArray<Anm2Frame> frames = Anm2SemanticDecoder.DecodeAllFrames(source);
        var scalarTracks = new List<ScalarTrack>(source.TrackDescriptors.Length);
        for (var trackIndex = 0; trackIndex < source.TrackDescriptors.Length; trackIndex++)
        {
            uint descriptor = source.TrackDescriptors[trackIndex];
            string name;
            if (morphs.TryGetValue(descriptor, out MorphChannelDefinition? morph))
            {
                name = morph.Name;
            }
            else if (retainUnknownDescriptors)
            {
                name = $"descriptor_{descriptor:X8}";
            }
            else
            {
                continue;
            }

            var keys = new ScalarKeyframe[frames.Length];
            for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                keys[frameIndex] = new ScalarKeyframe(
                    frameIndex,
                    frames[frameIndex].Tracks[trackIndex].TranslationX);
            }

            scalarTracks.Add(new ScalarTrack(name, keys));
        }

        return new AnimationClip(
            string.IsNullOrWhiteSpace(source.Name) ? "DL1 Mimic ANM2" : source.Name,
            frameRate,
            source.Header.FrameCount,
            scalarTracks: scalarTracks);
    }

    /// <summary>
    /// Imports only descriptors present in the exact retail target rig. Unlike
    /// the inspection-oriented import, authoring never invents placeholder
    /// morph names for an unknown descriptor.
    /// </summary>
    public static AnimationClip ImportMimicExact(
        Anm2Clip source,
        RigDefinition rig,
        FrameRate frameRate)
    {
        Anm2PartitionedImportResult imported =
            Anm2TrackPartitioner.Partition(source, rig, frameRate);
        ThrowIfAmbiguous(imported.Partition);
        return imported.FacialClip;
    }

    private static void ThrowIfAmbiguous(Anm2TrackPartition partition)
    {
        if (!partition.RequiresReview)
        {
            return;
        }

        throw new InvalidDataException(
            "ANM2 contains duplicated or bone/morph-colliding descriptors that require explicit review: " +
            string.Join(", ", partition.AmbiguousDescriptors.Select(
                static descriptor => $"0x{descriptor:X8}")) +
            ".");
    }

    public static byte[] ExportBody(
        AnimationClip clip,
        RigDefinition rig,
        IReadOnlyList<uint> descriptorOrder,
        double constantTolerance = 1e-7)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(descriptorOrder);
        ValidateTolerance(constantTolerance);
        if (clip.FrameCount > ushort.MaxValue)
        {
            throw new InvalidOperationException("DL1 ANM2 cannot contain more than 65535 frames.");
        }

        Dictionary<uint, int> rigByDescriptor = BuildUniqueBoneDescriptorMap(rig);
        var boneOrder = new int[descriptorOrder.Count];
        for (var index = 0; index < descriptorOrder.Count; index++)
        {
            if (!rigByDescriptor.TryGetValue(descriptorOrder[index], out boneOrder[index]))
            {
                throw new InvalidOperationException(
                    $"Descriptor 0x{descriptorOrder[index]:X8} is not present in rig '{rig.Id}'.");
            }
        }

        var frames = ImmutableArray.CreateBuilder<Anm2Frame>(checked((int)clip.FrameCount));
        for (var frameIndex = 0; frameIndex < clip.FrameCount; frameIndex++)
        {
            double seconds = clip.FrameRate.SecondsForFrame(frameIndex);
            SkeletonPose pose = clip.SamplePose(rig, seconds);
            var trackValues = ImmutableArray.CreateBuilder<Anm2TrackFrame>(boneOrder.Length);
            foreach (int boneIndex in boneOrder)
            {
                trackValues.Add(ToTrackFrame(pose.LocalTransforms[boneIndex]));
            }

            frames.Add(new Anm2Frame(trackValues.MoveToImmutable()));
        }

        ImmutableArray<Anm2Frame> frameArray = frames.MoveToImmutable();
        ImmutableArray<Anm2PackedComponents> packing = DeterminePacking(
            frameArray,
            descriptorOrder.Count,
            constantTolerance);
        return Anm2PayloadWriter.Build(
            CreateTemplate(checked((int)clip.FrameCount), descriptorOrder.Count),
            descriptorOrder.ToImmutableArray(),
            frameArray,
            packing);
    }

    public static byte[] ExportMimic(
        AnimationClip clip,
        RigDefinition rig,
        IReadOnlyList<uint> descriptorOrder,
        double constantTolerance = 1e-7) =>
        ExportMimic(
            clip,
            rig,
            descriptorOrder,
            constantTolerance,
            CancellationToken.None);

    public static byte[] ExportMimic(
        AnimationClip clip,
        RigDefinition rig,
        IReadOnlyList<uint> descriptorOrder,
        double constantTolerance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(descriptorOrder);
        ValidateTolerance(constantTolerance);
        cancellationToken.ThrowIfCancellationRequested();
        if (clip.FrameCount > ushort.MaxValue)
        {
            throw new InvalidOperationException("DL1 ANM2 cannot contain more than 65535 frames.");
        }

        Dictionary<uint, MorphChannelDefinition> morphs = BuildUniqueMorphDescriptorMap(rig);
        var channelNames = new string[descriptorOrder.Count];
        for (var index = 0; index < descriptorOrder.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!morphs.TryGetValue(descriptorOrder[index], out MorphChannelDefinition? morph))
            {
                throw new InvalidOperationException(
                    $"Mimic descriptor 0x{descriptorOrder[index]:X8} is not present in rig '{rig.Id}'.");
            }

            channelNames[index] = morph.Name;
        }

        var frames = ImmutableArray.CreateBuilder<Anm2Frame>(checked((int)clip.FrameCount));
        for (var frameIndex = 0; frameIndex < clip.FrameCount; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double seconds = clip.FrameRate.SecondsForFrame(frameIndex);
            ImmutableDictionary<string, double> values = clip.SampleScalars(seconds);
            var tracks = ImmutableArray.CreateBuilder<Anm2TrackFrame>(channelNames.Length);
            foreach (string name in channelNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                values.TryGetValue(name, out double value);
                tracks.Add(new Anm2TrackFrame(
                    0,
                    0,
                    0,
                    checked((float)value),
                    0,
                    0,
                    1,
                    1,
                    1));
            }

            frames.Add(new Anm2Frame(tracks.MoveToImmutable()));
        }

        ImmutableArray<Anm2Frame> frameArray = frames.MoveToImmutable();
        ImmutableArray<Anm2PackedComponents> packing = DeterminePacking(
            frameArray,
            descriptorOrder.Count,
            constantTolerance,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Anm2PayloadWriter.Build(
            CreateTemplate(checked((int)clip.FrameCount), descriptorOrder.Count),
            descriptorOrder.ToImmutableArray(),
            frameArray,
            packing,
            cancellationToken);
    }

    public static QuaternionD QuaternionFromCayley(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            throw new ArgumentException("ANM2 Cayley components must be finite.");
        }

        double squared = (x * x) + (y * y) + (z * z);
        double denominator = 1.0 + squared;
        return new QuaternionD(
            2.0 * x / denominator,
            2.0 * y / denominator,
            2.0 * z / denominator,
            (1.0 - squared) / denominator).Normalized();
    }

    public static Vector3D CayleyFromQuaternion(QuaternionD rotation)
    {
        QuaternionD value = rotation.Normalized();
        if (value.W < 0)
        {
            value = -value;
        }

        double denominator = 1.0 + value.W;
        if (denominator < 1e-10)
        {
            throw new InvalidOperationException(
                "Quaternion is at the ANM2 Cayley singularity.");
        }

        return new Vector3D(
            value.X / denominator,
            value.Y / denominator,
            value.Z / denominator);
    }

    private static TransformTRS ToTransform(Anm2TrackFrame values) =>
        new(
            new Vector3D(values.TranslationX, values.TranslationY, values.TranslationZ),
            QuaternionFromCayley(values.RotationX, values.RotationY, values.RotationZ),
            new Vector3D(values.ScaleX, values.ScaleY, values.ScaleZ));

    private static Anm2TrackFrame ToTrackFrame(TransformTRS transform)
    {
        Vector3D rotation = CayleyFromQuaternion(transform.Rotation);
        return new Anm2TrackFrame(
            checked((float)rotation.X),
            checked((float)rotation.Y),
            checked((float)rotation.Z),
            checked((float)transform.Translation.X),
            checked((float)transform.Translation.Y),
            checked((float)transform.Translation.Z),
            checked((float)transform.Scale.X),
            checked((float)transform.Scale.Y),
            checked((float)transform.Scale.Z));
    }

    private static ImmutableArray<Anm2PackedComponents> DeterminePacking(
        ImmutableArray<Anm2Frame> frames,
        int trackCount,
        double tolerance,
        CancellationToken cancellationToken = default)
    {
        var result = ImmutableArray.CreateBuilder<Anm2PackedComponents>(trackCount);
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Anm2PackedComponents packed = Anm2PackedComponents.None;
            for (var componentIndex = 0; componentIndex < 6; componentIndex++)
            {
                if (IsAnimated(
                        frames,
                        trackIndex,
                        componentIndex,
                        tolerance,
                        cancellationToken))
                {
                    packed |= (Anm2PackedComponents)(1 << componentIndex);
                }
            }

            if (IsAnimated(
                    frames,
                    trackIndex,
                    6,
                    tolerance,
                    cancellationToken) ||
                IsAnimated(
                    frames,
                    trackIndex,
                    7,
                    tolerance,
                    cancellationToken) ||
                IsAnimated(
                    frames,
                    trackIndex,
                    8,
                    tolerance,
                    cancellationToken))
            {
                packed |= Anm2PackedComponents.Scale;
            }

            result.Add(packed);
        }

        return result.MoveToImmutable();
    }

    private static bool IsAnimated(
        ImmutableArray<Anm2Frame> frames,
        int trackIndex,
        int componentIndex,
        double tolerance,
        CancellationToken cancellationToken)
    {
        float first = frames[0].Tracks[trackIndex][componentIndex];
        for (var frameIndex = 1; frameIndex < frames.Length; frameIndex++)
        {
            if ((frameIndex & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Math.Abs(frames[frameIndex].Tracks[trackIndex][componentIndex] - first) >
                tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<uint, int> BuildUniqueBoneDescriptorMap(RigDefinition rig)
    {
        var result = new Dictionary<uint, int>();
        foreach (BoneDefinition bone in rig.Bones)
        {
            if (bone.DescriptorHash is not uint descriptor)
            {
                continue;
            }

            if (!result.TryAdd(descriptor, bone.Index))
            {
                throw new InvalidOperationException(
                    $"Rig '{rig.Id}' contains duplicate bone descriptor 0x{descriptor:X8}.");
            }
        }

        return result;
    }

    private static Dictionary<uint, MorphChannelDefinition> BuildUniqueMorphDescriptorMap(
        RigDefinition rig)
    {
        var result = new Dictionary<uint, MorphChannelDefinition>();
        foreach (MorphChannelDefinition morph in rig.MorphChannels)
        {
            if (morph.DescriptorHash is not uint descriptor)
            {
                continue;
            }

            if (!result.TryAdd(descriptor, morph))
            {
                throw new InvalidOperationException(
                    $"Rig '{rig.Id}' contains duplicate morph descriptor 0x{descriptor:X8}.");
            }
        }

        return result;
    }

    private static Anm2Header CreateTemplate(int frameCount, int trackCount) =>
        new(
            Anm2Header.Dl1FormatVersion,
            Anm2Header.Dl1SamplerVersion,
            checked((ushort)frameCount),
            checked((ushort)trackCount),
            0,
            0,
            0,
            1,
            0,
            0);

    private static void ValidateTolerance(double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }
    }
}
