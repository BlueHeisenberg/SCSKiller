using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.App;

/// <summary>A Flatpak launcher's sandbox (Heroic's, Lutris's, Faugus's) and SCSKiller's recorder folder, the data folder's
/// <see cref="Folder"/>, which holds what the recorder reads (recorder.key, recorder.revoked) and nothing else of the data
/// folder's: whether the sandbox sees it (<c>flatpak info --show-permissions</c>), the user's override that lets it read it
/// (<c>flatpak override --user --filesystem=&lt;folder&gt;:ro</c>), and the entries that override added taken out again, as the
/// data folder's <see cref="LedgerName"/> lists them, never another entry or line of the override file. A Vulkan game's layer needs more (<see cref="VulkanEntries"/>): to write its recordings
/// and refusals into the recorder folder's vulkan and refused folders, and to read the data folder's vulkan-layer folder,
/// where its copy of the layer is.</summary>
static class FlatpakGrant
{
    public const string Folder = "recorder";

    // ponytail: per process, a launcher moved between its Flatpak and its native build shows after a restart
    static readonly ConcurrentDictionary<string, (string App, string Launcher)> Apps = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, bool> Sees = new(StringComparer.Ordinal);

    /// <summary>The override's entry: read-only, so nothing the sandbox runs can write there.</summary>
    static string Entry(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + ":ro";

    /// <summary>The Vulkan layer's entries beside <see cref="Entry"/>'s (the recorder folder <paramref name="folder"/> is
    /// the data folder's): its vulkan and refused folders writable, which flatpak mounts over the read-only folder above
    /// them (it exports by path, a parent first), and the data folder's vulkan-layer folder read-only.</summary>
    public static IReadOnlyList<(string Folder, bool Write)> VulkanEntries(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return [(Path.Combine(full, "vulkan"), true), (Path.Combine(full, "refused"), true), (Path.Combine(Path.GetDirectoryName(full)!, Vulkan.VulkanLayer.Folder), false)];
    }

    /// <summary>The data folder's list of the override entries its grants added, by app: only those are taken out again.</summary>
    public const string LedgerName = "flatpak-grants.json";

    /// <summary>The ledger (<see cref="LedgerName"/>): the entries the grants added, by app, and those a grant is about to add or
    /// a removal to take out, kept until it's done, so a crash in between is settled on the next load (<see cref="Granted"/>).</summary>
    internal sealed class Ledger
    {
        public Dictionary<string, List<string>> Granted { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> Adding { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> Removing { get; set; } = new(StringComparer.Ordinal);
    }

    static string Full(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>A Flatpak app id: two or more dot-separated names of ASCII letters, digits, _ and -, none starting with a digit.</summary>
    static bool AppId(string app) => app.Length is > 0 and <= 255 && app.Split('.') is { Length: >= 2 } names
        && names.All(n => n.Length > 0 && !char.IsAsciiDigit(n[0]) && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));

    /// <summary>The entries a grant for <paramref name="folder"/> may add, as flatpak saves them (a writable one without a mode,
    /// or with :rw): nothing else in a ledger counts as ours.</summary>
    static HashSet<string> Ours(string folder) =>
        [Entry(folder), .. VulkanEntries(folder).SelectMany(e => e.Write ? new[] { Full(e.Folder), Full(e.Folder) + ":rw" } : [Full(e.Folder) + ":ro"])];

    /// <summary>An override entry sets <paramref name="path"/> as <paramref name="write"/> asks: read-only by :ro, writable by
    /// :rw or no mode, as flatpak saves a writable one.</summary>
    static bool Same(string entry, string path, bool write) =>
        !entry.StartsWith('!') && PathOf(entry) == path && (entry.EndsWith(":ro", StringComparison.Ordinal) ? !write : write && !entry.EndsWith(":create", StringComparison.Ordinal));

    /// <summary>Runs <paramref name="change"/> on the ledger of the grants for <paramref name="folder"/> (<see cref="LedgerName"/>
    /// beside it) under a lock every process of this user shares, then saves it; the change saves it in between with the
    /// action it's given. Of what's read, only app ids (<see cref="AppId"/>) and entries a grant adds (<see cref="Ours"/>)
    /// count. First a crash's leftovers are settled: an entry a grant was adding that the override file has is ours, one a
    /// removal was taking out is taken out again. Before the first ledger: the override files that hold the recorder
    /// folder's read-only entry (<see cref="Entry"/>), as a grant made before the ledger was kept left them. Throws an
    /// IOException when it can't be read.</summary>
    static T Granted<T>(string folder, Action<string> log, Func<Ledger, Action, T> change)
    {
        var file = Path.Combine(Path.GetDirectoryName(Full(folder))!, LedgerName);
        T result = default!;
        AppStore.Locked(file, () =>
        {
            Ledger ledger;
            if (File.Exists(file))
                try { ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllBytes(file)) ?? new(); }
                catch (JsonException e) { throw new IOException($"{file} can't be read: {e.Message}"); }
            else ledger = new() { Granted = Legacy(folder) };
            // {"Granted": null} reads as none
            ledger.Granted ??= new(StringComparer.Ordinal);
            ledger.Adding ??= new(StringComparer.Ordinal);
            ledger.Removing ??= new(StringComparer.Ordinal);
            var ours = Ours(folder);
            foreach (var list in new[] { ledger.Granted, ledger.Adding, ledger.Removing })
                foreach (var app in list.Keys.ToList())
                    if (!AppId(app) || list[app]?.Where(ours.Contains).Distinct().ToList() is not { Count: > 0 } valid) list.Remove(app);
                    else list[app] = valid;
            var before = JsonSerializer.Serialize(ledger);
            void Save() => AppStore.WriteAtomic(file, JsonSerializer.SerializeToUtf8Bytes(ledger));
            foreach (var (app, adding) in ledger.Adding)
                ledger.Granted[app] = [.. (ledger.Granted.GetValueOrDefault(app) ?? []).Union(OverrideFilesystems(app).Where(adding.Contains))];
            ledger.Adding.Clear();
            foreach (var (app, removing) in ledger.Removing.ToList())
                if (Take(OverrideFile(app), removing, log)) ledger.Removing.Remove(app);
            result = change(ledger, Save);
            if (!File.Exists(file) || JsonSerializer.Serialize(ledger) != before) Save();
        });
        return result;
    }

    static Dictionary<string, List<string>> Legacy(string folder)
    {
        var all = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var dir = OverridesDir();
        if (Directory.Exists(dir))
            foreach (var app in Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().Where(AppId))
                if (OverrideFilesystems(app).Contains(Entry(folder))) all[app] = [Entry(folder)];
        return all;
    }

    /// <summary>The Flatpak app and launcher of a launcher's game (its XDG_CACHE_HOME is ~/.var/app/&lt;app&gt;/cache); null
    /// for any other game, or one whose launcher's files can't be read now.</summary>
    public static (string App, string Launcher)? AppOf(Game g)
    {
        if (!LauncherPrefix.Is(g)) return null;
        if (Apps.TryGetValue(g.Id, out var known)) return known;
        if (LauncherPrefix.Of(g, out _) is not { CacheHome: { } home } p) return null;
        var app = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(home)));
        return string.IsNullOrEmpty(app) ? null : Apps[g.Id] = (app, p.Launcher);
    }

