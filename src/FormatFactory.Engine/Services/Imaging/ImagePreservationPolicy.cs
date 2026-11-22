using System.Globalization;
using System.Text.RegularExpressions;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal static class ImagePreservationPolicy
{
    public static ImageAnimationInfo? ValidateFfmpegInput(MediaTask task, CancellationToken token = default)
    {
        try { return ValidateFfmpegInputCore(task, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            FormatFailureDiagnostics.Record(error, FormatFailureDiagnostics.Image(task, FormatFailureStage.ImagePolicy, FormatFailureReason.Unknown));
            FormatFailureDiagnostics.WriteApp(error, task.Id);
            throw;
        }
    }

    private static ImageAnimationInfo? ValidateFfmpegInputCore(MediaTask task, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (task.ConversionMode == "lossless" &&
            (FormatCatalog.IsVideoCodec(task.TargetFormatId) || FormatCatalog.IsFrameSequence(task.TargetFormatId)))
            throw Failure(task, new NotSupportedException(LocalizationService.T("ConversionUiLosslessRouteUnsupported")), FormatFailureReason.UnsupportedRoute);
        if (task.Kind != MediaKind.Image) return null;
        var actualPages = File.Exists(task.InputPath) ? TiffPageCountReader.Read(task.InputPath, token) : null;
        var animation = Path.GetExtension(task.InputPath).Equals(".gif", StringComparison.OrdinalIgnoreCase)
            ? ImageAnimationInfoReader.Read(task.InputPath, token) : null;
        var animated = animation?.IsAnimated == true || task.Probe.IsAnimated ||
            File.Exists(task.InputPath) && ImageAnimationDetector.HasAnimation(task.InputPath);
        if ((actualPages > 1 || task.Probe.PageCount > 1 || task.SourceFormatId == "tiff" && task.Probe.PageCount == 0 && actualPages is null) && !animated)
            throw FormatFailureDiagnostics.Attach(new NotSupportedException(LocalizationService.T("NativeImageMultiplePages")),
                FormatFailureDiagnostics.Image(task, FormatFailureStage.ImagePolicy, FormatFailureReason.MultiplePages) with { Pages = actualPages ?? task.Probe.PageCount });
        if (animated)
        {
            // Other animation decoders may expose only one frame. Never promise their preservation.
            if (animation?.IsAnimated != true || task.TargetFormatId != "webp" &&
                (task.TargetFormatId != "gif" || task.ConversionMode == "lossless"))
                throw Failure(task, new NotSupportedException(LocalizationService.T("NativeImageAnimationUnsupported")), FormatFailureReason.AnimationFormat);
            if (animation.FrameDurations.Any(delay => delay < 20) || animation.LoopCount > ushort.MaxValue)
                throw FormatFailureDiagnostics.Attach(new NotSupportedException(LocalizationService.T("NativeImageAnimationUnsupported")),
                    FormatFailureDiagnostics.Image(task, FormatFailureStage.ImagePolicy, FormatFailureReason.AnimationTiming) with { Frames = animation.FrameDurations.Count });
            return animation;
        }
        if (task.ConversionMode == "lossless")
        {
            if (task.TargetFormatId is not ("jpg" or "png" or "bmp" or "webp" or "avif" or "gif" or "tiff" or "tga"))
                throw Failure(task, new ImagePrecisionUnsupportedException(), FormatFailureReason.UnsupportedRoute);
            var sourceFormat = FormatCatalog.FindByExtension(task.InputPath)?.Id ?? task.SourceFormatId;
            if (sourceFormat is "raw" or "heif")
                throw Failure(task, new ImagePrecisionUnsupportedException(), FormatFailureReason.Precision);
            var depth = PixelBitDepth(task.Probe.PixelFormat);
            if (depth > 8 && task.TargetFormatId is "jpg" or "bmp" or "gif" or "webp" or "tga" ||
                depth > 12 && task.TargetFormatId == "avif")
                throw Failure(task, new ImagePrecisionUnsupportedException(), FormatFailureReason.Precision);
            // ljpeg rejects non-full-range planar YUV at normal strictness.
            // Coercing its range or converting to RGB can change decoded pixels.
            // YUVJ already identifies full-range samples without a separate range tag.
            if (task.TargetFormatId == "jpg" &&
                task.Probe.PixelFormat.StartsWith("yuv", StringComparison.OrdinalIgnoreCase) &&
                !task.Probe.PixelFormat.StartsWith("yuvj", StringComparison.OrdinalIgnoreCase) &&
                task.Probe.ColorRange?.Trim().ToLowerInvariant() is not ("pc" or "jpeg" or "full"))
                throw Failure(task, new NotSupportedException(LocalizationService.T("ConversionUiLosslessJpegRangeUnsupported")), FormatFailureReason.ColorRange);
            // A palette encoder cannot preserve arbitrary RGB colors.
            if (task.TargetFormatId == "gif" && task.SourceFormatId != "gif")
                throw Failure(task, new ImagePrecisionUnsupportedException(), FormatFailureReason.Precision);
        }
        return null;
    }

    private static T Failure<T>(MediaTask task, T error, FormatFailureReason reason) where T : Exception =>
        FormatFailureDiagnostics.Attach(error, FormatFailureDiagnostics.Image(task, FormatFailureStage.ImagePolicy, reason));

    internal static int PixelBitDepth(string pixelFormat)
    {
        var format = pixelFormat.ToLowerInvariant();
        if (format.Contains("f32", StringComparison.Ordinal)) return 32;
        if (format.Contains("f16", StringComparison.Ordinal) || format.StartsWith("p016", StringComparison.Ordinal)) return 16;
        if (format.StartsWith("p010", StringComparison.Ordinal)) return 10;
        if (format.StartsWith("p012", StringComparison.Ordinal)) return 12;
        if (format.StartsWith("rgb48", StringComparison.Ordinal) || format.StartsWith("bgr48", StringComparison.Ordinal) ||
            format.StartsWith("rgba64", StringComparison.Ordinal) || format.StartsWith("bgra64", StringComparison.Ordinal)) return 16;
        var match = Regex.Match(format, @"(?:p|gray|ya)(?<depth>9|10|12|14|16)(?:le|be)?$");
        return match.Success ? int.Parse(match.Groups["depth"].Value, CultureInfo.InvariantCulture) : 8;
    }
}
