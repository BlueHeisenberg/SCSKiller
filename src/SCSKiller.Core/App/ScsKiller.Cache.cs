using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.App;

/// <summary>A driver cache folder: the driver reads existing caches and writes new ones there. Final: where its files are
/// when the folder is a link; TargetMissing: a link whose target is gone; Cap: the driver's fixed limit on it, null = none.</summary>
public sealed record CacheFolder(string Label, string Path, bool Exists, string? Final, bool TargetMissing, long Bytes, long? Cap = null);

/// <summary>In the order the Cache page shows them, which is also the order a key claimed twice goes by.</summary>
public enum CacheGroupKind { Installed, Uninstalled, OldDriver, OldBuild, OtherVendor, Unattributed }

/// <summary>A cache file as the page read it: a delete takes it only while it is still the same.</summary>
public sealed record CacheFile(string Path, long Length, DateTime WrittenUtc);

/// <summary>Files that belong together. GameIds: the games whose compiled cache this is (Installed and Uninstalled items):
/// deleting it resets their compile. Seen: every file the read listed in the item's units (<see cref="ScsKiller.UnitKey"/>),
/// other items' included; a file of a unit not in it was added since. Note: shown with the item.</summary>
public sealed record CacheItem(string Name, CacheGroupKind Kind, IReadOnlyList<CacheFile> Files, IReadOnlyList<string> GameIds,
    IReadOnlyCollection<string> Seen, string? Note = null)
{
    public long Bytes => Files.Sum(f => f.Length);
}

/// <summary>A group of the Cache page; each item's files belong together.</summary>
public sealed record CacheGroup(CacheGroupKind Kind, IReadOnlyList<CacheItem> Items, string? Note = null)
{
    public long Bytes => Items.Sum(i => i.Bytes);
}

/// <summary>DriverInstalled: what Old driver files are older than; null = no Old driver group.</summary>
public sealed record CacheInventory(IReadOnlyList<CacheFolder> Folders, IReadOnlyList<CacheGroup> Groups, DateTime? DriverInstalled = null)
{
    /// <summary>What "Clear everything" deletes: every group but Other apps and games, unless the user ticked it.</summary>
    public IReadOnlyList<CacheItem> Everything(bool withOthers) =>
        Groups.Where(g => withOthers || g.Kind != CacheGroupKind.Unattributed).SelectMany(g => g.Items).ToList();

    public long Bytes(CacheGroupKind kind) => Groups.Where(g => g.Kind == kind).Sum(g => g.Bytes);
}

/// <summary>What a delete freed, and each unit of files it left with the reason.</summary>
public sealed record CacheDeleteResult(long Bytes, int Files, IReadOnlyList<string> Skipped);

// The Cache page: every driver cache file on the PC, each in one item. Only names, sizes and write times are read, never
// contents (AMD evicts by access time).
public sealed partial class ScsKiller
{
    /// <summary>When the active driver was installed (NVIDIA: <see cref="NvidiaBackend.InstalledAt"/>); null = unknown.
    /// Replaceable for tests.</summary>
    public Func<DateTime?> DriverInstalledAt { get; set; } = NvidiaBackend.InstalledAt;

    /// <summary>A compile or warm runs in any SCSKiller process: no cache is deleted meanwhile. Null = this process's queue
    /// or the session-wide compile mutex (<see cref="Busy"/>). Replaceable for tests: other tests hold that mutex.</summary>
    public Func<bool>? CompileRunning { get; set; }

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

    static readonly (GpuVendor Vendor, string Label, string Folder)[] VendorFolders =
    [
        (GpuVendor.Nvidia, "NVIDIA DirectX", @"NVIDIA\DXCache"), (GpuVendor.Nvidia, "NVIDIA OpenGL and Vulkan", @"NVIDIA\GLCache"),
        (GpuVendor.Amd, "AMD DirectX 12", @"AMD\DxcCache"), (GpuVendor.Amd, "AMD DirectX 11", @"AMD\DxCache"),
        (GpuVendor.Amd, "AMD Vulkan", @"AMD\VkCache"), (GpuVendor.Amd, "AMD OpenGL", @"AMD\GLCache"),
    ];

