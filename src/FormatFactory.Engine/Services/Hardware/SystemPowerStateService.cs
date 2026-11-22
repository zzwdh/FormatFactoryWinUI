using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public enum SystemPowerSource
{
    Unknown,
    Ac,
    Battery
}

public enum SystemPowerTransition
{
    Suspending,
    Resumed
}

public sealed record SystemPowerSnapshot(
    SystemPowerSource Source,
    bool IsBatteryPresent,
    int? BatteryPercent,
    bool EnergySaverEnabled,
    TimeSpan? BatteryLifeRemaining,
    DateTimeOffset CapturedAt);

public sealed class SystemPowerStateChangedEventArgs : EventArgs
{
    public SystemPowerStateChangedEventArgs(
        SystemPowerSnapshot current,
        SystemPowerSnapshot? previous)
    {
        Current = current;
        Previous = previous;
    }

    public SystemPowerSnapshot Current { get; }
    public SystemPowerSnapshot? Previous { get; }
}

public sealed class SystemPowerTransitionEventArgs : EventArgs
{
    public SystemPowerTransitionEventArgs(SystemPowerTransition transition) => Transition = transition;

    public SystemPowerTransition Transition { get; }
}

public interface ISystemPowerStatusProvider
{
    bool TryRead(out SystemPowerSnapshot snapshot);
}

public interface ISuspendResumeNotificationSource : IDisposable
{
    event EventHandler<SystemPowerTransitionEventArgs>? Transitioned;

    bool IsRunning { get; }

    bool Start();

    void Stop();
}

/// <summary>
/// Publishes AC/battery/energy-saver state and suspend/resume transitions.
/// Consumers can call Refresh immediately after settings or device changes.
/// </summary>
public sealed class SystemPowerStateService : IDisposable
{
    private readonly object _gate = new();
    private readonly ISystemPowerStatusProvider _statusProvider;
    private readonly ISuspendResumeNotificationSource _transitionSource;
    private Timer? _timer;
    private SynchronizationContext? _eventContext;
    private SystemPowerSnapshot? _current;
    private bool _running;
    private bool _disposed;

    public SystemPowerStateService()
        : this(new NativeSystemPowerStatusProvider(), new NativeSuspendResumeNotificationSource())
    {
    }

    internal SystemPowerStateService(
        ISystemPowerStatusProvider statusProvider,
        ISuspendResumeNotificationSource transitionSource)
    {
        _statusProvider = statusProvider ?? throw new ArgumentNullException(nameof(statusProvider));
        _transitionSource = transitionSource ?? throw new ArgumentNullException(nameof(transitionSource));
        _transitionSource.Transitioned += TransitionSource_Transitioned;
    }

    public event EventHandler<SystemPowerStateChangedEventArgs>? StateChanged;
    public event EventHandler<SystemPowerTransitionEventArgs>? Transitioned;

    public SystemPowerSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

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

    public void Start(TimeSpan? refreshInterval = null)
    {
        var interval = refreshInterval ?? TimeSpan.FromSeconds(15);
        if (interval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _running = true;
            _eventContext = SynchronizationContext.Current;
            _transitionSource.Start();
            _timer = new Timer(
                static state => ((SystemPowerStateService)state!).Refresh(),
                this,
                interval,
                interval);
            NativeDiagnostics.Log.ServiceStarted(nameof(SystemPowerStateService));
        }

        Refresh();
    }

    public bool Refresh()
    {
        if (!_statusProvider.TryRead(out var snapshot))
        {
            NativeDiagnostics.Log.NativeOperationFailed(
                nameof(SystemPowerStateService),
                Marshal.GetLastPInvokeError(),
                "GetSystemPowerStatus");
            return false;
        }

        SystemPowerSnapshot? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            previous = _current;
            _current = snapshot;
        }

        if (HasSamePowerValues(previous, snapshot))
        {
            return true;
        }

        NativeDiagnostics.Log.PowerStateChanged(
            snapshot.Source.ToString(),
            snapshot.BatteryPercent ?? -1,
            snapshot.EnergySaverEnabled);
        Dispatch(StateChanged, new SystemPowerStateChangedEventArgs(snapshot, previous));
        return true;
    }

    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            timer = _timer;
            _timer = null;
            _eventContext = null;
        }

        timer?.Dispose();
        _transitionSource.Stop();
        NativeDiagnostics.Log.ServiceStopped(nameof(SystemPowerStateService));
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
        _transitionSource.Transitioned -= TransitionSource_Transitioned;
        _transitionSource.Dispose();
    }

    internal static bool HasSamePowerValues(
        SystemPowerSnapshot? left,
        SystemPowerSnapshot right) =>
        left is not null &&
        left.Source == right.Source &&
        left.IsBatteryPresent == right.IsBatteryPresent &&
        left.BatteryPercent == right.BatteryPercent &&
        left.EnergySaverEnabled == right.EnergySaverEnabled &&
        left.BatteryLifeRemaining == right.BatteryLifeRemaining;

    private void TransitionSource_Transitioned(
        object? sender,
        SystemPowerTransitionEventArgs args)
    {
        NativeDiagnostics.Log.PowerTransition(args.Transition.ToString());
        if (args.Transition == SystemPowerTransition.Resumed)
        {
            Refresh();
        }

        Dispatch(Transitioned, args);
    }

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
            var dispatch = (EventDispatch<TEventArgs>)state!;
            dispatch.Handler(dispatch.Sender, dispatch.Args);
        }, new EventDispatch<TEventArgs>(this, handler, args));
    }

    private sealed record EventDispatch<TEventArgs>(
        SystemPowerStateService Sender,
        EventHandler<TEventArgs> Handler,
        TEventArgs Args)
        where TEventArgs : EventArgs;
}

