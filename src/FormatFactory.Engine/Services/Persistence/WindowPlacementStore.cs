using System.Text.Json;

namespace FormatFactoryWinUI.Services;

public sealed record WindowBounds(int X, int Y, int Width, int Height);
public sealed record SavedWindowPlacement(WindowBounds Bounds, bool Maximized);

/// <summary>Window geometry is independent of editable conversion settings.</summary>
public sealed class WindowPlacementStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowPlacementStore(string? path = null) =>
        _path = path ?? Path.Combine(LogService.AppDataRoot, "window-placement.json");

    public async Task<SavedWindowPlacement?> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var saved = JsonSerializer.Deserialize<SavedWindowPlacement>(await File.ReadAllTextAsync(_path));
            return saved is not null && IsValid(saved.Bounds) ? saved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(SavedWindowPlacement placement)
    {
        if (!IsValid(placement.Bounds)) return;
        var json = JsonSerializer.Serialize(placement);
        await _gate.WaitAsync();
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            await File.WriteAllTextAsync(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    public static bool IsValid(WindowBounds? bounds) => bounds is not null &&
        bounds.Width is >= 100 and <= 32768 && bounds.Height is >= 100 and <= 32768 &&
        Math.Abs((long)bounds.X) <= 131072 && Math.Abs((long)bounds.Y) <= 131072;

    // Coordinates may be negative on a monitor to the left/above the primary monitor.
    public static WindowBounds FitToWorkArea(WindowBounds bounds, WindowBounds workArea)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workArea.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workArea.Height);
        var width = Math.Clamp(bounds.Width, Math.Min(480, workArea.Width), workArea.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(320, workArea.Height), workArea.Height);
        return new WindowBounds(
            (int)Math.Clamp((long)bounds.X, workArea.X, (long)workArea.X + workArea.Width - width),
            (int)Math.Clamp((long)bounds.Y, workArea.Y, (long)workArea.Y + workArea.Height - height),
            width, height);
    }
}
