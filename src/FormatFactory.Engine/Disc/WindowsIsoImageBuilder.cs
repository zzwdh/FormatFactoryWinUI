using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Disc;

public sealed class WindowsIsoImageBuilder : IWindowsIsoImageBuilder
{
    private const int CopyBufferLength = 1024 * 1024;

    public Task<IsoImageBuildResult> CreateAsync(
        IsoImageBuildRequest request,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostic = new IsoBuildDiagnosticOperation();
        var validated = diagnostic.Observe(IsoBuildPhase.Validate, () => IsoImageBuildLayout.Validate(request));
        diagnostic.SourceCount = validated.SourcePaths.Count;
        diagnostic.FileSystems = validated.FileSystems;
        var destination = diagnostic.Observe(IsoBuildPhase.PrepareOutput,
            () => OpticalOutputStaging.PrepareFile(validated.OutputPath, ".iso"));
        return Task.Run(() =>
        {
            diagnostic.Phase = IsoBuildPhase.PlatformCheck;
            if (!OperatingSystem.IsWindows())
            {
                var error = new PlatformNotSupportedException(LocalizationService.T("ConversionUiImapi2FsWindowsOnly"));
                diagnostic.Fail(error);
                throw error;
            }

            dynamic? fileSystemImage = null;
            dynamic? root = null;
            dynamic? resultImage = null;
            object? imageStreamObject = null;
            var sourceStreams = new List<IStream>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                diagnostic.Phase = IsoBuildPhase.CreateFileSystem;
                var type = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage", throwOnError: true)
                    ?? throw new InvalidOperationException(LocalizationService.T("ConversionUiImapi2FsNotRegistered"));
                fileSystemImage = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException(LocalizationService.T("ConversionUiCannotCreateImapi2FsImage"));
                diagnostic.Phase = IsoBuildPhase.ConfigureFileSystem;
                fileSystemImage.FileSystemsToCreate = (int)validated.FileSystems;
                fileSystemImage.VolumeName = validated.VolumeLabel;
                root = fileSystemImage.Root;

                WindowsOpticalDiscReader.Report(
                    progress,
                    "imapi2fs-build",
                    0,
                    0,
                    null,
                    LocalizationService.T("ConversionUiOrganizingIsoFileSystem"));
                foreach (var sourcePath in validated.SourcePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Directory.Exists(sourcePath))
                    {
                        diagnostic.Phase = IsoBuildPhase.AddTree;
                        root.AddTree(sourcePath, validated.IncludeBaseDirectories);
                        diagnostic.CompletedSources++;
                        continue;
                    }

                    diagnostic.Phase = IsoBuildPhase.OpenSource;
                    var sourceStream = WindowsComStreams.OpenRead(sourcePath);
                    sourceStreams.Add(sourceStream);
                    diagnostic.Phase = IsoBuildPhase.AddFile;
                    root.AddFile(Path.GetFileName(sourcePath), sourceStream);
                    diagnostic.CompletedSources++;
                }

                cancellationToken.ThrowIfCancellationRequested();
                diagnostic.Phase = IsoBuildPhase.CreateResultImage;
                resultImage = fileSystemImage.CreateResultImage();
                diagnostic.Phase = IsoBuildPhase.OpenResultStream;
                imageStreamObject = resultImage.ImageStream;
                var imageStream = (IStream)imageStreamObject;
                var blockSize = checked((int)resultImage.BlockSize);
                if (blockSize <= 0)
                {
                    throw new InvalidDataException(LocalizationService.T("ConversionUiImapi2FsInvalidBlockSize"));
                }

                var totalBytes = checked((long)resultImage.TotalBlocks * blockSize);
                diagnostic.TotalBytes = totalBytes;
                diagnostic.BlockSize = blockSize;
                CopyImageStream(
                    imageStream,
                    destination.StagingPath,
                    totalBytes,
                    progress,
                    cancellationToken,
                    diagnostic);
                cancellationToken.ThrowIfCancellationRequested();
                diagnostic.Phase = IsoBuildPhase.Commit;
                File.Move(destination.StagingPath, destination.FinalPath, overwrite: false);
                diagnostic.Phase = IsoBuildPhase.ReadResult;
                var length = new FileInfo(destination.FinalPath).Length;
                WindowsOpticalDiscReader.Report(
                    progress,
                    "imapi2fs-build",
                    100,
                    length,
                    length,
                    LocalizationService.T("ConversionUiIsoFileSystemGenerated"));
                return new IsoImageBuildResult(
                    destination.FinalPath,
                    length,
                    validated.FileSystems);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                diagnostic.Fail(error);
                throw;
            }
            finally
            {
                OpticalOutputStaging.TryDeleteFile(destination.StagingPath);
                foreach (var stream in sourceStreams)
                {
                    WindowsComStreams.Release(stream);
                }

                WindowsComStreams.Release(imageStreamObject);
                WindowsComStreams.Release(resultImage);
                WindowsComStreams.Release(root);
                WindowsComStreams.Release(fileSystemImage);
            }
        }, CancellationToken.None);
    }

    private static void CopyImageStream(
        IStream source,
        string outputPath,
        long totalBytes,
        IProgress<OpticalOperationProgress>? progress,
        CancellationToken cancellationToken,
        IsoBuildDiagnosticOperation diagnostic)
    {
        diagnostic.Phase = IsoBuildPhase.CreateOutputStream;
        using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferLength,
            FileOptions.SequentialScan);
        ProcessResourcePolicy.TrySetFileIoPriority(output, FileIoPriority.Low);
        var buffer = new byte[CopyBufferLength];
        var readPointer = Marshal.AllocCoTaskMem(sizeof(int));
        long completed = 0;
        try
        {
            while (totalBytes <= 0 || completed < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.WriteInt32(readPointer, 0);
                var requested = totalBytes > 0
                    ? checked((int)Math.Min(buffer.Length, totalBytes - completed))
                    : buffer.Length;
                if (requested == 0)
                {
                    break;
                }

                diagnostic.Phase = IsoBuildPhase.ReadImageStream;
                source.Read(buffer, requested, readPointer);
                var read = Marshal.ReadInt32(readPointer);
                if (read <= 0)
                {
                    break;
                }

                diagnostic.Phase = IsoBuildPhase.WriteImageStream;
                output.Write(buffer, 0, read);
                completed += read;
                diagnostic.CompletedBytes = completed;
                WindowsOpticalDiscReader.Report(
                    progress,
                    "imapi2fs-build",
                    totalBytes > 0 ? completed * 100d / totalBytes : null,
                    completed,
                    totalBytes > 0 ? totalBytes : null,
                    LocalizationService.T("ConversionUiWritingIsoImage"));
            }

            if (totalBytes > 0 && completed != totalBytes)
            {
                throw new EndOfStreamException(
                    LocalizationService.T("ConversionUiImapi2FsStreamEndedEarly", totalBytes, completed));
            }

            diagnostic.Phase = IsoBuildPhase.FlushOutput;
            output.Flush(flushToDisk: true);
        }
        finally
        {
            Marshal.FreeCoTaskMem(readPointer);
        }
    }
}

