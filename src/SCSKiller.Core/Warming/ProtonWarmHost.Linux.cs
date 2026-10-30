using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.Warming;

/// <summary>scskiller_warm in a Steam game's own Proton prefix, on the game's Proton, through Steam's runtime
/// (<see cref="ProtonLaunch.Command"/>); or, without a Proton context, the native scskiller_warm of a Vulkan game's warm,
/// native or under Proton alike, with the same launcher, priority and protocol (<see cref="ProtonLaunch.NativeCommand"/>).
/// Proton's launcher drops the warm's stdout and has no named event Core can reach, so the protocol goes through files in
/// the work folder: --out and --err (new names per process), the stop file, and the alive file Core touches while the host
/// lives, so the warm stops on its own once Core is gone. Suspend and Kill reach the run's <see cref="WarmJob"/> alone,
/// never the launcher, wineserver or Wine's own processes. The warm's files run from a copy in the data folder
/// (<see cref="NativeCopy"/>). On NVIDIA a process is stopped once its cache file reaches the limit, or once the cache folder
/// has no more room for its files' rewrite (<see cref="Room"/>): a D3D12 warm goes on in a process that writes a fresh file
/// once the pass has rewritten that one, a Vulkan warm (it can't start past its first pipeline) ends there. A game of the
/// Flatpak Steam's runs its warm in that Steam's sandbox (<see cref="ProtonLaunch.FlatpakCommand"/>), each process in a
/// folder of its own (<see cref="FlatpakExchange"/>) that holds the protocol's files, copies of its inputs and its cache: what
/// it leaves comes out into the work and cache folders before anything reads it, its scopes' priority is lowered
/// (<see cref="FlatpakScopes"/>), and its sandbox is ended once it ignores a stop (<see cref="FlatpakSandbox"/>).</summary>
sealed class ProtonWarmHost : IWarmHost
{
    readonly ProtonContext? _ctx;
    readonly LauncherPrefix? _wine;
    readonly ProtonPaths? _paths;
    readonly ProtonTools _tools;
    readonly string _work, _dataDir, _gameDir, _stop, _alive, _launcherLog, _exeName, _appName;
    readonly List<(string Name, string Value)> _cache;
    readonly string? _gpuName;
    readonly bool _mesa;
    readonly string? _mesaKey;
    readonly Timer _touch;
    readonly Dictionary<string, string> _copies = [];
    readonly GlCacheNoDict? _noDict;
    readonly long _limit;
    readonly Lock _stopGate = new();
    bool _runStopped;
    string? _halt;   // why no process may start
    bool _full;      // _halt is the folder holding no more
    volatile string? _unstopped;   // why no process may start: the watcher couldn't stop the last one
    (string? Bin, bool Room, string? Unmeasured)? _reached;   // why the watcher stopped the last process: a .bin at the limit, no room, or the folder unmeasured
    Dictionary<string, (long Length, ulong Inode)> _before = [];   // the warm's .bin files as the last process started
    int _started;
    volatile bool _disposed;
    readonly string? _flatpak;          // the flatpak command a Flatpak Steam game's warm runs through
    readonly long _cacheCap;
    string _next = FlatpakExchange.NewName();   // the exchange the next process gets, which WarmPath names
    FlatpakExchange? _exchange;         // the running or last process's; under _stopGate
    readonly List<string> _earlier = [];   // the names of the exchanges processes ran in
    readonly HashSet<string> _inputs = new(StringComparer.Ordinal) { "scskiller_pass.bin", "scskiller.db", "scskiller_gen.db" };   // of the work folder, which an exchange copies in
    long _exchangeIn;                   // what the running process's exchange holds of the NVIDIA folder's copied in
    FlatpakScopes? _scopes;             // the running process's
    FlatpakSandbox? _sandbox;           // the running or last process's
    ProtonProcess? _running;            // a Flatpak Steam game's running or last process; under _stopGate

    internal WarmJob Job { get; }

    /// <summary>The warm's turns (100 ms each, counted only while it runs) without a new write time on the alive file before
    /// it stops: 30 s, at a touch a second. Its child is ended only if no last line came SCSKILLER_WARM_EXIT_S after
    /// that.</summary>
    internal int AliveTurns { get; init; } = 300;

    /// <summary>How long a launcher may outlive its warm's exit line (Wine's processes winding down), or its warm gone without
    /// one, before the process counts as exited; it is waited for in the background, never signalled.</summary>
    internal TimeSpan Linger { get; init; } = TimeSpan.FromSeconds(10);

    internal TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a Flatpak Steam game's warm may run, once told to stop, before it says it stops, and how long it may
    /// take to exit once it said it stops or is done (past the warm's own exit limit): its sandbox is ended after either, as
    /// the sandbox may keep the warm from reading its stop and alive files.</summary>
    internal TimeSpan StopBound { get; init; } = TimeSpan.FromMinutes(2);

