using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public static class NativeWindowService
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const uint MbIconAsterisk = 0x00000040;

    public static void Hide(IntPtr windowHandle) => ShowWindow(windowHandle, SwHide);

    public static void Show(IntPtr windowHandle)
    {
        ShowWindow(windowHandle, SwShow);
        SetForegroundWindow(windowHandle);
    }

    public static void PlayNotificationSound() => MessageBeep(MbIconAsterisk);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr windowHandle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr windowHandle);
    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
}
