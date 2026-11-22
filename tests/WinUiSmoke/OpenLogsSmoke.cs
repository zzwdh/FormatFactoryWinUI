using System.ComponentModel;
using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckOpenLogsActionsAsync()
    {
        await CheckOpenLogsFailureAsync(
            "Real log-folder button contains Win32 access denial, reports it once and allows retry without changing the queue or draft",
            () => new Win32Exception(5, "Synthetic Explorer launch access denied."));
        await CheckOpenLogsFailureAsync(
            "Real log-folder button contains directory access denial, reports it once and allows retry without changing the queue or draft",
            () => new UnauthorizedAccessException("Synthetic log directory access denied."));
    }

    private async Task CheckOpenLogsFailureAsync(string name, Func<Exception> createFailure)
    {
        await CheckAsync(name, async () =>
        {
            using var motionScope = new SettingsActionsMotionScope(_window);
            Require(Path.GetFullPath(LogService.AppDataRoot).StartsWith(
                Path.TrimEndingDirectorySeparator(_runRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Log-folder smoke checks must use the isolated run data root.");
            var fixture = Path.Combine(_runRoot, "open-logs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var settingsPath = Path.Combine(fixture, "settings.json");
            var queuePath = Path.Combine(fixture, "queue.json");
            var sourcePath = Path.Combine(fixture, "pending.txt");
            await File.WriteAllTextAsync(sourcePath, "Synthetic pending text for the isolated log-folder action check.");
            var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
            var settingsService = new SettingsService(settingsPath);
            await settingsService.SaveAsync(new AppSettings { WatchFolderEnabled = false, ParallelCount = 2 });
            var baseline = await settingsService.LoadAsync();
            var savedSettings = await File.ReadAllBytesAsync(settingsPath);
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            MainWindow? window = null;
            QueueSession? session = null;
            var reported = new List<Exception>();
            Action<Exception> observe = reported.Add;
            var paths = new List<string>();
            var failure = createFailure();
            Exception? nextFailure = failure;
            try
            {
                var services = new MainWindowServices(settingsService,
                    new QueueStore(queuePath, () => []), new LocalImportStorageAccess(),
                    feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)),
                    shell: new WindowShellServices(openLogsDirectory: path =>
                    {
                        paths.Add(path);
                        if (nextFailure is not null) throw nextFailure;
                        // A successful launch is recorded without opening a real Explorer window.
                    }));
                var activeWindow = new MainWindow(services);
                window = activeWindow;
                DetachSmokeWindowStartup(activeWindow);
                _window = activeWindow;
                SetField("_settings", baseline.Snapshot());
                SetField("_settingsAvailable", true);
                Invoke("EnsureSettingsViewLoaded");
                SetField("_initializing", false);
                var settings = (SettingsView)Field("SettingsView")!;
                settings.ParallelCountBox.Value = 1;
                var edited = settings.CaptureSettingsDraft();
                var draft = settings.ViewModel.DraftSession;
                var saveState = settings.ViewModel.SaveStateKey;
                Require(draft is { IsDirty: true } && draft.Current == edited,
                    "The fixture did not establish an edited settings draft.");
                var runtimeSettings = JsonSerializer.Serialize((AppSettings)Field("_settings")!);
                var activeSession = (QueueSession)Field("_queueSession")!;
                session = activeSession;
                var pending = new MediaTask
                {
                    InputPath = sourcePath, Kind = MediaKind.Document, SourceFormatId = "txt",
                    TargetFormatId = "pdf", State = ConversionState.Waiting, FileSize = sourceBytes.Length
                };
                pending.AvailableFormats.Add(FormatCatalog.FindById("pdf")!.DisplayName);
                Require(activeSession.AddTask(pending), "The isolated pending task was not accepted.");
                await activeSession.SaveAsync();
                var savedQueue = await File.ReadAllBytesAsync(queuePath);
                var handlers = typeof(SettingsView).GetField("OpenLogsButton_ClickRequested", InstanceMembers)!
                    .GetValue(settings) as Delegate;
                Require(handlers?.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, activeWindow)
                    && handler.Method.Name == "OpenLogsButton_Click") == true,
                    "The actual SettingsView log-folder button is not connected to the window handler.");
                SafeDiagnostics.ErrorReported += observe;

                typeof(SettingsView).GetMethod("OpenLogsButton_Click", InstanceMembers)!
                    .Invoke(settings, new object[] { settings.OpenLogsButton, new RoutedEventArgs() });
                Require(paths.SequenceEqual(new[] { LogService.LogsDirectory }),
                    "The failing log-folder action did not receive exactly the isolated logs path.");
                Require(reported.Count == 1 && ReferenceEquals(reported[0], failure),
                    "The original log-folder failure was hidden or reported more than once.");
                Require(settings.SettingsInfoBar.IsOpen && settings.SettingsInfoBar.Severity == InfoBarSeverity.Warning
                    && settings.SettingsInfoBar.Message.Contains(failure.Message, StringComparison.Ordinal),
                    "The log-folder failure did not leave a visible settings warning.");
                await RequireOpenLogsStateRetainedAsync();

                nextFailure = null;
                typeof(SettingsView).GetMethod("OpenLogsButton_Click", InstanceMembers)!
                    .Invoke(settings, new object[] { settings.OpenLogsButton, new RoutedEventArgs() });
                Require(paths.SequenceEqual(new[] { LogService.LogsDirectory, LogService.LogsDirectory }),
                    "A subsequent log-folder click could not retry the same isolated logs path.");
                Require(reported.Count == 1,
                    "The successful retry generated another error diagnostic.");
                await RequireOpenLogsStateRetainedAsync();

                async Task RequireOpenLogsStateRetainedAsync()
                {
                    Require(ReferenceEquals(draft, settings.ViewModel.DraftSession)
                        && settings.ViewModel.DraftSession is { IsDirty: true } retained
                        && retained.Current == edited && settings.CaptureSettingsDraft() == edited
                        && settings.ViewModel.SaveStateKey == saveState
                        && JsonSerializer.Serialize((AppSettings)Field("_settings")!) == runtimeSettings,
                        "Opening the logs folder replaced the edited draft or changed runtime settings.");
                    Require(activeSession.Tasks.Count == 1 && ReferenceEquals(activeSession.Tasks[0], pending)
                        && activeWindow.Tasks.Count == 1 && ReferenceEquals(activeWindow.Tasks[0].Model, pending)
                        && pending.State == ConversionState.Waiting,
                        "Opening the logs folder changed the pending task or started conversion.");
                    var settingsAfter = await File.ReadAllBytesAsync(settingsPath);
                    var queueAfter = await File.ReadAllBytesAsync(queuePath);
                    var sourceAfter = await File.ReadAllBytesAsync(sourcePath);
                    Require(settingsAfter.SequenceEqual(savedSettings) && queueAfter.SequenceEqual(savedQueue)
                        && sourceAfter.SequenceEqual(sourceBytes),
                        "Opening the logs folder changed persisted settings, the queue or source bytes.");
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
