using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.App.ViewModels;

public sealed record BodyBindingModeChoice(RigComponentBindingMode Mode, string Label);

public sealed partial class BodyComponentChoice : ObservableObject
{
    public BodyComponentChoice(RigGeometryComponent source)
    { Source = source; _included = source.Included; _useForAnatomy = source.UseForAnatomy; _bindingMode = source.BindingMode; _rigidBoneRoleId = source.RigidBoneRoleId; _kind = source.Kind; }
    public RigGeometryComponent Source { get; }
    public string DisplayName => Source.DisplayName;
    [ObservableProperty] private bool _included;
    [ObservableProperty] private bool _useForAnatomy;
    [ObservableProperty] private RigComponentBindingMode _bindingMode;
    [ObservableProperty] private string? _rigidBoneRoleId;
    [ObservableProperty] private RigGeometryComponentKind _kind;
    public RigGeometryComponent ToContract() => Source with { Included = Included, UseForAnatomy = Included && UseForAnatomy, BindingMode = BindingMode, RigidBoneRoleId = RigidBoneRoleId, Kind = Kind };
}

public sealed class BodyComponentsEventArgs(FbxModelAuthoringImportResult model, RiggingSession session, ImmutableArray<RigGeometryComponent> components) : EventArgs
{
    public FbxModelAuthoringImportResult Model { get; } = model;
    public RiggingSession Session { get; } = session;
    public ImmutableArray<RigGeometryComponent> Components { get; } = components;
}

