using System.Windows.Threading;

namespace ReAnimated.App.Infrastructure;

/// <summary>
/// Coalesces dock-root changes until AvalonDock has processed its pending visual
/// callbacks. A stopped scheduler never applies a queued layout after owner close.
/// </summary>
public sealed class DockLayoutUpdateScheduler
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _apply;
    private bool _queued;
    private bool _stopped;

    public DockLayoutUpdateScheduler(Dispatcher dispatcher, Action apply)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public void Request()
    {
        _dispatcher.VerifyAccess();
        if (_stopped || _queued) return;
        _queued = true;
        // ContextIdle can be starved indefinitely by a continuously rendering
        // HwndHost/D3D viewport. Dock transitions must still be deferred until
        // the current AvalonDock callback returns, but they must run ahead of
        // Render work so the shell cannot remain on the previous workflow.
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _queued = false;
            if (!_stopped) _apply();
        }));
    }

    public void Stop()
    {
        _dispatcher.VerifyAccess();
        _stopped = true;
    }
}
