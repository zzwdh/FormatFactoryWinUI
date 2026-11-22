using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal enum RuntimeFailureStage
{
    Unknown, ActivationRead, ActivationRegister, ActivationRedirect, ActivationDispatch,
    PickerCreate, PickerInitialize, PickerShow, PickerImport, ProcessStart, ProcessRun,
    OutputValidation, OcrDecode, OcrRecognize, Print, ShellOpen, PackageUpdate
}
internal enum DiagnosticPage { Unknown, Queue, Video, Audio, Image, Document, Ebook, Disc, Archive, Automation, History, Settings, Advanced }
internal sealed record RuntimeFailureInfo(RuntimeFailureStage Stage, Guid Operation, int Attempt,
    long ElapsedMs, bool? Recovered = null, int? Count = null);
internal sealed record DiagnosticUiSnapshot(DiagnosticPage Page, double Width, double Height, double Scale,
    int ActiveTasks, bool WindowOpen, DateTimeOffset ObservedUtc);

/// <summary>Only fixed enums, numeric facts and random correlation IDs; never exception text, Data or activation payloads.</summary>
internal static class RuntimeFailureDiagnostics
{
    private static readonly ConditionalWeakTable<Exception, RuntimeFailureInfo> Details = new();
    private static readonly ConditionalWeakTable<Exception, Identity> Identities = new();
    private static DiagnosticUiSnapshot? _ui;
    internal static Guid SessionId { get; } = Guid.NewGuid();
    internal static void Record(Exception error, RuntimeFailureInfo info) => Details.GetValue(error, _ => info);
    internal static RuntimeFailureInfo? Get(Exception error) => Details.TryGetValue(error, out var value) ? value : null;
    internal static void ObserveUi(DiagnosticUiSnapshot snapshot) => Volatile.Write(ref _ui, snapshot);

