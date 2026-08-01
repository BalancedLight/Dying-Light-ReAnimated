using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.DL1.Assets.Meshes;

/// <summary>
/// Builds the authoring rig directly from a decoded retail compact hierarchy.
/// No rest-skeleton structure or proprietary payload is synthesized.
/// </summary>
public static class Dl1RigDefinitionFactory
{
    private static readonly Dictionary<string, string> SemanticRoles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bip01"] = "body.root",
            ["pelvis"] = "body.pelvis",
            ["hspine"] = "body.spine.base",
            ["spine"] = "body.spine.0",
            ["spine1"] = "body.spine.1",
            ["spine2"] = "body.spine.2",
            ["spine3"] = "body.spine.3",
            ["neck"] = "body.neck.0",
            ["neck1"] = "body.neck.1",
            ["head"] = "body.head",
            ["eyecamera"] = "camera.eye",
            ["refcamera"] = "camera.reference_helper",
            ["l_upperarm"] = "arm.left.upper",
            ["l_forearm"] = "arm.left.lower",
            ["l_hand"] = "hand.left",
            ["r_upperarm"] = "arm.right.upper",
            ["r_forearm"] = "arm.right.lower",
            ["r_hand"] = "hand.right",
            ["l_thigh"] = "leg.left.upper",
            ["l_calf"] = "leg.left.lower",
            ["l_foot"] = "foot.left",
            ["r_thigh"] = "leg.right.upper",
            ["r_calf"] = "leg.right.lower",
            ["r_foot"] = "foot.right",
        };

    private static readonly (string Name, string Root, string Joint, string End)[]
        ValidatedPlayerChains =
        [
            ("left-hand", "l_upperarm", "l_forearm", "l_hand"),
            ("right-hand", "r_upperarm", "r_forearm", "r_hand"),
            ("left-foot", "l_thigh", "l_calf", "l_foot"),
            ("right-foot", "r_thigh", "r_calf", "r_foot"),
        ];

    public static RigDefinition? TryCreate(
        string resourceName,
        CompactMeshDocument hierarchy,
        IReadOnlyList<Dl1MorphTarget>? morphTargets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(hierarchy);
        if (!hierarchy.IsStructurallyValid)
        {
            return null;
        }

        int entityCount = GetAnimationEntityCount(hierarchy);
        if (entityCount <= 0)
        {
            return null;
        }

        BoneDefinition[] bones = new BoneDefinition[entityCount];
        for (int index = 0; index < entityCount; index++)
        {
            CompactMeshEntity entity = hierarchy.Entities[index];
            if (entity.ParentIndex >= entityCount)
            {
                return null;
            }

            BoneKind kind = ClassifyBone(entity);
            bones[index] = new BoneDefinition(
                index,
                entity.Name,
                entity.ParentIndex,
                ToTransform(entity),
                kind,
                requiredForExport: true,
                descriptorHash: Dl1NameHash.Compute(entity.Name),
                semanticRole: ResolveSemanticRole(entity.Name));
        }

        MorphChannelDefinition[] morphs =
            (morphTargets ?? [])
                .OrderBy(static target => target.Index)
                .Select((target, index) =>
                    new MorphChannelDefinition(
                        index,
                        target.Name,
                        descriptorHash: Dl1NameHash.Compute(target.Name),
                        minimumValue: -1.5,
                        maximumValue: 1.5))
                .ToArray();
        TwoBoneIkChainDefinition[] chains = BuildValidatedChains(bones);
        string normalizedResource = resourceName
            .Trim()
            .Replace('\\', '/')
            .ToLowerInvariant();
        return new RigDefinition(
            $"dl1-retail:{normalizedResource}",
            resourceName,
            bones,
            morphs,
            ikChains: chains);
    }

    private static int GetAnimationEntityCount(
        CompactMeshDocument hierarchy)
    {
        int count = Math.Clamp(
            hierarchy.AnimationEntityCountCandidate,
            0,
            hierarchy.Entities.Count);
        if (count == 0)
        {
            return 0;
        }

        bool hasRigEntity = hierarchy.Entities
            .Take(count)
            .Any(static entity =>
                entity.EntityType.HasFlag(CompactMeshEntityType.Bone) ||
                entity.EntityType.HasFlag(CompactMeshEntityType.Helper));
        return hasRigEntity ? count : 0;
    }

    private static BoneKind ClassifyBone(CompactMeshEntity entity)
    {
        if (entity.ParentIndex < 0)
        {
            return BoneKind.Root;
        }

        if (string.Equals(
                entity.Name,
                "eyecamera",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                entity.Name,
                "refcamera",
                StringComparison.OrdinalIgnoreCase))
        {
            return BoneKind.Camera;
        }

        return entity.EntityType.HasFlag(CompactMeshEntityType.Helper)
            ? BoneKind.Helper
            : entity.EntityType.HasFlag(CompactMeshEntityType.Bone)
                ? BoneKind.Deform
                : BoneKind.Prop;
    }

    private static string? ResolveSemanticRole(string name) =>
        SemanticRoles.TryGetValue(name, out string? role)
            ? role
            : null;

    private static TransformTRS ToTransform(CompactMeshEntity entity)
    {
        CompactMatrix3x4 matrix = entity.LocalMatrix;
        TransformMatrix value = new(
            matrix.M11,
            matrix.M12,
            matrix.M13,
            matrix.M14,
            matrix.M21,
            matrix.M22,
            matrix.M23,
            matrix.M24,
            matrix.M31,
            matrix.M32,
            matrix.M33,
            matrix.M34,
            0.0,
            0.0,
            0.0,
            1.0);
        try
        {
            // Compact mesh matrices are stored as 32-bit floats. Retail
            // orthonormal bases routinely carry ~1e-6 dot-product drift, so
            // use a float-appropriate tolerance while still rejecting actual
            // shear and zero axes.
            return value.Decompose(1.0e-4);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"The retail compact hierarchy entity {entity.Index} ('{entity.Name}', {entity.EntityType}) contains a singular or sheared local transform: {exception.Message}",
                exception);
        }
    }

    private static TwoBoneIkChainDefinition[] BuildValidatedChains(
        BoneDefinition[] bones)
    {
        List<TwoBoneIkChainDefinition> chains = [];
        foreach ((string name, string root, string joint, string end)
                 in ValidatedPlayerChains)
        {
            int rootIndex = FindUniqueBoneIndex(bones, root);
            int jointIndex = FindUniqueBoneIndex(bones, joint);
            int endIndex = FindUniqueBoneIndex(bones, end);
            if (rootIndex < 0 ||
                jointIndex < 0 ||
                endIndex < 0 ||
                bones[jointIndex].ParentIndex != rootIndex ||
                bones[endIndex].ParentIndex != jointIndex)
            {
                continue;
            }

            chains.Add(
                new TwoBoneIkChainDefinition(
                    name,
                    rootIndex,
                    jointIndex,
                    endIndex));
        }

        return chains.ToArray();
    }

    private static int FindUniqueBoneIndex(
        BoneDefinition[] bones,
        string name)
    {
        int found = -1;
        foreach (BoneDefinition bone in bones)
        {
            if (!string.Equals(
                    bone.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found >= 0)
            {
                return -1;
            }

            found = bone.Index;
        }

        return found;
    }
}
