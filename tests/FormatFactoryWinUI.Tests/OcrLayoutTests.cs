using FormatFactoryWinUI.Documents;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace FormatFactoryWinUI.Tests;

public sealed class OcrLayoutTests
{
    [Fact]
    public void Geometry_MapsPixelsAndClipsAtPageEdges()
    {
        var layout = new OcrPageLayout("test", 1000, 2000,
            [new("test", 100, 300, 250, 100), new("edge", 950, 1990, 100, 100), new("invalid", double.NaN, 0, 2, 2)]);
        var words = layout.MapToPage(500, 1000);
        Assert.Equal(2, words.Count);
        Assert.Equal(new OcrWordBox("test", 50, 150, 125, 50), words[0]);
        Assert.Equal(new OcrWordBox("edge", 475, 995, 25, 5), words[1]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public async Task SearchablePdf_WordCoordinatesFollowRotatedCropBox(int rotation, bool offsetMedia)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "cropped.pdf");
        var output = Path.Combine(directory.Path, "searchable.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(400);
            page.Height = XUnit.FromPoint(600);
            var dx = offsetMedia ? 20 : 0;
            var dy = offsetMedia ? 50 : 0;
            page.MediaBox = new PdfRectangle(new XPoint(dx, dy), new XPoint(400 + dx, 600 + dy));
            page.CropBox = new PdfRectangle(new XPoint(50 + dx, 100 + dy), new XPoint(250 + dx, 400 + dy));
            page.Rotate = rotation;
            document.Save(source);
        }
        var rotated = rotation is 90 or 270;
        var layout = new OcrPageLayout("Position", rotated ? 600 : 400, rotated ? 400 : 600,
            [new("Position", 40, 60, 120, 32)]);
        await new SearchablePdfWriter().WriteLayoutsAsync(source,
            new Dictionary<int, OcrPageLayout> { [0] = layout }, output);
        using var parsed = UglyToad.PdfPig.PdfDocument.Open(output);
        var result = parsed.GetPage(1);
        Assert.Contains("Position", result.Text, StringComparison.Ordinal);
        var letters = result.Letters;
        var left = letters.Min(letter => letter.BoundingBox.Left);
        var right = letters.Max(letter => letter.BoundingBox.Right);
        var top = result.Height - letters.Max(letter => letter.BoundingBox.Top);
        var bottom = result.Height - letters.Min(letter => letter.BoundingBox.Bottom);
        Assert.InRange(left, 18, 25);
        Assert.InRange(right, 75, 83);
        Assert.InRange(top, 29, 38);
        Assert.InRange(bottom, 39, 48);
    }

    [Fact]
    public void FontFallback_UsesActualCoverageForLatinAndCjk()
    {
        if (!OperatingSystem.IsWindows()) return;
        var resolver = UnicodePdfFontResolver.Instance;
        var runs = resolver.ResolveRuns("Latin中文");
        Assert.Equal("Latin中文", string.Concat(runs.Select(run => run.Text)));
        Assert.True(runs.Select(run => run.FontName).Distinct().Count() >= 2);
        foreach (var run in runs)
        {
            var font = new TrueTypeFontData(resolver.GetFont(run.FontName)!, 0);
            Assert.All(run.Text.EnumerateRunes(), rune => Assert.True(font.Supports(rune.Value)));
        }
    }
}
