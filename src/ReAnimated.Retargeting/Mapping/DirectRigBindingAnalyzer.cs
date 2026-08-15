using System.Collections.Immutable;
using System.Text;
using ReAnimated.Core.Domain;

namespace ReAnimated.Retargeting.Mapping;

public enum DirectRigCompatibilityKind
{
    ExactDirect,
    CompatibleDirect,
    Retarget,
}

public sealed record DirectRigCompatibilityResult(
    DirectRigCompatibilityKind Kind,
    DirectRigBinding? Binding,
    ImmutableArray<string> Diagnostics)
{
    public bool IsDirect => Kind is
        DirectRigCompatibilityKind.ExactDirect or
        DirectRigCompatibilityKind.CompatibleDirect;
}

/// <summary>
/// Classifies only deterministic direct playback. Anything requiring fuzzy
/// semantics, bind-basis correction, or hierarchy transfer remains a retarget
/// candidate for the existing suggestion/review system.
/// </summary>
public static class DirectRigBindingAnalyzer
{
    public const double LocalBindTolerance = 1e-6;
    public const double GlobalBindTolerance = 1e-5;

    public static DirectRigCompatibilityResult Analyze(
        RigDefinition source,
        RigDefinition target,
        AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(clip);

        int invalidTrack = clip.TransformTracks
            .Select(static track => track.BoneIndex)
            .FirstOrDefault(index => index < 0 || index >= source.BoneCount, -1);
        if (invalidTrack >= 0)
        {
            return Retarget(
                $"Animation track {invalidTrack} is outside the source rig.");
        }

        if (string.Equals(
                RigSignature.Compute(source),
                RigSignature.Compute(target),
                StringComparison.OrdinalIgnoreCase))
        {
            return new DirectRigCompatibilityResult(
                DirectRigCompatibilityKind.ExactDirect,
                null,
                ["Source and target use the same exact runtime rig identity."]);
        }

        ImmutableSortedSet<int> required = CollectRequiredSourceNodes(
            source,
            clip);
        Dictionary<uint, ImmutableArray<int>> sourceDescriptors =
            GroupDescriptors(source);
        Dictionary<uint, ImmutableArray<int>> targetDescriptors =
            GroupDescriptors(target);
        Dictionary<string, ImmutableArray<int>> sourceNames = GroupNames(source);
        Dictionary<string, ImmutableArray<int>> targetNames = GroupNames(target);
        var targetBySource = new Dictionary<int, int>();
        var evidenceBySource = new Dictionary<
            int,
            DirectBoneIdentityEvidence>();
        var usedTargets = new HashSet<int>();

        foreach (int sourceIndex in required)
        {
            BoneDefinition sourceBone = source.Bones[sourceIndex];
            int? targetIndex = null;
            DirectBoneIdentityEvidence evidence = default;
            if (sourceBone.DescriptorHash is uint descriptor &&
                sourceDescriptors.TryGetValue(
                    descriptor,
                    out ImmutableArray<int> sourceDescriptorRows) &&
                sourceDescriptorRows.Length == 1 &&
                targetDescriptors.TryGetValue(
                    descriptor,
                    out ImmutableArray<int> targetDescriptorRows) &&
                targetDescriptorRows.Length == 1)
            {
                targetIndex = targetDescriptorRows[0];
                evidence = DirectBoneIdentityEvidence.UniqueDescriptor;
            }
            else
            {
                string normalizedName = NormalizeExactName(sourceBone.Name);
                if (sourceNames.TryGetValue(
                        normalizedName,
                        out ImmutableArray<int> sourceNameRows) &&
                    sourceNameRows.Length == 1 &&
                    targetNames.TryGetValue(
                        normalizedName,
                        out ImmutableArray<int> targetNameRows) &&
                    targetNameRows.Length == 1)
                {
                    targetIndex = targetNameRows[0];
                    evidence =
                        DirectBoneIdentityEvidence.ExactNormalizedName;
                }
            }

            if (targetIndex is not int resolvedTarget)
            {
                return Retarget(
                    $"Source node '{sourceBone.Name}' has no unique exact target identity.");
            }

            if (!usedTargets.Add(resolvedTarget))
            {
                return Retarget(
                    $"More than one source node resolves to target node '{target.Bones[resolvedTarget].Name}'.");
            }

            BoneDefinition targetBone = target.Bones[resolvedTarget];
            if (sourceBone.Kind != targetBone.Kind)
            {
                return Retarget(
                    $"Node '{sourceBone.Name}' changes kind from {sourceBone.Kind} to {targetBone.Kind}.");
            }

            targetBySource.Add(sourceIndex, resolvedTarget);
            evidenceBySource.Add(sourceIndex, evidence);
        }

        SkeletonPose sourceBind = source.CreateBindPose();
        SkeletonPose targetBind = target.CreateBindPose();
        var rows = ImmutableArray.CreateBuilder<DirectBoneBinding>(
            required.Count);
        foreach (int sourceIndex in required)
        {
            int targetIndex = targetBySource[sourceIndex];
            BoneDefinition sourceBone = source.Bones[sourceIndex];
            BoneDefinition targetBone = target.Bones[targetIndex];
            int sourceParent = sourceBone.ParentIndex;
            int targetParent = targetBone.ParentIndex;
            bool parentMatches = sourceParent < 0
                ? targetParent < 0
                : targetBySource.TryGetValue(
                    sourceParent,
                    out int mappedParent) &&
                  mappedParent == targetParent;
            if (!parentMatches)
            {
                return Retarget(
                    $"Mapped parent topology differs at '{sourceBone.Name}'.");
            }

            bool localMatches = sourceBone.LocalBindPose
                .ToMatrix()
                .NearlyEquals(
                    targetBone.LocalBindPose.ToMatrix(),
                    LocalBindTolerance);
            bool globalMatches = sourceBind.GlobalMatrices[sourceIndex]
                .NearlyEquals(
                    targetBind.GlobalMatrices[targetIndex],
                    GlobalBindTolerance);
            if (!localMatches || !globalMatches)
            {
                return Retarget(
                    $"Bind basis differs at '{sourceBone.Name}'.");
            }

            rows.Add(new DirectBoneBinding
            {
                SourceBoneIndex = sourceIndex,
                TargetBoneIndex = targetIndex,
                SourceBoneName = sourceBone.Name,
                TargetBoneName = targetBone.Name,
                IdentityEvidence = evidenceBySource[sourceIndex],
                ParentTopologyMatches = true,
                LocalBindMatches = true,
                GlobalBindMatches = true,
            });
        }

        string sourceSignature = AnimationSkeletonSignature.Compute(source);
        string targetSignature = AnimationSkeletonSignature.Compute(target);
        ImmutableArray<DirectBoneBinding> immutableRows = rows.MoveToImmutable();
        string fingerprint = DirectRigBindingFingerprint.Compute(
            sourceSignature,
            targetSignature,
            DirectRigBinding.PolicyVersion,
            immutableRows);
        var binding = new DirectRigBinding(
            sourceSignature,
            targetSignature,
            fingerprint,
            DirectRigBinding.PolicyVersion,
            immutableRows);
        binding.ValidateFor(source, target);
        return new DirectRigCompatibilityResult(
            DirectRigCompatibilityKind.CompatibleDirect,
            binding,
            [
                $"{immutableRows.Length:N0} source node(s) use deterministic direct binding; unmatched target nodes remain at bind.",
            ]);
    }

