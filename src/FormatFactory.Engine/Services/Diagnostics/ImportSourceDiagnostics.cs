using System.Diagnostics;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal enum ImportSourceStage { Start, FileResolve, RootResolve, RootSelection, Availability, Reservation, Probe, Complete, QueueRestore, QueueRetry }
internal enum ImportSourceOutcome { Pending, Imported, Unavailable, Ignored, Duplicate, Failed, Cancelled, Ready, Blocked }
internal enum ImportTokenOutcome { NotRequested, Restored, Fallback }

/// <summary>Immutable correlation facts only. Paths, tokens, filenames and exception text are never accepted.</summary>
internal sealed record ImportSourceSnapshot(Guid RequestOperation, Guid TaskId, bool Persisted,
    ImportSourceStage Stage, ImportSourceOutcome Outcome, long ElapsedMs,
    int InputIndex, int InputsChecked, bool? FileExists, bool? RootResolved, bool? RootApplied,
    int TokenRestores, int TokenFallbacks);

internal static class ImportSourceDiagnostics
{
    private static readonly AsyncLocal<Operation?> Current = new();

    internal static Operation Begin(Guid taskId, bool persisted, Action<string>? write = null) => new(taskId, persisted, write);

    internal static void Observe(ImportSourceStage stage, bool? fileExists = null, bool? rootResolved = null,
        bool? rootApplied = null, ImportSourceOutcome? outcome = null, bool nextInput = false)
    {
        try { Current.Value?.Observe(stage, fileExists, rootResolved, rootApplied, outcome, nextInput); }
        catch (Exception) { /* Preserve the source operation even if a diagnostic fails. */ }
    }

    internal static void TokenResolution(StorageTokenDiagnosticKind kind, bool tokenPresent,
        ImportTokenOutcome outcome, Guid tokenOperation = default,
        StorageTokenDiagnosticStage tokenStage = StorageTokenDiagnosticStage.Unknown, Exception? error = null)
    {
        try { Current.Value?.TokenResolution(kind, tokenPresent, outcome, tokenOperation, tokenStage, error); }
        catch (Exception) { /* The storage fallback and cancellation behavior remain authoritative. */ }
    }

    internal static void QueueOutcome(MediaTask task, ImportSourceSnapshot? snapshot, bool ready, bool retry)
    {
        if (snapshot is null || snapshot.TaskId != task.Id) return;
        if (ready && !retry && snapshot.TokenFallbacks == 0) return;
        var final = snapshot with { Stage = retry ? ImportSourceStage.QueueRetry : ImportSourceStage.QueueRestore,
            Outcome = ready ? ImportSourceOutcome.Ready : ImportSourceOutcome.Blocked };
        SafeDiagnostics.Write(Format(final));
    }

    internal static string Format(ImportSourceSnapshot snapshot) => FormattableString.Invariant(
        $"Import source observation: session={RuntimeFailureDiagnostics.SessionId:N} request={snapshot.RequestOperation:N} task={snapshot.TaskId:N} observed_utc={DateTimeOffset.UtcNow:O} persisted={snapshot.Persisted} stage={Safe(snapshot.Stage)} outcome={Safe(snapshot.Outcome)} elapsed_ms={Math.Max(0, snapshot.ElapsedMs)} input_index={Math.Clamp(snapshot.InputIndex, 0, 1000000)} checked={Math.Clamp(snapshot.InputsChecked, 0, 1000000)} exists={Boolean(snapshot.FileExists)} root_resolved={Boolean(snapshot.RootResolved)} root_applied={Boolean(snapshot.RootApplied)} token_restores={Math.Clamp(snapshot.TokenRestores, 0, 1000000)} token_fallbacks={Math.Clamp(snapshot.TokenFallbacks, 0, 1000000)}\n");

    private static string Boolean(bool? value) => value?.ToString() ?? "none";
    private static string Safe<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";

    internal sealed class Operation : IDisposable
    {
        private readonly Operation? _parent;
        private readonly Action<string> _write;
        private readonly long _started = Stopwatch.GetTimestamp();
        private ImportSourceSnapshot _snapshot;
        private bool _completed;
        private int _fallbackLines;

