using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsPageViewModel ViewModel { get; } = new();
    internal event EventHandler? DraftChanged;

    public SettingsView()
    {
        InitializeComponent();
        AutomationProperties.SetLabeledBy(LanguageCombo, LanguageTitleText);
        foreach (var box in new[] { FfmpegPathTextBox, CompatibilityFfmpegPathTextBox, LibreOfficePathTextBox, CalibrePathTextBox,
            Caj2PdfPathTextBox, MakeMkvPathTextBox, AudioCdRipperPathTextBox, ReadomPathTextBox, OcrLanguageTextBox })
            box.TextChanged += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        foreach (var toggle in new[] { HardwareToggle, NotificationToggle, SoundToggle, TrayToggle,
            StartWithWindowsToggle, StartInTrayToggle, WatchFolderToggle, RepairDocxToggle })
            toggle.Toggled += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        ParallelCountBox.ValueChanged += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        CpuThreadsBox.ValueChanged += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        EbookLayoutModeCombo.SelectionChanged += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ApplySettings(AppSettings settings)
    {
        IsEnabled = ViewModel.IsAvailable;
        FfmpegPathTextBox.Text = settings.FfmpegPath;
        CompatibilityFfmpegPathTextBox.Text = settings.CompatibilityFfmpegPath;
        RepairDocxToggle.IsOn = settings.RepairDocxOnImport;
        LibreOfficePathTextBox.Text = settings.LibreOfficePath;
        CalibrePathTextBox.Text = settings.CalibreEbookConvertPath;
        Caj2PdfPathTextBox.Text = settings.Caj2PdfPath;
        MakeMkvPathTextBox.Text = settings.MakeMkvPath;
        AudioCdRipperPathTextBox.Text = settings.AudioCdRipperPath;
        ReadomPathTextBox.Text = settings.ReadomPath;
        EbookLayoutModeCombo.SelectedIndex = settings.EbookLayoutMode == "fixed" ? 1 : 0;
        OcrLanguageTextBox.Text = settings.OcrLanguage;
        ParallelCountBox.Value = Math.Clamp(settings.ParallelCount, 1, 4);
        CpuThreadsBox.Value = Math.Clamp(settings.CpuThreadsPerTask, 0, Environment.ProcessorCount);
        CpuStrategyText.Text = PerformanceTuning.DescribeCpuStrategy(settings);
        HardwareToggle.IsOn = settings.HardwareAcceleration;
        NotificationToggle.IsOn = settings.Notifications;
        SoundToggle.IsOn = settings.NotificationSound;
        TrayToggle.IsOn = settings.AlwaysMinimizeToTray;
        WatchFolderToggle.IsOn = settings.WatchFolderEnabled;
        WatchFolderPathText.Text = string.IsNullOrWhiteSpace(settings.WatchFolderPath) ? LocalizationService.T("WatchFolderPath") : settings.WatchFolderPath;
        StartWithWindowsToggle.IsOn = settings.StartWithWindows;
        StartInTrayToggle.IsOn = settings.StartInTray;
        ApplyTelemetryPreferences(settings);
        ApplyUpdatePreferences(settings);
        ThemeCombo.SelectedIndex = settings.Theme == "light" ? 1 : settings.Theme == "dark" ? 2 : 0;

    }

    internal void ApplyLocalization(AppSettings settings)
    {
        var ebookLayoutIndex = ViewModel.DraftSession is null
            ? (settings.EbookLayoutMode == "fixed" ? 1 : 0) : EbookLayoutModeCombo.SelectedIndex;
        SettingsTitleText.Text = LocalizationService.T("SettingsTitle");
        SettingsSubtitleText.Text = LocalizationService.T("SettingsSubtitle");
        EngineOptionsTitleText.Text = LocalizationService.T("SettingsActionsEngineOptionsTitle");
        EngineOptionsDescriptionText.Text = LocalizationService.T("SettingsActionsEngineOptionsDescription");
        AutomationProperties.SetName(EngineOptionsExpander, EngineOptionsTitleText.Text);
        FfmpegDescriptionText.Text = LocalizationService.T("FfmpegDescription");
        FfmpegPathTextBox.PlaceholderText = LocalizationService.T("PathPlaceholder");
        BrowseFfmpegButton.Content = LocalizationService.T("Browse");
        DetectFfmpegButton.Content = LocalizationService.T("AutoDetect");
        var lacksX64Emulation = PortableExecutableInspector.IsX64EmulationUnavailable;
        var compatibilityEngineTitle = LocalizationService.T("MediaCompatibilityEngineTitle");
        CompatibilityFfmpegPathTextBox.Header = compatibilityEngineTitle;
        CompatibilityFfmpegPathTextBox.PlaceholderText = LocalizationService.T(
            lacksX64Emulation ? "PathPlaceholder" : "MediaCompatibilityEnginePlaceholder");
        CompatibilityEngineHelpText.Text = LocalizationService.T("MediaCompatibilityEngineHelp");
        if (lacksX64Emulation)
            CompatibilityEngineHelpText.Text += Environment.NewLine + BundledEngineLocator.FormatX64UnavailableStatus(compatibilityEngineTitle);
        BrowseCompatibilityFfmpegButton.Content = LocalizationService.T("Browse");
        RepairDocxToggle.Header = LocalizationService.T("MediaCompatibilityRepairDocx");
        RepairDocxHelpText.Text = LocalizationService.T("MediaCompatibilityRepairDocxHelp");
        DocumentEnginesTitleText.Text = LocalizationService.T("DocumentEnginesTitle");
        DocumentEnginesDescriptionText.Text = LocalizationService.T("DocumentEnginesDescription");
        DetectDocumentEnginesButton.Content = LocalizationService.T("RefreshEngineStatus");
        BrowseLibreOfficeButton.Content = LocalizationService.T("Browse");
        BrowseCalibreButton.Content = LocalizationService.T("Browse");
        BrowseCajButton.Content = LocalizationService.T("Browse");
        OpticalEnginesTitleText.Text = LocalizationService.T("OpticalEnginesTitle");
        OpticalEnginesDescriptionText.Text = LocalizationService.T("OpticalEnginesDescription");
        DetectOpticalEnginesButton.Content = LocalizationService.T("RefreshEngineStatus");
        BrowseMakeMkvButton.Content = LocalizationService.T("Browse");
        InstallMakeMkvButton.Content = LocalizationService.T("ConversionUiInstallMakeMkv");
        InstallMakeMkvButton.Visibility = BundledEngineInstaller.HasMakeMkvInstaller ? Visibility.Visible : Visibility.Collapsed;
        MakeMkvInstallHintText.Text = lacksX64Emulation
            ? BundledEngineLocator.FormatX64UnavailableStatus("MakeMKV")
            : LocalizationService.T("ConversionUiMakeMkvInstallHint");
        BrowseAudioCdRipperButton.Content = LocalizationService.T("Browse");
        BrowseReadomButton.Content = LocalizationService.T("Browse");
        MakeMkvPathTextBox.PlaceholderText = LocalizationService.T("CoreMakeMkvPathPlaceholder");
        AudioCdRipperPathTextBox.PlaceholderText = LocalizationService.T("CoreAudioCdRipperPathPlaceholder");
        ReadomPathTextBox.PlaceholderText = LocalizationService.T("CoreReadomPathPlaceholder");
        LibreOfficePathTextBox.PlaceholderText = LocalizationService.T("CoreLibreOfficePathPlaceholder");
        CalibrePathTextBox.PlaceholderText = LocalizationService.T("CoreCalibrePathPlaceholder");
        Caj2PdfPathTextBox.PlaceholderText = LocalizationService.T("CoreCajPathPlaceholder");
        AudioCdRipperLabelText.Text = LocalizationService.T("CoreAudioCdRipperLabel");
        CajConverterLabelText.Text = LocalizationService.T("CoreCajConverterLabel");
        EbookLayoutModeCombo.Header = LocalizationService.T("EbookLayoutMode");
        EbookLayoutModeCombo.Items.Clear();
        EbookLayoutModeCombo.Items.Add(LocalizationService.T("EbookReflow"));
        EbookLayoutModeCombo.Items.Add(LocalizationService.T("EbookFixed"));
        EbookLayoutModeCombo.SelectedIndex = ebookLayoutIndex;
        OcrLanguageTextBox.Header = LocalizationService.T("OcrLanguage");
        OcrLanguageTextBox.PlaceholderText = LocalizationService.T("OcrLanguagePlaceholder");
        PerformanceTitleText.Text = LocalizationService.T("Performance");
        PerformanceDescriptionText.Text = LocalizationService.T("PerformanceDescription");
        HardwareSettingsTitleText.Text = LocalizationService.T("HardwareSettingsSectionTitle");
        HardwareSettingsDescriptionText.Text = LocalizationService.T("HardwareSettingsSectionDescription");
        HardwareDecodeTestButton.Content = LocalizationService.T("HardwareSettingsCheckNow");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            HardwareCardsRepeater,
            LocalizationService.T("HardwareSettingsHardwareListAutomationName"));
        ParallelCountBox.Header = LocalizationService.T("ParallelTasks");
        CpuThreadsBox.Header = LocalizationService.T("CpuThreads");
        HardwareToggle.Header = LocalizationService.T("AutoHardware");
        foreach (var toggle in new[] { HardwareToggle, NotificationToggle, SoundToggle, TrayToggle,
            StartWithWindowsToggle, StartInTrayToggle, WatchFolderToggle, RepairDocxToggle,
            UsageTelemetryToggle, AutomaticErrorReportingToggle })
        {
            toggle.OnContent = LocalizationService.T("HardwareOn");
            toggle.OffContent = LocalizationService.T("HardwareOff");
        }
        NotificationToggle.Header = LocalizationService.T("TaskNotification");
        SoundToggle.Header = LocalizationService.T("NotificationSound");
        TrayToggle.Header = LocalizationService.T("MinimizeTray");
        WatchFolderTitleText.Text = LocalizationService.T("WatchFolderTitle");
        WatchFolderDescriptionText.Text = LocalizationService.T("WatchFolderDescription");
        WatchFolderToggle.Header = LocalizationService.T("WatchFolderEnabled");
        WatchFolderPathText.Text = string.IsNullOrWhiteSpace(settings.WatchFolderPath) ? LocalizationService.T("WatchFolderPath") : settings.WatchFolderPath;
        ChooseWatchFolderButton.Content = LocalizationService.T("ChooseFolder");
        ToolTipService.SetToolTip(WatchFolderPathButton, LocalizationService.T("ChooseFolder"));
        StartupTitleText.Text = LocalizationService.T("StartupTitle");
        StartupDescriptionText.Text = LocalizationService.T("StartupDescription");
        StartWithWindowsToggle.Header = LocalizationService.T("StartWithWindows");
        StartInTrayToggle.Header = LocalizationService.T("StartInTray");
        ThemeTitleText.Text = LocalizationService.T("Theme");
        ThemeDescriptionText.Text = LocalizationService.T("ThemeDescription");
        SaveSettingsButton.Content = LocalizationService.T("SaveSettings");
        ResetSettingsTitleText.Text = LocalizationService.T("SettingsActionsResetTitle");
        ResetSettingsDescriptionText.Text = LocalizationService.T("SettingsActionsResetDescription");
        ResetSettingsButton.Content = LocalizationService.T("SettingsActionsResetButton");
        RateAppTitleText.Text = LocalizationService.T("SettingsActionsRateTitle");
        RateAppDescriptionText.Text = LocalizationService.T("SettingsActionsRateDescription");
        RateAppButton.Content = LocalizationService.T("SettingsActionsRateButton");
        ProjectHomeLink.Content = LocalizationService.T("SettingsActionsProjectHome");
        SubmitIssueLink.Content = LocalizationService.T("SettingsActionsSubmitIssue");
        ApplyFeedbackLocalization();
        ApplyTelemetryLocalization();
        ApplyUpdateLocalization();
        OpenLogsButton.Content = LocalizationService.T("OpenLogs");
        FooterText.Text = LocalizationService.T("Footer");
        LanguageTitleText.Text = LocalizationService.T("Language");
        LanguageDescriptionText.Text = LocalizationService.T("LanguageDescription");
        ThemeCombo.Items.Clear();
        ThemeCombo.Items.Add(LocalizationService.T("ThemeSystem"));
        ThemeCombo.Items.Add(LocalizationService.T("ThemeLight"));
        ThemeCombo.Items.Add(LocalizationService.T("ThemeDark"));
        ThemeCombo.SelectedIndex = settings.Theme == "light" ? 1 : settings.Theme == "dark" ? 2 : 0;
        var languageOptions = LocalizationService.LanguageOptions;
        LanguageCombo.ItemsSource = languageOptions;
        LanguageCombo.SelectedItem = languageOptions.FirstOrDefault(option => option.Key.Equals(settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? languageOptions[0];
        ApplyResponsiveLocalization();
    }

    internal void ApplyResponsiveLocalization()
    {
        AutomationProperties.SetName(LanguageCombo, LocalizationService.T("Language"));
        AutomationProperties.SetName(ThemeCombo, LocalizationService.T("Theme"));
    }

    internal SettingsDraft CaptureSettingsDraft() => new()
    {
        FfmpegPath = FfmpegPathTextBox.Text.Trim(),
        CompatibilityFfmpegPath = CompatibilityFfmpegPathTextBox.Text.Trim(),
        RepairDocxOnImport = RepairDocxToggle.IsOn,
        LibreOfficePath = LibreOfficePathTextBox.Text.Trim(),
        CalibreEbookConvertPath = CalibrePathTextBox.Text.Trim(),
        Caj2PdfPath = Caj2PdfPathTextBox.Text.Trim(),
        MakeMkvPath = MakeMkvPathTextBox.Text.Trim(),
        AudioCdRipperPath = AudioCdRipperPathTextBox.Text.Trim(),
        ReadomPath = ReadomPathTextBox.Text.Trim(),
        EbookLayoutMode = EbookLayoutModeCombo.SelectedIndex == 1 ? "fixed" : "reflow",
        OcrLanguage = string.IsNullOrWhiteSpace(OcrLanguageTextBox.Text) ? "auto" : OcrLanguageTextBox.Text.Trim(),
        ParallelCount = double.IsFinite(ParallelCountBox.Value) ? Math.Clamp((int)ParallelCountBox.Value, 1, 4) : 2,
        CpuThreadsPerTask = double.IsFinite(CpuThreadsBox.Value) ? Math.Clamp((int)CpuThreadsBox.Value, 0, Environment.ProcessorCount) : 0,
        HardwareAcceleration = HardwareToggle.IsOn,
        Notifications = NotificationToggle.IsOn,
        NotificationSound = SoundToggle.IsOn,
        AlwaysMinimizeToTray = TrayToggle.IsOn,
        StartWithWindows = StartWithWindowsToggle.IsOn,
        StartInTray = StartInTrayToggle.IsOn,
        WatchFolderEnabled = WatchFolderToggle.IsOn,
        WatchFolderPath = ViewModel.WatchFolderPath,
        WatchFolderAccessToken = ViewModel.WatchFolderAccessToken
    };

    internal event RoutedEventHandler? BrowseFfmpegButton_ClickRequested;
    internal event RoutedEventHandler? BrowseCompatibilityFfmpegButton_ClickRequested;
    private void BrowseCompatibilityFfmpegButton_Click(object sender, RoutedEventArgs args) => BrowseCompatibilityFfmpegButton_ClickRequested?.Invoke(sender, args);
    private void BrowseFfmpegButton_Click(object sender, RoutedEventArgs args) => BrowseFfmpegButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? DetectFfmpegButton_ClickRequested;
    private void DetectFfmpegButton_Click(object sender, RoutedEventArgs args) => DetectFfmpegButton_ClickRequested?.Invoke(sender, args);


    internal event RoutedEventHandler? DetectOpticalEnginesButton_ClickRequested;
    private void DetectOpticalEnginesButton_Click(object sender, RoutedEventArgs args) => DetectOpticalEnginesButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? InstallMakeMkvButton_ClickRequested;
    private void InstallMakeMkvButton_Click(object sender, RoutedEventArgs args) => InstallMakeMkvButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? BrowseMakeMkvButton_ClickRequested;
    private void BrowseMakeMkvButton_Click(object sender, RoutedEventArgs args) => BrowseMakeMkvButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? BrowseAudioCdRipperButton_ClickRequested;
    private void BrowseAudioCdRipperButton_Click(object sender, RoutedEventArgs args) => BrowseAudioCdRipperButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? BrowseReadomButton_ClickRequested;
    private void BrowseReadomButton_Click(object sender, RoutedEventArgs args) => BrowseReadomButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? DetectDocumentEnginesButton_ClickRequested;
    private void DetectDocumentEnginesButton_Click(object sender, RoutedEventArgs args) => DetectDocumentEnginesButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? BrowseLibreOfficeButton_ClickRequested;
    private void BrowseLibreOfficeButton_Click(object sender, RoutedEventArgs args) => BrowseLibreOfficeButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? BrowseCalibreButton_ClickRequested;
    private void BrowseCalibreButton_Click(object sender, RoutedEventArgs args) => BrowseCalibreButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? BrowseCajButton_ClickRequested;
    private void BrowseCajButton_Click(object sender, RoutedEventArgs args) => BrowseCajButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? HardwareDecodeTestButton_ClickRequested;
    private void HardwareDecodeTestButton_Click(object sender, RoutedEventArgs args) => HardwareDecodeTestButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? ChooseWatchFolderButton_ClickRequested;
    private void ChooseWatchFolderButton_Click(object sender, RoutedEventArgs args) => ChooseWatchFolderButton_ClickRequested?.Invoke(sender, args);

    internal event SelectionChangedEventHandler? LanguageCombo_SelectionChangedRequested;
    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs args) => LanguageCombo_SelectionChangedRequested?.Invoke(sender, args);

    internal event SelectionChangedEventHandler? ThemeCombo_SelectionChangedRequested;
    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs args) => ThemeCombo_SelectionChangedRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? SaveSettingsButton_ClickRequested;
    private void SaveSettingsButton_Click(object sender, RoutedEventArgs args) => SaveSettingsButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? ResetSettingsButton_ClickRequested;
    private void ResetSettingsButton_Click(object sender, RoutedEventArgs args) => ResetSettingsButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? RateAppButton_ClickRequested;
    private void RateAppButton_Click(object sender, RoutedEventArgs args) => RateAppButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? OpenLogsButton_ClickRequested;
    private void OpenLogsButton_Click(object sender, RoutedEventArgs args) => OpenLogsButton_ClickRequested?.Invoke(sender, args);
}
