using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal sealed class FfmpegErrorCapture
{
    private const int MaximumTailCharacters = BoundedProcessOutput.TailBytes / 4;
    internal const int MaximumFailureCharacters = 2048;
    private readonly Queue<string> _lastLines = new();
    private int _tailCharacters;
    private int _failurePriority;
    public IReadOnlyList<string> LastLines => _lastLines.ToArray();
    public string? FailureDetail { get; private set; }

    public void Add(string line)
    {
        var priority = FailurePriority(line);
        if (priority > _failurePriority)
        {
            var length = Math.Min(line.Length, MaximumFailureCharacters);
            if (length < line.Length && char.IsHighSurrogate(line[length - 1])) length--;
            FailureDetail = line[..length];
            _failurePriority = priority;
        }
        var captured = line.Length > MaximumTailCharacters ? line[^MaximumTailCharacters..] : line;
        if (captured.Length > 0 && char.IsLowSurrogate(captured[0])) captured = captured[1..];
        _lastLines.Enqueue(captured);
        _tailCharacters += captured.Length;
        while (_lastLines.Count > 20 || _tailCharacters > MaximumTailCharacters)
            _tailCharacters -= _lastLines.Dequeue().Length;
    }

    internal static string BuildFailureMessage(MediaTask task, IReadOnlyList<string> errorLines, string? failureDetail = null)
    {
        // A decoder failure can be followed by encoder EOF and an empty output.
        // Keep the diagnostic even when that shutdown sequence exceeds the tail.
        var detail = failureDetail ?? errorLines
            .Select(line => (Line: line, Priority: FailurePriority(line)))
            .Where(item => item.Priority > 0)
            .OrderByDescending(item => item.Priority)
            .Select(item => item.Line)
            .FirstOrDefault()
            ?? errorLines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?? LocalizationService.T("FfmpegNoReason");
        if (task.ConversionMode == "lossless")
        {
            return LocalizationService.T("LosslessFailed", detail);
        }
        if (Path.GetExtension(task.InputPath).Equals(".m4s", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationService.T("M4sFailed", detail);
        }
        return LocalizationService.T("ConversionFailed", detail);
    }

    private static int FailurePriority(string line)
    {
        if (line.Contains("no decoder found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unknown decoder", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error submitting packet to decoder", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error sending packet to decoder", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error while decoding stream", StringComparison.OrdinalIgnoreCase)) return 3;
        if ((line.Contains("Could not find codec parameters", StringComparison.OrdinalIgnoreCase) &&
             !line.Contains("(Attachment:", StringComparison.OrdinalIgnoreCase)) ||
            line.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Neither number of channels nor channel layout specified", StringComparison.OrdinalIgnoreCase)) return 2;
        if (IsX264AllocationFailure(line) || IsMatroskaMissingTimestampFailure(line) || IsAacChannelLayoutFailure(line) ||
            IsMp4InvalidDurationFailure(line) ||
            (line.Contains("Non-monotonic DTS;", StringComparison.OrdinalIgnoreCase) &&
             line.Contains("Error submitting a packet to the muxer:", StringComparison.OrdinalIgnoreCase)) ||
            line.Contains("Subtitle encoding failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error initializing output stream", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error initializing filter", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error reinitializing filters", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Failed to configure output pad", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Trailing garbage after stream specifier", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Failed to set value", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static bool IsMp4InvalidDurationFailure(string line)
    {
        // Keep the muxer's complete fatal cause ahead of its shutdown messages.
        const string prefix = "[mp4 @ ";
        const string message = "Application provided duration: ";
        const string separator = " in stream ";
        const string suffix = " is invalid";
        if (line.Length > MaximumFailureCharacters || line.Contains('\r') || line.Contains('\n')) return false;
        var text = line.AsSpan().Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var loggerEnd = text.IndexOf(']');
        if (loggerEnd <= prefix.Length || loggerEnd - prefix.Length > 32) return false;
        foreach (var character in text[prefix.Length..loggerEnd])
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-') return false;

        var payload = text[(loggerEnd + 1)..].Trim();
        if (payload.Length <= message.Length + suffix.Length ||
            !payload.StartsWith(message, StringComparison.OrdinalIgnoreCase) ||
            !payload.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var values = payload[message.Length..^suffix.Length];
        var streamStart = values.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (streamStart < 0) return false;
        var duration = values[..streamStart];
        var stream = values[(streamStart + separator.Length)..];
        if (duration.IsEmpty || duration.Length > 20 || stream.IsEmpty || stream.Length > 10) return false;
        var durationDigits = duration[0] == '-' ? duration[1..] : duration;
        return !durationDigits.IsEmpty && durationDigits.IndexOfAnyExceptInRange('0', '9') < 0 &&
            stream.IndexOfAnyExceptInRange('0', '9') < 0 &&
            long.TryParse(duration, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out _) &&
            int.TryParse(stream, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static bool IsAacChannelLayoutFailure(string line)
    {
        // AAC init rejects unsupported layouts with EINVAL; keep that cause
        // ahead of the subsequent generic encoder-open failure.
        const string prefix = "[aac @ ";
        const string message = "Unsupported channel layout \"";
        if (line.Contains('\r') || line.Contains('\n')) return false;
        var text = line.AsSpan().Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var loggerEnd = text.IndexOf(']');
        if (loggerEnd <= prefix.Length || loggerEnd - prefix.Length > 32) return false;
        foreach (var character in text[prefix.Length..loggerEnd])
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-') return false;

        var payload = text[(loggerEnd + 1)..].Trim();
        if (!payload.StartsWith(message, StringComparison.OrdinalIgnoreCase) ||
            !payload.EndsWith("\"", StringComparison.Ordinal) || payload.Length <= message.Length + 1) return false;
        var layout = payload[message.Length..^1];
        // FFmpeg formats this field into a 64-byte buffer, including its NUL.
        if (layout.Length > 63) return false;
        foreach (var character in layout)
            if (character < ' ' || character > '~' || character is '"' or '[' or ']') return false;
        return true;
    }

    private static bool IsMatroskaMissingTimestampFailure(string line)
    {
        // FFmpeg's Matroska muxer immediately rejects AV_NOPTS_VALUE with EINVAL.
        // Match its complete fatal line, without promoting timestamp warnings.
        const string prefix = "[matroska @ ";
        var text = line.AsSpan().Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            text.Contains('\r') || text.Contains('\n')) return false;
        var loggerEnd = text.IndexOf(']');
        return loggerEnd > prefix.Length && text[(loggerEnd + 1)..].Trim().Equals(
            "Can't write packet with unknown timestamp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsX264AllocationFailure(string line)
    {
        const string prefix = "x264 [error]: malloc of size ";
        const string suffix = " failed";
        var diagnostic = line.AsSpan().Trim();
        if (!diagnostic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !diagnostic.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Length <= prefix.Length + suffix.Length) return false;

        var requestedBytes = diagnostic[prefix.Length..^suffix.Length];
        return requestedBytes.IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
