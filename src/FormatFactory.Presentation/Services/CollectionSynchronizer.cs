using System.Collections.ObjectModel;

namespace FormatFactoryWinUI.Services;

internal static class CollectionSynchronizer
{
    public static void Update<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> source,
        Func<T, TKey> key) where TKey : notnull
    {
        var keys = source.Select(key).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(key(target[i]))) target.RemoveAt(i);
        var retained = target.Select(key).ToHashSet();

        for (var i = 0; i < source.Count; i++)
        {
            var wanted = key(source[i]);
            var found = -1;
            // Expanding a filter inserts many missing rows before retained rows.
            // Skip the suffix scan for keys that are known to be absent.
            if (retained.Contains(wanted))
                for (var j = i; j < target.Count; j++)
                    if (EqualityComparer<TKey>.Default.Equals(key(target[j]), wanted))
                    {
                        found = j;
                        break;
                    }
            if (found < 0)
            {
                target.Insert(i, source[i]);
                retained.Add(wanted);
            }
            else
            {
                if (found != i) target.Move(found, i);
                if (!EqualityComparer<T>.Default.Equals(target[i], source[i])) target[i] = source[i];
            }
        }
    }
}
