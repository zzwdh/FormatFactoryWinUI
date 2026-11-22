using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Disc;

public sealed class WindowsOpticalMediaWriter : IWindowsOpticalMediaWriter
{
    private const int VerifyBufferLength = 1024 * 1024;
    private readonly Func<string, object> _createComObject;
    private readonly Action<string>? _writeDiagnostic;

    public WindowsOpticalMediaWriter() : this(CreateComObject) { }

    internal WindowsOpticalMediaWriter(Func<string, object> createComObject, Action<string>? writeDiagnostic = null)
    {
        _createComObject = createComObject ?? throw new ArgumentNullException(nameof(createComObject));
        _writeDiagnostic = writeDiagnostic;
    }

    public Task<IReadOnlyList<OpticalRecorderInfo>> GetRecordersAsync(
        CancellationToken cancellationToken = default) =>
        RunComAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic? master = null;
            try
            {
                master = _createComObject("IMAPI2.MsftDiscMaster2");
                if (!(bool)master.IsSupportedEnvironment)
                {
                    return (IReadOnlyList<OpticalRecorderInfo>)Array.Empty<OpticalRecorderInfo>();
                }

                var result = new List<OpticalRecorderInfo>();
                foreach (var recorderIdValue in (IEnumerable)master)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var recorderId = Convert.ToString(recorderIdValue) ?? string.Empty;
                    if (recorderId.Length == 0)
                    {
                        continue;
                    }

                    dynamic? recorder = null;
                    try
                    {
                        recorder = CreateRecorder(recorderId);
                        result.Add(new OpticalRecorderInfo(
                            recorderId,
                            Convert.ToString(recorder.VendorId),
                            Convert.ToString(recorder.ProductId),
                            Convert.ToString(recorder.ProductRevision),
                            OpticalRecorderCatalog.NormalizeVolumePaths(recorder.VolumePathNames),
                            Convert.ToBoolean(recorder.DeviceCanLoadMedia)));
                    }
                    finally
                    {
                        WindowsComStreams.Release(recorder);
                    }
                }

