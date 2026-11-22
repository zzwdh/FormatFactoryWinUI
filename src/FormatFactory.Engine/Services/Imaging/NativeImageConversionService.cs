using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Native image conversion with the same output reservations and atomic commit as media jobs.</summary>
public sealed class NativeImageConversionService
{
    private readonly IImageCodecService _codecs;
    private readonly Func<MediaTask, string, AppSettings, Action<ConversionProgress>, CancellationToken, Task<ConversionResult>> _fallback;
    private readonly OutputPathService _paths;
    private readonly OutputRecoveryService _recovery;

    public NativeImageConversionService(IImageCodecService codecs, ConversionService fallback)
        : this(codecs, fallback.ConvertAsync) { }

    internal NativeImageConversionService(IImageCodecService codecs,
        Func<MediaTask, string, AppSettings, Action<ConversionProgress>, CancellationToken, Task<ConversionResult>> fallback,
        OutputRecoveryService? recovery = null, OutputPathService? paths = null)
    {
        _codecs = codecs;
        _fallback = fallback;
        _paths = paths ?? new OutputPathService();
        _recovery = recovery ?? OutputRecoveryService.Shared;
    }

    public bool CanConvertWithoutFfmpeg(MediaTask task) => task.Kind == MediaKind.Image &&
        task.InputPaths.Count == 1 && _codecs.CanEncode(task.TargetFormatId) && !task.Probe.IsAnimated &&
        !(task.TargetFormatId == "jpg" && task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase));

    public async Task<ConversionResult> ConvertAsync(MediaTask task, string ffmpegPath,
        AppSettings settings, Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        MediaConversionOptions options;
        try
        {
            options = MediaConversionOptions.FromTask(task);
            options.Validate(task);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            Observe(task, exception, FormatFailureStage.ImagePolicy, FormatFailureReason.UnsupportedRoute);
            return new(false, false, string.Empty, exception.Message, string.Empty);
        }
        var colorPolicy = task.Options.GetValueOrDefault("imageColorPolicy", "srgb").Trim().ToLowerInvariant();
        var preserve = colorPolicy == "preserve";
        if (colorPolicy is not ("srgb" or "preserve") || preserve &&
            (!WicProfilePreservingCodec.CanPreserve(task.TargetFormatId) || task.Probe.IsAnimated || task.InputPaths.Count != 1))
            return PolicyFailure(task, LocalizationService.T("NativeImagePreserveUnsupported"), FormatFailureReason.UnsupportedRoute);
        if (preserve && task.TargetFormatId == "jpg" && task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase))
            return PolicyFailure(task, LocalizationService.T("NativeImageLosslessJpegUnsupported"), FormatFailureReason.Precision);
        if (!CanConvertWithoutFfmpeg(task))
        {
            // Refresh page counts before a codec or FFmpeg fallback can flatten the source.
            var actualSource = await ProbeSourceAsync(task, cancellationToken);
            if (actualSource is { PageCount: > 1, IsAnimated: false })
                return PolicyFailure(task, LocalizationService.T("NativeImageMultiplePages"), FormatFailureReason.MultiplePages, actualSource);
            if (actualSource is not null) task.Probe = actualSource;
            if (task.Kind == MediaKind.Image && task.InputPaths.Count == 1 && !task.Probe.IsAnimated &&
                FormatCatalog.FindById(task.SourceFormatId)?.DefaultEngineId == "windows-image" &&
                !string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
                return await ConvertThroughPngAsync(task, ffmpegPath, settings, report, cancellationToken);
            return await _fallback(task, ffmpegPath, settings, report, cancellationToken);
        }
        if (task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase) && task.SourceFormatId == task.TargetFormatId)
            return PolicyFailure(task, LocalizationService.T("SameLossless"), FormatFailureReason.UnsupportedRoute);
        using var lease = _paths.Reserve(task, settings);
        MediaProbeResult? source = null;
        var diagnosticStage = FormatFailureStage.NativeImageProbe;
        try
        {
            source = await ProbeSourceAsync(task, cancellationToken);
            if (source is not null)
            {
                task.Probe = source;
                diagnosticStage = FormatFailureStage.ImagePolicy;
                options.Validate(task);
            }
            if (source is { PageCount: > 1, IsAnimated: false } && task.TargetFormatId != "tiff")
                return PolicyFailure(task, LocalizationService.T("NativeImageMultiplePages"), FormatFailureReason.MultiplePages, source);
            report(new ConversionProgress(0, "—", "—", LocalizationService.T(preserve ? "NativeImagePreserving" : "NativeImageConverting")));
            diagnosticStage = FormatFailureStage.NativeImageEncode;
            if (options.TargetSizeBytes is { } limit)
            {
                await MediaSizeBudget.SearchImageAsync(lease.TemporaryPath, limit,
                    MediaSizeBudget.InitialImageQuality(task, options), async (quality, attempt, token) =>
                    {
                        report(new(0, "—", "—", LocalizationService.T("MediaSizeAttempt", attempt, MediaSizeBudget.MaximumImageAttempts)));
                        await _codecs.TranscodeWithOptionsAsync(task.InputPath, lease.TemporaryPath, task.TargetFormatId,
                            task.ConversionMode, colorPolicy, options with { TargetSizeBytes = null, ImageQuality = quality }, token);
                    }, cancellationToken);
            }
            else
                await _codecs.TranscodeWithOptionsAsync(task.InputPath, lease.TemporaryPath, task.TargetFormatId,
                    task.ConversionMode, colorPolicy, options, cancellationToken);
            diagnosticStage = FormatFailureStage.NativeImageValidate;
            var verified = await _codecs.ProbeAsync(lease.TemporaryPath, cancellationToken);
            if (verified is null || verified.Width <= 0 || verified.Height <= 0 ||
                !File.Exists(lease.TemporaryPath) || new FileInfo(lease.TemporaryPath).Length == 0 ||
                source is { PageCount: > 0 } && verified.PageCount != source.PageCount ||
                task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase) && source is not null &&
                (source.Width > 0 && verified.Width != source.Width || source.Height > 0 && verified.Height != source.Height ||
                 ImagePreservationPolicy.PixelBitDepth(verified.PixelFormat) < ImagePreservationPolicy.PixelBitDepth(source.PixelFormat)))
                throw new InvalidDataException(LocalizationService.T("NativeImageValidationFailed"));
            if (options.HasDimensions && source is not null)
            {
                var dimensions = options.ResolveDimensions(source.Width, source.Height);
                if (verified.Width != dimensions.Width || verified.Height != dimensions.Height)
                    throw new InvalidDataException(LocalizationService.T("NativeImageValidationFailed"));
            }
            if (options.TargetSizeBytes is { } targetSize && new FileInfo(lease.TemporaryPath).Length > targetSize)
                throw MediaSizeBudget.CannotMeet();
            if (task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase) &&
                (source is null || !await _codecs.ValidateLosslessAsync(task.InputPath, lease.TemporaryPath, cancellationToken)))
                throw new InvalidDataException(LocalizationService.T("NativeImageLosslessSamplesChanged"));
            cancellationToken.ThrowIfCancellationRequested();
            diagnosticStage = FormatFailureStage.NativeImageCommit;
            lease.CommitValidated(task.Id, _recovery, cancellationToken,
                task.TargetFormatId, task.ConversionMode, task.EngineId);
            ConversionService.ReportCommittedProgress(report);
            return new(true, false, lease.FinalPath, LocalizationService.T("ConversionFinished"), string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new(false, true, string.Empty, LocalizationService.T("Cancelled"), string.Empty);
        }
        catch (OutputCommitException exception)
        {
            return CommitFailure(exception);
        }
        catch (Exception exception)
        {
            Observe(task, exception, diagnosticStage,
                diagnosticStage == FormatFailureStage.NativeImageValidate ? FormatFailureReason.OutputValidation : FormatFailureReason.CodecFailure, source);
            DeleteTemporary(lease.TemporaryPath);
            if (preserve || task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase) || exception is ImagePrecisionUnsupportedException ||
                options.TargetSizeBytes.HasValue && exception is InvalidDataException)
                return new(false, false, string.Empty, LocalizationService.T("ConversionFailed", exception.Message), string.Empty);
            // Do not silently discard TIFF pages when a single-page encoder is selected.
            if (source is { PageCount: > 1, IsAnimated: false } || task.Probe.PageCount > 1 && !task.Probe.IsAnimated)
                return new(false, false, string.Empty, LocalizationService.T("NativeImageMultiplePages"), string.Empty);
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                return new(false, false, string.Empty,
                    LocalizationService.T("NativeImageCodecRequired") + " " + exception.Message, string.Empty);
            report(new ConversionProgress(0, "—", "—", LocalizationService.T("BackendFallback", "Windows Imaging")));
            lease.Dispose();
            return await _fallback(task, ffmpegPath, settings, report, cancellationToken);
        }
    }

    private static ConversionResult CommitFailure(OutputCommitException exception) =>
        new(false, false, string.Empty, exception.Message, string.Empty)
        {
            RecoverableOutputId = exception.RecoveryId,
            PreservedOutputPath = exception.PreservedPath
        };

    private static void DeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<MediaProbeResult?> ProbeSourceAsync(MediaTask task, CancellationToken token)
    {
        try { return await _codecs.ProbeAsync(task.InputPath, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            Observe(task, error, FormatFailureStage.NativeImageProbe, FormatFailureReason.CodecFailure);
            throw;
        }
    }

    private static void Observe(MediaTask task, Exception error, FormatFailureStage stage, FormatFailureReason reason,
        MediaProbeResult? observed = null)
    {
        FormatFailureDiagnostics.Record(error, FormatFailureDiagnostics.Image(task, stage, reason, observed));
        FormatFailureDiagnostics.WriteApp(error, task.Id);
    }

    private static ConversionResult PolicyFailure(MediaTask task, string message, FormatFailureReason reason, MediaProbeResult? observed = null)
    {
        var result = new ConversionResult(false, false, string.Empty, message, string.Empty);
        FormatFailureDiagnostics.Record(result, FormatFailureDiagnostics.Image(task, FormatFailureStage.ImagePolicy, reason, observed));
        FormatFailureDiagnostics.WriteApp(result, task.Id);
        return result;
    }

    private async Task<ConversionResult> ConvertThroughPngAsync(MediaTask task, string ffmpegPath,
        AppSettings settings, Action<ConversionProgress> report, CancellationToken token)
    {
        using var lease = _paths.Reserve(task, settings);
        var directory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", "native-image", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var decoded = Path.Combine(directory, "decoded.png");
        using var workspaceBudget = DiskSpaceBudgetService.Shared.Reserve(task, directory, decoded);
        string? encoded = null;
        var diagnosticStage = FormatFailureStage.NativeImageIntermediate;
        try
        {
            // Propagate lossless mode to the intermediate decode so high-bit-depth
            // HEIF/RAW cannot be quantized before the final encoder sees the request.
            await _codecs.TranscodeAsync(task.InputPath, decoded, "png", task.ConversionMode, token);
            var probe = await _codecs.ProbeAsync(decoded, token)
                ?? throw new InvalidDataException(LocalizationService.T("NativeImageValidationFailed"));
            if (task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase) && !await _codecs.ValidateLosslessAsync(task.InputPath, decoded, token))
                throw new InvalidDataException(LocalizationService.T("NativeImageLosslessSamplesChanged"));
            var normalized = new MediaTask
            {
                Id = task.Id, InputPath = decoded, SourceFormatId = "png", Probe = probe,
                TargetFormatId = task.TargetFormatId, ConversionMode = task.ConversionMode,
                ParametersVersion = task.ParametersVersion
            };
            foreach (var option in task.Options) normalized.Options[option.Key] = option.Value;
            // Intermediate artifacts always stay in this private workspace, including
            // when the admitted task has its own output-directory override.
            normalized.Options[OutputPathService.DirectoryOverrideKey] = directory;
            normalized.Options.Remove("output.groupId");
            normalized.Options.Remove("outputLabel");
            var isolated = new AppSettings
            {
                OutputMode = "custom", CustomOutputDirectory = directory,
                CpuThreadsPerTask = settings.CpuThreadsPerTask, ParallelCount = settings.ParallelCount,
                HardwareAcceleration = settings.HardwareAcceleration
            };
            var result = await _fallback(normalized, ffmpegPath, isolated, value => report(value with
            {
                Percent = Math.Clamp(value.Percent, 0, 100) * 0.95,
                Message = value.Percent >= 100 ? LocalizationService.T("Converting") : value.Message,
                Remaining = value.Percent >= 100 ? "—" : value.Remaining
            }), token);
            if (!result.Success) return result;
            encoded = result.OutputPath;
            token.ThrowIfCancellationRequested();
            // Temporary storage can be on another volume. Copy into the reserved
            // sibling path first, then publish the complete file atomically.
            diagnosticStage = FormatFailureStage.NativeImageCommit;
            report(new(95, "—", "—", LocalizationService.T("Converting")));
            await using (var source = File.OpenRead(encoded))
            await using (var destination = new FileStream(lease.TemporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(destination, token);
                await destination.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            lease.CommitValidated(task.Id, _recovery, token,
                task.TargetFormatId, task.ConversionMode, task.EngineId);
            ConversionService.ReportCommittedProgress(report);
            return result with { OutputPath = lease.FinalPath };
        }
        catch (OperationCanceledException)
        {
            return new(false, true, string.Empty, LocalizationService.T("Cancelled"), string.Empty);
        }
        catch (OutputCommitException exception)
        {
            return CommitFailure(exception);
        }
        catch (Exception exception)
        {
            Observe(task, exception, diagnosticStage, FormatFailureReason.CodecFailure);
            return new(false, false, string.Empty, LocalizationService.T("ConversionFailed", exception.Message), string.Empty);
        }
        finally
        {
            DeleteTemporary(decoded);
            if (encoded is not null) DeleteTemporary(encoded);
            // This private directory contains only the two known artifacts; never delete recursively.
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
