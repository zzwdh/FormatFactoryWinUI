using System.Text.RegularExpressions;

namespace FormatFactoryWinUI.Services;

internal sealed record LocalizedResourceEntry(string Key, IReadOnlyList<string> Values);

internal sealed class LocalizedResourceModule
{
    internal static readonly IReadOnlyList<string> LegacyLanguageCodes =
    [
        "en-US", "zh-Hans", "zh-Hant", "ja-JP", "ko-KR", "es-ES", "fr-FR", "de-DE",
        "pt-BR", "ru-RU", "it-IT", "nl-NL", "pl-PL", "tr-TR", "ar-SA"
    ];

    internal static readonly IReadOnlyList<string> SupportedLanguageCodes =
        [.. LegacyLanguageCodes, .. AdditionalLanguageResources.Languages.Select(language => language.Key)];

    private readonly Dictionary<string, Dictionary<string, string>> _legacyResources;
    private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> _allResources;

    internal LocalizedResourceModule(string name, IReadOnlyList<LocalizedResourceEntry> entries,
        bool allowsEnglishFallback = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(entries);

        Name = name;
        AllowsEnglishFallback = allowsEnglishFallback;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        _legacyResources = LegacyLanguageCodes.ToDictionary(
            code => code,
            _ => new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || !keys.Add(entry.Key))
            {
                throw new InvalidOperationException($"Localization module '{name}' contains an empty or duplicate key: '{entry.Key}'.");
            }

            if (entry.Values.Count != LegacyLanguageCodes.Count)
            {
                throw new InvalidOperationException(
                    $"Localization key '{entry.Key}' in module '{name}' has {entry.Values.Count} values; expected {LegacyLanguageCodes.Count}.");
            }

            var expectedArguments = FormatArguments(entry.Values[0]);
            for (var index = 0; index < LegacyLanguageCodes.Count; index++)
            {
                var value = entry.Values[index];
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException(
                        $"Localization key '{entry.Key}' in module '{name}' is empty for {LegacyLanguageCodes[index]}.");
                }

                if (!expectedArguments.SequenceEqual(FormatArguments(value)))
                {
                    throw new InvalidOperationException(
                        $"Localization key '{entry.Key}' in module '{name}' has inconsistent format arguments for {LegacyLanguageCodes[index]}.");
                }

                _legacyResources[LegacyLanguageCodes[index]][entry.Key] = value;
            }
        }

        Entries = entries.ToArray();
        _allResources = new(BuildAllResources);
    }

    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildAllResources()
    {
        var result = _legacyResources.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
        foreach (var language in AdditionalLanguageResources.Languages)
        {
            var pack = AdditionalLanguageResources.Get(language.Key);
            result.Add(language.Key, Entries.ToDictionary(entry => entry.Key, entry => pack[entry.Key], StringComparer.Ordinal));
        }
        return result;
    }

    internal string Name { get; }
    internal bool AllowsEnglishFallback { get; }
    internal IReadOnlyList<LocalizedResourceEntry> Entries { get; }
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Resources => _allResources.Value;

    internal bool TryGetLegacy(string languageCode, string key, out string value)
    {
        if (_legacyResources.TryGetValue(languageCode, out var resources) && resources.TryGetValue(key, out var localized))
        {
            value = localized;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static int[] FormatArguments(string value) => Regex
        .Matches(value, @"(?<!\{)\{(?<index>\d+)(?:[^}]*)\}(?!\})")
        .Select(match => int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture))
        .Distinct()
        .Order()
        .ToArray();
}

internal static class LocalizationResourceCatalog
{
    private static readonly IReadOnlyList<LocalizedResourceModule> ResourceModules =
    [
        BaseUiLocalizationResources.Module,
        CoreLocalizationResources.Module,
        GeneralServiceLocalizationResources.Module,
        WindowsNativeUiLocalizationResources.Module,
        WindowsCapabilityLocalizationResources.Module,
        ConversionLocalizationResources.Module,
        HardwareSettingsLocalizationResources.Module,
        OperationalLogLocalizationResources.Module,
        MainWindowLogLocalizationResources.Module,
        QueueWorkflowLocalizationResources.Module,
        QueueReliabilityLocalizationResources.Module,
        QueueUndoLocalizationResources.Module,
        QueueOrderingLocalizationResources.Module,
        SourceRelocationLocalizationResources.Module,
        JumpListLocalizationResources.Module,
        TrayLocalizationResources.Module,
        SettingsWorkflowLocalizationResources.Module,
        SettingsActionsLocalizationResources.Module,
        FeedbackLocalizationResources.Module,
        TelemetryLocalizationResources.Module,
        StoreUpdateLocalizationResources.Module,
        ReviewReminderLocalizationResources.Module,
        LayoutWorkflowLocalizationResources.Module,
        NativeWorkflowLocalizationResources.Module,
        DesktopWorkflowLocalizationResources.Module,
        ScanPrintLocalizationResources.Module,
        NativeImageLocalizationResources.Module,
        ImagePreservationLocalizationResources.Module,
        AutomationLocalizationResources.Module,
        AutomationUpgradeLocalizationResources.Module,
        DesktopUpgradeLocalizationResources.Module,
        QueueBatchLocalizationResources.Module,
        HistoryReuseLocalizationResources.Module,
        OutputManagementLocalizationResources.Module,
        MediaCompatibilityLocalizationResources.Module,
        DocumentCompatibilityLocalizationResources.Module,
        PdfDocxLocalizationResources.Module,
        OutputRecoveryLocalizationResources.Module,
        MediaOutputLocalizationResources.Module,
        ResultSummaryLocalizationResources.Module,
        NamingPreviewLocalizationResources.Module,
        ArchiveLocalizationResources.Module
    ];

    private static readonly IReadOnlyDictionary<string, LocalizedResourceModule> ModulesByKey = BuildKeyIndex();
    internal static readonly IReadOnlyDictionary<string, string> EffectiveEnglish = BuildEffectiveEnglish();

    internal static IReadOnlyList<LocalizedResourceModule> Modules => ResourceModules;

    internal static bool TryGetLegacy(string languageCode, string key, out string value)
    {
        if (ModulesByKey.TryGetValue(key, out var module))
        {
            return module.TryGetLegacy(languageCode, key, out value);
        }

        value = string.Empty;
        return false;
    }

    private static IReadOnlyDictionary<string, LocalizedResourceModule> BuildKeyIndex()
    {
        var result = new Dictionary<string, LocalizedResourceModule>(StringComparer.Ordinal);
        foreach (var module in ResourceModules)
        {
            foreach (var entry in module.Entries)
            {
                if (!result.TryAdd(entry.Key, module))
                {
                    throw new InvalidOperationException(
                        $"Localization key '{entry.Key}' is declared by more than one resource module.");
                }
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildEffectiveEnglish()
    {
        var english = new Dictionary<string, string>(LocalizationService.BaseEnglishResources, StringComparer.Ordinal);
        foreach (var entry in ResourceModules.SelectMany(module => module.Entries))
            english[entry.Key] = entry.Values[0];
        return english;
    }
}