                return (IReadOnlyList<OpticalRecorderInfo>)result.AsReadOnly();
            }
            finally
            {
                WindowsComStreams.Release(master);
            }
        });

    public async Task<OpticalVerificationResult?> BurnIsoAsync(
        OpticalBurnRequest request,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RecorderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ImagePath);
        var imagePath = Path.GetFullPath(request.ImagePath);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException(LocalizationService.T("ConversionUiIsoToBurnMissing"), imagePath);
        }

        await RunComAsync(() =>
        {
            var diagnostic = new OpticalDiagnosticOperation(OpticalDiagnosticAction.Burn, _writeDiagnostic);
            dynamic? recorder = null;
            dynamic? format = null;
            System.Runtime.InteropServices.ComTypes.IStream? imageStream = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                diagnostic.Phase = OpticalDiagnosticPhase.DeviceInitialize;
                recorder = CreateRecorder(request.RecorderId);
                format = _createComObject("IMAPI2.MsftDiscFormat2Data");
                diagnostic.Phase = OpticalDiagnosticPhase.CapabilityCheck;
                var recorderSupported = (bool)format.IsRecorderSupported(recorder);
                diagnostic.RecorderSupported = recorderSupported;
                if (!recorderSupported)
                {
                    throw new InvalidOperationException(LocalizationService.T("ConversionUiDeviceNoImapi2Burn"));
                }

                diagnostic.Phase = OpticalDiagnosticPhase.Configure;
                format.Recorder = recorder;
                format.ClientName = "FluentFormatConverter";
                var closeMedia = request.CloseMedia;
                format.ForceMediaToBeClosed = closeMedia;
                diagnostic.CloseMedia = closeMedia;

                diagnostic.Phase = OpticalDiagnosticPhase.MediaCheck;
                var mediaSupported = (bool)format.IsCurrentMediaSupported(recorder);
                diagnostic.MediaSupported = mediaSupported;
                if (!mediaSupported)
                {
                    throw new InvalidOperationException(LocalizationService.T("ConversionUiMediaNoDataBurn"));
                }

                diagnostic.Phase = OpticalDiagnosticPhase.ImageOpen;
                imageStream = WindowsComStreams.OpenRead(imagePath);
                var length = new FileInfo(imagePath).Length;
                WindowsOpticalDiscReader.Report(
                    progress,
                    "imapi2-burn",
                    0,
                    0,
                    length,
                    LocalizationService.T("ConversionUiBurningIso"));
                using var registration = cancellationToken.Register(() => TryCancelWrite(format, diagnostic));
                diagnostic.Phase = OpticalDiagnosticPhase.Write;
                format.Write(imageStream);
                diagnostic.Phase = OpticalDiagnosticPhase.Result;
                cancellationToken.ThrowIfCancellationRequested();
                WindowsOpticalDiscReader.Report(
                    progress,
                    "imapi2-burn",
                    100,
                    length,
                    length,
                    LocalizationService.T("ConversionUiIsoBurnCompleted"));
            }
            catch (OperationCanceledException error) { diagnostic.Cancelled(error); throw; }
            catch (Exception error) { diagnostic.Fail(error); throw; }
            finally
            {
                WindowsComStreams.Release(imageStream);
                WindowsComStreams.Release(format);
                WindowsComStreams.Release(recorder);
            }
        });

        if (!request.VerifyAfterWrite)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.DevicePath))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiVerificationNeedsDevicePath"), nameof(request));
        }

        return await VerifyIsoAsync(
            imagePath,
            request.DevicePath,
            progress,
            cancellationToken);
    }

    public Task EraseAsync(
        OpticalEraseRequest request,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RecorderId);
        return RunComAsync(() =>
        {
            var diagnostic = new OpticalDiagnosticOperation(OpticalDiagnosticAction.Erase, _writeDiagnostic);
            dynamic? recorder = null;
            dynamic? format = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                diagnostic.Phase = OpticalDiagnosticPhase.DeviceInitialize;
                recorder = CreateRecorder(request.RecorderId);
                format = _createComObject("IMAPI2.MsftDiscFormat2Erase");
                diagnostic.Phase = OpticalDiagnosticPhase.CapabilityCheck;
                var recorderSupported = (bool)format.IsRecorderSupported(recorder);
                diagnostic.RecorderSupported = recorderSupported;
                if (!recorderSupported)
                {
                    throw new InvalidOperationException(LocalizationService.T("ConversionUiDeviceNoImapi2Erase"));
                }

                diagnostic.Phase = OpticalDiagnosticPhase.Configure;
                format.Recorder = recorder;
                format.ClientName = "FluentFormatConverter";
                var fullErase = request.FullErase;
                format.FullErase = fullErase;
                diagnostic.FullErase = fullErase;

                WindowsOpticalDiscReader.Report(
                    progress,
                    "imapi2-erase",
                    0,
                    0,
                    null,
                    fullErase
                        ? LocalizationService.T("ConversionUiFullErasingDisc")
                        : LocalizationService.T("ConversionUiQuickErasingDisc"));
                diagnostic.Phase = OpticalDiagnosticPhase.Erase;
                format.EraseMedia();
                diagnostic.Phase = OpticalDiagnosticPhase.Result;
                cancellationToken.ThrowIfCancellationRequested();
                WindowsOpticalDiscReader.Report(
                    progress,
                    "imapi2-erase",
                    100,
                    0,
                    null,
                    LocalizationService.T("ConversionUiDiscEraseCompleted"));
            }
            catch (OperationCanceledException error) { diagnostic.Cancelled(error); throw; }
            catch (Exception error) { diagnostic.Fail(error); throw; }
            finally
            {
                WindowsComStreams.Release(format);
                WindowsComStreams.Release(recorder);
            }
        });
    }

    public Task<OpticalVerificationResult> VerifyIsoAsync(
        string imagePath,
        string rootOrDevicePath,
        IProgress<OpticalOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullImagePath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullImagePath))
        {
            throw new FileNotFoundException(LocalizationService.T("ConversionUiIsoToVerifyMissing"), fullImagePath);
        }

        var devicePath = OpticalDevicePath.Normalize(rootOrDevicePath);
        return Task.Run(() => VerifyCore(
            fullImagePath,
            devicePath,
            progress,
            cancellationToken), CancellationToken.None);
    }

    private static OpticalVerificationResult VerifyCore(
        string imagePath,
        string devicePath,
        IProgress<OpticalOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var expected = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            VerifyBufferLength,
            FileOptions.SequentialScan);
        using var device = WindowsOpticalNative.OpenDevice(
            devicePath,
            WindowsOpticalNative.GenericRead);
        _ = WindowsOpticalNative.DeviceIoControl(
            device,
            WindowsOpticalNative.FsctlAllowExtendedDasdIo,
            null,
            0,
            null,
            0,
            out _,
            IntPtr.Zero);

        var expectedBuffer = new byte[VerifyBufferLength];
        var actualBuffer = new byte[VerifyBufferLength];
        long compared = 0;
        while (compared < expected.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = checked((int)Math.Min(expectedBuffer.Length, expected.Length - compared));
            var expectedRead = expected.Read(expectedBuffer, 0, requested);
            if (expectedRead == 0)
            {
                break;
            }

            var actualRead = ReadDeviceBlock(device, actualBuffer, expectedRead, compared);
            var mismatch = OpticalByteComparer.FindFirstMismatch(
                expectedBuffer.AsSpan(0, expectedRead),
                actualBuffer.AsSpan(0, actualRead));
            if (mismatch is not null)
            {
                var mismatchOffset = compared + mismatch.Value;
                return new OpticalVerificationResult(
                    false,
                    mismatchOffset,
                    mismatchOffset,
                    LocalizationService.T("ConversionUiBurnMismatchAtOffset", mismatchOffset));
            }

            compared += expectedRead;
            WindowsOpticalDiscReader.Report(
                progress,
                "disc-verify",
                compared * 100d / expected.Length,
                compared,
                expected.Length,
                LocalizationService.T("ConversionUiVerifyingBurnedContent"));
        }

        WindowsOpticalDiscReader.Report(
            progress,
            "disc-verify",
            100,
            compared,
            expected.Length,
            LocalizationService.T("ConversionUiBurnVerificationPassed"));
        return new OpticalVerificationResult(true, compared, null, LocalizationService.T("ConversionUiBurnMatchesIso"));
    }

    private static int ReadDeviceBlock(
        Microsoft.Win32.SafeHandles.SafeFileHandle device,
        byte[] buffer,
        int count,
        long offset)
    {
        var total = 0;
        while (total < count)
        {
            // Optical reads are sector-aligned in normal ISO verification. A short
            // successful read is continued; a zero-byte read is a mismatch.
            var remaining = checked((uint)(count - total));
            var target = total == 0 ? buffer : new byte[remaining];
            if (!WindowsOpticalNative.ReadFile(
                    device,
                    target,
                    remaining,
                    out var bytesRead,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    LocalizationService.T("ConversionUiVerificationDiscReadFailed", offset + total));
            }

            if (bytesRead == 0)
            {
                return total;
            }

            if (total != 0)
            {
                target.AsSpan(0, checked((int)bytesRead)).CopyTo(buffer.AsSpan(total));
            }

            total += checked((int)bytesRead);
        }

        return total;
    }

    private dynamic CreateRecorder(string recorderId)
    {
        dynamic recorder = _createComObject("IMAPI2.MsftDiscRecorder2");
        recorder.InitializeDiscRecorder(recorderId);
        return recorder;
    }

    private static dynamic CreateComObject(string programmaticId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(LocalizationService.T("ConversionUiImapi2WindowsOnly"));
        }

        var type = Type.GetTypeFromProgID(programmaticId, throwOnError: true)
            ?? throw new InvalidOperationException(LocalizationService.T("ConversionUiWindowsComNotRegistered", programmaticId));
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException(LocalizationService.T("ConversionUiCannotCreateWindowsCom", programmaticId));
    }

    private static void TryCancelWrite(dynamic format, OpticalDiagnosticOperation diagnostic)
    {
        try
        {
            format.CancelWrite();
        }
        catch (Exception error)
        {
            // The IMAPI implementation may be between cancellable phases.
            diagnostic.CancelFailure(error);
        }
    }

    private static Task<T> RunComAsync<T>(Func<T> action) =>
        Task.Run(action, CancellationToken.None);

    private static Task RunComAsync(Action action) =>
        Task.Run(action, CancellationToken.None);
}

internal static class OpticalRecorderCatalog
{
    public static IReadOnlyList<string> NormalizeVolumePaths(object? value)
    {
        if (value is not IEnumerable enumerable)
        {
            return [];
        }

        return enumerable
            .Cast<object?>()
            .Select(Convert.ToString)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }
}

internal static class OpticalByteComparer
{
    public static int? FindFirstMismatch(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var commonLength = Math.Min(expected.Length, actual.Length);
        for (var index = 0; index < commonLength; index++)
        {
            if (expected[index] != actual[index])
            {
                return index;
            }
        }

        return expected.Length == actual.Length ? null : commonLength;
    }
}
