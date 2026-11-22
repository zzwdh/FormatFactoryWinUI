using System.Collections.ObjectModel;
using System.Collections.Specialized;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ViewModels;

/// <summary>Maintains the grouped projection of the visible queue, including row expansion and group identity.</summary>
internal sealed class QueueOutputGroups : ObservableCollection<OutputTaskGroupViewModel>
{
    private const int ResetThreshold = 32;
    private readonly Dictionary<(Guid Id, bool Grouped), OutputTaskGroupViewModel> _byKey = [];

    public OutputTaskGroupViewModel? FindOutputGroup(Guid id) => _byKey.GetValueOrDefault((id, true));

    public void ClearProjection()
    {
        if (Count == 0) return;
        var retired = this.ToArray();
        Clear();
        _byKey.Clear();
        foreach (var group in retired) group.Dispose();
    }

    // The flat list observes its own edits. In a mixed queue an ordinary append only affects the last segment.
    public bool TryApplyIncrementalChange(NotifyCollectionChangedEventArgs change, int rowCount)
    {
        if (change.Action == NotifyCollectionChangedAction.Reset ||
            change.NewItems?.Cast<MediaTaskViewModel>().Any(task => task.Model.OutputGroupId.HasValue) == true)
            return false;
        if (Count == 0) return true;
        if (change.Action != NotifyCollectionChangedAction.Add || change.NewItems is not { Count: > 0 } items ||
            change.NewStartingIndex + items.Count != rowCount) return false;

        var segment = this[^1];
        if (segment.IsGrouped)
        {
            var first = (MediaTaskViewModel)items[0]!;
            segment = new(first.Id, grouped: false);
            _byKey.Add((first.Id, false), segment);
            Add(segment);
        }
        foreach (var row in items.Cast<MediaTaskViewModel>()) segment.Add(row);
        return true;
    }

    public void Synchronize(IReadOnlyList<MediaTaskViewModel> rows)
    {
        var plans = PlanSegments(rows);
        var wanted = plans.Select(plan => plan.Key).ToHashSet();
        var retired = this.Where(group => !wanted.Contains(KeyOf(group))).ToArray();
        if (retired.Length > ResetThreshold)
            ReplaceAll(this.Where(group => wanted.Contains(KeyOf(group))).ToArray());
        else
            foreach (var group in retired) Remove(group);

        // First detach retired groups and moved rows, then let their new groups set expansion.
        foreach (var group in retired)
        {
            _byKey.Remove(KeyOf(group));
            group.Dispose();
        }
        foreach (var plan in plans)
        {
            if (!_byKey.TryGetValue(plan.Key, out var group)) continue;
            var members = plan.Rows.ToHashSet();
            for (var index = group.Count - 1; index >= 0; index--)
                if (!members.Contains(group[index])) group.RemoveAt(index);
        }

        var desired = new List<OutputTaskGroupViewModel>(plans.Count);
        foreach (var plan in plans)
        {
            if (!_byKey.TryGetValue(plan.Key, out var group))
                _byKey.Add(plan.Key, group = new(plan.Key.Id, plan.Key.Grouped));
            CollectionSynchronizer.Update(group, plan.Rows, row => row.Id);
            group.RefreshLocalization();
            desired.Add(group);
        }
        var changes = Math.Abs(Count - desired.Count) + this.Zip(desired).Count(pair => !ReferenceEquals(pair.First, pair.Second));
        if (changes > ResetThreshold) ReplaceAll(desired);
        else CollectionSynchronizer.Update(this, desired, KeyOf);
    }

    private static (Guid Id, bool Grouped) KeyOf(OutputTaskGroupViewModel group) => (group.Id, group.IsGrouped);

    private static List<SegmentPlan> PlanSegments(IReadOnlyList<MediaTaskViewModel> rows)
    {
        // A multi-output group occupies the position of its first waiting row, or its first row when all have started.
        var groups = new Dictionary<Guid, SegmentPlan>();
        foreach (var row in rows)
        {
            if (row.Model.OutputGroupId is not { } id) continue;
            if (!groups.TryGetValue(id, out var plan)) groups.Add(id, plan = new((id, true)));
            plan.Rows.Add(row);
            if (row.State == ConversionState.Waiting) plan.FirstWaiting ??= row;
        }
        var segments = new List<SegmentPlan>();
        if (groups.Count == 0) return segments;
        SegmentPlan? ordinary = null;
        foreach (var row in rows)
        {
            if (row.Model.OutputGroupId is { } id)
            {
                var plan = groups[id];
                if (!ReferenceEquals(row, plan.FirstWaiting ?? plan.Rows[0])) continue;
                segments.Add(plan);
                ordinary = null;
            }
            else
            {
                if (ordinary is null) segments.Add(ordinary = new((row.Id, false)));
                ordinary.Rows.Add(row);
            }
        }
        return segments;
    }

    private void ReplaceAll(IReadOnlyList<OutputTaskGroupViewModel> groups)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var group in groups) Items.Add(group);
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }

    private sealed class SegmentPlan((Guid Id, bool Grouped) key)
    {
        public (Guid Id, bool Grouped) Key { get; } = key;
        public List<MediaTaskViewModel> Rows { get; } = [];
        public MediaTaskViewModel? FirstWaiting { get; set; }
    }
}
