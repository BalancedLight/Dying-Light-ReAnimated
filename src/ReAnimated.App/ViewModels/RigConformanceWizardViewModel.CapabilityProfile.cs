using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class ProfileCapabilityChoice(string id, bool selected) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private bool _selected = selected;
}
public sealed record ProfileAssignmentChoice(string RoleId, Guid EntityId, string NodeName)
{
    public string Label => RoleId + " -> " + NodeName;
}

public sealed partial class RigConformanceWizardViewModel
{
    private Func<string?>? _capabilityProfilePicker;
    private RigCapabilityProfile? _capabilityProfileDraft;
    private CapabilityProfilePreview? _capabilityProfilePreview;
    private ImmutableArray<RigAssetRoleBinding> _profileOwners = [];
    private long _capabilityProfileGeneration;
    [ObservableProperty] private RigRuntimeRole? _capabilityRole;
    [ObservableProperty] private RigEntityBinding? _capabilityEntity;
    [ObservableProperty] private ProfileAssignmentChoice? _capabilityAssignment;
    [ObservableProperty] private string? _capabilityOwnerRole;
    [ObservableProperty] private CapabilityRoleReview? _capabilityReviewRow;
    [ObservableProperty] private bool _capabilityProfileReviewed;
    [ObservableProperty] private string _capabilityProfileStatus = "Load a capability profile to review required roles. A fitting template alone is not a capability profile.";
    public ObservableCollection<ProfileCapabilityChoice> ProfileCapabilities { get; } = [];
    public ObservableCollection<RigRuntimeRole> CapabilityRoles { get; } = [];
    public ObservableCollection<RigEntityBinding> CapabilityEntities { get; } = [];
    public ObservableCollection<ProfileAssignmentChoice> CapabilityAssignments { get; } = [];
    public ObservableCollection<string> CapabilityOwnerRoles { get; } = [];
    public ObservableCollection<CapabilityRoleReview> CapabilityReviewRows { get; } = [];
    public IAsyncRelayCommand LoadCapabilityProfileCommand { get; private set; } = null!;
    public IRelayCommand CreateObservedNodeProfileCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewCapabilityProfileCommand { get; private set; } = null!;
    public IRelayCommand ApplyCapabilityProfileCommand { get; private set; } = null!;
    public IRelayCommand AssignCapabilityRoleCommand { get; private set; } = null!;
    public IRelayCommand RemoveCapabilityAssignmentCommand { get; private set; } = null!;
    public IRelayCommand BindCapabilityOwnerCommand { get; private set; } = null!;
    public IRelayCommand ResetCapabilityProfileCommand { get; private set; } = null!;
    public IRelayCommand CancelCapabilityProfileCommand { get; private set; } = null!;
    public event EventHandler<BodyModelEventArgs>? CapabilityProfileApplyRequested;
    public bool CanPreviewCapabilityProfile => !IsBusy && HasStudioSession && _capabilityProfileDraft is not null && ProfileCapabilities.Any(c => c.Selected);
    public bool CanCreateObservedNodeProfile => !IsBusy && HasStudioSession && _template is { } template &&
        template.ProfileName.Equals(TemplateProfileName, StringComparison.OrdinalIgnoreCase);
    public bool CanApplyCapabilityProfile => !IsBusy && CapabilityProfileReviewed && _capabilityProfilePreview is not null;
    public string CapabilityProfileIdentity => _capabilityProfileDraft is { } profile
        ? $"{profile.Identity.Id} / {profile.Identity.Version} ({profile.FamilyId})"
        : "No profile definition loaded.";
    public string CapabilityProfileProvenance => _capabilityProfileDraft is { } profile
        ? $"Profile content: {profile.Identity.ContentSha256}\nDeclared build: {profile.Identity.BuildFingerprint ?? "unresolved"}. " +
            $"Imported source fingerprints: {string.Join(", ", profile.Roles.SelectMany(r => r.Evidence).Where(e => e.Kind == RigEvidenceKind.ImportedSource && e.ArtifactSha256 is not null).Select(e => e.ArtifactSha256!).Distinct(StringComparer.Ordinal))}. " +
            "Content integrity and imported node observations do not verify native rules."
        : "Load the exact profile revision to inspect its contents.";
    public string CapabilityOwnerSummary => _profileOwners.IsEmpty ? "No asset owners assigned." : string.Join("\n", _profileOwners.Select(o =>
        $"{o.RoleId}: {(o.AssetId == _model?.Package.Document.ModelId ? "this character" : "another saved asset")}"));
    public string CapabilityReviewSummary => _capabilityProfilePreview is { } preview
        ? $"Effective capabilities: {string.Join(", ", preview.Review.EffectiveCapabilities)}. {preview.Review.PreservedUnassignedNodes} unassigned nodes retained.\n" +
            string.Join("\n", preview.Review.Diagnostics.Where(d => d.RoleId is null).Select(d => d.Message + " " + d.CorrectiveOperation))
        : "Preview the current choices to see missing roles, conflicts and evidence gaps. Assignment checks do not certify compiled or native behavior.";
    internal void SetCapabilityProfilePicker(Func<string?> picker) => _capabilityProfilePicker = picker;