public sealed class BodyModelEventArgs(FbxModelAuthoringImportResult source, FbxModelAuthoringImportResult result, string status) : EventArgs
{
    public FbxModelAuthoringImportResult Source { get; } = source;
    public FbxModelAuthoringImportResult Result { get; } = result;
    public string Status { get; } = status;
}

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private string _bodyAuthoringStatus = "Build a draft skeleton after saving and placing the body guides.";
    [ObservableProperty] private double _bindingFalloffPower = 2;
    private long _bodyAuthoringGeneration;
    private bool _restoringBodyComponents;
    public ObservableCollection<BodyComponentChoice> BodyComponents { get; } = [];
    public IReadOnlyList<RigGeometryComponentKind> BodyComponentKinds { get; } = Enum.GetValues<RigGeometryComponentKind>();
    public IReadOnlyList<BodyBindingModeChoice> BodyBindingModes { get; } = [
        new(RigComponentBindingMode.KeepSource, "Keep current weights"), new(RigComponentBindingMode.Automatic, "Automatic weights"), new(RigComponentBindingMode.Rigid, "Rigid assignment"),
    ];
    public IReadOnlyList<string> BodyBindingRoles { get; } = ["body.pelvis", "body.spine.0", "body.spine.1", "body.spine.2", "body.neck.0", "body.head",
        "arm.left.upper", "arm.left.lower", "hand.left", "arm.right.upper", "arm.right.lower", "hand.right", "leg.left.upper", "leg.left.lower", "foot.left", "leg.right.upper", "leg.right.lower", "foot.right"];
    public bool HasBodyAuthoringSource => HasUnriggedSource || _model is { } model && GeneratedBodyRig.IsGenerated(model.Package.Document);
    public bool HasGeneratedBodyRig => _model is { } model && GeneratedBodyRig.IsGenerated(model.Package.Document);
    public bool CanEditBodyComponentChoices => HasModel && !IsBusy;
    public bool HasUnsavedBodyComponents => HasModel && _model is { } model &&
        !(ComponentSession(model).Components)
            .SequenceEqual(BodyComponents.Select(static c => c.ToContract()));
    public IRelayCommand SaveBodyComponentsCommand { get; private set; } = null!;
    public IAsyncRelayCommand BuildBodyRigCommand { get; private set; } = null!;
    public IAsyncRelayCommand BindBodyGeometryCommand { get; private set; } = null!;
    public IRelayCommand CancelBodyAuthoringCommand { get; private set; } = null!;
    public event EventHandler<BodyComponentsEventArgs>? BodyComponentsApplyRequested;
    public event EventHandler<BodyModelEventArgs>? BodyModelApplyRequested;

    private void InitializeBodyAuthoring()
    {
        SaveBodyComponentsCommand = new RelayCommand(SaveBodyComponents, () => HasModel && !IsBusy);
        BuildBodyRigCommand = new AsyncRelayCommand(token => AuthorBodyAsync(bind: false, token), () => HasUnriggedSource && HasSavedBodyGuide && !IsBusy && !HasUnsavedBodyComponents);
        BindBodyGeometryCommand = new AsyncRelayCommand(token => AuthorBodyAsync(bind: true, token), () => HasGeneratedBodyRig && !IsBusy && !HasUnsavedBodyComponents);
        CancelBodyAuthoringCommand = new RelayCommand(() => { BuildBodyRigCommand.Cancel(); BindBodyGeometryCommand.Cancel(); },
            () => BuildBodyRigCommand.IsRunning || BindBodyGeometryCommand.IsRunning);
        BuildBodyRigCommand.PropertyChanged += (_, _) => CancelBodyAuthoringCommand.NotifyCanExecuteChanged();
        BindBodyGeometryCommand.PropertyChanged += (_, _) => CancelBodyAuthoringCommand.NotifyCanExecuteChanged();
    }

    private void SaveBodyComponents()
    {
        if (_model is not { } model) return;
        try
        {
            var session = ComponentSession(model);
            var components = BodyComponents.Select(static c => c.ToContract()).ToImmutableArray();
            foreach (var component in components) component.Validate();
            BodyComponentsApplyRequested?.Invoke(this, new(model, session, components));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { BodyAuthoringStatus = "Component choices need attention: " + error.Message; }
    }

    private void RestoreBodyAuthoring()
    {
        _restoringBodyComponents = true;
        foreach (var component in BodyComponents) component.PropertyChanged -= OnBodyComponentChanged;
        BodyComponents.Clear();
        if (_model is not { } model) { _restoringBodyComponents = false; NotifyBodyAuthoring(); return; }
        var session = ComponentSession(model);
        foreach (var component in session.Components)
        {
            var choice = new BodyComponentChoice(component); choice.PropertyChanged += OnBodyComponentChanged; BodyComponents.Add(choice);
        }
        _restoringBodyComponents = false;
        BodyAuthoringStatus = !HasBodyAuthoringSource ? "Component classification is separate from the existing rig and weights; save choices to use them in analysis."
            : HasGeneratedBodyRig ? session.BindingBackend is null
            ? "Draft body skeleton restored. Choose component binding modes, then bind geometry."
            : "Generated binding restored. Deformation, hands, helpers and native behavior still require review."
            : "Build a draft skeleton after saving and placing the body guides.";
        NotifyBodyAuthoring();
    }

    private void OnBodyComponentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_restoringBodyComponents) return;
        BodyAuthoringStatus = "Component choices changed. Save them before running detection, rig generation or binding.";
        NotifyBodyAuthoring(); DetectBodyCommand?.NotifyCanExecuteChanged();
    }

    private void InvalidateBodyAuthoring()
    {
        _bodyAuthoringGeneration++;
        BuildBodyRigCommand?.Cancel(); BindBodyGeometryCommand?.Cancel();
    }

    private void NotifyBodyAuthoring()
    {
        OnPropertyChanged(nameof(HasBodyAuthoringSource)); OnPropertyChanged(nameof(HasGeneratedBodyRig));
        OnPropertyChanged(nameof(CanEditBodyComponentChoices));
        OnPropertyChanged(nameof(HasUnsavedBodyComponents));
        SaveBodyComponentsCommand?.NotifyCanExecuteChanged(); BuildBodyRigCommand?.NotifyCanExecuteChanged();
        BindBodyGeometryCommand?.NotifyCanExecuteChanged(); CancelBodyAuthoringCommand?.NotifyCanExecuteChanged();
    }

    private async Task AuthorBodyAsync(bool bind, CancellationToken cancellationToken)
    {
        RouteStudioAction(bind ? RigStudioStage.Skin : RigStudioStage.Fit);
        if (_model is not { } source) return;
        CancelBodyGuideDrag();
        int resolution = BodyDetectionResolution; double power = BindingFalloffPower;
        long generation = ++_bodyAuthoringGeneration;
        IsBusy = true; NotifyStateChanged();
        BodyAuthoringStatus = bind ? "Preparing original source points and body volume…" : "Building the draft body skeleton from saved guides…";
        var progress = new Progress<int>(count => {
            if (generation == _bodyAuthoringGeneration && ReferenceEquals(_model, source)) BodyAuthoringStatus = $"Computing volume weights: {count} influences processed";
        });
        try
        {
            var result = await Task.Run(() => {
                if (!bind) return (Model: FbxGeneratedBodyBinding.Generate(source, cancellationToken), Status: "Draft body rig built from saved guides; source geometry is unchanged. Binding and native capability review remain required.");
                var work = FbxGeneratedBodyBinding.Prepare(source, cancellationToken);
                AutomaticSkinBindingResult binding;
                if (work.Points.All(static p => !p.FixedInfluences.IsEmpty && p.FixedInfluences.Sum(static w => w.Weight) == 1))
                {
                    work = FbxGeneratedBodyBinding.ForFixed(work);
                    binding = AutomaticSkinBinder.BindFixed(work.SourceSha256, work.Handles, work.Points, cancellationToken);
                }
                else
                {
                    var anatomy = FbxSourceGeometryAnalysis.Build(source, anatomyOnly: true, cancellationToken);
                    var proxy = SourceGeometrySeamProxy.Build(anatomy, cancellationToken);
                    var volume = SourceGeometryVolume.Build(proxy.Analysis, cancellationToken: cancellationToken);
                    var grid = SourceVolumeGrid.Build(volume, new() { LongestAxisCells = resolution }, cancellationToken: cancellationToken);
                    var options = new AutomaticSkinBindingOptions { DistancePower = power };
                    work = FbxGeneratedBodyBinding.ForVolume(work, grid, options);
                    binding = AutomaticSkinBinder.Bind(grid, work.Handles, work.Points, options, progress, cancellationToken);
                }
                if (!FbxGeneratedBodyBinding.TryApply(source, work, binding, out var applied, cancellationToken)) throw new InvalidOperationException("The binding inputs are no longer current.");
                int truncated = binding.Points.Count(static p => p.RemovedWeightBeforeRenormalization > 1e-6);
                return (Model: applied, Status: $"Assigned {binding.Points.Length} original source points; {truncated} points had provisional weights reduced to four influences. Inspect deformation before acceptance.");
            }, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _bodyAuthoringGeneration || !ReferenceEquals(_model, source)) return;
            BodyModelApplyRequested?.Invoke(this, new(source, result.Model, result.Status));
            BodyAuthoringStatus = result.Status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { if (generation == _bodyAuthoringGeneration) BodyAuthoringStatus = "Body authoring cancelled; the previous model was retained."; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or CustomModelFormatException or OverflowException)
        { if (ReferenceEquals(_model, source)) BodyAuthoringStatus = "Body authoring needs attention: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyBodyAuthoring(); }
    }

    private RiggingSession ComponentSession(FbxModelAuthoringImportResult model) => model.Package.Document.RiggingSession ??
        RiggingSessions.Create(model.Package.Document, model.Rig is null ? RigStudioEntryPath.AutoRigBiped :
            SelectedStudioEntry?.Path == RigStudioEntryPath.AdaptExistingRig ? RigStudioEntryPath.AdaptExistingRig : RigStudioEntryPath.RepairExistingRig);
}
