using System.Buffers.Binary;
using System.IO.Compression;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class DiscImageConverterTests
{
    [Fact]
    public async Task IsoToCsoToIso_RoundTripsCompressedPlainAndPartialBlocks()
    {
        using var directory = new TemporaryDirectory();
        var iso = Path.Combine(directory.Path, "源.iso");
        var cso = Path.Combine(directory.Path, "压缩.cso");
        var restored = Path.Combine(directory.Path, "还原.iso");
        var content = CreateMixedContent();
        await File.WriteAllBytesAsync(iso, content);

        var converter = new DiscImageConverter();
        await converter.ConvertIsoToCsoAsync(iso, cso);
        await converter.ConvertCsoToIsoAsync(cso, restored);

        Assert.Equal(content, await File.ReadAllBytesAsync(restored));
        var validation = await new DiscImageValidator().ValidateCsoAsync(cso);
        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal(content.Length, validation.UncompressedLength);
        Assert.Equal(2048, validation.BlockSize);
    }

    [Fact]
    public async Task RustAndManagedCodecsCanReadEachOthersOutput()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "cross source.iso");
        var managedCso = Path.Combine(directory.Path, "旧写入.cso");
        var rustCso = Path.Combine(directory.Path, "新写入.cso");
        var restored = Path.Combine(directory.Path, "restored.iso");
        var content = CreateMixedContent();
        await File.WriteAllBytesAsync(source, content);

        var converter = new DiscImageConverter();
        await CsoImageCodec.WriteAsync(source, managedCso, CsoImageCodec.DefaultBlockSize, null, CancellationToken.None);
        await converter.ConvertCsoToIsoAsync(managedCso, restored);
        Assert.Equal(content, await File.ReadAllBytesAsync(restored));

        await converter.ConvertIsoToCsoAsync(source, rustCso);
        await using var input = File.OpenRead(rustCso);
        var layout = await CsoImageCodec.ReadLayoutAsync(input, CancellationToken.None);
        using var decoded = new MemoryStream();
        await CsoImageCodec.DecodeAsync(input, decoded, layout, null, CancellationToken.None);
        Assert.Equal(content, decoded.ToArray());
    }

    [Theory]
    [InlineData(DiscImageFormat.Iso, DiscImageFormat.Cso)]
    [InlineData(DiscImageFormat.Cso, DiscImageFormat.Iso)]
    public async Task MissingWorkerReportsFailureAndLeavesNoPartialOutput(DiscImageFormat from, DiscImageFormat to)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source");
        var output = Path.Combine(directory.Path, "result");
        await File.WriteAllBytesAsync(input, from == DiscImageFormat.Iso
            ? CreateMixedContent() : CreateLegacyCso(plainPartialBlock: true).Cso);
        var missingWorker = Path.Combine(directory.Path, ConversionWorkerProcess.FileName);
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new DiscImageConverter(missingWorker).ConvertAsync(input, from, output, to));

        Assert.Equal(missingWorker, error.FileName);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentDestinationCreationPreservesExistingFileAndRemovesStaging()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.iso");
        var output = Path.Combine(directory.Path, "result.cso");
        await File.WriteAllBytesAsync(input, CreateMixedContent());
        var competingContent = "concurrent content"u8.ToArray();
        var progress = new InlineProgress<DiscImageConversionProgress>(value =>
        {
            if (value.ProcessedBlocks == 0) File.WriteAllBytes(output, competingContent);
        });

        await Assert.ThrowsAnyAsync<IOException>(() =>
            new DiscImageConverter().ConvertIsoToCsoAsync(input, output, progress));
        Assert.Equal(competingContent, await File.ReadAllBytesAsync(output));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task IsoToCso_MarksIncompressibleBlocksAsPlain()
    {
        using var directory = new TemporaryDirectory();
        var iso = Path.Combine(directory.Path, "随机.iso");
        var cso = Path.Combine(directory.Path, "随机.cso");
        var random = new byte[2048 * 4];
        new Random(73921).NextBytes(random);
        await File.WriteAllBytesAsync(iso, random);

        await new DiscImageConverter().ConvertIsoToCsoAsync(iso, cso);

        var bytes = await File.ReadAllBytesAsync(cso);
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        var firstIndex = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4));
        Assert.NotEqual(0u, firstIndex & 0x8000_0000u);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyZeroHeaderSize_ConvertsCompressedPlainAndPartialBlocks(bool plainPartialBlock)
    {
        using var directory = new TemporaryDirectory();
        var cso = Path.Combine(directory.Path, "legacy.cso");
        var restored = Path.Combine(directory.Path, "restored.iso");
        var (bytes, content) = CreateLegacyCso(plainPartialBlock);
        await File.WriteAllBytesAsync(cso, bytes);

        var validation = await new DiscImageValidator().ValidateCsoAsync(cso);

        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal(content.Length, validation.UncompressedLength);
        Assert.Equal(3, validation.BlockCount);
        await new DiscImageConverter().ConvertCsoToIsoAsync(cso, restored);
        Assert.Equal(content, await File.ReadAllBytesAsync(restored));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(cso));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(23u)]
    [InlineData(25u)]
    [InlineData(uint.MaxValue)]
    public async Task LegacyZeroHeaderSize_OtherNoncanonicalHeaderSizesRemainRejected(uint headerSize)
    {
        using var directory = new TemporaryDirectory();
        var cso = Path.Combine(directory.Path, "invalid-header.cso");
        var restored = Path.Combine(directory.Path, "restored.iso");
        var (bytes, _) = CreateLegacyCso(plainPartialBlock: false);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), headerSize);
        await File.WriteAllBytesAsync(cso, bytes);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DiscImageConverter().ConvertCsoToIsoAsync(cso, restored));

        Assert.Equal(LocalizationService.T("ConversionUiCsoHeaderSizeInvalid"), error.Message);
        Assert.False(File.Exists(restored));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(cso));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("version-zero", "ConversionUiCsoVersion1Only")]
    [InlineData("version-two", "ConversionUiCsoVersion1Only")]
    [InlineData("reserved", "ConversionUiCsoReservedHeaderInvalid")]
    [InlineData("index-overlap", "ConversionUiCsoDataOverlapsIndex")]
    [InlineData("index-truncated", "ConversionUiCsoIndexTruncated")]
    public async Task LegacyZeroHeaderSize_DoesNotBypassLayoutValidation(string damage, string messageKey)
    {
        using var directory = new TemporaryDirectory();
        var cso = Path.Combine(directory.Path, "invalid-layout.cso");
        var restored = Path.Combine(directory.Path, "restored.iso");
        var (bytes, _) = CreateLegacyCso(plainPartialBlock: false);
        switch (damage)
        {
            case "version-zero": bytes[20] = 0; break;
            case "version-two": bytes[20] = 2; break;
            case "reserved": bytes[22] = 1; break;
            case "index-overlap": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 24); break;
            case "index-truncated": Array.Resize(ref bytes, 28); break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        await File.WriteAllBytesAsync(cso, bytes);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DiscImageConverter().ConvertCsoToIsoAsync(cso, restored));
        var validation = await new DiscImageValidator().ValidateCsoAsync(cso);

        Assert.Equal(LocalizationService.T(messageKey), error.Message);
        Assert.False(validation.IsValid);
        Assert.Equal(error.Message, validation.Message);
        Assert.False(File.Exists(restored));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(cso));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task LegacyZeroHeaderSize_DoesNotBypassExpandedPayloadSizeValidation()
    {
        using var directory = new TemporaryDirectory();
        var cso = Path.Combine(directory.Path, "invalid-payload.cso");
        var restored = Path.Combine(directory.Path, "restored.iso");
        var (bytes, content) = CreateLegacyCso(plainPartialBlock: false);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), checked((ulong)content.Length - 1));
        await File.WriteAllBytesAsync(cso, bytes);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DiscImageConverter().ConvertCsoToIsoAsync(cso, restored));

        Assert.Equal(LocalizationService.T("ConversionUiCsoBlockExpandsBeyondDeclaredSize", 2), error.Message);
        var diagnostic = FormatFailureDiagnostics.Format(error);
        Assert.Contains("stage=CsoDecode reason=DecodedSize", diagnostic);
        Assert.Contains("index=2", diagnostic);
        Assert.False(File.Exists(restored));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(cso));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Validator_RejectsInvalidMagicAndDecreasingIndex()
    {
        using var directory = new TemporaryDirectory();
        var invalidMagic = Path.Combine(directory.Path, "magic.cso");
        await File.WriteAllBytesAsync(invalidMagic, new byte[64]);
        var validator = new DiscImageValidator();

        var magicResult = await validator.ValidateCsoAsync(invalidMagic);

        Assert.False(magicResult.IsValid);
        var iso = Path.Combine(directory.Path, "source.iso");
        var damaged = Path.Combine(directory.Path, "index.cso");
        await File.WriteAllBytesAsync(iso, CreateMixedContent());
        await new DiscImageConverter().ConvertIsoToCsoAsync(iso, damaged);
        await using (var stream = new FileStream(damaged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var entry = new byte[4];
            stream.Position = 24;
            await stream.ReadExactlyAsync(entry);
            var firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry) & 0x7fff_ffffu;
            BinaryPrimitives.WriteUInt32LittleEndian(entry, firstOffset - 1u);
            stream.Position = 28;
            await stream.WriteAsync(entry);
        }

        var indexResult = await validator.ValidateCsoAsync(damaged);

        Assert.False(indexResult.IsValid);
        Assert.Contains("offset", indexResult.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertCsoToIso_RejectsCorruptInputWithoutCommittingOutput()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "损坏.cso");
        var output = Path.Combine(directory.Path, "不应存在.iso");
        await File.WriteAllBytesAsync(source, "NOT-A-CSO"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DiscImageConverter().ConvertCsoToIsoAsync(source, output));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Validator_RejectsPayloadWhoseExpandedSizeDiffersFromHeader()
    {
        using var directory = new TemporaryDirectory();
        var iso = Path.Combine(directory.Path, "source.iso");
        var cso = Path.Combine(directory.Path, "size.cso");
        var content = CreateMixedContent();
        await File.WriteAllBytesAsync(iso, content);
        await new DiscImageConverter().ConvertIsoToCsoAsync(iso, cso);
        await using (var stream = new FileStream(cso, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var encodedLength = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(encodedLength, checked((ulong)(content.Length - 1)));
            stream.Position = 8;
            await stream.WriteAsync(encodedLength);
        }

        var result = await new DiscImageValidator().ValidateCsoAsync(cso);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task IsoValidator_RejectsEmptyImageAndAcceptsNonEmptyImage()
    {
        using var directory = new TemporaryDirectory();
        var empty = Path.Combine(directory.Path, "empty.iso");
        var valid = Path.Combine(directory.Path, "valid.iso");
        await File.WriteAllBytesAsync(empty, []);
        await File.WriteAllBytesAsync(valid, new byte[2048]);
        var validator = new DiscImageValidator();

        var emptyResult = await validator.ValidateIsoAsync(empty);
        var validResult = await validator.ValidateIsoAsync(valid);

        Assert.False(emptyResult.IsValid);
        Assert.True(validResult.IsValid, validResult.Message);
        Assert.Equal(2048, validResult.UncompressedLength);
    }

    [Fact]
    public async Task Cancellation_RemovesStagingFileAndDoesNotCommitOutput()
    {
        using var directory = new TemporaryDirectory();
        var iso = Path.Combine(directory.Path, "取消源.iso");
        var output = Path.Combine(directory.Path, "取消结果.cso");
        var content = new byte[2048 * 64];
        new Random(8128).NextBytes(content);
        await File.WriteAllBytesAsync(iso, content);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<DiscImageConversionProgress>(value =>
        {
            if (value.ProcessedBlocks >= 1) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DiscImageConverter().ConvertIsoToCsoAsync(
                iso,
                output,
                progress,
                cancellation.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Validator_CancellationIsPropagated()
    {
        using var directory = new TemporaryDirectory();
        var iso = Path.Combine(directory.Path, "source.iso");
        var cso = Path.Combine(directory.Path, "source.cso");
        await File.WriteAllBytesAsync(iso, CreateMixedContent());
        await new DiscImageConverter().ConvertIsoToCsoAsync(iso, cso);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DiscImageValidator().ValidateCsoAsync(cso, cancellation.Token));
    }

    [Fact]
    public async Task Validator_RejectsTruncatedHugeIndexBeforeAllocatingIt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "huge-index.cso");
        var header = new byte[24];
        "CISO"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 24);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), 2048UL * (int.MaxValue - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 2048);
        header[20] = 1;
        await File.WriteAllBytesAsync(path, header);

        var result = await new DiscImageValidator().ValidateCsoAsync(path);

        Assert.False(result.IsValid);
        Assert.Contains("index", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(2048)]
    [InlineData(8192)]
    public async Task RoundTrip_ReusedBuffersPreserveMixedBlocksAndProgressEndpoints(int blockSize)
    {
        using var directory = new TemporaryDirectory();
        var iso = Path.Combine(directory.Path, "mixed.iso");
        var cso = Path.Combine(directory.Path, "mixed.cso");
        var restored = Path.Combine(directory.Path, "restored.iso");
        var content = new byte[blockSize * 32 + 117];
        var random = new Random(8128);
        for (var block = 0; block < 32; block++)
        {
            var span = content.AsSpan(block * blockSize, blockSize);
            if (block % 2 == 0) random.NextBytes(span);
            else span.Fill((byte)block);
        }
        content.AsSpan(blockSize * 32).Fill(0xa3);
        await File.WriteAllBytesAsync(iso, content);
        var encodingProgress = new List<DiscImageConversionProgress>();
        var decodingProgress = new List<DiscImageConversionProgress>();
        var converter = new DiscImageConverter();

        await converter.ConvertIsoToCsoAsync(iso, cso, blockSize,
            new InlineProgress<DiscImageConversionProgress>(encodingProgress.Add));
        await converter.ConvertCsoToIsoAsync(cso, restored,
            new InlineProgress<DiscImageConversionProgress>(decodingProgress.Add));

        Assert.Equal(content, await File.ReadAllBytesAsync(restored));
        foreach (var progress in new[] { encodingProgress, decodingProgress })
        {
            Assert.Equal(0, progress[0].ProcessedBytes);
            Assert.Equal(0, progress[0].ProcessedBlocks);
            Assert.Equal(content.Length, progress[^1].ProcessedBytes);
            Assert.Equal(33, progress[^1].ProcessedBlocks);
            Assert.Equal(33, progress[^1].TotalBlocks);
            Assert.Equal(progress.OrderBy(item => item.ProcessedBytes), progress);
        }
    }

    private static (byte[] Cso, byte[] Content) CreateLegacyCso(bool plainPartialBlock)
    {
        // Build the historical fixed 24-byte v1 layout independently of the product writer.
        // The original ciso tool left the header-size field zero after clearing its header.
        const int blockSize = 2048;
        const int blockCount = 3;
        var content = new byte[blockSize * 2 + 317];
        content.AsSpan(0, blockSize).Fill(0x41);
        new Random(1907).NextBytes(content.AsSpan(blockSize, blockSize));
        content.AsSpan(blockSize * 2).Fill(0xa3);
        var header = new byte[24];
        "CISO"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8, 8), checked((ulong)content.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), blockSize);
        header[20] = 1;
        var index = new uint[blockCount + 1];
        using var output = new MemoryStream();
        output.Write(header);
        output.SetLength(24 + index.Length * sizeof(uint));
        output.Position = output.Length;
        for (var block = 0; block < blockCount; block++)
        {
            index[block] = checked((uint)output.Position);
            var payload = content.AsSpan(block * blockSize, Math.Min(blockSize, content.Length - block * blockSize));
            if (block == 1 || block == 2 && plainPartialBlock)
            {
                index[block] |= 0x8000_0000u;
                output.Write(payload);
            }
            else
            {
                using var deflater = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
                deflater.Write(payload);
            }
        }
        index[^1] = checked((uint)output.Position);
        var bytes = output.ToArray();
        for (var entry = 0; entry < index.Length; entry++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + entry * sizeof(uint), sizeof(uint)), index[entry]);
        return (bytes, content);
    }

    private static byte[] CreateMixedContent()
    {
        var content = new byte[(2048 * 3) + 317];
        Array.Fill<byte>(content, 0x41, 0, 2048);
        new Random(1907).NextBytes(content.AsSpan(2048, 2048));
        for (var index = 4096; index < content.Length; index++)
        {
            content[index] = (byte)(index % 17);
        }

        return content;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
