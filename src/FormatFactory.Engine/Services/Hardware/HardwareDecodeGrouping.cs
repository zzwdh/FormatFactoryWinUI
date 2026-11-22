using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public sealed class HardwareDecodeCapability
{
    public string Codec { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Supported { get; set; }
    public string Backend { get; set; } = string.Empty;
    public bool TestCompleted { get; set; }
    public bool SoftwareTestCompleted { get; set; }
    public bool SoftwareSupported { get; set; }
    public List<string> BackendIds { get; set; } = [];
    public List<string> TestedBackendIds { get; set; } = [];
}

public sealed class HardwareDecodeReport
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; }
    public DateTimeOffset TestedAt { get; set; }
    public bool TestCompleted { get; set; }
    public HardwareDecodeContextFingerprint? ContextFingerprint { get; set; }
    public List<HardwareDecodeCapability> Decoders { get; set; } = [];
}

public sealed class HardwareDecodeGpuFingerprint
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public long AdapterLuid { get; set; }
    public ulong DedicatedVideoMemory { get; set; }
}

public sealed class HardwareDecodeContextFingerprint
{
    public string CpuName { get; set; } = string.Empty;
    public Architecture ProcessArchitecture { get; set; }
    public Architecture SystemArchitecture { get; set; }
    public int LogicalProcessorCount { get; set; }
    public List<HardwareDecodeGpuFingerprint> Gpus { get; set; } = [];
    public string FfmpegCanonicalPath { get; set; } = string.Empty;
    public long FfmpegFileSize { get; set; }
    public long FfmpegLastWriteTimeUtcTicks { get; set; }

