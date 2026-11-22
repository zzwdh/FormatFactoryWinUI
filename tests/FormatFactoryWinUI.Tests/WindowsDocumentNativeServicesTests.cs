using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class WindowsDocumentNativeServicesTests
{
    [Fact]
    public void IFilterDocument_NormalizesParagraphsAndMetadata()
    {
        var document = WindowsFilterTextExtractor.CreateDocument(
            @"C:\资料\示例.doc",
            "第一行\r\n第二行\r\n\r\n第三段",
            "zh-CN");

        Assert.Equal("示例", document.Title);
        Assert.Equal("zh-CN", document.Language);
        var section = Assert.Single(document.Sections);
        Assert.Collection(
            section.Blocks,
            block => Assert.Equal("第一行 第二行", Assert.IsType<NormalizedParagraph>(block).Text),
            block => Assert.Equal("第三段", Assert.IsType<NormalizedParagraph>(block).Text));
    }

    [Fact]
    public async Task IFilterExtractor_RejectsMissingInputWithoutLoadingCom()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.doc");
        var extractor = new WindowsFilterTextExtractor();

        Assert.False(extractor.IsAvailable(missing));
        await Assert.ThrowsAsync<FileNotFoundException>(() => extractor.ExtractAsync(missing));
    }

    [Fact]
    public void DirectWriteCatalog_ReturnsSortedUniqueFontFamilies()
    {
        var fonts = new DirectWriteFontCatalog().Discover();

        Assert.All(fonts, font =>
        {
            Assert.False(string.IsNullOrWhiteSpace(font.FamilyName));
            Assert.True(font.FontCount > 0);
        });
        Assert.Equal(
            fonts.OrderBy(font => font.FamilyName, StringComparer.CurrentCultureIgnoreCase),
            fonts);
        Assert.Equal(
            fonts.Count,
            fonts.Select(font => font.FamilyName).Distinct(StringComparer.CurrentCultureIgnoreCase).Count());
    }

    [Theory]
    [InlineData("bmp", "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}")]
    [InlineData("jpeg", "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}")]
    [InlineData("TIF", "{B96B3CB1-0728-11D3-9D7B-0000F81EF32E}")]
    [InlineData("png", "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}")]
    [InlineData(null, "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}")]
    public void ScannerFormatIds_AreStable(string? format, string expected)
    {
        Assert.Equal(expected, WindowsScannerService.ResolveFormatId(format));
    }

    [Fact]
    public async Task ScannerDiscovery_DoesNotRequireConnectedHardware()
    {
        var devices = await new WindowsScannerService().DiscoverAsync();

        Assert.NotNull(devices);
        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.DeviceId));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
        });
    }

    [Fact]
    public void PrinterDiscovery_DoesNotRequireAPrinter()
    {
        var printers = new WindowsPrintService().DiscoverPrinters();

        Assert.NotNull(printers);
        Assert.All(printers, printer => Assert.False(string.IsNullOrWhiteSpace(printer.Name)));
        Assert.Equal(
            printers.OrderBy(printer => printer.Name, StringComparer.CurrentCultureIgnoreCase),
            printers);
    }

    [Fact]
    public async Task PrinterSubmission_RejectsMissingDocumentBeforeInvokingShell()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pdf");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new WindowsPrintService().SubmitAsync(missing, "unused"));
    }
}
