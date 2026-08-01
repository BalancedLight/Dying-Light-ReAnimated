using System.Windows;
using System.Windows.Threading;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;

namespace ReAnimated.App;

public partial class App : Application, IDisposable
{
    private readonly WpfStartupSmoke? _startupSmoke;
    private readonly FatalCrashPresentationGate _fatalCrashPresentation =
        new();
    private CrashReporter? _crashReporter;
    private StructuredFileLogger? _logger;
    private WorkspaceAutosaveService? _autosave;
    private MainWindowViewModel? _viewModel;
    private int _reportingCrash;
    private bool _disposed;

    internal App(
        WpfStartupSmoke? startupSmoke = null)
    {
        _startupSmoke = startupSmoke;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths paths = AppPaths.CreateDefault();
        JsonWorkspaceStateStore recoveryStore =
            new(paths.AutosaveFile);
        MainWindowViewModel viewModel = new(recoveryStore);
        _viewModel = viewModel;
        _autosave = new WorkspaceAutosaveService(viewModel, recoveryStore);
        _crashReporter = new CrashReporter(paths.CrashDirectory);
        _logger = new StructuredFileLogger(paths.LogDirectory);
        _logger.Write(
            AppLogLevel.Information,
            "application_start",
            "DL ReAnimated C# started.",
            new Dictionary<string, string>
            {
                ["runtime"] = Environment.Version.ToString(),
                ["os"] = Environment.OSVersion.VersionString,
            });

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        MainWindow window = new(viewModel, _autosave);
        MainWindow = window;
        _startupSmoke?.Attach(this, window, viewModel);
        window.Show();
        if (_startupSmoke is null)
        {
            _ = InitializeAssetCatalogAsync(viewModel);
        }
        _ = InitializeInstalledBuildStatusAsync(viewModel);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger?.Write(
            AppLogLevel.Information,
            "application_stop",
            "DL ReAnimated C# stopped.");
        _autosave?.Dispose();
        _autosave = null;
        if (_viewModel is not null)
        {
            _viewModel.DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
            _viewModel = null;
        }

        _logger?.Dispose();
        _logger = null;
        GC.SuppressFinalize(this);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs args)
    {
        // A modal MessageBox pumps a nested dispatcher frame. Without a
        // lifetime latch, a layout exception can re-enter this handler and
        // create an unbounded stack of crash reports and dialogs.
        args.Handled = true;
        if (!_fatalCrashPresentation.TryBegin())
        {
            return;
        }

        if (_startupSmoke is not null)
        {
            _startupSmoke.TryWriteStartupFailure(
                args.Exception,
                "DispatcherUnhandledException");
            Shutdown(1);
            return;
        }

        TryEmergencySave();
        string? reportPath = TryWriteCrashReport(
            args.Exception,
            "DispatcherUnhandledException");
        try
        {
            MessageBox.Show(
                reportPath is null
                    ? "Dying Light ReAnimated encountered a fatal error."
                    : $"Dying Light ReAnimated encountered a fatal error.\n\nCrash report:\n{reportPath}",
                "Dying Light ReAnimated",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            // The exception is handled only to prevent recursive dispatcher
            // presentation. The process still exits after the single report.
            Shutdown(1);
        }
    }

    private void OnDomainUnhandledException(
        object? sender,
        UnhandledExceptionEventArgs args)
    {
        Exception exception = args.ExceptionObject as Exception
            ?? new InvalidOperationException(
                $"Unhandled non-Exception object: {args.ExceptionObject}");
        _ = TryWriteCrashReport(exception, "AppDomain.UnhandledException");
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args)
    {
        _ = TryWriteCrashReport(args.Exception, "TaskScheduler.UnobservedTaskException");
        args.SetObserved();
    }

    private string? TryWriteCrashReport(
        Exception exception,
        string source)
    {
        if (Interlocked.Exchange(ref _reportingCrash, 1) != 0)
        {
            return null;
        }

        try
        {
            _logger?.Write(
                AppLogLevel.Critical,
                "unhandled_exception",
                $"Unhandled exception from {source}.",
                exception: exception);
            return _crashReporter?.WriteReport(
                exception,
                source,
                _autosave?.AutosavePath);
        }
        catch
        {
            return null;
        }
        finally
        {
            Volatile.Write(ref _reportingCrash, 0);
        }
    }

    private void TryEmergencySave()
    {
        try
        {
            _ = _autosave?.SaveNow("dispatcher-crash");
        }
        catch
        {
            // Crash reporting must preserve the original exception even when
            // the recovery writer is unavailable during teardown.
        }
    }

    private async Task InitializeInstalledBuildStatusAsync(
        MainWindowViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeInstalledBuildStatusAsync();
        }
        catch (Exception exception)
        {
            _logger?.Write(
                AppLogLevel.Warning,
                "dl1_build_fingerprint_unexpected_failure",
                "Installed DL1 build detection failed unexpectedly.",
                exception: exception);
        }
    }

    private async Task InitializeAssetCatalogAsync(
        MainWindowViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAssetCatalogAsync();
        }
        catch (Exception exception)
        {
            // The ViewModel normally reports expected discovery, I/O, and
            // validation failures in its diagnostics drawer. Keep this final
            // guard for unexpected startup failures without taking down WPF.
            _logger?.Write(
                AppLogLevel.Warning,
                "dl1_asset_catalog_unexpected_failure",
                "The saved Dying Light 1 asset catalog could not be opened unexpectedly.",
                exception: exception);
        }
    }
}
