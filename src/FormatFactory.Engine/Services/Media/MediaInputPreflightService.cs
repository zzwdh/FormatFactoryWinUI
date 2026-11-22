using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public enum MediaInputFailureKind
{
    None, UnreadableInput, UnsupportedDecoder, MissingOutputComponent, InvalidMedia, EngineUnavailable, DecodeTimedOut, AudioSelectionUnavailable
}

public sealed record MediaInputPreflightResult(bool Success, string ExecutablePath, MediaProbeResult? Probe,
    MediaInputFailureKind FailureKind, string Detail, bool UsedFallback)
{
    internal MediaInputProbeDiagnostic? ProbeDiagnostic { get; init; }
    internal Guid AttemptId { get; init; }
    internal bool CandidateFallback { get; init; }
    internal string? ObservedCandidatePath { get; init; }
    internal MediaPreflightPhase Phase { get; init; }
    internal MediaDecodeObservation? DecodeObservation { get; init; }
}

internal enum MediaPreflightPhase { Unknown, Probe, AudioSelection, Capability, StreamSelection, Decoder, OutputComponent, Decode, FallbackValidation }

internal enum MediaInputProbeExceptionKind
{
    IOException, FileNotFoundException, DirectoryNotFoundException, DriveNotFoundException, PathTooLongException,
    EndOfStreamException, InvalidDataException, JsonException, TimeoutException, Win32Exception
}

internal sealed record MediaInputProbeDiagnostic(bool FallbackCandidate, MediaInputProbeExceptionKind ExceptionKind,
    int HResult, int? NativeErrorCode, MediaProbeFailureDetail? ProbeDetail = null)
{
    internal Guid AttemptId { get; init; }
}

internal sealed record MediaDecodeCheckResult(bool Success, string Detail)
{
    internal MediaDecodeObservation? Observation { get; init; }
}
internal sealed record MediaDecodeObservation(int? ExitCode, long? ElapsedMs, int? OutputCharacters, int? ErrorCharacters,
    bool? Truncated, long? LastFrame, long? LastOutputTimeUs, Guid? ProcessOperation = null);
internal sealed record MediaDecodeStream(string Map, string Codec, string CodecTag);

/// <summary>Checks the actual streams before reserving an output or launching a full conversion.</summary>
public sealed class MediaInputPreflightService
{
    private readonly Func<string, string, CancellationToken, Task<MediaProbeResult>> _probe;
    private readonly Func<string, CancellationToken, Task<FfmpegCapabilitySnapshot>> _capabilities;
    private readonly Func<string, MediaTask, MediaProbeResult, CancellationToken, Task<MediaDecodeCheckResult>> _decode;
    private readonly Func<string, CancellationToken, Task<bool>> _validateFallback;

    public MediaInputPreflightService()
    {
        var mediaProbe = new MediaProbeService();
        var capabilities = new FfmpegCapabilityService();
        _probe = mediaProbe.ProbeAsync;
        _capabilities = capabilities.ProbeAsync;
        _decode = DecodeShortSampleAsync;
        _validateFallback = ValidateFallbackPairAsync;
    }

    internal MediaInputPreflightService(
        Func<string, string, CancellationToken, Task<MediaProbeResult>> probe,
        Func<string, CancellationToken, Task<FfmpegCapabilitySnapshot>> capabilities,
        Func<string, MediaTask, MediaProbeResult, CancellationToken, Task<MediaDecodeCheckResult>> decode,
        Func<string, CancellationToken, Task<bool>> validateFallback)
    {
        _probe = probe; _capabilities = capabilities; _decode = decode; _validateFallback = validateFallback;
    }

