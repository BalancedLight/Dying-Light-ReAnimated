using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class StructuralHelperAuthoringTests
{
    internal static FbxModelAuthoringImportResult Source()
    {
        var source=CameraTemplateCreationTests.Target();
        var doc=CustomModelHelperAuthoring.DuplicateAsHelper(source.Package.Document,0,CustomModelAuthoredHelperKind.Helper,"normal_marker_2");
        doc=CustomModelHelperAuthoring.SetLocalTransform(doc,doc.AuthoredHelpers[0].Id,new(new(.2,.3,.4),QuaternionD.Identity,Vector3D.One));
        doc=CustomModelHelperAuthoring.DuplicateAsHelper(doc,doc.Bones.Length,CustomModelAuthoredHelperKind.Helper,"unknown_extra");
        return source with{Package=source.Package with{Document=doc},Rig=doc.CreateRigDefinition()};
    }

    [Fact]
    public void InspectorSeparatesActualWeightsFromUnknownDriverSemantics()
    {
        var source=Source();var rows=FbxStructuralHelperAuthoring.Inspect(source);
        Assert.Equal(source.Rig!.BoneCount,rows.Length);
        var helper=rows.Single(r=>r.Name=="normal_marker_2");
        Assert.Equal(0,helper.WeightedCorners);Assert.False(helper.WeightedBranch);Assert.True(helper.CanEditHelper);
        Assert.Contains("unverified",helper.Roles,StringComparison.OrdinalIgnoreCase);
        var weighted=rows.First(r=>r.WeightedCorners>0);
        Assert.True(weighted.WeightedBranch);Assert.False(weighted.CanEditHelper);Assert.True(weighted.CanEditRest);
        Assert.Contains(rows,r=>r.Name=="unknown_extra");
    }

    [Fact]
    public void ProtectionPreservesPreparedFramesAndRejectsImplicitAimOrMotionChanges()
    {
        var source=Source();var row=FbxStructuralHelperAuthoring.Inspect(source).Single(r=>r.Name=="normal_marker_2");
        var before=Dl1CustomModelRigPreparer.Prepare(source);
        var preview=FbxStructuralHelperAuthoring.PreviewProtection(source,row.EntityId,RigHelperEditFields.All);
        Assert.True(FbxStructuralHelperAuthoring.TryApply(source,preview,out var locked));
        var after=Dl1CustomModelRigPreparer.Prepare(locked);
        for(int i=0;i<before.Contract.Nodes.Length;i++)
        {
            Assert.True(before.Contract.Nodes[i].GlobalBindMatrix.NearlyEquals(after.Contract.Nodes[i].GlobalBindMatrix,1e-6));
            Assert.Equal(before.Contract.Nodes[i].Bounds,after.Contract.Nodes[i].Bounds);
        }
        Assert.Same(source.AnimationClips,locked.AnimationClips);
        Assert.Equal<FbxModelSurface>(source.Surfaces,locked.Surfaces);
        Assert.Throws<InvalidOperationException>(()=>FbxStructuralHelperAuthoring.PreviewOffset(locked,row.EntityId,new(new(.1,0,0),QuaternionD.Identity,Vector3D.One)));
        var session=locked.Package.Document.RiggingSession!;
        var bad=session with{Recipe=session.Recipe with{Helpers=session.Recipe.Helpers.Select(h=>h.EntityId==row.EntityId?h with{FramePolicy=RigFramePolicy.GeneratedDeform}:h).ToImmutableArray()}};
        Assert.Throws<InvalidOperationException>(()=>RiggingSessions.Change(session,bad,RiggingEditKind.Helpers));
        var unlocked=FbxStructuralHelperAuthoring.PreviewProtection(locked,row.EntityId,RigHelperEditFields.None).Candidate;
        var moved=FbxStructuralHelperAuthoring.PreviewOffset(unlocked,row.EntityId,new(new(.1,0,0),QuaternionD.Identity,Vector3D.One));
        Assert.True(moved.HasChanges);Assert.Equal(source.Package.SourceFbx,moved.Candidate.Package.SourceFbx);
        Assert.Equal(2,moved.Candidate.Package.Document.AuthoredHelpers.Length);
        Assert.Equal<CustomModelBone>(source.Package.Document.Bones,moved.Candidate.Package.Document.Bones);
    }

    [Fact]
    public void WeightedJointNeedsRestTransactionAndLocksSurviveReopen()
    {
        var source=Source();var row=FbxStructuralHelperAuthoring.Inspect(source).First(r=>r.CanProtect&&r.WeightedCorners>0);
        Assert.Throws<InvalidOperationException>(()=>FbxStructuralHelperAuthoring.PreviewOffset(source,row.EntityId,new(new(.1,0,0),QuaternionD.Identity,Vector3D.One)));
        var locked=FbxStructuralHelperAuthoring.PreviewProtection(source,row.EntityId,RigHelperEditFields.Position|RigHelperEditFields.Orientation).Candidate;
        var globals=source.Rig!.CreateBindPose().GlobalMatrices;
        Assert.Throws<InvalidOperationException>(()=>FbxRestPoseAuthoring.Preview(locked,row.EntityId,TransformMatrix.CreateTranslation(new(.1,0,0))*globals[row.SourceIndex],RigRestDescendantMode.KeepGlobal,RigRestSurfaceMode.PreserveSurface));
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path=Path.Combine(directory,"protected-helpers.dlrmodel");CustomModelPackageSerializer.SaveAtomic(locked.Package,path);
            var reopened=FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.Equal(RigHelperEditFields.Position|RigHelperEditFields.Orientation,FbxStructuralHelperAuthoring.Inspect(reopened).Single(r=>r.EntityId==row.EntityId).Locks);
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }

    [Fact]
    public void ActualPaletteUsageBlocksAnUnweightedMetadataBranch()
    {
        var source = Source();
        var rows = FbxStructuralHelperAuthoring.Inspect(source);
        var child = rows.Single(r => r.Name == "unknown_extra");
        var surfaces = source.Surfaces.Select(s => !s.IsSkinned ? s : s with
        {
            PaletteBoneIndices = [child.SourceIndex],
            InverseBindMatrices = [TransformMatrix.Identity],
            Vertices = s.Vertices.Select(v => v with { BoneIndices = [0], BoneWeights = [1] }).ToImmutableArray(),
        }).ToImmutableArray();
        var changed = source with { Surfaces = surfaces };
        rows = FbxStructuralHelperAuthoring.Inspect(changed);
        Assert.True(rows.Single(r => r.EntityId == child.EntityId).WeightedCorners > 0);
        var parent = rows.Single(r => r.Name == "normal_marker_2");
        Assert.Equal(0, parent.WeightedCorners);
        Assert.True(parent.WeightedBranch); Assert.False(parent.CanEditHelper);
        Assert.Throws<InvalidOperationException>(() => FbxStructuralHelperAuthoring.PreviewOffset(changed,
            parent.EntityId, new(new(.01, 0, 0), QuaternionD.Identity, Vector3D.One)));
    }

    [Fact]
    public void StaleAndInvalidWeightInputsCannotBeApplied()
    {
        var source=Source();var id=source.Package.Document.AuthoredHelpers[0].Id;
        var preview=FbxStructuralHelperAuthoring.PreviewOffset(source,id,new(new(.1,0,0),QuaternionD.Identity,Vector3D.One));
        Assert.False(FbxStructuralHelperAuthoring.TryApply(source with{Package=source.Package},preview,out _));
        Assert.Throws<OperationCanceledException>(()=>FbxStructuralHelperAuthoring.Inspect(source,new(true)));
        Assert.Throws<InvalidDataException>(() => FbxStructuralHelperAuthoring.PreviewOffset(source, id,
            new(new(1e100, 0, 0), QuaternionD.Identity, Vector3D.One)));
        var surface=source.Surfaces.First(s=>s.IsSkinned);var vertex=surface.Vertices[0];
        var invalid=surface with{Vertices=surface.Vertices.SetItem(0,vertex with{BoneWeights=vertex.BoneWeights.SetItem(0,double.NaN)})};
        Assert.Throws<InvalidDataException>(()=>FbxStructuralHelperAuthoring.Inspect(source with{Surfaces=source.Surfaces.Replace(surface,invalid)}));
    }
}
