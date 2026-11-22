using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class MediaInputDiagnosticsTests
{
    private const string PrivateMessage = "synthetic-message-marker at C:\\SyntheticPrivate\\private-media.mov; token=diagnostic-private-token https://synthetic.invalid/private-input";

    [Theory]
    [InlineData("win32-2", "Win32Exception", "en-US")]
    [InlineData("win32-5", "Win32Exception", "zh-Hans")]
    [InlineData("io", "IOException", "en-US")]
    [InlineData("missing", "FileNotFoundException", "zh-Hans")]
    [InlineData("invalid", "InvalidDataException", "en-US")]
    [InlineData("json", "JsonException", "en-US")]
    [InlineData("timeout", "TimeoutException", "en-US")]
    public async Task ProbeFailure_ActualTaskLogRetainsSafeCauseWithoutChangingResult(
        string failure, string exceptionType, string language)
    {
        var exception = CreateException(failure);
        using var fixture = new Fixture(exception);
        LocalizationService.SetLanguage(language);
        try
        {
            var result = await fixture.ConvertAsync();

            fixture.AssertFailureUnchanged(result, exception);
            Assert.Equal(1, fixture.ProbeCalls);
            Assert.Equal(0, fixture.CapabilityCalls);
            Assert.Equal(0, fixture.DecodeCalls);
            Assert.Equal(0, fixture.FallbackValidations);
            var log = await File.ReadAllTextAsync(result.LogPath);
            Assert.Contains("Input check: UnreadableInput\n", log);
            AssertDiagnostic(log, ExpectedDiagnostic(exception, exceptionType, false));
        }
        finally { LocalizationService.SetLanguage("en-US"); }
    }

    [Fact]
    public async Task FallbackProbeFailure_IdentifiesAttemptedCandidateWithoutChangingFallbackPolicy()
    {
        var exception = CreateException("win32-5");
        using var fixture = new Fixture(exception, useFallback: true);
        var selected = await fixture.Preflight.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.False(selected.Success);
        Assert.False(selected.UsedFallback);
        Assert.Equal(MediaInputFailureKind.UnreadableInput, selected.FailureKind);
        Assert.Equal(exception.Message, selected.Detail);
        Assert.Null(selected.Probe);
        Assert.Empty(selected.ExecutablePath);
        fixture.ResetCounters();

        var result = await fixture.ConvertAsync();

        fixture.AssertFailureUnchanged(result, exception);
        Assert.Equal(2, fixture.ProbeCalls);
        Assert.Equal(1, fixture.CapabilityCalls);
        Assert.Equal(1, fixture.FallbackValidations);
        Assert.Equal(0, fixture.DecodeCalls);
        AssertDiagnostic(await File.ReadAllTextAsync(result.LogPath),
            ExpectedDiagnostic(exception, "Win32Exception", true));
    }

    [Fact]
    public async Task ProbeFailure_FeedbackZipPreservesSeparateDiagnosticAfterPathRedaction()
    {
        var exception = CreateException("win32-2");
        using var fixture = new Fixture(exception);
        var result = await fixture.ConvertAsync();
        fixture.AssertFailureUnchanged(result, exception);
        var feedbackRoot = Path.Combine(fixture.Root, "feedback");
        var logs = Directory.CreateDirectory(Path.Combine(feedbackRoot, "Logs")).FullName;
        var copiedTaskLog = Path.Combine(logs, "synthetic-task.log");
        File.Copy(result.LogPath, copiedTaskLog);
        var feedback = new FeedbackReportService(feedbackRoot);
        feedback.RecordError(result.Message, copiedTaskLog);

        using var report = await feedback.CreateAsync("", true, "synthetic-version");
        using var archive = ZipFile.OpenRead(report.ArchivePath);
        var taskEntry = Assert.Single(archive.Entries, entry => entry.FullName == "logs/last-error-task.log");
        using var reader = new StreamReader(taskEntry.Open());
        var safeTaskLog = await reader.ReadToEndAsync();
        Assert.Contains("[path]", safeTaskLog);
        Assert.DoesNotContain("SyntheticPrivate", report.Preview);
        Assert.DoesNotContain("private-media.mov", report.Preview);
        Assert.DoesNotContain("diagnostic-private-token", report.Preview);
        Assert.DoesNotContain("synthetic.invalid", report.Preview);
        AssertDiagnostic(safeTaskLog, ExpectedDiagnostic(exception, "Win32Exception", false));
        Assert.Contains(ExpectedDiagnostic(exception, "Win32Exception", false), report.Preview);
        Assert.DoesNotContain("Input diagnostic:", report.ErrorMessage!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeCancellation_PropagatesWithoutDecodeOrOutput(bool useFallback)
    {
        var expected = new OperationCanceledException("synthetic cancellation");
        using var fixture = new Fixture(expected, useFallback);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ConvertAsync());

        Assert.Same(expected, actual);
        Assert.Equal(useFallback ? 2 : 1, fixture.ProbeCalls);
        Assert.Equal(useFallback ? 1 : 0, fixture.FallbackValidations);
        Assert.Equal(0, fixture.DecodeCalls);
        fixture.AssertInputsAndOutputsUnchanged();
    }

    [Fact]
    public async Task ActualDecodeFailure_KeepsItsMessageAndHasNoProbeDiagnostic()
    {
        using var fixture = new Fixture(null) { DecodeResult = new(false, "synthetic decode failure") };

        var result = await fixture.ConvertAsync();

        Assert.False(result.Success);
        Assert.Equal(LocalizationService.T("MediaCompatibilityDamaged", "synthetic decode failure"), result.Message);
        var log = await File.ReadAllTextAsync(result.LogPath);
        Assert.Contains("Input check: InvalidMedia\n", log);
        Assert.DoesNotContain("Input diagnostic:", log);
        Assert.Equal(1, fixture.DecodeCalls);
        Assert.Equal(0, fixture.FallbackValidations);
        fixture.AssertInputsAndOutputsUnchanged();
    }

    [Fact]
    public async Task HealthyPreflight_KeepsPrimaryProbeAndDoesNotInspectFallback()
    {
        using var fixture = new Fixture(null);

        var result = await fixture.Preflight.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);

        Assert.True(result.Success);
        Assert.False(result.UsedFallback);
        Assert.Same(fixture.Probe, result.Probe);
        Assert.Equal(fixture.Primary, result.ExecutablePath);
        Assert.Equal(MediaInputFailureKind.None, result.FailureKind);
        Assert.Empty(result.Detail);
        Assert.Equal(1, fixture.DecodeCalls);
        Assert.Equal(0, fixture.FallbackValidations);
        fixture.AssertInputsAndOutputsUnchanged();
    }

    private static Exception CreateException(string failure) => failure switch
    {
        "win32-2" => new Win32Exception(2, PrivateMessage),
        "win32-5" => new Win32Exception(5, PrivateMessage),
        "io" => new IOException(PrivateMessage, unchecked((int)0x80131620)),
        "missing" => new FileNotFoundException(PrivateMessage, "private-media.mov"),
        "invalid" => new InvalidDataException(PrivateMessage),
        "json" => new JsonException(PrivateMessage),
        "timeout" => new TimeoutException(PrivateMessage),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    private static string ExpectedDiagnostic(Exception exception, string exceptionType, bool fallback) =>
        $"Input diagnostic: stage=probe candidate={(fallback ? "fallback" : "primary")} exception={exceptionType} " +
        $"hresult=0x{exception.HResult.ToString("X8", CultureInfo.InvariantCulture)} " +
        $"native={(exception is Win32Exception win32 ? win32.NativeErrorCode.ToString(CultureInfo.InvariantCulture) : "none")}";

    private static void AssertDiagnostic(string log, string expected)
    {
        var line = Assert.Single(log.Split('\n'), value => value.StartsWith("Input diagnostic:", StringComparison.Ordinal));
        Assert.Equal(expected, line.TrimEnd('\r'));
        Assert.DoesNotContain("synthetic-message-marker", line);
        Assert.DoesNotContain("private-media", line);
        Assert.DoesNotContain("SyntheticPrivate", line);
        Assert.DoesNotContain("token", line);
        Assert.DoesNotContain("http", line);
        Assert.DoesNotContain("path", line);
        Assert.DoesNotContain("message", line);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly bool _useFallback;
        private readonly Exception? _probeError;
        private readonly string _output;
        private readonly string _sentinel;
        public string Root => _directory.Path;
        public string Primary { get; }
        public string Fallback { get; }
        public MediaTask Task { get; }
        public MediaProbeResult Probe { get; } = new() { Kind = MediaKind.Video, VideoCodec = "h264", VideoStreamIndex = 0 };
        public MediaInputPreflightService Preflight { get; }
        public MediaDecodeCheckResult DecodeResult { get; set; } = new(true, string.Empty);
        public int ProbeCalls { get; private set; }
        public int CapabilityCalls { get; private set; }
        public int DecodeCalls { get; private set; }
        public int FallbackValidations { get; private set; }

        public Fixture(Exception? probeError, bool useFallback = false)
        {
            _probeError = probeError;
            _useFallback = useFallback;
            Primary = Path.Combine(Root, "primary", "ffmpeg.exe");
            Fallback = Path.Combine(Root, "fallback", "ffmpeg.exe");
            var input = Path.Combine(Root, "synthetic.mp4");
            File.WriteAllText(input, "synthetic input bytes");
            _output = Directory.CreateDirectory(Path.Combine(Root, "output")).FullName;
            _sentinel = Path.Combine(_output, "existing.txt");
            File.WriteAllText(_sentinel, "existing output bytes");
            Task = new MediaTask { InputPath = input, Kind = MediaKind.Video, TargetFormatId = "mp4" };
            Preflight = new MediaInputPreflightService(
                (executable, _, _) =>
                {
                    ProbeCalls++;
                    if (_probeError is not null && (!_useFallback || executable == FfmpegLocator.GetFfprobePath(Fallback)))
                        return System.Threading.Tasks.Task.FromException<MediaProbeResult>(_probeError);
                    return System.Threading.Tasks.Task.FromResult(Probe);
                },
                (executable, _) =>
                {
                    CapabilityCalls++;
                    return System.Threading.Tasks.Task.FromResult(new FfmpegCapabilitySnapshot(new HashSet<string> { "mp4" })
                    {
                        Decoders = _useFallback && executable == Primary ? new HashSet<string>() : new HashSet<string> { "h264" },
                        Encoders = new HashSet<string> { "libx264" }
                    });
                },
                (_, _, _, _) =>
                {
                    DecodeCalls++;
                    return System.Threading.Tasks.Task.FromResult(DecodeResult);
                },
                (_, _) =>
                {
                    FallbackValidations++;
                    return System.Threading.Tasks.Task.FromResult(true);
                });
        }

        public Task<ConversionResult> ConvertAsync() =>
            new ConversionService(Preflight, new OutputRecoveryService(Path.Combine(Root, "recovery"))).ConvertAsync(
                Task, Primary, new AppSettings
                {
                    CompatibilityFfmpegPath = Fallback, CustomOutputDirectory = _output, HardwareAcceleration = false
                }, _ => Assert.Fail("Preflight failure must not report encoding progress."), CancellationToken.None);

        public void AssertFailureUnchanged(ConversionResult result, Exception exception)
        {
            Assert.False(result.Success);
            Assert.False(result.Cancelled);
            Assert.Empty(result.OutputPath);
            Assert.Null(result.RecoverableOutputId);
            Assert.Null(result.PreservedOutputPath);
            Assert.False(result.Retryable);
            Assert.Equal(LocalizationService.T("MediaCompatibilityUnreadable", exception.Message), result.Message);
            Assert.DoesNotContain("Input diagnostic:", result.Message);
            AssertInputsAndOutputsUnchanged();
        }

        public void AssertInputsAndOutputsUnchanged()
        {
            Assert.Equal("synthetic input bytes", File.ReadAllText(Task.InputPath));
            Assert.Equal("existing output bytes", File.ReadAllText(_sentinel));
            Assert.Equal(new[] { _sentinel }, Directory.GetFiles(_output));
            Assert.Empty(Directory.GetDirectories(_output));
        }

        public void ResetCounters() => (ProbeCalls, CapabilityCalls, DecodeCalls, FallbackValidations) = (0, 0, 0, 0);
        public void Dispose() => _directory.Dispose();
    }
}
