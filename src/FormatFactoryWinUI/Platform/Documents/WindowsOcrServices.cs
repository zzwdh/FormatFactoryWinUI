using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed class WindowsPdfOcrService : IPdfOcrLayoutService
{
    public async Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
        string pdfPath,
        IReadOnlyList<int> zeroBasedPageIndexes,
        string? languageTag,
        string? password,
        CancellationToken cancellationToken)
    {
        var pages = await RecognizePageLayoutsAsync(pdfPath, zeroBasedPageIndexes, languageTag, password, cancellationToken);
        return pages.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
    }

    public async Task<IReadOnlyDictionary<int, OcrPageLayout>> RecognizePageLayoutsAsync(
        string pdfPath, IReadOnlyList<int> zeroBasedPageIndexes, string? languageTag,
        string? password, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(LocalizationService.T("ConversionUiPdfOcrWindowsOnly"));
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.PdfOcr)
        { Stage = RuntimeFailureStage.OcrDecode, Phase = PlatformDiagnosticPhase.OcrEngine };
        var engine = diagnostic.Observe(() => CreateEngine(languageTag));
        diagnostic.Phase = PlatformDiagnosticPhase.OcrOpen;
        var file = await diagnostic.ObserveAsync(() => StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask(cancellationToken));
        var document = string.IsNullOrEmpty(password)
            ? await diagnostic.ObserveAsync(() => PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken))
            : await diagnostic.ObserveAsync(() => PdfDocument.LoadFromFileAsync(file, password).AsTask(cancellationToken));
        diagnostic.ResultCount = document.PageCount <= int.MaxValue ? (int)document.PageCount : null;
        var result = new Dictionary<int, OcrPageLayout>();
        foreach (var index in zeroBasedPageIndexes.Distinct().Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < 0 || index >= document.PageCount) continue;
            diagnostic.Phase = PlatformDiagnosticPhase.OcrOpen;
            diagnostic.Page = index;
            using var page = diagnostic.Observe(() => document.GetPage((uint)index));
            using var stream = new InMemoryRandomAccessStream();
            var options = FitRenderOptions(page.Size.Width, page.Size.Height);
            diagnostic.Phase = PlatformDiagnosticPhase.OcrRender;
            diagnostic.Page = index;
            diagnostic.Width = options.DestinationWidth;
            diagnostic.Height = options.DestinationHeight;
            await diagnostic.ObserveAsync(() => page.RenderToStreamAsync(stream, options).AsTask(cancellationToken));
            stream.Seek(0);
            diagnostic.Phase = PlatformDiagnosticPhase.OcrBitmapDecode;
            var decoder = await diagnostic.ObserveAsync(() => BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken));
            using var bitmap = await DecodeForOcrAsync(decoder, cancellationToken);
            var recognized = await RecognizeWithDiagnosticsAsync(engine, bitmap, cancellationToken, index);
            result[index] = CreateLayout(recognized, bitmap.PixelWidth, bitmap.PixelHeight);
        }
        return result;
    }

    internal static Task<SoftwareBitmap> DecodeForOcrAsync(BitmapDecoder decoder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maximum = OcrEngine.MaxImageDimension;
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.PdfOcr)
        {
            Stage = RuntimeFailureStage.OcrDecode, Phase = PlatformDiagnosticPhase.OcrBitmapDecode,
            Width = decoder.PixelWidth, Height = decoder.PixelHeight, MaximumDimension = maximum
        };
        if (decoder.PixelWidth <= maximum && decoder.PixelHeight <= maximum)
            return diagnostic.ObserveAsync(() => decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore)
                .AsTask(cancellationToken));
        // Bound the actual rasterized pixels: a PDF render may exceed its requested dimensions.
        var scale = Math.Min((double)maximum / decoder.PixelWidth, (double)maximum / decoder.PixelHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Floor(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Floor(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        return diagnostic.ObserveAsync(() => decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken));
    }

    internal static Task<OcrResult> RecognizeWithDiagnosticsAsync(OcrEngine engine, SoftwareBitmap bitmap,
        CancellationToken cancellationToken, int? page = null,
        PlatformDiagnosticKind kind = PlatformDiagnosticKind.PdfOcr)
    {
        var diagnostic = new PlatformDiagnosticOperation(kind)
        {
            Stage = RuntimeFailureStage.OcrRecognize, Phase = PlatformDiagnosticPhase.OcrRecognize,
            Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, MaximumDimension = OcrEngine.MaxImageDimension, Page = page
        };
        return diagnostic.ObserveAsync(() => engine.RecognizeAsync(bitmap).AsTask(cancellationToken));
    }
    internal static OcrPageLayout CreateLayout(OcrResult result, int width, int height) =>
        new(result.Text, width, height, result.Lines.SelectMany(line => line.Words)
            .Select(word => new OcrWordBox(word.Text, word.BoundingRect.X, word.BoundingRect.Y,
                word.BoundingRect.Width, word.BoundingRect.Height)).ToArray());

    internal static OcrEngine CreateEngine(string? languageTag)
    {
        return OcrLanguagePolicy.Resolve(
            languageTag,
            requestedTag => OcrEngine.TryCreateFromLanguage(new Language(requestedTag)),
            OcrEngine.TryCreateFromUserProfileLanguages);
    }

    private static PdfPageRenderOptions FitRenderOptions(double width, double height)
    {
        const double preferredScale = 2;
        var scale = Math.Min(preferredScale, OcrEngine.MaxImageDimension / Math.Max(width, height));
        scale = Math.Max(scale, 0.1);
        return new PdfPageRenderOptions
        {
            DestinationWidth = Math.Max(1, (uint)Math.Round(width * scale)),
            DestinationHeight = Math.Max(1, (uint)Math.Round(height * scale))
        };
    }
}

