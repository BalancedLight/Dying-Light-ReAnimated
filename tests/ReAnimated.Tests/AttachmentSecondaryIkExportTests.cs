using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class AttachmentSecondaryIkExportTests
{
    [Fact]
    public void SavedProjectRetainsTheDriverAndItsPoleSpace()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var binding = AttachmentSecondaryIkTests.Binding(weight: .6);
            Guid source = Guid.NewGuid();
            var project = DlraProject.Create("secondary-grip") with
            {
                Assets = [
                    new() { Id = source, Kind = ProjectAssetKind.SourceAnimation, RelativePath = "source.fbx" },
                    new() { Id = binding.AssetId, Kind = ProjectAssetKind.RetailGameResource,
                        RelativePath = "retail/272/7", ResourceId = "rpack:272:generic_prop", ContentSha256 = new string('a', 64),
                        RetailIdentity = new() { InstallFingerprint = "test", ProviderId = "test", ProviderPack = "generic.rpack",
                            ResourceType = 272, ResourceIndex = 7, ResourceName = "generic_prop", Precedence = 0,
                            ContentSha256 = new string('a', 64) } },
                ],
                Animations = [new() { Name = "clip", SourceAssetId = source, TargetRigId = "two-arm", FrameCount = 1, Attachments = [binding] }],
            };
            string path = Path.Combine(directory, "grip.dlraproj");
            ProjectSerializer.SaveAtomic(project, path);
            AttachmentBinding restored = ProjectSerializer.Load(path).Animations[0].Attachments[0];
            AttachmentSecondaryIk driver = restored.GripCalibration!.Secondary!.Ik!;
            Assert.Equal("upper", driver.RootBoneName);
            Assert.Equal("joint", driver.JointBoneName);
            Assert.Equal("hand", driver.EndBoneName);
            Assert.Equal(.6, driver.Weight);
            Assert.Equal<double>(binding.GripCalibration!.Secondary!.Ik!.Pole, driver.Pole);
            Assert.True(driver.PoleInPrimaryGripSpace);
            Assert.True(driver.MatchOrientation);
            Assert.Equal(binding.Scope, restored.Scope);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static RigDefinition ExportRig(int? missingDescriptor = null)
    {
        RigDefinition source = AttachmentSecondaryIkTests.Rig();
        return new(source.Id, source.DisplayName, source.Bones.Select(b =>
            new BoneDefinition(b.Index, b.Name, b.ParentIndex, b.LocalBindPose,
                b.Kind, requiredForExport: b.Index != missingDescriptor,
                descriptorHash: b.Index == missingDescriptor ? null : (uint)(100 + b.Index))));
    }

    [Fact]
    public void EncodedHandMotionMatchesAuthoredPoseAndLeavesSourceKeysUntouched()
    {
        RigDefinition rig = ExportRig();
        AttachmentBinding binding = AttachmentSecondaryIkTests.Binding();
        TransformKeyframe[] keys =
        [
            new(0, rig.Bones[1].LocalBindPose),
            new(2, new(new(.3, .2, .1), QuaternionD.FromAxisAngle(Vector3D.UnitY, .2), Vector3D.One)),
        ];
        var clip = new AnimationClip("grip-motion", new(30, 1), 3, [new TransformTrack(1, keys)]);
        var evaluator = new AnimationEvaluator();
        var exporter = new Dl1AnimationExporter(new Anm2EvaluationAdapter(evaluator));
        Dl1AnimationExportResult result = exporter.Export(new()
        {
            Evaluation = new(rig, rig, clip, 0, PreviewProfile.RawAuthoring, attachments: [binding]),
        });
        Anm2DomainImportResult readback = Anm2DomainAdapter.ImportBody(
            Anm2Reader.Read(result.BodyAnm2!, "grip.anm2"), rig, clip.FrameRate);
        Assert.Empty(readback.UnmappedDescriptors);
        for (int frame = 0; frame < clip.FrameCount; frame++)
        {
            double time = clip.FrameRate.SecondsForFrame(frame);
            EvaluationFrame expected = evaluator.Evaluate(new(rig, rig, clip, time,
                PreviewProfile.RawAuthoring, purpose: EvaluationPurpose.Export, attachments: [binding]));
            SkeletonPose actual = readback.Clip.SamplePose(rig, time);
            for (int bone = 0; bone < rig.BoneCount; bone++)
                Assert.True(expected.AuthoredPose.GlobalMatrices[bone].NearlyEquals(actual.GlobalMatrices[bone], 1e-4),
                    $"Readback differs at frame {frame}, bone {bone}.");
            var contact = actual.GlobalMatrices[5] * binding.GripCalibration!.Secondary!.CharacterLocalOffset.ToMatrix();
            Assert.True(contact.NearlyEquals(expected.AuthoredAttachments[0].SecondaryPropWorldFrame!.Value, 1e-4));
        }
        Assert.Equal<TransformKeyframe>(keys, clip.TransformTracks[0].Keyframes);
    }

    [Fact]
    public void RejectedChainCannotSilentlyProduceAnAnimation()
    {
        RigDefinition rig = ExportRig();
        AttachmentBinding valid = AttachmentSecondaryIkTests.Binding();
        AttachmentGripCalibration grip = valid.GripCalibration!;
        var binding = new AttachmentBinding(valid.Id, valid.AssetId, valid.Name, valid.ParentBoneIndex,
            valid.LocalOffset, valid.Scope, valid.ParentBoneName, grip with
            {
                Secondary = grip.Secondary! with { Ik = grip.Secondary.Ik! with { JointBoneName = "stale-name" } },
            });
        var request = new EvaluationRequest(rig, rig, new("still", new(30, 1), 1), 0,
            PreviewProfile.RawAuthoring, attachments: [binding]);
        var exporter = new Dl1AnimationExporter(new Anm2EvaluationAdapter(new AnimationEvaluator()));
        var error = Assert.Throws<InvalidOperationException>(() => exporter.Export(new() { Evaluation = request }));
        Assert.Contains("attachment_secondary_ik_rejected", error.Message, StringComparison.Ordinal);
        Assert.Contains("frame 0", error.Message, StringComparison.Ordinal);
        bool accepted = false;
        Assert.Throws<InvalidOperationException>(() => AnimationEvaluator.EvaluateAuthoredPoseBatch(
            request, [0], (_, _) => accepted = true));
        Assert.False(accepted);
    }

    [Fact]
    public void EvenAnOptionalDrivenNodeNeedsAnExportDescriptor()
    {
        RigDefinition rig = ExportRig(missingDescriptor: 3);
        var adapter = new Anm2EvaluationAdapter(new AnimationEvaluator());
        var request = new EvaluationRequest(rig, rig, new("still", new(30, 1), 1), 0,
            PreviewProfile.RawAuthoring, attachments: [AttachmentSecondaryIkTests.Binding()]);
        var error = Assert.Throws<InvalidOperationException>(() => adapter.SampleAuthoredFrames(request));
        Assert.Contains("without a target ANM2 descriptor", error.Message, StringComparison.Ordinal);
    }
}
