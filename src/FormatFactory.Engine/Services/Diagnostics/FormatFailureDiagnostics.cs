using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal enum FormatFailureStage
{
    Unknown, CsoHeader, CsoIndex, CsoDecode, PdfInputParse, PdfOutputValidate,
    DocxPackage, DocxSchema, ImagePolicy, NativeImageProbe, NativeImageEncode,
    NativeImageValidate, NativeImageIntermediate, NativeImageCommit, RemuxPolicy, RemuxProbe, RemuxOutput,
    DocumentPolicy, DocumentConvert, DocumentOutput, CalibreInput, CalibreRun, CalibreOutput,
    CajInput, CajRun, CajOutput
}
internal enum FormatInputOrigin { Unknown, Original, Intermediate }

internal enum FormatFailureReason
{
    Unknown, HeaderRead, Magic, HeaderSize, TotalSize, BlockSize, Version, Alignment,
    Reserved, BlockCount, IndexTruncated, FinalIndexFlag, IndexOverlap, OffsetRange,
    PlainBlockSize, TrailingData, DecodedSize, DecodeRead, ParseFailure, Missing,
    NoPages, MissingBody, SchemaValidation, PackageRead, UnsupportedRoute,
    MultiplePages, AnimationFormat, AnimationTiming, Precision, ColorRange,
    CodecFailure, OutputValidation, SampleMismatch, UnknownStreams, StreamSelection,
    UnsupportedStream, MultipleStreams
}

internal sealed record FormatFailureFact(FormatFailureStage Stage, FormatFailureReason Reason)
{
    internal ulong? HeaderSize { get; init; }
    internal ulong? TotalBytes { get; init; }
    internal ulong? BlockSize { get; init; }
    internal ulong? Version { get; init; }
    internal ulong? Alignment { get; init; }
    internal ulong? Reserved { get; init; }
    internal long? InputLength { get; init; }
    internal long? Index { get; init; }
    internal long? Start { get; init; }
    internal long? End { get; init; }
    internal int? PdfHeaderOffset { get; init; }
    internal int? PrefixObservedBytes { get; init; }
    internal int? Count { get; init; }
    internal int? Width { get; init; }
    internal int? Height { get; init; }
    internal int? PixelBits { get; init; }
    internal int? Pages { get; init; }
    internal int? Frames { get; init; }
    internal bool? Animated { get; init; }
    internal bool? Lossless { get; init; }
    internal bool? MainDocumentPart { get; init; }
    internal string? Source { get; init; }
    internal string? Target { get; init; }
    internal string? Codec { get; init; }
    internal string? StreamType { get; init; }
    internal string? SchemaId { get; init; }
    internal FormatInputOrigin InputOrigin { get; init; }
    internal bool? PythonLauncher { get; init; }
}

/// <summary>Only fixed labels and measured numbers, kept separate from user-facing failures.</summary>
internal static class FormatFailureDiagnostics
{
    private static readonly ConditionalWeakTable<object, FormatFailureFact> Facts = new();
    private static readonly HashSet<string> Codecs = new(StringComparer.Ordinal)
    {
        "h264", "hevc", "av1", "vp8", "vp9", "mpeg1video", "mpeg2video", "mpeg4", "mjpeg", "png",
        "prores", "ffv1", "huffyuv", "aac", "apple_apac", "alac", "mp1", "mp2", "mp3", "flac",
        "ac3", "eac3", "dts", "truehd", "opus", "vorbis", "pcm_u8", "pcm_s8", "pcm_s16le",
        "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_f64le", "pcm_s16be", "pcm_s24be", "pcm_s32be",
        "pcm_f32be", "pcm_f64be", "pcm_alaw", "pcm_mulaw", "subrip", "ass", "ssa", "mov_text",
        "webvtt", "dvd_subtitle", "hdmv_pgs_subtitle", "dvb_subtitle", "bin_data", "klv"
    };

    internal static void Record(object owner, FormatFailureFact fact) => Facts.GetValue(owner, _ => fact);
    internal static T Attach<T>(T error, FormatFailureFact fact) where T : Exception { Record(error, fact); return error; }
    internal static T Transfer<T>(object source, T error) where T : class
    {
        if (Facts.TryGetValue(source, out var fact)) Record(error, fact);
        return error;
    }

