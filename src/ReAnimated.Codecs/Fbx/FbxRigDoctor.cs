using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Caller-selected contact requirements. They are not a built-in native profile.</summary>
public sealed record RigDoctorContactRule
{
    public string RoleId { get; init; } = string.Empty;
    public string HelperName { get; init; } = string.Empty;
    public string ParentName { get; init; } = string.Empty;
    public bool Required { get; init; } = true;
    public string? ComponentId { get; init; }
    public double MinimumParentWeight { get; init; } = .5;
    public ImmutableArray<double> Up { get; init; } = [0,1,0];
    public ImmutableArray<double> Forward { get; init; } = [0,0,1];
    public bool DirectionsInParentSpace { get; init; } = true;
    public ContactOriginMode Origin { get; init; } = ContactOriginMode.ParentPivot;
    public ContactAxisMode Axes { get; init; } = ContactAxisMode.ExplicitDirections;
    public double BottomBandFraction { get; init; } = .2;
    public ImmutableArray<double> BoundsCenter { get; init; } = [];
    public ImmutableArray<double> BoundsHalfExtents { get; init; } = [];
    public RigAnimationComponents? Components { get; init; }
    public RigAnimationLod? AnimationLod { get; init; }
    public string? ComponentRuleId { get; init; }
    public string? LodRuleId { get; init; }
    public RigEvidenceReference Evidence { get; init; } = new();
}

public enum RigDoctorRowStatus { Present, Proposed, NeedsReview, NotRequired }
public sealed record RigDoctorRow(string RoleId, string HelperName, string ParentName,
    RigDoctorRowStatus Status, string Message, string? ComponentId = null,
    int SelectedPointCount = 0, ContactFootprintFit? Fit = null,
    Vector3D? BoundsCenter = null, Vector3D? BoundsHalfExtents = null);

public sealed class RigDoctorPreview
{
    internal RigDoctorPreview(FbxModelAuthoringImportResult source, FbxModelAuthoringImportResult? candidate,
        ImmutableArray<RigDoctorRow> rows)
    { Source=source; Candidate=candidate; Rows=rows; Token=source.Package.Document.RiggingSession!.CreateJobToken(); }
    internal FbxModelAuthoringImportResult Source { get; }
    internal RiggingJobToken Token { get; }
    public FbxModelAuthoringImportResult? Candidate { get; }
    public ImmutableArray<RigDoctorRow> Rows { get; }
    public int RepairCount => Rows.Count(static row=>row.Status==RigDoctorRowStatus.Proposed);
    public bool IsNoOp => Candidate is not null && RepairCount==0;
    public bool CanApply => Candidate is not null && RepairCount>0;
}

