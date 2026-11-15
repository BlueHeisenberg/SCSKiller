using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Swarm;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.CryEngine;

/// <summary>CRYENGINE's shipped shader cache (measured: Hunt: Showdown 1896). The ShaderCache.pak archives in the install folder
/// and its top-level folders are ZIPs (engine\shadercache.pak, stored); one that doesn't open as a ZIP is counted, never
/// decrypted. Under shaders/cache/&lt;d3d11|d3d12&gt;/CG?Shaders/ each file is one shader entry point,
/// &lt;effect&gt;@&lt;entry&gt;, holding its compiled combinations in a CPCK resource file, little endian:
///   - "CPCK", i32 version (10: LZSS), i32 count, u32 directory offset;
///   - the directory: count records {u32 name CRC, u32 size (low 24 bits) and flags (high 8), u32 offset};
///   - an entry: u32 size then LZSS (<see cref="Lzss"/>), inflating to a 12-byte header and one DXBC/DXIL container.
/// The .cfxb/.cfib files beside them are parsed effect source, no bytecode. Nothing names the pipelines a combination joins:
/// maps are one pool per effect, API and pak (its hull and domain shaders together). Hunt's shaders carry no root signature,
/// so a D3D12 plan needs a recording or a rule. An install without D3D12 isn't claimed: no CryEngine D3D11 hit is measured.
/// EngineInfo: Family "CryEngine", Version "CPCK" [+ "+layouts"] [+ "+RTS0"], GraphicsApi "D3D12".</summary>
public sealed partial class CryEngineReader : IEngineReader
{
    public const string Family = "CryEngine", Version = "CPCK";
    /// <summary>Version suffix: the install ships its D3D12 resource layouts (<see cref="CryLayouts"/>), which give the shaders
    /// their root signatures.</summary>
    public const string Layouts = "+layouts";
    const string D3D11 = "D3D11", D3D12 = CarvedReader.Platform;

    /// <summary>A CPCK file read whole: beyond, it is left out (measured largest 34 MB).</summary>
    const long MaxFile = 256L << 20;
    /// <summary>Directory records a CPCK file may declare (measured at most a few thousand).</summary>
    const int MaxRecords = 1 << 20;
    /// <summary>Bytes before the container in an inflated entry that are searched for it (measured 12).</summary>
    const int MaxHeader = 64;
    /// <summary>Shader/layout pairs gathered to resolve unshipped layouts by fit : past it none is.</summary>
    internal long FitReferences { get; init; } = 2_000_000;
    /// <summary>Shaders per API folder Detect decodes before deciding, and records it tries for them.</summary>
    const int DetectSample = 64, DetectTries = 1 << 16;
    /// <summary>Records an index walks: beyond, the rest of the install is left out (measured 52,873).</summary>
    const int MaxIndexRecords = 1 << 24;

    static readonly Regex CgFile = new(@"(?:^|/)shaders/cache/(d3d1[12])/cg[vpgdhc]shaders/([^/]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record Loc(string Pak, string Entry, uint Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. In memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();
    /// <summary>The root signatures built from the layouts, by SHA-1, per game id, from the last Index.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, byte[]>> roots = new();

    public int ReaderVersion(EngineInfo engine) => engine.Version.Contains(Layouts) ? 2 : 1;

    /// <summary>The titles a measured hit proved this reader's plan on, by game id, with the row that proved it. A title is added
    /// here only after its proof.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Measured = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["steam:594650"] = "Hunt: Showdown 1896, card 1183 hunt-statics key match (NVIDIA)",
    };

    // Hunt, the one proven title, builds 221 of 221: a handful of odd layouts may fail, a large unplanned share may not
    internal const int MinBuiltPercent = 95;

    /// <summary>The shipped layouts plan the install: at least <see cref="MinBuiltPercent"/>% of them build a root signature
    /// (KCD II builds 3 of 135). Else the install plans as one without layouts.</summary>
    internal static bool Builds(IReadOnlyDictionary<ulong, CryLayout> shipped) =>
        shipped.Count > 0 && 100L * shipped.Keys.Count(id => CryRootSig.For(id, shipped).Blob != null) >= (long)MinBuiltPercent * shipped.Count;

    /// <summary>The shader paks Detect and Index open (<see cref="Paks"/>).</summary>
    internal Func<string, List<string>> ListPaks { get; init; } = Paks;

    /// <summary>Null also when the paks throw on reading: a format this reader doesn't know leaves the game to the carver.
    /// A file the game holds (IOException) is the scan's to retry.</summary>
    public EngineInfo? Detect(Game game)
    {
        try { return DetectPaks(game); }
        catch (Exception e) when (e is not (IOException or UnauthorizedAccessException or OperationCanceledException)) { return null; }
    }

