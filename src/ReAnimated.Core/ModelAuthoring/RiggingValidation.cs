using System.Collections.Immutable;
using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public enum RigValidationFacet
{
    GeometryAndBind,
    Mapping,
    RuntimeNodeCompleteness,
    MotionCompatibility,
    CompiledVerification,
    LoadedResourceIdentity,
    LiveScenario,
}

public enum RigValidationStatus { Unverified, Passed, Failed, NotApplicable }

public enum RigEvidenceKind
{
    ImportedSource,
    ProfileRule,
    GeometryInference,
    UserOverride,
    OfflineTest,
    CompiledReadBack,
    LoadedResourceCapture,
    LiveScenarioCapture,
}

/// <summary>A provenance reference, never an embedded retail payload or a confidence score.</summary>
public sealed record RigEvidenceReference
{
    public string Id { get; init; } = string.Empty;
    public RigEvidenceKind Kind { get; init; }
    public string? ArtifactSha256 { get; init; }
    public string? BuildFingerprint { get; init; }
    public string? ConsumerId { get; init; }
    public string? Description { get; init; }

    public void Validate()
    {
        RigContractRules.Text(Id, nameof(Id));
        RigContractRules.Defined(Kind, nameof(Kind));
        RigContractRules.OptionalHash(ArtifactSha256, nameof(ArtifactSha256));
        RigContractRules.OptionalHash(BuildFingerprint, nameof(BuildFingerprint));
        RigContractRules.OptionalText(ConsumerId, nameof(ConsumerId));
        RigContractRules.OptionalText(Description, nameof(Description), 16_384);
    }
}

/// <summary>
/// Input identity includes selection, coordinates, recipe and backend settings.
/// Native identities remain distinct from source identity and are not inferred.
/// </summary>
public sealed record RigValidationScope
{
    public string SourceSha256 { get; init; } = string.Empty;
    public string InputFingerprint { get; init; } = string.Empty;
    public string? AuthoredContractSha256 { get; init; }
    public string? ProfileId { get; init; }
    public string? ProfileVersion { get; init; }
    public string? BuildFingerprint { get; init; }
    public string? CompilerFingerprint { get; init; }
    /// <summary>Logical compiled resource content, using the same hash contract as LoadedResourceSha256.</summary>
    public string? CompiledAssetSha256 { get; init; }
    public string? LoadedResourceId { get; init; }
    public string? LoadedResourceSha256 { get; init; }
    public string? RuntimeSessionId { get; init; }
    public string? RuntimeActorId { get; init; }
    public string? ClipSha256 { get; init; }
    public string? ScenarioId { get; init; }
    public string? ScenarioFingerprint { get; init; }

    public void Validate()
    {
        RigContractRules.Hash(SourceSha256, nameof(SourceSha256));
        RigContractRules.Hash(InputFingerprint, nameof(InputFingerprint));
        RigContractRules.OptionalHash(AuthoredContractSha256, nameof(AuthoredContractSha256));
        RigContractRules.OptionalHash(BuildFingerprint, nameof(BuildFingerprint));
        RigContractRules.OptionalHash(CompilerFingerprint, nameof(CompilerFingerprint));
        RigContractRules.OptionalHash(CompiledAssetSha256, nameof(CompiledAssetSha256));
        RigContractRules.OptionalHash(LoadedResourceSha256, nameof(LoadedResourceSha256));
        RigContractRules.OptionalHash(ClipSha256, nameof(ClipSha256));
        RigContractRules.OptionalHash(ScenarioFingerprint, nameof(ScenarioFingerprint));
        RigContractRules.OptionalText(ProfileId, nameof(ProfileId));
        RigContractRules.OptionalText(ProfileVersion, nameof(ProfileVersion));
        if ((ProfileId is null) != (ProfileVersion is null))
            throw new ArgumentException("A profile identity requires both its ID and version.");
        RigContractRules.OptionalText(LoadedResourceId, nameof(LoadedResourceId));
        RigContractRules.OptionalText(RuntimeSessionId, nameof(RuntimeSessionId));
        RigContractRules.OptionalText(RuntimeActorId, nameof(RuntimeActorId));
        RigContractRules.OptionalText(ScenarioId, nameof(ScenarioId));
    }

