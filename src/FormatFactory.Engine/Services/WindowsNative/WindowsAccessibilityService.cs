using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FormatFactoryWinUI.Services;

public sealed record WindowsAccessibilitySnapshot(
    bool HighContrastEnabled,
    string? HighContrastScheme,
    double TextScaleFactor,
    uint WindowColorArgb,
    uint WindowTextColorArgb,
    uint HighlightColorArgb,
    uint AccentColorArgb,
    DateTimeOffset CapturedAt)
{
    public string ToLocalizedSummary() =>
        LocalizationService.T(
            "NativeCapabilityAccessibilitySummary",
            LocalizationService.T(
                HighContrastEnabled
                    ? "NativeCapabilityEnabled"
                    : "NativeCapabilityDisabled"),
            TextScaleFactor);

    /// <summary>
    /// Compatibility wrapper retained for existing callers. The returned text follows the
    /// currently selected application language.
    /// </summary>
    public string ToChineseSummary() => ToLocalizedSummary();
}

public sealed class WindowsAccessibilityChangedEventArgs : EventArgs
{
    public WindowsAccessibilityChangedEventArgs(
        WindowsAccessibilitySnapshot current,
        WindowsAccessibilitySnapshot? previous)
    {
        Current = current;
        Previous = previous;
    }

    public WindowsAccessibilitySnapshot Current { get; }

    public WindowsAccessibilitySnapshot? Previous { get; }
}

public interface IWindowsAccessibilityPlatform
{
    bool TryCapture(out WindowsAccessibilitySnapshot snapshot);
}

/// <summary>
/// Watches high contrast, system colors and the Windows accessibility text scale.
/// </summary>
public sealed class WindowsAccessibilityService : IDisposable
{
    private readonly object _gate = new();
    private readonly IWindowsAccessibilityPlatform _platform;
    private Timer? _timer;
    private SynchronizationContext? _eventContext;
    private WindowsAccessibilitySnapshot? _current;
    private bool _running;
    private bool _disposed;

    public WindowsAccessibilityService()
        : this(new NativeWindowsAccessibilityPlatform())
    {
    }

    internal WindowsAccessibilityService(IWindowsAccessibilityPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public event EventHandler<WindowsAccessibilityChangedEventArgs>? Changed;

    public WindowsAccessibilitySnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public void Start(TimeSpan? refreshInterval = null)
    {
        var interval = refreshInterval ?? TimeSpan.FromSeconds(2);
        if (interval < TimeSpan.FromMilliseconds(50))
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _running = true;
            _eventContext = SynchronizationContext.Current;
            _timer = new Timer(
                static state => ((WindowsAccessibilityService)state!).Refresh(),
                this,
                interval,
                interval);
        }

        Refresh();
    }

    public bool Refresh()
    {
        if (!_platform.TryCapture(out var snapshot))
        {
            return false;
        }

        WindowsAccessibilitySnapshot? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            previous = _current;
            _current = snapshot;
        }

        if (HasSamePreferences(previous, snapshot))
        {
            return true;
        }

        DispatchChanged(new WindowsAccessibilityChangedEventArgs(snapshot, previous));
        return true;
    }

    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            timer = _timer;
            _timer = null;
            _eventContext = null;
        }
        timer?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        Stop();
        lock (_gate)
        {
            _disposed = true;
        }
    }

    internal static bool HasSamePreferences(
        WindowsAccessibilitySnapshot? left,
        WindowsAccessibilitySnapshot right) =>
        left is not null &&
        left.HighContrastEnabled == right.HighContrastEnabled &&
        string.Equals(left.HighContrastScheme, right.HighContrastScheme, StringComparison.Ordinal) &&
        Math.Abs(left.TextScaleFactor - right.TextScaleFactor) < 0.001 &&
        left.WindowColorArgb == right.WindowColorArgb &&
        left.WindowTextColorArgb == right.WindowTextColorArgb &&
        left.HighlightColorArgb == right.HighlightColorArgb &&
        left.AccentColorArgb == right.AccentColorArgb;

    private void DispatchChanged(WindowsAccessibilityChangedEventArgs args)
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        SynchronizationContext? context;
        lock (_gate)
        {
            context = _eventContext;
        }

        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            handler(this, args);
            return;
        }

        context.Post(static state =>
        {
            var dispatch = (AccessibilityDispatch)state!;
            dispatch.Handler(dispatch.Sender, dispatch.Args);
        }, new AccessibilityDispatch(this, handler, args));
    }

    private sealed record AccessibilityDispatch(
        WindowsAccessibilityService Sender,
        EventHandler<WindowsAccessibilityChangedEventArgs> Handler,
        WindowsAccessibilityChangedEventArgs Args);
}

internal sealed partial class NativeWindowsAccessibilityPlatform : IWindowsAccessibilityPlatform
{
    public bool TryCapture(out WindowsAccessibilitySnapshot snapshot)
    {
        snapshot = default!;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var highContrast = new HighContrast
        {
            Size = checked((uint)Marshal.SizeOf<HighContrast>())
        };
        if (!SystemParametersInfo(
                SystemParameterGetHighContrast,
                highContrast.Size,
                ref highContrast,
                0))
        {
            return false;
        }

        snapshot = new WindowsAccessibilitySnapshot(
            (highContrast.Flags & HighContrastOn) != 0,
            highContrast.DefaultScheme == 0
                ? null
                : Marshal.PtrToStringUni(highContrast.DefaultScheme),
            ReadTextScaleFactor(),
            ToArgb(GetSysColor(ColorWindow)),
            ToArgb(GetSysColor(ColorWindowText)),
            ToArgb(GetSysColor(ColorHighlight)),
            ReadAccentColor(),
            DateTimeOffset.UtcNow);
        return true;
    }

    private static double ReadTextScaleFactor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1d;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            var raw = key?.GetValue("TextScaleFactor");
            return raw is not null && double.TryParse(
                Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var percentage)
                ? Math.Max(0.5, percentage / 100d)
                : 1d;
        }
        catch
        {
            return 1d;
        }
    }

    private static uint ReadAccentColor()
    {
        try
        {
            return DwmGetColorizationColor(out var color, out _) == 0
                ? color
                : ToArgb(GetSysColor(ColorHighlight));
        }
        catch (DllNotFoundException)
        {
            return ToArgb(GetSysColor(ColorHighlight));
        }
        catch (EntryPointNotFoundException)
        {
            return ToArgb(GetSysColor(ColorHighlight));
        }
    }

    private static uint ToArgb(uint colorRef)
    {
        var red = colorRef & 0xFF;
        var green = (colorRef >> 8) & 0xFF;
        var blue = (colorRef >> 16) & 0xFF;
        return 0xFF000000u | (red << 16) | (green << 8) | blue;
    }

    private const uint SystemParameterGetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x00000001;
    private const int ColorWindow = 5;
    private const int ColorWindowText = 8;
    private const int ColorHighlight = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref HighContrast value,
        uint updateFlags);

    [LibraryImport("user32.dll")]
    private static partial uint GetSysColor(int index);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetColorizationColor(
        out uint colorizationColor,
        [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);
}
