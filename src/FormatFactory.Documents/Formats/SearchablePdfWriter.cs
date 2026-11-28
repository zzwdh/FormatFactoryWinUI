using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed class SearchablePdfWriter
{
    private static readonly object FontLock = new();
    private readonly PdfArtifactValidator _validator = new();

    // Compatibility for plain-text callers. Production OCR uses WriteLayoutsAsync.
    public Task WriteAsync(string inputPath, IReadOnlyList<string> pageTexts, string outputPath,
        string? password = null, CancellationToken cancellationToken = default) =>
        WriteCoreAsync(inputPath, outputPath, password, cancellationToken, (index, width, height) =>
        {
            if (index >= pageTexts.Count || string.IsNullOrWhiteSpace(pageTexts[index])) return null;
            var words = pageTexts[index].Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
                .Select((line, row) => new OcrWordBox(line, 3, 3 + row * 8, Math.Min(width - 6, Math.Max(1, line.Length * 4)), 8))
                .Where(word => word.Y + word.Height < height).ToArray();
            return new OcrPageLayout(pageTexts[index], width, height, words);
        });

    public Task WriteLayoutsAsync(string inputPath, IReadOnlyDictionary<int, OcrPageLayout> pageLayouts,
        string outputPath, string? password = null, CancellationToken cancellationToken = default) =>
        WriteCoreAsync(inputPath, outputPath, password, cancellationToken,
            (index, _, _) => pageLayouts.GetValueOrDefault(index));

    private async Task WriteCoreAsync(string inputPath, string outputPath, string? password,
        CancellationToken cancellationToken, Func<int, double, double, OcrPageLayout?> layoutForPage)
    {
        var input = Path.GetFullPath(inputPath);
        var output = Path.GetFullPath(outputPath);
        if (!File.Exists(input)) throw new FileNotFoundException(LocalizationService.T("ConversionUiPdfDocumentNotFound"), input);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(() => Write(input, layoutForPage, temporary, password, cancellationToken), cancellationToken);
            var validation = await _validator.ValidateAsync(
                new Models.ConversionArtifact("searchable-pdf", "pdf", temporary, true), cancellationToken);
            if (!validation.IsValid) throw new InvalidDataException(validation.Message ?? LocalizationService.T("ConversionUiGeneratedPdfInvalid"));
            cancellationToken.ThrowIfCancellationRequested();
            OutputRecoveryService.Publish(temporary, output, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Write(string inputPath, Func<int, double, double, OcrPageLayout?> layoutForPage,
        string outputPath, string? password, CancellationToken token)
    {
        using var source = string.IsNullOrEmpty(password)
            ? PdfReader.Open(inputPath, PdfDocumentOpenMode.Import)
            : PdfReader.Open(inputPath, password, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        for (var index = 0; index < source.PageCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var page = output.AddPage(source.Pages[index]);
            var layout = layoutForPage(index, page.Width.Point, page.Height.Point);
            if (layout is null || layout.Words.Count == 0) continue;
            using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var viewport = PdfOcrViewport.ForPage(page);
            // WinRT renders the visible CropBox after /Rotate. XGraphics appends in
            // the unrotated media canvas, so undo that viewport transform explicitly.
            graphics.MultiplyTransform(viewport.ToCanvas);
            DrawTextLayer(graphics, layout, viewport.Width, viewport.Height, token);
        }
        token.ThrowIfCancellationRequested();
        output.Save(outputPath);
    }

    internal static void DrawTextLayer(XGraphics graphics, OcrPageLayout layout,
        double width, double height, CancellationToken token)
    {
        EnsureFontResolver();
        var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
        foreach (var word in layout.MapToPage(width, height))
        {
            token.ThrowIfCancellationRequested();
            var runs = UnicodePdfFontResolver.Instance.ResolveRuns(word.Text);
            var measured = runs.Select(run =>
            {
                var font = new XFont(run.FontName, word.Height, XFontStyleEx.Regular,
                    new XPdfFontOptions(PdfFontEncoding.Unicode));
                return (run.Text, Font: font, Size: graphics.MeasureString(run.Text, font));
            }).ToArray();
            var naturalWidth = measured.Sum(run => run.Size.Width);
            var naturalHeight = measured.Length == 0 ? 0 : measured.Max(run => run.Size.Height);
            if (naturalWidth <= 0 || naturalHeight <= 0) continue;
            var state = graphics.Save();
            try
            {
                graphics.TranslateTransform(word.X, word.Y);
                graphics.ScaleTransform(word.Width / naturalWidth, word.Height / naturalHeight);
                var x = 0d;
                foreach (var run in measured)
                {
                    graphics.DrawString(run.Text, run.Font, invisible, new XRect(x, 0, run.Size.Width, naturalHeight), XStringFormats.TopLeft);
                    x += run.Size.Width;
                }
            }
            finally { graphics.Restore(state); }
        }
    }

    private static void EnsureFontResolver()
    {
        lock (FontLock)
        {
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = UnicodePdfFontResolver.Instance;
            else if (!ReferenceEquals(GlobalFontSettings.FontResolver, UnicodePdfFontResolver.Instance) &&
                GlobalFontSettings.FallbackFontResolver is null)
                GlobalFontSettings.FallbackFontResolver = UnicodePdfFontResolver.Instance;
        }
    }
}

internal sealed record PdfOcrViewport(double Width, double Height, XMatrix ToCanvas)
{
    public static PdfOcrViewport ForPage(PdfPage page)
    {
        var media = page.MediaBox;
        var crop = page.CropBox.IsZero ? media : page.CropBox;
        var left = Math.Max(media.X1, crop.X1);
        var bottom = Math.Max(media.Y1, crop.Y1);
        var right = Math.Min(media.X2, crop.X2);
        var top = Math.Min(media.Y2, crop.Y2);
        var width = right - left;
        var height = top - bottom;
        if (width <= 0 || height <= 0) throw new InvalidDataException("PDF crop box is outside its media box.");
        var canvasHeight = page.Height.Point;
        var rotation = ((page.Rotate % 360) + 360) % 360;
        return rotation switch
        {
            0 => new(width, height, new XMatrix(1, 0, 0, 1, left, canvasHeight - top)),
            90 => new(height, width, new XMatrix(0, -1, 1, 0, left, canvasHeight - bottom)),
            180 => new(width, height, new XMatrix(-1, 0, 0, -1, right, canvasHeight - bottom)),
            270 => new(height, width, new XMatrix(0, 1, -1, 0, right, canvasHeight - top)),
            _ => throw new InvalidDataException("PDF page rotation must be a multiple of 90 degrees.")
        };
    }
}
