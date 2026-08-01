using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ReAnimated.Renderer.D3D11;

public interface IEditorRenderer
{
    IRenderSceneSource? SceneSource { get; set; }

    string StatusText { get; }

    double FramesPerSecond { get; }

    bool IsUsingWarp { get; }

    ReadOnlyObservableCollection<string> Diagnostics { get; }

    event EventHandler<D3D11RendererStatus>? RendererStatusChanged;

    IDisposable RegisterRenderPass(ID3D11RenderPass pass);
}

public sealed class D3D11RenderHost : HwndHost, IEditorRenderer
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int StaticStyleNotify = 0x00000100;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoZOrder = 0x0004;
    private const int WmKillFocus = 0x0008;
    private const int WmEraseBackground = 0x0014;
    private const int WmCancelMode = 0x001F;
    private const int WmDisplayChange = 0x007E;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMiddleButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmCaptureChanged = 0x0215;

    private static readonly DependencyPropertyKey StatusTextPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(StatusText),
            typeof(string),
            typeof(D3D11RenderHost),
            new PropertyMetadata("Renderer not started"));

    private static readonly DependencyPropertyKey FramesPerSecondPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(FramesPerSecond),
            typeof(double),
            typeof(D3D11RenderHost),
            new PropertyMetadata(0.0));

    private static readonly DependencyPropertyKey IsUsingWarpPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsUsingWarp),
            typeof(bool),
            typeof(D3D11RenderHost),
            new PropertyMetadata(false));

    public static readonly DependencyProperty SceneSourceProperty =
        DependencyProperty.Register(
            nameof(SceneSource),
            typeof(IRenderSceneSource),
            typeof(D3D11RenderHost),
            new PropertyMetadata(null, OnSceneSourceChanged));

    public static readonly DependencyProperty StatusTextProperty =
        StatusTextPropertyKey.DependencyProperty;

    public static readonly DependencyProperty FramesPerSecondProperty =
        FramesPerSecondPropertyKey.DependencyProperty;

    public static readonly DependencyProperty IsUsingWarpProperty =
        IsUsingWarpPropertyKey.DependencyProperty;

    private readonly object _passLock = new();
    private readonly List<ID3D11RenderPass> _renderPasses = [];
    private readonly ObservableCollection<string> _diagnostics = [];
    private readonly ReadOnlyObservableCollection<string> _readOnlyDiagnostics;
    private readonly RenderCameraInputState _cameraInput = new();
    private RenderTransformGizmoDragSession? _transformGizmoDrag;
    private IRenderTransformGizmoTarget? _transformGizmoTarget;
    private RenderTranslationGizmoDragSession? _translationGizmoDrag;
    private IRenderTranslationGizmoTarget? _translationGizmoTarget;
    private IntPtr _childWindow;
    private D3D11RenderLoop? _renderLoop;
    private IRenderSceneSource? _sceneSource;

    public D3D11RenderHost()
    {
        _readOnlyDiagnostics = new ReadOnlyObservableCollection<string>(_diagnostics);
        Focusable = true;
        SizeChanged += OnHostSizeChanged;
    }

    public event EventHandler<D3D11RendererStatus>? RendererStatusChanged;

    public IRenderSceneSource? SceneSource
    {
        get => (IRenderSceneSource?)GetValue(SceneSourceProperty);
        set => SetValue(SceneSourceProperty, value);
    }

    public string StatusText => (string)GetValue(StatusTextProperty);

    public double FramesPerSecond => (double)GetValue(FramesPerSecondProperty);

    public bool IsUsingWarp => (bool)GetValue(IsUsingWarpProperty);

    public ReadOnlyObservableCollection<string> Diagnostics => _readOnlyDiagnostics;

    public IDisposable RegisterRenderPass(ID3D11RenderPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        lock (_passLock)
        {
            _renderPasses.Add(pass);
        }

        return new RenderPassRegistration(this, pass);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _childWindow = NativeMethods.CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild |
                WsVisible |
                WsClipSiblings |
                WsClipChildren |
                StaticStyleNotify,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_childWindow == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the D3D11 child window.");
        }

        (int width, int height) = GetPixelSize();
        _renderLoop = new D3D11RenderLoop(
            _childWindow,
            width,
            height,
            () => Volatile.Read(ref _sceneSource),
            SnapshotRenderPasses,
            PublishStatus,
            PublishDiagnostic);
        _renderLoop.Start();
        return new HandleRef(this, _childWindow);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        CancelPointerInteractions(
            hwnd.Handle,
            releaseCapture: true);
        _renderLoop?.Dispose();
        _renderLoop = null;
        if (hwnd.Handle != IntPtr.Zero && !NativeMethods.DestroyWindow(hwnd.Handle))
        {
            PublishDiagnostic(
                $"DestroyWindow failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        _childWindow = IntPtr.Zero;
    }

    protected override IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmEraseBackground)
        {
            handled = true;
            return new IntPtr(1);
        }

        switch (msg)
        {
            case WmLeftButtonDown:
                return BeginPrimaryPointerDrag(
                    hwnd,
                    lParam,
                    ref handled);
            case WmMiddleButtonDown:
                return BeginCameraDrag(
                    hwnd,
                    RenderCameraPointerButton.Middle,
                    lParam,
                    ref handled);
            case WmMouseMove:
                return ContinuePointerDrag(
                    hwnd,
                    lParam,
                    ref handled);
            case WmLeftButtonUp:
                return EndPrimaryPointerDrag(
                    hwnd,
                    lParam,
                    ref handled);
            case WmMiddleButtonUp:
                return EndCameraDrag(
                    hwnd,
                    RenderCameraPointerButton.Middle,
                    ref handled);
            case WmMouseWheel:
                return ApplyCameraWheel(
                    wParam,
                    ref handled);
            case WmCaptureChanged:
                CancelPointerInteractions(
                    hwnd,
                    releaseCapture: false);
                break;
            case WmCancelMode:
            case WmKillFocus:
                CancelPointerInteractions(
                    hwnd,
                    releaseCapture: true);
                break;
            case WmDisplayChange:
                _renderLoop?.RequestAdapterRefresh();
                break;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private IntPtr BeginPrimaryPointerDrag(
        IntPtr hwnd,
        IntPtr packedPoint,
        ref bool handled)
    {
        IRenderSceneSource? source =
            Volatile.Read(ref _sceneSource);
        if (source is IRenderTransformGizmoTarget transformTarget)
        {
            (int x, int y) = UnpackPoint(packedPoint);
            (int width, int height) = GetPixelSize();
            RenderFrameSnapshot frame = source.CaptureFrame();
            if (RenderTransformGizmoHitTest.TryBeginDrag(
                    frame,
                    x,
                    y,
                    width,
                    height,
                    out RenderTransformGizmoDragSession? session))
            {
                handled = true;
                if (session is null ||
                    !transformTarget.TryBeginTransformGizmoDrag(
                        new RenderTransformGizmoDragStart(
                            session.Binding,
                            session.AxisDirectionWorld)))
                {
                    return IntPtr.Zero;
                }

                if (!TryCapturePointer(hwnd))
                {
                    transformTarget.CompleteTransformGizmoDrag(
                        commit: false);
                    return IntPtr.Zero;
                }

                _transformGizmoDrag = session;
                _transformGizmoTarget = transformTarget;
                return IntPtr.Zero;
            }
        }

        if (source is IRenderTranslationGizmoTarget gizmoTarget)
        {
            (int x, int y) = UnpackPoint(packedPoint);
            (int width, int height) = GetPixelSize();
            RenderFrameSnapshot frame = source.CaptureFrame();
            if (RenderTranslationGizmoHitTest.TryBeginDrag(
                    frame,
                    x,
                    y,
                    width,
                    height,
                    out RenderTranslationGizmoDragSession? session))
            {
                handled = true;
                if (session is null ||
                    !gizmoTarget.TryBeginTranslationGizmoDrag(
                        new RenderTranslationGizmoDragStart(
                            session.Binding,
                            session.AxisDirectionWorld)))
                {
                    return IntPtr.Zero;
                }

                if (!TryCapturePointer(hwnd))
                {
                    gizmoTarget.CompleteTranslationGizmoDrag(
                        commit: false);
                    return IntPtr.Zero;
                }

                _translationGizmoDrag = session;
                _translationGizmoTarget = gizmoTarget;
                return IntPtr.Zero;
            }
        }

        return BeginCameraDrag(
            hwnd,
            RenderCameraPointerButton.Left,
            packedPoint,
            ref handled);
    }

    private IntPtr BeginCameraDrag(
        IntPtr hwnd,
        RenderCameraPointerButton button,
        IntPtr packedPoint,
        ref bool handled)
    {
        if (_transformGizmoDrag is not null ||
            _translationGizmoDrag is not null)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (Volatile.Read(ref _sceneSource) is not
            IRenderCameraNavigationTarget)
        {
            return IntPtr.Zero;
        }

        (int x, int y) = UnpackPoint(packedPoint);
        if (!_cameraInput.BeginDrag(button, x, y))
        {
            return IntPtr.Zero;
        }

        if (!TryCapturePointer(hwnd))
        {
            _cameraInput.CancelDrag();
            return IntPtr.Zero;
        }

        handled = true;
        return IntPtr.Zero;
    }

    private IntPtr ContinuePointerDrag(
        IntPtr hwnd,
        IntPtr packedPoint,
        ref bool handled)
    {
        if (_transformGizmoDrag is { } transformDrag &&
            _transformGizmoTarget is { } transformTarget)
        {
            handled = true;
            (int transformX, int transformY) =
                UnpackPoint(packedPoint);
            if (!transformDrag.TryUpdate(
                    transformX,
                    transformY,
                    out RenderTransformGizmoDragUpdate update) ||
                !transformTarget.UpdateTransformGizmoDrag(update))
            {
                CompleteTransformGizmoDrag(
                    hwnd,
                    commit: false);
            }

            return IntPtr.Zero;
        }

        if (_translationGizmoDrag is { } gizmoDrag &&
            _translationGizmoTarget is { } gizmoTarget)
        {
            handled = true;
            (int gizmoX, int gizmoY) =
                UnpackPoint(packedPoint);
            if (!gizmoDrag.TryUpdate(
                    gizmoX,
                    gizmoY,
                    out RenderTranslationGizmoDragUpdate update) ||
                !gizmoTarget.UpdateTranslationGizmoDrag(update))
            {
                CompleteTranslationGizmoDrag(
                    hwnd,
                    commit: false);
            }

            return IntPtr.Zero;
        }

        return ContinueCameraDrag(
            packedPoint,
            ref handled);
    }

    private IntPtr ContinueCameraDrag(
        IntPtr packedPoint,
        ref bool handled)
    {
        (int x, int y) = UnpackPoint(packedPoint);
        (int width, int height) = GetPixelSize();
        if (!_cameraInput.TryMove(
                x,
                y,
                width,
                height,
                out RenderCameraNavigationInput input))
        {
            return IntPtr.Zero;
        }

        ApplyCameraNavigation(input);
        handled = true;
        return IntPtr.Zero;
    }

    private IntPtr EndPrimaryPointerDrag(
        IntPtr hwnd,
        IntPtr packedPoint,
        ref bool handled)
    {
        if (_transformGizmoDrag is { } transformDrag &&
            _transformGizmoTarget is { } transformTarget)
        {
            handled = true;
            (int x, int y) = UnpackPoint(packedPoint);
            bool commit =
                transformDrag.TryUpdate(
                    x,
                    y,
                    out RenderTransformGizmoDragUpdate update) &&
                transformTarget.UpdateTransformGizmoDrag(update) &&
                transformDrag.HasMeaningfulMovement;
            CompleteTransformGizmoDrag(hwnd, commit);
            return IntPtr.Zero;
        }

        if (_translationGizmoDrag is { } gizmoDrag &&
            _translationGizmoTarget is { } gizmoTarget)
        {
            handled = true;
            (int x, int y) = UnpackPoint(packedPoint);
            bool commit =
                gizmoDrag.TryUpdate(
                    x,
                    y,
                    out RenderTranslationGizmoDragUpdate update) &&
                gizmoTarget.UpdateTranslationGizmoDrag(update) &&
                gizmoDrag.HasMeaningfulMovement;
            CompleteTranslationGizmoDrag(hwnd, commit);
            return IntPtr.Zero;
        }

        return EndCameraDrag(
            hwnd,
            RenderCameraPointerButton.Left,
            ref handled);
    }

    private IntPtr EndCameraDrag(
        IntPtr hwnd,
        RenderCameraPointerButton button,
        ref bool handled)
    {
        if (!_cameraInput.EndDrag(button))
        {
            return IntPtr.Zero;
        }

        if (NativeMethods.GetCapture() == hwnd)
        {
            _ = NativeMethods.ReleaseCapture();
        }

        handled = true;
        return IntPtr.Zero;
    }

    private IntPtr ApplyCameraWheel(
        IntPtr packedKeysAndDelta,
        ref bool handled)
    {
        if (_transformGizmoDrag is not null ||
            _translationGizmoDrag is not null)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (Volatile.Read(ref _sceneSource) is not
            IRenderCameraNavigationTarget)
        {
            return IntPtr.Zero;
        }

        int wheelDelta = UnpackSignedHighWord(
            packedKeysAndDelta);
        if (wheelDelta == 0)
        {
            return IntPtr.Zero;
        }

        (int width, int height) = GetPixelSize();
        ApplyCameraNavigation(
            RenderCameraNavigationInput.Zoom(
                wheelDelta,
                width,
                height));
        handled = true;
        return IntPtr.Zero;
    }

    private void ApplyCameraNavigation(
        RenderCameraNavigationInput input)
    {
        if (Volatile.Read(ref _sceneSource) is
            IRenderCameraNavigationTarget target)
        {
            _ = target.NavigateCamera(input);
        }
    }

    private static bool TryCapturePointer(IntPtr hwnd)
    {
        _ = NativeMethods.SetFocus(hwnd);
        _ = NativeMethods.SetCapture(hwnd);
        return NativeMethods.GetCapture() == hwnd;
    }

    private void CompleteTranslationGizmoDrag(
        IntPtr hwnd,
        bool commit)
    {
        IRenderTranslationGizmoTarget? target =
            _translationGizmoTarget;
        _translationGizmoDrag = null;
        _translationGizmoTarget = null;
        if (hwnd != IntPtr.Zero &&
            NativeMethods.GetCapture() == hwnd)
        {
            _ = NativeMethods.ReleaseCapture();
        }

        target?.CompleteTranslationGizmoDrag(commit);
    }

    private void CompleteTransformGizmoDrag(
        IntPtr hwnd,
        bool commit)
    {
        IRenderTransformGizmoTarget? target =
            _transformGizmoTarget;
        _transformGizmoDrag = null;
        _transformGizmoTarget = null;
        if (hwnd != IntPtr.Zero &&
            NativeMethods.GetCapture() == hwnd)
        {
            _ = NativeMethods.ReleaseCapture();
        }

        target?.CompleteTransformGizmoDrag(commit);
    }

    private void CancelPointerInteractions(
        IntPtr hwnd,
        bool releaseCapture)
    {
        _cameraInput.CancelDrag();
        IRenderTransformGizmoTarget? transformTarget =
            _transformGizmoTarget;
        _transformGizmoDrag = null;
        _transformGizmoTarget = null;
        IRenderTranslationGizmoTarget? target =
            _translationGizmoTarget;
        _translationGizmoDrag = null;
        _translationGizmoTarget = null;
        if (releaseCapture &&
            hwnd != IntPtr.Zero &&
            NativeMethods.GetCapture() == hwnd)
        {
            _ = NativeMethods.ReleaseCapture();
        }

        transformTarget?.CompleteTransformGizmoDrag(
            commit: false);
        target?.CompleteTranslationGizmoDrag(
            commit: false);
    }

    private static (int X, int Y) UnpackPoint(IntPtr packedPoint)
    {
        long value = packedPoint.ToInt64();
        return (
            unchecked((short)(value & 0xFFFF)),
            unchecked((short)((value >> 16) & 0xFFFF)));
    }

    private static int UnpackSignedHighWord(IntPtr packedValue) =>
        unchecked((short)((packedValue.ToInt64() >> 16) & 0xFFFF));

    private static void OnSceneSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        D3D11RenderHost host = (D3D11RenderHost)dependencyObject;
        Volatile.Write(ref host._sceneSource, (IRenderSceneSource?)args.NewValue);
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        (int width, int height) = GetPixelSize();
        _renderLoop?.RequestResize(width, height);
        if (_childWindow != IntPtr.Zero)
        {
            _ = NativeMethods.SetWindowPos(
                _childWindow,
                IntPtr.Zero,
                0,
                0,
                width,
                height,
                SwpNoActivate | SwpNoZOrder);
        }
    }

    private (int Width, int Height) GetPixelSize()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        return (width, height);
    }

    private ID3D11RenderPass[] SnapshotRenderPasses()
    {
        lock (_passLock)
        {
            return _renderPasses.ToArray();
        }
    }

    private void RemoveRenderPass(ID3D11RenderPass pass)
    {
        lock (_passLock)
        {
            _ = _renderPasses.Remove(pass);
        }
    }

    private void PublishStatus(D3D11RendererStatus status)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            SetValue(StatusTextPropertyKey, status.Message);
            SetValue(FramesPerSecondPropertyKey, status.FramesPerSecond);
            SetValue(
                IsUsingWarpPropertyKey,
                status.AdapterMode == RendererAdapterMode.Warp);
            RendererStatusChanged?.Invoke(this, status);
        });
    }

    private void PublishDiagnostic(string message)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            _diagnostics.Add(message);
            while (_diagnostics.Count > 200)
            {
                _diagnostics.RemoveAt(0);
            }
        });
    }

    private sealed class RenderPassRegistration : IDisposable
    {
        private D3D11RenderHost? _owner;
        private readonly ID3D11RenderPass _pass;

        public RenderPassRegistration(
            D3D11RenderHost owner,
            ID3D11RenderPass pass)
        {
            _owner = owner;
            _pass = pass;
        }

        public void Dispose()
        {
            D3D11RenderHost? owner = Interlocked.Exchange(ref _owner, null);
            owner?.RemoveRenderPass(_pass);
        }
    }

    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use LibraryImportAttribute instead of DllImport",
        Justification = "The project intentionally does not enable unsafe blocks.")]
    private static class NativeMethods
    {
        [DllImport(
            "user32.dll",
            EntryPoint = "CreateWindowExW",
            SetLastError = true,
            ExactSpelling = true,
            CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(
            int extendedStyle,
            string className,
            string windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            int flags);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr window);

        [DllImport("user32.dll")]
        public static extern IntPtr GetCapture();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr window);
    }
}
