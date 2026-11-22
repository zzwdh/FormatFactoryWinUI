using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private bool _viewsInitialized;

    private void InitializeViews()
    {
        if (_viewsInitialized) return;
        _viewsInitialized = true;
        InitializeDesktopExperience();
        InitializeBatchActions();
        InitializeQueueUndo();
        InitializeQueueOrdering();
        InitializeSourceRelocation();
        InitializeConversionOptions();
        InitializeFailureActions();
        InitializeHistoryActions();
        ConverterView.OutputSettings_ClickRequested += OutputSettings_Click;
        ConverterView.BindPage(_queuePage);
        HistoryView.Initialize(_historyStore.SearchAsync, RunOnUiAsync, () => _motionReady && !_initializing);
        HistoryView.SearchFailed += HistorySearchFailed;
        SettingsView.DraftChanged += SettingsView_DraftChanged;
        ArchiveView.Configure(() => _windowHandle, ShowContentDialogWithCancellationAsync,
            taskStore: new ArchiveTaskStore(), recordHistory: (item, _) => RecordCompletedHistoryAsync(item));
        ConverterView.AddFilesButton_ClickRequested += AddFilesButton_Click;
        ConverterView.AddFolderButton_ClickRequested += AddFolderButton_Click;
        ConverterView.PasteFilesButton_ClickRequested += PasteFilesButton_Click;
        ConverterView.AddOpticalDiscButton_ClickRequested += AddOpticalDiscButton_Click;
        ConverterView.TaskList_DragItemsStartingRequested += TaskList_DragItemsStarting;
        ConverterView.StartTaskButton_ClickRequested += StartTaskButton_Click;
        ConverterView.PauseTaskButton_ClickRequested += PauseTaskButton_Click;
        ConverterView.PreviewTaskButton_ClickRequested += PreviewTaskButton_Click;
        ConverterView.OpenOutputFileButton_ClickRequested += OpenOutputFileButton_Click;
        ConverterView.CopyOutputButton_ClickRequested += CopyOutputButton_Click;
        ConverterView.ImageColorButton_ClickRequested += ImageColorButton_Click;
        ConverterView.OpenOutputButton_ClickRequested += OpenOutputButton_Click;
        ConverterView.RecoverOutputButton_ClickRequested += RecoverOutputButton_Click;
        ConverterView.ShareOutputButton_ClickRequested += ShareOutputButton_Click;
        ConverterView.MediaInfoButton_ClickRequested += MediaInfoButton_Click;
        ConverterView.CopyErrorButton_ClickRequested += CopyErrorButton_Click;
        ConverterView.CancelOrRemoveButton_ClickRequested += CancelOrRemoveButton_Click;
        ConverterView.PdfPasswordBox_LoadedRequested += PdfPasswordBox_Loaded;
        ConverterView.PdfPasswordBox_DataContextChangedRequested += PdfPasswordBox_DataContextChanged;
        ConverterView.PdfPasswordBox_PasswordChangedRequested += PdfPasswordBox_PasswordChanged;
        ConverterView.OutputModeCombo_SelectionChangedRequested += OutputModeCombo_SelectionChanged;
        ConverterView.ChooseOutputDirectoryButton_ClickRequested += ChooseOutputDirectoryButton_Click;
        ConverterView.PauseAllButton_ClickRequested += PauseAllButton_Click;
        ConverterView.ClearPendingButton_ClickRequested += ClearPendingButton_Click;
        ConverterView.CancelAllButton_ClickRequested += CancelAllButton_Click;
        ConverterView.StartAllButton_ClickRequested += StartAllButton_Click;
        HistoryView.OpenHistoryOutputButton_ClickRequested += OpenHistoryOutputButton_Click;
        SettingsView.BrowseFfmpegButton_ClickRequested += BrowseFfmpegButton_Click;
        SettingsView.BrowseCompatibilityFfmpegButton_ClickRequested += BrowseCompatibilityFfmpegButton_Click;
        SettingsView.DetectFfmpegButton_ClickRequested += DetectFfmpegButton_Click;
        SettingsView.DetectOpticalEnginesButton_ClickRequested += DetectOpticalEnginesButton_Click;
        SettingsView.InstallMakeMkvButton_ClickRequested += InstallMakeMkvButton_Click;
        SettingsView.BrowseMakeMkvButton_ClickRequested += BrowseMakeMkvButton_Click;
        SettingsView.BrowseAudioCdRipperButton_ClickRequested += BrowseAudioCdRipperButton_Click;
        SettingsView.BrowseReadomButton_ClickRequested += BrowseReadomButton_Click;
        SettingsView.DetectDocumentEnginesButton_ClickRequested += DetectDocumentEnginesButton_Click;
        SettingsView.BrowseLibreOfficeButton_ClickRequested += BrowseLibreOfficeButton_Click;
        SettingsView.BrowseCalibreButton_ClickRequested += BrowseCalibreButton_Click;
        SettingsView.BrowseCajButton_ClickRequested += BrowseCajButton_Click;
        SettingsView.HardwareDecodeTestButton_ClickRequested += HardwareDecodeTestButton_Click;
        SettingsView.ChooseWatchFolderButton_ClickRequested += ChooseWatchFolderButton_Click;
        SettingsView.LanguageCombo_SelectionChangedRequested += LanguageCombo_SelectionChanged;
        SettingsView.ThemeCombo_SelectionChangedRequested += ThemeCombo_SelectionChanged;
        SettingsView.UsageTelemetryToggle_ToggledRequested += UsageTelemetryToggle_Toggled;
        SettingsView.AutomaticErrorReportingToggle_ToggledRequested += AutomaticErrorReportingToggle_Toggled;
        SettingsView.AutomaticUpdatesToggle_ToggledRequested += AutomaticUpdatesToggle_Toggled;
        SettingsView.CheckForUpdatesButton_ClickRequested += CheckForUpdatesButton_Click;
        SettingsView.OpenStoreUpdatesButton_ClickRequested += OpenStoreUpdatesButton_Click;
        SettingsView.SaveSettingsButton_ClickRequested += SaveSettingsButton_Click;
        SettingsView.ResetSettingsButton_ClickRequested += ResetSettingsButton_Click;
        SettingsView.RateAppButton_ClickRequested += RateAppButton_Click;
        SettingsView.SendFeedbackButton_ClickRequested += SendFeedbackButton_Click;
        SettingsView.ReportErrorButton_ClickRequested += ReportErrorButton_Click;
        SettingsView.ExportErrorReportButton_ClickRequested += ExportErrorReportButton_Click;
        RefreshFeedbackState();
        SettingsView.OpenLogsButton_ClickRequested += OpenLogsButton_Click;
    }

    private void SettingsView_DraftChanged(object? sender, EventArgs args) => SettingsDraftChanged();

    private void HistorySearchFailed(object? sender, Exception exception)
    {
        SafeDiagnostics.Report(exception);
        if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
    }

    private void DisposeViews()
    {
        if (!_viewsInitialized) return;
        _viewsInitialized = false;
        DisposeDesktopExperience();
        DisposeBatchActions();
        DisposeQueueUndo();
        DisposeQueueOrdering();
        DisposeSourceRelocation();
        DisposeConversionOptions();
        DisposeFailureActions();
        DisposeHistoryActions();
        ConverterView.OutputSettings_ClickRequested -= OutputSettings_Click;
        HistoryView.SearchFailed -= HistorySearchFailed;
        SettingsView.DraftChanged -= SettingsView_DraftChanged;
        ArchiveView.Dispose();
        ConverterView.AddFilesButton_ClickRequested -= AddFilesButton_Click;
        ConverterView.AddFolderButton_ClickRequested -= AddFolderButton_Click;
        ConverterView.PasteFilesButton_ClickRequested -= PasteFilesButton_Click;
        ConverterView.AddOpticalDiscButton_ClickRequested -= AddOpticalDiscButton_Click;
        ConverterView.TaskList_DragItemsStartingRequested -= TaskList_DragItemsStarting;
        ConverterView.StartTaskButton_ClickRequested -= StartTaskButton_Click;
        ConverterView.PauseTaskButton_ClickRequested -= PauseTaskButton_Click;
        ConverterView.PreviewTaskButton_ClickRequested -= PreviewTaskButton_Click;
        ConverterView.OpenOutputFileButton_ClickRequested -= OpenOutputFileButton_Click;
        ConverterView.CopyOutputButton_ClickRequested -= CopyOutputButton_Click;
        ConverterView.ImageColorButton_ClickRequested -= ImageColorButton_Click;
        ConverterView.OpenOutputButton_ClickRequested -= OpenOutputButton_Click;
        ConverterView.RecoverOutputButton_ClickRequested -= RecoverOutputButton_Click;
        ConverterView.ShareOutputButton_ClickRequested -= ShareOutputButton_Click;
        ConverterView.MediaInfoButton_ClickRequested -= MediaInfoButton_Click;
        ConverterView.CopyErrorButton_ClickRequested -= CopyErrorButton_Click;
        ConverterView.CancelOrRemoveButton_ClickRequested -= CancelOrRemoveButton_Click;
        ConverterView.PdfPasswordBox_LoadedRequested -= PdfPasswordBox_Loaded;
        ConverterView.PdfPasswordBox_DataContextChangedRequested -= PdfPasswordBox_DataContextChanged;
        ConverterView.PdfPasswordBox_PasswordChangedRequested -= PdfPasswordBox_PasswordChanged;
        ConverterView.OutputModeCombo_SelectionChangedRequested -= OutputModeCombo_SelectionChanged;
        ConverterView.ChooseOutputDirectoryButton_ClickRequested -= ChooseOutputDirectoryButton_Click;
        ConverterView.PauseAllButton_ClickRequested -= PauseAllButton_Click;
        ConverterView.ClearPendingButton_ClickRequested -= ClearPendingButton_Click;
        ConverterView.CancelAllButton_ClickRequested -= CancelAllButton_Click;
        ConverterView.StartAllButton_ClickRequested -= StartAllButton_Click;
        HistoryView.OpenHistoryOutputButton_ClickRequested -= OpenHistoryOutputButton_Click;
        SettingsView.BrowseFfmpegButton_ClickRequested -= BrowseFfmpegButton_Click;
        SettingsView.BrowseCompatibilityFfmpegButton_ClickRequested -= BrowseCompatibilityFfmpegButton_Click;
        SettingsView.DetectFfmpegButton_ClickRequested -= DetectFfmpegButton_Click;
        SettingsView.DetectOpticalEnginesButton_ClickRequested -= DetectOpticalEnginesButton_Click;
        SettingsView.InstallMakeMkvButton_ClickRequested -= InstallMakeMkvButton_Click;
        SettingsView.BrowseMakeMkvButton_ClickRequested -= BrowseMakeMkvButton_Click;
        SettingsView.BrowseAudioCdRipperButton_ClickRequested -= BrowseAudioCdRipperButton_Click;
        SettingsView.BrowseReadomButton_ClickRequested -= BrowseReadomButton_Click;
        SettingsView.DetectDocumentEnginesButton_ClickRequested -= DetectDocumentEnginesButton_Click;
        SettingsView.BrowseLibreOfficeButton_ClickRequested -= BrowseLibreOfficeButton_Click;
        SettingsView.BrowseCalibreButton_ClickRequested -= BrowseCalibreButton_Click;
        SettingsView.BrowseCajButton_ClickRequested -= BrowseCajButton_Click;
        SettingsView.HardwareDecodeTestButton_ClickRequested -= HardwareDecodeTestButton_Click;
        SettingsView.ChooseWatchFolderButton_ClickRequested -= ChooseWatchFolderButton_Click;
        SettingsView.LanguageCombo_SelectionChangedRequested -= LanguageCombo_SelectionChanged;
        SettingsView.ThemeCombo_SelectionChangedRequested -= ThemeCombo_SelectionChanged;
        SettingsView.UsageTelemetryToggle_ToggledRequested -= UsageTelemetryToggle_Toggled;
        SettingsView.AutomaticErrorReportingToggle_ToggledRequested -= AutomaticErrorReportingToggle_Toggled;
        SettingsView.AutomaticUpdatesToggle_ToggledRequested -= AutomaticUpdatesToggle_Toggled;
        SettingsView.CheckForUpdatesButton_ClickRequested -= CheckForUpdatesButton_Click;
        SettingsView.OpenStoreUpdatesButton_ClickRequested -= OpenStoreUpdatesButton_Click;
        SettingsView.SaveSettingsButton_ClickRequested -= SaveSettingsButton_Click;
        SettingsView.ResetSettingsButton_ClickRequested -= ResetSettingsButton_Click;
        SettingsView.RateAppButton_ClickRequested -= RateAppButton_Click;
        SettingsView.SendFeedbackButton_ClickRequested -= SendFeedbackButton_Click;
        SettingsView.ReportErrorButton_ClickRequested -= ReportErrorButton_Click;
        SettingsView.ExportErrorReportButton_ClickRequested -= ExportErrorReportButton_Click;
        DisposeFeedback();
        SettingsView.OpenLogsButton_ClickRequested -= OpenLogsButton_Click;
        HistoryView.Dispose();
    }
}
