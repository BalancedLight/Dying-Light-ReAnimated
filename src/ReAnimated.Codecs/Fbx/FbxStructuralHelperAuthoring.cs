using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

public sealed record StructuralNodeReview(Guid EntityId, int SourceIndex, string Name, string ParentName, BoneKind Kind,
    int WeightedCorners, bool WeightedBranch, string Roles, string FrameRule, string ChannelRule, int AuthoredTrackCount,
    RigHelperEditFields Locks, TransformMatrix PreparedFrame, bool CanEditHelper, bool CanEditRest, bool CanProtect)
{
    public bool CanAddRetentionHelper { get; init; }
    public bool IsRetentionHelper { get; init; }
    public string RetentionStatus => IsRetentionHelper
        ? "This is a saved retention helper. Removal checks model and project dependencies and must be reviewed before Apply."
        : CanAddRetentionHelper
        ? "This bone's prepared branch has no mesh influences or helper dependencies. A retention helper can be proposed; compiled retention remains unverified."
        : "No retention helper proposal is needed for this selection's observed dependencies. This is not compiled-retention proof.";
    public string Eligibility => Name.Equals("EyeCamera", StringComparison.OrdinalIgnoreCase) || Name.Equals("RefCamera", StringComparison.OrdinalIgnoreCase) || Kind == BoneKind.Camera
        ? "Use the camera calibration workflow."
        : CanEditRest ? "Use the surface-preserving joint rest editor."
        : CanEditHelper ? "Unweighted helper branch: reviewed frame edits are available."
        : "Frame editing is unavailable: weighted helper branches require a coherent skin/rest transaction.";
}

public sealed class StructuralHelperPreview
{
    internal StructuralHelperPreview(FbxModelAuthoringImportResult source, FbxModelAuthoringImportResult candidate, Guid entityId, string summary)
    { Source = source; Candidate = candidate; EntityId = entityId; Summary = summary; }
    internal FbxModelAuthoringImportResult Source { get; }
    public FbxModelAuthoringImportResult Candidate { get; }
    public Guid EntityId { get; }
    public string Summary { get; }
    public Guid? AddedHelperId { get; init; }
    public Guid? RemovedHelperId { get; init; }
    public bool HasChanges => !ReferenceEquals(Source, Candidate);
}

/// <summary>Observed usage and explicit frame/protection edits, never name-inferred native drivers.</summary>
public static class FbxStructuralHelperAuthoring
{
    public static ImmutableArray<StructuralNodeReview> Inspect(FbxModelAuthoringImportResult model, CancellationToken token = default) => Inspect(model, null, token);

