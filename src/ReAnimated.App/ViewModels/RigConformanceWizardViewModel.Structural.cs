using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    private StructuralHelperPreview? _structuralPreview;
    private FbxCompilerRetentionBatchPreview? _structuralBatchPreview;
    private long _structuralGeneration;
    private bool _restoringStructural;
    [ObservableProperty] private StructuralNodeReview? _structuralNode;
    [ObservableProperty] private bool _structuralLockName;
    [ObservableProperty] private bool _structuralLockParent;
    [ObservableProperty] private bool _structuralLockPosition;
    [ObservableProperty] private bool _structuralLockOrientation;
    [ObservableProperty] private bool _structuralLockExtents;
    [ObservableProperty] private bool _structuralLockChannels;
    [ObservableProperty] private bool _structuralReviewed;
    [ObservableProperty] private bool _structuralPreviewEnabled;
    [ObservableProperty] private string _structuralRetentionBatchNames = "";
    [ObservableProperty] private string _structuralStatus = "Scan the current rig to inspect actual weight use and saved helper decisions.";
    public ContactVector StructuralOffset { get; } = new();
    public ContactVector StructuralRotation { get; } = new();
    public ObservableCollection<StructuralNodeReview> StructuralNodes { get; } = [];
    public IAsyncRelayCommand ScanStructuralCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewStructuralFrameCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewStructuralProtectionCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewStructuralRetentionCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewStructuralRetentionBatchCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewStructuralRetentionRemovalCommand { get; private set; } = null!;
    public IRelayCommand CancelStructuralCommand { get; private set; } = null!;
    public IRelayCommand ApplyStructuralCommand { get; private set; } = null!;
    public IRelayCommand OpenStructuralRestCommand { get; private set; } = null!;
    public IRelayCommand SelectStructuralChannelsCommand { get; private set; } = null!;
    public StructuralHelperPreview? StructuralPreview => _structuralPreview;
    public event EventHandler? StructuralPreviewChanged;
    public event EventHandler<BodyModelEventArgs>? StructuralApplyRequested;
    public bool CanScanStructural => !IsBusy && HasStudioSession && _model?.Rig is not null;
    public bool CanPreviewStructuralFrame => CanScanStructural && StructuralNode is { CanEditHelper: true } && StructuralOffset.Value.IsFinite && StructuralRotation.Value.IsFinite;
    public bool CanPreviewStructuralProtection => CanScanStructural && StructuralNode is { CanProtect: true };
    public bool CanPreviewStructuralRetention => CanScanStructural && StructuralNode is { CanAddRetentionHelper: true };
    public bool CanPreviewStructuralRetentionBatch => CanScanStructural && StructuralNodes.Count > 0 &&
        !string.IsNullOrWhiteSpace(StructuralRetentionBatchNames);
    public bool CanPreviewStructuralRetentionRemoval => CanScanStructural && StructuralNode is { IsRetentionHelper: true };
    public bool CanApplyStructural => !IsBusy && StructuralReviewed && _structuralPreview is { HasChanges: true };
    public string StructuralUsage => StructuralNode is { } row
        ? $"{row.Kind}; {row.WeightedCorners:N0} weighted draw corners; branch {(row.WeightedBranch ? "has weights or weighted metadata" : "is unweighted")}; {row.AuthoredTrackCount} authored transform tracks."
        : "No node selected.";

    private void InitializeStructural()
    {
        ScanStructuralCommand = new AsyncRelayCommand(ScanStructuralAsync, () => CanScanStructural);
        PreviewStructuralFrameCommand = new AsyncRelayCommand(t => PreviewStructuralAsync(false, t), () => CanPreviewStructuralFrame);
        PreviewStructuralProtectionCommand = new AsyncRelayCommand(t => PreviewStructuralAsync(true, t), () => CanPreviewStructuralProtection);
        PreviewStructuralRetentionCommand = new AsyncRelayCommand(t => PreviewStructuralAsync(false, t, retention: true), () => CanPreviewStructuralRetention);
        PreviewStructuralRetentionBatchCommand = new AsyncRelayCommand(PreviewStructuralRetentionBatchAsync,
            () => CanPreviewStructuralRetentionBatch);
        PreviewStructuralRetentionRemovalCommand = new AsyncRelayCommand(t => PreviewStructuralAsync(false, t, removal: true), () => CanPreviewStructuralRetentionRemoval);
        CancelStructuralCommand = new RelayCommand(InvalidateStructuralDraft);
        ApplyStructuralCommand = new RelayCommand(ApplyStructural, () => CanApplyStructural);
        OpenStructuralRestCommand = new RelayCommand(() =>
        {
            Guid? id = StructuralNode?.EntityId;
            InvalidateStructuralDraft();
            RouteStudioAction(RigStudioStage.Fit);
            RestPoseNode = RestPoseNodes.FirstOrDefault(n => n.EntityId == id);
            RestSurfaceMode = RestSurfaceChoices.Single(c => c.Mode == RigRestSurfaceMode.PreserveSurface);
            RestDescendantMode = RestDescendantChoices.Single(c => c.Mode == RigRestDescendantMode.KeepGlobal);
            StructuralStatus = "The selected joint is ready in Fit > Joint rest-pose editing. Surface and descendant frames will be preserved.";
        }, () => !IsBusy && StructuralNode is { CanEditRest: true });
        SelectStructuralChannelsCommand = new RelayCommand(() =>
        {
            SelectedChannelPolicy = ChannelPolicies.FirstOrDefault(p => p.EntityId == StructuralNode?.EntityId);
            StructuralStatus = "Selected this node in Channel ownership and LOD below. Existing channel protection still applies.";
        }, () => !IsBusy && StructuralNode is not null);
        StructuralOffset.PropertyChanged += (_, _) => InvalidateStructuralDraft();
        StructuralRotation.PropertyChanged += (_, _) => InvalidateStructuralDraft();
    }

    private void RestoreStructural()
    {
        InvalidateStructuralDraft();
        StructuralNodes.Clear();
        StructuralRetentionBatchNames = "";
        StructuralNode = null;
        StructuralStatus = "Scan the current rig to inspect actual weight use and saved helper decisions.";
        NotifyStructural();
    }

    private bool SynchronizeStructuralSnapshot()
    {
        try { StudioWorkspace?.CaptureProjectSession(); return true; }
        catch (Exception error) when (RestPoseError(error))
        {
            InvalidateStructuralDraft();
            StructuralStatus = "Correct the model settings before previewing structural changes: " + error.Message;
            return false;
        }
    }

    private async Task ScanStructuralAsync(CancellationToken token)
    {
        if (!SynchronizeStructuralSnapshot()) return;
        if (_model is not { } model) return;
        _structuralPreview = null; _structuralBatchPreview = null; StructuralReviewed = false; StructuralPreviewEnabled = false;
        long generation = ++_structuralGeneration;
        Guid? selected = StructuralNode?.EntityId;
        IsBusy = true;
        try
        {
            var rows = await Task.Run(() => FbxStructuralHelperAuthoring.Inspect(model, token), token);
            if (generation != _structuralGeneration || !ReferenceEquals(model, _model)) return;
            StructuralNodes.Clear();
            foreach (var row in rows) StructuralNodes.Add(row);
            StructuralNode = StructuralNodes.FirstOrDefault(n => n.EntityId == selected) ?? StructuralNodes.FirstOrDefault();
            StructuralStatus = $"Inspected {rows.Length} nodes. Weight counts use each surface's palette; source names alone do not establish native drivers or weight eligibility.";
        }
        catch (OperationCanceledException) { if (generation == _structuralGeneration) StructuralStatus = "Structural scan cancelled."; }
        catch (Exception error) when (RestPoseError(error)) { if (generation == _structuralGeneration) StructuralStatus = "Structural scan rejected: " + error.Message; }
        finally { IsBusy = false; NotifyStructural(); }
    }

    partial void OnStructuralNodeChanged(StructuralNodeReview? value)
    {
        _restoringStructural = true;
        try
        {
            var fields = value?.Locks ?? RigHelperEditFields.None;
            StructuralLockName = fields.HasFlag(RigHelperEditFields.Name);
            StructuralLockParent = fields.HasFlag(RigHelperEditFields.Parent);
            StructuralLockPosition = fields.HasFlag(RigHelperEditFields.Position);
            StructuralLockOrientation = fields.HasFlag(RigHelperEditFields.Orientation);
            StructuralLockExtents = fields.HasFlag(RigHelperEditFields.Extents);
            StructuralLockChannels = fields.HasFlag(RigHelperEditFields.Channels);
            StructuralOffset.Set(Vector3D.Zero); StructuralRotation.Set(Vector3D.Zero);
        }
        finally { _restoringStructural = false; }
        InvalidateStructuralDraft();
        OnPropertyChanged(nameof(StructuralUsage));
    }
    partial void OnStructuralLockNameChanged(bool value) => InvalidateStructuralDraft();
    partial void OnStructuralLockParentChanged(bool value) => InvalidateStructuralDraft();
    partial void OnStructuralLockPositionChanged(bool value) => InvalidateStructuralDraft();
    partial void OnStructuralLockOrientationChanged(bool value) => InvalidateStructuralDraft();
    partial void OnStructuralLockExtentsChanged(bool value) => InvalidateStructuralDraft();
    partial void OnStructuralLockChannelsChanged(bool value) => InvalidateStructuralDraft();
    partial void OnStructuralReviewedChanged(bool value) => NotifyStructural();
    partial void OnStructuralRetentionBatchNamesChanged(string value) => InvalidateStructuralDraft();
    partial void OnStructuralPreviewEnabledChanged(bool value) => StructuralPreviewChanged?.Invoke(this, EventArgs.Empty);

    private RigHelperEditFields StructuralProtection() =>
        (StructuralLockName ? RigHelperEditFields.Name : 0) | (StructuralLockParent ? RigHelperEditFields.Parent : 0) |
        (StructuralLockPosition ? RigHelperEditFields.Position : 0) | (StructuralLockOrientation ? RigHelperEditFields.Orientation : 0) |
        (StructuralLockExtents ? RigHelperEditFields.Extents : 0) | (StructuralLockChannels ? RigHelperEditFields.Channels : 0);

    private async Task PreviewStructuralAsync(bool protection, CancellationToken token, bool retention = false, bool removal = false)
    {
        if (!SynchronizeStructuralSnapshot()) return;
        if (_model is null || StructuralNode is not { } node) return;
        RouteStudioAction(RigStudioStage.HelpersAndHooks);
        var model = _model!;
        long generation = ++_structuralGeneration;
        var fields = StructuralProtection();
        var position = StructuralOffset.Value; var degrees = StructuralRotation.Value;
        IsBusy = true;
        try
        {
            var blockers = removal ? StudioWorkspace?.GetRetentionRemovalBlockers(node.EntityId) ?? [] : [];
            var rotation = removal || retention || protection ? QuaternionD.Identity : QuaternionD.FromAxisAngle(Vector3D.UnitX, degrees.X * Math.PI / 180) *
                QuaternionD.FromAxisAngle(Vector3D.UnitY, degrees.Y * Math.PI / 180) *
                QuaternionD.FromAxisAngle(Vector3D.UnitZ, degrees.Z * Math.PI / 180);
            var preview = await Task.Run(() => removal ? FbxCompilerRetentionAuthoring.PreviewRemoval(model, node.EntityId, blockers, token) : retention ? FbxCompilerRetentionAuthoring.Preview(model, node.EntityId, token) : protection
                ? FbxStructuralHelperAuthoring.PreviewProtection(model, node.EntityId, fields, token)
                : FbxStructuralHelperAuthoring.PreviewOffset(model, node.EntityId, new(position, rotation, Vector3D.One), token), token);
            if (generation != _structuralGeneration || !ReferenceEquals(model, _model)) return;
            _structuralBatchPreview = null; _structuralPreview = preview; StructuralReviewed = false; StructuralPreviewEnabled = true;
            StructuralStatus = preview.Summary;
        }
        catch (OperationCanceledException) { if (generation == _structuralGeneration) StructuralStatus = "Structural preview cancelled."; }
        catch (Exception error) when (RestPoseError(error))
        { if (generation == _structuralGeneration) { _structuralPreview = null; StructuralStatus = "Structural preview rejected: " + error.Message; } }
        finally { IsBusy = false; NotifyStructural(); StructuralPreviewChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async Task PreviewStructuralRetentionBatchAsync(CancellationToken token)
    {
        if (!SynchronizeStructuralSnapshot() || _model is not { } model || !CanPreviewStructuralRetentionBatch)
            return;
        string[] names = (StructuralRetentionBatchNames ?? "")
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static name => name.Trim().Trim('"', '\'', '`'))
            .Where(static name => name.Length > 0)
            .ToArray();
        if (names.Length == 0 || names.Length > FbxCompilerRetentionBatchAuthoring.MaximumBatchSize)
        {
            StructuralStatus = $"Enter 1–{FbxCompilerRetentionBatchAuthoring.MaximumBatchSize} exact node names from compiled readback, separated by lines or commas.";
            return;
        }
        var selected = new List<Guid>(names.Length);
        foreach (string name in names)
        {
            StructuralNodeReview[] matches = StructuralNodes.Where(row =>
                string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                StructuralStatus = $"Retention batch name '{name}' has {matches.Length} matching scanned nodes; each name must identify exactly one current node.";
                return;
            }
            selected.Add(matches[0].EntityId);
        }
        RouteStudioAction(RigStudioStage.HelpersAndHooks);
        long generation = ++_structuralGeneration;
        IsBusy = true;
        try
        {
            FbxCompilerRetentionBatchPreview batch = await Task.Run(() =>
                FbxCompilerRetentionBatchAuthoring.Preview(model, selected, token), token);
            if (generation != _structuralGeneration || !ReferenceEquals(model, _model)) return;
            _structuralBatchPreview = batch;
            _structuralPreview = batch.ToStructuralHelperPreview();
            StructuralReviewed = false;
            StructuralPreviewEnabled = true;
            StructuralStatus = batch.Summary + " The selected names were explicit; no other eligible branches were chosen.";
        }
        catch (OperationCanceledException)
        {
            if (generation == _structuralGeneration) StructuralStatus = "Retention batch preview cancelled.";
        }
        catch (Exception error) when (RestPoseError(error))
        {
            if (generation == _structuralGeneration)
            {
                _structuralBatchPreview = null;
                _structuralPreview = null;
                StructuralStatus = "Retention batch preview rejected: " + error.Message;
            }
        }
        finally { IsBusy = false; NotifyStructural(); StructuralPreviewChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void InvalidateStructuralDraft()
    {
        if (_restoringStructural) return;
        ScanStructuralCommand?.Cancel(); PreviewStructuralFrameCommand?.Cancel(); PreviewStructuralProtectionCommand?.Cancel(); PreviewStructuralRetentionCommand?.Cancel(); PreviewStructuralRetentionBatchCommand?.Cancel(); PreviewStructuralRetentionRemovalCommand?.Cancel();
        _structuralGeneration++; _structuralPreview = null; _structuralBatchPreview = null;
        StructuralReviewed = false; StructuralPreviewEnabled = false;
        NotifyStructural(); StructuralPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyStructural()
    {
        if (!CanApplyStructural || _model is not { } model || _structuralPreview is not { } preview) return;
        if (preview.RemovedHelperId is { } removed && StudioWorkspace?.GetRetentionRemovalBlockers(removed) is { Count: > 0 } blockers)
        {
            StructuralReviewed = false;
            StructuralStatus = "Project references changed. Resolve them before removal: " + string.Join("; ", blockers.Take(6));
            return;
        }
        FbxModelAuthoringImportResult result;
        bool applied = _structuralBatchPreview is { } batch
            ? FbxCompilerRetentionBatchAuthoring.TryApply(model, batch, out result)
            : FbxStructuralHelperAuthoring.TryApply(model, preview, out result);
        if (!applied)
        { InvalidateStructuralDraft(); StructuralStatus = "The source changed. Scan and preview it again."; return; }
        if (!RequestBodyChange(StructuralApplyRequested, new(model, result, "Applied reviewed structural helper changes."))) return;
        InvalidateStructuralDraft();
        StructuralStatus = "Applied the reviewed edit. Scan again to inspect its saved state; native behavior still requires validation.";
    }

    private void RefreshStructuralMetadata(FbxModelAuthoringImportResult current)
    {
        if (_structuralBatchPreview is not null)
        {
            InvalidateStructuralDraft();
            StructuralStatus = "Model settings changed. Preview the exact retention batch again.";
            return;
        }
        if (_structuralPreview is { } preview)
        {
            _structuralPreview = FbxStructuralHelperAuthoring.RefreshMetadata(preview, current);
            if (_structuralPreview is null)
            {
                InvalidateStructuralDraft();
                StructuralStatus = "Model settings changed. Scan and preview the current rig again.";
            }
        }
        NotifyStructural();
    }

    private void NotifyStructural()
    {
        OnPropertyChanged(nameof(StructuralPreview)); OnPropertyChanged(nameof(CanScanStructural));
        OnPropertyChanged(nameof(CanPreviewStructuralFrame)); OnPropertyChanged(nameof(CanPreviewStructuralProtection));
        OnPropertyChanged(nameof(CanApplyStructural)); OnPropertyChanged(nameof(CanPreviewStructuralRetention)); OnPropertyChanged(nameof(CanPreviewStructuralRetentionRemoval));
        OnPropertyChanged(nameof(CanPreviewStructuralRetentionBatch));
        ScanStructuralCommand?.NotifyCanExecuteChanged(); PreviewStructuralFrameCommand?.NotifyCanExecuteChanged();
        PreviewStructuralRetentionCommand?.NotifyCanExecuteChanged(); PreviewStructuralRetentionRemovalCommand?.NotifyCanExecuteChanged(); PreviewStructuralProtectionCommand?.NotifyCanExecuteChanged(); ApplyStructuralCommand?.NotifyCanExecuteChanged();
        PreviewStructuralRetentionBatchCommand?.NotifyCanExecuteChanged();
        OpenStructuralRestCommand?.NotifyCanExecuteChanged(); SelectStructuralChannelsCommand?.NotifyCanExecuteChanged();
    }
}
