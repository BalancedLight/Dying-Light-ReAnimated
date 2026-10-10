using System.Buffers.Binary;
using System.Collections.Immutable;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

public sealed class AnimationScrCompiledEventTests
{
    [Fact]
    public void BuildsAndReadsNotificationRowsWithSequenceAllocation()
    {
        AnimationScrSections sections = AnimationScrCodec.BuildWithEvents(
        [
            new AnimationSequenceUse
            {
                Name = "walk",
                Anm2Name = "walk.anm2",
                SourceStartFrame = 0,
                SourceEndFrame = 12,
                FPS = 30,
                WeightMode = 1,
                WeightTime = 0.5,
                Events =
                [
                    new AnimationEvent
                    {
                        LocalFrame = 2.5,
                        EventId = 12,
                        RequiredSlot = -1,
                        Delivery = AnimationEventDelivery.Event,
                    },
                    new AnimationEvent
                    {
                        LocalFrame = 4,
                        EventId = 44,
                        RequiredSlot = 2,
                        Delivery = AnimationEventDelivery.MustSendEvent,
                        RawFlags = 0x80,
                    },
                ],
            },
            new AnimationSequenceUse
            {
                Name = "idle",
                Anm2Name = "idle.anm2",
                SourceStartFrame = 0,
                SourceEndFrame = 3,
                FPS = 30,
                WeightMode = 0,
                WeightTime = 0,
                Events = [],
            },
        ]);

        ParsedAnimationScr parsed = AnimationScrCodec.Parse(sections);
        Assert.True(parsed.HasCanonicalEventTableLayout);
        Assert.Equal(2UL, parsed.TotalDeclaredEventCount);
        ParsedAnimationScrSequence walk = Assert.Single(
            parsed.Sequences,
            static sequence => sequence.Name == "walk");
        Assert.Equal(1, walk.WeightMode);
        Assert.Equal(0.5f, walk.WeightTime);
        Assert.Collection(
            walk.Events,
            first =>
            {
                Assert.Equal((ushort)12, first.Ticks);
                Assert.Equal((ushort)12, first.EventId);
                Assert.Equal(uint.MaxValue, first.ActionReference);
                Assert.Equal((short)-1, first.RequiredSlot);
                Assert.False(first.MustSend);
            },
            second =>
            {
                Assert.Equal((ushort)20, second.Ticks);
                Assert.Equal((ushort)44, second.EventId);
                Assert.Equal((short)2, second.RequiredSlot);
                Assert.True(second.MustSend);
                Assert.Equal((ushort)0x81, second.RawFlags);
            });
        Assert.Empty(Assert.Single(parsed.Sequences, static sequence => sequence.Name == "idle").Events);

        ImmutableArray<AnimationSequenceUse> uses = AnimationScrCodec.ReadSequenceUses(
            sections,
            new Dictionary<string, string>
            {
                ["walk"] = "walk.anm2",
                ["idle"] = "idle.anm2",
            });
        AnimationScrSections rebuilt = AnimationScrCodec.BuildWithEvents(uses);
        Assert.Equal(sections.RecordsAndNames, rebuilt.RecordsAndNames);
        Assert.Equal(sections.IndexAndNames, rebuilt.IndexAndNames);

        int walkIndex = Array.FindIndex(uses.ToArray(), static use => use.Name == "walk");
        AnimationSequenceUse walkUse = uses[walkIndex];
        AnimationSequenceUse retimedWalk = walkUse with
        {
            Events = walkUse.Events.SetItem(
                0,
                walkUse.Events[0] with
                {
                    LocalFrame = walkUse.Events[0].LocalFrame + 0.2,
                }),
        };
        ParsedAnimationScrSequence retimed = Assert.Single(
            AnimationScrCodec.Parse(AnimationScrCodec.BuildWithEvents(
                uses.SetItem(walkIndex, retimedWalk))).Sequences,
            static sequence => sequence.Name == "walk");
        Assert.Equal((ushort)13, retimed.Events[0].Ticks);
    }

    [Fact]
    public void PatchRetimesRowsAndPreservesActionReferencesAndUnknownFlags()
    {
        AnimationScrSections original = CreateEventBearingResource();
        ParsedAnimationScrSequence sequence = Assert.Single(
            AnimationScrCodec.Parse(original).Sequences);
        AnimationScrSections unchanged = AnimationScrCodec.PatchEvents(
            original,
            new Dictionary<string, ImmutableArray<AnimationEvent>>());
        Assert.Equal(original.RecordsAndNames, unchanged.RecordsAndNames);

        AnimationEvent[] edits = sequence.Events
            .Select(static row => new AnimationEvent
            {
                LocalFrame = row.LocalFrame,
                EventId = row.EventId,
                RequiredSlot = row.RequiredSlot,
                Delivery = row.MustSend
                    ? AnimationEventDelivery.MustSendEvent
                    : AnimationEventDelivery.Event,
                RawTicks = row.Ticks,
                RawFlags = row.RawFlags,
                RawActionReference = row.ActionReference,
            })
            .ToArray();
        edits[0] = edits[0] with { LocalFrame = 7.5 };
        edits[1] = edits[1] with { EventId = 321, Delivery = AnimationEventDelivery.MustSendEvent };

        AnimationScrSections patched = AnimationScrCodec.PatchEvents(
            original,
            new Dictionary<string, ImmutableArray<AnimationEvent>>
            {
                ["walk"] = edits.ToImmutableArray(),
            });
        ParsedAnimationScrSequence result = Assert.Single(
            AnimationScrCodec.Parse(patched).Sequences);

        Assert.Equal((ushort)37, result.Events[0].Ticks);
        Assert.Equal(0x12345678u, result.Events[0].ActionReference);
        Assert.Equal((ushort)0x80, result.Events[0].RawFlags);
        Assert.Equal((ushort)321, result.Events[1].EventId);
        Assert.Equal((ushort)0x89, result.Events[1].RawFlags);
        Assert.Equal(original.IndexAndNames, patched.IndexAndNames);

        AnimationSequenceUse use = Assert.Single(
            AnimationScrCodec.ReadSequenceUses(
                patched,
                new Dictionary<string, string> { ["walk"] = "walk.anm2" }));
        Assert.Equal(0x12345678u, use.Events[0].RawActionReference);
        Assert.Equal((ushort)37, use.Events[0].RawTicks);
        Assert.Equal((short)1, use.Events[0].RequiredSlot);
    }

