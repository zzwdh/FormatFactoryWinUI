namespace FormatFactoryWinUI.Services;

/// <summary>Tracks a conversion from input preflight through output commit, including gaps between processes.</summary>
internal sealed class MediaConversionOperations
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Operation> _active = [];

    internal bool HasActiveTasks
    {
        get { lock (_gate) return _active.Count != 0; }
    }

    internal Operation Begin(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_active.ContainsKey(id)) throw new InvalidOperationException("The media conversion is already running.");
            var operation = new Operation(this, id, cancellationToken);
            _active.Add(id, operation);
            return operation;
        }
    }

    internal FfmpegProcessRunner? FindRunner(Guid id)
    {
        lock (_gate) return _active.GetValueOrDefault(id)?.Runner;
    }

    internal void Cancel(Guid id)
    {
        lock (_gate) _active.GetValueOrDefault(id)?.RequestCancellation();
    }

    internal void CancelAll()
    {
        lock (_gate)
        {
            foreach (var operation in _active.Values) operation.RequestCancellation();
        }
    }

    internal sealed class Operation(MediaConversionOperations owner, Guid id, CancellationToken parent) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(parent);
        private Task? _cancellation;
        private bool _finishing;
        internal CancellationToken Token => _source.Token;
        internal FfmpegProcessRunner Runner { get; } = new();

        // Called under the registry lock. CancelAsync publishes the signal synchronously
        // and schedules user callbacks outside the lock.
        internal void RequestCancellation()
        {
            if (!_finishing) _cancellation ??= _source.CancelAsync();
        }

        public async ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                if (_finishing) return;
                _finishing = true;
            }
            try
            {
                if (_cancellation is { } pending) await pending.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SafeDiagnostics.Report(exception);
            }
            finally
            {
                _source.Dispose();
                // A retry with this ID can start only after the old callbacks have drained.
                lock (owner._gate) owner._active.Remove(id);
            }
        }
    }
}
