using System.Globalization;
using System.Security.Cryptography;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

/// <summary>
/// Runs ordered rules within the application lifetime. Periodic scans also recover from
/// watcher overflows and disconnected folders; importing a startup backlog is opt-in.
/// </summary>
public sealed class AutomationService : IAsyncDisposable
{
    private readonly AutomationStore _store;
    private readonly Func<AutomationRule, string, CancellationToken, Task<AutomationExecutionResult>> _execute;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _settleDelay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private readonly SemaphoreSlim _executionSlots = new(4, 4);
    private AutomationRule[] _rules = [];
    private List<AutomationProcessedFile> _records = [];
    private List<AutomationSuppressedFile> _suppressions = [];
    private List<AutomationRetryProgress> _retryProgress = [];
    private AutomationHistoryIndex? _historyIndex;
    private long _retryProgressRevision;
    private Dictionary<Guid, string> _scheduleRuns = [];
    private Dictionary<Guid, WatchState> _watchStates = [];
    private readonly Dictionary<string, FileJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _batches = [];
    private readonly HashSet<Guid> _scheduledInFlight = [];
    private CancellationTokenSource _shutdown = new();
    private Task? _loopTask;
    private bool _initialized;
    private bool _disposed;

    public AutomationService(
        AutomationStore store,
        Func<AutomationRule, string, CancellationToken, Task<AutomationExecutionResult>> execute,
        TimeSpan? pollInterval = null,
        TimeSpan? settleDelay = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _settleDelay = settleDelay ?? TimeSpan.FromSeconds(2);
        _clock = clock ?? (() => DateTimeOffset.Now);
        if (_pollInterval <= TimeSpan.Zero || _settleDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public event EventHandler<AutomationActivity>? ActivityChanged;

    public bool HasActiveWork
    {
        get
        {
            lock (_stateGate)
                return _scanGate.CurrentCount == 0 || _batches.Any(task => !task.IsCompleted)
                    || _jobs.Values.Any(job => !job.Completion.Task.IsCompleted);
        }
    }

    public IReadOnlyList<AutomationRule> Rules
    {
        get { lock (_stateGate) return _rules.Select(rule => rule.Snapshot()).ToArray(); }
    }

    public async Task ConfigureAsync(IEnumerable<AutomationRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var copies = rules.Select(rule => rule.Snapshot()).ToArray();
        foreach (var rule in copies) rule.Validate();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _store.SaveRulesAsync(copies, cancellationToken).ConfigureAwait(false);
            long retryProgressRevision;
            lock (_stateGate) retryProgressRevision = _retryProgressRevision;
            var snapshot = await RecoverArchiveReceiptsAsync(await _store.LoadAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            var states = await BuildWatchStatesAsync(copies, snapshot.Records, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                // Saving another rule must not discard an unchanged startup backlog,
                // including files still being written or waiting for an execution slot.
                foreach (var rule in copies)
                {
                    if (_watchStates.TryGetValue(rule.Id, out var existingState) && existingState.HasStartupCatchUp &&
                        _rules.FirstOrDefault(previous => previous.Id == rule.Id) is { } previousRule &&
                        SameRule(previousRule, rule))
                        states[rule.Id] = existingState;
                }
                _rules = copies;
                // A concurrent queue completion may be newer than the store read above.
                _records = MergeRecords(snapshot.Records, _records);
                _suppressions = MergeSuppressions(snapshot.Suppressions, _suppressions);
                _historyIndex = null;
                if (_retryProgressRevision == retryProgressRevision)
                    _retryProgress = MergeRetryProgress(snapshot.RetryProgress, _retryProgress);
                foreach (var (id, date) in snapshot.ScheduleRuns)
                {
                    if (string.CompareOrdinal(date, _scheduleRuns.GetValueOrDefault(id)) > 0) _scheduleRuns[id] = date;
                }
                _watchStates = states;
                foreach (var (path, job) in _jobs.ToArray())
                {
                    var winner = FirstMatchingRule(path, copies);
                    if (winner is null || !SameRule(job.Rule, winner)) job.RuleChanged.Cancel();
                }
                _initialized = true;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopTask is { IsCompleted: false }) return;
            if (!_initialized)
            {
                var snapshot = await RecoverArchiveReceiptsAsync(await _store.LoadAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                lock (_stateGate)
                {
                    _rules = snapshot.Rules.Select(rule => rule.Snapshot()).ToArray();
                    _records = snapshot.Records.ToList();
                    _suppressions = snapshot.Suppressions.ToList();
                    _historyIndex = null;
                    _retryProgress = snapshot.RetryProgress.ToList();
                    _scheduleRuns = snapshot.ScheduleRuns;
                    _initialized = true;
                }
            }
            if (_shutdown.IsCancellationRequested)
            {
                _shutdown.Dispose();
                _shutdown = new CancellationTokenSource();
            }
            AutomationRule[] rules;
            AutomationProcessedFile[] records;
            lock (_stateGate) { rules = _rules; records = _records.ToArray(); }
            var states = await BuildWatchStatesAsync(rules, records, cancellationToken, catchUpOnStartup: true).ConfigureAwait(false);
            lock (_stateGate) _watchStates = states;
            _loopTask = PollLoopAsync(_shutdown.Token);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _shutdown.Cancel();
            if (_loopTask is { } loop)
            {
                try { await loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
                _loopTask = null;
            }
            // Manual scans use the same lifetime token and must also finish before exit.
            await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _scanGate.Release();
            await WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<AutomationRunSummary> RunNowAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        using var linked = CreateScanToken(cancellationToken);
        await _scanGate.WaitAsync(linked.Token).ConfigureAwait(false);
        Task<AutomationRunSummary> execution;
        try
        {
            var rule = CurrentRules().FirstOrDefault(rule => rule.Id == ruleId)
                ?? throw new ArgumentException(LocalizationService.T("AutomationValidationOptions"), nameof(ruleId));
            var prepared = await PrepareScanAsync(rule, linked.Token, allowFailedRetry: true).ConfigureAwait(false);
            execution = TrackBatch(() => ExecutePreparedAsync(prepared, linked.Token));
        }
        finally { _scanGate.Release(); }
        return await execution.ConfigureAwait(false);
    }

    /// <summary>Waits for scans already started to finish without stopping the service.</summary>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] work;
            lock (_stateGate) work = _batches.Concat(_jobs.Values.Select(job => (Task)job.Completion.Task))
                .Where(task => !task.IsCompleted).ToArray();
            if (work.Length == 0) return;
            try { await Task.WhenAll(work).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A stopped service cancels its work; those jobs still need to unwind.
            }
        }
    }

    /// <summary>One polling pass, also useful for deterministic schedule and file-discovery tests.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CreateScanToken(cancellationToken);
        var token = linked.Token;
        await _scanGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            KeyValuePair<Guid, WatchState>[] states;
            lock (_stateGate) states = _watchStates.ToArray();
            foreach (var pair in states)
            {
                token.ThrowIfCancellationRequested();
                var rule = CurrentRules().FirstOrDefault(rule => rule.Id == pair.Key && rule.Enabled &&
                    (rule.WatchEnabled || pair.Value.HasStartupCatchUp));
                if (rule is not null) await PollFolderAsync(rule, pair.Value, token).ConfigureAwait(false);
            }

            var now = _clock();
            CancellationToken lifetimeToken;
            lock (_stateGate) lifetimeToken = _shutdown.Token;
            var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var rule in CurrentRules().Where(rule => rule.Enabled && rule.ScheduleEnabled))
            {
                token.ThrowIfCancellationRequested();
                var scheduled = TimeOnly.ParseExact(rule.ScheduleTime, "HH:mm", CultureInfo.InvariantCulture);
                if (TimeOnly.FromDateTime(now.DateTime) < scheduled) continue;
                lock (_stateGate)
                {
                    if (_scheduleRuns.GetValueOrDefault(rule.Id) == date || _scheduledInFlight.Contains(rule.Id)) continue;
                }
                var prepared = await PrepareScanAsync(rule, token).ConfigureAwait(false);
                // Offline or unreadable folders can recover later in the same day.
                if (!prepared.CanCheckpoint) continue;
                lock (_stateGate) _scheduledInFlight.Add(rule.Id);
                _ = TrackBatch(async () =>
                {
                    using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
                    var batchToken = batchCancellation.Token;
                    try
                    {
                        var summary = await ExecutePreparedAsync(prepared, batchToken).ConfigureAwait(false);
                        batchToken.ThrowIfCancellationRequested();
                        if (!prepared.HasDeferredFiles)
                            await _store.MarkScheduledRunAsync(rule.Id, date, batchToken).ConfigureAwait(false);
                        else return summary;
                        lock (_stateGate) _scheduleRuns[rule.Id] = date;
                        return summary;
                    }
                    catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
                    { return new AutomationRunSummary(0, 0, 0, 0); }
                    catch (Exception exception)
                    {
                        Emit(rule, rule.FolderPath, AutomationActivityState.Failed, exception.Message);
                        return new AutomationRunSummary(0, 0, 1, 0);
                    }
                    finally { lock (_stateGate) _scheduledInFlight.Remove(rule.Id); }
                });
            }
        }
        finally { _scanGate.Release(); }
    }

