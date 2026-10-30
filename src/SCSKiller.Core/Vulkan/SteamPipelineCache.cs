using System.Globalization;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vulkan;

/// <summary>A folder of a game's Steam pipeline cache that one plan reads: the top level (<see cref="Hash"/> null) or a
/// bucket. Recorded: this PC's archives, Steam's merge first. Apps: the application infos of a bucket's archives (the first
/// of each), of the top level's recorded ones all.</summary>
public sealed record SteamBucket(string Dir, ulong? Hash, IReadOnlyList<string> Recorded, string? Download, IReadOnlyList<FozApp> Apps, DateTime RecordedAt);

/// <summary>Steam's Fossilize archives of a game, steamapps/shadercache/&lt;appid&gt;/fozpipelinesv6, which SCSKiller only
/// reads: never opened for writing, renamed or deleted. Steam's layer records each launch there (a run's archive,
/// steamapprun_pipeline_cache.&lt;16 hex&gt;.&lt;n&gt;.foz), only what Valve's download (steam_pipeline_cache.foz, other
/// players' recordings) and Steam's merge of the earlier runs (steamapp_pipeline_cache.foz) lack: what it records may be
/// created from the download's objects. An application whose engine has a rule in Steam's filter (vkd3d-proton, DXVK)
/// records into a bucket folder, steamapprun_pipeline_cache.&lt;16 hex&gt;, named by Fossilize's bucket hash
/// (<see cref="FossilizeFilter"/>), with a download of its own. The top level's download holds every application's, told
/// apart by its blob links.</summary>
public static class SteamPipelineCache
{
    public const string Folder = "fozpipelinesv6";
    public const string Download = "steam_pipeline_cache.foz", Merged = "steamapp_pipeline_cache.foz", RunPrefix = "steamapprun_pipeline_cache.";

    /// <summary>The folder of app <paramref name="appId"/> in the Steam library <paramref name="library"/>.</summary>
    public static string Dir(string library, uint appId) =>
        Path.Combine(library, "steamapps", "shadercache", appId.ToString(CultureInfo.InvariantCulture), Folder);