    public CacheInventory CacheInventory()
    {
        var (own, folders) = AppCache switch
        {
            NvidiaAppCache nv => (GpuVendor.Nvidia, new List<(string, string, bool)> { ("DirectX 12 and 11", nv.Dir, true) }),
            AmdAppCache amd => (GpuVendor.Amd, [("DirectX 12", amd.DxcDir, true), ("DirectX 11", amd.DxDir, false)]),
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
        string? KeyOf(FileInfo f, bool d3d12) => own == GpuVendor.Nvidia ? NvidiaAppCache.Key(f.Name) : AmdAppCache.Key(f.Name, d3d12);
        var shown = new List<CacheFolder>();
        foreach (var (label, dir, d3d12) in folders)
        {
            shown.Add(Read(dir, out var files) with { Label = label, Cap = AppCache is AmdAppCache amd && dir == amd.DxcDir ? AmdAppCache.DxcCacheCap : null });
            foreach (var f in files)
            {
                var key = KeyOf(f, d3d12);
                var (kind, name, ids) = Owner(claims, key);
                if (f.LastWriteTimeUtc < oldBefore) Add(CacheGroupKind.OldDriver, ids != null ? name : OtherApps, [f]);
                else Add(kind, name, [f], ids);
            }
        }

        var gamesDir = Path.Combine(Store.DataDir, "games");
        // a compile deletes the folders its staged exes made: these are an earlier build's. None predates the games folder.
        // ponytail: reads every D3DSCache WAL made since the games folder; cache the verdict per folder if that gets slow
        if (Directory.Exists(gamesDir))
            Add(CacheGroupKind.OldBuild, CompileLeftovers, D3DSCache.Made(Path.Combine(LocalAppData, "D3DSCache"), gamesDir,
                Directory.GetCreationTimeUtc(gamesDir)).SelectMany(D3DSCache.CacheFiles));

        // the vendor's OpenGL and Vulkan caches: their files carry no key SCSKiller learns
        var read = folders.Select(f => GameFiles.DirKey(f.Item2)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, label, folder) in VendorFolders.Where(f => f.Vendor == own && !read.Contains(GameFiles.DirKey(Path.Combine(LocalAppData, f.Folder)))))
        {
            Read(Path.Combine(LocalAppData, folder), out var files, recurse: true);
            Add(CacheGroupKind.Unattributed, label, files);
        }

        string? note = null;
        var gpus = (Adapters ?? GpuBackends.Adapters)().Select(a => a.Gpu.Vendor).ToHashSet();
        foreach (var vendor in new[] { GpuVendor.Nvidia, GpuVendor.Amd }.Where(v => v != own))
        {
            foreach (var (_, label, folder) in VendorFolders.Where(f => f.Vendor == vendor))
            {
                Read(Path.Combine(LocalAppData, folder), out var files, recurse: true);
                Add(CacheGroupKind.OtherVendor, label, files);
            }
            if (gpus.Contains(vendor)) note = $"This PC also has an {(vendor == GpuVendor.Nvidia ? "NVIDIA" : "AMD")} GPU. Apps that run on it use this cache.";
        }

        // what the read listed per unit, across items: an older driver's files share a key with the game's current ones
        var listed = items.Values.SelectMany(i => i.Files).GroupBy(f => (Path.GetDirectoryName(f.FullName)!, UnitKey(f.FullName)))
            .ToDictionary(g => g.Key, g => g.Select(f => f.FullName).ToList());
        IReadOnlyCollection<string> Seen(List<FileInfo> files) => files.Select(f => (Path.GetDirectoryName(f.FullName)!, UnitKey(f.FullName))).Distinct()
            .SelectMany(u => listed[u]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = items.Where(i => i.Value.Files.Count > 0).GroupBy(i => i.Key.Kind).OrderBy(g => g.Key)
            .Select(g => new CacheGroup(g.Key,
                g.Select(i => new CacheItem(i.Key.Name, g.Key, i.Value.Files.Select(f => new CacheFile(f.FullName, f.Length, f.LastWriteTimeUtc)).ToList(),
                        i.Value.Ids.Order().ToList(), Seen(i.Value.Files), g.Key == CacheGroupKind.Uninstalled ? notes.GetValueOrDefault(i.Key.Name) : null))
                    .OrderByDescending(p => p.Bytes).ToList(),
                g.Key == CacheGroupKind.OtherVendor ? note : null))
            .ToList();
        return new(shown, groups, oldBefore);
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
            var unused = rec.GameKeys.Count > 0 && rec.WarmedKeys is { } warmed ? rec.CacheKeys.Except(rec.GameKeys).Except(warmed).ToHashSet() : [];
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
        if (own == GpuVendor.Nvidia)   // the key ignores the exe name's case and path: a game with the same exe name reads it too
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
        foreach (var dir in Directory.Exists(gamesDir) ? Directory.EnumerateDirectories(gamesDir) : [])
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

    sealed record OldBefore(string? DriverId, DateTime? At);
    OldBefore? _oldBefore;

    /// <summary>NVIDIA: files last written before this are an older driver's. The active driver's install time, bounded by
    /// the earliest compile under it: a Windows upgrade or a reinstall of the same driver makes its DriverStore folder anew.
    /// Kept per driver unless <paramref name="fresh"/>.</summary>
    DateTime? OldDriverBefore(bool fresh)
    {
        if (AppCache is not NvidiaAppCache) return null;
        var id = DriverId;
        if (!fresh && _oldBefore is { } known && known.DriverId == id) return known.At;
        var at = DriverInstalledAt();
        foreach (var rec in Games.Select(s => Store.LoadGame(s.Game.Id)).Concat(UninstalledRecords(Games).Select(r => r.Rec)))
            if (rec.WarmedDriverId != null && rec.WarmedDriverId == id && rec.WarmedAt?.UtcDateTime < at) at = rec.WarmedAt.Value.UtcDateTime;
        _oldBefore = new(id, at);
        return at;
    }

    /// <summary>Bytes of the NVIDIA cache files an older driver wrote (the Cache page's Old driver group), read without the
    /// rest of the inventory.</summary>
    public long OldDriverBytes() =>
        OldDriverBefore(fresh: false) is { } before && AppCache is NvidiaAppCache nv && AppCacheFiles.Readable(nv.Dir)
            ? new DirectoryInfo(nv.Dir).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }).Where(f => f.LastWriteTimeUtc < before).Sum(f => f.Length)
            : 0;

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

