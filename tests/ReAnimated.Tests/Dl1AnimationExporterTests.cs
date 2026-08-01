using System.Collections.Immutable;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class Dl1AnimationExporterTests
{
    [Fact]
    public void EncodesBodyAndMimicFromAuthoredEvaluatorState()
    {
        RigDefinition rig = CreateRig();
        var clip = new AnimationClip(
            "body_and_face",
            new FrameRate(30, 1),
            2,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(
                            1,
                            TransformTRS.Identity with
                            {
                                Translation = new Vector3D(2, 0, 0),
                            }),
                    ]),
            ],
            [
                new ScalarTrack(
                    "Smile",
                    [
                        new ScalarKeyframe(0, 0.25),
                        new ScalarKeyframe(1, 0.75),
                    ]),
            ]);
        var previewOnly = new BoneEditLayer(
            Guid.NewGuid(),
            "preview correction",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.PreviewOnly,
            1,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            TransformTRS.Identity with
                            {
                                Translation = new Vector3D(100, 0, 0),
                            }),
                    ]),
            ]);
        var evaluation = new EvaluationRequest(
            rig,
            rig,
            clip,
            0,
            PreviewProfile.FirstPersonAuthoring,
            editLayers: [previewOnly]);
        var exporter = new Dl1AnimationExporter(
            new Anm2EvaluationAdapter(new AnimationEvaluator()));

        Dl1AnimationExportResult result = exporter.Export(
            new Dl1AnimationExportRequest
            {
                Evaluation = evaluation,
                Parts = Dl1AnimationExportParts.BodyAndMimic,
            });

        Assert.NotNull(result.BodyAnm2);
        Assert.NotNull(result.MimicAnm2);
        Anm2Clip body = Anm2Reader.Read(result.BodyAnm2!, "body.anm2");
        Anm2Clip mimic = Anm2Reader.Read(result.MimicAnm2!, "mimic.anm2");
        ImmutableArray<Anm2Frame> bodyFrames =
            Anm2SemanticDecoder.DecodeAllFrames(body);
        ImmutableArray<Anm2Frame> mimicFrames =
            Anm2SemanticDecoder.DecodeAllFrames(mimic);

        Assert.Equal(0, bodyFrames[0].Tracks[0].TranslationX, 5);
        Assert.Equal(2, bodyFrames[1].Tracks[0].TranslationX, 5);
        Assert.Equal(0.25, mimicFrames[0].Tracks[0].TranslationX, 5);
        Assert.Equal(0.75, mimicFrames[1].Tracks[0].TranslationX, 5);
    }

    [Fact]
    public void RefusesMimicOutputWithoutAnimatedMimicChannels()
    {
        RigDefinition rig = CreateRig();
        var clip = new AnimationClip(
            "body_only",
            new FrameRate(30, 1),
            1,
            [
                new TransformTrack(
                    0,
                    [new TransformKeyframe(0, TransformTRS.Identity)]),
            ]);
        var exporter = new Dl1AnimationExporter(
            new Anm2EvaluationAdapter(new AnimationEvaluator()));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => exporter.Export(
                new Dl1AnimationExportRequest
                {
                    Evaluation = new EvaluationRequest(
                        rig,
                        rig,
                        clip,
                        0,
                        PreviewProfile.RawAuthoring),
                    Parts = Dl1AnimationExportParts.Mimic,
                }));

        Assert.Contains("no exportable DL1 mimic", exception.Message);
    }

    [Fact]
    public void RetainedFacialSourceClipGeneratesReviewedMimicOutput()
    {
        RigDefinition rig = CreateRig();
        FrameRate rate = new(30, 1);
        var body = new AnimationClip(
            "body",
            rate,
            3,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            TransformTRS.Identity),
                        new TransformKeyframe(
                            2,
                            TransformTRS.Identity),
                    ]),
            ]);
        var facialSource = new AnimationClip(
            "retained-facial-fbx",
            rate,
            3,
            scalarTracks:
            [
                new ScalarTrack(
                    "FbxSmile",
                    [
                        new ScalarKeyframe(0, 0.1),
                        new ScalarKeyframe(1, 0.8),
                        new ScalarKeyframe(2, 0.3),
                    ]),
            ]);
        AnimationClip synchronized =
            AnimationClipSynchronization.Synchronize(
                body,
                facialSource);
        ImmutableArray<MorphChannelBinding> bindings =
            ProjectMorphBindingResolver.Resolve(
                [
                    new ProjectMorphBinding
                    {
                        SourceChannel = "FbxSmile",
                        TargetMorph = "Smile",
                        TargetDescriptorHash = 0x87654321,
                        IsReviewed = true,
                        IsLocked = true,
                    },
                ],
                rig,
                ProjectMorphBindingResolutionMode.Export);
        var exporter = new Dl1AnimationExporter(
            new Anm2EvaluationAdapter(
                new AnimationEvaluator()));

        Dl1AnimationExportResult result = exporter.Export(
            new Dl1AnimationExportRequest
            {
                Evaluation = new EvaluationRequest(
                    rig,
                    rig,
                    synchronized,
                    0,
                    PreviewProfile.RawAuthoring,
                    purpose: EvaluationPurpose.Export,
                    morphBindings: bindings),
                Parts = Dl1AnimationExportParts.Mimic,
            });

        AnimationClip exported =
            Anm2DomainAdapter.ImportMimicExact(
                Anm2Reader.Read(
                    Assert.IsType<byte[]>(
                        result.MimicAnm2),
                    "retained-facial-source"),
                rig,
                rate);
        double[] expected = [0.1, 0.8, 0.3];
        for (var frame = 0; frame < expected.Length; frame++)
        {
            Assert.InRange(
                Math.Abs(
                    expected[frame] -
                    exported.SampleScalars(
                        rate.SecondsForFrame(frame))["Smile"]),
                0,
                0.0001);
        }
    }

    private static RigDefinition CreateRig() =>
        new(
            "test",
            "Test",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity,
                    descriptorHash: 0x12345678),
            ],
            [
                new MorphChannelDefinition(0, "Smile", 0x87654321),
            ]);
}
