using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>An approved parent relationship, checked against the current source hierarchy.</summary>
public sealed record RigParentDecision
{
    public Guid EntityId { get; init; }
    public Guid? ParentEntityId { get; init; }
    public string SourceSha256 { get; init; } = string.Empty;
    public bool UserApproved { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];

    public RigParentDecision() { }

    public RigParentDecision(Guid entityId, Guid? parentEntityId, string sourceSha256,
        bool userApproved, ImmutableArray<RigEvidenceReference> evidence = default) =>
        (EntityId, ParentEntityId, SourceSha256, UserApproved, Evidence) =
        (entityId, parentEntityId, sourceSha256, userApproved, evidence.IsDefault ? [] : evidence);

    public void Validate()
    {
        RigContractRules.Identifier(EntityId, nameof(EntityId));
        if (ParentEntityId is { } parent)
        {
            RigContractRules.Identifier(parent, nameof(ParentEntityId));
            if (parent == EntityId) throw new ArgumentException("A parent decision cannot parent an entity to itself.");
        }
        RigContractRules.Hash(SourceSha256, nameof(SourceSha256));
        RigContractRules.Array(Evidence, nameof(Evidence));
        RigRecipeRules.Evidence(Evidence);
        if (UserApproved && Evidence.IsEmpty)
            throw new ArgumentException("An approved parent decision requires source-backed evidence.");
    }
}

/// <summary>The immutable result of one stable-identity hierarchy reorder.</summary>
public sealed record RigHierarchyEditResult(
    CustomModelDocument Document,
    ImmutableArray<int> OldToNewBoneIndices,
    ImmutableArray<int> OldToNewEffectiveIndices);