    public bool Matches(RigValidationScope current, RigValidationFacet facet)
    {
        ArgumentNullException.ThrowIfNull(current);
        Validate(); current.Validate(); RigContractRules.Defined(facet, nameof(facet));
        if (!RigContractRules.SameHash(SourceSha256, current.SourceSha256) ||
            !RigContractRules.SameHash(InputFingerprint, current.InputFingerprint) ||
            !RigContractRules.SameHash(AuthoredContractSha256, current.AuthoredContractSha256) ||
            ProfileId != current.ProfileId || ProfileVersion != current.ProfileVersion ||
            !RigContractRules.SameHash(BuildFingerprint, current.BuildFingerprint)) return false;
        if (facet is RigValidationFacet.MotionCompatibility or RigValidationFacet.LiveScenario &&
            !RigContractRules.SameHash(ClipSha256, current.ClipSha256)) return false;
        if (facet is RigValidationFacet.CompiledVerification or RigValidationFacet.LoadedResourceIdentity or RigValidationFacet.LiveScenario &&
            (!RigContractRules.SameHash(CompiledAssetSha256, current.CompiledAssetSha256) ||
             !RigContractRules.SameHash(CompilerFingerprint, current.CompilerFingerprint))) return false;
        if (facet is RigValidationFacet.LoadedResourceIdentity or RigValidationFacet.LiveScenario &&
            (LoadedResourceId != current.LoadedResourceId ||
             !RigContractRules.SameHash(LoadedResourceSha256, current.LoadedResourceSha256) ||
             RuntimeSessionId != current.RuntimeSessionId || RuntimeActorId != current.RuntimeActorId)) return false;
        return facet != RigValidationFacet.LiveScenario ||
            ScenarioId == current.ScenarioId && RigContractRules.SameHash(ScenarioFingerprint, current.ScenarioFingerprint);
    }
}

public sealed record CapabilityValidationReceipt
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string CapabilityId { get; init; } = string.Empty;
    public RigValidationFacet Facet { get; init; }
    public RigValidationStatus Status { get; init; } = RigValidationStatus.Unverified;
    public RigValidationScope Scope { get; init; } = new();
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];
    public DateTimeOffset ObservedUtc { get; init; }
    public string? Reason { get; init; }

    public void Validate()
    {
        RigContractRules.Identifier(Id, nameof(Id));
        RigContractRules.Text(CapabilityId, nameof(CapabilityId));
        RigContractRules.Defined(Facet, nameof(Facet));
        RigContractRules.Defined(Status, nameof(Status));
        ArgumentNullException.ThrowIfNull(Scope); Scope.Validate();
        RigContractRules.Array(Evidence, nameof(Evidence));
        RigContractRules.OptionalText(Reason, nameof(Reason), 16_384);
        if (Status is RigValidationStatus.Failed or RigValidationStatus.NotApplicable && string.IsNullOrWhiteSpace(Reason))
            throw new ArgumentException("Failed or not-applicable results require a specific reason.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (RigEvidenceReference evidence in Evidence)
        {
            evidence.Validate();
            if (!identities.Add(evidence.Id)) throw new ArgumentException("Receipt evidence identities must be unique.");
        }
        if (Status != RigValidationStatus.Passed) return;
        if (ObservedUtc == default || Scope.AuthoredContractSha256 is null || Scope.ProfileId is null)
            throw new ArgumentException("A passing result requires a dated, profile-scoped authored-contract identity.");
        RigEvidenceKind requiredKind = Facet switch
        {
            RigValidationFacet.CompiledVerification => RigEvidenceKind.CompiledReadBack,
            RigValidationFacet.LoadedResourceIdentity => RigEvidenceKind.LoadedResourceCapture,
            RigValidationFacet.LiveScenario => RigEvidenceKind.LiveScenarioCapture,
            _ => RigEvidenceKind.OfflineTest,
        };
        if (!Evidence.Any(e => e.Kind == requiredKind && e.ArtifactSha256 is not null &&
                (requiredKind == RigEvidenceKind.OfflineTest ||
                 e.BuildFingerprint is not null && RigContractRules.SameHash(e.BuildFingerprint, Scope.BuildFingerprint))))
            throw new ArgumentException($"Facet '{Facet}' requires its own {requiredKind} evidence artifact.");
        if (Facet == RigValidationFacet.MotionCompatibility && Scope.ClipSha256 is null)
            throw new ArgumentException("Motion verification requires the selected clip identity.");
        if (Facet is RigValidationFacet.CompiledVerification or RigValidationFacet.LoadedResourceIdentity or RigValidationFacet.LiveScenario &&
            (Scope.BuildFingerprint is null || Scope.CompilerFingerprint is null || Scope.CompiledAssetSha256 is null))
            throw new ArgumentException("Native verification requires build, compiler and compiled-asset identities.");
        if (Facet is RigValidationFacet.LoadedResourceIdentity or RigValidationFacet.LiveScenario &&
            (Scope.LoadedResourceId is null || Scope.LoadedResourceSha256 is null || Scope.RuntimeSessionId is null || Scope.RuntimeActorId is null))
            throw new ArgumentException("Loaded-resource verification requires the physical provider, actor and capture-session identities.");
        if (Facet is RigValidationFacet.LoadedResourceIdentity or RigValidationFacet.LiveScenario &&
            !RigContractRules.SameHash(Scope.CompiledAssetSha256, Scope.LoadedResourceSha256))
            throw new ArgumentException("A passing load result must identify the expected compiled resource content.");
        if (Facet == RigValidationFacet.LiveScenario && (Scope.ScenarioId is null || Scope.ScenarioFingerprint is null || Scope.ClipSha256 is null))
            throw new ArgumentException("Live validation requires a specific scenario and selected clip identity.");
    }
}

