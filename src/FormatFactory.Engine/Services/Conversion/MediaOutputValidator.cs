using FormatFactoryWinUI.Models;
using static FormatFactoryWinUI.Services.MediaConversionArtifacts;

namespace FormatFactoryWinUI.Services;

/// <summary>Verifies size, duration, dimensions, preserved streams and media validity before publication.</summary>
internal sealed class MediaOutputValidator(MediaProbeService mediaProbe)
{
    private readonly MediaProbeService _mediaProbeService = mediaProbe;

    internal async Task<ConversionResult?> ValidateAsync(MediaTask task, string ffmpegPath, string temporaryPath,
        string logPath, PreparedMediaConversion prepared, MediaProbeResult originalProbe,
        bool validateCompatibleAudioSelection, CancellationToken cancellationToken)
    {
        var operation = Guid.NewGuid();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        LogService.AppendTask(logPath, RuntimeFailureDiagnostics.TaskFacts(task, operation));
        try
        {
            return await ValidateCoreAsync(task, ffmpegPath, temporaryPath, logPath, prepared, originalProbe,
                validateCompatibleAudioSelection, new OutputProbeObservation(task.Id, operation, logPath), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RuntimeFailureDiagnostics.Record(error, new(RuntimeFailureStage.OutputValidation, operation, 1,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            LogService.AppendTask(logPath, $"Output validation exception: {RuntimeFailureDiagnostics.ExceptionFields(error)}\n");
            throw;
        }
    }

    private async Task<ConversionResult?> ValidateCoreAsync(MediaTask task, string ffmpegPath, string temporaryPath,
        string logPath, PreparedMediaConversion prepared, MediaProbeResult originalProbe,
        bool validateCompatibleAudioSelection, OutputProbeObservation observation, CancellationToken cancellationToken)
    {
        var mediaOptions = prepared.Options;
        var animation = prepared.Animation;
        var copiedStreams = prepared.CopiedStreams;
        var ffprobePath = FfmpegLocator.GetFfprobePath(ffmpegPath);
        if (mediaOptions.TargetSizeBytes is { } targetSize)
        {
            var outputBytes = new FileInfo(temporaryPath).Length;
            if (outputBytes > targetSize)
            {
                LogService.AppendTask(logPath, FormattableString.Invariant($"Output validation: stage=size actual_bytes={outputBytes} maximum_bytes={targetSize}\n"));
                throw MediaSizeBudget.CannotMeet();
            }
            if (task.Kind is MediaKind.Video or MediaKind.Audio)
            {
                var actualOutput = await _mediaProbeService.ProbeAsync(ffprobePath, temporaryPath, cancellationToken);
                if (!MediaSizeBudget.HasCompleteDuration(originalProbe.Duration, actualOutput.Duration, mediaOptions.FrameRate))
                {
                    LogService.AppendTask(logPath, FormattableString.Invariant($"Output validation: stage=duration expected_ticks={originalProbe.Duration.Ticks} actual_ticks={actualOutput.Duration.Ticks}\n"));
                    throw new InvalidDataException(LocalizationService.T("MediaSizeOutputIncomplete"));
                }
            }
        }
        if (task.Kind == MediaKind.Image && mediaOptions.HasAny)
        {
            var actualOutput = await _mediaProbeService.ProbeAsync(ffprobePath, temporaryPath, cancellationToken);
            var expected = mediaOptions.HasDimensions
                ? mediaOptions.ResolveDimensions(originalProbe.Width, originalProbe.Height)
                : (Width: originalProbe.Width, Height: originalProbe.Height);
            if (actualOutput.Width != expected.Width || actualOutput.Height != expected.Height)
            {
                LogService.AppendTask(logPath, FormattableString.Invariant($"Output validation: stage=dimensions expected_width={expected.Width} expected_height={expected.Height} actual_width={actualOutput.Width} actual_height={actualOutput.Height}\n"));
                throw new InvalidDataException(LocalizationService.T("NativeImageValidationFailed"));
            }
        }

        if (!await _mediaProbeService.ValidateOutputAsync(ffprobePath, temporaryPath, task, animation, cancellationToken,
            ffmpegPath, validateCompatibleAudioSelection, observation) ||
            copiedStreams is not null && !await RemuxCompatibilityService.ValidateOutputAsync(
                ffprobePath, temporaryPath, copiedStreams, cancellationToken))
        {
            LogService.AppendTask(logPath, FormattableString.Invariant($"Output validation: stage=media_or_preservation animated={animation is { IsAnimated: true }} copied_streams={copiedStreams?.Count ?? 0} compatible_audio={validateCompatibleAudioSelection} accepted=False\n"));
            var staticLosslessImage = task.Kind == MediaKind.Image && task.ConversionMode == "lossless" &&
                animation is not { IsAnimated: true };
            if (staticLosslessImage)
                LogService.AppendTask(logPath, "Output validation failed at static-lossless-image.\n");
            DeleteIfExists(temporaryPath);
            return new ConversionResult(
                false,
                false,
                string.Empty,
                LocalizationService.T("ConversionFailed", LocalizationService.T(animation is { IsAnimated: true }
                    ? "NativeImageAnimationValidationFailed"
                    : staticLosslessImage ? "NativeImageValidationFailed" : "GeneralServiceOutputNoMediaStream")),
                logPath);
        }

        return null;
    }
}
