using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

// Offline sessions without EasyAntiCheat (Games.OfflineEac): the one case where the recorder goes into an anti-cheat game.
// SCSKiller starts the exe itself, suspended, and binds the attestation to that process (ScsKiller.ArmedFile's pid= and
// pid_time=), so any other launch is a pass-through while the files are there. A leftover d3d12.dll would still be loaded
// into a normal EasyAntiCheat launch, so every name the session may create is journaled (GameRecord.OfflineSession) before
// it exists, and its cleanup (CleanOfflineSession) runs when the process exits: in the app, in a helper process that
// outlives it (CleanupArg), at the next logon (RunOnce) and at every scan and watcher pass, until none of them is left.
public sealed partial class ScsKiller
{
    public const string SteamAppIdFile = "steam_appid.txt";
    /// <summary>SCSKiller.exe's argument for the cleanup helper, followed by the game id (<see cref="RunOfflineCleanup"/>).</summary>
    public const string CleanupArg = "--offline-cleanup";
    public const string SteamFirst = "Start Steam first (offline mode is fine).";
    const string TempSuffix = ".scskiller-new";

    // game id -> the process a session started, while it runs: its attestation is never disarmed (it admits no other process)
    readonly ConcurrentDictionary<string, SafeProcessHandle> _offline = new();
    // game id -> a session whose files may still be in the game folder; null until the data folder was read once
    ConcurrentDictionary<string, bool>? _offlineLeft;

    /// <summary>A session's process runs: this app's, or one an earlier run started (its pid and creation time recorded),
    /// which is still loading when SCSKiller opens again. Its attestation is never revoked meanwhile. Reads a record only for
    /// a game with a session pending (the first scan or watcher pass finds them): every disarm asks, and a record may be held.</summary>
    bool OfflineLive(string gameId) =>
        _offline.ContainsKey(gameId) || _offlineLeft?.ContainsKey(gameId) == true && Store.LoadGame(gameId).OfflineSession is { Resumed: true } s && Alive(s);

    // updates applying (Updater) and offline sessions starting: one excludes the other
    readonly object _updateGate = new();
    int _updating, _offlineStarting;
    public const string UpdateInstalling = "An update is being installed; try again after it.";

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

    /// <summary>The exe that runs <see cref="CleanupArg"/> (the app's own); null: no helper and no logon entry (the CLI, tests).</summary>
    public string? CleanupHelper { get; set; }

    /// <summary>Whether a process other than the session's runs from its folders, so its data files wait. Replaceable for tests.</summary>
    internal Func<OfflineSession, bool> OfflineRuns { get; set; }

    /// <summary>A process of <paramref name="all"/> is named like an exe of the session's folders, other than the session's
    /// own (one that exited, still held open).</summary>
    internal static bool OthersRun(OfflineSession s, List<(int Pid, int Parent, string Exe)> all) =>
        ExeNamesIn([s.InstallDir, Path.GetDirectoryName(s.Exe)]) is not { } names   // can't tell: as if one runs
        || all.Any(p => p.Pid != s.Pid && names.Contains(Path.GetFileNameWithoutExtension(p.Exe)));

