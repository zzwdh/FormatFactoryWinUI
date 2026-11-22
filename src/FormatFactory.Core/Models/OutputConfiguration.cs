using FormatFactoryWinUI.Services;
using System.Text.Json.Serialization;

namespace FormatFactoryWinUI.Models;

/// <summary>A reusable output choice. Sources, credentials and execution metadata are never part of it.</summary>
public sealed record OutputConfiguration
{
    [JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = string.Empty;
    [JsonRequired] public string TargetFormatId { get; set; } = "mp4";
    [JsonRequired] public string ConversionMode { get; set; } = FormatCatalog.DefaultConversionMode;
    public int SelectedAudioTrack { get; set; }
    [JsonRequired] public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public OutputConfiguration Snapshot() => this with
    {
        Options = new Dictionary<string, string>(Options ?? throw Invalid(), StringComparer.OrdinalIgnoreCase)
    };

    public void Validate()
    {
        if (Id == Guid.Empty || Label is null || Label.Length > 64 || Label.Any(char.IsControl) ||
            Label.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            FormatCatalog.FindById(TargetFormatId) is not { CanExport: true } ||
            !FormatCatalog.ConversionModes.Contains(ConversionMode, StringComparer.OrdinalIgnoreCase) ||
            SelectedAudioTrack is < 0 or > 255 || Options is null ||
            Options.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || IsExecutionOption(pair.Key)) ||
            Options.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Options.Count)
            throw Invalid();
    }

    public static OutputConfiguration FromTask(MediaTask task, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new()
        {
            Id = task.OutputConfigurationId ?? Guid.NewGuid(),
            Label = label ?? task.OutputLabel,
            TargetFormatId = task.TargetFormatId,
            ConversionMode = task.ConversionMode,
            SelectedAudioTrack = task.SelectedAudioTrack,
            Options = task.Options.Where(pair => !IsExecutionOption(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    public static bool IsExecutionOption(string key) =>
        key.Equals("password", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("outputLabel", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("automation.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("history.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("sourceIdentity.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("output.group", StringComparison.OrdinalIgnoreCase);

    private static InvalidDataException Invalid() => new(LocalizationService.T("AutomationValidationOptions"));
}
