using System.Collections.ObjectModel;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ViewModels;

public sealed record QueueSummaryPresentation(int Total, int Active, int Completed, int Waiting, int StopCount,
    string Summary, string Details, string ResultSummary, string EmptyTitle, string EmptyMessage)
{
    public bool HasTasks => Total > 0;
    public bool CanClearPending => Waiting > 0;
    public bool CanStop => StopCount > 0;
}

public sealed record QueueTaskbarPresentation(
    (double Progress, bool Active, bool Paused, bool Failed)[] Activities, bool HasOutput, int StopCount);

public sealed record QueueBatchPresentation(string Message, string? OutputPath, int FailedCount);

/// <summary>
/// Owns queue presentation state. Collection operations and draining run on the host
/// context; progress publication is thread safe and never mutates bound objects.
/// The host owns the 200 ms timer, animations, dialogs, and native notifications.
/// </summary>
public sealed class QueuePageViewModel : IDisposable
{
    private readonly Func<Guid, bool> _isActive;
    private readonly HashSet<Guid> _selected = [];
    private readonly Dictionary<Guid, MediaTask> _batchResults = [];
    private readonly object _progressGate = new();
    private readonly Dictionary<Guid, (MediaTaskViewModel Task, long Generation)> _generations = [];
    private readonly Dictionary<Guid, ProgressSnapshot> _pending = [];
    private readonly OutputAvailabilityCache _outputAvailability = new();
    private long _nextGeneration;
    private bool _progressScheduled;
    private bool _disposed;

    public QueuePageViewModel(Func<Guid, bool>? isActive = null) => _isActive = isActive ?? (_ => false);

    public IdIndexedCollection<MediaTaskViewModel> Tasks { get; } = new(task => task.Id);
    public ObservableCollection<MediaTaskViewModel> VisibleTasks { get; } = [];
    public string Filter { get; set; } = "all";
    public string EmptyTitle => Tasks.Count == 0 ? LocalizationService.T("EmptyTitle") : LocalizationService.T("EmptyFiltered");
    public string EmptyMessage => Tasks.Count == 0 ? LocalizationService.T("ConversionUiDropAnywhereHint") : LocalizationService.T("EmptyFilteredHint");
    public MediaTaskViewModel[] SelectedTasks => _selected.Select(Tasks.FindById).OfType<MediaTaskViewModel>().Where(MatchesFilter).ToArray();
    public Guid[] SelectedTaskIds => SelectedTasks.Select(task => task.Id).ToArray();
    public string SelectionSummary
    {
        get
        {
            var count = SelectedTasks.Length;
            return FormatSelectionSummary(count);
        }
    }

