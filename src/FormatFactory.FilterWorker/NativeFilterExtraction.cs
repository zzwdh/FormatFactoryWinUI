using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FormatFactory.FilterWorker;

internal static class NativeFilterExtraction
{
    public static WorkerResponse Extract(string fullPath)
    {
        IFilter? filter = null;
        try
        {
            var loadResult = LoadIFilter(fullPath, IntPtr.Zero, out filter);
            if (loadResult < 0 || filter is null) return WorkerResponse.Failed("not-registered", loadResult);
            // Request indexable text explicitly; some installed filters otherwise emit no chunks.
            var initResult = filter.Init(FilterInit.CanonParagraphs | FilterInit.HardLineBreaks |
                FilterInit.CanonHyphens | FilterInit.CanonSpaces | FilterInit.ApplyIndexAttributes, 0, IntPtr.Zero, out _);
            if (initResult < 0) return WorkerResponse.Failed("initialization-failed", initResult);
            var output = new StringBuilder();
            long encodedBytes = 0;
            while (true)
            {
                var chunkResult = filter.GetChunk(out var chunk);
                if (chunkResult == FilterEEndOfChunks) break;
                if (chunkResult < 0) return WorkerResponse.Failed("chunk-read-failed", chunkResult);
                if ((chunk.Flags & ChunkState.Text) == 0) continue;
                var wroteChunk = false;
                while (true)
                {
                    uint count = TextBufferSize - 1;
                    var buffer = new StringBuilder(TextBufferSize);
                    var textResult = filter.GetText(ref count, buffer);
                    if (textResult == FilterENoMoreText) break;
                    if (textResult < 0) return WorkerResponse.Failed("text-read-failed", textResult);
                    if (count > 0 && buffer.Length > 0)
                    {
                        var text = buffer.ToString(0, Math.Min(buffer.Length, checked((int)count)));
                        encodedBytes += JsonEncodedText.Encode(text, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).EncodedUtf8Bytes.Length;
                        if (encodedBytes > WorkerProtocol.MaximumResponseBytes - 1024) return WorkerResponse.Failed("output-limit");
                        output.Append(text);
                        wroteChunk = true;
                    }
                    if (textResult == FilterSLastText) break;
                }
                if (wroteChunk)
                {
                    encodedBytes += 4; // JSON-escaped CRLF.
                    if (encodedBytes > WorkerProtocol.MaximumResponseBytes - 1024) return WorkerResponse.Failed("output-limit");
                    output.AppendLine();
                }
            }
            return new WorkerResponse(WorkerProtocol.Version, true, output.ToString().Trim(), null);
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return WorkerResponse.Failed("native-failure", ex.HResult);
        }
        finally { Release(filter); }
    }
    private static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private const int TextBufferSize = 4096;
    private const int FilterEEndOfChunks = unchecked((int)0x80041700);
    private const int FilterENoMoreText = unchecked((int)0x80041701);
    private const int FilterSLastText = 0x00041709;

    [DllImport("query.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int LoadIFilter(
        string path,
        IntPtr outer,
        [MarshalAs(UnmanagedType.Interface)] out IFilter? filter);

    [ComImport]
    [Guid("89BCB740-6119-101A-BCB7-00DD010655AF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilter
    {
        [PreserveSig] int Init(FilterInit flags, uint attributeCount, IntPtr attributes, out FilterFlags resultFlags);
        [PreserveSig] int GetChunk(out StatChunk chunk);
        [PreserveSig] int GetText(ref uint bufferCharacterCount, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer);
        [PreserveSig] int GetValue(out IntPtr value);
        [PreserveSig] int BindRegion(ref FilterRegion region, ref Guid interfaceId, out IntPtr value);
    }

    [Flags]
    private enum FilterInit : uint
    {
        CanonParagraphs = 1,
        HardLineBreaks = 2,
        CanonHyphens = 4,
        CanonSpaces = 8,
        ApplyIndexAttributes = 16
    }

    [Flags]
    private enum FilterFlags : uint
    {
        None = 0,
        OleProperties = 1
    }

    [Flags]
    private enum ChunkState : uint
    {
        Text = 1,
        Value = 2,
        FilterOwnedValue = 4
    }

    private enum ChunkBreakType : uint
    {
        NoBreak,
        EndOfWord,
        EndOfSentence,
        EndOfParagraph,
        EndOfChapter
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatChunk
    {
        public uint IdChunk;
        public ChunkBreakType BreakType;
        public ChunkState Flags;
        public uint Locale;
        public FullPropSpec Attribute;
        public uint IdChunkSource;
        public uint StartSource;
        public uint LengthSource;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FullPropSpec
    {
        public Guid PropertySet;
        public PropSpec Property;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropSpec
    {
        [FieldOffset(0)] public uint Kind;
        [FieldOffset(8)] public uint PropertyId;
        [FieldOffset(8)] public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterRegion
    {
        public uint IdChunk;
        public uint Start;
        public uint Extent;
    }
}

