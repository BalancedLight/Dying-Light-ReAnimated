using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private bool _weightBrushEnabled;
    [ObservableProperty] private double _weightBrushRadius = .025;
    [ObservableProperty] private bool _mirrorWeightEdits;
    [ObservableProperty] private SkinWeightInfluenceChoice? _weightMirrorInfluence;
    [ObservableProperty] private double _weightMirrorTolerance = .005;
    [ObservableProperty] private double _weightMirrorOriginX;
    [ObservableProperty] private double _weightMirrorOriginY;
    [ObservableProperty] private double _weightMirrorOriginZ;
    [ObservableProperty] private double _weightMirrorNormalX = 1;
    [ObservableProperty] private double _weightMirrorNormalY;
    [ObservableProperty] private double _weightMirrorNormalZ;
    private SkinWeightBrushSurface? _weightBrushSurface;
    private WeightBrushStroke? _weightBrushStroke;
    private bool _restoringWeightMirror;
    private CancellationTokenSource? _brushCommitCancellation;
    public IRenderBrushTarget WeightBrushTarget { get; private set; } = null!;
    public Vector3D? WeightBrushCenter { get; private set; }
    public Task WeightBrushCommitTask { get; private set; } = Task.CompletedTask;
    public IAsyncRelayCommand SaveWeightMirroringCommand { get; private set; } = null!;
    public event EventHandler? WeightBrushOverlayChanged;

    private sealed class WeightBrushStroke(FbxModelAuthoringImportResult model, SkinWeightAuthoringSnapshot snapshot,
        SkinWeightBrushSurface surface, string component, Guid influence, double radius, double strength, double target)
    {
        public FbxModelAuthoringImportResult Model = model;
        public SkinWeightAuthoringSnapshot Snapshot = snapshot;
        public SkinWeightBrushSurface Surface { get; } = surface;
        public string Component { get; } = component;
        public Guid Influence { get; } = influence;
        public double Radius { get; } = radius;
        public double Strength { get; } = strength;
        public double Target { get; } = target;
        public Dictionary<int, double> Selection { get; } = [];
        public int Samples;
        public RenderBrushPointerRay LastRay;
        public Vector3D? LastHit;
    }

    private sealed class WeightBrushInput(RigConformanceWizardViewModel owner) : IRenderBrushTarget
    {
        public bool IsBrushEnabled => owner.WeightBrushEnabled && owner.CanCorrectWeights && owner._weightBrushSurface is not null && !owner.HasUnsavedWeightMirroring;
        public bool TryBeginBrush(RenderBrushPointerRay ray) => owner.BeginWeightBrush(ray);
        public bool UpdateBrush(RenderBrushPointerRay ray) => owner.UpdateWeightBrush(ray);
        public void CompleteBrush(bool commit) => owner.CompleteWeightBrush(commit);
    }

    private void InitializeWeightBrushing()
    {
        WeightBrushTarget = new WeightBrushInput(this);
        SaveWeightMirroringCommand = new AsyncRelayCommand(SaveWeightMirroringAsync, () => CanCorrectWeights && (WeightMirrorInfluence is not null || !MirrorWeightEdits));
    }

    public bool HasUnsavedWeightMirroring
    {
        get
        {
            if (_weightSnapshot is not { } snapshot || SelectedWeightInfluence is not { } selected) return false;
            var session = snapshot.Session;
            var pair = session.WeightMirrorPairs.FirstOrDefault(p => p.EntityId == selected.EntityId || p.CounterpartEntityId == selected.EntityId);
            Guid? counterpart = pair is null ? null : pair.EntityId == selected.EntityId ? pair.CounterpartEntityId : pair.EntityId;
            return MirrorWeightEdits != session.MirrorWeightEdits || WeightMirrorTolerance != session.WeightMirrorTolerance ||
                new Vector3D(WeightMirrorOriginX, WeightMirrorOriginY, WeightMirrorOriginZ) != session.SymmetryOrigin ||
                new Vector3D(WeightMirrorNormalX, WeightMirrorNormalY, WeightMirrorNormalZ) != session.SymmetryNormal ||
                WeightMirrorInfluence?.EntityId != counterpart;
        }
    }

    private void RestoreWeightMirroring()
    {
        if (_weightSnapshot is not { } snapshot || SelectedWeightInfluence is not { } selected) return;
        _restoringWeightMirror = true;
        var session = snapshot.Session;
        var pair = session.WeightMirrorPairs.FirstOrDefault(p => p.EntityId == selected.EntityId || p.CounterpartEntityId == selected.EntityId);
        Guid? counterpart = pair is null ? null : pair.EntityId == selected.EntityId ? pair.CounterpartEntityId : pair.EntityId;
        WeightMirrorInfluence = WeightInfluences.FirstOrDefault(b => b.EntityId == counterpart);
        MirrorWeightEdits = session.MirrorWeightEdits;
        WeightMirrorTolerance = session.WeightMirrorTolerance;
        WeightMirrorOriginX = session.SymmetryOrigin.X; WeightMirrorOriginY = session.SymmetryOrigin.Y; WeightMirrorOriginZ = session.SymmetryOrigin.Z;
        WeightMirrorNormalX = session.SymmetryNormal.X; WeightMirrorNormalY = session.SymmetryNormal.Y; WeightMirrorNormalZ = session.SymmetryNormal.Z;
        _restoringWeightMirror = false;
        OnPropertyChanged(nameof(HasUnsavedWeightMirroring));
    }

    private async Task SaveWeightMirroringAsync(CancellationToken token)
    {
        if (_model is not { } model || _weightSnapshot is not { } snapshot || SelectedWeightInfluence is not { } source) return;
        try
        {
            if (!FbxSkinWeightAuthoring.TrySetMirroring(model, snapshot, source.EntityId, WeightMirrorInfluence?.EntityId, MirrorWeightEdits,
                new(WeightMirrorOriginX, WeightMirrorOriginY, WeightMirrorOriginZ), new(WeightMirrorNormalX, WeightMirrorNormalY, WeightMirrorNormalZ),
                WeightMirrorTolerance, out var changed)) return;
            if (!ReferenceEquals(changed, model)) BodyModelApplyRequested?.Invoke(this, new(model, changed, "Saved the weight mirror plane and explicit influence pairing."));
            await InspectWeightsAsync(token);
        }
        catch (Exception error) when (IsWeightError(error)) { WeightEditingStatus = "Mirror settings were not saved: " + error.Message; }
    }

    private bool BeginWeightBrush(RenderBrushPointerRay ray)
    {
        if (!WeightBrushTarget.IsBrushEnabled || _model is not { } model || _weightSnapshot is not { } snapshot ||
            _weightBrushSurface is not { } surface || SelectedWeightComponent is not { } component || SelectedWeightInfluence is not { } influence) return false;
        CancelWeightBrush();
        try
        {
            if (!float.IsFinite((float)WeightBrushRadius)) throw new ArgumentException("Brush radius exceeds the viewport coordinate range.");
            var hit = surface.Sample(ToModel(ray.Origin), ToModel(ray.Direction), component.Id, WeightBrushRadius, WeightEditStrength);
            if (hit is null) return false;
            _weightBrushStroke = new(model, snapshot, surface, component.Id, influence.EntityId, WeightBrushRadius, WeightEditStrength, WeightTargetValue);
            _weightBrushStroke.LastRay = ray; _weightBrushStroke.LastHit = hit.Position;
            AccumulateBrushHit(hit);
            return true;
        }
        catch (Exception error) when (IsWeightError(error)) { CancelWeightBrush(); WeightEditingStatus = "Brush could not start: " + error.Message; return false; }
    }

    private bool UpdateWeightBrush(RenderBrushPointerRay ray)
    {
        if (_weightBrushStroke is not { } stroke || !ReferenceEquals(stroke.Model, _model) || !ReferenceEquals(stroke.Snapshot, _weightSnapshot)) return false;
        try
        {
            if (++stroke.Samples > 4096) throw new InvalidOperationException("This stroke exceeded its sample budget. Use shorter strokes.");
            var hit = stroke.Surface.Sample(ToModel(ray.Origin), ToModel(ray.Direction), stroke.Component, stroke.Radius, stroke.Strength);
            if (hit is not null && stroke.LastHit is { } previous)
            {
                double requested = Math.Ceiling((hit.Position - previous).Length / (stroke.Radius * .4));
                if (!double.IsFinite(requested) || requested > 64) throw new InvalidOperationException("Pointer movement exceeded the continuous-stroke sampling budget; use shorter movements or a larger radius.");
                for (int sample = 1; sample < requested; sample++)
                {
                    float fraction = (float)(sample / requested);
                    var origin = System.Numerics.Vector3.Lerp(stroke.LastRay.Origin, ray.Origin, fraction);
                    var direction = System.Numerics.Vector3.Lerp(stroke.LastRay.Direction, ray.Direction, fraction);
                    var between = stroke.Surface.Sample(ToModel(origin), ToModel(direction), stroke.Component, stroke.Radius, stroke.Strength);
                    if (between is not null) AccumulateBrushHit(between);
                    if (++stroke.Samples > 4096) throw new InvalidOperationException("This stroke exceeded its sample budget. Use shorter strokes.");
                }
            }
            if (hit is not null) AccumulateBrushHit(hit);
            stroke.LastRay = ray; stroke.LastHit = hit?.Position;
            return true;
        }
        catch (Exception error) when (IsWeightError(error)) { WeightEditingStatus = "Stroke cancelled: " + error.Message; return false; }
    }

    private void AccumulateBrushHit(SkinWeightBrushHit hit)
    {
        var stroke = _weightBrushStroke!;
        foreach (var point in hit.Selection)
        {
            if (stroke.Selection.Count >= 20_000 && !stroke.Selection.ContainsKey(point.PointIndex)) throw new InvalidOperationException("This stroke exceeded its point budget. Use shorter strokes.");
            stroke.Selection[point.PointIndex] = Math.Max(stroke.Selection.GetValueOrDefault(point.PointIndex), point.Strength);
        }
        WeightBrushCenter = hit.Position;
        WeightEditingStatus = $"Stroke: {stroke.Selection.Count} source points. Release to apply; Escape cancels.";
        WeightBrushOverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteWeightBrush(bool commit)
    {
        var stroke = _weightBrushStroke;
        bool hadOverlay = stroke is not null || WeightBrushCenter is not null;
        _weightBrushStroke = null; WeightBrushCenter = null;
        if (hadOverlay) WeightBrushOverlayChanged?.Invoke(this, EventArgs.Empty);
        if (commit && stroke is not null && stroke.Selection.Count > 0)
        { WeightBrushCommitTask = CommitWeightBrushAsync(stroke); OnPropertyChanged(nameof(WeightBrushCommitTask)); }
    }
    public void CancelWeightBrush() => CompleteWeightBrush(commit: false);

    private void RefreshWeightBrushMetadata(FbxModelAuthoringImportResult current, SkinWeightAuthoringSnapshot snapshot)
    { if (_weightBrushStroke is { } stroke) { stroke.Model = current; stroke.Snapshot = snapshot; } }

    private async Task CommitWeightBrushAsync(WeightBrushStroke stroke)
    {
        if (!ReferenceEquals(_model, stroke.Model) || !ReferenceEquals(_weightSnapshot, stroke.Snapshot)) return;
        long generation = ++_weightJobGeneration;
        using var cancellation = new CancellationTokenSource();
        _brushCommitCancellation = cancellation;
        var token = cancellation.Token;
        IsBusy = true; NotifyStateChanged();
        bool applied = false;
        try
        {
            var result = await Task.Run(() => {
                var selection = stroke.Selection.OrderBy(static p => p.Key).Select(static p => new SkinWeightCorrectionSelection(p.Key, p.Value)).ToArray();
                var preview = FbxSkinWeightAuthoring.Preview(stroke.Snapshot, selection, new() { TargetInfluence = stroke.Influence, TargetWeight = stroke.Target }, token);
                if (!preview.Correction.CanApply) throw new InvalidOperationException(string.Join(" ", preview.Correction.Diagnostics.Take(3).Select(static d => d.Message)));
                return FbxSkinWeightAuthoring.TryApply(stroke.Model, preview, out var changed, token) ? changed : null;
            }, token);
            if (result is null || token.IsCancellationRequested || generation != _weightJobGeneration || !ReferenceEquals(_model, stroke.Model)) return;
            if (!ReferenceEquals(result, stroke.Model)) BodyModelApplyRequested?.Invoke(this, new(stroke.Model, result, "Applied one source-point brush stroke."));
            applied = true;
        }
        catch (OperationCanceledException) { WeightEditingStatus = "Stroke calculation cancelled before commit."; }
        catch (Exception error) when (IsWeightError(error)) { WeightEditingStatus = "Stroke was not applied: " + error.Message; }
        finally { if (ReferenceEquals(_brushCommitCancellation, cancellation)) _brushCommitCancellation = null; IsBusy = false; NotifyStateChanged(); NotifyWeightEditing(); }
        if (applied) await InspectWeightsAsync(CancellationToken.None);
    }

    private static Vector3D ToModel(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
    partial void OnWeightBrushEnabledChanged(bool value) { CancelWeightBrush(); if (value) { StressPreviewEnabled = false; ShowWeightHeatmap = true; } WeightDisplayChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnWeightBrushRadiusChanged(double value) => CancelWeightBrush();
    private void WeightMirrorChanged()
    {
        if (_restoringWeightMirror) return;
        InvalidateWeightPreview(); OnPropertyChanged(nameof(HasUnsavedWeightMirroring));
        WeightEditingStatus = "Mirror choices changed. Save the plane and pairing before a correction or brush stroke.";
    }
    partial void OnMirrorWeightEditsChanged(bool value) => WeightMirrorChanged();
    partial void OnWeightMirrorInfluenceChanged(SkinWeightInfluenceChoice? value) => WeightMirrorChanged();
    partial void OnWeightMirrorToleranceChanged(double value) => WeightMirrorChanged();
    partial void OnWeightMirrorOriginXChanged(double value) => WeightMirrorChanged();
    partial void OnWeightMirrorOriginYChanged(double value) => WeightMirrorChanged();
    partial void OnWeightMirrorOriginZChanged(double value) => WeightMirrorChanged();
    partial void OnWeightMirrorNormalXChanged(double value) => WeightMirrorChanged();
    partial void OnWeightMirrorNormalYChanged(double value) => WeightMirrorChanged();
    partial void OnWeightMirrorNormalZChanged(double value) => WeightMirrorChanged();
}
