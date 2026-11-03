using System.Text;
using System.Text.Json;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vulkan;

// Linux: a registration is the manifest's copy in ImplicitDir under the manifest's own name (one per key), which the loader
// reads by itself, and for a game of a Flatpak launcher a second copy in that launcher's sandbox (SandboxDir: the sandbox's
// XDG_DATA_HOME is its own, so the loader there never reads ImplicitDir). It is this copy's when it loads the layer beside
// that manifest, or is that manifest's copy (a risk session's loads the layer beside the exe). Nothing else in those folders
// is read or written but entries of that name, and a temp file beside one while it is written. A launcher game's manifest
// has the file GameFile beside it in the data folder, naming the game its key is for.
public static partial class VulkanLayer
{
    public const string Dll = "libVkLayer_scskiller.so";

    /// <summary>The folder Flatpak keeps its apps' own data in: ~/.var/app. Replaceable for tests, which never touch the real one.</summary>
    internal static string FlatpakAppRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".var", "app");

    /// <summary>The launchers whose Flatpak's games may have a registration in its sandbox.</summary>
    static readonly string[] LauncherApps = [HeroicSource.Flatpak, LutrisSource.Flatpak, FaugusSource.Flatpak];

    /// <summary>The implicit layer folder of a Flatpak app's sandbox: its XDG_DATA_HOME's vulkan/implicit_layer.d.</summary>
    internal static string SandboxDir(string app) => Path.Combine(FlatpakAppRoot, app, "data", "vulkan", "implicit_layer.d");

    /// <summary>Every folder a registration may be in: the user's, then each launcher Flatpak's sandbox's.</summary>
    static IEnumerable<string> Dirs() => [ImplicitDir, .. LauncherApps.Select(SandboxDir)];

    /// <summary>The folders a game's registration goes in: the user's, and its launcher Flatpak's sandbox's when that Flatpak
    /// is installed for this user (its folder in <see cref="FlatpakAppRoot"/>).</summary>
    static List<string> DirsOf(Game? g) =>
        g != null && FlatpakGrant.AppOf(g) is { App: var app } && LauncherApps.Contains(app) && Directory.Exists(Path.Combine(FlatpakAppRoot, app))
            ? [ImplicitDir, SandboxDir(app)] : [ImplicitDir];

    static string Entry(string dir, string manifest) => Path.Combine(dir, Path.GetFileName(manifest));

    /// <summary>An implicit_layer.d entry's text: a regular file of at most 64 KB, read without blocking (a FIFO by that name
    /// is no manifest); null otherwise.</summary>
    static string? Text(string entry)
    {
        try
        {
            using var f = GameFiles.OpenRegular(entry);
            if (f == null || f.Length > 1 << 16) return null;
            var b = new byte[f.Length];
            f.ReadExactly(b);
            return Encoding.UTF8.GetString(b);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The library an implicit_layer.d entry loads; null when it isn't a manifest that can be read.</summary>
    static string? Library(string entry)
    {
        try
        {
            if (Text(entry) is not { } text) return null;
            using var d = JsonDocument.Parse(text);
            return d.RootElement.GetProperty("layer").GetProperty("library_path").GetString();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    static bool Ours(string entry, string manifest)
    {
        if (Library(entry) == Path.Combine(Path.GetDirectoryName(manifest)!, Dll)) return true;
        try { return Text(entry) is { } text && text == File.ReadAllText(manifest); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The entries of the key's name that aren't this manifest's: another copy's, or another program's file by that
    /// name, which is never replaced.</summary>
    public static partial List<string> Others(string key, string manifest) =>
        [.. Dirs().Select(d => Path.Combine(d, ManifestName(key))).Where(e => File.Exists(e) && !Ours(e, manifest))];

    public static partial bool Registered(string manifest, Game? g)
    {
        try
        {
            var text = File.ReadAllText(manifest);
            return DirsOf(g).All(d => Text(Entry(d, manifest)) == text) && (g == null || IsSteamKey(ManifestKey(manifest) ?? "0") || NamesGame(manifest, g.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Copies the manifest in under its name into each folder of the game's (<see cref="DirsOf"/>), through a temp
    /// file the loader doesn't read (not *.json), creating only the folder levels that are missing. A launcher game's
    /// <see cref="GameFile"/> goes beside the manifest first.</summary>
    public static partial void Register(string manifest, Game? g)
    {
        var dirs = DirsOf(g);
        var entries = dirs.Select(d => Entry(d, manifest)).ToList();
        if (entries.FirstOrDefault(e => File.Exists(e) && !Ours(e, manifest)) is { } foreign) throw new IOException($"{foreign} isn't this SCSKiller's: not replaced");
        if (g != null && ManifestKey(manifest) is { } key && !IsSteamKey(key) && !NamesGame(manifest, g.Id))
        {
            var file = Path.Combine(Path.GetDirectoryName(manifest)!, GameFile);
            var temp = $"{file}.{Guid.NewGuid():N}";
            try
            {
                File.WriteAllText(temp, g.Id);
                File.Move(temp, file, overwrite: true);
            }
            finally { File.Delete(temp); }
        }
        foreach (var dir in dirs)
        {
            var entry = Entry(dir, manifest);
            var replace = File.Exists(entry);
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $".{Path.GetFileName(manifest)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(manifest, temp);
                File.Move(temp, entry, overwrite: replace);   // none there: another copy's written meanwhile stays
            }
            finally { File.Delete(temp); }
        }
    }

    public static partial void Unregister(string manifest)
    {
        foreach (var dir in Dirs())
            if (Entry(dir, manifest) is var entry && File.Exists(entry) && Ours(entry, manifest)) File.Delete(entry);
    }

    public static partial List<string> UnregisterUnder(string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        // ManifestName's entries only, each loading the layer of its own key under the folder
        var mine = Dirs().Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "VkLayer_scskiller_*.json"))
            .Where(e => ManifestKey(e) is { } key && Library(e) == Path.Combine(root, key, Dll)).ToList();
        foreach (var e in mine) File.Delete(e);
        return mine;
    }

    /// <summary>An entry is this folder's when it loads the layer beside the folder's manifest of its name (<see cref="Ours"/>).</summary>
    public static partial List<string> Under(string folder) =>
        [.. Dirs().Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "VkLayer_scskiller_*.json")
                .Select(e => ManifestKey(e) is { } key && Path.Combine(folder, key, ManifestName(key)) is var m && Ours(e, m) ? m : null))
            .OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>The game a launcher game's manifest is for: its <see cref="GameFile"/>'s id, when the manifest's key is that id's.</summary>
    private static partial string? SettingsGame(string manifest)
    {
        try
        {
            var file = Path.Combine(Path.GetDirectoryName(manifest)!, GameFile);
            using var f = GameFiles.OpenRegular(file);
            if (f == null || f.Length is 0 or > 1024) return null;
            var b = new byte[f.Length];
            f.ReadExactly(b);
            var id = Encoding.UTF8.GetString(b);
            return !id.Any(char.IsControl) && ManifestKey(manifest) is { } key && !IsSteamKey(key) && HashKey(id) == key ? id : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static bool NamesGame(string manifest, string gameId) => SettingsGame(manifest) == gameId;

    public static partial string? LoaderProblem(string exe) => null;
}