    /// <summary>The folders a Cache page delete may take files from: the driver caches of both vendors and D3DSCache.</summary>
    IEnumerable<string> CacheRoots() =>
        (AppCache switch { NvidiaAppCache nv => [nv.Dir], AmdAppCache amd => new[] { amd.DxcDir, amd.DxDir }, _ => [] })
            .Concat(VendorFolders.Select(f => Path.Combine(LocalAppData, f.Folder)))
            .Append(Path.Combine(LocalAppData, "D3DSCache"))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deletes the files of <paramref name="items"/> as the Cache page read them. Throws InvalidOperationException,
    /// deleting nothing, while a compile runs. Skips, with the reason, an item whose game runs or compiles, and a unit
    /// (one app key's files, else one folder's) with a file that changed since the page read it, sits outside the cache
    /// folders or behind a link inside them, or is in use. A unit goes whole or not at all. Games whose compiled files went
    /// show as needing a compile.</summary>
    public CacheDeleteResult DeleteCaches(IReadOnlyList<CacheItem> items)
    {
        // held for the whole delete: a compile worker in any process checks it after taking its own hold, and this checks after
        using var deleting = Busy.Hold(DeletingName);
        if ((CompileRunning ?? (() => Compiling || Busy.IsHeld()))())
            throw new InvalidOperationException("a compile is in progress: delete caches once it has finished");
        var skipped = new List<string>();
        long bytes = 0;
        int count = 0;
        var games = Games.ToDictionary(s => s.Game.Id);
        var names = ProcessNames();
        var roots = CacheRoots().Select(r => (Root: r, Final: GameFiles.FinalPath(r))).Where(r => r.Final != null).ToList();
        var own = AppCache switch { NvidiaAppCache => GpuVendor.Nvidia, AmdAppCache => GpuVendor.Amd, _ => GpuVendor.Unknown };
        var claims = Claims(own);   // who claims each key now: a game seen running since the page read may have learned one
        var held = new Dictionary<string, FileStream?>();   // one lock per game: an id in two items is the same lock
        try
        {
            foreach (var item in items)
            {
                var running = item.GameIds.Select(id => games.TryGetValue(id, out var s) ? GameRunning(s.Game) ? s.Game.Name : null
                    : UninstalledExe(Store.LoadGame(id)) is { } exe && names.Contains(Path.GetFileNameWithoutExtension(exe)) ? exe : null).FirstOrDefault(n => n != null);
                if (running != null)
                {
                    skipped.Add($"{item.Name}: {running} is running");
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
                foreach (var unit in item.Files.GroupBy(f => (Dir: Path.GetDirectoryName(f.Path)!, Key: UnitKey(f.Path))))
                {
                    var key = OwnKey(unit.First().Path);
                    var owner = key != null && item.Kind != CacheGroupKind.OldDriver ? Owner(claims, key) : default;
                    var why = Unsafe(unit.ToList(), roots)
                        ?? Incomplete(unit.Key.Dir, unit.Key.Key, item.Seen.Concat(unit.Select(f => f.Path)))
                        ?? (owner.Ids is { } ids && (owner.Kind != item.Kind || ids.Except(item.GameIds).Any()) ? $"these files are now {owner.Name}'s" : null);
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

    /// <summary>Files go in units, whole or not at all: one app key's, else one folder's (a D3DSCache folder's db with its WAL).</summary>
    internal static string? UnitKey(string path) => NvidiaAppCache.Key(Path.GetFileName(path)) ?? AmdAppCache.Key(Path.GetFileName(path), false);

    /// <summary>The key the active driver's claims use for a file in one of its cache folders; null elsewhere.</summary>
    string? OwnKey(string path)
    {
        var dir = Path.GetDirectoryName(path);
        bool In(string d) => string.Equals(GameFiles.DirKey(d), GameFiles.DirKey(dir!), StringComparison.OrdinalIgnoreCase);
        return AppCache switch
        {
            NvidiaAppCache nv when In(nv.Dir) => NvidiaAppCache.Key(Path.GetFileName(path)),
            AmdAppCache amd when In(amd.DxcDir) => AmdAppCache.Key(Path.GetFileName(path), true),
            AmdAppCache amd when In(amd.DxDir) => AmdAppCache.Key(Path.GetFileName(path), false),
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
}
