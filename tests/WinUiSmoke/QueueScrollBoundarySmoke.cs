using System.Collections.ObjectModel;
using FormatFactoryWinUI.ViewModels;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    // Loaded functional checks only; never called from the timed performance mode.
    private async Task CheckQueueScrollBoundaryAsync()
    {
        var host = new Grid();
        var shell = new Window { Content = host };
        var queue = new QueueView();
        var rows = new ObservableCollection<MediaTaskViewModel>();
        try
        {
            queue.ApplyLocalization(new());
            queue.TaskList.Visibility = Visibility.Visible;
            queue.EmptyState.Visibility = Visibility.Collapsed;
            host.Children.Add(queue);
            shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 930));
            shell.AppWindow.Show(false);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!host.IsLoaded && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
            Require(host.IsLoaded, "The isolated scroll window did not load.");
            var scale = host.XamlRoot.RasterizationScale;
            shell.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(1280 * scale), (int)(900 * scale)));
            foreach (var count in new[] { 1000, 10000 })
            {
                queue.BindTasks(new());
                foreach (var row in rows) row.Dispose();
                rows = new(Enumerable.Range(0, count).Select(CreateSampleTask));
                if (count == 10000)
                {
                    var groupId = Guid.NewGuid();
                    rows[0].Model.OutputGroupId = rows[3].Model.OutputGroupId = groupId;
                }
                queue.BindTasks(rows);
                host.UpdateLayout();
                await Task.Delay(250);
                host.UpdateLayout();
                var scroller = FindDescendant<ScrollViewer>(queue.TaskList)!;
                var provider = (IScrollProvider)new ScrollViewerAutomationPeer(scroller).GetPattern(PatternInterface.Scroll);
                await CheckAsync($"A single UIA bottom request reaches the last of {count} variable-height tasks", async () =>
                {
                    provider.SetScrollPercent(-1, 100);
                    await Task.Delay(1200);
                    host.UpdateLayout();
                    var panel = (ItemsStackPanel)queue.TaskList.ItemsPanelRoot;
                    var lastRow = queue.TaskList.ContainerFromItem(rows[^1]) as FrameworkElement;
                    var bounds = lastRow?.TransformToVisual(scroller).TransformBounds(new Windows.Foundation.Rect(0, 0, lastRow.ActualWidth, lastRow.ActualHeight));
                    var details = $"visible={panel.FirstVisibleIndex}..{panel.LastVisibleIndex}, offset={scroller.VerticalOffset}, extent={scroller.ExtentHeight}, viewport={scroller.ViewportWidth}x{scroller.ViewportHeight}, lastBounds={bounds}";
                    Require(panel.LastVisibleIndex == count - 1,
                        $"A single {count}-task bottom request stopped before the final task. {details}");
                    Require(lastRow is not null && DensityFullyVisible(lastRow, scroller),
                        $"The final task was not physically visible after the bottom request settled. {details}");
                    Require(Enumerable.Range(0, count).Count(index => queue.TaskList.ContainerFromIndex(index) is not null) < 250,
                        "Reaching the final task disabled list virtualization.");
                });
                await CheckAsync($"Scrolling away cancels the {count}-task endpoint request without sticking", async () =>
                {
                    provider.SetScrollPercent(-1, 0);
                    await Task.Delay(250);
                    provider.SetScrollPercent(-1, 100);
                    await Task.Delay(30);
                    provider.SetScrollPercent(-1, 0);
                    await Task.Delay(900);
                    var panel = (ItemsStackPanel)queue.TaskList.ItemsPanelRoot;
                    var firstRow = queue.TaskList.ContainerFromItem(rows[0]) as FrameworkElement;
                    var firstName = firstRow is null ? null :
                        FindDescendants<TextBlock>(firstRow).FirstOrDefault(label => label.Text == rows[0].FileName);
                    // WinUI can adjust its offset a few pixels after the first row measures.
                    // Verify that the user's actual starting task and its title are in view.
                    Require(panel.FirstVisibleIndex == 0 && firstRow is not null && scroller.VerticalOffset < firstRow.ActualHeight &&
                        firstName is not null && DensityFullyVisible(firstName, scroller),
                        $"A delayed end correction overrode the later request to leave the bottom. visible={panel.FirstVisibleIndex}..{panel.LastVisibleIndex}, offset={scroller.VerticalOffset}, viewport={scroller.ViewportHeight}");
                });
            }

            await CheckAsync("A collapsed final output group stays collapsed and its header remains visible at the bottom", async () =>
            {
                foreach (var row in rows) row.Model.OutputGroupId = null;
                var groupId = Guid.NewGuid();
                rows[^2].Model.OutputGroupId = rows[^1].Model.OutputGroupId = groupId;
                queue.RefreshOutputGroups();
                typeof(QueueView).GetMethod("ToggleOutputGroup_Click", InstanceMembers)!
                    .Invoke(queue, [new Button { Tag = groupId }, new RoutedEventArgs()]);
                var scroller = FindDescendant<ScrollViewer>(queue.TaskList)!;
                var provider = (IScrollProvider)new ScrollViewerAutomationPeer(scroller).GetPattern(PatternInterface.Scroll);
                provider.SetScrollPercent(-1, 0);
                await Task.Delay(250);
                provider.SetScrollPercent(-1, 100);
                await Task.Delay(1200);
                host.UpdateLayout();
                Require(!rows[^2].OutputGroupExpanded && !rows[^1].OutputGroupExpanded,
                    "Endpoint correction expanded the final output group.");
                var header = FindDescendants<Button>(queue.TaskList).SingleOrDefault(button => Equals(button.Tag, groupId));
                var panel = (ItemsStackPanel)queue.TaskList.ItemsPanelRoot;
                Require(header is not null && DensityFullyVisible(header, scroller),
                    $"The collapsed final group header did not reach the viewport. visible={panel.FirstVisibleIndex}..{panel.LastVisibleIndex}, offset={scroller.VerticalOffset}, extent={scroller.ExtentHeight}, viewport={scroller.ViewportHeight}, headerRealized={header is not null}");
            });
        }
        finally
        {
            queue.BindTasks(new());
            shell.Close();
            foreach (var row in rows) row.Dispose();
        }
    }
}
