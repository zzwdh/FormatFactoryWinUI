using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class DynamicQueuePolicyTests
{
    [Fact]
    public async Task LowerLimitAffectsNextAdmissionWithoutCancellingRunningTasks_AndIncreaseWakesWaiters()
    {
        using var queue = new ConversionQueueCoordinator();
        queue.ConfigureParallelism(4);
        var started = Enumerable.Range(0, 6).Select(_ => Signal()).ToArray();
        var release = Enumerable.Range(0, 6).Select(_ => Signal()).ToArray();
        var finished = Enumerable.Range(0, 6).Select(_ => Signal()).ToArray();
        for (var index = 0; index < 6; index++)
        {
            var i = index;
            Assert.True(queue.TryStart(Guid.NewGuid(), async token =>
            {
                started[i].SetResult();
                await release[i].Task.WaitAsync(token);
                Assert.False(token.IsCancellationRequested);
            }, () => Task.CompletedTask, _ => { finished[i].SetResult(); return Task.CompletedTask; },
                () => Task.CompletedTask));
        }
        try
        {
            await started[3].Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.ConfigureParallelism(1);
            for (var i = 0; i < 3; i++)
            {
                release[i].SetResult();
                await finished[i].Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(started[4].Task.IsCompleted);
            }
            release[3].SetResult();
            await started[4].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(started[5].Task.IsCompleted);
            queue.ConfigureParallelism(3);
            await started[5].Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (var signal in release) signal.TrySetResult();
            await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(ConversionOutcome.Failed, "failed")]
    [InlineData(ConversionOutcome.Cancelled, "cancelled")]
    [InlineData(ConversionOutcome.Completed, "completed")]
    public async Task ExplicitOutcomeSurvivesNormalDelegateReturnAndMatchesDiagnostics(ConversionOutcome outcome, string stage)
    {
        using var listener = new StageListener();
        using var queue = new ConversionQueueCoordinator();
        var id = Guid.NewGuid();
        ConversionQueueOperationCompleted? observed = null;
        Assert.True(queue.TryStartWithOutcome(id, _ => Task.FromResult(outcome), () => Task.CompletedTask,
            result => { observed = result; return Task.CompletedTask; }, () => Task.CompletedTask));
        await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(outcome, observed!.Outcome);
        Assert.Equal(new[] { "started", stage }, listener.Stages.Where(item => item.Id == id.ToString("N")).Select(item => item.Stage));
        Assert.Empty(listener.Errors);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class StageListener : EventListener
    {
        public ConcurrentQueue<(string Id, string Stage)> Stages { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "zzwdh-FormatFactoryWinUI-Native") EnableEvents(source, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventId == 10 && e.Payload is { Count: >= 2 })
                Stages.Enqueue((e.Payload[0]?.ToString() ?? "", e.Payload[1]?.ToString() ?? ""));
            if (e.EventId == 0) Errors?.Enqueue(e.Message ?? "EventSource error");
        }
    }
}
