using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Meshes;

/// <summary>
/// What the decoded mesh payload proves about geometry ownership. A metadata
/// container is not silently treated as either static or skinned geometry.
/// </summary>
public enum Dl1MeshGeometryKind
{
    Unknown,
    MetadataContainer,
    Static,
    Skinned,
}

/// <summary>
/// Bounded DL1 rig families supported by the first-release browser. Unknown is
/// intentional: classification never guesses a family from proportions alone.
/// </summary>
public enum Dl1RigFamily
{
    Unknown,
    Player,
    GenericNpc,
    GenericInfected,
    Volatile,
    Screamer,
    Demolisher,
    Goon,
}

public enum Dl1MeshPerspective
{
    Unknown,
    FirstPerson,
    ThirdPerson,
}

public enum Dl1FacialSupport
{
    Unknown,
    None,
    MorphChannels,
    DecodedMorphDeltas,
}

public enum Dl1RetailSourceScope
{
    Unknown,
    BaseGame,
    Dlc,
    UserAdded,
}

/// <summary>
/// Confidence describes the offline evidence used by the classifier. It is
/// not a preview-fidelity badge and never means game-validated.
/// </summary>
public enum Dl1ClassificationConfidence
{
    None,
    Low,
    Medium,
    High,
}

public enum Dl1ClassificationEvidenceSource
{
    DecodedGeometry,
    DecodedRig,
    DecodedMorphs,
    ResourceIdentity,
    RetailProvider,
    VariantTable,
}

public sealed record Dl1ClassificationEvidence(
    string Code,
    Dl1ClassificationEvidenceSource Source,
    Dl1ClassificationConfidence Confidence,
    string Message);

/// <summary>
/// Filterable, non-proprietary metadata derived from one physical retail asset
/// identity and its decoded mesh. No mesh, texture, animation, or FED bytes are
/// stored in this profile.
/// </summary>
public sealed record Dl1RetailMeshProfile(
    RetailAssetId AssetId,
    Dl1MeshGeometryKind GeometryKind,
    string? RigSignature,
    Dl1RigFamily RigFamily,
    Dl1ClassificationConfidence RigFamilyConfidence,
    Dl1MeshPerspective Perspective,
    Dl1ClassificationConfidence PerspectiveConfidence,
    Dl1FacialSupport FacialSupport,
    string ProviderId,
    string PackName,
    Dl1RetailSourceScope SourceScope,
    string? DlcIdentifier,
    IReadOnlyList<string> VariantNames,
    IReadOnlyList<Dl1ClassificationEvidence> Evidence)
{
    public bool IsStatic => GeometryKind == Dl1MeshGeometryKind.Static;

    public bool IsSkinned => GeometryKind == Dl1MeshGeometryKind.Skinned;

    public bool HasFacialSupport =>
        FacialSupport is
            Dl1FacialSupport.MorphChannels or
            Dl1FacialSupport.DecodedMorphDeltas;
}

/// <summary>
/// A conjunctive filter for decoded retail mesh profiles. Unknown evidence
/// never satisfies a negative capability request; for example, an undecoded
/// container does not match <see cref="FacialSupport"/> set to false.
/// </summary>
public sealed record Dl1RetailMeshFilter
{
    public Dl1MeshGeometryKind? GeometryKind { get; init; }

    public string? RigSignature { get; init; }

    public Dl1RigFamily? RigFamily { get; init; }

    public Dl1ClassificationConfidence? MinimumRigFamilyConfidence
    {
        get;
        init;
    }

    public Dl1MeshPerspective? Perspective { get; init; }

    public Dl1ClassificationConfidence? MinimumPerspectiveConfidence
    {
        get;
        init;
    }

    public bool? FacialSupport { get; init; }

    public string? ProviderId { get; init; }

    public string? PackName { get; init; }

    public Dl1RetailSourceScope? SourceScope { get; init; }

    public string? DlcIdentifier { get; init; }

    public string? VariantName { get; init; }

    public bool Matches(Dl1RetailMeshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return
            (GeometryKind is null ||
             profile.GeometryKind == GeometryKind) &&
            (string.IsNullOrWhiteSpace(RigSignature) ||
             string.Equals(
                 profile.RigSignature,
                 RigSignature.Trim(),
                 StringComparison.OrdinalIgnoreCase)) &&
            (RigFamily is null ||
             profile.RigFamily == RigFamily) &&
            (MinimumRigFamilyConfidence is null ||
             profile.RigFamilyConfidence >=
                MinimumRigFamilyConfidence) &&
            (Perspective is null ||
             profile.Perspective == Perspective) &&
            (MinimumPerspectiveConfidence is null ||
             profile.PerspectiveConfidence >=
                MinimumPerspectiveConfidence) &&
            MatchesFacialSupport(profile) &&
            (string.IsNullOrWhiteSpace(ProviderId) ||
             string.Equals(
                 profile.ProviderId,
                 ProviderId.Trim(),
                 StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(PackName) ||
             string.Equals(
                 profile.PackName,
                 PackName.Trim(),
                 StringComparison.OrdinalIgnoreCase)) &&
            (SourceScope is null ||
             profile.SourceScope == SourceScope) &&
            (string.IsNullOrWhiteSpace(DlcIdentifier) ||
             string.Equals(
                 profile.DlcIdentifier,
                 DlcIdentifier.Trim(),
                 StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(VariantName) ||
             profile.VariantNames.Contains(
                 VariantName.Trim(),
                 StringComparer.OrdinalIgnoreCase));
    }

    private bool MatchesFacialSupport(Dl1RetailMeshProfile profile) =>
        FacialSupport switch
        {
            null => true,
            true => profile.HasFacialSupport,
            false => profile.FacialSupport == Dl1FacialSupport.None,
        };
}
