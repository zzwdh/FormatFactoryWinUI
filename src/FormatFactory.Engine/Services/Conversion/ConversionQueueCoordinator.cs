using System.Diagnostics;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed record ConversionQueueOperationCompleted(
    Guid TaskId,
    bool RemoveRequested,
    Exception? Error,
    ConversionOutcome Outcome = ConversionOutcome.Completed)
{
    internal ErrorCorrelationFacts? ErrorCorrelation { get; init; }
}

public sealed class ConversionQueueCoordinator : IDisposable
{
    private const int MinimumParallelism = 1;
    private const int MaximumParallelism = 4;
    private enum CancellationRequest { TaskCancel, WaitingCancel, Removal, CancelAll, QueueStop, QueueDisposal }

    private sealed class OperationState(Guid taskId, ConversionWorkload workload) : IDisposable
    {
        private readonly object _syncRoot = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public CancellationToken Token => _cancellation.Token;
        public Task Completion => _completion.Task;
        public Guid TaskId { get; } = taskId;
        public ConversionWorkload Workload { get; } = workload;
        public TaskCompletionSource<bool> Admission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Accessed only under the coordinator lock. A reserved slot is already running
        // for ordering purposes, even before its delegate publishes a UI state change.
        public bool IsAdmitted { get; set; }
        public bool IsCancellationPending { get; set; }

        public void Cancel(CancellationRequest request)
        {
            lock (_syncRoot)
            {
                if (!_disposed && !_cancellation.IsCancellationRequested)
                {
                    var requestedAt = DateTimeOffset.UtcNow;
                    try
                    {
                        _cancellation.Cancel();
                    }
                    catch (Exception ex)
                    {
                        LogSafely(ex);
                    }
                    finally
                    {
                        SafeDiagnostics.Write(FormattableString.Invariant(
                            $"Queue cancellation request: session={RuntimeFailureDiagnostics.SessionId:N} task={TaskId:N} reason={request} requested_utc={requestedAt:O}"));
                    }
                }
            }
        }

        public void Complete() => _completion.TrySetResult(null);

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }

    private readonly object _syncRoot = new();
    // Keep each operation until its callbacks finish, even if the same task restarts.
    private readonly HashSet<OperationState> _operations = [];
    private readonly Dictionary<Guid, OperationState> _executingOperations = [];
    private readonly HashSet<Guid> _removeAfterOperation = [];
    private readonly List<OperationState> _admissionQueue = [];
    private readonly HashSet<OperationState> _runningOperations = [];
    private readonly Func<ConversionResourceSnapshot> _resourceSnapshot;
    private Timer? _admissionTimer;
    private int _runningCount;
    private int _desiredParallelism = MinimumParallelism;
    private int _completionCallbacksInProgress;
    private int _orderPublicationsInProgress;
    private bool _batchWasStarted;
    private bool _acceptingOperations = true;
    private bool _disposed;

    public ConversionQueueCoordinator() : this(null) { }

    public ConversionQueueCoordinator(Func<ConversionResourceSnapshot>? resourceSnapshot)
    {
        _resourceSnapshot = resourceSnapshot ?? ConversionResourceMonitor.Capture;
    }

