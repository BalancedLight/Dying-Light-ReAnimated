using CommunityToolkit.Mvvm.Input;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private RelayCommand? _preserveGuidedExtraBonesAndPlayCommand;

    public RelayCommand PreserveGuidedExtraBonesAndPlayCommand =>
        _preserveGuidedExtraBonesAndPlayCommand ??= new RelayCommand(
            PreserveGuidedExtraBonesAndPlay, CanApplyGuidedPreviewReview);

    public IEnumerable<TargetBindReviewViewModel> GuidedExtraBoneExceptions =>
        RequiredTargetBindReviews.Where(row => !row.IsReviewed &&
            (!row.CanReview || !row.CanPreserveWithParent));

    public bool HasGuidedExtraBoneExceptions => GuidedExtraBoneExceptions.Any();

    public string GuidedExtraBonePreservationSummary
    {
        get
        {
            int ordinary = RequiredTargetBindReviews.Count(row => row.CanReview && row.CanPreserveWithParent);
            int exceptions = GuidedExtraBoneExceptions.Count();
            string summary = ordinary == 0
                ? "No extra bones need the default attachment choice."
                : $"Keep {ordinary:N0} extra bone(s) at their rest shape and attached to their parents. Geometry and skin weights are preserved.";
            return exceptions == 0 ? summary
                : summary + $" {exceptions:N0} motion or parent choice(s) need attention below.";
        }
    }

    internal static int ApplyGuidedExtraBonePreservationDefaults(
        IEnumerable<TargetBindReviewViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int changed = 0;
        foreach (TargetBindReviewViewModel row in rows.ToArray())
        {
            if (row.CanReview && row.CanPreserveWithParent && !row.IsReviewed)
            {
                row.IsReviewed = true;
                changed++;
            }
        }
        return changed;
    }

    internal async Task<bool> CompleteGuidedExtraBonePreviewAsync()
    {
        Guid? animationId = _activeAnimationId;
        long generation = Volatile.Read(ref _animationTransitionGeneration);
        // Refreshing the project can queue a separate model-browser decode.
        // Let that owned decode release its busy state before applying the
        // choices from this same preview action.
        await (_automaticAssetPreviewTask ?? Task.CompletedTask);
        if (animationId != _activeAnimationId ||
            generation != Volatile.Read(ref _animationTransitionGeneration) ||
            !GuidedPreviewDraftPairMatchesCurrentState() ||
            HasGuidedExtraBoneExceptions ||
            !CanApplyGuidedPreviewReview())
            return false;

        PreserveGuidedExtraBonesAndPlay();
        return !HasGuidedPreviewReviewDraft && Timeline.IsPlaying &&
            _lastPreviewFramePair?.Token.AnimationId == animationId;
    }

    private void PreserveGuidedExtraBonesAndPlay()
    {
        if (!CanApplyGuidedPreviewReview()) return;
        _batchReviewingMapping = true;
        try { ApplyGuidedExtraBonePreservationDefaults(RequiredTargetBindReviews); }
        finally { _batchReviewingMapping = false; }
        ApplyGuidedPreviewReview();
        NotifyGuidedExtraBonePresentation();
    }

    private void NotifyGuidedExtraBonePresentation()
    {
        OnPropertyChanged(nameof(GuidedExtraBonePreservationSummary));
        OnPropertyChanged(nameof(GuidedExtraBoneExceptions));
        OnPropertyChanged(nameof(HasGuidedExtraBoneExceptions));
        _preserveGuidedExtraBonesAndPlayCommand?.NotifyCanExecuteChanged();
    }
}
