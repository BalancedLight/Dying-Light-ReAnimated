using System.Collections.Specialized;
using System.ComponentModel;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private MainWindowViewModel? _guidedPreviewReviewContext;

    /// <summary>Stable presentation owner while the Models pane is docked or floating.</summary>
    public MainWindowViewModel? GuidedPreviewReviewContext => _guidedPreviewReviewContext;

    internal void SetGuidedPreviewReviewContext(MainWindowViewModel context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ReferenceEquals(_guidedPreviewReviewContext, context)) return;
        if (_guidedPreviewReviewContext is { } previous)
        {
            previous.PropertyChanged -= OnGuidedPreviewReviewContextChanged;
            previous.RequiredTargetBindReviews.CollectionChanged -=
                OnGuidedPreviewReviewRowsChanged;
        }

        _guidedPreviewReviewContext = context;
        context.PropertyChanged += OnGuidedPreviewReviewContextChanged;
        context.RequiredTargetBindReviews.CollectionChanged +=
            OnGuidedPreviewReviewRowsChanged;
        OnPropertyChanged(nameof(GuidedPreviewReviewContext));
        OnPropertyChanged(nameof(GuidedPrimaryLabel));
    }

    private void OnGuidedPreviewReviewContextChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or
            nameof(MainWindowViewModel.HasGuidedPreviewReviewDraft) or
            nameof(MainWindowViewModel.GuidedPreviewReviewSummary) or
            nameof(MainWindowViewModel.HasGuidedExtraBoneExceptions))
        {
            OnPropertyChanged(nameof(GuidedPrimaryLabel));
        }
    }

    private void OnGuidedPreviewReviewRowsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args) =>
        OnPropertyChanged(nameof(GuidedPrimaryLabel));
}

