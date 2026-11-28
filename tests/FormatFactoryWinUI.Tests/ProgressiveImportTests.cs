using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class ProgressiveImportTests
{
    [Fact]
    public async Task PublishedBatchesKeepOrderAndOwnershipWhenNextBatchIsCancelled()
    {
        using var directory = new TemporaryDirectory();
        var paths = Enumerable.Range(0, 20).Select(index => Path.Combine(directory.Path, $"{index}.docx")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "fixture");
        var importer = new MediaImportCoordinator();
        using var cancellation = new CancellationTokenSource();
        var published = new List<MediaTask>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportPathsProgressivelyAsync("", paths, batch =>
        {
            published.AddRange(batch.AddedTasks);
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellationToken: cancellation.Token));
        Assert.Equal(paths.Take(8), published.Select(task => task.InputPath));
        var retry = await importer.ImportPathsAsync("", paths);
        Assert.Equal(8, retry.IgnoredCount);
        Assert.Equal(paths.Skip(8), retry.AddedTasks.Select(task => task.InputPath));
        foreach (var task in published.Concat(retry.AddedTasks)) importer.Forget(task);
        Assert.Equal(20, (await importer.ImportPathsAsync("", paths)).AddedTasks.Count);
    }

    [Fact]
    public async Task RejectedPublicationReleasesOnlyUnpublishedBatch()
    {
        using var directory = new TemporaryDirectory();
        var paths = Enumerable.Range(0, 12).Select(index => Path.Combine(directory.Path, $"{index}.docx")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "fixture");
        var importer = new MediaImportCoordinator();
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => importer.ImportPathsProgressivelyAsync("", paths, _ =>
            ++calls == 1 ? Task.CompletedTask : Task.FromException(new IOException("dispatcher unavailable"))));
        var retry = await importer.ImportPathsAsync("", paths);
        Assert.Equal(8, retry.IgnoredCount);
        Assert.Equal(paths.Skip(8), retry.AddedTasks.Select(task => task.InputPath));
    }

    [Fact]
    public async Task DiscoveryPreservesNaturalOrderAndHonorsCancellation()
    {
        using var directory = new TemporaryDirectory();
        foreach (var name in new[] { "page10.png", "page2.png", "page1.png", "ignore.unknown" })
            File.WriteAllText(Path.Combine(directory.Path, name), "fixture");
        var importer = new MediaImportCoordinator();
        var paths = await importer.DiscoverFolderAsync(directory.Path);
        Assert.Equal(new[] { "page1.png", "page2.png", "page10.png" }, paths.Select(Path.GetFileName));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.DiscoverFolderAsync(directory.Path, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task FolderDiscoveryRecognizesAnExtensionOnlyFilename()
    {
        using var directory = new TemporaryDirectory();
        foreach (var name in new[] { ".mP3", "clip.MP3", ".unknown", "extensionless" })
            File.WriteAllText(Path.Combine(directory.Path, name), "fixture");
        var paths = await new MediaImportCoordinator().DiscoverFolderAsync(directory.Path);
        Assert.Equal(new[] { ".mP3", "clip.MP3" }, paths.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task LargeImportPublishesBeforeFinalProbeAndReportsAccurateCounts()
    {
        using var directory = new TemporaryDirectory();
        var paths = Enumerable.Range(0, 19).Select(index => Path.Combine(directory.Path, $"{index}.docx")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "fixture");
        var access = new TestDoubles.ImportStorageAccessFake
        {
            FileResolving = path => { if (path == paths[^1]) throw new UnauthorizedAccessException("fixture denied"); }
        };
        var importer = new MediaImportCoordinator(storageAccess: access);
        var publishedCounts = new List<int>();
        var reports = new List<MediaImportProgress>();
        var result = await importer.ImportPathsProgressivelyAsync("", paths, batch =>
        {
            publishedCounts.Add(batch.Items.Count);
            return Task.CompletedTask;
        }, progress: new InlineProgress(reports.Add));
        Assert.Equal(new[] { 8, 8, 3 }, publishedCounts);
        Assert.Equal(18, result.AddedTasks.Count);
        Assert.Single(result.Failures);
        Assert.Equal(new MediaImportProgress(false, 19, 19, 1), reports[^1]);
    }

    private sealed class InlineProgress(Action<MediaImportProgress> action) : IProgress<MediaImportProgress>
    {
        public void Report(MediaImportProgress value) => action(value);
    }
}