    EngineInfo? DetectPaks(Game game)
    {
        var apis = new SortedSet<string>(StringComparer.Ordinal);
        int programs = 0, rts0 = 0;
        foreach (var pak in ListPaks(game.InstallDir))
        {
            using var zip = OpenPak(game.InstallDir, pak);
            if (zip == null) continue;
            foreach (var g in CgEntries(zip).GroupBy(x => x.Api))
            {
                if (apis.Contains(g.Key)) continue;
                int n = 0, tries = 0;
                foreach (var (e, _, _) in g.OrderBy(x => x.Entry.Length))   // smallest first
                {
                    if (Read(e) is not { } f || Records(f) is not { } dir) continue;
                    foreach (var (offset, size) in dir.Distinct())
                    {
                        if (n >= DetectSample || ++tries > DetectTries) break;
                        if (Decode(f, offset, size) is not { } c || Dxbc.Kind(c) < 0) continue;
                        n++;
                        programs++;
                        if (!Dxbc.Part(c, "RTS0"u8).IsEmpty) rts0++;
                    }
                    if (n >= DetectSample || tries > DetectTries) break;
                }
                if (n > 0) apis.Add(g.Key);
            }
        }
        // no CryEngine D3D11 hit is measured yet: a D3D11 cache is never planned, an install without D3D12 (in its cache, or
        // in the renderers beside its exe) stays the carver's
        if (!apis.Contains(D3D12) || Renderers(game) is { Count: > 0 } renderers && !renderers.Contains(D3D12)) return null;
        const string api = D3D12;
        var shipped = CryLayouts.Read(game.InstallDir);
        var layouts = Builds(shipped) ? Layouts : "";
        var embedded = rts0 >= 0.9 * programs;
        // layouts or embedded root signatures plan with no recording only where a hit proved it: else the carver's answer stands
        if ((embedded || layouts != "") && !Measured.ContainsKey(game.Id)) return null;
        return new EngineInfo(Family, Version + layouts + (layouts != "" ? CryAmdSamplers.Suffixed(game, shipped) : "") + (embedded ? CarvedReader.EmbeddedRootSignatures : ""), null, api, false, null);
    }

