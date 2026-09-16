using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RigidEyeBindingTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();
    internal static FbxModelAuthoringImportResult Source(bool includeBody = false)
    {
        var components = new List<SourceGeometryComponentAnalysis> {
            EyePivotDetectorTests.Sphere(new(-.035, 1.6, .05), new(.025,.025,.025)).Components[0],
            EyePivotDetectorTests.Sphere(new(.035, 1.6, .05), new(.025,.025,.025)).Components[0] };
        if (includeBody) components.Add(AnatomicalVolumeFixtures.Create().Geometry.Components[0]);
        var points = new List<Vector3D>(); var polygons = new List<long>();
        foreach (var component in components)
        {
            int offset = points.Count; points.AddRange(component.Geometry.ControlPoints);
            foreach (var t in component.Triangles) polygons.AddRange([offset + t.A, offset + t.B, -(offset + t.C) - 1L]);
        }
        return FbxModelAuthoringImporter.Import(FbxCustomModelMorphImportTests.CreateMorphFbx(["blink"], firstShapeDeltaX: .001,
            meshVertices: points.SelectMany(p => new[] { p.X * 100, p.Y * 100, p.Z * 100 }).ToArray(), meshPolygons: polygons.ToArray()), "generic-eye-islands.fbx");
    }
    internal static FbxModelAuthoringImportResult WithEyes(bool includeBody = false, bool affineParent = false)
    {
        var model = FbxGeneratedBodyBinding.Generate(GeneratedBodyWorkflowTests.WithFixtureGuides(Source(includeBody)));
        if (affineParent)
        {
            var document = model.Package.Document;
            int head = document.Bones.Single(b => b.Name == "body_head").Index;
            var bone = document.Bones[head];
            var matrix = bone.ExactLocalBindMatrix * new TransformMatrix(1.4,.2,0,0, 0,.7,.1,0, 0,0,1.2,0, 0,0,0,1);
            var bones = document.Bones.SetItem(head,bone with {ExactLocalBindMatrix=matrix,LocalBindTransform=bone.LocalBindTransform with { Scale = new(1.4,.7,1.2) }});
            document=document with{Bones=bones,RigSignature=CustomModelContractSignatures.ComputeRig(bones)};
            model=model with{Package=model.Package with{Document=document},Rig=document.CreateRigDefinition()};
        }
        foreach (var (side, island) in new[] { (RigEyeSide.Left, 0), (RigEyeSide.Right, 1) })
        {
            var work = FbxEyeAuthoring.InspectGeometry(model, model.Surfaces[0].SourceGeometry!.Id, island, new());
            var setup = new RigEyeSetup { Side = side, Mode = RigEyeSetupMode.GeometryPivot, GeometryKind = RigEyeGeometryKind.GlobeCandidate,
                GlobalFrame = TransformMatrix.CreateTranslation(work.Detection.Center!.Value), GlobeRadius = work.Detection.Radius,
                ComponentId = work.Detection.ComponentId, IslandIndex = island, SourceControlPointIds = work.Detection.SourceControlPointIds,
                UserApproved = true, ParentEntityId = work.Session.Recipe.Assignments.Single(a => a.RoleId == "body.head").EntityId,
                Evidence = [new() { Id = "generic-eye-fit", Kind = RigEvidenceKind.GeometryInference, ArtifactSha256 = work.Detection.InputFingerprint }] };
            model = FbxEyeAuthoring.SaveSetup(model, work.Token, setup);
            model = FbxGeneratedEyeAuthoring.Append(model, side, "eye_" + side.ToString().ToLowerInvariant());
        }
        return model;
    }

    [Fact]
    public void RigidBindingChangesOnlyOneWholeIslandAndReopens()
    {
        var source = WithEyes(); var preview = FbxRigidEyeBinding.Preview(source, RigEyeSide.Left);
        Assert.Equal(266, preview.SelectedPointCount); Assert.Equal(266, preview.ChangedPointCount);
        Assert.True(FbxRigidEyeBinding.TryApply(source, preview, out var bound));
        Assert.True(GeneratedBodyRig.IsGenerated(bound.Package.Document));
        var observed = FbxSkinWeightAuthoring.Inspect(bound);
        Assert.All(observed.Points.Where(p => p.ControlPointIndex < 266), p => Assert.Equal(new[] { new ReAnimated.Retargeting.Geometry.GeneratedSkinInfluence(preview.EyeEntityId,1) },p.Weights.ToArray()));
        Assert.All(observed.Points.Where(p => p.ControlPointIndex >= 266), p => Assert.Empty(p.Weights));
        Assert.Contains(bound.Surfaces,s => s.IsSkinned); Assert.Contains(bound.Surfaces,s => !s.IsSkinned);
        Assert.Equal<byte>(source.Package.SourceFbx,bound.Package.SourceFbx);
        Assert.Equal<CustomModelMorphChannel>(source.Package.Document.MorphChannels,bound.Package.Document.MorphChannels);
        Assert.Equal(0,FbxRigidEyeBinding.Preview(bound,RigEyeSide.Left).ChangedPointCount);
        var path=Path.Combine(_directory,"eye-binding.dlrmodel"); CustomModelPackageSerializer.SaveAtomic(bound.Package,path);
        var reopened=FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.Equal(Weights(bound),Weights(reopened));
        Assert.Contains(reopened.Package.Document.Diagnostics,d=>d.Code==FbxRigidEyeBinding.ReviewDiagnosticCode);
        Assert.NotNull(reopened.Package.Document.RiggingSession!.Eyes.Single(e=>e.Side==RigEyeSide.Left).DeformEntityId);
        Assert.Contains(bound.Package.Document.Diagnostics,d=>d.Code==FbxGeneratedBodyBinding.UnboundDiagnostic);
        Assert.True(FbxRigidEyeBinding.TryApply(bound,FbxRigidEyeBinding.Preview(bound,RigEyeSide.Right),out var both));
        Assert.DoesNotContain(both.Package.Document.Diagnostics,d=>d.Code==FbxGeneratedBodyBinding.UnboundDiagnostic);
    }

    [Fact]
    public void LocksAndStaleSnapshotsPreventRigidReassignment()
    {
        var model=WithEyes(); var snapshot=FbxSkinWeightAuthoring.Inspect(model);
        var eye=model.Package.Document.RiggingSession!.Eyes.Single(e=>e.Side==RigEyeSide.Left);
        Assert.True(FbxSkinWeightAuthoring.TrySetLocks(model,snapshot,[0],eye.DeformEntityId!.Value,true,out var locked));
        Assert.Throws<InvalidOperationException>(()=>FbxRigidEyeBinding.Preview(locked,RigEyeSide.Left));
        var preview=FbxRigidEyeBinding.Preview(model,RigEyeSide.Left);
        Assert.False(FbxRigidEyeBinding.TryApply(locked,preview,out _));
        var document=model.Package.Document;
        var moved=model with{Package=model.Package with{Document=document with{RiggingSession=RiggingSessions.Navigate(document.RiggingSession!,RigStudioStage.Skin)}}};
        var refreshed=FbxRigidEyeBinding.RefreshMetadata(preview,moved);
        Assert.NotNull(refreshed); Assert.True(FbxRigidEyeBinding.TryApply(moved,refreshed!,out _));
        Assert.Throws<OperationCanceledException>(()=>FbxRigidEyeBinding.Preview(model,RigEyeSide.Left,new(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EyeMotionPreservesPivotRadiusAndOtherEyeWithMorphBlending(bool affineParent)
    {
        var model=WithEyes(affineParent:affineParent); var preview=FbxRigidEyeBinding.Preview(model,RigEyeSide.Left);
        Assert.True(FbxRigidEyeBinding.TryApply(model,preview,out model));
        int index=GeneratedEyeRig.GetBoneIndex(model.Package.Document,RigEyeSide.Left);
        var scene=CustomModelPreviewAdapter.CreateSession(model,CustomModelPreviewMode.SourceFbx);
        var rest=RigEyeMotion.Evaluate(model.Package.Document,model.Rig!,RigEyeSide.Left,0,0);
        var pose=RigEyeMotion.Evaluate(model.Package.Document,model.Rig!,RigEyeSide.Left,30,-15);
        Assert.True((rest.GlobalMatrices[index].Translation-pose.GlobalMatrices[index].Translation).Length<1e-10);
        var restSkeleton=scene.CreateSkeleton(rest,null); var poseSkeleton=scene.CreateSkeleton(pose,null);
        var origin=rest.GlobalMatrices[index].Translation;
        var rigid=pose.GlobalMatrices[index]*rest.GlobalMatrices[index].InvertedAffine();
        foreach (var mesh in scene.Meshes)
        {
            var a=CpuMeshDeformationEvaluator.Evaluate(mesh,restSkeleton,[new("blink",.6f)]);
            var b=CpuMeshDeformationEvaluator.Evaluate(mesh,poseSkeleton,[new("blink",.6f)]);
            for(int i=0;i<a.Length;i++)
            {
                var old=new Vector3D(a[i].Position.X,a[i].Position.Y,a[i].Position.Z);
                var current=new Vector3D(b[i].Position.X,b[i].Position.Y,b[i].Position.Z);
                var expected=mesh.IsSkinned?rigid.TransformPoint(old):old;
                Assert.True((expected-current).Length<1e-5);
                Assert.InRange(Math.Abs((old-origin).Length-(current-origin).Length),0,1e-5);
                var oldNormal=new Vector3D(a[i].Normal.X,a[i].Normal.Y,a[i].Normal.Z);
                var expectedNormal=mesh.IsSkinned?rigid.TransformDirection(oldNormal).Normalized():oldNormal;
                Assert.True((expectedNormal-new Vector3D(b[i].Normal.X,b[i].Normal.Y,b[i].Normal.Z)).Length<1e-5);
            }
        }
    }

    [Fact]
    public void SetupMetadataCannotDetachGeneratedEyesOrInventFacialDescriptors()
    {
        var model=WithEyes(); var session=model.Package.Document.RiggingSession!;
        var eye=session.Eyes.Single(e=>e.Side==RigEyeSide.Left);
        Assert.Throws<InvalidOperationException>(()=>FbxEyeAuthoring.SaveSetup(model,session.CreateJobToken(),eye with{DeformEntityId=null}));
        Assert.Throws<InvalidOperationException>(()=>FbxEyeAuthoring.SaveSetup(model,session.CreateJobToken(),eye with{GlobalFrame=TransformMatrix.Identity}));
        Assert.Throws<InvalidDataException>(()=>FbxEyeAuthoring.SaveSetup(model,session.CreateJobToken(),new(){Mode=RigEyeSetupMode.Mimic,MorphDescriptors=[0]}));
    }

    [Fact]
    public void NonidentityExpertBindNeedsRestReviewBeforeReassignment()
    {
        var model=WithEyes(); var doc=model.Package.Document;
        var matrices=doc.CreateRigDefinition().CreateBindPose().GlobalMatrices;
        int target=doc.Bones.Single(b=>b.Name=="body_head").Index;
        var changed=model with{Surfaces=model.Surfaces.Select(s=>s with {IsSkinned=true,PaletteBoneIndices=[target],InverseBindMatrices=[matrices[target].InvertedAffine()*TransformMatrix.CreateTranslation(new(.02,0,0))],
            Vertices=s.Vertices.Select(v=>v with{BoneIndices=[0],BoneWeights=[1d]}).ToImmutableArray()}).ToImmutableArray()};
        Assert.Throws<InvalidOperationException>(()=>FbxRigidEyeBinding.Preview(changed,RigEyeSide.Left));
    }
    private static string[] Weights(FbxModelAuthoringImportResult model)=>FbxSkinWeightAuthoring.Inspect(model).Points.Select(p=>p.ComponentId+":"+p.ControlPointIndex+":"+string.Join(";",p.Weights.Select(w=>w.HandleId+"="+w.Weight))).ToArray();
    public void Dispose()=>RpackTestData.DeleteTemporaryDirectory(_directory);
}
