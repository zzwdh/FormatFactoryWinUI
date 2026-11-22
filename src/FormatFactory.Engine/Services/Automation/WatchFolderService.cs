using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal sealed class WatchFolderService : IDisposable
{
    private readonly object _gate = new();
    private HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeDirectoryWatcher _watcher;
    private CancellationTokenSource? _watchCancellation;
    private string? _folderPath;
    private bool _disposed;

    public WatchFolderService()
        : this(new NativeDirectoryWatcher())
    {
    }

    internal WatchFolderService(NativeDirectoryWatcher watcher)
    {
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _watcher.Changed += Watcher_Changed;
        _watcher.Error += Watcher_Error;
    }

    public event EventHandler<string>? FileDiscovered;

    public bool IsWatching => _watcher.IsRunning;

    public void Start(string folderPath)
    {
        Stop();
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            _watchCancellation = new CancellationTokenSource();
            _watcher.Start(_folderPath);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _watchCancellation;
            _watchCancellation = null;
            _folderPath = null;
            // A previous wait must never remove a new configuration's reservation.
            _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        cancellation?.Cancel();
        _watcher.Stop();
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        Stop();
        _watcher.Changed -= Watcher_Changed;
        _watcher.Error -= Watcher_Error;
        _watcher.Dispose();
    }

    private void Watcher_Changed(object? sender, DirectoryChangeEventArgs args)
    {
        if (args.Kind is DirectoryChangeKind.Added or DirectoryChangeKind.Renamed)
        {
            Queue(args.FullPath);
        }
    }

    private void Queue(string path)
    {
        lock (_gate)
        {
            if (_disposed || _watchCancellation is null ||
                !string.Equals(Path.GetDirectoryName(path), _folderPath, StringComparison.OrdinalIgnoreCase) ||
                !FormatCatalog.SupportedExtensions.Contains(Path.GetExtension(path)) ||
                !_pending.Add(path))
            {
                return;
            }

            _ = WaitForFileAsync(path, _pending, _watchCancellation.Token);
        }
    }

    private async Task WaitForFileAsync(
        string path,
        HashSet<string> pending,
        CancellationToken cancellationToken)
    {
        try
        {
            (long Length, DateTime LastWriteUtc)? previousState = null;
            while (true)
            {
                await Task.Delay(500, cancellationToken);
                if (!File.Exists(path))
                {
                    return;
                }

                (long Length, DateTime LastWriteUtc) state;
                try
                {
                    // A copy can pause without changing length. Refuse existing
                    // writers so a temporary pause cannot import a partial file.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    state = (stream.Length, File.GetLastWriteTimeUtc(path));
                }
                catch (IOException)
                {
                    // Large files and temporary sharing locks can take longer
                    // than ten seconds. Keep waiting until stopped or removed.
                    previousState = null;
                    continue;
                }

                if (state.Length > 0 && state == previousState)
                {
                    lock (_gate)
                    {
                        if (!_disposed && ReferenceEquals(pending, _pending) &&
                            !cancellationToken.IsCancellationRequested)
                        {
                            FileDiscovered?.Invoke(this, path);
                        }
                    }
                    return;
                }
                previousState = state;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping or reconfiguring the watcher cancels pending discoveries.
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogWatchFolderFileReadFailed", path, ex.Message));
        }
        finally
        {
            lock (_gate)
            {
                pending.Remove(path);
            }
        }
    }

    private static void Watcher_Error(object? sender, ErrorEventArgs args) =>
        LogService.WriteApp(LocalizationService.T("OperationalLogWatchFolderError", args.GetException().Message));
}