    /// <summary>
    /// Completes a conversion, including one restored/retried by the ordinary queue.
    /// The original fingerprint must be persisted with that queue entry before conversion starts.
    /// </summary>
    public async Task<AutomationExecutionResult> CompleteExecutionAsync(
        AutomationRule rule,
        string inputPath,
        AutomationFileFingerprint expectedFingerprint,
        AutomationExecutionResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Checkpointed) return result;
        if (result.Cancelled)
        {
            using var cancellationLinked = CreateCompletionToken(cancellationToken);
            await SuppressAsync(rule, inputPath, expectedFingerprint, cancelled: true, cancellationLinked.Token).ConfigureAwait(false);
            return result;
        }
        if (!result.Success) return result;
        using var linked = CreateCompletionToken(cancellationToken);
        cancellationToken = linked.Token;
        cancellationToken.ThrowIfCancellationRequested();
        rule.Validate();
        var outputPaths = result.OutputPaths ?? (string.IsNullOrWhiteSpace(result.OutputPath) ? [] : [result.OutputPath]);
        if (outputPaths.Count == 0 || outputPaths.Any(output =>
            string.IsNullOrWhiteSpace(output) || (!File.Exists(output) && !Directory.Exists(output))))
            return new AutomationExecutionResult(false, result.OutputPath, LocalizationService.T("AutomationStateOutputMissing"));

