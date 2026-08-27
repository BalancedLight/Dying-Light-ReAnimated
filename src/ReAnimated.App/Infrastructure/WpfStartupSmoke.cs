using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ReAnimated.App.ViewModels;
using ReAnimated.App.Views;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

/// <summary>
/// Opt-in packaged startup acceptance. This runs the real WPF window and both
/// HwndHost-backed D3D11 viewports without exposing a normal interactive
/// window, records bounded evidence, then shuts the application down.
/// </summary>
internal sealed class WpfStartupSmoke
{
    public const string Switch = "--wpf-startup-smoke";
    public const string ResultFileName =
        "DL_REANIMATED_WPF_STARTUP_SMOKE.json";
    public const string Format =
        "dl-reanimated-wpf-startup-smoke";
    public const int SchemaVersion = 4;

    private const int RequiredViewportCount = 2;
    private const long RequiredPresentedFrames = 3;
    internal const int RequiredResizeStepCount = 6;
    private static readonly TimeSpan DefaultTimeout =
        TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            WriteIndented = true,
        };

    private readonly string _outputDirectory;
    private readonly string _resultPath;
    private readonly TimeSpan _timeout;
    private readonly Stopwatch _stopwatch = new();
    private int _finishing;

    private WpfStartupSmoke(
        string outputDirectory,
        TimeSpan timeout)
    {
        _outputDirectory = outputDirectory;
        _resultPath = Path.Combine(
            outputDirectory,
            ResultFileName);
        _timeout = timeout;
    }

    public bool IsComplete { get; private set; }

    public static bool IsRequested(
        IReadOnlyList<string> arguments) =>
        arguments.Count > 0 &&
        string.Equals(
            arguments[0],
            Switch,
            StringComparison.Ordinal);

    public static WpfStartupSmoke Create(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if ((arguments.Count != 2 &&
             arguments.Count != 3) ||
            !string.Equals(
                arguments[0],
                Switch,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(arguments[1]))
        {
            throw new ArgumentException(
                $"Usage: {Switch} <empty-output-directory> [<timeout-seconds>]",
                nameof(arguments));
        }

        TimeSpan timeout = DefaultTimeout;
        if (arguments.Count == 3)
        {
            if (!int.TryParse(
                    arguments[2],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int timeoutSeconds) ||
                timeoutSeconds is < 5 or > 120)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(arguments),
                    "The WPF startup-smoke timeout must be an integer from 5 through 120 seconds.");
            }

            timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        string outputDirectory =
            Path.GetFullPath(arguments[1]);
        if (File.Exists(outputDirectory))
        {
            throw new IOException(
                $"The WPF startup-smoke output path is a file: {outputDirectory}");
        }

        if (Directory.Exists(outputDirectory) &&
            Directory.EnumerateFileSystemEntries(
                    outputDirectory)
                .Any())
        {
            throw new IOException(
                "The WPF startup-smoke output directory must be empty.");
        }

        Directory.CreateDirectory(outputDirectory);
        return new WpfStartupSmoke(
            outputDirectory,
            timeout);
    }

    public void Attach(
        Application application,
        Window window,
        MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        // Reproduce the normal Browse -> Models transition before the renderer
        // smoke takes ownership of both animation viewports. The Models surface
        // is physically detached while Browse is active, so this catches the
        // exact regression where it returned without its command DataContext.
        viewModel.ActiveWorkspaceMode = "Models";

        window.WindowStartupLocation =
            WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        ConfigureSmokeWindowBounds(window);
        PositionOffscreen(window);

        window.Loaded += OnLoaded;
        return;

        async void OnLoaded(
            object sender,
            RoutedEventArgs args)
        {
            window.Loaded -= OnLoaded;
            // Command bindings are not guaranteed to have transferred until
            // the first loaded/layout pass. Validate the reattached Models
            // surface in the same live state in which a user can click it,
            // rather than treating the pre-show binding queue as a failure.
            ValidateModelsWorkspaceCommands(window, viewModel);
            ValidateDetachedWorkflowPaneContexts(window, viewModel);

            SeedAnimationLibrary(viewModel);
            await RunAsync(
                application,
                window);
        }
    }

    private static void ValidateModelsWorkspaceCommands(
        Window window,
        MainWindowViewModel viewModel)
    {
        var workspace = window.FindName(
            "ModelsWorkspaceSurface") as ModelsWorkspaceView
            ?? throw new InvalidDataException(
                "The detachable Models workspace was not found in the real WPF window.");
        if (!ReferenceEquals(workspace.DataContext, viewModel.Models))
        {
            throw new InvalidDataException(
                "The reattached Models workspace did not retain its Models ViewModel.");
        }

        var importButton = workspace.FindName(
            "ImportFbxButton") as Button
            ?? throw new InvalidDataException(
                "The Models Import FBX button was not found.");
        var openButton = workspace.FindName(
            "OpenPackageButton") as Button
            ?? throw new InvalidDataException(
                "The Models Open .dlrmodel button was not found.");
        if (!ReferenceEquals(
                importButton.Command,
                viewModel.Models.ImportFbxCommand) ||
            !importButton.Command.CanExecute(
                importButton.CommandParameter))
        {
            throw new InvalidDataException(
                "The reattached Models Import FBX button has no executable command.");
        }

        if (!ReferenceEquals(
                openButton.Command,
                viewModel.Models.OpenPackageCommand) ||
            !openButton.Command.CanExecute(
                openButton.CommandParameter))
        {
            throw new InvalidDataException(
                "The reattached Models Open .dlrmodel button has no executable command.");
        }
    }

    private static void ValidateDetachedWorkflowPaneContexts(
        Window window,
        MainWindowViewModel viewModel)
    {
        // These are the roots that exposed the regression: they are detached
        // from MainWindow before first render and can therefore never rely on
        // inherited DataContext. Checking both ordinary metadata panes and
        // nested viewport/timeline panes keeps a black renderer from passing
        // merely because its HwndHost continues to present empty frames.
        ValidatePaneDataContext(
            window,
            "ModelsPreviewPane",
            viewModel);
        ValidatePaneDataContext(
            window,
            "AnimationsDetailsPane",
            viewModel);
        ValidatePaneDataContext(
            window,
            "AnimationsSourcePreviewPane",
            viewModel.SourceViewport);

        // The Animations source preview had no transport at all; this pane is
        // the only way to play, step or scrub it from that workspace, so its
        // presence is checked rather than assumed.
        ValidatePaneDataContext(
            window,
            "AnimationsSourceTimelinePane",
            viewModel.Timeline);
        ValidatePaneDataContext(
            window,
            "PlaybackContextPane",
            viewModel);
        ValidatePaneDataContext(
            window,
            "PlaybackTargetViewportPane",
            viewModel.TargetViewport);
        ValidatePaneDataContext(
            window,
            "PlaybackTimelinePane",
            viewModel.Timeline);
        ValidatePaneDataContext(
            window,
            "AnimationContextStrip",
            viewModel);
        ValidatePaneDataContext(
            window,
            "SourceViewportPane",
            viewModel.SourceViewport);
        ValidatePaneDataContext(
            window,
            "RetargetTargetViewportPane",
            viewModel);
        ValidatePaneDataContext(
            window,
            "RetargetTimelinePane",
            viewModel.Timeline);
    }

    private static void ValidatePaneDataContext(
        Window window,
        string paneName,
        object expectedDataContext)
    {
        if (window.FindName(paneName) is not FrameworkElement pane)
        {
            throw new InvalidDataException(
                $"The detachable '{paneName}' pane was not found in the real WPF window.");
        }

        if (!ReferenceEquals(pane.DataContext, expectedDataContext))
        {
            throw new InvalidDataException(
                $"The detachable '{paneName}' pane lost its intended ViewModel.");
        }
    }

    public void TryWriteStartupFailure(
        Exception exception,
        string stage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (Interlocked.Exchange(
                ref _finishing,
                1) != 0)
        {
            return;
        }

        _ = TryWriteResult(
            CreateFailureResult(
                exception,
                stage,
                [],
                []));
    }

    private async Task RunAsync(
        Application application,
        Window window)
    {
        _stopwatch.Restart();
        D3D11RenderHost[] hosts = [];
        bool animationLibraryRowMaterialized = false;
        bool floatingViewportRoundTrip = false;
        var latestStatuses =
            new Dictionary<D3D11RenderHost, D3D11RendererStatus>();
        List<WpfResizeSmokeStepResult> resizeSteps = [];
        EventHandler<D3D11RendererStatus>? statusHandler = null;
        string stage = "WPF viewport startup";
        try
        {
            stage = "WPF animation library row";
            animationLibraryRowMaterialized =
                await MaterializeAnimationLibraryRowAsync(
                    window);

            // Materialize the Retarget/Edit dock workspace after the
            // animation-table check so resize and floating-window evidence
            // covers the source/target layout users interact with.
            if (window.DataContext is not MainWindowViewModel viewModel)
            {
                throw new InvalidDataException(
                    "The startup-smoke window lost its main ViewModel.");
            }

            await window.Dispatcher.InvokeAsync(
                () =>
                {
                    viewModel.ActiveWorkspaceMode = "Retarget/Edit";
                    viewModel.ConfigureStartupSmokeDualViewport();
                    window.UpdateLayout();
                },
                DispatcherPriority.Loaded);

            stage = "WPF viewport startup";
            hosts = await WaitForVisibleViewportHostsAsync(
                window,
                RequiredViewportCount);

            statusHandler = (
                object? sender,
                D3D11RendererStatus status) =>
            {
                if (sender is D3D11RenderHost host)
                {
                    latestStatuses[host] = status;
                }
            };
            foreach (D3D11RenderHost host in hosts)
            {
                host.RendererStatusChanged += statusHandler;
            }

            bool initialReady = false;
            while (_stopwatch.Elapsed < _timeout)
            {
                ThrowIfAnyViewportFaulted(
                    latestStatuses);

                if (hosts.All(host =>
                        latestStatuses.TryGetValue(
                            host,
                            out D3D11RendererStatus? status) &&
                        IsReadyAtCurrentSize(
                            host,
                            status,
                            RequiredPresentedFrames)))
                {
                    initialReady = true;
                    break;
                }

                await Task.Delay(50);
            }

            if (!initialReady)
            {
                throw new TimeoutException(
                    $"Both packaged WPF viewports did not reach Ready with at least {RequiredPresentedFrames:N0} presented frames and matching renderer pixel dimensions inside {_timeout.TotalSeconds:N0} seconds.");
            }

            stage = "WPF floating viewport";
            floatingViewportRoundTrip =
                await RunFloatingViewportRoundTripAsync(
                    application,
                    window,
                    hosts,
                    latestStatuses);

            stage = "WPF viewport resize";
            await RunResizeSequenceAsync(
                window,
                hosts,
                latestStatuses,
                resizeSteps);
            FinishSuccessfully(
                application,
                hosts,
                latestStatuses,
                resizeSteps,
                animationLibraryRowMaterialized,
                floatingViewportRoundTrip);
        }
        catch (Exception exception)
        {
            FinishWithFailure(
                application,
                exception,
                stage,
                hosts,
                latestStatuses,
                resizeSteps,
                animationLibraryRowMaterialized,
                floatingViewportRoundTrip);
        }
        finally
        {
            if (statusHandler is not null)
            {
                foreach (D3D11RenderHost host in hosts)
                {
                    host.RendererStatusChanged -=
                        statusHandler;
                }
            }
        }
    }

    private static async Task<D3D11RenderHost[]>
        WaitForVisibleViewportHostsAsync(
            Window window,
            int expectedCount)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        D3D11RenderHost[] visibleHosts = [];
        do
        {
            await window.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle);
            visibleHosts = FindVisualChildren<D3D11RenderHost>(
                    window)
                .Where(static host => host.IsVisible)
                .ToArray();
            if (visibleHosts.Length == expectedCount)
            {
                return visibleHosts;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        int materializedCount = FindVisualChildren<D3D11RenderHost>(
                window)
            .Count();
        throw new InvalidDataException(
            $"The real WPF window must contain exactly {expectedCount:N0} visible D3D11 viewport hosts; found {visibleHosts.Length:N0} visible and {materializedCount:N0} materialized.");
    }

    private static async Task<bool>
        RunFloatingViewportRoundTripAsync(
            Application application,
            Window window,
            IReadOnlyList<D3D11RenderHost> hosts,
            Dictionary<
                D3D11RenderHost,
                D3D11RendererStatus> latestStatuses)
    {
        if (window is not MainWindow mainWindow)
        {
            throw new InvalidDataException(
                "The startup-smoke window is not the dock-enabled main window.");
        }

        Dictionary<D3D11RenderHost, long> floatingBaselines =
            hosts.ToDictionary(
                host => host,
                host => latestStatuses[host].PresentedFrames);
        await window.Dispatcher.InvokeAsync(
            () =>
            {
                if (!mainWindow.FloatTargetViewportForSmoke())
                {
                    throw new InvalidDataException(
                        "The DL1 target viewport did not enter a floating dock window.");
                }

                window.UpdateLayout();
            },
            DispatcherPriority.Loaded);
        await window.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle);

        if (!application.Windows
                .OfType<Window>()
                .Any(candidate =>
                    candidate.IsVisible &&
                    string.Equals(
                        candidate.GetType().Name,
                        "LayoutAnchorableFloatingWindowControl",
                        StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Floating the target camera did not create a visible dock window.");
        }

        await WaitForKnownViewportHostsReadyAsync(
            hosts,
            latestStatuses,
            floatingBaselines,
            "floating");

        Dictionary<D3D11RenderHost, long> dockingBaselines =
            hosts.ToDictionary(
                host => host,
                host => latestStatuses[host].PresentedFrames);
        await window.Dispatcher.InvokeAsync(
            () =>
            {
                if (!mainWindow.DockTargetViewportForSmoke())
                {
                    throw new InvalidDataException(
                        "The floating DL1 target viewport did not dock back into Retarget/Edit.");
                }

                window.UpdateLayout();
            },
            DispatcherPriority.Loaded);
        await window.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle);

        await WaitForKnownViewportHostsReadyAsync(
            hosts,
            latestStatuses,
            dockingBaselines,
            "re-docked");
        await WaitForVisibleViewportHostsAsync(
            window,
            RequiredViewportCount);

        return true;
    }

    private static async Task
        WaitForKnownViewportHostsReadyAsync(
            IReadOnlyList<D3D11RenderHost> hosts,
            Dictionary<
                D3D11RenderHost,
                D3D11RendererStatus> latestStatuses,
            Dictionary<D3D11RenderHost, long> baselines,
            string stateLabel)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            await hosts[0].Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle);
            if (hosts.All(host =>
                    host.IsVisible &&
                    latestStatuses.TryGetValue(
                        host,
                        out D3D11RendererStatus? status) &&
                    IsReadyAtCurrentSize(
                        host,
                        status,
                        baselines[host] + 1)))
            {
                return;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidDataException(
            $"Both known D3D11 viewport hosts must remain visible, Ready, correctly sized, and advance a hardware frame while the target camera is {stateLabel}.");
    }

    private static void SeedAnimationLibrary(
        MainWindowViewModel viewModel)
    {
        viewModel.AnimationLibrary.Add(
            new AnimationLibraryItemViewModel(
                Guid.Parse(
                    "37b833c6-d560-4ce8-97b6-c886e235741c"),
                "Packaged startup binding control",
                "retail://dl1/base/animation/control.anm2",
                "player_11_fpp",
                "player_11_fpp",
                "Body + facial",
                "30/1 FPS (AnimationScr)",
                "1.0 seconds",
                "Same rig / direct",
                "Synthetic metadata only; no retail data is embedded.",
                true));
    }

    private static async Task<bool>
        MaterializeAnimationLibraryRowAsync(
            Window window)
    {
        await window.Dispatcher.InvokeAsync(
            () =>
            {
                var viewModel = window.DataContext as
                    MainWindowViewModel
                    ?? throw new InvalidDataException(
                        "The startup-smoke window has no main ViewModel.");
                viewModel.ActiveWorkspaceMode = "Animations";
                window.UpdateLayout();
            },
            DispatcherPriority.Loaded);
        await window.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Render);

        return await window.Dispatcher.InvokeAsync(
            () =>
            {
                var library = window.FindName(
                    "AnimationLibraryTable") as DataGrid
                    ?? throw new InvalidDataException(
                        "The dedicated animation-library table was not found in the real WPF window.");
                if (library.Items.Count != 1)
                {
                    throw new InvalidDataException(
                        $"The packaged startup smoke expected one animation-library row; found {library.Items.Count:N0}.");
                }

                library.ScrollIntoView(library.Items[0]);
                library.UpdateLayout();
                if (library.ItemContainerGenerator
                        .ContainerFromIndex(0) is not DataGridRow row ||
                    row.ActualWidth <= 0 ||
                    row.ActualHeight <= 0)
                {
                    throw new InvalidDataException(
                        "The animation-library row did not materialize with a measurable WPF container.");
                }

                return true;
            },
            DispatcherPriority.Render);
    }

    private async Task RunResizeSequenceAsync(
        Window window,
        IReadOnlyList<D3D11RenderHost> hosts,
        Dictionary<
            D3D11RenderHost,
            D3D11RendererStatus> latestStatuses,
        List<WpfResizeSmokeStepResult> resizeSteps)
    {
        IReadOnlyList<WpfResizeTarget> schedule =
            CreateResizeSchedule(
                window.MinWidth,
                window.MinHeight,
                window.MaxWidth,
                window.MaxHeight);
        if (schedule.Count != RequiredResizeStepCount)
        {
            throw new InvalidOperationException(
                $"The WPF startup-smoke resize schedule must contain exactly {RequiredResizeStepCount:N0} steps.");
        }

        for (int stepIndex = 0;
             stepIndex < schedule.Count;
             stepIndex++)
        {
            WpfResizeTarget target = schedule[stepIndex];
            var baselineFrames =
                hosts.ToDictionary(
                    host => host,
                    host => latestStatuses[host]
                        .PresentedFrames);

            window.Width = target.Width;
            window.Height = target.Height;
            PositionOffscreen(window);
            await window.Dispatcher.InvokeAsync(
                window.UpdateLayout,
                System.Windows.Threading.DispatcherPriority
                    .Render);

            bool stepReady = false;
            while (_stopwatch.Elapsed < _timeout)
            {
                ThrowIfAnyViewportFaulted(
                    latestStatuses);
                if (IsWindowAtCurrentSize(window, target) &&
                    hosts.All(host =>
                        latestStatuses.TryGetValue(
                            host,
                            out D3D11RendererStatus? status) &&
                        IsReadyAtCurrentSize(
                            host,
                            status,
                            baselineFrames[host] + 1)))
                {
                    stepReady = true;
                    break;
                }

                await Task.Delay(50);
            }

            if (!stepReady)
            {
                throw new TimeoutException(
                    $"Resize step {stepIndex + 1:N0}/{schedule.Count:N0} did not settle the packaged WPF window at {target.Width:0.##}x{target.Height:0.##} and leave both viewports Ready with advanced frames and matching renderer pixel dimensions inside {_timeout.TotalSeconds:N0} seconds. Actual window size: {window.ActualWidth:0.##}x{window.ActualHeight:0.##}.");
            }

            resizeSteps.Add(
                CreateResizeStepResult(
                    stepIndex,
                    target,
                    window,
                    hosts,
                    latestStatuses,
                    baselineFrames));
        }
    }

    internal static IReadOnlyList<WpfResizeTarget>
        CreateResizeSchedule(
            double minimumWidth,
            double minimumHeight,
            double maximumWidth,
            double maximumHeight)
    {
        double minimumWindowWidth = Math.Ceiling(
            Math.Max(1.0, minimumWidth));
        double minimumWindowHeight = Math.Ceiling(
            Math.Max(1.0, minimumHeight));
        double maximumWindowWidth = Math.Floor(maximumWidth);
        double maximumWindowHeight = Math.Floor(maximumHeight);
        if (maximumWindowWidth <= minimumWindowWidth ||
            maximumWindowHeight <= minimumWindowHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWidth),
                "The WPF startup-smoke resize bounds must leave room for both compact and expanded steps.");
        }

        double widthRange =
            maximumWindowWidth - minimumWindowWidth;
        double heightRange =
            maximumWindowHeight - minimumWindowHeight;

        double WidthAt(double fraction) =>
            Math.Round(
                minimumWindowWidth + widthRange * fraction,
                MidpointRounding.AwayFromZero);
        double HeightAt(double fraction) =>
            Math.Round(
                minimumWindowHeight + heightRange * fraction,
                MidpointRounding.AwayFromZero);

        return
        [
            new(
                WidthAt(0.20),
                HeightAt(0.20)),
            new(
                WidthAt(0.90),
                HeightAt(0.90)),
            new(
                WidthAt(0.35),
                HeightAt(0.35)),
            new(
                WidthAt(0.75),
                HeightAt(0.75)),
            new(
                WidthAt(0.15),
                HeightAt(0.15)),
            new(
                WidthAt(0.65),
                HeightAt(0.65)),
        ];
    }

    internal static WpfViewportPixelSize
        CalculateExpectedPixelSize(
            double actualWidth,
            double actualHeight,
            double dpiScaleX,
            double dpiScaleY) =>
        new(
            Math.Max(
                1,
                (int)Math.Ceiling(
                    actualWidth * dpiScaleX)),
            Math.Max(
                1,
                (int)Math.Ceiling(
                    actualHeight * dpiScaleY)));

    private static bool IsReadyAtCurrentSize(
        D3D11RenderHost host,
        D3D11RendererStatus status,
        long minimumPresentedFrames)
    {
        WpfViewportMeasurement measurement =
            CaptureViewportMeasurement(host);
        return status.State ==
                RendererLifecycleState.Ready &&
            status.AdapterMode is not null &&
            status.PresentedFrames >=
                minimumPresentedFrames &&
            status.ViewportPixelWidth ==
                measurement.ExpectedPixelWidth &&
            status.ViewportPixelHeight ==
                measurement.ExpectedPixelHeight;
    }

    private static bool IsWindowAtCurrentSize(
        Window window,
        WpfResizeTarget target)
    {
        double actualWidth = window.ActualWidth;
        double actualHeight = window.ActualHeight;
        if (actualWidth <= 0.0 || actualHeight <= 0.0)
        {
            return false;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        double widthTolerance =
            Math.Max(
                0.01,
                1.0 / Math.Max(0.01, dpi.DpiScaleX));
        double heightTolerance =
            Math.Max(
                0.01,
                1.0 / Math.Max(0.01, dpi.DpiScaleY));
        return Math.Abs(actualWidth - target.Width) <=
                widthTolerance &&
            Math.Abs(actualHeight - target.Height) <=
                heightTolerance;
    }

    private static void ConfigureSmokeWindowBounds(
        Window window)
    {
        const double safetyMargin = 20.0;
        const double requiredWidthRange = 440.0;
        const double requiredHeightRange = 180.0;

        double maximumWidth = Math.Floor(
            SystemParameters.WorkArea.Width - safetyMargin);
        double maximumHeight = Math.Floor(
            SystemParameters.WorkArea.Height - safetyMargin);
        double minimumWidth = Math.Min(
            window.MinWidth,
            maximumWidth - requiredWidthRange);
        double minimumHeight = Math.Min(
            window.MinHeight,
            maximumHeight - requiredHeightRange);
        if (minimumWidth < 320.0 ||
            minimumHeight < 240.0 ||
            maximumWidth <= minimumWidth ||
            maximumHeight <= minimumHeight)
        {
            throw new InvalidOperationException(
                $"The WPF startup-smoke desktop work area is too small for bounded resize evidence: {maximumWidth:0.##}x{maximumHeight:0.##}.");
        }

        window.MinWidth = minimumWidth;
        window.MinHeight = minimumHeight;
        window.MaxWidth = maximumWidth;
        window.MaxHeight = maximumHeight;
    }

    private static void ThrowIfAnyViewportFaulted(
        IReadOnlyDictionary<
            D3D11RenderHost,
            D3D11RendererStatus> statuses)
    {
        if (statuses.Values.Any(status =>
                status.State ==
                RendererLifecycleState.Faulted))
        {
            throw new InvalidOperationException(
                "A packaged WPF viewport entered the faulted renderer state.");
        }
    }

    private static WpfViewportMeasurement
        CaptureViewportMeasurement(
            D3D11RenderHost host)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(host);
        WpfViewportPixelSize expected =
            CalculateExpectedPixelSize(
                host.ActualWidth,
                host.ActualHeight,
                dpi.DpiScaleX,
                dpi.DpiScaleY);
        return new WpfViewportMeasurement(
            host.ActualWidth,
            host.ActualHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY,
            expected.Width,
            expected.Height);
    }

    private static void PositionOffscreen(
        Window window)
    {
        window.Left =
            SystemParameters.VirtualScreenLeft -
            Math.Max(
                window.Width,
                window.MinWidth) -
            128;
        window.Top =
            SystemParameters.VirtualScreenTop -
            Math.Max(
                window.Height,
                window.MinHeight) -
            128;
    }

    private void FinishSuccessfully(
        Application application,
        IReadOnlyList<D3D11RenderHost> hosts,
        IReadOnlyDictionary<
            D3D11RenderHost,
            D3D11RendererStatus> statuses,
        IReadOnlyList<WpfResizeSmokeStepResult>
            resizeSteps,
        bool animationLibraryRowMaterialized,
        bool floatingViewportRoundTrip)
    {
        if (Interlocked.Exchange(
                ref _finishing,
                1) != 0)
        {
            return;
        }

        WpfViewportSmokeResult[] viewports =
            CreateViewportResults(hosts, statuses);
        var result = new WpfStartupSmokeResult(
            Format,
            SchemaVersion,
            Complete: true,
            RuntimeInformation.ProcessArchitecture
                .ToString(),
            Environment.Version.ToString(),
            Environment.OSVersion.VersionString,
            typeof(WpfStartupSmoke)
                .Assembly
                .GetCustomAttribute<
                    AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ??
                string.Empty,
            _stopwatch.Elapsed.TotalMilliseconds,
            _timeout.TotalSeconds,
            RequiredViewportCount,
            RequiredPresentedFrames,
            RequiredResizeStepCount,
            animationLibraryRowMaterialized,
            floatingViewportRoundTrip,
            viewports,
            resizeSteps,
            ErrorStage: null,
            ErrorType: null,
            ErrorMessage: null);
        if (TryWriteResult(result))
        {
            IsComplete = true;
            application.Shutdown(0);
        }
        else
        {
            application.Shutdown(1);
        }
    }

    private void FinishWithFailure(
        Application application,
        Exception exception,
        string stage,
        IReadOnlyList<D3D11RenderHost> hosts,
        IReadOnlyDictionary<
            D3D11RenderHost,
            D3D11RendererStatus> statuses,
        IReadOnlyList<WpfResizeSmokeStepResult>
            resizeSteps,
        bool animationLibraryRowMaterialized,
        bool floatingViewportRoundTrip)
    {
        if (Interlocked.Exchange(
                ref _finishing,
                1) != 0)
        {
            return;
        }

        _ = TryWriteResult(
            CreateFailureResult(
                exception,
                stage,
                CreateViewportResults(
                    hosts,
                    statuses),
                resizeSteps,
                animationLibraryRowMaterialized,
                floatingViewportRoundTrip));
        application.Shutdown(1);
    }

    private WpfStartupSmokeResult CreateFailureResult(
        Exception exception,
        string stage,
        IReadOnlyList<WpfViewportSmokeResult> viewports,
        IReadOnlyList<WpfResizeSmokeStepResult>
            resizeSteps,
        bool animationLibraryRowMaterialized = false,
        bool floatingViewportRoundTrip = false) =>
        new(
            Format,
            SchemaVersion,
            Complete: false,
            RuntimeInformation.ProcessArchitecture
                .ToString(),
            Environment.Version.ToString(),
            Environment.OSVersion.VersionString,
            typeof(WpfStartupSmoke)
                .Assembly
                .GetCustomAttribute<
                    AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ??
                string.Empty,
            _stopwatch.Elapsed.TotalMilliseconds,
            _timeout.TotalSeconds,
            RequiredViewportCount,
            RequiredPresentedFrames,
            RequiredResizeStepCount,
            animationLibraryRowMaterialized,
            floatingViewportRoundTrip,
            viewports,
            resizeSteps,
            stage,
            exception.GetType().FullName,
            CreateFailureMessage(exception));

    private static string CreateFailureMessage(
        Exception exception)
    {
        var messages = new List<string>
        {
            exception.Message,
        };
        for (Exception? inner = exception.InnerException;
             inner is not null;
             inner = inner.InnerException)
        {
            messages.Add(
                $"{inner.GetType().FullName}: {inner.Message}");
        }

        return string.Join(
            " --> ",
            messages);
    }

    private static WpfResizeSmokeStepResult
        CreateResizeStepResult(
            int stepIndex,
            WpfResizeTarget target,
            Window window,
            IReadOnlyList<D3D11RenderHost> hosts,
            Dictionary<
                D3D11RenderHost,
                D3D11RendererStatus> statuses,
            Dictionary<
                D3D11RenderHost,
                long> baselineFrames) =>
        new(
            stepIndex,
            target.Width,
            target.Height,
            window.ActualWidth,
            window.ActualHeight,
            hosts.Select((host, index) =>
                {
                    D3D11RendererStatus status =
                        statuses[host];
                    WpfViewportMeasurement measurement =
                        CaptureViewportMeasurement(host);
                    return new WpfResizeViewportSmokeResult(
                        index,
                        status.State.ToString(),
                        status.AdapterMode?.ToString(),
                        status.Message,
                        baselineFrames[host],
                        status.PresentedFrames,
                        measurement.ActualWidth,
                        measurement.ActualHeight,
                        measurement.DpiScaleX,
                        measurement.DpiScaleY,
                        measurement.ExpectedPixelWidth,
                        measurement.ExpectedPixelHeight,
                        status.ViewportPixelWidth,
                        status.ViewportPixelHeight,
                        host.Diagnostics.ToArray());
                })
                .ToArray());

    private static WpfViewportSmokeResult[]
        CreateViewportResults(
            IReadOnlyList<D3D11RenderHost> hosts,
            IReadOnlyDictionary<
                D3D11RenderHost,
                D3D11RendererStatus> statuses) =>
        hosts
            .Select((host, index) =>
            {
                _ = statuses.TryGetValue(
                    host,
                    out D3D11RendererStatus? status);
                WpfViewportMeasurement measurement =
                    CaptureViewportMeasurement(host);
                return new WpfViewportSmokeResult(
                    index,
                    status?.State.ToString() ??
                    RendererLifecycleState.Starting
                        .ToString(),
                    status?.AdapterMode?.ToString(),
                    status?.Message ??
                    host.StatusText,
                    status?.FramesPerSecond ??
                    host.FramesPerSecond,
                    status?.PresentedFrames ?? 0,
                    measurement.ActualWidth,
                    measurement.ActualHeight,
                    measurement.DpiScaleX,
                    measurement.DpiScaleY,
                    measurement.ExpectedPixelWidth,
                    measurement.ExpectedPixelHeight,
                    status?.ViewportPixelWidth ?? 0,
                    status?.ViewportPixelHeight ?? 0,
                    host.Diagnostics.ToArray());
            })
            .ToArray();

    private bool TryWriteResult(
        WpfStartupSmokeResult result)
    {
        try
        {
            string temporaryPath =
                _resultPath + ".tmp";
            byte[] bytes =
                JsonSerializer.SerializeToUtf8Bytes(
                    result,
                    SerializerOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(
                temporaryPath,
                _resultPath);
            return true;
        }
        catch
        {
            // Process exit remains nonzero when evidence cannot be written.
            return false;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(
        DependencyObject parent)
        where T : DependencyObject
    {
        int count =
            VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(
                    parent,
                    index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in
                     FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    internal sealed record WpfStartupSmokeResult(
        [property: JsonPropertyName("format")]
        string Format,
        [property: JsonPropertyName("schemaVersion")]
        int SchemaVersion,
        [property: JsonPropertyName("complete")]
        bool Complete,
        [property: JsonPropertyName("processArchitecture")]
        string ProcessArchitecture,
        [property: JsonPropertyName("runtimeVersion")]
        string RuntimeVersion,
        [property: JsonPropertyName("osVersion")]
        string OsVersion,
        [property: JsonPropertyName("informationalVersion")]
        string InformationalVersion,
        [property: JsonPropertyName("elapsedMilliseconds")]
        double ElapsedMilliseconds,
        [property: JsonPropertyName("timeoutSeconds")]
        double TimeoutSeconds,
        [property: JsonPropertyName("requiredViewportCount")]
        int RequiredViewportCount,
        [property: JsonPropertyName("requiredPresentedFrames")]
        long RequiredPresentedFrames,
        [property: JsonPropertyName("requiredResizeStepCount")]
        int RequiredResizeStepCount,
        [property: JsonPropertyName("animationLibraryRowMaterialized")]
        bool AnimationLibraryRowMaterialized,
        [property: JsonPropertyName("floatingViewportRoundTrip")]
        bool FloatingViewportRoundTrip,
        [property: JsonPropertyName("viewports")]
        IReadOnlyList<WpfViewportSmokeResult> Viewports,
        [property: JsonPropertyName("resizeSteps")]
        IReadOnlyList<WpfResizeSmokeStepResult>
            ResizeSteps,
        [property: JsonPropertyName("errorStage")]
        string? ErrorStage,
        [property: JsonPropertyName("errorType")]
        string? ErrorType,
        [property: JsonPropertyName("errorMessage")]
        string? ErrorMessage);

    internal sealed record WpfViewportSmokeResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("state")]
        string State,
        [property: JsonPropertyName("adapterMode")]
        string? AdapterMode,
        [property: JsonPropertyName("message")]
        string Message,
        [property: JsonPropertyName("framesPerSecond")]
        double FramesPerSecond,
        [property: JsonPropertyName("presentedFrames")]
        long PresentedFrames,
        [property: JsonPropertyName("actualWidth")]
        double ActualWidth,
        [property: JsonPropertyName("actualHeight")]
        double ActualHeight,
        [property: JsonPropertyName("dpiScaleX")]
        double DpiScaleX,
        [property: JsonPropertyName("dpiScaleY")]
        double DpiScaleY,
        [property: JsonPropertyName("expectedPixelWidth")]
        int ExpectedPixelWidth,
        [property: JsonPropertyName("expectedPixelHeight")]
        int ExpectedPixelHeight,
        [property: JsonPropertyName("rendererPixelWidth")]
        int RendererPixelWidth,
        [property: JsonPropertyName("rendererPixelHeight")]
        int RendererPixelHeight,
        [property: JsonPropertyName("diagnostics")]
        IReadOnlyList<string> Diagnostics);

    internal sealed record WpfResizeSmokeStepResult(
        [property: JsonPropertyName("stepIndex")]
        int StepIndex,
        [property: JsonPropertyName("requestedWindowWidth")]
        double RequestedWindowWidth,
        [property: JsonPropertyName("requestedWindowHeight")]
        double RequestedWindowHeight,
        [property: JsonPropertyName("actualWindowWidth")]
        double ActualWindowWidth,
        [property: JsonPropertyName("actualWindowHeight")]
        double ActualWindowHeight,
        [property: JsonPropertyName("viewports")]
        IReadOnlyList<WpfResizeViewportSmokeResult>
            Viewports);

    internal sealed record WpfResizeViewportSmokeResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("state")]
        string State,
        [property: JsonPropertyName("adapterMode")]
        string? AdapterMode,
        [property: JsonPropertyName("message")]
        string Message,
        [property: JsonPropertyName("baselinePresentedFrames")]
        long BaselinePresentedFrames,
        [property: JsonPropertyName("presentedFrames")]
        long PresentedFrames,
        [property: JsonPropertyName("actualWidth")]
        double ActualWidth,
        [property: JsonPropertyName("actualHeight")]
        double ActualHeight,
        [property: JsonPropertyName("dpiScaleX")]
        double DpiScaleX,
        [property: JsonPropertyName("dpiScaleY")]
        double DpiScaleY,
        [property: JsonPropertyName("expectedPixelWidth")]
        int ExpectedPixelWidth,
        [property: JsonPropertyName("expectedPixelHeight")]
        int ExpectedPixelHeight,
        [property: JsonPropertyName("rendererPixelWidth")]
        int RendererPixelWidth,
        [property: JsonPropertyName("rendererPixelHeight")]
        int RendererPixelHeight,
        [property: JsonPropertyName("diagnostics")]
        IReadOnlyList<string> Diagnostics);

    internal readonly record struct WpfResizeTarget(
        double Width,
        double Height);

    internal readonly record struct WpfViewportPixelSize(
        int Width,
        int Height);

    private readonly record struct WpfViewportMeasurement(
        double ActualWidth,
        double ActualHeight,
        double DpiScaleX,
        double DpiScaleY,
        int ExpectedPixelWidth,
        int ExpectedPixelHeight);
}
