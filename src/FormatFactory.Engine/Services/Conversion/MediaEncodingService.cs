using FormatFactoryWinUI.Models;
using static FormatFactoryWinUI.Services.MediaConversionArtifacts;

namespace FormatFactoryWinUI.Services;

internal delegate Task<FfmpegProcessResult> RunMediaProcess(MediaTask task, string executable,
    IReadOnlyList<string> arguments, string logPath, Action<ConversionProgress> report, CancellationToken cancellationToken);

/// <summary>Executes encoding attempts, hardware fallbacks, size searches and icon frame generation.</summary>
internal sealed class MediaEncodingService(RunMediaProcess runProcess,
    HardwareCapabilitiesService? hardwareCapabilities = null)
{
    private readonly PresetService _presetService = new();
    private readonly HardwareCapabilitiesService _hardwareCapabilities = hardwareCapabilities ?? new();
    private readonly RunMediaProcess _runProcess = runProcess;

    internal string GetPreservationWarning(MediaTask task) => _presetService.GetPreservationWarning(task);

    internal async Task<ConversionResult?> EncodeAsync(MediaTask task, string ffmpegPath, string temporaryPath,
        string logPath, PreparedMediaConversion prepared, Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        if (task.TargetFormat == "ICO" && task.Kind == MediaKind.Image)
        {
            var result = await ConvertToIcoAsync(task, ffmpegPath, temporaryPath, logPath, report, cancellationToken);
            return result.Success ? null : result with { OutputPath = string.Empty };
        }

        if (task.Kind == MediaKind.Image && prepared.Options.TargetSizeBytes.HasValue)
        {
            var sized = await EncodeImageWithSizeLimitAsync(task, ffmpegPath, temporaryPath, logPath,
                prepared.Options, prepared.Settings, report, cancellationToken);
            if (sized.ExitCode != 0 || !HasOutput(temporaryPath, false))
                throw new InvalidDataException(FfmpegErrorCapture.BuildFailureMessage(task, sized.LastErrorLines, sized.FailureDetail));
            return null;
        }

        return await EncodeMediaAttemptsAsync(task, ffmpegPath, temporaryPath, logPath, prepared, report, cancellationToken);
    }

    private async Task<ConversionResult?> EncodeMediaAttemptsAsync(MediaTask task, string ffmpegPath, string temporaryPath,
        string logPath, PreparedMediaConversion prepared, Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        var mediaOptions = prepared.Options;
        var settings = prepared.Settings;
        var isFrameSequence = FormatCatalog.IsFrameSequence(task.TargetFormat);
        var attempts = await BuildAttemptsAsync(task, ffmpegPath, settings, cancellationToken);
        var cpuThreads = PerformanceTuning.ResolveCpuThreads(settings);
        var encodingOptions = MediaSizeBudget.Initial(task, mediaOptions);
        var sizeAttempts = mediaOptions.TargetSizeBytes.HasValue ? MediaSizeBudget.MaximumMediaAttempts : 1;

        FfmpegProcessResult? lastRun = null;
        var attemptNumber = 0;
        for (var sizeAttempt = 1; sizeAttempt <= sizeAttempts; sizeAttempt++)
        {
            foreach (var attempt in attempts)
            {
                var backend = attempt.Backend;
                cancellationToken.ThrowIfCancellationRequested();

                DeleteIfExists(temporaryPath);
                if (isFrameSequence) Directory.CreateDirectory(temporaryPath);
                var backendName = backend switch
                {
                    EncoderBackend.Nvidia => "NVIDIA NVENC",
                    EncoderBackend.Intel => "Intel Quick Sync",
                    EncoderBackend.Amd => "AMD AMF",
                    EncoderBackend.MediaFoundation => "Windows Media Foundation",
                    _ => $"CPU · {cpuThreads} threads"
                };
                var pathName = prepared.CopiedStreams is not null ? LocalizationService.T("RemuxCopying")
                    : attempt.HardwareDecode ? LocalizationService.T("GpuDecode", backendName) : backendName;
                report(new ConversionProgress(0, "—", "—", pathName));
                var arguments = _presetService.BuildArguments(task, temporaryPath, backend, attempt.HardwareDecode,
                    cpuThreads, cancellationToken, encodingOptions);
                attemptNumber++;
                NativeDiagnostics.Log.EncoderAttempt(task.Id.ToString("N"), backend.ToString(),
                    attempt.HardwareDecode, attemptNumber, attemptNumber > 1);
                var progress = mediaOptions.TargetSizeBytes.HasValue
                    ? new Action<ConversionProgress>(value => report(value with
                    { Message = LocalizationService.T("MediaSizeAttempt", sizeAttempt, sizeAttempts) })) : report;
                lastRun = await _runProcess(task, ffmpegPath, arguments, logPath, progress, cancellationToken);
                if (lastRun.ExitCode == 0 && HasOutput(temporaryPath, isFrameSequence)) break;

                if (cancellationToken.IsCancellationRequested)
                    return CancelledResult(logPath);

                if (backend != EncoderBackend.Cpu && !attempts.SkipWhile(item => item != attempt).Skip(1).Any(item => item.Backend == backend))
                    report(new ConversionProgress(0, "—", "—", LocalizationService.T("BackendFallback", backendName)));
            }

            if (lastRun is null || lastRun.ExitCode != 0 || !HasOutput(temporaryPath, isFrameSequence)) break;
            if (mediaOptions.TargetSizeBytes is not { } limit) break;
            var measuredBytes = new FileInfo(temporaryPath).Length;
            RuntimeFailureDiagnostics.WriteTaskObservation(logPath,
                $"Output size attempt: task={task.Id:N} attempt={sizeAttempt} maximum_bytes={limit} actual_bytes={measuredBytes} duration_ticks={task.Probe.Duration.Ticks} video_kbps={RuntimeFailureDiagnostics.Number(encodingOptions.VideoBitrateKbps)} audio_kbps={RuntimeFailureDiagnostics.Number(encodingOptions.AudioBitrateKbps)}\n");
            if (measuredBytes <= limit) break;
            if (sizeAttempt == sizeAttempts) throw MediaSizeBudget.CannotMeet();
            encodingOptions = MediaSizeBudget.Reduce(task, encodingOptions, measuredBytes);
        }

        if (lastRun is null || lastRun.ExitCode != 0 || !HasOutput(temporaryPath, isFrameSequence))
        {
            var message = FfmpegErrorCapture.BuildFailureMessage(task, lastRun?.LastErrorLines ?? [], lastRun?.FailureDetail);
            DeleteIfExists(temporaryPath);
            return new ConversionResult(false, false, string.Empty, message, logPath);
        }
        return null;
    }

    private async Task<FfmpegProcessResult> EncodeImageWithSizeLimitAsync(MediaTask task, string ffmpegPath,
        string temporaryPath, string logPath, MediaConversionOptions options, AppSettings settings,
        Action<ConversionProgress> report, CancellationToken token)
    {
        FfmpegProcessResult? last = null;
        await MediaSizeBudget.SearchImageAsync(temporaryPath, options.TargetSizeBytes!.Value,
            MediaSizeBudget.InitialImageQuality(task, options), async (quality, attempt, cancellation) =>
            {
                var effective = options with { ImageQuality = quality };
                var arguments = _presetService.BuildArguments(task, temporaryPath, EncoderBackend.Cpu, false,
                    PerformanceTuning.ResolveCpuThreads(settings), cancellation, effective);
                var message = LocalizationService.T("MediaSizeAttempt", attempt, MediaSizeBudget.MaximumImageAttempts);
                report(new(0, "—", "—", message));
                last = await _runProcess(task, ffmpegPath, arguments, logPath,
                    value => report(value with { Message = message }), cancellation);
                if (last.ExitCode != 0)
                    throw new InvalidDataException(FfmpegErrorCapture.BuildFailureMessage(task, last.LastErrorLines, last.FailureDetail));
            }, token);
        return last!;
    }

    private async Task<IReadOnlyList<EncoderAttempt>> BuildAttemptsAsync(MediaTask task, string ffmpegPath, AppSettings settings, CancellationToken cancellationToken)
    {
        var requestedCodec = FormatCatalog.VideoCodecForTarget(task.TargetFormat);
        var options = MediaConversionOptions.FromTask(task);
        if (options.TargetSizeBytes.HasValue || !settings.HardwareAcceleration || task.ConversionMode == "lossless" || !_presetService.WillUseHardware(task) || requestedCodec is "vp9" or "av1")
        {
            return [new EncoderAttempt(EncoderBackend.Cpu, false)];
        }

        var container = FormatCatalog.ContainerForVideoTarget(task.TargetFormat);
        var hevc = requestedCodec == "hevc" || (requestedCodec is null && task.Probe.IsHdr && container is "MP4" or "MKV" or "MOV");
        var checks = await Task.WhenAll(
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.Nvidia, hevc, cancellationToken),
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.Intel, hevc, cancellationToken),
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.Amd, hevc, cancellationToken),
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.MediaFoundation, hevc, cancellationToken));
        var attempts = new List<EncoderAttempt>();
        if (checks[0])
        {
            attempts.Add(new EncoderAttempt(EncoderBackend.Nvidia, !options.HasVideoFilters && CanUseHardwareDecode(task)));
            if (attempts[^1].HardwareDecode)
            {
                attempts.Add(new EncoderAttempt(EncoderBackend.Nvidia, false));
            }
        }
        if (checks[1])
        {
            attempts.Add(new EncoderAttempt(EncoderBackend.Intel, !options.HasVideoFilters && CanUseHardwareDecode(task)));
            if (attempts[^1].HardwareDecode)
            {
                attempts.Add(new EncoderAttempt(EncoderBackend.Intel, false));
            }
        }
        if (checks[2])
        {
            attempts.Add(new EncoderAttempt(EncoderBackend.Amd, false));
        }
        if (checks[3])
        {
            attempts.Add(new EncoderAttempt(EncoderBackend.MediaFoundation, !options.HasVideoFilters && CanUseHardwareDecode(task)));
            if (attempts[^1].HardwareDecode)
            {
                attempts.Add(new EncoderAttempt(EncoderBackend.MediaFoundation, false));
            }
        }
        attempts.Add(new EncoderAttempt(EncoderBackend.Cpu, false));
        return attempts;
    }

    private static bool CanUseHardwareDecode(MediaTask task) =>
        task.Kind == MediaKind.Video &&
        !PresetService.RequiresYcgcoConversion(task) &&
        !(task.Probe.IsHdr && FormatCatalog.ContainerForVideoTarget(task.TargetFormat) is ("AVI" or "FLV")) &&
        task.Probe.VideoCodec is ("h264" or "hevc" or "av1" or "vp9" or "vp8" or "mpeg2video" or "vc1" or "mjpeg") &&
        HardwareAccelerationTestService.SupportsHardwareDecode(task.Probe.VideoCodec);

    private async Task<ConversionResult> ConvertToIcoAsync(
        MediaTask task,
        string ffmpegPath,
        string temporaryOutput,
        string logPath,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", task.Id.ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var frames = new List<(int Size, string PngPath)>();
        try
        {
            for (var index = 0; index < sizes.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = sizes[index];
                var pngPath = Path.Combine(temporaryDirectory, $"icon-{size}.png");
                report(new ConversionProgress(index * 100d / sizes.Length, LocalizationService.T("Processing"), "—", LocalizationService.T("GeneratingIcon", size)));
                var arguments = _presetService.BuildIcoFrameArguments(task, size, pngPath);
                var run = await _runProcess(task, ffmpegPath, arguments, logPath, _ => { }, cancellationToken);
                if (run.ExitCode != 0 || !File.Exists(pngPath))
                {
                    return new ConversionResult(false, false, string.Empty, FfmpegErrorCapture.BuildFailureMessage(task, run.LastErrorLines, run.FailureDetail), logPath);
                }
                frames.Add((size, pngPath));
            }

            IcoBuilder.Build(frames, temporaryOutput);
            return new ConversionResult(true, false, temporaryOutput, LocalizationService.T("IcoComplete"), logPath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, true);
                }
            }
            catch
            {
                // 临时文件将在系统清理时移除。
            }
        }
    }

    private static ConversionResult CancelledResult(string logPath) =>
        new(false, true, string.Empty, LocalizationService.T("Cancelled"), logPath);

    private sealed record EncoderAttempt(EncoderBackend Backend, bool HardwareDecode);
}
