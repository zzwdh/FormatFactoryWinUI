using System.Collections.Concurrent;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationServiceTests
{
    [Fact]
    public async Task RunningService_DiscoversNewFileWithoutManualTick()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new AutomationService(new AutomationStore(StorePath(directory.Path)),
            async (matched, source, token) =>
            {
                var result = await SucceedAsync(matched, source, token);
                completion.TrySetResult(source);
                return result;
            }, pollInterval: TimeSpan.FromMilliseconds(25), settleDelay: TimeSpan.FromMilliseconds(15));
        await service.ConfigureAsync([rule]);
        await service.StartAsync();

        var input = WriteInput(rule, "arrival.mp4");

        Assert.Equal(input, await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await service.WaitForIdleAsync();
        await service.StopAsync();
    }

    [Fact]
    public async Task EnablingWatch_BaselinesExistingFilesAndProcessesOnlyNewArrival()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var oldFile = WriteInput(rule, "existing.mp4");
        var executed = new ConcurrentQueue<string>();
        await using var service = CreateService(directory.Path, async (matched, input, token) =>
        {
            executed.Enqueue(input);
            return await SucceedAsync(matched, input, token);
        });

        await service.ConfigureAsync([rule]);
        await service.TickAsync();
        Assert.Empty(executed);

        var newFile = WriteInput(rule, "new.mp4");
        await service.TickAsync();
        await Task.Delay(30);
        await service.TickAsync();
        await service.WaitForIdleAsync();

        Assert.Equal([newFile], executed);
        Assert.True(File.Exists(oldFile));
    }

    [Fact]
    public async Task ManualRun_ScansExistingFilesEvenWhenWatchBaselinedThem()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var input = WriteInput(rule, "existing.mp4");
        var executed = new ConcurrentQueue<string>();
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            executed.Enqueue(source);
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);

        var result = await service.RunNowAsync(rule.Id);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal([input], executed);
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task ConfiguredRule_IsIsolatedFromUncommittedEditorChanges()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.TargetFormatId = "mp3";
        rule.ConversionMode = "quality";
        rule.Options["profile"] = "original";
        WriteInput(rule, "source.mp4");
        AutomationRule? executedRule = null;
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            executedRule = matched.Snapshot();
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);
        rule.TargetFormatId = "wav";
        rule.ConversionMode = "fast";
        rule.Options["profile"] = "edited";

        await service.RunNowAsync(rule.Id);

        Assert.NotNull(executedRule);
        Assert.Equal("mp3", executedRule.TargetFormatId);
        Assert.Equal("quality", executedRule.ConversionMode);
        Assert.Equal("original", executedRule.Options["profile"]);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task IncludeSubfolders_ControlsRecursiveManualDiscovery(bool include, int expected)
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.IncludeSubfolders = include;
        WriteInput(rule, "root.mp4");
        var nested = Path.Combine(rule.FolderPath, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "child.mp4"), "child bytes");
        var executed = 0;
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            Interlocked.Increment(ref executed);
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);

        var result = await service.RunNowAsync(rule.Id);

        Assert.Equal(expected, executed);
        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public async Task DisabledRule_IsNotExecutedByManualRunOrWatch()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.Enabled = false;
        var executed = 0;
        await using var service = CreateService(directory.Path, async (matched, input, token) =>
        {
            Interlocked.Increment(ref executed);
            return await SucceedAsync(matched, input, token);
        });
        await service.ConfigureAsync([rule]);
        WriteInput(rule, "ignored.mp4");

        await service.RunNowAsync(rule.Id);
        await service.TickAsync();

        Assert.Equal(0, executed);
    }

    [Fact]
    public async Task FirstMatchingRule_WinsAcrossOverlappingRules()
    {
        using var directory = new TemporaryDirectory();
        var first = CreateRule(directory.Path);
        var second = first.Snapshot();
        second.Id = Guid.NewGuid();
        second.Name = "Second";
        second.TargetFormatId = "mp3";
        var executed = new ConcurrentQueue<Guid>();
        await using var service = CreateService(directory.Path, async (matched, input, token) =>
        {
            executed.Enqueue(matched.Id);
            return await SucceedAsync(matched, input, token);
        });
        await service.ConfigureAsync([first, second]);
        WriteInput(first, "source.mp4");

        await service.RunNowAsync(second.Id);
        Assert.Empty(executed);
        await service.TickAsync();
        await Task.Delay(30);
        await service.TickAsync();
        await service.WaitForIdleAsync();

        Assert.Equal([first.Id], executed);
    }

    [Fact]
    public async Task FirstMatchWithoutWatchTrigger_DoesNotFallThroughToLaterWatchRule()
    {
        using var directory = new TemporaryDirectory();
        var first = CreateRule(directory.Path);
        first.WatchEnabled = false;
        var second = first.Snapshot();
        second.Id = Guid.NewGuid();
        second.WatchEnabled = true;
        var executed = new ConcurrentQueue<Guid>();
        await using var service = CreateService(directory.Path, async (matched, input, token) =>
        {
            executed.Enqueue(matched.Id);
            return await SucceedAsync(matched, input, token);
        });
        await service.ConfigureAsync([first, second]);
        WriteInput(first, "source.mp4");

        await service.TickAsync();
        await service.RunNowAsync(second.Id);
        Assert.Empty(executed);
        await service.RunNowAsync(first.Id);

        Assert.Equal([first.Id], executed);
    }

    [Fact]
    public async Task FilteredEarlierRule_AllowsNextMatchingRule()
    {
        using var directory = new TemporaryDirectory();
        var first = CreateRule(directory.Path);
        first.Extensions = ".mkv";
        var second = first.Snapshot();
        second.Id = Guid.NewGuid();
        second.Extensions = ".MP4; mov";
        second.FileNamePattern = "clip-?*";
        var executed = new ConcurrentQueue<(Guid RuleId, string Source)>();
        await using var service = CreateService(directory.Path, async (matched, input, token) =>
        {
            executed.Enqueue((matched.Id, input));
            return await SucceedAsync(matched, input, token);
        });
        await service.ConfigureAsync([first, second]);
        var matchedFile = WriteInput(first, "clip-1.MP4");
        WriteInput(first, "other.mp4");
        WriteInput(first, "clip-2.txt");

        await service.RunNowAsync(second.Id);

        Assert.Equal([(second.Id, matchedFile)], executed);
    }

    [Fact]
    public async Task SuccessfulFingerprint_IsDurableAndPreventsDuplicateAfterServiceRestart()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        WriteInput(rule, "source.mp4");
        var executed = 0;
        async Task<AutomationExecutionResult> Execute(AutomationRule matched, string input, CancellationToken token)
        {
            Interlocked.Increment(ref executed);
            return await SucceedAsync(matched, input, token);
        }
        await using (var initial = CreateService(directory.Path, Execute))
        {
            await initial.ConfigureAsync([rule]);
            await initial.RunNowAsync(rule.Id);
            await initial.RunNowAsync(rule.Id);
        }
        await using (var restarted = CreateService(directory.Path, Execute))
        {
            await restarted.StartAsync();
            await restarted.RunNowAsync(rule.Id);
            await restarted.StopAsync();
        }

        Assert.Equal(1, executed);
        Assert.Single((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
    }

    [Fact]
    public async Task RewritingSuccessfulFile_AllowsNewFingerprintToRun()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var input = WriteInput(rule, "source.mp4");
        var executed = 0;
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            Interlocked.Increment(ref executed);
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);
        await service.RunNowAsync(rule.Id);

        await File.WriteAllTextAsync(input, "replacement with different bytes");
        await service.RunNowAsync(rule.Id);
        await service.RunNowAsync(rule.Id);

        Assert.Equal(2, executed);
    }

    [Fact]
    public async Task WatchModification_ToSuccessfulSourceRunsNewVersionExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var input = WriteInput(rule, "source.mp4");
        var executed = 0;
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            Interlocked.Increment(ref executed);
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);
        await service.RunNowAsync(rule.Id);
        await File.WriteAllTextAsync(input, "new bytes replacing successful source");

        await service.TickAsync();
        await Task.Delay(30);
        await service.TickAsync();
        await service.WaitForIdleAsync();
        await service.TickAsync();
        await service.WaitForIdleAsync();

        Assert.Equal(2, executed);
    }

    [Fact]
    public async Task StopAndRestart_DoesNotExecutePreviouslySuccessfulFileAgain()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var executed = 0;
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            Interlocked.Increment(ref executed);
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);
        WriteInput(rule, "source.mp4");
        await service.TickAsync();
        await Task.Delay(30);
        await service.TickAsync();
        await service.WaitForIdleAsync();

        await service.StopAsync();
        await service.StartAsync();
        await service.TickAsync();
        await service.WaitForIdleAsync();
        await service.RunNowAsync(rule.Id);
        await service.StopAsync();

        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task FailedConversion_KeepsOriginalAndCanBeRetriedManually()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.ArchiveSource = true;
        var input = WriteInput(rule, "source.mp4");
        var executed = 0;
        await using var service = CreateService(directory.Path, (_, _, _) =>
        {
            Interlocked.Increment(ref executed);
            return Task.FromResult(new AutomationExecutionResult(false, Error: "conversion failed"));
        });
        await service.ConfigureAsync([rule]);

        var first = await service.RunNowAsync(rule.Id);
        var retry = await service.RunNowAsync(rule.Id);

        Assert.Equal(1, first.Failed);
        Assert.Equal(1, retry.Failed);
        Assert.Equal(2, executed);
        Assert.True(File.Exists(input));
        Assert.False(Directory.Exists(rule.ArchiveDirectory));
        Assert.Empty((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
    }

    [Fact]
    public async Task SuccessfulResultWithMissingOutput_KeepsOriginalAndDoesNotRecordSuccess()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.ArchiveSource = true;
        var input = WriteInput(rule, "source.mp4");
        await using var service = CreateService(directory.Path, (_, _, _) =>
            Task.FromResult(new AutomationExecutionResult(true, Path.Combine(rule.OutputDirectory, "missing.mp4"))));
        await service.ConfigureAsync([rule]);

        var result = await service.RunNowAsync(rule.Id);

        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(input));
        Assert.Empty((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
    }

    [Fact]
    public async Task SuccessfulResultPointingToOriginal_IsRejectedAndCannotArchiveSource()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.ArchiveSource = true;
        var input = WriteInput(rule, "source.mp4");
        await using var service = CreateService(directory.Path, (_, source, _) =>
            Task.FromResult(new AutomationExecutionResult(true, source)));
        await service.ConfigureAsync([rule]);

        var result = await service.RunNowAsync(rule.Id);

        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(input));
        Assert.Empty((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
    }

    [Fact]
    public async Task FailedSuccessCheckpoint_KeepsOriginalAndPreservesUnreadableConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.ArchiveSource = true;
        var input = WriteInput(rule, "source.mp4");
        await using var service = CreateService(directory.Path, SucceedAsync);
        await service.ConfigureAsync([rule]);
        const string broken = "configuration damaged after service initialization";
        await File.WriteAllTextAsync(StorePath(directory.Path), broken);

        var result = await service.RunNowAsync(rule.Id);

        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(input));
        Assert.False(Directory.Exists(rule.ArchiveDirectory));
        Assert.Equal(broken, await File.ReadAllTextAsync(StorePath(directory.Path)));
    }

    [Fact]
    public async Task CompletionForRestoredQueueTask_PersistsAndArchivesExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.ArchiveSource = true;
        var input = WriteInput(rule, "source.mp4");
        var fingerprint = FileFingerprintService.Read(input)!;
        var output = await SucceedAsync(rule, input, CancellationToken.None);
        await using (var initial = CreateService(directory.Path, SucceedAsync))
        {
            await initial.ConfigureAsync([rule]);

            var result = await initial.CompleteExecutionAsync(rule, input, fingerprint, output);

            Assert.True(result.Success);
            Assert.False(File.Exists(input));
        }
        await using (var restarted = CreateService(directory.Path, SucceedAsync))
        {
            await restarted.StartAsync();
            await restarted.CompleteExecutionAsync(rule, input, fingerprint, output);
            await restarted.StopAsync();
        }

        Assert.Single(Directory.EnumerateFiles(rule.ArchiveDirectory));
        var record = Assert.Single((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
        Assert.Equal(fingerprint, record.Fingerprint);
        Assert.Equal(output.OutputPath, record.OutputPath);
        Assert.True(File.Exists(record.ArchivedPath));
    }

    [Fact]
    public async Task RestoredQueueCompletionAfterStop_CannotArchiveWithDefaultCancellationToken()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.ArchiveSource = true;
        var input = WriteInput(rule, "source.mp4");
        var fingerprint = FileFingerprintService.Read(input)!;
        var output = await SucceedAsync(rule, input, CancellationToken.None);
        await using var service = CreateService(directory.Path, SucceedAsync);
        await service.ConfigureAsync([rule]);
        await service.StopAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CompleteExecutionAsync(rule, input, fingerprint, output));

        Assert.True(File.Exists(input));
        Assert.False(Directory.Exists(rule.ArchiveDirectory));
        Assert.Empty((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
    }

    [Fact]
    public async Task SourceChangedDuringConversion_IsNotArchived()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.ArchiveSource = true;
        var input = WriteInput(rule, "source.mp4");
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            await File.WriteAllTextAsync(source, "replacement bytes written during conversion", token);
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);

        await service.RunNowAsync(rule.Id);

        Assert.True(File.Exists(input));
        Assert.Equal("replacement bytes written during conversion", await File.ReadAllTextAsync(input));
        Assert.False(Directory.Exists(rule.ArchiveDirectory));
    }

    [Fact]
    public async Task SuccessfulConversion_ArchivesSourceWithoutOverwritingExistingArchive()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.ArchiveSource = true;
        var input = WriteInput(rule, "source.mp4");
        Directory.CreateDirectory(rule.ArchiveDirectory);
        var previous = Path.Combine(rule.ArchiveDirectory, "source.mp4");
        await File.WriteAllTextAsync(previous, "previous archive");
        await using var service = CreateService(directory.Path, SucceedAsync);
        await service.ConfigureAsync([rule]);

        var result = await service.RunNowAsync(rule.Id);

        Assert.Equal(1, result.Succeeded);
        Assert.False(File.Exists(input));
        Assert.Equal("previous archive", await File.ReadAllTextAsync(previous));
        var archived = Directory.EnumerateFiles(rule.ArchiveDirectory).Where(path => path != previous).Single();
        Assert.Equal("input bytes", await File.ReadAllTextAsync(archived));
        var record = Assert.Single((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
        Assert.Equal(archived, record.ArchivedPath);
    }

    [Fact]
    public async Task OutputAndArchiveSubdirectories_AreExcludedFromRecursiveInputScan()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.IncludeSubfolders = true;
        rule.ArchiveSource = true;
        rule.OutputDirectory = Path.Combine(rule.FolderPath, "output");
        rule.ArchiveDirectory = Path.Combine(rule.FolderPath, "archive");
        Directory.CreateDirectory(rule.OutputDirectory);
        Directory.CreateDirectory(rule.ArchiveDirectory);
        File.WriteAllText(Path.Combine(rule.OutputDirectory, "converted.mp4"), "output bytes");
        File.WriteAllText(Path.Combine(rule.ArchiveDirectory, "archived.mp4"), "archive bytes");
        var input = WriteInput(rule, "source.mp4");
        var executed = new ConcurrentQueue<string>();
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            executed.Enqueue(source);
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);

        await service.RunNowAsync(rule.Id);
        await service.RunNowAsync(rule.Id);
        await service.TickAsync();

        Assert.Equal([input], executed);
    }

    [Fact]
    public async Task CopyWithOpenWriter_IsNeverExecutedUntilWriterCloses()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var executed = 0;
        var completed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new AutomationService(new AutomationStore(StorePath(directory.Path)),
            async (matched, source, token) =>
            {
                Interlocked.Increment(ref executed);
                var bytes = await File.ReadAllBytesAsync(source, token);
                var result = await SucceedAsync(matched, source, token);
                completed.TrySetResult(bytes);
                return result;
            }, pollInterval: TimeSpan.FromMilliseconds(25), settleDelay: TimeSpan.FromMilliseconds(30));
        await service.ConfigureAsync([rule]);
        await service.StartAsync();
        var input = Path.Combine(rule.FolderPath, "copying.mp4");
        using (var writer = new FileStream(input, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write([1, 2, 3]);
            writer.Flush();
            await Task.Delay(100);
            Assert.Equal(0, Volatile.Read(ref executed));
            writer.Write([4, 5, 6]);
            writer.Flush();
            await Task.Delay(100);
            Assert.Equal(0, Volatile.Read(ref executed));
        }
        var convertedBytes = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, executed);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, convertedBytes);
    }

    [Fact]
    public async Task DisablingRules_PreservesActiveExecutionAndPreventsNewWork()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var input = WriteInput(rule, "active.mp4");
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<string>();
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            executed.Enqueue(source);
            entered.TrySetResult(token);
            await release.Task.WaitAsync(token);
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);
        Assert.False(service.HasActiveWork);
        var run = service.RunNowAsync(rule.Id);
        var executionToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.HasActiveWork);

        await service.ConfigureAsync([rule with { Enabled = false }]);
        WriteInput(rule, "later.mp4");
        await service.TickAsync();

        Assert.True(service.HasActiveWork);
        Assert.False(executionToken.IsCancellationRequested);
        Assert.False(run.IsCompleted);
        Assert.Equal([input], executed);
        release.TrySetResult();
        var summary = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await service.TickAsync();
        var disabledRun = await service.RunNowAsync(rule.Id);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(0, disabledRun.Scanned);
        Assert.False(service.HasActiveWork);
        Assert.Equal([input], executed);
        var record = Assert.Single((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
        Assert.Equal(input, record.InputPath);
        Assert.True(File.Exists(record.OutputPath));
        Assert.False(Assert.Single(service.Rules).Enabled);
    }

    [Fact]
    public async Task Stop_CancelsActiveExecutionAndDoesNotArchiveOrRecordLateSuccess()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.ArchiveSource = true;
        var input = WriteInput(rule, "source.mp4");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled.TrySetResult(); }
            // A host can finish just as shutdown cancels it. The service must reject this late success.
            return await SucceedAsync(matched, source, CancellationToken.None);
        });
        await service.ConfigureAsync([rule]);
        var run = service.RunNowAsync(rule.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }

        Assert.True(File.Exists(input));
        Assert.False(Directory.Exists(rule.ArchiveDirectory));
        Assert.Empty((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
    }

    [Fact]
    public async Task Dispose_CancelsActiveExecutionAndWaitsForHostToFinish()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var input = WriteInput(rule, "source.mp4");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(directory.Path, async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (token.IsCancellationRequested) cancelled.TrySetResult(); }
            return new(false, Cancelled: true);
        });
        await service.ConfigureAsync([rule]);
        var run = service.RunNowAsync(rule.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }

        Assert.True(File.Exists(input));
        Assert.Empty((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records);
    }

    [Fact]
    public async Task ConcurrentScans_DoNotExecuteSameFingerprintTwice()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var input = WriteInput(rule, "source.mp4");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = 0;
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            Interlocked.Increment(ref executed);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return await SucceedAsync(matched, source, token);
        });
        await service.ConfigureAsync([rule]);
        var first = service.RunNowAsync(rule.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.RunNowAsync(rule.Id);
        release.TrySetResult();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, executed);
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task LongConversion_DoesNotBlockDiscoveryForAnotherRule()
    {
        using var directory = new TemporaryDirectory();
        var firstRoot = Path.Combine(directory.Path, "first");
        var secondRoot = Path.Combine(directory.Path, "second");
        var first = CreateRule(firstRoot);
        var second = CreateRule(secondRoot);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService(directory.Path, async (matched, input, token) =>
        {
            if (matched.Id == first.Id)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            else secondEntered.TrySetResult();
            return await SucceedAsync(matched, input, token);
        });
        await service.ConfigureAsync([first, second]);
        WriteInput(first, "long.mp4");
        await service.TickAsync();
        await Task.Delay(30);
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        WriteInput(second, "later.mp4");
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(30);
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseFirst.TrySetResult();
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await new AutomationStore(StorePath(directory.Path)).LoadAsync()).Records.Count);
    }

    [Fact]
    public async Task BurstOfFiles_RespectsFourConcurrentHostExecutions()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        var enteredFour = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var executed = 0;
        await using var service = CreateService(directory.Path, async (matched, input, token) =>
        {
            var count = Interlocked.Increment(ref active);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximum);
                if (count <= observed) break;
            }
            while (Interlocked.CompareExchange(ref maximum, count, observed) != observed);
            if (Interlocked.Increment(ref executed) == 4) enteredFour.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                return await SucceedAsync(matched, input, token);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        await service.ConfigureAsync([rule]);
        for (var index = 0; index < 6; index++) WriteInput(rule, $"source-{index}.mp4");
        await service.TickAsync();
        await Task.Delay(30);
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await enteredFour.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, Volatile.Read(ref active));
        Assert.Equal(4, Volatile.Read(ref executed));
        release.TrySetResult();
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await service.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(6, executed);
        Assert.Equal(4, maximum);
    }

    [Fact]
    public async Task ScheduledRun_ScansExistingFilesAtLocalTimeAndOnlyOncePerDay()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.WatchEnabled = false;
        rule.ScheduleEnabled = true;
        rule.ScheduleTime = "09:00";
        var first = WriteInput(rule, "existing.mp4");
        var now = new DateTimeOffset(2026, 10, 3, 8, 59, 0, TimeSpan.FromHours(8));
        var executed = new ConcurrentQueue<string>();
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            executed.Enqueue(source);
            return await SucceedAsync(matched, source, token);
        }, () => now);
        await service.ConfigureAsync([rule]);
        await service.TickAsync();
        Assert.Empty(executed);

        now = now.AddMinutes(2);
        await service.TickAsync();
        await service.WaitForIdleAsync();
        Assert.Equal([first], executed);
        var second = WriteInput(rule, "later.mp4");
        await service.TickAsync();
        await service.WaitForIdleAsync();
        Assert.Equal([first], executed);
        now = now.AddDays(1);
        await service.TickAsync();
        await service.WaitForIdleAsync();

        Assert.Equal([first, second], executed);
        Assert.Equal("2026-10-04", (await new AutomationStore(StorePath(directory.Path)).LoadAsync()).ScheduleRuns[rule.Id]);
    }

    [Fact]
    public async Task ScheduledCheckpoint_SurvivesRestartAndDoesNotRescanSameDay()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.WatchEnabled = false;
        rule.ScheduleEnabled = true;
        rule.ScheduleTime = "09:00";
        WriteInput(rule, "first.mp4");
        var now = new DateTimeOffset(2026, 10, 3, 9, 1, 0, TimeSpan.FromHours(8));
        var executed = 0;
        async Task<AutomationExecutionResult> Execute(AutomationRule matched, string source, CancellationToken token)
        {
            Interlocked.Increment(ref executed);
            return await SucceedAsync(matched, source, token);
        }
        await using (var initial = CreateService(directory.Path, Execute, () => now))
        {
            await initial.ConfigureAsync([rule]);
            await initial.TickAsync();
            await initial.WaitForIdleAsync();
        }
        WriteInput(rule, "second.mp4");
        await using (var restarted = CreateService(directory.Path, Execute, () => now))
        {
            await restarted.StartAsync();
            await restarted.TickAsync();
            await restarted.WaitForIdleAsync();
            await restarted.StopAsync();
        }

        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task ScheduledCopyStillInProgress_IsRetriedAfterWriterClosesOnSameDay()
    {
        using var directory = new TemporaryDirectory();
        var rule = CreateRule(directory.Path);
        rule.WatchEnabled = false;
        rule.ScheduleEnabled = true;
        rule.ScheduleTime = "09:00";
        var now = new DateTimeOffset(2026, 10, 3, 9, 1, 0, TimeSpan.FromHours(8));
        var executed = 0;
        await using var service = CreateService(directory.Path, async (matched, source, token) =>
        {
            Interlocked.Increment(ref executed);
            return await SucceedAsync(matched, source, token);
        }, () => now);
        await service.ConfigureAsync([rule]);
        var input = Path.Combine(rule.FolderPath, "copying.mp4");
        using (var writer = new FileStream(input, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write([1, 2, 3]);
            writer.Flush();
            await service.TickAsync();
            await service.WaitForIdleAsync();

            Assert.Equal(0, Volatile.Read(ref executed));
            Assert.False((await new AutomationStore(StorePath(directory.Path)).LoadAsync()).ScheduleRuns.ContainsKey(rule.Id));
        }

        await service.TickAsync();
        await service.WaitForIdleAsync();

        Assert.Equal(1, executed);
        Assert.Equal("2026-10-03", (await new AutomationStore(StorePath(directory.Path)).LoadAsync()).ScheduleRuns[rule.Id]);
    }

    private static AutomationRule CreateRule(string root)
    {
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        return new AutomationRule
        {
            Name = "Test rule", FolderPath = input,
            OutputDirectory = Path.Combine(root, "output"),
            ArchiveDirectory = Path.Combine(root, "archive")
        };
    }

    private static string WriteInput(AutomationRule rule, string name)
    {
        var path = Path.Combine(rule.FolderPath, name);
        File.WriteAllText(path, "input bytes");
        return path;
    }

    private static string StorePath(string root) => Path.Combine(root, "automation.json");

    private static AutomationService CreateService(string root,
        Func<AutomationRule, string, CancellationToken, Task<AutomationExecutionResult>> execute,
        Func<DateTimeOffset>? clock = null) => new(new AutomationStore(StorePath(root)), execute,
            pollInterval: TimeSpan.FromHours(1), settleDelay: TimeSpan.FromMilliseconds(15), clock: clock);

    private static async Task<AutomationExecutionResult> SucceedAsync(
        AutomationRule rule, string input, CancellationToken token)
    {
        Directory.CreateDirectory(rule.OutputDirectory);
        var output = Path.Combine(rule.OutputDirectory, Path.GetFileNameWithoutExtension(input) + "." + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllTextAsync(output, "converted bytes", token);
        return new(true, output);
    }
}
