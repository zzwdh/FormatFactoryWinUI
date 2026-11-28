using System.Collections.Specialized;
using System.Runtime.InteropServices.WindowsRuntime;
using FormatFactoryWinUI.Controls;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Motion;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Xml.Linq;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

// Compiled with the real application XAML in a temporary project by Run.ps1.
// The production App startup is replaced so single-instance activation, user data,
// tray registration, and automatic conversions cannot affect this regression run.
public partial class App : Application
{
    private readonly List<object> _results = [];
    private readonly string _runRoot = Path.GetFullPath(
        Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_ROOT")
        ?? throw new InvalidOperationException("Run this harness with tests/WinUiSmoke/Run.ps1."));
    private int _failures;
    private MainWindow? _window;
    private bool _finishing;
    private bool? _loadedMotionWasEnabled;
    private bool? _originalAnimationPreference;
    private bool? _originalHighContrastPreference;
    private bool? _testMotionPreferencesRestored;
    private bool _asynchronousFadeObserved;
    private readonly bool _capture = Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_CAPTURE") == "1";
    private readonly bool _motionOnly = Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_MOTION_ONLY") == "1";

    public App()
    {
        Directory.CreateDirectory(_runRoot);
        LogService.ConfigureAppDataRoot(Path.Combine(_runRoot, "data"));
        // Match the recorded benchmark baseline independently of the desktop theme.
        // Set this before creating any XAML; production and ordinary smoke keep their theme.
        if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_PERFORMANCE") == "1")
            RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            RecordFailure("Unhandled UI exception", e.Exception);
            e.Handled = true;
            Finish();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_PERFORMANCE") == "1")
            {
                await RunPerformanceAsync();
                return;
            }
            if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_STARTUP") is { Length: > 0 } startupCount)
            {
                await RunStartupAsync(int.Parse(startupCount));
                return;
            }
            _window = new MainWindow(new MainWindowServices(new SettingsService(),
                feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this))));
            DetachSmokeWindowStartup(_window);
            if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_INSTALLER_ONLY") == "1")
            {
                await CheckBundledInstallerCancellationAsync();
                return;
            }
            if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_ARCHIVE_ONLY") == "1")
            {
                await CheckArchiveConversionAsync();
                return;
            }
            if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_PASSWORD_ONLY") == "1")
            {
                await CheckPdfPasswordIdentityAsync();
                return;
            }
            Check("Settings controls exist while draft initialization is deferred", () =>
            {
                Require(Field("SettingsView") is FrameworkElement, "SettingsView was not created.");
                Require(Field("CpuStrategyText") is TextBlock, "CpuStrategyText was not created.");
                Require(Field("_settingsViewLoaded") is false, "The loaded flag was set too early.");
                Require(Field("_settingsDraftSession") is null, "Settings draft was initialized too early.");
            });
            Check("Startup localization before settings exists", () => Invoke("ApplyLocalization"));
            Check("Queue refresh before settings exists", () => Invoke("UpdateQueueSummary"));
            Check("Language change before the first settings visit", () =>
            {
                LocalizationService.SetLanguage("en-US");
                Invoke("ApplyLocalization");
                Require(Field("_settingsViewLoaded") is false, "Localization initialized settings too early.");
            });
            Check("First settings visit realizes every named control", () =>
            {
                SetField("_settingsAvailable", true);
                Invoke("EnsureSettingsViewLoaded");
                Require(Field("_settingsViewLoaded") is true, "Settings did not report completion.");
                Require(Field("SettingsView") is FrameworkElement, "SettingsView is still null.");
                Require(MissingSettingsControls().Length == 0,
                    "Unrealized settings controls: " + string.Join(", ", MissingSettingsControls()));
                Require(Field("_settingsDraftSession") is not null, "Settings draft was not initialized.");
                Require(!string.IsNullOrWhiteSpace(((TextBlock)Field("CpuStrategyText")!).Text),
                    "CPU strategy was not populated on first visit.");
            });
            Check("Repeated settings visits retain controls and draft", () =>
            {
                var settingsView = Field("SettingsView");
                var draft = Field("_settingsDraftSession");
                Require(settingsView is not null, "SettingsView is null before the second visit.");
                for (var i = 0; i < 3; i++) Invoke("EnsureSettingsViewLoaded");
                Require(ReferenceEquals(settingsView, Field("SettingsView")), "Settings subtree was recreated.");
                Require(ReferenceEquals(draft, Field("_settingsDraftSession")), "Settings draft was replaced.");
            });
            Check("Language changes after settings loads update settings and queue", () =>
            {
                foreach (var language in LocalizationService.LanguageOptions.Where(option => option.Key != "auto").Select(option => option.Key))
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    Invoke("UpdateQueueSummary");
                    Require(((TextBlock)Field("SettingsTitleText")!).Text == LocalizationService.T("SettingsTitle"),
                        $"Settings title was not localized for {language}.");
                    Require(!string.IsNullOrWhiteSpace(((TextBlock)Field("CpuStrategyText")!).Text),
                        $"CPU strategy was empty for {language}.");
                    var root = (Grid)Field("RootGrid")!;
                    Require(root.Language == LocalizationService.CurrentUICulture.Name,
                        $"Root language was not updated for {language}.");
                    Require(root.FlowDirection == (LocalizationService.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight),
                        $"Reading direction was not updated for {language}.");
                    var queue = (Views.QueueView)Field("ConverterView")!;
                    Require(queue.QueueTitleText.Text == LocalizationService.T("QueueTitle"),
                        $"Queue title was not localized for {language}.");
                    var history = (Views.HistoryView)Field("HistoryView")!;
                    Require(history.HistoryTitleText.Text == LocalizationService.T("HistoryTitle"),
                        $"History title was not localized for {language}.");
                    var settings = (Views.SettingsView)Field("SettingsView")!;
                    Require(settings.LanguageCombo.Items.Count == LocalizationService.LanguageOptions.Count,
                        $"Language picker omitted choices for {language}.");
                    Require(AutomationProperties.GetLabeledBy(settings.LanguageCombo) is TextBlock title &&
                        title.Text == LocalizationService.T("Language"), $"Language picker label was not localized for {language}.");
                    foreach (var toggle in new[] { settings.HardwareToggle, settings.NotificationToggle,
                        settings.SoundToggle, settings.TrayToggle, settings.StartWithWindowsToggle,
                        settings.StartInTrayToggle, settings.WatchFolderToggle, settings.RepairDocxToggle,
                        settings.UsageTelemetryToggle, settings.AutomaticErrorReportingToggle })
                    {
                        Require((string)toggle.OnContent == LocalizationService.T("HardwareOn") &&
                            (string)toggle.OffContent == LocalizationService.T("HardwareOff"),
                            $"Toggle state labels were not localized for {language}.");
                    }
                }
                LocalizationService.SetLanguage("zh-Hans");
                Invoke("ApplyLocalization");
            });
            Check("Page localization and repeat visits preserve edited settings", () =>
            {
                var draft = (SettingsDraftSession)Field("_settingsDraftSession")!;
                var threads = (NumberBox)Field("CpuThreadsBox")!;
                threads.Value = threads.Value == 1 ? 0 : 1;
                var edited = draft.Current;
                Require(draft.IsDirty, "Editing the page did not reach its draft session.");
                LocalizationService.SetLanguage("en-US");
                Invoke("ApplyLocalization");
                Invoke("EnsureSettingsViewLoaded");
                Require(ReferenceEquals(draft, Field("_settingsDraftSession")) && draft.Current == edited,
                    "Localization or a repeat visit replaced the edited settings draft.");
            });
            Check("Disc and archive conversion have direct navigation and retain the settings draft", () =>
            {
                var settings = (Views.SettingsView)Field("SettingsView")!;
                var draft = settings.ViewModel.DraftSession;
                Require(!settings.EngineOptionsExpander.IsExpanded, "Advanced engine options were expanded on the first visit.");
                RequireEngineOptionOwnership(settings);
                var navigation = (NavigationView)Field("NavView")!;
                foreach (var tag in new[] { "disc", "archive" })
                {
                    Require(navigation.MenuItems.Contains(FindNavigationItem(tag)),
                        $"{tag} is missing from the main navigation.");
                    Invoke("NavigateWithMotion", tag);
                    RequireNavigation(tag);
                }
                Require(!navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>()
                    .SelectMany(FlattenNavigationItem).Any(item => Equals(item.Tag, "advanced")),
                    "The obsolete advanced tools entry is still exposed.");
                Require(settings.FindName("WindowsNativeCard") is null,
                    "The removed Windows tools card is still present in settings.");
                Invoke("NavigateWithMotion", "settings");
                Require(ReferenceEquals(draft, settings.ViewModel.DraftSession), "Conversion navigation replaced the draft.");
                RequireEngineOptionOwnership(settings);
            });
            if (_motionOnly)
            {
                RunMotionChecks();
                await CaptureAndCheckLoadedWindowAsync();
                await CheckClosedWindowCallbacksAsync();
                return;
            }
            await CheckSettingsActionsAsync();
            await CheckBundledInstallerCancellationAsync();
            await CheckSettingsValidationDiagnosticsAsync();
            await CheckOpenLogsActionsAsync();
            await CheckOpenOutputLocationsAsync();
            await CheckRestoredAudioTracksAsync();
            await CheckPdfPasswordIdentityAsync();
            await CheckOutputFolderPickerAsync();
            await CheckCompatibleAudioTrackAsync();
            await CheckFeedbackActionsAsync();
            await CheckTelemetryPreferencesAsync();
            await CheckArchiveConversionAsync();
            await CheckTaskExperienceAsync();
            await CheckFailureActionsAsync();
            await CheckStoreUpdatePreferencesAsync();
            await RunDesktopUpgradeChecksAsync();
            await CheckTrayNavigationAsync();
            await CheckQueueExperienceAsync();
            RunMotionChecks();
            RunAutomationChecks();
            await CheckClipboardAsync();
            await CheckNativeWorkflowAsync();
            await CheckPdfOcrDimensionsAsync();
            await CheckPdfDocxAsync();
            await CheckDocumentCompatibilityAsync();
            await CheckScanPrintWorkflowAsync();
            if (_capture) await CaptureAndCheckLoadedWindowAsync();
            await CheckConversionOptionsAsync();
            await CheckImportExperienceAsync();
            await CheckResultSummaryAsync();
            await RunNamingPreviewChecksAsync();
            await CheckQueueSessionClosingImportAsync();
            await CheckDiscRouteRecoveryAsync();
            await CheckEpubRouteAvailabilityAsync();
            await CheckEpubActivationTargetsAsync();
            await CheckClosedWindowCallbacksAsync();
        }
        catch (Exception exception)
        {
            RecordFailure("Harness setup", exception);
        }
        finally
        {
            Finish();
        }
    }

    private async Task CheckClosedWindowCallbacksAsync()
    {
        const string name = "Late dispatcher and progress callbacks are inert after window close";
        try
        {
            var window = _window ?? throw new InvalidOperationException("The smoke window was not created.");
            var progressTask = CreateSampleTask(999);
            window.Close();
            _window = null;
            Require(ReadWindowMember(window, "_windowClosed") is true, "Closing the window did not publish its close state.");

            var dispatcherMethod = typeof(MainWindow).GetMethod("RunOnUiAsync", InstanceMembers)
                ?? throw new InvalidOperationException("The UI dispatch method is missing.");
            var actionRan = false;
            Task? dispatched = null;
            await Task.Run(() => dispatched = (Task)dispatcherMethod.Invoke(window,
                [new Func<Task>(() => { actionRan = true; return Task.CompletedTask; })])!);
            await dispatched!.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!actionRan, "A late callback accessed the closed window.");

            var progressMethod = typeof(MainWindow).GetMethod("ReportTaskProgress", InstanceMembers)
                ?? throw new InvalidOperationException("The progress callback method is missing.");
            await Task.Run(() => progressMethod.Invoke(window,
                [progressTask, 0L, 50d, "late", "late", "late"]));
            _results.Add(new { name, passed = true });
        }
        catch (Exception exception)
        {
            RecordFailure(name, exception);
        }
    }

    private void RunMotionChecks()
    {
        // Baseline folders created before the motion upgrade still exercise the
        // original settings lifecycle without requiring newer window members.
        if (typeof(MainWindow).GetMethod("NavigateWithMotion", BindingFlags.Instance | BindingFlags.NonPublic) is null)
        {
            _results.Add(new { name = "Motion checks", skipped = "Baseline predates motion navigation." });
            return;
        }

        Check("Detached animation targets settle without waiting for a compositor", () =>
        {
            var target = new Grid();
            var fade = FluentMotion.FadeOutAsync(target);
            Require(fade.IsCompletedSuccessfully, "A detached fade retained an incomplete awaiter.");
            Require(ElementCompositionPreview.GetElementVisual(target).Opacity == 0,
                "A detached fade did not reach its final visual state.");
            FluentMotion.Entrance(target);
            Require(ElementCompositionPreview.GetElementVisual(target).Opacity == 1,
                "A detached entrance did not restore visible content.");
            FluentMotion.Reset(target);
        });
        Check("Unloaded navigation settles synchronously across categories and pages", () =>
        {
            foreach (var tag in new[] { "video", "disc", "archive", "audio", "settings", "image", "history", "all" })
            {
                Invoke("NavigateWithMotion", tag);
                Require(Field("_navigationTransition") is Task { IsCompletedSuccessfully: true },
                    $"Unloaded {tag} navigation retained an asynchronous transition.");
                RequireNavigation(tag);
            }
        });
        Check("First task replaces the empty state", () =>
        {
            ClearSampleTasks();
            Invoke("NavigateWithMotion", "all");
            RequireEmptyState(true);
            _window!.Tasks.Add(CreateSampleTask(0));
            Invoke("RefreshVisibleTasks");
            RequireEmptyState(false);
            Require(ReferenceEquals(_window.Tasks[0], _window.VisibleTasks.Single()),
                "The first visible card did not preserve its view model.");
        });
        Check("A 100-task batch retains existing cards without collection resets", () =>
        {
            var original = _window!.Tasks[0];
            var changes = new List<NotifyCollectionChangedAction>();
            NotifyCollectionChangedEventHandler handler = (_, args) => changes.Add(args.Action);
            _window.VisibleTasks.CollectionChanged += handler;
            try
            {
                for (var index = 1; index < 100; index++) _window.Tasks.Add(CreateSampleTask(index));
                Invoke("RefreshVisibleTasks");
                Require(_window.VisibleTasks.Count == 100, "The batch did not produce 100 visible tasks.");
                Require(ReferenceEquals(original, _window.VisibleTasks[0]), "The existing card instance was replaced.");
                Require(!changes.Contains(NotifyCollectionChangedAction.Reset), "The batch reset the visible collection.");
                changes.Clear();
                Invoke("RefreshVisibleTasks");
                Require(changes.Count == 0, "Refreshing an unchanged queue produced collection changes.");
            }
            finally { _window.VisibleTasks.CollectionChanged -= handler; }
        });
        Check("Repeated filters preserve task identity and genuine conversion progress", () =>
        {
            var original = _window!.Tasks.ToDictionary(task => task.Id);
            var progress = original.ToDictionary(pair => pair.Key, pair => pair.Value.Progress);
            foreach (var tag in new[] { "video", "audio", "image", "video", "all" })
            {
                Invoke("NavigateWithMotion", tag);
                RequireNavigation(tag);
                Require(_window.VisibleTasks.All(task => ReferenceEquals(original[task.Id], task)),
                    $"{tag} recreated task view models.");
                Require(_window.Tasks.All(task => task.Progress == progress[task.Id]),
                    $"{tag} changed genuine conversion progress.");
            }
        });
        Check("A filtered empty view keeps the queue and removing the last task restores the empty state", () =>
        {
            Invoke("NavigateWithMotion", "document");
            RequireEmptyState(true);
            Require(_window!.Tasks.Count == 100, "A filter removed backing tasks.");
            Require(((TextBlock)Field("EmptyStateTitle")!).Text == LocalizationService.T("EmptyFiltered"),
                "Filtered empty state displayed the new-queue title.");
            Invoke("NavigateWithMotion", "all");
            ClearSampleTasks();
            RequireEmptyState(true);
            Require(((TextBlock)Field("EmptyStateTitle")!).Text == LocalizationService.T("EmptyTitle"),
                "Removing the final task did not restore the new-queue title.");
        });
        Check("Status controls preserve exact progress in every unloaded state", () =>
        {
            var progress = new TaskProgressBar();
            var badge = new TaskStatusBadge();
            var glyphs = new HashSet<string>();
            foreach (var state in Enum.GetValues<ConversionState>())
            {
                progress.State = state;
                progress.Progress = 42.5;
                Require(progress.Progress == 42.5 && progress.Value == 42.5,
                    $"{state} changed the real progress or failed to settle while unloaded.");
                Require(!progress.IsIndeterminate, $"{state} started animation while unloaded.");
                badge.State = state;
                badge.Text = "状态 " + state;
                var grid = badge.Content as Grid ?? throw new InvalidOperationException("Status badge has no content grid.");
                Require(grid.Children.OfType<TextBlock>().Any(text => text.Text == badge.Text),
                    $"{state} did not display the supplied localized text.");
                var glyph = grid.Children.OfType<FontIcon>().Single(icon => icon.Visibility == Visibility.Visible).Glyph;
                Require(glyphs.Add(glyph), $"{state} has no distinct visible state icon.");
                Require(AutomationProperties.GetLiveSetting(grid.Children.OfType<TextBlock>().Single()) == Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite,
                    $"{state} did not expose its status to assistive technology.");
            }
            progress.State = ConversionState.Failed;
            Require(progress.ShowError, "Failed progress did not expose the error state.");
            progress.State = ConversionState.Paused;
            Require(progress.ShowPaused, "Paused progress did not expose the paused state.");
        });
    }

    private MediaTaskViewModel CreateSampleTask(int index)
    {
        var kind = (index % 3) switch { 0 => MediaKind.Video, 1 => MediaKind.Audio, _ => MediaKind.Image };
        var extension = kind switch { MediaKind.Video => "mp4", MediaKind.Audio => "wav", _ => "png" };
        var state = (index % 7) switch
        {
            0 => ConversionState.Running, 1 => ConversionState.Probing, 2 => ConversionState.Paused,
            3 => ConversionState.Completed, 4 => ConversionState.Failed, 5 => ConversionState.Cancelled,
            _ => ConversionState.Waiting
        };
        var model = new MediaTask
        {
            InputPath = Path.Combine(_runRoot, "sample-inputs", $"旅行素材_{index + 1:000}.{extension}"),
            Kind = kind,
            FileSize = 42_000_000 + index * 1_024,
            SourceFormatId = extension,
            TargetFormat = kind switch { MediaKind.Video => "MP4", MediaKind.Audio => "MP3", _ => "JPEG" },
            State = state,
            Progress = state == ConversionState.Completed ? 100 : state is ConversionState.Waiting or ConversionState.Probing ? 0 : 42.5,
            Speed = state == ConversionState.Running ? "2.4x" : "—",
            Remaining = state == ConversionState.Running ? "00:24" : "—"
        };
        foreach (var format in kind switch
        {
            MediaKind.Video => new[] { "MP4", "MKV", "WebM" },
            MediaKind.Audio => new[] { "MP3", "AAC", "FLAC" },
            _ => new[] { "JPEG", "PNG", "WebP" }
        }) model.AvailableFormats.Add(format);
        var task = new MediaTaskViewModel(model);
        if (state == ConversionState.Failed) task.Message = "示例：源文件无法读取，请检查文件后重试。";
        return task;
    }

    private void ClearSampleTasks()
    {
        foreach (var task in _window!.Tasks.ToArray())
        {
            _window.Tasks.Remove(task);
            task.Dispose();
        }
        Invoke("RefreshVisibleTasks");
    }

    private void RequireEmptyState(bool empty)
    {
        Require(((FrameworkElement)Field("EmptyState")!).Visibility == (empty ? Visibility.Visible : Visibility.Collapsed),
            "Empty-state visibility did not match the current collection.");
        Require(((FrameworkElement)Field("TaskList")!).Visibility == (empty ? Visibility.Collapsed : Visibility.Visible),
            "Task-list visibility did not match the current collection.");
    }

    private void RequireNavigation(string tag)
    {
        Require(Equals(Field("_navigationTag"), tag), $"Expected {tag} but a stale navigation won.");
        Require(((FrameworkElement)Field("ConverterView")!).Visibility ==
            (tag is "settings" or "history" or "archive" or "automation" ? Visibility.Collapsed : Visibility.Visible), "Converter visibility is wrong.");
        Require(((FrameworkElement)Field("SettingsView")!).Visibility ==
            (tag == "settings" ? Visibility.Visible : Visibility.Collapsed), "Settings visibility is wrong.");
        Require(((FrameworkElement)Field("HistoryView")!).Visibility ==
            (tag == "history" ? Visibility.Visible : Visibility.Collapsed), "History visibility is wrong.");
        Require(((FrameworkElement)Field("ArchivePage")!).Visibility ==
            (tag == "archive" ? Visibility.Visible : Visibility.Collapsed), "Archive page visibility is wrong.");
        Require(((FrameworkElement)Field("AutomationView")!).Visibility ==
            (tag == "automation" ? Visibility.Visible : Visibility.Collapsed), "Automation visibility is wrong.");
        if (tag is not ("settings" or "history" or "archive" or "automation"))
        {
            Require(Equals(Field("_filter"), tag), "The selected filter is stale.");
            var kind = tag switch { "video" => MediaKind.Video, "audio" => MediaKind.Audio, "image" => MediaKind.Image, _ => (MediaKind?)null };
            if (kind is not null)
                Require(_window!.VisibleTasks.All(task => task.Kind == kind), "The visible list has tasks from another category.");
        }
    }

    private async Task CaptureAndCheckLoadedWindowAsync()
    {
        var root = (Grid)Field("RootGrid")!;
        // Detach only production startup. All real XAML layout and motion handlers
        // remain attached; no engine downloads, tray, queues, or conversions start.
        var startup = (RoutedEventHandler)typeof(MainWindow)
            .GetMethod("MainWindow_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(RoutedEventHandler), _window);
        root.Loaded -= startup;
        // RenderTargetBitmap cannot capture system Mica. Give only this test
        // window the theme's solid fallback behind its otherwise unchanged UI.
        root.Children.Insert(0, (Grid)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  Grid.RowSpan="2"
                  Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                  IsHitTestVisible="False" />
            """));
        ClearSampleTasks();
        for (var index = 0; index < 100; index++) _window!.Tasks.Add(CreateSampleTask(index));
        Invoke("ApplySettingsToControls", true);
        ((TextBlock)Field("OutputDirectoryText")!).Text = Path.Combine(_runRoot, "sample-output");
        Invoke("NavigateWithMotion", "all");
        Invoke("UpdateQueueSummary");
        _window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        _window.Activate();
        await Task.Delay(450);
        Require(root.IsLoaded, "Capture window failed to load.");
        ResizeForCapture(1280, 900);
        await Task.Delay(200);
        // Closing earlier test windows releases the shared motion policy.
        FluentMotion.Initialize(_window.DispatcherQueue);
        using var motionPreferences = new IsolatedMotionPreferenceScope(this);
        _loadedMotionWasEnabled = FluentMotion.IsEnabled;
        SetField("_initializing", false);
        await CheckAsync("Enabled visible Composition fade completes asynchronously", async () =>
        {
            Require(FluentMotion.IsEnabled, "Composition animation was not enabled for this check.");
            var host = (FrameworkElement)Field("TaskContentHost")!;
            Require(IsVisibleWithin(host, (FrameworkElement)Field("ConverterView")!),
                "The Composition target is not loaded and visible.");
            FluentMotion.Reset(host);
            try
            {
                var fade = FluentMotion.FadeOutAsync(host, 167);
                _asynchronousFadeObserved = !fade.IsCompleted;
                Require(_asynchronousFadeObserved,
                    "The enabled, visible fade completed synchronously instead of exercising Composition.");
                await fade.WaitAsync(TimeSpan.FromSeconds(2));
                Require(ElementCompositionPreview.GetElementVisual(host).Opacity == 0,
                    "The Composition fade did not reach its requested final opacity.");
            }
            finally { FluentMotion.Reset(host); }
        });
        await CheckAsync("Loaded Fluent fade-in stays anchored and completes fully visible", async () =>
        {
            var host = (FrameworkElement)Field("TaskContentHost")!;
            Require(FluentMotion.IsEnabled && host.IsLoaded, "Fade-in did not have an enabled, loaded target.");
            FluentMotion.Reset(host);
            try
            {
                FluentMotion.FadeIn(host);
                Require(IsElementMotionRunning(host), "Fade-in did not exercise a live Composition animation.");
                RequireMotionTranslationIsZero(host);
                await WaitForElementMotionAsync(host);
                Require(ElementCompositionPreview.GetElementVisual(host).Opacity == 1,
                    "Fade-in did not leave the loaded content fully visible.");
                RequireMotionTranslationIsZero(host);
            }
            finally { FluentMotion.Reset(host); }
        });
        await CheckAsync("Reversing entrance and exit releases awaiters and preserves terminal states", async () =>
        {
            var host = (FrameworkElement)Field("TaskContentHost")!;
            Require(FluentMotion.IsEnabled, "Reversal did not begin with active animation.");
            FluentMotion.Reset(host);
            try
            {
                FluentMotion.Entrance(host, 1000, 12);
                Require(IsElementMotionRunning(host), "There was no entrance to reverse.");
                var interruptedExit = FluentMotion.FadeOutAsync(host, 1000);
                Require(!interruptedExit.IsCompleted, "There was no exit awaiter to interrupt.");
                FluentMotion.FadeIn(host);
                await interruptedExit.WaitAsync(TimeSpan.FromSeconds(2));
                await WaitForElementMotionAsync(host);
                Require(ElementCompositionPreview.GetElementVisual(host).Opacity == 1,
                    "Reversing an exit to fade-in retained hidden content.");
                RequireMotionTranslationIsZero(host);

                FluentMotion.Entrance(host, 1000, 12);
                await FluentMotion.FadeOutAsync(host).WaitAsync(TimeSpan.FromSeconds(2));
                Require(ElementCompositionPreview.GetElementVisual(host).Opacity == 0,
                    "Reversing an entrance to exit did not reach its hidden state.");
                RequireMotionTranslationIsZero(host);
                FluentMotion.StopAll();
                Require(GetRunningMotions().Count == 0, "StopAll retained an animation or its target.");
            }
            finally { FluentMotion.Reset(host); }
        });
        await CheckAsync("Mixed top-level navigation can reverse incoming fades and settle at the original anchor", async () =>
        {
            Require(FluentMotion.IsEnabled, "Top-level navigation did not start with motion enabled.");
            foreach (var tag in new[] { "settings", "archive", "automation", "history" })
            {
                SelectNavigation(tag);
                await ((Task)Field("_navigationTransition")!).WaitAsync(TimeSpan.FromSeconds(3));
                RequireNavigation(tag);
                var pageName = tag switch
                {
                    "settings" => "SettingsView", "archive" => "ArchivePage",
                    "automation" => "AutomationView", _ => "HistoryView"
                };
                // Start the next route while this destination's incoming fade is
                // still eligible to run; no frame-time or screenshot assumption.
                RequireMotionTranslationIsZero((FrameworkElement)Field(pageName)!);
            }
            var transitions = new List<Task>();
            foreach (var tag in new[] { "settings", "archive", "automation", "history", "all" })
            {
                SelectNavigation(tag);
                transitions.Add((Task)Field("_navigationTransition")!);
            }
            await Task.WhenAll(transitions).WaitAsync(TimeSpan.FromSeconds(3));
            await AwaitNavigationAsync();
            RequireNavigation("all");
            foreach (var pageName in new[] { "ConverterView", "SettingsView", "ArchivePage", "AutomationView", "HistoryView", "TaskContentHost" })
            {
                var page = (FrameworkElement)Field(pageName)!;
                RequireMotionTranslationIsZero(page);
                Require(ElementCompositionPreview.GetElementVisual(page).Opacity == 1,
                    $"The final route retained stale opacity on {pageName}.");
            }
            Require(_window.VisibleTasks.Count == 100, "Mixed navigation lost queued work.");
        });
        await CheckAsync("Loaded rapid navigation settles on the final request", async () =>
        {
            Require(FluentMotion.IsEnabled, "Rapid navigation did not start with animation enabled.");
            var transitions = new List<Task>();
            foreach (var tag in new[] { "video", "audio", "image", "settings", "history", "video", "all" })
            {
                SelectNavigation(tag);
                transitions.Add((Task)Field("_navigationTransition")!);
            }
            Require(transitions.Any(transition => !transition.IsCompleted),
                "Rapid navigation did not exercise an asynchronous transition.");
            await Task.WhenAll(transitions).WaitAsync(TimeSpan.FromSeconds(3));
            await AwaitNavigationAsync();
            RequireNavigation("all");
            Require(_window.VisibleTasks.Count == 100, "The final queue lost tasks.");
        });
        await CheckAsync("Interrupted fades complete their awaiters and restore visible content", async () =>
        {
            var host = (FrameworkElement)Field("TaskContentHost")!;
            Require(FluentMotion.IsEnabled, "Interruption checks did not start with animation enabled.");
            var fade = FluentMotion.FadeOutAsync(host, 1000);
            Require(!fade.IsCompleted, "There was no active fade to interrupt.");
            FluentMotion.Reset(host);
            await fade.WaitAsync(TimeSpan.FromSeconds(2));
            Require(ElementCompositionPreview.GetElementVisual(host).Opacity == 1, "Reset left the page faded.");
            var stopped = FluentMotion.FadeOutAsync(host, 1000);
            FluentMotion.StopAll();
            await stopped.WaitAsync(TimeSpan.FromSeconds(2));
            FluentMotion.Reset(host);
        });
        await CheckAsync("Disabling animation mid-navigation reaches the requested terminal state", async () =>
        {
            var preference = typeof(FluentMotion).GetField("_animationsEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previous = (bool)preference.GetValue(null)!;
            try
            {
                Require(FluentMotion.IsEnabled, "The reduced-motion check did not begin with active animation.");
                SelectNavigation("video");
                Require(Field("_navigationTransition") is Task { IsCompleted: false }, "No navigation animation was active before disabling motion.");
                preference.SetValue(null, false); // Changes only this isolated process, never Windows settings.
                FluentMotion.SetWindowActive(false);
                Require(!FluentMotion.IsEnabled, "Disabling the isolated preference did not disable motion.");
                FluentMotion.StopAll();
                await AwaitNavigationAsync();
                RequireNavigation("video");
                foreach (var tag in new[] { "settings", "history", "audio", "all" })
                {
                    SelectNavigation(tag);
                    Require(Field("_navigationTransition") is Task { IsCompletedSuccessfully: true }, "Disabled motion retained an asynchronous navigation.");
                    RequireNavigation(tag);
                }
                var progress = new TaskProgressBar { State = ConversionState.Running, Progress = 37.25 };
                Require(progress.Value == 37.25, "Disabled progress did not reflect its actual value.");
            }
            finally { preference.SetValue(null, previous); FluentMotion.SetWindowActive(true); FluentMotion.Reset((FrameworkElement)Field("TaskContentHost")!); }
        });
        await CheckAsync("Loaded progress follows reported values and settles when paused or retried", async () =>
        {
            await AwaitNavigationAsync();
            var list = (ListView)Field("TaskList")!;
            list.UpdateLayout();
            await Task.Delay(100);
            // Virtualized lists may retain discarded templates with a cleared
            // DataContext. Exercise a currently bound card inside the viewport.
            var bar = FindDescendants<TaskProgressBar>(list).FirstOrDefault(candidate =>
                candidate.DataContext is MediaTaskViewModel boundTask &&
                _window!.VisibleTasks.Contains(boundTask) && IsVisibleWithin(candidate, list))
                ?? throw new InvalidOperationException("No visible, loaded task progress control retained a current task binding.");
            var task = (MediaTaskViewModel)bar.DataContext;
            task.State = ConversionState.Running;
            task.Progress = 10;
            await Task.Delay(30);
            task.Progress = 63.75;
            await Task.Delay(50);
            Require(bar.Progress == 63.75 && bar.Value == 63.75,
                "Native progress did not preserve the exact reported value and task binding.");
            await Task.Delay(200);
            Require(bar.Value == 63.75, "Progress did not settle on the exact reported value.");
            task.Progress = 72.5;
            task.State = ConversionState.Paused;
            await Task.Delay(30);
            Require(bar.Value == 72.5 && bar.ShowPaused && !bar.IsIndeterminate,
                "Pausing left progress animated or changed its value.");
            task.State = ConversionState.Running;
            task.Progress = 0;
            await Task.Delay(30);
            Require(bar.Value == 0, "A retry retained progress from the previous attempt.");
            task.Progress = 42.5;
            await Task.Delay(200);
        });
        await CheckAsync("Interleaved drag feedback leaves a collapsed, reset overlay", async () =>
        {
            Invoke("ShowDropFeedback");
            var firstHide = InvokeTask("HideDropFeedbackAsync");
            Invoke("ShowDropFeedback");
            var finalHide = InvokeTask("HideDropFeedbackAsync");
            await Task.WhenAll(firstHide, finalHide).WaitAsync(TimeSpan.FromSeconds(3));
            var overlay = (FrameworkElement)Field("DropFeedbackOverlay")!;
            Require(overlay.Visibility == Visibility.Collapsed, "An interrupted drag fade left the overlay visible.");
            Require(ElementCompositionPreview.GetElementVisual(overlay).Opacity == 1,
                "The hidden drag overlay retained stale opacity for the next drag.");
        });
        await CheckAsync("Loaded category navigation restores scroll position", async () =>
        {
            SelectNavigation("all");
            await AwaitNavigationAsync();
            var list = (ListView)Field("TaskList")!;
            var scroller = FindDescendant<ScrollViewer>(list) ?? throw new InvalidOperationException("List scroller was not realized.");
            scroller.ChangeView(null, 280, null, true);
            await Task.Delay(120);
            var expected = scroller.VerticalOffset;
            Require(expected > 0, "The 100-task list did not scroll.");
            SelectNavigation("audio");
            await AwaitNavigationAsync();
            var savedPositions = (Dictionary<string, (double List, double Page)>)Field("_queueScrollPositions")!;
            savedPositions.TryGetValue("all", out var savedAll);
            var audioOffset = scroller.VerticalOffset;
            SelectNavigation("all");
            await AwaitNavigationAsync();
            Require(Math.Abs(scroller.VerticalOffset - expected) < 2,
                $"Returning to the queue lost its scroll position. expected={expected:R}; actual={scroller.VerticalOffset:R}; " +
                $"saved_all={savedAll.List:R}; audio={audioOffset:R}; extent={scroller.ExtentHeight:R}; " +
                $"viewport={scroller.ViewportHeight:R}; pending={Field("_pendingScrollRestore") ?? "none"}; " +
                $"navigation={Field("_navigationTag")}; version={Field("_navigationVersion")}; " +
                $"same_scroller={ReferenceEquals(scroller, FindDescendant<ScrollViewer>(list))}.");
            scroller.ChangeView(null, 0, null, true);
        });

        await CheckReviewReminderDialogPriorityAsync();
        await CheckNavigationMotionAsync();
        await CheckJumpListNavigationAsync();
        await CheckQueueDensityAsync();
        await CheckQueueScrollBoundaryAsync();
        await CheckProgressStateColorsAsync();

        // Keep representative states for screenshots; the preceding checks use 100 cards.
        foreach (var task in _window.Tasks.Skip(7).ToArray()) { _window.Tasks.Remove(task); task.Dispose(); }
        Invoke("RefreshVisibleTasks");
        Invoke("UpdateQueueSummary");
        await CaptureNavigationStatesAsync();
        await CaptureAsync("queue-light-wide", ElementTheme.Light, 1280, 900, "all");
        await CaptureAsync("queue-dark-wide", ElementTheme.Dark, 1280, 900, "all");
        await CaptureAsync("queue-light-narrow", ElementTheme.Light, 720, 900, "all");
        var queueExperience = (Views.QueueView)Field("ConverterView")!;
        queueExperience.ShowUndo(LocalizationService.T("QueueUndoRemoved", 3));
        await CaptureAsync("queue-undo-dark-narrow", ElementTheme.Dark, 720, 900, "all");
        queueExperience.HideUndo();
        await CaptureAsync("settings-light-wide", ElementTheme.Light, 1280, 900, "settings");
        await CaptureAsync("settings-dark-narrow", ElementTheme.Dark, 720, 900, "settings");
        await CaptureAsync("settings-actions-light-wide", ElementTheme.Light, 1280, 900, "settings", ScrollSettingsActionsIntoViewAsync);
        await CaptureAsync("settings-actions-dark-narrow", ElementTheme.Dark, 720, 900, "settings", ScrollSettingsActionsIntoViewAsync);
        await CheckLoadedFeedbackAsync();
        await CaptureAsync("archive-light-wide", ElementTheme.Light, 1280, 900, "archive");
        await CaptureAsync("archive-dark-narrow", ElementTheme.Dark, 720, 900, "archive");
        await CaptureAsync("disc-light-wide", ElementTheme.Light, 1280, 900, "disc");
        await CaptureAsync("disc-dark-narrow", ElementTheme.Dark, 720, 900, "disc");
        await CaptureAsync("automation-light-wide", ElementTheme.Light, 1280, 900, "automation");
        await CaptureAsync("automation-dark-narrow", ElementTheme.Dark, 720, 900, "automation");
        await CaptureAsync("automation-dark-narrow-filters", ElementTheme.Dark, 720, 900, "automation", ScrollAutomationEditorToFiltersAsync);
        await CaptureAsync("automation-dark-narrow-conversion", ElementTheme.Dark, 720, 900, "automation", ScrollAutomationEditorToConversionAsync);
        await CaptureAsync("automation-dark-narrow-actions", ElementTheme.Dark, 720, 900, "automation", ScrollAutomationEditorToEndAsync);
        await CaptureAsync("history-dark-wide", ElementTheme.Dark, 1280, 900, "history");
        var previousCaptureLanguage = LocalizationService.CurrentLanguageCode;
        try
        {
            foreach (var language in new[] { "hu-HU", "el-GR", "uk-UA", "vi-VN", "th-TH", "hi-IN", "bn-BD", "ta-IN",
                "ar-SA", "he-IL", "fa-IR", "ur-PK", "pa-IN", "am-ET", "my-MM" })
            {
                LocalizationService.SetLanguage(language);
                Invoke("ApplyLocalization");
                await CaptureAsync($"settings-{language}-dark-narrow", ElementTheme.Dark, 720, 900, "settings");
                await CaptureAsync($"automation-{language}-light-wide", ElementTheme.Light, 1280, 900, "automation");
            }
        }
        finally
        {
            LocalizationService.SetLanguage(previousCaptureLanguage);
            Invoke("ApplyLocalization");
        }
        SelectNavigation("all");
        await AwaitNavigationAsync();
        ClearSampleTasks();
        await CaptureAsync("empty-light-wide", ElementTheme.Light, 1280, 900, "all");
        await CaptureAsync("empty-dark-narrow", ElementTheme.Dark, 720, 900, "all");
    }

    private async Task CheckReviewReminderDialogPriorityAsync()
    {
        const string name = "Loaded rating prompt fully closes before a business dialog opens";
        if (typeof(MainWindow).GetMethod("CreateReviewReminderDialog", InstanceMembers) is null)
        {
            _results.Add(new { name, skipped = "Baseline predates review reminder dialogs." });
            return;
        }

        await CheckAsync(name, async () =>
        {
            var root = (Grid)Field("RootGrid")!;
            var originalLanguage = LocalizationService.CurrentLanguageCode;
            var originalTheme = root.RequestedTheme;
            ContentDialog? ratingDialog = null;
            ContentDialog? businessDialog = null;
            Task<ContentDialogResult>? ratingShow = null;
            Task<ContentDialogResult>? businessShow = null;
            var ratingOpened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ratingClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ratingShowCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var businessOpened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new List<string>();
            var cleaningUp = false;
            try
            {
                Require(Field("_reviewBusinessDialogs") is 0, "A business dialog was already active.");
                Require(Field("_reviewReminderDialog") is null, "A rating prompt was already active.");
                LocalizationService.SetLanguage("ar-SA");
                Invoke("ApplyLocalization");
                root.RequestedTheme = ElementTheme.Dark;
                root.UpdateLayout();
                await Task.Delay(30);

                // Use the production factory only: no launch registration, persistence,
                // Store eligibility checks, or native rating requests run in this test.
                ratingDialog = (ContentDialog)Invoke("CreateReviewReminderDialog")!;
                businessDialog = new ContentDialog
                {
                    XamlRoot = root.XamlRoot,
                    RequestedTheme = root.ActualTheme,
                    FlowDirection = root.FlowDirection,
                    Title = "Smoke test business dialog",
                    Content = "The rating prompt must yield before this dialog opens.",
                    CloseButtonText = "Close"
                };
                ratingDialog.Opened += (_, _) =>
                {
                    ratingOpened.TrySetResult(true);
                    if (cleaningUp) ratingDialog.Hide();
                };
                ratingDialog.Closed += (_, _) =>
                {
                    events.Add("rating-closed");
                    ratingClosed.TrySetResult(true);
                };
                businessDialog.Opened += (_, _) =>
                {
                    events.Add("business-opened");
                    businessOpened.TrySetResult(true);
                    if (cleaningUp) businessDialog.Hide();
                };
                SetField("_reviewReminderDialog", ratingDialog);
                SetField("_reviewDialogClosed", ratingShowCompleted);

                async Task<ContentDialogResult> ShowRatingAsync()
                {
                    try { return await ratingDialog.ShowAsync(); }
                    finally { ratingShowCompleted.TrySetResult(true); }
                }
                ratingShow = ShowRatingAsync();
                await ratingOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(ratingDialog.PrimaryButtonText == LocalizationService.T("ReviewReminderRate")
                    && ratingDialog.SecondaryButtonText == LocalizationService.T("ReviewReminderNever")
                    && ratingDialog.CloseButtonText == LocalizationService.T("ReviewReminderLater"),
                    "The opened rating prompt did not retain its three localized choices.");
                Require(ratingDialog.DefaultButton == ContentDialogButton.Close,
                    "The opened rating prompt defaulted to a rating or permanent opt-out action.");
                Require(root.ActualTheme == ElementTheme.Dark && ratingDialog.RequestedTheme == root.ActualTheme,
                    "The opened rating prompt did not inherit the window's dark theme.");
                Require(root.FlowDirection == FlowDirection.RightToLeft
                    && ratingDialog.FlowDirection == root.FlowDirection,
                    "The opened rating prompt did not inherit Arabic right-to-left layout.");

                businessShow = (Task<ContentDialogResult>)Invoke("ShowContentDialogAsync", businessDialog)!;
                await businessOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(ratingClosed.Task.IsCompletedSuccessfully,
                    "The business dialog opened while the rating prompt was still closing.");
                Require(ratingShow!.IsCompletedSuccessfully,
                    "The business dialog opened before the rating ShowAsync finished framework cleanup.");
                Require(events.SequenceEqual(new[] { "rating-closed", "business-opened" }),
                    "The real dialog events did not occur in rating-closed then business-opened order.");
                Require(Field("_reviewBusinessDialogs") is 1,
                    "The production helper did not track the open business dialog.");
                businessDialog.Hide();
                await Task.WhenAll(ratingShow, businessShow).WaitAsync(TimeSpan.FromSeconds(5));
                Require(ratingShow.IsCompletedSuccessfully && businessShow.IsCompletedSuccessfully,
                    "A real dialog ShowAsync operation failed.");
                Require(Field("_reviewBusinessDialogs") is 0,
                    "The production helper did not release its business-dialog guard.");
            }
            finally
            {
                cleaningUp = true;
                try
                {
                    // Also close a dialog that finishes opening after a timeout.
                    businessDialog?.Hide();
                    ratingDialog?.Hide();
                    var outstandingShows = new[] { ratingShow, businessShow }.OfType<Task>();
                    await Task.WhenAll(outstandingShows).WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    SetField("_reviewReminderDialog", null);
                    SetField("_reviewDialogClosed", null);
                    LocalizationService.SetLanguage(originalLanguage);
                    Invoke("ApplyLocalization");
                    root.RequestedTheme = originalTheme;
                }
            }
        });
    }

    private void SelectNavigation(string tag)
    {
        var navigation = (NavigationView)Field("NavView")!;
        var roots = navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>();
        var selected = roots.Concat(roots.SelectMany(item => item.MenuItems.OfType<NavigationViewItem>()))
            .Single(item => Equals(item.Tag, tag));
        if (ReferenceEquals(navigation.SelectedItem, selected)) Invoke("NavigateWithMotion", tag);
        else navigation.SelectedItem = selected;
    }

    private async Task AwaitNavigationAsync()
    {
        if (Field("_navigationTransition") is Task transition)
            await transition.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(320); // Let enter animations and queued layout/scroll restoration finish.
    }

    private static System.Collections.IDictionary GetRunningMotions() =>
        (System.Collections.IDictionary)typeof(FluentMotion)
            .GetField("Running", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static bool IsElementMotionRunning(FrameworkElement element) => GetRunningMotions().Contains(element);

    private static async Task WaitForElementMotionAsync(FrameworkElement element)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (IsElementMotionRunning(element) && timeout.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(20);
        Require(!IsElementMotionRunning(element), "The loaded animation did not release its target after completion.");
    }

    private static void RequireMotionTranslationIsZero(FrameworkElement element)
    {
        var properties = ElementCompositionPreview.GetElementVisual(element).Properties;
        Require(properties.TryGetVector3("Translation", out var translation) == Microsoft.UI.Composition.CompositionGetValueStatus.Succeeded &&
            translation.LengthSquared() < 0.0001f, $"{element.Name} retained a page translation.");
    }

    private async Task CheckAsync(string name, Func<Task> action)
    {
        TraceCheck(name);
        try { await action(); _results.Add(new { name, passed = true }); }
        catch (Exception exception) { RecordFailure(name, exception); }
    }

    private async Task CaptureAsync(string name, ElementTheme theme, int width, int height, string tag, Func<Task>? afterReset = null)
    {
        await CheckAsync("Capture " + name, async () =>
        {
            var root = (Grid)Field("RootGrid")!;
            root.RequestedTheme = theme;
            ResizeForCapture(width, height);
            SelectNavigation(tag);
            await AwaitNavigationAsync();
            await ResetCaptureScrollAsync(tag);
            if (afterReset is not null) await afterReset();
            root.UpdateLayout();
            Require(Math.Abs(root.ActualWidth - width) <= 1, "The capture window did not reach its requested logical width.");
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "The rendered capture is empty.");
            var folderPath = Path.Combine(_runRoot, "captures");
            Directory.CreateDirectory(folderPath);
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            var file = await folder.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        });
    }

    // Override only the private policy snapshot inside this temporary application.
    // No UISettings property, registry value, or production preference is written.
    private sealed class IsolatedMotionPreferenceScope : IDisposable
    {
        private readonly App _owner;
        private readonly FieldInfo _animations = MotionField("_animationsEnabled");
        private readonly FieldInfo _highContrast = MotionField("_highContrast");
        private readonly FieldInfo _windowActive = MotionField("_windowActive");
        private readonly bool _previousAnimations;
        private readonly bool _previousHighContrast;
        private readonly bool _previousWindowActive;

        public IsolatedMotionPreferenceScope(App owner)
        {
            _owner = owner;
            _previousAnimations = (bool)_animations.GetValue(null)!;
            _previousHighContrast = (bool)_highContrast.GetValue(null)!;
            _previousWindowActive = (bool)_windowActive.GetValue(null)!;
            owner._originalAnimationPreference = _previousAnimations;
            owner._originalHighContrastPreference = _previousHighContrast;
            FluentMotion.SetWindowActive(false);
            _animations.SetValue(null, true);
            _highContrast.SetValue(null, false);
            FluentMotion.SetWindowActive(true);
        }

        public void Dispose()
        {
            FluentMotion.SetWindowActive(false);
            _animations.SetValue(null, _previousAnimations);
            _highContrast.SetValue(null, _previousHighContrast);
            FluentMotion.SetWindowActive(_previousWindowActive);
            _owner._testMotionPreferencesRestored =
                Equals(_animations.GetValue(null), _previousAnimations) &&
                Equals(_highContrast.GetValue(null), _previousHighContrast) &&
                Equals(_windowActive.GetValue(null), _previousWindowActive);
            Require(_owner._testMotionPreferencesRestored == true,
                "The isolated animation preferences were not restored.");
        }

        private static FieldInfo MotionField(string name) => typeof(FluentMotion)
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing isolated motion policy field: " + name);
    }

    private void ResizeForCapture(int width, int height)
    {
        var scale = ((Grid)Field("RootGrid")!).XamlRoot?.RasterizationScale ?? 1;
        _window!.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
            (int)Math.Round(width * scale), (int)Math.Round(height * scale)));
    }

    private async Task ResetCaptureScrollAsync(string tag)
    {
        var page = (FrameworkElement)Field(tag switch
        {
            "settings" => "SettingsView", "history" => "HistoryView", "archive" => "ArchivePage", "automation" => "AutomationView", _ => "ConverterView"
        })!;
        page.UpdateLayout();
        if (page is ScrollViewer pageScroller) pageScroller.ChangeView(null, 0, null, true);
        foreach (var scroller in FindDescendants<ScrollViewer>(page))
            scroller.ChangeView(null, 0, null, true);
        await Task.Delay(180);
        page.UpdateLayout();
    }

    private Task InvokeTask(string name) => (Task)typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_window, null)!;

    private static bool IsVisibleWithin(FrameworkElement element, FrameworkElement viewport)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
        var bounds = element.TransformToVisual(viewport).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Bottom > 0 && bounds.Top < viewport.ActualHeight &&
            bounds.Right > 0 && bounds.Left < viewport.ActualWidth;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void Check(string name, Action action)
    {
        TraceCheck(name);
        try
        {
            action();
            _results.Add(new { name, passed = true });
        }
        catch (Exception exception)
        {
            RecordFailure(name, exception);
        }
    }

    private void TraceCheck(string name)
    {
        if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_TRACE") == "1")
            File.AppendAllText(Path.Combine(_runRoot, "checks.log"),
                $"{DateTimeOffset.Now:O} {Thread.CurrentThread.GetApartmentState()} {name}{Environment.NewLine}");
    }

    private void RecordFailure(string name, Exception exception)
    {
        _failures++;
        _results.Add(new
        {
            name,
            passed = false,
            exception = exception.ToString(),
            missingSettingsControls = SafeMissingSettingsControls()
        });
    }

    private string[] SafeMissingSettingsControls()
    {
        try { return _window is null ? [] : MissingSettingsControls(); }
        catch (Exception exception) { return ["Diagnostic error: " + exception.Message]; }
    }
    // Support both the pre-extraction baseline and the real named fields in each page.
    // This is test introspection only; production code never crosses a namescope via FindName.
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? ReadWindowMember(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, InstanceMembers)?.GetValue(window) ??
        typeof(MainWindow).GetProperty(name, InstanceMembers)?.GetValue(window);

    private object? Field(string name)
    {
        if (typeof(MainWindow).GetField(name, InstanceMembers) is { } field) return field.GetValue(_window);
        if (typeof(MainWindow).GetProperty(name, InstanceMembers) is { } property) return property.GetValue(_window);
        foreach (var viewName in new[] { "ConverterView", "SettingsView", "HistoryView" })
        {
            var view = typeof(MainWindow).GetField(viewName, InstanceMembers)?.GetValue(_window);
            if (view?.GetType().GetField(name, InstanceMembers) is { } child) return child.GetValue(view);
        }
        var state = SettingsState();
        return SettingsStateName(name) is { } stateName
            ? state?.GetType().GetProperty(stateName)?.GetValue(state)
            : throw new InvalidOperationException("Missing window/page member: " + name);
    }

    private object? SettingsState()
    {
        var view = typeof(MainWindow).GetField("SettingsView", InstanceMembers)?.GetValue(_window);
        return view?.GetType().GetProperty("ViewModel")?.GetValue(view);
    }

    private static string? SettingsStateName(string name) => name switch
    {
        "_settingsViewLoaded" => "IsInitialized", "_settingsAvailable" => "IsAvailable",
        "_settingsDraftSession" => "DraftSession", _ => null
    };

    private void SetField(string name, object? value)
    {
        if (typeof(MainWindow).GetField(name, InstanceMembers) is { } field) field.SetValue(_window, value);
        else if (typeof(MainWindow).GetProperty(name, InstanceMembers) is { } property) property.SetValue(_window, value);
        else
        {
            var state = SettingsState() ?? throw new InvalidOperationException("Settings state is missing.");
            state.GetType().GetProperty(SettingsStateName(name) ?? name)!.SetValue(state, value);
        }
    }

    private object? Invoke(string name, params object?[] arguments)
    {
        try
        {
            return typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_window, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private string[] MissingSettingsControls()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var pagePath = Path.Combine(AppContext.BaseDirectory, "Inputs", "Views", "SettingsView.xaml");
        var separated = File.Exists(pagePath);
        var document = XDocument.Load(separated ? pagePath : Path.Combine(AppContext.BaseDirectory, "Inputs", "MainWindow.xaml"));
        var settings = separated ? document.Root! : document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "SettingsView");
        var owner = separated ? Field("SettingsView")! : _window!;
        return settings.DescendantsAndSelf()
            .Where(element => !element.Ancestors().Any(ancestor => ancestor.Name.LocalName is "DataTemplate" or "ControlTemplate"))
            .Select(element => (string?)element.Attribute(x + "Name"))
            .OfType<string>().Where(name => owner.GetType().GetField(name, InstanceMembers)?.GetValue(owner) is null).ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Finish()
    {
        if (_finishing) return;
        _finishing = true;
        try
        {
            _window?.Close();
            LogService.FlushAll();
        }
        catch (Exception exception)
        {
            RecordFailure("Window cleanup", exception);
        }
        File.WriteAllText(Path.Combine(_runRoot, "results.json"), JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            captureRequested = _capture,
            motionOnlyRequested = _motionOnly,
            motionEnabledForLoadedChecks = _loadedMotionWasEnabled,
            originalAnimationPreference = _originalAnimationPreference,
            originalHighContrastPreference = _originalHighContrastPreference,
            asynchronousCompositionFadeObserved = _asynchronousFadeObserved,
            isolatedMotionPreferencesRestored = _testMotionPreferencesRestored,
            failures = _failures,
            results = _results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.Exit(_failures == 0 ? 0 : 1);
    }
}










