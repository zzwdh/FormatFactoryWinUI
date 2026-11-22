using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class HardwareDecodeGroupingTests
{
    [Fact]
    public void Inventory_ReportsArchitectureAndLogicalProcessorCount()
    {
        var inventory = HardwareInventoryService.GetCurrent();

        Assert.Equal(RuntimeInformation.ProcessArchitecture, inventory.Cpu.ProcessArchitecture);
        Assert.Equal(RuntimeInformation.OSArchitecture, inventory.Cpu.SystemArchitecture);
        Assert.True(inventory.Cpu.LogicalProcessorCount >= 1);
        Assert.NotNull(inventory.Gpus);
    }

    [Fact]
    public void Group_AssignsBackendsToOnlyTheirOwningGpu()
    {
        var defaultAmd = new GpuHardwareInfo(0, "AMD default", 0x1002, 8UL * 1024 * 1024 * 1024);
        var firstNvidia = new GpuHardwareInfo(1, "NVIDIA first", HardwareDecodeGrouping.NvidiaVendorId, 12UL * 1024 * 1024 * 1024);
        var firstIntel = new GpuHardwareInfo(2, "Intel first", HardwareDecodeGrouping.IntelVendorId, 2UL * 1024 * 1024 * 1024);
        var secondNvidia = new GpuHardwareInfo(3, "NVIDIA second", HardwareDecodeGrouping.NvidiaVendorId, 6UL * 1024 * 1024 * 1024);
        var inventory = CreateInventory(defaultAmd, firstNvidia, firstIntel, secondNvidia);
        var report = CurrentReport(
            Capability("h264", software: true, "cuda", "qsv", "d3d11va", "dxva2"),
            Capability("hevc", software: false, "cuda"));

        var groups = HardwareDecodeGrouping.Group(inventory, report);

        Assert.Equal(5, groups.Count);
        Assert.Same(defaultAmd, inventory.DefaultGpu);

        var cpu = Assert.Single(groups, group => group.Kind == HardwareDecodeGroupKind.Cpu);
        Assert.Equal([HardwareDecodeGrouping.SoftwareBackendId], BackendIds(cpu));
        Assert.True(Find(cpu, "software", "h264").Supported);
        Assert.False(Find(cpu, "software", "hevc").Supported);
        Assert.Null(Find(cpu, "software", "vp9").Supported);

        var amd = GpuGroup(groups, defaultAmd.Index);
        Assert.Equal(
            [HardwareDecodeGrouping.Direct3D11BackendId, HardwareDecodeGrouping.Dxva2BackendId],
            BackendIds(amd));
        Assert.True(Find(amd, "d3d11va", "h264").Supported);
        Assert.False(Find(amd, "d3d11va", "hevc").Supported);

        var nvidia = GpuGroup(groups, firstNvidia.Index);
        Assert.Equal([HardwareDecodeGrouping.CudaBackendId], BackendIds(nvidia));
        Assert.True(Find(nvidia, "cuda", "hevc").Supported);

        var intel = GpuGroup(groups, firstIntel.Index);
        Assert.Equal([HardwareDecodeGrouping.QuickSyncBackendId], BackendIds(intel));
        Assert.True(Find(intel, "qsv", "h264").Supported);
        Assert.False(Find(intel, "qsv", "hevc").Supported);

        Assert.Empty(GpuGroup(groups, secondNvidia.Index).Decoders);
    }

    [Fact]
    public void Group_WithNoGpu_ReturnsOnlySoftwareOnCpu()
    {
        var report = CurrentReport(Capability("h264", software: true, "cuda", "d3d11va"));

        var group = Assert.Single(HardwareDecodeGrouping.Group(CreateInventory(), report));

        Assert.Equal(HardwareDecodeGroupKind.Cpu, group.Kind);
        Assert.Equal(HardwareDecodeGrouping.Codecs.Count, group.Decoders.Count);
        Assert.All(group.Decoders, assignment =>
        {
            Assert.True(assignment.IsSoftware);
            Assert.Equal(HardwareDecodeGrouping.SoftwareBackendId, assignment.BackendId);
        });
        Assert.True(Find(group, "software", "h264").Supported);
    }

    [Fact]
    public void Group_IgnoresLegacyReportFieldsWithoutCurrentSchema()
    {
        var gpu = new GpuHardwareInfo(4, "NVIDIA", HardwareDecodeGrouping.NvidiaVendorId, 1);
        var legacyReport = new HardwareDecodeReport
        {
            Decoders =
            [
                new HardwareDecodeCapability
                {
                    Codec = "h264",
                    DisplayName = "H.264",
                    Supported = true,
                    Backend = "NVIDIA CUDA"
                }
            ]
        };

        var groups = HardwareDecodeGrouping.Group(CreateInventory(gpu), legacyReport);

        Assert.All(groups.SelectMany(group => group.Decoders), assignment => Assert.Null(assignment.Supported));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Group_EmptyOrMissingReport_LeavesAllStatusesUntested(bool useEmptyCurrentReport)
    {
        var gpu = new GpuHardwareInfo(7, "Intel", HardwareDecodeGrouping.IntelVendorId, 1);
        var report = useEmptyCurrentReport ? CurrentReport() : null;

        var groups = HardwareDecodeGrouping.Group(CreateInventory(gpu), report);

        Assert.Equal(2, groups.Count);
        Assert.All(groups.SelectMany(group => group.Decoders), assignment => Assert.Null(assignment.Supported));
    }

    private static HardwareInventory CreateInventory(params GpuHardwareInfo[] gpus) => new(
        new CpuHardwareInfo("Test CPU", Architecture.X64, Architecture.X64, 16),
        gpus);

    private static HardwareDecodeReport CurrentReport(params HardwareDecodeCapability[] capabilities) => new()
    {
        SchemaVersion = HardwareDecodeReport.CurrentSchemaVersion,
        TestedAt = DateTimeOffset.UtcNow,
        TestCompleted = true,
        Decoders = capabilities.ToList()
    };

    private static HardwareDecodeCapability Capability(
        string codec,
        bool software,
        params string[] backendIds) => new()
    {
        Codec = codec,
        DisplayName = codec,
        TestCompleted = true,
        SoftwareTestCompleted = true,
        SoftwareSupported = software,
        Supported = backendIds.Length > 0,
        BackendIds = backendIds.ToList(),
        TestedBackendIds =
        [
            HardwareDecodeGrouping.CudaBackendId,
            HardwareDecodeGrouping.QuickSyncBackendId,
            HardwareDecodeGrouping.Direct3D11BackendId,
            HardwareDecodeGrouping.Dxva2BackendId
        ]
    };

    private static HardwareDecodeGroup GpuGroup(
        IReadOnlyList<HardwareDecodeGroup> groups,
        int gpuIndex) => Assert.Single(groups, group => group.Gpu?.Index == gpuIndex);

    private static HardwareDecodeAssignment Find(
        HardwareDecodeGroup group,
        string backendId,
        string codec) => Assert.Single(group.Decoders, assignment =>
            assignment.BackendId == backendId && assignment.Codec == codec);

    private static string[] BackendIds(HardwareDecodeGroup group) => group.Decoders
        .Select(assignment => assignment.BackendId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