/// <summary>Applicability is declared by the selected capability, not by a receipt wishing to skip a test.</summary>
public sealed record RigFacetRequirement(RigValidationFacet Facet, bool Applicable = true, string? Reason = null);
public sealed record RigFacetAssessment(RigValidationFacet Facet, RigValidationStatus Status, Guid? ReceiptId, string Reason);
public sealed record RigCapabilityAssessment(string CapabilityId, RigValidationStatus Status, ImmutableArray<RigFacetAssessment> Facets);

public static class RigCapabilityValidation
{
    public static RigCapabilityAssessment Assess(
        string capabilityId,
        RigValidationScope current,
        IEnumerable<CapabilityValidationReceipt> receipts,
        IEnumerable<RigFacetRequirement>? requirements = null)
    {
        RigContractRules.Text(capabilityId, nameof(capabilityId));
        ArgumentNullException.ThrowIfNull(current); current.Validate();
        ArgumentNullException.ThrowIfNull(receipts);
        CapabilityValidationReceipt[] history = receipts.ToArray();
        var receiptIds = new HashSet<Guid>();
        foreach (CapabilityValidationReceipt receipt in history)
        {
            if (receipt is null) throw new ArgumentException("Receipt history contains a missing entry.", nameof(receipts));
            receipt.Validate();
            if (!receiptIds.Add(receipt.Id)) throw new ArgumentException("Receipt identities must be unique.", nameof(receipts));
        }
        RigFacetRequirement[] declared = requirements?.ToArray() ??
            Enum.GetValues<RigValidationFacet>().Select(static facet => new RigFacetRequirement(facet)).ToArray();
        if (declared.Length != Enum.GetValues<RigValidationFacet>().Length ||
            declared.Any(static r => r is null || !Enum.IsDefined(r.Facet)) ||
            declared.Select(static r => r.Facet).Distinct().Count() != declared.Length)
            throw new ArgumentException("Every capability must declare all seven validation facets.", nameof(requirements));
        var results = ImmutableArray.CreateBuilder<RigFacetAssessment>();
        foreach (RigFacetRequirement requirement in declared.OrderBy(static r => r.Facet))
        {
            if (!requirement.Applicable)
            {
                RigContractRules.Text(requirement.Reason, nameof(requirement.Reason));
                results.Add(new(requirement.Facet, RigValidationStatus.NotApplicable, null, requirement.Reason!));
                continue;
            }
            CapabilityValidationReceipt? selected = history
                .Where(r => r.CapabilityId == capabilityId && r.Facet == requirement.Facet && r.Scope.Matches(current, r.Facet))
                .OrderByDescending(static r => r.ObservedUtc).ThenByDescending(static r => r.Id).FirstOrDefault();
            RigValidationStatus status = selected?.Status ?? RigValidationStatus.Unverified;
            string reason = selected?.Reason ?? (selected is null ? "No fresh evidence exists for this facet and scope." : "Fresh facet evidence is available.");
            if (status == RigValidationStatus.NotApplicable)
            {
                status = RigValidationStatus.Unverified;
                reason = "A not-applicable receipt cannot satisfy a required facet.";
            }
            results.Add(new(requirement.Facet, status, selected?.Id, reason));
        }
        ImmutableArray<RigFacetAssessment> facets = results.ToImmutable();
        RigValidationStatus aggregate = facets.Any(static f => f.Status == RigValidationStatus.Failed) ? RigValidationStatus.Failed :
            facets.Any(static f => f.Status == RigValidationStatus.Unverified) ? RigValidationStatus.Unverified :
            facets.All(static f => f.Status == RigValidationStatus.NotApplicable) ? RigValidationStatus.NotApplicable : RigValidationStatus.Passed;
        return new(capabilityId, aggregate, facets);
    }
}

internal static class RigContractRules
{
    public static void Text(string? value, string name, int maximum = 1024)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0'))
            throw new ArgumentException($"'{name}' must contain bounded, nonempty text without NUL characters.", name);
    }
    public static void OptionalText(string? value, string name, int maximum = 1024)
    {
        if (value is not null) Text(value, name, maximum);
    }
    public static void Hash(string value, string name) => ProjectAssetReference.ValidateSha256(value, name);
    public static void OptionalHash(string? value, string name) { if (value is not null) Hash(value, name); }
    public static bool SameHash(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    public static void Identifier(Guid value, string name) { if (value == Guid.Empty) throw new ArgumentException("Stable identifiers cannot be empty.", name); }
    public static void Defined<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(name, "Unsupported contract value.");
    }
    public static void Array<T>(ImmutableArray<T> items, string name)
    {
        if (items.IsDefault || items.Length > 65_536 || items.Any(static item => item is null))
            throw new ArgumentException("Contract collections must be initialized, bounded, and contain no null entries.", name);
    }
}
