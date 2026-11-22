using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class WindowPlacementStoreTests
{
    [Fact]
    public void RestoreOnLeftMonitor_PreservesNegativeCoordinates()
    {
        var bounds = new WindowBounds(-1800, 80, 1100, 800);
        Assert.Equal(bounds, WindowPlacementStore.FitToWorkArea(bounds, new(-1920, 0, 1920, 1040)));
    }

    [Fact]
    public void DisconnectedMonitorAndOversizedWindow_AreMovedEntirelyIntoCurrentWorkArea()
    {
        var fit = WindowPlacementStore.FitToWorkArea(new(4000, -500, 3000, 2000), new(0, 0, 1280, 720));
        Assert.Equal(new WindowBounds(0, 0, 1280, 720), fit);
        Assert.False(WindowPlacementStore.IsValid(new(int.MinValue, 0, 800, 600)));
    }

    [Fact]
    public async Task PlacementRoundTrip_PreservesNormalBoundsWhileMaximized()
    {
        using var directory = new TemporaryDirectory();
        var store = new WindowPlacementStore(Path.Combine(directory.Path, "placement.json"));
        var placement = new SavedWindowPlacement(new(-1500, 100, 900, 700), true);
        await store.SaveAsync(placement);
        Assert.Equal(placement, await store.LoadAsync());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task CorruptOrInvalidPlacement_DoesNotBlockStartup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "placement.json");
        var store = new WindowPlacementStore(path);
        await File.WriteAllTextAsync(path, "{broken");
        Assert.Null(await store.LoadAsync());
        await File.WriteAllTextAsync(path, "{\"Bounds\":null,\"Maximized\":true}");
        Assert.Null(await store.LoadAsync());
    }
}
