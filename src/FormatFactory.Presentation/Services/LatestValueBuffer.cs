namespace FormatFactoryWinUI.Services;

/// <summary>Bounds pending presentation work to one value per key and one scheduled drain.</summary>
internal sealed class LatestValueBuffer<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, TValue> _values = new();
    private bool _scheduled;

    public bool Publish(TKey key, TValue value)
    {
        lock (_gate)
        {
            _values[key] = value;
            if (_scheduled) return false;
            _scheduled = true;
            return true;
        }
    }

    public KeyValuePair<TKey, TValue>[] Drain()
    {
        lock (_gate)
        {
            var values = _values.ToArray();
            _values.Clear();
            _scheduled = false;
            return values;
        }
    }

    public void Remove(TKey key)
    {
        lock (_gate) _values.Remove(key);
    }
}
