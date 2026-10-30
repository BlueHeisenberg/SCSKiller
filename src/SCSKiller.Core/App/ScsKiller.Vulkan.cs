using SCSKiller.Core.Games;
using SCSKiller.Core.Unity;
using SCSKiller.Core.Vulkan;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

// The Vulkan recorder of a game (VulkanLayer): its registration in the data folder and HKCU (on Linux the loader's
// implicit_layer.d), the signed scskiller.ini beside the exe, and the layer's inbox in the recorder folder. Nothing else of
// it is in the game's folder, but a risk session's copy of the layer (StartRisk).
public sealed partial class ScsKiller
{
    public const string SkipVulkanSteamOnly = "Vulkan games are recorded only from Steam for now";

    /// <summary>The game page's recorder line for a game the layer records: nothing goes in its folder but the ini.</summary>
    public const string VulkanRecorderNote = "Adds a Vulkan layer for this game and a small scskiller.ini next to it to catch anything the plan missed.";

    public const string SteamPrecachingNote = "stopped while Steam processes this game's shaders: it goes on after";

    public const string VulkanOtherNote = "another SCSKiller copy's Vulkan recorder is registered for this game, so this one isn't installed";

    public const string ElevatedSessionNote = "The game ran as administrator, where the Vulkan recorder can't load. Start it without admin rights.";

    /// <summary>A Vulkan session's note when the game ran on DirectX (<paramref name="api"/>, as <see cref="ApiName(string)"/>
    /// reads it); where the user's pick sent it to the layer, back to Auto.</summary>
    public static string RanOnNote(string api, bool picked) => $"The game ran on {ApiName(api)} in this session." + (picked ? " Set the API to Auto." : "");

    /// <summary>The risk confirmation's line where the user picked Vulkan and detection, the last Player.log included, found
    /// DirectX: the layer only sees a Vulkan device. Null: nothing goes against the pick.</summary>
    public static string? VulkanPickWarning(GameState s)
    {
        if (s is not { ApiOverride: DxApi.Vulkan, ApiDetected: { } detected } || ApiKey(detected) is not { } key || key == "vk") return null;
        var dx = key switch { "11" => "DirectX 11", "12" => "DirectX 12", _ => "DirectX" };
        return (detected.EndsWith("(last run)", StringComparison.Ordinal) ? $"The game last ran on {dx}." : $"SCSKiller detected {dx} for this game.")
            + " The recorder only loads if the game runs on Vulkan.";
    }

    /// <summary>What a Vulkan session's game ran on since <paramref name="since"/> (Windows): its Unity Player.log's API, else
    /// Vulkan when Steam's cache folder of its app (<see cref="SteamGlCache"/>, where Steam points the driver's Vulkan cache)
    /// got a file. Null: neither tells.</summary>
    string? RanApi(Game g, EngineInfo? e, DateTime since) =>
        (e?.Family == UnityReader.Family ? new UnityReader().RanSince(g, since) : null)
        ?? (SteamGlCache(g) is { } dir && WrittenSince(dir, since) ? "Vulkan" : null);

