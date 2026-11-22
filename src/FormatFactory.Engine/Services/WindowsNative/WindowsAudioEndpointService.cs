using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public sealed partial class WindowsAudioEndpointService : IWindowsAudioEndpointService
{
    private const uint ClsctxAll = 0x17;
    private const uint DeviceStateAll = 0x0F;
    private static readonly Guid DeviceEnumeratorClass = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    private static readonly Guid DeviceEnumeratorInterface = new("a95664d2-9614-4f35-a746-de8db63617e6");
    private static readonly Guid AudioClientInterface = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    private static readonly PropertyKey FriendlyNameKey = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        14);

    public NativeCapabilityResult<IReadOnlyList<WindowsAudioEndpointInfo>> EnumerateEndpoints()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeCapabilityResult<IReadOnlyList<WindowsAudioEndpointInfo>>.NotSupported(
                "MMDevice and WASAPI are available only on Windows.");
        }

        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null)
        {
            return new NativeCapabilityResult<IReadOnlyList<WindowsAudioEndpointInfo>>(
                comResult.Status,
                null,
                comResult.Message);
        }

        using var com = comResult.Value;
        IntPtr enumerator = IntPtr.Zero;
        try
        {
            var classId = DeviceEnumeratorClass;
            var interfaceId = DeviceEnumeratorInterface;
            var createResult = CoCreateInstance(
                ref classId,
                IntPtr.Zero,
                ClsctxAll,
                ref interfaceId,
                out enumerator);
            if (createResult < 0 || enumerator == IntPtr.Zero)
            {
                return NativeCapabilityResult<IReadOnlyList<WindowsAudioEndpointInfo>>.Unavailable(
                    $"MMDevice enumerator activation failed (0x{createResult:X8}).");
            }

            var endpoints = new List<WindowsAudioEndpointInfo>();
            EnumerateFlow(enumerator, 0, WindowsAudioDataFlow.Render, endpoints);
            EnumerateFlow(enumerator, 1, WindowsAudioDataFlow.Capture, endpoints);
            var ordered = endpoints
                .DistinctBy(endpoint => (endpoint.Id, endpoint.DataFlow))
                .OrderBy(endpoint => endpoint.DataFlow)
                .ThenBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return NativeCapabilityResult<IReadOnlyList<WindowsAudioEndpointInfo>>.Available(
                ordered,
                $"Detected {ordered.Length} Windows audio endpoints.");
        }
        catch (DllNotFoundException)
        {
            return NativeCapabilityResult<IReadOnlyList<WindowsAudioEndpointInfo>>.Unavailable(
                "MMDevice/WASAPI runtime was not found.");
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCapabilityResult<IReadOnlyList<WindowsAudioEndpointInfo>>.Unavailable(
                "MMDevice/WASAPI activation is unavailable.");
        }
        catch (Exception exception)
        {
            return NativeCapabilityResult<IReadOnlyList<WindowsAudioEndpointInfo>>.Failed(exception.Message);
        }
        finally
        {
            if (enumerator != IntPtr.Zero)
            {
                Marshal.Release(enumerator);
            }
        }
    }

    private static void EnumerateFlow(
        IntPtr enumerator,
        uint dataFlow,
        WindowsAudioDataFlow managedDataFlow,
        ICollection<WindowsAudioEndpointInfo> output)
    {
        var enumerate = GetDelegate<EnumAudioEndpointsDelegate>(enumerator, 3);
        if (enumerate(enumerator, dataFlow, DeviceStateAll, out var collection) < 0
            || collection == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var getCount = GetDelegate<GetCollectionCountDelegate>(collection, 3);
            var item = GetDelegate<GetCollectionItemDelegate>(collection, 4);
            if (getCount(collection, out var count) < 0)
            {
                return;
            }

            for (var index = 0u; index < count; index++)
            {
                if (item(collection, index, out var device) < 0 || device == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var id = ReadDeviceId(device);
                    var name = ReadDeviceName(device);
                    var state = ReadDeviceState(device);
                    var mixFormat = ReadMixFormat(device);
                    output.Add(new WindowsAudioEndpointInfo(
                        id,
                        string.IsNullOrWhiteSpace(name) ? id : name,
                        managedDataFlow,
                        state,
                        mixFormat.Channels,
                        mixFormat.SamplesPerSecond,
                        mixFormat.BitsPerSample));
                }
                finally
                {
                    Marshal.Release(device);
                }
            }
        }
        finally
        {
            Marshal.Release(collection);
        }
    }

    private static string ReadDeviceId(IntPtr device)
    {
        var getId = GetDelegate<GetDeviceIdDelegate>(device, 5);
        IntPtr allocated = IntPtr.Zero;
        try
        {
            return getId(device, out allocated) >= 0 && allocated != IntPtr.Zero
                ? Marshal.PtrToStringUni(allocated) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            if (allocated != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(allocated);
            }
        }
    }

    private static string ReadDeviceName(IntPtr device)
    {
        var openPropertyStore = GetDelegate<OpenPropertyStoreDelegate>(device, 4);
        if (openPropertyStore(device, 0, out var propertyStore) < 0 || propertyStore == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var getValue = GetDelegate<GetPropertyValueDelegate>(propertyStore, 5);
            var key = FriendlyNameKey;
            var value = default(NativePropertyVariant);
            try
            {
                return getValue(propertyStore, ref key, out value) >= 0
                    ? NativePropertyValue.ToDisplayString(ref value)
                    : string.Empty;
            }
            finally
            {
                NativePropertyValue.Clear(ref value);
            }
        }
        finally
        {
            Marshal.Release(propertyStore);
        }
    }

    private static uint ReadDeviceState(IntPtr device)
    {
        var getState = GetDelegate<GetDeviceStateDelegate>(device, 6);
        return getState(device, out var state) >= 0 ? state : 0;
    }

    private static WaveFormatSummary ReadMixFormat(IntPtr device)
    {
        var activate = GetDelegate<ActivateDelegate>(device, 3);
        var interfaceId = AudioClientInterface;
        if (activate(device, ref interfaceId, ClsctxAll, IntPtr.Zero, out var audioClient) < 0
            || audioClient == IntPtr.Zero)
        {
            return default;
        }

        try
        {
            var getMixFormat = GetDelegate<GetMixFormatDelegate>(audioClient, 8);
            if (getMixFormat(audioClient, out var waveFormatPointer) < 0
                || waveFormatPointer == IntPtr.Zero)
            {
                return default;
            }

            try
            {
                var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(waveFormatPointer);
                return new WaveFormatSummary(
                    waveFormat.Channels,
                    checked((int)waveFormat.SamplesPerSecond),
                    waveFormat.BitsPerSample);
            }
            finally
            {
                Marshal.FreeCoTaskMem(waveFormatPointer);
            }
        }
        finally
        {
            Marshal.Release(audioClient);
        }
    }

    private static T GetDelegate<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    private readonly record struct WaveFormatSummary(int Channels, int SamplesPerSecond, int BitsPerSample);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAudioEndpointsDelegate(
        IntPtr self,
        uint dataFlow,
        uint stateMask,
        out IntPtr devices);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCollectionCountDelegate(IntPtr self, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCollectionItemDelegate(IntPtr self, uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateDelegate(
        IntPtr self,
        ref Guid interfaceId,
        uint classContext,
        IntPtr activationParameters,
        out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenPropertyStoreDelegate(IntPtr self, uint accessMode, out IntPtr propertyStore);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceIdDelegate(IntPtr self, out IntPtr id);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceStateDelegate(IntPtr self, out uint state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPropertyValueDelegate(
        IntPtr self,
        ref PropertyKey key,
        out NativePropertyVariant value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMixFormatDelegate(IntPtr self, out IntPtr waveFormat);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        out IntPtr instance);
}

public enum WindowsAudioEndpointChangeKind
{
    DeviceStateChanged,
    DeviceAdded,
    DeviceRemoved,
    DefaultDeviceChanged,
    PropertyValueChanged
}

public enum WindowsAudioEndpointRole
{
    Console,
    Multimedia,
    Communications
}

public sealed class WindowsAudioEndpointChangeEventArgs : EventArgs
{
    public WindowsAudioEndpointChangeEventArgs(
        WindowsAudioEndpointChangeKind kind,
        string? deviceId,
        DateTimeOffset timestamp,
        WindowsAudioDataFlow? dataFlow = null,
        WindowsAudioEndpointRole? role = null,
        uint? state = null,
        Guid? propertyFormatId = null,
        uint? propertyId = null)
    {
        Kind = kind;
        DeviceId = deviceId;
        Timestamp = timestamp;
        DataFlow = dataFlow;
        Role = role;
        State = state;
        PropertyFormatId = propertyFormatId;
        PropertyId = propertyId;
    }

    public WindowsAudioEndpointChangeKind Kind { get; }
    public string? DeviceId { get; }
    public DateTimeOffset Timestamp { get; }
    public WindowsAudioDataFlow? DataFlow { get; }
    public WindowsAudioEndpointRole? Role { get; }
    public uint? State { get; }
    public Guid? PropertyFormatId { get; }
    public uint? PropertyId { get; }
}

public interface IWindowsAudioEndpointChangeSource : IDisposable
{
    event EventHandler<WindowsAudioEndpointChangeEventArgs>? Changed;
    bool IsRunning { get; }
    bool Start();
    void Stop();
}

/// <summary>
/// Watches Windows audio endpoint changes and dispatches them to the synchronization
/// context on which the watcher was started.
/// </summary>
public sealed class WindowsAudioEndpointWatcher : IWindowsAudioEndpointChangeSource
{
    private readonly object _gate = new();
    private readonly IWindowsAudioEndpointNotificationBackend _backend;
    private SynchronizationContext? _eventContext;
    private long _generation;
    private bool _running;
    private bool _disposed;

    public WindowsAudioEndpointWatcher()
        : this(new MmDeviceAudioEndpointNotificationBackend())
    {
    }

    internal WindowsAudioEndpointWatcher(IWindowsAudioEndpointNotificationBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _backend.Changed += Backend_Changed;
    }

    public event EventHandler<WindowsAudioEndpointChangeEventArgs>? Changed;

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
                _generation++;
                NativeDiagnostics.Log.ServiceStarted(nameof(WindowsAudioEndpointWatcher));
            }
            else
            {
                _eventContext = null;
                NativeDiagnostics.Log.NativeOperationFailed(
                    nameof(WindowsAudioEndpointWatcher),
                    _backend.LastErrorCode,
                    "IMMDeviceEnumerator.RegisterEndpointNotificationCallback");
            }

            return _running;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _generation++;
            _eventContext = null;
        }

        _backend.Unregister();
        NativeDiagnostics.Log.ServiceStopped(nameof(WindowsAudioEndpointWatcher));
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

    private void Backend_Changed(object? sender, WindowsAudioEndpointChangeEventArgs args)
    {
        SynchronizationContext? context;
        long generation;
        lock (_gate)
        {
            if (!_running || _disposed)
            {
                return;
            }

            context = _eventContext;
            generation = _generation;
        }

        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            DispatchIfCurrent(args, generation);
            return;
        }

        context.Post(static state =>
        {
            var dispatch = (AudioEndpointChangeDispatch)state!;
            dispatch.Sender.DispatchIfCurrent(dispatch.Args, dispatch.Generation);
        }, new AudioEndpointChangeDispatch(this, args, generation));
    }

    private void DispatchIfCurrent(WindowsAudioEndpointChangeEventArgs args, long generation)
    {
        EventHandler<WindowsAudioEndpointChangeEventArgs>? handler;
        lock (_gate)
        {
            if (!_running || _disposed || generation != _generation)
            {
                return;
            }

            handler = Changed;
        }

        handler?.Invoke(this, args);
    }

    private sealed record AudioEndpointChangeDispatch(
        WindowsAudioEndpointWatcher Sender,
        WindowsAudioEndpointChangeEventArgs Args,
        long Generation);
}