    public static HardwareDecodeContextFingerprint Create(HardwareInventory inventory, string ffmpegPath)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);

        var canonicalPath = Path.GetFullPath(ffmpegPath.Trim());
        var file = new FileInfo(canonicalPath);
        file.Refresh();
        return new HardwareDecodeContextFingerprint
        {
            CpuName = inventory.Cpu.Name?.Trim() ?? string.Empty,
            ProcessArchitecture = inventory.Cpu.ProcessArchitecture,
            SystemArchitecture = inventory.Cpu.SystemArchitecture,
            LogicalProcessorCount = inventory.Cpu.LogicalProcessorCount,
            Gpus = inventory.Gpus.Select(gpu => new HardwareDecodeGpuFingerprint
            {
                Index = gpu.Index,
                Name = gpu.Name?.Trim() ?? string.Empty,
                VendorId = gpu.VendorId,
                DeviceId = gpu.DeviceId,
                AdapterLuid = gpu.AdapterLuid,
                DedicatedVideoMemory = gpu.DedicatedVideoMemory
            }).ToList(),
            FfmpegCanonicalPath = canonicalPath,
            FfmpegFileSize = file.Exists ? file.Length : -1,
            FfmpegLastWriteTimeUtcTicks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0
        };
    }

    public bool Matches(HardwareDecodeContextFingerprint? current)
    {
        if (current is null ||
            !string.Equals(CpuName?.Trim(), current.CpuName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            ProcessArchitecture != current.ProcessArchitecture ||
            SystemArchitecture != current.SystemArchitecture ||
            LogicalProcessorCount != current.LogicalProcessorCount ||
            !string.Equals(
                FfmpegCanonicalPath,
                current.FfmpegCanonicalPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            FfmpegFileSize != current.FfmpegFileSize ||
            FfmpegLastWriteTimeUtcTicks != current.FfmpegLastWriteTimeUtcTicks ||
            Gpus is null ||
            current.Gpus is null ||
            Gpus.Count != current.Gpus.Count)
        {
            return false;
        }

        for (var index = 0; index < Gpus.Count; index++)
        {
            var cachedGpu = Gpus[index];
            var currentGpu = current.Gpus[index];
            if (cachedGpu.Index != currentGpu.Index ||
                cachedGpu.VendorId != currentGpu.VendorId ||
                cachedGpu.DeviceId != currentGpu.DeviceId ||
                cachedGpu.AdapterLuid != currentGpu.AdapterLuid ||
                cachedGpu.DedicatedVideoMemory != currentGpu.DedicatedVideoMemory ||
                !string.Equals(cachedGpu.Name?.Trim(), currentGpu.Name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record HardwareDecodeCodec(string Codec, string DisplayName);

public enum HardwareDecodeGroupKind
{
    Cpu,
    Gpu
}

public sealed record HardwareDecodeAssignment(
    string Codec,
    string DisplayName,
    string BackendId,
    bool IsSoftware,
    bool? Supported);

public sealed record HardwareDecodeGroup(
    string HardwareId,
    HardwareDecodeGroupKind Kind,
    string Name,
    CpuHardwareInfo? Cpu,
    GpuHardwareInfo? Gpu,
    IReadOnlyList<HardwareDecodeAssignment> Decoders);

public static class HardwareDecodeGrouping
{
    public const string SoftwareBackendId = "software";
    public const string CudaBackendId = "cuda";
    public const string QuickSyncBackendId = "qsv";
    public const string Direct3D11BackendId = "d3d11va";
    public const string Dxva2BackendId = "dxva2";

    public const uint NvidiaVendorId = 0x10DE;
    public const uint IntelVendorId = 0x8086;

    public static IReadOnlyList<HardwareDecodeCodec> Codecs { get; } =
    [
        new("h264", "H.264"),
        new("hevc", "HEVC"),
        new("vp9", "VP9"),
        new("av1", "AV1"),
        new("mpeg2video", "MPEG-2 Video"),
        new("vc1", "VC-1"),
        new("mjpeg", "Motion JPEG")
    ];

    public static IReadOnlyList<HardwareDecodeGroup> Group(
        HardwareInventory inventory,
        HardwareDecodeReport? report)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var testedCapabilities = CreateTestedCapabilityLookup(report);
        var groups = new List<HardwareDecodeGroup>(inventory.Gpus.Count + 1)
        {
            new(
                "cpu",
                HardwareDecodeGroupKind.Cpu,
                inventory.Cpu.Name,
                inventory.Cpu,
                null,
                CreateAssignments(SoftwareBackendId, isSoftware: true, testedCapabilities))
        };

        var defaultGpu = inventory.DefaultGpu;
        var nvidiaGpu = inventory.Gpus.FirstOrDefault(gpu => gpu.VendorId == NvidiaVendorId);
        var intelGpu = inventory.Gpus.FirstOrDefault(gpu => gpu.VendorId == IntelVendorId);

        foreach (var gpu in inventory.Gpus)
        {
            var backends = new List<string>(4);
            if (gpu.Index == nvidiaGpu?.Index)
            {
                backends.Add(CudaBackendId);
            }

            if (gpu.Index == intelGpu?.Index)
            {
                backends.Add(QuickSyncBackendId);
            }

            if (gpu.Index == defaultGpu?.Index)
            {
                backends.Add(Direct3D11BackendId);
                backends.Add(Dxva2BackendId);
            }

            groups.Add(new HardwareDecodeGroup(
                $"gpu:{gpu.Index}",
                HardwareDecodeGroupKind.Gpu,
                gpu.Name,
                null,
                gpu,
                backends
                    .SelectMany(backend => CreateAssignments(backend, isSoftware: false, testedCapabilities))
                    .ToArray()));
        }

        return groups;
    }

    private static IReadOnlyDictionary<string, HardwareDecodeCapability>? CreateTestedCapabilityLookup(
        HardwareDecodeReport? report)
    {
        if (report?.SchemaVersion != HardwareDecodeReport.CurrentSchemaVersion ||
            report.TestCompleted != true ||
            report.Decoders is null)
        {
            return null;
        }

        return report.Decoders
            .Where(capability => !string.IsNullOrWhiteSpace(capability.Codec))
            .GroupBy(capability => capability.Codec, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<HardwareDecodeAssignment> CreateAssignments(
        string backendId,
        bool isSoftware,
        IReadOnlyDictionary<string, HardwareDecodeCapability>? testedCapabilities)
    {
        return Codecs.Select(codec =>
        {
            bool? supported = null;
            if (testedCapabilities?.TryGetValue(codec.Codec, out var capability) == true)
            {
                supported = isSoftware
                    ? capability.SoftwareTestCompleted
                        ? capability.SoftwareSupported
                        : null
                    : capability.TestCompleted &&
                      capability.TestedBackendIds?.Contains(backendId, StringComparer.OrdinalIgnoreCase) == true
                        ? capability.BackendIds?.Contains(backendId, StringComparer.OrdinalIgnoreCase) == true
                        : null;
            }

            return new HardwareDecodeAssignment(
                codec.Codec,
                codec.DisplayName,
                backendId,
                isSoftware,
                supported);
        }).ToArray();
    }
}
