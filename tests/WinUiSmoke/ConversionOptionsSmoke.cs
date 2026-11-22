using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Controls;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckConversionOptionsAsync()
    {
        Check("Custom parameter and multi-output commands reach each production window handler once", () =>
        {
            var queue = (QueueView)Field("ConverterView")!;
            foreach (var (eventName, handlerName) in new[]
            {
                ("CustomSettingsRequested", "CustomSettings_Click"),
                ("MultipleOutputsRequested", "MultipleOutputs_Click"),
                ("BatchCustomSettingsRequested", "BatchCustomSettings_Click"),
                ("BatchMultipleOutputsRequested", "BatchMultipleOutputs_Click"),
                ("SaveOutputGroupRequested", "SaveOutputGroup_Click")
            })
            {
                RequireUpgradeEvent(queue, eventName, handlerName);
                var subscribers = ((Delegate)typeof(QueueView).GetField(eventName, InstanceMembers)!.GetValue(queue)!).GetInvocationList();
                Require(subscribers.Count(handler => ReferenceEquals(handler.Target, _window) && handler.Method.Name == handlerName) == 1,
                    eventName + " is missing or duplicated on the actual window.");
            }
            var custom = (MenuFlyoutItem)typeof(QueueView).GetField("BatchCustomSettingsMenuItem", InstanceMembers)!.GetValue(queue)!;
            var multiple = (MenuFlyoutItem)typeof(QueueView).GetField("BatchMultipleOutputsMenuItem", InstanceMembers)!.GetValue(queue)!;
            Require(custom.Text == LocalizationService.T("MediaCustomSettings") && multiple.Text == LocalizationService.T("MultiOutputSettings"),
                "Batch conversion choices did not receive their localized labels.");
        });

        var host = new Grid();
        var shell = new Window { Content = host };
        try
        {
            shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 850));
            shell.AppWindow.Show(false);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!host.IsLoaded && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
            Require(host.IsLoaded && host.XamlRoot is not null, "The isolated conversion-options window did not load.");
            var scale = host.XamlRoot!.RasterizationScale;
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling(1040 * scale), (int)Math.Ceiling(1000 * scale)));
            host.RequestedTheme = ElementTheme.Light;
            host.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            await Task.Delay(50);

            await CheckNativeAsync("Cancelling a custom settings dialog retains source parameters and group identity", async () =>
            {
                var source = ConversionOptionsSource();
                var sourceGroup = Guid.NewGuid();
                source.OutputGroupId = sourceGroup;
                source.Options[MediaConversionOptions.MaxWidthKey] = "1920";
                var initial = OutputConfiguration.FromTask(source);
                var result = await OutputConfigurationEditorDialog.EditAsync(host.XamlRoot!, source.AvailableFormats.ToArray(),
                    (OutputConfiguration[])[initial], source, allowMultiple: false, showDialog: async dialog =>
                    {
                        await WithOptionsContentAsync(host, dialog, content =>
                        {
                            Require(OptionsControl<ComboBox>(content, "FormatHeader").SelectedItem as string == source.TargetFormat,
                                "The dialog did not preserve the source's selected output format.");
                            OptionsControl<NumberBox>(content, "MediaMaxWidth").Value = 640;
                            OptionsControl<NumberBox>(content, "MediaMaximumSize").Value = 10;
                            OptionsControl<ComboBox>(content, "FormatHeader").SelectedItem = FormatCatalog.FindById("mp3")!.DisplayName;
                            Require(OptionsControl<NumberBox>(content, "MediaAudioBitrate").Parent is FrameworkElement { Visibility: Visibility.Visible },
                                "Switching to audio did not expose audio parameters.");
                            return Task.CompletedTask;
                        });
                        return ContentDialogResult.None;
                    });
                Require(result is null && source.TargetFormatId == "mp4" && source.OutputGroupId == sourceGroup &&
                    source.Options[MediaConversionOptions.MaxWidthKey] == "1920" && !source.Options.ContainsKey(MediaConversionOptions.TargetSizeKey),
                    "Cancelling the settings dialog mutated the source task.");
                Require(initial.Options[MediaConversionOptions.MaxWidthKey] == "1920", "The dialog mutated its initial output snapshot.");
            });

            await CheckNativeAsync("Every output draft and format-specific parameter section can be selected independently", async () =>
            {
                var outputs = new[]
                {
                    new OutputConfiguration { Label = "Share", TargetFormatId = "mp4", Options = new() { [MediaConversionOptions.MaxWidthKey] = "1280" } },
                    new OutputConfiguration { Label = "Audio", TargetFormatId = "mp3", Options = new() { [MediaConversionOptions.AudioBitrateKey] = "192" } },
                    new OutputConfiguration { Label = "Image", TargetFormatId = "jpg", Options = new() { [MediaConversionOptions.ImageQualityKey] = "85" } }
                };
                var formats = new[] { "mp4", "mp3", "jpg", "pdf", "png" }.Select(id => FormatCatalog.FindById(id)!.DisplayName).ToArray();
                var result = await OutputConfigurationEditorDialog.EditAsync(host.XamlRoot!, formats, outputs, showDialog: async dialog =>
                {
                    await WithOptionsContentAsync(host, dialog, async content =>
                    {
                        var selector = OptionsControl<ComboBox>(content, "MultiOutputSettings");
                        var format = OptionsControl<ComboBox>(content, "FormatHeader");
                        var width = OptionsControl<NumberBox>(content, "MediaMaxWidth");
                        var fps = OptionsControl<NumberBox>(content, "MediaFrameRate");
                        var audio = OptionsControl<NumberBox>(content, "MediaAudioBitrate");
                        var quality = OptionsControl<NumberBox>(content, "MediaImageQuality");
                        Require(selector.Items.Count == 3 && width.Value == 1280,
                            "The multi-output selector lost an output or its retained settings.");
                        width.Value = 640;
                        selector.SelectedIndex = 1;
                        Require(format.SelectedItem as string == formats[1] && audio.Value == 192 &&
                            audio.Parent is FrameworkElement { Visibility: Visibility.Visible } &&
                            fps.Parent is FrameworkElement { Visibility: Visibility.Collapsed } &&
                            width.Parent is FrameworkElement { Visibility: Visibility.Collapsed },
                            "Selecting the audio output mixed in video parameters.");
                        selector.SelectedIndex = 2;
                        Require(format.SelectedItem as string == formats[2] && quality.Value == 85 &&
                            quality.Parent is FrameworkElement { Visibility: Visibility.Visible } &&
                            audio.Parent is FrameworkElement { Visibility: Visibility.Collapsed },
                            "Selecting an image output did not restore its quality controls.");
                        format.SelectedItem = formats[4];
                        Require(!OptionsControl<NumberBox>(content, "MediaMaximumSize").IsEnabled && !quality.IsEnabled &&
                            width.Parent is FrameworkElement { Visibility: Visibility.Visible },
                            "PNG exposed unsupported size/quality controls or lost its available dimension settings.");
                        format.SelectedItem = formats[3];
                        Require(OptionsControl<NumberBox>(content, "MediaMaximumSize").Parent is FrameworkElement { Visibility: Visibility.Collapsed },
                            "A document output still displayed unsupported media parameters.");
                        format.SelectedItem = formats[2];
                        selector.SelectedIndex = 0;
                        Require(width.Value == 640 && format.SelectedItem as string == formats[0],
                            "Returning to the video output discarded its independent unsaved draft.");
                        await Task.Delay(20);
                    });
                    return ContentDialogResult.None;
                });
                Require(result is null && outputs[0].Options[MediaConversionOptions.MaxWidthKey] == "1280" &&
                    outputs[1].Options[MediaConversionOptions.AudioBitrateKey] == "192" && outputs[2].TargetFormatId == "jpg",
                    "Editing or cancelling multiple drafts changed the initial output configurations.");
            });

            await CheckNativeAsync("Applying a combination preset edits copies while leaving the saved preset intact", async () =>
            {
                var preset = AutomationConversionPreset.FromOutputs((OutputConfiguration[])[
                    new() { Label = "Share", TargetFormatId = "mp4", Options = new() { [MediaConversionOptions.MaxWidthKey] = "1280" } },
                    new() { Label = "Audio", TargetFormatId = "mp3", Options = new() { [MediaConversionOptions.AudioBitrateKey] = "192" } }
                ], "Video plus audio");
                var source = ConversionOptionsSource();
                var result = await OutputConfigurationEditorDialog.EditAsync(host.XamlRoot!, source.AvailableFormats.ToArray(),
                    (OutputConfiguration[])[OutputConfiguration.FromTask(source)], source,
                    (AutomationConversionPreset[])[preset], showDialog: async dialog =>
                    {
                        await WithOptionsContentAsync(host, dialog, content =>
                        {
                            OptionsControl<ComboBox>(content, "AutomationConversionPreset").SelectedItem = preset;
                            var selector = OptionsControl<ComboBox>(content, "MultiOutputSettings");
                            Require(selector.Items.Count == 2, "Applying a combination preset copied only its first output.");
                            var width = OptionsControl<NumberBox>(content, "MediaMaxWidth");
                            width.Value = 640;
                            selector.SelectedIndex = 1;
                            Require(OptionsControl<NumberBox>(content, "MediaAudioBitrate").Value == 192,
                                "The copied audio preset lost its independent bitrate.");
                            selector.SelectedIndex = 0;
                            Require(width.Value == 640, "Editing a copied preset did not persist within its dialog draft.");
                            return Task.CompletedTask;
                        });
                        return ContentDialogResult.None;
                    });
                Require(result is null && preset.Outputs.Count == 2 &&
                    preset.Outputs[0].Options[MediaConversionOptions.MaxWidthKey] == "1280" &&
                    preset.Outputs[1].Options[MediaConversionOptions.AudioBitrateKey] == "192" && source.OutputGroupId is null,
                    "Editing or cancelling the combination preset changed the library or source task.");
            });

            await CheckNativeAsync("The loaded queue flattens source groups, retains selection and binds collapse and progress", async () =>
            {
                var source = ConversionOptionsSource();
                var models = MultiOutputTaskFactory.Create(source, (OutputConfiguration[])[
                    new() { Label = "Share", TargetFormatId = "mp4", Options = new()
                    {
                        [MediaConversionOptions.TargetSizeKey] = "10485760", [MediaConversionOptions.MaxWidthKey] = "1280"
                    } }, new() { Label = "Audio", TargetFormatId = "mp3" }
                ]);
                var memberViews = models.Select(model => new MediaTaskViewModel(model)).ToArray();
                var unrelated = new MediaTaskViewModel(new MediaTask
                {
                    InputPath = Path.Combine(_runRoot, "conversion-options", "separate.png"),
                    SourceFormatId = "png", TargetFormatId = "jpg", State = ConversionState.Waiting
                });
                unrelated.Model.AvailableFormats.Add(FormatCatalog.FindById("jpg")!.DisplayName);
                var rows = new ObservableCollection<MediaTaskViewModel>(memberViews.Append(unrelated));
                var queue = new QueueView();
                queue.ApplyLocalization(new());
                queue.BindTasks(rows);
                queue.TaskList.Visibility = Visibility.Visible;
                queue.EmptyState.Visibility = Visibility.Collapsed;
                host.Children.Add(queue);
                try
                {
                    host.UpdateLayout();
                    await Task.Delay(100);
                    host.UpdateLayout();
                    var grouped = (CollectionViewSource)typeof(QueueView).GetField("_groupedSource", InstanceMembers)!.GetValue(queue)!;
                    var groups = (ObservableCollection<OutputTaskGroupViewModel>)typeof(QueueView).GetField("_outputGroups", InstanceMembers)!.GetValue(queue)!;
                    Require(grouped.IsSourceGrouped && queue.TaskList.Items.OfType<MediaTaskViewModel>().SequenceEqual(rows) && groups.Count == 2,
                        "The real grouped CollectionViewSource did not expose ordinary selectable output rows.");
                    var group = groups.Single(item => item.Id == models[0].OutputGroupId);
                    Require(group.IsGrouped && group.Count == 2 && !groups.Single(item => item.Id == unrelated.Id).IsGrouped,
                        "Grouped and unrelated source rows were mixed together.");
                    queue.TaskList.SelectedItems.Add(memberViews[1]);
                    queue.RefreshOutputGroups();
                    Require(queue.SelectedTasks.Length == 1 && ReferenceEquals(queue.SelectedTasks[0], memberViews[1]) &&
                        ReferenceEquals(group, groups.Single(item => item.Id == group.Id)),
                        "Refreshing the grouped queue lost selection or replaced its group object.");
                    typeof(QueueView).GetMethod("ToggleOutputGroup_Click", InstanceMembers)!
                        .Invoke(queue, [new Button { Tag = group.Id }, new RoutedEventArgs()]);
                    host.UpdateLayout();
                    Require(!group.IsExpanded && memberViews.All(item => !item.OutputGroupExpanded),
                        "Collapsing the source group did not reach every bound child row.");
                    foreach (var item in memberViews)
                        if (queue.TaskList.ContainerFromItem(item) is ListViewItem container)
                            Require(container.Visibility == Visibility.Collapsed, "A realized child stayed visible after collapsing its source group.");
                    typeof(QueueView).GetMethod("ToggleOutputGroup_Click", InstanceMembers)!
                        .Invoke(queue, [new Button { Tag = group.Id }, new RoutedEventArgs()]);
                    memberViews[0].State = ConversionState.Completed;
                    memberViews[1].Progress = 40;
                    host.UpdateLayout();
                    await Task.Delay(30);
                    Require(group.IsExpanded && group.Progress == 70 && group.Summary == LocalizationService.T("MultiOutputProgress", 1, 2),
                        "The source group did not aggregate independent completion and progress.");
                    Require(FindDescendants<TextBlock>(queue.TaskList).Any(text => text.Text == group.Summary) &&
                        FindDescendants<TextBlock>(queue.TaskList).Any(text => text.Text == memberViews[0].OutputDescription) &&
                        FindDescendants<TextBlock>(queue.TaskList).Any(text => text.Text == memberViews[0].ConversionSettingsSummary),
                        "The real group header, output label or retained custom settings were not displayed.");
                    if (_capture) await CaptureConversionOptionsGroupAsync(host);
                }
                finally
                {
                    queue.BindTasks(new());
                    host.Children.Remove(queue);
                    foreach (var item in rows) item.Dispose();
                }
            });
        }
        finally { shell.Close(); }
    }

    private MediaTask ConversionOptionsSource()
    {
        var task = new MediaTask
        {
            InputPath = Path.Combine(_runRoot, "conversion-options", "presentation.mp4"),
            SourceFormatId = "mp4", TargetFormatId = "mp4", State = ConversionState.Waiting,
            Probe = new MediaProbeResult { Kind = MediaKind.Video, Width = 1920, Height = 1080, Duration = TimeSpan.FromSeconds(12) }
        };
        foreach (var id in new[] { "mp4", "mkv", "mp3" }) task.AvailableFormats.Add(FormatCatalog.FindById(id)!.DisplayName);
        return task;
    }

    private static T OptionsControl<T>(DependencyObject parent, string headerKey) where T : FrameworkElement =>
        OptionsSmokeDescendants(parent).OfType<T>().Single(control => control switch
        {
            ComboBox combo => Equals(combo.Header, LocalizationService.T(headerKey)),
            NumberBox number => Equals(number.Header, LocalizationService.T(headerKey)),
            TextBox text => Equals(text.Header, LocalizationService.T(headerKey)),
            _ => false
        });

    private static IEnumerable<DependencyObject> OptionsSmokeDescendants(DependencyObject parent)
    {
        IEnumerable<DependencyObject> children = parent switch
        {
            Panel panel => panel.Children.Cast<DependencyObject>(),
            ScrollViewer { Content: DependencyObject child } => (DependencyObject[])[child],
            ContentControl { Content: DependencyObject child } => (DependencyObject[])[child],
            _ => Array.Empty<DependencyObject>()
        };
        foreach (var child in children)
        {
            yield return child;
            foreach (var descendant in OptionsSmokeDescendants(child)) yield return descendant;
        }
    }

    private static async Task WithOptionsContentAsync(Grid host, ContentDialog dialog, Func<DependencyObject, Task> check)
    {
        var content = (ScrollViewer)dialog.Content;
        dialog.Content = null;
        host.Children.Add(content);
        try
        {
            host.UpdateLayout();
            await Task.Delay(25);
            await check(content);
        }
        finally
        {
            host.Children.Remove(content);
            dialog.Content = content;
        }
    }

    private async Task CaptureConversionOptionsGroupAsync(Grid root)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "The multi-output queue capture is empty.");
        var path = Path.Combine(_runRoot, "captures");
        Directory.CreateDirectory(path);
        var folder = await StorageFolder.GetFolderFromPathAsync(path);
        var file = await folder.CreateFileAsync("queue-multi-output.png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }
}
