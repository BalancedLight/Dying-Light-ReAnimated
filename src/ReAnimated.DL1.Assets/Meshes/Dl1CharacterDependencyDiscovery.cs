using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Meshes;

public enum Dl1CharacterHostContext { Unspecified, StandardHumanAiVis }

/// <summary>Verified means a source-level dependency with an exact catalog identity, not a runtime behavior claim.</summary>
public enum Dl1CharacterDependencyStatus { Verified, Candidate, Missing, Ambiguous, Malformed, SourceChanged, BoundExceeded }

public enum Dl1CharacterDependencyBasis
{
    StandardHumanBel,
    StandardHumanBelFallback,
    ExplicitMeshDeclaration,
    Include,
    DeclaredResource,
    SourceMention,
    ConditionalRelicMesh,
    GenericRelicPreload,
    AutomaticClothName,
    HumanRagdollDefault,
    GlobalMimicScript,
}

public sealed record Dl1CharacterDependencyDiscoveryOptions
{
    public Dl1CharacterHostContext HostContext { get; init; }
    public int MaximumScannedScripts { get; init; } = 2048;
    public int MaximumGraphResources { get; init; } = 256;
    public int MaximumGraphDepth { get; init; } = 16;
    public int MaximumSourceBytes { get; init; } = NativeCharacterScriptCodec.MaximumCharacters;
    public long MaximumTotalSourceBytes { get; init; } = 64L * 1024 * 1024;
}

/// <summary>Selected is a precedence-resolved physical asset; candidates retain shadowed providers and ambiguous logical names.</summary>
public sealed record Dl1CharacterDependencyFinding(
    Dl1CharacterDependencyBasis Basis,
    Dl1CharacterDependencyStatus Status,
    CharacterSubsystem Subsystem,
    string RequestedName,
    string ReferencedBy,
    RetailAssetRecord? Selected,
    ImmutableArray<RetailAssetRecord> Candidates,
    string? ObservedSha256,
    string Detail)
{
    public bool Required { get; init; } = true;
}

public sealed record Dl1CharacterDependencySourceObservation(
    string AssetIdentitySha256, string? ContentSha256, Dl1CharacterDependencyStatus Status);

public sealed record Dl1GenericRelicSymbolSource(
    string ResourceId, string ProviderSha256, string SourceFingerprint, string ContentSha256);
public sealed record Dl1GenericRelicBranchEvidence(
    string BodyResourceId, string BodyContentSha256,
    ImmutableArray<Dl1GenericRelicSymbolSource> SymbolSources,
    ImmutableDictionary<string, int> Symbols,
    ImmutableArray<Dl1RelicPreloadDependency> Selections)
{
    public string BodyPhysicalIdentitySha256 { get; init; } = string.Empty;
}

public sealed record Dl1CharacterDependencyScanProvenance
{
    public string RootLogicalId { get; init; } = string.Empty;
    public string RootProviderSha256 { get; init; } = string.Empty;
    public string RootSourceFingerprint { get; init; } = string.Empty;
    public string? RootContentFingerprint { get; init; }
    public string CatalogSnapshotSha256 { get; init; } = string.Empty;
    public string FinalCatalogSnapshotSha256 { get; init; } = string.Empty;
    public Dl1CharacterHostContext HostContext { get; init; }
    public int EffectiveScriptCount { get; init; }
    public int AttemptedScriptCount { get; init; }
    public int TraversedResourceCount { get; init; }
    public long TotalSourceBytes { get; init; }
    public Dl1CharacterDependencyDiscoveryOptions Bounds { get; init; } = new();
    public ImmutableArray<Dl1CharacterDependencySourceObservation> Observations { get; init; } = [];
    public ImmutableArray<string> LimitFindings { get; init; } = [];
    public ImmutableArray<Dl1GenericRelicBranchEvidence> GenericRelicBranches { get; init; } = [];

    public bool IsComplete => EffectiveScriptCount == AttemptedScriptCount &&
        AttemptedScriptCount <= Bounds.MaximumScannedScripts && TraversedResourceCount <= Bounds.MaximumGraphResources &&
        TotalSourceBytes <= Bounds.MaximumTotalSourceBytes && LimitFindings.IsEmpty &&
        CatalogSnapshotSha256 == FinalCatalogSnapshotSha256 &&
        !Observations.Any(observation => observation.Status is Dl1CharacterDependencyStatus.BoundExceeded or Dl1CharacterDependencyStatus.SourceChanged);
}

public sealed record Dl1CharacterDependencyDiscoveryResult(
    ImmutableArray<Dl1CharacterDependencyFinding> Roots,
    ImmutableArray<Dl1CharacterDependencyFinding> References,
    ImmutableArray<string> Diagnostics)
{
    public Dl1CharacterDependencyScanProvenance? ScanProvenance { get; init; }
    public ImmutableArray<RetailAssetLogicalId> VerifiedCompanionRoots => Roots
        .Where(static finding => finding.Status == Dl1CharacterDependencyStatus.Verified && finding.Selected is not null)
        .Select(static finding => finding.Selected!.Id.LogicalId)
        .Distinct()
        .ToImmutableArray();
}