    /// <summary>The app's sandbox sees <paramref name="folder"/>, by its permissions with the overrides; false when they
    /// can't be read (a Flatpak SCSKiller, no flatpak command).</summary>
    public static bool Visible(string app, string folder) => Sees.GetOrAdd(app + "\n" + folder, _ =>
        Run(["info", "--show-permissions", app]) is (0, var output) && Filesystems(output) is { } list
        && Covers(list, Path.GetFullPath(folder), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DataHome()));

    /// <summary>The app's sandbox may write in <paramref name="folder"/>: the deepest of its filesystems over it (flatpak mounts
    /// a deeper one over the folder above it) isn't read-only. False when they can't be read.</summary>
    public static bool Writable(string app, string folder) => Sees.GetOrAdd("w\n" + app + "\n" + folder, _ =>
        Run(["info", "--show-permissions", app]) is (0, var output) && Filesystems(output) is { } list
        && Mode(list, Path.GetFullPath(folder), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DataHome()) == true);

    /// <summary>Why the override can't be made here, null when it can: SCSKiller runs as a Flatpak (no flatpak command of the
    /// host's), there's none, or the folder's path has what --filesystem or the override file reads as a separator.</summary>
    public static string? CantGrant(string folder) =>
        LinuxStartup.InFlatpak ? "the Flatpak version of SCSKiller can't change another Flatpak's permissions"
        : OnPath("flatpak") == null ? "the flatpak command isn't there"
        : Path.GetFullPath(folder).IndexOfAny([':', ';', '\n', '\\']) >= 0 ? "the recorder folder's path has a ':', ';' or '\\'"
        : null;

