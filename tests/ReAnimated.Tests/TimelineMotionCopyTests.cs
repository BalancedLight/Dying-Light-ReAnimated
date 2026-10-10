using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Ik;

namespace ReAnimated.Tests;

public sealed class TimelineMotionCopyTests
{
    [Fact]
    public void BoneCopyPreservesAnAuthoredPulseAndExcludesPreviewLayers()
    {
        RigDefinition rig = CreateRig();
        AnimationClip clip = CreateBodyClip(11);
        BoneEditLayer pulse = BoneLayer(
            "Authored motion",
            BoneEditLayerScope.AuthoredExportable,
            BoneEditBlendMode.Override,
            1,
            [Key(0, 0), Key(5, 5), Key(10, 0)]);
        BoneEditLayer preview = BoneLayer(
            "Display adjustment",
            BoneEditLayerScope.PreviewOnly,
            BoneEditBlendMode.Override,
            1,
            [Key(0, 40), Key(10, 40)]);
        EvaluationRequest request = Request(rig, clip, [pulse, preview]);

        BoneEditLayer copied = MainWindowViewModel.BuildBoneEditLayer(
            clip.TransformTracks[0], 0, request, clip.FrameRate,
            0, 10, Guid.NewGuid(), CancellationToken.None);
        BoneEditTrack copiedTrack = Assert.Single(copied.Tracks);
        EvaluationFrame expectedAtPulse = Evaluate(request, 5);
        EvaluationFrame expectedBetweenKeys = Evaluate(request, 2.5);

        Assert.Equal(5, copiedTrack.Sample(5).Translation.X, 8);
        Assert.Equal(expectedAtPulse.AuthoredPose.LocalTransforms[0], copiedTrack.Sample(5));
        Assert.Equal(expectedBetweenKeys.AuthoredPose.LocalTransforms[0].Translation.X,
            copiedTrack.Sample(2.5).Translation.X, 6);
        Assert.Contains(copiedTrack.Keyframes, key => key.Frame == 5);
        Assert.True(copiedTrack.Keyframes.Length > 3);
    }

    [Fact]
    public void MorphCopyKeepsComposedBindingsAndExistingFacialEdits()
    {
        RigDefinition rig = CreateRig(includeMorph: true);
        var clip = new AnimationClip(
            "Expression",
            new FrameRate(24, 1),
            11,
            [new TransformTrack(0, [Key(0, 0), Key(10, 0)])],
            [
                new ScalarTrack("ChannelA", [new ScalarKeyframe(0, 0), new ScalarKeyframe(10, 1)]),
                new ScalarTrack("ChannelB", [new ScalarKeyframe(0, 1), new ScalarKeyframe(10, 0)]),
            ]);
        MorphEditLayer additive = new(
            Guid.NewGuid(), "Facial addition", MorphEditBlendMode.Additive,
            MorphEditLayerScope.AuthoredExportable, .4,
            [new MorphEditTrack("Expression", [new ScalarKeyframe(0, .25), new ScalarKeyframe(10, .75)])]);
        MorphEditLayer overrideLayer = new(
            Guid.NewGuid(), "Facial correction", MorphEditBlendMode.Override,
            MorphEditLayerScope.AuthoredExportable, .3,
            [new MorphEditTrack("Expression", [new ScalarKeyframe(0, .8), new ScalarKeyframe(5, .2), new ScalarKeyframe(10, .6)])]);
        EvaluationRequest request = Request(rig, clip,
            morphBindings:
            [
                new MorphChannelBinding("ChannelA", "Expression", .5, .1),
                new MorphChannelBinding("ChannelB", "Expression", .25, .2),
            ],
            morphLayers: [additive, overrideLayer]);

        MorphEditLayer copied = MainWindowViewModel.BuildMorphEditLayer(
            clip.ScalarTracks[0], ("Expression", .5, .1), 0, 10,
            Guid.NewGuid(), CancellationToken.None, request: request, frameRate: clip.FrameRate);
        MorphEditTrack copiedTrack = Assert.Single(copied.Tracks);

        foreach (double frame in new[] { 0d, 2.5, 5d, 7.5, 10d })
        {
            double expected = Evaluate(request, frame).AuthoredMorphWeights["Expression"];
            Assert.Equal(expected, copiedTrack.Sample(frame), 6);
        }
        Assert.Contains(copiedTrack.Keyframes, key => key.Frame == 5);
    }

