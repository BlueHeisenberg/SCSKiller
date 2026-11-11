using System.Diagnostics;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Vulkan;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    /// <summary>Steam's games and the non-Steam games added to it (ready once it runs them through Proton), the native Steam's
    /// or else the Flatpak or Snap Steam's, warmed in their Proton prefixes (<see cref="WarmHost.For"/>; the Flatpak Steam's
    /// listed only), and Heroic's, Lutris's and Faugus's Windows games, warmed in their launcher's prefix, and the games added by hand in a Proton prefix: no
    /// recorder for a sandboxed Steam's games (<see cref="SteamSandboxSkip"/>), and no compile for the Flatpak Steam's where
    /// none of them can be (<see cref="SteamSandboxCompile"/>).</summary>
    public static partial ScsKiller CreateDefault(Func<Game, bool>? only)
    {
        var vendor = DetectGpu();
        var steam = SteamSource.Root();
        SteamSandboxCompile = WarmHost.FlatpakSteamGames(steam, MesaLinux.Is(vendor.Caps));
        SteamSandboxSkip = SteamSource.Sandbox(steam) switch { "Flatpak" => LinuxStartup.InFlatpak ? WarmHost.InFlatpakError : SkipFlatpak, "Snap" => SkipSandboxed, _ => null };
        var grant = GrantFolder(AppStore.DefaultDir);
        FlatpakSkip = (g, vulkan) => FlatpakGrant.AppOf(g) is { } a && !FlatpakSees(a.App, grant, vulkan)
            ? FlatpakGrant.CantGrant(grant) == null ? vulkan ? FlatpakVulkanSkipLine(a.Launcher) : FlatpakSkipLine(a.Launcher)
                : vulkan ? FlatpakVulkanManualLine(grant, a.App, a.Launcher) : FlatpakManualLine(grant, a.App, a.Launcher) : null;
        // macOS: no game until its stores and a compile there exist
        var k = new ScsKiller(OperatingSystem.IsMacOS() ? [] : [new SteamSource(steam), new SteamShortcutSource(steam), .. HeroicSource.Runners.Select(r => new HeroicSource(r, steamRoot: steam)), new LutrisSource(steamRoot: steam), new FaugusSource(steamRoot: steam), new ManualSource(new AppStore(AppStore.DefaultDir))],
            vendor, DefaultReaders(), new Planner(Path.Combine(AppStore.DefaultDir, "packs"), SharedPackDir(AppStore.DefaultDir, vendor.Vendor)), new Warmer(vendor) { SteamRoot = steam },
            AppStore.DefaultDir, NativeTools.Find("d3d12.dll"), only) { Adapters = GpuBackends.Adapters, AgsDll = NativeTools.Find("scskiller_ags.dll"), SteamRoot = steam };
        k.RedetectGpu();   // DriverId from Vulkan before anything compares it
        SessionIdle.Log = line => k.Log?.Report(line);
        return k;
    }

    /// <summary>On arm64 under Mesa's Turnip, a game whose own driver files pick a Turnip build of their own (Armada's
    /// per-game pick, or VK_DRIVER_FILES in its launch options; <see cref="LaunchGpu.TurnipLibrary"/>) is compiled for that
    /// build, not the one SCSKiller's Vulkan lists: its DriverId gains the build's file (path, size, time and a hash of its
    /// content: Mesa keys its cache on the library's build id, else on that file's time, none of Armada's builds has an
    /// id, and an image-based system such as Armada gives every file under /usr the same time, 1970), and its version
    /// reads "Turnip &lt;build&gt;". So a new build of the game's own driver makes its compile stale, and one of the system's
    /// alone doesn't. Read again at most every 10 s; a file's hash once per path, size and time.</summary>
    partial void GameDriver(Game g, ref GpuSnapshot s)
    {
        if (s.Id == null || s.Id.Contains(TurnipTag, StringComparison.Ordinal)
            || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64
            || Vendor is not MesaLinuxBackend { Driver: MesaLinux.Turnip } || TurnipBuild(g) is not { } build) return;
        s = s with { Id = s.Id + TurnipTag + build.Identity, Gpu = s.Gpu with { DriverVersion = $"Turnip {build.Name}" } };
    }

    const string TurnipTag = "|turnip:";
    readonly Dictionary<string, (long At, (string Identity, string Name)? Build)> _turnipBuilds = [];
    readonly Dictionary<string, string> _turnipHashes = [];   // "path|size|time" -> its content's SHA-256, the first 16 hex digits

    (string Identity, string Name)? TurnipBuild(Game g)
    {
        var now = Environment.TickCount64;
        lock (_turnipBuilds)
            if (_turnipBuilds.TryGetValue(g.Id, out var c) && now - c.At < 10_000) return c.Build;
        (string, string)? build = null;
        try
        {
            if (LaunchGpu.TurnipLibrary(LaunchGpu.Of(g, (_warmer as Warmer)?.SteamRoot ?? SteamSource.Root())) is { } library && new FileInfo(library) is { Exists: true } f)
            {
                var dir = Path.GetDirectoryName(library)!;
                // Armada's system builds sit in <name>/aarch64/, its users' in <name>/
                var name = Path.GetFileName(dir) is "aarch64" or "lib" or "lib64" ? Path.GetFileName(Path.GetDirectoryName(dir)!) : Path.GetFileName(dir);
                var file = $"{library}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
                string? hash;
                lock (_turnipHashes) _turnipHashes.TryGetValue(file, out hash);
                if (hash == null)
                {
                    using (var stream = File.OpenRead(library)) hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream))[..16];
                    lock (_turnipHashes) _turnipHashes[file] = hash;
                }
                build = ($"{file}|{hash}", name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        lock (_turnipBuilds) _turnipBuilds[g.Id] = (now, build);
        return build;
    }

    partial void WineBuiltinFirst(Game g, ref IReadOnlySet<string>? dlls) =>
        dlls = SteamLaunchOptions.BuiltinFirst(SCSKiller.Core.Unreal.UnrealRhi.LaunchOptions(g, (_warmer as Warmer)?.SteamRoot ?? SteamSource.Root()));

    partial void GameGpu(Game g, ref GpuInfo? gpu, ref IReadOnlyList<(string Name, string Value)>? env)
    {
        if (LaunchGpu.Vkd3dConfig(g, PlaceSteamRoot) is { } vkd3d) env = [vkd3d];
        if (LaunchGpu.Of(g, PlaceSteamRoot) is not { Count: > 0 } vars || Adapters?.Invoke() is not { Count: > 0 } all) return;
        // the devices listed are SCSKiller's, which its own environment may have cut short of the game's
        string? why;
        var (picked, software) = ((DxgiAdapter?)null, false);
        if (LaunchGpu.Hides(Environment.GetEnvironmentVariable, vars)) why = "SCSKiller's own environment hides GPUs";
        else picked = LaunchGpu.Pick(vars, all, out why, out software);
        if (why != null)
        {
            if (software) throw new NotReadyException(why);
            // not told, not refused: the Settings GPU as without the variables, which could pick another device than it
            Log?.Report($"{g.Name}: SCSKiller can't tell which GPU its launch options run it on ({why}), the compile runs on {Vendor.Gpu.Name}");
            return;
        }
        if (env is { } vkd3dEnv) vars = [.. vars, .. vkd3dEnv];
        env = vars;
        if (picked == null) return;
        var backend = GpuBackends.For(picked, Settings.ExperimentalIntel);
        if (backend is UnsupportedVendor u) throw new NotReadyException(u.Why ?? $"SCSKiller doesn't compile on {picked.Gpu.Name}.");
        // the backend is the process's: a game on the other kind of driver isn't compiled by it
        if (MesaLinux.Is(backend.Caps) != MesaLinux.Is(Vendor.Caps))
            throw new NotReadyException($"{g.Name}'s launch options run it on {picked.Gpu.Name}. Pick {picked.Gpu.Name} in Settings to compile it.");
        if ((picked.Gpu.AdapterLuid, picked.Gpu.Name) == (Vendor.Gpu.AdapterLuid, Vendor.Gpu.Name)) return;
        Log?.Report($"{g.Name}: its launch options run it on {picked.Gpu.Name}, so the compile runs there");
        gpu = picked.Gpu;
    }

    /// <summary>Steam's launches in progress, of its games and of the non-Steam games added to it: its reaper
    /// (<see cref="SteamSource.LaunchingApp"/>) runs from the launch's start, while the game's own process waits for the
    /// prefix's wineserver, which a warm in the prefix holds. Once an exe of the app's games has run under the reaper for
    /// <see cref="GameSeenAfter"/> and none does any more, the launch counts only while its wineserver runs: a native
    /// helper that outlives the game keeps the reaper running, while a Windows one (a launcher that starts the game again,
    /// a crash reporter) keeps the wineserver a warm would join, holding Steam's launch open. A reaper that keeps a game
    /// running with none of its exes under it, or outlives the game, is logged once <see cref="LaunchTellAfter"/> on, with
    /// the processes under it.</summary>
    partial void AddLaunches(List<(int Pid, int Parent, string Exe)> all, HashSet<string> running)
    {
        var reapers = all.Where(p => p.Exe == "reaper")
            .Select(p => (p.Pid, App: ProcessTree.CommandLine(p.Pid) is { } argv ? SteamSource.LaunchingApp(argv) : null, Started: ProcessTree.Created(p.Pid)))
            .Where(r => r.App != null && r.Started != null).Select(r => (r.Pid, App: r.App!.Value, Started: r.Started!.Value)).ToList();
        lock (_launches)
            foreach (var gone in _launches.Keys.Where(k => !reapers.Any(r => (r.Pid, r.Started) == k)).ToList()) _launches.Remove(gone);
        if (reapers.Count == 0) return;
        var children = all.ToLookup(p => p.Parent);
        foreach (var (pid, app, started) in reapers)
        {
            var games = Games.Where(s => (SteamSource.AppId(s.Game.Id) ?? SteamShortcuts.AppId(s.Game.Id)) == app).ToList();
            if (games.Count == 0) continue;
            var exes = games.Select(s => Path.GetFileName(s.Game.ExePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var under = Under(pid, children);
            var runs = under.Any(p => exes.Contains(p.Exe));
            var wine = under.Any(p => p.Exe == "wineserver");
            var now = Timestamp();
            bool counts;
            string? tell = null;
            lock (_launches)
            {
                if (!_launches.TryGetValue((pid, started), out var l)) _launches[(pid, started)] = l = new(now);
                l.Runs = runs ? l.Runs ?? now : null;
                l.GameSeen |= l.Runs is { } since && Stopwatch.GetElapsedTime(since, now) >= GameSeenAfter;
                l.Closed = runs || !l.GameSeen ? null : l.Closed ?? now;
                counts = !l.GameSeen || runs || wine;
                var min = (int)LaunchTellAfter.TotalMinutes;
                if (l.Closed is { } closed && !l.ToldClosed && Stopwatch.GetElapsedTime(closed, now) >= LaunchTellAfter)
                {
                    l.ToldClosed = true;
                    tell = $"Steam's launch of the game (reaper {pid}) still runs {min} min after the game closed, which counts as "
                        + (wine ? "running while its wineserver runs" : "closed");
                }
                else if (!l.GameSeen && !l.ToldWaits && Stopwatch.GetElapsedTime(l.First, now) >= LaunchTellAfter)
                {
                    l.ToldWaits = true;
                    tell = $"Steam's launch of the game (reaper {pid}) has counted as the game running for {min} min with no {string.Join(" or ", exes)} under it";
                }
            }
            if (counts) running.UnionWith(exes);
            if (tell != null)
                Log?.Report($"{games[0].Game.Name}: {tell}. Under it: " + (under.Count == 0 ? "nothing"
                    : string.Join(", ", under.Take(40).Select(p => $"{p.Pid} {p.Exe}")) + (under.Count > 40 ? $" and {under.Count - 40} more" : "")));
        }
    }

    /// <summary>How long a reaper keeps a game running alone, or outlives it, before the processes under it are logged.</summary>
    static readonly TimeSpan LaunchTellAfter = TimeSpan.FromMinutes(2);

    static readonly TimeSpan GameSeenAfter = TimeSpan.FromMinutes(1);   // a launcher stub or a DRM check lasts seconds

    readonly Dictionary<(int Pid, long Started), Launch> _launches = [];   // Steam's reapers launching a listed game, under its own lock

    sealed class Launch(long first)
    {
        public readonly long First = first;
        public bool GameSeen, ToldWaits, ToldClosed;
        public long? Runs;     // since when one of the app's exes runs under it
        public long? Closed;   // since when none of the app's exes runs under it, once one has
    }

    // the launch's orphans stay under it: the reaper and pressure-vessel's pv-adverb are subreapers
    static List<(int Pid, int Parent, string Exe)> Under(int root, ILookup<int, (int Pid, int Parent, string Exe)> children)
    {
        var seen = new HashSet<int> { root };
        var found = new List<(int Pid, int Parent, string Exe)>();
        var next = new Queue<int>([root]);
        while (next.TryDequeue(out var pid))
            foreach (var c in children[pid])
                if (seen.Add(c.Pid))
                {
                    found.Add(c);
                    next.Enqueue(c.Pid);
                }
        return found;
    }

    /// <summary>The folder a Flatpak launcher is let read: the recorder's files alone, never the data folder's others.</summary>
    static string GrantFolder(string dataDir) => Path.Combine(Path.GetFullPath(dataDir), FlatpakGrant.Folder);

    /// <summary>The app's sandbox sees the recorder folder <paramref name="grant"/>, and for a Vulkan game may write in its
    /// vulkan and refused folders and read the data folder's vulkan-layer folder (<see cref="FlatpakGrant.VulkanEntries"/>).</summary>
    static bool FlatpakSees(string app, string grant, bool vulkan) =>
        FlatpakGrant.Visible(app, grant) && (!vulkan || FlatpakGrant.VulkanEntries(grant).All(e => e.Write ? FlatpakGrant.Writable(app, e.Folder) : FlatpakGrant.Visible(app, e.Folder)));

    public string? FlatpakGrantLauncher(string gameId) =>
        Games.FirstOrDefault(g => g.Game.Id == gameId) is { } s && FlatpakGrant.AppOf(s.Game) is { } a
        && (s.RecorderSkip == FlatpakSkipLine(a.Launcher) || s.RecorderSkip == FlatpakVulkanSkipLine(a.Launcher)) ? a.Launcher : null;

    /// <summary>The grant the game needs lets its launcher write in the recorder folder too: it records on Vulkan.</summary>
    public bool FlatpakGrantWrites(string gameId) =>
        Games.FirstOrDefault(g => g.Game.Id == gameId) is { } s && FlatpakGrant.AppOf(s.Game) is { } a && s.RecorderSkip == FlatpakVulkanSkipLine(a.Launcher);

    public string? GrantFlatpak(string gameId)
    {
        var s = Find(gameId);
        if (FlatpakGrant.AppOf(s.Game) is not { } a) return $"{s.Game.Name} isn't a game of a Flatpak launcher";
        var vulkan = s.Engine is { } e && VulkanPlan.IsVulkan(e);
        if (FlatpakGrant.Grant(a.App, GrantFolder(Store.DataDir), vulkan, m => RecorderLog(m)) is { } why) return why;
        RecorderLog($"{s.Game.Name}: {a.Launcher}'s Flatpak ({a.App}) may read the recorder folder" + (vulkan ? ", write Vulkan recordings in it and read the Vulkan recorder's folder" : ""));
        ReconcileRecorders(gameId);
        return null;
    }

    static partial void SettleRecorderFolder(string dataDir, Action<string> log)
    {
        if (!MovesRecorder(dataDir)) return;
        var data = Path.GetFullPath(dataDir);
        var to = Path.Combine(data, RecorderFolderName);
        static bool There(string p) => Path.Exists(p) || new FileInfo(p).LinkTarget != null;
        string key = Path.Combine(data, "recorder.key"), toKey = Path.Combine(to, "recorder.key");
        string revoked = Path.Combine(data, "recorder.revoked"), toRevoked = Path.Combine(to, "recorder.revoked");
        string[] folders = ["refused", "vulkan", "stamps", "arms"];
        if (!There(key) && !(There(toKey) && folders.Any(f => There(Path.Combine(data, f)) && !There(Path.Combine(to, f))))) return;
        using var mutex = NamedMutex.Open(RevokedMutex);   // a Revoke of another SCSKiller process
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) { log($"the recorder's files stay in {data}: another SCSKiller process holds the revocation list"); return; }
        try
        {
            try
            {
                if (new DirectoryInfo(to).LinkTarget != null) throw new IOException($"{to} is a link");
                if (There(key) && There(toKey))
                {
                    Union(revoked, toRevoked);
                    Union(toRevoked, revoked);
                    log($"recorder.key is in both {data} and {to}: the one in {data} stays in use");
                    return;
                }
                if (There(key))
                {
                    Directory.CreateDirectory(to);
                    if (Libc.Stat(data) is not { } d || Libc.Stat(to) is not { } t || (d.DevMajor, d.DevMinor) != (t.DevMajor, t.DevMinor))
                        throw new IOException($"{to} isn't on the data folder's file system");
                    Union(toRevoked, revoked);
                    File.Move(key, toKey);
                    log($"recorder.key and the revocation list moved into {to}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"the recorder's files stay in {data}: {e.Message}");
                return;
            }
            var moved = new List<string>();
            foreach (var name in folders)
            {
                string from = Path.Combine(data, name), into = Path.Combine(to, name);
                if (!There(from) || There(into)) continue;
                try
                {
                    if (Directory.Exists(from) && new DirectoryInfo(from).LinkTarget == null) Directory.Move(from, into);
                    else File.Move(from, into);
                    moved.Add(name);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"{name} stays in {data} until the next start: {e.Message}"); }
            }
            if (moved.Count > 0) log($"{string.Join(", ", moved)} moved into {to}");
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>Adds the lines of <paramref name="from"/> missing in <paramref name="into"/>, written whole by a rename: a
    /// revocation list only grows here.</summary>
    static void Union(string into, string from)
    {
        if (!File.Exists(from)) return;
        var have = File.Exists(into) ? File.ReadAllLines(into).Where(l => l.Length > 0).ToList() : [];
        var add = File.ReadAllLines(from).Where(l => l.Length > 0).Except(have).ToList();
        if (add.Count == 0) return;
        var temp = $"{into}.{Guid.NewGuid():N}";
        File.WriteAllLines(temp, have.Concat(add));
        File.Move(temp, into, overwrite: true);
    }

    static partial void RemoveFlatpakGrants(string dataDir, Action<string> log) => FlatpakGrant.Remove(GrantFolder(dataDir), log);

    partial void RemoveUnusedFlatpakGrants()
    {
        // only by a scan that finished and read every source this run: before it, or after a failed one, fewer games are listed than are armed
        if (!Volatile.Read(ref _wholeScan) || !ListWhole()) return;
        // a game this scan didn't list (a card not mounted, a library gone) may still hold its recorder
        if (UninstalledRecords(Games).Any(r => r.Rec.RecorderExe != null)) return;
        // a record's recorder counts too: an unchecked game's state doesn't show the recorder its folder still holds
        var armed = Games.Where(s => s.RecorderInstalled).Select(s => s.Game)
            .Concat(Games.Select(s => s.Game).Concat(HiddenGames).Where(g => Store.LoadGame(g.Id).RecorderExe != null))
            .Where(LauncherPrefix.Is).DistinctBy(g => g.Id).ToList();
        var apps = armed.Select(g => FlatpakGrant.AppOf(g)?.App).ToList();
        // a launcher whose files can't be read now may be a Flatpak with an armed game: nothing is taken out this time
        if (apps.Any(a => a == null) && armed.Any(g => LauncherPrefix.Of(g, out _) == null)) return;
        FlatpakGrant.Remove(GrantFolder(Store.DataDir), m => RecorderLog(m), keep: apps.OfType<string>().ToHashSet(StringComparer.Ordinal));
    }

    partial void CarriesOurToken(int pid, ref bool ours) => ours = WarmJob.Current.In(Store.DataDir).Contains(pid);

    partial void ExitedNow(int pid, ref bool exited) => exited = OperatingSystem.IsLinux() && !ProcessTree.Lives(pid);   // macOS has no /proc

    // a staged Wine process inherits its warm's Unix environment, so the job token (a run's own) pairs them
    partial void StagingWarm(int staged, List<(int Pid, int Parent, string Exe)> all, ref int pid)
    {
        if (!OperatingSystem.IsLinux() || !ProcessTree.IsWine(staged)) return;   // macOS has no /proc
        var token = ProcessTree.StartEnvironment(staged, WarmJob.Variable);
        pid = token is { Length: > 0 } && all.FirstOrDefault(w => w.Exe.Equals("scskiller_warm.exe", GameFiles.Names) && ProcessTree.IsWine(w.Pid)
            && ProcessTree.StartEnvironment(w.Pid, WarmJob.Variable) == token) is { Pid: > 0 } warm ? warm.Pid : 0;
    }

    // Proton's z: is the Linux root in every prefix: the one drive that names a path the same whatever the game's prefix
    static string WinePath(string full) => "Z:" + full.Replace('/', '\\');

    private static partial string LedgerText(string ledgerDir) => WinePath(Path.GetFullPath(ledgerDir));

    // only ".." steps climb Wine's view of the exe's folder (its drive may be s:, the library) as they climb the Linux one
    private static partial string RootIni(string here, string install) =>
        Path.GetRelativePath(here, install) is var up && (up == "." || up.Split('/').All(s => s == "..")) ? up.Replace('/', '\\') : WinePath(install);

    private static partial string RootFolder(string here, string root) =>
        root.StartsWith(@"Z:\", StringComparison.Ordinal) ? root[2..].Replace('\\', '/') : Path.GetFullPath(Path.Combine(here, root.Replace('\\', '/')));

    // a '\' in a name doesn't come back through Wine's path: Wine reads it as a separator, so another folder would be keyed
    private static partial bool RootNames(string here, string root, string install) => RootFolder(here, root) == install;

    // 0600, as %LOCALAPPDATA%'s ACL keeps it on Windows: whoever reads it can sign an ini the recorder records under
    internal static partial void WriteKey(string temp, string text)
    {
        using var f = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = Libc.OwnerReadWrite });
        f.Write(System.Text.Encoding.ASCII.GetBytes(text));
    }

    // a symlink in its place is refused, and a key others can read is made 0600
    internal static partial byte[] ReadKey(string file)
    {
        using var h = Libc.OpenOwnerOnly(file) ?? throw new IOException($"{file} isn't a file");
        using var f = new FileStream(h, FileAccess.Read);
        using var bytes = new MemoryStream();
        f.CopyTo(bytes);
        return bytes.ToArray();
    }

    partial void LinuxOf(Game game, ref LinuxSystem? linux) => linux = new(LinuxSystem.ThisKernel, ProtonContext.LastVersion(game, (_warmer as Warmer)?.SteamRoot));

    // DriveInfo types a mount by its file system's name alone: a USB stick's ext4 is Fixed, tmpfs Ram, ntfs-3g Network
    private static partial bool Linked(string path) => Libc.Links(path) is var (symlink, links) && (symlink || links > 1);

    private static partial bool OnFixedDrive(string root, string apps, string exeDir) => Mounts.Fixed([root, apps, exeDir], Mounts.Of, Mounts.Removable);

    [ThreadStatic] static int? _ioPriorityBefore;

    private static partial bool BackgroundBegin()
    {
        if (Libc.ThreadIoPriority() is not { } was || was == Libc.IoPriorityLowest || !Libc.SetThreadIoPriority(Libc.IoPriorityLowest)) return false;
        _ioPriorityBefore = was;
        return true;
    }

    private static partial void BackgroundEnd()
    {
        if (_ioPriorityBefore is { } was) Libc.SetThreadIoPriority(was);
        _ioPriorityBefore = null;
    }

    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle? OpenToDelete(string path, string dir)
    {
        try { return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException e) { throw new IOException($"couldn't open the {LedgerStamp} in {dir}", e); }
    }

    // unlink takes a path: the file goes only while its path still names the open one
    private static partial void DeleteOpen(Microsoft.Win32.SafeHandles.SafeFileHandle h, string path, string dir)
    {
        if (Libc.Stat(h) is not { } open || Libc.Stat(path) is not { } now
            || (open.DevMajor, open.DevMinor, open.Inode) != (now.DevMajor, now.DevMinor, now.Inode)) return;
        try { File.Delete(path); }
        catch (UnauthorizedAccessException e) { throw new IOException($"couldn't delete the {LedgerStamp} in {dir}", e); }
    }

    // the record's start time is /proc's (ProcessTree.Created), as the proxy's pid_time= on Linux
    // macOS has no /proc: a session copied from another system is left untouched
    private static partial bool Alive(OfflineSession s) => !OperatingSystem.IsLinux() || s.Pid != 0 && ProcessTree.Runs(s.Pid, s.Started);

    // no logon entry: a start that fails is cleaned up in the app, and what a crash left goes at the next start's pass
    private static partial void RunOnce(string gameId, string? exe) { }

    // macOS: no store launch a session binds to yet
    static partial void NoRiskSessions(ref string? why)
    {
        if (OperatingSystem.IsMacOS()) why = RiskOnMac;
    }

    internal const string RiskOnMac = "recording anti-cheat games isn't available on macOS yet";

    // /proc/<pid> is owned by the process's user
    static bool Mine(int pid) => Libc.Lstat($"/proc/{pid}")?.Uid == Libc.UserId;

    /// <summary>A process a risk session binds is a launch of the game's app by Steam: an ancestor of it is Steam's reaper for
    /// that app (<see cref="SteamSource.LaunchingApp"/>, a non-Steam game's app id too), as the game under Proton is. Each
    /// ancestor started no later than the one below it, and the reaper's command line is read between two reads of its start
    /// time: a pid reused on the way names no ancestor. The process and the reaper are this user's.</summary>
    partial void SteamLaunched(Game g, int pid, long started, List<(int Pid, int Parent, string Exe)> all, ref bool yes)
    {
        if ((SteamSource.AppId(g.Id) ?? SteamShortcuts.AppId(g.Id)) is not { } app || !Mine(pid)) return;
        var parents = all.GroupBy(p => p.Pid).ToDictionary(p => p.Key, p => p.First());
        var below = started;
        for (var (at, steps) = (pid, 0); steps < 64 && parents.TryGetValue(at, out var p) && p.Parent > 1; steps++)
        {
            at = p.Parent;
            if (ProcessTree.Created(at) is not { } up || up > below) return;
            below = up;
            if (parents.TryGetValue(at, out var parent) && parent.Exe == "reaper" && Mine(at) && ProcessTree.CommandLine(at) is { } argv
                && SteamSource.LaunchingApp(argv) == app && ProcessTree.Created(at) == up)
            {
                yes = true;
                return;
            }
        }
    }

    partial void ApplyRewarmService()
    {
        var program = LinuxStartup.ThisProgram(out var refused);
        try
        {
            // Right away is the app's, as Ask is: a Flatpak needs no background service for it
            RewarmService.Apply(Settings.DriverRightAway && Settings.OnDriverUpdate == DriverUpdateMode.WhenIdle ? DriverUpdateMode.Ask : Settings.OnDriverUpdate, LinuxStartup.InFlatpak, program, refused, program == null ? null : RewarmService.ThisCliRefused(program),
                RewarmService.UnitDir(Environment.GetEnvironmentVariable), new RewarmService.Systemctl());
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log?.Report($"Driver-update check: {e.Message}");
            throw;
        }
    }

    private static partial TimeSpan UserIdleTime() => SessionIdle.Now();

    private static partial string? CpuModel() => CpuModel("/proc/cpuinfo");

    private static partial int? SmartAppControlState() => null;

    /// <summary>The first processor's model name in <paramref name="cpuinfo"/>, laid out as /proc/cpuinfo (the CPU's brand
    /// string, as Windows shows it); null when it has none (ARM's) or can't be read.</summary>
    internal static string? CpuModel(string cpuinfo)
    {
        try
        {
            foreach (var line in File.ReadLines(cpuinfo))
                if (line.Split(':', 2) is [var name, var value] && name.Trim() == "model name") return CpuName(value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static partial string? ThisAppProgram() => AppImage.Program(Environment.ProcessPath, Environment.GetEnvironmentVariable, Libc.RealPath);

    // a child would stay in the service's cgroup, which systemd kills once the CLI exits: the app runs as a unit of its own
    public static partial string? StartApp(params string[] args)
    {
        if (DriverUpdatedApp(Environment.ProcessPath, AppContext.BaseDirectory, Environment.GetEnvironmentVariable, File.Exists) is not { } app) return null;
        var psi = new ProcessStartInfo("systemd-run") { UseShellExecute = false };   // its errors go to the CLI's output
        // the unit gets the user manager's environment: the data folder and test switches go along, as a child inherits them on Windows
        var carried = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("SCSKILLER_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(k => $"--setenv={k}={(k == "SCSKILLER_DATA_DIR" ? AppStore.Moved : Environment.GetEnvironmentVariable(k))}");   // the unit runs in the home folder: the data folder as this process resolved it
        foreach (var a in new[] { "--user", "--collect", "--quiet" }.Concat(carried).Concat(["--", app, .. args])) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            if (p.WaitForExit(TimeSpan.FromSeconds(30))) return p.ExitCode == 0 ? app : null;
            p.Kill();
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }   // no systemd-run
    }

    /// <summary>This copy's app for the CLI: the AppImage the CLI runs from (its file, never its mount), else SCSKiller.Ui beside it.</summary>
    internal static string? DriverUpdatedApp(string? processPath, string cliDir, Func<string, string?> env, Func<string, bool> exists) =>
        AppImage.Image(processPath, env) ?? (Path.Combine(cliDir, "SCSKiller.Ui") is var ui && exists(ui) ? ui : null);

    // Steam's filter buckets the pipelines vkd3d-proton and DXVK make under these engine names, which Fossilize's replayer
    // sets its device up for as they do (cli/device.cpp, application_info_promote_robustness2)
    partial void SteamDxEngine(EngineInfo e, ref string? name) =>
        name = VulkanPlan.IsVulkan(e) || Vendor.Caps.VulkanKey == null ? null : e.GraphicsApi.Contains("D3D12") ? "vkd3d" : e.GraphicsApi.StartsWith("D3D11", StringComparison.Ordinal) ? "DXVK" : null;

    /// <summary>The bucket of the game's Steam archives this GPU's vkd3d-proton or DXVK records into
    /// (<see cref="SteamPipelineCache.Choose"/>): Valve's download, and this PC's play only while SCSKiller has no recording
    /// of its own, which holds that play already.</summary>
    partial void SteamDxSources(Game g, EngineInfo engine, ref List<FozSource>? sources)
    {
        string? name = null;
        SteamDxEngine(engine, ref name);
        if (name == null || SteamCache(g) is not var (dir, root)
            || SteamPipelineCache.Choose(SteamPipelineCache.Buckets(dir, Log).Where(b => b.Hash != null), (uint)Vendor.Gpu.Vendor, FossilizeFilter.Read(root), name) is not { } bucket)
            return;
        var list = SteamPipelineCache.Sources(File.Exists(RecordingPath(g.Id)) ? bucket with { Recorded = [] } : bucket, null);
        if (list.Count > 0) sources = list;
    }
}
