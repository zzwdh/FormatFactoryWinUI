using FormatFactoryWinUI.Services;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private readonly WindowPlacementStore _windowPlacementStore = new();
    private SavedWindowPlacement? _lastWindowPlacement;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _placementSaveTimer;

    private async Task RestoreWindowPlacementAsync()
    {
        var saved = await _windowPlacementStore.LoadAsync();
        if (_windowClosed || _appWindow is null) return;
        if (saved is not null)
        {
            var b = saved.Bounds;
            var area = DisplayArea.GetFromRect(new RectInt32(b.X, b.Y, b.Width, b.Height), DisplayAreaFallback.Nearest).WorkArea;
            var fit = WindowPlacementStore.FitToWorkArea(b, new(area.X, area.Y, area.Width, area.Height));
            _appWindow.MoveAndResize(new RectInt32(fit.X, fit.Y, fit.Width, fit.Height));
            _lastWindowPlacement = new(fit, saved.Maximized);
            if (saved.Maximized && _appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
            if (_startInTray) NativeWindowService.Hide(_windowHandle);
        }
        CaptureWindowPlacement();
        _placementSaveTimer = DispatcherQueue.CreateTimer();
        _placementSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _placementSaveTimer.IsRepeating = false;
        _placementSaveTimer.Tick += async (_, _) => await PersistWindowPlacementAsync();
        _appWindow.Changed += WindowPlacement_Changed;
    }

    private void WindowPlacement_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_windowClosed || !(args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)) return;
        CaptureWindowPlacement();
        _placementSaveTimer?.Stop();
        _placementSaveTimer?.Start();
    }

    private void CaptureWindowPlacement()
    {
        if (_appWindow?.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized) return;
        if (presenter.State == OverlappedPresenterState.Restored)
        {
            var p = _appWindow.Position;
            var s = _appWindow.Size;
            var bounds = new WindowBounds(p.X, p.Y, s.Width, s.Height);
            if (WindowPlacementStore.IsValid(bounds)) _lastWindowPlacement = new(bounds, false);
        }
        else if (_lastWindowPlacement is not null)
        {
            _lastWindowPlacement = _lastWindowPlacement with { Maximized = true };
        }
    }

    private async Task PersistWindowPlacementAsync()
    {
        _placementSaveTimer?.Stop();
        CaptureWindowPlacement();
        if (_lastWindowPlacement is null) return;
        try { await _windowPlacementStore.SaveAsync(_lastWindowPlacement); }
        catch (Exception ex) { LogService.WriteAppError(ex); }
    }

    private void DisposeWindowPlacement()
    {
        _placementSaveTimer?.Stop();
        if (_appWindow is not null) _appWindow.Changed -= WindowPlacement_Changed;
    }
}