/// <summary>
/// Audits supplied contact rules and prepares one immutable missing-helper repair.
/// Existing helpers are audited against fitted rules but never changed implicitly.
/// Frame/retention choices are caller evidence and remain separate from native proof.
/// </summary>
public static class FbxRigDoctor
{
    public static RigDoctorPreview Preview(FbxModelAuthoringImportResult model,
        ImmutableArray<RigDoctorContactRule> rules, CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(model); model.Package.Document.Validate();
        if (rules.IsDefaultOrEmpty || rules.Length>64) throw new ArgumentException("Provide one to 64 contact rules.",nameof(rules));
        if (rules.Select(static r=>r.RoleId).Distinct(StringComparer.Ordinal).Count()!=rules.Length ||
            rules.Select(static r=>r.HelperName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rules.Length)
            throw new ArgumentException("Repair rules contain duplicate roles or competing helper names.",nameof(rules));
        var document=model.Package.Document;
        var session=document.RiggingSession??throw new InvalidOperationException("Start a studio session before running Rig Doctor.");
        if (!session.MatchesSource(document.Source.ContentSha256)) throw new InvalidOperationException("Review the changed source before repairing its runtime nodes.");
        var bones=document.CreateEffectiveBones();
        var observed=RiggingSessions.ObserveSourceHierarchy(document);
        var globals=new TransformMatrix[bones.Length];
        foreach(var bone in bones) globals[bone.Index]=bone.ParentIndex<0?bone.ExactLocalBindMatrix:globals[bone.ParentIndex]*bone.ExactLocalBindMatrix;
        var used=UsedBones(model,bones.Length,cancellationToken);
        var rows=ImmutableArray.CreateBuilder<RigDoctorRow>();
        var proposals=new List<(RigDoctorContactRule Rule, ContactAuthoringSnapshot Snapshot, Vector3D Center, Vector3D Half)>();
        foreach(var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested(); ValidateRule(rule);
            var matches=bones.Where(b=>b.Name.Equals(rule.HelperName,StringComparison.OrdinalIgnoreCase)).ToArray();
            var parents=document.Bones.Where(b=>b.Name==rule.ParentName).ToArray();
            CustomModelBone? existingHelper=null;
            if(matches.Length!=0)
            {
                var helper=matches[0];
                bool present=matches.Length==1 && helper.Name==rule.HelperName && helper.Kind==BoneKind.Helper &&
                    !used.Contains(helper.Index) && parents.Length==1 && helper.ParentIndex==parents[0].Index;
                if(!present)
                { rows.Add(Review(rule,"Existing node conflicts with the required spelling, type, skin use or parent. Review it explicitly; no replacement will be created.")); continue; }
                existingHelper=helper;
            }
            if(existingHelper is null&&!rule.Required)
            { rows.Add(new(rule.RoleId,rule.HelperName,rule.ParentName,RigDoctorRowStatus.NotRequired,"Optional absent helper retained as absent.")); continue; }
            if(parents.Length!=1 || parents[0].Kind!=BoneKind.Deform)
            { rows.Add(Review(rule,"The required deformation parent is missing or ambiguous.")); continue; }
            if(rule.Components is null || rule.AnimationLod is null || string.IsNullOrWhiteSpace(rule.ComponentRuleId) ||
                string.IsNullOrWhiteSpace(rule.LodRuleId) || rule.Evidence.ArtifactSha256 is null)
            { rows.Add(Review(rule,"Explicit component/LOD choices and a rule evidence hash are required before proposing this repair.")); continue; }
            var parent=parents[0]; var up=Vector(rule.Up);var forward=Vector(rule.Forward);
            if(rule.DirectionsInParentSpace)
            { up=globals[parent.Index].TransformDirection(up);forward=globals[parent.Index].TransformDirection(forward); }
            var options=new ContactFootprintOptions { Up=up,Forward=forward,Origin=rule.Origin,Axes=rule.Axes,BottomBandFraction=rule.BottomBandFraction };
            var components=model.Surfaces.Where(static s=>s.SourceGeometry is not null).Select(static s=>s.SourceGeometry!.Id)
                .Distinct(StringComparer.Ordinal).Where(id=>rule.ComponentId is null || id==rule.ComponentId).ToArray();
            var fits=new List<ContactAuthoringSnapshot>();var failures=new List<string>();
            foreach(string component in components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var fit=FbxContactAuthoring.Inspect(model,observed[parent.Index].EntityId,component,rule.MinimumParentWeight,null,options,cancellationToken);
                    if(fit.Fit.OrientationAmbiguous) { failures.Add("Footprint direction needs explicit review.");continue; }
                    fits.Add(fit);
                }
                catch(ArgumentException error) { failures.Add(error.Message); }
            }
            if(fits.Count!=1)
            {
                rows.Add(Review(rule,fits.Count>1?"Several geometry components fit this parent. Choose a component explicitly in the rule before repair.":
                    "No unambiguous weighted footprint was found. "+string.Join(" ",failures.Distinct().Take(2))));
                continue;
            }
            var snapshot=fits[0];
            var center=rule.BoundsCenter.IsEmpty?snapshot.Fit.BoundsCenter:Vector(rule.BoundsCenter);
            var half=rule.BoundsHalfExtents.IsEmpty?snapshot.Fit.BoundsHalfExtents:Vector(rule.BoundsHalfExtents);
            if(half.X<=0 || half.Y<=0 || half.Z<=0)
            { rows.Add(Review(rule,"Contact bounds have no positive volume. Supply reviewed extents."));continue; }
            if(existingHelper is not null)
            {
                Guid id=observed[existingHelper.Index].EntityId;
                var recipe=session.Recipe.Helpers.SingleOrDefault(h=>h.EntityId==id);
                var policy=session.Recipe.ComponentPolicies.SingleOrDefault(p=>p.EntityId==id);
                var local=recipe?.LocalFrame??existingHelper.ExactLocalBindMatrix;
                bool valid=local.NearlyEquals(snapshot.Fit.LocalFrame,1e-7)&&recipe?.BoundsCenter is { } oldCenter&&
                    recipe.BoundsHalfExtents is { } oldHalf&&(oldCenter-center).Length<=1e-7&&(oldHalf-half).Length<=1e-7&&
                    policy?.EmittedMask==rule.Components&&policy?.AnimationLod==rule.AnimationLod;
                rows.Add(new(rule.RoleId,rule.HelperName,rule.ParentName,valid?RigDoctorRowStatus.Present:RigDoctorRowStatus.NeedsReview,
                    valid?"Existing frame, bounds, parent and component/LOD choices match the selected rules and were retained. Native behavior remains separate.":
                    "Existing helper frame, bounds or component/LOD data differs or is unavailable. Review its existing settings; no duplicate or implicit correction will be made.",
                    snapshot.ComponentId,snapshot.ControlPointIds.Length,snapshot.Fit,center,half));
                continue;
            }
            proposals.Add((rule,snapshot,center,half));
            rows.Add(new(rule.RoleId,rule.HelperName,rule.ParentName,RigDoctorRowStatus.Proposed,
                "Missing helper: geometry-derived placement is ready for review. Existing weighted data and clips are retained.",
                snapshot.ComponentId,snapshot.ControlPointIds.Length,snapshot.Fit,center,half));
        }
        var completed=rows.ToImmutable();
        if(completed.Any(static row=>row.Status==RigDoctorRowStatus.NeedsReview))return new(model,null,completed);
        if(proposals.Count==0)return new(model,model,completed);
        var candidate=model;
        foreach(var proposal in proposals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule=proposal.Rule;
            var changed=RigContactHelperAuthoring.Apply(candidate.Package.Document,candidate.Package.Document.RiggingSession!.CreateJobToken(),
                proposal.Snapshot.ParentEntityId,null,rule.HelperName,rule.RoleId,proposal.Snapshot.Fit.LocalFrame,proposal.Center,proposal.Half,
                RigEvidenceKind.UserOverride,"Rig Doctor reviewed contact proposal; rule evidence "+rule.Evidence.Id+"; component "+proposal.Snapshot.ComponentId+"; native behavior unverified");
            var changedSession=changed.RiggingSession!;
            Guid id=changedSession.Recipe.Helpers.Single(h=>h.RoleId==rule.RoleId &&
                changedSession.Recipe.Entities.Any(e=>e.EntityId==h.EntityId&&e.NativeName==rule.HelperName)).EntityId;
            var evidence=ImmutableArray.Create(rule.Evidence);
            var policy=new AnimationComponentPolicy { EntityId=id,EmittedMask=rule.Components,AnimationLod=rule.AnimationLod,LodRuleId=rule.LodRuleId,LodEvidence=evidence,
                Position=new(){Owners=[RigComponentOwner.BindInherited],CompositionRuleId=rule.ComponentRuleId,Evidence=evidence},
                Rotation=new(){Owners=[RigComponentOwner.BindInherited],CompositionRuleId=rule.ComponentRuleId,Evidence=evidence},
                Scale=new(){Owners=[RigComponentOwner.BindInherited],CompositionRuleId=rule.ComponentRuleId,Evidence=evidence} };
            changed=changed with {RiggingSession=changedSession with {Recipe=changedSession.Recipe with
                {ComponentPolicies=changedSession.Recipe.ComponentPolicies.Add(policy)}}};
            changed.Validate(); candidate=candidate with {Package=candidate.Package with{Document=changed},Rig=changed.CreateRigDefinition()};
        }
        // Existing physical rows are retained by append-only contact insertion.
        // Recheck this invariant rather than silently relying on materializer order.
        var after=RiggingSessions.ObserveSourceHierarchy(candidate.Package.Document);
        if(!after.Take(observed.Length).SequenceEqual(observed))throw new InvalidOperationException("Repair would reorder existing runtime rows; a full palette/track transaction is required.");
        return new(model,candidate,completed);
    }

