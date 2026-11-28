using System.IO.Compression;
using System.Text;
using FormatFactoryWinUI.Documents;

namespace FormatFactoryWinUI.Tests;

public sealed class EpubStructureValidatorTests
{
    [Fact]
    public async Task Utf16PackageWithRelativeAndEscapedHrefsResolvesFixedPage()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "input.epub");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="books/meta/book.opf"/></rootfiles>
                </container>
                """);
            Add("books/meta/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.1">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier>test:book</dc:identifier><dc:title>书</dc:title><dc:language>zh</dc:language>
                    <meta property="rendition:layout">pre-paginated</meta>
                  </metadata>
                  <manifest><item id="page" properties="nav" href="../page%201.xhtml#title" media-type="application/xhtml+xml"/></manifest>
                  <spine><itemref idref="page"/></spine>
                </package>
                """, Encoding.Unicode);
            Add("books/page 1.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><meta name="viewport" content="width=2,height=3"/></head><body/></html>
                """);
            void Add(string name, string contents, Encoding? encoding = null)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                using var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(false));
                writer.Write(contents);
            }
        }
        var report = await new EpubStructureValidator().ValidateAsync(path);
        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors));
        Assert.Equal("books/meta/book.opf", report.PackagePath);
        Assert.Single(report.Warnings);
    }
}
