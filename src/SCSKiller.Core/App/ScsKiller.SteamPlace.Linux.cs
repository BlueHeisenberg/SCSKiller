using System.Globalization;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    readonly object _placeLock = new();
    readonly Dictionary<string, DateTimeOffset> _placeDue = [];             // game id -> when its cache in Steam's folder is looked at next; under _placeLock
    readonly Dictionary<string, SteamPlaceResult> _placeLast = [];          // game id -> what the last look did; under _placeLock
    readonly Dictionary<string, List<FileSystemWatcher>> _placeWatch = [];  // game id -> watchers of its Steam folder and working folder; the placer's thread
    readonly Dictionary<string, string> _slotSaid = [];                      // game id -> what the look for its own slot logged this run; under _placeLock
    readonly Dictionary<string, string> _vkd3dOff = [];                      // game id -> why its vkd3d-proton cache isn't placed, as logged this run; under _placeLock
    string? _placing;                                                        // the game being placed now; under _placeLock
    readonly Dictionary<string, GameCacheWhere?> _where = [];                // game id -> where its driver was seen reading (GameCacheWhere); under _placeLock

    /// <summary>How long the placer waits after the game exits or a watched file changes: Steam merges the game's cache
    /// after it exits, and a game writes its own files in bursts.</summary>
    static readonly TimeSpan PlaceSettle = TimeSpan.FromSeconds(30);

    /// <summary>A Steam game on NVIDIA's Linux driver or on Mesa, in a Steam library or a non-Steam game added to Steam, that
    /// Proton runs (a Windows exe), or a native one on the Vulkan route (<see cref="PlaceNativeVulkan"/>): its compiled cache goes
    /// in Steam's own cache folder for it (<see cref="MesaLinux.SteamFolder"/>), which Steam's environment points the driver at.
    /// A Vulkan game's is what the native replayer wrote, on NVIDIA under the same app name and folder as a D3D12 warm's
    /// (<see cref="ProtonWarmHost"/>), on Mesa in the game's own file (<see cref="MesaLinux.Caps"/>). Its data folder, the
    /// warm's app name, that folder and the app id; null for any other game. A launcher's game (<see cref="LauncherPlaced"/>):
    /// no Steam folder ("") and app 0.</summary>
    (string GameDir, string AppName, string SteamDir, uint App)? SteamPlaceScope(GameState s)
    {
        if (Vendor.Caps.Profile is not (NvidiaLinux.Profile or MesaLinux.Profile) || Vendor is UnsupportedVendor || s.Status == GameStatus.Unsupported
            || !(s.Game.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || PlaceNativeVulkan && VulkanRoute(s.Game, s.Engine)))
            return null;
        var (gameDir, appName) = (Store.GameDir(s.Game.Id), Wine.WarmAppName(Path.GetFileName(s.Game.ExePath)));
        if (MesaLinux.SteamFolder(s.Game, PlaceSteamRoot) is var (apps, app))
            return (gameDir, appName, Path.Combine(apps, "shadercache", app.ToString(CultureInfo.InvariantCulture)), app);
        return LauncherPlaced(s) ? (gameDir, appName, "", 0) : null;
    }

    /// <summary>A Windows game of Heroic, Lutris or Faugus on NVIDIA's driver, outside a Flatpak: its compile goes only into the
    /// game's own file in the driver's default folder, once the game was seen reading it (<see cref="LearnWhere"/>), never
    /// into a Steam folder, and vkd3d-proton's cache isn't placed.</summary>
    bool LauncherPlaced(GameState s) =>
        Vendor.Caps.Profile == NvidiaLinux.Profile && !VulkanRoute(s.Game, s.Engine) && s.Game.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && LauncherPrefix.Is(s.Game) && LauncherPrefix.Of(s.Game, out _) is { CacheHome: null };

    /// <summary>A launcher's game on NVIDIA's driver in a Flatpak: the sandbox's paths aren't the host's, so its compile stays
    /// in the game's own folder.</summary>
    bool LauncherFlatpak(GameState s) =>
        Vendor.Caps.Profile == NvidiaLinux.Profile && LauncherPrefix.Is(s.Game) && LauncherPrefix.Of(s.Game, out _) is { CacheHome: not null };

    /// <summary>A native Linux game on the Vulkan route gets its cache in Steam's folder too, taking Steam's environment for
    /// it to be the one Steam gives a game under Proton.</summary>
    const bool PlaceNativeVulkan = true;

    string PlaceSteamRoot => (_warmer as Warmer)?.SteamRoot ?? SteamSource.Root();

    // Mesa's warm writes the file Steam points the game at itself (MesaLinux.SteamCache): only vkd3d-proton's cache is placed
    IReadOnlyList<SteamTarget> PlaceTargets => MesaLinux.Is(Vendor.Caps) ? [] : NvidiaLinux.SteamTargets;

    /// <summary>The placer of the game's cache in Steam's folder, or in the driver's folder outside it the game was seen
    /// reading (<see cref="GameCacheWhere.Set"/>), its paths found now; null for a game out of <see cref="SteamPlaceScope"/>.
    /// Where the compile can't go (<see cref="Unplaceable"/>) it places vkd3d-proton's cache alone, except to take it out.</summary>
    SteamCachePlace? SteamPlacer(GameState s, Action? writing = null, bool remove = false)
    {
        if (SteamPlaceScope(s) is not var (gameDir, appName, steamDir, app)) return null;
        var root = PlaceSteamRoot;
        var exe = Path.GetFileName(s.Game.ExePath);
        var leaf = Store.LoadGame(s.Game.Id).WarmedKeys?.Select(k => k.Split('/')).Where(p => p.Length == 4 && p[3].StartsWith(appName, StringComparison.Ordinal))
            .Select(p => $"{p[1]}/{p[2]}").Distinct().Order(StringComparer.Ordinal).FirstOrDefault();
        var vkd3d = Path.Combine(Wine.CacheRoot(gameDir, make: false), "vkd3d");
        var vulkan = VulkanRoute(s.Game, s.Engine);   // its pipelines reach the driver without vkd3d-proton
        var launcher = steamDir.Length == 0;
        string? off = null;
        var work = vulkan || launcher ? null : WorkingFolder(s.Game, root, app, gameDir, out off);
        bool tell;
        lock (_placeLock)
        {
            tell = off != null && _vkd3dOff.GetValueOrDefault(s.Game.Id) != off;
            if (off == null) _vkd3dOff.Remove(s.Game.Id);
            else _vkd3dOff[s.Game.Id] = off;
        }
        if (tell) Log?.Report($"{s.Game.Name}: {off}");
        bool otherDriver, background;
        lock (_lock) (otherDriver, background) = (_driverStale.GetValueOrDefault(s.Game.Id), Background || _whenIdle.Contains(s.Game.Id));
        var sandbox = launcher ? null : SteamSource.SandboxHome(root);
        var where = sandbox != null ? null : WhereOf(s.Game.Id);   // what a sandbox's processes show isn't taken (LearnWhere)
        // a launcher's set the game was seen reading stays the target with no room left: the all-or-none placement then
        // takes an earlier one's entries out (NotGiven), as a removal does
        var outside = OutsideOf(where, anyRoom: launcher);
        return new SteamCachePlace(gameDir, appName, steamDir)
        {
            SandboxHome = sandbox,
            Targets = outside is var (_, _, t) ? [t] : launcher || !remove && Unplaceable(where) != null ? [] : PlaceTargets,
            NvidiaRoot = outside?.Root ?? Path.Combine(steamDir, "nvidiav1"),
            Outside = outside != null,
            ReadLeaf = outside?.Leaf,
            Coding = outside != null ? SteamEntryCoding.Stored : NvidiaLinux.SteamCoding,
            Leaf = leaf,
            Priority = background ? WarmPriority.Idle : Settings.Priority,
            WarmForOtherDriver = otherDriver,
            SteamConfig = outside != null || launcher ? null : Path.Combine(root, "config", "config.vdf"),
            // vkd3d-proton names its files in a folder of VKD3D_SHADER_CACHE_PATH after the exe, as Windows spells it
            Vkd3dSource = !vulkan && !launcher && Directory.Exists(vkd3d) ? new DirectoryInfo(vkd3d).EnumerateFiles("vkd3d-proton.*")
                .Select(f => f.Name.EndsWith(".write", StringComparison.Ordinal) ? f.FullName[..^".write".Length] : f.FullName)
                .FirstOrDefault(f => Path.GetFileName(f).Equals($"vkd3d-proton.{exe}.cache", StringComparison.OrdinalIgnoreCase)) : null,
            Vkd3dDir = work?.Dir,
            Vkd3dRoot = work?.Root ?? Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.Game.InstallDir)),
            Running = () => GameRunning(s.Game) || RunningGameExes().Contains(exe),
            GameSlot = files => SlotHeld(exe, files),
            Writing = writing,
            Log = m => Log?.Report($"{s.Game.Name}: {m}"),
        };
    }

    /// <summary>Where the game's driver was last seen reading (<see cref="LearnWhere"/>); null before it was.</summary>
    GameCacheWhere? WhereOf(string gameId)
    {
        lock (_placeLock)
        {
            if (!_where.TryGetValue(gameId, out var where)) _where[gameId] = where = GameCacheWhere.Load(Store.GameDir(gameId));
            return where;
        }
    }

    /// <summary>While the game plays: where its driver reads the cache, from its processes' environment and open files, kept
    /// in its data folder and logged when it changes. A process that doesn't tell yet leaves what was seen before.</summary>
    void LearnWhere(GameState s)
    {
        if (SteamPlaceScope(s) is not var (gameDir, _, steamDir, app)) return;
        var launcher = steamDir.Length == 0;
        // a Flatpak Steam's game says what its sandbox wants: its cache goes in Steam's folder alone
        if (!launcher && SteamSource.SandboxHome(PlaceSteamRoot) != null) return;
        var exe = Path.GetFileName(s.Game.ExePath);
        var mesa = MesaLinux.Is(Vendor.Caps);
        var id = app.ToString(CultureInfo.InvariantCulture);
        var shortcut = SteamShortcuts.Is(s.Game.Id);
        var gameId = shortcut ? SteamShortcuts.GameId(app).ToString(CultureInfo.InvariantCulture) : id;
        HashSet<string> prefixes = launcher && LauncherPrefix.Of(s.Game, out _) is { } lp ? PrefixPaths(lp)
            : shortcut && ProtonContext.ShortcutData(PlaceSteamRoot, app) is var data ? [.. new[] { data, Path.Combine(data, "pfx") }.Select(Libc.RealPath).OfType<string>()] : [];
        GameCacheWhere? seen = null;
        foreach (var p in AppCacheFiles.Processes().Where(p => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)))
            // Steam gives every launch its app id, a shortcut's its 64-bit game id or prefix, a launcher its prefix: another game's exe of the same name isn't this one
            if (ProcessTree.StartEnvironment(p.Pid) is { } env
                && (launcher ? InPrefix(env, prefixes)
                    : env.GetValueOrDefault("SteamGameId") == gameId || (shortcut ? InPrefix(env, prefixes) : env.GetValueOrDefault("SteamAppId") == id))
                && (mesa ? GameCacheWhere.Mesa(env, steamDir, Libc.RealPath)
                    : GameCacheWhere.Nvidia(env, ProcessTree.OpenPaths(p.Pid), launcher ? "" : Path.Combine(steamDir, "nvidiav1"), Libc.RealPath)) is { } where)
            {
                seen = launcher ? Shared(where, env) : where with { OwnProcess = shortcut };
                if (seen.Off == null) break;
            }
        // a foreign record stays until what was placed by it is out (ForgetForeignWhere)
        if (seen == null || seen == WhereOf(s.Game.Id) || ForeignWhere(s.Game.Id)) return;
        try { seen.Save(gameDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{s.Game.Name}: keeping where its cache is read: {e.Message}"); }
        lock (_placeLock) _where[s.Game.Id] = seen;
        Log?.Report($"{s.Game.Name}: " + (seen.Steam ? "the game reads Steam's shader cache folder"
            : seen.Set is { } set ? $"the game reads its shader cache from {set} (no Steam variables), up to {Format.Bytes(seen.Size)}" : $"the game's shader cache can't be reached: {seen.Off}"));
    }

    /// <summary>A launcher game's set as the placer may take it: only in the driver's default folder of the game's environment
    /// ($XDG_CACHE_HOME/nvidia, else ~/.cache/nvidia), never one its variables move elsewhere, and under the default cap with
    /// the folder's other files counted whatever the game sets: a program run without a size deletes the oldest sets of a
    /// folder past it.</summary>
    internal static GameCacheWhere Shared(GameCacheWhere where, IReadOnlyDictionary<string, string> env)
    {
        if (where.Set is not { } set) return where;
        var cache = env.GetValueOrDefault("XDG_CACHE_HOME") is { } x && Path.IsPathFullyQualified(x) ? x
            : env.GetValueOrDefault("HOME") is { } h && Path.IsPathFullyQualified(h) ? Path.Combine(h, ".cache") : null;
        return env.ContainsKey("__GL_SHADER_DISK_CACHE_PATH") || cache == null || Libc.RealPath(Path.Combine(cache, "nvidia", "GLCache")) is not { } root
            || !set.StartsWith(root + "/", StringComparison.Ordinal)
            ? new(Off: GameCacheWhere.NvidiaElsewhere)
            : where with { Size = Math.Min(where.Size, GameCacheWhere.NvidiaDefaultSize), SkipCleanup = false, Shared = true };
    }

    /// <summary>The launcher's prefix folder and its Wine prefix, links resolved: Proton's script and umu take the first as
    /// STEAM_COMPAT_DATA_PATH and give Wine the second (a link to the first under umu), a Wine build takes the first.</summary>
    internal static HashSet<string> PrefixPaths(LauncherPrefix p) => [.. new[] { p.Prefix, p.WineDir }.Select(Libc.RealPath).OfType<string>()];

    /// <summary>A process started in one of <paramref name="prefixes"/> (<see cref="PrefixPaths"/>): its WINEPREFIX or
    /// STEAM_COMPAT_DATA_PATH resolves to one.</summary>
    internal static bool InPrefix(IReadOnlyDictionary<string, string> env, IReadOnlySet<string> prefixes) =>
        new[] { "WINEPREFIX", "STEAM_COMPAT_DATA_PATH" }.Any(n => env.GetValueOrDefault(n) is { Length: > 0 } v && Libc.RealPath(v) is { } real && prefixes.Contains(real));

    /// <summary>A non-Steam game's record of where its cache is read that wasn't seen in its own process
    /// (<see cref="GameCacheWhere.OwnProcess"/>): it may be another game's of the same exe name.</summary>
    bool ForeignWhere(string gameId) => SteamShortcuts.Is(gameId) && WhereOf(gameId) is { Steam: false, OwnProcess: false };

    /// <summary>Forgets a <see cref="ForeignWhere"/> record, to be learned again at the game's next launch, once what was placed
    /// by it is taken out (the removal's all-or-none, SCSKiller's entries alone). False while that can't be done yet; null for
    /// no such record. Under the game's compile lock.</summary>
    bool? ForgetForeignWhere(GameState s, CancellationToken ct)
    {
        if (!ForeignWhere(s.Game.Id)) return null;
        var where = WhereOf(s.Game.Id)!;
        if (where.Set != null && (SteamPlacer(s, remove: true) is not { } placer || LowIo(() => placer.Remove(ct)).State is not (SteamPlaceState.Removed or SteamPlaceState.Nothing)))
            return false;
        try { File.Delete(GameCacheWhere.FileOf(Store.GameDir(s.Game.Id))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.Report($"{s.Game.Name}: keeping where its cache is read: {e.Message}");
            return false;
        }
        lock (_placeLock) _where[s.Game.Id] = null;
        Log?.Report($"{s.Game.Name}: forgot where its shader cache is read ({where.Set ?? where.Off}): it may have been another game's process of the same exe name. It's learned again at the game's next launch");
        return true;
    }

    /// <summary>Why the compile can't go where the game reads: <see cref="GameCacheWhere.Off"/>, or no room under its cache
    /// size; null when it can or nothing was seen.</summary>
    static string? Unplaceable(GameCacheWhere? where) => where switch
    {
        { Off: { } off } => off,
        { Set: { } set } when OutsideLimit(where) <= GlCacheFile.HeaderSize => $"its cache size of {Format.Bytes(where.Size)} leaves no room beside {Path.GetFileName(set)}'s own",
        _ => null,
    };

    /// <summary>The driver's folder outside Steam's whose GLCache the game reads, its "&lt;driver&gt;/&lt;gpu&gt;" folder and the
    /// game's own file there as a target: the game's entries kept first, the warm's after them; null for none or no room.</summary>
    static (string Root, string Leaf, SteamTarget Target)? OutsideOf(GameCacheWhere? where, bool anyRoom = false)
    {
        if (where is not { Set: { } set } || !anyRoom && Unplaceable(where) != null) return null;
        var gpu = Path.GetDirectoryName(set)!;
        var driver = Path.GetDirectoryName(gpu)!;
        return (Path.GetDirectoryName(Path.GetDirectoryName(driver))!, $"{Path.GetFileName(driver)}/{Path.GetFileName(gpu)}",
            new(Path.GetFileName(set), anyRoom ? Math.Max(OutsideLimit(where), GlCacheFile.HeaderSize + 1) : OutsideLimit(where), SteamFirst: true));
    }

    /// <summary>What the game's own file may hold: under what its .toc indexes and its cache size, the folder's other files
    /// counted unless the driver skips its cleanup, less what one session of the game adds: in a launcher game's shared default
    /// folder <see cref="GameCacheWhere.Headroom"/>, else <see cref="NvidiaLinux.SlotHeadroom"/>, which keeps a Steam game's
    /// file, often under a larger cap, within what its .toc indexes.</summary>
    static long OutsideLimit(GameCacheWhere where)
    {
        long others = 0, target = 0;
        if (!where.SkipCleanup && Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(where.Set))) is { } glCache)
            try
            {
                var all = new DirectoryInfo(glCache).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
                others = all.Where(f => f.FullName != where.Set + ".toc" && f.FullName != where.Set + ".bin").Sum(f => f.Length);
                target = all.Sum(f => f.Length) - others;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }   // the driver's cleanup may take what can't be counted
        // a shared folder holds the target's old files beside the new .bin and .toc until they take its name; the .toc's
        // 32 bytes an entry are at most a 32nd of the .bin with entries of 1 KiB or more (8.7 KB measured on average)
        return where.Shared ? Math.Min(NvidiaLinux.CacheFileIndexed, (where.Size - others - Math.Max(GameCacheWhere.Headroom, target)) / 33 * 32)
            : Math.Min(NvidiaLinux.CacheFileIndexed, where.Size - others) - NvidiaLinux.SlotHeadroom;
    }

    /// <summary>Of Steam's steamapp_shader_cache&lt;n&gt; files of a folder, the n of the one the game's own process has open, by
    /// its Windows exe (Proton's helpers hold others); null with why when it holds none or several or its open files can't be
    /// read.</summary>
    static (int? N, string? Why) SlotHeld(string exe, IReadOnlyList<(string Path, int N)> files)
    {
        if (files.Count == 0) return (null, "the folder holds no steamapp_shader_cache file");
        IReadOnlySet<string> held;
        try { held = AppCacheFiles.KeysOpenBy(exe, files.Select(f => (f.Path, f.N.ToString(CultureInfo.InvariantCulture)))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, $"{exe}'s open files can't be read: {e.Message}"); }
        return held.Count switch
        {
            1 => (int.Parse(held.First(), CultureInfo.InvariantCulture), null),
            0 => (null, $"{exe} has none of them open"),
            _ => (null, $"{exe} has several open ({string.Join(", ", held.Order(StringComparer.Ordinal))})"),
        };
    }

    /// <summary>While the game plays: notes which of Steam's cache files its own process writes, logged once a run. Returns
    /// what it found or why it found none; null when nothing is placed.</summary>
    string? LearnSlot(GameState s)
    {
        // a launcher's game has no slot of Steam's
        if (SteamPlaceScope(s) is not var (gameDir, appName, steamDir, _) || steamDir.Length == 0) return null;
        var exe = Path.GetFileName(s.Game.ExePath);
        var said = new SteamCachePlace(gameDir, appName, steamDir) { Targets = PlaceTargets, GameSlot = files => SlotHeld(exe, files) }.LearnSlot();
        lock (_placeLock)
        {
            if (said == null || _slotSaid.GetValueOrDefault(s.Game.Id) == said) return said;
            _slotSaid[s.Game.Id] = said;
        }
        Log?.Report($"{s.Game.Name}: {said}");
        return said;
    }

    /// <summary>The Windows working folder the game runs in, where vkd3d-proton keeps vkd3d-proton.cache without
    /// VKD3D_SHADER_CACHE_PATH, with the folder it is opened from: the folder of the newest one the game wrote itself in its
    /// install, as a launcher may start the game elsewhere (measured: a launcher entry with no working folder, the game's file
    /// in a folder above its exe's). Else a Steam game's working folder of Steam's launch entry that runs the exe, else the
    /// exe's folder, all opened from the install. A non-Steam game's start folder (<see cref="StartFolder"/>: Steam starts
    /// it there, measured) is its own root, and the exe's folder stands for one not set; null with why it isn't one.</summary>
    static (string Dir, string Root)? WorkingFolder(Game g, string root, uint app, string gameDir, out string? off)
    {
        off = null;
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir));
        var placed = SteamCachePlace.Vkd3dPlaced(gameDir);
        try
        {
            if (Directory.Exists(install) && new DirectoryInfo(install).EnumerateFiles("vkd3d-proton.cache*", Vkd3dSearch)
                    .Where(f => f.Name is "vkd3d-proton.cache" or "vkd3d-proton.cache.write" && !placed.Contains(f.FullName))
                    .MaxBy(f => f.LastWriteTimeUtc) is { } own)
                return (own.DirectoryName!, install);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(g.ExePath))!;
        if (!SteamShortcuts.Is(g.Id))
            return (LaunchFolder(SteamSource.Apps(SteamSource.AppInfoPath(root), new HashSet<uint> { app })?.GetValueOrDefault(app), g) ?? exeDir, install);
        if (SteamShortcuts.Find(root, app) is not { StartDir.Length: > 0 } shortcut) return (exeDir, install);
        if (StartFolder(root, shortcut.StartDir, out var why) is { } start) return (start, start);
        off = $"vkd3d-proton's cache isn't placed: {why}";
        return null;
    }

    /// <summary>A non-Steam game's start folder as the root its vkd3d-proton cache is placed under, never a folder above it:
    /// StartDir without the quotes Steam keeps it in (<see cref="SteamShortcuts"/>), a full path SCSKiller would list the
    /// shortcut with (<see cref="SteamShortcutSource.StartProblem"/>), its links resolved as Steam's change into it resolves
    /// them, then opened from / without following a link, a folder of this user's. Null with why for any other.</summary>
    static string? StartFolder(string root, string start, out string? why)
    {
        try
        {
            if ((why = SteamShortcutSource.StartProblem(root, start)) != null) return null;
            if (Libc.RealPath(start) is not { } real)
            {
                why = $"{start} doesn't exist";
                return null;
            }
            using (var h = Libc.OpenBelow("/", real.Split('/', StringSplitOptions.RemoveEmptyEntries), make: false))
                if (Libc.Owner(h)?.Uid != Libc.UserId)
                {
                    why = $"{real} isn't this user's";
                    return null;
                }
            return real;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { why = e.Message; }
        return null;
    }

    static readonly EnumerationOptions Vkd3dSearch = new() { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>The workingdir of the game's Windows launch entry in Steam's appinfo (config/launch) that runs its exe, the
    /// default one first, a beta branch's left out, under the install; null without one.</summary>
    internal static string? LaunchFolder(Dictionary<string, object>? app, Game g)
    {
        if (app?.GetValueOrDefault("config") is not Dictionary<string, object> config || config.GetValueOrDefault("launch") is not Dictionary<string, object> launch) return null;
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir));
        var exe = Path.GetFullPath(g.ExePath);
        var entry = launch.Values.OfType<Dictionary<string, object>>()
            .Where(e => e.GetValueOrDefault("executable") is string x && Path.GetFullPath(GameFiles.Find(install, x.Trim('\\', '/'))) == exe)
            .Where(e => e.GetValueOrDefault("config") is not Dictionary<string, object> c
                || !c.ContainsKey("BetaKey") && (c.GetValueOrDefault("oslist") is not string os || os.Contains("windows", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => "default".Equals(e.GetValueOrDefault("type") as string, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (entry?.GetValueOrDefault("workingdir") is not string { Length: > 0 } dir) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GameFiles.Find(install, dir.Trim('\\', '/'))));
        return full == install || full.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    // the folder MesaLinux.CacheEnvironment and Wine.CacheEnvironment name, none of it through a link; the placer's copy in
    // the game's working folder goes with the warm (SteamPlaceSoon)
    partial void Vkd3dFiles(string gameId, List<CachePart> parts)
    {
        var game = Store.GameDir(gameId);
        var dir = Path.Combine(game, "proton-cache", "vkd3d");
        if (new[] { game, Path.GetDirectoryName(dir)!, dir }.Any(d => new DirectoryInfo(d) is not { Exists: true } i || i.Attributes.HasFlag(FileAttributes.ReparsePoint))) return;
        // vkd3d-proton keeps its files flat in the folder: a delete opens it without a link (NvidiaLinuxAppCache.Vkd3dNoLinkFolders)
        try { parts.Add(Part(Vkd3dPart, new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // a game on SCSKiller's old launch line reads the compile's folder itself, with the line's size
    partial void CompileCacheCap(GameState s, ref long? cap)
    {
        if (Vendor.Caps.Profile == NvidiaLinux.Profile && !VulkanRoute(s.Game, s.Engine) && SteamPlaceScope(s) is var (gameDir, appName, steamDir, app)
            && (steamDir.Length == 0 || !OldLaunchLine(s.Game, app)))
            // outside Steam's folder the warm's stored entries go into the game's own file after its entries: the plan's budget is the room left
            cap = WhereOf(s.Game.Id) is { Set: { } set } where && OutsideOf(where) is var (_, _, t)
                ? t.Limit + (1L << 30) - Math.Max(0, (File.Exists(set + ".bin") ? new FileInfo(set + ".bin").Length : 0) - (new SteamCachePlace(gameDir, appName, steamDir).Last()?.Bytes ?? 0))
                : steamDir.Length == 0 ? cap : NvidiaLinux.PlacedCacheSize;
    }

    /// <summary>The game's Steam launch options (a non-Steam game's own) still point the driver or vkd3d-proton at SCSKiller's
    /// folder for the game.</summary>
    bool OldLaunchLine(Game g, uint app)
    {
        var gameDir = Path.GetFullPath(Store.GameDir(g.Id));
        var root = PlaceSteamRoot;
        var options = SteamShortcuts.Is(g.Id) ? SteamShortcuts.Find(root, app)?.LaunchOptions : SteamSource.LaunchOptions(root, app);
        return SteamLaunchOptions.Variables(options, out _).Any(v =>
            v.Name is "__GL_SHADER_DISK_CACHE_PATH" or "VKD3D_SHADER_CACHE_PATH" && v.Value.StartsWith(gameDir, StringComparison.Ordinal)
            || v.Name == "__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME" && v.Value.Contains("scskiller_", StringComparison.Ordinal));
    }

    partial void SteamCacheNoteOf(string gameId, ref string? note)
    {
        if (Games.FirstOrDefault(x => x.Game.Id == gameId) is not { WarmedAt: not null } s) return;
        if (SteamPlaceScope(s) is not var (gameDir, appName, steamDir, app))
        {
            if (LauncherFlatpak(s)) note = LauncherFlatpakNote;
            return;
        }
        if (steamDir.Length == 0)
        {
            note = LauncherNote(gameId, gameDir, appName);
            return;
        }
        if (OldLaunchLine(s.Game, app))
        {
            note = "Remove SCSKiller's line from the game's Steam launch options";
            return;
        }
        SteamPlaceResult? last;
        bool placing, due;
        lock (_placeLock) (last, placing, due) = (_placeLast.GetValueOrDefault(gameId), _placing == gameId, _placeDue.ContainsKey(gameId));
        last ??= new SteamCachePlace(gameDir, appName, steamDir).Last();
        note = placing || last == null && due ? PlacingNote : last is { } r ? NoteOf(r) : null;
        if (note == SteamReadsNote && WhereOf(gameId) is { Set: not null }) note = OwnFileNote;
    }

    const string PlacingNote = "Putting this cache in Steam's folder", SteamReadsNote = "Steam reads this cache with no launch options";

    internal const string LauncherFlatpakNote = "The compile is kept, but SCSKiller can't reach the cache of a Flatpak launcher's game yet",
        LauncherUnseenNote = "The compile is kept. Launch the game once so SCSKiller can find its cache file",
        LauncherNoRoomNote = "The compile is kept, but the game's driver cache has no room for it",
        LauncherCacheOffNote = "The compile is kept, but the game's driver cache is turned off",
        LauncherSeveralNote = "The compile is kept, but the game uses several driver cache files",
        LauncherElsewhereNote = "The compile is kept, but the game's driver cache is in a folder of its own",
        OwnFileNote = "The game reads this cache from its own cache file",
        SharedFileNote = "The game reads this cache from the driver's cache file, which other games share";

    /// <summary>The game page's line for a launcher's game (<see cref="LauncherPlaced"/>): where its compile is, and when the
    /// game can't use it, why.</summary>
    string? LauncherNote(string gameId, string gameDir, string appName)
    {
        SteamPlaceResult? last;
        bool placing;
        lock (_placeLock) (last, placing) = (_placeLast.GetValueOrDefault(gameId), _placing == gameId);
        if (placing) return "Putting this cache in the game's cache file";
        var where = WhereOf(gameId);
        if (where is { Off: { } off })
            return off == GameCacheWhere.NvidiaOff ? LauncherCacheOffNote : off == GameCacheWhere.NvidiaElsewhere ? LauncherElsewhereNote : LauncherSeveralNote;
        if (where is not { Set: not null }) return LauncherUnseenNote;
        if (Unplaceable(where) != null) return LauncherNoRoomNote;
        return (last ?? new SteamCachePlace(gameDir, appName, "").Last())?.State switch
        {
            SteamPlaceState.Placed or SteamPlaceState.UpToDate => SharedFileNote,
            SteamPlaceState.NotGiven => LauncherNoRoomNote,
            SteamPlaceState.GameRunning => "Waiting for the game to close to update its cache file",
            SteamPlaceState.NoSpace => "Not enough free disk space to update the game's cache file",
            SteamPlaceState.NextCompile => "The game's cache file is updated after the next compile",
            SteamPlaceState.Failed or SteamPlaceState.SteamBusy => "Couldn't update the game's cache file yet",
            _ => null,
        };
    }

    internal const string NotGivenNote = "Steam isn't giving this game its shader cache. Turn on Shader Pre-caching in Steam's Settings > Downloads, then restart Steam.";

    /// <summary>The game page's line for what the placer did last.</summary>
    internal static string? NoteOf(SteamPlaceResult r) => r.State switch
    {
        SteamPlaceState.Placed or SteamPlaceState.UpToDate => (r.SteamOwnsOne, r.LeftBytes > 0) switch
        {
            (true, true) => $"Steam keeps its own cache file for this game, {Format.Bytes(r.LeftBytes)} of the cache doesn't fit the merged one",
            (true, false) => "Steam keeps its own cache file for this game. SCSKiller adds to the merged one",
            (false, true) => $"{Format.Bytes(r.LeftBytes)} of the cache doesn't fit Steam's cache files",
            _ => SteamReadsNote,
        },
        SteamPlaceState.GameRunning => "Waiting for the game to close to update Steam's cache",
        SteamPlaceState.SteamBusy or SteamPlaceState.Unmerged => "Waiting for Steam to finish with its cache files",
        SteamPlaceState.OtherBucket => "Steam hasn't used this driver yet. Launch the game once from Steam",
        SteamPlaceState.NextCompile => "Steam's cache is updated after the next compile",
        SteamPlaceState.NoSpace => "Not enough free disk space to put this cache in Steam's folder",
        SteamPlaceState.NotGiven => NotGivenNote,
        SteamPlaceState.Failed => "Couldn't update Steam's cache",
        _ => null,
    };

    partial void SteamPlaceSoon(string gameId, bool exited) => PlaceDue(gameId, exited ? PlaceSettle : TimeSpan.Zero);

    /// <summary>The entries a set's .toc lists and its .bin's length; null when they can't be read.</summary>
    static (long Entries, long Bytes)? InFile(string set)
    {
        try { return GlCacheFile.ReadToc(File.ReadAllBytes(set + ".toc"), out _, v4: true) is var (_, records) ? (records.Count, new FileInfo(set + ".bin").Length) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // the driver opens the game's own file once the game starts rendering
    partial void SteamPlaceStarted(string gameId) => PlaceDue(gameId, PlaceSettle);

    void PlaceDue(string gameId, TimeSpan after, bool sooner = false)
    {
        var at = DateTimeOffset.Now + after;
        lock (_placeLock)
            if (!sooner || !_placeDue.TryGetValue(gameId, out var was) || at < was) _placeDue[gameId] = at;
    }

    partial void SteamPlaceStart(CancellationToken ct, ref Task? task) => task = Task.Run(() => PlaceLoop(ct), CancellationToken.None);

    /// <summary>Keeps each game's cache in Steam's folder as its compile left it, one game at a time: every game with a
    /// compile or a placement once the games are listed, then each that a compile, a delete, its exit or a change in a
    /// watched folder made due. A look at a placement that is up to date reads only .tocs.</summary>
    async Task PlaceLoop(CancellationToken ct)
    {
        try
        {
            while (Volatile.Read(ref _listed) == 0) await Task.Delay(WatchInterval, ct);
            foreach (var s in Games)
                if (SteamPlaceScope(s) is var (gameDir, _, _, _) && (Store.LoadGame(s.Game.Id).WarmedAt != null || File.Exists(SteamCachePlace.ManifestOf(gameDir)) || ForeignWhere(s.Game.Id)))
                    PlaceDue(s.Game.Id, TimeSpan.Zero, sooner: true);
            while (!ct.IsCancellationRequested)
            {
                List<string> due;
                lock (_placeLock) due = [.. _placeDue.Where(d => d.Value <= DateTimeOffset.Now).OrderBy(d => d.Value).Select(d => d.Key)];
                foreach (var id in due)
                {
                    ct.ThrowIfCancellationRequested();
                    try { PlaceOne(id, ct); }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        Log?.Report($"putting {id}'s cache in Steam's folder: {e.Message}");
                        PlaceDue(id, TimeSpan.FromMinutes(30));
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var w in _placeWatch.Values.SelectMany(w => w)) w.Dispose();
            _placeWatch.Clear();
        }
    }

    /// <summary>Places or takes out (the compile's cache deleted) one game's cache in Steam's folder, unless the game plays,
    /// is compiled or queued, another game plays (with <see cref="Settings.PauseWhileGaming"/>) or another process holds its
    /// compile; then it is due again later.</summary>
    void PlaceOne(string id, CancellationToken ct)
    {
        lock (_placeLock) _placeDue.Remove(id);
        if (Games.FirstOrDefault(x => x.Game.Id == id) is not { } s || SteamPlaceScope(s) == null)
        {
            Unwatch(id);
            lock (_placeLock) _placeLast.Remove(id);
            return;
        }
        if (_playing.Contains(id))
        {
            if (!PlaceAside(id))   // the before run's folder isn't the one placed
            {
                LearnSlot(s);
                LearnWhere(s);
            }
            bool shown;
            lock (_placeLock) shown = _placeLast.GetValueOrDefault(id)?.State is SteamPlaceState.Placed or SteamPlaceState.UpToDate;
            if (!shown) Remember(id, new(SteamPlaceState.GameRunning, Why: "the game is running"));
            PlaceDue(id, TimeSpan.FromMinutes(2));
            return;
        }
        lock (_placeLock) _slotSaid.Remove(id);
        bool busy;
        lock (_lock) busy = _current == id || _queue.Any(q => q.GameId == id && !q.PlanCheck && !Finished(q.Stage));
        if (busy || Settings.PauseWhileGaming && _playing.Any(p => p != id))
        {
            PlaceDue(id, PlaceSettle);
            return;
        }
        using (var own = CompileLock(id))
        {
            var remove = Store.LoadGame(id).WarmedAt == null;
            if (own != null && !PlaceAside(id) && ForgetForeignWhere(s, ct) == false)
            {
                PlaceDue(id, TimeSpan.FromMinutes(2));
                return;
            }
            if (own == null || PlaceAside(id) || SteamPlacer(s, () => Remember(id, null, placing: true), remove) is not { } placer)
            {
                PlaceDue(id, PlaceSettle);
                return;
            }
            SteamPlaceResult? placed = null;
            try { placed = NotGiven(LowIo(() => remove ? placer.Remove(ct) : placer.Place(ct)), remove ? null : WhereOf(id)); }
            finally { Remember(id, placed); }
            var r = placed;
            var retry = r.State switch
            {
                SteamPlaceState.GameRunning => TimeSpan.FromMinutes(2),   // also a launch the watcher doesn't see as playing yet
                SteamPlaceState.SteamBusy => TimeSpan.FromMinutes(1),
                SteamPlaceState.OtherBucket or SteamPlaceState.Unmerged => TimeSpan.FromMinutes(10),
                SteamPlaceState.NoSpace or SteamPlaceState.Failed => TimeSpan.FromMinutes(30),
                _ => r.Vkd3dWaits ? TimeSpan.FromMinutes(10) : (TimeSpan?)null,
            };
            if (retry is { } after) PlaceDue(id, after, sooner: true);
            if (remove || r.State is SteamPlaceState.Nothing) Unwatch(id);
            else Watch(id, r.Leaf is { } leaf && !placer.Outside && placer.SteamDir.Length > 0 ? Path.Combine(placer.NvidiaRoot, "GLCache", leaf) : null, placer.Vkd3dDir);
        }
    }

    /// <summary>A placement where the compile can't go where the game reads: vkd3d-proton's alone went in.</summary>
    static SteamPlaceResult NotGiven(SteamPlaceResult r, GameCacheWhere? where) =>
        Unplaceable(where) is { } why && r.State is SteamPlaceState.Placed or SteamPlaceState.UpToDate or SteamPlaceState.Nothing ? r with { State = SteamPlaceState.NotGiven, Why = why } : r;

    /// <summary>The game's Steam cache and vkd3d-proton's are set aside for its before run (<see cref="SetAsideSteam"/>, under
    /// the game's compile lock as a placement): the placer leaves them and what the run makes alone until they're back.</summary>
    bool PlaceAside(string id) => SetAside() is SteamSetAside q && q.PendingGame == id;

    /// <summary>Keeps the placer's last result for the game's note (<paramref name="placing"/>: it writes Steam's files
    /// now), and tells the page when the note changes.</summary>
    void Remember(string id, SteamPlaceResult? r, bool placing = false)
    {
        string? was, now;
        lock (_placeLock)
        {
            string? Note() => _placing == id ? PlacingNote : _placeLast.GetValueOrDefault(id) is { } last ? NoteOf(last) : null;
            was = Note();
            _placing = placing ? id : _placing == id ? null : _placing;
            if (r != null) _placeLast[id] = r;
            now = Note();
        }
        if (was != now && Games.FirstOrDefault(x => x.Game.Id == id) is { } s) GameChanged?.Invoke(s);
    }

    /// <summary>Watches Steam's folder of the game's cache and its working folder: a change there (Steam's merge, its
    /// download, vkd3d-proton merging its .write) makes the game due once the changes settle.</summary>
    void Watch(string id, string? steamLeaf, string? vkd3dDir)
    {
        string[] dirs = [.. new[] { steamLeaf, vkd3dDir }.OfType<string>().Where(Directory.Exists)];
        if (_placeWatch.TryGetValue(id, out var have) && have.Select(w => w.Path).SequenceEqual(dirs)) return;
        Unwatch(id);
        var watchers = new List<FileSystemWatcher>();
        foreach (var dir in dirs)
            try
            {
                var w = new FileSystemWatcher(dir, dir == vkd3dDir ? "vkd3d-proton.cache*" : "*")
                    { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
                void Changed(object? _, FileSystemEventArgs e)
                {
                    if (!e.Name?.StartsWith(SteamCachePlace.OwnPrefix, StringComparison.Ordinal) ?? true) PlaceDue(id, PlaceSettle);
                }
                w.Created += Changed;
                w.Changed += Changed;
                w.Deleted += Changed;
                w.Renamed += Changed;
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Log?.Report($"watching {dir}: {e.Message}");
            }
        _placeWatch[id] = watchers;
    }

    void Unwatch(string id)
    {
        if (!_placeWatch.Remove(id, out var watchers)) return;
        foreach (var w in watchers) w.Dispose();
    }

    partial void SteamPlaceOnce(GameState s, bool remove, CancellationToken ct, ref (bool, string, IReadOnlyList<string>)? report)
    {
        if (SteamPlacer(s, remove: remove) is not { } placer) return;
        using var own = CompileLock(s.Game.Id);
        if (own == null)
        {
            report = (false, $"A compile of {s.Game.Name} is in progress", []);
            return;
        }
        if (PlaceAside(s.Game.Id))
        {
            report = (false, $"{s.Game.Name}'s cache is set aside for its before and after recording", []);
            return;
        }
        switch (ForgetForeignWhere(s, ct))
        {
            case false:
                report = (false, $"{s.Game.Name}: where its cache is read may be another game's, and what was placed there can't be taken out yet", []);
                return;
            case true:
                placer = SteamPlacer(s, remove: remove)!;
                break;
        }
        var r = NotGiven(remove ? placer.Remove(ct) : placer.Place(ct), remove ? null : WhereOf(s.Game.Id));
        Remember(s.Game.Id, r);
        List<string> details = [$"state: {r.State}"];
        if (!remove && r.State == SteamPlaceState.GameRunning && LearnSlot(s) is { } slot) details.Add($"own slot: {slot}");
        if (r.Leaf != null) details.Add($"{(placer.Outside ? "the game's folder" : "Steam's folder")}: {Path.Combine(placer.NvidiaRoot, "GLCache", r.Leaf)}");
        var shared = placer.SteamDir.Length == 0;
        foreach (var f in r.Files ?? [])
        {
            // another program's take-out changes what's before SCSKiller's after the record was saved: counted from the file now
            var (before, beforeBytes) = shared && !remove && r.Leaf != null && InFile(Path.Combine(placer.NvidiaRoot, "GLCache", r.Leaf, f.Name)) is var (n, bytes)
                ? (n - f.Pipelines, bytes - GlCacheFile.HeaderSize - f.Bytes) : (f.SteamPipelines, f.SteamBytes);
            details.Add(f.SteamOwns ? $"{f.Name}: Steam's own, left as it is"
                : $"{f.Name}: {f.Pipelines:N0} pipelines, {f.Bytes:N0} bytes" + (before <= 0 ? ""
                    : shared ? $", with {before:N0} other pipelines in the file, {beforeBytes:N0} bytes" : $", after Steam's {before:N0} pipelines, {beforeBytes:N0} bytes"));
        }
        if (r.Files != null && !remove) details.Add($"didn't fit: {r.Left:N0} pipelines, {r.LeftBytes:N0} bytes");
        string? off;
        lock (_placeLock) off = placer.Vkd3dDir == null && !remove ? _vkd3dOff.GetValueOrDefault(s.Game.Id) : null;
        details.Add(off ?? $"vkd3d-proton: {(placer.Vkd3dDir is { } dir ? $"{dir}: " : "")}{r.Vkd3d ?? "nothing to place"}");
        if (r.Why != null) details.Add($"why: {r.Why}");
        var done = r.State is SteamPlaceState.Placed or SteamPlaceState.UpToDate or SteamPlaceState.Removed || remove && r.State == SteamPlaceState.Nothing;
        var status = placer.SteamDir.Length > 0 ? NoteOf(r) ?? (r.State == SteamPlaceState.Removed ? "Taken out of Steam's folder" : remove ? "Nothing of SCSKiller's in Steam's folder" : "Nothing to put in Steam's folder")
            : (remove ? null : LauncherNote(s.Game.Id, Store.GameDir(s.Game.Id), Wine.WarmAppName(Path.GetFileName(s.Game.ExePath))))
                ?? (r.State == SteamPlaceState.Removed ? "Taken out of the game's cache file" : remove ? "Nothing of SCSKiller's in the game's cache file" : "Nothing to put in the game's cache file");
        report = (done, status, details);
    }
}