    public async Task<MediaInputPreflightResult> SelectAsync(string ffmpegPath, string? fallbackFfmpegPath,
        MediaTask task, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        var attemptId = Guid.NewGuid();
        var primaryPath = FfmpegLocator.ResolveSymbolicLink(ffmpegPath);
        var primary = await CheckCandidateAsync(primaryPath, task, false, attemptId, cancellationToken);
        if (primary.Success || primary.FailureKind != MediaInputFailureKind.UnsupportedDecoder ||
            string.IsNullOrWhiteSpace(fallbackFfmpegPath)) return primary;

        string fallbackPath;
        try { fallbackPath = FfmpegLocator.ResolveSymbolicLink(Path.GetFullPath(fallbackFfmpegPath)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        { return Failure(MediaInputFailureKind.EngineUnavailable, exception.Message, primary.Probe)
            with { AttemptId = attemptId, CandidateFallback = true, Phase = MediaPreflightPhase.FallbackValidation }; }
        if (string.Equals(primaryPath, fallbackPath, StringComparison.OrdinalIgnoreCase)) return primary;
        try
        {
            if (!await _validateFallback(fallbackPath, cancellationToken))
                return Failure(MediaInputFailureKind.EngineUnavailable,
                    "The configured compatibility engine requires a runnable matching FFmpeg/FFprobe pair.", primary.Probe)
                    with { AttemptId = attemptId, CandidateFallback = true, ObservedCandidatePath = fallbackPath, Phase = MediaPreflightPhase.FallbackValidation };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or Win32Exception or TimeoutException or ArgumentException)
        { return Failure(MediaInputFailureKind.EngineUnavailable, exception.Message, primary.Probe)
            with { AttemptId = attemptId, CandidateFallback = true, ObservedCandidatePath = fallbackPath, Phase = MediaPreflightPhase.FallbackValidation }; }
        return await CheckCandidateAsync(fallbackPath, task, true, attemptId, cancellationToken);
    }

    private async Task<MediaInputPreflightResult> CheckCandidateAsync(string executable, MediaTask task,
        bool fallback, Guid attemptId, CancellationToken cancellationToken)
    {
        var result = await CheckCandidateCoreAsync(executable, task, fallback, attemptId, cancellationToken);
        return result with { AttemptId = attemptId, CandidateFallback = fallback, ObservedCandidatePath = executable };
    }

    private async Task<MediaInputPreflightResult> CheckCandidateCoreAsync(string executable, MediaTask task,
        bool fallback, Guid attemptId, CancellationToken cancellationToken)
    {
        MediaProbeResult probe;
        try { probe = await _probe(FfmpegLocator.GetFfprobePath(executable), task.InputPath, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or TimeoutException or Win32Exception)
        {
            return Failure(MediaInputFailureKind.UnreadableInput, exception.Message) with
            { ProbeDiagnostic = DescribeProbeException(exception, fallback) with { AttemptId = attemptId }, Phase = MediaPreflightPhase.Probe };
        }
        if (CompatibleAudioTrackPolicy.GetValidationError(task, probe) is { } selectionError)
            return Failure(MediaInputFailureKind.AudioSelectionUnavailable, selectionError, probe) with { Phase = MediaPreflightPhase.AudioSelection };

        FfmpegCapabilitySnapshot capabilities;
        try { capabilities = await _capabilities(executable, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or Win32Exception)
        { return Failure(MediaInputFailureKind.EngineUnavailable, exception.Message, probe) with { Phase = MediaPreflightPhase.Capability }; }

        // Stream copy never requires an input decoder or an output encoder.
        if (task.ConversionMode == "lossless" && probe.Kind is MediaKind.Audio or MediaKind.Video)
        {
            var missingMuxers = FfmpegCapabilityService.GetMissingOutputComponents(capabilities, task.TargetFormat);
            return missingMuxers.Count > 0
                ? Failure(MediaInputFailureKind.MissingOutputComponent, string.Join(", ", missingMuxers), probe) with { Phase = MediaPreflightPhase.OutputComponent }
                : new(true, executable, probe, MediaInputFailureKind.None, string.Empty, fallback);
        }

        IReadOnlyList<MediaDecodeStream> streams;
        try { streams = RequiredStreams(task, probe); }
        catch (InvalidDataException exception) { return Failure(MediaInputFailureKind.InvalidMedia, exception.Message, probe) with { Phase = MediaPreflightPhase.StreamSelection }; }
        var missingDecoders = streams.Where(stream => !capabilities.HasDecoder(stream.Codec)).ToArray();
        if (missingDecoders.Length > 0)
            return Failure(MediaInputFailureKind.UnsupportedDecoder, string.Join(", ", missingDecoders.Select(stream =>
                $"codec: {(string.IsNullOrWhiteSpace(stream.Codec) ? "unknown" : stream.Codec)}, tag: {stream.CodecTag}")), probe) with { Phase = MediaPreflightPhase.Decoder };
        var missingOutput = fallback ? MissingIntermediateComponents(capabilities, task, probe)
            : FfmpegCapabilityService.GetMissingOutputComponents(capabilities, task, probe);
        if (missingOutput.Count > 0)
            return Failure(MediaInputFailureKind.MissingOutputComponent, string.Join(", ", missingOutput), probe) with { Phase = MediaPreflightPhase.OutputComponent };

        // Static images keep their existing codec/preservation validation without an extra decode process.
        if (probe.Kind is MediaKind.Audio or MediaKind.Video)
        {
            try
            {
                var decoded = await _decode(executable, task, probe, cancellationToken);
                if (!decoded.Success)
                    return Failure(ReportsMissingDecoder(decoded.Detail) ? MediaInputFailureKind.UnsupportedDecoder
                        : MediaInputFailureKind.InvalidMedia, decoded.Detail, probe) with
                    { Phase = MediaPreflightPhase.Decode, DecodeObservation = decoded.Observation };
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException exception)
            {
                var process = RuntimeFailureDiagnostics.Get(exception);
                return Failure(MediaInputFailureKind.DecodeTimedOut, exception.Message, probe) with
                {
                    Phase = MediaPreflightPhase.Decode,
                    DecodeObservation = new(null, process?.ElapsedMs, null, null, null, null, null, process?.Operation)
                };
            }
            catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
            { return Failure(MediaInputFailureKind.EngineUnavailable, exception.Message, probe) with { Phase = MediaPreflightPhase.Decode }; }
        }
        return new(true, executable, probe, MediaInputFailureKind.None, string.Empty, fallback);
    }

    internal static IReadOnlyList<MediaDecodeStream> RequiredStreams(MediaTask task, MediaProbeResult probe)
    {
        if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            return CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, probe).Select(AudioStream).ToArray();
        }
        if (probe.VideoStreamIndex < 0 && string.IsNullOrWhiteSpace(probe.VideoCodec))
            throw new InvalidDataException("The current file has no video or image stream for this output.");
        var result = new List<MediaDecodeStream>
        {
            new(probe.VideoStreamIndex >= 0 ? $"0:{probe.VideoStreamIndex}" : "0:V:0", probe.VideoCodec, probe.VideoCodecTag)
        };
        if (!FormatCatalog.IsImageFormat(task.TargetFormat) && !FormatCatalog.IsFrameSequence(task.TargetFormat))
            result.AddRange(CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, probe).Select(AudioStream));
        return result;
    }

    private static MediaDecodeStream AudioStream(AudioTrackInfo track) => new(
        CompatibleAudioTrackPolicy.GetStreamMap(track), track.Codec, track.CodecTag);

    internal static IReadOnlyList<string> MissingIntermediateComponents(FfmpegCapabilitySnapshot capabilities,
        MediaTask task, MediaProbeResult probe)
    {
        var missing = new List<string>();
        if (!capabilities.HasMuxer("matroska")) missing.Add("muxer: matroska");
        if (!FormatCatalog.IsAudioFormat(task.TargetFormat) && !capabilities.HasEncoder("ffv1")) missing.Add("encoder: ffv1");
        var audioNeeded = FormatCatalog.IsAudioFormat(task.TargetFormat) ||
            !FormatCatalog.IsImageFormat(task.TargetFormat) && !FormatCatalog.IsFrameSequence(task.TargetFormat) && CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, probe).Count > 0;
        if (audioNeeded && !capabilities.HasEncoder("pcm_s32le")) missing.Add("encoder: pcm_s32le");
        return missing;
    }

