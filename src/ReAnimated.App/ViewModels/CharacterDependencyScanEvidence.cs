using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.App.ViewModels;

internal sealed record CharacterDependencyScanReceipt
{
    public string Format { get; init; } = "dl-reanimated-character-discovery-scan-v1";
    public string CharacterSourceSha256 { get; init; } = string.Empty;
    public string RootContentSha256 { get; init; } = string.Empty;
    public Dl1CharacterDependencyScanProvenance Scan { get; init; } = new();
}

internal static class CharacterDependencyScanEvidence
{
    internal const string ReceiptId = "discovery:receipt";
    private const string ReceiptProvider = "character-discovery:v1";
    private const int MaximumReceiptBytes = 32 * 1024 * 1024;
    private const string ScriptLimit = "Source-script scan was bounded; unscanned scripts may contain additional explicit declarations.";
    private const string GraphLimit = "Dependency graph resource bound reached; closure is incomplete.";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool IsReceipt(CharacterResourceRecord record) => record.Id == ReceiptId && record.ProviderIdentity == ReceiptProvider;

    public static FbxModelAuthoringImportResult Apply(
        FbxModelAuthoringImportResult model, Dl1CharacterDependencyDiscoveryResult discovery,
        IEnumerable<string>? explicitRootIds = null)
    {
        var package = model.Package;
        var inventory = package.Document.CharacterResources ?? throw new InvalidDataException("Character inventory is missing.");
        var scan = discovery.ScanProvenance ?? throw new InvalidDataException("Dependency discovery has no scan provenance.");
        var root = inventory.Resources.Single(resource => resource.Id == inventory.RootResourceId);
        var explicitIds = (explicitRootIds ?? []).ToHashSet(StringComparer.Ordinal);
        var candidateIds = discovery.Roots.Where(finding => finding.Status == Dl1CharacterDependencyStatus.Candidate &&
            finding.Selected is not null && !explicitIds.Contains(finding.Selected.Id.LogicalId.StableKey))
            .Select(finding => finding.Selected!.Id.LogicalId.StableKey).ToHashSet(StringComparer.Ordinal);
        var records = inventory.Resources.Where(resource => !IsReceipt(resource)).Select(resource =>
            candidateIds.Contains(resource.Id) ? resource with
            {
                Status = CharacterDependencyStatus.Ambiguous,
                Detail = "Original candidate retained; association requires review of its exact source declaration.",
            } : resource).ToImmutableArray();
        var problems = discovery.Roots.Concat(discovery.References).Where(finding =>
            !Dl1CharacterDependencyDiscovery.IsSatisfiedByRetainedMaterial(finding, inventory) && (!finding.Required ||
            finding.Status is Dl1CharacterDependencyStatus.Missing or Dl1CharacterDependencyStatus.Ambiguous or
                Dl1CharacterDependencyStatus.Malformed or Dl1CharacterDependencyStatus.SourceChanged or Dl1CharacterDependencyStatus.BoundExceeded))
            .Select(finding => new CharacterResourceRecord
            {
                Id = "discovery:finding:" + Hash(Encoding.UTF8.GetBytes(
                    finding.Basis + "\n" + finding.Status + "\n" + finding.Subsystem + "\n" + finding.RequestedName + "\n" + finding.ReferencedBy)),
                LogicalName = finding.RequestedName, Subsystem = finding.Subsystem,
                Status = finding.Status == Dl1CharacterDependencyStatus.Missing ? CharacterDependencyStatus.Missing :
                    finding.Status == Dl1CharacterDependencyStatus.Ambiguous ? CharacterDependencyStatus.Ambiguous : CharacterDependencyStatus.Unsupported,
                Detail = finding.Detail, ReferencedBy = [finding.ReferencedBy], Required = finding.Required,
            }).DistinctBy(resource => resource.Id).ToImmutableArray();
        if (!discovery.Diagnostics.IsEmpty)
            problems = problems.Add(new()
            {
                Id = "discovery:scan", LogicalName = "Dependency discovery", Subsystem = CharacterSubsystem.Helpers,
                Status = CharacterDependencyStatus.Unsupported, Detail = string.Join("; ", discovery.Diagnostics),
                ProviderIdentity = ReceiptProvider, SourceFingerprint = scan.CatalogSnapshotSha256,
                ReferencedBy = [inventory.RootResourceId],
            });
        var receipt = new CharacterDependencyScanReceipt
        {
            CharacterSourceSha256 = package.Document.Source.ContentSha256,
            RootContentSha256 = root.ContentSha256 ?? throw new InvalidDataException("Character root hash is missing."),
            Scan = scan,
        };
        Validate(receipt, package, root);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
        if (bytes.Length > MaximumReceiptBytes) throw new InvalidDataException("Dependency scan provenance exceeds its size limit.");
        string hash = Hash(bytes);
        string path = "character/resources/discovery-scan-" + hash + ".json";
        var record = new CharacterResourceRecord
        {
            Id = ReceiptId, LogicalName = "Dependency scan receipt", ProviderIdentity = ReceiptProvider,
            SourceFingerprint = scan.CatalogSnapshotSha256, ContentSha256 = hash, EntryPath = path, ByteLength = bytes.Length,
            Subsystem = CharacterSubsystem.Helpers, Status = CharacterDependencyStatus.Preserved, Required = false,
            ReferencedBy = [inventory.RootResourceId],
        };
        var payloads = package.CompanionPayloads;
        foreach (var prior in inventory.Resources.Where(IsReceipt))
            if (prior.EntryPath is not null) payloads = payloads.Remove(prior.EntryPath);
        var mergedRecords = records.ToBuilder();
        mergedRecords.AddRange(problems);
        mergedRecords.Add(record);
        inventory = inventory with { Resources = mergedRecords.ToImmutable() };
        inventory.Validate();
        return model with { Package = package with
        {
            Document = package.Document with { CharacterResources = inventory },
            CompanionPayloads = payloads.SetItem(path, ImmutableArray.Create(bytes)),
        } };
    }

