using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationReliabilityTests
{
    [Theory]
    [InlineData(-1, 30)]
    [InlineData(6, 30)]
    [InlineData(1, 0)]
    [InlineData(1, 3601)]
    public void InvalidRetrySettings_AreRejected(int count, int delay)
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { RetryCount = count, RetryDelaySeconds = delay };
        Assert.Throws<InvalidDataException>(rule.Validate);
    }

    [Fact]
    public async Task OldSnapshot_DefaultsToNoCatchUpOrRetryAndRemainsReadable()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var store = Store(directory.Path);
        await store.SaveRulesAsync([rule]);
        var path = StorePath(directory.Path);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document.Remove("Suppressions");
        document.Remove("RetryProgress");
        var storedRule = document["Rules"]![0]!.AsObject();
        storedRule.Remove("CatchUpOnStartup");
        storedRule.Remove("RetryCount");
        storedRule.Remove("RetryDelaySeconds");
        await File.WriteAllTextAsync(path, document.ToJsonString());

        var restored = await store.LoadAsync();

        Assert.Empty(restored.Suppressions);
        Assert.Empty(restored.RetryProgress);
        Assert.False(restored.Rules[0].CatchUpOnStartup);
        Assert.Equal(0, restored.Rules[0].RetryCount);
        Assert.Equal(30, restored.Rules[0].RetryDelaySeconds);
        restored.Rules[0].Validate();
    }

    [Fact]
    public async Task StartupCatchUp_IsOptInAndPreservesDestinationExclusionsAndPriority()
    {
        using var directory = new TemporaryDirectory();
        var first = CreateRule(directory.Path) with { CatchUpOnStartup = true, IncludeSubfolders = true };
        first.OutputDirectory = Path.Combine(first.FolderPath, "output");
        first.ArchiveSource = true;
        first.ArchiveDirectory = Path.Combine(first.FolderPath, "archive");
        var later = first.Snapshot() with { Id = Guid.NewGuid(), Name = "Later rule" };
        var input = WriteInput(first, "existing.mp4");
        Directory.CreateDirectory(first.OutputDirectory);
        Directory.CreateDirectory(first.ArchiveDirectory);
        await File.WriteAllTextAsync(Path.Combine(first.OutputDirectory, "excluded.mp4"), "output");
        await File.WriteAllTextAsync(Path.Combine(first.ArchiveDirectory, "excluded.mp4"), "archive");
        var executed = new ConcurrentQueue<(Guid, string)>();
        await using var service = Service(directory.Path, async (rule, path, token) =>
        {
            executed.Enqueue((rule.Id, path));
            return await SucceedAsync(rule, path, token);
        });
        await service.ConfigureAsync([first, later]);
        await service.TickAsync();
        await service.WaitForIdleAsync();
        Assert.Empty(executed);

        await service.StartAsync();
        await DiscoverAndWaitAsync(service);

        Assert.Equal([(first.Id, input)], executed);
        var stored = await Store(directory.Path).LoadAsync();
        var record = Assert.Single(stored.Records);
        Assert.Equal(first.Id, record.RuleId);
        Assert.True(File.Exists(record.ArchivedPath));
        Assert.False(File.Exists(input));
    }

    [Fact]
    public async Task NormalWatcher_KeepsExistingBaselineAndStillDiscoversNewFiles()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        WriteInput(rule, "existing.mp4");
        var executed = new ConcurrentQueue<string>();
        await using var service = Service(directory.Path, async (matched, path, token) =>
        {
            executed.Enqueue(path);
            return await SucceedAsync(matched, path, token);
        });
        await service.ConfigureAsync([rule]);
        await service.StartAsync();
        await DiscoverAndWaitAsync(service);
        Assert.Empty(executed);
        var newFile = WriteInput(rule, "new.mp4");

        await DiscoverAndWaitAsync(service);

        Assert.Equal([newFile], executed);
    }

    [Fact]
    public async Task CatchUpWithoutWatcher_ProcessesOnlyStartupPathsAndDeduplicatesAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { CatchUpOnStartup = true, WatchEnabled = false };
        var first = WriteInput(rule, "first.mp4");
        var executed = new ConcurrentQueue<string>();
        async Task<AutomationExecutionResult> Execute(AutomationRule matched, string path, CancellationToken token)
        {
            executed.Enqueue(path);
            return await SucceedAsync(matched, path, token);
        }
        string later;
        await using (var initial = Service(directory.Path, Execute))
        {
            await initial.ConfigureAsync([rule]);
            await initial.StartAsync();
            await DiscoverAndWaitAsync(initial);
            later = WriteInput(rule, "later.mp4");
            await DiscoverAndWaitAsync(initial);
            Assert.Equal([first], executed);
        }

        await using (var restarted = Service(directory.Path, Execute))
        {
            await restarted.StartAsync();
            await DiscoverAndWaitAsync(restarted);
        }

        Assert.Equal([first, later], executed);
        Assert.Equal(2, (await Store(directory.Path).LoadAsync()).Records.Count);
    }

    [Fact]
    public async Task StartupCopy_IsDeferredUntilWriterClosesWithStableVersion()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { CatchUpOnStartup = true, WatchEnabled = false };
        var input = Path.Combine(rule.FolderPath, "copying.mp4");
        var executed = 0;
        await using var service = Service(directory.Path, async (matched, path, token) =>
        {
            Interlocked.Increment(ref executed);
            return await SucceedAsync(matched, path, token);
        });
        using (var writer = new FileStream(input, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write([1, 2, 3]);
            writer.Flush();
            await service.ConfigureAsync([rule]);
            await service.StartAsync();
            await DiscoverAndWaitAsync(service);
            writer.Write([4, 5, 6]);
            writer.Flush();
            await DiscoverAndWaitAsync(service);
            Assert.Equal(0, executed);
        }

        await DiscoverAndWaitAsync(service);

        Assert.Equal(1, executed);
        var record = Assert.Single((await Store(directory.Path).LoadAsync()).Records);
        Assert.Equal(6, record.Fingerprint.Length);
    }

    [Fact]
    public async Task OfflineStartupFolder_CatchesUpOnceAfterItBecomesAvailable()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { CatchUpOnStartup = true, WatchEnabled = false };
        Directory.Delete(rule.FolderPath);
        var attempts = 0;
        await using var service = Service(directory.Path, async (matched, path, token) =>
        {
            Interlocked.Increment(ref attempts);
            return await SucceedAsync(matched, path, token);
        });
        await service.ConfigureAsync([rule]);
        await service.StartAsync();
        await DiscoverAndWaitAsync(service);
        Assert.Equal(0, attempts);
        Directory.CreateDirectory(rule.FolderPath);
        WriteInput(rule, "offline-input.mp4");

        await DiscoverAndWaitAsync(service);
        WriteInput(rule, "later.mp4");
        await DiscoverAndWaitAsync(service);

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingAnotherRule_PreservesUnchangedStartupFilesWaitingForWriters(bool watchEnabled)
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { CatchUpOnStartup = true, WatchEnabled = watchEnabled };
        var other = CreateRule(Path.Combine(directory.Path, "other")) with { Name = "Other rule" };
        var input = Path.Combine(rule.FolderPath, "copying.mp4");
        var attempts = 0;
        await using var service = Service(directory.Path, async (matched, path, token) =>
        {
            Interlocked.Increment(ref attempts);
            return await SucceedAsync(matched, path, token);
        });
        using (var writer = new FileStream(input, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write([1, 2, 3]);
            writer.Flush();
            await service.ConfigureAsync([rule, other]);
            await service.StartAsync();
            await DiscoverAndWaitAsync(service);
            Assert.Equal(0, attempts);

            await service.ConfigureAsync([rule.Snapshot(), other with { Name = "Edited other rule" }]);
            await DiscoverAndWaitAsync(service);
            Assert.Equal(0, attempts);
        }

        await DiscoverAndWaitAsync(service);

        Assert.Equal(1, attempts);
        Assert.Equal(input, Assert.Single((await Store(directory.Path).LoadAsync()).Records).InputPath);
    }

    [Fact]
    public async Task RetryableFailure_RetriesWithinOneJobAndWaitsConfiguredInterval()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { RetryCount = 2, RetryDelaySeconds = 1 };
        WriteInput(rule, "input.mp4");
        var attempts = 0;
        var retryEvents = 0;
        await using var service = Service(directory.Path, async (matched, path, token) =>
        {
            if (Interlocked.Increment(ref attempts) < 3) return new(false, Error: "temporary lock", Retryable: true);
            return await SucceedAsync(matched, path, token);
        });
        service.ActivityChanged += (_, activity) =>
        {
            if (activity.State == AutomationActivityState.Waiting) Interlocked.Increment(ref retryEvents);
        };
        await service.ConfigureAsync([rule]);
        var timer = Stopwatch.StartNew();
        var first = service.RunNowAsync(rule.Id);
        var joined = service.RunNowAsync(rule.Id);

        var results = await Task.WhenAll(first, joined).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(3, attempts);
        Assert.Equal(2, retryEvents);
        Assert.All(results, summary => Assert.Equal(1, summary.Succeeded));
        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(2));
        var snapshot = await Store(directory.Path).LoadAsync();
        Assert.Single(snapshot.Records);
        Assert.Empty(snapshot.Suppressions);
        Assert.Empty(snapshot.RetryProgress);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task PermanentCancelledAndDeferredResults_DoNotAutomaticallyRetry(bool cancelled, bool deferred, bool retryable)
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { RetryCount = 5, RetryDelaySeconds = 1 };
        WriteInput(rule, "input.mp4");
        var attempts = 0;
        await using var service = Service(directory.Path, (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new AutomationExecutionResult(false, Error: "blocked", Cancelled: cancelled,
                Deferred: deferred, Retryable: retryable));
        });
        await service.ConfigureAsync([rule]);

        await service.RunNowAsync(rule.Id).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, attempts);
        var snapshot = await Store(directory.Path).LoadAsync();
        Assert.Empty(snapshot.Records);
        if (deferred) Assert.Empty(snapshot.Suppressions);
        else Assert.Equal(cancelled, Assert.Single(snapshot.Suppressions).Cancelled);
    }

    [Fact]
    public async Task ExhaustedRetryBudget_SurvivesPollingScheduleAndStartupButAllowsManualRecovery()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with
        {
            CatchUpOnStartup = true, RetryCount = 1, RetryDelaySeconds = 1,
            ScheduleEnabled = true, ScheduleTime = "09:00"
        };
        WriteInput(rule, "input.mp4");
        var attempts = 0;
        var fail = true;
        var now = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.FromHours(8));
        async Task<AutomationExecutionResult> Execute(AutomationRule matched, string path, CancellationToken token)
        {
            Interlocked.Increment(ref attempts);
            return fail ? new(false, Error: "network disconnected", Retryable: true) : await SucceedAsync(matched, path, token);
        }
        await using (var initial = Service(directory.Path, Execute, () => now))
        {
            await initial.ConfigureAsync([rule]);
            await initial.StartAsync();
            await DiscoverAndWaitAsync(initial);
            now = now.AddDays(1);
            await DiscoverAndWaitAsync(initial);
            Assert.Equal(2, attempts);
        }
        now = now.AddDays(1);
        await using (var restarted = Service(directory.Path, Execute, () => now))
        {
            await restarted.StartAsync();
            await DiscoverAndWaitAsync(restarted);
            Assert.Equal(2, attempts);
            Assert.Single((await Store(directory.Path).LoadAsync()).Suppressions);
            fail = false;
            Assert.Equal(1, (await restarted.RunNowAsync(rule.Id)).Succeeded);
        }

        Assert.Equal(3, attempts);
        var snapshot = await Store(directory.Path).LoadAsync();
        Assert.Empty(snapshot.Suppressions);
        Assert.Single(snapshot.Records);
    }

    [Fact]
    public async Task UserCancellation_SurvivesRestartAndOwnershipChangeUntilSourceVersionChanges()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { CatchUpOnStartup = true };
        var input = WriteInput(rule, "input.mp4");
        await using (var initial = Service(directory.Path, (_, _, _) => Task.FromResult(new AutomationExecutionResult(false, Cancelled: true))))
        {
            await initial.ConfigureAsync([rule]);
            await initial.RunNowAsync(rule.Id);
        }
        var replacementRule = rule.Snapshot() with { Id = Guid.NewGuid(), Name = "Replacement owner" };
        await Store(directory.Path).SaveRulesAsync([replacementRule]);
        var attempts = 0;
        await using (var restarted = Service(directory.Path, async (matched, path, token) =>
        {
            Interlocked.Increment(ref attempts);
            return await SucceedAsync(matched, path, token);
        }))
        {
            await restarted.StartAsync();
            await DiscoverAndWaitAsync(restarted);
            var manual = await restarted.RunNowAsync(replacementRule.Id);
            Assert.Equal(1, manual.Skipped);
            Assert.Equal(0, attempts);
            await File.WriteAllTextAsync(input, "replacement source with a different version");
            await DiscoverAndWaitAsync(restarted);
        }

        Assert.Equal(1, attempts);
        var snapshot = await Store(directory.Path).LoadAsync();
        Assert.True(Assert.Single(snapshot.Suppressions).Cancelled);
        Assert.Single(snapshot.Records);
    }

    [Fact]
    public async Task DirectRestoredCancellation_IsDurableAndSuccessfulQueueRetryClearsIt()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var input = WriteInput(rule, "input.mp4");
        var fingerprint = FileFingerprintService.Read(input)!;
        await using var service = Service(directory.Path, SucceedAsync);
        await service.ConfigureAsync([rule]);

        await service.CompleteExecutionAsync(rule, input, fingerprint, new(false, Cancelled: true));
        Assert.True(Assert.Single((await Store(directory.Path).LoadAsync()).Suppressions).Cancelled);
        var converted = await SucceedAsync(rule, input, CancellationToken.None);
        await service.CompleteExecutionAsync(rule, input, fingerprint, converted);

        var snapshot = await Store(directory.Path).LoadAsync();
        Assert.Empty(snapshot.Suppressions);
        Assert.Single(snapshot.Records);
    }

    [Fact]
    public async Task DirectUserCancellationDuringRetryWait_StopsRetryImmediately()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { RetryCount = 5, RetryDelaySeconds = 3600 };
        var input = WriteInput(rule, "input.mp4");
        var fingerprint = FileFingerprintService.Read(input)!;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var service = Service(directory.Path, (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new AutomationExecutionResult(false, Retryable: true));
        });
        service.ActivityChanged += (_, activity) =>
        {
            if (activity.State == AutomationActivityState.Waiting) waiting.TrySetResult();
        };
        await service.ConfigureAsync([rule]);
        var run = service.RunNowAsync(rule.Id);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.CompleteExecutionAsync(rule, input, fingerprint, new(false, Cancelled: true));
        var summary = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, attempts);
        Assert.Equal(1, summary.Skipped);
        Assert.True(Assert.Single((await Store(directory.Path).LoadAsync()).Suppressions).Cancelled);
        Assert.False(service.HasActiveWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuleDisableOrParameterChange_InterruptsRetryWaitAndStopsFurtherAttempts(bool disable)
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { RetryCount = 5, RetryDelaySeconds = 3600 };
        WriteInput(rule, "input.mp4");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var service = Service(directory.Path, (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new AutomationExecutionResult(false, Retryable: true));
        });
        service.ActivityChanged += (_, activity) =>
        {
            if (activity.State == AutomationActivityState.Waiting) waiting.TrySetResult();
        };
        await service.ConfigureAsync([rule]);
        var run = service.RunNowAsync(rule.Id);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var changed = rule.Snapshot();
        if (disable) changed.Enabled = false;
        else changed.Options["profile"] = "quality";

        await service.ConfigureAsync([changed]);
        var summary = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, attempts);
        Assert.Equal(1, summary.Skipped);
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.HasActiveWork);
    }

    [Fact]
    public async Task Shutdown_InterruptsRetryWaitWithoutRememberingUserCancellation()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { RetryCount = 5, RetryDelaySeconds = 3600 };
        WriteInput(rule, "input.mp4");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var service = Service(directory.Path, (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new AutomationExecutionResult(false, Retryable: true));
        });
        service.ActivityChanged += (_, activity) =>
        {
            if (activity.State == AutomationActivityState.Waiting) waiting.TrySetResult();
        };
        await service.ConfigureAsync([rule]);
        var run = service.RunNowAsync(rule.Id);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(1, attempts);
        var snapshot = await Store(directory.Path).LoadAsync();
        Assert.Empty(snapshot.Suppressions);
        Assert.Equal(1, Assert.Single(snapshot.RetryProgress).FailedAttempts);
        Assert.False(service.HasActiveWork);
    }

    [Fact]
    public async Task DeferredAttempt_PreservesRemainingBudgetThroughPollingAndConfigure()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { CatchUpOnStartup = true, RetryCount = 1, RetryDelaySeconds = 1 };
        WriteInput(rule, "input.mp4");
        var attempts = 0;
        await using var service = Service(directory.Path, (_, _, _) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            return Task.FromResult(attempt == 2
                ? new AutomationExecutionResult(false, Deferred: true)
                : new AutomationExecutionResult(false, Retryable: true));
        });
        await service.ConfigureAsync([rule]);
        await service.StartAsync();
        await service.TickAsync();
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
        Assert.Equal(1, Assert.Single((await Store(directory.Path).LoadAsync()).RetryProgress).FailedAttempts);

        await service.TickAsync();
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await service.ConfigureAsync([rule]);
        await service.StopAsync();
        await service.StartAsync();
        await DiscoverAndWaitAsync(service);

        Assert.Equal(3, attempts);
        var snapshot = await Store(directory.Path).LoadAsync();
        Assert.Single(snapshot.Suppressions);
        Assert.Empty(snapshot.RetryProgress);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task PartialRetryBudget_SurvivesShutdownAndRestart(int retriesAfterRestart, int totalAttempts)
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { CatchUpOnStartup = true, RetryCount = 1, RetryDelaySeconds = 3600 };
        WriteInput(rule, "input.mp4");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        Task<AutomationExecutionResult> Execute(AutomationRule _, string __, CancellationToken ___)
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new AutomationExecutionResult(false, Retryable: true));
        }
        await using (var initial = Service(directory.Path, Execute))
        {
            initial.ActivityChanged += (_, activity) =>
            {
                if (activity.State == AutomationActivityState.Waiting) waiting.TrySetResult();
            };
            await initial.ConfigureAsync([rule]);
            await initial.StartAsync();
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await initial.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, Assert.Single((await Store(directory.Path).LoadAsync()).RetryProgress).FailedAttempts);
        await Store(directory.Path).SaveRulesAsync([rule with { RetryCount = retriesAfterRestart, RetryDelaySeconds = 1 }]);

        await using (var restarted = Service(directory.Path, Execute))
        {
            await restarted.StartAsync();
            await DiscoverAndWaitAsync(restarted);
        }

        Assert.Equal(totalAttempts, attempts);
        var snapshot = await Store(directory.Path).LoadAsync();
        Assert.Single(snapshot.Suppressions);
        Assert.Empty(snapshot.RetryProgress);
    }

    [Fact]
    public async Task PermanentFailureAfterTransientAttempt_ReplacesRetryProgressWithTerminalCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { CatchUpOnStartup = true, RetryCount = 5, RetryDelaySeconds = 1 };
        WriteInput(rule, "input.mp4");
        var attempts = 0;
        Task<AutomationExecutionResult> Execute(AutomationRule _, string __, CancellationToken ___)
        {
            var attempt = Interlocked.Increment(ref attempts);
            return Task.FromResult(new AutomationExecutionResult(false, Error: attempt == 1 ? "temporary lock" : "corrupt input",
                Retryable: attempt == 1));
        }
        await using (var initial = Service(directory.Path, Execute))
        {
            await initial.ConfigureAsync([rule]);
            await initial.StartAsync();
            await DiscoverAndWaitAsync(initial);
            var snapshot = await Store(directory.Path).LoadAsync();
            Assert.Equal(2, attempts);
            Assert.Empty(snapshot.RetryProgress);
            Assert.False(Assert.Single(snapshot.Suppressions).Cancelled);
            await initial.StopAsync();
        }

        await using (var restarted = Service(directory.Path, Execute))
        {
            await restarted.StartAsync();
            await DiscoverAndWaitAsync(restarted);
        }

        Assert.Equal(2, attempts);
        Assert.Empty((await Store(directory.Path).LoadAsync()).RetryProgress);
    }

    [Fact]
    public async Task SourceChangeDuringRetry_DoesNotExecuteOldVersionAgain()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { RetryCount = 1, RetryDelaySeconds = 1 };
        var input = WriteInput(rule, "input.mp4");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var service = Service(directory.Path, async (matched, path, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) return new(false, Retryable: true);
            return await SucceedAsync(matched, path, token);
        });
        service.ActivityChanged += (_, activity) =>
        {
            if (activity.State == AutomationActivityState.Waiting) waiting.TrySetResult();
        };
        await service.ConfigureAsync([rule]);
        var run = service.RunNowAsync(rule.Id);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(input, "a new source version with new content");

        Assert.Equal(1, (await run.WaitAsync(TimeSpan.FromSeconds(5))).Skipped);
        Assert.Equal(1, attempts);
        Assert.Equal(1, (await service.RunNowAsync(rule.Id)).Succeeded);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SharingViolationException_IsRetriedButOtherIoErrorsArePermanent()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path) with { RetryCount = 1, RetryDelaySeconds = 1 };
        WriteInput(rule, "input.mp4");
        var attempts = 0;
        await using var service = Service(directory.Path, async (matched, path, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("sharing violation", unchecked((int)0x80070020));
            return await SucceedAsync(matched, path, token);
        });
        await service.ConfigureAsync([rule]);

        Assert.Equal(1, (await service.RunNowAsync(rule.Id)).Succeeded);

        Assert.Equal(2, attempts);
        Assert.False(ConversionFailurePolicy.IsTransient(new IOException("disk full", unchecked((int)0x80070070))));
        Assert.False(ConversionFailurePolicy.IsTransient(new InvalidDataException("corrupt input")));
        Assert.False(ConversionFailurePolicy.IsTransient(new UnauthorizedAccessException()));
        Assert.True(ConversionFailurePolicy.IsTransient(new InvalidOperationException("wrapped", new IOException("network", unchecked((int)0x80070040)))));
    }

    private static AutomationRule CreateRule(string root)
    {
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        return new AutomationRule
        {
            Name = "Test rule", FolderPath = input,
            OutputDirectory = Path.Combine(root, "output"), ArchiveDirectory = Path.Combine(root, "archive")
        };
    }

    private static string WriteInput(AutomationRule rule, string name)
    {
        var path = Path.Combine(rule.FolderPath, name);
        File.WriteAllText(path, "source bytes");
        return path;
    }

    private static string StorePath(string root) => Path.Combine(root, "automation.json");
    private static AutomationStore Store(string root) => new(StorePath(root));
    private static AutomationService Service(string root,
        Func<AutomationRule, string, CancellationToken, Task<AutomationExecutionResult>> execute,
        Func<DateTimeOffset>? clock = null) => new(Store(root), execute,
        pollInterval: TimeSpan.FromHours(1), settleDelay: TimeSpan.Zero, clock: clock);

    private static async Task DiscoverAndWaitAsync(AutomationService service)
    {
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8));
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8));
    }

    private static async Task<AutomationExecutionResult> SucceedAsync(AutomationRule rule, string input, CancellationToken token)
    {
        Directory.CreateDirectory(rule.OutputDirectory);
        var output = Path.Combine(rule.OutputDirectory, Path.GetFileNameWithoutExtension(input) + "." + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllTextAsync(output, "converted bytes", token);
        return new(true, output);
    }
}
