using System.Runtime.InteropServices;
using System.Numerics;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed class ImagePrecisionUnsupportedException : NotSupportedException
{
    public ImagePrecisionUnsupportedException(Exception? inner = null)
        : base(LocalizationService.T("NativeImagePrecisionUnsupported"), inner) { }
}

/// <summary>WIC conversion in the original RGB color space, with per-frame ICC profiles attached to the encoder.</summary>
public static class WicProfilePreservingCodec
{
    private static readonly Guid FactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid FactoryInterface = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid Bgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    private static readonly Guid Bgr24 = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    private static readonly Guid Bgr32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90e");

    public static bool CanPreserve(string targetFormatId) =>
        FormatCatalog.NormalizeFormatId(targetFormatId) is "png" or "jpg" or "tiff";

    /// <summary>The native paths currently encode 8-bit channels. Never quantize a lossless request.</summary>
    public static void EnsureLosslessPrecision(string path, string mode, CancellationToken token = default)
    {
        if (!mode.Equals("lossless", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            // Installed HEIF/RAW codecs may expose a reduced-precision preview as
            // their only bitmap format. Its 8-bit format does not establish the
            // original sensor/bitstream precision, so do not call that lossless.
            if (FormatCatalog.FindByExtension(path)?.DefaultEngineId == "windows-image")
                throw new ImagePrecisionUnsupportedException();
            if (HasHighBitDepth(path, token)) throw new ImagePrecisionUnsupportedException();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ImagePrecisionUnsupportedException)
        {
            // Unknown precision is not permission to silently convert to 8-bit.
            throw new ImagePrecisionUnsupportedException(exception);
        }
    }

    public static bool HasHighBitDepth(string path, CancellationToken token = default)
    {
        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null) throw new NotSupportedException(comResult.Message);
        using var com = comResult.Value;
        nint factory = 0, decoder = 0;
        try
        {
            factory = CreateFactory();
            decoder = OpenDecoder(factory, path);
            Check(Call<GetUInt>(decoder, 12)(decoder, out var count));
            for (uint index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                nint frame = 0;
                try
                {
                    Check(Call<GetFrame>(decoder, 13)(decoder, index, out frame));
                    if (HasHighBitDepth(factory, frame)) return true;
                }
                finally { Release(frame); }
            }
            return false;
        }
        finally { Release(decoder); Release(factory); }
    }

    private static bool HasHighBitDepth(nint factory, nint frame)
    {
        nint component = 0, info = 0;
        try
        {
            Check(Call<GetPixelFormat>(frame, 4)(frame, out var format));
            Check(Call<CreateComponent>(factory, 6)(factory, ref format, out component));
            var iid = new Guid("e8eda601-3d48-431a-ab44-69059be88bbe");
            Check(Marshal.QueryInterface(component, in iid, out info));
            Check(Call<GetUInt>(info, 14)(info, out var channels));
            if (channels == 0 || channels > 16) throw new NotSupportedException();
            var getMask = Call<GetChannelMask>(info, 15);
            for (uint channel = 0; channel < channels; channel++)
            {
                Check(getMask(info, channel, 0, null, out var length));
                if (length == 0 || length > 128) throw new NotSupportedException();
                var mask = new byte[length];
                Check(getMask(info, channel, length, mask, out _));
                if (mask.Sum(value => BitOperations.PopCount((uint)value)) > 8) return true;
            }
            return false;
        }
        finally { Release(info); Release(component); }
    }

    /// <summary>Compares actual decoded samples without an ICC transform, including alpha and every page.</summary>
    public static Task<bool> ValidateLosslessAsync(string inputPath, string outputPath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
        ValidateLossless(inputPath, outputPath, cancellationToken), cancellationToken);

