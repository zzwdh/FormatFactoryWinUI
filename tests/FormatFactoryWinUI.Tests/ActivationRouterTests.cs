using System.Collections.Concurrent;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ActivationRouterTests
{
    [Fact]
    public async Task NavigationRequests_QueuedBeforeAndAfterAttach_AreHandledSequentially()
    {
        await using var router = new ActivationRouter();
        var handled = new ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(router.Enqueue(new ActivationRequest([], NavigationTarget: "history")));
        router.Attach(async request =>
        {
            handled.Enqueue(request.NavigationTarget!);
            if (request.NavigationTarget == "history")
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (handled.Count == 2) allHandled.TrySetResult(true);
        });

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(router.Enqueue(new ActivationRequest([], NavigationTarget: "video")));
        releaseFirst.TrySetResult(true);
        await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["history", "video"], handled);
    }

    [Fact]
    public async Task RequestsQueuedBeforeAttach_AreHandledSequentiallyInArrivalOrder()
    {
        await using var router = new ActivationRouter();
        var handled = new ConcurrentQueue<string>();
        var allHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(router.Enqueue(new ActivationRequest(["first.mp4"])));
        Assert.True(router.Enqueue(new ActivationRequest(["second.mp4"])));
        router.Attach(async request =>
        {
            handled.Enqueue(request.FilePaths[0]);
            await Task.Yield();
            if (handled.Count == 2)
            {
                allHandled.TrySetResult(true);
            }
        });

        await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["first.mp4", "second.mp4"], handled);
    }

    [Fact]
    public async Task FailedRequest_ReportsLifetimeFailureAndDoesNotBlockNextRequest()
    {
        await using var router = new ActivationRouter();
        var lifetime = new TestActivationLifetime();
        var nextHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.Attach(request =>
        {
            if (request.FilePaths[0] == "bad.mp4")
            {
                throw new InvalidOperationException("failed");
            }

            nextHandled.TrySetResult(true);
            return Task.CompletedTask;
        });

        Assert.True(router.Enqueue(new ActivationRequest(["bad.mp4"], Lifetime: lifetime)));
        Assert.True(router.Enqueue(new ActivationRequest(["good.mp4"])));

        await nextHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, lifetime.CompletedCount);
        Assert.Equal(1, lifetime.FailedCount);
    }

    private sealed class TestActivationLifetime : IActivationLifetime
    {
        public int CompletedCount { get; private set; }

        public int FailedCount { get; private set; }

        public void Complete() => CompletedCount++;

        public void Fail(string message) => FailedCount++;
    }
}