    internal static IReadOnlyList<string> BuildDecodeArguments(MediaTask task, MediaProbeResult probe)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-i", task.InputPath
        };
        foreach (var stream in RequiredStreams(task, probe)) arguments.AddRange(["-map", stream.Map]);
        arguments.AddRange(["-t", "2", "-threads", "1", "-filter_threads", "1", "-filter_complex_threads", "1",
            "-progress", "pipe:1", "-nostats", "-f", "null", "-"]);
        return arguments;
    }

    private static async Task<MediaDecodeCheckResult> DecodeShortSampleAsync(string executable, MediaTask task,
        MediaProbeResult probe, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await ProcessRunner.RunAsync(executable, BuildDecodeArguments(task, probe), TimeSpan.FromSeconds(20),
            new ProcessRunOptions { CaptureCompleteOutput = false, MaximumOutputBytes = 8192, MaximumErrorBytes = 8192 }, cancellationToken);
        if (result.ExitCode != 0)
            return new(false, string.IsNullOrWhiteSpace(result.StandardError)
                ? "The input sample could not be decoded." : LimitDetail(result.StandardError.Trim()))
            { Observation = DescribeDecode(result, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds) };
        var producedMedia = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line =>
        {
            var separator = line.IndexOf('=');
            return separator > 0 && (line[..separator] is "frame" or "out_time_us") &&
                long.TryParse(line[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0;
        });
        return producedMedia ? new(true, string.Empty) : new(false, "The input sample contains no decodable media frames or audio samples.")
        { Observation = DescribeDecode(result, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds) };
    }

    internal static MediaDecodeObservation DescribeDecode(ProcessExecutionResult result, long elapsedMs)
    {
        long? frame = null;
        long? outputTime = null;
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || !long.TryParse(line.AsSpan(separator + 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var value) || value < 0) continue;
            if (line.AsSpan(0, separator).SequenceEqual("frame")) frame = value;
            else if (line.AsSpan(0, separator).SequenceEqual("out_time_us")) outputTime = value;
        }
        return new(result.ExitCode, elapsedMs, result.StandardOutput.Length, result.StandardError.Length,
            result.OutputTruncated, frame, outputTime);
    }

    private static async Task<bool> ValidateFallbackPairAsync(string path, CancellationToken cancellationToken)
    {
        var pairedProbe = FfmpegLocator.GetFfprobePath(path);
        if (!PortableExecutableInspector.TryReadArchitecture(path, out var engineArchitecture) ||
            !PortableExecutableInspector.TryReadArchitecture(pairedProbe, out var probeArchitecture) ||
            engineArchitecture != probeArchitecture ||
            !PortableExecutableInspector.IsCompatible(engineArchitecture, RuntimeInformation.ProcessArchitecture,
                x64EmulationAvailable: !PortableExecutableInspector.IsX64EmulationUnavailable)) return false;
        var engine = ProcessRunner.RunAsync(path, ["-version"], TimeSpan.FromSeconds(5), cancellationToken);
        var probe = ProcessRunner.RunAsync(pairedProbe, ["-version"], TimeSpan.FromSeconds(5), cancellationToken);
        await Task.WhenAll(engine, probe);
        return (await engine).ExitCode == 0 && (await probe).ExitCode == 0;
    }

    private static MediaInputProbeDiagnostic DescribeProbeException(Exception exception, bool fallback) => new(
        fallback, exception switch
        {
            FileNotFoundException => MediaInputProbeExceptionKind.FileNotFoundException,
            DirectoryNotFoundException => MediaInputProbeExceptionKind.DirectoryNotFoundException,
            DriveNotFoundException => MediaInputProbeExceptionKind.DriveNotFoundException,
            PathTooLongException => MediaInputProbeExceptionKind.PathTooLongException,
            EndOfStreamException => MediaInputProbeExceptionKind.EndOfStreamException,
            InvalidDataException => MediaInputProbeExceptionKind.InvalidDataException,
            JsonException => MediaInputProbeExceptionKind.JsonException,
            TimeoutException => MediaInputProbeExceptionKind.TimeoutException,
            Win32Exception => MediaInputProbeExceptionKind.Win32Exception,
            _ => MediaInputProbeExceptionKind.IOException
        }, exception.HResult, exception is Win32Exception win32 ? win32.NativeErrorCode : null, MediaProbeFailureDetails.Get(exception));

    private static bool ReportsMissingDecoder(string detail) => detail.Contains("no decoder found", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("Unknown decoder", StringComparison.OrdinalIgnoreCase) || detail.Contains("Decoder not found", StringComparison.OrdinalIgnoreCase);

    private static string LimitDetail(string detail)
    {
        if (detail.Length <= 2048) return detail;
        var length = char.IsHighSurrogate(detail[2047]) ? 2047 : 2048;
        return detail[..length];
    }

    private static MediaInputPreflightResult Failure(MediaInputFailureKind kind, string detail, MediaProbeResult? probe = null) =>
        new(false, string.Empty, probe, kind, LimitDetail(detail), false);
}
