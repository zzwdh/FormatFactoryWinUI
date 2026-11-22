using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private bool _settingsResetRequested;
    private bool _settingsResetInProgress;

    private bool SettingsResetIsBusy => _windowClosed || _initializing || _startupTask is { IsCompleted: false } || _closeSaveInProgress
        || _queueSession.IsStopping || _queueCoordinator.HasActiveOperations
        || Tasks.Any(task => task.State is ConversionState.Running or ConversionState.Paused or ConversionState.Probing)
        || _automationCompletions.Count > 0 || _automationService?.HasActiveWork == true
        || _dropImportInProgress || _activeImport is not null || _reviewAttemptInProgress
        || SettingsView.ViewModel.IsSaving || _updatePreferenceSaves > 0 || _feedbackInProgress;

    private async void ResetSettingsButton_Click(object sender, RoutedEventArgs args)
    {
        if (_settingsResetRequested || _settingsResetInProgress || _windowClosed) return;
        if (SettingsResetIsBusy)
        {
            ShowSettingsInfo(LocalizationService.T("SettingsActionsResetBusy"), InfoBarSeverity.Warning);
            return;
        }

        _settingsResetRequested = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                FlowDirection = RootGrid.FlowDirection,
                Title = LocalizationService.T("SettingsActionsResetConfirmTitle"),
                Content = new TextBlock
                {
                    Text = LocalizationService.T("SettingsActionsResetConfirmMessage"),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520
                },
                PrimaryButtonText = LocalizationService.T("SettingsActionsResetButton"),
                CloseButtonText = LocalizationService.T("Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await ShowContentDialogAsync(dialog) == ContentDialogResult.Primary)
                await RestoreDefaultSettingsAsync();
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed)
                ShowSettingsInfo(LocalizationService.T("SettingsActionsResetFailed", exception.Message), InfoBarSeverity.Error);
        }
        finally { _settingsResetRequested = false; }
    }

    private async Task<bool> RestoreDefaultSettingsAsync()
    {
        if (_settingsResetInProgress || SettingsResetIsBusy)
        {
            if (!_windowClosed) ShowSettingsInfo(LocalizationService.T("SettingsActionsResetBusy"), InfoBarSeverity.Warning);
            return false;
        }

        _settingsResetInProgress = true;
        CancelPendingHistoryReplays();
        SettingsView.ViewModel.IsSaving = true;
        ClearSettingsSaveMessage();
        RefreshSettingsSaveState();
        var settingsEnabled = SettingsView.IsEnabled;
        var archiveEnabled = ArchivePage.IsEnabled;
        var automationEnabled = AutomationView.IsEnabled;
        var converterEnabled = ConverterView.IsEnabled;
        SettingsView.IsEnabled = ArchivePage.IsEnabled = AutomationView.IsEnabled = ConverterView.IsEnabled = false;
        var committed = false;
        var automationLocked = false;
        var previousRules = Array.Empty<AutomationRule>();
        var automationChanged = false;
        try
        {
            await _automationChanges.WaitAsync();
            automationLocked = true;
            if (_windowClosed || _closeSaveInProgress || _queueSession.IsStopping) return false;

            // Disable discovery without StopAsync: stopping would cancel automatic queue tasks.
            // Keep each rule's watcher/schedule preferences so it can be re-enabled later.
            previousRules = (_automationReady && _automationService is not null
                ? _automationService.Rules
                : (await _services.AutomationStore.LoadAsync()).Rules).Select(rule => rule.Snapshot()).ToArray();
            var disabledRules = previousRules.Select(rule => rule with { Enabled = false }).ToArray();
            automationChanged = true;
            if (_automationService is not null)
                await _automationService.ConfigureAsync(disabledRules);
            else
                await _services.AutomationStore.SaveRulesAsync(disabledRules);

            await SettingsCommits.ResetAsync(_settings);
            committed = true;
            _automationRules = disabledRules;
            ConfigureWatchFolder();
            await BackgroundTaskService.ConfigureWatchFolderAsync(false);
            AutomationView.RestoreRules(disabledRules);

            var wasUpdating = _updatingLocalization;
            _updatingLocalization = true;
            try
            {
                LocalizationService.SetLanguage(_settings.Language);
                ApplyLocalization();
                _updatingLocalization = true;
                ApplySettingsToControls(initializeRuntime: true);
                SettingsView.IsEnabled = ArchivePage.IsEnabled = false;
                SettingsView.ViewModel.ResetDraft(_settings);
                SettingsView.EngineOptionsExpander.IsExpanded = false;
                _hardwareDecodeReport = null;
                UpdateHardwareDecodeControls();
            }
            finally { _updatingLocalization = wasUpdating; }

            var ffmpeg = await _ffmpegLocator.LocateAsync(_settings.FfmpegPath);
            await SetFfmpegContextAsync(ffmpeg?.Ffmpeg ?? string.Empty, ffmpeg?.Ffprobe ?? string.Empty, true);
            RefreshTaskEngineAvailability();
            await RefreshDocumentEngineStatusAsync();
            await RefreshOpticalEngineStatusAsync();
            if (!_windowClosed)
                ShowSettingsInfo(LocalizationService.T("SettingsActionsResetSucceeded"), InfoBarSeverity.Success);
            return true;
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!committed && automationChanged)
            {
                try
                {
                    if (_automationService is not null) await _automationService.ConfigureAsync(previousRules);
                    else await _services.AutomationStore.SaveRulesAsync(previousRules);
                }
                catch (Exception rollbackFailure)
                {
                    SafeDiagnostics.Report(rollbackFailure);
                    exception = new AggregateException(exception, rollbackFailure);
                }
            }
            if (!_windowClosed)
                ShowSettingsInfo(LocalizationService.T(
                    committed ? "SettingsActionsResetApplyFailed" : "SettingsActionsResetFailed", exception.Message),
                    committed ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            return committed;
        }
        finally
        {
            if (automationLocked) _automationChanges.Release();
            _settingsResetInProgress = false;
            SettingsView.ViewModel.IsSaving = false;
            if (!_windowClosed)
            {
                SettingsView.IsEnabled = settingsEnabled;
                ArchivePage.IsEnabled = archiveEnabled;
                AutomationView.IsEnabled = automationEnabled;
                ConverterView.IsEnabled = converterEnabled;
                RefreshSettingsSaveState();
            }
        }
    }

    private async void RateAppButton_Click(object sender, RoutedEventArgs args)
    {
        if (_windowClosed || _reviewAttemptInProgress || _settingsResetInProgress || _feedbackInProgress) return;
        if (!StoreReviewService.IsStoreInstalled)
        {
            ShowSettingsInfo(LocalizationService.T("SettingsActionsRateStoreOnly"), InfoBarSeverity.Informational);
            return;
        }
        if (!StoreReviewService.CanRequestReview)
        {
            ShowSettingsInfo(LocalizationService.T("SettingsActionsRateElevated"), InfoBarSeverity.Warning);
            return;
        }

        _reviewAttemptInProgress = true;
        _reviewShownThisSession = true;
        _reviewBusinessDialogs++;
        _reviewReminderTimer?.Stop();
        SettingsView.RateAppButton.IsEnabled = false;
        try
        {
            await DismissReviewReminderAsync();
            if (_windowClosed) return;
            var result = await StoreReviewService.RequestAsync(_windowHandle);
            if (result == StoreReviewResult.Succeeded)
            {
                _reviewPromptPending = false;
                _reviewShownThisSession = true;
                try { await _services.ReviewReminders.StopRemindersAsync(); }
                catch (Exception exception) { SafeDiagnostics.Report(exception); }
                if (!_windowClosed)
                    ShowSettingsInfo(LocalizationService.T("ReviewReminderThanks"), InfoBarSeverity.Success);
            }
            else if (result == StoreReviewResult.Failed && !_windowClosed)
                ShowSettingsInfo(LocalizationService.T("ReviewReminderFailed"), InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed)
                ShowSettingsInfo(LocalizationService.T("ReviewReminderFailed"), InfoBarSeverity.Warning);
        }
        finally
        {
            _reviewAttemptInProgress = false;
            _reviewBusinessDialogs--;
            if (!_windowClosed) SettingsView.RateAppButton.IsEnabled = true;
            ScheduleReviewReminder();
        }
    }
}