    internal TimeSpan ExitBound { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How often the warm's cache files are measured while a process runs.</summary>
    internal TimeSpan WatchEvery { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The environment the launcher's is made from (default: Core's). Replaceable for tests.</summary>
    internal IReadOnlyDictionary<string, string>? Inherited { get; init; }

    /// <summary>Names the native warm keeps of <see cref="Inherited"/> besides <see cref="ProtonLaunch.NativeKept"/>'s. Replaceable for tests.</summary>
    internal IReadOnlyCollection<string>? AlsoInherited { get; init; }

    /// <param name="ctx">the game's Proton; null: the native Vulkan warm</param>
    /// <param name="gameDir">the game's data folder, which holds the work folder and its proton-cache folder</param>
    /// <param name="dataDir">the data folder, where the warm files' copies go</param>
    /// <param name="gpu">the GPU the warm runs on</param>
    /// <param name="limit">the .bin length a process is stopped at (default: <see cref="NvidiaLinux.SegmentLimit"/>, a Vulkan
    /// warm's <see cref="NvidiaLinux.VulkanLimit"/>)</param>
    /// <param name="wine">a launcher's prefix the warm runs in on its Wine build (<see cref="ProtonLaunch.WineEnvironment"/>), in place of a Proton context</param>
    /// <param name="cache">the cache variables the warm runs with in place of SCSKiller's own folder's: a game that reads its
    /// driver's default cache gets none (or its Flatpak's XDG_CACHE_HOME), and no file of it is rewritten, watched or keyed</param>
    /// <param name="cacheCap">what NVIDIA's cache folder may hold (default: <see cref="NvidiaLinux.CacheSize"/>), the warm's
    /// files <see cref="NvidiaLinux.BudgetOf"/> it. Throws <see cref="CacheFullException"/> when it has no room for a process.</param>
    /// <param name="mesa">the backend is Mesa's: Mesa's cache variables (<see cref="MesaLinux.CacheEnvironment"/>) and the native
    /// warm Mesa's settings; else NVIDIA's (<see cref="Wine.CacheEnvironment"/>). Null: by the GPU's vendor, AMD and Intel.</param>
    /// <param name="flatpak">the flatpak command: the game is the Flatpak Steam's, on NVIDIA under Proton, and its warm runs in
    /// that Steam's sandbox, each process in an exchange of its own</param>
    public ProtonWarmHost(ProtonContext? ctx, Game game, string workDir, string gameDir, string dataDir, GpuInfo gpu, ProtonTools tools, TimeSpan? touchEvery = null,
        IProgress<string>? log = null, long? limit = null, long? cacheCap = null, LauncherPrefix? wine = null, IReadOnlyList<(string Name, string Value)>? cache = null, bool? mesa = null,
        string? flatpak = null)
    {
        (_ctx, _wine) = (ctx, wine);
        if (flatpak != null && (ctx == null || wine != null || cache != null || mesa == true)) throw new ArgumentException("only a Proton warm on NVIDIA runs in the Flatpak Steam's sandbox", nameof(flatpak));
        (_flatpak, _gameDir, _exeName, _cacheCap) = (flatpak, Path.GetFullPath(gameDir), Path.GetFileName(game.ExePath), cacheCap ?? NvidiaLinux.CacheSize);
        _appName = Wine.WarmAppName(_exeName);
        _paths = ctx != null ? new ProtonPaths(ctx.Prefix) : wine != null ? new ProtonPaths(wine.WineDir) : null;
        _tools = tools;
        _work = Path.GetFullPath(workDir);
        _dataDir = Path.GetFullPath(dataDir);
        Job = new WarmJob(_work) { Log = log == null ? null : m => log.Report($"{game.Name}: {m}") };
        _gpuName = gpu.Name;
        _mesa = mesa ?? gpu.Vendor is GpuVendor.Amd or GpuVendor.Intel;
        _cache = cache?.ToList() ?? (_mesa
            ? MesaLinux.CacheEnvironment(gameDir, game, steamRoot: ctx?.SteamRoot)
            : Wine.CacheEnvironment(Wine.CacheRoot(gameDir), Path.GetFileName(game.ExePath), cacheCap ?? NvidiaLinux.CacheSize).Warm);
        _mesaKey = _mesa && cache == null ? (SteamSource.AppId(game.Id) ?? SteamShortcuts.AppId(game.Id))?.ToString(CultureInfo.InvariantCulture) : null;
        _limit = limit ?? (ctx == null ? NvidiaLinux.VulkanLimit : NvidiaLinux.SegmentLimit);
        if (!_mesa && cache == null)
        {
            _noDict = cacheCap is { } cap ? new GlCacheNoDict(gameDir, Wine.WarmAppName(Path.GetFileName(game.ExePath))) { Log = Job.Log, Cap = cap, Budget = NvidiaLinux.BudgetOf(cap) }
                : new GlCacheNoDict(gameDir, Wine.WarmAppName(Path.GetFileName(game.ExePath))) { Log = Job.Log };
            // a set at the limit with a dictionary is rewritten first: the first process could add to it
            if ((NoDict(() => { if (Full(null) != null) _noDict.Run(); else _noDict.Recover(); }) ?? Full(null)) is { } why)
                throw new InvalidOperationException(why);
            if (NoRoom() is { Why: { } none } room) throw room.Full ? new CacheFullException(none, _noDict.Keys()) : new InvalidOperationException(none);
        }
        (_stop, _alive, _launcherLog) = (Path.Combine(_work, "stop"), Path.Combine(_work, "alive"), Path.Combine(_work, "launcher.log"));
        if (_flatpak == null)
        {
            File.Delete(_stop);   // an earlier run's: it would stop this one's first process
            File.WriteAllBytes(_alive, []);
        }
        var every = touchEvery ?? TimeSpan.FromSeconds(1);
        _touch = new Timer(_ => Touch(), null, every, every);
    }

    void Touch()
    {
        if (_unstopped != null) return;   // the stop file couldn't be written: a stale alive file stops the warm, resumably
        if (_flatpak != null)
        {
            lock (_stopGate) _exchange?.Touch();
            return;
        }
        try { File.SetLastWriteTimeUtc(_alive, DateTime.UtcNow); }
        catch (FileNotFoundException) { try { File.WriteAllBytes(_alive, []); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public string WarmPath(string host) => _paths?.WarmPath(Exchanged(host)) ?? host;

    /// <summary>A Flatpak Steam game's warm: a path in the work folder as the next process's exchange holds it, copied in when
    /// it starts; any other path as it is.</summary>
    string Exchanged(string host)
    {
        if (_flatpak == null) return host;
        var full = Path.GetFullPath(host);
        if (full != _work && !full.StartsWith(_work + "/", StringComparison.Ordinal)) return host;
        var rel = Path.GetRelativePath(_work, full);
        if (rel != ".") lock (_inputs) _inputs.Add(rel);
        return Path.Combine(FlatpakExchange.PathOf(_dataDir, _next), FlatpakExchange.Work, rel == "." ? "" : rel).TrimEnd('/');
    }

    /// <summary>A Flatpak Steam game's warm names its stage folder in its exchange: Core opens the copy of it in the work
    /// folder (<see cref="FlatpakExchange.StageCopy"/>), never the exchange.</summary>
    public string HostPath(string warm)
    {
        var host = _paths?.HostPath(warm) ?? warm;
        if (_flatpak == null) return host;
        FlatpakExchange? x;
        lock (_stopGate) x = _exchange;
        return x?.StageCopy(_work, host) ?? Path.Combine(_work, "stage");
    }

    public IReadOnlyList<string> StopArgs => ["--stop-file", WarmPath(_flatpak == null ? _stop : Path.Combine(FlatpakExchange.PathOf(_dataDir, _next), FlatpakExchange.StopName))];

    // under Proton the GPU goes by name in the environment, the native warm takes its PCI address
    public IReadOnlyList<string> AdapterArgs(GpuInfo gpu) => _ctx == null && _wine == null ? ["--adapter-luid", gpu.AdapterLuid.ToString("X16")] : [];

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? unstopped = null;
        lock (_stopGate)
        {
            _runStopped = true;
            if (_flatpak == null) File.WriteAllBytes(_stop, []);
            else if (_exchange?.Stop() is { } why) _unstopped = unstopped = string.Format(CultureInfo.InvariantCulture, Unstopped, why);
            _running?.StopAsked();
        }
        if (unstopped != null) Job.Log?.Invoke(unstopped);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _touch.Dispose();
        _sandbox?.End();   // a teardown while the sandbox may still run: kill it before its handles go
        lock (_stopGate)
        {
            _exchange?.Dispose();
            _exchange = null;
        }
        _sandbox?.Dispose();
    }

    /// <summary>NVIDIA: rewrites the warm's cache files that hold a zstd dictionary (<see cref="GlCacheNoDict"/>). A rewrite it
    /// can't settle, a file at the limit it leaves (<see cref="Full"/>) or a folder without room for another process
    /// (<see cref="NoRoom"/>) keeps every later process of the run from starting. A D3D12 process the watcher stopped goes on
    /// in the next one, from where it stopped. Mesa: the key of the game's folder that <see cref="MesaLinux.CacheEnvironment(string, Game, bool)"/>
    /// names (<see cref="MesaLinuxAppCache"/>), which the native replayer, never named like the game, isn't seen holding.</summary>
    public async Task<ProcessEnd> AfterProcess(Action<string> note)
    {
        if (_flatpak != null)
        {
            ProtonProcess? running;
            lock (_stopGate) running = _running;
            if (running != null) await running.Settled;   // the sandbox is gone before its exchange is read and removed
            var unended = running is { Unended: true };
            if (unended) _halt ??= string.Format(CultureInfo.InvariantCulture, NotEnded, Format.Duration(StopBound));
            await Task.Run(() => CopyOut(copy: !unended));   // first: the pass and the watch's measures read Core's own folder alone
        }
        if (_noDict is not { } pass) return _mesaKey is { } key ? new([key]) : ProcessEnd.None;
        (string? Bin, bool Room, string? Unmeasured)? reached;
        lock (_stopGate)
        {
            (reached, _reached) = (_reached, null);
            if (reached != null && _ctx != null && !_runStopped && _flatpak == null) File.Delete(_stop);   // the next process goes on, unless the run was stopped
        }
        var grown = Grown(_before, pass.Bins());   // before the pass: its own files are never among them
        _halt ??= await Task.Run(() => NoDict(() => pass.Run(note)));
        _halt ??= reached?.Unmeasured;
        if (_halt == null)
            (_halt, _full) = reached is { } r && _ctx == null
                ? r.Room ? NoRoom(always: true) : (string.Format(CultureInfo.InvariantCulture, VulkanFull, Path.GetFileNameWithoutExtension(r.Bin), Format.Bytes(_limit)), true)
                : await Task.Run(() => Full(grown) is { } full ? (full, false) : NoRoom());
        return new([.. pass.Added], reached != null && _ctx != null, _halt, _full);
    }

    /// <summary>A Flatpak Steam game's process has exited: what it left in its exchange comes out (<see cref="FlatpakExchange"/>),
    /// its outputs into the work folder and its sets, within what the cache folder may still take, and vkd3d-proton's files
    /// into the cache folder, then the exchange goes. A copy that fails keeps every later process from starting. Nothing is
    /// copied without <paramref name="copy"/>.</summary>
    void CopyOut(bool copy)
    {
        FlatpakExchange? x;
        lock (_stopGate) x = _exchange;
        if (x == null) return;
        void Left(string why) => Job.Log?.Invoke(why);
        try
        {
            if (!copy) return;
            x.CopyOutputs(_work, Left);
            var pass = _noDict!;
            long used;
            try { used = pass.NvidiaBytes(); }
            catch (DirectoryNotFoundException) when (!Directory.Exists(pass.NvidiaDir)) { used = 0; }
            x.CopySetsOut(NvidiaLinuxAppCache.CacheDir(_gameDir), _appName, pass.Files, NvidiaLinux.CacheFileIndexed, pass.Cap - used, Left);
            x.CopyVkd3dOut(Path.Combine(Wine.CacheRoot(_gameDir), "vkd3d"), _exeName, NvidiaLinux.CacheFileIndexed, Left);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _halt ??= string.Format(CultureInfo.InvariantCulture, NotCopied, e.Message);
        }
        finally
        {
            lock (_stopGate)
                if (_exchange == x) _exchange = null;
            x.Dispose();
        }
    }

    public const string Unsettled = "NVIDIA's cache files are partly rewritten and can't be settled yet",
        NotCopied = "copying the compile's files out of its folder failed ({0}), so no compile may go on",
        TooFull = "NVIDIA's cache file {0} reached {1} and can't be rewritten ({2}), so no compile may add to it",
        VulkanFull = "NVIDIA's cache file {0} reached {1}, the most a Vulkan compile may write to it, so the compile stopped",
        NoRoomText = "NVIDIA's cache folder holds {0}, which leaves a compile no room to add more and rewrite it within the {1} the driver keeps",
        NoFileText = "NVIDIA's cache folder holds {0} of the compile's files, which leaves none free to add more",
        CantMeasure = "NVIDIA's cache folder {0} can't be measured ({1}), so no compile may add to it",
        Unwatched = "watching NVIDIA's cache files failed ({0}), so no compile may add to them",
        Unstopped = "stopping the compile's process failed ({0}), so no compile may go on",
        NotStopped = "the compile's process didn't stop within {0} of being told to, so its sandbox was ended",
        NotExited = "the compile's process didn't exit within {0} of its end, so its sandbox was ended",
        Orphaned = "the compile's launcher exited while its sandbox still ran, so its sandbox was ended",
        NotEnded = "the compile's sandbox was still there {0} after it was ended, so no compile may go on";

    /// <summary>What a process may still write once stopped, and between two measurements: its creates in flight (not measured).</summary>
    const long StopSlack = 128L << 20;

    /// <summary>The least room a process starts with: with less, the run ends rather than go on a few pipelines at a time.</summary>
    const long MinSegment = 256L << 20;

    /// <summary>The .bin files a process added to: those it made, and those longer than when it started (<paramref name="before"/>).</summary>
    static Dictionary<string, (long Length, ulong Inode)> Grown(IReadOnlyDictionary<string, (long Length, ulong Inode)> before,
        Dictionary<string, (long Length, ulong Inode)> now) =>
        now.Where(b => !(before.TryGetValue(b.Key, out var was) && was.Inode == b.Value.Inode && was.Length >= b.Value.Length)).ToDictionary(StringComparer.Ordinal);

    /// <summary>Why no process may add to the warm's files: one at or past the limit that holds a dictionary, which the pass
    /// left, or that the last process added to (<paramref name="grown"/>: those as it left them; null: before the first);
    /// null when there's none.</summary>
    string? Full(IReadOnlyDictionary<string, (long Length, ulong Inode)>? grown)
    {
        foreach (var (bin, (length, inode)) in _noDict!.Bins().OrderBy(b => b.Key, StringComparer.Ordinal))
            if (length >= _limit && (grown != null && grown.TryGetValue(bin, out var was) && was == (length, inode) || !GlCacheNoDict.Plain(bin)))
                return string.Format(CultureInfo.InvariantCulture, TooFull, Path.GetFileNameWithoutExtension(bin), Format.Bytes(length),
                    _noDict.Left.TryGetValue(bin, out var why) ? why : "it holds no dictionary to drop");
        return null;
    }

    /// <summary>The game's NVIDIA folder's bytes, and what a process may still add to the warm's files it added to, which hold
    /// <paramref name="grown"/> bytes: rewriting them keeps the folder within the driver's cap while both copies are there,
    /// and within the budget once rewritten (<see cref="GlCacheNoDict"/> refuses a rewrite past either). Unmeasured: why the
    /// folder can't be measured, and then no process may add to it.</summary>
    (long Used, long Room, string? Unmeasured) Room(long grown, long more = 0)
    {
        var pass = _noDict!;
        long used;
        try { used = pass.NvidiaBytes() + more; }
        catch (DirectoryNotFoundException) when (!Directory.Exists(pass.NvidiaDir)) { used = 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (0, 0, string.Format(CultureInfo.InvariantCulture, CantMeasure, pass.NvidiaDir, e.Message));
        }
        const double g = NvidiaLinux.RewriteGrowth;
        return (used, (long)Math.Min((pass.Cap - StopSlack - used - g * grown) / (1 + g), (pass.Budget - StopSlack - used - (g - 1) * grown) / g), null);
    }

    /// <summary>Why no process may start for want of room in the folder (<see cref="Room"/>) or of a free file number for
    /// its set's rewrite, <paramref name="always"/> even with room; null when there's room for one. Full: the folder holds no
    /// more, as opposed to unmeasured.</summary>
    (string? Why, bool Full) NoRoom(bool always = false)
    {
        var room = Room(0);
        if (room.Unmeasured is { } why) return (why, false);
        // a process takes a free number, its set's rewrite another (GlCacheNoDict)
        var sets = _noDict!.Bins().GroupBy(b => Path.GetDirectoryName(b.Key)).Select(g => g.Count()).DefaultIfEmpty().Max();
        if (sets >= _noDict.Files - 1) return (string.Format(CultureInfo.InvariantCulture, NoFileText, sets), true);
        return room.Room >= MinSegment && !always ? (null, false)
            : (string.Format(CultureInfo.InvariantCulture, NoRoomText, Format.Bytes(room.Used), Format.Bytes(_noDict.Cap)), true);
    }

    /// <summary>Stops the process once a .bin of the warm's that it added to reaches the limit (a process never moves on to
    /// another file, and past 4 GiB the driver indexes nothing, measured) or the folder has no more room for their rewrite.
    /// A Flatpak Steam game's are in its exchange <paramref name="x"/>, whose growth counts in the folder's.</summary>
    async Task Watch(ProtonProcess p, IReadOnlyDictionary<string, (long Length, ulong Inode)> before, FlatpakExchange? x = null)
    {
        var last = before;
        while (!p.HasExited && !_disposed)
        {
            await Task.WhenAny(p.Exited, Task.Delay(WatchEvery));
            if (p.HasExited) return;
            string? bin = null;
            long length = 0;
            (long Used, long Room, string? Unmeasured) room;
            // anything unexpected stops the process, which never compiles unwatched
            try
            {
                // the pass doesn't run meanwhile: a file that wasn't there is the process's
                var now = x == null ? Listed(last) : x.Bins(_appName);
                var grown = Grown(before, now);
                (bin, (length, _)) = grown.FirstOrDefault(b => b.Value.Length >= _limit);
                room = Room(grown.Values.Sum(b => b.Length), x == null ? 0 : Math.Max(0, x.NvidiaBytes() - _exchangeIn));
                last = now;
            }
            catch (Exception e) { room = (0, 0, string.Format(CultureInfo.InvariantCulture, Unwatched, e.Message)); }
            if (bin == null && room is { Unmeasured: null, Room: > 0 }) continue;
            string? unstopped = null;
            lock (_stopGate)
            {
                if (_disposed || p.HasExited) return;   // the pass may be at its files already
                try
                {
                    if (x == null) File.WriteAllBytes(_stop, []);
                    else if (x.Stop() is { } why) _unstopped = unstopped = string.Format(CultureInfo.InvariantCulture, Unstopped, why);
                }
                catch (Exception e) { _unstopped = unstopped = string.Format(CultureInfo.InvariantCulture, Unstopped, e.Message); }
                if (x != null) p.StopAsked();
                _reached = (bin, bin == null && room.Unmeasured == null && unstopped == null, room.Unmeasured ?? unstopped);
            }
            Job.Log?.Invoke(unstopped ?? (bin != null ? $"{Path.GetFileNameWithoutExtension(bin)} reached {Format.Bytes(length)}"
                    : room.Unmeasured ?? $"NVIDIA's cache folder holds {Format.Bytes(room.Used)}, all the room its rewrite leaves")
                + ": stopping the compile's process" + (_ctx != null && bin != null && room.Unmeasured == null ? ", the next one goes on in a fresh file" : ""));
            return;
        }
    }

    /// <summary>The warm's .bin files as <see cref="GlCacheNoDict.Bins"/> lists them, and each one of <paramref name="last"/> it
    /// doesn't list measured on its own (its .toc may be being written). Throws when the listing fails or one of them is gone,
    /// so no file escapes the watch.</summary>
    Dictionary<string, (long Length, ulong Inode)> Listed(IReadOnlyDictionary<string, (long Length, ulong Inode)> last)
    {
        var now = _noDict!.Bins(strict: true);
        foreach (var bin in last.Keys.Where(b => !now.ContainsKey(b)))
            now[bin] = new FileInfo(bin) is { Exists: true } f && Libc.Stat(bin) is { } st ? (f.Length, st.Inode)
                : throw new IOException($"{Path.GetFileName(bin)} went missing while the compile ran");
        return now;
    }

    /// <summary>Runs the pass: null when it left no rewrite half done, else why no warm process may start, a fault it didn't
    /// handle included (what that left isn't known).</summary>
    string? NoDict(Action pass)
    {
        try { pass(); }
        catch (Exception e)
        {
            Job.Log?.Invoke($"rewriting NVIDIA's cache files failed: {e.Message}");
            return $"{Unsettled} ({e.Message}): compile again later";
        }
        return _noDict!.Unfinished.Values.FirstOrDefault() is not { } why ? null
            : why.EndsWith(GlCacheNoDict.OpenElsewhere, StringComparison.Ordinal) ? $"{Unsettled} ({why}): close the game and compile again" : $"{Unsettled} ({why})";
    }

    public IWarmProcess Start(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, Action<string> errorLine)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((_halt ?? _unstopped) is { } why) throw new InvalidOperationException(why);
        var n = Interlocked.Increment(ref _started);
        string copy;
        lock (_copies)
            if (!_copies.TryGetValue(exe, out copy!)) _copies[exe] = copy = NativeCopy(exe, _dataDir, _ctx == null && _wine == null ? LinuxFiles : NativeFiles);
        var priority = args.ToList().IndexOf("--priority") is >= 0 and var at && at + 1 < args.Count && args[at + 1] == "idle" ? WarmPriority.Idle : WarmPriority.BelowNormal;
        var x = _flatpak == null ? null : Exchange(ref copy);
        try
        {
            if (x != null)
            {
                // the run names some paths once (the layer's), in its first process's exchange
                var name = Path.GetFileName(x.Path);
                args = [.. args.Select(a => _earlier.Aggregate(a, (s, e) => s.Replace(e, name, StringComparison.Ordinal)))];
                _earlier.Add(name);
            }
            var (outFile, errFile) = x != null ? (x.OutPath, x.ErrPath) : (Path.Combine(_work, $"warm-{n}.out"), Path.Combine(_work, $"warm-{n}.err"));
            if (x == null)
            {
                File.Delete(outFile);
                File.Delete(errFile);
            }
            List<string> all = [.. args, "--out", WarmPath(outFile), "--err", WarmPath(errFile), "--alive-file", WarmPath(x?.AlivePath ?? _alive), "--alive-turns", AliveTurns.ToString()];
            var unit = $"scskiller-warm-{Guid.NewGuid():N}";
            var argv = x != null ? ProtonLaunch.FlatpakCommand(_flatpak!, priority, Libc.Nice(), x.LauncherLogPath, x.Path, _ctx!.Runtime, _ctx.Proton, WarmPath(copy), all)
                : _wine != null ? ProtonLaunch.NativeCommand(_tools, priority, Libc.Nice(), unit, _launcherLog, _wine.Bin, [WarmPath(copy), .. all])
                : _ctx == null ? ProtonLaunch.NativeCommand(_tools, priority, Libc.Nice(), unit, _launcherLog, copy, all)
                : ProtonLaunch.Command(_tools, priority, Libc.Nice(), unit, _launcherLog, _ctx.Runtime, _ctx.Proton, WarmPath(copy), all);
            var psi = new ProcessStartInfo(argv[0]) { UseShellExecute = false, WorkingDirectory = x?.WorkPath ?? _work };
            foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
            var inherited = Inherited ?? System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.Ordinal);
            psi.Environment.Clear();
            var home = System.Environment.GetEnvironmentVariable("HOME");
            foreach (var (k, v) in x != null ? ProtonLaunch.Environment(inherited.Where(e => ProtonLaunch.FlatpakKept(e.Key)), _ctx!, Wine.CacheEnvironment(x.CachePath, _exeName, _cacheCap).Warm,
                             _gpuName, Job.Token, env, home, [x.Path], x.WorkPath)
                         : _wine != null ? ProtonLaunch.WineEnvironment(inherited, _wine.WineDir, _cache, _gpuName, Job.Token, env)
                         : _ctx == null ? ProtonLaunch.NativeEnvironment(inherited, _cache, _mesa, Job.Token, env, AlsoInherited)
                         : ProtonLaunch.Environment(inherited, _ctx, _cache, _gpuName, Job.Token, env, home, [_dataDir], _work))
                psi.Environment[k] = v;
            var logFrom = x == null && File.Exists(_launcherLog) ? new FileInfo(_launcherLog).Length : 0;
            var before = _noDict?.Bins(strict: true) ?? [];
            var watched = x?.Bins(_appName) ?? before;
            if (x != null)
            {
                _sandbox?.Dispose();
                (_sandbox, _scopes) = (null, null);
                Job.Within = static _ => false;   // till its sandbox is known
            }
            var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + argv[0]);
            if (x != null)
            {
                var sandbox = _sandbox = new FlatpakSandbox(p);
                if (!sandbox.Ready)
                {
                    // without the launcher's pidfd the sandbox can't be told from the host or ended: don't run one we can't stop
                    try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    Job.Log?.Invoke($"pidfd_open failed on the compile's launcher ({System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(sandbox.Error)})");
                    throw new InvalidOperationException(WarmHost.FlatpakNoPidfdError);
                }
                Job.Within = sandbox.Holds;
                if (_tools.SystemdRun is { } run)
                    _scopes = new FlatpakScopes(Path.Combine(Path.GetDirectoryName(run)!, "systemctl"), _tools.SystemdVersion, priority, sandbox, Job.Log);
            }
            var proc = new ProtonProcess(this, p, outFile, errFile, logFrom, errorLine, x, _sandbox);
            if (x != null)
                lock (_stopGate)
                {
                    _running = proc;
                    if (_runStopped) proc.StopAsked();
                }
            if (_noDict != null)
            {
                _before = before;
                _ = Task.Run(() => Watch(proc, watched, x));
            }
            return proc;
        }
        catch when (x != null)
        {
            lock (_stopGate)
                if (_exchange == x) _exchange = null;
            x.Dispose();
            throw;
        }
    }

