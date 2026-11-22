using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    public async Task CheckStoreUpdatePreferencesAsync()
    {
        await CheckAsync("Store update preferences persist without a draft save, survive localization and install only after shutdown", async () =>
        {
            using var motionScope = new SettingsActionsMotionScope(_window);
            Require(Path.GetFullPath(LogService.AppDataRoot).StartsWith(
                Path.TrimEndingDirectorySeparator(_runRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Update checks must use the isolated smoke data root.");
            var fixture = Path.Combine(_runRoot, "store-update-settings", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var settingsPath = Path.Combine(fixture, "settings.json");
            var queuePath = Path.Combine(fixture, "pending-queue.json");
            var service = new SettingsService(settingsPath);
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            MainWindow? window = null;
            StoreUpdateCoordinator? updates = null;
            Action<StoreUpdateStatus>? statusChanged = null;
            TaskCompletionSource<bool>? blockedSave = null;
            var saves = 0;
            try
            {
                var defaults = await service.LoadAsync();
                var defaultView = new SettingsView();
                defaultView.ApplyUpdatePreferences(defaults);
                Require(defaults.EnableAutomaticUpdates && defaultView.AutomaticUpdatesToggle.IsOn,
                    "A new installation did not enable automatic updates by default.");

                defaults.EnableAutomaticUpdates = false;
                await service.SaveAsync(defaults);
                var services = new MainWindowServices(service,
                    new QueueStore(queuePath, () => []), new LocalImportStorageAccess());
                Require(!services.EnableRemoteTelemetry, "The update fixture enabled a real telemetry receiver.");
                window = new MainWindow(services);
                DetachSmokeWindowStartup(window);
                _window = window;
                var session = (QueueSession)Field("_queueSession")!;
                var client = new StoreUpdateSmokeClient(() => session.IsStopping && File.Exists(queuePath));
                updates = new StoreUpdateCoordinator(client);
                SetField("_storeUpdateClient", client);
                SetField("_storeUpdates", updates);
                // Exercise the production status handler without creating a native StoreContext.
                statusChanged = typeof(MainWindow).GetMethod("StoreUpdateStatusChanged", InstanceMembers)!
                    .CreateDelegate<Action<StoreUpdateStatus>>(window);
                updates.StatusChanged += statusChanged;
                SetField("_settingsCommits", new SettingsCommitCoordinator(async candidate =>
                {
                    if (blockedSave is { } pending) await pending.Task;
                    await service.SaveAsync(candidate);
                    Interlocked.Increment(ref saves);
                }, (_, _) => Task.CompletedTask));
                SetField("_settings", await service.LoadAsync());
                SetField("_settingsAvailable", true);
                var originalBytes = await File.ReadAllBytesAsync(settingsPath);
                Invoke("ApplyAutomaticUpdatesFromSettings");
                Invoke("EnsureSettingsViewLoaded");
                var view = (SettingsView)Field("SettingsView")!;
                Require(!view.AutomaticUpdatesToggle.IsOn && !updates.EnableAutomaticUpdates,
                    "Initializing the settings page did not preserve the saved update preference.");
                Require(Volatile.Read(ref saves) == 0
                    && (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(originalBytes)
                    && client.Checks == 0 && client.Downloads == 0 && client.Installs == 0,
                    "Backfilling update controls saved settings or started an update operation.");
                SetField("_initializing", false);

                async Task WaitForPreferenceAsync(bool enabled, int expectedSaves)
                {
                    for (var attempt = 0; attempt < 100; attempt++)
                    {
                        if (Volatile.Read(ref saves) == expectedSaves
                            && ((AppSettings)Field("_settings")!).EnableAutomaticUpdates == enabled
                            && updates.EnableAutomaticUpdates == enabled
                            && (int)Field("_updatePreferenceSaves")! == 0) break;
                        await Task.Delay(25);
                    }
                    var persisted = await service.LoadAsync();
                    Require(Volatile.Read(ref saves) == expectedSaves
                        && persisted.EnableAutomaticUpdates == enabled
                        && view.AutomaticUpdatesToggle.IsOn == enabled
                        && updates.EnableAutomaticUpdates == enabled,
                        "The update switch did not immediately persist its preference.");
                    Require(view.ViewModel.DraftSession?.IsDirty == false,
                        "Changing automatic updates incorrectly dirtied conversion settings.");
                }

                view.AutomaticUpdatesToggle.IsOn = true;
                await WaitForPreferenceAsync(enabled: true, expectedSaves: 1);
                blockedSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
                view.AutomaticUpdatesToggle.IsOn = false;
                Require(!updates.EnableAutomaticUpdates && Volatile.Read(ref saves) == 1
                    && ((AppSettings)Field("_settings")!).EnableAutomaticUpdates,
                    "Opting out waited for disk persistence before disabling automatic work.");
                var exitSave = InvokeTask("WaitForStoreUpdatePreferenceSaveAsync");
                Require(!exitSave.IsCompleted,
                    "Normal exit did not wait for the blocked update-preference save.");
                blockedSave.TrySetResult(true);
                await exitSave.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForPreferenceAsync(enabled: false, expectedSaves: 2);
                blockedSave = null;

                var savedBytes = await File.ReadAllBytesAsync(settingsPath);
                foreach (var language in new[] { "en-US", "zh-Hans", "ar-SA" })
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    Require(view.UpdatesTitleText.Text == LocalizationService.T("StoreUpdatesTitle")
                        && (string)view.AutomaticUpdatesToggle.Header == LocalizationService.T("StoreUpdatesAutomaticTitle")
                        && (string)view.AutomaticUpdatesToggle.OnContent == LocalizationService.T("HardwareOn")
                        && (string)view.AutomaticUpdatesToggle.OffContent == LocalizationService.T("HardwareOff"),
                        "Update preference labels were not redrawn in the selected language.");
                    Require(!view.AutomaticUpdatesToggle.IsOn && !updates.EnableAutomaticUpdates,
                        "Changing the UI language re-enabled automatic updates.");
                }
                Require(Volatile.Read(ref saves) == 2
                    && (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(savedBytes),
                    "Localizing update settings unexpectedly wrote preferences.");

                Invoke("CheckForUpdatesButton_Click", view.CheckForUpdatesButton, null);
                await WaitForStoreUpdateSmokeAsync(() => client.Checks == 1 && !updates.IsBusy);
                Require(client.Checks == 1 && client.Downloads == 0 && client.Installs == 0,
                    "A manual query with automatic updates off downloaded or installed an update.");

                view.AutomaticUpdatesToggle.IsOn = true;
                await WaitForPreferenceAsync(enabled: true, expectedSaves: 3);
                Invoke("StoreUpdateTimer_Tick", null, null);
                await WaitForStoreUpdateSmokeAsync(() => updates.Status == StoreUpdateStatus.Ready && !updates.IsBusy);
                Require(client.Checks == 2 && client.Downloads == 1 && client.Installs == 0
                    && updates.Status == StoreUpdateStatus.Ready,
                    "The automatic check did not download the fake update without installing it.");

                SetField("_closeSaveInProgress", true);
                await InvokeTask("TryInstallStoreUpdateOnExitAsync");
                Require(!session.IsStopping && client.Installs == 0
                    && ((NavigationView)Field("NavView")!).IsEnabled,
                    "Exit installation started before the queue was stopped and saved.");
                SetField("_closeSaveInProgress", false);

                // Checkpoint the real, isolated queue before testing the final installation gate.
                await session.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var pendingInput = Path.Combine(fixture, "pending.png");
                await File.WriteAllBytesAsync(pendingInput, [1, 2, 3]);
                Require(session.AddTask(new MediaTask
                {
                    InputPath = pendingInput, ContentKind = ContentKind.Image,
                    SourceFormatId = "png", TargetFormatId = "png", TargetFormat = "PNG"
                }), "The isolated queue did not admit the pending fixture.");
                var shutdown = await session.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Require(shutdown.CanClose && session.IsStopping && File.Exists(queuePath),
                    "The isolated queue did not save its normal-exit checkpoint.");
                SetField("_closeSaveInProgress", true);
                await InvokeTask("TryInstallStoreUpdateOnExitAsync");
                Require(client.Installs == 1 && updates.Status == StoreUpdateStatus.UpToDate
                    && !((NavigationView)Field("NavView")!).IsEnabled,
                    "A saved, stopped session did not install through the fake client after disabling new UI actions.");
            }
            finally
            {
                blockedSave?.TrySetResult(true);
                if (updates is not null && statusChanged is not null) updates.StatusChanged -= statusChanged;
                try { window?.Close(); }
                finally
                {
                    updates?.Dispose();
                    _window = originalWindow;
                    LocalizationService.SetLanguage(originalLanguage);
                    if (_window is not null) Invoke("ApplyLocalization");
                }
            }
        });
    }

    private static async Task WaitForStoreUpdateSmokeAsync(Func<bool> completed)
    {
        for (var attempt = 0; attempt < 100 && !completed(); attempt++) await Task.Delay(25);
    }

    private sealed class StoreUpdateSmokeClient(Func<bool> canInstall) : IStoreUpdateClient
    {
        private int _checks;
        private int _downloads;
        private int _installs;
        public bool IsAvailable => true;
        public int Checks => Volatile.Read(ref _checks);
        public int Downloads => Volatile.Read(ref _downloads);
        public int Installs => Volatile.Read(ref _installs);

        public Task<StoreUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _checks);
            return Task.FromResult(new StoreUpdateCheckResult(HasUpdates: true, CanDownloadSilently: true));
        }

        public Task<bool> DownloadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _downloads);
            return Task.FromResult(true);
        }

        public Task<bool> InstallAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(canInstall(), "The fake installer was called before the queue checkpoint was safe.");
            Interlocked.Increment(ref _installs);
            return Task.FromResult(true);
        }
    }
}
