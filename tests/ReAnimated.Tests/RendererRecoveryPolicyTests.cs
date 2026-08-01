using ReAnimated.Renderer.D3D11;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace ReAnimated.Tests;

public sealed class RendererRecoveryPolicyTests
{
    [Fact]
    public void RepeatedRapidHardwareLossSwitchesToWarp()
    {
        RendererRecoveryPolicy policy = new();

        RendererRecoveryDecision first = policy.RecordDeviceLoss(
            RendererAdapterMode.Hardware,
            presentedFrames: 12);
        RendererRecoveryDecision second = policy.RecordDeviceLoss(
            RendererAdapterMode.Hardware,
            presentedFrames: 0);

        Assert.True(first.ShouldRetry);
        Assert.False(first.ForceWarp);
        Assert.Equal(TimeSpan.FromMilliseconds(250), first.Delay);
        Assert.True(second.ShouldRetry);
        Assert.True(second.ForceWarp);
        Assert.Equal(TimeSpan.FromMilliseconds(500), second.Delay);
        Assert.Equal(
            [RendererAdapterMode.Warp],
            RendererDeviceSelectionPolicy.GetAttempts(
                allowWarpFallback: true,
                forceWarp: second.ForceWarp));
    }

    [Fact]
    public void StableRunStartsAFreshRecoveryAudit()
    {
        RendererRecoveryPolicy policy = new();
        policy.RecordDeviceLoss(
            RendererAdapterMode.Hardware,
            presentedFrames: 0);
        Assert.True(policy.RecordDeviceLoss(
            RendererAdapterMode.Hardware,
            presentedFrames: 0).ForceWarp);

        RendererRecoveryDecision stableLoss = policy.RecordDeviceLoss(
            RendererAdapterMode.Warp,
            RendererRecoveryPolicy.StableFrameThreshold);

        Assert.True(stableLoss.ShouldRetry);
        Assert.False(stableLoss.ForceWarp);
        Assert.Equal(1, stableLoss.RapidLossCount);
    }

