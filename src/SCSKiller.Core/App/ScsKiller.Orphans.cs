using System.Text.RegularExpressions;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.App;

// A game's D3DSCache folder is named by its exe path, so an exe that moves (an Xbox update's new package folder) gets a new one
// and the runtime never opens the old one again. The old one goes only on the user's click, and only when our fold filled it.
public sealed partial class ScsKiller
{
    public const string OldVersionsNote = "Windows shader cache of earlier versions of the game, which it no longer reads.";

    /// <summary>The D3DSCache folders a fold of ours put rows into (<see cref="GameRecord.D3DSCacheFilled"/>), once also those an
    /// earlier version's folds put rows into by app.log's lines for the game, with their times, while their dbs name one exe path
    /// of the game's, the one the folder is named for. An earlier version's record isn't taken: its stamp, checked dbs and fold
    /// keys are written by a merge that added nothing too.</summary>
    Dictionary<string, D3DSCacheFill> FilledD3DSCache(Game g)
    {
        var r = Store.LoadGame(g.Id);
        if (r.D3DSCacheFilledSeeded || !OperatingSystem.IsWindows()) return r.D3DSCacheFilled ?? [];
        var root = Path.GetFullPath(Path.Combine(LocalAppData, "D3DSCache"));
        // the latest time a fold of ours wrote there: a folder made after it holds only what the runtime wrote since
        var names = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        void Saw(string? name, DateTimeOffset? at)
        {
            if (name != null && at is { } t && (!names.TryGetValue(name, out var was) || t > was)) names[name] = t;
        }
        foreach (var l in LoggedFolds().Where(l => l.Game == g.Name)) Saw(l.Folder, l.At);
        if (names.Count == 0) return r.D3DSCacheFilled ?? [];   // nothing to look at: no record written for a game never folded
        var seeded = new Dictionary<string, D3DSCacheFill>(StringComparer.Ordinal);
        var again = false;   // a folder that couldn't be read: looked for again next time, never dropped for good
        foreach (var (name, at) in names.Where(n => IsFolderName(n.Key)))
            try
            {
                var dir = Path.Combine(root, name);
                if (!Directory.Exists(dir) || IsLink(dir) || HoldsLink(dir)) continue;
                if (D3DSCache.ExePaths(dir) is not { } paths)
                {
                    again = true;
                    continue;
                }
                if (paths.Count != 1) continue;
                var exe = paths.Single();
                if (D3DSCache.FolderName(exe) == name && (D3DSCache.IsGameExe(exe, g) || exe.Equals(r.D3DSCacheExe, StringComparison.OrdinalIgnoreCase)))
                    seeded[name] = new(exe, at);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { again = true; }
        Dictionary<string, D3DSCacheFill>? all = null;
        AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
        {
            var w = Store.LoadGame(g.Id);
            all = w.D3DSCacheFilled ?? [];
            if (w.D3DSCacheFilledSeeded) return;
            foreach (var (name, fill) in seeded) all.TryAdd(name, fill);
            (w.D3DSCacheFilled, w.D3DSCacheFilledSeeded) = (all.Count > 0 ? all : null, !again);
            Store.SaveGame(g.Id, w);
        });
        if (seeded.Count > 0) Log?.Report($"{g.Name}: earlier D3DSCache folds on record: {string.Join(", ", seeded.Keys.Order())}");
        return all!;
    }

    // "<time> <game>: [the compile's ]D3DSCache dbs went into <folder>, <n> keys added": the fold lines of MergeLogged and FoldStaged
    static readonly Regex LoggedFoldLine = new(@"^(?<at>\S+ \S+) (?<game>.+): (?:the compile's )?D3DSCache dbs went into (?<folder>[0-9a-f]{1,16}), (?<keys>\d+) keys added", RegexOptions.CultureInvariant);

    List<(string Game, string Folder, DateTimeOffset At)>? _loggedFolds;

