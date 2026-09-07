using System.Collections.Immutable;
using System.Text.Json;
using ReAnimated.Codecs.Fed;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class SpeechCurveExchangeTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly int[] ExpectedSamples = [0, 127, 254];
    private static SpeechCurveExchange Create() => new(1, new("dialogue.spb", new string('a',64), 120, 1, 1), 254,
        "sample/curveValueMaximum*maximumWeight", [new("line",4,3,.05,[new("W",.8,[0,127,254])])]);
    private static RigDefinition Rig(string target) => new("face", "Face", [new BoneDefinition(0,"root",-1,TransformTRS.Identity)], [new MorphChannelDefinition(0,target)]);

    [Fact]
    public void ReaderPreservesLabelSourceAndExactEncodedCurve()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(Create(), Options);
        SpeechCurveExchange result = SpeechCurveExchangeReader.Read(new MemoryStream(json));
        Assert.Equal("W", result.Entries[0].Tracks[0].Label);
        Assert.Equal(ExpectedSamples, result.Entries[0].Tracks[0].Samples);
        Assert.Equal(new string('a',64), result.Source.Sha256);
    }

    [Fact]
    public void AdapterUsesDeclaredTimingMaximumWeightAndNeutralEnd()
    {
        MorphEditLayer result = SpeechCurveDomainAdapter.CreateLayer(Create(), "line", Rig("w"), 60);
        Assert.Equal(MorphEditLayerScope.PreviewOnly, result.Scope);
        Assert.Equal(.4, result.Tracks[0].Sample(3), 8);
        Assert.Equal(.8, result.Tracks[0].Sample(6), 8);
        Assert.Equal(0, result.Tracks[0].Sample(9), 8);
    }

    [Fact]
    public void AdapterRejectsMissingTargetsAndAcceptsExplicitMapping()
    {
        Assert.Throws<InvalidOperationException>(() => SpeechCurveDomainAdapter.CreateLayer(Create(), "line", Rig("mouth_round"), 30));
        MorphEditLayer layer = SpeechCurveDomainAdapter.CreateLayer(Create(), "line", Rig("mouth_round"), 30,
            new Dictionary<string,string> { ["W"] = "mouth_round" });
        Assert.Equal("mouth_round", layer.Tracks[0].MorphName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    public void UnsafeTimingIsRejected(double step)
    {
        SpeechCurveExchange document = Create();
        document = document with { Entries = [document.Entries[0] with { FrameStepSeconds = step }] };
        Assert.Throws<InvalidDataException>(() => SpeechCurveExchangeReader.Validate(document));
    }

    [Fact]
    public void TruncatedInvalidAndDuplicateSamplesAreRejected()
    {
        SpeechCurveExchange d = Create();
        Assert.Throws<InvalidDataException>(() => SpeechCurveExchangeReader.Validate(d with { Entries = [d.Entries[0] with { FrameCount = 2 }] }));
        Assert.Throws<InvalidDataException>(() => SpeechCurveExchangeReader.Validate(d with { Entries = [d.Entries[0] with { Tracks = [new("W",.8,[0,255,0])] }] }));
        Assert.Throws<InvalidDataException>(() => SpeechCurveExchangeReader.Validate(d with { Entries = [d.Entries[0],d.Entries[0]] }));
        Assert.Throws<InvalidDataException>(() => SpeechCurveExchangeReader.Validate(d with { SchemaVersion = 2 }));
    }

    [Fact]
    public void ExplicitMinimumWeightInterpolatesWithoutDroppingFlags()
    {
        SpeechCurveExchange d = Create() with
        {
            WeightInterpretation = "lerp(minimumWeight,maximumWeight,sample/curveValueMaximum)",
            Entries = [new("line",4,3,.05,[new("W",.75,[0,127,254],.125,2)])],
        };
        MorphEditLayer layer = SpeechCurveDomainAdapter.CreateLayer(d, "line", Rig("w"), 60);
        Assert.Equal(.125, layer.Tracks[0].Sample(0), 8);
        Assert.Equal(.4375, layer.Tracks[0].Sample(3), 8);
        Assert.Equal(.75, layer.Tracks[0].Sample(6), 8);
        Assert.Equal(2, d.Entries[0].Tracks[0].Flags);
    }

    [Theory]
    [InlineData(false, "w")]
    [InlineData(true, "wide")]
    public void PlayerPrefixLookupUsesInventoryOrderAndReportsShadowedExactName(bool reversed, string expected)
    {
        string[] names = reversed ? ["wide", "w"] : ["w", "wide"];
        RigDefinition rig = new("face", "Face", [new BoneDefinition(0,"root",-1,TransformTRS.Identity)],
            names.Select((name,index) => new MorphChannelDefinition(index,name)));
        SpeechPreviewBuildResult built = SpeechCurveDomainAdapter.BuildPreview(Create(), "line", rig, 30);
        Assert.Equal("W", built.Resolution.Targets[0].SourceLabel);
        Assert.Equal(expected, built.Resolution.Targets[0].TargetMorph);
        Assert.Equal(0, built.Resolution.Targets[0].TargetInventoryIndex);
        Assert.Equal(expected, built.Layer.Tracks[0].MorphName);
        Assert.Equal(2, built.Resolution.Targets[0].PrefixCandidates.Length);
        Assert.False(built.Resolution.Targets[0].ExplicitConfiguredMapping);
        Assert.Contains(built.Resolution.Diagnostics, x => x.Code == "SPB_PREFIX_FIRST");
        Assert.Contains("Retail Game", built.Resolution.EvidenceBoundary);
    }

    [Fact]
    public void ExplicitMapIsLabeledAndAvoidsPrefixShadowing()
    {
        RigDefinition rig = new("face", "Face", [new BoneDefinition(0,"root",-1,TransformTRS.Identity)],
            [new MorphChannelDefinition(0,"wide"),new MorphChannelDefinition(1,"w")]);
        SpeechPreviewBuildResult result = SpeechCurveDomainAdapter.BuildPreview(Create(), "line", rig, 30,
            new Dictionary<string,string> { ["W"] = "w" });
        Assert.Equal("w", result.Resolution.Targets[0].TargetMorph);
        Assert.True(result.Resolution.Targets[0].ExplicitConfiguredMapping);
        Assert.Contains(result.Resolution.Diagnostics, x => x.Code == "SPB_CONFIGURED_MAPPING");
    }

    [Fact]
    public void TooManyTracksAndPrefixCollisionsRejectInsteadOfTruncatingOrMerging()
    {
        SpeechCurveExchange d = Create();
        d = d with { Entries = [d.Entries[0] with { Tracks = Enumerable.Range(0,16).Select(i => new SpeechCurveTrack("channel"+i,.5,[0,127,254])).ToImmutableArray() }] };
        Assert.Contains(SpeechCurveDomainAdapter.ResolveTargets(d.Entries[0], Rig("w")).Diagnostics, x => x.Code == "SPB_TRACK_LIMIT");
        Assert.Throws<InvalidOperationException>(() => SpeechCurveDomainAdapter.CreateLayer(d,"line",Rig("w"),30));
        d = Create() with { Entries = [new("line",4,3,.05,[new("W",.5,[0,127,254]),new("wide",.5,[0,127,254])])] };
        Assert.Contains(SpeechCurveDomainAdapter.ResolveTargets(d.Entries[0],Rig("wide")).Diagnostics, x => x.Code == "SPB_TARGET_COLLISION");
        Assert.Throws<InvalidOperationException>(() => SpeechCurveDomainAdapter.CreateLayer(d,"line",Rig("wide"),30));
    }
}