    /// <summary>A pak that throws on reading throws InvalidDataException, as files that don't read: a game with a recording
    /// then plans from it alone.</summary>
    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        try { return IndexPaks(game, engine, log, ct); }
        catch (Exception e) when (e is not (InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException))
        {
            throw new InvalidDataException($"its shader paks don't read ({e.GetType().Name}: {e.Message})", e);
        }
    }

    ShaderIndex IndexPaks(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        // an EngineInfo an earlier build cached may claim what Detect no longer does: a D3D11-only install, or layouts or
        // embedded root signatures on a title no hit proved
        if (!engine.GraphicsApi.Contains(D3D12)) throw new InvalidDataException("a CryEngine install without D3D12 isn't read yet");
        if ((engine.Version.Contains(Layouts) || engine.Version.EndsWith(CarvedReader.EmbeddedRootSignatures)) && !Measured.ContainsKey(game.Id))
            throw new InvalidDataException("its plan from files isn't proven on this title");
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var rootSigs = new RootSigCarriers();
        var psv = new Dictionary<string, CryShader?>();
        var platformOf = new Dictionary<string, string>();
        var pools = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var versions = new SortedSet<int>();
        var stamp = new StringBuilder();
        var capped = false;
        int paks = 0, locked = 0, files = 0, entries = 0, unreadable = 0, notCpck = 0, undecoded = 0, bad = 0;
        foreach (var pak in ListPaks(game.InstallDir))
        {
            ct.ThrowIfCancellationRequested();
            if (capped) break;
            var rel = GameFiles.Rel(game.InstallDir, pak).ToLowerInvariant();
            var fs = OpenAllowed(game.InstallDir, pak);
            stamp.Append(fs == null ? $"{rel}|gone\n" : ContentStamps.Of(rel, fs.SafeFileHandle));
            using var zip = fs == null ? null : SwarmReader.Open(fs, out _);
            if (zip == null) { locked++; continue; }
            paks++;
            foreach (var (e, api, name) in CgEntries(zip))
            {
                ct.ThrowIfCancellationRequested();
                if (capped = entries >= MaxIndexRecords) break;
                if (api == D3D11) continue;   // never planned (Detect)
                files++;
                if (Read(e) is not { } f) { unreadable++; continue; }
                if (Records(f) is not { } dir) { notCpck++; continue; }
                versions.Add(BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(4)));
                var platform = api == D3D11 ? D3D11 : D3D12;
                var pool = $"{rel}:{api.ToLowerInvariant()}/{name.Split('@')[0].ToLowerInvariant()}";
                var at = new Dictionary<(uint, int), string?>();   // records of one combination share its bytes
                foreach (var (offset, size) in dir)
                {
                    entries++;
                    if (!at.TryGetValue((offset, size), out var sha))
                    {
                        at[(offset, size)] = sha = Decode(f, offset, size) is { } c ? See(c) : null;
                        if (sha == null) undecoded++;

                        string See(byte[] c)
                        {
                            var h = Convert.ToHexStringLower(SHA1.HashData(c));
                            var kind = Dxbc.Kind(c);
                            if (!locs.TryAdd(h, new Loc(pak, e.FullName, offset, size)) || kind < 0) return h;
                            rootSigs.See(c, h, kind);
                            try
                            {
                                if (ShaderContainer.Parse(c, h, new(0, 0, 0, 0)) is { } info)
                                {
                                    shaders[h] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                                    if (platform == D3D12 && CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) platformOf[h] = lanes;
                                    if (platform == D3D12) psv[h] = CryShader.Of(c);
                                }
                            }
                            catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                            return h;
                        }
                    }
                    if (sha == null || !shaders.ContainsKey(sha)) continue;
                    var key = $"{pool}|{platformOf.GetValueOrDefault(sha, platform)}";
                    if (!pools.TryGetValue(key, out var list)) pools[key] = list = [];
                    list.Add(sha);
                }
            }
        }
        rootSigs.Apply(shaders);
        var rootBlobs = new Dictionary<string, byte[]>(rootSigs.Blobs);
        var layoutMaps = engine.Version.Contains(Layouts) ? LayoutMaps(game, shaders, psv, pools, rootBlobs, FitReferences, log, ct) : null;
        if (layoutMaps != null) foreach (var k in pools.Keys.Where(k => !k.EndsWith("|" + D3D11)).ToList()) pools.Remove(k);
        var maps = pools.Select(p => (Name: p.Key[..p.Key.LastIndexOf('|')], Platform: p.Key[(p.Key.LastIndexOf('|') + 1)..], Shaders: p.Value.ToList()))
            .Select(p => new ShaderMap(CarvedReader.Sha1Hex(p.Platform is D3D11 or D3D12 ? p.Name : $"{p.Name}|{p.Platform}"), p.Name, p.Platform, p.Shaders)).ToList();
        if (layoutMaps != null) maps.AddRange(layoutMaps.Value.Maps);
        log?.Report($"{paks} shader paks{(locked > 0 ? $" ({locked} more don't open as ZIPs: encrypted or damaged, left out)" : "")}, {files} CPCK files"
            + (versions.Count > 0 ? $" (version {string.Join('/', versions)})" : "") + $", {entries} entries -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")) + "), "
            + $"{shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count()} embedded root signatures, "
            + $"{shaders.Values.Count(s => s.InlineRayTracing)} tracing rays inline, {maps.Count} pools"
            + (unreadable > 0 ? $"; {unreadable} files don't read (another compression, damaged, too large), left out" : "")
            + (notCpck > 0 ? $"; {notCpck} files aren't CPCK, left out" : "")
            + (undecoded > 0 ? $"; {undecoded} entries don't decode to a container, left out" : "")
            + (bad > 0 ? $"; {bad} unparseable" : "") + (capped ? "; warning: too many records, the rest left out" : "") + FormattableString.Invariant($" ({sw.Elapsed.TotalSeconds:F1}s)"));
        if (shaders.Count == 0) throw new InvalidDataException("no shader in the shader paks");
        located[game.Id] = locs;
        roots[game.Id] = rootBlobs;
        if (layoutMaps != null) stamp.Append($"layouts {ReaderVersion(engine)}\n");
        var platforms = maps.Select(m => m.Platform).Distinct().OrderBy(p => p == D3D12 ? 0 : p == D3D11 ? 1 : 2).ThenBy(p => p, StringComparer.Ordinal);
        return new ShaderIndex(CarvedReader.Sha1Hex(stamp.ToString()), [.. platforms], shaders, maps, rootBlobs, LeftOut: layoutMaps?.LeftOut);
    }

    /// <summary>The D3D12 shaders by the root signatures they can run under, as BlackSpace's passes: each shader takes every layout
    /// the cached pipelines of its effect use (compute ones for a compute shader, graphics ones otherwise) whose root signature
    /// covers what it binds (<see cref="RootSig.Uncovered(RootSig.Ranges, Stage, ShaderInfo)"/>); a layout the install doesn't
    /// ship takes its twin's (<see cref="CryTwins"/>), or both hedged twins'. A pooled map per effect, layout
    /// and platform of its graphics shaders, an IsPipeline map per compute shader and layout. Nothing pairs a shader with a
    /// pipeline record, so a shader fitting several layouts is planned under each; one fitting none is left out. Null when the
    /// install's layouts don't read. Adds the root signatures to <paramref name="blobs"/>. LeftOut: the shaders left out, by why.</summary>
    static (List<ShaderMap> Maps, Dictionary<string, int> LeftOut)? LayoutMaps(Game game, IReadOnlyDictionary<string, ShaderInfo> shaders, IReadOnlyDictionary<string, CryShader?> psv,
        Dictionary<string, HashSet<string>> pools, Dictionary<string, byte[]> blobs, long fitReferences, IProgress<string>? log, CancellationToken ct)
    {
        var layouts = CryLayouts.Read(game.InstallDir);
        if (!Builds(layouts)) return null;
        var rejected = new Dictionary<string, int>();
        var used = CryLayouts.PipelineLayouts(game.InstallDir, rejected);
        var byLayout = ShadersOfUnshipped(pools.Where(p => !p.Key.EndsWith("|" + D3D11)).SelectMany(p => p.Value.Select(sha => (Effect: p.Key[..p.Key.LastIndexOf('|')].Split('/')[^1],
            Shader: psv.GetValueOrDefault(sha)))).Where(x => x.Shader != null).Select(x => (x.Effect, x.Shader!)), used, layouts, fitReferences, ct);
        var twins = CryTwins.Resolve(CryTwins.Read(game.InstallDir), layouts, byLayout == null ? null : id => byLayout.GetValueOrDefault(id), ct: ct);
        var rebuilt = CryTwins.Rebuilt(twins, layouts);
        var rs = new Dictionary<ulong, (List<(string Sha, RootSig.Ranges Ranges)> Roots, string? Why)>();
        // the layouts a pipeline of the id runs under: its own, else its twins'
        CryLayout[] LayoutsOf(ulong id) => layouts.TryGetValue(id, out var l) ? [l] : [.. twins[id].Twins.Select(t => layouts[t])];
        (List<(string Sha, RootSig.Ranges Ranges)> Roots, string? Why) Root(ulong id)
        {
            if (rs.TryGetValue(id, out var r)) return r;
            var (list, why) = CryTwins.RootSignatures(id, layouts, twins, rebuilt);
            if (list.Count == 0) return rs[id] = ([], layouts.ContainsKey(id) ? "unbuilt" : twins.TryGetValue(id, out var t) ? $"not shipped, {t.State}" : "not shipped");
            var roots = new List<(string, RootSig.Ranges)>();
            foreach (var blob in list)
            {
                var sha = Convert.ToHexStringLower(SHA1.HashData(blob));
                blobs[sha] = blob;
                roots.Add((sha, RootSig.Parse(blob)));
            }
            return rs[id] = (roots, null);
        }
        var graphics = new Dictionary<(string Effect, string Rs, string Platform), SortedSet<string>>();
        var compute = new HashSet<(string Cs, string Rs, string Platform)>();
        var placed = new HashSet<string>();
        var why = new Dictionary<string, string>();   // a shader left out: the best reason over its pools
        foreach (var (key, set) in pools)
        {
            var bar = key.LastIndexOf('|');
            var platform = key[(bar + 1)..];
            if (platform == D3D11) continue;
            var effect = key[..bar].Split('/')[^1];
            var cands = used.TryGetValue(effect, out var c) ? c : [];
            foreach (var sha in set)
            {
                var info = shaders[sha];
                var isCs = info.Stage == Stage.Compute;
                var reason = "no cached pipeline of its effect";
                // an empty binding list fits any root signature: a shader whose PSV0 bindings don't read is never placed
                if (!psv.TryGetValue(sha, out var cry) || cry == null)
                {
                    why[sha] = "binding metadata missing or malformed";
                    continue;
                }
                // the planner takes an empty output signature as unread and plans it with the rasterizer on, which the runtime
                // rejects without SV_Position (E_INVALIDARG, debug layer id 682)
                if (info.Stage is Stage.Vertex or Stage.Domain or Stage.Geometry && info.Outputs.Count == 0)
                {
                    why[sha] = "a vertex stage with no outputs";
                    continue;
                }
                foreach (var (id, csLayout) in cands.Where(x => x.Compute == isCs))
                {
                    var r = Root(id);
                    if (r.Roots.Count == 0) { reason = reason.StartsWith("doesn't fit") ? reason : $"layout {r.Why}"; continue; }
                    // a hedged layout plans the shader under each twin, so it must fit both
                    if (r.Roots.Select(x => Misfit(x.Ranges, info)).FirstOrDefault(m => m != null) is { } misfit) { reason = $"doesn't fit: {misfit}"; continue; }
                    if (CryTwins.Misfit([cry], LayoutsOf(id)) != null) { reason = "doesn't fit: its PSV0 bindings"; continue; }
                    placed.Add(sha);
                    foreach (var (root, _) in r.Roots)
                        if (isCs) compute.Add((sha, root, platform));
                        else
                        {
                            if (!graphics.TryGetValue((effect, root, platform), out var pool)) graphics[(effect, root, platform)] = pool = new(StringComparer.Ordinal);
                            pool.Add(sha);
                        }
                }
                if (!placed.Contains(sha)) why[sha] = reason;
            }
        }
        foreach (var sha in placed) why.Remove(sha);
        log?.Report($"{layouts.Count} shipped resource layouts, {twins.Values.Count(t => t.State is CryTwinState.Twin or CryTwinState.Hedged)} of {twins.Count} unshipped layouts resolved by twins ({twins.Values.Count(t => t.ByFit && t.State is CryTwinState.Twin or CryTwinState.Hedged)} by shader fit), {blobs.Count} root signatures, {placed.Count} shaders placed under them"
            + string.Concat(rejected.Select(x => $"; {x.Value} pipeline records left out ({x.Key})"))
            + string.Concat(why.Values.GroupBy(w => w).OrderByDescending(g => g.Count()).Select(g => $"; {g.Count()} left out ({g.Key})")));
        var states = PoolStates(game, Root, graphics.Select(g => ((g.Key.Effect, g.Key.Rs),
            (IEnumerable<ShaderInfo>)[.. g.Value.Select(h => shaders[h]).Where(i => i.Stage == Stage.Vertex)])));
        return (
        [
            .. graphics.OrderBy(g => g.Key.Effect, StringComparer.Ordinal).ThenBy(g => g.Key.Rs, StringComparer.Ordinal).ThenBy(g => g.Key.Platform, StringComparer.Ordinal)
                .Select(g => new ShaderMap(CarvedReader.Sha1Hex($"{Family}|{g.Key.Effect}|{g.Key.Rs}|{g.Key.Platform}"), g.Key.Effect, g.Key.Platform, [.. g.Value], RootSignature: g.Key.Rs,
                    States: states.GetValueOrDefault((g.Key.Effect, g.Key.Rs)))),
            .. compute.OrderBy(x => x.Cs, StringComparer.Ordinal).ThenBy(x => x.Rs, StringComparer.Ordinal)
                .Select(x => new ShaderMap(CarvedReader.Sha1Hex($"{x.Cs}|{x.Rs}"), x.Cs, x.Platform, [x.Cs], IsPipeline: true, x.Rs)),
        ], why.Values.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>The vertex formats CRYENGINE defines in engine code, by the id the pipeline records name; in no file of the install.</summary>
    internal static readonly IReadOnlyDictionary<ulong, LayoutElem[]> BuiltInFormats = new Dictionary<ulong, LayoutElem[]>();

    /// <summary>Per (effect, root signature): the distinct states of the effect's graphics pipeline records under that root
    /// signature (<see cref="CryStates"/>), most records first. A record whose render pass doesn't read is left out; one whose
    /// vertex format no file or <paramref name="builtIn"/> defines takes its <paramref name="guessed"/> layout as a guess, or
    /// keeps a null layout.</summary>
    internal static Dictionary<(string Effect, string Rs), List<MapState>> PoolStates(IReadOnlyDictionary<ulong, LayoutElem[]> formats, IReadOnlyDictionary<ulong, CryPass> passes,
        IEnumerable<CryPso> pipelines, Func<ulong, IEnumerable<string>> rootsOf, IReadOnlyDictionary<ulong, LayoutElem[]>? builtIn = null,
        IReadOnlyDictionary<ulong, LayoutElem[]>? guessed = null)
    {
        var n = new Dictionary<(string, string), Dictionary<(ulong?, uint, string, uint), (MapState State, int N)>>();
        foreach (var p in pipelines)
        {
            if (!passes.TryGetValue(p.RenderPass, out var pass)) continue;
            LayoutElem[]? layout = p.VertexFormat is not { } vf ? [] : formats.TryGetValue(vf, out var f) ? f : builtIn?.GetValueOrDefault(vf);
            var guess = layout == null && p.VertexFormat is { } g && guessed?.TryGetValue(g, out layout) == true;
            var topo = CryStates.TopologyType(p.Topology);
            var k = (layout == null ? p.VertexFormat : p.VertexFormat ?? 0, topo, string.Join(',', pass.Rt), pass.Dsv);
            foreach (var rs in rootsOf(p.Layout))
            {
                if (!n.TryGetValue((p.Effect, rs), out var d)) n[(p.Effect, rs)] = d = [];
                d[k] = (d.TryGetValue(k, out var e) ? e.State : new MapState(layout, topo, pass.Rt, pass.Dsv, 0, guess), e.N + 1);
            }
        }
        return n.ToDictionary(x => x.Key, x => x.Value.Values.OrderByDescending(v => v.N).Select(v => v.State with { Records = v.N }).ToList());
    }

    static Dictionary<(string Effect, string Rs), List<MapState>> PoolStates(Game game, Func<ulong, (List<(string Sha, RootSig.Ranges Ranges)> Roots, string? Why)> root,
        IEnumerable<((string Effect, string Rs) Pool, IEnumerable<ShaderInfo> Vss)> pools)
    {
        var (formats, passes, pipelines) = CryStates.Read(game.InstallDir);
        var (builtIn, ambiguous) = CryAmdSamplers.Formats(CryAmdSamplers.Stamp(game) ?? "", game.InstallDir);
        IEnumerable<string> RootsOf(ulong id) => root(id).Roots.Select(r => r.Sha);
        Dictionary<ulong, LayoutElem[]>? guessed = null;
        if (CryAmdSamplers.GuessFormats)
        {
            guessed = GuessFormats(formats, builtIn, pipelines, RootsOf, pools);
            foreach (var (id, layout) in ambiguous) guessed[id] = layout;
        }
        return PoolStates(formats, passes, pipelines, RootsOf, builtIn, guessed);
    }

    /// <summary>A best guess for each vertex format the pipeline records name that no file or <paramref name="learned"/> defines:
    /// the base stream fitting most of its VSs (<see cref="CryVertexLayouts.Of"/>, ties in <see cref="CryVertexLayouts.Bases"/>
    /// order) with the engine's attachment streams, tangents as 16-bit (<see cref="CryVertexLayouts.Full"/>). Its VSs are those
    /// placed with it (<see cref="CryVertexLayouts.Place"/>), or for a format none is placed with, its pools' unplaced ones. A
    /// format with no VS or no fitting base gets none.</summary>
    internal static Dictionary<ulong, LayoutElem[]> GuessFormats(IReadOnlyDictionary<ulong, LayoutElem[]> shipped, IReadOnlyDictionary<ulong, LayoutElem[]> learned,
        IEnumerable<CryPso> pipelines, Func<ulong, IEnumerable<string>> rootsOf, IEnumerable<((string Effect, string Rs) Pool, IEnumerable<ShaderInfo> Vss)> pools)
    {
        var fmtsOf = new Dictionary<(string, string), HashSet<ulong>>();
        foreach (var p in pipelines)
            if (p.VertexFormat is { } vf)
                foreach (var rs in rootsOf(p.Layout)) (fmtsOf.TryGetValue((p.Effect, rs), out var set) ? set : fmtsOf[(p.Effect, rs)] = []).Add(vf);
        var list = pools.Select(p => (Formats: (IReadOnlyCollection<ulong>)(fmtsOf.GetValueOrDefault(p.Pool) ?? []), Vss: p.Vss.ToList())).ToList();
        var info = list.SelectMany(p => p.Vss).DistinctBy(v => v.Sha1).ToDictionary(v => v.Sha1);
        var place = CryVertexLayouts.Place(list.Select(p => (p.Formats, (IEnumerable<ShaderInfo>)p.Vss)), shipped);
        var o = new Dictionary<ulong, LayoutElem[]>();
        foreach (var f in fmtsOf.Values.SelectMany(x => x).Distinct().Where(f => !shipped.ContainsKey(f) && !learned.ContainsKey(f)).Order())
        {
            if ((place.Vss.GetValueOrDefault(f) ?? place.Open.GetValueOrDefault(f)) is not { Count: > 0 } vss) continue;
            var fit = CryVertexLayouts.Of([.. vss.Order(StringComparer.Ordinal).Select(h => info[h])]);
            if (fit.Fits[fit.Candidates[0]] > 0) o[f] = CryVertexLayouts.Full(CryVertexLayouts.Bases[fit.Candidates[0]].Elems, CryVertexLayouts.Tangents.Short);
        }
        return o;
    }

    /// <summary>The shaders of each layout the pipelines of their effect use that isn't shipped, compute ones for compute layouts,
    /// for a twin by fit; null once more than <paramref name="limit"/> pairs are gathered, so none is resolved by fit.</summary>
    internal static Dictionary<ulong, HashSet<CryShader>>? ShadersOfUnshipped(IEnumerable<(string Effect, CryShader Shader)> shaders,
        IReadOnlyDictionary<string, HashSet<(ulong Id, bool Compute)>> used, IReadOnlyDictionary<ulong, CryLayout> shipped, long limit, CancellationToken ct)
    {
        var o = new Dictionary<ulong, HashSet<CryShader>>();
        long n = 0;
        foreach (var (effect, s) in shaders)
        {
            ct.ThrowIfCancellationRequested();
            if (!used.TryGetValue(effect, out var ids)) continue;
            foreach (var (id, compute) in ids)
                if (compute == (s.Kind == 5) && !shipped.ContainsKey(id) && (o.TryGetValue(id, out var l) ? l : o[id] = []).Add(s) && ++n > limit) return null;
        }
        return o;
    }

    /// <summary>Why a shader's bindings aren't all in the root signature, by kind (no range of the class in the space, a register
    /// past the range, the stage not seeing it); null when they are (<see cref="RootSig.Uncovered(RootSig.Ranges, Stage, ShaderInfo)"/>).
    /// An unbounded binding needs an unbounded range here: a layout's bindless set is one, and Uncovered takes any table holding
    /// its first register.</summary>
    internal static string? Misfit(RootSig.Ranges rs, ShaderInfo sh)
    {
        var vis = sh.Stage switch { Stage.Vertex => 1u, Stage.Hull => 2u, Stage.Domain => 3u, Stage.Geometry => 4u, Stage.Pixel => 5u, _ => 0u };
        foreach (var b in sh.Bindings.Where(b => b.Count < 0))
        {
            var type = b.Class switch { "srv" => 0u, "uav" => 1u, "cbv" => 2u, "sampler" => 3u, _ => 9u };
            if (type != 9 && !rs.Slots.Any(x => x.Type == type && x.Space == (uint)b.Space && (x.Vis == 0 || x.Vis == vis) && x.Count == RootSig.Unbounded && x.Base <= (uint)b.Lower))
                return $"unbounded {b.Class} in a bounded range";
        }
        if (RootSig.Uncovered(rs, sh.Stage, sh) == null) return null;
        foreach (var b in sh.Bindings)
        {
            var type = b.Class switch { "srv" => 0u, "uav" => 1u, "cbv" => 2u, "sampler" => 3u, _ => 9u };
            if (type == 9) continue;
            var same = rs.Slots.Where(x => x.Type == type && x.Space == (uint)b.Space).ToList();
            if (same.Count == 0) return $"no {b.Class} range in its space";
            var seen = same.Where(x => x.Vis == 0 || x.Vis == vis).ToList();
            if (seen.Count == 0) return $"{b.Class} range hidden from the {sh.Stage.ToString().ToLowerInvariant()} stage";
            if (RootSig.Uncovered(rs with { Slots = [.. seen] }, sh.Stage, sh with { Bindings = [b] }) != null)
                return b.Count < 0 ? $"unbounded {b.Class} in a bounded range" : $"{b.Class} register past the range";
        }
        return "stage denied";
    }

    /// <summary>Decodes each entry the index found again; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        if (roots.TryGetValue(game.Id, out var rs))
            foreach (var h in sha1s)
                if (rs.TryGetValue(h, out var b)) sink(h, b);
        foreach (var pak in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Pak))
        {
            using var zip = OpenPak(game.InstallDir, pak.Key);
            if (zip == null) continue;
            foreach (var file in pak.GroupBy(x => x.At.Entry))
            {
                ct.ThrowIfCancellationRequested();
                if (zip.GetEntry(file.Key) is not { } e || Read(e) is not { } f) continue;
                foreach (var (sha, at) in file)
                    if (Decode(f, at.Offset, at.Size) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
            }
        }
    }

    /// <summary>The ShaderCache.pak files in the install folder and its top-level folders (CryEngine keeps it in Engine\ and the
    /// game's folder), an anti-cheat folder (<see cref="GameFiles.Marker"/>) never listed. Only that name: the other shader paks
    /// (Shaders, ShadersBin, ShaderCacheStartup, GameShaders) are encrypted on some games (measured: Hunt: Showdown 1896), which
    /// only reading them would tell, and are never opened.</summary>
    internal static List<string> Paks(string dir)
    {
        var options = new EnumerationOptions
        {
            MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        try
        {
            return Directory.Exists(dir) && RootAllowed(dir)
                ? GameFiles.EnumerateDirectories(dir, "*", options).Where(d => GameFiles.Marker(Path.GetFileName(d)) == AntiCheat.None && !IsLink(new DirectoryInfo(d)))
                    .Prepend(dir).SelectMany(d => GameFiles.EnumerateFiles(d, "ShaderCache.pak", options)).Where(p => Allowed(dir, p)).Order(GameFiles.PathOrder).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The pak as a ZIP when <see cref="OpenAllowed"/> opens it and it reads as one, else null.</summary>
    internal static ZipArchive? OpenPak(string root, string pak) => OpenAllowed(root, pak) is { } fs ? SwarmReader.Open(fs, out _) : null;

    /// <summary>The pak open for reading when <see cref="Allowed"/> and a regular file with one name, else null: a hard link
    /// (another name may be a pak never to be read) is closed with no byte read.</summary>
    static FileStream? OpenAllowed(string root, string pak)
    {
        if (!Allowed(root, pak)) return null;
        FileStream? fs = null;
        try
        {
            fs = GameFiles.OpenRegular(pak);
            if (fs == null || GameFiles.Links(fs.SafeFileHandle) == 1) return fs;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        fs?.Dispose();
        return null;
    }

    /// <summary>A pak the reader may open: a file of at least a ZIP's end record, no link, under <paramref name="root"/> through
    /// folders that aren't links, with no anti-cheat folder on its path or on the root's resolved one.</summary>
    static bool Allowed(string root, string pak)
    {
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var f = new FileInfo(Path.GetFullPath(pak));
            var rel = Path.GetRelativePath(root, f.FullName);
            if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) || !RootAllowed(root)) return false;
            // a FIFO, socket or device reports size 0: never opened, its open may never return
            if (!f.Exists || f.Length < 22 || IsLink(f)) return false;
            for (var d = f.Directory; d != null && d.FullName.Length > root.Length; d = d.Parent)
                if (IsLink(d) || GameFiles.Marker(d.Name) != AntiCheat.None) return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    static bool RootAllowed(string root) => RealRoot(root) != null;

    /// <summary><paramref name="root"/> resolved folder by folder, each link's target read from the link itself and its names
    /// checked before it is followed, so nothing in an anti-cheat folder is opened: the real folder, or null for a marker's
    /// name (<see cref="GameFiles.Marker"/>), a target that isn't a folder, a loop or a read that fails.</summary>
    static string? RealRoot(string root)
    {
        static IEnumerable<string> Parts(string p) => p.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var windows = OperatingSystem.IsWindows();
        try
        {
            // off Windows ".." leaves the folder a link before it led to, as the kernel resolves it: never collapsed as text
            var full = windows ? Path.GetFullPath(root) : Path.Combine(Directory.GetCurrentDirectory(), root);
            var at = Path.GetPathRoot(full)!;
            var left = new Stack<string>(Parts(full[at.Length..]).Reverse());
            for (var hops = 0; left.TryPop(out var name);)
            {
                if (name == ".") continue;
                if (name == "..")
                {
                    at = Path.GetDirectoryName(at) ?? at;
                    continue;
                }
                // a short name hides the long one the marker list has: one that doesn't expand isn't followed
                if (windows && name.Contains('~') && (GameFiles.LongPath(Path.Combine(at, name)) is not { } longPath || ShortAlias().IsMatch(name = Path.GetFileName(longPath))))
                    return null;
                if (GameFiles.Marker(name) != AntiCheat.None) return null;
                var d = new DirectoryInfo(Path.Combine(at, name));
                if (d.LinkTarget is { } target)
                {
                    if (++hops > 40) return null;
                    if (windows)
                    {
                        var to = Path.GetFullPath(target, at);
                        at = Path.GetPathRoot(to)!;
                        target = to[at.Length..];
                    }
                    else if (Path.IsPathRooted(target)) at = "/";
                    foreach (var p in Parts(target).Reverse()) left.Push(p);
                    continue;
                }
                if (!d.Exists) return null;
                at = d.FullName;
            }
            return at;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    [GeneratedRegex(@"^[^.~]{1,6}~[0-9]+(\.[^.]{0,3})?$")]
    private static partial Regex ShortAlias();

    static bool IsLink(FileSystemInfo i) => (i.Attributes & FileAttributes.ReparsePoint) != 0 || i.LinkTarget != null;

    /// <summary>The APIs of the CryRender&lt;api&gt;.dll files beside the selected exe, by name only; empty when none is there
    /// (a monolithic build, or a launcher stub), which says nothing about the API. Another folder's DLLs may be another build's.</summary>
    internal static HashSet<string> Renderers(Game game)
    {
        try
        {
            return Path.GetDirectoryName(game.ExePath) is { Length: > 0 } dir && Directory.Exists(dir)
                ? [.. new[] { D3D11, D3D12 }.Where(a => File.Exists(GameFiles.Find(dir, $"CryRender{a}.dll")))] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>The CG?Shaders files with their API folder ("D3D11", "D3D12") and file name. A name the pak holds twice is left out:
    /// ReadShaders finds an entry by its name.</summary>
    static IEnumerable<(ZipArchiveEntry Entry, string Api, string Name)> CgEntries(ZipArchive zip) => zip.Entries
        .Select(e => (Entry: e, Match: CgFile.Match(e.FullName))).Where(x => x.Match.Success)
        .GroupBy(x => x.Entry.FullName, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.First())
        .OrderBy(x => x.Entry.FullName, StringComparer.Ordinal)
        .Select(x => (x.Entry, x.Match.Groups[1].Value.ToUpperInvariant(), x.Match.Groups[2].Value));

    /// <summary>The entry's bytes when it reads whole and is at most <see cref="MaxFile"/>; else null.</summary>
    static byte[]? Read(ZipArchiveEntry e)
    {
        if (e.Length is < 16 or > MaxFile || e.IsEncrypted) return null;
        var b = new byte[e.Length];
        try
        {
            using var s = e.Open();
            s.ReadExactly(b);
            return s.ReadByte() == -1 ? b : null;   // longer than its header says
        }
        catch (Exception x) when (x is InvalidDataException or IOException or NotSupportedException or ArgumentException or OverflowException) { return null; }
    }

    /// <summary>A CPCK file's directory records with data (offset, size), or null when it isn't a CPCK file whose directory fits
    /// it. Entries aren't checked here: <see cref="Decode"/> bounds each.</summary>
    internal static List<(uint Offset, int Size)>? Records(ReadOnlySpan<byte> f)
    {
        if (f.Length < 16 || !f.StartsWith("CPCK"u8)) return null;
        var n = BinaryPrimitives.ReadInt32LittleEndian(f[8..]);
        var dir = BinaryPrimitives.ReadUInt32LittleEndian(f[12..]);
        if (n == 0) return [];   // an entry point with no combinations: its directory offset is -1 (measured)
        if (n is < 0 or > MaxRecords || dir < 16 || dir + 12L * n > f.Length) return null;
        var list = new List<(uint, int)>(n);
        for (var i = 0; i < n; i++)
        {
            var r = f.Slice((int)dir + 12 * i, 12);
            var size = (int)(BinaryPrimitives.ReadUInt32LittleEndian(r[4..]) & 0xFFFFFF);
            if (size > 0) list.Add((BinaryPrimitives.ReadUInt32LittleEndian(r[8..]), size));
        }
        return list;
    }

    /// <summary>The container of the entry at <paramref name="offset"/>, stored raw or as its u32 inflated size and LZSS;
    /// null when the entry overruns the file, inflates short or to more than a container and its header, or holds no valid container.</summary>
    internal static byte[]? Decode(ReadOnlySpan<byte> f, uint offset, int size)
    {
        if (size < 4 || offset + (long)size > f.Length) return null;
        var data = f.Slice((int)offset, size);
        if (Container(data) is { } raw) return raw;
        var n = BinaryPrimitives.ReadUInt32LittleEndian(data);
        // a flag byte and 8 matches, 17 bytes, inflate to 144 at most
        if (n is < 32 or > Dxbc.MaxSize + MaxHeader || n > (size - 4) * 9L) return null;
        var o = new byte[n];
        return Lzss(data[4..], o) == o.Length ? Container(o) : null;
    }

    /// <summary>The valid container starting at a 4-byte aligned offset in the first <see cref="MaxHeader"/> bytes, or null.</summary>
    static byte[]? Container(ReadOnlySpan<byte> b)
    {
        for (var k = 0; k <= MaxHeader && k + 32 <= b.Length; k += 4)
            if (Dxbc.HeaderSize(b[k..]) is var n and > 0 && k + n <= b.Length && Dxbc.Valid(b.Slice(k, n))) return b.Slice(k, n).ToArray();
        return null;
    }

    /// <summary>Okumura's LZSS as CryEngine stores it: a 4 KB ring of spaces written from 4078; each flag byte's bits, low first,
    /// give a literal (1) or a match (0) of two bytes, a 12-bit ring position (the second byte's high nibble on top) and the
    /// length - 3 in the low nibble. The bytes written to <paramref name="dst"/>, at most its length.</summary>
    internal static int Lzss(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Span<byte> ring = stackalloc byte[4096];
        ring.Fill((byte)' ');
        int r = 4096 - 18, p = 0, o = 0;
        uint flags = 0;
        while (o < dst.Length)
        {
            if (((flags >>= 1) & 0x100) == 0)
            {
                if (p >= src.Length) break;
                flags = src[p++] | 0xFF00u;
            }
            if ((flags & 1) != 0)
            {
                if (p >= src.Length) break;
                dst[o++] = ring[r] = src[p++];
                r = (r + 1) & 4095;
                continue;
            }
            if (p + 1 >= src.Length) break;
            int i = src[p] | (src[p + 1] & 0xF0) << 4, len = (src[p + 1] & 0x0F) + 3;
            p += 2;
            for (var k = 0; k < len && o < dst.Length; k++)
            {
                dst[o++] = ring[r] = ring[(i + k) & 4095];
                r = (r + 1) & 4095;
            }
        }
        return o;
    }
}
