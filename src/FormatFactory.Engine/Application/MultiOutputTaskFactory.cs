using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

/// <summary>Expand one source into ordinary, independently executable queue tasks.</summary>
public static class MultiOutputTaskFactory
{
    public static IReadOnlyList<MediaTask> Create(MediaTask source,
        IReadOnlyList<OutputConfiguration> outputs, Guid? groupId = null, bool retainAutomationSnapshot = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(outputs);
        if (source.ContentKind == ContentKind.Disc || outputs.Count == 0 || groupId == Guid.Empty)
            throw new InvalidOperationException(LocalizationService.T("AutomationValidationOptions"));
        var copied = outputs.Select(output => output?.Snapshot() ?? throw new InvalidDataException()).ToArray();
        foreach (var output in copied)
        {
            output.Validate();
            if (!source.AvailableFormats.Contains(FormatCatalog.FindById(output.TargetFormatId)!.DisplayName,
                    StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(LocalizationService.T("AutomationTargetUnsupported", source.FileName, output.TargetFormatId));
        }
        if (copied.Select(output => output.Id).Distinct().Count() != copied.Length)
            throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));

        var id = groupId ?? Guid.NewGuid();
        return copied.Select((output, index) =>
        {
            // Capture also makes a deep probe snapshot; applying a different output never mutates the source.
            var request = ConversionRequestFactory.Capture(source, new AppSettings()) with
            {
                TaskId = index == 0 ? source.Id : Guid.NewGuid(),
                TargetFormatId = output.TargetFormatId,
                Mode = output.ConversionMode,
                SelectedAudioTrack = output.SelectedAudioTrack,
                Options = new Dictionary<string, string>(output.Options, StringComparer.OrdinalIgnoreCase)
            };
            var target = FormatCatalog.FindById(output.TargetFormatId)!;
            var task = ConversionRequestFactory.CreateMediaTask(request, target.DefaultEngineId ?? source.EngineId);
            task.OutputGroupId = id;
            task.OutputConfigurationId = output.Id;
            task.OutputLabel = output.Label;
            task.OutputOrder = index;
            task.OutputExpectedCount = copied.Length;
            foreach (var format in source.AvailableFormats) task.AvailableFormats.Add(format);
            foreach (var audio in task.Probe.AudioTracks) task.AudioTracks.Add(audio);
            if (retainAutomationSnapshot)
                foreach (var option in source.Options.Where(pair => pair.Key.StartsWith("automation.", StringComparison.OrdinalIgnoreCase)))
                    task.Options[option.Key] = option.Value;
            return task;
        }).ToArray();
    }
}
