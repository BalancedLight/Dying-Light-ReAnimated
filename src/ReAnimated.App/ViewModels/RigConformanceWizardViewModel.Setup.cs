using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed class RigSetupMappingRow : ObservableObject
{
    private RigEntityBinding? _destination;
    public RigSetupMappingRow(RigSetupNode node, RigSetupMatch match) { Node = node; Reason = match.Reason; }
    public RigSetupNode Node { get; }
    public string Name => Node.NativeName;
    public string Details => $"{Node.Kind}; {(Node.Roles.IsEmpty ? "no role assignment" : "roles: " + string.Join(", ", Node.Roles))}. {Reason}" +
        (Node.RequiresFrameReview ? " Destination frame and bounds need review; donor coordinates will not be copied." : string.Empty);
    public string Reason { get; }
    public RigEntityBinding? Destination { get => _destination; set => SetProperty(ref _destination, value); }
}

public sealed partial class RigConformanceWizardViewModel
{
    private RigSetupPreset? _setupDraft;
    private RigSetupTransferPreview? _setupPreview;
    private long _setupGeneration;
    private Func<string?>? _openSetup, _saveSetup;
    [ObservableProperty] private string _setupStatus = "Save reusable profile and channel choices, then map them onto another model for review.";
    [ObservableProperty] private bool _setupReviewed;
    public ObservableCollection<RigSetupMappingRow> SetupMappings { get; } = [];
    public ObservableCollection<RigEntityBinding> SetupDestinations { get; } = [];
    public IAsyncRelayCommand LoadSetupCommand { get; private set; } = null!;
    public IAsyncRelayCommand SaveSetupCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewSetupCommand { get; private set; } = null!;
    public IRelayCommand ApplySetupCommand { get; private set; } = null!;
    public IRelayCommand CancelSetupCommand { get; private set; } = null!;
    public string SetupIdentity => _setupDraft is { } draft ? $"{draft.Name}\nProfile: {draft.Profile.Identity.Id} / {draft.Profile.Identity.Version}\nRevision: {draft.ContentSha256}" : "No reusable setup loaded.";
    public bool CanPreviewSetup => !IsBusy && HasStudioSession && _setupDraft is not null && SetupMappings.All(r => r.Destination is not null);
    public bool CanApplySetup => !IsBusy && SetupReviewed && _setupPreview is not null;
    public string SetupReview => _setupPreview is { } preview ? string.Join("\n", preview.Review.Diagnostics.Select(d => $"{d.Status}: {d.Message} {d.CorrectiveOperation}")) : "Preview the mapping to inspect the destination's profile requirements.";
    internal void SetSetupPickers(Func<string?> open, Func<string?> save) { _openSetup = open; _saveSetup = save; }

    private void InitializeSetupTransfer()
    {
        LoadSetupCommand = new AsyncRelayCommand(LoadSetupAsync, () => !IsBusy && HasStudioSession);
        SaveSetupCommand = new AsyncRelayCommand(SaveSetupAsync, () => !IsBusy && _model?.Package.Document.RiggingSession?.Recipe.ProfileSnapshot is not null);
        PreviewSetupCommand = new AsyncRelayCommand(PreviewSetupAsync, () => CanPreviewSetup);
        ApplySetupCommand = new RelayCommand(ApplySetup, () => CanApplySetup);
        CancelSetupCommand = new RelayCommand(InvalidateSetupPreview);
    }

    internal void SetSetupDraft(RigSetupPreset draft)
    {
        RigSetupPresetSerializer.Verify(draft);
        if (_model is not { } model) throw new InvalidOperationException("Open a destination model first.");
        InvalidateSetupPreview(); _setupDraft = draft;
        foreach (var row in SetupMappings) row.PropertyChanged -= OnSetupMappingChanged;
        SetupMappings.Clear(); SetupDestinations.Clear();
        foreach (var entity in model.Package.Document.RiggingSession!.Recipe.Entities.Where(e => e.OwnerAssetId == model.Package.Document.ModelId)) SetupDestinations.Add(entity);
        foreach (var match in FbxRigSetupTransfer.Propose(model, draft))
        {
            var row = new RigSetupMappingRow(draft.Nodes.Single(n => n.Key == match.Key), match)
            { Destination = SetupDestinations.FirstOrDefault(e => e.EntityId == match.DestinationEntityId) };
            row.PropertyChanged += OnSetupMappingChanged; SetupMappings.Add(row);
        }
        SetupStatus = "Review every destination mapping. Profile and channel choices transfer; destination anatomy, calibrated helpers, source clips and evidence history stay with this model.";
        NotifySetupTransfer();
    }

