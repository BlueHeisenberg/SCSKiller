namespace SCSKiller.Core.App;

/// <summary>The Library's order within each section: by status (then name), by name, or by the Pipelines or Cache column.</summary>
public enum LibrarySort { Status, NameAscending, NameDescending, PipelinesDescending, PipelinesAscending, CacheDescending, CacheAscending }

/// <summary>The Library's status filter.</summary>
public enum LibraryShow { All, NotSupported, ReadyToCompile, Compiled, CompiledByScsKiller }

public static class Library
{
    /// <summary>Not supported: <see cref="GameStatus.Unsupported"/>. Ready to compile: Ready or Stale, as the summary counts
    /// them. Compiled by SCSKiller: a complete compile of SCSKiller's is on record (<see cref="GameState.WarmedAt"/>), for
    /// this driver and build or an older one. Compiled: that, or the driver cache holds pipelines the game compiled itself
    /// while it ran (<see cref="GameState.CacheOnDisk"/> above 0).</summary>
    public static bool Shows(GameState s, LibraryShow show) => show switch
    {
        LibraryShow.NotSupported => s.Status == GameStatus.Unsupported && !ScsKiller.IsUnchecked(s),
        LibraryShow.ReadyToCompile => s.Status is GameStatus.Ready or GameStatus.Stale,
        LibraryShow.CompiledByScsKiller => s.WarmedAt != null,
        LibraryShow.Compiled => s.WarmedAt != null || s.CacheOnDisk > 0,
        _ => true,
    };

    static readonly StringComparer Names = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>A size sort puts the games with no size last, either way; ties go by name.</summary>
    public static IEnumerable<GameState> Sorted(IEnumerable<GameState> games, LibrarySort sort) => sort switch
    {
        LibrarySort.NameAscending => games.OrderBy(g => g.Game.Name, Names),
        LibrarySort.NameDescending => games.OrderByDescending(g => g.Game.Name, Names),
        LibrarySort.PipelinesDescending => By(games, ScsKiller.PlanPipelines, descending: true),
        LibrarySort.PipelinesAscending => By(games, ScsKiller.PlanPipelines, descending: false),
        LibrarySort.CacheDescending => By(games, CacheBytes, descending: true),
        LibrarySort.CacheAscending => By(games, CacheBytes, descending: false),
        _ => games.OrderBy(g => g.Status switch
        {
            GameStatus.Stale => 0, GameStatus.Warmed => 1, GameStatus.Ready => 2, GameStatus.NeedsRecording => 3, _ => 4,
        }).ThenBy(g => g.Game.Name, Names),
    };

    /// <summary>What the Cache column shows: the measured driver cache, else the estimate.</summary>
    public static long? CacheBytes(GameState s) => s.CacheOnDisk ?? s.EstimatedCacheBytes;

    static IEnumerable<GameState> By(IEnumerable<GameState> games, Func<GameState, long?> size, bool descending)
    {
        var known = games.OrderBy(g => size(g) == null);
        return (descending ? known.ThenByDescending(size) : known.ThenBy(size)).ThenBy(g => g.Game.Name, Names);
    }
}

/// <summary>The Library's own scan at its first load: one per run, and again after a scan failed.</summary>
public sealed class LibraryScan
{
    bool started;
    /// <summary>True for the load that starts it, false while it runs or once a scan worked.</summary>
    public bool Start() => !started && (started = true);
    public void Ended(bool ok) => started = ok;
}
