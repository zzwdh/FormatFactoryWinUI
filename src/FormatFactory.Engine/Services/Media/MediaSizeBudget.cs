using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal static class MediaSizeBudget
{
    internal const int MaximumImageAttempts = 9;

    public static MediaConversionOptions Initial(MediaTask task, MediaConversionOptions options) =>
        CalculateAsync(task, options, null, CancellationToken.None).GetAwaiter().GetResult();

    public static MediaConversionOptions Reduce(MediaTask task, MediaConversionOptions options, long measuredBytes) =>
        CalculateAsync(task, options, measuredBytes, CancellationToken.None).GetAwaiter().GetResult();

    internal static async Task<MediaConversionOptions> CalculateAsync(MediaTask task, MediaConversionOptions options,
        long? measured, CancellationToken token)
    {
        if (options.TargetSizeBytes is not { } limit || task.Kind == MediaKind.Image) return options;
        var rates = await ConversionBackend.RequestAsync<BudgetRates>("media-plan", Describe(task, options, limit, measured), token).ConfigureAwait(false);
        if (rates.Error is { } key) throw new InvalidDataException(LocalizationService.T(key));
        return options with { VideoBitrateKbps = rates.VideoBitrate, AudioBitrateKbps = rates.AudioBitrate };
    }

    internal static object Describe(MediaTask task, MediaConversionOptions options, long limit, long? measured)
    {
        var audio = FormatCatalog.IsAudioFormat(task.TargetFormat);
        return new
        {
            action = "budget", target = task.TargetFormatId, audioOutput = audio,
            modernAudio = FormatCatalog.ContainerForVideoTarget(task.TargetFormat) == "WEBM" ||
                FormatCatalog.VideoCodecForTarget(task.TargetFormat) is "vp9" or "av1",
            qualityMode = task.ConversionMode == "quality", durationTicks = task.Probe.Duration.Ticks,
            limit, tracks = audio ? 0 : CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, task.Probe).Count,
            videoBitrate = options.VideoBitrateKbps, audioBitrate = options.AudioBitrateKbps, measured
        };
    }

    private sealed record BudgetRates([property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error,
        [property: System.Text.Json.Serialization.JsonPropertyName("videoBitrate")] int? VideoBitrate,
        [property: System.Text.Json.Serialization.JsonPropertyName("audioBitrate")] int? AudioBitrate);
    public static InvalidDataException CannotMeet() => new(LocalizationService.T("MediaSizeCannotMeetTarget"));

    public static bool HasCompleteDuration(TimeSpan source, TimeSpan output, double? frameRate = null) =>
        HasCompleteDurationAsync(source, output, frameRate, CancellationToken.None).GetAwaiter().GetResult();

    internal static Task<bool> HasCompleteDurationAsync(TimeSpan source, TimeSpan output, double? frameRate, CancellationToken token) =>
        ConversionBackend.RequestAsync<bool>("media-plan", new { action = "validate", check = "duration",
            source = source.Ticks, output = output.Ticks, frameRate }, token);

    public static int InitialImageQuality(MediaTask task, MediaConversionOptions options) =>
        options.ImageQuality ?? task.ConversionMode switch { "quality" => 95, "fast" => 75, _ => 85 };

    public static async Task SearchImageAsync(string outputPath, long limit, int initialQuality,
        Func<int, int, CancellationToken, Task> encode, CancellationToken token)
    {
        var bestPath = outputPath + ".best";
        var ownsBest = false;
        using var session = new MediaWorkerSession(token);
        await session.SendAsync(new { mode = "imageSize", path = outputPath, limit, initialQuality });
        try
        {
            while (true)
            {
                var step = await session.ReadAsync<ImageStep>();
                switch (step.Event)
                {
                    case "image":
                        if (File.Exists(outputPath)) File.Delete(outputPath);
                        await encode(step.Quality, step.Attempt, token);
                        await session.SendAsync(new { exitCode = 0 });
                        break;
                    case "capture":
                        await using (var source = File.OpenRead(outputPath))
                        await using (var destination = new FileStream(bestPath, ownsBest ? FileMode.Create : FileMode.CreateNew,
                            FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        {
                            ownsBest = true;
                            await source.CopyToAsync(destination, token);
                        }
                        break;
                    case "error":
                        await session.CompleteAsync();
                        throw new InvalidDataException(LocalizationService.T(step.Key!));
                    case "complete":
                        await session.CompleteAsync();
                        token.ThrowIfCancellationRequested();
                        File.Copy(bestPath, outputPath, true);
                        return;
                    default: throw new InvalidDataException("Unknown image size search event.");
                }
            }
        }
        finally
        {
            if (ownsBest && File.Exists(bestPath)) File.Delete(bestPath);
        }
    }
    private sealed record ImageStep(string Event, int Quality, int Attempt, string? Key);
}
