using System.Runtime.InteropServices;
using System.Diagnostics;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>A conservative admission estimate, not a promise about actual memory use.</summary>
public sealed record ConversionWorkload(int Weight, long EstimatedPeakBytes)
{
    private const long MiB = 1024 * 1024;
    public static ConversionWorkload Unspecified { get; } = new(1, 0);

    public static ConversionWorkload FromTask(MediaTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase) &&
            task.ContentKind is ContentKind.Video or ContentKind.Audio)
            return new(1, 96 * MiB);

        var probe = task.Probe;
        var pixels = Math.Max(0L, probe.Width) * Math.Max(0L, probe.Height);
        if (task.ContentKind == ContentKind.Video && !FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            var weight = pixels >= 7680L * 4320 ? 4 : pixels >= 3840L * 2160 ? 2 : 1;
            if (FormatCatalog.VideoCodecForTarget(task.TargetFormat) is "av1" or "vp9") weight = Math.Max(2, weight);
            // Decoder surfaces, frame/filter buffers and encoder lookahead all scale with source size.
            var bytes = 256 * MiB + Math.Min(pixels, 268_435_456L) * (probe.IsHdr ? 192 : 128);
            return new(weight, bytes);
        }
        if (task.ContentKind == ContentKind.Audio || FormatCatalog.IsAudioFormat(task.TargetFormat))
            return new(1, 128 * MiB);
        if (task.ContentKind is ContentKind.Document or ContentKind.Ebook ||
            FormatCatalog.FindById(task.TargetFormatId)?.ContentKind is ContentKind.Document or ContentKind.Ebook ||
            task.EngineId.Contains("document", StringComparison.OrdinalIgnoreCase) || task.Inputs.Count > 1)
        {
            var pages = Math.Max(task.ContentProbe.PageCount, task.Inputs.Count);
            var large = pages >= 100 || task.FileSize >= 100 * MiB;
            return new(large ? 2 : 1, 384 * MiB + Math.Min(Math.Max(0, pages), 1024) * MiB);
        }
        if (task.ContentKind == ContentKind.Image)
            return new(pixels >= 80_000_000 ? 3 : pixels >= 24_000_000 ? 2 : 1,
                64 * MiB + Math.Min(pixels, 268_435_456L) * 32);
        return new(1, 256 * MiB);
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
        // A large or unknown task must still make progress, even on a small/pressured machine.
        if (running.Count == 0) return true;
        if (running.Count >= parallelLimit) return false;
        var weight = running.Sum(item => Math.Clamp(item.Weight, 1, 4));
        if (weight + Math.Clamp(candidate.Weight, 1, 4) > parallelLimit) return false;
        if (!resources.IsValid || candidate.EstimatedPeakBytes <= 0) return true;

        var reserve = Math.Max(512L * 1024 * 1024, resources.TotalMemoryBytes / 10);
        if (candidate.EstimatedPeakBytes > Math.Max(0, resources.AvailableMemoryBytes - reserve)) return false;
        // Current availability reflects already committed memory; the total budget separately
        // guards against multiple jobs reaching their estimated peaks at the same time.
        var peakBudget = resources.TotalMemoryBytes - resources.TotalMemoryBytes / 3;
        var estimated = running.Aggregate(0L, (sum, item) =>
            sum + Math.Clamp(item.EstimatedPeakBytes, 0, peakBudget - sum));
        return candidate.EstimatedPeakBytes <= peakBudget - estimated;
    }
}
