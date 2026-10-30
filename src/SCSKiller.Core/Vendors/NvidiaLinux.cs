namespace SCSKiller.Core.Vendors;

/// <summary>A file of a Steam game's NVIDIA folder that SCSKiller fills (<see cref="NvidiaLinux.SteamTargets"/>), its .bin
/// kept at or under <paramref name="Limit"/> bytes. <paramref name="SteamFirst"/>: Steam writes it too, and its entries stay
/// first and byte for byte; otherwise it holds the warm's entries alone, and one SCSKiller didn't write is Steam's.
/// <paramref name="WithSlot"/>: its limit while the game's own slot is known, 0 for the same. <paramref name="Slot"/>: the
/// game's own file, <paramref name="Name"/> and the n the game's process holds, which the game reads whole and appends to and
/// Steam folds into the merged file first at the game's exit. It stops at <paramref name="Limit"/> less
/// <paramref name="Headroom"/> less what the merged file keeps of Steam's and the game's, so after that fold theirs still end
/// within <paramref name="Limit"/>.</summary>
public sealed record SteamTarget(string Name, long Limit, bool SteamFirst, long WithSlot = 0, bool Slot = false, long Headroom = 0);

/// <summary>How the warm's entries are written into Steam's files: <see cref="Stored"/>, byte for byte as the warm's sets
/// hold them, without a dictionary; <see cref="LeafDictionary"/>, encoded again under the one dictionary of the folder
/// (zstd level 9, 1.65 times smaller than stored, measured).</summary>
public enum SteamEntryCoding { Stored, LeafDictionary }

/// <summary>NVIDIA's Linux driver as Proton runs the games and the warm on it (<see cref="Wine.CacheEnvironment"/>).</summary>
public static class NvidiaLinux
{
    public const string Profile = "nvidia-linux-1";

    /// <summary>__GL_SHADER_DISK_CACHE_SIZE of each game's cache folder: past it the driver deletes whole files of that game.</summary>
    public const long CacheSize = 10L << 30;

    /// <summary>What one cache file serves: its .toc indexes the .bin with 32-bit offsets (measured), so past 4 GiB the .bin
    /// grows unread. The Vulkan warm writes one file. Its plan charges each pipeline as rewritten without a dictionary
    /// (<c>GlCacheNoDict</c>), so the file the driver writes stays under this whatever the rewrite adds.</summary>
    public const long CacheFileIndexed = 4L << 30;

    /// <summary>Where a D3D12 warm's process is stopped so the next one writes a fresh file (<c>ProtonWarmHost</c>): a
    /// process never moves on to another file, and past <see cref="CacheFileIndexed"/> what it adds is never read
    /// (measured). It keeps a full plan's rewritten files and the last file's original under <see cref="CacheSize"/>, and
    /// its files within <see cref="Wine.WarmFiles"/>.</summary>
    public const long SegmentLimit = 3L << 29;

    /// <summary>Where a Vulkan warm, which replays from its first pipeline each time, is stopped for good: its plan keeps its
    /// one file under it (<see cref="Caps"/>' VulkanCacheCap).</summary>
    public const long VulkanLimit = 7L << 29;

    /// <summary>How many times larger a set is once rewritten without its dictionary (<c>GlCacheNoDict</c>), at most: 1.50 to
    /// 1.74 measured.</summary>
    public const double RewriteGrowth = 1.75;

    /// <summary>What the warm's files may hold, rewritten without a dictionary (<c>GlCacheNoDict</c>, 50 to 74% more
    /// than the driver's, measured): the rest of <see cref="CacheSize"/> is the game's own file's.</summary>
    public const long WarmBudget = CacheSize - (1L << 30);

    /// <summary>The cache folder's size for a game whose cache goes in Steam's folder (<see cref="SteamTargets"/>), which the
    /// game reads with no size set: their three files hold 10.8 GiB under the folder's dictionary, 1.65 times smaller than
    /// the warm's files, less Steam's own entries.</summary>
    public const long PlacedCacheSize = 17L << 30;

    /// <summary>What the warm's files may hold in a cache folder of <paramref name="size"/> bytes, as <see cref="WarmBudget"/> of <see cref="CacheSize"/>.</summary>
    public static long BudgetOf(long size) => size - (1L << 30);

    /// <summary>Under vkd3d-proton the driver keys whole pipelines, their formats and blend included (measured). Ray tracing and
    /// packaged games are unmeasured: the Windows defaults. A Vulkan game's, native or under Proton, is warmed by the native
    /// replayer under an app name of its own (<see cref="Vulkan.VulkanPlan.AppNameKey"/>), whose file the game reads up to
    /// <see cref="CacheFileIndexed"/>.</summary>
    public static VendorCaps Caps { get; } = new(Profile, CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false,
        PerStageCache: false, PerGameCacheCap: CacheSize, VulkanKey: Vulkan.VulkanPlan.AppNameKey, VulkanCacheCap: CacheFileIndexed, WarmCacheBudget: WarmBudget);

    /// <summary>What SCSKiller writes into a file of Steam's: 64 MiB under the .toc's 32-bit offsets, so every entry starts
    /// where the driver indexes it.</summary>
    public const long SteamCacheFile = (4L << 30) - (64L << 20);

    /// <summary>What Steam's merged file may hold once SCSKiller added to it while the game's own slot isn't known: its merge
    /// wraps offsets past 4 GiB (steam-for-linux #13622), and it puts the entries the game compiles in front.</summary>
    public const long SteamMerged = 7L << 29;

    /// <summary>What the game's own slot leaves under 4 GiB for one session: NVIDIA stops a process's cache writes a few MB past 1 GiB (measured).</summary>
    public const long SlotHeadroom = (1L << 30) + (64L << 20);

    /// <summary>The files of a Steam game's NVIDIA folder that the game only reads, in the order it reads them (Steam's
    /// __GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME), then the game's own file (APP_NAME and its n), as SCSKiller fills them
    /// with the warm's entries. With the own slot placed, Steam's fold pushes the merged file's entries past 4 GiB anyway, and
    /// the placement after it puts them back.</summary>
    public static IReadOnlyList<SteamTarget> SteamTargets { get; } =
        [new("steam_shader_cache", SteamCacheFile, SteamFirst: false), new("steamapp_merged_shader_cache", SteamMerged, SteamFirst: true, WithSlot: SteamCacheFile),
         new("steamapp_shader_cache", CacheFileIndexed, SteamFirst: false, Slot: true, Headroom: SlotHeadroom)];

    /// <summary>How the warm's entries go into <see cref="SteamTargets"/>.</summary>
    public const SteamEntryCoding SteamCoding = SteamEntryCoding.LeafDictionary;

    public const string ModuleVersionFile = "/proc/driver/nvidia/version";

    /// <summary>The kernel module's version in <see cref="ModuleVersionFile"/>'s text ("615.71.09"); null when it names none.</summary>
    public static string? ModuleVersion(string text) =>
        text.Split('\n').FirstOrDefault(l => l.StartsWith("NVRM version:", StringComparison.Ordinal))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.Split('.') is { Length: 2 or 3 } parts && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)));

    /// <summary>The same driver: Vulkan's version always has three parts ("390.157.00"), the module's may have two ("390.157").</summary>
    public static bool SameVersion(string a, string b) => Parts(a) is { } x && Parts(b) is { } y && x.SequenceEqual(y);

    static int[]? Parts(string version) =>
        version.Split('.') is { Length: >= 2 and <= 3 } p && p.All(s => int.TryParse(s, out _)) ? [.. p.Select(int.Parse), .. Enumerable.Repeat(0, 3 - p.Length)] : null;
}
