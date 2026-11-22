using System.ComponentModel;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ProcessTerminationTests
{
    [Fact]
    public void CancellationCallback_NestedAccessDeniedDoesNotEscape()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new AggregateException(new AggregateException(new Win32Exception(5, "synthetic private detail")));
        bool? attempted = null;
        using var registration = cancellation.Token.Register(() =>
            attempted = ProcessTermination.TryTerminate(() => throw failure));

        cancellation.Cancel();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(attempted);
    }

    [Fact]
    public void ConcurrentExit_IsBestEffort()
    {
        Assert.False(ProcessTermination.TryTerminate(() => throw new InvalidOperationException("synthetic exit race")));
        Assert.False(ProcessTermination.TryTerminate(() => throw new Win32Exception(5)));
    }

    [Fact]
    public void UnrelatedAggregate_IsNotSuppressed()
    {
        var failure = new AggregateException(new Win32Exception(5), new InvalidDataException("synthetic bug"));
        Assert.Same(failure, Assert.Throws<AggregateException>(() => ProcessTermination.TryTerminate(() => throw failure)));
    }

    [Fact]
    public void UnexpectedFailure_IsNotSuppressed()
    {
        var failure = new NullReferenceException("synthetic bug");
        Assert.Same(failure, Assert.Throws<NullReferenceException>(() => ProcessTermination.TryTerminate(() => throw failure)));
    }

    [Fact]
    public void SuccessfulRequest_IsInvokedOnce()
    {
        var calls = 0;
        Assert.True(ProcessTermination.TryTerminate(() => calls++));
        Assert.Equal(1, calls);
    }
}
