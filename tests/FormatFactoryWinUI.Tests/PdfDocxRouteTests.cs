using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class PdfDocxRouteTests
{
    [Fact]
    public async Task PdfImport_OffersDocxWithoutMediaProbeOrExternalTools()
    {
        using var directory = new TemporaryDirectory();
        var source = CreatePdf(directory.Path);
        var before = await File.ReadAllBytesAsync(source);

        var imported = await new MediaImportCoordinator().ImportPathsAsync(string.Empty, [source]);

        Assert.Empty(imported.Failures);
        var task = Assert.Single(imported.AddedTasks);
        Assert.Equal(ContentKind.Document, task.ContentKind);
        Assert.Equal("pdf", task.SourceFormatId);
        Assert.Contains("DOCX", task.AvailableFormats);
        Assert.Equal("pdf", task.TargetFormatId);
        task.TargetFormat = "DOCX";
        Assert.Equal("docx", task.TargetFormatId);
        Assert.Equal(DocumentEngineIds.Pipeline, task.EngineId);
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public void DocxExport_IsSelectableForPdfAndGeneralDocumentAutomation()
    {
        var format = Assert.IsType<FormatDescriptor>(FormatCatalog.FindById("docx"));
        Assert.True(format.CanImport);
        Assert.True(format.CanExport);
        Assert.Equal(".docx", format.PrimaryExtension);
        Assert.Equal(DocumentEngineIds.Pipeline, format.DefaultEngineId);
        Assert.Contains("DOCX", FormatCatalog.TargetsFor(ContentKind.Document, "PDF"));
        Assert.Contains("DOCX", FormatCatalog.TargetsFor(ContentKind.Document));
        new OutputConfiguration { TargetFormatId = "docx" }.Validate();
        new AutomationConversionPreset { Name = "Editable PDF text", TargetFormatId = "docx" }.Validate();
    }

    [Theory]
    [InlineData(ContentKind.Document, "docx")]
    [InlineData(ContentKind.Document, "doc")]
    [InlineData(ContentKind.Document, "caj")]
    [InlineData(ContentKind.Document, "hn")]
    [InlineData(ContentKind.Image, "png")]
    [InlineData(ContentKind.Image, "tiff")]
    [InlineData(ContentKind.Ebook, "epub")]
    public void NonPdfSources_DoNotAdvertiseAnUnimplementedDocxTarget(ContentKind kind, string source)
    {
        Assert.DoesNotContain("DOCX", FormatCatalog.TargetsFor(kind, source));
        Assert.False(new DocumentConversionRoutePlanner().CanPlan(source, "docx"));
    }

    [Fact]
    public void PdfDocxPlan_UsesBuiltInExtractionAndWriterWithoutExternalTools()
    {
        var planner = new DocumentConversionRoutePlanner();
        Assert.True(planner.CanPlan("PDF", "DOCX"));
        var plan = planner.Plan(new ConversionRequest
        {
            Inputs = [new("source.pdf", "pdf")], TargetFormatId = "docx"
        }, []);

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(DocumentEngineIds.PdfExtractor, plan.Steps[0].EngineId);
        Assert.Equal("pdf", plan.Steps[0].SourceFormatId);
        Assert.Equal("normalized-document", plan.Steps[0].TargetFormatId);
        Assert.Equal("normalized-document", plan.Steps[1].SourceFormatId);
        Assert.Equal("docx", plan.Steps[1].TargetFormatId);
        Assert.Equal([plan.Steps[0].OutputArtifactId], plan.Steps[1].InputArtifactIds);
    }

    [Fact]
    public async Task PersistedPdfDocx_RestoresTargetAndDoesNotPersistItsPassword()
    {
        using var directory = new TemporaryDirectory();
        var source = CreatePdf(directory.Path);
        var storePath = Path.Combine(directory.Path, "queue.json");
        var store = new QueueStore(storePath, () => []);
        var task = new MediaTask
        {
            InputPath = source, SourceFormatId = "pdf", ContentKind = ContentKind.Document,
            TargetFormatId = "docx", State = ConversionState.Waiting
        };
        task.Inputs.Add(new(source, "pdf"));
        task.Options["password"] = "memory-only-pdf-docx-secret";
        task.Options["ocrLanguage"] = "en-US";
        await store.SaveAsync([task]);

        var stored = Assert.Single((await store.LoadResultAsync()).Items);
        Assert.Equal("docx", stored.TargetFormatId);
        Assert.DoesNotContain("password", stored.Options.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("memory-only-pdf-docx-secret", await File.ReadAllTextAsync(storePath));
        var restored = await new MediaImportCoordinator().ImportPersistedAsync(string.Empty, stored);

        Assert.Null(restored.Error);
        var model = Assert.IsType<MediaTask>(restored.Task);
        Assert.Equal(task.Id, model.Id);
        Assert.Equal("docx", model.TargetFormatId);
        Assert.Equal("DOCX", model.TargetFormat);
        Assert.Equal(DocumentEngineIds.Pipeline, model.EngineId);
        Assert.Contains("DOCX", model.AvailableFormats);
        Assert.Equal("en-US", model.Options["ocrLanguage"]);
        Assert.DoesNotContain("password", model.Options.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.True(new DocumentConversionRoutePlanner().CanPlan(model.SourceFormatId, model.TargetFormatId));
    }

    [Fact]
    public void LosslessPdfDocx_IsRejectedBeforeNormalization()
    {
        var request = new ConversionRequest
        {
            Inputs = [new("source.pdf", "pdf")], TargetFormatId = "docx", Mode = "lossless"
        };
        Assert.Throws<NotSupportedException>(() => DocumentPreservationPolicy.Validate(request));
        Assert.Throws<NotSupportedException>(() => new DocumentConversionRoutePlanner().Plan(request, []));
    }

    private static string CreatePdf(string directory)
    {
        var path = Path.Combine(directory, "source.pdf");
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        pdf.AddPage();
        pdf.Save(path);
        return path;
    }
}