    [Fact]
    public void SourcePlusKeyCopyAddsPlayheadKeyWithoutDroppingSurroundingMotion()
    {
        RigDefinition rig = CreateRig();
        AnimationClip clip = CreateBodyClip(11);
        BoneEditLayer authored = BoneLayer(
            "Authored motion",
            BoneEditLayerScope.AuthoredExportable,
            BoneEditBlendMode.Override,
            1,
            [Key(0, 0), Key(10, 10)]);
        EvaluationRequest request = Request(rig, clip, [authored]);

        BoneEditLayer copied = MainWindowViewModel.BuildBoneEditLayer(
            clip.TransformTracks[0], 0, request, clip.FrameRate,
            0, 10, Guid.NewGuid(), CancellationToken.None,
            includeCurrentFrame: true, currentFrame: 4.25);
        BoneEditTrack copiedTrack = Assert.Single(copied.Tracks);

        Assert.Contains(copiedTrack.Keyframes, key => key.Frame == 4.25);
        Assert.True(copiedTrack.Keyframes.Length > 1);
        foreach (double frame in new[] { 0d, 2d, 4.25, 7d, 10d })
            Assert.Equal(Evaluate(request, frame).AuthoredPose.LocalTransforms[0].Translation.X,
                copiedTrack.Sample(frame).Translation.X, 6);
    }

    [Fact]
    public void CopyFrameUnionIncludesClipSourceAndAuthoredKeyTimesAndCurrentFrame()
    {
        RigDefinition rig = CreateRig(includeMorph: true);
        var clip = new AnimationClip(
            "Expression",
            new FrameRate(24, 1),
            11,
            [new TransformTrack(0, [Key(0, 0), Key(4.5, 0), Key(10, 0)])],
            [new ScalarTrack("Channel", [new ScalarKeyframe(0, 0), new ScalarKeyframe(7.25, 1), new ScalarKeyframe(10, 0)])]);
        BoneEditLayer authored = BoneLayer(
            "Authored motion",
            BoneEditLayerScope.AuthoredExportable,
            BoneEditBlendMode.Override,
            1,
            [Key(0, 0), Key(3.5, 2), Key(10, 0)]);
        MorphEditLayer facial = new(
            Guid.NewGuid(), "Facial motion", MorphEditBlendMode.Additive,
            MorphEditLayerScope.AuthoredExportable, 1,
            [new MorphEditTrack("Expression", [new ScalarKeyframe(0, 0), new ScalarKeyframe(6.5, .5), new ScalarKeyframe(10, 0)])]);
        EvaluationRequest request = Request(rig, clip, [authored], morphLayers: [facial]);

        ImmutableArray<double> frames = MainWindowViewModel.BuildMotionCopyFrames(
            request, [2.25], 0, 10, currentFrame: 8.25);

        Assert.Contains(2.25, frames);
        Assert.Contains(3.5, frames);
        Assert.Contains(4.5, frames);
        Assert.Contains(6.5, frames);
        Assert.Contains(7.25, frames);
        Assert.Contains(8.25, frames);
        Assert.Contains(0, frames);
        Assert.Contains(10, frames);
    }