/// <summary>
/// Bounded source-custody discovery. Only explicit source declarations and the opt-in
/// standard HumanAIVis BEL rule establish roots. Filename resemblance alone never does.
/// </summary>
public static class Dl1CharacterDependencyDiscovery
{
    public static bool IsSatisfiedByRetainedMaterial(Dl1CharacterDependencyFinding finding,CharacterResourceInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(finding);ArgumentNullException.ThrowIfNull(inventory);
        return finding.Status==Dl1CharacterDependencyStatus.Missing && finding.Subsystem==CharacterSubsystem.Materials &&
            inventory.Resources.Any(resource=>!resource.IsOriginalArchive && resource.EntryPath is not null &&
                resource.Status is CharacterDependencyStatus.Preserved or CharacterDependencyStatus.Decoded && resource.Material is not null &&
                resource.LogicalName.Equals(finding.RequestedName,StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> MeshDeclarations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mesh", "Model", "LoadMesh", "LoadModel", "SetMesh", "SetModel", "SetMeshName", "SetModelName",
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pre", ".scr", ".def", ".phx", ".mpcloth", ".bel", ".ascr", ".bscr", ".chr", ".fed",
    };

    public static async Task<Dl1CharacterDependencyDiscoveryResult> DiscoverAsync(
        Dl1MeshData character, RetailAssetRecord root, IRetailAssetCatalog catalog,
        Dl1CharacterDependencyDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(catalog);
        options ??= new();
        if (options.MaximumScannedScripts is < 1 or > 65536 ||
            options.MaximumGraphResources is < 1 or > 65536 ||
            options.MaximumGraphDepth is < 1 or > 64 ||
            options.MaximumSourceBytes is < 1 or > NativeCharacterScriptCodec.MaximumCharacters ||
            options.MaximumTotalSourceBytes < options.MaximumSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(options), "Character discovery bounds are invalid.");

        string meshStem = Normalize(character.ResourceName);
        if (meshStem.Length == 0) throw new ArgumentException("A decoded character resource name is required.", nameof(character));
        meshStem = RemoveMeshExtension(meshStem);
        var roots = ImmutableArray.CreateBuilder<Dl1CharacterDependencyFinding>();
        var references = ImmutableArray.CreateBuilder<Dl1CharacterDependencyFinding>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var cache = new Dictionary<string, SourceRead>(StringComparer.Ordinal);
        long totalRead = 0;
        int attemptedScripts = 0;
        var parseOutcomes = new Dictionary<string, Dl1CharacterDependencyStatus>(StringComparer.Ordinal);
        var genericBranches = ImmutableArray.CreateBuilder<Dl1GenericRelicBranchEvidence>();
        string catalogSnapshot = CatalogSnapshot(catalog);

        if (options.HostContext == Dl1CharacterHostContext.StandardHumanAiVis)
        {
            string requested = meshStem + ".bel";
            Dl1CharacterDependencyFinding bel = ResolveReference(requested,
                Dl1CharacterDependencyBasis.StandardHumanBel, CharacterSubsystem.Damage,
                root.Id.StableKey, exactIsVerified: true);
            // A basename candidate may be the requested file under another virtual path;
            // do not infer that the native absent-resource fallback has fired.
            if (bel.Status == Dl1CharacterDependencyStatus.Missing)
                bel = ResolveReference("default_elements.bel",
                    Dl1CharacterDependencyBasis.StandardHumanBelFallback,
                    CharacterSubsystem.Damage, root.Id.StableKey, exactIsVerified: true);
            if (bel.Selected is not null && bel.Status == Dl1CharacterDependencyStatus.Verified)
                bel = await ValidateBelAsync(bel).ConfigureAwait(false);
            roots.Add(bel);
        }

        // Exact native auto/empty cloth consumers derive mesh stem + .mpcloth.
        // Preserve a matching candidate without asserting the actor uses that mode.
        var cloth=ResolveReference(meshStem+".mpcloth",Dl1CharacterDependencyBasis.AutomaticClothName,CharacterSubsystem.Cloth,root.Id.StableKey,exactIsVerified:false);
        if(cloth.Selected is not null || cloth.Status==Dl1CharacterDependencyStatus.Ambiguous)
            roots.Add(cloth with {Status=cloth.Status==Dl1CharacterDependencyStatus.Verified?Dl1CharacterDependencyStatus.Candidate:cloth.Status,
                Detail="Native automatic cloth selection derives this name; the actor's auto/default cloth context must be reviewed. "+cloth.Detail});

        if(options.HostContext==Dl1CharacterHostContext.StandardHumanAiVis)
        {
            var ragdoll=ResolveReference("ragdoll_ai.phx",Dl1CharacterDependencyBasis.HumanRagdollDefault,CharacterSubsystem.Ragdoll,root.Id.StableKey,exactIsVerified:false);
            if(ragdoll.Selected is not null || ragdoll.Status==Dl1CharacterDependencyStatus.Ambiguous)
                roots.Add(ragdoll with {Detail="Native standard human ragdoll default candidate. Configured overrides and game-mode variants require actor-context review. "+ragdoll.Detail});
        }
        if(character.MorphTargets.Count>0)
        {
            var mimic=ResolveReference("FaceMimic.scr",Dl1CharacterDependencyBasis.GlobalMimicScript,CharacterSubsystem.FacialDefinitions,root.Id.StableKey,exactIsVerified:false);
            if(mimic.Selected is not null || mimic.Status==Dl1CharacterDependencyStatus.Ambiguous)
                roots.Add(mimic with {Detail="Native global mimic definition retained. The actor's selected mimic set and FED name require separate review; no mesh-stem FED is inferred. "+mimic.Detail});
        }

        RetailAssetRecord[] scripts = catalog.Assets
            .Where(static asset => asset.Id.Namespace == RetailAssetNamespace.VirtualFile &&
                SourceExtensions.Contains(Path.GetExtension(asset.Id.Name)))
            .GroupBy(static asset => asset.Id.LogicalId.StableKey, StringComparer.Ordinal)
            .Select(group => catalog.Resolve(group.First().Id.LogicalId))
            .Where(static asset => asset is not null)
            .Cast<RetailAssetRecord>()
            .OrderBy(static asset => Path.GetExtension(asset.Id.Name).Equals(".pre", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static asset => asset.Id.LogicalId.StableKey, StringComparer.Ordinal)
            .ToArray();
        if (scripts.Length > options.MaximumScannedScripts)
            diagnostics.Add("Source-script scan was bounded; unscanned scripts may contain additional explicit declarations.");
        foreach (RetailAssetRecord script in scripts.Take(options.MaximumScannedScripts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptedScripts++;
            SourceRead source = await ReadAsync(script).ConfigureAwait(false);
            if (source.Text is null) continue;
            NativeCharacterScriptDocument syntax;
            try { syntax = NativeCharacterScriptCodec.Parse(source.Text); }
            catch (FormatException) { parseOutcomes[script.Id.StableKey] = Dl1CharacterDependencyStatus.Malformed; continue; }
            bool presetSource = Path.GetExtension(script.Id.Name).Equals(".pre", StringComparison.OrdinalIgnoreCase);
            if (!syntax.Calls.Where((call, index) => MeshDeclarations.Contains(call.Name) ||
                    presetSource && CharacterActorSourceReview.IsSupportedModelDeclaration(syntax.Calls, index))
                .Any(call => (CharacterActorSourceReview.GetModelNameArgument(call) ??
                    call.QuotedArguments.FirstOrDefault(argument => argument.ArgumentIndex == 0)) is { } modelName &&
                    ReferencesRoot(modelName.Value, root.Id.LogicalId, meshStem))) continue;
            Dl1CharacterDependencyStatus status = source.Status == Dl1CharacterDependencyStatus.Verified
                ? Dl1CharacterDependencyStatus.Candidate : source.Status;
            roots.Add(new(Dl1CharacterDependencyBasis.ExplicitMeshDeclaration, status,
                Classify(script.Id.Name, syntax), script.Id.Name, root.Id.StableKey,
                script, catalog.GetCandidates(script.Id.LogicalId).ToImmutableArray(), source.Sha256,
                "Source explicitly declares the selected mesh; native wrapper semantics require review."));
        }

        var queue = new Queue<(bool Root, int Index, int Depth)>();
        for (int index = 0; index < roots.Count; index++)
            if (roots[index].Selected is not null && roots[index].Status is Dl1CharacterDependencyStatus.Verified or Dl1CharacterDependencyStatus.Candidate)
                queue.Enqueue((true, index, 0));
        var traversed = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dl1CharacterDependencyFinding parent = item.Root ? roots[item.Index] : references[item.Index];
            RetailAssetRecord asset = parent.Selected!;
            if (!traversed.Add(asset.Id.StableKey)) continue;
            if (traversed.Count > options.MaximumGraphResources)
            {
                diagnostics.Add("Dependency graph resource bound reached; closure is incomplete.");
                break;
            }
            SourceRead source = await ReadAsync(asset).ConfigureAwait(false);
            if (source.Status != Dl1CharacterDependencyStatus.Verified || source.Text is null)
            {
                Update(item, parent with { Status = source.Status, ObservedSha256 = source.Sha256,
                    Detail = source.Detail });
                continue;
            }
            NativeCharacterScriptDocument syntax;
            try { syntax = NativeCharacterScriptCodec.Parse(source.Text); }
            catch (FormatException)
            {
                Update(item, parent with { Status = Dl1CharacterDependencyStatus.Malformed,
                    ObservedSha256 = source.Sha256, Detail = "Source script syntax is malformed; references were not inferred." });
                continue;
            }
            foreach (var call in syntax.Calls.Where(call => call.Name == "DestroyedHeadParts"))
            {
                string template = call.QuotedArguments.FirstOrDefault(argument => argument.ArgumentIndex == 0)?.Value
                    ?? call.Arguments.FirstOrDefault() ?? "DestroyedHeadParts";
                try
                {
                    if (call.Arguments.Length < 2 || !call.QuotedArguments.Any(argument => argument.ArgumentIndex == 0))
                        throw new FormatException("DestroyedHeadParts requires a quoted template and integer count.");
                    _ = Dl1IndexedMeshTemplate.Expand(template, Dl1IndexedMeshTemplate.ParseCount(call.Arguments[1]));
                }
                catch (FormatException error)
                {
                    references.Add(new(Dl1CharacterDependencyBasis.DeclaredResource, Dl1CharacterDependencyStatus.Malformed,
                        CharacterSubsystem.DetachedParts, template, asset.Id.StableKey, null, [], source.Sha256,
                        "Indexed detached mesh declaration is invalid: " + error.Message));
                }
            }
            if (Path.GetExtension(asset.Id.Name).Equals(".bel", StringComparison.OrdinalIgnoreCase) &&
                !Dl1BodyElementsCodec.Read(source.Text).IsValid)
            {
                Update(item, parent with { Status = Dl1CharacterDependencyStatus.Malformed,
                    ObservedSha256 = source.Sha256, Detail = "BEL syntax is malformed; the native absent-file fallback is not inferred." });
                continue;
            }
            Update(item, parent with { ObservedSha256 = source.Sha256,
                Subsystem = Classify(asset.Id.Name, syntax,
                    parent.Subsystem == CharacterSubsystem.Cloth) });
            if (Path.GetExtension(asset.Id.Name).Equals(".pre", StringComparison.OrdinalIgnoreCase))
            {
                // One preset source can contain many actors. Preserve the full source;
                // dependency review must select the intended preset's exact scope.
                Update(item, parent with { ObservedSha256 = source.Sha256, Subsystem = CharacterSubsystem.Helpers,
                    Detail = "Preset source retained. Select its model declaration and review the scoped dependencies." });
                continue;
            }
            if (item.Depth >= options.MaximumGraphDepth)
            {
                diagnostics.Add("Dependency graph depth bound reached at " + asset.Id.LogicalId.StableKey + ".");
                continue;
            }
            Dl1RelicPreloadResult? genericSelection = null;
            if (Path.GetExtension(asset.Id.Name).Equals(".bel", StringComparison.OrdinalIgnoreCase))
            {
                var body = Dl1BodyElementsCodec.Read(source.Text);
                if (body.ForceGenericRelics)
                    genericSelection = await GatherGenericBranchAsync(body, asset, source, item.Depth).ConfigureAwait(false);
            }
            foreach ((string name, Dl1CharacterDependencyBasis basis, CharacterSubsystem subsystem) in
                     ExtractReferences(syntax, asset.Id.Name, meshStem))
            {
                Dl1CharacterDependencyFinding found = ResolveReference(name, basis, subsystem,
                    asset.Id.StableKey, exactIsVerified: parent.Status == Dl1CharacterDependencyStatus.Verified &&
                    basis is not (Dl1CharacterDependencyBasis.ConditionalRelicMesh or Dl1CharacterDependencyBasis.SourceMention));
                if (basis == Dl1CharacterDependencyBasis.ConditionalRelicMesh && genericSelection is { IsComplete: true })
                    found = found with { Required = false, Detail = "Alternative relic declaration outside this generic preload branch. " + found.Detail };
                references.Add(found);
                int index = references.Count - 1;
                if (found.Selected is not null && found.Status is Dl1CharacterDependencyStatus.Verified or Dl1CharacterDependencyStatus.Candidate &&
                    (SourceExtensions.Contains(Path.GetExtension(found.Selected.Id.Name)) ||
                     Path.GetExtension(found.Selected.Id.Name).Equals(".fx",StringComparison.OrdinalIgnoreCase)))
                    queue.Enqueue((false, index, item.Depth + 1));
            }
        }
        return new(roots.ToImmutable(), references.ToImmutable(), diagnostics.ToImmutable())
        {
            ScanProvenance = new()
            {
                RootLogicalId = root.Id.LogicalId.StableKey,
                RootProviderSha256 = HashIdentity(root.Id.ProviderId),
                RootSourceFingerprint = root.Id.SourceFingerprint,
                RootContentFingerprint = root.Id.ContentFingerprint,
                CatalogSnapshotSha256 = catalogSnapshot,
                FinalCatalogSnapshotSha256 = CatalogSnapshot(catalog),
                HostContext = options.HostContext, Bounds = options,
                EffectiveScriptCount = scripts.Length, AttemptedScriptCount = attemptedScripts,
                TraversedResourceCount = traversed.Count, TotalSourceBytes = totalRead,
                LimitFindings = diagnostics.ToImmutable(),
                GenericRelicBranches = genericBranches.ToImmutable(),
                Observations = cache.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                    new Dl1CharacterDependencySourceObservation(HashIdentity(pair.Key), pair.Value.Sha256, parseOutcomes.GetValueOrDefault(pair.Key, pair.Value.Status))).ToImmutableArray(),
            },
        };

        async Task<Dl1RelicPreloadResult?> GatherGenericBranchAsync(
            Dl1BodyElementsDocument body, RetailAssetRecord bel, SourceRead belSource, int parentDepth)
        {
            var required = body.Elements.Select(element => element.ElementToken).Distinct(StringComparer.Ordinal).ToArray();
            var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
            var symbolSources = ImmutableArray.CreateBuilder<Dl1GenericRelicSymbolSource>();
            var visitedIncludes = new HashSet<string>(StringComparer.Ordinal);
            var includes = new Queue<(RetailAssetRecord Asset, int Depth)>();
            var errors = new List<string>();
            AddIncludes(body.Syntax, bel, parentDepth);
            while (includes.TryDequeue(out var next))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visitedIncludes.Add(next.Asset.Id.StableKey)) continue;
                if (next.Depth > options.MaximumGraphDepth || visitedIncludes.Count > options.MaximumGraphResources)
                {
                    diagnostics.Add("Generic relic symbol include scan reached its graph bounds.");
                    errors.Add("The exact symbol include chain is incomplete.");
                    break;
                }
                var included = await ReadAsync(next.Asset).ConfigureAwait(false);
                if (included.Status != Dl1CharacterDependencyStatus.Verified || included.Text is null || included.Sha256 is null)
                {
                    errors.Add("The exact symbol source cannot be verified: " + next.Asset.Id.Name);
                    continue;
                }
                Dl1BodyElementSymbolMapResult map;
                try { map = Dl1BodyElementSymbolMap.Read(included.Text, required); }
                catch (Exception error) when (error is FormatException or ArgumentException)
                { errors.Add("The symbol source is malformed: " + next.Asset.Id.Name); continue; }
                symbolSources.Add(new(next.Asset.Id.LogicalId.StableKey, HashIdentity(next.Asset.Id.ProviderId),
                    next.Asset.Id.SourceFingerprint, included.Sha256));
                foreach (var diagnostic in map.Diagnostics.Where(row => row.Code != "body_symbol_missing"))
                    errors.Add(diagnostic.Message);
                foreach (var symbol in map.Symbols)
                    if (!symbols.TryAdd(symbol.Key, symbol.Value))
                        errors.Add("Body symbol has multiple effective source declarations: " + symbol.Key);
                AddIncludes(map.Syntax, next.Asset, next.Depth);
            }
            var selection = Dl1GenericRelicPreload.Select(body, symbols);
            errors.AddRange(selection.Diagnostics.Where(row => row.IsError || row.Code == "relic_preload_unresolved").Select(row => row.Message));
            if (errors.Count > 0 || !selection.IsComplete || symbolSources.Count == 0)
            {
                references.Add(new(Dl1CharacterDependencyBasis.GenericRelicPreload, Dl1CharacterDependencyStatus.Malformed,
                    CharacterSubsystem.DetachedParts, "Generic relic symbols", bel.Id.StableKey, null, [], belSource.Sha256,
                    string.Join("; ", errors.Count > 0 ? errors : ["The exact body symbol include map is unavailable."])));
                return null;
            }
            genericBranches.Add(new(bel.Id.LogicalId.StableKey, belSource.Sha256!, symbolSources.ToImmutable(),
                symbols.ToImmutableDictionary(StringComparer.Ordinal), selection.Selections) { BodyPhysicalIdentitySha256 = HashIdentity(bel.Id.StableKey) });
            foreach (var row in selection.Selections)
            {
                if (row.Selection == Dl1RelicPreloadSelection.Generic)
                    references.Add(ResolveReference(row.Name!, Dl1CharacterDependencyBasis.GenericRelicPreload,
                        CharacterSubsystem.DetachedParts, bel.Id.StableKey, exactIsVerified: true) with { ReferencedBy = bel.Id.LogicalId.StableKey });
                else if (row.Selection == Dl1RelicPreloadSelection.Excluded)
                    references.Add(new(Dl1CharacterDependencyBasis.GenericRelicPreload, Dl1CharacterDependencyStatus.Candidate,
                        CharacterSubsystem.DetachedParts, row.RelicName, bel.Id.LogicalId.StableKey, null, [], belSource.Sha256,
                        "Body-element declaration excluded from this generic preload gather.") { Required = false });
            }
            return selection;

            void AddIncludes(NativeCharacterScriptDocument syntax, RetailAssetRecord declaring, int depth)
            {
                foreach (var call in syntax.Calls.Where(call => call.Name == "!include"))
                {
                    string? request = call.QuotedArguments.FirstOrDefault(argument => argument.ArgumentIndex == 0)?.Value;
                    if (request is null) { errors.Add("The symbol include declaration is not an exact quoted path."); continue; }
                    try
                    {
                        string name = CharacterVirtualReferencePath.Resolve(request, declaring.Id.Name).CanonicalName;
                        var logical = RetailAssetLogicalId.VirtualFile(name);
                        var asset = catalog.Resolve(logical);
                        if (asset is null || catalog.GetCandidates(logical).Count == 0)
                            errors.Add("The exact symbol include is unavailable: " + name);
                        else includes.Enqueue((asset, depth + 1));
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidDataException)
                    { errors.Add("The symbol include path is unsafe."); }
                }
            }
        }

        async Task<Dl1CharacterDependencyFinding> ValidateBelAsync(Dl1CharacterDependencyFinding finding)
        {
            SourceRead source = await ReadAsync(finding.Selected!).ConfigureAwait(false);
            if (source.Status != Dl1CharacterDependencyStatus.Verified || source.Text is null)
                return finding with { Status = source.Status, ObservedSha256 = source.Sha256, Detail = source.Detail };
            try
            {
                if (!Dl1BodyElementsCodec.Read(source.Text).IsValid)
                    return finding with { Status = Dl1CharacterDependencyStatus.Malformed,
                        ObservedSha256 = source.Sha256, Detail = "Requested BEL has invalid statements; do not use absent-file fallback." };
            }
            catch (FormatException)
            {
                return finding with { Status = Dl1CharacterDependencyStatus.Malformed,
                    ObservedSha256 = source.Sha256, Detail = "Requested BEL syntax is malformed; do not use absent-file fallback." };
            }
            return finding with { ObservedSha256 = source.Sha256 };
        }

        void Update((bool Root, int Index, int Depth) location, Dl1CharacterDependencyFinding finding)
        {
            if (location.Root) roots[location.Index] = finding;
            else references[location.Index] = finding;
        }

        async Task<SourceRead> ReadAsync(RetailAssetRecord asset)
        {
            if (cache.TryGetValue(asset.Id.StableKey, out SourceRead? cached)) return cached;
            try
            {
                await using Stream stream = await catalog.OpenReadAsync(asset, cancellationToken).ConfigureAwait(false);
                using var bytes = new MemoryStream();
                byte[] buffer = new byte[16 * 1024];
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (bytes.Length + count > options.MaximumSourceBytes || totalRead + count > options.MaximumTotalSourceBytes)
                    {
                        var bounded = new SourceRead(null, null, Dl1CharacterDependencyStatus.BoundExceeded,
                            "Source exceeds discovery read bounds; no association was promoted.");
                        cache[asset.Id.StableKey] = bounded;
                        return bounded;
                    }
                    bytes.Write(buffer, 0, count);
                    totalRead += count;
                }
                byte[] payload = bytes.ToArray();
                string hash = Convert.ToHexStringLower(SHA256.HashData(payload));
                string? expected = asset.Id.ContentFingerprint;
                string text = new UTF8Encoding(false, true).GetString(payload);
                if (expected is not null && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    var changed = new SourceRead(text, hash, Dl1CharacterDependencyStatus.SourceChanged,
                        "Catalog content fingerprint differs from current source bytes.");
                    cache[asset.Id.StableKey] = changed;
                    return changed;
                }
                var result = new SourceRead(text, hash, Dl1CharacterDependencyStatus.Verified, "Current source bytes match catalog content identity when supplied.");
                cache[asset.Id.StableKey] = result;
                return result;
            }
            catch (DecoderFallbackException)
            {
                var result = new SourceRead(null, null, Dl1CharacterDependencyStatus.Malformed,
                    "Source text is not valid UTF-8; references were not inferred.");
                cache[asset.Id.StableKey] = result;
                return result;
            }
            catch (IOException exception)
            {
                var result = new SourceRead(null, null, Dl1CharacterDependencyStatus.SourceChanged,
                    "Source could not be read: " + exception.Message);
                cache[asset.Id.StableKey] = result;
                return result;
            }
        }

