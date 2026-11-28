using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal static class MediaProbeParser
{
    internal static MediaProbeResult Parse(string json, string inputPath) =>
        ParseAsync(json, inputPath, CancellationToken.None).GetAwaiter().GetResult();

    internal static async Task<MediaProbeResult> ParseAsync(string json, string inputPath, CancellationToken token)
    {
        var result = await ConversionBackend.RequestRawJsonAsync<ProbeResponse>("ffprobe-parse", json, token).ConfigureAwait(false);
        switch (result.Error)
        {
            case "json": throw new JsonException("Invalid ffprobe JSON.");
            case "root": throw new InvalidOperationException("Expected an ffprobe JSON object.");
            case "empty": throw new InvalidDataException(LocalizationService.T("GeneralServiceNoRecognizableMediaStreams"));
            case "kind": throw new InvalidDataException(LocalizationService.T("GeneralServiceCannotDetermineMediaType"));
        }
        var probe = result.Probe ?? throw new InvalidDataException("Missing ffprobe result.");
        probe.Kind = result.HasVideo
            ? FormatCatalog.FindByExtension(Path.GetExtension(inputPath))?.ContentKind == ContentKind.Image ? MediaKind.Image : MediaKind.Video
            : MediaKind.Audio;
        probe.Duration = TimeSpan.FromTicks(result.DurationTicks);
        foreach (var track in probe.AudioTracks) track.Language ??= LocalizationService.T("Unknown");
        return probe;
    }

    private sealed record ProbeResponse(string? Error, MediaProbeResult? Probe, bool HasVideo, long DurationTicks);
}
