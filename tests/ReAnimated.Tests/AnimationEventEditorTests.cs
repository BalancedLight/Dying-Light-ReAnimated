using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

public sealed class AnimationEventEditorTests
{
    [Fact]
    public void FreeDragKeepsACompileableFrameExpression()
    {
        var item = new AnimationEvent { LocalFrame = 2, EventId = 1001, IdExpression = "VIS_EVENT_STEP_LEFT", RequiredSlot = -1, SlotExpression = "-1" };
        var timeline = new TimelineViewModel();
        AnimationEventEditorViewModel editor = timeline.Events;
        editor.CanEdit = true;
        editor.Load([new AnimationSequenceUse { Name = "walk", Anm2Name = "walk.anm2", FPS = 30, SourceEndFrame = 20, Events = [item] }]);
        editor.Select(item.Id, false);
        editor.Snap = "Free";

        editor.BeginDrag(2);
        editor.DragTo(2.123456789123);
        editor.EndDrag();

        AnimationSequenceUse edited = Assert.Single(editor.Snapshot());
        AnimationEvent moved = Assert.Single(edited.Events);
        Assert.Equal(moved.LocalFrame, double.Parse(moved.FrameExpression!, System.Globalization.CultureInfo.InvariantCulture));
        AnimationScrSections compiled = AnimationScrCodec.BuildWithEvents([edited]);
        Assert.NotEmpty(compiled.RecordsAndNames);
    }

    [Fact]
    public void EventsRemainBrowsableWithoutAMarkerSelectionAndTypeReflectsTheRow()
    {
        var item = new AnimationEvent { LocalFrame = 4, EventId = 502, IdExpression = "502", RequiredSlot = -1, SlotExpression = "-1" };
        var editor = new AnimationEventEditorViewModel(new TimelineViewModel());
        editor.Load([new AnimationSequenceUse { Name = "generic_motion", Anm2Name = "generic_motion.anm2", FPS = 30, SourceEndFrame = 20, Events = [item] }]);

        Assert.Null(editor.SelectedEvent);
        Assert.True(editor.HasEvents);
        editor.Select(item.Id, false);
        Assert.Equal(502, editor.SelectedDefinition?.Id);
        Assert.Equal("502", editor.IdText);
        editor.SelectedEvent = null;
        Assert.True(editor.HasEvents);
        Assert.Single(editor.Items);
        Assert.Equal("IK & Contacts", AnimationEventEditorViewModel.Lane(new AnimationEvent { EventId = 1011, IdExpression = "1011" }));
    }

    [Fact]
    public void ApplyingAnEditRetainsOtherIdenticalSameTimeEvents()
    {
        AnimationEvent first = new() { LocalFrame = 4, EventId = 1012, IdExpression = "VIS_EVENT_LEFT_FOOT_LAND", RequiredSlot = -1, SlotExpression = "-1" };
        AnimationEvent second = first with { Id = Guid.NewGuid() };
        var sequence = new AnimationSequenceUse
        {
            Name = "generic_walk",
            Anm2Name = "generic_walk.anm2",
            FPS = 30,
            SourceEndFrame = 20,
            Events = [first, second],
        };
        var timeline = new TimelineViewModel();
        AnimationEventEditorViewModel editor = timeline.Events;
        editor.CanEdit = true;
        editor.Load([sequence]);

        editor.Select(second.Id, additive: false);
        editor.FrameText = "4.2";
        editor.IdText = "1013";
        editor.SlotText = "2";
        editor.ApplyCommand.Execute(null);

        AnimationEvent[] events = Assert.Single(editor.Snapshot()).Events.ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(first, events[0]);
        Assert.Equal(second.Id, events[1].Id);
        Assert.Equal(4.2, events[1].LocalFrame);
        Assert.Equal(1013, events[1].EventId);
        Assert.Equal((short)2, events[1].RequiredSlot);
        Assert.Equal(2, editor.Items.Count);
    }

