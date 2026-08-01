using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class AnimationPlaybackCorrectnessTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void PartitionerSeparatesBodyMorphAuxiliaryAndUnknownTracks()
    {
        const uint bodyDescriptor = 0x10000001;
        const uint morphDescriptor = 0x10000002;
        const uint unknownDescriptor = 0x10000003;
        RigDefinition rig = CreateRig(
            "partition",
            bodyDescriptor,
            morphDescriptor);
        Anm2Clip source = CreateAnm2(
            [
                bodyDescriptor,
                morphDescriptor,
                Anm2TrackPartitioner.MotionAccumulatorDescriptor,
                unknownDescriptor,
            ],
            frameCount: 3);

        Anm2PartitionedImportResult result =
            Anm2TrackPartitioner.Partition(
                source,
                rig,
                new FrameRate(30, 1));

        Assert.Equal(
            new uint[] { bodyDescriptor },
            result.Partition.BodyDescriptors.ToArray());
        Assert.Equal(
            new uint[] { morphDescriptor },
            result.Partition.MorphDescriptors.ToArray());
        Assert.Equal(
            new uint[]
            {
                Anm2TrackPartitioner.MotionAccumulatorDescriptor,
            },
            result.Partition.AuxiliaryDescriptors.ToArray());
        Assert.Equal(
            new uint[] { unknownDescriptor },
            result.Partition.UnresolvedDescriptors.ToArray());
        Assert.Empty(result.Partition.AmbiguousDescriptors);
        Assert.Single(result.BodyClip.TransformTracks);
        Assert.Single(result.FacialClip.ScalarTracks);
        Assert.Single(result.CombinedClip.AuxiliaryTransformTracks);
        Assert.Equal(
            AnimationSourceRoles.Body |
            AnimationSourceRoles.Facial |
            AnimationSourceRoles.Auxiliary,
            result.Partition.Roles);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void BoneMorphDescriptorCollisionBlocksPlayback()
    {
        const uint collision = 0x20000001;
        RigDefinition rig = CreateRig(
            "collision",
            collision,
            collision);
        Anm2Clip source = CreateAnm2([collision], frameCount: 2);

        Anm2PartitionedImportResult result =
            Anm2TrackPartitioner.Partition(
                source,
                rig,
                new FrameRate(30, 1));

        Assert.True(result.Partition.RequiresReview);
        Assert.Equal(
            new uint[] { collision },
            result.Partition.AmbiguousDescriptors.ToArray());
        Assert.Empty(result.CombinedClip.TransformTracks);
        Assert.Empty(result.CombinedClip.ScalarTracks);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void IndependentFacialTimingUsesNativeCadenceAndNeutralOutsideRange()
    {
        var body = new AnimationClip(
            "body",
            new FrameRate(30, 1),
            5);
        var facial = new AnimationClip(
            "facial",
            new FrameRate(15, 1),
            2,
            scalarTracks:
            [
                new ScalarTrack(
                    "jaw",
                    [
                        new ScalarKeyframe(0, 0.25),
                        new ScalarKeyframe(1, 0.75),
                    ]),
            ]);
        var timing = new FacialClipTiming
        {
            NativeFrameRate = facial.FrameRate,
            SourceStartFrame = 0,
            SourceEndFrame = 1,
            TimelineOffsetFrames = 1,
            OutsideRangeBehavior = FacialOutsideRangeBehavior.Neutral,
        };

        AnimationClip synchronized =
            AnimationClipSynchronization.Synchronize(
                body,
                facial,
                timing);
        ScalarTrack track = Assert.Single(synchronized.ScalarTracks);

        Assert.Equal(0.0, track.Sample(0), 10);
        Assert.Equal(0.25, track.Sample(1), 10);
        Assert.Equal(0.50, track.Sample(2), 10);
        Assert.Equal(0.75, track.Sample(3), 10);
        Assert.Equal(0.0, track.Sample(4), 10);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void FacialOnlyClipKeepsBindPoseAndEvaluatesMorphs()
    {
        RigDefinition rig = CreateRig(
            "facial-only",
            0x30000001,
            0x30000002);
        var clip = new AnimationClip(
            "facial-only",
            new FrameRate(30, 1),
            2,
            scalarTracks:
            [
                new ScalarTrack(
                    "jaw",
                    [
                        new ScalarKeyframe(0, 0),
                        new ScalarKeyframe(1, 0.8),
                    ]),
            ]);
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            retargetMap: null,
            AnimationRootMode.Recorded);

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                clip.FrameRate.SecondsForFrame(1),
                PreviewProfile.RawAuthoring,
                retargetMap: null,
                purpose: EvaluationPurpose.Preview,
                dl1AuthoringPolicy: policy));

        Assert.Equal(
            rig.Bones[0].LocalBindPose,
            frame.AuthoredPose.LocalTransforms[0]);
        Assert.Equal(0.8, frame.AuthoredMorphWeights["jaw"], 10);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void PreviewAccumulationMovesOnlyActorWorldTransform()
    {
        RigDefinition rig = CreateRig(
            "accumulator",
            0x40000001,
            0x40000002);
        var clip = new AnimationClip(
            "moving",
            new FrameRate(1, 1),
            2,
            transformTracks:
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(
                            1,
                            new TransformTRS(
                                new Vector3D(0.5, 0.25, -0.5),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ],
            auxiliaryTransformTracks:
            [
                new AuxiliaryTransformTrack(
                    Anm2TrackPartitioner.MotionAccumulatorDescriptor,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(
                            1,
                            new TransformTRS(
                                new Vector3D(10, 0, 3),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            retargetMap: null,
            AnimationRootMode.Recorded);
        EvaluationRequest baselineRequest = new(
            rig,
            rig,
            clip,
            1,
            PreviewProfile.RawAuthoring,
            retargetMap: null,
            purpose: EvaluationPurpose.Preview,
            dl1AuthoringPolicy: policy);
        var evaluator = new AnimationEvaluator();

        EvaluationFrame baseline = evaluator.Evaluate(baselineRequest);
        EvaluationFrame accumulated = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                1,
                PreviewProfile.RawAuthoring,
                retargetMap: null,
                purpose: EvaluationPurpose.Preview,
                dl1AuthoringPolicy: policy,
                previewMotionAccumulationEnabled: true));

        Assert.Equal(
            baseline.AuthoredPose.LocalTransforms.ToArray(),
            accumulated.AuthoredPose.LocalTransforms.ToArray());
        Assert.Equal(
            baseline.DisplayPose.LocalTransforms.ToArray(),
            accumulated.DisplayPose.LocalTransforms.ToArray());
        Vector3D actorRelativeVertex =
            accumulated.DisplayPose.GlobalMatrices[0]
                .TransformPoint(new Vector3D(1, 0, 0));
        Assert.Equal(
            baseline.DisplayPose.GlobalMatrices[0]
                .TransformPoint(new Vector3D(1, 0, 0)),
            actorRelativeVertex);
        Assert.Equal(
            new Vector3D(11.5, 0.25, 2.5),
            accumulated.ActorWorldTransform.TransformPoint(
                actorRelativeVertex));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Renderer")]
    public void RenderSceneRejectsStaleGeneratedPublication()
    {
        var buffer = new RenderSceneBuffer();
        MeshRenderData newer = CreateTriangle("newer");
        MeshRenderData stale = CreateTriangle("stale");

        buffer.SetScene([newer], null, [], generation: 8);
        buffer.SetScene([stale], null, [], generation: 7);
        RenderFrameSnapshot frame = buffer.Capture(RenderCamera.Default);

        Assert.Equal(8, frame.Generation);
        Assert.Equal("newer", Assert.Single(frame.Meshes).Id);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void WorkspaceLayoutAloneControlsSingleOrDualViewportPresentation()
    {
        Assert.True(MainWindowViewModel.ShouldShowSourceViewport(
            false,
            "Retarget",
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false));
        Assert.False(MainWindowViewModel.ShouldShowSourceViewport(
            true,
            "Animate",
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false));
        Assert.True(MainWindowViewModel.ShouldShowSourceViewport(
            false,
            "FPP",
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false));
        Assert.True(MainWindowViewModel.ShouldShowSourceViewport(
            false,
            "Cutscene",
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Project")]
    public void ProjectRoundTripPreservesActiveClipAndImmutableAnm2Binding()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dlr-playback-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Guid animationAssetId = Guid.NewGuid();
            Guid modelAssetId = Guid.NewGuid();
            Guid animationId = Guid.NewGuid();
            var partition = new Anm2TrackPartition
            {
                BodyDescriptors = [0x50000001],
                Fingerprint = new string('b', 64),
            };
            var project = DlraProject.Create("binding") with
            {
                Assets =
                [
                    new ProjectAssetReference
                    {
                        Id = animationAssetId,
                        Kind = ProjectAssetKind.SourceAnimation,
                        RelativePath = "Sources/test.anm2",
                        ContentSha256 = new string('a', 64),
                    },
                    CreateRetailAsset(modelAssetId),
                ],
                Animations =
                [
                    new ProjectAnimation
                    {
                        Id = animationId,
                        Name = "test",
                        SourceAssetId = animationAssetId,
                        SourceBinding = new ProjectAnimationSourceBinding
                        {
                            Kind = AnimationSourceKind.LocalAnm2,
                            AssetId = animationAssetId,
                            Roles = AnimationSourceRoles.Body,
                            SourceRigSignature = new string('c', 64),
                            RetailSourceModelAssetId = modelAssetId,
                            TimingProvenance =
                                AnimationTimingProvenance.UserSpecified,
                            Partition = partition,
                        },
                        TargetAssetId = modelAssetId,
                        TargetRigId = "rig",
                        SourceRigSignature = new string('c', 64),
                        TargetRigSignature = new string('c', 64),
                        FrameRate = new FrameRate(30, 1),
                        FrameCount = 3,
                        RootMotionMode = Dl1RootMotionMode.Recorded,
                    },
                ],
                ActiveAnimationId = animationId,
            };
            string path = Path.Combine(directory, "test.dlraproj");

            ProjectSerializer.SaveAtomic(project, path);
            DlraProject loaded = ProjectSerializer.Load(path);

            Assert.Equal(animationId, loaded.ActiveAnimationId);
            ProjectAnimation animation = Assert.Single(loaded.Animations);
            Assert.Equal(
                modelAssetId,
                animation.SourceBinding!.RetailSourceModelAssetId);
            Assert.Equal(
                partition.Fingerprint,
                animation.SourceBinding.Partition!.Fingerprint);
            Assert.Equal(Dl1RootMotionMode.Recorded, animation.RootMotionMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RigDefinition CreateRig(
        string id,
        uint boneDescriptor,
        uint morphDescriptor) =>
        new(
            id,
            id,
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: boneDescriptor,
                    semanticRole: "root.skeletal"),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    "jaw",
                    morphDescriptor,
                    "mimic.jaw"),
            ]);

    private static Anm2Clip CreateAnm2(
        ImmutableArray<uint> descriptors,
        int frameCount)
    {
        var frames = ImmutableArray.CreateBuilder<Anm2Frame>(frameCount);
        for (int frame = 0; frame < frameCount; frame++)
        {
            frames.Add(new Anm2Frame(
                descriptors.Select((_, track) =>
                        new Anm2TrackFrame(
                            0,
                            0,
                            0,
                            frame + (track * 0.1f),
                            0,
                            0,
                            1,
                            1,
                            1))
                    .ToImmutableArray()));
        }

        byte[] payload = Anm2PayloadWriter.Build(
            new Anm2Header(
                Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion,
                checked((ushort)frameCount),
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
                    Anm2PackedComponents.TranslationX,
                    descriptors.Length)
                .ToImmutableArray());
        return Anm2Reader.Read(payload, "generated");
    }

    private static MeshRenderData CreateTriangle(string id) =>
        new(
            id,
            new MeshVertex[]
            {
                new MeshVertex(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.Zero,
                    Vector4.Zero),
                new MeshVertex(
                    Vector3.UnitX,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.Zero,
                    Vector4.Zero),
                new MeshVertex(
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.Zero,
                    Vector4.Zero),
            },
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            ReadOnlyMemory<Matrix4x4>.Empty,
            false);

    private static ProjectAssetReference CreateRetailAsset(Guid id) =>
        new()
        {
            Id = id,
            Kind = ProjectAssetKind.RetailGameResource,
            RelativePath = "retail/272/0",
            ResourceId = "mesh/0",
            ContentSha256 = new string('d', 64),
            RetailIdentity = new ProjectRetailAssetIdentity
            {
                InstallFingerprint = "install",
                ProviderId = "provider",
                ProviderPack = "data/common.rpack",
                ResourceType = 272,
                ResourceIndex = 0,
                ResourceName = "model",
                Precedence = 0,
                ContentSha256 = new string('d', 64),
            },
        };
}
