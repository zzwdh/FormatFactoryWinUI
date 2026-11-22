using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

public enum QueueRemovalAction { None, RemoveWithUndo, Cancel, RemoveAfterOperation }
public sealed record QueueBatchEditPlan(Guid[] SelectedIds, MediaTask[] EditableTasks, string[] CommonFormats);
public sealed record QueuePriorityResult(int Changed, bool Scheduled);
public sealed record QueueRemovalResult(MediaTask[] RemovedWithUndo, MediaTask[] AutomaticRemovals, int CandidateCount)
{
    public int RemovedCount => RemovedWithUndo.Length + AutomaticRemovals.Length;
}

/// <summary>
/// Coordinates queue commands independently of controls. QueueSession remains the
/// owner of admission, durable transactions, ordering constraints, and undo holds.
/// Native process actions and visual side effects are supplied by the host.
/// </summary>
public sealed class QueueOperationsCoordinator(QueueSession session)
{
    public MediaTask[] SelectPending() => session.Tasks.Where(task => task.State == ConversionState.Waiting).ToArray();
    public MediaTask[] SelectToStop()
    {
        var ids = QueueActionPolicy.SelectToStop(session.Tasks, session.Coordinator.IsActive).ToHashSet();
        return session.Tasks.Where(task => ids.Contains(task.Id)).ToArray();
    }

    public QueueBatchEditPlan PrepareBatchEdit(IEnumerable<Guid> taskIds)
    {
        var ids = taskIds.Distinct().ToArray();
        var editable = session.GetEditableTasks(ids);
        string[] formats = editable.Length == 0 ? [] : editable.Skip(1).Aggregate(editable[0].AvailableFormats.AsEnumerable(),
                (common, task) => common.Intersect(task.AvailableFormats, StringComparer.OrdinalIgnoreCase))
            .OrderBy(format => format, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new(ids, editable, formats);
    }

    public int StartWaiting(Func<MediaTask, bool> isReady, Action<MediaTask> start)
    {
        var started = 0;
        foreach (var task in SelectPending())
        {
            if (session.IsStopping || task.IsRecoveringOutput || session.Coordinator.IsActive(task.Id) || !isReady(task)) continue;
            start(task);
            started++;
        }
        return started;
    }

    public bool StartTask(MediaTask task, Func<MediaTask, bool> isReady, Action<MediaTask> start)
    {
        if (session.IsStopping || task.IsRecoveringOutput ||
            task.State is ConversionState.Running or ConversionState.Paused or ConversionState.Probing || !isReady(task)) return false;
        if (task.State is ConversionState.Completed or ConversionState.Failed or ConversionState.Cancelled) ResetForRetry(task);
        start(task);
        return true;
    }

    public void ToggleTaskPause(MediaTask task, Action<MediaTask> pause, Action<MediaTask> resume)
    {
        if (task.State == ConversionState.Running) pause(task);
        else if (task.State == ConversionState.Paused) resume(task);
    }

    public bool CanMovePending(IEnumerable<Guid> selectedIds, Guid targetId)
    {
        var selected = selectedIds.ToArray();
        return selected.Length > 0 && !selected.Contains(targetId) && session.CanReorderTask(targetId) && selected.All(session.CanReorderTask);
    }

    public int RetryFailed(Func<MediaTask, bool> isReady, Action<MediaTask> start)
    {
        var started = 0;
        foreach (var task in session.GetRetryableTasks())
        {
            if (session.Coordinator.IsActive(task.Id) || !isReady(task)) continue;
            if (task.State == ConversionState.Failed) ResetForRetry(task);
            start(task);
            started++;
        }
        return started;
    }

    public void TogglePause(Action<MediaTask> pause, Action<MediaTask> resume)
    {
        var shouldPause = session.Tasks.Any(task => task.State == ConversionState.Running);
        var candidates = session.Tasks.Where(task => task.State == (shouldPause ? ConversionState.Running : ConversionState.Paused)).ToArray();
        foreach (var task in candidates)
            if (shouldPause) pause(task); else resume(task);
    }

    public QueueRemovalAction ResolveRemoval(MediaTask task)
    {
        if (session.IsStopping || task.IsRecoveringOutput || !session.Tasks.ContainsReference(task)) return QueueRemovalAction.None;
        if (task.State == ConversionState.Waiting && !task.Options.ContainsKey("automation.ruleSnapshot"))
            return QueueRemovalAction.RemoveWithUndo;
        if (session.Coordinator.IsActive(task.Id))
            return task.State is ConversionState.Waiting or ConversionState.Completed or ConversionState.Failed or ConversionState.Cancelled
                ? QueueRemovalAction.RemoveAfterOperation : QueueRemovalAction.Cancel;
        return task.State is ConversionState.Completed or ConversionState.Failed or ConversionState.Cancelled or ConversionState.Waiting or ConversionState.Blocked
            ? QueueRemovalAction.RemoveWithUndo : QueueRemovalAction.Cancel;
    }

    public void ResetForRetry(MediaTask task)
    {
        if (task.IsRecoveringOutput) return;
        if (task.OutputGroupId is { } groupId)
            foreach (var member in session.GetOutputGroup(groupId)) member.OutputGroupFinalized = false;
        task.OutputHistoryRecorded = false;
        task.OutputFingerprint = null;
        task.State = ConversionState.Waiting;
        task.Progress = 0; task.Speed = "—"; task.Remaining = "—";
        task.OutputPath = string.Empty;
        task.Message = LocalizationService.T("WaitingStart");
    }

    public QueuePriorityResult Prioritize(Guid taskId, Action<MediaTask> start)
    {
        if (!session.CanReorderTask(taskId) || session.Tasks.FindById(taskId) is not { } selected) return new(0, false);
        var batchActive = session.Coordinator.HasActiveOperations;
        var changed = session.PrioritizePendingTask(taskId);
        var scheduled = false;
        if (batchActive)
        {
            var group = session.Tasks.Where(task => task.Id == taskId || selected.OutputGroupId is { } id && task.OutputGroupId == id)
                .Where(task => session.CanReorderTask(task.Id) && !session.Coordinator.IsActive(task.Id)).ToArray();
            foreach (var task in group)
            { start(task); scheduled |= session.Coordinator.IsActive(task.Id); }
            // Urgent new tasks join admission first, then receive the same pending order.
            session.PrioritizePendingTask(taskId);
        }
        return new(changed, scheduled);
    }

    public async Task<QueueRemovalResult> RemoveWithUndoAsync(IEnumerable<Guid> taskIds)
    {
        var ids = taskIds.ToHashSet();
        var candidates = session.Tasks.Where(task => ids.Contains(task.Id)).ToArray();
        var removed = await session.RemoveTasksWithUndoAsync(candidates.Select(task => task.Id));
        var removedIds = removed.Select(task => task.Id).ToHashSet();
        var automatic = candidates.Where(task => !removedIds.Contains(task.Id) && session.Tasks.ContainsReference(task) &&
            task.Options.ContainsKey("automation.ruleSnapshot") && !session.Coordinator.IsActive(task.Id) && !task.IsRecoveringOutput &&
            task.State is not (ConversionState.Running or ConversionState.Paused or ConversionState.Probing)).ToArray();
        return new(removed, automatic, candidates.Length);
    }
}
