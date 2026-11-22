using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private bool _failureActionInProgress;

    private void InitializeFailureActions() => ConverterView.ResolveFailure_ClickRequested += ResolveFailure_Click;
    private void DisposeFailureActions() => ConverterView.ResolveFailure_ClickRequested -= ResolveFailure_Click;

    private async void ResolveFailure_Click(object sender, RoutedEventArgs args)
    {
        var task = FindTaskFromSender(sender);
        if (!CanResolveFailure(task)) return;
        switch (task!.FailureAction)
        {
            case FailureResolutionAction.SaveValidatedOutput:
                RecoverOutputButton_Click(sender, args);
                return;
            case FailureResolutionAction.LocateSource:
                RelocateSource_Click(sender, args);
                return;
            case FailureResolutionAction.EditConversionSettings:
                await EditConversionOutputsAsync(new[] { task.Model }, false);
                return;
            case FailureResolutionAction.OpenEngineSettings:
                NavView.SelectedItem = NavSettingsItem;
                return;
            case FailureResolutionAction.OpenAutomationRules:
                NavView.SelectedItem = NavAutomationItem;
                return;
            case FailureResolutionAction.ChooseOutputDirectory:
                await ChooseFailureOutputDirectoryAsync(task);
                return;
        }
    }

    private bool CanResolveFailure(MediaTaskViewModel? task) => task is not null && Tasks.Contains(task) &&
        !_windowClosed && !_initializing && !_closeSaveInProgress && !_settingsResetInProgress &&
        !_failureActionInProgress && !_desktopCommandInProgress && !_conversionOptionsOpen &&
        _reviewBusinessDialogs == 0 && !_queueSession.IsStopping && !_queueCoordinator.IsActive(task.Id) &&
        !task.Model.IsRecoveringOutput && task.State is ConversionState.Failed or ConversionState.Blocked;

    private async Task ChooseFailureOutputDirectoryAsync(MediaTaskViewModel task)
    {
        _failureActionInProgress = true;
        _desktopCommandInProgress = true;
        try
        {
            var directory = await PickOutputDirectoryWithDiagnosticsAsync();
            if (directory is null || _windowClosed || _closeSaveInProgress || _settingsResetInProgress ||
                _queueSession.IsStopping || !Tasks.Contains(task) || _queueCoordinator.IsActive(task.Id) ||
                task.State is not (ConversionState.Failed or ConversionState.Blocked) || task.Model.IsRecoveringOutput ||
                task.FailureAction != FailureResolutionAction.ChooseOutputDirectory) return;
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            OutputPathService.VerifyWriteAccess(directory);
            await StorageAccessService.RememberFolderAsync(directory);
            if (_windowClosed || _closeSaveInProgress || _settingsResetInProgress || _queueSession.IsStopping ||
                !Tasks.Contains(task) || _queueCoordinator.IsActive(task.Id) || task.Model.IsRecoveringOutput ||
                task.State is not (ConversionState.Failed or ConversionState.Blocked)) return;
            if (_queueSession.ApplyTaskOptionsWithUndo(new[] { task.Id }, model =>
                {
                    model.Options[OutputPathService.DirectoryOverrideKey] = directory;
                    model.ParametersVersion++;
                }) == 0) return;
            await SaveQueueAsync();
            task.RefreshLocalization();
            if (_windowClosed || _closeSaveInProgress || _settingsResetInProgress || _queueSession.IsStopping ||
                !Tasks.Contains(task) || _queueCoordinator.IsActive(task.Id) || !EnsureTaskEngineReady(task)) return;
            ResetTask(task);
            StartTask(task);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _failureActionInProgress = false;
            _desktopCommandInProgress = false;
        }
    }
}
