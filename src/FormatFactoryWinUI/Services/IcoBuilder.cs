namespace FormatFactoryWinUI.Services;

public static class IcoBuilder
{
    public static void Build(IEnumerable<(int Size, string PngPath)> frames, string outputPath)
    {
        var entries = frames.Select(frame => (frame.Size, Data: File.ReadAllBytes(frame.PngPath))).ToArray();
        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)entries.Length);

        var dataOffset = 6 + entries.Length * 16;
        foreach (var entry in entries)
        {
            writer.Write((byte)(entry.Size >= 256 ? 0 : entry.Size));
            writer.Write((byte)(entry.Size >= 256 ? 0 : entry.Size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)entry.Data.Length);
            writer.Write((uint)dataOffset);
            dataOffset += entry.Data.Length;
        }

        foreach (var entry in entries)
        {
            writer.Write(entry.Data);
        }
    }
}