    public void SetOfflineRecording(string gameId, bool on)
    {
        lock (_recorderLock)
        {
            var s = Find(gameId);
            if (on && !s.OfflineEligible) throw new InvalidOperationException($"{s.Game.Name}: offline sessions without EasyAntiCheat aren't available for this game");
            var rec = Store.LoadGame(gameId);
            rec.OfflineRecord = on;
            Store.SaveGame(gameId, rec);
        }
        RefreshGame(gameId);
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
            var entry = OfflineEac.Of(g, s.AntiCheat, s.Engine);
            var appIdFile = Path.Combine(dir, SteamAppIdFile);
            var addAppId = !File.Exists(appIdFile);   // else the user's own, with this id: left as it is
            string[] placed = addAppId ? ["d3d12.dll", "scskiller.ini", SteamAppIdFile] : ["d3d12.dll", "scskiller.ini"];
            string[] created = [.. placed.SelectMany(f => new[] { f, f + TempSuffix }), ArmedFile, .. RecorderDataFiles, .. Recordings.AppFiles];
            string? Refusal()
            {
                if (entry == null) return "offline sessions without EasyAntiCheat aren't available for this game";
                if (GameVerdicts.Current.Unsupported(g, s.Engine) is { } why) return $"not supported: {why.Text}";
                if (!rec.OfflineRecord) return "allow offline sessions for this game first";
                if (OfflineLive(gameId) || rec.OfflineSession != null) return "the last offline session's files are still being removed";
                if (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null || rec.RecorderRollback
                    || RevocationPending(gameId))
                    return "a recorder's removal is still pending";
                lock (_lock)
                    if (_current == gameId) return "a compile of it is in progress";
                if (GameRunning(g)) return "it is running: close the game first";
                if (_proxyDll == null || !File.Exists(_proxyDll)) return "the recorder (d3d12.dll) wasn't found next to SCSKiller";
                // fresh, not the scan's: EasyAntiCheat and nothing else
                if (GameFiles.DetectAntiCheat(g) != AntiCheat.EasyAntiCheat || GameFiles.DetectAntiCheat(g, ignore: AntiCheat.EasyAntiCheat) != AntiCheat.None)
                    return "its install has another anti-cheat, or couldn't be read whole";
                // never alongside a mod: none of the names the session creates may be there
                if (RecorderOwnFiles.Concat(created).FirstOrDefault(f => Path.Exists(Path.Combine(dir, f))) is { } there) return $"{there} is already in its folder";
                if (!addAppId && File.ReadAllText(appIdFile).Trim() != entry.AppId) return $"its folder has a {SteamAppIdFile} with another app id";
                if (ReShade.Detect(g) is { } mod) return $"ReShade ({Path.GetFileName(mod.Dll)}) is in its folder";
                if (!ProcessNames().Contains("steam")) return SteamFirst;   // else the game restarts through Steam, with EasyAntiCheat
                return null;
            }
            if (Refusal() is { } why) throw new InvalidOperationException(why == SteamFirst ? why : $"{g.Name}: no offline session: {why}");
            var original = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var ini = IniText(g, rec);
            // the journal first: whatever happens after, every cleanup knows what to take out
            rec.RecorderFiles = new() { ["d3d12.dll"] = ProxySha()!, ["scskiller.ini"] = Hex(ini) };
            if (addAppId) rec.RecorderFiles[SteamAppIdFile] = Hex(entry!.AppId);
            var session = new OfflineSession(gameId, g.ExePath, g.InstallDir, original, created);
            (rec.RecorderExe, rec.RecorderInstallDir, rec.OfflineSession) = (g.ExePath, g.InstallDir, session);
            Store.SaveGame(gameId, rec);
            OfflineLeft()[gameId] = true;
            RecorderLog($"{g.Name}: offline session without EasyAntiCheat: recorder in ({dir})");
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
                Place(Path.Combine(dir, "d3d12.dll"), temp => File.Copy(_proxyDll!, temp));
                Place(Path.Combine(dir, "scskiller.ini"), temp => WriteNew(temp, ini));
                if (addAppId) Place(Path.Combine(dir, SteamAppIdFile), temp => WriteNew(temp, entry!.AppId));
                if (GameFiles.DetectAntiCheat(g, quick: true, ignore: AntiCheat.EasyAntiCheat) is not AntiCheat.None and var other)
                    throw new InvalidOperationException($"{other} appeared in its folder");
                DeleteRevocationMark(g.ExePath);   // an earlier revocation's mark: the proxy would refuse on it
                process = StartAttested(g.ExePath, OfflineArguments, (h, pid, started) =>
                {
                    _offline[gameId] = h;
                    var now = Store.LoadGame(gameId);
                    if (now.OfflineSession == null) throw new InvalidOperationException("its session was cleaned up meanwhile");
                    now.OfflineSession = session = session with { Pid = pid, Started = started };
                    Store.SaveGame(gameId, now);
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
            return Task.Run(() => EndOffline(g, process!));
        }
    }

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
            File.Move(temp, path, replace);
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

    /// <summary>The cleanup, the moment the process exits (its handle, not the watcher, which lags a few polls).</summary>
    async Task EndOffline(Game g, SafeProcessHandle process)
    {
        try { await Task.Factory.StartNew(() => WaitForSingleObject(process, uint.MaxValue), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
        finally
        {
            _offline.TryRemove(g.Id, out _);
            process.Dispose();
        }
        RecorderLog($"{g.Name}: the offline session's game exited");
        if (!CleanOffline(g.Id)) RecorderLog($"{g.Name}: the offline session's files that are left go when nothing runs from the game folder");
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
    /// while its process runs: its attestation revoked; the names it journaled that a launch loads deleted first; then the
    /// recorder's data files, once its recording is merged, which waits while another process <paramref name="runs"/> from
    /// the folder. A file goes by name only under SCSKiller's own names; d3d12.dll only when it is our proxy (its export or
    /// the hash the session recorded), steam_appid.txt only with the bytes the session wrote. A name is dropped from the
    /// journal once the folder, listed whole, shows it gone, or holds a file that isn't ours there (a mod, the user's own:
    /// left), so a retry never deletes a file put there later. The session stays recorded, and its
    /// logon entry, until the journal is empty; then the folder's names are compared with the ones it had before (a
    /// difference is logged, nothing else deleted). Null when nothing of it is left, else what is.</summary>
    internal static List<string>? CleanOfflineSession(AppStore store, string id, Func<OfflineSession, bool> runs, Action<string> log)
    {
        using var gate = new AppStore.PathGate(Path.Combine(store.GameDir(id), "offline"));
        var rec = store.LoadGame(id);
        if (rec.OfflineSession is not { } s)
        {
            RunOnce(id, null);
            return null;
        }
        var name = OfflineEac.Current.FirstOrDefault(e => e.Id == id)?.Name ?? id;
        if (Alive(s))
        {
            // never resumed: our own child, still suspended (SCSKiller ended while it set the session up)
            if (s.Resumed || !EndSuspended(s)) return [.. s.Created];
            log($"{name}: the offline session's process {s.Pid} was never resumed: ended");
        }
        var dir = Path.GetDirectoryName(s.Exe)!;
        RevokeLedgers(s.Exe);
        var data = new HashSet<string>([.. RecorderDataFiles, .. Recordings.AppFiles], StringComparer.OrdinalIgnoreCase);
        // the folder's names, listed whole; null when it is gone or can't be read: nothing is known gone then
        HashSet<string>? Listed()
        {
            try { return Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        }
        void Delete(IEnumerable<string> files)
        {
            foreach (var file in files)
                try { File.Delete(Path.Combine(dir, file)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        // a held armed file that Revoke renamed aside is one the session created too
        var aside = Listed()?.Where(f => f.StartsWith(ArmedFile + ".", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".revoked", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        // null: not read (gone, or unreadable now): never deleted, kept in the journal while it is there
        bool? Ours(string file)
        {
            if (file.StartsWith("scskiller", StringComparison.OrdinalIgnoreCase) || file.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) return true;
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) return null;
            try { return file is "d3d12.dll" or "d3d11.dll" && IsOurProxy(path) || rec.RecorderFiles.TryGetValue(file, out var hash) && Sha256(path) == hash; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        var ours = s.Created.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(f => f, Ours, StringComparer.OrdinalIgnoreCase);
        foreach (var (file, _) in ours.Where(o => o.Value == false)) log($"{name}: left {Path.Combine(dir, file)}: not SCSKiller's");
        Delete(s.Created.Where(f => !data.Contains(f) && ours[f] == true).Concat(aside));   // what a launch loads goes first, before any wait for the recording
        // each name confirmed gone, or not ours, leaves the journal at once: one recreated later is never the session's
        void Retire()
        {
            if (Listed() is not { } listed) return;
            var kept = s.Created.Where(f => listed.Contains(f) && ours[f] != false).ToArray();
            if (kept.Length == s.Created.Length) return;
            rec.OfflineSession = s = s with { Created = kept };
            foreach (var file in rec.RecorderFiles.Keys.Where(f => !kept.Contains(f)).ToList()) rec.RecorderFiles.Remove(file);
            store.SaveGame(id, rec);
        }
        Retire();
        var inbox = new FileInfo(Path.Combine(dir, "scskiller.db"));
        var others = runs(s);
        if (!others && s.Created.Contains("scskiller.db") && inbox is { Exists: true, Length: > 0 })
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
        if (!others) Delete(s.Created.Where(data.Contains));
        Retire();
        var now = Listed();
        var gone = now == null && Gone(dir);   // the folder itself is gone (uninstalled, moved), its drive still there
        if (gone) (rec.RecorderFiles, now) = ([], []);
        var left = now == null ? [.. s.Created] : s.Created.Where(now.Contains).ToList();
        if (rec.RecorderFiles.Count == 0 && rec.RecorderExe == s.Exe) (rec.RecorderExe, rec.RecorderInstallDir) = (null, null);
        if (now == null || left.Count > 0 || now.Overlaps(aside))
        {
            store.SaveGame(id, rec);
            return now == null ? [dir] : [.. left, .. aside.Where(now.Contains)];
        }
        if (gone)
        {
            log($"{name}: offline session cleaned up: its game folder {dir} is gone");
            rec.OfflineSession = null;
            store.SaveGame(id, rec);
            RunOnce(id, null);
            return null;
        }
        var added = now.Except(s.Original, StringComparer.OrdinalIgnoreCase).Order().ToList();
        var missing = s.Original.Except(now, StringComparer.OrdinalIgnoreCase).Order().ToList();
        log(added.Count + missing.Count == 0
            ? $"{name}: offline session cleaned up: its folder has the {s.Original.Length} entries it had before"
            : $"{name}: offline session cleaned up; its folder differs from before (left as it is):"
              + (added.Count > 0 ? $" new {string.Join(", ", added)}" : "") + (missing.Count > 0 ? $" gone {string.Join(", ", missing)}" : ""));
        rec.OfflineSession = null;
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

    /// <summary>Starts <paramref name="exe"/> in its folder, suspended; arms the recorder for that process alone (its pid and
    /// creation time in the attestation); then lets it run. <paramref name="started"/> gets the process, its pid and creation
    /// time before it is armed, <paramref name="resumed"/> a call once it runs. Nothing armed runs if a step fails: the
    /// process is ended.</summary>
    internal static SafeProcessHandle StartAttested(string exe, string args, Action<SafeProcessHandle, int, long>? started = null, Action? resumed = null)
    {
        var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var cmd = new StringBuilder($"\"{exe}\"" + (args.Length > 0 ? " " + args : ""));
        if (!CreateProcessW(exe, cmd, 0, 0, false, CreateSuspended, 0, Path.GetDirectoryName(exe)!, ref si, out var pi)) throw new Win32Exception();
        var process = new SafeProcessHandle(pi.Process, true);
        try
        {
            if (!GetProcessTimes(process, out var created, out _, out _, out _)) throw new Win32Exception();
            started?.Invoke(process, pi.ProcessId, created);
            WriteAttestation(exe, (pi.ProcessId, created));
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
