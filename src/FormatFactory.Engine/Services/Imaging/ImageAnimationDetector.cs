using System.Buffers.Binary;

namespace FormatFactoryWinUI.Services;

/// <summary>Detect animation containers that a still-only installed decoder might expose as one frame.</summary>
public static class ImageAnimationDetector
{
    public static bool HasAnimation(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != header.Length) return false;
        if (header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            stream.Position = 8;
            Span<byte> chunk = stackalloc byte[8];
            while (stream.Position + 12 <= stream.Length)
            {
                stream.ReadExactly(chunk);
                var length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                if (chunk[4..].SequenceEqual("acTL"u8)) return true;
                if (chunk[4..].SequenceEqual("IDAT"u8) || chunk[4..].SequenceEqual("IEND"u8)) return false;
                if (length > stream.Length - stream.Position - 4) return false;
                stream.Seek((long)length + 4, SeekOrigin.Current);
            }
        }
        else if (header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WEBP"u8))
        {
            Span<byte> chunk = stackalloc byte[8];
            while (stream.Position + 8 <= stream.Length)
            {
                stream.ReadExactly(chunk);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                var padding = length & 1u;
                if (chunk[..4].SequenceEqual("ANIM"u8) || chunk[..4].SequenceEqual("ANMF"u8)) return true;
                if (chunk[..4].SequenceEqual("VP8X"u8) && length >= 1)
                {
                    var flags = stream.ReadByte();
                    if ((flags & 0x02) != 0) return true;
                    length--;
                }
                if (length > stream.Length - stream.Position) return false;
                stream.Seek(length + padding, SeekOrigin.Current);
            }
        }
        else if (header[4..8].SequenceEqual("ftyp"u8))
        {
            // AVIF's avis brand means an image sequence, even if the installed
            // Windows decoder only exposes its first image. Bound all header reads.
            long boxLength = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerLength = 8;
            if (boxLength == 1)
            {
                stream.Position = 8;
                Span<byte> extended = stackalloc byte[8];
                if (stream.Read(extended) != 8) return false;
                var unsigned = BinaryPrimitives.ReadUInt64BigEndian(extended);
                if (unsigned > long.MaxValue) return false;
                boxLength = (long)unsigned;
                headerLength = 16;
            }
            if (boxLength < headerLength + 8 || boxLength > stream.Length) return false;
            stream.Position = headerLength;
            var brands = new byte[(int)Math.Min(boxLength - headerLength, 4096)];
            stream.ReadExactly(brands);
            for (var offset = 0; offset + 4 <= brands.Length; offset += 4)
            {
                if (offset == 4) continue; // minor_version is not a brand
                if (brands.AsSpan(offset, 4).SequenceEqual("avis"u8)) return true;
            }
        }
        return false;
    }
}
