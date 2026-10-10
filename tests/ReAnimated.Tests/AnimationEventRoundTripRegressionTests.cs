using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

public sealed class AnimationEventRoundTripRegressionTests
{
    [Theory]
    [InlineData("START_FRAME, 10, 30, 1, 0.5")]
    [InlineData("0, END_FRAME, 30, 1, 0.5")]
    [InlineData("0, 10, FPS_VALUE, 1, 0.5")]
    [InlineData("0, 10, 30, WEIGHT_MODE, 0.5")]
    [InlineData("0, 10, 30, 1, WEIGHT_TIME")]
    [InlineData("0, , 30, 1, 0.5")]
    [InlineData("0, 10, 30, , 0.5")]
    [InlineData("0, 10, 30, 1, ")]
    public void BuildWithEventsRejectsUnresolvedSequenceFields(string fields)
    {
        AnimationSequenceUse sequence = Assert.Single(AnimationScriptTextCodec.Read(
            $"SeqTrack(\"clip\", \"clip.anm2\", {fields})").Sequences);

        Assert.Throws<InvalidDataException>(() => AnimationScrCodec.BuildWithEvents([sequence]));
    }

    [Fact]
    public void BuildWithEventsRejectsUnresolvedEventFrameAndSlot()
    {
        AnimationSequenceUse unresolvedFrame = ReadSequence("Event(FRAME_MISSING, 1001, 0)");
        AnimationSequenceUse blankFrame = ReadSequence("Event(, 1001, 0)");
        AnimationSequenceUse unresolvedSlot = ReadSequence("Event(2, 1001, SLOT_MISSING)");

        Assert.Throws<InvalidDataException>(() => AnimationScrCodec.BuildWithEvents([unresolvedFrame]));
        Assert.Throws<InvalidDataException>(() => AnimationScrCodec.BuildWithEvents([blankFrame]));
        Assert.Throws<InvalidDataException>(() => AnimationScrCodec.BuildWithEvents([unresolvedSlot]));
    }

    [Fact]
    public void PatchEventsRejectsMissingOrUnresolvedSlot()
    {
        AnimationScrSections sections = AnimationScrCodec.BuildWithEvents([SequenceWithEvent(2, 1001, 0)]);
        AnimationEvent original = Assert.Single(Assert.Single(AnimationScrCodec.ReadSequenceUses(sections, name => name + ".anm2")).Events);

        Assert.Throws<InvalidDataException>(() => AnimationScrCodec.PatchEvents(
            sections,
            new Dictionary<string, ImmutableArray<AnimationEvent>>
            {
                ["clip"] = [original with { RequiredSlot = null, SlotExpression = "" }],
            }));
        Assert.Throws<InvalidDataException>(() => AnimationScrCodec.PatchEvents(
            sections,
            new Dictionary<string, ImmutableArray<AnimationEvent>>
            {
                ["clip"] = [original with { RequiredSlot = null, SlotExpression = "SLOT_MISSING" }],
            }));
        Assert.Throws<InvalidDataException>(() => AnimationScrCodec.PatchEvents(
            sections,
            new Dictionary<string, ImmutableArray<AnimationEvent>>
            {
                ["clip"] = [original with { FrameExpression = "FRAME_MISSING" }],
            }));
        Assert.Throws<InvalidDataException>(() => AnimationScrCodec.PatchEvents(
            sections,
            new Dictionary<string, ImmutableArray<AnimationEvent>>
            {
                ["clip"] = [original with { FrameExpression = "" }],
            }));
    }

