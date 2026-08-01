using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class Dl1MimicGenerationTests
{
    [Fact]
    public void BuilderUsesTxScalarAndConsolidatesSourcesDeterministically()
    {
        Dl1MimicProfile profile = CreateSyntheticProfile();
        RigDefinition rig = CreateRig(profile);
        var scan = new Dl1MimicSourceScan(
            "synthetic.fbx",
            "Take 001",
            new FrameRate(30, 1),
            4,
            [
                new Dl1MimicSourceCurve(
                    "jawOpen",
                    [0, 0.25, 0.5, 1]),
                new Dl1MimicSourceCurve(
                    "mouthOpen",
                    [0, 0.10, 0.20, 0.30]),
                new Dl1MimicSourceCurve(
                    "smileLeft",
                    [0, 0.20, -0.10, 0.40]),
            ]);
        ImmutableArray<Dl1MimicMappingRow> mapping =
        [
            new("jawOpen", 0x11111111, 1),
            new("mouthOpen", 0x11111111, 0.5),
            new("smileLeft", 0x22222222, 1),
        ];
        var request = new Dl1MimicBuildRequest
        {
            Source = scan,
            Profile = profile,
            ExactTargetRig = rig,
            Mapping = mapping,
        };
        var builder = new Dl1MimicBuilder();

        Dl1MimicBuildResult build = builder.Build(request);
        Dl1MimicBuildResult repeated = builder.Build(request);

        Assert.Equal(build.Payload, repeated.Payload);
        Anm2Clip payload = Anm2Reader.Read(
            build.Payload,
            "generated-mimic.anm2");
        Assert.Equal(
            new uint[]
            {
                0x11111111,
                0x22222222,
            },
            payload.TrackDescriptors);
        ImmutableArray<Anm2Frame> frames =
            Anm2SemanticDecoder.DecodeAllFrames(payload);
        double[] expectedJaw = [0, 0.30, 0.60, 1.15];
        double[] expectedSmile = [0, 0.20, -0.10, 0.40];
        for (int frameIndex = 0;
             frameIndex < frames.Length;
             frameIndex++)
        {
            Assert.InRange(
                Math.Abs(
                    frames[frameIndex].Tracks[0].TranslationX -
                    expectedJaw[frameIndex]),
                0,
                2e-3);
            Assert.InRange(
                Math.Abs(
                    frames[frameIndex].Tracks[1].TranslationX -
                    expectedSmile[frameIndex]),
                0,
                2e-3);
            foreach (Anm2TrackFrame track in frames[frameIndex].Tracks)
            {
                Assert.Equal(0, track.RotationX, 6);
                Assert.Equal(0, track.RotationY, 6);
                Assert.Equal(0, track.RotationZ, 6);
                Assert.Equal(0, track.TranslationY, 6);
                Assert.Equal(0, track.TranslationZ, 6);
                Assert.Equal(1, track.ScaleX, 6);
                Assert.Equal(1, track.ScaleY, 6);
                Assert.Equal(1, track.ScaleZ, 6);
            }
        }

        Assert.Equal("tx", build.Report.WeightComponent);
        Assert.Equal(3, build.Report.MappedSourceShapeCount);
        Assert.Empty(build.Report.UnmappedAnimatedShapes);
        Assert.Collection(
            build.Report.ConsolidatedTargets[0x11111111],
            source => Assert.Equal("jawOpen", source),
            source => Assert.Equal("mouthOpen", source));
        Assert.InRange(
            build.Report.DecodedMaximumComponentError,
            0,
            2e-3);
    }

    [Fact]
    public void BuilderReportsUnmappedActivityInsteadOfGuessing()
    {
        Dl1MimicProfile profile = CreateSyntheticProfile();
        var scan = new Dl1MimicSourceScan(
            "synthetic.fbx",
            "Take 001",
            new FrameRate(30, 1),
            4,
            [
                new Dl1MimicSourceCurve(
                    "jawOpen",
                    [0, 0.25, 0.5, 1]),
                new Dl1MimicSourceCurve(
                    "mouthOpen",
                    [0, 0.10, 0.20, 0.30]),
                new Dl1MimicSourceCurve(
                    "smileLeft",
                    [0, 0.20, -0.10, 0.40]),
            ]);

        Dl1MimicBuildResult build = new Dl1MimicBuilder().Build(
            new Dl1MimicBuildRequest
            {
                Source = scan,
                Profile = profile,
                ExactTargetRig = CreateRig(profile),
                Mapping =
                [
                    new Dl1MimicMappingRow(
                        "jawOpen",
                        0x11111111),
                ],
            });

        Assert.Collection(
            build.Report.UnmappedAnimatedShapes,
            source => Assert.Equal("mouthOpen", source),
            source => Assert.Equal("smileLeft", source));
        Assert.InRange(
            build.Report.CapturedSourceActivityRatio,
            double.Epsilon,
            1 - double.Epsilon);
    }

    [Fact]
    public void PercentScaleHandlesPercentAndNormalizedExports()
    {
        Assert.Equal(
            0.01,
            Dl1MimicBuilder.DetectPercentScale(
                0,
                [0, 50, 100]));
        Assert.Equal(
            1,
            Dl1MimicBuilder.DetectPercentScale(
                0,
                [0, 0.5, 1]));
    }

    [Fact]
    public void Common46ProfileIsEmbeddedDeclarativeAndUnique()
    {
        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();

        Assert.Equal(
            Dl1MimicProfile.BuiltInCommon46Id,
            profile.ProfileId);
        Assert.Equal(46, profile.Targets.Length);
        Assert.Equal(
            46,
            profile.Descriptors.Distinct().Count());
        Assert.All(
            profile.Targets,
            target =>
            {
                Assert.Equal(
                    "morph_scalar_tx",
                    target.Semantic);
                Assert.Equal("tx", target.Component);
            });
        HashSet<string> names = profile.Targets
            .Select(static target => target.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string name in new[]
                 {
                     "morph_jaw_open",
                     "open",
                     "wide",
                     "w",
                     "fv",
                     "pbm",
                     "morph_nose",
                 })
        {
            Assert.Contains(name, names);
        }
    }

    [Fact]
    public void AutoMapSupportsConsolidationAndBlinkCompanion()
    {
        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();

        ImmutableArray<Dl1MimicMappingRow> rows =
            Dl1MimicAutoMapper.AutoMap(
                [
                    "jawOpen",
                    "mouthOpen",
                    "eyeBlinkLeft",
                    "mouthSmileRight",
                ],
                profile);
        Dl1MimicMappingRow[] enabled = rows
            .Where(static row => row.Enabled)
            .ToArray();

        Assert.Equal(
            "morph_jaw_open",
            profile.FindTarget(
                enabled.Single(row =>
                    row.Source == "jawOpen").TargetDescriptor)!.Name);
        Assert.Equal(
            "morph_jaw_open",
            profile.FindTarget(
                enabled.Single(row =>
                    row.Source == "mouthOpen").TargetDescriptor)!.Name);
        Assert.Collection(
            enabled
                .Where(static row =>
                    row.Source == "eyeBlinkLeft")
                .Select(row =>
                    profile.FindTarget(row.TargetDescriptor)!.Name)
                .Order(StringComparer.Ordinal),
            target => Assert.Equal("morph_l_b_lid", target),
            target => Assert.Equal("morph_l_u_lid", target));
        Assert.Contains(
            enabled.Where(static row =>
                row.Source == "mouthSmileRight"),
            row =>
                profile.FindTarget(row.TargetDescriptor)!.Name ==
                "morph_lips_R_smile");
    }

    [Fact]
    public void AutoMapUsesUnambiguousShapeAliasAndKeepsCanonicalSource()
    {
        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();
        var source = new Dl1MimicSourceCurve(
            "Channel_17",
            [0, 1],
            ["Channel_17", "jawOpen"]);

        Dl1MimicMappingRow row = Assert.Single(
            Dl1MimicAutoMapper.AutoMap([source], profile));

        Assert.Equal("Channel_17", row.Source);
        Assert.Equal(
            "morph_jaw_open",
            profile.FindTarget(row.TargetDescriptor)!.Name);
        Assert.StartsWith(
            "shape_alias:",
            row.Method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AutoMapLeavesConflictingShapeAliasesForManualReview()
    {
        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();
        var source = new Dl1MimicSourceCurve(
            "Channel_18",
            [0, 1],
            ["Channel_18", "jawOpen", "mouthSmileRight"]);

        Assert.Empty(
            Dl1MimicAutoMapper.AutoMap([source], profile));
    }

    [Fact]
    public void ProfileCanonicalRoundTripPreservesUnresolvedSemantics()
    {
        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();

        byte[] canonical =
            Dl1MimicProfileCodec.WriteCanonical(profile);
        byte[] withByteOrderMark =
        [
            0xEF,
            0xBB,
            0xBF,
            .. canonical,
        ];
        Dl1MimicProfile reloaded =
            Dl1MimicProfileCodec.Read(withByteOrderMark);

        Assert.Equal(
            canonical,
            Dl1MimicProfileCodec.WriteCanonical(reloaded));
        Dl1MimicTarget[] unresolved = reloaded.Targets
            .Where(static target =>
                target.NameStatus != "resolved")
            .ToArray();
        Assert.NotEmpty(unresolved);
        Assert.All(
            unresolved,
            static target =>
                Assert.NotEqual(0u, target.Descriptor));
    }

    [Fact]
    public void BuilderRequiresEveryProfileDescriptorOnExactTargetRig()
    {
        Dl1MimicProfile profile = CreateSyntheticProfile();
        RigDefinition incompleteRig = CreateRig(
            new Dl1MimicProfile(
                "test:incomplete",
                "Incomplete",
                [profile.Targets[0]]));
        var request = new Dl1MimicBuildRequest
        {
            Source = new Dl1MimicSourceScan(
                "face.fbx",
                "Take",
                new FrameRate(30, 1),
                1,
                [
                    new Dl1MimicSourceCurve(
                        "jawOpen",
                        [0]),
                ]),
            Profile = profile,
            ExactTargetRig = incompleteRig,
            Mapping = [],
        };

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => new Dl1MimicBuilder().Build(request));

        Assert.Contains(
            "0x22222222",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderHonorsHardAndSoftProfileClampWithoutChangingSource()
    {
        var target = new Dl1MimicTarget(
            0,
            0x11111111,
            "jaw",
            "Jaw",
            recommendedMinimum: -0.5,
            recommendedMaximum: 0.5);
        var profile = new Dl1MimicProfile(
            "test:clamp",
            "Clamp",
            [target]);
        var scan = new Dl1MimicSourceScan(
            "face.fbx",
            "Take",
            new FrameRate(30, 1),
            2,
            [
                new Dl1MimicSourceCurve(
                    "jaw",
                    [0, 2]),
            ]);
        var builder = new Dl1MimicBuilder();
        Dl1MimicBuildResult hard = builder.Build(
            new Dl1MimicBuildRequest
            {
                Source = scan,
                Profile = profile,
                ExactTargetRig = CreateRig(profile),
                Mapping =
                [
                    new Dl1MimicMappingRow(
                        "jaw",
                        target.Descriptor),
                ],
                ClampMode = Dl1MimicClampMode.Hard,
            });
        Dl1MimicBuildResult soft = builder.Build(
            new Dl1MimicBuildRequest
            {
                Source = scan,
                Profile = profile,
                ExactTargetRig = CreateRig(profile),
                Mapping =
                [
                    new Dl1MimicMappingRow(
                        "jaw",
                        target.Descriptor),
                ],
                ClampMode = Dl1MimicClampMode.Soft,
            });

        Assert.Equal(
            0.5,
            hard.Clip.SampleScalars(
                scan.FrameRate.SecondsForFrame(1))["jaw"],
            6);
        double softValue = soft.Clip.SampleScalars(
            scan.FrameRate.SecondsForFrame(1))["jaw"];
        Assert.InRange(softValue, 0, 0.5);
        Assert.Equal(2, scan.Curves[0].Values[1]);
    }

    [Fact]
    public void ProfileCodecRejectsFutureSchemaAndOversizedInput()
    {
        const string future =
            """
            {
              "format": "dl-reanimated-mimic-profile",
              "schema_version": 2,
              "tracks": []
            }
            """;
        InvalidDataException futureException = Assert.Throws<
            InvalidDataException>(
            () => Dl1MimicProfileCodec.Read(
                Encoding.UTF8.GetBytes(future)));
        Assert.Contains(
            "maximum supported schema",
            futureException.Message,
            StringComparison.Ordinal);

        byte[] oversized = new byte[
            Dl1MimicProfileCodec.MaximumProfileBytes + 1];
        Assert.Throws<InvalidDataException>(
            () => Dl1MimicProfileCodec.Read(oversized));
    }

    [Fact]
    public void CanceledBuildStopsBeforeAllocatingTargetCurves()
    {
        Dl1MimicProfile profile = CreateSyntheticProfile();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new Dl1MimicBuilder().Build(
                new Dl1MimicBuildRequest
                {
                    Source = new Dl1MimicSourceScan(
                        "face.fbx",
                        "Take",
                        new FrameRate(30, 1),
                        1,
                        [
                            new Dl1MimicSourceCurve(
                                "jawOpen",
                                [0]),
                        ]),
                    Profile = profile,
                    ExactTargetRig = CreateRig(profile),
                },
                cancellation.Token));
    }

    [Fact]
    public void BuilderRejectsOversizedMappingBeforeContributionSampling()
    {
        Dl1MimicProfile profile = CreateSyntheticProfile();
        ImmutableArray<Dl1MimicMappingRow> mapping = Enumerable
            .Range(0, Dl1MimicBuilder.MaximumMappingRowCount + 1)
            .Select(_ => new Dl1MimicMappingRow(
                "jawOpen",
                0x11111111))
            .ToImmutableArray();

        InvalidDataException exception = Assert.Throws<
            InvalidDataException>(
            () => new Dl1MimicBuilder().Build(
                new Dl1MimicBuildRequest
                {
                    Source = new Dl1MimicSourceScan(
                        "face.fbx",
                        "Take",
                        new FrameRate(30, 1),
                        1,
                        [
                            new Dl1MimicSourceCurve(
                                "jawOpen",
                                [0]),
                        ]),
                    Profile = profile,
                    ExactTargetRig = CreateRig(profile),
                    Mapping = mapping,
                }));

        Assert.Contains(
            Dl1MimicBuilder.MaximumMappingRowCount.ToString(
                CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    private static Dl1MimicProfile CreateSyntheticProfile() =>
        new(
            "test:face",
            "Synthetic face",
            [
                new Dl1MimicTarget(
                    0,
                    0x11111111,
                    "jaw",
                    "Jaw",
                    aliases: ["jawOpen"]),
                new Dl1MimicTarget(
                    1,
                    0x22222222,
                    "smile",
                    "Smile",
                    aliases: ["smileLeft"]),
            ]);

    private static RigDefinition CreateRig(
        Dl1MimicProfile profile) =>
        new(
            "mimic-rig",
            "Mimic rig",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0xABCDEF01),
            ],
            profile.Targets.Select(target =>
                new MorphChannelDefinition(
                    target.Index,
                    target.Name,
                    target.Descriptor,
                    "mimic." + target.Name,
                    target.RecommendedMinimum,
                    target.RecommendedMaximum)));
}
