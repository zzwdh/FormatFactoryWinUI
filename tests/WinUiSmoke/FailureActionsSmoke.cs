using System.Collections.ObjectModel;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckFailureActionsAsync()
    {
        var language = LocalizationService.CurrentLanguageCode;
        var fixture = Path.Combine(_runRoot, "failure-actions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var host = new Grid();
        var shell = new Window { Content = host };
        var queue = new QueueView();
        var rows = new ObservableCollection<MediaTaskViewModel>();
        try
        {
            LocalizationService.SetLanguage("en-US");
            rows.Add(new(FailureSmokeTask(fixture, "disk", "No space left on device")));
            rows.Add(new(FailureSmokeTask(fixture, "size", LocalizationService.T("MediaSizeCannotMeetTarget"))));
            rows.Add(new(FailureSmokeTask(fixture, "unknown", "Synthetic diagnostic with no recognized solution.")));
            queue.BindTasks(rows);
            queue.TaskList.Visibility = Visibility.Visible;
            queue.EmptyState.Visibility = Visibility.Collapsed;
            host.Children.Add(queue);
            shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 1100));
            shell.AppWindow.Show(false);
            await FailureSmokeWaitAsync(() => host.IsLoaded && host.XamlRoot is not null);
            var scale = host.XamlRoot!.RasterizationScale;
            shell.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(1060 * scale), (int)(1100 * scale)));
            host.UpdateLayout();
            await Task.Delay(100);

            await CheckAsync("Loaded failure buttons expose specific localized actions and retain original diagnostics", async () =>
            {
                var original = rows.Select(row => row.Model.LastError).ToArray();
                foreach (var culture in new[] { "en-US", "zh-Hans", "pt-BR", "ar-SA" })
                {
                    LocalizationService.SetLanguage(culture);
                    queue.ApplyLocalization(new());
                    foreach (var row in rows) row.RefreshLocalization();
                    host.UpdateLayout();
                    await Task.Delay(35);
                    foreach (var row in rows.Take(2))
                    {
                        var button = DensityControl<Button>(queue, row, "TaskFailureActionButton");
                        Require(IsVisibleWithin(button, host) && button.ActualWidth > 0 && button.ActualHeight > 0,
                            "The specific failure action was absent from the loaded task card: " + culture);
                        Require(AutomationProperties.GetName(button) == row.FailureActionLabel &&
                            button.Content is TextBlock { Text: { Length: > 0 } } text && text.Text == row.FailureActionLabel,
                            "The failure button omitted its localized visible or accessible name: " + culture);
                        Require(DensityControl<TextBlock>(queue, row, "TaskDetailMessageText").Text == row.Model.LastError,
                            "The solution button replaced the raw diagnostic.");
                    }
                    Require(DensityControl<Button>(queue, rows[2], "TaskFailureActionButton").Visibility == Visibility.Collapsed,
                        "An unknown failure offered an unsupported automatic solution.");
                    Require(rows.Select(row => row.Model.LastError).SequenceEqual(original),
                        "Changing languages overwrote a saved failure diagnostic.");
                }
                if (_capture) await SaveResultSummaryCaptureAsync(queue, "failure-actions-localized");
            });

            await CheckAsync("Loaded failure buttons hide when the state becomes active or the diagnosis no longer has an action", async () =>
            {
                var row = rows[0];
                row.State = ConversionState.Running;
                host.UpdateLayout();
                await Task.Delay(35);
                Require(DensityControl<Button>(queue, row, "TaskFailureActionButton").Visibility == Visibility.Collapsed,
                    "A running task retained a failure solution button.");
                row.State = ConversionState.Failed;
                row.Model.LastError = row.Message = "Synthetic new error with no recognized solution.";
                host.UpdateLayout();
                await Task.Delay(35);
                Require(DensityControl<Button>(queue, row, "TaskFailureActionButton").Visibility == Visibility.Collapsed &&
                    DensityControl<TextBlock>(queue, row, "TaskDetailMessageText").Text == row.Model.LastError,
                    "The task retained a stale solution after its diagnostic changed.");
            });
        }
        finally
        {
            host.Children.Clear();
            foreach (var row in rows) row.Dispose();
            shell.Close();
            LocalizationService.SetLanguage(language);
        }
        await CheckFailureActionWindowAsync(fixture);
    }

    private async Task CheckFailureActionWindowAsync(string fixture)
    {
        await CheckAsync("Failure action buttons forward to the window; cancelled selection preserves errors and chosen folders remain task-specific", async () =>
        {
            using var motion = new SettingsActionsMotionScope(_window);
            Require(Path.GetFullPath(LogService.AppDataRoot).StartsWith(
                Path.TrimEndingDirectorySeparator(_runRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Failure-action window checks must use the isolated smoke data root.");
            var originalWindow = _window;
            var language = LocalizationService.CurrentLanguageCode;
            var settingsPath = Path.Combine(fixture, "settings.json");
            var queuePath = Path.Combine(fixture, "queue.json");
            var chosen = Path.Combine(fixture, "chosen-output");
            var baseline = new AppSettings { CustomOutputDirectory = Path.Combine(fixture, "global-output"),
                Notifications = false, NotificationSound = false, EnableUsageTelemetry = false,
                EnableAutomaticErrorReporting = false, WatchFolderEnabled = false };
            var service = new SettingsService(settingsPath);
            await service.SaveAsync(baseline);
            var settingsBytes = await File.ReadAllBytesAsync(settingsPath);
            var choices = new Queue<string?>(new string?[] { null, chosen });
            var selectionCalls = 0;
            MainWindow? window = null;
            QueueSession? session = null;
            try
            {
                LocalizationService.SetLanguage("en-US");
                var services = new MainWindowServices(service, new QueueStore(queuePath, () => []),
                    new LocalImportStorageAccess(), shell: new WindowShellServices(chooseOutputDirectory: _ =>
                    {
                        selectionCalls++;
                        return Task.FromResult(choices.Dequeue());
                    }));
                window = new MainWindow(services);
                DetachSmokeWindowStartup(window);
                _window = window;
                SetField("_settings", baseline.Snapshot());
                SetField("_settingsAvailable", true);
                SetField("_initializing", false);
                // These fixtures never provide a runnable encoder. Engine readiness blocks execution after selection.
                SetField("_ffmpegPath", string.Empty);
                SetField("_ffprobePath", string.Empty);
                var queue = (QueueView)Field("ConverterView")!;
                session = (QueueSession)Field("_queueSession")!;
                var outputTask = FailureSmokeTask(fixture, "window-output", "No space left on device");
                var otherTask = FailureSmokeTask(fixture, "window-other", "Synthetic unrelated retained error.");
                var engineTask = FailureSmokeTask(fixture, "window-engine", LocalizationService.T("MediaCompatibilityEngineUnavailable", "Synthetic missing engine"));
                foreach (var model in new[] { outputTask, otherTask, engineTask })
                    Require(session.AddTask(model), "The isolated failure task was not added.");
                await session.SaveAsync();
                var queueBytes = await File.ReadAllBytesAsync(queuePath);
                var sourceBytes = await File.ReadAllBytesAsync(outputTask.InputPath);
                var handlers = typeof(QueueView).GetField("ResolveFailure_ClickRequested", InstanceMembers)!.GetValue(queue) as Delegate;
                Require(handlers?.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, window) &&
                    handler.Method.Name == "ResolveFailure_Click") == 1,
                    "The queue solution button was not subscribed exactly once to its window handler.");
                window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 1200));
                window.AppWindow.Show(false);
                var root = (Grid)Field("RootGrid")!;
                await FailureSmokeWaitAsync(() => root.IsLoaded && root.XamlRoot is not null);
                var scale = root.XamlRoot!.RasterizationScale;
                window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(1060 * scale), (int)(1200 * scale)));
                root.UpdateLayout();
                await Task.Delay(100);
                var outputRow = window.Tasks.Single(row => row.Id == outputTask.Id);
                var engineRow = window.Tasks.Single(row => row.Id == engineTask.Id);
                var outputButton = DensityControl<Button>(queue, outputRow, "TaskFailureActionButton");
                var engineButton = DensityControl<Button>(queue, engineRow, "TaskFailureActionButton");
                Require(engineRow.FailureAction == FailureResolutionAction.OpenEngineSettings,
                    "The engine failure did not offer the settings action.");
                InvokeFailureButton(engineButton);
                await FailureSmokeWaitAsync(() => ReferenceEquals(((NavigationView)Field("NavView")!).SelectedItem, Field("NavSettingsItem")));
                Require(!session.Coordinator.HasActiveOperations, "Opening engine settings started a conversion.");
                ((NavigationView)Field("NavView")!).SelectedItem = Field("NavAllItem");
                await Task.Delay(100);
                root.UpdateLayout();
                outputButton = DensityControl<Button>(queue, outputRow, "TaskFailureActionButton");

                InvokeFailureButton(outputButton);
                await FailureSmokeWaitAsync(() => selectionCalls == 1 && Field("_failureActionInProgress") is false);
                Require(outputTask.State == ConversionState.Failed && outputTask.LastError == "No space left on device" &&
                    !outputTask.Options.ContainsKey(OutputPathService.DirectoryOverrideKey) &&
                    (await File.ReadAllBytesAsync(queuePath)).SequenceEqual(queueBytes),
                    "Cancelling output selection changed the failed task, diagnostic, or durable queue.");

                InvokeFailureButton(outputButton);
                await FailureSmokeWaitAsync(() => selectionCalls == 2 && Field("_failureActionInProgress") is false);
                Require(outputTask.Options.GetValueOrDefault(OutputPathService.DirectoryOverrideKey) == chosen &&
                    !otherTask.Options.ContainsKey(OutputPathService.DirectoryOverrideKey) &&
                    ((AppSettings)Field("_settings")!).CustomOutputDirectory == baseline.CustomOutputDirectory &&
                    (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(settingsBytes),
                    "The output solution changed another task or global settings.");
                Require(!session.Coordinator.HasActiveOperations && outputTask.State == ConversionState.Failed &&
                    outputTask.LastError == "No space left on device" &&
                    (await File.ReadAllBytesAsync(outputTask.InputPath)).SequenceEqual(sourceBytes),
                    "The encoder-free fixture unexpectedly executed or discarded its source/error.");
                var persisted = (await new QueueStore(queuePath, () => []).LoadResultAsync()).Items.Single(item => item.TaskId == outputTask.Id);
                Require(persisted.Options.GetValueOrDefault(OutputPathService.DirectoryOverrideKey) == chosen,
                    "The task-specific output selection was not persisted before retry admission.");
                outputRow.RefreshLocalization();
                root.UpdateLayout();
                Require(DensityControl<TextBlock>(queue, outputRow, "TaskOutputDirectoryOverrideText").Text == chosen,
                    "The task card did not show its selected output directory.");
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
                    _window = originalWindow;
                    LocalizationService.SetLanguage(language);
                    if (_window is not null) Invoke("ApplyLocalization");
                }
            }
        });
    }

    private static MediaTask FailureSmokeTask(string directory, string name, string message)
    {
        var source = Path.Combine(directory, name + ".mp4");
        File.WriteAllText(source, "Synthetic failure-action fixture; never encoded.");
        var task = new MediaTask { InputPath = source, SourceFormatId = "mp4", TargetFormatId = "mp4",
            Kind = MediaKind.Video, State = ConversionState.Failed, Message = message, LastError = message };
        task.AvailableFormats.Add("MP4");
        task.AvailableFormats.Add("MKV");
        return task;
    }

    private static void InvokeFailureButton(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task FailureSmokeWaitAsync(Func<bool> complete)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!complete())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The isolated failure-action UI did not reach its expected state.");
            await Task.Delay(15);
        }
    }
}
