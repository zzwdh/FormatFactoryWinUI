using System.Diagnostics;
using FormatFactoryWinUI.Models;
using static FormatFactoryWinUI.Services.MediaConversionArtifacts;

namespace FormatFactoryWinUI.Services;

public sealed record ConversionProgress(double Percent, string Speed, string Remaining, string Message);
public sealed record ConversionResult(bool Success, bool Cancelled, string OutputPath, string Message, string LogPath)
{
    public string? RecoverableOutputId { get; init; }
    public string? PreservedOutputPath { get; init; }
    public bool Retryable { get; init; }
}

/// <summary>Coordinates the media stages and owns the active process and cancellation lifetimes.</summary>
public sealed class ConversionService
{
    private readonly MediaConversionOperations _operations = new();
    private readonly HardwareCapabilitiesService _hardwareCapabilities = new();
    private readonly OutputPathService _outputPathService = new();
    private readonly MediaConversionPreparation _preparation;
    private readonly MediaOutputValidator _validator;
    private readonly MediaOutputCommitter _committer;

    public ConversionService() : this(new MediaInputPreflightService()) { }

    internal ConversionService(MediaInputPreflightService inputPreflight, OutputRecoveryService? outputRecovery = null)
    {
        var mediaProbe = new MediaProbeService();
        _preparation = new(inputPreflight, mediaProbe);
        _validator = new(mediaProbe);
        _committer = new(outputRecovery ?? OutputRecoveryService.Shared);
    }

    public bool HasActiveTasks => _operations.HasActiveTasks;

    public async Task<ConversionResult> ConvertAsync(
        MediaTask task,
        string ffmpegPath,
        AppSettings settings,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        await using var operation = _operations.Begin(task.Id, cancellationToken);
        var logPath = LogService.CreateTaskLogPath(task.Id);
        using var logProtection = LogFilePolicy.Shared.Protect(logPath);
        try
        {
            var encoding = new MediaEncodingService(operation.Runner.RunProcessAsync, _hardwareCapabilities);
            return await ConvertCoreAsync(task, ffmpegPath, settings, report, encoding, logPath, operation.Token);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // Preserve preflight cancellation from the caller. A service command has no
            // cancelled caller token, so publish its outcome explicitly to the workflow.
            cancellationToken.ThrowIfCancellationRequested();
            return CancelledResult(logPath);
        }
        finally
        {
            try
            {
                LogService.FlushTask(logPath);
            }
            catch (Exception ex)
            {
                // Logging failure must not replace a committed output or cancellation result.
                Debug.WriteLine($"Conversion log flush failed: {ex.Message}");
            }
        }
    }

    private async Task<ConversionResult> ConvertCoreAsync(MediaTask task, string ffmpegPath, AppSettings settings,
        Action<ConversionProgress> report, MediaEncodingService encoding, string logPath, CancellationToken cancellationToken)
    {
        var preparation = await _preparation.PrepareAsync(task, ffmpegPath, settings, logPath, report, cancellationToken);
        if (preparation.Failure is { } preparationFailure) return preparationFailure;
        var prepared = preparation.Prepared!;
        settings = prepared.Settings;
        var fallbackDecoderPath = prepared.FallbackDecoderPath;
        using var outputPathLease = _outputPathService.Reserve(task, settings);
        var temporaryPath = outputPathLease.TemporaryPath;
        var isFrameSequence = FormatCatalog.IsFrameSequence(task.TargetFormat);
        if (isFrameSequence)
        {
            Directory.CreateDirectory(temporaryPath);
        }
        using var powerLease = PowerRequestService.Acquire(LocalizationService.T("GeneralServicePowerMediaConversion"));
        var warning = prepared.CopiedStreams is null ? encoding.GetPreservationWarning(task) : string.Empty;
        var originalInputPath = task.InputPath;
        var originalProbe = task.Probe;
        var originalAudioTrack = task.SelectedAudioTrack;
        var hadAudioTrackPolicy = task.Options.TryGetValue(CompatibleAudioTrackPolicy.OptionKey, out var originalAudioTrackPolicy);
        var validateCompatibleAudioSelection = CompatibleAudioTrackPolicy.IsRequested(task) && CompatibleAudioTrackPolicy.IsSupportedOutput(task);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            report(new ConversionProgress(0, "—", "—", warning));
        }

