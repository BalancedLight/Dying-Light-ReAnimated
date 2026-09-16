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

public sealed record HandBranchChoice(string? Id, string Label);
public sealed partial class HandDigitRow : ObservableObject
{
    public string Id { get; }
    public HandDigitRow(string id) => Id = id;
    [ObservableProperty] private RigFingerPresence _presence;
    [ObservableProperty] private HandBranchChoice? _branch;
    [ObservableProperty] private bool _reviewed;
    [ObservableProperty] private double _rollDegrees;
    public ContactVector CurlNormal { get; } = new();
}

public sealed partial class RigConformanceWizardViewModel
{
    private FbxHandDetectionWork? _handWork;
    private FbxModelAuthoringImportResult? _handSource;
    private long _handGeneration;
    private bool _restoringHand;
    [ObservableProperty] private RigHandSide _handSide;
    [ObservableProperty] private ContactComponentChoice? _handComponent;
    [ObservableProperty] private int _handResolution = 64;
    [ObservableProperty] private int _handExpectedDigits = 5;
    [ObservableProperty] private HandDigitRow? _selectedHandDigit;
    [ObservableProperty] private string _extraHandDigitId = "extra";
    [ObservableProperty] private bool _handReviewEnabled;
    [ObservableProperty] private string _handStatus = "Start the studio, choose a hand region and review the wrist seed before detection.";
    public ContactVector HandWrist { get; } = new();
    public ContactVector HandForward { get; } = new();
    public ContactVector HandNormal { get; } = new();
    public ContactVector HandMinimum { get; } = new();
    public ContactVector HandMaximum { get; } = new();
    public ContactVector HandPalmOffset { get; } = new();
    public ContactVector HandPalmRotation { get; } = new();
    public ObservableCollection<ContactComponentChoice> HandComponents { get; } = [];
    public ObservableCollection<HandBranchChoice> HandBranches { get; } = [];
    public ObservableCollection<HandDigitRow> HandDigits { get; } = [];
    public IReadOnlyList<RigHandSide> HandSides { get; } = Enum.GetValues<RigHandSide>();
    public IReadOnlyList<RigFingerPresence> HandPresenceChoices { get; } = Enum.GetValues<RigFingerPresence>();
    public IAsyncRelayCommand DetectHandCommand { get; private set; } = null!;
    public IRelayCommand UseHandProposalsCommand { get; private set; } = null!;
    public IRelayCommand CancelHandDetectionCommand { get; private set; } = null!;
    public IRelayCommand SuggestHandRegionCommand { get; private set; } = null!;
    public IRelayCommand AddHandDigitCommand { get; private set; } = null!;
    public IRelayCommand ReviewHandSetupCommand { get; private set; } = null!;
    public IAsyncRelayCommand BuildHandRigCommand { get; private set; } = null!;
    public event EventHandler? HandPreviewChanged;
    public event EventHandler<BodyModelEventArgs>? HandModelApplyRequested;
    public LocalHandDetectionResult? HandDetection => _handWork?.Detection;
    public bool HasHandSetup => CurrentHandSetup is not null;
    private RigHandSetup? CurrentHandSetup => _model?.Package.Document.RiggingSession?.Hands.FirstOrDefault(h => h.Side == HandSide);
    public bool CanDetectHand => !IsBusy && HasStudioSession && HandComponent is not null;
    public bool CanUseHandProposals => !IsBusy && _handWork?.Detection.PalmFrame is not null;
    public bool CanReviewHandSetup => !IsBusy && HasHandSetup;
    private bool CanEditPendingHandGuide => SelectedStoredBodyGuide is { } guide && guide.RoleId.StartsWith("finger.", StringComparison.Ordinal) &&
        _model?.Package.Document.RiggingSession is { } session && session.Hands.Any(h => h.Fingers.Any(f => f.JointGuideIds.Contains(guide.Id))) &&
        !session.Recipe.Assignments.Any(a => a.RoleId == guide.RoleId);
    public bool HasUnsavedHandReview => CurrentHandSetup is { } hand && (HandPalmOffset.Value != Vector3D.Zero || HandPalmRotation.Value != Vector3D.Zero ||
        HandDigits.Count != hand.Fingers.Length || HandDigits.Any(row => hand.Fingers.FirstOrDefault(f => f.Id == row.Id) is not { } saved ||
            row.Presence != saved.Presence || row.Reviewed != saved.UserApproved || row.RollDegrees != saved.RollDegrees || row.CurlNormal.Value != (saved.CurlPlaneNormal ?? Vector3D.UnitY)));
    public bool CanBuildHandRig => !IsBusy && !HasUnsavedHandReview && _model is { } model && GeneratedBodyRig.IsGenerated(model.Package.Document) &&
        CurrentHandSetup is { UserApproved: true } hand && hand.Fingers.All(f => f.Presence == RigFingerPresence.Absent || f.Presence == RigFingerPresence.Present && f.UserApproved);
    public IEnumerable<AnatomicalJointProposal> HandGuideChoices => CurrentHandSetup is { } hand
        ? BodyProposals.Where(p => _model!.Package.Document.RiggingSession!.Landmarks.Any(g => g.RoleId == p.Role &&
            (g.Id == hand.WristGuideId || hand.Fingers.Any(f => f.JointGuideIds.Contains(g.Id))))) : [];

