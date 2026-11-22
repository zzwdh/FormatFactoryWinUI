using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueStoreBackgroundSaveTests
{
    [Fact]
    public async Task Save_CapturesInputsAndOptionsBeforeReturningToCaller()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var store = new QueueStore(path);
        var task = new MediaTask { InputPath = Path.Combine(directory.Path, "first.png") };
        task.Inputs.Add(new ConversionInput(task.InputPath, "png"));
        task.Options["layout"] = "fixed";
        Task save;
        using (var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            save = store.SaveAsync([task]);
            task.Inputs.Clear();
            task.Options["layout"] = "changed";
            Assert.False(save.IsCompleted);
        }

        await save.WaitAsync(TimeSpan.FromSeconds(10));

        var result = Assert.Single(await new QueueStore(path).LoadAsync());
        Assert.Equal(task.InputPath, Assert.Single(result.Inputs).Path);
        Assert.Equal("fixed", result.Options["layout"]);
    }

    [Fact]
    public async Task OverlappingSaves_CompleteOnlyAfterTheirOrNewerSnapshotIsWritten()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var store = new QueueStore(path);
        var task = new MediaTask { InputPath = Path.Combine(directory.Path, "movie.mp4") };
        var saves = new List<Task>();
        using (var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            for (var index = 0; index < 30; index++)
            {
                task.Options["revision"] = index.ToString();
                saves.Add(store.SaveAsync([task]));
            }
            Assert.All(saves, save => Assert.False(save.IsCompleted));
        }

        await Task.WhenAll(saves).WaitAsync(TimeSpan.FromSeconds(10));

        var result = Assert.Single(await new QueueStore(path).LoadAsync());
        Assert.Equal("29", result.Options["revision"]);
    }

    [Fact]
    public async Task FailedSave_PropagatesErrorAndNextSaveCanRecover()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        await File.WriteAllTextAsync(path, "{broken");
        var store = new QueueStore(path);
        var task = new MediaTask { InputPath = Path.Combine(directory.Path, "movie.mp4") };

        await Assert.ThrowsAnyAsync<JsonException>(() => store.SaveAsync([task]));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "[]");
        await store.SaveAsync([task]);

        Assert.Equal(task.Id, Assert.Single(await new QueueStore(path).LoadAsync()).TaskId);
    }

    [Fact]
    public async Task SavingLocalChanges_PreservesAnotherStoresAddedTask()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var store = new QueueStore(path);
        var first = new MediaTask { InputPath = Path.Combine(directory.Path, "first.mp4") };
        await store.SaveAsync([first]);
        var external = new QueueStore(path);
        await external.MergePendingAsync(
            [new PersistedQueueItem { InputPath = Path.Combine(directory.Path, "second.mp4") }]);

        await store.SaveAsync([]);

        var result = Assert.Single(await new QueueStore(path).LoadAsync());
        Assert.Equal(Path.Combine(directory.Path, "second.mp4"), result.InputPath);
    }
}
