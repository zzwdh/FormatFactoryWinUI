using System.Collections.ObjectModel;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FormatFactoryWinUI;

public partial class App
{
    // Runs inside CaptureAndCheckLoadedWindowAsync's isolated, enabled motion
    // scope. Its model-only fixtures never start conversion or write preferences.
    private async Task CheckQueueDensityAsync()
    {
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        var host = new Grid();
        var shell = new Window { Content = host };
        var queue = new QueueView();
        var rows = new ObservableCollection<MediaTaskViewModel>(Enumerable.Range(0, 3)
            .Select(index => new MediaTaskViewModel(QueueExperienceModel($"density-{index}.mp4"))));
        try
        {
            LocalizationService.SetLanguage("zh-Hans");
            queue.ApplyLocalization(new());
            queue.BindTasks(rows);
            queue.TaskList.Visibility = Visibility.Visible;
            queue.EmptyState.Visibility = Visibility.Collapsed;
            host.Children.Add(queue);
            shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 1000));
            shell.AppWindow.Show(false);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!host.IsLoaded && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
            Require(host.IsLoaded && host.XamlRoot is not null, "The isolated density window did not load.");
            var scale = host.XamlRoot!.RasterizationScale;
            shell.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(1040 * scale), (int)(1000 * scale)));
            host.UpdateLayout();
            await Task.Delay(100);

            await CheckAsync("Queue cards keep details collapsed when batch selection changes", async () =>
            {
                Require(rows.All(row => !row.IsDetailsExpanded), "A new task expanded its conversion details by default.");
                foreach (var row in rows)
                    Require(DensityControl<StackPanel>(queue, row, "TaskDetailsEditor").Visibility == Visibility.Collapsed,
                        "A collapsed task retained its parameter editor on screen.");
                queue.SelectAllEditableTasks();
                host.UpdateLayout();
                await Task.Delay(30);
                Require(queue.SelectedTasks.Length == rows.Count && rows.All(row => !row.IsDetailsExpanded),
                    "Batch selection expanded task settings or lost selected tasks.");
                queue.TaskList.SelectedItems.Clear();
            });

            await CheckAsync("The details toggle opens only its row and keeps the output format directly visible", async () =>
            {
                var row = rows[0];
                var toggle = DensityControl<ToggleButton>(queue, row, "TaskDetailsToggle");
                var provider = (IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle);
                var format = DensityControl<ComboBox>(queue, row, "TaskOutputFormatCombo");
                Require(IsVisibleWithin(format, host) && format.IsEnabled && !row.IsDetailsExpanded,
                    "The output format was hidden or unavailable before opening details.");
                Require(!FindDescendants<TextBlock>(format).Any(label => label.Text == row.ToString()),
                    "The output format selector displayed the task view-model type as its header.");
                format.SelectedItem = row.AvailableFormats.Single(value => value == "MKV");
                Require(row.Model.TargetFormatId == "mkv" && !row.IsDetailsExpanded,
                    "The direct output format selector did not edit the model with details closed.");
                provider.Toggle();
                await Task.Delay(60);
                host.UpdateLayout();
                var editor = DensityControl<StackPanel>(queue, row, "TaskDetailsEditor");
                Require(row.IsDetailsExpanded && IsVisibleWithin(editor, host) && rows.Skip(1).All(item => !item.IsDetailsExpanded),
                    "The toggle did not expose exactly one task's editor.");
                var mode = FindDescendants<ComboBox>(editor).Single(control => ReferenceEquals(control.ItemsSource, row.ModeOptions));
                mode.SelectedValue = "quality";
                await Task.Delay(30);
                Require(row.Model.TargetFormatId == "mkv" && row.Model.ConversionMode == "quality" &&
                    DensityControl<TextBlock>(queue, row, "TaskOutputSummaryText").Text == row.CompactParametersSummary,
                    "Editing the preset did not update the model and compact parameters summary.");
                provider.Toggle();
                host.UpdateLayout();
                Require(!row.IsDetailsExpanded && editor.Visibility == Visibility.Collapsed && IsVisibleWithin(format, host) &&
                    Equals(format.SelectedItem, "MKV"),
                    "Collapsing the card discarded its selected conversion settings.");
            });

            await CheckAsync("Busy cards show read-only details while errors and compatibility warnings remain visible", async () =>
            {
                var row = rows[0];
                row.IsDetailsExpanded = true;
                row.State = ConversionState.Running;
                row.Speed = "2.4x";
                row.Remaining = "00:24";
                row.Message = LocalizationService.T("Converting");
                host.UpdateLayout();
                await Task.Delay(30);
                Require(DensityControl<StackPanel>(queue, row, "TaskDetailsEditor").Visibility == Visibility.Collapsed &&
                    IsVisibleWithin(DensityControl<StackPanel>(queue, row, "TaskReadOnlyDetails"), host),
                    "Running work still exposed editable parameters or lost its read-only media information.");
                Require(!FindDescendants<ComboBox>(DensityControl<StackPanel>(queue, row, "TaskDetailsEditor")).Any(),
                    "A running task retained its parameter selectors.");
                var format = DensityControl<ComboBox>(queue, row, "TaskOutputFormatCombo");
                Require(IsVisibleWithin(format, host) && !format.IsEnabled && Equals(format.SelectedItem, "MKV"),
                    "Running work did not retain its selected format in a visible read-only control.");
                Require(FindDescendants<TextBlock>(DensityRow(queue, row)).Any(text => text.Text == row.StatusMetricsSummary &&
                    IsVisibleWithin(text, host)) && row.StatusMetricsSummary.Contains("2.4x", StringComparison.Ordinal),
                    "The compact running status omitted actual speed and remaining time.");
                Require(DensityControl<TextBlock>(queue, row, "TaskDetailMessageText").Visibility == Visibility.Collapsed,
                    "The running card repeated its generic state message.");
                row.IsDetailsExpanded = false;
                row.State = ConversionState.Failed;
                row.Message = "Synthetic decoder failure retained for the user.";
                host.UpdateLayout();
                await Task.Delay(30);
                var error = DensityControl<TextBlock>(queue, row, "TaskDetailMessageText");
                Require(IsVisibleWithin(error, host) && error.Text == row.Message,
                    "Collapsing details concealed a meaningful conversion error.");
                row.State = ConversionState.Waiting;
                row.Model.TargetFormatId = "mp4";
                row.ConversionMode = "balanced";
                row.Model.Probe = new MediaProbeResult
                {
                    Kind = MediaKind.Video, VideoCodec = "h264", AudioTracks = new AudioTrackInfo[]
                    {
                        new() { Codec = "apple_apac", CodecTag = "apac", Channels = 4, StreamIndex = 1, RelativeIndex = 0 },
                        new() { Codec = "aac", Channels = 2, StreamIndex = 2, RelativeIndex = 1 }
                    }
                };
                row.UseCompatibleAudioTrack = true;
                host.UpdateLayout();
                await Task.Delay(30);
                var warning = DensityControl<TextBlock>(queue, row, "CompatibleAudioTrackWarningText");
                Require(row.UseCompatibleAudioTrack && !row.IsDetailsExpanded && IsVisibleWithin(warning, host) &&
                    warning.Text == row.CompatibleAudioTrackWarning,
                    "Collapsing details concealed the warning for an explicitly selected compatible audio track.");
            });

            await CheckAsync("Virtualized task editors restore per-task presets and accept edits after scrolling", async () =>
            {
                while (rows.Count < 64)
                    rows.Add(new MediaTaskViewModel(QueueExperienceModel($"recycled-{rows.Count}.mp4")));
                var edited = rows[20];
                var other = rows[52];
                var otherMode = other.ConversionMode;
                await ScrollTo(edited);
                edited.IsDetailsExpanded = true;
                await YieldLayoutAsync();
                host.UpdateLayout();
                PresetFor(edited).SelectedValue = "quality";
                await YieldLayoutAsync();
                host.UpdateLayout();
                Require(edited.ConversionMode == "quality", "The realized preset did not update its task.");

                await ScrollTo(other);
                Require(queue.TaskList.ContainerFromItem(edited) is null,
                    "The first editor never left the realized container range.");
                var otherEditor = DensityControl<StackPanel>(queue, other, "TaskDetailsEditor");
                Require(!other.IsDetailsExpanded && !FindDescendants<ComboBox>(otherEditor).Any() && other.ConversionMode == otherMode,
                    "Another task inherited the recycled editor or its preset.");
                await ScrollTo(edited);
                var preset = PresetFor(edited);
                Require(preset.IsEnabled && Equals(preset.SelectedValue, "quality"),
                    "The re-realized editor lost its task's preset.");
                preset.SelectedValue = "balanced";
                await YieldLayoutAsync();
                host.UpdateLayout();
                Require(edited.ConversionMode == "balanced" && other.ConversionMode == otherMode,
                    "An edit after recycling reached the wrong task.");

                ComboBox PresetFor(MediaTaskViewModel task) =>
                    FindDescendants<ComboBox>(DensityRow(queue, task)).Single(combo => ReferenceEquals(combo.ItemsSource, task.ModeOptions));

                async Task ScrollTo(MediaTaskViewModel task)
                {
                    queue.TaskList.ScrollIntoView(task, ScrollIntoViewAlignment.Leading);
                    var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
                    while (queue.TaskList.ContainerFromItem(task) is null && DateTimeOffset.UtcNow < timeout)
                        await Task.Delay(25);
                    await YieldLayoutAsync();
                    host.UpdateLayout();
                }
            });

            await CheckAsync("Compact command flyouts retain working output and queue controls", async () =>
            {
                var modeChanges = 0;
                var folderClicks = 0;
                var clearClicks = 0;
                var stopClicks = 0;
                queue.OutputModeCombo_SelectionChangedRequested += (_, _) => modeChanges++;
                queue.ChooseOutputDirectoryButton_ClickRequested += (_, _) => folderClicks++;
                queue.ClearPendingButton_ClickRequested += (_, _) => clearClicks++;
                queue.CancelAllButton_ClickRequested += (_, _) => stopClicks++;
                var path = Path.Combine(_runRoot, "density-output", "Exports");
                queue.OutputDirectoryText.Text = path;
                queue.OutputLocationButton.Flyout.ShowAt(queue.OutputLocationButton);
                await Task.Delay(100);
                Require(queue.OutputModeCombo.IsLoaded && queue.OutputModeCombo.ActualWidth > 0 &&
                    queue.OutputDirectoryButton.IsLoaded && queue.OutputDirectoryText.Text == path &&
                    queue.OutputFolderNameText.Text.EndsWith("Exports", StringComparison.Ordinal) &&
                    AutomationProperties.GetName(queue.OutputLocationButton).Contains(path, StringComparison.Ordinal),
                    "The output flyout lost the full path, selector, folder action, or short accessible summary.");
                queue.OutputModeCombo.SelectedIndex = 1;
                Require(queue.OutputFolderNameText.Text.Contains(LocalizationService.T("OutputSource"), StringComparison.Ordinal),
                    "Source-folder mode did not refresh the compact output summary.");
                queue.OutputModeCombo.SelectedIndex = 0;
                InvokeDensityButton(queue.OutputDirectoryButton);
                await Task.Delay(30);
                Require(modeChanges == 2 && folderClicks == 1, "The retained output controls no longer raise each original action once.");
                queue.OutputLocationButton.Flyout.Hide();
                await Task.Delay(100);
                queue.QueueOperationsButton.Flyout.ShowAt(queue.QueueOperationsButton);
                await Task.Delay(100);
                Require(queue.ClearPendingButton.IsLoaded && queue.ClearPendingButton.ActualWidth > 0 &&
                    queue.StopAllButton.IsLoaded && queue.StopAllButton.ActualWidth > 0,
                    "The queue operations flyout did not realize its original controls.");
                InvokeDensityButton(queue.ClearPendingButton);
                await Task.Delay(100);
                queue.QueueOperationsButton.Flyout.ShowAt(queue.QueueOperationsButton);
                await Task.Delay(100);
                InvokeDensityButton(queue.StopAllButton);
                await Task.Delay(30);
                Require(clearClicks == 1 && stopClicks == 1, "The retained queue operation buttons did not raise each original action once.");
                queue.QueueOperationsButton.Flyout.Hide();
            });
        }
        finally
        {
            queue.OutputLocationButton.Flyout.Hide();
            queue.QueueOperationsButton.Flyout.Hide();
            host.Children.Clear();
            shell.Close();
            foreach (var row in rows) row.Dispose();
            LocalizationService.SetLanguage(previousLanguage);
            Invoke("ApplyLocalization");
        }

        var root = (Grid)Field("RootGrid")!;
        var production = (QueueView)Field("ConverterView")!;
        foreach (var (eventName, handler) in new[]
        {
            ("OutputModeCombo_SelectionChangedRequested", "OutputModeCombo_SelectionChanged"),
            ("ChooseOutputDirectoryButton_ClickRequested", "ChooseOutputDirectoryButton_Click"),
            ("ClearPendingButton_ClickRequested", "ClearPendingButton_Click"),
            ("CancelAllButton_ClickRequested", "CancelAllButton_Click")
        }) RequireUpgradeEvent(production, eventName, handler);
        await CheckAsync("Short narrow and RTL windows keep the compact queue commands fully reachable", async () =>
        {
            try
            {
                SelectNavigation("all");
                await AwaitNavigationAsync();
                foreach (var language in new[] { "zh-Hans", "ar-SA" })
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    foreach (var width in new[] { 720, 960 })
                    {
                        ResizeForCapture(width, 560);
                        await Task.Delay(300);
                        root.UpdateLayout();
                        foreach (var control in new FrameworkElement[] { production.OutputLocationButton, production.PauseAllButton,
                                     production.QueueOperationsButton, production.StartAllButton })
                            Require(DensityFullyVisible(control, root), $"{language} {width}x560 clipped the {control.Name} command.");
                        Require(production.QueueCommandSurface.ActualHeight < 170,
                            "The compact queue command surface expanded back to a tall permanent settings panel.");
                    }
                }
            }
            finally
            {
                LocalizationService.SetLanguage(previousLanguage);
                Invoke("ApplyLocalization");
                ResizeForCapture(1280, 900);
                await Task.Delay(300);
            }
        });

        await CheckAsync("The queue summary flyout retains real counts and measurements without a second summary row", async () =>
        {
            Invoke("UpdateQueueSummary");
            var active = _window!.Tasks.Count(row => row.State is ConversionState.Probing or ConversionState.Running or ConversionState.Paused);
            var completed = _window.Tasks.Count(row => row.State == ConversionState.Completed);
            var expected = LocalizationService.T("QueueSummaryCounts", _window.Tasks.Count, active, completed);
            var results = ConversionResultSummaryFormatter.FormatBatch(_window.Tasks.Select(row => row.Model));
            Require(production.QueueSummaryText.Text == expected && production.ConversionResultsSummaryText.Visibility == Visibility.Collapsed &&
                production.QueueSummaryDetailsText.Text.Contains(expected, StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(results) || production.QueueSummaryDetailsText.Text.Contains(results, StringComparison.Ordinal)),
                "The header summary lost actual counts or aggregate result measurements.");
            production.QueueSummaryButton.Flyout.ShowAt(production.QueueSummaryButton);
            try
            {
                await Task.Delay(100);
                Require(production.QueueSummaryDetailsText.IsLoaded && production.QueueSummaryDetailsText.ActualWidth > 0 &&
                    AutomationProperties.GetHelpText(production.QueueSummaryButton) == production.QueueSummaryDetailsText.Text,
                    "The summary details were unavailable from the real flyout or its accessible help.");
            }
            finally { production.QueueSummaryButton.Flyout.Hide(); }
        });

        if (_capture)
        {
            var sample = _window!.Tasks.Take(7).ToArray();
            var expanded = sample.Select(row => row.IsDetailsExpanded).ToArray();
            try
            {
                foreach (var row in sample) row.IsDetailsExpanded = false;
                await CaptureAsync("queue-density-light-wide", ElementTheme.Light, 1280, 900, "all");
                await CaptureAsync("queue-density-dark-narrow", ElementTheme.Dark, 720, 900, "all");
                await CaptureAsync("queue-density-short", ElementTheme.Dark, 960, 560, "all");
                var editable = sample.FirstOrDefault(row => row.CanEditConversionOptions);
                if (editable is not null) editable.IsDetailsExpanded = true;
                await CaptureAsync("queue-density-expanded", ElementTheme.Light, 1280, 900, "all", async () =>
                {
                    if (editable is not null) production.TaskList.ScrollIntoView(editable);
                    await Task.Delay(180);
                });
            }
            finally
            {
                for (var index = 0; index < sample.Length; index++) sample[index].IsDetailsExpanded = expanded[index];
                ResizeForCapture(1280, 900);
            }
        }
    }

    private static FrameworkElement DensityRow(QueueView queue, MediaTaskViewModel row) =>
        queue.TaskList.ContainerFromItem(row) as FrameworkElement
        ?? throw new InvalidOperationException("The density fixture's actual queue row was not realized.");

    private static T DensityControl<T>(QueueView queue, MediaTaskViewModel row, string name) where T : FrameworkElement =>
        FindDescendants<T>(DensityRow(queue, row)).Single(control => control.Name == name);

    private static void InvokeDensityButton(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static bool DensityFullyVisible(FrameworkElement control, FrameworkElement viewport)
    {
        if (!IsVisibleWithin(control, viewport)) return false;
        var bounds = control.TransformToVisual(viewport).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }
}
