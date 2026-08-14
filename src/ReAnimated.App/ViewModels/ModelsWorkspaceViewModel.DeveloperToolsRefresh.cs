using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Models;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private static readonly IReadOnlyList<DeveloperToolsAnimationRefreshChoice<DeveloperToolsAnimationRefreshRoute>>
        AnimationRefreshRoutesValue =
        [
            new(
                DeveloperToolsAnimationRefreshRoute.ProjectRPack,
                "Project runtime RPack (recommended)",
                "Registers the hash-verified project-owned type-322/type-320 runtime pack and retains it for playback."),
            new(
                DeveloperToolsAnimationRefreshRoute.RawLoose,
                "Raw loose files (advanced)",
                "Editor-only comparison route. Set the loader's local [AnimationRefresh] Route=RawLoose before writing the request; raw parsing remains scoped to the selected model."),
        ];

    private static readonly IReadOnlyList<DeveloperToolsAnimationRefreshChoice<DeveloperToolsAnimationRefreshHost>>
        AnimationRefreshHostsValue =
        [
            new(
                DeveloperToolsAnimationRefreshHost.Editor,
                "Developer Tools Editor",
                "Refreshes the selected Editor project after you start it."),
            new(
                DeveloperToolsAnimationRefreshHost.Player,
                "DyingLightPlayer",
                "Requests Player resolution for your separate manual test; ReAnimated never starts it."),
        ];

    private DeveloperToolsAnimationRefreshChoice<DeveloperToolsAnimationRefreshRoute>
        _selectedAnimationRefreshRoute = AnimationRefreshRoutesValue[0];
    private DeveloperToolsAnimationRefreshChoice<DeveloperToolsAnimationRefreshHost>
        _selectedAnimationRefreshHost = AnimationRefreshHostsValue[0];
    private DeveloperToolsAnimationRefreshRequestResult? _lastAnimationRefreshRequest;
    private CancellationTokenSource? _animationRefreshMonitorCancellation;
    private string _animationRefreshStatus =
        "No refresh request written. Complete a schema-2 deployment first.";
    private string _animationRefreshResultDetails = string.Empty;
    private string _animationLoaderLogPath = string.Empty;
    private string _animationDiagnosticStatus =
        "Collect existing deployment receipts, refresh records, and an optional loader log.";

    public IReadOnlyList<DeveloperToolsAnimationRefreshChoice<DeveloperToolsAnimationRefreshRoute>>
        AnimationRefreshRoutes { get; } = AnimationRefreshRoutesValue;

    public IReadOnlyList<DeveloperToolsAnimationRefreshChoice<DeveloperToolsAnimationRefreshHost>>
        AnimationRefreshHosts { get; } = AnimationRefreshHostsValue;

    public DeveloperToolsAnimationRefreshChoice<DeveloperToolsAnimationRefreshRoute>
        SelectedAnimationRefreshRoute
    {
        get => _selectedAnimationRefreshRoute;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedAnimationRefreshRoute, value))
            {
                OnPropertyChanged(nameof(AnimationRefreshRouteDescription));
            }
        }
    }

    public DeveloperToolsAnimationRefreshChoice<DeveloperToolsAnimationRefreshHost>
        SelectedAnimationRefreshHost
    {
        get => _selectedAnimationRefreshHost;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetProperty(ref _selectedAnimationRefreshHost, value);
        }
    }

    public string AnimationRefreshRouteDescription =>
        SelectedAnimationRefreshRoute.Description;

    public string AnimationRefreshStatus
    {
        get => _animationRefreshStatus;
        private set => SetProperty(ref _animationRefreshStatus, value);
    }

    public string AnimationRefreshResultDetails
    {
        get => _animationRefreshResultDetails;
        private set => SetProperty(ref _animationRefreshResultDetails, value);
    }

    public string AnimationLoaderLogStatus => File.Exists(_animationLoaderLogPath)
        ? _animationLoaderLogPath
        : "No existing dl_universal_loader.log selected (optional).";

    public string AnimationDiagnosticStatus
    {
        get => _animationDiagnosticStatus;
        private set => SetProperty(ref _animationDiagnosticStatus, value);
    }

    public IRelayCommand RequestDeveloperToolsAnimationRefreshCommand { get; private set; } = null!;

    public IRelayCommand CheckDeveloperToolsAnimationRefreshResultCommand { get; private set; } = null!;

    public IRelayCommand SelectAnimationLoaderLogCommand { get; private set; } = null!;

    public IRelayCommand CollectAnimationDiagnosticBundleCommand { get; private set; } = null!;

    private void InitializeAnimationRefreshCommands()
    {
        RequestDeveloperToolsAnimationRefreshCommand = new RelayCommand(
            WriteAnimationRefreshRequest,
            () => Directory.Exists(DeveloperToolsProjectRoot) && !IsBusy);
        CheckDeveloperToolsAnimationRefreshResultCommand = new RelayCommand(
            CheckAnimationRefreshResult,
            () => _lastAnimationRefreshRequest is not null && !IsBusy);
        SelectAnimationLoaderLogCommand = new RelayCommand(
            SelectAnimationLoaderLog,
            () => !IsBusy);
        CollectAnimationDiagnosticBundleCommand = new RelayCommand(
            CollectAnimationDiagnosticBundle,
            () => Directory.Exists(DeveloperToolsProjectRoot) &&
                  !IsBusy);
    }

    private void WriteAnimationRefreshRequest()
    {
        try
        {
            Dl1DeveloperToolsDeploymentReceipt receipt =
                Dl1DeveloperToolsProjectDeployer.LoadLatestActiveReceipt(
                    DeveloperToolsProjectRoot) ??
                throw new InvalidOperationException(
                    "No active schema-2 Developer Tools deployment is available.");
            BeginAnimationRefreshRequest(
                DeveloperToolsProjectRoot,
                receipt,
                SelectedAnimationRefreshHost.Value,
                SelectedAnimationRefreshRoute.Value,
                "Refresh/retry");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
            InvalidOperationException or IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            AnimationRefreshStatus =
                $"Animation refresh request was not written: {exception.Message} " +
                "Live playback remains unproven.";
        }

        CheckDeveloperToolsAnimationRefreshResultCommand.NotifyCanExecuteChanged();
        _setStatus(AnimationRefreshStatus);
    }

    internal void QueueAutomaticDeveloperToolsAnimationRefresh(
        string projectRoot,
        Dl1DeveloperToolsDeploymentReceipt receipt)
    {
        try
        {
            BeginAnimationRefreshRequest(
                projectRoot,
                receipt,
                DeveloperToolsAnimationRefreshHost.Editor,
                SelectedAnimationRefreshRoute.Value,
                "Automatic post-deployment refresh");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
            InvalidOperationException or IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            AnimationRefreshStatus =
                $"Deployment completed, but its automatic Editor refresh request was not written: " +
                $"{exception.Message} Live playback remains unproven.";
            AnimationRefreshResultDetails = string.Empty;
        }

        CheckDeveloperToolsAnimationRefreshResultCommand.NotifyCanExecuteChanged();
    }

    private void BeginAnimationRefreshRequest(
        string projectRoot,
        Dl1DeveloperToolsDeploymentReceipt receipt,
        DeveloperToolsAnimationRefreshHost host,
        DeveloperToolsAnimationRefreshRoute route,
        string trigger)
    {
        StopAnimationRefreshMonitor();
        _lastAnimationRefreshRequest = null;
        _lastAnimationRefreshRequest =
            DeveloperToolsAnimationRefreshService.WriteRequest(
                projectRoot,
                receipt,
                host,
                route);
        string hostLabel = AnimationRefreshHostsValue.Single(choice => choice.Value == host).Label;
        string routeLabel = AnimationRefreshRoutesValue.Single(choice => choice.Value == route).Label;
        AnimationRefreshStatus =
            $"{trigger} request {_lastAnimationRefreshRequest.RequestId} written for " +
            $"{hostLabel} using {routeLabel}; it expires " +
            $"{_lastAnimationRefreshRequest.ExpiresUtc.ToLocalTime():g}. " +
            "Waiting for the loader result; live playback remains unproven.";
        AnimationRefreshResultDetails = string.Empty;
        StartAnimationRefreshMonitor(_lastAnimationRefreshRequest);
    }

    private void CheckAnimationRefreshResult()
    {
        if (_lastAnimationRefreshRequest is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(_lastAnimationRefreshRequest.ResultPath))
            {
                AnimationRefreshStatus =
                    "No loader result exists for the pending request. This does not " +
                    "indicate success or failure; live playback remains unproven.";
                AnimationRefreshResultDetails = string.Empty;
                return;
            }

            DeveloperToolsAnimationRefreshResultSummary result =
                DeveloperToolsAnimationRefreshService.ReadResult(
                    _lastAnimationRefreshRequest);
            AnimationRefreshStatus =
                $"Loader result: {result.Status}" +
                (result.FailureStage is null
                    ? ". "
                    : $"; first failed stage: {result.FailureStage}. ") +
                (result.AnimationNameCount is { } animationNameCount
                    ? $"Resolved {animationNameCount:N0} animation name(s). "
                    : string.Empty) +
                (result.RefusalReason is null
                    ? string.Empty
                    : $"Refusal: {result.RefusalReason}. ") +
                "This receipt does not prove that the animation list populated or playback occurred.";
            AnimationRefreshResultDetails = result.Details;
            StopAnimationRefreshMonitor();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            AnimationRefreshStatus =
                $"Loader result was rejected: {exception.Message} " +
                "Live playback remains unproven.";
            AnimationRefreshResultDetails = string.Empty;
            StopAnimationRefreshMonitor();
        }

        _setStatus(AnimationRefreshStatus);
    }

    private void StartAnimationRefreshMonitor(
        DeveloperToolsAnimationRefreshRequestResult request)
    {
        _animationRefreshMonitorCancellation = new CancellationTokenSource();
        _ = MonitorAnimationRefreshResultAsync(
            request,
            _animationRefreshMonitorCancellation.Token);
    }

    private async Task MonitorAnimationRefreshResultAsync(
        DeveloperToolsAnimationRefreshRequestResult request,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   !_disposed &&
                   DateTimeOffset.UtcNow <= request.ExpiresUtc)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                if (!ReferenceEquals(_lastAnimationRefreshRequest, request))
                {
                    return;
                }

                if (File.Exists(request.ResultPath))
                {
                    CheckAnimationRefreshResult();
                    return;
                }
            }

            if (!cancellationToken.IsCancellationRequested &&
                ReferenceEquals(_lastAnimationRefreshRequest, request))
            {
                AnimationRefreshStatus =
                    "The refresh request expired without a loader result. Use Refresh/retry; " +
                    "live playback remains unproven.";
                _setStatus(AnimationRefreshStatus);
                StopAnimationRefreshMonitor();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StopAnimationRefreshMonitor()
    {
        _animationRefreshMonitorCancellation?.Cancel();
        _animationRefreshMonitorCancellation?.Dispose();
        _animationRefreshMonitorCancellation = null;
    }

    private void SelectAnimationLoaderLog()
    {
        string? path = _fileDialogs.ShowOpenDeveloperToolsAnimationLoaderLogDialog(
            _animationLoaderLogPath);
        if (path is null)
        {
            return;
        }

        _animationLoaderLogPath = Path.GetFullPath(path);
        OnPropertyChanged(nameof(AnimationLoaderLogStatus));
        AnimationDiagnosticStatus =
            "Selected an existing loader log for the next bundle.";
    }

    private void CollectAnimationDiagnosticBundle()
    {
        string? outputPath =
            _fileDialogs.ShowSaveDeveloperToolsAnimationDiagnosticBundleDialog(
                DeveloperToolsProjectRoot);
        if (outputPath is null)
        {
            return;
        }

        try
        {
            DeveloperToolsAnimationDiagnosticBundleResult result =
                DeveloperToolsAnimationRefreshService.CreateDiagnosticBundle(
                DeveloperToolsProjectRoot,
                outputPath,
                string.IsNullOrWhiteSpace(_animationLoaderLogPath)
                    ? null
                    : _animationLoaderLogPath);
            AnimationDiagnosticStatus =
                $"Collected {result.CollectedFileCount:N0} existing receipt/log " +
                $"file(s) ({result.CollectedByteCount:N0} bytes) into " +
                $"{result.OutputPath}. No process was launched or inspected; " +
                "live playback remains unproven.";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
            InvalidOperationException or IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            AnimationDiagnosticStatus =
                $"Diagnostic bundle was not created: {exception.Message}";
        }

        _setStatus(AnimationDiagnosticStatus);
    }

    private void ResetAnimationRefreshState(string status)
    {
        StopAnimationRefreshMonitor();
        _lastAnimationRefreshRequest = null;
        AnimationRefreshStatus = status;
        AnimationRefreshResultDetails = string.Empty;
        CheckDeveloperToolsAnimationRefreshResultCommand?
            .NotifyCanExecuteChanged();
        NotifyAnimationRefreshCommands();
    }

    private void RestoreLatestAnimationRefreshState(
        Dl1DeveloperToolsDeploymentReceipt? activeReceipt)
    {
        StopAnimationRefreshMonitor();
        _lastAnimationRefreshRequest = null;
        AnimationRefreshResultDetails = string.Empty;
        if (activeReceipt is null || !Directory.Exists(DeveloperToolsProjectRoot))
        {
            AnimationRefreshStatus =
                "No active schema-2 deployment receipt is available for animation refresh.";
            CheckDeveloperToolsAnimationRefreshResultCommand.NotifyCanExecuteChanged();
            return;
        }

        try
        {
            DeveloperToolsAnimationRefreshRequestResult? request =
                DeveloperToolsAnimationRefreshService.LoadLatestRequest(
                    DeveloperToolsProjectRoot);
            if (request is null)
            {
                AnimationRefreshStatus =
                    "The active deployment is ready. No prior animation refresh request was found.";
                CheckDeveloperToolsAnimationRefreshResultCommand.NotifyCanExecuteChanged();
                return;
            }

            if (!string.Equals(
                    request.DeploymentId,
                    activeReceipt.DeploymentId,
                    StringComparison.Ordinal))
            {
                AnimationRefreshStatus =
                    "The latest animation refresh request belongs to an older deployment. Use Refresh / retry to write a request for the active receipt.";
                CheckDeveloperToolsAnimationRefreshResultCommand.NotifyCanExecuteChanged();
                return;
            }

            _lastAnimationRefreshRequest = request;
            _selectedAnimationRefreshHost = AnimationRefreshHostsValue.Single(
                choice => choice.Value == request.TargetHost);
            _selectedAnimationRefreshRoute = AnimationRefreshRoutesValue.Single(
                choice => choice.Value == request.Route);
            OnPropertyChanged(nameof(SelectedAnimationRefreshHost));
            OnPropertyChanged(nameof(SelectedAnimationRefreshRoute));
            OnPropertyChanged(nameof(AnimationRefreshRouteDescription));
            CheckDeveloperToolsAnimationRefreshResultCommand.NotifyCanExecuteChanged();
            if (File.Exists(request.ResultPath))
            {
                CheckAnimationRefreshResult();
                return;
            }

            AnimationRefreshStatus = request.ExpiresUtc > DateTimeOffset.UtcNow
                ? $"Restored pending refresh request {request.RequestId}; waiting for the loader result until {request.ExpiresUtc.ToLocalTime():g}."
                : $"Restored expired refresh request {request.RequestId}. Use Refresh / retry to write a current request.";
            if (request.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                StartAnimationRefreshMonitor(request);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            AnimationRefreshStatus =
                $"Existing animation refresh state was rejected: {exception.Message}";
            AnimationRefreshResultDetails = string.Empty;
            _lastAnimationRefreshRequest = null;
            CheckDeveloperToolsAnimationRefreshResultCommand.NotifyCanExecuteChanged();
        }
    }

    private void NotifyAnimationRefreshCommands()
    {
        RequestDeveloperToolsAnimationRefreshCommand?.NotifyCanExecuteChanged();
        CheckDeveloperToolsAnimationRefreshResultCommand?.NotifyCanExecuteChanged();
        SelectAnimationLoaderLogCommand?.NotifyCanExecuteChanged();
        CollectAnimationDiagnosticBundleCommand?.NotifyCanExecuteChanged();
    }
}
