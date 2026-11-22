using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;

namespace FormatFactoryWinUI;

public partial class App
{
    public async Task CheckTelemetryPreferencesAsync()
    {
        await CheckAsync("Telemetry switches load without saving, persist independently and survive localization", async () =>
        {
            using var motionScope = new SettingsActionsMotionScope(_window);
            Require(Path.GetFullPath(LogService.AppDataRoot).StartsWith(
                Path.TrimEndingDirectorySeparator(_runRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Telemetry checks must use the isolated smoke data root.");
            var fixture = Path.Combine(_runRoot, "telemetry-settings", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var settingsPath = Path.Combine(fixture, "settings.json");
            var service = new SettingsService(settingsPath);
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            MainWindow? window = null;
            var saves = 0;
            try
            {
                var defaults = await service.LoadAsync();
                var defaultView = new SettingsView();
                defaultView.ApplyTelemetryPreferences(defaults);
                Require(defaults.EnableUsageTelemetry && defaults.EnableAutomaticErrorReporting
                    && defaultView.UsageTelemetryToggle.IsOn && defaultView.AutomaticErrorReportingToggle.IsOn,
                    "A new installation did not enable both telemetry preferences by default.");

                defaults.EnableUsageTelemetry = false;
                await service.SaveAsync(defaults);
                var services = new MainWindowServices(service,
                    new QueueStore(Path.Combine(fixture, "pending-queue.json"), () => []),
                    new LocalImportStorageAccess());
                Require(!services.EnableRemoteTelemetry, "The telemetry settings fixture enabled a real receiver.");
                window = new MainWindow(services);
                DetachSmokeWindowStartup(window);
                _window = window;
                SetField("_settingsCommits", new SettingsCommitCoordinator(async candidate =>
                {
                    await service.SaveAsync(candidate);
                    Interlocked.Increment(ref saves);
                }, (_, _) => Task.CompletedTask));
                SetField("_settings", await service.LoadAsync());
                SetField("_settingsAvailable", true);
                var originalBytes = await File.ReadAllBytesAsync(settingsPath);
                Invoke("InitializeTelemetry");
                Invoke("EnsureSettingsViewLoaded");
                var view = (SettingsView)Field("SettingsView")!;
                Require(!view.UsageTelemetryToggle.IsOn && view.AutomaticErrorReportingToggle.IsOn
                    && !(bool)Field("_usageTelemetryEnabled")! && (bool)Field("_automaticErrorsEnabled")!,
                    "Initializing the actual settings page did not preserve the two saved preferences.");
                Require(Volatile.Read(ref saves) == 0
                    && (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(originalBytes)
                    && Field("_telemetry") is null,
                    "Initializing telemetry controls saved preferences or started a network service.");
                SetField("_initializing", false);

                async Task WaitForPreferencesAsync(bool usage, bool errors, int expectedSaves)
                {
                    for (var attempt = 0; attempt < 100; attempt++)
                    {
                        var current = (AppSettings)Field("_settings")!;
                        if (Volatile.Read(ref saves) == expectedSaves
                            && current.EnableUsageTelemetry == usage
                            && current.EnableAutomaticErrorReporting == errors
                            && (bool)Field("_usageTelemetryEnabled")! == usage
                            && (bool)Field("_automaticErrorsEnabled")! == errors) break;
                        await Task.Delay(25);
                    }
                    var persisted = await service.LoadAsync();
                    Require(Volatile.Read(ref saves) == expectedSaves
                        && persisted.EnableUsageTelemetry == usage
                        && persisted.EnableAutomaticErrorReporting == errors
                        && view.UsageTelemetryToggle.IsOn == usage
                        && view.AutomaticErrorReportingToggle.IsOn == errors
                        && (bool)Field("_usageTelemetryEnabled")! == usage
                        && (bool)Field("_automaticErrorsEnabled")! == errors,
                        "A telemetry switch did not persist immediately or changed the other preference.");
                    Require(view.ViewModel.DraftSession?.IsDirty == false,
                        "Changing telemetry incorrectly required saving the conversion settings draft.");
                }

                view.UsageTelemetryToggle.IsOn = true;
                await WaitForPreferencesAsync(usage: true, errors: true, expectedSaves: 1);
                view.UsageTelemetryToggle.IsOn = false;
                await WaitForPreferencesAsync(usage: false, errors: true, expectedSaves: 2);
                view.AutomaticErrorReportingToggle.IsOn = false;
                await WaitForPreferencesAsync(usage: false, errors: false, expectedSaves: 3);
                view.UsageTelemetryToggle.IsOn = true;
                await WaitForPreferencesAsync(usage: true, errors: false, expectedSaves: 4);

                var savedBytes = await File.ReadAllBytesAsync(settingsPath);
                foreach (var language in new[] { "en-US", "zh-Hans", "ar-SA" })
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    Require(view.TelemetryTitleText.Text == LocalizationService.T("TelemetryTitle")
                        && (string)view.UsageTelemetryToggle.Header == LocalizationService.T("TelemetryUsageTitle")
                        && (string)view.AutomaticErrorReportingToggle.Header == LocalizationService.T("TelemetryErrorsTitle"),
                        "Telemetry switch titles were not localized on the actual settings page.");
                    Require(view.UsageTelemetryToggle.IsOn && !view.AutomaticErrorReportingToggle.IsOn
                        && (bool)Field("_usageTelemetryEnabled")! && !(bool)Field("_automaticErrorsEnabled")!,
                        "Updating the UI language reset a telemetry preference.");
                }
                Require(Volatile.Read(ref saves) == 4
                    && (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(savedBytes),
                    "Localizing telemetry switches wrote settings unexpectedly.");
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
        });
    }
}
