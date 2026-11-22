using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ArchiveTaskStoreTests
{
    [Fact]
    public async Task PendingAndCompletedTasksRoundTripWithoutPasswordFields()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "archive-tasks.json");
        var source = Path.Combine(directory.Path, "source.7z");
        var output = Path.Combine(directory.Path, "output.zip");
        var pending = new ArchiveTaskCheckpoint
        {
            SourcePath = source, TargetFormat = ArchiveFormat.SevenZip,
            StrictVerification = true, EncryptOutput = true, State = ArchiveTaskState.Waiting,
            PreviousErrorKey = "ArchiveErrorInsufficientSpace"
        };
        var completed = new ArchiveTaskCheckpoint
        {
            SourcePath = Path.Combine(directory.Path, "second.zip"), TargetFormat = ArchiveFormat.Zip,
            State = ArchiveTaskState.Succeeded, OutputPath = output,
            CompletedAtUtc = DateTimeOffset.UtcNow, HistoryRecorded = true,
            ResultStatistics = new(300, 200, 42)
        };
        var snapshot = new ArchiveTaskSnapshot
        {
            OutputDirectory = directory.Path, DefaultTargetFormat = ArchiveFormat.SevenZip,
            DefaultStrictVerification = true, DefaultEncryptOutput = true, Tasks = [pending, completed]
        };
        await new ArchiveTaskStore(path).SaveAsync(snapshot);
        var restored = await new ArchiveTaskStore(path).LoadAsync();
        Assert.Equal(snapshot.OutputDirectory, restored.OutputDirectory);
        Assert.Equal(snapshot.DefaultTargetFormat, restored.DefaultTargetFormat);
        Assert.True(restored.DefaultEncryptOutput);
        Assert.Equal([pending, completed], restored.Tasks);
        Assert.DoesNotContain("password", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(ArchiveTaskState.Running)]
    [InlineData(ArchiveTaskState.AwaitingPassword)]
    public void InterruptedTaskRestoresWaitingAndStillRequiresOutputEncryption(ArchiveTaskState state)
    {
        var task = new ArchiveTaskCheckpoint
        {
            SourcePath = Path.GetFullPath("input.zip"), State = state,
            TargetFormat = ArchiveFormat.SevenZip, EncryptOutput = true,
            OutputPath = Path.GetFullPath("unfinished.7z"), ResultStatistics = new(1, 1, 1)
        };
        var restored = task.ForRestore();
        Assert.Equal(ArchiveTaskState.Waiting, restored.State);
        Assert.True(restored.EncryptOutput);
        Assert.Equal(task.Id, restored.Id);
        Assert.Empty(restored.OutputPath);
        Assert.Null(restored.ResultStatistics);
        Assert.False(restored.HistoryRecorded);
    }

    [Theory]
    [InlineData(ArchiveTaskState.Failed)]
    [InlineData(ArchiveTaskState.Cancelled)]
    [InlineData(ArchiveTaskState.Succeeded)]
    public void EndedTaskKeepsItsStatusDuringRestore(ArchiveTaskState state)
    {
        var task = new ArchiveTaskCheckpoint { State = state };
        Assert.Same(task, task.ForRestore());
    }

    [Fact]
    public async Task RemovingAllTasksPersistsAnEmptyList()
    {
        using var directory = new TemporaryDirectory();
        var store = new ArchiveTaskStore(Path.Combine(directory.Path, "tasks.json"));
        await store.SaveAsync(new() { Tasks = [new() { SourcePath = Path.Combine(directory.Path, "in.zip") }] });
        await store.SaveAsync(new());
        Assert.Empty((await store.LoadAsync()).Tasks);
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"Version\":99,\"Tasks\":[]}")]
    [InlineData("{\"Version\":1,\"Tasks\":[],\"Password\":\"must-not-be-accepted\"}")]
    public async Task UnreadableOrFutureSnapshotsCannotBeOverwritten(string original)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "tasks.json");
        await File.WriteAllTextAsync(path, original);
        var store = new ArchiveTaskStore(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new()));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DuplicateIdsAndUnsupportedEncryptionAreRejectedBeforeWriting()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "tasks.json");
        var store = new ArchiveTaskStore(path);
        var task = new ArchiveTaskCheckpoint { SourcePath = Path.Combine(directory.Path, "input.zip") };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new() { Tasks = [task, task] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new()
        { Tasks = [task, task with { Id = Guid.NewGuid() }] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new()
        { Tasks = [task with { TargetFormat = ArchiveFormat.Tar, EncryptOutput = true }] }));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task UnknownErrorKeysCannotEnterTheDurableSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var store = new ArchiveTaskStore(Path.Combine(directory.Path, "tasks.json"));
        var task = new ArchiveTaskCheckpoint { SourcePath = Path.Combine(directory.Path, "input.zip") };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new()
        { Tasks = [task with { ErrorKey = "invalid-error-key" }] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new()
        { Tasks = [task with { PreviousErrorKey = "invalid-error-key" }] }));
    }

    [Fact]
    public async Task CancelledSavePreservesPreviouslyCommittedTasks()
    {
        using var directory = new TemporaryDirectory();
        var store = new ArchiveTaskStore(Path.Combine(directory.Path, "tasks.json"));
        var task = new ArchiveTaskCheckpoint { SourcePath = Path.Combine(directory.Path, "input.zip") };
        await store.SaveAsync(new() { Tasks = [task] });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(), cancellation.Token));
        Assert.Equal(task, Assert.Single((await store.LoadAsync()).Tasks));
    }

    [Fact]
    public async Task UnusableOutputFolderProvidesSpecificFailureAndPreservesTheSource()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.zip");
        var blockedOutput = Path.Combine(directory.Path, "output-is-a-file");
        await File.WriteAllTextAsync(source, "unchanged source; process must never start");
        await File.WriteAllTextAsync(blockedOutput, "existing output location occupant");
        var fakeEngine = Path.Combine(directory.Path, "7z.exe");
        await File.WriteAllTextAsync(fakeEngine, "not executed");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "7z.dll"), "not loaded");

        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(fakeEngine)
            .ConvertAsync(source, blockedOutput, new(ArchiveFormat.Zip), (_, _) => Task.FromResult<string?>(null)));

        Assert.Equal(ArchiveFailureCode.OutputUnavailable, error.Code);
        Assert.Equal("unchanged source; process must never start", await File.ReadAllTextAsync(source));
        Assert.Equal("existing output location occupant", await File.ReadAllTextAsync(blockedOutput));
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".ffarchive-*"));
    }
}