    [Fact]
    public void RapidLossLoopFaultsAfterBoundedRetries()
    {
        RendererRecoveryPolicy policy = new();
        RendererRecoveryDecision decision = default;
        for (int index = 0;
             index <= RendererRecoveryPolicy.MaximumRapidLosses;
             index++)
        {
            decision = policy.RecordDeviceLoss(
                RendererAdapterMode.Warp,
                presentedFrames: 0);
        }

        Assert.False(decision.ShouldRetry);
        Assert.Equal(
            RendererRecoveryPolicy.MaximumRapidLosses + 1,
            decision.RapidLossCount);
        Assert.Equal(TimeSpan.FromSeconds(2), decision.Delay);
        Assert.Contains(
            "stopped",
            decision.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteDesktopDisconnectWaitsWithoutExhaustingDeviceRetries()
    {
        RendererRecoveryPolicy policy = new();
        RendererRecoveryDecision decision = default;

        for (int attempt = 0; attempt < 100; attempt++)
        {
            decision = policy.RecordFailure(
                RendererAdapterMode.Warp,
                presentedFrames: 0,
                RendererRecoverableFailureKind
                    .RemoteSessionUnavailable);
            Assert.True(decision.ShouldRetry);
            Assert.True(decision.ForceWarp);
        }

        Assert.Equal(100, decision.RapidLossCount);
        Assert.Equal(TimeSpan.FromSeconds(2), decision.Delay);
        Assert.Contains(
            "Remote Desktop",
            decision.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayChangeClearsForcedWarpAndRetriesHardwareImmediately()
    {
        RendererRecoveryPolicy policy = new();
        _ = policy.RecordDeviceLoss(
            RendererAdapterMode.Hardware,
            presentedFrames: 0);
        Assert.True(policy.RecordDeviceLoss(
            RendererAdapterMode.Hardware,
            presentedFrames: 0).ForceWarp);

        RendererRecoveryDecision displayChange =
            policy.RecordFailure(
                RendererAdapterMode.Warp,
                presentedFrames: 0,
                RendererRecoverableFailureKind
                    .DisplayConfigurationChanged);

        Assert.True(displayChange.ShouldRetry);
        Assert.False(displayChange.ForceWarp);
        Assert.Equal(TimeSpan.Zero, displayChange.Delay);
        Assert.Equal(0, displayChange.RapidLossCount);
        Assert.False(policy.RecordDeviceLoss(
            RendererAdapterMode.Hardware,
            presentedFrames: 0).ForceWarp);
    }

    [Fact]
    public void FailureClassifierCoversDeviceRemoteAndDisplayRecovery()
    {
        Result[] deviceLossResults =
        [
            ResultCode.DeviceRemoved,
            ResultCode.DeviceReset,
            ResultCode.DeviceHung,
            ResultCode.DriverInternalError,
        ];
        foreach (Result result in deviceLossResults)
        {
            Assert.True(
                RendererDeviceFailureClassifier.TryClassify(
                    result,
                    out RendererDeviceFailure failure));
            Assert.Equal(
                RendererRecoverableFailureKind.DeviceLost,
                failure.Kind);
            Assert.Equal(result, failure.Result);
        }

        Result[] remoteResults =
        [
            ResultCode.RemoteClientDisconnected,
            ResultCode.RemoteOutofmemory,
            ResultCode.SessionDisconnected,
            ResultCode.NotCurrentlyAvailable,
        ];
        foreach (Result result in remoteResults)
        {
            Assert.True(
                RendererDeviceFailureClassifier.TryClassify(
                    result,
                    out RendererDeviceFailure failure));
            Assert.Equal(
                RendererRecoverableFailureKind
                    .RemoteSessionUnavailable,
                failure.Kind);
        }

        Result[] displayResults =
        [
            ResultCode.RestrictToOutputStale,
            ResultCode.NotCurrent,
        ];
        foreach (Result result in displayResults)
        {
            Assert.True(
                RendererDeviceFailureClassifier.TryClassify(
                    result,
                    out RendererDeviceFailure failure));
            Assert.Equal(
                RendererRecoverableFailureKind
                    .DisplayConfigurationChanged,
                failure.Kind);
        }

        Assert.False(
            RendererDeviceFailureClassifier.TryClassify(
                ResultCode.InvalidCall,
                out _));
    }

    [Fact]
    public void FailureClassifierReadsThrownAndRemovedDeviceResults()
    {
        var thrown = new InvalidOperationException(
            "render wrapper",
            new SharpGenException(
                ResultCode.RemoteClientDisconnected));

        Assert.True(
            RendererDeviceFailureClassifier.TryClassify(
                thrown,
                Result.Ok,
                out RendererDeviceFailure thrownFailure));
        Assert.Equal(
            RendererRecoverableFailureKind
                .RemoteSessionUnavailable,
            thrownFailure.Kind);

        Assert.True(
            RendererDeviceFailureClassifier.TryClassify(
                ResultCode.InvalidCall,
                ResultCode.DeviceReset,
                out RendererDeviceFailure removedFailure));
        Assert.Equal(
            RendererRecoverableFailureKind.DeviceLost,
            removedFailure.Kind);
        Assert.Equal(
            ResultCode.DeviceReset,
            removedFailure.Result);

        Assert.True(
            RendererDeviceFailureClassifier.TryClassify(
                ResultCode.RemoteClientDisconnected,
                ResultCode.DeviceRemoved,
                out RendererDeviceFailure remoteFailure));
        Assert.Equal(
            RendererRecoverableFailureKind
                .RemoteSessionUnavailable,
            remoteFailure.Kind);

        var comFailure = new HResultTestException(
            ResultCode.DeviceReset);
        Assert.True(
            RendererDeviceFailureClassifier.TryClassify(
                comFailure,
                Result.Ok,
                out RendererDeviceFailure comDeviceFailure));
        Assert.Equal(
            RendererRecoverableFailureKind.DeviceLost,
            comDeviceFailure.Kind);
    }

    private sealed class HResultTestException : Exception
    {
        public HResultTestException(Result result)
            : base("Injected native HRESULT failure.")
        {
            HResult = result.Code;
        }
    }
}
