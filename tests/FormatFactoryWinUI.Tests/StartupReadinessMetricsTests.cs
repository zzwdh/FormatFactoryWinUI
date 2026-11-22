using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class StartupReadinessMetricsTests
{
    [Fact]
    public void ConcurrentMarks_RetainFirstValueAndReturnIndependentSnapshots()
    {
        var metrics = new StartupReadinessMetrics();
        metrics.Mark("add-files-ready");
        var before = metrics.Snapshot();
        Parallel.For(0, 50, _ => metrics.Mark("add-files-ready"));
        metrics.Mark("recovery-complete");
        Assert.Equal(before["add-files-ready"], metrics.Snapshot()["add-files-ready"]);
        Assert.Single(before);
        Assert.True(metrics.Snapshot()["recovery-complete"] >= before["add-files-ready"]);
    }

    [Fact]
    public void StageSnapshots_ShowPendingAndCompletedWorkWithoutDoubleDisposalOrReplacement()
    {
        var metrics = new StartupReadinessMetrics();
        using var stage = metrics.MeasureStage("engine-detection");
        Assert.Null(metrics.StageTimingsSnapshot()["engine-detection"].DurationMilliseconds);
        Assert.Empty(metrics.StagesSnapshot());
        stage.Dispose();
        var duration = metrics.StagesSnapshot()["engine-detection"];
        stage.Dispose();
        using (metrics.MeasureStage("engine-detection")) { }
        Assert.Equal(duration, metrics.StagesSnapshot()["engine-detection"]);
        Assert.True(duration >= 0);
    }

    [Fact]
    public void TraceEvents_ExposeAllInteractionBoundariesWithoutProviderErrors()
    {
        using var listener = new StartupListener();
        var metrics = new StartupReadinessMetrics(measureFromProcessStart: true);
        foreach (var milestone in new[] { "window-visible", "add-files-ready", "recovery-complete" })
            metrics.Mark(milestone);
        using (metrics.MeasureStage("engine")) { }
        var events = listener.Events.ToArray();
        Assert.DoesNotContain(events, item => item.Id == 0);
        foreach (var eventId in Enumerable.Range(1, 9))
            Assert.Contains(events, item => item.Id == eventId);
        Assert.All(metrics.Snapshot().Values, value => Assert.True(value >= 0));
    }

    private sealed class StartupListener : EventListener
    {
        public ConcurrentQueue<(int Id, string? Name)> Events { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "FormatFactory-Startup") EnableEvents(source, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs args) => Events.Enqueue((args.EventId, args.EventName));
    }
}
