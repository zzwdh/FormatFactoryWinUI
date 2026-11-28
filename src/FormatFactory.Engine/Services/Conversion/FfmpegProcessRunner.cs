using System.Diagnostics;
using System.Globalization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal sealed record FfmpegProcessResult(int ExitCode, IReadOnlyList<string> LastErrorLines, string? FailureDetail = null);

/// <summary>Runs the sequential encoder attempts for one conversion and exposes its current pausable process.</summary>
internal sealed class FfmpegProcessRunner
{
    private volatile ActiveConversionProcess? _activeProcess;
    private int _attempt;
    internal bool HasActiveProcess => _activeProcess is not null;
    internal bool Pause() => _activeProcess?.TrySuspend() == true;
    internal bool Resume() => _activeProcess?.TryResume() == true;

    internal async Task<FfmpegProcessResult> RunProcessAsync(
        MediaTask task,
        string executable,
        IReadOnlyList<string> arguments,
        string logPath,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        LogService.AppendTask(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {executable} {string.Join(' ', arguments.Select(Quote))}{Environment.NewLine}{Environment.NewLine}");
        var observation = Guid.NewGuid();
        var attempt = Interlocked.Increment(ref _attempt);
        var diagnostic = new EncoderProcessDiagnostics(task.Id, observation, attempt, logPath);
        var started = Stopwatch.GetTimestamp();
        LogService.AppendTask(logPath, RuntimeFailureDiagnostics.TaskFacts(task, observation));
        ConversionWorkerLaunch launch;
        try { launch = await ConversionWorkerLaunch.StartAsync(executable, arguments, diagnostic.LifetimeSignal, cancellationToken); }
        catch (Exception processError)
        {
            var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            diagnostic.Finished(null, null, null, processError, elapsedMs, cancellationToken.IsCancellationRequested);
            if (processError is not OperationCanceledException)
            {
                RuntimeFailureDiagnostics.Record(processError, new(RuntimeFailureStage.ProcessStart, observation, attempt, elapsedMs));
                RuntimeFailureDiagnostics.WriteProcess(logPath, observation, executable, RuntimeFailureStage.ProcessStart,
                    elapsedMs, null, processError);
                await AppendFailedEngineObservationAsync(logPath, executable, task.InputPath, cancellationToken);
            }
            throw;
        }
        using var ownedLaunch = launch;
        var lifetime = launch.Lifetime;
        var process = lifetime.Process;
        diagnostic.Started(launch.Child, executable, launch.ChildId);
        using var active = launch.Child is { } encoder ? new ActiveConversionProcess(encoder, lifetime.Job) : null;
        using var registration = cancellationToken.Register(() =>
        {
            var requestedAt = DateTimeOffset.UtcNow;
            diagnostic.MarkCancellationRequest();
            try { active?.TryTerminate(); }
            finally { diagnostic.WriteCancellationRequest(requestedAt); }
        });
        _activeProcess = active;
        var errorLines = new FfmpegErrorCapture();
        Task? progressTask = null;
        Task? errorTask = null;
        Exception? waitError = null;
        try
        {
            progressTask = ReadProgressAsync(process.StandardOutput, task.Probe.Duration, report, diagnostic, cancellationToken);
            errorTask = ReadErrorsAsync(process.StandardError, logPath, errorLines, diagnostic, cancellationToken);
            await lifetime.WaitAndDrainAsync(Timeout.InfiniteTimeSpan, cancellationToken, progressTask, errorTask);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            waitError = exception;
            throw;
        }
        finally
        {
            _activeProcess = null;
            diagnostic.Finished(launch.Child, progressTask, errorTask, waitError,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, cancellationToken.IsCancellationRequested, process);
        }

        var exitCode = process.ExitCode;
        LogService.AppendTask(logPath, FormattableString.Invariant(
            $"[{DateTimeOffset.UtcNow:O}] Process exit code: {exitCode} (0x{unchecked((uint)exitCode):X8}){Environment.NewLine}"));
        RuntimeFailureDiagnostics.WriteProcess(logPath, observation, executable, RuntimeFailureStage.ProcessRun,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, exitCode);
        LogService.AppendTask(logPath, FormattableString.Invariant(
            $"Process diagnostic tail: lines={errorLines.LastLines.Count} characters={errorLines.LastLines.Sum(line => line.Length)} scope=retained_tail\n"));
        if (exitCode != 0) await AppendFailedEngineObservationAsync(logPath, executable, task.InputPath, cancellationToken);
        return new FfmpegProcessResult(exitCode, errorLines.LastLines, errorLines.FailureDetail);
    }

    internal static async Task AppendFailedEngineObservationAsync(string logPath, string executable, string input,
        CancellationToken cancellationToken)
    {
        try
        {
            var facts = await MediaProbeSafeFacts.CollectAsync(executable, input, cancellationToken);
            LogService.AppendTask(logPath, FormattableString.Invariant(
                $"Conversion engine observation: scope=selected_file_after_failure state={facts.EngineState} sha256={facts.EngineSha256 ?? "none"} length={RuntimeFailureDiagnostics.Number(facts.EngineLength)} source_status={facts.ReadStatus}\n"));
        }
        // The process has already failed. Cancelling optional observations must not replace that failure.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception diagnosticError) { System.Diagnostics.Debug.WriteLine(diagnosticError); }
    }

    private static async Task ReadProgressAsync(StreamReader reader, TimeSpan duration, Action<ConversionProgress> report,
        EncoderProcessDiagnostics diagnostic, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var line in BoundedProcessOutput.ReadLinesAsync(reader, cancellationToken: cancellationToken,
            onCharactersRead: diagnostic.AddStdout))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            if (key is "out_time_us" or "out_time_ms" or "speed") values[key] = line[(separator + 1)..];
            if (!line.StartsWith("progress=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var microseconds = GetLong(values, "out_time_us");
            if (microseconds == 0)
            {
                microseconds = GetLong(values, "out_time_ms");
            }
            if ((values.TryGetValue("out_time_us", out var rawTime) || values.TryGetValue("out_time_ms", out rawTime)) &&
                long.TryParse(rawTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var observedTime))
                diagnostic.Progress(observedTime);
            var processed = TimeSpan.FromMilliseconds(microseconds / 1000d);
            var percent = duration > TimeSpan.Zero ? Math.Clamp(processed.TotalMilliseconds / duration.TotalMilliseconds * 100d, 0, 99.5) : 0;
            var speedText = values.TryGetValue("speed", out var speed) ? speed : "—";
            var remaining = CalculateRemaining(duration, processed, speedText);
            report(new ConversionProgress(percent, speedText, remaining, LocalizationService.T("Converting")));
            values.Clear();
        }
    }

    private static async Task ReadErrorsAsync(StreamReader reader, string logPath, FfmpegErrorCapture errors,
        EncoderProcessDiagnostics diagnostic, CancellationToken cancellationToken)
    {
        await foreach (var line in BoundedProcessOutput.ReadLinesAsync(reader,
            chunk => LogService.AppendTask(logPath, chunk), cancellationToken, requireCompleteLine: false,
            onTruncatedLine: diagnostic.TruncatedLine, onCharactersRead: diagnostic.AddStderr))
        {
            errors.Add(line);
        }
    }

    private static string CalculateRemaining(TimeSpan duration, TimeSpan processed, string speedText)
    {
        if (duration <= TimeSpan.Zero || !speedText.EndsWith('x') || !double.TryParse(speedText.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || speed <= 0)
        {
            return "—";
        }

        var seconds = Math.Max(0, (duration - processed).TotalSeconds / speed);
        var remaining = TimeSpan.FromSeconds(seconds);
        return remaining.TotalHours >= 1 ? remaining.ToString(@"h\:mm\:ss") : remaining.ToString(@"m\:ss");
    }

    private static long GetLong(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && long.TryParse(value, out var result) ? result : 0;
    private static string Quote(string argument) => argument.Contains(' ') ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;
}
