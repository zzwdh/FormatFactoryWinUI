using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

public sealed record SourceRelocationResult(int UpdatedTasks, Exception? SaveError = null);

public sealed partial class QueueSession
{
    public bool CanRelocateSource(MediaTask task) => !_stopping && !_disposed && CanSave && Tasks.ContainsReference(task) &&
        !IsBusy(task.Id) && !task.IsRecoveringOutput &&
        task.State is ConversionState.Waiting or ConversionState.Blocked or ConversionState.Failed or ConversionState.Cancelled &&
        SourceRelocationService.IsManualTask(task) && SourceRelocationService.HasMissingInputs(task);

    /// <summary>
    /// Reprobes through a separate importer, then replaces sources in place. All unfinished members of an output
    /// group are prepared before publishing any change; successful output members and automation sources are untouched.
    /// The host transfers reservations only after validation and always releases temporary imports.
    /// </summary>
    public async Task<SourceRelocationResult> RelocateSourcesAsync(MediaTask task,
        IReadOnlyDictionary<string, string> replacements,
        Func<PersistedQueueItem, CancellationToken, Task<MediaImportItemResult>> import,
        Action<MediaTask> releaseTemporary, Action<MediaTask>? rememberCommitted = null,
        bool requireKnownSize = true, CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var token = cancellation.Token;
        var members = new List<(MediaTask Original, PersistedQueueItem Request, string[] OriginalPaths)>();
        var prepared = new List<MediaTask>();
        MediaTask[] originalGroup = [];
        Guid? outputGroupId = null;
        try
        {
            await _dispatch(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!CanRelocateSource(task)) throw new InvalidOperationException(LocalizationService.T("SourceRelocationUnavailable"));
                outputGroupId = task.OutputGroupId;
                originalGroup = outputGroupId is { } id ? GetOutputGroup(id) : [task];
                foreach (var member in originalGroup.Where(member => member.State != ConversionState.Completed))
                {
                    if (!CanRelocateSource(member)) throw new InvalidOperationException(LocalizationService.T("SourceRelocationUnavailable"));
                    var request = SourceRelocationService.CreateRequest(member, replacements, requireKnownSize);
                    members.Add((member, request, member.InputPaths.ToArray()));
                }
                foreach (var member in members) member.Original.IsRecoveringOutput = true;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            foreach (var member in members)
            {
                token.ThrowIfCancellationRequested();
                var result = await import(member.Request, token).ConfigureAwait(false);
                if (result.Task is { } imported) prepared.Add(imported);
                if (result.Error is { } error) throw error;
                if (result.Task is null) throw new InvalidOperationException(LocalizationService.T("SourceRelocationUnavailable"));
                SourceRelocationService.ValidateImported(member.Original, member.Request, result.Task);
            }
            await _dispatch(() =>
            {
                token.ThrowIfCancellationRequested();
                var currentGroup = outputGroupId is { } id ? GetOutputGroup(id) : [task];
                if (_stopping || _disposed || !originalGroup.ToHashSet().SetEquals(currentGroup) ||
                    members.Any(member => !Tasks.ContainsReference(member.Original) || member.Original.OutputGroupId != outputGroupId ||
                        Coordinator.IsActive(member.Original.Id) ||
                        !member.Original.InputPaths.SequenceEqual(member.OriginalPaths, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(LocalizationService.T("SourceRelocationUnavailable"));
                // Recheck candidates immediately before publishing, including an old source that reappeared while probing.
                for (var index = 0; index < members.Count; index++)
                {
                    try
                    {
                        _ = SourceRelocationService.CreateRequest(members[index].Original, replacements, requireKnownSize);
                    }
                    catch (SourceRelocationValidationException exception)
                    {
                        // A candidate that changed after a successful probe is an operation failure,
                        // rather than an invalid initial choice that the host may omit from diagnostics.
                        throw new InvalidOperationException(exception.Message, exception);
                    }
                    SourceRelocationService.ValidateImported(members[index].Original, members[index].Request, prepared[index]);
                }
                // A successful source update must not later restore held rows
                // that still refer to the previous source location.
                if (outputGroupId is { } changedGroup) CommitRemovalUndoForGroups([changedGroup]);
                for (var index = 0; index < members.Count; index++)
                {
                    var original = members[index].Original;
                    _releaseImport?.Invoke(original);
                    ApplyProbe(original, prepared[index]);
                    SourceRelocationService.CaptureIdentity(original);
                    original.State = ConversionState.Waiting;
                    original.StateBeforeBlocked = null;
                    original.BlockedReason = original.LastError = string.Empty;
                    original.Message = LocalizationService.T("SourceRelocationReady");
                    original.Progress = 0;
                    rememberCommitted?.Invoke(original);
                    Notify(TaskStateChanged, original);
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            try { await SaveAsync(token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            { Report(error); return new(members.Count, error); }
            return new(members.Count);
        }
        finally
        {
            foreach (var imported in prepared)
            {
                try { releaseTemporary(imported); }
                catch (Exception error) { Report(error); }
            }
            await _dispatch(() =>
            {
                foreach (var member in members) member.Original.IsRecoveringOutput = false;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
