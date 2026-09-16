using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

public sealed class FbxHierarchyPreview
{
    internal FbxHierarchyPreview(FbxModelAuthoringImportResult source,FbxModelAuthoringImportResult candidate,
        RiggingJobToken token,Guid entityId,Guid? parentId,ImmutableArray<int> oldToNew,int remappedTracks)
    {SourceModel=source;PreviewModel=candidate;Token=token;EntityId=entityId;ParentEntityId=parentId;OldToNewBoneIndices=oldToNew;RemappedTracks=remappedTracks;}
    internal FbxModelAuthoringImportResult SourceModel{get;}
    internal RiggingJobToken Token{get;}
    public FbxModelAuthoringImportResult PreviewModel{get;}
    public Guid EntityId{get;}
    public Guid? ParentEntityId{get;}
    public ImmutableArray<int> OldToNewBoneIndices{get;}
    public int ReorderedBones=>OldToNewBoneIndices.Where((index,old)=>index!=old).Count();
    public int RemappedTracks{get;}
    public bool HasChanges=>!ReferenceEquals(SourceModel,PreviewModel);
}

/// <summary>Reparents a stable bone, reindexes draw palettes and decoded track ownership, and preserves neutral geometry.</summary>
public static class FbxHierarchyAuthoring
{
    public const string ReviewDiagnosticCode="hierarchy_motion_review";
    public static FbxHierarchyPreview Preview(FbxModelAuthoringImportResult model,Guid entityId,Guid? parentEntityId,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(model);cancellationToken.ThrowIfCancellationRequested();
        var source=model.Package.Document;
        var session=source.RiggingSession??throw new InvalidOperationException("Start a studio session before reparenting a joint.");
        var token=session.CreateJobToken();
        var edit=RigHierarchyAuthoring.Apply(source,token,entityId,parentEntityId);
        if(ReferenceEquals(edit.Document,source))return new(model,model,token,entityId,parentEntityId,edit.OldToNewBoneIndices,0);
        if(edit.OldToNewBoneIndices.Length!=source.Bones.Length||edit.OldToNewBoneIndices.Distinct().Count()!=source.Bones.Length)
            throw new InvalidDataException("The hierarchy transaction did not retain every source bone identity.");
        var surfaces=ImmutableArray.CreateBuilder<FbxModelSurface>(model.Surfaces.Length);
        foreach(var surface in model.Surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(surface.PaletteBoneIndices.Length!=surface.InverseBindMatrices.Length||surface.PaletteBoneIndices.Any(i=>(uint)i>=(uint)source.Bones.Length))
                throw new InvalidDataException("A source draw has invalid palette identities or inverse binds.");
            var palette=surface.PaletteBoneIndices.Select(i=>edit.OldToNewBoneIndices[i]).ToImmutableArray();
            surfaces.Add(palette.SequenceEqual(surface.PaletteBoneIndices)?surface:surface with{PaletteBoneIndices=palette});
        }
        var clips=FbxAnimationTrackReindexer.Reindex(model.AnimationClips,edit.OldToNewEffectiveIndices,cancellationToken);
        int trackCount=model.AnimationClips.Values.Sum(c=>c.TransformTracks.Count(t=>edit.OldToNewEffectiveIndices[t.BoneIndex]!=t.BoneIndex));
        var document=edit.Document with{Diagnostics=edit.Document.Diagnostics.Where(d=>d.Code!=ReviewDiagnosticCode).Append(new CustomModelImportDiagnostic
        {Code=ReviewDiagnosticCode,Severity=CustomModelImportSeverity.Warning,Message="Hierarchy parentage changed. Draw and animation-track indices retain their bone identities, but local animation curves require fresh motion and native-parent validation."}).ToImmutableArray()};
        var candidate=model with{Package=model.Package with{Document=document},Rig=document.CreateRigDefinition(),Surfaces=surfaces.MoveToImmutable(),AnimationClips=clips};
        candidate=FbxAuthoredModelLayer.Capture(candidate,cancellationToken);
        return new(model,candidate,token,entityId,parentEntityId,edit.OldToNewBoneIndices,trackCount);
    }

    public static bool TryApply(FbxModelAuthoringImportResult current,FbxHierarchyPreview preview,out FbxModelAuthoringImportResult result)
    {
        ArgumentNullException.ThrowIfNull(current);ArgumentNullException.ThrowIfNull(preview);result=current;
        if(!ReferenceEquals(current,preview.SourceModel)||current.Package.Document.RiggingSession?.Matches(preview.Token)!=true)return false;
        preview.PreviewModel.Package.Document.Validate();result=preview.PreviewModel;return true;
    }

    public static FbxHierarchyPreview? RefreshMetadata(FbxHierarchyPreview preview,FbxModelAuthoringImportResult current)
    {
        ArgumentNullException.ThrowIfNull(preview);ArgumentNullException.ThrowIfNull(current);
        var before=preview.SourceModel;var a=before.Package.Document;var b=current.Package.Document;
        if(a.RiggingSession is not { } oldSession||b.RiggingSession is not { } newSession||!newSession.Matches(preview.Token)||
            oldSession with{Stage=newSession.Stage}!=newSession||a with{RiggingSession=newSession}!=b||before.Surfaces!=current.Surfaces||
            before.AnimationClips!=current.AnimationClips||!ReferenceEquals(before.Rig,current.Rig)||before.Package.SourceFbx!=current.Package.SourceFbx||
            before.Package.AuthoredLayerPayload!=current.Package.AuthoredLayerPayload)return null;
        var candidate=preview.HasChanges?preview.PreviewModel with{Package=preview.PreviewModel.Package with{Document=preview.PreviewModel.Package.Document with
            {RiggingSession=RiggingSessions.Navigate(preview.PreviewModel.Package.Document.RiggingSession!,newSession.Stage)}}}:current;
        return new(current,candidate,preview.Token,preview.EntityId,preview.ParentEntityId,preview.OldToNewBoneIndices,preview.RemappedTracks);
    }
}
