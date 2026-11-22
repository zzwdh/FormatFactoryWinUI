using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>An explicit conversion choice that keeps only the first ordinary AAC stereo track.</summary>
public static class CompatibleAudioTrackPolicy
{
    public const string OptionKey = "audioTrackPolicy";
    public const string CompatibleValue = "compatible";

    public static bool IsRequested(MediaTask task) =>
        task.Options.TryGetValue(OptionKey, out var value) &&
        string.Equals(value, CompatibleValue, StringComparison.OrdinalIgnoreCase);

    public static bool HasCompatibleTracks(MediaProbeResult probe) =>
        probe.AudioTracks.Any(track => string.Equals(track.Codec, "apple_apac", StringComparison.OrdinalIgnoreCase)) &&
        FindCompatibleTrack(probe) is not null;

    public static bool IsSupportedOutput(MediaTask task) =>
        FormatCatalog.FindById(task.TargetFormat) is { CanExport: true, ContentKind: ContentKind.Video or ContentKind.Audio } &&
        !FormatCatalog.IsFrameSequence(task.TargetFormat);

    public static bool CanSelect(MediaTask task, MediaProbeResult probe) =>
        IsSupportedOutput(task) && task.ConversionMode != "lossless" &&
        probe.Kind is MediaKind.Video or MediaKind.Audio && HasCompatibleTracks(probe);

    public static string? GetValidationError(MediaTask task, MediaProbeResult probe)
    {
        if (!IsRequested(task) || !IsSupportedOutput(task)) return null;
        if (task.ConversionMode == "lossless") return LocalizationService.T("MediaCompatibilityAacLossless");
        return probe.Kind is MediaKind.Video or MediaKind.Audio && HasCompatibleTracks(probe)
            ? null : LocalizationService.T("MediaCompatibilityAacUnavailable");
    }

    /// <summary>Keeps source stream indices intact; list order is the output audio order.</summary>
    public static IReadOnlyList<AudioTrackInfo> GetSelectedAudioTracks(MediaTask task, MediaProbeResult probe)
    {
        if (GetValidationError(task, probe) is { } error) throw new NotSupportedException(error);
        if (IsRequested(task) && IsSupportedOutput(task)) return [FindCompatibleTrack(probe)!];
        if (FormatCatalog.IsImageFormat(task.TargetFormat) || FormatCatalog.IsFrameSequence(task.TargetFormat)) return [];
        if (!FormatCatalog.IsAudioFormat(task.TargetFormat)) return probe.AudioTracks;

        var selected = probe.Kind == MediaKind.Video ? Math.Max(0, task.SelectedAudioTrack) : 0;
        if (selected >= probe.AudioTracks.Count)
            throw new InvalidDataException("The selected audio track is unavailable in the current file.");
        return [probe.AudioTracks[selected]];
    }

    public static string GetStreamMap(AudioTrackInfo track) =>
        track.StreamIndex >= 0 ? $"0:{track.StreamIndex}" : $"0:a:{Math.Max(0, track.RelativeIndex)}";

    private static AudioTrackInfo? FindCompatibleTrack(MediaProbeResult probe) => probe.AudioTracks.FirstOrDefault(track =>
        string.Equals(track.Codec, "aac", StringComparison.OrdinalIgnoreCase) && track.Channels == 2);
}
