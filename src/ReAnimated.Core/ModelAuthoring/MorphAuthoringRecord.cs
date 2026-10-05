using System.Collections.Immutable;
using ReAnimated.Core.Project;
namespace ReAnimated.Core.ModelAuthoring;

public enum MorphAuthoringMethod { ManualSculpt, AssistedTransfer, ExactTopologyCopy }
/// <summary>Accepted expression provenance independent of generated target channel/object identifiers.</summary>
public sealed record MorphAuthoringRecord
{
    public string Name { get; init; }=string.Empty;
    public uint DescriptorHash { get; init; }
    public string ReferenceSourceSha256 { get; init; }=string.Empty;
    public int OriginalSourceChannelIndex { get; init; }
    public int TargetChannelSlot { get; init; }
    public string ReferenceSurfaceId { get; init; }=string.Empty;
    public string TargetSurfaceId { get; init; }=string.Empty;
    public string ReferenceTopologyFingerprint { get; init; }=string.Empty;
    public string TargetNeutralFingerprint { get; init; }=string.Empty;
    public string? AuthoredExpressionSha256 { get; init; }
    public MorphAuthoringMethod Method { get; init; }
    public MorphTransferConflict ConflictChoice { get; init; }
    public string? ReviewedRegionName {get;init;}
    public ImmutableArray<int> ReviewedTargetRegion {get;init;}=[];
    public ImmutableArray<MorphVertexCorrespondence> ReviewedLandmarks {get;init;}=[];
    public ImmutableArray<MorphTriangleCorrespondence> ReviewedTriangles { get; init; }=[];
    public ImmutableArray<int> LockedTargetVertices { get; init; }=[];
    public bool Accepted { get; init; }
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetSurfaceId);
        ProjectAssetReference.ValidateSha256(ReferenceSourceSha256,nameof(ReferenceSourceSha256));
        ProjectAssetReference.ValidateSha256(ReferenceTopologyFingerprint,nameof(ReferenceTopologyFingerprint));
        ProjectAssetReference.ValidateSha256(TargetNeutralFingerprint,nameof(TargetNeutralFingerprint));
        if(AuthoredExpressionSha256 is { } expressionHash) ProjectAssetReference.ValidateSha256(expressionHash,nameof(AuthoredExpressionSha256));
        if(OriginalSourceChannelIndex<0 || TargetChannelSlot<0 || !Enum.IsDefined(Method) || !Enum.IsDefined(ConflictChoice) ||
            ReviewedLandmarks.IsDefault || ReviewedTargetRegion.IsDefault || ReviewedTargetRegion.Any(i=>i<0) ||
            ReviewedLandmarks.Any(c=>c.SourceVertexIndex<0 || c.TargetVertexIndex<0 || !double.IsFinite(c.Weight) || c.Weight<=0) ||
            ReviewedTriangles.IsDefault || ReviewedTriangles.Any(c=>c.SourceTriangleIndex<0 || c.TargetTriangleIndex<0 || !double.IsFinite(c.Weight) || c.Weight<=0) || LockedTargetVertices.IsDefault || LockedTargetVertices.Any(i=>i<0))
            throw new ArgumentException("Expression provenance contains invalid mappings or constraints.");
    }
}
