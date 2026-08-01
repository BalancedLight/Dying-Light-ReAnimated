using SharpGen.Runtime;
using Vortice.DXGI;

namespace ReAnimated.Renderer.D3D11;

internal readonly record struct RendererViewportSize(
    int Width,
    int Height);

/// <summary>
/// Publishes a width/height pair through one atomic value. This prevents the
/// render thread from observing a new width with an old height while WPF is
/// rapidly resizing or moving between monitors with different DPI.
/// </summary>
internal sealed class RendererViewportSizeMailbox
{
    private long _packedSize;

    public RendererViewportSizeMailbox(int width, int height)
    {
        _packedSize = Pack(width, height);
    }

    public void Publish(int width, int height)
    {
        Interlocked.Exchange(
            ref _packedSize,
            Pack(width, height));
    }

    public RendererViewportSize Read()
    {
        long packed = Interlocked.Read(ref _packedSize);
        return new RendererViewportSize(
            unchecked((int)(packed >> 32)),
            unchecked((int)(packed & uint.MaxValue)));
    }

    private static long Pack(int width, int height)
    {
        uint stableWidth = checked((uint)Math.Max(1, width));
        uint stableHeight = checked((uint)Math.Max(1, height));
        return unchecked(
            (long)(((ulong)stableWidth << 32) | stableHeight));
    }
}

/// <summary>
/// Monotonic signal used to invalidate a device after Windows reports a
/// display configuration change. Consumers compare generations instead of
/// losing requests to a resettable Boolean flag.
/// </summary>
internal sealed class RendererAdapterRefreshSignal
{
    private long _generation;

    public long CaptureGeneration() =>
        Interlocked.Read(ref _generation);

    public void RequestRefresh()
    {
        _ = Interlocked.Increment(ref _generation);
    }

    public bool HasChanged(long observedGeneration) =>
        CaptureGeneration() != observedGeneration;
}

internal enum RendererRecoverableFailureKind
{
    DeviceLost,
    RemoteSessionUnavailable,
    DisplayConfigurationChanged,
}

internal readonly record struct RendererDeviceFailure(
    RendererRecoverableFailureKind Kind,
    Result Result);

/// <summary>
/// Centralizes the DXGI failures for which destroying and recreating the
/// device is a valid recovery action. Programming errors such as
/// DXGI_ERROR_INVALID_CALL remain fail-closed instead of entering a rebuild
/// loop.
/// </summary>
internal static class RendererDeviceFailureClassifier
{
    public static bool TryClassify(
        Result operationResult,
        Result deviceRemovedReason,
        out RendererDeviceFailure failure)
    {
        if (TryClassify(operationResult, out failure))
        {
            return true;
        }

        return TryClassify(deviceRemovedReason, out failure);
    }

    public static bool TryClassify(
        Exception exception,
        Result deviceRemovedReason,
        out RendererDeviceFailure failure)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            Result exceptionResult =
                current is SharpGenException sharpGenException
                    ? sharpGenException.ResultCode
                    : Result.GetResultFromException(current);
            if (TryClassify(
                    exceptionResult,
                    out failure))
            {
                return true;
            }
        }

        return TryClassify(deviceRemovedReason, out failure);
    }

    public static bool TryClassify(
        Result result,
        out RendererDeviceFailure failure)
    {
        RendererRecoverableFailureKind? kind =
            result == ResultCode.DeviceRemoved ||
            result == ResultCode.DeviceReset ||
            result == ResultCode.DeviceHung ||
            result == ResultCode.DriverInternalError
                ? RendererRecoverableFailureKind.DeviceLost
                : result == ResultCode.RemoteClientDisconnected ||
                  result == ResultCode.RemoteOutofmemory ||
                  result == ResultCode.SessionDisconnected ||
                  result == ResultCode.NotCurrentlyAvailable
                    ? RendererRecoverableFailureKind
                        .RemoteSessionUnavailable
                    : result == ResultCode.RestrictToOutputStale ||
                      result == ResultCode.NotCurrent
                        ? RendererRecoverableFailureKind
                            .DisplayConfigurationChanged
                        : null;
        if (kind is null)
        {
            failure = default;
            return false;
        }

        failure = new RendererDeviceFailure(
            kind.Value,
            result);
        return true;
    }
}
