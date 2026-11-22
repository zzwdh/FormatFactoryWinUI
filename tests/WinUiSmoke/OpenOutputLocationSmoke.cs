using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckOpenOutputLocationsAsync()
    {
        await CheckOutputLocationActionAsync("queue-file", directory: false);
        await CheckOutputLocationActionAsync("queue-directory", directory: true);
        await CheckOutputLocationActionAsync("history", directory: false);
        await CheckOutputLocationActionAsync("taskbar", directory: false);
        await CheckOutputLocationActionAsync("missing-queue", directory: false, missing: true);
        await CheckOutputLocationActionAsync("missing-history", directory: false, missing: true);
        Check("Output-location failure warning is translated in all supported languages", () =>
        {
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            try
            {
                LocalizationService.SetLanguage("en-US");
                var englishWarning = LocalizationService.T("DesktopOpenLocationFailed");
                foreach (var option in LocalizationService.LanguageOptions.Where(option => option.Key != "auto"))
                {
                    LocalizationService.SetLanguage(option.Key);
                    var warning = LocalizationService.T("DesktopOpenLocationFailed");
                    Require(!string.IsNullOrWhiteSpace(warning) && warning != "DesktopOpenLocationFailed",
                        "The output-location failure warning is missing for " + option.Key);
                    Require(option.Key == "en-US" || warning != englishWarning,
                        "The output-location failure warning fell back to English for " + option.Key);
                }
            }
            finally { LocalizationService.SetLanguage(originalLanguage); Invoke("ApplyLocalization"); }
        });
    }

    private async Task CheckOutputLocationActionAsync(string entry, bool directory, bool missing = false)
    {
        await CheckAsync("Real output-location action " + entry + " retains results and queue, contains launch errors and permits retry", async () =>
        {
            using var motionScope = new SettingsActionsMotionScope(_window);
            Require(Path.GetFullPath(LogService.AppDataRoot).StartsWith(
                Path.TrimEndingDirectorySeparator(_runRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Output-location checks must use the isolated run data root.");
            var fixture = Path.Combine(_runRoot, "output-location", entry, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var sourcePath = Path.Combine(fixture, "pending.txt");
            var outputPath = Path.Combine(fixture, directory ? "synthetic-result" : "synthetic result, quoted.txt");
            await File.WriteAllTextAsync(sourcePath, "Synthetic pending input for an isolated Explorer action.");
            if (!missing)
            {
                if (directory) Directory.CreateDirectory(outputPath);
                else await File.WriteAllTextAsync(outputPath, "Synthetic completed output.");
            }
            var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
            var outputBytes = directory || missing ? null : await File.ReadAllBytesAsync(outputPath);
            var settingsPath = Path.Combine(fixture, "settings.json");
            var queuePath = Path.Combine(fixture, "queue.json");
            var settingsService = new SettingsService(settingsPath);
            await settingsService.SaveAsync(new AppSettings { WatchFolderEnabled = false, ParallelCount = 2 });
            var baseline = await settingsService.LoadAsync();
            var settingsBytes = await File.ReadAllBytesAsync(settingsPath);
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            var launches = new List<ProcessStartInfo>();
            var reported = new List<Exception>();
            Exception failure = directory
                ? new UnauthorizedAccessException("Synthetic directory launch denied; private native details must stay out of the warning.")
                : new Win32Exception(5, "Synthetic launch denied; private native details must stay out of the warning.");
            Exception? nextFailure = failure;
            Action<Exception> observe = reported.Add;
            MainWindow? window = null;
            QueueSession? session = null;
            var feedbackBefore = Volatile.Read(ref _feedbackHttpRequests);
            try
            {
                var services = new MainWindowServices(settingsService, new QueueStore(queuePath, () => []),
                    new LocalImportStorageAccess(),
                    feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)),
                    shell: new WindowShellServices(startExplorer: info => { launches.Add(info); if (nextFailure is not null) throw nextFailure; }));
                var activeWindow = new MainWindow(services);
                window = activeWindow;
                DetachSmokeWindowStartup(activeWindow);
                _window = activeWindow;
                LocalizationService.SetLanguage("en-US");
                SetField("_settings", baseline.Snapshot());
                SetField("_settingsAvailable", true);
                Invoke("EnsureSettingsViewLoaded");
                SetField("_initializing", false);
                var settings = (SettingsView)Field("SettingsView")!;
                settings.ParallelCountBox.Value = 1;
                var draft = settings.ViewModel.DraftSession;
                var edited = settings.CaptureSettingsDraft();
                Require(draft is { IsDirty: true }, "The fixture did not establish an edited settings draft.");
                var runtimeSettings = JsonSerializer.Serialize((AppSettings)Field("_settings")!);
                var activeSession = (QueueSession)Field("_queueSession")!;
                session = activeSession;
                var completed = new MediaTask { InputPath = sourcePath, Kind = MediaKind.Document,
                    SourceFormatId = "txt", TargetFormatId = "txt", State = ConversionState.Completed, OutputPath = outputPath };
                var pending = new MediaTask { InputPath = sourcePath, Kind = MediaKind.Document,
                    SourceFormatId = "txt", TargetFormatId = "pdf", State = ConversionState.Waiting };
                Require(activeSession.AddTask(completed) && activeSession.AddTask(pending), "The isolated tasks were not accepted.");
                await activeSession.SaveAsync();
                var queueBytes = await File.ReadAllBytesAsync(queuePath);
                var queue = (QueueView)Field("ConverterView")!;
                var history = (HistoryView)Field("HistoryView")!;
                var isHistory = entry.Contains("history", StringComparison.Ordinal);
                Invoke("NavigateWithMotion", isHistory ? "history" : "all");
                var warning = isHistory ? history.HistoryInfoBar : queue.AppInfoBar;
                SafeDiagnostics.ErrorReported += observe;

                await WaitForTaskbarOutputAsync();
                Click();
                if (missing)
                {
                    Require(launches.Count == 0 && reported.Count == 0, "A missing output attempted to start Explorer or reported a launch failure.");
                    if (!isHistory) Require(queue.AppInfoBar.IsOpen && queue.AppInfoBar.Message == LocalizationService.T("NoOutput"),
                        "The missing queue output warning changed.");
                }
                else
                {
                    Require(launches.Count == 1 && reported.Count == 1 && ReferenceEquals(reported[0], failure),
                        "The original Explorer failure escaped, was hidden or was reported more than once.");
                    Require(warning.IsOpen && warning.Severity == InfoBarSeverity.Warning
                        && warning.Message == LocalizationService.T("DesktopOpenLocationFailed")
                        && !warning.Message.Contains(failure.Message, StringComparison.Ordinal)
                        && (isHistory ? history.Visibility : queue.Visibility) == Visibility.Visible,
                        "The active page did not show a safe, visible Explorer failure warning.");
                    RequireLaunchArguments(launches[0]);
                    await RequireStateRetainedAsync();
                    nextFailure = null;
                    await WaitForTaskbarOutputAsync();
                    Click();
                    Require(launches.Count == 2 && reported.Count == 1, "Retry did not start Explorer exactly once without a new diagnostic.");
                    RequireLaunchArguments(launches[1]);
                }
                await RequireStateRetainedAsync();

                async Task WaitForTaskbarOutputAsync()
                {
                    if (entry != "taskbar") return;
                    // The real taskbar enables its output action only after the
                    // background path check publishes into the host-context cache.
                    var page = (QueuePageViewModel)Field("_queuePage")!;
                    Invoke("UpdateTaskbarProgress");
                    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                    while (!page.CreateTaskbarState().HasOutput && DateTimeOffset.UtcNow < deadline)
                        await Task.Delay(25);
                    Require(page.CreateTaskbarState().HasOutput && page.FindLastCachedOutput() == outputPath,
                        "The taskbar output action did not become ready after its background availability check.");
                }
                void Click()
                {
                    if (entry == "taskbar") Invoke("TaskbarProgress_ButtonClicked", null, TaskbarProgressService.OpenOutputButtonId);
                    else
                    {
                        var owner = isHistory ? (object)history : queue;
                        var relay = isHistory ? "OpenHistoryOutputButton_Click" : "OpenOutputButton_Click";
                        var handler = typeof(MainWindow).GetMethod(relay, InstanceMembers)!;
                        var subscriptions = owner.GetType().GetField(relay + "Requested", InstanceMembers)!.GetValue(owner) as Delegate;
                        Require(subscriptions?.GetInvocationList().Any(callback => ReferenceEquals(callback.Target, activeWindow)
                            && callback.Method == handler) == true, "The real page output-location callback is disconnected.");
                        owner.GetType().GetMethod(relay, InstanceMembers)!.Invoke(owner,
                            new object[] { new Button { Tag = isHistory ? (object)outputPath : completed.Id }, new RoutedEventArgs() });
                    }
                }
                void RequireLaunchArguments(ProcessStartInfo info)
                {
                    Require(info.FileName == "explorer.exe" && info.UseShellExecute, "Explorer executable or shell behavior changed.");
                    if (directory) Require(info.Arguments == outputPath && info.ArgumentList.Count == 0, "The directory Explorer invocation changed.");
                    else Require(info.Arguments.Length == 0 && info.ArgumentList.SequenceEqual(new[] { "/select,", outputPath }),
                        "File selection no longer passes the isolated output as a separate argument.");
                }
                async Task RequireStateRetainedAsync()
                {
                    Require(activeSession.Tasks.Count == 2 && activeWindow.Tasks.Count == 2
                        && completed.State == ConversionState.Completed && completed.OutputPath == outputPath
                        && pending.State == ConversionState.Waiting && string.IsNullOrWhiteSpace(pending.OutputPath),
                        "Opening an output location changed conversion state or started a pending task.");
                    Require(ReferenceEquals(settings.ViewModel.DraftSession, draft) && draft!.IsDirty && draft.Current == edited
                        && settings.CaptureSettingsDraft() == edited && JsonSerializer.Serialize((AppSettings)Field("_settings")!) == runtimeSettings,
                        "Opening an output location replaced the settings draft or runtime settings.");
                    Require((await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(sourceBytes)
                        && (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(settingsBytes)
                        && (await File.ReadAllBytesAsync(queuePath)).SequenceEqual(queueBytes),
                        "Opening an output location changed source bytes or persisted settings/queue.");
                    if (!missing) Require(directory ? Directory.Exists(outputPath)
                        : (await File.ReadAllBytesAsync(outputPath)).SequenceEqual(outputBytes!), "The completed result changed.");
                    Require(Volatile.Read(ref _feedbackHttpRequests) == feedbackBefore, "The isolated output action sent remote feedback.");
                }
            }
            finally
            {
                SafeDiagnostics.ErrorReported -= observe;
                try
                {
                    if (session is not null) await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                    window?.Close();
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
}
