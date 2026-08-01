using System.Buffers.Binary;
using System.Collections.Immutable;

namespace ReAnimated.Codecs.Anm2;

[Flags]
public enum Anm2PackedComponents : ushort
{
    None = 0,
    RotationX = 1 << 0,
    RotationY = 1 << 1,
    RotationZ = 1 << 2,
    TranslationX = 1 << 3,
    TranslationY = 1 << 4,
    TranslationZ = 1 << 5,
    ScaleX = 1 << 6,
    ScaleY = 1 << 7,
    ScaleZ = 1 << 8,
    Scale = ScaleX | ScaleY | ScaleZ,
}

public readonly record struct Anm2TrackFrame(
    float RotationX,
    float RotationY,
    float RotationZ,
    float TranslationX,
    float TranslationY,
    float TranslationZ,
    float ScaleX,
    float ScaleY,
    float ScaleZ)
{
    public float this[int index] => index switch
    {
        0 => RotationX,
        1 => RotationY,
        2 => RotationZ,
        3 => TranslationX,
        4 => TranslationY,
        5 => TranslationZ,
        6 => ScaleX,
        7 => ScaleY,
        8 => ScaleZ,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

public sealed record Anm2Frame(ImmutableArray<Anm2TrackFrame> Tracks);

public static class Anm2PayloadWriter
{
    private static readonly byte[] DirectMaskBits = [1, 2, 4, 8, 16, 32, 64, 64, 64];

    public static byte[] Build(
        Anm2Header template,
        ImmutableArray<uint> descriptors,
        ImmutableArray<Anm2Frame> frames,
        ImmutableArray<Anm2PackedComponents> packedByTrack) =>
        Build(
            template,
            descriptors,
            frames,
            packedByTrack,
            CancellationToken.None);

    public static byte[] Build(
        Anm2Header template,
        ImmutableArray<uint> descriptors,
        ImmutableArray<Anm2Frame> frames,
        ImmutableArray<Anm2PackedComponents> packedByTrack,
        CancellationToken cancellationToken)
    {
        return BuildCore(
            template,
            descriptors,
            frames,
            packedByTrack,
            packedBuildCheckpoint: null,
            cancellationToken);
    }

    internal static byte[] Build(
        Anm2Header template,
        ImmutableArray<uint> descriptors,
        ImmutableArray<Anm2Frame> frames,
        ImmutableArray<Anm2PackedComponents> packedByTrack,
        Action packedBuildCheckpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packedBuildCheckpoint);
        return BuildCore(
            template,
            descriptors,
            frames,
            packedByTrack,
            packedBuildCheckpoint,
            cancellationToken);
    }

    private static byte[] BuildCore(
        Anm2Header template,
        ImmutableArray<uint> descriptors,
        ImmutableArray<Anm2Frame> frames,
        ImmutableArray<Anm2PackedComponents> packedByTrack,
        Action? packedBuildCheckpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInputs(
            descriptors,
            frames,
            packedByTrack,
            cancellationToken);

        var trackCount = descriptors.Length;
        var directValues = new List<float>(trackCount * 9);
        var packedCurves = new List<PackedCurve>(trackCount * 9);
        var masks = new byte[trackCount];

        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var flags = packedByTrack[trackIndex];
            var scalePacked = (flags & Anm2PackedComponents.Scale) != 0;
            if (scalePacked && (flags & Anm2PackedComponents.Scale) != Anm2PackedComponents.Scale)
            {
                throw new ArgumentException(
                    $"Track {trackIndex} mixes direct and packed scale axes.",
                    nameof(packedByTrack));
            }

            byte mask = 0;
            for (var componentIndex = 0; componentIndex < 9; componentIndex++)
            {
                if (componentIndex == 6)
                {
                    if (!scalePacked)
                    {
                        mask |= 64;
                        directValues.Add(frames[0].Tracks[trackIndex].ScaleX);
                        directValues.Add(frames[0].Tracks[trackIndex].ScaleY);
                        directValues.Add(frames[0].Tracks[trackIndex].ScaleZ);
                    }
                    else
                    {
                        for (var scaleIndex = 6; scaleIndex < 9; scaleIndex++)
                        {
                            packedCurves.Add(
                                BuildPackedCurve(
                                    trackIndex,
                                    scaleIndex,
                                    frames,
                                    cancellationToken));
                        }
                    }

                    break;
                }

                var packedFlag = (Anm2PackedComponents)(1 << componentIndex);
                if ((flags & packedFlag) != 0)
                {
                    packedCurves.Add(
                        BuildPackedCurve(
                            trackIndex,
                            componentIndex,
                            frames,
                            cancellationToken));
                    if (packedCurves.Count == 1)
                    {
                        packedBuildCheckpoint?.Invoke();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                else
                {
                    mask |= DirectMaskBits[componentIndex];
                    directValues.Add(frames[0].Tracks[trackIndex][componentIndex]);
                }
            }

            masks[trackIndex] = mask;
        }

        var slotCount = Math.Max(1, ((frames.Length - 2) / 15) + 1);
        var streamChunks = BuildStreamChunks(
            packedCurves,
            frames.Length,
            slotCount,
            cancellationToken);
        var baseSegment = BuildBaseSegment(
            directValues,
            masks,
            packedCurves,
            cancellationToken);
        var (pages, pageSpans) = BuildPages(
            baseSegment,
            streamChunks,
            frames.Length,
            cancellationToken);
        var payload = BuildPayload(
            template,
            descriptors,
            pages,
            pageSpans,
            frames.Length,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Anm2GeneratedPayloadValidator.Validate(
            payload,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return payload;
    }

    private static void ValidateInputs(
        ImmutableArray<uint> descriptors,
        ImmutableArray<Anm2Frame> frames,
        ImmutableArray<Anm2PackedComponents> packedByTrack,
        CancellationToken cancellationToken)
    {
        if (descriptors.IsDefaultOrEmpty || descriptors.Length > ushort.MaxValue)
        {
            throw new ArgumentException("ANM2 requires 1..65535 descriptors.", nameof(descriptors));
        }

        if (frames.IsDefaultOrEmpty || frames.Length > ushort.MaxValue)
        {
            throw new ArgumentException("ANM2 requires 1..65535 frames.", nameof(frames));
        }

        if (packedByTrack.Length != descriptors.Length)
        {
            throw new ArgumentException("Packing flag count does not match descriptor count.", nameof(packedByTrack));
        }

        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frames[frameIndex].Tracks.Length != descriptors.Length)
            {
                throw new ArgumentException(
                    $"Frame {frameIndex} contains {frames[frameIndex].Tracks.Length} tracks; expected {descriptors.Length}.",
                    nameof(frames));
            }

            for (var trackIndex = 0;
                 trackIndex < frames[frameIndex].Tracks.Length;
                 trackIndex++)
            {
                if ((trackIndex & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Anm2TrackFrame track =
                    frames[frameIndex].Tracks[trackIndex];
                for (var component = 0; component < 9; component++)
                {
                    if (!float.IsFinite(track[component]))
                    {
                        throw new ArgumentException(
                            $"Frame {frameIndex} contains a non-finite component.",
                            nameof(frames));
                    }
                }
            }
        }
    }

    private static PackedCurve BuildPackedCurve(
        int trackIndex,
        int componentIndex,
        ImmutableArray<Anm2Frame> frames,
        CancellationToken cancellationToken)
    {
        var values = new float[frames.Length];
        for (var index = 0; index < frames.Length; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            values[index] = frames[index].Tracks[trackIndex][componentIndex];
        }

        var bias = values[0];
        var (quantized, scale) =
            Quantize(values, bias, cancellationToken);
        return new PackedCurve(trackIndex, componentIndex, bias, scale, quantized);
    }

    private static (short[] Values, float Scale) Quantize(
        float[] values,
        float reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maximumDelta = 0f;
        for (var index = 0; index < values.Length; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            maximumDelta = Math.Max(
                maximumDelta,
                Math.Abs(values[index] - reference));
        }

        if (maximumDelta <= 0)
        {
            return (new short[values.Length], 1);
        }

        var scale = Math.Max(maximumDelta / 28_000f, 1e-9f);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quantized = new int[values.Length];
            var maximumValue = 0;
            for (var index = 0; index < values.Length; index++)
            {
                if ((index & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int value = checked(
                    (int)MathF.Round(
                        (values[index] - reference) / scale));
                quantized[index] = value;
                maximumValue = Math.Max(
                    maximumValue,
                    Math.Abs(value));
            }

            var maximumSecondOrder = MaximumSecondOrderDelta(
                quantized,
                cancellationToken);
            if (maximumValue <= 30_000 && maximumSecondOrder <= 30_000)
            {
                var result = new short[quantized.Length];
                for (var index = 0; index < quantized.Length; index++)
                {
                    if ((index & 0xFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    result[index] = checked((short)quantized[index]);
                }

                return (result, scale);
            }

            scale *= Math.Max(
                Math.Max(maximumValue / 30_000f, maximumSecondOrder / 30_000f),
                1.25f);
        }

        throw new InvalidDataException("Could not quantize ANM2 curve into a safe int16 stream.");
    }

    private static int MaximumSecondOrderDelta(
        int[] values,
        CancellationToken cancellationToken)
    {
        var maximum = 0;
        for (var index = 0; index < values.Length; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var delta = index switch
            {
                0 => values[index],
                1 => values[index] - values[index - 1],
                _ => values[index] - (2 * values[index - 1]) + values[index - 2],
            };
            maximum = Math.Max(maximum, Math.Abs(delta));
        }

        return maximum;
    }

    private static List<byte[]> BuildStreamChunks(
        IReadOnlyList<PackedCurve> curves,
        int frameCount,
        int slotCount,
        CancellationToken cancellationToken)
    {
        var groupCount = Math.Max(1, (curves.Count + 7) / 8);
        var chunks = new List<byte[]>(slotCount);
        for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseFrame = slotIndex * 15;
            using var stream = new MemoryStream();
            for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new IReadOnlyList<short>[Anm2PackedGroupCodec.FrameCount];
                for (var frameOffset = 0; frameOffset < Anm2PackedGroupCodec.FrameCount; frameOffset++)
                {
                    var frame = Math.Min(baseFrame + frameOffset, frameCount - 1);
                    var lanes = new short[Anm2PackedGroupCodec.LaneCount];
                    for (var lane = 0; lane < lanes.Length; lane++)
                    {
                        var curveIndex = (groupIndex * 8) + lane;
                        if (curveIndex < curves.Count)
                        {
                            lanes[lane] = curves[curveIndex].Values[frame];
                        }
                    }

                    values[frameOffset] = lanes;
                }

                stream.Write(Anm2PackedGroupCodec.Encode(values));
            }

            chunks.Add(stream.ToArray());
        }

        return chunks;
    }

    private static byte[] BuildBaseSegment(
        List<float> directValues,
        byte[] masks,
        List<PackedCurve> curves,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directCount = directValues.Count;
        var packedCount = curves.Count;
        var totalCount = checked(directCount + packedCount);
        if (totalCount != 9 * masks.Length)
        {
            throw new InvalidDataException("ANM2 component counts do not match mask count.");
        }

        var packedGroupCount = (packedCount + 7) / 8;
        var packedTableBytes = checked(64 * packedGroupCount);
        using var stream = new MemoryStream();
        WriteUInt16(stream, checked((ushort)directCount));
        WriteUInt16(stream, checked((ushort)packedCount));
        WriteUInt16(stream, checked((ushort)totalCount));
        WriteUInt16(stream, checked((ushort)packedTableBytes));
        stream.Write(new byte[8]);

        for (var groupIndex = 0; groupIndex < packedGroupCount; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var lane = 0; lane < 8; lane++)
            {
                var curveIndex = (groupIndex * 8) + lane;
                WriteSingle(stream, curveIndex < curves.Count ? curves[curveIndex].Bias : 0);
            }

            for (var lane = 0; lane < 8; lane++)
            {
                var curveIndex = (groupIndex * 8) + lane;
                WriteSingle(stream, curveIndex < curves.Count ? curves[curveIndex].Scale : 1);
            }
        }

        Pad(stream, 16);
        for (var index = 0; index < directValues.Count; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            WriteSingle(stream, directValues[index]);
        }

        Pad(stream, 4);
        for (var index = 0; index < masks.Length; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            stream.WriteByte(masks[index]);
        }

        Pad(stream, 16);
        return stream.ToArray();
    }

    private static (List<byte[]> Pages, List<ushort> Spans) BuildPages(
        byte[] baseSegment,
        IReadOnlyList<byte[]> chunks,
        int frameCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var groups = new List<List<byte[]>>();
        var current = new List<byte[]>();
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new List<byte[]>(current) { chunk };
            if (!TryBuildPage(
                    baseSegment,
                    candidate,
                    cancellationToken,
                    out _))
            {
                if (current.Count == 0)
                {
                    throw new InvalidDataException("A single ANM2 stream chunk exceeds one 64 KiB page.");
                }

                groups.Add(current);
                current = [chunk];
                if (!TryBuildPage(
                        baseSegment,
                        current,
                        cancellationToken,
                        out _))
                {
                    throw new InvalidDataException("A single ANM2 stream chunk exceeds one 64 KiB page.");
                }
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Count != 0)
        {
            groups.Add(current);
        }

        var pages = new List<byte[]>(groups.Count);
        foreach (List<byte[]> group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(
                BuildPage(
                    baseSegment,
                    group,
                    cancellationToken));
        }

        var remaining = frameCount - 1;
        var spans = new List<ushort>(groups.Count);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var span = Math.Min(15 * group.Count, remaining);
            spans.Add(checked((ushort)span));
            remaining -= span;
        }

        if (remaining != 0)
        {
            throw new InvalidDataException("ANM2 pages do not cover frame_count - 1.");
        }

        return (pages, spans);
    }

    private static bool TryBuildPage(
        byte[] baseSegment,
        IReadOnlyList<byte[]> chunks,
        CancellationToken cancellationToken,
        out byte[] page)
    {
        try
        {
            page = BuildPage(
                baseSegment,
                chunks,
                cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            page = [];
            return false;
        }
    }

    private static byte[] BuildPage(
        byte[] baseSegment,
        IReadOnlyList<byte[]> chunks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (chunks.Count == 0)
        {
            throw new ArgumentException("ANM2 page requires a stream chunk.", nameof(chunks));
        }

        var offsetWordCount = chunks.Count + 2;
        var tableWordCount = Math.Max(16, offsetWordCount);
        var firstSegmentWord = ((2 * tableWordCount) + 15) / 16;
        var offsets = new List<ushort>(offsetWordCount)
        {
            checked((ushort)firstSegmentWord),
        };
        var cursor = firstSegmentWord + (baseSegment.Length / 16);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offsets.Add(checked((ushort)cursor));
            cursor += chunk.Length / 16;
        }

        offsets.Add(checked((ushort)cursor));
        if (cursor * 16 > Anm2Header.PageSize)
        {
            throw new InvalidDataException("ANM2 page exceeds 64 KiB.");
        }

        using var stream = new MemoryStream(cursor * 16);
        foreach (var offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteUInt16(stream, offset);
        }

        stream.Write(new byte[(tableWordCount - offsets.Count) * 2]);
        while (stream.Length < firstSegmentWord * 16L)
        {
            stream.WriteByte(0);
        }

        stream.Write(baseSegment);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Write(chunk);
        }

        return stream.ToArray();
    }

    private static byte[] BuildPayload(
        Anm2Header template,
        ImmutableArray<uint> descriptors,
        List<byte[]> pages,
        List<ushort> pageSpans,
        int frameCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var headerSide = new MemoryStream();
        foreach (var descriptor in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteUInt32(headerSide, descriptor);
        }

        foreach (var span in pageSpans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteUInt16(headerSide, span);
        }

        WriteUInt16(headerSide, 1);
        WriteUInt16(headerSide, checked((ushort)(frameCount - 1)));
        WriteUInt16(headerSide, 1);
        PadFromAbsoluteOffset(headerSide, Anm2Header.Size, 16);

        var pageOffset = checked((ushort)(Anm2Header.Size + headerSide.Length));
        using var pageBlob = new MemoryStream();
        for (var index = 0; index < pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageBlob.Write(pages[index]);
            if (index + 1 < pages.Count)
            {
                pageBlob.Write(new byte[Anm2Header.PageSize - pages[index].Length]);
            }
        }

        var declaredLength = checked((uint)(pageOffset + pageBlob.Length));
        var header = new Anm2Header(
            Anm2Header.Dl1FormatVersion,
            template.SamplerVersion == 0 ? Anm2Header.Dl1SamplerVersion : template.SamplerVersion,
            checked((ushort)frameCount),
            checked((ushort)descriptors.Length),
            checked((ushort)pages.Count),
            pageOffset,
            declaredLength,
            1,
            0,
            0);

        var output = GC.AllocateUninitializedArray<byte>(checked((int)declaredLength));
        cancellationToken.ThrowIfCancellationRequested();
        header.Write(output);
        headerSide.ToArray().CopyTo(output, Anm2Header.Size);
        cancellationToken.ThrowIfCancellationRequested();
        pageBlob.ToArray().CopyTo(output, pageOffset);
        return output;
    }

    private static void Pad(Stream stream, int alignment)
    {
        var padding = (alignment - ((int)stream.Length % alignment)) % alignment;
        if (padding != 0)
        {
            stream.Write(new byte[padding]);
        }
    }

    private static void PadFromAbsoluteOffset(Stream stream, int prefixLength, int alignment)
    {
        var absolute = prefixLength + checked((int)stream.Length);
        var padding = (alignment - (absolute % alignment)) % alignment;
        if (padding != 0)
        {
            stream.Write(new byte[padding]);
        }
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteSingle(Stream stream, float value) =>
        WriteUInt32(stream, BitConverter.SingleToUInt32Bits(value));

    private sealed record PackedCurve(
        int TrackIndex,
        int ComponentIndex,
        float Bias,
        float Scale,
        short[] Values);
}
