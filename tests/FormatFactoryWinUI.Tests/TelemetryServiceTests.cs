using System.Collections.Concurrent;
using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class TelemetryServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Configure_TwoPreferencesAreIndependent(bool usageEnabled, bool errorsEnabled)
    {
        using var directory = new TemporaryDirectory();
        var reports = new FeedbackReportService(directory.Path);
        var usage = new ConcurrentQueue<TelemetryUsageSample>();
        var errors = new ConcurrentQueue<string>();
        using var service = new TelemetryService(reports, "1.2.3.4",
            (sample, _) => { usage.Enqueue(sample); return Task.CompletedTask; },
            (report, _) => { errors.Enqueue(report.Preview); return Task.CompletedTask; });
        service.Configure(usageEnabled, errorsEnabled);

        service.TrackAppLaunch();
        service.TrackPageVisit(TelemetryPage.Settings);
        service.TrackConversionResult(TelemetryConversionResult.Succeeded, MediaKind.Video, "MP4");
        service.ReportError("codec failure token=private-secret");
        if (usageEnabled)
            await WaitUntilAsync(() => usage.Count == 3);
        if (errorsEnabled)
            await WaitUntilAsync(() => !reports.HasPendingError);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(usageEnabled ? 3 : 0, usage.Count);
        Assert.Equal(errorsEnabled, errors.Count > 0);
        Assert.Equal(!errorsEnabled, reports.HasPendingError);
        Assert.All(errors, error => Assert.DoesNotContain("private-secret", error));
    }

    [Fact]
    public async Task UnconfiguredService_IsSilentUntilSavedPreferencesAreApplied()
    {
        using var directory = new TemporaryDirectory();
        var reports = new FeedbackReportService(directory.Path);
        reports.RecordError("previous launch error");
        var calls = 0;
        using var service = new TelemetryService(reports, "1.0",
            (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; });

        service.TrackAppLaunch();
        service.TrackPageVisit(TelemetryPage.History);
        service.ReportError("current local-only error");
        service.Configure(false, false);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, calls);
        Assert.True(reports.HasPendingError);
        using var report = await reports.CreateAsync("", true, "1.0");
        Assert.Contains("current local-only error", report.Preview);
    }

    [Fact]
    public async Task EnablingErrors_RetriesPendingLocalErrorOnceWithoutFailureLoop()
    {
        using var directory = new TemporaryDirectory();
        var reports = new FeedbackReportService(directory.Path);
        reports.RecordError("previous launch failure");
        var called = NewSignal();
        var calls = 0;
        string? temporaryZip = null;
        using var service = new TelemetryService(reports, "1.0", (_, _) => Task.CompletedTask,
            (report, _) =>
            {
                Interlocked.Increment(ref calls);
                temporaryZip = report.ArchivePath;
                called.TrySetResult();
                throw new HttpRequestException("simulated offline state");
            });

        service.Configure(false, true);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Configure(false, true);
        service.Configure(false, true);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
        Assert.True(reports.HasPendingError);
        Assert.False(File.Exists(temporaryZip));
    }

    [Fact]
    public async Task DisablingUsage_CancelsInFlightDropsQueuedSamplesAndKeepsErrorsEnabled()
    {
        using var directory = new TemporaryDirectory();
        var reports = new FeedbackReportService(directory.Path);
        var usageEntered = NewSignal();
        var usageCancelled = NewSignal();
        var usageCalls = 0;
        var errorCalls = 0;
        using var service = new TelemetryService(reports, "1.0", async (_, token) =>
        {
            Interlocked.Increment(ref usageCalls);
            usageEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { usageCancelled.TrySetResult(); throw; }
        }, (_, _) => { Interlocked.Increment(ref errorCalls); return Task.CompletedTask; });
        service.Configure(true, true);
        service.TrackAppLaunch();
        await usageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.TrackPageVisit(TelemetryPage.History);

        service.Configure(false, true);
        await usageCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ReportError("error after usage opt-out");
        await WaitUntilAsync(() => errorCalls > 0 && !reports.HasPendingError);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, usageCalls);
        Assert.True(errorCalls > 0);
    }

    [Fact]
    public async Task DisablingErrors_CancelsInFlightKeepsLocalLatestErrorAndUsageContinues()
    {
        using var directory = new TemporaryDirectory();
        var reports = new FeedbackReportService(directory.Path);
        var errorEntered = NewSignal();
        var errorCancelled = NewSignal();
        var errorCalls = 0;
        var usageCalls = 0;
        using var service = new TelemetryService(reports, "1.0",
            (_, _) => { Interlocked.Increment(ref usageCalls); return Task.CompletedTask; }, async (_, token) =>
            {
                Interlocked.Increment(ref errorCalls);
                errorEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { errorCancelled.TrySetResult(); throw; }
            });
        service.Configure(true, true);
        service.ReportError("first error");
        await errorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ReportError("queued error");

        service.Configure(true, false);
        await errorCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ReportError("local error while automatic reporting disabled");
        service.TrackAppLaunch();
        await WaitUntilAsync(() => usageCalls == 1);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, errorCalls);
        Assert.True(reports.HasPendingError);
        using var report = await reports.CreateAsync("", true, "1.0");
        Assert.Contains("local error while automatic reporting disabled", report.Preview);
    }

    [Fact]
    public async Task ReenablingUsage_DoesNotReplayOldGeneration()
    {
        using var directory = new TemporaryDirectory();
        var entered = NewSignal();
        var samples = new ConcurrentQueue<TelemetryUsageSample>();
        using var service = new TelemetryService(new FeedbackReportService(directory.Path), "1.0", async (sample, token) =>
        {
            samples.Enqueue(sample);
            if (sample.Event == "app_launch")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        }, (_, _) => Task.CompletedTask);
        service.Configure(true, false);
        service.TrackAppLaunch();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.TrackPageVisit(TelemetryPage.History);
        service.Configure(false, false);
        service.Configure(true, false);

        service.TrackPageVisit(TelemetryPage.Settings);
        await WaitUntilAsync(() => samples.Count == 2);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(samples, sample => sample.Page == "history");
        Assert.Contains(samples, sample => sample.Page == "settings");
    }

    [Fact]
    public async Task UsageQueue_BoundsBacklogDuringSlowNetwork()
    {
        using var directory = new TemporaryDirectory();
        var entered = NewSignal();
        var release = NewSignal();
        var calls = 0;
        using var service = new TelemetryService(new FeedbackReportService(directory.Path), "1.0", async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
        }, (_, _) => Task.CompletedTask);
        service.Configure(true, false);
        service.TrackAppLaunch();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 1000; i++)
            service.TrackPageVisit(TelemetryPage.Queue);
        release.TrySetResult();
        await WaitUntilAsync(() => calls == TelemetryService.UsageQueueCapacity + 1);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TelemetryService.UsageQueueCapacity + 1, calls);
    }

    [Fact]
    public async Task ErrorQueue_CoalescesBurstAndSuccessCannotAcknowledgeNewerError()
    {
        using var directory = new TemporaryDirectory();
        var reports = new FeedbackReportService(directory.Path);
        var entered = NewSignal();
        var release = NewSignal();
        var errors = new ConcurrentQueue<string>();
        using var service = new TelemetryService(reports, "1.0", (_, _) => Task.CompletedTask, async (report, _) =>
        {
            errors.Enqueue(report.ErrorMessage!);
            if (errors.Count == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
        });
        service.Configure(false, true);
        service.ReportError("first error");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++)
            service.ReportError($"newer error {i}");

        release.TrySetResult();
        await WaitUntilAsync(() => !reports.HasPendingError);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "first error", "newer error 99" }, errors.ToArray());
    }

    [Fact]
    public async Task Dispose_CancelsWithoutWaitingForNonCooperatingSender()
    {
        using var directory = new TemporaryDirectory();
        var entered = NewSignal();
        var release = NewSignal();
        using var service = new TelemetryService(new FeedbackReportService(directory.Path), "1.0", async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, (_, _) => Task.CompletedTask);
        service.Configure(true, false);
        service.TrackAppLaunch();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.Dispose();

        Assert.False(service.Completion.IsCompleted);
        release.TrySetResult();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        service.Configure(true, true);
        service.TrackAppLaunch();
    }

    [Fact]
    public async Task UsageDimensions_RejectUnknownEnumsAndArbitraryOutputStrings()
    {
        using var directory = new TemporaryDirectory();
        var samples = new ConcurrentQueue<TelemetryUsageSample>();
        using var service = new TelemetryService(new FeedbackReportService(directory.Path), "1.0",
            (sample, _) => { samples.Enqueue(sample); return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        service.Configure(true, false);

        service.TrackPageVisit((TelemetryPage)999);
        service.TrackConversionResult((TelemetryConversionResult)999, MediaKind.Video, "MP4");
        service.TrackConversionResult(TelemetryConversionResult.Failed, (MediaKind)999, "MP4");
        service.TrackConversionResult(TelemetryConversionResult.Succeeded, MediaKind.Video, "MP4");
        service.TrackConversionResult(TelemetryConversionResult.Failed, MediaKind.Document, "C:\\Users\\private-user\\report.pdf");
        await WaitUntilAsync(() => samples.Count == 2);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "mp4", "other" }, samples.Select(sample => sample.OutputFormat));
        Assert.DoesNotContain("private-user", JsonSerializer.Serialize(samples));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