    private void InitializeCapabilityProfile()
    {
        LoadCapabilityProfileCommand = new AsyncRelayCommand(LoadCapabilityProfileAsync, () => !IsBusy && HasStudioSession);
        CreateObservedNodeProfileCommand = new RelayCommand(CreateObservedNodeProfile, () => CanCreateObservedNodeProfile);
        PreviewCapabilityProfileCommand = new AsyncRelayCommand(PreviewCapabilityProfileAsync, () => CanPreviewCapabilityProfile);
        ApplyCapabilityProfileCommand = new RelayCommand(ApplyCapabilityProfile, () => CanApplyCapabilityProfile);
        AssignCapabilityRoleCommand = new RelayCommand(() =>
        {
            if (CapabilityRole is not { } role || CapabilityEntity is not { } entity) return;
            if (CapabilityAssignments.Any(a => a.RoleId == role.Id && a.EntityId == entity.EntityId)) return;
            CapabilityAssignments.Add(new(role.Id, entity.EntityId, entity.NativeName)); InvalidateCapabilityProfilePreview();
        }, () => !IsBusy && CapabilityRole is not null && CapabilityEntity is not null);
        RemoveCapabilityAssignmentCommand = new RelayCommand(() =>
        {
            if (CapabilityAssignment is { } assignment) CapabilityAssignments.Remove(assignment);
            InvalidateCapabilityProfilePreview();
        }, () => !IsBusy && CapabilityAssignment is not null);
        BindCapabilityOwnerCommand = new RelayCommand(() =>
        {
            if (_model is null || CapabilityOwnerRole is not { } role) return;
            _profileOwners = _profileOwners.Where(o => o.RoleId != role).Append(new RigAssetRoleBinding(role, _model.Package.Document.ModelId)).ToImmutableArray();
            InvalidateCapabilityProfilePreview(); OnPropertyChanged(nameof(CapabilityOwnerSummary));
        }, () => !IsBusy && CapabilityOwnerRole is not null && HasStudioSession);
        ResetCapabilityProfileCommand = new RelayCommand(RestoreCapabilityProfile, () => !IsBusy);
        CancelCapabilityProfileCommand = new RelayCommand(InvalidateCapabilityProfilePreview);
    }

    internal void SetCapabilityProfileDraft(RigCapabilityProfile profile)
    {
        RigCapabilityProfileSerializer.Verify(profile);
        InvalidateCapabilityProfilePreview(); _capabilityProfileDraft = profile;
        _profilePermissionSourceHash = profile.Identity.ContentSha256;
        ProfileCapabilities.Clear(); CapabilityRoles.Clear(); CapabilityEntities.Clear(); CapabilityAssignments.Clear(); CapabilityOwnerRoles.Clear();
        var recipe = _model?.Package.Document.RiggingSession?.Recipe;
        foreach (var capability in profile.Capabilities)
        {
            var choice = new ProfileCapabilityChoice(capability.Id, recipe?.SelectedCapabilityIds.Contains(capability.Id, StringComparer.Ordinal) ?? false);
            choice.PropertyChanged += (_, _) => InvalidateCapabilityProfilePreview(); ProfileCapabilities.Add(choice);
        }
        foreach (var role in profile.Roles.OrderBy(r => r.Id, StringComparer.Ordinal)) CapabilityRoles.Add(role);
        foreach (string owner in profile.Roles.Select(r => r.OwnerAssetRoleId).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) CapabilityOwnerRoles.Add(owner);
        if (recipe is not null)
        {
            foreach (var entity in recipe.Entities) CapabilityEntities.Add(entity);
            foreach (var assignment in recipe.Assignments)
                CapabilityAssignments.Add(new(assignment.RoleId, assignment.EntityId, recipe.Entities.Single(e => e.EntityId == assignment.EntityId).NativeName));
        }
        _profileOwners = recipe?.AssetRoles ?? [];
        CapabilityRole = CapabilityRoles.FirstOrDefault(); CapabilityEntity = null; CapabilityOwnerRole = CapabilityOwnerRoles.FirstOrDefault();
        CapabilityAssignment = null; CapabilityReviewRow = null;
        CapabilityProfileStatus = "Profile loaded as a draft. Choose capabilities and review assignments; existing nodes and previous assignments are retained.";
        NotifyCapabilityProfile();
    }

