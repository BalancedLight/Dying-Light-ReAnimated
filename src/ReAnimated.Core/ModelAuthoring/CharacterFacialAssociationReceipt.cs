using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record CharacterFacialAssociationReceipt
{
    public const string HumanConstructorEmptyProfile = "dl1-player-humanai-constructor-empty-facial-file-v1";
    public string Profile { get; init; } = HumanConstructorEmptyProfile;
    public string ActorResourceId { get; init; } = string.Empty;
    public string ActorSourceSha256 { get; init; } = string.Empty;
    public int ModelDeclarationCallIndex { get; init; }
    public int ActorScopeCallIndex { get; init; }
    public string ExactModelName { get; init; } = string.Empty;
    public string RootResourceId { get; init; } = string.Empty;
    public string RootSourceSha256 { get; init; } = string.Empty;
    public int MimicSetFieldCallIndex { get; init; }
    public int MimicSetArgumentIndex { get; init; } = 1;
    public int MimicSetTokenStart { get; init; }
    public int MimicSetTokenLength { get; init; }
    public string SelectedMimicName { get; init; } = string.Empty;
    public string MimicSourceResourceId { get; init; } = string.Empty;
    public string MimicSourceSha256 { get; init; } = string.Empty;
    public bool MimicLookupFound { get; init; }
    public string DerivedFedName { get; init; } = string.Empty;
    public string ScanReceiptResourceId { get; init; } = string.Empty;
    public string ScanReceiptSha256 { get; init; } = string.Empty;
    public string CatalogSnapshotSha256 { get; init; } = string.Empty;
    public string AbsenceEvidenceResourceId { get; init; } = string.Empty;
    public string AbsenceEvidenceSha256 { get; init; } = string.Empty;
    public string ProfileModuleSha256 { get; init; } = string.Empty;
    public string ConstructorAssessmentSha256 { get; init; } = string.Empty;
    public string ConsumerAssessmentSha256 { get; init; } = string.Empty;
    public bool Reviewed { get; init; }

    public void Validate(CharacterResourceInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        foreach (var hash in new[] { ActorSourceSha256, RootSourceSha256, MimicSourceSha256,
            ScanReceiptSha256, CatalogSnapshotSha256, AbsenceEvidenceSha256, ProfileModuleSha256,
            ConstructorAssessmentSha256, ConsumerAssessmentSha256 })
            ProjectAssetReference.ValidateSha256(hash, nameof(CharacterFacialAssociationReceipt));
        if (!Reviewed || Profile != HumanConstructorEmptyProfile || RootResourceId != inventory.RootResourceId ||
            ModelDeclarationCallIndex < 0 || ActorScopeCallIndex < 0 || MimicSetFieldCallIndex < 0 ||
            MimicSetArgumentIndex != 1 || MimicSetTokenStart < 0 || MimicSetTokenLength <= 0 ||
            string.IsNullOrWhiteSpace(SelectedMimicName) || SelectedMimicName.Length > 4096 ||
            string.IsNullOrWhiteSpace(ExactModelName) || string.IsNullOrWhiteSpace(DerivedFedName) ||
            !DerivedFedName.EndsWith(".fed",StringComparison.OrdinalIgnoreCase) ||
            DerivedFedName.Contains(':') || DerivedFedName.Contains('/') || DerivedFedName.Contains('\\') ||
            DerivedFedName.Any(char.IsControl))
            throw new ArgumentException("Facial association profile or selected source tokens are invalid.");
        Require(RootResourceId,RootSourceSha256);
        Require(ActorResourceId,ActorSourceSha256);
        Require(MimicSourceResourceId,MimicSourceSha256);
        Require(ScanReceiptResourceId,ScanReceiptSha256);
        Require(AbsenceEvidenceResourceId,AbsenceEvidenceSha256);
        if (!inventory.ActorSourceReviews.Any(review => review.ActorResourceId == ActorResourceId &&
            review.ContentSha256 == ActorSourceSha256 && review.ModelDeclarationCallIndex == ModelDeclarationCallIndex &&
            review.ActorScopeCallIndex == ActorScopeCallIndex && review.ExactModelName == ExactModelName))
            throw new ArgumentException("The exact reviewed actor model scope changed.");
        var mimic=inventory.Resources.Single(resource=>resource.Id==MimicSourceResourceId);
        if (mimic.Subsystem!=CharacterSubsystem.FacialDefinitions)
            throw new ArgumentException("The retained global mimic source has no facial identity.");
        void Require(string id,string hash)
        {
            var resource=inventory.Resources.SingleOrDefault(value=>value.Id==id);
            if(resource is null || resource.IsOriginalArchive || resource.EntryPath is null ||
                resource.ContentSha256!=hash)
                throw new ArgumentException("A facial association source is missing or stale.");
        }
    }

    public bool MatchesCurrentInventory(CharacterResourceInventory inventory)
    {
        try { Validate(inventory); return true; }
        catch (ArgumentException) { return false; }
    }
}