using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class FbxRestPoseAuthoringTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();
    internal static FbxModelAuthoringImportResult Source(bool expert = true)
    {
        var raw = FbxModelAuthoringImporter.Import(FbxCustomModelMorphImportTests.CreateMorphFbx(["shape_a","shape_b"], firstShapeDeltaX:.002,
            normalDeltas:[.2,.1,-.03,.1,-.2,-.02]),"generic-rest.fbx");
        var model = FbxGeneratedBodyBinding.Generate(GeneratedBodyWorkflowTests.WithFixtureGuides(raw));
        var document = model.Package.Document;
        var globals = ExactGlobals(document);
        int head = document.Bones.Single(b=>b.Name=="body_head").Index;
        int neck = document.Bones.Single(b=>b.Name=="body_neck_0").Index;
        var a=expert?TransformMatrix.CreateTranslation(new(.03,.05,.01))*new TransformMatrix(1.1,.2,0,0, 0,.9,.1,0, 0,0,1.2,0, 0,0,0,1):TransformMatrix.Identity;
        var b=expert?TransformMatrix.CreateTranslation(new(-.01,.03,0))*TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY,.2)):TransformMatrix.Identity;
        var surfaces=model.Surfaces.Select(s=>s with {IsSkinned=true,PaletteBoneIndices=[head,neck],InverseBindMatrices=[globals[head].InvertedAffine()*a,globals[neck].InvertedAffine()*b],
            Vertices=s.Vertices.Select(v=>v with{BoneIndices=[0,1],BoneWeights=[.35,.65]}).ToImmutableArray()}).ToImmutableArray();
        return FbxAuthoredModelLayer.Capture(model with{Surfaces=surfaces});
    }
    internal static TransformMatrix[] ExactGlobals(CustomModelDocument document)
    {
        var bones=document.CreateEffectiveBones();var result=new TransformMatrix[bones.Length];
        foreach(var bone in bones)result[bone.Index]=bone.ParentIndex<0?bone.ExactLocalBindMatrix:result[bone.ParentIndex]*bone.ExactLocalBindMatrix;
        return result;
    }
    private static (Guid Entity,int Index,TransformMatrix Frame) Selection(FbxModelAuthoringImportResult model)
    {
        int index=model.Package.Document.Bones.Single(b=>b.Name=="body_head").Index;
        var node=RiggingSessions.ObserveSourceHierarchy(model.Package.Document)[index];
        return (node.EntityId,index,ExactGlobals(model.Package.Document)[index]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InverseCompensationPreservesExpertNeutralAndBlendedMorphs(bool expert)
    {
        var source=Source(expert);var selected=Selection(source);
        var desired=selected.Frame*TransformMatrix.CreateTranslation(new(.02,.03,0))*TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY,.4));
        var preview=FbxRestPoseAuthoring.Preview(source,selected.Entity,desired,RigRestDescendantMode.KeepGlobal,RigRestSurfaceMode.PreserveSurface);
        Assert.True(preview.HasChanges);Assert.Equal(0,preview.Report.MaximumVisibleDisplacement);
        Assert.True(FbxRestPoseAuthoring.TryApply(source,preview,out var result));
        Assert.Equal(source.Surfaces[0].Vertices,result.Surfaces[0].Vertices);
        Assert.Equal(source.Surfaces[0].MorphTargets,result.Surfaces[0].MorphTargets);
        Assert.NotEqual(source.Surfaces[0].InverseBindMatrices,result.Surfaces[0].InverseBindMatrices);
        CompareRender(source,source.Package.Document,result);
        Assert.Equal(source.AnimationClips,result.AnimationClips);
        Assert.Equal(source.Package.Document.AnimationClips,result.Package.Document.AnimationClips);
        Assert.Equal(source.Package.Document.Materials,result.Package.Document.Materials);
        Assert.Equal(source.Package.SourceFbx,result.Package.SourceFbx);
        var path=Path.Combine(_directory,"refit.dlrmodel");CustomModelPackageSerializer.SaveAtomic(result.Package,path);
        var reopened=FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        CompareRender(source,source.Package.Document,reopened);
        Assert.Contains(reopened.Package.Document.Diagnostics,d=>d.Code==FbxRestPoseAuthoring.ReviewDiagnosticCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoseBakeMatchesPosedOriginalIncludingSignedMorphNormals(bool expert)
    {
        var source=Source(expert);var selected=Selection(source);
        var desired=selected.Frame*TransformMatrix.CreateTranslation(new(.02,0,.01))*new TransformMatrix(1.2,.1,0,0, 0,.9,.15,0, 0,0,1.1,0, 0,0,0,1);
        var preview=FbxRestPoseAuthoring.Preview(source,selected.Entity,desired,RigRestDescendantMode.FollowLocal,RigRestSurfaceMode.BakePose);
        Assert.True(preview.Report.MaximumVisibleDisplacement>0);Assert.True(preview.Report.EvaluatedVertices>0);
        Assert.True(FbxRestPoseAuthoring.TryApply(source,preview,out var result));
        CompareRender(source,result.Package.Document,result);
        Assert.Equal(source.Surfaces[0].SourceCorners,result.Surfaces[0].SourceCorners);
        Assert.Equal(source.Surfaces[0].SourceTriangles,result.Surfaces[0].SourceTriangles);
        Assert.Equal(source.Surfaces[0].PaletteBoneIndices,result.Surfaces[0].PaletteBoneIndices);
        var path=Path.Combine(_directory,"baked.dlrmodel");CustomModelPackageSerializer.SaveAtomic(result.Package,path);
        var reopened=FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        CompareRender(source,result.Package.Document,reopened);
        Assert.Equal<byte>(source.Package.SourceFbx,reopened.Package.SourceFbx);
    }

    [Fact]
    public void NoOpAndWorkflowRefreshDoNotDiscardOtherEdits()
    {
        var source=Source();var selected=Selection(source);
        var none=FbxRestPoseAuthoring.Preview(source,selected.Entity,selected.Frame,RigRestDescendantMode.FollowLocal,RigRestSurfaceMode.BakePose);
        Assert.False(none.HasChanges);Assert.Same(source,none.PreviewModel);
        var preview=FbxRestPoseAuthoring.Preview(source,selected.Entity,selected.Frame*TransformMatrix.CreateTranslation(new(.01,0,0)),RigRestDescendantMode.KeepGlobal,RigRestSurfaceMode.PreserveSurface);
        var d=source.Package.Document;
        var moved=source with{Package=source.Package with{Document=d with{RiggingSession=RiggingSessions.Navigate(d.RiggingSession!,RigStudioStage.Animate)}}};
        Assert.False(FbxRestPoseAuthoring.TryApply(moved,preview,out _));
        var refreshed=FbxRestPoseAuthoring.RefreshMetadata(preview,moved);
        Assert.NotNull(refreshed);Assert.True(FbxRestPoseAuthoring.TryApply(moved,refreshed!,out var result));
        Assert.Equal(RigStudioStage.Animate,result.Package.Document.RiggingSession!.Stage);
        var changed=moved with{Package=moved.Package with{Document=moved.Package.Document with{Materials=moved.Package.Document.Materials.Select(m=>m with{Name=m.Name+"_edited"}).ToImmutableArray()}}};
        Assert.Null(FbxRestPoseAuthoring.RefreshMetadata(preview,changed));
        Assert.Throws<OperationCanceledException>(()=>FbxRestPoseAuthoring.Preview(source,selected.Entity,selected.Frame,RigRestDescendantMode.KeepGlobal,RigRestSurfaceMode.PreserveSurface,new(true)));
    }

    private static void CompareRender(FbxModelAuthoringImportResult original,CustomModelDocument posedDocument,FbxModelAuthoringImportResult result)
    {
        var before=CustomModelPreviewAdapter.CreateSession(original,CustomModelPreviewMode.SourceFbx);
        var after=CustomModelPreviewAdapter.CreateSession(result,CustomModelPreviewMode.SourceFbx);
        var oldPose=new SkeletonPose(original.Rig!,posedDocument.CreateEffectiveBones().Select(b=>b.LocalBindTransform),posedDocument.CreateEffectiveBones().Select(b=>b.ExactLocalBindMatrix));
        var newPose=new SkeletonPose(result.Rig!,result.Package.Document.CreateEffectiveBones().Select(b=>b.LocalBindTransform),result.Package.Document.CreateEffectiveBones().Select(b=>b.ExactLocalBindMatrix));
        foreach(var weights in new[]{Array.Empty<MorphWeight>(),new[]{new MorphWeight("shape_a",.6f),new MorphWeight("shape_b",-.4f)}})
        {
            Assert.Equal(before.Meshes.Length,after.Meshes.Length);
            for(int mesh=0;mesh<before.Meshes.Length;mesh++)
            {
                var a=CpuMeshDeformationEvaluator.Evaluate(before.Meshes[mesh],before.CreateSkeleton(oldPose,null),weights);
                var b=CpuMeshDeformationEvaluator.Evaluate(after.Meshes[mesh],after.CreateSkeleton(newPose,null),weights);
                Assert.Equal(a.Length,b.Length);
                for(int i=0;i<a.Length;i++)
                {
                    Assert.True(System.Numerics.Vector3.Distance(a[i].Position,b[i].Position)<1e-5,$"Position {i}: {a[i].Position} / {b[i].Position}");
                    Assert.True(System.Numerics.Vector3.Distance(a[i].Normal,b[i].Normal)<1e-5,$"Normal {i}: {a[i].Normal} / {b[i].Normal}");
                    Assert.Equal(a[i].TextureCoordinate,b[i].TextureCoordinate);
                }
            }
        }
    }
    public void Dispose()=>RpackTestData.DeleteTemporaryDirectory(_directory);
}