    public int ActiveOperationCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _operations.Count;
            }
        }
    }

    public bool HasActiveOperations => ActiveOperationCount > 0;

    public bool IsActive(Guid taskId)
    {
        lock (_syncRoot)
        {
            return _executingOperations.ContainsKey(taskId);
        }
    }

    public bool CanReorder(Guid taskId)
    {
        lock (_syncRoot)
        {
            return !_disposed && _acceptingOperations &&
                (!_executingOperations.TryGetValue(taskId, out var state) ||
                 (!state.IsAdmitted && !state.IsCancellationPending && !state.Token.IsCancellationRequested));
        }
    }

    /// <summary>
    /// Publishes the host's pending row order together with admission order. Validation
    /// and publication share the slot lock, so a task cannot start between those steps.
    /// Idle task IDs are allowed; admitted or cancelling operations reject the update.
    /// </summary>
    public bool TryReorderPendingOperations(IReadOnlyList<Guid> orderedTaskIds, Action publishOrder)
    {
        ArgumentNullException.ThrowIfNull(orderedTaskIds);
        ArgumentNullException.ThrowIfNull(publishOrder);
        var positions = orderedTaskIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        lock (_syncRoot)
        {
            if (_disposed || !_acceptingOperations || orderedTaskIds.Any(id =>
                    _executingOperations.TryGetValue(id, out var state) &&
                    (state.IsAdmitted || state.IsCancellationPending || state.Token.IsCancellationRequested))) return false;
            var ordered = _admissionQueue.OrderBy(state => positions.GetValueOrDefault(state.TaskId, int.MaxValue)).ToArray();
            _admissionQueue.Clear();
            _admissionQueue.AddRange(ordered);
            _orderPublicationsInProgress++;
            try { publishOrder(); }
            finally
            {
                _orderPublicationsInProgress--;
                AdmitWaitingOperations();
            }
            return true;
        }
    }

    public void ConfigureParallelism(int parallelism)
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            _desiredParallelism = Math.Clamp(parallelism, MinimumParallelism, MaximumParallelism);
            AdmitWaitingOperations();
        }
    }

    public bool TryStart(
        Guid taskId,
        Func<CancellationToken, Task> operation,
        Func<Task> cancelledBeforeStart,
        Func<ConversionQueueOperationCompleted, Task> operationCompleted,
        Func<Task> batchCompleted,
        ConversionWorkload? workload = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return TryStartWithOutcome(taskId, async token =>
        {
            await operation(token);
            return token.IsCancellationRequested ? ConversionOutcome.Cancelled : ConversionOutcome.Completed;
        }, cancelledBeforeStart, operationCompleted, batchCompleted, workload);
    }

    public bool TryStartWithOutcome(
        Guid taskId,
        Func<CancellationToken, Task<ConversionOutcome>> operation,
        Func<Task> cancelledBeforeStart,
        Func<ConversionQueueOperationCompleted, Task> operationCompleted,
        Func<Task> batchCompleted,
        ConversionWorkload? workload = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(cancelledBeforeStart);
        ArgumentNullException.ThrowIfNull(operationCompleted);
        ArgumentNullException.ThrowIfNull(batchCompleted);

        OperationState state;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (!_acceptingOperations || _executingOperations.ContainsKey(taskId))
            {
                return false;
            }

            state = new OperationState(taskId, workload ?? ConversionWorkload.Unspecified);
            _operations.Add(state);
            _executingOperations.Add(taskId, state);
            _removeAfterOperation.Remove(taskId);
            _batchWasStarted = true;
            _admissionQueue.Add(state);
            AdmitWaitingOperations();
        }

        var runner = RunTrackedOperationAsync(
            taskId,
            state,
            operation,
            cancelledBeforeStart,
            operationCompleted,
            batchCompleted);
        _ = ObserveRunnerAsync(runner);
        return true;
    }

    public bool Cancel(Guid taskId)
    {
        OperationState? state;
        lock (_syncRoot)
        {
            _executingOperations.TryGetValue(taskId, out state);
        }

        state?.Cancel(CancellationRequest.TaskCancel);
        return state is not null;
    }

    /// <summary>
    /// Cancels an operation only while it is still waiting for a slot, and waits
    /// for its callbacks to drain before its row may be removed or restored.
    /// </summary>
    public async Task<bool> TryCancelWaitingAsync(Guid taskId)
    {
        OperationState state;
        lock (_syncRoot)
        {
            if (_disposed || !_executingOperations.TryGetValue(taskId, out state!) ||
                state.IsAdmitted || state.IsCancellationPending || state.Token.IsCancellationRequested) return false;
            state.IsCancellationPending = true;
            _admissionQueue.Remove(state);
        }

        // Cancellation invokes callbacks. Do not take the state's cancellation
        // lock while holding the scheduler lock: a concurrent Cancel may already
        // hold that lock and be calling back into the scheduler.
        state.Cancel(CancellationRequest.WaitingCancel);
        await state.Completion.ConfigureAwait(false);
        return true;
    }

    public bool RequestRemoval(Guid taskId)
    {
        OperationState? state;
        lock (_syncRoot)
        {
            if (!_executingOperations.TryGetValue(taskId, out state))
            {
                return false;
            }

            _removeAfterOperation.Add(taskId);
        }

        state.Cancel(CancellationRequest.Removal);
        return true;
    }

    public void CancelAll() => CancelAll(CancellationRequest.CancelAll);

    private void CancelAll(CancellationRequest request)
    {
        OperationState[] states;
        lock (_syncRoot)
        {
            states = [.. _executingOperations.Values];
        }

        foreach (var state in states)
        {
            state.Cancel(request);
        }
    }

    public async Task StopAndWaitAsync()
    {
        lock (_syncRoot)
        {
            _acceptingOperations = false;
        }

        CancelAll(CancellationRequest.QueueStop);
        await WaitForAllAsync();
    }

    public void ResumeAdmission()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_operations.Count != 0) throw new InvalidOperationException("Wait for stopped operations before reopening the queue.");
            _acceptingOperations = true;
        }
    }

    public async Task WaitForAllAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_syncRoot)
            {
                pending = [.. _operations.Select(state => state.Completion)];
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending);
        }
    }

    public void Dispose()
    {
        OperationState[] states;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _acceptingOperations = false;
            _admissionTimer?.Dispose();
            _admissionTimer = null;
            states = [.. _operations];
        }

        foreach (var state in states)
        {
            state.Cancel(CancellationRequest.QueueDisposal);
        }
    }

    private async Task RunTrackedOperationAsync(
        Guid taskId,
        OperationState state,
        Func<CancellationToken, Task<ConversionOutcome>> operation,
        Func<Task> cancelledBeforeStart,
        Func<ConversionQueueOperationCompleted, Task> operationCompleted,
        Func<Task> batchCompleted)
    {
        var gateAcquired = false;
        var operationStarted = false;
        IDisposable? powerRequest = null;
        Exception? operationError = null;
        var outcome = ConversionOutcome.Cancelled;
        var queuedAt = Stopwatch.GetTimestamp();
        try
        {
            try
            {
                await AcquireSlotAsync(state);
                gateAcquired = true;
                state.Token.ThrowIfCancellationRequested();
                operationStarted = true;
                NativeDiagnostics.Log.QueueWait(taskId.ToString("N"), (long)Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
                powerRequest = PowerRequestService.Acquire(LocalizationService.T("GeneralServicePowerConversionTask"));
                NativeDiagnostics.Log.ConversionStage(taskId.ToString("N"), "started", 0);
                outcome = await operation(state.Token);
            }
            catch (OperationCanceledException) when (
                state.Token.IsCancellationRequested &&
                !operationStarted)
            {
                await InvokeSafelyAsync(cancelledBeforeStart);
            }
            catch (OperationCanceledException) when (state.Token.IsCancellationRequested)
            {
                // The running operation owns its UI cancellation state.
            }
            catch (Exception ex)
            {
                operationError = ex;
                outcome = ConversionOutcome.Failed;
                LogSafely(ex);
            }
        }
        catch (Exception ex)
        {
            operationError ??= ex;
            outcome = ConversionOutcome.Failed;
            LogSafely(ex);
        }
        finally
        {
            NativeDiagnostics.Log.ConversionStage(taskId.ToString("N"), outcome.ToString().ToLowerInvariant(),
                outcome == ConversionOutcome.Completed ? 100 : 0);
            powerRequest?.Dispose();
            if (gateAcquired)
            {
                try
                {
                    ReleaseSlot(state);
                }
                catch (Exception ex)
                {
                    operationError ??= ex;
                    LogSafely(ex);
                }
            }

            await CompleteOperationAsync(
                taskId,
                state,
                operationError,
                outcome,
                operationCompleted,
                batchCompleted);
        }
    }

    private async Task CompleteOperationAsync(
        Guid taskId,
        OperationState state,
        Exception? operationError,
        ConversionOutcome outcome,
        Func<ConversionQueueOperationCompleted, Task> operationCompleted,
        Func<Task> batchCompleted)
    {
        bool removeRequested;
        lock (_syncRoot)
        {
            _executingOperations.Remove(taskId);
            removeRequested = _removeAfterOperation.Remove(taskId);
            _completionCallbacksInProgress++;
        }

        try
        {
            await operationCompleted(new ConversionQueueOperationCompleted(
                taskId,
                removeRequested,
                operationError,
                outcome));
        }
        catch (Exception ex)
        {
            LogSafely(ex);
        }

        bool notifyBatch;
        lock (_syncRoot)
        {
            _completionCallbacksInProgress--;
            notifyBatch =
                _batchWasStarted &&
                _executingOperations.Count == 0 &&
                _completionCallbacksInProgress == 0;
            if (notifyBatch)
            {
                _batchWasStarted = false;
            }
        }

        if (notifyBatch)
        {
            await InvokeSafelyAsync(batchCompleted);
        }

        lock (_syncRoot)
        {
            _operations.Remove(state);
            // Publish completion while holding the same lock used by WaitForAllAsync.
            // A waiter therefore either observes this operation and its completion task,
            // or observes it as fully removed after every callback has finished.
            state.Complete();
        }
        state.Dispose();
    }

    private static async Task InvokeSafelyAsync(Func<Task> callback)
    {
        try
        {
            await callback();
        }
        catch (Exception ex)
        {
            LogSafely(ex);
        }
    }

    private static async Task ObserveRunnerAsync(Task runner)
    {
        try
        {
            await runner;
        }
        catch (Exception ex)
        {
            LogSafely(ex);
        }
    }

    private static void LogSafely(Exception exception)
    {
        try
        {
            LogService.WriteAppError(exception);
        }
        catch
        {
            // Logging must never create a second unobserved failure.
        }
    }

    private async Task AcquireSlotAsync(OperationState state)
    {
        var token = state.Token;
        var waiter = state.Admission;
        using var registration = token.Register(() =>
        {
            lock (_syncRoot)
            {
                waiter.TrySetCanceled(token);
                AdmitWaitingOperations();
            }
        });
        await waiter.Task;
    }

    private void ReleaseSlot(OperationState state)
    {
        lock (_syncRoot)
        {
            _runningCount--;
            _runningOperations.Remove(state);
            AdmitWaitingOperations();
        }
    }

    // Called under _syncRoot. Lower limits leave running jobs intact; only admission changes.
    private void AdmitWaitingOperations()
    {
        ConversionResourceSnapshot resources;
        try { resources = _resourceSnapshot(); }
        catch { resources = ConversionResourceSnapshot.Unknown; }
        while (_admissionQueue.Count > 0)
        {
            var next = _admissionQueue[0];
            if (next.Admission.Task.IsCompleted) { _admissionQueue.RemoveAt(0); continue; }
            if (next.Token.IsCancellationRequested)
            {
                next.Admission.TrySetCanceled(next.Token);
                _admissionQueue.RemoveAt(0);
                continue;
            }
            if (_disposed || !_acceptingOperations || _orderPublicationsInProgress > 0 || _runningCount >= _desiredParallelism) break;
            var running = _runningOperations.Select(state => state.Workload).ToArray();
            if (!ConversionAdmissionPolicy.CanAdmit(next.Workload, running, _desiredParallelism, resources)) break;
            _admissionQueue.RemoveAt(0);
            if (next.Admission.TrySetResult(true))
            {
                next.IsAdmitted = true;
                _runningOperations.Add(next);
                _runningCount++;
            }
        }
        // Pressure can ease before the current job finishes. Poll only while there
        // are pending jobs and a running operation; no UI thread or task is blocked.
        if (!_disposed && _acceptingOperations && _admissionQueue.Count > 0 &&
            _runningCount > 0 && _runningCount < _desiredParallelism)
        {
            _admissionTimer ??= new Timer(_ => RecheckAdmission());
            _admissionTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        else _admissionTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void RecheckAdmission()
    {
        lock (_syncRoot)
        {
            if (!_disposed) AdmitWaitingOperations();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
