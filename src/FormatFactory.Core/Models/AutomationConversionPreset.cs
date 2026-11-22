using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Models;

/// <summary>Reusable conversion choices without sources, destinations, credentials, or rule metadata.</summary>
public sealed record AutomationConversionPreset
{
    private static readonly Dictionary<string, string> OptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["layoutMode"] = "layoutMode", ["layout"] = "layout",
        ["ocrLanguage"] = "ocrLanguage", ["language"] = "language", ["repairDocx"] = "repairDocx",
        ["imageColorPolicy"] = "imageColorPolicy", ["audioTrackPolicy"] = "audioTrackPolicy",
        ["selectedAudioTrack"] = "selectedAudioTrack", ["outputFileNameTemplate"] = "outputFileNameTemplate",
        ["preserveFolderStructure"] = "preserveFolderStructure",
        ["media.targetSizeBytes"] = "media.targetSizeBytes", ["media.maxWidth"] = "media.maxWidth",
        ["media.maxHeight"] = "media.maxHeight", ["media.frameRate"] = "media.frameRate",
        ["media.videoBitrateKbps"] = "media.videoBitrateKbps", ["media.audioBitrateKbps"] = "media.audioBitrateKbps",
        ["media.sampleRate"] = "media.sampleRate", ["media.channels"] = "media.channels", ["image.quality"] = "image.quality"
    };
    private static readonly Regex LanguageTag = new(@"^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ReservedFileName = new(@"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired] public string Name { get; set; } = string.Empty;
    [JsonRequired] public string TargetFormatId { get; set; } = "mp4";
    [JsonRequired] public string ConversionMode { get; set; } = FormatCatalog.DefaultConversionMode;
    [JsonRequired] public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<OutputConfiguration> Outputs { get; set; } = [];

    public IReadOnlyList<OutputConfiguration> GetOutputs() => Outputs.Count > 0
        ? Outputs.Select(output => output.Snapshot()).ToArray()
        : [new OutputConfiguration
        {
            Id = Id, TargetFormatId = TargetFormatId, ConversionMode = ConversionMode,
            SelectedAudioTrack = Options.TryGetValue("selectedAudioTrack", out var track) && int.TryParse(track, out var index) ? index : 0,
            Options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase)
        }];

    public void MigrateOutputs()
    {
        if (Outputs.Count == 0) Outputs = GetOutputs().Select(output => output.Snapshot()).ToList();
    }

    public AutomationConversionPreset Snapshot() => this with
    {
        Options = Options is null ? throw InvalidOptions() : new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase),
        Outputs = Outputs is null ? throw InvalidOptions() : Outputs.Select(output => output.Snapshot()).ToList()
    };

    public void Validate()
    {
        if (Id == Guid.Empty) throw InvalidOptions();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || Name.Any(char.IsControl))
            throw new InvalidDataException(LocalizationService.T("AutomationValidationName"));
        if (FormatCatalog.FindById(TargetFormatId) is not { CanExport: true } target)
            throw new InvalidDataException(LocalizationService.T("AutomationValidationTarget"));
        if (!FormatCatalog.ConversionModes.Contains(ConversionMode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(LocalizationService.T("AutomationValidationMode"));
        if (Options is null || Options.Count > OptionNames.Count ||
            Options.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Options.Count)
            throw InvalidOptions();
        foreach (var pair in Options)
        {
            if (pair.Key is null || !OptionNames.TryGetValue(pair.Key, out var name) ||
                pair.Value is null || !IsApplicable(name, target.ContentKind) ||
                !IsValidOption(name, pair.Value, target.Id, ConversionMode))
                throw InvalidOptions();
        }
        try
        {
            MediaConversionOptions.Parse(Options).Validate(new MediaTask
            {
                ContentKind = target.ContentKind, TargetFormatId = target.Id, ConversionMode = ConversionMode
            });
        }
        catch (NotSupportedException exception) { throw new InvalidDataException(exception.Message, exception); }
        if (Outputs is null || Outputs.Count > 16 || Outputs.Select(output => output?.Id).Distinct().Count() != Outputs.Count)
            throw InvalidOptions();
        foreach (var output in Outputs)
        {
            if (output is null) throw InvalidOptions();
            output.Validate();
            var validation = new AutomationConversionPreset
            {
                Name = Name, TargetFormatId = output.TargetFormatId, ConversionMode = output.ConversionMode,
                Options = new Dictionary<string, string>(output.Options, StringComparer.OrdinalIgnoreCase)
            };
            validation.Validate();
        }
    }

    public static AutomationConversionPreset FromRule(AutomationRule rule, string name)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return FromOutputs(rule.GetOutputs(), name);
    }

    public static AutomationConversionPreset FromTask(MediaTask task, string name)
    {
        ArgumentNullException.ThrowIfNull(task);
        var options = new Dictionary<string, string>(task.Options, StringComparer.OrdinalIgnoreCase);
        if (FormatCatalog.FindById(task.TargetFormatId)?.ContentKind is ContentKind.Audio or ContentKind.Video)
            options["selectedAudioTrack"] = task.SelectedAudioTrack.ToString(CultureInfo.InvariantCulture);
        return Create(name, task.TargetFormatId, task.ConversionMode, options);
    }

    public static AutomationConversionPreset FromTasks(IReadOnlyList<MediaTask> tasks, string name) =>
        FromOutputs(tasks.Select(task => OutputConfiguration.FromTask(task)).ToArray(), name);

    public static AutomationConversionPreset FromOutputs(IReadOnlyList<OutputConfiguration> outputs, string name)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count is < 1 or > 16) throw InvalidOptions();
        var copies = outputs.Select(output =>
        {
            var clean = Create(name, output.TargetFormatId, output.ConversionMode, output.Options);
            return output with { Options = clean.Options };
        }).ToList();
        var first = copies[0];
        var preset = Create(name, first.TargetFormatId, first.ConversionMode, first.Options);
        preset.Outputs = copies;
        preset.Validate();
        return preset;
    }

    private static AutomationConversionPreset Create(string name, string targetId, string mode,
        IReadOnlyDictionary<string, string>? options)
    {
        var preset = new AutomationConversionPreset
        {
            Name = name?.Trim() ?? string.Empty,
            TargetFormatId = FormatCatalog.NormalizeFormatId(targetId),
            ConversionMode = mode?.Trim().ToLowerInvariant() ?? string.Empty
        };
        var target = FormatCatalog.FindById(preset.TargetFormatId);
        if (options is null) throw InvalidOptions();
        foreach (var pair in options)
        {
            // Only supported parameters cross this boundary. Metadata often contains paths and tokens.
            if (pair.Key is null || !OptionNames.TryGetValue(pair.Key, out var option) ||
                target is null || !IsApplicable(option, target.ContentKind)) continue;
            if (pair.Value is null) throw InvalidOptions();
            if (!preset.Options.TryAdd(option, pair.Value.Trim())) throw InvalidOptions();
        }
        preset.Validate();
        return preset;
    }

    private static bool IsApplicable(string name, ContentKind kind) => name switch
    {
        "layoutMode" or "layout" or "ocrLanguage" or "language" or "repairDocx" =>
            kind is ContentKind.Document or ContentKind.Ebook,
        "imageColorPolicy" => kind == ContentKind.Image,
        "audioTrackPolicy" or "selectedAudioTrack" => kind is ContentKind.Audio or ContentKind.Video,
        "outputFileNameTemplate" or "preserveFolderStructure" => true,
        "media.targetSizeBytes" => kind is ContentKind.Audio or ContentKind.Video or ContentKind.Image,
        "media.audioBitrateKbps" or "media.sampleRate" or "media.channels" => kind is ContentKind.Audio or ContentKind.Video,
        "media.maxWidth" or "media.maxHeight" => kind is ContentKind.Video or ContentKind.Image,
        "media.frameRate" or "media.videoBitrateKbps" => kind == ContentKind.Video,
        "image.quality" => kind == ContentKind.Image,
        _ => false
    };

    private static bool IsValidOption(string name, string value, string target, string mode) => name switch
    {
        "layoutMode" or "layout" => IsOneOf(value, "reflow", "fixed"),
        "ocrLanguage" or "language" => value.Length <= 63 &&
            (value.Equals("auto", StringComparison.OrdinalIgnoreCase) || LanguageTag.IsMatch(value)),
        "repairDocx" or "preserveFolderStructure" => bool.TryParse(value, out _),
        "imageColorPolicy" => value.Equals("srgb", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("preserve", StringComparison.OrdinalIgnoreCase) && target is "png" or "jpg" or "tiff" &&
            !(target == "jpg" && mode.Equals("lossless", StringComparison.OrdinalIgnoreCase)),
        "audioTrackPolicy" => value.Equals("compatible", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("lossless", StringComparison.OrdinalIgnoreCase),
        "selectedAudioTrack" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            index is >= 0 and <= 255,
        "outputFileNameTemplate" => ValidFileNameTemplate(value),
        "media.targetSizeBytes" => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes > 0,
        "media.maxWidth" or "media.maxHeight" or "media.videoBitrateKbps" or "media.audioBitrateKbps" or "media.sampleRate" =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0,
        "media.frameRate" => double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) && double.IsFinite(rate) && rate > 0,
        "media.channels" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var channels) && channels is >= 1 and <= 8,
        "image.quality" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var quality) && quality is >= 1 and <= 100,
        _ => false
    };

    private static bool IsOneOf(string value, params string[] choices) =>
        choices.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool ValidFileNameTemplate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl) ||
            value is "." or ".." || ReservedFileName.IsMatch(value) || value.Trim(' ', '.').Length == 0) return false;
        var literal = value.Replace("{name}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{format}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", string.Empty, StringComparison.OrdinalIgnoreCase);
        return literal.IndexOfAny(['{', '}', '<', '>', ':', '"', '/', '\\', '|', '?', '*']) < 0;
    }

    private static InvalidDataException InvalidOptions() =>
        new(LocalizationService.T("AutomationValidationOptions"));
}
