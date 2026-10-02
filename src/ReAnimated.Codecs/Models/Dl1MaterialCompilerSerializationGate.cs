using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// Coordinates this application's MEConv jobs that target the same shared
/// template DLL. The current DL1 MEConv build writes that DLL beside the
/// executable, independently of the per-job working directory.
/// </summary>
/// <remarks>
/// The named mutex coordinates cooperating ReAnimated callers in the same
/// Windows logon session and using the same normalized output path. It does not
/// isolate MEConv or block callers that launch the vendor executable directly,
/// and distinct path aliases to the same file are not resolved to file identity.
/// </remarks>
internal static class Dl1MaterialCompilerSerializationGate
{
    private const string MutexPrefix = "Local\\ReAnimated.MEConvTemplate.";
    private const int LockPollMilliseconds = 100;

    internal static string CreateMutexName(string sharedOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedOutputPath);
        string normalizedPath = Path.GetFullPath(sharedOutputPath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        string digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return MutexPrefix + digest;
    }

    internal static Task<TResult> RunAsync<TResult>(
        string sharedOutputPath,
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task<TResult>> runStage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedOutputPath);
        ArgumentNullException.ThrowIfNull(runStage);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        return Task.Factory.StartNew(() =>
        {
            using var gate = new Mutex(
                initiallyOwned: false,
                name: CreateMutexName(sharedOutputPath));
            var elapsed = Stopwatch.StartNew();
            bool acquired = false;
            try
            {
                while (!acquired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TimeSpan remaining = timeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new TimeoutException(
                            $"Another material compiler job kept the shared template output busy for the full {timeout:g} stage timeout.");
                    }

                    int waitMilliseconds = Math.Clamp(
                        (int)Math.Ceiling(Math.Min(remaining.TotalMilliseconds, LockPollMilliseconds)),
                        1,
                        LockPollMilliseconds);
                    try
                    {
                        acquired = gate.WaitOne(waitMilliseconds);
                    }
                    catch (AbandonedMutexException)
                    {
                        // Windows grants ownership to this caller after an
                        // abandoned owner exits, allowing the next job through.
                        acquired = true;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan stageTimeout = timeout - elapsed.Elapsed;
                if (stageTimeout <= TimeSpan.Zero)
                {
                    throw new TimeoutException(
                        $"The shared material compiler template output lock consumed the full {timeout:g} stage timeout.");
                }

                // Keep mutex acquisition, async-stage waiting, and release on
                // one worker thread because Windows mutex ownership is thread-bound.
                return runStage(stageTimeout, cancellationToken).GetAwaiter().GetResult();
            }
            finally
            {
                if (acquired)
                    gate.ReleaseMutex();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
