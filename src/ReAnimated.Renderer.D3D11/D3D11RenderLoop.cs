using System.Diagnostics;
using System.Numerics;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Renderer.D3D11;

internal sealed class D3D11RenderLoop : IDisposable
{
    private static readonly FeatureLevel[] RequestedFeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private readonly IntPtr _windowHandle;
    private readonly Func<IRenderSceneSource?> _sceneSource;
    private readonly Func<IReadOnlyList<ID3D11RenderPass>> _renderPasses;
    private readonly Action<D3D11RendererStatus> _statusSink;
    private readonly Action<string> _diagnosticSink;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly D3D11EditorRenderPipeline _editorPipeline = new();
    private readonly Thread _thread;
    private readonly RendererViewportSizeMailbox _requestedSize;
    private readonly RendererAdapterRefreshSignal _adapterRefresh = new();
    private bool _disposed;

    public D3D11RenderLoop(
        IntPtr windowHandle,
        int width,
        int height,
        Func<IRenderSceneSource?> sceneSource,
        Func<IReadOnlyList<ID3D11RenderPass>> renderPasses,
        Action<D3D11RendererStatus> statusSink,
        Action<string> diagnosticSink)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(windowHandle, IntPtr.Zero);
        _windowHandle = windowHandle;
        _requestedSize = new RendererViewportSizeMailbox(
            width,
            height);
        _sceneSource = sceneSource ?? throw new ArgumentNullException(nameof(sceneSource));
        _renderPasses = renderPasses ?? throw new ArgumentNullException(nameof(renderPasses));
        _statusSink = statusSink ?? throw new ArgumentNullException(nameof(statusSink));
        _diagnosticSink = diagnosticSink ?? throw new ArgumentNullException(nameof(diagnosticSink));
        _thread = new Thread(RenderThreadMain)
        {
            IsBackground = true,
            Name = "ReAnimated D3D11 Render Loop",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thread.Start();
    }

    public void RequestResize(int width, int height)
    {
        _requestedSize.Publish(width, height);
    }

    public void RequestAdapterRefresh()
    {
        _adapterRefresh.RequestRefresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopSource.Cancel();
        bool stopped = !_thread.IsAlive
            || _thread.Join(TimeSpan.FromSeconds(3));
        if (!stopped)
        {
            _diagnosticSink("The D3D11 render loop did not stop inside the shutdown window.");
        }
        else
        {
            _stopSource.Dispose();
        }
    }

    private void RenderThreadMain()
    {
        bool faulted = false;
        bool forceWarp = false;
        RendererRecoveryPolicy recoveryPolicy = new();
        long adapterGeneration =
            _adapterRefresh.CaptureGeneration();
        _statusSink(new D3D11RendererStatus(
            RendererLifecycleState.Starting,
            null,
            "Creating D3D11 device…",
            0.0,
            0));

        while (!_stopSource.IsCancellationRequested)
        {
            if (_adapterRefresh.HasChanged(adapterGeneration))
            {
                adapterGeneration =
                    _adapterRefresh.CaptureGeneration();
                forceWarp = false;
                _ = recoveryPolicy.RecordFailure(
                    RendererAdapterMode.Hardware,
                    presentedFrames: 0,
                    RendererRecoverableFailureKind
                        .DisplayConfigurationChanged);
                _diagnosticSink(
                    "Display configuration changed during renderer recovery; retrying the hardware adapter.");
            }

            try
            {
                using DeviceResources resources =
                    CreateDeviceResources(forceWarp);
                RunDeviceLoop(
                    resources,
                    adapterGeneration);
                break;
            }
            catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
            {
                break;
            }
            catch (DeviceLostException exception)
            {
                _diagnosticSink(exception.Message);
                RendererRecoveryDecision recovery =
                    recoveryPolicy.RecordFailure(
                        exception.AdapterMode,
                        exception.PresentedFrames,
                        exception.Failure.Kind);
                if (exception.Failure.Kind ==
                    RendererRecoverableFailureKind
                        .DisplayConfigurationChanged)
                {
                    adapterGeneration =
                        _adapterRefresh.CaptureGeneration();
                }
                if (!recovery.ShouldRetry)
                {
                    faulted = true;
                    _diagnosticSink(recovery.Message);
                    _statusSink(new D3D11RendererStatus(
                        RendererLifecycleState.Faulted,
                        exception.AdapterMode,
                        recovery.Message,
                        0.0,
                        exception.PresentedFrames));
                    break;
                }

                forceWarp = recovery.ForceWarp;
                _diagnosticSink(recovery.Message);
                _statusSink(new D3D11RendererStatus(
                    RendererLifecycleState.Recovering,
                    exception.AdapterMode,
                    recovery.Message,
                    0.0,
                    exception.PresentedFrames));

                if (_stopSource.Token.WaitHandle.WaitOne(recovery.Delay))
                {
                    break;
                }

            }
            catch (Exception exception)
            {
                faulted = true;
                _diagnosticSink($"Renderer fault: {exception}");
                _statusSink(new D3D11RendererStatus(
                    RendererLifecycleState.Faulted,
                    null,
                    "D3D11 viewport unavailable. See diagnostics.",
                    0.0,
                    0));
                break;
            }
        }

        _editorPipeline.Dispose();
        if (!faulted)
        {
            _statusSink(new D3D11RendererStatus(
                RendererLifecycleState.Stopped,
                null,
                "Renderer stopped",
                0.0,
                0));
        }
    }