    [Fact]
    public void TrimmedSequenceViewStillCopiesMotionFromBothEndsOfTheClip()
    {
        RigDefinition rig = CreateRig();
        AnimationClip clip = CreateBodyClip(11);
        BoneEditLayer authored = BoneLayer(
            "Authored motion",
            BoneEditLayerScope.AuthoredExportable,
            BoneEditBlendMode.Override,
            1,
            [Key(0, 2), Key(5, 7), Key(10, 3)]);
        EvaluationRequest request = Request(rig, clip, [authored]);

        (double start, double end) = MainWindowViewModel.GetMotionCopyBounds(request);
        double visibleStart = 3;
        double visibleEnd = 7;
        double currentSourceFrame = 5;
        Assert.True(visibleStart > start);
        Assert.True(visibleEnd < end);
        BoneEditLayer copied = MainWindowViewModel.BuildBoneEditLayer(
            clip.TransformTracks[0], 0, request, clip.FrameRate,
            start, end, Guid.NewGuid(), CancellationToken.None,
            includeCurrentFrame: true, currentFrame: currentSourceFrame);
        BoneEditTrack copiedTrack = Assert.Single(copied.Tracks);

        Assert.Equal(0, copiedTrack.Keyframes[0].Frame);
        Assert.Equal(10, copiedTrack.Keyframes[^1].Frame);
        Assert.Equal(Evaluate(request, 0).AuthoredPose.LocalTransforms[0].Translation.X,
            copiedTrack.Sample(0).Translation.X, 6);
        Assert.Equal(Evaluate(request, 10).AuthoredPose.LocalTransforms[0].Translation.X,
            copiedTrack.Sample(10).Translation.X, 6);
        Assert.Contains(copiedTrack.Keyframes, key => key.Frame == currentSourceFrame);
    }

    [Fact]
    public void CopyPreservesStepWhenItFullyDrivesTheTargetAndRejectsMixedInterpolation()
    {
        RigDefinition rig = CreateRig();
        AnimationClip clip = CreateBodyClip(11);
        BoneEditLayer step = BoneLayer(
            "Stepped motion",
            BoneEditLayerScope.AuthoredExportable,
            BoneEditBlendMode.Override,
            1,
            [Key(0, 0), Key(5, 5), Key(10, 0)],
            BoneEditInterpolation.Step);
        EvaluationRequest stepRequest = Request(rig, clip, [step]);

        BoneEditLayer copiedStep = MainWindowViewModel.BuildBoneEditLayer(
            clip.TransformTracks[0], 0, stepRequest, clip.FrameRate,
            0, 10, Guid.NewGuid(), CancellationToken.None);
        BoneEditTrack copiedStepTrack = Assert.Single(copiedStep.Tracks);
        Assert.Equal(BoneEditInterpolation.Step, copiedStepTrack.Interpolation);
        Assert.Equal(0, copiedStepTrack.Sample(4.9).Translation.X, 8);
        Assert.Equal(5, copiedStepTrack.Sample(5.1).Translation.X, 8);

        BoneEditLayer linear = BoneLayer(
            "Continuous motion",
            BoneEditLayerScope.AuthoredExportable,
            BoneEditBlendMode.Additive,
            .5,
            [Key(0, 0), Key(10, 1)]);
        EvaluationRequest mixedRequest = Request(rig, clip, [step, linear]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            MainWindowViewModel.BuildBoneEditLayer(
                clip.TransformTracks[0], 0, mixedRequest, clip.FrameRate,
                0, 10, Guid.NewGuid(), CancellationToken.None));
        Assert.Contains("Step", error.Message, StringComparison.Ordinal);

        BoneEditLayer maskedStep = new(
            Guid.NewGuid(), "Masked step", BoneEditBlendMode.Override,
            BoneEditLayerScope.AuthoredExportable, 1,
            [new BoneEditTrack(0, [Key(0, 0), Key(10, 1)], BoneEditInterpolation.Step)],
            boneMask: new Dictionary<int, double> { [0] = 0 });
        EvaluationRequest maskedRequest = Request(rig, clip, [maskedStep]);
        BoneEditLayer copiedMasked = MainWindowViewModel.BuildBoneEditLayer(
            clip.TransformTracks[0], 0, maskedRequest, clip.FrameRate,
            0, 10, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(BoneEditInterpolation.Linear, Assert.Single(copiedMasked.Tracks).Interpolation);
    }

    [Fact]
    public void ComposedNoncommutingRotationsAreCapturedWithinTheSamplingTolerance()
    {
        RigDefinition rig = CreateRig();
        AnimationClip clip = CreateBodyClip(2);
        BoneEditLayer rotateX = BoneLayer(
            "First rotation",
            BoneEditLayerScope.AuthoredExportable,
            BoneEditBlendMode.Additive,
            1,
            [
                new TransformKeyframe(0, TransformTRS.Identity),
                new TransformKeyframe(1, new TransformTRS(Vector3D.Zero,
                    QuaternionD.FromAxisAngle(Vector3D.UnitX, Math.PI / 2), Vector3D.One)),
            ]);
        BoneEditLayer rotateY = BoneLayer(
            "Second rotation",
            BoneEditLayerScope.AuthoredExportable,
            BoneEditBlendMode.Additive,
            1,
            [
                new TransformKeyframe(0, TransformTRS.Identity),
                new TransformKeyframe(1, new TransformTRS(Vector3D.Zero,
                    QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.PI / 2), Vector3D.One)),
            ]);
        EvaluationRequest request = Request(rig, clip, [rotateX, rotateY]);

        BoneEditLayer copied = MainWindowViewModel.BuildBoneEditLayer(
            clip.TransformTracks[0], 0, request, clip.FrameRate,
            0, 1, Guid.NewGuid(), CancellationToken.None);
        BoneEditTrack copiedTrack = Assert.Single(copied.Tracks);

        foreach (double frame in new[] { .125, .25, .375, .5, .625, .75, .875 })
        {
            QuaternionD expected = Evaluate(request, frame).AuthoredPose.LocalTransforms[0].Rotation;
            Assert.True(QuaternionAngle(expected, copiedTrack.Sample(frame).Rotation) <= 1.0e-6);
        }
        Assert.Contains(copiedTrack.Keyframes, key => key.Frame == .25);
        Assert.Contains(copiedTrack.Keyframes, key => key.Frame == .75);
    }

