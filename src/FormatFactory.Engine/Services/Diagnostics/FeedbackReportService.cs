using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FormatFactoryWinUI.Services;

/// <summary>Builds a bounded, sanitized report; local error persistence is separate from delivery.</summary>
public sealed class FeedbackReportService
{
    internal const int MaximumLogBytes = 128 * 1024;
    private const int MaximumTextBytes = 8 * 1024;
    private const int MaximumMarkerBytes = 32 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Authorization = new(@"(?im)\b(?:proxy-)?authorization\s*[:=]\s*[^\r\n]+", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex Secret = new("(?i)([\"']?\\b(?:password|passwd|pwd|token|access[_-]?token|refresh[_-]?token|api[_-]?key|secret|client[_-]?secret)\\b[\"']?\\s*[:=]\\s*)(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s,;}\\r\\n]+)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SecretArgument = new(@"(?i)(--?(?:password|passwd|pwd|token|api[_-]?key|secret)\s+)(?:""[^""\r\n]*""|'[^'\r\n]*'|\S+)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex Url = new("(?i)\\b(?:https?|ftp|file)://[^\\s\"'<>]+", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex Email = new(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    // Punctuation and spaces are valid path characters. A quoted value gives us
    // a boundary; an ambiguous bare path is conservatively removed to line end.
    private static readonly Regex QuotedWindowsPath = new(
        """(?i)"(?:[A-Z]:[\\/]|\\\\)[^"\r\n]*"|'(?:[A-Z]:[\\/]|\\\\)[^\r\n]*'""",
        RegexOptions.Compiled | RegexOptions.NonBacktracking, RegexTimeout);
    private static readonly Regex WindowsPath = new("(?i)(?:[A-Z]:[\\\\/]|\\\\\\\\)[^\\r\\n\\t\"<>|]*", RegexOptions.Compiled | RegexOptions.NonBacktracking, RegexTimeout);
    // Old markers can retain a filename tail without any directory separator.
    // Its punctuation boundary is ambiguous, so remove the rest of that line.
    private static readonly Regex LegacyWindowsPathSuffix = new(@"\[path\][,;'][^\r\n]*",
        RegexOptions.Compiled | RegexOptions.NonBacktracking, RegexTimeout);
    private static readonly Regex UnixPath = new("(?<![\\w:])/(?:[^\\s\"'<>|\\r\\n]+)", RegexOptions.Compiled, RegexTimeout);
    private static readonly ConcurrentDictionary<string, object> ProcessGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    private readonly MarkerState _state;
    private readonly string _logsRoot;
    private readonly string _markerPath;

    public FeedbackReportService(string? dataRoot = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot ?? AppDataPaths.Root));
        _logsRoot = Path.Combine(root, "Logs");
        _markerPath = Path.Combine(root, "feedback-error.json");
        // The shared state also serves as the lock for every service using this marker path.
        _gate = ProcessGates.GetOrAdd(_markerPath, static _ => new MarkerState());
        _state = (MarkerState)_gate;
    }

    public bool HasPendingError
    {
        get
        {
            lock (_gate)
            {
                LoadMarker();
                return _state.Latest is not null;
            }
        }
    }

    /// <summary>Best-effort local persistence, safe to call from conversion/error handling.</summary>
    public void RecordError(string? message, string? taskLogPath = null)
        => RecordError(message, taskLogPath, null);

    internal void RecordError(string? message, string? taskLogPath, ErrorCorrelationFacts? correlation)
        => RecordErrorCore(message, taskLogPath, null, correlation);

    public void RecordException(Exception exception, string? taskLogPath = null)
        => RecordException(exception, taskLogPath, null);

    internal void RecordException(Exception exception, string? taskLogPath, ErrorCorrelationFacts? correlation)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var observation = RuntimeFailureDiagnostics.Get(exception);
        if (correlation is null && observation is { Operation: var operation, Stage: RuntimeFailureStage.ActivationRead or
                RuntimeFailureStage.ActivationRegister or RuntimeFailureStage.ActivationRedirect or RuntimeFailureStage.ActivationDispatch })
        {
            correlation = new(ErrorCorrelationKind.Activation, operation, null);
        }
        RecordErrorCore(exception.ToString(), taskLogPath, new(RuntimeFailureDiagnostics.ExceptionType(exception),
            exception.HResult, RuntimeFailureDiagnostics.NativeCode(exception), observation?.Stage ?? RuntimeFailureStage.Unknown,
            observation?.Operation ?? Guid.Empty, observation?.Attempt, observation?.ElapsedMs, observation?.Recovered), correlation);
    }

    private void RecordErrorCore(string? message, string? taskLogPath, ErrorSafetyFacts? facts, ErrorCorrelationFacts? correlation)
    {
        lock (_gate)
        {
            string? temporaryPath = null;
            try
            {
                LoadMarker();
                _state.LastChangedAt = NextChangeTime();
                _state.Latest = new ErrorMarker(Guid.NewGuid(), _state.LastChangedAt,
                    LimitUtf8(Redact(LimitUtf8(NormalizeErrorMessage(message), MaximumTextBytes)), MaximumTextBytes),
                    SafeLogPath(taskLogPath) is { } path ? Path.GetRelativePath(_logsRoot, path) : null,
                    facts, message?.Length ?? 0,
                    typeof(FeedbackReportService).Assembly.GetName().Version?.ToString(), RuntimeFailureDiagnostics.SessionId, correlation);
                _state.HasUnpersistedChange = true;
                Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
                temporaryPath = $"{_markerPath}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state.Latest), Encoding.UTF8);
                File.Move(temporaryPath, _markerPath, overwrite: true);
                _state.HasUnpersistedChange = false;
            }
            catch (Exception ex)
            {
                // Diagnostics must never change whether a conversion succeeds or fails.
                Debug.WriteLine($"Could not record feedback error: {ex.GetType().Name}");
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
    }

    public void ClearPendingError()
    {
        lock (_gate)
        {
            LoadMarker();
            ClearMarker();
        }
    }

    /// <summary>A submitted snapshot must not acknowledge a newer error recorded during delivery.</summary>
    public void ClearPendingError(FeedbackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            LoadMarker();
            if (report.ErrorId is { } id && _state.Latest?.Id == id)
            {
                ClearMarker();
            }
        }
    }

    public async Task<FeedbackReport> CreateAsync(string description, bool includeLogs, string appVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ErrorMarker? error;
        lock (_gate)
        {
            LoadMarker();
            error = _state.Latest;
        }

        var createdAt = DateTimeOffset.UtcNow;
        var safeVersion = LimitUtf8(Redact(LimitUtf8(appVersion ?? string.Empty, 1024)), 1024);
        var safeDescription = LimitUtf8(Redact(LimitUtf8(description ?? string.Empty, MaximumTextBytes)), MaximumTextBytes);
        var safeError = includeLogs && error is not null
            ? LimitUtf8(Redact(error.Message), MaximumTextBytes) : null;
        var metadata = $"FluentFormatConverter feedback report\n" +
            $"Created (UTC): {createdAt:O}\n" +
            $"App version: {safeVersion}\n" +
            $"OS: {RuntimeInformation.OSDescription}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\n" +
            $"Logs included: {includeLogs}\n\nDescription:\n{safeDescription}\n";
        if (includeLogs)
        {
            metadata += $"\nLatest error (UTC): {error?.OccurredAt.ToString("O") ?? "none"}\n{safeError ?? string.Empty}\n";
            metadata += FormattableString.Invariant(
                $"\nReport correlation: scope=report_creation report_session={RuntimeFailureDiagnostics.SessionId:N} marker={error?.Id.ToString("N") ?? "none"} original_characters={error?.OriginalCharacters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} redaction_omitted={safeError == "[content omitted: redaction exceeded time limit]"}\n");
            if (error is not null)
            {
                // A pending marker may survive an upgrade or restart. Never infer its origin from this report.
                var originVersion = NormalizeOriginVersion(error.OriginVersion) ?? "unknown";
                var originSession = error.OriginSession is { } session && session != Guid.Empty
                    ? session.ToString("N") : "none";
                metadata += $"Error origin: scope=marker_creation assembly_version={originVersion} session={originSession}\n";
            }
            if (error?.Safety is { } facts)
            {
                // Marker files are input too: render only a small fixed vocabulary and typed facts.
                var kind = AllowedExceptionKinds.Contains(facts.Kind) ? facts.Kind : "Other";
                var stage = Enum.IsDefined(facts.Stage) ? facts.Stage : RuntimeFailureStage.Unknown;
                metadata += FormattableString.Invariant($"Reported exception facts: exception={kind} hresult=0x{facts.HResult:X8} native={RuntimeFailureDiagnostics.Number(facts.Native)} stage={stage} operation={facts.Operation:N} attempt={RuntimeFailureDiagnostics.Number(facts.Attempt)} elapsed_ms={RuntimeFailureDiagnostics.Number(facts.ElapsedMs)} recovered={facts.Recovered?.ToString() ?? "none"}\n");
            }
            if (error?.Correlation is { } correlation && Enum.IsDefined(correlation.Kind) &&
                (Present(correlation.Operation) || Present(correlation.TaskId)))
            {
                metadata += $"Error correlation: scope=marker_creation kind={correlation.Kind} operation={Identifier(correlation.Operation)} task={Identifier(correlation.TaskId)}\n";
            }
        }
        var parts = new List<(string Name, string Content)> { ("report.txt", metadata) };
        if (includeLogs)
        {
            await AddLogAsync(parts, "logs/application.log", Path.Combine(_logsRoot, "application.log"), cancellationToken);
            if (error?.TaskLogRelativePath is { } relativePath)
            {
                var taskLogPath = Path.Combine(_logsRoot, relativePath);
                await AddLogAsync(parts, "logs/last-error-task.log", taskLogPath, cancellationToken);
                await AddLogAsync(parts, "logs/last-error-task.stderr.log", taskLogPath + ".stderr", cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var reportDirectory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", "feedback-reports");
        Directory.CreateDirectory(reportDirectory);
        var archivePath = Path.Combine(reportDirectory, $"feedback-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var part in parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(part.Name, CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(Encoding.UTF8.GetBytes(part.Content), cancellationToken);
                }
            }
            var preview = string.Join("\n\n", parts.Select(part => $"[{part.Name}]\n{part.Content}"));
            return new FeedbackReport(archivePath, preview, parts.Count - 1, includeLogs ? error?.Id : null,
                safeVersion, safeDescription, safeError, createdAt);
        }
        catch
        {
            TryDelete(archivePath);
            throw;
        }
    }

    private async Task AddLogAsync(List<(string Name, string Content)> parts, string name, string candidate,
        CancellationToken cancellationToken)
    {
        var path = SafeLogPath(candidate);
        if (path is null)
            return;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            var length = stream.Length;
            var count = (int)Math.Min(length, MaximumLogBytes);
            stream.Seek(length - count, SeekOrigin.Begin);
            var bytes = new byte[count];
            var read = 0;
            while (read < count)
            {
                var received = await stream.ReadAsync(bytes.AsMemory(read, count - read), cancellationToken);
                if (received == 0)
                    break;
                read += received;
            }
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            if (length > count)
            {
                // A tail can begin inside a path or credential. Discard that partial line.
                var newline = text.IndexOf('\n');
                text = newline >= 0 ? text[(newline + 1)..] : "[log line exceeded size limit]";
            }
            parts.Add((name, LimitUtf8(Redact(text), MaximumLogBytes)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string? SafeLogPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        try
        {
            var path = Path.GetFullPath(candidate);
            var prefix = Path.TrimEndingDirectorySeparator(_logsRoot) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !(path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                  path.EndsWith(".log.stderr", StringComparison.OrdinalIgnoreCase)))
                return null;
            // A lexical child can point outside Logs through a junction or symlink.
            for (var current = path; current is not null && current.Length >= _logsRoot.Length;
                current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return null;
            }
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void LoadMarker()
    {
        try
        {
            var marker = ReadMarker();
            if (_state.HasUnpersistedChange)
            {
                // An older disk snapshot must not replace a record whose best-effort write failed.
                // The same rule keeps a failed deletion acknowledged for the current process.
                if ((marker is null && _state.Latest is not null) ||
                    (marker is not null && marker.OccurredAt <= _state.LastChangedAt))
                    return;
            }
            _state.Latest = marker;
            _state.HasUnpersistedChange = false;
            if (marker is not null && marker.OccurredAt > _state.LastChangedAt)
                _state.LastChangedAt = marker.OccurredAt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Could not read feedback error: {ex.GetType().Name}");
        }
    }

    private ErrorMarker? ReadMarker()
    {
        if (!File.Exists(_markerPath))
            return null;
        using var stream = new FileStream(_markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumMarkerBytes)
            return null;
        var bytes = new byte[MaximumMarkerBytes + 1];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0)
                break;
            read += count;
        }
        if (read > MaximumMarkerBytes)
            return null;
        var start = read >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var marker = JsonSerializer.Deserialize<ErrorMarker>(bytes.AsSpan(start, read - start));
        return marker is not null && marker.Message is not null
            ? marker with { Message = Redact(LimitUtf8(NormalizeErrorMessage(marker.Message), MaximumTextBytes)) }
            : null;
    }

    private DateTimeOffset NextChangeTime()
    {
        var now = DateTimeOffset.UtcNow;
        return now > _state.LastChangedAt ? now : _state.LastChangedAt.AddTicks(1);
    }

    private void ClearMarker()
    {
        _state.Latest = null;
        _state.LastChangedAt = NextChangeTime();
        _state.HasUnpersistedChange = true;
        try
        {
            File.Delete(_markerPath);
            _state.HasUnpersistedChange = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string NormalizeErrorMessage(string? message) => string.IsNullOrWhiteSpace(message)
        ? "FluentFormatConverter reported an error without diagnostic details." : message;

    private static string Redact(string value)
    {
        try
        {
            value = Authorization.Replace(value, "Authorization: [redacted]");
            value = Secret.Replace(value, "$1[redacted]");
            value = SecretArgument.Replace(value, "$1[redacted]");
            value = Url.Replace(value, "[url]");
            value = Email.Replace(value, "[email]");
            value = LegacyWindowsPathSuffix.Replace(value, "[path]");
            // Use one unambiguous quote style so repeated redaction does not
            // mistake a freshly sanitized single-quoted value for a legacy tail.
            value = QuotedWindowsPath.Replace(value, "\"[path]\"");
            value = WindowsPath.Replace(value, "[path]");
            return UnixPath.Replace(value, "[path]");
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed if unusually complex log text cannot be redacted within the budget.
            return "[content omitted: redaction exceeded time limit]";
        }
    }

    private static string LimitUtf8(string value, int maximumBytes)
    {
        if (value.Length <= maximumBytes && Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return value;
        // Bound the intermediate allocation too, including a maliciously large description.
        var text = value.AsSpan(0, Math.Min(value.Length, maximumBytes));
        var bytes = new byte[maximumBytes];
        Encoding.UTF8.GetEncoder().Convert(text, bytes, flush: false, out _, out var bytesUsed, out _);
        return Encoding.UTF8.GetString(bytes, 0, bytesUsed);
    }

    internal static void TryDelete(string? path)
    {
        if (path is null)
            return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly HashSet<string> AllowedExceptionKinds = new(StringComparer.Ordinal)
    {
        "FileNotFoundException", "DirectoryNotFoundException", "UnauthorizedAccessException", "InvalidDataException", "IOException",
        "TimeoutException", "Win32Exception", "COMException", "JsonException", "OperationCanceledException", "NullReferenceException",
        "ArgumentException", "NotSupportedException", "InvalidOperationException", "OutOfMemoryException", "LayoutCycleException", "Other"
    };
    private sealed record ErrorSafetyFacts(string Kind, int HResult, int? Native, RuntimeFailureStage Stage,
        Guid Operation, int? Attempt, long? ElapsedMs, bool? Recovered);
    private static string? NormalizeOriginVersion(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => character is >= '0' and <= '9' or '.')
        && Version.TryParse(value, out var version) ? version.ToString() : null;

    private static bool Present(Guid? id) => id.HasValue && id.Value != Guid.Empty;
    private static string Identifier(Guid? id) => id is { } value && value != Guid.Empty ? value.ToString("N") : "none";

    private sealed record ErrorMarker(Guid Id, DateTimeOffset OccurredAt, string Message, string? TaskLogRelativePath,
        ErrorSafetyFacts? Safety = null, int? OriginalCharacters = null, string? OriginVersion = null,
        Guid? OriginSession = null, ErrorCorrelationFacts? Correlation = null);

    private sealed class MarkerState
    {
        public ErrorMarker? Latest { get; set; }
        public DateTimeOffset LastChangedAt { get; set; }
        public bool HasUnpersistedChange { get; set; }
    }
}

/// <summary>Owns one temporary ZIP. Preview contains exactly the text stored in its entries.</summary>
public sealed record FeedbackReport : IDisposable
{
    internal FeedbackReport(string archivePath, string preview, int logCount, Guid? errorId = null,
        string appVersion = "", string description = "", string? errorMessage = null, DateTimeOffset? createdAt = null)
    {
        ArchivePath = archivePath;
        Preview = preview;
        LogCount = logCount;
        ErrorId = errorId;
        AppVersion = appVersion;
        Description = description;
        ErrorMessage = errorMessage;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }

    public string ArchivePath { get; }
    public string Preview { get; }
    public int LogCount { get; }
    internal Guid? ErrorId { get; }
    // These exact strings are already present in the preview and ZIP's report.txt.
    internal string AppVersion { get; }
    internal string Description { get; }
    internal string? ErrorMessage { get; }
    internal DateTimeOffset CreatedAt { get; }

    public void Dispose() => FeedbackReportService.TryDelete(ArchivePath);
}
