using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

public enum BoneKind
{
    Root,
    Deform,
    Helper,
    Camera,
    Prop,
}

/// <summary>
/// One topologically ordered bone in an immutable rig definition.
/// </summary>
public sealed record BoneDefinition
{
    public BoneDefinition(
        int index,
        string name,
        int parentIndex,
        TransformTRS localBindPose,
        BoneKind kind = BoneKind.Deform,
        bool requiredForExport = true,
        uint? descriptorHash = null,
        string? semanticRole = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfLessThan(parentIndex, -1);

        if (!localBindPose.IsFinite ||
            Math.Abs(localBindPose.Scale.X) <= 1e-12 ||
            Math.Abs(localBindPose.Scale.Y) <= 1e-12 ||
            Math.Abs(localBindPose.Scale.Z) <= 1e-12)
        {
            throw new ArgumentException("The bind pose must be finite and have non-zero scale.", nameof(localBindPose));
        }

        Index = index;
        Name = name;
        ParentIndex = parentIndex;
        LocalBindPose = localBindPose.Normalized();
        Kind = kind;
        RequiredForExport = requiredForExport;
        DescriptorHash = descriptorHash;
        SemanticRole = semanticRole;
    }

    public int Index { get; }

    public string Name { get; }

    public int ParentIndex { get; }

    public TransformTRS LocalBindPose { get; }

    public BoneKind Kind { get; }

    public bool RequiredForExport { get; }

    /// <summary>
    /// The authoritative optional DL1 animation descriptor hash.
    /// </summary>
    public uint? DescriptorHash { get; }

    /// <summary>
    /// A stable semantic role such as camera.reference or prop.right_hand.
    /// </summary>
    public string? SemanticRole { get; }
}

/// <summary>
/// An immutable, topologically ordered authoring rig.
/// </summary>
public sealed class RigDefinition
{
    private readonly ImmutableDictionary<string, ImmutableArray<int>>
        _boneIndices;

    public RigDefinition(
        string id,
        string displayName,
        IEnumerable<BoneDefinition> bones,
        IEnumerable<MorphChannelDefinition>? morphChannels = null,
        SourceAssetFingerprint? sourceAssetFingerprint = null,
        IEnumerable<TwoBoneIkChainDefinition>? ikChains = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(bones);

        ImmutableArray<BoneDefinition> boneArray = bones.ToImmutableArray();
        if (boneArray.IsEmpty)
        {
            throw new ArgumentException("A rig must contain at least one bone.", nameof(bones));
        }

        for (int index = 0; index < boneArray.Length; index++)
        {
            BoneDefinition bone = boneArray[index];
            if (bone.Index != index)
            {
                throw new ArgumentException(
                    $"Bone '{bone.Name}' has index {bone.Index}; rigs must use contiguous topological indices.",
                    nameof(bones));
            }

            if (bone.ParentIndex >= index)
            {
                throw new ArgumentException(
                    $"Bone '{bone.Name}' must refer only to an earlier parent index.",
                    nameof(bones));
            }

        }

        Id = id;
        DisplayName = displayName;
        Bones = boneArray;
        MorphChannels = morphChannels?.ToImmutableArray() ?? [];
        SourceAssetFingerprint = sourceAssetFingerprint;
        IkChains = ikChains?.ToImmutableArray() ?? [];
        _boneIndices = boneArray
            .GroupBy(
                static bone => bone.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group
                    .Select(static bone => bone.Index)
                    .ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);

        ValidateMorphChannels(MorphChannels, nameof(morphChannels));
        ValidateIkChains(Bones, IkChains, nameof(ikChains));
    }

    public string Id { get; }

    public string DisplayName { get; }

    public ImmutableArray<BoneDefinition> Bones { get; }

    public ImmutableArray<MorphChannelDefinition> MorphChannels { get; }

    public SourceAssetFingerprint? SourceAssetFingerprint { get; }

    public ImmutableArray<TwoBoneIkChainDefinition> IkChains { get; }

    public int BoneCount => Bones.Length;

    /// <summary>
    /// Returns the sole row carrying <paramref name="name"/>, or -1 when
    /// the name is missing or ambiguous.
    /// </summary>
    public int GetBoneIndex(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _boneIndices.TryGetValue(
                name,
                out ImmutableArray<int> indexes) &&
            indexes.Length == 1
                ? indexes[0]
                : -1;
    }

