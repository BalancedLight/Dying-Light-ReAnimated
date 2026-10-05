using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public enum MorphTransferMethod { ExactTopology, CorrespondenceDeformationGradient }
public enum MorphTransferConflict { KeepExisting, ReplaceExisting, Reject }

public sealed record MorphVertexCorrespondence(
    string SourceGeometryIdentity,
    int SourceVertexIndex,
    string TargetGeometryIdentity,
    int TargetVertexIndex,
    double Weight = 1.0,
    bool Locked = false);

public sealed record MorphTriangleCorrespondence(
    string SourceGeometryIdentity,
    int SourceTriangleIndex,
    string TargetGeometryIdentity,
    int TargetTriangleIndex,
    double Weight = 1.0,
    bool Locked = false);

public sealed record MorphTransferOptions
{
    public MorphTransferMethod Method { get; init; } = MorphTransferMethod.ExactTopology;
    public ImmutableHashSet<int> LockedTargetVertices { get; init; } = ImmutableHashSet<int>.Empty;
    public ImmutableHashSet<int> TargetVertexMask { get; init; } = ImmutableHashSet<int>.Empty;
    public ImmutableArray<MorphVertexCorrespondence> Correspondences { get; init; } = [];
    public ImmutableArray<MorphTriangleCorrespondence> TriangleCorrespondences { get; init; } = [];
    public int MinimumAnchors { get; init; } = 3;
    public MorphTransferConflict Conflict { get; init; } = MorphTransferConflict.Reject;
}

public sealed record MorphExpressionProposal(
    string SourceSha256,
    string SourceTopologyFingerprint,
    string TargetTopologyFingerprint,
    string SourceChannelName,
    uint SourceDescriptorHash,
    string TargetExpressionName,
    MorphTransferMethod Method,
    ImmutableArray<MorphReferenceSurface> TargetSurfaces,
    ImmutableArray<ImmutableArray<Vector3D>> PositionDeltas,
    ImmutableArray<MorphVertexCorrespondence> Correspondences,
    ImmutableArray<MorphTriangleCorrespondence> TriangleCorrespondences,
    bool RequiresExplicitReview,
    string Evidence,
    bool IsReviewed = false)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceSha256) || string.IsNullOrWhiteSpace(SourceTopologyFingerprint) ||
            string.IsNullOrWhiteSpace(TargetTopologyFingerprint) || string.IsNullOrWhiteSpace(SourceChannelName) ||
            string.IsNullOrWhiteSpace(TargetExpressionName) || TargetSurfaces.IsDefault || PositionDeltas.IsDefault ||
            PositionDeltas.Length != TargetSurfaces.Length)
            throw new ArgumentException("Morph proposal identity or payload is incomplete.");
        for (int i = 0; i < TargetSurfaces.Length; i++)
            if (PositionDeltas[i].Length != TargetSurfaces[i].NeutralPositions.Length || PositionDeltas[i].Any(d=>!d.IsFinite))
                throw new ArgumentException("Morph proposal delta topology does not match its target surface.");
    }

    public void ValidateAgainst(MorphReferenceProfile source, MorphReferenceProfile target)
    {
        source.Validate(); target.Validate(); Validate();
        if (!string.Equals(SourceSha256, source.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SourceTopologyFingerprint, source.TopologyFingerprint, StringComparison.Ordinal) ||
            !string.Equals(TargetTopologyFingerprint, target.TopologyFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Morph proposal source or target topology is stale.");
        MorphReferenceChannel channel = source.Channels.SingleOrDefault(c =>
            string.Equals(c.Name, SourceChannelName, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Morph proposal source channel is no longer present.");
        if (channel.DescriptorHash != SourceDescriptorHash)
            throw new InvalidDataException("Morph proposal source descriptor changed.");
    }
}

public sealed record MorphExpressionConflict(string TargetExpressionName, string ExistingSourceSha256, MorphTransferConflict Resolution);