    private async Task LoadSetupAsync(CancellationToken token)
    {
        string? path = _openSetup?.Invoke(); if (path is null) return;
        var source = _model; long generation = ++_setupGeneration; IsBusy = true;
        try
        {
            if (new FileInfo(path).Length > RigSetupPresetSerializer.MaximumBytes) throw new InvalidDataException("Rig setup exceeds 8 MiB.");
            var bytes = await File.ReadAllBytesAsync(path, token);
            var draft = await Task.Run(() => RigSetupPresetSerializer.Deserialize(bytes), token);
            if (generation != _setupGeneration || !ReferenceEquals(source, _model)) return;
            token.ThrowIfCancellationRequested(); SetSetupDraft(draft);
        }
        catch (OperationCanceledException) { if (generation == _setupGeneration) SetupStatus = "Setup loading cancelled."; }
        catch (Exception error) when (ProfileReviewError(error)) { if (generation == _setupGeneration) SetupStatus = "Setup was not loaded: " + error.Message; }
        finally { IsBusy = false; NotifySetupTransfer(); }
    }

    private async Task SaveSetupAsync(CancellationToken token)
    {
        if (_model is not { } source || _saveSetup?.Invoke() is not { } path) return;
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                var preset = RigSetupPresetSerializer.Capture(source.Package.Document, source.Package.Document.Name);
                byte[] bytes = RigSetupPresetSerializer.Serialize(preset); token.ThrowIfCancellationRequested();
                AtomicFileWriter.WriteAllText(path, Encoding.UTF8.GetString(bytes));
            }, token);
            SetupStatus = "Reusable setup saved. Fitted coordinates, physical indices, model IDs and runtime acceptance were not exported.";
        }
        catch (OperationCanceledException) { SetupStatus = "Setup saving cancelled."; }
        catch (Exception error) when (ProfileReviewError(error)) { SetupStatus = "Setup was not saved: " + error.Message; }
        finally { IsBusy = false; NotifySetupTransfer(); }
    }

    private async Task PreviewSetupAsync(CancellationToken token)
    {
        if (!CanPreviewSetup || _model is not { } source || _setupDraft is not { } draft) return;
        var map = SetupMappings.ToDictionary(r => r.Node.Key, r => r.Destination!.EntityId, StringComparer.Ordinal);
        long generation = ++_setupGeneration; IsBusy = true;
        try
        {
            var preview = await Task.Run(() => FbxRigSetupTransfer.Preview(source, draft, map, token), token);
            if (generation != _setupGeneration || !ReferenceEquals(source, _model)) return;
            _setupPreview = preview; SetupReviewed = false;
            SetupStatus = "Review the destination report, then apply as one undoable edit. Missing roles and native evidence remain unresolved; saving a draft does not certify them.";
        }
        catch (OperationCanceledException) { if (generation == _setupGeneration) SetupStatus = "Setup preview cancelled."; }
        catch (Exception error) when (ProfileReviewError(error)) { if (generation == _setupGeneration) SetupStatus = "Setup preview was not created: " + error.Message; }
        finally { IsBusy = false; NotifySetupTransfer(); }
    }

    private void ApplySetup()
    {
        if (!CanApplySetup || _model is not { } source || _setupPreview is not { } preview) return;
        if (!FbxRigSetupTransfer.TryApply(source, preview, out var candidate))
        { InvalidateSetupPreview(); SetupStatus = "The destination changed. Preview its current mapping again."; return; }
        if (!RequestBodyChange(CapabilityProfileApplyRequested, new(source, candidate, "Applied reviewed reusable rig setup."))) return;
        InvalidateSetupPreview(); SetupStatus = "Profile and channel choices applied. Review destination helper calibration and native capabilities separately.";
    }

    private void ResetSetupTransfer()
    {
        InvalidateSetupPreview(); _setupDraft = null;
        foreach (var row in SetupMappings) row.PropertyChanged -= OnSetupMappingChanged;
        SetupMappings.Clear(); SetupDestinations.Clear(); NotifySetupTransfer();
    }
    private void RefreshSetupMetadata(FbxModelAuthoringImportResult current)
    {
        if (_setupPreview is { } preview)
        {
            _setupPreview = FbxRigSetupTransfer.RefreshMetadata(preview, current);
            if (_setupPreview is null) InvalidateSetupPreview();
        }
        NotifySetupTransfer();
    }
    private void OnSetupMappingChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(RigSetupMappingRow.Destination)) InvalidateSetupPreview(); }
    private void InvalidateSetupPreview()
    {
        _setupGeneration++; LoadSetupCommand?.Cancel(); PreviewSetupCommand?.Cancel(); _setupPreview = null; SetupReviewed = false; NotifySetupTransfer();
    }
    partial void OnSetupReviewedChanged(bool value) => NotifySetupTransfer();
    private void NotifySetupTransfer()
    {
        OnPropertyChanged(nameof(SetupIdentity)); OnPropertyChanged(nameof(CanPreviewSetup)); OnPropertyChanged(nameof(CanApplySetup)); OnPropertyChanged(nameof(SetupReview));
        LoadSetupCommand?.NotifyCanExecuteChanged(); SaveSetupCommand?.NotifyCanExecuteChanged(); PreviewSetupCommand?.NotifyCanExecuteChanged(); ApplySetupCommand?.NotifyCanExecuteChanged();
    }
}
