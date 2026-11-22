using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueSessionReliabilityTests
{
    [Fact]
    public async Task UnavailableSources_RetainIdentityOrderParametersAndRecoverWithoutAutomaticRetry()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var tasks = new[] { ConversionState.Failed, ConversionState.Cancelled, ConversionState.Paused, ConversionState.Running }
            .Select((state, index) => TaskModel(directory.Path, index, state)).ToArray();
        foreach (var task in tasks)
        {
            task.Inputs.Add(new ConversionInput(task.InputPath, "docx", AccessToken: "file-token"));
            task.Options["layout"] = "fixed";
            task.LastError = "previous diagnostic";
            task.Message = "previous message";
            task.LastAttemptAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        }
        await new QueueStore(path).SaveAsync(tasks);
        using (var unavailable = Session(new QueueStore(path), (_, _) =>
            Task.FromResult(new MediaImportItemResult("offline", null, null))))
        {
            await unavailable.RestoreAsync();
            Assert.Equal(tasks.Select(task => task.Id), unavailable.Tasks.Select(task => task.Id));
            Assert.All(unavailable.Tasks, task => Assert.Equal(ConversionState.Blocked, task.State));
            Assert.Equal([ConversionState.Failed, ConversionState.Cancelled, ConversionState.Paused, ConversionState.Waiting],
                unavailable.Tasks.Select(task => task.StateBeforeBlocked!.Value));
            var persisted = await new QueueStore(path).LoadAsync();
            Assert.Equal(tasks.Select(task => task.Id), persisted.Select(task => task.TaskId));
            Assert.All(persisted, item =>
            {
                Assert.Equal("fixed", item.Options["layout"]);
                Assert.Equal("quality", item.ConversionMode);
                Assert.Equal(2, item.SelectedAudioTrack);
                Assert.Equal("file-token", Assert.Single(item.Inputs).AccessToken);
            });
        }

        var executions = 0;
        using var connected = Session(new QueueStore(path), AvailableImport, (_, _, _) =>
        {
            executions++;
            return Task.FromResult(Success("output"));
        });
        await connected.RestoreAsync();
        Assert.Equal(0, executions);
        Assert.Equal([ConversionState.Failed, ConversionState.Cancelled, ConversionState.Paused, ConversionState.Waiting],
            connected.Tasks.Select(task => task.State));
        Assert.Equal(tasks.Select(task => task.Id), connected.Tasks.Select(task => task.Id));
        Assert.All(connected.Tasks, task => Assert.Equal("fixed", task.Options["layout"]));
        Assert.Equal("previous diagnostic", connected.Tasks[0].LastError);
        Assert.Equal("previous diagnostic", connected.Tasks[0].Message);
    }

    [Fact]
    public async Task ProbeException_LeavesBlockedRecordAndExplicitRetryUsesOriginalTask()
    {
        var item = Item("movie.mp4");
        item.TargetFormatId = "mp3";
        item.Options["custom"] = "retained";
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Success, [item]));
        var probeCalls = 0;
        var executed = new TaskCompletionSource<MediaTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = Session(persistence, (persisted, _) =>
        {
            if (++probeCalls == 1) throw new IOException("probe unavailable");
            return AvailableImport(persisted, CancellationToken.None);
        }, (task, _, _) =>
        {
            executed.TrySetResult(task);
            return Task.FromResult(Success("output.mp3"));
        });
        await session.RestoreAsync();
        var task = Assert.Single(session.Tasks);
        Assert.Equal(ConversionState.Blocked, task.State);
        Assert.Contains("probe unavailable", task.BlockedReason);
        Assert.True(session.TryStart(task));
        Assert.Same(task, await executed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(item.TaskId, task.Id);
        Assert.Equal("retained", task.Options["custom"]);
        Assert.Equal(ConversionState.Completed, task.State);
    }

    [Fact]
    public async Task SuccessfulConversion_SurvivesSimultaneousPersistenceHistoryDiagnosticsAndNotificationFailures()
    {
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Missing, [])) { SaveError = new IOException("disk full") };
        var historyAttempts = 0;
        var observedFailures = 0;
        ConversionQueueOperationCompleted? completion = null;
        using var session = new QueueSession(persistence, AvailableImport,
            (_, _, _) => Task.FromResult(Success("committed.mp4")),
            (_, _) => { historyAttempts++; throw new IOException("history read only"); },
            diagnostics: _ => throw new IOException("log also read only"));
        session.SideEffectFailed += _ => { observedFailures++; throw new InvalidOperationException("warning presenter failed"); };
        var task = TaskModel("input", 0, ConversionState.Waiting);
        Assert.True(session.TryStart(task, completed: value =>
        {
            completion = value;
            throw new InvalidOperationException("notification failed");
        }, batchCompleted: () => throw new IOException("jump list failed")));
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConversionState.Completed, task.State);
        Assert.Equal("committed.mp4", task.OutputPath);
        Assert.Equal(100, task.Progress);
        Assert.Empty(task.LastError);
        Assert.Equal(1, historyAttempts);
        Assert.True(observedFailures >= 4);
        Assert.NotNull(completion);
        Assert.Equal(ConversionOutcome.Completed, completion.Outcome);
        Assert.Null(completion.Error);
        Assert.Equal(0, session.Coordinator.ActiveOperationCount);
    }

    [Fact]
    public async Task SlowHistory_DoesNotHoldConversionAdmissionButShutdownWaitsForIt()
    {
        var firstHistoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = TaskModel("input", 0, ConversionState.Waiting);
        var second = TaskModel("input", 1, ConversionState.Waiting);
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Missing, []));
        using var session = new QueueSession(persistence, AvailableImport, (task, _, _) =>
        {
            if (task.Id == second.Id) secondStarted.TrySetResult();
            return Task.FromResult(Success(task.InputPath + ".out"));
        }, async (item, _) =>
        {
            if (item.TaskId != first.Id) return;
            firstHistoryStarted.TrySetResult();
            await releaseHistory.Task;
        });
        session.Coordinator.ConfigureParallelism(1);
        Assert.True(session.TryStart(first));
        await firstHistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.TryStart(second));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = session.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        releaseHistory.TrySetResult();
        Assert.True((await shutdown.WaitAsync(TimeSpan.FromSeconds(5))).CanClose);
        Assert.Equal(ConversionState.Completed, first.State);
    }

    [Fact]
    public async Task ShutdownBeforeLoadPublication_KeepOpenReloadsBeforeSavingInsteadOfReusingFaultedBarrier()
    {
        var item = Item("durable.mp4");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Success, [item]))
        {
            Load = async token =>
            {
                if (Interlocked.Increment(ref loads) == 1)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return new(QueueLoadStatus.Success, [item]);
            }
        };
        using var session = Session(persistence, AvailableImport);
        var originalRestore = session.RestoreAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failed = await session.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => originalRestore);
        Assert.False(failed.CanClose);
        Assert.Equal(0, persistence.SaveAttempts);
        session.ContinueAfterFailedShutdown();
        await session.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.SaveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, loads);
        Assert.Equal(item.TaskId, Assert.Single(session.Tasks).Id);
        Assert.Equal(item.TaskId, Assert.Single(persistence.LastSaved).TaskId);
        Assert.True((await session.ShutdownAsync()).CanClose);
    }

    [Fact]
    public async Task ShutdownAfterLoadBeforePublication_AndConcurrentSavePreserveEveryDurableId()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var initial = Enumerable.Range(0, 3).Select(index => TaskModel(directory.Path, index, ConversionState.Waiting)).ToArray();
        await new QueueStore(path).SaveAsync(initial);
        var dispatchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        async Task Dispatch(Func<Task> action)
        {
            if (Interlocked.Increment(ref dispatchCount) == 1)
            {
                dispatchEntered.TrySetResult();
                await releasePublication.Task;
            }
            await action();
        }
        using var session = new QueueSession(new QueueStore(path), AvailableImport,
            (_, _, _) => Task.FromResult(Success("unused")), (_, _) => Task.CompletedTask,
            Dispatch, diagnostics: _ => { });
        var restore = session.RestoreAsync();
        await dispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var save = session.SaveAsync();
        Assert.False(save.IsCompleted);
        var shutdown = session.ShutdownAsync();
        Assert.False(session.AddTask(TaskModel(directory.Path, 10, ConversionState.Waiting)));
        releasePublication.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await shutdown.WaitAsync(TimeSpan.FromSeconds(5))).CanClose);
        Assert.Equal(initial.Select(task => task.Id), session.Tasks.Select(task => task.Id));
        Assert.Equal(initial.Select(task => task.Id), (await new QueueStore(path).LoadResultAsync()).Items.Select(item => item.TaskId));
    }

    [Fact]
    public async Task ShutdownWithSlowRestore_StopsRunningWorkImmediatelyAndPersistsWaiting()
    {
        var restored = Item("restored.mp4");
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Success, [restored]));
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = Session(persistence, async (item, token) =>
        {
            probeEntered.TrySetResult();
            await releaseProbe.Task;
            token.ThrowIfCancellationRequested();
            return await AvailableImport(item, token);
        }, async (_, _, token) =>
        {
            executionEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { executionCancelled.TrySetResult(); throw; }
            return Success("unused");
        });
        var restore = session.RestoreAsync();
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = TaskModel("input", 9, ConversionState.Waiting);
        Assert.True(session.TryStart(running));
        await executionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = session.ShutdownAsync();
        await executionCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(shutdown.IsCompleted);
        releaseProbe.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.True((await shutdown.WaitAsync(TimeSpan.FromSeconds(5))).CanClose);
        Assert.Equal(ConversionState.Waiting, running.State);
        Assert.Contains(persistence.LastSaved, item => item.TaskId == running.Id && item.State == ConversionState.Waiting);
        Assert.Contains(persistence.LastSaved, item => item.TaskId == restored.TaskId);
    }

    [Fact]
    public async Task TailRetryCompletesWhileEarlierRestoreProbes_AndRestoreCannotOverwriteSuccess()
    {
        var first = Item("first.mp4");
        var tail = Item("tail.mp4");
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Success, [first, tail]));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tailProbes = 0;
        using var session = Session(persistence, async (item, token) =>
        {
            if (item.TaskId == first.TaskId)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                return await AvailableImport(item, token);
            }
            return ++tailProbes == 1 ? await AvailableImport(item, token) : new(item.InputPath, null, null);
        });
        var restore = session.RestoreAsync();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var task = session.Tasks[1];
        Assert.True(session.TryStart(task));
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConversionState.Completed, task.State);
        releaseFirst.TrySetResult();
        await restore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, tailProbes);
        Assert.Equal(ConversionState.Completed, task.State);
        Assert.Equal("output", task.OutputPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDuringRestore_IgnoresLateProbeAndReleasesItsReservation(bool remove)
    {
        var item = Item("restored.mp4");
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Success, [item]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var available = QueueSession.FromPersisted(item);
        var released = new List<MediaTask>();
        using var session = new QueueSession(persistence, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new(item.InputPath, available, null);
        }, (_, _, _) => Task.FromResult(Success("unused")), (_, _) => Task.CompletedTask,
            diagnostics: _ => { }, releaseImport: released.Add);
        var restore = session.RestoreAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var task = Assert.Single(session.Tasks);
        session.Cancel(task);
        if (remove) session.RemoveTask(task);
        release.TrySetResult();
        await restore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConversionState.Cancelled, task.State);
        Assert.Same(available, Assert.Single(released));
        if (remove) Assert.Empty(persistence.LastSaved);
        else Assert.Equal(ConversionState.Cancelled, Assert.Single(persistence.LastSaved).State);
    }

    [Fact]
    public async Task ExplicitRetryOfCurrentlyProbingTask_SerializesProbesAndDiscardsOldResult()
    {
        var item = Item("restored.mp4");
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Success, [item]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var oldProbe = QueueSession.FromPersisted(item);
        var released = new List<MediaTask>();
        using var session = new QueueSession(persistence, async (persisted, token) =>
        {
            if (Interlocked.Increment(ref probes) == 1)
            {
                entered.TrySetResult();
                await release.Task;
                return new(item.InputPath, oldProbe, null);
            }
            return await AvailableImport(persisted, token);
        }, (_, _, _) => Task.FromResult(Success("result.mp4")), (_, _) => Task.CompletedTask,
            diagnostics: _ => { }, releaseImport: released.Add);
        var restore = session.RestoreAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var task = Assert.Single(session.Tasks);
        Assert.True(session.TryStart(task));
        Assert.Equal(1, Volatile.Read(ref probes));
        release.TrySetResult();
        await restore.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, probes);
        Assert.Contains(oldProbe, released);
        Assert.Equal(ConversionState.Completed, task.State);
        Assert.Equal("result.mp4", task.OutputPath);
    }

    [Fact]
    public async Task ShutdownDuringRestore_PreservesUnprobedTailAndRejectsNewWork()
    {
        var items = Enumerable.Range(0, 3).Select(index => Item($"input-{index}.mp4")).ToArray();
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Success, items));
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = Session(persistence, async (item, token) =>
        {
            probeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(item.InputPath, null, null);
        });
        var restore = session.RestoreAsync();
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = await session.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.True(shutdown.CanClose);
        Assert.Equal(items.Select(item => item.TaskId), persistence.LastSaved.Select(item => item.TaskId));
        Assert.All(persistence.LastSaved, item => Assert.Equal(ConversionState.Blocked, item.State));
        Assert.False(session.TryStart(session.Tasks[0]));
    }

    [Fact]
    public async Task InterruptedRestore_KeepOpenResumesMetadataWithoutAutomaticExecution()
    {
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Success, [Item("first.mp4"), Item("tail.mp4")]))
        { SaveError = new IOException("temporarily read only") };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imports = 0;
        var executions = 0;
        using var session = Session(persistence, async (item, token) =>
        {
            if (++imports == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return await AvailableImport(item, token);
        }, (_, _, _) => { executions++; return Task.FromResult(Success("tail.out")); });
        var restore = session.RestoreAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await session.ShutdownAsync()).CanClose);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        session.ContinueAfterFailedShutdown();
        await session.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, executions);
        var tail = session.Tasks[1];
        Assert.Equal(ConversionState.Waiting, tail.State);
        Assert.True(session.TryStart(tail));
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, executions);
        Assert.Equal(ConversionState.Completed, tail.State);
        Assert.Equal(ConversionState.Waiting, session.Tasks[0].State);
    }

    [Fact]
    public async Task PublishedRestore_KeepOpenReusesRowsAndOnlyProbesUnfinishedItems()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "committed.mp4");
        await File.WriteAllTextAsync(output, "committed");
        var completed = Item("completed.mp4");
        completed.OutputGroupId = Guid.NewGuid();
        completed.State = ConversionState.Completed;
        completed.OutputPath = output;
        completed.OutputHistoryRecorded = true;
        var items = new[] { completed, Item("paused.mp4"), Item("failed.mp4"), Item("cancelled.mp4"), Item("waiting.mp4") };
        items[1].State = ConversionState.Paused;
        items[2].State = ConversionState.Failed;
        items[2].LastError = "prior failure";
        items[3].State = ConversionState.Cancelled;
        items[4].Options["custom"] = "retained";
        var loads = 0;
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Success, items))
        {
            SaveError = new IOException("read only"),
            Load = _ =>
            {
                loads++;
                return Task.FromResult(new QueueLoadResult(QueueLoadStatus.Success, items));
            }
        };
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = new Dictionary<Guid, int>();
        var executions = 0;
        var historyWrites = 0;
        using var session = new QueueSession(persistence, async (item, token) =>
        {
            probes[item.TaskId] = probes.GetValueOrDefault(item.TaskId) + 1;
            if (item.TaskId == items[2].TaskId)
            {
                if (probes[item.TaskId] == 1)
                {
                    interrupted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                else
                {
                    resumed.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            }
            return await AvailableImport(item, token);
        }, (_, _, _) => { executions++; return Task.FromResult(Success("unused")); },
            (_, _) => { historyWrites++; return Task.CompletedTask; }, diagnostics: _ => { });
        var restore = session.RestoreAsync();
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var originalRows = session.Tasks.ToArray();
        Assert.False((await session.ShutdownAsync()).CanClose);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);

        session.ContinueAfterFailedShutdown();
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var continuation = session.RestoreAsync();
        Assert.NotSame(restore, continuation);
        Assert.False(continuation.IsCompleted);
        release.TrySetResult();
        await continuation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, loads);
        Assert.Equal(items.Select(item => item.TaskId), session.Tasks.Select(task => task.Id));
        for (var index = 0; index < originalRows.Length; index++) Assert.Same(originalRows[index], session.Tasks[index]);
        Assert.Equal([ConversionState.Completed, ConversionState.Paused, ConversionState.Failed,
            ConversionState.Cancelled, ConversionState.Waiting], session.Tasks.Select(task => task.State));
        Assert.Equal("prior failure", session.Tasks[2].LastError);
        Assert.Equal("prior failure", session.Tasks[2].Message);
        Assert.Equal("retained", session.Tasks[4].Options["custom"]);
        Assert.False(probes.ContainsKey(completed.TaskId));
        Assert.Equal([1, 2, 1, 1], items.Skip(1).Select(item => probes[item.TaskId]));
        Assert.Equal(0, executions);
        Assert.Equal(0, historyWrites);
        Assert.Same(continuation, session.RestoreAsync());
    }

    [Fact]
    public async Task ClosingSession_PreservesPausedAndResetsQueuedToWaiting()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Missing, []));
        using var session = Session(persistence, AvailableImport, async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success("unused");
        });
        var paused = TaskModel("input", 0, ConversionState.Waiting);
        var queued = TaskModel("input", 1, ConversionState.Waiting);
        Assert.True(session.TryStart(paused));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Pause(paused, () => true));
        Assert.True(session.TryStart(queued));
        Assert.True((await session.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5))).CanClose);
        Assert.Equal(ConversionState.Paused, paused.State);
        Assert.Equal(ConversionState.Waiting, queued.State);
        Assert.Equal([ConversionState.Paused, ConversionState.Waiting], persistence.LastSaved.Select(item => item.State));
    }

    [Fact]
    public async Task CancellationBeforeRunningDispatch_PublishesCancelledWithoutCallingEngine()
    {
        var releaseDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        var executions = 0;
        async Task Dispatch(Func<Task> action)
        {
            if (++dispatchCount == 1) await releaseDispatch.Task;
            await action();
        }
        using var session = new QueueSession(new MemoryPersistence(new(QueueLoadStatus.Missing, [])),
            AvailableImport, (_, _, _) => { executions++; return Task.FromResult(Success("unused")); },
            (_, _) => Task.CompletedTask, Dispatch, diagnostics: _ => { });
        var task = TaskModel("input", 0, ConversionState.Waiting);
        Assert.True(session.TryStart(task));
        session.Cancel(task);
        releaseDispatch.TrySetResult();
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, executions);
        Assert.Equal(ConversionState.Cancelled, task.State);
    }

    [Fact]
    public async Task FutureVersionSession_NeverWritesEvenWhenShuttingDown()
    {
        var persistence = new MemoryPersistence(new(QueueLoadStatus.FutureVersion, []));
        using var session = Session(persistence, AvailableImport);
        await session.RestoreAsync();
        Assert.False(session.CanSave);
        Assert.False((await session.ShutdownAsync()).CanClose);
        Assert.True((await session.ShutdownAsync(save: false)).CanClose);
        Assert.Equal(0, persistence.SaveAttempts);
    }

    [Fact]
    public async Task ExitSaveFailure_AllowsNoSaveExitOrContinuingSession()
    {
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Missing, [])) { SaveError = new IOException("read only") };
        using var session = Session(persistence, AvailableImport);
        session.AddTask(TaskModel("input", 0, ConversionState.Waiting));
        var failed = await session.ShutdownAsync();
        Assert.False(failed.CanClose);
        Assert.IsType<IOException>(failed.SaveError);
        session.ContinueAfterFailedShutdown();
        Assert.True(session.TryStart(session.Tasks[0]));
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var attempted = persistence.SaveAttempts;
        Assert.True((await session.ShutdownAsync(save: false)).CanClose);
        Assert.Equal(attempted, persistence.SaveAttempts);
    }

    [Fact]
    public async Task PauseAndResume_DuringCancellationDrainRestartsOnceAndCancelRetainsTask()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var persistence = new MemoryPersistence(new(QueueLoadStatus.Missing, []));
        using var session = Session(persistence, AvailableImport, async (_, _, token) =>
        {
            if (++starts != 1) return Success("completed.pdf");
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); await release.Task; throw; }
            return Success("unused");
        });
        var task = TaskModel("input", 0, ConversionState.Waiting);
        Assert.True(session.TryStart(task));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Pause(task));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConversionState.Paused, task.State);
        Assert.True(session.Resume(task));
        release.TrySetResult();
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, starts);
        Assert.Equal(ConversionState.Completed, task.State);
        var pending = TaskModel("input", 1, ConversionState.Waiting);
        session.AddTask(pending);
        session.Cancel(pending);
        await session.SaveAsync();
        Assert.Contains(persistence.LastSaved, item => item.TaskId == pending.Id && item.State == ConversionState.Cancelled);
    }

    private static QueueSession Session(IQueuePersistence persistence,
        Func<PersistedQueueItem, CancellationToken, Task<MediaImportItemResult>> import,
        Func<MediaTask, IProgress<ConversionExecutionProgress>?, CancellationToken, Task<ConversionWorkflowResult>>? execute = null) =>
        new(persistence, import, execute ?? ((_, _, _) => Task.FromResult(Success("output"))),
            (_, _) => Task.CompletedTask, diagnostics: _ => { });

    private static Task<MediaImportItemResult> AvailableImport(PersistedQueueItem item, CancellationToken _) =>
        Task.FromResult(new MediaImportItemResult(item.InputPath, QueueSession.FromPersisted(item), null));

    private static ConversionWorkflowResult Success(string output) =>
        new(ConversionOutcome.Completed, new(true, false, [new ConversionArtifact("result", "mp4", output)], "completed"), "test-engine");

    private static PersistedQueueItem Item(string path) => new()
    {
        TaskId = Guid.NewGuid(), InputPath = path, SourceFormatId = "mp4", TargetFormat = "MP4",
        TargetFormatId = "mp4", ContentKind = ContentKind.Video,
        Inputs = [new() { Path = path, FormatId = "mp4" }]
    };

    private static MediaTask TaskModel(string directory, int index, ConversionState state) => new()
    {
        InputPath = Path.Combine(directory, $"input-{index}.docx"), ContentKind = ContentKind.Document,
        SourceFormatId = "docx", TargetFormatId = "pdf", ConversionMode = "quality", State = state,
        SelectedAudioTrack = 2
    };

    private sealed class MemoryPersistence(QueueLoadResult result) : IQueuePersistence
    {
        public Exception? SaveError { get; init; }
        public Func<CancellationToken, Task<QueueLoadResult>>? Load { get; init; }
        public int SaveAttempts { get; private set; }
        public PersistedQueueItem[] LastSaved { get; private set; } = [];
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default) =>
            Load?.Invoke(cancellationToken) ?? Task.FromResult(result);
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (SaveError is not null) throw SaveError;
            LastSaved = tasks.Select(QueueStore.ToPersistedItem).ToArray();
            return Task.CompletedTask;
        }
    }
}
