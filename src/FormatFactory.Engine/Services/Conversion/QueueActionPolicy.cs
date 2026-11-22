using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public enum QueueSortOrder
{
    FileName,
    SmallestFirst,
    LargestFirst
}

internal static class QueueActionPolicy
{
    internal static Guid[] SelectPending(IEnumerable<MediaTask> tasks) => tasks
        .Where(task => task.State == ConversionState.Waiting)
        .Select(task => task.Id)
        .ToArray();

    internal static Guid[] SelectToStop(IEnumerable<MediaTask> tasks, Func<Guid, bool> isScheduled) => tasks
        .Where(task => task.State is ConversionState.Running or ConversionState.Paused or ConversionState.Probing ||
                       task.State == ConversionState.Waiting && isScheduled(task.Id))
        .Select(task => task.Id)
        .ToArray();

    internal static bool CanBatchEdit(MediaTask task, Func<Guid, bool> isScheduled) =>
        task.State is ConversionState.Waiting or ConversionState.Failed or ConversionState.Cancelled or ConversionState.Blocked &&
        !isScheduled(task.Id);

    internal static Guid[] SelectRetryable(IEnumerable<MediaTask> tasks, Func<Guid, bool> isScheduled) => tasks
        .Where(task => task.State is ConversionState.Failed or ConversionState.Blocked && !isScheduled(task.Id))
        .Select(task => task.Id)
        .ToArray();
}