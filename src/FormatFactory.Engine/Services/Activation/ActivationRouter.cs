using System.Threading.Channels;

namespace FormatFactoryWinUI.Services;

/// <summary>
/// Preserves activation arrival order and ensures only one activation mutates the UI at a time.
/// The handler can be attached after startup, so early notification and redirection events are retained.
/// </summary>
internal sealed class ActivationRouter : IAsyncDisposable
{
    private readonly Channel<Func<Task<ActivationRequest>>> _queue = Channel.CreateUnbounded<Func<Task<ActivationRequest>>>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<bool> _handlerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _handlerGate = new();
    private readonly Task _worker;
    private Func<ActivationRequest, Task>? _handler;
    private bool _disposed;

    public ActivationRouter()
    {
        _worker = ProcessAsync();
    }

    public event EventHandler<Exception>? RoutingFailed;

    public void Attach(Func<ActivationRequest, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_handlerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handler is not null)
            {
                throw new InvalidOperationException("Activation handler is already attached.");
            }

            _handler = handler;
            _handlerReady.TrySetResult(true);
        }
    }

    public bool Enqueue(ActivationRequest request) =>
        request.HasWork && Enqueue(() => Task.FromResult(request));

    public bool Enqueue(Func<Task<ActivationRequest>> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        lock (_handlerGate)
        {
            return !_disposed && _queue.Writer.TryWrite(requestFactory);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_handlerGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.Writer.TryComplete();
            _shutdown.Cancel();
        }

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await _handlerReady.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            await foreach (var requestFactory in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                ActivationRequest? request = null;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    request = await requestFactory().ConfigureAwait(false);
                    if (!request.HasWork)
                    {
                        request.Delivery?.Write(ActivationDeliveryStage.NoWork, primary: true, routedWork: false);
                        continue;
                    }

                    Func<ActivationRequest, Task> handler;
                    lock (_handlerGate)
                    {
                        handler = _handler!;
                    }

                    await handler(request).ConfigureAwait(false);
                    request.Lifetime?.Complete();
                    request.Delivery?.Write(ActivationDeliveryStage.DispatchCompleted, primary: true, routedWork: true);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (request?.Delivery is { } delivery)
                    {
                        RuntimeFailureDiagnostics.Record(ex, new(RuntimeFailureStage.ActivationDispatch, delivery.Operation, 1,
                            (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                        delivery.Write(ActivationDeliveryStage.DispatchFailed, primary: true, routedWork: request.HasWork, error: ex);
                    }
                    request?.Lifetime?.Fail("The activation could not be processed.");
                    RoutingFailed?.Invoke(this, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }
}
