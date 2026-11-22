using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FormatFactoryWinUI.Services;

/// <summary>
/// Prepares pages asynchronously before entering the synchronous XAML print callbacks.
/// Compressed pixel files keep the preparation memory bounded to one rendered page.
/// </summary>
internal sealed class PreparedPrintDocument : IDisposable
{
    internal sealed record Page(string PixelPath, int Width, int Height)
    {
        public WriteableBitmap LoadBitmap()
        {
            var bitmap = new WriteableBitmap(Width, Height);
            using var input = File.OpenRead(PixelPath);
            using var pixels = new DeflateStream(input, CompressionMode.Decompress);
            using var output = bitmap.PixelBuffer.AsStream();
            var expected = checked(Width * Height * 4L);
            pixels.CopyTo(output);
            if (output.Position != expected)
                throw new InvalidDataException("The prepared print page is incomplete.");
            bitmap.Invalidate();
            return bitmap;
        }
    }

    internal const long MaximumPreparedPixelBytes = 512L * 1024 * 1024;
    private long _preparedPixelBytes;
    private readonly long _maximumPixelBytes;
    private readonly string _directory;
    private readonly List<Page> _pages = [];
    internal IReadOnlyList<Page> Pages => _pages;
    private PreparedPrintDocument(string directory, long maximumPixelBytes)
    {
        _directory = directory;
        _maximumPixelBytes = maximumPixelBytes;
    }

    internal static async Task<PreparedPrintDocument> CreateAsync(string path, CancellationToken cancellationToken = default, IProgress<int>? progress = null,
        long maximumPixelBytes = MaximumPreparedPixelBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPixelBytes, 1);
        var directory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", "Print", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = new PreparedPrintDocument(directory, maximumPixelBytes);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                using var pdfInput = await file.OpenReadAsync().AsTask(cancellationToken);
                var document = await PdfDocument.LoadFromStreamAsync(pdfInput).AsTask(cancellationToken);
                for (uint index = 0; index < document.PageCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var page = document.GetPage(index);
                    using var stream = new InMemoryRandomAccessStream();
                    var scale = Math.Min(2, 3000 / Math.Max(page.Size.Width, page.Size.Height));
                    await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
                        DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
                        BackgroundColor = Microsoft.UI.Colors.White
                    }).AsTask(cancellationToken);
                    stream.Seek(0);
                    var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
                    await result.SaveFrameAsync(await decoder.GetFrameAsync(0).AsTask(cancellationToken), cancellationToken);
                    progress?.Report(result.Pages.Count);
                }
            }
            else
            {
                using var stream = await file.OpenReadAsync().AsTask(cancellationToken);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
                for (uint index = 0; index < decoder.FrameCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frame = await decoder.GetFrameAsync(index).AsTask(cancellationToken);
                    await result.SaveFrameAsync(frame, cancellationToken);
                    progress?.Report(result.Pages.Count);
                }
            }
            if (result._pages.Count == 0) throw new InvalidDataException(LocalizationService.T("ScanPrintNoPages"));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private async Task SaveFrameAsync(BitmapFrame frame, CancellationToken token)
    {
        var scale = Math.Min(1, 3000d / Math.Max(frame.OrientedPixelWidth, frame.OrientedPixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(frame.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(frame.PixelHeight * scale))
        };
        using var bitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
        var pixelBytes = checked((long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
        if (_preparedPixelBytes + pixelBytes > _maximumPixelBytes)
            throw new InvalidOperationException(LocalizationService.T("ScanPrintMemoryLimit"));
        var pixels = new byte[checked((int)pixelBytes)];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        var path = Path.Combine(_directory, $"{_pages.Count + 1:00000}.pixels");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await using (var compressed = new DeflateStream(output, CompressionLevel.Fastest))
                await compressed.WriteAsync(pixels, token);
            _pages.Add(new Page(path, bitmap.PixelWidth, bitmap.PixelHeight));
            _preparedPixelBytes += pixelBytes;
        }
        catch
        {
            TryDeleteFile(path);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var page in _pages) TryDeleteFile(page.PixelPath);
        _pages.Clear();
        try { Directory.Delete(_directory, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}



