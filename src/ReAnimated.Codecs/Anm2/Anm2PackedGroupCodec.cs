using System.Buffers.Binary;

namespace ReAnimated.Codecs.Anm2;

public static class Anm2PackedGroupCodec
{
    public const int LaneCount = 8;
    public const int FrameCount = 16;

    public static byte[] Encode(IReadOnlyList<IReadOnlyList<short>> reconstructedValues)
    {
        Validate(reconstructedValues);
        var deltas = SecondOrderDeltas(reconstructedValues);
        var widths = deltas.Select(FrameWidth).ToArray();
        var byteCount = Align16(8 + widths.Sum());
        var wordCount = byteCount / 16;
        var laneWords = new ushort[LaneCount][];
        for (var lane = 0; lane < LaneCount; lane++)
        {
            laneWords[lane] = new ushort[wordCount];
        }

        for (var frameIndex = 0; frameIndex < widths.Length; frameIndex++)
        {
            var nibble = EncodeWidthNibble(widths[frameIndex]);
            if (frameIndex < LaneCount)
            {
                laneWords[frameIndex][0] |= (ushort)(nibble << 12);
            }
            else
            {
                laneWords[frameIndex - LaneCount][0] |= (ushort)(nibble << 8);
            }
        }

        var bitOffset = 8;
        for (var frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            var width = widths[frameIndex];
            if (width != 0)
            {
                for (var lane = 0; lane < LaneCount; lane++)
                {
                    InsertSignedBits(laneWords[lane], bitOffset, width, deltas[frameIndex][lane]);
                }
            }

            bitOffset += width;
        }

        var output = new byte[byteCount];
        for (var wordIndex = 0; wordIndex < wordCount; wordIndex++)
        {
            for (var lane = 0; lane < LaneCount; lane++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    output.AsSpan((wordIndex * 16) + (lane * 2)),
                    laneWords[lane][wordIndex]);
            }
        }

