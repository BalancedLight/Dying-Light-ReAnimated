namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// Bounded recovery policy for resize/present device loss. One isolated loss
/// rebuilds the current adapter; two rapid hardware losses switch recovery to
/// WARP. A viewport that rendered a stable interval starts a fresh audit.
/// </summary>
public sealed class RendererRecoveryPolicy
{
    public const long StableFrameThreshold = 300;

    public const int HardwareLossesBeforeWarp = 2;

    public const int MaximumRapidLosses = 8;

    private int _rapidLossCount;
    private int _remoteUnavailableCount;
    private bool _forceWarp;

    public RendererRecoveryDecision RecordDeviceLoss(
        RendererAdapterMode failedAdapter,
        long presentedFrames)
    {
        return RecordFailure(
            failedAdapter,
            presentedFrames,
            RendererRecoverableFailureKind.DeviceLost);
    }

    internal RendererRecoveryDecision RecordFailure(
        RendererAdapterMode failedAdapter,
        long presentedFrames,
        RendererRecoverableFailureKind failureKind)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(presentedFrames);
        if (failureKind ==
            RendererRecoverableFailureKind
                .DisplayConfigurationChanged)
        {
            _rapidLossCount = 0;
            _remoteUnavailableCount = 0;
            _forceWarp = false;
            return new RendererRecoveryDecision(
                ShouldRetry: true,
                ForceWarp: false,
                Delay: TimeSpan.Zero,
                RapidLossCount: 0,
                "Display configuration changed; rebuilding the D3D11 adapter.");
        }

        if (failureKind ==
            RendererRecoverableFailureKind
                .RemoteSessionUnavailable)
        {
            _remoteUnavailableCount++;
            _forceWarp = true;
            int remoteDelayExponent = Math.Min(
                _remoteUnavailableCount - 1,
                3);
            return new RendererRecoveryDecision(
                ShouldRetry: true,
                ForceWarp: true,
                Delay: TimeSpan.FromMilliseconds(
                    250 * (1 << remoteDelayExponent)),
                RapidLossCount: _remoteUnavailableCount,
                "The Windows display or Remote Desktop session is unavailable; waiting for the session and retrying with WARP.");
        }

        _remoteUnavailableCount = 0;
        if (presentedFrames >= StableFrameThreshold)
        {
            _rapidLossCount = 0;
            _forceWarp = false;
        }

        _rapidLossCount++;
        if (failedAdapter == RendererAdapterMode.Hardware &&
            _rapidLossCount >= HardwareLossesBeforeWarp)
        {
            _forceWarp = true;
        }

        bool shouldRetry = _rapidLossCount <= MaximumRapidLosses;
        int delayExponent = Math.Min(_rapidLossCount - 1, 3);
        TimeSpan delay = TimeSpan.FromMilliseconds(
            250 * (1 << delayExponent));
        string message = !shouldRetry
            ? $"The viewport stopped after {_rapidLossCount} rapid device losses."
            : _forceWarp
                ? "Repeated hardware device loss; rebuilding with the WARP diagnostic adapter."
                : "Display device was reset; rebuilding the viewport.";
        return new RendererRecoveryDecision(
            shouldRetry,
            _forceWarp,
            delay,
            _rapidLossCount,
            message);
    }
}

public readonly record struct RendererRecoveryDecision(
    bool ShouldRetry,
    bool ForceWarp,
    TimeSpan Delay,
    int RapidLossCount,
    string Message);
