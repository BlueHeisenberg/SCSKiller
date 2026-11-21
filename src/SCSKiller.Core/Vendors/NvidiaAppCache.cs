using System.Buffers.Binary;

namespace SCSKiller.Core.Vendors;

/// <summary>NVIDIA's DXCache is split per application: files are named <c>TTTTa91dKKKKKKKK.nvph</c>, where KKKKKKKK is the
/// application key, a 32-bit hash of the exe file name (case-insensitive, path-independent: <see cref="HintKeys"/>).
/// TTTT is the file's type, and the types change with the driver (610.88: fc52 shader cache,
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

    /// <summary>HINT ONLY: the keys the driver gives this exe file name, whatever its case: every process's, then the one a
    /// 32-bit process uses beside it. Measured on 621.14 and 621.24. A packaged app is keyed by its package family name, so
    /// attribution uses learned keys.</summary>
    public static string[] HintKeys(string exeFileName)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(exeFileName).ToLowerInvariant());
        return [$"{(uint)Murmur128(name):x8}", $"{Murmur32(name):x8}"];
    }

    /// <summary>Exe names many programs carry (engine defaults, runtimes, launchers, browsers): their files can be any of those
    /// programs', so a name key of one is never deleted.</summary>
    public static bool GenericName(string exeFileName) => Generic.Contains(Path.GetFileName(exeFileName));

    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "game.exe", "game64.exe", "game_x64.exe", "game-win64-shipping.exe", "client-win64-shipping.exe", "ue4game-win64-shipping.exe",
        "unrealgame-win64-shipping.exe", "ue4game.exe", "unrealgame.exe", "launcher.exe", "start.exe", "main.exe", "app.exe", "client.exe",
        "play.exe", "run.exe", "setup.exe", "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "electron.exe", "nw.exe", "love.exe",
        "hl.exe", "mono.exe", "dotnet.exe", "godot.exe", "dosbox.exe", "scummvm.exe", "retroarch.exe", "chrome.exe", "msedge.exe", "msedgewebview2.exe",
        "firefox.exe", "explorer.exe", "dwm.exe", "steamwebhelper.exe", "cefsharp.browsersubprocess.exe", "qtwebengineprocess.exe",
    };

    /// <summary>The first half of MurmurHash3_x64_128 as SMHasher's 2010 beta had it (c1 and c2 change every block).</summary>
    static ulong Murmur128(ReadOnlySpan<byte> data)
    {
        ulong h1 = 0x9368e53c2f6af274, h2 = 0x586dcd208f7cd3fd, c1 = 0x87c37b91114253d5, c2 = 0x4cf5ad432745937f;
        Span<byte> block = stackalloc byte[16];
        for (var at = 0; at < data.Length; at += 16)
        {
            block.Clear();
            data[at..Math.Min(at + 16, data.Length)].CopyTo(block);
            ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(block), k2 = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
            k1 *= c1; k1 = ulong.RotateLeft(k1, 23); k1 *= c2; h1 ^= k1; h1 += h2;
            h2 = ulong.RotateLeft(h2, 41);
            k2 *= c2; k2 = ulong.RotateLeft(k2, 23); k2 *= c1; h2 ^= k2; h2 += h1;
            h1 = h1 * 3 + 0x52dce729; h2 = h2 * 3 + 0x38495ab5;
            c1 = c1 * 5 + 0x7b7d159c; c2 = c2 * 5 + 0x6bce6396;
        }
        h2 ^= (ulong)data.Length;
        h1 += h2;
        h2 += h1;
        return Fmix(h1) + Fmix(h2);

        static ulong Fmix(ulong k)
        {
            k ^= k >> 33; k *= 0xff51afd7ed558ccd; k ^= k >> 33; k *= 0xc4ceb9fe1a85ec53; k ^= k >> 33;
            return k;
        }
    }

    /// <summary>MurmurHash3_x86_32 of the same beta.</summary>
    static uint Murmur32(ReadOnlySpan<byte> data)
    {
        uint h = 0x8de1c3ac, c1 = 0x95543787, c2 = 0x2ad7eb25;
        Span<byte> block = stackalloc byte[4];
        for (var at = 0; at < data.Length; at += 4)
        {
            block.Clear();
            data[at..Math.Min(at + 4, data.Length)].CopyTo(block);
            var k = BinaryPrimitives.ReadUInt32LittleEndian(block);
            k *= c1; k = uint.RotateLeft(k, 11); k *= c2; h ^= k;
            h = h * 3 + 0x52dce729;
            c1 = c1 * 5 + 0x7b7d159c; c2 = c2 * 5 + 0x6bce6396;
        }
        h ^= (uint)data.Length;
        h ^= h >> 16; h *= 0x85ebca6b; h ^= h >> 13; h *= 0xc2b2ae35; h ^= h >> 16;
        return h;
    }

    public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? [] : All().Where(f => set.Contains(f.Key)).Select(f => new FileInfo(f.Path)).ToList();
    }

    public long SizeOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => f.Length);

    /// <summary>The bytes the file holds: a u64 at header offset 8 (lengths step 16x from 4 KiB to 256 MiB, then double).
    /// Null when unreadable (the driver opens its files unshared while the device lives) or another layout (7e55 files).</summary>
    public static long? UsedBytes(string path) => Read(path, out var used) ? used : null;

    /// <summary>False when the file can't be opened: an open for its data is refused while a process holds it, whatever the
    /// share mode (measured). True with no <paramref name="used"/>: read, another layout.</summary>
    static bool Read(string path, out long? used)
    {
        used = null;
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[16];
            if (RandomAccess.Read(h, head, 0) < head.Length || !head[..4].SequenceEqual("nvph"u8)) return true;
            long bytes = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(head[8..]);
            if (bytes > 0 && bytes <= RandomAccess.GetLength(h)) used = bytes;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>What the file holds (<see cref="UsedBytes"/>), its length in another layout. Null when it can't be opened.</summary>
    public static long? Holds(FileInfo f) => Read(f.FullName, out var used) ? used ?? f.Length : null;

    /// <summary>What the keys' files hold (<see cref="Holds"/>): a file's length grows in steps, up to twice what it holds.
    /// Null while one can't be opened: not known, and its length would pass as held.</summary>
    public long? UsedOf(IEnumerable<string> keys)
    {
        long used = 0;
        foreach (var f in FilesOf(keys))
            if (Holds(f) is { } held) used += held;
            else return null;
        return used;
    }

    public IReadOnlySet<string> KeysOpenBy(string exeFileName) => AppCacheFiles.KeysOpenBy(exeFileName, All());

    public int Delete(IEnumerable<string> keys) => AppCacheFiles.DeleteAll(FilesOf(keys));

    public IReadOnlySet<string> AllKeys() => All().Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    IEnumerable<(string Path, string Key)> All() =>
        (AppCacheFiles.Readable(Dir) ? Directory.EnumerateFiles(Dir).Select(f => (Path: f, Key: (Key(Path.GetFileName(f)) ?? LegacyKey(f))!)).Where(f => f.Key != null) : [])
        .Concat(AppCacheFiles.VulkanFiles(VulkanDir, VulkanTypes));
}
