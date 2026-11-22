using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        Stop();

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
            reader?.Wait(TimeSpan.FromSeconds(2));
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
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        Stop();
        lock (_gate)
        {
            _disposed = true;
        }
        _backend.Dispose();
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
        catch (Exception exception)
        {
            DispatchError(exception);
        }
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
    private const int BufferSize = 64 * 1024;
    private readonly object _gate = new();
    private SafeFileHandle? _directory;
    private nint _activeOverlapped;
    private bool _includeSubdirectories;
    private bool _disposed;

    public void Open(string folderPath, bool includeSubdirectories)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CloseCore();
            var handle = CreateFile(
                folderPath,
                FileListDirectory,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOverlapped,
                0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error, LocalizationService.T("GeneralServiceCannotWatchFolder", folderPath));
            }

            _directory = handle;
            _includeSubdirectories = includeSubdirectories;
        }
    }

    public IReadOnlyList<NativeDirectoryChange> Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafeFileHandle directory;
        bool includeSubdirectories;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            directory = _directory
                ?? throw new InvalidOperationException(LocalizationService.T("GeneralServiceWatcherNotOpen"));
            includeSubdirectories = _includeSubdirectories;
        }

        var buffer = new byte[BufferSize];
        var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        using var completion = new EventWaitHandle(false, EventResetMode.AutoReset);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedData>());
        try
        {
            Marshal.StructureToPtr(
                new NativeOverlappedData
                {
                    EventHandle = completion.SafeWaitHandle.DangerousGetHandle()
                },
                overlapped,
                false);
            lock (_gate)
            {
                _activeOverlapped = overlapped;
            }

            var started = ReadDirectoryChangesW(
                directory,
                pinnedBuffer.AddrOfPinnedObject(),
                BufferSize,
                includeSubdirectories,
                NotifyChangeFileName | NotifyChangeDirectoryName |
                NotifyChangeSize | NotifyChangeLastWrite | NotifyChangeCreation,
                0,
                overlapped,
                0);
            if (!started)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorIoPending)
                {
                    throw new Win32Exception(error, LocalizationService.T("GeneralServiceDirectoryWatchStartFailed"));
                }
            }

            var signaled = WaitHandle.WaitAny([completion, cancellationToken.WaitHandle]);
            if (signaled == 1)
            {
                // CancelIoEx only requests cancellation. The OVERLAPPED structure
                // and pinned buffer must stay alive until the driver completes it.
                _ = CancelIoEx(directory, overlapped);
                _ = GetOverlappedResult(directory, overlapped, out _, true);
                throw new OperationCanceledException(cancellationToken);
            }

            if (!GetOverlappedResult(directory, overlapped, out var bytesReturned, false))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorOperationAborted && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new Win32Exception(error, LocalizationService.T("GeneralServiceDirectoryWatchReadFailed"));
            }

            return ParseNotifications(buffer.AsSpan(0, checked((int)bytesReturned)));
        }
        finally
        {
            lock (_gate)
            {
                if (_activeOverlapped == overlapped)
                {
                    _activeOverlapped = 0;
                }
            }
            Marshal.FreeHGlobal(overlapped);
            pinnedBuffer.Free();
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_directory is { IsInvalid: false, IsClosed: false } directory)
            {
                CancelIoEx(directory, _activeOverlapped);
            }
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            CloseCore();
        }
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
            CloseCore();
        }
    }

    internal static IReadOnlyList<NativeDirectoryChange> ParseNotifications(
        ReadOnlySpan<byte> buffer)
    {
        var result = new List<NativeDirectoryChange>();
        var offset = 0;
        while (offset + 12 <= buffer.Length)
        {
            var nextOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
            var actionValue = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 4, 4));
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 8, 4));
            if (nameLength > int.MaxValue ||
                (nameLength & 1) != 0 ||
                offset + 12L + nameLength > buffer.Length)
            {
                throw new InvalidDataException(LocalizationService.T("GeneralServiceDirectoryWatchInvalidRecord"));
            }

            var relativePath = Encoding.Unicode.GetString(
                buffer.Slice(offset + 12, checked((int)nameLength)));
            if (Enum.IsDefined(typeof(NativeDirectoryChangeAction), actionValue))
            {
                result.Add(new NativeDirectoryChange(
                    (NativeDirectoryChangeAction)actionValue,
                    relativePath));
            }

            if (nextOffset == 0)
            {
                break;
            }
            if (nextOffset < 12 || offset + nextOffset > buffer.Length)
            {
                throw new InvalidDataException(LocalizationService.T("GeneralServiceDirectoryWatchInvalidOffset"));
            }
            offset += checked((int)nextOffset);
        }
        return result;
    }

    private void CloseCore()
    {
        if (_directory is null)
        {
            return;
        }
        if (!_directory.IsInvalid && !_directory.IsClosed)
        {
            CancelIoEx(_directory, _activeOverlapped);
        }
        _directory.Dispose();
        _directory = null;
        _activeOverlapped = 0;
    }

    private const uint FileListDirectory = 0x0001;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint NotifyChangeFileName = 0x00000001;
    private const uint NotifyChangeDirectoryName = 0x00000002;
    private const uint NotifyChangeSize = 0x00000008;
    private const uint NotifyChangeLastWrite = 0x00000010;
    private const uint NotifyChangeCreation = 0x00000040;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedData
    {
        public nint Internal;
        public nint InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public nint EventHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadDirectoryChangesW(
        SafeFileHandle directory,
        nint buffer,
        uint bufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        nint bytesReturned,
        nint overlapped,
        nint completionRoutine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle file,
        nint overlapped,
        out uint bytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle file, nint overlapped);
}
