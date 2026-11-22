using System.Collections.ObjectModel;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

/// <summary>Capture on the owning UI thread, after admission, before running an engine.</summary>
public static class ConversionRequestFactory
{
    public static ConversionRequest Capture(MediaTask task, AppSettings settings, string? password = null)
    {
        var inputs = task.Inputs.Count > 0 ? task.Inputs.ToArray() :
            [new ConversionInput(task.InputPath, SourceFormat(task), task.ContentProbe, task.InputAccessToken)];
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["layoutMode"] = settings.EbookLayoutMode,
            ["repairDocx"] = settings.RepairDocxOnImport.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ocrLanguage"] = settings.OcrLanguage,
            ["outputFileNameTemplate"] = settings.OutputFileNameTemplate,
            ["preserveFolderStructure"] = settings.PreserveFolderStructure.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        // Explicit per-output choices override defaults captured from the application settings.
        foreach (var option in task.Options) options[option.Key] = option.Value;
        if (task.OutputGroupId is not null)
        {
            options["output.groupId"] = task.OutputGroupId.Value.ToString("D");
            options["outputLabel"] = task.OutputLabel;
        }
        if (!string.IsNullOrEmpty(password)) options["password"] = password;
        return new ConversionRequest
        {
            TaskId = task.Id,
            Inputs = Array.AsReadOnly(inputs.Select(input => input with { Probe = CloneProbe(input.Probe) }).ToArray()),
            TargetFormatId = task.TargetFormatId,
            OutputDirectory = task.Options.TryGetValue(OutputPathService.DirectoryOverrideKey, out var directory) &&
                !string.IsNullOrWhiteSpace(directory) ? Path.GetFullPath(directory) :
                settings.OutputMode == "source" ? null : settings.CustomOutputDirectory,
            Mode = task.ConversionMode,
            ParametersVersion = task.ParametersVersion,
            SelectedAudioTrack = task.SelectedAudioTrack,
            SourceRoot = task.SourceRoot,
            SourceRootAccessToken = task.SourceRootAccessToken,
            Options = new ReadOnlyDictionary<string, string>(options)
        };
    }

    public static MediaTask CreateMediaTask(ConversionRequest request, string engineId)
    {
        request.Validate();
        var input = request.Inputs[0];
        var source = FormatCatalog.FindById(input.FormatId);
        var probe = CloneProbe(input.Probe) ?? new ContentProbeResult
        {
            ContentKind = source?.ContentKind ?? (OpticalDiscFormatIds.IsPhysicalDisc(input.FormatId)
                ? ContentKind.Disc : ContentKind.Document),
            FormatId = input.FormatId
        };
        var task = new MediaTask
        {
            Id = request.TaskId, InputPath = input.Path, InputAccessToken = input.AccessToken,
            SourceRoot = request.SourceRoot, SourceRootAccessToken = request.SourceRootAccessToken,
            ContentProbe = probe, SourceFormatId = input.FormatId, TargetFormatId = request.TargetFormatId,
            EngineId = engineId, ConversionMode = request.Mode, ParametersVersion = request.ParametersVersion,
            SelectedAudioTrack = request.SelectedAudioTrack, FileSize = probe.FileSize
        };
        foreach (var item in request.Inputs) task.Inputs.Add(item);
        foreach (var option in request.Options) task.Options[option.Key] = option.Value;
        if (request.Options.TryGetValue("output.groupId", out var group) && Guid.TryParse(group, out var groupId))
            task.OutputGroupId = groupId;
        if (request.Options.TryGetValue("outputLabel", out var label)) task.OutputLabel = label;
        return task;
    }

    public static AppSettings OutputSettings(AppSettings snapshot, string? directory)
    {
        var copy = snapshot.Snapshot();
        if (!string.IsNullOrWhiteSpace(directory))
        {
            copy.OutputMode = "custom";
            copy.CustomOutputDirectory = directory;
        }
        return copy;
    }

    public static AppSettings OutputSettings(AppSettings snapshot, ConversionRequest request)
    {
        var copy = OutputSettings(snapshot, request.OutputDirectory);
        if (request.Options.TryGetValue("outputFileNameTemplate", out var template))
            copy.OutputFileNameTemplate = OutputPathService.NormalizeFileNameTemplate(template);
        if (request.Options.TryGetValue("preserveFolderStructure", out var preserve) && bool.TryParse(preserve, out var value))
            copy.PreserveFolderStructure = value;
        if (request.Options.TryGetValue(OutputPathService.DirectoryOverrideKey, out var directory) && !string.IsNullOrWhiteSpace(directory))
        {
            copy.OutputMode = "custom";
            copy.CustomOutputDirectory = Path.GetFullPath(directory);
            copy.PreserveFolderStructure = false;
        }
        return copy;
    }

    public static ConversionExecutionResult ToExecutionResult(ConversionResult result, string targetFormatId) =>
        new(result.Success, result.Cancelled,
            result.Success && !string.IsNullOrWhiteSpace(result.OutputPath)
                ? [new ConversionArtifact("output:0", targetFormatId, result.OutputPath)] : [],
            result.Message, result.LogPath)
        {
            RecoverableOutputId = result.RecoverableOutputId,
            PreservedOutputPath = result.PreservedOutputPath,
            Retryable = result.Retryable
        };

    private static string SourceFormat(MediaTask task) => string.IsNullOrWhiteSpace(task.SourceFormatId)
        ? Path.GetExtension(task.InputPath).TrimStart('.').ToLowerInvariant() : task.SourceFormatId;

    private static ContentProbeResult? CloneProbe(ContentProbeResult? probe)
    {
        if (probe is null) return null;
        ContentProbeResult clone = probe is MediaProbeResult media ? new MediaProbeResult
        {
            Duration = media.Duration, Bitrate = media.Bitrate, Width = media.Width, Height = media.Height,
            FormatName = media.FormatName, Title = media.Title, Artist = media.Artist, Album = media.Album,
            IsHdr = media.IsHdr, IsAnimated = media.IsAnimated, VideoCodec = media.VideoCodec,
            VideoCodecTag = media.VideoCodecTag, VideoStreamIndex = media.VideoStreamIndex,
            PixelFormat = media.PixelFormat, ColorTransfer = media.ColorTransfer, ColorPrimaries = media.ColorPrimaries,
            ColorSpace = media.ColorSpace, ColorRange = media.ColorRange,
            SubtitleCodecs = Array.AsReadOnly(media.SubtitleCodecs.ToArray()),
            AudioTracks = Array.AsReadOnly(media.AudioTracks.Select(track => new AudioTrackInfo
            {
                RelativeIndex = track.RelativeIndex, Codec = track.Codec, Language = track.Language,
                CodecTag = track.CodecTag, StreamIndex = track.StreamIndex,
                Title = track.Title, Channels = track.Channels, ChannelLayout = track.ChannelLayout
            }).ToArray())
        } : new ContentProbeResult();
        clone.ContentKind = probe.ContentKind;
        clone.FormatId = probe.FormatId;
        clone.FileSize = probe.FileSize;
        clone.PageCount = probe.PageCount;
        clone.Metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(probe.Metadata));
        return clone;
    }
}
