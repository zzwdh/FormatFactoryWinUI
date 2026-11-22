using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckEpubRouteAvailabilityAsync()
    {
        await CheckAsync("Restored EPUB keeps its target, blocks the unsupported route, and requires Calibre after a valid selection", async () =>
        {
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            try
            {
                foreach (var language in new[] { "pt-BR", "en-US", "zh-CN" })
                {
                    var fixture = Path.Combine(_runRoot, "epub-route", language, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(fixture);
                    var source = Path.Combine(fixture, "synthetic-book.epub");
                    var document = new NormalizedDocument("Route fixture", "en",
                        new NormalizedSection[] { new(null, new NormalizedBlock[]
                            { new NormalizedParagraph("Synthetic EPUB route regression.") }) },
                        Array.Empty<NormalizedResource>());
                    await new Epub3Writer().WriteAsync(new NormalizedDocumentEpubAdapter().CreatePublication(document), source);
                    var validation = await new EpubStructureValidator().ValidateAsync(source);
                    Require(validation.IsValid, "The EPUB route fixture is invalid.");
                    var originalBytes = await File.ReadAllBytesAsync(source);
                    var durable = new QueueStore(Path.Combine(fixture, "queue.json"), () => []);
                    await durable.SaveAsync(new[] { new MediaTask
                    {
                        InputPath = source, SourceFormatId = "epub", ContentKind = ContentKind.Ebook,
                        TargetFormatId = "epub", State = ConversionState.Waiting
                    } });
                    var services = new MainWindowServices(new SettingsService(Path.Combine(fixture, "settings.json")),
                        durable, new LocalImportStorageAccess(),
                        feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)));
                    var window = new MainWindow(services);
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
                        SetField("_documentTools", new Dictionary<ExternalToolKind, ExternalToolInfo>());
                        await session.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        var task = window.Tasks.Single();
                        Require(task.Model.TargetFormatId == "epub" && task.AvailableFormats.Contains("EPUB"),
                            "Restoring an EPUB silently changed or hid its requested target.");
                        var reminder = LocalizationService.T("ConversionUiRouteNotImplemented", "epub", "epub");
                        Require(!task.IsTargetAvailable && task.EngineRequirement == reminder && task.Message == reminder,
                            "An unsupported EPUB route did not show the neutral localized reminder.");
                        Require(Invoke("EnsureTaskEngineReady", task) is false,
                            "The unsupported EPUB route was admitted for conversion.");

                        // Use the actual queue template and its two-way target binding.
                        var row = (FrameworkElement)((ListView)Field("TaskList")!).ItemTemplate.LoadContent();
                        row.DataContext = task;
                        var start = EpubRouteDescendants(row).OfType<Button>()
                            .Single(button => button.Content is FontIcon { Glyph: "\uE768" });
                        var target = EpubRouteDescendants(row).OfType<ComboBox>()
                            .Single(combo => ReferenceEquals(combo.ItemsSource, task.AvailableFormats));
                        Require(!start.IsEnabled, "The real queue row enabled Start for an unsupported EPUB route.");

                        var calibre = new ExternalToolInfo(ExternalToolKind.Calibre, "Calibre", true,
                            Path.Combine(fixture, "availability-only-ebook-convert.exe"), "fixture", "Available");
                        SetField("_documentTools", new Dictionary<ExternalToolKind, ExternalToolInfo>
                            { [ExternalToolKind.Calibre] = calibre });
                        Invoke("RefreshTaskEngineAvailability");
                        Require(!task.IsTargetAvailable && task.EngineRequirement == reminder && !start.IsEnabled,
                            "Calibre availability incorrectly enabled EPUB to EPUB.");

                        SetField("_documentTools", new Dictionary<ExternalToolKind, ExternalToolInfo>());
                        target.SelectedItem = "MOBI";
                        Require(task.Model.TargetFormatId == "mobi" && !start.IsEnabled &&
                            task.EngineRequirement == LocalizationService.T("RequiresCalibre"),
                            "Choosing MOBI failed to refresh the Calibre requirement through the queue binding.");
                        Require(Invoke("EnsureTaskEngineReady", task) is false,
                            "An EPUB conversion without Calibre was admitted.");
                        SetField("_documentTools", new Dictionary<ExternalToolKind, ExternalToolInfo>
                            { [ExternalToolKind.Calibre] = calibre });
                        Invoke("RefreshTaskEngineAvailability");
                        Require(task.IsTargetAvailable && start.IsEnabled && string.IsNullOrWhiteSpace(task.EngineRequirement),
                            "MOBI stayed unavailable after Calibre became available.");
                        Require(Invoke("EnsureTaskEngineReady", task) is true,
                            "A supported EPUB route incorrectly required FFmpeg.");
                        target.SelectedItem = "AZW3";
                        Require(task.Model.TargetFormatId == "azw3" && task.IsTargetAvailable && start.IsEnabled,
                            "Choosing the other supported EPUB route failed to refresh the row.");
                        target.SelectedItem = "EPUB";
                        Require(!start.IsEnabled && task.EngineRequirement == reminder,
                            "Re-selecting the retained EPUB target bypassed the route guard.");
                        Require(reported.Count == 0, "EPUB route refresh emitted error telemetry.");
                        var currentBytes = await File.ReadAllBytesAsync(source);
                        Require(originalBytes.SequenceEqual(currentBytes), "Route changes altered the source EPUB.");
                        Require((await durable.LoadResultAsync()).Items.Single().TargetFormatId == "epub",
                            "Restore silently rewrote the durable target.");
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

        await CheckAsync("Background EPUB discovery persists a supported route that restores through the real importer", async () =>
        {
            var fixture = Path.Combine(_runRoot, "background-epub-route", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var source = Path.Combine(fixture, "synthetic-book.epub");
            var document = new NormalizedDocument("Background fixture", "en",
                new NormalizedSection[] { new(null, new NormalizedBlock[]
                    { new NormalizedParagraph("Synthetic background EPUB discovery regression.") }) },
                Array.Empty<NormalizedResource>());
            await new Epub3Writer().WriteAsync(new NormalizedDocumentEpubAdapter().CreatePublication(document), source);
            Require((await new EpubStructureValidator().ValidateAsync(source)).IsValid,
                "The background EPUB fixture is invalid.");
            var originalBytes = await File.ReadAllBytesAsync(source);
            var pending = BackgroundTaskWorker.CreatePendingItem(source, fixture, null);
            Require(pending.InputPath == source && pending.SourceRoot == fixture &&
                pending.SourceRootAccessToken is null && pending.ConversionMode == "balanced",
                "Background discovery changed source ownership or the conversion mode.");
            var durable = new QueueStore(Path.Combine(fixture, "queue.json"), () => []);
            await durable.MergePendingAsync(new[] { pending });
            var stored = (await durable.LoadResultAsync()).Items.Single();
            var importer = new MediaImportCoordinator(storageAccess: new LocalImportStorageAccess());
            var imported = await importer.ImportPersistedBatchAsync(string.Empty, new[] { stored });
            Require(imported.Failures.Count == 0 && imported.AddedTasks.Count == 1,
                "The pending background EPUB did not restore through the actual importer.");
            var task = imported.AddedTasks.Single();
            Require(task.SourceFormatId == "epub" && task.ContentKind == ContentKind.Ebook &&
                task.TargetFormatId is "mobi" or "azw3",
                "A new background EPUB restored with the unsupported EPUB to EPUB route.");
            Require(new DocumentConversionRoutePlanner().CanPlan(task.SourceFormatId, task.TargetFormatId),
                "The restored background EPUB target has no document conversion route.");
            var restoredBytes = await File.ReadAllBytesAsync(source);
            Require(task.InputPath == source && task.SourceRoot == fixture &&
                originalBytes.SequenceEqual(restoredBytes),
                "Background discovery or restoration altered the source EPUB.");

            foreach (var (name, expected) in new[]
            {
                ("synthetic.mp4", "MP4"), ("synthetic.wav", "MP3"),
                ("synthetic.png", "PNG"), ("synthetic.docx", "PDF")
            })
            {
                var path = Path.Combine(fixture, name);
                var media = BackgroundTaskWorker.CreatePendingItem(path, fixture, "isolated-access-token");
                Require(media.TargetFormat == expected && media.InputPath == path &&
                    media.SourceRoot == fixture && media.SourceRootAccessToken == "isolated-access-token" &&
                    media.ConversionMode == "balanced",
                    "Background discovery changed an existing media/document default or source grant.");
            }
        });
    }

    private static IEnumerable<DependencyObject> EpubRouteDescendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in EpubRouteDescendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
