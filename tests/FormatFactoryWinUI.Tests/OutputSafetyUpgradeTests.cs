using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Disc;

namespace FormatFactoryWinUI.Tests;

public sealed class OutputSafetyUpgradeTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void NamingTemplateAndPreservedDirectory_ApplyToFileAndDirectoryOutputs()
    {
        using var directory = new TemporaryDirectory();
        var sourceRoot = Path.Combine(directory.Path, "source");
        var task = new MediaTask
        {
            InputPath = Path.Combine(sourceRoot, "album", "book.pdf"), SourceRoot = sourceRoot,
            ContentKind = ContentKind.Document, TargetFormatId = "epub"
        };
        var settings = new AppSettings
        {
            OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"),
            OutputFileNameTemplate = "{NAME}_{format}", PreserveFolderStructure = true
        };
        using var file = new OutputPathService().Reserve(task, settings);
        using var folder = new OutputPathService().Reserve(task, settings, " tracks", isDirectory: true);

        Assert.Equal(Path.Combine(settings.CustomOutputDirectory, "album", "book_EPUB.epub"), file.FinalPath);
        Assert.Equal(Path.Combine(settings.CustomOutputDirectory, "album", "book_EPUB tracks"), folder.FinalPath);
    }

    [Theory]
    [InlineData("{unknown}")]
    [InlineData("../{name}")]
    [InlineData("CON")]
    [InlineData("LPT1.txt")]
    [InlineData("..")]
    public void InvalidNamingTemplates_AreRejectedBeforeWriting(string template)
    {
        Assert.NotNull(OutputPathService.ValidateFileNameTemplate(template));
        Assert.Throws<ArgumentException>(() => OutputPathService.BuildFileName(
            new MediaTask { InputPath = "input.pdf", TargetFormatId = "pdf" },
            new AppSettings { OutputFileNameTemplate = template }));
    }

    [Fact]
    public void UntrustedSourceName_CannotEscapeOutputDirectory()
    {
        var task = new MediaTask { InputPath = "input.iso", TargetFormatId = "cso" };
        var settings = new AppSettings { OutputFileNameTemplate = "{name}_{date}" };
        var name = OutputPathService.BuildFileName(task, settings, "../../unsafe:disc");

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.Matches(@"_\d{8}$", name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedLease_RemovesOnlyItsPrivateTemporaryOutput(bool directoryOutput)
    {
        using var directory = new TemporaryDirectory();
        var task = new MediaTask { InputPath = Path.Combine(directory.Path, "sample.iso"), TargetFormatId = "cso" };
        var lease = new OutputPathService().Reserve(task,
            new AppSettings { OutputMode = "custom", CustomOutputDirectory = directory.Path },
            directoryOutput ? " tracks" : ".cso", directoryOutput);
        if (directoryOutput)
        {
            Directory.CreateDirectory(lease.TemporaryPath);
            File.WriteAllText(Path.Combine(lease.TemporaryPath, "track.wav"), "partial output");
        }
        else File.WriteAllText(lease.TemporaryPath, "partial output");
        File.WriteAllText(lease.FinalPath, "unrelated file created before commit");

        Assert.ThrowsAny<IOException>(() => lease.Commit());
        lease.Dispose();
        lease.Dispose();

        Assert.False(File.Exists(lease.TemporaryPath));
        Assert.False(Directory.Exists(lease.TemporaryPath));
        Assert.Equal("unrelated file created before commit", File.ReadAllText(lease.FinalPath));
    }

    [Fact]
    public void CommittedLease_RetainsCompleteOutputAfterDisposal()
    {
        using var directory = new TemporaryDirectory();
        var lease = new OutputPathService().Reserve(
            new MediaTask { InputPath = Path.Combine(directory.Path, "sample.mov"), TargetFormatId = "mp4" },
            new AppSettings { OutputMode = "custom", CustomOutputDirectory = directory.Path });
        File.WriteAllText(lease.TemporaryPath, "complete output");

        lease.Commit();
        lease.Dispose();

        Assert.Equal("complete output", File.ReadAllText(lease.FinalPath));
        Assert.False(File.Exists(lease.TemporaryPath));
    }

    [Fact]
    public void DiskSpaceBudget_AccountsForOtherTasksAndReleasesFailedReservations()
    {
        var budget = new DiskSpaceBudgetService(folder =>
            new DiskSpaceInfo(folder == "third" ? "another-volume" : "test-volume", 256 * MiB));
        using var first = budget.ReserveBytes(80 * MiB, "first");
        Assert.Throws<IOException>(() => budget.ReserveBytes(128 * MiB, "second"));

        first.Dispose();
        using var next = budget.ReserveBytes(128 * MiB, "second");
        using var otherVolume = budget.ReserveBytes(128 * MiB, "third");
    }

    [Fact]
    public void DiskSpaceBudget_DoesNotCountWrittenBytesTwice()
    {
        var available = 256 * MiB;
        var written = 0L;
        var budget = new DiskSpaceBudgetService(_ => new DiskSpaceInfo("test-volume", available), _ => written);
        using var first = budget.ReserveBytes(128 * MiB, "output", "first.partial");
        available -= 128 * MiB;
        written = 128 * MiB;

        using var next = budget.ReserveBytes(32 * MiB, "output", "second.partial");
    }

    [Fact]
    public void Commit_ReleasesDiskBudgetBeforeProgressObserversOrLeaseDisposal()
    {
        using var directory = new TemporaryDirectory();
        var available = 256 * MiB;
        var budget = new DiskSpaceBudgetService(_ => new DiskSpaceInfo("test-volume", available));
        using var first = new OutputPathService(budget).Reserve(
            new MediaTask
            {
                InputPath = Path.Combine(directory.Path, "disc.iso"), TargetFormatId = "cso",
                ContentKind = ContentKind.Disc, FileSize = 80 * MiB
            },
            new AppSettings { OutputMode = "custom", CustomOutputDirectory = directory.Path });
        using (var stream = File.Create(first.TemporaryPath)) stream.SetLength(96 * MiB);

        first.Commit();
        available -= 96 * MiB;

        // Exactly enough remains for this output plus the common safety margin.
        // The first lease intentionally stays alive throughout the reservation.
        using var next = budget.ReserveBytes(96 * MiB, directory.Path, "next.partial");
        Assert.True(File.Exists(first.FinalPath));
    }

    [Fact]
    public void DiskSpaceBudget_UnavailableProviderDoesNotMeanZeroFreeBytes()
    {
        using var lease = new DiskSpaceBudgetService(_ => null).ReserveBytes(1024 * MiB, "network-folder");
    }

    [Fact]
    public void DiskSpaceEstimate_CountsEveryInputEvenWithoutImportedSizes()
    {
        using var directory = new TemporaryDirectory();
        var task = new MediaTask { ContentKind = ContentKind.Document, TargetFormatId = "pdf" };
        for (var index = 0; index < 2; index++)
        {
            var path = Path.Combine(directory.Path, $"source-{index}.pdf");
            using (var stream = File.Create(path)) stream.SetLength(4 * MiB);
            task.Inputs.Add(new ConversionInput(path, "pdf"));
        }

        Assert.True(DiskSpaceBudgetService.EstimateRequiredBytes(task) >= 8 * MiB * 3);
    }

    [Fact]
    public async Task ParallelDiscImageConversions_PreserveNamingAndCommitDistinctCompleteOutputs()
    {
        using var directory = new TemporaryDirectory();
        var sourceRoot = Path.Combine(directory.Path, "source");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(sourceRoot, "nested")).FullName;
        var input = Path.Combine(sourceDirectory, "disc.iso");
        await File.WriteAllBytesAsync(input, new byte[2048 * 8]);
        var settings = new AppSettings
        {
            OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"),
            OutputFileNameTemplate = "{name}_{format}", PreserveFolderStructure = true
        };
        MediaTask CreateTask() => new()
        {
            InputPath = input, SourceRoot = sourceRoot, SourceFormatId = "iso", TargetFormatId = "cso",
            ContentProbe = new ContentProbeResult { ContentKind = ContentKind.Disc, FormatId = "iso", FileSize = 2048 * 8 }
        };
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
            new DiscConversionService().ConvertAsync(CreateTask(), settings, string.Empty, progress => { }, default)));

        Assert.All(results, result => Assert.True(result.Success, result.Message));
        Assert.Equal(2, results.Select(result => result.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(Path.Combine(settings.CustomOutputDirectory, "nested", "disc_CSO.cso"), results.Select(result => result.OutputPath));
        foreach (var result in results)
            Assert.True((await new DiscImageValidator().ValidateCsoAsync(result.OutputPath)).IsValid);
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.CustomOutputDirectory, "*.partial*", SearchOption.AllDirectories));
    }
}
