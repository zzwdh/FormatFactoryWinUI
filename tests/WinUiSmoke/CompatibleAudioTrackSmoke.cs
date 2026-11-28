using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckCompatibleAudioTrackAsync()
    {
        await CheckAsync("Real compatible-audio opt-in retains its warning, queue choice and lossless admission guard", async () =>
        {
            using var motionScope = new SettingsActionsMotionScope(_window);
            var fixture = Path.Combine(_runRoot, "compatible-audio", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var input = Path.Combine(fixture, "synthetic-spatial-video.mov");
            byte[] inputBytes = [11, 29, 43, 71];
            await File.WriteAllBytesAsync(input, inputBytes);
            var durable = new QueueStore(Path.Combine(fixture, "queue.json"), () => []);
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            MainWindow? window = null;
            QueueSession? session = null;
            var reported = new List<Exception>();
            Action<Exception> observe = reported.Add;
            var feedbackBefore = _feedbackHttpRequests;
            try
            {
                var services = new MainWindowServices(new SettingsService(Path.Combine(fixture, "settings.json")),
                    durable, new LocalImportStorageAccess(),
                    feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)));
                window = new MainWindow(services);
                DetachSmokeWindowStartup(window);
                _window = window;
                SetField("_initializing", false);
                // Availability-only values: no encoder is started by this UI fixture.
                SetField("_ffmpegPath", Path.Combine(fixture, "availability-only-ffmpeg.exe"));
                SetField("_ffprobePath", Path.Combine(fixture, "availability-only-ffprobe.exe"));
                var probe = new MediaProbeResult
                {
                    Kind = MediaKind.Video, VideoCodec = "h264", VideoStreamIndex = 0,
                    Width = 16, Height = 16, Duration = TimeSpan.FromSeconds(1),
                    AudioTracks = new AudioTrackInfo[]
                    {
                        new() { Codec = "apple_apac", CodecTag = "apac", Channels = 4, StreamIndex = 3, RelativeIndex = 0 },
                        new() { Codec = "aac", Channels = 2, StreamIndex = 7, RelativeIndex = 1, Language = "en" }
                    }
                };
                var model = new MediaTask
                {
                    InputPath = input, SourceFormatId = "mov", TargetFormatId = "mp4",
                    Probe = probe, ConversionMode = "balanced", State = ConversionState.Waiting
                };
                foreach (var format in new[] { "MP4", "MP3", "PNG" }) model.AvailableFormats.Add(format);
                foreach (var track in probe.AudioTracks) model.AudioTracks.Add(track);
                session = (QueueSession)Field("_queueSession")!;
                Require(session.AddTask(model), "The compatible-audio fixture task was rejected.");
                var task = window.Tasks.Single();
                task.IsDetailsExpanded = true;
                var row = CreateQueueRowPresenter(task);
                // Host the real template in an off-screen window without taking focus.
                ((Grid)Field("RootGrid")!).Children.Add(row);
                window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
                window.AppWindow.Show(false);
                var loadedDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (!row.IsLoaded && DateTimeOffset.UtcNow < loadedDeadline) await Task.Delay(30);
                Require(row.IsLoaded, "The compatible-audio queue template was not loaded.");
                // Realize content presenters so CheckBox content inherits the row's context.
                row.Measure(new Windows.Foundation.Size(900, double.PositiveInfinity));
                row.Arrange(new Windows.Foundation.Rect(0, 0, 900, row.DesiredSize.Height));
                row.UpdateLayout();
                var checkbox = FindDescendants<CheckBox>(row).Single(control => control.Name == "CompatibleAudioTrackCheckBox");
                var warning = FindDescendants<TextBlock>(row).Single(control => control.Name == "CompatibleAudioTrackWarningText");
                checkbox.ApplyTemplate();
                await Task.Delay(30);
                var start = FindDescendants<Button>(row).Single(button => button.Content is FontIcon { Glyph: "\uE768" });
                SafeDiagnostics.ErrorReported += observe;

                Require(!task.UseCompatibleAudioTrack && checkbox.IsChecked != true && checkbox.IsEnabled
                    && !CompatibleAudioElementCollapsed(checkbox),
                    "Compatible stereo audio was enabled by default or the available opt-in was hidden.");
                checkbox.IsChecked = true;
                Require(task.UseCompatibleAudioTrack && model.Options.GetValueOrDefault(CompatibleAudioTrackPolicy.OptionKey)
                    == CompatibleAudioTrackPolicy.CompatibleValue, "The real checkbox did not persist the explicit opt-in.");
                await WaitForCompatibleChoiceAsync(true);
                var request = ConversionRequestFactory.Capture(model, new AppSettings());
                checkbox.IsChecked = false;
                await WaitForCompatibleChoiceAsync(false);
                Require(request.Options.GetValueOrDefault(CompatibleAudioTrackPolicy.OptionKey)
                    == CompatibleAudioTrackPolicy.CompatibleValue,
                    "Editing the UI changed an already captured conversion request.");
                checkbox.IsChecked = true;
                await WaitForCompatibleChoiceAsync(true);

                foreach (var language in new[] { "en-US", "zh-CN", "pt-BR" })
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    await Task.Delay(30);
                    Require(task.UseCompatibleAudioTrack && checkbox.IsChecked == true,
                        $"Localization changed the explicit compatible-audio choice ({language}).");
                    Require(checkbox.Content is TextBlock label
                        && ReferenceEquals(label.DataContext, task)
                        && label.Text == LocalizationService.T("MediaCompatibilityAacOption"),
                        $"Compatible-audio label binding failed ({language}); context=" +
                        $"{(checkbox.Content as TextBlock)?.DataContext?.GetType().Name ?? "null"}; " +
                        $"text='{(checkbox.Content as TextBlock)?.Text}'; " +
                        $"path='{(checkbox.Content as TextBlock)?.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding?.Path?.Path}'; " +
                        $"header='{task.CompatibleAudioTrackHeader}'; warning='{warning.Text}'.");
                    Require(warning.Text == LocalizationService.T("MediaCompatibilityAacWarning")
                        && !CompatibleAudioElementCollapsed(warning),
                        $"Compatible-audio omission warning failed ({language}); text='{warning.Text}'.");
                }

                task.ConversionMode = "lossless";
                Require(task.UseCompatibleAudioTrack && !task.IsTargetAvailable && !start.IsEnabled
                    && Invoke("EnsureTaskEngineReady", task) is false
                    && task.EngineRequirement == LocalizationService.T("MediaCompatibilityAacLossless"),
                    "Lossless mode silently cleared the choice or admitted a conversion that omits audio tracks.");
                Invoke("StartTaskButton_Click", start, new RoutedEventArgs());
                Require(model.State == ConversionState.Waiting && !services.Conversions.Conversion.HasActiveTasks,
                    "The blocked lossless action started an encoder.");
                checkbox.IsChecked = false;
                Require(!task.UseCompatibleAudioTrack && !checkbox.IsEnabled,
                    "Lossless mode did not permit cancelling the opt-in, or permits enabling it again.");
                task.ConversionMode = "balanced";
                checkbox.IsChecked = true;
                Require(task.IsTargetAvailable && start.IsEnabled,
                    "Returning to normal conversion did not restore the valid compatible-audio choice.");
                model.State = ConversionState.Running;
                await YieldLayoutAsync();
                row.UpdateLayout();
                Require(!FindDescendants<CheckBox>(row).Any(control => control.Name == "CompatibleAudioTrackCheckBox"),
                    "A running conversion retained its editable audio choice.");
                task.UseCompatibleAudioTrack = false;
                Require(task.UseCompatibleAudioTrack, "A running conversion changed its captured audio choice.");
                model.State = ConversionState.Waiting;
                await YieldLayoutAsync();
                row.UpdateLayout();
                checkbox = FindDescendants<CheckBox>(row).Single(control => control.Name == "CompatibleAudioTrackCheckBox");
                Require(checkbox.IsEnabled && checkbox.IsChecked == true,
                    "Returning to an editable state lost the saved compatible audio choice.");

                model.Probe = new MediaProbeResult
                {
                    Kind = MediaKind.Video, VideoCodec = "h264", VideoStreamIndex = 0,
                    AudioTracks = new AudioTrackInfo[] { probe.AudioTracks[0] }
                };
                Require(!task.IsTargetAvailable && !start.IsEnabled
                    && Invoke("EnsureTaskEngineReady", task) is false,
                    "A missing AAC track fell back to APAC or remained admitted.");
                checkbox.IsChecked = false;
                Require(!task.UseCompatibleAudioTrack && CompatibleAudioElementCollapsed(checkbox),
                    "A pure APAC input exposes a usable compatible-audio opt-in.");
                model.Probe = probe;
                task.TargetFormat = "MP3";
                checkbox.IsChecked = true;
                Require(task.UseCompatibleAudioTrack && task.IsTargetAvailable,
                    "The compatible stereo choice is unavailable for audio extraction.");
                await WaitForCompatibleChoiceAsync(true);
                var persisted = (await durable.LoadResultAsync()).Items.Single();
                Require(persisted.TargetFormatId == "mp3" && persisted.SelectedAudioTrack == 0,
                    "Audio compatibility changed the independent legacy audio-track selector.");
                Require(reported.Count == 0 && _feedbackHttpRequests == feedbackBefore
                    && session.Tasks.Count == 1 && model.State == ConversionState.Waiting
                    && (await File.ReadAllBytesAsync(input)).SequenceEqual(inputBytes),
                    "The opt-in checks reported errors, sent feedback, started conversion or changed the source.");

                async Task WaitForCompatibleChoiceAsync(bool expected)
                {
                    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                    do
                    {
                        var item = (await durable.LoadResultAsync()).Items.SingleOrDefault();
                        var actual = item?.Options.GetValueOrDefault(CompatibleAudioTrackPolicy.OptionKey)
                            == CompatibleAudioTrackPolicy.CompatibleValue;
                        if (item is not null && actual == expected && item.TargetFormatId == model.TargetFormatId
                            && item.ConversionMode == model.ConversionMode) return;
                        await Task.Delay(30);
                    } while (DateTimeOffset.UtcNow < deadline);
                    throw new InvalidOperationException("The real queue binding did not save its audio opt-in.");
                }
            }
            finally
            {
                SafeDiagnostics.ErrorReported -= observe;
                try
                {
                    if (session is not null) await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    try { window?.Close(); }
                    finally
                    {
                        _window = originalWindow;
                        LocalizationService.SetLanguage(originalLanguage);
                        if (_window is not null) Invoke("ApplyLocalization");
                    }
                }
            }
        });
    }

    private static bool CompatibleAudioElementCollapsed(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: Visibility.Collapsed }) return true;
        return false;
    }
}
