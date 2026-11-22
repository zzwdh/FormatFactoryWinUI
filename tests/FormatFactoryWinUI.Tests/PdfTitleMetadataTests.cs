using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FormatFactoryWinUI.Documents;

namespace FormatFactoryWinUI.Tests;

public sealed class PdfTitleMetadataTests
{
    [Theory]
    [InlineData(null, "source-book")]
    [InlineData("", "source-book")]
    [InlineData(" \t\r\n ", "source-book")]
    [InlineData("\u00a0\u2003", "source-book")]
    [InlineData("Example publication", "Example publication")]
    [InlineData("  Example publication  ", "  Example publication  ")]
    [InlineData("示例标题", "示例标题")]
    [InlineData("0", "0")]
    public async Task PdfToEpub_UsesFileNameForBlankMetadataAndPreservesValidTitle(
        string? metadataTitle, string expectedTitle)
    {
        using var directory = new TemporaryDirectory();
        var inputPath = Path.Combine(directory.Path, "source-book.pdf");
        var outputPath = Path.Combine(directory.Path, "converted.epub");
        WritePdf(inputPath, metadataTitle);
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(inputPath));

        try
        {
            using (var pdf = UglyToad.PdfPig.PdfDocument.Open(inputPath))
            {
                Assert.Equal(metadataTitle, pdf.Information.Title);
                Assert.Equal(1, pdf.NumberOfPages);
            }

            var extraction = await new PdfContentExtractor().ExtractAsync(inputPath, "en");
            Assert.Empty(extraction.OcrPageIndexes);
            Assert.Equal("Preserved source text", Assert.Single(extraction.PageTexts));

            var publication = new NormalizedDocumentEpubAdapter().CreatePublication(extraction.Document);
            await new Epub3Writer().WriteAsync(publication, outputPath);
            Assert.Equal(expectedTitle, extraction.Document.Title);

            var validation = await new EpubStructureValidator().ValidateAsync(outputPath);
            Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
            using var epub = ZipFile.OpenRead(outputPath);
            var package = ReadXml(epub, "EPUB/package.opf");
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            Assert.Equal(expectedTitle, Assert.Single(package.Descendants(dc + "title")).Value);
            Assert.Equal("en", Assert.Single(package.Descendants(dc + "language")).Value);
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(package.Descendants(dc + "identifier")).Value));

            XNamespace xhtml = "http://www.w3.org/1999/xhtml";
            var chapter = ReadXml(epub, "EPUB/chapter-0001.xhtml");
            Assert.Equal(expectedTitle, Assert.Single(chapter.Descendants(xhtml + "title")).Value);
            Assert.Contains(chapter.Descendants(xhtml + "p"), element => element.Value == "Preserved source text");
        }
        finally
        {
            Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(inputPath)));
        }
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        using var stream = (archive.GetEntry(path) ?? throw new InvalidDataException(path)).Open();
        return XDocument.Load(stream);
    }

    private static void WritePdf(string path, string? title)
    {
        const string content = "BT /F1 12 Tf 50 750 Td (Preserved source text) Tj ET\n";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"
        };
        if (title is not null)
        {
            var titleBytes = Encoding.BigEndianUnicode.GetPreamble()
                .Concat(Encoding.BigEndianUnicode.GetBytes(title)).ToArray();
            objects.Add($"<< /Title <{Convert.ToHexString(titleBytes)}> >>");
        }

        using var pdf = new MemoryStream();
        void Write(string value) => pdf.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(pdf.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xrefOffset = pdf.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        var info = title is null ? string.Empty : $" /Info {objects.Count} 0 R";
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{info} >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        File.WriteAllBytes(path, pdf.ToArray());
    }
}
