using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private ContentPresenter CreateQueueRowPresenter(MediaTaskViewModel task) => new()
    {
        ContentTemplate = ((ListView)Field("TaskList")!).ItemTemplate,
        Content = task
    };

    private async Task CheckPdfPasswordIdentityAsync()
    {
        await CheckAsync("A loaded PDF password editor follows its task when the same row is rebound", async () =>
        {
            var originalWindow = _window;
            var fixture = Path.Combine(_runRoot, "pdf-password-identity");
            Directory.CreateDirectory(fixture);
            MainWindow? window = null;
            FrameworkElement? row = null;
            try
            {
                window = new MainWindow(new MainWindowServices(
                    new SettingsService(Path.Combine(fixture, "settings.json")),
                    new QueueStore(Path.Combine(fixture, "queue.json"), () => []), new LocalImportStorageAccess()));
                DetachSmokeWindowStartup(window);
                _window = window;
                SetField("_initializing", false);
                var first = (MediaTaskViewModel)Invoke("AddImportedTask", new MediaTask
                {
                    InputPath = Path.Combine(fixture, "first.pdf"), SourceFormatId = "pdf",
                    ContentKind = ContentKind.Document, TargetFormatId = "txt"
                })!;
                var second = (MediaTaskViewModel)Invoke("AddImportedTask", new MediaTask
                {
                    InputPath = Path.Combine(fixture, "second.pdf"), SourceFormatId = "pdf",
                    ContentKind = ContentKind.Document, TargetFormatId = "txt"
                })!;
                var passwords = (Dictionary<Guid, string>)Field("_pdfPasswords")!;
                passwords[first.Id] = "first-password";
                passwords[second.Id] = "second-password";
                row = (FrameworkElement)((ListView)Field("TaskList")!).ItemTemplate.LoadContent();
                row.DataContext = first;
                ((Grid)Field("RootGrid")!).Children.Add(row);
                window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
                window.AppWindow.Show(false);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (!row.IsLoaded && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
                Require(row.IsLoaded, "The actual PDF task template did not load.");
                var password = FindDescendants<PasswordBox>(row).Single();
                Require(password.Password == "first-password", "Initial PDF task password was not restored.");
                var loads = 0;
                password.Loaded += (_, _) => ++loads;
                // The real virtualized template can receive another data context without Loaded.
                row.DataContext = second;
                await Task.Delay(25);
                Require(loads == 0 && ReferenceEquals(password.DataContext, second),
                    "The fixture did not reuse the same loaded password editor.");
                Require(password.Password == "second-password" && passwords[first.Id] == "first-password"
                    && passwords[second.Id] == "second-password",
                    "Rebinding the loaded password editor retained another task's password or overwrote its saved value.");
                password.Password = "second-edited";
                await Task.Delay(25); // PasswordChanged may be queued by the native control.
                Require(passwords[second.Id] == "second-edited" && passwords[first.Id] == "first-password",
                    "Editing the rebound PDF password changed the wrong task.");
                row.DataContext = null;
                await Task.Delay(25);
                Require(password.Password.Length == 0 && passwords[second.Id] == "second-edited",
                    "An unbound PDF editor retained a password or cleared the previous task's value.");
                row.DataContext = first;
                await Task.Delay(25);
                Require(password.Password == "first-password", "Returning to the original PDF did not restore its own password.");
            }
            finally
            {
                if (row is not null && window?.Content is Grid root) root.Children.Remove(row);
                window?.Close();
                _window = originalWindow;
            }
        });
    }

    private async Task CheckRestoredAudioTracksAsync()
    {
        foreach (var (selected, count) in new[] { (0, 1), (1, 2), (2, 3), (4, 2) })
        {
            await CheckAsync($"Real restored audio selector retains index {selected} while rebuilding {count} tracks", async () =>
            {
                using var motionScope = new SettingsActionsMotionScope(_window);
                var fixture = Path.Combine(_runRoot, "restored-audio-binding", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixture);
                var source = Path.Combine(fixture, "synthetic-input.mov");
                byte[] sourceBytes = [13, 31, 67, 89];
                await File.WriteAllBytesAsync(source, sourceBytes);
                var durable = new QueueStore(Path.Combine(fixture, "queue.json"), () => []);
                await durable.SaveAsync(new[] { new MediaTask
                {
                    Kind = MediaKind.Video, InputPath = source, SourceFormatId = "mov",
                    TargetFormatId = "mp3", SelectedAudioTrack = selected, State = ConversionState.Waiting
                } });
                var durableBytes = await File.ReadAllBytesAsync(Path.Combine(fixture, "queue.json"));
                var originalWindow = _window;
                var originalLanguage = LocalizationService.CurrentUICulture.Name;
                var feedbackBefore = _feedbackHttpRequests;
                MainWindow? window = null;
                QueueSession? session = null;
                MediaTaskViewModel? rowModel = null;
                FrameworkElement? row = null;
                var published = new TaskCompletionSource<MediaTask>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseImport = new TaskCompletionSource<MediaTask>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? restore = null;
                var reported = new List<Exception>();
                Action<Exception> observe = reported.Add;
                try
                {
                    var services = new MainWindowServices(new SettingsService(Path.Combine(fixture, "settings.json")),
                        new QueueStore(Path.Combine(fixture, "host-queue.json"), () => []),
                        new LocalImportStorageAccess(),
                        feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)));
                    window = new MainWindow(services);
                    DetachSmokeWindowStartup(window);
                    _window = window;
                    SetField("_initializing", false);
                    session = new QueueSession(durable,
                        async (item, _) => new MediaImportItemResult(item.InputPath, await releaseImport.Task, null),
                        (_, _, _) => throw new InvalidOperationException("Audio binding fixture must not execute conversion."),
                        (_, _) => Task.CompletedTask, DispatchAsync);
                    session.TaskAdded += model =>
                    {
                        rowModel = new MediaTaskViewModel(model) { IsDetailsExpanded = true };
                        row = CreateQueueRowPresenter(rowModel);
                        ((Grid)Field("RootGrid")!).Children.Add(row);
                        published.TrySetResult(model);
                    };
                    SafeDiagnostics.ErrorReported += observe;
                    window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
                    window.AppWindow.Show(false);
                    restore = session.RestoreAsync();
                    var restored = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var loadedDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
                    while (row?.IsLoaded != true && DateTimeOffset.UtcNow < loadedDeadline) await Task.Delay(30);
                    Require(row is { IsLoaded: true } && rowModel is not null,
                        "The actual queue audio row was not loaded before its restored probe.");
                    row!.Measure(new Windows.Foundation.Size(900, double.PositiveInfinity));
                    row.Arrange(new Windows.Foundation.Rect(0, 0, 900, row.DesiredSize.Height));
                    row.UpdateLayout();
                    ComboBox CurrentAudioSelector() => FindDescendants<ComboBox>(row).Single(control =>
                        ReferenceEquals(control.ItemsSource, rowModel!.AudioTrackOptions));
                    var audio = CurrentAudioSelector();
                    Require(restored.SelectedAudioTrack == selected && rowModel!.AudioTrackOptions.Count == 0,
                        "Preparing the restored binding changed the retained selection or prefilled tracks.");
                    var tracks = Enumerable.Range(0, count).Select(index => new AudioTrackInfo
                    {
                        RelativeIndex = index, StreamIndex = index + 1, Codec = "aac", Language = "en", Channels = 2
                    }).ToArray();
                    var imported = new MediaTask
                    {
                        Kind = MediaKind.Video, InputPath = source, SourceFormatId = "mov", TargetFormatId = "mp3",
                        Probe = new MediaProbeResult
                        {
                            Kind = MediaKind.Video, VideoCodec = "h264", Width = 16, Height = 16,
                            Duration = TimeSpan.FromSeconds(1), AudioTracks = tracks
                        }
                    };
                    foreach (var track in tracks) imported.AudioTracks.Add(track);
                    imported.AvailableFormats.Add("MP3");
                    releaseImport.TrySetResult(imported);
                    // RestoreCoreAsync calls the real ApplyProbe: Clear and one Add per source track.
                    await restore.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(30);
                    var expected = selected < count ? selected : -1;
                    Require(restored.SelectedAudioTrack == selected && audio.SelectedIndex == expected
                        && rowModel!.AudioTrackOptions.Count == count,
                        $"Restored selector lost its retained selection: model={restored.SelectedAudioTrack}, UI={audio.SelectedIndex}, options={rowModel!.AudioTrackOptions.Count}.");

                    // A populated re-probe runs the same production mutation path without replacing the row.
                    var applyProbe = typeof(QueueSession).GetMethod("ApplyProbe",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                    applyProbe.Invoke(null, new object[] { restored, imported });
                    await Task.Delay(30);
                    Require(restored.SelectedAudioTrack == selected && audio.SelectedIndex == expected,
                        "A second probe reset the retained audio selection.");
                    foreach (var language in new[] { "pt-BR", "en-US", "zh-CN" })
                    {
                        LocalizationService.SetLanguage(language);
                        rowModel.RefreshLocalization();
                        await Task.Delay(30);
                        Require(restored.SelectedAudioTrack == selected && audio.SelectedIndex == expected,
                            "A language rebuild reset or invalidated the retained audio selection.");
                    }
                    rowModel.IsDetailsExpanded = false;
                    await YieldLayoutAsync();
                    row.UpdateLayout();
                    Require(!FindDescendants<ComboBox>(row).Any(control => ReferenceEquals(control.ItemsSource, rowModel.AudioTrackOptions))
                        && restored.SelectedAudioTrack == selected, "Closing the restored editor lost its retained audio choice.");
                    rowModel.IsDetailsExpanded = true;
                    await YieldLayoutAsync();
                    row.UpdateLayout();
                    audio = CurrentAudioSelector();
                    Require(audio.IsEnabled && audio.SelectedIndex == expected && restored.SelectedAudioTrack == selected,
                        "Reopening the restored editor did not recover its audio choice.");
                    audio.SelectedIndex = 0;
                    Require(restored.SelectedAudioTrack == 0, "A real valid audio choice was not written to the model.");
                    audio.SelectedIndex = count - 1;
                    Require(restored.SelectedAudioTrack == count - 1,
                        "A later valid audio choice was not written to the model.");
                    audio.SelectedIndex = -1;
                    Require(restored.SelectedAudioTrack == count - 1,
                        "A cleared native selector overwrote the retained model selection.");
                    rowModel.RefreshLocalization();
                    Require(audio.SelectedIndex == count - 1,
                        "The retained model selection was not restored after a native clear.");
                    Require(reported.Count == 0 && _feedbackHttpRequests == feedbackBefore
                        && session.Tasks.Count == 1 && ReferenceEquals(session.Tasks[0], restored)
                        && (await File.ReadAllBytesAsync(source)).SequenceEqual(sourceBytes)
                        && (await File.ReadAllBytesAsync(Path.Combine(fixture, "queue.json"))).SequenceEqual(durableBytes),
                        "Audio binding checks reported errors, sent feedback or altered source/durable queue bytes.");

                    Task DispatchAsync(Func<Task> action)
                    {
                        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (!window.DispatcherQueue.TryEnqueue(async () =>
                        {
                            try { await action(); completion.TrySetResult(); }
                            catch (Exception exception) { completion.TrySetException(exception); }
                        })) completion.TrySetException(new InvalidOperationException("Fixture UI dispatcher stopped."));
                        return completion.Task;
                    }
                }
                finally
                {
                    SafeDiagnostics.ErrorReported -= observe;
                    releaseImport.TrySetCanceled();
                    if (restore is not null)
                    {
                        try { await restore.WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch { /* The enclosing check records the original binding failure. */ }
                    }
                    if (session is not null)
                    {
                        await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                        session.Dispose();
                    }
                    rowModel?.Dispose();
                    window?.Close();
                    _window = originalWindow;
                    LocalizationService.SetLanguage(originalLanguage);
                    if (_window is not null) Invoke("ApplyLocalization");
                }
            });
        }
    }
}