public sealed class WindowsPdfPageContentService
{
    // Compatibility entry point for callers that explicitly need all PNGs in memory.
    public async Task<IReadOnlyList<DecodedImagePage>> RenderAsync(
        string pdfPath,
        bool recognizeText,
        string? languageTag,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var pages = new List<DecodedImagePage>();
        await foreach (var page in RenderPagesAsync(pdfPath, recognizeText, languageTag, password,
            imageDirectory: null, cancellationToken: cancellationToken).ConfigureAwait(false))
            pages.Add(page);
        return pages;
    }

    public async IAsyncEnumerable<DecodedImagePage> RenderPagesAsync(
        string pdfPath,
        bool recognizeText,
        string? languageTag,
        string? password,
        string? imageDirectory,
        bool includeImageContent = true,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(LocalizationService.T("ConversionUiPdfRenderingWindowsOnly"));
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.PdfOcr)
        { Stage = RuntimeFailureStage.OcrDecode, Phase = PlatformDiagnosticPhase.OcrEngine };
        var engine = recognizeText ? diagnostic.Observe(() => WindowsPdfOcrService.CreateEngine(languageTag)) : null;
        diagnostic.Phase = PlatformDiagnosticPhase.OcrOpen;
        var file = await diagnostic.ObserveAsync(() => StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask(cancellationToken));
        var document = string.IsNullOrEmpty(password)
            ? await diagnostic.ObserveAsync(() => PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken))
            : await diagnostic.ObserveAsync(() => PdfDocument.LoadFromFileAsync(file, password).AsTask(cancellationToken));
        diagnostic.ResultCount = document.PageCount <= int.MaxValue ? (int)document.PageCount : null;
        if (document.PageCount == 0) throw new InvalidDataException(LocalizationService.T("ConversionUiPdfHasNoPages"));
        for (uint index = 0; index < document.PageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostic.Phase = PlatformDiagnosticPhase.OcrOpen;
            diagnostic.Page = index <= int.MaxValue ? (int)index : null;
            using var page = diagnostic.Observe(() => document.GetPage(index));
            using var rendered = new InMemoryRandomAccessStream();
            var scale = Math.Min(2d, OcrEngine.MaxImageDimension / Math.Max(page.Size.Width, page.Size.Height));
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = Math.Max(1, (uint)Math.Round(page.Size.Width * scale)),
                DestinationHeight = Math.Max(1, (uint)Math.Round(page.Size.Height * scale))
            };
            diagnostic.Phase = PlatformDiagnosticPhase.OcrRender;
            diagnostic.Page = index <= int.MaxValue ? (int)index : null;
            diagnostic.Width = options.DestinationWidth;
            diagnostic.Height = options.DestinationHeight;
            await diagnostic.ObserveAsync(() => page.RenderToStreamAsync(rendered, options).AsTask(cancellationToken));
            rendered.Seek(0);
            diagnostic.Phase = PlatformDiagnosticPhase.OcrBitmapDecode;
            var decoder = await diagnostic.ObserveAsync(() => BitmapDecoder.CreateAsync(rendered).AsTask(cancellationToken));
            using var bitmap = engine is not null && !includeImageContent
                ? await WindowsPdfOcrService.DecodeForOcrAsync(decoder, cancellationToken)
                : await diagnostic.ObserveAsync(() => decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore)
                    .AsTask(cancellationToken));
            OcrPageLayout? recognized = null;
            if (engine is not null)
            {
                if (bitmap.PixelWidth > OcrEngine.MaxImageDimension || bitmap.PixelHeight > OcrEngine.MaxImageDimension)
                {
                    using var ocrBitmap = await WindowsPdfOcrService.DecodeForOcrAsync(decoder, cancellationToken);
                    var layout = WindowsPdfOcrService.CreateLayout(
                        await WindowsPdfOcrService.RecognizeWithDiagnosticsAsync(engine, ocrBitmap, cancellationToken, diagnostic.Page), ocrBitmap.PixelWidth, ocrBitmap.PixelHeight);
                    // Keep exported image pixels and express words in that same coordinate space.
                    recognized = new(layout.Text, bitmap.PixelWidth, bitmap.PixelHeight,
                        layout.MapToPage(bitmap.PixelWidth, bitmap.PixelHeight));
                }
                else
                    recognized = WindowsPdfOcrService.CreateLayout(
                        await WindowsPdfOcrService.RecognizeWithDiagnosticsAsync(engine, bitmap, cancellationToken, diagnostic.Page), bitmap.PixelWidth, bitmap.PixelHeight);
            }
            yield return await WindowsPageImageEncoder.CreatePageAsync(bitmap, recognized,
                Path.GetFileName(pdfPath), index, imageDirectory, includeImageContent, cancellationToken);
        }
    }
}

