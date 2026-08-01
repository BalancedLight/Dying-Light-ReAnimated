using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.Tests;

public sealed class Dl1InstalledBuildFingerprintTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-BuildFingerprintTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadsExecutableWithStablePathIndependentIdentity()
    {
        byte[] payload = new byte[
            (Dl1InstalledBuildFingerprintService.HashBufferSize * 2) + 73];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = checked((byte)(index % 251));
        }

        string firstInstall = CreateInstall("first", payload);
        string secondInstall = CreateInstall("second", payload);
        var service = new Dl1InstalledBuildFingerprintService();

        Dl1InstalledBuildFingerprint first =
            await service.ReadAsync(firstInstall);
        Dl1InstalledBuildFingerprint second =
            await service.ReadAsync(secondInstall);

        Assert.Equal(payload.LongLength, first.ExecutableSize);
        Assert.True(
            first.ExecutableSize >
            Dl1InstalledBuildFingerprintService.HashBufferSize);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(payload))
                .ToLowerInvariant(),
            first.ExecutableSha256);
        Assert.Equal(first.ExecutableSha256, second.ExecutableSha256);
        Assert.Equal(first.BuildFingerprint, second.BuildFingerprint);
        Assert.Equal(64, first.BuildFingerprint.Length);
        Assert.NotEqual(first.ExecutablePath, second.ExecutablePath);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", first.FileVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", first.ProductVersion);
    }

    [Fact]
    public async Task BuildIdentityChangesWhenExecutableBytesChange()
    {
        byte[] firstPayload = [1, 2, 3, 4, 5];
        byte[] secondPayload = [1, 2, 3, 4, 6];
        string firstInstall = CreateInstall("before", firstPayload);
        string secondInstall = CreateInstall("after", secondPayload);
        var service = new Dl1InstalledBuildFingerprintService();

        Dl1InstalledBuildFingerprint before =
            await service.ReadAsync(firstInstall);
        Dl1InstalledBuildFingerprint after =
            await service.ReadAsync(secondInstall);

        Assert.Equal(before.ExecutableSize, after.ExecutableSize);
        Assert.NotEqual(before.ExecutableSha256, after.ExecutableSha256);
        Assert.NotEqual(before.BuildFingerprint, after.BuildFingerprint);
    }

    [Fact]
    public async Task ReadHonorsPreCanceledToken()
    {
        string install = CreateInstall("canceled", [1, 2, 3]);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new Dl1InstalledBuildFingerprintService().ReadAsync(
                install,
                source.Token));
    }

    [Fact]
    public void GameValidatedProfileRequiresMatchingBuildAndTrustedCapture()
    {
        string captureFingerprint = new('c', 64);
        PreviewProfile profile = CreateValidatedProfile(
            new string('a', 64),
            captureFingerprint);

        Assert.Equal(
            PreviewFidelityTier.Dl1Profile,
            profile.GetEffectiveFidelityTier(
                new string('b', 64),
                captureFingerprint));
        Assert.Equal(
            PreviewFidelityTier.Dl1Profile,
            profile.GetEffectiveFidelityTier(new string('A', 64)));
        Assert.Equal(
            PreviewFidelityTier.GameValidated,
            profile.GetEffectiveFidelityTier(
                new string('A', 64),
                new string('C', 64)));
    }

    [Fact]
    public void GameValidatedProfileRequiresSha256EvidencePair()
    {
        PreviewProfile baseline = PreviewProfile.ThirdPersonAuthoring;
        Assert.Throws<ArgumentException>(
            () => new PreviewProfile(
                baseline.Id,
                baseline.ViewMode,
                baseline.Fidelity,
                baseline.VisualStyle,
                baseline.CameraBoneName,
                baseline.CameraLens,
                baseline.CameraOffset,
                PreviewFidelityTier.GameValidated,
                baseline.Context,
                baseline.ProfileVersion,
                new string('a', 64),
                baseline.ProceduralToggles,
                baseline.MorphActivationThreshold,
                baseline.MaximumActiveMorphTargets,
                baseline.ClampMorphWeightsToRigBounds));
        Assert.Throws<ArgumentException>(
            () => CreateValidatedProfile("not-a-sha256", new string('c', 64)));
    }

    [Fact]
    public async Task ViewModelShowsSavedProfileDowngradeForDifferentInstall()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string expected = new('a', 64);
        string installed = new('b', 64);
        var build = new Dl1InstalledBuildFingerprint(
            @"C:\Games\Dying Light",
            @"C:\Games\Dying Light\DyingLightGame.exe",
            1234,
            new string('c', 64),
            "1.55.0.0",
            "1.55.0.0",
            installed);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "workspace.json")),
            new NoOpProjectFileDialogs(),
            assets,
            new FixedFingerprintService(build));
        viewModel.RestoreSnapshot(CreateSnapshot(
            DlraProject.Create("Validated") with
            {
                PreviewProfile = CreateValidatedProfile(
                    expected,
                    new string('c', 64)),
            }));

        await viewModel.InitializeInstalledBuildStatusAsync();

        FidelityBadgeViewModel preview = Assert.Single(
            viewModel.FidelityBadges,
            badge => badge.Label == "Preview fidelity");
        FidelityBadgeViewModel detected = Assert.Single(
            viewModel.FidelityBadges,
            badge => badge.Label == "Installed DL1 build");
        Assert.Equal("DL1 profile", preview.State);
        Assert.Contains(expected[..12], preview.Detail, StringComparison.Ordinal);
        Assert.Contains(installed[..12], preview.Detail, StringComparison.Ordinal);
        Assert.Contains("downgraded", preview.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Detected", detected.State);
        Assert.Contains("1.55.0.0", detected.Detail, StringComparison.Ordinal);
        Assert.Same(build, viewModel.InstalledBuildFingerprint);
    }

    [Fact]
    public async Task ViewModelDoesNotTrustProjectCaptureEvidenceByItself()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string fingerprint = new('d', 64);
        var build = new Dl1InstalledBuildFingerprint(
            @"C:\Games\Dying Light",
            @"C:\Games\Dying Light\DyingLightGame.exe",
            4321,
            new string('e', 64),
            "1.55.0.0",
            "1.55.0.0",
            fingerprint);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "matching-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "matching-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "matching-workspace.json")),
            new NoOpProjectFileDialogs(),
            assets,
            new FixedFingerprintService(build));
        viewModel.RestoreSnapshot(CreateSnapshot(
            DlraProject.Create("Validated") with
            {
                PreviewProfile = CreateValidatedProfile(
                    fingerprint,
                    new string('f', 64)),
            }));

        await viewModel.InitializeInstalledBuildStatusAsync();

        FidelityBadgeViewModel preview = Assert.Single(
            viewModel.FidelityBadges,
            badge => badge.Label == "Preview fidelity");
        Assert.Equal("DL1 profile", preview.State);
        Assert.Contains(
            "capture",
            preview.Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "downgraded",
            preview.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private string CreateInstall(string name, byte[] executable)
    {
        string install = Path.Combine(_temporaryDirectory, name);
        Directory.CreateDirectory(install);
        File.WriteAllBytes(
            Path.Combine(
                install,
                Dl1InstalledBuildFingerprintService.ExecutableFileName),
            executable);
        return install;
    }

    private static PreviewProfile CreateValidatedProfile(
        string fingerprint,
        string captureFingerprint)
    {
        PreviewProfile baseline = PreviewProfile.ThirdPersonAuthoring;
        return new PreviewProfile(
            baseline.Id,
            baseline.ViewMode,
            baseline.Fidelity,
            baseline.VisualStyle,
            baseline.CameraBoneName,
            baseline.CameraLens,
            baseline.CameraOffset,
            PreviewFidelityTier.GameValidated,
            baseline.Context,
            baseline.ProfileVersion,
            fingerprint,
            baseline.ProceduralToggles,
            baseline.MorphActivationThreshold,
            baseline.MaximumActiveMorphTargets,
            baseline.ClampMorphWeightsToRigBounds,
            captureFingerprint);
    }

    private static WorkspaceSnapshot CreateSnapshot(DlraProject project) =>
        new(
            WorkspaceSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            null,
            string.Empty,
            null,
            null,
            0,
            true,
            60.0f,
            0.02f,
            "Retarget",
            project);

    private sealed class FixedFingerprintService(
        Dl1InstalledBuildFingerprint? fingerprint) :
        IDl1InstalledBuildFingerprintService
    {
        public Task<Dl1InstalledBuildFingerprint?> TryReadDiscoveredAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(fingerprint);
        }

        public Task<Dl1InstalledBuildFingerprint> ReadAsync(
            string installPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return fingerprint is null
                ? Task.FromException<Dl1InstalledBuildFingerprint>(
                    new FileNotFoundException())
                : Task.FromResult(fingerprint);
        }
    }

    private sealed class NoOpProjectFileDialogs :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }
}
