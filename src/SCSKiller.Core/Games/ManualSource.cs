using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Games;

/// <summary>A game the user added by its exe: <see cref="Exe"/> is the resolved D3D12 process (<see cref="ManualSource.Resolve"/>).
/// <see cref="Confirmed"/>: the user confirmed <see cref="InstallDir"/> as the game's folder (<see cref="ManualSource.RootProblem"/>
/// passed), the folder the anti-cheat check covers before the recorder is allowed; an entry saved without one isn't.</summary>
public sealed record ManualEntry(string Exe, string InstallDir, string Name, bool Confirmed = false);

/// <summary>added-library.json: the folders SCSKiller looks for games in, the games the user removed (a folder's would be found
/// again), and the names the user gave, by game id.</summary>
public sealed record AddedLibrary(List<string>? Folders = null, List<string>? Removed = null, Dictionary<string, string>? Names = null);

/// <summary>Games the user added (manual-games.json in the data folder). Id "manual:" + a hash of the exe's full path, so
/// the same exe added again is the same game. No build id: a changed exe (size, write time) is what marks a patch, as for
/// the stores that give none. An entry whose exe is missing (a drive unplugged) is kept and listed again once it's back.</summary>
public sealed class ManualSource(AppStore store) : IGameSource, ILeavesOut
{
    // the app, the CLI and the scheduled task may change the list at once: one read-modify-write at a time per data folder
    readonly string _mutex = MutexName(store.DataDir);