    private DeviceResources CreateDeviceResources(bool forceWarp)
    {
        List<string> failures = [];
        RendererDeviceFailure? recoverableFailure = null;
        RendererAdapterMode recoverableAdapter =
            RendererAdapterMode.Hardware;
        foreach (RendererAdapterMode mode in
                 RendererDeviceSelectionPolicy.GetAttempts(
                      allowWarpFallback: true,
                     forceWarp))
        {
            Result result = TryCreateDeviceResources(mode, out DeviceResources? resources);
            if (result.Success && resources is not null)
            {
                if (mode == RendererAdapterMode.Warp)
                {
                    _diagnosticSink(forceWarp
                        ? "Using the WARP diagnostic adapter for renderer recovery."
                        : "Hardware D3D11 initialization failed; using the WARP software adapter.");
                }

                return resources;
            }

            failures.Add($"{mode}: 0x{result.Code:X8}");
            resources?.Dispose();
            if (RendererDeviceFailureClassifier.TryClassify(
                    result,
                    out RendererDeviceFailure failure))
            {
                if (recoverableFailure is null ||
                    GetRecoveryPriority(failure.Kind) >
                    GetRecoveryPriority(
                        recoverableFailure.Value.Kind))
                {
                    recoverableFailure = failure;
                    recoverableAdapter = mode;
                }
            }
        }

        string message =
            $"Unable to create a D3D11 device and swap chain ({string.Join(", ", failures)}).";
        if (recoverableFailure is { } transientFailure)
        {
            throw new DeviceLostException(
                recoverableAdapter,
                presentedFrames: 0,
                transientFailure,
                message);
        }

        throw new InvalidOperationException(message);
    }

    private static int GetRecoveryPriority(
        RendererRecoverableFailureKind kind) =>
        kind switch
        {
            RendererRecoverableFailureKind
                .RemoteSessionUnavailable => 3,
            RendererRecoverableFailureKind
                .DisplayConfigurationChanged => 2,
            _ => 1,
        };

    private Result TryCreateDeviceResources(
        RendererAdapterMode mode,
        out DeviceResources? resources)
    {
        RendererViewportSize requestedSize =
            _requestedSize.Read();
        int width = requestedSize.Width;
        int height = requestedSize.Height;
        SwapChainDescription description = new()
        {
            BufferCount = 2,
            BufferDescription = new ModeDescription(
                (uint)width,
                (uint)height,
                new Rational(60, 1),
                Format.B8G8R8A8_UNorm),
            BufferUsage = Usage.RenderTargetOutput,
            OutputWindow = _windowHandle,
            SampleDescription = new SampleDescription(1, 0),
            Windowed = true,
            SwapEffect = SwapEffect.FlipDiscard,
            Flags = SwapChainFlags.None,
        };

        DriverType driverType = mode == RendererAdapterMode.Hardware
            ? DriverType.Hardware
            : DriverType.Warp;
        // Vortice's nullable FeatureLevel output can remain null even when
        // the native call succeeds. Device.GetFeatureLevel is authoritative
        // once the device exists, so the nullable wrapper output must not be
        // treated as a creation failure.
        Result result = D3D11CreateDeviceAndSwapChain(
            null,
            driverType,
            DeviceCreationFlags.BgraSupport,
            RequestedFeatureLevels,
            description,
            out IDXGISwapChain? swapChain,
            out ID3D11Device? device,
            out FeatureLevel? wrapperFeatureLevel,
            out ID3D11DeviceContext? context);

        if (result.Failure
            || swapChain is null
            || device is null
            || context is null)
        {
            swapChain?.Dispose();
            device?.Dispose();
            context?.Dispose();
            resources = null;
            return result;
        }

        DeviceResources? createdResources = null;
        try
        {
            FeatureLevel featureLevel = device.FeatureLevel;
            if (wrapperFeatureLevel is FeatureLevel reportedFeatureLevel &&
                reportedFeatureLevel != featureLevel)
            {
                _diagnosticSink(
                    $"Vortice reported feature level {reportedFeatureLevel}, but the created device reports {featureLevel}; using the device value.");
            }

            createdResources = new DeviceResources(
                mode,
                featureLevel,
                device,
                context,
                swapChain,
                width,
                height);
            createdResources.CreateTargets();
            resources = createdResources;
            return result;
        }
        catch (Exception exception)
        {
            Result deviceRemovedReason =
                ReadDeviceRemovedReason(device);
            if (createdResources is not null)
            {
                createdResources.Dispose();
            }
            else
            {
                swapChain.Dispose();
                device.Dispose();
                context.Dispose();
            }

            resources = null;
            if (RendererDeviceFailureClassifier.TryClassify(
                    exception,
                    deviceRemovedReason,
                    out RendererDeviceFailure failure))
            {
                return failure.Result;
            }

            throw;
        }
    }

