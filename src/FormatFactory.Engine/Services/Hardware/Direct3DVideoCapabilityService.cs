using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public static partial class Direct3DVideoCapabilityService
{
    private static readonly Guid DxgiDeviceInterface = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid D3D11VideoDeviceInterface = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");

    public static string GetSummary()
    {
        var result = Probe();
        if (!result.IsAvailable || result.Value is null)
        {
            return result.Message;
        }

        var capability = result.Value;
        var parts = new List<string>
        {
            LocalizationService.T(
                "NativeCapabilityDirect3DSummaryFeatureLevel",
                capability.FeatureLevel)
        };
        if (!string.IsNullOrWhiteSpace(capability.AdapterName))
        {
            parts.Add(capability.AdapterName);
        }
        if (capability.SupportsDxva)
        {
            parts.Add("DXVA");
        }
        return string.Join(" · ", parts);
    }

    public static NativeCapabilityResult<Direct3DVideoCapabilities> Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeCapabilityResult<Direct3DVideoCapabilities>.NotSupported(
                LocalizationService.T("NativeCapabilityDirect3DWindowsOnly"));
        }

        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr adapter = IntPtr.Zero;
        IntPtr videoDevice = IntPtr.Zero;
        try
        {
            var levels = new[] { FeatureLevel12_1, FeatureLevel12_0, FeatureLevel11_1, FeatureLevel11_0 };
            var result = D3D11CreateDevice(
                IntPtr.Zero,
                1,
                IntPtr.Zero,
                0x820,
                levels,
                (uint)levels.Length,
                7,
                out device,
                out var level,
                out context);
            if (result < 0 || device == IntPtr.Zero)
            {
                return NativeCapabilityResult<Direct3DVideoCapabilities>.Unavailable(
                    LocalizationService.T(
                        "NativeCapabilityDirect3DDeviceCreationFailed",
                        $"0x{result:X8}"));
            }

            var adapterName = string.Empty;
            ulong dedicatedVideoMemory = 0;
            var dxgiGuid = DxgiDeviceInterface;
            var supportsDxgi = Marshal.QueryInterface(device, in dxgiGuid, out dxgiDevice) >= 0
                && dxgiDevice != IntPtr.Zero;
            if (supportsDxgi)
            {
                var getAdapter = GetDelegate<GetAdapterDelegate>(dxgiDevice, 7);
                if (getAdapter(dxgiDevice, out adapter) >= 0 && adapter != IntPtr.Zero)
                {
                    var getDescription = GetDelegate<GetDescDelegate>(adapter, 8);
                    if (getDescription(adapter, out var description) >= 0)
                    {
                        adapterName = description.Description?.TrimEnd('\0', ' ') ?? string.Empty;
                        dedicatedVideoMemory = description.DedicatedVideoMemory;
                    }
                }
            }

            var videoGuid = D3D11VideoDeviceInterface;
            var supportsDxva = Marshal.QueryInterface(device, in videoGuid, out videoDevice) >= 0
                && videoDevice != IntPtr.Zero;
            var decoderProfiles = supportsDxva ? ReadDecoderProfiles(videoDevice) : [];
            var capabilities = new Direct3DVideoCapabilities(
                FormatFeatureLevel(level),
                adapterName,
                dedicatedVideoMemory,
                supportsDxgi,
                supportsDxva,
                decoderProfiles);
            return NativeCapabilityResult<Direct3DVideoCapabilities>.Available(
                capabilities,
                supportsDxva
                    ? LocalizationService.T("NativeCapabilityDirect3DServicesAvailable")
                    : LocalizationService.T("NativeCapabilityDirect3DNoDxva"));
        }
        catch (DllNotFoundException)
        {
            return NativeCapabilityResult<Direct3DVideoCapabilities>.Unavailable(
                LocalizationService.T("NativeCapabilityDirect3DRuntimeMissing"));
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCapabilityResult<Direct3DVideoCapabilities>.Unavailable(
                LocalizationService.T("NativeCapabilityDirect3DDeviceCreationUnavailable"));
        }
        catch (Exception exception)
        {
            return NativeCapabilityResult<Direct3DVideoCapabilities>.Failed(
                LocalizationService.T(
                    "NativeCapabilityDirect3DUnexpectedFailure",
                    exception.Message));
        }
        finally
        {
            if (videoDevice != IntPtr.Zero) Marshal.Release(videoDevice);
            if (adapter != IntPtr.Zero) Marshal.Release(adapter);
            if (dxgiDevice != IntPtr.Zero) Marshal.Release(dxgiDevice);
            if (context != IntPtr.Zero) Marshal.Release(context);
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
    }

    private static string FormatFeatureLevel(FeatureLevel level) => level switch
    {
        FeatureLevel.FeatureLevel12_1 => "12.1",
        FeatureLevel.FeatureLevel12_0 => "12.0",
        FeatureLevel.FeatureLevel11_1 => "11.1",
        FeatureLevel.FeatureLevel11_0 => "11.0",
        _ => $"0x{(uint)level:X}"
    };

    private static IReadOnlyList<Guid> ReadDecoderProfiles(IntPtr videoDevice)
    {
        var getCount = GetDelegate<GetDecoderProfileCountDelegate>(videoDevice, 11);
        var getProfile = GetDelegate<GetDecoderProfileDelegate>(videoDevice, 12);
        var count = Math.Min(getCount(videoDevice), 1024u);
        var profiles = new List<Guid>(checked((int)count));
        for (var index = 0u; index < count; index++)
        {
            if (getProfile(videoDevice, index, out var profile) >= 0 && profile != Guid.Empty)
            {
                profiles.Add(profile);
            }
        }
        return profiles;
    }

    private static T GetDelegate<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static partial int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        FeatureLevel[] featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out FeatureLevel featureLevel,
        out IntPtr immediateContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDelegate(IntPtr self, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescDelegate(IntPtr self, out DxgiAdapterDescription description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetDecoderProfileCountDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDecoderProfileDelegate(IntPtr self, uint index, out Guid profile);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public long AdapterLuid;
    }

    private enum FeatureLevel : uint
    {
        FeatureLevel11_0 = 0xB000,
        FeatureLevel11_1 = 0xB100,
        FeatureLevel12_0 = 0xC000,
        FeatureLevel12_1 = 0xC100
    }

    private const FeatureLevel FeatureLevel11_0 = FeatureLevel.FeatureLevel11_0;
    private const FeatureLevel FeatureLevel11_1 = FeatureLevel.FeatureLevel11_1;
    private const FeatureLevel FeatureLevel12_0 = FeatureLevel.FeatureLevel12_0;
    private const FeatureLevel FeatureLevel12_1 = FeatureLevel.FeatureLevel12_1;
}
