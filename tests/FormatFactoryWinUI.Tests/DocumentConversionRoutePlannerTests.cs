using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class DocumentConversionRoutePlannerTests
{
    private static readonly IReadOnlyList<ExternalToolInfo> AllTools =
    [
        new(ExternalToolKind.MicrosoftWord, "Word", true, "winword.exe", "1", "可用"),
        new(ExternalToolKind.LibreOffice, "LibreOffice", true, "soffice.exe", "1", "可用"),
        new(ExternalToolKind.Calibre, "Calibre", true, "ebook-convert.exe", "1", "可用"),
        new(ExternalToolKind.Caj2Pdf, "CAJ", true, "caj2pdf.exe", "1", "可用")
    ];

    public static TheoryData<string, string> FullMatrix => new()
    {
        { "pdf", "pdf" }, { "pdf", "txt" }, { "pdf", "md" }, { "pdf", "epub" }, { "pdf", "mobi" }, { "pdf", "azw3" },
        { "docx", "pdf" }, { "docx", "txt" }, { "docx", "md" }, { "docx", "epub" }, { "docx", "mobi" }, { "docx", "azw3" },
        { "doc", "pdf" }, { "doc", "txt" }, { "doc", "md" }, { "doc", "epub" }, { "doc", "mobi" }, { "doc", "azw3" },
        { "jpg", "pdf" }, { "png", "txt" }, { "jpg", "md" }, { "webp", "epub" }, { "tiff", "mobi" }, { "bmp", "azw3" },
        { "caj", "pdf" }, { "hn", "txt" }, { "caj", "md" }, { "c8", "epub" }, { "kdh", "mobi" }, { "caj", "azw3" }
    };

    [Theory]
    [MemberData(nameof(FullMatrix))]
    public void Plan_CoversRequiredMatrix(string source, string target)
    {
        var request = Request([(source + ".input", source)], target);
        var plan = new DocumentConversionRoutePlanner().Plan(request, AllTools);

        Assert.NotEmpty(plan.Steps);
        Assert.Equal(target, plan.Steps[^1].TargetFormatId);
    }

    [Fact]
    public void Plan_AllowsMixedImageCollectionAndPreservesAllInputArtifacts()
    {
        var request = Request(
            [("001.jpg", "jpg"), ("002.png", "png"), ("003.webp", "webp"), ("004.tiff", "tiff")],
            "epub");

        var plan = new DocumentConversionRoutePlanner().Plan(request, []);

        Assert.Equal("image", plan.Steps[0].SourceFormatId);
        Assert.Equal(["input:0", "input:1", "input:2", "input:3"], plan.Steps[0].InputArtifactIds);
        Assert.Equal(DocumentEngineIds.ImageOcr, plan.Steps[0].EngineId);
        Assert.Equal("normalized-document", plan.Steps[0].TargetFormatId);
        Assert.Equal("normalized-document", plan.Steps[1].SourceFormatId);
    }

    [Fact]
    public void Plan_RequiresCalibreOnlyForMobiAndAzw3Output()
    {
        var planner = new DocumentConversionRoutePlanner();
        _ = planner.Plan(Request([("scan.pdf", "pdf")], "epub"), []);

        var error = Assert.Throws<DocumentEngineUnavailableException>(() =>
            planner.Plan(Request([("scan.pdf", "pdf")], "azw3"), []));
        Assert.Contains("Calibre", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("pdf", DocumentEngineIds.PdfExtractor)]
    [InlineData("docx", DocumentEngineIds.DocxReader)]
    [InlineData("jpg", DocumentEngineIds.ImageOcr)]
    public void Plan_MarkdownUsesNormalizedDocumentAndBuiltInWriter(string source, string firstEngine)
    {
        var plan = new DocumentConversionRoutePlanner().Plan(
            Request([($"source.{source}", source)], "md"),
            []);

        Assert.Equal(firstEngine, plan.Steps[0].EngineId);
        Assert.Equal(DocumentEngineIds.MarkdownWriter, plan.Steps[^1].EngineId);
        Assert.Equal("normalized-document", plan.Steps[^1].SourceFormatId);
        Assert.Equal("md", plan.Steps[^1].TargetFormatId);
    }

    [Theory]
    [InlineData("mobi")]
    [InlineData("azw3")]
    public void Plan_PdfEbookRouteUsesBuiltInExtractionAndEpubBeforeCalibre(string target)
    {
        var plan = new DocumentConversionRoutePlanner().Plan(
            Request([("source.pdf", "pdf")], target),
            AllTools);

        Assert.Equal(
            [DocumentEngineIds.PdfExtractor, DocumentEngineIds.EpubWriter, ExternalEngineIds.Calibre],
            plan.Steps.Select(step => step.EngineId));
        Assert.Equal("epub", plan.Steps[^1].SourceFormatId);
        Assert.Equal(target, plan.Steps[^1].TargetFormatId);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("doc")]
    [InlineData("caj")]
    public void Plan_RejectsMultipleNonImageInputsInsteadOfSilentlyIgnoringAllButFirst(string source)
    {
        var request = Request(
            [("001." + source, source), ("002." + source, source)],
            source == "pdf" ? "txt" : "pdf");

        var error = Assert.Throws<NotSupportedException>(() =>
            new DocumentConversionRoutePlanner().Plan(request, AllTools));

        Assert.Equal(LocalizationService.T("ConversionUiSingleDocumentInputOnly"), error.Message);
    }

    private static ConversionRequest Request(
        IReadOnlyList<(string Path, string Format)> inputs,
        string target) => new()
        {
            Inputs = inputs.Select(value => new ConversionInput(value.Path, value.Format)).ToList(),
            TargetFormatId = target
        };
}
