using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class FbxHierarchyAuthoringTests : IDisposable
{
    private static readonly int[] ReorderedTrackMap=[0,2,1];
    private static readonly int[] ShortTrackMap=[0];
    private static readonly int[] DuplicateTrackMap=[0,0];
    private readonly string _directory=RpackTestData.CreateTemporaryDirectory();
    internal static FbxModelAuthoringImportResult Source()
    {
        var model=FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(),"animated-hierarchy.fbx");
        var document=model.Package.Document;
        document=document with{RiggingSession=RiggingSessions.Create(document,RigStudioEntryPath.RepairExistingRig)};
        model=model with{Package=model.Package with{Document=document}};
        var component=model.Surfaces.First(s=>s.SourceGeometry is not null).SourceGeometry!.Id;
        var work=FbxEyeAuthoring.InspectGeometry(model,component,0,new());
        var setup=new RigEyeSetup{Side=RigEyeSide.Shared,Mode=RigEyeSetupMode.GeometryPivot,GeometryKind=RigEyeGeometryKind.ManualPivot,
            ParentEntityId=RiggingSessions.ObserveSourceHierarchy(document)[0].EntityId,GlobalFrame=TransformMatrix.CreateTranslation(new(.1,.2,.3)),
            ComponentId=component,IslandIndex=0,SourceControlPointIds=work.Detection.SourceControlPointIds,UserApproved=true};
        model=FbxEyeAuthoring.SaveSetup(model,work.Token,setup);
        model=FbxGeneratedEyeAuthoring.Append(model,RigEyeSide.Shared,"aux_eye");
        int child=model.Package.Document.Bones.Single(b=>b.Name=="Child").Index;
        var helpers=CustomModelHelperAuthoring.DuplicateAsHelper(model.Package.Document,child,CustomModelAuthoredHelperKind.Camera,"review_camera");
        helpers=CustomModelHelperAuthoring.DuplicateAsHelper(helpers,helpers.Bones.Length,CustomModelAuthoredHelperKind.Prop,"review_prop");
        helpers=CustomModelHelperAuthoring.SelectPreviewCamera(helpers,"review_camera");
        return model with{Package=model.Package with{Document=helpers},Rig=helpers.CreateRigDefinition()};
    }
    internal static Guid Entity(FbxModelAuthoringImportResult model,string name)=>
        RiggingSessions.ObserveSourceHierarchy(model.Package.Document)[model.Package.Document.Bones.Single(b=>b.Name==name).Index].EntityId;

    [Fact]
    public void LaterParentKeepsSkinAndTracksOnTheSameEntitiesThroughReopen()
    {
        var source=Source();Assert.NotEmpty(source.AnimationClips);
        Assert.Contains(source.AnimationClips.Values.SelectMany(c=>c.TransformTracks),t=>source.Package.Document.Bones[t.BoneIndex].Name=="Child");
        var preview=FbxHierarchyAuthoring.Preview(source,Entity(source,"Child"),Entity(source,"aux_eye"));
        Assert.True(preview.HasChanges);Assert.True(preview.ReorderedBones>=2);Assert.True(preview.RemappedTracks>0);
        Assert.True(FbxHierarchyAuthoring.TryApply(source,preview,out var result));
        int child=result.Package.Document.Bones.Single(b=>b.Name=="Child").Index;
        int parent=result.Package.Document.Bones.Single(b=>b.Name=="aux_eye").Index;
        Assert.True(parent<child);Assert.Equal(parent,result.Package.Document.Bones[child].ParentIndex);
        Assert.Equal(child,result.Package.Document.AuthoredHelpers[0].ParentNodeIndex);
        Assert.Equal(result.Package.Document.Bones.Length,result.Package.Document.AuthoredHelpers[1].ParentNodeIndex);
        for(int i=0;i<source.Surfaces.Length;i++)
        {
            Assert.Equal(source.Surfaces[i].Vertices,result.Surfaces[i].Vertices);
            Assert.Equal(source.Surfaces[i].InverseBindMatrices,result.Surfaces[i].InverseBindMatrices);
            Assert.Equal(source.Surfaces[i].MorphTargets,result.Surfaces[i].MorphTargets);
        }
        Assert.Equal(source.Package.Document.Camera,result.Package.Document.Camera);
        Assert.Equal(source.Package.Document.AnimationClips,result.Package.Document.AnimationClips);
        Assert.Equal(source.Package.SourceFbx,result.Package.SourceFbx);
        AssertSkinSame(source,result);AssertTracksSame(source,result);
        var path=Path.Combine(_directory,"reparented.dlrmodel");CustomModelPackageSerializer.SaveAtomic(result.Package,path);
        var reopened=FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        AssertSkinSame(source,reopened);AssertTracksSame(source,reopened);
        Assert.Equal<CustomModelBone>(result.Package.Document.Bones,reopened.Package.Document.Bones);
        Assert.Contains(reopened.Package.Document.Diagnostics,d=>d.Code==FbxHierarchyAuthoring.ReviewDiagnosticCode);
        Assert.Single(reopened.Package.Document.RiggingSession!.ParentDecisions);
    }

    [Fact]
    public void WorkflowRefreshIsStrictAndNoOpKeepsTheSnapshot()
    {
        var model=Source();var child=Entity(model,"Child");var root=Entity(model,"Root");
        var noOp=FbxHierarchyAuthoring.Preview(model,child,root);Assert.False(noOp.HasChanges);Assert.Same(model,noOp.PreviewModel);
        var preview=FbxHierarchyAuthoring.Preview(model,child,Entity(model,"aux_eye"));
        var document=model.Package.Document;
        var nav=model with{Package=model.Package with{Document=document with{RiggingSession=RiggingSessions.Navigate(document.RiggingSession!,RigStudioStage.Animate)}}};
        Assert.False(FbxHierarchyAuthoring.TryApply(nav,preview,out _));
        var updated=FbxHierarchyAuthoring.RefreshMetadata(preview,nav);Assert.NotNull(updated);
        Assert.True(FbxHierarchyAuthoring.TryApply(nav,updated!,out var result));
        Assert.Equal(RigStudioStage.Animate,result.Package.Document.RiggingSession!.Stage);
        Assert.Null(FbxHierarchyAuthoring.RefreshMetadata(preview,nav with{Package=nav.Package with{Document=nav.Package.Document with{Name="changed name"}}}));
        Assert.Throws<OperationCanceledException>(()=>FbxHierarchyAuthoring.Preview(model,child,root,new(true)));
    }

    [Fact]
    public void UntrackedAffineNodeKeepsItsExactBindDuringClipPreview()
    {
        var model=Source();int eye=model.Package.Document.Bones.Single(b=>b.Name=="aux_eye").Index;
        var old=model.Package.Document.Bones[eye];
        var exact=old.ExactLocalBindMatrix*new TransformMatrix(1,.25,0,0,0,1,.1,0,0,0,1,0,0,0,0,1);
        var poseEdit=RigRestPoseAuthoring.Apply(model.Package.Document,model.Package.Document.RiggingSession!.CreateJobToken(),Entity(model,"aux_eye"),
            FbxRestPoseAuthoringTests.ExactGlobals(model.Package.Document)[old.ParentIndex]*exact,RigRestDescendantMode.KeepGlobal);
        model=model with{Package=model.Package with{Document=poseEdit.Document},Rig=poseEdit.Document.CreateRigDefinition()};
        var clip=model.AnimationClips.Values.First();Assert.DoesNotContain(clip.TransformTracks,t=>t.BoneIndex==eye);
        var sampled=clip.SamplePose(model.Rig!,0);var locals=sampled.LocalMatrices.ToArray();locals[eye]=exact;
        for(int i=model.Package.Document.Bones.Length;i<locals.Length;i++)locals[i]=model.Package.Document.CreateEffectiveBones()[i].ExactLocalBindMatrix;
        var expectedPose=new SkeletonPose(model.Rig!,sampled.LocalTransforms,locals);
        var session=CustomModelPreviewAdapter.CreateSession(model,CustomModelPreviewMode.SourceFbx);
        var expected=session.CreateSkeleton(expectedPose,null);var actual=session.CreateSkeleton(clip,0,null);
        Assert.Equal(expected.Bones[eye],actual!.Bones[eye]);
    }

    private static void AssertTracksSame(FbxModelAuthoringImportResult a,FbxModelAuthoringImportResult b)
    {
        foreach(var (id,clip) in a.AnimationClips)
        {
            var other=b.AnimationClips[id];Assert.Equal(clip.FrameCount,other.FrameCount);Assert.Equal(clip.FrameRate,other.FrameRate);
            foreach(var track in clip.TransformTracks)
            {
                string name=a.Package.Document.CreateEffectiveBones()[track.BoneIndex].Name;
                var moved=other.TransformTracks.Single(t=>b.Package.Document.CreateEffectiveBones()[t.BoneIndex].Name==name);
                Assert.Equal<TransformKeyframe>(track.Keyframes,moved.Keyframes);
            }
            foreach(var scalar in clip.ScalarTracks)
                Assert.Equal<ScalarKeyframe>(scalar.Keyframes,other.ScalarTracks.Single(t=>t.ChannelName==scalar.ChannelName).Keyframes);
        }
    }

    [Fact]
    public void ReindexRetainsScalarAndAuxiliaryValuesAndRejectsInvalidOwnership()
    {
        var id=Guid.NewGuid();ImmutableArray<TransformKeyframe> keys=[new(0,TransformTRS.Identity),new(1,TransformTRS.Identity with{Translation=new(.2,.3,.4)})];
        var scalar=new ScalarTrack("expression",[new(0,-.4),new(1,.6)]);
        var auxiliary=new AuxiliaryTransformTrack(123,keys);
        var clip=new AnimationClip("generic",new(30,1),2,[new TransformTrack(1,keys)],[scalar],[auxiliary]);
        var source=ImmutableDictionary<Guid,AnimationClip>.Empty.Add(id,clip);
        var result=FbxAnimationTrackReindexer.Reindex(source,ReorderedTrackMap);
        Assert.Equal(2,Assert.Single(result[id].TransformTracks).BoneIndex);
        Assert.Equal<TransformKeyframe>(keys,result[id].TransformTracks[0].Keyframes);
        Assert.Same(scalar,Assert.Single(result[id].ScalarTracks));Assert.Same(auxiliary,Assert.Single(result[id].AuxiliaryTransformTracks));
        Assert.Throws<InvalidDataException>(()=>FbxAnimationTrackReindexer.Reindex(source,ShortTrackMap));
        Assert.Throws<InvalidDataException>(()=>FbxAnimationTrackReindexer.Reindex(source,DuplicateTrackMap));
    }
    private static void AssertSkinSame(FbxModelAuthoringImportResult a,FbxModelAuthoringImportResult b)
    {
        var first=CustomModelPreviewAdapter.CreateSession(a,CustomModelPreviewMode.SourceFbx);var second=CustomModelPreviewAdapter.CreateSession(b,CustomModelPreviewMode.SourceFbx);
        var rowsA=first.Meshes.SelectMany(m=>CpuMeshDeformationEvaluator.Evaluate(m,first.CreateSkeleton(null,0,null),[])).ToArray();
        var rowsB=second.Meshes.SelectMany(m=>CpuMeshDeformationEvaluator.Evaluate(m,second.CreateSkeleton(null,0,null),[])).ToArray();
        Assert.Equal(rowsA.Length,rowsB.Length);
        for(int i=0;i<rowsA.Length;i++)
        {
            Assert.True(System.Numerics.Vector3.Distance(rowsA[i].Position,rowsB[i].Position)<1e-5);
            Assert.True(System.Numerics.Vector3.Distance(rowsA[i].Normal,rowsB[i].Normal)<1e-5);
        }
    }
    public void Dispose()=>RpackTestData.DeleteTemporaryDirectory(_directory);
}