        internal Operation(Guid taskId, bool persisted, Action<string>? write)
        {
            _parent = Current.Value;
            _write = write ?? SafeDiagnostics.Write;
            _snapshot = new(Guid.NewGuid(), taskId, persisted, ImportSourceStage.Start, ImportSourceOutcome.Pending,
                0, 0, 0, null, null, null, 0, 0);
            Current.Value = this;
        }

        internal void Observe(ImportSourceStage stage, bool? fileExists, bool? rootResolved,
            bool? rootApplied, ImportSourceOutcome? outcome, bool nextInput)
        {
            _snapshot = _snapshot with { Stage = stage, ElapsedMs = Elapsed(),
                InputIndex = nextInput ? _snapshot.InputIndex + 1 : _snapshot.InputIndex,
                InputsChecked = fileExists.HasValue ? _snapshot.InputsChecked + 1 : _snapshot.InputsChecked,
                FileExists = fileExists ?? (nextInput ? null : _snapshot.FileExists),
                RootResolved = rootResolved ?? (nextInput ? null : _snapshot.RootResolved),
                RootApplied = rootApplied ?? (nextInput ? null : _snapshot.RootApplied),
                Outcome = outcome ?? _snapshot.Outcome };
            if (fileExists == false) Write(Format(_snapshot));
        }

        internal void TokenResolution(StorageTokenDiagnosticKind kind, bool tokenPresent, ImportTokenOutcome outcome,
            Guid tokenOperation, StorageTokenDiagnosticStage tokenStage, Exception? error)
        {
            _snapshot = _snapshot with { ElapsedMs = Elapsed(),
                TokenRestores = _snapshot.TokenRestores + (outcome == ImportTokenOutcome.Restored ? 1 : 0),
                TokenFallbacks = _snapshot.TokenFallbacks + (outcome == ImportTokenOutcome.Fallback ? 1 : 0) };
            if (outcome != ImportTokenOutcome.Fallback || _fallbackLines++ >= 4) return;
            Write(FormattableString.Invariant(
                $"Import token correlation: session={RuntimeFailureDiagnostics.SessionId:N} request={_snapshot.RequestOperation:N} task={_snapshot.TaskId:N} input_index={_snapshot.InputIndex} token_operation={(tokenOperation == Guid.Empty ? "none" : tokenOperation.ToString("N"))} kind={Safe(kind)} token_present={tokenPresent} outcome={Safe(outcome)} token_stage={Safe(tokenStage)} {(error is null ? "exception=none hresult=none native=none" : RuntimeFailureDiagnostics.ExceptionFields(error))}\n"));
        }

        internal ImportSourceSnapshot Complete(MediaTask? task, Exception? error, bool cancelled = false)
        {
            _completed = true;
            var outcome = cancelled || error is OperationCanceledException ? ImportSourceOutcome.Cancelled
                : _snapshot.Outcome is ImportSourceOutcome.Unavailable or ImportSourceOutcome.Duplicate ? _snapshot.Outcome
                : error is not null ? ImportSourceOutcome.Failed
                : task is not null ? ImportSourceOutcome.Imported
                : ImportSourceOutcome.Ignored;
            _snapshot = _snapshot with { Stage = error is not null ? _snapshot.Stage : ImportSourceStage.Complete,
                Outcome = outcome, ElapsedMs = Elapsed() };
            if (_snapshot.TokenFallbacks > 0 || outcome is ImportSourceOutcome.Unavailable or ImportSourceOutcome.Failed or ImportSourceOutcome.Cancelled)
                Write(Format(_snapshot));
            return _snapshot;
        }

        private long Elapsed() => (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        private void Write(string value)
        {
            try { _write(value); }
            catch (Exception) { /* Observations cannot replace an import result. */ }
        }

        public void Dispose()
        {
            if (!_completed) Complete(null, null, cancelled: true);
            Current.Value = _parent;
        }
    }
}
