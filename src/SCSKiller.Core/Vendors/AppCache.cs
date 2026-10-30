namespace SCSKiller.Core.Vendors;

/// <summary>What the <see cref="IAppCache"/> implementations share: which processes have a file open, and an
/// all-or-nothing delete. Only file attributes and handles are touched, never content.</summary>
public static partial class AppCacheFiles
{
    /// <summary>The processes whose open files count. Tests: their own process tree, so another test run on the PC with
    /// the same process names never counts.</summary>
    internal static Func<List<(int Pid, int Parent, string Exe)>> Processes { get; set; } = Warming.ProcessTree.Snapshot;

    /// <summary>The folder exists and its files can be listed: false for a link (a junction to another drive) whose target is
    /// gone, which Directory.Exists still reports as there.</summary>
    public static bool Readable(string dir) => Directory.Exists(dir) && Games.GameFiles.FinalPath(dir) != null;

    /// <summary>Steam's own shader cache files under a game's redirect (its pre-caching's, and what it merged from other
    /// players): deleted only when the delete names them as Steam's (<see cref="DeleteAll"/>), whatever else lists them.</summary>
    internal static bool SteamOwn(string fileName) => fileName.StartsWith("steam_shader_cache", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("steamapp_merged_shader_cache", StringComparison.OrdinalIgnoreCase);

    static void RefuseSteams(IReadOnlyList<FileInfo> files, IReadOnlySet<string>? steams)
    {
        if (files.FirstOrDefault(f => SteamOwn(f.Name) && steams?.Contains(f.FullName) != true) is { } f) throw new InvalidOperationException($"{f.Name} is Steam's shader cache, not deleted");
    }

    /// <summary>Keys of the vendor's own Vulkan cache folder (NVIDIA's GLCache, AMD's VkCache): "vk:" and the file's path below
    /// the folder without its extension, lower case, so NVIDIA's .bin and .toc of one app key are one key. No name format is
    /// read into them: a key is a game's when a process named like it holds its file open, as with the DirectX keys.</summary>
    public const string VulkanPrefix = "vk:";

    public static bool IsVulkanKey(string key) => key.StartsWith(VulkanPrefix, StringComparison.Ordinal);

    /// <summary>Every file of a Vulkan cache folder with one of the driver's <paramref name="extensions"/>, with its key
    /// (<see cref="VulkanPrefix"/>), never through a link; none when the folder can't be listed.</summary>
    internal static IEnumerable<(string Path, string Key)> VulkanFiles(string? dir, params string[] extensions)
    {
        if (dir == null || !Readable(dir)) return [];
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        return Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(f => (Path: f, Key: VulkanKey(root, f, extensions)!)).Where(f => f.Key != null);
    }

    /// <summary>The key (<see cref="VulkanPrefix"/>) of a file below a Vulkan cache folder with one of the driver's
    /// <paramref name="extensions"/>; null for any other file.</summary>
    public static string? VulkanKey(string dir, string path, params string[] extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && Games.GameFiles.Inside(dir, path)
            ? VulkanPrefix + Path.ChangeExtension(Path.GetRelativePath(Path.GetFullPath(dir), Path.GetFullPath(path)), null).Replace('\\', '/').ToLowerInvariant() : null;

    /// <summary>A cache folder of <paramref name="cache"/> that is a link whose target is missing; null when none is.</summary>
    public static string? MissingTarget(IAppCache? cache) =>
        (cache switch { NvidiaAppCache nv => [nv.Dir], AmdAppCache amd => new[] { amd.DxcDir, amd.DxDir }, IntelAppCache intel => [intel.Dir], _ => [] })
            .FirstOrDefault(d => Directory.Exists(d) && !Readable(d));
}
