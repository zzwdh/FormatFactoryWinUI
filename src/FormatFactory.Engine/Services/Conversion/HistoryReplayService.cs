using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.ApplicationServices;

namespace FormatFactoryWinUI.Services;

/// <summary>Only conversion choices are retained; credentials and device/access tokens are never captured.</summary>
public sealed record HistoryConversionSettings
{
    public string EbookLayoutMode { get; set; } = "reflow";
    public string OcrLanguage { get; set; } = "auto";
    public string OutputMode { get; set; } = "custom";
    public string CustomOutputDirectory { get; set; } = string.Empty;
    public int CpuThreadsPerTask { get; set; }
    public bool HardwareAcceleration { get; set; } = true;
    public bool PreserveFolderStructure { get; set; } = true;
    public string OutputFileNameTemplate { get; set; } = "{name}";
}

public sealed class HistoryReplayService
{
    private const string CapturedSettingsOption = "history.capturedSettings";
    private const string ReplaySettingsOption = "history.replaySettings";
    private static readonly HashSet<string> SafeOptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "imageColorPolicy", "layoutMode", "layout", "ocrLanguage", "language",
        "outputFileNameTemplate", "preserveFolderStructure", CompatibleAudioTrackPolicy.OptionKey,
        "media.targetSizeBytes", "media.maxWidth", "media.maxHeight", "media.frameRate",
        "media.videoBitrateKbps", "media.audioBitrateKbps", "media.sampleRate", "media.channels", "image.quality", "outputLabel",
        ArchiveHistoryReplayService.StrictVerificationKey, ArchiveHistoryReplayService.EncryptOutputKey,
        OutputPathService.DirectoryOverrideKey
    };

    /// <summary>Capture the settings actually used by an operation, after automation/replay overrides.</summary>
    public static void CaptureExecutionSettings(MediaTask task, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(settings);
        task.Options[CapturedSettingsOption] = JsonSerializer.Serialize(new HistoryConversionSettings
        {
            EbookLayoutMode = settings.EbookLayoutMode,
            OcrLanguage = settings.OcrLanguage,
            OutputMode = settings.OutputMode,
            CustomOutputDirectory = settings.CustomOutputDirectory,
            CpuThreadsPerTask = settings.CpuThreadsPerTask,
            HardwareAcceleration = settings.HardwareAcceleration,
            PreserveFolderStructure = settings.PreserveFolderStructure,
            OutputFileNameTemplate = settings.OutputFileNameTemplate
        });
    }

    public static void ApplyReplaySettings(MediaTask task, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(settings);
        if (!task.Options.TryGetValue(ReplaySettingsOption, out var json)) return;
        var snapshot = ReadSettings(json);
        if (snapshot is null) return;
        settings.EbookLayoutMode = snapshot.EbookLayoutMode;
        settings.OcrLanguage = snapshot.OcrLanguage;
        settings.OutputMode = snapshot.OutputMode;
        settings.CustomOutputDirectory = snapshot.CustomOutputDirectory;
        settings.CpuThreadsPerTask = snapshot.CpuThreadsPerTask;
        settings.HardwareAcceleration = snapshot.HardwareAcceleration;
        settings.PreserveFolderStructure = snapshot.PreserveFolderStructure;
        settings.OutputFileNameTemplate = snapshot.OutputFileNameTemplate;
    }

    internal static HistoryConversionSettings? ReadCapturedSettings(MediaTask task) =>
        task.Options.TryGetValue(CapturedSettingsOption, out var json) ? ReadSettings(json) : null;

    private static HistoryConversionSettings? ReadSettings(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<HistoryConversionSettings>(json);
            return value is null || value.EbookLayoutMode is null || value.OcrLanguage is null ||
                value.OutputMode is null || value.CustomOutputDirectory is null || value.OutputFileNameTemplate is null
                ? null : value;
        }
        catch (JsonException) { return null; }
    }

    internal static Dictionary<string, string> CopySafeOptions(IEnumerable<KeyValuePair<string, string>> options) =>
        options.Where(pair => SafeOptionNames.Contains(pair.Key) && pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, string> CopySafeOptions(MediaTask task)
    {
        var options = CopySafeOptions(task.Options);
        if (!string.IsNullOrWhiteSpace(task.OutputLabel)) options["outputLabel"] = task.OutputLabel;
        return options;
    }

    public static IReadOnlyList<string> MissingInputs(ConversionHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.InputPaths.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToArray();
    }

    public static PersistedQueueItem CreateQueueItem(ConversionHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.InputPaths.Count == 0 || item.InputPaths.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException(LocalizationService.T("HistoryReplayInputMissing", item.DisplayName));
        var target = FormatCatalog.FindById(item.TargetFormatId);
        if (target?.CanExport != true)
            throw new InvalidDataException(LocalizationService.T("HistoryReplayTargetUnavailable", item.TargetFormatId));
        var options = CopySafeOptions(item.Options);
        if (item.SettingsSnapshot is { } snapshot)
            options[ReplaySettingsOption] = JsonSerializer.Serialize(snapshot);
        return new PersistedQueueItem
        {
            TaskId = Guid.NewGuid(),
            InputPath = item.InputPaths[0],
            Inputs = item.InputPaths.Select(path => new PersistedConversionInput
            {
                Path = path,
                FormatId = FormatCatalog.FindByExtension(path)?.Id ?? item.SourceFormatId
            }).ToList(),
            SourceRoot = item.SourceRoot,
            ContentKind = item.ContentKind,
            SourceFormatId = item.SourceFormatId,
            TargetFormat = target.DisplayName,
            TargetFormatId = target.Id,
            EngineId = item.EngineId,
            ConversionMode = item.SnapshotVersion > 0 ? item.ConversionMode : FormatCatalog.DefaultConversionMode,
            ParametersVersion = item.ParametersVersion,
            SelectedAudioTrack = item.SelectedAudioTrack,
            OutputLabel = options.GetValueOrDefault("outputLabel", string.Empty),
            Options = options,
            State = ConversionState.Waiting
        };
    }

    /// <summary>Use a fresh import coordinator so replay can coexist with the original completed queue task.</summary>
    public static async Task<MediaImportItemResult> ImportAsync(ConversionHistoryItem item,
        string ffprobePath, ApplicationServices.MediaImportCoordinator importer, CancellationToken token = default)
    {
        var missing = MissingInputs(item);
        if (missing.Count > 0)
            throw new FileNotFoundException(LocalizationService.T("HistoryReplayInputMissing", missing[0]), missing[0]);
        var result = await importer.ImportPersistedAsync(ffprobePath, CreateQueueItem(item), token).ConfigureAwait(false);
        if (result.Task is { } task)
        {
            // Re-probing must never silently replace the saved target with a current default.
            if (!task.TargetFormatId.Equals(FormatCatalog.NormalizeFormatId(item.TargetFormatId), StringComparison.OrdinalIgnoreCase))
            {
                importer.Forget(task);
                throw new InvalidDataException(LocalizationService.T("HistoryReplayTargetUnavailable", item.TargetFormatId));
            }
            task.SelectedAudioTrack = item.SelectedAudioTrack;
        }
        return result;
    }
}