    private void RunDeviceLoop(
        DeviceResources resources,
        long adapterGeneration)
    {
        long frameNumber = 0;
        long framesAtLastSample = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool publishResizeStatusAfterPresent = false;
        TimeSpan lastSample = TimeSpan.Zero;
        _statusSink(new D3D11RendererStatus(
            RendererLifecycleState.Ready,
            resources.AdapterMode,
            $"{resources.AdapterMode} · D3D {resources.FeatureLevel}",
            0.0,
            0));

        while (!_stopSource.IsCancellationRequested)
        {
            if (_adapterRefresh.HasChanged(adapterGeneration))
            {
                throw new DeviceLostException(
                    resources.AdapterMode,
                    frameNumber,
                    new RendererDeviceFailure(
                        RendererRecoverableFailureKind
                            .DisplayConfigurationChanged,
                        Vortice.DXGI.ResultCode.NotCurrent),
                    "Windows reported a display configuration change.");
            }

            RendererViewportSize requestedSize =
                _requestedSize.Read();
            int width = requestedSize.Width;
            int height = requestedSize.Height;
            if (width != resources.Width || height != resources.Height)
            {
                Result resizeResult;
                try
                {
                    resizeResult = resources.Resize(
                        width,
                        height);
                }
                catch (Exception exception)
                {
                    if (TryCreateDeviceLostException(
                            resources,
                            frameNumber,
                            "resize target recreation",
                            exception,
                            out DeviceLostException deviceLoss))
                    {
                        throw deviceLoss;
                    }

                    throw;
                }

                if (resizeResult.Failure)
                {
                    if (TryCreateDeviceLostException(
                            resources,
                            frameNumber,
                            "resize",
                            resizeResult,
                            out DeviceLostException deviceLoss))
                    {
                        throw deviceLoss;
                    }

                    throw new InvalidOperationException(
                        $"DXGI resize failed with non-recoverable HRESULT 0x{resizeResult.Code:X8}.");
                }

                publishResizeStatusAfterPresent = true;
            }

            RenderFrameSnapshot frame = CaptureFrameSafely();
            RenderFrame(resources, frame, frameNumber);
            Result deviceRemovedReason =
                ReadDeviceRemovedReason(resources.Device);
            if (RendererDeviceFailureClassifier.TryClassify(
                    deviceRemovedReason,
                    out RendererDeviceFailure renderFailure))
            {
                throw new DeviceLostException(
                    resources.AdapterMode,
                    frameNumber,
                    renderFailure,
                    $"The D3D11 device entered a removed state after rendering frame {frameNumber:N0}.");
            }

            Result presentResult = resources.SwapChain.Present(1, PresentFlags.None);
            if (presentResult.Failure)
            {
                if (TryCreateDeviceLostException(
                        resources,
                        frameNumber,
                        "present",
                        presentResult,
                        out DeviceLostException deviceLoss))
                {
                    throw deviceLoss;
                }

                throw new InvalidOperationException(
                    $"DXGI present failed with non-recoverable HRESULT 0x{presentResult.Code:X8}.");
            }

            frameNumber++;
            if (publishResizeStatusAfterPresent)
            {
                _statusSink(new D3D11RendererStatus(
                    RendererLifecycleState.Ready,
                    resources.AdapterMode,
                    "Renderer ready after viewport resize.",
                    0.0,
                    frameNumber)
                {
                    ViewportPixelWidth = resources.Width,
                    ViewportPixelHeight = resources.Height,
                });
                publishResizeStatusAfterPresent = false;
            }

            TimeSpan elapsed = stopwatch.Elapsed;
            TimeSpan sampleDuration = elapsed - lastSample;
            if (sampleDuration >= TimeSpan.FromSeconds(1))
            {
                long sampleFrames = frameNumber - framesAtLastSample;
                double fps = sampleFrames / sampleDuration.TotalSeconds;
                _statusSink(new D3D11RendererStatus(
                    RendererLifecycleState.Ready,
                    resources.AdapterMode,
                    $"{resources.AdapterMode} · D3D {resources.FeatureLevel}",
                    fps,
                    frameNumber)
                {
                    ViewportPixelWidth = resources.Width,
                    ViewportPixelHeight = resources.Height,
                });
                lastSample = elapsed;
                framesAtLastSample = frameNumber;
            }
        }
    }

