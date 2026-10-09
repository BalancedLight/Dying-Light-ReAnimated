using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.App.ViewModels;

/// <summary>Outcome of preparing the normal guided fit workflow.</summary>
public sealed record GuidedFitPreparationResult(
    bool CanApply,
    string Reason,
    ImmutableArray<string> RecommendedActions);

public sealed partial class RigConformanceWizardViewModel
{
    private RigConformanceLandmarkViewModel? _selectedFittedFingerJoint;

    /// <summary>Current fitted finger joints available in the Adjust Hands stage.</summary>
    public ObservableCollection<RigConformanceLandmarkViewModel> FittedFingerJoints { get; } = [];

    /// <summary>Finger joints filtered to the hand currently selected for review.</summary>
    public IReadOnlyList<RigConformanceLandmarkViewModel> SelectedHandFingerJoints =>
        FittedFingerJoints.Where(joint =>
        {
            string prefix = $"finger.{HandSide.ToString().ToLowerInvariant()}.";
            return ReAnimated.Retargeting.Mapping.HumanoidBoneSemanticClassifier
                .Classify(joint.BoneName)?.Role.StartsWith(prefix, StringComparison.Ordinal) == true;
        }).ToArray();

    /// <summary>The fitted finger joint currently selected for viewport placement.</summary>
    public RigConformanceLandmarkViewModel? SelectedFittedFingerJoint
    {
        get => _selectedFittedFingerJoint;
        set
        {
            if (!SetProperty(ref _selectedFittedFingerJoint, value)) return;
            if (value is not null) SelectedLandmark = null;
            OnPropertyChanged(nameof(SelectedJointBoneName));
            OnPropertyChanged(nameof(SelectedBoneIndex));
            ResetBoneCommand.NotifyCanExecuteChanged();
            FitChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The selected body or finger joint's fitted name.</summary>
    public string? SelectedJointBoneName =>
        SelectedLandmark?.BoneName ?? SelectedFittedFingerJoint?.BoneName;

    /// <summary>
    /// Prepares a rigged model for the normal guided fit flow. Resolves the
    /// selected DL1 target when needed, runs automatic matching, and reports
    /// concrete next actions when the fit cannot proceed.
    /// </summary>
    public async Task<GuidedFitPreparationResult> PrepareGuidedFitAsync(
        bool preserveSourceProportions = true,
        CancellationToken cancellationToken = default)
    {
        if (_model is null)
        {
            SolveStatus = "Import a rigged model before preparing a DL1 fit.";
            NotifyStateChanged();
            return new(false, SolveStatus, ["Import a model that contains a skeleton."]);
        }

        if (_model.Rig is null)
        {
            SolveStatus = "This model has no source skeleton. Build or detect a body rig before mapping it to DL1.";
            NotifyStateChanged();
            return new(false, SolveStatus, ["Build a body rig from the model geometry, then prepare the DL1 fit."]);
        }

        if (_template is null)
        {
            IsBusy = true;
            try
            {
                Dl1RigTemplateResolution resolution = await _resolveTemplate(
                    TemplateProfileName,
                    cancellationToken).ConfigureAwait(true);
                UseTemplateResolution(resolution);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(false, "Preparing the fit was cancelled.", ["Run Prepare again when ready."]);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
            {
                TemplateStatus = $"The DL1 target could not be loaded: {exception.Message}";
                SolveStatus = TemplateStatus;
                NotifyStateChanged();
                return new(false, SolveStatus, ["Check the selected DL1 target and its indexed source, then prepare again."]);
            }
            finally
            {
                IsBusy = false;
            }
        }

        if (_template is null)
        {
            SolveStatus = string.IsNullOrWhiteSpace(TemplateStatus)
                ? "No DL1 target skeleton is available."
                : TemplateStatus;
            return new(false, SolveStatus, ["Select or index a valid DL1 target skeleton, then prepare again."]);
        }

        if (!_editingAppliedOutputRig && _model.Package.Document.RigConformance is null)
        {
            UseGeometryCorrespondence = true;
            ScaleMode = CustomModelConformanceScaleMode.Automatic;
            ConformanceStrength = preserveSourceProportions ? 0.0 : 1.0;
        }

        if (_sourceConformanceReviewRequired)
        {
            _roleOverrides = ImmutableDictionary<string, string>.Empty;
            _positionOverrides = ImmutableDictionary<string, Vector3D>.Empty;
            _previousPositionOverrides = null;
            _sourceConformanceReviewRequired = false;
            _geometryEvidence = null;
            _geometryEvidenceCaptured = false;
        }

        // GeneratedBodyRig validates stable typed role assignments against its
        // own hierarchy. Use only roles present in the selected target, and fill
        // only absent overrides so prior author choices remain authoritative.
        SeedGeneratedBodyRoleOverrides();
        await StartSolveAsync(debounce: false, cancellationToken).ConfigureAwait(true);

        // Automatic geometry matches are the normal path. Keep their proposed
        // selections as the default mapping so ambiguity review does not turn
        // the primary import flow into a dead end; advanced mapping remains
        // editable and undoable.
        if (HasPendingMappingReview)
        {
            AcceptMappingProposals();
            await StartSolveAsync(debounce: false, cancellationToken).ConfigureAwait(true);
        }

        if (Fit is null)
        {
            return new(false, SolveStatus, RecommendRepairActions());
        }

        Stage = RigConformanceStage.Refine;
        SelectedLandmark = Landmarks.FirstOrDefault(static landmark => landmark.IsResolved)
            ?? Landmarks.FirstOrDefault();
        bool ready = CanApply && MissingCoreRoles.IsEmpty;
        return new(
            ready,
            ready
                ? "The DL1 fit is ready. Adjust visible joints if needed, then apply it to the model."
                : GuidedFitBlockReason,
            ready
                ? ["Place any joints that need adjustment, then apply the fit."]
                : RecommendRepairActions());
    }

    /// <summary>Whether the primary guided flow can place joints on a solved rig.</summary>
    public bool CanPlaceGuidedBodyJoints =>
        !IsBusy && IsFitCurrent && _model?.Rig is not null && !RequiresSourceRematch;

    /// <summary>Whether an existing hand setup is ready for the optional hand review.</summary>
    public bool CanReviewGuidedHands => !IsBusy && HasHandSetup && CanReviewHandSetup;

    /// <summary>Whether a conformed skeleton can be shown in the fit viewport.</summary>
    public bool CanPreviewGuidedFit => IsFitCurrent && _template is not null && !IsBusy;

    /// <summary>Apply gate exposed to the simplified guided screen.</summary>
    public bool CanApplyGuidedFit => CanApply && MissingCoreRoles.IsEmpty && !IsBusy;

    /// <summary>Reviewable source choices for unresolved normal-flow body targets.</summary>
    public IReadOnlyList<RigConformanceMappingItemViewModel> GuidedMissingMappings =>
        Mappings.Where(row => row.Row.TemplateIndex >= 0 &&
                row.Row.Disposition != RigBoneDisposition.Mapped &&
                row.Role is { } role && MissingCoreRoles.Contains(role, StringComparer.Ordinal))
            .ToArray();

    /// <summary>Assign a source bone to one currently missing guided target.</summary>
    public bool SetGuidedRoleSource(string role, string sourceBoneName)
    {
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(sourceBoneName))
        {
            return false;
        }

        RigConformanceMappingItemViewModel? row = GuidedMissingMappings
            .FirstOrDefault(candidate => string.Equals(candidate.Role, role, StringComparison.Ordinal));
        if (row is null || !row.CanChooseSource || !row.Candidates.Contains(sourceBoneName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        row.SelectedSourceName = sourceBoneName;
        return string.Equals(
            Correspondence?.Rows.FirstOrDefault(candidate => candidate.Role == role)?.SourceName,
            sourceBoneName,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Action text for the primary guided fit step.</summary>
    public string GuidedFitPrimaryAction => Fit is null
        ? "Prepare fit"
        : CanApplyGuidedFit
            ? "Apply fit"
            : "Resolve fit issues";

    /// <summary>Human-readable blocker for the primary guided fit step.</summary>
    public string GuidedFitBlockReason => Fit is null
        ? SolveStatus
        : !IsFitCurrent
            ? "The fit is updating. Wait for the current result before applying."
        : !MissingCoreRoles.IsEmpty
            ? $"Choose source joints for these targets: {string.Join(", ", MissingCoreRoles)}."
            : !CanApplyGuidedFit
                ? !string.IsNullOrWhiteSpace(MappingReviewMessage)
                    ? MappingReviewMessage
                    : "Finish the current fit operation before applying."
                : string.Empty;

    private bool SeedGeneratedBodyRoleOverrides()
    {
        if (_editingAppliedOutputRig || _model is not { Rig: { } source } model ||
            _template is not { } template || !GeneratedBodyRig.IsGenerated(model.Package.Document) ||
            model.Package.Document.RiggingSession is not { } session)
        {
            return false;
        }

        var targetRoles = template.Entities
            .Select(entity => entity.SemanticRole ??
                ReAnimated.Retargeting.Mapping.HumanoidBoneSemanticClassifier.Classify(entity.Name)?.Role)
            .Where(static role => role is not null)
            .ToHashSet(StringComparer.Ordinal);
        var entities = session.Recipe.Entities
            .Where(entity => entity.OwnerAssetId == model.Package.Document.ModelId &&
                entity.SourceEntityId?.StartsWith("generated-body:", StringComparison.Ordinal) == true)
            .ToDictionary(static entity => entity.EntityId);

        bool changed = false;
        foreach (RigRoleAssignment assignment in session.Recipe.Assignments)
        {
            string targetRole = assignment.RoleId == "root_motion" ? "body.root" : assignment.RoleId;
            if (!targetRoles.Contains(targetRole) || _roleOverrides.ContainsKey(targetRole) ||
                !entities.TryGetValue(assignment.EntityId, out RigEntityBinding? entity) ||
                entity.SourceEntityId != "generated-body:" + assignment.RoleId ||
                source.GetBoneIndex(entity.NativeName) < 0)
            {
                continue;
            }

            _roleOverrides = _roleOverrides.SetItem(targetRole, entity.NativeName);
            changed = true;
        }

        return changed;
    }
    private void RefreshFittedFingerJoints()
    {
        string? selectedName = SelectedFittedFingerJoint?.BoneName;
        var resolved = Fit?.Bones
            .Where(static bone => bone.IsDeform &&
                ReAnimated.Retargeting.Mapping.HumanoidBoneSemanticClassifier
                    .Classify(bone.Name)?.Role.StartsWith("finger.", StringComparison.Ordinal) == true)
            .Select(bone =>
            {
                string[] role = ReAnimated.Retargeting.Mapping.HumanoidBoneSemanticClassifier
                    .Classify(bone.Name)!.Role.Split('.');
                string side = role.Length > 1 ? role[1] : "";
                string digit = role.Length > 2 ? role[2] : "finger";
                string segment = role.Length > 3 ? role[3] : "";
                string label = string.Join(" ", new[] { side, digit, segment }.Where(static part => !string.IsNullOrWhiteSpace(part)));
                var joint = new RigConformanceLandmarkViewModel(
                    bone.Name,
                    label,
                    "Place this fitted finger joint at its anatomical joint centre.",
                    null);
                joint.Update(bone, HasOverride(bone.Name));
                return joint;
            })
            .ToArray() ?? [];

        FittedFingerJoints.Clear();
        foreach (RigConformanceLandmarkViewModel joint in resolved)
            FittedFingerJoints.Add(joint);
        SelectedFittedFingerJoint = FittedFingerJoints.FirstOrDefault(joint =>
            string.Equals(joint.BoneName, selectedName, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedHandFingerJoints));
        OnPropertyChanged(nameof(SelectedJointBoneName));
        OnPropertyChanged(nameof(SelectedBoneIndex));
    }

    private ImmutableArray<string> RecommendRepairActions()
    {
        if (_model?.Rig is null)
        {
            return ["Build or detect a body skeleton before fitting."];
        }

        if (_template is null)
        {
            return ["Select or index a valid DL1 target skeleton, then prepare again."];
        }

        if (!MissingCoreRoles.IsEmpty)
        {
            return [$"Select source joints for the remaining body targets: {string.Join(", ", MissingCoreRoles)}."];
        }

        if (HasMissingAnatomicalCorrespondence)
        {
            return [$"Choose a source joint for each highlighted target: {MappingReviewMessage}"];
        }

        if (!string.IsNullOrWhiteSpace(SolveStatus))
        {
            return [$"Review the fit details: {SolveStatus}"];
        }

        return ["Review the highlighted mapping or scale issue, then prepare the fit again."];
    }
}
