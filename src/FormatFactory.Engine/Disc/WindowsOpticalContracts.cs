namespace FormatFactoryWinUI.Disc;

public sealed record CdTrackInfo(
    byte Number,
    int StartLba,
    int EndLba,
    bool IsData)
{
    public int SectorCount => Math.Max(0, EndLba - StartLba);

    public TimeSpan Duration => TimeSpan.FromSeconds(SectorCount / 75d);
}

public sealed record CdTableOfContents(
    byte FirstTrack,
    byte LastTrack,
    int LeadOutLba,
    IReadOnlyList<CdTrackInfo> Tracks)
{
    public IReadOnlyList<CdTrackInfo> AudioTracks => Tracks
        .Where(track => !track.IsData)
        .ToList()
        .AsReadOnly();
}

public sealed record OpticalOperationProgress(
    string Operation,
    double? Percentage,
    long CompletedBytes,
    long? TotalBytes,
    string Message);

[Flags]
public enum IsoFileSystems
{
    None = 0,
    Iso9660 = 1,
    Joliet = 2,
    Udf = 4
}

public sealed record IsoImageBuildRequest(
    IReadOnlyList<string> SourcePaths,
    string OutputPath,
    string VolumeLabel = "FORMAT_FACTORY",
    IsoFileSystems FileSystems = IsoFileSystems.Iso9660 | IsoFileSystems.Joliet | IsoFileSystems.Udf,
    bool IncludeBaseDirectories = true);

public sealed record IsoImageBuildResult(
    string OutputPath,
    long Length,
    IsoFileSystems FileSystems);

public sealed record OpticalRecorderInfo(
    string Id,
    string? VendorId,
    string? ProductId,
    string? ProductRevision,
    IReadOnlyList<string> VolumePaths,
    bool CanLoadMedia);

public sealed record OpticalBurnRequest(
    string RecorderId,
    string ImagePath,
    bool CloseMedia = true,
    bool VerifyAfterWrite = false,
    string? DevicePath = null);

public sealed record OpticalEraseRequest(
    string RecorderId,
    bool FullErase = false);

public sealed record OpticalVerificationResult(
    bool IsMatch,
    long ComparedBytes,
    long? MismatchOffset,
    string Message);

public interface IOpticalVolumeLock : IDisposable
{
    string DevicePath { get; }

    bool IsLocked { get; }
}

public interface IWindowsOpticalDeviceController
{
    Task<IOpticalVolumeLock> LockVolumeAsync(
        string rootOrDevicePath,
        bool dismount = false,
        CancellationToken cancellationToken = default);

    Task EjectAsync(
        string rootOrDevicePath,
        CancellationToken cancellationToken = default);
}

public interface IWindowsOpticalDiscReader
{
    Task<CdTableOfContents> ReadTableOfContentsAsync(
        string rootOrDevicePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ExtractAudioCdToWaveAsync(
        string rootOrDevicePath,
        string outputDirectory,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> ReadDataDiscToIsoAsync(
        string rootOrDevicePath,
        string outputPath,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IWindowsIsoImageBuilder
{
    Task<IsoImageBuildResult> CreateAsync(
        IsoImageBuildRequest request,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IWindowsOpticalMediaWriter
{
    Task<IReadOnlyList<OpticalRecorderInfo>> GetRecordersAsync(
        CancellationToken cancellationToken = default);

    Task<OpticalVerificationResult?> BurnIsoAsync(
        OpticalBurnRequest request,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task EraseAsync(
        OpticalEraseRequest request,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<OpticalVerificationResult> VerifyIsoAsync(
        string imagePath,
        string rootOrDevicePath,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IWindowsIsoMountService : IDisposable
{
    Task<MountedIso> MountAsync(
        string imagePath,
        CancellationToken cancellationToken = default);

    Task UnmountAsync(
        string imagePath,
        CancellationToken cancellationToken = default);
}
