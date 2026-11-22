using System.Text;
using System.Threading.Channels;

namespace FormatFactoryWinUI.Services;

/// <summary>Bounded, ordered background appends with explicit completion barriers.</summary>
internal sealed class BufferedLogWriter : IDisposable
{
    private const int ChunkLength = 8 * 1024;
    private readonly object _submissionGate = new();
    private readonly Channel<Request> _requests;
    private readonly Action<string, string> _append;
    private readonly Task _worker;
    private Exception? _lastWriteError;
    private bool _disposed;

    internal BufferedLogWriter(int capacity = 256, Action<string, string>? append = null, LogFilePolicy? policy = null)
    {
        _requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _append = append ?? (policy ?? LogFilePolicy.Shared).AppendText;
        _worker = Task.Run(DrainAsync);
    }

    public Exception? LastWriteError => Volatile.Read(ref _lastWriteError);

    public void Write(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        lock (_submissionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Backpressure bounds queued text even for exceptionally large messages.
            for (var offset = 0; offset < content.Length;)
            {
                var length = Math.Min(ChunkLength, content.Length - offset);
                if (offset + length < content.Length && char.IsHighSurrogate(content[offset + length - 1]))
                {
                    length--;
                }
                Enqueue(new Request(path, content.Substring(offset, length), null));
                offset += length;
            }
        }
    }

    public void Flush(string? path = null)
    {
        Task completion;
        lock (_submissionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            completion = EnqueueBarrier(path);
        }
        completion.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Task completion;
        lock (_submissionGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            completion = EnqueueBarrier(null);
            _requests.Writer.TryComplete();
        }
        try
        {
            completion.GetAwaiter().GetResult();
        }
        finally
        {
            _worker.GetAwaiter().GetResult();
        }
    }

    private void Enqueue(Request request) =>
        _requests.Writer.WriteAsync(request).AsTask().GetAwaiter().GetResult();

    private Task EnqueueBarrier(string? path)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new Request(path, null, completion));
        return completion.Task;
    }

    private async Task DrainAsync()
    {
        var failures = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
        while (await _requests.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
            Request? barrier = null;
            for (var count = 0; count < 128 && _requests.Reader.TryRead(out var request); count++)
            {
                if (request.Completion is not null)
                {
                    barrier = request;
                    break;
                }
                if (!batch.TryGetValue(request.Path!, out var buffer))
                {
                    batch[request.Path!] = buffer = new StringBuilder();
                }
                buffer.Append(request.Content);
            }

            foreach (var (path, buffer) in batch)
            {
                try
                {
                    _append(path, buffer.ToString());
                }
                catch (Exception ex)
                {
                    // A failure is observed at the next matching barrier; later
                    // writes can still succeed after the underlying issue is fixed.
                    failures.TryAdd(path, ex);
                    Volatile.Write(ref _lastWriteError, ex);
                    System.Diagnostics.Debug.WriteLine($"Log write failed ({path}): {ex}");
                }
            }

            if (barrier is not null)
            {
                var affected = failures.Where(pair => barrier.Path is null ||
                    pair.Key.Equals(barrier.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var pair in affected)
                {
                    failures.Remove(pair.Key);
                }
                if (affected.Length > 0)
                {
                    barrier.Completion!.TrySetException(new IOException(
                        "One or more submitted log records could not be written.",
                        new AggregateException(affected.Select(pair => pair.Value))));
                }
                else
                {
                    barrier.Completion!.TrySetResult();
                }
            }
        }
    }

    private sealed record Request(string? Path, string? Content, TaskCompletionSource? Completion);
}
