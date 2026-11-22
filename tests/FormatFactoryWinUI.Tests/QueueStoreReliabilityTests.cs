using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueStoreReliabilityTests
{
    [Fact]
    public async Task DistinctTaskIdsSharingInput_PreserveBothRoutesAndStableOrder()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var first = new MediaTask { InputPath = "shared.mp4", TargetFormatId = "mp3", ConversionMode = "quality" };
        var second = new MediaTask { InputPath = first.InputPath, TargetFormatId = "mp4", ConversionMode = "fast" };
        var store = new QueueStore(path);
        await store.SaveAsync([first, second]);
        await store.LoadResultAsync();
        await new QueueStore(path).MergePendingAsync([new() { InputPath = "external.pdf", TargetFormatId = "txt" }]);
        await store.SaveAsync([second, first]);
        var result = (await new QueueStore(path).LoadResultAsync()).Items;
        Assert.Equal([second.Id, first.Id], result.Take(2).Select(item => item.TaskId));
        Assert.Equal(["mp4", "mp3"], result.Take(2).Select(item => item.TargetFormatId));
        Assert.Equal("external.pdf", result[2].InputPath);
    }

    [Fact]
    public async Task LegacyRecordWithoutId_LoadThenSaveDoesNotCreateExternalDuplicate()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        await File.WriteAllTextAsync(path, "[{\"InputPath\":\"legacy.mp4\",\"TargetFormat\":\"MP3\"}]");
        var store = new QueueStore(path);
        var initial = Assert.Single((await store.LoadResultAsync()).Items);
        var reread = Assert.Single((await new QueueStore(path).LoadResultAsync()).Items);
        Assert.Equal(initial.TaskId, reread.TaskId);
        await store.SaveAsync([FormatFactoryWinUI.ApplicationServices.QueueSession.FromPersisted(initial)]);
        Assert.Equal(initial.TaskId, Assert.Single((await new QueueStore(path).LoadResultAsync()).Items).TaskId);
    }

    [Fact]
    public async Task RepeatedBackgroundDiscovery_DoesNotOverwriteExistingEditedRoute()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var task = new MediaTask { InputPath = "same.mp4", TargetFormatId = "mp3", ConversionMode = "quality" };
        await new QueueStore(path).SaveAsync([task]);
        var background = new QueueStore(path);
        await background.MergePendingAsync([new() { InputPath = task.InputPath, TargetFormat = "MP4", ConversionMode = "fast" }]);
        var result = Assert.Single((await new QueueStore(path).LoadResultAsync()).Items);
        Assert.Equal(task.Id, result.TaskId);
        Assert.Equal("mp3", result.TargetFormatId);
        Assert.Equal("quality", result.ConversionMode);
    }
    [Fact]
    public async Task CorruptDocument_QuarantinesExactBytesAndRecoversCheckpointThenCanSave()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        const string damaged = "{broken queue bytes";
        await File.WriteAllTextAsync(path, damaged);
        var item = new PersistedQueueItem { TaskId = Guid.NewGuid(), InputPath = "missing.mp4", TargetFormat = "MP4" };
        var store = new QueueStore(path, () => [item]);
        var result = await store.LoadResultAsync();
        Assert.Equal(QueueLoadStatus.CorruptRecovered, result.Status);
        Assert.True(result.CanSave);
        Assert.Equal(item.TaskId, Assert.Single(result.Items).TaskId);
        Assert.NotNull(result.QuarantinePath);
        Assert.Equal(damaged, await File.ReadAllTextAsync(result.QuarantinePath));
        Assert.False(File.Exists(path));
        await store.SaveAsync([new MediaTask { Id = item.TaskId, InputPath = item.InputPath, State = ConversionState.Blocked }]);
        Assert.Equal(ConversionState.Blocked, Assert.Single((await new QueueStore(path).LoadResultAsync()).Items).State);
        Assert.Equal(damaged, await File.ReadAllTextAsync(result.QuarantinePath));
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"Path\":null}]")]
    [InlineData("[{\"Path\":\"\"}]")]
    public async Task InvalidNestedInput_QuarantinesSemanticCorruptionAndRecoversCheckpoint(string inputs)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var damaged = "{\"SchemaVersion\":3,\"Items\":[{\"InputPath\":\"source.mp4\",\"Inputs\":" + inputs + "}]}";
        await File.WriteAllTextAsync(path, damaged);
        var checkpoint = new PersistedQueueItem
        {
            TaskId = Guid.NewGuid(), InputPath = "checkpoint.mp4", TargetFormatId = "mp3"
        };
        var store = new QueueStore(path, () => new[] { checkpoint });

        var result = await store.LoadResultAsync();

        Assert.Equal(QueueLoadStatus.CorruptRecovered, result.Status);
        Assert.IsType<InvalidDataException>(result.Error);
        Assert.True(result.CanSave);
        Assert.Equal(checkpoint.TaskId, Assert.Single(result.Items).TaskId);
        Assert.NotNull(result.QuarantinePath);
        Assert.Equal(damaged, await File.ReadAllTextAsync(result.QuarantinePath));
        Assert.False(File.Exists(path));
        await store.SaveAsync(new[] { FormatFactoryWinUI.ApplicationServices.QueueSession.FromPersisted(checkpoint) });
        Assert.Equal(checkpoint.TaskId, Assert.Single((await new QueueStore(path, () => []).LoadResultAsync()).Items).TaskId);
        Assert.Equal(damaged, await File.ReadAllTextAsync(result.QuarantinePath));
    }

    [Fact]
    public async Task FutureVersion_WithIncompatiblePayloadIsProtectedBeforeDeserializing()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        const string future = "{\"schemaVersion\":99,\"items\":\"a future wire format\"}";
        await File.WriteAllTextAsync(path, future);
        var store = new QueueStore(path, () => []);
        var result = await store.LoadResultAsync();
        Assert.Equal(QueueLoadStatus.FutureVersion, result.Status);
        Assert.False(result.CanSave);
        Assert.Null(result.QuarantinePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([]));
        Assert.Equal(future, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.corrupt-*"));
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("9223372036854775807")]
    public async Task FutureVersionLargerThanInt32_IsProtectedBeforeVersionOverflow(string version)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var future = "{\"schemaVersion\":" + version + ",\"items\":\"a future wire format\"}";
        await File.WriteAllTextAsync(path, future);
        var store = new QueueStore(path, () => []);

        var result = await store.LoadResultAsync();

        Assert.Equal(QueueLoadStatus.FutureVersion, result.Status);
        Assert.IsType<NotSupportedException>(result.Error);
        Assert.False(result.CanSave);
        Assert.Null(result.QuarantinePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([]));
        Assert.Equal(future, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.corrupt-*"));
    }

    [Fact]
    public async Task ReadAccessDenied_IsIoFailureAndPreservesWritableSourceBytes()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        const string original = "{\"SchemaVersion\":3,\"Items\":[]}";
        await File.WriteAllTextAsync(path, original);
        var store = new QueueStore(path, () => [], _ => throw new UnauthorizedAccessException("read denied"));
        var result = await store.LoadResultAsync();
        Assert.Equal(QueueLoadStatus.IoFailure, result.Status);
        Assert.IsType<UnauthorizedAccessException>(result.Error);
        Assert.False(result.CanSave);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([]));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.corrupt-*"));
    }

    [Fact]
    public async Task IoFailure_IsDistinctFromMissingAndNeverRewritesSource()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        Directory.CreateDirectory(path);
        var store = new QueueStore(path, () => []);
        var result = await store.LoadResultAsync();
        Assert.Equal(QueueLoadStatus.IoFailure, result.Status);
        Assert.False(result.CanSave);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([]));
        Assert.True(Directory.Exists(path));
    }

    [Theory]
    [InlineData(ConversionState.Waiting, ConversionState.Waiting)]
    [InlineData(ConversionState.Probing, ConversionState.Waiting)]
    [InlineData(ConversionState.Running, ConversionState.Waiting)]
    [InlineData(ConversionState.Paused, ConversionState.Paused)]
    [InlineData(ConversionState.Failed, ConversionState.Failed)]
    [InlineData(ConversionState.Cancelled, ConversionState.Cancelled)]
    [InlineData(ConversionState.Blocked, ConversionState.Blocked)]
    public async Task V3_RetainsRecoverableStatesErrorsAndAttemptTime(ConversionState saved, ConversionState expected)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var task = new MediaTask
        {
            InputPath = "missing.mp4", State = saved, LastError = "diagnostic", Message = "details",
            BlockedReason = "source offline", LastAttemptAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            StateBeforeBlocked = ConversionState.Paused
        };
        await new QueueStore(path).SaveAsync([task]);
        var item = Assert.Single((await new QueueStore(path).LoadResultAsync()).Items);
        Assert.Equal(expected, item.State);
        Assert.Equal(task.LastAttemptAtUtc, item.LastAttemptAtUtc);
        Assert.Equal(task.LastError, item.LastError);
        Assert.Equal(task.Message, item.Message);
        Assert.Equal(task.BlockedReason, item.BlockedReason);
        Assert.Equal(task.StateBeforeBlocked, item.StateBeforeBlocked);
        Assert.Contains("\"SchemaVersion\": 4", await File.ReadAllTextAsync(path));
    }
}
