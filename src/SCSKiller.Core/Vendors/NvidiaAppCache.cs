namespace SCSKiller.Core.Vendors;

/// <summary>NVIDIA's DXCache is split per application: files are named <c>TTTTa91dKKKKKKKK.nvph</c>, where KKKKKKKK is the
/// application key, a 32-bit hash of the exe file name (case-insensitive, path-independent; not a plain
/// CRC/FNV/murmur/xxHash of it). TTTT is the file's type, and the types change with the driver (610.88: fc52 shader cache,
/// 0002 D3D12 pipelines, c54e ray tracing state objects; 617.14: 32e6, 0002, bc2f), so files match by key alone, whatever
/// their type. D3D12 and D3D11 share the key. A second process of the same name running at the same time gets key + 1. The
/// driver keeps the files open while the device lives, which is how a key is attributed to a game: a process named like
/// the game (its staged warm copy, or the game itself) has them open. GLCache, the driver's own Vulkan and OpenGL cache, is
/// keyed the same way by <see cref="AppCacheFiles.VulkanPrefix"/>.</summary>
public sealed class NvidiaAppCache(string dir, string? vulkanDir = null) : IAppCache
{
    public string Dir { get; } = dir;

    public string? VulkanDir { get; } = vulkanDir;

    /// <summary>GLCache's file types: an app key's .bin and its .toc.</summary>
    public static readonly string[] VulkanTypes = [".bin", ".toc"];

    /// <summary>Older drivers' D3D cache folder below LocalLow, of .bin and .toc pairs keyed by their name without the
    /// extension, with <see cref="LegacyPrefix"/>: no name format is read.</summary>
    public static readonly string[] LegacyTail = ["NVIDIA", "PerDriverVersion", "DXCache"];
    public const string LegacyPrefix = "nvdx:";

    /// <summary>The key of a .bin or .toc directly in a <see cref="LegacyTail"/> folder; null for any other file. Never a
    /// subfolder's: a link could replace it between the listing and the delete.</summary>
    public static string? LegacyKey(string path) =>
        VulkanTypes.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir && IsLegacy(dir)
            ? LegacyPrefix + Path.GetFileNameWithoutExtension(path).ToLowerInvariant() : null;

    public static bool IsLegacy(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)).Split(Path.DirectorySeparatorChar) is var parts
        && parts.Length > LegacyTail.Length && parts[^LegacyTail.Length..].SequenceEqual(LegacyTail, StringComparer.OrdinalIgnoreCase);

    /// <summary>"0002a91d69d04596.nvph" -> "69d04596"; null for anything else.</summary>
    public static string? Key(string fileName) =>
        fileName.Length == 21 && fileName.EndsWith(".nvph", StringComparison.OrdinalIgnoreCase)
        && ulong.TryParse(fileName.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, null, out _)
            ? fileName.Substring(8, 8).ToLowerInvariant() : null;

    public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? [] : All().Where(f => set.Contains(f.Key)).Select(f => new FileInfo(f.Path)).ToList();
    }

    public long SizeOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => f.Length);

    /// <summary>The bytes the file holds: a u64 at header offset 8 (lengths step 16x from 4 KiB to 256 MiB, then double).
    /// Null when unreadable (the driver opens its files unshared while the device lives) or another layout (7e55 files).</summary>
    public static long? UsedBytes(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[16];
            if (RandomAccess.Read(h, head, 0) < head.Length || !head[..4].SequenceEqual("nvph"u8)) return null;
            long used = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(head[8..]);
            return used > 0 && used <= RandomAccess.GetLength(h) ? used : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>What the file holds (<see cref="UsedBytes"/>), else its length.</summary>
    public static long Holds(FileInfo f) => UsedBytes(f.FullName) ?? f.Length;

    /// <summary>What the keys' files hold (<see cref="Holds"/>): a file's length grows in steps, up to twice what it holds.</summary>
    public long UsedOf(IEnumerable<string> keys) => FilesOf(keys).Sum(Holds);

    public IReadOnlySet<string> KeysOpenBy(string exeFileName) => AppCacheFiles.KeysOpenBy(exeFileName, All());

    public int Delete(IEnumerable<string> keys) => AppCacheFiles.DeleteAll(FilesOf(keys));

    public IReadOnlySet<string> AllKeys() => All().Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    IEnumerable<(string Path, string Key)> All() =>
        (AppCacheFiles.Readable(Dir) ? Directory.EnumerateFiles(Dir).Select(f => (Path: f, Key: (Key(Path.GetFileName(f)) ?? LegacyKey(f))!)).Where(f => f.Key != null) : [])
        .Concat(AppCacheFiles.VulkanFiles(VulkanDir, VulkanTypes));
}
