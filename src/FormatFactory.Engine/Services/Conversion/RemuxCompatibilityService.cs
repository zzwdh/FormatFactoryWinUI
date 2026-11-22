using System.Globalization;
using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Checks every stream that -map copies before a lossless media conversion starts.</summary>
internal static class RemuxCompatibilityService
{
    internal static bool IsRequested(MediaTask task) => task.ConversionMode == "lossless" &&
        task.Kind is MediaKind.Audio or MediaKind.Video;

    internal static async Task<IReadOnlyList<RemuxStream>> PrepareAsync(
        MediaTask task, string ffprobePath, CancellationToken cancellationToken)
    {
        try
        {
        ValidateTarget(task);
        var json = await ReadStreamsAsync(ffprobePath, task.InputPath, cancellationToken);
        var streams = ParseStreams(json);
        var copied = Validate(task, streams);
        // Compatibility is checked against the current file, not stale imported metadata.
        task.Probe = MediaProbeParser.Parse(json, task.InputPath);
        return copied;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            FormatFailureDiagnostics.Record(error, Fact(task, FormatFailureStage.RemuxProbe, FormatFailureReason.ParseFailure));
            FormatFailureDiagnostics.WriteApp(error, task.Id);
            throw;
        }
    }

    internal static void ValidateTarget(MediaTask task)
    {
        if (FormatCatalog.IsVideoCodec(task.TargetFormatId) || FormatCatalog.IsFrameSequence(task.TargetFormatId))
            throw FormatFailureDiagnostics.Attach(new NotSupportedException(LocalizationService.T("ConversionUiLosslessRouteUnsupported")),
                Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnsupportedRoute));
        var target = FormatCatalog.FindById(task.TargetFormatId);
        if (target?.ContentKind is not (ContentKind.Audio or ContentKind.Video) || !target.CanExport)
            throw FormatFailureDiagnostics.Attach(Unsupported("media streams", task.TargetFormat),
                Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnsupportedRoute));
    }

    internal static IReadOnlyList<RemuxStream> Validate(MediaTask task, IReadOnlyList<RemuxStream> streams)
    {
        ValidateTarget(task);
        if (streams.Count == 0) throw FormatFailureDiagnostics.Attach(UnknownStreams(), Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnknownStreams));
        IReadOnlyList<RemuxStream> copied = streams;
        if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            var audio = streams.Where(stream => stream.Type == "audio").ToArray();
            var index = task.Kind == MediaKind.Video ? task.SelectedAudioTrack : 0;
            if (index < 0 || index >= audio.Length) throw FormatFailureDiagnostics.Attach(UnknownStreams(),
                Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.StreamSelection) with { Index = index, Count = audio.Length });
            copied = [audio[index]];
        }

        var container = FormatCatalog.IsAudioFormat(task.TargetFormat)
            ? task.TargetFormatId : FormatCatalog.ContainerForVideoTarget(task.TargetFormat).ToLowerInvariant();
        foreach (var stream in copied)
        {
            if (string.IsNullOrWhiteSpace(stream.Type) || string.IsNullOrWhiteSpace(stream.Codec) && stream.Type != "attachment")
                throw FormatFailureDiagnostics.Attach(UnknownStreams(), Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnknownStreams) with { Count = copied.Count });
            if (!Supports(container, stream))
                throw FormatFailureDiagnostics.Attach(Unsupported($"{stream.Type}: {stream.Codec}", task.TargetFormat),
                    Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnsupportedStream) with
                    { Codec = stream.Codec, StreamType = stream.Type, Width = stream.Width, Height = stream.Height, Count = copied.Count });
        }
        // FLV has exactly one slot for each media stream; accepting several would fail or lose tracks.
        if (container == "flv" && (copied.Count(stream => stream.Type == "video") > 1 ||
                                   copied.Count(stream => stream.Type == "audio") > 1))
            throw FormatFailureDiagnostics.Attach(Unsupported("multiple audio/video streams", task.TargetFormat),
                Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.MultipleStreams) with { Count = copied.Count });
        return copied;
    }

    // These are conservative, commonly supported combinations. An unlisted codec
    // fails explicitly; it is never replaced with an encoder or silently dropped.
    private static bool Supports(string container, RemuxStream stream) => stream.Type switch
    {
        "video" => container switch
        {
            "mp4" => stream.Codec is "h264" or "hevc" or "av1" or "vp9" or "mpeg4" ||
                     stream.AttachedPicture && stream.Codec is "mjpeg" or "png",
            "mov" => stream.Codec is "h264" or "hevc" or "av1" or "mpeg4" or "mpeg2video" or
                     "mjpeg" or "prores" or "qtrle" or "dvvideo" or "png",
            "mkv" => stream.Codec is "h264" or "hevc" or "av1" or "vp8" or "vp9" or "mpeg1video" or
                     "mpeg2video" or "mpeg4" or "msmpeg4v3" or "wmv1" or "wmv2" or "wmv3" or "vc1" or
                     "theora" or "ffv1" or "huffyuv" or "mjpeg" or "prores" or "dvvideo" or "png",
            "webm" => stream.Codec is "vp8" or "vp9" or "av1",
            "ts" => stream.Codec is "mpeg1video" or "mpeg2video" or "h264" or "hevc" or "vc1",
            "avi" => stream.Codec is "h264" or "mpeg4" or "mjpeg" or "msmpeg4v3" or "wmv1" or "wmv2" or
                     "ffv1" or "huffyuv" or "dvvideo",
            "flv" => stream.Codec is "flv1" or "h264" or "vp6f" or "vp6a",
            _ => false
        },
        "audio" => container switch
        {
            "mp4" => stream.Codec is "aac" or "alac" or "mp3" or "ac3" or "eac3",
            // M4A uses FFmpeg's ipod muxer, whose codec tags differ from MP4.
            "m4a" => stream.Codec is "aac" or "alac" or "ac3",
            "mov" => stream.Codec is "aac" or "alac" or "mp3" or "ac3" or "eac3" || IsPcm(stream.Codec),
            "mkv" => stream.Codec is "aac" or "ac3" or "eac3" or "dts" or "truehd" or "mp1" or "mp2" or
                     "mp3" or "opus" or "vorbis" or "flac" or "alac" || IsPcm(stream.Codec),
            "webm" => stream.Codec is "opus" or "vorbis",
            "ts" => stream.Codec is "mp2" or "mp3" or "aac" or "ac3" or "eac3" or "dts",
            "avi" => stream.Codec is "mp2" or "mp3" or "ac3" || IsLittleEndianPcm(stream.Codec),
            "flv" => stream.Codec is "aac" or "mp3" or "speex" or "pcm_s16le" or "pcm_u8" or "adpcm_swf" or "nellymoser",
            "mp3" => stream.Codec == "mp3",
            "aac" => stream.Codec == "aac",
            "opus" => stream.Codec == "opus",
            "vorbis" => stream.Codec == "vorbis",
            "ogg" => stream.Codec is "opus" or "vorbis",
            "flac" => stream.Codec == "flac",
            "alac" => stream.Codec == "alac",
            "wav" => IsLittleEndianPcm(stream.Codec) ||
                     (stream.Codec == "adpcm_ima_wav" && stream.Channels is 1 or 2 && stream.SampleRate > 0),
            "aiff" => stream.Codec is "pcm_s8" or "pcm_s16be" or "pcm_s24be" or "pcm_s32be" or "pcm_f32be" or "pcm_f64be",
            "ac3" => stream.Codec == "ac3",
            "eac3" => stream.Codec == "eac3",
            "wma" => stream.Codec is "wmav1" or "wmav2" or "wmapro" or "wmavoice" or "wmalossless",
            _ => false
        },
        "subtitle" => container switch
        {
            "mp4" or "mov" => stream.Codec == "mov_text",
            "mkv" => stream.Codec is "subrip" or "ass" or "ssa" or "webvtt" or "dvd_subtitle" or "hdmv_pgs_subtitle" or "dvb_subtitle",
            "webm" => stream.Codec == "webvtt",
            "ts" => stream.Codec == "dvb_subtitle",
            _ => false
        },
        "attachment" => container == "mkv",
        "data" => container == "ts" && stream.Codec is "bin_data" or "klv",
        _ => false
    };

    private static bool IsLittleEndianPcm(string codec) => codec is "pcm_u8" or "pcm_s16le" or "pcm_s24le" or
        "pcm_s32le" or "pcm_f32le" or "pcm_f64le" or "pcm_alaw" or "pcm_mulaw";
    private static bool IsPcm(string codec) => IsLittleEndianPcm(codec) || codec is "pcm_s8" or "pcm_s16be" or
        "pcm_s24be" or "pcm_s32be" or "pcm_f32be" or "pcm_f64be";

    internal static async Task<bool> ValidateOutputAsync(string ffprobePath, string outputPath,
        IReadOnlyList<RemuxStream> expected, CancellationToken cancellationToken)
    {
        try
        {
            var actual = ParseStreams(await ReadStreamsAsync(ffprobePath, outputPath, cancellationToken));
            var matches = MatchesOutput(expected, actual);
            if (!matches)
            {
                var observation = new object();
                FormatFailureDiagnostics.Record(observation, new(FormatFailureStage.RemuxOutput, FormatFailureReason.OutputValidation)
                    { Count = actual.Count, Index = expected.Count });
                FormatFailureDiagnostics.WriteApp(observation);
            }
            return matches;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or TimeoutException)
        {
            FormatFailureDiagnostics.Record(exception, new(FormatFailureStage.RemuxOutput, FormatFailureReason.ParseFailure) { Count = expected.Count });
            FormatFailureDiagnostics.WriteApp(exception);
            return false;
        }
    }

    internal static bool MatchesOutput(IReadOnlyList<RemuxStream> expected, IReadOnlyList<RemuxStream> actual)
    {
        // Container changes can reorder tracks and alter their codec tags. Media
        // types, codecs, dimensions, sample rates and channel counts must survive.
        var remaining = actual.ToList();
        foreach (var source in expected)
        {
            var index = remaining.FindIndex(output => source.Type == output.Type && source.Codec == output.Codec &&
                source.Width == output.Width && source.Height == output.Height &&
                source.SampleRate == output.SampleRate && source.Channels == output.Channels);
            if (index < 0) return false;
            remaining.RemoveAt(index);
        }
        return expected.Count > 0 && remaining.Count == 0;
    }

    internal static IReadOnlyList<RemuxStream> ParseStreams(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw UnknownStreams();
        return streams.EnumerateArray().Select(stream => new RemuxStream(
            Text(stream, "codec_type"), Text(stream, "codec_name"),
            Number(stream, "width"), Number(stream, "height"), Number(stream, "sample_rate"), Number(stream, "channels"),
            stream.TryGetProperty("disposition", out var disposition) && Number(disposition, "attached_pic") != 0)).ToArray();
    }

    private static string Text(JsonElement stream, string key) => stream.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).ToLowerInvariant() : string.Empty;
    private static int Number(JsonElement stream, string key) => stream.TryGetProperty(key, out var value) &&
        int.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static async Task<string> ReadStreamsAsync(string ffprobePath, string inputPath, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", inputPath],
            TimeSpan.FromSeconds(30), cancellationToken);
        if (result.ExitCode != 0) throw UnknownStreams();
        return result.StandardOutput;
    }

    private static NotSupportedException Unsupported(string stream, string target) =>
        new(LocalizationService.T("RemuxUnsupportedStream", stream, target));
    private static InvalidDataException UnknownStreams() => new(LocalizationService.T("RemuxUnknownStreams"));
    private static FormatFailureFact Fact(MediaTask task, FormatFailureStage stage, FormatFailureReason reason) =>
        new(stage, reason) { Source = task.SourceFormatId, Target = task.TargetFormatId, Lossless = task.ConversionMode == "lossless" };
}

internal sealed record RemuxStream(string Type, string Codec, int Width = 0, int Height = 0,
    int SampleRate = 0, int Channels = 0, bool AttachedPicture = false);
