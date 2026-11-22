using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

public enum QueueUndoKind { RemoveTasks, BatchOptions }
public sealed record QueueUndoInfo(QueueUndoKind Kind, int TaskCount, DateTimeOffset ExpiresAtUtc);

public sealed partial class QueueSession
{
    private static readonly TimeSpan UndoLifetime = TimeSpan.FromSeconds(30);
    private QueueUndoEntry? _undo;
    private readonly HashSet<Guid> _pendingUndoRemovals = [];

    public QueueUndoInfo? UndoState => _undo?.Info;
    public event Action<QueueUndoInfo?>? UndoChanged;
    public bool IsRemovalUndoPending(Guid taskId) => _pendingUndoRemovals.Contains(taskId);

    /// <summary>
    /// Detaches waiting admissions only if the coordinator can atomically withdraw
    /// them before execution starts. The original state is restored after their
    /// cancellation callbacks finish, so undo returns a waiting row, not a cancelled one.
    /// </summary>
    public async Task<MediaTask[]> RemoveTasksWithUndoAsync(IEnumerable<Guid> taskIds)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        if (_disposed || _stopping || _pendingUndoRemovals.Count > 0) return [];
        var selected = taskIds.ToHashSet();
        var candidates = Tasks.Where(task => selected.Contains(task.Id) && CanDetachWithUndo(task)).ToArray();
        var admitted = candidates.Where(task => Coordinator.IsActive(task.Id)).ToDictionary(task => task.Id,
            task => new RemovalState(task.State, task.StateBeforeBlocked, task.BlockedReason, task.Message));
        foreach (var task in candidates) _pendingUndoRemovals.Add(task.Id);
        try
        {
            var withdrawals = await Task.WhenAll(admitted.Select(async entry =>
                (entry.Key, Removed: await Coordinator.TryCancelWaitingAsync(entry.Key).ConfigureAwait(false)))).ConfigureAwait(false);
            MediaTask[] removed = [];
            await _dispatch(() =>
            {
                // A close owns the queue after stopping starts; do not undo its
                // cancellation/persistence choices or create a new undo offer.
                if (_disposed || _stopping) return Task.CompletedTask;
                foreach (var (id, withdrawn) in withdrawals)
                    if (withdrawn && candidates.First(task => task.Id == id) is { } task && Tasks.ContainsReference(task))
                        admitted[id].Restore(task);
                var withdrawnIds = withdrawals.Where(entry => entry.Removed).Select(entry => entry.Key).ToHashSet();
                var eligible = candidates.Where(task => !admitted.ContainsKey(task.Id) || withdrawnIds.Contains(task.Id))
                    .Select(task => task.Id).ToArray();
                foreach (var task in candidates) _pendingUndoRemovals.Remove(task.Id);
                removed = RemoveTasksWithUndo(eligible);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            await _dispatch(() =>
            {
                foreach (var task in candidates) _pendingUndoRemovals.Remove(task.Id);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Temporarily removes idle manual rows, retaining their original models and
    /// import reservations. Automation receipts and in-flight work are never undone.
    /// The caller must not release imports for the returned rows.
    /// </summary>
    public MediaTask[] RemoveTasksWithUndo(IEnumerable<Guid> taskIds)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        if (_disposed || _stopping) return [];
        var selected = taskIds.ToHashSet();
        var removed = Tasks.Select((task, index) => new RemovedQueueTask(task, index))
            .Where(entry => selected.Contains(entry.Task.Id) && CanRemoveWithUndo(entry.Task)).ToArray();
        if (removed.Length == 0) return [];

        // Register the hold before removing a row: group finalization may be
        // requested by collection observers or by another member's completion.
        var previous = _undo;
        _undo = new(new(QueueUndoKind.RemoveTasks, removed.Length, DateTimeOffset.UtcNow + UndoLifetime), removed, []);
        foreach (var entry in removed)
        {
            Tasks.Remove(entry.Task);
            _pendingRestore.Remove(entry.Task.Id);
            _starts.Remove(entry.Task.Id);
            _paused.Remove(entry.Task.Id);
            _restartPaused.Remove(entry.Task.Id);
        }
        if (previous is not null) CommitUndo(previous, finalizeGroups: true);
        Notify(UndoChanged, _undo.Info);
        _ = CheckpointSafelyAsync();
        return removed.Select(entry => entry.Task).ToArray();
    }

    private bool CanDetachWithUndo(MediaTask task) => !_pendingRestore.Contains(task.Id) && !IsRemovalUndoPending(task.Id) &&
        !task.IsRecoveringOutput &&
        task.State is not (ConversionState.Running or ConversionState.Paused or ConversionState.Probing) &&
        !task.Options.ContainsKey("automation.ruleSnapshot");

    private bool CanRemoveWithUndo(MediaTask task) => !IsBusy(task.Id) && CanDetachWithUndo(task);

    public bool HasPendingRemovalUndo(Guid groupId) =>
        _undo?.Removed.Any(entry => entry.Task.OutputGroupId == groupId) == true;

    public bool HasRemovalUndoForTask(Guid taskId) =>
        _undo?.Removed.Any(entry => entry.Task.Id == taskId) == true;

    /// <summary>
    /// Commits held rows belonging to groups that the caller is about to replace.
    /// Unrelated rows retain their original undo expiry and relative positions.
    /// </summary>
    public int CommitRemovalUndoForGroups(IEnumerable<Guid> groupIds)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        if (_disposed || _stopping || _undo is not { } undo) return 0;
        var groups = groupIds.ToHashSet();
        var committed = undo.Removed.Where(entry => entry.Task.OutputGroupId is { } id && groups.Contains(id)).ToArray();
        if (committed.Length == 0) return 0;
        var retained = undo.Removed.Except(committed)
            .Select(entry => entry with { Index = entry.Index - committed.Count(prior => prior.Index < entry.Index) })
            .ToArray();
        _undo = retained.Length == 0 ? null : new(undo.Info with { TaskCount = retained.Length }, retained, []);
        // The caller publishes its group mutation next; do not finalize the old
        // group in the middle of that publication.
        CommitUndo(new(undo.Info with { TaskCount = committed.Length }, committed, []), finalizeGroups: false);
        Notify(UndoChanged, _undo?.Info);
        _ = CheckpointSafelyAsync();
        return committed.Length;
    }

    /// <summary>Captures the exact route before changing compatible idle rows.</summary>
    public int ApplyBatchOptionsWithUndo(IEnumerable<Guid> taskIds, string? targetFormat = null, string? conversionMode = null)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        if (_disposed || _stopping) return 0;
        var editable = GetEditableTasks(taskIds);
        var before = editable.ToDictionary(task => task.Id, ParameterSnapshot.Capture);
        var changed = ApplyBatchOptions(editable.Select(task => task.Id), targetFormat, conversionMode);
        if (changed == 0) return 0;
        var edits = editable.Select(task => new EditedQueueTask(task, before[task.Id], ParameterSnapshot.Capture(task)))
            .Where(entry => !entry.Before.Matches(entry.Task)).ToArray();
        SetOptionsUndo(edits);
        return changed;
    }

    /// <summary>Applies an already-validated parameter edit and retains every parameter for undo.</summary>
    public int ApplyTaskOptionsWithUndo(IEnumerable<Guid> taskIds, Action<MediaTask> apply)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        ArgumentNullException.ThrowIfNull(apply);
        if (_disposed || _stopping) return 0;
        var editable = GetEditableTasks(taskIds);
        var before = editable.ToDictionary(task => task.Id, ParameterSnapshot.Capture);
        try
        {
            foreach (var task in editable) apply(task);
        }
        catch
        {
            foreach (var task in editable) before[task.Id].ApplyParameters(task);
            throw;
        }
        var edits = editable.Select(task => new EditedQueueTask(task, before[task.Id], ParameterSnapshot.Capture(task)))
            .Where(entry => !entry.Before.Matches(entry.Task)).ToArray();
        if (edits.Length == 0) return 0;
        SetOptionsUndo(edits);
        foreach (var entry in edits) Notify(TaskStateChanged, entry.Task);
        _ = CheckpointSafelyAsync();
        return edits.Length;
    }

    private void SetOptionsUndo(EditedQueueTask[] edits)
    {
        DiscardUndo();
        _undo = new(new(QueueUndoKind.BatchOptions, edits.Length, DateTimeOffset.UtcNow + UndoLifetime), [], edits);
        Notify(UndoChanged, _undo.Info);
    }

    /// <summary>
    /// Restores only the most recent operation. A later edit, source relocation,
    /// or execution makes that individual parameter edit ineligible for undo.
    /// </summary>
    public int UndoLastAction()
    {
        if (_disposed || _stopping) return 0;
        ExpireUndo();
        if (_undo is not { } undo) return 0;
        var restored = 0;
        // Keep group holds until every removed member has returned.
        foreach (var entry in undo.Removed.OrderBy(entry => entry.Index))
        {
            if ((Tasks.FindById(entry.Task.Id) is not null)) continue;
            Tasks.Insert(Math.Min(entry.Index, Tasks.Count), entry.Task);
            Notify(TaskAdded, entry.Task);
            restored++;
        }
        foreach (var entry in undo.Edited)
        {
            if (!Tasks.ContainsReference(entry.Task) || !QueueActionPolicy.CanBatchEdit(entry.Task, IsBusy) ||
                entry.Task.IsRecoveringOutput || !entry.After.Matches(entry.Task)) continue;
            entry.Before.ApplyParameters(entry.Task);
            Notify(TaskStateChanged, entry.Task);
            restored++;
        }
        _undo = null;
        // A same-ID replacement must not lose the reservation it now owns.
        foreach (var entry in undo.Removed.Where(entry => !(Tasks.FindById(entry.Task.Id) is not null)))
            ReleaseUndoImport(entry.Task);
        Notify(UndoChanged, (QueueUndoInfo?)null);
        // A sibling may have completed while this removal held finalization.
        // Once every removed row is restored, revisit those completed groups.
        foreach (var groupId in undo.Removed.Select(entry => entry.Task.OutputGroupId).OfType<Guid>().Distinct())
            _ = RunSideEffectAsync(() => TryFinalizeManualOutputGroupAsync(groupId));
        _ = CheckpointSafelyAsync();
        return restored;
    }

    public void ExpireUndo(DateTimeOffset? now = null)
    {
        if (_undo is { } undo && (now ?? DateTimeOffset.UtcNow) >= undo.Info.ExpiresAtUtc) DiscardUndo();
    }

    /// <summary>
    /// Commits a pending removal and releases retained import ownership. Close and
    /// disposal use finalizeGroups: false to avoid launching new side effects.
    /// </summary>
    public void DiscardUndo(bool finalizeGroups = true)
    {
        if (_undo is not { } undo) return;
        _undo = null;
        CommitUndo(undo, finalizeGroups);
        Notify(UndoChanged, (QueueUndoInfo?)null);
        if (finalizeGroups && !_stopping && !_disposed) _ = CheckpointSafelyAsync();
    }

    private void CommitUndo(QueueUndoEntry undo, bool finalizeGroups)
    {
        foreach (var entry in undo.Removed)
            if (!(Tasks.FindById(entry.Task.Id) is not null)) ReleaseUndoImport(entry.Task);
        foreach (var groupId in undo.Removed.Select(entry => entry.Task.OutputGroupId).OfType<Guid>().Distinct())
        {
            var remaining = GetOutputGroup(groupId);
            var retained = _undo?.Removed.Where(entry => entry.Task.OutputGroupId == groupId).ToArray() ?? [];
            var expectedCount = remaining.Length + retained.Count(entry => !Tasks.ContainsReference(entry.Task));
            foreach (var sibling in remaining.Concat(retained.Select(entry => entry.Task))) sibling.OutputExpectedCount = expectedCount;
            if (finalizeGroups && !_stopping && !_disposed)
                _ = RunSideEffectAsync(() => TryFinalizeManualOutputGroupAsync(groupId));
        }
    }

    private void ReleaseUndoImport(MediaTask task)
    {
        try { _releaseImport?.Invoke(task); }
        catch (Exception exception) { Report(exception); }
    }

    private sealed record QueueUndoEntry(QueueUndoInfo Info, RemovedQueueTask[] Removed, EditedQueueTask[] Edited);
    private sealed record RemovedQueueTask(MediaTask Task, int Index);
    private sealed record EditedQueueTask(MediaTask Task, ParameterSnapshot Before, ParameterSnapshot After);
    private sealed record RemovalState(ConversionState State, ConversionState? StateBeforeBlocked, string BlockedReason, string Message)
    {
        internal void Restore(MediaTask task)
        {
            task.State = State;
            task.StateBeforeBlocked = StateBeforeBlocked;
            task.BlockedReason = BlockedReason;
            task.Message = Message;
        }
    }

    private sealed record ParameterSnapshot(string Target, string TargetId, string EngineId, string Mode,
        int Version, int AudioTrack, string InputPath, string? SourceRoot, string[] InputPaths,
        ConversionState State, DateTimeOffset? LastAttempt, KeyValuePair<string, string>[] Options)
    {
        internal static ParameterSnapshot Capture(MediaTask task) => new(task.TargetFormat, task.TargetFormatId,
            task.EngineId, task.ConversionMode, task.ParametersVersion, task.SelectedAudioTrack, task.InputPath,
            task.SourceRoot, task.InputPaths.ToArray(), task.State, task.LastAttemptAtUtc,
            task.Options.OrderBy(option => option.Key, StringComparer.OrdinalIgnoreCase).ToArray());

        internal bool Matches(MediaTask task) => Target == task.TargetFormat && TargetId == task.TargetFormatId &&
            EngineId == task.EngineId && Mode == task.ConversionMode && Version == task.ParametersVersion &&
            AudioTrack == task.SelectedAudioTrack && InputPath == task.InputPath && SourceRoot == task.SourceRoot &&
            InputPaths.SequenceEqual(task.InputPaths) && State == task.State && LastAttempt == task.LastAttemptAtUtc &&
            Options.SequenceEqual(task.Options.OrderBy(option => option.Key, StringComparer.OrdinalIgnoreCase));

        internal void ApplyParameters(MediaTask task)
        {
            task.TargetFormat = Target;
            task.TargetFormatId = TargetId;
            task.EngineId = EngineId;
            task.ConversionMode = Mode;
            task.ParametersVersion = Version;
            task.SelectedAudioTrack = AudioTrack;
            task.Options.Clear();
            foreach (var (key, value) in Options) task.Options[key] = value;
        }
    }
}
