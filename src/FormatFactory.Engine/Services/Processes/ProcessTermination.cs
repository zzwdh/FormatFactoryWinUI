using System.ComponentModel;

namespace FormatFactoryWinUI.Services;

/// <summary>Expected OS termination failures must not escape a cancellation callback.</summary>
internal static class ProcessTermination
{
    internal static bool TryTerminate(Action terminate)
    {
        try
        {
            terminate();
            return true;
        }
        catch (Exception error) when (IsTerminationFailure(error))
        {
            var failures = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [error];
            SafeDiagnostics.Write(FormattableString.Invariant(
                $"Process termination observation: operation={Guid.NewGuid():N} result=failed failures={failures.Length} {RuntimeFailureDiagnostics.ExceptionFields(failures[0])}\n"));
            return false;
        }
    }

    private static bool IsTerminationFailure(Exception error) => error is InvalidOperationException or Win32Exception ||
        error is AggregateException aggregate && aggregate.Flatten().InnerExceptions is { Count: > 0 } failures &&
        failures.All(failure => failure is InvalidOperationException or Win32Exception);
}