    private void CreateObservedNodeProfile()
    {
        if (!CanCreateObservedNodeProfile || _template is not { } template) return;
        var profile = ObservedRigCapabilityProfileStarter.Create(template);
        SetCapabilityProfileDraft(profile);
        if (ProfileCapabilities.FirstOrDefault(c => c.Id == "observed-node-inventory") is { } inventory)
            inventory.Selected = true;
        CapabilityProfileStatus = "Created an incomplete observed-node inventory from the resolved template. Runtime requirements, aliases, consumers, channels, LOD rules and native readiness remain unknown.";
        NotifyCapabilityProfile();
    }

    private void RestoreCapabilityProfile()
    {
        InvalidateCapabilityProfilePreview();
        if (_model?.Package.Document.RiggingSession?.Recipe.ProfileSnapshot is { } profile) { SetCapabilityProfileDraft(profile); return; }
        _capabilityProfileDraft = null; _profileOwners = [];
        ProfileCapabilities.Clear(); CapabilityRoles.Clear(); CapabilityEntities.Clear(); CapabilityAssignments.Clear(); CapabilityOwnerRoles.Clear();
        CapabilityRole = null; CapabilityEntity = null; CapabilityAssignment = null; CapabilityOwnerRole = null;
        CapabilityProfileStatus = "Load a capability profile. Any existing profile reference and assignments are preserved until a reviewed change is applied.";
        NotifyCapabilityProfile();
    }

    private async Task LoadCapabilityProfileAsync(CancellationToken token)
    {
        string? path = _capabilityProfilePicker?.Invoke(); if (path is null) return;
        var model = _model; long generation = ++_capabilityProfileGeneration; IsBusy = true;
        try
        {
            if (new FileInfo(path).Length > RigCapabilityProfileSerializer.MaximumBytes) throw new InvalidDataException("Capability profiles must be at most 4 MiB.");
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            var profile = await Task.Run(() => RigCapabilityProfileSerializer.Deserialize(bytes), token);
            if (generation != _capabilityProfileGeneration || !ReferenceEquals(model, _model)) return;
            token.ThrowIfCancellationRequested(); SetCapabilityProfileDraft(profile);
        }
        catch (OperationCanceledException) { if (generation == _capabilityProfileGeneration) CapabilityProfileStatus = "Profile loading cancelled."; }
        catch (Exception error) when (ProfileReviewError(error)) { if (generation == _capabilityProfileGeneration) CapabilityProfileStatus = "Profile was not loaded: " + error.Message; }
        finally { IsBusy = false; NotifyCapabilityProfile(); }
    }

    private async Task PreviewCapabilityProfileAsync(CancellationToken token)
    {
        if (_model is not { } model || _capabilityProfileDraft is not { } profile) return;
        long generation = ++_capabilityProfileGeneration;
        var capabilities = ProfileCapabilities.Where(c => c.Selected).Select(c => c.Id).ToImmutableArray();
        var assignments = CapabilityAssignments.Select(a => new RigRoleAssignment(a.RoleId, a.EntityId)).ToImmutableArray();
        var owners = _profileOwners; IsBusy = true;
        try
        {
            var preview = await Task.Run(() => FbxCapabilityProfileAuthoring.Preview(model, profile, capabilities, assignments, owners, token), token);
            if (generation != _capabilityProfileGeneration || !ReferenceEquals(model, _model)) return;
            _capabilityProfilePreview = preview; CapabilityProfileReviewed = false;
            CapabilityReviewRows.Clear(); foreach (var row in preview.Review.Roles) CapabilityReviewRows.Add(row);
            CapabilityReviewRow = CapabilityReviewRows.FirstOrDefault(r => r.Status == "Needs repair") ?? CapabilityReviewRows.FirstOrDefault();
            CapabilityProfileStatus = "Review the role report before saving. Unresolved choices can be saved for later repair; this does not record a validation pass.";
        }
        catch (OperationCanceledException) { if (generation == _capabilityProfileGeneration) CapabilityProfileStatus = "Profile review cancelled."; }
        catch (Exception error) when (ProfileReviewError(error))
        { if (generation == _capabilityProfileGeneration) { _capabilityProfilePreview = null; CapabilityProfileStatus = "Profile review rejected: " + error.Message; } }
        finally { IsBusy = false; NotifyCapabilityProfile(); }
    }

