using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    const string KeptFolder = "kept";
    readonly object _keptLock = new();   // ponytail: one lock for every game's kept folder, they're touched seconds apart

    string KeptDir(string gameId) => Path.Combine(Store.GameDir(gameId), KeptFolder);

    /// <summary>The game's kept-aside files as they are: to read, count or drop. Null when not on AMD.</summary>
    AmdKept? KeptOf(string gameId) => AppCache is AmdAppCache amd ? new AmdKept(amd.DxcDir, KeptDir(gameId)) : null;

    /// <summary>The game's kept-aside files when files may be kept: the switch is on and DxcCache is a real folder on the data
    /// folder's volume (a hard link can't cross volumes, and a junction-moved DxcCache may move again).</summary>
    AmdKept? Keeping(string gameId) => Settings.KeepCachesAside && AppCache is AmdAppCache amd && AmdKept.Usable(amd.DxcDir, Store.DataDir)
        ? new AmdKept(amd.DxcDir, KeptDir(gameId)) : null;

    /// <summary>The game's D3D12 keys no other game has (an AMD app profile gives several exes one key): only their files are kept.</summary>
    HashSet<string> OwnKeys(string gameId, GameRecord rec)
    {
        var keys = rec.CacheKeys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count > 0)
            foreach (var (_, shared) in SharedWith(gameId, keys)) keys.ExceptWith(shared);
        return keys;
    }

    /// <summary>After a complete compile and after the game exits: links the cache files of a compiled game's own keys aside.</summary>
    void KeepAside(string gameId, GameRecord rec, string name)
    {
        try
        {
            lock (_keptLock)
            {
                if (rec.WarmedAt == null || Keeping(gameId) is not { } k || AppCache is not AmdAppCache amd) return;
                // another driver's links that couldn't be dropped must not be relabelled as this driver's
                if (DriverId is { } now && k.Read().Driver is { } was && was != now && !Drop(k, gameId, "kept for another driver")) return;
                var keys = OwnKeys(gameId, rec);   // none left: the links of keys shared since go
                if (k.Keep(amd.D3D12FileNames(keys), keys, DriverId) is { Unkept: > 0 } s)
                    Log?.Report($"{name}: {s.Unkept} cache file{(s.Unkept == 1 ? "" : "s")} not kept aside, in use by {s.HeldBy ?? "another app"}");
            }
        }
        catch (Exception e) { Log?.Report($"{name}: keeping the cache aside failed: {e.Message}"); }   // never fails the compile or the poll
    }

    public void PutBackKept(string gameId)
    {
        try
        {
            if (PutBack(gameId, late: false)) ShowKept(gameId);
        }
        catch (Exception e) { Log?.Report($"putting {gameId}'s cache back failed: {e.Message}"); }
    }

    /// <summary>Links the kept files the driver removed back into DxcCache: before a compile, at Play, and at the first poll
    /// that sees the game running (<paramref name="late"/>: the game may have compiled them again). Files kept under
    /// another driver, or after the driver's cache was reset, are dropped instead. True when the kept state changed.</summary>
    bool PutBack(string gameId, bool late)
    {
        try
        {
            lock (_keptLock)
            {
                if (Keeping(gameId) is not { Any: true } k || AppCache is not AmdAppCache amd) return false;
                if (DriverId is { } now && k.Read().Driver is { } was && was != now)
                {
                    Drop(k, gameId, "kept for another driver");
                    return true;
                }
                var keys = OwnKeys(gameId, Store.LoadGame(gameId));
                // a reset (Adrenalin, Disk Cleanup) empties DxcCache; an eviction leaves it just under the cap
                if (amd.D3D12FileNames(keys).Count == 0 && OperatingSystem.IsWindows() && amd.DxcBytes() < AmdAppCache.DxcCacheCap / 2)
                {
                    Drop(k, gameId, "the driver's cache was reset");
                    return true;
                }
                return k.PutBack(keys.Contains, late, DateTimeOffset.Now) > 0;
            }
        }
        catch (Exception e)   // never fails the compile, the poll or Play
        {
            Log?.Report($"{gameId}: putting the kept cache back failed: {e.Message}");
            return false;
        }
    }

    /// <summary>True when every link is gone.</summary>
    bool Drop(AmdKept k, string gameId, string why)
    {
        var all = k.Drop();
        Log?.Report($"{gameId}: kept cache files removed, {why}" + (all ? "" : ", some are in use and stay until the next time"));
        return all;
    }

    /// <summary>Clear cache, the game leaving the library, Manage's Remove. DxcCache isn't touched.</summary>
    void DropKept(string gameId)
    {
        try
        {
            lock (_keptLock)
                if (Directory.Exists(KeptDir(gameId))) Drop(new AmdKept("", KeptDir(gameId)), gameId, "the game's cache was cleared");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{gameId}: removing the kept cache failed: {e.Message}"); }
    }

    public void DropKeptCache(string gameId)
    {
        DropKept(gameId);
        Refresh(Find(gameId).Game);   // a trimmed cache shows as trimmed again
    }

    /// <summary>Drops every game's kept files: all when the switch goes off or not on AMD, else those kept under another driver.</summary>
    void DropStaleKept()
    {
        var games = Path.Combine(Store.DataDir, "games");
        var all = !Settings.KeepCachesAside || AppCache is not AmdAppCache;
        if (!Directory.Exists(games) || !all && DriverId == null) return;
        try
        {
            lock (_keptLock)
                foreach (var dir in Directory.EnumerateDirectories(games))
                    if (Path.Combine(dir, KeptFolder) is var kept && Directory.Exists(kept) && new AmdKept("", kept) is var k
                        && (all || k.Read().Driver is { } was && was != DriverId))
                        Drop(k, Path.GetFileName(dir), all ? "keeping caches aside is off" : "kept for another driver");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"removing kept caches failed: {e.Message}"); }
        foreach (var s in Games) ShowKept(s.Game.Id);
    }

    KeptCache? KeptState(string gameId)
    {
        try
        {
            if (KeptOf(gameId) is not { Any: true } k) return null;
            var (bytes, frees) = k.Size();
            var s = k.Read();
            return new KeptCache(bytes, frees, s.PutBackAt, s.Unkept, s.HeldBy, s.Late);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The kept state and the disk use that a put-back or drop changed, without a whole refresh.</summary>
    void ShowKept(string gameId)
    {
        var kept = KeptState(gameId);
        if (Games.FirstOrDefault(x => x.Game.Id == gameId) is not { } listed) return;
        var rec = Store.LoadGame(gameId);
        var (disk, windows) = (CacheOnDiskOf(listed.Game, listed.Engine, rec), WindowsCacheOf(listed.Game, rec));
        GameState? state = null;
        lock (_lock)
            if (_games.FindIndex(x => x.Game.Id == gameId) is var i and >= 0 && (_games[i].Kept != kept || _games[i].CacheOnDisk != disk || _games[i].WindowsCache != windows))
                _games[i] = state = _games[i] with { Kept = kept, CacheOnDisk = disk, WindowsCache = windows };
        if (state != null) GameChanged?.Invoke(state);
    }

    /// <summary>A warmed file the driver removed that is kept aside as the same file: it goes back before the game runs.
    /// <paramref name="kept"/>: <see cref="Keeping"/>'s, null while nothing goes back.</summary>
    bool KeptSame(AmdKept? kept, GameRecord r, string name) =>
        kept?.KeptId(name) is { } id && (r.WarmedFileIds?.GetValueOrDefault(name) is not { } warmed || warmed == id);
}
