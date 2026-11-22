namespace FormatFactoryWinUI.Documents;

/// <summary>OCR geometry in the rendered, correctly oriented bitmap's top-left coordinate space.</summary>
public sealed record OcrWordBox(string Text, double X, double Y, double Width, double Height);

public sealed record OcrPageLayout(string Text, double PixelWidth, double PixelHeight,
    IReadOnlyList<OcrWordBox> Words)
{
    public IReadOnlyList<OcrWordBox> MapToPage(double width, double height)
    {
        if (!double.IsFinite(PixelWidth) || !double.IsFinite(PixelHeight) ||
            PixelWidth <= 0 || PixelHeight <= 0 || width <= 0 || height <= 0)
            return [];
        var xScale = width / PixelWidth;
        var yScale = height / PixelHeight;
        return Words.Where(word => !string.IsNullOrWhiteSpace(word.Text) &&
                double.IsFinite(word.X) && double.IsFinite(word.Y) &&
                double.IsFinite(word.Width) && double.IsFinite(word.Height) &&
                word.Width > 0 && word.Height > 0)
            .Select(word =>
            {
                var x = Math.Clamp(word.X, 0, PixelWidth);
                var y = Math.Clamp(word.Y, 0, PixelHeight);
                var right = Math.Clamp(word.X + word.Width, x, PixelWidth);
                var bottom = Math.Clamp(word.Y + word.Height, y, PixelHeight);
                return word with { X = x * xScale, Y = y * yScale,
                    Width = (right - x) * xScale, Height = (bottom - y) * yScale };
            }).Where(word => word.Width > 0 && word.Height > 0).ToArray();
    }
}