    [Fact]
    public void BoneCopyBeforePartialAuthoredIkDoesNotApplyTheConstraintTwice()
    {
        RigDefinition rig = CreateChainRig();
        var clip = new AnimationClip(
            "Chain motion",
            new FrameRate(30, 1),
            11,
            [new TransformTrack(0, [Key(0, 0), Key(10, 1)])]);
        var ik = new IkConstraintLayer(
            Guid.NewGuid(), "Authored chain", 0, 1, 2, .45,
            [
                new IkConstraintKeyframe(0, new Vector3D(1.2, .8, 0), Vector3D.UnitZ),
                new IkConstraintKeyframe(10, new Vector3D(1.2, 1.2, 0), Vector3D.UnitZ),
            ]);
        EvaluationRequest request = new(
            rig, rig, clip, 0, PreviewProfile.ThirdPersonAuthoring,
            purpose: EvaluationPurpose.Export,
            ikLayers: [ik]);

        BoneEditLayer copied = MainWindowViewModel.BuildBoneEditLayer(
            clip.TransformTracks[0], 0, request, clip.FrameRate,
            0, 10, Guid.NewGuid(), CancellationToken.None);
        EvaluationRequest copiedRequest = new(
            request.SourceRig, request.TargetRig, request.Clip, request.TimeSeconds,
            request.PreviewProfile, request.RetargetMap,
            request.EditLayers.Add(copied), request.IkConstraints,
            request.PlaybackMode, request.Purpose, request.Attachments,
            request.Dl1AuthoringPolicy, request.MorphBindings,
            request.MorphEditLayers, request.IkLayers, request.Dl1PreviewInputs,
            previewMotionAccumulationEnabled: false,
            directRigBinding: request.DirectRigBinding);

        foreach (double frame in new[] { 0d, 2.5, 5d, 7.5, 10d })
        {
            EvaluationFrame original = Evaluate(request, frame);
            EvaluationFrame withCopy = Evaluate(copiedRequest, frame);
            for (int boneIndex = 0; boneIndex < rig.BoneCount; boneIndex++)
            {
                TransformTRS expected = original.AuthoredPose.LocalTransforms[boneIndex];
                TransformTRS actual = withCopy.AuthoredPose.LocalTransforms[boneIndex];
                Assert.True((expected.Translation - actual.Translation).Length <= 1.0e-6);
                Assert.True((expected.Scale - actual.Scale).Length <= 1.0e-6);
                Assert.True(QuaternionAngle(expected.Rotation, actual.Rotation) <= 1.0e-6);
            }
        }
    }

