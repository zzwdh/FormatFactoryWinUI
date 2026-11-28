using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using FormatFactoryWinUI.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace FormatFactoryWinUI.Services;

/// <summary>Uses installed Windows image codecs, with explicit orientation and sRGB output.</summary>
public sealed class WindowsImageCodecService : IImageCodecService
{
    public bool CanEncode(string targetFormatId) => EncoderId(targetFormatId) is not null;

    public Task<bool> ValidateLosslessAsync(string inputPath, string outputPath,
        CancellationToken cancellationToken = default) =>
        WicProfilePreservingCodec.ValidateLosslessAsync(inputPath, outputPath, cancellationToken);

    public Task TranscodeWithColorPolicyAsync(string inputPath, string outputPath, string targetFormatId,
        string mode, string colorPolicy, CancellationToken cancellationToken = default) =>
        colorPolicy.Trim().ToLowerInvariant() switch
        {
            "srgb" => TranscodeAsync(inputPath, outputPath, targetFormatId, mode, cancellationToken),
            "preserve" => WicProfilePreservingCodec.TranscodeAsync(inputPath, outputPath, targetFormatId, mode, cancellationToken),
            _ => throw new NotSupportedException(LocalizationService.T("NativeImagePreserveUnsupported"))
        };

    public Task TranscodeWithOptionsAsync(string inputPath, string outputPath, string targetFormatId,
        string mode, string colorPolicy, MediaConversionOptions options, CancellationToken cancellationToken = default) =>
        colorPolicy.Trim().ToLowerInvariant() switch
        {
            "srgb" => TranscodeCoreAsync(inputPath, outputPath, targetFormatId, mode, options, cancellationToken),
            "preserve" => WicProfilePreservingCodec.TranscodeWithOptionsAsync(inputPath, outputPath,
                targetFormatId, mode, options, cancellationToken),
            _ => throw new NotSupportedException(LocalizationService.T("NativeImagePreserveUnsupported"))
        };

    public async Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Decode the opened stream so WinRT's StorageFile path parser does not
            // reject valid extended paths. Managed IO still enforces read access.
            await using var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous);
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            var format = FormatCatalog.FindByExtension(path);
            return new MediaProbeResult
            {
                ContentKind = ContentKind.Image,
                FormatId = format?.Id ?? string.Empty,
                FileSize = new FileInfo(path).Length,
                Width = checked((int)decoder.OrientedPixelWidth),
                Height = checked((int)decoder.OrientedPixelHeight),
                PageCount = checked((int)decoder.FrameCount),
                FormatName = format?.DisplayName ?? decoder.DecoderInformation.FriendlyName,
                VideoCodec = decoder.DecoderInformation.FriendlyName,
                PixelFormat = decoder.BitmapPixelFormat switch
                {
                    BitmapPixelFormat.Bgra8 => "bgra",
                    BitmapPixelFormat.Rgba8 => "rgba",
                    BitmapPixelFormat.Rgba16 => "rgba64le",
                    BitmapPixelFormat.Gray8 => "gray",
                    BitmapPixelFormat.Gray16 => "gray16le",
                    BitmapPixelFormat.P010 => "p010le",
                    _ => string.Empty
                },
                // TIFF pages are not animation. Animated sources stay on the existing FFmpeg route.
                IsAnimated = decoder.FrameCount > 1 && format?.Id is "gif" or "webp" or "png" or "avif" ||
                    await ImageAnimationDetector.HasAnimationAsync(path, cancellationToken),
                Metadata = new Dictionary<string, string>
                {
                    ["imageDecoder"] = decoder.DecoderInformation.FriendlyName,
                    ["imageColorPolicy"] = "sRGB",
                    ["imageFrames"] = decoder.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            };
        }
        catch (COMException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public Task TranscodeAsync(string inputPath, string outputPath, string targetFormatId,
        string mode, CancellationToken cancellationToken = default) =>
        TranscodeCoreAsync(inputPath, outputPath, targetFormatId, mode, new(), cancellationToken);

    private async Task TranscodeCoreAsync(string inputPath, string outputPath, string targetFormatId,
        string mode, MediaConversionOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WicProfilePreservingCodec.EnsureLosslessPrecision(inputPath, mode, cancellationToken);
        var target = FormatCatalog.NormalizeFormatId(targetFormatId);
        var id = EncoderId(target) ?? throw new NotSupportedException(LocalizationService.T("NativeImageEncoderUnsupported"));
        if (mode.Equals("lossless", StringComparison.OrdinalIgnoreCase) && target == "jpg")
            throw new NotSupportedException(LocalizationService.T("NativeImageLosslessJpegUnsupported"));
        await using var file = new FileStream(Path.GetFullPath(inputPath), FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous);
        using var source = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(source).AsTask(cancellationToken);
        if (decoder.FrameCount > 1 && target != "tiff")
            throw new NotSupportedException(LocalizationService.T("NativeImageMultiplePages"));
        if (decoder.FrameCount > 1 && FormatCatalog.FindByExtension(inputPath)?.Id is "gif" or "webp" or "png" or "avif" ||
            await ImageAnimationDetector.HasAnimationAsync(inputPath, cancellationToken))
            throw new NotSupportedException(LocalizationService.T("NativeImageAnimationFallback"));
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 81920, FileOptions.Asynchronous);
        using var destination = output.AsRandomAccessStream();
        BitmapPropertySet? properties = target == "jpg" ? new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(options.ImageQuality.HasValue ? options.ImageQuality.Value / 100f
                : mode == "quality" ? 0.96f : mode == "fast" ? 0.75f : 0.9f,
                PropertyType.Single)
        } : null;
        var encoder = properties is null
            ? await BitmapEncoder.CreateAsync(id, destination).AsTask(cancellationToken)
            : await BitmapEncoder.CreateAsync(id, destination, properties).AsTask(cancellationToken);
        for (uint index = 0; index < decoder.FrameCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await decoder.GetFrameAsync(index).AsTask(cancellationToken);
            using var bitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
            if (target is "jpg" or "bmp")
            {
                // Alpha is composited over white before dropping it, matching the existing FFmpeg presets.
                var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
                bitmap.CopyToBuffer(pixels.AsBuffer());
                CompositeWhite(pixels);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, frame.DpiX, frame.DpiY, pixels);
            }
            else encoder.SetSoftwareBitmap(bitmap);
            if (options.HasDimensions)
            {
                var dimensions = options.ResolveDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
                encoder.BitmapTransform.ScaledWidth = (uint)dimensions.Width;
                encoder.BitmapTransform.ScaledHeight = (uint)dimensions.Height;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            encoder.IsThumbnailGenerated = false;
            // SetSoftwareBitmap retains the source until the frame is committed.
            // Commit before disposing it, including each page of a multi-page TIFF.
            if (index + 1 < decoder.FrameCount)
                await encoder.GoToNextFrameAsync().AsTask(cancellationToken);
            else
                await encoder.FlushAsync().AsTask(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void CompositeWhite(Span<byte> pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            for (var channel = 0; channel < 3; channel++)
                pixels[index + channel] = (byte)((pixels[index + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
            pixels[index + 3] = 255;
        }
    }

    private static Guid? EncoderId(string targetFormatId) => FormatCatalog.NormalizeFormatId(targetFormatId) switch
    {
        "png" => BitmapEncoder.PngEncoderId,
        "jpg" => BitmapEncoder.JpegEncoderId,
        "bmp" => BitmapEncoder.BmpEncoderId,
        "tiff" => BitmapEncoder.TiffEncoderId,
        _ => null
    };
}
