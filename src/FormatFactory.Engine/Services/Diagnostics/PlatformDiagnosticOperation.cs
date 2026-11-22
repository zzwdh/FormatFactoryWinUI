using System.Diagnostics;

namespace FormatFactoryWinUI.Services;

internal enum PlatformDiagnosticKind { Unknown, Activation, FilePicker, FolderPicker, Clipboard, Explorer, HardwareProbe, Print, PdfOcr, ImageOcr, OutputFolderPicker }
internal enum ActivationDiagnosticKind { Unknown, Launch, File, Protocol, ShareTarget, StartupTask, AppNotification, Other }
internal enum PlatformDiagnosticPhase
{
    Unknown, HardwareInventory, HardwareDecode, HardwareCache, ClipboardRead, ClipboardImport,
    ExplorerSelect, ExplorerDirectory, PrintPrepare, PrintShow, PrintRequest, PrintPaginate,
    PrintPreview, PrintAddPages, OcrEngine, OcrOpen, OcrRender, OcrBitmapDecode, OcrRecognize
}

/// <summary>Fixed labels and observed counts only; no activation, clipboard, document or window-handle values.</summary>
internal sealed class PlatformDiagnosticOperation(PlatformDiagnosticKind kind)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    internal Guid Id { get; } = Guid.NewGuid();
    internal RuntimeFailureStage Stage { get; set; } = RuntimeFailureStage.Unknown;
    internal ActivationDiagnosticKind ActivationKind { get; set; } = ActivationDiagnosticKind.Unknown;
    internal PlatformDiagnosticPhase Phase { get; set; } = PlatformDiagnosticPhase.Unknown;
    internal bool? WindowOpen { get; set; }
    internal bool? HandlePresent { get; set; }
    internal bool? PrimaryInstance { get; set; }
    internal bool? HasWork { get; set; }
    internal int? Concurrent { get; set; }
    internal int? ResultCount { get; set; }
    internal int? Page { get; set; }
    internal long? Width { get; set; }
    internal long? Height { get; set; }
    internal long? MaximumDimension { get; set; }

    internal void Fail(Exception error)
    {
        try
        {
            RuntimeFailureDiagnostics.Record(error, new(Stage, Id, 1,
                (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds, false, ResultCount));
            SafeDiagnostics.Write(Format("failed", error));
        }
        catch (Exception diagnosticError) { Debug.WriteLine(diagnosticError); }
    }

    internal void Complete(bool cancelled = false)
    {
        try { SafeDiagnostics.Write(Format(cancelled ? "cancelled" : "completed", null)); }
        catch (Exception diagnosticError) { Debug.WriteLine(diagnosticError); }
    }

    internal async Task<T> ObserveAsync<T>(Func<Task<T>> work)
    {
        try { return await work(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { Fail(error); throw; }
    }

    internal T Observe<T>(Func<T> work)
    {
        try { return work(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { Fail(error); throw; }
    }

    internal async Task ObserveAsync(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { Fail(error); throw; }
    }

    internal string Format(string outcome, Exception? error)
    {
        var safeOutcome = outcome is "failed" or "completed" or "cancelled" ? outcome : "unknown";
        return FormattableString.Invariant(
            $"Platform observation: session={RuntimeFailureDiagnostics.SessionId:N} operation={Id:N} kind={EnumName(kind)} stage={EnumName(Stage)} phase={EnumName(Phase)} outcome={safeOutcome} elapsed_ms={(long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds} activation={EnumName(ActivationKind)} primary={Flag(PrimaryInstance)} has_work={Flag(HasWork)} window_open={Flag(WindowOpen)} handle_present={Flag(HandlePresent)} concurrent={RuntimeFailureDiagnostics.Number(Concurrent)} count={RuntimeFailureDiagnostics.Number(ResultCount)} page={RuntimeFailureDiagnostics.Number(Page)} width={RuntimeFailureDiagnostics.Number(Width)} height={RuntimeFailureDiagnostics.Number(Height)} maximum_dimension={RuntimeFailureDiagnostics.Number(MaximumDimension)} {(error is null ? "exception=none hresult=none native=none" : RuntimeFailureDiagnostics.ExceptionFields(error))}\n");
    }

    private static string Flag(bool? value) => value?.ToString() ?? "none";
    private static string EnumName<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
}
