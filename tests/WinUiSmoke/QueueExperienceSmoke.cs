using System.Collections.ObjectModel;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckQueueExperienceAsync()
    {
        Check("Queue undo, priority, drag and source relocation reach production handlers once", () =>
        {
            var queue = (QueueView)Field("ConverterView")!;
            foreach (var (eventName, handlerName) in new[]
            {
                ("UndoRequested", "UndoQueue_Click"),
                ("PrioritizeTaskButton_ClickRequested", "PrioritizeTaskButton_Click"),
                ("TaskRow_DragOverRequested", "PendingTaskRow_DragOver"),
                ("TaskRow_DropRequested", "PendingTaskRow_Drop"),
                ("RelocateSource_ClickRequested", "RelocateSource_Click"),
                ("RelocateSourceFolder_ClickRequested", "RelocateSourceFolder_Click")
            })
            {
                RequireUpgradeEvent(queue, eventName, handlerName);
                var handlers = (Delegate)queue.GetType().GetField(eventName, InstanceMembers)!.GetValue(queue)!;
                Require(handlers.GetInvocationList().Length == 1, $"{eventName} was subscribed more than once.");
            }
        });

        await CheckAsync("Deleting a queue row exposes Undo and restores its identity, settings and password", async () =>
        {
            var session = (QueueSession)Field("_queueSession")!;
            var queue = (QueueView)Field("ConverterView")!;
            var passwords = (Dictionary<Guid, string>)Field("_pdfPasswords")!;
            var model = QueueExperienceModel("undo-source.mp4");
            model.Options[MediaConversionOptions.MaxWidthKey] = "1280";
            session.AddTask(model);
            passwords[model.Id] = "only-in-memory";
            try
            {
                var originalView = _window!.Tasks.Single(task => task.Id == model.Id);
                var removed = await (Task<int>)Invoke("RemoveTasksWithUndoAsync", (object)new MediaTaskViewModel[] { originalView })!;
                Require(removed == 1 && !session.Tasks.Contains(model) && !_window.Tasks.Any(task => task.Id == model.Id),
                    "The production remove command did not detach both model and view.");
                Require(queue.QueueUndoInfoBar.IsOpen && !passwords.ContainsKey(model.Id), "The undo action was not offered.");
                Invoke("UndoQueueAction");
                Require(session.Tasks.Contains(model) && ReferenceEquals(_window.Tasks.Single(task => task.Id == model.Id).Model, model),
                    "Undo did not restore the original task identity.");
                Require(model.Options[MediaConversionOptions.MaxWidthKey] == "1280" && passwords[model.Id] == "only-in-memory",
                    "Undo lost conversion settings or in-memory credentials.");
                Require(!queue.QueueUndoInfoBar.IsOpen, "The used undo action remained visible.");
            }
            finally
            {
                session.DiscardUndo(false);
                if (_window!.Tasks.FirstOrDefault(task => task.Id == model.Id) is { } row) Invoke("RemoveTask", row);
                passwords.Remove(model.Id);
            }
        });

        Check("Grouped queue display respects manual order without changing output configuration order", () =>
        {
            var first = QueueExperienceModel("group.mp4");
            var second = QueueExperienceModel("group.mp4");
            first.OutputGroupId = second.OutputGroupId = Guid.NewGuid();
            first.OutputOrder = 0;
            second.OutputOrder = 1;
            using var firstView = new MediaTaskViewModel(first);
            using var secondView = new MediaTaskViewModel(second);
            var rows = new ObservableCollection<MediaTaskViewModel> { firstView, secondView };
            var queue = new QueueView();
            queue.BindTasks(rows);
            rows.Move(1, 0);
            Require(queue.TaskList.Items.OfType<MediaTaskViewModel>().Select(task => task.Id).SequenceEqual(new[] { second.Id, first.Id }),
                "The grouped view overwrote the requested queue order.");
            Require(first.OutputOrder == 0 && second.OutputOrder == 1, "Ordering changed output configuration identity.");
        });

        await CheckAsync("Loaded queue retains selection, collapse and context actions across flat and grouped display", async () =>
        {
            var host = new Grid();
            var shell = new Window { Content = host };
            var queue = new QueueView();
            using var first = new MediaTaskViewModel(QueueExperienceModel("transition.mp4"));
            using var second = new MediaTaskViewModel(QueueExperienceModel("transition.mp4"));
            using var ordinary = new MediaTaskViewModel(QueueExperienceModel("ordinary.mp4"));
            using var appended = new MediaTaskViewModel(QueueExperienceModel("appended.mp4"));
            var rows = new ObservableCollection<MediaTaskViewModel> { first, second, ordinary };
            try
            {
                queue.ApplyLocalization(new());
                queue.BindTasks(rows);
                queue.EmptyState.Visibility = Visibility.Collapsed;
                queue.TaskList.Visibility = Visibility.Visible;
                host.Children.Add(queue);
                shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
                shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 1000));
                shell.AppWindow.Show(false);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while ((!host.IsLoaded || host.XamlRoot is null) && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
                Require(host.IsLoaded && host.XamlRoot is not null, "The queue transition window did not load.");
                var scale = host.XamlRoot!.RasterizationScale;
                shell.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(1040 * scale), (int)(1000 * scale)));
                host.UpdateLayout();
                await YieldLayoutAsync();
                Require(ReferenceEquals(queue.TaskList.ItemsSource, rows), "Ordinary tasks still used a grouped source.");
                Require(queue.TaskContentHost.ContextFlyout is MenuFlyout imports &&
                    imports.Items.OfType<MenuFlyoutItem>().Select(item => item.Text).SequenceEqual(new[]
                    {
                        queue.AddFilesText.Text, queue.AddFolderMenuItem.Text, queue.PasteFilesMenuItem.Text
                    }), "Right-clicking the queue background lacked localized import actions.");
                await CheckTaskContextMenuAsync(first, expectStart: true);
                queue.TaskList.SelectedItems.Add(first);

                var groupId = Guid.NewGuid();
                first.Model.OutputGroupId = second.Model.OutputGroupId = groupId;
                queue.RefreshOutputGroups();
                host.UpdateLayout();
                await YieldLayoutAsync();
                Require(!ReferenceEquals(queue.TaskList.ItemsSource, rows) && queue.SelectedTasks.Contains(first),
                    "Switching to a multi-output group lost the selection.");
                second.State = ConversionState.Running;
                await CheckTaskContextMenuAsync(second, expectStart: false);
                second.State = ConversionState.Waiting;
                typeof(QueueView).GetMethod("ToggleOutputGroup_Click", InstanceMembers)!
                    .Invoke(queue, new object[] { new Button { Tag = groupId }, new RoutedEventArgs() });
                Require(!first.OutputGroupExpanded && !second.OutputGroupExpanded && ordinary.OutputGroupExpanded,
                    "Group collapse affected the wrong tasks.");
                Require(queue.TaskList.ContainerFromItem(first) is ListViewItem { Visibility: Visibility.Collapsed },
                    "Collapsing a loaded group did not hide its realized row.");
                rows.Add(appended);
                queue.RefreshOutputGroups();
                Require(!first.OutputGroupExpanded && !second.OutputGroupExpanded && queue.SelectedTasks.Contains(first),
                    "Refreshing a mixed queue lost its group expansion or selection.");

                first.Model.OutputGroupId = second.Model.OutputGroupId = null;
                queue.RefreshOutputGroups();
                host.UpdateLayout();
                await YieldLayoutAsync();
                Require(ReferenceEquals(queue.TaskList.ItemsSource, rows) && queue.SelectedTasks.Contains(first) &&
                    rows.All(task => task.OutputGroupExpanded), "Returning to ordinary rows lost selection or left hidden tasks.");
                Require(queue.TaskList.ContainerFromItem(first) is ListViewItem { Visibility: Visibility.Visible },
                    "Returning to a flat queue left a collapsed container.");
                Require(queue.TaskList.Items.OfType<MediaTaskViewModel>().SequenceEqual(rows),
                    "Changing display modes changed task identity or order.");
                await CheckTaskContextMenuAsync(first, expectStart: true, fromMoreButton: true);
            }
            finally
            {
                rows.Clear();
                host.Children.Clear();
                shell.Close();
            }

            async Task CheckTaskContextMenuAsync(MediaTaskViewModel task, bool expectStart, bool fromMoreButton = false)
            {
                queue.TaskList.ScrollIntoView(task);
                host.UpdateLayout();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (queue.TaskList.ContainerFromItem(task) is not ListViewItem { IsLoaded: true } &&
                       DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }
                var container = queue.TaskList.ContainerFromItem(task) as ListViewItem
                    ?? throw new InvalidOperationException("The context-menu fixture did not realize the target row.");
                Require(container.IsLoaded, "The context-menu target row did not finish loading.");
                var menu = container.ContextFlyout as MenuFlyout
                    ?? throw new InvalidOperationException("The queue row is missing its native context flyout.");
                var start = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == "ContextStartTaskMenuItem");
                var pause = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == "ContextPauseTaskMenuItem");
                try
                {
                    if (fromMoreButton)
                    {
                        var more = FindDescendants<DropDownButton>(container).Single();
                        Require(IsVisibleWithin(more, host) && ReferenceEquals(more.Flyout, menu),
                            "The visible More button and context menu did not share the current row's commands.");
                        var provider = (IExpandCollapseProvider)new DropDownButtonAutomationPeer(more).GetPattern(PatternInterface.ExpandCollapse);
                        provider.Expand();
                    }
                    else
                    {
                        menu.ShowAt(container);
                    }
                    await YieldLayoutAsync();
                    Require(menu.IsOpen && ReferenceEquals(start.DataContext, task) && Equals(start.Tag, task.Id) &&
                        Equals(pause.Tag, task.Id) && start.IsEnabled == expectStart && pause.IsEnabled != expectStart,
                        "The native context menu did not open with actions gated for the invoked row.");
                    if (expectStart)
                    {
                        object? clicked = null;
                        void RecordStart(object sender, RoutedEventArgs _) => clicked = ((MenuFlyoutItem)sender).Tag;
                        queue.StartTaskButton_ClickRequested += RecordStart;
                        try
                        {
                            var provider = (IInvokeProvider)new MenuFlyoutItemAutomationPeer(start).GetPattern(PatternInterface.Invoke);
                            provider.Invoke();
                            await YieldLayoutAsync();
                            Require(Equals(clicked, task.Id), "Invoking Start from the context menu did not forward the task command.");
                        }
                        finally { queue.StartTaskButton_ClickRequested -= RecordStart; }
                    }
                }
                finally { menu.Hide(); }
            }
        });

        Check("Mixed queues coalesce ordinary rows and retain surviving groups during bulk removal", () =>
        {
            var rows = new ObservableCollection<MediaTaskViewModel>();
            var queue = new QueueView();
            for (var index = 0; index < 60; index++)
            {
                for (var ordinaryIndex = 0; ordinaryIndex < 4; ordinaryIndex++)
                    rows.Add(new MediaTaskViewModel(QueueExperienceModel($"ordinary-{index}-{ordinaryIndex}.mp4")));
                var groupId = Guid.NewGuid();
                for (var output = 0; output < 2; output++)
                {
                    var model = QueueExperienceModel($"group-{index}.mp4");
                    model.OutputGroupId = groupId;
                    rows.Add(new MediaTaskViewModel(model));
                }
            }
            var allRows = rows.ToArray();
            try
            {
                queue.BindTasks(rows);
                var groups = (IEnumerable<OutputTaskGroupViewModel>)typeof(QueueView)
                    .GetField("_outputGroups", InstanceMembers)!.GetValue(queue)!;
                Require(groups.Count() == 120 && groups.Where(group => !group.IsGrouped).All(group => group.Count == 4),
                    "Consecutive ordinary tasks created separate native groups.");
                var retained = groups.Last();
                retained.IsExpanded = false;
                var selected = retained[0];
                queue.TaskList.SelectedItems.Add(selected);
                using (queue.DeferOutputGroupRefresh())
                    foreach (var row in allRows.Take(40 * 6)) rows.Remove(row);
                Require(groups.Count() == 40 && ReferenceEquals(groups.Last(), retained) && !retained.IsExpanded,
                    "Bulk group removal replaced or expanded a surviving group.");
                Require(queue.SelectedTasks.Contains(selected) && queue.TaskList.Items.OfType<MediaTaskViewModel>().SequenceEqual(rows),
                    "Bulk group removal lost selection, identity or order.");
            }
            finally
            {
                rows.Clear();
                foreach (var row in allRows) row.Dispose();
            }
        });

        await CheckAsync("Priority command updates the displayed queue while retaining a running output", async () =>
        {
            var session = (QueueSession)Field("_queueSession")!;
            var queue = (QueueView)Field("ConverterView")!;
            var initializing = Field("_initializing");
            var filter = Field("_filter");
            var running = QueueExperienceModel("priority-group.mp4");
            var waiting = QueueExperienceModel("priority-group.mp4");
            var urgent = QueueExperienceModel("urgent.mp4");
            running.OutputGroupId = waiting.OutputGroupId = Guid.NewGuid();
            running.OutputConfigurationId = Guid.NewGuid();
            waiting.OutputConfigurationId = Guid.NewGuid();
            running.OutputExpectedCount = waiting.OutputExpectedCount = 2;
            running.State = ConversionState.Running;
            running.OutputOrder = 0;
            waiting.OutputOrder = 1;
            var models = new[] { running, waiting, urgent };
            foreach (var model in models) session.AddTask(model);
            try
            {
                SetField("_initializing", false);
                SetField("_filter", "all");
                var urgentView = _window!.Tasks.Single(task => task.Id == urgent.Id);
                Invoke("PrioritizeTaskButton_Click", new MenuFlyoutItem { DataContext = urgentView, Tag = urgent.Id }, new RoutedEventArgs());
                await Task.Delay(50); // The production handler refreshes the filtered view on the dispatcher.
                Require(session.Tasks.IndexOf(urgent) < session.Tasks.IndexOf(waiting), "Priority did not reorder the pending queue.");
                Require(running.State == ConversionState.Running, "Priority changed a running task.");
                var visibleIds = queue.TaskList.Items.OfType<MediaTaskViewModel>().Select(task => task.Id).ToArray();
                Require(Array.IndexOf(visibleIds, urgent.Id) < Array.IndexOf(visibleIds, waiting.Id),
                    "The displayed group order concealed the prioritized task.");
            }
            finally
            {
                SetField("_initializing", initializing);
                SetField("_filter", filter);
                foreach (var model in models)
                    if (_window!.Tasks.FirstOrDefault(task => task.Id == model.Id) is { } row) Invoke("RemoveTask", row);
                await session.SaveAsync();
            }
        });

        Check("Queue actions and undo feedback remain localized and respect task availability", () =>
        {
            var language = LocalizationService.CurrentLanguageCode;
            var model = QueueExperienceModel("missing-source.mp4");
            using var view = new MediaTaskViewModel(model);
            var queue = new QueueView();
            try
            {
                foreach (var code in new[] { "zh-Hans", "en-US", "ar-SA" })
                {
                    LocalizationService.SetLanguage(code);
                    view.RefreshLocalization();
                    queue.ApplyLocalization(new());
                    queue.ShowUndo(LocalizationService.T("QueueUndoRemoved", 1));
                    Require(view.PrioritizeTooltip == LocalizationService.T("QueuePriorityAction") &&
                        view.RelocateSourceTooltip == LocalizationService.T("SourceRelocationAction"), "Queue actions lost their language.");
                    Require(AutomationProperties.GetName(queue.QueueUndoButton) == LocalizationService.T("QueueUndoAction"),
                        "Undo did not expose an accessible localized action.");
                    Require(view.CanPrioritizeTask && view.CanRelocateSource, "An idle missing source lacked its recovery actions.");
                    model.State = ConversionState.Running;
                    Require(!view.CanPrioritizeTask && !view.CanRelocateSource, "Running work allowed ordering or source replacement.");
                    model.State = ConversionState.Waiting;
                }
            }
            finally { LocalizationService.SetLanguage(language); }
        });
    }

    private MediaTask QueueExperienceModel(string name)
    {
        var model = new MediaTask { InputPath = Path.Combine(_runRoot, name), Kind = MediaKind.Video,
            SourceFormatId = "mp4", TargetFormatId = "mp4", State = ConversionState.Waiting };
        model.AvailableFormats.Add("MP4");
        model.AvailableFormats.Add("MKV");
        return model;
    }
}
