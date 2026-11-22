using System.Text.Json;
using System.Text.Json.Nodes;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationMultiOutputTests
{
    [Fact]
    public async Task LegacyRulesAndPresets_MigrateWithoutRewritingOnRead()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        rule.Options["profile"] = "keep-legacy-option";
        rule.Options["media.targetSizeBytes"] = "1000000";
        var automation = Path.Combine(directory.Path, "automation.json");
        var legacy = JsonSerializer.Serialize(new AutomationSnapshot { Version = 1, Rules = [rule] });
        await File.WriteAllTextAsync(automation, legacy);

        var store = new AutomationStore(automation);
        var loaded = await store.LoadAsync();
        Assert.Equal(2, loaded.Version);
        var output = Assert.Single(loaded.Rules[0].Outputs);
        Assert.Equal("mp4", output.TargetFormatId);
        Assert.Equal("keep-legacy-option", output.Options["profile"]);
        Assert.Equal("1000000", output.Options["media.targetSizeBytes"]);
        Assert.Equal(legacy, await File.ReadAllTextAsync(automation));
        await store.SaveRulesAsync(loaded.Rules);
        Assert.Equal(2, JsonDocument.Parse(await File.ReadAllTextAsync(automation)).RootElement.GetProperty("Version").GetInt32());

        var presets = Path.Combine(directory.Path, "presets.json");
        var preset = new AutomationConversionPreset
        {
            Name = "Previous", TargetFormatId = "mp3", ConversionMode = "quality",
            Options = new(StringComparer.OrdinalIgnoreCase) { ["selectedAudioTrack"] = "2" }
        };
        var presetBytes = JsonSerializer.Serialize(new { Version = 1, Presets = new[] { preset } });
        await File.WriteAllTextAsync(presets, presetBytes);
        var presetStore = new AutomationPresetStore(presets);
        var restored = Assert.Single(await presetStore.LoadAsync());
        Assert.Equal(2, Assert.Single(restored.Outputs).SelectedAudioTrack);
        Assert.Equal(presetBytes, await File.ReadAllTextAsync(presets));
        await presetStore.SaveAsync([restored]);
        Assert.Equal(2, JsonDocument.Parse(await File.ReadAllTextAsync(presets)).RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public async Task CombinationPreset_CopiesEveryOutputAndDropsExecutionMetadata()
    {
        using var directory = new TemporaryDirectory();
        var first = new OutputConfiguration
        {
            Label = "Share", TargetFormatId = "mp4", Options = new(StringComparer.OrdinalIgnoreCase)
            {
                ["media.targetSizeBytes"] = "10000000", ["media.maxWidth"] = "1280",
                ["automation.ruleSnapshot"] = "private rule", ["password"] = "private password"
            }
        };
        var second = new OutputConfiguration
        {
            Label = "Audio", TargetFormatId = "mp3", SelectedAudioTrack = 2,
            Options = new(StringComparer.OrdinalIgnoreCase) { ["media.audioBitrateKbps"] = "192", ["media.channels"] = "2" }
        };
        var preset = AutomationConversionPreset.FromOutputs([first, second], "Video and audio");
        var store = new AutomationPresetStore(Path.Combine(directory.Path, "presets.json"));
        await store.SaveAsync([preset]);
        first.Options["media.maxWidth"] = "640";
        second.Label = "Changed";
        var restored = Assert.Single(await store.LoadAsync());

        Assert.Equal(2, restored.Outputs.Count);
        Assert.Equal("1280", restored.Outputs[0].Options["media.maxWidth"]);
        Assert.Equal("Audio", restored.Outputs[1].Label);
        Assert.Equal(2, restored.Outputs[1].SelectedAudioTrack);
        Assert.DoesNotContain("private", await File.ReadAllTextAsync(Path.Combine(directory.Path, "presets.json")));
        restored.Outputs[0].Options["media.maxWidth"] = "320";
        Assert.Equal("1280", (await store.LoadAsync())[0].Outputs[0].Options["media.maxWidth"]);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("TargetFormatId")]
    [InlineData("ConversionMode")]
    [InlineData("Options")]
    public async Task DamagedV2Output_IsRejectedWithoutOverwritingStoredBytes(string missingProperty)
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        rule.Outputs = [new() { TargetFormatId = "mp4" }];
        var json = JsonNode.Parse(JsonSerializer.Serialize(new AutomationSnapshot { Rules = [rule] }))!;
        json["Rules"]![0]!["Outputs"]![0]!.AsObject().Remove(missingProperty);
        var bytes = json.ToJsonString();
        var path = Path.Combine(directory.Path, "automation.json");
        await File.WriteAllTextAsync(path, bytes);
        var store = new AutomationStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRulesAsync([rule]));
        Assert.Equal(bytes, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task GroupCompletion_RequiresEveryOutputBeforeRecordingOrArchiving()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "movie.mp4");
        var video = Path.Combine(directory.Path, "output", "share.mp4");
        var audio = Path.Combine(directory.Path, "output", "audio.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(video, "video");
        var rule = Rule(directory.Path);
        rule.ArchiveSource = true;
        rule.ArchiveDirectory = Path.Combine(directory.Path, "archive");
        rule.Outputs = [new() { TargetFormatId = "mp4" }, new() { TargetFormatId = "mp3" }];
        var store = new AutomationStore(Path.Combine(directory.Path, "automation.json"));
        await using var service = new AutomationService(store, (_, _, _) => Task.FromResult(new AutomationExecutionResult(false)));
        await service.ConfigureAsync([rule]);
        var fingerprint = FileFingerprintService.Read(source)!;
        var result = new AutomationExecutionResult(true, video, OutputPaths: [video, audio]);

        Assert.False((await service.CompleteExecutionAsync(rule, source, fingerprint, result)).Success);
        Assert.True(File.Exists(source));
        Assert.Empty((await store.LoadAsync()).Records);
        await File.WriteAllTextAsync(audio, "audio");
        Assert.True((await service.CompleteExecutionAsync(rule, source, fingerprint, result)).Success);
        Assert.False(File.Exists(source));
        var receipt = Assert.Single((await store.LoadAsync()).Records);
        Assert.Equal([video, audio], receipt.Outputs!.Select(output => output.OutputPath));
        Assert.True(File.Exists(receipt.ArchivedPath));
        Assert.True((await service.CompleteExecutionAsync(rule, source, fingerprint, result)).Success);
        Assert.Single((await store.LoadAsync()).Records);
        Assert.Single(Directory.GetFiles(rule.ArchiveDirectory));
    }

    [Fact]
    public async Task Preview_ExpandsEveryConfiguredOutputWithIndependentNames()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllTextAsync(input, "source");
        var rule = Rule(directory.Path);
        rule.Outputs = [new() { Label = "Share", TargetFormatId = "mp4" }, new() { Label = "Audio", TargetFormatId = "mp3" }];
        var result = await AutomationPreviewService.PreviewAsync(rule, [rule], new(), new());

        Assert.Equal(2, result.Ready);
        Assert.All(result.Items, item => Assert.Equal(input, item.InputPath));
        Assert.Equal(["Share", "Audio"], result.Items.Select(item => item.OutputLabel));
        Assert.EndsWith(".mp4", result.Items[0].OutputPath);
        Assert.EndsWith(".mp3", result.Items[1].OutputPath);
        Assert.NotEqual(result.Items[0].OutputPath, result.Items[1].OutputPath);
        Assert.False(Directory.Exists(rule.OutputDirectory));
    }

    [Fact]
    public void RuleSnapshotsAndConversionComparison_IncludeEveryOutput()
    {
        var rule = new AutomationRule { Outputs = [new() { TargetFormatId = "mp4" }, new() { TargetFormatId = "mp3" }] };
        rule.Outputs[1].Options["media.audioBitrateKbps"] = "192";
        var copy = rule.Snapshot();
        Assert.True(AutomationService.SameConversion(rule, copy));
        copy.Outputs[1].Options["media.audioBitrateKbps"] = "128";
        Assert.False(AutomationService.SameConversion(rule, copy));
        Assert.Equal("192", rule.Outputs[1].Options["media.audioBitrateKbps"]);
    }

    [Fact]
    public void HistoryReplay_PreservesSafeCustomParametersAndOutputLabel()
    {
        var task = new MediaTask
        {
            InputPath = "source.mp4", TargetFormatId = "mp4", OutputLabel = "Share",
            OutputGroupId = Guid.NewGuid(), OutputConfigurationId = Guid.NewGuid()
        };
        task.Options["media.targetSizeBytes"] = "10000000";
        task.Options["media.maxWidth"] = "1280";
        task.Options["password"] = "secret";
        task.Options["automation.ruleSnapshot"] = "secret";
        var replay = HistoryReplayService.CreateQueueItem(ConversionHistoryItem.FromTask(task));

        Assert.Equal("Share", replay.OutputLabel);
        Assert.Equal("10000000", replay.Options["media.targetSizeBytes"]);
        Assert.Equal("1280", replay.Options["media.maxWidth"]);
        Assert.DoesNotContain("password", replay.Options.Keys);
        Assert.DoesNotContain("automation.ruleSnapshot", replay.Options.Keys);
        Assert.Null(replay.OutputGroupId);
    }

    [Fact]
    public async Task MixedFailures_RetryOnlyTemporaryOutputsAndManualRunCanRetryPermanentOutputs()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path) with
        {
            RetryCount = 2, RetryDelaySeconds = 1,
            Outputs = [new() { TargetFormatId = "mp4" }, new() { TargetFormatId = "mp3" }]
        };
        var source = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllTextAsync(source, "source");
        Directory.CreateDirectory(rule.OutputDirectory);
        var permanent = new MediaTask { InputPath = source, State = ConversionState.Waiting };
        var temporary = new MediaTask { InputPath = source, State = ConversionState.Waiting };
        var permanentAttempts = 0;
        var temporaryAttempts = 0;
        var permanentCanSucceed = false;
        var forceFlags = new List<bool>();
        var clockStep = 0;
        var store = new AutomationStore(Path.Combine(directory.Path, "automation.json"));
        await using var service = new AutomationService(store, async (captured, _, token) =>
        {
            forceFlags.Add(captured.ForceRetryFailedOutputs);
            var results = new List<AutomationExecutionResult>();
            foreach (var task in new[] { permanent, temporary })
            {
                if (task.State == ConversionState.Completed)
                {
                    results.Add(new(true, task.OutputPath));
                    continue;
                }
                if (!AutomationOutputExecutionPolicy.ShouldExecute(task, captured))
                {
                    results.Add(new(false, Error: task.LastError));
                    continue;
                }
                var isTemporary = ReferenceEquals(task, temporary);
                var attempt = isTemporary ? ++temporaryAttempts : ++permanentAttempts;
                var success = isTemporary ? attempt >= 3 : permanentCanSucceed;
                var result = new AutomationExecutionResult(success,
                    success ? Path.Combine(rule.OutputDirectory, isTemporary ? "audio.mp3" : "video.mp4") : null,
                    success ? null : isTemporary ? "temporary sharing violation" : "unsupported video encoder",
                    Retryable: !success && isTemporary);
                task.State = success ? ConversionState.Completed : ConversionState.Failed;
                task.LastError = result.Error ?? string.Empty;
                if (success)
                {
                    task.OutputPath = result.OutputPath!;
                    await File.WriteAllTextAsync(task.OutputPath, "converted", token);
                }
                else task.Options[AutomationOutputExecutionPolicy.RetryableOption] = result.Retryable.ToString();
                results.Add(result);
            }
            return AutomationOutputExecutionPolicy.Aggregate(results);
        }, settleDelay: TimeSpan.Zero,
            clock: () => DateTimeOffset.UtcNow.AddSeconds(10 * Interlocked.Increment(ref clockStep)));
        await service.ConfigureAsync([rule]);

        var failed = await service.RunNowAsync(rule.Id);

        Assert.Equal(1, failed.Failed);
        Assert.Equal(1, permanentAttempts);
        Assert.Equal(3, temporaryAttempts);
        Assert.Equal([true, false, false], forceFlags);
        Assert.Equal(ConversionState.Completed, temporary.State);
        Assert.True(File.Exists(temporary.OutputPath));
        Assert.Empty((await store.LoadAsync()).Records);
        Assert.Single((await store.LoadAsync()).Suppressions);

        permanentCanSucceed = true;
        var succeeded = await service.RunNowAsync(rule.Id);

        Assert.Equal(1, succeeded.Succeeded);
        Assert.Equal(2, permanentAttempts);
        Assert.Equal(3, temporaryAttempts); // The previous successful audio result is reused.
        Assert.True(forceFlags[^1]);
        Assert.Equal(2, Assert.Single((await store.LoadAsync()).Records).Outputs!.Count);
        Assert.Empty((await store.LoadAsync()).Suppressions);
        Assert.DoesNotContain("ForceRetryFailedOutputs", await File.ReadAllTextAsync(Path.Combine(directory.Path, "automation.json")));
    }

    [Fact]
    public void RetryPolicy_CancelledOutputPreventsRetryAndLegacyFailuresRemainAttemptable()
    {
        var task = new MediaTask { State = ConversionState.Failed };
        var rule = new AutomationRule();
        Assert.True(AutomationOutputExecutionPolicy.ShouldExecute(task, rule));
        task.Options[AutomationOutputExecutionPolicy.RetryableOption] = "False";
        Assert.False(AutomationOutputExecutionPolicy.ShouldExecute(task, rule));
        Assert.True(AutomationOutputExecutionPolicy.ShouldExecute(task, rule with { ForceRetryFailedOutputs = true }));
        var aggregate = AutomationOutputExecutionPolicy.Aggregate([
            new(false, Error: "sharing violation", Retryable: true), new(false, Cancelled: true)
        ]);
        Assert.True(aggregate.Cancelled);
        Assert.False(aggregate.Retryable);
    }

    [Fact]
    public async Task ArchiveFailure_KeepsSuccessfulReceiptAndRetriesArchiveWithoutChangingCompletionTime()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        rule.ArchiveSource = true;
        rule.ArchiveDirectory = Path.Combine(directory.Path, "archive");
        var source = Path.Combine(directory.Path, "movie.mp4");
        var output = Path.Combine(directory.Path, "movie-result.mp4");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(output, "result");
        await File.WriteAllTextAsync(rule.ArchiveDirectory, "a file prevents creation of the archive directory");
        var fingerprint = FileFingerprintService.Read(source)!;
        var store = new AutomationStore(Path.Combine(directory.Path, "automation.json"));
        var pendingQueue = new QueueStore(Path.Combine(directory.Path, "pending-archive-queue.json"), () => []);
        var pendingTask = new MediaTask
        {
            InputPath = source, SourceFormatId = "mp4", TargetFormatId = "mp4", OutputPath = output,
            OutputGroupId = Guid.NewGuid(), OutputConfigurationId = Guid.NewGuid(), OutputExpectedCount = 1,
            State = ConversionState.Completed, OutputHistoryRecorded = true,
            OutputFingerprint = FileFingerprintService.Read(output)
        };
        pendingTask.Options["automation.ruleSnapshot"] = JsonSerializer.Serialize(rule);
        pendingTask.Options["automation.fingerprint"] = JsonSerializer.Serialize(fingerprint);
        DateTimeOffset completed;
        await using (var service = new AutomationService(store, (_, _, _) => throw new InvalidOperationException("Archive retries must not reconvert.")))
        {
            await service.ConfigureAsync([rule]);
            var result = await service.CompleteExecutionAsync(rule, source, fingerprint, new(true, output, OutputPaths: [output]));
            Assert.True(result.Success);
            Assert.True(result.ArchivePending);
            Assert.NotEmpty(result.Error!);
            Assert.True(File.Exists(source));
            var receipt = Assert.Single((await store.LoadAsync()).Records);
            Assert.Null(receipt.ArchivedPath);
            Assert.Single(receipt.Outputs!);
            completed = receipt.CompletedAtUtc;
            await pendingQueue.SaveAsync([pendingTask]);
            var durableGroup = Assert.Single(await pendingQueue.LoadAsync());
            Assert.Equal(ConversionState.Completed, durableGroup.State);
            Assert.Equal(pendingTask.OutputGroupId, durableGroup.OutputGroupId);
            Assert.False(durableGroup.OutputGroupFinalized);
            Assert.True(JsonSerializer.Deserialize<AutomationRule>(durableGroup.Options["automation.ruleSnapshot"])!.ArchiveSource);
        }

        File.Delete(rule.ArchiveDirectory);
        await using var restarted = new AutomationService(store, (_, _, _) => throw new InvalidOperationException("Archive retries must not reconvert."));
        await restarted.StartAsync();
        var retry = await restarted.CompleteExecutionAsync(rule, source, fingerprint, new(true, output, OutputPaths: [output]));
        Assert.True(retry.Success);
        Assert.False(retry.ArchivePending);
        Assert.False(File.Exists(source));
        var archived = Assert.Single((await store.LoadAsync()).Records);
        Assert.Equal(completed, archived.CompletedAtUtc);
        Assert.True(File.Exists(archived.ArchivedPath));
        pendingTask.OutputGroupFinalized = true;
        await pendingQueue.SaveAsync([pendingTask]);
        Assert.Empty(await pendingQueue.LoadAsync());
    }

    [Fact]
    public async Task SourceChangedDuringCompletion_PreservesPendingArchiveAndSuccessfulOutput()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        rule.ArchiveSource = true;
        rule.ArchiveDirectory = Path.Combine(directory.Path, "archive");
        var source = Path.Combine(directory.Path, "movie.mp4");
        var output = Path.Combine(directory.Path, "movie-result.mp4");
        await File.WriteAllTextAsync(source, "original");
        var fingerprint = FileFingerprintService.Read(source)!;
        await File.WriteAllTextAsync(source, "a changed source version");
        await File.WriteAllTextAsync(output, "result for the original version");
        var store = new AutomationStore(Path.Combine(directory.Path, "automation.json"));
        await using var service = new AutomationService(store, (_, _, _) => Task.FromResult(new AutomationExecutionResult(false)));
        await service.ConfigureAsync([rule]);

        var result = await service.CompleteExecutionAsync(rule, source, fingerprint, new(true, output, OutputPaths: [output]));

        Assert.True(result.Success);
        Assert.True(result.ArchivePending);
        Assert.Equal(LocalizationService.T("AutomationStateSourceChanged"), result.Error);
        Assert.Equal("a changed source version", await File.ReadAllTextAsync(source));
        Assert.Equal(fingerprint, Assert.Single((await store.LoadAsync()).Records).Fingerprint);
        Assert.Null((await store.LoadAsync()).Records[0].ArchivedPath);
    }

    private static AutomationRule Rule(string source) => new()
    {
        Name = "Outputs", FolderPath = source, OutputDirectory = Path.Combine(source, "output"),
        InputKind = ContentKind.Video, WatchEnabled = false
    };
}
