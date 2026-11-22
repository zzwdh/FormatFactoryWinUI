using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class ConversionResultSummaryTests
{
    [Fact]
    public async Task WorkflowMeasuresOriginalInputsAndAllCommittedOutputsWithoutCountingTemporaryOrDuplicateArtifacts()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "first.png");
        var second = Path.Combine(directory.Path, "second.png");
        var outputDirectory = Path.Combine(directory.Path, "pages");
        var firstOutput = Path.Combine(outputDirectory, "page-1.png");
        var secondOutput = Path.Combine(outputDirectory, "page-2.png");
        var temporaryOutput = Path.Combine(outputDirectory, "temporary.png");
        await File.WriteAllBytesAsync(first, new byte[200]);
        await File.WriteAllBytesAsync(second, new byte[300]);
        var request = new ConversionRequest
        {
            Inputs = [new(first, "png"), new(second, "png"), new(first, "png")], TargetFormatId = "pdf"
        };
        var result = await new ConversionWorkflow([new SummaryEngine(async _ =>
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllBytesAsync(firstOutput, new byte[40]);
            await File.WriteAllBytesAsync(secondOutput, new byte[60]);
            await File.WriteAllBytesAsync(temporaryOutput, new byte[5000]);
            File.Delete(first); // Source measurements belong to the conversion attempt, before execution.
            return new(true, false,
                [new("pages", "png", outputDirectory), new("page", "png", firstOutput),
                 new("duplicate", "png", outputDirectory), new("temporary", "png", temporaryOutput, true)], "Done");
        })]).ExecuteAsync(request, null, default);

        Assert.Equal(ConversionOutcome.Completed, result.Outcome);
        Assert.Equal(500, result.Statistics!.InputBytes);
        Assert.Equal(100, result.Statistics.OutputBytes);
        Assert.True(result.Statistics.ElapsedMilliseconds >= 0);
    }

    [Fact]
    public async Task UnavailableMeasurementsNeverFailAConversionOrInventZeroByteSizes()
    {
        var request = new ConversionRequest { Inputs = [new("missing.png", "png")], TargetFormatId = "pdf" };
        var result = await new ConversionWorkflow([new SummaryEngine(_ => Task.FromResult(
            new ConversionExecutionResult(true, false, [new("result", "pdf", "missing-output.pdf")], "Done")))])
            .ExecuteAsync(request, null, default);

        Assert.Equal(ConversionOutcome.Completed, result.Outcome);
        Assert.Null(result.Statistics!.InputBytes);
        Assert.Null(result.Statistics.OutputBytes);
    }

    [Fact]
    public async Task FailedConversionDoesNotCountPartialArtifactsAsSuccessfulOutput()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.png");
        await File.WriteAllBytesAsync(source, new byte[200]);
        var result = await new ConversionWorkflow([new SummaryEngine(_ => Task.FromResult(
            new ConversionExecutionResult(false, false, [new("partial", "png", source)], "Failed")))])
            .ExecuteAsync(new() { Inputs = [new(source, "png")], TargetFormatId = "pdf" }, null, default);

        Assert.Equal(ConversionOutcome.Failed, result.Outcome);
        Assert.Equal(200, result.Statistics!.InputBytes);
        Assert.Null(result.Statistics.OutputBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectoryArtifactDoesNotCountSharedUserFiles(bool sourceDirectory)
    {
        using var directory = new TemporaryDirectory();
        var shared = Path.Combine(directory.Path, "shared");
        Directory.CreateDirectory(shared);
        File.WriteAllBytes(Path.Combine(shared, "unrelated.bin"), new byte[300]);
        var request = new ConversionRequest
        {
            Inputs = [new(sourceDirectory ? Path.Combine(shared, "source.png") : Path.Combine(directory.Path, "source.png"), "png")],
            OutputDirectory = sourceDirectory ? null : shared,
            TargetFormatId = "png"
        };
        Assert.Null(ConversionResultMeasurement.MeasureOutputs([new("output", "png", shared)], request));
    }

    [Fact]
    public void MarkdownWithoutCompanionArtifactCannotClaimACompleteOutputSize()
    {
        using var directory = new TemporaryDirectory();
        var markdown = Path.Combine(directory.Path, "result.md");
        File.WriteAllText(markdown, "![image](assets/picture.png)");
        Assert.Null(ConversionResultMeasurement.MeasureOutputs([new("output", "md", markdown)], new()));
    }

    [Fact]
    public async Task HistoryKeepsMeasuredValuesAfterFilesMoveAndLegacyRecordsRemainReadable()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "result.pdf");
        await File.WriteAllBytesAsync(output, new byte[100]);
        var historyPath = Path.Combine(directory.Path, "history.json");
        var store = new ConversionHistoryStore(historyPath);
        var task = new MediaTask
        {
            InputPath = "source.png", OutputPath = output, State = ConversionState.Completed,
            ResultStatistics = new(500, 100, 41_000)
        };
        await store.RecordCompletedAsync(task);
        File.Delete(output);

        var restored = Assert.Single(await new ConversionHistoryStore(historyPath).LoadAsync());
        Assert.Equal(task.ResultStatistics, restored.ResultStatistics);
        Assert.NotEmpty(HistoryPageViewModel.DescribeResult(restored));

        await File.WriteAllTextAsync(historyPath, "[{\"DisplayName\":\"legacy\",\"OutputPath\":\"moved.pdf\"}]");
        var legacy = Assert.Single(await new ConversionHistoryStore(historyPath).LoadAsync());
        Assert.Null(legacy.ResultStatistics);
        Assert.Empty(HistoryPageViewModel.DescribeResult(legacy));
    }

    [Fact]
    public async Task RetainedCompletedOutputPersistsStatisticsForQueueRestore()
    {
        using var directory = new TemporaryDirectory();
        var task = new MediaTask
        {
            InputPath = Path.Combine(directory.Path, "source.png"), SourceFormatId = "png", TargetFormatId = "pdf",
            OutputGroupId = Guid.NewGuid(), OutputConfigurationId = Guid.NewGuid(), OutputExpectedCount = 2,
            State = ConversionState.Completed, ResultStatistics = new(500, 100, 41_000)
        };
        var store = new QueueStore(Path.Combine(directory.Path, "queue.json"), () => []);
        await store.SaveAsync([task]);
        var persisted = Assert.Single(await store.LoadAsync());
        Assert.Equal(task.ResultStatistics, persisted.ResultStatistics);
        Assert.Equal(task.ResultStatistics, QueueSession.FromPersisted(persisted).ResultStatistics);
    }

    [Fact]
    public void CompletedSummaryUpdatesFromCapturedDataAndDisappearsDuringRetry()
    {
        WithEnglish(() =>
        {
            var task = new MediaTask { State = ConversionState.Completed, ResultStatistics = new(1000, 280, 41_000) };
            using var view = new MediaTaskViewModel(task);
            Assert.Contains("1000 B → 280 B", view.ResultSummary);
            Assert.Contains("72% smaller", view.ResultSummary);
            Assert.Contains("00:00:41", view.ResultSummary);
            var changes = new List<string?>();
            view.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            task.ResultStatistics = new(1000, 1250, 500);
            Assert.Contains("25% larger", view.ResultSummary);
            Assert.Contains(nameof(MediaTaskViewModel.ResultSummary), changes);
            task.State = ConversionState.Running;
            Assert.False(view.HasResultSummary);
            Assert.Empty(view.ResultSummary);
            Assert.Contains(nameof(MediaTaskViewModel.HasResultSummary), changes);
        });
    }

    [Fact]
    public void ZeroAndUnknownSizesNeverShowInvalidPercentagesAndLongDurationsDoNotWrap()
    {
        WithEnglish(() =>
        {
            Assert.DoesNotContain("%", ConversionResultSummaryFormatter.Format(new(0, 10, 0)));
            Assert.Contains("Size unchanged", ConversionResultSummaryFormatter.Format(new(0, 0, 0)));
            Assert.Contains("— → 10 B", ConversionResultSummaryFormatter.Format(new(null, 10, 0)));
            Assert.Contains("25:00:00", ConversionResultSummaryFormatter.Format(new(100, 50, 90_000_000)));
            Assert.Contains("2562047788015:12:55", ConversionResultSummaryFormatter.Format(new(long.MaxValue, 0, long.MaxValue)));
            Assert.Contains("— → —", ConversionResultSummaryFormatter.Format(new(-1, -1, -1)));
            Assert.Empty(ConversionResultSummaryFormatter.Format(null));
        });
    }

    [Theory]
    [InlineData("InputBytes", -1)]
    [InlineData("OutputBytes", -1)]
    [InlineData("ElapsedMilliseconds", -1)]
    public async Task InvalidPersistedMeasurementsDoNotRenderAsValidHistory(string field, long value)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.json");
        await File.WriteAllTextAsync(path, "[{\"ResultStatistics\":{\"" + field + "\":" + value + "}}]");
        Assert.Empty(await new ConversionHistoryStore(path).LoadAsync());
    }

    [Fact]
    public void BatchCountsSharedInputOnceAndDoesNotClaimSavingsForPartialGroups()
    {
        WithEnglish(() =>
        {
            var group = Guid.NewGuid();
            var video = new MediaTask { OutputGroupId = group, OutputExpectedCount = 2, State = ConversionState.Completed,
                ResultStatistics = new(1000, 800, 10) };
            var audio = new MediaTask { OutputGroupId = group, OutputExpectedCount = 2, State = ConversionState.Completed,
                ResultStatistics = new(1000, 700, 20) };
            var failed = new MediaTask { State = ConversionState.Failed };
            var cancelled = new MediaTask { State = ConversionState.Cancelled };
            var summary = ConversionResultSummaryFormatter.FormatBatch([video, audio, failed, cancelled]);
            Assert.Contains("2 completed · 1 failed · 1 cancelled", summary);
            Assert.Contains("50% larger", summary);
            Assert.DoesNotContain("smaller", summary);
            audio.State = ConversionState.Failed;
            Assert.DoesNotContain("→", ConversionResultSummaryFormatter.FormatBatch([video, audio]));
            Assert.Empty(ConversionResultSummaryFormatter.FormatBatch([new MediaTask()]));
        });
    }

    [Theory]
    [InlineData("zh-Hans", "减少", "耗时")]
    [InlineData("de-DE", "kleiner", "Dauer")]
    [InlineData("he-IL", "קטן", "משך")]
    public void SummaryUsesSelectedLanguage(string language, string reduction, string elapsed)
    {
        var previous = LocalizationService.CurrentLanguageCode;
        try
        {
            LocalizationService.SetLanguage(language);
            var summary = ConversionResultSummaryFormatter.Format(new(100, 50, 41_000));
            Assert.Contains(reduction, summary);
            Assert.Contains(elapsed, summary);
        }
        finally { LocalizationService.SetLanguage(previous); }
    }

    private static void WithEnglish(Action action)
    {
        var previous = LocalizationService.CurrentLanguageCode;
        try { LocalizationService.SetLanguage("en-US"); action(); }
        finally { LocalizationService.SetLanguage(previous); }
    }

    private sealed class SummaryEngine(Func<ConversionRequest, Task<ConversionExecutionResult>> execute) : IConversionEngine
    {
        public string EngineId => "summary-test";
        public bool CanHandle(string sourceFormatId, string targetFormatId) => true;
        public Task<ConversionExecutionResult> ConvertAsync(ConversionRequest request,
            IProgress<ConversionExecutionProgress>? progress, CancellationToken cancellationToken) => execute(request);
    }
}
