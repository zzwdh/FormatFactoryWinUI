using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public enum WindowsFilterFailureKind
{
    None,
    WorkerMissing,
    Timeout,
    WorkerCrashed,
    InvalidResponse,
    OutputLimit,
    NotRegistered,
    NativeFailure,
    UnsupportedPlatform
}

public sealed record WindowsFilterExtractionResult(bool IsAvailable, string Text, string? Error = null)
{
    public WindowsFilterFailureKind FailureKind { get; init; }
}

/// <summary>Queries registration without loading filters; all third-party COM runs in a disposable worker.</summary>
public sealed class WindowsFilterTextExtractor
{
    internal const int ProtocolVersion = 1;
    internal const int MaximumResponseBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(60);
    private readonly string _workerExecutable;
    private readonly IReadOnlyList<string> _argumentPrefix;
    private readonly TimeSpan _timeout;

    public WindowsFilterTextExtractor()
        : this(Path.Combine(AppContext.BaseDirectory, "FormatFactory.FilterWorker.exe")) { }

    internal WindowsFilterTextExtractor(string workerExecutable, IReadOnlyList<string>? argumentPrefix = null,
        TimeSpan? timeout = null)
    {
        _workerExecutable = workerExecutable;
        _argumentPrefix = argumentPrefix?.ToArray() ?? [];
        _timeout = timeout ?? MaximumTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > MaximumTimeout) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public bool IsAvailable(string path) => OperatingSystem.IsWindows() &&
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) && File.Exists(_workerExecutable) && HasRegisteredFilter(path);

    [SupportedOSPlatform("windows")]
    private static bool HasRegisteredFilter(string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(extension)) return false;
            using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
            using var directHandler = extensionKey?.OpenSubKey("PersistentHandler");
            var handler = directHandler?.GetValue(null) as string;
            if (handler is null && extensionKey?.GetValue(null) is string programId)
            {
                using var programClass = Registry.ClassesRoot.OpenSubKey(programId + @"\CLSID");
                if (programClass?.GetValue(null) is string classId && Guid.TryParse(classId, out _))
                {
                    using var classHandler = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + classId + @"\PersistentHandler");
                    handler = classHandler?.GetValue(null) as string;
                }
            }
            if (!Guid.TryParse(handler, out _)) return false;
            using var registration = Registry.ClassesRoot.OpenSubKey(
                @"CLSID\" + handler + @"\PersistentAddinsRegistered\{89BCB740-6119-101A-BCB7-00DD010655AF}");
            if (registration?.GetValue(null) is not string filterId || !Guid.TryParse(filterId, out _)) return false;
            using var server = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + filterId + @"\InprocServer32");
            return server?.GetValue(null) is string { Length: > 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    public WindowsFilterExtractionResult Extract(string path, CancellationToken cancellationToken = default) =>
        ExtractAsync(path, cancellationToken).GetAwaiter().GetResult();

    public async Task<WindowsFilterExtractionResult> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(LocalizationService.T("ConversionUiExtractionDocumentNotFound"), fullPath);
        if (!OperatingSystem.IsWindows())
            return Failed(WindowsFilterFailureKind.UnsupportedPlatform, LocalizationService.T("ConversionUiIFilterWindowsOnly"));
        if (Path.IsPathFullyQualified(_workerExecutable) && !File.Exists(_workerExecutable))
            return Failed(WindowsFilterFailureKind.WorkerMissing);

        var request = JsonSerializer.Serialize(new WorkerRequest(ProtocolVersion, "extract", fullPath));
        if (Encoding.UTF8.GetByteCount(request) > 128 * 1024) return Failed(WindowsFilterFailureKind.InvalidResponse);
        try
        {
            var result = await ProcessRunner.RunAsync(_workerExecutable, [.. _argumentPrefix, "--stdio-v1"], _timeout,
                new ProcessRunOptions { StandardInput = request, MaximumOutputBytes = MaximumResponseBytes }, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) return Failed(WindowsFilterFailureKind.WorkerCrashed);
            var response = JsonSerializer.Deserialize<WorkerResponse>(result.StandardOutput);
            if (response is null || response.Version != ProtocolVersion || response.Text is null ||
                (response.IsAvailable && response.Failure is not null) ||
                (!response.IsAvailable && string.IsNullOrWhiteSpace(response.Failure)))
                return Failed(WindowsFilterFailureKind.InvalidResponse);
            if (response.IsAvailable) return new WindowsFilterExtractionResult(true, response.Text);
            // An incomplete response is never promoted to a successful partial document.
            return response.Failure switch
            {
                "not-registered" => Failed(WindowsFilterFailureKind.NotRegistered, LocalizationService.T("ConversionUiIFilterNotRegistered")),
                "output-limit" => Failed(WindowsFilterFailureKind.OutputLimit),
                "initialization-failed" => Failed(WindowsFilterFailureKind.NativeFailure,
                    LocalizationService.T("ConversionUiIFilterInitializationFailed", response.HResult ?? 0)),
                "chunk-read-failed" => Failed(WindowsFilterFailureKind.NativeFailure,
                    LocalizationService.T("ConversionUiIFilterChunkReadFailed", response.HResult ?? 0)),
                "text-read-failed" or "native-failure" => Failed(WindowsFilterFailureKind.NativeFailure,
                    LocalizationService.T("ConversionUiIFilterTextReadFailed", response.HResult ?? 0)),
                _ => Failed(WindowsFilterFailureKind.InvalidResponse)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProcessOutputLimitExceededException) { return Failed(WindowsFilterFailureKind.OutputLimit); }
        catch (TimeoutException) { return Failed(WindowsFilterFailureKind.Timeout); }
        catch (JsonException) { return Failed(WindowsFilterFailureKind.InvalidResponse); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Failed(WindowsFilterFailureKind.WorkerCrashed);
        }
    }

    private static WindowsFilterExtractionResult Failed(WindowsFilterFailureKind kind, string? message = null) =>
        new(false, string.Empty, message ?? LocalizationService.T("ConversionFailed", "IFilter: " + kind)) { FailureKind = kind };

    private sealed record WorkerRequest(int Version, string Operation, string Path);
    private sealed record WorkerResponse(int Version, bool IsAvailable, string Text, string? Failure, int? HResult);
    public static NormalizedDocument CreateDocument(string path, string text, string language = "zh-CN")
    {
        var blocks = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => new NormalizedParagraph(value.Replace('\n', ' ').Trim()))
            .Where(paragraph => paragraph.Text.Length > 0)
            .Cast<NormalizedBlock>()
            .ToArray();
        return new NormalizedDocument(
            Path.GetFileNameWithoutExtension(path),
            language,
            [new NormalizedSection("content", blocks)],
            []);
    }

}

