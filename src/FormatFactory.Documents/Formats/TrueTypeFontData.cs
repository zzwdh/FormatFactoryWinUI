using System.Buffers.Binary;

namespace FormatFactoryWinUI.Documents;

/// <summary>Extracts a TrueType TTC face and checks the actual Unicode cmap, including supplementary characters.</summary>
internal sealed class TrueTypeFontData
{
    public byte[] Bytes { get; private set; }
    private readonly byte[] _mapData;
    private readonly int[] _cmaps;

    public TrueTypeFontData(byte[] source, uint faceIndex)
    {
        Bytes = ExtractFace(source, faceIndex);
        var cmap = FindTable(Bytes, 0x636d6170);
        if (cmap < 0) throw new InvalidDataException("Font has no Unicode character map.");
        var cmapLength = TableLength(Bytes, 0x636d6170);
        _mapData = Bytes.AsSpan(cmap, cmapLength).ToArray();
        var tables = new List<int>();
        for (var index = 0; index < U16(_mapData, 2); index++)
        {
            var entry = 4 + index * 8;
            var platform = U16(_mapData, entry);
            var encoding = U16(_mapData, entry + 2);
            if (platform == 0 || platform == 3 && encoding is 1 or 10)
            {
                var offset = checked((int)U32(_mapData, entry + 4));
                if (U16(_mapData, offset) is 4 or 12) tables.Add(offset);
            }
        }
        _cmaps = tables.Distinct().OrderByDescending(offset => U16(_mapData, offset)).ToArray();
    }

    public void ReleaseFontBytes() => Bytes = [];

    public bool Supports(int scalar)
    {
        var Bytes = _mapData;
        foreach (var offset in _cmaps)
        {
            if (U16(Bytes, offset) == 12)
            {
                var low = 0;
                var high = checked((int)U32(Bytes, offset + 12)) - 1;
                while (low <= high)
                {
                    var middle = low + (high - low) / 2;
                    var group = offset + 16 + middle * 12;
                    var start = U32(Bytes, group);
                    var end = U32(Bytes, group + 4);
                    if (scalar < start) high = middle - 1;
                    else if (scalar > end) low = middle + 1;
                    else return U32(Bytes, group + 8) + (uint)scalar - start != 0;
                }
            }
            else if (scalar <= ushort.MaxValue)
            {
                var count = U16(Bytes, offset + 6) / 2;
                var ends = offset + 14;
                var starts = ends + count * 2 + 2;
                var deltas = starts + count * 2;
                var ranges = deltas + count * 2;
                for (var index = 0; index < count; index++)
                {
                    if (scalar > U16(Bytes, ends + index * 2)) continue;
                    var start = U16(Bytes, starts + index * 2);
                    if (scalar < start) break;
                    var delta = U16(Bytes, deltas + index * 2);
                    var range = U16(Bytes, ranges + index * 2);
                    if (range == 0) return (ushort)(scalar + delta) != 0;
                    var glyph = U16(Bytes, ranges + index * 2 + range + (scalar - start) * 2);
                    return glyph != 0 && (ushort)(glyph + delta) != 0;
                }
            }
        }
        return false;
    }

    internal static byte[] ExtractFace(byte[] source, uint faceIndex)
    {
        var isCollection = U32(source, 0) == 0x74746366;
        if (isCollection && faceIndex >= U32(source, 8)) throw new InvalidDataException("Invalid TTC face index.");
        var offset = isCollection ? checked((int)U32(source, 12 + checked((int)faceIndex * 4))) : 0;
        if (U32(source, offset) is not (0x00010000 or 0x74727565))
            throw new NotSupportedException("The PDF text layer requires a TrueType outline font.");
        if (!isCollection) return source;
        var count = U16(source, offset + 4);
        var headerLength = checked(12 + count * 16);
        var length = headerLength;
        for (var index = 0; index < count; index++)
            length = checked(length + Align(checked((int)U32(source, offset + 12 + index * 16 + 12))));
        var result = new byte[length];
        source.AsSpan(offset, headerLength).CopyTo(result);
        var destination = headerLength;
        var head = -1;
        for (var index = 0; index < count; index++)
        {
            var entry = 12 + index * 16;
            var from = checked((int)U32(source, offset + entry + 8));
            var size = checked((int)U32(source, offset + entry + 12));
            source.AsSpan(from, size).CopyTo(result.AsSpan(destination));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry + 8), (uint)destination);
            if (U32(result, entry) == 0x68656164)
            {
                head = destination;
                result.AsSpan(head + 8, 4).Clear();
            }
            destination += Align(size);
        }
        if (head >= 0)
        {
            uint sum = 0;
            for (var index = 0; index < result.Length; index += 4) sum = unchecked(sum + U32(result, index));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(head + 8), unchecked(0xb1b0afba - sum));
        }
        return result;
    }

    private static int FindTable(byte[] bytes, uint tag)
    {
        for (var index = 0; index < U16(bytes, 4); index++)
        {
            var entry = 12 + index * 16;
            if (U32(bytes, entry) == tag) return checked((int)U32(bytes, entry + 8));
        }
        return -1;
    }
    private static int TableLength(byte[] bytes, uint tag)
    {
        for (var index = 0; index < U16(bytes, 4); index++)
        {
            var entry = 12 + index * 16;
            if (U32(bytes, entry) == tag) return checked((int)U32(bytes, entry + 12));
        }
        return 0;
    }
    private static int Align(int value) => checked((value + 3) & ~3);
    private static ushort U16(byte[] value, int offset) => BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(offset, 2));
    private static uint U32(byte[] value, int offset) => BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(offset, 4));
}
