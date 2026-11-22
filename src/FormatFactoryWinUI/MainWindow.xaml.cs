using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowServices _services;
    private readonly StartupReadinessMetrics _startupMetrics;
    internal StartupReadinessMetrics StartupMetrics => _startupMetrics;
    private readonly SettingsService _settingsService;
    private readonly ConversionHistoryStore _historyStore;
    private readonly FfmpegLocator _ffmpegLocator;
    private readonly ConversionService _conversionService;
    private readonly ConversionQueueCoordinator _queueCoordinator;
    private readonly QueueSession _queueSession;
    private readonly QueuePageViewModel _queuePage;
    private readonly QueueOperationsCoordinator _queueOperations;
    private readonly MediaImportCoordinator _mediaImportCoordinator;
    private readonly OpticalDiscDiscovery _opticalDiscDiscovery;
    private readonly WatchFolderImportCoordinator _watchFolderCoordinator;
    private readonly HardwareAccelerationTestService _hardwareAccelerationTest;
    private readonly DocumentConversionRoutePlanner _documentRoutes = new();
    private readonly WindowsFilterTextExtractor _windowsFilterTextExtractor;
    private readonly OpticalDeviceWatcher _opticalDeviceWatcher;
    private readonly SystemPowerStateService _systemPowerStateService;
    private readonly WindowsAccessibilityService _accessibilityService;
    private HardwareInventory? _hardwareInventory;
    private HardwareDecodeReport? _hardwareDecodeReport;
    private string? _hardwareInventoryError;
    private string? _hardwareDecodeTestError;
    private bool _hardwareInventoryLoading = true;
    private bool _hardwareDecodeTestRunning;
    private bool _hardwareDecodeNeedsFfmpeg;
    private int _hardwareDecodeContextVersion;
    private IReadOnlyDictionary<ExternalToolKind, ExternalToolInfo> _documentTools =
        new Dictionary<ExternalToolKind, ExternalToolInfo>();
    private IReadOnlyDictionary<OpticalDiscToolKind, OpticalDiscToolInfo> _opticalTools =
        new Dictionary<OpticalDiscToolKind, OpticalDiscToolInfo>();

    private AppSettings _settings = new();
    private string _ffmpegPath = string.Empty;
    private string _ffprobePath = string.Empty;
    private string _filter { get => _queuePage.Filter; set => _queuePage.Filter = value; }
    private IntPtr _windowHandle;
    private AppWindow? _appWindow;
    private TrayIconService? _trayIcon;
    private TaskbarProgressService? _taskbarProgress;
    private WindowsShareService? _shareService;
    private readonly ApplicationLifecycleCoordinator _lifecycle = new();
    private bool _initializing { get => _lifecycle.IsInitializing; set => _lifecycle.IsInitializing = value; }
    private Exception? _startupSettingsSaveFailure;
    private bool _allowClose { get => _lifecycle.CanClose; set => _lifecycle.CanClose = value; }
    private bool _closeSaveInProgress { get => _lifecycle.IsClosing; set => _lifecycle.IsClosing = value; }
    private readonly bool _startInTray;
    private TaskCompletionSource<bool> _activationReady => _lifecycle.ActivationCompletion;
    private Task? _startupTask => _lifecycle.StartupTask;
    private readonly Dictionary<Guid, string> _pdfPasswords = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PasswordBox, PdfPasswordEditorState> _pdfPasswordEditors = new();
    private bool _updatingLocalization;

    public IdIndexedCollection<MediaTaskViewModel> Tasks => _queuePage.Tasks;
    public ObservableCollection<MediaTaskViewModel> VisibleTasks => _queuePage.VisibleTasks;
    public ObservableCollection<ConversionHistoryItem> HistoryItems => HistoryView.ViewModel.Items;

    internal MainWindow(MainWindowServices services, bool startInTray = false,
        Task<bool>? reviewReminderRegistration = null, StartupReadinessMetrics? startupMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _startupMetrics = startupMetrics ?? new();
        using var construction = _startupMetrics.MeasureStage("window-construction");
        _services = services;
        _settingsService = services.Settings;
        _historyStore = services.Conversions.HistoryStore;
        _ffmpegLocator = services.Conversions.FfmpegLocator;
        _conversionService = services.Conversions.Conversion;
        _queueCoordinator = services.Conversions.QueueCoordinator;
        _queueSession = services.Conversions.CreateQueueSession(() => _ffprobePath, ExecuteConversionAsync,
            (item, _) => RecordCompletedHistoryAsync(item), RunOnUiAsync);
        _queuePage = new(_queueCoordinator.IsActive);
        _queueOperations = new(_queueSession);
        _queueSession.TaskAdded += QueueSession_TaskAdded;
        _queueSession.TaskStateChanged += QueueSession_TaskStateChanged;
        _queueSession.RestoreCompleted += QueueSession_RestoreCompleted;
        _queueSession.SideEffectFailed += QueueSession_SideEffectFailed;
        _mediaImportCoordinator = services.Conversions.MediaImport;
        _opticalDiscDiscovery = services.Conversions.OpticalDiscDiscovery;
        _watchFolderCoordinator = services.Conversions.WatchFolderImport;
        _hardwareAccelerationTest = services.HardwareAccelerationTest;
        _windowsFilterTextExtractor = services.FilterTextExtractor;
        _opticalDeviceWatcher = services.OpticalDeviceWatcher;
        _systemPowerStateService = services.PowerState;
        _accessibilityService = services.Accessibility;
        _startInTray = startInTray;
        _reviewReminderRegistration = reviewReminderRegistration;
        InitializeComponent();
        InitializeViews();
        InitializeFileDrop();
        InitializeMotion();
        InitializeNavigation();
        CompositionTarget.Rendering += Startup_FirstRendering;
        SystemBackdrop = new MicaBackdrop();
        RootGrid.Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        Activated += ReviewReminder_WindowActivated;
        _watchFolderCoordinator.ImportCompleted += WatchFolderCoordinator_ImportCompleted;
        _opticalDeviceWatcher.Changed += OpticalDeviceWatcher_Changed;
        _systemPowerStateService.StateChanged += SystemPowerStateService_StateChanged;
        _systemPowerStateService.Transitioned += SystemPowerStateService_Transitioned;
        _accessibilityService.Changed += WindowsAccessibilityService_Changed;
        Tasks.CollectionChanged += (_, _) => ScheduleQueueRefresh();
        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(200);
        _progressTimer.IsRepeating = false;
        _progressTimer.Tick += FlushPendingProgress;
        SafeDiagnostics.ErrorReported += OnDiagnosticError;
    }

    public void HideBeforeActivation()
    {
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeWindowService.Hide(_windowHandle);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= MainWindow_Loaded;
        var startup = InitializeWindowAsync();
        try
        {
            await startup;
            // Keep modal UI outside startup: activation and shutdown must not wait for it.
            if (!_windowClosed) _ = InitializeReviewReminderAsync();
            if (!_windowClosed) InitializeStoreUpdates();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && _queueSession.IsStopping)
            {
                RefreshSettingsSaveState();
                return;
            }
            SafeDiagnostics.Report(ex);
            if (!_windowClosed) ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task TryPersistStartupSettingsAsync(Func<Task> persist)
    {
        // A failed optional startup write must not block queue restoration. Keep the
        // first error and avoid more automatic writes until the user can retry.
        if (_startupSettingsSaveFailure is not null) return;
        var failure = await StartupSettingsPersistence.TrySaveAsync(persist);
        if (failure is not null && _startupSettingsSaveFailure is null)
        {
            _startupSettingsSaveFailure = failure;
            SafeDiagnostics.Report(failure);
        }
    }

    private Task UpdateAutomaticSettingsAsync(Action<AppSettings> update) => _initializing
        ? TryPersistStartupSettingsAsync(() => SettingsCommits.UpdateAsync(_settings, update))
        : SettingsCommits.UpdateAsync(_settings, update);

    private void ShowStartupSettingsSaveWarning()
    {
        if (_windowClosed || _startupSettingsSaveFailure is null) return;
        var message = LocalizationService.T("ConversionUiSettingsSaveFailedDetails", _startupSettingsSaveFailure.Message);
        SettingsView.ViewModel.SaveStateKey = "ConversionUiSettingsSaveFailed";
        SettingsView.ViewModel.SaveMessage = message;
        RefreshSettingsSaveState();
        ShowSettingsInfo(message, InfoBarSeverity.Warning);
        var info = ConverterView.AppInfoBar;
        if (info.IsOpen && info.Severity == InfoBarSeverity.Error) return;
        // Preserve queue recovery/save warnings alongside the optional settings warning.
        if (info.IsOpen && info.Severity == InfoBarSeverity.Warning && !string.IsNullOrWhiteSpace(info.Message))
            message = info.Message + Environment.NewLine + message;
        ShowInfo(message, InfoBarSeverity.Warning);
    }

    private async Task RestoreQueueAsync()
    {
        try { await OutputRecoveryService.Shared.LoadIndexAsync(); }
        catch (Exception exception) { SafeDiagnostics.Report(exception); }
        await _queueSession.RestoreAsync();
        _ = PruneRecoveryInBackgroundAsync();
        if (_windowClosed) return;
        _startupMetrics.Mark("first-restored-task");
        _startupMetrics.Mark("all-restored");
        if (_queueSession.LoadResult is { Status: QueueLoadStatus.CorruptRecovered } recovered)
            ShowInfo(LocalizationService.T("ConversionUiQueueRecovered", recovered.QuarantinePath), InfoBarSeverity.Warning);
        else if (!_queueSession.CanSave)
            ShowInfo(LocalizationService.T("ConversionUiQueueSaveUnavailable"), InfoBarSeverity.Warning);
        else if (_queueSession.Tasks.Count > 0)
            ShowInfo(LocalizationService.T("TasksRestored", _queueSession.Tasks.Count), InfoBarSeverity.Informational);
    }

    private void QueueSession_TaskAdded(MediaTask model)
    {
        if (_windowClosed || (Tasks.FindById(model.Id) is not null)) return;
        _startupMetrics.Mark("first-restored-task");
        _knownMotionTasks.Add(AddImportedTask(model).Id);
    }

    private void QueueSession_TaskStateChanged(MediaTask model)
    {
        if (_windowClosed || Tasks.FindById(model.Id) is not { } task) return;
        UpdateTaskEngineAvailability(task);
        UpdateTaskbarProgress();
        UpdateTaskNotification(task);
    }

    private void QueueSession_RestoreCompleted(QueueLoadResult _)
    {
        if (_windowClosed) return;
        _startupMetrics.Mark("first-restored-task");
        _startupMetrics.Mark("all-restored");
    }

    private void QueueSession_SideEffectFailed(Exception exception)
    {
        if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
    }

    private void ApplySettingsToControls(bool initializeRuntime = false)
    {
        if (_windowClosed) return;
        if (initializeRuntime)
        {
            ApplyTelemetryPreferencesFromSettings();
            ApplyAutomaticUpdatesFromSettings();
            ConverterView.OutputModeCombo.SelectedIndex = _settings.OutputMode == "source" ? 1 : 0;
            ConverterView.OutputDirectoryText.Text = _settings.CustomOutputDirectory;
            ConverterView.OutputDirectoryButton.IsEnabled = _settings.OutputMode == "custom";
            ApplyTheme();
            ApplyExecutionResourcePolicy();
        }
        if (SettingsView.ViewModel.IsInitialized) SettingsView.ApplySettings(_settings);
    }

    private void NormalizeSettings()
    {
        _settings.OutputMode = _settings.OutputMode switch { "源文件目录" or "source" => "source", _ => "custom" };
        _settings.Theme = _settings.Theme switch { "浅色" or "light" => "light", "深色" or "dark" => "dark", _ => "system" };
        _settings.Language = LocalizationService.NormalizePreference(_settings.Language);
        _settings.EbookLayoutMode = _settings.EbookLayoutMode == "fixed" ? "fixed" : "reflow";
        _settings.OcrLanguage = string.IsNullOrWhiteSpace(_settings.OcrLanguage) ? "auto" : _settings.OcrLanguage.Trim();
    }

    private void ApplyLocalization()
    {
        if (_windowClosed) return;
        _updatingLocalization = true;
        try
        {
            RootGrid.Language = LocalizationService.CurrentUICulture.Name;
            RootGrid.FlowDirection = LocalizationService.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Title = LocalizationService.T("AppTitle");
            _trayIcon?.RefreshLocalization();
            NavAllItem.Content = LocalizationService.T("NavAll");
            NavVideoItem.Content = LocalizationService.T("NavVideo");
            NavAudioItem.Content = LocalizationService.T("NavAudio");
            NavImageItem.Content = LocalizationService.T("NavImage");
            NavDocumentItem.Content = LocalizationService.T("NavDocument");
            NavEbookItem.Content = LocalizationService.T("NavEbook");
            NavDiscItem.Content = LocalizationService.T("NavDisc");
            NavArchiveItem.Content = LocalizationService.T("ArchiveTitle");
            NavHistoryItem.Content = LocalizationService.T("NavHistory");
            NavSettingsItem.Content = LocalizationService.T("NavSettings");
            NavAutomationItem.Content = LocalizationService.T("AutomationTitle");
            AutomationView.ApplyLocalization();
            DropFeedbackText.Text = LocalizationService.T("ConversionUiDropReleaseHint");
            ConverterView.ApplyLocalization(_settings);
            if (_appInfoBarTitleResourceKey is { } titleKey)
                ConverterView.AppInfoBar.Title = LocalizationService.T(titleKey);
            RefreshQueueUndoLocalization();
            HistoryView.ApplyLocalization();
            if (SettingsView.ViewModel.IsInitialized) SettingsView.ApplyLocalization(_settings);
            foreach (var task in Tasks) task.RefreshLocalization();
            RefreshTaskEngineAvailability();
            RefreshVisibleTasks();
            UpdateQueueSummary();
            UpdateHardwareDecodeControls();
            if (SettingsView.ViewModel.DraftSession is not null) RefreshSettingsSaveState();
            UpdateTaskbarProgress();
            ArchiveView.ApplyLocalization();
            LocalizeDesktopExperience();
            RefreshNavigationLabels();
        }
        finally { _updatingLocalization = false; }
    }

    private async Task LoadHardwareInventoryAsync()
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.HardwareProbe)
        { Stage = RuntimeFailureStage.ProcessRun, Phase = PlatformDiagnosticPhase.HardwareInventory };
        using var measurement = _initializing ? _startupMetrics.MeasureStage("hardware-inventory") : null;
        _hardwareInventoryLoading = true;
        _hardwareInventoryError = null;
        UpdateHardwareDecodeControls();
        try
        {
            _hardwareInventory = await Task.Run(HardwareInventoryService.GetCurrent);
        }
        catch (Exception ex)
        {
            diagnostic.Fail(ex);
            SafeDiagnostics.Report(ex);
            _hardwareInventory = null;
            _hardwareInventoryError = ex.Message;
        }
        finally
        {
            _hardwareInventoryLoading = false;
            UpdateHardwareDecodeControls();
        }
    }

    private async Task LoadHardwareDecodeReportAsync()
    {
        using var measurement = _initializing ? _startupMetrics.MeasureStage("hardware-decode-cache") : null;
        if (_windowClosed) return;
        if (_hardwareInventory is not null && !string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.HardwareProbe)
            { Stage = RuntimeFailureStage.ProcessRun, Phase = PlatformDiagnosticPhase.HardwareCache };
            _hardwareDecodeReport = await diagnostic.ObserveAsync(() => _hardwareAccelerationTest.LoadCachedAsync(
                _ffmpegPath,
                _hardwareInventory));
        }
        else
        {
            _hardwareDecodeReport = null;
            HardwareAccelerationTestService.SetCurrentReport(null);
        }
        var testCompleted = _hardwareDecodeReport is not null;
        if (_settings.HardwareDecodeTestCompleted != testCompleted)
            await UpdateAutomaticSettingsAsync(settings => settings.HardwareDecodeTestCompleted = testCompleted);

        UpdateHardwareDecodeControls();
    }

    private async Task SetFfmpegContextAsync(
        string? ffmpegPath,
        string? ffprobePath,
        bool startHardwareTestIfNeeded)
    {
        if (_windowClosed) return;
        _hardwareDecodeContextVersion++;
        _ffmpegPath = ffmpegPath?.Trim() ?? string.Empty;
        _ffprobePath = ffprobePath?.Trim() ?? string.Empty;
        _hardwareDecodeReport = null;
        _hardwareDecodeTestError = null;
        _hardwareDecodeTestRunning = false;
        _hardwareDecodeNeedsFfmpeg = string.IsNullOrWhiteSpace(_ffmpegPath);
        HardwareAccelerationTestService.SetCurrentReport(null);

        if (_hardwareDecodeNeedsFfmpeg || _hardwareInventory is null)
        {
            if (_settings.HardwareDecodeTestCompleted)
            {
                await UpdateAutomaticSettingsAsync(settings => settings.HardwareDecodeTestCompleted = false);
            }

            UpdateHardwareDecodeControls();
            return;
        }

        await LoadHardwareDecodeReportAsync();
        if (startHardwareTestIfNeeded && _hardwareDecodeReport is null)
        {
            _ = RunHardwareDecodeTestAsync(showInfo: false);
        }
    }

    private async void HardwareDecodeTestButton_Click(object sender, RoutedEventArgs e) => await RunHardwareDecodeTestAsync(showInfo: true);

    private async Task RunHardwareDecodeTestAsync(bool showInfo)
    {
        using var measurement = _initializing ? _startupMetrics.MeasureStage("hardware-decode-test") : null;
        if (_windowClosed) return;
        if (string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            _hardwareDecodeNeedsFfmpeg = true;
            _hardwareDecodeTestError = null;
            UpdateHardwareDecodeControls();
            return;
        }

        var inventory = _hardwareInventory;
        if (inventory is null)
        {
            _hardwareDecodeTestError = LocalizationService.T(
                "HardwareSettingsInventoryFailed",
                _hardwareInventoryError ?? string.Empty);
            UpdateHardwareDecodeControls();
            return;
        }

        var contextVersion = _hardwareDecodeContextVersion;
        var ffmpegPath = _ffmpegPath;

        _hardwareDecodeNeedsFfmpeg = false;
        _hardwareDecodeTestError = null;
        _hardwareDecodeTestRunning = true;
        UpdateHardwareDecodeControls();
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.HardwareProbe)
        { Stage = RuntimeFailureStage.ProcessRun, Phase = PlatformDiagnosticPhase.HardwareDecode, Concurrent = 1 };
        try
        {
            var report = await Task.Run(() => _hardwareAccelerationTest.RunAsync(ffmpegPath, inventory));
            if (contextVersion != _hardwareDecodeContextVersion)
            {
                HardwareAccelerationTestService.SetCurrentReport(_hardwareDecodeReport);
                return;
            }

            _hardwareDecodeReport = report;
            await _hardwareAccelerationTest.SaveAsync(report);
            if (contextVersion != _hardwareDecodeContextVersion)
            {
                HardwareAccelerationTestService.SetCurrentReport(_hardwareDecodeReport);
                return;
            }

            await SettingsCommits.UpdateAsync(_settings, settings => settings.HardwareDecodeTestCompleted = true);
            if (showInfo)
            {
                ShowSettingsInfo(LocalizationService.T("Saved"), InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            diagnostic.Fail(ex);
            SafeDiagnostics.Report(ex);
            if (contextVersion == _hardwareDecodeContextVersion)
            {
                _hardwareDecodeTestError = ex.Message;
                if (showInfo)
                {
                    ShowSettingsInfo(
                        LocalizationService.T("HardwareSettingsTestFailed", ex.Message),
                        InfoBarSeverity.Error);
                }
            }
        }
        finally
        {
            if (contextVersion == _hardwareDecodeContextVersion)
            {
                _hardwareDecodeTestRunning = false;
                UpdateHardwareDecodeControls();
            }
        }
    }

    private void UpdateHardwareDecodeControls()
    {
        if (_windowClosed) return;
        SettingsView.RenderHardware(new HardwareSettingsState(_hardwareInventory, _hardwareDecodeReport,
            _hardwareInventoryLoading, _hardwareInventoryError, _hardwareDecodeTestRunning,
            _hardwareDecodeNeedsFfmpeg, _hardwareDecodeTestError));
    }

    private void ApplyTheme()
    {
        if (_windowClosed) return;
        RootGrid.RequestedTheme = _settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private async Task DetectFfmpegAsync(bool showSuccess)
    {
        if (_windowClosed) return;
        var initialDetection = _initializing;
        var requestedPath = initialDetection || !SettingsView.ViewModel.IsInitialized ? _settings.FfmpegPath : SettingsView.FfmpegPathTextBox.Text;
        (string Ffmpeg, string Ffprobe)? result;
        using (_initializing ? _startupMetrics.MeasureStage("ffmpeg-pair-validation") : null)
            result = await _ffmpegLocator.LocateAsync(requestedPath);
        if (_windowClosed) return;
        if (result is null)
        {
            if (initialDetection)
                await SetFfmpegContextAsync(string.Empty, string.Empty, startHardwareTestIfNeeded: false);
            RefreshTaskEngineAvailability();
            ShowInfo(LocalizationService.T("FfmpegConfigureFailed"), InfoBarSeverity.Error);
            ShowSettingsInfo(LocalizationService.T("FfmpegConfigureFailed"), InfoBarSeverity.Error);
            return;
        }

        var detectedPath = result.Value.Ffmpeg;
        if (initialDetection)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
                await UpdateAutomaticSettingsAsync(settings => settings.FfmpegPath = PersistedEnginePath(detectedPath));
            AcceptInitializedSettings();
            await SetFfmpegContextAsync(detectedPath, result.Value.Ffprobe, startHardwareTestIfNeeded: true);
        }
        if (SettingsView.ViewModel.IsInitialized && !_windowClosed && SettingsView.FfmpegPathTextBox.Text == requestedPath
            && (!initialDetection || string.IsNullOrWhiteSpace(requestedPath)))
            SettingsView.FfmpegPathTextBox.Text = PersistedEnginePath(detectedPath);
        RefreshTaskEngineAvailability();
        if (showSuccess)
        {
            ShowSettingsInfo(LocalizationService.T("FfmpegAvailable"), InfoBarSeverity.Success);
        }
    }

    internal Task ActivationReadiness => _activationReady.Task;

    internal async Task DispatchActivationAsync(ActivationRequest activation)
    {
        activation.Delivery?.Write(ActivationDeliveryStage.WaitingForWindow, primary: true, routedWork: activation.HasWork);
        await _activationReady.Task;
        activation.Delivery?.Write(ActivationDeliveryStage.WindowReady, primary: true, routedWork: activation.HasWork);
        if (DispatcherQueue.HasThreadAccess)
        {
            await HandleActivationWithReviewPriorityAsync(activation);
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await HandleActivationWithReviewPriorityAsync(activation);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            throw new InvalidOperationException("The window dispatcher is unavailable.");
        }

        await completion.Task;
    }

    private async Task HandleActivationAsync(ActivationRequest activation)
    {
        NativeWindowService.Show(_windowHandle);
        if (activation.OpenSettings)
        {
            NavView.SelectedItem = NavSettingsItem;
            await _navigationTransition;
        }
        else if (activation.NavigationTarget is { } target)
        {
            var item = NavView.MenuItems.Concat(NavView.FooterMenuItems)
                .OfType<NavigationViewItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, target, StringComparison.Ordinal));
            if (item is not null)
            {
                NavView.SelectedItem = item;
                await _navigationTransition;
            }
        }

        if (!string.IsNullOrWhiteSpace(activation.OpenPath))
        {
            OpenPathInExplorer(activation.OpenPath);
        }

        if (activation.NotificationAction == NotificationAction.RetryFailed)
        {
            RetryFailedTasks();
        }
        else if (activation.TaskId is Guid taskId)
        {
            var task = Tasks.FindById(taskId);
            if (task is not null)
            {
                switch (activation.NotificationAction)
                {
                    case NotificationAction.Pause:
                        PauseTask(task);
                        break;
                    case NotificationAction.Resume:
                        ResumeTask(task);
                        break;
                    case NotificationAction.Cancel:
                        CancelTask(task);
                        break;
                    case NotificationAction.Retry:
                        if ((task.State is ConversionState.Failed or ConversionState.Cancelled) && EnsureTaskEngineReady(task))
                        {
                            ResetTask(task);
                            StartTask(task);
                        }
                        break;
                    case NotificationAction.OpenOutput when string.IsNullOrWhiteSpace(activation.OpenPath):
                        if (PathExists(task.OutputPath))
                        {
                            OpenPathInExplorer(task.OutputPath);
                        }
                        break;
                }
            }
        }

        if (activation.FilePaths.Count == 0)
        {
            return;
        }

        NavView.SelectedItem = NavAllItem;
        var existingTaskIds = Tasks.Select(task => task.Id).ToHashSet();
        await AddIncomingPathsAsync(activation.FilePaths, null);
        ApplyActivationTarget(activation, existingTaskIds);
    }

    private void ApplyActivationTarget(ActivationRequest activation, IReadOnlySet<Guid> existingTaskIds)
    {
        if (!string.IsNullOrWhiteSpace(activation.TargetFormatId) &&
            FormatCatalog.FindById(activation.TargetFormatId) is { CanExport: true } target)
        {
            foreach (var task in Tasks.Where(task => !existingTaskIds.Contains(task.Id)))
            {
                if (!task.AvailableFormats.Contains(target.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    // Keep an explicit legacy EPUB request visible so the route
                    // guard can explain it without silently choosing another output.
                    if (task.Model.SourceFormatId != "epub" || target.Id != "epub") continue;
                    task.AvailableFormats.Add(target.DisplayName);
                }
                task.TargetFormat = target.DisplayName;
                UpdateTaskEngineAvailability(task);
            }
        }
    }

    private void RetryFailedTasks()
    {
        _queueOperations.RetryFailed(model => model.State == ConversionState.Failed && IsQueueTaskReady(model), StartQueueModel);
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        try { await PickAndAddFilesAsync(); }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task PickAndAddFilesAsync()
    {
        var diagnostic = BeginPickerDiagnostic(PlatformDiagnosticKind.FilePicker);
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            foreach (var extension in FormatCatalog.SupportedExtensions.OrderBy(value => value))
                picker.FileTypeFilter.Add(extension);
            diagnostic.Stage = RuntimeFailureStage.PickerInitialize;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            diagnostic.Stage = RuntimeFailureStage.PickerShow;
            var files = await picker.PickMultipleFilesAsync();
            diagnostic.ResultCount = files.Count;
            diagnostic.Stage = RuntimeFailureStage.PickerImport;
            await AddIncomingPathsAsync(files.Select(file => file.Path), null);
            diagnostic.Complete(cancelled: files.Count == 0);
        }
        catch (OperationCanceledException) { diagnostic.Complete(cancelled: true); throw; }
        catch (Exception exception)
        {
            diagnostic.WindowOpen = !_windowClosed;
            CaptureDiagnosticUiSnapshot(); diagnostic.Fail(exception); throw;
        }
        finally { Interlocked.Decrement(ref _diagnosticPickerCount); }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try { await PickAndAddFolderAsync(); }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task PickAndAddFolderAsync()
    {
        var diagnostic = BeginPickerDiagnostic(PlatformDiagnosticKind.FolderPicker);
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            picker.FileTypeFilter.Add("*");
            diagnostic.Stage = RuntimeFailureStage.PickerInitialize;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            diagnostic.Stage = RuntimeFailureStage.PickerShow;
            var folder = await picker.PickSingleFolderAsync();
            diagnostic.ResultCount = folder is null ? 0 : 1;
            if (folder is not null)
            {
                diagnostic.Stage = RuntimeFailureStage.PickerImport;
                var accessToken = await StorageAccessService.RememberFolderAsync(folder.Path);
                await AddFolderAsync(folder.Path, accessToken);
            }
            diagnostic.Complete(cancelled: folder is null);
        }
        catch (OperationCanceledException) { diagnostic.Complete(cancelled: true); throw; }
        catch (Exception exception)
        {
            diagnostic.WindowOpen = !_windowClosed;
            CaptureDiagnosticUiSnapshot(); diagnostic.Fail(exception); throw;
        }
        finally { Interlocked.Decrement(ref _diagnosticPickerCount); }
    }

    private async void PasteFilesButton_Click(object sender, RoutedEventArgs e) => await PasteFilesAsync();

    private async Task PasteFilesAsync()
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.Clipboard)
        { Stage = RuntimeFailureStage.PickerShow, Phase = PlatformDiagnosticPhase.ClipboardRead, WindowOpen = !_windowClosed, HandlePresent = _windowHandle != IntPtr.Zero };
        try
        {
            var paths = await ClipboardService.ReadSupportedFilePathsAsync();
            diagnostic.ResultCount = paths.Count;
            if (paths.Count == 0)
            {
                diagnostic.Complete();
                ShowInfo(LocalizationService.T("ClipboardEmpty"), InfoBarSeverity.Warning);
                return;
            }
            diagnostic.Stage = RuntimeFailureStage.PickerImport;
            diagnostic.Phase = PlatformDiagnosticPhase.ClipboardImport;
            await AddIncomingPathsAsync(paths, null);
            diagnostic.Complete();
        }
        catch (Exception ex)
        {
            CaptureDiagnosticUiSnapshot();
            diagnostic.Fail(ex);
            SafeDiagnostics.Report(ex);
            ShowInfo(LocalizationService.T("ClipboardReadFailed", ex.Message), InfoBarSeverity.Error);
        }
    }
    private void WatchFolderCoordinator_ImportCompleted(object? sender, MediaImportBatchResult result)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var imported = ApplyImportResult(result, showSummary: true);
            foreach (var task in imported.Where(task => task.State == ConversionState.Waiting))
            {
                StartTask(task);
            }
        });
    }

    private async void AddOpticalDiscButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var discs = (await _opticalDiscDiscovery.DiscoverAsync())
                .Where(disc => disc.IsReady && disc.MediaKind != OpticalDiscMediaKind.Unknown)
                .ToArray();
            if (discs.Length == 0)
            {
                ShowInfo(LocalizationService.T("OpticalNoDrive"), InfoBarSeverity.Warning);
                return;
            }

            OpticalDiscInfo? selected = discs[0];
            if (discs.Length > 1)
            {
                var selector = new ComboBox
                {
                    Header = LocalizationService.T("OpticalSelectLabel"),
                    ItemsSource = discs,
                    DisplayMemberPath = nameof(OpticalDiscInfo.DisplayName),
                    SelectedIndex = 0,
                    MinWidth = 360
                };
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = LocalizationService.T("OpticalSelectTitle"),
                    Content = selector,
                    PrimaryButtonText = LocalizationService.T("AddOpticalDisc"),
                    CloseButtonText = LocalizationService.T("MediaInfoClose"),
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await ShowContentDialogAsync(dialog) != ContentDialogResult.Primary)
                {
                    return;
                }
                selected = selector.SelectedItem as OpticalDiscInfo;
            }

            if (selected is null)
            {
                return;
            }
            var result = await _mediaImportCoordinator.ImportOpticalDiscAsync(selected);
            ApplyImportResult(new MediaImportBatchResult(new[] { result }), showSummary: true);
            NavView.SelectedItem = NavDiscItem;
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Report(ex);
            ShowInfo(
                LocalizationService.T("OpticalImportFailed", ex.Message),
                InfoBarSeverity.Error);
        }
    }

    private void ConfigureWatchFolder()
    {
        _watchFolderCoordinator.Configure(_settings, () => _ffprobePath,
            path => !_settingsResetInProgress && !AutomationRuleMatcher.IsExcludedByDestinations(path, _automationRules) &&
                AutomationRuleMatcher.FirstMatch(_automationRules, path) is null);
    }
    private IReadOnlyList<MediaTaskViewModel> ApplyImportResult(MediaImportBatchResult result, bool showSummary)
    {
        if (_windowClosed || _queueSession.IsStopping)
        {
            foreach (var model in result.AddedTasks) _mediaImportCoordinator.Forget(model);
            return Array.Empty<MediaTaskViewModel>();
        }
        var imported = new List<MediaTaskViewModel>();
        using var groupRefresh = ConverterView.DeferOutputGroupRefresh();
        var acceptedIds = _queueSession.AddTasks(result.AddedTasks).Select(task => task.Id).ToHashSet();
        foreach (var model in result.AddedTasks)
        {
            if (acceptedIds.Contains(model.Id) && Tasks.FindById(model.Id) is { } accepted) imported.Add(accepted);
            else _mediaImportCoordinator.Forget(model);
        }

        foreach (var failure in result.Failures)
        {
            SafeDiagnostics.Write(LocalizationService.T(
                "OperationalLogMediaAnalysisFailed",
                failure.RequestedPath,
                failure.Error!.Message));
            ShowInfo(
                LocalizationService.T("FileReadFailed", Path.GetFileName(failure.RequestedPath), failure.Error.Message),
                InfoBarSeverity.Error);
        }

        if (showSummary)
        {
            var ignored = result.IgnoredCount;
            ShowInfo(
                LocalizationService.T(
                    "FilesAdded",
                    imported.Count,
                    ignored > 0 ? LocalizationService.T("IgnoredFiles", ignored) : string.Empty),
                InfoBarSeverity.Success);
        }

        return imported;
    }

    private MediaTaskViewModel AddImportedTask(MediaTask model)
    {
        if (model.State is ConversionState.Failed or ConversionState.Blocked)
        {
            try
            {
                if (OutputRecoveryService.Shared.GetCached(model.Id) is { } recovery)
                {
                    model.RecoverableOutputId = recovery.Id;
                    model.PreservedOutputPath = recovery.ArtifactPath;
                }
            }
            catch (Exception exception) { SafeDiagnostics.Report(exception); }
        }
        if (model.State == ConversionState.Waiting && string.IsNullOrWhiteSpace(model.Message))
            model.Message = model.Probe.IsHdr ? LocalizationService.T("HdrAuto") : LocalizationService.T("WaitingStart");
        var viewModel = _queuePage.AddTask(model);
        UpdateTaskEngineAvailability(viewModel);
        viewModel.PropertyChanged += Task_PropertyChanged;
        return viewModel;
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MediaTaskViewModel outputTask && e.PropertyName is nameof(MediaTaskViewModel.State) or nameof(MediaTaskViewModel.OutputPath))
        {
            _queuePage.InvalidateOutputAvailability(outputTask.Id);
            UpdateTaskbarProgress();
        }
        if (e.PropertyName == nameof(MediaTaskViewModel.State))
        {
            ScheduleQueueRefresh(save: false);
        }
        if (sender is MediaTaskViewModel task && e.PropertyName is
            nameof(MediaTaskViewModel.TargetFormat) or nameof(MediaTaskViewModel.ConversionMode) or
            nameof(MediaTaskViewModel.UseCompatibleAudioTrack) or nameof(MediaTaskViewModel.Probe))
        {
            UpdateTaskEngineAvailability(task);
        }
        if (!_initializing && e.PropertyName is nameof(MediaTaskViewModel.TargetFormat) or
            nameof(MediaTaskViewModel.ConversionMode) or nameof(MediaTaskViewModel.SelectedAudioTrack) or
            nameof(MediaTaskViewModel.UseCompatibleAudioTrack))
        {
            ScheduleQueueRefresh();
        }
    }

    private void PdfPasswordBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            SynchronizePdfPasswordEditor(passwordBox, passwordBox.DataContext as MediaTaskViewModel);
    }

    private void PdfPasswordBox_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        // Tag bindings can still identify the previous row while the data context changes.
        if (sender is PasswordBox passwordBox)
            SynchronizePdfPasswordEditor(passwordBox, args.NewValue as MediaTaskViewModel);
    }

    private void SynchronizePdfPasswordEditor(PasswordBox passwordBox, MediaTaskViewModel? task)
    {
        var state = _pdfPasswordEditors.GetValue(passwordBox, static _ => new());
        state.TaskId = task?.Id;
        state.Synchronizing = true;
        try
        {
            passwordBox.Password = task is not null && _pdfPasswords.TryGetValue(task.Id, out var password)
                ? password : string.Empty;
        }
        finally { state.Synchronizing = false; }
    }

    private void PdfPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox || passwordBox.DataContext is not MediaTaskViewModel task ||
            !_pdfPasswordEditors.TryGetValue(passwordBox, out var state) || state.Synchronizing || state.TaskId != task.Id)
        {
            return;
        }

        if (string.IsNullOrEmpty(passwordBox.Password))
        {
            _pdfPasswords.Remove(task.Id);
        }
        else
        {
            _pdfPasswords[task.Id] = passwordBox.Password;
        }
    }

    private sealed class PdfPasswordEditorState
    {
        public Guid? TaskId { get; set; }
        public bool Synchronizing { get; set; }
    }

    private void StartTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null || _outputRecoveryInProgress.Contains(task.Id))
        {
            return;
        }
        _queueOperations.StartTask(task.Model, IsQueueTaskReady, StartQueueModel);
    }

    private void StartAllButton_Click(object sender, RoutedEventArgs e)
    {
        _queueOperations.StartWaiting(IsQueueTaskReady, StartQueueModel);
    }

    private bool IsQueueTaskReady(MediaTask model) => Tasks.FindById(model.Id) is { } row && EnsureTaskEngineReady(row);
    private void StartQueueModel(MediaTask model) { if (Tasks.FindById(model.Id) is { } row) StartTask(row); }

    private void StartTask(MediaTaskViewModel task)
    {
        if (_settingsResetInProgress || _queueSession.IsStopping || _queueCoordinator.IsActive(task.Id) || _outputRecoveryInProgress.Contains(task.Id)) return;
        var generation = _queuePage.BeginProgress(task);
        ApplyExecutionResourcePolicy();
        var progress = new InlineProgress<ConversionExecutionProgress>(value =>
            ReportTaskProgress(task, generation, value.Percent, value.Speed ?? "—", value.Remaining ?? "—", value.Message));
        _queuePage.TrackBatchTask(task.Model);
        if (!_queueSession.TryStart(task.Model, progress, HandleTaskOperationCompletedAsync, NotifyBatchFinishedAsync))
        {
            _queuePage.ForgetBatchTask(task.Id);
            return;
        }
        UpdateTaskbarProgress();
        UpdateQueueSummary();
    }

    private async Task<ConversionWorkflowResult> ExecuteConversionAsync(MediaTask task,
        IProgress<ConversionExecutionProgress>? progress, CancellationToken cancellationToken)
    {
        ConversionRequest? request = null;
        ConversionWorkflow? workflow = null;
        await RunOnUiAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = _settings.Snapshot();
            ApplyAutomationOutputSettings(task, settings);
            HistoryReplayService.ApplyReplaySettings(task, settings);
            settings.ParallelCount = PerformanceTuning.ResolveParallelCount(settings, _systemPowerStateService.Current);
            _pdfPasswords.TryGetValue(task.Id, out var password);
            HistoryReplayService.CaptureExecutionSettings(task, settings);
            request = ConversionRequestFactory.Capture(task, settings, password);
            workflow = _services.Conversions.CreateConversionWorkflow(settings, _ffmpegPath);
            return Task.CompletedTask;
        });
        if (request is null || workflow is null) throw new OperationCanceledException(cancellationToken);
        if (task.OutputGroupId is not null) await _queueSession.SaveAsync(cancellationToken);
        using var conversionTrace = Volatile.Read(ref _telemetry)?.StartConversion(task.Kind, request.TargetFormatId);
        var result = await workflow.ExecuteAsync(request, progress, cancellationToken);
        if (result.Statistics is { } statistics)
            conversionTrace?.Finish(ToTelemetryConversionResult(result.Outcome), statistics.ElapsedMilliseconds);
        if (result.Execution.Retryable && result.Outcome == ConversionOutcome.Failed && task.Options.ContainsKey(AutomationRuleOption))
            _automationTransientFailures[task.Id] = 0;
        if (result.Outcome == ConversionOutcome.Failed && !string.IsNullOrWhiteSpace(result.Execution.LogPath))
            _feedbackTaskLogs[task.Id] = result.Execution.LogPath;
        return result;
    }
    private void ReportTaskProgress(MediaTaskViewModel task, long generation, double percent, string speed, string remaining, string message)
    {
        if (_windowClosed || !_queuePage.PublishProgress(task, generation, percent, speed, remaining, message)) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (!_windowClosed) _progressTimer?.Start();
        })) _queuePage.DiscardPendingProgress();
    }

    private Task HandleTaskOperationCompletedAsync(ConversionQueueOperationCompleted completion) =>
        RunOnUiAsync(async () =>
        {
            var task = Tasks.FindById(completion.TaskId);
            if (task is not null)
                _telemetry?.TrackConversionResult(ToTelemetryConversionResult(completion.Outcome),
                    task.Model.Kind, task.Model.TargetFormatId);
            _feedbackTaskLogs.TryRemove(completion.TaskId, out var errorLogPath);
            if (completion.Outcome == ConversionOutcome.Failed || completion.Error is not null)
                RecordFeedbackError(completion.Error?.ToString() ?? task?.Model.LastError ?? "Conversion failed", errorLogPath,
                    completion.Error, completion.ErrorCorrelation ?? new(ErrorCorrelationKind.ConversionTask, null, completion.TaskId));
            await HandleAutomationCompletionAsync(completion, task?.Model);
            if (task?.Model.OutputGroupId is { } outputGroupId)
                await _queueSession.TryFinalizeManualOutputGroupAsync(outputGroupId);
            if (task is not null && completion.RemoveRequested)
            {
                RemoveTask(task);
                await SaveQueueAsync();
            }
            UpdateTaskbarProgress();
        });
    private void TaskbarProgress_ButtonClicked(object? sender, int buttonId)
    {
        if (buttonId == TaskbarProgressService.PauseButtonId)
        {
            PauseAllTasks();
        }
        else if (buttonId == TaskbarProgressService.CancelButtonId)
        {
            CancelAllButton_Click(this, new RoutedEventArgs());
        }
        else if (buttonId == TaskbarProgressService.OpenOutputButtonId)
        {
            var output = _queuePage.FindLastCachedOutput();
            if (!string.IsNullOrWhiteSpace(output))
            {
                OpenPathInExplorer(output);
            }
        }
    }

    private void UpdateTaskbarProgress()
    {
        if (_windowClosed) return;
        var state = _queuePage.CreateTaskbarState();
        _taskbarProgress?.Update(state.Activities, state.HasOutput, state.StopCount);
        ScheduleTaskbarOutputAvailabilityCheck();
    }

    private void UpdateTaskNotification(MediaTaskViewModel task)
    {
        if (!_settings.Notifications || task.State == ConversionState.Waiting)
        {
            return;
        }

        var state = task.State switch
        {
            ConversionState.Paused => TaskNotificationState.Paused,
            ConversionState.Completed => TaskNotificationState.Completed,
            ConversionState.Failed => TaskNotificationState.Failed,
            ConversionState.Cancelled => TaskNotificationState.Cancelled,
            _ => TaskNotificationState.Running
        };
        _ = AppNotificationService.UpdateTaskAsync(
            task.Id,
            task.FileName,
            task.Message,
            task.Progress / 100d,
            state,
            task.OutputPath);
    }
    private Task NotifyBatchFinishedAsync()
    {
        return RunOnUiAsync(async () =>
        {
            UpdateQueueSummary();
            var batch = _queuePage.CompleteBatch(PathExists);
            var outputPath = batch.OutputPath;
            var failed = batch.FailedCount;
            var message = batch.Message;
            if (string.IsNullOrEmpty(message)) return;
            ShowInfo(message, failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            _appInfoBarTitleResourceKey = "ConversionComplete";
            ConverterView.AppInfoBar.Title = LocalizationService.T(_appInfoBarTitleResourceKey);
            await JumpListService.RefreshAsync();
            if (_settings.Notifications)
            {
                if (!AppNotificationService.Show(
                        LocalizationService.T("ConversionComplete"),
                        message,
                        outputPath,
                        retryFailedAvailable: failed > 0))
                {
                    _trayIcon?.ShowBalloon(LocalizationService.T("ConversionComplete"), message);
                }
            }
            if (_settings.NotificationSound)
            {
                NativeWindowService.PlayNotificationSound();
            }
        });
    }

    private void PauseTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null)
        {
            return;
        }
        _queueOperations.ToggleTaskPause(task.Model,
            model => { if (Tasks.FindById(model.Id) is { } row) PauseTask(row); },
            model => { if (Tasks.FindById(model.Id) is { } row) ResumeTask(row); });
    }

    private void PauseAllButton_Click(object sender, RoutedEventArgs e) => PauseAllTasks();

    private void PauseAllTasks()
    {
        _queueOperations.TogglePause(
            model => { if (Tasks.FindById(model.Id) is { } row) PauseTask(row); },
            model => { if (Tasks.FindById(model.Id) is { } row) ResumeTask(row); });
    }

    private void PauseTask(MediaTaskViewModel task)
    {
        _queueSession.Pause(task.Model, () => _conversionService.Pause(task.Id));
        UpdateTaskNotification(task);
    }

    private void ResumeTask(MediaTaskViewModel task)
    {
        if (!EnsureTaskEngineReady(task)) return;
        if (_queueCoordinator.IsActive(task.Id))
            _queueSession.Resume(task.Model, () => _conversionService.Resume(task.Id));
        else StartTask(task);
        UpdateTaskNotification(task);
    }
    private async void CancelOrRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null)
        {
            return;
        }

        switch (_queueOperations.ResolveRemoval(task.Model))
        {
            case QueueRemovalAction.RemoveWithUndo:
                await RemoveTasksWithUndoAsync(new[] { task });
                ScheduleQueueRefresh();
                break;
            case QueueRemovalAction.RemoveAfterOperation: RequestRemovalAfterOperation(task); break;
            case QueueRemovalAction.Cancel: CancelTask(task); break;
        }
    }

    private async void ClearPendingButton_Click(object sender, RoutedEventArgs e)
    {
        var pending = _queueOperations.SelectPending().Select(model => Tasks.FindById(model.Id)).OfType<MediaTaskViewModel>().ToArray();
        foreach (var task in pending.Where(task => _queueOperations.ResolveRemoval(task.Model) == QueueRemovalAction.RemoveAfterOperation))
            RequestRemovalAfterOperation(task);
        await RemoveTasksWithUndoAsync(pending);

        UpdateQueueSummary();
        ScheduleQueueRefresh();
    }

    private void CancelAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var model in _queueOperations.SelectToStop())
        {
            if (Tasks.FindById(model.Id) is { } task)
            {
                CancelTask(task);
            }
        }

        UpdateQueueSummary();
        ScheduleQueueRefresh();
    }

    private void RequestRemovalAfterOperation(MediaTaskViewModel task)
    {
        if (_queueCoordinator.RequestRemoval(task.Id))
        {
            _conversionService.Cancel(task.Id);
            task.Message = LocalizationService.T("Cancelling");
        }
    }

    private void CancelTask(MediaTaskViewModel task)
    {
        if (_closeSaveInProgress || _queueSession.IsStopping || task.Model.IsRecoveringOutput) return;
        var wasActive = _queueCoordinator.IsActive(task.Id);
        _queueSession.Cancel(task.Model);
        _conversionService.Cancel(task.Id);
        if (wasActive)
        {
            task.Message = LocalizationService.T("Cancelling");
        }
        else
        {
            task.State = ConversionState.Cancelled;
            task.Message = LocalizationService.T("Cancelled");
        }
        UpdateTaskNotification(task);
        if (!wasActive) RememberAutomationCancellation(task.Model);
    }

    private void RemoveTask(MediaTaskViewModel task)
    {
        if (_closeSaveInProgress || _queueSession.IsStopping || task.Model.IsRecoveringOutput) return;
        if (task.State != ConversionState.Completed) RememberAutomationCancellation(task.Model);
        _queuePage.ForgetProgress(task.Id);
        _pdfPasswords.Remove(task.Id);
        _ = AppNotificationService.RemoveTaskAsync(task.Id);
        task.PropertyChanged -= Task_PropertyChanged;
        _mediaImportCoordinator.Forget(task.Model);
        _queueSession.RemoveTask(task.Model);
        _queuePage.RemoveTask(task);
        UpdateTaskbarProgress();
    }

    private void ResetTask(MediaTaskViewModel task)
    {
        _queueOperations.ResetForRetry(task.Model);
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null || string.IsNullOrWhiteSpace(task.OutputPath) || !PathExists(task.OutputPath))
        {
            ShowInfo(LocalizationService.T("NoOutput"), InfoBarSeverity.Warning);
            return;
        }

        OpenPathInExplorer(task.OutputPath);
    }

    private void ShareOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null || string.IsNullOrWhiteSpace(task.OutputPath) || !File.Exists(task.OutputPath))
        {
            ShowInfo(LocalizationService.T("NoOutput"), InfoBarSeverity.Warning);
            return;
        }
        _shareService?.ShareFiles(new[] { task.OutputPath });
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private void OpenPathInExplorer(string path)
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.Explorer)
        { Stage = RuntimeFailureStage.ShellOpen, WindowOpen = !_windowClosed, HandlePresent = _windowHandle != IntPtr.Zero };
        try
        {
            if (File.Exists(path))
            {
                diagnostic.Phase = PlatformDiagnosticPhase.ExplorerSelect;
                var selectFile = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                selectFile.ArgumentList.Add("/select,");
                selectFile.ArgumentList.Add(path);
                _services.Shell.StartExplorer(selectFile);
            }
            else if (Directory.Exists(path))
            {
                diagnostic.Phase = PlatformDiagnosticPhase.ExplorerDirectory;
                _services.Shell.StartExplorer(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
            diagnostic.Complete();
        }
        catch (Exception exception)
        {
            CaptureDiagnosticUiSnapshot();
            diagnostic.Fail(exception);
            SafeDiagnostics.Report(exception);
            var warning = LocalizationService.T("DesktopOpenLocationFailed");
            if (_navigationTag == "history") HistoryView.ShowInfo(warning, InfoBarSeverity.Warning);
            else if (_navigationTag == "settings") ShowSettingsInfo(warning, InfoBarSeverity.Warning);
            else ShowInfo(warning, InfoBarSeverity.Warning);
        }
        finally
        {
            if (!_windowClosed)
            {
                _queuePage.InvalidateOutputAvailability(path);
                UpdateTaskbarProgress();
            }
        }
    }

    private async void MediaInfoButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"{LocalizationService.T("MediaInfoTitle")}: {task.FileName}",
            Content = await BuildNativeMediaInfoContentAsync(task),
            CloseButtonText = LocalizationService.T("MediaInfoClose")
        };

        if (RootGrid.XamlRoot is null)
        {
            return;
        }

        dialog.XamlRoot = RootGrid.XamlRoot;
        await ShowContentDialogAsync(dialog);
    }

    private static ScrollViewer BuildMediaInfoContent(MediaTaskViewModel task)
    {
        var probe = task.Probe;
        if (task.Kind == MediaKind.Disc)
        {
            var discLines = new List<string>
            {
                $"{LocalizationService.T("MediaInfoFile")}: {task.InputPath}",
                $"{LocalizationService.T("MediaInfoType")}: {task.KindText}",
                $"{LocalizationService.T("MediaInfoFormat")}: {task.MediaInfo}"
            };
            if (task.Model.ContentProbe.Metadata.TryGetValue("volumeLabel", out var volumeLabel))
            {
                discLines.Add($"{LocalizationService.T("DiscVolumeLabel")}: {volumeLabel}");
            }
            if (task.Model.ContentProbe.Metadata.TryGetValue("devicePath", out var devicePath))
            {
                discLines.Add($"{LocalizationService.T("DiscDevicePath")}: {devicePath}");
            }
            return BuildInformationScroller(discLines);
        }

        var lines = new List<string>
        {
            $"{LocalizationService.T("MediaInfoFile")}: {task.InputPath}",
            $"{LocalizationService.T("MediaInfoType")}: {task.KindText}",
            $"{LocalizationService.T("MediaInfoFormat")}: {DisplayValue(probe.FormatName)}",
            $"{LocalizationService.T("MediaInfoDuration")}: {FormatMediaDuration(probe.Duration)}",
            $"{LocalizationService.T("MediaInfoBitrate")}: {FormatMediaBitrate(probe.Bitrate)}"
        };

        if (probe.Width > 0 && probe.Height > 0)
        {
            lines.Add($"{LocalizationService.T("MediaInfoResolution")}: {probe.Width}×{probe.Height}");
        }

        if (task.Kind == MediaKind.Video && !string.IsNullOrWhiteSpace(probe.VideoCodec))
        {
            lines.Add($"{LocalizationService.T("MediaInfoVideoCodec")}: {probe.VideoCodec.ToUpperInvariant()}");
        }

        if (!string.IsNullOrWhiteSpace(probe.PixelFormat))
        {
            lines.Add($"{LocalizationService.T("MediaInfoPixelFormat")}: {probe.PixelFormat}");
        }

        var colorValues = new[] { probe.ColorTransfer, probe.ColorPrimaries, probe.ColorSpace, probe.ColorRange }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        lines.Add($"{LocalizationService.T("MediaInfoColor")}: {DisplayValue(string.Join(" · ", colorValues))}");

        if (task.Kind == MediaKind.Video)
        {
            lines.Add($"{LocalizationService.T("MediaInfoHdr")}: {LocalizationService.T(probe.IsHdr ? "MediaInfoYes" : "MediaInfoNo")}");
        }

        if (task.Kind == MediaKind.Image)
        {
            lines.Add($"{LocalizationService.T("MediaInfoAnimation")}: {LocalizationService.T(probe.IsAnimated ? "MediaInfoYes" : "MediaInfoNo")}");
        }

        var audioTracks = probe.AudioTracks.Count == 0
            ? LocalizationService.T("MediaInfoNone")
            : string.Join(Environment.NewLine, probe.AudioTracks.Select(track =>
                $"{LocalizationService.T("AudioTrack", track.RelativeIndex + 1, track.Language, track.Codec.ToUpperInvariant(), track.Channels)}{(string.IsNullOrWhiteSpace(track.Title) ? string.Empty : $" · {track.Title}")}"));
        lines.Add($"{LocalizationService.T("MediaInfoAudioTracks")}: {audioTracks}");

        var subtitles = probe.SubtitleCodecs.Count == 0
            ? LocalizationService.T("MediaInfoNone")
            : string.Join(", ", probe.SubtitleCodecs.Select(codec => codec.ToUpperInvariant()));
        lines.Add($"{LocalizationService.T("MediaInfoSubtitles")}: {subtitles}");

        AddMetadata(lines, LocalizationService.T("MediaInfoTitleMetadata"), probe.Title);
        AddMetadata(lines, LocalizationService.T("MediaInfoArtist"), probe.Artist);
        AddMetadata(lines, LocalizationService.T("MediaInfoAlbum"), probe.Album);

        return BuildInformationScroller(lines);
    }

    private static ScrollViewer BuildInformationScroller(IReadOnlyCollection<string> lines) =>
        new()
        {
            MaxHeight = 560,
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine, lines),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                LineHeight = 22
            }
        };

    private static void AddMetadata(ICollection<string> lines, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value}");
        }
    }

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value)
        ? LocalizationService.T("MediaInfoUnknown")
        : value;

    private static string FormatMediaDuration(TimeSpan duration) => duration <= TimeSpan.Zero
        ? LocalizationService.T("MediaInfoUnknown")
        : duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private static string FormatMediaBitrate(uint bitrate) => bitrate == 0
        ? LocalizationService.T("MediaInfoUnknown")
        : $"{bitrate / 1000} kbps";
    private void CopyErrorButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null)
        {
            return;
        }
        ClipboardService.CopyText($"{task.FileName}{Environment.NewLine}{task.StateText}: {task.Message}");
        ShowInfo(LocalizationService.T("Copied"), InfoBarSeverity.Success);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag) NavigateWithMotion(tag);
        CaptureDiagnosticUiSnapshot();
        UpdateNavigationPageHeading();
    }
    private void RefreshVisibleTasks()
    {
        using var groupRefresh = ConverterView.DeferOutputGroupRefresh();
        PrepareTaskCollectionMotion();
        _queuePage.RefreshVisibleTasks();

        var hasVisibleTasks = VisibleTasks.Count > 0;
        UpdateTaskContentVisibility(hasVisibleTasks);
        ConverterView.EmptyStateTitle.Text = _queuePage.EmptyTitle;
        ConverterView.EmptyStateText.Text = _queuePage.EmptyMessage;
    }

    private bool MatchesFilter(MediaTaskViewModel task) => _queuePage.MatchesFilter(task);

    private void UpdateQueueSummary()
    {
        var state = _queuePage.CreateSummary();
        ConverterView.ConversionResultsSummaryText.Text = state.ResultSummary;
        // Aggregate measurements remain available in the summary flyout; a second
        // permanent results row repeats the same queue information and costs height.
        ConverterView.ConversionResultsSummaryText.Visibility = Visibility.Collapsed;
        AnimateQueueSummary(state.Active, state.Completed);
        ConverterView.QueueSummaryText.Visibility = state.HasTasks ? Visibility.Visible : Visibility.Collapsed;
        ConverterView.QueueSummaryButton.Visibility = ConverterView.QueueSummaryText.Visibility;
        ConverterView.QueueSummaryText.Text = state.Summary;
        ConverterView.QueueSummaryDetailsText.Text = state.Details;
        ToolTipService.SetToolTip(ConverterView.QueueSummaryButton, state.Details);
        AutomationProperties.SetName(ConverterView.QueueSummaryButton, state.Summary);
        AutomationProperties.SetHelpText(ConverterView.QueueSummaryButton, state.Details);
        if (SettingsView.ViewModel.IsInitialized)
        {
            SettingsView.CpuStrategyText.Text = PerformanceTuning.DescribeCpuStrategy(_settings);
        }
        ConverterView.ClearPendingButton.IsEnabled = state.CanClearPending;
        ConverterView.StopAllButton.Content = LocalizationService.T("ConversionUiQueueStopCount", state.StopCount);
        ConverterView.StopAllButton.IsEnabled = state.CanStop;
    }



    private async void BrowseFfmpegButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        SettingsView.FfmpegPathTextBox.Text = file.Path;
        if (await _ffmpegLocator.ValidatePairAsync(file.Path))
        {
            ShowSettingsInfo(LocalizationService.T("FfmpegAvailable"), InfoBarSeverity.Success);
        }
        else
        {
            ShowSettingsInfo(LocalizationService.T("FfmpegInvalid"), InfoBarSeverity.Error);
        }
    }

    private Task RefreshHistoryAsync(string? query = null, bool debounce = false) => HistoryView.RefreshAsync(query, debounce);

    private void OpenHistoryOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && !string.IsNullOrWhiteSpace(path))
        {
            OpenPathInExplorer(path);
        }
    }

    private async void BrowseLibreOfficeButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExternalToolAsync(new[] { ".com", ".exe" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            SettingsView.LibreOfficePathTextBox.Text = path;
            await RefreshDocumentEngineStatusAsync(previewDraft: true);
        }
    }

    private async void BrowseCalibreButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExternalToolAsync(new[] { ".exe" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            SettingsView.CalibrePathTextBox.Text = path;
            await RefreshDocumentEngineStatusAsync(previewDraft: true);
        }
    }

    private async void BrowseCajButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExternalToolAsync(new[] { ".exe" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            SettingsView.Caj2PdfPathTextBox.Text = path;
            await RefreshDocumentEngineStatusAsync(previewDraft: true);
        }
    }

    private async void BrowseMakeMkvButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExternalToolAsync(new[] { ".exe" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            SettingsView.MakeMkvPathTextBox.Text = path;
            await RefreshOpticalEngineStatusAsync(previewDraft: true);
        }
    }

    private async void BrowseAudioCdRipperButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExternalToolAsync(new[] { ".exe" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            SettingsView.AudioCdRipperPathTextBox.Text = path;
            await RefreshOpticalEngineStatusAsync(previewDraft: true);
        }
    }

    private async void BrowseReadomButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExternalToolAsync(new[] { ".exe" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            SettingsView.ReadomPathTextBox.Text = path;
            await RefreshOpticalEngineStatusAsync(previewDraft: true);
        }
    }

    private async void DetectDocumentEnginesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDocumentEngineStatusAsync(autoDetect: true);
        ShowSettingsInfo(LocalizationService.T("DocumentEnginesRefreshed"), InfoBarSeverity.Success);
    }

    private async void DetectOpticalEnginesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshOpticalEngineStatusAsync(autoDetect: true);
        ShowSettingsInfo(
            LocalizationService.T("OpticalEnginesRefreshed"),
            InfoBarSeverity.Success);
    }

    private async Task<string?> PickExternalToolAsync(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task RefreshDocumentEngineStatusAsync(bool autoDetect = false, bool previewDraft = false)
    {
        using var measurement = _initializing ? _startupMetrics.MeasureStage("document-engine-discovery") : null;
        if (_windowClosed) return;
        previewDraft = SettingsView.ViewModel.IsInitialized && (previewDraft || (autoDetect && !_initializing));
        var libreOfficePath = previewDraft ? SettingsView.LibreOfficePathTextBox.Text : _settings.LibreOfficePath;
        var calibrePath = previewDraft ? SettingsView.CalibrePathTextBox.Text : _settings.CalibreEbookConvertPath;
        var cajPath = previewDraft ? SettingsView.Caj2PdfPathTextBox.Text : _settings.Caj2PdfPath;
        var discovery = new ExternalToolDiscovery(new ExternalToolConfiguration(
            libreOfficePath, calibrePath, cajPath));
        var tools = await Task.Run(() => discovery.DiscoverAsync());
        if (_windowClosed) return;
        if (!previewDraft)
            _documentTools = tools.ToDictionary(tool => tool.Kind);
        if (autoDetect && _initializing)
        {
            await UpdateAutomaticSettingsAsync(settings =>
            {
                foreach (var tool in tools.Where(tool => tool.IsAvailable && !string.IsNullOrWhiteSpace(tool.ExecutablePath)))
                {
                    switch (tool.Kind)
                    {
                        case ExternalToolKind.LibreOffice when string.IsNullOrWhiteSpace(settings.LibreOfficePath):
                            settings.LibreOfficePath = PersistedEnginePath(tool.ExecutablePath); break;
                        case ExternalToolKind.Calibre when string.IsNullOrWhiteSpace(settings.CalibreEbookConvertPath):
                            settings.CalibreEbookConvertPath = PersistedEnginePath(tool.ExecutablePath); break;
                        case ExternalToolKind.Caj2Pdf when string.IsNullOrWhiteSpace(settings.Caj2PdfPath):
                            settings.Caj2PdfPath = PersistedEnginePath(tool.ExecutablePath); break;
                    }
                }
            });
            AcceptInitializedSettings();
        }
        if (_windowClosed) return;
        RenderDocumentEngineStatus(tools, autoDetect, libreOfficePath, calibrePath, cajPath);
        foreach (var task in Tasks)
        {
            UpdateTaskEngineAvailability(task);
        }
    }

    private void RenderDocumentEngineStatus(IEnumerable<ExternalToolInfo> tools,
        bool autoDetect = false, string? libreOfficePath = null, string? calibrePath = null, string? cajPath = null)
    {
        if (!_windowClosed) SettingsView.RenderDocumentEngineStatus(tools, autoDetect, libreOfficePath, calibrePath, cajPath,
            preserveConfiguredPaths: _initializing);
    }

    private async Task RefreshOpticalEngineStatusAsync(bool autoDetect = false, bool previewDraft = false)
    {
        using var measurement = _initializing ? _startupMetrics.MeasureStage("optical-engine-discovery") : null;
        if (_windowClosed) return;
        previewDraft = SettingsView.ViewModel.IsInitialized && (previewDraft || (autoDetect && !_initializing));
        var makeMkvPath = previewDraft ? SettingsView.MakeMkvPathTextBox.Text : _settings.MakeMkvPath;
        var audioCdPath = previewDraft ? SettingsView.AudioCdRipperPathTextBox.Text : _settings.AudioCdRipperPath;
        var readomPath = previewDraft ? SettingsView.ReadomPathTextBox.Text : _settings.ReadomPath;
        var discovery = new OpticalDiscToolDiscovery(new OpticalDiscToolConfiguration(
            makeMkvPath, audioCdPath, readomPath));
        var tools = await Task.Run(() => discovery.DiscoverAsync());
        if (_windowClosed) return;
        if (!previewDraft)
            _opticalTools = tools.ToDictionary(tool => tool.Kind);
        if (autoDetect && _initializing)
        {
            await UpdateAutomaticSettingsAsync(settings =>
            {
                foreach (var tool in tools.Where(tool => tool.IsAvailable && !string.IsNullOrWhiteSpace(tool.ExecutablePath)))
                {
                    switch (tool.Kind)
                    {
                        case OpticalDiscToolKind.MakeMkv when string.IsNullOrWhiteSpace(settings.MakeMkvPath):
                            settings.MakeMkvPath = PersistedEnginePath(tool.ExecutablePath); break;
                        case OpticalDiscToolKind.AudioCdRipper when string.IsNullOrWhiteSpace(settings.AudioCdRipperPath):
                            settings.AudioCdRipperPath = PersistedEnginePath(tool.ExecutablePath); break;
                        case OpticalDiscToolKind.Readom when string.IsNullOrWhiteSpace(settings.ReadomPath):
                            settings.ReadomPath = PersistedEnginePath(tool.ExecutablePath); break;
                    }
                }
            });
            AcceptInitializedSettings();
        }
        if (_windowClosed) return;
        RenderOpticalEngineStatus(tools, autoDetect, makeMkvPath, audioCdPath, readomPath);
        foreach (var task in Tasks)
        {
            UpdateTaskEngineAvailability(task);
        }
    }

    private void RenderOpticalEngineStatus(IEnumerable<OpticalDiscToolInfo> tools,
        bool autoDetect = false, string? makeMkvPath = null, string? audioCdPath = null, string? readomPath = null)
    {
        if (!_windowClosed) SettingsView.RenderOpticalEngineStatus(tools, autoDetect, makeMkvPath, audioCdPath, readomPath,
            preserveConfiguredPaths: _initializing);
    }

    private async void DetectFfmpegButton_Click(object sender, RoutedEventArgs e) => await DetectFfmpegAsync(showSuccess: true);


    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _services.Shell.OpenLogsDirectory(LogService.LogsDirectory);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            ShowSettingsInfo(exception.Message, InfoBarSeverity.Warning);
        }
    }

    private string? _appInfoBarTitleResourceKey;

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        if (_windowClosed) return;
        var wasOpen = ConverterView.AppInfoBar.IsOpen;
        _appInfoBarTitleResourceKey = null;
        ConverterView.AppInfoBar.Title = string.Empty;
        ConverterView.AppInfoBar.Message = message;
        ConverterView.AppInfoBar.Severity = severity;
        ConverterView.AppInfoBar.IsOpen = true;
        AnimateInfoBar(ConverterView.AppInfoBar, wasOpen);
    }

    private void ShowSettingsInfo(string message, InfoBarSeverity severity)
    {
        if (_windowClosed) return;
        if (!SettingsView.ViewModel.IsInitialized)
        {
            _pendingSettingsInfo = (message, severity);
            return;
        }
        var wasOpen = SettingsView.SettingsInfoBar.IsOpen;
        SettingsView.SettingsInfoBar.Message = message;
        SettingsView.SettingsInfoBar.Severity = severity;
        SettingsView.SettingsInfoBar.IsOpen = true;
        AnimateInfoBar(SettingsView.SettingsInfoBar, wasOpen);
    }

    private bool EnsureFfmpegReady()
    {
        if (!string.IsNullOrWhiteSpace(_ffmpegPath) && !string.IsNullOrWhiteSpace(_ffprobePath))
        {
            return true;
        }
        ShowInfo(LocalizationService.T("PleaseSetFfmpeg"), InfoBarSeverity.Error);
        return false;
    }

    private bool EnsureTaskEngineReady(MediaTaskViewModel task)
    {
        UpdateTaskEngineAvailability(task);
        if (!task.IsTargetAvailable)
        {
            ShowInfo(task.EngineRequirement, InfoBarSeverity.Warning);
            return false;
        }

        return ShouldUseDocumentPipeline(task.Model) ||
               ShouldUseDiscPipeline(task.Model) ||
               _services.Conversions.NativeImageConversion.CanConvertWithoutFfmpeg(task.Model) ||
               EnsureFfmpegReady();
    }

    private void UpdateTaskEngineAvailability(MediaTaskViewModel task)
    {
        var source = ResolveSourceFormatId(task.Model);
        var target = ResolveTargetFormatId(task.Model);
        var wordAvailable = IsDocumentToolAvailable(ExternalToolKind.MicrosoftWord);
        var libreOfficeAvailable = IsDocumentToolAvailable(ExternalToolKind.LibreOffice);
        var calibreAvailable = IsDocumentToolAvailable(ExternalToolKind.Calibre);
        var cajAvailable = IsDocumentToolAvailable(ExternalToolKind.Caj2Pdf);

        string? requirement = null;
        var audioSelectionError = CompatibleAudioTrackPolicy.GetValidationError(task.Model, task.Probe);
        if (!string.IsNullOrWhiteSpace(audioSelectionError))
        {
            requirement = audioSelectionError;
        }
        else if (source == "epub" && !_documentRoutes.CanPlan(source, target))
        {
            requirement = LocalizationService.T("ConversionUiRouteNotImplemented", source, target);
        }
        else if (task.Model.ContentKind == ContentKind.Disc)
        {
            requirement = GetDiscEngineRequirement(task.Model, source, target);
        }
        else if (source is "caj" or "hn" or "c8" or "kdh" && !cajAvailable)
        {
            requirement = LocalizationService.T("RequiresCajConverter");
        }
        else if (source == "doc" && target == "pdf" && !wordAvailable && !libreOfficeAvailable)
        {
            requirement = LocalizationService.T("RequiresWordOrLibreOffice");
        }
        else if (source == "doc" && target != "pdf" &&
                 !libreOfficeAvailable &&
                 !_windowsFilterTextExtractor.IsAvailable(task.InputPath))
        {
            requirement = LocalizationService.T("RequiresLibreOffice");
        }
        else if (source == "docx" && target == "pdf" && !wordAvailable && !libreOfficeAvailable)
        {
            requirement = LocalizationService.T("RequiresWordOrLibreOffice");
        }
        else if (target is "mobi" or "azw3" && !calibreAvailable)
        {
            requirement = LocalizationService.T("RequiresCalibre");
        }

        var previousRequirement = task.EngineRequirement;
        task.SetEngineAvailability(requirement is null, requirement);
        if (task.State == ConversionState.Waiting)
        {
            if (requirement is not null)
            {
                task.Message = requirement;
            }
            else if (!string.IsNullOrWhiteSpace(previousRequirement) &&
                     string.Equals(task.Message, previousRequirement, StringComparison.Ordinal))
            {
                task.Message = LocalizationService.T("WaitingStart");
            }
        }
    }

    private bool IsDocumentToolAvailable(ExternalToolKind kind) =>
        _documentTools.TryGetValue(kind, out var tool) && tool.IsAvailable;

    private bool IsOpticalToolAvailable(OpticalDiscToolKind kind) =>
        _opticalTools.TryGetValue(kind, out var tool) && tool.IsAvailable;

    private void RefreshTaskEngineAvailability()
    {
        if (_windowClosed) return;
        foreach (var task in Tasks)
        {
            UpdateTaskEngineAvailability(task);
        }
    }

    private string? GetDiscEngineRequirement(MediaTask task, string source, string target)
    {
        // A restored row can be published before its unavailable source is probed.
        // Keep incomplete routes blocked without passing them to the strict planner.
        if (string.IsNullOrWhiteSpace(target))
            return LocalizationService.T("ConversionUiRequestRequiresTargetFormat");
        if (string.IsNullOrWhiteSpace(source))
            return LocalizationService.T("ConversionUiQueueBlocked");

        if (OpticalDiscFormatIds.IsPhysicalDisc(source))
        {
            var currentDisc = _opticalDiscDiscovery.FindByRootPath(task.InputPath);
            if (currentDisc is null || !currentDisc.IsReady)
            {
                return LocalizationService.T("RequiresDiscMedia");
            }
            if (!source.Equals(OpticalDiscFormatIds.OpticalDisc, StringComparison.OrdinalIgnoreCase) &&
                !currentDisc.SourceFormatId.Equals(source, StringComparison.OrdinalIgnoreCase))
            {
                return LocalizationService.T("RequiresMatchingDiscMedia");
            }
        }
        var plan = OpticalDiscBackendPlanner.Plan(source, target);
        if (plan.RequiredDependencies.Contains(OpticalDiscDependency.MakeMkv) &&
            !IsOpticalToolAvailable(OpticalDiscToolKind.MakeMkv))
        {
            return LocalizationService.T("RequiresMakeMkv");
        }
        if (plan.RequiredDependencies.Contains(OpticalDiscDependency.Ffmpeg) &&
            (string.IsNullOrWhiteSpace(_ffmpegPath) || !File.Exists(_ffmpegPath)))
        {
            return LocalizationService.T("RequiresFfmpegForCdAudio");
        }
        return null;
    }

    private static bool ShouldUseNativeImagePipeline(MediaTask task) =>
        task.Kind == MediaKind.Image && FormatCatalog.IsImageFormat(task.TargetFormat);

    private bool ShouldUseDocumentPipeline(MediaTask task)
    {
        if (task.ContentKind is ContentKind.Document or ContentKind.Ebook ||
            string.Equals(task.EngineId, DocumentEngineIds.Pipeline, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _documentRoutes.CanPlan(ResolveSourceFormatId(task), ResolveTargetFormatId(task));
    }

    private static bool ShouldUseDiscPipeline(MediaTask task) =>
        task.ContentKind == ContentKind.Disc ||
        string.Equals(task.EngineId, DiscConversionService.EngineId, StringComparison.OrdinalIgnoreCase);

    private static string ResolveSourceFormatId(MediaTask task) =>
        string.IsNullOrWhiteSpace(task.SourceFormatId)
            ? Path.GetExtension(task.InputPath).TrimStart('.').ToLowerInvariant()
            : task.SourceFormatId;

    private static string ResolveTargetFormatId(MediaTask task) =>
        string.IsNullOrWhiteSpace(task.TargetFormatId)
            ? FormatCatalog.NormalizeFormatId(task.TargetFormat)
            : task.TargetFormatId;

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_windowClosed) return Task.CompletedTask;
        if (DispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!_windowClosed) await action();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("UI dispatcher is unavailable."));
        }
        return completion.Task;
    }

    private Task SaveQueueAsync() => _queueSession.SaveAsync();
    private MediaTaskViewModel? FindTaskFromSender(object sender)
    {
        if (sender is not FrameworkElement element || element.Tag is null)
        {
            return null;
        }
        var id = element.Tag is Guid guid ? guid : Guid.TryParse(element.Tag.ToString(), out var parsed) ? parsed : Guid.Empty;
        return Tasks.FindById(id);
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SafeDiagnostics.Write(LocalizationService.T("OperationalLogWindowCloseRequested", _allowClose,
            _closeSaveInProgress, _queueCoordinator.ActiveOperationCount));
        if (_allowClose) return;
        args.Cancel = true;
        DismissReviewReminder();
        if (_closeSaveInProgress) return;
        var hasWork = ArchiveView.IsRunning || _queueCoordinator.HasActiveOperations || Tasks.Any(task => task.State is ConversionState.Running or ConversionState.Paused or ConversionState.Probing);
        if (_trayIcon is not null && (hasWork || _settings.AlwaysMinimizeToTray || HasAutomaticRules))
        {
            NativeWindowService.Hide(_windowHandle);
            _trayIcon.ShowBalloon(LocalizationService.T("AppTitle"), hasWork ? LocalizationService.T("BackgroundConversion") : LocalizationService.T("TrayMinimized"));
            return;
        }
        await CloseSessionAsync();
    }

    private async void TrayIcon_ExitRequested(object? sender, EventArgs e) => await CloseSessionAsync();

    private async Task CloseSessionAsync()
    {
        if (_settingsResetInProgress || _closeSaveInProgress || _windowClosed) return;
        try
        {
            await _lifecycle.ShutdownAsync(new ApplicationShutdownOperations
            {
                StopArchives = () => { DismissReviewReminder(); return ArchiveView.StopAsync(preserveForRestart: true); },
                StopImports = StopImportAsync,
                CloseDialogs = DismissFeedbackAsync,
                CancelHistoryIndexing = CancelHistoryIndexing,
                StopAutomation = StopAutomationAsync,
                SaveUpdatePreferences = WaitForStoreUpdatePreferenceSaveAsync,
                ShutdownQueue = save => _queueSession.ShutdownAsync(save: save),
                ConfirmUnsavedExit = ConfirmExitWithoutSavingAsync,
                ResumeQueue = _queueSession.ContinueAfterFailedShutdown,
                RestartAutomation = RestartAutomationAsync,
                ConfigureWatchFolders = ConfigureWatchFolder,
                PersistWindowPlacement = PersistWindowPlacementAsync,
                InstallUpdate = TryInstallStoreUpdateOnExitAsync,
                CloseWindow = () => { _trayIcon?.Dispose(); _trayIcon = null; Close(); },
                ObserveStartupFailure = exception => Debug.WriteLine(exception),
                ObservePlacementFailure = exception => SafeDiagnostics.Report(exception)
            });
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            NavView.IsEnabled = true;
            RecoveryService.Register();
            ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (!_windowClosed && !_allowClose) ScheduleStoreUpdateCheck(TimeSpan.FromSeconds(30));
        }
    }

    private async Task<bool> ConfirmExitWithoutSavingAsync(Exception error)
    {
        NativeWindowService.Show(_windowHandle);
        var dialog = new ContentDialog
        {
            Title = LocalizationService.T("ConversionUiExitWithoutSavingTitle"),
            Content = LocalizationService.T("ConversionUiExitWithoutSavingMessage", error.Message),
            PrimaryButtonText = LocalizationService.T("ConversionUiExitWithoutSaving"),
            CloseButtonText = LocalizationService.T("ConversionUiExitKeepWindowOpen"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        return await ShowContentDialogAsync(dialog) == ContentDialogResult.Primary;
    }
    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _windowClosed = true;
        _taskbarOutputLifetime.Cancel();
        _taskbarOutputLifetime.Dispose();
        CaptureDiagnosticUiSnapshot();
        CompositionTarget.Rendering -= Startup_FirstRendering;
        StopStartupReadinessObservation();
        _activeImport?.Cancellation.Cancel();
        SafeDiagnostics.ErrorReported -= OnDiagnosticError;
        DisposeTelemetry();
        DisposeStoreUpdates();
        DisposeAutomation();
        DisposeReviewReminder();
        DisposeHistoryIndexing();
        DisposeViews();
        DisposeNavigation();
        DisposeWindowPlacement();
        ClosePreviewWindows();
        DisposeMotion();
        _progressTimer?.Stop();
        if (_progressTimer is not null) _progressTimer.Tick -= FlushPendingProgress;
        _queuePage.ClearProgress();
        _activationReady.TrySetCanceled();
        ResetDropFeedback();
        SafeDiagnostics.Write(LocalizationService.T("OperationalLogWindowClosed"));
        _watchFolderCoordinator.ImportCompleted -= WatchFolderCoordinator_ImportCompleted;
        _watchFolderCoordinator.Dispose();
        _opticalDeviceWatcher.Changed -= OpticalDeviceWatcher_Changed;
        _systemPowerStateService.StateChanged -= SystemPowerStateService_StateChanged;
        _systemPowerStateService.Transitioned -= SystemPowerStateService_Transitioned;
        _accessibilityService.Changed -= WindowsAccessibilityService_Changed;
        _opticalDeviceWatcher.Dispose();
        _systemPowerStateService.Dispose();
        _accessibilityService.Dispose();
        foreach (var task in Tasks)
        {
            task.PropertyChanged -= Task_PropertyChanged;
        }
        _queuePage.Dispose();
        _shareService?.Dispose();
        _shareService = null;
        if (_taskbarProgress is not null)
        {
            _taskbarProgress.ButtonClicked -= TaskbarProgress_ButtonClicked;
            _taskbarProgress.Dispose();
            _taskbarProgress = null;
        }
        _trayIcon?.Dispose();
        AppNotificationService.Shutdown();
        RecoveryService.Unregister();
        SafeDiagnostics.Flush();
        _queueSession.TaskAdded -= QueueSession_TaskAdded;
        _queueSession.TaskStateChanged -= QueueSession_TaskStateChanged;
        _queueSession.RestoreCompleted -= QueueSession_RestoreCompleted;
        _queueSession.SideEffectFailed -= QueueSession_SideEffectFailed;
        _queueSession.Dispose();
    }
}
