using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Evaluation;

namespace ReAnimated.Codecs.Anm2;

[Flags]
public enum Dl1AnimationExportParts
{
    None = 0,
    Body = 1 << 0,
    Mimic = 1 << 1,
    BodyAndMimic = Body | Mimic,
}

public sealed record Dl1AnimationExportRequest
{
    public required EvaluationRequest Evaluation { get; init; }

    public Dl1AnimationExportParts Parts { get; init; } =
        Dl1AnimationExportParts.Body;

    public ImmutableArray<uint> BodyDescriptorOrder { get; init; } = [];

    public ImmutableArray<uint> MimicDescriptorOrder { get; init; } = [];

    public double ConstantTolerance { get; init; } = 1e-7;
}

public sealed record Dl1AnimationExportResult(
    Dl1Anm2AuthoringSequence AuthoredSequence,
    byte[]? BodyAnm2,
    byte[]? MimicAnm2);

public interface IDl1AnimationExporter
{
    Dl1AnimationExportResult Export(
        Dl1AnimationExportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Encodes the authoritative authored output of the shared evaluation pipeline.
/// Preview-only edit, IK, and DL1 procedural layers never reach this boundary.
/// </summary>
public sealed class Dl1AnimationExporter : IDl1AnimationExporter
{
    private readonly IAnm2EvaluationAdapter _evaluationAdapter;

    public Dl1AnimationExporter(IAnm2EvaluationAdapter evaluationAdapter)
    {
        _evaluationAdapter = evaluationAdapter ??
            throw new ArgumentNullException(nameof(evaluationAdapter));
    }

    public Dl1AnimationExportResult Export(
        Dl1AnimationExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Evaluation);
        ValidateRequest(request);

        Dl1Anm2AuthoringSequence sequence =
            _evaluationAdapter.SampleAuthoredFrames(
                request.Evaluation,
                cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        byte[]? body = null;
        if (request.Parts.HasFlag(Dl1AnimationExportParts.Body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<uint> descriptorOrder =
                ResolveBodyDescriptorOrder(request, sequence);
            AnimationClip clip = BuildBodyClip(
                sequence,
                request.Evaluation.TargetRig,
                descriptorOrder);
            body = Anm2DomainAdapter.ExportBody(
                clip,
                request.Evaluation.TargetRig,
                descriptorOrder,
                request.ConstantTolerance);
            cancellationToken.ThrowIfCancellationRequested();
        }

        byte[]? mimic = null;
        if (request.Parts.HasFlag(Dl1AnimationExportParts.Mimic))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<uint> descriptorOrder =
                ResolveMimicDescriptorOrder(request, sequence);
            AnimationClip clip = BuildMimicClip(
                sequence,
                request.Evaluation.TargetRig,
                descriptorOrder);
            mimic = Anm2DomainAdapter.ExportMimic(
                clip,
                request.Evaluation.TargetRig,
                descriptorOrder,
                request.ConstantTolerance,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new(sequence, body, mimic);
    }

    private static void ValidateRequest(Dl1AnimationExportRequest request)
    {
        if (request.Parts is Dl1AnimationExportParts.None ||
            (request.Parts & ~Dl1AnimationExportParts.BodyAndMimic) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "At least one supported DL1 animation output part is required.");
        }

        if (!double.IsFinite(request.ConstantTolerance) ||
            request.ConstantTolerance < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The ANM2 constant tolerance must be finite and non-negative.");
        }

        if (request.BodyDescriptorOrder.IsDefault ||
            request.MimicDescriptorOrder.IsDefault)
        {
            throw new ArgumentException(
                "Descriptor-order collections must be initialized.",
                nameof(request));
        }

        EnsureUnique(request.BodyDescriptorOrder, "body", nameof(request));
        EnsureUnique(request.MimicDescriptorOrder, "mimic", nameof(request));
    }

    private static ImmutableArray<uint> ResolveBodyDescriptorOrder(
        Dl1AnimationExportRequest request,
        Dl1Anm2AuthoringSequence sequence)
    {
        if (!request.BodyDescriptorOrder.IsEmpty)
        {
            return request.BodyDescriptorOrder;
        }

        ImmutableArray<uint> descriptors = sequence.Frames[0].Tracks
            .Select(static track => track.DescriptorHash)
            .ToImmutableArray();
        if (descriptors.IsEmpty)
        {
            throw new InvalidOperationException(
                "The evaluated target rig has no exportable DL1 body descriptors.");
        }

        EnsureUnique(descriptors, "evaluated body", nameof(sequence));
        return descriptors;
    }

    private static ImmutableArray<uint> ResolveMimicDescriptorOrder(
        Dl1AnimationExportRequest request,
        Dl1Anm2AuthoringSequence sequence)
    {
        if (!request.MimicDescriptorOrder.IsEmpty)
        {
            return request.MimicDescriptorOrder;
        }

        HashSet<uint> animated = sequence.Frames
            .SelectMany(static frame => frame.Morphs)
            .Select(static morph => morph.DescriptorHash)
            .ToHashSet();
        ImmutableArray<uint> descriptors = request.Evaluation.TargetRig.MorphChannels
            .Where(morph =>
                morph.DescriptorHash is uint descriptor &&
                animated.Contains(descriptor))
            .Select(static morph => morph.DescriptorHash!.Value)
            .ToImmutableArray();
        if (descriptors.IsEmpty)
        {
            throw new InvalidOperationException(
                "The evaluated animation has no exportable DL1 mimic channels.");
        }

        EnsureUnique(descriptors, "evaluated mimic", nameof(sequence));
        return descriptors;
    }

    private static AnimationClip BuildBodyClip(
        Dl1Anm2AuthoringSequence sequence,
        RigDefinition rig,
        ImmutableArray<uint> descriptorOrder)
    {
        Dictionary<uint, BoneDefinition> bonesByDescriptor = rig.Bones
            .Where(static bone => bone.DescriptorHash.HasValue)
            .ToDictionary(static bone => bone.DescriptorHash!.Value);
        var tracks = ImmutableArray.CreateBuilder<TransformTrack>(
            descriptorOrder.Length);
        foreach (uint descriptor in descriptorOrder)
        {
            if (!bonesByDescriptor.TryGetValue(
                    descriptor,
                    out BoneDefinition? bone))
            {
                throw new InvalidOperationException(
                    $"Body descriptor 0x{descriptor:X8} is absent from target rig '{rig.Id}'.");
            }

            var keys = ImmutableArray.CreateBuilder<TransformKeyframe>(
                sequence.Frames.Length);
            foreach (Dl1Anm2AuthoringFrame frame in sequence.Frames)
            {
                Dl1Anm2TrackSample sample = FindBodySample(
                    frame,
                    descriptor,
                    bone.Index);
                keys.Add(new TransformKeyframe(frame.FrameIndex, sample.LocalTransform));
            }

            tracks.Add(new TransformTrack(bone.Index, keys.MoveToImmutable()));
        }

        return new AnimationClip(
            sequence.Name,
            sequence.FrameRate,
            sequence.Frames.Length,
            tracks.MoveToImmutable());
    }

    private static AnimationClip BuildMimicClip(
        Dl1Anm2AuthoringSequence sequence,
        RigDefinition rig,
        ImmutableArray<uint> descriptorOrder)
    {
        Dictionary<uint, MorphChannelDefinition> morphsByDescriptor =
            rig.MorphChannels
                .Where(static morph => morph.DescriptorHash.HasValue)
                .ToDictionary(static morph => morph.DescriptorHash!.Value);
        var tracks = ImmutableArray.CreateBuilder<ScalarTrack>(
            descriptorOrder.Length);
        foreach (uint descriptor in descriptorOrder)
        {
            if (!morphsByDescriptor.TryGetValue(
                    descriptor,
                    out MorphChannelDefinition? morph))
            {
                throw new InvalidOperationException(
                    $"Mimic descriptor 0x{descriptor:X8} is absent from target rig '{rig.Id}'.");
            }

            var keys = ImmutableArray.CreateBuilder<ScalarKeyframe>(
                sequence.Frames.Length);
            foreach (Dl1Anm2AuthoringFrame frame in sequence.Frames)
            {
                double value = frame.Morphs
                    .FirstOrDefault(sample =>
                        sample.DescriptorHash == descriptor &&
                        sample.MorphIndex == morph.Index)
                    .Value;
                keys.Add(new ScalarKeyframe(frame.FrameIndex, value));
            }

            tracks.Add(new ScalarTrack(morph.Name, keys.MoveToImmutable()));
        }

        return new AnimationClip(
            sequence.Name,
            sequence.FrameRate,
            sequence.Frames.Length,
            scalarTracks: tracks.MoveToImmutable());
    }

    private static Dl1Anm2TrackSample FindBodySample(
        Dl1Anm2AuthoringFrame frame,
        uint descriptor,
        int boneIndex)
    {
        foreach (Dl1Anm2TrackSample sample in frame.Tracks)
        {
            if (sample.DescriptorHash == descriptor &&
                sample.BoneIndex == boneIndex)
            {
                return sample;
            }
        }

        throw new InvalidOperationException(
            $"Evaluated frame {frame.FrameIndex} lacks body descriptor 0x{descriptor:X8}.");
    }

    private static void EnsureUnique(
        ImmutableArray<uint> descriptors,
        string description,
        string parameterName)
    {
        if (descriptors.Distinct().Count() != descriptors.Length)
        {
            throw new ArgumentException(
                $"The {description} descriptor order contains duplicates.",
                parameterName);
        }
    }
}
