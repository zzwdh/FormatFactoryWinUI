using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FormatFactoryWinUI.Views;

public sealed partial class QueueView
{
    private ScrollViewer? _queueScroller;
    private object? _pendingQueueEndItem;
    private bool _queueEndAnchorApplied;
    private DispatcherQueueTimer? _queueEndTimer;
    private double _queueEndExtent;
    private int _queueEndStableTicks;
    private int _queueEndTicks;

    private void InitializeQueueScrollBoundary()
    {
        TaskList.Loaded += QueueScrollLoaded;
        TaskList.Unloaded += QueueScrollUnloaded;
        TaskList.AddHandler(PointerPressedEvent, new PointerEventHandler(QueueScrollPointerInput), true);
        TaskList.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(QueueScrollPointerInput), true);
        TaskList.AddHandler(KeyDownEvent, new KeyEventHandler(QueueScrollKeyboardInput), true);
    }

    private void QueueScrollLoaded(object sender, RoutedEventArgs args)
    {
        QueueScrollUnloaded(sender, args);
        EnsureQueueScrollViewer();
    }

    private void EnsureQueueScrollViewer()
    {
        if (_queueScroller is not null) return;
        _queueScroller = FindQueueScrollViewer(TaskList);
        if (_queueScroller is null) return;
        _queueScroller.ViewChanging += QueueScrollViewChanging;
        _queueScroller.ViewChanged += QueueScrollViewChanged;
    }

    private void QueueScrollUnloaded(object sender, RoutedEventArgs args)
    {
        CancelQueueEndScroll();
        if (_queueEndTimer is not null) _queueEndTimer.Tick -= QueueEndTimerTick;
        _queueEndTimer = null;
        if (_queueScroller is null) return;
        _queueScroller.ViewChanging -= QueueScrollViewChanging;
        _queueScroller.ViewChanged -= QueueScrollViewChanged;
        _queueScroller = null;
    }

    private void CancelQueueEndScroll()
    {
        _pendingQueueEndItem = null;
        _queueEndTimer?.Stop();
        _queueEndAnchorApplied = false;
    }

    private void QueueScrollPointerInput(object sender, PointerRoutedEventArgs args) => CancelQueueEndScroll();
    private void QueueScrollKeyboardInput(object sender, KeyRoutedEventArgs args) => CancelQueueEndScroll();

    private void QueueScrollViewChanging(object? sender, ScrollViewerViewChangingEventArgs args)
    {
        if (_queueScroller is not { } scroller) return;
        var destination = args.FinalView.VerticalOffset;
        // After a long jump, WinUI changes its offset backwards by part of a
        // viewport while measuring variable-height rows. Raw user input already
        // cancels the request. Preserve it through those small layout adjustments,
        // while respecting a subsequent absolute jump back up the list.
        if (destination < scroller.VerticalOffset - 1 &&
            (_pendingQueueEndItem is null || destination <= 1 || scroller.VerticalOffset - destination >= scroller.ViewportHeight))
        {
            CancelQueueEndScroll();
            return;
        }
        if (_queueEndAnchorApplied || TaskList.Items.Count == 0 ||
            destination <= scroller.VerticalOffset + 1 ||
            destination < scroller.ScrollableHeight - 1) return;
        // Pixel offsets use the virtualizing panel's current height estimate. After
        // a long jump, newly measured variable-height cards can increase the extent,
        // leaving a request for 100% around the middle. Remember this one explicit
        // endpoint request, then let ListView anchor the actual last data item.
        _pendingQueueEndItem = TaskList.Items[TaskList.Items.Count - 1];
    }

    private void QueueScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (args.IsIntermediate || _pendingQueueEndItem is null || _queueScroller is null) return;
        if (_queueEndTimer is null)
        {
            _queueEndTimer = DispatcherQueue.CreateTimer();
            _queueEndTimer.Interval = TimeSpan.FromMilliseconds(50);
            _queueEndTimer.Tick += QueueEndTimerTick;
        }
        if (_queueEndTimer.IsRunning) return;
        _queueEndExtent = _queueScroller.ExtentHeight;
        _queueEndStableTicks = _queueEndTicks = 0;
        _queueEndTimer.Start();
    }

    private void QueueEndTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_queueScroller is not { } scroller || _pendingQueueEndItem is not { } lastItem ||
            Visibility != Visibility.Visible || !TaskList.IsLoaded || TaskList.Items.Count == 0 ||
            !ReferenceEquals(TaskList.Items[TaskList.Items.Count - 1], lastItem))
        {
            CancelQueueEndScroll();
            return;
        }
        // Let the cards reached by the pixel jump finish measuring before the
        // single item anchor. Otherwise their later height estimates can displace it.
        _queueEndStableTicks = Math.Abs(scroller.ExtentHeight - _queueEndExtent) < 1 ? _queueEndStableTicks + 1 : 0;
        _queueEndExtent = scroller.ExtentHeight;
        ++_queueEndTicks;
        if (!_queueEndAnchorApplied)
        {
            if (_queueEndTicks < 10 && _queueEndStableTicks < 2) return;
            _queueEndAnchorApplied = true;
            _queueEndTicks = _queueEndStableTicks = 0;
            TaskList.ScrollIntoView(lastItem, ScrollIntoViewAlignment.Default);
            return;
        }

        // Realizing the final cards can change their heights again after the
        // item anchor. Check the measured row while that layout settles. A
        // collapsed group's hidden children have no row bounds to correct.
        if (_queueEndStableTicks >= 2 && scroller.ScrollableHeight - scroller.VerticalOffset > 1 &&
            TaskList.ContainerFromItem(lastItem) is FrameworkElement { Visibility: Visibility.Visible, ActualHeight: > 0 } row)
        {
            var bounds = row.TransformToVisual(scroller).TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
            if (bounds.Bottom > scroller.ViewportHeight + 1)
                scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
        }
        if (_queueEndTicks < 8) return;
        sender.Stop();
        _pendingQueueEndItem = null;
    }

    private static ScrollViewer? FindQueueScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroller) return scroller;
            if (FindQueueScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
