using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public enum Dl1StockPolicyProposalStatus
{
    Proposed,
    UnresolvedMissingDestination,
    UnresolvedAmbiguousDestination,
    UnresolvedMissingSemanticEntityId,
    UnresolvedExtraDestination,
}

/// <summary>A read-only review row comparing saved destination policy with exact source flags.</summary>
public sealed record Dl1StockBoneScriptPolicyReviewRow(
    int? SourceIndex,
    Guid? DestinationEntityId,
    string DestinationName,
    string? SourceSha256,
    uint? RawFlags,
    RigAnimationComponents? CurrentMask,
    RigAnimationLod? CurrentLod,
    RigAnimationComponents? ProposedMask,
    RigAnimationLod? ProposedLod,
    Dl1StockPolicyProposalStatus Status,
    string Reason);

/// <summary>Immutable source-to-destination policy observations; this type never edits the document.</summary>
public sealed record Dl1StockBoneScriptPolicyProposal(
    string SourceSha256,
    ImmutableArray<Dl1StockBoneScriptPolicyReviewRow> Rows)
{
    /// <summary>Integrity fingerprint over the complete immutable review proposal.</summary>
    public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>
/// Proposes BSCR mask/LOD values from exact decoded compact-mesh flags for a
/// destination whose saved conformance and prepared output rig still match.
/// The result is review data only: it assigns no component owner and makes no
/// native-readiness or runtime-behavior claim.
/// </summary>
public static class Dl1StockBoneScriptPolicyProposalService
{
    private const uint ComponentBitsMask = 0x0700;
    private const uint LodBitsMask = 0x7000;

    public static Dl1StockBoneScriptPolicyProposal Propose(
        Dl1RigTemplate template,
        CompactMeshDocument decodedSource,
        string sourceResourceSha256,
        CustomModelDocument destination,
        Dl1PreparedAuthoredRig preparedDestination)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(decodedSource);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(preparedDestination);
        RequireHash(sourceResourceSha256, nameof(sourceResourceSha256));
        if (!string.Equals(template.SourceFingerprint, sourceResourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The decoded source hash does not match the selected DL1 template fingerprint.");
        if (!decodedSource.IsStructurallyValid)
            throw new InvalidDataException("The decoded source hierarchy contains structural errors.");
        ValidateSourceRows(template, decodedSource);
        ValidateDestination(template, destination, preparedDestination);

        var policies = destination.RiggingSession?.Recipe.ComponentPolicies
            .GroupBy(static policy => policy.EntityId)
            .ToDictionary(static group => group.Key, static group => group.Count() == 1 ? group.Single() : null)
            ?? new Dictionary<Guid, AnimationComponentPolicy?>();

        Dl1AuthoredRigNode[] nodes = preparedDestination.Contract.Nodes.ToArray();
        var pairedDestinationNodes = new HashSet<Dl1AuthoredRigNode>();
        var rows = ImmutableArray.CreateBuilder<Dl1StockBoneScriptPolicyReviewRow>(
            template.EntityCount + nodes.Length);

        foreach (Dl1RigTemplateEntity sourceTemplateNode in template.Entities)
        {
            CompactMeshEntity sourceNode = decodedSource.Entities[sourceTemplateNode.Index];
            (RigAnimationComponents mask, RigAnimationLod lod) = DecodePolicy(sourceNode);
            Dl1AuthoredRigNode[] matches = nodes.Where(node =>
                string.Equals(node.Name, sourceTemplateNode.Name, StringComparison.Ordinal) &&
                node.ParentPhysicalIndex >= 0 == (sourceTemplateNode.ParentIndex >= 0) &&
                ParentName(nodes, node.ParentPhysicalIndex) == ParentName(template.Entities, sourceTemplateNode.ParentIndex) &&
                KindsMatch(sourceTemplateNode, sourceNode, node)).ToArray();

            if (matches.Length == 0)
            {
                rows.Add(new(sourceNode.Index, null, sourceTemplateNode.Name, sourceResourceSha256,
                    sourceNode.Flags, null, null, null, null,
                    Dl1StockPolicyProposalStatus.UnresolvedMissingDestination,
                    "No destination node has the exact native name, parent name, and kind."));
                continue;
            }
            if (matches.Length > 1)
            {
                rows.Add(new(sourceNode.Index, null, sourceTemplateNode.Name, sourceResourceSha256,
                    sourceNode.Flags, null, null, null, null,
                    Dl1StockPolicyProposalStatus.UnresolvedAmbiguousDestination,
                    "Multiple destination nodes have the exact native name, parent name, and kind."));
                continue;
            }

            Dl1AuthoredRigNode destinationNode = matches[0];
            pairedDestinationNodes.Add(destinationNode);
            if (destinationNode.SemanticEntityId is not { } entityId || entityId == Guid.Empty)
            {
                rows.Add(new(sourceNode.Index, null, destinationNode.Name, sourceResourceSha256,
                    sourceNode.Flags, null, null, null, null,
                    Dl1StockPolicyProposalStatus.UnresolvedMissingSemanticEntityId,
                    "The matched destination node has no Studio semantic EntityId."));
                continue;
            }

            policies.TryGetValue(entityId, out AnimationComponentPolicy? policy);
            rows.Add(new(sourceNode.Index, entityId, destinationNode.Name, sourceResourceSha256,
                sourceNode.Flags, policy?.EmittedMask, policy?.AnimationLod, mask, lod,
                Dl1StockPolicyProposalStatus.Proposed,
                "Decoded source flags are proposed for review; component ownership is unresolved by this service."));
        }

        foreach (Dl1AuthoredRigNode node in nodes)
        {
            if (pairedDestinationNodes.Contains(node))
                continue;
            AnimationComponentPolicy? current = node.SemanticEntityId is { } id && policies.TryGetValue(id, out var found)
                ? found : null;
            rows.Add(new(null, node.SemanticEntityId, node.Name, sourceResourceSha256, null,
                current?.EmittedMask, current?.AnimationLod, null, null,
                Dl1StockPolicyProposalStatus.UnresolvedExtraDestination,
                "No unique stock source row is paired with this destination node."));
        }

        var proposal = new Dl1StockBoneScriptPolicyProposal(sourceResourceSha256, rows.ToImmutable());
        return proposal with { Fingerprint = ComputeFingerprint(proposal) };
    }

    /// <summary>Recomputes the proposal integrity fingerprint without trusting its saved Fingerprint field.</summary>
    public static string ComputeFingerprint(Dl1StockBoneScriptPolicyProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new { proposal.SourceSha256, proposal.Rows });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static void ValidateDestination(
        Dl1RigTemplate template,
        CustomModelDocument destination,
        Dl1PreparedAuthoredRig prepared)
    {
        CustomModelRigConformance conformance = destination.RigConformance ??
            throw new InvalidDataException("The destination has no saved DL1 conformance record.");
        if (!string.Equals(conformance.TemplateId, template.TemplateId, StringComparison.Ordinal) ||
            !string.Equals(conformance.TemplateFingerprint, template.SourceFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(conformance.TemplateSourceResourceName, template.SourceResourceName, StringComparison.Ordinal))
            throw new InvalidDataException("The saved conformance template fingerprint or resource does not match the selected template.");
        // The saved applied signature identifies the conformed model rig. The
        // prepared Chrome preview can use different child-pointing bind frames
        // for the same hierarchy and is not the recorded source-rig identity.
        if (!conformance.MatchesAppliedOutputRig(
                prepared.SourceRig,
                destination.Source.ContentSha256))
            throw new InvalidDataException("The prepared destination rig does not match the saved applied output rig signature.");
    }

    private static void ValidateSourceRows(Dl1RigTemplate template, CompactMeshDocument decoded)
    {
        foreach (Dl1RigTemplateEntity expected in template.Entities)
        {
            if ((uint)expected.Index >= (uint)decoded.Entities.Count)
                throw new InvalidDataException($"Decoded source is missing template row {expected.Index}.");
            CompactMeshEntity actual = decoded.Entities[expected.Index];
            if (actual.Index != expected.Index ||
                !string.Equals(actual.Name, expected.Name, StringComparison.Ordinal) ||
                actual.ParentIndex != expected.ParentIndex ||
                KindOf(actual) != expected.Kind)
                throw new InvalidDataException($"Decoded source row {expected.Index} does not match the selected template index, name, parent, and kind.");
            _ = DecodePolicy(actual);
        }
    }

    private static (RigAnimationComponents Mask, RigAnimationLod Lod) DecodePolicy(CompactMeshEntity entity)
    {
        RigAnimationComponents mask = (RigAnimationComponents)((entity.Flags & ComponentBitsMask) >> 8);
        int lodValue = (int)((entity.Flags & LodBitsMask) >> 12);
        if (lodValue > (int)RigAnimationLod.Off)
            throw new InvalidDataException($"Decoded source row {entity.Index} has undefined animation LOD value {lodValue}.");
        return (mask, (RigAnimationLod)lodValue);
    }

    private static BoneKind KindOf(CompactMeshEntity entity)
    {
        if (entity.ParentIndex < 0) return BoneKind.Root;
        if (string.Equals(entity.Name, "eyecamera", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.Name, "refcamera", StringComparison.OrdinalIgnoreCase)) return BoneKind.Camera;
        if (entity.EntityType.HasFlag(CompactMeshEntityType.Helper)) return BoneKind.Helper;
        if (entity.EntityType.HasFlag(CompactMeshEntityType.Bone)) return BoneKind.Deform;
        return BoneKind.Prop;
    }

    private static bool KindsMatch(Dl1RigTemplateEntity templateNode,
        CompactMeshEntity sourceNode, Dl1AuthoredRigNode destinationNode) =>
        destinationNode.Kind == templateNode.Kind ||
        // The template classifies every root-level row as Root. Preserve the
        // more precise serialized Helper kind when matching a root-level
        // propsholder or similar source helper to the emitted hierarchy.
        templateNode.ParentIndex < 0 &&
        sourceNode.EntityType.HasFlag(CompactMeshEntityType.Helper) &&
        destinationNode.Kind == BoneKind.Helper &&
        destinationNode.ParentPhysicalIndex < 0;

    private static string? ParentName(IReadOnlyList<Dl1AuthoredRigNode> nodes, int parentIndex) =>
        parentIndex < 0 ? null : nodes.SingleOrDefault(node => node.PhysicalIndex == parentIndex)?.Name;

    private static string? ParentName(ImmutableArray<Dl1RigTemplateEntity> nodes, int parentIndex) =>
        parentIndex < 0 ? null : nodes[parentIndex].Name;

    private static void RequireHash(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A source resource hash must be a 64-character SHA-256 hex value.", parameterName);
    }
}
