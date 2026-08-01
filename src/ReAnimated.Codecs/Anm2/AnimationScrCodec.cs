using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace ReAnimated.Codecs.Anm2;

public sealed record AnimationScrSequence(
    string Name,
    string Anm2Name,
    float StartFrame,
    float EndFrame,
    float FramesPerSecond,
    int Enabled = 1,
    float Blend = 0.5f);

public sealed record AnimationScrSections(byte[] RecordsAndNames, byte[] IndexAndNames);

public sealed record ParsedAnimationScrSequence(
    string Name,
    int NameOffset,
    int RecordOffset,
    int Enabled,
    float Blend,
    float FramesPerSecond,
    float StartFrame,
    float EndFrame,
    int EventCount)
{
    public uint RawEventCount { get; init; }
}

public sealed record ParsedAnimationScr(
    int DeclaredSequenceCount,
    int NameTableOffset,
    ImmutableArray<ParsedAnimationScrSequence> Sequences)
{
    public int OpaquePayloadOffset { get; init; }

    public int OpaquePayloadLength { get; init; }

    public ulong TotalDeclaredEventCount { get; init; }

    public int? ExpectedEventTableLength { get; init; }

    public bool HasCanonicalEventTableLayout { get; init; }
}

public static class AnimationScrCodec
{
    public const int RecordSize = 56;
    public const int EventRecordSize = 12;
    public const uint RecordMagic = 471;
    public const uint RecordSentinel = 0x7FFA;
    public const uint Retail155RecordMagic = 588;
    public const uint Retail155RecordSentinel = 0x7FF9;
    private const int MaximumSequences = 1_000_000;
    private const int MaximumNameBytes = 160;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SearchValues<byte> NameBytes =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_-."u8);

