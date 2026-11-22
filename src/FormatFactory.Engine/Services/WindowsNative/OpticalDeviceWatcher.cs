using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public enum OpticalDeviceChangeKind
{
    Arrived,
    Removed,
    Updated
}

public sealed class OpticalDeviceChangeEventArgs : EventArgs
{
    public OpticalDeviceChangeEventArgs(
        OpticalDeviceChangeKind kind,
        string? devicePath,
        DateTimeOffset timestamp)
    {
        Kind = kind;
        DevicePath = devicePath;
        Timestamp = timestamp;
    }

    public OpticalDeviceChangeKind Kind { get; }
    public string? DevicePath { get; }
    public DateTimeOffset Timestamp { get; }
}

public interface IOpticalDeviceChangeSource : IDisposable
{
    event EventHandler<OpticalDeviceChangeEventArgs>? Changed;

    bool IsRunning { get; }

    bool Start();

    void Stop();
}

/// <summary>
/// Receives CD/DVD/Blu-ray interface notifications without requiring a window handle.
/// CM_Register_Notification works for both packaged and unpackaged desktop processes.
/// </summary>
public sealed class OpticalDeviceWatcher : IOpticalDeviceChangeSource
{
    private readonly object _gate = new();
    private readonly IOpticalDeviceNotificationBackend _backend;
    private SynchronizationContext? _eventContext;
    private bool _running;
    private bool _disposed;

    public OpticalDeviceWatcher()
        : this(new ConfigurationManagerOpticalDeviceNotificationBackend())
    {
    }

    internal OpticalDeviceWatcher(IOpticalDeviceNotificationBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _backend.Changed += Backend_Changed;
    }

    public event EventHandler<OpticalDeviceChangeEventArgs>? Changed;

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

    public bool Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return true;
            }

            _eventContext = SynchronizationContext.Current;
            _running = _backend.TryRegister();
            if (_running)
            {
                NativeDiagnostics.Log.ServiceStarted(nameof(OpticalDeviceWatcher));
            }
            else
            {
                NativeDiagnostics.Log.NativeOperationFailed(
                    nameof(OpticalDeviceWatcher),
                    _backend.LastErrorCode,
                    "CM_Register_Notification");
            }

            return _running;
        }
    }

    public void Stop()
    {
        var unregister = false;
        lock (_gate)
        {
            unregister = _running;
            _running = false;
            _eventContext = null;
        }

        if (unregister)
        {
            _backend.Unregister();
            NativeDiagnostics.Log.ServiceStopped(nameof(OpticalDeviceWatcher));
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
        }

        Stop();
        _backend.Changed -= Backend_Changed;
        _backend.Dispose();
    }

    private void Backend_Changed(object? sender, OpticalDeviceChangeEventArgs args)
    {
        EventHandler<OpticalDeviceChangeEventArgs>? handler;
        SynchronizationContext? context;
        lock (_gate)
        {
            if (!_running || _disposed)
            {
                return;
            }

            handler = Changed;
            context = _eventContext;
        }

        if (handler is null)
        {
            return;
        }

        NativeDiagnostics.Log.OpticalDeviceChanged(args.Kind.ToString(), args.DevicePath ?? string.Empty);
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            handler(this, args);
            return;
        }

        context.Post(static state =>
        {
            var dispatch = (DeviceChangeDispatch)state!;
            dispatch.Handler(dispatch.Sender, dispatch.Args);
        }, new DeviceChangeDispatch(this, handler, args));
    }

    private sealed record DeviceChangeDispatch(
        OpticalDeviceWatcher Sender,
        EventHandler<OpticalDeviceChangeEventArgs> Handler,
        OpticalDeviceChangeEventArgs Args);
}

internal interface IOpticalDeviceNotificationBackend : IDisposable
{
    event EventHandler<OpticalDeviceChangeEventArgs>? Changed;

    int LastErrorCode { get; }

    bool TryRegister();

    void Unregister();
}

internal sealed class ConfigurationManagerOpticalDeviceNotificationBackend : IOpticalDeviceNotificationBackend
{
    private static readonly Guid CdRomDeviceInterface = new("53F56308-B6BF-11D0-94F2-00A0C91EFB8B");
    private readonly object _gate = new();
    private readonly DeviceNotificationCallback _callback;
    private nint _notificationHandle;
    private bool _disposed;