    internal static ImmutableArray<StructuralNodeReview> Inspect(FbxModelAuthoringImportResult model, Dl1PreparedAuthoredRig? preparedRig, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(model); token.ThrowIfCancellationRequested();
        var doc = model.Package.Document; doc.Validate();
        if (model.Rig is null || doc.RiggingSession is not { } session) return [];
        var bones = doc.CreateEffectiveBones(); var observed = RiggingSessions.ObserveSourceHierarchy(doc);
        var used = new int[bones.Length]; var seen = new HashSet<int>();
        foreach (var surface in model.Surfaces.Where(s => s.IsSkinned))
        {
            if (surface.PaletteBoneIndices.Length != surface.InverseBindMatrices.Length)
                throw new InvalidDataException("Structural inspection needs complete skin palette/reference data.");
            for (int vertexIndex = 0; vertexIndex < surface.Vertices.Length; vertexIndex++)
            {
                if ((vertexIndex & 1023) == 0) token.ThrowIfCancellationRequested();
                var vertex = surface.Vertices[vertexIndex]; seen.Clear();
                if (vertex.BoneIndices.Length != vertex.BoneWeights.Length)
                    throw new InvalidDataException("Structural inspection found mismatched influences.");
                double total = 0;
                for (int i = 0; i < vertex.BoneIndices.Length; i++)
                {
                    int slot = vertex.BoneIndices[i]; double weight = vertex.BoneWeights[i];
                    if ((uint)slot >= (uint)surface.PaletteBoneIndices.Length || !double.IsFinite(weight) || weight < 0)
                        throw new InvalidDataException("Structural inspection found an invalid skin influence.");
                    int bone = surface.PaletteBoneIndices[slot];
                    if ((uint)bone >= (uint)bones.Length) throw new InvalidDataException("A skin palette references an absent node.");
                    if (weight > 0 && seen.Add(bone)) used[bone]++;
                    total += weight;
                }
                if (!double.IsFinite(total) || total <= 0) throw new InvalidDataException("A skinned corner has no positive finite weights.");
            }
        }
        var branch = bones.Select((b,i) => b.IsWeighted || used[i] > 0).ToArray();
        for (int i = bones.Length - 1; i >= 0; i--) if (bones[i].ParentIndex >= 0) branch[bones[i].ParentIndex] |= branch[i];
        var prepared = preparedRig ?? Dl1CustomModelRigPreparer.Prepare(model, token);
        var bySource = prepared.Contract.Nodes.ToDictionary(n => n.SourceBoneIndex);
        var dependencies = prepared.Contract.Nodes.Select(n => used[n.SourceBoneIndex] > 0 || !n.IsDeform).ToArray();
        for (int i = dependencies.Length - 1; i >= 0; i--)
            if (prepared.Contract.Nodes[i].ParentPhysicalIndex >= 0)
                dependencies[prepared.Contract.Nodes[i].ParentPhysicalIndex] |= dependencies[i];
        return bones.Select(b =>
        {
            Guid id = observed[b.Index].EntityId;
            var helper = session.Recipe.Helpers.FirstOrDefault(h => h.EntityId == id);
            var policy = session.Recipe.FramePolicies.FirstOrDefault(p => p.EntityId == id);
            var channels = session.Recipe.ComponentPolicies.FirstOrDefault(p => p.EntityId == id);
            var roles = session.Recipe.Assignments.Where(a => a.EntityId == id).Select(a => a.RoleId).ToList();
            if (helper is not null && !roles.Contains(helper.RoleId)) roles.Add(helper.RoleId);
            bool camera = RigCameraHelperAuthoring.IsCamera(b) || b.Name.Equals("EyeCamera",StringComparison.OrdinalIgnoreCase) || b.Name.Equals("RefCamera",StringComparison.OrdinalIgnoreCase);
            return new StructuralNodeReview(id,b.Index,b.Name,b.ParentIndex < 0 ? "World" : bones[b.ParentIndex].Name,b.Kind,used[b.Index],branch[b.Index],
                roles.Count == 0 ? "Unclassified; native role unverified" : string.Join(", ",roles),
                helper is not null ? $"{helper.FramePolicy}; {(helper.FollowPreparedParent ? "prepared" : "source")} parent basis" : policy?.FramePolicy.ToString() ?? "PreserveSource (default)",
                channels is null ? "No declared channel/LOD ownership" : $"P:{Owners(channels.Position)} R:{Owners(channels.Rotation)} S:{Owners(channels.Scale)}; mask {channels.EmittedMask}; LOD {channels.AnimationLod}",
                model.AnimationClips.Values.Sum(c => c.TransformTracks.Count(t => t.BoneIndex == b.Index)),helper?.LockedFields ?? RigHelperEditFields.None,
                bySource[b.Index].GlobalBindMatrix, !camera && !branch[b.Index] && b.Kind is BoneKind.Helper or BoneKind.Prop,
                !camera && b.Index < doc.Bones.Length && b.Kind is BoneKind.Root or BoneKind.Deform, b.ParentIndex >= 0)
            { CanAddRetentionHelper = bySource[b.Index].IsDeform && !dependencies[bySource[b.Index].PhysicalIndex],
                IsRetentionHelper = helper?.RoleId == FbxCompilerRetentionAuthoring.RoleId };
        }).ToImmutableArray();
        static string Owners(RigChannelOwnership c) => c.Owners.IsEmpty ? "unresolved" : string.Join("+",c.Owners);
    }