    [Fact]
    public void ActionEditorUsesCatalogParameterNamesAndTypes()
    {
        AnimationSequenceUse sequence = new()
        {
            Name = "generic_action",
            Anm2Name = "generic_action.anm2",
            FPS = 30,
            SourceEndFrame = 20,
            Events = [new AnimationEvent { LocalFrame = 2, EventId = 0, IdExpression = "0", RequiredSlot = -1, SlotExpression = "-1" }],
        };
        var timeline = new TimelineViewModel();
        AnimationEventEditorViewModel editor = timeline.Events;
        editor.CanEdit = true;
        editor.Load([sequence]);
        editor.SelectedActionDefinition = Dl1AnimationEventCatalog.FindAction("playsound23d");

        editor.AddActionCommand.Execute(null);

        AnimationActionEditorViewModel action = Assert.Single(editor.Actions);
        Assert.Equal("playsound23d", action.Keyword, ignoreCase: true);
        Assert.Equal(
            ["Volume Source", "Sound Name", "Volume", "Min Range", "Element Name", "Attach", "Local Position", "Stop On Anim End"],
            action.Arguments.Select(argument => argument.Name).ToArray());
        Assert.Equal(["i", "s", "f", "f", "s", "i", "v3", "i"], action.Arguments.Select(argument => argument.Type).ToArray());
        Assert.Equal("Left Foot Land", Dl1AnimationEventCatalog.FindEvent("VIS_EVENT_LEFT_FOOT_LAND")!.DisplayName);
    }

    [Fact]
    public void ReparseKeepsDistinctIdsForIdenticalSameTimeSourceRows()
    {
        const string source = "SeqTrack(\"generic_walk\", \"generic_walk.anm2\", 0, 20, 30, 1, 0.5)\n{\n    Event(4, 1012, -1)\n    Event(4, 1012, -1)\n}\n";
        AnimationSequenceUse previous = Assert.Single(AnimationScriptTextCodec.Read(source).Sequences);
        AnimationSequenceUse parsed = Assert.Single(AnimationScriptTextCodec.Read(source).Sequences);

        AnimationSequenceUse reconciled = Assert.Single(MainWindowViewModel.ReconcileEventIdentities([parsed], [previous]));

        Assert.Equal(previous.Events.Select(e => e.Id).ToArray(), reconciled.Events.Select(e => e.Id).ToArray());
        Assert.Equal(2, reconciled.Events.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void ReparseKeepsDistinctIdsForRepeatedSequenceDeclarations()
    {
        const string source = "SeqTrack(\"generic_pose\", \"generic_pose.anm2\", 0, 20, 30, 1, 0.5)\nSeqTrack(\"generic_pose\", \"generic_pose.anm2\", 0, 20, 30, 1, 0.5)\n";
        AnimationSequenceUse[] previous = AnimationScriptTextCodec.Read(source).Sequences.ToArray();
        AnimationSequenceUse[] parsed = AnimationScriptTextCodec.Read(source).Sequences.ToArray();

        AnimationSequenceUse[] reconciled = MainWindowViewModel.ReconcileEventIdentities([.. parsed], [.. previous]).ToArray();

        Assert.Equal(previous.Select(s => s.Id).ToArray(), reconciled.Select(s => s.Id).ToArray());
        Assert.Equal(2, reconciled.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void PreserveSecondsCannotApplyWhenImportedFrameRateIsUnresolved()
    {
        AnimationSequenceUse sequence = new()
        {
            Name = "generic_unresolved_rate",
            Anm2Name = "generic_unresolved_rate.anm2",
            FPS = 0,
            FpsExpression = "FPS_FROM_INCLUDE",
            SourceEndFrame = 20,
            Events = [new AnimationEvent { LocalFrame = 4, EventId = 1012, IdExpression = "VIS_EVENT_LEFT_FOOT_LAND" }],
        };
        AnimationEventEditorViewModel editor = new(new TimelineViewModel());
        editor.CanEdit = true;
        editor.Load([sequence]);
        editor.Retime = "Preserve seconds";
        editor.FPS = "30";

        editor.ReviewRangeCommand.Execute(null);

        Assert.False(editor.ApplyRangeCommand.CanExecute(null));
        Assert.Contains("FPS", editor.Status, StringComparison.OrdinalIgnoreCase);
    }
}
