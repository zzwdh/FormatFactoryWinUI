using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FormatFactoryWinUI.Documents;
using PdfSharp.Pdf;

namespace FormatFactoryWinUI.Tests;

public sealed class OfficeFixedLayoutEpubConverterTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aMVEAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(null, "source")]
    [InlineData("Custom book", "Custom book")]
    public async Task OfficePdfRenderComposition_WritesValidFixedEpubWithOrderedPagesAndActualViewports(
        string? title, string expectedTitle)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.docx");
        var output = Path.Combine(directory.Path, "result.epub");
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace")).FullName;
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        var exported = false;
        async Task Export(string source, string pdf, CancellationToken token)
        {
            Assert.Equal(input, source);
            Assert.StartsWith(workspace + Path.DirectorySeparatorChar, pdf);
            using var document = new PdfDocument();
            document.AddPage();
            document.AddPage();
            document.Save(pdf);
            exported = true;
            await Task.CompletedTask;
        }
        async IAsyncEnumerable<DecodedImagePage> Render(string pdf,
            [EnumeratorCancellation] CancellationToken token)
        {
            Assert.True(exported);
            using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
            Assert.Equal(2, document.NumberOfPages);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            // Simulated renderer supplies page pixel dimensions and page snapshots;
            // the production callback uses Windows PDF page rendering.
            yield return new(Png, 1200, 1800, "First page text", "office-layout.pdf", 0);
            yield return new(Png, 1800, 1200, "Second page text", "office-layout.pdf", 1);
        }

        await Converter().ConvertAsync(input, output, workspace, "en", title, Export, Render);

        var validation = await new EpubStructureValidator().ValidateAsync(output);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(input));
        using var archive = ZipFile.OpenRead(output);
        var package = ReadXml(archive, "EPUB/package.opf");
        Assert.Contains(package.Descendants(), element => element.Name.LocalName == "title" && element.Value == expectedTitle);
        Assert.Contains(package.Descendants(), element => (string?)element.Attribute("property") == "rendition:layout" &&
            element.Value == "pre-paginated");
        foreach (var (number, viewport, text) in new[]
        {
            (1, "width=1200,height=1800", "First page text"),
            (2, "width=1800,height=1200", "Second page text")
        })
        {
            var chapter = ReadXml(archive, $"EPUB/chapter-{number:D4}.xhtml");
            Assert.Contains(chapter.Descendants(), element => (string?)element.Attribute("name") == "viewport" &&
                (string?)element.Attribute("content") == viewport);
            Assert.Contains(chapter.Descendants(), element => element.Name.LocalName == "p" && element.Value == text);
            var image = Assert.Single(chapter.Descendants(), element => element.Name.LocalName == "img");
            Assert.Equal($"assets/page-{number:D4}.png", (string?)image.Attribute("src"));
            Assert.Contains("source.docx", (string?)image.Attribute("alt"));
            await using var source = archive.GetEntry($"EPUB/assets/page-{number:D4}.png")!.Open();
            using var bytes = new MemoryStream();
            await source.CopyToAsync(bytes);
            Assert.Equal(Png, bytes.ToArray());
        }
    }

    [Fact]
    public async Task FailedOfficeExport_DoesNotRenderOrCreateEpub()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "result.epub");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Converter().ConvertAsync(
            "source.docx", output, directory.Path, null, null,
            (_, _, _) => throw new InvalidOperationException("Office export failed."),
            (_, _) => throw new InvalidOperationException("Renderer must not run.")));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CancellationAfterOfficeExport_DoesNotRenderOrCreateEpub()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "result.epub");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Converter().ConvertAsync(
            "source.docx", output, directory.Path, null, null,
            (_, _, _) => { cancellation.Cancel(); return Task.CompletedTask; },
            (_, _) => throw new InvalidOperationException("Renderer must not run."),
            cancellationToken: cancellation.Token));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CancellationDuringRendering_DisposesPagesAndDoesNotCreateEpub()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "result.epub");
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        async IAsyncEnumerable<DecodedImagePage> Render(string _,
            [EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                await Task.Yield();
                yield return new(Png, 1200, 1800, "First page", "source.pdf", 0);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                yield return new(Png, 1200, 1800, "Second page", "source.pdf", 1);
            }
            finally { disposed = true; }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Converter().ConvertAsync(
            "source.docx", output, directory.Path, null, null,
            (_, _, _) => Task.CompletedTask, Render, cancellationToken: cancellation.Token));

        Assert.True(disposed);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    private static OfficeFixedLayoutEpubConverter Converter() =>
        new(new ImageDocumentBuilder(), new NormalizedDocumentEpubAdapter(), new Epub3Writer());

    private static XDocument ReadXml(ZipArchive archive, string entry)
    {
        using var stream = archive.GetEntry(entry)!.Open();
        return XDocument.Load(stream);
    }
}