public sealed class WindowsImageContentService
{
    // Compatibility entry point; the conversion pipeline uses DecodePagesAsync.
    public async Task<IReadOnlyList<DecodedImagePage>> DecodeAsync(
        IReadOnlyList<string> inputPaths,
        bool recognizeText,
        string? languageTag,
        CancellationToken cancellationToken = default)
    {
        var pages = new List<DecodedImagePage>();
        await foreach (var page in DecodePagesAsync(inputPaths, recognizeText, languageTag,
            imageDirectory: null, cancellationToken: cancellationToken).ConfigureAwait(false))
            pages.Add(page);
        return pages;
    }

    public async IAsyncEnumerable<DecodedImagePage> DecodePagesAsync(
        IReadOnlyList<string> inputPaths,
        bool recognizeText,
        string? languageTag,
        string? imageDirectory,
        bool includeImageContent = true,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        if (inputPaths.Count == 0) throw new ArgumentException(LocalizationService.T("ConversionUiAtLeastOneImageRequired"), nameof(inputPaths));
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.ImageOcr)
        { Stage = RuntimeFailureStage.OcrDecode, Phase = PlatformDiagnosticPhase.OcrEngine };
        var engine = recognizeText ? diagnostic.Observe(() => WindowsPdfOcrService.CreateEngine(languageTag)) : null;
        foreach (var inputPath in inputPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(inputPath);
            if (!File.Exists(path)) throw new FileNotFoundException(LocalizationService.T("ConversionUiImageNotFound"), path);
            diagnostic.Phase = PlatformDiagnosticPhase.OcrOpen;
            var file = await diagnostic.ObserveAsync(() => StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken));
            using var source = await diagnostic.ObserveAsync(() => file.OpenReadAsync().AsTask(cancellationToken));
            diagnostic.Phase = PlatformDiagnosticPhase.OcrBitmapDecode;
            var decoder = await diagnostic.ObserveAsync(() => BitmapDecoder.CreateAsync(source).AsTask(cancellationToken));
            for (uint frameIndex = 0; frameIndex < decoder.FrameCount; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = await diagnostic.ObserveAsync(() => decoder.GetFrameAsync(frameIndex).AsTask(cancellationToken));
                var transform = FitTransform(frame.PixelWidth, frame.PixelHeight, recognizeText);
                var frameDiagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.ImageOcr)
                {
                    Stage = RuntimeFailureStage.OcrDecode, Phase = PlatformDiagnosticPhase.OcrBitmapDecode,
                    Width = frame.PixelWidth, Height = frame.PixelHeight,
                    MaximumDimension = recognizeText ? OcrEngine.MaxImageDimension : null,
                    Page = frameIndex <= int.MaxValue ? (int)frameIndex : null,
                    ResultCount = decoder.FrameCount <= int.MaxValue ? (int)decoder.FrameCount : null
                };
                using var bitmap = await frameDiagnostic.ObserveAsync(() => frame.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken));
                var recognized = engine is null
                    ? null
                    : WindowsPdfOcrService.CreateLayout(await WindowsPdfOcrService.RecognizeWithDiagnosticsAsync(engine, bitmap, cancellationToken,
                        frameDiagnostic.Page, PlatformDiagnosticKind.ImageOcr),
                        bitmap.PixelWidth, bitmap.PixelHeight);
                yield return await WindowsPageImageEncoder.CreatePageAsync(bitmap, recognized,
                    Path.GetFileName(path), frameIndex, imageDirectory, includeImageContent, cancellationToken);
            }
        }
    }

    private static BitmapTransform FitTransform(uint width, uint height, bool forOcr)
    {
        var transform = new BitmapTransform();
        if (!forOcr || Math.Max(width, height) <= OcrEngine.MaxImageDimension) return transform;
        var scale = (double)OcrEngine.MaxImageDimension / Math.Max(width, height);
        transform.ScaledWidth = Math.Max(1, (uint)Math.Floor(width * scale));
        transform.ScaledHeight = Math.Max(1, (uint)Math.Floor(height * scale));
        transform.InterpolationMode = BitmapInterpolationMode.Fant;
        return transform;
    }
}