internal interface IWindowsAudioEndpointNotificationBackend : IDisposable
{
    event EventHandler<WindowsAudioEndpointChangeEventArgs>? Changed;
    int LastErrorCode { get; }
    bool TryRegister();
    void Unregister();
}

internal sealed partial class MmDeviceAudioEndpointNotificationBackend
    : IWindowsAudioEndpointNotificationBackend
{
    private const uint ClsctxAll = 0x17;
    private const uint CoinitMultithreaded = 0;
    private const int StartupTimeoutMilliseconds = 5000;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int ErrorNotSupported = 50;
    private const int ErrorTimeout = 1460;

    private static readonly Guid DeviceEnumeratorClass =
        new("bcde0395-e52f-467c-8e3d-c4579291692e");
    private static readonly Guid DeviceEnumeratorInterface =
        new("a95664d2-9614-4f35-a746-de8db63617e6");

    private readonly object _gate = new();
    private Thread? _worker;
    private ManualResetEventSlim? _startupSignal;
    private ManualResetEventSlim? _stopSignal;
    private bool _registered;
    private bool _disposed;
    private int _lastErrorCode;

    public event EventHandler<WindowsAudioEndpointChangeEventArgs>? Changed;

    public int LastErrorCode
    {
        get
        {
            lock (_gate)
            {
                return _lastErrorCode;
            }
        }
    }

    public bool TryRegister()
    {
        if (!OperatingSystem.IsWindows())
        {
            lock (_gate)
            {
                _lastErrorCode = ErrorNotSupported;
            }

            return false;
        }

        Thread worker;
        ManualResetEventSlim startup;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CleanupCompletedWorkerNoLock();
            if (_worker is not null)
            {
                return _registered;
            }

            startup = new ManualResetEventSlim(false);
            var stop = new ManualResetEventSlim(false);
            worker = new Thread(() => RunNotificationLoop(startup, stop))
            {
                IsBackground = true,
                Name = "FormatFactory audio endpoint notifications"
            };
            _startupSignal = startup;
            _stopSignal = stop;
            _worker = worker;
            _registered = false;
            _lastErrorCode = 0;
        }

        worker.Start();
        if (!startup.Wait(StartupTimeoutMilliseconds))
        {
            lock (_gate)
            {
                _lastErrorCode = ErrorTimeout;
            }

            Unregister();
            return false;
        }

        lock (_gate)
        {
            var registered = ReferenceEquals(_worker, worker) && _registered;
            if (!registered && !worker.IsAlive)
            {
                CleanupCompletedWorkerNoLock();
            }

            return registered;
        }
    }

    public void Unregister()
    {
        Thread? worker;
        ManualResetEventSlim? stop;
        lock (_gate)
        {
            worker = _worker;
            stop = _stopSignal;
            _registered = false;
        }

        stop?.Set();
        if (worker is not null && !ReferenceEquals(worker, Thread.CurrentThread))
        {
            worker.Join(StartupTimeoutMilliseconds);
        }

        lock (_gate)
        {
            if (worker is null || !worker.IsAlive)
            {
                CleanupCompletedWorkerNoLock();
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

        Unregister();
    }

    private void RunNotificationLoop(
        ManualResetEventSlim startup,
        ManualResetEventSlim stop)
    {
        var comInitialized = false;
        nint enumerator = 0;
        NativeMmNotificationClient? callback = null;
        var registrationSucceeded = false;
        try
        {
            var initializeResult = CoInitializeEx(0, CoinitMultithreaded);
            if (initializeResult < 0 && initializeResult != RpcEChangedMode)
            {
                SetStartupResult(false, initializeResult);
                return;
            }

            comInitialized = initializeResult >= 0;
            var classId = DeviceEnumeratorClass;
            var interfaceId = DeviceEnumeratorInterface;
            var createResult = CoCreateDeviceEnumerator(
                ref classId,
                0,
                ClsctxAll,
                ref interfaceId,
                out enumerator);
            if (createResult < 0 || enumerator == 0)
            {
                SetStartupResult(false, createResult);
                return;
            }

            callback = new NativeMmNotificationClient(QueueChanged);
            var register = GetDelegate<RegisterNotificationDelegate>(enumerator, 6);
            var registerResult = register(enumerator, callback.Instance);
            registrationSucceeded = registerResult >= 0;
            SetStartupResult(registrationSucceeded, registerResult);
            if (!registrationSucceeded)
            {
                return;
            }

            stop.Wait();
        }
        catch (DllNotFoundException exception)
        {
            SetStartupResult(false, exception.HResult);
        }
        catch (EntryPointNotFoundException exception)
        {
            SetStartupResult(false, exception.HResult);
        }
        catch (Exception exception)
        {
            SetStartupResult(false, exception.HResult);
        }
        finally
        {
            if (registrationSucceeded && enumerator != 0 && callback is not null)
            {
                try
                {
                    var unregister = GetDelegate<UnregisterNotificationDelegate>(enumerator, 7);
                    var unregisterResult = unregister(enumerator, callback.Instance);
                    if (unregisterResult < 0)
                    {
                        lock (_gate)
                        {
                            _lastErrorCode = unregisterResult;
                        }
                    }
                }
                catch (Exception exception)
                {
                    lock (_gate)
                    {
                        _lastErrorCode = exception.HResult;
                    }
                }
            }

            lock (_gate)
            {
                _registered = false;
            }

            callback?.Dispose();
            if (enumerator != 0)
            {
                Marshal.Release(enumerator);
            }

            if (comInitialized)
            {
                CoUninitialize();
            }

            startup.Set();
        }
    }

    private void SetStartupResult(bool registered, int errorCode)
    {
        lock (_gate)
        {
            _registered = registered;
            _lastErrorCode = errorCode;
            _startupSignal?.Set();
        }
    }

    private void QueueChanged(WindowsAudioEndpointChangeEventArgs args)
    {
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            var dispatch = (BackendChangeDispatch)state!;
            dispatch.Backend.PublishChanged(dispatch.Args);
        }, new BackendChangeDispatch(this, args), preferLocal: false);
    }

    private void PublishChanged(WindowsAudioEndpointChangeEventArgs args)
    {
        EventHandler<WindowsAudioEndpointChangeEventArgs>? handler;
        lock (_gate)
        {
            if (!_registered || _disposed)
            {
                return;
            }

            handler = Changed;
        }

        if (handler is null)
        {
            return;
        }

        foreach (EventHandler<WindowsAudioEndpointChangeEventArgs> subscriber
                 in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch
            {
                // Event subscribers cannot terminate the native notification pipeline.
            }
        }
    }

    private void CleanupCompletedWorkerNoLock()
    {
        if (_worker is not null && _worker.IsAlive)
        {
            return;
        }

        _startupSignal?.Dispose();
        _stopSignal?.Dispose();
        _startupSignal = null;
        _stopSignal = null;
        _worker = null;
        _registered = false;
    }

    private static T GetDelegate<T>(nint comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RegisterNotificationDelegate(nint self, nint callback);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnregisterNotificationDelegate(nint self, nint callback);

    private sealed record BackendChangeDispatch(
        MmDeviceAudioEndpointNotificationBackend Backend,
        WindowsAudioEndpointChangeEventArgs Args);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    private static partial int CoCreateDeviceEnumerator(
        ref Guid classId,
        nint outer,
        uint context,
        ref Guid interfaceId,
        out nint instance);
}