    /// <summary>The folds app.log and app.log.1 name that added keys, and when: read once per process, only the older folds need it.</summary>
    List<(string Game, string Folder, DateTimeOffset At)> LoggedFolds()
    {
        if (_loggedFolds is { } known) return known;
        var found = new List<(string, string, DateTimeOffset)>();
        foreach (var file in new[] { AppLog.FileName + ".1", AppLog.FileName }.Select(f => Path.Combine(Store.DataDir, f)).Where(File.Exists))
            try
            {
                using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(s);
                for (string? line; (line = reader.ReadLine()) != null;)
                    if (LoggedFoldLine.Match(line) is { Success: true } m && m.Groups["keys"].Value != "0" && DateTimeOffset.TryParseExact(m.Groups["at"].Value, "yyyy-MM-dd HH:mm:ss'Z'",
                            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                        found.Add((m.Groups["game"].Value, m.Groups["folder"].Value, at));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return _loggedFolds = found;
    }

    /// <summary>A folder on record for the game that is an earlier version's our fold filled (<see cref="OrphanedD3DSCache"/>), counted
    /// there and not as the game's cache. Any other stays the game's. The cheap test first: the dbs are read only for a filled
    /// folder whose exe is gone.</summary>
    bool LeftByOldExe(Game g, GameRecord r, string dir) => (r.D3DSCacheFilledSeeded ? r.D3DSCacheFilled : FilledD3DSCache(g))?.GetValueOrDefault(Path.GetFileName(dir)) is { } fill
        && !File.Exists(fill.Exe) && OrphanedD3DSCache(g).Any(o => SameDir(o, dir));

    /// <summary>The game's D3DSCache folders of an exe path that is gone, each one our fold filled (<see cref="NotOrphaned"/>).
    /// <paramref name="deleting"/>: also refused while the game runs or a file in it is open.</summary>
    List<string> OrphanedD3DSCache(Game g, bool deleting = false)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var filled = FilledD3DSCache(g);
        var root = Path.Combine(LocalAppData, "D3DSCache");
        var candidates = filled.Where(f => !File.Exists(f.Value.Exe) && Directory.Exists(Path.Combine(root, f.Key))).Select(f => Path.Combine(root, f.Key)).ToList();
        if (candidates.Count == 0) return [];
        var r = Store.LoadGame(g.Id);
        if (NamedByOthers(g.Id) is not { } others) return [];
        return candidates.Where(d => NotOrphaned(g, r, d, filled, others, deleting) == null).ToList();
    }

    /// <summary>Why <paramref name="dir"/> isn't an earlier exe's folder of the game that may go; null when it may: our fold filled
    /// it (<paramref name="filled"/>) and it wasn't made again since, it is no link, its dbs name only that fill's exe path, which
    /// is gone and which the folder is named for, the runtime doesn't open it for the game now, no moved install's merge waits for
    /// it, and no other game's record names it (<paramref name="others"/>).</summary>
    string? NotOrphaned(Game g, GameRecord r, string dir, IReadOnlyDictionary<string, D3DSCacheFill> filled, IReadOnlySet<string> others, bool deleting)
    {
        var name = Path.GetFileName(dir);
        try
        {
            if (!IsFolderName(name) || !filled.TryGetValue(name, out var fill)) return "SCSKiller didn't fill it";
            if (!Directory.Exists(dir) || IsLink(dir)) return "it is gone or a link";
            if (HoldsLink(dir)) return "it holds a link";
            // a later folder of that name holds only what the runtime wrote since
            if (FileTimes.CreatedUtc(new DirectoryInfo(dir)) is not { } made || made > fill.At.UtcDateTime.AddSeconds(2)) return "it was made again after SCSKiller filled it";
            if (LaunchExe(g) is not { } exe || D3DSCache.FolderName(exe) == name || D3DSCache.FolderName(g.ExePath) == name) return "the game's current version uses it";
            if (File.Exists(fill.Exe) || D3DSCache.FolderName(fill.Exe) != name) return "its game version is still installed";
            if (MovedD3DSCache(g, r) is { } moved && SameDir(moved, dir)) return "it goes into the game's new folder";
            if (others.Contains(name)) return "another game's cache uses it";
            if (D3DSCache.ExePaths(dir) is not { Count: 1 } paths || !paths.Single().Equals(fill.Exe, StringComparison.OrdinalIgnoreCase)) return "it holds another app's cache";
            if (deleting && CacheInUse(g)) return $"{g.Name} is running";
            if (deleting && _folds.Keys.Any(f => f.Id == g.Id)) return $"a fold of {g.Name} is running";
            if (deleting && Directory.EnumerateFiles(dir).Any(Opened)) return "a file in it is open";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }

    /// <summary>The D3DSCache folder names any other game's record names: its folders, its fills, its fold's stamp and keys, its checked
    /// dbs. A damaged record claims every folder name its text holds. Null when a record can't be read: its claim isn't known.</summary>
    HashSet<string>? NamedByOthers(string gameId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = Path.Combine(Store.DataDir, "games");
        var own = Path.GetFullPath(Store.GameDir(gameId));
        foreach (var state in (Directory.Exists(games) ? Directory.GetDirectories(games) : []).Where(d => !string.Equals(Path.GetFullPath(d), own, StringComparison.OrdinalIgnoreCase))
                     .Select(d => Path.Combine(d, "state.json")).Where(File.Exists))
        {
            GameRecord o;
            // read here, not by LoadGame, which takes a damaged record for an empty one
            try
            {
                using var f = new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (System.Text.Json.JsonSerializer.Deserialize<GameRecord>(f, AppStore.Json) is not { } read) return null;
                o = read;
            }
            catch (System.Text.Json.JsonException)
            {
                try { names.UnionWith(Regex.Matches(File.ReadAllText(state), "(?<![0-9A-Za-z])[0-9a-f]{1,16}(?![0-9A-Za-z])").Select(m => m.Value)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
                continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            names.UnionWith(new[] { o.D3DSCacheFolder, o.D3DSCacheMade, o.D3DSCacheStampFolder, o.D3DSCacheFoldKeys?.Folder }.OfType<string>());
            names.UnionWith(o.D3DSCacheFilled?.Keys ?? Enumerable.Empty<string>());
            names.UnionWith((o.D3DSCacheChecked?.Keys ?? Enumerable.Empty<string>()).Select(k => Path.GetFileName(Path.GetDirectoryName(k.Split('|')[0])) ?? ""));
        }
        return names;
    }

    static bool HoldsLink(string dir) => new DirectoryInfo(dir).EnumerateFileSystemInfos().Any(f => f.Attributes.HasFlag(FileAttributes.ReparsePoint));

    static long Bytes(IEnumerable<string> dirs) => dirs.SelectMany(D3DSCache.CacheFiles).Sum(f => f.Length);

    /// <summary>Deletes the game's earlier versions' D3DSCache folders (<see cref="OrphanedD3DSCache"/>), each checked again under
    /// the game's compile lock with its folds stopped: only the runtime's files, then the folder when nothing else is left. The
    /// bytes freed. Throws InvalidOperationException while the game compiles.</summary>
    public long DeleteOrphanedD3DSCache(string gameId)
    {
        var s = Find(gameId);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        using var held = CompileLock(gameId) ?? throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        using var folds = StopFolds(s.Game);
        long freed = 0;
        foreach (var dir in OrphanedD3DSCache(s.Game, deleting: true))
        {
            var files = D3DSCache.CacheFiles(dir).ToList();
            var bytes = files.Sum(f => f.Length);
            try { AppCacheFiles.DeleteAll(files); }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Log?.Report($"{s.Game.Name}: {Path.GetFileName(dir)} not deleted: {e.Message}");
                continue;
            }
            freed += bytes;
            Log?.Report($"{s.Game.Name}: the Windows shader cache of an earlier game version deleted ({Path.GetFileName(dir)}, {App.Format.Bytes(bytes)})");
            DeletedOldVersion(gameId, dir);
        }
        return freed;
    }

    /// <summary>After an earlier version's folder lost its files: the folder goes when nothing else is in it, and the record forgets
    /// it. The warm's db lengths counted its dbs too (same names), so what is left on record is the trim check's baseline now.</summary>
    void DeletedOldVersion(string gameId, string dir)
    {
        try { Directory.Delete(dir); }
        catch (IOException) { }   // not empty: a file appeared meanwhile, left alone
        var name = Path.GetFileName(dir);
        AppStore.Locked(Path.Combine(Store.GameDir(gameId), "state.json"), () =>
        {
            var r = Store.LoadGame(gameId);
            var onRecord = D3DSCacheNames(r).Contains(name);
            if (r.D3DSCacheFolder == name) (r.D3DSCacheFolder, r.D3DSCacheExe) = (null, null);
            if (r.D3DSCacheMade == name) r.D3DSCacheMade = null;
            if (onRecord && r.WarmedD3DSCacheBytes != null && D3DSCacheDbs(D3DSCacheNames(r)) is { } left) r.WarmedD3DSCacheBytes = Lengths(left);
            if (r.D3DSCacheFilled is { } filled && filled.Remove(name)) r.D3DSCacheFilled = filled.Count > 0 ? filled : null;
            Store.SaveGame(gameId, r);
        });
    }
}