    [Fact]
    public void CompiledBuilderRejectsActionsAndPatchSupportsEventCountChanges()
    {
        AnimationSequenceUse sequence = new()
        {
            Name = "walk",
            Anm2Name = "walk.anm2",
            SourceStartFrame = 0,
            SourceEndFrame = 10,
            FPS = 30,
            Events =
            [
                new AnimationEvent
                {
                    LocalFrame = 1,
                    EventId = 2,
                    RequiredSlot = -1,
                    Delivery = AnimationEventDelivery.Event,
                    Actions =
                    [
                        new AnimationEventAction { Keyword = "PlaySound" },
                    ],
                },
            ],
        };

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => AnimationScrCodec.BuildWithEvents([sequence]));
        Assert.Contains("action-bank", error.Message, StringComparison.OrdinalIgnoreCase);

        AnimationScrSections resource = CreateEventBearingResource();
        AnimationScrSections deleted = AnimationScrCodec.PatchEvents(
            resource,
            new Dictionary<string, ImmutableArray<AnimationEvent>>
            {
                ["walk"] = [],
            });
        Assert.Empty(Assert.Single(AnimationScrCodec.Parse(deleted).Sequences).Events);

        AnimationScrSections inserted = AnimationScrCodec.PatchEvents(
            deleted,
            new Dictionary<string, ImmutableArray<AnimationEvent>>
            {
                ["walk"] =
                [
                    new AnimationEvent
                    {
                        LocalFrame = 3.25,
                        EventId = 74,
                        RequiredSlot = -1,
                        Delivery = AnimationEventDelivery.MustSendEvent,
                    },
                ],
            });
        AnimationScrEventRow insertedRow = Assert.Single(
            Assert.Single(AnimationScrCodec.Parse(inserted).Sequences).Events);
        Assert.Equal((ushort)16, insertedRow.Ticks);
        Assert.Equal((ushort)74, insertedRow.EventId);
        Assert.Equal(uint.MaxValue, insertedRow.ActionReference);
    }

    [Fact]
    public void RpackBuilderRejectsFiniteActionReferencesWithoutFixupBacking()
    {
        AnimationScrSections sections = AnimationScrCodec.BuildWithEvents(
        [
            new AnimationSequenceUse
            {
                Name = "walk",
                Anm2Name = "walk.anm2",
                SourceStartFrame = 0,
                SourceEndFrame = 10,
                FPS = 30,
                Events =
                [
                    new AnimationEvent
                    {
                        LocalFrame = 1,
                        EventId = 0,
                        RequiredSlot = 0,
                    },
                ],
            },
        ]);
        byte[] section0 = sections.RecordsAndNames.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            section0.AsSpan(AnimationScrCodec.RecordSize + 4),
            4);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => Rp6lAnimationLibraryCodec.Build(
                new Dictionary<string, byte[]>(),
                new Dictionary<string, Rp6lAnimationScript>
                {
                    ["walk"] = new(section0, sections.IndexAndNames),
                }));
        Assert.Contains("separate action fixups", exception.Message, StringComparison.Ordinal);
    }

    private static AnimationScrSections CreateEventBearingResource()
    {
        AnimationScrSections built = AnimationScrCodec.BuildWithEvents(
        [
            new AnimationSequenceUse
            {
                Name = "walk",
                Anm2Name = "walk.anm2",
                SourceStartFrame = 0,
                SourceEndFrame = 20,
                FPS = 30,
                Events =
                [
                    new AnimationEvent { LocalFrame = 1, EventId = 5, RequiredSlot = 1 },
                    new AnimationEvent { LocalFrame = 2, EventId = 8, RequiredSlot = 0 },
                ],
            },
        ]);
        byte[] section0 = built.RecordsAndNames.ToArray();
        int rowsOffset = AnimationScrCodec.RecordSize;
        BinaryPrimitives.WriteUInt32LittleEndian(
            section0.AsSpan(rowsOffset + 4),
            0x12345678);
        BinaryPrimitives.WriteUInt16LittleEndian(
            section0.AsSpan(rowsOffset + 10),
            0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(
            section0.AsSpan(rowsOffset + AnimationScrCodec.EventRecordSize + 4),
            0x23456789);
        BinaryPrimitives.WriteUInt16LittleEndian(
            section0.AsSpan(rowsOffset + AnimationScrCodec.EventRecordSize + 10),
            0x88);
        return new AnimationScrSections(section0, built.IndexAndNames);
    }
}
