using System.Diagnostics;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Storage;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private Task InitializeWindowAsync() => _lifecycle.StartAsync(new ApplicationStartupOperations
    {
        PrepareWindow = PrepareNativeWindow,
        LoadSettings = LoadInitialSettingsAsync,
        DiscoverDocuments = () => RefreshDocumentEngineStatusAsync(autoDetect: true),
        DiscoverOptical = () => RefreshOpticalEngineStatusAsync(autoDetect: true),
        LoadHardware = LoadHardwareInventoryAsync,
        StartObservers = () =>
        {
            _opticalDeviceWatcher.Start();
            _systemPowerStateService.Start();
            _accessibilityService.Start();
        },
        DiscoverMedia = async () =>
        {
            using (_startupMetrics.MeasureStage("ffmpeg-detection-and-context"))
                await DetectFfmpegAsync(showSuccess: false);
        },
        RestoreQueue = async () =>
        {
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogFfmpegDetectionCompleted", _ffmpegPath));
            using (_startupMetrics.MeasureStage("queue-recovery")) await RestoreQueueAsync();
        },
        RestoreArchives = async () =>
        {
            using (_startupMetrics.MeasureStage("archive-recovery")) await ArchiveView.RestoreAsync();
        },
        PublishInteractive = () =>
        {
            _startupMetrics.Mark("recovery-complete");
            RefreshSettingsSaveState();
            _activationReady.TrySetResult(true);
            _startupMetrics.Mark("activation-ready");
            UpdateQueueSummary();
            ScheduleAddFilesReadiness();
        },
        StartAutomation = InitializeAutomationAsync,
        ReconcileAutomation = ReconcileRestoredAutomationGroupsAsync,
        ConfigureWatchFolders = ConfigureWatchFolder,
        ScheduleSupplementaryServices = () =>
        {
            _ = BackgroundTaskService.ConfigureWatchFolderAsync(_settings.WatchFolderEnabled);
            _ = InitializeSupplementaryServicesAsync();
        },
        Complete = () =>
        {
            ShowStartupSettingsSaveWarning();
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogWindowLoadingCompleted"));
        },
        IsQueueStopping = () => _queueSession.IsStopping
    });

    private void PrepareNativeWindow()
    {
        SafeDiagnostics.Write(LocalizationService.T("OperationalLogWindowLoadingStarted"));
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _taskbarProgress = _services.Shell.CreateTaskbarProgress(_windowHandle);
        _taskbarProgress.ButtonClicked += TaskbarProgress_ButtonClicked;
        try
        {
            _shareService = _services.Shell.CreateShareSource(_windowHandle);
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogShareSourceInitializationFailed", ex.Message));
        }
        if (_startInTray)
        {
            NativeWindowService.Hide(_windowHandle);
        }
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var width = Math.Min(1280, Math.Max(720, workArea.Width - 80));
        var height = Math.Min(820, Math.Max(560, workArea.Height - 80));
        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _appWindow.Closing += AppWindow_Closing;

        if (!string.Equals(Environment.GetEnvironmentVariable("FORMAT_FACTORY_DISABLE_TRAY"), "1", StringComparison.Ordinal))
        {
            _trayIcon = _services.Shell.CreateTrayIcon(_windowHandle);
            _trayIcon.ShowRequested += (_, _) => NativeWindowService.Show(_windowHandle);
            _trayIcon.ExitRequested += TrayIcon_ExitRequested;
            _trayIcon.NavigationRequested += TrayIcon_NavigationRequested;
            _trayIcon.TaskbarButtonCreated += (_, _) => _taskbarProgress?.ResetForTaskbarRecreation();
            _trayIcon.CommandReceived += (_, id) => _taskbarProgress?.HandleWindowCommand(new IntPtr(id));
            _taskbarProgress.ConfigureButtons();
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogTrayIconInitialized"));
        }

    }

    private async Task LoadInitialSettingsAsync()
    {
        using var settingsMeasurement = _startupMetrics.MeasureStage("settings-and-window-setup");
        _settings = await _settingsService.LoadAsync();
        if (_windowClosed) return;
        InitializeTelemetry();
        await RestoreWindowPlacementAsync();
        if (_windowClosed) return;
        NormalizeSettings();
        LocalizationService.SetLanguage(_settings.Language);
        ApplyLocalization();
        if (_settings.PerformanceDefaultsVersion < 2)
        {
            _settings.ParallelCount = Math.Clamp(_settings.ParallelCount, 1, 4);
            _settings.CpuThreadsPerTask = Math.Clamp(_settings.CpuThreadsPerTask, 0, Environment.ProcessorCount);
            _settings.HardwareAcceleration = true;
            _settings.PerformanceDefaultsVersion = 2;
            await TryPersistStartupSettingsAsync(() => _settingsService.SaveAsync(_settings));
        }
        if (_windowClosed) return;
        SafeDiagnostics.Write(LocalizationService.T("OperationalLogSettingsLoaded"));
        SettingsView.ViewModel.IsAvailable = true;
        ApplySettingsToControls(initializeRuntime: true);
        settingsMeasurement.Dispose();
        // Legacy smoke metric: settings only, not file-import readiness.
        _startupMetrics.Mark("first-interactive");
        _startupMetrics.Mark("settings-ready");
        if (SettingsView.ViewModel.IsInitialized) InitializeSettingsDraft();
    }
}
