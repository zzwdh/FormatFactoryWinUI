using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckDiscRouteRecoveryAsync()
    {
        await CheckAsync("Restoring an unavailable disc with no target keeps its queue row blocked without error telemetry", async () =>
        {
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            try
            {
                foreach (var language in new[] { "es-ES", "en-US", "zh-CN" })
                {
                    // QueueStore merges existing durable records on first save; repeated runs need fresh fixtures.
                    var fixture = Path.Combine(_runRoot, "disc-route-recovery", language, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(fixture);
                    var queuePath = Path.Combine(fixture, "queue.json");
                    var durable = new QueueStore(queuePath, () => []);
                    var incomplete = new MediaTask
                    {
                        InputPath = Path.Combine(fixture, "missing.iso"), SourceFormatId = "iso",
                        ContentKind = ContentKind.Disc, State = ConversionState.Failed
                    };
                    await durable.SaveAsync(new[] { incomplete });
                    var window = new MainWindow(new MainWindowServices(new SettingsService(Path.Combine(fixture, "settings.json")),
                        durable, new LocalImportStorageAccess(),
                        feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this))));
                    DetachSmokeWindowStartup(window);
                    _window = window;
                    var session = (QueueSession)Field("_queueSession")!;
                    var reported = new List<Exception>();
                    Action<Exception> observe = reported.Add;
                    SafeDiagnostics.ErrorReported += observe;
                    try
                    {
                        LocalizationService.SetLanguage(language);
                        SetField("_ffmpegPath", string.Empty);
                        await session.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        Require(reported.Count == 0, "Restoring the incomplete disc route reported a UI exception.");
                        Require(window.Tasks.Count == 1 && session.Tasks.Count == 1,
                            "The incomplete durable record did not retain its visible queue row.");
                        var task = window.Tasks.Single();
                        Require(task.State == ConversionState.Blocked && string.IsNullOrWhiteSpace(task.Model.TargetFormatId),
                            "Unavailable input was enabled or assigned an arbitrary target.");
                        Require(!task.IsTargetAvailable && task.EngineRequirement == LocalizationService.T("ConversionUiRequestRequiresTargetFormat"),
                            "The incomplete route did not show its localized target reminder.");
                        Require(Invoke("EnsureTaskEngineReady", task) is false, "The incomplete route was accepted for conversion.");
                        var stored = (await durable.LoadResultAsync()).Items.Single();
                        Require(string.IsNullOrWhiteSpace(stored.TargetFormatId), "Restore persisted an arbitrary target.");

                        task.TargetFormat = "CSO";
                        Require(task.Model.TargetFormatId == "cso" && task.IsTargetAvailable && string.IsNullOrWhiteSpace(task.EngineRequirement),
                            "Choosing a valid built-in route did not refresh the queue row.");
                        Require(task.State == ConversionState.Blocked && reported.Count == 0,
                            "Choosing a format started the unavailable source or reported an exception.");
                    }
                    finally
                    {
                        SafeDiagnostics.ErrorReported -= observe;
                        await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                        window.Close();
                        _window = originalWindow;
                    }
                }
            }
            finally
            {
                _window = originalWindow;
                LocalizationService.SetLanguage(originalLanguage);
            }
        });

        Check("Disc engine refresh rejects a missing source format without calling the strict backend planner", () =>
        {
            var model = new MediaTask { InputPath = Path.Combine(_runRoot, "no-extension"), ContentKind = ContentKind.Disc,
                TargetFormat = "CSO", State = ConversionState.Blocked };
            using var task = new MediaTaskViewModel(model);
            Invoke("UpdateTaskEngineAvailability", task);
            Require(!task.IsTargetAvailable && task.EngineRequirement == LocalizationService.T("ConversionUiQueueBlocked"),
                "An unrecognized source route was enabled or lost its localized reminder.");
            Require(Invoke("EnsureTaskEngineReady", task) is false, "The missing source route was accepted.");
        });

        Check("Valid built-in ISO and CSO routes remain available without external disc engines", () =>
        {
            var originalFfmpeg = Field("_ffmpegPath");
            try
            {
                SetField("_ffmpegPath", string.Empty);
                foreach (var route in new[] { (Source: "iso", Target: "CSO"), (Source: "cso", Target: "ISO") })
                {
                    using var task = new MediaTaskViewModel(new MediaTask
                    {
                        InputPath = Path.Combine(_runRoot, "synthetic." + route.Source), SourceFormatId = route.Source,
                        ContentKind = ContentKind.Disc, TargetFormat = route.Target
                    });
                    Invoke("UpdateTaskEngineAvailability", task);
                    Require(task.IsTargetAvailable && string.IsNullOrWhiteSpace(task.EngineRequirement),
                        "A valid built-in disc-image route unexpectedly required an external engine.");
                }
            }
            finally { SetField("_ffmpegPath", originalFfmpeg); }
        });
    }
}