    internal static string MutexName(string dataDir) =>
        @"Local\SCSKiller.manual-games." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToUpperInvariant())))[..16];

    public Store Store => Store.Manual;

    // game id -> its folder and whether it is confirmed, as the last read of the list had it (replaced whole under the mutex)
    volatile Dictionary<string, (string Root, bool Confirmed)> _roots = [];

    /// <summary>The last discovery's games, by id: a folder's game is looked for again only when its exe is gone.</summary>
    public IReadOnlyDictionary<string, Game>? Known { get; set; }

    /// <summary>The folders' scan skips these. A game added by its exe is listed wherever it is.</summary>
    public Func<string, bool> LeftOut { get; set; } = _ => false;

    /// <summary>The entries, then a game in each of the folders' game folders (<see cref="GameRoots"/>) that holds no entry's
    /// exe, isn't a store's install (as the last discovery listed them) and that the user didn't remove; each with the name
    /// the user gave it.</summary>
    public IReadOnlyList<Game> Discover()
    {
        Climb();
        var entries = Entries();
        var added = store.LoadAddedLibrary();
        var games = entries.Where(e => File.Exists(e.Exe)).Select(ToGame).ToList();
        var listed = entries.Select(e => IdOf(e.Exe)).Concat(added.Removed ?? []).ToHashSet();
        var known = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Known?.Values ?? []) if (g.Store == Store.Manual) known.TryAdd(GameFiles.DirKey(g.InstallDir), g);
        var stores = (Known?.Values ?? []).Where(g => g.Store != Store.Manual && g.InstallDir.Length > 0).Select(g => GameFiles.DirKey(g.InstallDir)).ToList();
        bool Overlaps(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) || GameFiles.Inside(a, b) || GameFiles.Inside(b, a);
        var found = new HashSet<string>();
        foreach (var root in (added.Folders ?? []).Where(f => !LeftOut(f)).SelectMany(f => GameRoots(f, LeftOut)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!entries.Any(e => GameFiles.Inside(root, e.Exe)) && !stores.Any(s => Overlaps(s, root))
                && (known.GetValueOrDefault(root) is { } k && File.Exists(k.ExePath) ? k : Found(root)) is { } g && listed.Add(g.Id))
            {
                games.Add(g);
                found.Add(g.Id);
            }
        _found = found;
        return games.Select(g => Named(g, added)).ToList();
    }

    volatile HashSet<string> _found = [];

    /// <summary>The game was found in a folder by the last discovery, not added by its exe.</summary>
    public bool InFolder(string gameId) => _found.Contains(gameId);

    static Game Named(Game g, AddedLibrary added) => added.Names?.GetValueOrDefault(g.Id) is { Length: > 0 } name ? g with { Name = name } : g;

    /// <summary>A folder's game folders: the folder itself when it holds a game (Unreal's Engine folder, or an exe and at most one
    /// folder with programs in it), else each folder in it, a store's or Windows' folder of many aside (<see cref="IsLibrary"/>);
    /// an Xbox app game's is its Content folder. None while the folder can't be read (a drive unplugged: its games come back with it).</summary>
    internal static List<string> GameRoots(string folder, Func<string, bool> leftOut)
    {
        try
        {
            var dir = GameFiles.DirKey(folder);
            var children = GameFiles.EnumerateDirectories(dir, "*", new EnumerationOptions { AttributesToSkip = 0 }).Select(GameFiles.DirKey)
                .Where(d => !IsLibrary(d) && !GameFiles.WindowsFolder(Path.GetFileName(d))).ToList();
            // a left-out folder counts as one with programs, unread
            bool Programs(string d) => leftOut(d) || !NotAGame.Any(n => Path.GetFileName(d).Contains(n, StringComparison.OrdinalIgnoreCase))
                && GameFiles.EnumerateFiles(d, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, AttributesToSkip = 0 }).Any();
            if (Directory.Exists(GameFiles.Find(dir, "Engine"))
                || GameFiles.EnumerateFiles(dir, "*.exe").Any() && children.Where(Programs).Take(2).Count() < 2) return [dir];
            return children.Where(d => !leftOut(d) && !leftOut(GameFiles.Find(d, "Content"))).Select(d => File.Exists(GameFiles.Find(d, @"Content\MicrosoftGame.config")) ? GameFiles.Find(d, "Content") : d).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return []; }
    }

    /// <summary>The game in a folder's game folder: its exe found as for a store's install (<see cref="GameFiles.FindExe"/>: an
    /// Unreal game's Shipping exe, never its launcher stub or a helper), then checked and named as a pick (<see cref="Resolve"/>).
    /// Null when there is none, it is no 64-bit program, or the folder looks like several games' (<see cref="RootProblem"/>).</summary>
    internal static Game? Found(string root)
    {
        // ponytail: a folder that holds no game is walked again at every scan; remember the misses if large folders get slow
        try
        {
            if (GameFiles.FindExe(root) is not { } exe) return null;
            var e = Resolve(exe, found: true);
            // no name in the exe: the game folder's, the one above Content for an Xbox app game
            var folder = GameFiles.FolderName(Path.GetFileName(root).Equals("Content", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(root)! : root);
            e = e with { InstallDir = root, Name = e.Name == GameFiles.FolderName(e.InstallDir) ? folder : e.Name };
            return RootProblem(root, e.Exe, []) == null ? ToGame(e) : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
        catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Added folders", root, e); return null; }
    }

    public IReadOnlyList<string> Folders => store.LoadAddedLibrary().Folders ?? [];

    /// <summary>ArgumentException for a folder that doesn't exist; one added already is kept once.</summary>
    public void AddFolder(string folder)
    {
        var dir = GameFiles.DirKey(folder);
        if (!Directory.Exists(dir)) throw new ArgumentException($"{dir} doesn't exist.");
        Locked(() =>
        {
            var added = store.LoadAddedLibrary();
            if ((added.Folders ?? []).Contains(dir, StringComparer.OrdinalIgnoreCase)) return false;
            store.SaveAddedLibrary(added with { Folders = [.. added.Folders ?? [], dir] });
            return true;
        });
    }

    public void RemoveFolder(string folder) => Locked(() =>
    {
        var added = store.LoadAddedLibrary();
        store.SaveAddedLibrary(added with { Folders = (added.Folders ?? []).Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList() });
        return true;
    });

    /// <summary>Only the name shown changes: the game id, which its plans, caches, recordings and saved state are kept under, is
    /// its exe's and stays. ArgumentException for an empty name.</summary>
    public void Rename(string gameId, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Type a name for the game.");
        Locked(() =>
        {
            var added = store.LoadAddedLibrary();
            store.SaveAddedLibrary(added with { Names = new(added.Names ?? []) { [gameId] = name } });
            return true;
        });
    }

    /// <summary>The well-formed entries: an entry with no exe or install folder, or a path that isn't a full one, is left out.</summary>
    /// <remarks>Read under the mutex, so a read that started before a change never publishes the folders it had after it.</remarks>
    public List<ManualEntry> Entries() => Locked(() =>
    {
        var list = store.LoadManualGames().Where(Valid).ToList();
        Read?.Invoke();
        _roots = list.GroupBy(e => IdOf(e.Exe)).ToDictionary(x => x.Key, x => (GameFiles.DirKey(x.First().InstallDir), x.First().Confirmed));
        return list;
    });

    /// <summary>The game's folder is the one the user confirmed (as the list was last read): the recorder may go in.</summary>
    public bool Confirmed(Game g) => _roots.TryGetValue(g.Id, out var r) && r.Confirmed && Same(r.Root, GameFiles.DirKey(g.InstallDir)) && GameFiles.Inside(r.Root, g.ExePath);

    /// <summary>Tests: runs between reading the list and publishing its folders.</summary>
    internal Action? Read { get; set; }

    /// <summary><see cref="Confirmed"/>, the list read again now (under the mutex): for arming.</summary>
    public bool ConfirmedNow(Game g)
    {
        Entries();
        return Confirmed(g);
    }

    /// <summary>A copy of the game from before its folder changed: its state is for another folder than the list's.</summary>
    public bool Stale(Game g) => _roots.TryGetValue(g.Id, out var r) && !Same(r.Root, GameFiles.DirKey(g.InstallDir));

    /// <summary>An entry saved with its exe's own folder before <see cref="InstallRoot"/> climbed gets the climbed root, written
    /// back; no add flow tells that folder from a user's pick of it. Climbed outside the mutex: a drive that hangs stalls this
    /// scan, never arming (<see cref="ConfirmedNow"/>).</summary>
    void Climb()
    {
        static bool Own(ManualEntry e) => Valid(e) && Path.GetDirectoryName(e.Exe) is { } dir && Same(GameFiles.DirKey(e.InstallDir), dir) && Same(InstallRoot(e.Exe, climb: false), dir);
        try
        {
            var moved = store.LoadManualGames().Where(e => Own(e) && File.Exists(e.Exe)).Select(e => (e.Exe, Root: InstallRoot(e.Exe, climb: true)))
                .Where(m => !Same(m.Root, Path.GetDirectoryName(m.Exe)!)).DistinctBy(m => m.Exe, StringComparer.OrdinalIgnoreCase).ToDictionary(m => m.Exe, m => m.Root, StringComparer.OrdinalIgnoreCase);
            if (moved.Count > 0)
                Locked(() =>
                {
                    // read again: an entry whose folder changed since keeps it
                    store.SaveManualGames(store.LoadManualGames().Select(e => Own(e) && moved.TryGetValue(e.Exe, out var root) ? e with { InstallDir = root } : e).ToList());
                    return true;
                });
        }
        // the entries stay as they are, the next scan tries again
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { GameFiles.Skip("Added games", "moving to the game's folder", e); }
    }

    static bool Valid(ManualEntry e) =>
        e is { Exe.Length: > 0, InstallDir.Length: > 0 } && e.Exe.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(e.Exe)
        && e.InstallDir.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(e.InstallDir);

    public static Game ToGame(ManualEntry e) =>
        new(IdOf(e.Exe), e.Name is { Length: > 0 } n ? n : Path.GetFileName(Path.TrimEndingDirectorySeparator(e.InstallDir)), Store.Manual, e.InstallDir, e.Exe);

    public static string IdOf(string exe) =>
        "manual:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant())))[..16];

    /// <summary>The game and whether it was already added. A confirmed folder replaces the one an entry had.</summary>
    public (Game Game, bool Existed) Add(ManualEntry entry) => Locked(() =>
    {
        if (entry.Confirmed && !GameFiles.Inside(entry.InstallDir, entry.Exe)) throw new ArgumentException($"{entry.InstallDir} doesn't hold {Path.GetFileName(entry.Exe)}.");
        var list = Entries();
        var id = IdOf(entry.Exe);
        var i = list.FindIndex(e => IdOf(e.Exe) == id);
        var added = store.LoadAddedLibrary();
        if (i >= 0 && !entry.Confirmed) return (Named(ToGame(list[i]), added), true);
        if (i >= 0) list[i] = list[i] with { InstallDir = entry.InstallDir, Confirmed = true };
        else list.Add(entry);
        store.SaveManualGames(list);
        Entries();
        if (added.Removed?.Remove(id) == true) store.SaveAddedLibrary(added);
        return (Named(ToGame(i >= 0 ? list[i] : entry), added), i >= 0);
    });

    /// <summary>Also remembered as removed: a folder's scan lists it again only once it is added by its exe.</summary>
    public bool Remove(string gameId) => Locked(() =>
    {
        var list = Entries();
        var had = list.RemoveAll(e => IdOf(e.Exe) == gameId) > 0;
        if (had) store.SaveManualGames(list);
        Entries();
        var added = store.LoadAddedLibrary();
        added.Names?.Remove(gameId);
        store.SaveAddedLibrary(added with { Removed = [.. (added.Removed ?? []).Where(r => r != gameId), gameId] });
        return had;
    });

    T Locked<T>(Func<T> change)
    {
        using var m = NamedMutex.Open(_mutex);
        try { m.WaitOne(); }
        catch (AbandonedMutexException) { }   // its holder died: the file was replaced whole or not at all (WriteAtomic)
        try { return change(); }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>The game an exe the user picked belongs to: its install root (Unreal's, above Engine\ and
    /// &lt;Project&gt;\Binaries; REDengine's, above bin\x64), the process that creates the D3D12 device found as for the
    /// stores (<see cref="GameFiles.GameExe"/>, <see cref="GameFiles.FindExe"/>: a launcher stub becomes its Shipping exe), and a name. ArgumentException
    /// with the message to show for a pick that is no 64-bit program, sits at a drive's root, or launches one of several
    /// exes SCSKiller can't tell apart.</summary>
    public static ManualEntry Resolve(string picked, bool found = false)
    {
        var pick = Path.GetFullPath(picked);
        var file = Path.GetFileName(pick);
        if (!pick.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{file} isn't a program: pick the game's .exe file.");
        if (!File.Exists(pick)) throw new ArgumentException($"{pick} doesn't exist.");
        CheckX64(pick, file);
        var root = InstallRoot(pick, climb: false);
        // the exe is resolved in the exe's own install root: a climbed one may hold another project's Shipping exe
        var top = found ? root : InstallRoot(pick, climb: true);
        if (Path.GetPathRoot(root) == root) throw new ArgumentException($"{file} is at the root of a drive: SCSKiller needs the game in a folder of its own.");
        // as discovery: no import table is read in an install with anti-cheat
        var clean = GameFiles.DetectAntiCheat(new Game("", "", Store.Manual, top, pick)) == AntiCheat.None;
        var graphics = clean ? GameFiles.ImportsGraphics(pick) : null;
        var exe = graphics == true || GameFiles.IsShipping(pick) ? GameFiles.GameExe(root, pick) : GameFiles.FindExe(root, GameFiles.Rel(root, pick)) ?? pick;
        if (Same(exe, pick) && graphics == false && !GameFiles.IsShipping(pick))   // a launcher FindExe kept: the one exe of the install that loads D3D, if there is one
            switch (GameFiles.GraphicsExes(root).Where(f => !Same(f, pick) && X64(f)).ToList())   // a 32-bit launcher is never the game
            {
                case [var one]: exe = one; break;
                case { Count: > 1 } several:
                    throw new ArgumentException($"{file} looks like a launcher, and SCSKiller can't tell which program it starts: "
                        + string.Join(", ", several.Take(4).Select(Path.GetFileName)) + (several.Count > 4 ? "…" : "")
                        + ". Pick the one that opens the game's window.");
            }
        if (!Same(exe, pick)) CheckX64(exe, Path.GetFileName(exe));   // what is added is the game's exe, not the pick
        if (found && clean && GameSign(exe) == null) throw new ArgumentException($"nothing in the folder of {Path.GetFileName(exe)} looks like a game");
        return new ManualEntry(exe, top, NameOf(exe, pick, top));
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>What makes a folder's program a game: Unreal's Shipping exe, a graphics API imported by the exe or a DLL in
    /// its folder two levels down, an engine's runtime or data. Null = none: a program that isn't a game. One that can't be read counts as one.</summary>
    internal static string? GameSign(string exe)
    {
        if (GameFiles.IsShipping(exe)) return "Unreal Shipping exe";
        if (ImportsAny(exe) is not false) return Path.GetFileName(exe);
        try
        {
            foreach (var e in new DirectoryInfo(Path.GetDirectoryName(exe)!).EnumerateFileSystemInfos("*", SignWalk))
                if (e is DirectoryInfo ? e.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                    : EngineFiles.Contains(e.Name) || DataExts.Contains(e.Extension) || e.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) && ImportsAny(e.FullName) == true)
                    return e.Name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return "unreadable"; }
        return null;
    }

    static readonly EnumerationOptions SignWalk = new() { RecurseSubdirectories = true, MaxRecursionDepth = 2, AttributesToSkip = 0, IgnoreInaccessible = false };
    // engines that load the graphics API at run time (Unity, MonoGame, FNA, SDL)
    static readonly HashSet<string> EngineFiles = new(["UnityPlayer.dll", "GameAssembly.dll", "MonoGame.Framework.dll", "FNA.dll", "SDL2.dll", "SDL3.dll"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> DataExts = new([".pak", ".utoc", ".ucas", ".assets", ".bundle", ".xnb", ".pck", ".rpf", ".wad", ".arc", ".psarc", ".forge", ".big"], StringComparer.OrdinalIgnoreCase);
    static readonly string[] AnyGraphics = ["d3d12.dll", "d3d11.dll", "dxgi.dll", "d3d9.dll", "opengl32.dll", "vulkan-1.dll", "sl.interposer.dll"];

    static bool? ImportsAny(string path)
    {
        try
        {
            return CarvedReader.PeImports(path, out _).Any(d => AnyGraphics.Contains(d, StringComparer.OrdinalIgnoreCase) || d.Contains("-RHI", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    static bool X64(string path)
    {
        try { CheckX64(path, Path.GetFileName(path)); return true; }
        catch (ArgumentException) { return false; }
    }

    static void CheckX64(string path, string file)
    {
        PEHeaders h;
        try
        {
            using var pe = PeFile.Open(path);
            h = pe.PEHeaders;
        }
        catch (BadImageFormatException) { throw new ArgumentException($"{file} isn't a Windows program."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ArgumentException($"Couldn't read {file}: {e.Message}"); }
        if (h.IsDll || h.PEHeader == null) throw new ArgumentException($"{file} isn't a Windows program.");
        if (h.CoffHeader.Machine != Machine.Amd64 || h.PEHeader.Magic != PEMagic.PE32Plus)
            throw new ArgumentException(h.CoffHeader.Machine == Machine.I386
                ? $"{file} is a 32-bit program: SCSKiller compiles 64-bit DirectX 12 games."
                : $"{file} isn't an x64 program: SCSKiller compiles 64-bit DirectX 12 games.");
    }

    /// <summary>The folder suggested as the game's (the user confirms or changes it): Unreal's, holding Engine\ and the project
    /// (the project's own folder when Engine\ isn't beside it); bin*\ or bin\x64*\: the folder above bin. Else, with
    /// <paramref name="climb"/> and no Engine\ or &lt;Project&gt;\Content\Paks in the exe's folder, the nearest of the 3 folders
    /// above it that holds Engine\ (Phantom Blade Zero's, above platform\steam\PBZ.exe), when it holds no other game's folder and passes
    /// <see cref="RootProblem"/>, never at or past a drive's root or a folder of many (<see cref="IsLibrary"/>), by any of their
    /// names. Else the exe's folder.</summary>
    static string InstallRoot(string exe, bool climb)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var root = dir;
        if (Path.GetDirectoryName(dir) is { } parent)
        {
            if (Path.GetFileName(parent).Equals("Binaries", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(parent) is { } project)
                root = Path.GetDirectoryName(project) is { } above && Directory.Exists(GameFiles.Find(above, "Engine")) ? above : project;
            else if (Path.GetFileName(parent).Equals("bin", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(parent) is { } game)
                root = game;
            else if (Path.GetFileName(dir).StartsWith("bin", StringComparison.OrdinalIgnoreCase)) root = parent;
        }
        if (!climb || !Same(root, dir)) return root;
        try
        {
            bool Engine(string d) => Directory.Exists(GameFiles.Find(d, "Engine"));
            // by every name: a junction may lead into a library or a drive's root
            bool Edge(string d) => GameFiles.Spellings(d).Any(s => IsLibrary(s) || Path.GetPathRoot(s) == s);
            if (Edge(dir) || Engine(dir) || GameFiles.EnumerateDirectories(dir).Any(p => Directory.Exists(GameFiles.Find(p, @"Content\Paks")))) return root;
            // beside the way down to the exe, a folder with a program of its own is another game's (the launcher stub is named like the game's exe)
            bool Alone(string up, string via) => !GameFiles.EnumerateDirectories(up).Any(d => !Same(GameFiles.DirKey(d), via) && !NotAGame.Any(n => Path.GetFileName(d).Contains(n, StringComparison.OrdinalIgnoreCase))
                && GameFiles.EnumerateFiles(d, "*.exe").Any(f => !Same(Path.GetFileName(f), Path.GetFileName(exe))));
            var (below, up) = (dir, Path.GetDirectoryName(dir));
            // Engine\ only above: a games folder holds other games' <Project>\Content\Paks, never an Engine\ of its own
            // each step up is one on disk too: a link (or a chain of them) below leads elsewhere, maybe out of a library
            bool Step(string from, string to) => GameFiles.FinalPath(from) is { } f && GameFiles.FinalPath(to) is { } t && Path.GetDirectoryName(GameFiles.DirKey(f)) is { } p && Same(p, GameFiles.DirKey(t));
            for (var n = 0; n < 3 && up != null && !Edge(up) && Step(below, up); n++, (below, up) = (up, Path.GetDirectoryName(up)))
                if (Engine(up)) return Alone(up, GameFiles.DirKey(below)) && RootProblem(up, exe, []) == null ? up : root;   // the nearest Engine\ is the game's or none is
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return root;
    }

    // stores' and launchers' folders of games, and Windows' own: never one game's folder
    static readonly string[] Libraries = ["steamapps", "SteamLibrary", "XboxGames", "WindowsApps", "ModifiableWindowsApps", "Epic Games",
        "EpicGames", "GOG Games", "GOG Galaxy", "EA Games", "Origin Games", "Ubisoft Game Launcher", "Battle.net", "WeGameApps", "rail_apps", "Program Files", "Program Files (x86)",
        "ProgramData", "Windows", "Users", "Desktop", "Downloads", "Documents", "OneDrive"];

    /// <summary>Why <paramref name="root"/> can't be confirmed as the game's folder; null = it can. Refused: a missing folder, a
    /// drive or share root, a store's or Windows' folder of many (<see cref="Libraries"/>, steamapps\common, the user's own
    /// folder), a folder that holds another game SCSKiller lists (<paramref name="listed"/>), or one whose subfolders look like
    /// several games (two or more with a program directly in them, not counting one with a program named like <paramref name="exe"/>,
    /// nor, when <paramref name="exe"/> is directly in the root, one whose programs show no sign of a game: <see cref="GameSign"/>),
    /// or one that doesn't hold <paramref name="exe"/>.</summary>
    public static string? RootProblem(string root, string exe, IEnumerable<Game> listed)
    {
        string full;
        try { full = GameFiles.DirKey(root); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return $"{root} isn't a folder."; }
        if (!Directory.Exists(full)) return $"{full} doesn't exist.";
        var mine = GameFiles.Spellings(full);
        if (mine.Any(d => Path.GetPathRoot(d) is { } drive && GameFiles.DirKey(drive).Equals(d, StringComparison.OrdinalIgnoreCase)))
            return $"{full} is a whole drive: pick the game's own folder.";
        if (mine.Any(IsLibrary)) return $"{full} holds many programs or games: pick the game's own folder.";
        if (!GameFiles.Inside(full, exe)) return $"{full} doesn't hold {Path.GetFileName(exe)}: pick the folder it's in.";
        List<(string Dir, bool Self)> above;
        try { above = [.. GameFiles.LinkedAncestors(full)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return $"Couldn't read {full}: {e.Message}"; }
        var names = mine.Concat(above.Where(a => a.Self).Select(a => a.Dir)).ToList();
        foreach (var g in listed)
        {
            if (Same(g.ExePath, exe) || g.InstallDir.Length == 0) continue;
            var theirs = GameFiles.Spellings(g.InstallDir);
            if (theirs.Any(t => names.Any(m => Same(m, t) || GameFiles.Inside(m, t)))) return $"{full} holds {g.Name} too: pick this game's own folder.";
            if (theirs.Any(t => above.Any(a => GameFiles.Inside(t, a.Dir) || !a.Self && Same(a.Dir, t))))
                return $"{full} is inside {g.Name}'s folder: pick this game's own folder.";
        }
        try
        {
            // a folder holding a program named like the game's is another build of it (DCS World's bin and bin-mt), not another game
            var name = Path.GetFileName(exe);
            // beside the game's own exe, a folder of programs is a mod tool's (Fluffy Mod Manager) unless it looks like a game
            var own = Same(GameFiles.DirKey(Path.GetDirectoryName(exe)!), full);
            var games = GameFiles.EnumerateDirectories(full).Where(d => !NotAGame.Any(n => Path.GetFileName(d).Contains(n, StringComparison.OrdinalIgnoreCase))
                && GameFiles.EnumerateFiles(d, "*.exe").Any(f => !own || GameSign(f) != null) && !File.Exists(GameFiles.Find(d, name))).Take(2).Count();
            if (games >= 2) return $"{full} looks like a folder of several games: pick this game's own folder.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Couldn't read {full}: {e.Message}"; }
        return null;
    }

    // the user's AppData folders hold launchers' and anti-cheats' own folders (a BattlEye folder in Local): never one game's
    static readonly Environment.SpecialFolder[] SpecialFolders = [Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData];

    /// <summary>A store's or Windows' folder of many (<see cref="Libraries"/>, steamapps\common, the user's own, AppData and temp
    /// folders); <paramref name="full"/> as <see cref="GameFiles.DirKey"/> gives it.</summary>
    internal static bool IsLibrary(string full)
    {
        var name = Path.GetFileName(full);
        return Libraries.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.Equals("common", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Path.GetDirectoryName(full)).Equals("steamapps", StringComparison.OrdinalIgnoreCase)
            || SpecialFolders.Select(Environment.GetFolderPath).Append(Path.GetTempPath())   // every program's scratch: build tools leave random 8.3 names there
                .Any(f => f.Length > 0 && GameFiles.DirKey(f).Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    // a game's own subfolders that hold programs of their own
    static readonly string[] NotAGame = ["redist", "directx", "crash", "setup", "install", "support", "tools", "launcher", "anticheat", "battleye"];

    // Unreal's launcher stub, engine defaults and launchers name no game
    static readonly string[] Generic = ["UnrealGame", "UE4Game", "UE5Game", "Unity"];

    static string NameOf(string exe, string pick, string root)
    {
        foreach (var f in new[] { exe, pick }.Distinct())
        {
            var v = PeVersionInfo.GetVersionInfo(f);
            foreach (var n in new[] { v.ProductName, v.FileDescription })
                if (n?.Trim() is { Length: > 0 } name && !Generic.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !name.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase) && !name.Contains("launcher", StringComparison.OrdinalIgnoreCase))
                    return StarEngine.StarEngineReader.Channel(exe) is { } channel ? $"{name} ({channel})" : name;   // LIVE and PTU: two games
        }
        return StarEngine.StarEngineReader.Channel(exe) is { } ch ? $"Star Citizen ({ch})" : Path.GetFileName(root);
    }
}