internal sealed class NativeMmNotificationClient : IDisposable
{
    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    private static readonly Guid IUnknownInterface =
        new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid NotificationClientInterface =
        new("7991eec9-7e89-4d85-8390-6c703cec60c0");

    private static readonly QueryInterfaceDelegate QueryInterfaceCallback = QueryInterface;
    private static readonly AddRefDelegate AddRefCallback = AddRef;
    private static readonly ReleaseDelegate ReleaseCallback = Release;
    private static readonly DeviceStateChangedDelegate DeviceStateChangedCallback = OnDeviceStateChanged;
    private static readonly DeviceChangedDelegate DeviceAddedCallback = OnDeviceAdded;
    private static readonly DeviceChangedDelegate DeviceRemovedCallback = OnDeviceRemoved;
    private static readonly DefaultDeviceChangedDelegate DefaultDeviceChangedCallback = OnDefaultDeviceChanged;
    private static readonly PropertyValueChangedDelegate PropertyValueChangedCallback = OnPropertyValueChanged;
    private static readonly nint Vtable = CreateVtable();

    private nint _instance;

    public NativeMmNotificationClient(Action<WindowsAudioEndpointChangeEventArgs> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var state = new CallbackState(changed);
        var handle = GCHandle.Alloc(state);
        var instance = Marshal.AllocHGlobal(IntPtr.Size * 2);
        Marshal.WriteIntPtr(instance, Vtable);
        Marshal.WriteIntPtr(instance, IntPtr.Size, GCHandle.ToIntPtr(handle));
        state.Attach(instance, handle);
        _instance = instance;
    }

