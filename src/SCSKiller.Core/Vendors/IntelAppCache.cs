namespace SCSKiller.Core.Vendors;

/// <summary>Intel's D3D12 and D3D11 cache (ARCHITECTURE.md, Intel): one file per exe name in <see cref="IntelBackend.CacheDir"/>,
/// named by a 64-hex hash that isn't derivable from the name. The hash is the key. Not measured: that the driver keeps the
/// file open while the device lives, as NVIDIA's does, which is how a key is attributed to a game (a process named like
/// it holds the file open). Without that, no key is learned and Clear cache deletes none of the game's files.</summary>
public sealed class IntelAppCache(string dir) : IAppCache
{
    public string Dir { get; } = dir;

    /// <summary>The 64-hex name, alone or before an extension, lower case; null for anything else.</summary>
    public static string? Key(string fileName) =>
        fileName.Length >= 64 && (fileName.Length == 64 || fileName[64] == '.') && fileName.Take(64).All(Uri.IsHexDigit)
            ? fileName[..64].ToLowerInvariant() : null;

    /// <summary><see cref="Key"/> of a file in a folder <c>Intel\ShaderCache</c>; null elsewhere, so other caches' hex names stay one unit per folder.</summary>
    public static string? KeyIn(string path) =>
        Path.GetDirectoryName(path) is { } d && Path.GetFileName(d).Equals("ShaderCache", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(Path.GetDirectoryName(d))?.Equals("Intel", StringComparison.OrdinalIgnoreCase) == true ? Key(Path.GetFileName(path)) : null;

    public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? [] : All().Where(f => set.Contains(f.Key)).Select(f => new FileInfo(f.Path)).ToList();
    }

    public long SizeOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => f.Exists ? f.Length : 0);

    public IReadOnlySet<string> KeysOpenBy(string exeFileName) => AppCacheFiles.KeysOpenBy(exeFileName, All());

    public int Delete(IEnumerable<string> keys) => AppCacheFiles.DeleteAll(FilesOf(keys));

    public IReadOnlySet<string> AllKeys() => All().Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bytes of the fullest key's files, the one <see cref="IntelBackend.PerGameCap"/> caps.</summary>
    public long Fullest() => All().GroupBy(f => f.Key).Select(g => g.Sum(f => new FileInfo(f.Path) is { Exists: true } i ? i.Length : 0)).DefaultIfEmpty().Max();

    IEnumerable<(string Path, string Key)> All() => !AppCacheFiles.Readable(Dir) ? []
        : Directory.EnumerateFiles(Dir).Select(f => (Path: f, Key: Key(Path.GetFileName(f))!)).Where(f => f.Key != null);
}
