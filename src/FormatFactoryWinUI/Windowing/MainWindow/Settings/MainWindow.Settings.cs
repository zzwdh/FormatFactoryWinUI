using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private SettingsCommitCoordinator? _settingsCommits;
    private (string Message, InfoBarSeverity Severity)? _pendingSettingsInfo;

    private void EnsureSettingsViewLoaded()
    {
        if (SettingsView.ViewModel.IsInitialized || _windowClosed) return;
        var wasUpdating = _updatingLocalization;
        _updatingLocalization = true;
        try
        {
            // Window-rooted XAML cannot realize x:Load through FindName. Controls are
            // created by InitializeComponent; initialize their state on the first visit.
            SettingsView.ViewModel.IsInitialized = true;
            ApplyLocalization();
            _updatingLocalization = true;
            SettingsView.IsEnabled = SettingsView.ViewModel.IsAvailable;
            if (SettingsView.ViewModel.IsAvailable)
            {
                ApplySettingsToControls();
                InitializeSettingsDraft();
            }
            RenderDocumentEngineStatus(_documentTools.Values);
            RenderOpticalEngineStatus(_opticalTools.Values);
            UpdateHardwareDecodeControls();
            if (_pendingSettingsInfo is { } info)
            {
                _pendingSettingsInfo = null;
                ShowSettingsInfo(info.Message, info.Severity);
            }
        }
        finally
        {
            _updatingLocalization = wasUpdating;
        }
    }

    private SettingsCommitCoordinator SettingsCommits => _settingsCommits ??= new(
        candidate => _settingsService.SaveAsync(candidate), StartupService.SetEnabledAsync);

    private void InitializeSettingsDraft()
    {
        if (!SettingsView.ViewModel.IsInitialized || SettingsView.ViewModel.DraftSession is not null || _windowClosed) return;
        SettingsView.ViewModel.InitializeDraft(_settings);
        if (_startupSettingsSaveFailure is not null)
        {
            SettingsView.ViewModel.SaveStateKey = "ConversionUiSettingsSaveFailed";
            SettingsView.ViewModel.SaveMessage = LocalizationService.T("ConversionUiSettingsSaveFailedDetails", _startupSettingsSaveFailure.Message);
        }
        RefreshSettingsSaveState();
    }

    private SettingsDraft CaptureSettingsDraft() => SettingsView.CaptureSettingsDraft();

    private void SettingsDraftChanged()
    {
        if (_windowClosed || SettingsView.ViewModel.DraftSession is null || _updatingLocalization) return;
        SettingsView.ViewModel.Edit(CaptureSettingsDraft());
        RefreshSettingsSaveState();
    }

    private void AcceptInitializedSettings()
    {
        if (_windowClosed || SettingsView.ViewModel.DraftSession is null) return;
        SettingsView.ViewModel.DraftSession.AcceptSaved(SettingsDraft.From(_settings));
        SettingsDraftChanged();
    }

    private void ClearSettingsSaveMessage()
    {
        if (SettingsView.ViewModel.IsInitialized && !_windowClosed && SettingsView.ViewModel.SaveMessage is not null && SettingsView.SettingsInfoBar.Message == SettingsView.ViewModel.SaveMessage)
            SettingsView.SettingsInfoBar.IsOpen = false;
        SettingsView.ViewModel.SaveMessage = null;
    }

    private void RefreshSettingsSaveState()
    {
        if (!SettingsView.ViewModel.IsInitialized || _windowClosed) return;
        SettingsView.SettingsSaveStateText.Text = LocalizationService.T(SettingsView.ViewModel.IsSaving ? "ConversionUiSettingsSaving" : SettingsView.ViewModel.SaveStateKey);
        SettingsView.SaveSettingsButton.IsEnabled = !_initializing && !SettingsView.ViewModel.IsSaving;
        SettingsView.WatchFolderPathText.Text = string.IsNullOrWhiteSpace(SettingsView.ViewModel.WatchFolderPath)
            ? LocalizationService.T("WatchFolderPath") : SettingsView.ViewModel.WatchFolderPath;
    }

    // Package paths include the MSIX version; retain automatic discovery across upgrades.
    private static string PersistedEnginePath(string? path) =>
        BundledEngineLocator.IsBundledExecutable(path) ? string.Empty : path ?? string.Empty;

    private async Task ValidateSettingsDraftAsync(SettingsDraft draft)
    {
        if (!string.IsNullOrWhiteSpace(draft.FfmpegPath) && !await _ffmpegLocator.ValidatePairAsync(draft.FfmpegPath))
            throw new SettingsValidationException(LocalizationService.T("FfmpegPathInvalid"));
        if (!string.IsNullOrWhiteSpace(draft.CompatibilityFfmpegPath) && !await _ffmpegLocator.ValidatePairAsync(draft.CompatibilityFfmpegPath))
            throw new SettingsValidationException(LocalizationService.T("MediaCompatibilityEngineUnavailable", draft.CompatibilityFfmpegPath));
        foreach (var path in new[] { draft.LibreOfficePath, draft.CalibreEbookConvertPath, draft.Caj2PdfPath,
            draft.MakeMkvPath, draft.AudioCdRipperPath, draft.ReadomPath })
        {
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
                throw new SettingsValidationException(LocalizationService.T("ConversionUiSettingsToolPathInvalid", path));
        }
        if (draft.WatchFolderEnabled && !Directory.Exists(draft.WatchFolderPath))
            throw new SettingsValidationException(LocalizationService.T("WatchFolderInvalid"));
    }

    private async void InstallMakeMkvButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.InstallMakeMkvButton.IsEnabled = false;
        try
        {
            try
            {
                await _services.Shell.InstallMakeMkvAsync();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ShellExecute returns ERROR_CANCELLED when installation authorization is declined.
                if (!_windowClosed) ShowSettingsInfo(LocalizationService.T("Cancelled"), InfoBarSeverity.Informational);
                return;
            }
            if (!_windowClosed) await RefreshOpticalEngineStatusAsync();
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Report(ex);
            if (!_windowClosed) ShowSettingsInfo(LocalizationService.T("ConversionUiSettingsApplyFailed", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            if (!_windowClosed) SettingsView.InstallMakeMkvButton.IsEnabled = true;
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsView.ViewModel.IsSaving || _initializing || SettingsView.ViewModel.DraftSession is null) return;
        var captured = CaptureSettingsDraft();
        SettingsView.ViewModel.DraftSession.Edit(captured);
        ClearSettingsSaveMessage();
        SettingsView.ViewModel.IsSaving = true;
        RefreshSettingsSaveState();
        var committed = false;
        try
        {
            var normalized = BundledEngineLocator.NormalizeSettingsPaths(captured);
            await SettingsCommits.CommitDraftAsync(_settings, normalized, ValidateSettingsDraftAsync);
            committed = true;
            SettingsView.ViewModel.DraftSession.AcceptSaved(captured);
            ApplyExecutionResourcePolicy();
            ConfigureWatchFolder();
            var ffmpeg = await _ffmpegLocator.LocateAsync(_settings.FfmpegPath);
            await SetFfmpegContextAsync(ffmpeg?.Ffmpeg ?? string.Empty, ffmpeg?.Ffprobe ?? string.Empty, true);
            RefreshTaskEngineAvailability();
            await RefreshDocumentEngineStatusAsync();
            await RefreshOpticalEngineStatusAsync();
            await BackgroundTaskService.ConfigureWatchFolderAsync(_settings.WatchFolderEnabled);
        }
        catch (Exception ex)
        {
            if (committed || ex is not SettingsValidationException) SafeDiagnostics.Report(ex);
            if (!committed) SettingsView.ViewModel.SaveStateKey = "ConversionUiSettingsSaveFailed";
            SettingsView.ViewModel.SaveMessage = LocalizationService.T(
                committed ? "ConversionUiSettingsApplyFailed" : "ConversionUiSettingsSaveFailedDetails", ex.Message);
            ShowSettingsInfo(SettingsView.ViewModel.SaveMessage, committed ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        }
        finally
        {
            SettingsView.ViewModel.IsSaving = false;
            if (committed)
                SettingsView.ViewModel.SaveStateKey = SettingsView.ViewModel.SaveMessage is not null
                    && SettingsView.SettingsInfoBar.Message == SettingsView.ViewModel.SaveMessage && SettingsView.SettingsInfoBar.Severity == InfoBarSeverity.Error
                    ? "ConversionUiSettingsSaveFailed"
                    : SettingsView.ViewModel.DraftSession.IsDirty ? "ConversionUiSettingsUnsaved" : "ConversionUiSettingsSaved";
            RefreshSettingsSaveState();
        }
    }

    private async Task<bool> CommitImmediateSettingAsync(Action<AppSettings> change)
    {
        var previousMessage = SettingsView.ViewModel.SaveMessage;
        try
        {
            await SettingsCommits.UpdateAsync(_settings, change);
            if (SettingsView.ViewModel.SaveMessage == previousMessage)
            {
                ClearSettingsSaveMessage();
                if (!SettingsView.ViewModel.IsSaving)
                    SettingsView.ViewModel.SaveStateKey = SettingsView.ViewModel.DraftSession?.IsDirty == true
                        ? "ConversionUiSettingsUnsaved" : "ConversionUiSettingsSaved";
            }
            RefreshSettingsSaveState();
            return true;
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Report(ex);
            SettingsView.ViewModel.SaveMessage = LocalizationService.T("ConversionUiSettingsSaveFailedDetails", ex.Message);
            if (!SettingsView.ViewModel.IsSaving) SettingsView.ViewModel.SaveStateKey = "ConversionUiSettingsSaveFailed";
            ShowSettingsInfo(SettingsView.ViewModel.SaveMessage, InfoBarSeverity.Error);
            RefreshSettingsSaveState();
            return false;
        }
    }

    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLocalization || _initializing || SettingsView.LanguageCombo.SelectedItem is not LocalizedOption option) return;
        await CommitImmediateSettingAsync(settings => settings.Language = option.Key);
        LocalizationService.SetLanguage(_settings.Language);
        ApplyLocalization();
        await JumpListService.RefreshAsync();
        await RefreshDocumentEngineStatusAsync();
        await RefreshOpticalEngineStatusAsync();
        RefreshVisibleTasks();
        UpdateQueueSummary();
    }

    private async void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLocalization || _initializing || SettingsView.ThemeCombo.SelectedIndex < 0) return;
        var theme = SettingsView.ThemeCombo.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        await CommitImmediateSettingAsync(settings => settings.Theme = theme);
        ApplyTheme();
        _updatingLocalization = true;
        try { SettingsView.ThemeCombo.SelectedIndex = _settings.Theme == "light" ? 1 : _settings.Theme == "dark" ? 2 : 0; }
        finally { _updatingLocalization = false; }
    }

    private async void OutputModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLocalization || _initializing || ConverterView.OutputModeCombo.SelectedIndex < 0) return;
        var mode = ConverterView.OutputModeCombo.SelectedIndex == 1 ? "source" : "custom";
        await CommitImmediateSettingAsync(settings => settings.OutputMode = mode);
        _updatingLocalization = true;
        try { ConverterView.OutputModeCombo.SelectedIndex = _settings.OutputMode == "source" ? 1 : 0; }
        finally { _updatingLocalization = false; }
        ConverterView.OutputDirectoryButton.IsEnabled = _settings.OutputMode == "custom";
    }

    private async void ChooseWatchFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        try
        {
            var token = await StorageAccessService.RememberFolderAsync(folder.Path);
            SettingsView.ViewModel.WatchFolderPath = folder.Path;
            SettingsView.ViewModel.WatchFolderAccessToken = token;
            SettingsDraftChanged();
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Report(ex);
            ShowSettingsInfo(LocalizationService.T("ConversionUiSettingsSaveFailedDetails", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async void ChooseOutputDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderPath = await PickOutputDirectoryWithDiagnosticsAsync();
            if (_windowClosed || folderPath is null) return;
            if (await CommitImmediateSettingAsync(settings => settings.CustomOutputDirectory = folderPath)
                && !_windowClosed)
                ConverterView.OutputDirectoryText.Text = _settings.CustomOutputDirectory;
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed)
                ShowInfo(LocalizationService.T("DesktopChooseOutputFolderFailed"), InfoBarSeverity.Warning);
        }
    }
}
