using System.Text;
using System.Text.Json;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vulkan;

// Linux: a registration is the manifest's copy in ImplicitDir under the manifest's own name (one per app id), which the
// loader reads by itself. It is this copy's when it loads the layer beside that manifest, or is that manifest's copy (a risk
// session's loads the layer beside the exe). Nothing else in the folder is read or written but entries of that name, and a
// temp file beside one while it is written.
public static partial class VulkanLayer
{
    public const string Dll = "libVkLayer_scskiller.so";

    static string Entry(string manifest) => Path.Combine(ImplicitDir, Path.GetFileName(manifest));

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

    /// <summary>The entry of the app id's name, when it isn't this manifest's: another copy's, or another program's file by
    /// that name, which is never replaced.</summary>
    public static partial List<string> Others(string key, string manifest) =>
        Path.Combine(ImplicitDir, ManifestName(key)) is var entry && File.Exists(entry) && !Ours(entry, manifest) ? [entry] : [];

    public static partial bool Registered(string manifest, Game? g)
    {
        try { return Text(Entry(manifest)) is { } text && text == File.ReadAllText(manifest); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Copies the manifest in under its name, through a temp file the loader doesn't read (not *.json), creating
    /// only the folder levels that are missing.</summary>
    public static partial void Register(string manifest, Game? g)
    {
        var entry = Entry(manifest);
        var replace = File.Exists(entry);
        if (replace && !Ours(entry, manifest)) throw new IOException($"{entry} isn't this SCSKiller's: not replaced");
        Directory.CreateDirectory(ImplicitDir);
        var temp = Path.Combine(ImplicitDir, $".{Path.GetFileName(manifest)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(manifest, temp);
            File.Move(temp, entry, overwrite: replace);   // none there: another copy's written meanwhile stays
        }
        finally { File.Delete(temp); }
    }

    public static partial void Unregister(string manifest)
    {
        var entry = Entry(manifest);
        if (File.Exists(entry) && Ours(entry, manifest)) File.Delete(entry);
    }

    public static partial List<string> UnregisterUnder(string folder)
    {
        if (!Directory.Exists(ImplicitDir)) return [];
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        // ManifestName's entries only, each loading the layer of its own app id under the folder
        var mine = Directory.EnumerateFiles(ImplicitDir, "VkLayer_scskiller_*.json")
            .Where(e => Path.GetFileNameWithoutExtension(e)["VkLayer_scskiller_".Length..] is { Length: > 0 } id && id.All(char.IsAsciiDigit)
                        && Library(e) == Path.Combine(root, id, Dll)).ToList();
        foreach (var e in mine) File.Delete(e);
        return mine;
    }

    /// <summary>An entry is this folder's when it loads the layer beside the folder's manifest of its name (<see cref="Ours"/>).</summary>
    public static partial List<string> Under(string folder) => !Directory.Exists(ImplicitDir) ? [] :
        [.. Directory.EnumerateFiles(ImplicitDir, "VkLayer_scskiller_*.json").Select(e => ManifestAppId(e) is { } id ? Path.Combine(folder, id, ManifestName(id)) : null)
            .OfType<string>().Where(m => Ours(Entry(m), m))];

    // every game's key is Steam's here (Key)
    private static partial string? SettingsGame(string manifest) => null;

    public static partial string? LoaderProblem(string exe) => null;
}
