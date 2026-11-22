using FormatFactoryWinUI.Controls;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckProgressStateColorsAsync()
    {
        var host = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        var shell = new Window { Content = host };
        var running = new TaskProgressBar { Width = 400, Progress = 42, State = ConversionState.Running };
        var complete = new TaskProgressBar { Width = 400, Progress = 100, State = ConversionState.Completed };
        var paused = new TaskProgressBar { Width = 400, Progress = 65, State = ConversionState.Paused };
        var failed = new TaskProgressBar { Width = 400, Progress = 80, State = ConversionState.Failed };
        var recycled = new TaskProgressBar { Width = 400 };
        recycled.SetBinding(TaskProgressBar.ProgressProperty, new Binding { Path = new PropertyPath(nameof(MediaTask.Progress)) });
        recycled.SetBinding(TaskProgressBar.StateProperty, new Binding { Path = new PropertyPath(nameof(MediaTask.State)) });
        var bars = new[] { running, complete, paused, failed };
        foreach (var bar in bars) host.Children.Add(bar);
        host.Children.Add(recycled);
        try
        {
            shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(800, 500));
            shell.AppWindow.Show(false);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!host.IsLoaded && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
            Require(host.IsLoaded, "The independent progress-color fixture did not load.");
            host.UpdateLayout();
            await Task.Delay(300);

            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                host.RequestedTheme = theme;
                await Task.Delay(300);
                await CheckAsync($"Progress state colors remain independent in {theme} theme", async () =>
                {
                    var accent = ProgressColor(running);
                    var snapshot = DescribeProgressColors(bars);
                    File.AppendAllText(Path.Combine(_runRoot, "progress-colors.txt"), theme + " " + snapshot + Environment.NewLine);
                    Require(ProgressColor(complete) == accent,
                        "A completed task inherited another task's error or pause color. " + snapshot);
                    Require(ProgressColor(paused) != accent && ProgressColor(failed) != accent &&
                        ProgressColor(paused) != ProgressColor(failed),
                        "Error, pause and normal indicators no longer show separate theme colors. " + snapshot);
                    Require(!running.ShowError && !complete.ShowError && paused.ShowPaused && failed.ShowError,
                        "The fixture did not exercise the requested native states.");
                    await Task.CompletedTask;
                });

                await CheckAsync($"Progress retry clears visual error colors in {theme} theme", async () =>
                {
                    failed.State = ConversionState.Running;
                    failed.Progress = 5;
                    paused.State = ConversionState.Completed;
                    paused.Progress = 100;
                    await Task.Delay(300);
                    var snapshot = DescribeProgressColors(bars);
                    Require(ProgressColor(failed) == ProgressColor(running) && ProgressColor(paused) == ProgressColor(complete),
                        "A retry or completion retained an earlier native state color. " + snapshot);
                    Require(failed.Value == 5 && paused.Value == 100 && !failed.ShowError && !paused.ShowPaused,
                        "Clearing a visual state changed the exact progress or state flags.");
                    failed.State = ConversionState.Failed;
                    failed.Progress = 80;
                    paused.State = ConversionState.Paused;
                    paused.Progress = 65;
                    await Task.Delay(250);
                });

                await CheckAsync($"Recycled progress bindings do not retain the previous task color in {theme} theme", async () =>
                {
                    foreach (var (previous, next) in new[]
                    {
                        (ConversionState.Failed, ConversionState.Completed),
                        (ConversionState.Paused, ConversionState.Running),
                        (ConversionState.Cancelled, ConversionState.Completed)
                    })
                    {
                        recycled.DataContext = new MediaTask { State = previous, Progress = 70 };
                        host.UpdateLayout();
                        await Task.Delay(220);
                        Require(recycled.State == previous &&
                            (previous == ConversionState.Failed ? recycled.ShowError : recycled.ShowPaused),
                            "The recycling fixture did not reach its prior error or pause state.");
                        recycled.DataContext = null;
                        recycled.DataContext = new MediaTask { State = next, Progress = next == ConversionState.Completed ? 100 : 8 };
                        host.UpdateLayout();
                        await Task.Delay(220);
                        Require(recycled.State == next && !recycled.ShowError && !recycled.ShowPaused &&
                            ProgressColor(recycled) == ProgressColor(complete),
                            "Replacing the real binding context retained a previous task's color. " + DescribeProgressColors(new[] { recycled, complete }));
                    }
                    recycled.DataContext = new MediaTask { State = ConversionState.Failed, Progress = 80 };
                    recycled.DataContext = new MediaTask { State = ConversionState.Paused, Progress = 50 };
                    recycled.DataContext = new MediaTask { State = ConversionState.Completed, Progress = 100 };
                    host.UpdateLayout();
                    await Task.Delay(220);
                    Require(recycled.State == ConversionState.Completed && recycled.Value == 100 &&
                        !recycled.ShowError && !recycled.ShowPaused && ProgressColor(recycled) == ProgressColor(complete),
                        "Rapid recycled bindings did not settle on the final task's progress and color.");
                });
            }
        }
        finally
        {
            host.Children.Clear();
            shell.Close();
        }

        var productionWindow = _window;
        if (_capture && productionWindow is not null && productionWindow.Tasks.Count > 0)
        {
            var root = (Grid)Field("RootGrid")!;
            var list = (ListView)Field("TaskList")!;
            var requestedTheme = root.RequestedTheme;
            try
            {
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    root.RequestedTheme = theme;
                    root.UpdateLayout();
                    await Task.Delay(300);
                    await CheckAsync($"Production queue progress colors match visible task states in {theme} theme", async () =>
                    {
                        var visible = FindDescendants<TaskProgressBar>(list).Where(bar =>
                            bar.DataContext is MediaTaskViewModel task && productionWindow.VisibleTasks.Contains(task) &&
                            IsVisibleWithin(bar, list)).ToArray();
                        Require(visible.Length > 0, "No real bound queue progress indicators were visible for color validation.");
                        foreach (var bar in visible)
                        {
                            var task = (MediaTaskViewModel)bar.DataContext;
                            Require(bar.State == task.State && bar.ShowError == (task.State == ConversionState.Failed) &&
                                bar.ShowPaused == (task.State is ConversionState.Paused or ConversionState.Cancelled or ConversionState.Blocked),
                                "A visible progress indicator retained a previous task's state flags.");
                            if (!bar.ShowError && !bar.ShowPaused)
                                Require(bar.Foreground is SolidColorBrush foreground && ProgressColor(bar) == foreground.Color,
                                    "A normal production task retained a stale error, pause, or theme color. " + DescribeProgressColors(new[] { bar }));
                        }
                        await Task.CompletedTask;
                    });
                }
            }
            finally
            {
                root.RequestedTheme = requestedTheme;
                root.UpdateLayout();
                await Task.Delay(250);
            }
        }
    }

    private static Windows.UI.Color ProgressColor(TaskProgressBar bar) =>
        ((SolidColorBrush)FindDescendants<Rectangle>(bar).Single(shape => shape.Name == "DeterminateProgressBarIndicator").Fill).Color;

    private static string DescribeProgressColors(IEnumerable<TaskProgressBar> bars) => string.Join("; ", bars.Select(bar =>
        $"{bar.State}: fill={ProgressColor(bar)}, foreground={(bar.Foreground as SolidColorBrush)?.Color}, error={bar.ShowError}, paused={bar.ShowPaused}"));
}
