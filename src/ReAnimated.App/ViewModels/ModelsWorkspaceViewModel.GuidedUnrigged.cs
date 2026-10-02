using System.Collections.ObjectModel;
using System.Globalization;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.App.ViewModels;

/// <summary>A readable, reviewable projection of one body-detection proposal.</summary>
public sealed record GuidedBodyProposalChoice(
    AnatomicalJointProposal Proposal,
    string Label,
    string PositionLabel)
{
    public string DisplayLabel => Label;
}

public sealed partial class ModelsWorkspaceViewModel
{
    public ObservableCollection<GuidedBodyProposalChoice> GuidedBodyProposals { get; } = [];

    public bool IsGuidedUnriggedPreparation => GuidedUsesDl1Rig && Conformance.HasUnriggedSource;

    public string GuidedBodyDetectionStatus => Conformance.BodyDetectionStatus;

    public bool GuidedDraftBodyGuidesAreUnapproved => _model?.Package.Document.RiggingSession is { } session &&
        session.Landmarks.Where(static guide => AnatomicalDetectionAdoption.OwnsRole(guide.RoleId)).Any() &&
        session.Landmarks.Where(static guide => AnatomicalDetectionAdoption.OwnsRole(guide.RoleId)).All(static guide => !guide.UserApproved);

    private GuidedBodyProposalChoice? _selectedGuidedBodyProposal;
    public GuidedBodyProposalChoice? SelectedGuidedBodyProposal
    {
        get => _selectedGuidedBodyProposal;
        set
        {
            if (!SetProperty(ref _selectedGuidedBodyProposal, value)) return;
            Conformance.SelectedBodyProposal = value?.Proposal;
        }
    }

