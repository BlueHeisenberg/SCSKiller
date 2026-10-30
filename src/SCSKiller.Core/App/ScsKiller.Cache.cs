using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Vulkan;

namespace SCSKiller.Core.App;

/// <summary>A driver cache folder: the driver reads existing caches and writes new ones there. Final: where its files are
/// when the folder is a link; TargetMissing: a link whose target is gone; Cap: the driver's fixed limit on it, null = none.</summary>
public sealed record CacheFolder(string Label, string Path, bool Exists, string? Final, bool TargetMissing, long Bytes, long? Cap = null);

/// <summary>In the order the Cache page shows them, which is also the order a key claimed twice goes by.</summary>
public enum CacheGroupKind { Installed, Uninstalled, OldDriver, OldBuild, OtherVendor, Unattributed }

/// <summary>A cache file as the page read it: a delete takes it only while it is still the same. Used: NVIDIA, what it holds
/// (<see cref="NvidiaAppCache.UsedBytes"/>); null = not read.</summary>
public sealed record CacheFile(string Path, long Length, DateTime WrittenUtc, long? Used = null);

/// <summary>Files that belong together. GameIds: the games whose compiled cache this is (Installed and Uninstalled items):
/// deleting it resets their compile. Seen: every file the read listed in the item's units (<see cref="ScsKiller.UnitKey"/>),
/// other items' included; a file of a unit not in it was added since. Note: shown with the item. Kept: shown, never deleted
/// (Mesa's file of a Steam game, see <see cref="MesaLinuxAppCache"/>).</summary>
public sealed record CacheItem(string Name, CacheGroupKind Kind, IReadOnlyList<CacheFile> Files, IReadOnlyList<string> GameIds,
    IReadOnlyCollection<string> Seen, string? Note = null, bool Kept = false)
{
    public long Bytes => Files.Sum(f => f.Length);

    /// <summary>What the files hold where read (a file not read: its length); null = none read.</summary>
    public long? InUse => Files.Any(f => f.Used != null) ? Files.Sum(f => f.Used ?? f.Length) : null;
}

/// <summary>A group of the Cache page; each item's files belong together.</summary>
public sealed record CacheGroup(CacheGroupKind Kind, IReadOnlyList<CacheItem> Items, string? Note = null)
{
    public long Bytes => Items.Sum(i => i.Bytes);

    public long? InUse => Items.Any(i => i.InUse != null) ? Items.Sum(i => i.InUse ?? i.Bytes) : null;
}

/// <summary>DriverInstalled: what Old driver files are older than; null = no Old driver group.</summary>
public sealed record CacheInventory(IReadOnlyList<CacheFolder> Folders, IReadOnlyList<CacheGroup> Groups, DateTime? DriverInstalled = null)
{
    /// <summary>What "Clear everything" deletes: every group but Other apps and games, unless the user ticked it.</summary>
    public IReadOnlyList<CacheItem> Everything(bool withOthers) =>
        Groups.Where(g => withOthers || g.Kind != CacheGroupKind.Unattributed).SelectMany(g => g.Items).Where(i => !i.Kept).ToList();

    public long Bytes(CacheGroupKind kind) => Groups.Where(g => g.Kind == kind).Sum(g => g.Bytes);

    /// <summary>The folders to open for <paramref name="item"/>: where its files outside the listed folders are (a game's own
    /// D3DSCache folder), then each listed folder holding some.</summary>
    public IReadOnlyList<string> FoldersOf(CacheItem item)
    {
        var listed = Folders.Select(f => GameFiles.DirKey(f.Path)).ToList();
        static bool In(string dir, string path) => string.Equals(GameFiles.DirKey(dir), GameFiles.DirKey(path), StringComparison.OrdinalIgnoreCase) || GameFiles.Inside(dir, path);
        // a folder holding a listed one (%LOCALAPPDATA%) or a drive root is no item's own
        bool Broad(string dir) => Path.GetPathRoot(dir) is { Length: > 0 } root && In(dir, root) || listed.Any(l => In(dir, l));
        string? Common(string a, string b)
        {
            for (string? c = a; c != null; c = Path.GetDirectoryName(c)) if (In(c, b)) return Broad(c) ? null : c;
            return null;
        }
        List<string> outside = [], inListed = [];
        // empty: a path without this OS's separators (the design data's Windows paths on Linux)
        foreach (var dir in item.Files.Select(f => Path.GetDirectoryName(f.Path)).OfType<string>().Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (listed.FirstOrDefault(l => In(l, dir)) is { } at)
            {
                if (!inListed.Contains(at, StringComparer.OrdinalIgnoreCase)) inListed.Add(at);
                continue;
            }
            if (Broad(dir)) continue;
            var i = outside.FindIndex(o => Common(o, dir) != null);
            if (i < 0) outside.Add(dir);
            else outside[i] = Common(outside[i], dir)!;
        }
        return [.. outside.Where(o => !outside.Any(p => GameFiles.Inside(p, o))).Distinct(StringComparer.OrdinalIgnoreCase), .. inListed];
    }
}

/// <summary>What a delete freed, and each unit of files it left with the reason.</summary>
public sealed record CacheDeleteResult(long Bytes, int Files, IReadOnlyList<string> Skipped);