        try
        {
            using var compatibleInput = fallbackDecoderPath is null ? null
                : await new MediaCompatibilityDecodeService().PrepareAsync(fallbackDecoderPath, ffmpegPath,
                    task, Path.GetDirectoryName(temporaryPath)!, cancellationToken);
            if (compatibleInput is not null)
            {
                LogService.AppendTask(logPath, $"Compatibility decode log: {compatibleInput.LogPath}\n");
                task.InputPath = compatibleInput.Path;
                task.Probe = compatibleInput.Probe;
                if (FormatCatalog.IsAudioFormat(task.TargetFormat)) task.SelectedAudioTrack = 0;
                // The selected AAC track is now the sole PCM track. Do not select it again by source codec/index.
                if (validateCompatibleAudioSelection) task.Options.Remove(CompatibleAudioTrackPolicy.OptionKey);
            }
            if (await encoding.EncodeAsync(task, ffmpegPath, temporaryPath, logPath, prepared, report, cancellationToken) is { } encodingFailure)
                return encodingFailure;

            if (await _validator.ValidateAsync(task, ffmpegPath, temporaryPath, logPath, prepared, originalProbe,
                    validateCompatibleAudioSelection, cancellationToken) is { } validationFailure)
                return validationFailure;

            return _committer.Commit(task, outputPathLease, logPath, report, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(temporaryPath);
            return CancelledResult(logPath);
        }
        catch (OutputCommitException ex)
        {
            LogService.AppendTask(logPath, $"Validated output could not be committed: {ex}\nPreserved output: {ex.PreservedPath}\n");
            if (StorageFailureDiagnostics.Format(ex) is { } commitFacts)
                RuntimeFailureDiagnostics.WriteTaskObservation(logPath, $"{commitFacts}\n");
            return new(false, false, string.Empty, ex.Message, logPath)
            {
                RecoverableOutputId = ex.RecoveryId,
                PreservedOutputPath = ex.PreservedPath
            };
        }
        catch (Exception ex)
        {
            DeleteIfExists(temporaryPath);
            LogService.AppendTask(
                logPath,
                Environment.NewLine + LocalizationService.T("OperationalLogTaskException", ex) + Environment.NewLine);
            return new ConversionResult(false, false, string.Empty, LocalizationService.T("ConversionFailed", ex.Message), logPath)
            { Retryable = ConversionFailurePolicy.IsTransient(ex) };
        }
        finally
        {
            task.InputPath = originalInputPath;
            task.Probe = originalProbe;
            task.SelectedAudioTrack = originalAudioTrack;
            if (hadAudioTrackPolicy) task.Options[CompatibleAudioTrackPolicy.OptionKey] = originalAudioTrackPolicy!;
            else task.Options.Remove(CompatibleAudioTrackPolicy.OptionKey);
        }
    }

    public bool Pause(Guid taskId) => _operations.FindRunner(taskId)?.Pause() == true;

    public bool Resume(Guid taskId) => _operations.FindRunner(taskId)?.Resume() == true;

    public void Cancel(Guid taskId) => _operations.Cancel(taskId);

    public void CancelAll() => _operations.CancelAll();

    internal static void ReportCommittedProgress(Action<ConversionProgress> report) =>
        MediaOutputCommitter.ReportCommittedProgress(report);

    private static ConversionResult CancelledResult(string logPath) => new(false, true, string.Empty, LocalizationService.T("Cancelled"), logPath);
}
