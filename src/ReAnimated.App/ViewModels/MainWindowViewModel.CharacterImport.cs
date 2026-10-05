using System.IO;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Core.Domain;

namespace ReAnimated.App.ViewModels;

public sealed record CharacterImportContextChoice(string Label,Dl1CharacterHostContext Context);

public sealed partial class MainWindowViewModel
{
    private AsyncRelayCommand? _importCompleteCharacterCommand;
    private Dl1CharacterHostContext _characterImportHostContext;
    public IReadOnlyList<CharacterImportContextChoice> CharacterImportHostContexts {get;}=[new("Actor type not specified",Dl1CharacterHostContext.Unspecified),new("Standard human NPC",Dl1CharacterHostContext.StandardHumanAiVis)];
    public Dl1CharacterHostContext CharacterImportHostContext {get=>_characterImportHostContext;set=>SetProperty(ref _characterImportHostContext,value);}
    public AsyncRelayCommand ImportCompleteCharacterCommand => _importCompleteCharacterCommand ??= new(
        ImportCompleteCharacterAsync, () => !IsBusy && AssetBrowser.SelectedAsset is { Kind: AssetKind.Mesh, RetailAsset: not null });

    private async Task<ReAnimated.Codecs.Fbx.FbxModelAuthoringImportResult> ResolveCharacterCompanionsAsync(CustomModelPackage package, IEnumerable<string> names)
    {
        var inventory = package.Document.CharacterResources ?? throw new InvalidOperationException("Load a stock character package first.");
        var catalog = _assetWorkspace.Catalog ?? throw new InvalidOperationException("Load the game catalog first.");
        var source = inventory.Resources.Single(r=>r.Id==inventory.RootResourceId);
        var asset = catalog.Assets.SingleOrDefault(a=>a.Id.LogicalId.StableKey==source.Id && a.Id.ProviderId==source.ProviderIdentity && a.Id.SourceFingerprint==source.SourceFingerprint)
            ?? throw new InvalidOperationException("The original catalog source is missing or stale. Keep the package and refresh its exact source identity.");
        var roots=names.Select(name=>catalog.Assets.Where(a=>a.Id.LogicalId.StableKey==name || a.Id.Name==name).ToArray())
            .Select(matches=>matches.Length==1?matches[0].Id.LogicalId:throw new InvalidDataException("Choose an exact, unique catalog resource identity."))
            .ToImmutableArray();
        var decoded=await _assetWorkspace.DecodeMeshAsync(asset,_lifetimeSource.Token);
        var options=await _assetWorkspace.CreateCharacterImportOptionsAsync(decoded.Source,roots,_lifetimeSource.Token);
        var priorScan=CharacterDependencyScanEvidence.Read(package);
        var discovery=await Dl1CharacterDependencyDiscovery.DiscoverAsync(decoded.Source,asset,catalog,
            new()
            {
                HostContext=priorScan?.Scan.HostContext??CharacterImportHostContext,
                MaximumScannedScripts=65536,
                MaximumTotalSourceBytes=256L*1024*1024,
            },_lifetimeSource.Token);
        var discoveredRoots=discovery.Roots.Where(finding=>finding.Selected is not null &&
                finding.Status is Dl1CharacterDependencyStatus.Verified or Dl1CharacterDependencyStatus.Candidate)
            .Select(finding=>finding.Selected!.Id.LogicalId).Distinct().ToImmutableArray();
        options=options with {CompanionRoots=options.CompanionRoots.AddRange(discoveredRoots).Distinct().ToImmutableArray()};
        var imported=await Dl1CharacterImporter.ImportAsync(decoded.Source,asset,catalog,options,_lifetimeSource.Token);
        return CharacterDependencyScanEvidence.Apply(imported,discovery,roots.Select(root=>root.StableKey));
    }

    private async Task ImportCompleteCharacterAsync()
    {
        if (AssetBrowser.SelectedAsset is not { Kind: AssetKind.Mesh, RetailAsset: not null } selected) return;
        var catalog = _assetWorkspace.Catalog ?? throw new InvalidOperationException("Load the game catalog first.");
        string? path = _fileDialogs.ShowSaveCustomModelPackageDialog(selected.Name, ProjectPath);
        if (path is null) return;
        CancelAutomaticAssetPreview();
        var job = BeginExclusiveAssetDecode($"Import character: {selected.Name}", "Resolving original character data");
        IsBusy = true;
        try
        {
            var session = await DecodeRetailModelAsync(selected, job);
            var options=await _assetWorkspace.CreateCharacterImportOptionsAsync(session.Payload.Source,cancellationToken:job.CancellationToken);
            var discovery=await Dl1CharacterDependencyDiscovery.DiscoverAsync(session.Payload.Source,selected.RetailAsset,catalog,
                new() {HostContext=CharacterImportHostContext},job.CancellationToken);
            var discoveredRoots=discovery.Roots.Where(r=>r.Selected is not null && r.Status is Dl1CharacterDependencyStatus.Verified or Dl1CharacterDependencyStatus.Candidate)
                .Select(r=>r.Selected!.Id.LogicalId).Distinct().ToImmutableArray();
            options=options with {CompanionRoots=options.CompanionRoots.AddRange(discoveredRoots).Distinct().ToImmutableArray()};
            var imported = await Dl1CharacterImporter.ImportAsync(session.Payload.Source, selected.RetailAsset, catalog,options,
                cancellationToken: job.CancellationToken);
            imported=CharacterDependencyScanEvidence.Apply(imported,discovery);
            job.CancellationToken.ThrowIfCancellationRequested();
            CustomModelPackageSerializer.SaveAtomic(imported.Package, path);
            IsBusy = false;
            OpenCustomModelAuthoring();
            await Models.OpenPackagePathAsync(path, job.CancellationToken);
            // Conditional candidates remain inventoried for review; do not silently select them.
            Models.CharacterCompanionRoots=string.Join(Environment.NewLine,discovery.VerifiedCompanionRoots.Select(r=>r.StableKey));
            StatusText = "Original character imported; review completeness and attach unresolved companion roots.";
            job.Complete("Imported");
        }
        catch (OperationCanceledException) { job.Complete("Canceled"); }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        { StatusText = "Complete character import failed: " + e.Message; job.Complete("Failed"); }
        finally { IsBusy = false; ImportCompleteCharacterCommand.NotifyCanExecuteChanged(); }
    }
}