// The Cache page: every driver cache file on the PC, each in one item. Only names, sizes and write times are read, and
// NVIDIA's file headers, never AMD's contents (AMD evicts by access time).
public sealed partial class ScsKiller
{
    /// <summary>When the active driver was installed (NVIDIA: <see cref="NvidiaBackend.InstalledAt"/>); null = unknown.
    /// Replaceable for tests.</summary>
    public Func<DateTime?> DriverInstalledAt { get; set; } = OperatingSystem.IsWindows() ? NvidiaBackend.InstalledAt : () => null;

    /// <summary>A compile or warm runs in any SCSKiller process: no cache is deleted meanwhile. Null = this process's queue
    /// or the session-wide compile mutex (<see cref="Busy"/>). Replaceable for tests: other tests hold that mutex.</summary>
    public Func<bool>? CompileRunning { get; set; }

    /// <summary>This process's queue item runs and hasn't shown Done: a failed or stopped one still stops its warm and folds.</summary>
    bool ItemRunning { get { lock (_lock) return _current is { } c && (_queue.FirstOrDefault(q => q.GameId == c) is not { } q || q.Stage != QueueStage.Done); } }

    /// <summary>Exists while a Cache page delete runs: no compile starts meanwhile (<see cref="HoldForCompile"/>).</summary>
    public static string DeletingName => @"Local\SCSKiller.CacheDelete" + Busy.Scope;

    /// <summary>A compile worker's hold (<see cref="Busy.TryHold"/>); null while an update is handed over or a cache delete runs.</summary>
    IDisposable? HoldForCompile()
    {
        var hold = Busy.TryHold(Store.DataDir, Clock());
        if (hold == null || !Busy.IsHeld(DeletingName)) return hold;
        hold.Dispose();
        return null;
    }

    public const string OtherApps = "Other apps and games", NoAppKey = "Files without an app key",
        CompileLeftovers = "Windows shader cache of earlier compiles";

    (GpuVendor Vendor, string Label, string Dir)[] VendorFolders() =>
    [
        (GpuVendor.Nvidia, "NVIDIA DirectX", Path.Combine(LocalAppData, "NVIDIA", "DXCache")), (GpuVendor.Nvidia, "NVIDIA OpenGL and Vulkan", Path.Combine(LocalAppData, "NVIDIA", "GLCache")),
        (GpuVendor.Nvidia, "NVIDIA DirectX, older drivers", Path.Combine([LocalAppData + "Low", .. NvidiaAppCache.LegacyTail])),
        (GpuVendor.Amd, "AMD DirectX 12", Path.Combine(LocalAppData, "AMD", "DxcCache")), (GpuVendor.Amd, "AMD DirectX 11", Path.Combine(LocalAppData, "AMD", "DxCache")),
        (GpuVendor.Amd, "AMD Vulkan", Path.Combine(LocalAppData, "AMD", "VkCache")), (GpuVendor.Amd, "AMD OpenGL", Path.Combine(LocalAppData, "AMD", "GLCache")),
    ];

