using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FormatFactoryWinUI.Services;

internal static class AdditionalLanguageResources
{
    // These older JSON files are supplemented by the feature-specific tables in Load.
    private static readonly IReadOnlyList<LocalizedOption> SupplementedLanguages =
    [
        new("da-DK", "Dansk"),
        new("hu-HU", "Magyar"),
        new("ro-RO", "Română"),
        new("sv-SE", "Svenska"),
        new("nb-NO", "Norsk bokmål"),
        new("fi-FI", "Suomi"),
        new("cs-CZ", "Čeština"),
        new("el-GR", "Ελληνικά"),
        new("uk-UA", "Українська"),
        new("vi-VN", "Tiếng Việt"),
        new("th-TH", "ไทย"),
        new("id-ID", "Bahasa Indonesia"),
        new("ms-MY", "Bahasa Melayu"),
        new("hi-IN", "हिन्दी"),
        new("fil-PH", "Filipino"),
        new("sk-SK", "Slovenčina"),
        new("sl-SI", "Slovenščina"),
        new("hr-HR", "Hrvatski"),
        new("bg-BG", "Български"),
        new("lt-LT", "Lietuvių"),
        new("lv-LV", "Latviešu"),
        new("et-EE", "Eesti"),
        new("is-IS", "Íslenska"),
        new("sr-Latn-RS", "Srpski (latinica)"),
        new("pt-PT", "Português (Portugal)"),
        new("he-IL", "עברית"),
        new("fa-IR", "فارسی"),
        new("bn-BD", "বাংলা"),
        new("ta-IN", "தமிழ்")
    ];

    internal static readonly IReadOnlyList<LocalizedOption> Languages =
    [
        .. SupplementedLanguages,
        new("ur-PK", "اردو"),
        new("pcm-NG", "Naijá (Nigerian Pidgin)"),
        new("mr-IN", "मराठी"),
        new("te-IN", "తెలుగు"),
        new("sw-KE", "Kiswahili"),
        new("ha-Latn-NG", "Hausa"),
        new("pa-Arab-PK", "پنجابی (شاہ مکھی)"),
        new("pa-IN", "ਪੰਜਾਬੀ (ਗੁਰਮੁਖੀ)"),
        new("yue-Hant-HK", "粵語（繁體）"),
        new("jv-ID", "Basa Jawa"),
        new("gu-IN", "ગુજરાતી"),
        new("yo-NG", "Yorùbá"),
        new("sr-Cyrl-RS", "Српски (ћирилица)"),
        new("es-419", "Español (Latinoamérica)"),
        new("bho-IN", "भोजपुरी"),
        new("kn-IN", "ಕನ್ನಡ"),
        new("ml-IN", "മലയാളം"),
        new("uz-Latn-UZ", "O‘zbekcha"),
        new("am-ET", "አማርኛ"),
        new("my-MM", "မြန်မာ"),
        new("or-IN", "ଓଡ଼ିଆ"),
        new("ne-NP", "नेपाली"),
        new("ps-AF", "پښتو"),
        new("om-ET", "Afaan Oromoo")
    ];

    private static readonly HashSet<string> SupplementedCodes =
        SupplementedLanguages.Select(option => option.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static bool UsesSupplementalTables(string code) => SupplementedCodes.Contains(code);

    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyDictionary<string, string>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, string> Get(string languageCode)
    {
        var canonicalCode = Languages.FirstOrDefault(language =>
            language.Key.Equals(languageCode, StringComparison.OrdinalIgnoreCase))?.Key
            ?? throw new ArgumentException("Unsupported additional language.", nameof(languageCode));
        return Cache.GetOrAdd(canonicalCode, code =>
            new Lazy<IReadOnlyDictionary<string, string>>(() => Load(code))).Value;
    }

    private static void ValidateCoverage(string code, IReadOnlyDictionary<string, string> resources)
    {
        var english = LocalizationResourceCatalog.EffectiveEnglish;
        var unexpectedKeys = resources.Keys.Except(english.Keys, StringComparer.Ordinal).ToArray();
        var missingKeys = english.Keys.Except(resources.Keys, StringComparer.Ordinal).ToArray();
        if (unexpectedKeys.Length != 0 || missingKeys.Length != 0)
        {
            throw new InvalidOperationException(
                $"Invalid localization coverage for '{code}': missing [{string.Join(", ", missingKeys)}], unexpected [{string.Join(", ", unexpectedKeys)}].");
        }
        foreach (var (key, value) in english)
            ValidateTranslation(code, key, value, resources[key]);
    }

    private static IReadOnlyDictionary<string, string> Load(string languageCode)
    {
        var resourceName = $"FormatFactoryWinUI.Localization.AdditionalLanguages.{languageCode}.json";
        using var stream = typeof(AdditionalLanguageResources).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded language resources: {languageCode}.");
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Language resources '{languageCode}' must be a JSON object.");
        }

        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"Invalid localization entry '{languageCode}:{property.Name}'.");
            }

            var value = property.Value.GetString()!;
            if (string.IsNullOrWhiteSpace(value) || !resources.TryAdd(property.Name, value))
            {
                throw new InvalidOperationException($"Empty or duplicate localization resource '{languageCode}:{property.Name}'.");
            }
        }

        if (UsesSupplementalTables(languageCode))
        {
            MediaOutputResourceData.AddTo(languageCode, resources);
            QueueUndoResourceData.AddTo(languageCode, resources);
            QueueOrderingResourceData.AddTo(languageCode, resources);
            SourceRelocationResourceData.AddTo(languageCode, resources);
            NavigationResourceData.AddTo(languageCode, resources);
            JumpListResourceData.AddTo(languageCode, resources);
            TrayResourceData.AddTo(languageCode, resources);
            ArchiveResourceData.AddTo(resources);
        }
        ValidateCoverage(languageCode, resources);
        return resources;
    }

    internal static void ValidateTranslation(string languageCode, string key, string english, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated) || !Placeholders(english).SequenceEqual(Placeholders(translated)) ||
            english.Count(character => character == '{') != translated.Count(character => character == '{') ||
            english.Count(character => character == '}') != translated.Count(character => character == '}'))
        {
            throw new InvalidOperationException($"Inconsistent localization placeholders for '{languageCode}:{key}'.");
        }

        // Named filename-template tokens are literal text, not composite format arguments.
        var hasNumericArguments = Regex.IsMatch(english, @"(?<!\{)\{\d+(?:[^}]*)\}(?!\})");
        if (!hasNumericArguments) return;
        try
        {
            var parsed = CompositeFormat.Parse(translated);
            var expected = CompositeFormat.Parse(english);
            if (parsed.MinimumArgumentCount != expected.MinimumArgumentCount)
            {
                throw new FormatException("Unexpected format argument count.");
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"Invalid composite format for '{languageCode}:{key}'.", exception);
        }
    }

    private static string[] Placeholders(string value) => Regex.Matches(value,
            @"(?<!\{)\{(?<token>[^{}]+)\}(?!\})")
        .Select(match => match.Groups["token"].Value)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
