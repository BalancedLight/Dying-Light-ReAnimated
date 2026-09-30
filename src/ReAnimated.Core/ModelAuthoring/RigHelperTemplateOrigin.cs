using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Historical creation provenance, not a current native rule or a live frame override.</summary>
public sealed record RigHelperTemplateOrigin
{
    public string TemplateId { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string ResourceName { get; init; } = string.Empty;
    public string ResourceSha256 { get; init; } = string.Empty;
    public string SourceNodeName { get; init; } = string.Empty;
    public string SourceParentName { get; init; } = string.Empty;
    public TransformMatrix SourceLocalFrame { get; init; } = TransformMatrix.Identity;
    public TransformMatrix LocalOffsetAtCreation { get; init; } = TransformMatrix.Identity;

    public void Validate()
    {
        RigContractRules.Text(TemplateId, nameof(TemplateId));
        RigContractRules.Text(ProfileName, nameof(ProfileName));
        RigContractRules.Text(ResourceName, nameof(ResourceName));
        RigContractRules.Hash(ResourceSha256, nameof(ResourceSha256));
        RigContractRules.Text(SourceNodeName, nameof(SourceNodeName));
        RigContractRules.Text(SourceParentName, nameof(SourceParentName));
        RigRecipeRules.Affine(SourceLocalFrame, nameof(SourceLocalFrame));
        RigRecipeRules.Affine(LocalOffsetAtCreation, nameof(LocalOffsetAtCreation));
    }
}