    public nint Instance
    {
        get
        {
            var instance = Volatile.Read(ref _instance);
            ObjectDisposedException.ThrowIf(instance == 0, this);
            return instance;
        }
    }

    public void Dispose()
    {
        var instance = Interlocked.Exchange(ref _instance, 0);
        if (instance != 0)
        {
            Release(instance);
        }
    }

    private static nint CreateVtable()
    {
        var vtable = Marshal.AllocHGlobal(IntPtr.Size * 8);
        Marshal.WriteIntPtr(vtable, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(QueryInterfaceCallback));
        Marshal.WriteIntPtr(vtable, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(AddRefCallback));
        Marshal.WriteIntPtr(vtable, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(ReleaseCallback));
        Marshal.WriteIntPtr(vtable, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(DeviceStateChangedCallback));
        Marshal.WriteIntPtr(vtable, 4 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(DeviceAddedCallback));
        Marshal.WriteIntPtr(vtable, 5 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(DeviceRemovedCallback));
        Marshal.WriteIntPtr(vtable, 6 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(DefaultDeviceChangedCallback));
        Marshal.WriteIntPtr(vtable, 7 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(PropertyValueChangedCallback));
        return vtable;
    }

    private static int QueryInterface(nint self, ref Guid interfaceId, out nint instance)
    {
        instance = 0;
        if (interfaceId != IUnknownInterface && interfaceId != NotificationClientInterface)
        {
            return E_NOINTERFACE;
        }

        if (GetState(self) is null)
        {
            return E_NOINTERFACE;
        }

        AddRef(self);
        instance = self;
        return S_OK;
    }