        inputPath = Path.GetFullPath(inputPath);
        var outputPath = Path.GetFullPath(outputPaths[0]);
        if (outputPaths.Any(output => Path.GetFullPath(output).Equals(inputPath, StringComparison.OrdinalIgnoreCase)))
            return new AutomationExecutionResult(false, outputPath, LocalizationService.T("AutomationValidationOverlap"));
        await _completionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = new AutomationProcessedFile(rule.Id, inputPath, expectedFingerprint, outputPath,
                FileFingerprintService.Read(outputPath), DateTimeOffset.UtcNow,
                Outputs: result.OutputPaths is null ? null : outputPaths.Select(output =>
                    new AutomationOutputReceipt(Path.GetFullPath(output), FileFingerprintService.Read(output))).ToArray());
            AutomationProcessedFile? existing;
            lock (_stateGate) existing = _records.FirstOrDefault(value => SameSource(value, record));
            if (existing is not null)
            {
                record = record with { CompletedAtUtc = existing.CompletedAtUtc,
                    ArchivedPath = existing.ArchivedPath, ArchiveReceipt = existing.ArchiveReceipt };
                if (existing.ArchivedPath is { } archived)
                {
                    var matches = await MatchesArchiveAsync(existing, archived, cancellationToken).ConfigureAwait(false);
                    return result with { ArchivePending = !matches,
                        Error = matches ? (result.ArchivePending ? null : result.Error) : LocalizationService.T("AutomationStateSourceChanged") };
                }
                if (await TryRecoverMovedArchiveAsync(existing, cancellationToken).ConfigureAwait(false) is not null)
                    return result with { ArchivePending = false, Error = result.ArchivePending ? null : result.Error };
            }