    [Fact]
    public void MatchingNumericFrameExpressionCompilesAndPreservesImportedRawTicks()
    {
        AnimationSequenceUse sequence = SequenceWithEvent(2.6, 1001, -1) with
        {
            SourceStartFrameExpression = "0",
            SourceEndFrameExpression = "10",
            FpsExpression = "30",
            WeightModeExpression = "1",
            WeightTimeExpression = "0.5",
            Events = [new AnimationEvent { LocalFrame = 2.6, FrameExpression = "2.6", EventId = 1001, IdExpression = "1001", RequiredSlot = -1, SlotExpression = "", RawTicks = 13, RawFlags = 0x80 }],
        };

        AnimationScrSections sections = AnimationScrCodec.BuildWithEvents([sequence]);
        ImmutableArray<AnimationSequenceUse> imported = AnimationScrCodec.ReadSequenceUses(sections, name => name + ".anm2");
        AnimationScrSections rebuilt = AnimationScrCodec.BuildWithEvents(imported);
        Assert.Equal(sections.RecordsAndNames, rebuilt.RecordsAndNames);
        Assert.Equal(sections.IndexAndNames, rebuilt.IndexAndNames);
        ParsedAnimationScr parsed = AnimationScrCodec.Parse(rebuilt);
        AnimationScrEventRow row = Assert.Single(Assert.Single(parsed.Sequences).Events);

        Assert.Equal((ushort)13, row.Ticks);
        Assert.Equal((ushort)0x80, row.RawFlags);
    }

    [Fact]
    public void SourceRegenerationRetainsNumericHeadersAndSymbolicIdSpelling()
    {
        const string source = "SeqTrack(\"clip\", \"clip.anm2\", 108, 125, 30, 1, CROWD_BLEND)\n{\n\tEvent(2, VIS_EVENT_STEP_LEFT, -1)\n}\n";
        AnimationSequenceUse sequence = Assert.Single(AnimationScriptTextCodec.Read(source).Sequences);

        string generated = AnimationSequenceExport.Source([sequence]);
        AnimationScriptTextDocument reopened = AnimationScriptTextCodec.Read(generated);
        AnimationSequenceUse parsed = Assert.Single(reopened.Sequences);

        Assert.Contains("108, 125, 30, 1, CROWD_BLEND", generated);
        Assert.Contains("Event(2, VIS_EVENT_STEP_LEFT, -1)", generated);
        Assert.Equal(sequence.SourceStartFrame, parsed.SourceStartFrame);
        Assert.Equal(sequence.WeightTimeExpression, parsed.WeightTimeExpression);
        Assert.Equal("VIS_EVENT_STEP_LEFT", Assert.Single(parsed.Events).IdExpression);
    }

