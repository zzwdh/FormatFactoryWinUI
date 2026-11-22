using PdfSharp.Drawing;
using PdfSharp.Pdf;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed class ImagePdfWriter
{
    private readonly PdfArtifactValidator _validator = new();

    public Task WriteAsync(
        IReadOnlyList<DecodedImagePage> images,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0) throw new ArgumentException(LocalizationService.T("ConversionUiImagePageRequired"), nameof(images));
        return WriteAsync(EnumerateAsync(images, cancellationToken), outputPath, cancellationToken);
    }

    public async Task WriteAsync(
        IAsyncEnumerable<DecodedImagePage> images,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(() => WritePagesAsync(images, temporary, cancellationToken), cancellationToken);
            var validation = await _validator.ValidateAsync(
                new Models.ConversionArtifact("image-pdf", "pdf", temporary, true),
                cancellationToken);
            if (!validation.IsValid) throw new InvalidDataException(validation.Message ?? LocalizationService.T("ConversionUiGeneratedPdfInvalid"));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WritePagesAsync(IAsyncEnumerable<DecodedImagePage> images, string path, CancellationToken token)
    {
        using var document = new PdfDocument();
        await foreach (var item in images.WithCancellation(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            using var imageStream = item.OpenRead();
            using var image = XImage.FromStream(imageStream);
            var page = document.AddPage();
            var width = Math.Max(1, image.PointWidth);
            var height = Math.Max(1, image.PointHeight);
            const double maxPoints = 14400;
            var scale = Math.Min(1, maxPoints / Math.Max(width, height));
            page.Width = XUnit.FromPoint(width * scale);
            page.Height = XUnit.FromPoint(height * scale);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }
        token.ThrowIfCancellationRequested();
        if (document.PageCount == 0) throw new ArgumentException(LocalizationService.T("ConversionUiImagePageRequired"), nameof(images));
        document.Save(path);
    }

    private static async IAsyncEnumerable<DecodedImagePage> EnumerateAsync(
        IReadOnlyList<DecodedImagePage> images,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        foreach (var page in images)
        {
            token.ThrowIfCancellationRequested();
            yield return page;
        }
    }
}