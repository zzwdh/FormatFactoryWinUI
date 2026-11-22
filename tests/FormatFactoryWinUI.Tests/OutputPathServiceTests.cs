using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class OutputPathServiceTests
{
    [Fact]
    public void Reserve_EquivalentOutputDirectoriesShareReservations()
    {
        using var directory = new TemporaryDirectory();
        var service = new OutputPathService();
        var task = CreateTask(directory.Path);
        using var first = service.Reserve(task, CreateSettings(directory.Path));
        using var second = service.Reserve(task, CreateSettings(Path.Combine(directory.Path, "nested", "..")));

        Assert.NotEqual(first.FinalPath, second.FinalPath);
        Assert.Equal(Path.Combine(directory.Path, "sample (1).mp4"), second.FinalPath);
    }

    [Fact]
    public void Reserve_PreservesSubdirectoryWhoseNameStartsWithTwoDots()
    {
        using var directory = new TemporaryDirectory();
        var sourceRoot = Path.Combine(directory.Path, "source");
        var task = CreateTask(Path.Combine(sourceRoot, "..draft"));
        task.SourceRoot = sourceRoot;
        var outputDirectory = Path.Combine(directory.Path, "output");

        using var lease = new OutputPathService().Reserve(task, CreateSettings(outputDirectory));

        Assert.Equal(Path.Combine(outputDirectory, "..draft", "sample.mp4"), lease.FinalPath);
    }

    [Fact]
    public void Reserve_SourceRootOnAnotherDriveKeepsCustomOutputDirectory()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(Path.Combine(directory.Path, "input"));
        task.SourceRoot = Path.GetPathRoot(directory.Path)!.Equals(@"Z:\", StringComparison.OrdinalIgnoreCase)
            ? @"Y:\source"
            : @"Z:\source";
        var outputDirectory = Path.Combine(directory.Path, "output");

        using var lease = new OutputPathService().Reserve(task, CreateSettings(outputDirectory));

        Assert.Equal(Path.Combine(outputDirectory, "sample.mp4"), lease.FinalPath);
    }

    [Fact]
    public void Reserve_InputOutsideSourceRootKeepsCustomOutputDirectory()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(Path.Combine(directory.Path, "input"));
        task.SourceRoot = Path.Combine(directory.Path, "other-source");
        var outputDirectory = Path.Combine(directory.Path, "output");

        using var lease = new OutputPathService().Reserve(task, CreateSettings(outputDirectory));

        Assert.Equal(Path.Combine(outputDirectory, "sample.mp4"), lease.FinalPath);
    }

    [Fact]
    public void Dispose_ReleasesFinalPathForReuse()
    {
        using var directory = new TemporaryDirectory();
        var service = new OutputPathService();
        var task = CreateTask(directory.Path);
        var settings = CreateSettings(directory.Path);

        var first = service.Reserve(task, settings);
        using var second = service.Reserve(task, settings);

        Assert.NotEqual(first.FinalPath, second.FinalPath);
        first.Dispose();

        using var reused = service.Reserve(task, settings);
        Assert.Equal(first.FinalPath, reused.FinalPath);

        // Disposing a lease twice must be harmless and must not release another lease.
        first.Dispose();
        using var fourth = service.Reserve(task, settings);
        Assert.NotEqual(reused.FinalPath, fourth.FinalPath);
    }

    [Fact]
    public async Task Reserve_ConcurrentCallersReceiveUniqueFinalAndTemporaryPaths()
    {
        using var directory = new TemporaryDirectory();
        var service = new OutputPathService();
        var task = CreateTask(directory.Path);
        var settings = CreateSettings(directory.Path);

        var leases = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() => service.Reserve(task, settings))));

        try
        {
            Assert.Equal(leases.Length, leases.Select(lease => lease.FinalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(leases.Length, leases.Select(lease => lease.TemporaryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        using var afterRelease = service.Reserve(task, settings);
        Assert.EndsWith("sample.mp4", afterRelease.FinalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyWriteAccess_TransientSharingViolationRetriesAndCleansProbe()
    {
        using var directory = new TemporaryDirectory();
        FileStream? externalReader = null;
        var attempts = 0;
        var delays = new List<int>();
        try
        {
            OutputPathService.VerifyWriteAccess(directory.Path, (source, destination) =>
            {
                if (++attempts == 1)
                    externalReader = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                File.Move(source, destination, overwrite: false);
            }, delay =>
            {
                delays.Add(delay);
                externalReader?.Dispose();
                externalReader = null;
            });

            Assert.Equal(2, attempts);
            Assert.Equal(new[] { 50 }, delays);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        }
        finally { externalReader?.Dispose(); }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void VerifyWriteAccess_PersistentSharingOrLockViolationRemainsFailure(int hresult)
    {
        using var directory = new TemporaryDirectory();
        var expected = new IOException("Synthetic persistent probe conflict", hresult);
        var attempts = 0;
        var delays = new List<int>();

        var actual = Assert.Throws<IOException>(() => OutputPathService.VerifyWriteAccess(directory.Path,
            (_, _) => { attempts++; throw expected; }, delays.Add));

        Assert.Same(expected, actual);
        Assert.Equal(4, attempts);
        Assert.Equal(new[] { 50, 100, 200 }, delays);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void VerifyWriteAccess_AccessDeniedDoesNotRetry()
    {
        using var directory = new TemporaryDirectory();
        var expected = new UnauthorizedAccessException("Synthetic probe access denied");
        var attempts = 0;
        var delays = new List<int>();

        var actual = Assert.Throws<UnauthorizedAccessException>(() => OutputPathService.VerifyWriteAccess(directory.Path,
            (_, _) => { attempts++; throw expected; }, delays.Add));

        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
        Assert.Empty(delays);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void VerifyWriteAccess_OtherIoFailureDoesNotRetry()
    {
        using var directory = new TemporaryDirectory();
        var expected = new IOException("Synthetic probe disk full", unchecked((int)0x80070070));
        var attempts = 0;
        var delays = new List<int>();

        var actual = Assert.Throws<IOException>(() => OutputPathService.VerifyWriteAccess(directory.Path,
            (_, _) => { attempts++; throw expected; }, delays.Add));

        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
        Assert.Empty(delays);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }
    private static MediaTask CreateTask(string directory) => new()
    {
        InputPath = Path.Combine(directory, "sample.mov"),
        TargetFormat = "MP4"
    };

    private static AppSettings CreateSettings(string directory) => new()
    {
        OutputMode = "custom",
        CustomOutputDirectory = directory
    };
}
