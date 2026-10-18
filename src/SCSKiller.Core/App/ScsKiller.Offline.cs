using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

// Offline sessions without EasyAntiCheat (Games.OfflineEac) and risk sessions beside a game's anti-cheat (StartRiskSession,
// Settings.RecordAntiCheatGames): the only cases where the recorder goes into an anti-cheat game. The signed scskiller.ini
// names one process (pid= and pid_time=; a risk session's carries risk=1 too, inside the signature), so any other launch is
// a pass-through while the files are there. SCSKiller starts the exe itself, suspended, where it can, and only then places
// the recorder; a game its store's launcher starts gets an unsigned ini first and is signed for its exe's first process
// once it runs. A leftover recorder dll would still be loaded into a normal anti-cheat launch, so the session is recorded
// (GameRecord.OfflineSession) before any file exists, and its cleanup (CleanOfflineSession) deletes each file that is still
// the one it placed (by hash) when the process exits: in the app, in a helper process that outlives it (CleanupArg), at the
// next logon (RunOnce) and at every scan and watcher pass, until none of them is left.
public sealed partial class ScsKiller
{
    public const string SteamAppIdFile = "steam_appid.txt";
    /// <summary>SCSKiller.exe's argument for the cleanup helper, followed by the game id (<see cref="RunOfflineCleanup"/>).</summary>
    public const string CleanupArg = "--offline-cleanup";
    public const string SteamFirst = "Start Steam first (offline mode is fine).";
    const string TempSuffix = ".scskiller-new";

    // game id -> the process a session started, while it runs: nothing takes its recorder out (it admits no other process);
    // an invalid handle while a risk session waits for its store's launch to start the exe
    readonly ConcurrentDictionary<string, SafeProcessHandle> _offline = new();
    // game id -> SessionNote, until its next session starts or its switch or the setting is turned off
    readonly ConcurrentDictionary<string, string> _sessionNotes = new();
    public const string UnboundSessionNote = "This launch started before SCSKiller could attach: nothing was recorded. Try again.";
    public const string AntiCheatSessionNote = "The recorder didn't load in this session. This game contains anti-cheat. Please report it so we can check.";

    /// <summary>The game page's note for a risk or offline session the proxy admitted none of, from its refusal during the session (null:
    /// it wrote none). An unstamped ini's refusal is the store's game reaching its first device before <see cref="BindRisk"/>
    /// signed it: the proxy decides once per process.</summary>
    internal static string SessionNote(string? refused, string neverSaw = NeverSawNote) => refused switch
    {
        null => neverSaw,
        _ when Unstamped(refused) || refused == "scskiller.ini changed while deciding" => UnboundSessionNote,
        _ => $"The last launch wasn't recorded: {refused}.",
    };
    // game id -> a session whose files may still be in the game folder; null until the data folder was read once
    ConcurrentDictionary<string, bool>? _offlineLeft;

    /// <summary>A session's process runs: this app's, or one an earlier run started (its pid and creation time recorded),
    /// which is still loading when SCSKiller opens again. Its files are never taken out meanwhile. Reads a record only for a
    /// game with a session pending (the first scan or watcher pass finds them): every removal asks, and a record may be held.</summary>
    bool OfflineLive(string gameId) =>
        _offline.ContainsKey(gameId) || _offlineLeft?.ContainsKey(gameId) == true && Store.LoadGame(gameId).OfflineSession is { Resumed: true } s && Alive(s);

    // updates applying (Updater) and offline sessions starting: one excludes the other
    readonly object _updateGate = new();
    int _updating, _offlineStarting;
    public const string UpdateInstalling = "An update is being installed. Try again after it.";

    /// <summary>An update's apply stops every process under the install root, an offline session's cleanup helper too: not
    /// while a session starts, its process runs or its helper does. A session whose files wait for a drive that's gone
    /// doesn't hold updates back.</summary>
    public bool OfflineBlocksUpdate => _offlineStarting > 0 || !_offline.IsEmpty
        || OfflineLeft().Keys.Any(id => HelperRuns(id) || Store.LoadGame(id).OfflineSession is { } s && Alive(s));

    /// <summary>An update's apply begins: no offline session starts until <see cref="EndUpdate"/>. False (nothing begun) while
    /// one blocks it.</summary>
    public bool BeginUpdate()
    {
        lock (_updateGate)
        {
            if (OfflineBlocksUpdate) return false;
            _updating++;
            return true;
        }
    }

    public void EndUpdate()
    {
        lock (_updateGate) _updating--;
    }

    /// <summary>The cleanup helper of the game runs: it holds this mutex for its whole run.</summary>
    static string HelperMutex(string gameId) => @"Local\SCSKiller-offline-cleanup-" + gameId.Replace('\\', '_');

    static bool HelperRuns(string gameId)
    {
        if (!Mutex.TryOpenExisting(HelperMutex(gameId), out var m)) return false;
        m.Dispose();
        return true;
    }

    /// <summary>Writes the logon entry (tests: none).</summary>
    internal Action<string, string> AddRunOnce { get; set; } = (gameId, exe) => RunOnce(gameId, exe);

    /// <summary>What the session's exe is started with (tests).</summary>
    internal string OfflineArguments { get; set; } = "";

    /// <summary>What a risk session of a store's game runs to start it (tests).</summary>
    internal Func<Game, ProcessStartInfo?> LaunchCommand { get; set; } = g => StoreLaunch.Command(g);

    /// <summary>The exe that runs <see cref="CleanupArg"/> (the app's own); null: no helper and no logon entry (the CLI, tests).</summary>
    public string? CleanupHelper { get; set; }

    /// <summary>Whether a process other than the session's runs from its folders, so its data files wait. Replaceable for tests.</summary>
    internal Func<OfflineSession, bool> OfflineRuns { get; set; }

    /// <summary>A process of <paramref name="all"/> is named like an exe of the session's folders, other than the session's
    /// own (one that exited, still held open).</summary>
    internal static bool OthersRun(OfflineSession s, List<(int Pid, int Parent, string Exe)> all) =>
        ExeNamesIn([s.InstallDir, Path.GetDirectoryName(s.Exe)]) is not { } names   // can't tell: as if one runs
        || all.Any(p => p.Pid != s.Pid && names.Contains(Path.GetFileNameWithoutExtension(p.Exe)));

