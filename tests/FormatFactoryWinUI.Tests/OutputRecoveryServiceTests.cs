using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class OutputRecoveryServiceTests
{
    [Fact]
    public async Task FailedValidatedCommit_SurvivesRestartAndSavesWithoutOverwriting()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "recovery");
        var task = CreateTask(directory.Path);
        var recovery = new OutputRecoveryService(root);
        string id;
        Guid originalCommit;
        using (var lease = Reserve(task, directory.Path))
        {
            File.WriteAllText(lease.TemporaryPath, "validated media bytes");
            File.WriteAllText(lease.FinalPath, "other user's file");
            var error = Assert.Throws<OutputCommitException>(() => lease.CommitValidated(task.Id, recovery,
                targetFormatId: "mp4", conversionMode: "lossless", engineId: "ffmpeg"));
            id = Assert.IsType<string>(error.RecoveryId);
            originalCommit = Assert.IsType<StorageFailureObservation>(StorageFailureDiagnostics.Get(error)).OperationId;
            Assert.Equal("validated media bytes", File.ReadAllText(error.PreservedPath));
            Assert.Equal("other user's file", File.ReadAllText(lease.FinalPath));
        }
        var restarted = new OutputRecoveryService(root);
        Assert.Equal(id, restarted.Get(task.Id)!.Id);
        Assert.Equal(originalCommit, restarted.Get(task.Id)!.OriginalCommitOperation);
        Assert.Equal("mp4", restarted.Get(task.Id)!.TargetFormatId);
        Assert.Equal("lossless", restarted.Get(task.Id)!.ConversionMode);
        Assert.Equal("ffmpeg", restarted.Get(task.Id)!.EngineId);
        var saved = await restarted.SaveAsAsync(id, directory.Path);
        Assert.Equal(Path.Combine(directory.Path, "sample (1).mp4"), saved);
        Assert.Equal("validated media bytes", File.ReadAllText(saved));
        Assert.Null(restarted.GetById(id));
        Assert.Equal("other user's file", File.ReadAllText(Path.Combine(directory.Path, "sample.mp4")));
    }

    [Fact]
    public async Task DirectoryRecovery_PreservesNestedFilesAndNeverOverwritesDirectory()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var task = CreateTask(directory.Path);
        string id;
        using (var lease = new OutputPathService().Reserve(task, Settings(directory.Path), " frames", true))
        {
            Directory.CreateDirectory(Path.Combine(lease.TemporaryPath, "nested", "empty"));
            File.WriteAllText(Path.Combine(lease.TemporaryPath, "nested", "frame-0001.png"), "validated frame");
            Directory.CreateDirectory(lease.FinalPath);
            File.WriteAllText(Path.Combine(lease.FinalPath, "existing.txt"), "original directory");
            id = Assert.Throws<OutputCommitException>(() => lease.CommitValidated(task.Id, recovery)).RecoveryId!;
        }
        var output = await recovery.SaveAsAsync(id, directory.Path);
        Assert.Equal(Path.Combine(directory.Path, "sample frames (1)"), output);
        Assert.Equal("validated frame", File.ReadAllText(Path.Combine(output, "nested", "frame-0001.png")));
        Assert.True(Directory.Exists(Path.Combine(output, "nested", "empty")));
        Assert.Equal("original directory", File.ReadAllText(Path.Combine(directory.Path, "sample frames", "existing.txt")));
    }

    [Fact]
    public async Task CancelledSaveAndFailedDestination_KeepRetainedArtifact()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var record = Preserve(recovery, directory.Path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.SaveAsAsync(record.Id, Path.Combine(directory.Path, "cancelled"), cancelled.Token));
        Assert.NotNull(recovery.GetById(record.Id));
        var blockedDirectory = Path.Combine(directory.Path, "blocked");
        File.WriteAllText(blockedDirectory, "a file occupies the directory name");
        await Assert.ThrowsAsync<IOException>(() => recovery.SaveAsAsync(record.Id, blockedDirectory));
        Assert.Equal("validated", File.ReadAllText(record.ArtifactPath));
        Assert.NotNull(recovery.GetById(record.Id));
    }

    [Fact]
    public async Task ChangedArtifact_IsRejectedAndRemainsRetained()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var record = Preserve(recovery, directory.Path);
        File.WriteAllText(record.ArtifactPath, "changed");
        var outputDirectory = Path.Combine(directory.Path, "destination");
        await Assert.ThrowsAsync<InvalidDataException>(() => recovery.SaveAsAsync(record.Id, outputDirectory));
        Assert.NotNull(recovery.GetById(record.Id));
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public void FailedIndexStorage_RetainsPrivateOriginalInsteadOfDeletingIt()
    {
        using var directory = new TemporaryDirectory();
        var blockedRoot = Path.Combine(directory.Path, "recovery-file");
        File.WriteAllText(blockedRoot, "blocked");
        var recovery = new OutputRecoveryService(blockedRoot);
        var task = CreateTask(directory.Path);
        string original;
        using (var lease = Reserve(task, directory.Path))
        {
            original = lease.TemporaryPath;
            File.WriteAllText(original, "validated");
            File.WriteAllText(lease.FinalPath, "existing");
            var error = Assert.Throws<OutputCommitException>(() => lease.CommitValidated(task.Id, recovery));
            Assert.Null(error.RecoveryId);
            Assert.Equal(original, error.PreservedPath);
            Assert.NotNull(error.PreservationError);
        }
        Assert.Equal("validated", File.ReadAllText(original));
    }

    [Fact]
    public void UnvalidatedOrCancelledOutput_IsNotIndexedAndIsCleanedUp()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var task = CreateTask(directory.Path);
        string temporary;
        using (var lease = Reserve(task, directory.Path))
        {
            temporary = lease.TemporaryPath;
            File.WriteAllText(temporary, "invalid partial output");
            File.WriteAllText(lease.FinalPath, "collision");
            Assert.Throws<IOException>(() => lease.Commit());
        }
        Assert.False(File.Exists(temporary));
        using (var lease = Reserve(task, directory.Path))
        {
            temporary = lease.TemporaryPath;
            File.WriteAllText(temporary, "cancelled output");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => lease.CommitValidated(task.Id, recovery, cancellation.Token));
        }
        Assert.False(File.Exists(temporary));
        Assert.Empty(recovery.Load());
    }

    [Fact]
    public void RecoveryRecords_AreBoundedAndExpire()
    {
        using var directory = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"), () => now);
        OutputRecoveryRecord? first = null;
        for (var index = 0; index <= OutputRecoveryService.MaximumItems; index++)
        {
            now += TimeSpan.FromSeconds(1);
            var record = Preserve(recovery, directory.Path);
            first ??= record;
        }
        Assert.Equal(OutputRecoveryService.MaximumItems, recovery.Load().Count);
        Assert.Null(recovery.GetById(first!.Id));
        Assert.False(File.Exists(first.ArtifactPath));
        now += OutputRecoveryService.Retention + TimeSpan.FromSeconds(1);
        Assert.Empty(recovery.Load());
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public async Task CachedRecoveryLookups_DoNotReadMetadataPerQueueItem(int lookupCount)
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "recovery");
        var writer = new OutputRecoveryService(root);
        var records = Enumerable.Range(0, OutputRecoveryService.MaximumItems)
            .Select(_ => Preserve(writer, directory.Path)).ToArray();
        var reader = new OutputRecoveryService(root);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => reader.LoadIndexAsync()));
        var readsAfterInitialization = reader.MetadataReadCount;
        Assert.Equal(OutputRecoveryService.MaximumItems, readsAfterInitialization);
        for (var index = 0; index < lookupCount; index++)
        {
            var expected = records[index % records.Length];
            Assert.Equal(expected.Id, reader.GetCached(expected.TaskId)?.Id);
            Assert.Equal(expected.TaskId, reader.GetByIdCached(expected.Id)?.TaskId);
            Assert.Null(reader.GetCached(Guid.Empty));
        }
        await reader.LoadIndexAsync();
        Assert.Equal(readsAfterInitialization, reader.MetadataReadCount);
    }

    [Fact]
    public async Task IndexInitialization_FiltersExpiredRecordsWithoutDeletingArtifacts()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "recovery");
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var writer = new OutputRecoveryService(root, () => now);
        var record = Preserve(writer, directory.Path);
        now += OutputRecoveryService.Retention + TimeSpan.FromSeconds(1);
        var reader = new OutputRecoveryService(root, () => now);

        Assert.Empty(await reader.LoadIndexAsync());
        Assert.Null(reader.GetCached(record.TaskId));
        Assert.True(File.Exists(record.ArtifactPath));
        await reader.PruneAsync();
        Assert.False(File.Exists(record.ArtifactPath));
    }

    [Fact]
    public async Task PublishedIndex_TracksPreservationAndSuccessfulSave()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        Assert.Empty(await recovery.LoadIndexAsync());
        var record = Preserve(recovery, directory.Path);
        Assert.Equal(record.Id, recovery.GetCached(record.TaskId)?.Id);
        Assert.Equal(record.TaskId, recovery.GetByIdCached(record.Id)?.TaskId);

        await recovery.SaveAsAsync(record.Id, Path.Combine(directory.Path, "saved"));
        Assert.Null(recovery.GetCached(record.TaskId));
        Assert.Null(recovery.GetByIdCached(record.Id));
    }

    [Fact]
    public async Task Save_RevalidatesMetadataInsteadOfTrustingCachedRecord()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var record = Preserve(recovery, directory.Path);
        Assert.NotNull(recovery.GetByIdCached(record.Id));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(record.ArtifactPath)!, "entry.json"), "{broken json");

        await Assert.ThrowsAsync<FileNotFoundException>(() => recovery.SaveAsAsync(record.Id, Path.Combine(directory.Path, "saved")));
        Assert.Null(recovery.GetByIdCached(record.Id));
        Assert.Equal("validated", File.ReadAllText(record.ArtifactPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveSave_ProtectsArtifactFromCleanupWithoutBlockingOtherMetadata(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "recovery");
        var ticks = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero).Ticks;
        DateTimeOffset Clock() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        var copyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCopy = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var recovery = new OutputRecoveryService(root, Clock, () =>
        {
            copyStarted.TrySetResult();
            if (!releaseCopy.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Recovery copy was not released.");
        });
        var record = Preserve(recovery, directory.Path);
        var save = recovery.SaveAsAsync(record.Id, Path.Combine(directory.Path, "saved"), cancellation.Token);
        try
        {
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Interlocked.Add(ref ticks, (OutputRecoveryService.Retention + TimeSpan.FromSeconds(1)).Ticks);
            var otherInstance = new OutputRecoveryService(root, Clock);
            await Task.WhenAll(otherInstance.PruneAsync(), otherInstance.LoadIndexAsync()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(record.ArtifactPath));
            Assert.False(save.IsCompleted);
            if (cancel) cancellation.Cancel();
        }
        finally { releaseCopy.Set(); }

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
            Assert.Equal("validated", File.ReadAllText(record.ArtifactPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(directory.Path, "saved")));
        }
        else
        {
            var output = await save;
            Assert.Equal("validated", File.ReadAllText(output));
            Assert.False(File.Exists(record.ArtifactPath));
        }
    }

    [Fact]
    public async Task ConcurrentSavesAcrossInstances_CommitRecoveryOnlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "recovery");
        var recovery = new OutputRecoveryService(root);
        var record = Preserve(recovery, directory.Path);
        var destination = Path.Combine(directory.Path, "saved");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { return await new OutputRecoveryService(root).SaveAsAsync(record.Id, destination); }
            catch (FileNotFoundException) { return null; }
        }));
        var output = Assert.Single(outcomes, path => path is not null);
        Assert.Equal("validated", File.ReadAllText(output!));
        Assert.Single(Directory.EnumerateFiles(destination));
        Assert.Empty(recovery.Load());
    }

    private static OutputRecoveryRecord Preserve(OutputRecoveryService recovery, string directory)
    {
        var source = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial.mp4");
        File.WriteAllText(source, "validated");
        return recovery.PreserveValidated(Guid.NewGuid(), source, Path.Combine(directory, "sample.mp4"), false);
    }
    private static MediaTask CreateTask(string directory) => new() { InputPath = Path.Combine(directory, "sample.mov"), TargetFormat = "MP4" };
    private static AppSettings Settings(string directory) => new() { OutputMode = "custom", CustomOutputDirectory = directory };
    private static OutputPathLease Reserve(MediaTask task, string directory) => new OutputPathService().Reserve(task, Settings(directory));
}
