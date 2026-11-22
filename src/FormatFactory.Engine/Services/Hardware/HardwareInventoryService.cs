using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FormatFactoryWinUI.Services;

public sealed record CpuHardwareInfo(
    string Name,
    Architecture ProcessArchitecture,
    Architecture SystemArchitecture,
    int LogicalProcessorCount);

public sealed record GpuHardwareInfo(
    int Index,
    string Name,
    uint VendorId,
    ulong DedicatedVideoMemory)
{
    public uint DeviceId { get; init; }
    public long AdapterLuid { get; init; }

    public GpuHardwareInfo(
        int index,
        string name,
        uint vendorId,
        ulong dedicatedVideoMemory,
        uint deviceId,
        long adapterLuid)
        : this(index, name, vendorId, dedicatedVideoMemory)
    {
        DeviceId = deviceId;
        AdapterLuid = adapterLuid;
    }
}

public sealed record HardwareInventory(
    CpuHardwareInfo Cpu,
    IReadOnlyList<GpuHardwareInfo> Gpus)
{
    public GpuHardwareInfo? DefaultGpu => Gpus.Count == 0 ? null : Gpus[0];
}

public static class HardwareInventoryService
{
    private static readonly Guid DxgiFactory1GuidValue = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const uint DxgiAdapterFlagSoftware = 0x2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const ushort AllProcessorGroups = 0xffff;

    public static HardwareInventory GetCurrent() => new(CreateCpuInfo(), EnumerateGpus());

    internal static IReadOnlyList<GpuHardwareInfo> EnumerateGpus()
    {
        var adapters = new List<GpuHardwareInfo>();
        if (!OperatingSystem.IsWindows())
        {
            return adapters;
        }

        IntPtr factory = IntPtr.Zero;
        try
        {
            var factoryGuid = DxgiFactory1GuidValue;
            var result = CreateDXGIFactory1(ref factoryGuid, out factory);
            if (result < 0 || factory == IntPtr.Zero)
            {
                return adapters;
            }

            var enumAdapters = GetDelegate<EnumAdapters1Delegate>(factory, 12);
            for (uint index = 0; ; index++)
            {
                IntPtr adapter = IntPtr.Zero;
                var enumResult = enumAdapters(factory, index, out adapter);
                if (enumResult == DxgiErrorNotFound)
                {
                    break;
                }

                if (enumResult < 0)
                {
                    if (adapter != IntPtr.Zero)
                    {
                        Marshal.Release(adapter);
                    }
                    break;
                }

                if (adapter == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var getDescription = GetDelegate<GetDesc1Delegate>(adapter, 10);
                    if (getDescription(adapter, out var description) < 0 ||
                        (description.Flags & DxgiAdapterFlagSoftware) != 0)
                    {
                        continue;
                    }

                    adapters.Add(new GpuHardwareInfo(
                        checked((int)index),
                        NormalizeGpuName(description.Description),
                        description.VendorId,
                        description.DedicatedVideoMemory,
                        description.DeviceId,
                        description.AdapterLuid));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read DXGI GPU information: {ex.Message}");
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }
        }

        return adapters.ToArray();
    }

    private static CpuHardwareInfo CreateCpuInfo() => new(
        ReadCpuName(),
        RuntimeInformation.ProcessArchitecture,
        RuntimeInformation.OSArchitecture,
        ReadLogicalProcessorCount());

    private static int ReadLogicalProcessorCount()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var count = GetActiveProcessorCount(AllProcessorGroups);
                if (count > 0)
                {
                    return count > int.MaxValue ? int.MaxValue : (int)count;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read the system logical processor count: {ex.Message}");
            }
        }

        return Math.Max(1, Environment.ProcessorCount);
    }

    private static string ReadCpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var processorKey = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    writable: false);
                if (processorKey?.GetValue("ProcessorNameString") is string registryName &&
                    !string.IsNullOrWhiteSpace(registryName))
                {
                    return registryName.Trim();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read the CPU name from the registry: {ex.Message}");
            }
        }

        var identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return string.IsNullOrWhiteSpace(identifier) ? string.Empty : identifier.Trim();
    }

    private static string NormalizeGpuName(string? description) =>
        string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim();

    private static T GetDelegate<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr adapter, out DxgiAdapterDesc1 description);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