    private static bool ValidateLossless(string inputPath, string outputPath, CancellationToken token)
    {
        // BGRA comparison is exact only for supported integer channels up to 8 bits.
        EnsureLosslessPrecision(inputPath, "lossless", token);
        EnsureLosslessPrecision(outputPath, "lossless", token);
        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null) throw new NotSupportedException(comResult.Message);
        using var com = comResult.Value;
        nint factory = 0, input = 0, output = 0;
        try
        {
            factory = CreateFactory();
            input = OpenDecoder(factory, inputPath);
            output = OpenDecoder(factory, outputPath);
            Check(Call<GetUInt>(input, 12)(input, out var inputCount));
            Check(Call<GetUInt>(output, 12)(output, out var outputCount));
            if (inputCount == 0 || inputCount > 100_000 || inputCount != outputCount) return false;
            for (uint index = 0; index < inputCount; index++)
            {
                token.ThrowIfCancellationRequested();
                nint before = 0, after = 0;
                try
                {
                    Check(Call<GetFrame>(input, 13)(input, index, out before));
                    Check(Call<GetFrame>(output, 13)(output, index, out after));
                    Check(Call<GetPixelFormat>(before, 4)(before, out var beforeFormat));
                    Check(Call<GetPixelFormat>(after, 4)(after, out var afterFormat));
                    // CMYK, premultiplied samples, floating point and unknown formats cannot
                    // establish preservation through an 8-bit BGRA conversion.
                    if (!CanCompareExactly(beforeFormat) || !CanCompareExactly(afterFormat))
                        throw new ImagePrecisionUnsupportedException();
                    var beforeProfile = ReadProfile(factory, before);
                    var afterProfile = ReadProfile(factory, after);
                    if (beforeProfile is null ? afterProfile is not null :
                        afterProfile is null || !beforeProfile.AsSpan().SequenceEqual(afterProfile)) return false;
                    if (!FrameSamplesMatch(factory, before, after, token)) return false;
                }
                finally { Release(after); Release(before); }
            }
            return true;
        }
        finally { Release(output); Release(input); Release(factory); }
    }

    private static bool CanCompareExactly(Guid format)
    {
        if (format == new Guid("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9") ||
            format == new Guid("d98c6b95-3efe-47d6-bb25-eb1748ab0cf1") ||
            format == new Guid("05ec7c2b-f1e6-4961-ad46-e1cc810a87d2")) return true;
        // WIC indexed, gray <=8 bit, BGR555/BGR565, RGB/BGR24 and straight BGRA32.
        // 0x0b is 16-bit gray and deliberately excluded.
        for (var suffix = 1; suffix <= 15; suffix++)
            if (suffix != 11 && format == new Guid($"6fddc324-4e03-4bfe-b185-3d77768dc9{suffix:x2}")) return true;
        return false;
    }

    private static bool FrameSamplesMatch(nint factory, nint before, nint after, CancellationToken token)
    {
        nint beforeRotator = 0, afterRotator = 0, beforeConverter = 0, afterConverter = 0, rectangle = 0;
        try
        {
            var beforeSource = OrientFrame(factory, before, out beforeRotator);
            var afterSource = OrientFrame(factory, after, out afterRotator);
            Check(Call<GetObject>(factory, 10)(factory, out beforeConverter));
            Check(Call<GetObject>(factory, 10)(factory, out afterConverter));
            var format = Bgra32;
            Check(Call<InitializeConverter>(beforeConverter, 8)(beforeConverter, beforeSource, ref format, 0, 0, 0, 0));
            format = Bgra32;
            Check(Call<InitializeConverter>(afterConverter, 8)(afterConverter, afterSource, ref format, 0, 0, 0, 0));
            Check(Call<GetSize>(beforeConverter, 3)(beforeConverter, out var width, out var height));
            Check(Call<GetSize>(afterConverter, 3)(afterConverter, out var afterWidth, out var afterHeight));
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue ||
                width != afterWidth || height != afterHeight) return false;
            const int tileWidth = 16_384;
            var beforePixels = new byte[Math.Min((int)width, tileWidth) * 4];
            var afterPixels = new byte[beforePixels.Length];
            rectangle = Marshal.AllocCoTaskMem(Marshal.SizeOf<PixelRectangle>());
            var copyBefore = Call<CopyPixels>(beforeConverter, 7);
            var copyAfter = Call<CopyPixels>(afterConverter, 7);
            // Bounded tiles keep validation memory independent of image dimensions.
            for (var y = 0; y < (int)height; y++)
            for (var x = 0; x < (int)width;)
            {
                token.ThrowIfCancellationRequested();
                var columns = Math.Min((int)width - x, tileWidth);
                var bytes = checked((uint)columns * 4);
                Marshal.StructureToPtr(new PixelRectangle { X = x, Y = y, Width = columns, Height = 1 }, rectangle, false);
                Check(copyBefore(beforeConverter, rectangle, bytes, bytes, beforePixels));
                Check(copyAfter(afterConverter, rectangle, bytes, bytes, afterPixels));
                if (!beforePixels.AsSpan(0, (int)bytes).SequenceEqual(afterPixels.AsSpan(0, (int)bytes))) return false;
                x += columns;
            }
            return true;
        }
        finally
        {
            if (rectangle != 0) Marshal.FreeCoTaskMem(rectangle);
            Release(afterConverter); Release(beforeConverter); Release(afterRotator); Release(beforeRotator);
        }
    }

    private static nint OrientFrame(nint factory, nint frame, out nint rotator)
    {
        rotator = 0;
        var orientation = ReadOrientation(frame);
        if (orientation == 1) return frame;
        Check(Call<GetObject>(factory, 13)(factory, out rotator));
        // WIC applies flips before rotation; transpose and transverse use opposite rotations.
        var rotation = orientation switch { 2 => 8u, 3 => 2u, 4 => 16u, 5 => 11u, 6 => 1u, 7 => 9u, 8 => 3u, _ => 0u };
        Check(Call<InitializeRotator>(rotator, 8)(rotator, frame, rotation));
        return rotator;
    }

    public static Task TranscodeAsync(string inputPath, string outputPath, string targetFormatId,
        string mode, CancellationToken cancellationToken = default) => Task.Run(() =>
        Transcode(inputPath, outputPath, FormatCatalog.NormalizeFormatId(targetFormatId), mode, new(), cancellationToken), cancellationToken);

    public static Task TranscodeWithOptionsAsync(string inputPath, string outputPath, string targetFormatId,
        string mode, MediaConversionOptions options, CancellationToken cancellationToken = default) => Task.Run(() =>
        Transcode(inputPath, outputPath, FormatCatalog.NormalizeFormatId(targetFormatId), mode, options, cancellationToken), cancellationToken);

    /// <summary>Returns the embedded ICC profile bytes, not a synthesized EXIF color-space tag.</summary>
    public static byte[]? ReadProfile(string path, uint frameIndex = 0)
    {
        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null) throw new NotSupportedException(comResult.Message);
        using var com = comResult.Value;
        nint factory = 0, decoder = 0, frame = 0;
        try
        {
            factory = CreateFactory();
            decoder = OpenDecoder(factory, path);
            Check(Call<GetFrame>(decoder, 13)(decoder, frameIndex, out frame));
            return ReadProfile(factory, frame);
        }
        finally { Release(frame); Release(decoder); Release(factory); }
    }

    private static void Transcode(string inputPath, string outputPath, string target, string mode,
        MediaConversionOptions mediaOptions, CancellationToken token)
    {
        if (!CanPreserve(target)) throw new NotSupportedException(LocalizationService.T("NativeImagePreserveUnsupported"));
        if (mode.Equals("lossless", StringComparison.OrdinalIgnoreCase) && target == "jpg")
            throw new NotSupportedException(LocalizationService.T("NativeImageLosslessJpegUnsupported"));
        EnsureLosslessPrecision(inputPath, mode, token);
        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null) throw new NotSupportedException(comResult.Message);
        using var com = comResult.Value;
        nint factory = 0, decoder = 0, stream = 0, encoder = 0;
        var ownedOutput = false;
        var committed = false;
        try
        {
            token.ThrowIfCancellationRequested();
            factory = CreateFactory();
            decoder = OpenDecoder(factory, inputPath);
            Check(Call<GetUInt>(decoder, 12)(decoder, out var frameCount));
            if (frameCount > 1 && target != "tiff")
                throw new NotSupportedException(LocalizationService.T("NativeImageMultiplePages"));
            if (frameCount > 1 && FormatCatalog.FindByExtension(inputPath)?.Id is "gif" or "webp" or "png" or "avif" ||
                ImageAnimationDetector.HasAnimation(inputPath))
                throw new NotSupportedException(LocalizationService.T("NativeImagePreserveUnsupported"));
            // Reserve exclusively before WIC opens the private staging file; never overwrite a user artifact.
            using (new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            ownedOutput = true;
            Check(Call<GetObject>(factory, 14)(factory, out stream));
            Check(Call<InitializeFile>(stream, 15)(stream, Path.GetFullPath(outputPath), 0x40000000));
            var container = target switch
            {
                "png" => new Guid("1b7cfaf4-713f-473c-bbcd-6137425faeaf"),
                "jpg" => new Guid("19e4a5aa-5662-4fc5-a0c0-1758028e1057"),
                _ => new Guid("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3")
            };
            Check(Call<CreateEncoder>(factory, 8)(factory, ref container, 0, out encoder));
            Check(Call<InitializeEncoder>(encoder, 3)(encoder, stream, 2));
            for (uint index = 0; index < frameCount; index++)
            {
                token.ThrowIfCancellationRequested();
                nint frame = 0;
                try
                {
                    Check(Call<GetFrame>(decoder, 13)(decoder, index, out frame));
                    var profile = ReadProfile(factory, frame)
                        ?? throw new NotSupportedException(LocalizationService.T("NativeImageNoIcc"));
                    // A CMYK/gray ICC profile cannot describe decoded BGRA pixels. Never mislabel them as RGB.
                    if (profile.Length < 128 || profile[16] != 'R' || profile[17] != 'G' || profile[18] != 'B' || profile[19] != ' ')
                        throw new NotSupportedException(LocalizationService.T("NativeImageRgbIccOnly"));
                    EncodeFrame(factory, encoder, frame, profile, target, mode, mediaOptions, token);
                }
                finally { Release(frame); }
            }
            token.ThrowIfCancellationRequested();
            Check(Call<Commit>(encoder, 11)(encoder));
            committed = true;
        }
        finally
        {
            Release(encoder); Release(stream); Release(decoder); Release(factory);
            if (ownedOutput && !committed && File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    private static void EncodeFrame(nint factory, nint encoder, nint frame, byte[] profile,
        string target, string mode, MediaConversionOptions mediaOptions, CancellationToken token)
    {
        nint rotator = 0, scaler = 0, converter = 0, outputFrame = 0, options = 0, color = 0;
        try
        {
            var source = OrientFrame(factory, frame, out rotator);
            if (mediaOptions.HasDimensions)
            {
                Check(Call<GetSize>(source, 3)(source, out var sourceWidth, out var sourceHeight));
                var dimensions = mediaOptions.ResolveDimensions(checked((int)sourceWidth), checked((int)sourceHeight));
                Check(Call<GetObject>(factory, 11)(factory, out scaler));
                Check(Call<InitializeScaler>(scaler, 8)(scaler, source, (uint)dimensions.Width, (uint)dimensions.Height, 4));
                source = scaler;
            }
            Check(Call<GetObject>(factory, 10)(factory, out converter));
            var bgra = Bgra32;
            Check(Call<InitializeConverter>(converter, 8)(converter, source, ref bgra, 0, 0, 0, 0));
            Check(Call<GetSize>(converter, 3)(converter, out var width, out var height));
            Check(Call<GetResolution>(converter, 5)(converter, out var dpiX, out var dpiY));
            var stride = checked(width * 4);
            var pixels = new byte[checked((int)(stride * height))];
            Check(Call<CopyPixels>(converter, 7)(converter, 0, stride, (uint)pixels.Length, pixels));
            if (target == "jpg") CompositeWhite(pixels);
            token.ThrowIfCancellationRequested();
            Check(Call<CreateFrame>(encoder, 10)(encoder, out outputFrame, out options));
            if (target == "jpg" && options != 0) SetQuality(options, mode, mediaOptions.ImageQuality);
            Check(Call<InitializeFrame>(outputFrame, 3)(outputFrame, options));
            Check(Call<SetSize>(outputFrame, 4)(outputFrame, width, height));
            Check(Call<SetResolution>(outputFrame, 5)(outputFrame, dpiX > 0 ? dpiX : 96, dpiY > 0 ? dpiY : 96));
            var pixelFormat = Bgra32;
            Check(Call<SetPixelFormat>(outputFrame, 6)(outputFrame, ref pixelFormat));
            if (pixelFormat == Bgr24)
            {
                var packed = new byte[checked((int)(width * height * 3))];
                for (int from = 0, to = 0; from < pixels.Length; from += 4, to += 3)
                    pixels.AsSpan(from, 3).CopyTo(packed.AsSpan(to, 3));
                pixels = packed;
                stride = checked(width * 3);
            }
            else if (pixelFormat != Bgra32 && pixelFormat != Bgr32)
                throw new NotSupportedException(LocalizationService.T("NativeImagePreserveUnsupported"));
            Check(Call<GetObject>(factory, 15)(factory, out color));
            Check(Call<InitializeProfile>(color, 4)(color, profile, (uint)profile.Length));
            var colorArray = Marshal.AllocCoTaskMem(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(colorArray, color);
                Check(Call<SetColorContexts>(outputFrame, 7)(outputFrame, 1, colorArray));
            }
            finally { Marshal.FreeCoTaskMem(colorArray); }
            Check(Call<WritePixels>(outputFrame, 10)(outputFrame, height, stride, (uint)pixels.Length, pixels));
            token.ThrowIfCancellationRequested();
            Check(Call<Commit>(outputFrame, 12)(outputFrame));
        }
        finally { Release(color); Release(options); Release(outputFrame); Release(converter); Release(scaler); Release(rotator); }
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

    private static byte[]? ReadProfile(nint factory, nint frame)
    {
        var get = Call<GetColorContexts>(frame, 9);
        if (get(frame, 0, 0, out var count) < 0 || count == 0) return null;
        if (count > 64) throw new InvalidDataException("Invalid image color context count.");
        var contexts = new nint[count];
        var array = Marshal.AllocCoTaskMem(checked((int)count * IntPtr.Size));
        try
        {
            for (var index = 0; index < count; index++)
            {
                Check(Call<GetObject>(factory, 15)(factory, out contexts[index]));
                Marshal.WriteIntPtr(array, index * IntPtr.Size, contexts[index]);
            }
            Check(get(frame, count, array, out var actual));
            for (var index = 0; index < Math.Min(count, actual); index++)
            {
                Check(Call<GetUInt>(contexts[index], 6)(contexts[index], out var type));
                if (type != 1) continue;
                var read = Call<GetProfileBytes>(contexts[index], 7);
                Check(read(contexts[index], 0, null, out var length));
                if (length < 128 || length > 64 * 1024 * 1024) continue;
                var bytes = new byte[length];
                Check(read(contexts[index], length, bytes, out _));
                return bytes;
            }
            return null;
        }
        finally
        {
            foreach (var context in contexts) Release(context);
            Marshal.FreeCoTaskMem(array);
        }
    }

    private static ushort ReadOrientation(nint frame)
    {
        nint reader = 0;
        try
        {
            if (Call<GetObject>(frame, 8)(frame, out reader) < 0 || reader == 0) return 1;
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                var variant = default(NativePropertyVariant);
                try
                {
                    if (Call<GetMetadata>(reader, 5)(reader, query, out variant) >= 0)
                    {
                        var value = NativePropertyValue.ToUInt16(variant);
                        if (value is >= 1 and <= 8) return value;
                    }
                }
                finally { NativePropertyValue.Clear(ref variant); }
            }
            return 1;
        }
        finally { Release(reader); }
    }

    private static void SetQuality(nint options, string mode, int? quality)
    {
        var property = new PropertyInfo { Name = "ImageQuality", Type = 4 };
        var value = new Variant { Type = 4, Float = quality.HasValue ? quality.Value / 100f : mode == "quality" ? 0.96f : mode == "fast" ? 0.75f : 0.9f };
        Check(Call<WriteProperty>(options, 4)(options, 1, ref property, ref value));
    }
    private static nint CreateFactory()
    {
        var clsid = FactoryClass; var iid = FactoryInterface;
        Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out var factory));
        return factory;
    }
    private static nint OpenDecoder(nint factory, string path)
    {
        Check(Call<CreateDecoder>(factory, 3)(factory, Path.GetFullPath(path), 0, 0x80000000, 0, out var decoder));
        return decoder;
    }
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void Release(nint value) { if (value != 0) Marshal.Release(value); }
    private static T Call<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential)] private struct PixelRectangle
    {
        public int X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PropertyInfo
    {
        public uint Kind; public ushort Type; public ushort ClipboardFormat; public uint Hint;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name; public Guid ClassId;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public float Float;
    }
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObject(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt(nint self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPixelFormat(nint self, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateComponent(nint self, ref Guid format, out nint component);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetChannelMask(nint self, uint channel, uint length, [Out] byte[]? mask, out uint actual);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int CreateDecoder(nint self, string path, nint vendor, uint access, uint cache, out nint decoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFrame(nint self, uint index, out nint frame);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int InitializeFile(nint self, string path, uint access);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateEncoder(nint self, ref Guid container, nint vendor, out nint encoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeEncoder(nint self, nint stream, uint cache);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeRotator(nint self, nint source, uint transform);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeScaler(nint self, nint source, uint width, uint height, uint interpolation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeConverter(nint self, nint source, ref Guid format, uint dither, nint palette, double alpha, uint paletteType);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSize(nint self, out uint width, out uint height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetResolution(nint self, out double dpiX, out double dpiY);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CopyPixels(nint self, nint rect, uint stride, uint size, [Out] byte[] pixels);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateFrame(nint self, out nint frame, out nint options);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeFrame(nint self, nint options);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSize(nint self, uint width, uint height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetResolution(nint self, double dpiX, double dpiY);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetPixelFormat(nint self, ref Guid format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeProfile(nint self, byte[] bytes, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetColorContexts(nint self, uint count, nint contexts);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetColorContexts(nint self, uint count, nint contexts, out uint actual);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetProfileBytes(nint self, uint count, [Out] byte[]? bytes, out uint actual);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WritePixels(nint self, uint lines, uint stride, uint size, byte[] pixels);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Commit(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int GetMetadata(nint self, string name, out NativePropertyVariant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WriteProperty(nint self, uint count, ref PropertyInfo property, ref Variant value);
}
