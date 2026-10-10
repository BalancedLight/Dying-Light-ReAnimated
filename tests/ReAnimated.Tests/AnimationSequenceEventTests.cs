using System.Collections.Immutable;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

public sealed class AnimationSequenceEventTests
{
    [Fact]
    public void TextReadWritePreservesUnchangedSourceExactly()
    {
        const string source = "// bank header\r\n!include(\"events.def\")\r\nSeqTrack(\"walk\", \"walk.anm2\", 108, 125, 30, 1, 0.0)\r\n{\r\n    Event(2.5, EVENT_THROWABLE_THROW, -1) // keep this comment\r\n    {\r\n        PlaySound23D(Source, \"step, left.wav\", [0, 0, (1 + 2)], 0)\r\n    }\r\n}\r\n// tail\r\n";

        AnimationScriptTextDocument document = AnimationScriptTextCodec.Read(source);

        Assert.Equal(source, AnimationScriptTextCodec.Write(document));
        AnimationSequenceUse sequence = Assert.Single(document.Sequences);
        Assert.Equal(1, sequence.WeightMode);
        Assert.Equal(0, sequence.WeightTime);
        AnimationEvent animationEvent = Assert.Single(sequence.Events);
        Assert.Equal(2.5, animationEvent.LocalFrame);
        Assert.Equal("EVENT_THROWABLE_THROW", animationEvent.IdExpression);
        Assert.Equal(501, animationEvent.EventId);
        AnimationEventAction action = Assert.Single(animationEvent.Actions);
        Assert.Equal("PlaySound23D", action.Keyword);
        Assert.Equal(4, action.Arguments.Length);
        Assert.Equal("[0, 0, (1 + 2)]", action.Arguments[2].Text);
    }

    [Fact]
    public void EditedEventPatchesItsFieldsAndKeepsOtherSource()
    {
        const string source = "!include(\"events.def\")\nSeqTrack(\"walk\", \"walk.anm2\", 0, 10, 30, 1, 0.5)\n{\n\tEvent(2.5, EVENT_STEP, -1) // event note\n\tEvent(4, 9, 0)\n}\n// tail retained\n";
        AnimationScriptTextDocument document = AnimationScriptTextCodec.Read(source);
        AnimationSequenceUse sequence = Assert.Single(document.Sequences);
        AnimationEvent original = sequence.Events[0];
        AnimationSequenceUse edited = sequence with
        {
            Events = sequence.Events.SetItem(0, original with { LocalFrame = 3.2, EventId = 1012, RequiredSlot = 1 }),
        };

        string written = AnimationScriptTextCodec.Write(document with { Sequences = [edited] });

        Assert.Contains("!include(\"events.def\")", written);
        Assert.Contains("Event(3.2, 1012, 1) // event note", written);
        Assert.Contains("Event(4, 9, 0)", written);
        Assert.Contains("// tail retained", written);
        Assert.Equal(2, Assert.Single(AnimationScriptTextCodec.Read(written).Sequences).Events.Length);
    }

    [Fact]
    public void EventAndActionCollectionsCanBeAddedAndRemovedWithoutDroppingUnrelatedComments()
    {
        const string source = "SeqTrack(\"idle\", \"idle.anm2\", 0, 10, 30, 1, 0.2)\n{\n\t// keep block note\n\tEvent(1, 10, -1)\n\tEvent(2, 11, -1)\n}\n";
        AnimationScriptTextDocument document = AnimationScriptTextCodec.Read(source);
        AnimationSequenceUse sequence = Assert.Single(document.Sequences);
        AnimationSequenceUse edited = sequence with
        {
            Events = [
                sequence.Events[0] with { Actions = [new AnimationEventAction { Keyword = "PlaySound", Arguments = [new AnimationEventArgument { Text = "\"step.wav\"" }] }] },
                new AnimationEvent { LocalFrame = 3, EventId = 12, IdExpression = "12", RequiredSlot = -1, SlotExpression = "-1" },
            ],
        };

        string written = AnimationScriptTextCodec.Write(document with { Sequences = [edited] });

        Assert.Contains("// keep block note", written);
        Assert.Contains("Event(1, 10, -1)", written);
        Assert.True(written.Contains("PlaySound(\"step.wav\")", StringComparison.Ordinal), written);
        Assert.Contains("Event(3, 12, -1)", written);
        Assert.DoesNotContain("Event(2, 11, -1)", written);
        Assert.Equal(2, Assert.Single(AnimationScriptTextCodec.Read(written).Sequences).Events.Length);
    }

