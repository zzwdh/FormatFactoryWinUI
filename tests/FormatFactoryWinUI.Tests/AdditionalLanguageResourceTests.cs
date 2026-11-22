using System.Globalization;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class AdditionalLanguageResourceTests
{
    public static TheoryData<string, string> RegionalLanguages => new()
    {
        { "DA_gl", "da-DK" }, { "hu", "hu-HU" }, { "ro-MD", "ro-RO" },
        { "sv-FI", "sv-SE" }, { "no-NO", "nb-NO" }, { "nb", "nb-NO" },
        { "fi", "fi-FI" }, { "cs", "cs-CZ" }, { "el-CY", "el-GR" },
        { "uk", "uk-UA" }, { "  Uk_ua  ", "uk-UA" },
        { "vi", "vi-VN" }, { "th", "th-TH" }, { "id", "id-ID" }, { "ms-BN", "ms-MY" },
        { "hi", "hi-IN" }, { "fil", "fil-PH" }, { "sk", "sk-SK" }, { "sl", "sl-SI" },
        { "hr-BA", "hr-HR" }, { "bg", "bg-BG" }, { "lt", "lt-LT" }, { "lv", "lv-LV" },
        { "et", "et-EE" }, { "is", "is-IS" }, { "sr-Latn-BA", "sr-Latn-RS" },
        { "pt", "pt-PT" }, { "pt-AO", "pt-PT" }, { "PT_br", "pt-BR" }, { "pt-Latn-BR", "pt-BR" },
        { "he", "he-IL" }, { "fa", "fa-IR" }, { "bn-IN", "bn-BD" }, { "ta-LK", "ta-IN" },
        { "sr", "sr-Latn-RS" }, { "sr-RS", "sr-Latn-RS" }, { "sr-Cyrl", "sr-Cyrl-RS" },
        { " SR_cYRL_BA ", "sr-Cyrl-RS" }, { "ur-IN", "ur-PK" }, { "mr", "mr-IN" },
        { "sw-TZ", "sw-KE" }, { "ha-NE", "ha-Latn-NG" },
        { "pnb", "pa-Arab-PK" }, { "pa-PK", "pa-Arab-PK" }, { "pa-Arab-IN", "pa-Arab-PK" },
        { "pa", "pa-IN" }, { "pa-Guru-PK", "pa-IN" }, { "pcm", "pcm-NG" },
        { "yue", "yue-Hant-HK" }, { "zh-yue-HK", "yue-Hant-HK" }, { "jv", "jv-ID" },
        { "gu", "gu-IN" }, { "yo-BJ", "yo-NG" }, { "bho", "bho-IN" }, { "kn", "kn-IN" },
        { "ml", "ml-IN" }, { "uz", "uz-Latn-UZ" }, { "am", "am-ET" }, { "my", "my-MM" },
        { "or", "or-IN" }, { "ne-IN", "ne-NP" }, { "ps-PK", "ps-AF" }, { "om-KE", "om-ET" },
        { "es-Latn-AR", "es-419" }, { "es-CO", "es-419" }, { "es-US", "es-419" },
        { "es", "es-ES" }, { "es-GQ", "es-ES" }, { "es-ES-u-rg-mxzzzz", "es-ES" },
        { "zh-HK-u-nu-hanidec", "zh-Hant" }, { "zh-Hans-HK", "zh-Hans" },
        { "pt-PT-x-BR", "pt-PT" }
    };

    [Theory]
    [MemberData(nameof(RegionalLanguages))]
    public void RegionalAndNeutralLanguagesResolveToNewResources(string preference, string expected)
    {
        Assert.Equal(expected, LocalizationService.NormalizeCode(preference));
    }

    [Theory]
    [InlineData("ur-IN", "ur-PK", "ترتیبات")]
    [InlineData("sr-Cyrl-BA", "sr-Cyrl-RS", "Поставке")]
    [InlineData("sw-TZ", "sw-KE", "Mipangilio")]
    [InlineData("ha-NG", "ha-Latn-NG", "Saituna")]
    [InlineData("mr-IN", "mr-IN", "सेटिंग्ज")]
    [InlineData("te-IN", "te-IN", "సెట్టింగ్‌లు")]
    [InlineData("pcm-NG", "pcm-NG", "App settings")]
    [InlineData("yue-HK", "yue-Hant-HK", "設定")]
    [InlineData("jv-ID", "jv-ID", "Setelan")]
    [InlineData("pa-PK", "pa-Arab-PK", "سیٹنگاں")]
    [InlineData("es-MX", "es-419", "Configuración")]
    [InlineData("uz-UZ", "uz-Latn-UZ", "Sozlamalar")]
    [InlineData("gu-IN", "gu-IN", "સેટિંગ્સ")]
    [InlineData("yo-BJ", "yo-NG", "Ààtò")]
    [InlineData("pa-Guru-IN", "pa-IN", "ਸੈਟਿੰਗਾਂ")]
    [InlineData("bho-IN", "bho-IN", "सेटिंग")]
    [InlineData("kn-IN", "kn-IN", "ಸೆಟ್ಟಿಂಗ್‌ಗಳು")]
    [InlineData("ml-IN", "ml-IN", "ക്രമീകരണങ്ങൾ")]
    [InlineData("am-ET", "am-ET", "ቅንብሮች")]
    [InlineData("or-IN", "or-IN", "ସେଟିଂସ୍")]
    [InlineData("ps-PK", "ps-AF", "امستنې")]
    [InlineData("ne-IN", "ne-NP", "सेटिङ")]
    [InlineData("my-MM", "my-MM", "ဆက်တင်များ")]
    [InlineData("om-KE", "om-ET", "Qindaa’ina")]
    public async Task SystemLanguageAndSavedPickerChoiceShowTheSameTranslation(
        string systemLanguage, string expectedCode, string settingsTitle)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        using var directory = new TemporaryDirectory();
        var store = new SettingsService(Path.Combine(directory.Path, "settings.json"));
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(systemLanguage);
            LocalizationService.SetLanguage("auto");
            Assert.Equal(expectedCode, LocalizationService.CurrentLanguageCode);
            Assert.Equal(settingsTitle, LocalizationService.T("SettingsTitle"));

            var choice = Assert.Single(LocalizationService.LanguageOptions, option => option.Key == expectedCode);
            await store.SaveAsync(new AppSettings { Language = choice.Key });
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            LocalizationService.SetLanguage((await store.LoadAsync()).Language);
            Assert.Equal(settingsTitle, LocalizationService.T("SettingsTitle"));
            Assert.Equal(expectedCode, LocalizationService.CurrentUICulture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
            LocalizationService.SetLanguage(previousLanguage);
        }
    }

    [Fact]
    public void EmbeddedResourcesCoverAllEffectiveKeysAndPreserveTemplates()
    {
        var english = new Dictionary<string, string>(LocalizationService.BaseEnglishResources, StringComparer.Ordinal);
        foreach (var entry in LocalizationResourceCatalog.Modules.SelectMany(module => module.Entries))
            english[entry.Key] = entry.Values[0];

        foreach (var language in AdditionalLanguageResources.Languages)
        {
            var resources = AdditionalLanguageResources.Get(language.Key);
            Assert.Equal(english.Keys.Order(StringComparer.Ordinal), resources.Keys.Order(StringComparer.Ordinal));
            foreach (var (key, value) in english)
            {
                AdditionalLanguageResources.ValidateTranslation(language.Key, key, value, resources[key]);
                Assert.DoesNotContain('\uFFFD', resources[key]);
                Assert.Equal(value.Count(character => character == '\n'), resources[key].Count(character => character == '\n'));
            }
        }
    }

    [Theory]
    [InlineData("NamingTemplateHelp", "unknown")]
    [InlineData("ConversionUiRequiresCaj2Pdf", "caj2pdf")]
    [InlineData("ConversionUiJolietRequiresIso9660", "Joliet")]
    [InlineData("NativeOcrMissingFont", "U+{0}")]
    public void CompletePacksKeepLiteralFileValuesAndTechnicalIdentifiers(string key, string literal)
    {
        foreach (var language in AdditionalLanguageResources.Languages.Where(option =>
                     !AdditionalLanguageResources.UsesSupplementalTables(option.Key)))
            Assert.Contains(literal, AdditionalLanguageResources.Get(language.Key)[key], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{name}-{date}.{format}", "{nimi}-{date}.{format}")]
    [InlineData("{name}-{date}.{format}", "{name}-{date}.{format}-{bogus}")]
    [InlineData("Use {name}", "Brug {name} {")]
    [InlineData("Track {0}", "Spor {1}")]
    [InlineData("{0:X8}", "{0:X}")]
    [InlineData("{0} × {0}", "{0}")]
    [InlineData("Track {0}", "Spor {0} {")]
    public void InvalidTranslationsRejectChangedOrMalformedPlaceholders(string english, string translated)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AdditionalLanguageResources.ValidateTranslation("da-DK", "Sample", english, translated));
    }

    [Fact]
    public void ReorderedNumericArgumentsAndLiteralFilenameTokensAreValid()
    {
        AdditionalLanguageResources.ValidateTranslation("da-DK", "Sample", "{0} of {1}", "Af {1}: {0}");
        AdditionalLanguageResources.ValidateTranslation("da-DK", "Sample", "Use {name}-{date}.{format}", "Brug {name}-{date}.{format}");
    }

    [Fact]
    public void ExplicitLanguageFormatsFileSizesIndependentlyOfSystemCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            using var viewModel = new MediaTaskViewModel(new MediaTask { FileSize = 1_572_864 });
            foreach (var language in AdditionalLanguageResources.Languages)
            {
                LocalizationService.SetLanguage(language.Key);
                viewModel.RefreshLocalization();
                Assert.Equal(language.Key, LocalizationService.CurrentUICulture.Name);
                Assert.Equal(CultureInfo.GetCultureInfo(language.Key).TextInfo.IsRightToLeft,
                    LocalizationService.IsRightToLeft);
                Assert.Equal(1.5.ToString("F1", CultureInfo.GetCultureInfo(language.Key)) + " MB", viewModel.FileSizeText);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            LocalizationService.SetLanguage(previousLanguage);
        }
    }

    [Fact]
    public void AdditionalLanguagesUseTheExpectedReadingDirection()
    {
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        try
        {
            foreach (var language in AdditionalLanguageResources.Languages)
            {
                LocalizationService.SetLanguage(language.Key);
                Assert.Equal(language.Key is "he-IL" or "fa-IR" or "ur-PK" or "pa-Arab-PK" or "ps-AF", LocalizationService.IsRightToLeft);
            }
        }
        finally
        {
            LocalizationService.SetLanguage(previousLanguage);
        }
    }

    [Fact]
    public void FollowingSystemLanguageKeepsItsRegionalNumberFormat()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUICulture = CultureInfo.CurrentUICulture;
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("sv-FI");
            LocalizationService.SetLanguage("auto");
            Assert.Equal("sv-SE", LocalizationService.CurrentLanguageCode);
            Assert.Equal("en-US", LocalizationService.CurrentCulture.Name);
            using var viewModel = new MediaTaskViewModel(new MediaTask { FileSize = 1_572_864 });
            Assert.Equal("1.5 MB", viewModel.FileSizeText);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUICulture;
            LocalizationService.SetLanguage(previousLanguage);
        }
    }
}