internal static class WindowsPageImageEncoder
{
    public static async Task<DecodedImagePage> CreatePageAsync(
        SoftwareBitmap bitmap, OcrPageLayout? recognized, string sourceName, uint frameIndex,
        string? imageDirectory, bool includeImageContent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var page = new DecodedImagePage([], (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight,
            recognized?.Text, sourceName, frameIndex) { OcrLayout = recognized };
        // TXT consumes OCR and the existing image placeholders, without encoding pixels.
        if (!includeImageContent) return page;
        if (imageDirectory is not null)
        {
            Directory.CreateDirectory(imageDirectory);
            var path = Path.Combine(imageDirectory, $"{Guid.NewGuid():N}.png");
            try
            {
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 81920, FileOptions.Asynchronous);
                using var stream = file.AsRandomAccessStream();
                await EncodeAsync(bitmap, stream, token);
                return page with { PngPath = path };
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                throw;
            }
        }

        using var memory = new InMemoryRandomAccessStream();
        await EncodeAsync(bitmap, memory, token);
        memory.Seek(0);
        using var managed = memory.AsStreamForRead();
        using var content = new MemoryStream();
        await managed.CopyToAsync(content, token);
        return page with { PngContent = content.ToArray() };
    }

    private static async Task EncodeAsync(SoftwareBitmap bitmap, IRandomAccessStream stream, CancellationToken token)
    {
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(token);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(token);
    }
}