    [Fact]
    public void EventSourceExpressionsAndWeightNamesRemainAvailableThroughLegacyParser()
    {
        const string source = "SeqTrack(\"bump\", \"bump.anm2\", 0, 44, 30, 1, CROWD_BLEND)\n";
        AnimationScriptSeqTrack legacy = Assert.Single(AnimationScriptSourceParser.ParseSeqTracks(source));
        Assert.Equal(legacy.Enabled, legacy.WeightMode);
        Assert.Equal(legacy.Blend, legacy.WeightTime);
        AnimationSequenceUse sequence = Assert.Single(AnimationScriptSourceParser.ParseEventDocument(source).Sequences);
        Assert.Equal("CROWD_BLEND", sequence.WeightTimeExpression);
    }

    [Fact]
    public void TickEncodingUsesFloat32MultiplicationAndTruncatesWithinUshortRange()
    {
        Assert.Equal((ushort)12, AnimationEventTiming.ToTicks(2.5));
        Assert.Equal((ushort)65535, AnimationEventTiming.ToTicks(13107));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationEventTiming.ToTicks(13107.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationEventTiming.ToTicks(double.NaN));

        AnimationEvent original = new() { LocalFrame = 1.25, RawTicks = 6 };
        Assert.Equal((ushort)6, AnimationEventTiming.ToTicks(original with { }, original));
        Assert.Equal((ushort)10, AnimationEventTiming.ToTicks(original with { LocalFrame = 2 }, original));
    }

    [Fact]
    public void RetimePreservesSecondsOrSourceRangeWithoutDeletingEvents()
    {
        AnimationSequenceUse sequence = new()
        {
            Name = "run",
            Anm2Name = "run.anm2",
            SourceStartFrame = 20,
            SourceEndFrame = 40,
            FPS = 20,
            Events = [new AnimationEvent { LocalFrame = 10 }, new AnimationEvent { LocalFrame = 18 }],
        };

        AnimationSequenceUse seconds = AnimationSequenceRetimer.Retime(sequence, 30, AnimationSequenceRetimeMode.PreserveSeconds);
        Assert.Equal(15, seconds.Events[0].LocalFrame);
        Assert.Equal(27, seconds.Events[1].LocalFrame);
        Assert.Equal(2, seconds.Events.Length);

        AnimationSequenceUse stretched = AnimationSequenceRetimer.Retime(sequence, 20, AnimationSequenceRetimeMode.ScaleToSourceRange, newSourceEndFrame: 50);
        Assert.Equal(50, stretched.SourceEndFrame);
        Assert.Equal(15, stretched.Events[0].LocalFrame);
        Assert.Equal(27, stretched.Events[1].LocalFrame);
        Assert.Equal(2, stretched.Events.Length);

        AnimationSequenceUse sourcePositions = AnimationSequenceRetimer.Retime(sequence, 20, AnimationSequenceRetimeMode.KeepSourcePositions, newSourceStartFrame: 22, newSourceEndFrame: 42);
        Assert.Equal(22, sourcePositions.SourceStartFrame);
        Assert.Equal(8, sourcePositions.Events[0].LocalFrame);
        Assert.Equal(16, sourcePositions.Events[1].LocalFrame);

        Assert.Contains(AnimationSequenceUseValidation.Validate(sequence with { Events = [sequence.Events[0] with { LocalFrame = 13108 }] }), issue => issue.Contains("16-bit", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogIncludesEventDefinitionsActionsAndEditorMappings()
    {
        Assert.Equal(488, Dl1AnimationEventCatalog.Events.Length);
        Assert.Equal(83, Dl1AnimationEventCatalog.SoundMappings.Length);
        Assert.Contains(Dl1AnimationEventCatalog.Events, e => e.Symbol == "EVENT_OPENDOOR" && e.Legacy);
        Assert.Equal(2, Dl1AnimationEventCatalog.FindEvents(502).Length);
        Assert.Contains(Dl1AnimationEventCatalog.Actions, a => a.Keyword == "playsound23d" && a.ParameterNames.Length > 0);
        Assert.Contains(Dl1AnimationEventCatalog.Groups, g => g.Name == "Final Pos");
        Dl1AnimationEventDefinition observed = Assert.IsType<Dl1AnimationEventDefinition>(Dl1AnimationEventCatalog.FindEvent("VIS_EVENT_LEFT_FOOT_LAND"));
        Assert.True(observed.ObservedCount > 0);
        Assert.Contains("DL Base Game Paks.zip", observed.ObservedIn);
        Assert.Contains("EVENT_END_SPIN_ATTACK_DAMAGE", Dl1AnimationEventCatalog.Unresolved.Select(item => item.Expression));
    }
}
