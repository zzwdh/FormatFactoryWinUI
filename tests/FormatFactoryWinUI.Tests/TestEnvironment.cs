using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

internal static class TestEnvironment
{
    internal static string AppDataRoot { get; } = Path.Combine(
        Path.GetTempPath(), "FormatFactoryWinUI.Tests", $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsWindows())
        {
            // Native fixtures inherit the process error mode. Preserve crash exit codes
            // without a Windows error dialog holding an unattended test run open.
            const uint failCriticalErrors = 0x0001;
            const uint noGpFaultErrorBox = 0x0002;
            const uint noOpenFileErrorBox = 0x8000;
            SetErrorMode(GetErrorMode() | failCriticalErrors | noGpFaultErrorBox | noOpenFileErrorBox);
        }
        LogService.ConfigureAppDataRoot(AppDataRoot);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
