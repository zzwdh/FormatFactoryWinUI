using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class RepositoryMetadataTests
{
    [Fact]
    public async Task RepositoryValidationScript_PassesOfflineReleaseGates()
    {
        var script = Path.Combine(RepositoryTestContext.Root, "build", "validate-repository.ps1");
        var result = await ProcessRunner.RunAsync(
            "pwsh.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Quiet"],
            TimeSpan.FromSeconds(20));

        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    [Fact]
    public void Version_IsFourPartSingleSourceAndMsixConfigDoesNotDuplicateIt()
    {
        var version = File.ReadAllText(Path.Combine(RepositoryTestContext.Root, "VERSION")).Trim();
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", version);
        Assert.All(version.Split('.'), segment => Assert.InRange(int.Parse(segment), 0, 65_535));

        var props = File.ReadAllText(Path.Combine(RepositoryTestContext.Root, "Directory.Build.props"));
        Assert.Contains("VERSION", props, StringComparison.Ordinal);
        Assert.Contains("<Version>$(ReleaseVersion)</Version>", props, StringComparison.Ordinal);

        using var config = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryTestContext.Root, "packaging", "msix-config.json")));
        Assert.False(config.RootElement.TryGetProperty("Version", out _));
    }

    [Fact]
    public void ManifestTemplate_RendersBothCompleteExtensionSets()
    {
        var catalogPath = Path.Combine(
            RepositoryTestContext.Root,
            "src", "FormatFactory.Core", "Models", "formats.json");
        using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var extensions = catalog.RootElement.EnumerateArray()
            .Where(format => format.GetProperty("capabilities").EnumerateArray()
                .Any(capability => capability.GetString() == "Import"))
            .SelectMany(format => format.GetProperty("extensions").EnumerateArray())
            .Select(extension => extension.GetString()!)
            .ToArray();
        Assert.NotEmpty(extensions);
        Assert.All(extensions, extension => Assert.Matches(@"^\.[a-z0-9]+$", extension));
        Assert.Equal(extensions.Length, extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var template = File.ReadAllText(Path.Combine(
            RepositoryTestContext.Root,
            "packaging",
            "AppxManifest.xml.template"));
        var supportedFileTypes = string.Join(
            Environment.NewLine,
            extensions.Select(extension => $"              <uap:FileType>{extension}</uap:FileType>"));
        var explorerItemTypes = string.Join(
            Environment.NewLine,
            extensions.Select(extension =>
                $"            <desktop5:ItemType Type=\"{extension}\"><desktop5:Verb Id=\"FormatFactoryConvert\" Clsid=\"B23F9D12-9ED4-4A40-B854-7A2B0C7D9A14\" /></desktop5:ItemType>"));
        var rendered = template
            .Replace("__ASSOCIATED_FILE_TYPES__", string.Join(Environment.NewLine,
                extensions.Where(extension => extension != ".epub").Select(extension => $"<uap:FileType>{extension}</uap:FileType>")), StringComparison.Ordinal)
            .Replace("__PREVIEW_FILE_TYPES__", "<uap:FileType>.epub</uap:FileType>", StringComparison.Ordinal)
            .Replace("__SUPPORTED_FILE_TYPES__", supportedFileTypes, StringComparison.Ordinal)
            .Replace("__EXPLORER_ITEM_TYPES__", explorerItemTypes, StringComparison.Ordinal)
            .Replace("__PACKAGE_NAME__", "FormatFactory.Tests", StringComparison.Ordinal)
            .Replace("__PUBLISHER__", "CN=Format Factory Tests", StringComparison.Ordinal)
            .Replace("__VERSION__", "1.2.3.4", StringComparison.Ordinal)
            .Replace("__ARCHITECTURE__", "x64", StringComparison.Ordinal)
            .Replace("__MINIMUM_WINDOWS_VERSION__", "10.0.19045.0", StringComparison.Ordinal)
            .Replace("__DISPLAY_NAME__", "Format Factory Tests", StringComparison.Ordinal)
            .Replace("__PUBLISHER_DISPLAY_NAME__", "Format Factory Tests", StringComparison.Ordinal)
            .Replace("__DESCRIPTION__", "Manifest rendering test", StringComparison.Ordinal);

        Assert.DoesNotMatch(new Regex("__[A-Z_]+__"), rendered);
        var document = XDocument.Parse(rendered);
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        XNamespace desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";
        var supportedFileTypeSets = document
            .Descendants(uap + "SupportedFileTypes")
            .Select(element => element.Elements(uap + "FileType").Select(item => item.Value).ToArray())
            .ToArray();

        Assert.Equal(3, supportedFileTypeSets.Length);
        Assert.Equal(extensions.Where(extension => extension != ".epub"), supportedFileTypeSets[0]);
        Assert.Equal(new[] { ".epub" }, supportedFileTypeSets[1]);
        Assert.Equal(extensions, supportedFileTypeSets[2]);
        XNamespace desktop2 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/2";
        Assert.Equal("7657D52B-308A-4B9F-821F-5D839DB74A11", document.Descendants(desktop2 + "ThumbnailHandler").Single().Attribute("Clsid")?.Value);
        Assert.Equal("34E71D22-EBB7-49E1-A7B3-CB9186CC4D20", document.Descendants(desktop2 + "DesktopPreviewHandler").Single().Attribute("Clsid")?.Value);
        var explorerTypes = document.Descendants(desktop5 + "ItemType")
            .Select(element => element.Attribute("Type")?.Value)
            .ToArray();
        Assert.Equal(extensions, explorerTypes);
        Assert.DoesNotContain("*", explorerTypes);
    }

    [Fact]
    public void ManifestTemplate_DeclaresEveryStoreSupportedInterfaceLanguage()
    {
        var document = XDocument.Load(Path.Combine(
            RepositoryTestContext.Root,
            "packaging",
            "AppxManifest.xml.template"));
        XNamespace manifest = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var declaredLanguages = document
            .Descendants(manifest + "Resource")
            .Select(resource => resource.Attribute("Language")?.Value)
            .ToArray();
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryTestContext.Root, "packaging", "msix-config.json")));
        var inAppOnlyLanguages = config.RootElement.GetProperty("InAppOnlyLanguages").EnumerateArray()
            .Select(language => language.GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(LocalizationService.LanguageOptions.Select(option => option.Key).ToHashSet(), inAppOnlyLanguages);
        var expectedLanguages = LocalizationService.LanguageOptions
            .Skip(1)
            .Where(option => !inAppOnlyLanguages.Contains(option.Key))
            .Select(option => option.Key switch
            {
                "zh-Hans" => "zh-CN",
                "zh-Hant" => "zh-TW",
                _ => option.Key
            })
            .ToArray();

        Assert.Equal(expectedLanguages, declaredLanguages);
    }

    [Fact]
    public void ImportCatalog_IncludesDocumentsAndKeepsEbookOutputsSeparate()
    {
        var extensions = FormatCatalog.SupportedExtensions;

        Assert.Contains(".mp4", extensions);
        Assert.Contains(".pdf", extensions);
        Assert.Contains(".docx", extensions);
        Assert.Contains(".caj", extensions);
        Assert.DoesNotContain(".mobi", extensions);
        Assert.DoesNotContain(".azw3", extensions);
    }
}

