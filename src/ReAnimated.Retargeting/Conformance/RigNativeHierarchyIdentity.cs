using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Retargeting.Conformance;

internal static class RigNativeHierarchyIdentity
{
    public static int Find(Dl1RigTemplateEntity target, RigDefinition source,
        IReadOnlyDictionary<int, int> mappedParents, ISet<int> claimed,
        ISet<int> reserved, RigCorrespondenceOptions options)
    {
        // Unweighted template pivots retain the existing helper-alias path.
        // This match is needed for structural rows that actually deform skin.
        if (!target.IsDeform) return -1;
        int parent = -1;
        if (target.ParentIndex >= 0 && !mappedParents.TryGetValue(target.ParentIndex, out parent))
            return -1;

        int match = -1;
        foreach (BoneDefinition bone in source.Bones)
        {
            if (!string.Equals(target.Name, bone.Name, StringComparison.OrdinalIgnoreCase) ||
                bone.ParentIndex != parent || claimed.Contains(bone.Index) || reserved.Contains(bone.Index) ||
                options.ExcludedSourceBones.Contains(bone.Name))
                continue;

            // A camera or prop cannot become a deforming joint through its name.
            if (bone.Kind is BoneKind.Camera or BoneKind.Prop)
                continue;

            if (match >= 0) return -1;
            match = bone.Index;
        }
        return match;
    }
}