    internal static FormatFailureFact Image(MediaTask task, FormatFailureStage stage, FormatFailureReason reason,
        MediaProbeResult? observed = null) => new(stage, reason)
        {
            Source = task.SourceFormatId, Target = task.TargetFormatId,
            Lossless = task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase),
            Width = (observed ?? task.Probe).Width, Height = (observed ?? task.Probe).Height,
            PixelBits = (observed ?? task.Probe).PixelFormat is { Length: <= 64 } pixel
                ? ImagePreservationPolicy.PixelBitDepth(pixel) : null,
            Pages = (observed ?? task.Probe).PageCount, Animated = (observed ?? task.Probe).IsAnimated
        };

    internal static string? Format(object owner)
    {
        var origin = owner;
        if (!Facts.TryGetValue(origin, out var fact))
        {
            if (origin is not Exception exception) return null;
            for (var depth = 0; depth < 8 && exception.InnerException is { } inner; depth++)
            {
                exception = inner;
                if (Facts.TryGetValue(exception, out fact)) { origin = exception; break; }
            }
            if (fact is null) return null;
        }
        var line = new StringBuilder("Format diagnostic: ");
        line.Append("stage=").Append(SafeEnum(fact.Stage)).Append(" reason=").Append(SafeEnum(fact.Reason));
        Add(line, "header_size", fact.HeaderSize); Add(line, "total_bytes", fact.TotalBytes);
        Add(line, "block_size", fact.BlockSize); Add(line, "version", fact.Version);
        Add(line, "alignment", fact.Alignment); Add(line, "reserved", fact.Reserved);
        Add(line, "input_length", fact.InputLength); Add(line, "index", fact.Index);
        Add(line, "start", fact.Start); Add(line, "end", fact.End);
        Add(line, "pdf_header_offset", fact.PdfHeaderOffset); Add(line, "prefix_observed_bytes", fact.PrefixObservedBytes);
        Add(line, "count", fact.Count); Add(line, "width", fact.Width); Add(line, "height", fact.Height);
        Add(line, "pixel_bits", fact.PixelBits); Add(line, "pages", fact.Pages); Add(line, "frames", fact.Frames);
        Add(line, "animated", fact.Animated); Add(line, "lossless", fact.Lossless); Add(line, "main_part", fact.MainDocumentPart);
        line.Append(" input_origin=").Append(SafeEnum(fact.InputOrigin));
        Add(line, "python_launcher", fact.PythonLauncher);
        if (fact.Source is not null) line.Append(" source=").Append(SafeFormat(fact.Source));
        if (fact.Target is not null) line.Append(" target=").Append(SafeFormat(fact.Target));
        if (fact.Codec is not null) line.Append(" codec=").Append(Codecs.Contains(fact.Codec) ? fact.Codec : "unknown");
        if (fact.StreamType is not null) line.Append(" stream_type=").Append(fact.StreamType is "video" or "audio" or "subtitle" or "attachment" or "data" ? fact.StreamType : "unknown");
        if (fact.SchemaId is not null) line.Append(" schema=").Append(fact.SchemaId is "Sch_UndeclaredAttribute" or "Sch_UnexpectedElementContentExpectingComplex" or
            "Sch_InvalidChildSequence" or "Sch_IncompleteContentExpectingComplex" or "Sch_EnumerationConstraintFailed" or "Sch_AttributeValueDataTypeDetailed" or
            "Sch_AttributeValueDataType" or "Sch_RequiredAttributeMissing" ? fact.SchemaId : "other");
        if (origin is Exception error)
        {
            line.Append(" exception=").Append(error switch
            {
                InvalidDataException => "InvalidDataException", IOException => "IOException",
                UnauthorizedAccessException => "UnauthorizedAccessException", NotSupportedException => "NotSupportedException",
                System.Runtime.InteropServices.COMException => "COMException", Win32Exception => "Win32Exception", _ => "other"
            }).Append(" hresult=0x").Append(error.HResult.ToString("X8", CultureInfo.InvariantCulture));
            var native = error is Win32Exception win32 ? win32.NativeErrorCode :
                (error.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? error.HResult & 0xffff : (int?)null;
            line.Append(" native=").Append(native?.ToString(CultureInfo.InvariantCulture) ?? "none");
        }
        return line.ToString();
    }

    internal static void WriteApp(object owner, Guid? taskId = null)
    {
        var line = Format(owner);
        if (line is null) return;
        if (taskId is { } task) line += $" task={task:N}";
        try { LogService.WriteApp(line); } catch (Exception) { /* Diagnostics cannot change conversion policy. */ }
    }

    internal static void WriteTask(string logPath, object owner, Guid taskId)
    {
        var line = Format(owner);
        if (line is null) return;
        try
        {
            LogService.AppendTask(logPath, line + $" task={taskId:N}\n");
            LogService.FlushTask(logPath);
        }
        catch (Exception) { /* Preserve the original conversion failure. */ }
    }

    private static string SafeEnum<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
    private static string SafeFormat(string value) => value.Length <= 64 ? FormatCatalog.FindById(value)?.Id ?? "unknown" : "unknown";
    private static void Add<T>(StringBuilder line, string name, T? value) where T : struct
    {
        if (value is { } measured) line.Append(' ').Append(name).Append('=').Append(
            measured is bool boolean ? (boolean ? "true" : "false") : Convert.ToString(measured, CultureInfo.InvariantCulture));
    }
}
