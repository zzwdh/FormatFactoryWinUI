using System.ComponentModel;
using System.Text.Json;

namespace FormatFactoryWinUI.Services;

public enum DirectoryChangeKind
{
    Added,
    Removed,
    Modified,
    Renamed
}

public sealed class DirectoryChangeEventArgs : EventArgs
{
    public DirectoryChangeEventArgs(
        DirectoryChangeKind kind,
        string fullPath,
        string? oldFullPath = null)
    {
        Kind = kind;
        FullPath = fullPath;
        OldFullPath = oldFullPath;
    }

    public DirectoryChangeKind Kind { get; }
    public string FullPath { get; }
    public string? OldFullPath { get; }
}

/// <summary>
/// ReadDirectoryChangesW-based watcher used for high-volume watch folders.
/// It keeps rename pairs together and can be cancelled without a window message pump.
/// </summary>
public sealed class NativeDirectoryWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private readonly IDirectoryChangeBackend _backend;
    private CancellationTokenSource? _cancellation;
    private Task? _reader;
    private SynchronizationContext? _eventContext;
    private string? _rootPath;
    private bool _running;
    private bool _disposed;

    public NativeDirectoryWatcher()
        : this(new ReadDirectoryChangesBackend())
    {
    }

    internal NativeDirectoryWatcher(IDirectoryChangeBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public event EventHandler<DirectoryChangeEventArgs>? Changed;
    public event EventHandler<ErrorEventArgs>? Error;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public bool Start(string folderPath, bool includeSubdirectories = false)
    {
        lock (_lifecycle) return StartCore(folderPath, includeSubdirectories);
    }

    private bool StartCore(string folderPath, bool includeSubdirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        StopCore();

        var root = Path.GetFullPath(folderPath);
        if (!Directory.Exists(root))
        {
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _backend.Open(root, includeSubdirectories);
            }
            catch (Exception exception)
            {
                DispatchError(exception);
                return false;
            }

            _rootPath = root;
            _eventContext = SynchronizationContext.Current;
            _cancellation = new CancellationTokenSource();
            _running = true;
            var cancellationToken = _cancellation.Token;
            _reader = Task.Factory.StartNew(
                () => ReadLoop(cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            NativeDiagnostics.Log.ServiceStarted(nameof(NativeDirectoryWatcher));
            return true;
        }
    }

    public void Stop()
    {
        lock (_lifecycle) StopCore();
    }

    private void StopCore()
    {
        CancellationTokenSource? cancellation;
        Task? reader;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            cancellation = _cancellation;
            reader = _reader;
            _cancellation = null;
            _reader = null;
            _rootPath = null;
            _eventContext = null;
        }

        cancellation?.Cancel();
        _backend.Cancel();
        try
        {
            if (reader is not null && reader.Id != Task.CurrentId) reader.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(item => item is OperationCanceledException))
        {
        }
        finally
        {
            cancellation?.Dispose();
            _backend.Close();
        }
        NativeDiagnostics.Log.ServiceStopped(nameof(NativeDirectoryWatcher));
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            StopCore();
            _backend.Dispose();
        }
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        string? oldRenamePath = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var changes = _backend.Read(cancellationToken);
                foreach (var change in changes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var root = GetRootPath();
                    if (root is null)
                    {
                        return;
                    }

                    var fullPath = Path.GetFullPath(Path.Combine(root, change.RelativePath));
                    switch (change.Action)
                    {
                        case NativeDirectoryChangeAction.Added:
                            DispatchChange(new DirectoryChangeEventArgs(
                                DirectoryChangeKind.Added,
                                fullPath));
                            break;
                        case NativeDirectoryChangeAction.Removed:
                            DispatchChange(new DirectoryChangeEventArgs(
                                DirectoryChangeKind.Removed,
                                fullPath));
                            break;
                        case NativeDirectoryChangeAction.Modified:
                            DispatchChange(new DirectoryChangeEventArgs(
                                DirectoryChangeKind.Modified,
                                fullPath));
                            break;
                        case NativeDirectoryChangeAction.RenamedOldName:
                            oldRenamePath = fullPath;
                            break;
                        case NativeDirectoryChangeAction.RenamedNewName:
                            DispatchChange(new DirectoryChangeEventArgs(
                                DirectoryChangeKind.Renamed,
                                fullPath,
                                oldRenamePath));
                            oldRenamePath = null;
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            try { DispatchError(exception); }
            finally
            {
                lock (_gate)
                {
                    // A handler can stop or restart the watcher. Only this subscription
                    // owns the backend and cancellation source while its token matches.
                    if (_cancellation?.Token == cancellationToken)
                    {
                        _running = false;
                        _reader = null;
                        _rootPath = null;
                        _eventContext = null;
                        _cancellation.Dispose();
                        _cancellation = null;
                        _backend.Close();
                        NativeDiagnostics.Log.ServiceStopped(nameof(NativeDirectoryWatcher));
                    }
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
    }

    private string? GetRootPath()
    {
        lock (_gate)
        {
            return _running ? _rootPath : null;
        }
    }

    private void DispatchChange(DirectoryChangeEventArgs args)
    {
        NativeDiagnostics.Log.DirectoryChanged(args.Kind.ToString(), args.FullPath);
        Dispatch(Changed, args);
    }

    private void DispatchError(Exception exception) =>
        Dispatch(Error, new ErrorEventArgs(exception));

    private void Dispatch<TEventArgs>(EventHandler<TEventArgs>? handler, TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handler is null)
        {
            return;
        }

        SynchronizationContext? context;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            context = _eventContext;
        }

        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            handler(this, args);
            return;
        }

        context.Post(static state =>
        {
            var dispatch = (DirectoryEventDispatch<TEventArgs>)state!;
            dispatch.Handler(dispatch.Sender, dispatch.Args);
        }, new DirectoryEventDispatch<TEventArgs>(this, handler, args));
    }

    private sealed record DirectoryEventDispatch<TEventArgs>(
        NativeDirectoryWatcher Sender,
        EventHandler<TEventArgs> Handler,
        TEventArgs Args)
        where TEventArgs : EventArgs;
}

