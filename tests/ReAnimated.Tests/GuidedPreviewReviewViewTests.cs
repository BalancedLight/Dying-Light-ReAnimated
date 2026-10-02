using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.App.Views;
using ReAnimated.Core.Domain;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.Tests;

/// <summary>
/// Exercises the Models workspace host used by the docked and floating views.
/// The setup and all WPF tree access stay on the shared test dispatcher.
/// </summary>
public sealed class GuidedPreviewReviewViewTests
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(), $"GuidedPreviewReviewView-{Guid.NewGuid():N}");

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "EditorUsability")]
    public async Task ReviewBindingsSurviveDetachedHostAndReparenting()
    {
        MainWindowViewModel owner = await CreateOwnerAsync();
        try
        {
            WpfTestDispatcher.Run(() =>
            {
                SetDraft(owner, hasDraft: true);
                owner.Models.SetGuidedPreviewReviewContext(owner);
                SetGuidedStep(owner.Models, GuidedModelSetupStep.Preview);

                var view = new GuidedModelSetupView
                {
                    DataContext = owner.Models,
                };
                var firstHost = new ContentControl { Content = view };
                UpdateLayout(firstHost, view);

                Assert.Null(Window.GetWindow(view));
                StackPanel panel = GetReviewPanel(view);
                Assert.Same(owner, panel.DataContext);
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.Contains(
                    panel.GetVisualDescendants<TextBlock>(),
                    text => text.Text == owner.GuidedExtraBonePreservationSummary);

                Assert.Contains(
                    panel.GetVisualDescendants<TextBlock>(),
                    text => text.Text == "Synthetic weighted branch context.");

                Expander details = Assert.Single(
                    panel.GetVisualDescendants<Expander>(),
                    candidate => Equals(candidate.Header, "Extra bone details"));
                Assert.False(details.IsExpanded);
                details.IsExpanded = true;
                details.UpdateLayout();
                ItemsControl detailedReviews = Assert.Single(
                    details.GetVisualDescendants<ItemsControl>(),
                    candidate => ReferenceEquals(
                        candidate.ItemsSource,
                        owner.RequiredTargetBindReviews));
                Assert.Same(owner.RequiredTargetBindReviews, detailedReviews.ItemsSource);
                Assert.Same(
                    owner.RequiredTargetBindReviews[0],
                    Assert.Single(detailedReviews.Items.Cast<object>()));

                AssertButtonCommandAndEnabledState(
                    panel,
                    "Review mapped bones…",
                    owner.OpenGuidedMappingReviewCommand);
                AssertButtonCommandAndEnabledState(
                    panel,
                    "Keep extra bones and play",
                    owner.PreserveGuidedExtraBonesAndPlayCommand);
                AssertButtonCommandAndEnabledState(
                    panel,
                    "Set up cloth or hair motion…",
                    owner.OpenGuidedSecondaryMotionSetupCommand);

                firstHost.Content = null;
                var secondHost = new ContentControl { Content = view };
                UpdateLayout(secondHost, view);
                Assert.Null(Window.GetWindow(view));
                Assert.Same(owner, GetReviewPanel(view).DataContext);
                Assert.Equal(Visibility.Visible, GetReviewPanel(view).Visibility);
                Expander floatingDetails = Assert.Single(
                    GetReviewPanel(view).GetVisualDescendants<Expander>(),
                    candidate => Equals(candidate.Header, "Extra bone details"));
                Assert.True(floatingDetails.IsExpanded);
                AssertButtonCommandAndEnabledState(
                    GetReviewPanel(view),
                    "Keep extra bones and play",
                    owner.PreserveGuidedExtraBonesAndPlayCommand);
            });
        }
        finally
        {
            await DisposeOwnerAsync(owner);
            DeleteTemporaryDirectory();
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "EditorUsability")]
    public async Task ReviewPanelCollapsesForMissingDraftOrOwner()
    {
        MainWindowViewModel owner = await CreateOwnerAsync();
        MainWindowViewModel? unownedOwner = null;
        try
        {
            WpfTestDispatcher.Run(() =>
            {
                SetDraft(owner, hasDraft: false);
                owner.Models.SetGuidedPreviewReviewContext(owner);
                SetGuidedStep(owner.Models, GuidedModelSetupStep.Preview);

                var ownedView = new GuidedModelSetupView
                {
                    DataContext = owner.Models,
                };
                var ownedHost = new ContentControl { Content = ownedView };
                UpdateLayout(ownedHost, ownedView);
                Assert.Equal(Visibility.Collapsed, GetReviewPanel(ownedView).Visibility);
                Assert.Same(owner, GetReviewPanel(ownedView).DataContext);

                var unownedAssets = new Dl1AssetWorkspace(
                    Path.Combine(_temporaryDirectory, "unowned-assets.sqlite3"),
                    Path.Combine(_temporaryDirectory, "unowned-cache"));
                unownedOwner = new MainWindowViewModel(
                    new JsonWorkspaceStateStore(
                        Path.Combine(_temporaryDirectory, "unowned-workspace.json")),
                    new NoDialogs(),
                    unownedAssets);
                SetGuidedStep(unownedOwner.Models, GuidedModelSetupStep.Preview);
                var unownedView = new GuidedModelSetupView
                {
                    DataContext = unownedOwner.Models,
                };
                var unownedHost = new ContentControl { Content = unownedView };
                UpdateLayout(unownedHost, unownedView);
                StackPanel panel = GetReviewPanel(unownedView);
                Assert.Null(unownedOwner.Models.GuidedPreviewReviewContext);
                Assert.Null(panel.DataContext);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
            });
        }
        finally
        {
            if (unownedOwner is not null)
            {
                await DisposeOwnerAsync(unownedOwner);
            }
            await DisposeOwnerAsync(owner);
            DeleteTemporaryDirectory();
        }
    }

    private Task<MainWindowViewModel> CreateOwnerAsync()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        Dl1AssetWorkspace assets = new(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        MainWindowViewModel? createdOwner = null;
        WpfTestDispatcher.Run(() =>
        {
            createdOwner = new MainWindowViewModel(
                new JsonWorkspaceStateStore(
                    Path.Combine(_temporaryDirectory, "workspace.json")),
                new NoDialogs(),
                assets);
        });
        return Task.FromResult(createdOwner ?? throw new InvalidOperationException(
            "The MainWindowViewModel was not created on the WPF dispatcher."));
    }

    private static void SetDraft(MainWindowViewModel owner, bool hasDraft)
    {
        SetPrivateProperty(
            owner,
            nameof(MainWindowViewModel.HasGuidedPreviewReviewDraft),
            hasDraft);
        SetPrivateProperty(
            owner,
            nameof(MainWindowViewModel.GuidedPreviewReviewSummary),
            hasDraft ? "Synthetic preview review summary." : string.Empty);
        owner.RequiredTargetBindReviews.Clear();
        if (hasDraft)
        {
            owner.RequiredTargetBindReviews.Add(
                new TargetBindReviewViewModel(
                    targetBoneIndex: 1,
                    targetBone: "synthetic_accessory",
                    boneKind: BoneKind.Prop,
                    isReviewed: false,
                    contextSummary: "Synthetic weighted branch context.",
                    canReview: false,
                    canPreserveWithParent: true));
        }
    }

    private static void SetPrivateProperty(
        object owner,
        string propertyName,
        object value)
    {
        PropertyInfo property = owner.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public) ??
            throw new MissingMemberException(owner.GetType().FullName, propertyName);
        MethodInfo setter = property.GetSetMethod(nonPublic: true) ??
            throw new MissingMethodException(owner.GetType().FullName, propertyName);
        setter.Invoke(owner, [value]);
    }

    private static void SetGuidedStep(
        ModelsWorkspaceViewModel models,
        GuidedModelSetupStep step)
    {
        PropertyInfo property = typeof(ModelsWorkspaceViewModel).GetProperty(
            nameof(ModelsWorkspaceViewModel.GuidedStep))!;
        property.GetSetMethod(nonPublic: true)!.Invoke(models, [step]);
    }

    private static void UpdateLayout(
        ContentControl host,
        GuidedModelSetupView view)
    {
        host.ApplyTemplate();
        view.ApplyTemplate();
        host.Measure(new Size(900, 700));
        host.Arrange(new Rect(0, 0, 900, 700));
        host.UpdateLayout();
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.DataBind,
            new Action(static () => { }));
    }

    private static StackPanel GetReviewPanel(GuidedModelSetupView view) =>
        Assert.IsType<StackPanel>(view.FindName("GuidedPreviewReviewPanel"));

    private static void AssertButtonCommandAndEnabledState(
        StackPanel panel,
        string content,
        RelayCommand expectedCommand)
    {
        Button button = Assert.Single(
            panel.GetVisualDescendants<Button>(),
            candidate => Equals(candidate.Content, content));
        Assert.Same(expectedCommand, button.Command);
        Assert.Equal(expectedCommand.CanExecute(null), button.IsEnabled);
    }

    private static T FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject =>
        parent.GetVisualDescendants<T>().First();

    private static async Task DisposeOwnerAsync(MainWindowViewModel owner)
    {
        await WpfTestDispatcher.Run(() => owner.DisposeAsync().AsTask());
    }

    private void DeleteTemporaryDirectory()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }
}

internal static class GuidedReviewVisualTreeExtensions
{
    public static IEnumerable<T> GetVisualDescendants<T>(
        this DependencyObject parent)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in child.GetVisualDescendants<T>())
            {
                yield return descendant;
            }
        }
    }
}
