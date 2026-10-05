using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Automation;

namespace ReAnimated.App.Infrastructure;

internal sealed class AppControlServer : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AppControlInstance _instance = AppControlTransport.CreateInstance();
    private readonly WindowHost _host;
    private readonly string _discoveryPath;
    private NamedPipeServerStream? _activePipe;
    private bool _disposed;

    public AppControlServer(Window window, MainWindowViewModel viewModel, Func<bool> isReady)
    {
        _host = new WindowHost(window, viewModel, _instance, isReady);
        // Create the protected endpoint before publishing discovery.
        _activePipe = CreatePipe();
        try { _discoveryPath = AppControlTransport.Publish(_instance); }
        catch
        {
            _activePipe.Dispose();
            _shutdown.Dispose();
            throw;
        }
        _ = RunAsync(_activePipe);
    }

    private NamedPipeServerStream CreatePipe() => new(_instance.PipeName,
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);

    private async Task RunAsync(NamedPipeServerStream initialPipe)
    {
        NamedPipeServerStream pipe = initialPipe;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using (pipe)
                {
                    await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    timeout.CancelAfter(AppControlTransport.RequestTimeout);
                    _host.CloseRequested = false;
                    try
                    {
                        AppControlRequest request = await AppControlTransport.ReadAsync<AppControlRequest>(
                            pipe, timeout.Token).ConfigureAwait(false);
                        AppControlResponse response = AppControlTransport.Authenticate(_instance, request)
                            ? await AppControlHandler.ExecuteAsync(_host, request.Command,
                                request.OutputPath, request.ProjectPath, timeout.Token).ConfigureAwait(false)
                            : new(false, "App authentication failed.", null);
                        await AppControlTransport.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
                        if (response.Success && _host.CloseRequested) _host.DispatchClose();
                    }
                    catch (Exception exception) when (exception is IOException or JsonException
                        or ArgumentException or InvalidOperationException or UnauthorizedAccessException
                        or FormatException or OperationCanceledException)
                    {
                        _host.CloseRequested = false;
                        if (!_shutdown.IsCancellationRequested && pipe.IsConnected)
                        {
                            try
                            {
                                using var errorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                                await AppControlTransport.WriteAsync(pipe,
                                    new AppControlResponse(false,
                                        exception switch
                                        {
                                            OperationCanceledException => "App request cancelled.",
                                            ArgumentException or InvalidOperationException => exception.Message,
                                            _ => "App request failed.",
                                        },
                                        null), errorTimeout.Token).ConfigureAwait(false);
                            }
                            catch (Exception writeException) when (writeException is IOException
                                or OperationCanceledException or ObjectDisposedException) { }
                        }
                    }
                }
                if (_shutdown.IsCancellationRequested) break;
                pipe = CreatePipe();
                _activePipe = pipe;
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
            or ObjectDisposedException) { }
        finally
        {
            pipe.Dispose();
            RemoveDiscovery();
        }
    }

    private void RemoveDiscovery()
    {
        try { File.Delete(_discoveryPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _activePipe?.Dispose();
        _shutdown.Dispose();
        RemoveDiscovery();
        GC.SuppressFinalize(this);
    }

    private sealed class WindowHost(Window window, MainWindowViewModel viewModel,
        AppControlInstance instance, Func<bool> isReady) : IAppControlHost
    {
        public bool CloseRequested { get; set; }

        public async Task<AppControlStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            await window.Dispatcher.InvokeAsync(() => new AppControlStatus(instance.InstanceId,
                instance.ProcessId, viewModel.CurrentProject.Name, viewModel.ProjectPath,
                viewModel.HasAppControlUnsavedChanges,
                !isReady() || viewModel.IsBusy || viewModel.Models.IsBusy)
            {
                ActiveWorkflow = viewModel.ActiveWorkspace.ToString(),
                ActivePage = viewModel.IsWelcomeWorkflowVisible ? "Welcome"
                    : viewModel.IsCustomModelAuthoringSurfaceVisible ? "ModelAuthoring"
                    : viewModel.IsRetailModelBrowserSurfaceVisible ? "ModelBrowser"
                    : viewModel.ActiveWorkspace.ToString(),
                GuidedStep = !viewModel.IsWelcomeWorkflowVisible && viewModel.IsCustomModelAuthoringSurfaceVisible
                    ? viewModel.Models.GuidedStep.ToString() : null,
                ProjectId = viewModel.CurrentProject.ProjectId,
                SelectedModelId = viewModel.SelectedProjectModel?.ModelId,
                SelectedAnimationId = viewModel.SelectedAnimationLibraryItem?.Id,
                ActiveAnimationId = viewModel.CurrentProject.ActiveAnimationId,
            },
                DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);

        public async Task SaveAsync(string outputPath, CancellationToken cancellationToken)
        {
            Task save = await window.Dispatcher.InvokeAsync(
                () => viewModel.SaveWorkspaceToNewPathAsync(outputPath, cancellationToken),
                DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
            await save.ConfigureAwait(false);
        }

        public void ScheduleClose() => CloseRequested = true;

        public async Task OpenAsync(string projectPath, CancellationToken cancellationToken)
        {
            Task open = await window.Dispatcher.InvokeAsync(
                () => viewModel.OpenWorkspaceForAppControlAsync(projectPath, cancellationToken),
                DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
            await open.ConfigureAwait(false);
        }

        public void DispatchClose() => window.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
            new Action(() =>
            {
                if (isReady() && !viewModel.IsBusy && !viewModel.Models.IsBusy &&
                    !viewModel.HasAppControlUnsavedChanges && window.IsVisible) window.Close();
            }));
    }
}
