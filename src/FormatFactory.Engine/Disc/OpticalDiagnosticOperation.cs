using System.Diagnostics;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Disc;

internal enum OpticalDiagnosticAction { Unknown, Burn, Erase }
internal enum OpticalDiagnosticPhase
{
    Unknown, DeviceInitialize, CapabilityCheck, Configure, MediaCheck, ImageOpen,
    Write, Erase, Cancel, Result
}

/// <summary>Only fixed COM phases and booleans already observed by the caller.</summary>
internal sealed class OpticalDiagnosticOperation(OpticalDiagnosticAction action, Action<string>? write = null)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Action<string> _write = write ?? SafeDiagnostics.Write;
    internal Guid Id { get; } = Guid.NewGuid();
    internal OpticalDiagnosticPhase Phase { get; set; }
    internal bool? RecorderSupported { get; set; }
    internal bool? MediaSupported { get; set; }
    internal bool? CloseMedia { get; set; }
    internal bool? FullErase { get; set; }

    internal T Observe<T>(OpticalDiagnosticPhase phase, Func<T> work)
    {
        Phase = phase;
        try { return work(); }
        catch (OperationCanceledException error) { Cancelled(error); throw; }
        catch (Exception error) { Fail(error); throw; }
    }

    internal void Fail(Exception error) => Write(error, Phase, cancelled: false);
    internal void CancelFailure(Exception error) => Write(error, OpticalDiagnosticPhase.Cancel, cancelled: false);
    internal void Cancelled(OperationCanceledException error) => Write(error, OpticalDiagnosticPhase.Cancel, cancelled: true);

    private void Write(Exception error, OpticalDiagnosticPhase phase, bool cancelled)
    {
        try
        {
            if (!cancelled)
                RuntimeFailureDiagnostics.Record(error, new(RuntimeFailureStage.Unknown, Id, 1,
                    (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
            _write(Format(error, phase, cancelled));
        }
        catch (Exception) { /* Preserve the original COM, cancellation and cleanup behavior. */ }
    }

    internal string Format(Exception error, OpticalDiagnosticPhase? phase = null, bool cancelled = false) =>
        FormattableString.Invariant(
            $"Optical observation: session={RuntimeFailureDiagnostics.SessionId:N} operation={Id:N} action={Safe(action)} phase={Safe(phase ?? Phase)} outcome={(cancelled ? "cancelled" : "failed")} elapsed_ms={(long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds} recorder_supported={Flag(RecorderSupported)} media_supported={Flag(MediaSupported)} close_media={Flag(CloseMedia)} full_erase={Flag(FullErase)} {RuntimeFailureDiagnostics.ExceptionFields(error)}\n");

    private static string Safe<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
    private static string Flag(bool? value) => value is { } observed ? observed ? "true" : "false" : "none";
}