    public MediaTaskViewModel AddTask(MediaTask model)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Tasks.FindById(model.Id) is { } existing) return existing;
        var row = new MediaTaskViewModel(model);
        Tasks.Add(row);
        return row;
    }

    public bool RemoveTask(MediaTaskViewModel task)
    {
        if (!Tasks.ContainsReference(task)) return false;
        ForgetProgress(task.Id);
        _outputAvailability.Invalidate(task.Id);
        _selected.Remove(task.Id);
        Tasks.Remove(task);
        task.Dispose();
        return true;
    }

    public bool MatchesFilter(MediaTaskViewModel task) => Filter switch
    {
        "video" => task.Kind == MediaKind.Video,
        "audio" => task.Kind == MediaKind.Audio,
        "image" => task.Kind == MediaKind.Image,
        "document" => task.Kind == MediaKind.Document,
        "ebook" => task.Kind == MediaKind.Ebook,
        "disc" => task.Kind == MediaKind.Disc,
        _ => true
    };

    public void RefreshVisibleTasks()
    {
        CollectionSynchronizer.Update(VisibleTasks, Tasks.Where(MatchesFilter).ToArray(), task => task.Id);
        _selected.RemoveWhere(id => Tasks.FindById(id) is not { } task || !MatchesFilter(task));
    }

    public void SetSelection(IEnumerable<Guid> taskIds)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var selected = taskIds.Where(id => Tasks.FindById(id) is { } task && MatchesFilter(task)).ToHashSet();
        _selected.Clear();
        _selected.UnionWith(selected);
    }

    public MediaTaskViewModel[] SelectEditable(IEnumerable<Guid> editableIds)
    { SetSelection(editableIds); return SelectedTasks; }

    public static MediaTaskViewModel[] SelectEditableRows(IEnumerable<MediaTaskViewModel> tasks, Func<Guid, bool> isScheduled) =>
        tasks.Where(task => !task.Model.IsRecoveringOutput && QueueActionPolicy.CanBatchEdit(task.Model, isScheduled)).ToArray();

    public static string FormatSelectionSummary(int count) => count > 0 ? LocalizationService.T("QueueBatchSelected", count) : string.Empty;

    public QueueSummaryPresentation CreateSummary()
    {
        var counts = new Dictionary<ConversionState, int>();
        foreach (var task in Tasks) counts[task.State] = counts.GetValueOrDefault(task.State) + 1;
        var active = counts.GetValueOrDefault(ConversionState.Probing) + counts.GetValueOrDefault(ConversionState.Running) +
            counts.GetValueOrDefault(ConversionState.Paused);
        var completed = counts.GetValueOrDefault(ConversionState.Completed);
        var summary = LocalizationService.T("QueueSummaryCounts", Tasks.Count, active, completed);
        var result = ConversionResultSummaryFormatter.FormatBatch(Tasks.Select(task => task.Model));
        var labels = new (string Key, ConversionState State)[]
        {
            ("StateWaiting", ConversionState.Waiting), ("StateProbing", ConversionState.Probing),
            ("StateRunning", ConversionState.Running), ("StatePaused", ConversionState.Paused),
            ("StateCompleted", ConversionState.Completed), ("StateFailed", ConversionState.Failed),
            ("StateCancelled", ConversionState.Cancelled), ("ConversionUiStateBlocked", ConversionState.Blocked)
        };
        var stateDetails = string.Join(Environment.NewLine, labels.Select(item => LocalizationService.T(item.Key) + ": " +
            counts.GetValueOrDefault(item.State).ToString(LocalizationService.CurrentCulture)));
        var details = LocalizationService.T("QueueTitle") + Environment.NewLine + summary + Environment.NewLine +
            Environment.NewLine + stateDetails + (string.IsNullOrEmpty(result) ? string.Empty : Environment.NewLine + Environment.NewLine + result);
        var stopCount = QueueActionPolicy.SelectToStop(Tasks.Select(task => task.Model), _isActive).Length;
        return new(Tasks.Count, active, completed, counts.GetValueOrDefault(ConversionState.Waiting), stopCount,
            Tasks.Count == 0 ? string.Empty : summary, details, result,
            EmptyTitle, EmptyMessage);
    }

    public QueueTaskbarPresentation CreateTaskbarState()
    {
        var activities = Tasks.Where(task => task.State is ConversionState.Probing or ConversionState.Running or
                ConversionState.Paused or ConversionState.Failed)
            .Select(task => (task.Progress, task.State is ConversionState.Probing or ConversionState.Running,
                task.State == ConversionState.Paused, task.State == ConversionState.Failed)).ToArray();
        return new(activities, Tasks.Any(task => task.State == ConversionState.Completed && _outputAvailability.IsAvailable(task.Id, task.OutputPath)),
            QueueActionPolicy.SelectToStop(Tasks.Select(task => task.Model), _isActive).Length);
    }

    internal OutputAvailabilityCheck[] CaptureOutputAvailabilityChecks() => _outputAvailability.CaptureChecks(
        Tasks.Where(task => task.State == ConversionState.Completed).Select(task => new OutputAvailabilityTarget(task.Id, task.OutputPath)));

    internal void ApplyOutputAvailabilityResults(IEnumerable<OutputAvailabilityResult> results) => _outputAvailability.Apply(
        results.Where(result => Tasks.FindById(result.Check.TaskId) is { State: ConversionState.Completed } task &&
            string.Equals(task.OutputPath, result.Check.Path, StringComparison.OrdinalIgnoreCase)));

    internal void InvalidateOutputAvailability(Guid taskId) => _outputAvailability.Invalidate(taskId);

    internal void InvalidateOutputAvailability(string path)
    {
        foreach (var task in Tasks.Where(task => string.Equals(task.OutputPath, path, StringComparison.OrdinalIgnoreCase)))
            _outputAvailability.Invalidate(task.Id);
    }

    internal string? FindLastCachedOutput() => Tasks.LastOrDefault(task => task.State == ConversionState.Completed &&
        _outputAvailability.IsAvailable(task.Id, task.OutputPath))?.OutputPath;

    public string? FindLastOutput(Func<string, bool> pathExists) =>
        Tasks.LastOrDefault(task => task.State == ConversionState.Completed && pathExists(task.OutputPath))?.OutputPath;

    public void SynchronizeOrder(IEnumerable<Guid> modelOrder, bool refreshLocalization = false)
    {
        ArgumentNullException.ThrowIfNull(modelOrder);
        var positions = modelOrder.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        var ordered = Tasks.OrderBy(task => positions.GetValueOrDefault(task.Id, int.MaxValue)).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = Tasks.IndexOf(ordered[index]);
            if (current != index) Tasks.Move(current, index);
            if (refreshLocalization) ordered[index].RefreshLocalization();
        }
    }

    public long BeginProgress(MediaTaskViewModel task)
    {
        lock (_progressGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = ++_nextGeneration;
            _generations[task.Id] = (task, generation);
            _pending.Remove(task.Id);
            return generation;
        }
    }

    /// <returns>True once per pending drain, when the host should start its timer.</returns>
    public bool PublishProgress(MediaTaskViewModel task, long generation, double percent, string speed, string remaining, string message)
    {
        lock (_progressGate)
        {
            if (_disposed || !_generations.TryGetValue(task.Id, out var current) ||
                current.Generation != generation || !ReferenceEquals(current.Task, task)) return false;
            _pending[task.Id] = new(task, generation, percent, speed, remaining, message);
            if (_progressScheduled) return false;
            _progressScheduled = true;
            return true;
        }
    }

    public MediaTaskViewModel[] DrainProgress()
    {
        ProgressSnapshot[] updates;
        lock (_progressGate)
        {
            updates = _pending.Values.ToArray();
            _pending.Clear();
            _progressScheduled = false;
        }
        var changed = new List<MediaTaskViewModel>();
        foreach (var update in updates)
        {
            var task = update.Task;
            lock (_progressGate)
                if (_disposed || !_generations.TryGetValue(task.Id, out var current) || current.Generation != update.Generation ||
                    !ReferenceEquals(current.Task, task)) continue;
            if (task.State != ConversionState.Running || !Tasks.ContainsReference(task)) continue;
            task.Progress = update.Percent; task.Speed = update.Speed; task.Remaining = update.Remaining; task.Message = update.Message;
            changed.Add(task);
        }
        return changed.ToArray();
    }

    public void ForgetProgress(Guid taskId)
    { lock (_progressGate) { _generations.Remove(taskId); _pending.Remove(taskId); } }

    public void DiscardPendingProgress()
    { lock (_progressGate) { _pending.Clear(); _progressScheduled = false; } }

    public void ClearProgress()
    { lock (_progressGate) { _generations.Clear(); _pending.Clear(); _progressScheduled = false; } }

    public void TrackBatchTask(MediaTask task) => _batchResults[task.Id] = task;
    public void ForgetBatchTask(Guid taskId) => _batchResults.Remove(taskId);

    public QueueBatchPresentation CompleteBatch(Func<string, bool> pathExists)
    {
        var finished = _batchResults.Values.Where(task => task.State is ConversionState.Completed or ConversionState.Failed or ConversionState.Cancelled).ToArray();
        foreach (var id in _batchResults.Keys.Where(id => !_isActive(id)).ToArray()) _batchResults.Remove(id);
        return new(ConversionResultSummaryFormatter.FormatBatch(finished),
            finished.FirstOrDefault(task => task.State == ConversionState.Completed && pathExists(task.OutputPath))?.OutputPath,
            finished.Count(task => task.State == ConversionState.Failed));
    }

    public void Dispose()
    {
        lock (_progressGate) _disposed = true;
        ClearProgress();
        _selected.Clear();
        _batchResults.Clear();
        _outputAvailability.Clear();
        foreach (var task in Tasks) task.Dispose();
    }

    private sealed record ProgressSnapshot(MediaTaskViewModel Task, long Generation, double Percent,
        string Speed, string Remaining, string Message);
}
