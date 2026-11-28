using System.Runtime.InteropServices;
using System.Diagnostics;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>A conservative admission estimate, not a promise about actual memory use.</summary>
public sealed record ConversionWorkload(int Weight, long EstimatedPeakBytes)
{
    public static ConversionWorkload Unspecified { get; } = new(1, 0);

    public static ConversionWorkload FromTask(MediaTask task) =>
        ConversionScheduler.Decide<ConversionWorkload>(WorkloadEstimate.FromTask(task));
}

internal sealed record WorkloadEstimate(bool StreamCopy, bool Video, bool Audio, bool Document, bool Image,
    bool HeavyCodec, long Pixels, bool Hdr, int Pages, long FileSize)
{
    public string Action => "Estimate";

    internal static WorkloadEstimate FromTask(MediaTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var probe = task.Probe;
        var audioTarget = FormatCatalog.IsAudioFormat(task.TargetFormat);
        return new(
            StreamCopy: task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase) &&
                task.ContentKind is ContentKind.Audio or ContentKind.Video,
            Video: task.ContentKind == ContentKind.Video && !audioTarget,
            Audio: task.ContentKind == ContentKind.Audio || audioTarget,
            Document: task.ContentKind is ContentKind.Document or ContentKind.Ebook ||
                FormatCatalog.FindById(task.TargetFormatId)?.ContentKind is ContentKind.Document or ContentKind.Ebook ||
                task.EngineId.Contains("document", StringComparison.OrdinalIgnoreCase) || task.Inputs.Count > 1,
            Image: task.ContentKind == ContentKind.Image,
            HeavyCodec: FormatCatalog.VideoCodecForTarget(task.TargetFormat) is "av1" or "vp9",
            Pixels: Math.Max(0L, probe.Width) * Math.Max(0L, probe.Height), Hdr: probe.IsHdr,
            Pages: Math.Max(task.ContentProbe.PageCount, task.Inputs.Count), FileSize: task.FileSize);
    }
}

public readonly record struct ConversionResourceSnapshot(long TotalMemoryBytes, long AvailableMemoryBytes)
{
    public static ConversionResourceSnapshot Unknown => default;
    internal bool IsValid => TotalMemoryBytes > 0 && AvailableMemoryBytes >= 0 && AvailableMemoryBytes <= TotalMemoryBytes;
}

internal static class ConversionResourceMonitor
{
    private static readonly object Gate = new();
    private static long _capturedAt;
    private static ConversionResourceSnapshot _snapshot;

    internal static ConversionResourceSnapshot Capture()
    {
        if (!OperatingSystem.IsWindows()) return ConversionResourceSnapshot.Unknown;
        lock (Gate)
        {
            if (_capturedAt != 0 && Stopwatch.GetElapsedTime(_capturedAt) < TimeSpan.FromMilliseconds(500)) return _snapshot;
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            _snapshot = GlobalMemoryStatusEx(ref status)
                ? new((long)Math.Min(status.TotalPhysical, (ulong)long.MaxValue),
                    (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue))
                : ConversionResourceSnapshot.Unknown;
            _capturedAt = Stopwatch.GetTimestamp();
            return _snapshot;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}

internal static class ConversionAdmissionPolicy
{
    internal static bool CanAdmit(ConversionWorkload candidate, IReadOnlyCollection<ConversionWorkload> running,
        int parallelLimit, ConversionResourceSnapshot resources)
    {
        return ConversionScheduler.Decide<bool>(new
        {
            Action = "Admit", Candidate = candidate, Running = running, ParallelLimit = parallelLimit,
            Total = resources.TotalMemoryBytes, Available = resources.AvailableMemoryBytes
        });
    }
}
