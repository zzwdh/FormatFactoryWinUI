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
        var options = prepared.Options;
        var threads = PerformanceTuning.ResolveCpuThreads(prepared.Settings);
        var frameSequence = FormatCatalog.IsFrameSequence(task.TargetFormat);
        var plan = await PresetService.DescribeAsync(task, temporaryPath, EncoderBackend.Cpu, false, threads, cancellationToken, options);
        var available = await ProbeEncodersAsync(task, ffmpegPath, prepared.Settings, cancellationToken);
        var begin = new
        {
            mode = "encode", plan, available, sourceCodec = task.Probe.VideoCodec,
            decodeAvailable = HardwareAccelerationTestService.SupportsHardwareDecode(task.Probe.VideoCodec),
            budget = options.TargetSizeBytes is { } limit ? MediaSizeBudget.Describe(task, options, limit, null) : null
        };

        using var session = new MediaWorkerSession(cancellationToken);
        FfmpegProcessResult? last = null;
        await session.SendAsync(begin);
        while (true)
        {
            var step = await session.ReadAsync<SessionEvent>();
            switch (step.Event)
            {
                case "run":
                    cancellationToken.ThrowIfCancellationRequested();
                    DeleteIfExists(temporaryPath);
                    if (frameSequence) Directory.CreateDirectory(temporaryPath);
                    var name = BackendName(step.Backend, threads);
                    var pathName = prepared.CopiedStreams is not null ? LocalizationService.T("RemuxCopying")
                        : step.HardwareDecode ? LocalizationService.T("GpuDecode", name) : name;
                    report(new(0, "—", "—", pathName));
                    NativeDiagnostics.Log.EncoderAttempt(task.Id.ToString("N"), step.Backend!, step.HardwareDecode,
                        step.Number, step.Number > 1);
                    var progress = options.TargetSizeBytes.HasValue
                        ? new Action<ConversionProgress>(value => report(value with
                            { Message = LocalizationService.T("MediaSizeAttempt", step.SizeAttempt, step.SizeAttempts) })) : report;
                    last = await _runProcess(task, ffmpegPath, step.Arguments!, logPath, progress, cancellationToken);
                    if (cancellationToken.IsCancellationRequested && last.ExitCode != 0)
                        return new(false, true, string.Empty, LocalizationService.T("Cancelled"), logPath);
                    cancellationToken.ThrowIfCancellationRequested();
                    await session.SendAsync(new { exitCode = last.ExitCode });
                    break;
                case "fallback":
                    report(new(0, "—", "—", LocalizationService.T("BackendFallback", BackendName(step.Backend, threads))));
                    break;
                case "measured":
                    RuntimeFailureDiagnostics.WriteTaskObservation(logPath,
                        $"Output size attempt: task={task.Id:N} attempt={step.SizeAttempt} maximum_bytes={step.Limit} actual_bytes={step.Actual} duration_ticks={task.Probe.Duration.Ticks} video_kbps={RuntimeFailureDiagnostics.Number(step.VideoBitrate)} audio_kbps={RuntimeFailureDiagnostics.Number(step.AudioBitrate)}\n");
                    break;
                case "failed":
                case "complete":
                case "error":
                    await session.CompleteAsync();
                    if (step.Event == "complete") return null;
                    if (step.Event == "error") throw new InvalidDataException(LocalizationService.T(step.Key!));
                    DeleteIfExists(temporaryPath);
                    var message = FfmpegErrorCapture.BuildFailureMessage(task, last?.LastErrorLines ?? [], last?.FailureDetail);
                    return new(false, false, string.Empty, message, logPath);
                default:
                    throw new InvalidDataException("Unknown encoding session event.");
            }
        }
    }

    private sealed record SessionEvent(string Event, string[]? Arguments, string? Backend, bool HardwareDecode, int Number,
        int SizeAttempt, int SizeAttempts, long Limit, long Actual, int? VideoBitrate, int? AudioBitrate, string? Key);

    private static string BackendName(string? backend, int threads) => backend switch
    {
        "Nvidia" => "NVIDIA NVENC", "Intel" => "Intel Quick Sync", "Amd" => "AMD AMF",
        "MediaFoundation" => "Windows Media Foundation", _ => $"CPU · {threads} threads"
    };

    private async Task<FfmpegProcessResult> EncodeImageWithSizeLimitAsync(MediaTask task, string ffmpegPath,
        string temporaryPath, string logPath, MediaConversionOptions options, AppSettings settings,
        Action<ConversionProgress> report, CancellationToken token)
    {
        FfmpegProcessResult? last = null;
        await MediaSizeBudget.SearchImageAsync(temporaryPath, options.TargetSizeBytes!.Value,
            MediaSizeBudget.InitialImageQuality(task, options), async (quality, attempt, cancellation) =>
            {
                var effective = options with { ImageQuality = quality };
                var arguments = await _presetService.BuildArgumentsAsync(task, temporaryPath, EncoderBackend.Cpu, false,
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

    private async Task<bool[]> ProbeEncodersAsync(MediaTask task, string ffmpegPath, AppSettings settings, CancellationToken cancellationToken)
    {
        var requestedCodec = FormatCatalog.VideoCodecForTarget(task.TargetFormat);
        var options = MediaConversionOptions.FromTask(task);
        if (options.TargetSizeBytes.HasValue || !settings.HardwareAcceleration || task.ConversionMode == "lossless" || !_presetService.WillUseHardware(task) || requestedCodec is "vp9" or "av1")
        {
            return [false, false, false, false];
        }

        var container = FormatCatalog.ContainerForVideoTarget(task.TargetFormat);
        var hevc = requestedCodec == "hevc" || (requestedCodec is null && task.Probe.IsHdr && container is "MP4" or "MKV" or "MOV");
        return await Task.WhenAll(
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.Nvidia, hevc, cancellationToken),
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.Intel, hevc, cancellationToken),
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.Amd, hevc, cancellationToken),
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.MediaFoundation, hevc, cancellationToken));
    }

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
                var arguments = await _presetService.BuildIcoFrameArgumentsAsync(task, size, pngPath, cancellationToken);
                var run = await _runProcess(task, ffmpegPath, arguments, logPath, _ => { }, cancellationToken);
                if (run.ExitCode != 0 || !File.Exists(pngPath))
                {
                    return new ConversionResult(false, false, string.Empty, FfmpegErrorCapture.BuildFailureMessage(task, run.LastErrorLines, run.FailureDetail), logPath);
                }
                frames.Add((size, pngPath));
            }

            await IcoBuilder.BuildAsync(frames, temporaryOutput, cancellationToken);
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

}
