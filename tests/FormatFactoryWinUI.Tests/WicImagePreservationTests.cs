using System.Buffers.Binary;
using System.IO.Compression;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class WicImagePreservationTests
{
    [Fact]
    public async Task LosslessGuard_RejectsReal16BitTiffBeforeQuantizationOrOutputCreation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "sRGB Color Space Profile.icm");
        var profile = await File.ReadAllBytesAsync(profilePath);
        var source = Path.Combine(directory.Path, "sixteen-bit.tiff");
        var destination = Path.Combine(directory.Path, "must-not-exist.png");
        var samples = Enumerable.Range(0, 18).SelectMany(index => BitConverter.GetBytes((ushort)(index * 3101 + 1))).ToArray();
        await File.WriteAllBytesAsync(source, CreateTiff(1, profile, samples, 16));
        Assert.True(WicProfilePreservingCodec.HasHighBitDepth(source));
        Assert.Throws<ImagePrecisionUnsupportedException>(() => WicProfilePreservingCodec.EnsureLosslessPrecision(source, "lossless"));
        WicProfilePreservingCodec.EnsureLosslessPrecision(source, "quality");
        await Assert.ThrowsAsync<ImagePrecisionUnsupportedException>(() =>
            WicProfilePreservingCodec.TranscodeAsync(source, destination, "png", "lossless"));
        Assert.False(File.Exists(destination));
        Assert.Equal(profile, WicProfilePreservingCodec.ReadProfile(source));
    }

    [Fact]
    public async Task LosslessVerifier_DetectsChangedRgbAndAlphaInRealPngFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.png");
        var output = Path.Combine(directory.Path, "output.png");
        byte[] rgba = [31, 67, 113, 128, 211, 149, 79, 255];
        await File.WriteAllBytesAsync(input, CreatePng(rgba));
        await File.WriteAllBytesAsync(output, CreatePng(rgba));
        Assert.True(await WicProfilePreservingCodec.ValidateLosslessAsync(input, output));
        var changed = rgba.ToArray();
        changed[0]++;
        await File.WriteAllBytesAsync(output, CreatePng(changed));
        Assert.False(await WicProfilePreservingCodec.ValidateLosslessAsync(input, output));
        changed = rgba.ToArray();
        changed[3] = 255;
        await File.WriteAllBytesAsync(output, CreatePng(changed));
        Assert.False(await WicProfilePreservingCodec.ValidateLosslessAsync(input, output));
    }

    [Fact]
    public async Task LosslessVerifier_ChecksEveryTiffPageAndOriginalProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var profile = await File.ReadAllBytesAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "sRGB Color Space Profile.icm"));
        byte[] first = [31, 67, 113, 211, 149, 79, 41, 59, 73, 83, 97, 101, 107, 127, 131, 137, 139, 151];
        byte[] second = first.Select(value => (byte)(255 - value)).ToArray();
        var input = Path.Combine(directory.Path, "pages.tiff");
        var output = Path.Combine(directory.Path, "output.tiff");
        await File.WriteAllBytesAsync(input, CreateTwoPageTiff(profile, first, second));
        await WicProfilePreservingCodec.TranscodeAsync(input, output, "tiff", "lossless");
        Assert.True(await WicProfilePreservingCodec.ValidateLosslessAsync(input, output));
        var changed = second.ToArray();
        changed[0]++;
        await File.WriteAllBytesAsync(output, CreateTwoPageTiff(profile, first, changed));
        Assert.False(await WicProfilePreservingCodec.ValidateLosslessAsync(input, output));
        await File.WriteAllBytesAsync(output, CreateTiff(1, profile, first));
        Assert.False(await WicProfilePreservingCodec.ValidateLosslessAsync(input, output));
        var replacedProfile = profile.ToArray();
        replacedProfile[84] ^= 1; // Profile identifier; RGB samples and color transform remain unchanged.
        await File.WriteAllBytesAsync(output, CreateTwoPageTiff(replacedProfile, first, second));
        Assert.False(await WicProfilePreservingCodec.ValidateLosslessAsync(input, output));
    }

    [Theory]
    [InlineData(1, 2, 3, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(2, 2, 3, new[] { 1, 0, 3, 2, 5, 4 })]
    [InlineData(3, 2, 3, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(4, 2, 3, new[] { 4, 5, 2, 3, 0, 1 })]
    [InlineData(5, 3, 2, new[] { 0, 2, 4, 1, 3, 5 })]
    [InlineData(6, 3, 2, new[] { 4, 2, 0, 5, 3, 1 })]
    [InlineData(7, 3, 2, new[] { 5, 3, 1, 4, 2, 0 })]
    [InlineData(8, 3, 2, new[] { 1, 3, 5, 0, 2, 4 })]
    public async Task PreserveIcc_NormalizesEveryExifOrientationWithoutChangingColors(
        int orientation, int width, int height, int[] expectedOrder)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "sRGB Color Space Profile.icm");
        var profile = await File.ReadAllBytesAsync(profilePath);
        var source = Path.Combine(directory.Path, "oriented.tiff");
        var destination = Path.Combine(directory.Path, "oriented.png");
        byte[][] colors = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 0], [255, 0, 255], [0, 255, 255]];
        await File.WriteAllBytesAsync(source, CreateTiff(orientation, profile, colors.SelectMany(color => color).ToArray()));
        Assert.Equal(profile, WicProfilePreservingCodec.ReadProfile(source));
        Assert.False(WicProfilePreservingCodec.HasHighBitDepth(source));
        WicProfilePreservingCodec.EnsureLosslessPrecision(source, "lossless");

        await WicProfilePreservingCodec.TranscodeAsync(source, destination, "png", "lossless");
        Assert.True(await WicProfilePreservingCodec.ValidateLosslessAsync(source, destination));

        Assert.Equal(profile, WicProfilePreservingCodec.ReadProfile(destination));
        var decoded = ReadPng(await File.ReadAllBytesAsync(destination));
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(expectedOrder.SelectMany(index => colors[index]).ToArray(), decoded.Rgb);
        var metadata = await new WindowsImagingNativeService().ReadMetadataAsync(destination);
        Assert.True(metadata.IsAvailable, metadata.Message);
        Assert.Equal((ushort)1, metadata.Value!.ExifOrientation);
        Assert.Contains(metadata.Value.ColorContexts, context => context.ProfileByteCount == profile.Length);
    }

    [Fact]
    public async Task CustomSize_WithExifRotation_PreservesRgbProfileAndFitsBounds()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var profile = await File.ReadAllBytesAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "sRGB Color Space Profile.icm"));
        var source = Path.Combine(directory.Path, "rotated.tiff");
        var destination = Path.Combine(directory.Path, "scaled.png");
        await File.WriteAllBytesAsync(source, CreateTiff(6, profile,
            Enumerable.Range(0, 18).Select(value => (byte)(value * 13)).ToArray()));
        await WicProfilePreservingCodec.TranscodeWithOptionsAsync(source, destination, "png", "balanced",
            new MediaConversionOptions { MaxWidth = 2, MaxHeight = 2 });
        Assert.Equal(profile, WicProfilePreservingCodec.ReadProfile(destination));
        var decoded = ReadPng(await File.ReadAllBytesAsync(destination));
        Assert.Equal(2, decoded.Width);
        Assert.Equal(1, decoded.Height);
    }

    [Fact]
    public async Task CustomJpegQuality_AffectsEncodingWithoutDroppingProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var profile = await File.ReadAllBytesAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "sRGB Color Space Profile.icm"));
        var source = Path.Combine(directory.Path, "source.tiff");
        var high = Path.Combine(directory.Path, "high.jpg");
        var low = Path.Combine(directory.Path, "low.jpg");
        await File.WriteAllBytesAsync(source, CreateTiff(1, profile,
            Enumerable.Range(0, 18).Select(value => (byte)(value * 13)).ToArray()));
        await WicProfilePreservingCodec.TranscodeWithOptionsAsync(source, high, "jpg", "balanced", new() { ImageQuality = 95 });
        await WicProfilePreservingCodec.TranscodeWithOptionsAsync(source, low, "jpg", "balanced", new() { ImageQuality = 10 });
        Assert.NotEqual(await File.ReadAllBytesAsync(high), await File.ReadAllBytesAsync(low));
        Assert.Equal(profile, WicProfilePreservingCodec.ReadProfile(high));
        Assert.Equal(profile, WicProfilePreservingCodec.ReadProfile(low));
    }

    private static byte[] CreateTwoPageTiff(byte[] profile, byte[] firstRgb, byte[] secondRgb)
    {
        var first = CreateTiff(1, profile, firstRgb);
        var second = CreateTiff(1, profile, secondRgb);
        var relocation = checked((uint)first.Length - 8);
        const int entryCount = 15;
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(8 + 2 + entryCount * 12), (uint)first.Length);
        for (var entry = 0; entry < entryCount; entry++)
        {
            var offset = 10 + entry * 12;
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(second.AsSpan(offset));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(second.AsSpan(offset + 2));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(offset + 4));
            var size = type switch { 3 => 2u, 4 => 4u, 5 => 8u, 7 => 1u, _ => throw new InvalidDataException() };
            if (tag == 273 || count * size > 4)
                BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(offset + 8),
                    BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(offset + 8)) + relocation);
        }
        return first.Concat(second.Skip(8)).ToArray();
    }

    private static byte[] CreatePng(byte[] rgba)
    {
        using var result = new MemoryStream();
        result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] payload)
        {
            var chunk = new byte[payload.Length + 12];
            BinaryPrimitives.WriteInt32BigEndian(chunk, payload.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
            payload.CopyTo(chunk, 8);
            var crc = uint.MaxValue;
            foreach (var value in chunk.AsSpan(4, payload.Length + 4))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(payload.Length + 8), ~crc);
            result.Write(chunk);
        }
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, rgba.Length / 4);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8; header[9] = 6;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.WriteByte(0);
            zlib.Write(rgba);
        }
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return result.ToArray();
    }

    private static byte[] CreateTiff(int orientation, byte[] profile, byte[] rgb, ushort bitDepth = 8)
    {
        const int entryCount = 15;
        const uint bitsOffset = 8 + 2 + entryCount * 12 + 4;
        const uint dpiOffset = bitsOffset + 6;
        const uint pixelsOffset = dpiOffset + 16;
        var profileOffset = pixelsOffset + (uint)rgb.Length;
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write(8u);
        writer.Write((ushort)entryCount);
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value);
        }
        Entry(256, 4, 1, 2); Entry(257, 4, 1, 3); Entry(258, 3, 3, bitsOffset);
        Entry(259, 3, 1, 1); Entry(262, 3, 1, 2); Entry(273, 4, 1, pixelsOffset);
        Entry(274, 3, 1, (uint)orientation); Entry(277, 3, 1, 3); Entry(278, 4, 1, 3);
        Entry(279, 4, 1, (uint)rgb.Length); Entry(282, 5, 1, dpiOffset); Entry(283, 5, 1, dpiOffset + 8);
        Entry(284, 3, 1, 1); Entry(296, 3, 1, 2); Entry(34675, 7, (uint)profile.Length, profileOffset);
        writer.Write(0u);
        writer.Write(bitDepth); writer.Write(bitDepth); writer.Write(bitDepth);
        writer.Write(96u); writer.Write(1u); writer.Write(96u); writer.Write(1u);
        writer.Write(rgb); writer.Write(profile);
        return memory.ToArray();
    }

    // Independent PNG decoder for 8-bit RGB/RGBA fixtures; it does not call the WIC implementation under test.
    private static (int Width, int Height, byte[] Rgb) ReadPng(byte[] bytes)
    {
        var width = 0; var height = 0; var channels = 0;
        using var compressed = new MemoryStream();
        for (var offset = 8; offset + 12 <= bytes.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            var type = bytes.AsSpan(offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                Assert.Equal(8, data[8]);
                channels = data[9] switch { 2 => 3, 6 => 4, _ => throw new InvalidDataException("Unexpected PNG color type.") };
                Assert.Equal(0, data[12]);
            }
            if (type.SequenceEqual("IDAT"u8)) compressed.Write(data);
            offset += length + 12;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var stride = width * channels;
        var pixels = new byte[stride * height];
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = zlib.ReadByte();
            zlib.ReadExactly(row);
            for (var x = 0; x < stride; x++)
            {
                var a = x >= channels ? pixels[y * stride + x - channels] : 0;
                var b = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                var c = y > 0 && x >= channels ? pixels[(y - 1) * stride + x - channels] : 0;
                var predictor = filter switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("Unexpected PNG filter.")
                };
                pixels[y * stride + x] = unchecked((byte)(row[x] + predictor));
            }
        }
        var rgb = new byte[width * height * 3];
        for (int source = 0, target = 0; source < pixels.Length; source += channels, target += 3)
            pixels.AsSpan(source, 3).CopyTo(rgb.AsSpan(target, 3));
        return (width, height, rgb);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
