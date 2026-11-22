using System.Runtime.InteropServices.WindowsRuntime;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfSharp.Drawing;
using Windows.Graphics.Imaging;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckScanPrintWorkflowAsync()
    {
        var fixtureRoot = Path.Combine(_runRoot, "print-fixtures");
        Directory.CreateDirectory(fixtureRoot);
        var pdf = Path.Combine(fixtureRoot, "ordered-colors.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            for (var index = 0; index < 8; index++)
            {
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(72);
                page.Height = XUnit.FromPoint(72);
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawRectangle(index % 2 == 0 ? XBrushes.Red : XBrushes.Blue, 0, 0, 72, 72);
            }
            document.Save(pdf);
        }
        await CheckNativeAsync("Print preparation preserves PDF page order and synchronously reloads real pixels", async () =>
        {
            var prepared = await PreparedPrintDocument.CreateAsync(pdf);
            var paths = prepared.Pages.Select(page => page.PixelPath).ToArray();
            try
            {
                Require(prepared.Pages.Count == 8, "Print preparation lost PDF pages.");
                for (var index = 0; index < prepared.Pages.Count; index++)
                {
                    var bitmap = prepared.Pages[index].LoadBitmap();
                    var pixels = bitmap.PixelBuffer.ToArray();
                    var offset = (bitmap.PixelHeight / 2 * bitmap.PixelWidth + bitmap.PixelWidth / 2) * 4;
                    Require(pixels[offset + (index % 2 == 0 ? 2 : 0)] > 240,
                        "Page pixels were empty or out of order.");
                    Require(pixels[offset + (index % 2 == 0 ? 0 : 2)] < 15,
                        "The page color was not preserved.");
                }
                using var exclusive = new FileStream(pdf, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally { prepared.Dispose(); }
            Require(paths.All(path => !File.Exists(path)), "Print temporary pixel files were left behind.");
        });
        await CheckNativeAsync("Preview eviction releases images and leaves separate print pages intact", async () =>
        {
            using var prepared = await PreparedPrintDocument.CreateAsync(pdf);
            var cache = new NativePrintPreviewCache(NativeDocumentPrintService.PreviewCacheLimit);
            Grid MakePage(int number) => new() { Children = { new Image { Source = prepared.Pages[number - 1].LoadBitmap() } } };
            var first = cache.GetOrCreate(1, () => MakePage(1));
            var firstImage = (Image)first.Children[0];
            var finalPage = MakePage(1);
            for (var number = 2; number <= 8; number++)
            {
                var current = number;
                cache.GetOrCreate(current, () => MakePage(current));
                Require(cache.Count <= NativeDocumentPrintService.PreviewCacheLimit, "Preview cache exceeded its limit.");
            }
            Require(first.Children.Count == 0 && firstImage.Source is null, "Eviction retained decoded preview pixels.");
            cache.Clear();
            Require(((Image)finalPage.Children[0]).Source is WriteableBitmap, "Preview cleanup blanked a final print page.");
            NativePrintPreviewCache.ReleasePageImage(finalPage);
            Require(finalPage.Children.Count == 0 && cache.Count == 0, "Print page cleanup was incomplete.");
        });
        await CheckNativeAsync("Print preparation enforces a cumulative pixel budget before showing print UI", async () =>
        {
            var rejected = false;
            try { using var prepared = await PreparedPrintDocument.CreateAsync(pdf, maximumPixelBytes: 200000); }
            catch (InvalidOperationException ex)
            {
                rejected = ex.Message == LocalizationService.T("ScanPrintMemoryLimit");
            }
            Require(rejected, "Multi-page print preparation ignored the cumulative budget.");
            using var exclusive = new FileStream(pdf, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        await CheckNativeAsync("Print preparation decodes image pixels and supports cancellation without printer access", async () =>
        {
            var png = Path.Combine(fixtureRoot, "green.png");
            await using (var output = new FileStream(png, FileMode.Create, FileAccess.ReadWrite))
            {
                using var stream = output.AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 2, 2, 96, 96,
                    new byte[] { 0,255,0,255, 0,255,0,255, 0,255,0,255, 0,255,0,255 });
                await encoder.FlushAsync();
            }
            using (var prepared = await PreparedPrintDocument.CreateAsync(png))
            {
                Require(prepared.Pages.Count == 1, "Image printing did not prepare exactly one frame.");
                var pixels = prepared.Pages[0].LoadBitmap().PixelBuffer.ToArray();
                Require(pixels[1] == 255 && pixels[0] == 0 && pixels[3] == 255, "Prepared image colors changed.");
            }
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var stopped = false;
            try { using var prepared = await PreparedPrintDocument.CreateAsync(png, canceled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Require(stopped, "Print preparation ignored cancellation.");
            using var exclusive = new FileStream(png, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }
}
