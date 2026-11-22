using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.ApplicationServices;

public sealed partial class QueueSession
{
    public bool CanReorderTask(Guid taskId) => !_stopping && !_disposed &&
        Tasks.FindById(taskId) is { } task && IsPendingForOrdering(task);

    private bool IsPendingForOrdering(MediaTask task) =>
        task is { State: ConversionState.Waiting, IsRecoveringOutput: false } &&
        !_pendingRestore.Contains(task.Id) && !IsRemovalUndoPending(task.Id) && Coordinator.CanReorder(task.Id);

    /// <summary>Moves a waiting task to the first pending position without interrupting admitted work.</summary>
    public int PrioritizePendingTask(Guid taskId)
    {
        if (!CanReorderTask(taskId)) return 0;
        var before = Tasks.ToArray();
        var pending = before.Where(IsPendingForOrdering).ToArray();
        var selected = pending.FirstOrDefault(task => task.Id == taskId);
        if (selected is null) return 0;
        var block = new List<MediaTask> { selected };
        if (selected.OutputGroupId is { } groupId)
            block.AddRange(pending.Where(task => task.Id != taskId && task.OutputGroupId == groupId));
        var moving = block.Select(task => task.Id).ToHashSet();
        return ApplyPendingOrder(before, pending, [.. block, .. pending.Where(task => !moving.Contains(task.Id))]);
    }

    /// <summary>
    /// Moves pending rows as one block while preserving non-pending positions.
    /// Crossing output groups also moves idle siblings, with selected outputs first.
    /// Moving within one group only moves the explicitly selected outputs.
    /// </summary>
    public int MovePendingTasks(IEnumerable<Guid> taskIds, Guid targetTaskId, bool insertAfter = false)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        if (_stopping || _disposed) return 0;
        var selected = taskIds.ToHashSet();
        if (selected.Count == 0 || selected.Contains(targetTaskId) ||
            !CanReorderTask(targetTaskId) || selected.Any(id => !CanReorderTask(id))) return 0;
        var before = Tasks.ToArray();
        var pending = before.Where(IsPendingForOrdering).ToArray();
        var target = pending.FirstOrDefault(task => task.Id == targetTaskId);
        if (target is null) return 0;
        var block = new List<MediaTask>();
        var included = new HashSet<Guid>();
        foreach (var task in pending.Where(task => selected.Contains(task.Id)))
        {
            if (!included.Add(task.Id)) continue;
            block.Add(task);
            if (task.OutputGroupId is not { } groupId || groupId == target.OutputGroupId) continue;
            foreach (var sibling in pending.Where(item => item.OutputGroupId == groupId)
                         .OrderByDescending(item => selected.Contains(item.Id)))
                if (included.Add(sibling.Id)) block.Add(sibling);
        }
        var ordered = pending.Where(task => !included.Contains(task.Id)).ToList();
        var withinTargetGroup = target.OutputGroupId is { } targetGroup &&
            block.All(task => task.OutputGroupId == targetGroup);
        var targetIndex = target.OutputGroupId is { } boundaryGroup && !withinTargetGroup
            ? (insertAfter ? ordered.FindLastIndex(task => task.OutputGroupId == boundaryGroup)
                : ordered.FindIndex(task => task.OutputGroupId == boundaryGroup))
            : ordered.FindIndex(task => task.Id == targetTaskId);
        if (targetIndex < 0) return 0;
        ordered.InsertRange(targetIndex + (insertAfter ? 1 : 0), block);
        return ApplyPendingOrder(before, pending, ordered);
    }

    private int ApplyPendingOrder(MediaTask[] before, MediaTask[] pending, IReadOnlyList<MediaTask> ordered)
    {
        var movable = pending.Select(task => task.Id).ToHashSet();
        var position = 0;
        var after = before.Select(task => movable.Contains(task.Id) ? ordered[position++] : task).ToArray();
        var changed = before.Where((task, index) => task.Id != after[index].Id).Count();

        // Recheck admission under the scheduler lock: a released slot may have made
        // an apparently Waiting row active while the new order was being calculated.
        if (!Coordinator.TryReorderPendingOperations(ordered.Select(task => task.Id).ToArray(), () =>
            {
                for (var index = 0; index < after.Length; index++)
                {
                    var current = Tasks.IndexOf(after[index]);
                    if (current != index) Tasks.Move(current, index);
                }
            })) return 0;
        if (changed > 0) _ = CheckpointSafelyAsync();
        return changed;
    }
}