    private static uint AddRef(nint self) =>
        GetState(self)?.AddRef() ?? 0;

    private static uint Release(nint self) =>
        GetState(self)?.Release() ?? 0;

    private static int OnDeviceStateChanged(nint self, nint deviceId, uint newState) =>
        Publish(
            self,
            new WindowsAudioEndpointChangeEventArgs(
                WindowsAudioEndpointChangeKind.DeviceStateChanged,
                ReadDeviceId(deviceId),
                DateTimeOffset.UtcNow,
                state: newState));

    private static int OnDeviceAdded(nint self, nint deviceId) =>
        Publish(
            self,
            new WindowsAudioEndpointChangeEventArgs(
                WindowsAudioEndpointChangeKind.DeviceAdded,
                ReadDeviceId(deviceId),
                DateTimeOffset.UtcNow));

    private static int OnDeviceRemoved(nint self, nint deviceId) =>
        Publish(
            self,
            new WindowsAudioEndpointChangeEventArgs(
                WindowsAudioEndpointChangeKind.DeviceRemoved,
                ReadDeviceId(deviceId),
                DateTimeOffset.UtcNow));

    private static int OnDefaultDeviceChanged(
        nint self,
        int dataFlow,
        int role,
        nint deviceId) =>
        Publish(
            self,
            new WindowsAudioEndpointChangeEventArgs(
                WindowsAudioEndpointChangeKind.DefaultDeviceChanged,
                ReadDeviceId(deviceId),
                DateTimeOffset.UtcNow,
                MapDataFlow(dataFlow),
                MapRole(role)));