    public static bool TryApply(FbxModelAuthoringImportResult current,RigDoctorPreview preview,bool reviewed,out FbxModelAuthoringImportResult result)
    {
        ArgumentNullException.ThrowIfNull(current);ArgumentNullException.ThrowIfNull(preview);result=current;
        if(!ReferenceEquals(current,preview.Source) || current.Package.Document.RiggingSession?.Matches(preview.Token)!=true)return false;
        if(preview.IsNoOp)return true;
        if(!reviewed || !preview.CanApply)throw new InvalidOperationException("Review all proposed repairs and resolve all conflicting rows before applying.");
        result=preview.Candidate!;return true;
    }

    public static RigDoctorPreview? RefreshMetadata(RigDoctorPreview preview,FbxModelAuthoringImportResult current)
    {
        var before=preview.Source;var a=before.Package.Document;var b=current.Package.Document;
        if(a.RiggingSession is not { } old || b.RiggingSession is not { } next || !next.Matches(preview.Token) ||
            old with{Stage=next.Stage}!=next || a with{RiggingSession=next}!=b || before.Surfaces!=current.Surfaces ||
            before.AnimationClips!=current.AnimationClips || !ReferenceEquals(before.Rig,current.Rig) || before.Package.SourceFbx!=current.Package.SourceFbx)return null;
        var candidate=preview.IsNoOp?current:preview.Candidate is { } model?model with{Package=model.Package with{Document=model.Package.Document with
            {RiggingSession=RiggingSessions.Navigate(model.Package.Document.RiggingSession!,next.Stage)}}}:null;
        return new(current,candidate,preview.Rows);
    }

