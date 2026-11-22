using System.Reflection;
using System.Text.Json;
using System.Collections.Concurrent;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckAutomationUpgradeAsync(string fixturePng)
    {
        await CheckNativeAsync("Automation preview keeps unsaved recovery choices and applying presets retains folder ownership", async () =>
        {
            var root = Path.Combine(_runRoot, "automation-upgrade", Guid.NewGuid().ToString("N"));
            var input = Path.Combine(root, "input");
            Directory.CreateDirectory(input);
            File.Copy(fixturePng, Path.Combine(input, "preview.png"));
            var rule = new AutomationRule
            {
                Name = "Image rule", FolderPath = input, InputKind = ContentKind.Image,
                TargetFormatId = "png", OutputDirectory = Path.Combine(root, "output"),
                ArchiveDirectory = Path.Combine(root, "archive"), WatchEnabled = false
            };
            rule.Options["futureOption"] = "retained";
            var view = new AutomationView();
            view.Initialize((AutomationRule[])[rule], AutomationNoFolder);
            AutomationRule? captured = null;
            view.PreviewRequested += snapshot => captured = snapshot;
            AutomationControl<CheckBox>(view, "CatchUpBox").IsChecked = true;
            AutomationControl<NumberBox>(view, "RetryCountBox").Value = 2;
            AutomationControl<NumberBox>(view, "RetryDelayBox").Value = 15;
            AutomationControl<TextBox>(view, "OutputTemplateBox").Text = "result-{name}";
            typeof(AutomationView).GetMethod("PreviewButton_Click", InstanceMembers)!
                .Invoke(view, [AutomationControl<Button>(view, "PreviewButton"), new RoutedEventArgs()]);
            Require(captured is { CatchUpOnStartup: true, RetryCount: 2, RetryDelaySeconds: 15 },
                "Preview lost unsaved recovery controls.");
            Require(view.Rules.Single() is { CatchUpOnStartup: false, RetryCount: 0 } && !Directory.Exists(rule.OutputDirectory),
                "Preview saved the draft or created an output directory.");
            var preview = await AutomationPreviewService.PreviewAsync(captured!, view.Rules, new(), new());
            Require(preview.Ready == 1 && preview.Items[0].OutputPath == Path.Combine(rule.OutputDirectory, "result-preview.png") &&
                !Directory.Exists(rule.OutputDirectory), "Preview did not use the draft naming policy without side effects.");

            var preset = new AutomationConversionPreset
            {
                Name = "Preserved images", TargetFormatId = "png", ConversionMode = "balanced",
                Options = new() { ["imageColorPolicy"] = "preserve", ["outputFileNameTemplate"] = "preset-{name}" }
            };
            view.SetPresets((AutomationConversionPreset[])[preset]);
            var selector = AutomationControl<ComboBox>(view, "PresetBox");
            selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, preset.Id));
            typeof(AutomationView).GetMethod("PreviewButton_Click", InstanceMembers)!
                .Invoke(view, [AutomationControl<Button>(view, "PreviewButton"), new RoutedEventArgs()]);
            Require(captured is not null && captured.Id == rule.Id && captured.FolderPath == input &&
                captured.OutputDirectory == rule.OutputDirectory && captured.CatchUpOnStartup &&
                captured.Options.GetValueOrDefault("futureOption") == "retained" &&
                captured.Options.GetValueOrDefault("imageColorPolicy") == "preserve" &&
                captured.Options.GetValueOrDefault("outputFileNameTemplate") == "preset-{name}",
                "Applying a conversion preset changed rule ownership or discarded preserved options.");
            captured!.Options["imageColorPolicy"] = "srgb";
            Require(preset.Options["imageColorPolicy"] == "preserve", "Editing the draft changed the stored preset.");
        });

        await CheckNativeAsync("Automation presets persist and the save notice follows live language changes", async () =>
        {
            var previousStore = Field("_automationPresetStore");
            var previousPresets = (IReadOnlyList<AutomationConversionPreset>)Field("_automationPresets")!;
            var previousLanguage = LocalizationService.CurrentLanguageCode;
            var view = (AutomationView)Field("AutomationView")!;
            var path = Path.Combine(_runRoot, "automation-upgrade-presets", Guid.NewGuid().ToString("N"), "automation-presets.json");
            var store = new AutomationPresetStore(path);
            SetField("_automationPresetStore", store);
            try
            {
                var preset = new AutomationConversionPreset
                {
                    Name = "Reusable image conversion", TargetFormatId = "png", ConversionMode = "balanced",
                    Options = new() { ["imageColorPolicy"] = "srgb", ["preserveFolderStructure"] = "false" }
                };
                await (Task)Invoke("SaveAutomationPresetAsync", preset)!;
                var loaded = await new AutomationPresetStore(path).LoadAsync();
                Require(loaded.Count == 1 && loaded[0].Id == preset.Id && loaded[0].Options["imageColorPolicy"] == "srgb",
                    "The host did not save a reusable conversion preset.");
                Require(AutomationControl<ComboBox>(view, "PresetBox").Items.OfType<ComboBoxItem>()
                    .Any(item => Equals(item.Tag, preset.Id)), "The host did not update the automation preset picker.");
                var json = await File.ReadAllTextAsync(path);
                Require(!json.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                    !json.Contains("FolderPath", StringComparison.OrdinalIgnoreCase), "The preset library stored input ownership data.");
                var notice = AutomationControl<InfoBar>(view, "StatusBar");
                foreach (var language in new[] { "ur-PK", "my-MM" })
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    Require(notice.IsOpen && notice.Message == LocalizationService.T("AutomationPresetSaved", preset.Name),
                        $"The open preset notice did not update to {language} with the original preset name.");
                }
            }
            finally
            {
                LocalizationService.SetLanguage(previousLanguage);
                Invoke("ApplyLocalization");
                SetField("_automationPresetStore", previousStore);
                SetField("_automationPresets", previousPresets);
                view.SetPresets(previousPresets);
            }
        });

        await CheckNativeAsync("A repeated automation attempt reuses the retained failed queue row", async () =>
        {
            var root = Path.Combine(_runRoot, "automation-reuse", Guid.NewGuid().ToString("N"));
            var input = Path.Combine(root, "input");
            Directory.CreateDirectory(input);
            var source = Path.Combine(input, "retained.png");
            File.Copy(fixturePng, source);
            var rule = new AutomationRule
            {
                Name = "Retained conversion", FolderPath = input, InputKind = ContentKind.Image,
                TargetFormatId = "png", OutputDirectory = Path.Combine(root, "output"), WatchEnabled = false
            };
            var services = (Composition.MainWindowServices)Field("_services")!;
            var priorRuntime = Field("_automationService");
            var priorReady = Field("_automationReady");
            var priorRules = Field("_automationRules");
            var priorStopping = Field("_automationStopping");
            var storePath = Path.Combine(root, "automation.json");
            var runtime = new AutomationService(new AutomationStore(storePath),
                (_, _, _) => throw new InvalidOperationException("This test calls the retained host directly."));
            await runtime.ConfigureAsync((AutomationRule[])[rule]);
            SetField("_automationService", runtime);
            SetField("_automationReady", true);
            SetField("_automationRules", (IReadOnlyList<AutomationRule>)new[] { rule });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var first = await (Task<AutomationExecutionResult>)Invoke("QueueAutomationFileAsync", rule, source, timeout.Token)!;
                Require(first.Success, "The first retained-row fixture failed: " + first.Error);
                var row = _window!.Tasks.Single(task => task.InputPath == source);
                // Persisted source metadata is not an edit to conversion settings.
                row.Model.Options["sourceIdentity.size.0"] = new FileInfo(source).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                row.State = ConversionState.Failed;
                row.Model.LastError = "Simulated retryable failure after the first fixture conversion";
                row.OutputPath = string.Empty;
                var count = _window.Tasks.Count;
                var second = await (Task<AutomationExecutionResult>)Invoke("QueueAutomationFileAsync", rule, source, timeout.Token)!;
                Require(second.Success && _window.Tasks.Count == count &&
                    ReferenceEquals(row, _window.Tasks.Single(task => task.InputPath == source)),
                    "A retry added a duplicate queue row or failed to resume the retained task: " + second.Error);
                Require(File.Exists(first.OutputPath) && File.Exists(second.OutputPath) && first.OutputPath != second.OutputPath,
                    "Retried output replaced an existing result.");

                row.State = ConversionState.Failed;
                row.Model.Options["imageColorPolicy"] = "preserve";
                var edited = await (Task<AutomationExecutionResult>)Invoke("QueueAutomationFileAsync", rule, source, timeout.Token)!;
                Require(!edited.Success && !edited.Retryable && edited.Error == LocalizationService.T("AutomationTaskEdited") &&
                    _window.Tasks.Count == count && row.Model.Options["imageColorPolicy"] == "preserve",
                    "Automatic retry discarded a user's parameter edit or created a new row.");

                // Cancellation must describe a new, unfinished source version: successful
                // versions deliberately keep their success checkpoint rather than a suppression.
                var successfulFingerprint = FileFingerprintService.Read(source)!;
                File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(1));
                var cancelledFingerprint = FileFingerprintService.Read(source)!;
                Require(cancelledFingerprint != successfulFingerprint, "The unfinished source fixture reused a successful version.");
                row.Model.Options["automation.fingerprint"] = JsonSerializer.Serialize(cancelledFingerprint);

                // Hold persistence to reproduce deleting a row immediately before exit.
                var gates = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(AutomationStore)
                    .GetField("Gates", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var gate = gates[Path.GetFullPath(storePath)];
                await gate.WaitAsync(timeout.Token);
                Task stopping;
                try
                {
                    Invoke("RemoveTask", row);
                    stopping = (Task)Invoke("StopAutomationAsync")!;
                    Require(!stopping.IsCompleted, "Exit did not wait for the pending cancellation record.");
                }
                finally { gate.Release(); }
                await stopping.WaitAsync(timeout.Token);
                var saved = await new AutomationStore(storePath).LoadAsync(timeout.Token);
                Require(saved.Suppressions.Any(item => item.InputPath == source && item.Cancelled && item.Fingerprint == cancelledFingerprint) &&
                    saved.Records.Any(item => item.InputPath == source && item.Fingerprint == successfulFingerprint) &&
                    !_window.Tasks.Any(task => task.InputPath == source),
                    "Deleting a row immediately before exit lost its durable cancellation record.");
            }
            finally
            {
                SetField("_automationService", priorRuntime);
                SetField("_automationReady", priorReady);
                SetField("_automationRules", priorRules);
                SetField("_automationStopping", priorStopping);
                await runtime.DisposeAsync();
            }
        });
    }
}
