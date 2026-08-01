using System.Buffers.Binary;
using System.Collections.Immutable;

namespace ReAnimated.Codecs.Anm2;

public sealed record Anm2DecodedSample(
    double RequestedTime,
    double EvaluatedFrame,
    int PageIndex,
    int TableIndex,
    int FrameInSlot,
    float Fraction,
    Anm2Frame Frame);

public sealed record Anm2BulkDecodeResult(
    ImmutableArray<uint> TrackDescriptors,
    ImmutableArray<Anm2Frame> Frames,
    int UniquePackedSlotsDecoded);

/// <summary>
/// Decodes the DL1 sampler-v1 page, calibration, direct-component, and packed-component
/// layout into the same nine-component track representation consumed by the writer.
/// </summary>
public static class Anm2SemanticDecoder
{
    public const long DefaultMaximumDecodedComponentCount =
        64L * 1024 * 1024;

    private static readonly byte[] DirectMaskBits = [1, 2, 4, 8, 16, 32, 64, 64, 64];

    public static Anm2DecodedSample Sample(Anm2Clip clip, double time)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var data = clip.OriginalBytes.AsSpan();
        SamplerLayout layout = ReadLayout(
            clip,
            data,
            workCheckpoint: null,
            CancellationToken.None);
        SamplePosition position = ResolveSamplePosition(
            clip,
            layout,
            time);
        ushort[] table = ReadPageTable(
            data,
            clip.Header,
            position.PageIndex,
            workCheckpoint: null,
            CancellationToken.None);
        ValidateTableIndex(
            table,
            position.PageIndex,
            position.TableIndex);
        var pageOffset = checked(
            clip.Header.PageOffset +
            (Anm2Header.PageSize * position.PageIndex));
        var baseOffset = checked(pageOffset + (16 * table[0]));
        var streamStart = checked(
            pageOffset +
            (16 * table[position.TableIndex]));
        var streamEnd = checked(
            pageOffset +
            (16 * table[position.TableIndex + 1]));
        var frame = DecodeSelectedFrame(
            data,
            clip.Header.TrackCount,
            baseOffset,
            checked(16 * (table[1] - table[0])),
            streamStart,
            streamEnd,
            position.FrameInSlot,
            position.Fraction);

