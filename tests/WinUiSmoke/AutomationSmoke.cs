using System.Reflection;
using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckAutomationQueueWorkflowAsync(string fixturePng)
    {
        await CheckNativeAsync("Custom automation converts through the real queue, preserves collisions, archives and skips successful versions", async () =>
        {
            var root = Path.Combine(_runRoot, "automation-e2e", Guid.NewGuid().ToString("N"));
            var input = Path.Combine(root, "input");
            var output = Path.Combine(root, "output");
            var archive = Path.Combine(root, "archive");
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(output);
            var source = Path.Combine(input, "transparent.png");
            var existingOutput = Path.Combine(output, "transparent.png");
            File.Copy(fixturePng, source, overwrite: true);
            var originalBytes = await File.ReadAllBytesAsync(source);
            var collisionBytes = new byte[] { 19, 37, 73, 109 };
            await File.WriteAllBytesAsync(existingOutput, collisionBytes);
            var fingerprint = FileFingerprintService.Read(source)!;
            var rule = new AutomationRule
            {
                Name = "Real queue image conversion", FolderPath = input,
                WatchEnabled = false, ScheduleEnabled = false,
                InputKind = ContentKind.Image, Extensions = "png", TargetFormatId = "png",
                ConversionMode = "balanced", OutputDirectory = output,
                ArchiveSource = true, ArchiveDirectory = archive
            };
            var services = (MainWindowServices)Field("_services")!;
            var settings = (AppSettings)Field("_settings")!;
            var notifications = settings.Notifications;
            var originalRules = (IReadOnlyList<AutomationRule>)Field("_automationRules")!;
            var ready = (bool)Field("_automationReady")!;
            var stopping = (bool)Field("_automationStopping")!;
            var view = (AutomationView)Field("AutomationView")!;
            var status = AutomationControl<InfoBar>(view, "StatusBar");
            var statusMessage = status.Message;
            var statusOpen = status.IsOpen;
            var statusSeverity = status.Severity;
            AutomationService? runtime = null;
            try
            {
                Invoke("CancelHistoryIndexing");
                settings.Notifications = false;
                await services.AutomationStore.SaveRulesAsync((AutomationRule[])[rule]);
                await (Task)Invoke("InitializeAutomationAsync")!;
                runtime = (AutomationService?)Field("_automationService");
                Require(runtime is not null && Field("_automationReady") is true,
                    "The real window could not initialize the stored automation rule: " + status.Message);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var summary = await runtime!.RunNowAsync(rule.Id, timeout.Token);
                Require(summary is { Scanned: 1, Succeeded: 1, Failed: 0 },
                    "The automation scan did not complete its real conversion: " + summary + "; " + status.Message);
                var task = _window!.Tasks.Single(item => item.InputPath == source);
                Require(task.State == ConversionState.Completed && task.Model.TargetFormatId == "png" &&
                    task.Model.ConversionMode == "balanced" && File.Exists(task.OutputPath),
                    "The conversion did not complete through the retained queue row: " + task.Model.LastError);
                var retainedOutputBytes = await File.ReadAllBytesAsync(existingOutput);
                Require(Path.GetDirectoryName(task.OutputPath) == output && task.OutputPath != existingOutput &&
                    collisionBytes.SequenceEqual(retainedOutputBytes),
                    "Automation overwrote the existing output or ignored the rule output folder.");
                Require(await new WindowsImageCodecService().ProbeAsync(task.OutputPath) is { Width: 2, Height: 2 },
                    "The real automation output could not be decoded.");

                // Completed rows are intentionally absent from pending-queue.json. Exercise
                // its production mapping directly to prove retries retain the rule snapshot.
                var persisted = (PersistedQueueItem)typeof(QueueStore)
                    .GetMethod("ToPersistedItem", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [task.Model])!;
                var snapshotPath = Path.Combine(root, "task-snapshot.json");
                await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(persisted,
                    new JsonSerializerOptions { WriteIndented = true }));
                var restored = JsonSerializer.Deserialize<PersistedQueueItem>(await File.ReadAllTextAsync(snapshotPath))!;
                var capturedRule = JsonSerializer.Deserialize<AutomationRule>(restored.Options["automation.ruleSnapshot"]);
                var capturedFingerprint = JsonSerializer.Deserialize<AutomationFileFingerprint>(restored.Options["automation.fingerprint"]);
                Require(capturedRule is not null && capturedRule.Id == rule.Id && capturedRule.ArchiveSource &&
                    capturedRule.ArchiveDirectory == archive && capturedRule.OutputDirectory == output &&
                    capturedFingerprint == fingerprint && restored.Options["automation.outputDirectory"] == output,
                    "The persisted queue mapping lost the rule or source version needed for retry.");

                var stored = await services.AutomationStore.LoadAsync();
                var record = stored.Records.Single(item => item.RuleId == rule.Id && item.InputPath == source);
                var archivedBytes = record.ArchivedPath is not null && File.Exists(record.ArchivedPath)
                    ? await File.ReadAllBytesAsync(record.ArchivedPath) : [];
                Require(!File.Exists(source) && record.ArchivedPath is not null && File.Exists(record.ArchivedPath) &&
                    originalBytes.SequenceEqual(archivedBytes) &&
                    record.Fingerprint == fingerprint && record.OutputPath == task.OutputPath &&
                    record.OutputFingerprint == FileFingerprintService.Read(task.OutputPath),
                    "Verified conversion did not retain a durable success record and intact archived source.");

                // Put the same unchanged source version back into the watched location,
                // retaining the archive evidence, so the second scan must really skip it.
                File.Copy(record.ArchivedPath!, source);
                File.SetCreationTimeUtc(source, new DateTime(fingerprint.CreationTimeUtcTicks, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(source, new DateTime(fingerprint.LastWriteTimeUtcTicks, DateTimeKind.Utc));
                Require(FileFingerprintService.Read(source) == fingerprint, "The repeat fixture changed its source version.");
                var count = _window.Tasks.Count;
                var repeated = await runtime.RunNowAsync(rule.Id, timeout.Token);
                Require(repeated is { Scanned: 1, Succeeded: 0, Failed: 0, Skipped: 1 } &&
                    _window.Tasks.Count == count && Directory.GetFiles(output).Length == 2 && File.Exists(source),
                    "A successful unchanged version created another queue operation or output: " + repeated);
                var skippedRecord = (await services.AutomationStore.LoadAsync()).Records
                    .Single(item => item.RuleId == rule.Id && item.InputPath == source);
                Require(JsonSerializer.Serialize(skippedRecord) == JsonSerializer.Serialize(record),
                    "The skipped source changed its completed automation record.");
                File.Delete(source);
            }
            finally
            {
                if (runtime is not null)
                {
                    await runtime.StopAsync();
                    await runtime.DisposeAsync();
                    Invoke("DisposeAutomation");
                }
                SetField("_automationService", null);
                SetField("_automationReady", ready);
                SetField("_automationStopping", stopping);
                SetField("_automationRules", originalRules);
                await services.AutomationStore.SaveRulesAsync(originalRules);
                foreach (var task in _window!.Tasks.Where(item => item.InputPath == source).ToArray())
                    Invoke("RemoveTask", task);
                settings.Notifications = notifications;
                status.Message = statusMessage;
                status.Severity = statusSeverity;
                status.IsOpen = statusOpen;
            }
        });
    }

    private void RunAutomationChecks()
    {
        Check("Automation form saves, toggles, sorts, runs and deletes only complete rules", () =>
        {
            var first = AutomationFixture("Downloaded videos", "downloads");
            var second = AutomationFixture("Audio archive", "audio") with { InputKind = ContentKind.Audio, TargetFormatId = "mp3" };
            var view = AutomationTestView((AutomationRule[])[first, second]);
            IReadOnlyList<AutomationRule>? emitted = null;
            Guid? run = null;
            view.RulesChanged += rules => emitted = rules;
            view.RunRequested += id => run = id;
            AutomationControl<TextBox>(view, "NameBox").Text = "Renamed video rule";
            AutomationClick(view, "SaveButton_Click", "SaveButton");
            Require(view.Rules[0].Name == "Renamed video rule" && emitted?.Count == 2, "The edited rule was not saved.");
            AutomationClick(view, "NewButton_Click", "NewButton");
            AutomationControl<TextBox>(view, "NameBox").Text = "Unfinished draft";
            var list = AutomationControl<ListView>(view, "RulesList");
            var firstToggle = ((StackPanel)((ListViewItem)list.Items[0]).Content).Children.OfType<ToggleSwitch>().Single();
            firstToggle.IsOn = false;
            Require(emitted is { Count: 2 } && !emitted[0].Enabled && emitted.All(rule => rule.Name != "Unfinished draft"),
                "A toggle saved an unfinished draft or did not disable the rule.");
            list.SelectedIndex = 0;
            AutomationClick(view, "DownButton_Click", "DownButton");
            Require(view.Rules[0].Id == second.Id && view.Rules[1].Id == first.Id, "Priority was not changed.");
            AutomationClick(view, "NewButton_Click", "NewButton");
            Require(AutomationControl<TextBox>(view, "NameBox").Text == "Unfinished draft", "The unfinished new rule could not be resumed.");
            AutomationControl<TextBox>(view, "FolderBox").Text = Path.Combine(_runRoot, "automation-fixtures", "new-input");
            AutomationControl<TextBox>(view, "OutputBox").Text = Path.Combine(_runRoot, "automation-fixtures", "new-input", "Converted");
            AutomationClick(view, "SaveButton_Click", "SaveButton");
            Require(view.Rules.Count == 3 && emitted?.Count == 3, "The completed new rule was not added.");
            var newId = view.Rules[2].Id;
            AutomationClick(view, "RunButton_Click", "RunButton");
            Require(run == newId, "Manual run did not identify the saved rule.");
            AutomationClick(view, "DeleteButton_Click", "DeleteButton");
            Require(view.Rules.Count == 2 && view.Rules.All(rule => rule.Id != newId), "Delete did not remove the selected rule.");
        });

        Check("Automation save rollback restores committed rules while preserving the retry draft", () =>
        {
            var saved = AutomationFixture("Saved name", "rollback");
            var view = AutomationTestView((AutomationRule[])[saved]);
            AutomationControl<TextBox>(view, "NameBox").Text = "Pending retry";
            AutomationClick(view, "SaveButton_Click", "SaveButton");
            view.RestoreRules((AutomationRule[])[saved]);
            Require(view.Rules.Single().Name == "Saved name", "Rollback retained optimistic committed data.");
            Require(AutomationControl<TextBox>(view, "NameBox").Text == "Pending retry", "Rollback discarded the retry draft.");
            AutomationClick(view, "SaveButton_Click", "SaveButton");
            Require(view.Rules.Single().Name == "Pending retry", "The retained draft could not be saved again.");
        });

        Check("Automation output choices use the shared format catalog for every file type", () =>
        {
            var view = AutomationTestView((AutomationRule[])[AutomationFixture("Formats", "formats")]);
            var kinds = AutomationControl<ComboBox>(view, "KindBox");
            var formats = AutomationControl<ComboBox>(view, "TargetBox");
            foreach (var kind in Enum.GetValues<ContentKind>())
            {
                kinds.SelectedItem = kinds.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, kind));
                var expected = FormatCatalog.TargetsFor(kind).Select(FormatCatalog.FindById).OfType<FormatDescriptor>()
                    .Select(format => format.Id).Distinct().ToArray();
                var actual = formats.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string).ToArray();
                Require(expected.SequenceEqual(actual), $"{kind} automation formats differ from FormatCatalog.");
            }
            kinds.SelectedIndex = 0;
            Require(formats.Items.Count == Enum.GetValues<ContentKind>().SelectMany(kind => FormatCatalog.TargetsFor(kind))
                .Select(FormatCatalog.FindById).OfType<FormatDescriptor>().Select(format => format.Id).Distinct().Count(),
                "All-types selection dropped or duplicated catalog formats.");
            Require(AutomationControl<ComboBox>(view, "ModeBox").Items.OfType<ComboBoxItem>()
                .Select(item => item.Tag as string).SequenceEqual(FormatCatalog.ConversionModes), "Conversion modes differ from the shared catalog.");
        });

        Check("All automation languages and repeat visits preserve the unsaved form", () =>
        {
            var originalLanguage = LocalizationService.CurrentLanguageCode;
            var saved = AutomationFixture("Original", "languages");
            var view = AutomationTestView((AutomationRule[])[saved]);
            try
            {
                AutomationControl<TextBox>(view, "NameBox").Text = "Unsaved language draft";
                AutomationControl<TextBox>(view, "ExtensionsBox").Text = "mp4,mkv";
                AutomationControl<TextBox>(view, "PatternBox").Text = "recording-*.mp4";
                AutomationControl<CheckBox>(view, "ScheduleBox").IsChecked = true;
                AutomationControl<TimePicker>(view, "SchedulePicker").Time = new TimeSpan(21, 37, 0);
                foreach (var language in LocalizationService.LanguageOptions.Where(option => option.Key != "auto"))
                {
                    LocalizationService.SetLanguage(language.Key);
                    view.ApplyLocalization();
                    view.Initialize((AutomationRule[])[saved], AutomationNoFolder);
                    Require(AutomationControl<TextBlock>(view, "TitleText").Text == LocalizationService.T("AutomationTitle"),
                        $"Automation title was not localized in {language.Key}.");
                    Require(AutomationControl<TextBox>(view, "NameBox").Text == "Unsaved language draft" &&
                        AutomationControl<TextBox>(view, "ExtensionsBox").Text == "mp4,mkv" &&
                        AutomationControl<TextBox>(view, "PatternBox").Text == "recording-*.mp4" &&
                        AutomationControl<TimePicker>(view, "SchedulePicker").Time == new TimeSpan(21, 37, 0),
                        $"Automation draft changed in {language.Key}.");
                    Require(view.Rules.Single().Name == "Original", "Localization committed an unsaved change.");
                    foreach (var key in new[] { "AutomationDescription", "AutomationWatch", "AutomationManualHint", "AutomationArchiveHint", "AutomationValidationOverlap" })
                        Require(LocalizationService.T(key) != key, $"Automation resource {key} was missing in {language.Key}.");
                }
            }
            finally { LocalizationService.SetLanguage(originalLanguage); }
        });

        Check("Automation validation keeps incomplete paths and invalid destinations out of saved rules", () =>
        {
            var saved = AutomationFixture("Valid", "validation");
            var view = AutomationTestView((AutomationRule[])[saved]);
            var count = 0;
            view.RulesChanged += _ => count++;
            AutomationControl<TextBox>(view, "OutputBox").Text = saved.FolderPath;
            AutomationClick(view, "SaveButton_Click", "SaveButton");
            Require(count == 0 && AutomationControl<InfoBar>(view, "StatusBar").IsOpen, "Overlapping output was accepted.");
            AutomationControl<TextBox>(view, "OutputBox").Text = saved.OutputDirectory;
            AutomationControl<TextBox>(view, "NameBox").Text = " ";
            AutomationClick(view, "SaveButton_Click", "SaveButton");
            Require(count == 0 && view.Rules.Single().Name == "Valid", "An empty rule name was accepted.");
        });

        Check("Main window automation navigation uses the real retained page", () =>
        {
            var view = (AutomationView)Field("AutomationView")!;
            view.Initialize(AutomationCaptureRules(), AutomationNoFolder);
            view.IsEnabled = true;
            Invoke("NavigateWithMotion", "automation");
            RequireNavigation("automation");
            AutomationControl<TextBox>(view, "NameBox").Text = "Capture draft: downloaded videos";
            Invoke("NavigateWithMotion", "settings");
            Invoke("NavigateWithMotion", "automation");
            Require(ReferenceEquals(view, Field("AutomationView")) &&
                AutomationControl<TextBox>(view, "NameBox").Text == "Capture draft: downloaded videos", "Navigation recreated the automation draft.");
        });
    }

    private AutomationRule AutomationFixture(string name, string folder) => new()
    {
        Name = name, FolderPath = Path.Combine(_runRoot, "automation-fixtures", folder),
        OutputDirectory = Path.Combine(_runRoot, "automation-fixtures", folder, "Converted"),
        InputKind = ContentKind.Video, TargetFormatId = "mp4", ConversionMode = "balanced"
    };

    private IReadOnlyList<AutomationRule> AutomationCaptureRules() => (AutomationRule[])
    [
        AutomationFixture("Downloaded videos", "downloads") with
        {
            Extensions = "mp4,mkv,mov", FileNamePattern = "*", IncludeSubfolders = true,
            ScheduleEnabled = true, ScheduleTime = "21:30", ArchiveSource = true,
            ArchiveDirectory = Path.Combine(_runRoot, "automation-fixtures", "downloads", "Archived")
        },
        AutomationFixture("Audio archive", "audio") with { InputKind = ContentKind.Audio, TargetFormatId = "mp3", WatchEnabled = false, ScheduleEnabled = true },
        AutomationFixture("Image backups", "images") with { InputKind = ContentKind.Image, TargetFormatId = "png", ConversionMode = "lossless", Enabled = false }
    ];

    private static AutomationView AutomationTestView(IReadOnlyList<AutomationRule> rules)
    {
        var view = new AutomationView();
        view.Initialize(rules, AutomationNoFolder);
        return view;
    }

    private static Task<(string Path, string? AccessToken)?> AutomationNoFolder() => Task.FromResult<(string Path, string? AccessToken)?>(null);

    private async Task ScrollAutomationEditorToFiltersAsync()
    {
        var view = (AutomationView)Field("AutomationView")!;
        var scroller = AutomationControl<ScrollViewer>(view, "EditorPane");
        var fields = AutomationControl<StackPanel>(view, "EditorFields");
        var title = AutomationControl<TextBlock>(view, "FiltersTitle");
        scroller.UpdateLayout();
        var offset = title.TransformToVisual(fields).TransformPoint(new Windows.Foundation.Point(0, 0)).Y - 16;
        scroller.ChangeView(null, Math.Min(scroller.ScrollableHeight, Math.Max(0, offset)), null, true);
        await Task.Delay(180);
        scroller.UpdateLayout();
        foreach (var name in new[] { "KindBox", "ExtensionsBox", "PatternBox" })
            Require(IsVisibleWithin(AutomationControl<FrameworkElement>(view, name), scroller),
                "The narrow automation filter/conversion capture does not show " + name);
    }

    private async Task ScrollAutomationEditorToConversionAsync()
    {
        var view = (AutomationView)Field("AutomationView")!;
        var scroller = AutomationControl<ScrollViewer>(view, "EditorPane");
        var fields = AutomationControl<StackPanel>(view, "EditorFields");
        var title = AutomationControl<TextBlock>(view, "ConversionTitle");
        scroller.UpdateLayout();
        var offset = title.TransformToVisual(fields).TransformPoint(new Windows.Foundation.Point(0, 0)).Y - 16;
        scroller.ChangeView(null, Math.Min(scroller.ScrollableHeight, Math.Max(0, offset)), null, true);
        await Task.Delay(180);
        scroller.UpdateLayout();
        foreach (var name in new[] { "PresetBox", "TargetBox", "ModeBox" })
            Require(IsVisibleWithin(AutomationControl<FrameworkElement>(view, name), scroller),
                "The narrow automation conversion capture does not show " + name);
    }

    private async Task ScrollAutomationEditorToEndAsync()
    {
        var view = (AutomationView)Field("AutomationView")!;
        var scroller = AutomationControl<ScrollViewer>(view, "EditorPane");
        scroller.UpdateLayout();
        scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
        await Task.Delay(180);
        scroller.UpdateLayout();
        foreach (var name in new[] { "OutputBox", "ArchiveBox", "ArchiveFolderBox", "SaveButton", "RunButton", "DeleteButton" })
            Require(IsVisibleWithin(AutomationControl<FrameworkElement>(view, name), scroller),
                "The narrow automation archive/actions capture does not show " + name);
    }

    private static T AutomationControl<T>(AutomationView view, string name) where T : class =>
        typeof(AutomationView).GetField(name, InstanceMembers)?.GetValue(view) as T
        ?? throw new InvalidOperationException("Automation control missing: " + name);

    private static void AutomationClick(AutomationView view, string handler, string button) =>
        typeof(AutomationView).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [AutomationControl<Button>(view, button), new RoutedEventArgs()]);
}