    private void InitializeHands()
    {
        InitializeHandBinding();
        HandForward.Set(Vector3D.UnitZ); HandNormal.Set(Vector3D.UnitY);
        foreach (var vector in new[] { HandWrist, HandForward, HandNormal, HandMinimum, HandMaximum })
            vector.PropertyChanged += (_, _) => InvalidateHandDetection();
        foreach (var vector in new[] { HandPalmOffset, HandPalmRotation })
            vector.PropertyChanged += (_, _) => HandDraftChanged();
        DetectHandCommand = new AsyncRelayCommand(DetectHandAsync, () => CanDetectHand);
        UseHandProposalsCommand = new RelayCommand(UseHandProposals, () => CanUseHandProposals);
        CancelHandDetectionCommand = new RelayCommand(() => DetectHandCommand.Cancel(), () => DetectHandCommand.IsRunning);
        SuggestHandRegionCommand = new RelayCommand(SuggestHandRegion, () => _model is not null && !IsBusy);
        AddHandDigitCommand = new RelayCommand(AddHandDigit, () => !IsBusy);
        ReviewHandSetupCommand = new RelayCommand(ReviewHandSetup, () => CanReviewHandSetup);
        BuildHandRigCommand = new AsyncRelayCommand(BuildHandRigAsync, () => CanBuildHandRig);
        DetectHandCommand.PropertyChanged += (_, _) => CancelHandDetectionCommand.NotifyCanExecuteChanged();
    }

    private void RestoreHands()
    {
        var component = HandComponent?.Id;
        _restoringHand = true;
        try
        {
            DetectHandCommand?.Cancel(); BuildHandRigCommand?.Cancel(); _handGeneration++; _handWork = null; _handSource = null;
            ClearHandBinding();
            HandComponents.Clear(); HandBranches.Clear(); HandBranches.Add(new(null, "Not assigned")); HandDigits.Clear();
            if (_model is { } model)
                foreach (var group in model.Surfaces.Where(static s => s.SourceGeometry is not null).GroupBy(static s => s.SourceGeometry!.Id))
                    HandComponents.Add(new(group.Key, group.First().MeshName));
            HandComponent = HandComponents.FirstOrDefault(c => c.Id == component) ?? HandComponents.FirstOrDefault();
            var hand = CurrentHandSetup;
            foreach (string id in hand?.Fingers.Select(static f => f.Id) ?? ["thumb", "index", "middle", "ring", "little"])
            {
                var saved = hand?.Fingers.FirstOrDefault(f => f.Id == id);
                var row = new HandDigitRow(id) { Presence = saved?.Presence ?? RigFingerPresence.Unresolved, Reviewed = saved?.UserApproved ?? false,
                    RollDegrees = saved?.RollDegrees ?? 0, Branch = HandBranches[0] };
                row.CurlNormal.Set(saved?.CurlPlaneNormal ?? Vector3D.UnitY); AttachHandRow(row); HandDigits.Add(row);
            }
            SelectedHandDigit = HandDigits.FirstOrDefault(); HandPalmOffset.Set(Vector3D.Zero); HandPalmRotation.Set(Vector3D.Zero);
            HandStatus = hand is null ? "Choose local bounds and a wrist seed. No fingers are assumed absent from a failed detection." :
                "Saved hand guides restored. Fit the guides, review curl/roll and every digit declaration, then approve the setup.";
        }
        finally { _restoringHand = false; }
        SuggestHandRegion(); NotifyHands();
    }

