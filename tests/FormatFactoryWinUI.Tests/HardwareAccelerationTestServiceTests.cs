using System.Runtime.InteropServices;
using System.Text.Json;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class HardwareAccelerationTestServiceTests
{
    [Theory]
    [InlineData("-hwaccels")]
    [InlineData("-decoders")]
    public async Task RunAsync_WhenInitialProbeFails_ThrowsWithStderrAndDoesNotCreateReport(string failedCommand)
    {
        var cachePath = TemporaryCachePath();
        HardwareAccelerationTestService.SetCurrentReport(null);
        var service = new HardwareAccelerationTestService(cachePath, (executable, arguments, cancellationToken) =>
        {
            var command = arguments.Contains("-hwaccels") ? "-hwaccels" : "-decoders";
            return Task.FromResult(command == failedCommand
                ? new HardwareProbeProcessResult(false, string.Empty, "probe stderr marker")
                : new HardwareProbeProcessResult(true, "cuda", string.Empty));
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunAsync("ffmpeg.exe", CreateInventory()));

        Assert.Contains(failedCommand, exception.Message, StringComparison.Ordinal);
        Assert.Contains("probe stderr marker", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(cachePath));
        Assert.True(HardwareAccelerationTestService.SupportsHardwareDecode("h264"));
    }

    [Fact]
    public async Task RunAsync_WhenSamplesCannotBeCreated_LeavesHardwareStatusesUntested()
    {
        var cachePath = TemporaryCachePath();
        var service = new HardwareAccelerationTestService(cachePath, (executable, arguments, cancellationToken) =>
        {
            if (arguments.Contains("-hwaccels"))
            {
                return Task.FromResult(new HardwareProbeProcessResult(true, "Hardware acceleration methods:\ncuda", string.Empty));
            }

            if (arguments.Contains("-decoders"))
            {
                return Task.FromResult(new HardwareProbeProcessResult(
                    true,
                    " V..... h264 H.264\n V..... vc1 VC-1",
                    string.Empty));
            }

            return Task.FromResult(new HardwareProbeProcessResult(false, string.Empty, "encoder unavailable"));
        });
        var gpu = new GpuHardwareInfo(0, "NVIDIA", HardwareDecodeGrouping.NvidiaVendorId, 1);
        var inventory = CreateInventory(gpu);

        var report = await service.RunAsync("ffmpeg.exe", inventory);

        Assert.True(report.TestCompleted);
        Assert.Equal(HardwareDecodeReport.CurrentSchemaVersion, report.SchemaVersion);
        var h264 = Assert.Single(report.Decoders, capability => capability.Codec == "h264");
        Assert.True(h264.SoftwareTestCompleted);
        Assert.True(h264.SoftwareSupported);
        Assert.False(h264.TestCompleted);
        Assert.Empty(h264.TestedBackendIds);
        var vc1 = Assert.Single(report.Decoders, capability => capability.Codec == "vc1");
        Assert.True(vc1.SoftwareSupported);
        Assert.False(vc1.TestCompleted);

        var groups = HardwareDecodeGrouping.Group(inventory, report);
        var cpu = Assert.Single(groups, group => group.Kind == HardwareDecodeGroupKind.Cpu);
        var nvidia = Assert.Single(groups, group => group.Gpu?.Index == gpu.Index);
        Assert.True(Find(cpu, HardwareDecodeGrouping.SoftwareBackendId, "h264").Supported);
        Assert.Null(Find(nvidia, HardwareDecodeGrouping.CudaBackendId, "h264").Supported);
        Assert.Null(Find(nvidia, HardwareDecodeGrouping.CudaBackendId, "vc1").Supported);
    }

    [Fact]
    public void CurrentSchema_RequiresExplicitCompletedReportAndBackendStatuses()
    {
        Assert.Equal(3, HardwareDecodeReport.CurrentSchemaVersion);
        var report = new HardwareDecodeReport
        {
            SchemaVersion = HardwareDecodeReport.CurrentSchemaVersion,
            TestCompleted = false,
            Decoders =
            [
                new HardwareDecodeCapability
                {
                    Codec = "h264",
                    SoftwareTestCompleted = true,
                    SoftwareSupported = true,
                    TestCompleted = true,
                    BackendIds = [HardwareDecodeGrouping.CudaBackendId],
                    TestedBackendIds = [HardwareDecodeGrouping.CudaBackendId]
                }
            ]
        };

        var groups = HardwareDecodeGrouping.Group(
            CreateInventory(new GpuHardwareInfo(0, "NVIDIA", HardwareDecodeGrouping.NvidiaVendorId, 1)),
            report);

        Assert.All(groups.SelectMany(group => group.Decoders), assignment => Assert.Null(assignment.Supported));
    }

    [Fact]
    public async Task LoadCachedAsync_RejectsChangedGpuIdentityAndChangedFfmpegBinary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cachePath = Path.Combine(directory, "hardware-decode-report.json");
        var ffmpegPath = Path.Combine(directory, "ffmpeg.exe");
        await File.WriteAllBytesAsync(ffmpegPath, [1, 2, 3]);
        var logs = new List<string>();
        var service = new HardwareAccelerationTestService(
            cachePath,
            (_, _, _) => Task.FromResult(new HardwareProbeProcessResult(true, string.Empty, string.Empty)),
            logs.Add);
        var originalGpu = new GpuHardwareInfo(0, "GPU A", 0x10DE, 8, 0x2684, 12345);
        var originalInventory = CreateInventory(originalGpu);
        var report = CompletedReport(originalInventory, ffmpegPath);
        await service.SaveAsync(report);

        Assert.NotNull(await service.LoadCachedAsync(ffmpegPath, originalInventory));

        var changedGpu = new GpuHardwareInfo(0, "GPU B", 0x10DE, 8, 0x2685, 54321);
        Assert.Null(await service.LoadCachedAsync(ffmpegPath, CreateInventory(changedGpu)));
        Assert.Contains(logs, message => message.Contains("hardware", StringComparison.OrdinalIgnoreCase));

        await service.SaveAsync(report);
        await File.AppendAllTextAsync(ffmpegPath, "changed");
        File.SetLastWriteTimeUtc(ffmpegPath, DateTime.UtcNow.AddMinutes(1));
        Assert.Null(await service.LoadCachedAsync(ffmpegPath, originalInventory));
    }

    [Fact]
    public async Task LoadCachedAsync_RejectsPreviousSchemaAndSaveRejectsIncompleteReport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cachePath = Path.Combine(directory, "hardware-decode-report.json");
        var ffmpegPath = Path.Combine(directory, "ffmpeg.exe");
        await File.WriteAllBytesAsync(ffmpegPath, [1]);
        var service = new HardwareAccelerationTestService(
            cachePath,
            (_, _, _) => Task.FromResult(new HardwareProbeProcessResult(true, string.Empty, string.Empty)));
        var inventory = CreateInventory();
        var legacy = CompletedReport(inventory, ffmpegPath);
        legacy.SchemaVersion = HardwareDecodeReport.CurrentSchemaVersion - 1;
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(legacy));

        Assert.Null(await service.LoadCachedAsync(ffmpegPath, inventory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(new HardwareDecodeReport
        {
            SchemaVersion = HardwareDecodeReport.CurrentSchemaVersion,
            TestCompleted = false,
            ContextFingerprint = HardwareDecodeContextFingerprint.Create(inventory, ffmpegPath)
        }));
    }

    private static HardwareInventory CreateInventory(params GpuHardwareInfo[] gpus) => new(
        new CpuHardwareInfo("Test CPU", Architecture.X64, Architecture.X64, 8),
        gpus);

    private static HardwareDecodeAssignment Find(HardwareDecodeGroup group, string backendId, string codec) =>
        Assert.Single(group.Decoders, assignment => assignment.BackendId == backendId && assignment.Codec == codec);

    private static HardwareDecodeReport CompletedReport(HardwareInventory inventory, string ffmpegPath) => new()
    {
        SchemaVersion = HardwareDecodeReport.CurrentSchemaVersion,
        TestedAt = DateTimeOffset.UtcNow,
        TestCompleted = true,
        ContextFingerprint = HardwareDecodeContextFingerprint.Create(inventory, ffmpegPath)
    };

    private static string TemporaryCachePath() => Path.Combine(
        Path.GetTempPath(),
        "FormatFactoryWinUI.Tests",
        Guid.NewGuid().ToString("N"),
        "hardware-decode-report.json");
}
