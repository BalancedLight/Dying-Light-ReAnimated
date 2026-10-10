using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.Anm2;

public sealed record AnimationScrSequence(
    string Name,
    string Anm2Name,
    float StartFrame,
    float EndFrame,
    float FramesPerSecond,
    int Enabled = 1,
    float Blend = 0.5f)
{
    // Kept for source compatibility with callers that used the old, misleading
    // names. These values are the native SeqTrack weight mode and weight time.
    public int WeightMode => Enabled;

    public float WeightTime => Blend;
}

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

    public ImmutableArray<AnimationScrEventRow> Events { get; init; } = [];

    public int WeightMode => Enabled;

    public float WeightTime => Blend;
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
        return BuildCore(ordered, default);
    }

    public static AnimationScrSections BuildWithEvents(IReadOnlyList<AnimationSequenceUse> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        AnimationSequenceUse[] ordered = sequences
            .OrderBy(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (AnimationSequenceUse sequence in ordered)
        {
            AnimationScriptCompilationValidator.ValidateSequence(sequence);
            if (sequence.Events.IsDefault)
            {
                throw new ArgumentException(
                    $"SeqTrack '{sequence.Name}' has an uninitialized event list.",
                    nameof(sequences));
            }

            foreach (AnimationEvent authored in sequence.Events)
            {
                AnimationScriptCompilationValidator.ValidateEvent(sequence, authored);
            }
        }

        AnimationScrSequence[] records = ordered
            .Select(static sequence => new AnimationScrSequence(
                sequence.Name,
                sequence.Anm2Name,
                checked((float)sequence.SourceStartFrame),
                checked((float)sequence.SourceEndFrame),
                checked((float)sequence.FPS),
                sequence.WeightMode,
                checked((float)sequence.WeightTime)))
            .ToArray();
        ValidateSequenceSet(records);
        var eventRows = new List<ImmutableArray<AnimationScrEventRow>>(ordered.Length);
        foreach (AnimationSequenceUse sequence in ordered)
        {
            var rows = ImmutableArray.CreateBuilder<AnimationScrEventRow>(sequence.Events.Length);
            foreach (AnimationEvent authored in sequence.Events)
            {
                if (!authored.Actions.IsDefaultOrEmpty)
                {
                    throw new NotSupportedException(
                        $"Compiled actions for SeqTrack '{sequence.Name}' cannot be built because the action-bank and fixup output has not been validated against a Windows compiler resource.");
                }

                if (authored.RawActionReference is uint actionReference &&
                    actionReference != uint.MaxValue)
                {
                    throw new NotSupportedException(
                        $"SeqTrack '{sequence.Name}' carries a compiled action reference but no matching action-bank payload was supplied.");
                }

                ushort eventId = authored.EventId is int id && id is >= 0 and <= ushort.MaxValue
                    ? (ushort)id
                    : throw new ArgumentOutOfRangeException(
                        nameof(sequences),
                        $"SeqTrack '{sequence.Name}' has an event without a numeric ID from 0 to 65535.");
                ushort ticks = authored.RawTicks is ushort rawTicks &&
                    authored.LocalFrame == rawTicks / 5d
                        ? rawTicks
                        : AnimationEventTiming.ToTicks(authored.LocalFrame);
                ushort flags = (ushort)(
                    (authored.RawFlags & ~AnimationScrEventCodec.MustSendFlag) |
                    (authored.Delivery == AnimationEventDelivery.MustSendEvent
                        ? AnimationScrEventCodec.MustSendFlag
                        : 0));
                rows.Add(new AnimationScrEventRow(
                    ticks,
                    eventId,
                    authored.RawActionReference ?? uint.MaxValue,
                    authored.RequiredSlot ?? -1,
                    flags,
                    0));
            }

            eventRows.Add(rows.ToImmutable());
        }

        return BuildCore(records, eventRows.ToImmutableArray());
    }

    private static AnimationScrSections BuildCore(
        AnimationScrSequence[] ordered,
        ImmutableArray<ImmutableArray<AnimationScrEventRow>> eventRows)
    {
        ValidateSequenceSet(ordered);
        byte[] names = BuildNames(ordered.Select(static sequence => sequence.Name.ToLowerInvariant()));
        int[] offsets = ReadSequentialNameOffsets(names, ordered.Length);
        int totalEvents = eventRows.IsDefault
            ? 0
            : eventRows.Aggregate(0, static (sum, rows) => checked(sum + rows.Length));
        int eventTableBytes = checked(totalEvents * EventRecordSize);
        int recordsLength = checked(RecordSize * ordered.Length);
        var section0 = new byte[checked(recordsLength + eventTableBytes + names.Length)];
        for (var index = 0; index < ordered.Length; index++)
        {
            WriteRecord(
                section0.AsSpan(index * RecordSize, RecordSize),
                ordered[index],
                offsets[index],
                eventRows.IsDefault ? 0 : checked((uint)eventRows[index].Length));
        }

        int cursor = recordsLength;
        if (!eventRows.IsDefault)
        {
            for (int sequenceIndex = 0; sequenceIndex < eventRows.Length; sequenceIndex++)
            {
                foreach (AnimationScrEventRow row in eventRows[sequenceIndex])
                {
                    AnimationScrEventCodec.WriteRow(
                        section0.AsSpan(cursor, EventRecordSize),
                        row);
                    cursor = checked(cursor + EventRecordSize);
                }
            }
        }

        names.CopyTo(section0, checked(recordsLength + eventTableBytes));
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
        int eventRowOffset = recordBytes;
        bool hasCanonicalEventTableLayout =
            expectedEventTableLength == nameTableOffset - recordBytes;
        for (var index = 0; index < count; index++)
        {
            int recordOffset = index * RecordSize;
            ReadOnlySpan<byte> record = section0.Slice(recordOffset, RecordSize);
            uint rawEventCount =
                BinaryPrimitives.ReadUInt32LittleEndian(record[48..]);
            int sequenceEventCount = rawEventCount <= int.MaxValue
                ? checked((int)rawEventCount)
                : throw new InvalidDataException(
                    $"AnimationScr sequence at record {recordOffset} declares too many events.");
            ImmutableArray<AnimationScrEventRow> events = [];
            if (hasCanonicalEventTableLayout)
            {
                events = AnimationScrEventCodec.ReadRows(
                    section0,
                    eventRowOffset,
                    sequenceEventCount);
                eventRowOffset = checked(
                    eventRowOffset +
                    (sequenceEventCount * EventRecordSize));
            }

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
                sequenceEventCount)
            {
                RawEventCount = rawEventCount,
                Events = events,
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
            HasCanonicalEventTableLayout = hasCanonicalEventTableLayout,
        };
    }

    /// <summary>
    /// Maps a canonical compiled AnimationScr to editable sequence uses while
    /// retaining the raw row data needed for lossless event round-trips.
    /// </summary>
    public static ImmutableArray<AnimationSequenceUse> ReadSequenceUses(
        AnimationScrSections sections,
        Func<string, string> anm2NameResolver)
    {
        ArgumentNullException.ThrowIfNull(anm2NameResolver);
        ParsedAnimationScr parsed = Parse(sections);
        if (!parsed.HasCanonicalEventTableLayout ||
            parsed.Sequences.Length != parsed.DeclaredSequenceCount)
        {
            throw new NotSupportedException(
                "This AnimationScr resource does not have a supported canonical sequence and event layout.");
        }

        return parsed.Sequences
            .Select(sequence =>
            {
                string anm2Name = anm2NameResolver(sequence.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(anm2Name);
                return new AnimationSequenceUse
                {
                    Name = sequence.Name,
                    Anm2Name = anm2Name,
                    SourceStartFrame = sequence.StartFrame,
                    SourceEndFrame = sequence.EndFrame,
                    FPS = sequence.FramesPerSecond,
                    WeightMode = sequence.WeightMode,
                    WeightTime = sequence.WeightTime,
                    Events = sequence.Events.Select(ToDomainEvent).ToImmutableArray(),
                };
            })
            .ToImmutableArray();
    }

    public static ImmutableArray<AnimationSequenceUse> ReadSequenceUses(
        AnimationScrSections sections,
        IReadOnlyDictionary<string, string> anm2Names)
    {
        ArgumentNullException.ThrowIfNull(anm2Names);
        return ReadSequenceUses(
            sections,
            name => anm2Names.TryGetValue(name, out string? anm2Name)
                ? anm2Name
                : throw new KeyNotFoundException(
                    $"No ANM2 name was supplied for compiled SeqTrack '{name}'."));
    }

    public static AnimationScrSections PatchRanges(
        AnimationScrSections sections,
        IReadOnlyDictionary<string, (float Start, float End, float FramesPerSecond)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ParsedAnimationScr parsed = Parse(sections);
        Dictionary<string, ParsedAnimationScrSequence> byName = parsed.Sequences
            .ToDictionary(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase);
        foreach (ParsedAnimationScrSequence sequence in parsed.Sequences)
        {
            ValidateRange(sequence.StartFrame, sequence.EndFrame, sequence.FramesPerSecond);
            if (!float.IsFinite(sequence.WeightTime))
            {
                throw new InvalidDataException($"AnimationScr sequence '{sequence.Name}' has a non-finite weight time.");
            }
        }

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

    /// <summary>
    /// Rebuilds the canonical event table while preserving existing action references.
    /// New rows use the native no-action sentinel unless an existing reference is supplied.
    /// </summary>
    public static AnimationScrSections PatchEvents(
        AnimationScrSections sections,
        IReadOnlyDictionary<string, ImmutableArray<AnimationEvent>> eventsBySequence)
    {
        ArgumentNullException.ThrowIfNull(eventsBySequence);
        ParsedAnimationScr parsed = Parse(sections);
        if (!parsed.HasCanonicalEventTableLayout)
        {
            throw new NotSupportedException(
                "This AnimationScr resource does not have a canonical event table; its event rows cannot be edited safely.");
        }

        if (parsed.Sequences.Length != parsed.DeclaredSequenceCount)
        {
            throw new NotSupportedException(
                "This AnimationScr resource contains unsupported sequence records; event allocation cannot be rewritten safely.");
        }

        Dictionary<string, ParsedAnimationScrSequence> byName = parsed.Sequences
            .ToDictionary(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AnimationSequenceUse> validationSequences = ReadSequenceUses(
                sections,
                name => name + ".anm2")
            .ToDictionary(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase);
        foreach (string name in eventsBySequence.Keys)
        {
            if (!byName.ContainsKey(name))
            {
                throw new KeyNotFoundException(
                    $"AnimationScr section is missing sequence '{name}'.");
            }
        }

        var rowsBySequence = new ImmutableArray<AnimationScrEventRow>[parsed.DeclaredSequenceCount];
        foreach (ParsedAnimationScrSequence sequence in parsed.Sequences)
        {
            ImmutableArray<AnimationEvent> authoredEvents = eventsBySequence.TryGetValue(
                sequence.Name,
                out ImmutableArray<AnimationEvent> replacement)
                    ? replacement
                    : sequence.Events.Select(ToDomainEvent).ToImmutableArray();
            if (authoredEvents.IsDefault)
            {
                throw new ArgumentException(
                    $"Compiled event edits for '{sequence.Name}' use an uninitialized event list.",
                    nameof(eventsBySequence));
            }

            AnimationSequenceUse validationSequence = validationSequences[sequence.Name];
            AnimationScriptCompilationValidator.ValidateSequence(validationSequence);

            var rows = ImmutableArray.CreateBuilder<AnimationScrEventRow>(authoredEvents.Length);
            for (var index = 0; index < authoredEvents.Length; index++)
            {
                AnimationEvent authored = authoredEvents[index];
                AnimationScriptCompilationValidator.ValidateEvent(validationSequence, authored);
                AnimationScrEventRow? original = authored.RawActionReference is not null &&
                    index < sequence.Events.Length
                    ? sequence.Events[index]
                    : null;
                if (!authored.Actions.IsDefaultOrEmpty)
                {
                    throw new NotSupportedException(
                        $"Compiled action editing for '{sequence.Name}' is unavailable. Export the .scr source.");
                }

                ushort eventId = authored.EventId is int id && id is >= 0 and <= ushort.MaxValue
                    ? (ushort)id
                    : throw new ArgumentOutOfRangeException(
                        nameof(eventsBySequence),
                        $"Compiled event '{sequence.Name}' row {index} needs a numeric event ID from 0 to 65535.");
                if (authored.RawActionReference is { } reference && reference != uint.MaxValue && !parsed.Sequences.SelectMany(s => s.Events).Any(e => e.ActionReference == reference))
                    throw new NotSupportedException("The edited event refers to an action bank outside this resource.");
                short requiredSlot = authored.RequiredSlot!.Value;
                ushort ticks = authored.RawTicks is ushort rawTicks && authored.LocalFrame == rawTicks / 5d
                    ? rawTicks
                    : AnimationEventTiming.ToTicks(authored.LocalFrame);
                ushort flags = (ushort)(
                    (authored.RawFlags & ~AnimationScrEventCodec.MustSendFlag) |
                    (authored.Delivery == AnimationEventDelivery.MustSendEvent
                        ? AnimationScrEventCodec.MustSendFlag
                        : 0));
                rows.Add(new AnimationScrEventRow(
                    ticks,
                    eventId,
                    authored.RawActionReference ?? uint.MaxValue,
                    requiredSlot,
                    flags,
                    0));
            }

            rowsBySequence[sequence.RecordOffset / RecordSize] = rows.ToImmutable();
        }

        int recordBytes = checked(parsed.DeclaredSequenceCount * RecordSize);
        int eventCount = rowsBySequence.Sum(static rows => rows.Length);
        int eventBytes = checked(eventCount * EventRecordSize);
        ReadOnlySpan<byte> namesAndTrailingData = sections.RecordsAndNames.AsSpan(parsed.NameTableOffset);
        byte[] section0 = new byte[checked(recordBytes + eventBytes + namesAndTrailingData.Length)];
        sections.RecordsAndNames.AsSpan(0, recordBytes).CopyTo(section0);
        int cursor = recordBytes;
        for (var sequenceIndex = 0; sequenceIndex < rowsBySequence.Length; sequenceIndex++)
        {
            ImmutableArray<AnimationScrEventRow> rows = rowsBySequence[sequenceIndex];
            if (rows.IsDefault)
            {
                throw new NotSupportedException(
                    $"AnimationScr sequence record {sequenceIndex} has no editable identity.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                section0.AsSpan((sequenceIndex * RecordSize) + 48),
                checked((uint)rows.Length));
            foreach (AnimationScrEventRow row in rows)
            {
                AnimationScrEventCodec.WriteRow(section0.AsSpan(cursor, EventRecordSize), row);
                cursor = checked(cursor + EventRecordSize);
            }
        }

        namesAndTrailingData.CopyTo(section0.AsSpan(recordBytes + eventBytes));
        return new AnimationScrSections(section0, sections.IndexAndNames.ToArray());
    }

    private static AnimationEvent ToDomainEvent(AnimationScrEventRow row) => new()
    {
        LocalFrame = row.LocalFrame,
        EventId = row.EventId,
        IdExpression = row.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RequiredSlot = row.RequiredSlot,
        SlotExpression = row.RequiredSlot.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Delivery = row.MustSend
            ? AnimationEventDelivery.MustSendEvent
            : AnimationEventDelivery.Event,
        RawTicks = row.Ticks,
        RawFlags = row.RawFlags,
        RawActionReference = row.ActionReference,
    };

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
                "AnimationScr resources with auxiliary/event data between records and names cannot be appended losslessly; preserve the base resource or use a different library identity.");
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
                checked(oldNames.Length + newOffsets[index]),
                0);
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
        int nameOffset,
        uint eventCount)
    {
        ValidateSequence(sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, checked((uint)nameOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], RecordMagic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], sequence.WeightMode);
        WriteSingle(destination[20..], sequence.WeightTime);
        WriteSingle(destination[24..], sequence.FramesPerSecond);
        WriteSingle(destination[28..], sequence.StartFrame);
        WriteSingle(destination[32..], sequence.EndFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[48..], eventCount);
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
        if (!float.IsFinite(sequence.WeightTime))
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