    private RenderFrameSnapshot CaptureFrameSafely()
    {
        try
        {
            return _sceneSource()?.CaptureFrame() ?? RenderFrameSnapshot.Empty();
        }
        catch (Exception exception)
        {
            _diagnosticSink($"Scene snapshot failed: {exception.Message}");
            return RenderFrameSnapshot.Empty(new Vector4(0.12f, 0.025f, 0.035f, 1.0f));
        }
    }

    private void RenderFrame(
        DeviceResources resources,
        RenderFrameSnapshot frame,
        long frameNumber)
    {
        ID3D11RenderTargetView renderTarget = resources.RenderTarget
            ?? throw new InvalidOperationException("The render target is not initialized.");
        ID3D11DepthStencilView depthStencil = resources.DepthStencil
            ?? throw new InvalidOperationException("The depth target is not initialized.");
        Vector4 clear = Vector4.Clamp(frame.ClearColor, Vector4.Zero, Vector4.One);
        resources.Context.OMSetRenderTargets(renderTarget, depthStencil);
        resources.Context.RSSetViewport(new Viewport(
            0.0f,
            0.0f,
            resources.Width,
            resources.Height,
            0.0f,
            1.0f));
        resources.Context.ClearRenderTargetView(
            renderTarget,
            new Color4(clear.X, clear.Y, clear.Z, clear.W));
        resources.Context.ClearDepthStencilView(
            depthStencil,
            DepthStencilClearFlags.Depth,
            1.0f,
            0);

        D3D11RenderFrameContext context = new(
            resources.Device,
            resources.Context,
            renderTarget,
            depthStencil,
            resources.Width,
            resources.Height,
            frameNumber,
            _diagnosticSink);
        foreach (ID3D11RenderPass pass in _editorPipeline.Passes)
        {
            RenderPassSafely(
                pass,
                resources,
                in context,
                frame);
        }

        foreach (ID3D11RenderPass pass in _renderPasses())
        {
            RenderPassSafely(
                pass,
                resources,
                in context,
                frame);
        }
    }

    private void RenderPassSafely(
        ID3D11RenderPass pass,
        DeviceResources resources,
        in D3D11RenderFrameContext context,
        RenderFrameSnapshot frame)
    {
        try
        {
            pass.Render(in context, frame);
        }
        catch (Exception exception)
        {
            if (TryCreateDeviceLostException(
                    resources,
                    context.FrameNumber,
                    $"render pass '{pass.Name}'",
                    exception,
                    out DeviceLostException deviceLoss))
            {
                throw deviceLoss;
            }

            _diagnosticSink($"Render pass '{pass.Name}' failed: {exception.Message}");
        }
    }

    private static bool TryCreateDeviceLostException(
        DeviceResources resources,
        long presentedFrames,
        string operation,
        Result operationResult,
        out DeviceLostException exception)
    {
        Result deviceRemovedReason =
            ReadDeviceRemovedReason(resources.Device);
        if (!RendererDeviceFailureClassifier.TryClassify(
                operationResult,
                deviceRemovedReason,
                out RendererDeviceFailure failure))
        {
            exception = null!;
            return false;
        }

        exception = new DeviceLostException(
            resources.AdapterMode,
            presentedFrames,
            failure,
            BuildDeviceFailureMessage(
                operation,
                operationResult,
                deviceRemovedReason));
        return true;
    }

