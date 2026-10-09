using CommunityToolkit.Mvvm.Input;
using ReAnimated.Core.Domain;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.App.ViewModels;

public enum GuidedModelSetupStep { Import, Adjust, Preview, Export }

/// <summary>The normal model workflow. Specialist editors remain in Advanced.</summary>
public sealed partial class ModelsWorkspaceViewModel
{
    private GuidedModelSetupStep _guidedStep;
    private Guid? _guidedModelId;
    private bool _guidedUsesDl1Rig = true;
    private bool _guidedWorking;
    private bool _guidedShowJoints;
    private string _guidedStatus = "Choose an FBX, then choose its skeleton.";
    private Func<Task<bool>>? _guidedPreviewHandler;
    private string? _guidedPreviewFailureMessage;
    private Func<string, IEnumerable<AnimationClip>, Task<bool>>? _importedAnimationWorkspaceOffer;
    private readonly HashSet<string> _offeredAnimationSources = new(StringComparer.Ordinal);
    private AsyncRelayCommand? _guidedPrimaryCommand;
    private AsyncRelayCommand? _guidedBackCommand;
    private AsyncRelayCommand? _guidedChooseSkeletonCommand;

    public IAsyncRelayCommand GuidedPrimaryCommand => _guidedPrimaryCommand ??= new AsyncRelayCommand(
        RunGuidedPrimaryAsync, () => !IsBusy && !_guidedWorking && !Conformance.IsBusy);
    public IAsyncRelayCommand GuidedBackCommand => _guidedBackCommand ??= new AsyncRelayCommand(
        ReturnToGuidedStepAsync,
        () => GuidedStep > GuidedModelSetupStep.Adjust && !IsGuidedBusy);
    public IAsyncRelayCommand GuidedChooseSkeletonCommand => _guidedChooseSkeletonCommand ??= new AsyncRelayCommand(async () =>
    {
        CustomModelSkeletonChoice choice = _fileDialogs.SelectCustomModelSkeleton();
        if (choice != CustomModelSkeletonChoice.Cancel) await BeginGuidedSetupAsync(choice);
    }, () => HasModel && !IsGuidedBusy);

    public GuidedModelSetupStep GuidedStep
    {
        get => _guidedStep;
        private set { if (SetProperty(ref _guidedStep, value)) NotifyGuidedState(); }
    }
    public bool GuidedUsesDl1Rig => _guidedUsesDl1Rig;
    public bool GuidedPreviewAvailable => _guidedPreviewHandler is not null;
    public bool IsGuidedImport => GuidedStep == GuidedModelSetupStep.Import;
    public bool IsGuidedAdjust => GuidedStep == GuidedModelSetupStep.Adjust;
    public bool ShowGuidedFitReview => IsGuidedAdjust && GuidedUsesDl1Rig;
    public bool IsGuidedPreview => GuidedStep == GuidedModelSetupStep.Preview;
    public bool IsGuidedExport => GuidedStep == GuidedModelSetupStep.Export;
    public bool IsGuidedBusy => _guidedWorking || IsBusy || Conformance.IsBusy;
    public bool GuidedShowJoints
    {
        get => _guidedShowJoints;
        set { if (SetProperty(ref _guidedShowJoints, value)) { ActivateGuidedStep(); NotifyGuidedState(); } }
    }
    public bool ShowGuidedJointControls => IsGuidedAdjust && GuidedShowJoints && GuidedUsesDl1Rig && !IsGuidedUnriggedPreparation;
    public string GuidedStatus { get => _guidedStatus; private set => SetProperty(ref _guidedStatus, value); }
    public string GuidedStepTitle => GuidedStep switch
    {
        GuidedModelSetupStep.Import => "1  Import your model",
        GuidedModelSetupStep.Adjust => "2  Adjust",
        GuidedModelSetupStep.Preview => "3  Preview movement",
        _ => "4  Export",
    };
    public string GuidedPrimaryLabel => GuidedStep switch
    {
        GuidedModelSetupStep.Import => "Import FBX…",
        GuidedModelSetupStep.Adjust => IsGuidedUnriggedPreparation ? "Build rig and continue"
            : GuidedUsesDl1Rig ? "Apply and continue" : "Continue to preview",
        GuidedModelSetupStep.Preview => GuidedPreviewPrimaryLabel,
        _ => "Build model package…",
    };

