using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FormatFactoryWinUI.Documents;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckPdfOcrDimensionsAsync()
    {
        var root = Path.Combine(_runRoot, "ocr-dimension-fixtures");
        Directory.CreateDirectory(root);
        var wide = Path.Combine(root, "wide.pdf");
        var tall = Path.Combine(root, "tall.pdf");
        var ordinary = Path.Combine(root, "ordinary.pdf");
        var multiple = Path.Combine(root, "multiple.pdf");
        WriteDimensionPdf(wide, (13000, 100, "BOUNDARY"));
        WriteDimensionPdf(tall, (10, 13000, ""));
        WriteDimensionPdf(ordinary, (595, 842, "ORDINARY PAGE"));
        WriteDimensionPdf(multiple, (595, 842, "FIRST PAGE"), (595, 842, "SECOND PAGE"));
        var sourceHashes = new[] { wide, tall, ordinary, multiple }
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var maximum = OcrEngine.MaxImageDimension;
        var evidence = new List<object>();

        await CheckNativeAsync("PDF OCR bounds actual wide and tall rasterized pixels", async () =>
        {
            foreach (var path in new[] { wide, tall })
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var document = await PdfDocument.LoadFromFileAsync(file);
                Require(document.PageCount == 1, "The legal PDF fixture did not retain its page.");
                using var page = document.GetPage(0);
                var method = typeof(WindowsPdfOcrService).GetMethod("FitRenderOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
                var options = (PdfPageRenderOptions)method.Invoke(null, new object[] { page.Size.Width, page.Size.Height })!;
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, options);
                stream.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                using var bitmap = await WindowsPdfOcrService.DecodeForOcrAsync(decoder, default);
                Require(bitmap.PixelWidth <= maximum && bitmap.PixelHeight <= maximum &&
                    bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Actual OCR bitmap exceeds the system pixel limit.");
                using var original = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                Require(bitmap.BitmapPixelFormat == original.BitmapPixelFormat && bitmap.BitmapAlphaMode == original.BitmapAlphaMode,
                    $"OCR decode changed actual pixel/alpha from {original.BitmapPixelFormat}/{original.BitmapAlphaMode} to {bitmap.BitmapPixelFormat}/{bitmap.BitmapAlphaMode}.");
                var engine = WindowsPdfOcrService.CreateEngine("en-US");
                var recognized = await engine.RecognizeAsync(bitmap);
                var layouts = await new WindowsPdfOcrService().RecognizePageLayoutsAsync(path, new[] { 0 }, "en-US", null, default);
                Require(layouts.Count == 1 && layouts[0].PixelWidth == bitmap.PixelWidth && layouts[0].PixelHeight == bitmap.PixelHeight,
                    "The real OCR service did not return the recognizer bitmap coordinate space.");
                if (path == wide) Require(recognized.Text.Contains("BOUNDARY", StringComparison.OrdinalIgnoreCase),
                    "The wide page content was lost or cropped before OCR.");
                evidence.Add(new { fixture = path == wide ? "wide" : "tall", maximum,
                    pageWidth = page.Size.Width, pageHeight = page.Size.Height,
                    requestedWidth = options.DestinationWidth, requestedHeight = options.DestinationHeight,
                    rasterWidth = decoder.PixelWidth, rasterHeight = decoder.PixelHeight,
                    ocrWidth = bitmap.PixelWidth, ocrHeight = bitmap.PixelHeight, recognized = true,
                    pixelFormat = bitmap.BitmapPixelFormat.ToString(), alphaMode = bitmap.BitmapAlphaMode.ToString() });
            }
        });

        await CheckNativeAsync("PDF content OCR keeps original image pixels and maps real word boxes to them", async () =>
        {
            var service = new WindowsPdfPageContentService();
            var withoutOcr = (await service.RenderAsync(wide, false, null, null)).Single();
            var withOcr = (await service.RenderAsync(wide, true, "en-US", null)).Single();
            Require(withOcr.Width == withoutOcr.Width && withOcr.Height == withoutOcr.Height,
                "Enabling OCR reduced the exported image dimensions.");
            Require(SHA256.HashData(withOcr.PngContent).SequenceEqual(SHA256.HashData(withoutOcr.PngContent)),
                "Enabling OCR changed the exported PNG pixels or encoding.");
            var originalLayout = (await new WindowsPdfOcrService().RecognizePageLayoutsAsync(wide, new[] { 0 }, "en-US", null, default))[0];
            var mapped = originalLayout.MapToPage(withOcr.Width, withOcr.Height);
            var actual = withOcr.OcrLayout!;
            Require(actual is not null && actual.PixelWidth == withOcr.Width && actual.PixelHeight == withOcr.Height,
                "Word boxes and exported image do not share one pixel coordinate space.");
            Require(mapped.Count > 0 && actual!.Words.Count == mapped.Count, "Real OCR word boxes were lost.");
            for (var index = 0; index < mapped.Count; index++)
            {
                var expected = mapped[index];
                var word = actual!.Words[index];
                Require(word.Text == expected.Text && Math.Abs(word.X - expected.X) < .01 && Math.Abs(word.Y - expected.Y) < .01 &&
                    Math.Abs(word.Width - expected.Width) < .01 && Math.Abs(word.Height - expected.Height) < .01,
                    "OCR word boxes were not scaled back to the actual exported pixels.");
                Require(word.X >= 0 && word.Y >= 0 && word.X + word.Width <= withOcr.Width + .01 &&
                    word.Y + word.Height <= withOcr.Height + .01, "Mapped word exceeds the image bounds.");
            }
            evidence.Add(new { fixture = "image-with-ocr", imageWidth = withOcr.Width, imageHeight = withOcr.Height,
                ocrWidth = originalLayout.PixelWidth, ocrHeight = originalLayout.PixelHeight,
                layoutWidth = actual!.PixelWidth, layoutHeight = actual.PixelHeight,
                pngBytesIdentical = true, mappedWordCount = mapped.Count,
                xScale = withOcr.Width / originalLayout.PixelWidth, yScale = withOcr.Height / originalLayout.PixelHeight,
                originalWord = originalLayout.Words[0], mappedWord = actual.Words[0] });
        });

        await CheckNativeAsync("PDF OCR-only output uses bounded pixels without encoding an image", async () =>
        {
            var pages = new List<DecodedImagePage>();
            await foreach (var page in new WindowsPdfPageContentService().RenderPagesAsync(wide, true, "en-US", null,
                null, includeImageContent: false)) pages.Add(page);
            var result = pages.Single();
            Require(result.Width <= maximum && result.Height <= maximum && result.OcrLayout is not null &&
                result.OcrLayout.PixelWidth == result.Width && result.OcrLayout.PixelHeight == result.Height,
                "OCR-only output did not retain its bounded coordinate space.");
            Require(result.PngContent.Length == 0 && result.PngPath is null &&
                result.RecognizedText?.Contains("BOUNDARY", StringComparison.OrdinalIgnoreCase) == true,
                "OCR-only output encoded an image or lost text.");
            evidence.Add(new { fixture = "ocr-only", result.Width, result.Height, imageBytes = result.PngContent.Length });
        });

        await CheckNativeAsync("Ordinary PDF OCR retains its original decode pixels and layout", async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(ordinary);
            var document = await PdfDocument.LoadFromFileAsync(file);
            using var page = document.GetPage(0);
            var method = typeof(WindowsPdfOcrService).GetMethod("FitRenderOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
            var options = (PdfPageRenderOptions)method.Invoke(null, new object[] { page.Size.Width, page.Size.Height })!;
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var original = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            using var unchanged = await WindowsPdfOcrService.DecodeForOcrAsync(decoder, default);
            Require(original.PixelWidth == unchanged.PixelWidth && original.PixelHeight == unchanged.PixelHeight &&
                BitmapDigest(original).SequenceEqual(BitmapDigest(unchanged)), "Ordinary OCR pixels were resampled.");
            var result = (await new WindowsPdfOcrService().RecognizePageLayoutsAsync(ordinary, new[] { 0 }, "en-US", null, default))[0];
            Require(result.PixelWidth == original.PixelWidth && result.PixelHeight == original.PixelHeight &&
                result.Text.Contains("ORDINARY PAGE", StringComparison.OrdinalIgnoreCase), "Ordinary page layout changed or lost text.");
            evidence.Add(new { fixture = "ordinary", unchanged.PixelWidth, unchanged.PixelHeight, decodedPixelsIdentical = true });
        });

        await CheckNativeAsync("PDF OCR preserves multiple pages, requested indexes, image bytes and page text", async () =>
        {
            var layouts = await new WindowsPdfOcrService().RecognizePageLayoutsAsync(multiple,
                new[] { 1, 0, 1, -1, 99 }, "en-US", null, default);
            Require(layouts.Count == 2 && layouts.ContainsKey(0) && layouts.ContainsKey(1) &&
                layouts[0].Text.Contains("FIRST PAGE", StringComparison.OrdinalIgnoreCase) &&
                layouts[1].Text.Contains("SECOND PAGE", StringComparison.OrdinalIgnoreCase), "PDF OCR lost or reordered a requested page.");
            var service = new WindowsPdfPageContentService();
            var plain = await service.RenderAsync(multiple, false, null, null);
            var recognized = await service.RenderAsync(multiple, true, "en-US", null);
            Require(recognized.Count == 2 && recognized[0].FrameIndex == 0 && recognized[1].FrameIndex == 1,
                "Image content lost page indexes.");
            for (var index = 0; index < 2; index++)
                Require(recognized[index].Width == plain[index].Width && recognized[index].Height == plain[index].Height &&
                    SHA256.HashData(recognized[index].PngContent).SequenceEqual(SHA256.HashData(plain[index].PngContent)),
                    "OCR changed an exported page image.");
            evidence.Add(new { fixture = "multiple", pageCount = recognized.Count, requestedIndexes = layouts.Keys.ToArray(),
                pngBytesIdentical = true, firstTextRetained = true, secondTextRetained = true });
        });

        await CheckNativeAsync("PDF OCR cancellation propagates before work and between streamed pages", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var stopped = false;
            try { await new WindowsPdfOcrService().RecognizePageLayoutsAsync(ordinary, new[] { 0 }, "en-US", null, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Require(stopped, "Pre-cancelled OCR did not propagate cancellation.");
            using var betweenPages = new CancellationTokenSource();
            await using var enumerator = new WindowsPdfPageContentService().RenderPagesAsync(multiple, true, "en-US", null,
                null, includeImageContent: false, cancellationToken: betweenPages.Token).GetAsyncEnumerator();
            Require(await enumerator.MoveNextAsync() && enumerator.Current.FrameIndex == 0, "First streamed page was lost.");
            betweenPages.Cancel();
            stopped = false;
            try { await enumerator.MoveNextAsync(); } catch (OperationCanceledException) { stopped = true; }
            Require(stopped, "Streamed OCR ignored cancellation before the next page.");
        });

        Check("PDF OCR regression preserves all original fixture files", () =>
        {
            foreach (var pair in sourceHashes) Require(pair.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pair.Key))),
                "OCR modified a source PDF.");
        });
        await File.WriteAllTextAsync(Path.Combine(_runRoot, "ocr-dimensions-evidence.json"),
            JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, maximum, evidence }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static byte[] BitmapDigest(SoftwareBitmap bitmap)
    {
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return SHA256.HashData(pixels);
    }

    private static void WriteDimensionPdf(string path, params (double Width, double Height, string Text)[] pages)
    {
        var data = new StringBuilder("%PDF-1.7\n");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pages.Length).Select(index => $"{3 + 2 * index} 0 R")) +
                $"] /Count {pages.Length} >>"
        };
        var fontId = 3 + pages.Length * 2;
        foreach (var page in pages)
        {
            var contentId = objects.Count + 2;
            objects.Add(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {contentId} 0 R >>"));
            var content = page.Text.Length == 0 ? "" : FormattableString.Invariant($"BT /F1 40 Tf 30 30 Td ({page.Text}) Tj ET\n");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(data.ToString()));
            data.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(data.ToString());
        data.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) data.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        data.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        File.WriteAllText(path, data.ToString(), Encoding.ASCII);
    }
}
