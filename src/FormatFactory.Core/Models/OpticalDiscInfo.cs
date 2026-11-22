using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Models;

/// <summary>
/// Optical-disc kinds are kept separate from <see cref="MediaKind"/> because a
/// disc is a conversion source/container rather than a single payload kind.
/// </summary>
public enum OpticalDiscMediaKind
{
    Unknown = 0,
    BluRay,
    DvdVideo,
    DataDisc,
    AudioCd
}

public static class OpticalDiscFormatIds
{
    public const string BluRayDisc = "bluray-disc";
    public const string DvdDisc = "dvd-disc";
    public const string DataDisc = "data-disc";
    public const string AudioCd = "audio-cd";
    public const string OpticalDisc = "optical-disc";

    public static bool IsPhysicalDisc(string? formatId) => formatId is not null &&
        (formatId.Equals(BluRayDisc, StringComparison.OrdinalIgnoreCase) ||
         formatId.Equals(DvdDisc, StringComparison.OrdinalIgnoreCase) ||
         formatId.Equals(DataDisc, StringComparison.OrdinalIgnoreCase) ||
         formatId.Equals(AudioCd, StringComparison.OrdinalIgnoreCase) ||
         formatId.Equals(OpticalDisc, StringComparison.OrdinalIgnoreCase));

    public static OpticalDiscMediaKind ToMediaKind(string formatId) =>
        formatId.ToLowerInvariant() switch
        {
            BluRayDisc => OpticalDiscMediaKind.BluRay,
            DvdDisc => OpticalDiscMediaKind.DvdVideo,
            DataDisc => OpticalDiscMediaKind.DataDisc,
            AudioCd => OpticalDiscMediaKind.AudioCd,
            _ => OpticalDiscMediaKind.Unknown
        };

    public static string FromMediaKind(OpticalDiscMediaKind kind) => kind switch
    {
        OpticalDiscMediaKind.BluRay => BluRayDisc,
        OpticalDiscMediaKind.DvdVideo => DvdDisc,
        OpticalDiscMediaKind.DataDisc => DataDisc,
        OpticalDiscMediaKind.AudioCd => AudioCd,
        OpticalDiscMediaKind.Unknown => OpticalDisc,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

/// <summary>
/// A stable snapshot of one Windows optical drive. <see cref="RootPath"/> is
/// the durable lookup key used to rediscover a queued source. DriveOrdinal is
/// only the current DriveInfo enumeration order and is never used as a MakeMKV
/// disc index.
/// </summary>
public sealed record OpticalDiscInfo
{
    public OpticalDiscInfo(
        string rootPath,
        string devicePath,
        string? volumeLabel,
        OpticalDiscMediaKind mediaKind,
        bool isReady,
        bool isFileSystemAccessible,
        int driveOrdinal)
    {
        if (driveOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(driveOrdinal));
        }

        RootPath = OpticalDiscPath.NormalizeRootPath(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        DevicePath = devicePath.Trim();
        VolumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? null : volumeLabel.Trim();
        MediaKind = mediaKind;
        IsReady = isReady;
        IsFileSystemAccessible = isFileSystemAccessible;
        DriveOrdinal = driveOrdinal;
    }

    public string RootPath { get; }

    public string DevicePath { get; }

    public string? VolumeLabel { get; }

    public OpticalDiscMediaKind MediaKind { get; }

    public bool IsReady { get; }

    public bool IsFileSystemAccessible { get; }

    public int DriveOrdinal { get; }

    /// <summary>
    /// Compatibility alias for persisted/display metadata. This is the
    /// DriveInfo enumeration ordinal, not a MakeMKV disc index.
    /// </summary>
    public int DriveIndex => DriveOrdinal;

    public string SourceFormatId => OpticalDiscFormatIds.FromMediaKind(MediaKind);

    public string DisplayName
    {
        get
        {
            var drive = OpticalDiscPath.ToDriveSpecifier(RootPath);
            if (VolumeLabel is not null)
            {
                return $"{VolumeLabel} ({drive})";
            }

            var kind = MediaKind switch
            {
                OpticalDiscMediaKind.BluRay => "Blu-ray",
                OpticalDiscMediaKind.DvdVideo => "DVD",
                OpticalDiscMediaKind.DataDisc => LocalizationService.T("CoreOpticalDiscDataName"),
                OpticalDiscMediaKind.AudioCd => LocalizationService.T("CoreOpticalDiscAudioCdName"),
                _ => LocalizationService.T("CoreOpticalDriveName")
            };
            return $"{kind} ({drive})";
        }
    }
}

public static class OpticalDiscPath
{
    public static string NormalizeRootPath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var value = rootPath.Trim().Trim('"').Replace('/', '\\');
        if (value.Length < 2 || !char.IsAsciiLetter(value[0]) || value[1] != ':')
        {
            throw new ArgumentException(LocalizationService.T("CoreOpticalDriveLetterRequired"), nameof(rootPath));
        }

        if (value.Length > 2 && value[2..].Any(character => character != '\\'))
        {
            throw new ArgumentException(LocalizationService.T("CoreOpticalDriveRootRequired"), nameof(rootPath));
        }

        return $"{char.ToUpperInvariant(value[0])}:\\";
    }

    public static string ToDriveSpecifier(string rootPath) => NormalizeRootPath(rootPath)[..2];

    public static string ToDevicePath(string rootPath) => $@"\\.\{ToDriveSpecifier(rootPath)}";
}