internal static class IsoImageBuildLayout
{
    private static readonly char[] InvalidVolumeLabelCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    public static IsoImageBuildRequest Validate(IsoImageBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.SourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        if (request.SourcePaths.Count == 0)
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiIsoSourceRequired"), nameof(request));
        }

        var sources = request.SourcePaths
            .Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                {
                    throw new FileNotFoundException(LocalizationService.T("ConversionUiIsoSourceMissing"), fullPath);
                }

                return Path.TrimEndingDirectorySeparator(fullPath);
            })
            .ToList();
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiIsoSourcePathsUnique"), nameof(request));
        }

        if (request.FileSystems == IsoFileSystems.None ||
            (request.FileSystems & ~(IsoFileSystems.Iso9660 | IsoFileSystems.Joliet | IsoFileSystems.Udf)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), LocalizationService.T("ConversionUiIsoFileSystemCombinationInvalid"));
        }
        if (request.FileSystems.HasFlag(IsoFileSystems.Joliet) &&
            !request.FileSystems.HasFlag(IsoFileSystems.Iso9660))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiJolietRequiresIso9660"), nameof(request));
        }

        var volumeLabel = string.IsNullOrWhiteSpace(request.VolumeLabel)
            ? "FORMAT_FACTORY"
            : request.VolumeLabel.Trim();
        if (volumeLabel.Length > 32 || volumeLabel.IndexOfAny(InvalidVolumeLabelCharacters) >= 0)
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiVolumeLabelInvalid"), nameof(request));
        }

        return request with
        {
            SourcePaths = sources.AsReadOnly(),
            OutputPath = Path.GetFullPath(request.OutputPath),
            VolumeLabel = volumeLabel
        };
    }
}

internal static class WindowsComStreams
{
    private const uint StgmRead = 0x00000000;
    private const uint StgmShareDenyWrite = 0x00000020;

    public static IStream OpenRead(string path)
    {
        var result = SHCreateStreamOnFileEx(
            Path.GetFullPath(path),
            StgmRead | StgmShareDenyWrite,
            0,
            false,
            null,
            out var stream);
        Marshal.ThrowExceptionForHR(result);
        return stream;
    }

    public static void Release(object? value)
    {
        if (!OperatingSystem.IsWindows() || value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // COM cleanup is best effort after the operation has completed.
        }
    }

    [DllImport("shlwapi.dll", EntryPoint = "SHCreateStreamOnFileEx", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(
        string fileName,
        uint mode,
        uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create,
        IStream? template,
        out IStream stream);
}
