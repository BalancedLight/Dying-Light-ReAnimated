namespace ReAnimated.App.Infrastructure;

/// <summary>
/// Process-lifetime latch for fatal UI exception presentation. It is never
/// reset: after the first dispatcher failure, nested dispatcher frames may
/// drain but cannot publish another report or dialog.
/// </summary>
internal sealed class FatalCrashPresentationGate
{
    private int _started;

    public bool TryBegin() =>
        Interlocked.CompareExchange(ref _started, 1, 0) == 0;
}