        return new Anm2DecodedSample(
            time,
            position.EvaluatedFrame,
            position.PageIndex,
            position.TableIndex,
            position.FrameInSlot,
            position.Fraction,
            frame);
    }

    public static ImmutableArray<Anm2Frame> DecodeAllFrames(
        Anm2Clip clip,
        CancellationToken cancellationToken = default) =>
        DecodeFrames(
            clip,
            selectedTrackDescriptors: null,
            DefaultMaximumDecodedComponentCount,
            cancellationToken).Frames;

    public static Anm2BulkDecodeResult DecodeFrames(
        Anm2Clip clip,
        IEnumerable<uint>? selectedTrackDescriptors = null,
        long maximumDecodedComponentCount =
            DefaultMaximumDecodedComponentCount,
        CancellationToken cancellationToken = default) =>
        DecodeFrames(
            clip,
            selectedTrackDescriptors,
            maximumDecodedComponentCount,
            workCheckpoint: null,
            cancellationToken);

    internal static Anm2BulkDecodeResult DecodeFrames(
        Anm2Clip clip,
        IEnumerable<uint>? selectedTrackDescriptors,
        long maximumDecodedComponentCount,
        Action? workCheckpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumDecodedComponentCount);
        CheckCancellation(workCheckpoint, cancellationToken);

        (
            ImmutableArray<uint> descriptors,
            int[] selectedTrackIndices
        ) = ResolveTrackSelection(
            clip,
            selectedTrackDescriptors);
        long decodedComponentCount = checked(
            (long)clip.Header.FrameCount *
            selectedTrackIndices.Length *
            9);
        if (decodedComponentCount >
            maximumDecodedComponentCount)
        {
            throw new InvalidDataException(
                $"ANM2 bulk decode would materialize {decodedComponentCount:N0} components; the configured limit is {maximumDecodedComponentCount:N0}.");
        }

        ReadOnlySpan<byte> data = clip.OriginalBytes.AsSpan();
        SamplerLayout layout = ReadLayout(
            clip,
            data,
            workCheckpoint,
            cancellationToken);
        var positions =
            new SamplePosition[clip.Header.FrameCount];
        var frames = ImmutableArray.CreateBuilder<Anm2Frame>(
            clip.Header.FrameCount);
        var slotFrames =
            new Dictionary<SlotKey, List<int>>();
        for (var frameIndex = 0; frameIndex < clip.Header.FrameCount; frameIndex++)
        {
            if ((frameIndex & 0x3F) == 0)
            {
                CheckCancellation(
                    workCheckpoint,
                    cancellationToken);
            }

            SamplePosition position = ResolveSamplePosition(
                clip,
                layout,
                frameIndex);
            positions[frameIndex] = position;
            var key = new SlotKey(
                position.PageIndex,
                position.TableIndex);
            if (!slotFrames.TryGetValue(
                    key,
                    out List<int>? frameIndexes))
            {
                frameIndexes = [];
                slotFrames.Add(key, frameIndexes);
            }

            frameIndexes.Add(frameIndex);
            frames.Add(default!);
        }

        PageBaseData? pageBase = null;
        ushort[]? pageTable = null;
        var pageBaseIndex = -1;
        var uniquePackedSlotsDecoded = 0;
        foreach (KeyValuePair<SlotKey, List<int>> group in
                 slotFrames
                     .OrderBy(static entry =>
                         entry.Key.PageIndex)
                     .ThenBy(static entry =>
                         entry.Key.TableIndex))
        {
            CheckCancellation(
                workCheckpoint,
                cancellationToken);
            SlotKey key = group.Key;
            var pageOffset = checked(
                clip.Header.PageOffset +
                (Anm2Header.PageSize * key.PageIndex));
            if (pageBaseIndex != key.PageIndex)
            {
                pageTable = ReadPageTable(
                    data,
                    clip.Header,
                    key.PageIndex,
                    workCheckpoint,
                    cancellationToken);
                pageBase = ReadPageBase(
                    data,
                    clip.Header.TrackCount,
                    checked(
                        pageOffset +
                        (16 * pageTable[0])),
                    checked(
                        16 *
                        (pageTable[1] - pageTable[0])),
                    workCheckpoint,
                    cancellationToken);
                pageBaseIndex = key.PageIndex;
            }

            ushort[] currentPageTable =
                pageTable ??
                throw new InvalidOperationException(
                    "ANM2 page-table decode did not publish a result.");
            PageBaseData currentPageBase =
                pageBase ??
                throw new InvalidOperationException(
                    "ANM2 page-base decode did not publish a result.");
            ValidateTableIndex(
                currentPageTable,
                key.PageIndex,
                key.TableIndex);
            var streamStart = checked(
                pageOffset +
                (16 * currentPageTable[key.TableIndex]));
            var streamEnd = checked(
                pageOffset +
                (16 * currentPageTable[key.TableIndex + 1]));
            float[,] packedFrames = DecodePackedFrames(
                data,
                currentPageBase.StreamBase,
                streamStart,
                streamEnd,
                currentPageBase.PackedCount,
                workCheckpoint,
                cancellationToken);
            if (currentPageBase.PackedCount > 0)
            {
                uniquePackedSlotsDecoded++;
            }

            foreach (int frameIndex in group.Value)
            {
                SamplePosition position = positions[frameIndex];
                frames[frameIndex] = BuildFrame(
                    currentPageBase,
                    packedFrames,
                    selectedTrackIndices,
                    position.FrameInSlot,
                    position.Fraction,
                    workCheckpoint,
                    cancellationToken);
            }
        }

        CheckCancellation(workCheckpoint, cancellationToken);
        return new Anm2BulkDecodeResult(
            descriptors,
            frames.MoveToImmutable(),
            uniquePackedSlotsDecoded);
    }

    private static SamplerLayout ReadLayout(
        Anm2Clip clip,
        ReadOnlySpan<byte> data,
        Action? workCheckpoint,
        CancellationToken cancellationToken)
    {
        CheckCancellation(workCheckpoint, cancellationToken);
        var durationCount = checked((int)(clip.Header.DurationKeyCount & 0xFFFF));
        if (durationCount == 0)
        {
            throw new InvalidDataException("DL1 sampler-v1 ANM2 has no duration keys.");
        }

        var durationWordCount = checked(1 + (2 * durationCount));
        var durationOffset = checked(
            Anm2Header.Size +
            (4 * clip.Header.TrackCount) +
            (2 * clip.Header.PageCount));
        var durationByteCount = checked(2 * durationWordCount);
        EnsureRange(data, durationOffset, durationByteCount, "duration table");
        if (durationOffset + durationByteCount > clip.Header.PageOffset)
        {
            throw new InvalidDataException("ANM2 duration table overlaps the first page.");
        }

        var durationWords = new ushort[durationWordCount];
        for (var index = 0; index < durationWords.Length; index++)
        {
            if ((index & 0xFF) == 0)
            {
                CheckCancellation(
                    workCheckpoint,
                    cancellationToken);
            }

            durationWords[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                data[(durationOffset + (2 * index))..]);
        }

        if (durationWords[0] == 0)
        {
            throw new InvalidDataException("ANM2 duration scale cannot be zero.");
        }

        return new SamplerLayout(
            durationWords,
            clip.PageFrameSpans.ToArray());
    }

    private static ushort[] ReadPageTable(
        ReadOnlySpan<byte> data,
        Anm2Header header,
        int pageIndex,
        Action? workCheckpoint,
        CancellationToken cancellationToken)
    {
        CheckCancellation(workCheckpoint, cancellationToken);
        var pageOffset = checked(header.PageOffset + (Anm2Header.PageSize * pageIndex));
        EnsureRange(data, pageOffset, sizeof(ushort), $"page {pageIndex} table");
        var firstWord = BinaryPrimitives.ReadUInt16LittleEndian(data[pageOffset..]);
        var wordCount = Math.Max(16, checked(firstWord * 8));
        if (wordCount > Anm2Header.PageSize / sizeof(ushort))
        {
            throw new InvalidDataException($"ANM2 page {pageIndex} table is unreasonably large.");
        }

        EnsureRange(
            data,
            pageOffset,
            checked(wordCount * sizeof(ushort)),
            $"page {pageIndex} table");
        var offsets = new List<ushort>(wordCount);
        for (var index = 0; index < wordCount; index++)
        {
            if ((index & 0xFF) == 0)
            {
                CheckCancellation(
                    workCheckpoint,
                    cancellationToken);
            }

            var value = BinaryPrimitives.ReadUInt16LittleEndian(
                data[(pageOffset + (index * sizeof(ushort)))..]);
            if (value == 0)
            {
                break;
            }

            if (value >= Anm2Header.PageSize / 16)
            {
                throw new InvalidDataException(
                    $"ANM2 page {pageIndex} table entry {index} exceeds its 64 KiB page.");
            }

            if (offsets.Count != 0 && value <= offsets[^1])
            {
                throw new InvalidDataException(
                    $"ANM2 page {pageIndex} table offsets are not strictly increasing.");
            }

            offsets.Add(value);
        }

        if (offsets.Count < 3)
        {
            throw new InvalidDataException(
                $"ANM2 page {pageIndex} needs base, stream, and end offsets.");
        }

        var finalOffset = checked(pageOffset + (16 * offsets[^1]));
        if (finalOffset > data.Length)
        {
            throw new InvalidDataException($"ANM2 page {pageIndex} content is truncated.");
        }

        return offsets.ToArray();
    }

    private static double EvaluateDuration(ushort[] words, double time)
    {
        var scale = words[0];
        var elapsed = 0d;
        var result = 0d;
        for (var index = 1; index < words.Length - 1; index += 2)
        {
            var duration = (double)words[index] / scale;
            var speed = (double)words[index + 1] / scale;
            if (time - elapsed < duration)
            {
                return result + ((time - elapsed) * speed);
            }

            elapsed += duration;
            result += speed * duration;
        }

        return result;
    }

    private static SamplePosition ResolveSamplePosition(
        Anm2Clip clip,
        SamplerLayout layout,
        double time)
    {
        if (!double.IsFinite(time) ||
            time < 0 ||
            time > clip.Header.FrameCount - 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(time),
                $"Sample time must be finite and within 0..{clip.Header.FrameCount - 1}.");
        }

        if (layout.PageSpans.Length == 0)
        {
            throw new InvalidDataException(
                "DL1 sampler-v1 ANM2 has no pages.");
        }

        double evaluatedFrame = EvaluateDuration(
            layout.DurationWords,
            time);
        var adjustedFrame = Math.Max(
            0,
            checked((int)Math.Ceiling(evaluatedFrame)) -
            1);
        var remaining = adjustedFrame;
        var pageIndex = 0;
        for (; pageIndex < layout.PageSpans.Length; pageIndex++)
        {
            if (remaining < layout.PageSpans[pageIndex])
            {
                break;
            }

            remaining -= layout.PageSpans[pageIndex];
        }

        if (pageIndex == layout.PageSpans.Length)
        {
            pageIndex = layout.PageSpans.Length - 1;
            remaining = Math.Max(
                0,
                layout.PageSpans[pageIndex] - 1);
        }

        var tableIndex = (remaining / 15) + 1;
        var frameInSlot = remaining % 15;
        var fraction = checked(
            (float)(evaluatedFrame - adjustedFrame));
        if (fraction is < 0 or > 1)
        {
            throw new InvalidDataException(
                $"ANM2 duration evaluation produced an invalid interpolation fraction {fraction}.");
        }

        return new SamplePosition(
            evaluatedFrame,
            pageIndex,
            tableIndex,
            frameInSlot,
            fraction);
    }

    private static void ValidateTableIndex(
        ushort[] table,
        int pageIndex,
        int tableIndex)
    {
        if (tableIndex < 1 ||
            tableIndex + 1 >= table.Length)
        {
            throw new InvalidDataException(
                $"ANM2 page {pageIndex} has no end offset for table entry {tableIndex}.");
        }
    }

    private static (
        ImmutableArray<uint> Descriptors,
        int[] TrackIndices
    ) ResolveTrackSelection(
        Anm2Clip clip,
        IEnumerable<uint>? selectedTrackDescriptors)
    {
        if (selectedTrackDescriptors is null)
        {
            return (
                clip.TrackDescriptors,
                Enumerable.Range(
                        0,
                        clip.Header.TrackCount)
                    .ToArray());
        }

        ImmutableArray<uint> selected =
            selectedTrackDescriptors.ToImmutableArray();
        if (selected.Distinct().Count() != selected.Length)
        {
            throw new ArgumentException(
                "Selected ANM2 track descriptors must be unique.",
                nameof(selectedTrackDescriptors));
        }

        var indexByDescriptor = new Dictionary<uint, int>();
        var ambiguousDescriptors = new HashSet<uint>();
        for (var index = 0;
             index < clip.TrackDescriptors.Length;
             index++)
        {
            uint descriptor = clip.TrackDescriptors[index];
            if (!indexByDescriptor.TryAdd(
                    descriptor,
                    index))
            {
                ambiguousDescriptors.Add(descriptor);
            }
        }

        var indices = new int[selected.Length];
        for (var index = 0; index < selected.Length; index++)
        {
            uint descriptor = selected[index];
            if (ambiguousDescriptors.Contains(descriptor))
            {
                throw new InvalidDataException(
                    $"ANM2 descriptor 0x{descriptor:X8} is duplicated and cannot be selected unambiguously.");
            }

            if (!indexByDescriptor.TryGetValue(
                    descriptor,
                    out int trackIndex))
            {
                throw new InvalidDataException(
                    $"Selected ANM2 descriptor 0x{descriptor:X8} is absent from the clip.");
            }

            indices[index] = trackIndex;
        }

        return (selected, indices);
    }

    private static Anm2Frame DecodeSelectedFrame(
        ReadOnlySpan<byte> data,
        int trackCount,
        int baseOffset,
        int baseSegmentSize,
        int streamStart,
        int streamEnd,
        int frameInSlot,
        float fraction)
    {
        PageBaseData pageBase = ReadPageBase(
            data,
            trackCount,
            baseOffset,
            baseSegmentSize,
            workCheckpoint: null,
            CancellationToken.None);
        float[,] packedFrames = DecodePackedFrames(
            data,
            pageBase.StreamBase,
            streamStart,
            streamEnd,
            pageBase.PackedCount,
            workCheckpoint: null,
            CancellationToken.None);
        return BuildFrame(
            pageBase,
            packedFrames,
            Enumerable.Range(0, trackCount).ToArray(),
            frameInSlot,
            fraction,
            workCheckpoint: null,
            CancellationToken.None);
    }

    private static PageBaseData ReadPageBase(
        ReadOnlySpan<byte> data,
        int trackCount,
        int baseOffset,
        int baseSegmentSize,
        Action? workCheckpoint,
        CancellationToken cancellationToken)
    {
        CheckCancellation(workCheckpoint, cancellationToken);
        EnsureRange(data, baseOffset, 16, "base segment");
        if (baseSegmentSize < 16)
        {
            throw new InvalidDataException(
                "ANM2 selected sampler base bounds are invalid.");
        }

        var directCount = BinaryPrimitives.ReadUInt16LittleEndian(data[baseOffset..]);
        var packedCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(baseOffset + 2)..]);
        var totalCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(baseOffset + 4)..]);
        var packedTableBytes = BinaryPrimitives.ReadUInt16LittleEndian(data[(baseOffset + 6)..]);
        if (totalCount != checked(9 * trackCount) ||
            checked(directCount + packedCount) != totalCount)
        {
            throw new InvalidDataException("ANM2 component counts do not match the track count.");
        }

        var packedGroupCount = (packedCount + 7) / 8;
        if (packedTableBytes < checked(packedGroupCount * 64))
        {
            throw new InvalidDataException("ANM2 packed calibration table is truncated.");
        }

        var streamBase = AlignDown16(checked(baseOffset + 0x19));
        var directOffset = AlignUp16(checked(streamBase + packedTableBytes));
        var maskOffset = AlignUp4(checked(directOffset + (4 * directCount)));
        var baseEnd = checked(baseOffset + baseSegmentSize);
        if (maskOffset + trackCount > baseEnd)
        {
            throw new InvalidDataException("ANM2 base tables exceed the base segment.");
        }

        EnsureRange(data, directOffset, checked(4 * directCount), "direct component table");
        EnsureRange(data, maskOffset, trackCount, "component mask table");
        var directValues = new float[directCount];
        for (var index = 0; index < directValues.Length; index++)
        {
            if ((index & 0xFF) == 0)
            {
                CheckCancellation(
                    workCheckpoint,
                    cancellationToken);
            }

            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[(directOffset + (4 * index))..]);
            var value = BitConverter.UInt32BitsToSingle(bits);
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("ANM2 direct component is not finite.");
            }

            directValues[index] = value;
        }

        var references = new ComponentReference[totalCount];
        var directIndex = 0;
        var packedIndex = 0;
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            if ((trackIndex & 0x3F) == 0)
            {
                CheckCancellation(
                    workCheckpoint,
                    cancellationToken);
            }

            var mask = data[maskOffset + trackIndex];
            for (var componentIndex = 0; componentIndex < 9; componentIndex++)
            {
                var isDirect = (mask & DirectMaskBits[componentIndex]) != 0;
                references[(trackIndex * 9) + componentIndex] = isDirect
                    ? new ComponentReference(true, directIndex++)
                    : new ComponentReference(false, packedIndex++);
            }
        }

        if (directIndex != directCount || packedIndex != packedCount)
        {
            throw new InvalidDataException(
                "ANM2 component masks disagree with the direct and packed counts.");
        }

        return new PageBaseData(
            directValues,
            references,
            packedCount,
            streamBase,
            trackCount);
    }

    private static Anm2Frame BuildFrame(
        PageBaseData pageBase,
        float[,] packedFrames,
        int[] selectedTrackIndices,
        int frameInSlot,
        float fraction,
        Action? workCheckpoint,
        CancellationToken cancellationToken)
    {
        if ((uint)frameInSlot >=
            Anm2PackedGroupCodec.FrameCount ||
            fraction is < 0 or > 1)
        {
            throw new InvalidDataException(
                "ANM2 selected packed-frame coordinates are invalid.");
        }

        var tracks =
            ImmutableArray.CreateBuilder<Anm2TrackFrame>(
                selectedTrackIndices.Length);
        Span<float> components = stackalloc float[9];
        for (var outputTrackIndex = 0;
             outputTrackIndex < selectedTrackIndices.Length;
             outputTrackIndex++)
        {
            if ((outputTrackIndex & 0x3F) == 0)
            {
                CheckCancellation(
                    workCheckpoint,
                    cancellationToken);
            }

            int trackIndex =
                selectedTrackIndices[outputTrackIndex];
            if ((uint)trackIndex >=
                (uint)pageBase.TrackCount)
            {
                throw new InvalidDataException(
                    "Selected ANM2 track index is outside the page base.");
            }

            for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                ComponentReference reference =
                    pageBase.References[
                        (trackIndex * 9) +
                        componentIndex];
                var value = reference.IsDirect
                    ? pageBase.DirectValues[reference.Index]
                    : Lerp(
                        packedFrames[frameInSlot, reference.Index],
                        packedFrames[Math.Min(frameInSlot + 1, 15), reference.Index],
                        fraction);
                if (!float.IsFinite(value))
                {
                    throw new InvalidDataException("ANM2 decoded component is not finite.");
                }

                components[componentIndex] = value;
            }

            tracks.Add(new Anm2TrackFrame(
                components[0],
                components[1],
                components[2],
                components[3],
                components[4],
                components[5],
                components[6],
                components[7],
                components[8]));
        }

        return new Anm2Frame(tracks.MoveToImmutable());
    }

    private static float[,] DecodePackedFrames(
        ReadOnlySpan<byte> data,
        int calibrationOffset,
        int streamStart,
        int streamEnd,
        int packedCount,
        Action? workCheckpoint,
        CancellationToken cancellationToken)
    {
        CheckCancellation(workCheckpoint, cancellationToken);
        if (streamStart < 0 ||
            streamStart >= streamEnd)
        {
            throw new InvalidDataException(
                "ANM2 selected packed-stream bounds are invalid.");
        }

        EnsureRange(
            data,
            streamStart,
            streamEnd - streamStart,
            "packed stream");
        var frames = new float[Anm2PackedGroupCodec.FrameCount, packedCount];
        if (packedCount == 0)
        {
            return frames;
        }

        var cursor = streamStart;
        var groupCount = (packedCount + 7) / 8;
        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            CheckCancellation(
                workCheckpoint,
                cancellationToken);
            var encodedLength = Anm2PackedGroupCodec.GetEncodedLength(data[cursor..streamEnd]);
            if (cursor + encodedLength > streamEnd)
            {
                throw new InvalidDataException("ANM2 packed component group is truncated.");
            }

            var values = Anm2PackedGroupCodec.Decode(
                data.Slice(cursor, encodedLength),
                Anm2PackedGroupCodec.FrameCount - 1);
            var biasOffset = checked(calibrationOffset + (groupIndex * 64));
            var scaleOffset = checked(biasOffset + 32);
            EnsureRange(data, biasOffset, 64, "packed calibration group");
            for (var lane = 0; lane < 8; lane++)
            {
                var bias = ReadSingle(data, biasOffset + (lane * 4));
                var scale = ReadSingle(data, scaleOffset + (lane * 4));
                if (!float.IsFinite(bias) || !float.IsFinite(scale))
                {
                    throw new InvalidDataException("ANM2 packed calibration value is not finite.");
                }

                var packedIndex = (groupIndex * 8) + lane;
                if (packedIndex >= packedCount)
                {
                    continue;
                }

                for (var frameIndex = 0;
                     frameIndex < Anm2PackedGroupCodec.FrameCount;
                     frameIndex++)
                {
                    frames[frameIndex, packedIndex] =
                        bias + (values[frameIndex][lane] * scale);
                }
            }

            cursor += encodedLength;
        }

        if (cursor != streamEnd)
        {
            throw new InvalidDataException(
                $"ANM2 packed stream has {streamEnd - cursor} trailing bytes.");
        }

        return frames;
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset) =>
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]));

    private static float Lerp(float left, float right, float fraction) =>
        left + ((right - left) * fraction);

    private static void CheckCancellation(
        Action? workCheckpoint,
        CancellationToken cancellationToken)
    {
        workCheckpoint?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static int AlignDown16(int value) => value & ~0x0F;

    private static int AlignUp16(int value) => checked((value + 0x0F) & ~0x0F);

    private static int AlignUp4(int value) => checked((value + 0x03) & ~0x03);

    private static void EnsureRange(
        ReadOnlySpan<byte> data,
        int offset,
        int length,
        string description)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new InvalidDataException($"ANM2 {description} is outside the payload.");
        }
    }

    private sealed record SamplerLayout(
        ushort[] DurationWords,
        ushort[] PageSpans);

    private sealed record PageBaseData(
        float[] DirectValues,
        ComponentReference[] References,
        int PackedCount,
        int StreamBase,
        int TrackCount);

    private readonly record struct SamplePosition(
        double EvaluatedFrame,
        int PageIndex,
        int TableIndex,
        int FrameInSlot,
        float Fraction);

    private readonly record struct SlotKey(
        int PageIndex,
        int TableIndex);

    private readonly record struct ComponentReference(bool IsDirect, int Index);
}
