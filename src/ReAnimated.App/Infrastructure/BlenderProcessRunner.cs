using System.Diagnostics;
using System.IO;
using System.Text;

namespace ReAnimated.App.Infrastructure;

public sealed class BlenderProcessRunner : IBlenderProcessRunner
{
    private const int MaximumRetainedLogCharacters =
        2 * 1024 * 1024;
    private const int RetainedLogTailCharacters =
        1536 * 1024;

    public async Task<BlenderProcessResult> RunAsync(
        BlenderProcessRequest request,
        Action<string>? outputLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The Blender timeout must be positive.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = request.BlenderExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory =
                Path.GetDirectoryName(request.JobPath)
                ?? Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("--background");
        startInfo.ArgumentList.Add("--factory-startup");
        startInfo.ArgumentList.Add("--python");
        startInfo.ArgumentList.Add(request.HelperScriptPath);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--job");
        startInfo.ArgumentList.Add(request.JobPath);

        using Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Windows did not start Blender.");
        }

        using var timeoutSource =
            new CancellationTokenSource(request.Timeout);
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
        var standardOutput = new BoundedLogBuffer();
        var standardError = new BoundedLogBuffer();
        Task outputTask = PumpAsync(
            process.StandardOutput,
            standardOutput,
            outputLine,
            linkedSource.Token);
        Task errorTask = PumpAsync(
            process.StandardError,
            standardError,
            outputLine,
            linkedSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token)
                .ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            await DrainPumpsAfterKillAsync(
                    outputTask,
                    errorTask)
                .ConfigureAwait(false);
            if (timeoutSource.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Blender exceeded the {request.Timeout.TotalMinutes:N0}-minute export timeout.");
            }

            throw;
        }

        return new BlenderProcessResult(
            process.ExitCode,
            standardOutput.GetText(),
            standardError.GetText());
    }

    private static async Task DrainPumpsAfterKillAsync(
        Task outputTask,
        Task errorTask)
    {
        Task pumps = Task.WhenAll(
            outputTask,
            errorTask);
        try
        {
            await pumps
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = pumps.ContinueWith(
                static task =>
                {
                    _ = task.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
            or IOException
            or ObjectDisposedException)
        {
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        BoundedLogBuffer destination,
        Action<string>? outputLine,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            destination.AppendLine(line);
            outputLine?.Invoke(line);
        }
    }

    private sealed class BoundedLogBuffer
    {
        private readonly StringBuilder _builder = new();
        private long _discardedCharacters;

        public void AppendLine(string line)
        {
            _ = _builder.AppendLine(line);
            if (_builder.Length <=
                MaximumRetainedLogCharacters)
            {
                return;
            }

            int removeCount =
                _builder.Length -
                RetainedLogTailCharacters;
            _builder.Remove(0, removeCount);
            _discardedCharacters += removeCount;
        }

        public string GetText()
        {
            if (_discardedCharacters == 0)
            {
                return _builder.ToString();
            }

            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"[DL ReAnimated truncated {_discardedCharacters:N0} earlier Blender log characters]{Environment.NewLine}{_builder}");
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task WaitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (TimeoutException)
        {
        }
    }
}
