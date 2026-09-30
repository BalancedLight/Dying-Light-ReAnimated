using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    private bool _restoringProfilePermissions;
    private string? _profilePermissionSourceHash;
    [ObservableProperty] private bool _enforceRoleEditPermissions;
    [ObservableProperty] private bool _permitRoleName;
    [ObservableProperty] private bool _permitRoleParent;
    [ObservableProperty] private bool _permitRolePosition;
    [ObservableProperty] private bool _permitRoleOrientation;
    [ObservableProperty] private bool _permitRoleExtents;
    [ObservableProperty] private bool _permitRoleChannels;
    [ObservableProperty] private bool _permitRoleRemoval;
    public bool CanEditCapabilityPermissions => !IsBusy && _capabilityProfileDraft is not null && CapabilityRole is not null;

    private void RestoreProfilePermissions(RigRuntimeRole? role)
    {
        if (_restoringProfilePermissions) return;
        _restoringProfilePermissions = true;
        try
        {
            var fields = role?.AllowedEdits ?? RigHelperEditFields.None;
            EnforceRoleEditPermissions = role?.ValidationRules?.Edits is not null;
            PermitRoleName = fields.HasFlag(RigHelperEditFields.Name); PermitRoleParent = fields.HasFlag(RigHelperEditFields.Parent);
            PermitRolePosition = fields.HasFlag(RigHelperEditFields.Position); PermitRoleOrientation = fields.HasFlag(RigHelperEditFields.Orientation);
            PermitRoleExtents = fields.HasFlag(RigHelperEditFields.Extents); PermitRoleChannels = fields.HasFlag(RigHelperEditFields.Channels);
            PermitRoleRemoval = role?.ValidationRules?.Edits?.AllowRemoval ?? false;
        }
        finally { _restoringProfilePermissions = false; }
        OnPropertyChanged(nameof(CanEditCapabilityPermissions));
    }

    private void UpdateProfilePermissions()
    {
        if (_restoringProfilePermissions || !CanEditCapabilityPermissions || CapabilityRole is not { } role || _capabilityProfileDraft is not { } profile) return;
        var fields = (PermitRoleName ? RigHelperEditFields.Name : 0) | (PermitRoleParent ? RigHelperEditFields.Parent : 0) |
            (PermitRolePosition ? RigHelperEditFields.Position : 0) | (PermitRoleOrientation ? RigHelperEditFields.Orientation : 0) |
            (PermitRoleExtents ? RigHelperEditFields.Extents : 0) | (PermitRoleChannels ? RigHelperEditFields.Channels : 0);
        var edits = EnforceRoleEditPermissions ? new RigRoleEditCheck { AllowRemoval = PermitRoleRemoval } : null;
        if (fields == role.AllowedEdits && edits == role.ValidationRules?.Edits) return;
        const string evidenceId = "authoring-permission-override";
        var replacement = role with
        {
            AllowedEdits = fields,
            ValidationRules = (role.ValidationRules ?? new()) with { Edits = edits },
            Evidence = role.Evidence.Where(e => e.Id != evidenceId).Append(new RigEvidenceReference
            {
                Id = evidenceId, Kind = RigEvidenceKind.UserOverride, ArtifactSha256 = _profilePermissionSourceHash ?? profile.Identity.ContentSha256,
                Description = "Local authoring permission draft. Native frame, component, retention and evidence requirements were not relaxed.",
            }).ToImmutableArray(),
        };
        _capabilityProfileDraft = RigCapabilityProfileSerializer.Seal(profile with
            { Roles = profile.Roles.Select(r => r.Id == role.Id ? replacement : r).ToImmutableArray() });
        _restoringProfilePermissions = true;
        try
        {
            int index = CapabilityRoles.IndexOf(role);
            if (index >= 0) CapabilityRoles[index] = replacement;
            CapabilityRole = replacement;
        }
        finally { _restoringProfilePermissions = false; }
        InvalidateCapabilityProfilePreview();
        CapabilityProfileStatus = "Local permission draft changed. Preview and review it before saving; the character and native role checks are unchanged.";
        NotifyCapabilityProfile();
    }
    partial void OnEnforceRoleEditPermissionsChanged(bool value) => UpdateProfilePermissions();
    partial void OnPermitRoleNameChanged(bool value) => UpdateProfilePermissions();
    partial void OnPermitRoleParentChanged(bool value) => UpdateProfilePermissions();
    partial void OnPermitRolePositionChanged(bool value) => UpdateProfilePermissions();
    partial void OnPermitRoleOrientationChanged(bool value) => UpdateProfilePermissions();
    partial void OnPermitRoleExtentsChanged(bool value) => UpdateProfilePermissions();
    partial void OnPermitRoleChannelsChanged(bool value) => UpdateProfilePermissions();
    partial void OnPermitRoleRemovalChanged(bool value) => UpdateProfilePermissions();
}
