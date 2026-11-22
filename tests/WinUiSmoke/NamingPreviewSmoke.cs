using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task RunNamingPreviewChecksAsync()
    {
        var host = new Grid
        {
            RequestedTheme = ElementTheme.Light,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
        };
        var shell = new Window { Content = host };
        var settings = (AppSettings)Field("_settings")!;
        var originalSettings = settings.Snapshot();
        var session = (QueueSession)Field("_queueSession")!;
        var queue = (QueueView)Field("ConverterView")!;
        var sourceRoot = Path.Combine(_runRoot, "naming-preview-source");
        var destination = Path.Combine(_runRoot, "naming-preview-output");
        var source = new MediaTask
        {
            InputPath = Path.Combine(sourceRoot, "album", "preview.mov"), SourceRoot = sourceRoot,
            Probe = new MediaProbeResult { Width = 1920, Height = 1080 }, ContentKind = ContentKind.Video,
            TargetFormatId = "mp4", OutputLabel = "Mobile"
        };
        source.AvailableFormats.Add("MP4");
        try
        {
            shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 900));
            shell.AppWindow.Show(false);
            await NamingWaitAsync(() => host.IsLoaded && host.XamlRoot is not null);
            settings.OutputMode = "custom";
            settings.CustomOutputDirectory = destination;
            settings.OutputFileNameTemplate = "{name}";
            settings.PreserveFolderStructure = true;
            session.AddTask(source);
            Invoke("RefreshVisibleTasks");
            queue.TaskList.SelectedItems.Clear();
            queue.TaskList.SelectedItems.Add(_window!.Tasks.Single(task => task.Id == source.Id));

            await CheckAsync("Naming preview updates live for selected source without writing or saving draft settings", async () =>
            {
                var dialog = (ContentDialog)Invoke("CreateOutputSettingsDialog")!;
                var content = (StackPanel)dialog.Content;
                dialog.Content = null;
                host.Children.Add(content);
                try
                {
                    var input = content.Children.OfType<TextBox>().Single();
                    var keepFolders = content.Children.OfType<ToggleSwitch>().Single();
                    var preview = (TextBlock)content.Children.OfType<ScrollViewer>().Single().Content;
                    var patterns = content.Children.OfType<ComboBox>().Single();
                    await NamingWaitAsync(() => preview.Text == Path.Combine(destination, "album", "preview_Mobile.mp4"));
                    Require(!Directory.Exists(destination), "Preview created an output folder.");
                    Require(AutomationProperties.GetLiveSetting(preview) == AutomationLiveSetting.Polite && preview.IsTextSelectionEnabled,
                        "The output path preview is not accessible or selectable.");
                    patterns.SelectedItem = "{name}_{preset}_{format}";
                    await NamingWaitAsync(() => preview.Text.EndsWith("preview_Mobile_MP4.mp4", StringComparison.Ordinal));
                    Require(input.Text == "{name}_{preset}_{format}", "Choosing a template did not fill the editor.");
                    input.Text = "{name}_{width}x{height}_{preset}";
                    await NamingWaitAsync(() => preview.Text.EndsWith("preview_1920x1080_Mobile.mp4", StringComparison.Ordinal));
                    if (_capture) await SaveResultSummaryCaptureAsync(host, "naming-path-preview");
                    keepFolders.IsOn = false;
                    await NamingWaitAsync(() => preview.Text == Path.Combine(destination, "preview_1920x1080_Mobile.mp4"));
                    input.Text = "../{name}";
                    Require(!dialog.IsPrimaryButtonEnabled && preview.Text.Length == 0 && content.Children.OfType<InfoBar>().Single().IsOpen,
                        "An invalid template remained saveable or showed an old path.");
                    input.Text = "{name}_{preset}";
                    await NamingWaitAsync(() => preview.Text == Path.Combine(destination, "preview_Mobile.mp4"));
                    Require(dialog.IsPrimaryButtonEnabled && settings.OutputFileNameTemplate == "{name}" && settings.PreserveFolderStructure,
                        "Editing a naming draft mutated settings before save.");
                    input.Select(input.Text.Length, 0);
                    var tokens = content.Children.OfType<StackPanel>().Single();
                    var button = tokens.Children.OfType<Button>().Single(item => Equals(item.Content, "{date}"));
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                    Require(input.Text.EndsWith("{date}", StringComparison.Ordinal), "The token button did not insert at the cursor.");
                    await NamingWaitAsync(() => preview.Text.Contains(DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
                }
                finally
                {
                    host.Children.Remove(content);
                    dialog.Content = content;
                }
            });

            Check("Naming preview is translated in every supported language", () =>
            {
                var language = LocalizationService.CurrentLanguageCode;
                try
                {
                    LocalizationService.SetLanguage("en-US");
                    var english = LocalizationService.T("NamingPreviewNote");
                    foreach (var option in LocalizationService.LanguageOptions.Where(option => option.Key != "auto"))
                    {
                        LocalizationService.SetLanguage(option.Key);
                        var localized = LocalizationService.T("NamingPreviewNote");
                        Require(localized != "NamingPreviewNote" && (option.Key == "en-US" || localized != english),
                            "Naming preview is not translated for " + option.Key);
                    }
                }
                finally { LocalizationService.SetLanguage(language); }
            });
        }
        finally
        {
            settings.CopyFrom(originalSettings);
            if (_window!.Tasks.FirstOrDefault(task => task.Id == source.Id) is { } row) Invoke("RemoveTask", row);
            queue.TaskList.SelectedItems.Clear();
            shell.Close();
        }
    }

    private static async Task NamingWaitAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
        Require(condition(), "Timed out waiting for the live naming preview.");
    }
}