    [Fact]
    public void ScalarRefinementChecksQuarterPointsWhenTheMidpointMatchesExactly()
    {
        var samples = new SortedDictionary<double, double> { [0] = 0, [1] = 1 };
        static double Smoothstep(double frame) => 3 * frame * frame - 2 * frame * frame * frame;

        MainWindowViewModel.RefineScalarSamples(samples, Smoothstep, CancellationToken.None);

        Assert.Equal(.5, Smoothstep(.5), 12);
        Assert.Contains(.25, samples.Keys);
        Assert.Contains(.75, samples.Keys);
        Assert.Equal(Smoothstep(.25), samples[.25], 12);
        Assert.Equal(Smoothstep(.75), samples[.75], 12);
    }

    [Fact]
    public void NewSequenceDefaultsUseClipRateAndFullUntrimmedFrameRange()
    {
        AnimationSequenceUse sequence = MainWindowViewModel.CreateAnimationSequenceDefaults(
            "Walk", "walk.anm2", Guid.NewGuid(), new FrameRate(24, 1), 173);

        Assert.Equal(24, sequence.FPS);
        Assert.Equal(0, sequence.SourceStartFrame);
        Assert.Equal(172, sequence.SourceEndFrame);
    }

    private static EvaluationRequest Request(
        RigDefinition rig,
        AnimationClip clip,
        IEnumerable<BoneEditLayer>? boneLayers = null,
        IEnumerable<MorphChannelBinding>? morphBindings = null,
        IEnumerable<MorphEditLayer>? morphLayers = null) =>
        new(rig, rig, clip, 0, PreviewProfile.ThirdPersonAuthoring,
            editLayers: boneLayers,
            purpose: EvaluationPurpose.Export,
            morphBindings: morphBindings,
            morphEditLayers: morphLayers);

    private static EvaluationFrame Evaluate(EvaluationRequest request, double frame) =>
        new AnimationEvaluator().Evaluate(new EvaluationRequest(
            request.SourceRig, request.TargetRig, request.Clip,
            request.Clip.FrameRate.SecondsForFrame(frame), request.PreviewProfile,
            request.RetargetMap, request.EditLayers, request.IkConstraints,
            PlaybackMode.Clamp, EvaluationPurpose.Export, request.Attachments,
            request.Dl1AuthoringPolicy, request.MorphBindings,
            request.MorphEditLayers, request.IkLayers, request.Dl1PreviewInputs,
            previewMotionAccumulationEnabled: false,
            directRigBinding: request.DirectRigBinding));

    private static RigDefinition CreateRig(bool includeMorph = false) =>
        new("timeline-copy", "Timeline copy rig",
            [new BoneDefinition(0, "Root", -1, TransformTRS.Identity, BoneKind.Root)],
            includeMorph ? [new MorphChannelDefinition(0, "Expression")] : null);

    private static RigDefinition CreateChainRig() =>
        new("timeline-chain", "Timeline chain rig",
        [
            new BoneDefinition(0, "Root", -1, TransformTRS.Identity, BoneKind.Root),
            new BoneDefinition(1, "Joint", 0, new TransformTRS(Vector3D.UnitX,
                QuaternionD.Identity, Vector3D.One)),
            new BoneDefinition(2, "End", 1, new TransformTRS(Vector3D.UnitX,
                QuaternionD.Identity, Vector3D.One)),
        ]);

    private static AnimationClip CreateBodyClip(long frameCount) =>
        new("Body motion", new FrameRate(30, 1), frameCount,
            [new TransformTrack(0, [Key(0, 0), Key(frameCount - 1, 0)])]);

    private static BoneEditLayer BoneLayer(
        string name,
        BoneEditLayerScope scope,
        BoneEditBlendMode blendMode,
        double weight,
        IEnumerable<TransformKeyframe> keys,
        BoneEditInterpolation interpolation = BoneEditInterpolation.Linear) =>
        new(Guid.NewGuid(), name, blendMode, scope, weight,
            [new BoneEditTrack(0, keys, interpolation)]);

    private static TransformKeyframe Key(double frame, double x) =>
        new(frame, new TransformTRS(
            new Vector3D(x, 0, 0), QuaternionD.Identity, Vector3D.One));

    private static double QuaternionAngle(QuaternionD left, QuaternionD right)
    {
        QuaternionD a = left.Normalized();
        QuaternionD b = right.Normalized();
        double dot = Math.Clamp(Math.Abs((a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W)), 0, 1);
        return 2 * Math.Acos(dot);
    }
}
