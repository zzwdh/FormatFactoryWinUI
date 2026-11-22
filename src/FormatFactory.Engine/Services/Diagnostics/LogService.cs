using System.Text;

namespace FormatFactoryWinUI.Services;

public static class LogService
{
    private const int TaskLogFlushThreshold = 8 * 1024;
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, StringBuilder> TaskBuffers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IDisposable> TaskProtections = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<string> LogsDirectoryPath = new(() =>
        Directory.CreateDirectory(Path.Combine(AppDataRoot, "Logs")).FullName);
    private static readonly Lazy<BufferedLogWriter> WriterInstance = new(() => new BufferedLogWriter());
    private static BufferedLogWriter Writer => WriterInstance.Value;

    public static string AppDataRoot => AppDataPaths.Root;
    public static string LogsDirectory => LogsDirectoryPath.Value;
    public static string AppLogPath => Path.Combine(LogsDirectory, "application.log");
    public static Exception? LastWriteError => WriterInstance.IsValueCreated ? Writer.LastWriteError : null;

    public static void ConfigureAppDataRoot(string root, bool migrateLegacyData = false) =>
        AppDataPaths.Configure(root, migrateLegacyData);

    public static string CreateTaskLogPath(Guid taskId) =>
        Path.Combine(LogsDirectory, $"task-{DateTime.Now:yyyyMMdd-HHmmss}-{taskId:N}.log");

    public static void WriteApp(string message) =>
        Writer.Write(AppLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");

    public static void WriteAppError(Exception exception)
    {
        try
        {
            WriteApp(RuntimeFailureDiagnostics.Format(exception));
            if (StorageFailureDiagnostics.Format(exception) is { } storage) WriteApp(storage);
            if (FormatFailureDiagnostics.Format(exception) is { } format) WriteApp(format);
            if (PersistenceFailureDiagnostics.Format(exception) is { } persistence) WriteApp(persistence);
            var detail = LocalizationService.T("OperationalLogUnhandledException", exception);
            WriteApp(string.Join('\n', detail.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                .Split('\n').Select(line => "| " + line)));
            Writer.Flush(AppLogPath);
        }
        catch (Exception diagnosticError)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            System.Diagnostics.Debug.WriteLine(diagnosticError);
        }
    }

    public static void AppendTask(string logPath, string content)
    {
        lock (SyncRoot)
        {
            if (!TaskBuffers.TryGetValue(logPath, out var buffer))
            {
                buffer = new StringBuilder();
                TaskBuffers[logPath] = buffer;
                TaskProtections[logPath] = LogFilePolicy.Shared.Protect(logPath);
            }

            buffer.Append(content);
            if (buffer.Length >= TaskLogFlushThreshold)
            {
                FlushTaskLocked(logPath, buffer);
            }
        }
    }

    public static void FlushTask(string logPath)
    {
        IDisposable? protection;
        lock (SyncRoot)
        {
            if (TaskBuffers.Remove(logPath, out var buffer))
            {
                FlushTaskLocked(logPath, buffer);
            }
            TaskProtections.Remove(logPath, out protection);
        }
        // Wait outside the task-buffer lock; the worker never needs this lock.
        try { Writer.Flush(logPath); }
        finally { protection?.Dispose(); }
    }

    public static void FlushAll()
    {
        IDisposable[] protections;
        lock (SyncRoot)
        {
            foreach (var pair in TaskBuffers.ToArray())
            {
                FlushTaskLocked(pair.Key, pair.Value);
            }
            TaskBuffers.Clear();
            protections = TaskProtections.Values.ToArray();
            TaskProtections.Clear();
        }
        try { if (WriterInstance.IsValueCreated) Writer.Flush(); }
        finally { foreach (var protection in protections) protection.Dispose(); }
    }

    internal static void FlushApp()
    {
        // Automatic feedback runs on a worker. Drain earlier app writes without
        // flushing or rotating the logs of conversions that are still running.
        try { if (WriterInstance.IsValueCreated) Writer.Flush(AppLogPath); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
    }

    private static void FlushTaskLocked(string logPath, StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        Writer.Write(logPath, buffer.ToString());
        buffer.Clear();
    }
}
