using System.Collections.ObjectModel;
using System.Collections.Specialized;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class PresentationBatchingTests
{
    [Fact]
    public void ProgressBurst_SchedulesOnceAndRetainsLatestPerTask()
    {
        var buffer = new LatestValueBuffer<int, int>();
        Assert.True(buffer.Publish(1, 1));
        for (var i = 2; i <= 1000; i++) Assert.False(buffer.Publish(1, i));
        Assert.False(buffer.Publish(2, 7));
        var values = buffer.Drain().ToDictionary(item => item.Key, item => item.Value);
        Assert.Equal(2, values.Count);
        Assert.Equal(1000, values[1]);
        Assert.Equal(7, values[2]);
        Assert.True(buffer.Publish(1, 1001));
    }

    [Fact]
    public void RemovedTask_DoesNotLeavePendingPresentation()
    {
        var buffer = new LatestValueBuffer<int, string>();
        buffer.Publish(1, "old progress");
        buffer.Remove(1);
        Assert.Empty(buffer.Drain());
        Assert.True(buffer.Publish(1, "new run"));
        Assert.Equal("new run", Assert.Single(buffer.Drain()).Value);
    }

    [Fact]
    public async Task ConcurrentTasks_DoNotLoseTheirFinalValues()
    {
        var buffer = new LatestValueBuffer<int, int>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(id => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) buffer.Publish(id, i);
        })));
        var items = buffer.Drain();
        Assert.Equal(4, items.Length);
        Assert.All(items, item => Assert.Equal(999, item.Value));
    }

    [Fact]
    public void UnchangedCollection_DoesNotNotifyOrReplaceExistingItems()
    {
        var first = new Row(1, "first");
        var second = new Row(2, "second");
        var target = new ObservableCollection<Row> { first, second };
        var notifications = 0;
        target.CollectionChanged += (_, _) => notifications++;
        CollectionSynchronizer.Update(target, new[] { first, second }, item => item.Id);
        Assert.Equal(0, notifications);
        Assert.Same(first, target[0]);
    }

    [Fact]
    public void FilteringAndReordering_KeepRetainedItemsWithoutReset()
    {
        var first = new Row(1, "first");
        var second = new Row(2, "second");
        var third = new Row(3, "third");
        var target = new ObservableCollection<Row> { first, second, third };
        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => actions.Add(e.Action);
        CollectionSynchronizer.Update(target, new[] { third, first, new Row(4, "new") }, item => item.Id);
        Assert.Equal(new[] { 3, 1, 4 }, target.Select(item => item.Id));
        Assert.Same(third, target[0]);
        Assert.Same(first, target[1]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    private sealed record Row(int Id, string Text);

    [Fact]
    public void ExpandingLargeFilter_KeepsIdentityWithoutScanningEveryMissingRowSuffix()
    {
        var source = Enumerable.Range(0, 10000).Select(id => new Row(id, id.ToString())).ToArray();
        var target = new ObservableCollection<Row>(source.Where(row => row.Id % 2 == 0));
        var calls = 0;
        var reset = false;
        target.CollectionChanged += (_, args) => reset |= args.Action == NotifyCollectionChangedAction.Reset;
        CollectionSynchronizer.Update(target, source, row => { calls++; return row.Id; });
        Assert.Equal(source.Length, target.Count);
        Assert.All(Enumerable.Range(0, source.Length), index => Assert.Same(source[index], target[index]));
        Assert.False(reset);
        Assert.True(calls <= source.Length * 4, $"Expanding a stable filter performed {calls} key lookups.");
    }
}
