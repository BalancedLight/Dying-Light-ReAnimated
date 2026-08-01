using System.Collections.Immutable;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class FbxFacialAnimationAdapterTests
{
    private static readonly long FrameTick =
        FbxBinaryDocument.TicksPerSecond / 30;

    [Fact]
    public void ImportsSelectedDeformPercentCurveWithRawProvenanceAndShapeAlias()
    {
        FbxBinaryDocument document = Document(
            [
                Channel(10, "jawOpen", 25.0),
                Shape(11, "Jaw_Alias"),
                Stack(40, "Face", 0, FrameTick * 2),
                Layer(100, "FaceLayer"),
                CurveNode(20),
                Curve(
                    30,
                    [0, FrameTick, FrameTick * 2],
                    [0.0, 50.0, 100.0]),
            ],
            [
                Connection("OO", 11, 10),
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 10, "DeformPercent"),
                Connection("OP", 30, 20, "d|DeformPercent"),
            ],
            GlobalSettings(Property70("TimeMode", 6)));

        FbxFacialAnimationImportResult result =
            FbxFacialAnimationAdapter.Import(
                document,
                PercentOptions());

        Assert.Equal("Face", result.AnimationStack.Name);
        Assert.Equal(new FrameRate(30, 1), result.DeclaredTimebase.FrameRate);
        Assert.True(
            ImmutableArray.Create(0L, FrameTick, FrameTick * 2)
                .AsSpan()
                .SequenceEqual(result.SampleTicks.AsSpan()));
        FbxFacialChannel channel = Assert.Single(result.Channels);
        Assert.Equal("jawOpen", channel.Name);
        Assert.True(
            ImmutableArray.Create("jawOpen", "Jaw_Alias")
                .AsSpan()
                .SequenceEqual(channel.Aliases.AsSpan()));
        Assert.Equal(25.0, channel.DefaultDeformPercent);
        Assert.Equal(
            FbxFacialSourceValueUnit.Percent,
            channel.SourceValueUnit);
        Assert.Equal(0.01, channel.SourceToAuthoredScale);
        Assert.Equal(0.25, channel.DefaultAuthoredValue);
        Assert.True(channel.Animated);
        Assert.True(result.HasFacialAnimation);
        FbxFacialCurveBinding binding =
            Assert.IsType<FbxFacialCurveBinding>(channel.Binding);
        Assert.Equal(10, binding.ChannelId);
        Assert.Equal(20, binding.CurveNodeId);
        Assert.Equal("DeformPercent", binding.TargetPropertyName);
        Assert.Equal("d|DeformPercent", binding.CurvePropertyName);
        Assert.True(
            ImmutableArray.Create(0.0, 50.0, 100.0)
                .AsSpan()
                .SequenceEqual(binding.Curve.KeyValues.AsSpan()));
        Assert.Equal(
            0.5,
            result.Clip.SampleScalars(1.0 / 30.0)["jawOpen"],
            12);
    }

    [Fact]
    public void ExplicitSelectionIsolatesMalformedCurveInUnusedStack()
    {
        FbxBinaryDocument document = Document(
            [
                Channel(10, "smile", 0.0),
                Stack(40, "Good", 0, FrameTick),
                Stack(41, "Broken", 0, FrameTick),
                Layer(100, "GoodLayer"),
                Layer(101, "BrokenLayer"),
                CurveNode(20),
                CurveNode(21),
                Curve(30, [0, FrameTick], [0.0, 100.0]),
                Curve(31, [0, FrameTick], [10.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 101, 41),
                Connection("OO", 20, 100),
                Connection("OO", 21, 101),
                Connection("OP", 20, 10, "DeformPercent"),
                Connection("OP", 21, 10, "DeformPercent"),
                Connection("OP", 30, 20, "d|X"),
                Connection("OP", 31, 21, "d|X"),
            ],
            GlobalSettings(Property70("TimeMode", 6)));

        FbxFacialAnimationImportResult good =
            FbxFacialAnimationAdapter.Import(
                document,
                new FbxFacialAnimationImportOptions
                {
                    AnimationStackName = "Good",
                    DefaultSourceValueUnit =
                        FbxFacialSourceValueUnit.Percent,
                });

        Assert.Equal("Good", good.AnimationStack.Name);
        Assert.True(good.HasFacialAnimation);
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxFacialAnimationAdapter.Import(
                document,
                new FbxFacialAnimationImportOptions
                {
                    AnimationStackName = "Broken",
                }));
        Assert.Contains(
            "selected facial animation curve 31",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "equal non-empty",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleStacksRequireAnExplicitFacialTake()
    {
        FbxBinaryDocument document = Document(
            [
                Channel(10, "smile", 0.0),
                Stack(40, "First", 0, 0),
                Stack(41, "Second", 0, 0),
                Layer(100, "FirstLayer"),
                Layer(101, "SecondLayer"),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 101, 41),
            ]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxFacialAnimationAdapter.Import(document));

        Assert.Contains(
            "select one explicitly",
            error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "Visibility",
        "d|DeformPercent",
        "expected DeformPercent")]
    [InlineData(
        "DeformPercent",
        "d|Y",
        "unsupported scalar axis")]
    public void RejectsUnsupportedChannelPropertyOrScalarAxis(
        string targetProperty,
        string curveProperty,
        string expectedMessage)
    {
        FbxBinaryDocument document = SingleCurveDocument(
            targetProperty,
            curveProperty);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxFacialAnimationAdapter.Import(document));

        Assert.Contains(
            expectedMessage,
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresExactlyOneChannelBindingAndOneScalarCurve()
    {
        FbxBinaryDocument multipleChannels = Document(
            [
                Channel(10, "left", 0.0),
                Channel(11, "right", 0.0),
                Stack(40, "Face", 0, FrameTick),
                Layer(100, "Layer"),
                CurveNode(20),
                Curve(30, [0, FrameTick], [0.0, 1.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 10, "DeformPercent"),
                Connection("OP", 20, 11, "DeformPercent"),
                Connection("OP", 30, 20, "d"),
            ]);
        FbxBinaryDocument multipleCurves = Document(
            [
                Channel(10, "left", 0.0),
                Stack(40, "Face", 0, FrameTick),
                Layer(100, "Layer"),
                CurveNode(20),
                Curve(30, [0, FrameTick], [0.0, 1.0]),
                Curve(31, [0, FrameTick], [0.0, 2.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 10, "DeformPercent"),
                Connection("OP", 30, 20, "d"),
                Connection("OP", 31, 20, "d|X"),
            ]);

        InvalidDataException channelError =
            Assert.Throws<InvalidDataException>(
                () => FbxFacialAnimationAdapter.Import(
                    multipleChannels));
        InvalidDataException curveError =
            Assert.Throws<InvalidDataException>(
                () => FbxFacialAnimationAdapter.Import(
                    multipleCurves));

        Assert.Contains(
            "2 BlendShapeChannel bindings",
            channelError.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "owns 2 AnimationCurve objects",
            curveError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StaticProperties70DefaultRemainsRawAndIsNotAnimated()
    {
        FbxBinaryDocument document = Document(
            [
                ChannelFromProperties70(10, "blink", 50.0),
                Stack(40, "Face", 0, FrameTick * 2),
                Layer(100, "Layer"),
            ],
            [
                Connection("OO", 100, 40),
            ],
            GlobalSettings(Property70("TimeMode", 6)));

        FbxFacialAnimationImportResult result =
            FbxFacialAnimationAdapter.Import(
                document,
                PercentOptions());

        FbxFacialChannel channel = Assert.Single(result.Channels);
        Assert.Null(channel.Binding);
        Assert.Equal(50.0, channel.DefaultDeformPercent);
        Assert.Equal(
            FbxFacialSourceValueUnit.Percent,
            channel.SourceValueUnit);
        Assert.Equal(0.01, channel.SourceToAuthoredScale);
        Assert.False(channel.Animated);
        Assert.False(result.HasFacialAnimation);
        Assert.Equal(
            0.5,
            result.Clip.SampleScalars(1.0 / 30.0)["blink"],
            12);
    }

    [Fact]
    public void FacialOnlyKeysDefineSpanAndLowConfidenceTimebase()
    {
        long sourceFrameTick =
            FbxBinaryDocument.TicksPerSecond / 24;
        FbxBinaryDocument document = Document(
            [
                Channel(10, "jawOpen", 0.0),
                Stack(40, "Face", 0, 0),
                Layer(100, "Layer"),
                CurveNode(20),
                Curve(
                    30,
                    [0, sourceFrameTick, sourceFrameTick * 2],
                    [0.0, 50.0, 100.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 10, "DeformPercent"),
                Connection("OP", 30, 20, "d|DeformPercent"),
            ]);

        FbxFacialAnimationImportResult result =
            FbxFacialAnimationAdapter.Import(
                document,
                PercentOptions());

        Assert.Equal(
            new FrameRate(24, 1),
            result.DeclaredTimebase.FrameRate);
        Assert.Equal(
            FbxTimebaseSource.AnimationCurveKeySpacing,
            result.DeclaredTimebase.Source);
        Assert.Equal(
            FbxTimebaseConfidence.InferredLow,
            result.DeclaredTimebase.Confidence);
        Assert.Equal(3, result.SampleTicks.Length);
        Assert.Equal(sourceFrameTick * 2, result.SampleTicks[^1]);
        Assert.Equal(1.0, result.Clip.SampleScalars(2.0 / 24.0)["jawOpen"]);
    }

    [Fact]
    public void KeepsDeclaredSourceRateAndExactTickSpanWhenSamplingAtAnotherRate()
    {
        const double durationSeconds = 12.6333338419;
        long startTick = FbxBinaryDocument.TicksPerSecond;
        long stopTick = startTick + checked(
            (long)Math.Round(
                durationSeconds * FbxBinaryDocument.TicksPerSecond));
        FbxBinaryDocument document = Document(
            [
                Channel(10, "blink", 0.0),
                Stack(40, "Face", startTick, stopTick),
                Layer(100, "Layer"),
            ],
            [
                Connection("OO", 100, 40),
            ],
            GlobalSettings(Property70("TimeMode", 11)));

        FbxFacialAnimationImportResult result =
            FbxFacialAnimationAdapter.Import(
                document,
                new FbxFacialAnimationImportOptions
                {
                    SamplingFrameRate = new FrameRate(30, 1),
                    DefaultSourceValueUnit =
                        FbxFacialSourceValueUnit.Percent,
                });

        Assert.Equal(
            new FrameRate(24, 1),
            result.DeclaredTimebase.FrameRate);
        Assert.Equal(new FrameRate(30, 1), result.Clip.FrameRate);
        Assert.Equal(startTick, result.SourceStartTick);
        Assert.Equal(stopTick, result.SourceStopTick);
        Assert.Equal(
            durationSeconds,
            result.SourceDurationSeconds,
            9);
        Assert.Equal(381, result.Clip.FrameCount);
    }

    [Fact]
    public void ExplicitNormalizedUnitLeavesSourceValuesUnchanged()
    {
        FbxFacialAnimationImportResult result =
            FbxFacialAnimationAdapter.Import(
                SingleCurveDocument(
                    "DeformPercent",
                    "d|X"),
                new FbxFacialAnimationImportOptions
                {
                    DefaultSourceValueUnit =
                        FbxFacialSourceValueUnit.Normalized,
                });

        FbxFacialChannel channel = Assert.Single(result.Channels);
        Assert.Equal(
            FbxFacialSourceValueUnit.Normalized,
            channel.SourceValueUnit);
        Assert.Equal(1.0, channel.SourceToAuthoredScale);
        Assert.Equal(
            1.0,
            result.Clip.SampleScalars(1.0 / 30.0)["smile"]);
    }

    [Fact]
    public void RefusesToGuessSourceValueUnitFromCurveRange()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxFacialAnimationAdapter.Import(
                SingleCurveDocument(
                    "DeformPercent",
                    "d|X")));

        Assert.Contains(
            "no explicit source value unit",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "not inferred from value ranges",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SupportsExplicitMixedUnitsByCanonicalChannelName()
    {
        FbxBinaryDocument document = Document(
            [
                Channel(10, "smile", 0.0),
                Channel(11, "browRaise", 50.0),
                Stack(40, "Face", 0, FrameTick),
                Layer(100, "Layer"),
                CurveNode(20),
                Curve(30, [0, FrameTick], [0.0, 1.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 10, "DeformPercent"),
                Connection("OP", 30, 20, "d|X"),
            ],
            GlobalSettings(Property70("TimeMode", 6)));

        FbxFacialAnimationImportResult result =
            FbxFacialAnimationAdapter.Import(
                document,
                new FbxFacialAnimationImportOptions
                {
                    ChannelSourceValueUnits =
                        new Dictionary<string, FbxFacialSourceValueUnit>
                        {
                            ["smile"] =
                                FbxFacialSourceValueUnit.Normalized,
                            ["browRaise"] =
                                FbxFacialSourceValueUnit.Percent,
                        },
                });

        FbxFacialChannel smile =
            Assert.Single(
                result.Channels,
                static channel => channel.Name == "smile");
        FbxFacialChannel brow =
            Assert.Single(
                result.Channels,
                static channel => channel.Name == "browRaise");
        Assert.Equal(
            FbxFacialSourceValueUnit.Normalized,
            smile.SourceValueUnit);
        Assert.Equal(
            FbxFacialSourceValueUnit.Percent,
            brow.SourceValueUnit);
        Assert.Equal(
            1.0,
            result.Clip.SampleScalars(1.0 / 30.0)["smile"]);
        Assert.Equal(
            0.5,
            result.Clip.SampleScalars(1.0 / 30.0)["browRaise"]);
    }

    [Fact]
    public void RejectsUnknownPerChannelSourceUnitOverride()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxFacialAnimationAdapter.Import(
                SingleCurveDocument(
                    "DeformPercent",
                    "d|X"),
                new FbxFacialAnimationImportOptions
                {
                    DefaultSourceValueUnit =
                        FbxFacialSourceValueUnit.Normalized,
                    ChannelSourceValueUnits =
                        new Dictionary<string, FbxFacialSourceValueUnit>
                        {
                            ["typo"] =
                                FbxFacialSourceValueUnit.Percent,
                        },
                }));

        Assert.Contains(
            "does not match a canonical BlendShapeChannel name",
            error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x00006102, "constant")]
    [InlineData(0x00006108, "cubic")]
    public void RejectsNonLinearFacialCurves(
        int interpolationFlags,
        string expectedKind)
    {
        FbxBinaryDocument document = Document(
            [
                Channel(10, "smile", 0.0),
                Stack(40, "Face", 0, FrameTick),
                Layer(100, "Layer"),
                CurveWithInterpolation(
                    30,
                    [0, FrameTick],
                    [0.0, 1.0],
                    interpolationFlags),
                CurveNode(20),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 10, "DeformPercent"),
                Connection("OP", 30, 20, "d|X"),
            ]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxFacialAnimationAdapter.Import(
                document,
                new FbxFacialAnimationImportOptions
                {
                    DefaultSourceValueUnit =
                        FbxFacialSourceValueUnit.Normalized,
                }));

        Assert.Contains(
            $"uses {expectedKind} interpolation",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "only baked linear curves",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsCurveWithoutInterpolationMetadata()
    {
        FbxBinaryDocument document = Document(
            [
                Channel(10, "smile", 0.0),
                Stack(40, "Face", 0, FrameTick),
                Layer(100, "Layer"),
                CurveWithoutInterpolation(
                    30,
                    [0, FrameTick],
                    [0.0, 1.0]),
                CurveNode(20),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 10, "DeformPercent"),
                Connection("OP", 30, 20, "d|X"),
            ]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxFacialAnimationAdapter.Import(
                document,
                new FbxFacialAnimationImportOptions
                {
                    DefaultSourceValueUnit =
                        FbxFacialSourceValueUnit.Normalized,
                }));

        Assert.Contains(
            "has no KeyAttrFlags interpolation metadata",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Bake or linearize",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcesChannelRawKeyFrameAndAggregateSampleBounds()
    {
        FbxBinaryDocument document = SingleCurveDocument(
            "DeformPercent",
            "d|X");
        FbxBinaryDocument twoChannels = Document(
            [
                Channel(10, "left", 0.0),
                Channel(11, "right", 0.0),
                Stack(40, "Face", 0, 0),
                Layer(100, "Layer"),
            ],
            [
                Connection("OO", 100, 40),
            ]);

        InvalidDataException channelError =
            Assert.Throws<InvalidDataException>(
                () => FbxFacialAnimationAdapter.Import(
                    twoChannels,
                    new FbxFacialAnimationImportOptions
                    {
                        MaximumChannels = 1,
                    }));
        InvalidDataException rawError =
            Assert.Throws<InvalidDataException>(
                () => FbxFacialAnimationAdapter.Import(
                    document,
                    new FbxFacialAnimationImportOptions
                    {
                        MaximumRawCurveKeys = 1,
                    }));
        InvalidDataException frameError =
            Assert.Throws<InvalidDataException>(
                () => FbxFacialAnimationAdapter.Import(
                    document,
                    new FbxFacialAnimationImportOptions
                    {
                        MaximumSampleFrames = 1,
                    }));
        InvalidDataException aggregateError =
            Assert.Throws<InvalidDataException>(
                () => FbxFacialAnimationAdapter.Import(
                    document,
                    new FbxFacialAnimationImportOptions
                    {
                        MaximumSampledScalarKeys = 1,
                    }));

        Assert.Contains(
            "more than 1 BlendShapeChannel",
            channelError.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "2 raw DeformPercent keys",
            rawError.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "2 samples",
            frameError.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "2 sampled scalar keys",
            aggregateError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HonorsCancellationBeforeFacialParsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => FbxFacialAnimationAdapter.Import(
                SingleCurveDocument(
                    "DeformPercent",
                    "d|DeformPercent",
                    "jawOpen"),
                options: null,
                cancellation.Token));
    }

    [Fact]
    public void RejectsDuplicateExposedChannelNames()
    {
        FbxBinaryDocument document = Document(
            [
                Channel(10, "Smile", 0.0),
                Channel(11, "smile", 0.0),
                Stack(40, "Face", 0, 0),
                Layer(100, "Layer"),
            ],
            [
                Connection("OO", 100, 40),
            ]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxFacialAnimationAdapter.Import(document));

        Assert.Contains(
            "names must be unique",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectReviewUsesBodyTimelineAndKeepsSuggestionsUnexportable()
    {
        FbxBinaryDocument document = Document(
            [
                Channel(10, "jawOpen", 0.0),
                Shape(11, "Jaw_Alias"),
                Stack(40, "Face", 0, FrameTick * 2),
                Layer(100, "FaceLayer"),
                CurveNode(20),
                Curve(
                    30,
                    [0, FrameTick, FrameTick * 2],
                    [0.0, 50.0, 100.0]),
            ],
            [
                Connection("OO", 11, 10),
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 10, "DeformPercent"),
                Connection("OP", 30, 20, "d|DeformPercent"),
            ],
            GlobalSettings(Property70("TimeMode", 6)));
        FbxFacialAnimationImportResult imported =
            FbxFacialAnimationAdapter.Import(
                document,
                PercentOptions());
        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();
        RigDefinition exactTarget = FacialTargetRig(
            profile,
            "morph_jaw_open");
        var timing = new AnimationTiming(
            new FrameRate(30, 1),
            3);

        FbxFacialProjectReview review =
            FbxFacialProjectReviewService.Create(
                new FbxFacialProjectReviewRequest
                {
                    SourcePath = "Sources/face.fbx",
                    Import = imported,
                    BodyTiming = timing,
                    Profile = profile,
                    ExactTargetRig = exactTarget,
                });

        Assert.Equal(timing, review.Timing);
        Assert.Equal(profile.ProfileId, review.ProfileId);
        Assert.Equal(64, review.MappingFingerprint.Length);
        Assert.Empty(review.UnmappedAnimatedChannels);
        FbxFacialProjectSourceChannel source =
            Assert.Single(review.SourceChannels);
        Assert.Equal(
            FbxFacialSourceValueUnit.Percent,
            source.SourceValueUnit);
        Assert.Equal(0.01, source.SourceToAuthoredScale);
        Assert.Equal(
            0.5,
            review.SourceScan.Curves[0].Values[1],
            12);
        ProjectMorphBinding suggestion =
            Assert.Single(review.SuggestedBindings);
        Assert.Equal(
            ProjectMorphSourceValueUnit.Percent,
            suggestion.SourceValueUnit);
        Assert.Equal("morph_jaw_open", suggestion.TargetMorph);
        Assert.False(suggestion.IsReviewed);
        Assert.False(suggestion.IsLocked);

        var projectAnimation = new ProjectAnimation
        {
            Name = "Body with face",
            SourceAssetId = Guid.NewGuid(),
            TargetRigId = exactTarget.Id,
            FrameRate = timing.FrameRate,
            FrameCount = timing.FrameCount,
        };
        ProjectAnimation updated = review.ApplyTo(projectAnimation);
        Assert.Equal(profile.ProfileId, updated.MimicProfileId);
        Assert.Equal(
            review.MappingFingerprint,
            updated.MimicMappingFingerprint);
        Assert.Single(
            ProjectMorphBindingResolver.Resolve(
                updated.MorphBindings,
                exactTarget,
                ProjectMorphBindingResolutionMode.Preview));
        InvalidDataException exportError =
            Assert.Throws<InvalidDataException>(
                () => ProjectMorphBindingResolver.Resolve(
                    updated.MorphBindings,
                    exactTarget,
                    ProjectMorphBindingResolutionMode.Export));
        Assert.Contains(
            "not reviewed and locked",
            exportError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectReviewRefusesAnIndependentFacialTimeline()
    {
        FbxFacialAnimationImportResult imported =
            FbxFacialAnimationAdapter.Import(
                SingleCurveDocument(
                    "DeformPercent",
                    "d|DeformPercent",
                    "jawOpen"),
                PercentOptions());
        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();
        RigDefinition exactTarget = FacialTargetRig(
            profile,
            "morph_jaw_open");

        InvalidDataException rateError =
            Assert.Throws<InvalidDataException>(
                () => FbxFacialProjectReviewService.Create(
                    new FbxFacialProjectReviewRequest
                    {
                        SourcePath = "Sources/face.fbx",
                        Import = imported,
                        BodyTiming = new AnimationTiming(
                            new FrameRate(25, 1),
                            2),
                        Profile = profile,
                        ExactTargetRig = exactTarget,
                    }));
        Assert.Contains(
            "Explicitly resample",
            rateError.Message,
            StringComparison.Ordinal);

        InvalidDataException frameError =
            Assert.Throws<InvalidDataException>(
                () => FbxFacialProjectReviewService.Create(
                    new FbxFacialProjectReviewRequest
                    {
                        SourcePath = "Sources/face.fbx",
                        Import = imported,
                        BodyTiming = new AnimationTiming(
                            new FrameRate(30, 1),
                            3),
                        Profile = profile,
                        ExactTargetRig = exactTarget,
                    }));
        Assert.Contains(
            "3 frames",
            frameError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectReviewRequiresTheExactRetailMorphDescriptor()
    {
        FbxFacialAnimationImportResult imported =
            FbxFacialAnimationAdapter.Import(
                SingleCurveDocument(
                    "DeformPercent",
                    "d|DeformPercent",
                    "jawOpen"),
                PercentOptions());
        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();
        var rigWithoutMappedMorph = new RigDefinition(
            "retail:wrong-family",
            "Wrong family",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
            ]);

        InvalidDataException error =
            Assert.Throws<InvalidDataException>(
                () => FbxFacialProjectReviewService.Create(
                    new FbxFacialProjectReviewRequest
                    {
                        SourcePath = "Sources/face.fbx",
                        Import = imported,
                        BodyTiming = new AnimationTiming(
                            new FrameRate(30, 1),
                            2),
                        Profile = profile,
                        ExactTargetRig = rigWithoutMappedMorph,
                    }));

        Assert.Contains(
            "exactly one morph",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "exact retail rig",
            error.Message,
            StringComparison.Ordinal);
    }

    private static RigDefinition FacialTargetRig(
        Dl1MimicProfile profile,
        string targetName)
    {
        Dl1MimicTarget target = Assert.Single(
            profile.Targets,
            candidate => string.Equals(
                candidate.Name,
                targetName,
                StringComparison.Ordinal));
        return new RigDefinition(
            "retail:facial-control",
            "Facial control",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    target.Name,
                    target.Descriptor,
                    "face.jaw"),
            ]);
    }

    private static FbxBinaryDocument SingleCurveDocument(
        string targetProperty,
        string curveProperty,
        string channelName = "smile") =>
        Document(
            [
                Channel(10, channelName, 0.0),
                Stack(40, "Face", 0, FrameTick),
                Layer(100, "Layer"),
                CurveNode(20),
                Curve(30, [0, FrameTick], [0.0, 1.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 10, targetProperty),
                Connection("OP", 30, 20, curveProperty),
            ],
            GlobalSettings(Property70("TimeMode", 6)));

    private static FbxNode Channel(
        long objectId,
        string name,
        double defaultValue) =>
        Node(
            "Deformer",
            [
                objectId,
                $"SubDeformer::{name}",
                "BlendShapeChannel",
            ],
            Node("DeformPercent", [defaultValue]));

    private static FbxNode ChannelFromProperties70(
        long objectId,
        string name,
        double defaultValue) =>
        Node(
            "Deformer",
            [
                objectId,
                $"SubDeformer::{name}",
                "BlendShapeChannel",
            ],
            Node(
                "Properties70",
                [],
                Property70("Deform Percent", defaultValue)));

    private static FbxNode Shape(long objectId, string name) =>
        Node(
            "Geometry",
            [objectId, $"Geometry::{name}", "Shape"]);

    private static FbxNode CurveNode(long objectId) =>
        Node(
            "AnimationCurveNode",
            [
                objectId,
                $"AnimationCurveNode::{objectId}",
                string.Empty,
            ]);

    private static FbxNode Layer(long objectId, string name) =>
        Node(
            "AnimationLayer",
            [objectId, $"AnimLayer::{name}", string.Empty]);

    private static FbxNode Stack(
        long objectId,
        string name,
        long start,
        long stop) =>
        Node(
            "AnimationStack",
            [objectId, $"AnimStack::{name}", string.Empty],
            Node(
                "Properties70",
                [],
                Property70("LocalStart", start),
                Property70("LocalStop", stop)));

    private static FbxNode Curve(
        long objectId,
        long[] keyTimes,
        double[] keyValues) =>
        CurveWithInterpolation(
            objectId,
            keyTimes,
            keyValues,
            0x00006104);

    private static FbxNode CurveWithInterpolation(
        long objectId,
        long[] keyTimes,
        double[] keyValues,
        int interpolationFlags) =>
        Node(
            "AnimationCurve",
            [
                objectId,
                $"AnimationCurve::{objectId}",
                string.Empty,
            ],
            Node("KeyTime", [keyTimes.ToImmutableArray()]),
            Node("KeyValueFloat", [keyValues.ToImmutableArray()]),
            Node(
                "KeyAttrFlags",
                [ImmutableArray.Create(interpolationFlags)]),
            Node(
                "KeyAttrDataFloat",
                [ImmutableArray.Create(0.0f, 0.0f, 0.0f, 0.0f)]),
            Node(
                "KeyAttrRefCount",
                [ImmutableArray.Create(keyTimes.Length)]));

    private static FbxNode CurveWithoutInterpolation(
        long objectId,
        long[] keyTimes,
        double[] keyValues) =>
        Node(
            "AnimationCurve",
            [
                objectId,
                $"AnimationCurve::{objectId}",
                string.Empty,
            ],
            Node("KeyTime", [keyTimes.ToImmutableArray()]),
            Node("KeyValueFloat", [keyValues.ToImmutableArray()]));

    private static FbxFacialAnimationImportOptions PercentOptions() =>
        new()
        {
            DefaultSourceValueUnit =
                FbxFacialSourceValueUnit.Percent,
        };

    private static FbxNode GlobalSettings(params FbxNode[] properties) =>
        Node(
            "GlobalSettings",
            [],
            Node("Properties70", [], properties));

    private static FbxNode Property70(
        string name,
        params object[] values) =>
        Node(
            "P",
            [name, name, string.Empty, "A", .. values]);

    private static FbxNode Connection(
        string kind,
        long childId,
        long parentId,
        params object[] metadata) =>
        Node(
            "C",
            [kind, childId, parentId, .. metadata]);

    private static FbxBinaryDocument Document(
        FbxNode[] objects,
        FbxNode[] connections,
        FbxNode? globalSettings = null)
    {
        var nodes = ImmutableArray.CreateBuilder<FbxNode>();
        if (globalSettings is not null)
        {
            nodes.Add(globalSettings);
        }

        nodes.Add(Node("Objects", [], objects));
        nodes.Add(Node("Connections", [], connections));
        return new FbxBinaryDocument(7400, nodes.ToImmutable());
    }

    private static FbxNode Node(
        string name,
        object[] properties,
        params FbxNode[] children) =>
        new(
            name,
            properties.Select(Property).ToImmutableArray(),
            children.ToImmutableArray(),
            0,
            0);

    private static FbxProperty Property(object value) =>
        new(
            value switch
            {
                long => 'L',
                int => 'I',
                float => 'F',
                double => 'D',
                string => 'S',
                ImmutableArray<int> => 'i',
                ImmutableArray<float> => 'f',
                ImmutableArray<long> => 'l',
                ImmutableArray<double> => 'd',
                _ => 'R',
            },
            value);
}
