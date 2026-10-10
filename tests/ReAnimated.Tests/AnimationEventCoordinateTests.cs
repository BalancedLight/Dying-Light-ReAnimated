using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

public sealed class AnimationEventCoordinateTests
{
    [Fact]
    public void SequenceRulerAndSourceAuthoringUseDifferentCoordinates()
    {
        var timeline = new TimelineViewModel();
        timeline.Events.Load([new AnimationSequenceUse { Name = "segment", Anm2Name = "clip.anm2", SourceStartFrame = 108, SourceEndFrame = 125, FPS = 30 }]);
        timeline.PositionFrame = 2.5;
        Assert.Equal(2, timeline.CurrentFrame);
        Assert.Equal(110, timeline.SourceFrame);
        var track = new TimelineTrackViewModel("root", "Position");
        track.Keyframes.Add(new TimelineKeyframeViewModel("root", 110, 0, 0));
        timeline.ReplaceTracks([track]);
        Assert.Equal(2 * timeline.PixelsPerFrame, Assert.Single(timeline.VisibleKeyframes).PixelX, 8);
    }

    [Fact]
    public void SourceEditsRetainExplicitSymbolAndAllowMissingSlotRepair()
    {
        var document = AnimationScriptTextCodec.Read("SeqTrack(\"clip\",\"clip.anm2\",0,10,30,1,0){ Event(2,0) }");
        var sequence = document.Sequences[0];
        var edited = sequence.Events[0] with { EventId = 1011, IdExpression = "VIS_EVENT_LEFT_FOOT_LAND", RequiredSlot = -1, SlotExpression = "-1" };
        string text = AnimationScriptTextCodec.Write(document with { Sequences = [sequence with { Events = [edited] }] });
        Assert.Contains("VIS_EVENT_LEFT_FOOT_LAND", text, StringComparison.Ordinal);
        var reopened = Assert.Single(Assert.Single(AnimationScriptTextCodec.Read(text).Sequences).Events);
        Assert.Equal((short)-1, reopened.RequiredSlot);
        Assert.Equal(1011, reopened.EventId);
    }

    [Fact]
    public void DeletingAnEarlierCompiledEventPreservesImportedTicks()
    {
        var sections = AnimationScrCodec.BuildWithEvents([new AnimationSequenceUse { Name = "clip", Anm2Name = "clip.anm2", FPS = 30, SourceEndFrame = 10,
            Events = [new AnimationEvent { LocalFrame = 1, EventId = 1, RequiredSlot = -1 }, new AnimationEvent { LocalFrame = 2.6, RawTicks = 13, EventId = 2, RequiredSlot = -1 }] }]);
        var imported = AnimationScrCodec.ReadSequenceUses(sections, name => name + ".anm2");
        var patched = AnimationScrCodec.PatchEvents(sections, new Dictionary<string, ImmutableArray<AnimationEvent>> { ["clip"] = [imported[0].Events[1]] });
        Assert.Equal((ushort)13, Assert.Single(Assert.Single(AnimationScrCodec.Parse(patched).Sequences).Events).Ticks);
    }
}
