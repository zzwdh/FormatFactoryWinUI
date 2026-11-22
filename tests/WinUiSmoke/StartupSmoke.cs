using System.Reflection;
using System.Text.Json;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task RunStartupAsync(int count)
    {
        if (count is not (0 or 100 or 1000)) throw new ArgumentOutOfRangeException(nameof(count));
        var settings = new AppSettings { AlwaysMinimizeToTray = false, StartWithWindows = false,
            StartInTray = false, WatchFolderEnabled = false, Notifications = false };
        var settingsStore = new SettingsService();
        await settingsStore.SaveAsync(settings);
        var source = Path.Combine(_runRoot, "sources");
        Directory.CreateDirectory(source);
        var pending = new List<MediaTask>();
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(source, $"input-{index:D4}.docx");
            // The final input deliberately models an unavailable source volume.
            if (index != count - 1) File.WriteAllText(path, "Startup import fixture");
            var task = new MediaTask { InputPath = path, ContentKind = ContentKind.Document,
                SourceFormatId = "docx", TargetFormat = "PDF", TargetFormatId = "pdf",
                State = index switch { 0 => ConversionState.Failed, 1 => ConversionState.Cancelled,
                    2 => ConversionState.Paused, _ => ConversionState.Waiting },
                LastError = index == 0 ? "Previous conversion failed" : string.Empty };
            task.Inputs.Add(new ConversionInput(path, "docx"));
            pending.Add(task);
        }
        await new QueueStore().SaveAsync(pending);
        _window = new MainWindow(new MainWindowServices(settingsStore));
        // Invoke the production startup handler on the UI thread without activating
        // a visible window. Discovery and queue restoration remain the actual code.
        Invoke("MainWindow_Loaded", Field("RootGrid"), null);
        var startup = (Task?)Field("_startupTask") ?? throw new InvalidOperationException("Startup did not begin.");
        await startup.WaitAsync(TimeSpan.FromSeconds(150));
        Require(Field("_initializing") is false, "Production startup remained unavailable.");
        Require(_window.Tasks.Count == count, "Production restoration lost saved tasks.");
        if (count > 0)
        {
            Require(_window.Tasks.Any(task => task.State == ConversionState.Blocked), "Unavailable input was discarded.");
            Require(_window.Tasks[0].State == ConversionState.Failed, "Failed task was automatically retried.");
            Require(_window.Tasks[1].State == ConversionState.Cancelled, "Cancelled task was automatically retried.");
            Require(_window.Tasks[2].State == ConversionState.Paused, "Paused task was automatically resumed.");
        }
        Require(_window.Tasks.All(task => task.State != ConversionState.Running), "Startup automatically started conversion.");
        var metrics = ((StartupReadinessMetrics)Field("_startupMetrics")!).Snapshot();
        foreach (var stage in new[] { "first-interactive", "activation-ready", "first-restored-task", "all-restored" })
            Require(metrics.ContainsKey(stage), $"Production startup omitted {stage} timing.");
        File.WriteAllText(Path.Combine(_runRoot, "startup.json"), JsonSerializer.Serialize(new
        {
            persistedCount = count, restoredCount = _window.Tasks.Count,
            blockedCount = _window.Tasks.Count(task => task.State == ConversionState.Blocked),
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            osVersion = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
            milestonesMilliseconds = metrics
        }, new JsonSerializerOptions { WriteIndented = true }));
        _results.Add(new { name = $"Production startup and retained queue ({count} tasks)", passed = true });
        Finish();
    }
}
