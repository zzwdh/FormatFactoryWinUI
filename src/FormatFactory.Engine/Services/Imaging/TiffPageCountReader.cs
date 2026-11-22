using System.Buffers.Binary;

namespace FormatFactoryWinUI.Services;

internal static class TiffPageCountReader
{
    public static int? Read(string path, CancellationToken token = default)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        if (stream.Read(header[..4]) != 4) return null;
        var littleEndian = header[..2].SequenceEqual("II"u8);
        if (!littleEndian && !header[..2].SequenceEqual("MM"u8)) return null;
        ushort UInt16(ReadOnlySpan<byte> bytes) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
        uint UInt32(ReadOnlySpan<byte> bytes) => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        ulong UInt64(ReadOnlySpan<byte> bytes) => littleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var version = UInt16(header[2..]);
        if (version is not (42 or 43)) return null;
        var bigTiff = version == 43;
        stream.ReadExactly(header.Slice(4, bigTiff ? 12 : 4));
        if (bigTiff && (UInt16(header[4..]) != 8 || UInt16(header[6..]) != 0)) throw InvalidTiff();
        var offset = bigTiff ? UInt64(header[8..]) : UInt32(header[4..]);
        var visited = new HashSet<ulong>();
        Span<byte> value = stackalloc byte[8];
        while (offset != 0)
        {
            token.ThrowIfCancellationRequested();
            if (visited.Count >= 100_000 || !visited.Add(offset) || offset > (ulong)stream.Length ||
                offset < (bigTiff ? 16u : 8u)) throw InvalidTiff();
            stream.Position = checked((long)offset);
            stream.ReadExactly(value[..(bigTiff ? 8 : 2)]);
            var entries = bigTiff ? UInt64(value) : UInt16(value);
            var entrySize = bigTiff ? 20u : 12u;
            var nextOffsetSize = bigTiff ? 8u : 4u;
            var remaining = (ulong)(stream.Length - stream.Position);
            if (remaining < nextOffsetSize || entries > (remaining - nextOffsetSize) / entrySize) throw InvalidTiff();
            stream.Position += checked((long)(entries * entrySize));
            stream.ReadExactly(value[..(int)nextOffsetSize]);
            offset = bigTiff ? UInt64(value) : UInt32(value);
        }
        if (visited.Count == 0) throw InvalidTiff();
        return visited.Count;
    }

    private static InvalidDataException InvalidTiff() =>
        new(LocalizationService.T("NativeImageValidationFailed"));
}