    private static int OnPropertyValueChanged(
        nint self,
        nint deviceId,
        NativeAudioPropertyKey key) =>
        Publish(
            self,
            new WindowsAudioEndpointChangeEventArgs(
                WindowsAudioEndpointChangeKind.PropertyValueChanged,
                ReadDeviceId(deviceId),
                DateTimeOffset.UtcNow,
                propertyFormatId: key.FormatId,
                propertyId: key.PropertyId));

    private static int Publish(nint self, WindowsAudioEndpointChangeEventArgs args)
    {
        try
        {
            GetState(self)?.Publish(args);
        }
        catch
        {
            // Exceptions must never cross the COM callback boundary.
        }

        return S_OK;
    }

    private static CallbackState? GetState(nint self)
    {
        if (self == 0)
        {
            return null;
        }

        var handlePointer = Marshal.ReadIntPtr(self, IntPtr.Size);
        if (handlePointer == 0)
        {
            return null;
        }

        return GCHandle.FromIntPtr(handlePointer).Target as CallbackState;
    }

    private static string? ReadDeviceId(nint pointer) =>
        pointer == 0 ? null : Marshal.PtrToStringUni(pointer);

    private static WindowsAudioDataFlow? MapDataFlow(int value) => value switch
    {
        0 => WindowsAudioDataFlow.Render,
        1 => WindowsAudioDataFlow.Capture,
        _ => null
    };

