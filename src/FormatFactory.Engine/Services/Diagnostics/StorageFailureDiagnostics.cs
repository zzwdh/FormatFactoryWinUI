using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace FormatFactoryWinUI.Services;

internal enum StorageFailureStage
{
    ResolveDirectory, CreateDirectory, WriteProbe, ReserveName, BudgetQuery, BudgetReserve,
    CommitReceipt, Commit, Preserve, PreserveFingerprint, PreserveCreate, PreserveCopy, PreserveVerify, PreserveIndex,
    RecoveryLookup, RecoveryVerify, RecoveryReserve, RecoveryCopy,
    RecoveryCommit, RecoveryCleanup, DisposeCleanup, SourceRequest, SourceImportValidation
}
internal enum StoragePathKind { Unknown, Relative, LocalRooted, Unc, Device }
internal enum StorageRecoveryState { NotAttempted, Indexed, Unindexed, Saved, Cancelled }
internal enum StorageSourceCheck { Identity, Size, Options, Request }

internal sealed record StorageBudgetObservation(long EstimatedBytes, long RequiredBytes, long CallerAvailableBytes,
    long AvailableAfterReservations, long OutstandingBytes, long WrittenBytes, int ActiveReservations);
internal sealed record StorageSourceObservation(StorageSourceCheck Check, int ExpectedInputs, int ActualInputs,
    long? ExpectedBytes = null, long? ActualBytes = null);
internal sealed record StorageFailureObservation(StorageFailureStage Stage, Guid OperationId, Guid? TaskId,
    StoragePathKind PathKind, bool? DirectoryOutput, StorageBudgetObservation? Budget = null,
    StorageRecoveryState Recovery = StorageRecoveryState.NotAttempted, StorageSourceObservation? Source = null,
    Guid? OriginalCommitOperation = null);

/// <summary>Only already-observed storage facts. Never reads paths, messages, Exception.Data, ACLs or extra file metadata.</summary>
internal static class StorageFailureDiagnostics
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly ConditionalWeakTable<Exception, StorageFailureObservation> Observations = new();

    internal static void Record(Exception error, StorageFailureObservation facts)
    {
        lock (Observations) Observations.GetValue(error, _ => facts);
    }

    internal static void RecoveryOutcome(Exception error, StorageRecoveryState state)
    {
        lock (Observations)
        {
            if (Observations.TryGetValue(error, out var facts))
            {
                Observations.Remove(error);
                Observations.Add(error, facts with { Recovery = state });
            }
        }
    }

    internal static StorageFailureObservation? Get(Exception error)
    {
        // Wrappers retain the earliest inner observation without interpreting their user-visible text.
        for (var depth = 0; error is not null && depth < 8; depth++, error = error.InnerException!)
            if (Observations.TryGetValue(error, out var facts)) return facts;
        return null;
    }

    internal static string? Format(Exception error)
    {
        var facts = Get(error);
        if (facts is null) return null;
        var origin = error;
        for (var depth = 0; depth < 8 && !Observations.TryGetValue(origin, out _); depth++)
        {
            if (origin.InnerException is null) break;
            origin = origin.InnerException;
        }
        var exception = origin switch
        {
            UnauthorizedAccessException => "UnauthorizedAccessException",
            FileNotFoundException => "FileNotFoundException",
            DirectoryNotFoundException => "DirectoryNotFoundException",
            InvalidDataException => "InvalidDataException",
            PathTooLongException => "PathTooLongException",
            IOException => "IOException",
            Win32Exception => "Win32Exception",
            OperationCanceledException => "OperationCanceledException",
            System.Text.Json.JsonException => "JsonException",
            ArgumentException => "ArgumentException",
            NotSupportedException => "NotSupportedException",
            InvalidOperationException => "InvalidOperationException",
            _ => "Other"
        };
        var native = origin is Win32Exception win32 ? win32.NativeErrorCode :
            (origin.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? origin.HResult & 0xFFFF : (int?)null;
        var line = FormattableString.Invariant(
            $"Storage diagnostic: session={SessionId:N} task={facts.TaskId?.ToString("N") ?? "none"} operation={facts.OperationId:N} stage={Safe(facts.Stage)} path_kind={Safe(facts.PathKind)} directory_output={(facts.DirectoryOutput?.ToString() ?? "none")} exception={exception} hresult=0x{origin.HResult:X8} native={Number(native)} recovery={Safe(facts.Recovery)}");
        if (facts.OriginalCommitOperation is { } original && original != Guid.Empty)
            line += $" original_commit_operation={original:N}";
        if (facts.Budget is { } budget)
            line += FormattableString.Invariant(
                $"\nStorage budget: estimated={budget.EstimatedBytes} required={budget.RequiredBytes} caller_available={budget.CallerAvailableBytes} available_after_reservations={budget.AvailableAfterReservations} outstanding={budget.OutstandingBytes} written={budget.WrittenBytes} active_reservations={budget.ActiveReservations} quota_limit=none");
        if (facts.Source is { } source)
            line += FormattableString.Invariant(
                $"\nStorage source validation: check={Safe(source.Check)} expected_inputs={source.ExpectedInputs} actual_inputs={source.ActualInputs} expected_bytes={Number(source.ExpectedBytes)} actual_bytes={Number(source.ActualBytes)}");
        if (error is OutputCommitException { PreservationError: { } preservationError } && Format(preservationError) is { } preservation)
            line += "\n" + preservation;
        return line;
    }

    internal static StoragePathKind ClassifyPath(string? path)
    {
        // Syntax only: drive type, mount/availability and ACL are deliberately not queried here.
        if (string.IsNullOrWhiteSpace(path)) return StoragePathKind.Unknown;
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
            return StoragePathKind.Device;
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return StoragePathKind.Unc;
        try { return Path.IsPathFullyQualified(path) ? StoragePathKind.LocalRooted : StoragePathKind.Relative; }
        catch (ArgumentException) { return StoragePathKind.Unknown; }
    }

    internal static void WriteRecorded(Exception error)
    {
        try { if (Format(error) is { } line) LogService.WriteApp(line); }
        catch (Exception) { /* Optional diagnostics must not replace a save failure. */ }
    }

    internal static void WriteRecoveryOutcome(Guid operation, Guid taskId, bool directoryOutput,
        StoragePathKind pathKind, StorageRecoveryState recovery, StorageFailureStage stage, Guid? originalCommitOperation)
    {
        try
        {
            LogService.WriteApp(FormattableString.Invariant(
                $"Storage recovery outcome: session={SessionId:N} task={taskId:N} operation={operation:N} stage={Safe(stage)} path_kind={Safe(pathKind)} directory_output={directoryOutput} recovery={Safe(recovery)} observed_utc={DateTimeOffset.UtcNow:O} original_commit_operation={originalCommitOperation?.ToString("N") ?? "none"}"));
        }
        catch (Exception) { /* Success of the committed output does not depend on logging. */ }
    }

    private static string Safe<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";
}
