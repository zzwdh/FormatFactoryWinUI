using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ConversionHistoryStoreTests
{
    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("{}")]
    public Task MalformedHistory_ReadIsSafeButRecordingPreservesOriginal(string contents) =>
        AssertUnreadableHistoryIsPreservedAsync<JsonException>(contents);

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"InputPaths\":null}]")]
    [InlineData("[{\"InputPaths\":[null]}]")]
    [InlineData("[{\"OutputPath\":null}]")]
    [InlineData("[{\"DisplayName\":null}]")]
    [InlineData("[{\"TargetFormatId\":null}]")]
    public Task InvalidHistoryRecords_ReadIsSafeButRecordingPreservesOriginal(string contents) =>
        AssertUnreadableHistoryIsPreservedAsync<InvalidDataException>(contents);

    [Fact]
    public async Task LockedHistory_ReadIsSafeButRecordingDoesNotReplaceOriginal()
    {
        using var directory = new TemporaryDirectory();
        var historyPath = Path.Combine(directory.Path, "history.json");
        await File.WriteAllTextAsync(historyPath, "[]");
        var store = new ConversionHistoryStore(historyPath);
        var task = CreateCompletedTask(directory.Path);

        using (var fileLock = new FileStream(historyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(await store.SearchAsync("result"));
            await Assert.ThrowsAsync<IOException>(() => store.RecordCompletedAsync(task));
        }

        Assert.Equal("[]", await File.ReadAllTextAsync(historyPath));
    }

    [Fact]
    public async Task CancelledRead_StillPropagatesCancellation()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new ConversionHistoryStore(Path.Combine(directory.Path, "history.json"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SearchAsync("result", cancellation.Token));
    }

    [Fact]
    public async Task RecordCompleted_RoundTripsAndSearchesValidHistory()
    {
        using var directory = new TemporaryDirectory();
        var historyPath = Path.Combine(directory.Path, "history.json");
        var store = new ConversionHistoryStore(historyPath);
        var task = CreateCompletedTask(directory.Path);
        var recorded = await store.RecordCompletedAsync(task);
        Assert.NotNull(recorded);

        var restoredStore = new ConversionHistoryStore(historyPath);
        var restored = Assert.Single(await restoredStore.LoadAsync());
        Assert.Equal(task.Id, restored.TaskId);
        Assert.Equal(task.OutputPath, restored.OutputPath);
        Assert.Equal(task.InputPaths, restored.InputPaths);
        Assert.Equal(task.Id, Assert.Single(await restoredStore.SearchAsync("RESULT")).TaskId);
        Assert.Equal(task.Id, Assert.Single(await restoredStore.SearchAsync(" source ")).TaskId);
        Assert.Equal(task.Id, Assert.Single(await restoredStore.SearchAsync("pdf")).TaskId);
        Assert.Empty(await restoredStore.SearchAsync("missing"));

        await restoredStore.RecordCompletedAsync(task);
        Assert.Single(await store.LoadAsync());
    }

    [Fact]
    public async Task CachedHistory_ReturnsIndependentRecordsAndInputLists()
    {
        using var directory = new TemporaryDirectory();
        var store = new ConversionHistoryStore(Path.Combine(directory.Path, "history.json"));
        var task = CreateCompletedTask(directory.Path);
        await store.RecordCompletedAsync(task);
        var item = Assert.Single(await store.LoadAsync());
        item.DisplayName = "mutated";
        ((string[])item.InputPaths)[0] = "mutated-input";

        var restored = Assert.Single(await store.LoadAsync());

        Assert.Equal("result.pdf", restored.DisplayName);
        Assert.Equal(task.InputPath, Assert.Single(restored.InputPaths));
    }

    [Fact]
    public async Task CachedHistory_RefreshesAfterExternalReplacementAndDeletion()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.json");
        var store = new ConversionHistoryStore(path);
        await store.RecordCompletedAsync(CreateCompletedTask(directory.Path));
        Assert.Single(await store.LoadAsync());

        await File.WriteAllTextAsync(path, "[]");

        Assert.Empty(await store.LoadAsync());
        File.Delete(path);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task WarmCache_DoesNotHideLockedHistoryOrPermitDamagedFileReplacement()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.json");
        var store = new ConversionHistoryStore(path);
        var task = CreateCompletedTask(directory.Path);
        await store.RecordCompletedAsync(task);
        Assert.Single(await store.LoadAsync());
        using (var fileLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(await store.LoadAsync());
            await Assert.ThrowsAsync<IOException>(() => store.RecordCompletedAsync(task));
        }

        await File.WriteAllTextAsync(path, "[]");
        Assert.Empty(await store.LoadAsync());
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "{}");
        File.SetLastWriteTimeUtc(path, timestamp);

        await Assert.ThrowsAsync<JsonException>(() => store.RecordCompletedAsync(task));
        Assert.Equal("{}", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RecordCompleted_KeepsOneThousandMostRecentItems()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.json");
        var items = Enumerable.Range(0, 1000).Select(index => new ConversionHistoryItem
        {
            TaskId = Guid.NewGuid(),
            OutputPath = Path.Combine(directory.Path, index + ".pdf"),
            DisplayName = index + ".pdf"
        }).ToArray();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(items));
        var store = new ConversionHistoryStore(path);
        var task = CreateCompletedTask(directory.Path);

        await store.RecordCompletedAsync(task);
        var restored = await store.LoadAsync();

        Assert.Equal(1000, restored.Count);
        Assert.Equal(task.Id, restored[0].TaskId);
        Assert.Equal(items[998].TaskId, restored[^1].TaskId);
        Assert.DoesNotContain(restored, item => item.TaskId == items[999].TaskId);
    }

    private static async Task AssertUnreadableHistoryIsPreservedAsync<TException>(string contents)
        where TException : Exception
    {
        using var directory = new TemporaryDirectory();
        var historyPath = Path.Combine(directory.Path, "history.json");
        await File.WriteAllTextAsync(historyPath, contents);
        var store = new ConversionHistoryStore(historyPath);

        Assert.Empty(await store.LoadAsync());
        Assert.Empty(await store.SearchAsync("result"));
        await Assert.ThrowsAsync<TException>(() => store.RecordCompletedAsync(CreateCompletedTask(directory.Path)));
        Assert.Equal(contents, await File.ReadAllTextAsync(historyPath));
    }

    private static MediaTask CreateCompletedTask(string directory)
    {
        var outputPath = Path.Combine(directory, "result.pdf");
        File.WriteAllText(outputPath, "converted document");
        return new MediaTask
        {
            InputPath = Path.Combine(directory, "source.docx"),
            OutputPath = outputPath,
            ContentKind = ContentKind.Document,
            SourceFormatId = "docx",
            TargetFormatId = "pdf",
            State = ConversionState.Completed
        };
    }
}
