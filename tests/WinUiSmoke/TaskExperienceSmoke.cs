using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckTaskExperienceAsync()
    {
        var language = LocalizationService.CurrentLanguageCode;
        var fixture = Path.Combine(_runRoot, "task-experience", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var source = Path.Combine(fixture, "restart-source.zip");
        var second = Path.Combine(fixture, "completed-source.zip");
        CreateArchiveSmokeZip(source);
        CreateArchiveSmokeZip(second);
        var storePath = Path.Combine(fixture, "archive-tasks.json");
        var historyStore = new ConversionHistoryStore(Path.Combine(fixture, "history.json"));
        var host = new Grid { Padding = new Thickness(18), RequestedTheme = ElementTheme.Light };
        var shell = new Window { Content = host };
        ArchiveConversionView? view = null;
        try
        {
            LocalizationService.SetLanguage("zh-Hans");
            shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 1000));
            shell.AppWindow.Show(false);
            await ArchiveSmokeWaitAsync(() => host.IsLoaded && host.XamlRoot is not null);
            var scale = host.XamlRoot!.RasterizationScale;
            shell.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(1060 * scale), (int)(1050 * scale)));
            ArchiveConversionView CreateView() => new();
            void Configure(ArchiveConversionView target) => target.Configure(
                () => WinRT.Interop.WindowNative.GetWindowHandle(shell),
                (_, _) => Task.FromResult(ContentDialogResult.None),
                new ArchiveConversionService(FindArchiveSmokeEngine()), new ArchiveTaskStore(storePath),
                async (item, token) => { await historyStore.RecordCompletedAsync(item, token); },
                commitReceipts: new ArchiveCommitReceiptStore(Path.Combine(fixture, "receipts")));

            await CheckAsync("Archive live task list restores encryption intent and interrupted work without passwords or automatic conversion", async () =>
            {
                view = CreateView();
                Configure(view);
                host.Children.Add(view);
                await view.RestoreAsync();
                view.OutputFolderBox.Text = fixture;
                view.AddInputs(new[] { source }, new(ArchiveFormat.SevenZip, true, "synthetic-never-persist-this-password"));
                view.Tasks.Single().Model.SetState(ArchiveTaskState.Running);
                await view.FlushAsync();
                Require(!(await File.ReadAllTextAsync(storePath)).Contains("synthetic-never-persist", StringComparison.Ordinal),
                    "The archive task store included an output password.");
                host.Children.Clear();
                view.Dispose();
                view = CreateView();
                Configure(view);
                host.Children.Add(view);
                await view.RestoreAsync();
                host.UpdateLayout();
                await Task.Delay(80);
                var task = view.Tasks.Single();
                Require(task.SourcePath == source && task.Options.TargetFormat == ArchiveFormat.SevenZip &&
                    task.Options.StrictVerification && task.HasEncryption && task.Options.OutputPassword is null &&
                    task.State != ArchiveTaskState.Running && !view.IsRunning,
                    "An interrupted encrypted archive was dropped, auto-started, or restored without its encryption requirement.");
                Require(ArchiveSmokeTaskButton(view, task, "ArchiveTaskSettings").IsEnabled,
                    "A restored waiting archive could not open its output settings.");
                if (_capture) { await Task.Delay(500); await SaveResultSummaryCaptureAsync(view, "archive-restored-encrypted"); }
            });

            await CheckAsync("Archive real conversion records common history and renders persistent result measurements", async () =>
            {
                view!.Session.Tasks.Clear();
                view.AddInputs(new[] { second }, new(ArchiveFormat.TarGZip, true));
                await view.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(view.Tasks.Single().State == ArchiveTaskState.Succeeded, "The restored archive view could not perform a new conversion.");
                var item = (await historyStore.LoadAsync()).Single();
                Require(item.ContentKind == ContentKind.Archive && item.ResultStatistics is { InputBytes: > 0, OutputBytes: > 0 } &&
                    ArchiveHistoryReplayService.ReadOptions(item).Options.StrictVerification &&
                    HistoryPageViewModel.DescribeParameters(item).Contains("TAR.GZ", StringComparison.Ordinal),
                    "Archive completion omitted measured statistics or common history settings.");
                host.UpdateLayout();
                await Task.Delay(80);
                Require(FindDescendants<TextBlock>(view).Any(text => text.Visibility == Visibility.Visible &&
                    text.Text.Contains("→", StringComparison.Ordinal)), "The completed archive row did not show its measured result.");
                if (_capture) await SaveResultSummaryCaptureAsync(view, "archive-completed-summary");
                await view.FlushAsync();
                host.Children.Clear();
                view.Dispose();
                view = CreateView();
                Configure(view);
                host.Children.Add(view);
                await view.RestoreAsync();
                Require(view.Tasks.Single().State == ArchiveTaskState.Succeeded &&
                    (await historyStore.LoadAsync()).Count == 1, "Restoring completed archive tasks repeated conversion or duplicated history.");
            });

            await CheckAsync("Common history adds an encrypted archive to the archive page with saved safe choices", async () =>
            {
                var item = new ConversionHistoryItem
                {
                    TaskId = Guid.NewGuid(), InputPaths = new[] { source }, DisplayName = "restart-source.7z",
                    ContentKind = ContentKind.Archive, EngineId = "archive", TargetFormatId = "7z", SnapshotVersion = 1,
                    Options = new() { [ArchiveHistoryReplayService.StrictVerificationKey] = "true",
                        [ArchiveHistoryReplayService.EncryptOutputKey] = "true" }
                };
                var archive = (ArchiveConversionView)Field("ArchiveView")!;
                await archive.RestoreAsync();
                Invoke("RepeatHistoryTaskButton_Click", new Button { Tag = item }, new RoutedEventArgs());
                await ArchiveSmokeWaitAsync(() => archive.Tasks.Any(task => task.SourcePath == source));
                var replay = archive.Tasks.Single(task => task.SourcePath == source);
                Require(replay.HasEncryption && replay.Options.OutputPassword is null && replay.Options.StrictVerification &&
                    replay.Options.TargetFormat == ArchiveFormat.SevenZip && !archive.IsRunning &&
                    ReferenceEquals(((NavigationView)Field("NavView")!).SelectedItem, Field("NavArchiveItem")),
                    "Common history did not route an archive to its page with safe saved settings.");
                archive.Session.Tasks.Clear();
                await archive.FlushAsync();
            });

            await CheckAsync("Closing an archive batch preserves interrupted and queued tasks for manual continuation", async () =>
            {
                var exitRoot = Path.Combine(fixture, "exit-recovery");
                var output = Path.Combine(exitRoot, "output");
                Directory.CreateDirectory(output);
                var payload = Path.Combine(exitRoot, "payload.txt");
                var encrypted = Path.Combine(exitRoot, "locked.zip");
                await File.WriteAllTextAsync(payload, "Synthetic encrypted archive used only for cancellation verification.");
                await RunArchiveSmokeEngineAsync(FindArchiveSmokeEngine(), exitRoot,
                    "a", "-tzip", "-psynthetic-source-password", "-y", encrypted, payload);
                var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var store = new ArchiveTaskStore(Path.Combine(exitRoot, "archive-tasks.json"));
                using var exitView = new ArchiveConversionView();
                exitView.Configure(() => WinRT.Interop.WindowNative.GetWindowHandle(shell), async (_, token) =>
                {
                    prompt.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return ContentDialogResult.None;
                }, new ArchiveConversionService(FindArchiveSmokeEngine()), store,
                    commitReceipts: new ArchiveCommitReceiptStore(Path.Combine(exitRoot, "receipts")));
                host.Children.Clear();
                host.Children.Add(exitView);
                try
                {
                    await exitView.RestoreAsync();
                    exitView.OutputFolderBox.Text = output;
                    exitView.AddInputs(new[] { encrypted, second }, new(ArchiveFormat.SevenZip));
                    var run = exitView.StartAsync();
                    await prompt.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Require(exitView.Tasks[0].State == ArchiveTaskState.AwaitingPassword,
                        "The cancellation fixture never reached a real archive password request.");
                    await exitView.StopAsync(preserveForRestart: true);
                    await run;
                    var snapshot = await store.LoadAsync();
                    Require(!exitView.IsRunning && snapshot.Tasks.Count == 2 &&
                        snapshot.Tasks.All(task => task.State == ArchiveTaskState.Waiting) &&
                        Directory.GetFileSystemEntries(output).Length == 0,
                        "Exit cancelled durable tasks instead of preserving a clean, manually resumable batch.");
                }
                finally { await exitView.StopAsync(); host.Children.Clear(); }
            });

            await CheckAsync("Archive close and flush preserve the restored output folder while history recovery is pending", async () =>
            {
                var restoreRoot = Path.Combine(fixture, "restore-close-overlap");
                var output = Path.Combine(restoreRoot, "saved-output");
                Directory.CreateDirectory(output);
                var published = Path.Combine(output, "completed.zip");
                File.Copy(second, published);
                var publishedBytes = await File.ReadAllBytesAsync(published);
                var completed = new ArchiveTaskCheckpoint
                {
                    SourcePath = source, OutputDirectory = output, TargetFormat = ArchiveFormat.Zip,
                    State = ArchiveTaskState.Succeeded, OutputPath = published,
                    CompletedAtUtc = DateTimeOffset.UtcNow, HistoryRecorded = false
                };
                var waiting = new ArchiveTaskCheckpoint
                {
                    SourcePath = second, OutputDirectory = output, TargetFormat = ArchiveFormat.SevenZip,
                    State = ArchiveTaskState.Waiting
                };
                var store = new ArchiveTaskStore(Path.Combine(restoreRoot, "archive-tasks.json"));
                await store.SaveAsync(new() { OutputDirectory = output, Tasks = new[] { completed, waiting } });
                var enteredHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var historyCalls = 0;
                using var restoringView = new ArchiveConversionView();
                restoringView.Configure(() => WinRT.Interop.WindowNative.GetWindowHandle(shell),
                    (_, _) => Task.FromResult(ContentDialogResult.None),
                    new ArchiveConversionService(FindArchiveSmokeEngine()), store,
                    async (_, token) =>
                    {
                        historyCalls++;
                        enteredHistory.TrySetResult();
                        await releaseHistory.Task.WaitAsync(token);
                    }, commitReceipts: new ArchiveCommitReceiptStore(Path.Combine(restoreRoot, "receipts")));
                host.Children.Clear();
                host.Children.Add(restoringView);
                var restoring = restoringView.RestoreAsync();
                Task? stopping = null;
                Task? flushing = null;
                try
                {
                    await enteredHistory.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Require(restoringView.Session.CanPersist && restoringView.Session.IsRestoring,
                        "The fixture did not pause after durable tasks loaded and before the adapter published restored settings.");
                    stopping = restoringView.StopAsync(preserveForRestart: true);
                    flushing = restoringView.FlushAsync();
                    Require(!stopping.IsCompleted && !flushing.IsCompleted,
                        "Close or flush passed the incomplete archive restoration.");
                    releaseHistory.TrySetResult();
                    await Task.WhenAll(restoring, stopping, flushing).WaitAsync(TimeSpan.FromSeconds(10));
                    var saved = await store.LoadAsync();
                    Require(saved.OutputDirectory == output && restoringView.OutputFolderBox.Text == output &&
                        restoringView.Session.OutputDirectory == output,
                        "Close or flush replaced the restored output folder with an uninitialized text control value.");
                    Require(saved.Tasks.Count == 2 && restoringView.Tasks.Count == 2 && historyCalls == 1 &&
                        saved.Tasks.Single(task => task.Id == completed.Id) is { State: ArchiveTaskState.Succeeded, HistoryRecorded: true } &&
                        saved.Tasks.Single(task => task.Id == waiting.Id).State == ArchiveTaskState.Waiting &&
                        !restoringView.IsRunning && restoringView.Session.LastBatch.Count == 0 &&
                        Directory.GetFiles(output).Length == 1 &&
                        (await File.ReadAllBytesAsync(published)).SequenceEqual(publishedBytes),
                        "Overlapping restore, close, and flush dropped a task, repeated history, or automatically converted work.");
                }
                finally
                {
                    releaseHistory.TrySetResult();
                    await Task.WhenAll(new[] { restoring, stopping, flushing }.OfType<Task>())
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    host.Children.Clear();
                }
            });

            await CheckAsync("Archive restore recognizes an already published verified result before the succeeded checkpoint", async () =>
            {
                var crashRoot = Path.Combine(fixture, "publication-gap");
                Directory.CreateDirectory(crashRoot);
                var staged = Path.Combine(crashRoot, "staged.zip");
                var final = Path.Combine(crashRoot, "committed.zip");
                File.Copy(source, staged);
                var checkpoint = new ArchiveTaskCheckpoint
                {
                    SourcePath = source, TargetFormat = ArchiveFormat.Zip, OutputDirectory = crashRoot,
                    State = ArchiveTaskState.Running
                };
                var receipts = new ArchiveCommitReceiptStore(Path.Combine(crashRoot, "receipts"));
                await receipts.PrepareAsync(checkpoint, staged, final);
                File.Move(staged, final);
                var crashStore = new ArchiveTaskStore(Path.Combine(crashRoot, "archive-tasks.json"));
                await crashStore.SaveAsync(new() { OutputDirectory = crashRoot, Tasks = new[] { checkpoint } });
                using var recoveredView = new ArchiveConversionView();
                recoveredView.Configure(() => WinRT.Interop.WindowNative.GetWindowHandle(shell),
                    (_, _) => Task.FromResult(ContentDialogResult.None), taskStore: crashStore,
                    recordHistory: async (item, token) => { await historyStore.RecordCompletedAsync(item, token); },
                    commitReceipts: receipts);
                await recoveredView.RestoreAsync();
                var task = recoveredView.Tasks.Single();
                Require(task.State == ArchiveTaskState.Succeeded && task.OutputPath == final &&
                    task.HistoryRecorded && !recoveredView.IsRunning,
                    "An archive published before a checkpoint was queued for a duplicate conversion.");
                await recoveredView.FlushAsync();
            });
        }
        finally
        {
            if (view is not null) { await view.StopAsync(); view.Dispose(); }
            host.Children.Clear();
            shell.Close();
            LocalizationService.SetLanguage(language);
        }
    }
}