    public static StructuralHelperPreview PreviewProtection(FbxModelAuthoringImportResult model, Guid entityId,
        RigHelperEditFields fields, CancellationToken token = default)
    {
        StructuralNodeReview row = Inspect(model,token).Single(r => r.EntityId == entityId);
        if (!row.CanProtect) throw new InvalidOperationException("World-root protection needs a separate root-frame policy.");
        if ((fields & ~RigHelperEditFields.All) != 0) throw new ArgumentOutOfRangeException(nameof(fields));
        var doc=model.Package.Document; var session=doc.RiggingSession!;
        HelperRecipe? existing=session.Recipe.Helpers.FirstOrDefault(h=>h.EntityId==entityId);
        if ((existing?.LockedFields ?? RigHelperEditFields.None)==fields) return new(model,model,entityId,"Protection is unchanged.");
        HelperRecipe recipe = (existing ?? CapturePreparedRecipe(model,entityId,token)) with { LockedFields = fields };
        var changed = ApplyRecipe(model,recipe,allowUnlock:true);
        _ = Dl1CustomModelRigPreparer.Prepare(changed, token);
        FbxProfileEditGuard.RequireAllowed(model, changed, token);
        return new(model,changed,entityId,"Updated explicit frame/name/parent/bounds/channel protection. Native role and driver semantics remain unverified.");
    }

    public static StructuralHelperPreview PreviewOffset(FbxModelAuthoringImportResult model, Guid entityId,
        TransformTRS offset, CancellationToken token = default)
    {
        StructuralNodeReview row = Inspect(model,token).Single(r => r.EntityId == entityId);
        if (!row.CanEditHelper) throw new InvalidOperationException(row.Eligibility);
        if (!offset.IsFinite || offset.Scale != Vector3D.One) throw new ArgumentException("Helper offsets must be finite rigid transforms.",nameof(offset));
        var session=model.Package.Document.RiggingSession!;
        HelperRecipe existing=session.Recipe.Helpers.FirstOrDefault(h=>h.EntityId==entityId) ?? CapturePreparedRecipe(model,entityId,token);
        if (offset.ToMatrix().NearlyEquals(TransformMatrix.Identity,1e-12)) return new(model,model,entityId,"Frame is unchanged.");
        var edited=existing with { LocalFrame=existing.LocalFrame*offset.ToMatrix(),
            FramePolicy=existing.FramePolicy==RigFramePolicy.PreserveSource?RigFramePolicy.Manual:existing.FramePolicy,
            PlacementProvenance=RigEvidenceKind.UserOverride,UserApproved=true };
        var candidate = ApplyRecipe(model,edited,allowUnlock:false);
        _ = Dl1CustomModelRigPreparer.Prepare(candidate, token);
        FbxProfileEditGuard.RequireAllowed(model, candidate, token);
        return new(model,candidate,entityId,
            "Reviewed unweighted helper frame edit; source geometry, skinning, morphs and clips are retained. Native driver behavior needs separate review.");
    }

    private static HelperRecipe CapturePreparedRecipe(FbxModelAuthoringImportResult model,Guid id,CancellationToken token)
    {
        var doc=model.Package.Document;var session=doc.RiggingSession!;
        var observed=RiggingSessions.ObserveSourceHierarchy(doc);var bones=doc.CreateEffectiveBones();
        int index=Enumerable.Range(0,observed.Length).Single(i=>observed[i].EntityId==id);
        int parent=bones[index].ParentIndex;if(parent<0)throw new InvalidOperationException("Select a node with a parent.");
        var globals=new TransformMatrix[bones.Length];
        foreach(var bone in bones)globals[bone.Index]=bone.ParentIndex<0?bone.ExactLocalBindMatrix:globals[bone.ParentIndex]*bone.ExactLocalBindMatrix;
        var node=Dl1CustomModelRigPreparer.Prepare(model,token).Contract.Nodes.Single(n=>n.SourceBoneIndex==index);
        return new(){EntityId=id,OwnerAssetId=doc.ModelId,ParentEntityId=observed[parent].EntityId,
            RoleId=session.Recipe.Assignments.FirstOrDefault(a=>a.EntityId==id)?.RoleId ?? "structure.manual",
            LocalFrame=globals[parent].InvertedAffine()*node.GlobalBindMatrix,FramePolicy=RigFramePolicy.Manual,
            BoundsCenter=node.Bounds.Center,BoundsHalfExtents=node.Bounds.HalfExtents,
            PlacementProvenance=RigEvidenceKind.UserOverride,UserApproved=true,
            Evidence=[new(){Id="structural-frame-capture:"+id.ToString("N"),Kind=RigEvidenceKind.UserOverride,ArtifactSha256=doc.Source.ContentSha256,
                Description="Explicitly captured the current prepared frame and bounds. No native structural driver or weight-eligibility rule is inferred."}]};
    }

