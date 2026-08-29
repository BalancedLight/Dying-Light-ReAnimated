using ReAnimated.Core.Domain;

namespace ReAnimated.Retargeting.Mapping;

internal sealed class RetargetRigRoleIndex
{
    private readonly RigDefinition _rig;
    private readonly string?[] _roles;
    private readonly bool[] _bodyEligible;
    private readonly Dictionary<string, int[]> _bodyTargetsByRole;
    private readonly Dictionary<string, int[]> _bonesByRole;
    private readonly int[] _depths;

    public RetargetRigRoleIndex(RigDefinition rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        _rig = rig;
        _roles = new string?[rig.Bones.Length];
        _bodyEligible = new bool[rig.Bones.Length];
        _depths = new int[rig.Bones.Length];

        foreach (BoneDefinition bone in rig.Bones)
        {
            _roles[bone.Index] = HumanoidBoneSemanticClassifier.Classify(
                bone.SemanticRole ?? bone.Name)?.Role;
            _depths[bone.Index] = bone.ParentIndex < 0
                ? 0
                : _depths[bone.ParentIndex] + 1;
        }

        bool hasDeformBones = rig.Bones.Any(static bone => bone.Kind == BoneKind.Deform);
        bool[] hasDeformDescendant = new bool[rig.Bones.Length];
        for (int index = rig.Bones.Length - 1; index >= 0; index--)
        {
            BoneDefinition bone = rig.Bones[index];
            if (bone.ParentIndex >= 0 &&
                (bone.Kind == BoneKind.Deform || hasDeformDescendant[index]))
            {
                hasDeformDescendant[bone.ParentIndex] = true;
            }
        }

        foreach (BoneDefinition bone in rig.Bones)
        {
            string? role = _roles[bone.Index];
            _bodyEligible[bone.Index] = bone.Kind switch
            {
                BoneKind.Root or BoneKind.Deform => true,
                BoneKind.Camera or BoneKind.Prop => false,
                BoneKind.Helper =>
                    HumanoidBoneSemanticClassifier.IsCanonicalRole(role) &&
                    (!hasDeformBones || hasDeformDescendant[bone.Index]),
                _ => false,
            };
        }

        _bonesByRole = BuildRoleLookup(static _ => true);
        _bodyTargetsByRole = BuildRoleLookup(index => _bodyEligible[index]);
    }

    public string? GetRole(int boneIndex) => _roles[boneIndex];

    public bool IsBodyEligibleTarget(int boneIndex) =>
        _bodyEligible[boneIndex];

    public bool IsHelperOnlyTarget(int boneIndex) =>
        _rig.Bones[boneIndex].Kind is
            BoneKind.Helper or BoneKind.Camera or BoneKind.Prop &&
        !_bodyEligible[boneIndex];

    public bool TryGetUniqueRoleBone(string role, out int boneIndex)
    {
        boneIndex = -1;
        if (!_bonesByRole.TryGetValue(role, out int[]? candidates) ||
            candidates.Length != 1)
        {
            return false;
        }

        boneIndex = candidates[0];
        return true;
    }

    public bool TryGetUniqueBodyRoleTarget(string role, out int boneIndex)
    {
        boneIndex = -1;
        if (!_bodyTargetsByRole.TryGetValue(role, out int[]? candidates))
        {
            return false;
        }

        if (candidates.Length == 1)
        {
            boneIndex = candidates[0];
            return true;
        }

        int shallowest = candidates
            .OrderBy(index => _depths[index])
            .ThenBy(static index => index)
            .First();
        if (candidates.All(candidate => IsAncestorOrSelf(shallowest, candidate)))
        {
            boneIndex = shallowest;
            return true;
        }

        return false;
    }

    private Dictionary<string, int[]> BuildRoleLookup(Func<int, bool> include) =>
        _roles
            .Select((role, index) => (Role: role, Index: index))
            .Where(row => row.Role is not null && include(row.Index))
            .GroupBy(static row => row.Role!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static row => row.Index).ToArray(),
                StringComparer.Ordinal);

    private bool IsAncestorOrSelf(int ancestorIndex, int boneIndex)
    {
        int current = boneIndex;
        while (current >= 0)
        {
            if (current == ancestorIndex)
            {
                return true;
            }

            current = _rig.Bones[current].ParentIndex;
        }

        return false;
    }
}
