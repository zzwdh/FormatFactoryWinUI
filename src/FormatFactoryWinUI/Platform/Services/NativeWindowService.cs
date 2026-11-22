using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public static partial class NativeWindowService
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint MbIconAsterisk = 0x00000040;

    public static void Hide(IntPtr windowHandle) => ShowWindow(windowHandle, SwHide);

    public static void Show(IntPtr windowHandle)
    {
        ShowWindow(windowHandle, IsIconic(windowHandle) ? SwRestore : SwShow);
        SetForegroundWindow(windowHandle);
    }

    public static void PlayNotificationSound() => MessageBeep(MbIconAsterisk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr windowHandle, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr windowHandle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr windowHandle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
