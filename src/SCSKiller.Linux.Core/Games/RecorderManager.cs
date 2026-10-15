using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Games;

/// <summary>Record mode on Linux, as SCSKiller.Core's ScsKiller.Install / ImportRecording / WriteKeys do on Windows:
/// the d3d12.dll recorder (Native/d3d12.dll, built from Native/recorder.cpp with mingw-w64) goes next to the game's exe,
/// where Wine loads it before the prefix's vkd3d-proton d3d12.dll. It writes scskiller.db there (the inbox); SCSKiller
/// imports it into games/&lt;id&gt;/recording.db and writes scskiller.keys back so nothing is recorded twice.</summary>
public static class RecorderManager
{
    public static string DataDir => SCSKiller.Linux.Core.AppPaths.DataDir;
    public static string GameDir(string gameId) => Path.Combine(DataDir, "games", Sanitize(gameId));
    public static string RecordingPath(string gameId) => Path.Combine(GameDir(gameId), "recording.db");

    static string Sanitize(string id) => string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));

    static string? ProxySource()
    {
        foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "Native", "d3d12.dll"), Path.Combine(AppContext.BaseDirectory, "d3d12.dll") })
            if (File.Exists(p) && ScsKiller.IsOurProxy(p)) return p;
        return null;
    }

    /// <summary>The folder of the exe that really runs (an Unreal game's Shipping exe, not its launcher stub).</summary>
    public static string? ExeDir(GameInfo game)
    {
        var exe = game.ExecutablePath;
        if (string.IsNullOrEmpty(exe)) exe = GameEngineDetector.FindExecutable(game.InstallPath) ?? "";
        if (string.IsNullOrEmpty(exe)) return null;
        try
        {
            var resolved = SCSKiller.Core.Games.GameFiles.GameExe(game.InstallPath, exe);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) exe = resolved;
        }
        catch { }
        return Path.GetDirectoryName(exe);
    }

    public static bool IsInstalled(GameInfo game) => ExeDir(game) is { } dir && ScsKiller.IsOurProxy(Path.Combine(dir, "d3d12.dll"));

    /// <summary>Pipelines recorded so far: SCSKiller's copy plus what the inbox holds that the copy doesn't. Each file's keys
    /// are read again only when its size or write time changed (a scan runs this for every game).</summary>
    public static int RecordedPsos(GameInfo game)
    {
        var keys = new HashSet<string>();
        try
        {
            foreach (var path in new[] { RecordingPath(game.Id), ExeDir(game) is { } d ? Path.Combine(d, "scskiller.db") : null })
                if (path != null && new FileInfo(path) is { Exists: true, Length: > 0 } f)
                    keys.UnionWith(KeysOf(f));
        }
        catch { }
        return keys.Count;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime Written, HashSet<string> Keys)> _keys = new();

    static HashSet<string> KeysOf(FileInfo f)
    {
        if (_keys.TryGetValue(f.FullName, out var c) && c.Length == f.Length && c.Written == f.LastWriteTimeUtc) return c.Keys;
        var keys = PsoDb.Read(f.FullName).Where(r => r.Tag is not ('B' or 'N' or 'L' or 'W')).Select(r => r.Key).ToHashSet();
        _keys[f.FullName] = (f.Length, f.LastWriteTimeUtc, keys);
        return keys;
    }

    static SCSKiller.Core.Game ToCore(GameInfo g, string exeDir) => new(g.Id, g.Name,
        g.Source switch { GameSource.Steam => SCSKiller.Core.Store.Steam, GameSource.Heroic => SCSKiller.Core.Store.Epic, _ => SCSKiller.Core.Store.Manual },
        g.InstallPath, File.Exists(g.ExecutablePath) && Path.GetDirectoryName(g.ExecutablePath) == exeDir ? g.ExecutablePath
            : Directory.EnumerateFiles(exeDir, "*.exe").FirstOrDefault() ?? g.ExecutablePath);

    public const string IniText = "[scskiller]\r\n; written by Shaderless (unofficial Linux port of SCSKiller): the recorder records every pipeline the game creates into scskiller.db\r\nmode=record\r\n";

    /// <summary>Installs and arms the recorder. Refuses next to anti-cheat (a d3d12.dll there could get the account banned)
    /// and over another mod's d3d12.dll.</summary>
    public static void Install(GameInfo game)
    {
        var dir = ExeDir(game) ?? throw new InvalidOperationException($"{game.Name}: the game's exe wasn't found");
        var src = ProxySource() ?? throw new FileNotFoundException("the recorder (Native/d3d12.dll) was not found next to SCSKiller");
        var ac = SCSKiller.Core.Games.GameFiles.DetectAntiCheat(ToCore(game, dir));
        if (ac != SCSKiller.Core.AntiCheat.None)
            throw new InvalidOperationException($"{game.Name} uses {ac}: a d3d12.dll in its folder could get the account banned. Not installing.");
        var dll = Path.Combine(dir, "d3d12.dll");
        if (File.Exists(dll) && !ScsKiller.IsOurProxy(dll))
            throw new InvalidOperationException($"{dll} already exists and is not SCSKiller's (another mod or wrapper). Not replacing it.");
        File.Copy(src, dll, overwrite: true);
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), IniText);
        File.WriteAllText(Path.Combine(dir, "scskiller.armed"), "[scskiller]\r\narmed=1\r\n");
        WriteKeys(game, dir);
    }

    /// <summary>Imports what was recorded, then removes the recorder and its files.</summary>
    public static void Uninstall(GameInfo game)
    {
        var dir = ExeDir(game) ?? throw new InvalidOperationException($"{game.Name}: the game's exe wasn't found");
        Import(game);
        var dll = Path.Combine(dir, "d3d12.dll");
        if (ScsKiller.IsOurProxy(dll)) File.Delete(dll);
        foreach (var f in new[] { "scskiller.ini", "scskiller.armed", "scskiller.log", "scskiller_creates.csv", Recordings.KeysFile, Recordings.WarmedFile })
            try { File.Delete(Path.Combine(dir, f)); } catch { }
        var inbox = Path.Combine(dir, "scskiller.db");
        if (!File.Exists(inbox) || new FileInfo(inbox).Length == 0) try { File.Delete(inbox); } catch { }
    }

    /// <summary>Merges the game folder's scskiller.db into SCSKiller's compact copy and empties it (Recordings.Rotate:
    /// only while the game doesn't hold it). Returns the pipelines added.</summary>
    public static int Import(GameInfo game)
    {
        if (ExeDir(game) is not { } dir) return 0;
        var inbox = new FileInfo(Path.Combine(dir, "scskiller.db"));
        if (!inbox.Exists || inbox.Length == 0) return 0;
        var store = RecordingPath(game.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        int added;
        using (Recordings.Lock(store))
        {
            added = Recordings.Merge(store, inbox.FullName, null).Count;
            Recordings.Rotate(inbox.FullName, inbox.Length);
        }
        WriteKeys(game, dir);
        return added;
    }

    /// <summary>scskiller.keys next to the recorder: what the copy already holds, so the next session records only what's new.</summary>
    static void WriteKeys(GameInfo game, string dir)
    {
        if (!ScsKiller.IsOurProxy(Path.Combine(dir, "d3d12.dll"))) return;
        var store = RecordingPath(game.Id);
        if (!File.Exists(store)) return;
        using (Recordings.Lock(store)) Recordings.WriteKeys(store, null, Path.Combine(dir, Recordings.KeysFile));
    }

    /// <summary>Planner.HasDraws: a graphics PSO with a vertex shader, so a state-dependent cache's exact layouts are known.</summary>
    public static bool HasDraws(string db)
    {
        foreach (var r in PsoDb.Read(db))
            try { if (r.Tag is 'G' or 'S' && PsoDb.Parse(r).Stages.ContainsKey((int)SCSKiller.Core.Stage.Vertex)) return true; }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException) { }
        return false;
    }
}
