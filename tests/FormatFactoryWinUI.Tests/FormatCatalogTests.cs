using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class FormatCatalogTests
{
    [Fact]
    public void Catalog_HasStableUniqueIdsAndImportExtensions()
    {
        Assert.NotEmpty(FormatCatalog.All);
        Assert.Equal(
            FormatCatalog.All.Count,
            FormatCatalog.All.Select(format => format.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var importExtensions = FormatCatalog.All
            .Where(format => format.CanImport)
            .SelectMany(format => format.Extensions)
            .ToArray();
        Assert.Equal(
            importExtensions.Length,
            importExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(importExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase), FormatCatalog.SupportedExtensions);
    }

    [Theory]
    [InlineData("report.pdf", "pdf", ContentKind.Document)]
    [InlineData("legacy.DOC", "doc", ContentKind.Document)]
    [InlineData("archive.kdh", "caj", ContentKind.Document)]
    [InlineData("book.epub", "epub", ContentKind.Ebook)]
    [InlineData("page.webp", "webp", ContentKind.Image)]
    [InlineData("backup.ISO", "iso", ContentKind.Disc)]
    [InlineData("compressed.cso", "cso", ContentKind.Disc)]
    public void FindByExtension_MapsEveryContentFamily(
        string path,
        string expectedId,
        ContentKind expectedKind)
    {
        var format = Assert.IsType<FormatDescriptor>(FormatCatalog.FindByExtension(path));
        Assert.Equal(expectedId, format.Id);
        Assert.Equal(expectedKind, format.ContentKind);
    }

    [Fact]
    public void TargetSelection_UpdatesEngineThroughStableFormatId()
    {
        var task = new MediaTask
        {
            SourceFormatId = "docx",
            ContentKind = ContentKind.Document
        };

        task.TargetFormatId = "pdf";
        Assert.Equal("PDF", task.TargetFormat);
        Assert.Equal("document-pipeline", task.EngineId);

        task.TargetFormat = "MP3";
        Assert.Equal("mp3", task.TargetFormatId);
        Assert.Equal("ffmpeg", task.EngineId);
    }

    [Fact]
    public void ImageTargets_IncludeDocumentAndEbookOutputs()
    {
        var targets = FormatCatalog.TargetsFor(MediaKind.Image);

        Assert.Contains("PDF", targets);
        Assert.Contains("TXT", targets);
        Assert.Contains("MD", targets);
        Assert.Contains("EPUB", targets);
        Assert.Contains("MOBI", targets);
        Assert.Contains("AZW3", targets);
    }

    [Fact]
    public void Markdown_IsStableDocumentExportFormat()
    {
        var markdown = Assert.IsType<FormatDescriptor>(FormatCatalog.FindById("md"));

        Assert.Equal(ContentKind.Document, markdown.ContentKind);
        Assert.Equal(".md", markdown.PrimaryExtension);
        Assert.Contains("text/markdown", markdown.MimeTypes);
        Assert.True(markdown.CanExport);
        Assert.False(markdown.CanImport);
        Assert.Equal("document-pipeline", markdown.DefaultEngineId);
    }

    [Fact]
    public void DiscTargets_AreSpecificToPhysicalMediaAndImageSource()
    {
        Assert.Equal(["CSO"], FormatCatalog.TargetsFor(ContentKind.Disc, "iso"));
        Assert.Equal(["ISO"], FormatCatalog.TargetsFor(ContentKind.Disc, "cso"));
        Assert.Equal(["MKV"], FormatCatalog.TargetsFor(ContentKind.Disc, "bluray-disc"));
        Assert.Equal(
            ["ISO", "CSO"],
            FormatCatalog.TargetsFor(ContentKind.Disc, "dvd-disc"));
        var audioCdTargets = FormatCatalog.TargetsFor(ContentKind.Disc, "audio-cd");
        Assert.Contains("WAV", audioCdTargets);
        Assert.Contains("MP3", audioCdTargets);
        Assert.DoesNotContain("ISO", audioCdTargets);
    }

    [Fact]
    public void DiscTask_KeepsDiscPipelineWhenAudioTargetChanges()
    {
        var task = new MediaTask
        {
            SourceFormatId = OpticalDiscFormatIds.AudioCd,
            ContentKind = ContentKind.Disc
        };

        task.TargetFormat = "MP3";

        Assert.Equal("mp3", task.TargetFormatId);
        Assert.Equal("disc-pipeline", task.EngineId);
    }
}
