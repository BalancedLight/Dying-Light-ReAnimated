using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.App.ViewModels;

public sealed partial class ContactVector : ObservableObject
{
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _z;
    public Vector3D Value => new(X, Y, Z);
    public void Set(Vector3D value) { X = value.X; Y = value.Y; Z = value.Z; }
}
public sealed record ContactNodeChoice(Guid? Id, string Name);
public sealed record ContactComponentChoice(string Id, string Name);
public sealed record ContactAxisChoice(ContactAxisMode Mode, string Label);
public sealed record ContactOriginChoice(ContactOriginMode Mode, string Label);
public sealed record ContactPreview(ContactFootprintFit Fit, TransformMatrix GlobalFrame, Vector3D Center, Vector3D HalfExtents);

public sealed partial class RigConformanceWizardViewModel
{
    private ContactAuthoringSnapshot? _contactSnapshot;
    private bool _restoringContact;
    private long _contactGeneration;
    [ObservableProperty] private ContactNodeChoice? _contactParent;
    [ObservableProperty] private ContactNodeChoice? _contactHelper;
    [ObservableProperty] private ContactComponentChoice? _contactComponent;
    [ObservableProperty] private ContactAxisChoice? _contactAxes;
    [ObservableProperty] private ContactOriginChoice? _contactOrigin;
    [ObservableProperty] private string _contactName = "sole_contact";
    [ObservableProperty] private string _contactRole = "contact.custom";
    [ObservableProperty] private string _contactSourcePoints = string.Empty;
    [ObservableProperty] private bool _contactUseFootWeights = true;
    [ObservableProperty] private double _contactMinimumWeight = .5;
    [ObservableProperty] private double _contactBottomBand = .2;
    [ObservableProperty] private string _contactStatus = "Start a studio session, select a foot bone and shoe geometry, then fit a draft.";
    public ContactVector ContactUp { get; } = new();
    public ContactVector ContactForward { get; } = new();
    public ContactVector ContactOffset { get; } = new();
    public ContactVector ContactRotation { get; } = new();
    public ContactVector ContactBoundsCenter { get; } = new();
    public ContactVector ContactHalfExtents { get; } = new();
    public ObservableCollection<ContactNodeChoice> ContactParents { get; } = [];
    public ObservableCollection<ContactNodeChoice> ContactHelpers { get; } = [];
    public ObservableCollection<ContactComponentChoice> ContactComponents { get; } = [];
    public IReadOnlyList<ContactAxisChoice> ContactAxisChoices { get; } = [new(ContactAxisMode.FootprintMajorAxis, "Fit long axis from footprint"), new(ContactAxisMode.ExplicitDirections, "Use the entered directions")];
    public IReadOnlyList<ContactOriginChoice> ContactOriginChoices { get; } = [new(ContactOriginMode.ParentPivot, "Keep foot pivot"), new(ContactOriginMode.FootprintCenter, "Center on contact plane")];
    public IAsyncRelayCommand FitContactCommand { get; private set; } = null!;
    public IRelayCommand ApplyContactCommand { get; private set; } = null!;
    public IRelayCommand ClearContactCommand { get; private set; } = null!;
    public IRelayCommand CancelContactCommand { get; private set; } = null!;
    public event EventHandler? ContactPreviewChanged;
    public event EventHandler<BodyModelEventArgs>? ContactModelApplyRequested;
    public bool CanFitContact => !IsBusy && _model?.Package.Document.RiggingSession is not null && ContactParent?.Id is not null && ContactComponent is not null;
    public bool HasContactPreview => _contactSnapshot is not null;
    public bool CanApplyContact => HasContactPreview && !IsBusy && _contactSnapshot?.Fit.OrientationAmbiguous == false &&
        ContactHalfExtents.Value.IsFinite && ContactHalfExtents.X > 0 && ContactHalfExtents.Y > 0 && ContactHalfExtents.Z > 0 &&
        ContactBoundsCenter.Value.IsFinite && ContactOffset.Value.IsFinite && ContactRotation.Value.IsFinite &&
        !string.IsNullOrWhiteSpace(ContactName) && !string.IsNullOrWhiteSpace(ContactRole);

    private void InitializeContacts()
    {
        ContactUp.Set(Vector3D.UnitY); ContactForward.Set(Vector3D.UnitZ);
        ContactAxes = ContactAxisChoices[0]; ContactOrigin = ContactOriginChoices[0];
        foreach (var vector in new[] { ContactUp, ContactForward }) vector.PropertyChanged += (_, _) => InvalidateContactInput();
        foreach (var vector in new[] { ContactOffset, ContactRotation, ContactBoundsCenter, ContactHalfExtents })
            vector.PropertyChanged += (_, _) => { if (!_restoringContact) { NotifyContacts(); ContactPreviewChanged?.Invoke(this, EventArgs.Empty); } };
        FitContactCommand = new AsyncRelayCommand(FitContactAsync, () => CanFitContact);
        ApplyContactCommand = new RelayCommand(ApplyContact, () => CanApplyContact);
        ClearContactCommand = new RelayCommand(InvalidateContactInput, () => HasContactPreview);
        CancelContactCommand = new RelayCommand(() => FitContactCommand.Cancel(), () => FitContactCommand.IsRunning);
        FitContactCommand.PropertyChanged += (_, _) => CancelContactCommand.NotifyCanExecuteChanged();
    }

