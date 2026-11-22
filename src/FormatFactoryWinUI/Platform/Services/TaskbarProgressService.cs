using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

internal sealed class TaskbarProgressService : IDisposable
{
    public const int PauseButtonId = 1;
    public const int CancelButtonId = 2;
    public const int OpenOutputButtonId = 3;

    private readonly ITaskbarList3? _taskbar;
    private readonly IntPtr _windowHandle;
    private bool _buttonsAdded;
    private bool _disposed;
    private IReadOnlyCollection<(double Progress, bool Running, bool Paused, bool Failed)> _lastTasks = [];
    private bool _lastHasOutput;
    private int _lastStopCount;

    public event EventHandler<int>? ButtonClicked;

    public TaskbarProgressService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogTaskbarProgressInitializationFailed", ex.Message));
        }
    }

    public void ConfigureButtons()
    {
        if (_disposed || _taskbar is null || _buttonsAdded)
        {
            return;
        }

        try
        {
            var icon = LoadIcon(IntPtr.Zero, (IntPtr)32512);
            var buttons = new[]
            {
                CreateButton(PauseButtonId, icon, LocalizationService.T("PauseAll"), ThumbButtonFlags.Disabled),
                CreateButton(CancelButtonId, icon, LocalizationService.T("ConversionUiQueueStopCount", 0), ThumbButtonFlags.Disabled),
                CreateButton(OpenOutputButtonId, icon, LocalizationService.T("OpenOutput"), ThumbButtonFlags.Disabled)
            };
            _taskbar.ThumbBarAddButtons(_windowHandle, (uint)buttons.Length, buttons);
            _buttonsAdded = true;
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogTaskbarThumbnailButtonsInitializationFailed", ex.Message));
        }
    }

    public void ResetForTaskbarRecreation()
    {
        if (_disposed) return;
        _buttonsAdded = false;
        ConfigureButtons();
        Update(_lastTasks, _lastHasOutput, _lastStopCount);
    }

    public void HandleWindowCommand(IntPtr wParam)
    {
        if (_disposed) return;
        var commandId = unchecked((int)(wParam.ToInt64() & 0xFFFF));
        if (commandId is PauseButtonId or CancelButtonId or OpenOutputButtonId)
        {
            ButtonClicked?.Invoke(this, commandId);
        }
    }

    public void Update(IReadOnlyCollection<(double Progress, bool Running, bool Paused, bool Failed)> tasks, bool hasOutput = false, int stopCount = 0)
    {
        if (_disposed) return;
        _lastTasks = tasks.ToArray();
        _lastHasOutput = hasOutput;
        _lastStopCount = stopCount;
        if (_taskbar is null || tasks.Count == 0)
        {
            Clear();
            UpdateButtons(false, stopCount, hasOutput);
            return;
        }

        try
        {
            if (tasks.Any(task => task.Failed))
            {
                _taskbar.SetProgressState(_windowHandle, TaskbarProgressState.Error);
            }
            else if (tasks.Any(task => task.Paused))
            {
                _taskbar.SetProgressState(_windowHandle, TaskbarProgressState.Paused);
            }
            else if (tasks.Any(task => task.Running))
            {
                _taskbar.SetProgressState(_windowHandle, TaskbarProgressState.Normal);
            }
            else
            {
                _taskbar.SetProgressState(_windowHandle, TaskbarProgressState.NoProgress);
            }

            var value = Math.Clamp(tasks.Average(task => task.Progress), 0, 100);
            _taskbar.SetProgressValue(_windowHandle, (ulong)Math.Round(value), 100);
            UpdateButtons(tasks.Any(task => task.Running || task.Paused), stopCount, hasOutput);
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogTaskbarProgressUpdateFailed", ex.Message));
        }
    }

    private void UpdateButtons(bool canPause, int stopCount, bool canOpenOutput)
    {
        if (!_buttonsAdded || _taskbar is null)
        {
            return;
        }

        try
        {
            var icon = LoadIcon(IntPtr.Zero, (IntPtr)32512);
            var buttons = new[]
            {
                CreateButton(PauseButtonId, icon, LocalizationService.T("PauseAll"), canPause ? ThumbButtonFlags.Enabled : ThumbButtonFlags.Disabled),
                CreateButton(CancelButtonId, icon, LocalizationService.T("ConversionUiQueueStopCount", stopCount), stopCount > 0 ? ThumbButtonFlags.Enabled : ThumbButtonFlags.Disabled),
                CreateButton(OpenOutputButtonId, icon, LocalizationService.T("OpenOutput"), canOpenOutput ? ThumbButtonFlags.Enabled : ThumbButtonFlags.Disabled)
            };
            _taskbar.ThumbBarUpdateButtons(_windowHandle, (uint)buttons.Length, buttons);
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogTaskbarThumbnailButtonsUpdateFailed", ex.Message));
        }
    }

    private static ThumbButton CreateButton(int id, IntPtr icon, string tooltip, ThumbButtonFlags flags = ThumbButtonFlags.Enabled) =>
        new()
        {
            Mask = ThumbButtonMask.Icon | ThumbButtonMask.Tooltip | ThumbButtonMask.Flags,
            Id = (uint)id,
            Icon = icon,
            Tooltip = tooltip,
            Flags = flags
        };

    public void Clear()
    {
        if (_disposed) return;
        try
        {
            _taskbar?.SetProgressState(_windowHandle, TaskbarProgressState.NoProgress);
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogTaskbarProgressClearFailed", ex.Message));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
        _buttonsAdded = false;
        _lastTasks = [];
        ButtonClicked = null;
        if (_taskbar is not null && Marshal.IsComObject(_taskbar))
        {
            Marshal.ReleaseComObject(_taskbar);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
}

[ComImport]
[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);
    void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
    void SetProgressState(IntPtr hwnd, TaskbarProgressState state);
    void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
    void UnregisterTab(IntPtr hwndTab);
    void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
    void SetTabActive(IntPtr hwndTab, IntPtr hwndInsert, uint reserved);
    void ThumbBarAddButtons(IntPtr hwnd, uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] buttons);
    void ThumbBarUpdateButtons(IntPtr hwnd, uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] buttons);
    void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
    void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string description);
    void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tooltip);
    void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    void SetTabProperties(IntPtr hwndTab, uint flags);
}

[ComImport]
[Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
internal class TaskbarList
{
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ThumbButton
{
    public ThumbButtonMask Mask;
    public uint Id;
    public uint Bitmap;
    public IntPtr Icon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string Tooltip;
    public ThumbButtonFlags Flags;
}

[Flags]
internal enum ThumbButtonMask : uint
{
    Bitmap = 1,
    Icon = 2,
    Tooltip = 4,
    Flags = 8
}

[Flags]
internal enum ThumbButtonFlags : uint
{
    Enabled = 0,
    Disabled = 1,
    Hidden = 8,
    NoBackground = 2,
    NonInteractive = 16
}

internal enum TaskbarProgressState : uint
{
    NoProgress = 0,
    Indeterminate = 1,
    Normal = 2,
    Error = 4,
    Paused = 8
}