    private string GuidedPreviewPrimaryLabel
    {
        get
        {
            if (!_guidedUsesDl1Rig && HasDecodedSourceAnimation)
                return "Preview animation";
            if (!_guidedUsesDl1Rig && !HasHumanoidStockPreviewRig)
                return "Preview movement";

            MainWindowViewModel? review = GuidedPreviewReviewContext;
            if (review?.HasGuidedPreviewReviewDraft != true)
            {
                return "Play a Dying Light animation";
            }

            if (review.HasGuidedExtraBoneExceptions) return "Review motion options";
            return review.RequiredTargetBindReviews.Any(static row => !row.IsReviewed)
                ? "Keep extra bones and play"
                : "Use reviewed choices and play";
        }
    }
    public string GuidedRigSummary => !HasModel ? string.Empty : IsGuidedUnriggedPreparation
        ? "Body guides · Dying Light skeleton will be built"
        : GuidedUsesDl1Rig
        ? "Dying Light skeleton · original body proportions preserved"
        : "Original rig";
    public string GuidedFeaturesSummary => _model is not { } model ? string.Empty
        : $"{model.Package.Document.MorphChannels.Length} morph controls detected" +
          (model.Package.Document.SecondaryMotion.NativeSources.IsEmpty
              ? string.Empty : $" · {model.Package.Document.SecondaryMotion.NativeSources.Length} native physics resources retained");

    public void SetGuidedPreviewHandler(Func<Task<bool>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _guidedPreviewHandler = handler;
        NotifyGuidedState();
    }

    public string? GuidedPreviewFailureMessage =>
        _guidedPreviewFailureMessage;

    public void SetGuidedPreviewFailure(string? message)
    {
        _guidedPreviewFailureMessage =
            string.IsNullOrWhiteSpace(message)
                ? null
                : message.Trim();
        if (GuidedStep == GuidedModelSetupStep.Preview &&
            _guidedPreviewFailureMessage is { } detail)
        {
            GuidedStatus = detail;
        }
    }

    public void SetGuidedPreviewStatus(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (GuidedStep == GuidedModelSetupStep.Preview)
        {
            GuidedStatus = message.Trim();
        }
    }

    /// <summary>
    /// Advances the guided workflow after the host has saved explicit review
    /// decisions and verified the target's stock-motion preview.
    /// </summary>
    public bool AdvanceGuidedPreviewAfterVerifiedPlayback()
    {
        if (GuidedStep != GuidedModelSetupStep.Preview || !HasModel)
            return false;

        GuidedStep = GuidedModelSetupStep.Export;
        GuidedStatus =
            "The reviewed animation is playing. Continue to Export when ready.";
        ActivateGuidedStep();
        return true;
    }

    private bool HasDecodedSourceAnimation => Animations.Any(static item =>
        item.DecodedClip is not null);

    private bool HasHumanoidStockPreviewRig
    {
        get
        {
            if (_model?.Rig is null)
                return false;
            var roles = _model.Package.Document.CreateEffectiveBones()
                .Select(bone => HumanoidBoneSemanticClassifier.Classify(bone.Name)?.Role)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            return roles.Contains("body.pelvis") && roles.Contains("body.head") &&
                roles.Contains("arm.left.upper") && roles.Contains("arm.right.upper") &&
                roles.Contains("leg.left.upper") && roles.Contains("leg.right.upper");
        }
    }

    public void SetImportedAnimationWorkspaceOffer(Func<string, IEnumerable<AnimationClip>, Task<bool>> offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        _importedAnimationWorkspaceOffer = offer;
    }

