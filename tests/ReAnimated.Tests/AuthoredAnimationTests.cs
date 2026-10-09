using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class AuthoredAnimationTests
{
    [Fact]
    public void OversizedFrameRateEditKeepsTheLastValidPreviewRate()
    {
        var model = Create(Source(), "open", 1, new FrameRate(30, 1));
        var selection = model.Package.Document.AnimationClips.Single(static clip => clip.AuthoredAnimation is not null);
        var item = new ReAnimated.App.ViewModels.CustomModelAnimationClipItemViewModel(selection, model.AnimationClips[selection.Id]);
        item.FrameRateNumerator = int.MaxValue;
        Assert.Equal(30, item.FrameRateNumerator);
        Assert.Equal(new FrameRate(30, 1), item.DecodedClip!.FrameRate);
        Assert.Contains("frame rate", item.DecodeStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FrameRateResamplingRejectsTheAggregateKeyBudgetBeforeAllocation()
    {
        var clip = new AnimationClip("dense", new FrameRate(30, 1), 1000,
            Enumerable.Range(0, 1100).Select(index => new TransformTrack(index,
                [new TransformKeyframe(0, TransformTRS.Identity), new TransformKeyframe(999, TransformTRS.Identity)])));
        Assert.Throws<InvalidOperationException>(() => FbxAnimationTimingAuthoring.WithFrameRate(clip, new FrameRate(31, 1)));
    }
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void NewClipUsesTheRequestedTimingAndStartsAtTheRigBindPose()
    {
        FbxModelAuthoringImportResult source = Source();
        FbxModelAuthoringImportResult authored = Create(source, "open", 1.25, new FrameRate(24, 1));
        CustomModelAnimationClip selection = authored.Package.Document.AnimationClips.Single(clip => clip.DisplayName == "open");
        AnimationClip clip = authored.AnimationClips[selection.Id];

        Assert.Equal(31, clip.FrameCount);
        Assert.Equal(new FrameRate(24, 1), clip.FrameRate);
        Assert.Equal(0, selection.FbxObjectId);
        Assert.True(selection.Included);
        Assert.Equal(source.Package.SourceFbx, authored.Package.SourceFbx);
        SkeletonPose pose = clip.SamplePose(authored.Rig!, 0, PlaybackMode.Clamp);
        foreach (CustomModelBone bone in authored.Package.Document.CreateEffectiveBones())
            Assert.True(pose.LocalMatrices[bone.Index].NearlyEquals(bone.ExactLocalBindMatrix, 1e-9));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task AuthoredHingeKeysSurvivePackageReopenAndAnimationExport()
    {
        FbxModelAuthoringImportResult model = Create(Source(), "open", 1, new FrameRate(30, 1));
        CustomModelAnimationClip clip = model.Package.Document.AnimationClips.Single(selection => selection.DisplayName == "open");
        int boneIndex = model.Package.Document.Bones.Single(static bone => bone.Name == "Child").Index;
        TransformTRS bind = model.Rig!.Bones[boneIndex].LocalBindPose;
        TransformTRS opened = bind with { Rotation = QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.PI / 2) };
        model = SetKey(model, clip.Id, boneIndex, 30, opened);
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "prop.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(model.Package, path);
            FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            SkeletonPose pose = reopened.AnimationClips[clip.Id].SamplePose(reopened.Rig!, 1, PlaybackMode.Clamp);
            Assert.True(pose.LocalTransforms[boneIndex].ToMatrix().NearlyEquals(opened.ToMatrix(), 1e-9));
            FbxDerivedMotionAuthoring.ValidateExport(reopened,
                reopened.Package.Document.AnimationClips.Single(selection => selection.Id == clip.Id));
            CustomModelAnimationLibraryResult output = await CustomModelAnimationLibraryExporter.ExportAsync(new()
            {
                Model = reopened,
                OutputPath = Path.Combine(directory, "animations.rpack"),
            });
            Assert.Contains("open", output.AnimationNames);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void CaptureCreatesANewAuthoredPayloadAndPreservesAllSampledTracks()
    {
        FbxModelAuthoringImportResult model = Create(Source(), "open", 1, new FrameRate(30, 1));
        ImmutableArray<CustomModelAnimationClip> originalSelections = model.Package.Document.AnimationClips;
        ImmutableDictionary<Guid, ImmutableArray<byte>> originalPayloads = model.Package.AuthoredAnimationPayloads;
        ImmutableDictionary<Guid, AnimationClip> originalClips = model.AnimationClips;
        int child = model.Package.Document.CreateEffectiveBones()
            .Single(static bone => bone.Name == "Child").Index;
        TransformTRS bind = model.Rig!.Bones[child].LocalBindPose;
        TransformTRS opened = bind with
        {
            Rotation = QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.PI / 2),
        };
        var sampled = new AnimationClip("project-edited", new FrameRate(24, 1), 25,
            [new TransformTrack(child, [
                new TransformKeyframe(0, bind),
                new TransformKeyframe(24, opened),
            ])],
            [new ScalarTrack("expression.sample", [
                new ScalarKeyframe(0, 0),
                new ScalarKeyframe(24, 0.75),
            ])],
            [new AuxiliaryTransformTrack(0x12345678, [
                new TransformKeyframe(0, TransformTRS.Identity),
                new TransformKeyframe(24, TransformTRS.Identity with
                {
                    Translation = new Vector3D(0.5, 0, 0),
                }),
            ])]);

        FbxModelAuthoringImportResult captured = FbxAuthoredAnimationAuthoring.Capture(
            model, "edited-open", sampled);

        CustomModelAnimationClip added = Assert.Single(captured.Package.Document.AnimationClips,
            static selection => selection.AuthoredAnimation is not null && selection.DisplayName == "edited-open");
        Assert.NotEqual(Guid.Empty, added.Id);
        Assert.True(added.Included);
        Assert.Equal(sampled.FrameRate, added.FrameRate);
        Assert.Equal(sampled.FrameCount, added.FrameCount);
        Assert.True(originalSelections.AsSpan().SequenceEqual(captured.Package.Document.AnimationClips
            .Where(selection => selection.Id != added.Id).ToImmutableArray().AsSpan()));
        Assert.Equal(originalPayloads.Keys.Order(), captured.Package.AuthoredAnimationPayloads.Keys
            .Where(id => id != added.Id).Order());
        Assert.All(originalPayloads, pair => Assert.True(pair.Value.AsSpan().SequenceEqual(
            captured.Package.AuthoredAnimationPayloads[pair.Key].AsSpan())));
        Assert.All(originalClips, pair => Assert.Same(pair.Value, captured.AnimationClips[pair.Key]));

        AnimationClip capturedClip = captured.AnimationClips[added.Id];
        Assert.True(capturedClip.TransformTracks.Single().Keyframes[^1].Value.ToMatrix()
            .NearlyEquals(opened.ToMatrix(), 1e-9));
        Assert.Equal(0.75, Assert.Single(capturedClip.ScalarTracks).Keyframes[^1].Value);
        Assert.Equal(new Vector3D(0.5, 0, 0),
            Assert.Single(capturedClip.AuxiliaryTransformTracks).Keyframes[^1].Value.Translation);
        ImmutableArray<byte> payload = captured.Package.AuthoredAnimationPayloads[added.Id];
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "captured.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(captured.Package, path);
            FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
                CustomModelPackageSerializer.Load(path));
            AnimationClip roundTrip = reopened.AnimationClips[added.Id];
            Assert.True(roundTrip.TransformTracks.Single().Keyframes[^1].Value.ToMatrix()
                .NearlyEquals(opened.ToMatrix(), 1e-9));
            Assert.Equal(0.75, Assert.Single(roundTrip.ScalarTracks).Keyframes[^1].Value);
            Assert.Equal(new Vector3D(0.5, 0, 0),
                Assert.Single(roundTrip.AuxiliaryTransformTracks).Keyframes[^1].Value.Translation);
            Assert.True(payload.AsSpan().SequenceEqual(reopened.Package.AuthoredAnimationPayloads[added.Id].AsSpan()));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void CaptureRejectsEmptyOutOfRangeAffineAndOversizedSamples()
    {
        FbxModelAuthoringImportResult model = Source();
        Assert.Throws<ArgumentException>(() => FbxAuthoredAnimationAuthoring.Capture(
            model, "empty", new AnimationClip("empty", new FrameRate(30, 1), 2)));

        var invalidRate = new AnimationClip("invalid-rate", default, 2,
            [new TransformTrack(0, [new TransformKeyframe(0, TransformTRS.Identity)])]);
        Assert.Throws<ArgumentException>(() => FbxAuthoredAnimationAuthoring.Capture(
            model, "invalid-rate", invalidRate));

        int count = model.Package.Document.CreateEffectiveBones().Length;
        var outsideRig = new AnimationClip("bad-index", new FrameRate(30, 1), 2,
            [new TransformTrack(count, [new TransformKeyframe(0, TransformTRS.Identity)])]);
        Assert.Throws<ArgumentException>(() => FbxAuthoredAnimationAuthoring.Capture(model, "bad-index", outsideRig));

        RigDefinition mismatchedRig = new(
            model.Rig!.Id,
            model.Rig.DisplayName,
            model.Rig.Bones.Select(bone => new BoneDefinition(
                bone.Index,
                bone.Index == 0 ? bone.Name + " changed" : bone.Name,
                bone.ParentIndex,
                bone.LocalBindPose,
                bone.Kind,
                bone.RequiredForExport,
                bone.DescriptorHash,
                bone.SemanticRole)));
        var mismatchedModel = model with { Rig = mismatchedRig };
        Assert.Throws<ArgumentException>(() => FbxAuthoredAnimationAuthoring.Capture(
            mismatchedModel, "wrong-runtime-rig", new AnimationClip("sample", new FrameRate(30, 1), 2,
                [new TransformTrack(0, [new TransformKeyframe(0, TransformTRS.Identity)])])));

        var tooManyFrames = new AnimationClip("long", new FrameRate(30, 1), 65_536,
            [new TransformTrack(0, [new TransformKeyframe(0, TransformTRS.Identity)])]);
        Assert.Throws<ArgumentException>(() => FbxAuthoredAnimationAuthoring.Capture(model, "long", tooManyFrames));

        var singular = new AnimationClip("singular", new FrameRate(30, 1), 2,
            [new TransformTrack(0, [new TransformKeyframe(0, TransformTRS.Identity with
            {
                Scale = new Vector3D(0, 1, 1),
            })])]);
        Assert.Throws<ArgumentException>(() => FbxAuthoredAnimationAuthoring.Capture(model, "singular", singular));

        CustomModelBone bone = model.Package.Document.Bones[0];
        TransformMatrix affine = bone.ExactLocalBindMatrix with { M12 = bone.ExactLocalBindMatrix.M12 + 0.3 };
        var affineDocument = model.Package.Document with
        {
            Bones = model.Package.Document.Bones.SetItem(0, bone with { ExactLocalBindMatrix = affine }),
        };
        var affineModel = model with { Package = model.Package with { Document = affineDocument } };
        Assert.Throws<InvalidOperationException>(() => FbxAuthoredAnimationAuthoring.Capture(
            affineModel, "affine", new AnimationClip("affine", new FrameRate(30, 1), 2,
                [new TransformTrack(0, [new TransformKeyframe(0, TransformTRS.Identity)])])));

        ImmutableArray<TransformKeyframe> auxKeys = Enumerable.Range(0, 1001)
            .Select(index => new TransformKeyframe(index, TransformTRS.Identity))
            .ToImmutableArray();
        var dense = new AnimationClip("dense", new FrameRate(30, 1), 1001,
            auxiliaryTransformTracks: Enumerable.Range(0, 1000)
                .Select(index => new AuxiliaryTransformTrack((uint)index, auxKeys)));
        Assert.Throws<InvalidOperationException>(() => FbxAuthoredAnimationAuthoring.Capture(model, "dense", dense));
    }

    private static FbxModelAuthoringImportResult Source() => FbxModelAuthoringImporter.Import(
        BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic-prop.fbx");

    [Fact]
    public void CreatingKeysRefusesAnAffineBindThatCannotBeRepresentedByTrs()
    {
        var source = Source();
        var bone = source.Package.Document.Bones[0];
        var affine = bone.ExactLocalBindMatrix with { M12 = bone.ExactLocalBindMatrix.M12 + 0.3 };
        var document = source.Package.Document with
        {
            Bones = source.Package.Document.Bones.SetItem(0, bone with { ExactLocalBindMatrix = affine }),
        };
        source = source with { Package = source.Package with { Document = document } };
        Assert.Throws<InvalidOperationException>(() => Create(source, "open", 1, new FrameRate(30, 1)));
    }

    [Fact]
    public void ReimportRetainsAuthoredClipsAndPayloads()
    {
        var model = Create(Source(), "open", 1, new FrameRate(30, 1));
        var authored = model.Package.Document.AnimationClips.Single(static clip => clip.AuthoredAnimation is not null);
        var replacement = FbxModelAuthoringImporter.PreviewReimport(model.Package,
            model.Package.SourceFbx.AsSpan(), "generic-prop.fbx").Replacement;
        Assert.Contains(replacement.Package.Document.AnimationClips, clip => clip.Id == authored.Id);
        Assert.True(model.Package.AuthoredAnimationPayloads[authored.Id].AsSpan()
            .SequenceEqual(replacement.Package.AuthoredAnimationPayloads[authored.Id].AsSpan()));
        Assert.True(replacement.AnimationClips.ContainsKey(authored.Id));
    }

    [Fact]
    public void AuthoredMotionCannotExportThroughASuppressedComponentPolicy()
    {
        var source = Source();
        var document = source.Package.Document;
        source = source with { Package = source.Package with { Document = document with
        {
            RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig),
        } } };
        var model = Create(source, "open", 1, new FrameRate(30, 1));
        var clip = model.Package.Document.AnimationClips.Single(static clip => clip.AuthoredAnimation is not null);
        int child = model.Package.Document.Bones.Single(static bone => bone.Name == "Child").Index;
        var bind = model.Rig!.Bones[child].LocalBindPose;
        model = SetKey(model, clip.Id, child, 30,
            bind with { Rotation = QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.PI / 2) });
        var session = model.Package.Document.RiggingSession!;
        document = model.Package.Document with { RiggingSession = session with { Recipe = session.Recipe with
        {
            ComponentPolicies = [new AnimationComponentPolicy
            {
                EntityId = RiggingSessions.ObserveSourceHierarchy(model.Package.Document)[child].EntityId,
                EmittedMask = RigAnimationComponents.None,
            }],
        } } };
        var blocked = model with { Package = model.Package with { Document = document } };
        Assert.Throws<InvalidOperationException>(() => FbxDerivedMotionAuthoring.ValidateExport(blocked, clip));
    }

    [Fact]
    public void PackageRejectsMissingTamperedAndOrphanAuthoredPayloads()
    {
        var model = Create(Source(), "open", 1, new FrameRate(30, 1));
        var clip = model.Package.Document.AnimationClips.Single(static clip => clip.AuthoredAnimation is not null);
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.ValidateAuthoredAnimationPayloads(
            model.Package with { AuthoredAnimationPayloads = ImmutableDictionary<Guid, ImmutableArray<byte>>.Empty }));
        var bytes = model.Package.AuthoredAnimationPayloads[clip.Id].ToArray();
        bytes[0] ^= 1;
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.ValidateAuthoredAnimationPayloads(
            model.Package with { AuthoredAnimationPayloads = model.Package.AuthoredAnimationPayloads.SetItem(clip.Id, [.. bytes]) }));
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.ValidateAuthoredAnimationPayloads(
            model.Package with { Document = model.Package.Document with
            { AnimationClips = model.Package.Document.AnimationClips.Remove(clip) } }));
    }

    [Fact]
    public void ChangedEffectiveBindCannotReuseAuthoredMotion()
    {
        var model = Create(Source(), "open", 1, new FrameRate(30, 1));
        var clip = model.Package.Document.AnimationClips.Single(static clip => clip.AuthoredAnimation is not null);
        var bone = model.Package.Document.Bones[0];
        var transform = bone.LocalBindTransform with { Translation = bone.LocalBindTransform.Translation + Vector3D.UnitX };
        var changed = model with { Package = model.Package with { Document = model.Package.Document with
        {
            Bones = model.Package.Document.Bones.SetItem(0, bone with
            { LocalBindTransform = transform, ExactLocalBindMatrix = transform.ToMatrix() }),
        } } };
        Assert.Throws<InvalidOperationException>(() => FbxDerivedMotionAuthoring.ValidateExport(changed, clip));
        Assert.Throws<InvalidOperationException>(() => SetKey(changed, clip.Id, 0, 1, transform));
    }

    private static FbxModelAuthoringImportResult Create(FbxModelAuthoringImportResult model,
        string name, double duration, FrameRate rate) => FbxAuthoredAnimationAuthoring.Create(model, name, duration, rate);

    private static FbxModelAuthoringImportResult SetKey(FbxModelAuthoringImportResult model,
        Guid clipId, int boneIndex, long frame, TransformTRS value) =>
        FbxAuthoredAnimationAuthoring.SetKey(model, clipId, boneIndex, frame, value);
}
