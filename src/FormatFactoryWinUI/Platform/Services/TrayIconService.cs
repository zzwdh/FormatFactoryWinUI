using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetFocus = 0x00000003;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifShowTip = 0x00000080;
    private const uint NiifInfo = 0x00000001;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmCommand = 0x0111;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint TpmReturnCommand = 0x0100;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmRightAlign = 0x0008;
    private const uint TpmLayoutRtl = 0x8000;
    private const uint MfString = 0x0000;
    private const uint MfPopup = 0x0010;
    private const uint MfSeparator = 0x0800;
    private const int GwlpWndProc = -4;

    private static readonly (uint Command, string Target, string LabelKey)[] ConversionDestinations =
    [
        (1101, "video", "NavVideo"),
        (1102, "audio", "NavAudio"),
        (1103, "image", "NavImage"),
        (1104, "document", "NavDocument"),
        (1105, "ebook", "NavEbook"),
        (1106, "disc", "NavDisc"),
        (1107, "archive", "ArchiveTitle")
    ];

    private readonly IntPtr _windowHandle;
    private readonly WindowProcedure _windowProcedure;
    private readonly IntPtr _originalWindowProcedure;
    private readonly uint _taskbarCreatedMessage;
    private readonly uint _taskbarButtonCreatedMessage;
    private NotifyIconData _data;
    private bool _disposed;
    private bool _version4;
    private bool _menuOpen;

    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<string>? NavigationRequested;
    public event EventHandler? TaskbarButtonCreated;
    public event EventHandler<int>? CommandReceived;

    public TrayIconService(IntPtr windowHandle, string iconPath)
    {
        _windowHandle = windowHandle;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _taskbarButtonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
        _windowProcedure = WindowProc;
        _originalWindowProcedure = SetWindowLongPtr(_windowHandle, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_windowProcedure));

        var icon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _windowHandle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = CallbackMessage,
            hIcon = icon,
            szTip = LocalizationService.T("AppTitle")
        };
        TryNotify(NimAdd);
    }

    public void RefreshLocalization()
    {
        if (_disposed) return;
        _data.szTip = LocalizationService.T("AppTitle");
        _data.uFlags = NifTip | NifShowTip;
        TryNotify(NimModify);
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed) return;
        _data.uFlags = NifInfo;
        _data.szInfoTitle = title;
        _data.szInfo = message;
        _data.dwInfoFlags = NiifInfo;
        _data.uTimeoutOrVersion = 5000;
        TryNotify(NimModify);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryNotify(NimDelete);
        if (_originalWindowProcedure != IntPtr.Zero)
        {
            SetWindowLongPtr(_windowHandle, GwlpWndProc, _originalWindowProcedure);
        }
        if (_data.hIcon != IntPtr.Zero)
        {
            DestroyIcon(_data.hIcon);
        }
    }

    private bool TryNotify(uint message)
    {
        try
        {
            // NIM_MODIFY may update only the tooltip or balloon. A new Explorer
            // process needs the complete registration, never that partial mask.
            var data = _data;
            if (message == NimAdd) data.uFlags = NifMessage | NifIcon | NifTip | NifShowTip;
            var succeeded = Shell_NotifyIcon(message, ref data);
            if (message == NimAdd)
            {
                // Explorer forgets the callback version when it restarts.
                data.uTimeoutOrVersion = NotifyIconVersion4;
                _version4 = succeeded && Shell_NotifyIcon(NimSetVersion, ref data);
            }
            return succeeded;
        }
        catch
        {
            return false;
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            TryNotify(NimAdd);
            return IntPtr.Zero;
        }

        if (message == _taskbarButtonCreatedMessage)
        {
            TaskbarButtonCreated?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        if (message == WmCommand)
        {
            CommandReceived?.Invoke(this, unchecked((int)(wParam.ToInt64() & 0xFFFF)));
            return IntPtr.Zero;
        }

        if (message == CallbackMessage && !_disposed)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (_version4) mouseMessage &= 0xFFFF;
            if ((_version4 && mouseMessage is NinSelect or NinKeySelect)
                || (!_version4 && mouseMessage == WmLButtonUp)
                || mouseMessage == NinBalloonUserClick)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            }
            if ((_version4 && mouseMessage == WmContextMenu)
                || (!_version4 && mouseMessage == WmRButtonUp))
            {
                var point = _version4
                    ? new Point { X = unchecked((short)wParam.ToInt64()), Y = unchecked((short)(wParam.ToInt64() >> 16)) }
                    : new Point { X = -1, Y = -1 };
                ShowContextMenu(point);
                return IntPtr.Zero;
            }
        }

        return CallWindowProc(_originalWindowProcedure, hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu(Point point)
    {
        if (_disposed || _menuOpen) return;
        var menu = CreateContextMenu();
        if (menu == IntPtr.Zero) return;

        uint command;
        _menuOpen = true;
        try
        {
            if (point.X == -1 && point.Y == -1)
            {
                var identifier = new NotifyIconIdentifier
                {
                    cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
                    hWnd = _windowHandle,
                    uID = _data.uID
                };
                if (Shell_NotifyIconGetRect(ref identifier, out var rect) == 0)
                    point = new Point { X = rect.Right, Y = rect.Bottom };
                else GetCursorPos(out point);
            }
            SetForegroundWindow(_windowHandle);
            var flags = TpmReturnCommand | TpmNoNotify | TpmRightButton | TpmRightAlign;
            if (LocalizationService.IsRightToLeft) flags |= TpmLayoutRtl;
            command = TrackPopupMenu(menu, flags, point.X, point.Y, 0, _windowHandle, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
            _menuOpen = false;
            // Let the shell dismiss the menu reliably on the next invocation.
            PostMessage(_windowHandle, 0, IntPtr.Zero, IntPtr.Zero);
        }

        if (_disposed) return;
        if (command == 0) TryNotify(NimSetFocus);
        else if (command == 1001) ShowRequested?.Invoke(this, EventArgs.Empty);
        else if (command == 1002) ExitRequested?.Invoke(this, EventArgs.Empty);
        else if (NavigationTargetForCommand(command) is { } target)
            NavigationRequested?.Invoke(this, target);
    }

    internal static IntPtr CreateContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return IntPtr.Zero;
        var conversions = IntPtr.Zero;
        try
        {
            conversions = CreatePopupMenu();
            if (conversions == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            AddMenuItem(menu, 1001, "TrayOpenWindow");
            AddSeparator(menu);
            AddMenuItem(menu, 1010, "QueueTitle");
            foreach (var destination in ConversionDestinations)
                AddMenuItem(conversions, destination.Command, destination.LabelKey);
            if (!AppendMenu(menu, MfPopup, (nuint)conversions, MenuLabel("TrayConversions")))
                throw new System.ComponentModel.Win32Exception();
            conversions = IntPtr.Zero; // The root menu now owns its submenu.
            AddSeparator(menu);
            AddMenuItem(menu, 1020, "NavHistory");
            AddMenuItem(menu, 1021, "AutomationTitle");
            AddMenuItem(menu, 1022, "NavSettings");
            AddSeparator(menu);
            AddMenuItem(menu, 1002, "TrayExit");
            SetMenuDefaultItem(menu, 1001, false);
            return menu;
        }
        catch
        {
            if (conversions != IntPtr.Zero) DestroyMenu(conversions);
            DestroyMenu(menu);
            return IntPtr.Zero;
        }
    }

    internal static string? NavigationTargetForCommand(uint command) => command switch
    {
        1010 => "all",
        1020 => "history",
        1021 => "automation",
        1022 => "settings",
        _ => ConversionDestinations.FirstOrDefault(destination => destination.Command == command).Target
    };

    private static string MenuLabel(string key) => LocalizationService.T(key).Replace("&", "&&", StringComparison.Ordinal);

    private static void AddMenuItem(IntPtr menu, uint command, string labelKey)
    {
        if (!AppendMenu(menu, MfString, command, MenuLabel(labelKey)))
            throw new System.ComponentModel.Win32Exception();
    }

    private static void AddSeparator(IntPtr menu)
    {
        if (!AppendMenu(menu, MfSeparator, 0, null))
            throw new System.ComponentModel.Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    private delegate IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, bool byPosition);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);
}
