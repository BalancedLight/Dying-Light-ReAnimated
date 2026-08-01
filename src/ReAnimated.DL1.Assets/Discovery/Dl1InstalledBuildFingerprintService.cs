using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.DL1.Assets.Discovery;

/// <summary>
/// Read-only identity for the installed Windows DL1 executable. The validation
/// fingerprint excludes the absolute install path and filesystem timestamps so
/// copying the same build to another Steam library does not change its identity.
/// </summary>
public sealed record Dl1InstalledBuildFingerprint(
    string InstallPath,
    string ExecutablePath,
    long ExecutableSize,
    string ExecutableSha256,
    string FileVersion,
    string ProductVersion,
    string BuildFingerprint);

public interface IDl1InstalledBuildFingerprintService
{
    Task<Dl1InstalledBuildFingerprint?> TryReadDiscoveredAsync(
        CancellationToken cancellationToken = default);

    Task<Dl1InstalledBuildFingerprint> ReadAsync(
        string installPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Computes a versioned DL1 build identity without starting or loading the
/// game executable. Hashing streams through one rented buffer.
/// </summary>
public sealed class Dl1InstalledBuildFingerprintService :
    IDl1InstalledBuildFingerprintService
{
    public const string ExecutableFileName = "DyingLightGame.exe";

    public const string FingerprintSchema =
        "dl-reanimated-dl1-windows-build-v1";

    public const int HashBufferSize = 1024 * 1024;

    public async Task<Dl1InstalledBuildFingerprint?> TryReadDiscoveredAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Dl1InstallLocation> locations = await Task.Run(
            static () => SteamInstallDiscovery.Discover(),
            cancellationToken).ConfigureAwait(false);
        Dl1InstallLocation? install = locations.FirstOrDefault(
            static candidate => candidate.IsValid);
        if (install is null)
        {
            return null;
        }

        return await ReadAsync(
            install.InstallPath,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Dl1InstalledBuildFingerprint> ReadAsync(
        string installPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedInstallPath = Path.GetFullPath(installPath);
        string executablePath = Path.Combine(
            normalizedInstallPath,
            ExecutableFileName);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                $"The DL1 executable was not found at '{executablePath}'.",
                executablePath);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        try
        {
            await using var stream = new FileStream(
                executablePath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    BufferSize = 64 * 1024,
                    Options =
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan,
                });
            long executableSize = stream.Length;
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, HashBufferSize),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string executableSha256 = Convert.ToHexString(
                    hash.GetHashAndReset())
                .ToLowerInvariant();
            FileVersionInfo version =
                FileVersionInfo.GetVersionInfo(executablePath);
            string fileVersion = FormatVersion(
                version.FileMajorPart,
                version.FileMinorPart,
                version.FileBuildPart,
                version.FilePrivatePart);
            string productVersion = FormatVersion(
                version.ProductMajorPart,
                version.ProductMinorPart,
                version.ProductBuildPart,
                version.ProductPrivatePart);
            string buildFingerprint = ComputeBuildFingerprint(
                executableSize,
                executableSha256,
                fileVersion,
                productVersion);

            return new Dl1InstalledBuildFingerprint(
                normalizedInstallPath,
                executablePath,
                executableSize,
                executableSha256,
                fileVersion,
                productVersion,
                buildFingerprint);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string ComputeBuildFingerprint(
        long executableSize,
        string executableSha256,
        string fileVersion,
        string productVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(executableSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        if (executableSha256.Length != 64 ||
            !executableSha256.All(
                static character =>
                    char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "The executable SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(executableSha256));
        }

        string canonical = string.Join(
            '\n',
            FingerprintSchema,
            $"executable={ExecutableFileName}",
            $"size={executableSize.ToString(CultureInfo.InvariantCulture)}",
            $"file-version={fileVersion.Trim()}",
            $"product-version={productVersion.Trim()}",
            $"sha256={executableSha256.ToLowerInvariant()}");
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string FormatVersion(
        int major,
        int minor,
        int build,
        int revision) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{major}.{minor}.{build}.{revision}");
}
