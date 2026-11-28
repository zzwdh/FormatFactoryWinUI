using System.Globalization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public enum EncoderBackend
{
    Nvidia,
    Intel,
    Amd,
    MediaFoundation,
    Cpu
}

/// <summary>Adapts the application format catalog, probe and preservation policies to the Rust FFmpeg planner.</summary>
public sealed class PresetService
{
    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text"
    };

    public IReadOnlyList<string> BuildArguments(MediaTask task, string temporaryOutput, EncoderBackend backend,
        bool hardwareDecode = false, int cpuThreads = 0) =>
        BuildArgumentsAsync(task, temporaryOutput, backend, hardwareDecode, cpuThreads, CancellationToken.None)
            .GetAwaiter().GetResult();

    internal async Task<string[]> BuildArgumentsAsync(MediaTask task, string temporaryOutput, EncoderBackend backend,
        bool hardwareDecode, int cpuThreads, CancellationToken token, MediaConversionOptions? executionOptions = null)
    {
        var request = await DescribeAsync(task, temporaryOutput, backend, hardwareDecode, cpuThreads, token, executionOptions).ConfigureAwait(false);
        return await ConversionBackend.RequestAsync<string[]>("media-plan", request, token).ConfigureAwait(false);
    }

    internal static async Task<object> DescribeAsync(MediaTask task, string temporaryOutput, EncoderBackend backend,
        bool hardwareDecode, int cpuThreads, CancellationToken token, MediaConversionOptions? executionOptions)
    {
        token.ThrowIfCancellationRequested();
        var requested = MediaConversionOptions.FromTask(task);
        requested.Validate(task);
        var options = executionOptions ?? await MediaSizeBudget.CalculateAsync(task, requested, null, token).ConfigureAwait(false);
        if (CompatibleAudioTrackPolicy.GetValidationError(task, task.Probe) is { } error)
            throw new NotSupportedException(error);
        var animation = await ImagePreservationPolicy.ValidateFfmpegInputAsync(task, token).ConfigureAwait(false);
        if (RemuxCompatibilityService.IsRequested(task)) RemuxCompatibilityService.ValidateTarget(task);
        var descriptor = FormatCatalog.FindById(task.TargetFormatId);
        if (options.HasDimensions && descriptor?.ContentKind != ContentKind.Video && (task.Probe.Width <= 0 || task.Probe.Height <= 0))
            throw new NotSupportedException(LocalizationService.T("MediaOptionsTargetUnsupported"));

        var compatible = CompatibleAudioTrackPolicy.IsRequested(task) && CompatibleAudioTrackPolicy.IsSupportedOutput(task);
        var tracks = compatible ? CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, task.Probe) : task.Probe.AudioTracks;
        var probe = task.Probe;
        return new
        {
            action = "encode",
            input = task.InputPath,
            output = FormatCatalog.IsFrameSequence(task.TargetFormat) ? Path.Combine(temporaryOutput, "frame-%06d.png") : temporaryOutput,
            sourceKind = task.Kind.ToString(),
            target = task.TargetFormatId,
            targetKind = descriptor?.ContentKind.ToString() ?? string.Empty,
            container = FormatCatalog.ContainerForVideoTarget(task.TargetFormat),
            codec = FormatCatalog.VideoCodecForTarget(task.TargetFormat),
            mode = task.ConversionMode,
            backend = backend.ToString(),
            hardwareDecode,
            threads = cpuThreads,
            width = probe.Width,
            height = probe.Height,
            hdr = probe.IsHdr,
            animated = probe.IsAnimated,
            animationLoops = animation is { IsAnimated: true } ? animation.LoopCount : (int?)null,
            colorSpace = probe.ColorSpace,
            colorTransfer = probe.ColorTransfer,
            colorPrimaries = probe.ColorPrimaries,
            colorRange = probe.ColorRange,
            subtitles = probe.SubtitleCodecs,
            compatibleAudio = compatible,
            selectedAudio = task.Kind == MediaKind.Video ? Math.Max(0, task.SelectedAudioTrack) : 0,
            audioTracks = tracks.Select(track => new
            {
                map = CompatibleAudioTrackPolicy.GetStreamMap(track),
                channels = track.Channels,
                layout = track.ChannelLayout
            }),
            options = new
            {
                sizeLimit = requested.TargetSizeBytes.HasValue,
                videoBitrate = options.VideoBitrateKbps,
                audioBitrate = options.AudioBitrateKbps,
                channels = options.Channels,
                sampleRate = options.SampleRate,
                imageQuality = options.ImageQuality,
                maxWidth = options.MaxWidth,
                maxHeight = options.MaxHeight,
                frameRate = options.FrameRate?.ToString("0.########", CultureInfo.InvariantCulture)
            }
        };
    }

    public IReadOnlyList<string> BuildIcoFrameArguments(MediaTask task, int size, string outputPng) =>
        BuildIcoFrameArgumentsAsync(task, size, outputPng, CancellationToken.None).GetAwaiter().GetResult();

    internal Task<string[]> BuildIcoFrameArgumentsAsync(MediaTask task, int size, string outputPng, CancellationToken token) =>
        ConversionBackend.RequestAsync<string[]>("media-plan", new { action = "icon", input = task.InputPath, output = outputPng, size }, token);

    public bool WillUseHardware(MediaTask task) =>
        task.Kind == MediaKind.Video &&
        FormatCatalog.VideoCodecForTarget(task.TargetFormat) is not ("vp9" or "av1") &&
        FormatCatalog.ContainerForVideoTarget(task.TargetFormat) is "MP4" or "MKV" or "MOV" or "TS" or "FLV";

    public string GetPreservationWarning(MediaTask task)
    {
        if (CompatibleAudioTrackPolicy.IsRequested(task) && CompatibleAudioTrackPolicy.IsSupportedOutput(task))
            return LocalizationService.T("MediaCompatibilityAacWarning");
        if (task.Probe.IsHdr && task.TargetFormat is "AVI" or "FLV") return LocalizationService.T("HdrToSdr");
        if (task.Probe.SubtitleCodecs.Count > 0 && task.TargetFormat is "AVI" or "FLV") return LocalizationService.T("SubtitleIgnored");
        if (task.Probe.SubtitleCodecs.Any(codec => !TextSubtitleCodecs.Contains(codec)) && task.TargetFormat is "MP4" or "MOV")
            return LocalizationService.T("GraphicSubtitleIgnored");
        return string.Empty;
    }
}