/// <summary>
/// Reparents one base node while retaining every effective global bind frame.
/// This is an authoring hierarchy edit and does not claim native validation.
/// </summary>
public static class RigHierarchyAuthoring
{
    public static RigHierarchyEditResult Apply(
        CustomModelDocument document,
        RiggingJobToken token,
        Guid entityId,
        Guid? newParentEntityId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(token);
        document.Validate();
        RiggingSession session = document.RiggingSession ??
            throw new InvalidOperationException("Hierarchy authoring requires a current rigging session.");
        if (!session.Matches(token))
            throw new InvalidOperationException("Hierarchy authoring inputs changed; refresh the model before reparenting.");

        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        int selectedOldIndex = IndexOfEntity(observed, entityId);
        if (selectedOldIndex < 0 || selectedOldIndex >= document.Bones.Length)
            throw new ArgumentException("Hierarchy edits select an observed base bone identity.", nameof(entityId));
        if (document.Bones[selectedOldIndex].Kind is not (BoneKind.Root or BoneKind.Deform or BoneKind.Helper or BoneKind.Camera or BoneKind.Prop))
            throw new ArgumentException("Hierarchy edits require a supported base bone kind.", nameof(entityId));
        if (newParentEntityId is null && session.Eyes.Any(eye => eye.DeformEntityId == entityId))
            throw new InvalidOperationException("A generated eye deform node requires a concrete base parent.");

        int parentOldIndex = -1;
        if (newParentEntityId is { } parentId)
        {
            parentOldIndex = IndexOfEntity(observed, parentId);
            if (parentOldIndex < 0 || parentOldIndex >= document.Bones.Length)
                throw new ArgumentException("A new hierarchy parent must be an observed base bone identity.", nameof(newParentEntityId));
            if (parentOldIndex == selectedOldIndex)
                throw new ArgumentException("A hierarchy node cannot parent itself.", nameof(newParentEntityId));
            if (IsDescendant(document.Bones, selectedOldIndex, parentOldIndex))
                throw new InvalidOperationException("A hierarchy parent cannot be moved below its selected descendant.");
            if (document.Bones[parentOldIndex].Kind is not (BoneKind.Root or BoneKind.Deform or BoneKind.Helper or BoneKind.Camera or BoneKind.Prop))
                throw new ArgumentException("A new hierarchy parent must be a supported base node.", nameof(newParentEntityId));
        }

        ImmutableArray<CustomModelBone> oldBones = document.Bones;
        TransformMatrix[] oldGlobals = Globals(oldBones);
        TransformMatrix[] oldEffectiveGlobals = Globals(document.CreateEffectiveBones());
        int[] oldParents = oldBones.Select(static bone => bone.ParentIndex).ToArray();
        if (oldParents[selectedOldIndex] == parentOldIndex || parentOldIndex < 0 && oldParents[selectedOldIndex] < 0)
            return new RigHierarchyEditResult(document,
                Enumerable.Range(0, oldBones.Length).ToImmutableArray(),
                Enumerable.Range(0, document.CreateEffectiveBones().Length).ToImmutableArray());
        oldParents[selectedOldIndex] = parentOldIndex;

        int[] order = TopologicalOrder(oldParents, oldBones.Length);
        var oldToNew = new int[oldBones.Length];
        for (int index = 0; index < order.Length; index++) oldToNew[order[index]] = index;
        var rebuilt = ImmutableArray.CreateBuilder<CustomModelBone>(oldBones.Length);
        foreach (int oldIndex in order)
        {
            CustomModelBone oldBone = oldBones[oldIndex];
            int newParent = oldParents[oldIndex] < 0 ? -1 : oldToNew[oldParents[oldIndex]];
            TransformMatrix local;
            TransformTRS preview;
            if (oldIndex == selectedOldIndex)
            {
                local = oldParents[oldIndex] < 0
                    ? oldGlobals[oldIndex]
                    : oldGlobals[oldParents[oldIndex]].InvertedAffine() * oldGlobals[oldIndex];
                EnsureFrame(local, "The reparented hierarchy produced an invalid exact local frame.");
                preview = Dl1AuthoredRigContract.ProjectAffineToTrs(local);
            }
            else
            {
                // Reordering changes only physical indices. Every unselected
                // node retains its exact authored local matrix and preview TRS
                // byte-for-byte, including affine/shear residuals.
                local = oldBone.ExactLocalBindMatrix;
                preview = oldBone.LocalBindTransform;
            }
            rebuilt.Add(oldBone with
            {
                Index = rebuilt.Count,
                ParentIndex = newParent,
                LocalBindTransform = preview,
                ExactLocalBindMatrix = local,
            });
        }

        int baseCount = oldBones.Length;
        ImmutableArray<CustomModelAuthoredHelper> helpers = document.AuthoredHelpers.Select(helper =>
        {
            int parent = helper.ParentNodeIndex < baseCount
                ? oldToNew[helper.ParentNodeIndex]
                : baseCount + (helper.ParentNodeIndex - baseCount);
            return helper with { ParentNodeIndex = parent };
        }).ToImmutableArray();
        CustomModelDocument frameDocument = document with { Bones = rebuilt.ToImmutable(), AuthoredHelpers = helpers };

        RigEvidenceReference evidence = new()
        {
            Id = "parent-decision:" + entityId.ToString("N"),
            Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = session.SourceSha256,
            Description = "Reviewed hierarchy parent choice; native runtime parent behavior has not been established.",
        };
        var entities = session.Recipe.Entities.ToBuilder();
        if (parentOldIndex >= 0)
        {
            int parentEntityIndex = IndexOfEntity(entities, newParentEntityId!.Value);
            if (parentEntityIndex >= 0 && entities[parentEntityIndex].Kind == RigNativeEntityKind.Unknown)
            {
                RigNativeEntityKind? kind = document.Bones[parentOldIndex].Kind switch
                {
                    BoneKind.Root or BoneKind.Deform => RigNativeEntityKind.Bone,
                    BoneKind.Helper => RigNativeEntityKind.Helper,
                    _ => null,
                };
                if (kind is { } classified) entities[parentEntityIndex] = entities[parentEntityIndex] with { Kind = classified };
            }
        }
        ImmutableArray<RigParentDecision> decisions = session.ParentDecisions
            .Where(decision => decision.EntityId != entityId)
            .Append(new RigParentDecision(entityId, newParentEntityId, session.SourceSha256, true, [evidence]))
            .ToImmutableArray();
        ImmutableArray<RigEyeSetup> eyes = session.Eyes.Select(eye =>
            eye.DeformEntityId == entityId
                ? eye with { ParentEntityId = newParentEntityId, UserApproved = false }
                : eye).ToImmutableArray();
        ImmutableArray<RigEntityFramePolicy> policies = session.Recipe.FramePolicies.Select(policy =>
        {
            int oldIndex = IndexOfEntity(observed, policy.EntityId);
            return oldIndex < 0 || policy.SolvedGlobalFrame is null ? policy : policy with { SolvedGlobalFrame = oldEffectiveGlobals[oldIndex] };
        }).ToImmutableArray();
        ImmutableArray<HelperRecipe> recipes = session.Recipe.Helpers.Select(recipe =>
        {
            int oldIndex = IndexOfEntity(observed, recipe.EntityId);
            if (recipe.EntityId == entityId && newParentEntityId is null)
                throw new InvalidOperationException("A source helper recipe requires a concrete base parent; world parenting needs a unified helper transaction.");
            return oldIndex < 0 ? recipe : recipe with
            {
                LocalFrame = oldIndex < baseCount ? rebuilt[oldToNew[oldIndex]].ExactLocalBindMatrix : helpers[oldIndex - baseCount].ExactLocalMatrix,
                ParentEntityId = recipe.EntityId == entityId ? newParentEntityId!.Value : recipe.ParentEntityId,
            };
        }).ToImmutableArray();
        RiggingSession replacement = session with
        {
            ParentDecisions = decisions,
            Eyes = eyes,
            BindingBackend = null,
            Recipe = session.Recipe with { Entities = entities.ToImmutable(), FramePolicies = policies, Helpers = recipes },
        };
        RiggingSession changed = RiggingSessions.Change(session, replacement, RiggingEditKind.Anatomy);
        CustomModelDocument result = frameDocument with
        {
            RiggingSession = changed,
            RigSignature = CustomModelContractSignatures.ComputeRig(frameDocument.CreateEffectiveBones()),
            LastBuildReceipt = null,
        };
        result.Validate();
        return new RigHierarchyEditResult(
            result,
            oldToNew.ToImmutableArray(),
            BuildEffectiveMap(oldToNew, oldBones.Length, document.AuthoredHelpers.Length));
    }

