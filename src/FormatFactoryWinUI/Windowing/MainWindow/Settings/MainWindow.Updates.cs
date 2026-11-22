using FormatFactoryWinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Launcher = Windows.System.Launcher;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private StoreUpdateCoordinator? _storeUpdates;
    private IStoreUpdateClient? _storeUpdateClient;
    private DispatcherQueueTimer? _storeUpdateTimer;
    private readonly CancellationTokenSource _storeUpdateLifetime = new();
    private int _updatePreferenceVersion;
    private int _updatePreferenceSaves;
    private Task<bool>? _updatePreferencePersistence;

    private void InitializeStoreUpdates()
    {
        if (_windowClosed || _closeSaveInProgress || _storeUpdates is not null) return;
        _storeUpdateClient = new WindowsStoreUpdateClient(_windowHandle, DispatcherQueue);
        _storeUpdates = new(_storeUpdateClient);
        _storeUpdates.StatusChanged += StoreUpdateStatusChanged;
        _storeUpdateTimer = DispatcherQueue.CreateTimer();
        _storeUpdateTimer.IsRepeating = false;
        _storeUpdateTimer.Tick += StoreUpdateTimer_Tick;
        ApplyAutomaticUpdatesFromSettings();
    }

    private void ApplyAutomaticUpdatesFromSettings()
    {
        if (_storeUpdates is null) return;
        ++_updatePreferenceVersion;
        _storeUpdates.Configure(_settings.EnableAutomaticUpdates);
        ScheduleStoreUpdateCheck(TimeSpan.FromSeconds(30));
        RefreshStoreUpdateState();
    }

    private void ScheduleStoreUpdateCheck(TimeSpan delay)
    {
        _storeUpdateTimer?.Stop();
        if (_storeUpdateTimer is null || _windowClosed || _closeSaveInProgress
            || !_settings.EnableAutomaticUpdates || _storeUpdateClient?.IsAvailable != true) return;
        _storeUpdateTimer.Interval = delay;
        _storeUpdateTimer.Start();
    }

    private async void StoreUpdateTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (_windowClosed || _closeSaveInProgress || _settingsResetInProgress || _storeUpdates is null) return;
            await _storeUpdates.CheckAsync(manual: false, _storeUpdateLifetime.Token);
        }
        finally { ScheduleStoreUpdateCheck(TimeSpan.FromHours(6)); }
    }

    private void StoreUpdateStatusChanged(StoreUpdateStatus status)
    {
        if (_windowClosed) return;
        if (DispatcherQueue.HasThreadAccess) RefreshStoreUpdateState();
        else DispatcherQueue.TryEnqueue(RefreshStoreUpdateState);
    }

    private void RefreshStoreUpdateState()
    {
        if (_windowClosed) return;
        var status = _storeUpdates?.Status ?? StoreUpdateStatus.Unavailable;
        if (_storeUpdateClient?.IsAvailable != true) status = StoreUpdateStatus.Unavailable;
        if (status == StoreUpdateStatus.Ready && !_settings.EnableAutomaticUpdates) status = StoreUpdateStatus.Disabled;
        SettingsView.SetUpdateState(status switch
        {
            StoreUpdateStatus.Checking => "StoreUpdateChecking",
            StoreUpdateStatus.Downloading => "StoreUpdateDownloading",
            StoreUpdateStatus.Ready => "StoreUpdateReady",
            StoreUpdateStatus.UpToDate => "StoreUpdateUpToDate",
            StoreUpdateStatus.Unavailable => "StoreUpdateUnavailable",
            StoreUpdateStatus.NeedsStore => "StoreUpdateNeedsStore",
            StoreUpdateStatus.Failed => "StoreUpdateFailed",
            StoreUpdateStatus.Disabled => "StoreUpdateDisabled",
            StoreUpdateStatus.Installing => "StoreUpdateInstalling",
            _ => "StoreUpdateIdle"
        },
            _storeUpdates?.IsBusy == true, _storeUpdateClient?.IsAvailable == true);
    }

    private async void AutomaticUpdatesToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_updatingLocalization || _initializing || _windowClosed || _closeSaveInProgress || _storeUpdates is null) return;
        var enabled = SettingsView.AutomaticUpdatesToggle.IsOn;
        var revision = ++_updatePreferenceVersion;
        ++_updatePreferenceSaves;
        // Stop future automatic work before waiting for persistence.
        if (!enabled)
        {
            _storeUpdateTimer?.Stop();
            _storeUpdates.Configure(false);
        }
        try
        {
            var persistence = CommitImmediateSettingAsync(settings => settings.EnableAutomaticUpdates = enabled);
            _updatePreferencePersistence = persistence;
            var saved = await persistence;
            if (_windowClosed || revision != _updatePreferenceVersion) return;
            if (saved)
            {
                _storeUpdates.Configure(enabled);
                ScheduleStoreUpdateCheck(TimeSpan.FromSeconds(30));
            }
            else
            {
                // Keep this session opted out after a failed opt-out write, but report that it was not saved.
                if (!enabled) _settings.EnableAutomaticUpdates = false;
                SettingsView.ApplyUpdatePreferences(_settings);
            }
            RefreshStoreUpdateState();
        }
        finally { --_updatePreferenceSaves; }
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs args)
    {
        if (_windowClosed || _initializing || _closeSaveInProgress || _settingsResetInProgress || _storeUpdates is null) return;
        await _storeUpdates.CheckAsync(manual: true, _storeUpdateLifetime.Token);
    }

    private async void OpenStoreUpdatesButton_Click(object sender, RoutedEventArgs args)
    {
        if (_windowClosed || _closeSaveInProgress) return;
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-windows-store://downloadsandupdates")) && !_windowClosed)
                ShowSettingsInfo(LocalizationService.T("StoreUpdateFailed"), InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowSettingsInfo(LocalizationService.T("StoreUpdateFailed"), InfoBarSeverity.Warning);
        }
    }

    private async Task TryInstallStoreUpdateOnExitAsync()
    {
        _storeUpdateTimer?.Stop();
        if (_storeUpdates is null || !_settings.EnableAutomaticUpdates || _updatePreferenceSaves > 0
            || !_closeSaveInProgress || !_queueSession.IsStopping || _initializing
            || _storeUpdates.Status != StoreUpdateStatus.Ready || _storeUpdates.IsBusy
            || _queueCoordinator.HasActiveOperations
            || _automationService?.HasActiveWork == true || _automationCompletions.Count > 0
            || _dropImportInProgress || _reviewActivationInProgress > 0 || _reviewBusinessDialogs > 0
            || SettingsView.ViewModel.IsSaving || _feedbackInProgress) return;

        // Shutdown has already stopped new queue work and saved pending tasks. Prevent late UI actions.
        NavView.IsEnabled = false;
        // Updating an exiting application should not relaunch it through crash-recovery registration.
        RecoveryService.Unregister();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_storeUpdateLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await _storeUpdates.TryInstallOnExitAsync(timeout.Token).WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { } // A slow Store service must not prevent normal exit.
        catch (Exception exception) { SafeDiagnostics.Report(exception); }
    }

    private async Task WaitForStoreUpdatePreferenceSaveAsync()
    {
        // The settings commit gate serializes writes, so the latest save also drains earlier toggles.
        // Once closing begins the toggle handler cannot submit another write.
        if (_updatePreferencePersistence is { } pending) await pending;
    }

    private void DisposeStoreUpdates()
    {
        _storeUpdateTimer?.Stop();
        if (_storeUpdateTimer is not null) _storeUpdateTimer.Tick -= StoreUpdateTimer_Tick;
        _storeUpdateLifetime.Cancel();
        if (_storeUpdates is not null)
        {
            _storeUpdates.StatusChanged -= StoreUpdateStatusChanged;
            _storeUpdates.Dispose();
        }
    }
}