    private void ApplyCapabilityProfile()
    {
        if (!CanApplyCapabilityProfile || _model is not { } model || _capabilityProfilePreview is not { } preview) return;
        if (!FbxCapabilityProfileAuthoring.TryApply(model, preview, out var candidate))
        { InvalidateCapabilityProfilePreview(); CapabilityProfileStatus = "The source changed. Preview the current choices again."; return; }
        if (!RequestBodyChange(CapabilityProfileApplyRequested, new(model, candidate, "Saved reviewed capability profile and role assignments."))) return;
        InvalidateCapabilityProfilePreview(); CapabilityProfileStatus = "Profile definition and assignments saved with the model. Required repairs and native acceptance remain separate.";
    }

    private void RefreshCapabilityProfileMetadata(FbxModelAuthoringImportResult current)
    {
        if (_capabilityProfilePreview is { } preview)
        {
            _capabilityProfilePreview = FbxCapabilityProfileAuthoring.RefreshMetadata(preview, current);
            if (_capabilityProfilePreview is null) InvalidateCapabilityProfilePreview();
        }
        NotifyCapabilityProfile();
    }

    private void InvalidateCapabilityProfilePreview()
    {
        LoadCapabilityProfileCommand?.Cancel(); PreviewCapabilityProfileCommand?.Cancel();
        _capabilityProfileGeneration++; _capabilityProfilePreview = null; CapabilityProfileReviewed = false;
        CapabilityReviewRows.Clear(); CapabilityReviewRow = null; NotifyCapabilityProfile();
    }
    partial void OnCapabilityRoleChanged(RigRuntimeRole? value) { RestoreProfilePermissions(value); NotifyCapabilityProfile(); }
    partial void OnCapabilityEntityChanged(RigEntityBinding? value) => NotifyCapabilityProfile();
    partial void OnCapabilityAssignmentChanged(ProfileAssignmentChoice? value) => NotifyCapabilityProfile();
    partial void OnCapabilityOwnerRoleChanged(string? value) => NotifyCapabilityProfile();
    partial void OnCapabilityReviewRowChanged(CapabilityRoleReview? value)
    {
        if (value is null) return;
        CapabilityRole = CapabilityRoles.FirstOrDefault(r => r.Id == value.Id);
        CapabilityEntity = value.EntityIds.Length == 1 ? CapabilityEntities.FirstOrDefault(e => e.EntityId == value.EntityIds[0]) : null;
        CapabilityAssignment = CapabilityAssignments.FirstOrDefault(a => a.RoleId == value.Id);
    }
    partial void OnCapabilityProfileReviewedChanged(bool value) => NotifyCapabilityProfile();
    private static bool ProfileReviewError(Exception error) => RestPoseError(error) || error is IOException or UnauthorizedAccessException or JsonException;
    private void NotifyCapabilityProfile()
    {
        OnPropertyChanged(nameof(CanEditCapabilityPermissions));
        OnPropertyChanged(nameof(CanPreviewCapabilityProfile)); OnPropertyChanged(nameof(CanApplyCapabilityProfile));
        OnPropertyChanged(nameof(CanCreateObservedNodeProfile));
        OnPropertyChanged(nameof(CapabilityProfileIdentity)); OnPropertyChanged(nameof(CapabilityProfileProvenance));
        OnPropertyChanged(nameof(CapabilityOwnerSummary)); OnPropertyChanged(nameof(CapabilityReviewSummary));
        LoadCapabilityProfileCommand?.NotifyCanExecuteChanged(); CreateObservedNodeProfileCommand?.NotifyCanExecuteChanged();
        PreviewCapabilityProfileCommand?.NotifyCanExecuteChanged(); ApplyCapabilityProfileCommand?.NotifyCanExecuteChanged();
        AssignCapabilityRoleCommand?.NotifyCanExecuteChanged(); RemoveCapabilityAssignmentCommand?.NotifyCanExecuteChanged(); BindCapabilityOwnerCommand?.NotifyCanExecuteChanged(); ResetCapabilityProfileCommand?.NotifyCanExecuteChanged();
    }
}
