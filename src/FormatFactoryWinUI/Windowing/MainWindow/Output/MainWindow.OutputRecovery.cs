using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private readonly HashSet<Guid> _outputRecoveryInProgress = [];

    private static async Task PruneRecoveryInBackgroundAsync()
    {
        try { await OutputRecoveryService.Shared.PruneAsync(); }
        catch (Exception exception) { SafeDiagnostics.Report(exception); }
    }


    private async void RecoverOutputButton_Click(object sender, RoutedEventArgs args)
    {
        var task = FindTaskFromSender(sender);
        if (task is null || !Tasks.Contains(task) || task.State is not (ConversionState.Failed or ConversionState.Blocked) || _windowClosed || _initializing ||
            _closeSaveInProgress || _settingsResetInProgress || _queueSession.IsStopping ||
            _desktopCommandInProgress || _queueCoordinator.IsActive(task.Id) ||
            !_outputRecoveryInProgress.Add(task.Id)) return;
        _desktopCommandInProgress = true;
        task.Model.IsRecoveringOutput = true;
        try
        {
            await OutputRecoveryService.Shared.LoadIndexAsync();
            if (_windowClosed) return;
            var recovery = OutputRecoveryService.Shared.GetByIdCached(task.Model.RecoverableOutputId)
                ?? OutputRecoveryService.Shared.GetCached(task.Id);
            if (recovery is null)
            {
                ShowInfo(LocalizationService.T("OutputRecoveryMissing"), InfoBarSeverity.Warning);
                return;
            }
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || _windowClosed || !Tasks.Contains(task)) return;
            await StorageAccessService.RememberFolderAsync(folder.Path);
            ShowInfo(LocalizationService.T("OutputRecoverySaving"), InfoBarSeverity.Informational);
            var output = await OutputRecoveryService.Shared.SaveAsAsync(recovery.Id, folder.Path);
            await CompleteRecoveredOutputAsync(task, output, recovery);
            if (!_windowClosed) ShowInfo(LocalizationService.T("OutputRecoverySaved", output), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _desktopCommandInProgress = false;
            task.Model.IsRecoveringOutput = false;
            _outputRecoveryInProgress.Remove(task.Id);
        }
    }
}
