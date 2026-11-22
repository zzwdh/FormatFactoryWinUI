using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Tests.TestDoubles;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaImportOwnershipTests
{
    [Fact]
    public async Task PersistedBatch_SameSourceRestoresDistinctIdsAndKeepsRemainingOwner()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "input.docx");
        File.WriteAllText(path, "document");
        var first = Single(path);
        var second = Single(path);
        second.TargetFormatId = "txt";
        var coordinator = new MediaImportCoordinator();

        var restored = await coordinator.ImportPersistedBatchAsync(string.Empty, [first, second, first]);

        Assert.Empty(restored.Failures);
        Assert.Equal([first.TaskId, second.TaskId], restored.AddedTasks.Select(task => task.Id));
        Assert.Equal("txt", restored.AddedTasks[1].TargetFormatId);
        Assert.Equal(1, restored.IgnoredCount);
        coordinator.Forget(restored.AddedTasks[0]);
        Assert.Equal(1, (await coordinator.ImportPathsAsync(string.Empty, [path])).IgnoredCount);
        coordinator.Forget(restored.AddedTasks[1]);
        Assert.Single((await coordinator.ImportPathsAsync(string.Empty, [path])).AddedTasks);
    }

    [Fact]
    public async Task PersistedId_AlreadyImportingIsIgnoredWithoutReservingItsOtherPath()
    {
        using var directory = new TemporaryDirectory();
        var firstPath = Path.Combine(directory.Path, "first.mp4");
        var secondPath = Path.Combine(directory.Path, "second.docx");
        File.WriteAllText(firstPath, "video");
        File.WriteAllText(secondPath, "document");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new MediaImportCoordinator(async (_, _, token) =>
        {
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
            return new MediaProbeResult { Kind = MediaKind.Video };
        });
        var persisted = Single(firstPath, "mp4");
        var duplicate = Single(secondPath);
        duplicate.TaskId = persisted.TaskId;
        var importing = coordinator.ImportPersistedAsync("ffprobe", persisted);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await coordinator.ImportPersistedAsync(string.Empty, duplicate)).IsIgnored);
            Assert.Single((await coordinator.ImportPathsAsync(string.Empty, [secondPath])).AddedTasks);
        }
        finally
        {
            finish.TrySetResult();
        }
        Assert.NotNull((await importing).Task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledSharedImport_OnlyReleasesItsOwnReservation(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "input.mp4");
        File.WriteAllText(path, "video");
        using var cancellation = new CancellationTokenSource();
        var fail = false;
        var coordinator = new MediaImportCoordinator((_, _, token) =>
        {
            if (fail)
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                throw new InvalidDataException("probe failed");
            }
            return Task.FromResult(new MediaProbeResult { Kind = MediaKind.Video });
        });
        var original = Assert.IsType<MediaTask>((await coordinator.ImportPersistedAsync("ffprobe", Single(path, "mp4"))).Task);
        var retryItem = Single(path, "mp4");
        fail = true;

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                coordinator.ImportPersistedAsync("ffprobe", retryItem, cancellation.Token));
        }
        else
        {
            var failed = await coordinator.ImportPersistedAsync("ffprobe", retryItem);
            Assert.Null(failed.Task);
            Assert.IsType<InvalidDataException>(failed.Error);
        }
        Assert.Equal(1, (await coordinator.ImportPathsAsync("ffprobe", [path])).IgnoredCount);
        fail = false;
        var retry = Assert.IsType<MediaTask>((await coordinator.ImportPersistedAsync("ffprobe", retryItem)).Task);
        Assert.Equal(retryItem.TaskId, retry.Id);
        coordinator.Forget(original);
        Assert.Equal(1, (await coordinator.ImportPathsAsync("ffprobe", [path])).IgnoredCount);
        coordinator.Forget(retry);
        Assert.Single((await coordinator.ImportPathsAsync("ffprobe", [path])).AddedTasks);
    }

    [Fact]
    public async Task PersistedBatch_CancellationReleasesNewCopiesAndPreservesExistingTask()
    {
        using var directory = new TemporaryDirectory();
        var document = Path.Combine(directory.Path, "input.docx");
        var video = Path.Combine(directory.Path, "input.mp4");
        File.WriteAllText(document, "document");
        File.WriteAllText(video, "video");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new MediaImportCoordinator(async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new MediaProbeResult { Kind = MediaKind.Video };
        });
        var original = Assert.IsType<MediaTask>((await coordinator.ImportPersistedAsync(string.Empty, Single(document))).Task);
        var clone = Single(document);
        using var cancellation = new CancellationTokenSource();
        var importing = coordinator.ImportPersistedBatchAsync("ffprobe", [clone, Single(video, "mp4")], cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);

        Assert.Equal(1, (await coordinator.ImportPathsAsync(string.Empty, [document])).IgnoredCount);
        var restoredClone = Assert.IsType<MediaTask>((await coordinator.ImportPersistedAsync(string.Empty, clone)).Task);
        coordinator.Forget(restoredClone);
        Assert.Equal(1, (await coordinator.ImportPathsAsync(string.Empty, [document])).IgnoredCount);
        coordinator.Forget(original);
        Assert.Single((await coordinator.ImportPathsAsync(string.Empty, [document])).AddedTasks);
    }

    [Fact]
    public async Task NewImageCollection_ConflictReleasesEarlierInputsAndKeepsExistingTask()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "first.png");
        var second = Path.Combine(directory.Path, "second.png");
        File.WriteAllText(first, "image");
        File.WriteAllText(second, "image");
        var coordinator = CreateCoordinator();
        var existing = Assert.Single((await coordinator.ImportPathsAsync("ffprobe", [second])).AddedTasks);

        var conflicting = await coordinator.ImportImageCollectionAsync(string.Empty, [first, second]);

        Assert.Null(conflicting.Task);
        Assert.IsType<InvalidDataException>(conflicting.Error);
        var subsequent = await coordinator.ImportPathsAsync("ffprobe", [first, second]);
        Assert.Equal(first, Assert.Single(subsequent.AddedTasks).InputPath);
        Assert.Equal(1, subsequent.IgnoredCount);
        coordinator.Forget(existing);
        Assert.Single((await coordinator.ImportPathsAsync("ffprobe", [second])).AddedTasks);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("png")]
    public async Task SharedGroup_RetainsEveryInputUntilItsLastOwnerIsRemoved(string extension)
    {
        using var directory = new TemporaryDirectory();
        var first = Group(directory.Path, extension);
        var second = CopyGroup(first);
        var coordinator = CreateCoordinator();
        var restored = await coordinator.ImportPersistedBatchAsync("ffprobe", [first, second]);

        Assert.Empty(restored.Failures);
        Assert.Equal([first.TaskId, second.TaskId], restored.AddedTasks.Select(task => task.Id));
        Assert.All(restored.AddedTasks, task => Assert.Equal(first.Inputs.Select(input => input.Path), task.InputPaths));
        coordinator.Forget(restored.AddedTasks[0]);
        Assert.Equal(2, (await coordinator.ImportPathsAsync("ffprobe", first.Inputs.Select(input => input.Path))).IgnoredCount);
        coordinator.Forget(restored.AddedTasks[1]);
        Assert.Equal(2, (await coordinator.ImportPathsAsync("ffprobe", first.Inputs.Select(input => input.Path))).AddedTasks.Count);
    }

    [Theory]
    [InlineData("docx", false)]
    [InlineData("docx", true)]
    [InlineData("png", false)]
    [InlineData("png", true)]
    public async Task SharedGroup_FailureOrCancellationDoesNotReleaseExistingInputs(string extension, bool cancel)
    {
        using var directory = new TemporaryDirectory();
        var first = Group(directory.Path, extension);
        var storage = new ImportStorageAccessFake();
        var coordinator = CreateCoordinator(storage);
        var original = Assert.IsType<MediaTask>((await coordinator.ImportPersistedAsync("ffprobe", first)).Task);
        var retryItem = CopyGroup(first);
        var lastPath = Path.Combine(directory.Path, "last." + extension);
        retryItem.Inputs.Add(new PersistedConversionInput { Path = lastPath, FormatId = extension });
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            File.WriteAllText(lastPath, "last");
            storage.FileResolving = path => { if (path == lastPath) cancellation.Cancel(); };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                coordinator.ImportPersistedAsync("ffprobe", retryItem, cancellation.Token));
            storage.FileResolving = null;
        }
        else
        {
            var failed = await coordinator.ImportPersistedAsync("ffprobe", retryItem);
            Assert.Null(failed.Task);
            Assert.NotNull(failed.Error);
            File.WriteAllText(lastPath, "last");
        }

        Assert.Equal(2, (await coordinator.ImportPathsAsync("ffprobe", first.Inputs.Select(input => input.Path))).IgnoredCount);
        var retry = Assert.IsType<MediaTask>((await coordinator.ImportPersistedAsync("ffprobe", retryItem)).Task);
        Assert.Equal(retryItem.TaskId, retry.Id);
        coordinator.Forget(original);
        Assert.Equal(3, (await coordinator.ImportPathsAsync("ffprobe", retryItem.Inputs.Select(input => input.Path))).IgnoredCount);
        coordinator.Forget(retry);
        Assert.Equal(3, (await coordinator.ImportPathsAsync("ffprobe", retryItem.Inputs.Select(input => input.Path))).AddedTasks.Count);
    }

    [Fact]
    public async Task Forget_UnregisteredTaskDoesNotReleaseAnExistingTask()
    {
        using var directory = new TemporaryDirectory();
        var persisted = Group(directory.Path, "docx");
        var coordinator = new MediaImportCoordinator();
        var original = Assert.IsType<MediaTask>((await coordinator.ImportPersistedAsync(string.Empty, persisted)).Task);
        var foreign = new MediaTask { InputPath = original.InputPath };
        foreach (var input in original.Inputs) foreign.Inputs.Add(input);

        coordinator.Forget(foreign);

        Assert.Equal(2, (await coordinator.ImportPathsAsync(string.Empty, original.InputPaths)).IgnoredCount);
        coordinator.Forget(original);
        Assert.Equal(2, (await coordinator.ImportPathsAsync(string.Empty, original.InputPaths)).AddedTasks.Count);
    }

    [Fact]
    public async Task RememberTask_RegistersSharedPathsOnceAndSurvivesOriginalRemoval()
    {
        using var directory = new TemporaryDirectory();
        var persisted = Group(directory.Path, "docx");
        var coordinator = new MediaImportCoordinator();
        var original = Assert.IsType<MediaTask>((await coordinator.ImportPersistedAsync(string.Empty, persisted)).Task);
        var historyClone = new MediaTask { InputPath = original.InputPath };
        foreach (var input in original.Inputs) historyClone.Inputs.Add(input);

        coordinator.RememberTask(historyClone);
        coordinator.RememberTask(historyClone);
        coordinator.Forget(original);

        Assert.Equal(2, (await coordinator.ImportPathsAsync(string.Empty, historyClone.InputPaths)).IgnoredCount);
        coordinator.Forget(historyClone);
        Assert.Equal(2, (await coordinator.ImportPathsAsync(string.Empty, historyClone.InputPaths)).AddedTasks.Count);
    }

    [Fact]
    public async Task OpticalDisc_RememberedCloneKeepsDeviceReservedAfterOriginalRemoval()
    {
        var disc = new OpticalDiscInfo("e:/", @"\\.\E:", "DISC", OpticalDiscMediaKind.DvdVideo, true, true, 0);
        var coordinator = new MediaImportCoordinator();
        var original = Assert.IsType<MediaTask>((await coordinator.ImportOpticalDiscAsync(disc)).Task);
        var clone = new MediaTask { InputPath = disc.RootPath, ContentKind = ContentKind.Disc };
        coordinator.RememberTask(clone);
        coordinator.Forget(original);

        Assert.True((await coordinator.ImportOpticalDiscAsync(disc)).IsIgnored);
        coordinator.Forget(clone);
        Assert.NotNull((await coordinator.ImportOpticalDiscAsync(disc)).Task);
    }

    private static MediaImportCoordinator CreateCoordinator(IImportStorageAccess? storage = null) =>
        new((_, path, _) => Task.FromResult(new MediaProbeResult
        {
            ContentKind = Path.GetExtension(path) == ".png" ? ContentKind.Image : ContentKind.Video
        }), storageAccess: storage);

    private static PersistedQueueItem Single(string path, string format = "docx") => new()
    {
        TaskId = Guid.NewGuid(), InputPath = path, SourceFormatId = format,
        ContentKind = format == "mp4" ? ContentKind.Video : ContentKind.Document,
        TargetFormatId = format == "mp4" ? "mp4" : "pdf"
    };

    private static PersistedQueueItem Group(string directory, string extension)
    {
        var paths = Enumerable.Range(0, 2).Select(index => Path.Combine(directory, $"input-{index}.{extension}")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "input");
        return new PersistedQueueItem
        {
            TaskId = Guid.NewGuid(), InputPath = paths[0], SourceFormatId = extension,
            ContentKind = extension == "png" ? ContentKind.Image : ContentKind.Document,
            TargetFormatId = "pdf",
            Inputs = paths.Select(path => new PersistedConversionInput { Path = path, FormatId = extension }).ToList()
        };
    }

    private static PersistedQueueItem CopyGroup(PersistedQueueItem original) => new()
    {
        TaskId = Guid.NewGuid(), InputPath = original.InputPath, SourceFormatId = original.SourceFormatId,
        ContentKind = original.ContentKind, TargetFormatId = original.TargetFormatId,
        Inputs = original.Inputs.Select(input => new PersistedConversionInput
        {
            Path = input.Path, FormatId = input.FormatId, AccessToken = input.AccessToken
        }).ToList()
    };
}
