using System.Runtime.CompilerServices;

namespace FormatFactoryWinUI.Services;

internal enum DiagnosticStore { Unknown, Settings, Queue, History, Review }
internal enum DiagnosticPersistenceStage { Read, AcquireLock, CreateDirectory, WriteSnapshot, CommitSnapshot, Cleanup }
internal sealed record PersistenceFailureFact(DiagnosticStore Store, DiagnosticPersistenceStage Stage,
    Guid Operation, long? Bytes = null, int? Items = null, long? ElapsedMs = null);
internal static class PersistenceFailureDiagnostics
{
    private static readonly ConditionalWeakTable<Exception, PersistenceFailureFact> Facts = new();
    internal static void Record(Exception error, DiagnosticStore store, DiagnosticPersistenceStage stage,
        long? bytes = null, int? items = null, long? elapsedMs = null) => Facts.GetValue(error, _ => new(store, stage, Guid.NewGuid(), bytes, items, elapsedMs));
    internal static string? Format(Exception error)
    {
        for (var depth = 0; depth < 8 && error is not null; depth++, error = error.InnerException!)
            if (Facts.TryGetValue(error, out var fact))
                return FormattableString.Invariant($"Persistence diagnostic: session={RuntimeFailureDiagnostics.SessionId:N} operation={fact.Operation:N} store={fact.Store} stage={fact.Stage} bytes={RuntimeFailureDiagnostics.Number(fact.Bytes)} items={RuntimeFailureDiagnostics.Number(fact.Items)} elapsed_ms={RuntimeFailureDiagnostics.Number(fact.ElapsedMs)} {RuntimeFailureDiagnostics.ExceptionFields(error)}\n");
        return null;
    }
}
