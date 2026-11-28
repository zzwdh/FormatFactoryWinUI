using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

internal static class CsoImageCodec
{
    internal const int HeaderSize = 24;
    internal const int DefaultBlockSize = 2048;
    internal const uint PlainBlockFlag = 0x8000_0000u;
    internal const uint OffsetMask = 0x7fff_ffffu;
    private const int MaximumBlockSize = 16 * 1024 * 1024;
    private const int IoBufferSize = 128 * 1024;

    internal sealed record Layout(
        long TotalBytes,
        int BlockSize,
        int BlockCount,
        byte Alignment,
        uint[] Index,
        long FileLength);

    internal static async Task WriteAsync(
        string isoPath,
        string csoPath,
        int blockSize,
        IProgress<DiscImageConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateBlockSize(blockSize);
        await using var input = new FileStream(
            isoPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            IoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ProcessResourcePolicy.TrySetFileIoPriority(input, FileIoPriority.Low);
        if (input.Length <= 0)
        {
            throw new InvalidDataException(LocalizationService.T("ConversionUiIsoImageEmpty"));
        }

        var totalBytes = input.Length;
        var blockCount64 = DivideRoundUp(totalBytes, blockSize);
        if (blockCount64 > int.MaxValue - 1L)
        {
            throw new InvalidDataException(LocalizationService.T("ConversionUiIsoTooManyCsoBlocks"));
        }

        var blockCount = checked((int)blockCount64);
        var index = new uint[blockCount + 1];
        var tableLength = checked((long)index.Length * sizeof(uint));
        var dataStart = checked(HeaderSize + tableLength);
        var alignment = ChooseAlignment(totalBytes, blockCount, dataStart);
        var alignmentUnit = 1L << alignment;

        await using var output = new FileStream(
            csoPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            IoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ProcessResourcePolicy.TrySetFileIoPriority(output, FileIoPriority.Low);

        var header = CreateHeader(totalBytes, blockSize, alignment);
        await output.WriteAsync(header, cancellationToken);
        output.SetLength(dataStart);
        output.Position = dataStart;

        progress?.Report(new DiscImageConversionProgress(0, totalBytes, 0, blockCount));
        var lastProgress = Stopwatch.GetTimestamp();
        using var compressionBuffer = new MemoryStream(blockSize);
        var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            long processed = 0;
            for (var block = 0; block < blockCount; block++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PadToAlignmentAsync(output, alignmentUnit, cancellationToken);
                index[block] = EncodeOffset(output.Position, alignment);

                var expected = checked((int)Math.Min(blockSize, totalBytes - processed));
                await ReadExactlyAsync(input, buffer.AsMemory(0, expected), cancellationToken);
                var compressed = Compress(buffer.AsSpan(0, expected), compressionBuffer);
                var compressedStoredLength = RoundUp(compressed.Length, alignmentUnit);
                var plainStoredLength = RoundUp(expected, alignmentUnit);
                if (compressedStoredLength < plainStoredLength)
                {
                    await output.WriteAsync(compressed, cancellationToken);
                }
                else
                {
                    index[block] |= PlainBlockFlag;
                    await output.WriteAsync(buffer.AsMemory(0, expected), cancellationToken);
                }

                processed += expected;
                ReportProgress(progress, processed, totalBytes, block + 1, blockCount, ref lastProgress);
            }

            await PadToAlignmentAsync(output, alignmentUnit, cancellationToken);
            index[^1] = EncodeOffset(output.Position, alignment);
            await WriteIndexAsync(output, index, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async Task<Layout> ReadLayoutAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("The reference decoder needs a readable, seekable CSO stream.", nameof(input));
        }

        input.Position = 0;
        var header = new byte[HeaderSize];
        try { await ReadExactlyAsync(input, header, cancellationToken); }
        catch (IOException error)
        {
            FormatFailureDiagnostics.Record(error, new(FormatFailureStage.CsoHeader, FormatFailureReason.HeaderRead));
            throw;
        }
        var headerFacts = new FormatFailureFact(FormatFailureStage.CsoHeader, FormatFailureReason.Unknown)
        {
            HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)),
            TotalBytes = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8, 8)),
            BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4)),
            Version = header[20], Alignment = header[21],
            Reserved = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2))
        };
        if (!header.AsSpan(0, 4).SequenceEqual("CISO"u8))
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoMagicInvalid")), headerFacts with { Reason = FormatFailureReason.Magic });
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        // The original v1 writer left this field zero; its index still starts at byte 24.
        // Retain the fixed layout and all version, index and payload checks below.
        if (headerSize != 0 && headerSize != HeaderSize)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoHeaderSizeInvalid")), headerFacts with { Reason = FormatFailureReason.HeaderSize });
        }

        var totalBytesUnsigned = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8, 8));
        if (totalBytesUnsigned == 0 || totalBytesUnsigned > long.MaxValue)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoUncompressedSizeInvalid")), headerFacts with { Reason = FormatFailureReason.TotalSize });
        }

        var blockSizeUnsigned = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
        if (blockSizeUnsigned > int.MaxValue)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoBlockSizeInvalid")), headerFacts with { Reason = FormatFailureReason.BlockSize });
        }

        var blockSize = checked((int)blockSizeUnsigned);
        try { ValidateBlockSize(blockSize); }
        catch (InvalidDataException error)
        {
            FormatFailureDiagnostics.Record(error, headerFacts with { Reason = FormatFailureReason.BlockSize });
            throw;
        }
        if (header[20] != 1)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoVersion1Only")), headerFacts with { Reason = FormatFailureReason.Version });
        }

        var alignment = header[21];
        if (alignment > 31)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoAlignmentInvalid")), headerFacts with { Reason = FormatFailureReason.Alignment });
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2)) != 0)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoReservedHeaderInvalid")), headerFacts with { Reason = FormatFailureReason.Reserved });
        }

        var totalBytes = checked((long)totalBytesUnsigned);
        var blockCount64 = DivideRoundUp(totalBytes, blockSize);
        if (blockCount64 > int.MaxValue - 1L)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoTooManyBlocks")), headerFacts with { Reason = FormatFailureReason.BlockCount });
        }

        var blockCount = checked((int)blockCount64);
        var indexLength = checked(blockCount + 1);
        var tableEnd = checked((long)HeaderSize + ((long)indexLength * sizeof(uint)));
        if (tableEnd > input.Length)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoIndexTruncated")), headerFacts with { Reason = FormatFailureReason.IndexTruncated, Stage = FormatFailureStage.CsoIndex });
        }

        var index = new uint[indexLength];

        try { await ReadIndexAsync(input, index, cancellationToken); }
        catch (IOException error)
        {
            FormatFailureDiagnostics.Record(error, headerFacts with { Stage = FormatFailureStage.CsoIndex, Reason = FormatFailureReason.IndexTruncated });
            throw;
        }
        if ((index[^1] & PlainBlockFlag) != 0)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoFinalIndexFlagInvalid")), headerFacts with { Reason = FormatFailureReason.FinalIndexFlag, Stage = FormatFailureStage.CsoIndex });
        }

        var previousOffset = DecodeOffset(index[0], alignment);
        if (previousOffset < tableEnd)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoDataOverlapsIndex")), headerFacts with { Reason = FormatFailureReason.IndexOverlap, Stage = FormatFailureStage.CsoIndex });
        }

        for (var block = 0; block < blockCount; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = DecodeOffset(index[block], alignment);
            var end = DecodeOffset(index[block + 1], alignment);
            if (start != previousOffset || end <= start || end > input.Length)
            {
                throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoBlockOffsetRangeInvalid", block)), headerFacts with { Reason = FormatFailureReason.OffsetRange, Stage = FormatFailureStage.CsoIndex, Index = block, Start = start, End = end });
            }

            if ((index[block] & PlainBlockFlag) != 0)
            {
                var expected = Math.Min((long)blockSize, totalBytes - ((long)block * blockSize));
                var stored = end - start;
                var alignmentUnit = 1L << alignment;
                if (stored < expected || stored - expected >= alignmentUnit)
                {
                    throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoPlainBlockStoredSizeInvalid", block)), headerFacts with { Reason = FormatFailureReason.PlainBlockSize, Stage = FormatFailureStage.CsoIndex, Index = block, Start = start, End = end });
                }
            }

            previousOffset = end;
        }

        if (previousOffset != input.Length)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoDataBeyondFinalIndex")), headerFacts with { Reason = FormatFailureReason.TrailingData, Stage = FormatFailureStage.CsoIndex });
        }

        return new Layout(totalBytes, blockSize, blockCount, alignment, index, input.Length);
    }

    internal static async Task DecodeAsync(
        Stream input,
        Stream output,
        Layout layout,
        IProgress<DiscImageConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The reference decoder needs a writable ISO stream.", nameof(output));
        }

        progress?.Report(new DiscImageConversionProgress(0, layout.TotalBytes, 0, layout.BlockCount));
        var lastProgress = Stopwatch.GetTimestamp();
        var extra = new byte[1];
        var buffer = ArrayPool<byte>.Shared.Rent(layout.BlockSize);
        var observedBlock = -1;
        try
        {
            long processed = 0;
            for (var block = 0; block < layout.BlockCount; block++)
            {
                observedBlock = block;
                cancellationToken.ThrowIfCancellationRequested();
                var expected = checked((int)Math.Min(layout.BlockSize, layout.TotalBytes - processed));
                var start = DecodeOffset(layout.Index[block], layout.Alignment);
                var end = DecodeOffset(layout.Index[block + 1], layout.Alignment);
                input.Position = start;

                if ((layout.Index[block] & PlainBlockFlag) != 0)
                {
                    await ReadExactlyAsync(input, buffer.AsMemory(0, expected), cancellationToken);
                }
                else
                {
                    using var bounded = new BoundedReadStream(input, end - start);
                    using var inflater = new DeflateStream(bounded, CompressionMode.Decompress, leaveOpen: true);
                    await ReadExactlyAsync(inflater, buffer.AsMemory(0, expected), cancellationToken);
                    if (await inflater.ReadAsync(extra, cancellationToken) != 0)
                    {
                        throw new InvalidDataException(LocalizationService.T("ConversionUiCsoBlockExpandsBeyondDeclaredSize", block));
                    }
                }

                await output.WriteAsync(buffer.AsMemory(0, expected), cancellationToken);
                processed += expected;
                ReportProgress(progress, processed, layout.TotalBytes, block + 1, layout.BlockCount, ref lastProgress);
            }

            if (processed != layout.TotalBytes)
            {
                throw new InvalidDataException(LocalizationService.T("ConversionUiCsoDecodedSizeMismatch"));
            }
        }
        catch (InvalidDataException error)
        {
            FormatFailureDiagnostics.Record(error, new(FormatFailureStage.CsoDecode, FormatFailureReason.DecodedSize)
            { TotalBytes = (ulong)layout.TotalBytes, BlockSize = (ulong)layout.BlockSize, Index = observedBlock, InputLength = layout.FileLength });
            throw;
        }
        catch (IOException exception)
        {
            throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiCsoBlockDataTruncatedOrInvalid"), exception),
                new(FormatFailureStage.CsoDecode, FormatFailureReason.DecodeRead)
                { TotalBytes = (ulong)layout.TotalBytes, BlockSize = (ulong)layout.BlockSize, Index = observedBlock, InputLength = layout.FileLength });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] CreateHeader(long totalBytes, int blockSize, byte alignment)
    {
        var header = new byte[HeaderSize];
        "CISO"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8, 8), checked((ulong)totalBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), checked((uint)blockSize));
        header[20] = 1;
        header[21] = alignment;
        return header;
    }

    private static ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> source, MemoryStream compressed)
    {
        compressed.SetLength(0);
        compressed.Position = 0;
        using (var deflater = new DeflateStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflater.Write(source);
        }

        // The caller awaits the write before this buffer is reused for the next block.
        return compressed.GetBuffer().AsMemory(0, checked((int)compressed.Length));
    }

    private static void ReportProgress(
        IProgress<DiscImageConversionProgress>? progress,
        long processed,
        long totalBytes,
        int completedBlocks,
        int blockCount,
        ref long lastProgress)
    {
        if (progress is null) return;
        var now = Stopwatch.GetTimestamp();
        if (completedBlocks != blockCount &&
            Stopwatch.GetElapsedTime(lastProgress, now) < TimeSpan.FromMilliseconds(200)) return;
        lastProgress = now;
        progress.Report(new DiscImageConversionProgress(processed, totalBytes, completedBlocks, blockCount));
    }

    private static async Task WriteIndexAsync(
        FileStream output,
        IReadOnlyList<uint> index,
        CancellationToken cancellationToken)
    {
        output.Position = HeaderSize;
        var bytes = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var entry = 0;
            while (entry < index.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(bytes.Length / sizeof(uint), index.Count - entry);
                for (var offset = 0; offset < count; offset++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes.AsSpan(offset * sizeof(uint), sizeof(uint)),
                        index[entry + offset]);
                }

                await output.WriteAsync(bytes.AsMemory(0, count * sizeof(uint)), cancellationToken);
                entry += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static async Task ReadIndexAsync(
        Stream input,
        uint[] index,
        CancellationToken cancellationToken)
    {
        input.Position = HeaderSize;
        var bytes = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var entry = 0;
            while (entry < index.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(bytes.Length / sizeof(uint), index.Length - entry);
                var length = count * sizeof(uint);
                await ReadExactlyAsync(input, bytes.AsMemory(0, length), cancellationToken);
                for (var offset = 0; offset < count; offset++)
                {
                    index[entry + offset] = BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(offset * sizeof(uint), sizeof(uint)));
                }

                entry += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static async Task PadToAlignmentAsync(
        Stream output,
        long alignmentUnit,
        CancellationToken cancellationToken)
    {
        var padding = checked((int)((alignmentUnit - (output.Position % alignmentUnit)) % alignmentUnit));
        if (padding == 0)
        {
            return;
        }

        var zeros = ArrayPool<byte>.Shared.Rent(Math.Min(padding, 64 * 1024));
        try
        {
            Array.Clear(zeros, 0, zeros.Length);
            var remaining = padding;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, zeros.Length);
                await output.WriteAsync(zeros.AsMemory(0, count), cancellationToken);
                remaining -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream input,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await input.ReadAsync(destination[read..], cancellationToken);
            if (count == 0)
            {
                throw new InvalidDataException(LocalizationService.T("ConversionUiUnexpectedEndOfStream"));
            }

            read += count;
        }
    }

    private static byte ChooseAlignment(long totalBytes, int blockCount, long dataStart)
    {
        for (byte alignment = 0; alignment <= 31; alignment++)
        {
            var unit = 1L << alignment;
            var maximumLength = (decimal)dataStart + totalBytes + ((decimal)(blockCount + 1L) * (unit - 1L));
            var maximumEncodedOffset = decimal.Ceiling(maximumLength / unit);
            if (maximumEncodedOffset <= OffsetMask && maximumLength <= long.MaxValue)
            {
                return alignment;
            }
        }

        throw new InvalidDataException(LocalizationService.T("ConversionUiIsoTooLargeForCsoV1"));
    }

    private static uint EncodeOffset(long offset, byte alignment)
    {
        var unit = 1L << alignment;
        if (offset < 0 || offset % unit != 0)
        {
            throw new InvalidDataException("Unaligned block offset in the reference encoder.");
        }

        var encoded = offset / unit;
        if (encoded > OffsetMask)
        {
            throw new InvalidDataException(LocalizationService.T("ConversionUiCsoBlockOffsetExceedsV1"));
        }

        return checked((uint)encoded);
    }

    private static long DecodeOffset(uint entry, byte alignment)
    {
        var offset = (ulong)(entry & OffsetMask) << alignment;
        if (offset > long.MaxValue)
        {
            throw new InvalidDataException("Block offset exceeds the reference decoder's maximum file size.");
        }

        return checked((long)offset);
    }

    private static long DivideRoundUp(long value, int divisor) =>
        checked(((value - 1L) / divisor) + 1L);

    private static long RoundUp(long value, long unit) =>
        checked(((value + unit - 1L) / unit) * unit);

    internal static void ValidateBlockSize(int blockSize)
    {
        if (blockSize < 512 || blockSize > MaximumBlockSize || !IsPowerOfTwo(blockSize))
        {
            throw new InvalidDataException(LocalizationService.T("ConversionUiCsoBlockSizePowerOfTwo"));
        }
    }

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;

    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        private long _remaining = length >= 0
            ? length
            : throw new ArgumentOutOfRangeException(nameof(length));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, _remaining);
            if (count == 0) return 0;
            var read = inner.Read(buffer, offset, count);
            _remaining -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, _remaining);
            if (count == 0) return 0;
            var read = inner.Read(buffer[..count]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var count = (int)Math.Min(buffer.Length, _remaining);
            if (count == 0) return 0;
            var read = await inner.ReadAsync(buffer[..count], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