    /// <summary>The next process's exchange, made now as <see cref="WarmPath"/> named it: the warm's files of
    /// <paramref name="copy"/> (its <see cref="NativeCopy"/>, then its copy in the exchange), what the warm reads of the work
    /// folder, Core's sets and vkd3d-proton's files copied in. The last process's exchange goes.</summary>
    FlatpakExchange Exchange(ref string copy)
    {
        FlatpakExchange? last;
        lock (_stopGate) (last, _exchange) = (_exchange, null);
        last?.Dispose();   // AfterProcess took its files out
        var x = FlatpakExchange.Make(_dataDir, _next, stopped: false);
        _next = FlatpakExchange.NewName();
        try
        {
            var dir = Path.GetDirectoryName(copy)!;
            var root = Path.GetFileName(dir) == "segheap" ? Path.GetDirectoryName(dir)! : dir;
            var rel = Path.GetRelativePath(root, copy);
            foreach (var f in NativeFiles.Append(rel).Distinct())
                if (File.Exists(Path.Combine(root, f))) x.CopyIn(Path.Combine(root, f), $"{FlatpakExchange.Bin}/{f}");
            string[] inputs;
            lock (_inputs) inputs = [.. _inputs];
            foreach (var f in inputs)
                if (File.Exists(Path.Combine(_work, f))) x.CopyIn(Path.Combine(_work, f), $"{FlatpakExchange.Work}/{f}");
                else if (Directory.Exists(Path.Combine(_work, f))) x.CopyTreeIn(Path.Combine(_work, f), $"{FlatpakExchange.Work}/{f}");
            x.CopySetsIn(NvidiaLinuxAppCache.CacheDir(_gameDir), _appName);
            x.CopyVkd3dIn(Path.Combine(Wine.CacheRoot(_gameDir), "vkd3d"), _exeName);
            _exchangeIn = x.NvidiaBytes();
            Job.Stage(x.WorkPath);
            copy = Path.Combine(x.Path, FlatpakExchange.Bin, rel);
            lock (_stopGate)
            {
                if (_runStopped && x.Stop() is { } why) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Unstopped, why));
                _exchange = x;
            }
            return x;
        }
        catch
        {
            x.Dispose();
            throw;
        }
    }

    static readonly string[] NativeFiles = ["d3d12.dll", "scskiller_warm.exe", Path.Combine("segheap", "scskiller_warm.exe")];

    /// <summary>The native Vulkan warm's files: it runs the replayer beside it.</summary>
    internal static readonly string[] LinuxFiles = ["scskiller_warm", "fossilize-replay"];

    /// <summary>A copy of the warm exe <paramref name="exe"/> to run, with the <paramref name="files"/> beside it in their
    /// layout (default: the PE files, the proxy beside the NT-heap exe and above segheap\), in <paramref name="dataDir"/>'s
    /// native/&lt;content key&gt;/: the runtime's container sees the home folder but has a /usr of its own, where the app may
    /// be installed, and an update of the app never replaces a running warm's. A copy already there with the same content is
    /// reused.</summary>
    internal static string NativeCopy(string exe, string dataDir, IEnumerable<string>? files = null)
    {
        var full = Path.GetFullPath(exe);
        var dir = Path.GetDirectoryName(full)!;
        var root = Path.GetFileName(dir) == "segheap" ? Path.GetDirectoryName(dir)! : dir;
        var rel = Path.GetRelativePath(root, full);
        var copied = (files ?? NativeFiles).Append(rel).Distinct().Where(f => File.Exists(Path.Combine(root, f))).Order(StringComparer.Ordinal)
            .Select(f => (Name: f, Hash: Hash(Path.Combine(root, f)))).ToList();
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', copied.Select(f => $"{f.Name}\0{f.Hash}")))))[..16];
        var target = Path.Combine(dataDir, "native", key);
        bool Copied() => copied.All(f => File.Exists(Path.Combine(target, f.Name)) && Hash(Path.Combine(target, f.Name)) == f.Hash);
        if (Copied()) return Path.Combine(target, rel);
        var temp = $"{target}.{Guid.NewGuid():N}";
        try
        {
            foreach (var (name, _) in copied)
            {
                var to = Path.Combine(temp, name);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(root, name), to);
            }
            if (Directory.Exists(target)) Directory.Delete(target, true);   // its content isn't its key's
            Directory.Move(temp, target);
        }
        catch (IOException) when (Copied()) { }   // another Core made it meanwhile
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
        return Path.Combine(target, rel);
    }

    static string Hash(string file)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>One warm process: its launcher (setsid's), and the out and err files its warm writes.</summary>
    sealed class ProtonProcess : IWarmProcess
    {
        readonly ProtonWarmHost _host;
        readonly Process _launcher;
        readonly Task _launcherExited, _outDone, _errDone;
        readonly TaskCompletionSource _warmGone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);   // the sandbox is gone
        readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
        readonly Lock _gate = new();
        int? _code;
        int _errLines;
        bool _suspended, _watching, _killing;
        volatile bool _seen, _stopAsked, _ending, _killed, _unended;
        readonly Microsoft.Win32.SafeHandles.SafeFileHandle? _launcherLog;   // an exchange's, Core's own file whatever its name now

        /// <param name="x">a Flatpak Steam game's exchange: its out, err and launcher log are read through Core's own handles
        /// on them, each up to <see cref="FlatpakOutputMax"/></param>
        /// <param name="sandbox">a Flatpak Steam game's sandbox, ended once the warm ignores its stop (<see cref="Bound"/>)</param>
        public ProtonProcess(ProtonWarmHost host, Process launcher, string outFile, string errFile, long logFrom, Action<string> errorLine, FlatpakExchange? x = null,
            FlatpakSandbox? sandbox = null)
        {
            (_host, _launcher) = (host, launcher);
            var (outHandle, errHandle) = x == null ? (null, null) : (FlatpakExchange.Reopen(x.Out), FlatpakExchange.Reopen(x.Err));
            _launcherLog = x == null ? null : FlatpakExchange.Reopen(x.LauncherLog);
            var max = x == null ? long.MaxValue : FlatpakOutputMax;
            _launcherExited = launcher.WaitForExitAsync().ContinueWith(_ => launcher.Dispose(), TaskScheduler.Default);
            Output = new ChannelReader(_lines.Reader);
            // a sandbox gone, or past its kill, writes no more
            var end = sandbox == null ? Task.WhenAny(_launcherExited, _warmGone.Task) : Task.WhenAny(_launcherExited, _warmGone.Task, _settled.Task);
            _outDone = Task.Run(async () =>
            {
                try
                {
                    await Follow(outFile, end, l =>
                    {
                        if (ExitCode(l) is { } code)   // the last line
                        {
                            _code = code;
                            _ending = true;
                            return false;
                        }
                        if (sandbox != null && Ending(l)) _ending = true;
                        _lines.Writer.TryWrite(l);
                        return true;
                    }, host.Poll, outHandle, max);
                }
                finally
                {
                    // out read to its end or its cap counts as the warm's end: past ExitBound the sandbox is ended
                    if (sandbox != null) _ending = true;
                    _lines.Writer.TryComplete();
                }
            });
            _errDone = Task.Run(() => Follow(errFile, _outDone, l =>
            {
                if (l.Length == 0) return true;
                Interlocked.Increment(ref _errLines);
                errorLine(l);
                return true;
            }, host.Poll, errHandle, max));
            Exited = Exit(logFrom, errorLine);
            _ = Task.Run(WatchWarm);
            if (sandbox != null) _ = Task.Run(() => Bound(sandbox));
            else _settled.TrySetResult();
        }

        /// <summary>The warm was told to stop: <see cref="Bound"/> counts from here.</summary>
        public void StopAsked() => _stopAsked = true;

        /// <summary>Completes once a Flatpak Steam game's sandbox is gone (<see cref="FlatpakSandbox.Gone"/>): the host waits
        /// for this before it reads and removes the exchange and starts the next process, so a warm that faked its exit and
        /// kept running can't keep the exchange's handles or add to the disk behind a disposed sandbox.</summary>
        public Task Settled => _settled.Task;

        /// <summary>Its sandbox was killed and still wasn't gone within <see cref="StopBound"/>: <see cref="Settled"/>
        /// completed without it.</summary>
        public bool Unended => _unended;

        /// <summary>Ends a Flatpak Steam game's sandbox (<see cref="FlatpakSandbox.End"/>) and waits until it's gone, counting
        /// from its own termination, not the warm's protocol: once told to stop or killed, it must stop within
        /// <see cref="StopBound"/>, and once it said its process ends or wrote its last line, it must exit within
        /// <see cref="ExitBound"/>. Past the bound its sandbox is ended, and at once when its launcher exited before it. Time it
        /// spends suspended doesn't count. Still there <see cref="StopBound"/> after it was ended, it's left
        /// (<see cref="Unended"/>).</summary>
        async Task Bound(FlatpakSandbox sandbox)
        {
            var (clock, stopRan, exitRan, endRan) = (Stopwatch.StartNew(), TimeSpan.Zero, TimeSpan.Zero, (TimeSpan?)null);
            try
            {
                while (!sandbox.Gone)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    var step = clock.Elapsed;
                    clock.Restart();
                    if (sandbox.Gone) continue;
                    if (endRan is { } ran)
                    {
                        // past a kill only a process held in the kernel stays, and it can't run again: the run halts rather than hang
                        if ((endRan = ran + step) < _host.StopBound) continue;
                        _unended = true;
                        _host.Job.Log?.Invoke(string.Format(CultureInfo.InvariantCulture, NotEnded, Format.Duration(_host.StopBound)));
                        return;
                    }
                    TimeSpan bound;
                    string text;
                    // the launcher exits only after the sandbox's first process, unless it was killed
                    if (_launcherExited.IsCompleted) (bound, text) = (TimeSpan.Zero, Orphaned);
                    else if (_suspended) continue;
                    else if (_ending) (exitRan, bound, text) = (exitRan + step, _host.ExitBound, NotExited);
                    else if (_stopAsked || _killed) (stopRan, bound, text) = (stopRan + step, _host.StopBound, NotStopped);
                    else continue;
                    if (bound > TimeSpan.Zero && (_ending ? exitRan : stopRan) < bound) continue;
                    endRan = TimeSpan.Zero;
                    if (sandbox.End()) _host.Job.Log?.Invoke(string.Format(CultureInfo.InvariantCulture, text, Format.Duration(bound)));
                }
            }
            finally { _settled.TrySetResult(); }
        }

        public TextReader Output { get; }

        public Task<int?> Exited { get; }

        public bool HasExited => _outDone.IsCompleted || _launcherExited.IsCompleted || _warmGone.Task.IsCompleted;

        /// <summary>Completes <see cref="_warmGone"/> once the warm, seen in the job or killed, has left it without its last
        /// line and stayed out <see cref="Linger"/>: a launcher that hangs after its warm died isn't waited for. Before the
        /// warm shows up, only the launcher's exit ends the process.</summary>
        async Task WatchWarm()
        {
            while (!_launcherExited.IsCompleted && !_outDone.IsCompleted)
            {
                var members = _host.Job.Members();
                _host._scopes?.Lower(members);
                var gone = members.Count == 0;
                if (!gone) _seen = true;
                if (_seen && gone)
                {
                    await Task.WhenAny(_launcherExited, _outDone, Task.Delay(_host.Linger));
                    if (!_launcherExited.IsCompleted && !_outDone.IsCompleted && _host.Job.Members().Count == 0)
                    {
                        _warmGone.TrySetResult();
                        return;
                    }
                }
                else await Task.WhenAny(_launcherExited, _outDone, Task.Delay(250));
            }
        }

        async Task<int?> Exit(long logFrom, Action<string> errorLine)
        {
            await _outDone;
            // the warm may still be exiting (a driver writes its cache then): it is waited for, a launcher lingering after it isn't
            var linger = Task.Delay(_host.Linger);
            while (!_launcherExited.IsCompleted && !_warmGone.Task.IsCompleted && !_unended && !(linger.IsCompleted && _host.Job.Members().Count == 0))
                await Task.WhenAny(_launcherExited, linger.IsCompleted ? Task.Delay(500) : linger);
            await _errDone;
            // no line of the warm's to go by: the launcher's own say why it ended
            if (_code == null && Volatile.Read(ref _errLines) == 0)
                foreach (var l in (_launcherLog != null ? LauncherLines(_launcherLog, FlatpakOutputMax) : LauncherLines(_host._launcherLog, logFrom)).TakeLast(3)) errorLine(l);
            _launcherLog?.Dispose();
            return _code;
        }

        public void Suspend(bool suspend)
        {
            lock (_gate)
            {
                _suspended = suspend;
                _host.Job.Suspend(suspend);
                if (!suspend || _watching || HasExited || _host.Job.Members().Count > 0) return;
                _watching = true;
            }
            // a warm Proton hasn't started yet is stopped once it shows up
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.WhenAny(_launcherExited, _outDone, Task.Delay(500));
                    lock (_gate)
                    {
                        if (!_suspended || HasExited || _host.Job.Members().Count > 0)
                        {
                            if (_suspended && !HasExited) _host.Job.Suspend(true);
                            _watching = false;
                            return;
                        }
                    }
                }
            });
        }

        public void Kill()
        {
            _killed = true;   // arms the sandbox's deadline even if no warm ever shows and no terminal line comes
            if (_host.Job.Kill()) _seen = true;
            lock (_gate)
            {
                if (_killing || HasExited) return;
                _killing = true;
            }
            // as Suspend: a warm Proton starts after this is ended once it shows up, until the process has exited and no
            // member has shown up for a second (a child forked as its parent was killed comes up after it)
            _ = Task.Run(async () =>
            {
                var quiet = Stopwatch.StartNew();
                while (!HasExited || quiet.Elapsed < TimeSpan.FromSeconds(1))
                {
                    await (HasExited ? Task.Delay(250) : Task.WhenAny(_launcherExited, _outDone, _warmGone.Task, Task.Delay(250)));
                    if (_host.Job.Kill())
                    {
                        _seen = true;
                        quiet.Restart();
                    }
                }
            });
        }

        /// <summary>The line is the warm's done, retry or stopping event: its process ends.</summary>
        static bool Ending(string line)
        {
            if (!line.StartsWith('{') || !line.Contains("\"done\"", StringComparison.Ordinal) && !line.Contains("\"retry\"", StringComparison.Ordinal)
                && !line.Contains("\"stopping\"", StringComparison.Ordinal)) return false;
            try
            {
                using var d = JsonDocument.Parse(line);
                return d.RootElement is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String
                    && e.GetString() is "done" or "retry" or "stopping";
            }
            catch (JsonException) { return false; }
        }

        static IEnumerable<string> LauncherLines(Microsoft.Win32.SafeHandles.SafeFileHandle log, long max)
        {
            try
            {
                var length = Math.Min(RandomAccess.GetLength(log), max);
                var buffer = new byte[Math.Min(length, 1 << 20)];
                var n = RandomAccess.Read(log, buffer, length - buffer.Length);
                return Encoding.UTF8.GetString(buffer, 0, n).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }

        static IEnumerable<string> LauncherLines(string log, long from)
        {
            try
            {
                using var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                f.Position = Math.Min(from, f.Length);
                return new StreamReader(f).ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>The exit line's code, the warm's last line in --out ({"event":"exit","code":N}); null for any other line.</summary>
    internal static int? ExitCode(string line)
    {
        if (!line.StartsWith('{') || !line.Contains("\"exit\"", StringComparison.Ordinal)) return null;
        try
        {
            using var d = JsonDocument.Parse(line);
            return d.RootElement is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String
                && e.GetString() == "exit" && o.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var code) ? code : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The most a Flatpak Steam game's warm's out, err or launcher log is read of: the sandbox writes them.</summary>
    const long FlatpakOutputMax = 256L << 20;

    /// <summary>Follows <paramref name="path"/> as another process writes it (it may not exist yet), or the file
    /// <paramref name="handle"/> is open on, which it then owns: each complete line, its \r\n or \n off, to
    /// <paramref name="line"/>, which returns false to stop there. Once <paramref name="end"/> has completed it reads what's
    /// left, a last line without its end too, and returns. Past <paramref name="max"/> bytes it reads no more.</summary>
    internal static async Task Follow(string path, Task end, Func<string, bool> line, TimeSpan poll, Microsoft.Win32.SafeHandles.SafeFileHandle? handle = null, long max = long.MaxValue)
    {
        var h = handle;
        var pending = new MemoryStream();
        var buffer = new byte[1 << 16];
        long at = 0;
        bool Emit()
        {
            var s = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
            pending.SetLength(0);
            return line(s.EndsWith('\r') ? s[..^1] : s);
        }
        try
        {
            while (true)
            {
                var ending = end.IsCompleted || at >= max;   // first: what was written before the end is read below
                if (h == null)
                    try { h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
                var n = h == null || at >= max ? 0 : RandomAccess.Read(h, buffer.AsSpan(0, (int)Math.Min(buffer.Length, max - at)), at);
                if (n > 0)
                {
                    at += n;
                    var from = 0;
                    for (var i = 0; i < n; i++)
                        if (buffer[i] == '\n')
                        {
                            pending.Write(buffer, from, i - from);
                            from = i + 1;
                            if (!Emit()) return;
                        }
                    pending.Write(buffer, from, n - from);
                    continue;
                }
                if (ending)
                {
                    if (pending.Length > 0) Emit();
                    return;
                }
                await Task.WhenAny(end, Task.Delay(poll));
            }
        }
        finally { h?.Dispose(); }
    }

    /// <summary>The lines the out file's follower passes on, as a reader that ends with them.</summary>
    sealed class ChannelReader(ChannelReader<string> lines) : TextReader
    {
        public override async Task<string?> ReadLineAsync() => await lines.WaitToReadAsync() && lines.TryRead(out var l) ? l : null;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct) => await lines.WaitToReadAsync(ct) && lines.TryRead(out var l) ? l : null;

        public override string? ReadLine() => ReadLineAsync().GetAwaiter().GetResult();
    }
}