    private void SuggestHandRegion()
    {
        if (_model is not { } model || HandComponent is not { } component) return;
        var points = model.Surfaces.Where(s => s.SourceGeometry?.Id == component.Id).SelectMany(s => s.Vertices.Select(v => v.Position)).ToArray();
        if (points.Length == 0) return;
        _restoringHand = true;
        try
        {
            var minimum = new Vector3D(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
            var maximum = new Vector3D(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
            string side = HandSide.ToString().ToLowerInvariant();
            var wrist = model.Package.Document.RiggingSession?.Landmarks.FirstOrDefault(g => g.RoleId == "hand." + side);
            var elbow = model.Package.Document.RiggingSession?.Landmarks.FirstOrDefault(g => g.RoleId == "arm." + side + ".lower");
            if (wrist is not null && elbow is not null && (wrist.Position - elbow.Position).TryNormalize(out var direction))
            {
                double reach = (wrist.Position - elbow.Position).Length;
                var center = wrist.Position + direction * (reach * .4);
                HandMinimum.Set(center - Vector3D.One * (reach * .75)); HandMaximum.Set(center + Vector3D.One * (reach * .75));
                HandWrist.Set(wrist.Position); HandForward.Set(direction);
                var up = Math.Abs(Vector3D.Dot(direction, Vector3D.UnitY)) < .9 ? Vector3D.UnitY : Vector3D.UnitZ;
                HandNormal.Set((up - direction * Vector3D.Dot(up, direction)).Normalized());
            }
            else
            {
                double margin = Math.Max(1e-6, (maximum - minimum).Length * .04);
                HandMinimum.Set(minimum - Vector3D.One * margin); HandMaximum.Set(maximum + Vector3D.One * margin);
                HandWrist.Set(wrist?.Position ?? new((minimum.X + maximum.X) * .5, (minimum.Y + maximum.Y) * .5, minimum.Z));
                HandForward.Set(Vector3D.UnitZ); HandNormal.Set(Vector3D.UnitY);
            }
        }
        finally { _restoringHand = false; }
        InvalidateHandDetection();
    }

    private void InvalidateHandDetection()
    {
        if (_restoringHand) return;
        DetectHandCommand?.Cancel(); _handGeneration++; _handWork = null; _handSource = null;
        ClearHandBinding();
        NotifyHands(); HandPreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnHandSideChanged(RigHandSide value) { if (!_restoringHand) RestoreHands(); }
    partial void OnHandComponentChanged(ContactComponentChoice? value) => InvalidateHandDetection();
    partial void OnHandResolutionChanged(int value) => InvalidateHandDetection();
    partial void OnHandExpectedDigitsChanged(int value) => InvalidateHandDetection();
    partial void OnHandReviewEnabledChanged(bool value) => HandPreviewChanged?.Invoke(this, EventArgs.Empty);
    partial void OnSelectedHandDigitChanged(HandDigitRow? value) { if (!_restoringHand) { HandPreviewChanged?.Invoke(this, EventArgs.Empty); HandBindingDisplayChanged?.Invoke(this, EventArgs.Empty); } }
    private void AttachHandRow(HandDigitRow row)
    { row.PropertyChanged += (_, _) => HandDraftChanged(); row.CurlNormal.PropertyChanged += (_, _) => HandDraftChanged(); }
    private void HandDraftChanged()
    { if (!_restoringHand) { ClearHandBinding(); NotifyHands(); HandPreviewChanged?.Invoke(this, EventArgs.Empty); } }

    private async Task DetectHandAsync(CancellationToken token)
    {
        if (_model is not { } model || HandComponent is not { } component) return;
        RouteStudioAction(RigStudioStage.Detect); model = _model!;
        var minimum = HandMinimum.Value; var maximum = HandMaximum.Value;
        var options = new LocalHandDetectionOptions { Side = HandSide.ToString().ToLowerInvariant(), Wrist = HandWrist.Value,
            Forward = HandForward.Value, PalmNormalHint = HandNormal.Value, ExpectedDigits = HandExpectedDigits };
        int resolution = HandResolution; long generation = ++_handGeneration;
        IsBusy = true; NotifyStateChanged();
        try
        {
            var work = await Task.Run(() => FbxLocalHandAuthoring.Detect(model, component.Id, minimum, maximum, options,
                new SourceVolumeGridOptions { LongestAxisCells = resolution }, token), token);
            if (generation != _handGeneration || !ReferenceEquals(_model, model)) return;
            _handWork = work; _handSource = model;
            HandBranches.Clear(); HandBranches.Add(new(null, "Not assigned"));
            foreach (var (finger, index) in work.Detection.Fingers.Select((f, i) => (f, i)))
                HandBranches.Add(new(finger.BranchId, $"Candidate {index + 1} · {finger.BranchLength * 1000:0.#} mm"));
            foreach (var row in HandDigits)
            {
                var finger = work.Detection.Fingers.FirstOrDefault(f => f.Digit == row.Id);
                row.Branch = HandBranches.FirstOrDefault(b => b.Id == finger?.BranchId) ?? HandBranches[0];
                row.Presence = finger is null ? RigFingerPresence.Unresolved : RigFingerPresence.Present;
                row.Reviewed = false; if (finger is not null) row.CurlNormal.Set(finger.CurlPlaneNormal);
            }
            HandReviewEnabled = true;
            HandStatus = $"{work.Detection.Fingers.Length} branch proposals at {work.Grid.CellSize * 1000:0.##} mm spacing. " +
                string.Join(" ", work.Detection.Diagnostics.Select(static d => d.Message));
        }
        catch (OperationCanceledException) { if (generation == _handGeneration) HandStatus = "Hand detection cancelled."; }
        catch (Exception error) when (HandError(error)) { if (generation == _handGeneration) HandStatus = "Hand detection needs assistance: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyHands(); HandPreviewChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void UseHandProposals()
    {
        if (_model is not { } model || _handWork is not { } work || !ReferenceEquals(model, _handSource)) return;
        try
        {
            var current = model.Package.Document.RiggingSession ?? work.Session;
            var adopted = HandDetectionAdoption.Adopt(current, work.Token, work.Detection, work.Wrist,
                HandDigits.Select(r => new HandDigitSelection(r.Id, r.Presence, r.Branch?.Id)).ToArray());
            PublishHandSession(model, adopted, "Saved local hand guides and declarations. Existing bones and skinning were preserved.");
            RouteStudioAction(RigStudioStage.Fit); HandReviewEnabled = true;
        }
        catch (Exception error) when (HandError(error)) { HandStatus = "Hand guides were not saved: " + error.Message; }
    }

    private void AddHandDigit()
    {
        string id = ExtraHandDigitId.Trim().ToLowerInvariant();
        if (id.Length == 0 || id.Contains('.') || id.Any(char.IsWhiteSpace) || HandDigits.Any(r => r.Id == id))
        { HandStatus = "Choose a unique digit identifier without spaces or dots."; return; }
        var row = new HandDigitRow(id) { Branch = HandBranches.FirstOrDefault() }; row.CurlNormal.Set(Vector3D.UnitY); AttachHandRow(row); HandDigits.Add(row); HandDraftChanged();
    }

    private void ReviewHandSetup()
    {
        if (_model is not { } model || model.Package.Document.RiggingSession is not { } session || CurrentHandSetup is not { } hand) return;
        try
        {
            var fingers = HandDigits.Select(row =>
            {
                var saved = hand.Fingers.FirstOrDefault(f => f.Id == row.Id);
                return new RigFingerDeclaration { Id = row.Id, Presence = row.Presence,
                    JointGuideIds = row.Presence is RigFingerPresence.Absent or RigFingerPresence.Fused ? [] : saved?.JointGuideIds ?? [],
                    CurlPlaneNormal = row.CurlNormal.Value, RollDegrees = row.RollDegrees, UserApproved = row.Reviewed, Evidence = saved?.Evidence ?? [] };
            }).ToImmutableArray();
            var palm = hand.PalmFrame * TransformMatrix.CreateTranslation(HandPalmOffset.Value) * HandRotation(HandPalmRotation.Value);
            var reviewed = hand with { PalmFrame = palm, Fingers = fingers, UserApproved = true };
            var replacement = session with { Hands = session.Hands.Select(h => h.Side == HandSide ? reviewed : h).ToImmutableArray() };
            var candidateDocument = model.Package.Document with { RiggingSession = replacement };
            // Existing owned finger topology cannot disappear through a declaration edit.
            if (GeneratedBodyRig.IsGenerated(model.Package.Document) && !GeneratedBodyRig.IsGenerated(candidateDocument))
                throw new InvalidOperationException("This changes an existing generated hand's topology; use a reviewed rig transaction instead.");
            PublishHandSession(model, RiggingSessions.Change(session, replacement, RiggingEditKind.Anatomy), "Saved hand review and curl/roll settings. Native compatibility remains unverified.");
        }
        catch (Exception error) when (HandError(error)) { HandStatus = "Hand review was not saved: " + error.Message; }
    }

    private async Task BuildHandRigAsync(CancellationToken token)
    {
        if (_model is not { } model) return;
        RigHandSide side = HandSide;
        IsBusy = true; NotifyStateChanged();
        try
        {
            var result = await Task.Run(() => FbxGeneratedHandAuthoring.Append(model, side, token), token);
            if (!ReferenceEquals(_model, model)) return;
            if (ReferenceEquals(result, model)) { HandStatus = "This reviewed hand is already generated."; return; }
            HandModelApplyRequested?.Invoke(this, new(model, result, "Appended reviewed finger bones. Existing weights were preserved; new finger weights still need binding and review."));
            HandStatus = "Finger bones appended. Bind and review the hand before animation acceptance.";
        }
        catch (OperationCanceledException) { HandStatus = "Hand generation cancelled."; }
        catch (Exception error) when (HandError(error)) { HandStatus = "Hand rig was not changed: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyHands(); }
    }

    private void PublishHandSession(FbxModelAuthoringImportResult model, RiggingSession session, string status)
    {
        var result = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session, LastBuildReceipt = null } } };
        HandModelApplyRequested?.Invoke(this, new(model, result, status)); HandStatus = status;
    }
    private static TransformMatrix HandRotation(Vector3D degrees)
    {
        if (!degrees.IsFinite) throw new ArgumentException("Palm rotation must be finite.", nameof(degrees));
        const double r = Math.PI / 180;
        return TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitX, degrees.X * r)) *
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, degrees.Y * r)) *
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitZ, degrees.Z * r));
    }
    public TransformMatrix? HandPalmPreview => (CurrentHandSetup?.PalmFrame ?? HandDetection?.PalmFrame) is { } frame && HandPalmOffset.Value.IsFinite && HandPalmRotation.Value.IsFinite
        ? frame * TransformMatrix.CreateTranslation(HandPalmOffset.Value) * HandRotation(HandPalmRotation.Value) : null;
    private static bool HandError(Exception error) => error is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException;
    private void RefreshHandMetadata(FbxModelAuthoringImportResult current)
    {
        if (_handWork is { } work) { _handWork = FbxLocalHandAuthoring.RefreshMetadata(work, current); _handSource = _handWork is null ? null : current; }
        RefreshHandBindingMetadata(current);
        NotifyHands();
    }
    private void NotifyHands()
    {
        foreach (string property in new[] { nameof(HandDetection), nameof(HasHandSetup), nameof(HasUnsavedHandReview), nameof(CanDetectHand), nameof(CanUseHandProposals), nameof(CanReviewHandSetup), nameof(CanBuildHandRig), nameof(HandGuideChoices) }) OnPropertyChanged(property);
        DetectHandCommand?.NotifyCanExecuteChanged(); UseHandProposalsCommand?.NotifyCanExecuteChanged(); ReviewHandSetupCommand?.NotifyCanExecuteChanged();
        BuildHandRigCommand?.NotifyCanExecuteChanged(); SuggestHandRegionCommand?.NotifyCanExecuteChanged(); AddHandDigitCommand?.NotifyCanExecuteChanged();
        NotifyHandBinding();
    }
}