    private void InitializeGuidedUnrigged()
    {
        Conformance.BodyDetectionChanged += (_, _) => RefreshGuidedBodyProposals();
        Conformance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RigConformanceWizardViewModel.BodyDetectionStatus))
                OnPropertyChanged(nameof(GuidedBodyDetectionStatus));
            if (args.PropertyName is nameof(RigConformanceWizardViewModel.HasSavedBodyGuide) or
                nameof(RigConformanceWizardViewModel.SelectedBodyProposal))
                OnPropertyChanged(nameof(GuidedDraftBodyGuidesAreUnapproved));
            if (args.PropertyName is nameof(RigConformanceWizardViewModel.HasUnriggedSource) or
                nameof(RigConformanceWizardViewModel.HasGeneratedBodyRig))
                OnPropertyChanged(nameof(IsGuidedUnriggedPreparation));
        };
        RefreshGuidedBodyProposals();
    }

    private void RefreshGuidedBodyProposals()
    {
        GuidedBodyProposals.Clear();
        foreach (var proposal in Conformance.BodyProposals)
        {
            GuidedBodyProposals.Add(new(proposal, FriendlyBodyRole(proposal.Role), FormatBodyPosition(proposal.Position)));
        }

        SelectedGuidedBodyProposal = GuidedBodyProposals.FirstOrDefault(choice =>
            choice.Proposal == Conformance.SelectedBodyProposal) ?? GuidedBodyProposals.FirstOrDefault();
        OnPropertyChanged(nameof(IsGuidedUnriggedPreparation));
        OnPropertyChanged(nameof(GuidedBodyDetectionStatus));
        OnPropertyChanged(nameof(GuidedDraftBodyGuidesAreUnapproved));
    }

    private async Task PrepareGuidedUnriggedModelAsync()
    {
        if (Conformance.StartAutoRigStudioCommand.CanExecute(null))
            Conformance.StartAutoRigStudioCommand.Execute(null);

        if (!Conformance.HasUnriggedSource || !Conformance.HasStudioSession)
        {
            GuidedStatus = "The unrigged model could not enter body preparation. Review its geometry and try again.";
            return;
        }

        if (Conformance.HasUnsavedBodyComponents && Conformance.SaveBodyComponentsCommand.CanExecute(null))
            Conformance.SaveBodyComponentsCommand.Execute(null);

        if (Conformance.BodyProposals.Count == 0 && Conformance.DetectBodyCommand.CanExecute(null))
        {
            GuidedStatus = "Finding reviewable body guides from the selected geometry…";
            await Conformance.DetectBodyCommand.ExecuteAsync(null);
            if (Conformance.BodyProposals.Count > 0 && Conformance.UseBodyGuidesCommand.CanExecute(null))
                Conformance.UseBodyGuidesCommand.Execute(null);
        }

        RefreshGuidedBodyProposals();
        ActivateGuidedStep();
        GuidedStatus = Conformance.HasSavedBodyGuide
            ? "Review the draft body guides in the list and viewport. They remain unapproved until you apply the setup."
            : Conformance.BodyDetectionStatus;
    }

    private async Task<bool> BuildGuidedUnriggedRigAsync()
    {
        if (!Conformance.HasSavedBodyGuide)
        {
            GuidedStatus = "Review and save body guides before building the draft rig.";
            return false;
        }

        if (Conformance.BuildBodyRigCommand.CanExecute(null))
        {
            GuidedStatus = "Building the draft body rig from the reviewed guides…";
            await Conformance.BuildBodyRigCommand.ExecuteAsync(null);
        }

        if (!Conformance.HasGeneratedBodyRig)
        {
            GuidedStatus = Conformance.BodyAuthoringStatus;
            return false;
        }

        FbxModelAuthoringImportResult? beforeBind = _model;
        long revisionBeforeBind = PersistenceRevision;
        if (Conformance.BindBodyGeometryCommand.CanExecute(null))
        {
            GuidedStatus = "Binding the selected geometry to the draft rig…";
            await Conformance.BindBodyGeometryCommand.ExecuteAsync(null);
        }

        bool bindingCommitted = PersistenceRevision > revisionBeforeBind &&
            !ReferenceEquals(_model, beforeBind) &&
            _model?.Package.Document.RiggingSession?.BindingBackend is not null;
        if (!Conformance.HasRiggedStudioSource || !Conformance.HasGeneratedBodyRig || !bindingCommitted)
        {
            GuidedStatus = string.IsNullOrWhiteSpace(Conformance.BodyAuthoringStatus)
                ? "Geometry binding did not produce a verified assignment. Review the source geometry and try again."
                : Conformance.BodyAuthoringStatus;
            return false;
        }

        await PrepareGuidedAdjustmentAsync();
        if (!Conformance.CanApplyGuidedFit)
        {
            GuidedStatus = Conformance.GuidedFitBlockReason;
            return false;
        }

        return true;
    }

    private static string FriendlyBodyRole(string role) => role switch
    {
        "body.pelvis" => "Pelvis",
        "body.spine.0" => "Lower spine",
        "body.spine.1" => "Mid spine",
        "body.spine.2" => "Upper spine",
        "body.neck.0" => "Neck",
        "body.head" => "Head",
        "arm.left.upper" => "Left upper arm",
        "arm.left.lower" => "Left forearm",
        "hand.left" => "Left hand",
        "arm.right.upper" => "Right upper arm",
        "arm.right.lower" => "Right forearm",
        "hand.right" => "Right hand",
        "leg.left.upper" => "Left thigh",
        "leg.left.lower" => "Left lower leg",
        "foot.left" => "Left foot",
        "leg.right.upper" => "Right thigh",
        "leg.right.lower" => "Right lower leg",
        "foot.right" => "Right foot",
        _ => role.Replace('.', ' '),
    };

    private static string FormatBodyPosition(ReAnimated.Core.Mathematics.Vector3D position) =>
        string.Create(CultureInfo.InvariantCulture, $"X {position.X:0.##}, Y {position.Y:0.##}, Z {position.Z:0.##}");
}
