using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

public sealed record QueueShutdownResult(bool CanClose, Exception? SaveError = null);

/// <summary>
/// Owns the queue's application lifecycle. Dispatch confines model mutations to the
/// host's synchronization context; persistence, import and execution remain replaceable.
/// </summary>
public sealed partial class QueueSession : IDisposable
{
    private readonly IQueuePersistence _persistence;
    private readonly Func<PersistedQueueItem, CancellationToken, Task<MediaImportItemResult>> _import;
    private readonly Func<MediaTask, IProgress<ConversionExecutionProgress>?, CancellationToken, Task<ConversionWorkflowResult>> _execute;
    private readonly Func<ConversionHistoryItem, CancellationToken, Task> _history;
    private readonly Func<Func<Task>, Task> _dispatch;
    private readonly Action<Exception>? _diagnostics;
    private readonly Action<MediaTask>? _releaseImport;
    private CancellationTokenSource _lifetime = new();
    private CancellationToken _lifetimeToken;
    private readonly HashSet<Guid> _restartPaused = [];
    private readonly HashSet<Guid> _paused = [];
    private readonly HashSet<Guid> _preserveOnExit = [];
    private readonly HashSet<Guid> _pendingRestore = [];
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _probeGates = new();
    private readonly Dictionary<Guid, StartOptions> _starts = [];
    private bool _skipCheckpoints;
    private bool _disposed;
    private volatile bool _stopping;
    private Task? _restoreTask;
    private TaskCompletionSource? _restorePublication;
    private (PersistedQueueItem Item, MediaTask Task)[]? _publishedRestoreEntries;

    public QueueSession(IQueuePersistence persistence,
        Func<PersistedQueueItem, CancellationToken, Task<MediaImportItemResult>> import,
        Func<MediaTask, IProgress<ConversionExecutionProgress>?, CancellationToken, Task<ConversionWorkflowResult>> execute,
        Func<ConversionHistoryItem, CancellationToken, Task> history,
        Func<Func<Task>, Task>? dispatch = null, Action<Exception>? diagnostics = null,
        ConversionQueueCoordinator? coordinator = null, Action<MediaTask>? releaseImport = null)
    {
        _persistence = persistence;
        _import = import;
        _execute = execute;
        _history = history;
        _dispatch = dispatch ?? (action => action());
        _diagnostics = diagnostics;
        _releaseImport = releaseImport;
        _lifetimeToken = _lifetime.Token;
        Coordinator = coordinator ?? new();
    }

    public IdIndexedCollection<MediaTask> Tasks { get; } = new(task => task.Id);
    public ConversionQueueCoordinator Coordinator { get; }
    public QueueLoadResult? LoadResult { get; private set; }
    public bool CanSave => LoadResult?.CanSave ?? true;
    public bool IsStopping => _stopping;
    public event Action<MediaTask>? TaskAdded;
    public event Action<MediaTask>? TaskStateChanged;
    public event Action<Exception>? SideEffectFailed;
    public event Action<QueueLoadResult>? RestoreCompleted;

