using System.Diagnostics;

namespace FormatFactoryWinUI.Services;

/// <summary>Diagnostics must never alter a conversion result or prevent cleanup.</summary>
public static class SafeDiagnostics
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, object> NotifiedErrors = new();
    public static event Action<Exception>? ErrorReported;

    public static void Write(string message)
    {
        try { LogService.WriteApp(message); }
        catch (Exception exception) { Debug.WriteLine(exception); }
    }

    public static void Report(Exception exception, Action<Exception>? report = null, bool notifySubscribers = true)
    {
        try { (report ?? LogService.WriteAppError)(exception); }
        catch (Exception diagnosticError)
        {
            Debug.WriteLine(exception);
            Debug.WriteLine(diagnosticError);
        }
        // Conversion completion supplies the final error and task log separately.
        // Activation and global exception callbacks can surface the same object more than once.
        // Local logging is preserved on every call; weak keys avoid retaining exception graphs.
        if (!notifySubscribers || !NotifiedErrors.TryAdd(exception, new object())) return;
        // A failing diagnostic subscriber must never affect the original operation.
        foreach (var subscriber in ErrorReported?.GetInvocationList() ?? [])
        {
            try { ((Action<Exception>)subscriber)(exception); }
            catch (Exception diagnosticError) { Debug.WriteLine(diagnosticError); }
        }
    }

    public static void Flush()
    {
        try { LogService.FlushAll(); }
        catch (Exception exception) { Debug.WriteLine(exception); }
    }
}
