using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

internal static class PowerRequestService
{
    private static readonly ConversionPowerRequestManager Manager = new(new NativePowerRequestPlatform());

    public static IDisposable Acquire(string reason) => Manager.Acquire(reason);
}

internal interface IPowerRequestPlatform
{
    nint Create(string reason);

    bool Set(nint handle, PowerRequestType requestType);

    bool Clear(nint handle, PowerRequestType requestType);

    void Close(nint handle);
}

internal enum PowerRequestType : uint
{
    SystemRequired = 1,
    ExecutionRequired = 3
}

internal sealed class ConversionPowerRequestManager
{
    private readonly object _gate = new();
    private readonly IPowerRequestPlatform _platform;
    private nint _handle;
    private int _leases;

    public ConversionPowerRequestManager(IPowerRequestPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    internal int ActiveLeaseCount
    {
        get
        {
            lock (_gate)
            {
                return _leases;
            }
        }
    }

    public IDisposable Acquire(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_leases == 0)
            {
                var handle = _platform.Create(reason);
                if (handle != 0 && handle != new nint(-1))
                {
                    try
                    {
                        var system = _platform.Set(handle, PowerRequestType.SystemRequired);
                        var execution = _platform.Set(handle, PowerRequestType.ExecutionRequired);
                        if (!system || !execution)
                        {
                            NativeDiagnostics.Log.NativeOperationFailed(
                                nameof(PowerRequestService),
                                Marshal.GetLastPInvokeError(),
                                "PowerSetRequest");
                        }

                        _handle = handle;
                    }
                    catch
                    {
                        _platform.Close(handle);
                        throw;
                    }
                }
                else
                {
                    NativeDiagnostics.Log.NativeOperationFailed(
                        nameof(PowerRequestService),
                        Marshal.GetLastPInvokeError(),
                        "PowerCreateRequest");
                }
            }

            _leases++;
        }

        return new Lease(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_leases <= 0 || --_leases != 0)
            {
                return;
            }

            if (_handle == 0)
            {
                return;
            }

            _platform.Clear(_handle, PowerRequestType.SystemRequired);
            _platform.Clear(_handle, PowerRequestType.ExecutionRequired);
            _platform.Close(_handle);
            _handle = 0;
        }
    }

    private sealed class Lease : IDisposable
    {
        private ConversionPowerRequestManager? _owner;

        public Lease(ConversionPowerRequestManager owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

internal sealed class NativePowerRequestPlatform : IPowerRequestPlatform
{
    public nint Create(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext
            {
                Version = 0,
                Flags = SimpleReasonString,
                Reason = text
            };
            return PowerCreateRequest(ref context);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    public bool Set(nint handle, PowerRequestType requestType) =>
        OperatingSystem.IsWindows() && PowerSetRequest(handle, requestType);

    public bool Clear(nint handle, PowerRequestType requestType) =>
        OperatingSystem.IsWindows() && PowerClearRequest(handle, requestType);

    public void Close(nint handle)
    {
        if (OperatingSystem.IsWindows() && handle != 0)
        {
            CloseHandle(handle);
        }
    }

    private const uint SimpleReasonString = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint request, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint request, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