    [Fact]
    public void SourceRegenerationPreservesResolvedSymbolsAndRoundTripsFractionalValues()
    {
        double sourceEnd = 42.123456789012344;
        double weightTime = 0.12345678901234566;
        AnimationSequenceUse sequence = new()
        {
            Name = "clip",
            Anm2Name = "clip.anm2",
            SourceStartFrame = 12.345678901234567,
            SourceStartFrameExpression = "START_RESOLVED",
            SourceEndFrame = sourceEnd,
            SourceEndFrameExpression = "42.1234567890123440",
            FPS = 29.970000000000002,
            FpsExpression = "FPS_RESOLVED",
            WeightMode = 2,
            WeightModeExpression = "WEIGHT_MODE_RESOLVED",
            WeightTime = weightTime,
            WeightTimeExpression = "0.2",
        };

        string generated = AnimationSequenceExport.Source([sequence]);
        AnimationSequenceUse reopened = Assert.Single(AnimationScriptTextCodec.Read(generated).Sequences);

        Assert.Contains("START_RESOLVED", generated);
        Assert.Contains("42.1234567890123440", generated);
        Assert.Contains("FPS_RESOLVED", generated);
        Assert.Contains("WEIGHT_MODE_RESOLVED", generated);
        Assert.Equal(sourceEnd, reopened.SourceEndFrame);
        Assert.Equal(weightTime.ToString("G17", CultureInfo.InvariantCulture), FormatHeaderNumber(generated, 6));
        Assert.Equal(weightTime, reopened.WeightTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WriterKeepsInsertedActionAtItsRequestedPosition(int insertIndex)
    {
        const string source = "SeqTrack(\"clip\", \"clip.anm2\", 0, 10, 30, 1, 0.5)\n{\n\tEvent(2, 1001, -1)\n\t{\n\t\tFirst(1)\n\t\t// keep this note\n\t\tSecond(2)\n\t\tUnknownCall(3)\n\t}\n}\n// keep trailing note\n";
        AnimationScriptTextDocument document = AnimationScriptTextCodec.Read(source);
        AnimationSequenceUse sequence = Assert.Single(document.Sequences);
        AnimationEvent animationEvent = Assert.Single(sequence.Events);
        AnimationEventAction inserted = Action("Inserted", "4");
        List<AnimationEventAction> desired = animationEvent.Actions.ToList();
        desired.Insert(insertIndex, inserted);

        string written = AnimationScriptTextCodec.Write(document with
        {
            Sequences = [sequence with { Events = [animationEvent with { Actions = desired.ToImmutableArray() }] }],
        });
        ImmutableArray<AnimationEventAction> reopened = Assert.Single(Assert.Single(AnimationScriptTextCodec.Read(written).Sequences).Events).Actions;

        Assert.Equal(desired.Select(action => action.Keyword), reopened.Select(action => action.Keyword));
        Assert.Contains("UnknownCall(3)", written);
        Assert.Contains("// keep this note", written);
        Assert.Contains("// keep trailing note", written);
    }

    [Fact]
    public void WriterHandlesReorderingAndDeletionWithInsertedActions()
    {
        const string source = "SeqTrack(\"clip\", \"clip.anm2\", 0, 10, 30, 1, 0.5)\n{\n\tEvent(2, 1001, -1)\n\t{\n\t\tFirst(1)\n\t\t// preserve note\n\t\tSecond(2)\n\t\tUnknownCall(3)\n\t}\n}\n";
        AnimationScriptTextDocument document = AnimationScriptTextCodec.Read(source);
        AnimationSequenceUse sequence = Assert.Single(document.Sequences);
        AnimationEvent animationEvent = Assert.Single(sequence.Events);
        AnimationEventAction unknown = Assert.Single(animationEvent.Actions, action => action.Keyword == "UnknownCall");
        AnimationEventAction second = Assert.Single(animationEvent.Actions, action => action.Keyword == "Second");
        AnimationEventAction inserted = Action("Inserted", "4");
        AnimationEventAction[] desired = [unknown, second, inserted];

        string written = AnimationScriptTextCodec.Write(document with
        {
            Sequences = [sequence with { Events = [animationEvent with { Actions = [.. desired] }] }],
        });
        ImmutableArray<AnimationEventAction> reopened = Assert.Single(Assert.Single(AnimationScriptTextCodec.Read(written).Sequences).Events).Actions;

        Assert.Equal(desired.Select(action => action.Keyword), reopened.Select(action => action.Keyword));
        Assert.DoesNotContain("First(1)", written);
        Assert.Contains("Second(2)", written);
        Assert.Contains("UnknownCall(3)", written);
        Assert.Contains("// preserve note", written);
    }

    private static AnimationSequenceUse ReadSequence(string eventText) =>
        Assert.Single(AnimationScriptTextCodec.Read(
            $"SeqTrack(\"clip\", \"clip.anm2\", 0, 10, 30, 1, 0.5) {{ {eventText} }}").Sequences);

    private static AnimationSequenceUse SequenceWithEvent(double frame, int eventId, short? slot) => new()
    {
        Name = "clip",
        Anm2Name = "clip.anm2",
        SourceStartFrame = 0,
        SourceEndFrame = 10,
        FPS = 30,
        WeightMode = 1,
        WeightTime = 0.5,
        Events = [new AnimationEvent { LocalFrame = frame, EventId = eventId, IdExpression = eventId.ToString(CultureInfo.InvariantCulture), RequiredSlot = slot, SlotExpression = slot?.ToString(CultureInfo.InvariantCulture) ?? "" }],
    };

    private static AnimationEventAction Action(string keyword, string argument) => new()
    {
        Keyword = keyword,
        Arguments = [new AnimationEventArgument { Text = argument }],
    };

    private static string FormatHeaderNumber(string source, int fieldIndex)
    {
        int open = source.IndexOf('(');
        int close = source.IndexOf(')', open + 1);
        string[] fields = source[(open + 1)..close].Split(',');
        return fields[fieldIndex].Trim();
    }
}
