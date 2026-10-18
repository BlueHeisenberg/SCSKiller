using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class LibraryTests
{
    static GameState S(string name, GameStatus status, long? cache = null, long? estimate = null, long? plan = null, DateTimeOffset? warmed = null) =>
        new(new Game("steam:" + name, name, Store.Steam, "", ""), null, AntiCheat.None, status, "", null,
            plan is { } p ? new PlanStats(p, 0, 0, 0, false) : null, estimate, null, null, warmed, null, false, null, CacheOnDisk: cache);

    static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Each_status_filter_shows_its_games()
    {
        var games = new[]
        {
            S("unsupported", GameStatus.Unsupported),
            S("ready", GameStatus.Ready),
            S("stale", GameStatus.Stale, cache: 10, warmed: At),
            S("warmed", GameStatus.Warmed, cache: 10, warmed: At),
            S("played", GameStatus.NeedsRecording, cache: 5),   // the game compiled its own while it ran
            S("estimated", GameStatus.Ready, estimate: 50),   // an estimate is no cache
        };
        string[] Shown(LibraryShow show) => games.Where(g => Library.Shows(g, show)).Select(g => g.Game.Name).ToArray();
        Assert.Equal(games.Select(g => g.Game.Name), Shown(LibraryShow.All));
        Assert.Equal(["unsupported"], Shown(LibraryShow.NotSupported));
        Assert.Equal(["ready", "stale", "estimated"], Shown(LibraryShow.ReadyToCompile));
        Assert.Equal(["stale", "warmed", "played"], Shown(LibraryShow.Compiled));
        Assert.Equal(["stale", "warmed"], Shown(LibraryShow.CompiledByScsKiller));
    }

    [Fact]
    public void Sorts_by_name_or_size_with_unknown_sizes_last_either_way()
    {
        var games = new[]
        {
            S("beta", GameStatus.Ready, estimate: 300, plan: 30),
            S("Alpha", GameStatus.Warmed, cache: 100, plan: 10),
            S("delta", GameStatus.Unsupported),
            S("gamma", GameStatus.Stale, cache: 200, plan: 20),
        };
        string[] Sorted(LibrarySort sort) => Library.Sorted(games, sort).Select(g => g.Game.Name).ToArray();
        Assert.Equal(["gamma", "Alpha", "beta", "delta"], Sorted(LibrarySort.Status));
        Assert.Equal(["Alpha", "beta", "delta", "gamma"], Sorted(LibrarySort.NameAscending));
        Assert.Equal(["gamma", "delta", "beta", "Alpha"], Sorted(LibrarySort.NameDescending));
        Assert.Equal(["beta", "gamma", "Alpha", "delta"], Sorted(LibrarySort.CacheDescending));   // the estimate counts, as the column shows it
        Assert.Equal(["Alpha", "gamma", "beta", "delta"], Sorted(LibrarySort.CacheAscending));
        Assert.Equal(["beta", "gamma", "Alpha", "delta"], Sorted(LibrarySort.PipelinesDescending));
        Assert.Equal(["Alpha", "gamma", "beta", "delta"], Sorted(LibrarySort.PipelinesAscending));
    }

    [Fact]
    public void A_load_during_the_first_scan_starts_none_and_only_a_failed_scan_lets_the_next_load_scan()
    {
        var scan = new LibraryScan();
        Assert.True(scan.Start());
        Assert.False(scan.Start());   // a second load while the first scan runs
        scan.Ended(ok: false);
        Assert.True(scan.Start());
        scan.Ended(ok: true);
        Assert.False(scan.Start());
    }
}
