using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class AttachmentSecondaryIkTests
{
    internal static RigDefinition Rig(Vector3D? scale = null) => new("two-arm", "two-arm",
    [new(0,"root",-1,new(Vector3D.Zero,QuaternionD.Identity,scale??Vector3D.One),BoneKind.Root),
     new(1,"primary",0,new(new(.4,.3,0),QuaternionD.Identity,Vector3D.One),BoneKind.Prop),
     new(2,"upper",0,new(new(-.5,0,0),QuaternionD.Identity,Vector3D.One),BoneKind.Deform),
     new(3,"joint",2,new(new(.4,.1,0),QuaternionD.Identity,Vector3D.One),BoneKind.Deform),
     new(4,"hand",3,new(new(.4,-.1,0),QuaternionD.Identity,Vector3D.One),BoneKind.Deform),
     new(5,"contact",4,new(new(.05,0,0),QuaternionD.Identity,Vector3D.One),BoneKind.Helper),
     new(6,"camera",0,new(new(0,1,0),QuaternionD.Identity,Vector3D.One),BoneKind.Camera)]);
    internal static AttachmentBinding Binding(AttachmentScope scope = AttachmentScope.AuthoredExportable, double weight = 1)
    {
        Guid asset = Guid.NewGuid();
        return new(Guid.NewGuid(), asset, "test-prop", 1, TransformTRS.Identity, scope, "primary", new()
        {
            PropAssetId = asset,
            PropContentSha256 = new string('a', 64),
            PrimaryPropFrame = AttachmentGripFrame.FromMatrix(0, "holder", TransformMatrix.Identity),
            Secondary = new()
            {
                CharacterBoneIndex = 5,
                CharacterBoneName = "contact",
                CharacterLocalOffset = new(new(.01, .02, 0), QuaternionD.FromAxisAngle(Vector3D.UnitZ, .1), Vector3D.One),
                PropFrame = AttachmentGripFrame.FromMatrix(1, "support", new TransformTRS(new(-.3, 0, 0), QuaternionD.FromAxisAngle(Vector3D.UnitZ, .3), Vector3D.One).ToMatrix()),
                Ik = new() { RootBoneIndex = 2, RootBoneName = "upper", JointBoneIndex = 3, JointBoneName = "joint", EndBoneIndex = 4, EndBoneName = "hand", Pole = [0, 0, 1], Weight = weight }
            },
        });
    }
    private static AttachmentBinding Change(AttachmentBinding b, AttachmentSecondaryGrip secondary, int? primary = null, string? primaryName = null) =>
        new(b.Id, b.AssetId, b.Name, primary ?? b.ParentBoneIndex, b.LocalOffset, b.Scope, primaryName ?? b.ParentBoneName, b.GripCalibration! with { Secondary = secondary });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MovingPropDrivesTheOtherHandWithoutChangingSourceOrPrimary(double scale)
    {
        var rig = Rig(new(scale, scale, scale)); var binding = Binding();
        var keys = new[] { new TransformKeyframe(0, rig.Bones[1].LocalBindPose), new TransformKeyframe(30, new(new(.3, .2, .1), QuaternionD.FromAxisAngle(Vector3D.UnitY, .2), Vector3D.One)) };
        var clip = new AnimationClip("motion", new(30, 1), 31, [new TransformTrack(1, keys)]);
        foreach (double time in new[] { 0, .5, 1 })
        {
            var frame = new AnimationEvaluator().Evaluate(new(rig, rig, clip, time, PreviewProfile.RawAuthoring, attachments: [binding]));
            var attached = Assert.Single(frame.AuthoredAttachments);
            Assert.InRange(attached.SecondaryPositionError!.Value, 0, 1e-6);
            Assert.True(attached.SecondaryPropWorldFrame!.Value.NearlyEquals(attached.SecondaryCharacterWorldFrame!.Value, 1e-6));
            Assert.True(frame.AuthoredPose.GlobalMatrices[1].NearlyEquals(clip.SamplePose(rig, time).GlobalMatrices[1]));
            Assert.True(frame.AuthoredPose.GlobalMatrices[6].NearlyEquals(clip.SamplePose(rig, time).GlobalMatrices[6]));
            Assert.All(frame.AttachmentIkReports, r => Assert.True(r.Applied, r.Message));
        }
        Assert.Equal<TransformKeyframe>(keys, clip.TransformTracks[0].Keyframes);
    }

    [Fact]
    public void PartialWeightIsNotAppliedTwiceAndBatchMatchesSingleFrame()
    {
        var rig = Rig(); var binding = Binding(weight: .5); var clip = new AnimationClip("still", new(30, 1), 1);
        var request = new EvaluationRequest(rig, rig, clip, 0, PreviewProfile.RawAuthoring, attachments: [binding]);
        var frame = new AnimationEvaluator().Evaluate(request);
        for (int i = 0; i < rig.BoneCount; i++) Assert.True(frame.AuthoredPose.GlobalMatrices[i].NearlyEquals(frame.DisplayPose.GlobalMatrices[i], 1e-10));
        AnimationEvaluator.EvaluateAuthoredPoseBatch(request, [0], (_, pose) =>
        { for (int i = 0; i < rig.BoneCount; i++) Assert.True(pose.GlobalMatrices[i].NearlyEquals(frame.AuthoredPose.GlobalMatrices[i], 1e-10)); });
        Assert.True(frame.AuthoredAttachments[0].SecondaryPositionError > 1e-6);
    }

    [Fact]
    public void PreviewOnlyDriverDoesNotChangeExportPose()
    {
        var rig = Rig(); var binding = Binding(AttachmentScope.PreviewOnly); var clip = new AnimationClip("still", new(30, 1), 1);
        var preview = new AnimationEvaluator().Evaluate(new(rig, rig, clip, 0, PreviewProfile.RawAuthoring, attachments: [binding]));
        Assert.InRange(preview.DisplayAttachments[0].SecondaryPositionError!.Value, 0, 1e-6);
        var export = new AnimationEvaluator().Evaluate(new(rig, rig, clip, 0, PreviewProfile.RawAuthoring, purpose: EvaluationPurpose.Export, attachments: [binding]));
        Assert.Empty(export.AttachmentIkReports);
        for (int i = 0; i < rig.BoneCount; i++) Assert.True(export.AuthoredPose.GlobalMatrices[i].NearlyEquals(rig.CreateBindPose().GlobalMatrices[i]));
    }

    [Fact]
    public void ReorderedChainsFeedbackAndNonuniformScaleAreRejected()
    {
        var rig = Rig(); var original = Binding(); var secondary = original.GripCalibration!.Secondary!;
        var renamed = Change(original, secondary with { Ik = secondary.Ik! with { JointBoneName = "old-joint" } });
        var feedback = Change(original, secondary, 4, "hand");
        foreach (var binding in new[] { renamed, feedback })
        {
            var frame = new AnimationEvaluator().Evaluate(new(rig, rig, new("still", new(30, 1), 1), 0, PreviewProfile.RawAuthoring, attachments: [binding]));
            Assert.Contains(frame.Diagnostics, d => d.Code == "attachment_secondary_ik_rejected");
            Assert.All(frame.AttachmentIkReports, r => Assert.False(r.Applied));
            for (int i = 0; i < rig.BoneCount; i++) Assert.True(frame.AuthoredPose.GlobalMatrices[i].NearlyEquals(rig.CreateBindPose().GlobalMatrices[i]));
        }
        var nonuniform = Rig(new(2, 1, 1));
        var rejected = new AnimationEvaluator().Evaluate(new(nonuniform, nonuniform, new("still", new(30, 1), 1), 0, PreviewProfile.RawAuthoring, attachments: [original]));
        Assert.Contains(rejected.Diagnostics, d => d.Message.Contains("uniform scale", StringComparison.Ordinal));
    }

    [Fact]
    public void UnreachableAndOverlappingDriversAreNotReportedAsClosed()
    {
        var rig = Rig(); var original = Binding(); var secondary = original.GripCalibration!.Secondary!;
        var unreachable = Change(original, secondary with { PropFrame = AttachmentGripFrame.FromMatrix(1, "support", TransformMatrix.CreateTranslation(new(10, 0, 0))) });
        var frame = new AnimationEvaluator().Evaluate(new(rig, rig, new("still", new(30, 1), 1), 0, PreviewProfile.RawAuthoring, attachments: [unreachable]));
        Assert.Contains(frame.AttachmentIkReports, r => r.Clamped && r.Applied);
        Assert.True(frame.AuthoredAttachments[0].SecondaryPositionError > 1);
        var other = Binding();
        var conflict = new AnimationEvaluator().Evaluate(new(rig, rig, new("still", new(30, 1), 1), 0, PreviewProfile.RawAuthoring, attachments: [original, other]));
        Assert.All(conflict.AttachmentIkReports, r => Assert.False(r.Applied));
        Assert.Contains(conflict.Diagnostics, d => d.Message.Contains("overlap", StringComparison.Ordinal));
    }
}
