using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;

// This file is compiled only by Measure-Startup.ps1, together with the production
// App and window. Fixture preparation runs in a different process from measurement.
namespace FormatFactoryWinUI
{
    public partial class App
    {
        private static readonly JsonSerializerOptions MeasurementJson = new() { WriteIndented = true };
        private static string MeasurementRoot => Path.GetFullPath(
            Environment.GetEnvironmentVariable("FORMAT_FACTORY_STARTUP_ROOT")
            ?? throw new InvalidOperationException("Use Measure-Startup.ps1 to launch this test host."));
        private static int FixtureCount => int.Parse(Environment.GetEnvironmentVariable("FORMAT_FACTORY_STARTUP_COUNT")!);
        private const BindingFlags WindowMembers = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        partial void ConfigureStartupMeasurement()
        {
            Directory.CreateDirectory(MeasurementRoot);
            Services.WindowsAppDataStorage.Initialize();
            if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_STARTUP_PREPARE") == "1")
            {
                try
                {
                    Task.Run(PrepareStartupFixturesAsync).GetAwaiter().GetResult();
                    Environment.Exit(0);
                }
                catch (Exception exception) { ExitMeasurementFailure(exception); }
            }
            UnhandledException += (_, args) =>
            {
                args.Handled = true;
                ExitMeasurementFailure(args.Exception);
            };
        }

        partial void ObserveStartupMeasurement(MainWindow window) => _ = MeasureStartupAndExitAsync(window);

        private static async Task PrepareStartupFixturesAsync()
        {
            var count = FixtureCount;
            if (count is not (0 or 100 or 1000)) throw new ArgumentOutOfRangeException(nameof(count));
            var sources = Path.Combine(MeasurementRoot, "sources");
            Directory.CreateDirectory(sources);
            Require(FormatCatalog.FindByExtension(".docx") is { CanImport: true },
                "The startup fixture format must support production file import.");
            var settings = new AppSettings
            {
                PerformanceDefaultsVersion = AppSettings.CurrentPerformanceDefaultsVersion,
                AlwaysMinimizeToTray = false, StartWithWindows = false, StartInTray = false,
                WatchFolderEnabled = false, Notifications = false, NotificationSound = false,
                EnableUsageTelemetry = false, EnableAutomaticErrorReporting = false, EnableAutomaticUpdates = false,
                CustomOutputDirectory = Path.Combine(MeasurementRoot, "output")
            };
            await new SettingsService().SaveAsync(settings);
            var tasks = new List<MediaTask>();
            for (var index = 0; index < count; index++)
            {
                var source = Path.Combine(sources, $"input-{index:D4}.docx");
                // Retain one unavailable source, plus failed/cancelled/paused tasks.
                if (index != count - 1) WriteMinimalDocx(source);
                var task = new MediaTask
                {
                    InputPath = source, ContentKind = ContentKind.Document, SourceFormatId = "docx",
                    TargetFormat = "PDF", TargetFormatId = "pdf", EngineId = "document-pipeline",
                    State = index switch
                    {
                        0 => ConversionState.Failed, 1 => ConversionState.Cancelled,
                        2 => ConversionState.Paused, _ => ConversionState.Waiting
                    },
                    LastError = index == 0 ? "Previous conversion failed" : string.Empty
                };
                task.Inputs.Add(new ConversionInput(source, "docx"));
                tasks.Add(task);
            }
            await new QueueStore().SaveAsync(tasks);
            var archiveTasks = count == 0 ? Array.Empty<ArchiveTaskCheckpoint>() : new[]
            {
                new ArchiveTaskCheckpoint
                {
                    SourcePath = Path.Combine(sources, "unavailable-archive.zip"),
                    OutputDirectory = settings.CustomOutputDirectory, TargetFormat = ArchiveFormat.SevenZip,
                    State = ArchiveTaskState.Running
                }
            };
            await new ArchiveTaskStore().SaveAsync(new ArchiveTaskSnapshot
            {
                OutputDirectory = settings.CustomOutputDirectory, Tasks = archiveTasks,
                DefaultTargetFormat = ArchiveFormat.SevenZip
            });
            WriteMinimalDocx(Path.Combine(sources, "new-import.docx"));
            await File.WriteAllTextAsync(Path.Combine(MeasurementRoot, "fixture.json"), JsonSerializer.Serialize(new
            {
                queueCount = count, archiveCount = archiveTasks.Length,
                description = "Valid minimal DOCX documents; one missing input; retained Failed, Cancelled and Paused states; one archive formerly Running.",
                hardwareDecodeTestCompleted = settings.HardwareDecodeTestCompleted,
                telemetryEnabled = false, automaticUpdatesEnabled = false
            }, MeasurementJson));
        }

