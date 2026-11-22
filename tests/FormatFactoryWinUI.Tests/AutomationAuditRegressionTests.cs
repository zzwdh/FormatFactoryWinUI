using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Abstractions;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationAuditRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MovedSourceWithFailedFinalCheckpoint_IsRecoveredFromDurableIntentAfterRestart()
    {
        using var fixture = new Fixture();
        var failFinal = true;
        var store = new AutomationStore(fixture.StorePath, snapshot =>
        {
            if (failFinal && snapshot.Records.Any(record => record.ArchivedPath is not null))
            { failFinal = false; throw new IOException("injected final archive checkpoint failure"); }
        });
        await using (var initial = fixture.Service(store))
        {
            await initial.ConfigureAsync([fixture.Rule]);
            var result = await initial.CompleteExecutionAsync(fixture.Rule, fixture.Input, fixture.Fingerprint, fixture.Result);
            Assert.True(result.Success);
            Assert.True(result.ArchivePending);
        }
        Assert.False(File.Exists(fixture.Input));
        var interrupted = Assert.Single((await new AutomationStore(fixture.StorePath).LoadAsync()).Records);
        Assert.Null(interrupted.ArchivedPath);
        Assert.NotNull(interrupted.ArchiveReceipt);
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(interrupted.ArchiveReceipt.DestinationPath));

        await using var restarted = fixture.Service(new AutomationStore(fixture.StorePath));
        await restarted.StartAsync();
        var resumed = await restarted.CompleteExecutionAsync(fixture.Rule, fixture.Input, fixture.Fingerprint,
            fixture.Result with { ArchivePending = true });
        Assert.False(resumed.ArchivePending);
        var recovered = Assert.Single((await new AutomationStore(fixture.StorePath).LoadAsync()).Records);
        Assert.Equal(interrupted.ArchiveReceipt.DestinationPath, recovered.ArchivedPath);
        Assert.Single(Directory.EnumerateFiles(fixture.Rule.ArchiveDirectory));
    }

    [Fact]
    public async Task CancellationAfterMove_DoesNotCancelFinalArchiveCheckpoint()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var store = new AutomationStore(fixture.StorePath, snapshot =>
        {
            if (snapshot.Records.Any(record => record.ArchivedPath is not null)) cancellation.Cancel();
        });
        await using var service = fixture.Service(store);
        await service.ConfigureAsync([fixture.Rule]);
        var result = await service.CompleteExecutionAsync(fixture.Rule, fixture.Input, fixture.Fingerprint,
            fixture.Result, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Success);
        Assert.False(result.ArchivePending);
        var saved = Assert.Single((await new AutomationStore(fixture.StorePath).LoadAsync()).Records);
        Assert.NotNull(saved.ArchivedPath);
        Assert.False(File.Exists(fixture.Input));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(saved.ArchivedPath));
    }

    [Fact]
    public async Task RecoveryDoesNotClaimWrongContentAtIntendedArchivePath()
    {
        using var fixture = new Fixture();
        var store = new AutomationStore(fixture.StorePath);
        await store.SaveRulesAsync([fixture.Rule]);
        var candidate = Path.Combine(fixture.Rule.ArchiveDirectory, "input.mp4");
        Directory.CreateDirectory(fixture.Rule.ArchiveDirectory);
        var receipt = new AutomationArchiveReceipt(candidate, Convert.ToHexString(SHA256.HashData(fixture.Content)));
        await store.RecordSuccessAsync(fixture.Record with { ArchiveReceipt = receipt });
        File.Move(fixture.Input, candidate);
        var different = fixture.Content.Select(value => (byte)(value ^ 0xff)).ToArray();
        await File.WriteAllBytesAsync(candidate, different);

        await using var restarted = fixture.Service(new AutomationStore(fixture.StorePath));
        await restarted.StartAsync();
        var result = await restarted.CompleteExecutionAsync(fixture.Rule, fixture.Input, fixture.Fingerprint,
            fixture.Result with { ArchivePending = true });
        Assert.True(result.ArchivePending);
        Assert.Null(Assert.Single((await store.LoadAsync()).Records).ArchivedPath);
        Assert.Equal(different, await File.ReadAllBytesAsync(candidate));
    }

    [Fact]
    public async Task LegacyTerminalArchiveWithDifferentCreationTime_RemainsComplete()
    {
        using var fixture = new Fixture();
        var store = new AutomationStore(fixture.StorePath);
        await store.SaveRulesAsync([fixture.Rule]);
        var archived = Path.Combine(fixture.Rule.ArchiveDirectory, "legacy.mp4");
        Directory.CreateDirectory(fixture.Rule.ArchiveDirectory);
        File.Move(fixture.Input, archived);
        // Older cross-volume moves may have changed destination creation time without storing it.
        var original = fixture.Fingerprint with { CreationTimeUtcTicks = fixture.Fingerprint.CreationTimeUtcTicks - 1 };
        Assert.NotEqual(original, FileFingerprintService.Read(archived));
        await store.RecordSuccessAsync(fixture.Record with { Fingerprint = original, ArchivedPath = archived });
        await using var service = fixture.Service(store);
        await service.StartAsync();
        var result = await service.CompleteExecutionAsync(fixture.Rule, fixture.Input, original,
            fixture.Result with { ArchivePending = true });
        Assert.False(result.ArchivePending);
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(archived));
    }

    [Fact]
    public async Task SameSizeAndTimestampCorruption_IsRejectedEvenAfterCacheWasPrimed()
    {
        using var fixture = new Fixture();
        var store = new AutomationStore(fixture.StorePath);
        await store.SaveRulesAsync([fixture.Rule]);
        var candidate = Path.Combine(fixture.Rule.ArchiveDirectory, "input.mp4");
        await store.RecordSuccessAsync(fixture.Record with
        { ArchiveReceipt = new(candidate, Convert.ToHexString(SHA256.HashData(fixture.Content))) });
        await store.LoadAsync();
        var previousTime = File.GetLastWriteTimeUtc(fixture.StorePath);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.StorePath))!;
        json["Records"]![0]!["ArchiveReceipt"]!["Sha256"] = new string('x', 64);
        var damaged = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(fixture.StorePath, damaged);
        File.SetLastWriteTimeUtc(fixture.StorePath, previousTime);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRulesAsync([fixture.Rule]));
        Assert.Equal(damaged, await File.ReadAllTextAsync(fixture.StorePath));
    }

    [Fact]
    public async Task RepeatedCheckpointsReuseValidatedSnapshotAndSkipUnchangedWrites()
    {
        using var fixture = new Fixture();
        var store = new AutomationStore(fixture.StorePath);
        await store.SaveRulesAsync([fixture.Rule]);
        await store.RecordSuccessAsync(fixture.Record);
        await store.MarkScheduledRunAsync(fixture.Rule.Id, "2026-11-12");
        var initialWrites = store.WriteCount;
        var initialReads = store.DeserializationCount;
        for (var i = 0; i < 10; i++)
        {
            await store.SaveRulesAsync([fixture.Rule]);
            await store.RecordSuccessAsync(fixture.Record);
            await store.MarkScheduledRunAsync(fixture.Rule.Id, "2026-11-12");
            await store.ResetRetryProgressAsync(fixture.Input, fixture.Fingerprint);
            await store.LoadAsync();
        }
        Assert.Equal(initialWrites, store.WriteCount);
        Assert.Equal(initialReads, store.DeserializationCount);
        output.WriteLine($"Automation unchanged checkpoints: operations=50, additional_deserializations={store.DeserializationCount - initialReads}, additional_writes={store.WriteCount - initialWrites}; file content is still verified on reads.");
    }

    [Theory]
    [InlineData("relative.mp4", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("absolute", "bad-hash")]
    public async Task InvalidArchiveIntent_IsRejectedBeforePersistence(string destination, string digest)
    {
        using var fixture = new Fixture();
        var store = new AutomationStore(fixture.StorePath);
        if (destination == "absolute") destination = Path.Combine(fixture.Rule.ArchiveDirectory, "input.mp4");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RecordSuccessAsync(fixture.Record with
        { ArchiveReceipt = new(destination, digest) }));
        Assert.False(File.Exists(fixture.StorePath));
    }

    [Fact]
    public void HistoryIndexPreservesOldSourceVersionsAndSharedOutputReferences()
    {
        using var fixture = new Fixture();
        var first = fixture.Record;
        var second = first with { InputPath = Path.Combine(fixture.Rule.FolderPath, "second.mp4") };
        var index = new AutomationHistoryIndex([first, second]);
        Assert.True(index.IsExcluded(first.OutputPath!, first.OutputFingerprint));
        var newer = first with { Fingerprint = first.Fingerprint with { LastWriteTimeUtcTicks = first.Fingerprint.LastWriteTimeUtcTicks + 1 },
            OutputPath = Path.Combine(fixture.Rule.OutputDirectory, "new.mp4") };
        index.Remember(newer);
        Assert.True(index.IsKnownSource(first.InputPath, first.Fingerprint, allowFailedRetry: true));
        Assert.True(index.IsKnownSource(first.InputPath, newer.Fingerprint, allowFailedRetry: true));
        index.Remember(first with { OutputPath = newer.OutputPath });
        Assert.True(index.IsExcluded(second.OutputPath!, second.OutputFingerprint));
        index.Remember(second with { OutputPath = newer.OutputPath });
        Assert.False(index.IsExcluded(second.OutputPath!, second.OutputFingerprint));
    }

    [Fact]
    public async Task HistoryLookupBenchmark_IndexedResultsMatchPreviousScan()
    {
        using var fixture = new Fixture();
        const int historyCount = 10_000;
        const int fileCount = 1000;
        var records = Enumerable.Range(0, historyCount).Select(i => fixture.Record with
        { InputPath = Path.Combine(fixture.Rule.FolderPath, $"source-{i}.mp4"), OutputPath = Path.Combine(fixture.Rule.OutputDirectory, $"output-{i}.mp4") }).ToArray();
        var files = Enumerable.Range(0, fileCount).Select(i => Path.Combine(fixture.Rule.OutputDirectory,
            i % 4 == 0 ? $"output-{historyCount - 1 - i}.mp4" : $"missing-{i}.mp4")).ToArray();
        var timer = Stopwatch.StartNew();
        var previous = files.Select(path => records.Any(record =>
            path.Equals(record.OutputPath, StringComparison.OrdinalIgnoreCase) && fixture.Record.OutputFingerprint == record.OutputFingerprint)).ToArray();
        var previousMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var index = new AutomationHistoryIndex(records);
        var buildMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var indexed = files.Select(path => index.IsExcluded(path, fixture.Record.OutputFingerprint)).ToArray();
        var indexedMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(previous, indexed);
        Assert.Equal(250, indexed.Count(value => value));
        var report = JsonSerializer.Serialize(new { historyCount, fileCount, previousMs, buildMs, indexedMs,
            previousComparisonUpperBound = historyCount * fileCount, indexedLookups = fileCount }, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_PERFORMANCE_REPORT_DIR") is { Length: > 0 } root)
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "automation-history-lookup.json"), report);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        public AutomationRule Rule { get; }
        public string Input { get; }
        public string StorePath { get; }
        public byte[] Content { get; } = [1, 3, 5, 7, 9, 11];
        public AutomationFileFingerprint Fingerprint { get; }
        public AutomationProcessedFile Record { get; }
        public AutomationExecutionResult Result { get; }
        public Fixture()
        {
            Rule = new() { Name = "audit", FolderPath = Path.Combine(_directory.Path, "input"),
                OutputDirectory = Path.Combine(_directory.Path, "output"), ArchiveDirectory = Path.Combine(_directory.Path, "archive"),
                ArchiveSource = true, WatchEnabled = false };
            Directory.CreateDirectory(Rule.FolderPath);
            Directory.CreateDirectory(Rule.OutputDirectory);
            Input = Path.Combine(Rule.FolderPath, "input.mp4");
            File.WriteAllBytes(Input, Content);
            Fingerprint = FileFingerprintService.Read(Input)!;
            var converted = Path.Combine(Rule.OutputDirectory, "converted.mp4");
            File.WriteAllBytes(converted, [2, 4, 6]);
            Result = new(true, converted);
            Record = new(Rule.Id, Input, Fingerprint, converted, FileFingerprintService.Read(converted), DateTimeOffset.UtcNow);
            StorePath = Path.Combine(_directory.Path, "automation.json");
        }
        public AutomationService Service(AutomationStore store) => new(store,
            (_, _, _) => throw new InvalidOperationException("Checkpoint recovery must not encode again."), TimeSpan.FromHours(1), TimeSpan.Zero);
        public void Dispose() => _directory.Dispose();
    }
}
