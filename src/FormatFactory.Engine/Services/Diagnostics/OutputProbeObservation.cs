namespace FormatFactoryWinUI.Services;

internal enum OutputProbeOutcome { ProbeUnavailable, ArtifactUnavailable, ProbeNonzeroExit, NoStreamOutput, ProbeException }

internal sealed record OutputProbeObservation(Guid Task, Guid Operation, string LogPath)
{
    internal void Write(OutputProbeOutcome outcome, bool directoryOutput, ProcessExecutionResult? result = null,
        Exception? exception = null)
    {
        try
        {
            var processOperation = exception is null ? null : RuntimeFailureDiagnostics.Get(exception)?.Operation;
            LogService.AppendTask(LogPath, FormattableString.Invariant(
                $"Output probe: session={RuntimeFailureDiagnostics.SessionId:N} task={Task:N} operation={Operation:N} observed_utc={DateTimeOffset.UtcNow:O} outcome={outcome} directory_output={directoryOutput} exit={RuntimeFailureDiagnostics.Number(result?.ExitCode)} stdout_retained_chars={RuntimeFailureDiagnostics.Number(result?.StandardOutput.Length)} stderr_retained_chars={RuntimeFailureDiagnostics.Number(result?.StandardError.Length)} truncated={result?.OutputTruncated.ToString() ?? "none"} process_operation={processOperation?.ToString("N") ?? "none"} {(exception is null ? "exception=none" : RuntimeFailureDiagnostics.ExceptionFields(exception))}\n"));
        }
        catch (Exception) { /* Optional observations must not decide output acceptance. */ }
    }
}
