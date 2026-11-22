namespace FormatFactoryWinUI.Services;

/// <summary>Classifies failures before conversion results are translated into display messages.</summary>
public static class ConversionFailurePolicy
{
    /// <summary>Only temporary sharing, lock and network IO faults are automatically retryable.</summary>
    public static bool IsTransient(Exception? exception)
    {
        if (exception is IOException && (exception.HResult & 0xffff) is 32 or 33 or 53 or 54 or 59 or 64 or 67 or 121 or 170 or 1236)
            return true;
        return exception?.InnerException is { } inner && IsTransient(inner);
    }
}
