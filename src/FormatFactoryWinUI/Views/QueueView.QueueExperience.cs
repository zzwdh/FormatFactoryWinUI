using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Motion;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace FormatFactoryWinUI.Views;

public sealed partial class QueueView
{
    private long _undoMotionVersion;
    internal event RoutedEventHandler? UndoRequested;
    internal event RoutedEventHandler? PrioritizeTaskButton_ClickRequested;
    internal event RoutedEventHandler? RelocateSource_ClickRequested;
    internal event RoutedEventHandler? RelocateSourceFolder_ClickRequested;
    internal event RoutedEventHandler? ResolveFailure_ClickRequested;
    internal event DragEventHandler? TaskRow_DragOverRequested;
    internal event DragEventHandler? TaskRow_DropRequested;

    internal void ShowUndo(string message)
    {
        var wasOpen = QueueUndoInfoBar.IsOpen;
        QueueUndoInfoBar.Message = message;
        QueueUndoInfoBar.IsOpen = true;
        if (wasOpen) return;
        var version = ++_undoMotionVersion;
        if (!IsLoaded || Visibility != Visibility.Visible || !FluentMotion.IsEnabled) return;
        // IsOpen may realize the InfoBar template during the next layout turn.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version == _undoMotionVersion && IsLoaded && Visibility == Visibility.Visible && QueueUndoInfoBar.IsOpen)
                FluentMotion.Entrance(QueueUndoInfoBar, FluentMotion.ContentDurationMs, 4);
        });
    }

    internal void HideUndo()
    {
        ++_undoMotionVersion;
        QueueUndoInfoBar.IsOpen = false;
        FluentMotion.Reset(QueueUndoInfoBar);
    }

    private void ApplyQueueExperienceLocalization()
    {
        QueueUndoButton.Content = LocalizationService.T("QueueUndoAction");
        AutomationProperties.SetName(QueueUndoButton, LocalizationService.T("QueueUndoAction"));
        QueueReorderHintText.Text = LocalizationService.T("QueueReorderHint");
    }

    private void QueueUndo_Click(object sender, RoutedEventArgs args) => UndoRequested?.Invoke(sender, args);
    private void PrioritizeTaskButton_Click(object sender, RoutedEventArgs args) => PrioritizeTaskButton_ClickRequested?.Invoke(sender, args);
    private void RelocateSource_Click(object sender, RoutedEventArgs args) => RelocateSource_ClickRequested?.Invoke(sender, args);
    private void RelocateSourceFolder_Click(object sender, RoutedEventArgs args) => RelocateSourceFolder_ClickRequested?.Invoke(sender, args);
    private void ResolveFailure_Click(object sender, RoutedEventArgs args) => ResolveFailure_ClickRequested?.Invoke(sender, args);
    private void TaskRow_DragOver(object sender, DragEventArgs args) => TaskRow_DragOverRequested?.Invoke(sender, args);
    private void TaskRow_Drop(object sender, DragEventArgs args) => TaskRow_DropRequested?.Invoke(sender, args);
}
