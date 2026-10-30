namespace SCSKiller.Core.Vendors;

/// <summary>NVIDIA's Linux cache, a folder per game (<see cref="Wine.CacheEnvironment"/>):
/// <c>games/&lt;game&gt;/proton-cache/nvidia/GLCache/&lt;32 hex&gt;/&lt;16 hex&gt;/&lt;app name&gt;&lt;n&gt;.toc</c> and <c>.bin</c>, where each process
/// holding the cache gets the lowest free n of its app name. A key is one such file set, named by its path under the
/// games folder without the fixed parts: <c>&lt;game&gt;/&lt;32 hex&gt;/&lt;16 hex&gt;/&lt;app name&gt;&lt;n&gt;</c>, '/' on every system.
/// Only that layout counts, and never through a link: a linked folder or file is left out.</summary>
public sealed class NvidiaLinuxAppCache(string gamesDir) : IAppCache
{
    public string GamesDir { get; } = gamesDir;

    static readonly string[] Fixed = ["proton-cache", "nvidia", "GLCache"];

    public static string CacheDir(string gameDir) => Path.Combine(gameDir, "proton-cache", "nvidia", "GLCache");

    public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        return [.. set.Select(k => k.Split('/')[0]).Distinct().Where(IsFolderName).SelectMany(Files).Where(f => set.Contains(f.Key)).Select(f => f.File)];
    }

    public long SizeOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => f.Length);

    public IReadOnlySet<string> KeysOpenBy(string exeFileName) => AppCacheFiles.KeysOpenBy(exeFileName, All().Select(f => (f.File.FullName, f.Key)));

    public int Delete(IEnumerable<string> keys) => AppCacheFiles.DeleteAll(FilesOf(keys));

    public IReadOnlySet<string> AllKeys() => All().Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Bytes of every game's cache folders, the driver's and vkd3d-proton's: the files grow as they're written.</summary>
    public long Usage() => Games().Sum(g => Bytes(g, "nvidia") + Bytes(g, "vkd3d"));

    /// <summary>Bytes of the fullest game's driver cache folder, the one <see cref="NvidiaLinux.CacheSize"/> caps.</summary>
    public long Fullest() => Games().Select(g => Bytes(g, "nvidia")).DefaultIfEmpty().Max();

    long Bytes(string game, string cache) => Path.Combine(GamesDir, game, "proton-cache", cache) is var d && IsFolder(Path.GetDirectoryName(d)!) && IsFolder(d)
        ? new DirectoryInfo(d).EnumerateFiles("*", Recurse).Sum(f => f.Length) : 0;

    /// <summary>For a file of this layout, the games folder and the folders from there down to the file's: a delete opens
    /// those without following a link. Null for any other path.</summary>
    internal static (string Root, string[] Folders)? NoLinkFolders(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar);
        if (parts.Length < 9 || !IsSetFile(parts[^1]) || !IsHex(parts[^2], 16) || !IsHex(parts[^3], 32)
            || !parts[^6..^3].SequenceEqual(Fixed) || !IsFolderName(parts[^7])) return null;
        return (string.Join(Path.DirectorySeparatorChar, parts[..^7]), parts[^7..^1]);
    }

    /// <summary>For a file of vkd3d-proton's cache in a game's folder (<c>&lt;game&gt;/proton-cache/vkd3d/&lt;file&gt;</c>), the folder
    /// above the game's and the folders from there down to the file's, as <see cref="NoLinkFolders"/>. Null for any other path.</summary>
    internal static (string Root, string[] Folders)? Vkd3dNoLinkFolders(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar);
        if (parts.Length < 5 || parts[^3] != "proton-cache" || parts[^2] != "vkd3d" || !IsFolderName(parts[^4])) return null;
        return (string.Join(Path.DirectorySeparatorChar, parts[..^4]), parts[^4..^1]);
    }

    IEnumerable<(FileInfo File, string Key)> All() => Games().SelectMany(Files);

    // the games folder itself may be a link (the data folder on another drive), the folders in it may not
    IEnumerable<string> Games() => Directory.Exists(GamesDir) ? new DirectoryInfo(GamesDir).EnumerateDirectories("*", NoLinks).Select(d => d.Name) : [];

    IEnumerable<(FileInfo File, string Key)> Files(string game)
    {
        var dir = Path.Combine(GamesDir, game);
        if (!IsFolder(dir)) return [];
        foreach (var name in Fixed)
            if (!IsFolder(dir = Path.Combine(dir, name))) return [];
        return from a in new DirectoryInfo(dir).EnumerateDirectories("*", NoLinks) where IsHex(a.Name, 32)
               from b in a.EnumerateDirectories("*", NoLinks) where IsHex(b.Name, 16)
               from f in b.EnumerateFiles("*", NoLinks) where IsSetFile(f.Name)
               select (f, $"{game}/{a.Name}/{b.Name}/{f.Name[..^4]}");
    }

    static bool IsFolder(string path) => new DirectoryInfo(path) is { Exists: true } d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint);

    // the driver names its folders in lower-case hex, and a set's files <app name><n>.toc and .bin
    static bool IsHex(string name, int length) => name.Length == length && name.All(char.IsAsciiHexDigitLower);

    static bool IsSetFile(string name) => name.Length > 4 && (name.EndsWith(".toc", StringComparison.Ordinal) || name.EndsWith(".bin", StringComparison.Ordinal)) && char.IsAsciiDigit(name[^5]);

    // a key names its game's folder: never a path elsewhere
    static bool IsFolderName(string name) => name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(name);

    static readonly EnumerationOptions NoLinks = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    static readonly EnumerationOptions Recurse = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
}
