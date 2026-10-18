using System.Collections.ObjectModel;

namespace SCSKiller.Core.App;

/// <summary>Makes a bound list hold <c>want</c> (same items, same order, by reference) with few removes and inserts and
/// never a Reset: a list control keeps its scroll position and the containers of the items that stay. One item moved
/// anywhere costs one remove and one insert.</summary>
public static class ListSync
{
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> want) where T : class
    {
        var keep = want.ToHashSet(ReferenceEqualityComparer.Instance);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], want[i])) continue;
            if (i + 1 < target.Count && ReferenceEquals(target[i + 1], want[i]) && IndexOf(want, target[i], i + 2) >= 0)
            {
                target.RemoveAt(i);   // this item moved further down: it goes back in when its place comes
                continue;
            }
            int from = IndexOf(target, want[i], i + 1);
            if (from >= 0) target.RemoveAt(from);
            // a remove and an insert, not Move: the grouped ListView's handling of Move isn't documented
            target.Insert(i, want[i]);
        }
    }

    static int IndexOf<T>(IReadOnlyList<T> list, T item, int from) where T : class
    {
        for (int i = from; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }
}
