using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FormatFactoryWinUI.Services;

internal enum StorageTokenDiagnosticAction { Unknown, Remember, Resolve, Recover, Release }
internal enum StorageTokenDiagnosticKind { Unknown, File, Folder }
internal enum StorageTokenDiagnosticStage
{
    Unknown, ItemOpen, NormalizePath, AcquireLock, RegistryRead, FindExisting,
    FutureAccessCheck, FutureAccessAdd, RestoreItem, RegistryUpdate,
    CreateDirectory, RegistryWrite, RegistryFlush, RegistryCommit, RegistryCleanup, FutureAccessRemove
}

/// <summary>Fixed labels and observed booleans only; never accepts a token, path or storage item.</summary>
internal sealed class StorageTokenDiagnosticOperation(StorageTokenDiagnosticAction action,
    StorageTokenDiagnosticKind kind = StorageTokenDiagnosticKind.Unknown, bool tokenPresent = false)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly ConditionalWeakTable<Exception, object> _reported = new();
    internal Guid Id { get; } = Guid.NewGuid();
    internal StorageTokenDiagnosticStage Stage { get; set; }
    internal StorageTokenDiagnosticKind Kind { get; set; } = kind;
    internal bool TokenPresent { get; set; } = tokenPresent;

    internal void Fail(Exception error)
    {
        try
        {
            if (_reported.TryGetValue(error, out _)) return;
            _reported.Add(error, new());
            SafeDiagnostics.Write(Format(error));
        }
        catch (Exception) { /* Diagnostics must preserve the existing fallback and cancellation behavior. */ }
    }

    internal string Format(Exception error) => FormattableString.Invariant(
        $"Storage token observation: session={RuntimeFailureDiagnostics.SessionId:N} operation={Id:N} action={Safe(action)} stage={Safe(Stage)} kind={Safe(Kind)} token_present={TokenPresent} outcome={(error is OperationCanceledException ? "cancelled" : "failed")} elapsed_ms={(long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds} {RuntimeFailureDiagnostics.ExceptionFields(error)}\n");

    private static string Safe<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
}
