using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelBatchViewModel
{
    private FbxModelAuthoringImportResult? _setupSource;
    private RigSetupPreset? _setupDraft;
    private Dl1ModelBatchFile? _setupFile;
    private RigSetupTransferPreview? _setupPreview;
    private Guid? _setupItemId;
    private long _setupGeneration;
    private bool _setupReviewed;
    private string _setupStatus = "Select a package to review an optional reusable setup before compiling it.";
    public ObservableCollection<RigSetupMappingRow> SetupMappings { get; } = [];
    public ObservableCollection<RigEntityBinding> SetupDestinations { get; } = [];
    public string SetupStatus { get => _setupStatus; private set => SetProperty(ref _setupStatus, value); }
    public bool SetupReviewed { get => _setupReviewed; set { if (CanEdit && SetProperty(ref _setupReviewed, value)) NotifySetupAvailability(); } }
    public string SetupIdentity => _setupDraft is { } draft && _setupFile is { } file
        ? $"{draft.Name}\nFile revision: {file.Sha256}\nProfile: {draft.Profile.Identity.Id} / {draft.Profile.Identity.Version}" : "No setup draft loaded for this item.";
    public string SetupReview => _setupPreview is { } preview ? string.Join("\n", preview.Review.Diagnostics.Select(d => $"{d.Status}: {d.Message} {d.CorrectiveOperation}"))
        : "Preview the mappings to inspect requirements on this package revision.";
    public bool CanPreviewSetup => CanEdit && _setupSource is not null && _setupDraft is not null && SelectedItem?.Id == _setupItemId && SetupMappings.All(m => m.Destination is not null);
    public bool CanAttachSetup => CanEdit && SetupReviewed && _setupPreview is not null && SelectedItem?.Id == _setupItemId;
    public IAsyncRelayCommand ChooseSetupCommand { get; private set; } = null!;
    public IAsyncRelayCommand ReviewLinkedSetupCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewSetupCommand { get; private set; } = null!;
    public IAsyncRelayCommand AttachSetupCommand { get; private set; } = null!;
    public IRelayCommand RemoveSetupCommand { get; private set; } = null!;

    private void InitializeSetupQueue()
    {
        ChooseSetupCommand = new AsyncRelayCommand(async token =>
        { if (_dialogs.ShowOpenRigSetupDialog() is { } path) await LoadSetupForSelectedAsync(path, token); }, () => CanEdit && SelectedItem is not null);
        ReviewLinkedSetupCommand = new AsyncRelayCommand(async token =>
        { if (SelectedItem?.Item.Setup is { } setup) await LoadSetupForSelectedAsync(setup.Preset.Path, token); }, () => CanEdit && SelectedItem?.Item.Setup is not null);
        PreviewSetupCommand = new AsyncRelayCommand(token => Execute(async t =>
        {
            if (_setupSource is not { } source || _setupDraft is not { } draft) return;
            long generation = ++_setupGeneration;
            var mapping = SetupMappings.ToDictionary(m => m.Node.Key, m => m.Destination?.EntityId ?? Guid.Empty, StringComparer.Ordinal);
            var preview = await Task.Run(() => FbxRigSetupTransfer.Preview(source, draft, mapping, t), t);
            if (_disposed || generation != _setupGeneration) return;
            _setupPreview = preview; ResetSetupApproval();
            SetupStatus = "Review the destination requirements, then attach this exact setup and mapping. The package file itself is not changed.";
            NotifySetupAvailability();
        }, token), () => CanPreviewSetup);
        AttachSetupCommand = new AsyncRelayCommand(token => Execute(async t =>
        {
            if (!_setupReviewed || _setupPreview is not { } preview || _setupFile is not { } file || SelectedItem is not { } row || row.Id != _setupItemId) return;
            long generation = _setupGeneration;
            if (!SameRevision(await FileIdentity(row.Path, t), row.Item.Package) || !SameRevision(await FileIdentity(file.Path, t), file))
            {
                _setupPreview = null; ResetSetupApproval(); NotifySetupAvailability();
                throw new InvalidDataException("The package or setup changed after preview. Refresh the package and review the setup again.");
            }
            t.ThrowIfCancellationRequested();
            if (_disposed || generation != _setupGeneration || !ReferenceEquals(row, SelectedItem)) return;
            if (_setupSource is null || !FbxRigSetupTransfer.TryApply(_setupSource, preview, out _)) throw new InvalidOperationException("The setup preview is stale.");
            var binding = new Dl1ModelBatchSetup { Preset = file, Bindings = SetupMappings.OrderBy(m => m.Node.Key, StringComparer.Ordinal)
                .Select(m => new Dl1ModelBatchSetupBinding(m.Node.Key, m.Destination!.EntityId)).ToImmutableArray() };
            ReplaceSelectedSetup(row, binding);
            Status = "Reviewed setup linked. Approve this combined package/setup revision in the queue before running.";
        }, token), () => CanAttachSetup);
        RemoveSetupCommand = new RelayCommand(() =>
        {
            if (SelectedItem is { } row) ReplaceSelectedSetup(row, null);
            Status = "Setup removed. Review and approve the original package revision before running.";
        }, () => CanEdit && SelectedItem?.Item.Setup is not null);
    }

    public Task LoadSetupForSelectedAsync(string path, CancellationToken token = default) => Execute(async t =>
    {
        if (SelectedItem is not { } row) throw new InvalidOperationException("Select a queued package first.");
        ClearSetupDraft(); long generation = _setupGeneration;
        if (new FileInfo(path).Length > RigSetupPresetSerializer.MaximumBytes) throw new InvalidDataException("Rig setup exceeds 8 MiB.");
        var file = await FileIdentity(path, t);
        if (!SameRevision(await FileIdentity(row.Path, t), row.Item.Package)) throw new InvalidDataException("The queued package changed. Refresh and review its revision first.");
        var source = await Task.Run(() => FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(row.Path), t), t);
        var bytes = await File.ReadAllBytesAsync(file.Path, t);
        var draft = await Task.Run(() => RigSetupPresetSerializer.Deserialize(bytes), t);
        if (!SameRevision(await FileIdentity(file.Path, t), file) || !SameRevision(await FileIdentity(row.Path, t), row.Item.Package))
            throw new InvalidDataException("A setup input changed during inspection. Load it again.");
        if (_disposed || generation != _setupGeneration || !ReferenceEquals(SelectedItem, row)) return;
        var matches = FbxRigSetupTransfer.Propose(source, draft);
        _setupSource = source; _setupDraft = draft; _setupFile = file; _setupItemId = row.Id;
        foreach (var entity in source.Package.Document.RiggingSession!.Recipe.Entities.Where(e => e.OwnerAssetId == source.Package.Document.ModelId)) SetupDestinations.Add(entity);
        var prior = row.Item.Setup is { } saved && SameRevision(saved.Preset, file) ? saved : null;
        foreach (var match in matches)
        {
            var destination = prior?.Bindings.FirstOrDefault(b => b.Key == match.Key)?.DestinationEntityId ?? match.DestinationEntityId;
            var choice = new RigSetupMappingRow(draft.Nodes.Single(n => n.Key == match.Key), match)
            { Destination = SetupDestinations.FirstOrDefault(e => e.EntityId == destination) };
            choice.PropertyChanged += SetupMappingChanged; SetupMappings.Add(choice);
        }
        SetupStatus = "Review every mapping for this saved package. Unresolved nodes and native requirements must remain visible.";
        NotifySetupAvailability();
    }, token);

    private void ReplaceSelectedSetup(ModelBatchQueueItemViewModel row, Dl1ModelBatchSetup? setup)
    {
        int index = Items.IndexOf(row); row.PropertyChanged -= ItemChanged;
        var replacement = NewRow(row.ToContract() with { Setup = setup, Approved = false });
        Items[index] = replacement; SelectedItem = replacement; Invalidate();
    }
    private static bool SameRevision(Dl1ModelBatchFile left, Dl1ModelBatchFile right) =>
        left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase) && left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase);
    private void SetupMappingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RigSetupMappingRow.Destination)) return;
        _setupGeneration++; _setupPreview = null; ResetSetupApproval(); NotifySetupAvailability();
    }
    private void ResetSetupApproval() { _setupReviewed = false; OnPropertyChanged(nameof(SetupReviewed)); }
    private void ClearSetupDraft()
    {
        _setupGeneration++; _setupSource = null; _setupDraft = null; _setupFile = null; _setupPreview = null; _setupItemId = null; ResetSetupApproval();
        foreach (var row in SetupMappings) row.PropertyChanged -= SetupMappingChanged;
        SetupMappings.Clear(); SetupDestinations.Clear();
        SetupStatus = "Select and review a setup for the current package, or keep its original saved choices.";
        NotifySetupAvailability();
    }
    private void NotifySetupAvailability()
    {
        OnPropertyChanged(nameof(SetupIdentity)); OnPropertyChanged(nameof(SetupReview)); OnPropertyChanged(nameof(CanPreviewSetup)); OnPropertyChanged(nameof(CanAttachSetup));
        ChooseSetupCommand?.NotifyCanExecuteChanged(); ReviewLinkedSetupCommand?.NotifyCanExecuteChanged(); PreviewSetupCommand?.NotifyCanExecuteChanged(); AttachSetupCommand?.NotifyCanExecuteChanged(); RemoveSetupCommand?.NotifyCanExecuteChanged();
    }
    private void CancelSetupQueue() { ChooseSetupCommand?.Cancel(); ReviewLinkedSetupCommand?.Cancel(); PreviewSetupCommand?.Cancel(); AttachSetupCommand?.Cancel(); }
}