    private static WindowsAudioEndpointRole? MapRole(int value) => value switch
    {
        0 => WindowsAudioEndpointRole.Console,
        1 => WindowsAudioEndpointRole.Multimedia,
        2 => WindowsAudioEndpointRole.Communications,
        _ => null
    };

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeAudioPropertyKey
    {
        public readonly Guid FormatId;
        public readonly uint PropertyId;
    }

    private sealed class CallbackState
    {
        private readonly Action<WindowsAudioEndpointChangeEventArgs> _changed;
        private nint _instance;
        private GCHandle _handle;
        private int _referenceCount = 1;

        public CallbackState(Action<WindowsAudioEndpointChangeEventArgs> changed)
        {
            _changed = changed;
        }

        public void Attach(nint instance, GCHandle handle)
        {
            _instance = instance;
            _handle = handle;
        }

        public uint AddRef() => checked((uint)Interlocked.Increment(ref _referenceCount));

        public uint Release()
        {
            var remaining = Interlocked.Decrement(ref _referenceCount);
            if (remaining == 0)
            {
                var instance = Interlocked.Exchange(ref _instance, 0);
                if (instance != 0)
                {
                    Marshal.WriteIntPtr(instance, IntPtr.Size, 0);
                    if (_handle.IsAllocated)
                    {
                        _handle.Free();
                    }

                    Marshal.FreeHGlobal(instance);
                }
            }

            return remaining <= 0 ? 0 : checked((uint)remaining);
        }

        public void Publish(WindowsAudioEndpointChangeEventArgs args) => _changed(args);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(nint self, ref Guid interfaceId, out nint instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint AddRefDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceStateChangedDelegate(nint self, nint deviceId, uint newState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceChangedDelegate(nint self, nint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DefaultDeviceChangedDelegate(
        nint self,
        int dataFlow,
        int role,
        nint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyValueChangedDelegate(
        nint self,
        nint deviceId,
        NativeAudioPropertyKey key);
}
