using System.Diagnostics;

namespace FormatFactoryWinUI.Services;

/// <summary>Numeric facts from one launch and its pipes. The child image name is reduced to a known tool ID.</summary>
internal sealed class EncoderProcessDiagnostics(Guid task, Guid operation, int attempt, string logPath)
{
    private long _stdout;
    private long _stderr;
    private int _truncatedLine;
    private int _cancellation;
    private ProgressObservation? _progress;
    private int? _pid;
    private string _image = "unknown";

    internal void Started(Process process, string selectedExecutable)
    {
        _pid = ReadProcessFact(() => process.Id);
        var started = ReadProcessFact(() => new DateTimeOffset(process.StartTime.ToUniversalTime()));
        try
        {
            if (process.MainModule?.FileName is { } image) _image = RuntimeFailureDiagnostics.Tool(image);
        }
        catch (Exception) { /* The process may have already exited or access may be denied. */ }
        Write(FormattableString.Invariant($"stage=Started pid={RuntimeFailureDiagnostics.Number(_pid)} start_utc={Utc(started)} selected_tool={RuntimeFailureDiagnostics.Tool(selectedExecutable)} process_image_tool={_image}"));
    }

    internal void AddStdout(int count) => Interlocked.Add(ref _stdout, count);
    internal void AddStderr(int count) => Interlocked.Add(ref _stderr, count);
    internal void TruncatedLine() => Volatile.Write(ref _truncatedLine, 1);
    internal void Progress(long microseconds)
    {
        if (microseconds >= 0) Volatile.Write(ref _progress, new(microseconds, DateTimeOffset.UtcNow));
    }

    internal void LifetimeSignal(ProcessLifetimeSignal signal) => Write($"stage=Lifetime signal={signal}");

    internal void MarkCancellationRequest() => Volatile.Write(ref _cancellation, 1);
    internal void WriteCancellationRequest(DateTimeOffset requestedAt) => Write(FormattableString.Invariant(
        $"stage=TerminationRequested reason=ConversionToken requested_utc={requestedAt:O}"));

    internal void Finished(Process? process, Task? stdout, Task? stderr, Exception? error, long elapsedMs,
        bool cancellationObserved)
    {
        var exit = process is null ? null : ReadProcessFact(() => process.ExitCode);
        var exitUtc = process is null ? null : ReadProcessFact(() => new DateTimeOffset(process.ExitTime.ToUniversalTime()));
        var progress = Volatile.Read(ref _progress);
        Write(FormattableString.Invariant(
            $"stage=Finished pid={RuntimeFailureDiagnostics.Number(_pid)} process_image_tool={_image} elapsed_ms={elapsedMs} exit={RuntimeFailureDiagnostics.Number(exit)} exit_utc={Utc(exitUtc)} token_cancelled={cancellationObserved} token_termination_requested={Volatile.Read(ref _cancellation) != 0} stdout_chars={Count(stdout, Interlocked.Read(ref _stdout))} stderr_chars={Count(stderr, Interlocked.Read(ref _stderr))} stdout_drain={stdout?.Status.ToString() ?? "NotStarted"} stderr_drain={stderr?.Status.ToString() ?? "NotStarted"} truncated_line={Volatile.Read(ref _truncatedLine) != 0} last_progress_us={RuntimeFailureDiagnostics.Number(progress?.Microseconds)} progress_utc={Utc(progress?.Utc)} {(error is null ? "exception=none" : RuntimeFailureDiagnostics.ExceptionFields(error))}"));
    }

    private void Write(string facts)
    {
        try
        {
            LogService.AppendTask(logPath, FormattableString.Invariant(
                $"\nEncoder process: session={RuntimeFailureDiagnostics.SessionId:N} task={task:N} operation={operation:N} attempt={attempt} observed_utc={DateTimeOffset.UtcNow:O} {facts}\n"));
        }
        catch (Exception) { /* Never replace a conversion result or block termination. */ }
    }

    private static T? ReadProcessFact<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception) { return default; }
    }

    private static string Count(Task? pipe, long value) => RuntimeFailureDiagnostics.Number(pipe is null ? null : value);
    private static string Utc(DateTimeOffset? time) => time?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "none";
    private sealed record ProgressObservation(long Microseconds, DateTimeOffset Utc);
}
