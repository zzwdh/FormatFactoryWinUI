namespace FormatFactoryWinUI.Services;

public enum StoreUpdateStatus
{
    Idle,
    Checking,
    Downloading,
    Ready,
    UpToDate,
    Unavailable,
    NeedsStore,
    Failed,
    Disabled,
    Installing
}

public sealed record StoreUpdateCheckResult(bool HasUpdates, bool CanDownloadSilently);

public interface IStoreUpdateClient
{
    bool IsAvailable { get; }
    Task<StoreUpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
    Task<bool> DownloadAsync(CancellationToken cancellationToken);
    Task<bool> InstallAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Downloads Store updates without interrupting work and applies completed downloads only on exit.
/// The client owns the Store package list; all access to it is serialized by this coordinator.
/// </summary>
public sealed class StoreUpdateCoordinator(IStoreUpdateClient client) : IDisposable
{
    private readonly IStoreUpdateClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly object _sync = new();
    private Operation? _active;
    private StoreUpdateStatus _status = StoreUpdateStatus.Disabled;
    private bool _enabled;
    private bool _hasDownloadedUpdates;
    private bool _disposed;

    public event Action<StoreUpdateStatus>? StatusChanged;

    public StoreUpdateStatus Status
    {
        get { lock (_sync) return _status; }
    }

    public bool IsBusy
    {
        get { lock (_sync) return _active is not null; }
    }

    public bool EnableAutomaticUpdates
    {
        get { lock (_sync) return _enabled; }
        set => Configure(value);
    }

    public void Configure(bool enabled)
    {
        lock (_sync)
        {
            if (_disposed || _enabled == enabled)
                return;

            _enabled = enabled;
            if (!enabled && _active is { RequiresAutomaticUpdates: true } operation)
                Cancel(operation);

            // Manual queries are still allowed when the download/install preference is off.
            if (_active is { RequiresAutomaticUpdates: false })
                return;

            if (!enabled)
                SetStatusLocked(StoreUpdateStatus.Disabled);
            else if (_active is null || _active.Token.IsCancellationRequested)
                SetStatusLocked(_hasDownloadedUpdates ? StoreUpdateStatus.Ready : StoreUpdateStatus.Idle);
        }
    }

    /// <summary>Busy checks return immediately; manual queries do not depend on the preference.</summary>
    public Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        Operation operation;
        lock (_sync)
        {
            if (_disposed || _active is not null || cancellationToken.IsCancellationRequested || (!manual && !_enabled))
                return Task.CompletedTask;

            // Preserve the client's downloaded package list until it is installed or this session ends.
            if (_hasDownloadedUpdates)
            {
                SetStatusLocked(_enabled ? StoreUpdateStatus.Ready : StoreUpdateStatus.NeedsStore);
                return Task.CompletedTask;
            }

            operation = new Operation(manual, cancellationToken);
            _active = operation;
            SetStatusLocked(StoreUpdateStatus.Checking);
        }

        return RunCheckAsync(operation);
    }

