using System.Diagnostics;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ActiveConversionProcessTests
{
    [Fact]
    public async Task TrySuspend_ConcurrentCallsAcquireOnlyOneSuspension()
    {
        using var process = Process.GetCurrentProcess();
        var acquisitions = 0;
        var releases = 0;
        using var active = new ActiveConversionProcess(process, suspendProcess: _ =>
        {
            Interlocked.Increment(ref acquisitions);
            Thread.Sleep(30);
            return new CallbackDisposable(() => Interlocked.Increment(ref releases));
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(active.TrySuspend)));

        Assert.Single(results, result => result);
        Assert.Equal(1, acquisitions);
        Assert.True(active.TryResume());
        Assert.False(active.TryResume());
        active.Dispose();
        Assert.Equal(1, releases);
        Assert.False(active.TrySuspend());
    }

    [Fact]
    public async Task Dispose_DuringSuspendReleasesTheNewSuspensionBeforeReturning()
    {
        using var process = Process.GetCurrentProcess();
        using var suspendEntered = new ManualResetEventSlim();
        using var releaseSuspend = new ManualResetEventSlim();
        using var disposeEntered = new ManualResetEventSlim();
        var releases = 0;
        using var active = new ActiveConversionProcess(process, suspendProcess: _ =>
        {
            suspendEntered.Set();
            if (!releaseSuspend.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return new CallbackDisposable(() => Interlocked.Increment(ref releases));
        });
        var suspend = Task.Run(active.TrySuspend);
        Assert.True(suspendEntered.Wait(TimeSpan.FromSeconds(5)));
        var dispose = Task.Factory.StartNew(() =>
        {
            disposeEntered.Set();
            active.Dispose();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotSame(dispose, await Task.WhenAny(dispose, Task.Delay(100)));
        }
        finally
        {
            releaseSuspend.Set();
            await Task.WhenAll(suspend, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(await suspend);
        Assert.Equal(1, releases);
        Assert.False(active.TrySuspend());
        Assert.False(active.TryResume());
    }

    [Fact]
    public void TrySuspend_AfterProcessDisposalReturnsFalse()
    {
        var process = Process.GetCurrentProcess();
        using var active = new ActiveConversionProcess(process, suspendProcess: _ =>
            throw new InvalidOperationException("A disposed process must not be suspended."));
        process.Dispose();

        Assert.False(active.TrySuspend());
    }

    [Fact]
    public async Task TryTerminate_FailedResumeStillTerminatesProcessAndRejectsLaterPause()
    {
        using var process = RepositoryTestContext.StartTestHelper("wait");
        using var active = new ActiveConversionProcess(process, suspendProcess: _ =>
            new CallbackDisposable(() => throw new InvalidOperationException("Resume failed.")));
        try
        {
            Assert.True(active.TrySuspend());

            active.TryTerminate();

            Assert.False(active.TrySuspend());
            Assert.False(active.TryResume());
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