    private static ImmutableSortedSet<int> CollectRequiredSourceNodes(
        RigDefinition source,
        AnimationClip clip)
    {
        var required = ImmutableSortedSet.CreateBuilder<int>();
        foreach (TransformTrack track in clip.TransformTracks)
        {
            int index = track.BoneIndex;
            while (index >= 0 && required.Add(index))
            {
                index = source.Bones[index].ParentIndex;
            }
        }

        return required.ToImmutable();
    }

    private static Dictionary<uint, ImmutableArray<int>> GroupDescriptors(
        RigDefinition rig) =>
        rig.Bones
            .Where(static bone => bone.DescriptorHash.HasValue)
            .GroupBy(static bone => bone.DescriptorHash!.Value)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static bone => bone.Index)
                    .ToImmutableArray());

    private static Dictionary<string, ImmutableArray<int>> GroupNames(
        RigDefinition rig) =>
        rig.Bones
            .GroupBy(
                static bone => NormalizeExactName(bone.Name),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static bone => bone.Index)
                    .ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);

    private static string NormalizeExactName(string value) =>
        value.Normalize(NormalizationForm.FormKC).Trim();

    private static DirectRigCompatibilityResult Retarget(string diagnostic) =>
        new(
            DirectRigCompatibilityKind.Retarget,
            null,
            [diagnostic]);
}
