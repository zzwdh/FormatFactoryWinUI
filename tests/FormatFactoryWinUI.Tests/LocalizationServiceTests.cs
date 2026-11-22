using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using System.Reflection;
using System.Text.RegularExpressions;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class LocalizationServiceTests
{
    [Fact]
    public void LanguageOptions_HaveUniqueStableKeysAndDisplayText()
    {
        var options = LocalizationService.LanguageOptions;

        const string prefix = "FormatFactoryWinUI.Localization.AdditionalLanguages.";
        var embeddedCodes = typeof(LocalizationService).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => name[prefix.Length..^5]);
        Assert.Equal(LocalizedResourceModule.LegacyLanguageCodes.Concat(embeddedCodes).Order(StringComparer.Ordinal),
            options.Skip(1).Select(option => option.Key).Order(StringComparer.Ordinal));
        Assert.Equal(options.Count, options.Select(option => option.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("auto", options[0].Key);
        Assert.All(options, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.DisplayName));
            Assert.Equal(option.DisplayName, option.ToString());
        });
    }

    [Fact]
    public void LanguageDictionaries_MatchOptionsAndPreserveFormatPlaceholders()
    {
        var english = GetPrivateField<IReadOnlyDictionary<string, string>>("English");
        var languages = GetPrivateField<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>("LegacyLanguages");
        var simplifiedChineseAdditions = GetPrivateField<IReadOnlyDictionary<string, string>>("SimplifiedChineseDocumentAdditions");
        var optionCodes = LocalizationService.LanguageOptions.Skip(1).Select(option => option.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(optionCodes.Order(StringComparer.OrdinalIgnoreCase),
            languages.Keys.Concat(AdditionalLanguageResources.Languages.Select(option => option.Key)).Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (code, resources) in languages)
        {
            Assert.Equal(english.Keys.Order(StringComparer.Ordinal), resources.Keys.Order(StringComparer.Ordinal));
            Assert.All(resources, resource => Assert.False(string.IsNullOrWhiteSpace(resource.Value), $"{code}:{resource.Key}"));
            foreach (var (key, value) in resources)
            {
                Assert.Equal(FormatArguments(english[key]), FormatArguments(value));
            }
        }

        foreach (var (key, value) in simplifiedChineseAdditions)
        {
            Assert.True(english.ContainsKey(key), key);
            Assert.False(string.IsNullOrWhiteSpace(value), key);
            Assert.Equal(FormatArguments(english[key]), FormatArguments(value));
        }
    }

    [Fact]
    public void FeatureResourceModules_CoverEveryOfferedLanguageAndKey()
    {
        var expectedCodes = LocalizationService.LanguageOptions
            .Skip(1)
            .Select(option => option.Key)
            .ToArray();
        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        // Layout-only formats such as "  {0}: {1}" carry no English wording.
        var formatToken = new Regex(@"\{[^{}]*\}");

        Assert.NotEmpty(LocalizationResourceCatalog.Modules);
        foreach (var module in LocalizationResourceCatalog.Modules)
        {
            Assert.Equal(module.Name == "Archive", module.AllowsEnglishFallback);
            Assert.NotEmpty(module.Entries);
            Assert.Equal(
                expectedCodes.Order(StringComparer.OrdinalIgnoreCase),
                module.Resources.Keys.Order(StringComparer.OrdinalIgnoreCase));

            var expectedKeys = module.Entries.Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray();
            var proseKeys = module.Resources["en-US"].Where(pair =>
                formatToken.Replace(pair.Value, string.Empty).Any(char.IsLetter)).Select(pair => pair.Key).ToArray();
            foreach (var (code, resources) in module.Resources)
            {
                Assert.Equal(expectedKeys, resources.Keys.Order(StringComparer.Ordinal));
                Assert.All(resources, resource =>
                    Assert.False(string.IsNullOrWhiteSpace(resource.Value), $"{module.Name}:{code}:{resource.Key}"));
                if (module.AllowsEnglishFallback && code is not ("en-US" or "zh-Hans" or "zh-Hant") &&
                    (LocalizedResourceModule.LegacyLanguageCodes.Contains(code) || AdditionalLanguageResources.UsesSupplementalTables(code)))
                {
                    Assert.All(resources, resource => Assert.Equal(module.Resources["en-US"][resource.Key], resource.Value));
                }
                else if (!code.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                {
                    var translatedCount = proseKeys.Count(key => resources[key] != module.Resources["en-US"][key]);
                    Assert.True(
                        translatedCount >= Math.Ceiling(proseKeys.Length * 0.6),
                        $"{module.Name}:{code} translates only {translatedCount}/{proseKeys.Length} text resources.");
                }
            }

            foreach (var entry in module.Entries)
            {
                Assert.True(allKeys.Add(entry.Key), $"Duplicate localization key: {entry.Key}");
            }
        }
    }

    [Fact]
    public void FeatureResourceModules_AreResolvedForEveryOfferedLanguage()
    {
        try
        {
            foreach (var code in LocalizationService.LanguageOptions.Skip(1).Select(option => option.Key))
            {
                LocalizationService.SetLanguage(code);
                foreach (var module in LocalizationResourceCatalog.Modules)
                {
                    foreach (var entry in module.Entries)
                    {
                        Assert.True(module.Resources[code].TryGetValue(entry.Key, out var expected));
                        Assert.Equal(expected, LocalizationService.T(entry.Key));
                    }
                }
            }
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Fact]
    public void BaseUiResourceModule_ExactlyCoversReferencedEnglishFallbacks()
    {
        var english = GetPrivateField<IReadOnlyDictionary<string, string>>("English");
        var languages = GetPrivateField<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>("LegacyLanguages");
        var simplifiedChineseAdditions = GetPrivateField<IReadOnlyDictionary<string, string>>("SimplifiedChineseDocumentAdditions");
        var sourceRoot = Path.Combine(RepositoryTestContext.Root, "src");
        var localizationRoot = Path.Combine(sourceRoot, "FormatFactory.Core", "Localization") + Path.DirectorySeparatorChar;
        var productionSource = string.Join(
            Environment.NewLine,
            RepositoryTestContext.EnumerateApplicationSources("*.cs")
            .Where(path => !path.StartsWith(localizationRoot, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Equals(
                Path.Combine(sourceRoot, "FormatFactory.Core", "Localization", "LocalizationService.cs"),
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        var referencedBaseKeys = Regex.Matches(
                productionSource,
                @"LocalizationService\.T\(\s*""(?<key>[A-Za-z0-9]+)""")
            .Select(match => match.Groups["key"].Value)
            .Where(english.ContainsKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
                     productionSource,
                     @"LocalizationService\.T\(\s*[A-Za-z0-9_.]+\s*\?\s*""(?<whenTrue>[A-Za-z0-9]+)""\s*:\s*""(?<whenFalse>[A-Za-z0-9]+)""\s*\)"))
        {
            foreach (var groupName in new[] { "whenTrue", "whenFalse" })
            {
                var key = match.Groups[groupName].Value;
                if (english.ContainsKey(key))
                {
                    referencedBaseKeys.Add(key);
                }
            }
        }

        var expectedFallbackKeys = referencedBaseKeys
            .Where(key => languages
                .Where(language => !language.Key.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                .Any(language =>
                {
                    var effectiveValue = language.Key.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) &&
                                         simplifiedChineseAdditions.TryGetValue(key, out var addition)
                        ? addition
                        : language.Value[key];
                    return effectiveValue.Equals(english[key], StringComparison.Ordinal);
                }))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualKeys = BaseUiLocalizationResources.Module.Entries
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedFallbackKeys);
        Assert.Equal(expectedFallbackKeys, actualKeys);
    }

    [Fact]
    public void FeatureResourceKeys_MatchProductionReferences()
    {
        var sourceRoot = Path.Combine(RepositoryTestContext.Root, "src");
        var localizationRoot = Path.Combine(sourceRoot, "FormatFactory.Core", "Localization") + Path.DirectorySeparatorChar;
        var productionSource = string.Join(
            Environment.NewLine,
            RepositoryTestContext.EnumerateApplicationSources("*.cs")
                .Where(path => !path.StartsWith(localizationRoot, StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        var declared = LocalizationResourceCatalog.Modules
            .Where(module => !module.Name.Equals("BaseUi", StringComparison.Ordinal))
            .SelectMany(module => module.Entries)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        // TrayMinimized belongs to BaseUi; the native tray menu uses the Tray feature module.
        var referenced = Regex.Matches(
                productionSource,
                "\"(?<key>(?:Archive|JumpList|Tray(?!Minimized\")|Core|GeneralService|NativeUi|NativeCapability|ConversionUi|HardwareSettings|OperationalLog|Desktop|ScanPrint|NativeImage|NativeOcr|ReviewReminder|Automation|SettingsActions|Feedback|Telemetry|StoreUpdate|Queue(?:Batch|Undo|Priority|Order|Reorder|Drop)|SourceRelocation|Media(?:Compatibility|Custom|Maximum|Max|Frame|Video|Audio|Sample|Chan|Image|Keep|Settings|Options|Size)|MultiOutput|DocumentCompatibility|PdfDocx|OutputRecovery|OutputDisk|OutputFileName|Naming|ResultSummary|Remux|History(?:Legacy|Output|Parameters|Repeat|Replay))[A-Za-z0-9]+)\"")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(referenced.Except(declared, StringComparer.Ordinal));
        var unused = declared.Except(referenced, StringComparer.Ordinal).Order().ToArray();
        Assert.True(unused.Length == 0, "Unreferenced localization keys: " + string.Join(", ", unused));
    }

    [Fact]
    public void ProductionRuntimeText_HasNoHardCodedHanCharactersOutsideLocalizationResources()
    {
        var sourceRoot = Path.Combine(RepositoryTestContext.Root, "src");
        var localizationRoot = Path.Combine(sourceRoot, "FormatFactory.Core", "Localization") + Path.DirectorySeparatorChar;
        var approvedLegacyValues = new[]
        {
            "源文件目录", "浅色", "深色", "快速", "高质量", "无损", "平衡"
        };
        var failures = new List<string>();

        foreach (var path in RepositoryTestContext.EnumerateApplicationSources("*.*")
                     .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.StartsWith(localizationRoot, StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Equals(
                         Path.Combine(sourceRoot, "FormatFactory.Core", "Localization", "LocalizationService.cs"),
                         StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains(
                         $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                         StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains(
                         $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                         StringComparison.OrdinalIgnoreCase)))
        {
            var source = File.ReadAllText(path);
            source = path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? Regex.Replace(source, "<!--.*?-->", string.Empty, RegexOptions.Singleline)
                : Regex.Replace(
                    Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
                    @"//.*$",
                    string.Empty,
                    RegexOptions.Multiline);

            foreach (var legacyValue in approvedLegacyValues)
            {
                source = source.Replace($"\"{legacyValue}\"", "\"\"", StringComparison.Ordinal);
            }

            var match = Regex.Match(source, @"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]");
            if (match.Success)
            {
                var line = source[..match.Index].Count(character => character == '\n') + 1;
                failures.Add($"{Path.GetRelativePath(sourceRoot, path)}:{line}");
            }
        }

        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("en-GB", "en-US")]
    [InlineData("ZH_cn", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hant-HK", "zh-Hant")]
    [InlineData("zh-CHT", "zh-Hant")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("ar-EG", "ar-SA")]
    [InlineData("es-MX", "es-419")]
    [InlineData("unsupported", "en-US")]
    [InlineData("  FR-ca  ", "fr-FR")]
    public void NormalizeCode_MapsRegionalCulturesToSupportedLanguages(string value, string expected)
    {
        Assert.Equal(expected, LocalizationService.NormalizeCode(value));
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData(" AUTO ", "auto")]
    [InlineData("ja", "ja-JP")]
    [InlineData("unknown", "en-US")]
    public void NormalizePreference_PreservesAutoAndCanonicalizesExplicitLanguages(string? value, string expected)
    {
        Assert.Equal(expected, LocalizationService.NormalizePreference(value));
    }

    [Fact]
    public void LanguagePickerText_IsLocalizedForEveryOfferedLanguage()
    {
        LocalizationService.SetLanguage("en-US");
        var englishDescription = LocalizationService.T("LanguageDescription");
        var englishAutoLabel = LocalizationService.T("AutoLanguage");
        var englishExit = LocalizationService.T("Exit");
        var englishGpuDecode = LocalizationService.T("GpuDecode", "D3D11");
        var englishQueueSummary = LocalizationService.T("QueueSummaryCounts", 3, 2, 1);
        var englishEngineStatus = LocalizationService.T("EngineAvailableVersion", "1.0");

        try
        {
            foreach (var code in LocalizationService.LanguageOptions.Skip(1).Select(option => option.Key).Where(code => code != "en-US"))
            {
                LocalizationService.SetLanguage(code);

                Assert.Equal(code, LocalizationService.CurrentLanguageCode);
                Assert.NotEqual(englishDescription, LocalizationService.T("LanguageDescription"));
                Assert.NotEqual(englishAutoLabel, LocalizationService.T("AutoLanguage"));
                Assert.NotEqual(englishExit, LocalizationService.T("Exit"));
                Assert.NotEqual(englishGpuDecode, LocalizationService.T("GpuDecode", "D3D11"));
                Assert.NotEqual(englishQueueSummary, LocalizationService.T("QueueSummaryCounts", 3, 2, 1));
                Assert.NotEqual(englishEngineStatus, LocalizationService.T("EngineAvailableVersion", "1.0"));
            }
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Fact]
    public void SetLanguage_AppliesCanonicalCultureAndRightToLeftDirection()
    {
        try
        {
            LocalizationService.SetLanguage("zh-HK");
            Assert.Equal("zh-Hant", LocalizationService.CurrentLanguageCode);
            Assert.Equal("zh-TW", LocalizationService.CurrentUICulture.Name);
            Assert.False(LocalizationService.IsRightToLeft);

            LocalizationService.SetLanguage("ar-EG");
            Assert.Equal("ar-SA", LocalizationService.CurrentLanguageCode);
            Assert.Equal("ar-SA", LocalizationService.CurrentUICulture.Name);
            Assert.True(LocalizationService.IsRightToLeft);
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Theory]
    [InlineData("en-US", "Data disc (E:)")]
    [InlineData("zh-Hans", "数据光盘 (E:)")]
    [InlineData("zh-Hant", "資料光碟 (E:)")]
    [InlineData("ja-JP", "データ ディスク (E:)")]
    [InlineData("ar-SA", "قرص بيانات (E:)")]
    public void OpticalDiscDisplayName_FollowsSelectedLanguage(string languageCode, string expected)
    {
        try
        {
            LocalizationService.SetLanguage(languageCode);
            var disc = new OpticalDiscInfo(
                "E:\\",
                @"\\.\E:",
                volumeLabel: null,
                OpticalDiscMediaKind.DataDisc,
                isReady: true,
                isFileSystemAccessible: true,
                driveOrdinal: 0);

            Assert.Equal(expected, disc.DisplayName);
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    private static T GetPrivateField<T>(string name) where T : class =>
        Assert.IsAssignableFrom<T>(typeof(LocalizationService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null));

    private static int[] FormatArguments(string value) => Regex
        .Matches(value, @"(?<!\{)\{(?<index>\d+)(?:[^}]*)\}(?!\})")
        .Select(match => int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture))
        .Distinct()
        .Order()
        .ToArray();
}

[CollectionDefinition(nameof(LocalizationServiceCollection), DisableParallelization = true)]
public sealed class LocalizationServiceCollection;


