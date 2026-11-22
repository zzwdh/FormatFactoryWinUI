using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Motion;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private string _navigationTag = "all";
    private long _navigationVersion;
    private Task _navigationTransition = Task.CompletedTask;
    private bool _motionReady;
    private bool _suppressListMotion;
    private readonly Dictionary<string, (double List, double Page)> _queueScrollPositions = new();
    private readonly HashSet<Guid> _knownMotionTasks = [];
    private readonly Dictionary<Guid, (long Expires, int Delay)> _taskEntrances = new();
    private (int Active, int Completed)? _lastMotionSummary;
    private (string Tag, long Version)? _pendingScrollRestore;

    private void InitializeMotion()
    {
        FluentMotion.Initialize(DispatcherQueue);
        _motionReady = true;
        FluentMotion.PreferencesChanged += MotionPreferencesChanged;
        Activated += MotionWindowActivated;
        ConverterView.TaskList.ContainerContentChanging += TaskList_ContainerContentChanging;
        ApplyMotionPreferences();
    }

    private void MotionWindowActivated(object sender, WindowActivatedEventArgs args) =>
        FluentMotion.SetWindowActive(args.WindowActivationState != WindowActivationState.Deactivated);

    private void MotionPreferencesChanged(object? sender, EventArgs args)
    {
        if (!_windowClosed) ApplyMotionPreferences();
    }

    private void ApplyMotionPreferences()
    {
        // Reposition retained, realized containers. Entry is separately limited to
        // three genuinely new tasks; filtering and recycling never replay it.
        ConverterView.TaskList.ItemContainerTransitions = FluentMotion.IsEnabled
            ? new TransitionCollection { new RepositionThemeTransition() }
            : new TransitionCollection();
        if (!FluentMotion.IsEnabled) _taskEntrances.Clear();
    }

    private void NavigateWithMotion(string tag)
    {
        if (_windowClosed || ConverterView is null || ConverterView.TaskContentHost is null) return;
        _navigationTransition = NavigateWithMotionAsync(tag);
    }

    private async Task NavigateWithMotionAsync(string tag)
    {
        var version = ++_navigationVersion;
        var previous = _navigationTag;
        var fromConverter = previous is not ("settings" or "history" or "archive" or "automation");
        var toConverter = tag is not ("settings" or "history" or "archive" or "automation");
        if (fromConverter) SaveQueueScrollPosition(previous);
        ResetDropFeedback();
        try
        {
            // Top-level destinations share a short fade. Keeping the page stationary
            // preserves its spatial anchor while the native navigation indicator moves.
            var outgoing = fromConverter && toConverter
                ? ConverterView.TaskContentHost : GetNavigationPage(previous);
            if (_motionReady && !_initializing && previous != tag &&
                outgoing.IsLoaded && FluentMotion.IsEnabled)
            {
                await FluentMotion.FadeOutAsync(outgoing);
                if (_windowClosed || version != _navigationVersion) return;
            }

            if (tag == "settings") EnsureSettingsViewLoaded();
            if (_windowClosed || version != _navigationVersion) return;
            _navigationTag = tag;
            if (previous != tag) TrackTelemetryPage(tag);
            ResetNavigationMotion();
            SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
            HistoryView.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
            ArchivePage.Visibility = tag == "archive" ? Visibility.Visible : Visibility.Collapsed;
            AutomationView.Visibility = tag == "automation" ? Visibility.Visible : Visibility.Collapsed;
            ConverterView.Visibility = toConverter ? Visibility.Visible : Visibility.Collapsed;

            if (toConverter)
            {
                _filter = tag;
                ConverterView.AddOpticalDiscMenuItem.Visibility = tag == "disc"
                    ? Visibility.Visible : Visibility.Collapsed;
                _suppressListMotion = true;
                try { RefreshVisibleTasks(); }
                finally { _suppressListMotion = false; }
                RestoreQueueScrollPosition(tag, version);
            }
            else if (tag == "history")
            {
                _ = RefreshHistoryAsync(HistoryView.HistorySearchBox.Text);
            }

            if (_motionReady && !_initializing && previous != tag)
            {
                if (fromConverter && toConverter)
                    FluentMotion.FadeIn(ConverterView.TaskContentHost);
                else
                    FluentMotion.FadeIn(GetNavigationPage(tag));
            }
        }
        catch (Exception ex)
        {
            // Motion must never prevent navigation or leave an invisible hit target.
            System.Diagnostics.Debug.WriteLine(ex);
            if (version == _navigationVersion && !_windowClosed)
            {
                ResetNavigationMotion();
                _navigationTag = tag;
                SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
                HistoryView.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
                ArchivePage.Visibility = tag == "archive" ? Visibility.Visible : Visibility.Collapsed;
                AutomationView.Visibility = tag == "automation" ? Visibility.Visible : Visibility.Collapsed;
                ConverterView.Visibility = toConverter ? Visibility.Visible : Visibility.Collapsed;
                if (toConverter)
                {
                    _filter = tag;
                    RefreshVisibleTasks();
                }
            }
        }
    }

    private FrameworkElement GetNavigationPage(string tag) => tag switch
    {
        "settings" => SettingsView,
        "history" => HistoryView,
        "archive" => ArchivePage,
        "automation" => AutomationView,
        _ => ConverterView
    };

    private void ResetNavigationMotion()
    {
        FluentMotion.Reset(ConverterView.TaskContentHost);
        foreach (var page in new FrameworkElement[] { ConverterView, SettingsView, HistoryView, ArchivePage, AutomationView })
            FluentMotion.Reset(page);
    }

    private void SaveQueueScrollPosition(string tag)
    {
        if (_pendingScrollRestore is { } pending && pending.Tag == tag) return;
        var listScroller = FindVisualDescendant<ScrollViewer>(ConverterView.TaskList);
        _queueScrollPositions[tag] = (listScroller?.VerticalOffset ?? 0, ConverterView.ConverterScrollViewer.VerticalOffset);
    }

    private void RestoreQueueScrollPosition(string tag, long version)
    {
        _queueScrollPositions.TryGetValue(tag, out var position);
        _pendingScrollRestore = (tag, version);
        // The list's extent is updated after the filtered collection changes.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_windowClosed || version != _navigationVersion || _navigationTag != tag) return;
            ConverterView.TaskList.UpdateLayout();
            FindVisualDescendant<ScrollViewer>(ConverterView.TaskList)?.ChangeView(null, position.List, null, true);
            ConverterView.ConverterScrollViewer.ChangeView(null, position.Page, null, true);
            _pendingScrollRestore = null;
        });
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void PrepareTaskCollectionMotion()
    {
        var now = Environment.TickCount64;
        foreach (var id in _taskEntrances.Where(item => item.Value.Expires < now).Select(item => item.Key).ToArray())
            _taskEntrances.Remove(id);
        var ids = Tasks.Select(task => task.Id).ToHashSet();
        if (_motionReady && !_initializing && !_suppressListMotion && FluentMotion.IsEnabled &&
            ConverterView.Visibility == Visibility.Visible && ConverterView.TaskList.Visibility == Visibility.Visible && ConverterView.TaskContentHost.IsLoaded)
        {
            var newTasks = Tasks.Where(task => !_knownMotionTasks.Contains(task.Id) && MatchesFilter(task)).Take(3);
            var delay = 0;
            foreach (var task in newTasks)
            {
                _taskEntrances[task.Id] = (now + 500, delay);
                delay += 35;
            }
        }
        _knownMotionTasks.IntersectWith(ids);
        _knownMotionTasks.UnionWith(ids);
        foreach (var id in _taskEntrances.Keys.Where(id => !ids.Contains(id)).ToArray()) _taskEntrances.Remove(id);
    }

    private void TaskList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not { } container) return;
        FluentMotion.Reset(container);
        if (args.InRecycleQueue || args.Item is not MediaTaskViewModel task) return;
        if (!_taskEntrances.Remove(task.Id, out var entry) || entry.Expires < Environment.TickCount64) return;
        var id = task.Id;
        args.RegisterUpdateCallback((_, ready) =>
        {
            if (!_windowClosed && !_suppressListMotion && !_initializing &&
                ready.Item is MediaTaskViewModel current && current.Id == id &&
                !ready.InRecycleQueue && ConverterView.Visibility == Visibility.Visible)
            {
                var itemContainer = ready.ItemContainer;
                // The content callback can precede the container's Loaded event.
                // One layout turn gives its visual a surface without replaying on scroll.
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (!_windowClosed && itemContainer.Content is MediaTaskViewModel visible &&
                        visible.Id == id && _navigationTag == _filter &&
                        ConverterView.Visibility == Visibility.Visible && entry.Expires >= Environment.TickCount64)
                        FluentMotion.Entrance(itemContainer, FluentMotion.ContentDurationMs, 8, entry.Delay);
                });
            }
        });
    }

    private void UpdateTaskContentVisibility(bool hasTasks)
    {
        var changed = ConverterView.TaskList.Visibility != (hasTasks ? Visibility.Visible : Visibility.Collapsed);
        ConverterView.TaskList.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
        ConverterView.EmptyState.Visibility = hasTasks ? Visibility.Collapsed : Visibility.Visible;
        if (changed && _motionReady && !_initializing && !_suppressListMotion &&
            ConverterView.Visibility == Visibility.Visible)
        {
            if (hasTasks) _taskEntrances.Clear();
            FluentMotion.Entrance(hasTasks ? ConverterView.TaskList : ConverterView.EmptyState, FluentMotion.ContentDurationMs, 6);
        }
    }

    private void AnimateQueueSummary(int active, int completed)
    {
        if (_lastMotionSummary is { } prior && prior != (active, completed) &&
            _motionReady && !_initializing && ConverterView.Visibility == Visibility.Visible)
            FluentMotion.Pulse(ConverterView.QueueSummaryText);
        _lastMotionSummary = (active, completed);
    }

    private void AnimateInfoBar(InfoBar infoBar, bool wasOpen)
    {
        if (!wasOpen && _motionReady && !_initializing) FluentMotion.Entrance(infoBar, FluentMotion.ContentDurationMs, 4);
    }

    private long _dropMotionVersion;
    private bool _dropFeedbackVisible;

    private void ShowDropFeedback()
    {
        if (_dropFeedbackVisible) return;
        _dropFeedbackVisible = true;
        ++_dropMotionVersion;
        FluentMotion.Reset(DropFeedbackOverlay);
        DropFeedbackOverlay.Visibility = Visibility.Visible;
        if (_motionReady)
        {
            FluentMotion.FadeIn(DropFeedbackOverlay, FluentMotion.FastDurationMs);
            FluentMotion.Entrance(DropFeedbackCard, FluentMotion.ContentDurationMs, 4);
        }
    }

    private async Task HideDropFeedbackAsync()
    {
        if (!_dropFeedbackVisible && DropFeedbackOverlay.Visibility == Visibility.Collapsed) return;
        _dropFeedbackVisible = false;
        var version = ++_dropMotionVersion;
        if (_motionReady && !_windowClosed)
            await FluentMotion.FadeOutAsync(DropFeedbackOverlay);
        if (version != _dropMotionVersion || _dropFeedbackVisible) return;
        DropFeedbackOverlay.Visibility = Visibility.Collapsed;
        FluentMotion.Reset(DropFeedbackOverlay);
        FluentMotion.Reset(DropFeedbackCard);
    }

    private void UpdateHistoryContentVisibility() => HistoryView.UpdateContentVisibility();

    private void DisposeMotion()
    {
        ++_navigationVersion;
        _taskEntrances.Clear();
        FluentMotion.PreferencesChanged -= MotionPreferencesChanged;
        Activated -= MotionWindowActivated;
        ConverterView.TaskList.ContainerContentChanging -= TaskList_ContainerContentChanging;
        FluentMotion.Shutdown();
    }
}