    public Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stopping) return Task.CompletedTask;
        if (_restoreTask is not null) return _restoreTask;
        if (_publishedRestoreEntries is null)
            _restorePublication = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _restoreTask = RestoreCoreAsync(cancellationToken);
    }

    private async Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var token = cancellation.Token;
        if (_publishedRestoreEntries is { } published)
        {
            await RestorePublishedEntriesAsync(published, token).ConfigureAwait(false);
            return;
        }
        (PersistedQueueItem Item, MediaTask Task)[] restored;
        try
        {
            LoadResult = await _persistence.LoadResultAsync(token).ConfigureAwait(false);
            // Publishing loaded records is independent of new-task admission. A close
            // or save during this dispatch must preserve every already-durable record.
            restored = LoadResult.Items.Select(item => (Item: item, Task: FromPersisted(item))).ToArray();
            foreach (var (_, task) in restored.Where(entry => entry.Task.OutputGroupId is not null &&
                         entry.Task.State is not (ConversionState.Completed or ConversionState.Cancelled)))
            {
                try
                {
                    if (OutputCommitReceiptStore.Shared.TryRecover(task) is { } committedPath)
                    {
                        task.OutputPath = committedPath;
                        task.OutputFingerprint = FileFingerprintService.Read(committedPath);
                        task.State = ConversionState.Completed;
                        task.Progress = 100;
                        task.Message = LocalizationService.T("ConversionFinished");
                        task.OutputHistoryRecorded = false;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or ArgumentException or System.Text.Json.JsonException)
                { Report(exception); }
            }
            await _dispatch(() =>
            {
                foreach (var entry in restored)
                {
                    if (entry.Task.OutputGroupId is not null && entry.Task.State == ConversionState.Completed)
                    {
                        AddTaskCore(entry.Task);
                        continue;
                    }
                    entry.Task.StateBeforeBlocked = entry.Task.State == ConversionState.Blocked
                        ? entry.Task.StateBeforeBlocked ?? ConversionState.Waiting : entry.Task.State;
                    entry.Task.State = ConversionState.Blocked;
                    entry.Task.BlockedReason = LocalizationService.T("ConversionUiQueueBlocked");
                    _pendingRestore.Add(entry.Task.Id);
                    if (!AddTaskCore(entry.Task)) _pendingRestore.Remove(entry.Task.Id);
                }
                _publishedRestoreEntries = restored;
                _restorePublication!.TrySetResult();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _restorePublication!.TrySetException(exception);
            throw;
        }
        await RestorePublishedEntriesAsync(restored, token).ConfigureAwait(false);
    }

    private async Task RestorePublishedEntriesAsync((PersistedQueueItem Item, MediaTask Task)[] restored,
        CancellationToken token)
    {
        foreach (var (item, task) in restored)
        {
            token.ThrowIfCancellationRequested();
            if (task.OutputGroupId is not null && task.State == ConversionState.Completed)
            {
                await _dispatch(() =>
                {
                    if (!OutputExists(task)) task.Message = LocalizationService.T("HistoryOutputMissing", task.OutputPath);
                    else if (!MatchesOutputFingerprint(task)) task.Message = LocalizationService.T("OutputRecoveryChanged");
                    Notify(TaskStateChanged, task);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                if (!task.OutputHistoryRecorded && OutputExists(task))
                    await RecordHistorySafelyAsync(task, ConversionHistoryItem.FromTask(task)).ConfigureAwait(false);
                continue;
            }
            var gate = _probeGates.GetOrAdd(task.Id, _ => new(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var pending = false;
                await _dispatch(() =>
                {
                    pending = _pendingRestore.Contains(task.Id) && Tasks.ContainsReference(task);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                if (!pending) continue;
                MediaImportItemResult imported;
                try { imported = await _import(item, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { imported = new(item.InputPath, null, exception); }
                await _dispatch(() =>
                {
                    if (token.IsCancellationRequested || !_pendingRestore.Remove(task.Id) || !Tasks.ContainsReference(task))
                    {
                        if (imported.Task is { } stale) _releaseImport?.Invoke(stale);
                        return Task.CompletedTask;
                    }
                    if (imported.Task is { } available && IsSourceReady(available))
                    {
                        ApplyProbe(task, available);
                        if (task.State == ConversionState.Blocked)
                            task.State = task.StateBeforeBlocked ?? ConversionState.Waiting;
                        if (task.State == ConversionState.Failed && !string.IsNullOrWhiteSpace(task.LastError))
                            task.Message = task.LastError;
                        task.BlockedReason = string.Empty;
                        task.StateBeforeBlocked = null;
                    }
                    else
                    {
                        task.StateBeforeBlocked ??= task.State;
                        task.State = ConversionState.Blocked;
                        task.BlockedReason = imported.Error?.Message ?? LocalizationService.T("ConversionUiQueueBlocked");
                        if (string.IsNullOrWhiteSpace(task.LastError)) task.LastError = task.BlockedReason;
                        task.Message = task.BlockedReason;
                        if (imported.Task is { } unavailable) _releaseImport?.Invoke(unavailable);
                        SafeDiagnostics.Write(LocalizationService.T("OperationalLogTaskRestoreFailed", item.InputPath, task.BlockedReason));
                    }
                    ImportSourceDiagnostics.QueueOutcome(task, imported.SourceDiagnostic, task.State != ConversionState.Blocked, retry: false);
                    Notify(TaskStateChanged, task);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            finally { gate.Release(); }
        }
        // Future-version/I/O failures never authorize rewriting the source file.
        if (CanSave) await CheckpointSafelyAsync().ConfigureAwait(false);
        if (CanSave)
            foreach (var groupId in Tasks.Where(task => task.OutputGroupId is not null)
                         .Select(task => task.OutputGroupId!.Value).Distinct().ToArray())
                await RunSideEffectAsync(() => TryFinalizeManualOutputGroupAsync(groupId)).ConfigureAwait(false);
        await _dispatch(() => { Notify(RestoreCompleted, LoadResult!); return Task.CompletedTask; }).ConfigureAwait(false);
        _publishedRestoreEntries = null;
    }

    public bool AddTask(MediaTask task) => !_stopping && !_disposed && AddTaskCore(task);

    public IReadOnlyList<MediaTask> AddTasks(IEnumerable<MediaTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var accepted = new List<MediaTask>();
        foreach (var task in tasks)
            if (AddTask(task)) accepted.Add(task);
        return accepted;
    }

    private bool AddTaskCore(MediaTask task)
    {
        if (Tasks.FindById(task.Id) is not null) return false;
        SourceRelocationService.CaptureIdentity(task);
        Tasks.Add(task);
        Notify(TaskAdded, task);
        return true;
    }

    public void RemoveTask(MediaTask task)
    {
        if (task.IsRecoveringOutput) return;
        if (Coordinator.IsActive(task.Id)) throw new InvalidOperationException("Stop the task before removing it.");
        Tasks.Remove(task);
        _pendingRestore.Remove(task.Id);
        _starts.Remove(task.Id);
        _paused.Remove(task.Id);
        _restartPaused.Remove(task.Id);
        if (task.OutputGroupId is { } groupId && !task.Options.ContainsKey("automation.ruleSnapshot"))
        {
            var remaining = GetOutputGroup(groupId);
            foreach (var sibling in remaining) sibling.OutputExpectedCount = remaining.Length;
            _ = RunSideEffectAsync(() => TryFinalizeManualOutputGroupAsync(groupId));
        }
    }

    public bool TryStart(MediaTask task, IProgress<ConversionExecutionProgress>? progress = null,
        Func<ConversionQueueOperationCompleted, Task>? completed = null, Func<Task>? batchCompleted = null)
    {
        if (_stopping || _disposed || Coordinator.IsActive(task.Id) || task.IsRecoveringOutput || IsRemovalUndoPending(task.Id)) return false;
        _paused.Remove(task.Id);
        _restartPaused.Remove(task.Id);
        AddTask(task);
        _pendingRestore.Remove(task.Id);
        var options = new StartOptions(progress, completed, batchCompleted);
        _starts[task.Id] = options;
        ConversionHistoryItem? history = null;
        ErrorCorrelationFacts? sourceFailure = null;
        return Coordinator.TryStartPlanned(task.Id,
            async token =>
            {
                ConversionOutcome outcome;
                try { outcome = await ExecuteAsync(task, progress, token, facts => sourceFailure = facts).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await SetCancelledAsync(task).ConfigureAwait(false);
                    return ConversionOutcome.Cancelled;
                }
                await _dispatch(() =>
                {
                    if (outcome == ConversionOutcome.Completed) history = ConversionHistoryItem.FromTask(task);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                return outcome;
            },
            () => SetCancelledAsync(task),
            async completion =>
            {
                if (completion.Error is { } exception)
                    await _dispatch(() =>
                    {
                        if (task.State != ConversionState.Blocked)
                        {
                            task.State = ConversionState.Failed;
                            task.Message = task.LastError = exception.Message;
                        }
                        Notify(TaskStateChanged, task);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                // Conversion admission is released before durable side effects. Their
                // failure cannot change the execution outcome or strand queue capacity.
                await CheckpointSafelyAsync().ConfigureAwait(false);
                if (history is not null)
                    await RecordHistorySafelyAsync(task, history).ConfigureAwait(false);
                if (completed is not null)
                    await RunSideEffectAsync(() => completed(completion with { ErrorCorrelation = sourceFailure })).ConfigureAwait(false);
                await _dispatch(() =>
                {
                    if (!_stopping && _restartPaused.Remove(task.Id))
                    {
                        _paused.Remove(task.Id);
                        TryStart(task, options.Progress, options.Completed, options.BatchCompleted);
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            },
            () => batchCompleted is null || _stopping ? Task.CompletedTask : RunSideEffectAsync(batchCompleted),
            WorkloadEstimate.FromTask(task));
    }

    private async Task<ConversionOutcome> ExecuteAsync(MediaTask task,
        IProgress<ConversionExecutionProgress>? progress, CancellationToken token, Action<ErrorCorrelationFacts> sourceFailure)
    {
        var blocked = false;
        await _dispatch(() =>
        {
            blocked = task.State == ConversionState.Blocked;
            task.LastAttemptAtUtc = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        if (blocked)
        {
            var gate = _probeGates.GetOrAdd(task.Id, _ => new(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                PersistedQueueItem? persisted = null;
                await _dispatch(() =>
                {
                    _releaseImport?.Invoke(task);
                    persisted = QueueStore.ToPersistedItem(task);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                MediaImportItemResult imported;
                try { imported = await _import(persisted!, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { await SetCancelledAsync(task).ConfigureAwait(false); return ConversionOutcome.Cancelled; }
                catch (Exception exception)
                {
                    Report(exception, notifySubscribers: false);
                    imported = new(task.InputPath, null, exception);
                }
                if (token.IsCancellationRequested)
                {
                    if (imported.Task is { } stale) _releaseImport?.Invoke(stale);
                    token.ThrowIfCancellationRequested();
                }
                if (imported.Task is null || !IsSourceReady(imported.Task))
                {
                    if (imported.SourceDiagnostic is { } diagnostic && diagnostic.TaskId == task.Id)
                        sourceFailure(new(ErrorCorrelationKind.SourceImport, diagnostic.RequestOperation, task.Id));
                    await _dispatch(() =>
                    {
                        task.BlockedReason = imported.Error?.Message ?? LocalizationService.T("ConversionUiQueueBlocked");
                        task.LastError = task.Message = task.BlockedReason;
                        if (imported.Task is { } unavailable) _releaseImport?.Invoke(unavailable);
                        ImportSourceDiagnostics.QueueOutcome(task, imported.SourceDiagnostic, ready: false, retry: true);
                        Notify(TaskStateChanged, task);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                    return ConversionOutcome.Failed;
                }
                await _dispatch(() =>
                {
                    ApplyProbe(task, imported.Task);
                    ImportSourceDiagnostics.QueueOutcome(task, imported.SourceDiagnostic, ready: true, retry: true);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        await _dispatch(() =>
        {
            token.ThrowIfCancellationRequested();
            task.State = ConversionState.Running;
            task.StateBeforeBlocked = null;
            task.BlockedReason = task.LastError = string.Empty;
            task.Progress = 0;
            task.OutputPath = string.Empty;
            task.ResultStatistics = null;
            task.Message = LocalizationService.T("Starting");
            Notify(TaskStateChanged, task);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        try
        {
            var result = await _execute(task, progress, token).ConfigureAwait(false);
            await _dispatch(() =>
            {
                if (result.Outcome == ConversionOutcome.Cancelled) SetCancelled(task);
                else
                {
                    task.State = result.Outcome == ConversionOutcome.Completed ? ConversionState.Completed : ConversionState.Failed;
                    task.Message = result.Execution.Message;
                    task.LastError = result.Outcome == ConversionOutcome.Failed ? result.Execution.Message : string.Empty;
                    task.RecoverableOutputId = result.Execution.RecoverableOutputId;
                    task.PreservedOutputPath = result.Execution.PreservedOutputPath;
                    if (result.Outcome == ConversionOutcome.Completed)
                    {
                        task.Progress = 100;
                        task.OutputPath = result.OutputPath;
                        task.ResultStatistics = result.Statistics;
                        task.OutputFingerprint = FileFingerprintService.Read(result.OutputPath);
                        task.OutputHistoryRecorded = false;
                        task.EngineId = result.EngineId;
                    }
                }
                Notify(TaskStateChanged, task);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            return result.Outcome;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await SetCancelledAsync(task).ConfigureAwait(false);
            return ConversionOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            // The completed operation reports the final conversion error with its task context.
            Report(exception, notifySubscribers: false);
            await _dispatch(() =>
            {
                task.State = ConversionState.Failed;
                task.Message = task.LastError = exception.Message;
                Notify(TaskStateChanged, task);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            return ConversionOutcome.Failed;
        }
    }

    public async Task CompleteRecoveredOutputAsync(MediaTask task, string outputPath, OutputRecoveryRecord recovery)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.TaskId != task.Id || !(File.Exists(outputPath) || Directory.Exists(outputPath)))
            throw new InvalidDataException(LocalizationService.T("OutputRecoveryMissing"));
        var savedPath = Path.GetFullPath(outputPath);
        if (string.Equals(savedPath, Path.GetFullPath(recovery.ArtifactPath), StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(task.PreservedOutputPath) &&
            string.Equals(savedPath, Path.GetFullPath(task.PreservedOutputPath), StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(task.RecoverableOutputId) && task.RecoverableOutputId != recovery.Id)
            throw new InvalidDataException(LocalizationService.T("OutputRecoveryMissing"));
        ConversionHistoryItem? history = null;
        await _dispatch(() =>
        {
            if (!Tasks.ContainsReference(task) || Coordinator.IsActive(task.Id) || task.State is not (ConversionState.Failed or ConversionState.Blocked))
                throw new InvalidOperationException(LocalizationService.T("OutputRecoveryMissing"));
            if (!string.IsNullOrWhiteSpace(recovery.TargetFormatId)) task.TargetFormatId = recovery.TargetFormatId;
            if (!string.IsNullOrWhiteSpace(recovery.ConversionMode)) task.ConversionMode = recovery.ConversionMode;
            if (!string.IsNullOrWhiteSpace(recovery.EngineId)) task.EngineId = recovery.EngineId;
            task.OutputPath = outputPath;
            task.ResultStatistics = null;
            task.OutputFingerprint = FileFingerprintService.Read(outputPath);
            task.OutputHistoryRecorded = false;
            task.Progress = 100;
            task.State = ConversionState.Completed;
            task.StateBeforeBlocked = null;
            task.LastError = task.BlockedReason = string.Empty;
            task.RecoverableOutputId = task.PreservedOutputPath = null;
            task.Message = LocalizationService.T("ConversionFinished");
            history = ConversionHistoryItem.FromTask(task);
            Notify(TaskStateChanged, task);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await CheckpointSafelyAsync().ConfigureAwait(false);
        await RecordHistorySafelyAsync(task, history!).ConfigureAwait(false);
    }

    public MediaTask[] GetOutputGroup(Guid groupId) => Tasks.Where(task => task.OutputGroupId == groupId)
        .OrderBy(task => task.OutputOrder).ToArray();

    /// <summary>The caller must complete automation finalization before releasing an automatic group.</summary>
    public async Task<bool> FinalizeOutputGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        if (HasPendingRemovalUndo(groupId)) return false;
        var group = GetOutputGroup(groupId);
        if (group.Length == 0 || group.Any(task => task.OutputExpectedCount != group.Length ||
                task.State != ConversionState.Completed || !task.OutputHistoryRecorded || !OutputExists(task) ||
                !MatchesOutputFingerprint(task) || Coordinator.IsActive(task.Id))) return false;
        var finalized = false;
        await _dispatch(() =>
        {
            if (HasPendingRemovalUndo(groupId) || group.Any(task => !Tasks.ContainsReference(task))) return Task.CompletedTask;
            foreach (var task in group) task.OutputGroupFinalized = true;
            finalized = true;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        if (!finalized) return false;
        try { await SaveAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            await _dispatch(() =>
            {
                foreach (var task in group) task.OutputGroupFinalized = false;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            throw;
        }
        return true;
    }

    public async Task<bool> TryFinalizeManualOutputGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        if (HasPendingRemovalUndo(groupId)) return false;
        var group = GetOutputGroup(groupId);
        if (group.Length == 0 || group.Any(task => task.Options.ContainsKey("automation.ruleSnapshot"))) return false;
        if (!await EnsureOutputGroupDurableAsync(groupId, cancellationToken).ConfigureAwait(false)) return false;
        return await FinalizeOutputGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Repairs failed history side effects and checkpoints a complete group before source archiving.</summary>
    public async Task<bool> EnsureOutputGroupDurableAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasPendingRemovalUndo(groupId)) return false;
        var group = GetOutputGroup(groupId);
        if (group.Length == 0 || group.Any(task => task.OutputExpectedCount != group.Length ||
                task.State != ConversionState.Completed || !OutputExists(task) || !MatchesOutputFingerprint(task))) return false;
        foreach (var task in group.Where(task => !task.OutputHistoryRecorded))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecordHistorySafelyAsync(task, ConversionHistoryItem.FromTask(task)).ConfigureAwait(false);
        }
        if (HasPendingRemovalUndo(groupId) || group.Any(task => !task.OutputHistoryRecorded)) return false;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RecordHistorySafelyAsync(MediaTask task, ConversionHistoryItem history)
    {
        await RunSideEffectAsync(async () =>
        {
            await _history(history, CancellationToken.None).ConfigureAwait(false);
            await _dispatch(() => { task.OutputHistoryRecorded = true; return Task.CompletedTask; }).ConfigureAwait(false);
            if (task.OutputGroupId is not null) await SaveAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static bool OutputExists(MediaTask task) =>
        !string.IsNullOrWhiteSpace(task.OutputPath) && (File.Exists(task.OutputPath) || Directory.Exists(task.OutputPath));
    private static bool MatchesOutputFingerprint(MediaTask task) => task.OutputFingerprint is null ||
        FileFingerprintService.Read(task.OutputPath) == task.OutputFingerprint;

    public MediaTask[] GetRetryableTasks()
    {
        if (_stopping || _disposed) return [];
        var retryable = QueueActionPolicy.SelectRetryable(Tasks, IsBusy).ToHashSet();
        return Tasks.Where(task => retryable.Contains(task.Id) && !task.IsRecoveringOutput).ToArray();
    }

    public MediaTask[] GetEditableTasks(IEnumerable<Guid> taskIds)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        if (_stopping || _disposed) return [];
        var selected = taskIds.ToHashSet();
        return Tasks.Where(task => selected.Contains(task.Id) && !task.IsRecoveringOutput && QueueActionPolicy.CanBatchEdit(task, IsBusy)).ToArray();
    }

    /// <summary>Applies a common target and preset only to idle, compatible selected tasks.</summary>
    public int ApplyBatchOptions(IEnumerable<Guid> taskIds, string? targetFormat = null, string? conversionMode = null)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        if (targetFormat is null && conversionMode is null) return 0;
        if (conversionMode is not null && !FormatCatalog.ConversionModes.Contains(conversionMode, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown conversion preset.", nameof(conversionMode));
        var changed = 0;
        foreach (var task in GetEditableTasks(taskIds))
        {
            var target = targetFormat is null ? null : task.AvailableFormats.FirstOrDefault(format =>
                string.Equals(format, targetFormat, StringComparison.OrdinalIgnoreCase));
            if (targetFormat is not null && target is null) continue;
            if ((target is null || string.Equals(task.TargetFormat, target, StringComparison.OrdinalIgnoreCase)) &&
                (conversionMode is null || string.Equals(task.ConversionMode, conversionMode, StringComparison.OrdinalIgnoreCase))) continue;
            if (target is not null) task.TargetFormat = target;
            if (conversionMode is not null) task.ConversionMode = conversionMode;
            changed++;
        }
        if (changed > 0) _ = CheckpointSafelyAsync();
        return changed;
    }

    /// <summary>Reorders waiting rows and their admission order, keeping running rows in place.</summary>
    public int ReorderPendingTasks(QueueSortOrder order)
    {
        if (_stopping || _disposed) return 0;
        var before = Tasks.ToArray();
        var pending = before.Where(IsPendingForOrdering).ToArray();
        var ordered = order switch
        {
            QueueSortOrder.FileName => pending.OrderBy(task => task.FileName, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            QueueSortOrder.SmallestFirst => pending.OrderBy(task => task.FileSize).ToArray(),
            QueueSortOrder.LargestFirst => pending.OrderByDescending(task => task.FileSize).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
        return ApplyPendingOrder(before, pending, ordered);
    }

    private bool IsBusy(Guid taskId) => Coordinator.IsActive(taskId) || _pendingRestore.Contains(taskId) || IsRemovalUndoPending(taskId);
    public bool Pause(MediaTask task, Func<bool>? suspendNative = null)
    {
        if (task.State != ConversionState.Running || !Coordinator.IsActive(task.Id)) return false;
        _paused.Add(task.Id);
        if (suspendNative?.Invoke() != true) Coordinator.Cancel(task.Id);
        task.State = ConversionState.Paused;
        task.Message = LocalizationService.T("PausedMessage");
        Notify(TaskStateChanged, task);
        _ = CheckpointSafelyAsync();
        return true;
    }

    public bool Resume(MediaTask task, Func<bool>? resumeNative = null)
    {
        if (_stopping || task.State != ConversionState.Paused) return false;
        if (resumeNative?.Invoke() == true)
        {
            _paused.Remove(task.Id);
            task.State = ConversionState.Running;
            task.Message = LocalizationService.T("ContinueMessage");
            Notify(TaskStateChanged, task);
            return true;
        }
        if (Coordinator.IsActive(task.Id)) { _restartPaused.Add(task.Id); return true; }
        _paused.Remove(task.Id);
        return _starts.TryGetValue(task.Id, out var options)
            ? TryStart(task, options.Progress, options.Completed, options.BatchCompleted)
            : TryStart(task);
    }

    public void Cancel(MediaTask task)
    {
        if (task.IsRecoveringOutput) return;
        _pendingRestore.Remove(task.Id);
        _paused.Remove(task.Id);
        _restartPaused.Remove(task.Id);
        if (!Coordinator.Cancel(task.Id))
        {
            SetCancelled(task);
            Notify(TaskStateChanged, task);
            _ = CheckpointSafelyAsync();
        }
    }

    private Task SetCancelledAsync(MediaTask task) => _dispatch(() =>
    {
        SetCancelled(task);
        Notify(TaskStateChanged, task);
        return Task.CompletedTask;
    });

    private void SetCancelled(MediaTask task)
    {
        task.StateBeforeBlocked = null;
        task.BlockedReason = string.Empty;
        task.State = _preserveOnExit.Contains(task.Id) ? ConversionState.Waiting
            : _paused.Contains(task.Id) ? ConversionState.Paused : ConversionState.Cancelled;
        task.Message = LocalizationService.T(task.State == ConversionState.Paused ? "PausedMessage"
            : task.State == ConversionState.Waiting ? "WaitingStart" : "Cancelled");
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (_restorePublication is { } publication)
            await publication.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        (Guid Id, string Path)[] committed = [];
        await _dispatch(() =>
        {
            if (!CanSave) throw new InvalidOperationException(LocalizationService.T("ConversionUiQueueSaveUnavailable"));
            committed = Tasks.Where(task => task.OutputGroupId is not null && task.State == ConversionState.Completed)
                .Select(task => (task.Id, task.OutputPath)).ToArray();
            return _persistence.SaveAsync(Tasks, cancellationToken);
        }).ConfigureAwait(false);
        foreach (var (id, path) in committed) OutputCommitReceiptStore.Shared.Forget(id, path);
    }

    public async Task<QueueShutdownResult> ShutdownAsync(bool save = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stopping = true;
        _skipCheckpoints |= !save;
        await _dispatch(() =>
        {
            DiscardUndo(finalizeGroups: false);
            foreach (var task in Tasks.Where(task => Coordinator.IsActive(task.Id)))
                if (task.State != ConversionState.Paused) _preserveOnExit.Add(task.Id);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        _lifetime.Cancel();
        var stoppedOperations = Coordinator.StopAndWaitAsync();
        if (_restoreTask is not null)
        {
            try { await _restoreTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Report(exception); }
        }
        await stoppedOperations.ConfigureAwait(false);
        if (!save) return new(true);
        try { await SaveAsync().ConfigureAwait(false); return new(true); }
        catch (Exception exception) { Report(exception); return new(false, exception); }
    }

    private Task CheckpointSafelyAsync() => _skipCheckpoints ? Task.CompletedTask : RunSideEffectAsync(() => SaveAsync());

    public void ContinueAfterFailedShutdown()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Coordinator.ResumeAdmission();
        _lifetime.Dispose();
        _lifetime = new();
        _lifetimeToken = _lifetime.Token;
        _stopping = _skipCheckpoints = false;
        _preserveOnExit.Clear();
        if (_restorePublication is { Task.IsCompletedSuccessfully: false })
        {
            // A cancelled/failed load never published a trustworthy snapshot. Reload
            // before allowing any later save to replace the durable source records.
            _restoreTask = null;
            _restorePublication = null;
            _ = RunSideEffectAsync(() => RestoreAsync());
        }
        else if (_publishedRestoreEntries is not null && _restoreTask is { IsCompletedSuccessfully: false })
        {
            // The durable rows already belong to this session. Resume their remaining
            // probes with the new lifetime, preserving IDs, ordering and model references.
            _restoreTask = null;
            _ = RunSideEffectAsync(() => RestoreAsync());
        }
    }
    private async Task RunSideEffectAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception exception)
        {
            Report(exception);
            try { await _dispatch(() => { Notify(SideEffectFailed, exception); return Task.CompletedTask; }).ConfigureAwait(false); }
            catch (Exception dispatchError) { Report(dispatchError); }
        }
    }
    private void Report(Exception exception, bool notifySubscribers = true) =>
        SafeDiagnostics.Report(exception, _diagnostics, notifySubscribers);
    private void Notify<T>(Action<T>? handler, T value)
    {
        if (_disposed || handler is null) return;
        foreach (Action<T> subscriber in handler.GetInvocationList())
            try { subscriber(value); } catch (Exception exception) { Report(exception); }
    }

    internal static MediaTask FromPersisted(PersistedQueueItem item)
    {
        var task = new MediaTask
        {
            Id = item.TaskId, InputPath = item.InputPath, InputAccessToken = item.InputAccessToken,
            OutputGroupId = item.OutputGroupId, OutputConfigurationId = item.OutputConfigurationId,
            OutputLabel = item.OutputLabel, OutputOrder = item.OutputOrder, OutputExpectedCount = item.OutputExpectedCount,
            OutputGroupFinalized = item.OutputGroupFinalized, OutputHistoryRecorded = item.OutputHistoryRecorded,
            OutputFingerprint = item.OutputFingerprint, OutputPath = item.OutputPath, ResultStatistics = item.ResultStatistics,
            SourceRoot = item.SourceRoot, SourceRootAccessToken = item.SourceRootAccessToken,
            ContentKind = item.ContentKind ?? FormatCatalog.FindByExtension(item.InputPath)?.ContentKind ?? ContentKind.Video,
            SourceFormatId = item.SourceFormatId, TargetFormat = item.TargetFormat, TargetFormatId = item.TargetFormatId,
            EngineId = item.EngineId, ConversionMode = item.ConversionMode, ParametersVersion = item.ParametersVersion,
            SelectedAudioTrack = item.SelectedAudioTrack,
            State = item.State is ConversionState.Running or ConversionState.Probing ? ConversionState.Waiting : item.State,
            Message = item.Message, LastError = item.LastError, BlockedReason = item.BlockedReason,
            RecoverableOutputId = item.RecoverableOutputId, PreservedOutputPath = item.PreservedOutputPath,
            LastAttemptAtUtc = item.LastAttemptAtUtc, StateBeforeBlocked = item.StateBeforeBlocked
        };
        foreach (var input in item.Inputs) task.Inputs.Add(new(input.Path, input.FormatId, AccessToken: input.AccessToken));
        foreach (var option in item.Options.Where(option => !option.Key.Equals("password", StringComparison.OrdinalIgnoreCase)))
            task.Options[option.Key] = option.Value;
        foreach (var target in FormatCatalog.TargetsFor(task.ContentKind, task.SourceFormatId)) task.AvailableFormats.Add(target);
        if (!string.IsNullOrWhiteSpace(task.TargetFormat) && !task.AvailableFormats.Contains(task.TargetFormat))
            task.AvailableFormats.Add(task.TargetFormat);
        SourceRelocationService.CaptureIdentity(task);
        return task;
    }

    private static void ApplyProbe(MediaTask task, MediaTask imported)
    {
        task.ContentProbe = imported.ContentProbe;
        task.FileSize = imported.FileSize;
        task.InputPath = imported.InputPath;
        task.InputAccessToken = imported.InputAccessToken;
        task.SourceRoot = imported.SourceRoot;
        task.SourceRootAccessToken = imported.SourceRootAccessToken;
        task.Inputs.Clear();
        foreach (var input in imported.Inputs) task.Inputs.Add(input);
        task.AudioTracks.Clear();
        foreach (var audio in imported.AudioTracks) task.AudioTracks.Add(audio);
        // Older queue snapshots may have captured an empty selector value.
        // The importer has already chosen a supported target for the probed input.
        if (string.IsNullOrWhiteSpace(task.TargetFormatId) && !string.IsNullOrWhiteSpace(imported.TargetFormatId))
            task.TargetFormatId = imported.TargetFormatId;
        task.AvailableFormats.Clear();
        foreach (var target in imported.AvailableFormats) task.AvailableFormats.Add(target);
        if (!string.IsNullOrWhiteSpace(task.TargetFormat) && !task.AvailableFormats.Contains(task.TargetFormat))
            task.AvailableFormats.Add(task.TargetFormat);
    }

    private static bool IsSourceReady(MediaTask task) =>
        !(task.ContentKind == ContentKind.Disc && task.ContentProbe.Metadata.TryGetValue("isReady", out var ready)
          && ready.Equals("false", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_disposed) return;
        DiscardUndo(finalizeGroups: false);
        _disposed = _stopping = true;
        _lifetime.Cancel();
        Coordinator.Dispose();
        _lifetime.Dispose();
    }
    private sealed record StartOptions(IProgress<ConversionExecutionProgress>? Progress,
        Func<ConversionQueueOperationCompleted, Task>? Completed, Func<Task>? BatchCompleted);
}
