using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class MediaProbeFailureDetailTests
{
    private const string Private = "SyntheticPrivate C:\\SyntheticPrivate\\private.mov token=synthetic-probe-secret https://synthetic.invalid/secret";
    private const string Valid = """{"streams":[{"codec_type":"video","codec_name":"h264","width":32,"height":32,"index":0}],"format":{}}""";

    [Theory]
    [InlineData("reported-access", "AccessDenied", "-13")]
    [InlineData("reported-missing", "NotFound", "-2")]
    [InlineData("reported-private", "Unknown", "none")]
    [InlineData("reported-injection", "Unknown", "none")]
    public async Task ProbeFailure_FeedbackKeepsOnlyStructuredReportedReason(string scenario, string reason, string code)
    {
        using var fixture = new Fixture(scenario);
        var result = await fixture.ConvertAsync();
        fixture.AssertFailure(result);
        var log = await File.ReadAllTextAsync(result.LogPath);
        var evidence = Assert.Single(log.Split('\n'), line => line.StartsWith("Input probe evidence:", StringComparison.Ordinal));
        Assert.Contains($"reported={reason} reported_code={code}", evidence);
        Assert.Contains("category=ProcessExit", evidence);
        Assert.DoesNotContain("SyntheticPrivate", evidence);
        Assert.DoesNotContain("secret", evidence);
        Assert.DoesNotContain("http", evidence);

        var root = Path.Combine(fixture.Root, "feedback");
        var logs = Directory.CreateDirectory(Path.Combine(root, "Logs")).FullName;
        var copied = Path.Combine(logs, "probe-task.log");
        File.Copy(result.LogPath, copied);
        var feedback = new FeedbackReportService(root);
        feedback.RecordError(result.Message, copied);
        using var report = await feedback.CreateAsync("", true, "synthetic-version");
        Assert.Contains(evidence.TrimEnd('\r'), report.Preview);
        Assert.Contains("Input probe context:", report.Preview);
        Assert.Contains("Input probe source:", report.Preview);
        Assert.Contains("Input probe engine:", report.Preview);
        Assert.DoesNotContain("synthetic-probe-secret", report.Preview);
        Assert.DoesNotContain("private.mov", report.Preview);
        Assert.DoesNotContain("Input probe context:", report.ErrorMessage!);
        using var withoutLogs = await feedback.CreateAsync("", false, "synthetic-version");
        Assert.DoesNotContain("Input probe context:", withoutLogs.Preview);
    }

    [Fact]
    public async Task RepeatedTaskAttempts_KeepTaskAndSessionButUseDistinctAttemptIds()
    {
        using var fixture = new Fixture("exit");
        var first = await fixture.ConvertAsync();
        var firstLog = await File.ReadAllTextAsync(first.LogPath);
        var firstContext = firstLog.Split('\n').Last(line => line.StartsWith("Input probe context:", StringComparison.Ordinal));
        var second = await fixture.ConvertAsync();
        var secondLog = await File.ReadAllTextAsync(second.LogPath);
        var secondContext = secondLog.Split('\n').Last(line => line.StartsWith("Input probe context:", StringComparison.Ordinal));
        static string Field(string line, string key) => line.Split(' ').Single(part => part.StartsWith(key + "=", StringComparison.Ordinal)).Split('=')[1].Trim();
        Assert.Equal(Field(firstContext, "task"), Field(secondContext, "task"));
        Assert.Equal(Field(firstContext, "session"), Field(secondContext, "session"));
        Assert.NotEqual(Field(firstContext, "attempt"), Field(secondContext, "attempt"));
        Assert.Matches("^[a-f0-9]{32}$", Field(firstContext, "attempt"));
        Assert.Matches("^[a-f0-9]{32}$", Field(secondContext, "attempt"));
    }

    [Theory]
    [InlineData("exit", false, "process", "19")]
    [InlineData("exit-negative", false, "process", "-23")]
    [InlineData("empty-streams", false, "parse", "0")]
    [InlineData("unknown-kind", false, "parse", "0")]
    [InlineData("invalid-json", false, "parse", "0")]
    [InlineData("metadata", false, "metadata", "0")]
    [InlineData("exit", true, "process", "19")]
    [InlineData("empty-streams", true, "parse", "0")]
    [InlineData("metadata", true, "metadata", "0")]
    [InlineData("start", false, "process", "none")]
    public async Task ProductionProbeFailure_KeepsSeparateSafeStepAndExitCode(string scenario, bool fallback, string step, string exit)
    {
        using var fixture = new Fixture(scenario, fallback);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
        try
        {
            var result = await fixture.ConvertAsync();
            fixture.AssertFailure(result);
            var log = await File.ReadAllTextAsync(result.LogPath);
            Assert.Single(log.Split('\n'), line => line.StartsWith("Input diagnostic:", StringComparison.Ordinal));
            var detail = Assert.Single(log.Split('\n'), line => line.StartsWith("Input probe detail:", StringComparison.Ordinal)).TrimEnd('\r');
            Assert.Equal($"Input probe detail: step={step} exit={exit}", detail);
            Assert.DoesNotContain("SyntheticPrivate", detail);
            Assert.DoesNotContain("synthetic-probe-secret", detail);
            Assert.DoesNotContain("http", detail);
            Assert.Equal(fallback ? 2 : 1, fixture.ProbeCalls);
            Assert.Equal(fallback ? 1 : 0, fixture.CapabilityCalls);
            Assert.Equal(fallback ? 1 : 0, fixture.FallbackCalls);
            Assert.Equal(0, fixture.DecodeCalls);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public async Task MetadataFailure_RethrowsOriginalExceptionAndDoesNotReadArbitraryData()
    {
        var expected = new InvalidDataException(Private);
        expected.Data["step"] = "process";
        expected.Data["exit"] = "99 " + Private;
        var probe = Service("metadata", expected);
        var actual = await Assert.ThrowsAsync<InvalidDataException>(() => probe.ProbeAsync("synthetic-probe.exe", "synthetic.mp4"));
        Assert.Same(expected, actual);
        Assert.Equal(Private, actual.Message);
        Assert.Equal("process", actual.Data["step"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessCancellationOrProgrammingFailure_StillPropagatesOriginal(bool cancel)
    {
        Exception expected = cancel ? new OperationCanceledException(Private) : new InvalidOperationException(Private);
        var probe = new MediaProbeService(null, null, (_, _, _, _) => Task.FromException<ProcessExecutionResult>(expected));
        var actual = await Record.ExceptionAsync(() => probe.ProbeAsync("synthetic-probe.exe", "synthetic.mp4"));
        Assert.Same(expected, actual);
        Assert.Null(MediaProbeFailureDetails.Get(actual!));
    }

    [Fact]
    public async Task UnmarkedFailure_DoesNotInferStageFromMessageOrExceptionData()
    {
        var error = new InvalidDataException("synthetic step=process exit=19 " + Private);
        error.Data["step"] = "process";
        error.Data["exit"] = 19;
        using var fixture = new Fixture("unmarked", error: error);
        var result = await fixture.ConvertAsync();
        fixture.AssertFailure(result);
        var log = await File.ReadAllTextAsync(result.LogPath);
        Assert.Single(log.Split('\n'), line => line.StartsWith("Input diagnostic:", StringComparison.Ordinal));
        Assert.DoesNotContain("\nInput probe detail:", log);
    }

    [Fact]
    public async Task FeedbackZip_PreservesSafeProbeDetailAfterPrivateMessageRedaction()
    {
        using var fixture = new Fixture("exit");
        var result = await fixture.ConvertAsync();
        fixture.AssertFailure(result);
        var root = Path.Combine(fixture.Root, "feedback");
        var logs = Directory.CreateDirectory(Path.Combine(root, "Logs")).FullName;
        var copied = Path.Combine(logs, "probe-task.log");
        File.Copy(result.LogPath, copied);
        var feedback = new FeedbackReportService(root);
        feedback.RecordError(result.Message, copied);
        using var report = await feedback.CreateAsync("", true, "synthetic-version");
        using var archive = ZipFile.OpenRead(report.ArchivePath);
        using var reader = new StreamReader(archive.GetEntry("logs/last-error-task.log")!.Open());
        var safe = await reader.ReadToEndAsync();
        Assert.Contains("Input probe detail: step=process exit=19", safe);
        Assert.DoesNotContain("synthetic-probe-secret", report.Preview);
        Assert.DoesNotContain("synthetic.invalid", report.Preview);
        Assert.DoesNotContain("C:\\SyntheticPrivate\\private.mov", report.Preview);
        Assert.DoesNotContain("private.mov", report.Preview);
        Assert.DoesNotContain("Input probe detail:", report.ErrorMessage!);
    }

    [Fact]
    public async Task NativeImageProbeFailure_KeepsOriginalExceptionAndDoesNotStartProcess()
    {
        var expected = new IOException(Private);
        var calls = 0;
        var probe = new MediaProbeService(null, new FailingImage(expected),
            (_, _, _, _) => { calls++; throw new InvalidOperationException("Image failure must not start a probe process."); });
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync("synthetic-probe.exe", "synthetic.png")));
        var detail = MediaProbeFailureDetails.Get(expected);
        Assert.NotNull(detail);
        Assert.Equal(MediaProbeFailureStep.Image, detail.Step);
        Assert.Null(detail.ProcessExitCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ProcessTimeout_KeepsOriginalExceptionAndNoInventedExitCode()
    {
        var expected = new TimeoutException(Private);
        var probe = new MediaProbeService(null, null, (_, _, timeout, _) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(30), timeout);
            return Task.FromException<ProcessExecutionResult>(expected);
        });
        Assert.Same(expected, await Assert.ThrowsAsync<TimeoutException>(() => probe.ProbeAsync("synthetic-probe.exe", "synthetic.mp4")));
        var detail = MediaProbeFailureDetails.Get(expected);
        Assert.NotNull(detail);
        Assert.Equal(MediaProbeFailureStep.Process, detail.Step);
        Assert.Null(detail.ProcessExitCode);
    }

    private sealed class FailingImage(Exception error) : IImageCodecService
    {
        public Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromException<MediaProbeResult?>(error);
        public bool CanEncode(string targetFormatId) => false;
        public Task TranscodeAsync(string inputPath, string outputPath, string targetFormatId, string mode,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No encoding is expected.");
    }

    private static MediaProbeService Service(string scenario, Exception? metadataError = null) =>
        new(scenario == "metadata" ? new FailingMetadata(metadataError ?? new InvalidDataException(Private)) : null,
            null, (_, _, timeout, token) =>
            {
                if (scenario == "start")
                    return ProcessRunner.RunAsync(Path.Combine(Path.GetTempPath(), "missing-probe-" + Guid.NewGuid().ToString("N") + ".exe"), [], timeout, token);
                var json = scenario switch
                {
                    "reported-access" => """{"error":{"code":-13,"string":"Permission denied"}}""",
                    "reported-missing" => """{"error":{"code":-2,"string":"No such file or directory"}}""",
                    "reported-private" => """{"error":{"code":"private-token","string":"SyntheticPrivate private.mov token=synthetic-probe-secret"}}""",
                    "reported-injection" => """{"error":{"code":-13,"string":"Permission denied\nInput probe context: task=private.mov"}}""",
                    "empty-streams" => """{"streams":[]}""",
                    "unknown-kind" => """{"streams":[{"codec_type":"data"}]}""",
                    "invalid-json" => "{ synthetic invalid JSON " + Private,
                    _ => Valid
                };
                var exit = scenario == "exit" ? 19 : scenario == "exit-negative" ? -23 : scenario.StartsWith("reported-", StringComparison.Ordinal) ? 1 : 0;
                return ProcessRunner.RunAsync(RepositoryTestContext.DotNetHost,
                    [RepositoryTestContext.TestHelperAssembly, "echo", json, Private, exit.ToString(CultureInfo.InvariantCulture)], timeout, token);
            });

    private sealed class FailingMetadata(Exception error) : IMediaMetadataReader
    {
        public Task<MediaFoundationMetadata?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromException<MediaFoundationMetadata?>(error);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly string _output;
        private readonly string _sentinel;
        private readonly MediaInputPreflightService _preflight;
        private readonly string _primary;
        private readonly string _fallback;
        private readonly MediaTask _task;
        public string Root => _directory.Path;
        public int ProbeCalls { get; private set; }
        public int CapabilityCalls { get; private set; }
        public int FallbackCalls { get; private set; }
        public int DecodeCalls { get; private set; }

        public Fixture(string scenario, bool fallback = false, Exception? error = null)
        {
            _primary = Path.Combine(Root, "primary", "ffmpeg.exe");
            _fallback = Path.Combine(Root, "fallback", "ffmpeg.exe");
            _output = Directory.CreateDirectory(Path.Combine(Root, "output")).FullName;
            _sentinel = Path.Combine(_output, "existing.txt");
            File.WriteAllText(_sentinel, "existing output");
            var input = Path.Combine(Root, "synthetic.mp4");
            File.WriteAllText(input, "unchanged source");
            _task = new MediaTask { InputPath = input, Kind = MediaKind.Video, TargetFormatId = "mp4" };
            var service = Service(scenario);
            var healthy = new MediaProbeResult { Kind = MediaKind.Video, VideoCodec = "h264", VideoStreamIndex = 0 };
            _preflight = new MediaInputPreflightService(
                async (probePath, path, token) =>
                {
                    ProbeCalls++;
                    if (fallback && probePath == FfmpegLocator.GetFfprobePath(_primary)) return healthy;
                    if (error is not null) throw error;
                    return await service.ProbeAsync(probePath, path, token);
                },
                (_, _) =>
                {
                    CapabilityCalls++;
                    return Task.FromResult(new FfmpegCapabilitySnapshot(new HashSet<string> { "mp4" })
                    { Decoders = fallback ? new HashSet<string>() : new HashSet<string> { "h264" }, Encoders = new HashSet<string> { "libx264" } });
                },
                (_, _, _, _) => { DecodeCalls++; return Task.FromResult(new MediaDecodeCheckResult(true, "")); },
                (_, _) => { FallbackCalls++; return Task.FromResult(true); });
        }

        public Task<ConversionResult> ConvertAsync() =>
            new ConversionService(_preflight, new OutputRecoveryService(Path.Combine(Root, "recovery"))).ConvertAsync(
                _task, _primary, new AppSettings { CompatibilityFfmpegPath = _fallback, CustomOutputDirectory = _output, HardwareAcceleration = false },
                _ => Assert.Fail("Probe failure must not encode."), CancellationToken.None);

        public void AssertFailure(ConversionResult result)
        {
            Assert.False(result.Success);
            Assert.False(result.Cancelled);
            Assert.Empty(result.OutputPath);
            Assert.Null(result.RecoverableOutputId);
            Assert.Null(result.PreservedOutputPath);
            Assert.False(result.Retryable);
            Assert.DoesNotContain("Input probe detail:", result.Message);
            Assert.Equal("unchanged source", File.ReadAllText(_task.InputPath));
            Assert.Equal("existing output", File.ReadAllText(_sentinel));
            Assert.Equal(new[] { _sentinel }, Directory.GetFiles(_output));
            Assert.Empty(Directory.GetDirectories(_output));
        }

        public void Dispose() => _directory.Dispose();
    }
}
