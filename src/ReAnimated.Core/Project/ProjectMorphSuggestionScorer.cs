using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;

namespace ReAnimated.Core.Project;

/// <summary>
/// Persists explainable facial-mapping evidence and applies the conservative
/// assisted-review policy. Merely preparing or rescoring suggestions never
/// reviews a row.
/// </summary>
public static class ProjectMorphSuggestionScorer
{
    public const string PolicyVersion = "dlra-facial-assisted-review-v1";

    public const double AssistedReviewThreshold = 0.90;

    public static ImmutableArray<ProjectMorphBinding> Score(
        IEnumerable<ProjectMorphBinding> bindings,
        RigDefinition exactTargetRig)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(exactTargetRig);
        string rigSignature = RigSignature.Compute(exactTargetRig);
        Dictionary<string, MorphChannelDefinition> targets = exactTargetRig
            .MorphChannels
            .ToDictionary(static morph => morph.Name, StringComparer.OrdinalIgnoreCase);

        return bindings
            .Select(binding => Score(binding, targets, rigSignature))
            .ToImmutableArray();
    }

    public static ImmutableArray<ProjectMorphBinding> ApplyAssistedReview(
        IEnumerable<ProjectMorphBinding> bindings,
        RigDefinition exactTargetRig)
    {
        return Score(bindings, exactTargetRig)
            .Select(binding => IsEligibleForAssistedReview(binding)
                ? binding with
                {
                    IsReviewed = true,
                    IsLocked = true,
                    ReviewOrigin = ProjectMappingReviewOrigin.Assisted,
                }
                : binding)
            .ToImmutableArray();
    }

    /// <summary>
    /// Explicit approvals are author decisions and survive rescoring. An
    /// assisted approval is cleared when the rig, policy, row, or evidence
    /// changes.
    /// </summary>
    public static ImmutableArray<ProjectMorphBinding> RevalidateAssistedApprovals(
        IEnumerable<ProjectMorphBinding> bindings,
        RigDefinition exactTargetRig)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ProjectMorphBinding[] original = bindings.ToArray();
        ImmutableArray<ProjectMorphBinding> rescored = Score(original, exactTargetRig);
        var result = ImmutableArray.CreateBuilder<ProjectMorphBinding>(original.Length);
        for (int index = 0; index < original.Length; index++)
        {
            ProjectMorphBinding prior = original[index];
            ProjectMorphBinding current = rescored[index];
            if (prior.ReviewOrigin != ProjectMappingReviewOrigin.Assisted)
            {
                result.Add(prior);
                continue;
            }

            bool remainsValid =
                string.Equals(prior.ScorerVersion, PolicyVersion, StringComparison.Ordinal) &&
                string.Equals(
                    prior.EvidenceFingerprint,
                    current.EvidenceFingerprint,
                    StringComparison.OrdinalIgnoreCase) &&
                IsEligibleForAssistedReview(current);
            result.Add(remainsValid
                ? current with
                {
                    IsReviewed = true,
                    IsLocked = true,
                    ReviewOrigin = ProjectMappingReviewOrigin.Assisted,
                }
                : current with
                {
                    IsReviewed = false,
                    IsLocked = false,
                    ReviewOrigin = ProjectMappingReviewOrigin.None,
                });
        }

        return result.MoveToImmutable();
    }

    public static bool IsEligibleForAssistedReview(ProjectMorphBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.Enabled ||
            binding.Confidence < AssistedReviewThreshold ||
            binding.Weight != 1.0 ||
            binding.Bias != 0.0 ||
            string.IsNullOrWhiteSpace(binding.TargetMorph) ||
            !binding.TargetDescriptorHash.HasValue ||
            !string.Equals(binding.ScorerVersion, PolicyVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(binding.EvidenceFingerprint))
        {
            return false;
        }

        return Classify(binding.Method) is MorphEvidenceKind.ExactIdentity or
            MorphEvidenceKind.DeclaredSemanticAlias;
    }

    private static ProjectMorphBinding Score(
        ProjectMorphBinding binding,
        Dictionary<string, MorphChannelDefinition> targets,
        string rigSignature)
    {
        MorphEvidenceKind kind = Classify(binding.Method);
        bool targetIsExact =
            targets.TryGetValue(binding.TargetMorph, out MorphChannelDefinition? target) &&
            binding.TargetDescriptorHash.HasValue &&
            target.DescriptorHash == binding.TargetDescriptorHash;
        double confidence = kind switch
        {
            MorphEvidenceKind.ExactIdentity when targetIsExact => 1.0,
            MorphEvidenceKind.DeclaredSemanticAlias when targetIsExact =>
                Math.Max(0.90, Math.Min(binding.Confidence, 1.0)),
            MorphEvidenceKind.Companion => Math.Min(binding.Confidence, 0.78),
            _ => Math.Min(binding.Confidence, 0.70),
        };
        string evidence = kind switch
        {
            MorphEvidenceKind.ExactIdentity when targetIsExact =>
                $"A unique exact facial alias resolves to '{binding.TargetMorph}' and descriptor 0x{binding.TargetDescriptorHash:X8} on the target rig.",
            MorphEvidenceKind.DeclaredSemanticAlias when targetIsExact =>
                $"A declared facial semantic alias resolves uniquely to '{binding.TargetMorph}' and descriptor 0x{binding.TargetDescriptorHash:X8} on the target rig.",
            MorphEvidenceKind.Companion =>
                "This companion/fan-out facial row uses a non-identity weight and always requires explicit review.",
            _ when !targetIsExact =>
                "The persisted target name and descriptor do not identify one exact morph on the current target rig.",
            _ => "This facial mapping method requires explicit review.",
        };
        ProjectMorphBinding scored = binding with
        {
            Confidence = confidence,
            Evidence = evidence,
            ScorerVersion = PolicyVersion,
            ReviewOrigin = binding.ReviewOrigin == ProjectMappingReviewOrigin.Assisted
                ? ProjectMappingReviewOrigin.None
                : binding.ReviewOrigin,
            IsReviewed = binding.ReviewOrigin == ProjectMappingReviewOrigin.Assisted
                ? false
                : binding.IsReviewed,
            IsLocked = binding.ReviewOrigin == ProjectMappingReviewOrigin.Assisted
                ? false
                : binding.IsLocked,
        };
        return scored with
        {
            EvidenceFingerprint = ComputeEvidenceFingerprint(scored, rigSignature),
        };
    }

    private static MorphEvidenceKind Classify(string method)
    {
        string value = method?.Trim() ?? string.Empty;
        if (value.Equals("exact_alias", StringComparison.Ordinal) ||
            value.Equals("shape_alias:exact_alias", StringComparison.Ordinal))
        {
            return MorphEvidenceKind.ExactIdentity;
        }

        if (value.Equals("semantic_alias", StringComparison.Ordinal) ||
            value.Equals("semantic_disambiguation", StringComparison.Ordinal) ||
            value.Equals("shape_alias:semantic_alias", StringComparison.Ordinal) ||
            value.Equals("shape_alias:semantic_disambiguation", StringComparison.Ordinal))
        {
            return MorphEvidenceKind.DeclaredSemanticAlias;
        }

        return value.Contains("companion", StringComparison.Ordinal)
            ? MorphEvidenceKind.Companion
            : MorphEvidenceKind.ReviewRequired;
    }

    private static string ComputeEvidenceFingerprint(
        ProjectMorphBinding binding,
        string rigSignature)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, PolicyVersion);
        Append(hash, rigSignature);
        Append(hash, binding.SourceChannel);
        Append(hash, (int)binding.SourceValueUnit);
        Append(hash, binding.TargetMorph);
        Append(hash, binding.TargetDescriptorHash ?? uint.MaxValue);
        Append(hash, BitConverter.DoubleToInt64Bits(binding.Weight));
        Append(hash, BitConverter.DoubleToInt64Bits(binding.Bias));
        Append(hash, binding.Enabled ? 1 : 0);
        Append(hash, BitConverter.DoubleToInt64Bits(binding.Confidence));
        Append(hash, binding.Method);
        Append(hash, binding.Evidence);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private enum MorphEvidenceKind
    {
        ExactIdentity,
        DeclaredSemanticAlias,
        Companion,
        ReviewRequired,
    }
}
