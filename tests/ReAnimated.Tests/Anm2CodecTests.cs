using System.Buffers.Binary;
using System.Collections.Immutable;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class Anm2CodecTests
{
    [Theory]
    [InlineData(
        "infected_turn_90r.template.anm2",
        13_152,
        "E19323CEF0BA995B96FF07362423261E82A0FD46F36AE541E43AC48B197B8F92")]
    [InlineData(
        "stock_writer_control.anm2",
        14_976,
        "3C0AF8AA3F0800CB7C85B794F8A809FED1861E115274B3723316A18E4594E05D")]
    public void ReaderPreservesStockDl1Controls(
        string fileName,
        int expectedLength,
        string expectedSha256)
    {
        var repository = FindRepositoryRoot();
        var bytes = File.ReadAllBytes(
            Path.Combine(
                repository,
                "tests",
                "ReAnimated.Tests",
                "Fixtures",
                "Anm2",
                fileName));

        var clip = Anm2Reader.Read(bytes, fileName);

        Assert.Equal(58, clip.Header.FrameCount);
        Assert.Equal(70, clip.Header.TrackCount);
        Assert.Equal(320, clip.Header.PageOffset);
        Assert.Equal(checked((uint)expectedLength), clip.Header.DeclaredLength);
        Assert.Equal(expectedSha256, clip.Sha256);
        Assert.Equal(bytes, clip.EncodePreservingBody().ToArray());
    }

    [Fact]
    public void PackedGroupRoundTripsSecondOrderValues()
    {
        var frames = Enumerable.Range(0, Anm2PackedGroupCodec.FrameCount)
            .Select(frame => (IReadOnlyList<short>)Enumerable.Range(0, Anm2PackedGroupCodec.LaneCount)
                .Select(lane => checked((short)((frame * frame * (lane + 1)) - (70 * lane))))
                .ToArray())
            .ToArray();

        var encoded = Anm2PackedGroupCodec.Encode(frames);
        var decoded = Anm2PackedGroupCodec.Decode(encoded);

        Assert.Equal(0, encoded.Length % 16);
        Assert.Equal(encoded.Length, Anm2PackedGroupCodec.GetEncodedLength(encoded));
        for (var frame = 0; frame < frames.Length; frame++)
        {
            Assert.Equal(frames[frame], decoded[frame]);
        }
    }

    [Fact]
    public void PackedGroupSupportsInt16Extremes()
    {
        var values = Enumerable.Range(0, 16)
            .Select(frame => (IReadOnlyList<short>)
            [
                frame % 2 == 0 ? short.MinValue : short.MaxValue,
                -1,
                0,
                1,
                -16384,
                16383,
                checked((short)(frame * 10)),
                checked((short)(-frame * 10)),
            ])
            .ToArray();

        var decoded = Anm2PackedGroupCodec.Decode(Anm2PackedGroupCodec.Encode(values));

        for (var frame = 0; frame < values.Length; frame++)
        {
            Assert.Equal(values[frame], decoded[frame]);
        }
    }

    [Fact]
    public void PackedGroupDecodesOnlyRequestedPartialFrameRange()
    {
        var values = Enumerable.Range(0, Anm2PackedGroupCodec.FrameCount)
            .Select(frame => (IReadOnlyList<short>)
            [
                checked((short)(frame * frame)),
                checked((short)-frame),
                checked((short)frame),
                0,
                0,
                1,
                -1,
                2,
            ])
            .ToArray();

        short[][] decoded = Anm2PackedGroupCodec.Decode(
            Anm2PackedGroupCodec.Encode(values),
            maximumFrame: 4);

        Assert.Equal(5, decoded.Length);
        for (var frame = 0; frame < decoded.Length; frame++)
        {
            Assert.Equal(values[frame], decoded[frame]);
        }
    }

    [Fact]
    public void WriterCreatesStrictReadableMultipagePayload()
    {
        const int frameCount = 2210;
        var descriptors = Enumerable.Range(0, 5)
            .Select(index => 0x11111111u + checked((uint)index))
            .ToImmutableArray();
        var frames = ImmutableArray.CreateBuilder<Anm2Frame>(frameCount);
        for (var frame = 0; frame < frameCount; frame++)
        {
            frames.Add(new Anm2Frame(
                Enumerable.Range(0, descriptors.Length)
                    .Select(track =>
                    {
                        var rotationBits =
                            ((frame * 1_103_515_245L) + (track * 12_345L)) & 0xFFFF;
                        var translationBits =
                            ((frame * 214_013L) + (track * 2_531_011L)) & 0xFFFF;
                        return new Anm2TrackFrame(
                            ((rotationBits / 65_535f) - 0.5f) * 2,
                            0,
                            0,
                            ((translationBits / 65_535f) - 0.5f) * 20,
                            0,
                            0,
                            1,
                            1,
                            1);
                    })
                    .ToImmutableArray()));
        }

        var bytes = Anm2PayloadWriter.Build(
            new Anm2Header(
                Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion,
                frameCount,
                checked((ushort)descriptors.Length),
                0,
                0,
                0,
                1,
                0,
                0),
            descriptors,
            frames.MoveToImmutable(),
            Enumerable.Repeat(
                    Anm2PackedComponents.RotationX |
                    Anm2PackedComponents.TranslationX,
                    descriptors.Length)
                .ToImmutableArray());

        var clip = Anm2Reader.Read(bytes, "long_control");

        Assert.Equal(frameCount, clip.Header.FrameCount);
        Assert.True(descriptors.AsSpan().SequenceEqual(clip.TrackDescriptors.AsSpan()));
        Assert.True(clip.Header.PageCount > 1);
        Assert.Equal(frameCount - 1, clip.PageFrameSpans.Sum(value => value));
        Assert.Equal(checked((uint)bytes.Length), clip.Header.DeclaredLength);
        Assert.Equal(Convert.ToHexString(bytes), Convert.ToHexString(clip.EncodePreservingBody().Span));

        Anm2BulkDecodeResult decoded =
            Anm2SemanticDecoder.DecodeFrames(clip);
        int firstPageSpan = clip.PageFrameSpans[0];
        int[] controlFrames =
        [
            0,
            firstPageSpan,
            Math.Min(
                firstPageSpan + 1,
                frameCount - 1),
            frameCount - 1,
        ];
        foreach (int frameIndex in controlFrames.Distinct())
        {
            AssertFrameEqual(
                Anm2SemanticDecoder.Sample(
                    clip,
                    frameIndex).Frame,
                decoded.Frames[frameIndex]);
        }
    }

    [Fact]
    public void WriterKeepsSmallPackedClipOnOneStrictPage()
    {
        const int frameCount = 61;
        var bytes = BuildSmallPackedPayload(frameCount);

        Anm2Clip clip = Anm2GeneratedPayloadValidator.Validate(
            bytes,
            "small_control");

        Assert.Equal(1, clip.Header.PageCount);
        Assert.Equal(
            checked((ushort)(frameCount - 1)),
            Assert.Single(clip.PageFrameSpans));
        Assert.Equal(checked((uint)bytes.Length), clip.Header.DeclaredLength);
    }

    [Fact]
    public void GeneratedPayloadValidatorRejectsOversizedDeclaredOnePageClip()
    {
        var bytes = BuildSmallPackedPayload(61);
        Array.Resize(ref bytes, checked(bytes.Length + Anm2Header.PageSize));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(16),
            checked((uint)bytes.Length));

        Anm2Clip permissiveStockRead = Anm2Reader.Read(
            bytes,
            "bad_long_clip");
        var error = Assert.Throws<InvalidDataException>(
            () => Anm2GeneratedPayloadValidator.Validate(
                bytes,
                "bad_long_clip"));

        Assert.Equal(1, permissiveStockRead.Header.PageCount);
        Assert.Contains(
            "invalid ANM2 page layout",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "page_count 1",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticDecoderRoundTripsDirectPackedAndScaleComponents()
    {
        const int frameCount = 47;
        var descriptors = ImmutableArray.Create(0xAABBCCDDu, 0x10203040u);
        var expected = ImmutableArray.CreateBuilder<Anm2Frame>(frameCount);
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var time = frameIndex / 12f;
            expected.Add(new Anm2Frame(
            [
                new Anm2TrackFrame(
                    MathF.Sin(time),
                    MathF.Cos(time * 0.5f),
                    time * 0.05f,
                    frameIndex * 0.01f,
                    frameIndex * -0.02f,
                    MathF.Sin(time * 0.2f) * 0.4f,
                    1,
                    1,
                    1),
                new Anm2TrackFrame(
                    0,
                    0,
                    0,
                    2,
                    -3,
                    4,
                    1 + (frameIndex * 0.001f),
                    1 - (frameIndex * 0.002f),
                    0.5f + (MathF.Sin(time) * 0.01f)),
            ]));
        }

        var expectedFrames = expected.MoveToImmutable();
        var bytes = Anm2PayloadWriter.Build(
            new Anm2Header(
                Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion,
                frameCount,
                checked((ushort)descriptors.Length),
                0,
                0,
                0,
                1,
                0,
                0),
            descriptors,
            expectedFrames,
            ImmutableArray.Create(
                Anm2PackedComponents.RotationX |
                Anm2PackedComponents.RotationY |
                Anm2PackedComponents.RotationZ |
                Anm2PackedComponents.TranslationX |
                Anm2PackedComponents.TranslationY |
                Anm2PackedComponents.TranslationZ,
                Anm2PackedComponents.Scale));
        var clip = Anm2Reader.Read(bytes);

        var actual = Anm2SemanticDecoder.DecodeAllFrames(clip);

        Assert.Equal(frameCount, actual.Length);
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            for (var trackIndex = 0; trackIndex < descriptors.Length; trackIndex++)
            {
                for (var componentIndex = 0; componentIndex < 9; componentIndex++)
                {
                    Assert.InRange(
                        MathF.Abs(
                            expectedFrames[frameIndex].Tracks[trackIndex][componentIndex] -
                            actual[frameIndex].Tracks[trackIndex][componentIndex]),
                        0,
                        0.001f);
                }
            }
        }

        var fractional = Anm2SemanticDecoder.Sample(clip, 12.5);
        Assert.Equal(12.5, fractional.EvaluatedFrame, 6);
        Assert.Equal(0.5f, fractional.Fraction, 6);
    }

    [Fact]
    public void BulkDecoderCachesPackedSlotsAndSelectsDescriptors()
    {
        const int frameCount = 47;
        ImmutableArray<uint> descriptors =
        [
            0x10101010,
            0x20202020,
            0x30303030,
        ];
        ImmutableArray<Anm2Frame> frames = Enumerable
            .Range(0, frameCount)
            .Select(frameIndex =>
                new Anm2Frame(
                    Enumerable
                        .Range(0, descriptors.Length)
                        .Select(trackIndex =>
                            new Anm2TrackFrame(
                                MathF.Sin(
                                    (frameIndex +
                                     trackIndex) *
                                    0.07f),
                                0,
                                0,
                                (frameIndex * 0.125f) +
                                trackIndex,
                                trackIndex * -0.25f,
                                0,
                                1,
                                1,
                                1))
                        .ToImmutableArray()))
            .ToImmutableArray();
        byte[] bytes = Anm2PayloadWriter.Build(
            new Anm2Header(
                Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion,
                frameCount,
                checked((ushort)descriptors.Length),
                0,
                0,
                0,
                1,
                0,
                0),
            descriptors,
            frames,
            Enumerable.Repeat(
                    Anm2PackedComponents.RotationX |
                    Anm2PackedComponents.TranslationX,
                    descriptors.Length)
                .ToImmutableArray());
        Anm2Clip clip = Anm2Reader.Read(
            bytes,
            "bulk-cache-control");
        ImmutableArray<uint> selected =
        [
            descriptors[2],
            descriptors[0],
        ];

        Anm2BulkDecodeResult decoded =
            Anm2SemanticDecoder.DecodeFrames(
                clip,
                selected);

        Assert.Equal(selected, decoded.TrackDescriptors);
        Assert.Equal(frameCount, decoded.Frames.Length);
        int expectedUniquePackedSlots = Enumerable
            .Range(0, frameCount)
            .Select(frameIndex =>
            {
                Anm2DecodedSample sample =
                    Anm2SemanticDecoder.Sample(
                        clip,
                        frameIndex);
                return (
                    sample.PageIndex,
                    sample.TableIndex
                );
            })
            .Distinct()
            .Count();
        Assert.Equal(
            expectedUniquePackedSlots,
            decoded.UniquePackedSlotsDecoded);
        int[] sourceTrackIndices = [2, 0];
        for (var frameIndex = 0;
             frameIndex < frameCount;
             frameIndex++)
        {
            Anm2Frame sampled =
                Anm2SemanticDecoder.Sample(
                    clip,
                    frameIndex).Frame;
            for (var outputTrackIndex = 0;
                 outputTrackIndex <
                 sourceTrackIndices.Length;
                 outputTrackIndex++)
            {
                Anm2TrackFrame expected =
                    sampled.Tracks[
                        sourceTrackIndices[
                            outputTrackIndex]];
                Anm2TrackFrame actual =
                    decoded.Frames[frameIndex]
                        .Tracks[outputTrackIndex];
                for (var componentIndex = 0;
                     componentIndex < 9;
                     componentIndex++)
                {
                    Assert.Equal(
                        expected[componentIndex],
                        actual[componentIndex]);
                }
            }
        }

        Assert.Throws<ArgumentException>(() =>
            Anm2SemanticDecoder.DecodeFrames(
                clip,
                [descriptors[0], descriptors[0]]));
        Assert.Throws<InvalidDataException>(() =>
            Anm2SemanticDecoder.DecodeFrames(
                clip,
                [0xDEADBEEF]));
        Assert.Throws<InvalidDataException>(() =>
            Anm2SemanticDecoder.DecodeFrames(
                clip,
                selected,
                maximumDecodedComponentCount:
                    (frameCount *
                     selected.Length *
                    9) -
                    1));
    }

    [Fact]
    public void BulkDecoderMatchesRandomAccessForStockDl1Clip()
    {
        string repository = FindRepositoryRoot();
        byte[] bytes = File.ReadAllBytes(
            Path.Combine(
                repository,
                "tests",
                "ReAnimated.Tests",
                "Fixtures",
                "Anm2",
                "infected_turn_90r.template.anm2"));
        Anm2Clip clip = Anm2Reader.Read(
            bytes,
            "infected_turn_90r.template.anm2");

        Anm2BulkDecodeResult decoded =
            Anm2SemanticDecoder.DecodeFrames(clip);
        int[] controlFrames =
        [
            0,
            1,
            10,
            clip.Header.FrameCount / 2,
            clip.Header.FrameCount - 1,
        ];

        Assert.Equal(
            clip.TrackDescriptors,
            decoded.TrackDescriptors);
        Assert.Equal(
            clip.Header.FrameCount,
            decoded.Frames.Length);
        foreach (int frameIndex in controlFrames.Distinct())
        {
            Anm2Frame expected =
                Anm2SemanticDecoder.Sample(
                    clip,
                    frameIndex).Frame;
            Anm2Frame actual = decoded.Frames[frameIndex];
            Assert.Equal(
                expected.Tracks.Length,
                actual.Tracks.Length);
            for (var trackIndex = 0;
                 trackIndex < expected.Tracks.Length;
                 trackIndex++)
            {
                for (var componentIndex = 0;
                     componentIndex < 9;
                     componentIndex++)
                {
                    Assert.Equal(
                        expected.Tracks[trackIndex][componentIndex],
                        actual.Tracks[trackIndex][componentIndex]);
                }
            }
        }
    }

    [Fact]
    public async Task BulkDecoderStopsWhenCanceledDuringWork()
    {
        const int frameCount = 61;
        byte[] bytes = BuildSmallPackedPayload(
            frameCount);
        Anm2Clip clip = Anm2Reader.Read(
            bytes,
            "bulk-cancellation-control");
        using var cancellation =
            new CancellationTokenSource();
        using var decodeReached =
            new ManualResetEventSlim();
        using var releaseDecode =
            new ManualResetEventSlim();
        var checkpoints = 0;
        Task<Anm2BulkDecodeResult> decode =
            Task.Factory.StartNew(
                () =>
                    Anm2SemanticDecoder.DecodeFrames(
                        clip,
                        selectedTrackDescriptors: null,
                        Anm2SemanticDecoder
                            .DefaultMaximumDecodedComponentCount,
                        () =>
                        {
                            if (Interlocked.Increment(
                                    ref checkpoints) ==
                                8)
                            {
                                decodeReached.Set();
                                releaseDecode.Wait();
                            }
                        },
                        cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

        try
        {
            Assert.True(
                decodeReached.Wait(
                    TimeSpan.FromSeconds(30)));
            Assert.False(decode.IsCompleted);
            cancellation.Cancel();
        }
        finally
        {
            releaseDecode.Set();
        }

        await Assert.ThrowsAsync<
            OperationCanceledException>(
            async () => await decode);
    }

    [Fact]
    public void BodyImportAlignsCayleyRotationsAcrossTwoTurns()
    {
        const int frameCount = 257;
        const uint descriptor = 0x61626364;
        var rig = new RigDefinition(
            "cayley-continuity",
            "Cayley continuity",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: descriptor,
                    semanticRole: "root.skeletal"),
            ]);
        var sourceTrack = new TransformTrack(
            0,
            Enumerable
                .Range(0, frameCount)
                .Select(frameIndex =>
                    new TransformKeyframe(
                        frameIndex,
                        new TransformTRS(
                            Vector3D.Zero,
                            QuaternionD.FromAxisAngle(
                                Vector3D.UnitY,
                                (4.0 *
                                 Math.PI *
                                 frameIndex) /
                                (frameCount - 1)),
                            Vector3D.One))));
        var source = new AnimationClip(
            "two-turn-cayley",
            new FrameRate(30, 1),
            frameCount,
            [sourceTrack]);
        byte[] bytes = Anm2DomainAdapter.ExportBody(
            source,
            rig,
            [descriptor]);
        Anm2Clip encoded = Anm2Reader.Read(
            bytes,
            "two-turn-cayley.anm2");

        AnimationClip imported =
            Anm2DomainAdapter.ImportBody(
                encoded,
                rig,
                source.FrameRate).Clip;
        TransformTrack actual =
            Assert.Single(imported.TransformTracks);
        ImmutableArray<Anm2Frame> decoded =
            Anm2SemanticDecoder.DecodeAllFrames(encoded);

        Assert.Equal(frameCount, actual.Keyframes.Length);
        for (var frameIndex = 0;
             frameIndex < frameCount;
             frameIndex++)
        {
            QuaternionD scalar =
                Anm2DomainAdapter.QuaternionFromCayley(
                    decoded[frameIndex]
                        .Tracks[0].RotationX,
                    decoded[frameIndex]
                        .Tracks[0].RotationY,
                    decoded[frameIndex]
                        .Tracks[0].RotationZ);
            QuaternionD aligned =
                actual.Keyframes[frameIndex]
                    .Value.Rotation;
            Assert.InRange(
                Math.Abs(
                    QuaternionD.Dot(
                        scalar,
                        aligned)),
                1.0 - 1e-12,
                1.0 + 1e-12);
            if (frameIndex > 0)
            {
                Assert.True(
                    QuaternionD.Dot(
                        actual.Keyframes[
                            frameIndex -
                            1].Value.Rotation,
                        aligned) >= 0.0);
            }
        }
    }

    [Fact]
    public void DomainAdapterRoundTripsBodyAndMimicWithoutFormatConventionsLeaking()
    {
        var rig = new RigDefinition(
            "retail:test",
            "Retail Test",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x11112222),
                new BoneDefinition(
                    1,
                    "hand",
                    0,
                    new TransformTRS(
                        Vector3D.UnitY,
                        QuaternionD.Identity,
                        Vector3D.One),
                    descriptorHash: 0x33334444),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    "jaw_open",
                    descriptorHash: 0x55556666),
            ]);
        var body = new AnimationClip(
            "body",
            new FrameRate(30, 1),
            17,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(
                            16,
                            new TransformTRS(
                                new Vector3D(1.25, -2.5, 0.75),
                                QuaternionD.FromAxisAngle(Vector3D.UnitY, 0.65),
                                Vector3D.One)),
                    ]),
            ]);
        var mimic = new AnimationClip(
            "mimic",
            new FrameRate(30, 1),
            17,
            scalarTracks:
            [
                new ScalarTrack(
                    "jaw_open",
                    [new ScalarKeyframe(0, 0), new ScalarKeyframe(16, 0.8)]),
            ]);

        var bodyBytes = Anm2DomainAdapter.ExportBody(
            body,
            rig,
            [0x11112222, 0x33334444]);
        Anm2DomainImportResult importedBody = Anm2DomainAdapter.ImportBody(
            Anm2Reader.Read(bodyBytes, "body"),
            rig,
            new FrameRate(30, 1));
        var mimicBytes = Anm2DomainAdapter.ExportMimic(
            mimic,
            rig,
            [0x55556666]);
        AnimationClip importedMimic = Anm2DomainAdapter.ImportMimic(
            Anm2Reader.Read(mimicBytes, "mimic"),
            rig,
            new FrameRate(30, 1));

        SkeletonPose finalPose = importedBody.Clip.SamplePose(rig, 16d / 30d);
        Assert.Empty(importedBody.UnmappedDescriptors);
        Assert.Equal(1.25, finalPose.LocalTransforms[0].Translation.X, 3);
        Assert.Equal(-2.5, finalPose.LocalTransforms[0].Translation.Y, 3);
        Assert.True(
            Math.Abs(
                QuaternionD.Dot(
                    QuaternionD.FromAxisAngle(Vector3D.UnitY, 0.65),
                    finalPose.LocalTransforms[0].Rotation)) > 0.9999);
        Assert.Equal(
            0.8,
            importedMimic.SampleScalars(16d / 30d)["jaw_open"],
            3);
    }

    [Fact]
    public void MimicExportHonorsCancellationBeforeSampling()
    {
        var rig = new RigDefinition(
            "mimic-cancellation",
            "Mimic cancellation",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x11112222),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    "jaw_open",
                    descriptorHash: 0x55556666),
            ]);
        var clip = new AnimationClip(
            "mimic",
            new FrameRate(30, 1),
            2,
            scalarTracks:
            [
                new ScalarTrack(
                    "jaw_open",
                    [
                        new ScalarKeyframe(0, 0),
                        new ScalarKeyframe(1, 1),
                    ]),
            ]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Anm2DomainAdapter.ExportMimic(
                clip,
                rig,
                [0x55556666],
                1e-7,
                cancellation.Token));
    }

    [Fact]
    public async Task WriterStopsWhenCanceledDuringLargePackedBuild()
    {
        const int frameCount = 8_192;
        const int trackCount = 16;
        ImmutableArray<uint> descriptors = Enumerable
            .Range(0, trackCount)
            .Select(index => 0x70000000u + checked((uint)index))
            .ToImmutableArray();
        var frames = ImmutableArray.CreateBuilder<Anm2Frame>(
            frameCount);
        for (var frameIndex = 0;
             frameIndex < frameCount;
             frameIndex++)
        {
            var tracks = ImmutableArray.CreateBuilder<Anm2TrackFrame>(
                trackCount);
            for (var trackIndex = 0;
                 trackIndex < trackCount;
                 trackIndex++)
            {
                tracks.Add(
                    new Anm2TrackFrame(
                        0,
                        0,
                        0,
                        (frameIndex * 0.001f) +
                        (trackIndex * 0.01f),
                        0,
                        0,
                        1,
                        1,
                        1));
            }

            frames.Add(new Anm2Frame(tracks.MoveToImmutable()));
        }

        using var cancellation = new CancellationTokenSource();
        using var packedBuildReached = new ManualResetEventSlim();
        using var releasePackedBuild = new ManualResetEventSlim();
        Task<byte[]> build = Task.Factory.StartNew(
            () =>
            {
                return Anm2PayloadWriter.Build(
                    new Anm2Header(
                        Anm2Header.Dl1FormatVersion,
                        Anm2Header.Dl1SamplerVersion,
                        frameCount,
                        trackCount,
                        0,
                        0,
                        0,
                        1,
                        0,
                        0),
                    descriptors,
                    frames.MoveToImmutable(),
                    Enumerable.Repeat(
                            Anm2PackedComponents.TranslationX,
                            trackCount)
                        .ToImmutableArray(),
                    () =>
                    {
                        packedBuildReached.Set();
                        releasePackedBuild.Wait();
                    },
                    cancellation.Token);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.True(
                packedBuildReached.Wait(TimeSpan.FromSeconds(30)));
            Assert.False(build.IsCompleted);
            cancellation.Cancel();
        }
        finally
        {
            releasePackedBuild.Set();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await build);
    }

    [Fact]
    public void ReaderRejectsLegacyOrDl2Header()
    {
        var bytes = new byte[Anm2Header.Size];
        "ANM2"u8.CopyTo(bytes);
        bytes[4] = 43;

        var error = Assert.Throws<InvalidDataException>(() => Anm2Reader.Read(bytes));

        Assert.Contains("supported DL1 format", error.Message, StringComparison.Ordinal);
    }

    private static byte[] BuildSmallPackedPayload(int frameCount)
    {
        var descriptor = ImmutableArray.Create(0x12345678u);
        var frames = Enumerable.Range(0, frameCount)
            .Select(frame => new Anm2Frame(
            [
                new Anm2TrackFrame(
                    frame * 0.01f,
                    0,
                    0,
                    0,
                    0,
                    0,
                    1,
                    1,
                    1),
            ]))
            .ToImmutableArray();
        return Anm2PayloadWriter.Build(
            new Anm2Header(
                Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion,
                checked((ushort)frameCount),
                1,
                0,
                0,
                0,
                1,
                0,
                0),
            descriptor,
            frames,
            [Anm2PackedComponents.RotationX]);
    }

    private static void AssertFrameEqual(
        Anm2Frame expected,
        Anm2Frame actual)
    {
        Assert.Equal(
            expected.Tracks.Length,
            actual.Tracks.Length);
        for (var trackIndex = 0;
             trackIndex < expected.Tracks.Length;
             trackIndex++)
        {
            for (var componentIndex = 0;
                 componentIndex < 9;
                 componentIndex++)
            {
                Assert.Equal(
                    expected.Tracks[trackIndex][componentIndex],
                    actual.Tracks[trackIndex][componentIndex]);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DLReAnimated.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DL ReAnimated repository root.");
    }
}
