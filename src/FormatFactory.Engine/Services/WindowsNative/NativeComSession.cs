using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

internal sealed partial class NativeComSession : IDisposable
{
    private const uint CoinitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private readonly bool _mustUninitialize;

    private NativeComSession(bool mustUninitialize)
    {
        _mustUninitialize = mustUninitialize;
    }

    public static NativeCapabilityResult<NativeComSession> TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeCapabilityResult<NativeComSession>.NotSupported("Windows COM is not available on this platform.");
        }

        try
        {
            var result = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
            if (result >= 0)
            {
                return NativeCapabilityResult<NativeComSession>.Available(new NativeComSession(true));
            }

            if (result == RpcEChangedMode)
            {
                return NativeCapabilityResult<NativeComSession>.Available(new NativeComSession(false));
            }

            return NativeCapabilityResult<NativeComSession>.Unavailable($"COM initialization failed (0x{result:X8}).");
        }
        catch (DllNotFoundException)
        {
            return NativeCapabilityResult<NativeComSession>.Unavailable("Windows COM runtime was not found.");
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCapabilityResult<NativeComSession>.Unavailable("Windows COM initialization is unavailable.");
        }
    }

    public void Dispose()
    {
        if (_mustUninitialize)
        {
            CoUninitialize();
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
