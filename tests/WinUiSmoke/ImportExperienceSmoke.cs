using System.Diagnostics;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckImportExperienceAsync()
    {
        var initializing = Field("_initializing");
        try
        {
            // The retained helper has production startup detached. These checks
            // begin at an interactive window and exercise the actual import path.
            SetField("_initializing", false);
            await CheckInteractiveImportExperienceAsync();
        }
        finally { SetField("_initializing", initializing); }
    }

    private async Task CheckInteractiveImportExperienceAsync()
    {
        await CheckAsync("Incoming files expose an editable output format with details collapsed", async () =>
        {
            var folder = Path.Combine(_runRoot, "import-output-format");
            Directory.CreateDirectory(folder);
            var source = Path.Combine(folder, "incoming.pdf");
            WriteDimensionPdf(source, (595, 842, "Incoming PDF output format fixture"));
            var originalWindow = _window;
            var window = new MainWindow(new MainWindowServices(new SettingsService(Path.Combine(folder, "settings.json")),
                new QueueStore(Path.Combine(folder, "queue.json"), () => []), new LocalImportStorageAccess()));
            DetachSmokeWindowStartup(window);
            _window = window;
            var session = (QueueSession)Field("_queueSession")!;
            try
            {
                SetField("_initializing", false);
                Invoke("NavigateWithMotion", "all");
                var list = (ListView)Field("TaskList")!;
                var root = (Grid)Field("RootGrid")!;
                window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 850));
                window.AppWindow.Show(false);
                await ((Task)Invoke("AddIncomingPathsAsync", new[] { source }, null, null)!).WaitAsync(TimeSpan.FromSeconds(15));
                var task = window.Tasks.Single();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (list.ContainerFromItem(task) is null && DateTimeOffset.UtcNow < deadline)
                {
                    root.UpdateLayout();
                    await Task.Delay(25);
                }

                var row = list.ContainerFromItem(task) as FrameworkElement
                    ?? throw new InvalidOperationException("The imported file did not realize a queue row.");
                var format = FindDescendants<ComboBox>(row).Single(control => control.Name == "TaskOutputFormatCombo");
                var editor = FindDescendants<StackPanel>(row).Single(control => control.Name == "TaskDetailsEditor");
                Require(IsVisibleWithin(format, root) && format.IsEnabled && !task.IsDetailsExpanded &&
                    editor.Visibility == Visibility.Collapsed && AutomationProperties.GetName(format) == task.FormatHeader &&
                    Equals(format.SelectedItem, task.TargetFormat),
                    "A newly imported file did not expose its labeled current output format above the collapsed details.");

                var chosen = task.Model.TargetFormatId == "txt" ? "DOCX" : "TXT";
                Require(task.AvailableFormats.Contains(chosen), "The PDF fixture did not offer the expected alternate format.");
                format.SelectedItem = chosen;
                Require(task.Model.TargetFormatId == chosen.ToLowerInvariant() && editor.Visibility == Visibility.Collapsed &&
                    ReferenceEquals(session.Tasks.FindById(task.Id), task.Model),
                    "Selecting a visible output format did not update the admitted task with details still collapsed.");
            }
            finally
            {
                await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                window.Close();
                _window = originalWindow;
            }
        });

        await CheckAsync("Progressive import uses the real window admission path and exposes cancellation", async () =>
        {
            var folder = Path.Combine(_runRoot, "progressive-import");
            Directory.CreateDirectory(folder);
            foreach (var index in Enumerable.Range(0, 24))
                await File.WriteAllTextAsync(Path.Combine(folder, $"document{index}.docx"), "import only");
            var prior = _window!.Tasks.Select(task => task.Id).ToHashSet();
            await ((Task)Invoke("AddFolderAsync", folder, null)!).WaitAsync(TimeSpan.FromSeconds(15));
            var added = _window.Tasks.Where(task => !prior.Contains(task.Id)).ToArray();
            Require(added.Length == 24, "Not every discovered document reached the UI queue.");
            Require(added.Select(task => Path.GetFileName(task.InputPath)).SequenceEqual(
                Enumerable.Range(0, 24).Select(index => $"document{index}.docx")), "Natural discovery order changed.");
            Require(!((InfoBar)Field("ImportStatusBar")!).IsOpen, "Import progress stayed open after completion.");
            Require(!string.IsNullOrWhiteSpace(((Button)Field("CancelImportButton")!).Content?.ToString()), "Import cancel action lacks a localized label.");
            var session = (QueueSession)Field("_queueSession")!;
            Require(added.All(task => ReferenceEquals(session.Tasks.FindById(task.Id), task.Model)), "Admission did not preserve model identity.");
            foreach (var task in added) Invoke("RemoveTask", task);
        });

        await CheckAsync("Cancelling an active UI import retains published tasks and releases unpublished reservations", async () =>
        {
            var folder = Path.Combine(_runRoot, "cancel-progressive-import");
            Directory.CreateDirectory(folder);
            var paths = Enumerable.Range(0, 16).Select(index => Path.Combine(folder, $"document{index}.docx")).ToArray();
            foreach (var path in paths) await File.WriteAllTextAsync(path, "import cancellation fixture");
            var coordinator = (MediaImportCoordinator)Field("_mediaImportCoordinator")!;
            var storageField = typeof(MediaImportCoordinator).GetField("_storageAccess", InstanceMembers)!;
            var originalStorage = (IImportStorageAccess)storageField.GetValue(coordinator)!;
            // The first item of batch two completes but is not published; the remaining
            // in-flight reads block after reserving their paths until the user cancels.
            var controlledStorage = new BlockingImportStorageAccess(originalStorage, paths.Skip(9));
            var prior = _window!.Tasks.Select(task => task.Id).ToHashSet();
            Task? import = null;
            storageField.SetValue(coordinator, controlledStorage);
            try
            {
                import = (Task)Invoke("AddFolderAsync", folder, null)!;
                await controlledStorage.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var firstBatch = _window.Tasks.Where(task => !prior.Contains(task.Id)).ToArray();
                Require(firstBatch.Length == 8, "The first published batch was not visible while the second batch was waiting.");
                Require(((InfoBar)Field("ImportStatusBar")!).IsOpen, "Active import progress was not visible.");
                Require(((Button)Field("CancelImportButton")!).IsEnabled, "Active import could not be cancelled.");
                Invoke("CancelImportButton_Click", Field("CancelImportButton"), new RoutedEventArgs());
                await import.WaitAsync(TimeSpan.FromSeconds(15));
                Require(!((InfoBar)Field("ImportStatusBar")!).IsOpen && Field("_activeImport") is null,
                    "Cancelled import did not finish its UI session.");
                Require(_window.Tasks.Where(task => !prior.Contains(task.Id)).Select(task => task.Id)
                    .SequenceEqual(firstBatch.Select(task => task.Id)), "Cancellation changed the already published batch.");

                storageField.SetValue(coordinator, originalStorage);
                await ((Task)Invoke("AddFolderAsync", folder, null)!).WaitAsync(TimeSpan.FromSeconds(15));
                var afterRetry = _window.Tasks.Where(task => !prior.Contains(task.Id)).ToArray();
                Require(afterRetry.Length == paths.Length, "Cancelled unpublished paths remained reserved and could not be reimported.");
                Require(afterRetry.Select(task => task.InputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length,
                    "Reimport duplicated already published tasks.");
                Require(firstBatch.All(task => ReferenceEquals(_window.Tasks.FindById(task.Id), task)),
                    "Reimport replaced the retained first-batch models.");
            }
            finally
            {
                if (import is { IsCompleted: false })
                {
                    Invoke("CancelImportButton_Click", Field("CancelImportButton"), new RoutedEventArgs());
                    await import.WaitAsync(TimeSpan.FromSeconds(15));
                }
                storageField.SetValue(coordinator, originalStorage);
                foreach (var task in _window.Tasks.Where(task => !prior.Contains(task.Id)).ToArray()) Invoke("RemoveTask", task);
            }
        });
    }

    private sealed class BlockingImportStorageAccess(IImportStorageAccess inner, IEnumerable<string> blockedPaths) : IImportStorageAccess
    {
        private readonly HashSet<string> _blockedPaths = new(blockedPaths, StringComparer.OrdinalIgnoreCase);
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> RememberFileAsync(string path, CancellationToken cancellationToken = default)
        {
            if (_blockedPaths.Contains(path))
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return await inner.RememberFileAsync(path, cancellationToken);
        }
        public Task<string> ResolveFilePathAsync(string path, string? token, CancellationToken cancellationToken = default) =>
            inner.ResolveFilePathAsync(path, token, cancellationToken);
        public Task<string?> ResolveFolderPathAsync(string? path, string? token, CancellationToken cancellationToken = default) =>
            inner.ResolveFolderPathAsync(path, token, cancellationToken);
    }

    private async Task MeasureQueueAdmissionAsync(List<PerformanceMetric> metrics)
    {
        var session = (QueueSession)Field("_queueSession")!;
        await OutputRecoveryService.Shared.LoadIndexAsync();
        foreach (var state in new[] { ConversionState.Waiting, ConversionState.Failed })
        foreach (var count in new[] { 1000, 10000 })
        {
            RecordPerformancePhase($"admission-{state}-{count}-clear");
            ClearSampleTasks();
            session.Tasks.Clear();
            var items = Enumerable.Range(0, count).Select(index =>
            {
                var model = new MediaTask
                {
                    InputPath = Path.Combine(_runRoot, "admission-inputs", $"file{index}.docx"),
                    ContentKind = ContentKind.Document,
                    SourceFormatId = "docx", TargetFormatId = "txt", State = state
                };
                return new MediaImportItemResult(model.InputPath, model, null);
            }).ToArray();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            RecordPerformancePhase($"admission-{state}-{count}-start");
            var timer = Stopwatch.StartNew();
            Invoke("ApplyImportResult", new MediaImportBatchResult(items), false);
            ((Grid)Field("RootGrid")!).UpdateLayout();
            await YieldLayoutAsync();
            timer.Stop();
            RecordPerformancePhase($"admission-{state}-{count}-complete");
            Require(session.Tasks.Count == count && _window!.Tasks.Count == count && _window.VisibleTasks.Count == count,
                "Production batch admission lost tasks.");
            Require(items.All(item => ReferenceEquals(_window!.Tasks.FindById(item.Task!.Id)?.Model, item.Task)),
                "Production batch admission changed identity.");
            var list = (ListView)Field("TaskList")!;
            var realized = Enumerable.Range(0, count).Count(index => list.ContainerFromIndex(index) is not null);
            Require(realized < 250, "Production batch admission lost list virtualization.");
            metrics.Add(Metric($"queue_{count}_{state.ToString().ToLowerInvariant()}_admission",
                new[] { timer.Elapsed.TotalMilliseconds }, new[] { ReadPrivateBytes() }, new[] { realized }));
        }
        ClearSampleTasks();
        session.Tasks.Clear();
    }
}
