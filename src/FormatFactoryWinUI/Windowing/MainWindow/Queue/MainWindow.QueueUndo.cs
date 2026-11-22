using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _queueUndoTimer;
    private KeyboardAccelerator? _queueUndoAccelerator;
    private readonly Dictionary<Guid, string> _queueUndoPasswords = [];
    private bool _queueUndoRemovalInProgress;

    private void InitializeQueueUndo()
    {
        _queueSession.UndoChanged += QueueUndoChanged;
        ConverterView.UndoRequested += UndoQueue_Click;
        _queueUndoTimer = DispatcherQueue.CreateTimer();
        _queueUndoTimer.Interval = TimeSpan.FromSeconds(1);
        _queueUndoTimer.Tick += QueueUndoTimer_Tick;
        _queueUndoAccelerator = new() { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control };
        _queueUndoAccelerator.Invoked += QueueUndoAccelerator_Invoked;
        RootGrid.KeyboardAccelerators.Add(_queueUndoAccelerator);
    }

    private void DisposeQueueUndo()
    {
        _queueSession.UndoChanged -= QueueUndoChanged;
        ConverterView.UndoRequested -= UndoQueue_Click;
        if (_queueUndoTimer is not null)
        {
            _queueUndoTimer.Stop();
            _queueUndoTimer.Tick -= QueueUndoTimer_Tick;
            _queueUndoTimer = null;
        }
        if (_queueUndoAccelerator is not null)
        {
            _queueUndoAccelerator.Invoked -= QueueUndoAccelerator_Invoked;
            RootGrid.KeyboardAccelerators.Remove(_queueUndoAccelerator);
            _queueUndoAccelerator = null;
        }
        _queueSession.DiscardUndo(finalizeGroups: false);
        _queueUndoPasswords.Clear();
    }

    private void QueueUndoTimer_Tick(DispatcherQueueTimer sender, object args) => _queueSession.ExpireUndo();

    private void QueueUndoChanged(QueueUndoInfo? undo)
    {
        foreach (var id in _queueUndoPasswords.Keys.Where(id => !_queueSession.HasRemovalUndoForTask(id)).ToArray())
            _queueUndoPasswords.Remove(id);
        if (undo is null)
        {
            _queueUndoTimer?.Stop();
            ConverterView.HideUndo();
            return;
        }
        RefreshQueueUndoLocalization();
        _queueUndoTimer?.Start();
    }

    private void RefreshQueueUndoLocalization()
    {
        if (_queueSession.UndoState is { } undo)
            ConverterView.ShowUndo(LocalizationService.T(undo.Kind == QueueUndoKind.RemoveTasks
                ? "QueueUndoRemoved" : "QueueUndoOptions", undo.TaskCount));
    }

    /// <summary>
    /// Detaches rows without releasing their import reservations. Automation-owned
    /// rows keep their existing permanent-removal semantics and are identified to users.
    /// </summary>
    private async Task<int> RemoveTasksWithUndoAsync(IEnumerable<MediaTaskViewModel> selected)
    {
        if (_windowClosed || _closeSaveInProgress || _settingsResetInProgress || _queueSession.IsStopping || _queueUndoRemovalInProgress) return 0;
        _queueUndoRemovalInProgress = true;
        try
        {
            var candidates = selected.Where(Tasks.Contains).DistinctBy(task => task.Id).ToArray();
            var result = await _queueOperations.RemoveWithUndoAsync(candidates.Select(task => task.Id));
            var removed = result.RemovedWithUndo.Select(task => task.Id).ToHashSet();
            if (_windowClosed) return 0;
            foreach (var task in candidates.Where(task => removed.Contains(task.Id)))
            {
                if (_pdfPasswords.Remove(task.Id, out var password)) _queueUndoPasswords[task.Id] = password;
                _ = AppNotificationService.RemoveTaskAsync(task.Id);
                task.PropertyChanged -= Task_PropertyChanged;
                _queuePage.RemoveTask(task);
            }

            var automaticRemoved = 0;
            foreach (var model in result.AutomaticRemovals)
            {
                if (Tasks.FindById(model.Id) is not { } task) continue;
                RemoveTask(task);
                automaticRemoved++;
            }
            if (automaticRemoved > 0)
                ShowInfo(LocalizationService.T("QueueUndoAutomaticRemoved", automaticRemoved), InfoBarSeverity.Informational);
            else if (removed.Count == 0 && candidates.Length > 0)
                ShowInfo(LocalizationService.T("QueueUndoBusy"), InfoBarSeverity.Informational);
            if (removed.Count + automaticRemoved > 0) ScheduleQueueRefresh();
            return removed.Count + automaticRemoved;
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
            return 0;
        }
        finally { _queueUndoRemovalInProgress = false; }
    }

    private void UndoQueue_Click(object? sender, RoutedEventArgs args) => UndoQueueAction();

    private void QueueUndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!DesktopShortcutsAvailable || ConverterView.Visibility != Visibility.Visible ||
            IsTextInputFocused() || _queueSession.UndoState is null) return;
        args.Handled = true;
        UndoQueueAction();
    }

    private void UndoQueueAction()
    {
        if (_windowClosed || _closeSaveInProgress || _settingsResetInProgress || _queueSession.IsStopping || _queueUndoRemovalInProgress) return;
        var expected = _queueSession.UndoState?.TaskCount ?? 0;
        var passwords = _queueUndoPasswords.ToArray();
        var restored = _queueSession.UndoLastAction();
        foreach (var (id, password) in passwords)
            if (Tasks.Any(task => task.Id == id)) _pdfPasswords[id] = password;
        _queuePage.SynchronizeOrder(_queueSession.Tasks.Select(task => task.Id), refreshLocalization: true);
        ScheduleQueueRefresh();
        ShowInfo(LocalizationService.T(restored == 0 ? "QueueUndoUnavailable" : restored < expected
            ? "QueueUndoPartial" : "QueueUndoRestored", restored), InfoBarSeverity.Informational);
    }
}