    internal static bool WrittenSince(string dir, DateTime since)
    {
        try
        {
            return Directory.Exists(dir) && new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Any(f => f.LastWriteTimeUtc >= since);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public const string VulkanSettingsNote = "another program's Vulkan loader settings (vk_loader_settings.json) are in use, so the Vulkan recorder isn't installed";

    /// <summary>A game on Vulkan with a layer key (<see cref="VulkanLayer.Key"/>), on a vendor with a Vulkan warm: its recorder is the layer.</summary>
    bool VulkanRoute(Game g, EngineInfo? e) => VulkanLayer.Key(g) != null && e != null && VulkanPlan.IsVulkan(e) && Vendor.Caps.VulkanKey != null;

    /// <summary>A DirectX game whose exe loads vulkan-1.dll too (the carver's <see cref="EngineInfo.OffersVulkan"/>), with no
    /// API picked: it runs either, so the layer goes in beside the DirectX recorder (<see cref="ReconcileLayerBeside"/>) and
    /// a recording tells which it ran (<see cref="WithApiChoice"/>).</summary>
    bool BothApis(GameState s, GameRecord rec) => OperatingSystem.IsWindows() && rec.ApiOverride == null && s.Engine is { OffersVulkan: true } e
        && Carved.CarvedReader.Carved(e) && ApiKey(s.ApiDetected ?? e.GraphicsApi) is { } detected && detected != "vk"
        && VulkanLayer.Key(s.Game) != null && Vendor.Caps.VulkanKey != null;

    /// <summary>Why this copy's layer can't be registered for the game: another copy's registration for it, another
    /// program's loader settings file, or a loader too old for one. Null: it can.</summary>
    static string? VulkanBlocked(Game g, string key, string manifest) =>
        VulkanLayer.Others(key, manifest).Count > 0 ? VulkanLayer.AppId(g) != null ? VulkanOtherNote : VulkanSettingsNote
        : VulkanLayer.AppId(g) == null ? VulkanLayer.LoaderProblem(g.ExePath) : null;

    /// <summary>The game's Vulkan recorder is registered for its exe's folder: its scskiller.ini is the recorder's.</summary>
    static bool VulkanIn(Game g, GameRecord rec) => rec.VulkanLayer != null && rec.RecorderExe is { } exe
        && SameDir(Path.GetDirectoryName(exe)!, Path.GetDirectoryName(g.ExePath)!);

    string VulkanManifest(string key) => Path.Combine(Store.DataDir, VulkanLayer.Folder, key, VulkanLayer.ManifestName(key));

    /// <summary>The layer the app ships, beside its proxy (native\ next to the app; on Linux native/ too, found alone when
    /// there's no proxy).</summary>
    string? VulkanLayerSource => _proxyDll is { } p ? Path.Combine(Path.GetDirectoryName(p)!, VulkanLayer.Dll) : OperatingSystem.IsWindows() ? null : NativeTools.Find(VulkanLayer.Dll);

    /// <summary>The layer's inbox for the game's exe as it is now; null when its path can't be used.</summary>
    static string? VulkanInboxDir(Game g)
    {
        try { return VulkanLayer.Inbox(RecorderDir, FileKey(g.ExePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Every inbox the game's layer may have written: its exe's now and the ones the exe had while it recorded
    /// (Steam replacing the exe gives it another file key).</summary>
    static List<string> VulkanInboxDirs(Game g, GameRecord rec) =>
        [.. rec.VulkanInboxes.Append(VulkanInboxDir(g)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];

    static IEnumerable<string> VulkanInboxes(Game g, GameRecord rec) =>
        VulkanInboxDirs(g, rec).Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, VulkanPlan.InboxPattern));

    /// <summary>The layer's output for the game: its inboxes' archives and session files.</summary>
    IEnumerable<string> VulkanFiles(Game g)
    {
        var rec = Store.LoadGame(g.Id);
        return [.. VulkanInboxes(g, rec), .. VulkanInboxDirs(g, rec).SelectMany(d => SessionFiles.Select(f => Path.Combine(d, f))).Where(File.Exists)];
    }

    /// <summary>Merges the layer's archives into the game's <see cref="FozPath"/> (<see cref="VulkanPlan.Import"/>), keeps the
    /// last session's driver-cache variables for the warm (<see cref="VulkanLayer.CacheEnv"/>) and drops an earlier exe's
    /// inbox once it is imported. True when it added pipelines.</summary>
    bool ImportVulkan(Game g, GameRecord rec, CancellationToken ct)
    {
        if (Collections.RecordedBy(g.Id) != null || Foreign(g)) return false;
        var now = VulkanInboxDir(g);
        var dirs = VulkanInboxDirs(g, rec);
        // the newest session's, also when it is an earlier exe's inbox (Steam replaced the exe since): read before it goes
        var csv = dirs.Select(d => Path.Combine(d, "scskiller_creates.csv")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        Dictionary<string, string>? env = null;
        string? device = null;
        try { (env, device) = csv == null ? (null, null) : (VulkanLayer.CacheEnv(csv), VulkanLayer.Device(csv) ?? rec.VulkanDevice); }   // a launch that made no device keeps the last one
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { csv = null; }   // unread says nothing: the record's stays
        if (csv != null && (!SameEnv(env, rec.VulkanCacheEnv) || device != rec.VulkanDevice))
        {
            var moved = !SameEnv(env, rec.VulkanCacheEnv);
            (rec.VulkanCacheEnv, rec.VulkanDevice) = (env, device);
            if (Vendor.Caps.VulkanKey == VulkanPlan.DeviceKey) rec.RecordingImportedAt = DateTimeOffset.Now;   // its plan's check reads both
            Store.SaveGame(g.Id, rec);   // also when no pipeline is new, and before the inbox it came from goes
            if (moved) Log?.Report($"{g.Name}: " + (env == null ? "the last launch's Vulkan cache isn't redirected" : "the last launch's Vulkan cache is redirected (Steam's shader pre-caching)"));
        }
        var added = new List<string>();
        if (dirs.Any(d => Directory.Exists(d) && Directory.EnumerateFiles(d, VulkanPlan.InboxPattern).Any()))
            using (Recordings.Lock(RecordingPath(g.Id), ct: ct))
                foreach (var d in dirs)
                    added.AddRange(VulkanPlan.Import(d, FozPath(g.Id), Log));
        foreach (var old in dirs.Where(d => !d.Equals(now, StringComparison.OrdinalIgnoreCase)))
            try
            {
                if (Directory.Exists(old) && Directory.EnumerateFiles(old, VulkanPlan.InboxPattern).Any()) continue;   // still held: next time
                if (!OperatingSystem.IsWindows() && VulkanPlan.Held(Path.Combine(old, "scskiller_creates.csv"))) continue;   // a recording that made no archive yet
                if (Directory.Exists(old)) Directory.Delete(old, true);
                rec.VulkanInboxes.Remove(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (added.Count == 0) return false;
        rec.RecordingImportedAt = DateTimeOffset.Now;
        Log?.Report($"{g.Name}: {added.Count:N0} Vulkan pipelines recorded");
        return true;
    }

    /// <summary>A started game with the Vulkan recorder in: whether its process runs as administrator, which skips HKCU's
    /// layers and settings (the loader's is_high_integrity), kept on the record until a launch is seen without it. Its own
    /// process alone is opened (<see cref="RunningExe"/>), never an anti-cheat game's.</summary>
    void CheckElevated(GameState s)
    {
        if (!OperatingSystem.IsWindows() || s.AntiCheat != AntiCheat.None || IsUnchecked(s) || !VulkanRoute(s.Game, s.Engine)) return;
        var rec = Store.LoadGame(s.Game.Id);
        if (rec.VulkanLayer == null) return;
        bool? elevated;
        try
        {
            using var process = RunningExe(s.Game, out _, out _);
            elevated = process == null ? null : Elevated.OfProcess(process);
        }
        catch (System.ComponentModel.Win32Exception) { return; }
        if (elevated is not { } now || now == (rec.RanElevated != null)) return;
        rec.RanElevated = now ? DateTimeOffset.Now : null;
        Store.SaveGame(s.Game.Id, rec);
        if (rec.RanElevated != null) RecorderLog($"{s.Game.Name}: {ElevatedSessionNote}");
    }

    static bool SameEnv(Dictionary<string, string>? a, Dictionary<string, string>? b) =>
        a == null ? b == null : b != null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Steam's own replay of a game's Vulkan shaders (shader pre-caching) runs: a warm into the cache it fills waits.
    /// Steam's Linux replayer is fossilize_replay. Our own replayer runs under the game's exe name, or natively on Linux as
    /// fossilize-replay.
    /// ponytail: any game's replay holds every Vulkan warm; the replayer's command line names the app id if that's too broad.</summary>
    bool SteamReplaying() => Processes(true).Any(p => Path.GetFileName(p.Exe) is var n
        && (n.Equals("fossilize-replay64.exe", StringComparison.OrdinalIgnoreCase) || n.Equals("fossilize-replay.exe", StringComparison.OrdinalIgnoreCase)
            || n == "fossilize_replay"));

    /// <summary><see cref="Reconcile"/> for a game on <see cref="VulkanRoute"/>: the layer registered and the ini signed while
    /// the game's recorder is wanted, both out otherwise. A running game's change waits for it to exit.</summary>
    bool ReconcileVulkan(GameState s, GameRecord rec, bool changed)
    {
        var (g, id) = (s.Game, s.Game.Id);
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var key = VulkanLayer.Key(g);
        if (rec.VulkanLayer != null && !OursUnder(rec.VulkanLayer, Store))   // a record copied from another data folder: its registration is that copy's
        {
            rec.VulkanLayer = null;
            Store.SaveGame(id, rec);
        }
        var manifest = rec.VulkanLayer ?? (key == null ? "" : VulkanManifest(key));
        // a dll beside the exe that may be ours, or the layer's files, can't be read: anti-cheat first, then nothing until they read
        var unread = ProxyNames.Select(n => Path.Combine(dir, n))
            .Concat(rec.VulkanLayer == null ? [] : [manifest, Path.Combine(Path.GetDirectoryName(manifest)!, VulkanLayer.Dll)]).FirstOrDefault(CantRead);
        string? stamp = null, note = null;
        long gen = 0;
        var clean = false;
        if (s.AntiCheat == AntiCheat.None && (rec.VulkanLayer != null || rec.RecorderExe != null || unread != null))
        {
            (stamp, gen) = (Stamp(ArmTarget(g).ExePath), RemovalGen(id));
            if (FullCheck(g) is not AntiCheat.None and var found)
            {
                AntiCheatFound(g, found ?? AntiCheat.Other, unfinished: found == null);   // takes it out, or when the game exits
                Refresh(g);
                return true;
            }
            clean = true;
        }
        if (unread != null)
        {
            note = $"waits until {unread} can be read";
            if (note != _recorderNotes.GetValueOrDefault(id))
            {
                (_recorderNotes[id], changed) = (note, true);
                RecorderLog($"{g.Name}: recorder {note}");
            }
            if (changed) Refresh(g);
            return changed;
        }
        // off the route (the game runs on DirectX now, or has no layer key): out, and the next reconcile is the DirectX one
        var want = !LeftOut(g) && VulkanRoute(g, s.Engine) && RecorderWanted(rec.Recorder ?? RecorderOverride.Default, Settings.RecordAllGames && s.NoStutter == null, RecorderSkip(s, null), false);
        if (want && VulkanBlocked(g, key!, manifest) is { } blocked) (want, note) = (false, blocked);
        else if (want && rec.RanElevated != null) note = ElevatedSessionNote;   // the loader skips HKCU's layers and settings for it
        var placed = rec.VulkanLayer != null;
        var src = VulkanLayerSource;
        // a copy that changed, or an install cut off before its registration (or after, by a crash): done again
        var update = want && placed && (src != null && File.Exists(src) && !SameBytes(Path.Combine(Path.GetDirectoryName(manifest)!, VulkanLayer.Dll), src)
                                        || !File.Exists(manifest) || !VulkanLayer.Registered(manifest, g) || !VulkanIn(g, rec));   // the last: the exe moved from a folder that's gone
        // the DirectX recorder of an earlier evaluation (the game was on DirectX then): out first
        var proxy = rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || OurProxyIn(dir);
        if (want != placed || update || proxy || !want && rec.RecorderExe != null)
        {
            if (GameRunning(g)) note ??= update ? "updates when the game exits" : want ? "installs when the game exits" : "removed when the game exits";
            else
                try
                {
                    if (proxy && want && !Uninstall(g, rec)) throw new InvalidOperationException("the DirectX recorder in its folder couldn't be taken out yet");
                    if (want)
                    {
                        (stamp, gen) = (Stamp(ArmTarget(g).ExePath), RemovalGen(id));
                        InstallVulkan(g, rec, manifest);
                        if (!Attest(ArmTarget(g), rec, stamp, gen)) note = ForeignIni(dir, rec);
                    }
                    else Uninstall(g, rec);
                    _recorderSkips.TryRemove(id, out _);
                    KeepFailure(id, rec, null);
                    RecorderLog($"{g.Name}: Vulkan recorder {(update ? "updated" : want ? "installed" : "removed")} ({dir})");
                    changed = true;
                }
                catch (UnauthorizedAccessException)
                {
                    if (_recorderSkips.GetValueOrDefault(id) != SkipNeedsAdmin)
                    {
                        _recorderSkips[id] = SkipNeedsAdmin;
                        RecorderLog($"{g.Name}: Vulkan recorder not installed: {SkipNeedsAdmin} ({dir})");
                        changed = true;
                    }
                }
                catch (RemovedFromAllException e) { note = e.Message; }
                catch (Exception e) when (e is IOException or InvalidOperationException or System.Security.SecurityException)
                {
                    note = (want ? "couldn't install: " : "couldn't remove: ") + e.Message;
                    RecorderLog($"{g.Name}: Vulkan recorder {note}");
                    note = want ? InstallFailed(g, e, update) : "couldn't remove: " + FailureReason(e, dir);
                }
        }
        else if (want && !GameRunning(g) && _removedFromAll) note = RemovedFromAllNote;   // a registration the removal left: never armed again
        else if (want && !GameRunning(g))
        {
            if (src != null && File.Exists(src)) changed |= KeepFailure(id, rec, null);   // in place and current, its source still there
            if (clean && !File.Exists(Path.Combine(dir, "scskiller.ini"))) UpdateRecorderIni(g, rec, "");
            if (clean && !Attest(ArmTarget(g), rec, stamp, gen)) note = ForeignIni(dir, rec);
            else UpdateRecorderIni(g, rec);
        }
        if (want && VulkanInboxDir(g) is { } inbox && !rec.VulkanInboxes.Contains(inbox, StringComparer.OrdinalIgnoreCase))
        {
            rec.VulkanInboxes.Add(inbox);   // the exe's file key while it records: Steam replacing the exe leaves it to import
            Store.SaveGame(id, rec);
        }
        if (!want) KeepFailure(id, rec, null);
        if (note != _recorderNotes.GetValueOrDefault(id))
        {
            if (note == null) _recorderNotes.TryRemove(id, out _); else _recorderNotes[id] = note;
            changed = true;
        }
        if (changed) Refresh(g);
        return changed;
    }

    /// <summary>The layer's copy and manifest in the data folder, on the record before they're registered, then the ini beside
    /// the exe (unsigned: <see cref="Attest"/> signs it). The full anti-cheat check follows; a finding takes it all out.</summary>
    void InstallVulkan(Game g, GameRecord rec, string manifest)
    {
        if (_removedFromAll) throw new RemovedFromAllException();
        if (!RecorderInstallAllowed(Store.DataDir, AppStore.Moved, Environment.GetEnvironmentVariable("SCSKILLER_TEST_RECORDER")))
            throw new InvalidOperationException("the data folder is moved (SCSKILLER_DATA_DIR), which SCSKiller's uninstall doesn't know: set SCSKILLER_TEST_RECORDER=1 to install a recorder anyway, and take it out yourself");
        RefuseAntiCheat(g, rec);
        if (!GameFolderWrite(g)) throw new InvalidOperationException($"{g.Name} is running");
        var src = VulkanLayerSource is { } p && File.Exists(p) ? p : throw new FileNotFoundException($"the Vulkan recorder ({VulkanLayer.Dll}) was not found next to the app");
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var ini = Path.Combine(dir, "scskiller.ini");
        var (iniState, judged) = JudgeOwnIni(g, rec, dir, null);
        if (iniState == IniState.Unknown) throw new IOException($"{ini} can't be read: not installing until it can");
        if (iniState is not (IniState.Absent or IniState.Ours)) throw new InvalidOperationException($"{ini} is not SCSKiller's: the Vulkan recorder needs its own. Not installing.");
        (rec.RecorderExe, rec.RecorderInstallDir) = (g.ExePath, g.InstallDir);
        StampLedger(dir);   // the layer finds this copy's recorder folder through it
        PlaceLayer(g, rec, manifest, src);
        var late = false;
        var antiCheat = GameFiles.DetectAntiCheat(g, quick: true, late: () => late = true);
        if (antiCheat == AntiCheat.None && !WriteIni(dir, IniText(g, rec), judged, null, g.InstallDir, () => GameFolderWrite(g),
                step => { if (step == "saved") InstallStep?.Invoke("ini"); }, install: true) && !GameFolderWrite(g))
        {
            TakeOut(g, rec, "the game started while the Vulkan recorder was installed");
            throw new InvalidOperationException($"{g.Name} started while the recorder was installed");
        }
        if (antiCheat == AntiCheat.None) antiCheat = GameFiles.DetectAntiCheat(g, late: () => late = true);
        if (antiCheat != AntiCheat.None)
        {
            AntiCheatFound(g, antiCheat, installing: rec, unfinished: late);
            throw AntiCheatRefusal(g, antiCheat, late);
        }
        lock (_scanLock) _scan?.Remove(g.Id);   // the next refresh evaluates the game again, its anti-cheat too
    }

    /// <summary>The layer's copy and manifest in the data folder, on the record before they're registered.</summary>
    void PlaceLayer(Game g, GameRecord rec, string manifest, string src)
    {
        var folder = Path.GetDirectoryName(manifest)!;
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, VulkanLayer.Dll);
        Place(dll, CopyOf(src), replace: true);
        Place(manifest, Text(VulkanLayer.Manifest(VulkanLayer.Key(g)!, dll)), replace: true);
        (rec.VulkanLayer, rec.VulkanLayerSha) = (manifest, Sha256(dll));
        Store.SaveGame(g.Id, rec);   // before the registration: every removal finds it
        InstallStep?.Invoke("vulkan");
        VulkanLayer.Register(manifest, g);
    }

    /// <summary>For a game on <see cref="BothApis"/>: the layer registered while the DirectX recorder is in its exe's folder
    /// (<paramref name="armed"/>, which signed the ini the layer reads too), placed again when the app's copy changed, out
    /// otherwise. The note why it isn't registered, or null. A running game's change waits for it to exit.</summary>
    string? ReconcileLayerBeside(Game g, GameRecord rec, bool armed, ref bool changed)
    {
        if (GameRunning(g)) return null;
        if (!armed)
        {
            if (rec.VulkanLayer != null && UnregisterVulkan(rec, Store, g.Name, RecorderLog))
            {
                Store.SaveGame(g.Id, rec);
                RecorderLog($"{g.Name}: Vulkan recorder removed");
                changed = true;
            }
            return null;
        }
        if (_removedFromAll) return null;
        var manifest = VulkanManifest(VulkanLayer.Key(g)!);
        if (VulkanBlocked(g, VulkanLayer.Key(g)!, manifest) is { } blocked) return blocked;
        if (VulkanInboxDir(g) is { } inbox && !rec.VulkanInboxes.Contains(inbox, StringComparer.OrdinalIgnoreCase))
        {
            rec.VulkanInboxes.Add(inbox);   // as ReconcileVulkan: Steam replacing the exe leaves it to import
            Store.SaveGame(g.Id, rec);
        }
        var src = VulkanLayerSource;
        if (rec.VulkanLayer != null && File.Exists(manifest) && VulkanLayer.Registered(manifest, g)
            && !(src != null && File.Exists(src) && !SameBytes(Path.Combine(Path.GetDirectoryName(manifest)!, VulkanLayer.Dll), src))) return null;
        try
        {
            if (src == null || !File.Exists(src)) throw new FileNotFoundException($"the Vulkan recorder ({VulkanLayer.Dll}) was not found next to the app");
            var update = rec.VulkanLayer != null;
            PlaceLayer(g, rec, manifest, src);
            RecorderLog($"{g.Name}: Vulkan recorder {(update ? "updated" : "installed")} beside the DirectX one");
            changed = true;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            RecorderLog($"{g.Name}: Vulkan recorder couldn't install: {e.Message}");
            return "couldn't install the Vulkan recorder: " + FailureReason(e, Path.GetDirectoryName(g.ExePath)!);
        }
    }

    /// <summary>The record's layer registration out, with its copy and manifest when nothing holds them: first in every
    /// removal (<see cref="RemoveRecorder"/>), so no launch after it loads the layer. False when the registry refused.</summary>
    static bool UnregisterVulkan(GameRecord rec, AppStore store, string name, Action<string> log)
    {
        if (rec.VulkanLayer is not { } manifest) return true;
        if (!OursUnder(manifest, store))   // a record copied from another data folder: that copy's registration, left to it
        {
            rec.VulkanLayer = null;
            return true;
        }
        try { VulkanLayer.Unregister(manifest); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log($"{name}: couldn't unregister the Vulkan recorder ({e.Message}): tried again later");
            return false;
        }
        var (sha, folder) = (rec.VulkanLayerSha, Path.GetDirectoryName(manifest)!);
        (rec.VulkanLayer, rec.VulkanLayerSha) = (null, null);
        // only the bytes it placed: the copy by its hash, the manifest by its text, then the folder if nothing else is in it. A
        // risk session's manifest names its copy beside the exe, which goes with the record's files
        try
        {
            var dll = Path.Combine(folder, VulkanLayer.Dll);
            if (File.Exists(dll) && sha != null && Sha256(dll) == sha) File.Delete(dll);
            var appId = Path.GetFileNameWithoutExtension(manifest)["VkLayer_scskiller_".Length..];
            if (File.Exists(manifest) && File.ReadAllText(manifest) is var text && (text == VulkanLayer.Manifest(appId, dll)
                    || rec.RecorderExe is { } exe && text == VulkanLayer.Manifest(appId, Path.Combine(Path.GetDirectoryName(exe)!, VulkanLayer.Dll))))
                File.Delete(manifest);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }   // held by a running game: unregistered, it's inert
        return true;
    }

    /// <summary>This copy's layer registrations (<see cref="VulkanLayer.Under"/>) out when no record names them, their exe's folder
    /// isn't there (Steam took the whole folder) or their game has anti-cheat now, each logged with why: after every full
    /// reconcile and in every watcher pass, whether the game is listed or not. A session's own cleanup decides on its
    /// registration. A folder without its exe yet (an install under way) keeps it and is walked like any other.</summary>
    void SweepVulkan()
    {
        var check = new List<Game>();
        lock (_recorderLock)
        {
            List<string> registered;
            try { registered = VulkanLayer.Under(Path.Combine(Store.DataDir, VulkanLayer.Folder)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                RecorderLog($"the Vulkan recorders' registrations can't be read: {e.Message}");
                return;
            }
            foreach (var manifest in registered)
                try
                {
                    if (VulkanLayer.GameId(manifest) is not { } id)
                    {
                        if (UnregisterVulkan(new GameRecord { VulkanLayer = manifest }, Store, manifest, RecorderLog))
                            RecorderLog($"Vulkan recorder unregistered: no game is named for it ({manifest})");
                        continue;
                    }
                    if (OfflineLive(id)) continue;
                    var rec = Store.LoadGame(id);
                    if (rec.OfflineSession != null) continue;   // CleanOfflineSession: kept only while the session's process runs
                    var state = Games.FirstOrDefault(s => s.Game.Id == id);
                    var name = state?.Game.Name ?? id;
                    if (rec.VulkanLayer is not { } named || !named.Equals(manifest, GameFiles.Names))
                    {
                        if (UnregisterVulkan(new GameRecord { VulkanLayer = manifest }, Store, name, RecorderLog))
                            RecorderLog($"{name}: Vulkan recorder unregistered: no game record names it ({manifest})");
                        continue;
                    }
                    var dir = rec.RecorderExe is { } exe ? Path.GetDirectoryName(exe) : null;
                    if (dir == null || !Directory.Exists(dir))
                    {
                        var why = dir == null ? "its record names no game folder" : Gone(dir) ? $"its game folder {dir} is gone" : $"its game folder {dir} can't be reached";
                        if (dir == null) UnregisterVulkan(rec, Store, name, RecorderLog);
                        else RemoveRecorder(dir, rec, Store, name, RecorderLog, rec.RecorderInstallDir);
                        Store.SaveGame(id, rec);
                        if (rec.VulkanLayer == null) RecorderLog($"{name}: Vulkan recorder unregistered: {why}");
                        continue;
                    }
                    // a listed game's own reconcile and the watcher pass walk it already
                    if (state == null || IsUnchecked(state))
                        check.Add(state?.Game ?? new Game(id, name, VulkanLayer.ManifestAppId(manifest) != null ? Core.Store.Steam : Core.Store.Other, rec.RecorderInstallDir ?? dir, rec.RecorderExe!));
                }
                catch (Exception e)   // the next registration is swept all the same
                {
                    RecorderLog($"{manifest}: couldn't sweep the Vulkan recorder's registration: {e.Message}");
                }
        }
        foreach (var g in check)
            try
            {
                if (VacCheck(g)) TakeOutNow(g, "VAC found");
                else if (FullCheck(g) is not AntiCheat.None and var found) AntiCheatFound(g, found ?? AntiCheat.Other, unfinished: found == null);
            }
            catch (Exception e) { RecorderLog($"{g.Name}: checking the Vulkan recorder's game for anti-cheat failed: {e.Message}"); }
    }

    /// <summary>The file is there and can't be opened for reading (held against it, or this user may not read it).</summary>
    static bool CantRead(string path)
    {
        try
        {
            using (File.OpenRead(path)) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return File.Exists(path); }
    }

    /// <summary>The manifest is in <paramref name="store"/>'s own vulkan-layer folder.</summary>
    static bool OursUnder(string manifest, AppStore store)
    {
        try { return Path.GetFullPath(manifest).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(store.DataDir, VulkanLayer.Folder))) + Path.DirectorySeparatorChar, GameFiles.Names); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>NVIDIA's cache variables as Steam's shader pre-caching sets them for a game on Windows (measured): the driver
    /// writes the game's cache under Steam's nvidiav1 folder of its app, in files of this app name.</summary>
    const string SteamGlAppName = "steamapp_shader_cache";

    /// <summary>The variables a Vulkan warm runs under: the last recorded session's cache variables, else, on Windows for a game
    /// never recorded, Steam's (<see cref="SteamGlAppName"/>) once Steam made its NVIDIA folder. None: the driver's own cache.
    /// Where the vendor keys on the device (<see cref="VulkanPlan.DeviceKey"/>), the game's recorded device too.
    /// ponytail: the folder stands in for Steam's pre-caching setting, read Steam's config if a game with it off keeps one.</summary>
    Dictionary<string, string>? VulkanWarmEnv(Game g, GameRecord rec)
    {
        // the NVIDIA driver writes nothing when the folder its variable names is missing (measured), and Steam makes AMD's only after a Vulkan run on AMD
        if (SteamVulkanDir(g, rec) is { } steam && !Directory.Exists(steam))
            try
            {
                Directory.CreateDirectory(steam);
                Log?.Report($"{g.Name}: made Steam's Vulkan cache folder {steam} for the compile");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: Steam's Vulkan cache folder {steam} couldn't be made: {e.Message}"); }
        if (Vendor.Caps.VulkanKey == VulkanPlan.DeviceKey && rec.VulkanDevice is { } device)   // Steam's layer is only in a Steam launch
            return new(rec.VulkanCacheEnv ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
                { [VulkanPlan.DeviceVariable] = g.Store == Core.Store.Steam ? VulkanPlan.UnderSteamLayer(device) : device };
        if (rec.VulkanCacheEnv != null || !OperatingSystem.IsWindows() || Vendor.Vendor != GpuVendor.Nvidia || File.Exists(FozPath(g.Id))
            || SteamGlCache(g) is not { } gl || Path.GetDirectoryName(gl) is not { } nv || !Directory.Exists(nv)) return rec.VulkanCacheEnv;
        Log?.Report($"{g.Name}: the compile writes Steam's Vulkan cache of the game in {nv}");
        return SteamCacheVars(GpuVendor.Nvidia, nv);
    }

    /// <summary>The variables Steam's launches set for the app's folder <paramref name="dir"/> (<see cref="VulkanPlan.SteamCache"/>), as recorded.</summary>
    static Dictionary<string, string> SteamCacheVars(GpuVendor vendor, string dir) => vendor == GpuVendor.Nvidia
        ? new(StringComparer.OrdinalIgnoreCase)
        {
            ["__GL_SHADER_DISK_CACHE_PATH"] = dir, ["__GL_SHADER_DISK_CACHE_APP_NAME"] = SteamGlAppName,
            ["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = "steam_shader_cache;steamapp_merged_shader_cache", ["__GL_SHADER_DISK_CACHE_SKIP_CLEANUP"] = "1",
        }
        : new(StringComparer.OrdinalIgnoreCase) { ["AMD_VK_USE_PIPELINE_CACHE"] = "1", ["AMD_VK_PIPELINE_CACHE_PATH"] = dir, ["AMD_VK_PIPELINE_CACHE_FILENAME"] = SteamGlAppName };

    /// <summary>On Windows, for a Steam game with DXVK's d3d11.dll: Steam's folder of its app that this vendor's Vulkan cache
    /// variable names. Steam sets it for every launch, so the game's DXVK reads that folder's cache, keyed on the app alone
    /// (measured). Null otherwise. ponytail: a launch from outside Steam reads the vendor's own cache instead, record #cache
    /// for D3D11 games if those need covering too.</summary>
    string? DxvkSteamDir(Game g)
    {
        if (!OperatingSystem.IsWindows() || VulkanPlan.SteamCache(OwnDriver) is not (_, { } tail) || SteamGlCache(g) is not { } dir
            || Warmer.DxvkDll(OwnDriver, g.ExePath, steam: true) == null) return null;
        for (var up = tail.Length; up > 1; up--) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    /// <summary>A DirectX 11 warm's variables: Steam's (<see cref="DxvkSteamDir"/>), its folder made first as the driver writes
    /// nothing without it. Kept when the folder can't be made: the driver's own cache isn't what the game reads. Null: the
    /// driver's own cache.</summary>
    Dictionary<string, string>? DxvkWarmEnv(Game g)
    {
        if (DxvkSteamDir(g) is not { } dir) return null;
        try { Directory.CreateDirectory(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: Steam's Vulkan cache folder {dir} couldn't be made: {e.Message}"); }
        Log?.Report($"{g.Name}: the compile writes Steam's Vulkan cache of the game in {dir}");
        return SteamCacheVars(OwnDriver, dir);
    }

    /// <summary>The game's driver-cache bytes: its keys' files (NVIDIA: its <see cref="LiveKeys"/>') and its files in Steam's
    /// GLCache folder (<see cref="SteamGlFiles"/>). Null: none known.</summary>
    long? CacheOnDiskOf(Game g, EngineInfo? engine, GameRecord rec)
    {
        long? keyed = rec.CacheKeys.Count > 0 && AppCache != null ? AppCache.SizeOf(AppCache is Vendors.NvidiaAppCache ? LiveKeys(rec) : rec.CacheKeys) : null;
        return SteamGlFiles(g, engine) is { Count: > 0 } steam ? (keyed ?? 0) + steam.Sum(f => f.Length) : keyed;
    }

    /// <summary>NVIDIA: of <see cref="CacheOnDiskOf"/>, what its keys' files hold (<see cref="Vendors.NvidiaAppCache.UsedOf"/>). Null
    /// on other vendors or without keys.</summary>
    long? CacheInUseOf(Game g, EngineInfo? engine, GameRecord rec) =>
        AppCache is Vendors.NvidiaAppCache nv && rec.CacheKeys.Count > 0 ? nv.UsedOf(LiveKeys(rec)) + SteamGlFiles(g, engine).Sum(f => f.Length) : null;

    /// <summary>The keys the game's own process or the last complete warm held open, once both are known, else every learned
    /// one. The others only an earlier compile opened: the game doesn't read them.</summary>
    static IEnumerable<string> LiveKeys(GameRecord r) =>
        r.GameKeys.Count > 0 && r.WarmedKeys is { } warmed ? r.CacheKeys.Where(k => r.GameKeys.Contains(k) || warmed.Contains(k)) : r.CacheKeys;

    /// <summary>NVIDIA: forgets the DXCache keys outside <see cref="LiveKeys"/> whose files are gone (a Clear cache, the driver's
    /// cleanup), by a listing of the folder that didn't fail. Returns whether any went.</summary>
    bool PruneGoneKeys(GameRecord r)
    {
        if (AppCache is not Vendors.NvidiaAppCache nv) return false;
        // Vulkan's, older drivers' and other vendors' keys aren't 8 hex digits: their files are elsewhere
        var old = r.CacheKeys.Except(LiveKeys(r)).Where(k => k.Length == 8 && k.All(Uri.IsHexDigit)).ToList();
        if (old.Count == 0) return false;
        HashSet<string> present;
        try { present = Directory.EnumerateFiles(nv.Dir).Select(f => Vendors.NvidiaAppCache.Key(Path.GetFileName(f))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        return r.CacheKeys.RemoveWhere(k => old.Contains(k) && !present.Contains(k)) > 0;
    }

    /// <summary>A Vulkan game's files in Steam's cache folder of its app for this vendor (<see cref="SteamGlCache"/>), where Steam's
    /// pre-caching points the driver: what the game and the warm write (<see cref="SteamGlAppName"/>, also AMD's file name) and
    /// Steam's download and merge. Nothing else of Steam's folder, and none through a link.</summary>
    List<FileInfo> SteamGlFiles(Game g, EngineInfo? engine)
    {
        if (!VulkanRoute(g, engine) || SteamGlCache(g) is not { } dir || GameFiles.FinalPath(dir) is not { } final
            || !SamePath(final, dir)) return [];
        try
        {
            return [.. new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(f => f.Name.StartsWith(SteamGlAppName, StringComparison.OrdinalIgnoreCase) || Vendors.AppCacheFiles.SteamOwn(f.Name))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>On Windows, Steam's folder of the game's app that this vendor's Vulkan cache variable names (<see cref="VulkanPlan.SteamCache"/>),
    /// when the game's recorded session ran with it there, so its warm writes there too. Null: elsewhere or none.</summary>
    string? SteamVulkanDir(Game g, GameRecord rec)
    {
        if (!OperatingSystem.IsWindows() || VulkanPlan.SteamCache(OwnDriver) is not (_, { } tail) || SteamGlCache(g) is not { } dir || VulkanPlan.CachePath(rec.VulkanCacheEnv, OwnDriver) is not { } path) return null;
        for (var up = tail.Length; up > 1; up--) dir = Path.GetDirectoryName(dir)!;
        return SamePath(path, dir) ? dir : null;
    }

    /// <summary>The last warm wrote Steam's folder (<see cref="SteamVulkanDir"/>) and none of the game's files are left there:
    /// Steam cleared its shader cache. On AMD a .parc without an entry counts as none (<see cref="Vendors.AmdAppCache.HasEntries"/>).
    /// Null while the folder or anything in it is a link, or it or a .parc can't be read.</summary>
    string? SteamVulkanGone(string gameId, GameRecord r)
    {
        if (r.WarmedAt == null || Games.FirstOrDefault(s => s.Game.Id == gameId) is not { } s || !VulkanRoute(s.Game, s.Engine) || SteamVulkanDir(s.Game, r) is not { } dir) return null;
        try
        {
            if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint)) return null;
            var found = false;
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
                if (e is not FileInfo || !e.Name.StartsWith(SteamGlAppName, StringComparison.OrdinalIgnoreCase) && !Vendors.AppCacheFiles.SteamOwn(e.Name)) continue;
                if (OwnDriver != GpuVendor.Amd || !e.Extension.Equals(".parc", StringComparison.OrdinalIgnoreCase)) found = true;
                else if (Vendors.AmdAppCache.HasEntries(e.FullName) is { } entries) found |= entries;
                else return null;
            }
            return found ? null : TrimmedAllReason;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return TrimmedAllReason; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>For Clear cache's delete of <see cref="SteamGlFiles"/>: the game's lock, after the Linux placer took its
    /// entries out of Steam's files. InvalidOperationException while a compile or a placement runs, or a before and after
    /// recording has the folder set aside.</summary>
    FileStream SteamGlHeld(GameState s)
    {
        if (PairAsides().Any(a => a.PendingGame == s.Game.Id)) throw new InvalidOperationException($"{s.Game.Name}'s cache is set aside for its before and after recording, end it first");
        (bool Done, string Status, IReadOnlyList<string>)? removed = null;
        SteamPlaceOnce(s, remove: true, CancellationToken.None, ref removed);
        if (removed is { Done: false } r) throw new InvalidOperationException(r.Status);
        return CompileLock(s.Game.Id) ?? throw new InvalidOperationException($"A compile of {s.Game.Name} is in progress");
    }

    /// <summary>Steam's folder, whose filter file a Vulkan plan reads Steam's archives by (<see cref="SteamSources"/>); null:
    /// Steam's archives aren't read.</summary>
    internal string? SteamRoot { get; set; }

    /// <summary>A Steam game's archives in its library's shadercache folder (<see cref="SteamPipelineCache"/>): the top
    /// level, where Steam records an application its filter has no rule for, as a Vulkan game is. None without Steam's
    /// filter file (<see cref="SteamPipelineCache.Choose"/>).</summary>
    /// <summary>The engine name of Steam's bucket of a DirectX game's pipelines under Proton (Linux); left null where none is read.</summary>
    partial void SteamDxEngine(EngineInfo e, ref string? name);

    partial void SteamSources(Game g, string? ours, ref List<FozSource>? sources)
    {
        if (SteamCache(g) is not var (dir, root)) return;
        if (SteamPipelineCache.Choose(SteamPipelineCache.Buckets(dir, Log).Where(b => b.Hash == null), (uint)Vendor.Gpu.Vendor, FossilizeFilter.Read(root)) is { } bucket)
            sources = SteamPipelineCache.Sources(bucket, ours);
    }

    // Steam records into them while the game runs, with no import of SCSKiller's to check the game again
    partial void SteamStamp(Game g, EngineInfo? engine, ref string key)
    {
        string? dx = null;
        if (engine != null) SteamDxEngine(engine, ref dx);
        if (engine == null || !VulkanPlan.IsVulkan(engine) && dx == null || SteamCache(g) is not var (dir, root)) return;
        try
        {
            var files = (Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.foz", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }) : [])
                .Append(new FileInfo(Path.Combine(root, FossilizeFilter.FileName)))
                .Where(f => f.Exists).ToList();
            key += $"|steam:{files.Count}.{files.Sum(f => f.Length)}.{files.Select(f => f.LastWriteTimeUtc.Ticks).DefaultIfEmpty().Max()}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { key += "|steam:?"; }
    }

    /// <summary>Valve's shader downloads in the game's Steam archives, where a plan reads them (a Vulkan game's, a DirectX
    /// game's under Proton), as their paths, lengths and write times: one landing or replaced makes the plan stale. Null: none.</summary>
    string? SteamDownloads(Game g, EngineInfo? engine)
    {
        string? dx = null;
        if (engine != null) SteamDxEngine(engine, ref dx);
        if (engine == null || !VulkanPlan.IsVulkan(engine) && dx == null || SteamCache(g) is not var (dir, _) || !Directory.Exists(dir)) return null;
        try
        {
            var files = new DirectoryInfo(dir).GetFiles(SteamPipelineCache.Download, new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(f => $"{Path.GetRelativePath(dir, f.FullName)}:{f.Length}:{f.LastWriteTimeUtc.Ticks}").Order(StringComparer.Ordinal).ToList();
            return files.Count == 0 ? null : string.Join('|', files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The game's shadercache folder in its Steam library and Steam's folder; null for a game not in a Steam
    /// library, or where Steam's archives aren't read.</summary>
    (string Dir, string Root)? SteamCache(Game g)
    {
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + Path.DirectorySeparatorChar;
        var at = install.IndexOf(SteamSource.CommonPart, StringComparison.OrdinalIgnoreCase);
        return SteamRoot is { } root && SteamSource.AppId(g.Id) is { } app && at >= 0 ? (SteamPipelineCache.Dir(install[..at], app), root) : null;
    }
}
