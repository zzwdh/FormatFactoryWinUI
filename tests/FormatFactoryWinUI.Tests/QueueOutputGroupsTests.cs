using System.Collections.Specialized;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueOutputGroupsTests
{
    [Fact]
    public void GroupFollowsItsFirstWaitingOutputAndPreservesRowOrder()
    {
        var groupId = Guid.NewGuid();
        using var running = Row(groupId, ConversionState.Running);
        using var before = Row();
        using var waiting = Row(groupId);
        using var after = Row();
        var projection = new QueueOutputGroups();
        MediaTaskViewModel[] rows = [running, before, waiting, after];
        projection.Synchronize(rows);
        Assert.Equal([before, running, waiting, after], projection.SelectMany(segment => segment));
        var group = Assert.IsType<OutputTaskGroupViewModel>(projection.FindOutputGroup(groupId));
        group.IsExpanded = false;

        waiting.State = ConversionState.Running;
        projection.Synchronize(rows);

        Assert.Same(group, projection[0]);
        Assert.False(waiting.OutputGroupExpanded);
        Assert.Equal([before, after], projection[1].ToArray());
        projection.ClearProjection();
        Assert.True(waiting.OutputGroupExpanded);
    }

    [Fact]
    public void RebindingAnEquivalentIdTransfersExpansionAndSubscriptionsToTheNewRow()
    {
        using var original = Row(Guid.NewGuid());
        using var replacement = new MediaTaskViewModel(new MediaTask { Id = original.Id, OutputGroupId = original.Model.OutputGroupId });
        var projection = new QueueOutputGroups();
        projection.Synchronize([original]);
        var group = Assert.Single(projection);
        group.IsExpanded = false;

        projection.Synchronize([replacement]);

        Assert.Same(replacement, Assert.Single(group));
        Assert.True(original.OutputGroupExpanded);
        Assert.False(replacement.OutputGroupExpanded);
        var notifications = 0;
        ((System.ComponentModel.INotifyPropertyChanged)group).PropertyChanged += (_, _) => notifications++;
        original.Progress = 20;
        Assert.Equal(0, notifications);
        replacement.Progress = 30;
        Assert.True(notifications > 0);
        projection.ClearProjection();
    }

    [Fact]
    public void AnOrdinaryRowCanHaveTheSameIdAsAGroupWhenAppendedOrRebuilt()
    {
        using var ordinary = Row();
        using var grouped = Row(ordinary.Id);
        using var next = Row();
        var projection = new QueueOutputGroups();
        projection.Synchronize([grouped]);
        Assert.True(projection.TryApplyIncrementalChange(new(NotifyCollectionChangedAction.Add, ordinary, 1), 2));
        Assert.True(projection.TryApplyIncrementalChange(new(NotifyCollectionChangedAction.Add, next, 2), 3));
        var outputGroup = projection[0];
        outputGroup.IsExpanded = false;

        projection.Synchronize([grouped, ordinary, next]);

        Assert.Same(outputGroup, projection.FindOutputGroup(ordinary.Id));
        Assert.Equal([ordinary, next], projection[1].ToArray());
        Assert.True(ordinary.OutputGroupExpanded);
        Assert.False(grouped.OutputGroupExpanded);
        projection.ClearProjection();
    }

    private static MediaTaskViewModel Row(Guid? groupId = null, ConversionState state = ConversionState.Waiting) =>
        new(new MediaTask { OutputGroupId = groupId, State = state });
}