    /// <summary>Makes the folder and lets the app read it, with <paramref name="vulkan"/> the Vulkan layer's folders too
    /// (<see cref="VulkanEntries"/>); null when done, else why not. What the override adds is kept in the ledger
    /// (<see cref="Granted"/>), as flatpak saved it. An entry the app's override file has already, the user's or ours, is
    /// left as it is and never taken out; another one for a folder of ours that isn't ours refuses the grant: taking ours
    /// out later would take theirs with it.</summary>
    public static string? Grant(string app, string folder, bool vulkan = false, Action<string>? log = null)
    {
        if (CantGrant(folder) is { } why) return why;
        if (!AppId(app)) return $"{app} isn't a Flatpak app id";
        List<(string Path, bool Write)> want = [(Full(folder), false), .. (vulkan ? VulkanEntries(folder) : []).Select(e => (Full(e.Folder), e.Write))];
        try
        {
            return Granted(folder, log ?? (_ => { }), (ledger, save) =>
            {
                var mine = ledger.Granted.GetValueOrDefault(app) ?? [];
                var have = OverrideFilesystems(app);
                if (want.FirstOrDefault(w => have.Any(h => PathOf(h) == w.Path && !mine.Contains(h) && !Same(h, w.Path, w.Write))) is { Path: { } theirs })
                    return $"{app}'s Flatpak override already sets {theirs} another way: change it with flatpak override, then try again";
                var add = want.Where(w => !have.Any(h => Same(h, w.Path, w.Write))).ToList();
                if (add.Count == 0) return null;
                // flatpak exposes only a path that exists when the sandbox starts
                foreach (var (f, _) in want) Directory.CreateDirectory(f);
                // what flatpak may save for these paths: none of the forms is in the file now
                var forms = Ours(folder).Where(o => add.Any(a => Same(o, a.Path, a.Write))).ToList();
                ledger.Adding[app] = forms;
                save();
                var (code, output) = Run(["override", "--user", .. add.Select(a => $"--filesystem={a.Path}:{(a.Write ? "rw" : "ro")}"), app]);
                Sees.Clear();
                var added = OverrideFilesystems(app).Where(s => !have.Contains(s) && forms.Contains(s)).ToList();
                if (added.Count > 0) ledger.Granted[app] = [.. mine.Union(added)];
                ledger.Adding.Remove(app);
                return code == 0 ? null : $"flatpak override failed ({output.Trim()})";
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }

    /// <summary>The user's override file of an app (flatpak override --user writes it).</summary>
    static string OverrideFile(string app) => Path.Combine(OverridesDir(), app);

    static string OverridesDir() => Path.Combine(Environment.GetEnvironmentVariable("FLATPAK_USER_DIR") is { Length: > 0 } user ? user : Path.Combine(DataHome(), "flatpak"), "overrides");

    /// <summary>The app's override file's [Context] filesystems entries; none when it has none or can't be read.</summary>
    static List<string> OverrideFilesystems(string app)
    {
        try
        {
            return File.Exists(OverrideFile(app)) && Filesystems(File.ReadAllText(OverrideFile(app))) is { } list
                ? [.. list.Split(';', StringSplitOptions.RemoveEmptyEntries)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>An entry's path, its mode and a "!" taken off.</summary>
    static string PathOf(string entry)
    {
        var e = entry.TrimStart('!');
        return Path.TrimEndingDirectorySeparator(e.LastIndexOf(':') is var colon and > 0 && e[(colon + 1)..] is "ro" or "rw" or "create" ? e[..colon] : e);
    }

    /// <summary>The entries the grants for <paramref name="folder"/> added (<see cref="Granted"/>) out of the override files of
    /// the apps but those in <paramref name="keep"/>, every other entry and line left as it was. What was taken out is logged
    /// by launcher app.</summary>
    public static void Remove(string folder, Action<string> log, IReadOnlySet<string>? keep = null)
    {
        try
        {
            Granted(folder, log, (ledger, save) =>
            {
                foreach (var app in ledger.Granted.Keys.Where(a => keep?.Contains(a) != true).ToList())
                {
                    // out of the ledger first: a crash before the file is written leaves the entries to take out on the next load
                    ledger.Removing[app] = ledger.Granted[app];
                    ledger.Granted.Remove(app);
                    save();
                    if (Take(OverrideFile(app), ledger.Removing[app], log)) ledger.Removing.Remove(app);
                }
                return 0;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"the recorder folder's Flatpak overrides weren't removed: {e.Message}"); }
        Sees.Clear();
    }

    /// <summary>Takes <paramref name="entries"/> out of the override <paramref name="file"/>; false when it couldn't, or it
    /// changed since it was read.</summary>
    static bool Take(string file, List<string> entries, Action<string> log)
    {
        try
        {
            if (!File.Exists(file)) return true;
            var text = File.ReadAllText(file);
            var kept = entries.Aggregate(text, (t, e) => Without(t, e) ?? t);
            if (kept == text) return true;
            var temp = $"{file}.{Guid.NewGuid():N}";
            // nothing but our entries were in it (flatpak override made it for the grant): the file goes with it, moved
            // aside first so an edit since the read is put back instead
            if (kept.Trim() is "" or "[Context]")
            {
                File.Move(file, temp);
                if (File.ReadAllText(temp) != text)
                {
                    File.Move(temp, file);
                    return false;
                }
                File.Delete(temp);
            }
            else
            {
                File.WriteAllText(temp, kept);
                // an edit between this check and the rename is still lost: flatpak takes no lock to wait on
                if (File.ReadAllText(file) != text)
                {
                    File.Delete(temp);
                    return false;
                }
                File.Move(temp, file, overwrite: true);
            }
            log($"{Path.GetFileName(file)}'s access to the recorder folder is removed");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"{file}: the recorder folder's override wasn't removed: {e.Message}"); return false; }
    }

    /// <summary>The override file's text with <paramref name="entry"/> out of [Context]'s filesystems= list (the key gone with
    /// its last entry); null when it isn't there. A ';' after a '\' is part of an entry, as the key file escapes it.</summary>
    internal static string? Without(string text, string entry)
    {
        var lines = text.Split('\n');
        var group = "";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith('[')) group = line;
            if (group != "[Context]" || !line.StartsWith("filesystems=", StringComparison.Ordinal)) continue;
            var items = line["filesystems=".Length..].Split(';').ToList();
            var at = Enumerable.Range(0, items.Count).FirstOrDefault(j => items[j] == entry && (j == 0 || !items[j - 1].EndsWith('\\')), -1);
            if (at < 0) return null;
            items.RemoveAt(at);
            var rest = string.Join(';', items);
            if (rest.Trim(';').Length == 0) lines = [.. lines[..i], .. lines[(i + 1)..]];
            else lines[i] = "filesystems=" + rest + (lines[i].EndsWith('\r') ? "\r" : "");
            return string.Join('\n', lines);
        }
        return null;
    }

    /// <summary>[Context]'s filesystems= of <c>flatpak info --show-permissions</c>; null when it has none.</summary>
    internal static string? Filesystems(string permissions)
    {
        var group = "";
        foreach (var raw in permissions.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) group = line;
            else if (group == "[Context]" && line.StartsWith("filesystems=", StringComparison.Ordinal)) return line["filesystems=".Length..];
        }
        return null;
    }

    /// <summary>The permissions' filesystems (in order, a "!" entry taking an earlier one back) expose <paramref name="path"/>:
    /// host, home, ~/..., an absolute path, xdg-data, xdg-config, xdg-cache or xdg-state and a folder of theirs, each as the
    /// folder itself or one above it. Any other entry exposes nothing here.</summary>
    internal static bool Covers(string filesystems, string path, string home, string dataHome)
    {
        var seen = false;
        foreach (var raw in filesystems.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var off = raw.StartsWith('!');
            var e = off ? raw[1..] : raw;
            if (e.LastIndexOf(':') is var colon and > 0 && e[(colon + 1)..] is "ro" or "rw" or "create") e = e[..colon];
            var (name, sub) = e.IndexOf('/') is var slash and > 0 ? (e[..slash], e[(slash + 1)..]) : (e, "");
            string? root = name switch
            {
                "host" => "/",
                "home" => home,
                "~" => Path.Combine(home, sub),
                "xdg-data" => Path.Combine(dataHome, sub),
                "xdg-config" => Path.Combine(Xdg("XDG_CONFIG_HOME", home, ".config"), sub),
                "xdg-cache" => Path.Combine(Xdg("XDG_CACHE_HOME", home, ".cache"), sub),
                "xdg-state" => Path.Combine(Xdg("XDG_STATE_HOME", home, Path.Combine(".local", "state")), sub),
                _ => e.StartsWith('/') ? e : null,
            };
            if (root == null) continue;
            root = Path.TrimEndingDirectorySeparator(root);
            if (path == root || root == "/" || path.StartsWith(root + "/", StringComparison.Ordinal)) seen = !off;
        }
        return seen;
    }

    /// <summary>Whether the filesystems expose <paramref name="path"/> writable (true), read-only (false) or not at all (null):
    /// the deepest entry over it decides, a later one of the same folder over an earlier, a "!" one hiding it. Entries as
    /// <see cref="Covers"/> reads them, host and home writable unless :ro.</summary>
    internal static bool? Mode(string filesystems, string path, string home, string dataHome)
    {
        (int Depth, bool? Write) best = (-1, null);
        foreach (var raw in filesystems.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var off = raw.StartsWith('!');
            var e = off ? raw[1..] : raw;
            var write = true;
            if (e.LastIndexOf(':') is var colon and > 0 && e[(colon + 1)..] is var mode && mode is "ro" or "rw" or "create") (e, write) = (e[..colon], mode != "ro");
            var (name, sub) = e.IndexOf('/') is var slash and > 0 ? (e[..slash], e[(slash + 1)..]) : (e, "");
            string? root = name switch
            {
                "host" => "/",
                "home" => home,
                "~" => Path.Combine(home, sub),
                "xdg-data" => Path.Combine(dataHome, sub),
                "xdg-config" => Path.Combine(Xdg("XDG_CONFIG_HOME", home, ".config"), sub),
                "xdg-cache" => Path.Combine(Xdg("XDG_CACHE_HOME", home, ".cache"), sub),
                "xdg-state" => Path.Combine(Xdg("XDG_STATE_HOME", home, Path.Combine(".local", "state")), sub),
                _ => e.StartsWith('/') ? e : null,
            };
            if (root == null) continue;
            root = Path.TrimEndingDirectorySeparator(root);
            if (!(path == root || root == "/" || path.StartsWith(root + "/", StringComparison.Ordinal))) continue;
            var depth = root == "/" ? 0 : root.Count(c => c == '/');
            if (depth >= best.Depth) best = (depth, off ? null : write);
        }
        return best.Write;
    }

    static string Xdg(string variable, string home, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v && Path.IsPathRooted(v) ? v : Path.Combine(home, fallback);

    static string DataHome() => Xdg("XDG_DATA_HOME", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.Combine(".local", "share"));

    static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    /// <summary>flatpak with <paramref name="args"/>: its exit code and output, (-1, why) when it can't run or takes over 15 s.</summary>
    static (int Code, string Output) Run(string[] args)
    {
        if (OnPath("flatpak") is not { } flatpak) return (-1, "no flatpak command");
        try
        {
            var psi = new ProcessStartInfo(flatpak) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (-1, "flatpak didn't start");
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return (-1, "flatpak took too long");
            }
            return (p.ExitCode, p.ExitCode == 0 ? output.Result : error.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return (-1, e.Message); }
    }
}
