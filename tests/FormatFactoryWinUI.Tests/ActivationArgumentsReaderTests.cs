using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ActivationArgumentsReaderTests
{
    [Fact]
    public async Task SuccessfulRead_PreservesActivationWithoutDelayOrError()
    {
        var activation = new object();
        var result = await ActivationArgumentsReader.ReadAsync(() => activation,
            _ => Assert.Fail("A successful read should not report a failure."),
            _ => throw new InvalidOperationException("A successful read should not retry."));

        Assert.Same(activation, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientBrokerFailure_RetriesAndPreservesRecoveredActivation(bool rpcFailure)
    {
        var activation = new object();
        var failure = BrokerFailure(rpcFailure);
        var attempts = 0;
        var delays = 0;
        var reports = new List<Exception>();
        var result = await ActivationArgumentsReader.ReadAsync(() =>
        {
            if (++attempts == 1) throw failure;
            return activation;
        }, reports.Add, delay =>
        {
            Assert.Equal(ActivationArgumentsReader.RetryDelay, delay);
            delays++;
            return Task.CompletedTask;
        });

        Assert.Same(activation, result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, delays);
        Assert.Same(failure, Assert.Single(reports));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentBrokerFailure_AllowsLaunchArgumentFallbackAndReportsFinalError(bool rpcFailure)
    {
        var attempts = 0;
        var reports = new List<Exception>();
        Exception? finalFailure = null;
        var result = await ActivationArgumentsReader.ReadAsync<object>(() =>
        {
            attempts++;
            finalFailure = BrokerFailure(rpcFailure);
            throw finalFailure;
        }, reports.Add, _ => Task.CompletedTask);

        Assert.Null(result);
        Assert.Equal(2, attempts);
        Assert.Same(finalFailure, Assert.Single(reports));
        // App.OnLaunched feeds its ordinary arguments to this existing fallback path.
        var fallback = ActivationArgumentParser.FromLaunchArguments("--open-settings", new HashSet<string>());
        Assert.True(fallback.OpenSettings);
    }

    [Fact]
    public async Task UnexpectedBrokerFailure_AllowsFallbackWithoutRetryAndReportsOriginalError()
    {
        var failure = UnexpectedBrokerFailure();
        var attempts = 0;
        var reports = new List<Exception>();
        var result = await ActivationArgumentsReader.ReadAsync<object>(() =>
        {
            attempts++;
            throw failure;
        }, reports.Add, _ => throw new InvalidOperationException("An unexpected broker failure must not retry."));

        Assert.Null(result);
        Assert.Equal(1, attempts);
        Assert.Same(failure, Assert.Single(reports));
        var fallback = ActivationArgumentParser.FromLaunchArguments("--open-settings", new HashSet<string>());
        Assert.True(fallback.OpenSettings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientThenUnexpectedBrokerFailure_StopsAndReportsFinalError(bool rpcFailure)
    {
        var initialFailure = BrokerFailure(rpcFailure);
        var finalFailure = UnexpectedBrokerFailure();
        var attempts = 0;
        var delays = 0;
        var reports = new List<Exception>();
        var result = await ActivationArgumentsReader.ReadAsync<object>(() =>
        {
            if (++attempts == 1) throw initialFailure;
            throw finalFailure;
        }, reports.Add, delay =>
        {
            Assert.Equal(ActivationArgumentsReader.RetryDelay, delay);
            delays++;
            return Task.CompletedTask;
        });

        Assert.Null(result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, delays);
        Assert.Same(finalFailure, Assert.Single(reports));
    }

    [Fact]
    public async Task UnrelatedComFailure_IsPropagatedWithoutRetry()
    {
        var failure = new COMException("Synthetic access failure", unchecked((int)0x80070005));
        var attempts = 0;
        var caught = await Assert.ThrowsAsync<COMException>(() => ActivationArgumentsReader.ReadAsync<object>(() =>
        {
            attempts++;
            throw failure;
        }, _ => Assert.Fail("An unrelated failure must propagate."),
            _ => throw new InvalidOperationException("An unrelated failure must not retry.")));

        Assert.Same(failure, caught);
        Assert.Equal(1, attempts);
    }

    private static Exception BrokerFailure(bool rpcFailure) => rpcFailure
        ? new COMException("Synthetic broker unavailable", unchecked((int)0x800706BA))
        : new TimeoutException("Synthetic broker timeout");

    private static COMException UnexpectedBrokerFailure() =>
        new("Synthetic activation context failure", unchecked((int)0x8000FFFF));
}
