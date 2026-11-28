using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Motion;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private sealed record PerformanceMetric(string Name, double P95Milliseconds, long P95PrivateBytes,
        int MaxRealizedContainers, double[] SamplesMilliseconds);

    private async Task RunPerformanceAsync()
    {
        const int sampleCount = 1; // Run.ps1 aggregates nine independent processes.
        var metrics = new List<PerformanceMetric>();
        var construction = new List<double>();
        var firstSettings = new List<double>();
        var constructionMemory = new List<long>();
        var settingsMemory = new List<long>();
        var handlesBeforeWindow = ReadHandleCount();
        var handlesAfterConstruction = 0;
        LocalizationService.SetLanguage("en-US");

        // This measures UI construction and first settings initialization, not production
        // cold startup: engine discovery, tray, activation and real user files stay isolated.
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var timer = Stopwatch.StartNew();
            _window = new MainWindow(new MainWindowServices(new SettingsService()));
            var elapsed = timer.Elapsed.TotalMilliseconds;
            handlesAfterConstruction = ReadHandleCount();
            construction.Add(elapsed);
            constructionMemory.Add(ReadPrivateBytes());
            SetField("_settingsAvailable", true);
            Invoke("ApplyLocalization");
            timer.Restart();
            Invoke("EnsureSettingsViewLoaded");
            firstSettings.Add(timer.Elapsed.TotalMilliseconds);
            settingsMemory.Add(ReadPrivateBytes());
        }
        metrics.Add(Metric("ui_construction", construction, constructionMemory, []));
        metrics.Add(Metric("first_settings_initialization", firstSettings, settingsMemory, []));

        var root = (Grid)Field("RootGrid")!;
        var startup = (RoutedEventHandler)typeof(MainWindow).GetMethod("MainWindow_Loaded", InstanceMembers)!
            .CreateDelegate(typeof(RoutedEventHandler), _window);
        root.Loaded -= startup;
        Invoke("ApplySettingsToControls", true);
        _window!.Activate();
        ResizeForCapture(1280, 900);
        await Task.Delay(200);
        ResizeForCapture(1280, 900);
        root.UpdateLayout();
        await YieldLayoutAsync();
        var documentFixture = await MeasureLargeDocumentAsync(metrics);
        RecordPerformancePhase("document-complete");
        var animationField = typeof(FluentMotion).GetField("_animationsEnabled", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var previousAnimation = animationField.GetValue(null);
        animationField.SetValue(null, false);
        try
        {
            foreach (var (count, mixed) in new[] { (1000, false), (10000, false), (10000, true) })
            {
                var metricPrefix = $"queue_{count}" + (mixed ? "_mixed" : string.Empty);
                RecordPerformancePhase($"{metricPrefix}-clear");
                ClearSampleTasks();
                RecordPerformancePhase($"{metricPrefix}-populate");
                var outputGroupId = Guid.NewGuid();
                for (var index = 0; index < count; index++)
                {
                    var task = CreateSampleTask(index);
                    // One real two-output group must not turn the other 9,998 rows
                    // into individual group headers. Both outputs survive the video filter.
                    if (mixed && index is 0 or 3) task.Model.OutputGroupId = outputGroupId;
                    _window.Tasks.Add(task);
                }
                RecordPerformancePhase($"{metricPrefix}-first-layout");
                Invoke("NavigateWithMotion", "all");
                root.UpdateLayout();
                await YieldLayoutAsync();
                // Warm the filter and virtualization path before its measured roundtrip.
                RecordPerformancePhase($"{metricPrefix}-warm-filter");
                Invoke("NavigateWithMotion", "video");
                root.UpdateLayout();
                await YieldLayoutAsync();
                Invoke("NavigateWithMotion", "all");
                root.UpdateLayout();
                await YieldLayoutAsync();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var samples = new List<double>();
                var privateBytes = new List<long>();
                var realized = new List<int>();
                var original = _window.Tasks.ToDictionary(task => task.Id);
                RecordPerformancePhase($"{metricPrefix}-measure-filter");
                for (var sample = 0; sample < sampleCount; sample++)
                {
                    var timer = Stopwatch.StartNew();
                    Invoke("NavigateWithMotion", "video");
                    root.UpdateLayout();
                    await YieldLayoutAsync();
                    Invoke("NavigateWithMotion", "all");
                    root.UpdateLayout();
                    await YieldLayoutAsync();
                    samples.Add(timer.Elapsed.TotalMilliseconds);
                    privateBytes.Add(ReadPrivateBytes());
                    var list = (ListView)Field("TaskList")!;
                    realized.Add(Enumerable.Range(0, count).Count(index => list.ContainerFromIndex(index) is not null));
                    Require(_window.VisibleTasks.Count == count && _window.VisibleTasks.All(task => ReferenceEquals(original[task.Id], task)),
                        "Performance filtering changed task identity or lost tasks.");
                }
                Require(realized.Max() < 250, $"The {count}-task list lost virtualization ({realized.Max()} realized containers).");
                metrics.Add(Metric($"{metricPrefix}_filter_roundtrip", samples, privateBytes, realized));
                RecordPerformancePhase($"{metricPrefix}-filter-complete");
            }
            // Opt-in diagnostics run after every timed sample, so they cannot change
            // the measured filter path or the release comparison's thresholds.
            if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_UI_SMOKE_VIRTUALIZATION_DIAGNOSTICS") == "1")
                await DiagnoseVirtualizationAsync(root);
            await MeasureQueueAdmissionAsync(metrics);
            var measuredEnvironment = new
            {
                machine = Environment.MachineName,
                os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processors = Environment.ProcessorCount,
                processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                winUiAssembly = typeof(Window).Assembly.GetName().Version?.ToString(),
                width = root.ActualWidth,
                height = root.ActualHeight,
                scale = root.XamlRoot.RasterizationScale,
                language = LocalizationService.CurrentUICulture.Name,
                theme = root.ActualTheme.ToString(),
                highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast,
                animation = false,
                configuration = "Release",
                samples = sampleCount
            };
            _window!.Close();
            _window = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var handlesAfterWindowClose = ReadHandleCount();
            var report = new
            {
                schemaVersion = 2,
                measurement = "Isolated UI construction/settings, loaded queue filtering, real queue admission, cached recovery lookup, and built-in DOCX-to-EPUB conversion; excludes production cold startup.",
                environment = measuredEnvironment,
                handleCounts = new
                {
                    beforeWindow = handlesBeforeWindow,
                    afterConstruction = handlesAfterConstruction,
                    afterWindowClose = handlesAfterWindowClose,
                    deltaAfterClose = handlesAfterWindowClose - handlesBeforeWindow
                },
                documentFixture,
                metrics
            };
            File.WriteAllText(Path.Combine(_runRoot, "performance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            _results.Add(new { name = "Repeatable isolated UI performance samples", passed = true });
        }
        finally { animationField.SetValue(null, previousAnimation); }
    }

    private async Task<object> MeasureLargeDocumentAsync(List<PerformanceMetric> metrics)
    {
        const int paragraphs = 2500;
        const int charactersPerParagraph = 1536;
        var input = Path.Combine(_runRoot, "large-document.docx");
        var outputDirectory = Path.Combine(_runRoot, "large-document-output");
        await Task.Run(() => CreateDocumentFixture(input, paragraphs, charactersPerParagraph));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var request = new ConversionRequest
        {
            Inputs = new[] { new ConversionInput(input, "docx") },
            TargetFormatId = "epub",
            OutputDirectory = outputDirectory
        };
        long peakPrivateBytes = ReadPrivateBytes();
        using var samplingCancellation = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!samplingCancellation.IsCancellationRequested)
            {
                var current = ReadPrivateBytes();
                long previous;
                do
                {
                    previous = Interlocked.Read(ref peakPrivateBytes);
                    if (current <= previous) break;
                } while (Interlocked.CompareExchange(ref peakPrivateBytes, current, previous) != previous);

                try { await Task.Delay(10, samplingCancellation.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        var timer = Stopwatch.StartNew();
        ConversionExecutionResult result;
        try
        {
            result = await Task.Run(() => new DocumentConversionService().ConvertAsync(request, null, CancellationToken.None));
        }
        finally
        {
            timer.Stop();
            samplingCancellation.Cancel();
            await sampler;
            peakPrivateBytes = Math.Max(peakPrivateBytes, ReadPrivateBytes());
        }
        Require(result.Success && !result.Cancelled && result.Artifacts.Count == 1 && File.Exists(result.Artifacts[0].Path),
            $"Large DOCX to EPUB conversion failed: {result.Message}");
        metrics.Add(Metric("large_docx_to_epub", new[] { timer.Elapsed.TotalMilliseconds }, new[] { peakPrivateBytes }, []));
        return new
        {
            paragraphs,
            charactersPerParagraph,
            inputBytes = new FileInfo(input).Length,
            outputBytes = new FileInfo(result.Artifacts[0].Path).Length,
            engine = "built-in-docx-reader-and-epub-writer"
        };
    }

    private async Task DiagnoseVirtualizationAsync(Grid root)
    {
        var list = (ListView)Field("TaskList")!;
        var panel = list.ItemsPanelRoot as ItemsStackPanel
            ?? throw new InvalidOperationException("Queue virtualization panel was not realized.");
        var scroller = FindDescendant<ScrollViewer>(list)
            ?? throw new InvalidOperationException("Queue virtualization scroller was not realized.");
        var snapshots = new List<object>();
        void Capture(string phase)
        {
            var containers = Enumerable.Range(0, list.Items.Count)
                .Select(index => (Index: index, Container: list.ContainerFromIndex(index) as FrameworkElement))
                .Where(item => item.Container is not null)
                .Select(item => new { index = item.Index, height = item.Container!.ActualHeight,
                    top = item.Container.TransformToVisual(list).TransformPoint(new Windows.Foundation.Point()).Y })
                .ToArray();
            snapshots.Add(new { phase, cacheLength = panel.CacheLength,
                firstVisibleIndex = panel.FirstVisibleIndex, lastVisibleIndex = panel.LastVisibleIndex,
                firstCacheIndex = panel.FirstCacheIndex, lastCacheIndex = panel.LastCacheIndex,
                viewportHeight = scroller.ViewportHeight, offset = scroller.VerticalOffset,
                extentHeight = scroller.ExtentHeight, listHeight = list.ActualHeight, containers });
        }
        async Task Settle(string phase)
        {
            Capture(phase + "-immediate");
            for (var turn = 0; turn < 4; turn++) await YieldLayoutAsync();
            Capture(phase + "-four-low-priority-turns");
            await Task.Delay(200);
            root.UpdateLayout();
            Capture(phase + "-200ms");
        }
        await Settle("original-measurement");
        var originalCacheLength = panel.CacheLength;
        try
        {
            foreach (var cache in new[] { originalCacheLength, 0.5, 0.0 })
            {
                panel.CacheLength = cache;
                Invoke("NavigateWithMotion", "video");
                root.UpdateLayout();
                await YieldLayoutAsync();
                Invoke("NavigateWithMotion", "all");
                root.UpdateLayout();
                await YieldLayoutAsync();
                await Settle($"cache-{cache}");
                scroller.ChangeView(null, 1000, null, true);
                root.UpdateLayout();
                await YieldLayoutAsync();
                await Settle($"cache-{cache}-scroll-1000");
                scroller.ChangeView(null, 0, null, true);
                root.UpdateLayout();
                await YieldLayoutAsync();
            }
        }
        finally { panel.CacheLength = originalCacheLength; }
        File.WriteAllText(Path.Combine(_runRoot, "virtualization-diagnostics.json"),
            JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CreateDocumentFixture(string path, int paragraphs, int charactersPerParagraph)
    {
        var random = new Random(137);
        using var package = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        var body = new Body();
        var characters = new char[charactersPerParagraph];
        for (var index = 0; index < paragraphs; index++)
        {
            for (var character = 0; character < characters.Length; character++)
                characters[character] = (char)('a' + random.Next(26));
            body.Append(new Paragraph(new Run(new Text(new string(characters)))));
        }
        main.Document = new Document(body);
        main.Document.Save();
    }

    private Task YieldLayoutAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_window!.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => completion.TrySetResult(true)))
            completion.TrySetException(new InvalidOperationException("The performance window dispatcher is closed."));
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private void RecordPerformancePhase(string phase) => File.AppendAllText(
        Path.Combine(_runRoot, "performance-phases.log"), $"{DateTimeOffset.UtcNow:O} {phase}{Environment.NewLine}");

    private static PerformanceMetric Metric(string name, IReadOnlyList<double> samples, IReadOnlyList<long> memory, IReadOnlyList<int> realized) =>
        new(name, Percentile95(samples), memory.Count == 0 ? 0 : memory.Order().ElementAt((int)Math.Ceiling(memory.Count * .95) - 1),
            realized.Count == 0 ? 0 : realized.Max(), samples.ToArray());

    private static double Percentile95(IReadOnlyList<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);

    private static long ReadPrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static int ReadHandleCount()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.HandleCount;
    }
}