    private void InitializeGuidedSetup()
    {
        InitializeGuidedUnrigged();
        InitializeGuidedExport();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(HasModel) or nameof(ModelName)) RefreshGuidedModel();
            if (args.PropertyName == nameof(SelectedAnimation)) RefreshGuidedFeatures();
            if (args.PropertyName is nameof(IsBusy) or nameof(BuildStatus)) NotifyGuidedState();
        };
        Conformance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(RigConformanceWizardViewModel.IsBusy) or
                nameof(RigConformanceWizardViewModel.Fit) or nameof(RigConformanceWizardViewModel.GuidedFitBlockReason))
                NotifyGuidedState();
        };
    }

    private void RefreshGuidedModel()
    {
        RefreshGuidedFeatures();
        Guid? modelId = _model?.Package.Document.ModelId;
        if (_guidedModelId == modelId) { NotifyGuidedState(); return; }
        _guidedModelId = modelId;
        _guidedShowJoints = false;
        bool hasAppliedFit = _model?.Rig is { } rig &&
            _model.Package.Document.RigConformance is { } conformance &&
            conformance.MatchesAppliedOutputRig(rig, _model.Package.Document.Source.ContentSha256);
        _guidedUsesDl1Rig = hasAppliedFit || _model?.Package.Document.WorkflowMode switch
        {
            CustomModelWorkflowMode.Character => true,
            CustomModelWorkflowMode.OriginalRig => false,
            _ => hasAppliedFit || _model?.Package.Document.RigMode == CustomModelRigMode.Dl1HumanoidFit,
        };
        GuidedStep = modelId is null ? GuidedModelSetupStep.Import
            : hasAppliedFit ? GuidedModelSetupStep.Preview : GuidedModelSetupStep.Adjust;
        GuidedStatus = modelId is null ? "Choose an FBX, then choose its skeleton."
            : hasAppliedFit ? "Your saved Dying Light fit is restored. Preview movement before export."
            : _guidedUsesDl1Rig
                ? "The model is loaded. Continue to prepare its Dying Light skeleton."
                : "The original skeleton is restored. Preview an animation before export.";
        NotifyGuidedState();
    }

    public async Task BeginGuidedSetupAsync(CustomModelSkeletonChoice choice)
    {
        if (!HasModel || choice == CustomModelSkeletonChoice.Cancel) return;
        if (choice == CustomModelSkeletonChoice.KeepOriginal &&
            _model?.Package.Document.RigConformance?.AppliedOutputRigSignature is not null)
        {
            GuidedStatus = "Undo the fit or reopen the source model to use its original skeleton.";
            return;
        }
        _guidedUsesDl1Rig = choice == CustomModelSkeletonChoice.MapToDl1;
        PersistGuidedWorkflowMode(_guidedUsesDl1Rig
            ? CustomModelWorkflowMode.Character
            : CustomModelWorkflowMode.OriginalRig);
        GuidedStep = GuidedModelSetupStep.Adjust;
        IsConformTabSelected = true;
        if (_guidedUsesDl1Rig)
        {
            if (Conformance.HasUnriggedSource)
                await PrepareGuidedUnriggedModelAsync();
            else
            {
                if (Conformance.StartAdaptStudioCommand.CanExecute(null)) Conformance.StartAdaptStudioCommand.Execute(null);
                await PrepareGuidedAdjustmentAsync();
            }
        }
        else GuidedStatus = "Your original skeleton and skinning are retained. Continue to preview movement.";
        ActivateGuidedStep();
        NotifyGuidedState();
        if (_importedAnimationWorkspaceOffer is { } offer && _model is { } model)
        {
            string sourceKey = $"{model.Package.Document.ModelId:N}/{model.Package.Document.Source.ContentSha256}";
            if (_offeredAnimationSources.Add(sourceKey))
                await offer(ModelName, Animations.Where(item => item.DecodedClip is not null).Select(item => item.DecodedClip!));
        }
    }

    private async Task PrepareGuidedAdjustmentAsync()
    {
        GuidedStatus = "Matching the skeleton and checking joint positions…";
        var result = await Conformance.PrepareGuidedFitAsync(preserveSourceProportions: true);
        GuidedStatus = result.CanApply
            ? "Ready. Keep the automatic fit, or turn on Adjust joints to move a joint in the viewport."
            : result.Reason;
    }

    private void ActivateGuidedStep()
    {
        if (!HasModel) return;
        IsConformTabSelected = true;
        Conformance.StudioStage = IsGuidedAdjust ? RigStudioStage.Fit
            : IsGuidedPreview ? RigStudioStage.Animate : RigStudioStage.VerifyAndExport;
        if (IsGuidedAdjust) Conformance.Stage = RigConformanceStage.Refine;
    }

    private void PersistGuidedWorkflowMode(CustomModelWorkflowMode mode)
    {
        if (_model is not { } current ||
            current.Package.Document.WorkflowMode == mode)
            return;

        AuthoringSnapshot before = CaptureAuthoringSnapshot();
        _model = Conformance.UpdateWorkflowMode(mode) ?? current;
        RecordAuthoringUndo(before);
        MarkAuthoringChanged();
    }

    private async Task ReturnToGuidedStepAsync()
    {
        _guidedWorking = true;
        NotifyGuidedState();
        try
        {
            GuidedStep = (GuidedModelSetupStep)((int)GuidedStep - 1);
            if (IsGuidedAdjust && GuidedUsesDl1Rig)
                await PrepareGuidedAdjustmentAsync();
            else GuidedStatus = IsGuidedAdjust
                ? "Your original skeleton and skinning are retained. Continue to preview movement."
                : "Play a stock animation to check this model in motion.";
            ActivateGuidedStep();
        }
        finally { _guidedWorking = false; NotifyGuidedState(); }
    }

    private async Task RunGuidedPrimaryAsync()
    {
        _guidedWorking = true;
        NotifyGuidedState();
        try
        {
            switch (GuidedStep)
            {
                case GuidedModelSetupStep.Import:
                    await ImportFbxCommand.ExecuteAsync(null);
                    break;
                case GuidedModelSetupStep.Adjust:
                    if (GuidedUsesDl1Rig)
                    {
                        if (Conformance.HasUnriggedSource && !await BuildGuidedUnriggedRigAsync()) break;
                        if (!Conformance.CanApplyGuidedFit) await PrepareGuidedAdjustmentAsync();
                        if (!Conformance.CanApplyGuidedFit) { GuidedStatus = Conformance.GuidedFitBlockReason; break; }
                        long revisionBeforeApply = PersistenceRevision;
                        var modelBeforeApply = _model;
                        Conformance.ApplyConformanceCommand.Execute(null);
                        if (PersistenceRevision <= revisionBeforeApply || ReferenceEquals(_model, modelBeforeApply) ||
                            _model?.Package.Document.RigConformance is null)
                        { GuidedStatus = BuildStatus; break; }
                    }
                    GuidedStep = GuidedModelSetupStep.Preview;
                    GuidedStatus = GuidedUsesDl1Rig ? "Preview an animation to check the fit." : "Preview or create an animation.";
                    ActivateGuidedStep();
                    break;
                case GuidedModelSetupStep.Preview:
                    if (!GuidedUsesDl1Rig && HasDecodedSourceAnimation)
                    {
                        CustomModelAnimationClipItemViewModel? sourceAnimation =
                            SelectedAnimation?.DecodedClip is not null
                                ? SelectedAnimation
                                : Animations.FirstOrDefault(static item => item.DecodedClip is not null);
                        if (sourceAnimation is null)
                        {
                            GuidedStatus = "No animation is available. Import or create one before exporting this rig.";
                            break;
                        }

                        SelectedAnimation = sourceAnimation;
                        Timeline.CurrentFrame = 0;
                        Timeline.IsPlaying = true;
                        GuidedStep = GuidedModelSetupStep.Export;
                        ActivateGuidedStep();
                        GuidedStatus = $"Previewing {sourceAnimation.DisplayName}. Continue to Export when ready.";
                        break;
                    }
                    if (!GuidedUsesDl1Rig && !HasHumanoidStockPreviewRig)
                    {
                        GuidedStatus = "No animation is available. Import or create one before exporting this rig.";
                        break;
                    }
                    if (_guidedPreviewHandler is null)
                    { GuidedStatus = "Animation preview is unavailable in this workspace."; break; }
                    await _synchronizeProject();
                    GuidedStatus = "Loading a Dying Light animation and matching it to your model…";
                    bool played = await _guidedPreviewHandler();
                    if (!played && GuidedPreviewReviewContext is { HasGuidedPreviewReviewDraft: true } review &&
                        !review.HasGuidedExtraBoneExceptions)
                    {
                        played = await review.CompleteGuidedExtraBonePreviewAsync();
                    }
                    if (played)
                    {
                        GuidedStep = GuidedModelSetupStep.Export;
                        GuidedStatus = "The animation is playing. Return to Models when ready to export.";
                    }
                    else GuidedStatus = GuidedPreviewFailureMessage ??
                        "The animation could not be played. Check the reported source or rig issue and try again.";
                    break;
                case GuidedModelSetupStep.Export:
                    if (GuidedChannelPolicyReviewRequired)
                    {
                        GuidedStatus = GuidedChannelPolicyReviewSummary;
                        break;
                    }
                    if (HasGuidedIncludedPoseOnlyTakes)
                        GuidedStatus = GuidedAnimationPackageSummary;
                    await BuildCompletePackageCommand.ExecuteAsync(null);
                    GuidedStatus = BuildStatus;
                    break;
            }
        }
        finally { _guidedWorking = false; NotifyGuidedState(); }
    }

    private void NotifyGuidedState()
    {
        foreach (string property in new[] { nameof(IsGuidedImport), nameof(IsGuidedAdjust), nameof(IsGuidedPreview),
            nameof(IsGuidedExport), nameof(IsGuidedBusy), nameof(GuidedStepTitle), nameof(GuidedPrimaryLabel),
            nameof(GuidedUsesDl1Rig), nameof(GuidedPreviewAvailable), nameof(GuidedRigSummary), nameof(GuidedFeaturesSummary),
            nameof(GuidedShowJoints), nameof(ShowGuidedJointControls), nameof(ShowGuidedFitReview), nameof(IsGuidedUnriggedPreparation),
            nameof(GuidedBodyDetectionStatus) }) OnPropertyChanged(property);
        _guidedPrimaryCommand?.NotifyCanExecuteChanged();
        _guidedBackCommand?.NotifyCanExecuteChanged();
        _guidedChooseSkeletonCommand?.NotifyCanExecuteChanged();
    }
}
