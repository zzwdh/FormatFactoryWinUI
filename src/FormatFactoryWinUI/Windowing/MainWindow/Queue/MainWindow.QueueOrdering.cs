using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private const string QueueReorderDragMarker = "FormatFactory.PendingTaskOrder";
    private readonly string _queueReorderDragOwner = Guid.NewGuid().ToString("N");
    private Guid[] _draggedPendingTaskIds = [];

    private void InitializeQueueOrdering()
    {
        ConverterView.PrioritizeTaskButton_ClickRequested += PrioritizeTaskButton_Click;
        ConverterView.TaskRow_DragOverRequested += PendingTaskRow_DragOver;
        ConverterView.TaskRow_DropRequested += PendingTaskRow_Drop;
    }

    private void DisposeQueueOrdering()
    {
        ConverterView.PrioritizeTaskButton_ClickRequested -= PrioritizeTaskButton_Click;
        ConverterView.TaskRow_DragOverRequested -= PendingTaskRow_DragOver;
        ConverterView.TaskRow_DropRequested -= PendingTaskRow_Drop;
        _draggedPendingTaskIds = [];
    }

    private void PrioritizeTaskButton_Click(object sender, RoutedEventArgs args)
    {
        if (_windowClosed || _initializing || _closeSaveInProgress || _settingsResetInProgress || _queueSession.IsStopping) return;
        var task = FindTaskFromSender(sender);
        if (task is null || !_queueSession.CanReorderTask(task.Id)) return;
        var result = _queueOperations.Prioritize(task.Id, StartQueueModel);
        if (result.Changed > 0) SynchronizeQueueOrdering();
        ShowInfo(LocalizationService.T(result.Changed > 0 || result.Scheduled ? "QueuePriorityUpdated" : "QueueOrderUnchanged"),
            InfoBarSeverity.Informational);
    }

    // Returns false only when the existing completed-output drag handler should run.
    private bool TryPrepareQueueReorderDrag(DragItemsStartingEventArgs args)
    {
        _draggedPendingTaskIds = [];
        var tasks = args.Items.OfType<MediaTaskViewModel>().ToArray();
        if (tasks.Length == 0 || tasks.All(task => task.State == ConversionState.Completed)) return false;
        if (_windowClosed || _initializing || _settingsResetInProgress ||
            tasks.Any(task => !_queueSession.CanReorderTask(task.Id)))
        {
            args.Cancel = true;
            return true;
        }
        _draggedPendingTaskIds = tasks.Select(task => task.Id).ToArray();
        args.Data.Properties[QueueReorderDragMarker] = _queueReorderDragOwner;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        return true;
    }

    private bool IsPendingTaskDrag(DragEventArgs args) =>
        args.DataView.Properties.TryGetValue(QueueReorderDragMarker, out var owner) &&
        string.Equals(owner as string, _queueReorderDragOwner, StringComparison.Ordinal);

    private bool CanAcceptPendingTaskDrop(object sender, DragEventArgs args, out MediaTaskViewModel? target)
    {
        target = (sender as FrameworkElement)?.DataContext as MediaTaskViewModel;
        return !_windowClosed && !_initializing && !_settingsResetInProgress &&
            IsPendingTaskDrag(args) && target is not null && _queueOperations.CanMovePending(_draggedPendingTaskIds, target.Id);
    }

    private void PendingTaskRow_DragOver(object sender, DragEventArgs args)
    {
        if (!IsPendingTaskDrag(args)) return;
        args.Handled = true;
        var accepted = CanAcceptPendingTaskDrop(sender, args, out _);
        args.AcceptedOperation = accepted ? DataPackageOperation.Move : DataPackageOperation.None;
        if (!accepted) return;
        var after = sender is FrameworkElement row && args.GetPosition(row).Y >= row.ActualHeight / 2;
        args.DragUIOverride.Caption = LocalizationService.T(after ? "QueueDropAfter" : "QueueDropBefore");
        args.DragUIOverride.IsCaptionVisible = true;
    }

    private void PendingTaskRow_Drop(object sender, DragEventArgs args)
    {
        if (!IsPendingTaskDrag(args)) return;
        args.Handled = true;
        args.AcceptedOperation = DataPackageOperation.None;
        if (!CanAcceptPendingTaskDrop(sender, args, out var target)) return;
        var after = sender is FrameworkElement row && args.GetPosition(row).Y >= row.ActualHeight / 2;
        var changed = _queueSession.MovePendingTasks(_draggedPendingTaskIds, target!.Id, after);
        _draggedPendingTaskIds = [];
        if (changed == 0) return;
        args.AcceptedOperation = DataPackageOperation.Move;
        SynchronizeQueueOrdering();
        ShowInfo(LocalizationService.T("QueueOrderUpdated"), InfoBarSeverity.Informational);
    }

    private void SynchronizeQueueOrdering()
    {
        _queuePage.SynchronizeOrder(_queueSession.Tasks.Select(task => task.Id));
        ScheduleQueueRefresh();
    }
}
