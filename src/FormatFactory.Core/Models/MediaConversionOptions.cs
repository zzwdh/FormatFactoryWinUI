using System.Globalization;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Models;

/// <summary>Validated, portable media choices; absent values retain the existing preset behavior.</summary>
public sealed record MediaConversionOptions
{
    public const string TargetSizeKey = "media.targetSizeBytes";
    public const string MaxWidthKey = "media.maxWidth";
    public const string MaxHeightKey = "media.maxHeight";
    public const string FrameRateKey = "media.frameRate";
    public const string VideoBitrateKey = "media.videoBitrateKbps";
    public const string AudioBitrateKey = "media.audioBitrateKbps";
    public const string SampleRateKey = "media.sampleRate";
    public const string ChannelsKey = "media.channels";
    public const string ImageQualityKey = "image.quality";

    public static IReadOnlySet<string> OptionNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        TargetSizeKey, MaxWidthKey, MaxHeightKey, FrameRateKey, VideoBitrateKey,
        AudioBitrateKey, SampleRateKey, ChannelsKey, ImageQualityKey
    };

    public long? TargetSizeBytes { get; init; }
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }
    public double? FrameRate { get; init; }
    public int? VideoBitrateKbps { get; init; }
    public int? AudioBitrateKbps { get; init; }
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public int? ImageQuality { get; init; }
    public bool HasDimensions => MaxWidth.HasValue || MaxHeight.HasValue;
    public bool HasVideoFilters => HasDimensions || FrameRate.HasValue;
    public bool HasAny => TargetSizeBytes.HasValue || HasVideoFilters || VideoBitrateKbps.HasValue ||
        AudioBitrateKbps.HasValue || SampleRate.HasValue || Channels.HasValue || ImageQuality.HasValue;

    public static MediaConversionOptions FromTask(MediaTask task) => Parse(task.Options);

    public static MediaConversionOptions Parse(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string? Read(string key)
        {
            var matches = values.Where(pair => key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length != 1 || matches[0].Value is null) throw InvalidValue(key);
            return matches[0].Value;
        }
        long? Integer(string key, long minimum, long maximum)
        {
            var value = Read(key);
            if (value is null) return null;
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
                number < minimum || number > maximum) throw InvalidValue(key);
            return number;
        }
        double? Rate()
        {
            var value = Read(FrameRateKey);
            if (value is null) return null;
            if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
                !double.IsFinite(number) || number is < 1 or > 240) throw InvalidValue(FrameRateKey);
            return number;
        }
        var options = new MediaConversionOptions
        {
            TargetSizeBytes = Integer(TargetSizeKey, 1, 1L << 40),
            MaxWidth = (int?)Integer(MaxWidthKey, 1, 16384),
            MaxHeight = (int?)Integer(MaxHeightKey, 1, 16384),
            FrameRate = Rate(),
            VideoBitrateKbps = (int?)Integer(VideoBitrateKey, 1, 200000),
            AudioBitrateKbps = (int?)Integer(AudioBitrateKey, 8, 1536),
            SampleRate = (int?)Integer(SampleRateKey, 8000, 192000),
            Channels = (int?)Integer(ChannelsKey, 1, 2),
            ImageQuality = (int?)Integer(ImageQualityKey, 1, 100)
        };
        return options;
    }

    public Dictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, object? value)
        {
            if (value is IFormattable formatted) result[key] = formatted.ToString(null, CultureInfo.InvariantCulture);
        }
        Add(TargetSizeKey, TargetSizeBytes); Add(MaxWidthKey, MaxWidth); Add(MaxHeightKey, MaxHeight);
        Add(FrameRateKey, FrameRate); Add(VideoBitrateKey, VideoBitrateKbps); Add(AudioBitrateKey, AudioBitrateKbps);
        Add(SampleRateKey, SampleRate); Add(ChannelsKey, Channels); Add(ImageQualityKey, ImageQuality);
        // Validate values created in code as well as values read from persistence.
        _ = Parse(result);
        return result;
    }

    public void ApplyTo(IDictionary<string, string> values)
    {
        var encoded = ToDictionary();
        foreach (var key in values.Keys.Where(OptionNames.Contains).ToArray()) values.Remove(key);
        foreach (var pair in encoded) values[pair.Key] = pair.Value;
    }

    public static bool SupportsTargetSize(string targetFormatId) => FormatCatalog.NormalizeFormatId(targetFormatId) is
        "mp4" or "mkv" or "mov" or "webm" or "h264" or "hevc" or "vp9" or "av1" or
        "mp3" or "m4a" or "aac" or "opus" or "vorbis" or "ogg" or "ac3" or "eac3" or "wma" or
        "jpg" or "webp" or "avif";

    public void Validate(MediaTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = ToDictionary();
        if (!HasAny) return;
        if (task.ConversionMode.Equals("lossless", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(LocalizationService.T("MediaOptionsLosslessConflict"));
        var target = FormatCatalog.FindById(task.TargetFormatId);
        var image = task.Kind == MediaKind.Image && target?.ContentKind == ContentKind.Image;
        var video = task.Kind == MediaKind.Video && target?.ContentKind == ContentKind.Video;
        var audio = task.Kind is MediaKind.Video or MediaKind.Audio && target?.ContentKind == ContentKind.Audio;
        if (!image && !video && !audio || target?.CanExport != true || task.TargetFormatId == "frames" ||
            task.TargetFormatId == "ico") throw Unsupported();
        if (image && (task.Probe.IsAnimated || task.Probe.PageCount > 1 || task.InputPaths.Count > 1))
            throw new NotSupportedException(LocalizationService.T(task.Probe.IsAnimated
                ? "NativeImageAnimationUnsupported" : "NativeImageMultiplePages"));
        if ((FrameRate.HasValue || VideoBitrateKbps.HasValue) && !video ||
            (AudioBitrateKbps.HasValue || SampleRate.HasValue || Channels.HasValue) && !audio && !video ||
            HasDimensions && !video && !image || ImageQuality.HasValue && !image ||
            image && ImageQuality.HasValue && task.TargetFormatId is not ("jpg" or "webp" or "avif") ||
            video && (MaxWidth is 1 || MaxHeight is 1)) throw Unsupported();
        if (AudioBitrateKbps.HasValue && audio && task.TargetFormatId is "flac" or "alac" or "wav" or "aiff")
            throw Unsupported();
        // Reject unsupported explicit AAC rates rather than silently changing a requested rate.
        // Absent values keep FFmpeg's negotiation; other codecs retain their existing validation.
        if (SampleRate is { } sampleRate &&
            (audio && task.TargetFormatId is ("aac" or "m4a") ||
             video && task.Probe.AudioTracks.Count > 0 &&
             FormatCatalog.ContainerForVideoTarget(task.TargetFormat) is ("MP4" or "MKV" or "MOV" or "TS" or "FLV")) &&
            sampleRate is not (8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000 or 64000 or 88200 or 96000))
            throw InvalidValue(SampleRateKey);
        // MP3 output and AVI audio use libmp3lame, whose supported rates differ from AAC.
        // Keep an absent rate negotiable and never round an explicitly requested value.
        if (SampleRate is { } mp3SampleRate &&
            (audio && task.TargetFormatId == "mp3" ||
             video && task.Probe.AudioTracks.Count > 0 &&
             FormatCatalog.ContainerForVideoTarget(task.TargetFormat) == "AVI") &&
            mp3SampleRate is not (8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000))
            throw InvalidValue(SampleRateKey);
        // libopus accepts only these explicit input rates; leave absent rates negotiable.
        if (SampleRate is { } opusSampleRate &&
            (audio && task.TargetFormatId == "opus" ||
             video && task.Probe.AudioTracks.Count > 0 &&
             FormatCatalog.ContainerForVideoTarget(task.TargetFormat) == "WEBM") &&
            opusSampleRate is not (8000 or 12000 or 16000 or 24000 or 48000))
            throw InvalidValue(SampleRateKey);
        if (TargetSizeBytes.HasValue)
        {
            if (!SupportsTargetSize(task.TargetFormatId)) throw Unsupported();
            if (VideoBitrateKbps.HasValue || audio && AudioBitrateKbps.HasValue || ImageQuality.HasValue)
                throw new InvalidDataException(LocalizationService.T("MediaOptionsSizeBitrateConflict"));
        }
    }

    private static InvalidDataException InvalidValue(string key) =>
        new(LocalizationService.T("MediaOptionsInvalidValue", LocalizationService.T(key switch
        {
            TargetSizeKey => "MediaMaximumSize", MaxWidthKey => "MediaMaxWidth", MaxHeightKey => "MediaMaxHeight",
            FrameRateKey => "MediaFrameRate", VideoBitrateKey => "MediaVideoBitrate", AudioBitrateKey => "MediaAudioBitrate",
            SampleRateKey => "MediaSampleRate", ChannelsKey => "MediaChannels", ImageQualityKey => "MediaImageQuality", _ => key
        })));
    private static NotSupportedException Unsupported() =>
        new(LocalizationService.T("MediaOptionsTargetUnsupported"));

    public (int Width, int Height) ResolveDimensions(int sourceWidth, int sourceHeight, bool video = false)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw Unsupported();
        var ratio = Math.Min(1d, Math.Min(MaxWidth.HasValue ? (double)MaxWidth.Value / sourceWidth : 1,
            MaxHeight.HasValue ? (double)MaxHeight.Value / sourceHeight : 1));
        var width = Math.Max(1, (int)Math.Floor(sourceWidth * ratio));
        var height = Math.Max(1, (int)Math.Floor(sourceHeight * ratio));
        if (video)
        {
            width -= width % 2;
            height -= height % 2;
            if (width < 2 || height < 2) throw Unsupported();
        }
        return (width, height);
    }
}
