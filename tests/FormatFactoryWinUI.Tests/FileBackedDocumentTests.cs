using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class FileBackedDocumentTests
{
    [Fact]
    public async Task ResourceOpenRead_PreservesMemorySlicesAndBorrowsFileSources()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "page.png");
        byte[] expected = [2, 3, 4];
        await File.WriteAllBytesAsync(path, expected);
        var inMemory = new NormalizedResource("image", "image/png", "page.png", new byte[] { 1, 2, 3, 4, 5 }.AsMemory(1, 3));
        var onDisk = inMemory with { Content = ReadOnlyMemory<byte>.Empty, ContentPath = path };

        foreach (var resource in new[] { inMemory, onDisk })
        {
            await using var stream = resource.OpenRead();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(expected, copy.ToArray());
        }
        Assert.True(File.Exists(path));
        File.Delete(path); // Readers have released their handles; ownership stays with the workspace.
    }

    [Fact]
    public async Task HundredPageDocument_UsesFileSourcesThroughEpubAndMarkdownAndPreservesOrder()
    {
        using var directory = new TemporaryDirectory();
        var image = CreatePng();
        var path = Path.Combine(directory.Path, "source.png");
        await File.WriteAllBytesAsync(path, image);
        var pages = Enumerable.Range(1, 100).Select(number =>
            new DecodedImagePage([], 2, 1, $"OCR page {number:D3}", "source.tif", (uint)(number - 1)) { PngPath = path }).ToArray();
        var document = await new ImageDocumentBuilder().BuildAsync(Enumerate(pages), "Pages", "en", fixedLayout: true);

        Assert.Equal(100, document.Sections.Count);
        Assert.Equal(100, document.Resources.Count);
        Assert.All(document.Resources, resource =>
        {
            Assert.True(resource.Content.IsEmpty);
            Assert.Equal(path, resource.ContentPath);
        });
        Assert.Equal(Enumerable.Range(1, 100).Select(number => $"OCR page {number:D3}"),
            document.Blocks.OfType<NormalizedParagraph>().Select(paragraph => paragraph.Text));
        var publication = new NormalizedDocumentEpubAdapter().CreatePublication(document, fixedLayout: true);
        Assert.All(publication.Resources!, resource => Assert.Equal(path, resource.ContentPath));
        var epubPath = Path.Combine(directory.Path, "pages.epub");
        await new Epub3Writer().WriteAsync(publication, epubPath);
        using (var archive = ZipFile.OpenRead(epubPath))
        {
            Assert.Equal(100, archive.Entries.Count(entry => entry.FullName.StartsWith("EPUB/assets/", StringComparison.Ordinal)));
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("EPUB/assets/", StringComparison.Ordinal)))
            {
                await using var stream = entry.Open();
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                Assert.Equal(image, copy.ToArray());
            }
            using var chapterReader = new StreamReader(archive.GetEntry("EPUB/chapter-0100.xhtml")!.Open());
            Assert.Contains("OCR page 100", await chapterReader.ReadToEndAsync(), StringComparison.Ordinal);
        }
        var markdownPath = Path.Combine(directory.Path, "pages.md");
        await new MarkdownDocumentWriter().WriteAsync(document, markdownPath);
        var markdown = await File.ReadAllTextAsync(markdownPath);
        Assert.True(markdown.IndexOf("OCR page 001", StringComparison.Ordinal) < markdown.IndexOf("OCR page 100", StringComparison.Ordinal));
        var exported = Directory.GetFiles(directory.Path, "page-*.png", SearchOption.AllDirectories);
        Assert.Equal(100, exported.Length);
        Assert.Equal(image, await File.ReadAllBytesAsync(exported[0]));
        File.Delete(path);
    }

    [Fact]
    public async Task TextOnlyPages_KeepOcrAndImagePlaceholdersWithoutResources()
    {
        using var directory = new TemporaryDirectory();
        DecodedImagePage[] pages =
        [
            new([], 10, 20, " Recognized text ", "source.tif", 0),
            new([], 10, 20, null, "source.tif", 1)
        ];
        var document = await new ImageDocumentBuilder().BuildAsync(Enumerate(pages), "Text", "en", fixedLayout: false);
        Assert.Empty(document.Resources);
        Assert.Equal("Recognized text", Assert.IsType<NormalizedParagraph>(document.Sections[0].Blocks[0]).Text);
        Assert.IsType<NormalizedImage>(Assert.Single(document.Sections[1].Blocks));
        var path = Path.Combine(directory.Path, "result.txt");
        await new PlainTextDocumentWriter().WriteAsync(document, path);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("Recognized text", text, StringComparison.Ordinal);
        Assert.Contains("source.tif", text, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task BuilderCancellation_DisposesPageEnumerator()
    {
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        async IAsyncEnumerable<DecodedImagePage> Pages([EnumeratorCancellation] CancellationToken token = default)
        {
            try
            {
                await Task.Yield();
                yield return new DecodedImagePage([], 10, 20, "first", "scan.tif", 0);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                yield return new DecodedImagePage([], 10, 20, "second", "scan.tif", 1);
            }
            finally { disposed = true; }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ImageDocumentBuilder().BuildAsync(Pages(), "Text", "en", false, cancellation.Token));
        Assert.True(disposed);
    }

    [Fact]
    public async Task ImagePdf_FilePagesMatchMemoryPagesAndReleaseSource()
    {
        using var directory = new TemporaryDirectory();
        var image = CreatePng();
        var source = Path.Combine(directory.Path, "page.png");
        await File.WriteAllBytesAsync(source, image);
        var memoryPage = new DecodedImagePage(image, 2, 1, null, "page.png", 0);
        var filePage = memoryPage with { PngContent = [], PngPath = source };
        var memoryPath = Path.Combine(directory.Path, "memory.pdf");
        var streamedPath = Path.Combine(directory.Path, "streamed.pdf");
        await new ImagePdfWriter().WriteAsync(new[] { memoryPage, memoryPage }, memoryPath);
        await new ImagePdfWriter().WriteAsync(Enumerate([filePage, filePage]), streamedPath);

        using var expected = UglyToad.PdfPig.PdfDocument.Open(memoryPath);
        using var actual = UglyToad.PdfPig.PdfDocument.Open(streamedPath);
        Assert.Equal(2, actual.NumberOfPages);
        for (var index = 1; index <= 2; index++)
        {
            Assert.Equal(expected.GetPage(index).Width, actual.GetPage(index).Width);
            Assert.Equal(expected.GetPage(index).Height, actual.GetPage(index).Height);
        }
        File.Delete(source);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ImagePdfCancellation_DoesNotCommitOrLeakStagingOutput()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        async IAsyncEnumerable<DecodedImagePage> Pages([EnumeratorCancellation] CancellationToken token = default)
        {
            try
            {
                await Task.Yield();
                yield return new DecodedImagePage(CreatePng(), 2, 1, null, "page.png", 0);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            finally { disposed = true; }
        }

        var path = Path.Combine(directory.Path, "cancelled.pdf");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ImagePdfWriter().WriteAsync(Pages(), path, cancellation.Token));
        Assert.True(disposed);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task MissingResource_DoesNotLeaveMarkdownStagingOrDeleteBorrowedFiles()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.png");
        await File.WriteAllBytesAsync(source, CreatePng());
        var pages = new[]
        {
            new DecodedImagePage([], 2, 1, null, "first.png", 0) { PngPath = source },
            new DecodedImagePage([], 2, 1, null, "missing.png", 0) { PngPath = Path.Combine(directory.Path, "missing.png") }
        };
        var document = new ImageDocumentBuilder().Build(pages, "Missing", "en", true);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new MarkdownDocumentWriter().WriteAsync(document, Path.Combine(directory.Path, "missing.md")));
        Assert.Equal(new[] { source }, Directory.GetFileSystemEntries(directory.Path));
    }

    private static async IAsyncEnumerable<DecodedImagePage> Enumerate(
        IEnumerable<DecodedImagePage> pages, [EnumeratorCancellation] CancellationToken token = default)
    {
        foreach (var page in pages)
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return page;
        }
    }

    private static byte[] CreatePng()
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        header[9] = 2; // RGB red and green pixels.
        WriteChunk(png, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(new byte[] { 0, 255, 0, 0, 0, 255, 0 });
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] content)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, content.Length);
        stream.Write(number);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(content);
        var crc = uint.MaxValue;
        foreach (var value in typeBytes.Concat(content))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }
}