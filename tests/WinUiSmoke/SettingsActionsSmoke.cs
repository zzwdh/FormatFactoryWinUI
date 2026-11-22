using System.Reflection;
using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Motion;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private static void DetachSmokeWindowStartup(MainWindow window)
    {
        var root = (Grid)typeof(MainWindow).GetField("RootGrid", InstanceMembers)!.GetValue(window)!;
        var startup = (RoutedEventHandler)typeof(MainWindow).GetMethod("MainWindow_Loaded", InstanceMembers)!
            .CreateDelegate(typeof(RoutedEventHandler), window);
        root.Loaded -= startup;
    }

    // The production motion service has one-window lifetime. Closing our second
    // fixture window shuts it down, so restore the first window's shared runtime
    // and event handlers without attaching its instance handlers a second time.
    private sealed class SettingsActionsMotionScope(MainWindow? primary) : IDisposable
    {
        private static readonly BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        private readonly FieldInfo _animations = typeof(FluentMotion).GetField("_animationsEnabled", StaticMembers)!;
        private readonly FieldInfo _contrast = typeof(FluentMotion).GetField("_highContrast", StaticMembers)!;
        private readonly FieldInfo _active = typeof(FluentMotion).GetField("_windowActive", StaticMembers)!;
        private readonly FieldInfo _preferences = typeof(FluentMotion).GetField("PreferencesChanged", StaticMembers)!;
        private readonly bool _originalAnimations = FluentMotion.AnimationsEnabled;
        private readonly bool _originalContrast = FluentMotion.HighContrast;
        private readonly bool _originalActive = (bool)typeof(FluentMotion).GetField("_windowActive", StaticMembers)!.GetValue(null)!;
        private readonly object? _originalHandlers = typeof(FluentMotion).GetField("PreferencesChanged", StaticMembers)!.GetValue(null);
        private readonly bool _originalEnabled = FluentMotion.IsEnabled;

        public void Dispose()
        {
            if (primary is null) return;
            FluentMotion.Initialize(primary.DispatcherQueue);
            _animations.SetValue(null, _originalAnimations);
            _contrast.SetValue(null, _originalContrast);
            _active.SetValue(null, _originalActive);
            _preferences.SetValue(null, _originalHandlers);
            typeof(MainWindow).GetMethod("ApplyMotionPreferences", InstanceMembers)!.Invoke(primary, null);
            Require(FluentMotion.IsEnabled == _originalEnabled
                && FluentMotion.AnimationsEnabled == _originalAnimations
                && FluentMotion.HighContrast == _originalContrast,
                "The settings-action fixture did not restore the primary window's motion runtime and preferences.");
        }
    }

    private async Task ScrollSettingsActionsIntoViewAsync()
    {
        var settings = (SettingsView)Field("SettingsView")!;
        // Responsive grids finish measuring after the window's SizeChanged pass.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(160);
            settings.PageRoot.UpdateLayout();
            var content = (UIElement)settings.PageRoot.Content;
            var offset = settings.RateAppButton.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point()).Y - 16;
            settings.PageRoot.ChangeView(null, Math.Clamp(offset, 0, settings.PageRoot.ScrollableHeight), null, disableAnimation: true);
        }
        await Task.Delay(160);
        var root = (Grid)Field("RootGrid")!;
        var position = settings.RateAppButton.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
        Require(position.Y >= 0 && position.Y + settings.RateAppButton.ActualHeight <= root.ActualHeight,
            "The settings actions screenshot did not scroll the rating button into view.");
    }

    private static void RequireEngineOptionOwnership(SettingsView settings)
    {
        Require(settings.EngineOptionsExpander.Content is StackPanel, "Advanced engine options have no content panel.");
        var engines = (StackPanel)settings.EngineOptionsExpander.Content;
        foreach (var control in new FrameworkElement[]
        {
            settings.FfmpegPathTextBox, settings.CompatibilityFfmpegPathTextBox, settings.RepairDocxToggle, settings.LibreOfficePathTextBox, settings.CalibrePathTextBox,
            settings.Caj2PdfPathTextBox, settings.MakeMkvPathTextBox, settings.AudioCdRipperPathTextBox,
            settings.ReadomPathTextBox, settings.BrowseFfmpegButton, settings.DetectFfmpegButton,
            settings.BrowseLibreOfficeButton, settings.BrowseCalibreButton, settings.BrowseCajButton,
            settings.DetectDocumentEnginesButton, settings.BrowseMakeMkvButton,
            settings.BrowseAudioCdRipperButton, settings.BrowseReadomButton, settings.DetectOpticalEnginesButton
        })
            Require(ContainsSettingsElement(engines, control), "An engine control escaped the collapsed settings group: " + control.Name);
        Require(ContainsSettingsElement(engines, settings.OpticalEnginesCard),
            "The disc-engine card is no longer owned by the settings engine group.");
        Require(!ContainsSettingsElement(engines, settings.EbookLayoutModeCombo)
            && !ContainsSettingsElement(engines, settings.OcrLanguageTextBox),
            "Common ebook layout and OCR options were hidden with custom engine paths.");
    }

    // Expander content is not always in the visual tree while collapsed. Inspect its
    // real logical controls rather than requiring a loaded or visible test window.
    private static bool ContainsSettingsElement(DependencyObject parent, DependencyObject target)
    {
        if (ReferenceEquals(parent, target)) return true;
        return parent switch
        {
            Panel panel => panel.Children.Any(child => ContainsSettingsElement(child, target)),
            Border { Child: { } child } => ContainsSettingsElement(child, target),
            ContentControl { Content: DependencyObject child } => ContainsSettingsElement(child, target),
            _ => false
        };
    }

    private static string[] SettingsActionEnginePaths(AppSettings settings) =>
    [
        settings.FfmpegPath, settings.CompatibilityFfmpegPath, settings.LibreOfficePath, settings.CalibreEbookConvertPath,
        settings.Caj2PdfPath, settings.MakeMkvPath, settings.AudioCdRipperPath, settings.ReadomPath
    ];

    private static string[] SettingsActionEnginePaths(SettingsDraft settings) =>
    [
        settings.FfmpegPath, settings.CompatibilityFfmpegPath, settings.LibreOfficePath, settings.CalibreEbookConvertPath,
        settings.Caj2PdfPath, settings.MakeMkvPath, settings.AudioCdRipperPath, settings.ReadomPath
    ];

    private async Task CheckStartupSettingsWriteFailureAsync()
    {
        foreach (var lockReadOnly in new[] { true, false })
        await CheckAsync(lockReadOnly
            ? "Read-only settings and queue locks permit durable startup and shutdown"
            : "Read-only settings file does not block engine discovery or queue restoration", async () =>
        {
            using var motionScope = new SettingsActionsMotionScope(_window);
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            var fixture = Path.Combine(_runRoot, "startup-settings-write-failure", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var settingsPath = Path.Combine(fixture, "settings.json");
            var queuePath = Path.Combine(fixture, "queue.json");
            var protectedPath = lockReadOnly ? settingsPath + ".lock" : settingsPath;
            var input = Path.Combine(fixture, "synthetic.docx");
            await File.WriteAllTextAsync(input, "Synthetic paused task; never converted.");
            var settings = new AppSettings { PerformanceDefaultsVersion = lockReadOnly ? 0 : AppSettings.CurrentPerformanceDefaultsVersion, ParallelCount = 9,
                CpuThreadsPerTask = 999, HardwareAcceleration = false, Notifications = false };
            await new SettingsService(settingsPath).SaveAsync(settings);
            var settingsBytes = await File.ReadAllBytesAsync(settingsPath);
            var persistence = new QueueStore(queuePath, () => []);
            await persistence.SaveAsync(new[] { new MediaTask { InputPath = input, SourceFormatId = "docx",
                TargetFormatId = "pdf", ContentKind = ContentKind.Document,
                State = ConversionState.Failed, Message = "Synthetic retained failure." } });
            MainWindow? window = null;
            QueueSession? session = null;
            try
            {
                File.SetAttributes(protectedPath, FileAttributes.ReadOnly);
                if (lockReadOnly) File.SetAttributes(queuePath + ".lock", FileAttributes.ReadOnly);
                var services = new MainWindowServices(new SettingsService(settingsPath), persistence,
                    new LocalImportStorageAccess(),
                    feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)));
                window = new MainWindow(services);
                DetachSmokeWindowStartup(window);
                _window = window;
                session = (QueueSession)Field("_queueSession")!;

                await InvokeTask("InitializeWindowAsync").WaitAsync(TimeSpan.FromSeconds(150));

                Require(Field("_initializing") is false, "An optional settings write blocked initialization.");
                Require(((TaskCompletionSource<bool>)Field("_activationReady")!).Task.IsCompletedSuccessfully,
                    "Activation readiness did not complete after the settings write failure.");
                Require(window.Tasks.Count == 1 && window.Tasks[0].State == ConversionState.Failed,
                    "The durable failed task was lost or started after the settings write failure.");
                Require(((AppSettings)Field("_settings")!).PerformanceDefaultsVersion == AppSettings.CurrentPerformanceDefaultsVersion,
                    "The upgraded runtime defaults were not retained.");
                if (lockReadOnly)
                {
                    Require(Field("_startupSettingsSaveFailure") is null,
                        "The lock requested write access despite only needing exclusive sharing.");
                    var saved = await new SettingsService(settingsPath).LoadAsync();
                    Require(saved.PerformanceDefaultsVersion == AppSettings.CurrentPerformanceDefaultsVersion
                        && saved.ParallelCount == 4, "The upgraded runtime defaults were not actually persisted.");
                    Require((await session.ShutdownAsync()).CanClose,
                        "A read-only queue lock prevented durable shutdown.");
                    Require(File.GetAttributes(protectedPath).HasFlag(FileAttributes.ReadOnly)
                        && File.GetAttributes(queuePath + ".lock").HasFlag(FileAttributes.ReadOnly),
                        "Startup or shutdown modified lock attributes.");
                }
                else
                {
                    var failure = Field("_startupSettingsSaveFailure") as UnauthorizedAccessException;
                    Require(failure is not null, "The actual settings-file write failure was not retained.");
                    Invoke("EnsureSettingsViewLoaded");
                    var settingsView = (SettingsView)Field("SettingsView")!;
                    Require(settingsView.SettingsInfoBar.IsOpen
                        && settingsView.SettingsInfoBar.Severity == InfoBarSeverity.Warning
                        && settingsView.SettingsInfoBar.Message == LocalizationService.T("ConversionUiSettingsSaveFailedDetails", failure!.Message),
                        "The settings write failure was not visibly reported as a warning.");
                    Require(settingsView.ViewModel.SaveStateKey == "ConversionUiSettingsSaveFailed",
                        "Startup reported unsaved runtime defaults as saved.");
                    var retainedSettingsBytes = await File.ReadAllBytesAsync(settingsPath);
                    Require(settingsBytes.SequenceEqual(retainedSettingsBytes),
                        "The read-only startup path replaced saved settings.");
                }
                Require((await persistence.LoadAsync()).Count == 1, "Startup replaced the durable queue.");
            }
            finally
            {
                try
                {
                    if (session is not null) await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                    window?.Close();
                }
                finally
                {
                    File.SetAttributes(protectedPath, FileAttributes.Normal);
                    if (lockReadOnly) File.SetAttributes(queuePath + ".lock", FileAttributes.Normal);
                    _window = originalWindow;
                    LocalizationService.SetLanguage(originalLanguage);
                    if (_window is not null) Invoke("ApplyLocalization");
                }
            }
        });
    }

    private async Task CheckSettingsActionsAsync()
    {
        await CheckStartupSettingsWriteFailureAsync();
        await CheckAsync("Real settings actions preserve upgraded engines, reset preferences, disable automation and retain queue/files", async () =>
        {
            using var motionScope = new SettingsActionsMotionScope(_window);
            Require(Path.GetFullPath(LogService.AppDataRoot).StartsWith(
                Path.TrimEndingDirectorySeparator(_runRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Settings action smoke checks must use the isolated run data root.");
            var fixture = Path.Combine(_runRoot, "settings-actions", Guid.NewGuid().ToString("N"));
            var input = Path.Combine(fixture, "input");
            var watched = Path.Combine(fixture, "watched");
            var output = Path.Combine(fixture, "output");
            foreach (var directory in new[] { input, watched, output }) Directory.CreateDirectory(directory);
            var pendingSource = Path.Combine(input, "pending-source.png");
            var processedSource = Path.Combine(input, "processed-source.png");
            var retainedOutput = Path.Combine(output, "existing-result.png");
            byte[] sourceBytes = [3, 17, 41, 89];
            byte[] outputBytes = [7, 23, 47, 101];
            await File.WriteAllBytesAsync(pendingSource, sourceBytes);
            await File.WriteAllBytesAsync(processedSource, sourceBytes);
            await File.WriteAllBytesAsync(retainedOutput, outputBytes);
            var settingsPath = Path.Combine(fixture, "settings.json");
            var queuePath = Path.Combine(fixture, "pending-queue.json");
            var automationPath = Path.Combine(LogService.AppDataRoot, "automation.json");
            var originalAutomationBytes = File.Exists(automationPath) ? await File.ReadAllBytesAsync(automationPath) : null;
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            MainWindow? window = null;
            QueueSession? session = null;
            AutomationService? automation = null;
            try
            {
                var persistence = new QueueStore(queuePath, () => []);
                var services = new MainWindowServices(new SettingsService(settingsPath), persistence, new LocalImportStorageAccess());
                window = new MainWindow(services);
                DetachSmokeWindowStartup(window);
                _window = window;
                var modified = new AppSettings
                {
                    FfmpegPath = Path.Combine(fixture, "custom-ffmpeg.exe"),
                    CompatibilityFfmpegPath = Path.Combine(fixture, "compatibility-ffmpeg.exe"),
                    RepairDocxOnImport = true,
                    LibreOfficePath = Path.Combine(fixture, "custom-office.exe"),
                    CalibreEbookConvertPath = Path.Combine(fixture, "custom-ebook.exe"),
                    Caj2PdfPath = Path.Combine(fixture, "custom-caj.exe"),
                    MakeMkvPath = Path.Combine(fixture, "custom-makemkv.exe"),
                    AudioCdRipperPath = Path.Combine(fixture, "custom-ripper.exe"),
                    ReadomPath = Path.Combine(fixture, "custom-readom.exe"),
                    EbookLayoutMode = "fixed", OcrLanguage = "zh-Hans",
                    OutputMode = "source", CustomOutputDirectory = output,
                    ParallelCount = 4, CpuThreadsPerTask = 1, HardwareAcceleration = false,
                    HardwareDecodeTestCompleted = true, PerformanceDefaultsVersion = 1,
                    Notifications = false, NotificationSound = false, AlwaysMinimizeToTray = true,
                    StartWithWindows = true, StartInTray = false,
                    WatchFolderEnabled = true, WatchFolderPath = watched, WatchFolderAccessToken = "isolated-folder-token",
                    Theme = "dark", Language = "ar-SA"
                };
                await services.Settings.SaveAsync(modified);
                SetField("_settings", await services.Settings.LoadAsync());
                SetField("_settingsAvailable", true);
                Invoke("EnsureSettingsViewLoaded");
                Invoke("EnsureSettingsViewLoaded");
                var settingsView = (SettingsView)Field("SettingsView")!;
                var originalPaths = SettingsActionEnginePaths(modified);
                Require(!settingsView.EngineOptionsExpander.IsExpanded,
                    "Loading saved custom engines expanded advanced settings automatically.");
                RequireEngineOptionOwnership(settingsView);
                Require(SettingsActionEnginePaths((AppSettings)Field("_settings")!).SequenceEqual(originalPaths)
                    && SettingsDraft.From(modified) == settingsView.ViewModel.DraftSession!.Current,
                    "Opening collapsed settings changed a previously configured custom engine.");
                settingsView.EngineOptionsExpander.IsExpanded = true;
                LocalizationService.SetLanguage("en-US");
                Invoke("ApplyLocalization");
                Invoke("EnsureSettingsViewLoaded");
                Require(SettingsActionEnginePaths(settingsView.CaptureSettingsDraft()).SequenceEqual(originalPaths),
                    "Localization or repeated settings navigation lost a saved custom engine path.");

                var documentFallbacks = new[] { ExternalToolKind.LibreOffice, ExternalToolKind.Calibre, ExternalToolKind.Caj2Pdf }
                    .Select(kind => new ExternalToolInfo(kind, kind.ToString(), true,
                        Path.Combine(fixture, "detected-" + kind + ".exe"), "smoke", "Available")).ToArray();
                var opticalFallbacks = new[] { OpticalDiscToolKind.MakeMkv, OpticalDiscToolKind.AudioCdRipper, OpticalDiscToolKind.Readom }
                    .Select(kind => new OpticalDiscToolInfo(kind, kind.ToString(), true,
                        Path.Combine(fixture, "detected-" + kind + ".exe"), "smoke", "Available")).ToArray();
                settingsView.RenderDocumentEngineStatus(documentFallbacks, autoDetect: true,
                    modified.LibreOfficePath, modified.CalibreEbookConvertPath, modified.Caj2PdfPath, preserveConfiguredPaths: true);
                settingsView.RenderOpticalEngineStatus(opticalFallbacks, autoDetect: true,
                    modified.MakeMkvPath, modified.AudioCdRipperPath, modified.ReadomPath, preserveConfiguredPaths: true);
                Require(SettingsActionEnginePaths(settingsView.CaptureSettingsDraft()).SequenceEqual(originalPaths),
                    "Startup fallback discovery replaced a previously configured document or disc engine.");
                settingsView.LibreOfficePathTextBox.Text = string.Empty;
                settingsView.RenderDocumentEngineStatus(documentFallbacks, autoDetect: true,
                    string.Empty, modified.CalibreEbookConvertPath, modified.Caj2PdfPath, preserveConfiguredPaths: true);
                Require(settingsView.LibreOfficePathTextBox.Text == documentFallbacks[0].ExecutablePath,
                    "Startup discovery no longer fills an unconfigured engine path.");
                settingsView.LibreOfficePathTextBox.Text = modified.LibreOfficePath;

                var startupCalls = new List<(bool Enabled, bool Tray)>();
                // Exercise the production reset transaction without changing Windows
                // startup registration outside this isolated test application.
                var recordingCommits = new SettingsCommitCoordinator(candidate => services.Settings.SaveAsync(candidate),
                    (enabled, tray) => { startupCalls.Add((enabled, tray)); return Task.CompletedTask; });
                SetField("_settingsCommits", recordingCommits);
                SetField("_initializing", false);
                session = (QueueSession)Field("_queueSession")!;
                var pending = new MediaTask
                {
                    InputPath = pendingSource, Kind = MediaKind.Image, SourceFormatId = "png",
                    TargetFormatId = "png", State = ConversionState.Waiting, FileSize = sourceBytes.Length
                };
                pending.AvailableFormats.Add(FormatCatalog.FindById("png")!.DisplayName);
                Require(session.AddTask(pending), "The isolated pending task was not accepted.");
                await session.SaveAsync();
                var queueBytes = await File.ReadAllBytesAsync(queuePath);
                var rule = new AutomationRule
                {
                    Name = "Retained watch and schedule", FolderPath = input, OutputDirectory = output,
                    Enabled = true, WatchEnabled = true, ScheduleEnabled = true, ScheduleTime = "23:59",
                    InputKind = ContentKind.Image, Extensions = "png", TargetFormatId = "png"
                };
                await services.AutomationStore.SaveRulesAsync((AutomationRule[])[rule]);
                var record = new AutomationProcessedFile(rule.Id, processedSource, FileFingerprintService.Read(processedSource)!,
                    retainedOutput, FileFingerprintService.Read(retainedOutput), DateTimeOffset.UtcNow);
                await services.AutomationStore.RecordSuccessAsync(record);
                await services.AutomationStore.MarkScheduledRunAsync(rule.Id, "2026-10-03");
                var executions = 0;
                automation = new AutomationService(services.AutomationStore, (_, _, _) =>
                {
                    executions++;
                    return Task.FromResult(new AutomationExecutionResult(false, Error: "The disabled fixture must never execute."));
                }, pollInterval: TimeSpan.FromHours(1), settleDelay: TimeSpan.Zero,
                    clock: () => new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.FromHours(8)));
                await automation.ConfigureAsync((AutomationRule[])[rule]);
                await automation.StartAsync();
                await automation.TickAsync();
                await automation.WaitForIdleAsync();
                var beforeAutomation = await services.AutomationStore.LoadAsync();
                SetField("_automationService", automation);
                SetField("_automationRules", (AutomationRule[])[rule]);
                SetField("_automationReady", true);
                var automationView = (AutomationView)Field("AutomationView")!;
                automationView.Initialize((AutomationRule[])[rule], () => Task.FromResult<(string Path, string? AccessToken)?>(null));
                services.Conversions.WatchFolderImport.Configure(modified, () => string.Empty);
                Require(services.Conversions.WatchFolderImport.IsWatching, "The fixture did not start the real legacy file watcher.");

                pending.State = ConversionState.Running;
                Require(!await (Task<bool>)Invoke("RestoreDefaultSettingsAsync")!,
                    "Reset was allowed while a queue row reported an active conversion.");
                Require((await services.Settings.LoadAsync()).Theme == "dark" && automation.Rules.Single().Enabled
                    && startupCalls.Count == 0, "A rejected busy reset changed preferences or automation.");
                pending.State = ConversionState.Waiting;
                // Finish the initial polling pass before exercising the idle guard,
                // and checkpoint the restored row after the deliberate busy probe.
                await automation.TickAsync();
                await automation.WaitForIdleAsync();
                await session.SaveAsync();
                queueBytes = await File.ReadAllBytesAsync(queuePath);
                var settingsBytesBeforeFailure = await File.ReadAllBytesAsync(settingsPath);
                var runtimeBeforeFailure = ((AppSettings)Field("_settings")!).Snapshot();
                var failedStartupCalls = new List<(bool Enabled, bool Tray)>();
                SetField("_settingsCommits", new SettingsCommitCoordinator(
                    _ => Task.FromException(new IOException("Isolated settings persistence failure.")),
                    (enabled, tray) => { failedStartupCalls.Add((enabled, tray)); return Task.CompletedTask; }));
                Require(!await (Task<bool>)Invoke("RestoreDefaultSettingsAsync")!,
                    "The reset reported success after the settings persistence failure.");
                var rolledBackAutomation = await services.AutomationStore.LoadAsync();
                var runtimeAfterFailure = (AppSettings)Field("_settings")!;
                var settingsAfterFailureBytes = await File.ReadAllBytesAsync(settingsPath);
                await File.WriteAllTextAsync(Path.Combine(fixture, "reset-rollback-diagnostics.json"), JsonSerializer.Serialize(new
                {
                    runtimeBeforeFailure, runtimeAfterFailure,
                    persistedBefore = JsonSerializer.Deserialize<AppSettings>(settingsBytesBeforeFailure),
                    persistedAfter = JsonSerializer.Deserialize<AppSettings>(settingsAfterFailureBytes),
                    rulesBefore = beforeAutomation, rulesAfter = rolledBackAutomation,
                    runtimeRules = automation.Rules, displayedRules = automationView.Rules,
                    watchingAfter = services.Conversions.WatchFolderImport.IsWatching,
                    failedStartupCalls = failedStartupCalls.Select(call => new { call.Enabled, call.Tray }).ToArray(),
                    feedback = settingsView.SettingsInfoBar.Message
                }, new JsonSerializerOptions { WriteIndented = true }));
                Require(JsonSerializer.Serialize(runtimeAfterFailure) == JsonSerializer.Serialize(runtimeBeforeFailure),
                    "Failed reset changed runtime preferences: " + JsonSerializer.Serialize(runtimeAfterFailure));
                Require(settingsAfterFailureBytes.SequenceEqual(settingsBytesBeforeFailure),
                    "Failed reset changed the settings file bytes.");
                Require(rolledBackAutomation.Rules.Count == 1 && rolledBackAutomation.Rules[0].Id == rule.Id
                    && rolledBackAutomation.Rules[0].Enabled,
                    "Failed reset left the persisted rule disabled: " + JsonSerializer.Serialize(rolledBackAutomation.Rules));
                Require(automation.Rules.Count == 1 && automation.Rules[0].Id == rule.Id && automation.Rules[0].Enabled,
                    "Failed reset left the runtime rule disabled: " + JsonSerializer.Serialize(automation.Rules));
                Require(automationView.Rules.Count == 1 && automationView.Rules[0].Id == rule.Id && automationView.Rules[0].Enabled,
                    "Failed reset changed the displayed rule: " + JsonSerializer.Serialize(automationView.Rules));
                Require(services.Conversions.WatchFolderImport.IsWatching, "Failed reset stopped the old watched-folder service.");
                Require(JsonSerializer.Serialize(rolledBackAutomation.Records) == JsonSerializer.Serialize(beforeAutomation.Records),
                    "Failed reset changed the completed-file records.");
                Require(rolledBackAutomation.ScheduleRuns.Count == beforeAutomation.ScheduleRuns.Count
                    && beforeAutomation.ScheduleRuns.All(pair => rolledBackAutomation.ScheduleRuns.GetValueOrDefault(pair.Key) == pair.Value),
                    "Failed reset changed scheduled-scan checkpoints.");
                Require(failedStartupCalls.SequenceEqual(new[] { (false, true), (true, false) }),
                    "Failed reset did not restore startup through both callbacks. Actual: "
                    + string.Join(", ", failedStartupCalls.Select(call => $"({call.Enabled}, {call.Tray})"))
                    + "; feedback: " + settingsView.SettingsInfoBar.Message);
                Require(settingsView.SettingsInfoBar.IsOpen && settingsView.SettingsInfoBar.Severity == InfoBarSeverity.Error
                    && settingsView.EngineOptionsExpander.IsExpanded && session.Tasks.Count == 1,
                    "Failed reset changed the advanced group, pending queue or error feedback.");
                SetField("_settingsCommits", recordingCommits);
                Require(await (Task<bool>)Invoke("RestoreDefaultSettingsAsync")!,
                    "The idle production reset did not complete: " + settingsView.SettingsInfoBar.Message);
                var expected = AppSettings.CreateDefaultsPreservingFolders(modified);
                var runtimeSettings = (AppSettings)Field("_settings")!;
                Require(JsonSerializer.Serialize(runtimeSettings) == JsonSerializer.Serialize(expected)
                    && JsonSerializer.Serialize(await services.Settings.LoadAsync()) == JsonSerializer.Serialize(expected),
                    "The runtime or reloaded settings do not match the complete defaults with retained watched-folder access.");
                Require(startupCalls.SequenceEqual(new[] { (false, true) }),
                    "The reset did not disable startup through its configured transaction.");
                Require(!settingsView.EngineOptionsExpander.IsExpanded
                    && settingsView.ViewModel.DraftSession is { IsDirty: false } draft
                    && draft.Current == SettingsDraft.From(expected) && draft.Baseline == draft.Current,
                    "The reset left the advanced group open or retained an old settings draft.");
                Require(SettingsActionEnginePaths(runtimeSettings).All(string.IsNullOrEmpty)
                    && string.IsNullOrEmpty(settingsView.FfmpegPathTextBox.Text)
                    && string.IsNullOrEmpty(settingsView.LibreOfficePathTextBox.Text)
                    && string.IsNullOrEmpty(settingsView.CalibrePathTextBox.Text)
                    && string.IsNullOrEmpty(settingsView.Caj2PdfPathTextBox.Text)
                    && string.IsNullOrEmpty(settingsView.MakeMkvPathTextBox.Text)
                    && string.IsNullOrEmpty(settingsView.AudioCdRipperPathTextBox.Text)
                    && string.IsNullOrEmpty(settingsView.ReadomPathTextBox.Text),
                    "A custom engine override survived the reset in settings or controls.");
                var converter = (QueueView)Field("ConverterView")!;
                Require(((Grid)Field("RootGrid")!).RequestedTheme == ElementTheme.Default
                    && converter.OutputModeCombo.SelectedIndex == 0
                    && converter.OutputDirectoryText.Text == expected.CustomOutputDirectory
                    && settingsView.ParallelCountBox.Value == expected.ParallelCount
                    && settingsView.HardwareToggle.IsOn && !settingsView.WatchFolderToggle.IsOn
                    && (int)typeof(ConversionQueueCoordinator).GetField("_desiredParallelism", InstanceMembers)!
                        .GetValue(services.Conversions.QueueCoordinator)! == PerformanceTuning.ResolveParallelCount(expected, services.PowerState.Current)
                    && (string?)Field("_ffmpegPath") != modified.FfmpegPath,
                    "Restored preferences did not reach the real theme, output controls, watcher or FFmpeg context.");
                Require(!services.Conversions.WatchFolderImport.IsWatching,
                    "The old watched-folder service remained active after reset.");
                var retainedAutomation = await services.AutomationStore.LoadAsync();
                var retainedRule = retainedAutomation.Rules.Single();
                Require(retainedRule.Id == rule.Id && !retainedRule.Enabled && retainedRule.WatchEnabled && retainedRule.ScheduleEnabled
                    && retainedRule.FolderPath == rule.FolderPath && retainedRule.OutputDirectory == rule.OutputDirectory
                    && !automation.Rules.Single().Enabled && !automationView.Rules.Single().Enabled
                    && JsonSerializer.Serialize(retainedAutomation.Records) == JsonSerializer.Serialize(beforeAutomation.Records)
                    && retainedAutomation.ScheduleRuns.Count == beforeAutomation.ScheduleRuns.Count
                    && beforeAutomation.ScheduleRuns.All(pair => retainedAutomation.ScheduleRuns.GetValueOrDefault(pair.Key) == pair.Value),
                    "Reset deleted a rule, changed its trigger preferences, or lost successful/scheduled scan records.");
                await File.WriteAllBytesAsync(Path.Combine(input, "new-after-reset.png"), sourceBytes);
                await automation.TickAsync();
                await automation.WaitForIdleAsync();
                Require(executions == 0 && session.Tasks.Count == 1 && ReferenceEquals(session.Tasks[0], pending)
                    && window.Tasks.Count == 1 && ReferenceEquals(window.Tasks[0].Model, pending)
                    && (await ReadSettingsActionQueueSnapshotAsync(queuePath)).SequenceEqual(queueBytes)
                    && (await File.ReadAllBytesAsync(pendingSource)).SequenceEqual(sourceBytes)
                    && (await File.ReadAllBytesAsync(retainedOutput)).SequenceEqual(outputBytes),
                    "Reset started disabled automation, replaced the pending queue, or changed retained media.");

                foreach (var action in new[] { "ResetSettingsButton", "RateAppButton" })
                {
                    var handlers = typeof(SettingsView).GetField(action + "_ClickRequested", InstanceMembers)?.GetValue(settingsView) as Delegate;
                    Require(handlers?.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, window)
                        && handler.Method.Name == action + "_Click") == true,
                        "The settings button is not connected to the window action: " + action);
                }
                Require(!StoreReviewService.IsStoreInstalled, "The smoke harness unexpectedly has a Store identity; do not request a review.");
                typeof(SettingsView).GetMethod("RateAppButton_Click", InstanceMembers)!
                    .Invoke(settingsView, [settingsView.RateAppButton, new RoutedEventArgs()]);
                Require(settingsView.SettingsInfoBar.IsOpen
                    && settingsView.SettingsInfoBar.Severity == InfoBarSeverity.Informational
                    && settingsView.SettingsInfoBar.Message == LocalizationService.T("SettingsActionsRateStoreOnly"),
                    "The development-build rating button did not show the Store-installation hint.");
            }
            finally
            {
                try
                {
                    if (automation is not null)
                    {
                        await automation.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        await automation.DisposeAsync();
                        if (window is not null) SetField("_automationService", null);
                    }
                    if (session is not null) await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                    window?.Close();
                }
                finally
                {
                    _window = originalWindow;
                    if (originalAutomationBytes is null) File.Delete(automationPath);
                    else await File.WriteAllBytesAsync(automationPath, originalAutomationBytes);
                    LocalizationService.SetLanguage(originalLanguage);
                    if (_window is not null) Invoke("ApplyLocalization");
                }
            }
        });
    }

    private static async Task<byte[]> ReadSettingsActionQueueSnapshotAsync(string path)
    {
        // The production queue replaces complete snapshots in the background. Allow replacement
        // and wait through its short commit window without weakening the byte-preservation check.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                using var copy = new MemoryStream();
                await source.CopyToAsync(copy);
                return copy.ToArray();
            }
            catch (IOException) when (attempt < 4) { await Task.Delay(25); }
        }
    }
    private async Task CheckBundledInstallerCancellationAsync()
    {
        foreach (var scenario in new[] { "cancel-async", "cancel-sync", "access-denied", "integrity", "other-cancellation", "io-same-code", "closed-cancel", "closed-denied" })
        await CheckAsync("Bundled installer: " + scenario, async () =>
        {
            using var motionScope = new SettingsActionsMotionScope(_window);
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            var fixture = Path.Combine(_runRoot, "installer-cancellation", scenario);
            Directory.CreateDirectory(fixture);
            var settingsPath = Path.Combine(fixture, "settings.json");
            var queuePath = Path.Combine(fixture, "queue.json");
            var settingsService = new SettingsService(settingsPath);
            await settingsService.SaveAsync(new AppSettings { ParallelCount = 2, WatchFolderEnabled = false });
            var settingsBytes = await File.ReadAllBytesAsync(settingsPath);
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reported = new List<Exception>();
            Action<Exception> observe = reported.Add;
            var calls = 0;
            var cancelled = scenario is "cancel-async" or "cancel-sync" or "closed-cancel";
            var closed = scenario.StartsWith("closed-", StringComparison.Ordinal);
            Exception failure = scenario switch
            {
                "access-denied" or "closed-denied" => new System.ComponentModel.Win32Exception(5, "Synthetic installation access denied."),
                "integrity" => new InvalidDataException("Synthetic invalid installer digest."),
                "other-cancellation" => new OperationCanceledException("Synthetic unrelated cancellation."),
                "io-same-code" => new IOException("Synthetic storage failure with a matching numeric code.", 1223),
                _ => new System.ComponentModel.Win32Exception(1223, "Synthetic installer launch cancelled.")
            };
            MainWindow? window = null;
            QueueSession? session = null;
            var feedbackBefore = Volatile.Read(ref _feedbackHttpRequests);
            try
            {
                var services = new MainWindowServices(settingsService, new QueueStore(queuePath, () => []),
                    new LocalImportStorageAccess(),
                    feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)),
                    shell: new WindowShellServices(installMakeMkv: () =>
                    {
                        calls++;
                        if (calls > 1) return Task.CompletedTask;
                        if (scenario == "cancel-sync") throw failure;
                        return pending.Task;
                    }));
                window = new MainWindow(services);
                DetachSmokeWindowStartup(window);
                _window = window;
                SetField("_settings", await settingsService.LoadAsync());
                SetField("_settingsAvailable", true);
                Invoke("EnsureSettingsViewLoaded");
                SetField("_initializing", false);
                LocalizationService.SetLanguage(scenario == "cancel-sync" ? "it-IT" : "en-US");
                Invoke("ApplyLocalization");
                var settings = (SettingsView)Field("SettingsView")!;
                settings.ParallelCountBox.Value = 1;
                var draft = settings.ViewModel.DraftSession!;
                var edited = settings.CaptureSettingsDraft();
                Require(draft.IsDirty, "The installer fixture did not create an unsaved settings draft.");
                var runtime = JsonSerializer.Serialize((AppSettings)Field("_settings")!);
                session = (QueueSession)Field("_queueSession")!;
                var input = Path.Combine(fixture, "pending.txt");
                await File.WriteAllTextAsync(input, "Synthetic retained input; never converted.");
                var inputBytes = await File.ReadAllBytesAsync(input);
                var task = new MediaTask { InputPath = input, SourceFormatId = "txt", TargetFormatId = "pdf",
                    ContentKind = ContentKind.Document, State = ConversionState.Waiting };
                Require(session.AddTask(task), "The pending task was not accepted.");
                await session.SaveAsync();
                var queueBytes = await ReadSettingsActionQueueSnapshotAsync(queuePath);
                var relay = typeof(SettingsView).GetMethod("InstallMakeMkvButton_Click", InstanceMembers)!;
                var handler = typeof(MainWindow).GetMethod("InstallMakeMkvButton_Click", InstanceMembers)!;
                var subscribers = typeof(SettingsView).GetField("InstallMakeMkvButton_ClickRequested", InstanceMembers)!.GetValue(settings) as Delegate;
                Require(subscribers?.GetInvocationList().Any(callback => ReferenceEquals(callback.Target, window)
                    && callback.Method == handler) == true, "The production install button callback is disconnected.");
                SafeDiagnostics.ErrorReported += observe;
                Click();
                Require(calls == 1, "The button did not invoke exactly one installation attempt.");
                if (scenario != "cancel-sync")
                {
                    Require(!settings.InstallMakeMkvButton.IsEnabled, "The installer button remained enabled while awaiting installation.");
                    if (closed)
                    {
                        await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                        window.Close();
                        Require(Field("_windowClosed") is true, "The fixture window was not actually closed.");
                    }
                    pending.SetException(failure);
                }
                if (closed) await Task.Delay(150);
                else await WaitForInstallerSmokeAsync(() => settings.InstallMakeMkvButton.IsEnabled);
                Require(reported.Count == (cancelled ? 0 : 1)
                    && (cancelled || ReferenceEquals(reported[0], failure)),
                    "Installer cancellation was reported as an error, or a genuine error was hidden/replaced/reported twice.");
                if (closed)
                    Require(!settings.SettingsInfoBar.IsOpen && !settings.InstallMakeMkvButton.IsEnabled,
                        "An installer completion updated UI after the window closed.");
                else if (cancelled)
                    Require(settings.SettingsInfoBar.IsOpen && settings.SettingsInfoBar.Severity == InfoBarSeverity.Informational
                        && settings.SettingsInfoBar.Message == LocalizationService.T("Cancelled"),
                        "Installer cancellation did not show the localized cancellation state.");
                else
                    Require(settings.SettingsInfoBar.IsOpen && settings.SettingsInfoBar.Severity == InfoBarSeverity.Error
                        && settings.SettingsInfoBar.Message == LocalizationService.T("ConversionUiSettingsApplyFailed", failure.Message),
                        "A genuine installer failure lost its visible error state.");
                Require(ReferenceEquals(settings.ViewModel.DraftSession, draft) && draft.IsDirty && draft.Current == edited
                    && settings.CaptureSettingsDraft() == edited && JsonSerializer.Serialize((AppSettings)Field("_settings")!) == runtime,
                    "The installer completion replaced the draft or changed runtime settings.");
                Require(session.Tasks.Count == 1 && ReferenceEquals(session.Tasks[0], task) && task.State == ConversionState.Waiting
                    && (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(settingsBytes)
                    && (await ReadSettingsActionQueueSnapshotAsync(queuePath)).SequenceEqual(queueBytes)
                    && (await File.ReadAllBytesAsync(input)).SequenceEqual(inputBytes),
                    "The installer completion changed pending conversion, settings, or source data.");
                if (scenario == "cancel-async")
                {
                    Click();
                    await WaitForInstallerSmokeAsync(() => settings.InstallMakeMkvButton.IsEnabled);
                    Require(calls == 2 && reported.Count == 0, "A cancelled installation could not be retried normally.");
                }
                Require(Volatile.Read(ref _feedbackHttpRequests) == feedbackBefore, "The isolated installer check sent remote feedback.");
                void Click() => relay.Invoke(settings, new object[] { settings.InstallMakeMkvButton, new RoutedEventArgs() });
            }
            finally
            {
                SafeDiagnostics.ErrorReported -= observe;
                try
                {
                    if (window is not null && ReadWindowMember(window, "_windowClosed") is false)
                    {
                        if (session is not null) await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                        window.Close();
                    }
                }
                finally
                {
                    _window = originalWindow;
                    LocalizationService.SetLanguage(originalLanguage);
                    if (_window is not null) Invoke("ApplyLocalization");
                }
            }
        });
    }

    private static async Task WaitForInstallerSmokeAsync(Func<bool> completed)
    {
        for (var attempt = 0; attempt < 200 && !completed(); attempt++) await Task.Delay(25);
        Require(completed(), "The installer UI callback did not complete within five seconds.");
    }
}