    /// <summary><see cref="OfflineEac.Of"/>, never for a game added by hand before its folder is confirmed (as the recorder); an
    /// unsupported engine only while Settings.RecordAntiCheatGames is on.</summary>
    OfflineEacGame? OfflineEntry(Game g, AntiCheat antiCheat, EngineInfo? engine) =>
        Unconfirmed(g) || RecorderCrashes(g) ? null : OfflineEac.Of(g, antiCheat, engine, unsupported: Settings.RecordAntiCheatGames);

    public void SetOfflineRecording(string gameId, bool on)
    {
        lock (_recorderLock)
        {
            var s = Find(gameId);
            if (on && !s.OfflineEligible && !s.RiskEligible) throw new InvalidOperationException($"{s.Game.Name}: recording while you play isn't available for this game");
            var rec = Store.LoadGame(gameId);
            rec.OfflineRecord = on;
            Store.SaveGame(gameId, rec);
            if (!on) _sessionNotes.TryRemove(gameId, out _);
        }
        RefreshGame(gameId);
    }

    /// <summary>The anti-cheat as the game page, the confirmation and the recorder log name it.</summary>
    public static string AntiCheatName(AntiCheat a) => a switch
    {
        AntiCheat.EasyAntiCheat => "EasyAntiCheat",
        AntiCheat.BattlEye => "BattlEye",
        _ => "anti-cheat",
    };

    /// <summary><see cref="AntiCheatName(AntiCheat)"/>; "VAC" for a game whose only anti-cheat is VAC.</summary>
    public static string AntiCheatName(GameState s) => AntiCheatName(s.AntiCheat, s.Vac);

    static string AntiCheatName(AntiCheat a, bool vac) => a == AntiCheat.None && vac ? "VAC" : AntiCheatName(a);

    /// <summary><see cref="GameState.RiskEligible"/>, OfflineEligible aside.</summary>
    bool RiskOffered(GameState s) => RiskOffered(s.Game, s.Engine, s.AnyAntiCheat);

    bool RiskOffered(Game g, EngineInfo? engine, bool anyAntiCheat) => RiskOfferedButLaunch(g, engine, anyAntiCheat) && StoreLaunches(g);

    /// <summary>Whether the game's store has a launch a risk session can start (<see cref="StoreLaunch.Supported"/>). Replaceable for tests.</summary>
    internal Func<Game, bool> StoreLaunches { get; set; } = StoreLaunch.Supported;

    bool RiskOfferedButLaunch(Game g, EngineInfo? engine, bool anyAntiCheat) =>
        Settings.RecordAntiCheatGames && anyAntiCheat && !Unconfirmed(g) && !RecorderCrashes(g)
        && engine is { } e && (e.GraphicsApi.Contains("D3D12") || Records11Api(e, Vendor.Caps)) && GameVerdicts.Current.Unsupported(g, engine) == null;

    /// <summary>The recorder a risk or offline session places: d3d11.dll for a DirectX 11 game whose D3D11 recording records
    /// (<see cref="Records11Api"/>, whatever the engine's support), and on such a GPU for an Unreal 4 game that may run on either
    /// (UE4 defaults to DirectX 11); d3d12.dll otherwise. One name only: two copies in one process would each append to the same
    /// scskiller.db and log.</summary>
    internal static string RiskDll(EngineInfo? e, VendorCaps caps) =>
        Records11Api(e, caps) || e is { Family: "Unreal" } && ApiKey(e.GraphicsApi) == "11+12" && e.Version.StartsWith("4.") && !Planner.D3D11CoversVs(caps) ? Dll11 : Dll12;

    /// <summary>An offline session's note when its recorder never loaded: for a game that may run on either DirectX, the one it
    /// didn't place.</summary>
    static string OfflineNeverSaw(EngineInfo? e, string dll) => ApiKey(e?.GraphicsApi) != "11+12" ? NeverSawNote : dll == Dll12 ? NeverSawDx11Note : NeverSawDx12Note;

