using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class ConformanceHelperPreservationTests
{
    [Fact]
    public void PreparedParentCameraKeepsItsApprovedFrameThroughConformance()
    {
        var (source, _) = Fixture(false, true); var doc = source.Package.Document; var session = doc.RiggingSession!;
        int parent = doc.AuthoredHelpers[0].ParentNodeIndex; Guid parentId = RiggingSessions.ObserveSourceHierarchy(doc)[parent].EntityId;
        var frame = source.Rig!.CreateBindPose().GlobalMatrices[parent] * TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, .4));
        doc = doc with { RiggingSession = session with { Recipe = session.Recipe with { FramePolicies =
            [new() { EntityId = parentId, FramePolicy = RigFramePolicy.Manual, SolvedGlobalFrame = frame }] } } };
        source = source with { Package = source.Package with { Document = doc } };
        source = FbxCameraHelperAuthoring.Preview(source, doc.AuthoredHelpers[0].Id, TransformTRS.Identity).Candidate;
        var before = Dl1CustomModelRigPreparer.Prepare(source).Contract.Nodes.Single(n => n.Name == "authored_view").GlobalBindMatrix;
        var template = Dl1RigConformanceApplierTests.CreateTemplate(); var mapping = RigCorrespondenceSolver.Solve(template, source.Rig!);
        var fit = RigConformanceSolver.Solve(template, source.Rig!, mapping, RigLandmarkSolver.Solve(template, source.Rig!, mapping));
        var result = Dl1RigConformanceApplier.Apply(source, fit);
        var actual = Dl1CustomModelRigPreparer.Prepare(result).Contract.Nodes.Single(n => n.Name == "authored_view").GlobalBindMatrix;
        var expected = fit.RestPoseTransfer.SkinningTransforms[source.Rig!.GetBoneIndex("authored_view")] * before;
        Assert.True(expected.NearlyEquals(actual, 1e-6));
        Assert.True(result.Package.Document.RiggingSession!.Recipe.Helpers.Single(h => h.EntityId == doc.AuthoredHelpers[0].Id).FollowPreparedParent);
    }

    [Fact]
    public void RealFbxAuthoredLayerReopensWithRenamedBaseIdentitiesAndHelperBranches()
    {
        var source = StructuralHelperAuthoringTests.Source(); var doc = source.Package.Document;
        var template = CameraTemplateCreationTests.Template();
        var old = doc.CreateEffectiveBones(); var oldGlobals = source.Rig!.CreateBindPose().GlobalMatrices;
        var shift = TransformMatrix.CreateTranslation(new(.2, 0, 0));
        var rows = old.Select(b => new RigConformedBone
        {
            Index = b.Index, Name = b.Index < doc.Bones.Length ? "target_" + b.Name : b.Name, ParentIndex = b.ParentIndex,
            Kind = b.Kind, IsDeform = b.IsWeighted, Disposition = RigBoneDisposition.Extra, SourceBoneIndex = b.Index, TemplateIndex = -1,
            Position = (shift * oldGlobals[b.Index]).Translation, Orientation = oldGlobals[b.Index], OffsetFromSourceJoint = .2, SegmentRatio = 1,
        });
        var fit = new RigConformanceResult(template, new() { UniformScale = 1, PelvisAnchor = Vector3D.Zero, Samples = [], RegionFits = [],
            ProportionResidual = 0, WorstSampleDeviation = 0, Evidence = "Synthetic translated control" }, 0, rows, [], new()
            { PosedGlobals = oldGlobals.Select(g => shift * g).ToImmutableArray(), SkinningTransforms = old.Select(_ => shift).ToImmutableArray(), MaximumJointResidual = 0 });
        var settings = new CustomModelRigConformance { TemplateId = template.TemplateId, TemplateProfileName = template.ProfileName,
            TemplateSourceResourceName = template.SourceResourceName, TemplateFingerprint = template.SourceFingerprint, SourceFbxSha256 = doc.Source.ContentSha256 };
        var result = FbxAuthoredModelLayer.Capture(Dl1RigConformanceApplier.Apply(source, fit, settings));
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "conformed.dlrmodel"); CustomModelPackageSerializer.SaveAtomic(result.Package, path);
            var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.Equal(result.Package.Document.RigSignature, reopened.Package.Document.RigSignature);
            Assert.Equal<CustomModelAuthoredHelper>(result.Package.Document.AuthoredHelpers, reopened.Package.Document.AuthoredHelpers);
            Assert.Equal(doc.Bones[0].FbxObjectId, reopened.Package.Document.Bones[0].FbxObjectId);
            Assert.Equal("target_Root", reopened.Package.Document.Bones[0].Name);
            Assert.Equal(result.AnimationClips.Count, reopened.AnimationClips.Count);
            Assert.Equal<RigParentObservation>(RiggingSessions.ObserveSourceHierarchy(result.Package.Document), RiggingSessions.ObserveSourceHierarchy(reopened.Package.Document));
            _ = Dl1CustomModelRigPreparer.Prepare(reopened);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    internal static (FbxModelAuthoringImportResult Model, RigConformanceResult Fit) Fixture(bool dropExtras, bool studio)
    {
        var (model, _) = Dl1RigConformanceApplierTests.BuildModel(dropExtras);
        int parent = model.Rig!.GetBoneIndex("CC_Base_L_ForearmTwist01");
        var doc = CustomModelHelperAuthoring.DuplicateAsHelper(model.Package.Document, parent, CustomModelAuthoredHelperKind.Camera, "authored_view");
        var affine = new TransformMatrix(1,.2,0,.05, 0,1.2,0,.03, 0,0,.8,.04, 0,0,0,1);
        doc = CustomModelHelperAuthoring.SetLocalTransform(doc, doc.AuthoredHelpers[0].Id,
            FbxCoreAnimationAdapter.ProjectAffineToTrs(affine, "authored_view", "test"), affine);
        doc = CustomModelHelperAuthoring.DuplicateAsHelper(doc, doc.Bones.Length, CustomModelAuthoredHelperKind.Helper, "authored_tip");
        doc = CustomModelHelperAuthoring.SetLocalTransform(doc, doc.AuthoredHelpers[1].Id, new(new(.02, .01, 0), QuaternionD.Identity, Vector3D.One));
        doc = CustomModelHelperAuthoring.SelectPreviewCamera(doc, "authored_view");
        if (studio) doc = doc with { RiggingSession = RiggingSessions.Create(doc, RigStudioEntryPath.AdaptExistingRig) };
        model = model with { Package = model.Package with { Document = doc }, Rig = doc.CreateRigDefinition() };
        var template = Dl1RigConformanceApplierTests.CreateTemplate();
        var mapping = RigCorrespondenceSolver.Solve(template, model.Rig!, new() { DropExtraBones = dropExtras });
        var landmarks = RigLandmarkSolver.Solve(template, model.Rig!, mapping);
        return (model, RigConformanceSolver.Solve(template, model.Rig!, mapping, landmarks));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HelperBranchesKeepIdentityAffinePlacementAndCurrentPaletteIndices(bool dropExtras, bool studio)
    {
        var (source, fit) = Fixture(dropExtras, studio);
        var result = Dl1RigConformanceApplier.Apply(source, fit);
        Assert.Equal(2, result.Package.Document.AuthoredHelpers.Length);
        var globals = result.Rig!.CreateBindPose().GlobalMatrices;
        var oldGlobals = source.Rig!.CreateBindPose().GlobalMatrices;
        foreach (var helper in source.Package.Document.AuthoredHelpers)
        {
            var retained = result.Package.Document.AuthoredHelpers.Single(h => h.Id == helper.Id);
            int oldIndex = source.Rig.GetBoneIndex(helper.Name), index = result.Rig.GetBoneIndex(retained.Name);
            var expected = fit.RestPoseTransfer.SkinningTransforms[oldIndex] * oldGlobals[oldIndex];
            Assert.True(expected.NearlyEquals(globals[index], 1e-7), helper.Name);
            Assert.Equal(helper.Kind, retained.Kind);
            Assert.DoesNotContain(result.Package.Document.Bones, b => b.Name == retained.Name);
        }
        var tip = result.Package.Document.AuthoredHelpers.Single(h => h.Name == "authored_tip");
        Assert.Equal("authored_view", result.Rig.Bones[tip.ParentNodeIndex].Name);
        Assert.Equal("authored_view", result.Package.Document.Camera.ActivePreviewNodeName);
        Assert.Equal(source.Package.SourceFbx, result.Package.SourceFbx);
        Assert.Equal(source.Package.Document.Materials, result.Package.Document.Materials);
        foreach (var surface in result.Surfaces.Where(s => s.IsSkinned))
            for (int slot = 0; slot < surface.PaletteBoneIndices.Length; slot++)
                Assert.True((globals[surface.PaletteBoneIndices[slot]] * surface.InverseBindMatrices[slot]).NearlyEquals(TransformMatrix.Identity, 1e-7));
        if (studio)
        {
            var old = RiggingSessions.ObserveSourceHierarchy(source.Package.Document);
            var current = RiggingSessions.ObserveSourceHierarchy(result.Package.Document);
            Assert.Equal(old[source.Rig.GetBoneIndex("CC_Base_L_Hand")].EntityId, current[result.Rig.GetBoneIndex("l_hand")].EntityId);
            Assert.Equal(2, result.Package.Document.AuthoredHelpers.Count(h => current.Any(c => c.EntityId == h.Id)));
        }
        _ = Dl1CustomModelRigPreparer.Prepare(result);
    }
    [Fact]
    public void SourceLessHelperBranchMatchingTemplateNameSharesOneStablePreviewIdentity()
    {
        var (source, fit) = Fixture(false, true);
        RigConformedBone templateCamera = fit.Bones.Single(static bone => bone.Name == "eyecamera");
        int extraIndex = fit.Bones.Length;
        RigConformedBone duplicate = templateCamera with
        {
            Index = extraIndex,
            TemplateIndex = -1,
            SourceBoneIndex = -1,
            Disposition = RigBoneDisposition.Extra,
            Kind = BoneKind.Helper,
            IsDeform = false,
        };
        RigConformedBone descendant = duplicate with
        {
            Index = extraIndex + 1,
            Name = "preview_helper_tip",
            ParentIndex = extraIndex,
            Position = duplicate.Position + new Vector3D(0.03, 0.01, 0),
        };
        RigConformanceResult branchFit = fit.WithBones(fit.Bones.Add(duplicate).Add(descendant));
        TransformMatrix sourceTipGlobal = Dl1RigConformanceApplier.CreateRigDefinition(branchFit)
            .CreateBindPose().GlobalMatrices[extraIndex + 1];

        Dl1RigConformanceApplyResult applied = Dl1RigConformanceApplier.ApplyDetailed(source, branchFit);
        ImmutableArray<CustomModelBone> effective = applied.Model.Package.Document.CreateEffectiveBones();
        CustomModelBone camera = Assert.Single(effective, static bone => bone.Name == "eyecamera");
        CustomModelBone tip = Assert.Single(effective, static bone => bone.Name == "preview_helper_tip");
        Assert.Equal(camera.Index, tip.ParentIndex);
        Assert.Equal(extraIndex, applied.FitToEffective.Length - 2);
        Assert.Equal(applied.FitToEffective[templateCamera.Index], applied.FitToEffective[extraIndex]);
        Assert.True(applied.Model.Rig!.CreateBindPose().GlobalMatrices[tip.Index].NearlyEquals(sourceTipGlobal, 1e-8));
        ImmutableArray<CustomModelAuthoredHelper> sourceHelpers = source.Package.Document.AuthoredHelpers;
        ImmutableArray<CustomModelAuthoredHelper> outputHelpers = applied.Model.Package.Document.AuthoredHelpers;
        Assert.Equal(sourceHelpers.Length, outputHelpers.Length);
        TransformMatrix[] sourceGlobals = source.Rig!.CreateBindPose().GlobalMatrices.ToArray();
        TransformMatrix[] outputGlobals = applied.Model.Rig!.CreateBindPose().GlobalMatrices.ToArray();
        foreach (CustomModelAuthoredHelper original in sourceHelpers)
        {
            CustomModelAuthoredHelper retained = Assert.Single(outputHelpers, helper => helper.Id == original.Id);
            Assert.Equal(original.Name, retained.Name);
            Assert.Equal(original.Kind, retained.Kind);
            int expectedParent = applied.SourceToEffective[original.ParentNodeIndex];
            Assert.Equal(expectedParent, retained.ParentNodeIndex);
            int sourceBone = source.Rig!.GetBoneIndex(original.Name);
            int outputBone = applied.Model.Rig!.GetBoneIndex(retained.Name);
            TransformMatrix expectedGlobal = branchFit.RestPoseTransfer.SkinningTransforms[sourceBone] * sourceGlobals[sourceBone];
            Assert.True(expectedGlobal.NearlyEquals(outputGlobals[outputBone], 1e-7), original.Name);
        }
        Assert.Equal(source.Surfaces[0].Vertices.Select(static vertex => vertex.BoneWeights),
            applied.Model.Surfaces[0].Vertices.Select(static vertex => vertex.BoneWeights));

        _ = ReAnimated.App.Infrastructure.CustomModelPreviewAdapter.CreateSession(applied.Model,
            ReAnimated.App.Infrastructure.CustomModelPreviewMode.Dl1Output);
        Assert.Equal(effective.Length, RiggingSessions.ObserveSourceHierarchy(applied.Model.Package.Document).Length);
    }

    [Fact]
    public void WeightedAuthoredHelperIsNotFoldedWhenSourceExtrasAreDropped()
    {
        var (source, fit) = Fixture(true, false);
        int oldHelper = source.Rig!.GetBoneIndex("authored_tip");
        var surface = source.Surfaces[0]; int slot = surface.PaletteBoneIndices.Length;
        surface = surface with { PaletteBoneIndices = surface.PaletteBoneIndices.Add(oldHelper),
            InverseBindMatrices = surface.InverseBindMatrices.Add(source.Rig.CreateBindPose().GlobalMatrices[oldHelper].InvertedAffine()),
            Vertices = surface.Vertices.SetItem(0, surface.Vertices[0] with { BoneIndices = [slot], BoneWeights = [1] }) };
        var model = source with { Surfaces = source.Surfaces.SetItem(0, surface) };
        var result = Dl1RigConformanceApplier.Apply(model, fit);
        var actual = result.Surfaces[0]; var vertex = actual.Vertices[0];
        Assert.Equal("authored_tip", result.Rig!.Bones[actual.PaletteBoneIndices[vertex.BoneIndices[0]]].Name);
        Assert.Equal(1, Assert.Single(vertex.BoneWeights));
    }

    [Fact]
    public void TrackOwnershipFollowsSourceIdentityAndDroppedTracksRemainRecoverable()
    {
        var (source, fit) = Fixture(true, true);
        int hand = source.Rig!.GetBoneIndex("CC_Base_L_Hand"), dropped = source.Rig.GetBoneIndex("CC_Base_L_ForearmTwist01");
        var keys = new[] { new TransformKeyframe(0, TransformTRS.Identity), new TransformKeyframe(1, new(new(.1, 0, 0), QuaternionD.Identity, Vector3D.One)) };
        var keep = new AnimationClip("retained", new(30, 1), 2, [new TransformTrack(hand, keys)]);
        var unavailable = new AnimationClip("dropped", new(30, 1), 2, [new TransformTrack(dropped, keys)]);
        Guid keepId = Guid.NewGuid(), dropId = Guid.NewGuid();
        var original = ImmutableDictionary<Guid, AnimationClip>.Empty.Add(keepId, keep).Add(dropId, unavailable);
        source = source with { AnimationClips = original };
        var result = Dl1RigConformanceApplier.Apply(source, fit);
        Assert.Equal("l_hand", result.Rig!.Bones[result.AnimationClips[keepId].TransformTracks[0].BoneIndex].Name);
        Assert.Equal<TransformKeyframe>(keep.TransformTracks[0].Keyframes, result.AnimationClips[keepId].TransformTracks[0].Keyframes);
        Assert.False(result.AnimationClips.ContainsKey(dropId)); Assert.Equal(2, source.AnimationClips.Count);
        Assert.Contains(result.Package.Document.Diagnostics, d => d.Message.Contains("embedded source", StringComparison.Ordinal));
        var replayed = FbxAnimationTrackReindexer.ReindexByIdentity(original, source.Package.Document.CreateEffectiveBones(), result.Package.Document.CreateEffectiveBones());
        Assert.Equal(result.AnimationClips[keepId].TransformTracks[0].BoneIndex, replayed[keepId].TransformTracks[0].BoneIndex);
        Assert.False(replayed.ContainsKey(dropId));
    }
}
