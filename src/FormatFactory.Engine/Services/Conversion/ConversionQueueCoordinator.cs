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

    private sealed class OperationState(SchedulingCandidate demand) : IDisposable
    {
        private readonly object _syncRoot = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _cancelling;
        private bool _disposed;

        public CancellationToken Token => _cancellation.Token;
        public Task Completion => _completion.Task;
        public Guid TaskId => Demand.Id;
        public SchedulingCandidate Demand { get; } = demand;
        public ConversionWorkload? Reservation { get; set; }
        public LinkedListNode<OperationState>? PendingNode { get; set; }
        public TaskCompletionSource<bool> Admission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Accessed only under the coordinator lock. A reserved slot is already running
        // for ordering purposes, even before its delegate publishes a UI state change.
        public bool IsAdmitted { get; set; }
        public bool IsCancellationPending { get; set; }

        public void Cancel(CancellationRequest request)
        {
            lock (_syncRoot)
            {
                if (_disposed || _cancelling || _cancellation.IsCancellationRequested) return;
                _cancelling = true;
            }
            var requestedAt = DateTimeOffset.UtcNow;
            try { _cancellation.Cancel(); }
            catch (Exception error) { LogSafely(error); }
            finally
            {
                lock (_syncRoot)
                {
                    _cancelling = false;
                    if (_disposed) _cancellation.Dispose();
                }
                SafeDiagnostics.Write(FormattableString.Invariant(
                    $"Queue cancellation request: session={RuntimeFailureDiagnostics.SessionId:N} task={TaskId:N} reason={request} requested_utc={requestedAt:O}"));
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
                if (!_cancelling) _cancellation.Dispose();
            }
        }
    }

    private readonly object _syncRoot = new();
    // Keep each operation until its callbacks finish, even if the same task restarts.
    private readonly HashSet<OperationState> _operations = [];
    private readonly Dictionary<Guid, OperationState> _executingOperations = [];
    private readonly HashSet<Guid> _removeAfterOperation = [];
    private readonly LinkedList<OperationState> _admissionQueue = new();
    private readonly HashSet<OperationState> _runningOperations = [];
    private readonly Func<ConversionResourceSnapshot> _resourceSnapshot;
    private Timer? _admissionTimer;

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
        if (orderedTaskIds.Count != orderedTaskIds.Distinct().Count()) throw new ArgumentException("Repeated pending task ID.", nameof(orderedTaskIds));
        lock (_syncRoot)
        {
            if (_disposed || !_acceptingOperations || _orderPublicationsInProgress != 0) return false;
            var unavailable = _executingOperations.Values.Where(state => state.IsAdmitted ||
                state.IsCancellationPending || state.Token.IsCancellationRequested).Select(state => state.TaskId);
            var decision = ConversionScheduler.Schedule(_admissionQueue.Select(state => state.Demand), [],
                _desiredParallelism, ConversionResourceSnapshot.Unknown, orderedTaskIds, unavailable);
            if (!decision.Accepted) return false;
            var order = decision.Ordered ?? throw new InvalidDataException("Missing scheduler order.");
            if (order.Length != _admissionQueue.Count || order.Distinct().Count() != order.Length ||
                order.Any(id => !_executingOperations.TryGetValue(id, out var state) || state.PendingNode is null))
                throw new InvalidDataException("Unexpected scheduler order.");
            var nodes = order.Select(id => _executingOperations[id].PendingNode!).ToArray();
            _orderPublicationsInProgress++;
            try
            {
                publishOrder();
                // Callbacks may have cancelled rows or appended new ones. Publish
                // the accepted order of surviving rows ahead of those new arrivals.
                for (var i = nodes.Length - 1; i >= 0; i--)
                {
                    var node = nodes[i];
                    if (node.List is null) continue;
                    _admissionQueue.Remove(node);
                    _admissionQueue.AddFirst(node);
                }
            }
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

    public bool TryStartWithOutcome(Guid taskId, Func<CancellationToken, Task<ConversionOutcome>> operation,
        Func<Task> cancelledBeforeStart, Func<ConversionQueueOperationCompleted, Task> operationCompleted,
        Func<Task> batchCompleted, ConversionWorkload? workload = null) =>
        Start(new(taskId, workload ?? ConversionWorkload.Unspecified, null), operation, cancelledBeforeStart, operationCompleted, batchCompleted);

    internal bool TryStartPlanned(Guid taskId, Func<CancellationToken, Task<ConversionOutcome>> operation,
        Func<Task> cancelledBeforeStart, Func<ConversionQueueOperationCompleted, Task> operationCompleted,
        Func<Task> batchCompleted, WorkloadEstimate estimate) =>
        Start(new(taskId, null, estimate), operation, cancelledBeforeStart, operationCompleted, batchCompleted);

    private bool Start(SchedulingCandidate demand, Func<CancellationToken, Task<ConversionOutcome>> operation,
        Func<Task> cancelledBeforeStart, Func<ConversionQueueOperationCompleted, Task> operationCompleted, Func<Task> batchCompleted)
    {
        var taskId = demand.Id;
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

            state = new OperationState(demand);
            _operations.Add(state);
            _executingOperations.Add(taskId, state);
            _removeAfterOperation.Remove(taskId);
            _batchWasStarted = true;
            state.PendingNode = _admissionQueue.AddLast(state);
            if (_admissionQueue.Count == 1) AdmitWaitingOperations();
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
            if (state is not null) MarkCancellationPending(state);
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
            MarkCancellationPending(state);
        }

        // Unlinking and cancellation intent are visible before callbacks run.
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
            MarkCancellationPending(state);
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
            foreach (var state in states) MarkCancellationPending(state);
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
            foreach (var state in states) MarkCancellationPending(state);
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

            _runningOperations.Remove(state);
            AdmitWaitingOperations();
        }
    }

    private void RemovePending(OperationState state)
    {
        if (state.PendingNode is not { } node) return;
        _admissionQueue.Remove(node);
        state.PendingNode = null;
    }

    // Mark and unlink under the slot lock before invoking user cancellation callbacks outside it.
    private void MarkCancellationPending(OperationState state)
    {
        state.IsCancellationPending = true;
        RemovePending(state);
    }

    private void AdmitWaitingOperations()
    {
        if (_disposed || !_acceptingOperations || _orderPublicationsInProgress != 0) return;
        if (_admissionQueue.Count != 0 && _runningOperations.Count < _desiredParallelism)
        {
            ConversionResourceSnapshot resources;
            try { resources = _resourceSnapshot(); }
            catch { resources = ConversionResourceSnapshot.Unknown; }

            // At most four operations can be granted. Later FIFO entries cannot
            // participate until one of these starts or is removed. This keeps ordinary
            // slot releases bounded even for queues with many thousands of rows.
            try
            {
                var decision = ConversionScheduler.Schedule(_admissionQueue.Take(MaximumParallelism).Select(state => state.Demand),
                    _runningOperations.Select(state => state.Reservation!), _desiredParallelism, resources);
                if (!decision.Accepted || decision.Grants.Length > _desiredParallelism - _runningOperations.Count ||
                    decision.Grants.Select(g => g.Id).Distinct().Count() != decision.Grants.Length ||
                    decision.Grants.Any(g => !_executingOperations.TryGetValue(g.Id, out var state) || state.PendingNode is null))
                    throw new InvalidDataException("Unexpected scheduler admission.");
                foreach (var grant in decision.Grants)
                {
                    var state = _executingOperations[grant.Id];
                    RemovePending(state);
                    state.Reservation = grant.Cost;
                    state.IsAdmitted = true;
                    _runningOperations.Add(state);
                    state.Admission.TrySetResult(true);
                }
            }
            catch (Exception failure)
            {
                // A failed worker cannot authorize execution. Resolve every waiter
                // exactly once through the usual completion/error callback.
                while (_admissionQueue.First is { Value: var state })
                {
                    RemovePending(state);
                    state.Admission.TrySetException(failure);
                }
            }
        }
        if (_admissionQueue.Count > 0 && _runningOperations.Count > 0 && _runningOperations.Count < _desiredParallelism)
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
