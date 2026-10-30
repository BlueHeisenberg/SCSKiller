using System.Diagnostics;
using System.Text.Json;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vulkan;

// Windows: a Steam game's registration is a value of HKCU's ImplicitLayers named by the manifest's path. Any other game's
// is an entry of the data folder's loader settings file (Folder\SettingsName), whose path is a value of HKCU's
// LoaderSettings: the entry's app_keys name the game's exe, and the loader turns the layer on for that process alone.
public static partial class VulkanLayer
{
    public const string Dll = "VkLayer_scskiller.dll";

    /// <summary>HKCU's key the loader finds its settings file through. Replaceable for tests, which never touch the real one.</summary>
    internal static string SettingsKey { get; set; } = @"SOFTWARE\Khronos\Vulkan\LoaderSettings";

    // one read-modify-write of the settings file at a time
    static readonly object SettingsLock = new();

    static bool SteamManifest(string manifest) => ManifestAppId(manifest) != null;

    static string SettingsOf(string manifest) => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(manifest)!)!, SettingsName);

    /// <summary>One whose manifest isn't there (a moved data folder's, or a copy on a drive that's away) loads nothing and
    /// doesn't count.</summary>
    public static partial List<string> Others(string key, string manifest) => IsSteamKey(key)
        ? Values(RegistryKey).Where(v => Path.GetFileName(v).Equals(ManifestName(key), StringComparison.OrdinalIgnoreCase) && !Same(v, manifest) && File.Exists(v)).ToList()
        : ForeignSettings(SettingsOf(manifest));

    // the loader takes the first value whose file name is SettingsName (compared with case) and whose file is there, in
    // HKCU, else in HKLM: ours in HKCU would hide an HKLM one from every process
    static List<string> ForeignSettings(string ours) =>
        Values(SettingsKey).Concat(Values(SettingsKey, Microsoft.Win32.Registry.LocalMachine))
            .Where(v => Path.GetFileName(v) == SettingsName && !Same(v, ours) && File.Exists(v)).ToList();

    // a value's name as written: registry names compare without case, never as paths
    static bool Same(string value, string manifest) => value.Equals(manifest, StringComparison.OrdinalIgnoreCase);

    public static partial bool Registered(string manifest, Game? g) => SteamManifest(manifest) ? Values(RegistryKey).Any(v => Same(v, manifest))
        : g != null && Values(SettingsKey).Any(v => Same(v, SettingsOf(manifest)))
          && Entries(SettingsOf(manifest))?.Any(e => Same(e.Manifest, manifest) && e.Exes.SequenceEqual(Exes(g), StringComparer.Ordinal)) == true;

    /// <summary>Adds the manifest's value (DWORD 0: enabled), creating only the key levels that are missing; for a game that
    /// isn't Steam's, its entry in the settings file, then the file's value.</summary>
    public static partial void Register(string manifest, Game? g)
    {
        if (SteamManifest(manifest))
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryKey);
            key.SetValue(manifest, 0, Microsoft.Win32.RegistryValueKind.DWord);
            return;
        }
        ArgumentNullException.ThrowIfNull(g);
        lock (SettingsLock)
        {
            var file = SettingsOf(manifest);
            if (ForeignSettings(file) is [var other, ..]) throw new IOException($"{other} is another program's Vulkan loader settings: not replaced");
            var entries = Entries(file) ?? throw new IOException($"{file} isn't SCSKiller's: not replaced");
            entries.RemoveAll(e => Same(e.Manifest, manifest));
            entries.Add(new(g.Id, manifest, Exes(g)));
            Write(file, entries);
        }
    }

    /// <summary>Deletes the manifest's value alone: the key and every other value stay as they are. For a game that isn't
    /// Steam's, its entry alone, and the file with its value once no entry is left.</summary>
    public static partial void Unregister(string manifest)
    {
        if (!SteamManifest(manifest))
        {
            lock (SettingsLock)
            {
                var file = SettingsOf(manifest);
                if (Entries(file) is { } entries && (entries.RemoveAll(e => Same(e.Manifest, manifest)) > 0 || entries.Count == 0)) Write(file, entries);
            }
            return;
        }
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
        if (key?.GetValueNames().Any(v => Same(v, manifest)) == true) key.DeleteValue(manifest, false);
    }

    public static partial List<string> UnregisterUnder(string folder)
    {
        var mine = Under(folder);
        foreach (var v in mine.Where(SteamManifest)) Unregister(v);
        lock (SettingsLock)
        {
            var file = Path.Combine(folder, SettingsName);
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey, writable: true))
                foreach (var v in key?.GetValueNames().Where(v => Same(v, file)) ?? []) key!.DeleteValue(v, false);
            if (Entries(file) != null) File.Delete(file);
        }
        return mine;
    }

    public static partial List<string> Under(string folder) =>
        [.. Values(RegistryKey).Where(v => IsUnder(v, folder)), .. Entries(Path.Combine(folder, SettingsName))?.Select(e => e.Manifest) ?? []];

    static bool IsUnder(string path, string folder)
    {
        var under = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        try { return Path.GetFullPath(path).StartsWith(under, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    static string[] Values(string subKey, Microsoft.Win32.RegistryKey? hive = null)
    {
        using var key = (hive ?? Microsoft.Win32.Registry.CurrentUser).OpenSubKey(subKey);
        return key?.GetValueNames() ?? [];
    }

    private static partial string? SettingsGame(string manifest) => Entries(SettingsOf(manifest))?.FirstOrDefault(e => Same(e.Manifest, manifest))?.Game;

    public static partial string? LoaderProblem(string exe)
    {
        var local = Path.Combine(Path.GetDirectoryName(exe)!, "vulkan-1.dll");   // the exe's folder comes first in the search order
        var dll = File.Exists(local) ? local : Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
        if (!File.Exists(dll)) return null;
        var v = FileVersionInfo.GetVersionInfo(dll);
        return v.FileMajorPart > 0 && (v.FileMajorPart, v.FileMinorPart, v.FileBuildPart).CompareTo((1, 3, 255)) < 0
            ? $"its Vulkan loader ({dll}, {v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}) is older than 1.3.255, which the Vulkan recorder needs outside Steam" : null;
    }

    /// <summary>The app keys of a game's exe: its path as listed and as the file system spells it. The loader compares the
    /// process's path with each, case and all.</summary>
    static List<string> Exes(Game g) =>
        [.. new[] { Path.GetFullPath(g.ExePath), GameFiles.FinalPath(g.ExePath), PackagedExe(g) }.OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>An Xbox app game's exe as its process names it: below the package's WindowsApps folder, a junction to the
    /// install's Content that GetFinalPathNameByHandle resolves the other way. Null for any other game or an unregistered package.</summary>
    static unsafe string? PackagedExe(Game g)
    {
        if (XboxSource.AppUserModelId(g) is not { } aumid) return null;
        var bang = aumid.IndexOf('!');
        if (bang <= 0) return null;
        var rel = Path.GetRelativePath(g.InstallDir, g.ExePath);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        uint count = 0, chars = 0;
        if (GetPackagesByPackageFamily(aumid[..bang], ref count, null, ref chars, null) != 122 /* ERROR_INSUFFICIENT_BUFFER */ || count == 0) return null;
        var names = new nint[count];
        var buf = new char[chars];
        var fulls = new List<string>();
        fixed (nint* n = names) fixed (char* b = buf)   // names point into buf
        {
            if (GetPackagesByPackageFamily(aumid[..bang], ref count, n, ref chars, b) != 0) return null;
            for (var i = 0; i < count; i++) fulls.Add(new string((char*)n[i]));
        }
        foreach (var full in fulls)
        {
            uint len = 0;
            if (GetPackagePathByFullName(full, ref len, null) != 122) continue;
            var path = new char[len];
            fixed (char* p = path)
                if (GetPackagePathByFullName(full, ref len, p) == 0) return Path.Combine(new string(p), rel);
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern unsafe int GetPackagesByPackageFamily(string family, ref uint count, nint* names, ref uint bufferLength, char* buffer);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern unsafe int GetPackagePathByFullName(string fullName, ref uint length, char* path);

    sealed record Entry(string Game, string Manifest, List<string> Exes);

    const string GameField = "scskiller_game";   // the loader reads no field it doesn't know

    /// <summary>The settings file's entries: none when it isn't there, null when it holds anything this app didn't write
    /// (it is then never changed or deleted).</summary>
    static List<Entry>? Entries(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllBytes(file));
            if (!d.RootElement.TryGetProperty("settings_array", out var all) || all.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Entry>();
            foreach (var s in all.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty(GameField, out var game) && game.GetString() is { } id
                    && s.TryGetProperty("app_keys", out var keys) && keys.ValueKind == JsonValueKind.Array && keys.GetArrayLength() > 0
                    && s.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array
                    && layers.EnumerateArray().FirstOrDefault(l => l.ValueKind == JsonValueKind.Object && l.TryGetProperty("path", out _)) is { ValueKind: JsonValueKind.Object } layer
                    && layer.GetProperty("path").GetString() is { } manifest && ManifestKey(manifest) != null)
                    list.Add(new(id, manifest, [.. keys.EnumerateArray().Select(k => k.GetString()).OfType<string>()]));
                else return null;
            return list;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>The settings file with these entries, through a temp file the loader doesn't read, then its value; with none,
    /// the value out first, then the file. Each entry turns its layer on for its exe alone, after every layer the loader
    /// finds by itself (unordered_layer_location: without it the game would lose them): the recorder sees what reaches the
    /// driver.</summary>
    static void Write(string file, List<Entry> entries)
    {
        if (entries.Count == 0)
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey, writable: true))
                if (key?.GetValueNames().FirstOrDefault(v => Same(v, file)) is { } name) key.DeleteValue(name, false);
            File.Delete(file);
            return;
        }
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("file_format_version", "1.0.0");
            w.WriteStartArray("settings_array");
            foreach (var e in entries)
            {
                w.WriteStartObject();
                w.WriteString(GameField, e.Game);
                w.WriteStartArray("app_keys");
                foreach (var exe in e.Exes) w.WriteStringValue(exe);
                w.WriteEndArray();
                w.WriteStartArray("layers");
                w.WriteStartObject();
                w.WriteString("control", "unordered_layer_location");
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteString("control", "on");
                w.WriteString("name", LayerName(ManifestKey(e.Manifest) ?? ""));
                w.WriteString("path", e.Manifest);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, ms.ToArray());
            File.Move(temp, file, overwrite: true);
        }
        finally { File.Delete(temp); }
        using var reg = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (!reg.GetValueNames().Any(v => Same(v, file))) reg.SetValue(file, 0, Microsoft.Win32.RegistryValueKind.DWord);
    }
}