    /// <summary>The top level and every bucket of <paramref name="dir"/>, as far as they can be read; none when it's missing.</summary>
    public static List<SteamBucket> Buckets(string dir, IProgress<string>? log = null)
    {
        var list = new List<SteamBucket>();
        if (!Directory.Exists(dir)) return list;
        list.Add(Read(dir, null, log));
        foreach (var d in Directory.EnumerateDirectories(dir, RunPrefix + "*").Order(StringComparer.Ordinal))
            if (Path.GetFileName(d)[RunPrefix.Length..] is { Length: 16 } hex && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, null, out var hash))
                list.Add(Read(d, hash, log));
        return list;
    }

    static SteamBucket Read(string dir, ulong? hash, IProgress<string>? log)
    {
        var runs = Directory.EnumerateFiles(dir, RunPrefix + "*.foz").Order(StringComparer.Ordinal);
        var recorded = new[] { Path.Combine(dir, Merged) }.Where(File.Exists).Concat(runs).ToList();
        var download = Path.Combine(dir, Download);
        var apps = new List<FozApp>();
        // a bucket's applications differ only in what its hash leaves out: one of each archive tells it
        foreach (var path in recorded.Count > 0 || hash == null ? recorded : File.Exists(download) ? [download] : [])
            try
            {
                using var f = Open(path);
                foreach (var e in hash == null ? Foz.List(f).Where(e => e.Tag == Foz.ApplicationInfo) : Foz.First(f, e => e.Tag == Foz.ApplicationInfo) is { } first ? [first] : [])
                    if (FozApp.Parse(e.Hash, Foz.Payload(f, e)) is { } app && !apps.Contains(app)) apps.Add(app);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { log?.Report($"{path} can't be read: {e.Message}"); }
        return new(dir, hash, recorded, File.Exists(download) ? download : null, apps,
            recorded.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max());
    }

    /// <summary>A Steam archive, read shared: Steam's layer and merge write them while a plan reads. A FIFO or device is refused.</summary>
    internal static FileStream Open(string path) => GameFiles.OpenRegular(path) ?? throw new InvalidDataException("not a regular file");

    /// <summary>The bucket a plan for a GPU of <paramref name="vendorId"/> reads, one of those whose application names
    /// <paramref name="engine"/> (any when null). The one its hash proves this vendor's comes first, then one this PC
    /// recorded into, the latest recorded, the newest engine. One that only another vendor's GPU gives (a laptop's other
    /// GPU) is never chosen. Null when none is left, and without Steam's filter, which alone tells this GPU's bucket: a plan
    /// then reads only SCSKiller's own recording.</summary>
    public static SteamBucket? Choose(IEnumerable<SteamBucket> buckets, uint vendorId, FossilizeFilter? filter, string? engine = null) =>
        filter == null ? null : buckets.Where(b => engine == null || b.Apps.Any(a => a.Engine == engine))
            .Select(b => (Bucket: b, Fits: Fits(b, vendorId, filter)))
            .Where(x => x.Fits != false && (x.Bucket.Recorded.Count > 0 || x.Bucket.Download != null))
            .OrderByDescending(x => x.Fits == true)
            .ThenByDescending(x => x.Bucket.Recorded.Count > 0)
            .ThenByDescending(x => x.Bucket.RecordedAt)
            .ThenByDescending(x => x.Bucket.Apps.Select(a => a.EngineVersion).DefaultIfEmpty(0u).Max())
            .ThenBy(x => x.Bucket.Hash)
            .Select(x => x.Bucket).FirstOrDefault();

    /// <summary>The bucket's hash is a GPU's of this vendor (true), only another vendor's (false), or can't be told (null).
    /// The top level is this PC's, whatever ran.</summary>
    internal static bool? Fits(SteamBucket b, uint vendorId, FossilizeFilter filter)
    {
        if (b.Hash is not { } hash) return true;
        var found = b.Apps.Select(a => filter.Matches(hash, a, vendorId)).ToList();
        return found.Contains(true) ? true : found.Contains(false) ? false : null;
    }

    /// <summary>A plan's archives for <paramref name="bucket"/>, best first: <paramref name="ours"/> (SCSKiller's own recording
    /// of this bucket, when there is one), this PC's recordings, then Valve's download. At the top level only the download's
    /// pipelines that this PC's applications recorded count.</summary>
    public static List<FozSource> Sources(SteamBucket bucket, string? ours)
    {
        var list = new List<FozSource>();
        if (ours != null) list.Add(new(ours, FozOrigin.Ours));
        list.AddRange(bucket.Recorded.Select(p => new FozSource(p, FozOrigin.SteamLocal)));
        if (bucket.Download != null)
            list.Add(new(bucket.Download, FozOrigin.SteamDownload) { Apps = bucket.Hash == null ? bucket.Apps.Select(a => a.Hash).ToHashSet() : null });
        return list;
    }

    /// <summary>A path in a Steam library (Steam's own folder is one) or under a steamapps or shadercache folder, as given,
    /// with the links of its folders resolved (also of folders yet to be created), or as the link it is points: Steam's,
    /// which SCSKiller never writes, renames or deletes. A library's links are resolved too.
    /// <paramref name="libraries"/>: the Steam libraries (default: the ones Steam lists).</summary>
    public static bool Owned(string path, IEnumerable<string>? libraries = null)
    {
        var full = Path.GetFullPath(path);
        var target = LinkTarget(full);
        string[] forms = [.. new[] { full, Resolved(full), target, target == null ? null : Resolved(target) }.OfType<string>().Distinct()];
        var libs = (libraries ?? KnownLibraries()).SelectMany(l => new[] { Path.GetFullPath(l), GameFiles.FinalPath(l) }).OfType<string>()
            .Select(l => Path.TrimEndingDirectorySeparator(l) + Path.DirectorySeparatorChar).Distinct().ToList();
        return forms.Any(p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                                                                                                         || s.Equals("shadercache", StringComparison.OrdinalIgnoreCase))
                              || libs.Any(l => (p + Path.DirectorySeparatorChar).StartsWith(l, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The path with the links of its folders resolved, from the deepest one that exists; null when none can be.</summary>
    static string? Resolved(string full)
    {
        var below = new Stack<string>();
        for (var d = Path.GetDirectoryName(full); d != null; d = Path.GetDirectoryName(d))
        {
            if (GameFiles.FinalPath(d) is { } final) return Path.Combine([final, .. below, Path.GetFileName(full)]);
            below.Push(Path.GetFileName(d));
        }
        return null;
    }

    /// <summary>Where the link at the path finally points, whether that exists or not; null when it isn't a link.</summary>
    static string? LinkTarget(string full)
    {
        try { return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Steam's folder and the libraries it lists, as far as they can be read.</summary>
    static List<string> KnownLibraries()
    {
        try
        {
            var root = SteamSource.Root();
            return [root, .. SteamSource.Libraries(root)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return []; }
    }
}
