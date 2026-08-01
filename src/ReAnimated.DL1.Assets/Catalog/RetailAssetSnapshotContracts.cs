using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.DL1.Assets.Catalog;

/// <summary>
/// Lightweight, bounded evidence that the physical inputs of a retail asset
/// provider still match the inputs used to build a persistent catalog.
/// </summary>
public sealed record RetailAssetSourceSnapshot(
    int Ordinal,
    RetailAssetSourceKind Kind,
    int Priority,
    string Path,
    long Length,
    long LastWriteTimeUtcTicks,
    string BoundedFingerprint);

public sealed record RetailAssetRootSnapshot(
    int Ordinal,
    string Role,
    string Path,
    bool Exists);

public sealed record RetailAssetProviderSnapshot(
    string ProviderId,
    string ProviderKind,
    string InstallId,
    string ConfigurationFingerprint,
    IReadOnlyList<RetailAssetRootSnapshot> Roots,
    IReadOnlyList<RetailAssetSourceSnapshot> Sources)
{
    public string StableFingerprint =>
        RetailAssetIdentity.CreateSourceFingerprint(
            ProviderId,
            ProviderKind,
            InstallId,
            ConfigurationFingerprint,
            string.Join(
                "\n",
                Roots.Select(static root => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{root.Ordinal}|{root.Role}|{root.Path.ToUpperInvariant()}|{root.Exists}"))),
            string.Join(
                "\n",
                Sources.Select(static source => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{source.Ordinal}|{(int)source.Kind}|{source.Priority}|{source.Path.ToUpperInvariant()}|{source.Length}|{source.LastWriteTimeUtcTicks}|{source.BoundedFingerprint}"))));
}

/// <summary>
/// Implemented by providers whose physical source inventory can be validated
/// without enumerating/decompressing every asset in those sources.
/// </summary>
public interface IRetailAssetSnapshotProvider
{
    ValueTask<RetailAssetProviderSnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default);
}

internal static class RetailAssetSnapshotCapture
{
    private const int SampleBytes = 16 * 1024;
    private const int FullFileThresholdBytes = 64 * 1024;

    public static async ValueTask<RetailAssetSourceSnapshot> CaptureFileAsync(
        int ordinal,
        RetailAssetSourceKind kind,
        int priority,
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        FileInfo before = new(fullPath);
        if (!before.Exists)
        {
            throw new FileNotFoundException(
                "A retail catalog source disappeared during snapshot validation.",
                fullPath);
        }

        long length = before.Length;
        long lastWriteTicks = before.LastWriteTimeUtc.Ticks;
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(
            hash,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{fullPath.ToUpperInvariant()}|{length}|{lastWriteTicks}"));

        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            SampleBytes,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        int bufferSize = length <= FullFileThresholdBytes
            ? checked((int)Math.Max(1, length))
            : SampleBytes;
        byte[] buffer = new byte[bufferSize];
        foreach (long offset in GetSampleOffsets(length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = offset;
            int remaining = checked((int)Math.Min(buffer.Length, length - offset));
            int totalRead = 0;
            while (totalRead < remaining)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, remaining - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            AppendText(
                hash,
                offset.ToString(CultureInfo.InvariantCulture));
            hash.AppendData(buffer, 0, totalRead);
        }

        FileInfo after = new(fullPath);
        after.Refresh();
        if (!after.Exists ||
            after.Length != length ||
            after.LastWriteTimeUtc.Ticks != lastWriteTicks)
        {
            throw new IOException(
                $"Retail catalog source '{fullPath}' changed during snapshot validation.");
        }

        return new RetailAssetSourceSnapshot(
            ordinal,
            kind,
            priority,
            fullPath,
            length,
            lastWriteTicks,
            Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant());
    }

    public static string CreateConfigurationFingerprint(
        params object?[] parts) =>
        RetailAssetIdentity.CreateSourceFingerprint(parts);

    private static IReadOnlyList<long> GetSampleOffsets(long length)
    {
        if (length <= FullFileThresholdBytes)
        {
            return [0];
        }

        long last = Math.Max(0, length - SampleBytes);
        return
        [
            0,
            Math.Min(last, length / 4),
            Math.Min(last, length / 2),
            Math.Min(last, length / 4 * 3),
            last,
        ];
    }

    private static void AppendText(
        IncrementalHash hash,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }
}
