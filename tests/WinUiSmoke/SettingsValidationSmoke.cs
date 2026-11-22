using System.Text.Json;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckSettingsValidationDiagnosticsAsync()
    {
        await CheckAsync("Expected settings validation retains the draft without error telemetry; real persistence failures remain reported", async () =>
        {
            using var motionScope = new SettingsActionsMotionScope(_window);
            var fixture = Path.Combine(_runRoot, "settings-validation");
            Directory.CreateDirectory(fixture);
            var settingsPath = Path.Combine(fixture, "settings.json");
            var settingsService = new SettingsService(settingsPath);
            var baseline = new AppSettings { WatchFolderEnabled = false, WatchFolderPath = Path.Combine(fixture, "missing-folder") };
            await settingsService.SaveAsync(baseline);
            baseline = await settingsService.LoadAsync();
            var savedBytes = await File.ReadAllBytesAsync(settingsPath);
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            MainWindow? window = null;
            var reported = new List<Exception>();
            Action<Exception> observe = reported.Add;
            var persistenceCalls = 0;
            try
            {
                var services = new MainWindowServices(settingsService,
                    new QueueStore(Path.Combine(fixture, "queue.json"), () => []), new LocalImportStorageAccess(),
                    feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)));
                window = new MainWindow(services);
                DetachSmokeWindowStartup(window);
                _window = window;
                SetField("_settings", await settingsService.LoadAsync());
                SetField("_settingsAvailable", true);
                Invoke("EnsureSettingsViewLoaded");
                SetField("_initializing", false);
                var settings = (SettingsView)Field("SettingsView")!;
                SetField("_settingsCommits", new SettingsCommitCoordinator(_ =>
                {
                    persistenceCalls++;
                    return Task.FromException(new IOException("Synthetic persistence failure."));
                }, (_, _) => Task.CompletedTask));
                SafeDiagnostics.ErrorReported += observe;

                var cases = new Action<SettingsView>[]
                {
                    view => view.WatchFolderToggle.IsOn = true,
                    view => view.FfmpegPathTextBox.Text = Path.Combine(fixture, "missing-primary.exe"),
                    view => view.CompatibilityFfmpegPathTextBox.Text = Path.Combine(fixture, "missing-compatibility.exe"),
                    view => view.LibreOfficePathTextBox.Text = Path.Combine(fixture, "missing-office.exe"),
                    view => view.CalibrePathTextBox.Text = Path.Combine(fixture, "missing-ebook.exe"),
                    view => view.Caj2PdfPathTextBox.Text = Path.Combine(fixture, "missing-caj.exe"),
                    view => view.MakeMkvPathTextBox.Text = Path.Combine(fixture, "missing-disc.exe"),
                    view => view.AudioCdRipperPathTextBox.Text = Path.Combine(fixture, "missing-ripper.exe"),
                    view => view.ReadomPathTextBox.Text = Path.Combine(fixture, "missing-reader.exe")
                };
                foreach (var language in new[] { "fr-FR", "en-US", "zh-CN" })
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    foreach (var configure in cases)
                    {
                        Invoke("ApplySettingsToControls", false);
                        configure(settings);
                        var edited = settings.CaptureSettingsDraft();
                        reported.Clear();
                        await SaveValidationFixtureAsync(settings);
                        Require(reported.Count == 0 && persistenceCalls == 0,
                            "Expected input validation reached diagnostics or persistence.");
                        Require(settings.ViewModel.DraftSession is { IsDirty: true } session && session.Current == edited
                            && settings.ViewModel.SaveStateKey == "ConversionUiSettingsSaveFailed"
                            && settings.SettingsInfoBar.IsOpen && settings.SaveSettingsButton.IsEnabled,
                            "Validation lost the editable draft or its visible local reminder.");
                        if (settings.WatchFolderToggle.IsOn)
                            Require(settings.SettingsInfoBar.Message.Contains(LocalizationService.T("WatchFolderInvalid")),
                                "Watched-folder validation lost the localized actionable message.");
                        var after = await File.ReadAllBytesAsync(settingsPath);
                        Require(after.SequenceEqual(savedBytes)
                            && JsonSerializer.Serialize((AppSettings)Field("_settings")!) == JsonSerializer.Serialize(baseline),
                            "Rejected validation changed persisted or runtime settings.");
                    }
                }

                foreach (var failure in new Exception[]
                {
                    new IOException("Synthetic settings write failure."),
                    new InvalidOperationException("Synthetic unexpected persistence failure.")
                })
                {
                    Invoke("ApplySettingsToControls", false);
                    settings.ParallelCountBox.Value = baseline.ParallelCount == 1 ? 2 : 1;
                    SetField("_settingsCommits", new SettingsCommitCoordinator(_ => Task.FromException(failure), (_, _) => Task.CompletedTask));
                    reported.Clear();
                    await SaveValidationFixtureAsync(settings);
                    Require(reported.Count == 1 && ReferenceEquals(reported[0], failure),
                        "A real persistence failure was hidden or reported more than once.");
                    Require(settings.ViewModel.DraftSession is { IsDirty: true }
                        && settings.SettingsInfoBar.Severity == InfoBarSeverity.Error,
                        "Persistence failure lost the retry draft or error feedback.");
                    var after = await File.ReadAllBytesAsync(settingsPath);
                    Require(after.SequenceEqual(savedBytes), "Persistence failure changed the saved settings.");
                }
            }
            finally
            {
                SafeDiagnostics.ErrorReported -= observe;
                if (window is not null)
                {
                    var queue = (FormatFactoryWinUI.ApplicationServices.QueueSession)Field("_queueSession")!;
                    await queue.ShutdownAsync(save: false);
                    window.Close();
                }
                _window = originalWindow;
                LocalizationService.SetLanguage(originalLanguage);
                if (_window is not null) Invoke("ApplyLocalization");
            }
        });
    }

    private async Task SaveValidationFixtureAsync(SettingsView settings)
    {
        Invoke("SaveSettingsButton_Click", settings.SaveSettingsButton, new RoutedEventArgs());
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (settings.ViewModel.IsSaving && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Require(!settings.ViewModel.IsSaving, "Settings save did not finish within the isolated check timeout.");
    }
}
