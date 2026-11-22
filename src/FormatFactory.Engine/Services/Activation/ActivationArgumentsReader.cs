using System.Runtime.InteropServices;
using System.Diagnostics;

namespace FormatFactoryWinUI.Services;

/// <summary>Reads optional activation data with bounded broker recovery and ordinary launch fallback.</summary>
internal static class ActivationArgumentsReader
{
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static async Task<T?> ReadAsync<T>(Func<T?> read, Action<Exception> reportFailure,
        Func<TimeSpan, Task>? delay = null, Guid? diagnosticOperation = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(reportFailure);
        delay ??= Task.Delay;

        Exception? failure = null;
        var operation = diagnosticOperation ?? Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            T? result;
            try
            {
                result = read();
            }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x8000FFFF))
            {
                // Do not treat this activation-context failure as a transient timeout. Preserve its
                // diagnostic and let the caller try ordinary launch arguments without retrying.
                RuntimeFailureDiagnostics.Record(exception, new(RuntimeFailureStage.ActivationRead, operation, attempt + 1,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, false));
                reportFailure(exception);
                return null;
            }
            catch (Exception exception) when (IsTransientBrokerFailure(exception))
            {
                failure = exception;
                if (attempt == 0)
                {
                    await delay(RetryDelay);
                    continue;
                }
                // The caller can use LaunchActivatedEventArgs.Arguments while retaining diagnostics.
                RuntimeFailureDiagnostics.Record(failure, new(RuntimeFailureStage.ActivationRead, operation, attempt + 1,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, false));
                reportFailure(failure);
                return null;
            }
            if (failure is not null)
            {
                RuntimeFailureDiagnostics.Record(failure, new(RuntimeFailureStage.ActivationRead, operation, attempt + 1,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, result is not null));
                reportFailure(failure);
            }
            return result;
        }
        return null;
    }

    private static bool IsTransientBrokerFailure(Exception exception) =>
        exception is TimeoutException ||
        exception is COMException { HResult: unchecked((int)0x800706BA) };
}
