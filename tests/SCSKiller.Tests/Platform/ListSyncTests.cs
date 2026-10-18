using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class ListSyncTests
{
    static (ObservableCollection<string> List, List<NotifyCollectionChangedAction> Changes) Listed(params string[] items)
    {
        var list = new ObservableCollection<string>(items);
        var changes = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => changes.Add(e.Action);
        return (list, changes);
    }

    static string[] Rows(int n) => [.. Enumerable.Range(0, n).Select(i => $"game {i}")];

    /// <summary>A scan or a background re-evaluation that leaves the order as it was must not touch the Library's list.</summary>
    [Fact]
    public void The_same_items_change_nothing()
    {
        var rows = Rows(60);
        var (list, changes) = Listed(rows);
        ListSync.Sync(list, rows);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(3, 40)]
    [InlineData(40, 3)]
    [InlineData(0, 59)]
    [InlineData(59, 0)]
    [InlineData(10, 11)]
    public void One_item_moved_is_one_remove_and_one_insert(int from, int to)
    {
        var rows = Rows(60);
        var (list, changes) = Listed(rows);
        var want = rows.ToList();
        var moved = want[from];
        want.RemoveAt(from);
        want.Insert(to, moved);
        ListSync.Sync(list, want);
        Assert.Equal(want, list);
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add], changes);
    }

    [Fact]
    public void Any_change_ends_in_the_wanted_order_without_a_reset()
    {
        var random = new Random(7);
        var pool = Rows(80);
        for (int run = 0; run < 500; run++)
        {
            var (list, changes) = Listed([.. pool.Where(_ => random.Next(3) > 0).OrderBy(_ => random.Next())]);
            string[] want = [.. pool.Where(_ => random.Next(3) > 0).OrderBy(_ => random.Next())];
            ListSync.Sync(list, want);
            Assert.Equal(want, list);
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        }
    }

    [Fact]
    public void A_new_item_is_one_insert_and_a_gone_one_one_remove()
    {
        var rows = Rows(10);
        var (list, changes) = Listed(rows);
        ListSync.Sync(list, [.. rows[..4], "new", .. rows[5..]]);
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add], changes);
    }
}