    public static CharacterDependencyScanReceipt? Read(CustomModelPackage package)
    {
        var inventory = package.Document.CharacterResources ?? throw new InvalidDataException("Character inventory is missing.");
        var record = inventory.Resources.SingleOrDefault(IsReceipt);
        if (record is null) return null;
        if (record.EntryPath is null || !package.CompanionPayloads.TryGetValue(record.EntryPath, out var bytes) ||
            bytes.IsDefault || bytes.Length != record.ByteLength || bytes.Length > MaximumReceiptBytes ||
            !string.Equals(Hash(bytes.AsSpan()), record.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dependency scan receipt bytes or identity changed.");
        CharacterDependencyScanReceipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<CharacterDependencyScanReceipt>(bytes.AsSpan(), JsonOptions)
                ?? throw new InvalidDataException("Dependency scan receipt is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("Dependency scan receipt is malformed.", error); }
        var root = inventory.Resources.Single(resource => resource.Id == inventory.RootResourceId);
        Validate(receipt, package, root);
        if (record.SourceFingerprint != receipt.Scan.CatalogSnapshotSha256)
            throw new InvalidDataException("Dependency scan catalog provenance changed.");
        return receipt;
    }

    public static bool CanSupersedeLimits(CustomModelPackage current, CustomModelPackage refreshed)
    {
        var previous = Read(current);
        var incoming = Read(refreshed);
        if (incoming is null || !incoming.Scan.IsComplete) return false;
        if (previous is not null && (previous.Scan.CatalogSnapshotSha256 != incoming.Scan.CatalogSnapshotSha256 ||
            previous.Scan.HostContext != incoming.Scan.HostContext))
            throw new InvalidDataException("The dependency scan catalog or host context changed.");
        var root = current.Document.CharacterResources!.Resources.Single(resource =>
            resource.Id == current.Document.CharacterResources.RootResourceId);
        Validate(incoming, current, root);
        return true;
    }

    public static bool CanMakeConditionalRelicAdvisory(
        CharacterResourceRecord finding, CustomModelPackage current, CustomModelPackage refreshed)
    {
        if (finding.EntryPath is not null || finding.Subsystem != CharacterSubsystem.DetachedParts ||
            finding.Status is not (CharacterDependencyStatus.Missing or CharacterDependencyStatus.Ambiguous or CharacterDependencyStatus.Unsupported) ||
            finding.ReferencedBy.Length != 1)
            return false;
        var receipt = Read(refreshed);
        if (receipt is null || !receipt.Scan.IsComplete) return false;
        var original = current.Document.CharacterResources!;
        var incoming = refreshed.Document.CharacterResources!;
        var branch = receipt.Scan.GenericRelicBranches.SingleOrDefault(row => row.BodyResourceId == finding.ReferencedBy[0] || row.BodyPhysicalIdentitySha256 == Hash(Encoding.UTF8.GetBytes(finding.ReferencedBy[0])));
        if (branch is null || branch.SymbolSources.IsDefaultOrEmpty) return false;
        try
        {
            var bodySource = SameSource(branch.BodyResourceId);
            if (bodySource.ContentSha256 != branch.BodyContentSha256 ||
                !Path.GetExtension(bodySource.LogicalName).Equals(".bel", StringComparison.OrdinalIgnoreCase)) return false;
            var body = Dl1BodyElementsCodec.Read(Text(bodySource));
            if (!body.IsValid || !body.ForceGenericRelics) return false;
            var required = body.Elements.Select(element => element.ElementToken).Distinct(StringComparer.Ordinal).ToArray();
            var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var source in branch.SymbolSources)
            {
                var record = SameSource(source.ResourceId);
                if (record.ContentSha256 != source.ContentSha256 || record.SourceFingerprint != source.SourceFingerprint ||
                    Hash(Encoding.UTF8.GetBytes(record.ProviderIdentity)) != source.ProviderSha256) return false;
                var map = Dl1BodyElementSymbolMap.Read(Text(record), required);
                if (map.Diagnostics.Any(diagnostic => diagnostic.Code != "body_symbol_missing")) return false;
                foreach (var pair in map.Symbols) if (!symbols.TryAdd(pair.Key, pair.Value)) return false;
            }
            var selected = Dl1GenericRelicPreload.Select(body, symbols);
            if (!selected.IsComplete || !selected.Selections.SequenceEqual(branch.Selections) ||
                symbols.Count != branch.Symbols.Count || symbols.Any(pair => !branch.Symbols.TryGetValue(pair.Key, out int value) || value != pair.Value))
                return false;
            string rootName = original.Resources.Single(row => row.Id == original.RootResourceId).LogicalName.Replace('\\','/');
            string extension = Path.GetExtension(rootName);
            if (extension is ".msh" or ".skn" or ".msh_obj") rootName = rootName[..^extension.Length];
            bool matchesAlternative = selected.Selections.Any(row => new[]
            {
                rootName + row.RelicName + ".msh", rootName + "_" + row.RelicName + ".msh", row.RelicName + ".msh",
            }.Any(name => name.Equals(finding.LogicalName, StringComparison.OrdinalIgnoreCase) ||
                CharacterVirtualReferencePath.Resolve(name, bodySource.LogicalName).CanonicalName.Equals(finding.LogicalName, StringComparison.OrdinalIgnoreCase)));
            if (!matchesAlternative) return false;
            foreach (var row in selected.Selections.Where(row => row.Selection == Dl1RelicPreloadSelection.Generic))
            {
                string canonical = CharacterVirtualReferencePath.Resolve(row.Name!, bodySource.LogicalName).CanonicalName;
                string stem = Path.GetFileNameWithoutExtension(row.Name ?? throw new InvalidDataException("Generic relic selection has no exact resource name."));
                bool inventoried = incoming.Resources.Any(record => record.Required &&
                    record.Subsystem == CharacterSubsystem.DetachedParts &&
                    record.ReferencedBy.Contains(bodySource.Id, StringComparer.Ordinal) &&
                    (record.EntryPath is null && record.Status is CharacterDependencyStatus.Missing or CharacterDependencyStatus.Unsupported or CharacterDependencyStatus.Ambiguous &&
                        (record.LogicalName.Equals(canonical, StringComparison.OrdinalIgnoreCase) || record.LogicalName.Equals(row.Name, StringComparison.OrdinalIgnoreCase)) ||
                     record.EntryPath is not null && record.NativeResource is { ResourceType: 272 } native && native.ResourceName.Equals(stem, StringComparison.OrdinalIgnoreCase)));
                if (!inventoried) return false;
            }
            return true;

            CharacterResourceRecord SameSource(string id)
            {
                var old = original.Resources.Single(record => record.Id == id && !record.IsOriginalArchive);
                var fresh = incoming.Resources.Single(record => record.Id == id && !record.IsOriginalArchive);
                if (old.LogicalName != fresh.LogicalName || old.ProviderIdentity != fresh.ProviderIdentity ||
                    old.SourceFingerprint != fresh.SourceFingerprint || old.ContentSha256 != fresh.ContentSha256 ||
                    old.ByteLength != fresh.ByteLength || !SourceBytes(old, current).AsSpan().SequenceEqual(SourceBytes(fresh, refreshed).AsSpan()))
                    throw new InvalidDataException("Generic relic branch source changed.");
                return old;
            }
            string Text(CharacterResourceRecord source)
            {
                var bytes = SourceBytes(source, current);
                bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
                return new UTF8Encoding(false,true).GetString(bytes.AsSpan()[(bom?3:0)..]);
            }
        }
        catch (Exception error) when (error is ArgumentException or FormatException or IOException or InvalidOperationException)
        { return false; }
    }

    public static bool CanSupersedeIndexedTemplate(
        CharacterResourceRecord finding, CustomModelPackage current, CustomModelPackage refreshed)
    {
        if (finding.EntryPath is not null || finding.Subsystem != CharacterSubsystem.DetachedParts ||
            finding.Status is not (CharacterDependencyStatus.Missing or CharacterDependencyStatus.Unsupported or CharacterDependencyStatus.Ambiguous) ||
            finding.ReferencedBy.Length != 1)
            return false;
        var original = current.Document.CharacterResources!;
        var incoming = refreshed.Document.CharacterResources!;
        var source = original.Resources.SingleOrDefault(record => record.Id == finding.ReferencedBy[0] &&
            !record.IsOriginalArchive && record.EntryPath is not null &&
            Path.GetExtension(record.LogicalName).Equals(".bel", StringComparison.OrdinalIgnoreCase));
        if (source is null) return false;
        var fresh = incoming.Resources.SingleOrDefault(record => record.Id == source.Id && !record.IsOriginalArchive);
        if (fresh is null || fresh.LogicalName != source.LogicalName || fresh.ProviderIdentity != source.ProviderIdentity ||
            fresh.SourceFingerprint != source.SourceFingerprint || fresh.ByteLength != source.ByteLength ||
            !string.Equals(fresh.ContentSha256, source.ContentSha256, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var bytes = SourceBytes(source, current);
            if (!bytes.AsSpan().SequenceEqual(SourceBytes(fresh, refreshed).AsSpan())) return false;
            bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var text = new UTF8Encoding(false, true).GetString(bytes.AsSpan()[(bom ? 3 : 0)..]);
            var document = Dl1BodyElementsCodec.Read(text);
            if (!document.IsValid) return false;
            var matches = document.DestroyedHeadPartDeclarations.Where(declaration =>
                declaration.Template == finding.LogicalName ||
                CharacterVirtualReferencePath.Resolve(declaration.Template, source.LogicalName).CanonicalName == finding.LogicalName).ToArray();
            if (matches.Length != 1) return false;
            var names = Dl1IndexedMeshTemplate.Expand(matches[0].Template, matches[0].Count);
            foreach (var name in names)
            {
                var resolved = CharacterVirtualReferencePath.Resolve(name.Name, source.LogicalName);
                string stem = Path.GetFileNameWithoutExtension(resolved.CanonicalName);
                bool diagnosed = incoming.Resources.Any(record => record.Subsystem == CharacterSubsystem.DetachedParts &&
                    record.EntryPath is null && record.Status is CharacterDependencyStatus.Missing or CharacterDependencyStatus.Unsupported or CharacterDependencyStatus.Ambiguous &&
                    record.ReferencedBy.Contains(source.Id, StringComparer.Ordinal) &&
                    (record.LogicalName == resolved.CanonicalName || record.LogicalName == name.Name));
                if (diagnosed) continue;
                var native = incoming.Resources.Where(record => !record.IsOriginalArchive &&
                    record.Subsystem == CharacterSubsystem.DetachedParts && record.EntryPath is not null &&
                    record.ReferencedBy.Contains(source.Id, StringComparer.Ordinal) &&
                    record.NativeResource is { ResourceType: 272 } receipt &&
                    (receipt.ResourceName == resolved.CanonicalName[..^Path.GetExtension(resolved.CanonicalName).Length] ||
                        !resolved.RequiresExactLookup && receipt.ResourceName == stem)).ToArray();
                if (native.Length != 1) return false;
                native[0].NativeResource!.Validate(native[0]);
                var payload = SourceBytes(native[0], refreshed);
                foreach (var item in native[0].NativeResource!.Items)
                    if (!string.Equals(Hash(payload.AsSpan((int)item.PayloadOffset, item.ByteLength)), item.ContentSha256,
                        StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or IOException)
        {
            return false;
        }
    }

    private static ImmutableArray<byte> SourceBytes(CharacterResourceRecord resource, CustomModelPackage package)
    {
        if (resource.EntryPath is null || !package.CompanionPayloads.TryGetValue(resource.EntryPath, out var bytes) ||
            bytes.IsDefault || bytes.Length != resource.ByteLength ||
            !string.Equals(Hash(bytes.AsSpan()), resource.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Indexed source payload does not match its identity.");
        return bytes;
    }

    public static bool IsKnownLimitFinding(CharacterResourceRecord resource)
    {
        if (resource.Id != "discovery:scan" || resource.EntryPath is not null ||
            resource.Status != CharacterDependencyStatus.Unsupported) return false;
        string remaining = resource.Detail.Replace(ScriptLimit, string.Empty, StringComparison.Ordinal)
            .Replace(GraphLimit, string.Empty, StringComparison.Ordinal);
        remaining = Regex.Replace(remaining, @"Dependency graph depth bound reached at [^;\r\n]+\.", string.Empty,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return remaining != resource.Detail && remaining.Trim(' ', ';').Length == 0;
    }

    private static void Validate(CharacterDependencyScanReceipt receipt, CustomModelPackage package, CharacterResourceRecord root)
    {
        var scan = receipt.Scan;
        if (scan is null || scan.Bounds is null) throw new InvalidDataException("Dependency scan provenance is missing.");
        if (receipt.Format != "dl-reanimated-character-discovery-scan-v1" ||
            receipt.CharacterSourceSha256 != package.Document.Source.ContentSha256 || receipt.RootContentSha256 != root.ContentSha256 ||
            scan.RootLogicalId != root.Id || scan.RootProviderSha256 != Hash(Encoding.UTF8.GetBytes(root.ProviderIdentity)) ||
            scan.RootSourceFingerprint != root.SourceFingerprint ||
            scan.RootContentFingerprint is not null && !string.Equals(scan.RootContentFingerprint, root.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
            !IsHash(scan.CatalogSnapshotSha256) || !IsHash(scan.FinalCatalogSnapshotSha256) ||
            !Enum.IsDefined(scan.HostContext) || scan.Bounds.HostContext != scan.HostContext ||
            scan.EffectiveScriptCount < 0 || scan.AttemptedScriptCount < 0 || scan.AttemptedScriptCount > scan.EffectiveScriptCount ||
            scan.Bounds.MaximumScannedScripts is < 1 or > 65536 || scan.Bounds.MaximumGraphResources is < 1 or > 65536 ||
            scan.Bounds.MaximumGraphDepth is < 1 or > 64 || scan.Bounds.MaximumSourceBytes is < 1 or > 4 * 1024 * 1024 ||
            scan.Bounds.MaximumTotalSourceBytes < scan.Bounds.MaximumSourceBytes ||
            scan.TotalSourceBytes < 0 || scan.TraversedResourceCount < 0 ||
            scan.Observations.IsDefault || scan.LimitFindings.IsDefault || scan.GenericRelicBranches.IsDefault ||
            scan.Observations.Length > scan.Bounds.MaximumScannedScripts + scan.Bounds.MaximumGraphResources ||
            scan.Observations.Length < scan.AttemptedScriptCount ||
            scan.Observations.Any(observation => observation is null || !IsHash(observation.AssetIdentitySha256) ||
                observation.ContentSha256 is not null && !IsHash(observation.ContentSha256) || !Enum.IsDefined(observation.Status)) ||
            scan.Observations.Select(observation => observation.AssetIdentitySha256).Distinct(StringComparer.Ordinal).Count() != scan.Observations.Length)
            throw new InvalidDataException("Dependency scan root, catalog or coverage provenance is invalid.");
    }

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}