    /// <summary>Settings.RecordAntiCheatGames turned off: every game's switch off but the offline EasyAntiCheat games', and what
    /// sessions left goes (a running one's at its end).</summary>
    void RiskOff()
    {
        _sessionNotes.Clear();
        lock (_recorderLock)
            foreach (var s in Games.Where(s => OfflineEac.Of(s.Game, s.AntiCheat, s.Engine) == null))
                try
                {
                    var rec = Store.LoadGame(s.Game.Id);
                    if (!rec.OfflineRecord) continue;
                    rec.OfflineRecord = false;
                    Store.SaveGame(s.Game.Id, rec);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{s.Game.Name}: couldn't turn off Record while I play: {e.Message}"); }
        CleanOfflineSessions();
    }

    public Task<AntiCheat> CheckAntiCheatAsync(string gameId)
    {
        var g = Find(gameId).Game;
        return Task.Run(() => GameFiles.DetectAntiCheat(g));
    }

    public Task StartRiskSession(string gameId, bool confirmed, AntiCheat shown)
    {
        var s = Find(gameId);
        var g = s.Game;
        if (!confirmed) throw new InvalidOperationException($"{g.Name}: a session starts only after you confirm it");
        lock (_updateGate)
        {
            if (_updating > 0) throw new InvalidOperationException(UpdateInstalling);
            _offlineStarting++;
        }
        try { return StartRisk(s, g, gameId, shown); }
        finally
        {
            lock (_updateGate) _offlineStarting--;
        }
    }

    Task StartRisk(GameState s, Game g, string gameId, AntiCheat shown)
    {
        Task task;
        lock (_recorderLock)
        {
            var rec = Store.LoadGame(gameId);
            var dir = Path.GetDirectoryName(g.ExePath)!;
            var manual = g.Store == Core.Store.Manual;
            var dll = RiskDll(s.Engine, Vendor.Caps);
            var neverSaw = AntiCheatSessionNote;
            string[] placed = [.. DefaultCopy ? [] : new[] { LedgerStamp }, dll, "scskiller.ini"];
            var stamp = Stamp(g.ExePath);   // before the fresh check below
            ProcessStartInfo? launch = null;
            AntiCheat found = AntiCheat.None;
            bool vac = false;
            string? Refusal()
            {
                if (!Settings.RecordAntiCheatGames) return "allow recording in games with anti-cheat in Settings first";
                if (!s.RiskEligible) return "recording while you play isn't available for this game";
                if (!rec.OfflineRecord) return "turn on Record while I play for this game first";
                if (OfflineLive(gameId) || rec.OfflineSession != null) return "the last session's files are still being removed";
                if (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null || rec.RecorderRollback)
                    return "a recorder's removal is still pending";
                lock (_lock)
                    if (_current == gameId) return "a compile of it is in progress";
                if (GameRunning(g)) return "it is running: close the game first";
                if (_proxyDll == null || !File.Exists(_proxyDll)) return $"the recorder ({dll}) wasn't found next to SCSKiller";
                if (stamp == null) return "its exe can't be read";
                // fresh, not the scan's: what the confirmation named is what is there
                (found, vac) = (GameFiles.DetectAntiCheat(g), VacCheck(g));
                if (found != shown) return $"{AntiCheatName(found, vac)} was found, not {AntiCheatName(shown, vac)}: confirm again";
                if (!manual && (launch = LaunchCommand(g)) == null) return "its store has no launch SCSKiller can start";
                // a mod is left as it is: only one holding a name the session places refuses
                if (RecorderOwnFiles.Concat(placed).FirstOrDefault(f => Path.Exists(Path.Combine(dir, f))) is { } there) return $"{there} is already in its folder";
                if (ReShade.Detect(g) is { ProxyLibrary: true }) return "ReShade chains a proxy library ([PROXY])";
                return null;
            }
            if (Refusal() is { } why) throw new InvalidOperationException($"{g.Name}: no session: {why}");
            _sessionNotes.TryRemove(gameId, out _);
            var since = DateTime.UtcNow;
            // the journal first: whatever happens after, every cleanup knows the folder to look at
            var session = new OfflineSession(gameId, g.ExePath, g.InstallDir, Created: [.. placed.Select(f => f + TempSuffix)], Risk: true);
            (rec.RecorderExe, rec.RecorderInstallDir, rec.OfflineSession) = (g.ExePath, g.InstallDir, session);
            Store.SaveGame(gameId, rec);
            OfflineLeft()[gameId] = true;
            RecorderLog($"{g.Name}: session in a game with {AntiCheatName(found, vac)}, at the user's own risk: recorder in as {dll} ({dir})");
            // every hash final before its file exists: the cleanup deletes a placed name only while it holds these bytes (the ini
            // by its folder's entry). A store's launch gets an ini without a stamp, refused until BindRisk signs it.
            void Publish(string ini, Action<GameRecord>? also = null)
            {
                var now = Store.LoadGame(gameId);
                if (now.OfflineSession == null) throw new InvalidOperationException("its session was cleaned up meanwhile");
                now.RecorderFiles = new() { [dll] = ProxySha(dll)! };
                SetIni(Store, dir, _ => new IniOwner(dir, Hex(ini), Install: g.InstallDir));
                also?.Invoke(now);
                Store.SaveGame(gameId, now);
                if (!DefaultCopy) Place(Path.Combine(dir, LedgerStamp), temp => WriteNew(temp, Path.GetFullPath(LedgerDir)));   // first: this copy's
                Place(Path.Combine(dir, dll), temp => File.Copy(_proxyDll!, temp));
                Place(Path.Combine(dir, "scskiller.ini"), temp => WriteNew(temp, ini));
            }
            SafeProcessHandle? process = null;
            try
            {
                // recovery before any file is published: the logon entry, then the helper, which waits for the session's
                // process (the record's pid) while this app sets it up
                if (CleanupHelper != null)
                {
                    AddRunOnce(gameId, CleanupHelper);
                    using var helper = Process.Start(new ProcessStartInfo(CleanupHelper, [CleanupArg, gameId]) { UseShellExecute = false, CreateNoWindow = true })
                        ?? throw new InvalidOperationException("its cleanup helper didn't start");
                }
                if (manual)
                    process = StartAttested(g.ExePath, OfflineArguments, (h, pid, started) =>
                    {
                        _offline[gameId] = h;
                        Publish(IniText(g, rec, Signed(g.ExePath, RootValue(g), stamp!, BoundTo(pid, started) + RiskLine + SignedLines(g, rec, session: true, riskSession: true))),
                            now => now.OfflineSession = session = session with { Pid = pid, Started = started });
                    }, () =>
                    {
                        var now = Store.LoadGame(gameId);
                        now.OfflineSession = session = session with { Resumed = true };
                        Store.SaveGame(gameId, now);
                    });
                else
                {
                    _offline[gameId] = new SafeProcessHandle(0, false);   // until it is bound: no pass takes out or re-signs its files
                    Publish(IniText(g, rec));
                    Process.Start(launch!)?.Dispose();
                }
            }
            catch (Exception e)
            {
                _offline.TryRemove(gameId, out _);
                CleanOffline(gameId);
                throw new InvalidOperationException($"{g.Name}: the session didn't start: {e.Message}", e);
            }
            if (process != null)
            {
                RecorderLog($"{g.Name}: session started ({g.ExePath}, process {session.Pid}): only that process records");
                task = Task.Run(() => EndOffline(g, process!, since, true, neverSaw));
            }
            else
            {
                RecorderLog($"{g.Name}: session started through its store: it records the first {Path.GetFileName(g.ExePath)} that runs from {dir}");
                task = Task.Run(() => BindRisk(g, stamp!, since, neverSaw));   // before the evaluation below: the store's game may reach its first device meanwhile
            }
        }
        Refresh(g);
        return task;
    }

    /// <summary>The stamp line of a session at the user's own risk, after its binding (<see cref="BoundTo"/>), inside the signature.</summary>
    internal const string RiskLine = "risk=1\r\n";

    /// <summary>A store-launched risk session: the first process of the game's exe is bound (its pid and creation time, with
    /// risk=1, signed over the <paramref name="stamp"/> taken before the session's check), then left to run; nothing binds
    /// within 5 minutes: the session ends.</summary>
    async Task BindRisk(Game g, string stamp, DateTime since, string neverSaw)
    {
        SafeProcessHandle? process = null;
        int pid = 0;
        try
        {
            for (var clock = Stopwatch.StartNew(); (process = RunningExe(g, out pid)) == null; await Task.Delay(50))
                if (clock.Elapsed > TimeSpan.FromMinutes(5)) throw new InvalidOperationException("the game didn't start within 5 minutes");
            if (!GetProcessTimes(process, out var created, out _, out _, out _)) throw new Win32Exception();
            lock (_recorderLock)
            {
                // the cleanup's gate (CleanOfflineSession), the lock's order as CleanOffline's: a helper cleaning up a game that
                // exited at once never runs between the binding and its signed ini, which would stay unjournaled
                using var gate = new AppStore.PathGate(Path.Combine(Store.GameDir(g.Id), "offline"));
                var now = Store.LoadGame(g.Id);
                if (now.OfflineSession == null) throw new InvalidOperationException("its session was cleaned up meanwhile");
                // resumed: the launcher's process, never a suspended child of ours that a cleanup may end
                now.OfflineSession = now.OfflineSession with { Pid = pid, Started = created, Resumed = true };
                Store.SaveGame(g.Id, now);
                _offline[g.Id] = process;
                var dir = Path.GetDirectoryName(g.ExePath)!;
                var (state, judged) = JudgeIni(dir, Store, null, g.InstallDir);
                var ini = IniText(g, now, Signed(g.ExePath, RootValue(g), stamp, BoundTo(pid, created) + RiskLine + SignedLines(g, now, session: true, riskSession: true)));
                if (state != IniState.Ours || !WriteIni(dir, ini, judged, null, g.InstallDir, () => true, null))
                    throw new InvalidOperationException("its scskiller.ini changed meanwhile");
            }
        }
        catch (Exception e)
        {
            _offline.TryRemove(g.Id, out _);
            process?.Dispose();
            CleanOffline(g.Id);
            Refresh(g);
            throw new InvalidOperationException($"{g.Name}: the session didn't start: {e.Message}", e);
        }
        RecorderLog($"{g.Name}: session bound to process {pid} ({g.ExePath}): only that process records");
        Refresh(g);
        await EndOffline(g, process!, since, true, neverSaw);
    }

    /// <summary>A process of the game's exe, by its path, opened; null when none runs.</summary>
    SafeProcessHandle? RunningExe(Game g, out int pid)
    {
        var name = Path.GetFileName(g.ExePath);
        // the image path names the folder resolved: an install reached through a junction spells it otherwise (as Outside)
        var real = GameFiles.FinalPath(Path.GetDirectoryName(g.ExePath)!) is { } r ? Path.Combine(r, name) : null;
        bool Ours(int pid) => ProcessPath(pid) is { } path
            && (path.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase) || real != null && path.Equals(real, StringComparison.OrdinalIgnoreCase));
        // by path first, without opening it: another game's process of the same exe name is never opened under its anti-cheat
        foreach (var p in Processes(true).Where(p => p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase) && Ours(p.Pid)))
        {
            var h = OpenProcess(Synchronize | QueryLimitedInformation, false, p.Pid);
            // the open handle keeps the pid from being reused: the path read now is that process's
            if (!h.IsInvalid && Ours(p.Pid))
            {
                pid = p.Pid;
                return h;
            }
            h.Dispose();
        }
        pid = 0;
        return null;
    }

    internal const string RiskLogLine = "risk=1: recording at the user's own risk";

    /// <summary>The proxy admitted a process of the session: it opens scskiller.log only once it admits one, and under risk=1
    /// its first line there is <see cref="RiskLogLine"/>; the log is in the game folder, or in the data folder once the
    /// session's cleanup moved it (newer than the session's start). Unreadable: taken as admitted.</summary>
    bool Admitted(Game g, DateTime since, bool risk)
    {
        foreach (var dir in new[] { Path.GetDirectoryName(g.ExePath)!, OfflineSessionDir(Store, g.Id) })
            try
            {
                var log = new FileInfo(Path.Combine(dir, "scskiller.log"));
                if (!log.Exists || log.LastWriteTimeUtc < since) continue;
                using var r = new StreamReader(new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                if (!risk || r.ReadToEnd().Contains(RiskLogLine)) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        return false;
    }

    public Task StartOfflineSession(string gameId, bool confirmed)
    {
        var s = Find(gameId);
        var g = s.Game;
        if (!confirmed) throw new InvalidOperationException($"{g.Name}: an offline session starts only after you confirm it");
        lock (_updateGate)
        {
            if (_updating > 0) throw new InvalidOperationException(UpdateInstalling);
            _offlineStarting++;
        }
        try { return StartOffline(s, g, gameId); }
        finally
        {
            lock (_updateGate) _offlineStarting--;
        }
    }

    Task StartOffline(GameState s, Game g, string gameId)
    {
        lock (_recorderLock)
        {
            var rec = Store.LoadGame(gameId);
            var dir = Path.GetDirectoryName(g.ExePath)!;
            var entry = OfflineEntry(g, s.AntiCheat, s.Engine);
            var appIdFile = Path.Combine(dir, SteamAppIdFile);
            var addAppId = !File.Exists(appIdFile);   // else the user's own, with this id: left as it is
            var dll = RiskDll(s.Engine, Vendor.Caps);
            var neverSaw = OfflineNeverSaw(s.Engine, dll);
            string[] placed = [.. DefaultCopy ? [] : new[] { LedgerStamp }, dll, "scskiller.ini", .. addAppId ? new[] { SteamAppIdFile } : []];
            var stamp = Stamp(g.ExePath);   // before the full check below
            string? Refusal()
            {
                if (entry == null) return "offline sessions without EasyAntiCheat aren't available for this game";
                if (GameVerdicts.Current.Unsupported(g, s.Engine) is { } why) return $"not supported: {why.Text}";
                if (!rec.OfflineRecord) return "allow offline sessions for this game first";
                if (OfflineLive(gameId) || rec.OfflineSession != null) return "the last offline session's files are still being removed";
                if (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null || rec.RecorderRollback)
                    return "a recorder's removal is still pending";
                lock (_lock)
                    if (_current == gameId) return "a compile of it is in progress";
                if (GameRunning(g)) return "it is running: close the game first";
                if (_proxyDll == null || !File.Exists(_proxyDll)) return $"the recorder ({dll}) wasn't found next to SCSKiller";
                if (stamp == null) return "its exe can't be read";
                // fresh, not the scan's: EasyAntiCheat and nothing else
                if (GameFiles.DetectAntiCheat(g) != AntiCheat.EasyAntiCheat || GameFiles.DetectAntiCheat(g, ignore: AntiCheat.EasyAntiCheat) != AntiCheat.None)
                    return "its install has another anti-cheat, or couldn't be read whole";
                // never alongside a mod: none of the names the session places may be there
                if (RecorderOwnFiles.Concat(placed).FirstOrDefault(f => Path.Exists(Path.Combine(dir, f))) is { } there) return $"{there} is already in its folder";
                if (!addAppId && File.ReadAllText(appIdFile).Trim() != entry.AppId) return $"its folder has a {SteamAppIdFile} with another app id";
                if (ReShade.Detect(g) is { } mod) return $"ReShade ({Path.GetFileName(mod.Dll)}) is in its folder";
                if (!ProcessNames().Contains("steam")) return SteamFirst;   // else the game restarts through Steam, with EasyAntiCheat
                return null;
            }
            if (Refusal() is { } why) throw new InvalidOperationException(why == SteamFirst ? why : $"{g.Name}: no offline session: {why}");
            // the session first: whatever happens after, every cleanup knows the folder to look at
            var session = new OfflineSession(gameId, g.ExePath, g.InstallDir, Created: [.. placed.Select(f => f + TempSuffix), .. addAppId ? [SteamAppIdFile] : Array.Empty<string>()]);
            (rec.RecorderExe, rec.RecorderInstallDir, rec.OfflineSession) = (g.ExePath, g.InstallDir, session);
            Store.SaveGame(gameId, rec);
            OfflineLeft()[gameId] = true;
            var since = DateTime.UtcNow;
            RecorderLog($"{g.Name}: offline session without EasyAntiCheat: recorder in as {dll} ({dir})");
            SafeProcessHandle? process = null;
            try
            {
                // recovery before any file is published: the logon entry, then the helper, which waits for the session's
                // process (the record's pid) while this app sets it up
                if (CleanupHelper != null)
                {
                    AddRunOnce(gameId, CleanupHelper);
                    using var helper = Process.Start(new ProcessStartInfo(CleanupHelper, [CleanupArg, gameId]) { UseShellExecute = false, CreateNoWindow = true })
                        ?? throw new InvalidOperationException("its cleanup helper didn't start");
                }
                process = StartAttested(g.ExePath, OfflineArguments, (h, pid, started) =>
                {
                    _offline[gameId] = h;
                    var ini = IniText(g, rec, Signed(g.ExePath, RootValue(g), stamp!, BoundTo(pid, started) + SignedLines(g, rec, session: true)));
                    var now = Store.LoadGame(gameId);
                    if (now.OfflineSession == null) throw new InvalidOperationException("its session was cleaned up meanwhile");
                    // every hash final before its file exists: the cleanup deletes a placed name only while it holds these bytes (the
                    // ini by its folder's entry)
                    now.RecorderFiles = new() { [dll] = ProxySha(dll)! };
                    SetIni(Store, dir, _ => new IniOwner(dir, Hex(ini), Install: g.InstallDir));
                    if (addAppId) now.RecorderFiles[SteamAppIdFile] = Hex(entry!.AppId);
                    now.OfflineSession = session = session with { Pid = pid, Started = started };
                    Store.SaveGame(gameId, now);
                    if (!DefaultCopy) Place(Path.Combine(dir, LedgerStamp), temp => WriteNew(temp, Path.GetFullPath(LedgerDir)));   // first: this copy's
                    Place(Path.Combine(dir, dll), temp => File.Copy(_proxyDll!, temp));
                    Place(Path.Combine(dir, "scskiller.ini"), temp => WriteNew(temp, ini));
                    if (addAppId) Place(Path.Combine(dir, SteamAppIdFile), temp => WriteNew(temp, entry!.AppId));
                    if (GameFiles.DetectAntiCheat(g, quick: true, ignore: AntiCheat.EasyAntiCheat) is not AntiCheat.None and var other)
                        throw new InvalidOperationException($"{other} appeared in its folder");
                }, () =>
                {
                    var now = Store.LoadGame(gameId);
                    now.OfflineSession = session = session with { Resumed = true };
                    Store.SaveGame(gameId, now);
                });
            }
            catch (Exception e)
            {
                _offline.TryRemove(gameId, out _);
                CleanOffline(gameId);
                throw new InvalidOperationException($"{g.Name}: the offline session didn't start: {e.Message}", e);
            }
            RecorderLog($"{g.Name}: offline session started ({g.ExePath}, process {session.Pid}): only that process records");
            Refresh(g);
            return Task.Run(() => EndOffline(g, process!, since, false, neverSaw));
        }
    }

    /// <summary>scskiller.ini's lines that bind the recorder to one process: its pid and creation FILETIME.</summary>
    internal static string BoundTo(int pid, long created) => $"pid={pid}\r\npid_time={created}\r\n";

    static string Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Writes a file in a game's folder under a temp name beside it, then renames it into place: a write cut off (a
    /// full disk, the app closed) leaves no half file under the real name, and one it <paramref name="replace"/>s stays whole
    /// until the rename.</summary>
    static void Place(string path, Action<string> write, bool replace = false)
    {
        var temp = path + TempSuffix;
        try
        {
            write(temp);
            for (var attempt = 1; ; attempt++)
                try
                {
                    File.Move(temp, path, replace);
                    break;
                }
                // any open handle on the file it replaces refuses the rename, a reader's too (the game's proxy, a virus scanner)
                catch (Exception e) when (replace && attempt < 20 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    static void WriteNew(string path, string text)
    {
        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        f.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The cleanup, the moment the process exits (its handle, not the watcher, which lags a few polls). A
    /// <paramref name="risk"/> session lasts while any process runs from the game's folders (a launcher, a relaunch). Either
    /// ends with <see cref="SessionNote"/> when the proxy admitted none of its processes since <paramref name="since"/>.</summary>
    async Task EndOffline(Game g, SafeProcessHandle process, DateTime since, bool risk, string neverSaw)
    {
        try { await Task.Factory.StartNew(() => WaitForSingleObject(process, uint.MaxValue), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
        finally
        {
            _offline.TryRemove(g.Id, out _);
            process.Dispose();
        }
        var kind = risk ? "session" : "offline session";
        RecorderLog($"{g.Name}: the {kind}'s game exited");
        var admitted = Admitted(g, since, risk);
        var done = CleanOffline(g.Id);
        while (!done && risk && Store.LoadGame(g.Id).OfflineSession is { } left && OfflineRuns(left))
        {
            await Task.Delay(2000);
            done = CleanOffline(g.Id);
        }
        if (!done) RecorderLog($"{g.Name}: the {kind}'s files that are left go when nothing runs from the game folder");
        if (!admitted)
        {
            // after the wait: another process of the session may have reached its first device meanwhile (a missing file reads as 1601)
            var why = File.GetLastWriteTimeUtc(RefusedFile(g.ExePath)) < since ? null : Refused(g.ExePath);
            _sessionNotes[g.Id] = SessionNote(why, neverSaw);
            RecorderLog($"{g.Name}: the {kind} recorded nothing: {(why != null ? "the recorder refused it: " + why : "the recorder never loaded, no scskiller.log written during the run")}");
        }
        Refresh(g);
    }

    ConcurrentDictionary<string, bool> OfflineLeft()
    {
        if (_offlineLeft is { } known) return known;
        var found = new ConcurrentDictionary<string, bool>();
        var games = Path.Combine(Store.DataDir, "games");
        foreach (var d in Directory.Exists(games) ? Directory.GetDirectories(games) : [])
            try
            {
                if (Store.LoadGame(AppStore.GameId(Path.GetFileName(d))).OfflineSession is { } s) found[s.GameId] = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { }
        return Interlocked.CompareExchange(ref _offlineLeft, found, null) ?? found;
    }

    /// <summary>Every offline session that isn't running and may have left files (SCSKiller closed or crashed while the game
    /// ran, a removal that failed): first thing in a scan and in every watcher pass.</summary>
    void CleanOfflineSessions()
    {
        foreach (var id in OfflineLeft().Keys)
            try { if (CleanOffline(id)) _offlineLeft!.TryRemove(id, out _); }
            catch (Exception e) { RecorderLog($"{id}: cleaning up after an offline session failed: {e.Message}"); }
    }

    /// <summary><see cref="CleanOfflineSession"/> for a session not running in this app. True when nothing of it is left.</summary>
    bool CleanOffline(string id)
    {
        if (OfflineLive(id)) return false;
        bool done;
        lock (_recorderLock) done = !OfflineLive(id) && CleanOfflineSession(Store, id, OfflineRuns, RecorderLog) == null;
        if (done) _offlineLeft?.TryRemove(id, out _);
        if (done && Games.FirstOrDefault(s => s.Game.Id == id) is { } state)
        {
            Refresh(state.Game);
            StartSharing([state.Game]);
        }
        return done;
    }

    /// <summary>The one cleanup of an offline session, in the app and in the helper (one at a time, across processes), never
    /// while its process runs: each file it placed that still holds the bytes it placed (<see cref="GameRecord.RecorderFiles"/>)
    /// deleted first, and each name it journaled (<see cref="OfflineSession.Created"/>) only when it is SCSKiller's: its own
    /// names (scskiller*, a temp name) by name, d3d12.dll or d3d11.dll when it is our proxy (its export or the recorded hash),
    /// anything else (steam_appid.txt) only with the bytes the session wrote. One that isn't ours (a mod, the user's own) is
    /// left and leaves the journal; one that can't be read waits for a retry. Then the recorder's data files (none was there
    /// before it), once its recording is merged and its report kept, which waits while another process <paramref name="runs"/>
    /// from the folder; its copy's marker (<see cref="LedgerStamp"/>) last. The session stays recorded, and its logon entry,
    /// until nothing of it is left. Every step can run again. Null when nothing of it is left, else what is.</summary>
    internal static List<string>? CleanOfflineSession(AppStore store, string id, Func<OfflineSession, bool> runs, Action<string> log)
    {
        using var gate = new AppStore.PathGate(Path.Combine(store.GameDir(id), "offline"));
        MigrateInis(store);   // this helper may run before the app's first start does it
        var rec = store.LoadGame(id);
        if (rec.OfflineSession is not { } s)
        {
            RunOnce(id, null);
            return null;
        }
        var name = OfflineEac.Current.FirstOrDefault(e => e.Id == id || e.Exe.Equals(Path.GetFileName(s.Exe), StringComparison.OrdinalIgnoreCase))?.Name ?? id;
        if (Foreign(Path.GetDirectoryName(s.Exe)!))   // a journal copied with another copy's data (a portable first start): that copy's session
        {
            log($"{name}: the offline session in {Path.GetDirectoryName(s.Exe)} is another SCSKiller copy's: left to it");
            rec.OfflineSession = null;
            store.SaveGame(id, rec);
            return null;
        }
        if (Alive(s))
        {
            // never resumed: our own child, still suspended (SCSKiller ended while it set the session up)
            if (s.Resumed || !EndSuspended(s)) return [.. rec.RecorderFiles.Keys];
            log($"{name}: the offline session's process {s.Pid} was never resumed: ended");
        }
        var dir = Path.GetDirectoryName(s.Exe)!;
        if (!Directory.Exists(dir))
        {
            if (!Gone(dir)) return [dir];   // its drive isn't there: nothing is known gone
            log($"{name}: offline session cleaned up: its game folder {dir} is gone");
            (rec.OfflineSession, rec.RecorderFiles, rec.RecorderExe, rec.RecorderInstallDir) = (null, [], null, null);
            store.SaveGame(id, rec);
            RunOnce(id, null);
            return null;
        }
        var left = new List<string>();
        static bool Named(string file) => file.StartsWith("scskiller", StringComparison.OrdinalIgnoreCase) || file.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase);
        void Delete(string file)
        {
            try { File.Delete(Path.Combine(dir, file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(file); }
        }
        // what a launch loads goes first, before any wait for the recording
        var data = RecorderDataFiles.Concat(Recordings.AppFiles).Append(LedgerStamp).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // the ini never by its name (1.2's journals list it): by its folder's entry, as every removal takes it (StripIni)
        var created = (s.Created ?? []).Where(f => !data.Contains(f) && !f.Equals("scskiller.ini", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try { StripIni(dir, store, null, s.InstallDir, name, log); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            left.Add("scskiller.ini");   // its folder's entry couldn't be read or saved: tried again
            log($"{name}: couldn't take {Path.Combine(dir, "scskiller.ini")} out ({e.Message}): tried again later");
        }
        foreach (var n in new[] { Dll12, Dll11 })   // ours by its export, whatever the manifest says
            try { if (IsOurProxy(Path.Combine(dir, n))) File.Delete(Path.Combine(dir, n)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(n); }
        // a name is gone only when the folder, listed whole, lacks it (File.Exists is false on an access error too); one that
        // can't be listed keeps every name for the retry
        HashSet<string>? listed;
        try { listed = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { listed = null; }
        foreach (var (file, hash) in rec.RecorderFiles.Where(f => !f.Key.Equals("scskiller.ini", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var path = Path.Combine(dir, file);
            try
            {
                if (listed == null) { left.Add(file); continue; }
                if (!listed.Contains(file)) { rec.RecorderFiles.Remove(file); continue; }
                if (!Named(file) && !(file is Dll12 or Dll11 && IsOurProxy(path)) && Sha256(path) != hash)
                {
                    log($"{name}: left {path}: not the file the offline session placed");
                    rec.RecorderFiles.Remove(file);
                    continue;
                }
                File.Delete(path);
                listed.Remove(file);
                rec.RecorderFiles.Remove(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(file); }
        }
        foreach (var file in created)
        {
            var path = Path.Combine(dir, file);
            try
            {
                if (listed == null) { left.Add(file); continue; }
                if (!listed.Contains(file)) continue;
                // SCSKiller's own names by name; a dll only when it is our proxy, anything else only with the bytes it wrote
                if (Named(file) || file is Dll12 or Dll11 && IsOurProxy(path) || rec.RecorderFiles.TryGetValue(file, out var hash) && Sha256(path) == hash) Delete(file);
                else log($"{name}: left {path}: not SCSKiller's");   // it leaves the journal below
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(file); }   // read again at the retry
        }
        if (IniPending(store, dir)) left.Add("scskiller.ini");   // its folder's entry takes it out, the session stays till then
        // a name confirmed gone leaves the list: one put there later is never the session's
        if (s.Created != null) rec.OfflineSession = s = s with { Created = [.. s.Created.Where(f => left.Contains(f) || data.Contains(f) && File.Exists(Path.Combine(dir, f)))] };
        store.SaveGame(id, rec);
        if (left.Count > 0) return left;
        var others = runs(s);
        var inbox = new FileInfo(Path.Combine(dir, "scskiller.db"));
        if (!others && inbox is { Exists: true, Length: > 0 })
            try
            {
                var recording = Path.Combine(store.GameDir(id), "recording.db");
                using (Recordings.Lock(recording)) Recordings.Merge(recording, inbox.FullName, null);
                rec.RecordingImportedAt = DateTimeOffset.Now;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                log($"{name}: the offline session's recording couldn't be imported ({e.Message}): its files stay until it is");
                others = true;
            }
        if (!others)
            try
            {
                // the session's report (its csv, log and frame log) goes to the data folder, where the game page reads it
                Directory.CreateDirectory(OfflineSessionDir(store, id));
                MoveSessionFiles(dir, OfflineSessionDir(store, id));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"{name}: the offline session's report couldn't be kept ({e.Message}): its files stay until it is");
                others = true;
            }
        var present = data.Where(f => File.Exists(Path.Combine(dir, f))).ToList();
        if (!others)
        {
            present.Where(f => !f.Equals(LedgerStamp, StringComparison.OrdinalIgnoreCase)).ToList().ForEach(Delete);
            if (left.Count == 0 && present.Contains(LedgerStamp, StringComparer.OrdinalIgnoreCase)) Delete(LedgerStamp);   // last: what is left stays this copy's
        }
        else left.AddRange(present);
        if (left.Count > 0)
        {
            store.SaveGame(id, rec);
            return left;
        }
        log($"{name}: offline session cleaned up");
        (rec.OfflineSession, rec.RecorderExe, rec.RecorderInstallDir) = (null, null, null);
        store.SaveGame(id, rec);
        RunOnce(id, null);
        return null;
    }

    /// <summary>The session's process runs: its pid is a process created when the record says.</summary>
    static bool Alive(OfflineSession s)
    {
        if (s.Pid == 0) return false;
        using var h = OpenProcess(Synchronize | QueryLimitedInformation, false, s.Pid);
        return !h.IsInvalid && GetProcessTimes(h, out var created, out _, out _, out _) && created == s.Started && WaitForSingleObject(h, 0) == WaitTimeout;
    }

    /// <summary>The folder doesn't exist, as the nearest ancestor that can be listed shows; false when none can (the drive
    /// unavailable) or one can't be read.</summary>
    internal static bool Gone(string dir)
    {
        for (var p = dir; Path.GetDirectoryName(p) is { } parent; p = parent)
            try { return !Directory.EnumerateFileSystemEntries(parent).Any(e => Path.GetFileName(e).Equals(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)); }
            catch (DirectoryNotFoundException) { }   // that one is gone too
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        return false;
    }

    /// <summary>Ends the session's process, its pid and creation time checked again on the handle; true once it has exited.</summary>
    static bool EndSuspended(OfflineSession s)
    {
        using var h = OpenProcess(Terminate | Synchronize | QueryLimitedInformation, false, s.Pid);
        return !h.IsInvalid && GetProcessTimes(h, out var created, out _, out _, out _) && created == s.Started
               && TerminateProcess(h, 1) && WaitForSingleObject(h, 5000) == 0;
    }

    /// <summary>Where an offline session's report (<see cref="SessionFiles"/>) is kept once it left the game folder.</summary>
    internal static string OfflineSessionDir(AppStore store, string gameId) => Path.Combine(store.GameDir(gameId), "offline-session");

    /// <summary>A process's creation FILETIME; null when it can't be opened.</summary>
    internal static long? StartedAt(int pid)
    {
        using var h = OpenProcess(QueryLimitedInformation, false, pid);
        return !h.IsInvalid && GetProcessTimes(h, out var created, out _, out _, out _) ? created : null;
    }

    /// <summary><see cref="CleanupArg"/>: waits for the session's process to exit (at once when it already has, or the pid is
    /// another process now; while it has none yet, as long as the SCSKiller that started this helper sets it up), then
    /// cleans up, again every few seconds while anything of it is left. <paramref name="exe"/>: this helper's, for the
    /// logon entry, written again while the session is pending (none in tests). 0 when nothing is left.</summary>
    public static int RunOfflineCleanup(AppStore store, string gameId, TimeSpan? giveUp = null, string? exe = null, Func<OfflineSession, bool>? othersRun = null)
    {
        othersRun ??= s => OthersRun(s, ProcessTree.Snapshot());
        void Log(string line) => RecordersLog(store.DataDir, "offline cleanup: " + line);
        using var running = new Mutex(false, HelperMutex(gameId));   // while it runs, no update is applied (OfflineBlocksUpdate)
        using var app = StartingApp();
        bool SettingUp() => app != null && WaitForSingleObject(app, 0) == WaitTimeout;
        for (var clock = Stopwatch.StartNew(); ; Thread.Sleep(SettingUp() ? 200 : 5000))
        {
            try
            {
                if (store.LoadGame(gameId).OfflineSession is not { } s) return 0;
                if (exe != null) RunOnce(gameId, exe);
                if (s.Pid == 0 && SettingUp()) continue;
                if (s.Pid != 0)
                    using (var h = OpenProcess(Synchronize | QueryLimitedInformation, false, s.Pid))
                        if (!h.IsInvalid && GetProcessTimes(h, out var created, out _, out _, out _) && created == s.Started)
                        {
                            if (s.Resumed) WaitForSingleObject(h, uint.MaxValue);
                            // never resumed within a minute: the cleanup ends it, unless it was resumed meanwhile
                            else if (WaitForSingleObject(h, 60_000) == WaitTimeout && store.LoadGame(gameId).OfflineSession is { Resumed: true }) continue;
                        }
                if (CleanOfflineSession(store, gameId, othersRun, Log) == null) return 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Log($"{gameId}: {e.Message}"); }
            if (clock.Elapsed > (giveUp ?? TimeSpan.FromHours(24))) return 1;   // the next start or logon goes on
        }
    }

    /// <summary>The SCSKiller that started this process (its parent, of the same exe name); null for one started otherwise
    /// (at logon, in tests).</summary>
    static SafeProcessHandle? StartingApp()
    {
        var all = ProcessTree.Snapshot();
        var me = all.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        if (me.Parent == 0 || all.FirstOrDefault(p => p.Pid == me.Parent) is not { Pid: not 0 } parent
            || !parent.Exe.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)) return null;
        var h = OpenProcess(Synchronize, false, parent.Pid);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    /// <summary>The logon entry's value name and command: "!" defers Windows' deletion of it until the command has run.</summary>
    internal static (string Name, string Command) RunOnceEntry(string gameId, string exe) =>
        ($"!SCSKiller offline cleanup {gameId}", $"\"{exe}\" {CleanupArg} \"{gameId}\"");

    /// <summary>HKCU's RunOnce entry that runs the cleanup at the next logon (<paramref name="exe"/>: the helper's); null removes it.</summary>
    static void RunOnce(string gameId, string? exe)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
            var (name, command) = RunOnceEntry(gameId, exe ?? "");
            if (exe != null) key.SetValue(name, command);
            else if (key.GetValue(name) != null) key.DeleteValue(name, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>Starts <paramref name="exe"/> in its folder, suspended (its loader hasn't run, so nothing is loaded yet);
    /// <paramref name="setUp"/> gets the process, its pid and creation time and places the recorder for that process alone;
    /// then it runs, and <paramref name="resumed"/> is called. A step that fails ends the process.</summary>
    internal static SafeProcessHandle StartAttested(string exe, string args, Action<SafeProcessHandle, int, long> setUp, Action? resumed = null)
    {
        var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var cmd = new StringBuilder($"\"{exe}\"" + (args.Length > 0 ? " " + args : ""));
        if (!CreateProcessW(exe, cmd, 0, 0, false, CreateSuspended, 0, Path.GetDirectoryName(exe)!, ref si, out var pi)) throw new Win32Exception();
        var process = new SafeProcessHandle(pi.Process, true);
        try
        {
            if (!GetProcessTimes(process, out var created, out _, out _, out _)) throw new Win32Exception();
            setUp(process, pi.ProcessId, created);
            if (ResumeThread(pi.Thread) == uint.MaxValue) throw new Win32Exception();
            resumed?.Invoke();
            return process;
        }
        catch
        {
            TerminateProcess(process, 1);
            process.Dispose();
            throw;
        }
        finally { CloseHandle(pi.Thread); }
    }

    const uint CreateSuspended = 0x4, Terminate = 0x1, Synchronize = 0x100000, QueryLimitedInformation = 0x1000, WaitTimeout = 0x102;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }
    struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint env,
        string dir, ref StartupInfo si, out ProcessInformation pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(SafeProcessHandle h, uint ms);
}
