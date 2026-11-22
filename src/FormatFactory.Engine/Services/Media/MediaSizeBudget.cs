using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal static class MediaSizeBudget
{
    internal const int MaximumMediaAttempts = 3;
    internal const int MaximumImageAttempts = 9;

    public static MediaConversionOptions Initial(MediaTask task, MediaConversionOptions options)
    {
        if (options.TargetSizeBytes is not { } limit || task.Kind == MediaKind.Image) return options;
        var duration = task.Probe.Duration.TotalSeconds;
        if (!double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException(LocalizationService.T("MediaSizeDurationRequired"));
        var budgetKbps = Math.Floor(limit * 0.95 * 8 / duration / 1000);
        if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            var audio = SupportedAudioBitrate(task.TargetFormatId, budgetKbps);
            if (audio < 8) throw CannotMeet();
            return options with { AudioBitrateKbps = audio };
        }
        var tracks = CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, task.Probe).Count;
        var audioKbps = options.AudioBitrateKbps ?? DefaultAudioBitrate(task);
        var videoKbps = budgetKbps - audioKbps * tracks;
        if (videoKbps < 1) throw CannotMeet();
        return options with
        {
            AudioBitrateKbps = tracks > 0 ? audioKbps : null,
            VideoBitrateKbps = (int)Math.Min(200000, videoKbps)
        };
    }

    public static MediaConversionOptions Reduce(MediaTask task, MediaConversionOptions options, long measuredBytes)
    {
        if (options.TargetSizeBytes is not { } limit || measuredBytes <= 0) throw CannotMeet();
        var ratio = Math.Min(0.9, limit * 0.95 / measuredBytes);
        if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            var bitrate = SupportedAudioBitrate(task.TargetFormatId, Math.Floor(options.AudioBitrateKbps!.Value * ratio));
            if (bitrate < 8 || bitrate >= options.AudioBitrateKbps.Value) throw CannotMeet();
            return options with { AudioBitrateKbps = bitrate };
        }
        var reduced = (int)Math.Floor(options.VideoBitrateKbps!.Value * ratio);
        if (reduced < 1 || reduced >= options.VideoBitrateKbps.Value) throw CannotMeet();
        return options with { VideoBitrateKbps = reduced };
    }

    private static int DefaultAudioBitrate(MediaTask task) =>
        FormatCatalog.ContainerForVideoTarget(task.TargetFormat) == "WEBM" ||
        FormatCatalog.VideoCodecForTarget(task.TargetFormat) is "vp9" or "av1"
            ? task.ConversionMode == "quality" ? 160 : 128 : 192;

    // MP3/AC-3/WMA encoders need discrete rates or impose small codec-specific upper limits.
    private static int SupportedAudioBitrate(string target, double ceiling)
    {
        var candidates = target switch
        {
            "mp3" => new[] { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
            "ac3" => [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640],
            "wma" => [24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256, 320],
            _ => []
        };
        if (candidates.Length > 0) return candidates.LastOrDefault(value => value <= ceiling);
        return (int)Math.Clamp(ceiling, 0, target == "opus" ? 512 : target == "eac3" ? 640 : 1536);
    }

    public static InvalidDataException CannotMeet() => new(LocalizationService.T("MediaSizeCannotMeetTarget"));

    public static bool HasCompleteDuration(TimeSpan source, TimeSpan output, double? frameRate = null) => source > TimeSpan.Zero &&
        output > TimeSpan.Zero && Math.Abs((source - output).TotalSeconds) <= Math.Max(0.25, frameRate.HasValue ? 1 / frameRate.Value : 0);

    public static int InitialImageQuality(MediaTask task, MediaConversionOptions options) =>
        options.ImageQuality ?? task.ConversionMode switch { "quality" => 95, "fast" => 75, _ => 85 };

    public static async Task SearchImageAsync(string outputPath, long limit, int initialQuality,
        Func<int, int, CancellationToken, Task> encode, CancellationToken token)
    {
        var bestPath = outputPath + ".best";
        var ownsBest = false;
        var low = 1;
        var high = initialQuality;
        var quality = high;
        try
        {
            for (var attempt = 1; attempt <= MaximumImageAttempts && low <= high; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(outputPath)) File.Delete(outputPath);
                await encode(quality, attempt, token);
                token.ThrowIfCancellationRequested();
                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                    throw new InvalidDataException(LocalizationService.T("NativeImageValidationFailed"));
                if (new FileInfo(outputPath).Length <= limit)
                {
                    await using (var source = File.OpenRead(outputPath))
                    await using (var destination = new FileStream(bestPath, ownsBest ? FileMode.Create : FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        ownsBest = true;
                        await source.CopyToAsync(destination, token);
                    }
                    if (quality == initialQuality) break;
                    low = quality + 1;
                }
                else high = quality - 1;
                quality = low + (high - low) / 2;
            }
            if (!ownsBest) throw CannotMeet();
            token.ThrowIfCancellationRequested();
            File.Copy(bestPath, outputPath, true);
        }
        finally
        {
            if (ownsBest && File.Exists(bestPath)) File.Delete(bestPath);
        }
    }
}