internal sealed class NativeSystemPowerStatusProvider : ISystemPowerStatusProvider
{
    public bool TryRead(out SystemPowerSnapshot snapshot)
    {
        snapshot = default!;
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var status))
        {
            return false;
        }

        var source = status.AcLineStatus switch
        {
            0 => SystemPowerSource.Battery,
            1 => SystemPowerSource.Ac,
            _ => SystemPowerSource.Unknown
        };
        var batteryPresent = (status.BatteryFlag & NoSystemBattery) == 0;
        var percentage = batteryPresent && status.BatteryLifePercent <= 100
            ? status.BatteryLifePercent
            : (int?)null;
        var remaining = batteryPresent && status.BatteryLifeTime != UnknownBatteryLifetime
            ? TimeSpan.FromSeconds(status.BatteryLifeTime)
            : (TimeSpan?)null;
        snapshot = new SystemPowerSnapshot(
            source,
            batteryPresent,
            percentage,
            status.SystemStatusFlag != 0,
            remaining,
            DateTimeOffset.UtcNow);
        return true;
    }

    private const byte NoSystemBattery = 0x80;
    private const uint UnknownBatteryLifetime = uint.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);
}

internal sealed class NativeSuspendResumeNotificationSource : ISuspendResumeNotificationSource
{
    private readonly object _gate = new();
    private readonly SuspendResumeCallback _callback;
    private nint _registration;
    private bool _disposed;

    public NativeSuspendResumeNotificationSource()
    {
        _callback = OnNotification;
    }

    public event EventHandler<SystemPowerTransitionEventArgs>? Transitioned;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _registration != 0;
            }
        }
    }

    public bool Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registration != 0)
            {
                return true;
            }

            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var parameters = new DeviceNotifySubscribeParameters
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_callback)
            };
            var result = PowerRegisterSuspendResumeNotification(
                DeviceNotifyCallback,
                ref parameters,
                out _registration);
            if (result == 0)
            {
                return true;
            }

            _registration = 0;
            NativeDiagnostics.Log.NativeOperationFailed(
                nameof(NativeSuspendResumeNotificationSource),
                checked((int)result),
                "PowerRegisterSuspendResumeNotification");
            return false;
        }
    }

    public void Stop()
    {
        nint registration;
        lock (_gate)
        {
            registration = _registration;
            _registration = 0;
        }

        if (registration != 0)
        {
            var result = PowerUnregisterSuspendResumeNotification(registration);
            if (result != 0)
            {
                NativeDiagnostics.Log.NativeOperationFailed(
                    nameof(NativeSuspendResumeNotificationSource),
                    checked((int)result),
                    "PowerUnregisterSuspendResumeNotification");
            }
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
    }

    private uint OnNotification(nint context, uint type, nint setting)
    {
        var transition = type switch
        {
            PowerBroadcastSuspend => (SystemPowerTransition?)SystemPowerTransition.Suspending,
            PowerBroadcastResumeSuspend => SystemPowerTransition.Resumed,
            PowerBroadcastResumeAutomatic => SystemPowerTransition.Resumed,
            _ => null
        };
        if (transition is not null)
        {
            try
            {
                Transitioned?.Invoke(this, new SystemPowerTransitionEventArgs(transition.Value));
            }
            catch
            {
                // Exceptions must not cross the native callback boundary.
            }
        }
        return 0;
    }

    private const uint DeviceNotifyCallback = 2;
    private const uint PowerBroadcastSuspend = 0x0004;
    private const uint PowerBroadcastResumeSuspend = 0x0007;
    private const uint PowerBroadcastResumeAutomatic = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public nint Callback;
        public nint Context;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint SuspendResumeCallback(nint context, uint type, nint setting);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerRegisterSuspendResumeNotification(
        uint flags,
        ref DeviceNotifySubscribeParameters recipient,
        out nint registrationHandle);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerUnregisterSuspendResumeNotification(nint registrationHandle);
}
