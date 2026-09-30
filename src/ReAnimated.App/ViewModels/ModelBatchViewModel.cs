using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed class ModelBatchQueueItemViewModel : ObservableObject
{
    private bool _approved, _editable = true;
    private string _state = "Queued", _message = "Review this saved package before approving it.";
    private DateTimeOffset? _updated;
    public ModelBatchQueueItemViewModel(Dl1ModelBatchItem item) { Item = item; _approved = item.Approved; }
    public Dl1ModelBatchItem Item { get; }
    public string SetupSummary => Item.Setup is { } setup
        ? $"Setup: {System.IO.Path.GetFileName(setup.Preset.Path)}; {setup.Bindings.Length} mapped nodes; file {setup.Preset.Sha256}."
        : "No setup attached; uses the package's saved choices.";
    public Guid Id => Item.Id;
    public string Name => Item.Name;
    public string Path => Item.Package.Path;
    public string Sha256 => Item.Package.Sha256;
    public bool Approved { get => _approved; set { if (_editable) SetProperty(ref _approved, value); } }
    public bool IsEditable { get => _editable; internal set => SetProperty(ref _editable, value); }
    public string StateLabel { get => _state; private set => SetProperty(ref _state, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    internal Dl1ModelBatchItem ToContract() => Item with { Approved = Approved };
    internal void Reset() { _updated = null; StateLabel = "Queued"; Message = "The next run will recheck this package revision."; }
    internal void Apply(Dl1ModelBatchItemReceipt receipt)
    {
        if (_updated is { } last && receipt.UpdatedUtc is { } next && next < last) return;
        _updated = receipt.UpdatedUtc;
        StateLabel = receipt.State switch { Dl1ModelBatchItemState.CompilerValidated => "Compiled; runtime unverified",
            Dl1ModelBatchItemState.NeedsReview => "Needs review", _ => receipt.State.ToString() };
        Message = receipt.Message + (receipt.Warnings.IsDefaultOrEmpty ? string.Empty : " " + string.Join(" ", receipt.Warnings.Take(3)));
    }
}

/// <summary>Interactive approval/queue shell over the shared durable batch runner.</summary>
public sealed partial class ModelBatchViewModel : ObservableObject, IDisposable
{
    private readonly IProjectFileDialogService _dialogs;
    private readonly Func<bool> _externalBusy;
    private readonly Func<Dl1ModelBatchRequest, CancellationToken, Task<Dl1ModelBatchResult>> _run;
    private bool _busy, _disposed, _restoring;
    private long _generation;
    private Dl1ModelBatchManifest? _manifest;
    private ImmutableArray<Dl1ModelBatchFile> _additionalInputs = [];
    private string _compiler = string.Empty, _retail = string.Empty, _work = string.Empty, _output = string.Empty;
    private string _status = "Add saved model packages, review their revisions, then approve the items to build.";
    private string _receipt = string.Empty, _manifestPath = string.Empty;
    private ModelBatchQueueItemViewModel? _selected;
    public ObservableCollection<ModelBatchQueueItemViewModel> Items { get; } = [];
    public ModelBatchQueueItemViewModel? SelectedItem { get => _selected; set { if (SetProperty(ref _selected, value)) ClearSetupDraft(); RefreshAvailability(); } }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) RefreshAvailability(); } }
    public bool CanEdit => !_disposed && !IsBusy && !_externalBusy();
    public string CompilerPath { get => _compiler; set => Edit(ref _compiler, value); }
    public string RetailData0Path { get => _retail; set => Edit(ref _retail, value); }
    public string CompilerWorkingDirectory { get => _work; set => Edit(ref _work, value); }
    public string OutputDirectory
    {
        get => _output;
        set
        {
            if (!CanEdit && !_restoring) return;
            if (SetProperty(ref _output, value ?? string.Empty) && !_restoring)
            {
                // Output location is not a manifest input. Keep its identity so a
                // queue moved elsewhere can resume the original durable job.
                ReceiptPath = string.Empty;
                foreach (var row in Items) row.Reset();
                RefreshAvailability();
            }
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ReceiptPath { get => _receipt; private set => SetProperty(ref _receipt, value); }
    public string ManifestPath { get => _manifestPath; private set => SetProperty(ref _manifestPath, value); }
    public IAsyncRelayCommand AddPackageCommand { get; }
    public IRelayCommand RemoveSelectedCommand { get; }
    public IAsyncRelayCommand RefreshSelectedCommand { get; }
    public IAsyncRelayCommand LoadQueueCommand { get; }
    public IAsyncRelayCommand SaveQueueCommand { get; }
    public IRelayCommand SelectCompilerCommand { get; }
    public IRelayCommand SelectRetailDataCommand { get; }
    public IRelayCommand SelectOutputCommand { get; }
    public IRelayCommand SelectCompilerWorkCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand InspectReceiptCommand { get; }
    public IRelayCommand NewBatchCommand { get; }

    public ModelBatchViewModel(IProjectFileDialogService dialogs, Func<bool>? externalBusy = null)
        : this(dialogs, externalBusy, Dl1ModelBatchRunner.RunAsync) { }
    internal ModelBatchViewModel(IProjectFileDialogService dialogs, Func<bool>? externalBusy,
        Func<Dl1ModelBatchRequest, CancellationToken, Task<Dl1ModelBatchResult>> run)
    {
        ArgumentNullException.ThrowIfNull(dialogs); ArgumentNullException.ThrowIfNull(run);
        _dialogs = dialogs; _externalBusy = externalBusy ?? (() => false); _run = run;
        AddPackageCommand = new AsyncRelayCommand(async token =>
        { if (_dialogs.ShowOpenCustomModelPackageDialog(SelectedItem?.Path) is { } path) await AddPackageFromPathAsync(path, token); }, () => CanEdit && Items.Count < 64);
        RemoveSelectedCommand = new RelayCommand(() => { if (SelectedItem is not { } row) return; row.PropertyChanged -= ItemChanged; Items.Remove(row); SelectedItem = Items.FirstOrDefault(); Invalidate(); }, () => CanEdit && SelectedItem is not null);
        RefreshSelectedCommand = new AsyncRelayCommand(token => Execute(async t =>
        {
            if (SelectedItem is not { } old) return;
            var fresh = await ReadPackage(old.Path, old.Id, t);
            int index = Items.IndexOf(old); old.PropertyChanged -= ItemChanged;
            var row = NewRow(fresh); Items[index] = row; SelectedItem = row; Invalidate();
            Status = "Package revision refreshed. Review and approve it again before building.";
        }, token), () => CanEdit && SelectedItem is not null);
        LoadQueueCommand = new AsyncRelayCommand(async token =>
        { if (_dialogs.ShowOpenModelBatchManifestDialog(ManifestPath) is { } path) await LoadQueueFromPathAsync(path, token); }, () => CanEdit);
        SaveQueueCommand = new AsyncRelayCommand(token => Execute(async t => { await SaveManifest(t); Status = "Queue saved. Approvals apply to the recorded package and setup revisions, including destination mappings."; }, token), () => CanEdit && Items.Count > 0 && OutputDirectory.Length > 0 && CompilerPath.Length > 0);
        SelectCompilerCommand = new RelayCommand(() => { if (_dialogs.ShowOpenDl1DeveloperToolsCompilerDialog(CompilerPath) is { } path) CompilerPath = path; }, () => CanEdit);
        SelectRetailDataCommand = new RelayCommand(() => { if (_dialogs.ShowOpenModelBatchData0Dialog(RetailData0Path) is { } path) RetailData0Path = path; }, () => CanEdit);
        SelectOutputCommand = new RelayCommand(() => { if (_dialogs.ShowSelectCustomModelOutputDirectory(OutputDirectory) is { } path) OutputDirectory = path; }, () => CanEdit);
        SelectCompilerWorkCommand = new RelayCommand(() => { if (_dialogs.ShowSelectCustomModelOutputDirectory(CompilerWorkingDirectory) is { } path) CompilerWorkingDirectory = path; }, () => CanEdit);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanEdit && Items.Any(i => i.Approved) && OutputDirectory.Length > 0 && CompilerPath.Length > 0);
        CancelCommand = new RelayCommand(() => { AddPackageCommand.Cancel(); RefreshSelectedCommand.Cancel(); LoadQueueCommand.Cancel(); SaveQueueCommand.Cancel(); RunCommand.Cancel(); InspectReceiptCommand?.Cancel(); CancelSetupQueue(); }, () => IsBusy);
        InspectReceiptCommand = new AsyncRelayCommand(token => Execute(async t =>
        {
            if (_manifest is null) throw new InvalidOperationException("Open or save a queue first.");
            var directory = Dl1ModelBatchRunner.ResolveRunDirectory(OutputDirectory, _manifest.Id);
            var receipt = await Task.Run(() => Dl1ModelBatchRunner.ReadReceipt(directory), t);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(_manifest, Dl1ModelBatchJson.Options)));
            if (receipt.Id != _manifest.Id || receipt.ManifestSha256 != expectedHash ||
                !receipt.Items.Select(i => i.Id).SequenceEqual(_manifest.Items.Select(i => i.Id)))
                throw new InvalidDataException("This receipt belongs to a different queue revision.");
            Apply(receipt); ReceiptPath = Path.Combine(directory, "receipt.json");
            Status = "Recorded results loaded. Run/resume rechecks current inputs and outputs; inspection alone does not.";
        }, token), () => CanEdit && _manifest is not null && OutputDirectory.Length > 0);
        InitializeSetupQueue();
        NewBatchCommand = new RelayCommand(() => { Invalidate(); Status = "New batch identity prepared. Existing job folders are retained; the current queue and approvals remain visible."; }, () => CanEdit);
    }

    public Task AddPackageFromPathAsync(string path, CancellationToken token = default) => Execute(async t =>
    {
        if (Items.Count >= 64) throw new InvalidOperationException("A queue is limited to 64 packages.");
        string full = Path.GetFullPath(path);
        if (Items.Any(i => i.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("This package is already queued. Refresh its revision instead.");
        var row = NewRow(await ReadPackage(full, Guid.NewGuid(), t)); Items.Add(row); SelectedItem = row; Invalidate();
        Status = "Saved package added. Review the file and revision, then approve it for this batch.";
    }, token);

    public Task LoadQueueFromPathAsync(string path, CancellationToken token = default) => Execute(async t =>
    {
        var manifest = await Task.Run(() => Dl1ModelBatchJson.LoadManifest(path), t);
        Dl1ModelBatchRunner.ValidateManifest(manifest);
        _restoring = true;
        try
        {
            foreach (var row in Items) row.PropertyChanged -= ItemChanged;
            Items.Clear(); foreach (var item in manifest.Items) Items.Add(NewRow(item)); SelectedItem = Items.FirstOrDefault();
            CompilerPath = manifest.Compiler.Path; RetailData0Path = manifest.RetailData0?.Path ?? string.Empty;
            CompilerWorkingDirectory = manifest.CompilerWorkingDirectory ?? string.Empty;
            OutputDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            _additionalInputs = manifest.AdditionalToolInputs; _manifest = manifest; ManifestPath = Path.GetFullPath(path); ReceiptPath = string.Empty;
            _generation++;
        }
        finally { _restoring = false; }
        Status = "Queue loaded. Choose its original output parent to resume if this manifest was moved. Run rechecks all recorded hashes.";
    }, token);

    private async Task RunAsync(CancellationToken token) => await Execute(async t =>
    {
        var manifest = await SaveManifest(t); long generation = ++_generation;
        var progress = new Progress<Dl1ModelBatchItemReceipt>(item =>
        { if (!_disposed && generation == _generation) Items.FirstOrDefault(i => i.Id == item.Id)?.Apply(item); });
        Status = "Building approved package snapshots. Other items continue if one fails.";
        var result = await Task.Run(() => _run(new() { Manifest = manifest, OutputDirectory = OutputDirectory, Progress = progress }, t), t);
        if (_disposed || generation != _generation) return;
        _generation++; // Ignore already queued intermediate progress after the final receipt.
        Apply(result.Receipt); ReceiptPath = result.ReceiptPath;
        int complete = result.Receipt.Items.Count(i => i.State == Dl1ModelBatchItemState.CompilerValidated);
        Status = result.Receipt.Interrupted ? $"Cancelled. {complete} compiled result(s) retained; resume uses the saved queue." :
            $"{complete}/{Items.Count} package(s) compiler-validated. Review failed or changed items below. Runtime binding and gameplay remain unverified.";
    }, token);

    private async Task<Dl1ModelBatchManifest> SaveManifest(CancellationToken token)
    {
        var manifest = _manifest;
        if (manifest is null)
        {
            var compiler = await FileIdentity(CompilerPath, token);
            var retail = string.IsNullOrWhiteSpace(RetailData0Path) ? null : await FileIdentity(RetailData0Path, token);
            manifest = new() { Id = Guid.NewGuid(), Compiler = compiler, RetailData0 = retail,
                CompilerWorkingDirectory = string.IsNullOrWhiteSpace(CompilerWorkingDirectory) ? null : Path.GetFullPath(CompilerWorkingDirectory),
                AdditionalToolInputs = _additionalInputs, Items = Items.Select(i => i.ToContract()).ToImmutableArray() };
            Dl1ModelBatchRunner.ValidateManifest(manifest);
        }
        string root = Path.GetFullPath(OutputDirectory); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "model-batch-" + manifest.Id.ToString("N") + ".json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Dl1ModelBatchJson.Options);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("The reviewed queue exceeds the 1 MiB manifest limit. Split it into smaller batches.");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The existing queue file exceeds the manifest size limit.");
            if (!(await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("The saved queue file differs. Start a new batch identity before replacing it.");
        }
        else
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(bytes, token); stream.Flush(flushToDisk: true);
        }
        _manifest = manifest; ManifestPath = path; return manifest;
    }

    private static async Task<Dl1ModelBatchItem> ReadPackage(string path, Guid id, CancellationToken token)
    {
        if (new FileInfo(path).Length > CustomModelPackageSerializer.MaximumPackageBytes) throw new InvalidDataException("The selected package exceeds the package size limit.");
        var identity = await FileIdentity(path, token);
        var package = await Task.Run(() => CustomModelPackageSerializer.Load(identity.Path), token);
        if (identity != await FileIdentity(path, token)) throw new InvalidDataException("Package changed during inspection. Add or refresh it again.");
        return new() { Id = id, Name = package.Document.Name, Package = identity };
    }
    private static async Task<Dl1ModelBatchFile> FileIdentity(string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(full, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)));
    }
    private ModelBatchQueueItemViewModel NewRow(Dl1ModelBatchItem item)
    { var row = new ModelBatchQueueItemViewModel(item); row.PropertyChanged += ItemChanged; return row; }
    private void ItemChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ModelBatchQueueItemViewModel.Approved) && !_restoring) Invalidate(); }
    private void Apply(Dl1ModelBatchReceipt receipt)
    { foreach (var item in receipt.Items) Items.FirstOrDefault(i => i.Id == item.Id)?.Apply(item); }
    private void Edit(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (!CanEdit && !_restoring) return;
        if (SetProperty(ref field, value ?? string.Empty, name) && !_restoring) Invalidate();
    }
    private void Invalidate()
    {
        _manifest = null; ManifestPath = string.Empty; ReceiptPath = string.Empty; _generation++;
        foreach (var row in Items) row.Reset();
        RefreshAvailability();
    }
    private async Task Execute(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        if (!CanEdit) return;
        IsBusy = true;
        try { await operation(token); }
        catch (OperationCanceledException) { if (!_disposed) Status = "Cancelled. Existing files and completed batch results are retained."; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException or FormatException or NotSupportedException)
        { if (!_disposed) Status = "Batch operation did not complete: " + error.Message; }
        finally { _generation++; IsBusy = false; }
    }
    public void RefreshAvailability()
    {
        NotifySetupAvailability();
        OnPropertyChanged(nameof(CanEdit));
        foreach (var row in Items) row.IsEditable = CanEdit;
        AddPackageCommand?.NotifyCanExecuteChanged(); RemoveSelectedCommand?.NotifyCanExecuteChanged(); RefreshSelectedCommand?.NotifyCanExecuteChanged();
        LoadQueueCommand?.NotifyCanExecuteChanged(); SaveQueueCommand?.NotifyCanExecuteChanged(); RunCommand?.NotifyCanExecuteChanged();
        CancelCommand?.NotifyCanExecuteChanged(); InspectReceiptCommand?.NotifyCanExecuteChanged(); NewBatchCommand?.NotifyCanExecuteChanged();
        SelectCompilerCommand?.NotifyCanExecuteChanged(); SelectRetailDataCommand?.NotifyCanExecuteChanged(); SelectOutputCommand?.NotifyCanExecuteChanged(); SelectCompilerWorkCommand?.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++;
        ClearSetupDraft();
        CancelCommand.Execute(null);
        foreach (var row in Items) row.PropertyChanged -= ItemChanged;
        RefreshAvailability();
    }
}