    public ConfigurationManagerOpticalDeviceNotificationBackend()
    {
        _callback = OnNotification;
    }

    public event EventHandler<OpticalDeviceChangeEventArgs>? Changed;

    public int LastErrorCode { get; private set; }

    public bool TryRegister()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_notificationHandle != 0)
            {
                return true;
            }

            if (!OperatingSystem.IsWindows())
            {
                LastErrorCode = -1;
                return false;
            }

            var filter = new DeviceNotificationFilter
            {
                Size = checked((uint)Marshal.SizeOf<DeviceNotificationFilter>()),
                FilterType = DeviceNotificationFilterType.DeviceInterface,
                ClassGuid = CdRomDeviceInterface
            };
            var result = CM_Register_Notification(ref filter, 0, _callback, out _notificationHandle);
            LastErrorCode = checked((int)result);
            if (result == ConfigurationManagerSuccess)
            {
                return true;
            }

            _notificationHandle = 0;
            return false;
        }
    }

    public void Unregister()
    {
        nint handle;
        lock (_gate)
        {
            handle = _notificationHandle;
            _notificationHandle = 0;
        }

        if (handle != 0)
        {
            LastErrorCode = checked((int)CM_Unregister_Notification(handle));
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
        }

        Unregister();
    }

    private uint OnNotification(
        nint notification,
        nint context,
        DeviceNotificationAction action,
        nint eventData,
        uint eventDataSize)
    {
        var kind = action switch
        {
            DeviceNotificationAction.DeviceInterfaceArrival => OpticalDeviceChangeKind.Arrived,
            DeviceNotificationAction.DeviceInterfaceRemoval => OpticalDeviceChangeKind.Removed,
            DeviceNotificationAction.DeviceRemoveComplete => OpticalDeviceChangeKind.Removed,
            _ => OpticalDeviceChangeKind.Updated
        };

        if (action is not (
            DeviceNotificationAction.DeviceInterfaceArrival or
            DeviceNotificationAction.DeviceInterfaceRemoval or
            DeviceNotificationAction.DeviceRemoveComplete))
        {
            return ConfigurationManagerSuccess;
        }

        try
        {
            var path = ReadSymbolicLink(eventData, eventDataSize);
            Changed?.Invoke(
                this,
                new OpticalDeviceChangeEventArgs(kind, path, DateTimeOffset.UtcNow));
        }
        catch
        {
            // Exceptions must not cross the native callback boundary.
        }

        return ConfigurationManagerSuccess;
    }

    internal static string? ReadSymbolicLink(nint eventData, uint eventDataSize)
    {
        const int symbolicLinkOffset = 24;
        if (eventData == 0 || eventDataSize <= symbolicLinkOffset + sizeof(char))
        {
            return null;
        }

        var characterCount = checked(((int)eventDataSize - symbolicLinkOffset) / sizeof(char));
        return Marshal.PtrToStringUni(eventData + symbolicLinkOffset, characterCount)?.TrimEnd('\0');
    }

    private const uint ConfigurationManagerSuccess = 0;

    private enum DeviceNotificationFilterType : uint
    {
        DeviceInterface = 0
    }

    private enum DeviceNotificationAction : uint
    {
        DeviceInterfaceArrival = 0,
        DeviceInterfaceRemoval = 1,
        DeviceQueryRemove = 2,
        DeviceQueryRemoveFailed = 3,
        DeviceRemovePending = 4,
        DeviceRemoveComplete = 5,
        DeviceCustomEvent = 6,
        DeviceInstanceEnumerated = 7,
        DeviceInstanceStarted = 8,
        DeviceInstanceRemoved = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotificationFilter
    {
        public uint Size;
        public uint Flags;
        public DeviceNotificationFilterType FilterType;
        public uint Reserved;
        public Guid ClassGuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotificationCallback(
        nint notification,
        nint context,
        DeviceNotificationAction action,
        nint eventData,
        uint eventDataSize);

    [DllImport("cfgmgr32.dll", SetLastError = false)]
    private static extern uint CM_Register_Notification(
        ref DeviceNotificationFilter filter,
        nint context,
        DeviceNotificationCallback callback,
        out nint notifyContext);

    [DllImport("cfgmgr32.dll", SetLastError = false)]
    private static extern uint CM_Unregister_Notification(nint notifyContext);
}
