using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class SafeDiagnosticsTests
{
    [Fact]
    public void Report_RepeatedExceptionNotifiesOnceWhilePreservingEveryLocalWrite()
    {
        var error = new InvalidOperationException("synthetic repeated activation error");
        var writes = 0;
        var notifications = 0;
        void Subscriber(Exception value)
        {
            if (ReferenceEquals(value, error)) Interlocked.Increment(ref notifications);
        }
        SafeDiagnostics.ErrorReported += Subscriber;
        try
        {
            // A deferred conversion report must not consume the notification identity.
            SafeDiagnostics.Report(error, _ => writes++, notifySubscribers: false);
            SafeDiagnostics.Report(error, _ => writes++);
            SafeDiagnostics.Report(error, _ => writes++);
            Assert.Equal(3, writes);
            Assert.Equal(1, notifications);
        }
        finally { SafeDiagnostics.ErrorReported -= Subscriber; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Report_PreservesLocalDiagnosticsWhenNotificationIsSuppressed(bool notify)
    {
        var error = new InvalidOperationException("synthetic diagnostic");
        var writes = 0;
        var notifications = 0;
        void Subscriber(Exception value)
        {
            if (ReferenceEquals(value, error)) Interlocked.Increment(ref notifications);
        }
        SafeDiagnostics.ErrorReported += Subscriber;
        try
        {
            SafeDiagnostics.Report(error, value =>
            {
                Assert.Same(error, value);
                writes++;
            }, notifySubscribers: notify);
            Assert.Equal(1, writes);
            Assert.Equal(notify ? 1 : 0, notifications);
        }
        finally { SafeDiagnostics.ErrorReported -= Subscriber; }
    }

    [Fact]
    public void Report_FailingLoggerAndSubscriberDoNotChangeOriginalOperation()
    {
        var error = new InvalidOperationException("synthetic diagnostic failure");
        var received = false;
        void Failing(Exception value)
        {
            if (ReferenceEquals(value, error)) throw new IOException("synthetic subscriber failure");
        }
        void Receiving(Exception value)
        {
            if (ReferenceEquals(value, error)) received = true;
        }
        SafeDiagnostics.ErrorReported += Failing;
        SafeDiagnostics.ErrorReported += Receiving;
        try
        {
            SafeDiagnostics.Report(error, _ => throw new IOException("synthetic logger failure"));
            Assert.True(received);
        }
        finally
        {
            SafeDiagnostics.ErrorReported -= Failing;
            SafeDiagnostics.ErrorReported -= Receiving;
        }
    }
}
