using System.Diagnostics;
using System.Text;

namespace FormatFactoryWinUI.Services;

internal sealed record ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string? LogPath { get; init; }
    public bool OutputTruncated { get; init; }
}

internal sealed record ProcessRunOptions
{
    public bool CaptureCompleteOutput { get; init; } = true;
    public int MaximumOutputBytes { get; init; } = BoundedProcessOutput.MaximumParsedBytes;
    public int MaximumErrorBytes { get; init; } = BoundedProcessOutput.TailBytes;
    public string? LogPath { get; init; }
    public string? StandardInput { get; init; }
    public bool UseConversionWorker { get; init; }
}

internal static class ProcessRunner
{
    public static Task<ProcessExecutionResult> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunAsync(executable, arguments, timeout, new ProcessRunOptions(), cancellationToken);

    public static Task<ProcessExecutionResult> RunConverterLoggedAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunAsync(executable, arguments, timeout, new ProcessRunOptions
        {
            CaptureCompleteOutput = false,
            UseConversionWorker = true,
            MaximumOutputBytes = BoundedProcessOutput.TailBytes,
            LogPath = LogService.CreateTaskLogPath(Guid.NewGuid())
        }, cancellationToken);

    public static async Task<ProcessExecutionResult> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, ProcessRunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(options);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout),
            LocalizationService.T("GeneralServiceTimeoutPositive"));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumErrorBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = options.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (options.StandardInput is not null) startInfo.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (options.LogPath is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.LogPath))!);

        var observation = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        IDisposable owner;
        ExternalProcessLifetime lifetime;
        try
        {
            if (options.UseConversionWorker)
            {
                var launch = await ConversionWorkerLaunch.StartAsync(executable, startInfo.ArgumentList,
                    null, cancellationToken, redirectStandardInput: options.StandardInput is not null).ConfigureAwait(false);
                owner = launch;
                lifetime = launch.Lifetime;
            }
            else
            {
                lifetime = ExternalProcessLifetime.Start(startInfo, cancellationToken);
                owner = lifetime;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RuntimeFailureDiagnostics.Record(error, new(RuntimeFailureStage.ProcessStart, observation, 1,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            RuntimeFailureDiagnostics.WriteProcess(options.LogPath, observation, executable, RuntimeFailureStage.ProcessStart,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, error);
            throw;
        }
        using var processLifetime = owner;
        var process = lifetime.Process;
        var outputTask = BoundedProcessOutput.ReadAsync(process.StandardOutput.BaseStream, "stdout",
            options.MaximumOutputBytes, options.CaptureCompleteOutput, options.LogPath);
        var errorTask = BoundedProcessOutput.ReadAsync(process.StandardError.BaseStream, "stderr",
            options.MaximumErrorBytes, requireComplete: false, options.LogPath is null ? null : options.LogPath + ".stderr");
        var inputTask = options.StandardInput is null ? Task.CompletedTask : WriteInputAsync(process, options.StandardInput);
        try { await lifetime.WaitAndDrainAsync(timeout, cancellationToken, outputTask, errorTask, inputTask).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RuntimeFailureDiagnostics.Record(error, new(RuntimeFailureStage.ProcessRun, observation, 1,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            RuntimeFailureDiagnostics.WriteProcess(options.LogPath, observation, executable, RuntimeFailureStage.ProcessRun,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, error);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        var standardError = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            RuntimeFailureDiagnostics.WriteProcess(options.LogPath, observation, executable, RuntimeFailureStage.ProcessRun,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, process.ExitCode, null,
                output.Text.Length, standardError.Text.Length, output.Truncated || standardError.Truncated);
        return new ProcessExecutionResult(process.ExitCode, output.Text, standardError.Text)
        {
            LogPath = options.LogPath,
            OutputTruncated = output.Truncated || standardError.Truncated
        };
    }

    private static async Task WriteInputAsync(Process process, string input)
    {
        await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        process.StandardInput.Close();
    }

    internal static Task DrainCompletedProcessOutputAsync(Process process, Task outputTask, Task errorTask) =>
        ExternalProcessLifetime.DrainCompletedAsync(process, outputTask, errorTask);
}
