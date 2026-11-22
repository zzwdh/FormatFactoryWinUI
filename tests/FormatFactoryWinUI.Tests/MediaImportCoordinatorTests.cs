using FormatFactoryWinUI.Tests.TestDoubles;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaImportCoordinatorTests
{
    [Fact]
    public async Task ImportSourceObservation_ConcurrentRequestsKeepTokenFallbackWithTheirOwnTask()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = ObserveAsync(firstId, ImportTokenOutcome.Fallback);
        var second = ObserveAsync(secondId, ImportTokenOutcome.Restored);
        ready.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results[0].Snapshot.TokenFallbacks);
        Assert.Equal(0, results[1].Snapshot.TokenFallbacks);
        Assert.Contains(results[0].Lines, line => line.Contains($"request={results[0].Snapshot.RequestOperation:N} task={firstId:N}") &&
            line.Contains("outcome=Fallback") && line.Contains("native=2"));
        Assert.DoesNotContain(results[0].Lines, line => line.Contains(secondId.ToString("N")) || line.Contains("private-file"));

        async Task<(ImportSourceSnapshot Snapshot, List<string> Lines)> ObserveAsync(Guid task, ImportTokenOutcome outcome)
        {
            var lines = new List<string>();
            using var scope = ImportSourceDiagnostics.Begin(task, persisted: true, lines.Add);
            await ready.Task;
            ImportSourceDiagnostics.TokenResolution(StorageTokenDiagnosticKind.File, true, outcome,
                Guid.NewGuid(), StorageTokenDiagnosticStage.RestoreItem,
                outcome == ImportTokenOutcome.Fallback ? new FileNotFoundException("private-file") : null);
            return (scope.Complete(null, null), lines);
        }
    }

    [Fact]
    public async Task PersistedGroup_RestoresEveryOrderedInputTaskIdAndSettings()
    {
        using var directory = new TemporaryDirectory();
        var persisted = CreateGroup(directory.Path);
        var coordinator = new MediaImportCoordinator();

        var result = await coordinator.ImportPersistedAsync(string.Empty, persisted);

        Assert.Null(result.Error);
        var task = Assert.IsType<MediaTask>(result.Task);
        Assert.Equal(persisted.TaskId, task.Id);
        Assert.Equal(persisted.Inputs.Select(input => input.Path), task.InputPaths);
        Assert.Equal(persisted.Inputs.Select(input => input.AccessToken), task.Inputs.Select(input => input.AccessToken));
        Assert.Equal(7, task.FileSize);
        Assert.Equal("pdf", task.TargetFormatId);
        Assert.Equal("quality", task.ConversionMode);
        Assert.Equal("value", task.Options["test"]);
        Assert.Equal(3, task.ParametersVersion);
        coordinator.Forget(task);
        Assert.NotNull((await coordinator.ImportPersistedAsync(string.Empty, persisted)).Task);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledImport_ReleasesEarlierSuccessfulReservations(bool persistedGroup)
    {
        using var directory = new TemporaryDirectory();
        var persisted = CreateGroup(directory.Path);
        var storageAccess = new ImportStorageAccessFake();
        var coordinator = new MediaImportCoordinator(storageAccess: storageAccess);
        using var cancellation = new CancellationTokenSource();
        storageAccess.FileResolving = path =>
        {
            if (path == persisted.Inputs[1].Path)
            {
                cancellation.Cancel();
            }
        };
        try
        {
            if (persistedGroup)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    coordinator.ImportPersistedAsync(string.Empty, persisted, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    coordinator.ImportPathsAsync(string.Empty, persisted.Inputs.Select(input => input.Path),
                        cancellationToken: cancellation.Token));
            }
        }
        finally
        {
            storageAccess.FileResolving = null;
        }

        var retried = await coordinator.ImportPersistedAsync(string.Empty, persisted);
        Assert.Null(retried.Error);
        Assert.Equal(2, Assert.IsType<MediaTask>(retried.Task).Inputs.Count);
    }

    [Fact]
    public async Task FailedGroup_ReleasesEarlierSuccessfulReservations()
    {
        using var directory = new TemporaryDirectory();
        var persisted = CreateGroup(directory.Path);
        var coordinator = new MediaImportCoordinator();
        File.Delete(persisted.Inputs[1].Path);

        var failed = await coordinator.ImportPersistedAsync(string.Empty, persisted);
        Assert.Null(failed.Task);
        Assert.NotNull(failed.Error);
        var failedSource = Assert.IsType<ImportSourceSnapshot>(failed.SourceDiagnostic);
        Assert.Equal(persisted.TaskId, failedSource.TaskId);
        Assert.Equal(ImportSourceOutcome.Unavailable, failedSource.Outcome);
        Assert.False(failedSource.FileExists);
        Assert.Equal(2, failedSource.InputIndex);
        Assert.DoesNotContain(directory.Path, ImportSourceDiagnostics.Format(failedSource));

        File.WriteAllText(persisted.Inputs[1].Path, "last");
        var retried = await coordinator.ImportPersistedAsync(string.Empty, persisted);
        Assert.Null(retried.Error);
        Assert.Equal(2, Assert.IsType<MediaTask>(retried.Task).Inputs.Count);
        var restored = Assert.IsType<ImportSourceSnapshot>(retried.SourceDiagnostic);
        Assert.Equal(failedSource.TaskId, restored.TaskId);
        Assert.NotEqual(failedSource.RequestOperation, restored.RequestOperation);
        Assert.Equal(ImportSourceOutcome.Imported, restored.Outcome);
        Assert.True(restored.FileExists);
    }

    [Theory]
    [InlineData("docx", ContentKind.Document)]
    [InlineData("iso", ContentKind.Disc)]
    public async Task NonMediaImport_DoesNotRequireFfprobe(string extension, ContentKind expectedKind)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "input." + extension);
        File.WriteAllText(path, "content");

        var result = await new MediaImportCoordinator().ImportPathsAsync(string.Empty, [path]);

        Assert.Empty(result.Failures);
        Assert.Equal(expectedKind, Assert.Single(result.AddedTasks).ContentKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImageCollection_CancelledDuringLastResolutionReleasesAllReservations(bool restorePersisted)
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "first.png");
        var second = Path.Combine(directory.Path, "second.png");
        File.WriteAllText(first, "first image");
        File.WriteAllText(second, "second image");
        var persisted = new PersistedQueueItem
        {
            TaskId = Guid.NewGuid(),
            InputPath = first,
            SourceRoot = directory.Path,
            SourceRootAccessToken = "folder-token",
            ContentKind = ContentKind.Image,
            SourceFormatId = "png",
            TargetFormatId = "pdf",
            Inputs =
            [
                new PersistedConversionInput { Path = first, FormatId = "png", AccessToken = "first-token" },
                new PersistedConversionInput { Path = second, FormatId = "png", AccessToken = "second-token" }
            ]
        };
        var storageAccess = new ImportStorageAccessFake();
        var coordinator = new MediaImportCoordinator(storageAccess: storageAccess);
        using var cancellation = new CancellationTokenSource();
        storageAccess.FileResolving = path =>
        {
            if (path == second)
            {
                cancellation.Cancel();
            }
        };
        try
        {
            if (restorePersisted)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    coordinator.ImportPersistedAsync(string.Empty, persisted, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    coordinator.ImportImageCollectionAsync(string.Empty, [first, second],
                        directory.Path, "folder-token", cancellation.Token));
            }
        }
        finally
        {
            storageAccess.FileResolving = null;
        }

        var retried = await coordinator.ImportPersistedAsync(string.Empty, persisted);
        Assert.Null(retried.Error);
        Assert.Equal([first, second], Assert.IsType<MediaTask>(retried.Task).InputPaths);
    }

    [Fact]
    public async Task Batch_ProbesTwoAtATimeAndReturnsInputOrder()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateVideos(directory.Path, 3);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTwoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var started = 0;
        var excessiveConcurrency = false;
        var coordinator = new MediaImportCoordinator(async (_, path, token) =>
        {
            if (Interlocked.Increment(ref active) > 2) excessiveConcurrency = true;
            if (Interlocked.Increment(ref started) == 2) firstTwoStarted.TrySetResult();
            try
            {
                if (path == paths[0]) await first.Task.WaitAsync(token);
                else if (path == paths[1]) await second.Task.WaitAsync(token);
                else thirdStarted.TrySetResult();
                return new MediaProbeResult { Kind = MediaKind.Video };
            }
            finally { Interlocked.Decrement(ref active); }
        });

        var importing = coordinator.ImportPathsAsync("ffprobe", paths);
        try
        {
            await firstTwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(thirdStarted.Task.IsCompleted);
            second.TrySetResult();
            await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.Task.IsCompleted);
        }
        finally
        {
            first.TrySetResult();
            second.TrySetResult();
        }
        var result = await importing;

        Assert.Empty(result.Failures);
        Assert.Equal(paths, result.Items.Select(item => item.RequestedPath));
        Assert.Equal(paths, result.AddedTasks.Select(task => task.InputPath));
        Assert.False(excessiveConcurrency);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task Batch_DuplicateWaitsForFailedAttemptThenRetries()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateVideos(directory.Path, 1)[0];
        var attempts = 0;
        var coordinator = new MediaImportCoordinator(async (_, _, _) =>
        {
            await Task.Yield();
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidDataException("retryable");
            return new MediaProbeResult { Kind = MediaKind.Video };
        });

        var result = await coordinator.ImportPathsAsync("ffprobe", [path, path, path]);

        Assert.IsType<InvalidDataException>(result.Items[0].Error);
        Assert.NotNull(result.Items[1].Task);
        Assert.True(result.Items[2].IsIgnored);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Batch_CancellationDrainsProbesAndAllowsCompleteRetry()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateVideos(directory.Path, 3);
        using var cancellation = new CancellationTokenSource();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shouldBlock = true;
        var exited = false;
        var coordinator = new MediaImportCoordinator(async (_, path, token) =>
        {
            if (shouldBlock && path == paths[1])
            {
                blocked.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { exited = true; }
            }
            return new MediaProbeResult { Kind = MediaKind.Video };
        });

        var importing = coordinator.ImportPathsAsync("ffprobe", paths, cancellationToken: cancellation.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
        Assert.True(exited);
        shouldBlock = false;

        var retry = await coordinator.ImportPathsAsync("ffprobe", paths);

        Assert.Empty(retry.Failures);
        Assert.Equal(paths, retry.AddedTasks.Select(task => task.InputPath));
    }

    [Fact]
    public async Task Batch_EnumerationFailureCancelsPendingProbeAndReleasesReservation()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateVideos(directory.Path, 1)[0];
        var shouldBlock = true;
        var exited = false;
        var coordinator = new MediaImportCoordinator(async (_, _, token) =>
        {
            if (shouldBlock)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { exited = true; }
            }
            return new MediaProbeResult { Kind = MediaKind.Video };
        });
        IEnumerable<string> BrokenPaths()
        {
            yield return path;
            throw new InvalidOperationException("enumeration failed");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ImportPathsAsync("ffprobe", BrokenPaths()));
        Assert.True(exited);
        shouldBlock = false;

        Assert.Single((await coordinator.ImportPathsAsync("ffprobe", [path])).AddedTasks);
    }

    [Fact]
    public async Task PersistedBatch_GroupWaitsForEarlierProbeAndBlocksLaterImport()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateVideos(directory.Path, 2);
        var group = CreateGroup(directory.Path);
        var storageAccess = new ImportStorageAccessFake();
        var firstFinished = false;
        var groupResolved = false;
        var lastStarted = false;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new MediaImportCoordinator(async (_, path, token) =>
        {
            if (path == paths[0])
            {
                await first.Task.WaitAsync(token);
                firstFinished = true;
            }
            else lastStarted = true;
            return new MediaProbeResult { Kind = MediaKind.Video };
        }, storageAccess: storageAccess);
        storageAccess.FileResolving = path =>
        {
            if (group.Inputs.Any(input => input.Path == path))
            {
                Assert.True(firstFinished);
                Assert.False(lastStarted);
                groupResolved = true;
            }
        };
        try
        {
            var importing = coordinator.ImportPersistedBatchAsync("ffprobe",
            [
                new PersistedQueueItem { InputPath = paths[0], SourceFormatId = "mp4", ContentKind = ContentKind.Video },
                group,
                new PersistedQueueItem { InputPath = paths[1], SourceFormatId = "mp4", ContentKind = ContentKind.Video }
            ]);
            Assert.False(groupResolved);
            Assert.False(lastStarted);
            first.TrySetResult();
            var result = await importing;

            Assert.Empty(result.Failures);
            Assert.Equal([paths[0], group.InputPath, paths[1]], result.AddedTasks.Select(task => task.InputPath));
            Assert.Equal(group.TaskId, result.AddedTasks[1].Id);
            Assert.Equal(group.Inputs.Select(input => input.Path), result.AddedTasks[1].InputPaths);
            Assert.True(groupResolved);
            Assert.True(lastStarted);
        }
        finally
        {
            first.TrySetResult();
            storageAccess.FileResolving = null;
        }
    }

    private static string[] CreateVideos(string directory, int count)
    {
        var paths = Enumerable.Range(0, count).Select(index => Path.Combine(directory, $"video-{index}.mp4")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "probe fixture");
        return paths;
    }

    private static PersistedQueueItem CreateGroup(string directory)
    {
        var first = Path.Combine(directory, "first.docx");
        var second = Path.Combine(directory, "second.docx");
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "last");
        return new PersistedQueueItem
        {
            TaskId = Guid.NewGuid(),
            InputPath = first,
            ContentKind = ContentKind.Document,
            SourceFormatId = "docx",
            TargetFormatId = "pdf",
            ConversionMode = "quality",
            ParametersVersion = 3,
            Options = new Dictionary<string, string> { ["test"] = "value" },
            Inputs =
            [
                new PersistedConversionInput { Path = first, FormatId = "docx", AccessToken = "first-token" },
                new PersistedConversionInput { Path = second, FormatId = "docx", AccessToken = "second-token" }
            ]
        };
    }
}