    private static FbxModelAuthoringImportResult ApplyRecipe(FbxModelAuthoringImportResult model,HelperRecipe recipe,bool allowUnlock)
    {
        var doc=model.Package.Document;var session=doc.RiggingSession!;var observed=RiggingSessions.ObserveSourceHierarchy(doc);var bones=doc.CreateEffectiveBones();
        var entities=session.Recipe.Entities.Select(e=>
        {
            if(e.Kind!=RigNativeEntityKind.Unknown || e.EntityId!=recipe.EntityId && e.EntityId!=recipe.ParentEntityId)return e;
            int i=Enumerable.Range(0,observed.Length).Single(i=>observed[i].EntityId==e.EntityId);
            return e with{Kind=bones[i].IsWeighted&&bones[i].Kind is BoneKind.Root or BoneKind.Deform?RigNativeEntityKind.Bone:RigNativeEntityKind.Helper};
        }).ToImmutableArray();
        var updated=session with{Recipe=session.Recipe with{Entities=entities,
            Helpers=session.Recipe.Helpers.Where(h=>h.EntityId!=recipe.EntityId).Append(recipe).ToImmutableArray(),
            FramePolicies=session.Recipe.FramePolicies.Where(p=>p.EntityId!=recipe.EntityId).ToImmutableArray()}};
        doc=doc with{RiggingSession=RiggingSessions.Change(session,updated,RiggingEditKind.Helpers,allowLockedChanges:allowUnlock),LastBuildReceipt=null};
        doc=RiggingHelperMaterializer.Apply(doc);doc.Validate();
        return model with{Package=model.Package with{Document=doc},Rig=doc.CreateRigDefinition()};
    }

    public static StructuralHelperPreview? RefreshMetadata(StructuralHelperPreview preview, FbxModelAuthoringImportResult current)
    {
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(current);
        var before = preview.Source; var a = before.Package.Document; var b = current.Package.Document;
        if (a.RiggingSession is not { } oldSession || b.RiggingSession is not { } next || !next.Matches(oldSession.CreateJobToken()) ||
            oldSession with { Stage = next.Stage } != next || a with { RiggingSession = next } != b ||
            before.Surfaces != current.Surfaces || before.AnimationClips != current.AnimationClips || !ReferenceEquals(before.Rig, current.Rig) ||
            before.Package.SourceFbx != current.Package.SourceFbx || before.Package.AuthoredLayerPayload != current.Package.AuthoredLayerPayload ||
            before.Package.TexturePayloads != current.Package.TexturePayloads) return null;
        var candidate = preview.HasChanges ? preview.Candidate with { Package = preview.Candidate.Package with
        { Document = preview.Candidate.Package.Document with { RiggingSession = RiggingSessions.Navigate(preview.Candidate.Package.Document.RiggingSession!, next.Stage) } } } : current;
        return new(current, candidate, preview.EntityId, preview.Summary) { AddedHelperId = preview.AddedHelperId, RemovedHelperId = preview.RemovedHelperId };
    }

    public static bool TryApply(FbxModelAuthoringImportResult current,StructuralHelperPreview preview,out FbxModelAuthoringImportResult result)
    {
        result=current;if(!ReferenceEquals(current,preview.Source))return false;result=preview.Candidate;return true;
    }
}
