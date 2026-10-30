using System.Globalization;

namespace SCSKiller.Core.Vendors;

/// <summary>Mesa's cache of each Steam game in <paramref name="games"/>, in the folder <see cref="MesaLinux.SteamCache"/> names. A
/// key is the game's app id and stands for its whole folder: every GPU's and driver build's files in it, which Steam's own
/// pre-cache writes too. Steam's shadercache folder may be a link, nothing from the game's folder in it down may: a game
/// whose folder or Mesa folder is a link has no files, and a link below them is left out. A delete takes every file of the
/// folder or none: Steam's pre-cache, the game and the warm append to the same Fossilize file, whose entries don't say who
/// wrote them, and Steam processes its pre-cache again. <paramref name="games"/> given true (a delete's listing) throws
/// InvalidOperationException when a library can't be listed.</summary>
public sealed class MesaLinuxAppCache(Func<bool, IEnumerable<(string SteamApps, uint AppId)>> games) : IAppCache
{
    static readonly string[] Kinds = ["mesa_shader_cache_sf", "mesa_shader_cache_db", "mesa_shader_cache"];

    /// <summary>Each game's key and Mesa folder, the game's folder and it no link; an app listed twice keeps its first library.
    /// <paramref name="strict"/>: InvalidOperationException when one of <paramref name="keys"/>'s folders can't be told missing.</summary>
    IEnumerable<(string Key, string Dir)> Folders(bool strict = false, IReadOnlySet<string>? keys = null)
    {
        var seen = new HashSet<uint>();
        foreach (var (apps, id) in games(strict))
            if (seen.Add(id) && id.ToString(CultureInfo.InvariantCulture) is var key && (keys == null || keys.Contains(key))
                && MesaLinux.Folder(MesaLinux.SteamCache(apps, id)) is { } dir && IsFolder(Path.GetDirectoryName(dir)!, strict) && IsFolder(dir, strict))
                yield return (key, dir);
    }

    public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys) => Listed(keys, strict: false);

    /// <summary>Every file of the keys: InvalidOperationException when a game's folder or one below can't be read, before any is
    /// deleted.</summary>
    public IReadOnlyList<FileInfo> FilesToDelete(IEnumerable<string> keys) => Listed(keys, strict: true);

    IReadOnlyList<FileInfo> Listed(IEnumerable<string> keys, bool strict) =>
        [.. Folders(strict, keys.ToHashSet(StringComparer.Ordinal)).SelectMany(f => Files(f.Dir, strict))];

    public long SizeOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => f.Length);

    public IReadOnlySet<string> KeysOpenBy(string exeFileName) =>
        AppCacheFiles.KeysOpenBy(exeFileName, Folders().SelectMany(f => Files(f.Dir).Select(file => (file.FullName, f.Key))));

    public int Delete(IEnumerable<string> keys) => AppCacheFiles.DeleteAll(FilesToDelete(keys));

    public IReadOnlySet<string> AllKeys() => Folders().Where(f => Files(f.Dir).Any()).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Bytes of every game's cache.</summary>
    public long Usage() => Folders().Sum(f => Files(f.Dir).Sum(file => file.Length));

    /// <summary>Bytes of the fullest game's cache.</summary>
    public long Fullest() => Folders().Select(f => Files(f.Dir).Sum(file => file.Length)).DefaultIfEmpty().Max();

    /// <summary>For a file in a game's Mesa folder or Steam's NVIDIA one (nvidiav1), Steam's shadercache folder and the folders from the game's down to the
    /// file's: a delete opens those without following a link. Null for any other path.</summary>
    internal static (string Root, string[] Folders)? NoLinkFolders(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar);
        for (var at = 1; at + 3 < parts.Length; at++)
            if (parts[at] == "shadercache" && uint.TryParse(parts[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _) && (Kinds.Contains(parts[at + 2]) || parts[at + 2] == "nvidiav1"))
                return (string.Join(Path.DirectorySeparatorChar, parts[..(at + 1)]), parts[(at + 1)..^1]);
        return null;
    }

    static IEnumerable<FileInfo> Files(string dir, bool strict = false)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", strict ? Strict : Recurse).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (strict) throw new InvalidOperationException($"files can't be listed ({e.Message})", e);
            return [];
        }
    }

    /// <summary>A folder and no link. False when it's missing, and when it can't be told unless <paramref name="strict"/>.</summary>
    static bool IsFolder(string path, bool strict)
    {
        try { return File.GetAttributes(path) is var a && a.HasFlag(FileAttributes.Directory) && !a.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (strict) throw new InvalidOperationException($"files can't be listed ({e.Message})", e);
            return false;
        }
    }

    static readonly EnumerationOptions Recurse = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    static readonly EnumerationOptions Strict = new() { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
}
