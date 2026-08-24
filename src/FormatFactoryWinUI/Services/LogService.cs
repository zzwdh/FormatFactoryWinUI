using System.Text;

namespace FormatFactoryWinUI.Services;

public static class LogService
{
    private static readonly object SyncRoot = new();

    public static string AppDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FormatFactoryWinUI");

    public static string LogsDirectory { get; } = Path.Combine(AppDataRoot, "Logs");
    public static string AppLogPath { get; } = Path.Combine(LogsDirectory, "application.log");

    static LogService()
    {
        Directory.CreateDirectory(LogsDirectory);
    }

    public static string CreateTaskLogPath(Guid taskId) =>
        Path.Combine(LogsDirectory, $"task-{DateTime.Now:yyyyMMdd-HHmmss}-{taskId:N}.log");

    public static void WriteApp(string message)
    {
        lock (SyncRoot)
        {
            File.AppendAllText(AppLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
    }

    public static void WriteAppError(Exception exception) => WriteApp($"未处理异常: {exception}");

    public static void AppendTask(string logPath, string content)
    {
        lock (SyncRoot)
        {
            File.AppendAllText(logPath, content, Encoding.UTF8);
        }
    }
}
