using FormatFactoryWinUI.Models;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

/// <summary>
/// Enumerates Windows CD-ROM drives and classifies the mounted media without
/// invoking an external conversion engine.
/// </summary>
public sealed partial class OpticalDiscDiscovery
{
    public IReadOnlyList<OpticalDiscInfo> Discover(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var drives = DriveInfo.GetDrives()
            .Where(drive => TryGetDriveType(drive) == DriveType.CDRom)
            .ToList();
        var result = new List<OpticalDiscInfo>(drives.Count);

        for (var index = 0; index < drives.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(Probe(drives[index], index));
        }

        return result;
    }

    public Task<IReadOnlyList<OpticalDiscInfo>> DiscoverAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Discover(cancellationToken));

    /// <summary>
    /// Rediscovers a source by its stable drive-root key. The returned snapshot
    /// contains the current media classification. MakeMKV indices are resolved
    /// separately from MakeMKV's own DRV output.
    /// </summary>
    public OpticalDiscInfo? FindByRootPath(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var normalized = OpticalDiscPath.NormalizeRootPath(rootPath);
        return Discover(cancellationToken).FirstOrDefault(
            disc => string.Equals(disc.RootPath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public Task<OpticalDiscInfo?> FindByRootPathAsync(
        string rootPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(FindByRootPath(rootPath, cancellationToken));

    /// <summary>
    /// Pure media classifier used by discovery and unit tests. A ready optical
    /// drive with no mountable file system is treated as an audio CD; an empty
    /// or otherwise not-ready drive remains unknown.
    /// </summary>
    public static OpticalDiscMediaKind Classify(
        bool isReady,
        bool isFileSystemAccessible,
        IEnumerable<string>? rootDirectoryNames)
    {
        if (!isReady)
        {
            return OpticalDiscMediaKind.Unknown;
        }

        if (!isFileSystemAccessible)
        {
            return OpticalDiscMediaKind.AudioCd;
        }

        var names = (rootDirectoryNames ?? [])
            .Select(GetLeafName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Contains("BDMV"))
        {
            return OpticalDiscMediaKind.BluRay;
        }

        if (names.Contains("VIDEO_TS"))
        {
            return OpticalDiscMediaKind.DvdVideo;
        }

        return OpticalDiscMediaKind.DataDisc;
    }

    private static OpticalDiscInfo Probe(DriveInfo drive, int index)
    {
        var rootPath = OpticalDiscPath.NormalizeRootPath(drive.Name);
        var isReady = TryGetIsReady(drive) || TryCheckMediaPresent(rootPath);
        var hasAudioTracks = isReady && TryHasAudioTracks(rootPath);
        var isFileSystemAccessible = false;
        IReadOnlyList<string> rootDirectoryNames = [];
        string? volumeLabel = null;

        if (isReady)
        {
            try
            {
                rootDirectoryNames = Directory.EnumerateDirectories(rootPath)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToList();
                isFileSystemAccessible = true;
            }
            catch (IOException)
            {
                // Audio CDs do not expose a mountable file system.
            }
            catch (UnauthorizedAccessException)
            {
                // Treat an unreadable mounted volume conservatively as non-filesystem media.
            }

            try
            {
                volumeLabel = drive.VolumeLabel;
            }
            catch (IOException)
            {
                // A volume label is optional metadata.
            }
            catch (UnauthorizedAccessException)
            {
                // A volume label is optional metadata.
            }
        }

        var mediaKind = hasAudioTracks
            ? OpticalDiscMediaKind.AudioCd
            : Classify(isReady, isFileSystemAccessible, rootDirectoryNames);
        return new OpticalDiscInfo(
            rootPath,
            OpticalDiscPath.ToDevicePath(rootPath),
            volumeLabel,
            mediaKind,
            isReady,
            isFileSystemAccessible,
            index);
    }

    private static DriveType? TryGetDriveType(DriveInfo drive)
    {
        try
        {
            return drive.DriveType;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryGetIsReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryCheckMediaPresent(string rootPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var handle = NativeMethods.CreateFile(
                OpticalDiscPath.ToDevicePath(rootPath),
                0,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                0,
                IntPtr.Zero);
            return !handle.IsInvalid &&
                   NativeMethods.DeviceIoControl(
                       handle,
                       NativeMethods.IoctlStorageCheckVerify2,
                       IntPtr.Zero,
                       0,
                       IntPtr.Zero,
                       0,
                       out _,
                       IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHasAudioTracks(string rootPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr tableOfContents = IntPtr.Zero;
        try
        {
            using var handle = NativeMethods.CreateFile(
                OpticalDiscPath.ToDevicePath(rootPath),
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            const int tableSize = 804;
            tableOfContents = Marshal.AllocHGlobal(tableSize);
            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.IoctlCdromReadToc,
                    IntPtr.Zero,
                    0,
                    tableOfContents,
                    tableSize,
                    out var bytesReturned,
                    IntPtr.Zero) ||
                bytesReturned < 12)
            {
                return false;
            }

            var firstTrack = Marshal.ReadByte(tableOfContents, 2);
            var lastTrack = Marshal.ReadByte(tableOfContents, 3);
            if (firstTrack == 0 || lastTrack < firstTrack)
            {
                return false;
            }

            var trackCount = Math.Min(lastTrack - firstTrack + 1, (int)((bytesReturned - 4) / 8));
            for (var index = 0; index < trackCount; index++)
            {
                var controlAndAddress = Marshal.ReadByte(tableOfContents, 4 + (index * 8) + 1);
                const int dataTrackFlag = 0x04;
                if ((controlAndAddress & dataTrackFlag) == 0)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (tableOfContents != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(tableOfContents);
            }
        }
    }

    private static string GetLeafName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
    }

    private static partial class NativeMethods
    {
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint GenericRead = 0x80000000;
        internal const uint OpenExisting = 3;
        internal const uint IoctlStorageCheckVerify2 = 0x002D0800;
        internal const uint IoctlCdromReadToc = 0x00024000;

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            IntPtr inputBuffer,
            uint inputBufferSize,
            IntPtr outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}