    /// <summary>
    /// Returns every topological row carrying <paramref name="name"/>.
    /// Retail compact hierarchies may legitimately repeat helper names, so
    /// callers that need identity must use the returned indexes instead of
    /// assuming that a name is unique.
    /// </summary>
    public ImmutableArray<int> GetBoneIndices(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _boneIndices.TryGetValue(
                name,
                out ImmutableArray<int> indexes)
                    ? indexes
                    : [];
    }

    public SkeletonPose CreateBindPose() =>
        new(this, Bones.Select(static bone => bone.LocalBindPose));

    public ImmutableArray<TransformMatrix> ComputeGlobalMatrices(
        IEnumerable<TransformTRS> localTransforms)
    {
        ArgumentNullException.ThrowIfNull(localTransforms);
        ImmutableArray<TransformTRS> locals = localTransforms.ToImmutableArray();
        if (locals.Length != Bones.Length)
        {
            throw new ArgumentException(
                $"Expected {Bones.Length} local transforms but received {locals.Length}.",
                nameof(localTransforms));
        }

        var globals = ImmutableArray.CreateBuilder<TransformMatrix>(Bones.Length);
        for (int index = 0; index < Bones.Length; index++)
        {
            TransformMatrix local = locals[index].ToMatrix();
            int parentIndex = Bones[index].ParentIndex;
            globals.Add(parentIndex < 0 ? local : globals[parentIndex] * local);
        }

        return globals.MoveToImmutable();
    }

    private static void ValidateMorphChannels(
        ImmutableArray<MorphChannelDefinition> morphChannels,
        string parameterName)
    {
        for (int index = 0; index < morphChannels.Length; index++)
        {
            if (morphChannels[index].Index != index)
            {
                throw new ArgumentException(
                    "Morph channels must use contiguous indices.",
                    parameterName);
            }
        }

        if (morphChannels
            .Select(static channel => channel.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != morphChannels.Length)
        {
            throw new ArgumentException("Morph channel names must be unique.", parameterName);
        }
    }

    private static void ValidateIkChains(
        ImmutableArray<BoneDefinition> bones,
        ImmutableArray<TwoBoneIkChainDefinition> ikChains,
        string parameterName)
    {
        if (ikChains
            .Select(static chain => chain.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != ikChains.Length)
        {
            throw new ArgumentException("IK chain names must be unique.", parameterName);
        }

        foreach (TwoBoneIkChainDefinition chain in ikChains)
        {
            if (chain.RootBoneIndex >= bones.Length ||
                chain.JointBoneIndex >= bones.Length ||
                chain.EndBoneIndex >= bones.Length ||
                bones[chain.JointBoneIndex].ParentIndex != chain.RootBoneIndex ||
                bones[chain.EndBoneIndex].ParentIndex != chain.JointBoneIndex)
            {
                throw new ArgumentException(
                    $"IK chain '{chain.Name}' is not a direct root/joint/end hierarchy.",
                    parameterName);
            }
        }
    }
}

/// <summary>
/// An immutable local and global pose for a specific rig.
/// </summary>
public sealed class SkeletonPose
{
    public SkeletonPose(
        RigDefinition rig,
        IEnumerable<TransformTRS> localTransforms)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(localTransforms);

        ImmutableArray<TransformTRS> locals = localTransforms
            .Select(static transform => transform.Normalized())
            .ToImmutableArray();
        if (locals.Length != rig.BoneCount)
        {
            throw new ArgumentException(
                $"Expected {rig.BoneCount} local transforms but received {locals.Length}.",
                nameof(localTransforms));
        }

        if (locals.Any(static transform => !transform.IsFinite))
        {
            throw new ArgumentException("Pose transforms must be finite.", nameof(localTransforms));
        }

        Rig = rig;
        LocalTransforms = locals;
        GlobalMatrices = rig.ComputeGlobalMatrices(locals);
    }

    public RigDefinition Rig { get; }

    public ImmutableArray<TransformTRS> LocalTransforms { get; }

    public ImmutableArray<TransformMatrix> GlobalMatrices { get; }

    public SkeletonPose WithLocalTransform(int boneIndex, TransformTRS transform)
    {
        if ((uint)boneIndex >= (uint)LocalTransforms.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(boneIndex));
        }

        return new SkeletonPose(Rig, LocalTransforms.SetItem(boneIndex, transform));
    }
}
