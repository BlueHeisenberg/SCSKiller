using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.RedEngine;

/// <summary>REDengine 3 (The Witcher 3's DX12 build): the shaders of content\content0's caches (<see cref="RedShaderCache"/>).
/// Maps: every distinct technique's graphics stages are one IsPipeline map and its compute shader another (a technique is
/// one pipeline: every recorded PSO of material shaders is one); the static cache is one pool, paired by linkage. Shaders
/// carry no root signature: the engine has three, one per kind of pipeline (plan-rules.json's red3 rule, <see cref="RulesFile"/>).
/// A material cache that doesn't read whole, or whose first techniques name no usable pipeline, isn't detected: the next
/// reader (the carver) gets the game, under its own engine, so the REDengine 3 root signatures never apply to it.
/// EngineInfo: Family "REDengine 3", Version "DX12".</summary>
public sealed class RedEngineReader : IEngineReader
{
    public const string Family = "REDengine 3", Version = "DX12";

    /// <summary>The library name of a technique's ray tracing hit group map: its closest hit library, then its any hit library
    /// when it has one (<see cref="RedRayTracing"/>).</summary>
    public const string HitGroups = "hitgroups";
    const string Materials = @"content\content0\shaderdx12_0.cache", Static = @"content\content0\staticshaderDx12_0.cache";

    /// <summary>Techniques whose shaders Detect inflates, at most, to find one usable pipeline.</summary>
    const int DetectTechniques = 64;

    public sealed record Loc(string Path, RedShaderCache.Entry Entry);

    /// <summary>Where each shader is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The whole material cache's layout, then the shaders of its first techniques whose keys it has, until one
    /// is a pipeline <see cref="Graphics"/> takes of a stage set the embedded rule lists.</summary>
    public EngineInfo? Detect(Game game)
    {
        var path = Path.Combine(game.InstallDir, Materials);
        if (!File.Exists(path)) return null;
        using var f = Open(path);
        if (RedShaderCache.ReadMaterials(f) is not { } m) return null;
        var shaOf = new Dictionary<ulong, string>();
        var shaders = new Dictionary<string, ShaderInfo>();
        foreach (var t in m.Techniques.Where(t => t.All(k => k == 0 || m.Shaders.ContainsKey(k))).Take(DetectTechniques))
        {
            foreach (var k in t.Where(k => k != 0 && !shaOf.ContainsKey(k)))
                if (RedShaderCache.Container(f, m.Shaders[k]) is { } c && Dxbc.Valid(c))
                {
                    var sha = shaOf[k] = Convert.ToHexStringLower(SHA1.HashData(c));
                    try { if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info; }
                    catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { }
                }
            if (Graphics(t, shaOf, shaders) is { } g && Listed(g, shaders)) return new EngineInfo(Family, Version, null, "D3D12", false, null);
        }
        return null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var platformOf = new Dictionary<string, string>();
        var maps = new List<ShaderMap>();
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        string? Add(string path, RedShaderCache.Entry e, Stream f)
        {
            ct.ThrowIfCancellationRequested();
            if (RedShaderCache.Container(f, e) is not { } c || !Dxbc.Valid(c)) return null;
            var sha = Convert.ToHexStringLower(SHA1.HashData(c));
            if (!locs.TryAdd(sha, new Loc(path, e))) return sha;
            try
            {
                if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings), RootSignature = null };
                if (CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) platformOf[sha] = lanes;
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { } // valid container, odd program: not usable
            return sha;
        }
        string PlatformOf(string sha) => platformOf.GetValueOrDefault(sha, CarvedReader.Platform);

        var path = Path.Combine(game.InstallDir, Materials);
        RedShaderCache.Materials? m;
        using (var f = Open(path)) m = RedShaderCache.ReadMaterials(f, ct);
        if (m == null) throw new InvalidDataException($"{Materials} no longer reads as a version 5 shader cache: scan the game again");

        var staticPath = Path.Combine(game.InstallDir, Static);
        var engineShaders = 0;
        if (File.Exists(staticPath))
        {
            using var f = Open(staticPath);
            content.AppendData(Stamp(game, staticPath));
            var pool = (RedShaderCache.ReadStatic(f) ?? []).Select(e => Add(staticPath, e, f)).OfType<string>().Where(shaders.ContainsKey).Distinct().ToList();
            engineShaders = pool.Count;
            foreach (var g in pool.GroupBy(PlatformOf))
                maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{Static}|{g.Key}"), Static, g.Key, g.ToList()));
        }

