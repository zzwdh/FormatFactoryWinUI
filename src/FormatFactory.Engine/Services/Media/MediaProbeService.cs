using System.ComponentModel;
using System.Diagnostics;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed class MediaProbeService
{
    private readonly IMediaMetadataReader _metadataReader;
    private readonly IImageCodecService? _imageCodecs;
    private readonly Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>> _runProbe;
    private readonly Func<string, string, CancellationToken, Task<MediaProbeSafeFacts>> _collectFacts;

    public MediaProbeService(IMediaMetadataReader? metadataReader = null, IImageCodecService? imageCodecs = null)
        : this(metadataReader, imageCodecs, ProcessRunner.RunAsync) { }

    internal MediaProbeService(IMediaMetadataReader? metadataReader, IImageCodecService? imageCodecs,
        Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>> runProbe,
        Func<string, string, CancellationToken, Task<MediaProbeSafeFacts>>? collectFacts = null)
    {
        _metadataReader = metadataReader ?? new NullMediaMetadataReader();
        _imageCodecs = imageCodecs;
        _runProbe = runProbe;
        _collectFacts = collectFacts ?? MediaProbeSafeFacts.CollectAsync;
    }

    public async Task<MediaProbeResult> ProbeAsync(string ffprobePath, string inputPath, CancellationToken cancellationToken = default)
    {
        var nativeAnimation = false;
        if (_imageCodecs is not null && FormatCatalog.FindByExtension(inputPath)?.ContentKind == ContentKind.Image)
        {
            MediaProbeResult? image;
            try { image = await _imageCodecs.ProbeAsync(inputPath, cancellationToken); }
            catch (Exception exception) when (MediaProbeFailureDetails.CanDescribe(exception))
            {
                MediaProbeFailureDetails.Record(exception, MediaProbeFailureStep.Image, null);
                throw;
            }
            nativeAnimation = image?.IsAnimated == true;
            // FFprobe supplies animation timing and duration when it is available.
            if (image is not null && (!image.IsAnimated || string.IsNullOrWhiteSpace(ffprobePath))) return image;
        }
        if (string.IsNullOrWhiteSpace(ffprobePath))
            throw new NotSupportedException(LocalizationService.T("NativeImageCodecRequired"));
        ProcessExecutionResult result;
        var started = Stopwatch.GetTimestamp();
        try
        {
            result = await _runProbe(
                ffprobePath,
                ["-v", "error", "-print_format", "json", "-show_error", "-show_format", "-show_streams", inputPath],
                TimeSpan.FromSeconds(30),
                cancellationToken);
        }
        catch (Exception exception) when (MediaProbeFailureDetails.CanDescribe(exception))
        {
            await RecordFailureAsync(exception, MediaProbeFailureStep.Process, null, started, ffprobePath, inputPath, cancellationToken);
            throw;
        }
        if (result.ExitCode != 0)
        {
            var exception = new InvalidDataException(string.IsNullOrWhiteSpace(result.StandardError)
                ? LocalizationService.T("GeneralServiceFfprobeCannotRead")
                : result.StandardError.Trim());
            await RecordFailureAsync(exception, MediaProbeFailureStep.Process, result, started, ffprobePath, inputPath, cancellationToken);
            throw exception;
        }

        MediaProbeResult probe;
        try { probe = MediaProbeParser.Parse(result.StandardOutput, inputPath); }
        catch (Exception exception) when (MediaProbeFailureDetails.CanDescribe(exception))
        {
            await RecordFailureAsync(exception, MediaProbeFailureStep.Parse, result, started, ffprobePath, inputPath, cancellationToken);
            throw;
        }
        probe.IsAnimated |= nativeAnimation;
        cancellationToken.ThrowIfCancellationRequested();
        MediaFoundationMetadata? metadata;
        try { metadata = await _metadataReader.ReadAsync(inputPath, cancellationToken); }
        catch (Exception exception) when (MediaProbeFailureDetails.CanDescribe(exception))
        {
            await RecordFailureAsync(exception, MediaProbeFailureStep.Metadata, result, started, ffprobePath, inputPath, cancellationToken);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (metadata is not null)
        {
            if (probe.Duration <= TimeSpan.Zero) probe.Duration = metadata.Duration;
            if (probe.Bitrate == 0) probe.Bitrate = metadata.Bitrate;
            if (probe.Width == 0) probe.Width = metadata.Width;
            if (probe.Height == 0) probe.Height = metadata.Height;
            probe.Title = metadata.Title;
            probe.Artist = metadata.Artist;
            probe.Album = metadata.Album;
        }
        if (Path.GetExtension(inputPath).Equals(".webp", StringComparison.OrdinalIgnoreCase) &&
            probe.Duration > TimeSpan.FromMilliseconds(100))
        {
            probe.IsAnimated = true;
        }
        return probe;
    }

    private async Task RecordFailureAsync(Exception exception, MediaProbeFailureStep step, ProcessExecutionResult? result,
        long started, string probePath, string inputPath, CancellationToken cancellationToken)
    {
        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var category = step switch
        {
            MediaProbeFailureStep.Parse => MediaProbeFailureCategory.Parse,
            MediaProbeFailureStep.Metadata => MediaProbeFailureCategory.Metadata,
            MediaProbeFailureStep.Process when exception is TimeoutException => MediaProbeFailureCategory.Timeout,
            MediaProbeFailureStep.Process when result is null => MediaProbeFailureCategory.ProcessException,
            MediaProbeFailureStep.Process => MediaProbeFailureCategory.ProcessExit,
            _ => MediaProbeFailureCategory.Unknown
        };
        MediaProbeSafeFacts? facts = null;
        cancellationToken.ThrowIfCancellationRequested();
        try { facts = await _collectFacts(probePath, inputPath, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException or
            Win32Exception or ArgumentException or NotSupportedException or InvalidOperationException or TimeoutException) { }
        cancellationToken.ThrowIfCancellationRequested();
        var reported = MediaProbeFailureDetails.ReadReportedError(result);
        MediaProbeFailureDetails.Record(exception, step, result?.ExitCode,
            new(category, reported.Reason, reported.Code, elapsed, result?.StandardOutput.Length ?? 0,
                result?.StandardError.Length ?? 0, result?.OutputTruncated ?? false, facts));
    }

    internal async Task<bool> ValidateOutputAsync(string ffprobePath, string outputPath, MediaTask task,
        ImageAnimationInfo? sourceAnimation, CancellationToken cancellationToken = default, string? ffmpegPath = null,
        bool validateCompatibleAudioSelection = false, OutputProbeObservation? observation = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceAnimation is { IsAnimated: true })
        {
            try
            {
                var output = ImageAnimationInfoReader.Read(outputPath, cancellationToken);
                if (!sourceAnimation.Matches(output, task.ConversionMode == "lossless")) return false;
                // FFprobe can report an image stream without decoding its frame payload.
                // Animated WebP decoders may expose only the first image, so decode each
                // ANMF image independently before accepting the whole animation.
                if (task.TargetFormatId == "webp")
                    return await ImageAnimationInfoReader.ValidateWebpFramesAsync(outputPath, ffmpegPath ?? string.Empty, cancellationToken);
                if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath)) return false;
                var decoded = await ProcessRunner.RunAsync(ffmpegPath,
                    ["-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-i", outputPath, "-map", "0:V:0", "-f", "null", "-"],
                    TimeSpan.FromSeconds(60), cancellationToken);
                return decoded.ExitCode == 0;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException or TimeoutException)
            {
                return false;
            }
        }
        if (!await ValidateOutputCoreAsync(ffprobePath, outputPath, cancellationToken, observation)) return false;
        if (validateCompatibleAudioSelection)
        {
            try
            {
                var output = await ProbeAsync(ffprobePath, outputPath, cancellationToken);
                if (!ValidateCompatibleAudioOutput(task, output)) return false;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }
        if (task.Kind != MediaKind.Image || task.ConversionMode != "lossless") return true;
        try
        {
            var output = await ProbeAsync(ffprobePath, outputPath, cancellationToken);
            if ((task.Probe.Width > 0 && output.Width != task.Probe.Width) ||
                (task.Probe.Height > 0 && output.Height != task.Probe.Height) ||
                ImagePreservationPolicy.PixelBitDepth(output.PixelFormat) < ImagePreservationPolicy.PixelBitDepth(task.Probe.PixelFormat) ||
                TiffPageCountReader.Read(outputPath, cancellationToken) > 1 || string.IsNullOrWhiteSpace(ffmpegPath)) return false;
            var sourcePixels = await ReadImagePixelHashAsync(ffmpegPath, task.InputPath, cancellationToken);
            var outputPixels = await ReadImagePixelHashAsync(ffmpegPath, outputPath, cancellationToken);
            return sourcePixels == outputPixels;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    internal static bool ValidateCompatibleAudioOutput(MediaTask task, MediaProbeResult output)
    {
        var expectedChannels = MediaConversionOptions.FromTask(task).Channels ?? 2;
        if (!CompatibleAudioTrackPolicy.IsSupportedOutput(task) || output.AudioTracks.Count != 1 ||
            output.AudioTracks[0].Channels != expectedChannels ||
            string.Equals(output.AudioTracks[0].Codec, "apple_apac", StringComparison.OrdinalIgnoreCase)) return false;
        var hasVideo = output.VideoStreamIndex >= 0 || !string.IsNullOrWhiteSpace(output.VideoCodec);
        return FormatCatalog.IsAudioFormat(task.TargetFormat) ? !hasVideo : hasVideo;
    }

    private static async Task<string> ReadImagePixelHashAsync(string ffmpegPath, string path, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ffmpegPath,
            ["-v", "error", "-nostdin", "-i", path, "-map", "0:V:0", "-frames:v", "1", "-pix_fmt", "rgba64le",
                "-f", "hash", "-hash", "sha256", "-"], TimeSpan.FromSeconds(60), cancellationToken);
        var hash = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || hash.Length != 71 || !hash.StartsWith("SHA256=", StringComparison.Ordinal) ||
            hash[7..].Any(character => !Uri.IsHexDigit(character))) throw new InvalidDataException(LocalizationService.T("NativeImageValidationFailed"));
        return hash;
    }

    public Task<bool> ValidateOutputAsync(string ffprobePath, string outputPath, CancellationToken cancellationToken = default)
        => ValidateOutputCoreAsync(ffprobePath, outputPath, cancellationToken, null);

    internal static async Task<bool> ValidateOutputCoreAsync(string ffprobePath, string outputPath,
        CancellationToken cancellationToken, OutputProbeObservation? observation)
    {
        var validationPath = outputPath;
        var directoryOutput = Directory.Exists(outputPath);
        if (directoryOutput)
        {
            validationPath = Directory.EnumerateFiles(outputPath, "frame-*.png", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? string.Empty;
        }
        if (!File.Exists(ffprobePath))
        {
            observation?.Write(OutputProbeOutcome.ProbeUnavailable, directoryOutput);
            return false;
        }
        if (!File.Exists(validationPath))
        {
            observation?.Write(OutputProbeOutcome.ArtifactUnavailable, directoryOutput);
            return false;
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                ffprobePath,
                ["-v", "error", "-show_entries", "stream=codec_type", "-of", "default=nw=1:nk=1", validationPath],
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                observation?.Write(OutputProbeOutcome.ProbeNonzeroExit, directoryOutput, result);
                return false;
            }
            var streamLines = result.StandardOutput.Split(Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            if (streamLines == 0) observation?.Write(OutputProbeOutcome.NoStreamOutput, directoryOutput, result);
            return streamLines > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            observation?.Write(OutputProbeOutcome.ProbeException, directoryOutput, exception: exception);
            return false;
        }
    }
}
