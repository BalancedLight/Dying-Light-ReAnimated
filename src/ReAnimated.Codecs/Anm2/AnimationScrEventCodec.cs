using System.Buffers.Binary;

namespace ReAnimated.Codecs.Anm2;

/// <summary>A compiled SeqTrack notification row and its preserved action-bank reference.</summary>
public sealed record AnimationScrEventRow(
    ushort Ticks,
    ushort EventId,
    uint ActionReference,
    short RequiredSlot,
    ushort RawFlags,
    int RecordOffset)
{
    public double LocalFrame => Ticks / 5d;

    public bool MustSend => (RawFlags & AnimationScrEventCodec.MustSendFlag) != 0;
}

/// <summary>Reads and writes the verified 12-byte compiled SeqTrack event row.</summary>
public static class AnimationScrEventCodec
{
    public const ushort MustSendFlag = 1;

    public static AnimationScrEventRow ReadRow(ReadOnlySpan<byte> source, int recordOffset = 0)
    {
        if (source.Length < AnimationScrCodec.EventRecordSize)
        {
            throw new InvalidDataException("AnimationScr event row is truncated.");
        }

        return new AnimationScrEventRow(
            BinaryPrimitives.ReadUInt16LittleEndian(source),
            BinaryPrimitives.ReadUInt16LittleEndian(source[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
            BinaryPrimitives.ReadInt16LittleEndian(source[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[10..]),
            recordOffset);
    }

    public static void WriteRow(Span<byte> destination, AnimationScrEventRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (destination.Length < AnimationScrCodec.EventRecordSize)
        {
            throw new InvalidDataException("AnimationScr event row destination is too small.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, row.Ticks);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], row.EventId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], row.ActionReference);
        BinaryPrimitives.WriteInt16LittleEndian(destination[8..], row.RequiredSlot);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], row.RawFlags);
    }

    internal static System.Collections.Immutable.ImmutableArray<AnimationScrEventRow> ReadRows(
        ReadOnlySpan<byte> source,
        int offset,
        int count)
    {
        int byteCount = checked(count * AnimationScrCodec.EventRecordSize);
        if (offset < 0 || offset > source.Length - byteCount)
        {
            throw new InvalidDataException("AnimationScr event table is truncated.");
        }

        var rows = System.Collections.Immutable.ImmutableArray.CreateBuilder<AnimationScrEventRow>(count);
        for (var index = 0; index < count; index++)
        {
            int rowOffset = checked(offset + (index * AnimationScrCodec.EventRecordSize));
            rows.Add(ReadRow(
                source.Slice(rowOffset, AnimationScrCodec.EventRecordSize),
                rowOffset));
        }

        return rows.MoveToImmutable();
    }
}
