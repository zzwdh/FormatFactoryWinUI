using System.Runtime.InteropServices;
using System.Text;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Reserves the estimated bytes still to be written by concurrent conversions on a volume.</summary>
public sealed class DiskSpaceBudgetService
{
    private const long MiB = 1024 * 1024;
    private const long SafetyMargin = 64 * MiB;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Reservation> _reservations = [];
    private readonly Func<string, DiskSpaceInfo?> _readSpace;
    private readonly Func<string, long> _writtenBytes;

    public static DiskSpaceBudgetService Shared { get; } = new();

    public DiskSpaceBudgetService() : this(ReadDiskSpace, ReadWrittenBytes) { }

    internal DiskSpaceBudgetService(Func<string, DiskSpaceInfo?> readSpace, Func<string, long>? writtenBytes = null)
    {
        _readSpace = readSpace;
        _writtenBytes = writtenBytes ?? ReadWrittenBytes;
    }

    public IDisposable Reserve(MediaTask task, string outputDirectory, string? temporaryPath = null) =>
        Reserve(task, outputDirectory, temporaryPath, Guid.NewGuid());

    internal IDisposable Reserve(MediaTask task, string outputDirectory, string? temporaryPath, Guid operationId) =>
        ReserveBytesCore(EstimateRequiredBytes(task), outputDirectory, temporaryPath, task.Id, operationId);

    public IDisposable ReserveBytes(long estimatedBytes, string outputDirectory, string? temporaryPath = null)
        => ReserveBytesCore(estimatedBytes, outputDirectory, temporaryPath, null, Guid.NewGuid());