        Dl1CharacterDependencyFinding ResolveReference(string request, Dl1CharacterDependencyBasis basis,
            CharacterSubsystem subsystem, string parent, bool exactIsVerified)
        {
            RetailAssetRecord? declaring = parent == root.Id.StableKey ? null : catalog.Assets.FirstOrDefault(asset => asset.Id.StableKey == parent);
            bool packedFx=declaring?.Source.Kind==RetailAssetSourceKind.RpackEmbeddedEffect;
            CharacterVirtualReferencePathResult resolved;
            try { resolved = CharacterVirtualReferencePath.Resolve(request, packedFx?null:declaring?.Id.Name); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            { return new(basis, Dl1CharacterDependencyStatus.Malformed, subsystem, request, parent, null, [], null, "Reference path is unsafe: " + error.Message); }
            string name = resolved.CanonicalName;
            var logicalNames = new List<RetailAssetLogicalId> { RetailAssetLogicalId.VirtualFile(name) };
            if (!resolved.RequiresExactLookup && resolved.IsRelative)
                logicalNames.Add(RetailAssetLogicalId.VirtualFile(Normalize(request)));
            foreach (RetailAssetLogicalId logical in logicalNames.Distinct())
            {
                IReadOnlyList<RetailAssetRecord> exact = catalog.GetCandidates(logical);
                if (exact.Count == 0) continue;
                RetailAssetRecord? selected = catalog.Resolve(logical);
                if (selected is null) break;
                bool providerConsistent = !(resolved.RequiresExactLookup && resolved.IsRelative) || declaring is null || selected.Id.ProviderId == declaring.Id.ProviderId;
                return new(basis, exactIsVerified && providerConsistent ? Dl1CharacterDependencyStatus.Verified : Dl1CharacterDependencyStatus.Candidate,
                    subsystem, request, parent, selected, exact.ToImmutableArray(), null,
                    providerConsistent ? "Exact logical identity resolved with catalog precedence; physical alternatives retained." : "Exact logical path resolves across the declaring provider; review its precedence before accepting the dependency.");
            }
            if ((basis==Dl1CharacterDependencyBasis.ConditionalRelicMesh || (basis is Dl1CharacterDependencyBasis.DeclaredResource or Dl1CharacterDependencyBasis.GenericRelicPreload) && subsystem==CharacterSubsystem.DetachedParts) &&
                !resolved.RequiresExactLookup &&
                Path.GetExtension(name).Equals(".msh", StringComparison.OrdinalIgnoreCase))
            {
                string stem = RemoveMeshExtension(Path.GetFileName(name));
                RetailAssetLogicalId compiled = RetailAssetLogicalId.Rpack(
                    ReAnimated.Codecs.Rp6l.Rp6lResourceTypes.Mesh, stem);
                IReadOnlyList<RetailAssetRecord> candidates = catalog.GetCandidates(compiled);
                RetailAssetRecord? selected = catalog.Resolve(compiled);
                if (selected is not null)
                    return new(basis, (basis is Dl1CharacterDependencyBasis.DeclaredResource or Dl1CharacterDependencyBasis.GenericRelicPreload) && exactIsVerified?Dl1CharacterDependencyStatus.Verified:Dl1CharacterDependencyStatus.Candidate,
                        subsystem,request,parent,selected,candidates.ToImmutableArray(),null,
                        basis==Dl1CharacterDependencyBasis.ConditionalRelicMesh?
                            "Exact compiled type-272 mesh stem found; conditional native relic filename selection remains unverified.":
                            "Literal detached mesh filename resolved to its exact compiled resource stem.");
            }
            if (packedFx || resolved.RequiresExactLookup)
                return new(basis, Dl1CharacterDependencyStatus.Missing, subsystem, request, parent, null, [], null, "The normalized source-relative logical path is absent; basename fallback is not permitted.");
            // A sole basename match is reviewable, never automatically verified. More than
            // one logical path must be refused even if one provider has higher priority.
            RetailAssetRecord[] basename = catalog.Assets.Where(asset =>
                asset.Id.Namespace == RetailAssetNamespace.VirtualFile &&
                Path.GetFileName(asset.Id.Name).Equals(Path.GetFileName(name), StringComparison.OrdinalIgnoreCase)).ToArray();
            string[] distinct = basename.Select(static asset => asset.Id.LogicalId.StableKey)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length > 1)
                return new(basis, Dl1CharacterDependencyStatus.Ambiguous, subsystem, request, parent,
                    null, basename.ToImmutableArray(), null, "Basename matches multiple logical resources; select one exact identity.");
            if (distinct.Length == 1)
            {
                RetailAssetRecord? selected = catalog.Resolve(basename[0].Id.LogicalId);
                return new(basis, Dl1CharacterDependencyStatus.Candidate, subsystem, request, parent,
                    selected, basename.ToImmutableArray(), null, "Only a basename match is known; association requires review.");
            }
            return new(basis, Dl1CharacterDependencyStatus.Missing, subsystem, request, parent,
                null, [], null, "Referenced resource is absent from the catalog.");
        }
    }

    private static IEnumerable<(string Name, Dl1CharacterDependencyBasis Basis, CharacterSubsystem Subsystem)> ExtractReferences(
        NativeCharacterScriptDocument syntax, string sourceName, string meshStem)
    {
        bool cloth = IsCloth(sourceName, syntax);
        foreach (NativeCharacterCall call in syntax.Calls)
        {
            string? Quoted(int argument) => call.QuotedArguments.FirstOrDefault(value => value.ArgumentIndex == argument)?.Value;
            bool classified = false;
            if (MeshDeclarations.Contains(call.Name) && Quoted(0) is { Length: > 0 } declaredMesh &&
                RemoveMeshExtension(Normalize(declaredMesh)).Equals(meshStem, StringComparison.OrdinalIgnoreCase))
                classified = true;
            if ((call.Name.Equals("!include", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("import", StringComparison.Ordinal)) && Quoted(0) is { Length: > 0 } include)
            {
                yield return (include, Dl1CharacterDependencyBasis.Include,
                    Classify(include, null, cloth));
                classified = true;
            }
            else if (call.Name.Equals("LoadBodyElements", StringComparison.OrdinalIgnoreCase) && Quoted(0) is { Length: > 0 } bel)
            {
                yield return (bel, Dl1CharacterDependencyBasis.DeclaredResource, CharacterSubsystem.Damage);
                classified = true;
            }
            else if (call.Name.Equals("BehaviorSet", StringComparison.OrdinalIgnoreCase) && Quoted(0) is { Length: > 0 } behavior)
            {
                yield return (behavior, Dl1CharacterDependencyBasis.DeclaredResource,
                    cloth ? CharacterSubsystem.Cloth : CharacterSubsystem.Ragdoll);
                classified = true;
            }
            else if (call.Name is "AddRelics" or "AddRelicsWithDestroyedChild")
            {
                classified = true;
                if (Quoted(2) is { Length: > 0 } physics)
                    yield return (physics, Dl1CharacterDependencyBasis.DeclaredResource, CharacterSubsystem.Ragdoll);
                if (Quoted(3) is { Length: > 0 } effect)
                    yield return (effect, Dl1CharacterDependencyBasis.DeclaredResource, CharacterSubsystem.Damage);
                if (Quoted(0) is { Length: > 0 } relic)
                {
                    // The exact Player consumer has conditional stem, underscore, and
                    // standalone-name branches. Their runtime selector is not a BEL field.
                    foreach (string candidate in new[] { meshStem + relic + ".msh", meshStem + "_" + relic + ".msh", relic + ".msh" }
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                        yield return (candidate, Dl1CharacterDependencyBasis.ConditionalRelicMesh, CharacterSubsystem.DetachedParts);
                }
            }
            else if (call.Name is "ParticleEmiter" or "Sequence" or "Standard" or "Light" or "CameraShake" or "VarList" or "Flare" or "Trace")
            {
                // Exact Player SequenceDef consumer dispatches these quoted effect names.
                // Only explicit .fx identities are classified; no suffixes are guessed.
                foreach(var argument in call.QuotedArguments.Where(argument=>
                    Path.GetExtension(argument.Value).Equals(".fx",StringComparison.OrdinalIgnoreCase)))
                    yield return (argument.Value,Dl1CharacterDependencyBasis.DeclaredResource,CharacterSubsystem.Damage);
                classified = call.QuotedArguments.Any(argument=>Path.GetExtension(argument.Value).Equals(".fx",StringComparison.OrdinalIgnoreCase));
            }
            else if (Path.GetExtension(sourceName).Equals(".fx",StringComparison.OrdinalIgnoreCase))
            {
                foreach(var argument in call.QuotedArguments.Where(argument=>
                    Path.GetExtension(argument.Value).ToLowerInvariant() is ".mat" or ".dds"))
                    yield return (argument.Value,Dl1CharacterDependencyBasis.SourceMention,
                        Path.GetExtension(argument.Value).Equals(".mat",StringComparison.OrdinalIgnoreCase)
                            ? CharacterSubsystem.Materials : CharacterSubsystem.Textures);
            }
            else if (call.Name == "DestroyedHeadParts")
            {
                classified = true;
                ImmutableArray<Dl1IndexedMeshName> meshes = [];
                try
                {
                    if (Quoted(0) is { Length: > 0 } template && call.Arguments.Length >= 2)
                        meshes = Dl1IndexedMeshTemplate.Expand(template, Dl1IndexedMeshTemplate.ParseCount(call.Arguments[1]));
                }
                catch (FormatException) { }
                foreach (var expandedMesh in meshes)
                    yield return (expandedMesh.Name, Dl1CharacterDependencyBasis.DeclaredResource, CharacterSubsystem.DetachedParts);
            }
            else if (call.Name == "AddMeatPart" && Quoted(0) is { Length: > 0 } mesh)
            {
                yield return (mesh, Dl1CharacterDependencyBasis.DeclaredResource, CharacterSubsystem.DetachedParts);
                classified = true;
            }
            if (!classified)
                foreach (NativeCharacterQuotedArgument argument in call.QuotedArguments)
                {
                    string extension = Path.GetExtension(Normalize(argument.Value));
                    if (SourceExtensions.Contains(extension) || extension.Equals(".fx", StringComparison.OrdinalIgnoreCase))
                        yield return (argument.Value, Dl1CharacterDependencyBasis.SourceMention,
                            Classify(argument.Value, null, cloth));
                }
        }
    }

    private static CharacterSubsystem Classify(string name, NativeCharacterScriptDocument? syntax = null, bool inheritedCloth = false) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".phx" => inheritedCloth || syntax is not null && IsCloth(name, syntax)
                ? CharacterSubsystem.Cloth : CharacterSubsystem.Ragdoll,
            ".mpcloth" => CharacterSubsystem.Cloth,
            ".bel" => CharacterSubsystem.Damage,
            ".fed" => CharacterSubsystem.FacialDefinitions,
            ".chr" => CharacterSubsystem.Variants,
            ".pre" => CharacterSubsystem.Helpers,
            ".msh" or ".msh_obj" or ".skn" => CharacterSubsystem.DetachedParts,
            _ => CharacterSubsystem.Damage,
        };

    private static bool IsCloth(string name, NativeCharacterScriptDocument syntax) =>
        Path.GetExtension(name).Equals(".mpcloth", StringComparison.OrdinalIgnoreCase) ||
        syntax.Calls.Any(call => call.Name.Equals("!include", StringComparison.OrdinalIgnoreCase) &&
            call.QuotedArguments.Any(argument => Path.GetFileName(Normalize(argument.Value))
                .Equals("MeshPartCloth.def", StringComparison.OrdinalIgnoreCase)));

    private static bool ReferencesRoot(string reference, RetailAssetLogicalId root, string meshStem)
    {
        string normalized = Normalize(reference);
        string name = RemoveMeshExtension(normalized);
        if (!SafeName(name)) return false;
        return name.Equals(meshStem, StringComparison.OrdinalIgnoreCase) &&
            (root.Namespace == RetailAssetNamespace.RpackResource
                ? name.Equals(root.Name, StringComparison.OrdinalIgnoreCase)
                : normalized.Equals(root.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static string RemoveMeshExtension(string value) =>
        Path.GetExtension(value).ToLowerInvariant() is ".msh" or ".skn" or ".msh_obj"
            ? value[..^Path.GetExtension(value).Length] : value;

    private static string Normalize(string name) => name.Trim().Replace('\\', '/');

    private static bool SafeName(string name) => name.Length is > 0 and <= 4096 &&
        !Path.IsPathRooted(name) && !name.Contains(':') && !name.Contains('\0') &&
        name.Split('/').All(static part => part is not ("" or "." or ".."));

    private static string CatalogSnapshot(IRetailAssetCatalog catalog)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var asset in catalog.Assets.OrderBy(asset => asset.Id.StableKey, StringComparer.Ordinal))
        {
            byte[] value = Encoding.UTF8.GetBytes(asset.Id.StableKey + "\n" +
                catalog.Resolve(asset.Id.LogicalId)?.Id.StableKey + "\n");
            hash.AppendData(value);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string HashIdentity(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record SourceRead(string? Text, string? Sha256, Dl1CharacterDependencyStatus Status, string Detail);
}
