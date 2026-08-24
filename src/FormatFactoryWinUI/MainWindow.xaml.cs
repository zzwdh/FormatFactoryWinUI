using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly QueueStore _queueStore = new();
    private readonly FfmpegLocator _ffmpegLocator = new();
    private readonly MediaProbeService _probeService = new();
    private readonly ConversionService _conversionService = new();
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _taskCancellation = new();
    private readonly ConcurrentDictionary<Guid, Task> _runOperations = new();

    private AppSettings _settings = new();
    private SemaphoreSlim _runGate = new(1, 3);
    private string _ffmpegPath = string.Empty;
    private string _ffprobePath = string.Empty;
    private string _filter = "all";
    private IntPtr _windowHandle;
    private AppWindow? _appWindow;
    private TrayIconService? _trayIcon;
    private bool _initializing = true;
    private bool _allowClose;
    private bool _batchWasStarted;

    public ObservableCollection<MediaTask> Tasks { get; } = [];
    public ObservableCollection<MediaTask> VisibleTasks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        RootGrid.Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        Tasks.CollectionChanged += async (_, _) =>
        {
            RefreshVisibleTasks();
            UpdateQueueSummary();
            if (!_initializing)
            {
                await _queueStore.SaveAsync(Tasks);
            }
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LogService.WriteApp("窗口 Loaded 开始");
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _appWindow.Closing += AppWindow_Closing;

        if (!string.Equals(Environment.GetEnvironmentVariable("FORMAT_FACTORY_DISABLE_TRAY"), "1", StringComparison.Ordinal))
        {
            _trayIcon = new TrayIconService(_windowHandle, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            _trayIcon.ShowRequested += (_, _) => NativeWindowService.Show(_windowHandle);
            _trayIcon.ExitRequested += TrayIcon_ExitRequested;
            LogService.WriteApp("托盘图标已初始化");
        }

        _settings = await _settingsService.LoadAsync();
        if (_settings.PerformanceDefaultsVersion < 2)
        {
            _settings.ParallelCount = Math.Clamp(_settings.ParallelCount, 1, 4);
            _settings.CpuThreadsPerTask = Math.Clamp(_settings.CpuThreadsPerTask, 0, Environment.ProcessorCount);
            _settings.HardwareAcceleration = true;
            _settings.PerformanceDefaultsVersion = 2;
            await _settingsService.SaveAsync(_settings);
        }
        LogService.WriteApp("设置已读取");
        ApplySettingsToControls();
        await DetectFfmpegAsync(showSuccess: false);
        LogService.WriteApp($"FFmpeg 检测结束: {_ffmpegPath}");
        await RestoreQueueAsync();
        _initializing = false;
        UpdateQueueSummary();
        LogService.WriteApp("窗口 Loaded 完成");
    }

    private async Task RestoreQueueAsync()
    {
        if (string.IsNullOrWhiteSpace(_ffprobePath))
        {
            return;
        }

        var savedItems = await _queueStore.LoadAsync();
        var restored = 0;
        foreach (var item in savedItems)
        {
            if (!File.Exists(item.InputPath))
            {
                continue;
            }
            var task = await AddFileInternalAsync(item.InputPath, item.SourceRoot, item);
            if (task is not null)
            {
                restored++;
            }
        }

        if (restored > 0)
        {
            ShowInfo($"已恢复 {restored} 个任务。", InfoBarSeverity.Informational);
        }
    }

    private void ApplySettingsToControls()
    {
        FfmpegPathTextBox.Text = _settings.FfmpegPath;
        ParallelCountBox.Value = Math.Clamp(_settings.ParallelCount, 1, 4);
        CpuThreadsBox.Value = Math.Clamp(_settings.CpuThreadsPerTask, 0, Environment.ProcessorCount);
        CpuStrategyText.Text = PerformanceTuning.DescribeCpuStrategy(_settings);
        HardwareToggle.IsOn = _settings.HardwareAcceleration;
        NotificationToggle.IsOn = _settings.Notifications;
        SoundToggle.IsOn = _settings.NotificationSound;
        TrayToggle.IsOn = _settings.AlwaysMinimizeToTray;
        ThemeCombo.SelectedItem = _settings.Theme;
        OutputModeCombo.SelectedItem = _settings.OutputMode;
        OutputDirectoryText.Text = _settings.CustomOutputDirectory;
        OutputDirectoryButton.IsEnabled = _settings.OutputMode == "自定义目录";
        ApplyTheme();
        _runGate.Dispose();
        _runGate = new SemaphoreSlim(Math.Clamp(_settings.ParallelCount, 1, 4), 4);
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _settings.Theme switch
        {
            "浅色" => ElementTheme.Light,
            "深色" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private async Task DetectFfmpegAsync(bool showSuccess)
    {
        var result = await _ffmpegLocator.LocateAsync(FfmpegPathTextBox.Text);
        if (result is null)
        {
            ShowInfo("未找到 FFmpeg，正在自动下载并安装。", InfoBarSeverity.Informational);
            ShowSettingsInfo("未找到 FFmpeg，正在自动下载并安装。", InfoBarSeverity.Informational);
            try
            {
                var progress = new Progress<int>(value =>
                {
                    var message = value >= 100 ? "正在校验并安装 FFmpeg…" : $"正在下载 FFmpeg… {value}%";
                    ShowInfo(message, InfoBarSeverity.Informational);
                    ShowSettingsInfo(message, InfoBarSeverity.Informational);
                });
                result = await _ffmpegLocator.DownloadAndInstallAsync(progress);
            }
            catch (Exception exception)
            {
                LogService.WriteAppError(exception);
                _ffmpegPath = string.Empty;
                _ffprobePath = string.Empty;
                ShowInfo("FFmpeg 自动下载失败，请检查网络后重试或手动选择。", InfoBarSeverity.Error);
                ShowSettingsInfo("FFmpeg 自动下载失败，请检查网络后重试或手动选择。", InfoBarSeverity.Error);
                return;
            }
        }

        if (result is null)
        {
            _ffmpegPath = string.Empty;
            _ffprobePath = string.Empty;
            ShowInfo("未能配置 FFmpeg，请手动选择。", InfoBarSeverity.Error);
            return;
        }

        _ffmpegPath = result.Value.Ffmpeg;
        _ffprobePath = result.Value.Ffprobe;
        FfmpegPathTextBox.Text = _ffmpegPath;
        _settings.FfmpegPath = _ffmpegPath;
        await _settingsService.SaveAsync(_settings);
        ShowInfo("FFmpeg 已就绪。", InfoBarSeverity.Success);
        if (showSuccess)
        {
            ShowSettingsInfo("FFmpeg 可用。", InfoBarSeverity.Success);
        }
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureFfmpegReady())
        {
            return;
        }

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in FormatCatalog.SupportedExtensions.OrderBy(value => value))
        {
            picker.FileTypeFilter.Add(extension);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        var files = await picker.PickMultipleFilesAsync();
        await AddPathsAsync(files.Select(file => file.Path), null);
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureFfmpegReady())
        {
            return;
        }

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await AddFolderAsync(folder.Path);
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "加入转换队列";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!EnsureFfmpegReady() || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFolder folder)
            {
                await AddFolderAsync(folder.Path);
            }
            else if (item is StorageFile file)
            {
                await AddPathsAsync([file.Path], null);
            }
        }
    }

    private async Task AddFolderAsync(string folderPath)
    {
        ShowInfo("正在递归扫描文件夹…", InfoBarSeverity.Informational);
        string[] files;
        try
        {
            files = await Task.Run(() => Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                .Where(path => FormatCatalog.SupportedExtensions.Contains(Path.GetExtension(path)))
                .ToArray());
        }
        catch (Exception ex)
        {
            ShowInfo($"读取文件夹失败：{ex.Message}", InfoBarSeverity.Error);
            return;
        }

        await AddPathsAsync(files, folderPath);
    }

    private async Task AddPathsAsync(IEnumerable<string> paths, string? sourceRoot)
    {
        var added = 0;
        var ignored = 0;
        foreach (var path in paths)
        {
            if (!FormatCatalog.SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                ignored++;
                continue;
            }
            if (await AddFileInternalAsync(path, sourceRoot, null) is null)
            {
                ignored++;
            }
            else
            {
                added++;
            }
        }

        ShowInfo($"已添加 {added} 个文件{(ignored > 0 ? $"，忽略 {ignored} 个重复或不可识别文件" : string.Empty)}。", InfoBarSeverity.Success);
    }

    private async Task<MediaTask?> AddFileInternalAsync(string path, string? sourceRoot, PersistedQueueItem? persisted)
    {
        var fullPath = Path.GetFullPath(path);
        if (!_knownPaths.Add(fullPath))
        {
            return null;
        }

        try
        {
            var probe = await _probeService.ProbeAsync(_ffprobePath, fullPath);
            var info = new FileInfo(fullPath);
            var task = new MediaTask
            {
                InputPath = fullPath,
                SourceRoot = sourceRoot,
                Kind = probe.Kind,
                Probe = probe,
                FileSize = info.Length
            };

            var targets = probe.Kind == MediaKind.Video && probe.AudioTracks.Count == 0
                ? FormatCatalog.VideoFormats
                : FormatCatalog.TargetsFor(probe.Kind);
            foreach (var format in targets)
            {
                task.AvailableFormats.Add(format);
            }
            foreach (var track in probe.AudioTracks)
            {
                task.AudioTracks.Add(track);
            }

            task.TargetFormat = persisted is not null && task.AvailableFormats.Contains(persisted.TargetFormat)
                ? persisted.TargetFormat
                : FormatCatalog.DefaultTarget(probe.Kind);
            task.ConversionMode = NormalizeConversionMode(persisted?.ConversionMode);
            task.SelectedAudioTrack = Math.Clamp(persisted?.SelectedAudioTrack ?? 0, 0, Math.Max(0, task.AudioTracks.Count - 1));
            task.Message = probe.IsHdr ? "HDR/10-bit · 自动处理" : "等待开始";
            task.PropertyChanged += Task_PropertyChanged;
            Tasks.Add(task);
            return task;
        }
        catch (Exception ex)
        {
            _knownPaths.Remove(fullPath);
            LogService.WriteApp($"分析失败 {fullPath}: {ex.Message}");
            ShowInfo($"无法读取“{Path.GetFileName(fullPath)}”：{ex.Message}", InfoBarSeverity.Error);
            return null;
        }
    }

    private async void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_initializing && e.PropertyName is nameof(MediaTask.TargetFormat) or nameof(MediaTask.ConversionMode) or nameof(MediaTask.SelectedAudioTrack))
        {
            await _queueStore.SaveAsync(Tasks);
        }
    }

    private void StartTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null || task.State is ConversionState.Running or ConversionState.Paused or ConversionState.Probing)
        {
            return;
        }
        if (!EnsureFfmpegReady())
        {
            return;
        }
        if (task.State is ConversionState.Completed or ConversionState.Failed or ConversionState.Cancelled)
        {
            ResetTask(task);
        }
        StartTask(task);
    }

    private void StartAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureFfmpegReady())
        {
            return;
        }
        foreach (var task in Tasks.Where(task => task.State == ConversionState.Waiting).ToArray())
        {
            StartTask(task);
        }
    }

    private void StartTask(MediaTask task)
    {
        if (_runOperations.ContainsKey(task.Id))
        {
            return;
        }

        _batchWasStarted = true;
        var cancellation = new CancellationTokenSource();
        _taskCancellation[task.Id] = cancellation;
        var operation = ExecuteTaskAsync(task, cancellation.Token);
        _runOperations[task.Id] = operation;
        _ = ObserveTaskAsync(task.Id, operation);
    }

    private async Task ExecuteTaskAsync(MediaTask task, CancellationToken cancellationToken)
    {
        try
        {
            await _runGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            task.State = ConversionState.Cancelled;
            task.Message = "已取消";
            return;
        }

        try
        {
            task.State = ConversionState.Running;
            task.Message = "正在启动…";
            var result = await _conversionService.ConvertAsync(
                task,
                _ffmpegPath,
                _settings,
                progress => DispatcherQueue.TryEnqueue(() =>
                {
                    task.Progress = progress.Percent;
                    task.Speed = progress.Speed;
                    task.Remaining = progress.Remaining;
                    task.Message = progress.Message;
                }),
                cancellationToken);

            if (result.Cancelled)
            {
                task.State = ConversionState.Cancelled;
            }
            else if (result.Success)
            {
                task.State = ConversionState.Completed;
                task.Progress = 100;
                task.OutputPath = result.OutputPath;
            }
            else
            {
                task.State = ConversionState.Failed;
            }
            task.Message = result.Message;
        }
        catch (OperationCanceledException)
        {
            task.State = ConversionState.Cancelled;
            task.Message = "已取消";
        }
        finally
        {
            _runGate.Release();
            await _queueStore.SaveAsync(Tasks);
        }
    }

    private async Task ObserveTaskAsync(Guid id, Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception ex)
        {
            LogService.WriteAppError(ex);
        }
        finally
        {
            _runOperations.TryRemove(id, out _);
            if (_taskCancellation.TryRemove(id, out var cancellation))
            {
                cancellation.Dispose();
            }
            DispatcherQueue.TryEnqueue(NotifyBatchIfFinished);
        }
    }

    private void NotifyBatchIfFinished()
    {
        UpdateQueueSummary();
        if (!_batchWasStarted || !_runOperations.IsEmpty)
        {
            return;
        }
        _batchWasStarted = false;
        var completed = Tasks.Count(task => task.State == ConversionState.Completed);
        var failed = Tasks.Count(task => task.State == ConversionState.Failed);
        var message = $"成功 {completed} · 失败 {failed}";
        if (_settings.Notifications)
        {
            _trayIcon?.ShowBalloon("转换完成", message);
        }
        if (_settings.NotificationSound)
        {
            NativeWindowService.PlayNotificationSound();
        }
    }

    private void PauseTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null)
        {
            return;
        }
        if (task.State == ConversionState.Running && _conversionService.Pause(task.Id))
        {
            task.State = ConversionState.Paused;
            task.Message = "已暂停";
        }
        else if (task.State == ConversionState.Paused && _conversionService.Resume(task.Id))
        {
            task.State = ConversionState.Running;
            task.Message = "继续转换";
        }
    }

    private void PauseAllButton_Click(object sender, RoutedEventArgs e)
    {
        var shouldPause = Tasks.Any(task => task.State == ConversionState.Running);
        foreach (var task in Tasks.ToArray())
        {
            if (shouldPause && task.State == ConversionState.Running && _conversionService.Pause(task.Id))
            {
                task.State = ConversionState.Paused;
                task.Message = "已暂停";
            }
            else if (!shouldPause && task.State == ConversionState.Paused && _conversionService.Resume(task.Id))
            {
                task.State = ConversionState.Running;
                task.Message = "继续转换";
            }
        }
    }

    private void CancelOrRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null)
        {
            return;
        }
        if (task.State is ConversionState.Completed or ConversionState.Failed or ConversionState.Cancelled or ConversionState.Waiting)
        {
            RemoveTask(task);
            return;
        }
        CancelTask(task);
    }

    private void CancelAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var task in Tasks.ToArray())
        {
            if (task.State == ConversionState.Waiting)
            {
                if (_taskCancellation.TryGetValue(task.Id, out var waitingCancellation))
                {
                    waitingCancellation.Cancel();
                }
                RemoveTask(task);
            }
            else if (task.State is ConversionState.Running or ConversionState.Paused or ConversionState.Probing)
            {
                CancelTask(task);
            }
        }
    }

    private void CancelTask(MediaTask task)
    {
        if (_taskCancellation.TryGetValue(task.Id, out var cancellation))
        {
            cancellation.Cancel();
        }
        _conversionService.Cancel(task.Id);
        task.Message = "正在取消…";
    }

    private void RemoveTask(MediaTask task)
    {
        task.PropertyChanged -= Task_PropertyChanged;
        _knownPaths.Remove(task.InputPath);
        Tasks.Remove(task);
    }

    private void ResetTask(MediaTask task)
    {
        task.State = ConversionState.Waiting;
        task.Progress = 0;
        task.Speed = "—";
        task.Remaining = "—";
        task.OutputPath = string.Empty;
        task.Message = "等待开始";
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null || string.IsNullOrWhiteSpace(task.OutputPath) || !File.Exists(task.OutputPath))
        {
            ShowInfo("暂无输出文件。", InfoBarSeverity.Warning);
            return;
        }

        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(task.OutputPath);
        Process.Start(startInfo);
    }

    private void CopyErrorButton_Click(object sender, RoutedEventArgs e)
    {
        var task = FindTaskFromSender(sender);
        if (task is null)
        {
            return;
        }
        var package = new DataPackage();
        package.SetText($"{task.FileName}{Environment.NewLine}{task.StateText}: {task.Message}");
        Clipboard.SetContent(package);
        ShowInfo("已复制。", InfoBarSeverity.Success);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }
        var settings = tag == "settings";
        SettingsView.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        ConverterView.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        if (!settings)
        {
            _filter = tag;
            RefreshVisibleTasks();
        }
    }

    private void RefreshVisibleTasks()
    {
        VisibleTasks.Clear();
        foreach (var task in Tasks.Where(MatchesFilter))
        {
            VisibleTasks.Add(task);
        }

        var hasVisibleTasks = VisibleTasks.Count > 0;
        TaskList.Visibility = hasVisibleTasks ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasVisibleTasks ? Visibility.Collapsed : Visibility.Visible;
        EmptyStateTitle.Text = Tasks.Count == 0 ? "还没有转换任务" : "当前分类没有任务";
        EmptyStateText.Text = Tasks.Count == 0
            ? "点击“添加文件”或将文件拖入上方区域"
            : "切换左侧分类，或添加新的文件";
    }

    private bool MatchesFilter(MediaTask task) => _filter switch
    {
        "video" => task.Kind == MediaKind.Video,
        "audio" => task.Kind == MediaKind.Audio,
        "image" => task.Kind == MediaKind.Image,
        _ => true
    };

    private void UpdateQueueSummary()
    {
        var active = Tasks.Count(task => task.State is ConversionState.Probing or ConversionState.Running or ConversionState.Paused);
        QueueSummaryText.Text = Tasks.Count == 0
            ? "拖入文件，选择预设，开始转换"
            : $"{Tasks.Count} 个任务 · 进行中 {active} · 完成 {Tasks.Count(task => task.State == ConversionState.Completed)}";
        CpuStrategyText.Text = PerformanceTuning.DescribeCpuStrategy(_settings);
    }

    private async void OutputModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutputModeCombo.SelectedItem is not string mode)
        {
            return;
        }
        _settings.OutputMode = mode;
        OutputDirectoryButton.IsEnabled = mode == "自定义目录";
        if (!_initializing)
        {
            await _settingsService.SaveAsync(_settings);
        }
    }

    private async void ChooseOutputDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }
        _settings.CustomOutputDirectory = folder.Path;
        OutputDirectoryText.Text = folder.Path;
        await _settingsService.SaveAsync(_settings);
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
        FfmpegPathTextBox.Text = file.Path;
        if (await _ffmpegLocator.ValidatePairAsync(file.Path))
        {
            _ffmpegPath = FfmpegLocator.ResolveSymbolicLink(file.Path);
            _ffprobePath = FfmpegLocator.GetFfprobePath(_ffmpegPath);
            ShowSettingsInfo("FFmpeg 可用。", InfoBarSeverity.Success);
        }
        else
        {
            ShowSettingsInfo("FFmpeg 或 FFprobe 不可用。", InfoBarSeverity.Error);
        }
    }

    private async void DownloadFfmpegButton_Click(object sender, RoutedEventArgs e)
    {
        await DetectFfmpegAsync(showSuccess: true);
    }

    private async void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeCombo.SelectedItem is not string theme)
        {
            return;
        }

        _settings.Theme = theme;
        ApplyTheme();
        await _settingsService.SaveAsync(_settings);
    }

    private async void DetectFfmpegButton_Click(object sender, RoutedEventArgs e) => await DetectFfmpegAsync(showSuccess: true);

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FfmpegPathTextBox.Text) && !await _ffmpegLocator.ValidatePairAsync(FfmpegPathTextBox.Text))
        {
            ShowSettingsInfo("FFmpeg 路径无效。", InfoBarSeverity.Error);
            return;
        }

        _settings.FfmpegPath = FfmpegLocator.ResolveSymbolicLink(FfmpegPathTextBox.Text);
        _settings.ParallelCount = Math.Clamp((int)ParallelCountBox.Value, 1, 4);
        _settings.CpuThreadsPerTask = Math.Clamp((int)CpuThreadsBox.Value, 0, Environment.ProcessorCount);
        _settings.HardwareAcceleration = HardwareToggle.IsOn;
        _settings.Notifications = NotificationToggle.IsOn;
        _settings.NotificationSound = SoundToggle.IsOn;
        _settings.AlwaysMinimizeToTray = TrayToggle.IsOn;
        _settings.Theme = ThemeCombo.SelectedItem as string ?? "跟随系统";
        _ffmpegPath = _settings.FfmpegPath;
        _ffprobePath = FfmpegLocator.GetFfprobePath(_ffmpegPath);
        if (_runOperations.IsEmpty)
        {
            _runGate.Dispose();
            _runGate = new SemaphoreSlim(_settings.ParallelCount, 4);
        }
        ApplyTheme();
        await _settingsService.SaveAsync(_settings);
        ShowSettingsInfo("已保存。", InfoBarSeverity.Success);
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LogService.LogsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", LogService.LogsDirectory) { UseShellExecute = true });
    }

    private static string NormalizeConversionMode(string? mode) => mode switch
    {
        "快速" => "快速",
        "高质量" => "高质量",
        "无损" => "无损",
        _ => FormatCatalog.DefaultConversionMode
    };

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        AppInfoBar.Message = message;
        AppInfoBar.Severity = severity;
        AppInfoBar.IsOpen = true;
    }

    private void ShowSettingsInfo(string message, InfoBarSeverity severity)
    {
        SettingsInfoBar.Message = message;
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.IsOpen = true;
    }

    private bool EnsureFfmpegReady()
    {
        if (!string.IsNullOrWhiteSpace(_ffmpegPath) && !string.IsNullOrWhiteSpace(_ffprobePath))
        {
            return true;
        }
        ShowInfo("请先设置 FFmpeg。", InfoBarSeverity.Error);
        return false;
    }

    private MediaTask? FindTaskFromSender(object sender)
    {
        if (sender is not FrameworkElement element || element.Tag is null)
        {
            return null;
        }
        var id = element.Tag is Guid guid ? guid : Guid.TryParse(element.Tag.ToString(), out var parsed) ? parsed : Guid.Empty;
        return Tasks.FirstOrDefault(task => task.Id == id);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        LogService.WriteApp($"收到窗口关闭事件，allowClose={_allowClose}，运行任务={_runOperations.Count}");
        if (_allowClose)
        {
            return;
        }

        var hasWork = !_runOperations.IsEmpty || Tasks.Any(task => task.State is ConversionState.Running or ConversionState.Paused or ConversionState.Probing);
        if (hasWork || _settings.AlwaysMinimizeToTray)
        {
            args.Cancel = true;
            NativeWindowService.Hide(_windowHandle);
            _trayIcon?.ShowBalloon("格式工厂 WinUI", hasWork ? "转换仍在后台进行。" : "程序已最小化到系统托盘。" );
            return;
        }

        _ = _queueStore.SaveAsync(Tasks);
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private async void TrayIcon_ExitRequested(object? sender, EventArgs e)
    {
        foreach (var cancellation in _taskCancellation.Values)
        {
            cancellation.Cancel();
        }
        _conversionService.CancelAll();
        if (!_runOperations.IsEmpty)
        {
            await Task.WhenAll(_runOperations.Values.ToArray());
        }
        await _queueStore.SaveAsync(Tasks);
        _allowClose = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        LogService.WriteApp("窗口 Closed");
        _trayIcon?.Dispose();
        _runGate.Dispose();
        foreach (var cancellation in _taskCancellation.Values)
        {
            cancellation.Dispose();
        }
    }
}
