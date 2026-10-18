using System.Text.Json;

namespace SCSKiller.Core.Games;

/// <summary>Installed Epic games from the launcher's %LOCALAPPDATA%\com.epicgames.eglv2\installations.jsonl, then from the
/// classic launcher's %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item.</summary>
public sealed class EpicSource(string? manifestsDir = null, string? launcherDir = null) : IGameSource
{
    public Store Store => Store.Epic;

    public static string DefaultManifestsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static string DefaultLauncherDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "com.epicgames.eglv2");

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var installs = Installs(launcherDir ?? (manifestsDir == null ? DefaultLauncherDir : null));
        foreach (var i in installs)
            if (i.Complete && GameFiles.FindExe(i.Dir, i.Exe) is { } exe) games.Add(new Game($"epic:{i.App}", i.Title, Store.Epic, i.Dir, exe, i.Version));
        var current = installs.Select(i => i.App).ToHashSet();   // the launcher leaves the classic one's manifests behind
        var dir = manifestsDir ?? DefaultManifestsDir;
        if (Directory.Exists(dir))
            foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(item));
                    var r = doc.RootElement;
                    string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                    if (S("AppName") is not { } app || S("InstallLocation") is not { } install || current.Contains(app)) continue;
                    if (S("MainGameAppName") is { Length: > 0 } main && main != app) continue;   // add-on sharing the main game's install (Legendary names a base game's own)
                    if (GameFiles.FindExe(install, string.IsNullOrEmpty(S("LaunchExecutable")) ? null : S("LaunchExecutable")) is not { } exe) continue;
                    games.Add(new Game($"epic:{app}", S("DisplayName") ?? app, Store.Epic, install, exe, S("AppVersionString")));
                }
                catch (JsonException) { }   // half-written manifest while the launcher installs
            }
        return games.DistinctBy(g => g.Id).ToList();
    }

    internal sealed record Install(string App, string Namespace, string ItemId, string Dir, string? Exe, string Title, string? Version, bool Complete);

    /// <summary>The launcher's base-game installs, finished or not. installations.jsonl is a journal: a header line, then a full snapshot per
    /// change, the last one per installationId current. Titles and add-ons come from its catalog cache.</summary>
    internal static List<Install> Installs(string? launcherDir)
    {
        var journal = launcherDir == null ? null : Path.Combine(launcherDir, "installations.jsonl");
        if (journal == null || !File.Exists(journal)) return [];
        var last = new Dictionary<string, JsonElement>();
        using (var reader = new StreamReader(new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement is { ValueKind: JsonValueKind.Object } e && Str(e, "installationId") is { } id) last[id] = e.Clone();
                }
                catch (JsonException) { }   // the line the launcher is appending
            }
        var catalog = Catalog(launcherDir!);
        var installs = new List<Install>();
        foreach (var e in last.Values)
        {
            if (Str(e, "artifactId") is not { Length: > 0 } app || Str(e, "dir") is not { Length: > 0 } dir) continue;
            if (Str(e, "parentInstallationId") is { Length: > 0 }) continue;   // an add-on in its game's install
            var itemId = Str(e, "itemId") ?? "";
            var known = catalog.TryGetValue(itemId, out var item);
            if (known && item.TryGetProperty("mainGameItem", out var main) && main.ValueKind != JsonValueKind.Null) continue;   // an add-on
            dir = Path.TrimEndingDirectorySeparator(dir);
            var title = known && Str(item, "title") is { Length: > 0 } t ? t : null;
            installs.Add(new Install(app, Str(e, "namespace") ?? "", itemId, dir, Str(e, "launchExecutable") is { Length: > 0 } exe ? exe : null,
                title ?? (Path.GetFileName(dir) is { Length: > 0 } folder ? folder : app), Str(e, "version") is { Length: > 0 } v ? v : null,
                e.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True));
        }
        return installs;
    }

    static Dictionary<string, JsonElement> Catalog(string launcherDir)
    {
        var items = new Dictionary<string, JsonElement>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(launcherDir, "cache", "prod", "catalog-cache.json")));
            if (doc.RootElement is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("items", out var all) && all.ValueKind == JsonValueKind.Object)
                foreach (var p in all.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                        items[p.Name] = item.Clone();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return items;
    }

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
