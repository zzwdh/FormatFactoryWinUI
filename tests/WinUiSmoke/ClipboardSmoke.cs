using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DocumentFormat.OpenXml.Packaging;
using FormatFactoryWinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckClipboardAsync()
    {
        var fixtureRoot = Path.Combine(_runRoot, "clipboard-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var sourcePath = Path.Combine(fixtureRoot, "synthetic-source.png");
        await File.WriteAllBytesAsync(sourcePath, new byte[] { 1, 2, 3 });

        await CheckAsync("Synthetic clipboard provider advertises a format that is unavailable on read", async () =>
        {
            var package = new DataPackage();
            AdvertiseUnavailableClipboardFormat(package, StandardDataFormats.StorageItems);
            try
            {
                await package.GetView().GetStorageItemsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("The synthetic unavailable provider unexpectedly supplied storage items.");
            }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x80040064)) { }
        });

        await CheckAsync("Unavailable clipboard storage representation falls back to an existing text file path", async () =>
        {
            var package = new DataPackage();
            AdvertiseUnavailableClipboardFormat(package, StandardDataFormats.StorageItems);
            package.SetText(sourcePath);
            var paths = await ClipboardService.ReadSupportedFilePathsAsync(package.GetView(), Path.Combine(fixtureRoot, "storage-fallback"))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Require(paths.Count == 1 && paths[0] == sourcePath, "The available text path was discarded after the storage representation became unavailable.");
        });

        await CheckAsync("Unavailable clipboard text representation falls back to an available HTML document", async () =>
        {
            var package = new DataPackage();
            AdvertiseUnavailableClipboardFormat(package, StandardDataFormats.Text);
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<p>合成 <b>HTML</b> &amp; Unicode 文本</p>"));
            var imports = Path.Combine(fixtureRoot, "text-fallback");
            var paths = await ClipboardService.ReadSupportedFilePathsAsync(package.GetView(), imports).WaitAsync(TimeSpan.FromSeconds(5));
            RequireClipboardDocument(paths, imports, "合成 HTML & Unicode 文本");
        });

        await CheckAsync("Unavailable clipboard HTML representation falls back to an available RTF document", async () =>
        {
            var package = new DataPackage();
            AdvertiseUnavailableClipboardFormat(package, StandardDataFormats.Html);
            package.SetRtf(@"{\rtf1 synthetic rtf clipboard content}");
            var imports = Path.Combine(fixtureRoot, "html-fallback");
            var paths = await ClipboardService.ReadSupportedFilePathsAsync(package.GetView(), imports).WaitAsync(TimeSpan.FromSeconds(5));
            RequireClipboardDocument(paths, imports, "synthetic rtf clipboard content");
        });

        await CheckAsync("Unavailable clipboard RTF and bitmap representations preserve available plain text", async () =>
        {
            var package = new DataPackage();
            AdvertiseUnavailableClipboardFormat(package, StandardDataFormats.Rtf);
            AdvertiseUnavailableClipboardFormat(package, StandardDataFormats.Bitmap);
            package.SetText("synthetic plain clipboard content");
            var imports = Path.Combine(fixtureRoot, "bitmap-fallback");
            var paths = await ClipboardService.ReadSupportedFilePathsAsync(package.GetView(), imports).WaitAsync(TimeSpan.FromSeconds(5));
            RequireClipboardDocument(paths, imports, "synthetic plain clipboard content");
        });

        await CheckAsync("Clipboard with only unavailable advertised representations returns no import", async () =>
        {
            var package = new DataPackage();
            foreach (var format in new[] { StandardDataFormats.StorageItems, StandardDataFormats.Text,
                StandardDataFormats.Html, StandardDataFormats.Rtf, StandardDataFormats.Bitmap })
                AdvertiseUnavailableClipboardFormat(package, format);
            var paths = await ClipboardService.ReadSupportedFilePathsAsync(package.GetView(), Path.Combine(fixtureRoot, "unavailable-only"))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Require(paths.Count == 0, "An unavailable clipboard representation produced an import.");
        });

        await CheckAsync("Clipboard bitmap import commits a valid PNG and releases its file handles", async () =>
        {
            using var bitmap = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, bitmap);
            var expected = new byte[] { 7, 17, 27, 255, 37, 47, 57, 255, 67, 77, 87, 255, 97, 107, 117, 255 };
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 2, 2, 96, 96, expected);
            await encoder.FlushAsync();
            bitmap.Seek(0);
            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(bitmap));
            var imports = Path.Combine(fixtureRoot, "bitmap");
            var paths = await ClipboardService.ReadSupportedFilePathsAsync(package.GetView(), imports).WaitAsync(TimeSpan.FromSeconds(5));
            Require(paths.Count == 1 && Path.GetDirectoryName(paths[0]) == imports && Path.GetExtension(paths[0]) == ".png",
                "Bitmap import did not commit one isolated PNG.");
            using (var file = File.Open(paths[0], FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var stream = file.AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                Require(decoder.PixelWidth == 2 && decoder.PixelHeight == 2 && pixels.DetachPixelData().SequenceEqual(expected),
                    "The committed PNG differs from the synthetic clipboard pixels.");
            }
            using (File.Open(paths[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            var renamed = paths[0] + ".renamed.png";
            File.Move(paths[0], renamed);
            Require(File.Exists(renamed) && !Directory.EnumerateFiles(imports, "*.tmp").Any(),
                "Bitmap import kept a locked file or an unfinished temporary PNG.");
        });

        await CheckAsync("Clipboard document output write failures propagate instead of being treated as unavailable formats", async () =>
        {
            var package = new DataPackage();
            package.SetText("synthetic document that must not be silently discarded");
            var blockedDirectory = Path.Combine(fixtureRoot, "blocked-output");
            await File.WriteAllTextAsync(blockedDirectory, "A fixture file blocks creation of the import directory.");
            try
            {
                await ClipboardService.ReadSupportedFilePathsAsync(package.GetView(), blockedDirectory).WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("The document write failure was swallowed.");
            }
            catch (IOException) { }
            Require(File.ReadAllText(blockedDirectory).Contains("fixture", StringComparison.Ordinal), "The blocked destination fixture was replaced.");
        });
    }

    private static void AdvertiseUnavailableClipboardFormat(DataPackage package, string format) =>
        package.SetDataProvider(format, _ => throw new COMException("Synthetic unavailable clipboard representation.", unchecked((int)0x80040064)));

    private static void RequireClipboardDocument(IReadOnlyList<string> paths, string importDirectory, string expectedText)
    {
        Require(paths.Count == 1 && Path.GetDirectoryName(paths[0]) == importDirectory && Path.GetExtension(paths[0]) == ".docx",
            "The available clipboard document was not materialized in the isolated fixture directory.");
        using var document = WordprocessingDocument.Open(paths[0], false);
        Require(document.MainDocumentPart?.Document?.Body?.InnerText.Contains(expectedText, StringComparison.Ordinal) == true,
            "The clipboard document lost the available representation's text.");
    }
}
