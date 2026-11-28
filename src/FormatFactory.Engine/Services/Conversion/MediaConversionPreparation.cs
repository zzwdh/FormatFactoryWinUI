using System.Globalization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal sealed record PreparedMediaConversion(MediaConversionOptions Options, AppSettings Settings,
    string? FallbackDecoderPath, ImageAnimationInfo? Animation, IReadOnlyList<RemuxStream>? CopiedStreams);

internal sealed record MediaConversionPreparationResult(PreparedMediaConversion? Prepared, ConversionResult? Failure);

/// <summary>Checks source preservation, input compatibility and engine capabilities before reserving output.</summary>
internal sealed class MediaConversionPreparation(MediaInputPreflightService inputPreflight, MediaProbeService mediaProbe)
{
    private static readonly Guid ProbeSessionId = Guid.NewGuid();
    private readonly MediaInputPreflightService _inputPreflight = inputPreflight;
    private readonly MediaProbeService _mediaProbeService = mediaProbe;
    private readonly FfmpegCapabilityService _capabilityService = new();

    internal async Task<MediaConversionPreparationResult> PrepareAsync(MediaTask task, string ffmpegPath,
        AppSettings settings, string logPath, Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        string? fallbackDecoderPath = null;
        ImageAnimationInfo? animation;
        IReadOnlyList<RemuxStream>? copiedStreams = null;
        MediaConversionOptions mediaOptions;
        try
        {
            mediaOptions = MediaConversionOptions.FromTask(task);
            animation = await ImagePreservationPolicy.ValidateFfmpegInputAsync(task, cancellationToken);
            if (task.Kind is MediaKind.Video or MediaKind.Audio)
            {
                LogService.AppendTask(logPath, RuntimeFailureDiagnostics.TaskFacts(task, Guid.NewGuid()));
                var preflight = await _inputPreflight.SelectAsync(ffmpegPath,
                    CompatibilityEngineLocator.Find(settings.CompatibilityFfmpegPath), task, cancellationToken);
                var loggedDetail = preflight.ProbeDiagnostic is null ? preflight.Detail :
                    string.Join('\n', preflight.Detail.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                        .Split('\n').Select(line => "| " + line));
                LogService.AppendTask(logPath, $"Input check: {preflight.FailureKind}\n{loggedDetail}\n");
                if (preflight.ProbeDiagnostic is { } diagnostic)
                {
                    // Correlation belongs only in the attachment, never the visible error/fingerprint.
                    LogService.AppendTask(logPath,
                        $"Input probe context: session={ProbeSessionId:N} task={task.Id:N} attempt={diagnostic.AttemptId:N} candidate={(diagnostic.FallbackCandidate ? "fallback" : "primary")}\n");
                    var nativeCode = diagnostic.NativeErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none";
                    LogService.AppendTask(logPath, FormattableString.Invariant(
                        $"Input diagnostic: stage=probe candidate={(diagnostic.FallbackCandidate ? "fallback" : "primary")} exception={diagnostic.ExceptionKind} hresult=0x{diagnostic.HResult:X8} native={nativeCode}\n"));
                    if (diagnostic.ProbeDetail is { } detail)
                    {
                        var step = detail.Step switch
                        {
                            MediaProbeFailureStep.Image => "image",
                            MediaProbeFailureStep.Process => "process",
                            MediaProbeFailureStep.Parse => "parse",
                            MediaProbeFailureStep.Metadata => "metadata",
                            _ => "unknown"
                        };
                        var exitCode = detail.ProcessExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none";
                        LogService.AppendTask(logPath, $"Input probe detail: step={step} exit={exitCode}\n");
                        if (detail.Evidence is { } evidence)
                        {
                            LogService.AppendTask(logPath, FormattableString.Invariant(
                                $"Input probe evidence: category={SafeEnum(evidence.Category)} reported={SafeEnum(evidence.ReportedReason)} reported_code={Number(evidence.ReportedCode)} duration_ms={evidence.DurationMilliseconds} stdout_chars={evidence.OutputCharacters} stderr_chars={evidence.ErrorCharacters} truncated={evidence.OutputTruncated}\n"));
                            var facts = evidence.Facts;
                            LogService.AppendTask(logPath, FormattableString.Invariant(
                                $"Input probe source: observed_utc={facts?.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture) ?? "none"} status={(facts is null ? "NotChecked" : SafeEnum(facts.ReadStatus))} native={Number(facts?.SourceNativeError)} length={Number(facts?.SourceLength)} bytes_read={Number(facts?.SourceBytesRead)}\n"));
                            var digest = facts?.EngineSha256;
                            if (digest is null || digest.Length != 64 || !digest.All(char.IsAsciiHexDigit)) digest = "none";
                            LogService.AppendTask(logPath, FormattableString.Invariant(
                                $"Input probe engine: scope=selected_probe_file_after_failure state={(facts is null ? "NotChecked" : SafeEnum(facts.EngineState))} sha256={digest} length={Number(facts?.EngineLength)}\n"));
                        }
                    }
                }
                if (!preflight.Success)
                {
                    LogService.AppendTask(logPath, FormattableString.Invariant(
                        $"Preflight observation: session={RuntimeFailureDiagnostics.SessionId:N} task={task.Id:N} attempt={preflight.AttemptId:N} phase={preflight.Phase} candidate={(preflight.CandidateFallback ? "fallback" : "primary")} failure={preflight.FailureKind}\n"));
                    if (preflight.Probe is { } failedProbe)
                        LogService.AppendTask(logPath, FormattableString.Invariant(
                            $"Input stream observation: video={RuntimeFailureDiagnostics.Codec(failedProbe.VideoCodec)} video_index={failedProbe.VideoStreamIndex} audio_count={failedProbe.AudioTracks.Count} width={failedProbe.Width} height={failedProbe.Height} duration_ticks={failedProbe.Duration.Ticks}\n"));
                    if (preflight.Phase == MediaPreflightPhase.Decode && preflight.Probe is { } decodeProbe)
                        WriteDecodeObservation(logPath, task, preflight, decodeProbe);
                    if (preflight.ProbeDiagnostic is null && preflight.ObservedCandidatePath is { } candidate)
                    {
                        var facts = await MediaProbeSafeFacts.CollectAsync(candidate, task.InputPath, cancellationToken);
                        LogService.AppendTask(logPath, FormattableString.Invariant(
                            $"Preflight engine observation: scope=selected_file_after_failure state={facts.EngineState} sha256={facts.EngineSha256 ?? "none"} length={Number(facts.EngineLength)} source_status={facts.ReadStatus}\n"));
                    }
                    LogService.FlushTask(logPath);
                    return new(null, new ConversionResult(false, false, string.Empty, DescribeInputFailure(preflight), logPath));
                }
                if (preflight.Probe is { } actual) task.Probe = actual;
                if (preflight.UsedFallback)
                {
                    fallbackDecoderPath = preflight.ExecutablePath;
                    settings = settings.Snapshot();
                    settings.HardwareAcceleration = false;
                    report(new(0, "—", "—", LocalizationService.T("MediaCompatibilityUsingFallback")));
                    LogService.AppendTask(logPath, $"Compatibility decoder: {fallbackDecoderPath}\nPrimary encoder: {ffmpegPath}\n");
                }
            }
            if (RemuxCompatibilityService.IsRequested(task))
                copiedStreams = await RemuxCompatibilityService.PrepareAsync(task,
                    FfmpegLocator.GetFfprobePath(ffmpegPath), cancellationToken);
            if (task.Kind == MediaKind.Image && task.ConversionMode == "lossless" && animation is not { IsAnimated: true })
            {
                // Native import probes do not always include precision; refresh from the actual source.
                var known = task.Probe;
                var actual = await _mediaProbeService.ProbeAsync(FfmpegLocator.GetFfprobePath(ffmpegPath), task.InputPath, cancellationToken);
                actual.PageCount = Math.Max(actual.PageCount, known.PageCount);
                actual.IsAnimated |= known.IsAnimated;
                task.Probe = actual;
                animation = await ImagePreservationPolicy.ValidateFfmpegInputAsync(task, cancellationToken);
            }
            mediaOptions.Validate(task);
            _ = await MediaSizeBudget.CalculateAsync(task, mediaOptions, null, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or IOException or
                                         System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            FormatFailureDiagnostics.WriteTask(logPath, exception, task.Id);
            return new(null, new ConversionResult(false, false, string.Empty, exception.Message, string.Empty)
            { Retryable = ConversionFailurePolicy.IsTransient(exception) });
        }
        var sourceExtension = NormalizeExtension(Path.GetExtension(task.InputPath));
        var targetExtension = NormalizeExtension(FormatCatalog.ExtensionFor(task.TargetFormat));
        if (task.ConversionMode == "lossless" && sourceExtension == targetExtension)
        {
            return new(null, new ConversionResult(false, false, string.Empty, LocalizationService.T("SameLossless"), string.Empty));
        }

        try
        {
            var capabilities = await _capabilityService.ProbeAsync(ffmpegPath, cancellationToken);
            var missingComponents = fallbackDecoderPath is null
                ? FfmpegCapabilityService.GetMissingOutputComponents(capabilities, task.TargetFormat)
                : FfmpegCapabilityService.GetMissingOutputComponents(capabilities, task, task.Probe);
            if (missingComponents.Count > 0)
            {
                return new(null, new ConversionResult(
                    false,
                    false,
                    string.Empty,
                    LocalizationService.T(
                        "ConversionFailed",
                        LocalizationService.T(fallbackDecoderPath is null ? "GeneralServiceFfmpegMissingMuxer" : "MediaCompatibilityMissingOutput",
                            string.Join(", ", missingComponents))),
                    string.Empty));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 能力探测失败时继续执行，让 FFmpeg 返回最终的详细错误。
        }
        return new(new(mediaOptions, settings, fallbackDecoderPath, animation, copiedStreams), null);
    }

    private static void WriteDecodeObservation(string logPath, MediaTask task,
        MediaInputPreflightResult preflight, MediaProbeResult probe)
    {
        try
        {
            var streams = MediaInputPreflightService.RequiredStreams(task, probe);
            var observed = preflight.DecodeObservation;
            LogService.AppendTask(logPath, FormattableString.Invariant(
                $"Strict decode: task={task.Id:N} attempt={preflight.AttemptId:N} budget_ms=20000 selected_streams={streams.Count} exit={Number(observed?.ExitCode)} elapsed_ms={Number(observed?.ElapsedMs)} stdout_retained_chars={Number(observed?.OutputCharacters)} stderr_retained_chars={Number(observed?.ErrorCharacters)} truncated={observed?.Truncated.ToString() ?? "none"} last_retained_frame={Number(observed?.LastFrame)} last_retained_out_time_us={Number(observed?.LastOutputTimeUs)} process_operation={observed?.ProcessOperation?.ToString("N") ?? "none"}\n"));
            // Map is derived from numeric stream indices, never from a tag or a demuxer string.
            foreach (var stream in streams.Take(8))
                LogService.AppendTask(logPath, $"Strict decode stream: attempt={preflight.AttemptId:N} map={stream.Map} codec={RuntimeFailureDiagnostics.Codec(stream.Codec)}\n");
        }
        catch (Exception) { /* Keep the existing preflight result when optional diagnostics fail. */ }
    }

    private static string DescribeInputFailure(MediaInputPreflightResult result)
    {
        var key = result.FailureKind switch
        {
            MediaInputFailureKind.UnsupportedDecoder => "MediaCompatibilityUnsupported",
            MediaInputFailureKind.AudioSelectionUnavailable => "MediaCompatibilityAacSelectionFailed",
            MediaInputFailureKind.MissingOutputComponent => "MediaCompatibilityMissingOutput",
            MediaInputFailureKind.InvalidMedia => "MediaCompatibilityDamaged",
            MediaInputFailureKind.UnreadableInput => "MediaCompatibilityUnreadable",
            MediaInputFailureKind.DecodeTimedOut => "MediaCompatibilityTimedOut",
            _ => "MediaCompatibilityEngineUnavailable"
        };
        return LocalizationService.T(key, result.Detail);
    }

    private static string NormalizeExtension(string extension) => extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : extension.ToLowerInvariant();

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";
    private static string SafeEnum<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
}
