using System.Diagnostics;
using System.Text.Json;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Games;

/// <summary>Install-folder heuristics shared by the game sources.</summary>
public static partial class GameFiles
{
    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = 0 };
    // Chrome Engine keeps the exe 4 folders down (Dying Light: The Beast's ph_ft\work\bin\x64): looked for only when nothing is nearer
    static readonly EnumerationOptions FarDeep = new() { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = 0 };
    static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>ScsKiller's log, for a game a store source skips because reading it threw (<see cref="Skip"/>).</summary>
    public static Action<string>? Warn;

    internal static void Skip(string store, string entry, Exception e) => Warn?.Invoke($"{store}: {entry} skipped: {e.GetType().Name}: {e.Message}");

    /// <summary>The process that creates the D3D12 device: an Unreal game's Shipping exe (<see cref="ShippingExe"/>), else under
    /// a &lt;Project&gt;\Binaries\Win* folder a Shipping exe, then the largest exe (not Engine\Binaries, which only holds helpers like
    /// CrashReportClient), helpers (<see cref="IsHelper"/>) aside; of exes named alike, the one nearest the root; a 32-bit one is
    /// passed over, and none is taken when none is the game's, <see cref="UnrealExe"/>), else
    /// the exe BattlEye's launcher starts (<see cref="BattlEyeTarget"/>), else <paramref name="launcherExe"/>, else the
    /// largest exe near the install root. A launcher among the last two is replaced by the game it starts
    /// (<see cref="LaunchedExe"/>). Exes in a patcher's or installer's copy of the game (<see cref="Staging"/>) are never
    /// picked, nor are those an Xbox app game's config marks as not the game (<see cref="XboxSource.NotTheGame"/>).</summary>
    public static string? FindExe(string installDir, string? launcherExe = null)
    {
        if (!Directory.Exists(installDir)) return null;
        if (ShippingExe(installDir, launcherExe == null ? null : Path.GetFileName(Under("", launcherExe))) is { } shipping) return shipping;
        var excluded = XboxSource.NotTheGame(installDir);
        var unreal = UnrealFolders(installDir)
            .SelectMany(d => EnumerateFiles(d, "*.exe", Flat))
            .Where(f => !IsEngineFolder(installDir, f) && !InStaging(installDir, f) && !IsHelper(f) && !excluded.Contains(Path.GetFullPath(f)))
            .Select(f => new FileInfo(f))
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(same => same.MinBy(f => Path.GetRelativePath(installDir, f.FullName).Count(c => c == Path.DirectorySeparatorChar))!)
            .OrderByDescending(f => IsShipping(f.FullName) && !NotTheGameBuild.Any(n => BuildName(f.FullName).Contains(n, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(f => f.Length).ToList();
        if (unreal.Count > 0 && UnrealExe(installDir, unreal) is { } built) return built;
        if (BattlEyeTarget(installDir) is { } be) return be;
        if (launcherExe != null)
        {
            var p = Path.GetFullPath(Find(installDir, launcherExe));
            if (File.Exists(p)) return RedLauncherTarget(installDir, p) ?? BuildOf32BitLauncher(installDir, p) ?? p;
        }
        // ponytail: non-Unreal games get a guess; Steam's launch target (appinfo.vdf config/launch, SteamSource.Apps) could seed it, but for launcher games it names the launcher
        List<FileInfo> Exes(EnumerationOptions depth) => [.. EnumerateFiles(installDir, "*.exe", depth).Where(f => !NotTheGameExe(installDir, f, excluded)).Select(f => new FileInfo(f))];
        var exes = Exes(Deep) is { Count: > 0 } near ? near : Exes(FarDeep);
        // the exe an EasyAntiCheat bootstrap starts is the game: ELDEN RING NIGHTREIGN's Artbook/OST DLC is a Unity app in its own folder
        if (exes.Where(f => f.Name.Equals("start_protected_game.exe", StringComparison.OrdinalIgnoreCase)).Select(f => EacExecutable(installDir, f.FullName)).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList() is [{ } eac])
            return eac;   // bootstraps naming different exes: none is trusted over the others
        if (exes.FirstOrDefault(f => Directory.Exists(Find(f.DirectoryName!, Path.GetFileNameWithoutExtension(f.Name) + "_Data"))) is { } unity) return unity.FullName;   // Unity: Game.exe + Game_Data
        var guess = exes.OrderBy(f => f.DirectoryName!.Length > installDir.TrimEnd(Path.DirectorySeparatorChar).Length ? 1 : 0)   // root folder first
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
    /// larger than a modular build's exe): then the largest of the others that isn't. Null when none is a Shipping exe and
    /// each is a readable PE that imports no graphics API (<see cref="ImportsGraphics"/>): tools the project's Binaries
    /// keep while the game runs from another folder. Read only in an install without anti-cheat.</summary>
    static string? UnrealExe(string installDir, List<FileInfo> bySize)
    {
        var guess = bySize[0].FullName;
        // the root's and the exe folders' markers: enough to read no binary beside anti-cheat, without a full walk on every scan
        bool Beside(string exe) => DetectAntiCheat(new Game("", "", Store.Other, installDir, exe), quick: true) != AntiCheat.None;
        if (Beside(guess)) return guess;
        // Phantom Blade Zero keeps an archive tool, 8za_v2.exe, in ProjectPBZ\Binaries\Win64 and runs platform\steam\PBZ.exe
        if (bySize.All(f => !IsShipping(f.FullName) && !Beside(f.FullName) && ImportsGraphics(f.FullName) == false)) return null;
        if (bySize.Count == 1 || !Is32Bit(guess)) return guess;
        return bySize.Skip(1).FirstOrDefault(f => !Is32Bit(f.FullName))?.FullName ?? guess;
    }

    /// <summary>The exe SCSKiller uses for a game whose store or user names <paramref name="exe"/>: a Shipping exe it names, else
    /// the one an EasyAntiCheat bootstrap it names starts (<see cref="EacExecutable"/>), then of that the Unreal game's Shipping
    /// exe (<see cref="ShippingExe"/>) over a bootstrap stub or launcher; else that exe.</summary>
    public static string GameExe(string installDir, string exe, Action<string>? ambiguous = null)
    {
        if (IsShipping(exe)) return exe;
        exe = EacExecutable(installDir, exe) ?? exe;
        return IsShipping(exe) ? exe : ShippingExe(installDir, exe, ambiguous) ?? exe;
    }

    /// <summary>The exe the EasyAntiCheat bootstrap <paramref name="exe"/> starts: the "executable" of the
    /// EasyAntiCheat\Settings.json beside it, relative to its folder, spelled as on disk. Null when <paramref name="exe"/> is
    /// that exe or a sibling of it other than start_protected_game.exe, or the file is missing, unreadable, malformed or names
    /// no other exe of the install: one reached through a junction or symlink below it, a helper, launcher or staging copy
    /// (<see cref="NotTheGameExe"/>) don't count. Only read.</summary>
    internal static string? EacExecutable(string installDir, string exe)
    {
        try
        {
            var dir = EacSettings(exe).Directory!.Parent!.FullName;
            if (ReadEacSettings(exe) is not { } root || !root.TryGetProperty("executable", out var named) || named.ValueKind != JsonValueKind.String) return null;
            var target = Path.GetFullPath(Find(dir, named.GetString()!));   // "/" or "\", from the bootstrap's folder
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || target.Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(target).IndexOfAny(['*', '?']) >= 0 || !InsideNoLinks(installDir, target)
                || NotTheGameExe(installDir, target) || XboxSource.LauncherOrUtility(Path.GetFileName(target))) return null;
            // beside its target only start_protected_game.exe is a bootstrap: another sibling is a variant the user picked (a DX12 build)
            if (Path.GetDirectoryName(target)!.Equals(dir, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(exe).Equals("start_protected_game.exe", StringComparison.OrdinalIgnoreCase)) return null;
            // the driver's cache key may hash the exe name's exact case: the file's, not the setting's
            return EnumerateFiles(Path.GetDirectoryName(target)!, Path.GetFileName(target), Flat).FirstOrDefault(f => f.Equals(target, StringComparison.OrdinalIgnoreCase));
        }
        // InvalidOperationException: GetString of a lone surrogate
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException) { return null; }
    }

    /// <summary>The EasyAntiCheat product the game beside <paramref name="exe"/> is, "productid/sandboxid" of its
    /// EasyAntiCheat\Settings.json; null when either is missing or the file can't be read. Only read.</summary>
    internal static string? EacProduct(string exe)
    {
        try
        {
            return ReadEacSettings(exe) is { } root && root.TryGetProperty("productid", out var product) && product.ValueKind == JsonValueKind.String
                && root.TryGetProperty("sandboxid", out var sandbox) && sandbox.ValueKind == JsonValueKind.String ? $"{product.GetString()}/{sandbox.GetString()}" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException) { return null; }
    }

    /// <summary>EasyAntiCheat\Settings.json beside <paramref name="exe"/> when it is a JSON object of at most 64 KiB, else null.</summary>
    static JsonElement? ReadEacSettings(string exe)
    {
        var settings = EacSettings(exe);
        if (!settings.Exists || settings.Length > 64 * 1024) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(settings.FullName), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
    }

    static FileInfo EacSettings(string exe) => new(Find(Path.GetDirectoryName(Path.GetFullPath(exe))!, @"EasyAntiCheat\Settings.json"));

    /// <summary>The size and write time of the EasyAntiCheat settings beside <paramref name="exe"/>, "" without them.</summary>
    internal static string EacSettingsStamp(string exe)
    {
        try { return EacSettings(exe) is { Exists: true } f ? $"|eac:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    /// <summary>An Unreal game's own process: &lt;Name&gt;-&lt;Platform&gt;-Shipping.exe in a &lt;Project&gt;\Binaries\&lt;Platform&gt;
    /// folder (Win64, WinGDK, WinGRTS...) of the install root or a project one or two levels under it (<see cref="UnrealFolders"/>),
    /// else, with none there, of a project beside an Engine folder two levels under it (<see cref="DeepUnrealFolders"/>), with an Engine folder
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
            var unrealRoot = Directory.Exists(Find(installDir, "Engine"));
            var excluded = XboxSource.NotTheGame(installDir);
            List<string> Candidates(IEnumerable<string> folders) => folders
                .SelectMany(d => EnumerateFiles(d, "*-Shipping.exe", Flat))
                .Where(f => IsShipping(f) && !excluded.Contains(Path.GetFullPath(f)) && !IsEngineFolder(installDir, f) && !InStaging(installDir, f) && !IsHelper(f)
                    && !NotTheGameBuild.Any(n => BuildName(f).Contains(n, StringComparison.OrdinalIgnoreCase))
                    && (unrealRoot || Directory.Exists(Find(Path.GetDirectoryName(ProjectDir(f))!, "Engine")))).ToList();
            var found = Candidates(UnrealFolders(installDir));
            // deeper only when nearer finds nothing: a deeper second project never makes a nearer pick ambiguous
            if (found.Count == 0) found = Candidates(DeepUnrealFolders(installDir));
            all = found
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

    /// <summary>An exe in a &lt;Project&gt;\Binaries\Win* folder.</summary>
    internal static bool InBinaries(string exe) =>
        Path.GetDirectoryName(exe) is { } dir && Path.GetFileName(dir).StartsWith("Win", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), "Binaries", StringComparison.OrdinalIgnoreCase);

    /// <summary>The &lt;Project&gt;\Binaries\Win* folders of the install root and of projects one or two levels under it, or three
    /// through a WindowsNoEditor folder (Client\WindowsNoEditor\&lt;Project&gt;). Win32 is left out: a 32-bit exe never creates the
    /// game's D3D12 device.</summary>
    static IEnumerable<string> UnrealFolders(string installDir) =>
        PlatformFolders(EnumerateDirectories(installDir, "*", Flat)
            .SelectMany(d => EnumerateDirectories(d, "*", Flat).SelectMany(Packaged).Prepend(d)).Prepend(installDir));

    /// <summary>The &lt;Project&gt;\Binaries\Win* folders of projects beside an Engine folder two levels under the install
    /// (x64\Windows\&lt;Project&gt;, Immortals of Aveum's).</summary>
    static IEnumerable<string> DeepUnrealFolders(string installDir) =>
        PlatformFolders(EnumerateDirectories(installDir, "*", Flat)
            .SelectMany(d => EnumerateDirectories(d, "*", Flat))
            .Where(d => Directory.Exists(Find(d, "Engine")))
            .SelectMany(d => EnumerateDirectories(d, "*", Flat))
            .Where(p => !Path.GetFileName(p).Equals("Engine", StringComparison.OrdinalIgnoreCase)));

    static IEnumerable<string> PlatformFolders(IEnumerable<string> projects) =>
        projects.Select(p => Find(p, "Binaries"))
            .Where(Directory.Exists)
            .SelectMany(b => EnumerateDirectories(b, "Win*", Flat))
            .Where(d => !Path.GetFileName(d).Equals("Win32", StringComparison.OrdinalIgnoreCase));

    static IEnumerable<string> Packaged(string dir) => Path.GetFileName(dir).Equals("WindowsNoEditor", StringComparison.OrdinalIgnoreCase)
        ? EnumerateDirectories(dir, "*", Flat).Prepend(dir) : [dir];

    static bool IsX64(string exe)
    {
        try { using var pe = PeFile.Open(exe); return pe.PEHeaders.CoffHeader.Machine == System.Reflection.PortableExecutable.Machine.Amd64; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { return false; }
    }

    /// <summary>An i386 PE that runs as a 32-bit process (not an AnyCPU .NET exe); false when it isn't one or can't be read.</summary>
    internal static bool Is32Bit(string exe)
    {
        try
        {
            using var pe = PeFile.Open(exe);
            var h = pe.PEHeaders;
            return h.CoffHeader.Machine == System.Reflection.PortableExecutable.Machine.I386
                && !(h.CorHeader?.Flags is { } f && f.HasFlag(System.Reflection.PortableExecutable.CorFlags.ILOnly) && !f.HasFlag(System.Reflection.PortableExecutable.CorFlags.Requires32Bit));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { return false; }
    }

    /// <summary>The game a launcher (<paramref name="guess"/>) starts: the target of CD PROJEKT RED's launcher-configuration.json
    /// next to it, else the 64-bit build a 32-bit guess is named for (<see cref="BuildOf32BitLauncher"/>), else, when the guess
    /// imports no graphics API, the one larger exe of <paramref name="exes"/> that does (no binary read in an install with
    /// anti-cheat). Anything else (one unreadable, several, none) keeps the guess.</summary>
    static string LaunchedExe(string installDir, FileInfo guess, IReadOnlyList<FileInfo> exes)
    {
        if (RedLauncherTarget(installDir, guess.FullName) is { } red) return red;
        var larger = exes.Where(f => f.Length > guess.Length).ToList();
        if (BuildOf32BitLauncher(installDir, guess.FullName) is { } build) return build;
        if (larger.Count == 0 || ImportsGraphics(guess.FullName) != false
            || DetectAntiCheat(new Game("", "", Store.Other, installDir, guess.FullName)) != AntiCheat.None) return guess.FullName;
        var imports = larger.Select(f => (f.FullName, Imports: ImportsGraphics(f.FullName))).ToList();
        return imports.All(x => x.Imports != null) && imports.Where(x => x.Imports == true).ToList() is [var game] ? game.FullName : guess.FullName;
    }

    /// <summary>The one x64 exe of the install named after the 32-bit <paramref name="launcher"/> (BeamNG.drive.exe starts
    /// Bin64\BeamNG.drive.x64.exe, not BinArm64\BeamNG.drive.arm64.exe); null when the launcher isn't 32-bit, there is none
    /// or several, or the install has anti-cheat (no other binary is read then).</summary>
    static string? BuildOf32BitLauncher(string installDir, string launcher)
    {
        if (!Is32Bit(launcher)) return null;
        var stem = Path.GetFileNameWithoutExtension(launcher) + ".";
        var named = EnumerateFiles(installDir, "*.exe", Deep)
            .Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase) && !NotTheGameExe(installDir, f)).ToList();
        if (named.Count == 0 || DetectAntiCheat(new Game("", "", Store.Other, installDir, launcher), quick: true) != AntiCheat.None) return null;
        return named.Where(IsX64).ToList() is [var build] ? Path.GetFullPath(build) : null;
    }

    /// <summary>The 64BitExe of the BattlEye\BELauncher.ini files near the install root (relative to the folder holding
    /// BattlEye\, where the game's *_BE.exe sits): the one existing exe inside the install they
    /// name; null when they name none or several.</summary>
    internal static string? BattlEyeTarget(string installDir)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ini in EnumerateFiles(installDir, "BELauncher.ini", Deep))
        {
            var dir = Path.GetDirectoryName(ini)!;
            if (!Path.GetFileName(dir).Equals("BattlEye", StringComparison.OrdinalIgnoreCase) || InStaging(installDir, ini)) continue;
            string[] lines;
            try { lines = File.ReadAllLines(ini); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var rel = lines.Select(l => l.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals("64BitExe", StringComparison.OrdinalIgnoreCase))?[1].Trim();
            if (string.IsNullOrEmpty(rel) || Path.IsPathRooted(rel) || rel.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
            try { if (Path.GetFullPath(Find(Path.GetDirectoryName(dir)!, rel)) is var exe && InsideNoLinks(installDir, exe) && !InStaging(installDir, exe)) targets.Add(exe); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return targets.Count == 1 ? targets.First() : null;
    }

    /// <summary><paramref name="target"/> is an existing file inside <paramref name="installDir"/>, reached through no junction or
    /// symlink below it (the anti-cheat scan doesn't follow those; the root itself may be one).</summary>
    internal static bool InsideNoLinks(string installDir, string target)
    {
        var root = DirKey(installDir);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return false;
        for (var p = target; p.Length > root.Length; p = Path.GetDirectoryName(p)!)
            if ((p == target ? new FileInfo(p) : (FileSystemInfo)new DirectoryInfo(p)) is { LinkTarget: not null }) return false;
        return true;
    }

    /// <summary>launcher-configuration.json's executablePath (Cyberpunk 2077), else its executables[]: the entry whose
    /// description is its "fallback", else the first. Only a relative path to an existing .exe inside the install that
    /// discovery could pick (<see cref="NotTheGameExe"/>); null for none, or a file over 64 KiB.</summary>
    static string? RedLauncherTarget(string installDir, string exe)
    {
        var config = new FileInfo(Find(Path.GetDirectoryName(exe)!, "launcher-configuration.json"));
        if (!config.Exists || config.Length > 64 * 1024) return null;
        // refused before any of it is touched: rooted, a colon (drive, share, device, stream), a short name (GetFullPath looks it up), NUL
        string? Target(string rel) =>
            !Path.IsPathRooted(rel) && rel.IndexOfAny([':', '~', '\0']) < 0 && Path.GetFullPath(Find(config.DirectoryName!, rel)) is var path
            && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !NotTheGameExe(installDir, path) && InsideNoLinks(installDir, path) ? path : null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(config.FullName));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (r.TryGetProperty("executablePath", out var flat) && flat.ValueKind == JsonValueKind.String && Target(flat.GetString()!) is { } named) return named;
            if (!r.TryGetProperty("executables", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var fallback = r.TryGetProperty("fallback", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            var entries = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            if (entries.Count == 0) return null;
            var pick = entries.FirstOrDefault(e => e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() == fallback,
                entries[0]);
            if (!pick.TryGetProperty("executable", out var x) || x.ValueKind != JsonValueKind.Object) return null;
            string? S(string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return S("fileName") is { Length: > 0 } name ? Target(Path.Combine(S("directoryPath") ?? "", name)) : null;
        }
        // InvalidOperationException: GetString of a lone surrogate
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
    }

    // Streamline's interposer stands in for dxgi and d3d12 in games that ship it: witcher3.exe imports neither
    static readonly string[] GraphicsDlls = ["d3d12.dll", "d3d11.dll", "dxgi.dll", "sl.interposer.dll", "vulkan-1.dll"];

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
    /// System32's, so the recorder never loads. An import found in neither the exe's folder nor <paramref name="system"/>
    /// (<see cref="SystemFolder"/>), API sets aside, is resolved somewhere this can't follow: null, as for a module that can't
    /// be read. A DLL of the exe's folder that forwards an export to d3d12 counts as importing it. <paramref name="files"/>
    /// gets the files the answer depends on: sl.interposer.dll and each import's path in the exe's folder, found or not, and
    /// for a null answer the system folder (<see cref="Stamp"/>).</summary>
    internal static bool? StreamlineFirst(string exe, string? system, List<string> files)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var interposer = Find(dir, "sl.interposer.dll");
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
                    var local = Find(dir, dll);
                    files.Add(local);
                    if (File.Exists(local)) todo.Push((local, true));
                    else if (system != null && Find(system, dll) is var shipped && File.Exists(shipped)) todo.Push((shipped, false));
                    else
                    {
                        if (system != null) files.Add(system);   // a DLL put there later, or a prefix Proton makes at the game's first start
                        return null;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
        return true;
    }

    /// <summary>The folder of the system DLLs the game's exe loads: System32, or under Proton its prefix's system32; null when
    /// that isn't known (a game Steam doesn't run).</summary>
    internal static partial string? SystemFolder(Game game);

    /// <summary>SHA-256 of each AMD AGS build whose version is known to fit the recorder's AGS wrapper (its exports are still
    /// matched): Cyberpunk 2077's AGS 5.4.</summary>
    internal static HashSet<string> WrappableAgs { get; set; } = new(["C1F0BD493B470FA369D681C370BBD46E019A7196DBE03D91272EA0BE214B3384"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The exe has AGS make its DirectX 12 device (agsDriverExtensionsDX12_CreateDevice, which can reach System32's
    /// d3d12.dll without a d3d12.dll beside the exe seeing it), with an AGS the wrapper fits (<see cref="AgsUnfit"/>): the one
    /// beside the exe, or the game's own once the wrapper stands in for it (the name it links). <paramref name="unfit"/>: why
    /// the wrapper doesn't fit an AGS that makes the device, null otherwise.</summary>
    internal static bool AgsMakesTheDevice(string exe, string? wrapper, out string? unfit)
    {
        unfit = null;
        var dir = Path.GetDirectoryName(exe)!;
        var ags = Find(dir, "amd_ags_x64.dll");
        try
        {
            if (App.ScsKiller.IsOurProxy(ags)) ags = Path.Combine(dir, App.ScsKiller.AgsLinked(ags));
            if (!File.Exists(ags)) return false;
            using (var pe = PeFile.Open(exe))
                if (!PeFile.ImportedFunctions(pe, "amd_ags_x64.dll").Contains("agsDriverExtensionsDX12_CreateDevice")) return false;
            unfit = AgsUnfit(ags, exe, wrapper);
            return unfit == null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return false; }
    }

    /// <summary>Why the AGS wrapper (<paramref name="wrapper"/>) can't stand in for <paramref name="ags"/> in front of
    /// <paramref name="exe"/>, null when it can: a build in <see cref="WrappableAgs"/> or an AGS 5.2 to 5.4, that exports
    /// every name the exe, the DLLs beside it and the wrapper link it by, while the wrapper exports every name the game links.</summary>
    internal static string? AgsUnfit(string ags, string exe, string? wrapper)
    {
        var known = WrappableAgs.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ags))));
        var v = known ? null : PeVersionInfo.GetVersionInfo(ags);
        var version = v == null ? "a known build" : v.FileMajorPart == 0 ? "no version" : $"version {v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
        // amd_ags.h 5.2-5.4: CreateDevice's extension params start {app, engine, app version, engine version} and its returned
        // params with the device, all the wrapper reads. AGS 6 renamed exports the wrapper forwards (agsInit, agsDeInit).
        if (v != null && (v.FileMajorPart != 5 || v.FileMinorPart is < 2 or > 4)) return version;
        if (wrapper == null || !File.Exists(wrapper)) return version + ", the recorder's AGS wrapper is missing";
        try
        {
            using var a = PeFile.Open(ags);
            using var e = PeFile.Open(exe);
            using var w = PeFile.Open(wrapper);
            var has = PeFile.ExportNames(a).ToHashSet(StringComparer.Ordinal);
            var wraps = PeFile.ExportNames(w).ToHashSet(StringComparer.Ordinal);
            // the exe's DLLs beside it link AGS too, by ordinal as well: each name must reach the same function
            var linked = PeFile.ImportedFunctions(e, "amd_ags_x64.dll").Concat(Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "*.dll")
                .Where(d => !Path.GetFileName(d).StartsWith("amd_ags_", StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => { using var m = PeFile.Open(d); return PeFile.ImportedFunctions(m, "amd_ags_x64.dll").ToList(); })).Distinct().ToList();
            var missing = linked.Concat(PeFile.ImportedFunctions(w, App.ScsKiller.AgsChainName)).FirstOrDefault(n => !has.Contains(n))
                ?? linked.FirstOrDefault(n => !wraps.Contains(n));
            return missing != null ? $"{version}, {missing} not matched" : null;
        }
        catch (Exception x) when (x is BadImageFormatException or InvalidOperationException) { return version + ", a library's imports or exports unreadable"; }
    }

    /// <summary>Each file's size and write time, a folder's write time, "-" when missing: to tell when a cached answer about
    /// them is stale.</summary>
    internal static string Stamp(IEnumerable<string> files) =>
        string.Join('|', files.Select(f => new FileInfo(f) is { Exists: true } i ? $"{i.Length}:{i.LastWriteTimeUtc.Ticks}"
            : new DirectoryInfo(f) is { Exists: true } d ? $"d:{d.LastWriteTimeUtc.Ticks}" : "-"));

    /// <summary>The exes near the install root (as <see cref="FindExe"/> looks) that import a graphics API.</summary>
    internal static IEnumerable<string> GraphicsExes(string installDir) => EnumerateFiles(installDir, "*.exe", Deep)
        .Where(f => !NotTheGame.Any(s => Path.GetRelativePath(installDir, f).Contains(s, StringComparison.OrdinalIgnoreCase)) && !IsEngineFolder(installDir, f) && !InStaging(installDir, f) && !IsHelper(f))
        .Where(f => ImportsGraphics(f) == true);

    /// <summary><paramref name="path"/> (below <paramref name="dir"/>) is in a Wine prefix, a folder holding
    /// drive_c/windows/system32, whose DLLs are Wine's, not the game's. A prefix that holds <paramref name="exe"/> (dir itself
    /// as one) keeps its files outside drive_c/windows.</summary>
    internal static bool InWinePrefix(string dir, string exe, string path)
    {
        var parts = Path.GetRelativePath(dir, path).Split(Path.DirectorySeparatorChar);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].Equals("drive_c", StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = Path.Combine(dir, Path.Combine(parts[..i]));
            if (Directory.Exists(Path.Combine(prefix, parts[i], "windows", "system32")))
                return !Inside(prefix, exe) || parts[i + 1].Equals("windows", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary><paramref name="path"/> is inside the folder <paramref name="dir"/> (case-insensitive, full paths).</summary>
    public static bool Inside(string dir, string path) => Inside(dir, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>The drive or mount point <paramref name="dir"/> is on: the longest drive root holding it, else its path root
    /// (a network share); null for a path that can't be read. <paramref name="roots"/>: <see cref="DriveRoots"/>, read once for many.</summary>
    public static string? DriveOf(string dir, IReadOnlyList<string>? roots = null)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            var names = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return (roots ?? DriveRoots())
                .Where(r => full.StartsWith(r, names) && (full.Length == r.Length || Path.EndsInDirectorySeparator(r) || full[r.Length] == Path.DirectorySeparatorChar))
                .MaxBy(r => r.Length) ?? Path.GetPathRoot(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public static IReadOnlyList<string> DriveRoots()
    {
        try { return [.. DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary><see cref="Inside(string, string)"/>, the names compared as <paramref name="names"/> says.</summary>
    internal static bool Inside(string dir, string path, StringComparison names) =>
        dir.Length > 0 && Path.GetFullPath(path).StartsWith(DirKey(dir) + Path.DirectorySeparatorChar, names);

    /// <summary>The folder's path with every junction and symlink on the way resolved, as a process's image path names
    /// it; null when it can't be opened.</summary>
    public static partial string? FinalPath(string dir);

    /// <summary>Where the file system holding <paramref name="path"/> (or its nearest folder that exists) is mounted: a
    /// drive's root on Windows, a mount point on Linux. Null when none can be read.</summary>
    public static partial string? VolumeOf(string path);

    /// <summary>A file's identity (FILE_ID_INFO's 24 bytes: the volume serial and the 128-bit file id, the same through any
    /// link or junction; under Wine <see cref="WineFileId"/>) and its <see cref="FinalPath(string)"/>, each null when it can't
    /// be read; null when the file can't be opened. As the proxy keys its exe (recorder_key.h file_key).</summary>
    public static partial (byte[]? Id, string? Final)? Identity(string path);

    /// <summary>What the proxy under Wine puts in place of FILE_ID_INFO's volume serial, which is that of the drive letter the
    /// file was opened through (recorder_key.h kWineVolume).</summary>
    internal static ReadOnlySpan<byte> WineVolume => "wine-ino"u8;

    /// <summary>The proxy's identity of a file under Wine (recorder_key.h id_key): <see cref="WineVolume"/>, then Wine's file
    /// id, the inode.</summary>
    internal static byte[] WineFileId(ulong inode)
    {
        var id = new byte[24];
        WineVolume.CopyTo(id);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(8), inode);
        return id;
    }

    /// <summary>The folder's own name, also from a path ending in a separator (Ubisoft Connect's end in "/").</summary>
    public static string FolderName(string dir) => Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));

    /// <summary>An install folder compared across sources: full path, no trailing separator (a drive root keeps its own).</summary>
    public static string DirKey(string dir) => dir.Length == 0 ? dir : Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    /// <summary><paramref name="path"/> relative to <paramref name="root"/> with '\' separators on every system: the form of a
    /// game-relative id, library name, content stamp or shared key, the same for an install read on Windows or Linux.</summary>
    public static string Rel(string root, string path) =>
        Path.DirectorySeparatorChar == '\\' ? Path.GetRelativePath(root, path) : Path.GetRelativePath(root, path).Replace('/', '\\');

    /// <summary>The file Path.ChangeExtension names, as Windows finds it: on Windows that path, elsewhere <see cref="Find"/>'s
    /// path for its name in <paramref name="path"/>'s folder.</summary>
    public static partial string ChangeExtension(string path, string extension);

    /// <summary><see cref="Rel(string, string)"/> of <paramref name="path"/>, which is <paramref name="found"/> or under it, with
    /// <paramref name="found"/>'s last segments spelled as <paramref name="rel"/> (the path <see cref="Find"/> gave
    /// <paramref name="found"/> for) spells them: the id or stamp Windows makes of the same file.</summary>
    public static partial string Rel(string root, string path, string found, string rel);

    /// <summary>The Windows-form relative path <paramref name="rel"/> ('\' separators: a reader's constant, a path read from a
    /// game's or launcher's files) as a native path under <paramref name="root"/>.</summary>
    public static string Under(string root, string rel) =>
        Path.Combine(root, Path.DirectorySeparatorChar == '\\' ? rel : rel.Replace('\\', '/'));

    /// <summary>The file or folder <see cref="Under"/> names, as Windows' case-insensitive lookup finds it: on Windows that path,
    /// with no IO. Elsewhere, when it doesn't exist, each segment of <paramref name="rel"/> matched to its folder's entries
    /// ignoring case (an entry spelled as given first, else the first in ordinal order), and <see cref="Under"/>'s path when
    /// a segment matches none. For IO only: an id, a map name or a stamp keeps <paramref name="rel"/>'s own spelling.</summary>
    public static partial string Find(string root, string rel);

    /// <summary>Every file or folder <see cref="Under"/> names but for case: on Windows that path, with no IO. Elsewhere each
    /// segment matched to all of its folder's entries ignoring case, those spelled as given first: where <see cref="Find"/>
    /// gives one of names alike, a check that one exists sees each.</summary>
    public static partial IEnumerable<string> FindAll(string root, string rel);

    /// <summary>Directory.EnumerateFiles as Windows gives a game's files: on Windows that call. Elsewhere the pattern matches
    /// ignoring case, each folder's entries come in NTFS's order (<see cref="StringComparer.OrdinalIgnoreCase"/> of the name)
    /// and a recursive enumeration lists folder by folder, breadth first, as .NET's enumerator does on Windows, but never
    /// walks into a link to a folder (a Wine prefix's dosdevices links name the whole system).</summary>
    public static partial IEnumerable<string> EnumerateFiles(string dir, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly);

    /// <inheritdoc cref="EnumerateFiles(string, string, SearchOption)"/>
    public static partial IEnumerable<string> EnumerateFiles(string dir, string pattern, EnumerationOptions options);

    /// <inheritdoc cref="EnumerateFiles(string, string, SearchOption)"/>
    public static partial IEnumerable<string> EnumerateDirectories(string dir, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly);

    /// <inheritdoc cref="EnumerateFiles(string, string, SearchOption)"/>
    public static partial IEnumerable<string> EnumerateDirectories(string dir, string pattern, EnumerationOptions options);

    /// <inheritdoc cref="EnumerateFiles(string, string, SearchOption)"/>
    public static partial IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo dir, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly);

    /// <inheritdoc cref="EnumerateFiles(string, string, SearchOption)"/>
    public static partial IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo dir, string pattern, EnumerationOptions options);

    /// <inheritdoc cref="EnumerateFiles(string, string, SearchOption)"/>
    public static partial IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo dir, string pattern, EnumerationOptions options);

    /// <summary>The order of a game's file paths where it feeds a content stamp, a dedupe or a map's name: ordinal and
    /// case-insensitive, '/' read as '\', so files under one root sort as their <see cref="Rel"/> paths do on Windows.</summary>
    public static readonly IComparer<string> PathOrder = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : new SlashAsBackslash();

    sealed class SlashAsBackslash : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == null || y == null) return x == null ? y == null ? 0 : -1 : 1;
            char[]? rented = null;
            var n = x.Length + y.Length;
            Span<char> buf = n <= 1024 ? stackalloc char[n] : rented = System.Buffers.ArrayPool<char>.Shared.Rent(n);
            Span<char> a = buf[..x.Length], b = buf.Slice(x.Length, y.Length);
            x.AsSpan().Replace(a, '/', '\\');
            y.AsSpan().Replace(b, '/', '\\');
            var order = MemoryExtensions.CompareTo(a, b, StringComparison.OrdinalIgnoreCase);
            if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
            return order;
        }
    }

    static readonly string[] NotTheGame = ["redist", "directx", "crash", "unins", "setup", "vconsole"];   // vconsole2.exe: Source 2's developer console

    // the Epic Online Services installer is 64-bit and can be the largest exe in an Unreal game's Binaries\Win64
    static readonly string[] Helpers = ["EpicOnlineServices", "EOSBootstrapper", "EpicWebHelper", "CrashReport", "UnrealCEFSubProcess",
        "Prereq", "setup", "redist", "installer", "unins",
        "ffmpeg",   // Once Human's ffmpeg.exe is larger than ONCE_HUMAN.exe beside it
        "HD2Arsenal",   // a HELLDIVERS 2 mod manager kept in the game's root, where the guess looks before bin\helldivers2.exe
        "crs-handler", "crs-uploader", "crs-video"];   // Sony's PC crash reporter, beside the exe of its ports and still running after a crash

    /// <summary>An installer, redistributable or engine helper shipped beside a game's exe, by its file name.</summary>
    internal static bool IsHelper(string exe) => Helpers.Any(h => Path.GetFileName(exe).Contains(h, StringComparison.OrdinalIgnoreCase)) || TrialBeside(exe);

    /// <summary>&lt;Name&gt;_Trial.exe with &lt;Name&gt;.exe beside it: the EA app leaves EA SPORTS WRC's trial build next to the game.</summary>
    static bool TrialBeside(string exe) => Path.GetFileNameWithoutExtension(exe) is var n && n.EndsWith("_Trial", StringComparison.OrdinalIgnoreCase)
        && File.Exists(Find(Path.GetDirectoryName(exe) ?? "", n[..^6] + ".exe"));

    /// <summary>Folders a patcher or installer keeps a copy of the game's files in, which never run: Stellar Blade's
    /// PatchData\SB\Binaries\Win64 holds a second SB-Win64-Shipping.exe; the EA app's __Installer its own tools.</summary>
    static readonly string[] Staging = ["PatchData", "__Installer", "Backup", "Backups", "Staging"];

    // a drive root's own folders, Hidden|System: the recycle bin keeps deleted exes
    static readonly string[] WindowsFolders = ["$RECYCLE.BIN", "System Volume Information"];

    internal static bool WindowsFolder(string name) => WindowsFolders.Contains(name, StringComparer.OrdinalIgnoreCase);

    static bool InStaging(string installDir, string path) =>
        Path.GetRelativePath(installDir, Path.GetDirectoryName(path)!).Split(Path.DirectorySeparatorChar).Any(seg => Staging.Contains(seg, StringComparer.OrdinalIgnoreCase) || WindowsFolder(seg));

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
        ("ONCE_HUMAN.exe", AntiCheat.Other),   // NetEase's anti-cheat installs outside the game folder (also AntiCheatIds)
        ("forzahorizon5.exe", AntiCheat.Other), ("forzahorizon6.exe", AntiCheat.Other),   // Playground's own detection leaves no file of its own (also AntiCheatIds)
        ("RobloxPlayerBeta.exe", AntiCheat.Other), ("RobloxPlayerBeta.dll", AntiCheat.Other), ("RobloxPlayerLauncher.exe", AntiCheat.Other),
        ("RobloxPlayerInstaller.exe", AntiCheat.Other),   // Hyperion is built into the player, no file of its own
        ("arbiter.dll", AntiCheat.Other),   // Arbiter, Halo Infinite's own anti-cheat, in the game's root
        ("gameguard.des", AntiCheat.Other),
        ("DenuvoAC", AntiCheat.Other), ("denuvo-anti-cheat.sys", AntiCheat.Other), ("denuvo-anti-cheat-runtime.dll", AntiCheat.Other),
        ("denuvo-anti-cheat-update-service.exe", AntiCheat.Other), ("Denuvo Anti-Cheat Installer.exe", AntiCheat.Other),
        ("Elytra", AntiCheat.Other), ("Elytra.exe", AntiCheat.Other), ("Elytra-Setup.exe", AntiCheat.Other),   // WARDOGS: its service installs under Program Files
        ("*.xem", AntiCheat.Other), ("*_BE.exe", AntiCheat.BattlEye),
    ];

    /// <summary>The marker names as the proxy's built-in list has them (its check beside the exe): "*x" matches a name ending in x.</summary>
    public static IEnumerable<string> MarkerNames => Markers.Select(m => m.Name);

    /// <summary>Markers that count only at their path under a folder the check treats as the install (its root, the exe's
    /// folder, a hand-added game's folders above): Blizzard's static TACT container, which Overwatch's Steam install
    /// carries instead of Battle.net's files. Anywhere else the name means nothing. Not in the proxy's list: it knows only
    /// the exe's folder, by name.</summary>
    internal static readonly string[] RootMarkers = [@"data\.build.config"];

    /// <summary>Every marker, as a stamp: a clean verdict holds only for the markers it was checked against.</summary>
    public static readonly string MarkersStamp = string.Join('|', MarkerNames.Concat(RootMarkers));

    static string? RootMarker(string dir) => RootMarkers.SelectMany(m => FindAll(dir, m)).FirstOrDefault(File.Exists);

    /// <summary>Looks for anti-cheat folders/files by name anywhere under the install, under the exe's folder when it is
    /// outside it, and in the names of the folders from the exe's up to the install root. A tree that can't be read whole
    /// (a folder it may not list, the root included; more than <see cref="MaxEntries"/> entries; longer than <see cref="Budget"/>) is <see cref="AntiCheat.Other"/>:
    /// not known to be clean. A junction or symlink is a name, not followed: a folder it points into inside the tree is
    /// read where it is, and one outside is another folder's; one on the way from the install root to the exe is Other. <paramref name="quick"/>: the install root's and the exe
    /// folder's own entries only, for a recheck right after a full one. A game added by hand (<see cref="Store.Manual"/>), or to
    /// Steam as a non-Steam game (<see cref="SteamShortcuts"/>), also has
    /// the entries of each folder above its install read, one level each, up to a drive root or a store's folder of games
    /// (<see cref="ManualSource.IsLibrary"/>), not included: the folder the user confirmed, or the one found from the exe they
    /// picked, may be a subfolder of the game's. Battle.net titles are marked conservatively:
    /// Blizzard's Warden is server-side, not a file the install carries. HoYoPlay titles too: every HoYoverse game ships a kernel anti-cheat. So are the truck simulators on a PC with TruckersMP (<see cref="TruckersMpExes"/>), and anything under a "Riot Games" folder or listed by the Riot Client. The only anti-cheat detector: engine readers and
    /// middleware detection call it to skip their own work, exe discovery to read no other binary; the app's evaluation acts on its verdict.
    /// <see cref="RootMarkers"/> count only under the install root, the exe's folder and a hand-added game's folders above.
    /// A Steam app one of whose launch options, in any branch, starts a marker (<see cref="Vac.LaunchExes"/>) is Other when the
    /// files show none: an install that carries an anti-cheat no marker names yet.
    /// <paramref name="ignore"/>: that anti-cheat's markers don't count (an offline session's check for any other).
    /// <paramref name="at"/>: given the path of the marker found; not called for a verdict no file gave (the game's store, a
    /// tree that can't be read whole, TruckersMP, a Riot Games path, a launch option).
    /// <paramref name="steamRoot"/>: Steam's folder (default: Steam's own).
    /// <paramref name="late"/>: called when the time ran out, an Other that isn't a finding.
    /// <paramref name="unreadable"/>: given the error when a read failed, also an Other that isn't a finding.</summary>
    public static AntiCheat DetectAntiCheat(Game game, bool quick = false, TimeSpan? budget = null, AntiCheat ignore = AntiCheat.None, Action<string>? at = null,
        string? steamRoot = null, Action? late = null, Action<Exception>? unreadable = null)
    {
        AntiCheat Marker(string name) => GameFiles.Marker(name) is var kind && kind == ignore ? AntiCheat.None : kind;
        if (game.Id.StartsWith("battlenet:", StringComparison.Ordinal) || game.Id.StartsWith("hoyoplay:", StringComparison.Ordinal)) return AntiCheat.Other;
        if (game.Id.StartsWith("riot:", StringComparison.Ordinal) || RiotGames(game.InstallDir) || RiotGames(game.ExePath)) return AntiCheat.Other;
        if (AntiCheatIds.Contains(game.Id, StringComparer.OrdinalIgnoreCase)) return AntiCheat.Other;
        // the same install listed under another id (added by hand, also through a junction): the Steam app whose folder it is counts too
        if (!game.Id.StartsWith("steam:", StringComparison.Ordinal) && SteamAntiCheatApp(game, steamRoot)) return AntiCheat.Other;
        var install =Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(game.ExePath))!;
        // off Windows a folder spelled otherwise is another folder, and one reached twice has one real path
        var named = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Inside(string d) => d.Equals(install, named) || d.StartsWith(install + Path.DirectorySeparatorChar, named);
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var seen = 0;
        var clock = Stopwatch.StartNew();
        bool Late()
        {
            if (clock.Elapsed <= (budget ?? Budget)) return false;
            late?.Invoke();
            return true;
        }

        AntiCheat Walk(string root, bool deep = true)
        {
            var dirs = new Stack<string>([root]);
            while (dirs.TryPop(out var dir))
            {
                if (!visited.Add(OperatingSystem.IsWindows() ? dir : FinalPath(dir) ?? dir)) continue;
                if (Late()) return AntiCheat.Other;   // also between folders: empty ones queued, a slow open
                IEnumerator<FileSystemInfo> entries;   // the cap bounds memory too
                try
                {
                    // null: the folder's read stopped at the cap or the budget
                    if (MarkerEntries(new DirectoryInfo(dir), MaxEntries - seen, Late) is not { } listed) return AntiCheat.Other;
                    entries = listed.GetEnumerator();
                }
                catch (DirectoryNotFoundException) when (dir == root) { continue; }   // nothing installed there (access denied throws otherwise)
                using var _ = entries;
                while (entries.MoveNext())
                {
                    var e = entries.Current;
                    if (++seen > MaxEntries || Late()) return AntiCheat.Other;
                    if (Marker(e.Name) is var kind and not AntiCheat.None)
                    {
                        at?.Invoke(e.FullName);
                        return kind;
                    }
                    if (deep && !quick && e is DirectoryInfo d && ((d.Attributes & FileAttributes.ReparsePoint) == 0 || d.LinkTarget == null)) dirs.Push(d.FullName);
                }
            }
            return AntiCheat.None;
        }

        try
        {
            if (TruckersMpExes.Contains(Path.GetFileName(game.ExePath), StringComparer.OrdinalIgnoreCase) && TruckersMpHere()) return AntiCheat.Other;
            // the walk doesn't follow links: one between the install root and the exe would hide the exe's own folders.
            // The root itself may be one (a game folder moved to another drive): it is walked, so nothing is hidden.
            for (var d = exeDir; d != null && Inside(d) && d.Length > install.Length; d = Path.GetDirectoryName(d))
                if (new DirectoryInfo(d) is { Exists: true } info && (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget != null)
                    return AntiCheat.Other;
            foreach (var root in quick || !Inside(exeDir) ? new[] { install, exeDir } : new[] { install })
            {
                if (ignore != AntiCheat.Other && RootMarker(root) is { } marker)
                {
                    at?.Invoke(marker);
                    return AntiCheat.Other;
                }
                if (Walk(root) is var kind and not AntiCheat.None) return kind;
            }
            for (var d = exeDir; d != null && Inside(d); d = Path.GetDirectoryName(d))
                if (Marker(Path.GetFileName(d)) is var kind and not AntiCheat.None)
                {
                    at?.Invoke(d);
                    return kind;
                }
            if (game.Store == Store.Manual || SteamShortcuts.Is(game.Id) || LutrisSource.Is(game.Id))
                for (var d = Path.GetDirectoryName(install); d != null && Path.GetPathRoot(d) != d && !ManualSource.IsLibrary(d); d = Path.GetDirectoryName(d))
                {
                    if (ignore != AntiCheat.Other && RootMarker(d) is { } marker)
                    {
                        at?.Invoke(marker);
                        return AntiCheat.Other;
                    }
                    if (Walk(d, deep: false) is var kind and not AntiCheat.None) return kind;
                }
            if (Vac.LaunchExes(game, steamRoot).Any(e => Marker(e) != AntiCheat.None)) return AntiCheat.Other;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            unreadable?.Invoke(e);
            return AntiCheat.Other;
        }
        return AntiCheat.None;
    }

    /// <summary>The path has a "Riot Games" folder: the Riot Client installs every Riot title under one.</summary>
    internal static bool RiotGames(string path) =>
        Path.GetFullPath(path).Split(Path.DirectorySeparatorChar).Contains("Riot Games", StringComparer.OrdinalIgnoreCase);

    // AttributesToSkip defaults to Hidden | System: a hidden EasyAntiCheat folder or a system randgrid.sys still counts
    static readonly EnumerationOptions AllNames = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };

    /// <summary>A folder's entries for the anti-cheat walk in the order Windows lists them: on Windows streamed, never null.
    /// Elsewhere in NTFS's order once read whole, and null when the read reaches <paramref name="left"/> + 1 entries or
    /// <paramref name="over"/>: a folder not read whole, which the walk counts as anti-cheat.</summary>
    internal static partial IEnumerable<FileSystemInfo>? MarkerEntries(DirectoryInfo dir, int left, Func<bool> over);

    /// <summary>Entries one detection reads at most (names only: an install of 170,000 entries reads in about 0.1 s warm).</summary>
    public const int MaxEntries = 2_000_000;

    /// <summary>The time one detection takes at most (the slowest install measured: about 0.3 s cold).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>Euro Truck Simulator 2 and American Truck Simulator. TruckersMP's launcher injects its client (core_ets2mp.dll,
    /// core_atsmp.dll) from its own folder, so nothing of it is in the game's; with TruckersMP anywhere on the PC (<see cref="TruckersMp"/>)
    /// they count as anti-cheat games.</summary>
    internal static readonly string[] TruckersMpExes = ["eurotrucks2.exe", "amtrucks.exe"];

    /// <summary>Games whose anti-cheat leaves no file in the install. NetEase's: Once Human, Marvel Rivals, FragPunk, Sword of
    /// Justice, NARAKA: BLADEPOINT. Elytra's, whose service installs under Program Files: THE FINALS. Playground's own (online
    /// checks, hardware bans): Forza Horizon 5 and 6 on Steam and the Xbox app. Ironmace's IronShield, no marker for it yet: Dark and Darker.
    /// Activision's Ricochet: Call of Duty: Modern Warfare (2019) on Steam, whose install has no randgrid.sys, and Call of Duty
    /// HQ and Warzone, should their driver leave the game folder.</summary>
    internal static readonly string[] AntiCheatIds = ["steam:2139460", "steam:2767030", "steam:2943650", "steam:2782500", "steam:1203220",
        "steam:2483190", "steam:2073850", "steam:2016590", "xbox:Microsoft.ForteBaseGame_8wekyb3d8bbwe",
        "steam:1551360", "xbox:Microsoft.624F8B84B80_8wekyb3d8bbwe", "steam:2000950", "steam:1938090", "steam:1962663"];

    /// <summary>The game's folder or exe, as given or as it is on disk, lies in the install folder, as named or as it is on disk,
    /// of a Steam app on <see cref="AntiCheatIds"/>: in a library Steam lists or the one the game's path is in. Such an app's
    /// manifest that can't be read counts for its library's whole steamapps\common, each folder in it as on disk too.</summary>
    static bool SteamAntiCheatApp(Game g, string? steamRoot)
    {
        var names = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var paths = new[] { g.InstallDir, g.ExePath }.SelectMany(p => new[] { Path.GetFullPath(p), FinalPath(p) }).OfType<string>().ToList();
        var libraries = paths.Select(p => p.IndexOf(SteamSource.CommonPart, names) is var at and >= 0 ? p[..at] : null).OfType<string>().ToList();
        try { libraries.AddRange(SteamSource.Libraries(steamRoot ?? SteamSource.Root())); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { }
        bool Holds(string? root) => root != null && paths.Any(p => p.Equals(root, names) || p.StartsWith(root + Path.DirectorySeparatorChar, names));
        static string[] Folders(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
        foreach (var lib in libraries.Select(Path.TrimEndingDirectorySeparator).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            foreach (var id in AntiCheatIds.Where(i => i.StartsWith("steam:", StringComparison.Ordinal)))
            {
                var common = Path.Combine(lib, "steamapps", "common");
                string? dir;
                try { dir = SteamSource.Values(File.ReadAllText(Path.Combine(lib, "steamapps", $"appmanifest_{id["steam:".Length..]}.acf")), "installdir").FirstOrDefault(); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { continue; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (Holds(common) || Holds(FinalPath(common)) || Folders(common).Any(f => Holds(FinalPath(f)))) return true;
                    continue;
                }
                var root = string.IsNullOrEmpty(dir) ? common : Path.Combine(common, dir);
                if (Holds(root) || Holds(FinalPath(root))) return true;
            }
        return false;
    }

    /// <summary>A TruckersMP folder (any leftover counts) or an installed program named like it.</summary>
    internal static bool TruckersMp(IEnumerable<string> folders, IEnumerable<string> programs) =>
        folders.Any(Directory.Exists) || programs.Any(p => p.Contains("TruckersMP", StringComparison.OrdinalIgnoreCase));

    /// <summary>TruckersMP is where the game would meet it (<see cref="TruckersMp"/>): on Windows the user's and the machine's
    /// folders and uninstall list. Elsewhere always: under Proton it can run from any prefix.</summary>
    internal static partial bool TruckersMpHere();

    /// <summary>A read stream on <paramref name="path"/> when it is a regular file; null for a FIFO, socket or device, whose open
    /// would wait for a writer or which has no end. Throws what opening the file throws.</summary>
    internal static partial FileStream? OpenRegular(string path);

    /// <summary>A read-write handle on <paramref name="path"/> that no other handle shares; throws for a symlink or junction
    /// (opened as itself, never followed) and for a file with more than one name.</summary>
    internal static partial Microsoft.Win32.SafeHandles.SafeFileHandle OpenUnlinked(string path);

    internal static AntiCheat Marker(string name)
    {
        foreach (var (marker, kind) in Markers)
            if (marker[0] == '*' ? name.EndsWith(marker[1..], StringComparison.OrdinalIgnoreCase) : name.Equals(marker, StringComparison.OrdinalIgnoreCase)) return kind;
        return AntiCheat.None;
    }
}
