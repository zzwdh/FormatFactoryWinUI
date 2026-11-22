using System.Text.RegularExpressions;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class HardwareSettingsUiTests
{
    [Fact]
    public void SettingsPage_UsesGroupedDeviceCardsWithoutLegacySummaryControls()
    {
        var (xaml, code) = ReadMainWindowSources();
        var combined = xaml + Environment.NewLine + code;

        Assert.Contains("x:Name=\"HardwareCardsRepeater\"", xaml, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"HardwareDecodeGrouping\s*\.\s*Group"), code);
        Assert.Contains("HardwareInventoryService.GetCurrent", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GpuInfoText", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("HardwareDecodeResultsText", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalizationRefresh_RebuildsCardsAndUpdatesDirectionAndAutomationText()
    {
        var (_, code) = ReadMainWindowSources();

        Assert.Matches(
            new Regex(
                @"private void ApplyLocalization\(\).*?RootGrid\.FlowDirection = LocalizationService\.IsRightToLeft.*?SettingsView\.ApplyLocalization\(_settings\);.*?UpdateHardwareDecodeControls\(\);",
                RegexOptions.Singleline),
            code);
        Assert.Contains("HardwareSettingsHardwareListAutomationName", code, StringComparison.Ordinal);
        Assert.Contains("HardwareSettingsCardAutomationName", code, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedLegacyReport_ResetsCompletionFlagAndPersistsSettings()
    {
        var (_, code) = ReadMainWindowSources();

        Assert.Matches(
            new Regex(
                @"var testCompleted = _hardwareDecodeReport is not null;.*?if \(_settings\.HardwareDecodeTestCompleted != testCompleted\).*?await UpdateAutomaticSettingsAsync\(settings => settings\.HardwareDecodeTestCompleted = testCompleted\);",
                RegexOptions.Singleline),
            code);
        Assert.Matches(
            new Regex(
                @"private Task UpdateAutomaticSettingsAsync\(Action<AppSettings> update\) => _initializing\s*\? TryPersistStartupSettingsAsync\(\(\) => SettingsCommits\.UpdateAsync\(_settings, update\)\)\s*:\s*SettingsCommits\.UpdateAsync\(_settings, update\);"),
            code);
    }

    [Fact]
    public void CacheLoadAndProbe_UseCurrentFfmpegAndHardwareContext()
    {
        var (_, code) = ReadMainWindowSources();

        Assert.Matches(
            new Regex(@"LoadCachedAsync\(\s*_ffmpegPath,\s*_hardwareInventory\s*\)"),
            code);
        Assert.Matches(
            new Regex(@"var ffmpegPath = _ffmpegPath;.*?RunAsync\(\s*ffmpegPath,\s*inventory\s*\)", RegexOptions.Singleline),
            code);
    }

    [Fact]
    public void FfmpegContextChanges_InvalidateOldHardwareReportAcrossEveryUpdatePath()
    {
        var (_, code) = ReadMainWindowSources();

        Assert.Matches(
            new Regex(
                @"private async Task SetFfmpegContextAsync\(.*?_hardwareDecodeContextVersion\+\+;.*?_hardwareDecodeReport = null;.*?HardwareAccelerationTestService\.SetCurrentReport\(null\);.*?LoadHardwareDecodeReportAsync\(\);",
                RegexOptions.Singleline),
            code);
        Assert.Matches(
            new Regex(@"private async Task DetectFfmpegAsync\(.*?SetFfmpegContextAsync\(", RegexOptions.Singleline),
            code);
        var browse = Regex.Match(code,
            @"private async void BrowseFfmpegButton_Click\(.*?(?=\n    private )",
            RegexOptions.Singleline).Value;
        Assert.NotEmpty(browse);
        Assert.Contains("FfmpegPathTextBox.Text = file.Path;", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("SetFfmpegContextAsync(", browse, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"private async void SaveSettingsButton_Click\(.*?CommitDraftAsync\(.*?SetFfmpegContextAsync\(", RegexOptions.Singleline),
            code);
        Assert.Contains("contextVersion != _hardwareDecodeContextVersion", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsToolLabelsAndAccessibilityNames_AreLocalizedAtRuntime()
    {
        var (xaml, code) = ReadMainWindowSources();
        var shareService = File.ReadAllText(Path.Combine(
            RepositoryTestContext.Root,
            "src", "FormatFactoryWinUI", "Platform", "Services", "WindowsShareService.cs"));

        Assert.Contains("x:Name=\"AudioCdRipperLabelText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CajConverterLabelText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ChooseWatchFolderButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"WindowsNativeCard\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Windows native capabilities\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Refresh Windows native capability status\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LocalizationService.T(\"CoreAudioCdRipperLabel\")", code, StringComparison.Ordinal);
        Assert.Contains("LocalizationService.T(\"CoreCajConverterLabel\")", code, StringComparison.Ordinal);
        foreach (var key in new[]
                 {
                     "CoreMakeMkvPathPlaceholder",
                     "CoreAudioCdRipperPathPlaceholder",
                     "CoreReadomPathPlaceholder",
                     "CoreLibreOfficePathPlaceholder",
                     "CoreCalibrePathPlaceholder",
                     "CoreCajPathPlaceholder"
                 })
        {
            Assert.Contains($"LocalizationService.T(\"{key}\")", code, StringComparison.Ordinal);
        }
        Assert.Contains("ChooseWatchFolderButton.Content = LocalizationService.T(\"ChooseFolder\")", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName", code, StringComparison.Ordinal);
        Assert.Contains("LocalizationService.T(\"CoreShareConvertedMediaDescription\")", shareService, StringComparison.Ordinal);
        Assert.Contains("LocalizationService.T(\"BrandTitle\")", shareService, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageChanges_RefreshExternalToolStatusesAndTaskRequirements()
    {
        var (_, code) = ReadMainWindowSources();

        Assert.Matches(
            new Regex(
                @"private void ApplyLocalization\(\).*?task\.RefreshLocalization\(\);.*?RefreshTaskEngineAvailability\(\);",
                RegexOptions.Singleline),
            code);
        Assert.Matches(
            new Regex(
                @"private async void LanguageCombo_SelectionChanged\(.*?ApplyLocalization\(\);.*?RefreshDocumentEngineStatusAsync\(\);.*?RefreshOpticalEngineStatusAsync\(\);",
                RegexOptions.Singleline),
            code);
    }

    [Fact]
    public void HardwareSettingsResources_MatchUiReferencesAndTranslateEveryLanguage()
    {
        var (_, code) = ReadMainWindowSources();
        code += Environment.NewLine + File.ReadAllText(Path.Combine(
            RepositoryTestContext.Root,
            "src", "FormatFactory.Engine", "Services", "Hardware", "HardwareAccelerationTestService.cs"));
        var module = HardwareSettingsLocalizationResources.Module;
        var referencedKeys = Regex.Matches(code, "\\\"(?<key>HardwareSettings[A-Za-z0-9]+)\\\"")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var declaredKeys = module.Entries
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declaredKeys.Order(StringComparer.Ordinal), referencedKeys.Order(StringComparer.Ordinal));
        Assert.Equal(LocalizedResourceModule.SupportedLanguageCodes.Count, module.Resources.Count);
        foreach (var languageCode in LocalizedResourceModule.SupportedLanguageCodes.Where(code => code != "en-US"))
        {
            var translatedCount = module.Resources[languageCode].Count(resource =>
                !resource.Value.Equals(module.Resources["en-US"][resource.Key], StringComparison.Ordinal));
            Assert.True(
                translatedCount >= Math.Ceiling(module.Entries.Count * 0.6),
                $"{languageCode} translates only {translatedCount}/{module.Entries.Count} hardware resources.");
        }
    }

    private static (string Xaml, string Code) ReadMainWindowSources()
    {
        var root = Path.Combine(RepositoryTestContext.Root, "src", "FormatFactoryWinUI");
        return (
            string.Join(Environment.NewLine, Directory.GetFiles(Path.Combine(root, "Views"), "*.xaml")
                .Append(Path.Combine(root, "MainWindow.xaml")).Select(File.ReadAllText)),
            string.Join(Environment.NewLine, Directory.GetFiles(root, "MainWindow*.cs")
                .Concat(Directory.GetFiles(Path.Combine(root, "Windowing", "MainWindow"), "MainWindow*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(Path.Combine(root, "Views"), "*.cs"))
                .Order(StringComparer.Ordinal).Select(File.ReadAllText)));
    }
}