    /// <summary>Never throws: a failed update must not prevent the application from closing.</summary>
    public Task<bool> TryInstallOnExitAsync(CancellationToken cancellationToken = default)
    {
        Operation operation;
        lock (_sync)
        {
            if (_disposed || !_enabled || !_hasDownloadedUpdates || _active is not null || cancellationToken.IsCancellationRequested)
                return Task.FromResult(false);

            operation = new Operation(isManual: false, cancellationToken);
            _active = operation;
            SetStatusLocked(StoreUpdateStatus.Installing);
        }

        return RunInstallAsync(operation);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _enabled = false;
            StatusChanged = null;
            if (_active is { } operation)
                Cancel(operation);
        }
    }

    private async Task RunCheckAsync(Operation operation)
    {
        try
        {
            if (!IsClientAvailable(operation))
            {
                SetStatus(operation, StoreUpdateStatus.Unavailable);
                return;
            }

            var result = await InvokeClientAsync(operation, _client.CheckAsync).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (!result.HasUpdates)
            {
                SetStatus(operation, StoreUpdateStatus.UpToDate);
                return;
            }

            lock (_sync)
            {
                if (!CanContinueLocked(operation))
                    return;
                if (!result.CanDownloadSilently || !_enabled)
                {
                    SetStatusLocked(StoreUpdateStatus.NeedsStore);
                    return;
                }

                // A manual query becomes preference-dependent before any download starts.
                operation.RequiresAutomaticUpdates = true;
            }

            if (!SetStatus(operation, StoreUpdateStatus.Downloading))
                return;

            var downloaded = await InvokeClientAsync(operation, _client.DownloadAsync).ConfigureAwait(false);
            lock (_sync)
            {
                if (!CanContinueLocked(operation))
                    return;

                _hasDownloadedUpdates = downloaded;
                SetStatusLocked(downloaded ? StoreUpdateStatus.Ready : StoreUpdateStatus.Failed);
            }
        }
        catch (OperationCanceledException)
        {
            RestoreAfterCancellation(operation);
        }
        catch (Exception)
        {
            if (operation.Token.IsCancellationRequested)
                RestoreAfterCancellation(operation);
            else
                SetStatus(operation, StoreUpdateStatus.Failed);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private async Task<bool> RunInstallAsync(Operation operation)
    {
        try
        {
            if (!IsClientAvailable(operation))
            {
                SetStatus(operation, StoreUpdateStatus.Unavailable);
                return false;
            }

            var installed = await InvokeClientAsync(operation, _client.InstallAsync).ConfigureAwait(false);
            lock (_sync)
            {
                if (!CanContinueLocked(operation))
                    return false;

                if (installed)
                    _hasDownloadedUpdates = false;
                SetStatusLocked(installed ? StoreUpdateStatus.UpToDate : StoreUpdateStatus.Failed);
                return installed;
            }
        }
        catch (OperationCanceledException)
        {
            RestoreAfterCancellation(operation);
            return false;
        }
        catch (Exception)
        {
            if (operation.Token.IsCancellationRequested)
                RestoreAfterCancellation(operation);
            else
                SetStatus(operation, StoreUpdateStatus.Failed);
            return false;
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private bool IsClientAvailable(Operation operation)
    {
        lock (_sync)
        {
            EnsureCanContinueLocked(operation);
            return _client.IsAvailable;
        }
    }

    private Task<T> InvokeClientAsync<T>(Operation operation, Func<CancellationToken, Task<T>> action)
    {
        lock (_sync)
        {
            // Starting client work and disabling the preference share a lock, so an automatic
            // download or installation cannot begin after Configure(false) has returned.
            EnsureCanContinueLocked(operation);
            return action(operation.Token);
        }
    }

    private bool CanContinueLocked(Operation operation) =>
        !_disposed && ReferenceEquals(_active, operation) && !operation.Token.IsCancellationRequested
        && (!operation.RequiresAutomaticUpdates || _enabled);

    private void EnsureCanContinueLocked(Operation operation)
    {
        if (!CanContinueLocked(operation))
            throw new OperationCanceledException(operation.Token);
    }

    private bool SetStatus(Operation operation, StoreUpdateStatus status)
    {
        lock (_sync)
        {
            if (!CanContinueLocked(operation))
                return false;
            SetStatusLocked(status);
            return CanContinueLocked(operation);
        }
    }

    private void RestoreAfterCancellation(Operation operation)
    {
        lock (_sync)
        {
            if (!_disposed && ReferenceEquals(_active, operation))
                SetStatusLocked(!_enabled ? StoreUpdateStatus.Disabled
                    : _hasDownloadedUpdates ? StoreUpdateStatus.Ready : StoreUpdateStatus.Idle);
        }
    }

    private void EndOperation(Operation operation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_active, operation))
            {
                _active = null;
                if (operation.Token.IsCancellationRequested)
                    SetStatusLocked(!_enabled ? StoreUpdateStatus.Disabled
                        : _hasDownloadedUpdates ? StoreUpdateStatus.Ready : StoreUpdateStatus.Idle, notifyEvenIfUnchanged: true);
                else
                    SetStatusLocked(_status, notifyEvenIfUnchanged: true);
            }
        }
        operation.Cancellation.Dispose();
    }

    private void SetStatusLocked(StoreUpdateStatus status, bool notifyEvenIfUnchanged = false)
    {
        if (_disposed || (!notifyEvenIfUnchanged && _status == status))
            return;

        _status = status;
        // Dispatch while synchronized to make Dispose a boundary for callbacks. Consumers may
        // marshal this event to their UI dispatcher; event failures never fail an update operation.
        if (StatusChanged is not { } changed)
            return;
        foreach (Action<StoreUpdateStatus> subscriber in changed.GetInvocationList())
        {
            if (_disposed || _status != status)
                break;
            try { subscriber(status); }
            catch (Exception) { }
        }
    }

    private static void Cancel(Operation operation)
    {
        try { operation.Cancellation.Cancel(); }
        catch (Exception) { }
    }

    private sealed class Operation
    {
        public Operation(bool isManual, CancellationToken cancellationToken)
        {
            RequiresAutomaticUpdates = !isManual;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Token = Cancellation.Token;
        }

        public bool RequiresAutomaticUpdates { get; set; }
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token { get; }
    }
}
