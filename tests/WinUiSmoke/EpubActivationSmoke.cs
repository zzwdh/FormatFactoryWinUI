using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckEpubActivationTargetsAsync()
    {
        await CheckAsync("Explicit EPUB activation target application preserves its request and blocks conversion", () =>
            CheckEpubActivationTargetAsync("epub"));
        await CheckAsync("Explicit MOBI activation target application retains its request and requires Calibre", () =>
            CheckEpubActivationTargetAsync("mobi"));
    }

    private async Task CheckEpubActivationTargetAsync(string requestedTarget)
    {
        var originalWindow = _window;
        var originalLanguage = LocalizationService.CurrentUICulture.Name;
        using var motion = new SettingsActionsMotionScope(originalWindow);
        var fixture = Path.Combine(_runRoot, "epub-activation", requestedTarget, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        // Use a valid publication with an upper-case extension to exercise the real import path.
        var source = Path.Combine(fixture, "synthetic-book.EPUB");
        var document = new NormalizedDocument("Activation route fixture", "en",
            new NormalizedSection[] { new(null, new NormalizedBlock[]
                { new NormalizedParagraph("Synthetic EPUB activation regression.") }) },
            Array.Empty<NormalizedResource>());
        await new Epub3Writer().WriteAsync(new NormalizedDocumentEpubAdapter().CreatePublication(document), source);
        var validation = await new EpubStructureValidator().ValidateAsync(source);
        Require(validation.IsValid, "The EPUB activation fixture is invalid.");
        var originalBytes = await File.ReadAllBytesAsync(source);
        var durable = new QueueStore(Path.Combine(fixture, "queue.json"), () => []);
        var services = new MainWindowServices(new SettingsService(Path.Combine(fixture, "settings.json")),
            durable, new LocalImportStorageAccess(),
            feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)));
        var window = new MainWindow(services);
        DetachSmokeWindowStartup(window);
        _window = window;
        var session = (QueueSession)Field("_queueSession")!;
        var reported = new List<Exception>();
        Action<Exception> observe = reported.Add;
        var beforeFeedback = Volatile.Read(ref _feedbackHttpRequests);
        SafeDiagnostics.ErrorReported += observe;
        try
        {
            LocalizationService.SetLanguage("en-US");
            SetField("_ffmpegPath", string.Empty);
            SetField("_ffprobePath", string.Empty);
            SetField("_documentTools", new Dictionary<ExternalToolKind, ExternalToolInfo>());
            await session.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // This isolated helper bypasses production startup and supplies its
            // own discovery results. Its restored queue is now ready for import.
            SetField("_initializing", false);
            var existingTaskIds = window.Tasks.Select(task => task.Id).ToHashSet();
            var activation = new ActivationRequest(new[] { source }, TargetFormatId: requestedTarget);
            // Exercise the production import and target-application stages without
            // the outer dispatch stage that shows and foregrounds the helper window.
            await ((Task)Invoke("AddIncomingPathsAsync", activation.FilePaths, null, null)!)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Invoke("ApplyActivationTarget", activation, existingTaskIds);

            var task = window.Tasks.Single();
            Require(task.Model.SourceFormatId == "epub" && task.Model.TargetFormatId == requestedTarget,
                $"Activation silently changed the explicit {requestedTarget} output to {task.Model.TargetFormatId}.");
            Require(task.AvailableFormats.Contains(FormatCatalog.FindById(requestedTarget)!.DisplayName),
                "The explicit activation target disappeared from the queue selector.");
            Require(string.IsNullOrWhiteSpace(task.Model.InputAccessToken) &&
                task.Model.Inputs.All(input => string.IsNullOrWhiteSpace(input.AccessToken)),
                "The isolated activation unexpectedly created persistent storage-access tokens.");
            var row = (FrameworkElement)((ListView)Field("TaskList")!).ItemTemplate.LoadContent();
            row.DataContext = task;
            var start = EpubRouteDescendants(row).OfType<Button>()
                .Single(button => button.Content is FontIcon { Glyph: "\uE768" });
            Require(!task.IsTargetAvailable && !start.IsEnabled &&
                Invoke("EnsureTaskEngineReady", task) is false,
                "The unavailable activation route was admitted for conversion.");
            var expectedRequirement = requestedTarget == "epub"
                ? LocalizationService.T("ConversionUiRouteNotImplemented", "epub", "epub")
                : LocalizationService.T("RequiresCalibre");
            Require(task.EngineRequirement == expectedRequirement && task.Message == expectedRequirement,
                "The activation did not show the requested route's localized requirement.");
            Invoke("StartTaskButton_Click", start, new RoutedEventArgs());
            Require(task.State == ConversionState.Waiting && !services.Conversions.QueueCoordinator.HasActiveOperations &&
                string.IsNullOrWhiteSpace(task.OutputPath),
                "Clicking the unavailable activation row started a conversion.");

            var calibre = new ExternalToolInfo(ExternalToolKind.Calibre, "Calibre", true,
                Path.Combine(fixture, "availability-only-ebook-convert.exe"), "fixture", "Available");
            SetField("_documentTools", new Dictionary<ExternalToolKind, ExternalToolInfo>
                { [ExternalToolKind.Calibre] = calibre });
            Invoke("RefreshTaskEngineAvailability");
            if (requestedTarget == "epub")
            {
                Require(!task.IsTargetAvailable && !start.IsEnabled && task.EngineRequirement == expectedRequirement &&
                    Invoke("EnsureTaskEngineReady", task) is false,
                    "Calibre availability enabled the unsupported explicit EPUB activation.");
                Invoke("StartTaskButton_Click", start, new RoutedEventArgs());
            }
            else
            {
                Require(task.IsTargetAvailable && start.IsEnabled && string.IsNullOrWhiteSpace(task.EngineRequirement) &&
                    Invoke("EnsureTaskEngineReady", task) is true,
                    "The explicit MOBI activation stayed blocked after Calibre became available.");
                // Availability is a fixture; do not launch its synthetic tool path.
                SetField("_documentTools", new Dictionary<ExternalToolKind, ExternalToolInfo>());
                Invoke("RefreshTaskEngineAvailability");
                Require(!task.IsTargetAvailable && !start.IsEnabled && task.EngineRequirement == expectedRequirement,
                    "Removing Calibre failed to restore the explicit MOBI activation's requirement.");
            }
            Require(task.Model.TargetFormatId == requestedTarget && task.State == ConversionState.Waiting &&
                !services.Conversions.QueueCoordinator.HasActiveOperations && string.IsNullOrWhiteSpace(task.OutputPath),
                "Activation checks changed the requested output or started transcoding.");
            Require(reported.Count == 0 && Volatile.Read(ref _feedbackHttpRequests) == beforeFeedback,
                "Activation route checks emitted error telemetry or feedback.");
            Require(!Directory.EnumerateFiles(fixture, "*.mobi", SearchOption.AllDirectories).Any() &&
                !Directory.EnumerateFiles(fixture, "*.azw3", SearchOption.AllDirectories).Any(),
                "Activation unexpectedly produced converted output.");
            var currentBytes = await File.ReadAllBytesAsync(source);
            Require(originalBytes.SequenceEqual(currentBytes), "Activation altered the original EPUB.");
        }
        finally
        {
            SafeDiagnostics.ErrorReported -= observe;
            await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            _window = originalWindow;
            LocalizationService.SetLanguage(originalLanguage);
        }
    }
}
