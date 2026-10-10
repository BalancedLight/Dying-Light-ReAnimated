using ReAnimated.Core.Domain;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class AnimationEventPreviewTests
{
    [Fact]
    public void StartPlaysEventsAtThePlayheadAndCatchupEventsBeforeIt()
    {
        AnimationEvent normalBefore = Event(1, 1, AnimationEventDelivery.Event, slot: 0);
        AnimationEvent catchupBefore = Event(2, 2, AnimationEventDelivery.MustSendEvent, slot: 0);
        AnimationEvent atPlayhead = Event(5, 3, AnimationEventDelivery.Event, slot: 0);
        AnimationEvent secondAtPlayhead = Event(5, 4, AnimationEventDelivery.MustSendEvent, slot: 0);
        AnimationEvent otherSlot = Event(2, 5, AnimationEventDelivery.MustSendEvent, slot: 1);
        AnimationSequenceUse sequence = Sequence(normalBefore, catchupBefore, atPlayhead, secondAtPlayhead, otherSlot);

        AnimationEvent[] started = AnimationEventPlaybackCursor.Start(sequence, frame: 5, slot: 0).ToArray();

        Assert.Equal([catchupBefore.Id, atPlayhead.Id, secondAtPlayhead.Id], started.Select(e => e.Id).ToArray());
        Assert.Empty(AnimationEventPlaybackCursor.Start(sequence, frame: 5, slot: 1).Where(e => e.Id == catchupBefore.Id));
        Assert.Empty(AnimationEventPlaybackCursor.Start(sequence, frame: 5, slot: 0).Where(e => e.Id == normalBefore.Id));
    }

    [Fact]
    public void CrossingASequenceLoopDeliversTailThenHeadInSourceOrder()
    {
        AnimationEvent atStartA = Event(0, 10, AnimationEventDelivery.Event);
        AnimationEvent atStartB = Event(0, 11, AnimationEventDelivery.Event);
        AnimationEvent middle = Event(5, 12, AnimationEventDelivery.Event);
        AnimationEvent atEnd = Event(10, 13, AnimationEventDelivery.Event);
        AnimationSequenceUse sequence = Sequence(atStartA, atStartB, middle, atEnd);

        AnimationEvent[] crossed = AnimationEventPlaybackCursor.Cross(sequence, from: 8, to: 1, loops: 1).ToArray();

        Assert.Equal([atEnd.Id, atStartA.Id, atStartB.Id], crossed.Select(e => e.Id).ToArray());
        Assert.Empty(AnimationEventPlaybackCursor.Cross(sequence, from: 2, to: 4, loops: 0));
    }

    [Fact]
    public void PreviewStateReconstructsSameFrameIkAndElementChangesInSourceOrder()
    {
        AnimationEvent enableIk = new() { LocalFrame = 2, EventId = 1040, IdExpression = "VIS_EVENT_IK_ENABLE" };
        AnimationEvent show = new() { LocalFrame = 3, Actions = [Action("ShowElement", "left_glove")] };
        AnimationEvent hide = new() { LocalFrame = 3, Actions = [Action("HideElement", "left_glove")] };
        AnimationEvent disableIk = new() { LocalFrame = 3, EventId = 1041, IdExpression = "VIS_EVENT_IK_DISABLE" };
        AnimationSequenceUse sequence = Sequence(enableIk, show, hide, disableIk);

        AnimationEventPreviewState before = AnimationEventPreviewState.Reconstruct(sequence, 2.9);
        AnimationEventPreviewState after = AnimationEventPreviewState.Reconstruct(sequence, 3);

        Assert.Equal(true, before.IkEnabled);
        Assert.Equal(false, after.IkEnabled);
        Assert.True(after.ElementVisibility["left_glove"] == false);
        Assert.False(after.AllowsIk("left_hand"));
        Assert.True(new AnimationEventPreviewState(true, after.LimbIk, after.ElementVisibility).AllowsIk("left_hand"));
    }

    private static AnimationSequenceUse Sequence(params AnimationEvent[] events) => new()
    {
        Name = "generic_preview",
        Anm2Name = "generic_preview.anm2",
        SourceStartFrame = 100,
        SourceEndFrame = 110,
        FPS = 30,
        Events = [.. events],
    };

    private static AnimationEvent Event(double frame, int id, AnimationEventDelivery delivery, short slot = -1) => new()
    {
        LocalFrame = frame,
        EventId = id,
        IdExpression = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RequiredSlot = slot,
        SlotExpression = slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Delivery = delivery,
    };

    private static AnimationEventAction Action(string keyword, string argument) => new()
    {
        Keyword = keyword,
        Arguments = [new AnimationEventArgument { Text = $"\"{argument}\"" }],
    };
}