    private static bool TryCreateDeviceLostException(
        DeviceResources resources,
        long presentedFrames,
        string operation,
        Exception cause,
        out DeviceLostException exception)
    {
        Result deviceRemovedReason =
            ReadDeviceRemovedReason(resources.Device);
        if (!RendererDeviceFailureClassifier.TryClassify(
                cause,
                deviceRemovedReason,
                out RendererDeviceFailure failure))
        {
            exception = null!;
            return false;
        }

        exception = new DeviceLostException(
            resources.AdapterMode,
            presentedFrames,
            failure,
            BuildDeviceFailureMessage(
                operation,
                failure.Result,
                deviceRemovedReason),
            cause);
        return true;
    }

    private static Result ReadDeviceRemovedReason(
        ID3D11Device device)
    {
        try
        {
            return device.DeviceRemovedReason;
        }
        catch (SharpGenException exception)
        {
            return exception.ResultCode;
        }
    }

    private static string BuildDeviceFailureMessage(
        string operation,
        Result operationResult,
        Result deviceRemovedReason)
    {
        string reasonDetail =
            deviceRemovedReason.Failure &&
            deviceRemovedReason != operationResult
                ? $"; device removal reason 0x{deviceRemovedReason.Code:X8}"
                : string.Empty;
        return
            $"D3D11 {operation} failed with 0x{operationResult.Code:X8}{reasonDetail}.";
    }

    private sealed class DeviceResources : IDisposable
    {
        public DeviceResources(
            RendererAdapterMode adapterMode,
            FeatureLevel featureLevel,
            ID3D11Device device,
            ID3D11DeviceContext context,
            IDXGISwapChain swapChain,
            int width,
            int height)
        {
            AdapterMode = adapterMode;
            FeatureLevel = featureLevel;
            Device = device;
            Context = context;
            SwapChain = swapChain;
            Width = width;
            Height = height;
        }

        public RendererAdapterMode AdapterMode { get; }

        public FeatureLevel FeatureLevel { get; }

        public ID3D11Device Device { get; }

        public ID3D11DeviceContext Context { get; }

        public IDXGISwapChain SwapChain { get; }

        public ID3D11RenderTargetView? RenderTarget { get; private set; }

        public ID3D11Texture2D? DepthTexture { get; private set; }

        public ID3D11DepthStencilView? DepthStencil { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public void CreateTargets()
        {
            RenderTarget?.Dispose();
            using ID3D11Texture2D backBuffer = SwapChain.GetBuffer<ID3D11Texture2D>(0);
            RenderTarget = Device.CreateRenderTargetView(backBuffer);

            DepthStencil?.Dispose();
            DepthTexture?.Dispose();
            DepthTexture = Device.CreateTexture2D(
                new Texture2DDescription
                {
                    Width = (uint)Math.Max(1, Width),
                    Height = (uint)Math.Max(1, Height),
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.D24_UNorm_S8_UInt,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.DepthStencil,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                });
            DepthStencil = Device.CreateDepthStencilView(DepthTexture);
        }

        public Result Resize(int width, int height)
        {
            Context.OMSetRenderTargets(
                Array.Empty<ID3D11RenderTargetView>(),
                null);
            RenderTarget?.Dispose();
            RenderTarget = null;
            DepthStencil?.Dispose();
            DepthStencil = null;
            DepthTexture?.Dispose();
            DepthTexture = null;
            Result result = SwapChain.ResizeBuffers(
                0,
                (uint)Math.Max(1, width),
                (uint)Math.Max(1, height),
                Format.Unknown,
                SwapChainFlags.None);
            if (result.Success)
            {
                Width = Math.Max(1, width);
                Height = Math.Max(1, height);
                CreateTargets();
            }

            return result;
        }

        public void Dispose()
        {
            Context.ClearState();
            Context.Flush();
            DepthStencil?.Dispose();
            DepthTexture?.Dispose();
            RenderTarget?.Dispose();
            SwapChain.Dispose();
            Context.Dispose();
            Device.Dispose();
        }
    }

    private sealed class DeviceLostException : Exception
    {
        public DeviceLostException(
            RendererAdapterMode adapterMode,
            long presentedFrames,
            RendererDeviceFailure failure,
            string message,
            Exception? innerException = null)
            : base(message, innerException)
        {
            AdapterMode = adapterMode;
            PresentedFrames = presentedFrames;
            Failure = failure;
        }

        public RendererAdapterMode AdapterMode { get; }

        public long PresentedFrames { get; }

        public RendererDeviceFailure Failure { get; }
    }
}