    public static AnimationScrSections Build(IEnumerable<AnimationScrSequence> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        AnimationScrSequence[] ordered = sequences
            .OrderBy(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ValidateSequenceSet(ordered);
        byte[] names = BuildNames(ordered.Select(static sequence => sequence.Name.ToLowerInvariant()));
        int[] offsets = ReadSequentialNameOffsets(names, ordered.Length);
        var section0 = new byte[checked((RecordSize * ordered.Length) + names.Length)];
        for (var index = 0; index < ordered.Length; index++)
        {
            WriteRecord(
                section0.AsSpan(index * RecordSize, RecordSize),
                ordered[index],
                offsets[index]);
        }

        names.CopyTo(section0, RecordSize * ordered.Length);
        var section1 = new byte[checked(8 + names.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            section1,
            checked((uint)ordered.Length));
        names.CopyTo(section1, 8);
        return new AnimationScrSections(section0, section1);
    }

    public static ParsedAnimationScr Parse(AnimationScrSections sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(sections.RecordsAndNames);
        ArgumentNullException.ThrowIfNull(sections.IndexAndNames);
        ReadOnlySpan<byte> section0 = sections.RecordsAndNames;
        ReadOnlySpan<byte> section1 = sections.IndexAndNames;
        if (section1.Length < 8)
        {
            throw new InvalidDataException("AnimationScr section 1 is smaller than its header.");
        }

        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(section1));
        if (count > MaximumSequences)
        {
            throw new InvalidDataException(
                $"AnimationScr declares an unsafe sequence count of {count:N0}.");
        }

        int recordBytes = checked(count * RecordSize);
        if (recordBytes > section0.Length)
        {
            throw new InvalidDataException(
                $"AnimationScr section 0 is too small for {count:N0} records.");
        }

        ulong totalDeclaredEventCount = 0;
        for (var index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> record = section0.Slice(
                index * RecordSize,
                RecordSize);
            totalDeclaredEventCount = checked(
                totalDeclaredEventCount +
                BinaryPrimitives.ReadUInt32LittleEndian(record[48..]));
        }

        int? expectedEventTableLength =
            totalDeclaredEventCount <=
            (ulong)(int.MaxValue / EventRecordSize)
                ? checked(
                    (int)totalDeclaredEventCount *
                    EventRecordSize)
                : null;
        int nameTableOffset = FindNameTableOffset(
            section0,
            count,
            recordBytes,
            expectedEventTableLength);
        var parsed = ImmutableArray.CreateBuilder<ParsedAnimationScrSequence>(count);
        for (var index = 0; index < count; index++)
        {
            int recordOffset = index * RecordSize;
            ReadOnlySpan<byte> record = section0.Slice(recordOffset, RecordSize);
            uint rawEventCount =
                BinaryPrimitives.ReadUInt32LittleEndian(record[48..]);
            if (!IsSupportedRecordMarkerPair(record))
            {
                continue;
            }

            uint rawNameOffset =
                BinaryPrimitives.ReadUInt32LittleEndian(record);
            if (rawNameOffset > int.MaxValue)
            {
                throw new InvalidDataException(
                    "AnimationScr name offset is outside section 0.");
            }

            int nameOffset = (int)rawNameOffset;
            if (nameOffset > section0.Length - nameTableOffset)
            {
                throw new InvalidDataException(
                    "AnimationScr name offset is outside section 0.");
            }

            string name = ReadName(section0, nameTableOffset + nameOffset);
            if (name.Length == 0)
            {
                continue;
            }

            parsed.Add(new ParsedAnimationScrSequence(
                name,
                nameOffset,
                recordOffset,
                BinaryPrimitives.ReadInt32LittleEndian(record[16..]),
                ReadSingle(record[20..]),
                ReadSingle(record[24..]),
                ReadSingle(record[28..]),
                ReadSingle(record[32..]),
                unchecked((int)rawEventCount))
            {
                RawEventCount = rawEventCount,
            });
        }

        int opaquePayloadLength = checked(nameTableOffset - recordBytes);
        return new ParsedAnimationScr(
            count,
            nameTableOffset,
            parsed.ToImmutable())
        {
            OpaquePayloadOffset = recordBytes,
            OpaquePayloadLength = opaquePayloadLength,
            TotalDeclaredEventCount = totalDeclaredEventCount,
            ExpectedEventTableLength = expectedEventTableLength,
            HasCanonicalEventTableLayout =
                expectedEventTableLength == opaquePayloadLength,
        };
    }

    public static AnimationScrSections PatchRanges(
        AnimationScrSections sections,
        IReadOnlyDictionary<string, (float Start, float End, float FramesPerSecond)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ParsedAnimationScr parsed = Parse(sections);
        Dictionary<string, ParsedAnimationScrSequence> byName = parsed.Sequences
            .ToDictionary(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase);
        byte[] section0 = sections.RecordsAndNames.ToArray();
        foreach ((string name, (float start, float end, float fps)) in ranges)
        {
            if (!byName.TryGetValue(name, out ParsedAnimationScrSequence? sequence))
            {
                throw new KeyNotFoundException(
                    $"AnimationScr section is missing sequence '{name}'.");
            }

            ValidateRange(start, end, fps);
            WriteSingle(section0.AsSpan(sequence.RecordOffset + 24), fps);
            WriteSingle(section0.AsSpan(sequence.RecordOffset + 28), start);
            WriteSingle(section0.AsSpan(sequence.RecordOffset + 32), end);
        }

        return new AnimationScrSections(section0, sections.IndexAndNames.ToArray());
    }

    public static AnimationScrSections Append(
        AnimationScrSections sections,
        IEnumerable<AnimationScrSequence> additions)
    {
        ArgumentNullException.ThrowIfNull(additions);
        AnimationScrSequence[] ordered = additions
            .OrderBy(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ordered.Length == 0)
        {
            return new AnimationScrSections(
                sections.RecordsAndNames.ToArray(),
                sections.IndexAndNames.ToArray());
        }

        ValidateSequenceSet(ordered);
        ParsedAnimationScr parsed = Parse(sections);
        int recordsEnd = checked(parsed.DeclaredSequenceCount * RecordSize);
        if (parsed.NameTableOffset != recordsEnd)
        {
            throw new NotSupportedException(
                "AnimationScr resources with auxiliary/event data between records and names cannot be appended.");
        }

        HashSet<string> existing = parsed.Sequences
            .Select(static sequence => sequence.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] duplicates = ordered
            .Where(sequence => existing.Contains(sequence.Name))
            .Select(static sequence => sequence.Name)
            .ToArray();
        if (duplicates.Length != 0)
        {
            throw new InvalidOperationException(
                $"AnimationScr already contains: {string.Join(", ", duplicates)}.");
        }

        byte[] newNames = BuildNames(
            ordered.Select(static sequence => sequence.Name.ToLowerInvariant()));
        int[] newOffsets = ReadSequentialNameOffsets(newNames, ordered.Length);
        ReadOnlySpan<byte> oldNames = sections.RecordsAndNames.AsSpan(recordsEnd);
        var newRecords = new byte[checked(RecordSize * ordered.Length)];
        for (var index = 0; index < ordered.Length; index++)
        {
            WriteRecord(
                newRecords.AsSpan(index * RecordSize, RecordSize),
                ordered[index],
                checked(oldNames.Length + newOffsets[index]));
        }

        var section0 = new byte[checked(
            recordsEnd + newRecords.Length + oldNames.Length + newNames.Length)];
        sections.RecordsAndNames.AsSpan(0, recordsEnd).CopyTo(section0);
        newRecords.CopyTo(section0, recordsEnd);
        oldNames.CopyTo(section0.AsSpan(recordsEnd + newRecords.Length));
        newNames.CopyTo(
            section0,
            recordsEnd + newRecords.Length + oldNames.Length);

        if (sections.IndexAndNames.Length < 8)
        {
            throw new InvalidDataException("AnimationScr section 1 is smaller than its header.");
        }

        var section1 = new byte[checked(sections.IndexAndNames.Length + newNames.Length)];
        sections.IndexAndNames.CopyTo(section1, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            section1,
            checked((uint)(parsed.DeclaredSequenceCount + ordered.Length)));
        newNames.CopyTo(section1, sections.IndexAndNames.Length);
        AnimationScrSections result = new(section0, section1);
        ParsedAnimationScr roundTrip = Parse(result);
        foreach (AnimationScrSequence addition in ordered)
        {
            if (!roundTrip.Sequences.Any(sequence =>
                    string.Equals(
                        sequence.Name,
                        addition.Name,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    $"Appended AnimationScr sequence '{addition.Name}' did not round-trip.");
            }
        }

        return result;
    }

    private static void WriteRecord(
        Span<byte> destination,
        AnimationScrSequence sequence,
        int nameOffset)
    {
        ValidateSequence(sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, checked((uint)nameOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], RecordMagic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], sequence.Enabled);
        WriteSingle(destination[20..], sequence.Blend);
        WriteSingle(destination[24..], sequence.FramesPerSecond);
        WriteSingle(destination[28..], sequence.StartFrame);
        WriteSingle(destination[32..], sequence.EndFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[52..], RecordSentinel);
    }

    private static void ValidateSequenceSet(AnimationScrSequence[] sequences)
    {
        if (sequences.Length > MaximumSequences)
        {
            throw new ArgumentException("Too many AnimationScr sequences.", nameof(sequences));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationScrSequence sequence in sequences)
        {
            ValidateSequence(sequence);
            if (!names.Add(sequence.Name))
            {
                throw new ArgumentException(
                    $"AnimationScr sequence '{sequence.Name}' is duplicated.",
                    nameof(sequences));
            }
        }
    }

    private static void ValidateSequence(AnimationScrSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequence.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequence.Anm2Name);
        if (sequence.Name.Contains('\0') ||
            sequence.Name.Contains('\r') ||
            sequence.Name.Contains('\n') ||
            Encoding.UTF8.GetByteCount(sequence.Name) > MaximumNameBytes)
        {
            throw new ArgumentException(
                $"AnimationScr sequence name '{sequence.Name}' is invalid.");
        }

        ValidateRange(
            sequence.StartFrame,
            sequence.EndFrame,
            sequence.FramesPerSecond);
        if (!float.IsFinite(sequence.Blend))
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }
    }

    private static void ValidateRange(float start, float end, float fps)
    {
        if (!float.IsFinite(start) ||
            !float.IsFinite(end) ||
            !float.IsFinite(fps) ||
            start < 0 ||
            end < start ||
            fps <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                "AnimationScr frame ranges and rate must be finite and ordered.");
        }
    }

    private static byte[] BuildNames(IEnumerable<string> names)
    {
        using var output = new MemoryStream();
        foreach (string name in names)
        {
            byte[] encoded = StrictUtf8.GetBytes(name);
            output.Write(encoded);
            output.WriteByte(0);
        }

        return output.ToArray();
    }

    private static int[] ReadSequentialNameOffsets(ReadOnlySpan<byte> names, int count)
    {
        var result = new int[count];
        var cursor = 0;
        for (var index = 0; index < count; index++)
        {
            result[index] = cursor;
            int terminator = names[cursor..].IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("AnimationScr name table is truncated.");
            }

            cursor = checked(cursor + terminator + 1);
        }

        if (cursor != names.Length)
        {
            throw new InvalidDataException("AnimationScr name table has trailing bytes.");
        }

        return result;
    }

    private static int FindNameTableOffset(
        ReadOnlySpan<byte> section0,
        int count,
        int recordBytes,
        int? expectedEventTableLength)
    {
        if (expectedEventTableLength is int eventTableLength &&
            eventTableLength <= section0.Length - recordBytes)
        {
            int canonicalOffset = recordBytes + eventTableLength;
            int sampleCount = Math.Min(count, 128);
            if (sampleCount > 0 &&
                ScoreNameTableOffset(
                    section0,
                    count,
                    canonicalOffset) == sampleCount)
            {
                return canonicalOffset;
            }
        }

        int simpleOffset = recordBytes;
        int bestOffset = -1;
        int bestRun = 0;
        int position = simpleOffset;
        while (position < section0.Length)
        {
            bool startsAfterNull = position == 0 || section0[position - 1] == 0;
            if (!startsAfterNull || !IsNameStart(section0[position]))
            {
                position++;
                continue;
            }

            int run = NameRunLength(
                section0,
                position,
                out int nextPosition);
            if (run > bestRun)
            {
                bestRun = run;
                bestOffset = position;
            }

            // A valid run is measured once and skipped as a unit. Rechecking
            // every suffix would rescan the remaining names and make the
            // malformed/auxiliary fallback quadratic.
            position = Math.Max(position + 1, nextPosition);
        }

        if (bestRun <= 0 ||
            ScoreNameTableOffset(section0, count, bestOffset) <= 0)
        {
            throw new InvalidDataException(
                "Could not locate the AnimationScr sequence-name table.");
        }

        return bestOffset;
    }

    private static int ScoreNameTableOffset(
        ReadOnlySpan<byte> section0,
        int count,
        int candidateOffset)
    {
        if (candidateOffset < 0 || candidateOffset > section0.Length)
        {
            return 0;
        }

        int score = 0;
        for (var index = 0; index < Math.Min(count, 128); index++)
        {
            uint rawNameOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                section0[(index * RecordSize)..]);
            if (rawNameOffset <= int.MaxValue &&
                rawNameOffset <=
                (uint)(section0.Length - candidateOffset) &&
                LooksLikeName(
                    section0,
                    candidateOffset + (int)rawNameOffset))
            {
                score++;
            }
        }

        return score;
    }

    private static bool IsSupportedRecordMarkerPair(
        ReadOnlySpan<byte> record)
    {
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
        uint sentinel = BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
        return magic == RecordMagic && sentinel == RecordSentinel ||
            magic == Retail155RecordMagic &&
            sentinel == Retail155RecordSentinel;
    }

    private static int NameRunLength(
        ReadOnlySpan<byte> data,
        int offset,
        out int endOffset)
    {
        int count = 0;
        int cursor = offset;
        while (cursor < data.Length && IsNameStart(data[cursor]))
        {
            int searchLength = Math.Min(
                MaximumNameBytes + 1,
                data.Length - cursor);
            int terminator = data
                .Slice(cursor, searchLength)
                .IndexOf((byte)0);
            if (terminator <= 0 || terminator > MaximumNameBytes)
            {
                break;
            }

            ReadOnlySpan<byte> candidate = data.Slice(cursor, terminator);
            if (candidate.ContainsAnyExcept(NameBytes))
            {
                break;
            }

            count++;
            cursor = checked(cursor + terminator + 1);
        }

        endOffset = cursor;
        return count;
    }

    private static bool LooksLikeName(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset >= data.Length || !IsNameStart(data[offset]))
        {
            return false;
        }

        int searchLength = Math.Min(
            MaximumNameBytes + 1,
            data.Length - offset);
        int terminator = data
            .Slice(offset, searchLength)
            .IndexOf((byte)0);
        return terminator is > 0 and <= MaximumNameBytes;
    }

    private static bool IsNameStart(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' ||
        value is >= (byte)'0' and <= (byte)'9' ||
        value == (byte)'_';

    private static string ReadName(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
        {
            throw new InvalidDataException("AnimationScr name offset is outside section 0.");
        }

        int available = Math.Min(MaximumNameBytes + 1, data.Length - offset);
        int terminator = data.Slice(offset, available).IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("AnimationScr name is not NUL terminated.");
        }

        try
        {
            return StrictUtf8.GetString(data.Slice(offset, terminator));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "AnimationScr name is not valid UTF-8.",
                exception);
        }
    }

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));

    private static void WriteSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(value));
}
