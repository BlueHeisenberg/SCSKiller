using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

public sealed class FaugusSource(IReadOnlyList<(string Data, string Config)>? folders = null, string? steamRoot = null) : IGameSource
{
    public Store Store => Store.Other;

    public const string IdPrefix = "faugus:";

    public IProgress<string>? Log { get; set; }

    public static bool Is(string gameId) => gameId.StartsWith(IdPrefix, StringComparison.Ordinal) && gameId.Length > IdPrefix.Length;

    public static IReadOnlyList<(string Data, string Config)> Folders(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string Xdg(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } x && Path.IsPathRooted(x) ? x : fallback;
        return [(Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), "faugus-launcher"), Path.Combine(Xdg("XDG_CONFIG_HOME", Path.Combine(home, ".config")), "faugus-launcher"))];
    }

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var root = steamRoot ?? SteamSource.Root();
        foreach (var f in folders ?? Folders())
            foreach (var entry in Entries(f.Data))
                if (GameOf(entry, root, out var why) is { } g) games.Add(g);
                else Log?.Report($"Faugus: {entry.Title} isn't listed: {why}");
        return games.DistinctBy(g => g.Id).ToList();
    }

    internal sealed record Entry(string Id, string Title, string Path, string? Prefix, string? Runner);

    static List<Entry> Entries(string data)
    {
        var list = new List<Entry>();
        if (HeroicSource.Json(Path.Combine(data, "games.json")) is not { ValueKind: JsonValueKind.Array } all) return list;
        foreach (var g in all.EnumerateArray())
            if (g.ValueKind == JsonValueKind.Object && HeroicSource.Str(g, "gameid") is { } id && SafeId(id) && HeroicSource.Str(g, "path") is { Length: > 0 } path)
                list.Add(new(id, HeroicSource.Str(g, "title") is { Length: > 0 } t ? t : id, path, HeroicSource.Str(g, "prefix"), HeroicSource.Str(g, "runner")));
        return list;
    }

    static bool SafeId(string id) => id.Length is > 0 and <= 128 && id != "." && id != ".." && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    static Game? GameOf(Entry e, string steamRoot, out string? why)
    {
        why = null;
        try
        {
            if (!e.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                why = $"{System.IO.Path.GetFileName(e.Path)} isn't a Windows program";
                return null;
            }
            if (!System.IO.Path.IsPathFullyQualified(e.Path) || !File.Exists(e.Path))
            {
                why = "its exe isn't there";
                return null;
            }
            if (SteamShortcutSource.StartProblem(steamRoot, e.Path) is { } bad)
            {
                why = bad;
                return null;
            }
            var r = ManualSource.Resolve(e.Path);
            why = SteamShortcutSource.StartProblem(steamRoot, r.InstallDir) ?? SteamShortcutSource.StartProblem(steamRoot, r.Exe) ?? ManualSource.RootProblem(r.InstallDir, r.Exe, []);
            return why == null ? new Game(IdPrefix + e.Id, e.Title, Store.Other, r.InstallDir, r.Exe) : null;
        }
        catch (ArgumentException x) { why = x.Message; }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or NotSupportedException) { why = x.Message; }
        catch (Exception x) when (x is not OperationCanceledException) { why = $"{x.GetType().Name}: {x.Message}"; }
        return null;
    }

    public static LauncherPrefix? PrefixOf(Game g, out string? why, IReadOnlyList<(string Data, string Config)>? folders = null, string? steamRoot = null)
    {
        why = null;
        if (!Is(g.Id)) return null;
        var id = g.Id[IdPrefix.Length..];
        try
        {
            foreach (var f in folders ?? Folders())
            {
                if (Entries(f.Data).FirstOrDefault(x => x.Id == id) is not { } entry) continue;
                if (entry.Prefix is not { Length: > 0 } prefix || !System.IO.Path.IsPathFullyQualified(prefix))
                {
                    why = "Faugus names no Wine prefix for it";
                    return null;
                }
                var runner = entry.Runner is null or "" or "Default" ? DefaultRunner(f.Config) : entry.Runner;
                if (runner is not { Length: > 0 })
                {
                    why = "Faugus names no runner for it";
                    return null;
                }
                if (runner == "Linux-Native")
                {
                    why = "it runs natively, not on Wine or Proton";
                    return null;
                }
                if (Build(runner, steamRoot ?? SteamSource.Root()) is not { } dir)
                {
                    why = $"its runner {runner} isn't installed where Faugus keeps its Proton builds";
                    return null;
                }
                return new LauncherPrefix("Faugus", System.IO.Path.TrimEndingDirectorySeparator(prefix), dir, true, null);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            why = $"Faugus's files couldn't be read: {e.Message}";
            return null;
        }
        why = "Faugus doesn't list it anymore";
        return null;
    }

    static string? DefaultRunner(string config) =>
        HeroicSource.Json(System.IO.Path.Combine(config, "config.json")) is { ValueKind: JsonValueKind.Object } c ? HeroicSource.Str(c, "default-runner")?.Trim('"') : null;

    static string? Build(string runner, string steamRoot)
    {
        if (runner.IndexOfAny(['/', '\\']) >= 0 || runner is "." or "..") return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { steamRoot, System.IO.Path.Combine(home, ".local", "share", "Steam"), "/usr/share/steam" })
        {
            var dir = System.IO.Path.Combine(root, "compatibilitytools.d", runner);
            if (File.Exists(System.IO.Path.Combine(dir, "proton"))) return dir;
        }
        return null;
    }
}