    private static RigDoctorRow Review(RigDoctorContactRule rule,string message)=>new(rule.RoleId,rule.HelperName,rule.ParentName,RigDoctorRowStatus.NeedsReview,message);
    private static Vector3D Vector(ImmutableArray<double> values)=>new(values[0],values[1],values[2]);
    private static void ValidateRule(RigDoctorContactRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);ArgumentException.ThrowIfNullOrWhiteSpace(rule.RoleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.HelperName);ArgumentException.ThrowIfNullOrWhiteSpace(rule.ParentName);
        foreach(var values in new[]{rule.Up,rule.Forward})
            if(values.IsDefault || values.Length!=3 || values.Any(static n=>!double.IsFinite(n)))throw new ArgumentException("Contact directions require three finite values.");
        foreach(var values in new[]{rule.BoundsCenter,rule.BoundsHalfExtents})
            if(values.IsDefault || values.Length is not (0 or 3) || values.Any(static n=>!double.IsFinite(n)))throw new ArgumentException("Contact bounds require zero or three finite values.");
        if(!Enum.IsDefined(rule.Origin)||!Enum.IsDefined(rule.Axes)||rule.AnimationLod is { } lod&&!Enum.IsDefined(lod))throw new ArgumentException("Unknown contact rule mode.");
        if(rule.Components is { } mask&&(mask&~(RigAnimationComponents.Position|RigAnimationComponents.Rotation|RigAnimationComponents.Scale))!=0)throw new ArgumentException("Unknown component mask.");
        if(!double.IsFinite(rule.MinimumParentWeight)||rule.MinimumParentWeight is <=0 or >1||!double.IsFinite(rule.BottomBandFraction)||rule.BottomBandFraction is <=0 or >1)throw new ArgumentException("Contact selection thresholds must be within (0,1].");
        rule.Evidence.Validate();
    }

    private static HashSet<int> UsedBones(FbxModelAuthoringImportResult model,int boneCount,CancellationToken token)
    {
        var used=new HashSet<int>();
        foreach(var surface in model.Surfaces.Where(static s=>s.IsSkinned))
        {
            if(surface.PaletteBoneIndices.Length!=surface.InverseBindMatrices.Length)throw new InvalidDataException("Skin palette/reference counts differ.");
            foreach(var vertex in surface.Vertices)
            {
                token.ThrowIfCancellationRequested();
                if(vertex.BoneIndices.Length!=vertex.BoneWeights.Length)throw new InvalidDataException("Skin influence counts differ.");
                for(int i=0;i<vertex.BoneIndices.Length;i++)
                {
                    double weight=vertex.BoneWeights[i];int slot=vertex.BoneIndices[i];
                    if(!double.IsFinite(weight)||weight<0||(uint)slot>=(uint)surface.PaletteBoneIndices.Length)throw new InvalidDataException("A skin influence is invalid.");
                    int bone=surface.PaletteBoneIndices[slot];
                    if((uint)bone>=(uint)boneCount)throw new InvalidDataException("A skin influence references an absent node.");
                    if(weight>0)used.Add(bone);
                }
            }
        }
        return used;
    }
}