internal enum NativeDirectoryChangeAction : uint
{
    Added = 1,
    Removed = 2,
    Modified = 3,
    RenamedOldName = 4,
    RenamedNewName = 5
}

internal readonly record struct NativeDirectoryChange(
    NativeDirectoryChangeAction Action,
    string RelativePath);

internal interface IDirectoryChangeBackend : IDisposable
{
    void Open(string folderPath, bool includeSubdirectories);

    IReadOnlyList<NativeDirectoryChange> Read(CancellationToken cancellationToken);

    void Cancel();

    void Close();
}

internal sealed class ReadDirectoryChangesBackend : IDirectoryChangeBackend
{
    private readonly object _gate = new();
    private ExternalProcessLifetime? _session;
    private Task<CapturedProcessOutput>? _errors;
    private bool _disposed;

    public void Open(string folderPath, bool includeSubdirectories)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Close();
            var session = ConversionBackend.Start("watch-directory", CancellationToken.None);
            try
            {
                var process = session.Process;
                var errors = BoundedProcessOutput.ReadAsync(process.StandardError.BaseStream, "stderr", 1024, requireComplete: false);
                process.StandardInput.WriteLine(JsonSerializer.Serialize(new { path = folderPath, subtree = includeSubdirectories }));
                process.StandardInput.Flush();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var ready = process.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
                if (ready != "ready")
                    throw Failure(errors.GetAwaiter().GetResult().Text,
                        LocalizationService.T("GeneralServiceCannotWatchFolder", folderPath));
                _session = session;
                _errors = errors;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
    }

    public IReadOnlyList<NativeDirectoryChange> Read(CancellationToken token)
    {
        ExternalProcessLifetime session;
        Task<CapturedProcessOutput> errors;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            session = _session ?? throw new InvalidOperationException(LocalizationService.T("GeneralServiceWatcherNotOpen"));
            errors = _errors!;
        }
        var line = session.Process.StandardOutput.ReadLineAsync(token).AsTask().GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        if (line is null)
            throw Failure(errors.GetAwaiter().GetResult().Text, LocalizationService.T("GeneralServiceDirectoryWatchReadFailed"));
        return JsonSerializer.Deserialize<NativeDirectoryChange[]>(line)
            ?? throw new InvalidDataException(LocalizationService.T("GeneralServiceDirectoryWatchInvalidRecord"));
    }

    private static Exception Failure(string line, string message)
    {
        return line.Trim().Split(' ') switch
        {
            ["error", "io", var number] when int.TryParse(number, out var code) => new Win32Exception(code, message),
            ["error", "GeneralServiceDirectoryWatchStartFailed", var number] when int.TryParse(number, out var code)
                => new Win32Exception(code, LocalizationService.T("GeneralServiceDirectoryWatchStartFailed")),
            ["error", var key] when key is "GeneralServiceDirectoryWatchInvalidOffset" or "GeneralServiceDirectoryWatchInvalidRecord"
                => new InvalidDataException(LocalizationService.T(key)),
            _ => new IOException(message)
        };
    }

    public void Cancel()
    {
        lock (_gate) _session?.Process.StandardInput.Close();
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_session is not { } session) return;
            _session = null;
            _errors = null;
            try
            {
                session.Process.StandardInput.Close();
                // EOF releases the native subscription even with no directory activity.
                session.Exited.Wait(TimeSpan.FromSeconds(2));
            }
            finally { session.Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Close();
        }
    }
}
