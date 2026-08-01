using System.Buffers.Binary;
using ReAnimated.Codecs.Anm2;

namespace ReAnimated.Tests;

public sealed class AnimationScrCodecTests
{
    [Fact]
    public void BuildsParsesPatchesAndAppendsNoEventDl1Sequences()
    {
        AnimationScrSections sections = AnimationScrCodec.Build(
        [
            new AnimationScrSequence("Walk_B", "walk_b.anm2", 0, 29, 30),
            new AnimationScrSequence("Walk_A", "walk_a.anm2", 2, 62, 60),
        ]);

        ParsedAnimationScr parsed = AnimationScrCodec.Parse(sections);

        Assert.Equal(2, parsed.DeclaredSequenceCount);
        Assert.Equal(AnimationScrCodec.RecordSize * 2, parsed.NameTableOffset);
        Assert.Equal(["walk_a", "walk_b"], parsed.Sequences.Select(row => row.Name));
        Assert.Equal(60, parsed.Sequences[0].FramesPerSecond);
        Assert.All(parsed.Sequences, row => Assert.Equal(0, row.EventCount));

        AnimationScrSections patched = AnimationScrCodec.PatchRanges(
            sections,
            new Dictionary<string, (float Start, float End, float FramesPerSecond)>
            {
                ["walk_a"] = (0, 90, 30),
            });
        ParsedAnimationScr patchedDocument = AnimationScrCodec.Parse(patched);
        ParsedAnimationScrSequence walkA =
            Assert.Single(patchedDocument.Sequences, row => row.Name == "walk_a");
        Assert.Equal(0, walkA.StartFrame);
        Assert.Equal(90, walkA.EndFrame);
        Assert.Equal(30, walkA.FramesPerSecond);
        Assert.Equal(sections.IndexAndNames, patched.IndexAndNames);

        AnimationScrSections appended = AnimationScrCodec.Append(
            patched,
            [new AnimationScrSequence("Jump", "jump.anm2", 0, 20, 30)]);
        ParsedAnimationScr appendedDocument = AnimationScrCodec.Parse(appended);
        Assert.Equal(3, appendedDocument.DeclaredSequenceCount);
        Assert.Equal(
            ["jump", "walk_a", "walk_b"],
            appendedDocument.Sequences.Select(row => row.Name).Order().ToArray());
    }

    [Fact]
    public void BuilderIsDeterministicAndRejectsInvalidRanges()
    {
        AnimationScrSections left = AnimationScrCodec.Build(
        [
            new AnimationScrSequence("b", "b.anm2", 0, 2, 30),
            new AnimationScrSequence("a", "a.anm2", 0, 2, 30),
        ]);
        AnimationScrSections right = AnimationScrCodec.Build(
        [
            new AnimationScrSequence("a", "a.anm2", 0, 2, 30),
            new AnimationScrSequence("b", "b.anm2", 0, 2, 30),
        ]);

        Assert.Equal(left.RecordsAndNames, right.RecordsAndNames);
        Assert.Equal(left.IndexAndNames, right.IndexAndNames);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AnimationScrCodec.Build(
            [
                new AnimationScrSequence("bad", "bad.anm2", 10, 2, 30),
            ]));
    }

    [Fact]
    public void ParserSkipsInvalidRecordWithoutFailingImmutableResult()
    {
        AnimationScrSections valid = AnimationScrCodec.Build(
        [
            new AnimationScrSequence("alpha", "alpha.anm2", 0, 2, 30),
            new AnimationScrSequence("bravo", "bravo.anm2", 0, 2, 30),
        ]);
        byte[] section0 = valid.RecordsAndNames.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            section0.AsSpan(4),
            0);

        ParsedAnimationScr parsed = AnimationScrCodec.Parse(
            new AnimationScrSections(section0, valid.IndexAndNames));

        Assert.Equal(2, parsed.DeclaredSequenceCount);
        ParsedAnimationScrSequence sequence =
            Assert.Single(parsed.Sequences);
        Assert.Equal("bravo", sequence.Name);
    }

    [Fact(Timeout = 10_000)]
    public async Task NoncanonicalNameFallbackMeasuresLargeRunsOnce()
    {
        await Task.Yield();

        AnimationScrSections valid = AnimationScrCodec.Build(
        [
            new AnimationScrSequence(
                "alpha",
                "alpha.anm2",
                0,
                2,
                30),
        ]);
        const int additionalNameCount = 20_000;
        byte[] names =
            new byte["alpha\0"u8.Length + (additionalNameCount * 2)];
        "alpha\0"u8.CopyTo(names);
        int cursor = "alpha\0"u8.Length;
        for (int index = 0; index < additionalNameCount; index++)
        {
            names[cursor++] = (byte)'x';
            names[cursor++] = 0;
        }

        byte[] section0 =
            new byte[AnimationScrCodec.RecordSize + 2 + names.Length];
        valid.RecordsAndNames
            .AsSpan(0, AnimationScrCodec.RecordSize)
            .CopyTo(section0);
        section0[AnimationScrCodec.RecordSize] = 0xFF;
        section0[AnimationScrCodec.RecordSize + 1] = 0;
        names.CopyTo(
            section0,
            AnimationScrCodec.RecordSize + 2);

        ParsedAnimationScr parsed = AnimationScrCodec.Parse(
            new AnimationScrSections(
                section0,
                valid.IndexAndNames));

        Assert.Equal(
            AnimationScrCodec.RecordSize + 2,
            parsed.NameTableOffset);
        Assert.Equal(2, parsed.OpaquePayloadLength);
        Assert.False(parsed.HasCanonicalEventTableLayout);
        Assert.Equal(
            "alpha",
            Assert.Single(parsed.Sequences).Name);
    }
}
