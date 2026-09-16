using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Codecs.Fbx;

public sealed class FbxDerivedMotionPreview
{
    internal FbxDerivedMotionPreview(FbxModelAuthoringImportResult source,FbxModelAuthoringImportResult? candidate,
        RiggingJobToken token,Guid clipId,DerivedMotionReport report)
    {SourceModel=source;PreviewModel=candidate;Token=token;ClipId=clipId;Report=report;}
    internal FbxModelAuthoringImportResult SourceModel{get;}
    internal RiggingJobToken Token{get;}
    public FbxModelAuthoringImportResult? PreviewModel{get;}
    public Guid ClipId{get;}
    public DerivedMotionReport Report{get;}
    public bool CanApply=>PreviewModel is not null&&Report.CanExport;
}

/// <summary>Creates distinct portable derived clips from immutable absolute-local FBX source animation.</summary>
public static class FbxDerivedMotionAuthoring
{
    public const string StaleDiagnosticCode="derived_motion_stale";
    public const string ReviewDiagnosticCode="derived_motion_review";
    public static FbxDerivedMotionPreview Preview(FbxModelAuthoringImportResult model,Guid sourceClipId,string name,int sampleMultiplier=2,
        CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(model);ArgumentException.ThrowIfNullOrWhiteSpace(name);cancellationToken.ThrowIfCancellationRequested();
        var document=model.Package.Document;document.Validate();
        var session=document.RiggingSession??throw new InvalidOperationException("Start a studio session before deriving animation.");
        var selection=document.AnimationClips.Single(c=>c.Id==sourceClipId);
        if(selection.DerivedMotion is not null)throw new InvalidOperationException("Choose an original FBX source clip for this derivation path.");
        if(document.AnimationClips.Any(c=>c.DisplayName.Equals(name.Trim(),StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose a distinct name for the derived animation.");
        var source=FbxModelAuthoringImporter.Import(model.Package.SourceFbx.AsSpan(),document.Source.OriginalFileName,new()
        {RigMode=document.AuthoredLayer?.SourceRigMode??CustomModelRigMode.Auto,IgnoreMorphChannels=document.AuthoredLayer?.SourceIgnoreMorphChannels??document.IgnoreMorphChannels},cancellationToken);
        var sourceSelection=source.Package.Document.AnimationClips.SingleOrDefault(c=>c.SourceFingerprint==selection.SourceFingerprint)??
            throw new InvalidOperationException("The selected clip no longer matches the immutable FBX source.");
        if(source.Rig is null||model.Rig is null||!source.AnimationClips.TryGetValue(sourceSelection.Id,out var clip))
            throw new InvalidOperationException("This path requires a decoded source rig and source animation clip.");
        var sourceBones=source.Package.Document.CreateEffectiveBones();var targetBones=document.CreateEffectiveBones();
        var sourceNames=sourceBones.ToDictionary(b=>b.Name,b=>b.Index,StringComparer.Ordinal);
        var sourceIds=sourceBones.Where(b=>b.FbxObjectId!=0).ToDictionary(b=>b.FbxObjectId,b=>b.Index);
        int?[] map=targetBones.Select(b=>b.FbxObjectId!=0&&sourceIds.TryGetValue(b.FbxObjectId,out int byId)?(int?)byId:
            sourceNames.TryGetValue(b.Name,out int byName)?byName:null).ToArray();
        if(map.All(i=>i is null)||clip.TransformTracks.Any(t=>!map.Contains(t.BoneIndex)))
            throw new InvalidOperationException("Every animated source bone needs a retained target identity. Review the source-to-target mapping before deriving motion.");
        var result=DerivedMotionSolver.Derive(source.Rig,sourceBones.Select(b=>b.ExactLocalBindMatrix).ToImmutableArray(),clip,
            model.Rig,targetBones.Select(b=>b.ExactLocalBindMatrix).ToImmutableArray(),map,new(){Name=name.Trim(),SampleMultiplier=sampleMultiplier},cancellationToken);
        Guid id=Guid.NewGuid();
        if(result.Clip is not { } derived||!result.Report.CanExport)return new(model,null,session.CreateJobToken(),id,result.Report);
        var data=new DerivedAnimationData(id,derived.Name,derived.FrameRate,derived.FrameCount,
            derived.TransformTracks.Select(t=>new DerivedTransformTrack(targetBones[t.BoneIndex].Name,t.Keyframes)).ToImmutableArray(),
            derived.ScalarTracks.Select(t=>new DerivedScalarTrack(t.ChannelName,t.Keyframes)).ToImmutableArray(),
            derived.AuxiliaryTransformTracks.Select(t=>new DerivedAuxiliaryTrack(t.Descriptor,t.Keyframes)).ToImmutableArray());
        var payload=DerivedAnimationDataCodec.Serialize(data);string hash=Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan()));
        var metadata=new CustomModelAnimationClip{Id=id,FbxObjectId=0,SourceName=derived.Name,DisplayName=derived.Name,Included=false,
            FrameRate=derived.FrameRate,FrameCount=derived.FrameCount,StartFrame=0,RootMotionMode=selection.RootMotionMode,RootBoneName=selection.RootBoneName,
            SourceFingerprint=hash,HasSkeletalTracks=!derived.TransformTracks.IsEmpty,HasMorphTracks=!derived.ScalarTracks.IsEmpty,FacialSourceValueUnit=selection.FacialSourceValueUnit,
            DerivedMotion=new(){SourceClipId=selection.Id,SourceClipFingerprint=selection.SourceFingerprint,SourceFileSha256=document.Source.ContentSha256,
                SourceRigSignature=source.Package.Document.RigSignature,TargetRigSignature=document.RigSignature,AlgorithmId=result.Report.Algorithm,
                SampleMultiplier=sampleMultiplier,PayloadLength=payload.Length,PayloadSha256=hash,MaximumPositionError=result.Report.MaximumPositionError,
                MaximumAngularError=result.Report.MaximumAngularErrorRadians,MaximumLinearError=result.Report.MaximumLinearError}};
        var changed=RiggingSessions.Change(session,session,RiggingEditKind.Motion);
        var candidateDocument=document with{AnimationClips=document.AnimationClips.Add(metadata),RiggingSession=changed,LastBuildReceipt=null,
            Diagnostics=document.Diagnostics.Where(d=>d.Code!=ReviewDiagnosticCode).Append(new CustomModelImportDiagnostic{Code=ReviewDiagnosticCode,
                Severity=CustomModelImportSeverity.Warning,Message="Derived motion is a separate sampled FBX compatibility asset. Review native component ownership, root policy, facial behavior and runtime events/IK before acceptance."}).ToImmutableArray()};
        var candidate=model with{Package=model.Package with{Document=candidateDocument,DerivedAnimationPayloads=model.Package.DerivedAnimationPayloads.Add(id,payload)},
            AnimationClips=model.AnimationClips.Add(id,derived)};
        candidateDocument.Validate();return new(model,candidate,session.CreateJobToken(),id,result.Report);
    }

    public static bool TryApply(FbxModelAuthoringImportResult current,FbxDerivedMotionPreview preview,out FbxModelAuthoringImportResult result)
    {
        ArgumentNullException.ThrowIfNull(current);ArgumentNullException.ThrowIfNull(preview);result=current;
        if(!ReferenceEquals(current,preview.SourceModel)||current.Package.Document.RiggingSession?.Matches(preview.Token)!=true)return false;
        if(!preview.CanApply)throw new InvalidOperationException("Resolve the derived motion report before saving this clip.");
        result=preview.PreviewModel!;return true;
    }

    public static FbxModelAuthoringImportResult RestoreAvailability(FbxModelAuthoringImportResult model)
    {
        var document=model.Package.Document;
        if(!document.AnimationClips.Any(c=>c.DerivedMotion is not null)&&!document.Diagnostics.Any(d=>d.Code==StaleDiagnosticCode))return model;
        var clips=model.AnimationClips.ToBuilder();var stale=new List<string>();
        foreach(var selection in document.AnimationClips.Where(c=>c.DerivedMotion is not null))
        {
            var data=ReadPayload(model.Package,selection);
            if(!IsCurrent(document,selection)) {clips.Remove(selection.Id);stale.Add(selection.DisplayName);continue;}
            var indices=document.CreateEffectiveBones().ToDictionary(b=>b.Name,b=>b.Index,StringComparer.Ordinal);
            if(data.TransformTracks.Any(t=>!indices.ContainsKey(t.BoneName))){clips.Remove(selection.Id);stale.Add(selection.DisplayName);continue;}
            clips[selection.Id]=new AnimationClip(data.Name,data.FrameRate,data.FrameCount,
                data.TransformTracks.Select(t=>new TransformTrack(indices[t.BoneName],t.Keyframes)),
                data.ScalarTracks.Select(t=>new ScalarTrack(t.ChannelName,t.Keyframes)),data.AuxiliaryTracks.Select(t=>new AuxiliaryTransformTrack(t.Descriptor,t.Keyframes)));
        }
        var diagnostics=document.Diagnostics.Where(d=>d.Code!=StaleDiagnosticCode).ToImmutableArray();
        if(stale.Count>0)diagnostics=diagnostics.Add(new(){Code=StaleDiagnosticCode,Severity=CustomModelImportSeverity.Warning,
            Message="Derived clips retained as historical data but unavailable for this source/rig: "+string.Join(", ",stale)+". Derive and review a new version before export."});
        return model with{Package=model.Package with{Document=document with{Diagnostics=diagnostics}},AnimationClips=clips.ToImmutable()};
    }

    public static FbxDerivedMotionPreview? RefreshMetadata(FbxDerivedMotionPreview preview,FbxModelAuthoringImportResult current)
    {
        ArgumentNullException.ThrowIfNull(preview);ArgumentNullException.ThrowIfNull(current);
        var before=preview.SourceModel;var a=before.Package.Document;var b=current.Package.Document;
        if(a.RiggingSession is not { } oldSession||b.RiggingSession is not { } newSession||!newSession.Matches(preview.Token)||
            oldSession with{Stage=newSession.Stage}!=newSession||a with{RiggingSession=newSession}!=b||before.Surfaces!=current.Surfaces||
            before.AnimationClips!=current.AnimationClips||!ReferenceEquals(before.Rig,current.Rig)||before.Package.SourceFbx!=current.Package.SourceFbx||
            before.Package.AuthoredLayerPayload!=current.Package.AuthoredLayerPayload||before.Package.DerivedAnimationPayloads!=current.Package.DerivedAnimationPayloads)return null;
        var candidate=preview.PreviewModel is { } model?model with{Package=model.Package with{Document=model.Package.Document with
            {RiggingSession=RiggingSessions.Navigate(model.Package.Document.RiggingSession!,newSession.Stage)}}}:null;
        return new(current,candidate,preview.Token,preview.ClipId,preview.Report);
    }

    public static void ValidateExport(FbxModelAuthoringImportResult model,CustomModelAnimationClip selection)
    {
        var authoritative=model.Package.Document.AnimationClips.SingleOrDefault(c=>c.Id==selection.Id);
        if(authoritative?.DerivedMotion is null&&selection.DerivedMotion is null)return;
        if(authoritative?.DerivedMotion is null||!IsCurrent(model.Package.Document,authoritative))
            throw new InvalidOperationException("The derived animation belongs to a different source or target rig. Re-derive it before export.");
        var data=ReadPayload(model.Package,authoritative);
        var document=model.Package.Document;var observed=RiggingSessions.ObserveSourceHierarchy(document);
        var bones=document.CreateEffectiveBones().ToDictionary(b=>b.Name);
        var policies=document.RiggingSession!.Recipe.ComponentPolicies.ToDictionary(p=>p.EntityId);
        foreach(var track in data.TransformTracks)
        {
            if(!bones.TryGetValue(track.BoneName,out var bone))throw new InvalidDataException($"Derived motion references missing bone '{track.BoneName}'.");
            var bind=bone.LocalBindTransform;RigAnimationComponents required=RigAnimationComponents.None;
            if(track.Keyframes.Any(k=>(k.Value.Translation-bind.Translation).Length>1e-8))required|=RigAnimationComponents.Position;
            if(track.Keyframes.Any(k=>Math.Abs(QuaternionDot(k.Value.Rotation,bind.Rotation))<1-1e-10))required|=RigAnimationComponents.Rotation;
            if(track.Keyframes.Any(k=>(k.Value.Scale-bind.Scale).Length>1e-8))required|=RigAnimationComponents.Scale;
            if(required==RigAnimationComponents.None)continue;
            if(!policies.TryGetValue(observed[bone.Index].EntityId,out var policy)||policy.EmittedMask is not { } mask||(mask&required)!=required)
                throw new InvalidOperationException($"Derived motion on '{bone.Name}' requires explicit {required} components. Resolve its POS/ROT/SCL export policy without suppressing the animated values.");
        }
    }
    private static double QuaternionDot(ReAnimated.Core.Mathematics.QuaternionD a,ReAnimated.Core.Mathematics.QuaternionD b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z+a.W*b.W;
    private static bool IsCurrent(CustomModelDocument document,CustomModelAnimationClip selection)=>selection.DerivedMotion is { } d&&
        d.TargetRigSignature.Equals(document.RigSignature,StringComparison.OrdinalIgnoreCase)&&d.SourceFileSha256.Equals(document.Source.ContentSha256,StringComparison.OrdinalIgnoreCase);
    private static DerivedAnimationData ReadPayload(CustomModelPackage package,CustomModelAnimationClip selection)
    {
        var reference=selection.DerivedMotion!;reference.Validate();
        if(!package.DerivedAnimationPayloads.TryGetValue(selection.Id,out var payload)||payload.Length!=reference.PayloadLength||
            !Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())).Equals(reference.PayloadSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The derived animation payload is missing or does not match its recorded hash.");
        var data=DerivedAnimationDataCodec.Deserialize(payload.AsSpan());
        if(data.ClipId!=selection.Id||data.FrameCount!=selection.FrameCount)throw new InvalidDataException("The derived clip metadata and payload do not match.");
        return data;
    }
}
