using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FbxRigDoctorTests : IDisposable
{
    private readonly string _directory=RpackTestData.CreateTemporaryDirectory();
    internal static RigDoctorContactRule Rule(FbxModelAuthoringImportResult model,string name="required_contact",string role="contact.required") => new()
    {
        RoleId=role,HelperName=name,ParentName=model.Package.Document.Bones.First(b=>b.Kind==ReAnimated.Core.Domain.BoneKind.Deform).Name,ComponentId="source-shoe",Components=RigAnimationComponents.None,
        AnimationLod=RigAnimationLod.Off,ComponentRuleId="reviewed-contact-components",LodRuleId="reviewed-contact-retention",
        Evidence=new(){Id="fixture-rule",Kind=RigEvidenceKind.UserOverride,ArtifactSha256=model.Package.Document.Source.ContentSha256,
            Description="Synthetic authoring choices; no native profile certification."},
    };

    [Fact]
    public void MissingHelpersPreviewAtomicallyAndASecondDiagnosisIsANoOp()
    {
        var (source,_)=FbxContactAuthoringTests.Model();
        ImmutableArray<RigDoctorContactRule> rules=[Rule(source),Rule(source,"second_contact","contact.second")];
        var preview=FbxRigDoctor.Preview(source,rules);
        Assert.True(preview.CanApply);Assert.Equal(2,preview.RepairCount);
        Assert.Empty(source.Package.Document.AuthoredHelpers);
        Assert.Throws<InvalidOperationException>(()=>FbxRigDoctor.TryApply(source,preview,false,out _));
        Assert.True(FbxRigDoctor.TryApply(source,preview,true,out var repaired));
        Assert.Equal(source.Surfaces,repaired.Surfaces);
        Assert.Same(source.AnimationClips,repaired.AnimationClips);
        Assert.Equal(source.Package.SourceFbx,repaired.Package.SourceFbx);
        Assert.Equal(source.Package.Document.Bones,repaired.Package.Document.Bones);
        Assert.Equal(source.Package.Document.MorphChannels,repaired.Package.Document.MorphChannels);
        Assert.Equal(2,repaired.Package.Document.AuthoredHelpers.Length);
        Assert.All(repaired.Package.Document.RiggingSession!.Recipe.ComponentPolicies,p=>
        { Assert.Equal(RigAnimationComponents.None,p.EmittedMask);Assert.Equal(RigAnimationLod.Off,p.AnimationLod); });
        var again=FbxRigDoctor.Preview(repaired,rules);
        Assert.True(again.IsNoOp);Assert.Equal(0,again.RepairCount);Assert.Same(repaired,again.Candidate);
        Assert.True(FbxRigDoctor.TryApply(repaired,again,false,out var same));Assert.Same(repaired,same);
    }

    [Fact]
    public void ConflictInOneRowPreventsAllRepairs()
    {
        var (source,_)=FbxContactAuthoringTests.Model();
        ImmutableArray<RigDoctorContactRule> rules=[Rule(source),Rule(source,"another","contact.other") with{ParentName="absent"}];
        var preview=FbxRigDoctor.Preview(source,rules);
        Assert.False(preview.CanApply);Assert.Null(preview.Candidate);
        Assert.Contains(preview.Rows,r=>r.Status==RigDoctorRowStatus.NeedsReview);
        Assert.Empty(source.Package.Document.AuthoredHelpers);
    }

    [Fact]
    public void ExistingWrongParentIsNotDuplicatedOrMoved()
    {
        var (source,_)=FbxContactAuthoringTests.Model();
        int child=source.Package.Document.Bones.Single(b=>b.Name=="Root").Index;
        var document=CustomModelHelperAuthoring.DuplicateAsHelper(source.Package.Document,child,CustomModelAuthoredHelperKind.Helper,"required_contact");
        source=source with{Package=source.Package with{Document=document},Rig=document.CreateRigDefinition()};
        var preview=FbxRigDoctor.Preview(source,[Rule(source)]);
        Assert.Equal(RigDoctorRowStatus.NeedsReview,Assert.Single(preview.Rows).Status);
        Assert.Null(preview.Candidate);Assert.Single(source.Package.Document.AuthoredHelpers);
    }

    [Fact]
    public void AmbiguousGeometryRequiresAnExplicitComponent()
    {
        var (source,_)=FbxContactAuthoringTests.Model();var surface=source.Surfaces[0];
        source=source with{Surfaces=source.Surfaces.Add(surface with{SourceGeometry=surface.SourceGeometry! with{Id="another-shoe"}})};
        var preview=FbxRigDoctor.Preview(source,[Rule(source) with{ComponentId=null}]);
        Assert.False(preview.CanApply);Assert.Contains("Several",preview.Rows[0].Message,StringComparison.Ordinal);
        Assert.True(FbxRigDoctor.Preview(source,[Rule(source)]).CanApply);
    }

    [Fact]
    public void IncompletePolicyAndStaleReviewCannotApply()
    {
        var (source,_)=FbxContactAuthoringTests.Model();
        Assert.False(FbxRigDoctor.Preview(source,[Rule(source) with{Components=null}]).CanApply);
        var preview=FbxRigDoctor.Preview(source,[Rule(source)]);
        var changed=source with{Package=source.Package with{Document=source.Package.Document with{Name="changed"}}};
        Assert.False(FbxRigDoctor.TryApply(changed,preview,true,out _));
        Assert.Null(FbxRigDoctor.RefreshMetadata(preview,changed));
        var nav=source with{Package=source.Package with{Document=source.Package.Document with{RiggingSession=
            RiggingSessions.Navigate(source.Package.Document.RiggingSession!,RigStudioStage.Animate)}}};
        var refreshed=FbxRigDoctor.RefreshMetadata(preview,nav);Assert.NotNull(refreshed);
        Assert.True(FbxRigDoctor.TryApply(nav,refreshed!,true,out var result));
        Assert.Equal(RigStudioStage.Animate,result.Package.Document.RiggingSession!.Stage);
        Assert.Throws<OperationCanceledException>(()=>FbxRigDoctor.Preview(source,[Rule(source)],new(true)));
    }

    [Fact]
    public void ExistingSettingsThatConflictWithChangedRulesNeedReview()
    {
        var (source,_)=FbxContactAuthoringTests.Model();var rule=Rule(source);
        var preview=FbxRigDoctor.Preview(source,[rule]);Assert.True(FbxRigDoctor.TryApply(source,preview,true,out var repaired));
        var changedRule=rule with{BoundsHalfExtents=[.4,.4,.4]};
        var mismatch=FbxRigDoctor.Preview(repaired,[changedRule]);
        Assert.False(mismatch.IsNoOp);Assert.False(mismatch.CanApply);
        Assert.Equal(RigDoctorRowStatus.NeedsReview,Assert.Single(mismatch.Rows).Status);
        Assert.Single(repaired.Package.Document.AuthoredHelpers);
    }

    [Fact]
    public void RealSourceHelperRepairSavesAndReopensWithoutChangingWeightsOrClips()
    {
        var source=FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceWeightFixture(),"generic-contact.fbx");
        source=source with{Package=source.Package with{Document=source.Package.Document with
            {RiggingSession=RiggingSessions.Create(source.Package.Document,RigStudioEntryPath.RepairExistingRig)}}};
        var surface=source.Surfaces.First(s=>s.IsSkinned);var a=surface.Vertices[0].Position;var b=surface.Vertices[1].Position;var c=surface.Vertices[2].Position;
        var up=Vector3D.Cross(b-a,c-a).Normalized();var forward=(b-a).Normalized();
        var rule=Rule(source) with{ComponentId=surface.SourceGeometry!.Id,DirectionsInParentSpace=false,MinimumParentWeight=.05,
            Up=[up.X,up.Y,up.Z],Forward=[forward.X,forward.Y,forward.Z],BoundsCenter=[0,0,0],BoundsHalfExtents=[.01,.01,.01]};
        var preview=FbxRigDoctor.Preview(source,[rule]);Assert.True(preview.CanApply,preview.Rows[0].Message);
        Assert.True(FbxRigDoctor.TryApply(source,preview,true,out var result));
        string path=Path.Combine(_directory,"repaired.dlrmodel");CustomModelPackageSerializer.SaveAtomic(result.Package,path);
        var reopened=FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.True(FbxRigDoctor.Preview(reopened,[rule]).IsNoOp);
        Assert.True(source.Package.SourceFbx.AsSpan().SequenceEqual(reopened.Package.SourceFbx.AsSpan()));
        Assert.Equal<CustomModelBone>(source.Package.Document.Bones,reopened.Package.Document.Bones);
        for(int i=0;i<source.Surfaces.Length;i++)
        {
            var expected=source.Surfaces[i].Vertices;var actual=reopened.Surfaces[i].Vertices;
            Assert.Equal(expected.Length,actual.Length);
            for(int v=0;v<expected.Length;v++)
            {
                Assert.Equal(expected[v].Position,actual[v].Position);Assert.Equal(expected[v].Normal,actual[v].Normal);
                Assert.Equal<int>(expected[v].BoneIndices,actual[v].BoneIndices);Assert.Equal<double>(expected[v].BoneWeights,actual[v].BoneWeights);
            }
            Assert.Equal<int>(source.Surfaces[i].PaletteBoneIndices,reopened.Surfaces[i].PaletteBoneIndices);
            Assert.Equal<TransformMatrix>(source.Surfaces[i].InverseBindMatrices,reopened.Surfaces[i].InverseBindMatrices);
        }
        foreach(var (id,clip) in source.AnimationClips)
            foreach(var track in clip.TransformTracks)
                Assert.Equal<ReAnimated.Core.Domain.TransformKeyframe>(track.Keyframes,reopened.AnimationClips[id].TransformTracks.Single(t=>t.BoneIndex==track.BoneIndex).Keyframes);
    }
    public void Dispose()=>RpackTestData.DeleteTemporaryDirectory(_directory);
}
