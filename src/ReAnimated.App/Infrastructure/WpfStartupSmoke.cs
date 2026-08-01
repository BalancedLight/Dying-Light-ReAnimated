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
    public const int SchemaVersion = 3;

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

        SeedAnimationLibrary(viewModel);

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
            await RunAsync(
                application,
                window);
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

            stage = "WPF viewport startup";
            await window.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority
                    .ApplicationIdle);
            hosts = FindVisualChildren<D3D11RenderHost>(
                    window)
                .ToArray();
            if (hosts.Length != RequiredViewportCount)
            {
                throw new InvalidDataException(
                    $"The real WPF window must contain exactly {RequiredViewportCount:N0} D3D11 viewport hosts; found {hosts.Length:N0}.");
            }

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
                animationLibraryRowMaterialized);
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
                animationLibraryRowMaterialized);
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
                var tabControl = window.FindName(
                    "ExplorerTabControl") as TabControl
                    ?? throw new InvalidDataException(
                        "The explorer tab control was not found in the real WPF window.");
                var animationsTab = window.FindName(
                    "AnimationLibraryTab") as TabItem
                    ?? throw new InvalidDataException(
                        "The Animations tab was not found in the real WPF window.");
                tabControl.SelectedItem = animationsTab;
            },
            DispatcherPriority.Loaded);
        await window.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Render);

        return await window.Dispatcher.InvokeAsync(
            () =>
            {
                var library = window.FindName(
                    "AnimationLibraryList") as ListBox
                    ?? throw new InvalidDataException(
                        "The animation library was not found in the real WPF window.");
                if (library.Items.Count != 1)
                {
                    throw new InvalidDataException(
                        $"The packaged startup smoke expected one animation-library row; found {library.Items.Count:N0}.");
                }

                library.ScrollIntoView(library.Items[0]);
                library.UpdateLayout();
                if (library.ItemContainerGenerator
                        .ContainerFromIndex(0) is not ListBoxItem row ||
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
        bool animationLibraryRowMaterialized)
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
        bool animationLibraryRowMaterialized)
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
                animationLibraryRowMaterialized));
        application.Shutdown(1);
    }

    private WpfStartupSmokeResult CreateFailureResult(
        Exception exception,
        string stage,
        IReadOnlyList<WpfViewportSmokeResult> viewports,
        IReadOnlyList<WpfResizeSmokeStepResult>
            resizeSteps,
        bool animationLibraryRowMaterialized = false) =>
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
