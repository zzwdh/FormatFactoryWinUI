using System.Collections.Specialized;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI.Views;

public sealed partial class ArchiveConversionView
{
    private Task? _restoreTask;
    private bool _usesPersistence;

    private void AttachSession()
    {
        _session.Changed += SessionChanged;
        _session.Status += SessionStatus;
        _session.Tasks.CollectionChanged += SessionTasksChanged;
        foreach (var task in _session.Tasks) _tasks.Add(new(task));
    }

    private void DetachSession()
    {
        _session.Changed -= SessionChanged;
        _session.Status -= SessionStatus;
        _session.Tasks.CollectionChanged -= SessionTasksChanged;
        foreach (var task in Tasks) task.Dispose();
        _tasks.Clear();
    }

    private void SessionTasksChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var task in Tasks) task.Dispose();
            _tasks.Clear();
            foreach (var task in _session.Tasks) _tasks.Add(new(task));
            return;
        }
        if (args.OldItems is not null)
            foreach (ArchiveTask model in args.OldItems)
            {
                var task = Tasks.FirstOrDefault(item => ReferenceEquals(item.Model, model));
                if (task is null) continue;
                task.Dispose();
                _tasks.Remove(task);
            }
        if (args.NewItems is not null)
        {
            var index = args.NewStartingIndex;
            foreach (ArchiveTask model in args.NewItems)
                _tasks.Insert(index < 0 ? Tasks.Count : index++, new(model));
        }
    }

    private void SessionChanged()
    {
        if (_disposed) return;
        RefreshControls();
    }

    private void SessionStatus(string key, ArchiveSessionNoticeKind kind) => ShowStatus(T(key), kind switch
    {
        ArchiveSessionNoticeKind.Information => InfoBarSeverity.Informational,
        ArchiveSessionNoticeKind.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Error
    });

    private Task DispatchAsync(Func<Task> action)
    {
        if (_dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new InvalidOperationException("Archive session dispatcher unavailable."));
        return completion.Task;
    }

    internal Task RestoreAsync(CancellationToken token = default) =>
        _restoreTask ??= RestoreSessionAsync(token);

    private async Task RestoreSessionAsync(CancellationToken token)
    {
        await _session.RestoreAsync(token);
        if (!_disposed && _usesPersistence && _session.CanPersist)
            OutputFolderBox.Text = _session.OutputDirectory;
    }

    internal async Task FlushAsync()
    {
        await RestoreAsync();
        // TextChanged can be queued after a programmatic Text update. Read the control
        // at the operation boundary instead of overwriting it during ordinary refreshes.
        _session.OutputDirectory = OutputFolderBox.Text;
        await _session.FlushAsync();
    }
    private void QueueCheckpoint() => _session.QueueCheckpoint();
    internal bool AddRestoredInputs(IEnumerable<string> paths, ArchiveConversionOptions options, bool encrypted) =>
        _session.AddRestoredInputs(paths, options, encrypted);

    private string FormatBatchSummary()
    {
        var tasks = _session.LastBatch.Where(_session.Tasks.Contains).Select(task => new MediaTask
        {
            Id = task.Id,
            State = task.State switch
            {
                ArchiveTaskState.Succeeded => ConversionState.Completed,
                ArchiveTaskState.Failed => ConversionState.Failed,
                ArchiveTaskState.Cancelled => ConversionState.Cancelled,
                _ => ConversionState.Waiting
            },
            ResultStatistics = task.ResultStatistics
        });
        var summary = ConversionResultSummaryFormatter.FormatBatch(tasks);
        return string.IsNullOrEmpty(summary) ? string.Empty : T("ArchiveLastBatch") + " " + summary;
    }
}
