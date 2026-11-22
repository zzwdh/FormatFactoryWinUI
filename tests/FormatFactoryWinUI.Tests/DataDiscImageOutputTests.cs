using FormatFactoryWinUI.Disc;

namespace FormatFactoryWinUI.Tests;

public sealed class DataDiscImageOutputTests
{
    [Fact]
    public void CopyDataDiscToIso_CommitsCompleteImageAfterClosingOutput()
    {
        using var directory = new TemporaryDirectory();
        var destination = OpticalOutputStaging.PrepareFile(Path.Combine(directory.Path, "disc.iso"), ".iso");
        var bytes = new byte[1024 * 1024 + 37];
        new Random(37).NextBytes(bytes);
        using var source = new MemoryStream(bytes, writable: false);

        var result = WindowsOpticalDiscReader.CopyDataDiscToIso(
            destination, source.Length,
            (buffer, requested, _) => source.Read(buffer, 0, requested),
            null, CancellationToken.None);

        Assert.Equal(destination.FinalPath, result);
        Assert.Equal(bytes, File.ReadAllBytes(result));
        Assert.False(File.Exists(destination.StagingPath));
        using var exclusive = new FileStream(result, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(bytes.Length, exclusive.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyDataDiscToIso_DiscReadFailureRemovesPartialOutput(bool throwReadError)
    {
        using var directory = new TemporaryDirectory();
        var destination = OpticalOutputStaging.PrepareFile(Path.Combine(directory.Path, "disc.iso"), ".iso");
        var reads = 0;

        Assert.ThrowsAny<IOException>(() => WindowsOpticalDiscReader.CopyDataDiscToIso(
            destination, 2048,
            (buffer, _, _) =>
            {
                if (reads++ > 0)
                {
                    if (throwReadError) throw new IOException("simulated unreadable sector");
                    return 0;
                }
                Array.Fill(buffer, (byte)42, 0, 512);
                return 512;
            },
            null, CancellationToken.None));

        Assert.False(File.Exists(destination.FinalPath));
        Assert.False(File.Exists(destination.StagingPath));
    }

    [Fact]
    public void CopyDataDiscToIso_CancellationAfterLastReadDoesNotCommitImage()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var destination = OpticalOutputStaging.PrepareFile(Path.Combine(directory.Path, "disc.iso"), ".iso");

        Assert.Throws<OperationCanceledException>(() => WindowsOpticalDiscReader.CopyDataDiscToIso(
            destination, 2048,
            (buffer, requested, _) =>
            {
                Array.Fill(buffer, (byte)42, 0, requested);
                cancellation.Cancel();
                return requested;
            },
            null, cancellation.Token));

        Assert.False(File.Exists(destination.FinalPath));
        Assert.False(File.Exists(destination.StagingPath));
    }

    [Fact]
    public void CopyDataDiscToIso_PreservesFileCreatedBeforeCommit()
    {
        using var directory = new TemporaryDirectory();
        var destination = OpticalOutputStaging.PrepareFile(Path.Combine(directory.Path, "disc.iso"), ".iso");
        File.WriteAllText(destination.FinalPath, "existing output");
        using var source = new MemoryStream(new byte[2048], writable: false);

        Assert.Throws<IOException>(() => WindowsOpticalDiscReader.CopyDataDiscToIso(
            destination, source.Length,
            (buffer, requested, _) => source.Read(buffer, 0, requested),
            null, CancellationToken.None));

        Assert.Equal("existing output", File.ReadAllText(destination.FinalPath));
        Assert.False(File.Exists(destination.StagingPath));
    }
}