        private static void WriteMinimalDocx(string path)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            var parts = new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
                ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
                ["word/document.xml"] = "<?xml version=\"1.0\" encoding=\"utf-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Startup measurement document.</w:t></w:r></w:p><w:sectPr/></w:body></w:document>"
            };
            foreach (var (name, content) in parts)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Fastest).Open());
                writer.Write(content);
            }
        }

        private static async Task MeasureStartupAndExitAsync(MainWindow window)
        {
            try
            {
                await Task.Yield();
                var wait = Stopwatch.StartNew();
                IReadOnlyDictionary<string, double> milestones;
                while (true)
                {
                    milestones = window.StartupMetrics.Snapshot();
                    if (new[] { "window-visible", "add-files-ready", "recovery-complete" }.All(milestones.ContainsKey)) break;
                    if (ReadWindowField(window, "_startupTask") is Task { IsFaulted: true } failed) await failed;
                    if (wait.Elapsed > TimeSpan.FromSeconds(150))
                        throw new TimeoutException("Production startup did not reach all three measured milestones.");
                    await Task.Delay(20);
                }

                // Freeze timing before validating behavior; injected import is outside startup.
                var stages = window.StartupMetrics.StagesSnapshot();
                var stageTimings = window.StartupMetrics.StageTimingsSnapshot();
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                Require(IsWindowVisible(handle), "The measured native window is not visible.");
                Require(ReadWindowField(window, "_initializing") is false, "File import is still gated by initialization.");
                var queueView = (QueueView)ReadWindowField(window, "ConverterView")!;
                Require(queueView.AddFilesButton.IsEnabled, "The Add files button is disabled.");
                Require(window.Tasks.Count == FixtureCount, "Queue restoration changed the retained task count.");
                Require(window.Tasks.All(task => task.State != ConversionState.Running), "Startup automatically ran a conversion.");
                if (FixtureCount > 0)
                {
                    Require(window.Tasks[0].State == ConversionState.Failed, "The failed task was automatically retried.");
                    Require(window.Tasks[1].State == ConversionState.Cancelled, "The cancelled task was automatically retried.");
                    Require(window.Tasks[2].State == ConversionState.Paused, "The paused task was automatically resumed.");
                    Require(window.Tasks.Any(task => task.State == ConversionState.Blocked), "The missing input was lost.");
                }
                var archive = (ArchiveConversionView)ReadWindowField(window, "ArchiveView")!;
                var archiveCount = archive.Tasks.Count;
                Require(archiveCount == (FixtureCount == 0 ? 0 : 1), "Archive restoration was incomplete.");
                Require(archive.Tasks.All(task => task.State != ArchiveTaskState.Running), "Archive conversion automatically restarted.");

                var restoredCount = window.Tasks.Count;
                var blockedCount = window.Tasks.Count(task => task.State == ConversionState.Blocked);
                var newPath = Path.Combine(MeasurementRoot, "sources", "new-import.docx");
                Require(File.Exists(newPath) && FormatCatalog.FindByExtension(newPath) is { CanImport: true },
                    "The readiness validation fixture must exist and support production file import.");
                var import = (Task)typeof(MainWindow).GetMethod("AddIncomingPathsAsync", WindowMembers)!
                    .Invoke(window, new object?[] { new[] { newPath }, null, null })!;
                await import.WaitAsync(TimeSpan.FromSeconds(30));
                Require(window.Tasks.Count == restoredCount + 1 && window.Tasks.Any(task => task.InputPath == newPath),
                    "Production import did not add a file after the readiness milestone.");
                Require(window.Tasks.All(task => task.State != ConversionState.Running), "Import automatically started conversion.");

                // Background decoder checks may outlive all user-facing milestones.
                // Observe them separately and let native child processes finish before
                // exiting, so they cannot contaminate the next independent sample.
                var postReadiness = Stopwatch.StartNew();
                if (ReadWindowField(window, "_startupTask") is Task startup)
                    await startup.WaitAsync(TimeSpan.FromSeconds(120));
                while (ReadWindowField(window, "_hardwareDecodeTestRunning") is true)
                {
                    if (postReadiness.Elapsed > TimeSpan.FromSeconds(120))
                        throw new TimeoutException("Background hardware decoder checks did not finish before sample cleanup.");
                    await Task.Delay(25);
                }

                await File.WriteAllTextAsync(Path.Combine(MeasurementRoot, "startup.json"), JsonSerializer.Serialize(new
                {
                    passed = true, persistedCount = FixtureCount, restoredCount, blockedCount, archiveCount,
                    processId = Environment.ProcessId,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    osVersion = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
                    processorCount = Environment.ProcessorCount,
                    productionApplicationStartup = true,
                    isolationOverrides = new[] { "Private data root with no legacy migration", "Separate single-instance key", "Tray disabled", "Telemetry and automatic updates disabled in fixture settings" },
                    timingOrigin = "Windows process creation time, continued with monotonic Stopwatch timing",
                    windowVisibleDefinition = "Loaded and visible AppWindow at first CompositionTarget.Rendering callback; first-frame proxy, not DWM present telemetry",
                    fixture = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(MeasurementRoot, "fixture.json"))),
                    milestonesMilliseconds = milestones,
                    stagesAtReadinessMilliseconds = stages, stageTimingsAtReadiness = stageTimings,
                    stagesMilliseconds = window.StartupMetrics.StagesSnapshot(),
                    stageTimings = window.StartupMetrics.StageTimingsSnapshot(),
                    postReadinessObservationMilliseconds = postReadiness.Elapsed.TotalMilliseconds,
                    verified = new[] { "Native window visible", "Add files enabled", "All queued and archive tasks restored", "Failed/cancelled/paused retained", "Missing input retained", "Actual new file imported", "No automatic conversions" }
                }, MeasurementJson));

                // All measurements and behavior checks are complete before graceful cleanup.
                typeof(MainWindow).GetProperty("_allowClose", WindowMembers)!.SetValue(window, true);
                window.Close();
                LogService.FlushAll();
                Environment.Exit(0);
            }
            catch (Exception exception) { ExitMeasurementFailure(exception); }
        }

        private static object? ReadWindowField(MainWindow window, string name) =>
            typeof(MainWindow).GetField(name, WindowMembers)?.GetValue(window) ??
            typeof(MainWindow).GetProperty(name, WindowMembers)?.GetValue(window);
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void ExitMeasurementFailure(Exception exception)
        {
            File.WriteAllText(Path.Combine(MeasurementRoot, "failure.txt"), exception.ToString());
            try
            {
                var window = MainWindowInstance;
                var queue = window is null ? null : ReadWindowField(window, "ConverterView") as QueueView;
                var session = window is null ? null : ReadWindowField(window, "_queueSession");
                File.WriteAllText(Path.Combine(MeasurementRoot, "failure.json"), JsonSerializer.Serialize(new
                {
                    passed = false, exception = exception.ToString(),
                    tasksCount = window?.Tasks.Count,
                    taskStates = window?.Tasks.GroupBy(task => task.State).ToDictionary(group => group.Key.ToString(), group => group.Count()),
                    firstTasks = window?.Tasks.Take(5).Select(task => new { task.InputPath, task.State, task.Message }).ToArray(),
                    initializing = window is null ? null : ReadWindowField(window, "_initializing"),
                    windowClosed = window is null ? null : ReadWindowField(window, "_windowClosed"),
                    closeSaveInProgress = window is null ? null : ReadWindowField(window, "_closeSaveInProgress"),
                    settingsResetInProgress = window is null ? null : ReadWindowField(window, "_settingsResetInProgress"),
                    queueSessionIsStopping = session?.GetType().GetProperty("IsStopping")?.GetValue(session),
                    desktopShortcutsAvailable = window is null ? null : typeof(MainWindow).GetProperty("DesktopShortcutsAvailable", WindowMembers)?.GetValue(window),
                    addFilesEnabled = queue?.AddFilesButton.IsEnabled,
                    infoBarMessage = queue?.AppInfoBar.Message, infoBarSeverity = queue?.AppInfoBar.Severity.ToString(),
                    startupTaskStatus = (window is null ? null : ReadWindowField(window, "_startupTask") as Task)?.Status.ToString(),
                    milestonesMilliseconds = window?.StartupMetrics.Snapshot(),
                    stageTimings = window?.StartupMetrics.StageTimingsSnapshot()
                }, MeasurementJson));
                LogService.FlushAll();
            }
            catch (Exception diagnosticError)
            {
                File.AppendAllText(Path.Combine(MeasurementRoot, "failure.txt"), "\nDiagnostic capture failed: " + diagnosticError);
            }
            Environment.Exit(1);
        }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);
    }
}

namespace FormatFactoryWinUI.Services
{
    // The sole replaced production service. It prevents migration/access to real app data.
    internal static class WindowsAppDataStorage
    {
        public static void Initialize() => LogService.ConfigureAppDataRoot(Path.Combine(
            Path.GetFullPath(Environment.GetEnvironmentVariable("FORMAT_FACTORY_STARTUP_ROOT")!), "data"));
    }
}
