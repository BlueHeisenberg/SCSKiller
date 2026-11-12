using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.App;

// Outside sessions: games whose protection ends them when any module they don't ship is loaded from their own folder
// (OutsideOnly). No recorder dll goes into such a folder. SCSKiller starts the exe itself, suspended, as an offline session
// does (StartAttested), and adds the recorder, a copy outside the game folder under OutsideDllName, as the first entry of
// the exe's import table in that process's memory (AttachOutside): Windows' own loader loads it before any of the game's
// code runs, with no thread started in the game. The recorder puts the exe's headers back in its DllMain (proxy.cpp,
// restore_exe_imports: these games check them too), patches System32's d3d12.dll at its entry and keeps its data files in
// the exe's folder, where the session's signed scskiller.cfg names that one process. The session is journaled and cleaned
// up as an offline session is (GameRecord.OfflineSession, CleanOfflineSession).
public sealed partial class ScsKiller
{
    /// <summary>The recorder's file name outside a game folder: under it the proxy takes the exe's folder for its data files
    /// and hooks System32's d3d12.dll instead of standing in for it (proxy.cpp's DllMain).</summary>
    public const string OutsideDllName = "scskiller_d3d12.dll";

    public const string SkipOutside = "this game closes itself with any added file loaded from its folder: record a session instead";