    private IDisposable ReserveBytesCore(long estimatedBytes, string outputDirectory, string? temporaryPath,
        Guid? taskId, Guid operationId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytes);
        lock (_gate)
        {
            // An unavailable query (for example an unsupported network provider) is not zero free space.
            DiskSpaceInfo? space;
            try { space = _readSpace(outputDirectory); }
            catch (Exception error)
            {
                StorageFailureDiagnostics.Record(error, new(StorageFailureStage.BudgetQuery, operationId,
                    taskId, StorageFailureDiagnostics.ClassifyPath(outputDirectory), null));
                throw;
            }
            if (space is null) return new BudgetLease(null);
            var matching = _reservations.Values
                .Where(item => string.Equals(item.Volume, space.Volume, StringComparison.OrdinalIgnoreCase)).ToArray();
            var outstanding = 0L;
            var written = 0L;
            foreach (var item in matching)
            {
                var observedWritten = item.TemporaryPath is null ? 0 : _writtenBytes(item.TemporaryPath);
                written = SaturatingAdd(written, Math.Max(0, observedWritten));
                outstanding = SaturatingAdd(outstanding, Math.Max(0, item.EstimatedBytes - observedWritten));
            }
            var required = SaturatingAdd(estimatedBytes, SafetyMargin);
            var available = Math.Max(0, space.AvailableBytes - outstanding);
            if (available < required)
            {
                var error = new IOException(LocalizationService.T("OutputDiskInsufficientSpace", outputDirectory,
                    required / (double)MiB, available / (double)MiB));
                StorageFailureDiagnostics.Record(error, new(StorageFailureStage.BudgetReserve, operationId,
                    taskId, StorageFailureDiagnostics.ClassifyPath(outputDirectory), null,
                    new(estimatedBytes, required, space.AvailableBytes, available, outstanding, written, matching.Length)));
                throw error;
            }
            var id = Guid.NewGuid();
            _reservations.Add(id, new Reservation(space.Volume, estimatedBytes, temporaryPath));
            return new BudgetLease(() => { lock (_gate) _reservations.Remove(id); });
        }
    }

    // This is a conservative preflight estimate, not a promised final size. Variable-rate encoders
    // and external tools can exceed it; an actual disk-full error still fails the conversion safely.
    public static long EstimateRequiredBytes(MediaTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var sourceBytes = Math.Max(task.FileSize, task.ContentProbe.FileSize);
        var inputBytes = 0L;
        foreach (var input in task.InputPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (File.Exists(input)) inputBytes = SaturatingAdd(inputBytes, new FileInfo(input).Length); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        sourceBytes = Math.Max(sourceBytes, inputBytes);
        if (task.Inputs.Count > 1)
            sourceBytes = Math.Max(sourceBytes, task.Inputs.Aggregate(0L,
                (total, input) => SaturatingAdd(total, Math.Max(0, input.Probe?.FileSize ?? 0))));
        if (OpticalDiscFormatIds.IsPhysicalDisc(task.SourceFormatId))
        {
            try { sourceBytes = Math.Max(sourceBytes, new DriveInfo(task.InputPath).TotalSize); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
            if (sourceBytes <= 0) sourceBytes = task.SourceFormatId switch
            {
                OpticalDiscFormatIds.AudioCd => 800 * MiB,
                OpticalDiscFormatIds.BluRayDisc => 25_000_000_000,
                _ => 4_700_000_000
            };
        }
        var probe = task.Probe;
        var seconds = Math.Max(0, probe.Duration.TotalSeconds);
        double expected = Math.Max(MiB, sourceBytes);
        if (task.ContentKind == ContentKind.Disc || task.ConversionMode == "lossless" && task.Kind != MediaKind.Image)
            return ClampBytes(expected * 1.1 + MiB);
        if (FormatCatalog.IsFrameSequence(task.TargetFormat))
        {
            var fps = task.ConversionMode == "fast" ? 8 : task.ConversionMode == "quality" ? 30 : 15;
            expected = Math.Max(expected, PixelBytes(probe) * Math.Max(1, Math.Ceiling(seconds * fps)));
        }
        else if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            var trackIndex = task.Kind == MediaKind.Video ? task.SelectedAudioTrack : 0;
            var channels = trackIndex >= 0 && trackIndex < probe.AudioTracks.Count
                ? Math.Max(1, probe.AudioTracks[trackIndex].Channels) : 2;
            var target = task.TargetFormat.ToUpperInvariant();
            var bitsPerSecond = target switch
            {
                "WAV" or "AIFF" or "FLAC" or "ALAC" => channels * 48000d * 16,
                "EAC3" => task.ConversionMode == "quality" ? 640000 : 384000,
                "OPUS" => task.ConversionMode == "quality" ? 160000 : 128000,
                _ => task.ConversionMode == "fast" ? 128000 : task.ConversionMode == "quality" ? 320000 : 192000
            };
            expected = Math.Max(expected, seconds * bitsPerSecond / 8);
        }
        else if (task.Kind == MediaKind.Image || FormatCatalog.IsImageFormat(task.TargetFormat))
        {
            var frames = probe.IsAnimated ? Math.Max(1, Math.Ceiling(seconds * 25)) : Math.Max(1, probe.PageCount);
            expected = Math.Max(expected, PixelBytes(probe) * frames);
        }
        else if (task.Kind == MediaKind.Video)
        {
            var bitsPerPixel = task.ConversionMode == "fast" ? .06 : task.ConversionMode == "quality" ? .15 : .1;
            var rate = (double)Math.Max(0, probe.Width) * Math.Max(0, probe.Height) * 30 * bitsPerPixel + 320000;
            expected = Math.Max(expected * (task.ConversionMode == "quality" ? 1.5 : 1.0), seconds * rate / 8);
        }
        else expected *= 3; // Document intermediates and embedded media may grow beyond the source.
        return ClampBytes(expected * 1.25 + MiB);
    }

    private static double PixelBytes(MediaProbeResult probe) =>
        (double)Math.Max(0, probe.Width) * Math.Max(0, probe.Height) *
        (ImagePreservationPolicy.PixelBitDepth(probe.PixelFormat) > 8 ? 8 : 4);
    private static long ClampBytes(double value) => !double.IsFinite(value) || value >= long.MaxValue
        ? long.MaxValue : (long)Math.Ceiling(Math.Max(0, value));
    private static long SaturatingAdd(long left, long right) => right > long.MaxValue - left ? long.MaxValue : left + right;

    private static long ReadWrittenBytes(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path)) return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Aggregate(0L, (sum, file) => SaturatingAdd(sum, new FileInfo(file).Length));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return 0;
    }

    private static DiskSpaceInfo? ReadDiskSpace(string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(directory);
            if (OperatingSystem.IsWindows())
            {
                // GetDiskFreeSpaceEx also handles UNC shares and reports the caller's quota.
                if (!GetDiskFreeSpaceEx(fullPath, out var available, out _, out _)) return null;
                var volumePath = new StringBuilder(1024);
                var volume = GetVolumePathName(fullPath, volumePath, (uint)volumePath.Capacity)
                    ? volumePath.ToString() : Path.GetPathRoot(fullPath) ?? fullPath;
                return new DiskSpaceInfo(volume, (long)Math.Min(available, (ulong)long.MaxValue));
            }
            var root = Path.GetPathRoot(fullPath);
            if (root is null) return null;
            return new DiskSpaceInfo(root, new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePath, uint bufferLength);

    private sealed record Reservation(string Volume, long EstimatedBytes, string? TemporaryPath);
    private sealed class BudgetLease(Action? release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

internal sealed record DiskSpaceInfo(string Volume, long AvailableBytes);
