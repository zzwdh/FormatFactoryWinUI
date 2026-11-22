namespace FormatFactoryWinUI.Services;

/// <summary>Allows initialization to continue after an optional settings write fails.</summary>
internal static class StartupSettingsPersistence
{
    // Callers retain/report the failure; a non-null result never means settings were saved.
    // Manual saves use the normal transactional path and still propagate failures.
    internal static async Task<Exception?> TrySaveAsync(Func<Task> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        try
        {
            await persist();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return exception;
        }
    }
}