    private void RestoreContacts()
    {
        var parent = ContactParent?.Id; var component = ContactComponent?.Id; var helper = ContactHelper?.Id;
        _restoringContact = true;
        try
        {
            FitContactCommand?.Cancel(); _contactGeneration++; _contactSnapshot = null;
            ContactParents.Clear(); ContactComponents.Clear(); ContactHelpers.Clear();
            ContactHelpers.Add(new(null, "Create a contact helper"));
            if (_model?.Package.Document is { RiggingSession: { } session } document)
            {
                var entities = session.Recipe.Entities.ToDictionary(static e => e.EntityId);
                var observed = RiggingSessions.ObserveSourceHierarchy(document);
                for (int i = 0; i < observed.Length; i++)
                {
                    var observation = observed[i];
                    var entity = entities[observation.EntityId];
                    BoneKind? sourceKind = i < document.Bones.Length ? document.Bones[i].Kind : null;
                    if (entity.Kind == RigNativeEntityKind.Bone || entity.Kind == RigNativeEntityKind.Unknown && sourceKind == BoneKind.Deform)
                        ContactParents.Add(new(entity.EntityId, entity.NativeName));
                    if (entity.Kind == RigNativeEntityKind.Helper || entity.Kind == RigNativeEntityKind.Unknown && sourceKind == BoneKind.Helper)
                        ContactHelpers.Add(new(entity.EntityId, entity.NativeName));
                }
                foreach (var group in _model.Surfaces.Where(static s => s.SourceGeometry is not null).GroupBy(static s => s.SourceGeometry!.Id))
                    ContactComponents.Add(new(group.Key, group.First().MeshName));
            }
            ContactParent = ContactParents.FirstOrDefault(c => c.Id == parent);
            ContactComponent = ContactComponents.FirstOrDefault(c => c.Id == component) ?? ContactComponents.FirstOrDefault();
            ContactHelper = ContactHelpers.FirstOrDefault(c => c.Id == helper) ?? ContactHelpers[0];
            ContactStatus = "Select the foot bone explicitly. Fit geometry, review the footprint and bounds, then apply.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        { ContactStatus = "Contact setup needs a current hierarchy: " + error.Message; }
        finally { _restoringContact = false; NotifyContacts(); }
    }

    private void InvalidateContactInput()
    {
        if (_restoringContact) return;
        FitContactCommand?.Cancel(); _contactGeneration++; _contactSnapshot = null;
        ContactStatus = "Fit the current foot and shoe selection before applying.";
        NotifyContacts(); ContactPreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnContactParentChanged(ContactNodeChoice? value) => InvalidateContactInput();
    partial void OnContactComponentChanged(ContactComponentChoice? value) => InvalidateContactInput();
    partial void OnContactAxesChanged(ContactAxisChoice? value) => InvalidateContactInput();
    partial void OnContactOriginChanged(ContactOriginChoice? value) => InvalidateContactInput();
    partial void OnContactUseFootWeightsChanged(bool value) => InvalidateContactInput();
    partial void OnContactMinimumWeightChanged(double value) => InvalidateContactInput();
    partial void OnContactBottomBandChanged(double value) => InvalidateContactInput();
    partial void OnContactSourcePointsChanged(string value) => InvalidateContactInput();
    partial void OnContactNameChanged(string value) => NotifyContacts();
    partial void OnContactRoleChanged(string value) => NotifyContacts();
    partial void OnContactHelperChanged(ContactNodeChoice? value)
    {
        if (_restoringContact || value?.Id is not { } id) return;
        ContactName = value.Name;
        if (_model?.Package.Document.RiggingSession?.Recipe.Helpers.FirstOrDefault(h => h.EntityId == id) is { } recipe)
        { ContactRole = recipe.RoleId; ContactParent = ContactParents.FirstOrDefault(p => p.Id == recipe.ParentEntityId); }
        else if (_model is { } model)
        {
            var observed = RiggingSessions.ObserveSourceHierarchy(model.Package.Document).First(o => o.EntityId == id);
            ContactParent = ContactParents.FirstOrDefault(p => p.Id == observed.ParentEntityId);
        }
        InvalidateContactInput();
    }

    private async Task FitContactAsync(CancellationToken token)
    {
        if (_model is not { } model || ContactParent?.Id is not { } parent || ContactComponent is not { } component) return;
        RouteStudioAction(RigStudioStage.HelpersAndHooks);
        // Navigation can replace only workflow metadata; use its current immutable model.
        model = _model!;
        var options = new ContactFootprintOptions { Up = ContactUp.Value, Forward = ContactForward.Value, BottomBandFraction = ContactBottomBand,
            Origin = ContactOrigin!.Mode, Axes = ContactAxes!.Mode };
        double? minimumWeight = ContactUseFootWeights ? ContactMinimumWeight : null;
        string pointText = ContactSourcePoints;
        long generation = ++_contactGeneration;
        IsBusy = true; NotifyStateChanged();
        try
        {
            IReadOnlySet<int>? points = string.IsNullOrWhiteSpace(pointText) ? null : ParseContactPoints(pointText);
            var snapshot = await Task.Run(() => FbxContactAuthoring.Inspect(model, parent, component.Id, minimumWeight, points, options, token), token);
            if (generation != _contactGeneration || !ReferenceEquals(model, _model)) return;
            _contactSnapshot = snapshot;
            _restoringContact = true;
            try { ContactOffset.Set(Vector3D.Zero); ContactRotation.Set(Vector3D.Zero); ContactBoundsCenter.Set(snapshot.Fit.BoundsCenter); ContactHalfExtents.Set(snapshot.Fit.BoundsHalfExtents); }
            finally { _restoringContact = false; }
            ContactStatus = $"{snapshot.ControlPointIds.Length} shoe points; {snapshot.Fit.SamplePointCount} bottom samples; {snapshot.Fit.Footprint.Length} footprint corners. " +
                (snapshot.Fit.OrientationAmbiguous ? "Direction is ambiguous: choose explicit directions and fit again. " : "Review axes, plane and bounds. ") +
                (snapshot.Fit.HasVolume ? "" : "Flat geometry: enter a positive thickness before applying. ") + "Native contact behavior remains unverified.";
        }
        catch (OperationCanceledException) { if (generation == _contactGeneration) ContactStatus = "Contact fit cancelled."; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException or FormatException)
        { if (generation == _contactGeneration) ContactStatus = "Contact fit was not applied: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyContacts(); ContactPreviewChanged?.Invoke(this, EventArgs.Empty); }
    }

    private static HashSet<int> ParseContactPoints(string text)
    {
        if (text.Length > 2_000_000) throw new ArgumentException("The source point selection is too large.", nameof(text));
        var ids = new HashSet<int>();
        foreach (var item in text.Split([',', ' ', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id < 0)
                throw new ArgumentException("Enter original nonnegative control-point numbers separated by commas, or leave empty for the whole selection.", nameof(text));
            ids.Add(id);
            if (ids.Count > 250_000) throw new ArgumentException("Too many source points selected.", nameof(text));
        }
        return ids;
    }

    public ContactPreview? GetContactPreview()
    {
        if (_contactSnapshot is not { } snapshot || !ContactOffset.Value.IsFinite || !ContactRotation.Value.IsFinite ||
            !ContactBoundsCenter.Value.IsFinite || !ContactHalfExtents.Value.IsFinite) return null;
        const double radians = Math.PI / 180;
        var rotation = TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitX, ContactRotation.X * radians)) *
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, ContactRotation.Y * radians)) *
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitZ, ContactRotation.Z * radians));
        return new(snapshot.Fit, snapshot.Fit.GlobalFrame * TransformMatrix.CreateTranslation(ContactOffset.Value) * rotation,
            ContactBoundsCenter.Value, ContactHalfExtents.Value);
    }

    private void ApplyContact()
    {
        if (!CanApplyContact || _contactSnapshot is not { } snapshot || _model is not { } model || GetContactPreview() is not { } preview) return;
        try
        {
            bool overridden = ContactOffset.Value != Vector3D.Zero || ContactRotation.Value != Vector3D.Zero || preview.Center != snapshot.Fit.BoundsCenter || preview.HalfExtents != snapshot.Fit.BoundsHalfExtents;
            var updated = FbxContactAuthoring.Apply(snapshot, model, ContactHelper?.Id, ContactName.Trim(), ContactRole.Trim(),
                snapshot.ParentGlobal.InvertedAffine() * preview.GlobalFrame, preview.Center, preview.HalfExtents, overridden);
            if (ReferenceEquals(updated, model)) { ContactStatus = "This contact placement is already saved."; return; }
            ContactModelApplyRequested?.Invoke(this, new(model, updated, "Saved reviewed contact frame and bounds; source weights, geometry and morphs were preserved."));
            ContactStatus = "Contact saved. Channel ownership and native contact behavior require separate review.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        { ContactStatus = "Contact was not saved: " + error.Message; }
    }

    private void RefreshContactMetadata(FbxModelAuthoringImportResult model)
    { if (_contactSnapshot is { } snapshot) _contactSnapshot = FbxContactAuthoring.RefreshMetadata(snapshot, model); NotifyContacts(); }
    private void NotifyContacts()
    {
        OnPropertyChanged(nameof(CanFitContact)); OnPropertyChanged(nameof(CanApplyContact)); OnPropertyChanged(nameof(HasContactPreview));
        FitContactCommand?.NotifyCanExecuteChanged(); ApplyContactCommand?.NotifyCanExecuteChanged(); ClearContactCommand?.NotifyCanExecuteChanged();
    }
}