    public CacheInventory CacheInventory()
    {
        var (own, folders) = AppCache switch
        {
            NvidiaAppCache nv => (GpuVendor.Nvidia, new List<(string, string, bool)> { ("DirectX 12 and 11", nv.Dir, true) }),
            AmdAppCache amd => (GpuVendor.Amd, [("DirectX 12", amd.DxcDir, true), ("DirectX 11", amd.DxDir, false)]),
            IntelAppCache intel => (GpuVendor.Intel, [("DirectX 12 and 11", intel.Dir, true)]),
            _ => (GpuVendor.Unknown, []),
        };
        var notes = new Dictionary<string, string>();
        var claims = Claims(own, notes);

        var items = new Dictionary<(CacheGroupKind Kind, string Name), (List<FileInfo> Files, HashSet<string> Ids)>();
        void Add(CacheGroupKind kind, string name, IEnumerable<FileInfo> files, IEnumerable<string>? ids = null)
        {
            if (!items.TryGetValue((kind, name), out var i)) items[(kind, name)] = i = ([], []);
            i.Files.AddRange(files);
            // only these groups' files are what a game's compile made and reads
            if (kind is CacheGroupKind.Installed or CacheGroupKind.Uninstalled && ids != null) i.Ids.UnionWith(ids);
        }

        var oldBefore = OldDriverBefore(fresh: true);
        string? KeyOf(FileInfo f, bool d3d12) => own switch
        {
            GpuVendor.Nvidia => NvidiaAppCache.Key(f.Name) ?? NvidiaAppCache.LegacyKey(f.FullName), GpuVendor.Intel => IntelAppCache.Key(f.Name), _ => AmdAppCache.Key(f.Name, d3d12),
        };
        var shown = new List<CacheFolder>();
        var readKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, dir, d3d12) in folders)
        {
            shown.Add(Read(dir, out var files) with { Label = label, Cap = AppCache is AmdAppCache amd && dir == amd.DxcDir ? AmdAppCache.DxcCacheCap : null });
            var old = oldBefore != null ? OldDriverFiles(files, oldBefore) : [];
            foreach (var f in files)
            {
                var key = KeyOf(f, d3d12);
                if (key != null) readKeys.Add(key);
                var (kind, name, ids) = Owner(claims, key);
                if (old.Contains(f.FullName)) Add(CacheGroupKind.OldDriver, ids != null ? name : OtherApps, [f]);
                else Add(kind, name, [f], ids);
            }
        }

        // a listed game's own D3DSCache folder is its cache, never an earlier compile's: it goes only with the game's
        var gameD3ds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Games)
        {
            var rec = Store.LoadGame(s.Game.Id);
            // a kept or killed compile's staged folders wait for their fold (MergeD3DSCache): the game's, never leftovers
            var unfolded = OperatingSystem.IsWindows() && rec.D3DSCacheSince is { } since
                ? D3DSCache.Made(Path.Combine(LocalAppData, "D3DSCache"), WorkDirs(Store.GameDir(s.Game.Id)), since).SelectMany(D3DSCache.CacheFiles) : [];
            Add(CacheGroupKind.Installed, s.Game.Name, GameD3DSCacheAll(s.Game, rec).Concat(unfolded).Where(f => gameD3ds.Add(f.FullName)).ToList(), [s.Game.Id]);
        }

        var gamesDir = Path.Combine(Store.DataDir, "games");
        // a compile deletes the folders its staged exes made: these are an earlier build's. None predates the games folder.
        // ponytail: reads every D3DSCache WAL made since the games folder; cache the verdict per folder if that gets slow
        if (OperatingSystem.IsWindows() && Directory.Exists(gamesDir) && FileTimes.CreatedUtc(gamesDir) is { } gamesMade)
            Add(CacheGroupKind.OldBuild, CompileLeftovers, D3DSCache.Made(Path.Combine(LocalAppData, "D3DSCache"), gamesDir,
                gamesMade, RunningD3DSCache()).SelectMany(D3DSCache.CacheFiles).Where(f => !gameD3ds.Contains(f.FullName)));

        // what the game pages count (CacheOnDiskOf) beyond the folders read above: the files of the games' keys, then Steam's
        // folder of a Vulkan game. Each file goes in the first item that takes it.
        var added = items.Values.SelectMany(i => i.Files).Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);
        var kept = new HashSet<(CacheGroupKind, string)>();
        if (AppCache != null)
        {
            foreach (var by in claims.Keys.Where(k => !readKeys.Contains(k)).Select(k => (Key: k, Owner: Owner(claims, k))).GroupBy(x => (x.Owner.Kind, x.Owner.Name)))
            {
                Add(by.Key.Kind, by.Key.Name, AppCache.FilesOf(by.Select(x => x.Key)).Where(f => added.Add(f.FullName)), by.SelectMany(x => x.Owner.Ids ?? []));
                if (AppCache is MesaLinuxAppCache) kept.Add(by.Key);
            }
            // SCSKiller's own folder: a file of no record's key is an earlier compile's, named by its game's folder
            if (AppCache is NvidiaLinuxAppCache linux)
                foreach (var by in linux.AllKeys().Where(k => !claims.ContainsKey(k)).GroupBy(k => k.Split('/')[0]))
                    Add(CacheGroupKind.OldBuild, by.Key, linux.FilesOf(by).Where(f => added.Add(f.FullName)));
        }
        foreach (var s in Games)
            Add(CacheGroupKind.Installed, s.Game.Name, SteamGlFiles(s.Game, s.Engine).Where(f => added.Add(f.FullName)), [s.Game.Id]);

        // the vendor's OpenGL and Vulkan caches, whatever no game's key took
        var read = folders.Select(f => GameFiles.DirKey(f.Item2)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, label, dir) in VendorFolders().Where(f => OperatingSystem.IsWindows() && f.Vendor == own && !read.Contains(GameFiles.DirKey(f.Dir))))
        {
            Read(dir, out var files, recurse: true);
            Add(CacheGroupKind.Unattributed, label, files.Where(f => added.Add(f.FullName)));
        }
        foreach (var (label, dir) in DriverOwnFolders())
        {
            shown.Add(Read(dir, out var files, recurse: true) with { Label = label });
            Add(CacheGroupKind.Unattributed, label, files.Where(f => added.Add(f.FullName)));
        }

        string? note = null;
        if (OperatingSystem.IsWindows())
        {
            var gpus = (Adapters ?? GpuBackends.Adapters)().Select(a => a.Gpu.Vendor).ToHashSet();
            foreach (var vendor in new[] { GpuVendor.Nvidia, GpuVendor.Amd }.Where(v => v != own))
            {
                foreach (var (_, label, dir) in VendorFolders().Where(f => f.Vendor == vendor))
                {
                    Read(dir, out var files, recurse: true);
                    Add(CacheGroupKind.OtherVendor, label, files);
                }
                if (gpus.Contains(vendor)) note = $"This PC also has an {(vendor == GpuVendor.Nvidia ? "NVIDIA" : "AMD")} GPU. Apps that run on it use this cache.";
            }
        }

        // the folders that hold the game items' files: their size is what the items list, never all of Steam's
        var all = items.Values.SelectMany(i => i.Files).ToList();
        CacheFolder Holding(string label, string dir) => Read(dir, out _) with { Label = label, Bytes = all.Where(f => GameFiles.Inside(dir, f.FullName)).Sum(f => f.Length) };
        if (AppCache is NvidiaLinuxAppCache nl) shown.Add(Holding("Compiled by SCSKiller", nl.GamesDir));
        var shadercache = $"{SteamSource.AppsPart}{Path.DirectorySeparatorChar}shadercache{Path.DirectorySeparatorChar}";
        foreach (var dir in all.Select(f => f.FullName.IndexOf(shadercache, GameFiles.Names) is var at and >= 0 ? f.FullName[..(at + shadercache.Length - 1)] : null)
            .OfType<string>().Distinct(StringComparer.FromComparison(GameFiles.Names)).Order(StringComparer.Ordinal))
            shown.Add(Holding("Steam's shader cache", dir));

        // what the read listed per unit, across items: an older driver's files share a key with the game's current ones
        var listed = items.Values.SelectMany(i => i.Files).GroupBy(f => (Path.GetDirectoryName(f.FullName)!, UnitKey(f.FullName)))
            .ToDictionary(g => g.Key, g => g.Select(f => f.FullName).ToList());
        IReadOnlyCollection<string> Seen(List<FileInfo> files) => files.Select(f => (Path.GetDirectoryName(f.FullName)!, UnitKey(f.FullName))).Distinct()
            .SelectMany(u => listed[u]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = items.Where(i => i.Value.Files.Count > 0).GroupBy(i => i.Key.Kind).OrderBy(g => g.Key)
            .Select(g => new CacheGroup(g.Key,
                g.Select(i => new CacheItem(i.Key.Name, g.Key, i.Value.Files.Select(f => new CacheFile(f.FullName, f.Length, f.LastWriteTimeUtc,
                        own == GpuVendor.Nvidia && NvidiaAppCache.Key(f.Name) != null ? NvidiaAppCache.UsedBytes(f.FullName) : null)).ToList(),
                        i.Value.Ids.Order().ToList(), Seen(i.Value.Files), kept.Contains(i.Key) ? MesaKept : g.Key == CacheGroupKind.Uninstalled ? notes.GetValueOrDefault(i.Key.Name) : null,
                        kept.Contains(i.Key)))
                    .OrderByDescending(p => p.Bytes).ToList(),
                g.Key == CacheGroupKind.OtherVendor ? note : null))
            .ToList();
        return new(shown, groups, oldBefore?.At);
    }

    const string MesaKept = "Steam's pre-cache writes to the same file, so it isn't deleted here.";

    /// <summary>The driver's own cache folders on Linux, where apps and games without Steam's folder for them keep theirs, under
    /// XDG_CACHE_HOME, else ~/.cache: NVIDIA's, Mesa's of each type there (the database, Mesa's default, when none is).</summary>
    IEnumerable<(string Label, string Dir)> DriverOwnFolders()
    {
        if (!OperatingSystem.IsLinux()) return [];
        var home = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x && Path.IsPathRooted(x) ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        (string, string)[] mesa = [("Mesa", Path.Combine(home, "mesa_shader_cache_db")), ("Mesa single file", Path.Combine(home, "mesa_shader_cache_sf")),
            ("Mesa files", Path.Combine(home, "mesa_shader_cache"))];
        return AppCache switch
        {
            NvidiaLinuxAppCache => [("NVIDIA OpenGL and Vulkan", Path.Combine(home, "nvidia", "GLCache"))],
            MesaLinuxAppCache => mesa.Where(m => Directory.Exists(m.Item2)).DefaultIfEmpty(mesa[0]),
            _ => [],
        };
    }

    /// <summary>Driver cache key -> the games that claim it: learned keys, on AMD the exe name's hash, and on NVIDIA every game
    /// whose exe has the same file name (the key is that name's). <paramref name="notes"/> gets uninstalled games' notes.</summary>
    Dictionary<string, List<(CacheGroupKind Kind, string Name, string Id)>> Claims(GpuVendor own, Dictionary<string, string>? notes = null)
    {
        var claims = new Dictionary<string, List<(CacheGroupKind Kind, string Name, string Id)>>(StringComparer.OrdinalIgnoreCase);
        var byExe = new List<(HashSet<string> Exes, CacheGroupKind Kind, string Name, string Id)>();
        void Claim(GameRecord rec, IEnumerable<string?> exeNames, CacheGroupKind kind, string name, string id)
        {
            // opened only by an earlier compile, neither by the game's own process nor by the last compile: the game doesn't read it
            var unused = rec.CacheKeys.Except(LiveKeys(rec)).ToHashSet();
            var keys = rec.CacheKeys.Select(k => (k, unused.Contains(k) ? CacheGroupKind.OldBuild : kind)).ToList();
            if (own == GpuVendor.Amd)   // the name hash is the key of an exe without an app profile; a learned key's place stands
                keys.AddRange(exeNames.OfType<string>().Select(AmdAppCache.HintKey).Where(h => !rec.CacheKeys.Contains(h)).Select(h => (h, kind)));
            foreach (var (k, claimed) in keys)
                (claims.TryGetValue(k, out var l) ? l : claims[k] = []).Add((claimed, name, id));
            // every name it ran or compiled under: a key learned under an earlier exe name is that name's
            byExe.Add((exeNames.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase), kind, name, id));
        }

        var games = Games;
        foreach (var g in games.Select(s => s.Game).Concat(HiddenGames))   // a hidden game is still installed: its cache isn't an uninstalled game's
        {
            var rec = Store.LoadGame(g.Id);
            Claim(rec, [Path.GetFileName(g.ExePath), rec.LaunchedExeName, rec.WarmedExeName], CacheGroupKind.Installed, g.Name, g.Id);
        }
        foreach (var (id, rec) in UninstalledRecords(games))
        {
            var name = UninstalledName(id, rec);
            if (notes != null && rec.RecorderInstallDir is { } dir && Directory.Exists(dir))
                notes[name] = $"Its folder {dir} is still there. It may be on a drive or in a store SCSKiller didn't find this time.";
            Claim(rec, [UninstalledExe(rec), rec.LaunchedExeName, Path.GetFileName(rec.RecorderExe)], CacheGroupKind.Uninstalled, name, id);
        }
        // the key ignores the exe name's path, and on NVIDIA its case (Intel's case is unmeasured): a game with the same exe name reads it too
        if (own is GpuVendor.Nvidia or GpuVendor.Intel)
            foreach (var list in claims.Values)
                foreach (var (kind, _, id) in list.ToList())
                    foreach (var o in byExe.Where(o => o.Id != id && byExe.Any(m => m.Id == id && m.Exes.Overlaps(o.Exes)) && !list.Any(x => x.Id == o.Id)))
                        list.Add((kind == CacheGroupKind.OldBuild ? kind : o.Kind, o.Name, o.Id));
        return claims;
    }

    /// <summary>Whose a key's files are: installed wins over uninstalled, either over a compile's unused key, and every
    /// claimant is named. Ids: the games whose compiled cache it is; null when nobody claims the key.</summary>
    static (CacheGroupKind Kind, string Name, IReadOnlyList<string>? Ids) Owner(Dictionary<string, List<(CacheGroupKind Kind, string Name, string Id)>> claims, string? key)
    {
        if (key == null || !claims.TryGetValue(key, out var by)) return (CacheGroupKind.Unattributed, key ?? NoAppKey, null);
        var kind = by.Min(x => x.Kind);
        return (kind, string.Join(", ", by.Select(x => x.Name).Distinct()), kind is CacheGroupKind.Installed or CacheGroupKind.Uninstalled ? by.Select(x => x.Id).Distinct().Order().ToList() : []);
    }

    /// <summary>Records in the games folder of games not listed now nor hidden, by the id their folder maps to.</summary>
    IEnumerable<(string Id, GameRecord Rec)> UninstalledRecords(IReadOnlyList<GameState> games)
    {
        var gamesDir = Path.Combine(Store.DataDir, "games");
        var listed = games.Select(s => s.Game).Concat(HiddenGames).Select(g => Store.GameDir(g.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in (Directory.Exists(gamesDir) ? Directory.EnumerateDirectories(gamesDir) : []).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (listed.Contains(dir)) continue;
            var id = AppStore.GameId(Path.GetFileName(dir));
            GameRecord rec;
            try { rec = Store.LoadGame(id); }
            catch (ArgumentException) { continue; }   // no id maps to this folder
            yield return (id, rec);
        }
    }

    static string? UninstalledExe(GameRecord rec) => rec.WarmedExeName ?? rec.LaunchedExeName ?? Path.GetFileName(rec.RecorderExe);

    // the record has no game name: its exe's and its folder's
    string UninstalledName(string id, GameRecord rec)
    {
        var folder = Path.GetFileName(Store.GameDir(id));
        return UninstalledExe(rec) is { Length: > 0 } exe ? $"{exe} ({folder})" : folder;
    }

    /// <summary>At: the cutoff (null = no Old driver group). Used: keys a compile or a game used under the current driver.</summary>
    sealed record OldBefore(string? DriverId, DateTime? At, IReadOnlySet<string> Used);
    OldBefore? _oldBefore;

    /// <summary>NVIDIA: files last written before At may be an older driver's. The active driver's install time, bounded by
    /// the earliest compile under it: a Windows upgrade or a reinstall of the same driver makes its DriverStore folder anew.
    /// Kept per driver unless <paramref name="fresh"/>.</summary>
    OldBefore? OldDriverBefore(bool fresh)
    {
        if (AppCache is not NvidiaAppCache) return null;
        var id = DriverId;
        if (!fresh && _oldBefore is { } known && known.DriverId == id) return known;
        var at = DriverInstalledAt();
        var recs = Games.Select(s => s.Game).Concat(HiddenGames).Select(g => Store.LoadGame(g.Id)).Concat(UninstalledRecords(Games).Select(r => r.Rec)).ToList();
        var warmed = recs.Where(r => r.WarmedDriverId != null && r.WarmedDriverId == id).ToList();
        foreach (var rec in warmed)
            if (rec.WarmedAt?.UtcDateTime < at) at = rec.WarmedAt.Value.UtcDateTime;
        var used = warmed.SelectMany(r => r.WarmedKeys ?? r.CacheKeys)
            .Concat(recs.Where(r => r.LastPlay is { } p && !(p.To.UtcDateTime < at)).SelectMany(r => r.GameKeys))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _oldBefore = new(id, at, used);
    }

    /// <summary>Paths of <paramref name="files"/> (an NVIDIA cache folder's) an older driver left, by whole app key: every
    /// file of the key last written before the cutoff and of a type the current driver hasn't written since (it reuses an
    /// older driver's files of the types it still writes), the key not used under it, no file of it held open. None while
    /// the current driver wrote nothing yet: which types it reads is unknown then.</summary>
    static HashSet<string> OldDriverFiles(IEnumerable<FileInfo> files, OldBefore old)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (old.At is not { } before) return found;
        var keyed = files.Where(f => NvidiaAppCache.Key(f.Name) != null).ToList();
        var since = keyed.Where(f => f.LastWriteTimeUtc >= before).ToList();
        if (since.Count == 0) return found;
        var types = since.Select(f => f.Name[..4]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = since.Select(f => NvidiaAppCache.Key(f.Name)!).Concat(old.Used).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keyed.GroupBy(f => NvidiaAppCache.Key(f.Name)!, StringComparer.OrdinalIgnoreCase))
            if (!current.Contains(key.Key) && key.All(f => f.LastWriteTimeUtc < before && !types.Contains(f.Name[..4]))
                && key.All(f => AppCacheFiles.InUse(f.FullName) == false))
                found.UnionWith(key.Select(f => f.FullName));
        return found;
    }

    /// <summary>The Old driver group's files in the NVIDIA cache folder now; none off NVIDIA.</summary>
    HashSet<string> OldDriverFiles(bool fresh) =>
        OldDriverBefore(fresh) is { } old && AppCache is NvidiaAppCache nv && AppCacheFiles.Readable(nv.Dir)
            ? OldDriverFiles(new DirectoryInfo(nv.Dir).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }).ToList(), old)
            : new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bytes of the Old driver group, read without the rest of the inventory.</summary>
    public long OldDriverBytes() => OldDriverFiles(fresh: false).Sum(p => new FileInfo(p) is { Exists: true } f ? f.Length : 0);

    /// <summary>The folder as the page shows it, and its files: none when it is missing or can't be read. A link below it is
    /// never followed: its files may be anything.</summary>
    static CacheFolder Read(string dir, out List<FileInfo> files, bool recurse = false)
    {
        var final = GameFiles.FinalPath(dir);
        var exists = Directory.Exists(dir);
        // a link: its final path isn't its parent's plus its name (so short names and case don't count)
        var linked = final != null && Path.GetDirectoryName(dir) is { } parent && GameFiles.FinalPath(parent) is { } p
            && !string.Equals(final, Path.Combine(p, Path.GetFileName(dir)), StringComparison.OrdinalIgnoreCase);
        files = [];
        if (final != null)
            try
            {
                files = new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = recurse, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                }).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new("", dir, exists, linked ? final : null, exists && final == null, files.Sum(f => f.Length));
    }

    /// <summary>The folders a Cache page delete may take files from: the driver caches of both vendors, D3DSCache, and on
    /// Linux SCSKiller's per-game NVIDIA caches and the driver's own folders. Steam's folders go only through
    /// <see cref="SteamGlHeld"/>, Mesa's per-game file never.</summary>
    IEnumerable<string> CacheRoots() =>
        (AppCache switch { NvidiaAppCache nv => [nv.Dir], AmdAppCache amd => new[] { amd.DxcDir, amd.DxDir }, IntelAppCache intel => [intel.Dir], NvidiaLinuxAppCache linux => [linux.GamesDir], _ => [] })
            .Concat(VendorFolders().Select(f => f.Dir))
            .Concat(DriverOwnFolders().Select(f => f.Dir))
            .Append(Path.Combine(LocalAppData, "D3DSCache"))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deletes the files of <paramref name="items"/> as the Cache page read them. Throws InvalidOperationException,
    /// deleting nothing, while a compile runs. Skips, with the reason, an item whose game runs or compiles, and a unit
    /// (one app key's files, else one folder's) with a file that changed since the page read it, sits outside the cache
    /// folders or behind a link inside them, or is in use. A unit goes whole or not at all. Games whose compiled files went
    /// show as needing a compile. A game's item goes whole or not at all: one listed game's through its Clear cache
    /// (<see cref="ClearGameCache"/>, its Steam files and its Windows shader cache too), any other's when all its units can.
    /// A kept item is never deleted.</summary>
    public CacheDeleteResult DeleteCaches(IReadOnlyList<CacheItem> items)
    {
        // held for the whole delete: a compile worker in any process checks it after taking its own hold, and this checks after
        using var deleting = Busy.Hold(DeletingName);
        // an item past Done still refreshes its game under our Busy hold, which isn't another process's compile
        if ((CompileRunning ?? (() => ItemRunning || !Compiling && Busy.IsHeld()))())
            throw new InvalidOperationException("a compile is in progress: delete caches once it has finished");
        var skipped = new List<string>();
        long bytes = 0;
        int count = 0;
        var games = Games.ToDictionary(s => s.Game.Id);
        var names = ProcessNames();
        var roots = CacheRoots().Select(r => (Root: r, Final: GameFiles.FinalPath(r))).Where(r => r.Final != null).ToList();
        var own = AppCache switch { NvidiaAppCache => GpuVendor.Nvidia, AmdAppCache => GpuVendor.Amd, IntelAppCache => GpuVendor.Intel, _ => GpuVendor.Unknown };
        var claims = Claims(own);   // who claims each key now: a game seen running since the page read may have learned one
        var held = new Dictionary<string, FileStream?>();   // one lock per game: an id in two items is the same lock
        var gameD3ds = Games.Select(s => s.Game).Concat(HiddenGames).SelectMany(g => GameD3DSCacheDirs(g, Store.LoadGame(g.Id)).Select(d => (Dir: d, g.Id)))
            .ToLookup(x => Path.GetFullPath(x.Dir), x => x.Id, StringComparer.OrdinalIgnoreCase);
        var cleared = new HashSet<string>();
        try
        {
            foreach (var item in items.Where(i => !i.Kept))
            {
                var running = item.GameIds.Select(id => games.TryGetValue(id, out var s) ? CacheInUse(s.Game) ? s.Game.Name : null
                    : UninstalledExe(Store.LoadGame(id)) is { } exe && names.Contains(Path.GetFileNameWithoutExtension(exe)) ? exe : null).FirstOrDefault(n => n != null);
                if (running != null)
                {
                    skipped.Add($"{item.Name}: {running} is running");
                    continue;
                }
                if (item is { Kind: CacheGroupKind.Installed, GameIds: [var only] } && games.ContainsKey(only))
                {
                    // the game page's Clear cache, which takes the game's lock itself
                    if (!cleared.Add(only)) continue;
                    if (held.Remove(only, out var had)) had?.Dispose();
                    try
                    {
                        if (PairHoldsCache(only) is { } why) throw new InvalidOperationException(why);
                        ClearCache(only, gamePrecache: false, steam: true, locked: true, out var freed);
                        (bytes, count) = (bytes + freed.Bytes, count + freed.Files);
                    }
                    catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { skipped.Add($"{item.Name}: {e.Message}"); }
                    continue;
                }
                // a compile of these games, here or in another process, waits for the delete
                foreach (var id in item.GameIds) if (!held.ContainsKey(id)) held[id] = CompileLock(id);
                if (item.GameIds.Any(id => held[id] == null))
                {
                    skipped.Add($"{item.Name}: a compile of it is in progress");
                    continue;
                }
                var deleted = false;
                // the Old driver group as it is now: a key the driver or a process took up since the page read stays
                var oldNow = item.Kind == CacheGroupKind.OldDriver ? OldDriverFiles(fresh: true) : null;
                string? Refusal(string dir, string? key, List<CacheFile> unit)
                {
                    var fileKey = OwnKey(unit[0].Path);
                    var owner = fileKey != null && item.Kind != CacheGroupKind.OldDriver ? Owner(claims, fileKey) : default;
                    if (item.Kind == CacheGroupKind.OldDriver && !unit.All(f => oldNow!.Contains(Path.GetFullPath(f.Path)))) return "the current driver uses these files";
                    // a game's own D3DSCache folder goes only with that game's cache
                    if (gameD3ds[Path.GetFullPath(dir)].ToList() is { Count: > 0 } d3dsOf && !(item.Kind == CacheGroupKind.Installed && d3dsOf.All(item.GameIds.Contains)))
                        return "it is a game's own Windows shader cache";
                    return Unsafe(unit, roots) ?? Incomplete(dir, key, item.Seen.Concat(unit.Select(f => f.Path)))
                        ?? (owner.Ids is { } ids && (owner.Kind != item.Kind || ids.Except(item.GameIds).Any()) ? $"these files are now {owner.Name}'s" : null);
                }
                var game = item.Kind is CacheGroupKind.Installed or CacheGroupKind.Uninstalled;
                var units = item.Files.GroupBy(f => (Dir: Path.GetDirectoryName(f.Path)!, Key: UnitKey(f.Path))).ToList();
                // a game's units all pass, then go in one delete
                var refusal = game ? units.Select(u => Refusal(u.Key.Dir, u.Key.Key, u.ToList())).FirstOrDefault(w => w != null) : null;
                if (refusal != null)
                {
                    skipped.Add($"{item.Name}: {refusal}");
                    continue;
                }
                if (game)
                {
                    try { AppCacheFiles.DeleteAll(item.Files.Select(f => new FileInfo(f.Path)).ToList()); }
                    catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        skipped.Add($"{item.Name}: {e.Message}");
                        continue;
                    }
                    (bytes, count, deleted) = (bytes + item.Bytes, count + item.Files.Count, true);
                    units = [];
                }
                foreach (var unit in units)
                {
                    var why = Refusal(unit.Key.Dir, unit.Key.Key, unit.ToList());
                    if (why != null)
                    {
                        skipped.Add($"{item.Name}: {why}");
                        continue;
                    }
                    try { AppCacheFiles.DeleteAll(unit.Select(f => new FileInfo(f.Path)).ToList()); }
                    catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        skipped.Add($"{item.Name}: {e.Message}");
                        continue;
                    }
                    (bytes, count, deleted) = (bytes + unit.Sum(f => f.Length), count + unit.Count(), true);
                    if (GameFiles.Inside(Path.Combine(LocalAppData, "D3DSCache"), unit.Key.Dir))
                        try { Directory.Delete(unit.Key.Dir); }
                        catch (IOException) { }   // not empty: the runtime made a file meanwhile, left alone
                }
                if (!deleted) continue;
                foreach (var id in item.GameIds)
                {
                    ForgetWarm(id, Store.LoadGame(id));
                    if (games.TryGetValue(id, out var s)) Refresh(s.Game);
                }
            }
        }
        finally { foreach (var h in held.Values) h?.Dispose(); }
        return new(bytes, count, skipped);
    }

    /// <summary>Files go in units, whole or not at all: one app key's (on Linux an NVIDIA set's, as <see cref="NvidiaLinuxAppCache"/>
    /// keys it), else one folder's (a D3DSCache folder's db with its WAL).</summary>
    internal static string? UnitKey(string path) => NvidiaAppCache.Key(Path.GetFileName(path)) ?? AmdAppCache.Key(Path.GetFileName(path), false) ?? IntelAppCache.KeyIn(path)
        ?? NvidiaAppCache.LegacyKey(path)
        ?? (OperatingSystem.IsWindows() ? AppCacheFiles.VulkanKey(VulkanPlan.DefaultCache(GpuVendor.Nvidia)!, path, NvidiaAppCache.VulkanTypes)
            ?? AppCacheFiles.VulkanKey(VulkanPlan.DefaultCache(GpuVendor.Amd)!, path, AmdAppCache.VulkanTypes) : null)
        ?? (NvidiaLinuxAppCache.NoLinkFolders(path) is (_, [var game, _, _, _, var a, var b]) ? $"{game}/{a}/{b}/{Path.GetFileNameWithoutExtension(path)}" : null);

    /// <summary>The key the active driver's claims use for a file in one of its cache folders; null elsewhere.</summary>
    string? OwnKey(string path)
    {
        var dir = Path.GetDirectoryName(path);
        bool In(string d) => string.Equals(GameFiles.DirKey(d), GameFiles.DirKey(dir!), StringComparison.OrdinalIgnoreCase);
        return AppCache switch
        {
            NvidiaAppCache nv when In(nv.Dir) => NvidiaAppCache.Key(Path.GetFileName(path)) ?? NvidiaAppCache.LegacyKey(path),
            AmdAppCache amd when In(amd.DxcDir) => AmdAppCache.Key(Path.GetFileName(path), true),
            AmdAppCache amd when In(amd.DxDir) => AmdAppCache.Key(Path.GetFileName(path), false),
            IntelAppCache intel when In(intel.Dir) => IntelAppCache.Key(Path.GetFileName(path)),
            NvidiaAppCache { VulkanDir: { } vk } => AppCacheFiles.VulkanKey(vk, path, NvidiaAppCache.VulkanTypes),
            AmdAppCache { VkDir: { } vk } => AppCacheFiles.VulkanKey(vk, path, AmdAppCache.VulkanTypes),
            NvidiaLinuxAppCache linux when GameFiles.Inside(linux.GamesDir, path) => UnitKey(path),
            _ => null,
        };
    }

    /// <summary>Why deleting the unit's listed files would leave part of it: a link among its files, or a file the read
    /// didn't list (<paramref name="seen"/>; a key split, a WAL without its db). Decided by the listing, not by file times,
    /// which come from a coarser clock than the read's.</summary>
    static string? Incomplete(string dir, string? key, IEnumerable<string> seen)
    {
        var known = seen.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0 }).Where(f => UnitKey(f.FullName) == key))
                // a link among them would stay behind, the rest of its key gone
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0) return $"{f.Name} is a link";
                else if (!known.Contains(f.FullName)) return $"{f.Name} was added since the page read it";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"{Path.GetFileName(dir)} can't be read: {e.Message}"; }
    }

    /// <summary>Why a unit's files can't be deleted as the page read them; null when they can.</summary>
    static string? Unsafe(IReadOnlyList<CacheFile> unit, List<(string Root, string? Final)> roots)
    {
        foreach (var f in unit)
        {
            var path = Path.GetFullPath(f.Path);
            if (path.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith("fozpipelines", StringComparison.OrdinalIgnoreCase)))
                return $"{Path.GetFileName(path)} is in Steam's pipeline archives";
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != f.Length || info.LastWriteTimeUtc != f.WrittenUtc) return $"{info.Name} changed since the page read it";
            if (roots.FirstOrDefault(r => GameFiles.Inside(r.Root, path)) is not { Root: { } root, Final: { } final })
                return $"{info.Name} is outside the driver's cache folders";
            // the cache folder itself may be a link (moved to another drive); nothing below it may be
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0
                || !string.Equals(GameFiles.Identity(path)?.Final, Path.Combine(final, Path.GetRelativePath(root, path)), StringComparison.OrdinalIgnoreCase))
                return $"{info.Name} is reached through a link inside the cache folder";
        }
        return null;
    }

    /// <summary>The "Clear everything" warning: every game compiles again, and whether Other apps and games is included.</summary>
    public static string EverythingWarning(CacheInventory inv, bool withOthers) =>
        $"Deletes {Format.Bytes(inv.Everything(withOthers).Sum(i => i.Bytes))}. Every game compiles its shaders again. Games SCSKiller compiled stutter until you compile them again, and the rest build "
        + "their cache as you play. " + (withOthers
            ? $"The {OtherApps.ToLowerInvariant()} group ({Format.Bytes(inv.Bytes(CacheGroupKind.Unattributed))}) is included: apps and games outside SCSKiller build their caches again too."
            : $"The {OtherApps.ToLowerInvariant()} group ({Format.Bytes(inv.Bytes(CacheGroupKind.Unattributed))}) is kept unless you tick it.");

    /// <summary>The D3DSCache folders of the games running now, whose dbs the runtime holds open: never read.</summary>
    HashSet<string> RunningD3DSCache() => Games.Where(s => CacheInUse(s.Game))
        .SelectMany(s => D3DSCacheNames(Store.LoadGame(s.Game.Id)).Append(D3DSCache.FolderName(s.Game.ExePath))
            .Concat(s.Game.Store == Core.Store.Xbox && XboxSource.LaunchPath(s.Game) is { } x ? [D3DSCache.FolderName(x)] : []))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
