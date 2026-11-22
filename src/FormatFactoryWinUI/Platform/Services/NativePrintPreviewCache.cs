using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI.Services;

/// <summary>Releases both cached page elements and their decoded images when previews are evicted.</summary>
internal sealed class NativePrintPreviewCache(int capacity)
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly Dictionary<int, Grid> _pages = [];
    private readonly Queue<int> _order = [];
    internal int Count => _pages.Count;
    internal Grid GetOrCreate(int pageNumber, Func<Grid> create)
    {
        if (_pages.TryGetValue(pageNumber, out var existing)) return existing;
        while (_pages.Count >= _capacity)
        {
            var oldest = _order.Dequeue();
            if (_pages.Remove(oldest, out var evicted)) ReleasePageImage(evicted);
        }
        var page = create();
        _pages.Add(pageNumber, page);
        _order.Enqueue(pageNumber);
        return page;
    }
    internal void Clear()
    {
        foreach (var page in _pages.Values) ReleasePageImage(page);
        _pages.Clear();
        _order.Clear();
    }
    internal static void ReleasePageImage(Grid page)
    {
        foreach (var image in page.Children.OfType<Image>()) image.Source = null;
        page.Children.Clear();
    }
}

