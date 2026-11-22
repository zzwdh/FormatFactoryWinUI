using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed record FfmpegCapabilitySnapshot(IReadOnlySet<string> Muxers)
{
    public IReadOnlySet<string> Decoders { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> DecoderCodecIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> Encoders { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool HasMuxer(string name) => Muxers.Contains(name);
    public bool HasDecoder(string codec) => !string.IsNullOrWhiteSpace(codec) &&
        (Decoders.Contains(codec) || DecoderCodecIds.Contains(codec));
    public bool HasEncoder(string name) => Encoders.Contains(name);
}

public sealed class FfmpegCapabilityService
{
    private static readonly IReadOnlyDictionary<string, string> OutputMuxers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MP4"] = "mp4", ["H264"] = "mp4", ["HEVC"] = "mp4", ["M4A"] = "ipod", ["ALAC"] = "ipod", ["AAC"] = "adts", ["MKV"] = "matroska",
        ["WEBM"] = "webm", ["VP9"] = "webm", ["AV1"] = "webm", ["MOV"] = "mov", ["TS"] = "mpegts", ["AVI"] = "avi", ["FLV"] = "flv",
        ["MP3"] = "mp3", ["OPUS"] = "opus", ["VORBIS"] = "ogg", ["OGG"] = "ogg", ["FLAC"] = "flac", ["WAV"] = "wav",
        ["AIFF"] = "aiff", ["AC3"] = "ac3", ["EAC3"] = "eac3", ["WMA"] = "asf",
        ["FRAMES"] = "image2", ["JPG"] = "image2", ["PNG"] = "image2", ["ICO"] = "image2", ["BMP"] = "image2",
        ["WEBP"] = "image2", ["AVIF"] = "image2", ["GIF"] = "gif", ["TIFF"] = "image2", ["TGA"] = "image2"
    };
    private readonly ConcurrentDictionary<string, Lazy<Task<FfmpegCapabilitySnapshot>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<FfmpegCapabilitySnapshot> ProbeAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(ffmpegPath);
        var task = _cache.GetOrAdd(fullPath, CreateProbe).Value;

        return task.WaitAsync(cancellationToken);
    }

    public static IReadOnlyList<string> GetMissingOutputComponents(FfmpegCapabilitySnapshot snapshot, string targetFormat)
    {
        if (!OutputMuxers.TryGetValue(targetFormat, out var muxer) || snapshot.HasMuxer(muxer))
        {
            return [];
        }

        return [muxer];
    }

    internal static IReadOnlyList<string> GetMissingOutputComponents(FfmpegCapabilitySnapshot snapshot,
        MediaTask task, MediaProbeResult probe)
    {
        var missing = GetMissingOutputComponents(snapshot, task.TargetFormat).Select(name => "muxer: " + name).ToList();
        if (RemuxCompatibilityService.IsRequested(task)) return missing;
        foreach (var encoder in RequiredSoftwareEncoders(task, probe))
            if (!snapshot.HasEncoder(encoder)) missing.Add("encoder: " + encoder);
        return missing;
    }

    private static IEnumerable<string> RequiredSoftwareEncoders(MediaTask task, MediaProbeResult probe)
    {
        var target = task.TargetFormat;
        if (FormatCatalog.IsAudioFormat(target))
        {
            yield return target switch
            {
                "MP3" => "libmp3lame", "WMA" => "wmav2", "AAC" or "M4A" => "aac",
                "FLAC" => "flac", "WAV" => "pcm_s16le", "AIFF" => "pcm_s16be", "OPUS" => "libopus",
                "VORBIS" or "OGG" => "libvorbis", "ALAC" => "alac", "AC3" => "ac3", "EAC3" => "eac3",
                _ => target.ToLowerInvariant()
            };
            yield break;
        }
        if (FormatCatalog.IsFrameSequence(target) || target == "ICO") { yield return "png"; yield break; }
        if (FormatCatalog.IsImageFormat(target))
        {
            yield return target switch
            {
                "JPG" => task.ConversionMode == "lossless" ? "ljpeg" : "mjpeg",
                "PNG" => "png", "BMP" => "bmp", "WEBP" => probe.IsAnimated ? "libwebp_anim" : "libwebp",
                "AVIF" => "libaom-av1", "GIF" => "gif", "TIFF" => "tiff", "TGA" => "targa",
                _ => target.ToLowerInvariant()
            };
            yield break;
        }
        var container = FormatCatalog.ContainerForVideoTarget(target);
        var codec = FormatCatalog.VideoCodecForTarget(target);
        yield return container == "AVI" ? "mpeg4"
            : codec == "av1" ? "libaom-av1" : codec == "vp9" || container == "WEBM" ? "libvpx-vp9"
            : codec == "hevc" || codec is null && probe.IsHdr && container is "MP4" or "MKV" or "MOV"
                ? "libx265" : "libx264";
        if (probe.AudioTracks.Count > 0)
            yield return container == "AVI" ? "libmp3lame" : container == "WEBM" ? "libopus" : "aac";
        if (container is "MP4" or "MOV" && probe.SubtitleCodecs.Count > 0 && probe.SubtitleCodecs.All(codecName =>
            codecName is "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text"))
            yield return "mov_text";
    }
    private Lazy<Task<FfmpegCapabilitySnapshot>> CreateProbe(string path)
    {
        Lazy<Task<FfmpegCapabilitySnapshot>>? owner = null;
        owner = new Lazy<Task<FfmpegCapabilitySnapshot>>(
            () => ProbeAndEvictOnFailureAsync(path, owner!),
            LazyThreadSafetyMode.ExecutionAndPublication);
        return owner;
    }

    private async Task<FfmpegCapabilitySnapshot> ProbeAndEvictOnFailureAsync(
        string path,
        Lazy<Task<FfmpegCapabilitySnapshot>> owner)
    {
        try
        {
            return await ProbeCoreAsync(path);
        }
        catch
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<FfmpegCapabilitySnapshot>>>>)_cache)
                .Remove(new KeyValuePair<string, Lazy<Task<FfmpegCapabilitySnapshot>>>(path, owner));
            throw;
        }
    }

    private static async Task<FfmpegCapabilitySnapshot> ProbeCoreAsync(string ffmpegPath)
    {
        var formats = RunAsync(ffmpegPath, "-hide_banner", "-formats");
        var decoders = RunAsync(ffmpegPath, "-hide_banner", "-decoders");
        var encoders = RunAsync(ffmpegPath, "-hide_banner", "-encoders");
        await Task.WhenAll(formats, decoders, encoders);
        var decoderRows = ParseCodecs(await decoders);
        return new FfmpegCapabilitySnapshot(ParseMuxers(await formats))
        {
            Decoders = decoderRows.Names, DecoderCodecIds = decoderRows.CodecIds,
            Encoders = ParseCodecs(await encoders).Names
        };
    }

    private static async Task<string> RunAsync(string executable, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(executable, arguments, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
            ? LocalizationService.T("GeneralServiceFfmpegCommandFailed", string.Join(' ', arguments))
                : result.StandardError.Trim());
        }

        return result.StandardOutput;
    }

    internal static HashSet<string> ParseMuxers(string text)
    {
        var muxers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2 || columns[0].Length is < 1 or > 2 || columns[0].Any(character => character is not ('D' or 'E')))
            {
                continue;
            }

            var name = columns[1];
            if (columns[0].Contains('E'))
            {
                foreach (var alias in name.Split(',', StringSplitOptions.RemoveEmptyEntries)) muxers.Add(alias);
            }
        }

        return muxers;
    }

    internal static (IReadOnlySet<string> Names, IReadOnlySet<string> CodecIds) ParseCodecs(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codecIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2 || columns[0].Length != 6 || columns[0][0] is not ('V' or 'A' or 'S') ||
                columns[0].Any(character => !"VAS.FSDTIBX".Contains(character)) || columns[1] == "=") continue;
            names.Add(columns[1]);
            if (columns.Length == 3 && Regex.Match(columns[2], @"\(codec\s+([a-zA-Z0-9_]+)\)") is { Success: true } alias)
                codecIds.Add(alias.Groups[1].Value);
        }
        return (names, codecIds);
    }
}
