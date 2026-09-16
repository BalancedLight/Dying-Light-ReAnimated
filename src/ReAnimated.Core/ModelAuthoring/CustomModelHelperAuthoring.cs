using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Pure document operations for authored helper nodes. Imported FBX bones are
/// never rewritten; all user-created rows remain in the schema-2 helper layer.
/// Callers can wrap these immutable transitions in their normal undo service.
/// </summary>
public static class CustomModelHelperAuthoring
{
    public const string GameCameraName = "EyeCamera";

    public static CustomModelDocument DuplicateAsHelper(
        CustomModelDocument document,
        int sourceNodeIndex,
        CustomModelAuthoredHelperKind kind,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ImmutableArray<CustomModelBone> effective = document.CreateEffectiveBones();
        if ((uint)sourceNodeIndex >= (uint)effective.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceNodeIndex));
        }

        string requestedName = string.IsNullOrWhiteSpace(name)
            ? BuildUniqueName(document, $"{effective[sourceNodeIndex].Name}_{kind}")
            : name.Trim();
        EnsureUniqueName(document, requestedName);
        var helper = new CustomModelAuthoredHelper
        {
            Name = requestedName,
            ParentNodeIndex = sourceNodeIndex,
            // A child identity transform begins at its selected parent's global
            // bind transform and provides a clean local offset for editing.
            LocalTransform = TransformTRS.Identity,
            ExactLocalMatrix = TransformMatrix.Identity,
            Kind = kind,
        };
        return FinalizeRigMutation(document, document with
        {
            AuthoredHelpers = document.AuthoredHelpers.Add(helper),
        });
    }

    public static CustomModelDocument CreateEyeCameraHelper(
        CustomModelDocument document,
        int sourceNodeIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        ImmutableArray<CustomModelBone> effective = document.CreateEffectiveBones();
        CustomModelBone? existing = effective.FirstOrDefault(static bone =>
            string.Equals(bone.Name, GameCameraName, StringComparison.Ordinal));
        if (existing is not null)
        {
            string resolution = existing.Kind == BoneKind.Camera && !existing.IsWeighted
                ? "Choose the existing helper explicitly or cancel camera promotion."
                : "The name is owned by a node that is not an unweighted camera helper.";
            throw new InvalidOperationException(
                $"The exact EyeCamera name already exists. {resolution}");
        }

        if (effective.Any(static bone =>
                string.Equals(bone.Name, GameCameraName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "A case-insensitive EyeCamera name collision must be resolved before creating the game camera helper.");
        }

        CustomModelDocument updated = DuplicateAsHelper(
            document,
            sourceNodeIndex,
            CustomModelAuthoredHelperKind.Camera,
            GameCameraName);
        return SelectPreviewCamera(updated, GameCameraName);
    }

    public static CustomModelDocument SetLocalTransform(
        CustomModelDocument document,
        Guid helperId,
        TransformTRS localTransform,
        TransformMatrix? exactLocalMatrix = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (helperId == Guid.Empty)
        {
            throw new ArgumentException("The helper identifier cannot be empty.", nameof(helperId));
        }

        if (!localTransform.IsFinite)
        {
            throw new ArgumentException("The helper transform must be finite.", nameof(localTransform));
        }

        int index = -1;
        for (int candidate = 0; candidate < document.AuthoredHelpers.Length; candidate++)
        {
            if (document.AuthoredHelpers[candidate].Id == helperId)
            {
                index = candidate;
                break;
            }
        }
        if (index < 0)
        {
            throw new KeyNotFoundException($"Authored helper '{helperId}' was not found.");
        }

        CustomModelAuthoredHelper previous = document.AuthoredHelpers[index];
        TransformMatrix previousTrs = previous.LocalTransform.ToMatrix();
        TransformMatrix exact = exactLocalMatrix ?? (previous.ExactLocalMatrix.NearlyEquals(previousTrs, 1e-12)
            ? localTransform.ToMatrix()
            : localTransform.ToMatrix() * previousTrs.InvertedAffine() * previous.ExactLocalMatrix);
        CustomModelAuthoredHelper replacement = document.AuthoredHelpers[index] with
        {
            LocalTransform = localTransform,
            ExactLocalMatrix = exact,
        };
        return FinalizeRigMutation(document, document with
        {
            AuthoredHelpers = document.AuthoredHelpers.SetItem(index, replacement),
        });
    }

    public static CustomModelDocument SelectPreviewCamera(
        CustomModelDocument document,
        string? nodeName)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (nodeName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
            string[] matches = document.CreateEffectiveBones()
                .Where(bone => string.Equals(bone.Name, nodeName, StringComparison.OrdinalIgnoreCase))
                .Select(static bone => bone.Name)
                .ToArray();
            if (matches.Length != 1)
            {
                throw new KeyNotFoundException(
                    $"Preview camera node '{nodeName}' is missing or ambiguous.");
            }

            nodeName = matches[0];
        }

        CustomModelDocument updated = document with
        {
            Camera = document.Camera with { ActivePreviewNodeName = nodeName },
        };
        updated.Validate();
        return updated;
    }

    private static CustomModelDocument FinalizeRigMutation(CustomModelDocument original, CustomModelDocument document)
    {
        string rigSignature = CustomModelContractSignatures.ComputeRig(
            document.CreateEffectiveBones());
        CustomModelDocument updated = document with
        {
            RigSignature = rigSignature,
            LastBuildReceipt = null,
        };
        updated = SynchronizeStudioHelpers(original, updated);
        updated.Validate();
        return updated;
    }

    private static CustomModelDocument SynchronizeStudioHelpers(CustomModelDocument original, CustomModelDocument updated)
    {
        if (original.RiggingSession is not { } session) return updated;
        ImmutableArray<RigParentObservation> originalNodes = RiggingSessions.ObserveSourceHierarchy(original);
        var entities = session.Recipe.Entities.ToBuilder();
        var recipes = session.Recipe.Helpers.ToBuilder();
        var policies = session.Recipe.FramePolicies.ToBuilder();
        ImmutableArray<CustomModelBone> bones = updated.CreateEffectiveBones();
        var globals = new List<TransformMatrix>(bones.Length);
        foreach (CustomModelBone bone in bones)
            globals.Add(bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix);
        foreach (CustomModelAuthoredHelper helper in updated.AuthoredHelpers)
        {
            CustomModelAuthoredHelper? previous = original.AuthoredHelpers.FirstOrDefault(h => h.Id == helper.Id);
            if (previous == helper) continue;
            int entityIndex = FindEntity(entities, helper.Id);
            if (entityIndex < 0)
                entities.Add(new() { EntityId = helper.Id, OwnerAssetId = updated.ModelId, NativeName = helper.Name,
                    SourceEntityId = "authored:" + helper.Id.ToString("N"), Kind = RigNativeEntityKind.Helper, Imported = false });
            else entities[entityIndex] = entities[entityIndex] with { NativeName = helper.Name };
            Guid parentId = helper.ParentNodeIndex < updated.Bones.Length ? originalNodes[helper.ParentNodeIndex].EntityId
                : updated.AuthoredHelpers[helper.ParentNodeIndex - updated.Bones.Length].Id;
            int recipeIndex = -1;
            for (int i = 0; i < recipes.Count; i++) if (recipes[i].EntityId == helper.Id) { recipeIndex = i; break; }
            if (recipeIndex >= 0)
            {
                HelperRecipe recipe = recipes[recipeIndex];
                recipes[recipeIndex] = recipe with
                {
                    LocalFrame = helper.ExactLocalMatrix, ParentEntityId = parentId, UserApproved = false,
                    FramePolicy = recipe.FramePolicy is RigFramePolicy.PreserveSource or RigFramePolicy.GeneratedDeform ? RigFramePolicy.Manual : recipe.FramePolicy,
                };
            }
            else
            {
                int policyIndex = -1;
                for (int i = 0; i < policies.Count; i++) if (policies[i].EntityId == helper.Id) { policyIndex = i; break; }
                RigEntityFramePolicy policy = policyIndex < 0 ? new() { EntityId = helper.Id } : policies[policyIndex];
                int nodeIndex = updated.Bones.Length + updated.AuthoredHelpers.IndexOf(helper);
                policy = policy with
                {
                    FramePolicy = policy.FramePolicy is RigFramePolicy.PreserveSource or RigFramePolicy.GeneratedDeform ? RigFramePolicy.Manual : policy.FramePolicy,
                    SolvedGlobalFrame = globals[nodeIndex],
                };
                if (policyIndex < 0) policies.Add(policy); else policies[policyIndex] = policy;
            }
        }
        var replacement = session with { Recipe = session.Recipe with { Entities = entities.ToImmutable(), Helpers = recipes.ToImmutable(), FramePolicies = policies.ToImmutable() } };
        return updated with { RiggingSession = RiggingSessions.Change(session, replacement, RiggingEditKind.Helpers) };

        static int FindEntity(ImmutableArray<RigEntityBinding>.Builder source, Guid id)
        {
            for (int i = 0; i < source.Count; i++) if (source[i].EntityId == id) return i;
            return -1;
        }
    }

    private static string BuildUniqueName(CustomModelDocument document, string stem)
    {
        string candidate = stem;
        int suffix = 2;
        while (document.CreateEffectiveBones().Any(bone =>
                   string.Equals(bone.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{stem}_{suffix++}";
        }

        return candidate;
    }

    private static void EnsureUniqueName(CustomModelDocument document, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (document.CreateEffectiveBones().Any(bone =>
                string.Equals(bone.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Hierarchy name '{name}' is already in use.",
                nameof(name));
        }
    }
}
