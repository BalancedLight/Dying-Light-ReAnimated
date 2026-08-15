using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.App.Infrastructure;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class DirectRigBindingTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Retargeting")]
    public void ExactRuntimeIdentityNeedsNoDirectRowsOrRetargetMap()
    {
        RigDefinition rig = CreateSourceRig();
        AnimationClip clip = CreateClip();

        DirectRigCompatibilityResult result =
            DirectRigBindingAnalyzer.Analyze(rig, rig, clip);

        Assert.Equal(DirectRigCompatibilityKind.ExactDirect, result.Kind);
        Assert.Null(result.Binding);
        Assert.True(result.IsDirect);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Retargeting")]
    public void ReorderedIndexesAndExtraUnanimatedNodesUseCompatibleDirectPlayback()
    {
        RigDefinition source = CreateSourceRig();
        RigDefinition target = CreateCompatibleTargetRig();
        AnimationClip clip = CreateClip();

        DirectRigCompatibilityResult result =
            DirectRigBindingAnalyzer.Analyze(source, target, clip);
        DirectRigBinding binding = Assert.IsType<DirectRigBinding>(
            result.Binding);
        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                source,
                target,
                clip,
                1.0,
                PreviewProfile.RawAuthoring,
                purpose: EvaluationPurpose.Export,
                directRigBinding: binding));

        Assert.Equal(
            DirectRigCompatibilityKind.CompatibleDirect,
            result.Kind);
        Assert.Equal([0, 1], binding.Rows
            .Select(static row => row.SourceBoneIndex));
        Assert.Equal([0, 2], binding.Rows
            .Select(static row => row.TargetBoneIndex));
        Assert.Equal(
            new Vector3D(5.0, 0.0, 0.0),
            frame.AuthoredPose.LocalTransforms[1].Translation);
        Assert.Equal(
            new Vector3D(2.0, 1.0, 0.0),
            frame.AuthoredPose.LocalTransforms[2].Translation);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Retargeting")]
    public void AmbiguousIdentityFallsBackToRetargetSuggestions()
    {
        RigDefinition source = CreateSourceRig();
        RigDefinition target = new(
            "target-ambiguous",
            "Target ambiguous",
            [
                Bone(0, "root", -1, TransformTRS.Identity, 0x1000u,
                    BoneKind.Root),
                Bone(1, "arm", 0, ArmBind, 0x2000u),
                Bone(2, "arm", 0, ArmBind, 0x2000u),
            ]);

        DirectRigCompatibilityResult result =
            DirectRigBindingAnalyzer.Analyze(
                source,
                target,
                CreateClip());

        Assert.Equal(DirectRigCompatibilityKind.Retarget, result.Kind);
        Assert.Null(result.Binding);
        Assert.Contains(
            result.Diagnostics,
            static message => message.Contains(
                "no unique exact target identity",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Retargeting")]
    public void ParentTopologyOrBindBasisDifferenceRequiresRetargeting()
    {
        RigDefinition source = CreateSourceRig();
        RigDefinition wrongParent = new(
            "target-parent",
            "Target parent",
            [
                Bone(0, "root", -1, TransformTRS.Identity, 0x1000u,
                    BoneKind.Root),
                Bone(1, "middle", 0, TransformTRS.Identity, 0x3000u),
                Bone(2, "arm", 1, ArmBind, 0x2000u),
            ]);
        RigDefinition wrongBind = new(
            "target-bind",
            "Target bind",
            [
                Bone(0, "root", -1, TransformTRS.Identity, 0x1000u,
                    BoneKind.Root),
                Bone(
                    1,
                    "arm",
                    0,
                    new TransformTRS(
                        new Vector3D(0.0, 1.001, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One),
                    0x2000u),
            ]);

        DirectRigCompatibilityResult topology =
            DirectRigBindingAnalyzer.Analyze(
                source,
                wrongParent,
                CreateClip());
        DirectRigCompatibilityResult bind =
            DirectRigBindingAnalyzer.Analyze(
                source,
                wrongBind,
                CreateClip());

        Assert.Equal(DirectRigCompatibilityKind.Retarget, topology.Kind);
        Assert.Equal(DirectRigCompatibilityKind.Retarget, bind.Kind);
        Assert.Contains(
            topology.Diagnostics,
            static message => message.Contains(
                "parent topology",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            bind.Diagnostics,
            static message => message.Contains(
                "Bind basis",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Evaluation")]
    public void EvaluationRejectsRetargetMapAndDirectBindingTogether()
    {
        RigDefinition source = CreateSourceRig();
        RigDefinition target = CreateCompatibleTargetRig();
        AnimationClip clip = CreateClip();
        DirectRigBinding binding =
            DirectRigBindingAnalyzer.Analyze(source, target, clip)
                .Binding!;
        RetargetMap retarget = RetargetMapBuilder.CreateSuggested(
            source,
            target);

        Assert.Throws<ArgumentException>(() => new EvaluationRequest(
            source,
            target,
            clip,
            0.0,
            PreviewProfile.RawAuthoring,
            retargetMap: retarget,
            directRigBinding: binding));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Evaluation")]
    public void CompatibleDirectUsesOneBindingPathForPreviewAnm2AndFbx()
    {
        RigDefinition source = CreateSourceRig();
        RigDefinition target = CreateCompatibleTargetRig();
        AnimationClip clip = CreateClip();
        DirectRigBinding binding = Assert.IsType<DirectRigBinding>(
            DirectRigBindingAnalyzer.Analyze(source, target, clip).Binding);
        var template = new EvaluationRequest(
            source,
            target,
            clip,
            0.0,
            PreviewProfile.RawAuthoring,
            playbackMode: PlaybackMode.Clamp,
            purpose: EvaluationPurpose.Export,
            directRigBinding: binding);

        EvaluationFrame preview = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                source,
                target,
                clip,
                1.0,
                PreviewProfile.RawAuthoring,
                directRigBinding: binding));
        Dl1Anm2AuthoringSequence anm2 = new Anm2EvaluationAdapter(
            new AnimationEvaluator()).SampleAuthoredFrames(template);
        ProjectAnimation variant = new()
        {
            Name = "Generic compatible variant",
            SourceAssetId = Guid.NewGuid(),
            TargetRigId = target.Id,
            FrameRate = clip.FrameRate,
            FrameCount = clip.FrameCount,
            BindingMode = ProjectAnimationBindingMode.CompatibleDirect,
            DirectBinding = binding,
            BindingEvidenceFingerprint = binding.EvidenceFingerprint,
            BindingPolicyVersion = binding.Policy,
        };
        BlenderFbxEvaluatedClip fbx =
            BlenderFbxActiveVariantEvaluator.Evaluate(
                variant,
                "generic-source.fbx",
                new string('a', 64),
                template,
                CancellationToken.None);

        TransformTRS expected =
            preview.AuthoredPose.LocalTransforms[2];
        Assert.Equal(
            expected,
            anm2.Frames[1].Tracks.Single(track =>
                track.BoneIndex == 2).LocalTransform);
        Assert.Equal(expected, fbx.Frames[1].BoneLocals[2]);
        Assert.Equal(
            target.Bones[1].LocalBindPose,
            fbx.Frames[1].BoneLocals[1]);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Evaluation")]
    public void CompatibleBodyDoesNotImplicitlyBindFacialChannels()
    {
        MorphChannelDefinition morph = new(
            0,
            "smile",
            0x4000u);
        RigDefinition source = WithMorph(CreateSourceRig(), morph);
        RigDefinition target = WithMorph(
            CreateCompatibleTargetRig(),
            morph);
        AnimationClip clip = new(
            "generic_face",
            new FrameRate(1, 1),
            2,
            CreateClip().TransformTracks,
            [
                new ScalarTrack(
                    "smile",
                    [
                        new ScalarKeyframe(0.0, 0.0),
                        new ScalarKeyframe(1.0, 0.75),
                    ]),
            ]);
        DirectRigBinding binding = Assert.IsType<DirectRigBinding>(
            DirectRigBindingAnalyzer.Analyze(source, target, clip).Binding);

        EvaluationFrame unavailable = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                source,
                target,
                clip,
                1.0,
                PreviewProfile.RawAuthoring,
                directRigBinding: binding));
        EvaluationFrame explicitlyBound = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                source,
                target,
                clip,
                1.0,
                PreviewProfile.RawAuthoring,
                morphBindings:
                [
                    new MorphChannelBinding(
                        "smile",
                        "smile"),
                ],
                directRigBinding: binding));

        Assert.Empty(unavailable.AuthoredMorphWeights);
        Assert.Contains(
            unavailable.Diagnostics,
            static diagnostic => diagnostic.Code ==
                "morph_binding_required");
        Assert.Equal(0.75,
            explicitlyBound.AuthoredMorphWeights["smile"]);
    }

    private static readonly TransformTRS ArmBind = new(
        Vector3D.UnitY,
        QuaternionD.Identity,
        Vector3D.One);

    private static RigDefinition CreateSourceRig() => new(
        "source-asset",
        "Source asset",
        [
            Bone(0, "root", -1, TransformTRS.Identity, 0x1000u,
                BoneKind.Root),
            Bone(1, "arm", 0, ArmBind, 0x2000u),
        ]);

    private static RigDefinition CreateCompatibleTargetRig() => new(
        "target-asset",
        "Target asset",
        [
            Bone(0, "root", -1, TransformTRS.Identity, 0x1000u,
                BoneKind.Root),
            Bone(
                1,
                "unanimated_prop",
                0,
                new TransformTRS(
                    new Vector3D(5.0, 0.0, 0.0),
                    QuaternionD.Identity,
                    Vector3D.One),
                0x3000u,
                BoneKind.Prop),
            Bone(2, "arm", 0, ArmBind, 0x2000u),
        ]);

    private static RigDefinition WithMorph(
        RigDefinition rig,
        MorphChannelDefinition morph) =>
        new(
            rig.Id,
            rig.DisplayName,
            rig.Bones,
            [morph]);

    private static AnimationClip CreateClip() => new(
        "generic_move",
        new FrameRate(1, 1),
        2,
        [
            new TransformTrack(
                1,
                [
                    new TransformKeyframe(0.0, ArmBind),
                    new TransformKeyframe(
                        1.0,
                        new TransformTRS(
                            new Vector3D(2.0, 1.0, 0.0),
                            QuaternionD.Identity,
                            Vector3D.One)),
                ]),
        ]);

    private static BoneDefinition Bone(
        int index,
        string name,
        int parent,
        TransformTRS bind,
        uint descriptor,
        BoneKind kind = BoneKind.Deform) =>
        new(
            index,
            name,
            parent,
            bind,
            kind,
            requiredForExport: true,
            descriptorHash: descriptor);
}
