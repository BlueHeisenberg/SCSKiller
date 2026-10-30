using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vulkan;
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
// next logon on Windows (RunOnce) and at every scan and watcher pass, until none of them is left.
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
    // game id -> the Always record session waiting for its launch (BindRisk), until it binds one or is called off
    readonly ConcurrentDictionary<string, CancellationTokenSource> _armed = new();
    // game id -> why its Always record session doesn't wait now, as last logged
    readonly ConcurrentDictionary<string, string> _armFailed = new();
    readonly object _armGate = new();
    public const string AlwaysOffNote = "Always record turned off: the game's anti-cheat changed. Turn it on again to confirm.";
    public const string UnboundSessionNote = "This launch started before SCSKiller could attach: nothing was recorded. Try again.";
    public const string AntiCheatSessionNote = "The recorder didn't load in this session. This game contains anti-cheat. Please report it so we can check.";

    /// <summary>The game page's note for a risk or offline session the proxy admitted none of, from its refusal during the session (null:
    /// it wrote none). An unstamped ini's refusal is the store's game reaching its first device before <see cref="BindRisk"/>
    /// signed it: the proxy decides once per process.</summary>
    internal static string SessionNote(string? refused, string neverSaw = NeverSawNote) => refused switch
    {
        null => neverSaw,
        _ when Unstamped(refused) || refused == "scskiller.ini changed while deciding" => UnboundSessionNote,
        _ => NotRecordedNote(refused),
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
    public bool OfflineBlocksUpdate => _offlineStarting > 0 || _offline.Keys.Any(id => !_armed.ContainsKey(id))
        || OfflineLeft().Keys.Any(id => !_armed.ContainsKey(id) && (HelperRuns(id) || Store.LoadGame(id).OfflineSession is { } s && Alive(s)));

    /// <summary>An update's apply begins: no offline session starts until <see cref="EndUpdate"/>, and the Always record
    /// sessions waiting for a launch are called off (their files out, their helpers gone) first. False (nothing begun) while
    /// one blocks it.</summary>
    public bool BeginUpdate()
    {
        lock (_updateGate)
        {
            if (OfflineBlocksUpdate) return false;
            _updating++;
        }
        if (!Disarm(null)) return true;
        for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(10); Thread.Sleep(100))
            if (!OfflineBlocksUpdate) return true;
        EndUpdate();
        return false;
    }

    public void EndUpdate()
    {
        lock (_updateGate) _updating--;
    }

    /// <summary>The cleanup helper of the game runs: it holds this mutex for its whole run.</summary>
    internal static string HelperMutex(string gameId) => @"Local\SCSKiller-offline-cleanup-" + gameId.Replace('\\', '_');

    static bool HelperRuns(string gameId) => NamedMutex.Held(HelperMutex(gameId));

    /// <summary>Writes the logon entry (tests: none).</summary>
    internal Action<string, string> AddRunOnce { get; set; } = (gameId, exe) => RunOnce(gameId, exe);

    /// <summary>What the session's exe is started with (tests).</summary>
    internal string OfflineArguments { get; set; } = "";

    /// <summary>What a risk session of a store's game runs to start it (tests).</summary>
    internal Func<Game, ProcessStartInfo?> LaunchCommand { get; set; } = g => StoreLaunch.Command(g);

    /// <summary>The exe that runs <see cref="CleanupArg"/> (the app's own, <see cref="AppProgram"/>); null: no helper and no
    /// logon entry (the CLI, tests).</summary>
    public string? CleanupHelper { get; set; }

    /// <summary>The file that starts this app again once this process has exited, as the cleanup helper outlives it: an
    /// AppImage's own file on Linux, never its mount, which goes with the AppImage's last process.</summary>
    public static string? AppProgram => ThisAppProgram();

    private static partial string? ThisAppProgram();

    /// <summary>Whether a process other than the session's runs from its folders, so its data files wait. Replaceable for tests.</summary>
    internal Func<OfflineSession, bool> OfflineRuns { get; set; }

    /// <summary>A process of <paramref name="all"/> is named like an exe of the session's folders, other than the session's
    /// own (one that exited, still held open).</summary>
    internal static bool OthersRun(OfflineSession s, List<(int Pid, int Parent, string Exe)> all) =>
        ExeNamesIn([s.InstallDir, Path.GetDirectoryName(s.Exe)]) is not { } names   // can't tell: as if one runs
        || all.Any(p => p.Pid != s.Pid && names.Contains(Path.GetFileNameWithoutExtension(p.Exe)));

    /// <summary><see cref="OfflineEac.Of"/>, never for a game added by hand before its folder is confirmed (as the recorder), nor
    /// where this OS has no offline sessions (<see cref="OfflineUnavailable"/>); an unsupported engine only while Settings.RecordAntiCheatGames is on.</summary>
    OfflineEacGame? OfflineEntry(Game g, AntiCheat antiCheat, EngineInfo? engine) =>
        OfflineUnavailable != null || Unconfirmed(g) || RecorderCrashes(g) ? null : OfflineEac.Of(g, antiCheat, engine, unsupported: Settings.RecordAntiCheatGames);

    /// <summary>Why this OS has no offline sessions: they start the game suspended (<see cref="StartAttested"/>), which only
    /// Windows does. Null where it has them.</summary>
    string? OfflineUnavailable => RiskUnavailable ?? (OperatingSystem.IsWindows() ? null : OfflineOnWindowsOnly);

    internal const string OfflineOnWindowsOnly = "offline sessions are available on Windows only";

    /// <summary>Why this OS has no risk or offline sessions yet, null where it has them. Replaceable for tests.</summary>
    internal string? RiskUnavailable { get; set; } = RiskUnavailableHere();

    static string? RiskUnavailableHere()
    {
        string? why = null;
        NoRiskSessions(ref why);
        return why;
    }

    static partial void NoRiskSessions(ref string? why);

    /// <summary>Settings.RecordAntiCheatGames, read as off where this OS has no risk sessions: settings.json keeps the user's value.</summary>
    bool RecordsAntiCheat => RiskUnavailable == null && Settings.RecordAntiCheatGames;

    public void SetOfflineRecording(string gameId, bool on)
    {
        lock (_recorderLock)
        {
            var s = Find(gameId);
            if (on && !s.OfflineEligible && !s.RiskEligible) throw new InvalidOperationException($"{s.Game.Name}: recording while you play isn't available for this game");
            var rec = Store.LoadGame(gameId);
            rec.OfflineRecord = on;
            if (!on) rec.AlwaysRecord = false;
            Store.SaveGame(gameId, rec);
            if (!on) _sessionNotes.TryRemove(gameId, out _);
        }
        if (!on) Disarm(gameId);
        RefreshGame(gameId);
    }

    public void SetAlwaysRecord(string gameId, bool on, bool confirmed, AntiCheat shown)
    {
        lock (_recorderLock)
        {
            var s = Find(gameId);
            var rec = Store.LoadGame(gameId);
            if (on)
            {
                if (!confirmed) throw new InvalidOperationException($"{s.Game.Name}: Always record goes on only after you confirm it");
                if (!s.RiskEligible || !rec.OfflineRecord) throw new InvalidOperationException($"{s.Game.Name}: Always record isn't available for this game");
                if (GameFiles.DetectAntiCheat(s.Game) is var found && found != shown)
                    throw new InvalidOperationException($"{s.Game.Name}: {AntiCheatName(found, s.Vac)} was found, not {AntiCheatName(shown, s.Vac)}: confirm again");
            }
            (rec.AlwaysRecord, rec.AlwaysRecordShown) = (on, on ? shown : AntiCheat.None);
            Store.SaveGame(gameId, rec);
            _armFailed.TryRemove(gameId, out _);
            _sessionNotes.TryRemove(new KeyValuePair<string, string>(gameId, AlwaysOffNote));
        }
        if (on) Arm(gameId);
        else Disarm(gameId);
        RefreshGame(gameId);
    }

    /// <summary>Starts the game's Always record session, waiting for its next launch, unless one waits or runs. Why it can't
    /// (the game runs, a removal is pending, an update installs) is logged once; the watcher's next pass tries again.</summary>
    void Arm(string gameId)
    {
        lock (_armGate)
        {
            if (_armed.ContainsKey(gameId) || OfflineLive(gameId)) return;
            Task bind;
            try
            {
                var s = Find(gameId);
                lock (_updateGate)
                {
                    if (_updating > 0) throw new InvalidOperationException(UpdateInstalling);
                    _offlineStarting++;
                }
                try { bind = StartRisk(s, s.Game, gameId, Store.LoadGame(gameId).AlwaysRecordShown, always: true); }
                finally
                {
                    lock (_updateGate) _offlineStarting--;
                }
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                ArmFailed(gameId, e.Message);
                return;
            }
            _armFailed.TryRemove(gameId, out _);
            _ = bind.ContinueWith(t => { if (t.Exception?.InnerException is { InnerException: not OperationCanceledException } e) ArmFailed(gameId, e.Message); },
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    void ArmFailed(string gameId, string why)
    {
        if (_armFailed.TryGetValue(gameId, out var last) && last == why) return;
        _armFailed[gameId] = why;
        RecorderLog($"Always record: {why}");
    }

    /// <summary>Every game with Always record on gets its session waiting, and one no longer allowed is called off (the
    /// watcher's pass).</summary>
    void ArmAlways()
    {
        foreach (var id in _armed.Keys.Where(id => !AlwaysAllowed(id)).ToList()) Disarm(id);
        foreach (var s in Games.Where(s => s.AlwaysRecord)) Arm(s.Game.Id);
    }

    /// <summary>The game may record every launch now: a session is offered for it (RiskEligible, which includes
    /// Settings.RecordAntiCheatGames) and both its switches are on.</summary>
    bool AlwaysAllowed(string gameId) =>
        RecordsAntiCheat && Games.FirstOrDefault(s => s.Game.Id == gameId) is { RiskEligible: true } st && !IntelLocalDriver(st.Game) && Store.LoadGame(gameId) is { OfflineRecord: true, AlwaysRecord: true };

    /// <summary>Calls off the Always record sessions still waiting for a launch (null: every game's): each takes its files out
    /// as a session's end does. One already bound runs to its end. True when one was waiting.</summary>
    bool Disarm(string? gameId)
    {
        var any = false;
        foreach (var id in _armed.Keys.Where(id => gameId == null || id == gameId))
            if (_armed.TryRemove(id, out var stop))
            {
                stop.Cancel();
                any = true;
            }
        return any;
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
        RecordsAntiCheat && anyAntiCheat && !Unconfirmed(g) && !RecorderCrashes(g)
        && engine is { } e && RecordsApi(g, e) && GameVerdicts.Current.Unsupported(g, engine) == null;

    /// <summary>A recorder can record the game's API on this OS and GPU: DirectX 12, DirectX 11 where its recording records
    /// (<see cref="Records11Api"/>), Vulkan through the layer (a Steam launch's).</summary>
    bool RecordsApi(Game g, EngineInfo e) =>
        OperatingSystem.IsWindows() && (e.GraphicsApi.Contains("D3D12") || Records11Api(e, Vendor.Caps)) || ProtonDx12(g, e) || VulkanRoute(g, e);

    /// <summary>Off Windows, a DirectX 12 game Steam runs under Proton: the session binds what Steam's reaper started
    /// (<see cref="SteamLaunched"/>), and its d3d12.dll checks that against its Linux process (admit.h's wine_bound). A sandboxed
    /// Steam's game doesn't see the recorder folder.</summary>
    static bool ProtonDx12(Game g, EngineInfo e) => !OperatingSystem.IsWindows() && e.GraphicsApi.Contains("D3D12") && SteamRun(g)
        && g.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && SteamSandboxSkip == null;

    static bool SteamRun(Game g) => (SteamSource.AppId(g.Id) ?? SteamShortcuts.AppId(g.Id)) != null;

    /// <summary>Why no session is offered for the game on this OS whatever its anti-cheat; null where one can be.</summary>
    public static string? RiskNotHere(GameState s) =>
        OperatingSystem.IsWindows() || s.Engine is { } e && (VulkanPlan.IsVulkan(e) || ProtonDx12(s.Game, e)) ? null
        : SteamSandboxSkip is { } sandboxed && s.Engine?.GraphicsApi.Contains("D3D12") == true && SteamRun(s.Game) ? Format.Sentence(sandboxed) + "."
        : RiskSteamOnly;

    public const string RiskSteamOnly = "On Linux, games with anti-cheat can be recorded only when Steam runs them on Vulkan or DirectX 12 for now.";

    /// <summary>The recorder a risk or offline session places: d3d11.dll for a DirectX 11 game whose D3D11 recording records
    /// (<see cref="Records11Api"/>, whatever the engine's support), and on such a GPU for an Unreal 4 game that may run on either
    /// (UE4 defaults to DirectX 11); d3d12.dll otherwise. One name only: two copies in one process would each append to the same
    /// scskiller.db and log.</summary>
    internal static string RiskDll(EngineInfo? e, VendorCaps caps) => e != null && VulkanPlan.IsVulkan(e) ? DllVulkan :
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
                    if (!rec.OfflineRecord && !rec.AlwaysRecord) continue;
                    (rec.OfflineRecord, rec.AlwaysRecord) = (false, false);
                    Store.SaveGame(s.Game.Id, rec);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{s.Game.Name}: couldn't turn off Record while I play: {e.Message}"); }
        Disarm(null);
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

    /// <param name="always">an Always record session: confirmed when the switch went on (<paramref name="shown"/>), it starts
    /// nothing and binds the game's next launch however long that takes; another anti-cheat found turns the switch off</param>
    Task StartRisk(GameState s, Game g, string gameId, AntiCheat shown, bool always = false)
    {
        Task task;
        lock (_recorderLock)
        {
            var rec = Store.LoadGame(gameId);
            var dir = Path.GetDirectoryName(g.ExePath)!;
            var launcher = LauncherStarts(g, s.Engine);
            var manual = g.Store == Core.Store.Manual && !launcher;
            var dll = RiskDll(s.Engine, Vendor.Caps);
            var neverSaw = AntiCheatSessionNote;
            // a Vulkan game's recorder is the layer's registration (VulkanLayer), its dll beside the exe as the proxy is: an
            // anti-cheat game may load no module from outside its folder
            var vulkan = dll == DllVulkan;
            var key = VulkanLayer.Key(g);
            string[] placed = [.. WritesLedger ? new[] { LedgerStamp } : [], dll, "scskiller.ini"];
            var stamp = Stamp(g.ExePath);   // before the fresh check below
            ProcessStartInfo? launch = null;
            AntiCheat found = AntiCheat.None;
            bool vac = false, changed = false;
            string? Refusal()
            {
                if (RiskUnavailable is { } unavailable) return unavailable;
                if (_removedFromAll) return RemovedFromAllNote;
                if (!Settings.RecordAntiCheatGames) return "allow recording in games with anti-cheat in Settings first";
                if (!s.RiskEligible) return "recording while you play isn't available for this game";
                if (vulkan && key == null) return VulkanRecorderMissing;   // no layer key: the layer's route is Steam's there
                if (dll == Dll12 && s.StreamlineFirst) return SkipStreamline;   // a session places no preloader beside anti-cheat
                if (!rec.OfflineRecord) return "turn on Record while I play for this game first";
                if (always != rec.AlwaysRecord) return always ? "Always record is off" : "Always record is on: every launch is recorded";
                if (OfflineLive(gameId) || rec.OfflineSession != null) return "the last session's files are still being removed";
                if (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null || rec.RecorderRollback
                    || rec.VulkanLayer != null)
                    return "a recorder's removal is still pending";
                lock (_lock)
                    if (_current == gameId) return "a compile of it is in progress";
                if (GameRunning(g)) return "it is running: close the game first";
                if ((vulkan ? VulkanLayerSource : _proxyDll) is not { } src || !File.Exists(src)) return $"the recorder ({dll}) wasn't found next to SCSKiller";
                if (vulkan && VulkanBlocked(g, key!, VulkanManifest(key!)) is { } blocked) return blocked;
                if (stamp == null) return "its exe can't be read";
                // fresh, not the scan's: what the confirmation named is what is there
                (found, vac) = (GameFiles.DetectAntiCheat(g), VacCheck(g));
                if (found != shown)
                {
                    changed = true;
                    return $"{AntiCheatName(found, vac)} was found, not {AntiCheatName(shown, vac)}" + (always ? "" : ": confirm again");
                }
                if (!manual && !launcher && !always && (launch = LaunchCommand(g)) == null) return "its store has no launch SCSKiller can start";
                // a mod is left as it is: only one holding a name the session places refuses
                if (Foreign(dir)) return "another SCSKiller copy's recorder is in its folder";
                if ((RecorderOwnFiles.Where(f => !f.StartsWith(Dll9, StringComparison.OrdinalIgnoreCase)).Concat(placed).FirstOrDefault(f => Path.Exists(Path.Combine(dir, f))) ?? PreloadIn(dir)) is { } there) return $"{there} is already in its folder";
                if (ShaderInjectorIn(dir) != false) return SkipShaderInjector;
                if (IntelLocalDriver(g)) return SkipIntelDriver;
                if (ReShade.Detect(g) is { ProxyLibrary: true }) return "ReShade chains a proxy library ([PROXY])";
                return null;
            }
            if (Refusal() is { } why)
            {
                if (always && changed)
                {
                    rec.AlwaysRecord = false;
                    Store.SaveGame(gameId, rec);
                    _sessionNotes[gameId] = AlwaysOffNote;
                }
                throw new InvalidOperationException($"{g.Name}: no session: {why}");
            }
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
                var manifest = vulkan ? VulkanManifest(key!) : null;
                if (manifest != null)
                {
                    (now.VulkanLayer, now.VulkanLayerSha) = (manifest, null);   // on the record before it's registered, no copy beside it
                    if (VulkanInboxDir(g) is { } inbox && !now.VulkanInboxes.Contains(inbox, StringComparer.OrdinalIgnoreCase)) now.VulkanInboxes.Add(inbox);   // imported after Steam replaces the exe too
                }
                now.RecorderFiles = new() { [dll] = manifest != null ? Sha256(VulkanLayerSource!) : ProxySha(dll)! };
                SetIni(Store, dir, _ => new IniOwner(dir, Hex(ini), Install: g.InstallDir));
                also?.Invoke(now);
                Store.SaveGame(gameId, now);
                // the Linux layer reads it as a Linux path, the proxy under Wine through its drives
                RefuseForeign(dir);
                if (WritesLedger) CreateLedger(Path.Combine(dir, LedgerStamp), manifest != null && !OperatingSystem.IsWindows() ? Path.GetFullPath(LedgerDir) : LedgerText(LedgerDir));   // first: this copy's
                Place(Path.Combine(dir, dll), CopyOf(manifest != null ? VulkanLayerSource! : _proxyDll!));
                if (manifest != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                    Place(manifest, Text(VulkanLayer.Manifest(key!, Path.Combine(dir, dll))), replace: true);
                    VulkanLayer.Register(manifest, g);
                }
                Place(Path.Combine(dir, "scskiller.ini"), Text(ini));
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
                if (manual && !always)
                    process = StartAttested(g.ExePath, OfflineArguments, (h, pid, started) =>
                    {
                        _offline[gameId] = h;
                        Publish(IniText(g, rec, Signed(g, stamp!, BoundTo(pid, started) + RiskLine + SignedLines(g, rec, session: true, riskSession: true))),
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
                    if (launch != null) Process.Start(launch)?.Dispose();   // else the user starts it from its launcher
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
            else if (always)
            {
                var stop = _armed[gameId] = new CancellationTokenSource();
                RecorderLog($"{g.Name}: Always record: the session waits for the next {Path.GetFileName(g.ExePath)} that runs from {dir}");
                task = Task.Run(() => BindRisk(g, stamp!, since, neverSaw, vulkan && OperatingSystem.IsWindows(), stop.Token, shown));
            }
            else
            {
                RecorderLog($"{g.Name}: session started through its {(launcher ? "launcher" : "store")}: it records the first {Path.GetFileName(g.ExePath)} that runs from {dir}");
                if (launcher) _sessionNotes[gameId] = LauncherSessionNote;
                task = Task.Run(() => BindRisk(g, stamp!, since, neverSaw, vulkan && OperatingSystem.IsWindows()));   // before the evaluation below: the store's game may reach its first device meanwhile
            }
        }
        Refresh(g);
        return task;
    }

    /// <summary>A Vulkan game's session until this build carries the Vulkan recorder (the loader proxy and its layer).</summary>
    public const string VulkanRecorderMissing = "this version of SCSKiller can't record Vulkan games yet";

    /// <summary>A game the user added that only its own launcher starts (Star Citizen, from the RSI Launcher; Wuthering Waves,
    /// whose exe asks for admin rights and quits without its launcher): its session binds the first process of its exe that
    /// starts from its folder, like a store's launch, and SCSKiller starts nothing.</summary>
    internal static bool LauncherStarts(Game g, EngineInfo? e) =>
        g.Store == Core.Store.Manual && (e?.Family == StarEngine.StarEngineReader.Family || e?.Fork == "GAME_WutheringWaves");

    /// <summary><see cref="LauncherStarts"/> for the game page: until its session binds, the card says <see cref="LauncherSessionNote"/>.</summary>
    public static bool StartsFromLauncher(GameState s) => LauncherStarts(s.Game, s.Engine);

    public const string LauncherSessionNote = "Start the game from its launcher now. SCSKiller records the first launch within 5 minutes.";

    /// <summary>The stamp line of a session at the user's own risk, after its binding (<see cref="BoundTo"/>), inside the signature.</summary>
    internal const string RiskLine = "risk=1\r\n";

    /// <summary>A store-launched risk session: the first process of the game's exe is bound (its pid and creation time, with
    /// risk=1, signed over the <paramref name="stamp"/> taken before the session's check), then left to run; nothing binds
    /// within 5 minutes: the session ends. A <paramref name="vulkan"/> session's game run as administrator is said at once: the
    /// loader reads no HKCU layer in an elevated process.</summary>
    /// <param name="armed">an Always record session's: no deadline, called off by <see cref="Disarm"/>; an exe changed since
    /// <paramref name="stamp"/> is checked again, and signed only while it still has <paramref name="confirmed"/></param>
    async Task BindRisk(Game g, string stamp, DateTime since, string neverSaw, bool vulkan, CancellationToken armed = default, AntiCheat confirmed = AntiCheat.None)
    {
        SafeProcessHandle? process = null;
        OfflineSession? bound = null;
        int pid = 0;
        try
        {
            // GetTickCount64 counts sleep and ignores clock changes; checked once found too: a suspended app's poll can find a
            // launch long after the confirmation
            // ponytail: an Always record session polls every 50 ms for as long as it waits (a process list each time); a process
            // start notification if that ever shows in the app's CPU time
            var deadline = armed.CanBeCanceled ? long.MaxValue : Environment.TickCount64 + (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
            long created = 0;
            while ((process = RunningExe(g, out pid, out created)) == null && Environment.TickCount64 <= deadline) await Task.Delay(50, armed);
            if (Environment.TickCount64 > deadline) throw new InvalidOperationException("the game didn't start within 5 minutes");
            lock (_recorderLock)
            {
                if (_removedFromAll) throw new RemovedFromAllException();   // a binding that waited while the removal left the running game's unsigned ini
                armed.ThrowIfCancellationRequested();
                if (IntelLocalDriver(g)) throw new InvalidOperationException(SkipIntelDriver);
                if (armed.CanBeCanceled)
                {
                    // as a confirmed session's start checks it, now: the session may have waited through an update or a change
                    if (!AlwaysAllowed(g.Id)) throw new InvalidOperationException("Always record isn't available for this game now");
                    stamp = Stamp(g.ExePath) ?? throw new InvalidOperationException("its exe can't be read");
                    if (GameFiles.DetectAntiCheat(g) is var found && found != confirmed)
                    {
                        var off = Store.LoadGame(g.Id);
                        off.AlwaysRecord = false;
                        Store.SaveGame(g.Id, off);
                        _sessionNotes[g.Id] = AlwaysOffNote;
                        throw new InvalidOperationException($"{AntiCheatName(found)} was found, not {AntiCheatName(confirmed)}");
                    }
                }
                // the cleanup's gate (CleanOfflineSession), the lock's order as CleanOffline's: a helper cleaning up a game that
                // exited at once never runs between the binding and its signed ini, which would stay unjournaled
                using var gate = new AppStore.PathGate(Path.Combine(Store.GameDir(g.Id), "offline"));
                var now = Store.LoadGame(g.Id);
                if (now.OfflineSession == null) throw new InvalidOperationException("its session was cleaned up meanwhile");
                // resumed: the launcher's process, never a suspended child of ours that a cleanup may end
                now.OfflineSession = bound = now.OfflineSession with { Pid = pid, Started = created, Resumed = true };
                Store.SaveGame(g.Id, now);
                _offline[g.Id] = process!;
                var dir = Path.GetDirectoryName(g.ExePath)!;
                var (state, judged) = JudgeIni(dir, Store, null, g.InstallDir);
                var ini = IniText(g, now, Signed(g, stamp, BoundTo(pid, created) + RiskLine + SignedLines(g, now, session: true, riskSession: true)));
                if (state != IniState.Ours || !WriteIni(dir, ini, judged, null, g.InstallDir, () => true, null, vulkan: VulkanIn(g, now)))
                    throw new InvalidOperationException("its scskiller.ini changed meanwhile");
                _armed.TryRemove(g.Id, out _);
            }
        }
        catch (Exception e)
        {
            if (_armed.TryGetValue(g.Id, out var stop) && stop.Token == armed) _armed.TryRemove(new KeyValuePair<string, CancellationTokenSource>(g.Id, stop));
            _sessionNotes.TryRemove(new KeyValuePair<string, string>(g.Id, LauncherSessionNote));
            _offline.TryRemove(g.Id, out _);
            process?.Dispose();
            CleanOffline(g.Id);
            Refresh(g);
            throw new InvalidOperationException($"{g.Name}: the session didn't start: {e.Message}", e);
        }
        _sessionNotes.TryRemove(new KeyValuePair<string, string>(g.Id, LauncherSessionNote));
        RecorderLog($"{g.Name}: session bound to process {pid} ({g.ExePath}): only that process records");
        if (OperatingSystem.IsWindows())
            try
            {
                // a bootstrapper between Steam and the game can drop SteamAppId, which the Vulkan layer's enable_environment needs
                var all = Processes(true);
                if (all.Find(p => p.Pid == pid) is { Exe: not null } self)
                    RecorderLog(all.Find(p => p.Pid == self.Parent).Exe is { } parent
                        ? $"{g.Name}: process {pid} was started by {parent} (pid {self.Parent})"
                        : $"{g.Name}: process {pid} was started by parent pid {self.Parent}, not running");
            }
            catch (Exception) { }
        // a risk session's game has anti-cheat, which may refuse its token to any reader
        var elevated = vulkan && Elevated.OfProcess(process!, deniedIsElevated: false) == true;
        if (elevated)
        {
            _sessionNotes[g.Id] = ElevatedSessionNote;
            RecorderLog($"{g.Name}: the session's game runs as administrator, which loads no per-user Vulkan layer");
        }
        Refresh(g);
        await EndOffline(g, process!, since, true, neverSaw, vulkan, elevated, bound);
    }

    /// <summary>A process of the game's exe, by its path, opened, and its creation time (the proxy's pid_time=); null when none
    /// runs. Elsewhere than on Windows no handle (an invalid one): its start time, read before and after its path, names the
    /// process the path was read of, and it must be a launch of the game's app by Steam (<see cref="SteamLaunched"/>).</summary>
    SafeProcessHandle? RunningExe(Game g, out int pid, out long created)
    {
        var name = Path.GetFileName(g.ExePath);
        // the image path names the folder resolved: an install reached through a junction spells it otherwise (as Outside)
        var real = GameFiles.FinalPath(Path.GetDirectoryName(g.ExePath)!) is { } r ? Path.Combine(r, name) : null;
        bool Ours(int pid) => ProcessPath(pid) is { } path
            && (path.Equals(g.ExePath, GameFiles.Names) || real != null && path.Equals(real, GameFiles.Names));
        // by path first, without opening it: another game's process of the same exe name is never opened under its anti-cheat
        var all = Processes(true);
        foreach (var p in all.Where(p => p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase) && Ours(p.Pid)))
        {
            if (!OperatingSystem.IsWindows())
            {
                var steam = false;
                // the ancestry from a list taken after its start time was read: a pid reused since the first list isn't judged by the old one's
                if (ProcessTree.Created(p.Pid) is { } at && Ours(p.Pid))
                {
                    SteamLaunched(g, p.Pid, at, Processes(true), ref steam);
                    if (steam && ProcessTree.Created(p.Pid) == at)
                    {
                        (pid, created) = (p.Pid, at);
                        return new SafeProcessHandle(0, false);
                    }
                }
                continue;
            }
            var h = OpenProcess(Synchronize | QueryLimitedInformation, false, p.Pid);
            // the open handle keeps the pid from being reused: the path read now is that process's
            if (!h.IsInvalid && Ours(p.Pid))
            {
                if (!GetProcessTimes(h, out created, out _, out _, out _))
                {
                    h.Dispose();
                    throw new Win32Exception();
                }
                pid = p.Pid;
                return h;
            }
            h.Dispose();
        }
        (pid, created) = (0, 0);
        return null;
    }

    /// <summary>Elsewhere than on Windows: the process, started at <paramref name="started"/>, is a launch of the game's app by
    /// Steam, a descendant of Steam's reaper for that app.</summary>
    partial void SteamLaunched(Game g, int pid, long started, List<(int Pid, int Parent, string Exe)> all, ref bool yes);

    internal const string RiskLogLine = "risk=1: recording at the user's own risk";

    /// <summary>The proxy admitted a process of the session: it opens scskiller.log only once it admits one, and under risk=1
    /// its first line there is <see cref="RiskLogLine"/>; the log is in the game folder, or in the data folder once the
    /// session's cleanup moved it (newer than the session's start), or the Vulkan layer's inbox. Unreadable: taken as admitted.</summary>
    bool Admitted(Game g, DateTime since, bool risk)
    {
        foreach (var dir in new[] { Path.GetDirectoryName(g.ExePath)!, OfflineSessionDir(Store, g.Id), VulkanInboxDir(g) }.OfType<string>())
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
            if (_removedFromAll) throw new RemovedFromAllException();   // the session places the recorder itself, not through Install
            var rec = Store.LoadGame(gameId);
            var dir = Path.GetDirectoryName(g.ExePath)!;
            var entry = OfflineEntry(g, s.AntiCheat, s.Engine);
            var appIdFile = Path.Combine(dir, SteamAppIdFile);
            var addAppId = !File.Exists(appIdFile);   // else the user's own, with this id: left as it is
            var dll = RiskDll(s.Engine, Vendor.Caps);
            var neverSaw = OfflineNeverSaw(s.Engine, dll);
            string[] placed = [.. WritesLedger ? new[] { LedgerStamp } : [], dll, "scskiller.ini", .. addAppId ? new[] { SteamAppIdFile } : []];
            var stamp = Stamp(g.ExePath);   // before the full check below
            string? Refusal()
            {
                if (OfflineUnavailable is { } unavailable) return unavailable;
                if (entry == null) return "offline sessions without EasyAntiCheat aren't available for this game";
                if (dll == DllVulkan) return VulkanRecorderMissing;   // the layer is enabled by Steam's launch, which this session isn't
                if (dll == Dll12 && s.StreamlineFirst) return SkipStreamline;   // a session places no preloader beside anti-cheat
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
                if (Foreign(dir)) return "another SCSKiller copy's recorder is in its folder";
                if ((RecorderOwnFiles.Where(f => !f.StartsWith(Dll9, StringComparison.OrdinalIgnoreCase)).Concat(placed).FirstOrDefault(f => Path.Exists(Path.Combine(dir, f))) ?? PreloadIn(dir)) is { } there) return $"{there} is already in its folder";
                if (ShaderInjectorIn(dir) != false) return SkipShaderInjector;
                if (IntelLocalDriver(g)) return SkipIntelDriver;
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
                    var ini = IniText(g, rec, Signed(g, stamp!, BoundTo(pid, started) + SignedLines(g, rec, session: true)));
                    var now = Store.LoadGame(gameId);
                    if (now.OfflineSession == null) throw new InvalidOperationException("its session was cleaned up meanwhile");
                    // every hash final before its file exists: the cleanup deletes a placed name only while it holds these bytes (the
                    // ini by its folder's entry)
                    now.RecorderFiles = new() { [dll] = ProxySha(dll)! };
                    SetIni(Store, dir, _ => new IniOwner(dir, Hex(ini), Install: g.InstallDir));
                    if (addAppId) now.RecorderFiles[SteamAppIdFile] = Hex(entry!.AppId);
                    now.OfflineSession = session = session with { Pid = pid, Started = started };
                    Store.SaveGame(gameId, now);
                    RefuseForeign(dir);
                    if (WritesLedger) CreateLedger(Path.Combine(dir, LedgerStamp), LedgerText(LedgerDir));   // first: this copy's
                    Place(Path.Combine(dir, dll), CopyOf(_proxyDll!));
                    Place(Path.Combine(dir, "scskiller.ini"), Text(ini));
                    if (addAppId) Place(Path.Combine(dir, SteamAppIdFile), Text(entry!.AppId));
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
    internal static void Place(string path, Action<Stream> write, bool replace = false, string suffix = TempSuffix)
    {
        // E2E: an antivirus refusing the recorder's copy (800700E1, 800700E2)
        if (Environment.GetEnvironmentVariable("SCSKILLER_TEST_PLACE_FAIL") is { Length: > 0 } hr && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new IOException(hr, Convert.ToInt32(hr, 16));
        var (file, temp) = CreateTemp(path, suffix);
        try
        {
            using (file) write(file);
            RefuseLink(path);
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

    /// <summary>A new file beside <paramref name="path"/>, and its name: one nobody can know before it exists, so a link
    /// planted beside it is never written through.</summary>
    internal static (FileStream File, string Name) CreateTemp(string path, string suffix)
    {
        var temp = $"{path}.{Guid.NewGuid():N}{suffix}";
        return (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), temp);
    }

    /// <summary>Throws when <paramref name="path"/> is a link (on Linux a hard link too): someone else put it there.</summary>
    internal static void RefuseLink(string path)
    {
        if (Linked(path)) throw new IOException($"{path} is a link: not replaced");
    }

    private static partial bool Linked(string path);

    static Action<Stream> Text(string text) => f => f.Write(Encoding.UTF8.GetBytes(text));

    static Action<Stream> CopyOf(string source) => f =>
    {
        using var s = File.OpenRead(source);
        s.CopyTo(f);
    };

    /// <summary>The cleanup, the moment the process exits (its handle, not the watcher, which lags a few polls). A
    /// <paramref name="risk"/> session lasts while any process runs from the game's folders (a launcher, a relaunch). Either
    /// ends with <see cref="SessionNote"/> when the proxy admitted none of its processes since <paramref name="since"/>; a
    /// <paramref name="vulkan"/> session whose layer never loaded with why, where the game ran elevated or on DirectX.</summary>
    /// <param name="bound">the session as bound: elsewhere than on Windows its process is waited for by its pid and start time</param>
    async Task EndOffline(Game g, SafeProcessHandle process, DateTime since, bool risk, string neverSaw, bool vulkan = false, bool elevated = false, OfflineSession? bound = null)
    {
        try
        {
            if (OperatingSystem.IsWindows()) await Task.Factory.StartNew(() => WaitForSingleObject(process, uint.MaxValue), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            else
                while (bound != null && Alive(bound)) await Task.Delay(250);
        }
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
            var state = vulkan && why == null && !elevated ? Games.FirstOrDefault(s => s.Game.Id == g.Id) : null;
            var ran = state == null ? null : RanApi(g, state.Engine, since);
            _sessionNotes[g.Id] = why == null && elevated ? ElevatedSessionNote
                : ran != null && ApiKey(ran) != "vk" ? RanOnNote(ran, state!.ApiOverride == DxApi.Vulkan) : SessionNote(why, neverSaw);
            RecorderLog($"{g.Name}: the {kind} recorded nothing: {(why != null ? "the recorder refused it: " + why : "the recorder never loaded, no scskiller.log written during the run")}"
                + (why == null && elevated ? ", the game ran as administrator" : ran != null ? $", the game ran on {ApiName(ran)}" : ""));
        }
        else if (elevated) _sessionNotes.TryRemove(new KeyValuePair<string, string>(g.Id, ElevatedSessionNote));   // it loaded all the same
        Refresh(g);
        if (risk && done && Store.LoadGame(g.Id).AlwaysRecord) Arm(g.Id);   // else the watcher's next pass, once the files are out
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
        if (rec is { VulkanLayer: not null, VulkanLayerSha: null })   // first: no launch loads the layer after this. A normal install's (its copy's hash) stays
        {
            if (!UnregisterVulkan(rec, store, name, log)) return ["the Vulkan recorder's registration"];
            store.SaveGame(id, rec);
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
        catch (ForeignFolderException e)   // another copy's since the check above: nothing more here, the next cleanup leaves it to that copy
        {
            log($"{name}: {e.Message}");
            return ["scskiller.ini"];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            left.Add("scskiller.ini");   // its folder's entry couldn't be read or saved: tried again
            log($"{name}: couldn't take {Path.Combine(dir, "scskiller.ini")} out ({e.Message}): tried again later");
        }
        foreach (var n in new[] { Dll12, Dll11, Dll9 })   // ours by its export, whatever the manifest says
            try { if (IsOurProxy(Path.Combine(dir, n))) File.Delete(Path.Combine(dir, n)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(n); }
        foreach (var n in PreloadNames)   // never a session's: a recorder's, left from before the anti-cheat was found
            try { if (IsOurPreload(Path.Combine(dir, n))) File.Delete(Path.Combine(dir, n)); }
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
                if (!Named(file) && !(file is Dll12 or Dll11 or Dll9 && IsOurProxy(path)) && Sha256(path) != hash)
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
                if (Named(file) || file is Dll12 or Dll11 or Dll9 && IsOurProxy(path) || rec.RecorderFiles.TryGetValue(file, out var hash) && Sha256(path) == hash) Delete(file);
                else log($"{name}: left {path}: not SCSKiller's");   // it leaves the journal below
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(file); }   // read again at the retry
        }
        // a write cut off by a crash leaves Place's own temp name (<name>.<guid>.scskiller-new), never the journaled one
        foreach (var file in created.Where(f => f.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)))
            try
            {
                var placed = file[..^TempSuffix.Length];
                foreach (var temp in Directory.EnumerateFiles(dir, placed + ".*" + TempSuffix).Select(Path.GetFileName).OfType<string>().Where(t => PlaceTemp(t, placed)).ToList())
                    File.Delete(Path.Combine(dir, temp));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(file); }
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
    private static partial bool Alive(OfflineSession s);

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
        using var running = NamedMutex.Open(HelperMutex(gameId));   // while it runs, no update is applied (OfflineBlocksUpdate)
        var starting = StartingAppPid();
        using var app = starting is { } a && OperatingSystem.IsWindows() ? OpenStartingApp(a) : null;
        // its start time while it is still this helper's parent: once it exits, the helper is another's child
        var appStarted = starting is { } b && !OperatingSystem.IsWindows() && ProcessTree.Created(b) is { } t && StartingAppPid() == b ? t : (long?)null;
        bool SettingUp() => app != null ? WaitForSingleObject(app, 0) == WaitTimeout : appStarted != null && ProcessTree.Created(starting!.Value) == appStarted;
        for (var clock = Stopwatch.StartNew(); ; Thread.Sleep(SettingUp() ? 200 : 5000))
        {
            try
            {
                if (store.LoadGame(gameId).OfflineSession is not { } s) return 0;
                if (exe != null) RunOnce(gameId, exe);
                if (s.Pid == 0 && SettingUp()) continue;
                if (s.Pid != 0 && !OperatingSystem.IsWindows())
                {
                    // bound to its start time and running (BindRisk): waited for until it exits
                    while (s.Resumed && Alive(s)) Thread.Sleep(1000);
                }
                else if (s.Pid != 0)
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
    static int? StartingAppPid()
    {
        var all = ProcessTree.Snapshot();
        var me = all.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        return me.Parent == 0 || all.FirstOrDefault(p => p.Pid == me.Parent) is not { Pid: not 0 } parent
            || !parent.Exe.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase) ? null : parent.Pid;
    }

    static SafeProcessHandle? OpenStartingApp(int pid)
    {
        var h = OpenProcess(Synchronize, false, pid);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    /// <summary>The logon entry that runs the cleanup at the next logon (<paramref name="exe"/>: the helper's); null removes it.</summary>
    private static partial void RunOnce(string gameId, string? exe);

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