        int pipelines = 0, rejected = 0, overCap = 0, libraries;
        using (var f = Open(path))
        {
            content.AppendData(Stamp(game, path));
            var shaOf = new Dictionary<ulong, string>(m.Shaders.Count);
            foreach (var (key, e) in m.Shaders.OrderBy(s => s.Value.At))
                if (Add(path, e, f) is { } sha) shaOf[key] = sha;
            HashSet<string> seen = [], wider = [];   // each capped: the embedded rule's sets fill the cap as they always did
            void Pipeline(List<string> shas, bool listed = true)
            {
                var h = CarvedReader.Sha1Hex(string.Join(',', shas.Order(StringComparer.Ordinal)));
                var set = listed ? seen : wider;
                if (set.Contains(h)) return;
                if (set.Count >= RedShaderCache.MaxPipelines) { overCap++; return; }
                set.Add(h);
                maps.Add(new ShaderMap(h, Materials, shas.Select(PlatformOf).FirstOrDefault(p => p != CarvedReader.Platform, CarvedReader.Platform), shas, IsPipeline: true));
            }
            var groups = new HashSet<string>();
            for (var i = 0; i < m.Techniques.Count; i++)
            {
                if (i % 4096 == 0) ct.ThrowIfCancellationRequested();
                var t = m.Techniques[i];
                if (HitGroup(t, shaOf, shaders) is { } hg && groups.Count < RedShaderCache.MaxPipelines && groups.Add(string.Join(',', hg)))
                    maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{HitGroups}|{string.Join(',', hg)}"), HitGroups, CarvedReader.Platform, hg));
                if (Graphics(t, shaOf, shaders) is not { } g) { rejected++; continue; }
                var listed = Listed(g, shaders);
                if (g.Count > 0) Pipeline(g, listed);
                // a technique's compute pass is a pipeline of its own.
                // ponytail: kept only with a graphics set the embedded rule lists (as always); a server rule widening the
                // sets plans the wider graphics pipelines, not their techniques' compute passes. Carry the set in the map if one must.
                if (t[5] != 0 && listed) Pipeline([shaOf[t[5]]]);
                pipelines++;
            }
            // DXIL libraries outside the hit groups: one pool
            var libs = shaOf.Values.Distinct().Where(h => shaders.TryGetValue(h, out var s) && s.Stage == Stage.Library).ToList();
            libraries = libs.Count;
            if (libs.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{Materials}|libraries"), Materials, CarvedReader.Platform, libs));
        }
        if (pipelines == 0) throw new InvalidDataException($"{Materials}: none of its {m.Techniques.Count} techniques names a pipeline of its shaders: scan the game again");
        located[game.Id] = locs;
        log?.Report($"{shaders.Count} shaders ({engineShaders} engine, {libraries} ray tracing libraries; "
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {m.Techniques.Count} techniques -> {maps.Count(x => x.IsPipeline)} distinct pipelines"
            + (rejected > 0 ? $"; {rejected} techniques left out (a shader missing or of another stage, or stages that form no pipeline)" : "")
            + (overCap > 0 ? $"; warning: {overCap} pipelines over the cap of {RedShaderCache.MaxPipelines} left out" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. platformOf.Values.Distinct().Order(StringComparer.Ordinal)], shaders, maps);
    }

    /// <summary>A technique's ray tracing hit group: its closest hit library, then its any hit library when it names one;
    /// null when it names none, or a key isn't a library of the cache.</summary>
    internal static List<string>? HitGroup(ulong[] t, IReadOnlyDictionary<ulong, string> shaOf, IReadOnlyDictionary<string, ShaderInfo> shaders)
    {
        bool Lib(ulong k, out string sha) => shaOf.TryGetValue(k, out sha!) && shaders.TryGetValue(sha, out var s) && s.Stage == Stage.Library;
        if (t.Length < 8 || t[6] == 0 || !Lib(t[6], out var ch)) return null;
        if (t[7] == 0) return [ch];
        return Lib(t[7], out var ah) ? [ch, ah] : null;
    }

    /// <summary>A technique's graphics stages (slots VS, PS, GS, HS, DS) as SHA-1s, empty when it has none; null when a key
    /// isn't a shader of its slot's stage, or the stages can't form a pipeline. Which stage sets get a root signature is
    /// the rule's, decided at plan time, so the index doesn't depend on the rules.</summary>
    static readonly PlanRule? Red3 = RulesFile.Embedded.RuleFor(new(Family, Version, null, "D3D12", false, null));

    /// <summary>The embedded rule lists the technique's graphics stage set (none: compute only). Detection and compute passes
    /// go by it, so they don't depend on the rules in use.</summary>
    static bool Listed(List<string> g, IReadOnlyDictionary<string, ShaderInfo> shaders) =>
        g.Count == 0 || Red3 is { } r && RootSig.InStageSets(r, g.Select(h => shaders[h].Stage));

    internal static List<string>? Graphics(ulong[] t, IReadOnlyDictionary<ulong, string> shaOf, IReadOnlyDictionary<string, ShaderInfo> shaders)
    {
        Stage[] slots = [Stage.Vertex, Stage.Pixel, Stage.Geometry, Stage.Hull, Stage.Domain, Stage.Compute];
        var shas = new string[6];
        for (var i = 0; i < 6; i++)
            if (t[i] != 0)
            {
                if (!shaOf.TryGetValue(t[i], out var sha) || !shaders.TryGetValue(sha, out var s) || s.Stage != slots[i]) return null;
                shas[i] = sha;
            }
        var stages = Enumerable.Range(0, 5).Where(i => shas[i] != null).ToList();
        if (stages.Count == 0) return shas[5] != null ? [] : null;
        return shas[0] != null && (shas[3] == null) == (shas[4] == null) ? stages.Select(i => shas[i]).ToList() : null; // D3D12: a VS, and a HS only with a DS
    }

    /// <summary>Re-reads each shader where the index found it; one whose bytes changed or are gone since (game patched) is
    /// skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Path))
        {
            FileStream f;
            try { f = Open(file.Key); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; } // gone since the index: those shaders are unavailable
            using var _ = f;
            foreach (var (sha, at) in file.OrderBy(x => x.At.Entry.At))
            {
                ct.ThrowIfCancellationRequested();
                if (RedShaderCache.Container(f, at.Entry) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
            }
        }
    }

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);

    static byte[] Stamp(Game game, string path)
    {
        var fi = new FileInfo(path);
        return Encoding.UTF8.GetBytes($"{Path.GetRelativePath(game.InstallDir, path)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}\n");
    }
}
