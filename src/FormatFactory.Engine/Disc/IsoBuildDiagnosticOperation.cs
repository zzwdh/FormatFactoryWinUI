using System.Diagnostics;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Disc;

internal enum IsoBuildPhase
{
    Unknown, Validate, PrepareOutput, PlatformCheck, CreateFileSystem, ConfigureFileSystem,
    AddTree, OpenSource, AddFile, CreateResultImage, OpenResultStream, CreateOutputStream,
    ReadImageStream, WriteImageStream, FlushOutput, Commit, ReadResult
}

/// <summary>Failure observations contain no source names, paths, volume labels or COM text.</summary>
internal sealed class IsoBuildDiagnosticOperation(Action<string>? write = null)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Action<string> _write = write ?? SafeDiagnostics.Write;
    internal Guid Id { get; } = Guid.NewGuid();
    internal IsoBuildPhase Phase { get; set; }
    internal int? SourceCount { get; set; }
    internal int CompletedSources { get; set; }
    internal IsoFileSystems? FileSystems { get; set; }
    internal int? BlockSize { get; set; }
    internal long? TotalBytes { get; set; }
    internal long CompletedBytes { get; set; }

    internal T Observe<T>(IsoBuildPhase phase, Func<T> work)
    {
        Phase = phase;
        try { return work(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { Fail(error); throw; }
    }

    internal void Fail(Exception error)
    {
        try
        {
            if (error is OperationCanceledException) return;
            RuntimeFailureDiagnostics.Record(error, new(RuntimeFailureStage.Unknown, Id, 1,
                (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds, false, SourceCount));
            _write(Format(error));
        }
        catch (Exception) { /* A diagnostic failure cannot replace the original ISO failure. */ }
    }

    internal string Format(Exception error)
    {
        var phase = Enum.IsDefined(Phase) ? Phase.ToString() : "Unknown";
        var systems = FileSystems is { } value && (int)value is 1 or 3 or 4 or 5 or 7
            ? ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
        return FormattableString.Invariant(
            $"ISO build observation: session={RuntimeFailureDiagnostics.SessionId:N} operation={Id:N} phase={phase} target=iso elapsed_ms={(long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds} source_count={RuntimeFailureDiagnostics.Number(SourceCount)} completed_sources={CompletedSources} filesystems={systems} block_size={RuntimeFailureDiagnostics.Number(BlockSize)} total_bytes={RuntimeFailureDiagnostics.Number(TotalBytes)} completed_bytes={CompletedBytes} {RuntimeFailureDiagnostics.ExceptionFields(error)}\n");
    }
}
