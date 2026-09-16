using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.App.ViewModels;

public sealed class BodyDetectionApplyEventArgs(FbxModelAuthoringImportResult model, RiggingSession session,
    RiggingJobToken token, AnatomicalDetectionResult detection) : EventArgs
{
    public FbxModelAuthoringImportResult Model { get; } = model;
    public RiggingSession Session { get; } = session;
    public RiggingJobToken Token { get; } = token;
    public AnatomicalDetectionResult Detection { get; } = detection;
}

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private string _bodyDetectionStatus = "Unrigged geometry can be analyzed into draft body guides.";
    [ObservableProperty] private int _bodyDetectionResolution = 64;
    [ObservableProperty] private bool _bodyLeftIsPositiveX = true;
    [ObservableProperty] private AnatomicalJointProposal? _selectedBodyProposal;
    private long _bodyDetectionGeneration;
    private BodyDetectionApplyEventArgs? _bodyDetectionWork;
    public AnatomicalDetectionResult? BodyDetection { get; private set; }
    public ObservableCollection<AnatomicalJointProposal> BodyProposals { get; } = [];
    public bool HasUnriggedSource => _model is { Rig: null } && !_model.Surfaces.IsEmpty;
    public IAsyncRelayCommand DetectBodyCommand { get; private set; } = null!;
    public IRelayCommand CancelBodyDetectionCommand { get; private set; } = null!;
    public IRelayCommand UseBodyGuidesCommand { get; private set; } = null!;
    public event EventHandler? BodyDetectionChanged;
    public event EventHandler<BodyDetectionApplyEventArgs>? BodyGuidesApplyRequested;

    private void InitializeBodyDetection()
    {
        InitializeBodyGuideEditing();
        InitializeBodyAuthoring();
        DetectBodyCommand = new AsyncRelayCommand(DetectBodyAsync, () => HasUnriggedSource && !IsBusy && !HasUnsavedBodyComponents);
        CancelBodyDetectionCommand = new RelayCommand(() => DetectBodyCommand.Cancel(), () => DetectBodyCommand.IsRunning);
        UseBodyGuidesCommand = new RelayCommand(() => {
            if (_bodyDetectionWork is { } work && ReferenceEquals(work.Model, _model)) BodyGuidesApplyRequested?.Invoke(this, work);
        }, () => _bodyDetectionWork is not null && BodyProposals.Count > 0 && !IsBusy);
        DetectBodyCommand.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(IAsyncRelayCommand.IsRunning) or nameof(IAsyncRelayCommand.CanBeCanceled))
                CancelBodyDetectionCommand.NotifyCanExecuteChanged();
        };
    }

    private void InvalidateBodyDetection()
    {
        CancelBodyGuideDrag();
        InvalidateBodyAuthoring();
        _bodyDetectionGeneration++;
        DetectBodyCommand?.Cancel();
        _bodyDetectionWork = null; BodyDetection = null; BodyProposals.Clear(); SelectedBodyProposal = null;
        BodyDetectionStatus = "Unrigged geometry can be analyzed into draft body guides.";
        BodyDetectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowBodyDetectionDraft(AnatomicalDetectionResult result, string status)
    {
        _bodyDetectionWork = null;
        PublishBodyDetection(result);
        BodyDetectionStatus = status;
        NotifyBodyDetectionCommands();
    }

    private async Task DetectBodyAsync(CancellationToken cancellationToken)
    {
        RouteStudioAction(RigStudioStage.Detect);
        if (_model is not { Rig: null } model) return;
        if (BodyDetectionResolution is < 16 or > 128) { BodyDetectionStatus = "Choose a body sampling resolution from 16 to 128."; return; }
        CancelBodyGuideDrag();
        var session = model.Package.Document.RiggingSession ?? RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.AutoRigBiped);
        var token = session.CreateJobToken();
        long generation = ++_bodyDetectionGeneration;
        int resolution = BodyDetectionResolution;
        var options = new AnatomicalDetectionOptions {
            Frame = new() { Left = BodyLeftIsPositiveX ? Vector3D.UnitX : -Vector3D.UnitX },
            Guides = session.Landmarks.Where(static l => l.Locked || l.UserApproved).Select(static l => new AnatomicalGuide(l.RoleId, l.Position, l.Locked)).ToImmutableArray(),
        };
        _bodyDetectionWork = null;
        IsBusy = true; BodyDetectionStatus = "Analyzing selected source geometry…"; NotifyBodyDetectionCommands();
        var progress = new Progress<SourceVolumeGridProgress>(p => {
            if (generation == _bodyDetectionGeneration && ReferenceEquals(model, _model)) BodyDetectionStatus = $"Sampling body volume: {p.CompletedSlices}/{p.TotalSlices} slices";
        });
        try
        {
            var result = await Task.Run(() => {
                var source = FbxSourceGeometryAnalysis.Build(model, cancellationToken: cancellationToken);
                var proxy = SourceGeometrySeamProxy.Build(source, cancellationToken);
                var volume = SourceGeometryVolume.Build(proxy.Analysis, cancellationToken: cancellationToken);
                if (volume.HasUnreliableTopology)
                    throw new InvalidDataException("Selected geometry still has open or unreliable boundaries after exact seam matching. Review component selection and geometry before volume-based detection.");
                var grid = SourceVolumeGrid.Build(volume, new() { LongestAxisCells = resolution }, progress, cancellationToken);
                return AnatomicalRigDetector.Detect(grid, options, cancellationToken);
            }, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _bodyDetectionGeneration || !ReferenceEquals(model, _model)) return;
            _bodyDetectionGeneration++; // Late progress callbacks cannot replace the completed result/status.
            _bodyDetectionWork = new(model, session, token, result);
            PublishBodyDetection(result);
            BodyDetectionStatus = $"{result.Joints.Length} draft guides; {result.Status}. Review the supplied frame, joint positions and diagnostics before fitting." +
                Environment.NewLine + string.Join(Environment.NewLine, result.Diagnostics.Select(static d => d.Message).Distinct());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _bodyDetectionGeneration) BodyDetectionStatus = "Body detection cancelled; no guides were applied.";
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            if (ReferenceEquals(model, _model) && (generation == _bodyDetectionGeneration || generation + 1 == _bodyDetectionGeneration))
            { _bodyDetectionWork = null; BodyDetectionStatus = "Body detection needs attention: " + exception.Message; }
        }
        finally { if (generation == _bodyDetectionGeneration) _bodyDetectionGeneration++; IsBusy = false; NotifyStateChanged(); NotifyBodyDetectionCommands(); }
    }

    private void PublishBodyDetection(AnatomicalDetectionResult result)
    {
        BodyDetection = result; BodyProposals.Clear();
        foreach (var proposal in result.Joints) BodyProposals.Add(proposal);
        SelectedBodyProposal = BodyProposals.FirstOrDefault();
        BodyDetectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void RestoreStoredBodyGuides()
    {
        if (_model is not { } model || model.Package.Document.RiggingSession is not { } session || !HasBodyAuthoringSource && session.Hands.IsEmpty) return;
        var handIds = session.Hands.SelectMany(h => new[] { h.WristGuideId }.Concat(h.Fingers.SelectMany(f => f.JointGuideIds))).ToHashSet();
        var guides = session.Landmarks.Where(l => HasBodyAuthoringSource && AnatomicalDetectionAdoption.OwnsRole(l.RoleId) || handIds.Contains(l.Id)).ToArray();
        if (guides.Length == 0) return;
        BodyProposals.Clear();
        foreach (var guide in guides)
            BodyProposals.Add(new(guide.RoleId, guide.Position, AnatomicalPlacementMethod.StoredGuide, 0, guide.Locked, null));
        SelectedBodyProposal = BodyProposals.FirstOrDefault();
        BodyDetectionStatus = "Saved draft body guides restored. These positions still require fitting review.";
        BodyDetectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void NotifyBodyDetectionCommands()
    {
        OnPropertyChanged(nameof(HasUnriggedSource)); OnPropertyChanged(nameof(BodyDetection));
        DetectBodyCommand?.NotifyCanExecuteChanged(); CancelBodyDetectionCommand?.NotifyCanExecuteChanged(); UseBodyGuidesCommand?.NotifyCanExecuteChanged();
        NotifyBodyGuideEditing();
        NotifyBodyAuthoring();
    }
    partial void OnSelectedBodyProposalChanged(AnatomicalJointProposal? value)
    { RefreshBodyGuideEditor(); BodyDetectionChanged?.Invoke(this, EventArgs.Empty); }
    private void ResetBodyDetectionInputs()
    { InvalidateBodyDetection(); RestoreStoredBodyGuides(); NotifyBodyDetectionCommands(); }
    partial void OnBodyDetectionResolutionChanged(int value) => ResetBodyDetectionInputs();
    partial void OnBodyLeftIsPositiveXChanged(bool value) => ResetBodyDetectionInputs();
}
