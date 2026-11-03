using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Games installed by WeGame, Tencent's launcher: each folder of a WeGameApps library's rail_apps
/// (&lt;Name&gt;(&lt;id&gt;)), or of the library itself (League of Legends' layout), that holds rail_files\rail_game_identify.json
/// ({"game_id": id}) and WeGameLauncher\launcher.exe, which WeGame installs with the game. Libraries: WeGameApps at each
/// fixed drive's root and beside WeGame's own folder (its Uninstall\WeGame entry), and a WeGameApps holding rail_apps one or
/// two folders below a fixed drive's root or in a Steam library's steamapps\common. A library elsewhere is added by hand.
/// WeGame's own install list is encrypted and isn't read.</summary>
public sealed class WeGameSource(IEnumerable<string>? libraries = null) : IGameSource, ILeavesOut
{
    public const string Prefix = "wegame:";
    const string Library = "WeGameApps", RailApps = "rail_apps";
    public static readonly string Identity = Path.Combine("rail_files", "rail_game_identify.json");

    public Store Store => Store.Other;

    public Func<string, bool> LeftOut { get; set; } = _ => false;

    public static bool Is(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in (libraries ?? (OperatingSystem.IsWindows() ? Libraries(LeftOut) : [])).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (LeftOut(library) || !Directory.Exists(library)) continue;
            foreach (var folder in new[] { Path.Combine(library, RailApps), library })
            {
                if (LeftOut(folder)) continue;
                string[] installs;
                try { installs = Directory.Exists(folder) ? Directory.GetDirectories(folder) : []; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var install in installs)
                    try
                    {
                        if (LeftOut(install) || !seen.Add(GameFiles.DirKey(install))) continue;
                        if (!File.Exists(Path.Combine(install, Identity)) || !File.Exists(Path.Combine(install, "WeGameLauncher", "launcher.exe"))) continue;
                        if (GameId(Path.Combine(install, Identity)) is not { } id || GameFiles.FindExe(install) is not { } exe) continue;
                        var name = GameFiles.FolderName(install);
                        if (name.EndsWith($"({id})", StringComparison.Ordinal) && name[..^$"({id})".Length].Trim() is { Length: > 0 } bare) name = bare;
                        games.Add(new Game(Prefix + id, name, Store.Other, install, exe));
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("WeGame", install, e); }
            }
        }
        return games;
    }

    /// <summary>The game_id of a rail_game_identify.json, a number or a string of digits; null when it has none or can't be read.</summary>
    internal static string? GameId(string identity)
    {
        try
        {
            if (new FileInfo(identity).Length > 64 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(identity));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("game_id", out var v)) return null;
            var text = v.ValueKind switch { JsonValueKind.Number => v.GetRawText(), JsonValueKind.String => v.GetString(), _ => null };
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The path is in a WeGame install folder in a Steam library: steamapps\common\&lt;folder&gt;\rail_apps\&lt;name&gt;(&lt;id&gt;),
    /// whose rail_files\rail_game_identify.json names that id in its plain form (<see cref="PlainId"/>) and which holds
    /// WeGameLauncher\launcher.exe. As the proxy's wegame_folder (admit.h) decides it.</summary>
    internal static bool InSteamLibrary(string path)
    {
        var full = Path.GetFullPath(path);
        var at = full.IndexOf(SteamSource.CommonPart, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        var parts = full[(at + SteamSource.CommonPart.Length)..].Split(Path.DirectorySeparatorChar);
        if (parts.Length < 3 || parts[0].Length == 0 || !parts[1].Equals(RailApps, StringComparison.OrdinalIgnoreCase)) return false;
        var name = parts[2];
        var open = name.LastIndexOf('(');
        if (open < 0 || !name.EndsWith(')') || name[(open + 1)..^1] is not { Length: > 0 and <= 19 } id || id[0] == '0' || !id.All(char.IsAsciiDigit)) return false;
        var folder = Path.Combine(full[..(at + SteamSource.CommonPart.Length)], parts[0], parts[1], name);
        var identity = Path.Combine(folder, Identity);
        return File.Exists(Path.Combine(folder, "WeGameLauncher", "launcher.exe")) && File.Exists(identity) && new FileInfo(identity).Length <= 4096
            && PlainId(File.ReadAllBytes(identity)) == id;
    }

    /// <summary>The digits of a rail_game_identify.json written plainly, {"game_id":N} or {"game_id":"N"} with JSON blanks
    /// around each token and an optional BOM; null for anything else. As the proxy's wegame_id reads it.</summary>
    internal static string? PlainId(byte[] b)
    {
        var t = System.Text.Encoding.Latin1.GetString(b);
        if (t.StartsWith("\u00EF\u00BB\u00BF", StringComparison.Ordinal)) t = t[3..];
        var i = 0;
        void Blanks() { while (i < t.Length && t[i] is ' ' or '\t' or '\r' or '\n') i++; }
        bool Take(string s)
        {
            Blanks();
            if (i + s.Length > t.Length || string.CompareOrdinal(t, i, s, 0, s.Length) != 0) return false;
            i += s.Length;
            return true;
        }
        if (!Take("{") || !Take("\"game_id\"") || !Take(":")) return null;
        Blanks();
        var quoted = i < t.Length && t[i] == '"';
        if (quoted) i++;
        var start = i;
        while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
        var id = t[start..i];
        if (id.Length is 0 or > 19 || id[0] == '0' || quoted && (i >= t.Length || t[i++] != '"') || !Take("}")) return null;
        Blanks();
        return i == t.Length ? id : null;
    }

    [SupportedOSPlatform("windows")]
    static IEnumerable<string> Libraries(Func<string, bool> leftOut)
    {
#if SCSK_TEST_HOOKS
        // SCSKILLER_TEST_WEGAME_LIBRARIES: the only libraries a test run reads, ';'-separated (a scratch tree, never the PC's own)
        if (Environment.GetEnvironmentVariable("SCSKILLER_TEST_WEGAME_LIBRARIES") is { Length: > 0 } test) return test.Split(';', StringSplitOptions.RemoveEmptyEntries);
        // SCSKILLER_TEST_WEGAME_DRIVES (';'-separated) and SCSKILLER_TEST_WEGAME_STEAM: the drive roots and Steam folder a test run scans, in place of the PC's own and its registry
        var testDrives = Environment.GetEnvironmentVariable("SCSKILLER_TEST_WEGAME_DRIVES");
        var testSteam = Environment.GetEnvironmentVariable("SCSKILLER_TEST_WEGAME_STEAM");
        if (testDrives != null || testSteam != null) return Scan(testDrives?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [], testSteam, leftOut);
#endif
        var drives = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed) drives.Add(d.RootDirectory.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        string? steam = null;
        try { steam = SteamSource.Root(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        var found = Scan(drives, steam, leftOut);
        foreach (var (hive, path) in new[] { (Registry.LocalMachine, @"SOFTWARE\WOW6432Node"), (Registry.LocalMachine, "SOFTWARE"), (Registry.CurrentUser, "Software") })
            try
            {
                using var key = hive.OpenSubKey(path + @"\Microsoft\Windows\CurrentVersion\Uninstall\WeGame");
                var dir = key?.GetValue("InstallLocation") as string is { Length: > 0 } location ? location
                    : key?.GetValue("UninstallString") is string uninstall && GryphlineSource.ExeOf(uninstall) is { } exe ? Path.GetDirectoryName(exe) : null;
                if (dir != null && Path.IsPathFullyQualified(dir) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir)) is { } parent)
                    found.Add(Path.Combine(parent, Library));
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
        return found;
    }

    /// <summary>WeGameApps at each drive's root, and each WeGameApps holding rail_apps one or two folders below a drive's root
    /// or in a Steam library's steamapps\common. Existence checks only, nothing deeper, and nothing <paramref name="leftOut"/> takes.</summary>
    static List<string> Scan(IEnumerable<string> drives, string? steam, Func<string, bool> leftOut)
    {
        var (found, walks) = (new List<string>(), Walked);
        void Probe(string library)
        {
            var rail = Path.Combine(library, RailApps);
            if (!leftOut(rail) && Directory.Exists(rail)) found.Add(library);
        }
        foreach (var drive in drives)
        {
            if (leftOut(drive)) continue;
            found.Add(Path.Combine(drive, Library));
            if (!walks.TryGetValue(drive, out var walked))
            {
                if (Walk(drive, leftOut) is not { } done)
                {
                    GameFiles.Warn?.Invoke($"WeGame: {drive} skipped, listing its folders took over {WalkBudget.TotalSeconds:0} s");
                    continue;
                }
                walks[drive] = walked = done;
            }
            foreach (var library in walked) Probe(library);
        }
        if (steam != null)
            try { foreach (var library in SteamSource.Libraries(steam)) Probe(Path.Combine(library, "steamapps", "common", Library)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return found;
    }

    // A drive's two levels are walked once per process (measured: about 2 s cold, 35 ms warm), only the WeGameApps found probed after
    // replaced, not cleared, so a walk that started before ForgetWalks caches into the old one
    static volatile ConcurrentDictionary<string, string[]> Walked = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(3);

    /// <summary>The next discovery walks each drive's first two folder levels again.</summary>
    public static void ForgetWalks() => Walked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The WeGameApps one or two folders below the drive's root, none in or under a folder <paramref name="leftOut"/>
    /// takes; null past <see cref="WalkBudget"/>, checked between calls, so a busy drive is skipped for this scan.</summary>
    static string[]? Walk(string drive, Func<string, bool> leftOut)
    {
        var clock = Stopwatch.StartNew();
        var found = new List<string>();
        foreach (var a in Folders(drive))
        {
            if (leftOut(a)) continue;
            if (clock.Elapsed > WalkBudget) return null;
            foreach (var dir in Folders(a).Prepend(a))
            {
                if (clock.Elapsed > WalkBudget) return null;
                var library = Path.Combine(dir, Library);
                if (!leftOut(library) && Directory.Exists(library)) found.Add(library);
            }
        }
        return clock.Elapsed > WalkBudget ? null : [.. found];
    }

    // Hidden, system and linked folders are skipped: $Recycle.Bin, System Volume Information and junction loops
    static readonly EnumerationOptions Shallow = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };

    static string[] Folders(string dir)
    {
        try { return Directory.GetDirectories(dir, "*", Shallow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
