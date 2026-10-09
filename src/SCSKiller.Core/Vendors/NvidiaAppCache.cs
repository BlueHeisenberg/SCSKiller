namespace SCSKiller.Core.Vendors;

/// <summary>NVIDIA's DXCache is split per application: files are named <c>TTTTa91dKKKKKKKK.nvph</c>, where KKKKKKKK is the
/// application key, a 32-bit hash of the exe file name (case-insensitive, path-independent; not a plain
/// CRC/FNV/murmur/xxHash of it). TTTT is the file's type, and the types change with the driver (610.88: fc52 shader cache,
/// 0002 D3D12 pipelines, c54e ray tracing state objects; 617.14: 32e6, 0002, bc2f), so files match by key alone, whatever
/// their type. D3D12 and D3D11 share the key. A second process of the same name running at the same time gets key + 1. The
/// driver keeps the files open while the device lives, which is how a key is attributed to a game: a process named like
/// the game (its staged warm copy, or the game itself) has them open.</summary>
public sealed class NvidiaAppCache(string dir) : IAppCache
{
    public string Dir { get; } = dir;

    /// <summary>"0002a91d69d04596.nvph" -> "69d04596"; null for anything else.</summary>
    public static string? Key(string fileName) =>
        fileName.Length == 21 && fileName.EndsWith(".nvph", StringComparison.OrdinalIgnoreCase)
        && ulong.TryParse(fileName.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, null, out _)
            ? fileName.Substring(8, 8).ToLowerInvariant() : null;

    public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 || !Directory.Exists(Dir) ? []
            : new DirectoryInfo(Dir).EnumerateFiles("*.nvph").Where(f => Key(f.Name) is { } k && set.Contains(k)).ToList();
    }

    public long SizeOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => f.Length);

    public IReadOnlySet<string> KeysOpenBy(string exeFileName) => !Directory.Exists(Dir) ? new HashSet<string>()
        : AppCacheFiles.KeysOpenBy(exeFileName, Directory.EnumerateFiles(Dir, "*.nvph")
            .Select(f => (Path: f, Key: Key(Path.GetFileName(f))!)).Where(f => f.Key != null));

    public int Delete(IEnumerable<string> keys) => AppCacheFiles.DeleteAll(FilesOf(keys));
}
