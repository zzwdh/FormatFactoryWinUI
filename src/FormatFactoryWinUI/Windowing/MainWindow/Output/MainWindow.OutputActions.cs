using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private readonly HashSet<NativeMediaPreviewWindow> _previewWindows = [];

    private void TaskList_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (TryPrepareQueueReorderDrag(args)) return;
        var tasks = args.Items.OfType<MediaTaskViewModel>().ToArray();
        if (tasks.Length == 0 || tasks.Any(task => task.State != ConversionState.Completed ||
                string.IsNullOrWhiteSpace(task.OutputPath) || !PathExists(task.OutputPath)))
        {
            args.Cancel = true;
            return;
        }
        OutputTransferService.PrepareDrag(args.Data, tasks.Select(task => task.OutputPath!).ToArray());
    }

    private async void CopyOutputButton_Click(object sender, RoutedEventArgs args)
    {
        var task = FindTaskFromSender(sender);
        if (task?.State != ConversionState.Completed || string.IsNullOrWhiteSpace(task.OutputPath)) return;
        try
        {
            await OutputTransferService.CopyAsync(new[] { task.OutputPath });
            ShowInfo(LocalizationService.T("DesktopOutputCopied"), InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo(ex.Message, InfoBarSeverity.Warning); }
    }

    private async void OpenOutputFileButton_Click(object sender, RoutedEventArgs args)
    {
        var task = FindTaskFromSender(sender);
        if (task?.State != ConversionState.Completed || string.IsNullOrWhiteSpace(task.OutputPath)) return;
        try
        {
            var opened = Directory.Exists(task.OutputPath)
                ? await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(task.OutputPath))
                : await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(task.OutputPath));
            if (!opened) ShowInfo(LocalizationService.T("DesktopOpenFailed"), InfoBarSeverity.Warning);
        }
        catch (Exception ex) { ShowInfo(ex.Message, InfoBarSeverity.Warning); }
        finally
        {
            if (!_windowClosed)
            {
                _queuePage.InvalidateOutputAvailability(task.Id);
                UpdateTaskbarProgress();
            }
        }
    }

    private void PreviewTaskButton_Click(object sender, RoutedEventArgs args)
    {
        var task = FindTaskFromSender(sender);
        if (task is null) return;
        var sources = new List<NativePreviewSource>();
        var inputs = task.Model.InputPaths;
        foreach (var path in inputs.Where(File.Exists))
            sources.Add(new(LocalizationService.T("DesktopPreviewInput", Path.GetFileName(path)), path, _pdfPasswords.GetValueOrDefault(task.Id)));
        if (task.State == ConversionState.Completed && File.Exists(task.OutputPath))
            sources.Add(new(LocalizationService.T("DesktopPreviewResult", Path.GetFileName(task.OutputPath)), task.OutputPath!, null));
        if (sources.Count == 0)
        {
            ShowInfo(LocalizationService.T("DesktopPreviewMissing"), InfoBarSeverity.Warning);
            return;
        }
        var preview = new NativeMediaPreviewWindow(sources, RootGrid.ActualTheme);
        _previewWindows.Add(preview);
        preview.Closed += (_, _) => _previewWindows.Remove(preview);
        preview.Activate();
    }

    private void ClosePreviewWindows()
    {
        foreach (var preview in _previewWindows.ToArray()) preview.Close();
        _previewWindows.Clear();
    }

    private async void ImageColorButton_Click(object sender, RoutedEventArgs args)
    {
        var task = FindTaskFromSender(sender);
        if (task?.CanEditImageColor != true) return;
        var choice = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Header = LocalizationService.T("NativeImageColorTitle"),
            ItemsSource = new[] { LocalizationService.T("NativeImageColorSrgb"), LocalizationService.T("NativeImageColorPreserve") },
            SelectedIndex = task.Model.Options.GetValueOrDefault("imageColorPolicy") == "preserve" ? 1 : 0
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(choice);
        panel.Children.Add(new TextBlock { Text = LocalizationService.T("NativeImageColorHelp"), TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot, Title = LocalizationService.T("NativeImageColorTitle"), Content = panel,
            PrimaryButtonText = LocalizationService.T("SaveSettings"), CloseButtonText = LocalizationService.T("NativeUiCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        try
        {
            if (await ShowContentDialogAsync(dialog) == ContentDialogResult.Primary && task.CanEditImageColor)
            {
                task.Model.Options["imageColorPolicy"] = choice.SelectedIndex == 1 ? "preserve" : "srgb";
                await SaveQueueAsync();
            }
        }
        catch (Exception ex) { ShowInfo(ex.Message, InfoBarSeverity.Warning); }
    }
}
