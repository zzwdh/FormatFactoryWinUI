using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;
using Microsoft.Win32.SafeHandles;

namespace FormatFactoryWinUI.Disc;

public sealed class WindowsOpticalDiscReader : IWindowsOpticalDiscReader
{
    private const int TocBufferLength = 804;
    private const int AudioSectorsPerRead = 27;
    private const int DataBufferLength = 1024 * 1024;

    public Task<CdTableOfContents> ReadTableOfContentsAsync(
        string rootOrDevicePath,
        CancellationToken cancellationToken = default)
    {
        var devicePath = OpticalDevicePath.Normalize(rootOrDevicePath);
        return Task.Run(
            () => ReadTableOfContents(devicePath, cancellationToken),
            CancellationToken.None);
    }

    public Task<IReadOnlyList<string>> ExtractAudioCdToWaveAsync(
        string rootOrDevicePath,
        string outputDirectory,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var devicePath = OpticalDevicePath.Normalize(rootOrDevicePath);
        var destination = OpticalOutputStaging.PrepareDirectory(outputDirectory);
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var handle = WindowsOpticalNative.OpenDevice(
                    devicePath,
                    WindowsOpticalNative.GenericRead);
                var table = ReadTableOfContents(handle);
                var tracks = table.AudioTracks;
                if (tracks.Count == 0)
                {
                    throw new InvalidDataException(LocalizationService.T("ConversionUiDiscHasNoCddaTracks"));
                }

                var totalBytes = tracks.Sum(track => checked((long)track.SectorCount * CdAudioWave.SectorBytes));
                long completedBytes = 0;
                var relativeOutputs = new List<string>(tracks.Count);
                foreach (var track in tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileName = $"track{track.Number:D2}.wav";
                    var stagedPath = Path.Combine(destination.StagingPath, fileName);
                    WriteAudioTrack(
                        handle,
                        track,
                        stagedPath,
                        totalBytes,
                        ref completedBytes,
                        progress,
                        cancellationToken);
                    relativeOutputs.Add(fileName);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(destination.StagingPath, destination.FinalPath);
                Report(progress, "audio-cd-read", 100, completedBytes, totalBytes, LocalizationService.T("ConversionUiAudioCdExtractionCompleted"));
                return relativeOutputs
                    .Select(file => Path.Combine(destination.FinalPath, file))
                    .ToList()
                    .AsReadOnly();
            }
            finally
            {
                OpticalOutputStaging.TryDeleteDirectory(destination.StagingPath);
            }
        }, CancellationToken.None);
    }

    public Task<string> ReadDataDiscToIsoAsync(
        string rootOrDevicePath,
        string outputPath,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var devicePath = OpticalDevicePath.Normalize(rootOrDevicePath);
        var destination = OpticalOutputStaging.PrepareFile(outputPath, ".iso");
        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var handle = WindowsOpticalNative.OpenDevice(
                    devicePath,
                    WindowsOpticalNative.GenericRead);
                var mediaLength = GetMediaLength(handle);
                if (mediaLength <= 0)
                {
                    throw new InvalidDataException(LocalizationService.T("ConversionUiCannotDetermineDiscLength"));
                }

                // Allows reads through the physical end of the mounted file system.
                _ = WindowsOpticalNative.DeviceIoControl(
                    handle,
                    WindowsOpticalNative.FsctlAllowExtendedDasdIo,
                    null,
                    0,
                    null,
                    0,
                    out _,
                    IntPtr.Zero);

                return CopyDataDiscToIso(
                    destination,
                    mediaLength,
                    (buffer, requested, completed) =>
                    {
                        if (!WindowsOpticalNative.ReadFile(
                                handle,
                                buffer,
                                checked((uint)requested),
                                out var bytesRead,
                                IntPtr.Zero))
                        {
                            throw new Win32Exception(
                                Marshal.GetLastWin32Error(),
                                LocalizationService.T("ConversionUiDiscSectorReadFailed", completed));
                        }

                        return checked((int)bytesRead);
                    },
                    progress,
                    cancellationToken);
            }
            finally
            {
                OpticalOutputStaging.TryDeleteFile(destination.StagingPath);
            }
        }, CancellationToken.None);
    }

    internal static string CopyDataDiscToIso(
        (string FinalPath, string StagingPath) destination,
        long mediaLength,
        Func<byte[], int, long, int> readBlock,
        IProgress<OpticalOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            // Windows cannot rename a file held open without FileShare.Delete.
            using (var output = new FileStream(
                destination.StagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                DataBufferLength,
                FileOptions.SequentialScan))
            {
                ProcessResourcePolicy.TrySetFileIoPriority(output, FileIoPriority.Low);
                var buffer = new byte[DataBufferLength];
                long completed = 0;
                Report(progress, "disc-to-iso", 0, 0, mediaLength, LocalizationService.T("ConversionUiReadingDataDisc"));
                while (completed < mediaLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = checked((int)Math.Min(buffer.Length, mediaLength - completed));
                    var bytesRead = readBlock(buffer, requested, completed);
                    if (bytesRead == 0)
                    {
                        throw new EndOfStreamException(LocalizationService.T("ConversionUiDiscEndedEarly", completed));
                    }

                    output.Write(buffer, 0, bytesRead);
                    completed += bytesRead;
                    Report(
                        progress,
                        "disc-to-iso",
                        completed * 100d / mediaLength,
                        completed,
                        mediaLength,
                        LocalizationService.T("ConversionUiReadingDataDisc"));
                }

                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(destination.StagingPath, destination.FinalPath, overwrite: false);
            Report(progress, "disc-to-iso", 100, mediaLength, mediaLength, LocalizationService.T("ConversionUiIsoGenerated"));
            return destination.FinalPath;
        }
        finally
        {
            OpticalOutputStaging.TryDeleteFile(destination.StagingPath);
        }
    }

    private static CdTableOfContents ReadTableOfContents(
        string devicePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = WindowsOpticalNative.OpenDevice(
            devicePath,
            WindowsOpticalNative.GenericRead);
        return ReadTableOfContents(handle);
    }

    private static CdTableOfContents ReadTableOfContents(SafeFileHandle handle)
    {
        var output = new byte[TocBufferLength];
        if (!WindowsOpticalNative.DeviceIoControl(
                handle,
                WindowsOpticalNative.IoctlCdromReadToc,
                null,
                0,
                output,
                checked((uint)output.Length),
                out var bytesReturned,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), LocalizationService.T("ConversionUiCannotReadCdToc"));
        }

        return CdTableOfContentsParser.Parse(output.AsSpan(0, checked((int)bytesReturned)));
    }

    private static void WriteAudioTrack(
        SafeFileHandle handle,
        CdTrackInfo track,
        string outputPath,
        long totalBytes,
        ref long completedBytes,
        IProgress<OpticalOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pcmLength = checked((long)track.SectorCount * CdAudioWave.SectorBytes);
        using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.SequentialScan);
        ProcessResourcePolicy.TrySetFileIoPriority(output, FileIoPriority.Low);
        output.Write(CdAudioWave.CreateHeader(pcmLength));

        var buffer = new byte[AudioSectorsPerRead * CdAudioWave.SectorBytes];
        var currentLba = track.StartLba;
        while (currentLba < track.EndLba)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sectorCount = Math.Min(AudioSectorsPerRead, track.EndLba - currentLba);
            var input = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(0, 8), checked((long)currentLba * 2048));
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8, 4), checked((uint)sectorCount));
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12, 4), 2); // CDDA
            var requestedBytes = checked(sectorCount * CdAudioWave.SectorBytes);
            if (!WindowsOpticalNative.DeviceIoControl(
                    handle,
                    WindowsOpticalNative.IoctlCdromRawRead,
                    input,
                    checked((uint)input.Length),
                    buffer,
                    checked((uint)requestedBytes),
                    out var bytesReturned,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    LocalizationService.T("ConversionUiCdTrackSectorReadFailed", track.Number, currentLba));
            }

            if (bytesReturned != requestedBytes)
            {
                throw new EndOfStreamException(
                    LocalizationService.T("ConversionUiCdTrackSectorIncomplete", track.Number));
            }

            var samples = buffer.AsSpan(0, requestedBytes);
            CdAudioWave.ConvertBigEndianPcmToWaveInPlace(samples);
            output.Write(samples);
            currentLba += sectorCount;
            completedBytes += requestedBytes;
            Report(
                progress,
                "audio-cd-read",
                totalBytes == 0 ? 100 : completedBytes * 100d / totalBytes,
                completedBytes,
                totalBytes,
                LocalizationService.T("ConversionUiExtractingTrack", track.Number));
        }

        output.Flush(flushToDisk: true);
    }

    private static long GetMediaLength(SafeFileHandle handle)
    {
        var buffer = new byte[sizeof(long)];
        if (!WindowsOpticalNative.DeviceIoControl(
                handle,
                WindowsOpticalNative.IoctlDiskGetLengthInfo,
                null,
                0,
                buffer,
                checked((uint)buffer.Length),
                out var bytesReturned,
                IntPtr.Zero) ||
            bytesReturned < sizeof(long))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), LocalizationService.T("ConversionUiCannotReadDiscCapacity"));
        }

        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    internal static void Report(
        IProgress<OpticalOperationProgress>? progress,
        string operation,
        double? percentage,
        long completed,
        long? total,
        string message)
    {
        try
        {
            progress?.Report(new OpticalOperationProgress(
                operation,
                percentage is null ? null : Math.Clamp(percentage.Value, 0, 100),
                completed,
                total,
                message));
        }
        catch
        {
            // Progress observers do not control the device operation.
        }
    }
}

internal static class OpticalOutputStaging
{
    public static (string FinalPath, string StagingPath) PrepareFile(
        string outputPath,
        string requiredExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var finalPath = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(finalPath).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiOutputMustUseExtension", requiredExtension), nameof(outputPath));
        }

        if (File.Exists(finalPath) || Directory.Exists(finalPath))
        {
            throw new IOException(LocalizationService.T("ConversionUiOutputPathAlreadyExists", finalPath));
        }

        var parent = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiOutputFileNeedsParent"), nameof(outputPath));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(
            parent,
            $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.partial{requiredExtension}");
        return (finalPath, staging);
    }

    public static (string FinalPath, string StagingPath) PrepareDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var finalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (File.Exists(finalPath) || Directory.Exists(finalPath))
        {
            throw new IOException(LocalizationService.T("ConversionUiOutputPathAlreadyExists", finalPath));
        }

        var parent = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiOutputDirectoryNeedsParent"), nameof(outputDirectory));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(staging);
        return (finalPath, staging);
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The original operation error remains more useful.
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The original operation error remains more useful.
        }
    }
}
