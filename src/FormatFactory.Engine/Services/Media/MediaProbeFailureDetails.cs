using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FormatFactoryWinUI.Services;

internal enum MediaProbeFailureStep { Image, Process, Parse, Metadata }
internal sealed record MediaProbeFailureDetail(MediaProbeFailureStep Step, int? ProcessExitCode,
    MediaProbeFailureEvidence? Evidence = null);

internal enum MediaProbeFailureCategory { ProcessExit, ProcessException, Timeout, Parse, Metadata, Unknown }
internal enum MediaProbeReportedReason { Unknown, AccessDenied, NotFound, InvalidData, EndOfFile, IoError }
internal sealed record MediaProbeFailureEvidence(MediaProbeFailureCategory Category, MediaProbeReportedReason ReportedReason,
    int? ReportedCode, long DurationMilliseconds, int OutputCharacters, int ErrorCharacters, bool OutputTruncated,
    MediaProbeSafeFacts? Facts);

/// <summary>Retains only locally observed probe facts without wrapping exceptions or reading their data/messages.</summary>
internal static class MediaProbeFailureDetails
{
    private static readonly ConditionalWeakTable<Exception, MediaProbeFailureDetail> Failures = new();

    internal static bool CanDescribe(Exception exception) =>
        exception is IOException or InvalidDataException or JsonException or TimeoutException or Win32Exception;

    internal static void Record(Exception exception, MediaProbeFailureStep step, int? exitCode,
        MediaProbeFailureEvidence? evidence = null)
    {
        // The earliest observation is the origin; callers must not overwrite it during propagation.
        Failures.GetValue(exception, _ => new(step, exitCode, evidence));
    }

    internal static MediaProbeFailureDetail? Get(Exception exception) =>
        Failures.TryGetValue(exception, out var detail) ? detail : null;

    // Only accept the bounded ERROR section emitted by -show_error. The value is
    // a tool-reported signal, never a confirmed filesystem cause or native code.
    internal static (MediaProbeReportedReason Reason, int? Code) ReadReportedError(ProcessExecutionResult? result)
    {
        if (result is null || result.ExitCode == 0 || result.OutputTruncated || result.StandardOutput.Length > 64 * 1024)
            return (MediaProbeReportedReason.Unknown, null);
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number) ||
                !error.TryGetProperty("string", out var text) || text.ValueKind != JsonValueKind.String)
                return (MediaProbeReportedReason.Unknown, null);
            var reason = (number, text.GetString()) switch
            {
                (-13, "Permission denied") => MediaProbeReportedReason.AccessDenied,
                (-2, "No such file or directory") => MediaProbeReportedReason.NotFound,
                (-1094995529, "Invalid data found when processing input") => MediaProbeReportedReason.InvalidData,
                (-541478725, "End of file") => MediaProbeReportedReason.EndOfFile,
                (-5, "Input/output error") => MediaProbeReportedReason.IoError,
                _ => MediaProbeReportedReason.Unknown
            };
            return (reason, reason == MediaProbeReportedReason.Unknown ? null : number);
        }
        catch (JsonException) { return (MediaProbeReportedReason.Unknown, null); }
        catch (InvalidOperationException) { return (MediaProbeReportedReason.Unknown, null); }
    }
}
