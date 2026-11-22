using System.Buffers.Binary;
using FormatFactoryWinUI.Disc;

namespace FormatFactoryWinUI.Tests;

public sealed class WindowsOpticalNativeTests
{
    [Theory]
    [InlineData("e:\\", @"\\.\E:")]
    [InlineData("E:", @"\\.\E:")]
    [InlineData(@"\\.\e:", @"\\.\E:")]
    public void DevicePath_NormalizesDriveRoots(string source, string expected)
    {
        Assert.Equal(expected, OpticalDevicePath.Normalize(source));
    }

    [Theory]
    [InlineData("C:\\folder")]
    [InlineData("not-a-drive")]
    [InlineData(@"\\server\share")]
    public void DevicePath_RejectsNonRootPaths(string source)
    {
        Assert.Throws<ArgumentException>(() => OpticalDevicePath.Normalize(source));
    }

    [Fact]
    public void TocParser_ProducesAudioAndDataTrackRanges()
    {
        var toc = BuildToc(
            (1, false, 0),
            (2, true, 150),
            (0xAA, false, 300));

        var result = CdTableOfContentsParser.Parse(toc);

        Assert.Equal((byte)1, result.FirstTrack);
        Assert.Equal((byte)2, result.LastTrack);
        Assert.Equal(300, result.LeadOutLba);
        Assert.Collection(
            result.Tracks,
            track =>
            {
                Assert.Equal((byte)1, track.Number);
                Assert.False(track.IsData);
                Assert.Equal(0, track.StartLba);
                Assert.Equal(150, track.EndLba);
                Assert.Equal(TimeSpan.FromSeconds(2), track.Duration);
            },
            track =>
            {
                Assert.Equal((byte)2, track.Number);
                Assert.True(track.IsData);
                Assert.Equal(150, track.StartLba);
                Assert.Equal(300, track.EndLba);
            });
        Assert.Single(result.AudioTracks);
    }

    [Fact]
    public void TocParser_RejectsMissingLeadOut()
    {
        var toc = BuildToc((1, false, 0));

        Assert.Throws<InvalidDataException>(() => CdTableOfContentsParser.Parse(toc));
    }

    [Fact]
    public void CdAudioWave_WritesCanonicalHeaderAndSwapsSamples()
    {
        var pcm = new byte[] { 0x12, 0x34, 0xFE, 0xDC };
        CdAudioWave.ConvertBigEndianPcmToWaveInPlace(pcm);
        var header = CdAudioWave.CreateHeader(pcm.Length);

        Assert.Equal(new byte[] { 0x34, 0x12, 0xDC, 0xFE }, pcm);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40, 4)));
    }

    [Fact]
    public void IsoLayout_NormalizesExistingSourcesAndVolumeLabel()
    {
        using var directory = new TemporaryDirectory();
        var inputFile = Path.Combine(directory.Path, "input.txt");
        var inputDirectory = Path.Combine(directory.Path, "资料");
        File.WriteAllText(inputFile, "content");
        Directory.CreateDirectory(inputDirectory);
        var output = Path.Combine(directory.Path, "output.iso");

        var validated = IsoImageBuildLayout.Validate(new IsoImageBuildRequest(
            [inputFile, inputDirectory],
            output,
            "  ARCHIVE  "));

        Assert.Equal("ARCHIVE", validated.VolumeLabel);
        Assert.Equal(Path.GetFullPath(output), validated.OutputPath);
        Assert.All(validated.SourcePaths, path => Assert.True(Path.IsPathFullyQualified(path)));
    }

    [Fact]
    public void IsoLayout_RejectsDuplicateSourcesAndInvalidFileSystems()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "input.txt");
        File.WriteAllText(input, "content");

        Assert.Throws<ArgumentException>(() => IsoImageBuildLayout.Validate(
            new IsoImageBuildRequest([input, input], Path.Combine(directory.Path, "a.iso"))));
        Assert.Throws<ArgumentOutOfRangeException>(() => IsoImageBuildLayout.Validate(
            new IsoImageBuildRequest(
                [input],
                Path.Combine(directory.Path, "b.iso"),
                FileSystems: (IsoFileSystems)128)));
        Assert.Throws<ArgumentException>(() => IsoImageBuildLayout.Validate(
            new IsoImageBuildRequest(
                [input],
                Path.Combine(directory.Path, "c.iso"),
                FileSystems: IsoFileSystems.Joliet | IsoFileSystems.Udf)));
    }

    [Fact]
    public async Task IsoImageBuilder_CreatesImageWithoutOpticalDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "hello.txt"), "hello from IMAPI2FS");
        var output = Path.Combine(directory.Path, "built.iso");

        var result = await new WindowsIsoImageBuilder().CreateAsync(
            new IsoImageBuildRequest([source], output, "FF_TEST"));

        Assert.Equal(Path.GetFullPath(output), result.OutputPath);
        Assert.True(File.Exists(output));
        Assert.Equal(result.Length, new FileInfo(output).Length);
        Assert.True(result.Length > 32 * 1024);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.partial.iso"));
    }

    [Fact]
    public void OpticalByteComparer_ReturnsFirstMismatchOrLengthBoundary()
    {
        Assert.Null(OpticalByteComparer.FindFirstMismatch([1, 2, 3], [1, 2, 3]));
        Assert.Equal(1, OpticalByteComparer.FindFirstMismatch([1, 9, 3], [1, 2, 3]));
        Assert.Equal(2, OpticalByteComparer.FindFirstMismatch([1, 2, 3], [1, 2]));
    }

    [Fact]
    public void RecorderCatalog_NormalizesSafeArrayValues()
    {
        var result = OpticalRecorderCatalog.NormalizeVolumePaths(
            new object?[] { " E:\\ ", "d:\\", null, "e:\\" });

        Assert.Equal(new[] { "d:\\", "E:\\" }, result);
    }

    [Fact]
    public void OutputStaging_UsesSiblingPartialAndRejectsExistingDestination()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "disc.iso");

        var staging = OpticalOutputStaging.PrepareFile(output, ".iso");

        Assert.Equal(Path.GetFullPath(output), staging.FinalPath);
        Assert.Equal(Path.GetDirectoryName(output), Path.GetDirectoryName(staging.StagingPath));
        Assert.EndsWith(".partial.iso", staging.StagingPath, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(output, "existing");
        Assert.Throws<IOException>(() => OpticalOutputStaging.PrepareFile(output, ".iso"));
    }

    private static byte[] BuildToc(params (int Number, bool IsData, int Lba)[] entries)
    {
        var result = new byte[4 + entries.Length * 8];
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(0, 2), checked((ushort)(result.Length - 2)));
        result[2] = 1;
        result[3] = checked((byte)entries
            .Where(entry => entry.Number != 0xAA)
            .Select(entry => entry.Number)
            .DefaultIfEmpty(1)
            .Max());
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var offset = 4 + index * 8;
            result[offset + 1] = entry.IsData ? (byte)0x04 : (byte)0;
            result[offset + 2] = checked((byte)entry.Number);
            var absoluteFrame = entry.Lba + 150;
            result[offset + 5] = checked((byte)(absoluteFrame / (60 * 75)));
            result[offset + 6] = checked((byte)((absoluteFrame / 75) % 60));
            result[offset + 7] = checked((byte)(absoluteFrame % 75));
        }

        return result;
    }
}