    private static ImmutableArray<int> BuildEffectiveMap(int[] boneMap, int boneCount, int helperCount)
    {
        var result = ImmutableArray.CreateBuilder<int>(boneCount + helperCount);
        result.AddRange(boneMap);
        for (int index = 0; index < helperCount; index++) result.Add(boneCount + index);
        return result.ToImmutable();
    }

    private static int[] TopologicalOrder(int[] parents, int count)
    {
        var children = Enumerable.Range(0, count).Select(static _ => new List<int>()).ToArray();
        var indegree = new int[count];
        for (int index = 0; index < count; index++)
        {
            if (parents[index] >= 0) { children[parents[index]].Add(index); indegree[index] = 1; }
        }
        var ready = new SortedSet<int>(Enumerable.Range(0, count).Where(index => indegree[index] == 0));
        var order = new int[count];
        int written = 0;
        while (ready.Count > 0)
        {
            int next = ready.Min; ready.Remove(next); order[written++] = next;
            foreach (int child in children[next]) if (--indegree[child] == 0) ready.Add(child);
        }
        if (written != count) throw new InvalidOperationException("The requested hierarchy parent creates a cycle.");
        return order;
    }

    private static bool IsDescendant(IReadOnlyList<CustomModelBone> bones, int ancestor, int candidate)
    {
        int current = candidate;
        while (current >= 0)
        {
            if (current == ancestor) return true;
            current = bones[current].ParentIndex;
        }
        return false;
    }

    private static int IndexOfEntity(ImmutableArray<RigParentObservation> observed, Guid entityId)
    {
        for (int index = 0; index < observed.Length; index++) if (observed[index].EntityId == entityId) return index;
        return -1;
    }

    private static int IndexOfEntity(ImmutableArray<RigEntityBinding>.Builder entities, Guid entityId)
    {
        for (int index = 0; index < entities.Count; index++) if (entities[index].EntityId == entityId) return index;
        return -1;
    }

    private static TransformMatrix[] Globals(IReadOnlyList<CustomModelBone> bones)
    {
        var result = new TransformMatrix[bones.Count];
        foreach (CustomModelBone bone in bones)
            result[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : result[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        return result;
    }

    private static void EnsureFrame(TransformMatrix matrix, string message)
    {
        if (!matrix.IsFinite || !double.IsFinite(matrix.LinearDeterminant) || matrix.LinearDeterminant == 0)
            throw new InvalidDataException(message);
    }
}
