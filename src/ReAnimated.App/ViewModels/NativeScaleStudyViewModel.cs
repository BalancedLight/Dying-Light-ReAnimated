using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Models;

namespace ReAnimated.App.ViewModels;

/// <summary>Source-only preparation for controlled HumanAI scale observations.</summary>
public sealed partial class NativeScaleStudyViewModel : ObservableObject, IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ReceiptJson = new() { WriteIndented = true };
    private readonly IProjectFileDialogService _dialogs;
    private string? _sourcePath, _source, _sourceHash;
    private Dl1ScaleExperimentSource? _preview;
    private long _generation;
    private bool _disposed;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _sourceLabel = "No preset source selected.";
    [ObservableProperty] private string? _selectedPreset;
    [ObservableProperty] private string _trialPrefix = "size_study";
    [ObservableProperty] private double _firstScale = .5;
    [ObservableProperty] private double _secondScale = 1;
    [ObservableProperty] private double _thirdScale = 2;
    [ObservableProperty] private string _status = "Choose a control preset. Preparing trials does not apply scale to this model or certify native behavior.";
    public ObservableCollection<string> Presets { get; } = [];
    public ObservableCollection<Dl1ScaleTrialReceipt> Trials { get; } = [];
    public ObservableCollection<Dl1PresetField> ControlFields { get; } = [];
    public IAsyncRelayCommand LoadSourceCommand { get; }
    public IAsyncRelayCommand PrepareCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public bool CanPrepare => !_disposed && !IsBusy && _source is not null && SelectedPreset is not null;
    public bool CanSave => !_disposed && !IsBusy && _preview is not null;

    public NativeScaleStudyViewModel(IProjectFileDialogService dialogs)
    {
        _dialogs = dialogs;
        LoadSourceCommand = new AsyncRelayCommand(LoadAsync, () => !_disposed && !IsBusy);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, () => CanPrepare);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        CancelCommand = new RelayCommand(() => { LoadSourceCommand.Cancel(); PrepareCommand.Cancel(); SaveCommand.Cancel(); });
    }
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        string? path = _dialogs.ShowOpenScaleStudySourceDialog();
        if (path is null) return;
        IsBusy = true;
        try
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("The preset source is too large for a bounded study.");
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            string source = StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
            var presets = await Task.Run(() => Dl1HumanAiScaleExperiments.ListPresets(source,"Character"),cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return;
            Invalidate(); _sourcePath = Path.GetFullPath(path); _source = source; _sourceHash = Hash(bytes);
            Presets.Clear(); foreach (string preset in presets) Presets.Add(preset);
            SelectedPreset = Presets.FirstOrDefault(); SourceLabel = Path.GetFileName(path);
            Status = $"{presets.Length} control presets found. Select one to prepare three fixed-size trials.";
        }
        catch (Exception error) when (StudyError(error)) { if (!_disposed) Status = "Source was not loaded: " + error.Message; }
        finally { IsBusy = false; }
    }
    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_source is not { } source || SelectedPreset is not { } preset) return;
        var trials = new[] { new Dl1ScaleTrial(TrialPrefix + "_a", FirstScale), new Dl1ScaleTrial(TrialPrefix + "_b", SecondScale), new Dl1ScaleTrial(TrialPrefix + "_c", ThirdScale) };
        long generation = ++_generation; IsBusy = true;
        try
        {
            var result = await Task.Run(() => Dl1HumanAiScaleExperiments.Build(source,"Character",preset,trials),cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation) return;
            _preview = result; Trials.Clear(); foreach (var trial in result.Trials) Trials.Add(trial);
            ControlFields.Clear(); foreach (var field in result.ControlFields) ControlFields.Add(field);
            Status = "Three source trials are ready. Original presets and other fields are retained. Save to a new file, then review and merge the trial presets before Player testing.";
        }
        catch (Exception error) when (StudyError(error)) { if (!_disposed && generation == _generation) Status = "Trials were not prepared: " + error.Message; }
        finally { IsBusy = false; }
    }
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_preview is not { } preview || _sourcePath is not { } sourcePath) return;
        string? selected = _dialogs.ShowSaveScaleStudyDialog(); if (selected is null) return;
        string path = Path.GetFullPath(selected), receiptPath = path + ".study.json";
        IsBusy = true;
        try
        {
            if (path.Equals(sourcePath,StringComparison.OrdinalIgnoreCase) || File.Exists(path) || File.Exists(receiptPath))
                throw new IOException("Choose a new file. Existing sources and study files are retained.");
            if (Hash(await File.ReadAllBytesAsync(sourcePath,cancellationToken)) != _sourceHash)
                throw new IOException("The source changed on disk. Reload it and prepare the trials again.");
            byte[] payload = StrictUtf8.GetBytes(preview.Source);
            var receipt = new { SchemaVersion = 1, SourceBytesSha256 = _sourceHash, ResultBytesSha256 = Hash(payload), preview.ControlPresetName,
                preview.ControlFields, preview.Trials, NativeAcceptance = "unverified", Deployed = false, MeshOrAnimationFilesWritten = false };
            byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt,ReceiptJson);
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                await stream.WriteAsync(payload,CancellationToken.None);
            using (var stream = new FileStream(receiptPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                await stream.WriteAsync(receiptBytes,CancellationToken.None);
            Status = "Study source and hash receipt saved. Merge only the additional trial presets into the current project; native size, animation binding, contacts and collision remain unverified.";
        }
        catch (Exception error) when (StudyError(error)) { if (!_disposed) Status = "Study was not saved completely: " + error.Message; }
        finally { IsBusy = false; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool StudyError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException;
    private void Invalidate() { _generation++; _preview = null; Trials.Clear(); ControlFields.Clear(); NotifyCommands(); }
    partial void OnSelectedPresetChanged(string? value) => Invalidate();
    partial void OnTrialPrefixChanged(string value) => Invalidate();
    partial void OnFirstScaleChanged(double value) => Invalidate();
    partial void OnSecondScaleChanged(double value) => Invalidate();
    partial void OnThirdScaleChanged(double value) => Invalidate();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanPrepare)); OnPropertyChanged(nameof(CanSave));
        LoadSourceCommand?.NotifyCanExecuteChanged(); PrepareCommand?.NotifyCanExecuteChanged(); SaveCommand?.NotifyCanExecuteChanged();
    }
    public void Dispose() { _disposed = true; CancelCommand.Execute(null); Invalidate(); }
}