            // Persist successful conversion before moving the source. A crash during archive
            // must not re-convert a successfully processed input after restart.
            await _store.RecordSuccessAsync(record, cancellationToken).ConfigureAwait(false);
            Remember(record);
            string? warning = null;
            var archivePending = rule.ArchiveSource;
            if (rule.ArchiveSource)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FileFingerprintService.Read(inputPath) != expectedFingerprint)
                {
                    warning = LocalizationService.T("AutomationStateSourceChanged");
                }
                else
                {
                    try
                    {
                        rule.Validate();
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!await ArchiveSourceAsync(record, rule.ArchiveDirectory, cancellationToken).ConfigureAwait(false))
                        {
                            warning = LocalizationService.T("AutomationStateSourceChanged");
                        }
                        else
                        {
                            archivePending = false;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        warning = $"{LocalizationService.T("AutomationStateArchiveFailed")} {exception.Message}";
                    }
                }
            }
            return result with { Error = warning ?? (result.ArchivePending ? null : result.Error), ArchivePending = archivePending };
        }
        finally { _completionGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _shutdown.Dispose();
        _lifecycleGate.Dispose();
        _scanGate.Dispose();
        _completionGate.Dispose();
        _executionSlots.Dispose();
    }

    private CancellationTokenSource CreateScanToken(CancellationToken token)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized) throw new InvalidOperationException(LocalizationService.T("AutomationValidationOptions"));
            return CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        }
    }

    private CancellationTokenSource CreateCompletionToken(CancellationToken token)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        }
    }

    private AutomationRule[] CurrentRules()
    {
        lock (_stateGate) return _rules;
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await TickAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Emit(null, string.Empty, AutomationActivityState.Failed, exception.Message);
            }
            try { await Task.Delay(_pollInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task<Dictionary<Guid, WatchState>> BuildWatchStatesAsync(
        AutomationRule[] rules, IReadOnlyList<AutomationProcessedFile> records, CancellationToken token,
        bool catchUpOnStartup = false) =>
        await Task.Run(() =>
        {
            var states = new Dictionary<Guid, WatchState>();
            var history = new AutomationHistoryIndex(records);
            foreach (var rule in rules.Where(rule => rule.Enabled && (rule.WatchEnabled || catchUpOnStartup && rule.CatchUpOnStartup)))
            {
                token.ThrowIfCancellationRequested();
                var files = Enumerate(rule, rules, history, token);
                states[rule.Id] = new WatchState
                {
                    Available = files.Error is null,
                    Seen = catchUpOnStartup && rule.CatchUpOnStartup ? new(StringComparer.OrdinalIgnoreCase) : files.Files,
                    LastError = files.Error,
                    HasStartupCatchUp = catchUpOnStartup && rule.CatchUpOnStartup,
                    StartupPaths = catchUpOnStartup && rule.CatchUpOnStartup && files.Error is null
                        ? new HashSet<string>(files.Files.Keys, StringComparer.OrdinalIgnoreCase) : null
                };
            }
            return states;
        }, token).ConfigureAwait(false);

    private async Task PollFolderAsync(AutomationRule rule, WatchState state, CancellationToken token)
    {
        var rules = CurrentRules();
        var history = HistoryIndex();
        var listing = await Task.Run(() => Enumerate(rule, rules, history, token), token).ConfigureAwait(false);
        if (!IsCurrentState(rule.Id, state)) return;
        if (listing.Error is { } error)
        {
            if (state.LastError != error) Emit(rule, rule.FolderPath, AutomationActivityState.Waiting, error);
            state.LastError = error;
            state.Available = false;
            state.Seen.Clear();
            state.Pending.Clear();
            return;
        }
        state.LastError = null;
        if (!state.Available)
        {
            // Recovery has a new baseline, just like enabling a watcher.
            state.Seen = state.HasStartupCatchUp ? new(StringComparer.OrdinalIgnoreCase) : listing.Files;
            if (state.HasStartupCatchUp) state.StartupPaths ??= new(listing.Files.Keys, StringComparer.OrdinalIgnoreCase);
            state.Pending.Clear();
            state.Available = true;
            if (!state.HasStartupCatchUp) return;
        }
        foreach (var path in state.Seen.Keys.Except(listing.Files.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
            state.Seen.Remove(path);
        foreach (var path in state.Pending.Keys.Except(listing.Files.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
            state.Pending.Remove(path);
        state.StartupPaths?.ExceptWith(state.StartupPaths.Except(listing.Files.Keys, StringComparer.OrdinalIgnoreCase).ToArray());
        foreach (var (path, fingerprint) in listing.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrentState(rule.Id, state)) return;
            if (!rule.WatchEnabled && state.StartupPaths?.Contains(path) != true) continue;
            if (state.Seen.GetValueOrDefault(path) == fingerprint) continue;
            var winner = FirstMatchingRule(path, CurrentRules());
            if (winner?.Id != rule.Id)
            {
                state.Seen[path] = fingerprint;
                state.Pending.Remove(path);
                state.StartupPaths?.Remove(path);
                continue;
            }
            if (!state.Pending.TryGetValue(path, out var candidate) || candidate.Fingerprint != fingerprint)
            {
                candidate = new PendingFile(fingerprint, DateTimeOffset.UtcNow);
                state.Pending[path] = candidate;
            }
            if (candidate.Job is { } priorJob)
            {
                if (!priorJob.IsCompleted) continue;
                if (priorJob.Status == TaskStatus.RanToCompletion && priorJob.Result == AutomationActivityState.Waiting)
                {
                    candidate.Job = null;
                }
                else
                {
                    state.Seen[path] = fingerprint;
                    state.Pending.Remove(path);
                    state.StartupPaths?.Remove(path);
                    continue;
                }
            }
            if (DateTimeOffset.UtcNow - candidate.ObservedAt < _settleDelay || ReadReadyFingerprint(path) != fingerprint)
                continue;
            // Conversion completion belongs to the queue, not to the scanning lock.
            // When all four slots are occupied this candidate stays pending for the next pass.
            CancellationToken lifetimeToken;
            lock (_stateGate) lifetimeToken = _shutdown.Token;
            candidate.Job = TryStartFile(winner, path, fingerprint, lifetimeToken);
        }
        if (state.StartupPaths is { Count: 0 }) state.HasStartupCatchUp = false;
    }

    private bool IsCurrentState(Guid ruleId, WatchState state)
    {
        lock (_stateGate) return _watchStates.TryGetValue(ruleId, out var current) && ReferenceEquals(current, state);
    }

    private async Task<ScanPreparation> PrepareScanAsync(AutomationRule rule, CancellationToken token, bool allowFailedRetry = false)
    {
        if (!rule.Enabled) return new ScanPreparation(rule, [], false, new AutomationRunSummary(0, 0, 0, 0));
        Emit(rule, rule.FolderPath, AutomationActivityState.Scanning, LocalizationService.T("AutomationStateScanning"));
        var rules = CurrentRules();
        var history = HistoryIndex();
        var listing = await Task.Run(() => Enumerate(rule, rules, history, token), token).ConfigureAwait(false);
        if (listing.Error is { } error)
        {
            Emit(rule, rule.FolderPath, AutomationActivityState.Failed, error);
            return new ScanPreparation(rule, [], false, new AutomationRunSummary(0, 0, 1, 0));
        }
        var candidates = listing.Files.Where(pair => FirstMatchingRule(pair.Key, rules)?.Id == rule.Id).ToArray();
        return new ScanPreparation(rule, candidates, true, null, allowFailedRetry);
    }

    private async Task<AutomationRunSummary> ExecutePreparedAsync(ScanPreparation prepared, CancellationToken token)
    {
        if (prepared.EmptySummary is { } emptySummary) return emptySummary;
        if (prepared.Candidates.Length > 0 && _settleDelay > TimeSpan.Zero)
            await Task.Delay(_settleDelay, token).ConfigureAwait(false);
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        // Four worker tasks for a batch, regardless of its file count. All triggers share
        // the same four execution slots, so thousands of files never create thousands of tasks.
        await Parallel.ForEachAsync(prepared.Candidates, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = 4
        }, async (candidate, workerToken) =>
        {
            var (path, fingerprint) = candidate;
            token.ThrowIfCancellationRequested();
            var winner = FirstMatchingRule(path, CurrentRules());
            if (winner?.Id != prepared.Rule.Id || ReadReadyFingerprint(path) != fingerprint)
            {
                Interlocked.Increment(ref skipped);
                if (winner?.Id == prepared.Rule.Id) prepared.MarkDeferred();
                Emit(prepared.Rule, path, AutomationActivityState.Waiting, LocalizationService.T("AutomationStateWaiting"));
                return;
            }
            var outcome = await RunOrJoinFileAsync(winner, path, fingerprint, workerToken, prepared.AllowFailedRetry).ConfigureAwait(false);
            switch (outcome)
            {
                case AutomationActivityState.Succeeded: Interlocked.Increment(ref succeeded); break;
                case AutomationActivityState.Failed: Interlocked.Increment(ref failed); break;
                default:
                    if (outcome == AutomationActivityState.Waiting) prepared.MarkDeferred();
                    Interlocked.Increment(ref skipped);
                    break;
            }
        }).ConfigureAwait(false);
        return new AutomationRunSummary(prepared.Candidates.Length, succeeded, failed, skipped);
    }

    private Task<AutomationRunSummary> TrackBatch(Func<Task<AutomationRunSummary>> action)
    {
        var task = Task.Run(action, CancellationToken.None);
        lock (_stateGate) _batches.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_stateGate) _batches.Remove(completed);
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private Task<AutomationActivityState>? TryStartFile(
        AutomationRule rule, string path, AutomationFileFingerprint fingerprint, CancellationToken token)
    {
        lock (_stateGate)
        {
            if (_jobs.TryGetValue(path, out var existing))
                return existing.Fingerprint == fingerprint ? existing.Completion.Task : null;
            if (!_executionSlots.Wait(0)) return null;
            return StartOwnedFile(rule, path, fingerprint, token).Completion.Task;
        }
    }

    private async Task<AutomationActivityState> RunOrJoinFileAsync(
        AutomationRule rule, string path, AutomationFileFingerprint fingerprint, CancellationToken token,
        bool allowFailedRetry = false)
    {
        FileJob? existing;
        lock (_stateGate) _jobs.TryGetValue(path, out existing);
        if (existing is not null)
        {
            var joined = await existing.Completion.Task.WaitAsync(token).ConfigureAwait(false);
            return existing.Fingerprint == fingerprint ? joined : AutomationActivityState.Skipped;
        }
        await _executionSlots.WaitAsync(token).ConfigureAwait(false);
        Task<AutomationActivityState> task;
        lock (_stateGate)
        {
            if (_jobs.TryGetValue(path, out existing))
            {
                _executionSlots.Release();
                task = existing.Completion.Task;
            }
            else task = StartOwnedFile(rule, path, fingerprint, token, allowFailedRetry).Completion.Task;
        }
        var result = await task.WaitAsync(token).ConfigureAwait(false);
        return existing is null || existing.Fingerprint == fingerprint ? result : AutomationActivityState.Skipped;
    }

    // Called with stateGate held and a reserved execution slot.
    private FileJob StartOwnedFile(AutomationRule rule, string path, AutomationFileFingerprint fingerprint, CancellationToken token,
        bool allowFailedRetry = false)
    {
        var job = new FileJob(rule.Snapshot(), fingerprint);
        _jobs[path] = job;
        _ = RunOwnedFileAsync(job.Rule, path, job, token, allowFailedRetry);
        return job;
    }

    private async Task RunOwnedFileAsync(AutomationRule rule, string path, FileJob job, CancellationToken token, bool allowFailedRetry)
    {
        await Task.Yield();
        var outcome = AutomationActivityState.Failed;
        var cancelled = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var currentWinner = FirstMatchingRule(path, CurrentRules());
            outcome = currentWinner?.Id != rule.Id
                ? AutomationActivityState.Skipped
                : ReadReadyFingerprint(path) != job.Fingerprint
                ? AutomationActivityState.Waiting
                : await ExecuteFileAsync(rule, path, job, token, allowFailedRetry).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { cancelled = true; }
        catch (Exception exception)
        {
            Emit(rule, path, AutomationActivityState.Failed, exception.Message);
        }
        finally
        {
            lock (_stateGate)
            {
                if (_jobs.GetValueOrDefault(path) == job) _jobs.Remove(path);
                _executionSlots.Release();
            }
            if (cancelled) job.Completion.TrySetCanceled(token);
            else job.Completion.TrySetResult(outcome);
            job.RuleChanged.Dispose();
        }
    }

    private async Task<AutomationActivityState> ExecuteFileAsync(
        AutomationRule rule, string path, FileJob job, CancellationToken token, bool allowFailedRetry)
    {
        var fingerprint = job.Fingerprint;
        if (IsKnownSource(path, fingerprint, allowFailedRetry))
        {
            Emit(rule, path, AutomationActivityState.Skipped, LocalizationService.T("AutomationStateSkipped"));
            return AutomationActivityState.Skipped;
        }
        if (AutomationRuleMatcher.IsExcludedByDestinations(path, CurrentRules()) || HistoryIndex().IsExcluded(path, fingerprint))
            return AutomationActivityState.Skipped;
        token.ThrowIfCancellationRequested();
        try
        {
            if (allowFailedRetry) await ResetRetryProgressAsync(path, fingerprint, token).ConfigureAwait(false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var progress = RetryProgressFor(path, fingerprint);
                if (progress is { FailedAttempts: > 0 })
                {
                    if (progress.FailedAttempts > rule.RetryCount)
                    {
                        await SuppressAsync(rule, path, fingerprint, cancelled: false, token).ConfigureAwait(false);
                        Emit(rule, path, AutomationActivityState.Failed, LocalizationService.T("AutomationStateFailed"));
                        return AutomationActivityState.Failed;
                    }
                    if (job.RuleChanged.IsCancellationRequested || IsKnownSource(path, fingerprint, allowFailedRetry))
                        return AutomationActivityState.Skipped;
                    var delay = progress.RetryAfterUtc is { } retryAfter ? retryAfter - _clock().ToUniversalTime() : TimeSpan.Zero;
                    delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0, rule.RetryDelaySeconds));
                    Emit(rule, path, AutomationActivityState.Waiting,
                        LocalizationService.T("AutomationStateRetrying", progress.FailedAttempts, rule.RetryCount,
                            (int)Math.Ceiling(delay.TotalSeconds)));
                    using var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, job.RuleChanged.Token);
                    try { await Task.Delay(delay, retryCancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (job.RuleChanged.IsCancellationRequested && !token.IsCancellationRequested)
                    { return AutomationActivityState.Skipped; }
                    var winner = FirstMatchingRule(path, CurrentRules());
                    if (winner is null || !SameRule(rule, winner)) return AutomationActivityState.Skipped;
                    if (IsKnownSource(path, fingerprint, allowFailedRetry)) return AutomationActivityState.Skipped;
                    if (FileFingerprintService.Read(path) != fingerprint) return AutomationActivityState.Waiting;
                    if (ReadReadyFingerprint(path) != fingerprint)
                    {
                        // Persistent progress keeps the remaining budget while a reopened writer settles.
                        Emit(rule, path, AutomationActivityState.Waiting, LocalizationService.T("AutomationStateWaiting"));
                        return AutomationActivityState.Waiting;
                    }
                }
                Emit(rule, path, AutomationActivityState.Running, LocalizationService.T("AutomationStateRunning"));
                AutomationExecutionResult result;
                try
                {
                    var executionRule = rule.Snapshot() with
                    {
                        ForceRetryFailedOutputs = allowFailedRetry && (progress is null || progress.FailedAttempts == 0)
                    };
                    result = await _execute(executionRule, path, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                { result = new(false, Error: exception.Message, Retryable: ConversionFailurePolicy.IsTransient(exception)); }
                token.ThrowIfCancellationRequested();
                var terminalCheckpointed = false;
                if (!result.Success && !result.Cancelled && !result.Deferred)
                {
                    if (IsKnownSource(path, fingerprint, allowFailedRetry)) return AutomationActivityState.Skipped;
                    if (result.Retryable)
                        progress = await RecordFailureAsync(rule, path, fingerprint, token).ConfigureAwait(false);
                    else
                    {
                        // Permanent errors must never be persisted as resumable retry progress.
                        await SuppressAsync(rule, path, fingerprint, cancelled: false, token).ConfigureAwait(false);
                        terminalCheckpointed = true;
                    }
                }
                result = await CompleteExecutionAsync(rule, path, fingerprint, result, token).ConfigureAwait(false);
                if (!result.Success && !result.Cancelled && !result.Deferred && result.Retryable &&
                    progress is { } failedProgress && failedProgress.FailedAttempts <= rule.RetryCount)
                {
                    if (FileFingerprintService.Read(path) != fingerprint) return AutomationActivityState.Waiting;
                    var winner = FirstMatchingRule(path, CurrentRules());
                    if (winner is null || !SameRule(rule, winner) || job.RuleChanged.IsCancellationRequested)
                        return AutomationActivityState.Skipped;
                    continue;
                }
                if (!result.Success && !result.Cancelled && !result.Deferred && !terminalCheckpointed)
                    await SuppressAsync(rule, path, fingerprint, cancelled: false, token).ConfigureAwait(false);
                var state = result.Deferred ? AutomationActivityState.Waiting :
                    result.Success && !result.Cancelled ? AutomationActivityState.Succeeded : AutomationActivityState.Failed;
                Emit(rule, path, state, result.Error ?? LocalizationService.T(
                    result.Deferred ? "AutomationStateWaiting" : result.Success ? "AutomationStateSucceeded" : "AutomationStateFailed"));
                return state;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Emit(rule, path, AutomationActivityState.Failed, exception.Message);
            return AutomationActivityState.Failed;
        }
    }

    public static bool SameConversion(AutomationRule first, AutomationRule second) =>
        first.TargetFormatId == second.TargetFormatId && first.ConversionMode == second.ConversionMode &&
        first.Options.Count == second.Options.Count && first.Options.All(pair => second.Options.GetValueOrDefault(pair.Key) == pair.Value) &&
        SameOutputs(first.GetOutputs(), second.GetOutputs());

    private static bool SameRule(AutomationRule first, AutomationRule second) =>
        first with { Options = second.Options, Outputs = second.Outputs, ForceRetryFailedOutputs = second.ForceRetryFailedOutputs } == second &&
        SameConversion(first, second);

    private static bool SameOutputs(IReadOnlyList<OutputConfiguration> first, IReadOnlyList<OutputConfiguration> second) =>
        first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Label == pair.Second.Label &&
            pair.First.TargetFormatId == pair.Second.TargetFormatId && pair.First.ConversionMode == pair.Second.ConversionMode &&
            pair.First.SelectedAudioTrack == pair.Second.SelectedAudioTrack && pair.First.Options.Count == pair.Second.Options.Count &&
            pair.First.Options.All(option => pair.Second.Options.GetValueOrDefault(option.Key) == option.Value));

    private bool IsKnownSource(string path, AutomationFileFingerprint fingerprint, bool allowFailedRetry)
    {
        return HistoryIndex().IsKnownSource(path, fingerprint, allowFailedRetry);
    }

    private AutomationRetryProgress? RetryProgressFor(string path, AutomationFileFingerprint fingerprint)
    {
        lock (_stateGate) return _retryProgress.FirstOrDefault(progress =>
            progress.InputPath.Equals(path, StringComparison.OrdinalIgnoreCase) && progress.Fingerprint == fingerprint);
    }

    private async Task ResetRetryProgressAsync(string path, AutomationFileFingerprint fingerprint, CancellationToken token)
    {
        await _completionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                _retryProgress.RemoveAll(progress => progress.InputPath.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                    progress.Fingerprint == fingerprint);
                _retryProgressRevision++;
            }
            await _store.ResetRetryProgressAsync(path, fingerprint, token).ConfigureAwait(false);
        }
        finally { _completionGate.Release(); }
    }

    private async Task<AutomationRetryProgress> RecordFailureAsync(AutomationRule rule, string path,
        AutomationFileFingerprint fingerprint, CancellationToken token)
    {
        await _completionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            AutomationRetryProgress progress;
            lock (_stateGate)
            {
                var existing = _retryProgress.FirstOrDefault(value =>
                    value.InputPath.Equals(path, StringComparison.OrdinalIgnoreCase) && value.Fingerprint == fingerprint);
                progress = new(rule.Id, Path.GetFullPath(path), fingerprint, (existing?.FailedAttempts ?? 0) + 1,
                    _clock().ToUniversalTime().AddSeconds(rule.RetryDelaySeconds));
                _retryProgress.RemoveAll(value => value.InputPath.Equals(path, StringComparison.OrdinalIgnoreCase) && value.Fingerprint == fingerprint);
                _retryProgress.Add(progress);
                _retryProgressRevision++;
            }
            await _store.RecordRetryProgressAsync(progress, token).ConfigureAwait(false);
            return progress;
        }
        finally { _completionGate.Release(); }
    }

    private async Task SuppressAsync(AutomationRule rule, string path, AutomationFileFingerprint fingerprint,
        bool cancelled, CancellationToken token)
    {
        var suppression = new AutomationSuppressedFile(rule.Id, Path.GetFullPath(path), fingerprint, cancelled, DateTimeOffset.UtcNow);
        await _completionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (_records.Any(record => record.InputPath.Equals(suppression.InputPath, StringComparison.OrdinalIgnoreCase) &&
                    record.Fingerprint == fingerprint)) return;
                suppression = suppression with { Cancelled = cancelled || _suppressions.Any(existing => existing.Cancelled &&
                    existing.InputPath.Equals(suppression.InputPath, StringComparison.OrdinalIgnoreCase) && existing.Fingerprint == fingerprint) };
                _suppressions.RemoveAll(existing => SameSource(existing, suppression));
                _suppressions.Add(suppression);
                _historyIndex?.Suppress(suppression);
                _retryProgress.RemoveAll(progress => progress.InputPath.Equals(suppression.InputPath, StringComparison.OrdinalIgnoreCase) &&
                    progress.Fingerprint == fingerprint);
                _retryProgressRevision++;
                if (suppression.Cancelled && _jobs.TryGetValue(suppression.InputPath, out var job) && job.Fingerprint == fingerprint)
                    job.RuleChanged.Cancel();
            }
            await _store.RecordSuppressionAsync(suppression, token).ConfigureAwait(false);
        }
        finally { _completionGate.Release(); }
    }

    private AutomationHistoryIndex HistoryIndex()
    {
        lock (_stateGate) return _historyIndex ??= new AutomationHistoryIndex(_records, _suppressions);
    }

    private void Remember(AutomationProcessedFile record)
    {
        lock (_stateGate)
        {
            _records.RemoveAll(existing => SameSource(existing, record));
            _records.Add(record);
            _historyIndex?.Remember(record);
            _suppressions.RemoveAll(existing => existing.InputPath.Equals(record.InputPath, StringComparison.OrdinalIgnoreCase) &&
                existing.Fingerprint == record.Fingerprint);
            _retryProgress.RemoveAll(progress => progress.InputPath.Equals(record.InputPath, StringComparison.OrdinalIgnoreCase) &&
                progress.Fingerprint == record.Fingerprint);
            _retryProgressRevision++;
        }
    }

    private static bool SameSource(AutomationProcessedFile first, AutomationProcessedFile second) =>
        first.InputPath.Equals(second.InputPath, StringComparison.OrdinalIgnoreCase) && first.Fingerprint == second.Fingerprint;

    private static bool SameSource(AutomationSuppressedFile first, AutomationSuppressedFile second) =>
        first.InputPath.Equals(second.InputPath, StringComparison.OrdinalIgnoreCase) && first.Fingerprint == second.Fingerprint;

    private static List<AutomationSuppressedFile> MergeSuppressions(
        IEnumerable<AutomationSuppressedFile> stored, IEnumerable<AutomationSuppressedFile> remembered)
    {
        var result = stored.ToList();
        foreach (var suppression in remembered)
        {
            var cancelled = suppression.Cancelled || result.Any(existing => SameSource(existing, suppression) && existing.Cancelled);
            result.RemoveAll(existing => SameSource(existing, suppression));
            result.Add(suppression with { Cancelled = cancelled });
        }
        return result;
    }

    private static List<AutomationRetryProgress> MergeRetryProgress(
        IEnumerable<AutomationRetryProgress> stored, IEnumerable<AutomationRetryProgress> remembered)
    {
        var result = stored.ToList();
        foreach (var progress in remembered)
        {
            var existing = result.FirstOrDefault(value => value.InputPath.Equals(progress.InputPath, StringComparison.OrdinalIgnoreCase) &&
                value.Fingerprint == progress.Fingerprint);
            if (existing is not null && existing.FailedAttempts > progress.FailedAttempts) continue;
            result.RemoveAll(value => value.InputPath.Equals(progress.InputPath, StringComparison.OrdinalIgnoreCase) && value.Fingerprint == progress.Fingerprint);
            result.Add(progress);
        }
        return result;
    }

    private static List<AutomationProcessedFile> MergeRecords(
        IEnumerable<AutomationProcessedFile> stored, IEnumerable<AutomationProcessedFile> remembered)
    {
        var result = stored.ToList();
        foreach (var record in remembered)
        {
            result.RemoveAll(existing => SameSource(existing, record));
            result.Add(record);
        }
        return result;
    }

    private static AutomationFileFingerprint? ReadReadyFingerprint(string path)
    {
        try
        {
            // Existing writers are incompatible with FileShare.Read, even during a copy pause.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fingerprint = FileFingerprintService.Read(path);
            return stream.Length > 0 && fingerprint?.Length == stream.Length ? fingerprint : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static FileListing Enumerate(
        AutomationRule rule, IReadOnlyList<AutomationRule> rules, AutomationHistoryIndex history,
        CancellationToken token)
    {
        var files = new Dictionary<string, AutomationFileFingerprint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(rule.FolderPath))
                return new FileListing(files, $"{LocalizationService.T("AutomationStateFolderUnavailable")} {rule.FolderPath}");
            foreach (var entry in FolderDiscovery.Enumerate(rule.FolderPath, rule.IncludeSubfolders, false, true, token))
            {
                token.ThrowIfCancellationRequested();
                var path = entry.Path;
                if (AutomationRuleMatcher.IsExcludedByDestinations(path, rules)) continue;
                var fingerprint = entry.Fingerprint;
                if (!history.IsExcluded(path, fingerprint)) files[Path.GetFullPath(path)] = fingerprint;
            }
            return new FileListing(files, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new FileListing(files, $"{LocalizationService.T("AutomationStateFolderUnavailable")} {rule.FolderPath} ({exception.Message})"); }
    }

    private static AutomationRule? FirstMatchingRule(string path, IEnumerable<AutomationRule> rules) =>
        AutomationRuleMatcher.FirstMatch(rules, path);

    private async Task<bool> ArchiveSourceAsync(AutomationProcessedFile record, string archiveDirectory, CancellationToken token)
    {
        Directory.CreateDirectory(archiveDirectory);
        // Deny writers while permitting this process to rename the open file.
        await using var source = new FileStream(record.InputPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != record.Fingerprint.Length || FileFingerprintService.Read(record.InputPath) != record.Fingerprint) return false;
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        if (record.ArchiveReceipt is { } previous && !digest.Equals(previous.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileNameWithoutExtension(record.InputPath);
        var extension = Path.GetExtension(record.InputPath);
        for (var suffix = 0; suffix < 100_000; suffix++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = Path.Combine(archiveDirectory, suffix == 0 ? name + extension : $"{name} ({suffix}){extension}");
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            record = record with { ArchiveReceipt = new(candidate, digest) };
            // This intent survives termination between File.Move and the final checkpoint.
            await _store.RecordSuccessAsync(record, token).ConfigureAwait(false);
            Remember(record);
            token.ThrowIfCancellationRequested();
            if (FileFingerprintService.Read(record.InputPath) != record.Fingerprint) return false;
            try
            {
                File.Move(record.InputPath, candidate, overwrite: false);
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { continue; }
            record = record with { ArchivedPath = candidate,
                ArchiveReceipt = record.ArchiveReceipt with { Fingerprint = FileFingerprintService.Read(candidate) } };
            // Once the source has moved, ordinary shutdown must not cancel its commit.
            await _store.RecordSuccessAsync(record, CancellationToken.None).ConfigureAwait(false);
            Remember(record);
            return true;
        }
        throw new IOException(LocalizationService.T("AutomationStateArchiveFailed"));
    }

    private async Task<AutomationSnapshot> RecoverArchiveReceiptsAsync(AutomationSnapshot snapshot, CancellationToken token)
    {
        if (!snapshot.Records.Any(record => record.ArchivedPath is null && record.ArchiveReceipt is not null)) return snapshot;
        await _completionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A queue completion may have advanced the record while configuration awaited this gate.
            snapshot = await _store.LoadAsync(token).ConfigureAwait(false);
            var records = snapshot.Records.ToArray();
            for (var index = 0; index < records.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (records[index].ArchivedPath is not null || records[index].ArchiveReceipt is null) continue;
                try
                {
                    if (await TryRecoverMovedArchiveAsync(records[index], token).ConfigureAwait(false) is { } recovered)
                        records[index] = recovered;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                { SafeDiagnostics.Report(exception); }
            }
            return snapshot with { Records = records };
        }
        finally { _completionGate.Release(); }
    }

    private async Task<AutomationProcessedFile?> TryRecoverMovedArchiveAsync(AutomationProcessedFile record, CancellationToken token)
    {
        if (record.ArchiveReceipt is not { } receipt ||
            FileFingerprintService.Read(record.InputPath) == record.Fingerprint ||
            !await MatchesArchiveAsync(record, receipt.DestinationPath, token).ConfigureAwait(false)) return null;
        var recovered = record with { ArchivedPath = receipt.DestinationPath,
            ArchiveReceipt = receipt with { Fingerprint = FileFingerprintService.Read(receipt.DestinationPath) } };
        await _store.RecordSuccessAsync(recovered, CancellationToken.None).ConfigureAwait(false);
        Remember(recovered);
        return recovered;
    }

    private static async Task<bool> MatchesArchiveAsync(AutomationProcessedFile record, string path, CancellationToken token)
    {
        // Older terminal archive records did not store a destination fingerprint or hash.
        // Cross-volume moves may change creation time; preserve their existing terminal semantics.
        if (record.ArchiveReceipt is not { } receipt) return File.Exists(path);
        if (!path.Equals(receipt.DestinationPath, StringComparison.OrdinalIgnoreCase) ||
            FileFingerprintService.Read(path)?.Length != record.Fingerprint.Length) return false;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            return digest.Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private void Emit(AutomationRule? rule, string path, AutomationActivityState state, string message)
    {
        try
        {
            ActivityChanged?.Invoke(this, new AutomationActivity(rule?.Id ?? Guid.Empty, rule?.Name ?? string.Empty,
                path, state, message, _clock()));
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
    }

    private sealed class WatchState
    {
        public bool HasStartupCatchUp { get; set; }
        public HashSet<string>? StartupPaths { get; set; }
        public bool Available { get; set; }
        public string? LastError { get; set; }
        public Dictionary<string, AutomationFileFingerprint> Seen { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PendingFile> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed record PendingFile(AutomationFileFingerprint Fingerprint, DateTimeOffset ObservedAt)
    {
        public Task<AutomationActivityState>? Job { get; set; }
    }
    private sealed record FileListing(Dictionary<string, AutomationFileFingerprint> Files, string? Error);
    private sealed record ScanPreparation(AutomationRule Rule, KeyValuePair<string, AutomationFileFingerprint>[] Candidates,
        bool CanCheckpoint, AutomationRunSummary? EmptySummary, bool AllowFailedRetry = false)
    {
        private int _deferred;
        public bool HasDeferredFiles => Volatile.Read(ref _deferred) != 0;
        public void MarkDeferred() => Interlocked.Exchange(ref _deferred, 1);
    }
    private sealed class FileJob(AutomationRule rule, AutomationFileFingerprint fingerprint)
    {
        public AutomationRule Rule { get; } = rule;
        public CancellationTokenSource RuleChanged { get; } = new();
        public AutomationFileFingerprint Fingerprint { get; } = fingerprint;
        public TaskCompletionSource<AutomationActivityState> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