    internal static string ExceptionFields(Exception error) => FormattableString.Invariant(
        $"exception={ExceptionType(error)} hresult=0x{error.HResult:X8} native={Number(NativeCode(error))}");
    internal static string ExceptionType(Exception error) => error switch
    {
        FileNotFoundException => "FileNotFoundException", DirectoryNotFoundException => "DirectoryNotFoundException",
        UnauthorizedAccessException => "UnauthorizedAccessException", InvalidDataException => "InvalidDataException",
        IOException => "IOException", TimeoutException => "TimeoutException", Win32Exception => "Win32Exception",
        COMException => "COMException", System.Text.Json.JsonException => "JsonException",
        OperationCanceledException => "OperationCanceledException", NullReferenceException => "NullReferenceException",
        ArgumentException => "ArgumentException", NotSupportedException => "NotSupportedException",
        InvalidOperationException => "InvalidOperationException", OutOfMemoryException => "OutOfMemoryException",
        _ when error.GetType().Name == "LayoutCycleException" => "LayoutCycleException",
        _ => "Other"
    };
    internal static int? NativeCode(Exception error) => error is Win32Exception native ? native.NativeErrorCode :
        (error.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? error.HResult & 0xFFFF : null;
    internal static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";
    internal static string FormatId(string? value) => value is { Length: <= 32 } ? FormatCatalog.FindById(value)?.Id ?? "unknown" : "unknown";
    internal static string Mode(string? value) => value is "lossless" or "quality" or "balanced" or "fast" ? value : "unknown";
    internal static string Codec(string? value)
    {
        var normalized = value is { Length: <= 32 } ? value.ToLowerInvariant() : null;
        return normalized switch
        {
            "h264" or "hevc" or "av1" or "vp8" or "vp9" or "mpeg4" or "mpeg1video" or "mpeg2video" or
            "mjpeg" or "png" or "bmp" or "tiff" or "webp" or "gif" or "ffv1" or "prores" or "bvc2" or
            "aac" or "mp3" or "mp3float" or "alac" or "flac" or "opus" or "vorbis" or "ac3" or "eac3" or
            "apple_apac" or "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" => normalized,
            _ => "unknown"
        };
    }
    internal static string Tool(string path)
    {
        string name;
        try { name = Path.GetFileName(path).ToLowerInvariant(); }
        catch (ArgumentException) { return "other"; }
        return name switch
        {
        "ffmpeg.exe" => "ffmpeg", "ffprobe.exe" => "ffprobe", "python.exe" => "python",
        "ebook-convert.exe" => "calibre", "soffice.exe" or "soffice.com" => "libreoffice",
        "7z.exe" => "7zip", "mutool.exe" => "mutool", "tesseract.exe" => "tesseract",
        "makemkvcon.exe" or "makemkvcon64.exe" => "makemkv", "caj2pdf.exe" => "caj",
        _ => "other"
        };
    }

    internal static void WriteProcess(string? logPath, Guid operation, string executable, RuntimeFailureStage stage,
        long elapsedMs, int? exit, Exception? error = null, int? outputCharacters = null, int? errorCharacters = null,
        bool? truncated = null)
    {
        try
        {
            var line = FormattableString.Invariant($"Process observation: session={SessionId:N} operation={operation:N} tool={Tool(executable)} stage={stage} elapsed_ms={elapsedMs} exit={Number(exit)} stdout_chars={Number(outputCharacters)} stderr_chars={Number(errorCharacters)} truncated={truncated?.ToString() ?? "none"} { (error is null ? "exception=none hresult=none native=none" : ExceptionFields(error))}\n");
            if (logPath is null) SafeDiagnostics.Write(line); else LogService.AppendTask(logPath, line);
        }
        catch (Exception diagnosticError) { System.Diagnostics.Debug.WriteLine(diagnosticError); }
    }

    internal static string Format(Exception error)
    {
        var id = Identities.GetValue(error, _ => new(Guid.NewGuid())).Value;
        var note = Get(error);
        var text = new StringBuilder(FormattableString.Invariant(
            $"Failure context: session={SessionId:N} error={id:N} observed_utc={DateTimeOffset.UtcNow:O} {ExceptionFields(error)} stage={note?.Stage ?? RuntimeFailureStage.Unknown} operation={note?.Operation.ToString("N") ?? "none"} attempt={note?.Attempt ?? 0} elapsed_ms={Number(note?.ElapsedMs)} recovered={note?.Recovered?.ToString() ?? "none"} count={Number(note?.Count)} architecture={RuntimeInformation.ProcessArchitecture}\n"));
        if (Volatile.Read(ref _ui) is { } ui)
            text.Append(FormattableString.Invariant($"UI observation: scope=last_observation observed_utc={ui.ObservedUtc:O} page={ui.Page} width={Finite(ui.Width)} height={Finite(ui.Height)} scale={Finite(ui.Scale)} active={ui.ActiveTasks} open={ui.WindowOpen}\n"));
        if (error.InnerException is { } inner) text.Append($"Inner failure: {ExceptionFields(inner)}\n");
        return text.ToString();
    }

    internal static string TaskFacts(MediaTask task, Guid operation) => FormattableString.Invariant(
        $"Media context: session={SessionId:N} task={task.Id:N} operation={operation:N} kind={task.Kind} source={FormatId(task.SourceFormatId)} target={FormatId(task.TargetFormatId)} mode={Mode(task.ConversionMode)} width={task.Probe.Width} height={task.Probe.Height} duration_ticks={task.Probe.Duration.Ticks} video={Codec(task.Probe.VideoCodec)} video_index={task.Probe.VideoStreamIndex} audio_count={task.Probe.AudioTracks.Count} selected_audio={task.SelectedAudioTrack} pages={task.Probe.PageCount} animated={task.Probe.IsAnimated}\n");
    internal static void WriteTaskObservation(string logPath, FormattableString observation)
    {
        try { LogService.AppendTask(logPath, FormattableString.Invariant(observation)); }
        catch (Exception) { /* Optional observations cannot change conversion results. */ }
    }
    private static string Finite(double value) => double.IsFinite(value) && value >= 0 && value <= 100000
        ? value.ToString("0.###", CultureInfo.InvariantCulture) : "none";
    private sealed record Identity(Guid Value);
}
