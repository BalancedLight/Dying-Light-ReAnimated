using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CameraHelperCalibrationTests
{
    internal static FbxModelAuthoringImportResult Source()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "camera-study.fbx");
        var document = model.Package.Document;
        document = document with { RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig) };
        Guid parent = RiggingSessions.ObserveSourceHierarchy(document)[0].EntityId;
        document = RigCameraHelperAuthoring.Create(document, document.RiggingSession.CreateJobToken(), "RefCamera", parent,
            new TransformTRS(new(.1, 1.5, .2), QuaternionD.FromAxisAngle(Vector3D.UnitY, .3), Vector3D.One).ToMatrix());
        Guid reference = document.AuthoredHelpers.Single(h => h.Name == "RefCamera").Id;
        document = RigCameraHelperAuthoring.Create(document, document.RiggingSession!.CreateJobToken(), "EyeCamera", reference,
            new TransformTRS(new(.04, .03, .1), QuaternionD.FromAxisAngle(Vector3D.UnitZ, .2), Vector3D.One).ToMatrix());
        return model with { Package = model.Package with { Document = document }, Rig = document.CreateRigDefinition() };
    }

    [Fact]
    public void IndependentFramesOffsetsAndChannelsSurvivePreparationAndReopen()
    {
        var original = Source();
        var nodes = FbxCameraHelperAuthoring.Inspect(original);
        Assert.Equal(2, nodes.Length);
        var eye = nodes.Single(n => n.Name == "EyeCamera");
        var doc = original.Package.Document;
        Assert.True(RigComponentPolicyAuthoring.TryApply(doc, doc.RiggingSession!.CreateJobToken(),
            [new(eye.EntityId, RigAnimationComponents.Rotation, RigAnimationLod.Lod1, RigComponentOwner.BindInherited, RigComponentOwner.Clip, RigComponentOwner.BindInherited)], out var session));
        original = original with { Package = original.Package with { Document = doc with { RiggingSession = session } } };
        var offset = new TransformTRS(new(.02, -.01, .03), QuaternionD.FromAxisAngle(Vector3D.UnitZ, .4), Vector3D.One);
        var preview = FbxCameraHelperAuthoring.Preview(original, eye.EntityId, offset);
        Assert.True(preview.HasChanges);
        Assert.True(FbxCameraHelperAuthoring.TryApply(original, preview, out var result));
        Assert.Equal<FbxModelSurface>(original.Surfaces, result.Surfaces);
        Assert.Equal(original.Package.SourceFbx, result.Package.SourceFbx);
        Assert.Equal(original.AnimationClips, result.AnimationClips);
        Assert.Equal<CustomModelBone>(original.Package.Document.Bones, result.Package.Document.Bones);
        Assert.Equal<AnimationComponentPolicy>(session.Recipe.ComponentPolicies, result.Package.Document.RiggingSession!.Recipe.ComponentPolicies);
        Assert.Equal(original.Package.Document.AuthoredHelpers[0], result.Package.Document.AuthoredHelpers[0]);
        var finalEye = result.Package.Document.AuthoredHelpers.Single(h => h.Name == "EyeCamera");
        Assert.True(finalEye.ExactLocalMatrix.NearlyEquals(eye.LocalFrame * offset.ToMatrix()));
        var contract = Dl1CustomModelRigPreparer.Prepare(result).Contract;
        var emittedEye = contract.Nodes.Single(n => n.Name == "EyeCamera");
        var emittedRef = contract.Nodes.Single(n => n.Name == "RefCamera");
        Assert.True(emittedEye.GlobalBindMatrix.NearlyEquals(emittedRef.GlobalBindMatrix * finalEye.ExactLocalMatrix, 1e-6));
        Assert.False(emittedEye.GlobalBindMatrix.NearlyEquals(emittedRef.GlobalBindMatrix));
        Assert.Equal(1, preview.Forward.Length, 10);
        Assert.Equal(0, Vector3D.Dot(preview.Forward, preview.Up), 10);
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "cameras.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(result.Package, path);
            var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.All(reopened.Package.Document.RiggingSession!.Recipe.Helpers, h => Assert.True(h.FollowPreparedParent));
            var reopenedContract = Dl1CustomModelRigPreparer.Prepare(reopened).Contract;
            Assert.True(emittedEye.GlobalBindMatrix.NearlyEquals(reopenedContract.Nodes.Single(n => n.Name == "EyeCamera").GlobalBindMatrix, 1e-6));
            Assert.Equal("RefCamera", FbxCameraHelperAuthoring.Inspect(reopened).Single(n => n.Name == "EyeCamera").ParentName);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void NestedHelperUsesTheParentsSolvedFrameRatherThanItsSourceFrame()
    {
        var original = Source();
        var doc = original.Package.Document;
        Guid root = RiggingSessions.ObserveSourceHierarchy(doc)[0].EntityId;
        TransformMatrix moved = TransformMatrix.CreateTranslation(new(3, 4, 5));
        var policy = new RigEntityFramePolicy { EntityId = root, FramePolicy = RigFramePolicy.Manual, SolvedGlobalFrame = moved };
        var session = doc.RiggingSession!;
        doc = doc with { RiggingSession = RiggingSessions.Change(session, session with
            { Recipe = session.Recipe with { FramePolicies = [policy] } }, RiggingEditKind.Helpers) };
        var model = original with { Package = original.Package with { Document = doc } };
        var prepared = Dl1CustomModelRigPreparer.Prepare(model);
        var reference = prepared.Contract.Nodes.Single(n => n.Name == "RefCamera");
        var eye = prepared.Contract.Nodes.Single(n => n.Name == "EyeCamera");
        Assert.True(reference.GlobalBindMatrix.NearlyEquals(moved * doc.AuthoredHelpers[0].ExactLocalMatrix, 1e-6));
        Assert.True(eye.GlobalBindMatrix.NearlyEquals(reference.GlobalBindMatrix * doc.AuthoredHelpers[1].ExactLocalMatrix, 1e-6));
    }

    [Fact]
    public void StaleCalibrationAndLockedFramesCannotApply()
    {
        var model = Source();
        var eye = FbxCameraHelperAuthoring.Inspect(model).Single(n => n.Name == "EyeCamera");
        var preview = FbxCameraHelperAuthoring.Preview(model, eye.EntityId, new(new(.1, 0, 0), QuaternionD.Identity, Vector3D.One));
        Assert.False(FbxCameraHelperAuthoring.TryApply(model with { Package = model.Package }, preview, out var unchanged));
        Assert.NotSame(model, unchanged);
        var doc = model.Package.Document;
        var session = doc.RiggingSession!;
        var locked = session with { Recipe = session.Recipe with { Helpers = session.Recipe.Helpers.Select(h => h.EntityId == eye.EntityId
            ? h with { LockedFields = RigHelperEditFields.Position } : h).ToImmutableArray() } };
        doc = doc with { RiggingSession = locked };
        Assert.Throws<InvalidOperationException>(() => RigCameraHelperAuthoring.Apply(doc, locked.CreateJobToken(), eye.EntityId,
            eye.LocalFrame * TransformMatrix.CreateTranslation(new(.1, 0, 0))));
        Assert.Throws<InvalidOperationException>(() => RigCameraHelperAuthoring.Apply(doc, session.CreateJobToken() with { Generation = Guid.NewGuid() }, eye.EntityId, eye.LocalFrame));
        var sourceBasis = session with { Recipe = session.Recipe with { Helpers = session.Recipe.Helpers.Select(h => h.EntityId == eye.EntityId
            ? h with { FollowPreparedParent = false, LockedFields = RigHelperEditFields.Parent } : h).ToImmutableArray() } };
        var basisLocked = model.Package.Document with { RiggingSession = sourceBasis };
        Assert.Throws<InvalidOperationException>(() => RigCameraHelperAuthoring.Apply(basisLocked, sourceBasis.CreateJobToken(), eye.EntityId, eye.LocalFrame));
        var noOp = FbxCameraHelperAuthoring.Preview(model, eye.EntityId, TransformTRS.Identity);
        Assert.False(noOp.HasChanges);
        Assert.Same(model, noOp.Candidate);
    }
}