    // Resident Evil Requiem and Onimusha: Way of the Sword: EXCEPTION_ILLEGAL_INSTRUCTION a few seconds after the first frame
    // with any dll of ours, or a copy of System32's d3d12.dll, loaded from the exe's folder
    static readonly HashSet<string> OutsideExes = new(["re9", "OnimushaWotS"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The game page's line for a <see cref="GameState.OutsideEligible"/> game, above its session button.</summary>
    public const string OutsideNote = "This game closes itself when the recorder is in its folder, so SCSKiller records it one session at a time. "
        + "It starts the game itself with the recorder loaded from outside the game folder. Launches from Steam are recorded only with the switch below on. Steam must be running.";
    /// <summary>The confirmation before each such session.</summary>
    public const string OutsideConfirm = "SCSKiller starts the game now and records until you quit it. Nothing is added to the game's program files: "
        + "its settings and recording go beside the exe and are removed when the game exits (after a crash: at the next logon).";
    /// <summary>The library row's title and line for such a game with no recording yet.</summary>
    public const string NeedsOutsideTitle = "Needs a recorded session", NeedsOutsideNote = "Start a recorded session and play for about 5 minutes.";

    /// <summary>An <see cref="GameState.OutsideEligible"/> game with nothing recorded yet.</summary>
    public static bool NeedsOutsideSession(GameState s) => s.OutsideEligible && s.Status == GameStatus.NeedsRecording;

    /// <summary>The game records only through an outside session: nothing of the recorder's is ever loaded from its folder.</summary>
    internal static bool OutsideOnly(Game g) => OutsideExes.Contains(Path.GetFileNameWithoutExtension(g.ExePath));

    /// <summary><see cref="GameState.OutsideEligible"/>.</summary>
    bool OutsideOffered(GameState s) => OperatingSystem.IsWindows() && OutsideOnly(s.Game) && !s.AnyAntiCheat && !s.RootUnconfirmed
        && s.Engine is { } e && e.GraphicsApi.Contains("D3D12") && GameVerdicts.Current.Unsupported(s.Game, e) == null;

    /// <summary>Where this copy keeps the recorder that outside sessions load.</summary>
    string OutsideDll => Path.Combine(Store.DataDir, "outside", OutsideDllName);

    public Task StartOutsideSession(string gameId, bool confirmed) => StartOutsideSession(gameId, confirmed, null, null);

    /// <summary><see cref="StartOutsideSession(string, bool)"/> for a launch its store made: <paramref name="exe"/> and
    /// <paramref name="arguments"/> are the command the store would have run (a Steam launch option's %command%), which
    /// must be the game's exe; the store's own environment names its app, so no <see cref="SteamAppIdFile"/> is placed.</summary>
    public Task StartOutsideSession(string gameId, bool confirmed, string? exe, IReadOnlyList<string>? arguments)
    {
        var s = Find(gameId);
        var g = s.Game;
        if (!confirmed) throw new InvalidOperationException($"{g.Name}: a session starts only after you confirm it");
        lock (_updateGate)
        {
            if (_updating > 0) throw new InvalidOperationException(UpdateInstalling);
            _offlineStarting++;
        }
        try { return StartOutside(s, g, gameId, exe, arguments); }
        finally
        {
            lock (_updateGate) _offlineStarting--;
        }
    }

    /// <summary>One argument as CreateProcess's command line takes it (the C runtime's rules: backslashes before a quote doubled).</summary>
    internal static string QuoteArgument(string a)
    {
        if (a.Length > 0 && !a.Any(c => c is ' ' or '\t' or '"')) return a;
        var q = new StringBuilder("\"");
        for (var i = 0; i < a.Length; i++)
        {
            var slashes = 0;
            for (; i < a.Length && a[i] == '\\'; i++) slashes++;
            if (i == a.Length) q.Append('\\', slashes * 2);
            else if (a[i] == '"') q.Append('\\', slashes * 2 + 1).Append('"');
            else q.Append('\\', slashes).Append(a[i]);
        }
        return q.Append('"').ToString();
    }

    Task StartOutside(GameState s, Game g, string gameId, string? storeExe = null, IReadOnlyList<string>? storeArguments = null)
    {
        lock (_recorderLock)
        {
            if (_removedFromAll) throw new RemovedFromAllException();
            var rec = Store.LoadGame(gameId);
            var dir = Path.GetDirectoryName(g.ExePath)!;
            // started outside its store, a Steam game finds its app through this file, as an offline session's does
            var appId = SteamSource.AppId(g.Id)?.ToString();
            var appIdFile = Path.Combine(dir, SteamAppIdFile);
            var byStore = storeExe != null;
            var addAppId = !byStore && appId != null && !File.Exists(appIdFile);   // else the user's own, with this id: left as it is
            var arguments = byStore ? string.Join(' ', (storeArguments ?? []).Select(QuoteArgument)) : OfflineArguments;
            string[] placed = [.. WritesLedger ? new[] { LedgerStamp } : [], IniName, .. addAppId ? new[] { SteamAppIdFile } : []];
            var stamp = Stamp(g.ExePath);   // before the fresh check below
            var outside = OutsideDll;
            string? Refusal()
            {
                if (!OperatingSystem.IsWindows()) return "sessions that load the recorder from outside the game folder are available on Windows only";
                if (!OutsideOffered(s)) return "recording a session isn't available for this game";
                if (byStore && !(Path.GetFullPath(storeExe!).Equals(g.ExePath, GameFiles.Names) || GameFiles.FinalPath(Path.GetDirectoryName(g.ExePath)!) is { } real
                        && Path.GetFullPath(storeExe!).Equals(Path.Combine(real, Path.GetFileName(g.ExePath)), GameFiles.Names)))
                    return $"its store started {storeExe}, not {g.ExePath}";
                if (OfflineLive(gameId) || rec.OfflineSession != null) return "the last session's files are still being removed";
                if (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null || rec.RecorderRollback)
                    return "a recorder's removal is still pending";
                lock (_lock)
                    if (_current == gameId) return "a compile of it is in progress";
                if (GameRunning(g)) return "it is running: close the game first";
                if (_proxyDll == null || !File.Exists(_proxyDll)) return $"the recorder ({Dll12}) wasn't found next to SCSKiller";
                if (outside.Any(c => c is < ' ' or > '~')) return $"SCSKiller's data folder ({Store.DataDir}) has characters an import name can't hold";
                if (stamp == null) return "its exe can't be read";
                // fresh, not the scan's: no anti-cheat at all
                var late = false;
                if (GameFiles.DetectAntiCheat(g, late: () => late = true) != AntiCheat.None || late || VacCheck(g)) return "its install has anti-cheat, or couldn't be read whole";
                if (Foreign(dir)) return "another SCSKiller copy's recorder is in its folder";
                // nothing of ours may be loaded from the folder, and none of the names the session places may be there
                if ((ProxyNames.FirstOrDefault(n => IsOurProxy(Path.Combine(dir, n))) ?? PreloadNames.FirstOrDefault(n => IsOurPreload(Path.Combine(dir, n)))
                     ?? placed.FirstOrDefault(f => Path.Exists(Path.Combine(dir, f)))) is { } there) return $"{there} is already in its folder";
                if (IntelLocalDriver(g)) return SkipIntelDriver;
                if (!byStore && appId != null && !addAppId && File.ReadAllText(appIdFile).Trim() != appId) return $"its folder has a {SteamAppIdFile} with another app id";
                if (appId != null && !ProcessNames().Contains("steam")) return SteamFirst;
                return null;
            }
            if (Refusal() is { } why) throw new InvalidOperationException(why == SteamFirst ? why : $"{g.Name}: no session: {why}");
            _sessionNotes.TryRemove(gameId, out _);
            // the session first: whatever happens after, every cleanup knows the folder to look at
            var session = new OfflineSession(gameId, g.ExePath, g.InstallDir, Created: [.. placed.Select(f => f + TempSuffix), .. addAppId ? [SteamAppIdFile] : Array.Empty<string>()]);
            (rec.RecorderExe, rec.RecorderInstallDir, rec.OfflineSession) = (g.ExePath, g.InstallDir, session);
            Store.SaveGame(gameId, rec);
            OfflineLeft()[gameId] = true;
            var since = DateTime.UtcNow;
            RecorderLog($"{g.Name}: session with the recorder outside its folder: {outside} into {g.ExePath}");
            SafeProcessHandle? process = null;
            try
            {
                if (CleanupHelper != null)
                {
                    AddRunOnce(gameId, CleanupHelper);
                    using var helper = Process.Start(new ProcessStartInfo(CleanupHelper, [CleanupArg, gameId]) { UseShellExecute = false, CreateNoWindow = true })
                        ?? throw new InvalidOperationException("its cleanup helper didn't start");
                }
                // this build's recorder, under the name it knows itself by: replaced when an update brought another
                if (!File.Exists(outside) || Sha256(outside) != Sha256(_proxyDll!))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
                    Place(outside, CopyOf(_proxyDll!), replace: true);
                }
                process = StartAttested(g.ExePath, arguments, (h, pid, started) =>
                {
                    _offline[gameId] = h;
                    var ini = IniText(g, rec, Signed(g, stamp!, BoundTo(pid, started) + SignedLines(g, rec, session: true)));
                    var now = Store.LoadGame(gameId);
                    if (now.OfflineSession == null) throw new InvalidOperationException("its session was cleaned up meanwhile");
                    now.RecorderFiles = [];   // no dll in the folder: the ini goes by its folder's entry
                    SetIni(Store, dir, _ => new IniOwner(dir, Hex(ini), Install: g.InstallDir, Wrote: Hex(ini)));
                    if (addAppId) now.RecorderFiles[SteamAppIdFile] = Hex(appId!);
                    now.OfflineSession = session = session with { Pid = pid, Started = started };
                    Store.SaveGame(gameId, now);
                    RefuseForeign(dir);
                    if (WritesLedger) CreateLedger(Path.Combine(dir, LedgerStamp), LedgerText(LedgerDir));   // first: this copy's
                    Place(Path.Combine(dir, IniName), Text(ini));
                    if (addAppId) Place(Path.Combine(dir, SteamAppIdFile), Text(appId!));
                    AttachOutside(h, outside);
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
                CleanFailedStart(g);
                throw new InvalidOperationException($"{g.Name}: the session didn't start: {e.Message}", e);
            }
            RecorderLog($"{g.Name}: session started ({g.ExePath}, process {session.Pid}): only that process records");
            Refresh(g);
            return Task.Run(() => EndOffline(g, process!, since, false, NeverSawNote));
        }
    }

    /// <summary>Adds <paramref name="dll"/> (a full path, printable ASCII) as the first import of the exe of a process started
    /// suspended and not yet resumed: its loader hasn't run, so it loads the dll with the exe's own imports. The new import
    /// table goes in a block of its own above the image, within reach of a 32-bit RVA, and the headers' import directory
    /// points at it; the dll must export <paramref name="symbol"/>, the one name imported. 64-bit processes only.</summary>
    internal static void AttachOutside(SafeProcessHandle process, string dll, string symbol = "D3D12CreateDevice")
    {
        byte[] Read(nint at, int n)
        {
            var b = new byte[n];
            if (!ReadProcessMemory(process, at, b, n, out var got) || got != n) throw new Win32Exception();
            return b;
        }
        void Write(nint at, byte[] b)
        {
            if (!WriteProcessMemory(process, at, b, b.Length, out var put) || put != b.Length) throw new Win32Exception();
        }
        var pbi = new byte[48];
        if (NtQueryInformationProcess(process, 0, pbi, pbi.Length, out _) < 0) throw new InvalidOperationException("its process couldn't be read");
        var peb = (nint)BinaryPrimitives.ReadInt64LittleEndian(pbi.AsSpan(8));
        var image = (nint)BinaryPrimitives.ReadInt64LittleEndian(Read(peb + 0x10, 8));
        var dos = Read(image, 0x40);
        if (BinaryPrimitives.ReadUInt16LittleEndian(dos) != 0x5A4D) throw new InvalidOperationException("its exe has no DOS header");
        var ntAt = image + BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
        const int Optional = 24, Dirs = Optional + 112, ImportDir = Dirs + 8 * 1, BoundDir = Dirs + 8 * 11, HeaderSize = Optional + 240;
        var nt = Read(ntAt, HeaderSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(nt) != 0x4550 || BinaryPrimitives.ReadUInt16LittleEndian(nt.AsSpan(Optional)) != 0x20B)
            throw new InvalidOperationException("its exe isn't a 64-bit program");
        var sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(Optional + 56));
        var importRva = BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(ImportDir));
        // the exe's own descriptors, up to the empty one that ends them
        const int Descriptor = 20;
        var old = new List<byte[]>();
        for (var i = 0; importRva != 0; i++)
        {
            var d = Read(image + (nint)importRva + i * Descriptor, Descriptor);
            if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12)) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16)) == 0) break;
            if (i > 4096) throw new InvalidOperationException("its import table has no end");
            old.Add(d);
        }
        // descriptors (ours, the exe's, the empty one), our lookup and address tables (one entry and its end), the name, the path
        byte[] name = Encoding.ASCII.GetBytes(symbol + "\0"), path = Encoding.ASCII.GetBytes(dll + "\0");
        int count = old.Count + 2, lookup = count * Descriptor, address = lookup + 16, hint = address + 16, pathAt = hint + 2 + name.Length;
        var block = new byte[pathAt + path.Length];
        nint at = 0;
        for (long tryAt = ((long)image + sizeOfImage + 0xFFFF) & ~0xFFFFL; at == 0 && tryAt < (long)image + 0x7FF00000; tryAt += 0x10000)
            at = VirtualAllocEx(process, (nint)tryAt, block.Length, 0x3000 /* MEM_COMMIT | MEM_RESERVE */, 0x04 /* PAGE_READWRITE */);
        if (at == 0) throw new InvalidOperationException("no memory near its exe for the import table");
        var rva = (uint)((long)at - (long)image);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), rva + (uint)lookup);     // OriginalFirstThunk
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), rva + (uint)pathAt);    // Name
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), rva + (uint)address);   // FirstThunk
        for (var i = 0; i < old.Count; i++) old[i].CopyTo(block, (i + 1) * Descriptor);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(lookup), rva + (uint)hint);   // by name, hint 0
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(address), rva + (uint)hint);
        name.CopyTo(block, hint + 2);
        path.CopyTo(block, pathAt);
        Write(at, block);
        // the headers point at it; a bound import table would skip the binding
        var dirs = Read(ntAt + ImportDir, BoundDir + 8 - ImportDir);
        BinaryPrimitives.WriteUInt32LittleEndian(dirs.AsSpan(0), rva);
        BinaryPrimitives.WriteUInt32LittleEndian(dirs.AsSpan(4), (uint)(count * Descriptor));
        dirs.AsSpan(BoundDir - ImportDir, 8).Clear();
        if (!VirtualProtectEx(process, ntAt + ImportDir, dirs.Length, 0x04, out var was)) throw new Win32Exception();
        try { Write(ntAt + ImportDir, dirs); }
        finally { VirtualProtectEx(process, ntAt + ImportDir, dirs.Length, was, out _); }
    }

    // ---- Every Steam launch recorded: a launch option that runs the CLI's "record launch" in the game's place ----

    public const string SteamMustClose = "Close Steam first: it keeps its settings in memory and writes them over any change made while it runs.";
    public const string OutsideAlwaysNote = "Adds SCSKiller to this game's Steam launch options, so every launch from Steam is recorded.";

    /// <summary>The dialog before the launch option goes in: what it is, and what it costs.</summary>
    public static string OutsideAlwaysOnText(string option) =>
        "Steam will start this game through SCSKiller, which loads the recorder from outside the game folder and then starts the game as usual. "
        + "If the recorder can't load, the game still starts.\n\n"
        + "This goes into the game's launch options in Steam (Properties > General > Launch Options):\n\n" + option + "\n\n"
        + "SCSKiller can add it for you, which needs Steam closed, or copy it so you paste it yourself.\n\n"
        + "Turn this off before you uninstall or move SCSKiller: with the option left behind, Steam can't start the game until you clear it.";
    public const string OutsideAlwaysOffText = "SCSKiller takes its part out of this game's Steam launch options, which needs Steam closed. "
        + "Or remove it yourself in Steam: Properties > General > Launch Options.";
    public const string OutsideCopiedText = "Copied. In Steam, right-click the game > Properties > General, and paste it into Launch Options. "
        + "If you already have launch options there, put this in front of them, with %command% only once.";

    /// <summary>The command-line exe a launch option runs (tests: another).</summary>
    internal Func<string?> LaunchCli { get; set; } = () => ScheduledTask.TaskExe();

    string? OutsidePrefix(Game g) => SteamSource.AppId(g.Id) == null || LaunchCli() is not { } cli ? null : $"\"{cli}\" record launch {g.Id} --";

    /// <summary>What goes into the game's Steam launch options for <see cref="GameState.OutsideAlways"/>; null when it isn't a
    /// Steam game or this install has no command-line exe.</summary>
    string? OutsideOption(Game g) => OutsidePrefix(g) is { } p ? p + " %command%" : null;

    static System.Text.RegularExpressions.Regex OursIn(string gameId) =>
        new("\"[^\"]*\" record launch " + System.Text.RegularExpressions.Regex.Escape(gameId) + @" --\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The launch options run SCSKiller's "record launch" for the game, whichever copy's.</summary>
    internal static bool HasLaunch(string options, string gameId) => OursIn(gameId).IsMatch(options);

    /// <summary>The options with <paramref name="prefix"/> in front of the game's command: in front of their %command%, or of
    /// all of them when they are plain arguments.</summary>
    internal static string WithLaunch(string options, string gameId, string prefix)
    {
        options = WithoutLaunch(options, gameId);
        var at = options.IndexOf("%command%", StringComparison.Ordinal);
        return at >= 0 ? options[..at] + prefix + " " + options[at..] : (prefix + " %command% " + options).TrimEnd();
    }

    /// <summary>The options without SCSKiller's part; a %command% left leading nothing but arguments goes too.</summary>
    internal static string WithoutLaunch(string options, string gameId)
    {
        var had = HasLaunch(options, gameId);
        options = OursIn(gameId).Replace(options, "").Trim();
        return had && options.StartsWith("%command%", StringComparison.Ordinal) && options.IndexOf("%command%", 1, StringComparison.Ordinal) < 0
            ? options["%command%".Length..].Trim() : options;
    }

    /// <summary>Steam's settings file that holds the user's launch options: the newest userdata\*\config\localconfig.vdf, as
    /// <see cref="Unreal.UnrealRhi.LaunchOptions(Game)"/> reads them.</summary>
    static string? LocalConfig(string? steamRoot)
    {
        if (steamRoot == null || Path.Combine(steamRoot, "userdata") is var userdata && !Directory.Exists(userdata)) return null;
        return Directory.EnumerateDirectories(Path.Combine(steamRoot, "userdata")).Select(u => new FileInfo(Path.Combine(u, "config", "localconfig.vdf")))
            .Where(f => f.Exists).MaxBy(f => f.LastWriteTimeUtc)?.FullName;
    }

    /// <summary>Puts SCSKiller's part into the game's Steam launch options, or takes it out, leaving the user's own options
    /// around it. Null when done, else why not: nothing is written while Steam runs (<see cref="SteamMustClose"/>). The file
    /// as it was is kept beside it as localconfig.vdf.scskiller-bak.</summary>
    public string? SetOutsideAlways(string gameId, bool on)
    {
        var s = Find(gameId);
        var g = s.Game;
        if (!s.OutsideEligible || SteamSource.AppId(g.Id) is not { } app) return "recording every Steam launch isn't available for this game";
        if (OutsidePrefix(g) is not { } prefix) return "SCSKiller's command line (cli\\scskillerw.exe) wasn't found next to the app";
        if (ProcessNames().Contains("steam")) return SteamMustClose;
        try
        {
            if (LocalConfig(SteamRoot ?? SteamSource.Root()) is not { } file) return "Steam's settings file (localconfig.vdf) wasn't found";
            var text = File.ReadAllText(file);
            if (ChangeLaunchOptions(text, app, old => on ? WithLaunch(old, g.Id, prefix) : WithoutLaunch(old, g.Id)) is not { } edited)
                return "Steam's settings have no place for this game's launch options yet: start it once from Steam, close Steam, and try again";
            if (edited != text)
            {
                File.Copy(file, file + ".scskiller-bak", overwrite: true);
                var temp = file + "." + Guid.NewGuid().ToString("N") + TempSuffix;
                File.WriteAllText(temp, edited, new UTF8Encoding(false));
                File.Move(temp, file, overwrite: true);
                RecorderLog($"{g.Name}: Steam launch options {(on ? "now start it through" : "no longer start it through")} {prefix} ({file})");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Steam's settings couldn't be changed: {e.Message}"; }
        Refresh(g);
        return null;
    }

    /// <summary>A text KeyValues file (Steam's localconfig.vdf) with app <paramref name="app"/>'s LaunchOptions changed by
    /// <paramref name="change"/> (from "" when it has none), every other byte as it was: the value replaced in place, or the
    /// key added to the app's block, or the block added to "apps". Null when the file has no
    /// UserLocalConfigStore/Software/Valve/Steam/apps or doesn't parse; the same text when nothing changes.</summary>
    internal static string? ChangeLaunchOptions(string vdf, uint app, Func<string, string> change)
    {
        string[] path = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps", app.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        var stack = new List<string>();
        bool At(int depth) => stack.Count == depth && stack.Zip(path).All(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase));
        (int Start, int End, string Text)? value = null;
        int appClose = -1, appsClose = -1;
        string? key = null;
        for (var i = 0; i < vdf.Length && appsClose < 0;)
        {
            var c = vdf[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < vdf.Length && vdf[i + 1] == '/') { while (i < vdf.Length && vdf[i] != '\n') i++; continue; }
            if (c == '{')
            {
                if (key == null || stack.Count > 64) return null;
                stack.Add(key);
                key = null;
                i++;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count == 0) return null;
                if (At(path.Length) && appClose < 0) appClose = i;
                else if (At(path.Length - 1)) appsClose = i;
                stack.RemoveAt(stack.Count - 1);
                key = null;
                i++;
                continue;
            }
            var start = i;
            var token = new StringBuilder();
            if (c == '"')
            {
                for (i++; i < vdf.Length && vdf[i] != '"'; i++)
                    token.Append(vdf[i] == '\\' && i + 1 < vdf.Length ? vdf[++i] : vdf[i]);
                if (i >= vdf.Length) return null;   // no closing quote
                i++;
            }
            else
                for (; i < vdf.Length && !char.IsWhiteSpace(vdf[i]) && vdf[i] is not ('{' or '}' or '"'); i++) token.Append(vdf[i]);
            if (key == null) key = token.ToString();
            else
            {
                if (value == null && appClose < 0 && At(path.Length) && key.Equals("LaunchOptions", StringComparison.OrdinalIgnoreCase)) value = (start, i, token.ToString());
                key = null;
            }
        }
        static string Quoted(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        if (value is { } v)
            return change(v.Text) is var to && to == v.Text ? vdf : vdf[..v.Start] + Quoted(to) + vdf[v.End..];
        var close = appClose >= 0 ? appClose : appsClose;
        if (close < 0) return null;
        var added = change("");
        if (added.Length == 0) return vdf;
        var nl = vdf.Contains("\r\n") ? "\r\n" : "\n";
        var line = vdf.LastIndexOf('\n', close) + 1;
        var indent = vdf[line..close];
        if (indent.Any(ch => ch is not (' ' or '\t'))) (line, indent) = (close, "");   // the brace shares its line: before the brace itself
        var entry = appClose >= 0
            ? $"{indent}\t\"LaunchOptions\"\t\t{Quoted(added)}{nl}"
            : $"{indent}\t\"{path[^1]}\"{nl}{indent}\t{{{nl}{indent}\t\t\"LaunchOptions\"\t\t{Quoted(added)}{nl}{indent}\t}}{nl}";
        return vdf[..line] + entry + vdf[line..];
    }

    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(SafeProcessHandle process, int cls, byte[] info, int size, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(SafeProcessHandle process, nint at, byte[] buffer, nint size, out nint read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(SafeProcessHandle process, nint at, byte[] buffer, nint size, out nint written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint VirtualAllocEx(SafeProcessHandle process, nint at, nint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtectEx(SafeProcessHandle process, nint at, nint size, uint protect, out uint was);
}
