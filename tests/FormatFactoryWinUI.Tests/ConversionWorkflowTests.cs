using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ConversionEngines;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class ConversionWorkflowTests
{
    [Fact]
    public async Task RealDiscPipelineRoundTripsThroughUnifiedWorkflow()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "原始镜像.iso");
        var bytes = new byte[8193];
        new Random(7).NextBytes(bytes.AsSpan(4096));
        await File.WriteAllBytesAsync(input, bytes);
        var settings = new AppSettings { OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "结果") };
        var workflow = new ConversionWorkflow([new DiscConversionEngine(new(), string.Empty, settings)]);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "iso", TargetFormatId = "cso",
            ContentProbe = new() { ContentKind = ContentKind.Disc, FormatId = "iso", FileSize = bytes.Length }
        };
        var compressed = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings), null, default);
        Assert.Equal(ConversionOutcome.Completed, compressed.Outcome);
        task.InputPath = compressed.OutputPath;
        task.SourceFormatId = "cso";
        task.TargetFormatId = "iso";
        var restored = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings), null, default);
        Assert.Equal(ConversionOutcome.Completed, restored.Outcome);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(restored.OutputPath));
        Assert.Empty(Directory.EnumerateFiles(settings.CustomOutputDirectory, "*.partial*"));
    }

    [Theory]
    [InlineData(true, false, ConversionOutcome.Completed)]
    [InlineData(false, false, ConversionOutcome.Failed)]
    [InlineData(false, true, ConversionOutcome.Cancelled)]
    public async Task MapsEngineOutcomeAndRetainsCommittedArtifact(bool success, bool cancelled, ConversionOutcome expected)
    {
        var engine = new Engine("selected", true, (_, _) => Task.FromResult(new ConversionExecutionResult(success, cancelled,
            success ? [new("result", "mp3", "output.mp3")] : [], "result")));
        var wrong = new Engine("not-supported", false, (_, _) => throw new InvalidOperationException("Wrong route"));
        var result = await new ConversionWorkflow([wrong, engine]).ExecuteAsync(Request(), null, default);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal("selected", result.EngineId);
        Assert.Equal(success ? "output.mp3" : string.Empty, result.OutputPath);
    }

    [Fact]
    public async Task MissingEngineAndThrowingEngineReturnFailure()
    {
        Assert.Equal(ConversionOutcome.Failed,
            (await new ConversionWorkflow([]).ExecuteAsync(Request(), null, default)).Outcome);
        var broken = new Engine("broken", true, (_, _) => throw new InvalidDataException("Damaged input"));
        Assert.Equal(ConversionOutcome.Failed,
            (await new ConversionWorkflow([broken]).ExecuteAsync(Request(), null, default)).Outcome);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-Hans")]
    [InlineData("es-ES")]
    [InlineData("he-IL")]
    public async Task MissingEngineReportsNeutralRouteWithoutInvokingFfmpeg(string language)
    {
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        try
        {
            LocalizationService.SetLanguage(language);
            var epubRequest = ConversionRequestFactory.Capture(new MediaTask
            {
                InputPath = "input.epub", SourceFormatId = "epub", TargetFormatId = "epub",
                ContentProbe = new() { ContentKind = ContentKind.Ebook, FormatId = "epub" }
            }, new());
            foreach (var request in new[] { Request(), epubRequest })
            {
                foreach (var registerFfmpeg in new[] { false, true })
                {
                    var ffmpegCalls = 0;
                    var ffmpeg = new Engine("ffmpeg", false, (_, _) =>
                    {
                        ffmpegCalls++;
                        throw new InvalidOperationException("The rejected FFmpeg engine must not execute.");
                    });
                    IConversionEngine[] engines = registerFfmpeg ? [ffmpeg] : [];
                    var result = await new ConversionWorkflow(engines).ExecuteAsync(request, null, default);
                    var source = request.Inputs[0].FormatId;
                    var target = request.TargetFormatId;

                    Assert.Equal(ConversionOutcome.Failed, result.Outcome);
                    Assert.False(result.Execution.Success);
                    Assert.False(result.Execution.Cancelled);
                    Assert.Empty(result.EngineId);
                    Assert.Empty(result.Execution.Artifacts);
                    Assert.Empty(result.OutputPath);
                    Assert.Equal(0, ffmpegCalls);
                    Assert.Equal(LocalizationService.T("ConversionFailed",
                        LocalizationService.T("ConversionUiRouteNotImplemented", source, target)), result.Execution.Message);
                    Assert.Contains(source, result.Execution.Message, StringComparison.Ordinal);
                    Assert.Contains(target, result.Execution.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("FFmpeg", result.Execution.Message, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            LocalizationService.SetLanguage(previousLanguage);
        }
    }

    [Fact]
    public async Task CancellationBeforeStartDoesNotInvokeEngine()
    {
        var engine = new Engine("unused", true, (_, _) => throw new Exception("Must not start"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new ConversionWorkflow([engine]).ExecuteAsync(Request(), null, cancellation.Token);
        Assert.Equal(ConversionOutcome.Cancelled, result.Outcome);
        Assert.Equal(string.Empty, result.EngineId);
    }

    [Fact]
    public void SnapshotCopiesNestedProbeOptionsSettingsAndAudioSelection()
    {
        var task = Media();
        task.Options["codec"] = "h264";
        var settings = new AppSettings { ParallelCount = 4, OutputMode = "source" };
        var snapshotSettings = settings.Snapshot();
        var request = ConversionRequestFactory.Capture(task, snapshotSettings, "memory-only-password");
        task.SelectedAudioTrack = 0;
        task.Options["codec"] = "hevc";
        task.Probe.AudioTracks[0].Language = "en";
        task.Probe.Width = 2;
        settings.ParallelCount = 1;
        var captured = ConversionRequestFactory.CreateMediaTask(request, "ffmpeg");
        Assert.Equal(2, captured.SelectedAudioTrack);
        Assert.Equal("h264", captured.Options["codec"]);
        Assert.Equal("zh", captured.Probe.AudioTracks[0].Language);
        Assert.Equal(1920, captured.Probe.Width);
        Assert.Equal(4, snapshotSettings.ParallelCount);
        Assert.Equal("source", ConversionRequestFactory.OutputSettings(snapshotSettings, request.OutputDirectory).OutputMode);
        Assert.Equal("memory-only-password", request.Options["password"]);
    }

    [Fact]
    public async Task QueuedEditsAreCapturedAtAdmissionAndSubsequentEditsCannotChangeExecution()
    {
        using var queue = new ConversionQueueCoordinator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = new TaskCompletionSource<ConversionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Media();
        queue.TryStart(Guid.NewGuid(), token => release.Task.WaitAsync(token), () => Task.CompletedTask,
            _ => Task.CompletedTask, () => Task.CompletedTask);
        queue.TryStart(task.Id, _ => { captured.SetResult(ConversionRequestFactory.Capture(task, new())); return Task.CompletedTask; },
            () => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask);
        task.ConversionMode = "lossless";
        release.SetResult();
        var request = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        task.ConversionMode = "balanced";
        await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lossless", request.Mode);
    }

    private static MediaTask Media() => new()
    {
        InputPath = "input.mp4", SourceFormatId = "mp4", TargetFormatId = "mp3", SelectedAudioTrack = 2,
        Probe = new MediaProbeResult
        {
            ContentKind = ContentKind.Video, FormatId = "mp4", Width = 1920,
            AudioTracks = [new() { RelativeIndex = 2, Language = "zh" }]
        }
    };
    private static ConversionRequest Request() => ConversionRequestFactory.Capture(Media(), new());
    private sealed class Engine(string id, bool supports, Func<ConversionRequest, CancellationToken, Task<ConversionExecutionResult>> run)
        : IConversionEngine
    {
        public string EngineId => id;
        public bool CanHandle(string sourceFormatId, string targetFormatId) => supports;
        public Task<ConversionExecutionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionExecutionProgress>? progress,
            CancellationToken cancellationToken) => run(request, cancellationToken);
    }
}
