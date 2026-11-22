using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckResultSummaryAsync()
    {
        Check("Production queue aggregate displays captured result sizes and counts", () =>
        {
            var session = (QueueSession)Field("_queueSession")!;
            var productionQueue = (QueueView)Field("ConverterView")!;
            var completedModel = QueueExperienceModel("summary-completed.mp4");
            completedModel.State = ConversionState.Completed;
            completedModel.ResultStatistics = new(240 * 1048576L, 68 * 1048576L, 41_000);
            session.AddTask(completedModel);
            try
            {
                Invoke("UpdateQueueSummary");
                Require(productionQueue.ConversionResultsSummaryText.Text ==
                    ConversionResultSummaryFormatter.FormatBatch(_window!.Tasks.Select(task => task.Model)) &&
                    productionQueue.ConversionResultsSummaryText.Visibility == Visibility.Collapsed &&
                    productionQueue.QueueSummaryDetailsText.Text.Contains(productionQueue.ConversionResultsSummaryText.Text,
                        StringComparison.Ordinal),
                    "The production queue did not retain aggregate measurements in its summary details without a duplicate row.");
            }
            finally
            {
                if (_window!.Tasks.FirstOrDefault(task => task.Id == completedModel.Id) is { } row) Invoke("RemoveTask", row);
            }
        });

        var language = LocalizationService.CurrentLanguageCode;
        var host = (Grid)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  RequestedTheme="Light" Background="{ThemeResource SolidBackgroundFillColorBaseBrush}" />
            """);
        var shell = new Window { Content = host };
        using var history = new HistoryView();
        var model = QueueExperienceModel("summary-video.mp4");
        model.State = ConversionState.Completed;
        model.ResultStatistics = new(240 * 1048576L, 68 * 1048576L, 41_000);
        using var view = new MediaTaskViewModel(model);
        var queue = new QueueView();
        try
        {
            shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 1000));
            shell.AppWindow.Show(false);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!host.IsLoaded && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
            Require(host.IsLoaded && host.XamlRoot is not null, "The isolated result-summary window did not load.");
            var scale = host.XamlRoot!.RasterizationScale;
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling(1040 * scale), (int)Math.Ceiling(1000 * scale)));

            await CheckNativeAsync("Rendered queue result summaries localize and hide when a task retries", async () =>
            {
                queue.BindTasks(new ObservableCollection<MediaTaskViewModel> { view });
                queue.EmptyState.Visibility = Visibility.Collapsed;
                queue.TaskList.Visibility = Visibility.Visible;
                host.Children.Add(queue);
                foreach (var code in new[] { "en-US", "zh-Hans" })
                {
                    LocalizationService.SetLanguage(code);
                    view.RefreshLocalization();
                    queue.ApplyLocalization(new());
                    queue.ConversionResultsSummaryText.Text = ConversionResultSummaryFormatter.FormatBatch(new[] { model });
                    queue.QueueSummaryDetailsText.Text = queue.ConversionResultsSummaryText.Text;
                    host.UpdateLayout();
                    await Task.Delay(100);
                    var text = FindDescendants<TextBlock>(queue).Single(block => block.Name == "TaskResultSummaryText");
                    Require(text.Visibility == Visibility.Visible && text.ActualWidth > 0 &&
                        text.Text == ConversionResultSummaryFormatter.Format(model.ResultStatistics) &&
                        text.Text.Contains("240 MB") && text.Text.Contains("68 MB") && text.Text.Contains("00:00:41"),
                        "The rendered row lost its measured sizes, duration, or selected language.");
                }
                if (_capture) await SaveResultSummaryCaptureAsync(host, "result-summary-queue");
                model.State = ConversionState.Running;
                await Task.Delay(50);
                var retryText = FindDescendants<TextBlock>(queue).Single(block => block.Name == "TaskResultSummaryText");
                Require(retryText.Visibility == Visibility.Collapsed, "A retry displayed stale completed statistics.");
                model.State = ConversionState.Completed;
                host.Children.Clear();
            });

            await CheckNativeAsync("Rendered history preserves the summary and clears it for a reused legacy row", async () =>
            {
                var item = ConversionHistoryItem.FromTask(model);
                item.OutputPath = Path.Combine(_runRoot, "result-summary-output.mp4");
                item.DisplayName = "result-summary-output.mp4";
                history.ViewModel.Items.Add(item);
                history.ApplyLocalization();
                history.UpdateContentVisibility();
                host.Children.Add(history);
                host.UpdateLayout();
                await Task.Delay(100);
                var text = HistoryResultTextForItem(history, item);
                Require(text.Visibility == Visibility.Visible && text.ActualWidth > 0 &&
                    text.Text == HistoryPageViewModel.DescribeResult(item), "The rendered history omitted its saved result summary.");
                if (_capture) await SaveResultSummaryCaptureAsync(host, "result-summary-history");
                var legacy = new ConversionHistoryItem { TaskId = Guid.NewGuid(), DisplayName = "Legacy record" };
                history.ViewModel.Items[0] = legacy;
                host.UpdateLayout();
                await Task.Delay(100);
                var reused = HistoryResultTextForItem(history, legacy);
                Require(reused.Visibility == Visibility.Collapsed && string.IsNullOrEmpty(reused.Text),
                    "A recycled history row retained another record's result measurements.");

                // Replacement may retain the old container for its remove animation.
                // Also exercise DataContextChanged on the same realized card, without
                // calling the label helper or relying on another Loaded event.
                var container = (ListViewItem)history.HistoryList.ContainerFromItem(legacy);
                var card = FindDescendants<Border>(container).Single(border => ReferenceEquals(border.Tag, legacy));
                card.DataContext = item;
                await Task.Delay(25);
                Require(reused.Visibility == Visibility.Visible && reused.Text == HistoryPageViewModel.DescribeResult(item),
                    "The realized history card did not refresh after receiving a measured record.");
                card.DataContext = legacy;
                await Task.Delay(25);
                Require(ReferenceEquals(reused.DataContext, legacy) && reused.Visibility == Visibility.Collapsed &&
                    string.IsNullOrEmpty(reused.Text), "Rebinding the same history card to a legacy record did not clear its summary.");
            });

            await CheckNativeAsync("History task context menu routes the current record", async () =>
            {
                var item = history.ViewModel.Items.Single();
                var container = (ListViewItem)history.HistoryList.ContainerFromItem(item);
                var card = FindDescendants<Border>(container).Single(border => ReferenceEquals(border.Tag, item));
                var invoked = new TaskCompletionSource<ConversionHistoryItem>(TaskCreationOptions.RunContinuationsAsynchronously);
                history.RepeatHistoryTaskButton_ClickRequested += (sender, _) =>
                {
                    if (sender is Button { Tag: ConversionHistoryItem record }) invoked.TrySetResult(record);
                };
                var menu = await OpenTaskContextMenuAsync(card);
                var repeat = menu.Items.OfType<MenuFlyoutItem>().Single(action => action.Text == LocalizationService.T("HistoryRepeatTask"));
                InvokeTaskContextMenuItem(repeat);
                Require(ReferenceEquals(await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5)), item),
                    "The history menu tried to repeat a different record.");
            });
        }
        finally
        {
            LocalizationService.SetLanguage(language);
            host.Children.Clear();
            shell.Close();
        }
    }

    private static TextBlock HistoryResultTextForItem(HistoryView history, ConversionHistoryItem item)
    {
        var container = history.HistoryList.ContainerFromItem(item) as ListViewItem;
        Require(container is not null && ReferenceEquals(history.HistoryList.ItemFromContainer(container), item),
            "The current history record has no matching realized container.");
        return FindDescendants<TextBlock>(container!).Single(block => block.Name == "HistoryResultSummaryText" &&
            ReferenceEquals(block.DataContext, item));
    }

    private async Task SaveResultSummaryCaptureAsync(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "The result-summary capture was empty.");
        var path = Path.Combine(_runRoot, "captures");
        Directory.CreateDirectory(path);
        var folder = await StorageFolder.GetFolderFromPathAsync(path);
        var file = await folder.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }
}
