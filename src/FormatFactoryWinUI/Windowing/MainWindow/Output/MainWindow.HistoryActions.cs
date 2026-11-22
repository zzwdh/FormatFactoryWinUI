using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _historyReplayCancellation;
    private readonly HashSet<Guid> _historyReplayInProgress = [];

    private void InitializeHistoryActions()
    {
        _historyReplayCancellation = new CancellationTokenSource();
        HistoryView.RepeatHistoryTaskButton_ClickRequested += RepeatHistoryTaskButton_Click;
        HistoryView.OpenHistoryFileButton_ClickRequested += OpenHistoryFileButton_Click;
    }

    private void CancelPendingHistoryReplays()
    {
        _historyReplayCancellation?.Cancel();
        _historyReplayCancellation?.Dispose();
        _historyReplayCancellation = new CancellationTokenSource();
    }

    private void DisposeHistoryActions()
    {
        HistoryView.RepeatHistoryTaskButton_ClickRequested -= RepeatHistoryTaskButton_Click;
        HistoryView.OpenHistoryFileButton_ClickRequested -= OpenHistoryFileButton_Click;
        _historyReplayCancellation?.Cancel();
        _historyReplayCancellation?.Dispose();
        _historyReplayCancellation = null;
    }

    private async void RepeatHistoryTaskButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ConversionHistoryItem item } button ||
            _historyReplayCancellation is null || _queueSession.IsStopping || _settingsResetInProgress || _closeSaveInProgress || _windowClosed ||
            !_historyReplayInProgress.Add(item.TaskId)) return;
        var token = _historyReplayCancellation.Token;
        button.IsEnabled = false;
        MediaImportCoordinator? importer = null;
        Models.MediaTask? imported = null;
        var queueAdded = false;
        try
        {
            if (ArchiveHistoryReplayService.IsArchive(item))
            {
                var missing = HistoryReplayService.MissingInputs(item);
                if (item.InputPaths.Count == 0 || missing.Count > 0)
                    throw new InvalidDataException(LocalizationService.T("HistoryReplayInputMissing",
                        missing.FirstOrDefault() ?? item.DisplayName));
                var (options, encrypted) = ArchiveHistoryReplayService.ReadOptions(item);
                await ArchiveView.RestoreAsync(token);
                token.ThrowIfCancellationRequested();
                if (_windowClosed || _closeSaveInProgress || _queueSession.IsStopping || _settingsResetInProgress) return;
                NavView.SelectedItem = NavArchiveItem;
                if (!ArchiveView.AddRestoredInputs(item.InputPaths, options, encrypted)) return;
                await ArchiveView.FlushAsync();
                ShowInfo(LocalizationService.T("HistoryReplayAdded", item.DisplayName), InfoBarSeverity.Success);
                return;
            }
            importer = _services.Conversions.CreateImporter();
            var result = await HistoryReplayService.ImportAsync(item, _ffprobePath, importer, token);
            if (result.Error is { } error) throw error;
            imported = result.Task;
            if (imported is null)
                throw new InvalidDataException(LocalizationService.T("HistoryReplayInputMissing", item.InputPaths.FirstOrDefault() ?? item.DisplayName));
            token.ThrowIfCancellationRequested();
            if (_windowClosed || _closeSaveInProgress || _queueSession.IsStopping || _settingsResetInProgress) return;
            if (!_queueSession.AddTask(imported)) return;
            queueAdded = true;
            _mediaImportCoordinator.RememberTask(imported);
            NavView.SelectedItem = NavAllItem;
            await SaveQueueAsync();
            ShowInfo(LocalizationService.T(item.SnapshotVersion == 0 ? "HistoryReplayLegacyAdded" : "HistoryReplayAdded", item.DisplayName),
                item.SnapshotVersion == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_windowClosed) ShowInfo(queueAdded
                ? LocalizationService.T("HistoryReplaySaveFailed", exception.Message) : exception.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            // The temporary importer owns only a reservation; the queue owns the resulting task.
            if (imported is not null) importer?.Forget(imported);
            _historyReplayInProgress.Remove(item.TaskId);
            if (!_windowClosed) button.IsEnabled = true;
        }
    }

    private async void OpenHistoryFileButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                ShowInfo(LocalizationService.T("HistoryOutputMissing", path), InfoBarSeverity.Warning);
                return;
            }
            var opened = Directory.Exists(path)
                ? await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(path))
                : await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
            if (!opened) ShowInfo(LocalizationService.T("DesktopOpenFailed"), InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
    }
}