        return output;
    }

    public static short[][] Decode(ReadOnlySpan<byte> data, int maximumFrame = FrameCount - 1)
    {
        if (maximumFrame is < 0 or >= FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrame), "Frame must be in 0..15.");
        }

        if (data.Length < 16)
        {
            throw new InvalidDataException("Packed group must contain at least one 16-byte block.");
        }

        var widths = ReadWidths(data);
        var byteCount = Align16(8 + widths.Sum());
        if (data.Length < byteCount)
        {
            throw new InvalidDataException($"Packed group is truncated: {data.Length} < {byteCount}.");
        }

        var deltas = new short[maximumFrame + 1][];
        var bitOffset = 8;
        for (var frameIndex = 0; frameIndex <= maximumFrame; frameIndex++)
        {
            var width = widths[frameIndex];
            var frame = new short[LaneCount];
            for (var lane = 0; lane < LaneCount; lane++)
            {
                frame[lane] = ExtractSignedBits(data, lane, bitOffset, width);
            }

            deltas[frameIndex] = frame;
            bitOffset += width;
        }

        return IntegrateSecondOrder(deltas);
    }

    public static int GetEncodedLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
        {
            throw new InvalidDataException("Packed group must contain at least one 16-byte block.");
        }

        return Align16(8 + ReadWidths(data).Sum());
    }

    private static void Validate(IReadOnlyList<IReadOnlyList<short>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != FrameCount)
        {
            throw new ArgumentException($"Expected {FrameCount} frames, found {values.Count}.", nameof(values));
        }

        for (var frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            if (values[frameIndex].Count != LaneCount)
            {
                throw new ArgumentException(
                    $"Frame {frameIndex} has {values[frameIndex].Count} lanes; expected {LaneCount}.",
                    nameof(values));
            }
        }
    }

    private static short[][] SecondOrderDeltas(IReadOnlyList<IReadOnlyList<short>> values)
    {
        var deltas = new short[FrameCount][];
        for (var frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            var frame = new short[LaneCount];
            for (var lane = 0; lane < LaneCount; lane++)
            {
                var current = values[frameIndex][lane];
                frame[lane] = frameIndex switch
                {
                    0 => current,
                    1 => Wrap16(current - values[frameIndex - 1][lane]),
                    _ => Wrap16(
                        current -
                        Saturate16((2 * values[frameIndex - 1][lane]) - values[frameIndex - 2][lane])),
                };
            }

            deltas[frameIndex] = frame;
        }

        return deltas;
    }

    private static short[][] IntegrateSecondOrder(short[][] deltas)
    {
        var values = new short[deltas.Length][];
        for (var frameIndex = 0; frameIndex < deltas.Length; frameIndex++)
        {
            var frame = new short[LaneCount];
            for (var lane = 0; lane < LaneCount; lane++)
            {
                frame[lane] = frameIndex switch
                {
                    0 => deltas[frameIndex][lane],
                    1 => Wrap16(deltas[frameIndex][lane] + values[frameIndex - 1][lane]),
                    _ => Wrap16(
                        deltas[frameIndex][lane] +
                        Saturate16((2 * values[frameIndex - 1][lane]) - values[frameIndex - 2][lane])),
                };
            }

            values[frameIndex] = frame;
        }

        return values;
    }

    private static int FrameWidth(IReadOnlyList<short> frame)
    {
        var width = frame.Max(SignedWidth);
        return width == 15 ? 16 : width;
    }

    private static int SignedWidth(short value)
    {
        if (value == 0)
        {
            return 0;
        }

        for (var width = 1; width <= 16; width++)
        {
            if (width == 15)
            {
                continue;
            }

            var minimum = -(1 << (width - 1));
            var maximum = (1 << (width - 1)) - 1;
            if (value >= minimum && value <= maximum)
            {
                return width;
            }
        }

        throw new InvalidOperationException($"Value {value} cannot be represented in the packed stream.");
    }

    private static int[] ReadWidths(ReadOnlySpan<byte> data)
    {
        var widths = new int[FrameCount];
        for (var lane = 0; lane < LaneCount; lane++)
        {
            var word = BinaryPrimitives.ReadUInt16LittleEndian(data[(lane * 2)..]);
            widths[lane] = DecodeWidthNibble((word >> 12) & 0x0F);
            widths[lane + LaneCount] = DecodeWidthNibble((word >> 8) & 0x0F);
        }

        return widths;
    }

    private static void InsertSignedBits(
        ushort[] words,
        int bitOffset,
        int width,
        short value)
    {
        var mask = width == 16 ? 0xFFFFu : (1u << width) - 1u;
        var encoded = unchecked((uint)value) & mask;
        for (var bitIndex = 0; bitIndex < width; bitIndex++)
        {
            if ((encoded & (1u << (width - 1 - bitIndex))) == 0)
            {
                continue;
            }

            var absolute = bitOffset + bitIndex;
            var wordIndex = absolute / 16;
            var bitInWord = 15 - (absolute % 16);
            words[wordIndex] |= (ushort)(1 << bitInWord);
        }
    }

    private static short ExtractSignedBits(
        ReadOnlySpan<byte> data,
        int lane,
        int bitOffset,
        int width)
    {
        if (width == 0)
        {
            return 0;
        }

        uint value = 0;
        for (var bitIndex = 0; bitIndex < width; bitIndex++)
        {
            var absolute = bitOffset + bitIndex;
            var wordIndex = absolute / 16;
            var bitInWord = 15 - (absolute % 16);
            var word = BinaryPrimitives.ReadUInt16LittleEndian(
                data[((wordIndex * 16) + (lane * 2))..]);
            value = (value << 1) | (uint)((word >> bitInWord) & 1);
        }

        var signBit = 1u << (width - 1);
        var signed = (value & signBit) != 0
            ? (int)(value - (1u << width))
            : (int)value;
        return checked((short)signed);
    }

    private static int EncodeWidthNibble(int width) => width switch
    {
        16 => 15,
        >= 0 and <= 14 => width,
        _ => throw new ArgumentOutOfRangeException(nameof(width)),
    };

    private static int DecodeWidthNibble(int nibble) => nibble == 15 ? 16 : nibble;

    private static short Wrap16(int value) => unchecked((short)value);

    private static short Saturate16(int value) =>
        (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private static int Align16(int value) => (value + 15) & ~15;
}
