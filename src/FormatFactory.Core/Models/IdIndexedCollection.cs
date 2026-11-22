using System.Collections.ObjectModel;

namespace FormatFactoryWinUI.Models;

/// <summary>An ordered UI collection with constant-time lookup by stable identity.</summary>
public sealed class IdIndexedCollection<T>(Func<T, Guid> identity) : ObservableCollection<T> where T : class
{
    private readonly Dictionary<Guid, T> _byId = [];
    // Ids are stable while an item belongs to the collection.
    public T? FindById(Guid id) => _byId.GetValueOrDefault(id);
    public bool ContainsReference(T item) => ReferenceEquals(FindById(identity(item)), item);

    protected override void InsertItem(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (index < 0 || index > Count) throw new ArgumentOutOfRangeException(nameof(index));
        CheckReentrancy();
        if (!_byId.TryAdd(identity(item), item)) throw new ArgumentException("Duplicate task identity.", nameof(item));
        base.InsertItem(index, item);
    }

    protected override void RemoveItem(int index)
    {
        var item = this[index];
        CheckReentrancy();
        _byId.Remove(identity(item));
        base.RemoveItem(index);
    }

    protected override void SetItem(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var previous = this[index];
        CheckReentrancy();
        if (identity(previous) != identity(item) && _byId.ContainsKey(identity(item)))
            throw new ArgumentException("Duplicate task identity.", nameof(item));
        _byId.Remove(identity(previous));
        _byId[identity(item)] = item;
        base.SetItem(index, item);
    }

    protected override void ClearItems()
    {
        CheckReentrancy();
        _byId.Clear();
        base.ClearItems();
    }
}
