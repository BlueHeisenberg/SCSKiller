using System.Diagnostics;
using System.Text.Json;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Games;

/// <summary>Install-folder heuristics shared by the game sources.</summary>
public static class GameFiles
{
    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
    static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true };

    /// <summary>The process that creates the D3D12 device: an Unreal game's Shipping exe (<see cref="ShippingExe"/>), else under
    /// a &lt;Project&gt;\Binaries\Win* folder a Shipping exe, then the largest exe (not Engine\Binaries, which only holds helpers like
    /// CrashReportClient), helpers (<see cref="IsHelper"/>) aside; of exes named alike, the one nearest the root; a 32-bit one is
    /// passed over, <see cref="UnrealExe"/>), else
    /// the exe BattlEye's launcher starts (<see cref="BattlEyeTarget"/>), else <paramref name="launcherExe"/>, else the
    /// largest exe near the install root. A launcher among the last two is replaced by the game it starts
    /// (<see cref="LaunchedExe"/>). Exes in a patcher's or installer's copy of the game (<see cref="Staging"/>) are never
    /// picked, nor are those an Xbox app game's config marks as not the game (<see cref="XboxSource.NotTheGame"/>).</summary>
    public static string? FindExe(string installDir, string? launcherExe = null)
    {
        if (!Directory.Exists(installDir)) return null;
        if (ShippingExe(installDir, launcherExe) is { } shipping) return shipping;
        var excluded = XboxSource.NotTheGame(installDir);
        var unreal = UnrealFolders(installDir)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.exe", Flat))
            .Where(f => !IsEngineFolder(installDir, f) && !InStaging(installDir, f) && !IsHelper(f) && !excluded.Contains(Path.GetFullPath(f)))
            .Select(f => new FileInfo(f))
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(same => same.MinBy(f => Path.GetRelativePath(installDir, f.FullName).Count(c => c == Path.DirectorySeparatorChar))!)
            .OrderByDescending(f => IsShipping(f.FullName) && !NotTheGameBuild.Any(n => BuildName(f.FullName).Contains(n, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(f => f.Length).ToList();
        if (unreal.Count > 0) return UnrealExe(installDir, unreal);
        if (BattlEyeTarget(installDir) is { } be) return be;
        if (launcherExe != null)
        {
            var p = Path.GetFullPath(Path.Combine(installDir, launcherExe));
            if (File.Exists(p)) return RedLauncherTarget(installDir, p) ?? p;
        }
        // ponytail: non-Unreal games get a guess; Steam's real launch target lives in the binary appinfo.vdf, parse it if this misfires
        var exes = Directory.EnumerateFiles(installDir, "*.exe", Deep)
            .Where(f => !NotTheGameExe(installDir, f, excluded))
            .Select(f => new FileInfo(f))
            .ToList();
        if (exes.FirstOrDefault(f => Directory.Exists(Path.ChangeExtension(f.FullName, null) + "_Data")) is { } unity) return unity.FullName;   // Unity: Game.exe + Game_Data
        var guess = exes.OrderBy(f => f.DirectoryName!.Length > installDir.TrimEnd('\\').Length ? 1 : 0)   // root folder first
            .ThenByDescending(f => f.Length).FirstOrDefault();
        return guess == null ? null : NotPlus(LaunchedExe(installDir, guess, exes), exes);
    }

    /// <summary>&lt;Name&gt;.exe for &lt;Name&gt;_Plus.exe beside it, whatever their sizes: the Ubisoft+ build, which only
    /// subscribers run (the watcher follows it when it runs).</summary>
    static string NotPlus(string exe, IReadOnlyList<FileInfo> exes)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        if (!name.EndsWith("_Plus", StringComparison.OrdinalIgnoreCase)) return exe;
        var plain = Path.Combine(Path.GetDirectoryName(exe)!, name[..^"_Plus".Length] + ".exe");
        return exes.FirstOrDefault(f => f.FullName.Equals(plain, StringComparison.OrdinalIgnoreCase))?.FullName ?? exe;
    }

    /// <summary>An exe <see cref="FindExe"/> never picks by its path or name: a helper, a redistributable's, patcher's or installer's copy, or one an Xbox app game's config marks as not the game.</summary>
    internal static bool NotTheGameExe(string installDir, string exe, ISet<string>? excluded = null) =>
        NotTheGame.Any(s => Path.GetRelativePath(installDir, exe).Contains(s, StringComparison.OrdinalIgnoreCase)) || InStaging(installDir, exe) || IsHelper(exe)
        || (excluded ?? XboxSource.NotTheGame(installDir)).Contains(Path.GetFullPath(exe));

    /// <summary>The first of <paramref name="bySize"/> (a Shipping exe, then the largest), unless it is 32-bit (a launcher
    /// larger than a modular build's exe): then the largest of the others that isn't, read only in an install without
    /// anti-cheat.</summary>
    static string UnrealExe(string installDir, List<FileInfo> bySize)
    {
        var guess = bySize[0].FullName;
        // the root's and the exe folders' markers: enough to read no binary beside anti-cheat, without a full walk on every scan
        if (bySize.Count == 1 || DetectAntiCheat(new Game("", "", Store.Other, installDir, guess), quick: true) != AntiCheat.None || !Is32Bit(guess)) return guess;
        return bySize.Skip(1).FirstOrDefault(f => !Is32Bit(f.FullName))?.FullName ?? guess;
    }

    /// <summary>The exe SCSKiller uses for a game whose store or user names <paramref name="exe"/>: a Shipping exe it names, else
    /// the Unreal game's Shipping exe (<see cref="ShippingExe"/>) over a bootstrap stub or launcher; else <paramref name="exe"/>.</summary>
    public static string GameExe(string installDir, string exe, Action<string>? ambiguous = null) =>
        IsShipping(exe) ? exe : ShippingExe(installDir, exe, ambiguous) ?? exe;

    /// <summary>An Unreal game's own process: &lt;Name&gt;-&lt;Platform&gt;-Shipping.exe in a &lt;Project&gt;\Binaries\&lt;Platform&gt;
    /// folder (Win64, WinGDK, WinGRTS...) of the install root or a project one or two levels under it, with an Engine folder
    /// beside the project or in the root (an Unreal install); Engine's, a patcher's copy, helpers and tools (<see cref="NotTheGameBuild"/>)
    /// aside, and those an Xbox app game's config marks as not the game (<see cref="XboxSource.NotTheGame"/>); of exes named alike, the one nearest the root. One such exe is the game's; of several, the one whose name or
    /// project is <paramref name="named"/>'s name (the stub starts &lt;Stub&gt;-&lt;Platform&gt;-Shipping.exe) or the install
    /// folder's. Null when there is none, or several and none or more than one tied (said to <paramref name="ambiguous"/>).</summary>
    public static string? ShippingExe(string installDir, string? named = null, Action<string>? ambiguous = null)
    {
        if (!Directory.Exists(installDir)) return null;
        List<string> all;
        try
        {
            var unrealRoot = Directory.Exists(Path.Combine(installDir, "Engine"));
            var excluded = XboxSource.NotTheGame(installDir);
            all = UnrealFolders(installDir)
                .SelectMany(d => Directory.EnumerateFiles(d, "*-Shipping.exe", Flat))
                .Where(f => IsShipping(f) && !excluded.Contains(Path.GetFullPath(f)) && !IsEngineFolder(installDir, f) && !InStaging(installDir, f) && !IsHelper(f)
                    && !NotTheGameBuild.Any(n => BuildName(f).Contains(n, StringComparison.OrdinalIgnoreCase))
                    && (unrealRoot || Directory.Exists(Path.Combine(Path.GetDirectoryName(ProjectDir(f))!, "Engine"))))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(same => same.MinBy(f => Path.GetRelativePath(installDir, f).Count(c => c == Path.DirectorySeparatorChar))!)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (all.Count <= 1) return all.FirstOrDefault();
        var names = new[] { named == null ? null : Path.GetFileNameWithoutExtension(named), Path.GetFileName(DirKey(installDir)) }.OfType<string>().ToList();
        if (all.Where(f => names.Any(n => n.Equals(BuildName(f), StringComparison.OrdinalIgnoreCase)
                || n.Equals(Path.GetFileName(ProjectDir(f)), StringComparison.OrdinalIgnoreCase))).ToList() is [var tied]) return tied;
        ambiguous?.Invoke($"{installDir}: several Shipping exes and none tied to {Path.GetFileName(named) ?? "the install"}, kept as named: "
            + string.Join(", ", all.Select(f => Path.GetRelativePath(installDir, f))));
        return null;
    }

    /// <summary>&lt;Name&gt; of &lt;Name&gt;-&lt;Platform&gt;-Shipping.exe.</summary>
    static string BuildName(string shipping)
    {
        var name = Path.GetFileName(shipping);
        return name[..^($"-{Path.GetFileName(Path.GetDirectoryName(shipping))}-Shipping.exe".Length)];
    }

    /// <summary>&lt;Project&gt; of &lt;Project&gt;\Binaries\&lt;Platform&gt;	he exe.</summary>
    static string ProjectDir(string shipping) => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(shipping)!)!)!;

    // Shipping builds that ship beside a game and aren't it
    static readonly string[] NotTheGameBuild = ["Server", "Editor", "Tool", "Benchmark", "CrashReport"];

    /// <summary>&lt;Name&gt;-&lt;Platform&gt;-Shipping.exe in a Binaries\&lt;Platform&gt; folder.</summary>
    public static bool IsShipping(string exe) =>
        Path.GetDirectoryName(exe) is { } dir && string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), "Binaries", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(exe).EndsWith($"-{Path.GetFileName(dir)}-Shipping.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The &lt;Project&gt;\Binaries\Win* folders of the install root and of projects one or two levels under it. Win32
    /// is left out: a 32-bit exe never creates the game's D3D12 device.</summary>
    static IEnumerable<string> UnrealFolders(string installDir) =>
        Directory.EnumerateDirectories(installDir, "*", Flat)
            .SelectMany(d => Directory.EnumerateDirectories(d, "*", Flat).Prepend(d)).Prepend(installDir)
            .Select(p => Path.Combine(p, "Binaries"))
            .Where(Directory.Exists)
            .SelectMany(b => Directory.EnumerateDirectories(b, "Win*", Flat))
            .Where(d => !Path.GetFileName(d).Equals("Win32", StringComparison.OrdinalIgnoreCase));

    /// <summary>An i386 PE; false when it isn't one or can't be read.</summary>
    internal static bool Is32Bit(string exe)
    {
        try
        {
            using var pe = PeFile.Open(exe);
            return pe.PEHeaders.CoffHeader.Machine == System.Reflection.PortableExecutable.Machine.I386;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { return false; }
    }

    /// <summary>The game a launcher (<paramref name="guess"/>) starts: the target of CD PROJEKT RED's launcher-configuration.json
    /// next to it, else, when the guess imports no graphics API, the one larger exe of <paramref name="exes"/> that does (no
    /// binary read in an install with anti-cheat). Anything else (one unreadable, several, none) keeps the guess.</summary>
    static string LaunchedExe(string installDir, FileInfo guess, IReadOnlyList<FileInfo> exes)
    {
        if (RedLauncherTarget(installDir, guess.FullName) is { } red) return red;
        var larger = exes.Where(f => f.Length > guess.Length).ToList();
        if (larger.Count == 0 || ImportsGraphics(guess.FullName) != false
            || DetectAntiCheat(new Game("", "", Store.Other, installDir, guess.FullName)) != AntiCheat.None) return guess.FullName;
        var imports = larger.Select(f => (f.FullName, Imports: ImportsGraphics(f.FullName))).ToList();
        return imports.All(x => x.Imports != null) && imports.Where(x => x.Imports == true).ToList() is [var game] ? game.FullName : guess.FullName;
    }

    /// <summary>The 64BitExe of the BattlEye\BELauncher.ini files near the install root (relative to the folder holding
    /// BattlEye\, where the game's *_BE.exe sits): the one existing exe inside the install they
    /// name; null when they name none or several.</summary>
    internal static string? BattlEyeTarget(string installDir)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ini in Directory.EnumerateFiles(installDir, "BELauncher.ini", Deep))
        {
            var dir = Path.GetDirectoryName(ini)!;
            if (!Path.GetFileName(dir).Equals("BattlEye", StringComparison.OrdinalIgnoreCase)) continue;
            string[] lines;
            try { lines = File.ReadAllLines(ini); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var rel = lines.Select(l => l.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals("64BitExe", StringComparison.OrdinalIgnoreCase))?[1].Trim();
            if (string.IsNullOrEmpty(rel) || Path.IsPathRooted(rel) || rel.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
            try { if (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dir)!, rel)) is var exe && InsideNoLinks(installDir, exe)) targets.Add(exe); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return targets.Count == 1 ? targets.First() : null;
    }

    /// <summary><paramref name="target"/> is an existing file inside <paramref name="installDir"/>, reached through no junction or
    /// symlink below it (the anti-cheat scan doesn't follow those; the root itself may be one).</summary>
    static bool InsideNoLinks(string installDir, string target)
    {
        var root = DirKey(installDir);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return false;
        for (var p = target; p.Length > root.Length; p = Path.GetDirectoryName(p)!)
            if ((p == target ? new FileInfo(p) : (FileSystemInfo)new DirectoryInfo(p)) is { LinkTarget: not null }) return false;
        return true;
    }

    /// <summary>launcher-configuration.json's executables[]: the entry whose description is its "fallback", else the first;
    /// null unless that exe exists inside the install.</summary>
    static string? RedLauncherTarget(string installDir, string exe)
    {
        var config = Path.Combine(Path.GetDirectoryName(exe)!, "launcher-configuration.json");
        if (!File.Exists(config)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("executables", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var fallback = r.TryGetProperty("fallback", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            var entries = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            if (entries.Count == 0) return null;
            var pick = entries.FirstOrDefault(e => e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() == fallback,
                entries[0]);
            if (!pick.TryGetProperty("executable", out var x) || x.ValueKind != JsonValueKind.Object) return null;
            string? S(string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("fileName") is not { Length: > 0 } name) return null;
            var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(config)!, S("directoryPath") ?? "", name));
            return InsideNoLinks(installDir, target) ? target : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // Streamline's interposer stands in for dxgi and d3d12 in games that ship it: witcher3.exe imports neither
    static readonly string[] GraphicsDlls = ["d3d12.dll", "d3d11.dll", "dxgi.dll", "sl.interposer.dll"];

    /// <summary>Null when the file isn't a readable PE. A modular Unreal build's exe (Returnal) imports its RHI module
    /// (&lt;Project&gt;-RHI-Win64-Shipping.dll), which loads the graphics API.</summary>
    internal static bool? ImportsGraphics(string exe)
    {
        try
        {
            return CarvedReader.PeImports(exe, out _).Any(d => GraphicsDlls.Contains(d, StringComparer.OrdinalIgnoreCase)
                || d.Contains("-RHI-", StringComparison.OrdinalIgnoreCase) || d.EndsWith("-RHI.dll", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Streamline's interposer is beside the exe and nothing the exe imports statically, itself or through the DLLs of
    /// its folder, imports d3d12.dll: the first d3d12.dll comes with Streamline's plugins. NVIDIA's downloaded plugins load
    /// System32's, so the recorder never loads. An import found in neither the exe's folder nor System32 (API sets aside) is
    /// resolved somewhere this can't follow: null, as for a module that can't be read. A DLL of the exe's folder that forwards
    /// an export to d3d12 counts as importing it. <paramref name="files"/> gets the exe folder's files the answer depends on:
    /// sl.interposer.dll and each import's path there, found or not (<see cref="Stamp"/>).</summary>
    internal static bool? StreamlineFirst(string exe, List<string> files)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var interposer = Path.Combine(dir, "sl.interposer.dll");
        files.Add(interposer);
        if (!File.Exists(interposer)) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var todo = new Stack<(string Path, bool Local)>([(exe, true)]);
        try
        {
            while (todo.TryPop(out var module))
            {
                using var pe = PeFile.Open(module.Path);
                if (module.Local && PeFile.Forwarders(pe).Any(f => f.StartsWith("d3d12.", StringComparison.OrdinalIgnoreCase))) return false;
                foreach (var dll in CarvedReader.PeImports(pe, delayed: false))
                {
                    if (dll.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase)) return false;
                    if (dll.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase) || !seen.Add(dll)) continue;
                    var local = Path.Combine(dir, dll);
                    files.Add(local);
                    if (File.Exists(local)) todo.Push((local, true));
                    else if (Path.Combine(SystemDir, dll) is var system && File.Exists(system)) todo.Push((system, false));
                    else return null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
        return true;
    }

    internal static string SystemDir { get; set; } = Environment.SystemDirectory;   // replaceable for tests

    /// <summary>Each file's size and write time ("-" when missing), to tell when a cached answer about them is stale.</summary>
    internal static string Stamp(IEnumerable<string> files) =>
        string.Join('|', files.Select(f => new FileInfo(f) is { Exists: true } i ? $"{i.Length}:{i.LastWriteTimeUtc.Ticks}" : "-"));

    /// <summary>The exes near the install root (as <see cref="FindExe"/> looks) that import a graphics API.</summary>
    internal static IEnumerable<string> GraphicsExes(string installDir) => Directory.EnumerateFiles(installDir, "*.exe", Deep)
        .Where(f => !NotTheGame.Any(s => Path.GetRelativePath(installDir, f).Contains(s, StringComparison.OrdinalIgnoreCase)) && !IsEngineFolder(installDir, f) && !InStaging(installDir, f) && !IsHelper(f))
        .Where(f => ImportsGraphics(f) == true);

    /// <summary><paramref name="path"/> is inside the folder <paramref name="dir"/> (case-insensitive, full paths).</summary>
    public static bool Inside(string dir, string path) =>
        dir.Length > 0 && Path.GetFullPath(path).StartsWith(DirKey(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder's path with every junction and symlink on the way resolved, as a process's image path names
    /// it; null when it can't be opened.</summary>
    public static string? FinalPath(string dir)
    {
        using var h = CreateFileW(dir, 0, 7 /* FILE_SHARE_READ | WRITE | DELETE */, 0, 3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, 0);
        return h.IsInvalid ? null : FinalPath(h);
    }

    /// <summary>A file's identity (FILE_ID_INFO's 24 bytes: the volume serial and the 128-bit file id, the same through any
    /// link or junction) and its <see cref="FinalPath(string)"/>, each null when it can't be read; null when the file can't
    /// be opened. As the proxy reads its exe's (proxy.cpp ledger_path).</summary>
    public static (byte[]? Id, string? Final)? Identity(string path)
    {
        using var h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7, 0, 3, 0x02000000, 0);
        if (h.IsInvalid) return null;
        var id = new byte[24];
        return (GetFileInformationByHandleEx(h, 0x12 /* FileIdInfo */, id, id.Length) ? id : null, FinalPath(h));
    }

    static string? FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        var name = new char[32768];
        var n = GetFinalPathNameByHandleW(h, name, name.Length, 0);
        if (n == 0 || n >= name.Length) return null;
        var path = new string(name, 0, (int)n);
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
             : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle h, char[] name, int size, uint flags);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass, byte[] info, int size);

    /// <summary>The folder's own name, also from a path ending in a separator (Ubisoft Connect's end in "/").</summary>
    public static string FolderName(string dir) => Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));

    /// <summary>An install folder compared across sources: full path, no trailing separator (a drive root keeps its own).</summary>
    public static string DirKey(string dir) => dir.Length == 0 ? dir : Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    static readonly string[] NotTheGame = ["redist", "directx", "crash", "unins", "setup", "vconsole"];   // vconsole2.exe: Source 2's developer console

    // the Epic Online Services installer is 64-bit and can be the largest exe in an Unreal game's Binaries\Win64
    static readonly string[] Helpers = ["EpicOnlineServices", "EOSBootstrapper", "EpicWebHelper", "CrashReport", "UnrealCEFSubProcess",
        "Prereq", "setup", "redist", "installer", "unins"];

    /// <summary>An installer, redistributable or engine helper shipped beside a game's exe, by its file name.</summary>
    internal static bool IsHelper(string exe) => Helpers.Any(h => Path.GetFileName(exe).Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Folders a patcher or installer keeps a copy of the game's files in, which never run: Stellar Blade's
    /// PatchData\SB\Binaries\Win64 holds a second SB-Win64-Shipping.exe; the EA app's __Installer its own tools.</summary>
    static readonly string[] Staging = ["PatchData", "__Installer", "Backup", "Backups", "Staging"];

    static bool InStaging(string installDir, string path) =>
        Path.GetRelativePath(installDir, Path.GetDirectoryName(path)!).Split(Path.DirectorySeparatorChar).Any(seg => Staging.Contains(seg, StringComparer.OrdinalIgnoreCase));

    static bool IsEngineFolder(string installDir, string path) =>
        Path.GetRelativePath(installDir, path).StartsWith("Engine" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static readonly (string Name, AntiCheat Kind)[] Markers =
    [
        ("EasyAntiCheat", AntiCheat.EasyAntiCheat), ("EasyAntiCheat_EOS", AntiCheat.EasyAntiCheat), ("start_protected_game.exe", AntiCheat.EasyAntiCheat),
        ("EasyAntiCheat_EOS_Setup.exe", AntiCheat.EasyAntiCheat), ("EasyAntiCheat_Setup.exe", AntiCheat.EasyAntiCheat),
        ("BattlEye", AntiCheat.BattlEye), ("BEService.exe", AntiCheat.BattlEye), ("BEService_x64.exe", AntiCheat.BattlEye), ("BELauncher.exe", AntiCheat.BattlEye),
        ("BEClient_x64.dll", AntiCheat.BattlEye), ("BEClient.dll", AntiCheat.BattlEye),
        ("EAAntiCheat.Installer.exe", AntiCheat.Other), ("GameGuard", AntiCheat.Other), ("XIGNCODE", AntiCheat.Other), ("nProtect", AntiCheat.Other),
        ("randgrid.sys", AntiCheat.Other),   // Ricochet (Call of Duty)
        ("NCGuardSDK", AntiCheat.Other), ("NCGuard", AntiCheat.Other),
        ("AntiCheatExpert", AntiCheat.Other), ("AceAntibotClient", AntiCheat.Other), ("TP3Helper.exe", AntiCheat.Other),
        ("SGuard", AntiCheat.Other), ("SGuard64.exe", AntiCheat.Other), ("SGuardSvc64.exe", AntiCheat.Other), ("ACE-Base64.dll", AntiCheat.Other),
        ("ACE-Base.dat", AntiCheat.Other), ("ACE-Setup64.exe", AntiCheat.Other), ("ACE-Service64.exe", AntiCheat.Other), ("ACE-ATS64.dll", AntiCheat.Other),
        ("ACE-CSI64.dll", AntiCheat.Other), ("ACE-DFS64.dll", AntiCheat.Other), ("TenProtect", AntiCheat.Other), ("TesSafe.sys", AntiCheat.Other),
        ("HoYoKProtect.sys", AntiCheat.Other), ("mhypbase.dll", AntiCheat.Other), ("mhyprot.sys", AntiCheat.Other), ("mhyprot2.sys", AntiCheat.Other), ("mhyprot3.sys", AntiCheat.Other),
        ("ACE-BASE.sys", AntiCheat.Other),   // Anti-Cheat Expert's driver in a game's root (Honkai Impact 3rd)
        // HoYoverse's game exes and the HoYoPlay launcher a Steam install carries: each of these games ships a kernel
        // anti-cheat, whatever its driver file is named in a given version
        ("GenshinImpact.exe", AntiCheat.Other), ("YuanShen.exe", AntiCheat.Other), ("StarRail.exe", AntiCheat.Other), ("ZenlessZoneZero.exe", AntiCheat.Other),
        ("BH3.exe", AntiCheat.Other), ("HYP.exe", AntiCheat.Other), ("HYPHelper.exe", AntiCheat.Other), ("HYPWorker.exe", AntiCheat.Other),
        ("EAAntiCheat.GameServiceLauncher.exe", AntiCheat.Other), ("EAAntiCheat.GameServiceLauncher.dll", AntiCheat.Other),
        ("vgk.sys", AntiCheat.Other), ("vgc.exe", AntiCheat.Other),
        // Riot Games' titles, all online; Vanguard installs outside the game's folder (also RiotGames)
        ("VALORANT.exe", AntiCheat.Other), ("VALORANT-Win64-Shipping.exe", AntiCheat.Other), ("League of Legends.exe", AntiCheat.Other),
        ("LeagueClient.exe", AntiCheat.Other), ("LeagueClientUx.exe", AntiCheat.Other), ("LeagueClientUxRender.exe", AntiCheat.Other),
        ("LoR.exe", AntiCheat.Other), ("Lion-Win64-Shipping.exe", AntiCheat.Other),   // Legends of Runeterra, 2XKO
        ("RiotClientServices.exe", AntiCheat.Other), ("RiotClientUx.exe", AntiCheat.Other), ("RiotClientUxRender.exe", AntiCheat.Other),
        ("NeacClient.exe", AntiCheat.Other), ("NeacSafe64.sys", AntiCheat.Other), ("NeacSafe64_ex.sys", AntiCheat.Other),
        ("NEP2.dll", AntiCheat.Other), ("NEPCleaner.exe", AntiCheat.Other),   // NetEase Protect (Aniimo)
        ("BlackCall.aes", AntiCheat.Other), ("BlackCall64.aes", AntiCheat.Other), ("BlackCat64.sys", AntiCheat.Other),
        ("HShield", AntiCheat.Other),
        ("PunkBuster", AntiCheat.Other), ("PnkBstrA.exe", AntiCheat.Other), ("pbsvc.exe", AntiCheat.Other), ("pbsv.dll", AntiCheat.Other),
        ("equ8_conf.json", AntiCheat.Other),
        (".build.info", AntiCheat.Other), (".product.db", AntiCheat.Other),   // Battle.net's install: a Blizzard game another store lists, or one added by hand
        ("Warframe.x64.exe", AntiCheat.Other),   // Digital Extremes' own client-side detection: no driver or folder of its own
        ("arbiter.dll", AntiCheat.Other),   // Arbiter, Halo Infinite's own anti-cheat, in the game's root
        ("gameguard.des", AntiCheat.Other),
        ("DenuvoAC", AntiCheat.Other), ("denuvo-anti-cheat.sys", AntiCheat.Other), ("denuvo-anti-cheat-runtime.dll", AntiCheat.Other),
        ("denuvo-anti-cheat-update-service.exe", AntiCheat.Other), ("Denuvo Anti-Cheat Installer.exe", AntiCheat.Other),
        ("*.xem", AntiCheat.Other), ("*_BE.exe", AntiCheat.BattlEye),
    ];

    /// <summary>The marker names as the proxy's built-in list has them (its check beside the exe): "*x" matches a name ending in x.</summary>
    public static IEnumerable<string> MarkerNames => Markers.Select(m => m.Name);

    /// <summary>Looks for anti-cheat folders/files by name anywhere under the install, under the exe's folder when it is
    /// outside it, and in the names of the folders from the exe's up to the install root. A tree that can't be read whole
    /// (a folder it may not list, the root included; more than <see cref="MaxEntries"/> entries; longer than <see cref="Budget"/>) is <see cref="AntiCheat.Other"/>:
    /// not known to be clean. A junction or symlink is a name, not followed: a folder it points into inside the tree is
    /// read where it is, and one outside is another folder's; one on the way from the install root to the exe is Other. <paramref name="quick"/>: the install root's and the exe
    /// folder's own entries only, for a recheck right after a full one. A game added by hand (<see cref="Store.Manual"/>) also has
    /// the entries of each folder above its install read, one level each, up to a drive root or a store's folder of games
    /// (<see cref="ManualSource.IsLibrary"/>), not included: the folder the user confirmed may be a subfolder of the game's. Battle.net titles are marked conservatively:
    /// Blizzard's Warden is server-side, not a file the install carries. HoYoPlay titles too: every HoYoverse game ships a kernel anti-cheat. Anything under a "Riot Games" folder too. The only anti-cheat detector: engine readers and
    /// middleware detection call it to skip their own work, exe discovery to read no other binary; the app's evaluation acts on its verdict.
    /// <paramref name="ignore"/>: that anti-cheat's markers don't count (an offline session's check for any other).</summary>
    public static AntiCheat DetectAntiCheat(Game game, bool quick = false, TimeSpan? budget = null, AntiCheat ignore = AntiCheat.None)
    {
        AntiCheat Marker(string name) => GameFiles.Marker(name) is var kind && kind == ignore ? AntiCheat.None : kind;
        if (game.Id.StartsWith("battlenet:", StringComparison.Ordinal) || game.Id.StartsWith("hoyoplay:", StringComparison.Ordinal)) return AntiCheat.Other;
        if (RiotGames(game.InstallDir) || RiotGames(game.ExePath)) return AntiCheat.Other;
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(game.ExePath))!;
        bool Inside(string d) => d.Equals(install, StringComparison.OrdinalIgnoreCase) || d.StartsWith(install + '\\', StringComparison.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        var clock = Stopwatch.StartNew();

        AntiCheat Walk(string root, bool deep = true)
        {
            var dirs = new Stack<string>([root]);
            while (dirs.TryPop(out var dir))
            {
                if (!visited.Add(dir)) continue;
                if (clock.Elapsed > (budget ?? Budget)) return AntiCheat.Other;   // also between folders: empty ones queued, a slow open
                IEnumerator<FileSystemInfo> entries;   // streamed: the cap bounds memory too
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", AllNames).GetEnumerator(); }
                catch (DirectoryNotFoundException) when (dir == root) { continue; }   // nothing installed there (access denied throws otherwise)
                using var _ = entries;
                while (entries.MoveNext())
                {
                    var e = entries.Current;
                    if (++seen > MaxEntries || clock.Elapsed > (budget ?? Budget)) return AntiCheat.Other;
                    if (Marker(e.Name) is var kind and not AntiCheat.None) return kind;
                    if (deep && !quick && e is DirectoryInfo d && ((d.Attributes & FileAttributes.ReparsePoint) == 0 || d.LinkTarget == null)) dirs.Push(d.FullName);
                }
            }
            return AntiCheat.None;
        }

        try
        {
            // the walk doesn't follow links: one between the install root and the exe would hide the exe's own folders.
            // The root itself may be one (a game folder moved to another drive): it is walked, so nothing is hidden.
            for (var d = exeDir; d != null && Inside(d) && d.Length > install.Length; d = Path.GetDirectoryName(d))
                if (new DirectoryInfo(d) is { Exists: true } info && (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget != null)
                    return AntiCheat.Other;
            foreach (var root in quick || !Inside(exeDir) ? new[] { install, exeDir } : new[] { install })
                if (Walk(root) is var kind and not AntiCheat.None) return kind;
            for (var d = exeDir; d != null && Inside(d); d = Path.GetDirectoryName(d))
                if (Marker(Path.GetFileName(d)) is var kind and not AntiCheat.None) return kind;
            if (game.Store == Store.Manual)
                for (var d = Path.GetDirectoryName(install); d != null && Path.GetPathRoot(d) != d && !ManualSource.IsLibrary(d); d = Path.GetDirectoryName(d))
                    if (Walk(d, deep: false) is var kind and not AntiCheat.None) return kind;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return AntiCheat.Other; }
        return AntiCheat.None;
    }

    /// <summary>The path has a "Riot Games" folder: the Riot Client installs every Riot title under one.</summary>
    internal static bool RiotGames(string path) =>
        Path.GetFullPath(path).Split(Path.DirectorySeparatorChar).Contains("Riot Games", StringComparer.OrdinalIgnoreCase);

    // AttributesToSkip defaults to Hidden | System: a hidden EasyAntiCheat folder or a system randgrid.sys still counts
    static readonly EnumerationOptions AllNames = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };

    /// <summary>Entries one detection reads at most (names only: an install of 170,000 entries reads in about 0.1 s warm).</summary>
    public const int MaxEntries = 2_000_000;

    /// <summary>The time one detection takes at most (the slowest install measured: about 0.3 s cold).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    internal static AntiCheat Marker(string name)
    {
        foreach (var (marker, kind) in Markers)
            if (marker[0] == '*' ? name.EndsWith(marker[1..], StringComparison.OrdinalIgnoreCase) : name.Equals(marker, StringComparison.OrdinalIgnoreCase)) return kind;
        return AntiCheat.None;
    }
}
