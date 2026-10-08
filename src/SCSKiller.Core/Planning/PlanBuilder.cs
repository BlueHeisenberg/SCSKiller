using System.Security.Cryptography;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>One <see cref="Planner.Build"/> run (see <see cref="Planner"/> for what gets planned): reads the recording
/// (templates by shape, the root-signature check), walks the library's stage sets (<see cref="StageSets"/>), turns each into
/// plan PSOs (<see cref="Emit"/>) and writes the plan file.</summary>
sealed class PlanBuilder
{
    readonly Game game;
    readonly EngineInfo engine;
    readonly ShaderIndex index;
    readonly Recording? recording;
    readonly VendorCaps caps;
    readonly string outDir;
    readonly IProgress<string>? log;
    readonly CancellationToken ct;
    readonly bool maximum;
    readonly MiddlewarePacks? packs, sharedPacks;

    readonly IReadOnlyDictionary<string, ShaderInfo> bc;
    /// <summary>Shader maps; Global shaders pair across maps (one fullscreen VS serves pixel shaders from many global maps): pooled.</summary>
    readonly List<(string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline)> maps;
    readonly Dictionary<string, string> platOfSha = [];
    readonly RootSig.Rule rule;
    readonly uint maxSrvs; // the game's MAX_SRVS when its shaders need more than the rule's (0 = the rule's)

    // the recording
    readonly List<Rec> recs = [];   // PSO records ('G' / 'C' / 'S'): what plans are built from
    readonly List<Rec> stateObjects = []; // recorded ray tracing state objects ('R' / 'A'): replayed as recorded; RtPlan learns from them
    readonly List<Rec> nvRecs = [];       // the recording's 'N' records: the NVAPI state RtPlan gives synthesized collections
    readonly HashSet<string> layered = [];  // what the driver got from a layer wrapping the device ('W'): a mod's, kept out of packs (they are shared)
    readonly Dictionary<string, byte[]> recBlobs = [];
    readonly Dictionary<string, List<(string Key, string Rs, bool Layout)>> templates = [];
    readonly Dictionary<string, Rec> recByKey = [];
    readonly Dictionary<(uint Topology, string Shape), (string Key, string Rs, bool Layout)> gsTopo = []; // GS templates by input topology and stages (a stream template has only its own shaders' subobjects)
    readonly Dictionary<string, List<string>> rsByCounts = []; // recorded root signatures by resource counts, in recording order
    readonly HashSet<string> have = [];
    readonly Dictionary<string, int> plats = [];
    byte[]? samplers;
    readonly Dictionary<string, HashSet<string>> settings = [];   // a recorded root signature without its samplers' parameters -> the sampler sets it comes with
    readonly Dictionary<string, byte[]> samplerSets = [];          // ...each by its hex
    int builtOk, builtN, ownOk, ownN; // own: recorded PSOs whose shaders carry a root signature, and those created with it

    // decided from the recording and the caps
    bool verified, build, synth, dx12;
    string plat = "";
    int embeddedRs;
    string? plat11;
    List<string> d3d11 = [];
    readonly List<(string Hs, string Ds)> tess11 = [];

    readonly Dictionary<string, string?> rsCache = [];   // null: the runtime won't serialize it
    readonly Dictionary<string, byte[]> rsBlobs = [];
    readonly Dictionary<string, RootSig.Desc> rsDescs = [];   // the rule's root signatures, to serialize again with other samplers

    // the plan
    readonly List<byte[]> items = [];
    readonly List<Rec> synthesized = [];
    readonly Dictionary<string, string> synthByShape = [];
    readonly HashSet<string> usedTemplates = [];
    readonly HashSet<string> usedRs = [];
    readonly HashSet<string> seen = [];
    readonly SortedDictionary<string, int> stats = [];
    // per-stage plans (PerStage): where each new unit's state came from, and the index VSs whose layouts a recording gave
    readonly long[] unitsBy = new long[3];
    double layoutCoverage;

    /// <param name="maximum">with a per-stage cache: also every stage set whose units the cover already has, on its first
    /// resolved state (every pre-linked pair, as a whole-pipeline plan has them); the units stay the same</param>
    public PlanBuilder(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir,
        IProgress<string>? log, CancellationToken ct, bool maximum = false, MiddlewarePacks? packs = null, MiddlewarePacks? sharedPacks = null)
    {
        (this.game, this.engine, this.index, this.recording, this.caps, this.outDir, this.log, this.ct, this.maximum, this.packs, this.sharedPacks) =
            (game, engine, index, recording, caps, outDir, log, ct, maximum, packs, sharedPacks);
        bc = index.Shaders;
        maps = index.Maps.Select(m => (m.Platform, Pooled: false, Shas: m.Shaders, m.IsPipeline)).ToList();
        maps.AddRange(index.Maps.Where(m => m.Library == "Global").GroupBy(m => m.Platform)
            .Select(g => (g.Key, true, (IReadOnlyList<string>)g.SelectMany(m => m.Shaders).Distinct().ToList(), false)));
        foreach (var m in index.Maps) foreach (var h in m.Shaders) platOfSha[h] = m.Platform;
        rule = RootSig.RuleFor(engine) ?? RootSig.Rule.Ff7; // no rule: FF7's is what a recording gets checked against
        maxSrvs = RootSig.MaxSrvsFor(rule, bc.Values);
    }

    public Plan Build()
    {
        ReadRecording();
        Decide();
        if (dx12) RuntimeBuilt();
        if (dx12 && UnitPolicy.For(caps) is { } policy) PerStage(policy);
        else StageSets(Emit);
        if (dx12 && Planner.RtCollectionCache(caps))
            try { RtPlan(); }
            catch (RootSig.SerializeException e)   // the rule's global root signature: no collections, the PSOs still plan
            {
                rtItems.Clear();
                log?.Report($"warning: ray tracing: no collections: the runtime won't serialize the rule's global root signature ({e.Message})");
            }
        if (Planner.SeedsPacks(engine) && (packs ?? sharedPacks) != null) SeedMiddleware();
        return Write();
    }

    // middleware packs: entries seeded into this plan ('M' records) and the root signatures they use
    readonly List<Rec> packEntries = [];
    readonly Dictionary<string, byte[]> packRs = [];
    long packNew, packShared;

    /// <summary>What seeding read of both kinds of pack (<see cref="Planner.PackFingerprint"/>'s form); null when it read none.</summary>
    public string? PackFingerprint { get; private set; }

    /// <summary>Middleware DLLs next to the exe: this recording's PSOs made only of a DLL's shaders (none in the game's
    /// index) go into that DLL version's pack (<see cref="MiddlewarePacks.Promote"/>); then every pack of a DLL version this
    /// install has seeds the plan, whichever game's recording filled it: this PC's packs, then the shared ones downloaded
    /// for this GPU vendor. Recorded PSOs aren't enumerated: only replayed. A PSO with a [WaveSize] this vendor doesn't run
    /// is left out (a pack recorded on another vendor's GPU: the runtime would reject it).</summary>
    void SeedMiddleware()
    {
        var dlls = Middleware.Detect(game);
        if (dlls.Count == 0) return;
        // a game the user added never fills a pack: packs are shared, and its recording's origin is unknown
        if (recs.Count > 0 && packs != null && game.Store != Store.Manual)
            foreach (var p in packs.Promote(recs, recBlobs, bc, dlls, game.Id, caps.Profile.StartsWith("amd") ? "amd" : caps.Profile.StartsWith("nvidia") ? "nvidia" : null, layered))
                log?.Report($"middleware: {p.Records} recorded PSOs are {p.Dll.Name}'s ({p.Dll.Vendor}, {p.ContentHash[..12]}), {p.New} new in its pack");
        var seen = new HashSet<string>();
        var recKeys = recs.Select(r => r.Key).ToHashSet();
        var amd = caps.Profile.StartsWith("amd");
        var lanes = new Dictionary<string, bool>();
        var wrongLanes = 0;
        string local = "", remote = "";
        List<MiddlewarePacks.Seeded> ours = packs?.Seed(dlls, out local) ?? [], theirs = sharedPacks?.Seed(dlls, out remote) ?? [];
        PackFingerprint = local + (remote.Length > 0 ? "|shared:" + remote : "");
        foreach (var (seeded, shared) in new[] { (ours, false), (theirs, true) })
            foreach (var s in seeded)
            {
                var image = Middleware.Scan(s.Dll.Path);
                int n = 0, recorded = 0, dup = 0, lanesOut = 0;
                foreach (var e in s.Pack.Entries)
                {
                    if (!seen.Add(e.Key)) { dup++; continue; } // in this PC's pack too, or its shaders are in two DLLs (FSR 3.1 in amdxcffx64 and the upscaler DLL): seeded once
                    if (!MiddlewarePacks.Runs(e, image, amd, lanes)) { lanesOut++; continue; }
                    var pso = Parse(e);
                    packEntries.Add(MiddlewarePacks.Wrap(e, s.Dll.Name, s.ContentHash));
                    if (s.Pack.RootSignatures.TryGetValue(pso.Rs, out var rs)) packRs[pso.Rs] = rs;
                    n++;
                    if (recKeys.Contains(e.Key)) recorded++; // replayed from scskiller.db anyway (Materialize skips it)
                    else
                    {
                        packNew++;
                        if (shared) packShared++;
                    }
                }
                wrongLanes += lanesOut;
                log?.Report($"middleware: {s.Dll.Name} ({s.Dll.Vendor}, {s.ContentHash[..12]}): {n} {(shared ? "shared " : "")}pack PSOs ({recorded} already in the recording, {n - recorded} new"
                    + (dup > 0 ? $"; {dup} seeded already" : "") + (lanesOut > 0 ? $"; {lanesOut} with a wave size this GPU doesn't run left out" : "")
                    + $"; from {string.Join(", ", s.Pack.Header.Sources)})");
            }
        if (wrongLanes > 0) stats["vendor_extension"] = stats.GetValueOrDefault("vendor_extension") + wrongLanes;
    }

    /// <summary>Recorded PSOs with a shader in no file of the install (neither the index nor a middleware DLL next to the exe):
    /// built at run time (D3DCompile/DXC in the game or a mod) or an overlay's from outside the game folder. Only this PC's
    /// recording has their bytes: they replay from it (Materialize copies it whole), and a hash-only upload names them without them, flagged 'L' (HashOnly.LocalOnly).</summary>
    void RuntimeBuilt()
    {
        var shas = recs.SelectMany(r => Parse(r).Stages.Values).Where(h => !bc.ContainsKey(h)).ToHashSet();
        if (shas.Count == 0) return;
        shas.ExceptWith(Middleware.Detect(game).Where(d => d.Packable).SelectMany(d => Middleware.Scan(d.Path).Containers.Keys));
        if (shas.Count == 0) return;
        var psos = recs.Count(r => Parse(r).Stages.Values.Any(shas.Contains));
        log?.Report($"recorded: {psos} PSOs use {shas.Count} shaders in no file of the install (built at run time, or an overlay's): "
            + $"{shas.Count(recBlobs.ContainsKey)} of those shaders are in this PC's recording and replay from it; a shared recording flags those PSOs as needing a recording");
    }

    /// <summary>Templates by shape, platform in use, and a check that root signatures rebuild from shader counts.</summary>
    void ReadRecording()
    {
        if (recording != null)
            foreach (var r in Read(recording.DbPath))
                if (r.Tag == 'B') recBlobs[Hex(r.Payload.AsSpan(0, 20))] = r.Payload[20..];
                else if (r.Tag is 'G' or 'C' or 'S') recs.Add(r);
                else if (IsStateObject(r.Tag)) stateObjects.Add(r);
                else if (r.Tag == 'N') nvRecs.Add(r);
                else if (r.Tag == 'W' && r.Payload.Length == 40) layered.Add(Hex(r.Payload.AsSpan(0, 20)));
        // a root signature the readers can't follow (a shared recording's) goes with the records naming it, not the game
        var named = recs.Select(r => Parse(r).Rs).Concat(stateObjects.SelectMany(r => ParseStateObject(r).RootSignatures)).ToHashSet();
        var bad = named.Where(h => recBlobs.TryGetValue(h, out var b) && !Carved.Dxbc.RootSignatureValid(b)).ToHashSet();
        if (bad.Count > 0)
        {
            foreach (var h in bad) recBlobs.Remove(h);
            var left = recs.RemoveAll(r => bad.Contains(Parse(r).Rs)) + stateObjects.RemoveAll(r => ParseStateObject(r).RootSignatures.Any(bad.Contains));
            log?.Report($"warning: recorded: {bad.Count} malformed root signatures left out with the {left} records naming them");
        }
        var unbuilt = 0;
        foreach (var r in recs)
        {
            var pso = Parse(r);
            // a layer's output (what the driver got, 'W'): replayed as recorded, never a template nor a rule check
            if (layered.Contains(r.Key) || !pso.Stages.Values.All(bc.ContainsKey)) { have.Add(pso.Tuple); continue; } // not a library shader (e.g. an overlay's): no template
            var st = Infos(pso.Stages);
            if (pso.Stages.Values.Select(h => bc[h].RootSignature).FirstOrDefault(h => h != null) is { } own)
            {
                ownN++;
                if (own == pso.Rs) ownOk++;
            }
            if (recBlobs.TryGetValue(pso.Rs, out var rsBlob))
            {
                // the rule's rebuild with these samplers the runtime won't serialize (its ranges overlap them): the rule
                // doesn't reproduce this one, which still replays as recorded
                builtN++;
                // static samplers follow the player's texture filtering setting: a recording shared by players of other
                // settings is checked with each record's own (FF7 Rebirth's mixes 1x and 16x anisotropic)
                var its = RootSig.Samplers(rsBlob);
                try
                {
                    if (RootSig.Serialize(RootSig.Build(rule, st, MeshTier(pso.Stages), maxSrvs), its).AsSpan().SequenceEqual(rsBlob)) builtOk++;
                    samplers ??= its;
                    if (SamplerVariants.Setting(rsBlob) is { } x)
                    {
                        (settings.TryGetValue(x.RootSignature, out var with) ? with : settings[x.RootSignature] = []).Add(x.Samplers);
                        samplerSets.TryAdd(x.Samplers, its);
                    }
                }
                catch (RootSig.SerializeException) { unbuilt++; }
            }
            have.Add(pso.Tuple);
            // a [WaveSize] partition counts for its base platform: OnPlatform takes it in relative to that one
            // an indexed shader in no map (a REDengine 3 technique left out) has no platform
            foreach (var p in pso.Stages.Values.Where(platOfSha.ContainsKey).Select(h => platOfSha[h].Split(" wave")[0])) plats[p] = plats.GetValueOrDefault(p) + 1;
            if (!rsByCounts.TryGetValue(Planner.CountsKey(st), out var byCounts)) rsByCounts[Planner.CountsKey(st)] = byCounts = [];
            if (!byCounts.Contains(pso.Rs)) byCounts.Add(pso.Rs);
            var t = (r.Key, pso.Rs, pso.HasLayout);
            recByKey[r.Key] = r;
            var shape = Planner.Shape(pso.Stages);
            Add($"{shape}|{PsOut(pso.Stages)}", t);
            Add(shape, t); // fallback: any template with these stages
            if (pso.Stages.ContainsKey((int)Stage.Geometry)) gsTopo.TryAdd((pso.Topology, shape), t);
        }
        if (unbuilt > 0) log?.Report($"recorded: the rule's rebuild of {unbuilt} root signatures doesn't serialize with their static samplers: counted as not rebuilt (they replay as recorded)");
        void Add(string shape, (string, string, bool) t) { if (!templates.TryGetValue(shape, out var l)) templates[shape] = l = []; l.Add(t); }
    }

    RtCollections.Nv? rasterNv;   // the NVAPI state every synthesized PSO is created with (an 'N' record each); null = none

    /// <summary>The NVAPI state a game creates its PSOs with: NVIDIA keys a compile on the shader-extension slot (selftest
    /// nvext), so a PSO warmed without the game's slot is a miss. From a recording: the slot and space at least 99% of its
    /// raster records ('G' / 'C' / 'S') share, with their most common creation options (The Witcher 3: 841 of 843 at slot
    /// 12 space 1, options 0 on 789 and 17 on 52). Otherwise, for REDengine 3, its slot 12 space 1, as its recording has it:
    /// also when the recording has none (made on AMD, where the recorder captures no NVAPI state). NVIDIA only: AMD's
    /// runtime has no NVAPI state.</summary>
    internal static RtCollections.Nv? RasterNv(VendorCaps caps, RootSig.Rule rule, IReadOnlyCollection<Rec> recs, IEnumerable<Rec> nvRecs) =>
        !caps.Profile.StartsWith("nvidia") ? null : LearnedRasterNv(recs, nvRecs) ?? (rule == RootSig.Rule.Red3 ? new RtCollections.Nv(12, 1, 0) : null);

    /// <summary>The recording's part of <see cref="RasterNv"/>: the slot and space at least 99% of the raster records' final
    /// states share (a record's last 'N' is what the warm applies), with their most common options; null otherwise.</summary>
    internal static RtCollections.Nv? LearnedRasterNv(IEnumerable<Rec> recs, IEnumerable<Rec> nvRecs)
    {
        var keys = recs.Where(r => r.Tag is 'G' or 'C' or 'S').Select(r => r.Key).ToHashSet();
        var states = nvRecs.Where(r => r.Tag == 'N').Select(NvState.Parse).Where(n => keys.Contains(n.Target))
            .GroupBy(n => n.Target).Select(g => g.Last()).Where(n => n.Slot != uint.MaxValue).ToList();
        var top = states.GroupBy(n => (n.Slot, n.Space)).MaxBy(g => g.Count());
        return top != null && top.Count() >= 0.99 * keys.Count
            ? new RtCollections.Nv(top.Key.Slot, top.Key.Space, top.GroupBy(n => n.Options).MaxBy(g => g.Count())!.Key) : null;
    }

    void Decide()
    {
        var fromRecording = builtN > 0;
        verified = fromRecording ? builtOk >= 0.99 * builtN : RootSig.Verified(engine);
        build = fromRecording ? verified : RootSig.RuleFor(engine) != null;
        samplers ??= RootSig.StaticSamplers(rule);
        synth = caps.StateIndependentCache;
        dx12 = engine.GraphicsApi.Contains("D3D12");
        plat = !dx12 ? "" : plats.Count > 0 ? plats.MaxBy(p => p.Value).Key // "": no map matches, no PSOs
            : new[] { "PCD3D_SM6", "PCD3D_SM5" }.FirstOrDefault(index.Platforms.Contains) ?? index.Platforms.FirstOrDefault() ?? "";
        embeddedRs = bc.Values.Count(s => s.RootSignature != null);
        if (!build && embeddedRs == 0 && !maps.Any(m => m.Pooled)) Learned();
        rasterNv = RasterNv(caps, rule, recs, nvRecs);
        if (rasterNv is { } nvs) log?.Report($"NVAPI: synthesized PSOs get shader-extension slot {nvs.Slot} space {nvs.Space} (options {nvs.Options})");
        var ownOnly = embeddedRs > 0 && ownN == builtN; // every recorded PSO the rule was checked on carries its own: the rule plans nothing
        if (dx12) log?.Report($"recorded: {recs.Count} PSOs{(stateObjects.Count > 0 ? $" + {stateObjects.Count} ray tracing state objects (replayed as recorded)" : "")}, platform {plat}; "
            + (ownOnly ? "" : $"root sigs rebuilt from shader counts: {builtOk}/{builtN} exact -> " + (build ? "building" : "learned lookup")) + (maxSrvs > 0 ? $" (SRV tables of {maxSrvs}, not Unreal {engine.Version}'s {RootSig.MaxSrvs(rule)}: {(maxSrvs == 128 ? "the bindless fork's" : "its shaders bind more")})" : "")
            + (embeddedRs > 0 ? $"{(ownOnly ? "" : "; ")}{embeddedRs} shaders carry their own root signature{(ownN > 0 ? $", {ownOk}/{ownN} recorded PSOs created with it" : "")}" : "") + (synth ? ", synthesized templates allowed" : ""));

        // D3D11: every shader of the game's SM5 platform once (Unreal PCD3D_SM5; the carver's DXBC containers), no pairing;
        // hull and domain shaders as HS+DS pairs of one map (the warm needs both to draw), every one in at least one pair;
        // only the stages the vendor's cache keeps apart from the game's state (Planner.D3D11Warms)
        plat11 = index.Platforms.Contains("PCD3D_SM5") ? "PCD3D_SM5" : null;
        if (!engine.GraphicsApi.StartsWith("D3D11") || !Planner.D3D11Cache(caps)) return;
        var maps11 = index.Maps.Where(m => m.Platform == (plat11 ?? m.Platform)).Select(m => m.Shaders.Where(h => bc.TryGetValue(h, out var s) && Planner.IsD3D11(s)).Select(h => bc[h]).ToList()).ToList();
        d3d11 = maps11.SelectMany(m => m).Where(s => s.Stage is not (Stage.Hull or Stage.Domain) && Planner.D3D11Warms(caps, s.Stage)).Select(s => s.Sha1).Distinct().ToList();
        if (!Planner.D3D11Warms(caps, Stage.Hull)) return;
        var paired = new HashSet<string>();
        foreach (var (h, d) in maps11.SelectMany(Planner.TessPairs)) // greedy: a pair is kept when it brings a shader no kept pair has
            if (paired.Add(h.Sha1) | paired.Add(d.Sha1)) tess11.Add((h.Sha1, d.Sha1));
        var unpaired = maps11.SelectMany(m => m).Where(s => s.Stage is Stage.Hull or Stage.Domain && !paired.Contains(s.Sha1)).Select(s => s.Sha1).Distinct().Count();
        if (unpaired > 0) stats["d3d11_tess_unpaired"] = unpaired;
    }

    /// <summary>The stage set's root signature, null when there is none. One the runtime won't serialize (a rule's ranges
    /// overlapping a recording's static samplers) is null too, counted as "rs_unserializable": that stage set is left
    /// out, not the plan.</summary>
    string? RootSigOf(SortedDictionary<int, string> stages)
    {
        unbuildable = false;
        // carved shaders carrying their root signature (RTS0): exact, served by the reader at materialize time
        if (stages.Values.Select(h => bc[h].RootSignature).FirstOrDefault(r => r != null) is { } embedded) return embedded;
        if (rule == RootSig.Rule.Red3 && !RootSig.Red3Validated(stages.Keys.Select(k => (Stage)k))) return null; // not a stage set the rule was confirmed on
        if (!build) return rsByCounts.TryGetValue(Planner.CountsKey(Infos(stages)), out var learned) ? learned.FirstOrDefault(r => Covers(r, stages), learned[0]) : null;
        var desc = RootSig.Build(rule, Infos(stages), MeshTier(stages), maxSrvs);
        if (!rsCache.TryGetValue(desc.Key, out var h))
        {
            try
            {
                var b = RootSig.Serialize(desc, samplers!);
                rsCache[desc.Key] = h = Hex(SHA1.HashData(b));
                rsBlobs[h] = b;
                rsDescs[h] = desc;
            }
            catch (RootSig.SerializeException e)
            {
                rsCache[desc.Key] = h = null;
                unserializable ??= e.Message;
            }
        }
        unbuildable = h == null;
        return h;
    }

    string? unserializable;   // the first serializer error
    bool unbuildable;         // RootSigOf's last null: the runtime won't serialize it (a pipeline that can't exist, not a gap)

    /// <summary>What a stage set without a root signature counts as.</summary>
    string NoRs => unbuildable ? "rs_unserializable" : "no_rs";

    // a learned lookup's stage and counts parts (Planner.CountsKey), and of its VS+PS keys the VS parts per PS part
    HashSet<string>? learnedParts;
    readonly Dictionary<string, HashSet<string>> learnedVs = [];
    int unpaired;   // the shaders StageSets left out or paired only in part for it

    static string Part(ShaderInfo s) => $"{(int)s.Stage}:{s.Counts}";

    /// <summary>A learned lookup resolves a stage set only by its counts key, so StageSets pairs only what a recorded key has:
    /// each shader's part, and a VS->PS pair's two (RE Engine: 13.5M of PRAGMATA's 13.6M stage sets had no root signature,
    /// 46 s and 5.7 GB to enumerate). Not with a pooled library: whether a PS is fed in its own map counts every VS there.</summary>
    void Learned()
    {
        learnedParts = [];
        foreach (var parts in rsByCounts.Keys.Select(k => k.Split(';')))
        {
            learnedParts.UnionWith(parts);
            if (parts is [var vs, var ps] && vs.StartsWith($"{(int)Stage.Vertex}:") && ps.StartsWith($"{(int)Stage.Pixel}:"))
                (learnedVs.TryGetValue(ps, out var l) ? l : learnedVs[ps] = []).Add(vs);
        }
    }

    readonly Dictionary<string, RootSig.Ranges?> rsRanges = [];
    readonly Dictionary<(string Rs, string Sha, int Stage), bool> covered = [];
    readonly Dictionary<string, string> uncoveredExample = [];

    /// <summary>Pre-emit guard: every shader's declared resources are in the root signature, visible to its stage (else the
    /// driver rejects the PSO: E_INVALIDARG). A rule that doesn't know a fork's extra ranges (Hogwarts Legacy's bindless SRVs
    /// in spaces 4-7: 159594 of 200804 planned PSOs) or a learned root signature picked by counts alone fails here, and the
    /// stage set is counted as "rs_uncovered" instead of planned. A root signature the planner can't read (a carved shader's
    /// own, served at materialize) is trusted.</summary>
    bool Covers(string rs, SortedDictionary<int, string> stages)
    {
        if (!rsRanges.TryGetValue(rs, out var ranges))
            rsRanges[rs] = ranges = rsBlobs.TryGetValue(rs, out var b) || recBlobs.TryGetValue(rs, out b) ? RootSig.Parse(b) : null;
        if (ranges == null) return true;
        foreach (var (st, sha) in stages)
        {
            if (covered.TryGetValue((rs, sha, st), out var ok)) { if (!ok) return false; continue; }
            var why = RootSig.Uncovered(ranges, (Stage)st, bc[sha]);
            covered[(rs, sha, st)] = why == null;
            if (why == null) continue;
            uncoveredExample.TryAdd(why, sha);
            return false;
        }
        return true;
    }

    List<LayoutElem>? LayoutFor(SortedDictionary<int, string> stages, bool templateHasLayout) // the VS's own inputs, never the template's
    {
        if (!stages.TryGetValue((int)Stage.Vertex, out var vs)) return null;
        return templateHasLayout || bc[vs].Inputs.Any(e => e.SysValue == 0) ? Planner.VsLayout(bc[vs]) : null;
    }

    void Count(string k) => stats[k] = stats.GetValueOrDefault(k) + 1;

    /// <summary>One stage set as whole-pipeline PSOs: a recorded template of its shape with the shaders swapped (every GS
    /// template of the GS's input topology), else a synthesized one.</summary>
    void Emit(SortedDictionary<int, string> stages, string shape, string psOut)
    {
        if (!seen.Add(Tuple("", stages))) return; // shaders shared across maps
        var rs = RootSigOf(stages);
        var gs = stages.TryGetValue((int)Stage.Geometry, out var g) ? bc[g] : null;
        var topo = stages.ContainsKey((int)Stage.Hull) ? 4u : gs == null ? 3u : Planner.TopologyType(gs.GsInputPrimitive); // 4 = patch; 0 = GS input unknown
        // a GS needs its input's topology type: the recorded templates of that type and stages (all topologies when unknown:
        // the ones not matching fail harmlessly at replay), else a synthesized one
        var cands = gs != null ? gsTopo.Where(t => (topo == 0 || t.Key.Topology == topo) && t.Key.Shape == shape).Select(t => t.Value).ToList() is { Count: > 0 } l ? l : null
            : templates.GetValueOrDefault($"{shape}|{psOut}") ?? templates.GetValueOrDefault(shape);
        if (rs == null || (cands == null && (!synth || topo == 0))) { Count(rs == null ? NoRs : gs != null ? "no_gs_template" : "no_template"); return; }
        if (!Covers(rs, stages)) { Count("rs_uncovered"); return; }
        if (have.Contains(Tuple(rs, stages))) { Count("already_recorded"); return; }
        usedRs.Add(rs);
        Count("generated");
        if (gs != null && cands != null)
        {
            foreach (var t in cands) { items.Add(Item(t.Key, rs, stages, LayoutFor(stages, t.Layout))); usedTemplates.Add(t.Key); }
            return;
        }
        if (cands != null)
        {
            var t = cands.FirstOrDefault(c => c.Rs == rs, cands[0]);
            items.Add(Item(t.Key, rs, stages, LayoutFor(stages, t.Layout)));
            usedTemplates.Add(t.Key);
            return;
        }
        var (rt, dsv) = Targets(stages);
        var skey = $"{shape}|{topo}|{string.Join(',', rt)}|{dsv}";
        var layout = LayoutFor(stages, true);
        if (synthByShape.TryGetValue(skey, out var tkey)) items.Add(Item(tkey, rs, stages, layout));
        else // the first PSO of a shape is its template (and gets compiled as itself)
        {
            var rec = new Rec('S', Stream(rs, stages, layout, topo, rt, dsv));
            synthByShape[skey] = rec.Key;
            synthesized.Add(rec);
        }
    }

    /// <summary>A per-stage cache (<see cref="VendorCaps.PerStageCache"/>, AMD today) needs each stage unit compiled once, not
    /// every VS x PS pair, and (AMD) with the state its stage is keyed on (<see cref="UnitPolicy"/>): what the recording says
    /// about each shader (<see cref="ExactLayouts"/>, resolved per shader, not per pair), the units of every stage set of the
    /// library, one greedy cover (<see cref="UnitCover.CoverAll"/>) seeded with the recording's own units (it replays as it
    /// is), each pick a synthesized 'S' stream with its exact read layout, topology, RT formats / write masks / logic op of its
    /// export shape. A VS alone (depth pass) is its own unit. Stage sets whose state is exact or inferred come first, guesses
    /// last, so a stopped warm has done the likely ones.</summary>
    void PerStage(UnitPolicy policy)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var facts = ExactLayouts.Build(recs, recBlobs, policy, bc);
        var cover = UnitCover.Seeded(facts, h => bc.ContainsKey(h) || recBlobs.ContainsKey(h)); // a shared recording's PSO with a shader neither has doesn't replay
        var recorded = cover.Covered.ToHashSet();

        var layouts = new Dictionary<string, Resolved<List<List<LayoutElem>>>>();
        var shapes = new Dictionary<string, Resolved<List<string>>>();
        var topos = new Dictionary<string, List<uint>>();
        var cands = new List<(UnitCover.Candidate C, Provenance Vs, Provenance Ps)>();
        int pairs = 0;
        Resolved<List<List<LayoutElem>>> none = new([], Provenance.Exact);
        StageSets((stages, _, _) =>
        {
            if (!seen.Add(Tuple("", stages))) return; // shaders shared across maps
            var rs = RootSigOf(stages);
            if (rs == null) { Count(NoRs); return; }
            if (!Covers(rs, stages)) { Count("rs_uncovered"); return; }
            if (rsBlobs.TryGetValue(rs, out var blob)) facts.AddRootSignature(rs, blob); // VS units share PIXEL-only differences
            var l = stages.TryGetValue((int)Stage.Vertex, out var vs) ? layouts.TryGetValue(vs, out var r) ? r : layouts[vs] = facts.Layouts(bc[vs]) : none;
            var s = stages.TryGetValue((int)Stage.Pixel, out var ps) ? shapes.TryGetValue(ps, out var q) ? q : shapes[ps] = facts.Shapes(bc[ps]) : new([], Provenance.Exact);
            List<uint> t = [];
            if (vs != null)
            {
                var tk = $"{vs}|{stages.GetValueOrDefault((int)Stage.Geometry)}|{stages.ContainsKey((int)Stage.Hull)}";
                if (!topos.TryGetValue(tk, out t!)) topos[tk] = t = facts.Topologies(stages).Value;
            }
            if (ps != null && (vs != null || stages.ContainsKey((int)Stage.Mesh))) pairs++;
            cands.Add((new UnitCover.Candidate(stages, rs, l.Value, t, s.Value), l.Provenance, s.Provenance));
        });
        var ordered = cands.OrderBy(c => (int)(c.Vs > c.Ps ? c.Vs : c.Ps)).ToList(); // stable: exact, inferred, then guessed
        var prov = new Dictionary<UnitCover.Candidate, (Provenance Vs, Provenance Ps)>(ReferenceEqualityComparer.Instance);
        foreach (var c in ordered) prov[c.C] = (c.Vs, c.Ps);
        var picks = cover.CoverAll(ordered.Select(c => c.C).ToList());
        var coverSize = picks.Count;
        if (maximum) // every stage set once more, on its first candidates (their units are covered: nothing new to compile)
        {
            var byCand = picks.ToLookup<(UnitCover.Candidate Candidate, UnitPick Pick), UnitCover.Candidate>(p => p.Candidate, ReferenceEqualityComparer.Instance);
            picks = [.. ordered.SelectMany(o => byCand[o.C] is var own && own.Any() ? own
                : have.Contains(Tuple(o.C.Rs, o.C.Stages)) ? [] // recorded: the recording replays it
                : [(o.C, FirstPick(o.C))])];
        }

        var counted = recorded.ToHashSet();
        var newByStage = new SortedDictionary<Stage, int>();
        foreach (var (c, p) in picks)
        {
            var (pv, pp) = prov[c];
            foreach (var u in UnitCover.Units(facts, c.Stages, c.Rs, p.Layout, p.Topology, p.Shape))
                if (counted.Add(u))
                {
                    unitsBy[(int)(u.Stage == Stage.Vertex ? pv : u.Stage == Stage.Pixel ? pp : Provenance.Exact)]++;
                    newByStage[u.Stage] = newByStage.GetValueOrDefault(u.Stage) + 1;
                }
            var stages = (SortedDictionary<int, string>)c.Stages;
            var hasPs = stages.TryGetValue((int)Stage.Pixel, out var ps);
            var (rt, masks, ops) = hasPs && p.Shape != "" ? facts.ShapeExample[p.Shape] : ([], [], []);
            // DSV, blend and depth state are CHEAP: a depth pass gets a depth buffer, a PS one only if it writes depth, unless
            // the recording says how the PS's shape is drawn (AMD: then the game's PSO is an exact hit, not a ~3 ms relink)
            var dsv = !hasPs ? D32Float : Planner.Targets(bc[ps!]).Dsv;
            var link = hasPs && policy.ExportShape ? facts.Link(bc[ps!], p.Shape) : null;
            if (link is { Dsv: 0 } && dsv != 0) link = link with { Dsv = dsv }; // a PS writing depth keeps a depth buffer
            synthesized.Add(new Rec('S', link != null ? Stream(c.Rs, stages, p.Layout, p.Topology, rt, link.Dsv, blend: link.Blend, depthStencil: link.DepthStencil)
                : Stream(c.Rs, stages, p.Layout, p.Topology, rt, dsv, masks, ops.Length > 0 ? ops[0] : -1)));
            usedRs.Add(c.Rs);
            Count("generated");
        }

        // how much of the library's vertex input the recording pins down: recorded for the VS (L0), no vertex input (L0'),
        // recorded for a VS with the same input signature (L1)
        var libVs = maps.Where(m => m.Platform == plat).SelectMany(m => m.Shas).Distinct().Where(h => bc.TryGetValue(h, out var v) && v.Stage == Stage.Vertex).Select(h => bc[h]).ToList();
        var l0 = libVs.Count(v => facts.ReadLayouts.ContainsKey(v.Sha1));
        var l0b = libVs.Count(v => !facts.ReadLayouts.ContainsKey(v.Sha1) && v.Inputs.All(i => i.SysValue != 0));
        var l1 = libVs.Count(v => !facts.ReadLayouts.ContainsKey(v.Sha1) && v.Inputs.Any(i => i.SysValue == 0) && facts.LayoutsBySig.ContainsKey(ExactLayouts.SigKey(v)));
        layoutCoverage = libVs.Count == 0 ? 1 : (double)(l0 + l0b + l1) / libVs.Count;
        log?.Report($"per-stage ({policy.Name}): {cands.Count} stage sets ({pairs} with a PS), units {recorded.Count} recorded + {counted.Count - recorded.Count} new "
            + $"({string.Join(", ", newByStage.Select(s => $"{s.Key} {s.Value}"))}; exact/inferred/guessed {string.Join('/', unitsBy)}), "
            + $"cover {coverSize} PSOs{(maximum ? $" + {picks.Count - coverSize} for stage sets it doesn't need (maximum)" : "")}; VS layouts resolved from the recording: {layoutCoverage:P0} of {libVs.Count} (L0 {l0}, L0' no vertex input {l0b}, L1 {l1}) ({sw.Elapsed.TotalSeconds:F1}s)"
            + (facts.ClassFixes.Count > 0 ? $"; guessed layouts with a mixed slot: {string.Join(", ", facts.ClassFixes.Select(f => $"{f.Key} {f.Value}"))}" : ""));
        if (layoutCoverage < 0.8 && policy.ReadLayout)   // a vendor whose VS key ignores the layout (NVIDIA) doesn't care
            log?.Report($"warning: only {layoutCoverage:P0} of the game's vertex shaders have a recorded input layout; the rest are inferred or guessed "
                + "(a guessed layout that's wrong costs a compile in game): a longer recording helps");
    }

    /// <summary>A stage set's first candidates (what <see cref="UnitCover.Cover"/> zips from).</summary>
    static UnitPick FirstPick(UnitCover.Candidate c)
    {
        var vs = c.Stages.ContainsKey((int)Stage.Vertex);
        return new UnitPick(vs && c.Layouts.Count > 0 ? c.Layouts[0] : [], vs ? c.Topologies.Count > 0 ? c.Topologies[0] : 3u : 0u,
            c.Stages.ContainsKey((int)Stage.Pixel) && c.Shapes.Count > 0 ? c.Shapes[0] : "");
    }

    const int AgsSpace = 0x7FFF0ADE; // AGS_DX12_SHADER_INSTRINSICS_SPACE_ID

    /// <summary>A shader of AMD's AGS permutation (a UAV in <see cref="AgsSpace"/>), off AMD: the game never uses it there.</summary>
    bool VendorOnly(string h) => !caps.Profile.StartsWith("amd") && bc.TryGetValue(h, out var s) && s.Bindings.Any(b => b.Space == AgsSpace);

    /// <summary>The platform in use and, on AMD (32 and 64 lanes), its "&lt;platform&gt; wave&lt;min&gt;[-&lt;max&gt;]" platforms
    /// (the readers' [WaveSize] split) whose range takes 64; NVIDIA runs 32 lanes only.</summary>
    bool OnPlatform(string mapPlat)
    {
        if (mapPlat == plat) return true;
        if (!caps.Profile.StartsWith("amd") || !mapPlat.StartsWith(plat + " wave")) return false;
        var r = mapPlat[(plat.Length + 5)..].Split('-');
        return uint.TryParse(r[0], out var min) && uint.TryParse(r[^1], out var max) && min <= 64 && max >= 64;
    }

    /// <summary>Every stage set of the library on the platform in use, in a fixed order (may repeat one: shaders shared
    /// across maps): every compute shader; every VS/MS alone and VS->GS (depth passes); AS->MS(->PS); every VS->PS, MS->PS, VS->GS->PS and VS->HS->DS
    /// (->GS) (->PS) chain inside a shader map (Global: pooled) whose signatures link; a pipeline the game ships as is; global VS->GS chains feeding
    /// any material PS; global VSs feeding a material PS nothing in its own map feeds; pairs across maps with a default
    /// material's shader (<see cref="SharedAcrossMaps"/>). <paramref name="sink"/> gets (stages, shape, the PS's output signature or "").</summary>
    void StageSets(Action<SortedDictionary<int, string>, string, string> sink)
    {
        // AMD's AGS intrinsics (a UAV in AGS_DX12_SHADER_INSTRINSICS_SPACE_ID): an AMD-only permutation, and no UE 4 root
        // signature has that slot (E_INVALIDARG): left out on other vendors, counted once per shader ("vendor_extension")
        var ags = new HashSet<string>();
        var unpairedShaders = new HashSet<string>();
        bool Usable(string h)
        {
            if (!bc.TryGetValue(h, out var s)) return false;
            if (VendorOnly(h))
            {
                ags.Add(h);
                return false;
            }
            if (learnedParts == null || s.Stage == Stage.Library || learnedParts.Contains(Part(s))) return true; // libraries: not raster stage sets
            unpairedShaders.Add(h);
            return false;
        }
        var fed = new HashSet<string>();
        var unfed = new List<ShaderInfo>();
        foreach (var (mapPlat, pooled, shas, isPipeline) in maps)
        {
            ct.ThrowIfCancellationRequested();
            if (!OnPlatform(mapPlat)) continue;
            var all = shas.Where(bc.ContainsKey).ToList();
            var ds = all.Where(Usable).Select(h => bc[h]).ToList();
            if (isPipeline && ds.Count < all.Count) continue; // a shipped pipeline stays whole or not at all
            if (isPipeline) // a pipeline the game ships (e.g. a PSO cache record): exactly its stages
            {
                var st = new SortedDictionary<int, string>();
                foreach (var d in ds) st[(int)d.Stage] = d.Sha1;
                if (st.Count > 0 && Planner.Positioned(st.ToDictionary(x => (Stage)x.Key, x => bc[x.Value]))) sink(st, Planner.Shape(st), PsOut(st));
                else if (st.Count > 0 && seen.Add(Tuple("", st)))   // a stage set like any other, counted once
                    Count(RootSigOf(st) is { } rs && have.Contains(Tuple(rs, st)) ? "already_recorded" : unbuildable ? NoRs : "stream_output");
                continue;
            }
            var srcs = new Dictionary<string, List<ShaderInfo>>();
            var gss = new Dictionary<string, List<ShaderInfo>>(); // geometry shaders by output signature, fed by a VS with matching outputs
            foreach (var d in ds)
            {
                if (d.Stage == Stage.Compute) sink(new() { [(int)Stage.Compute] = d.Sha1 }, Planner.Shape(Stage.Compute), "");
                if (d.Stage is Stage.Vertex or Stage.Mesh)
                {
                    if (d.Stage == Stage.Vertex) Planner.Push(srcs, Planner.Sig(d.Outputs), d);
                    // depth passes run it without a pixel shader (not only position-only ones); a VS without SV_Position only
                    // feeds a GS (the runtime rejects it alone: E_INVALIDARG)
                    if (Planner.Rasterizable(d)) sink(new() { [(int)d.Stage] = d.Sha1 }, Planner.Shape(d.Stage), "");
                }
                if (d.Stage == Stage.Geometry) Planner.Push(gss, Planner.Sig(d.Outputs), d);
            }
            var mss = ds.Where(d => d.Stage == Stage.Mesh).ToList();
            var byPart = new Dictionary<string, ILookup<string, (int At, ShaderInfo Vs)>>();
            // the VSs of a PS's input signature, in map order; a PS that loses one to the lookup is left out once (whether the
            // lost VS links isn't checked: that is the cross product)
            List<ShaderInfo> Feeding(string pin, ShaderInfo p)
            {
                var all = srcs.GetValueOrDefault(pin, []);
                if (learnedParts == null) return all;
                if (!byPart.TryGetValue(pin, out var l)) byPart[pin] = l = all.Select((v, i) => (i, v)).ToLookup(x => Part(x.v));
                var kept = learnedVs.TryGetValue(Part(p), out var parts) ? parts.SelectMany(v => l[v]).OrderBy(x => x.At).Select(x => x.Vs).ToList() : [];
                if (kept.Count < all.Count) unpairedShaders.Add(p.Sha1);
                return kept;
            }
            foreach (var p in ds.Where(d => d.Stage == Stage.Pixel))
            {
                var pin = Planner.Sig(p.Inputs);
                var n = 0;
                foreach (var s in Feeding(pin, p).Where(s => Planner.Rasterizable(s) && Planner.Links(s, p) && Planner.SameRs(s, p))
                    .Concat(mss.Where(m => Planner.Rasterizable(m) && Planner.MeshFeeds(m, p) && Planner.SameRs(m, p))))
                {
                    n++;
                    sink(new() { [(int)s.Stage] = s.Sha1, [(int)Stage.Pixel] = p.Sha1 }, Planner.Shape(s.Stage, Stage.Pixel), Planner.Sig(p.Outputs, false));
                }
                foreach (var g in gss.GetValueOrDefault(pin, []).Where(g => Planner.Links(g, p)))
                    foreach (var v in srcs.GetValueOrDefault(Planner.Sig(g.Inputs), []).Where(v => v.Stage == Stage.Vertex && Planner.Links(v, g) && Planner.SameRs(v, g, p)))
                    {
                        n++;
                        sink(new() { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Geometry] = g.Sha1, [(int)Stage.Pixel] = p.Sha1 },
                            Planner.Shape(Stage.Vertex, Stage.Pixel, Stage.Geometry), Planner.Sig(p.Outputs, false));
                    }
                if (n > 0) fed.Add(p.Sha1);
                else if (!pooled) unfed.Add(p);
            }
            // VS -> GS with no PS (depth passes: UE's one-pass point-light shadows draw opaque casters into a cube map's
            // depth through a GS, no PS): every GS writing SV_Position behind every VS of its map that feeds it
            foreach (var g in ds.Where(d => d.Stage == Stage.Geometry && Planner.Rasterizable(d)))
                foreach (var v in srcs.GetValueOrDefault(Planner.Sig(g.Inputs), []).Where(v => v.Stage == Stage.Vertex && Planner.Links(v, g) && Planner.SameRs(v, g)))
                    sink(new() { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Geometry] = g.Sha1 }, Planner.Shape(Stage.Vertex, Stage.Geometry), "");
            // AS -> MS (-> PS): an amplification shader launches mesh shader groups of its map; alone (depth) and with every
            // PS the MS links to. ponytail: the AS payload size must equal the MS's (PSV0), which ShaderInfo doesn't carry
            // (a ShaderInfo.PayloadBytes field is proposed); no installed game ships an AS (0 of 27 indexes), so a mismatched
            // pair would only show up as a failed item
            foreach (var a in ds.Where(d => d.Stage == Stage.Amplification))
                foreach (var m in mss.Where(m => Planner.SameRs(a, m)))
                {
                    if (Planner.Rasterizable(m)) sink(new() { [(int)Stage.Amplification] = a.Sha1, [(int)Stage.Mesh] = m.Sha1 }, Planner.Shape(Stage.Amplification, Stage.Mesh), "");
                    foreach (var p in ds.Where(d => d.Stage == Stage.Pixel && Planner.Rasterizable(m) && Planner.MeshFeeds(m, d) && Planner.SameRs(a, m, d)))
                        sink(new() { [(int)Stage.Amplification] = a.Sha1, [(int)Stage.Mesh] = m.Sha1, [(int)Stage.Pixel] = p.Sha1 },
                            Planner.Shape(Stage.Amplification, Stage.Mesh, Stage.Pixel), Planner.Sig(p.Outputs, false));
                }
            // tessellation: VS -> HS -> DS (-> GS), alone (depth passes) and -> PS, linked like the other stages (the VS feeding
            // a HS writes no SV_Position; a DS without it feeds a GS taking triangles: UE's one-pass cube shadows)
            var pixel = ds.Where(d => d.Stage == Stage.Pixel).ToLookup(d => Planner.Sig(d.Inputs));
            var gsIn = ds.Where(d => d.Stage == Stage.Geometry && Planner.TopologyType(d.GsInputPrimitive) == 3).ToLookup(d => Planner.Sig(d.Inputs));
            foreach (var (h, d) in Planner.TessPairs(ds))
            {
                var tails = new List<(ShaderInfo? Gs, ShaderInfo? Ps)>(); // after the DS: nothing or a GS (depth passes), then a PS
                foreach (var g in (Planner.Rasterizable(d) ? [null] : Array.Empty<ShaderInfo?>()).Concat(gsIn[Planner.Sig(d.Outputs)].Where(g => Planner.Links(d, g) && Planner.Rasterizable(g))))
                {
                    var last = g ?? d;
                    tails.Add((g, null));
                    tails.AddRange(pixel[Planner.Sig(last.Outputs)].Where(p => Planner.Links(last, p)).Select(p => (g, (ShaderInfo?)p)));
                }
                foreach (var v in srcs.GetValueOrDefault(Planner.Sig(h.Inputs), []).Where(v => v.Stage == Stage.Vertex && Planner.Links(v, h) && Planner.SameRs(v, h, d)))
                    foreach (var (g, p) in tails.Where(t => Planner.SameRs([v, h, d, .. new[] { t.Gs, t.Ps }.OfType<ShaderInfo>()])))
                    {
                        var st = new SortedDictionary<int, string> { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Hull] = h.Sha1, [(int)Stage.Domain] = d.Sha1 };
                        if (g != null) st[(int)Stage.Geometry] = g.Sha1;
                        if (p != null) st[(int)Stage.Pixel] = p.Sha1;
                        sink(st, Planner.Shape(st), p != null ? Planner.Sig(p.Outputs, false) : "");
                    }
            }
        }

        // global VS->GS chains also feed material pixel shaders (e.g. rendering into volume textures): pair across libraries
        var gpool = maps.Where(m => m.Pooled && OnPlatform(m.Platform)).SelectMany(m => m.Shas).Where(Usable).Select(h => bc[h]).ToList();
        var psByIn = new Dictionary<string, List<ShaderInfo>>();
        foreach (var (mapPlat, pooled, shas, _) in maps)
            if (!pooled && OnPlatform(mapPlat))
                foreach (var h in shas.Where(h => Usable(h) && bc[h].Stage == Stage.Pixel)) Planner.Push(psByIn, Planner.Sig(bc[h].Inputs), bc[h]);
        if (ags.Count > 0) stats["vendor_extension"] = ags.Count;
        foreach (var g in gpool.Where(d => d.Stage == Stage.Geometry))
        {
            var vss = gpool.Where(v => v.Stage == Stage.Vertex && Planner.Sig(v.Outputs) == Planner.Sig(g.Inputs) && Planner.Links(v, g)).ToList();
            foreach (var p in psByIn.GetValueOrDefault(Planner.Sig(g.Outputs), []).Where(p => Planner.Links(g, p)))
                foreach (var v in vss.Where(v => Planner.SameRs(v, g, p)))
                    sink(new() { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Geometry] = g.Sha1, [(int)Stage.Pixel] = p.Sha1 },
                        Planner.Shape(Stage.Vertex, Stage.Pixel, Stage.Geometry), Planner.Sig(p.Outputs, false));
        }

        // a material PS nothing in its own map feeds is drawn behind a global VS (UE 5.1 Nanite material passes)
        var gvs = gpool.Where(d => d.Stage == Stage.Vertex && Planner.Rasterizable(d)).ToLookup(v => Planner.Sig(v.Outputs));
        var orphans = new List<ShaderInfo>();
        foreach (var p in unfed.Where(p => !fed.Contains(p.Sha1)).DistinctBy(p => p.Sha1))
        {
            var n = 0;
            foreach (var v in gvs[Planner.Sig(p.Inputs)].Where(v => Planner.Links(v, p) && Planner.SameRs(v, p)))
            {
                n++;
                sink(new() { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Pixel] = p.Sha1 }, Planner.Shape(Stage.Vertex, Stage.Pixel), Planner.Sig(p.Outputs, false));
            }
            if (n == 0) orphans.Add(p);
        }

        // a per-stage cache compiles the PS on its own, keyed with its root signature: a PS still unpaired that carries its own
        // (so no VS changes it) gets the first VS of any map that feeds it. Elden Ring draws gxflvershader's pixel shaders
        // behind material VSs of its shaderbdle bundles
        orphans.RemoveAll(p => p.RootSignature == null);
        if (caps.PerStageCache && orphans.Count > 0)
        {
            var vss = maps.Where(m => !m.Pooled && OnPlatform(m.Platform)).SelectMany(m => m.Shas).Distinct()
                .Where(h => Usable(h) && bc[h].Stage == Stage.Vertex && Planner.Rasterizable(bc[h])).Select(h => bc[h]).ToLookup(v => Planner.Sig(v.Outputs));
            foreach (var p in orphans)
                if (vss[Planner.Sig(p.Inputs)].FirstOrDefault(v => Planner.Links(v, p) && Planner.SameRs(v, p)) is { } v)
                {
                    Count("orphan_ps_paired");
                    sink(new() { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Pixel] = p.Sha1 }, Planner.Shape(Stage.Vertex, Stage.Pixel), Planner.Sig(p.Outputs, false));
                }
        }

        if (engine.Family == "Unreal" && engine.Version.StartsWith("5.")) SharedAcrossMaps(sink, Usable);
        // once per shader, as a stage set left out (the plan's StageSets and LeftOut): its stage sets aren't enumerated
        if ((unpaired = unpairedShaders.Count) > 0) stats["no_rs"] = stats.GetValueOrDefault("no_rs") + unpaired;
    }

    /// <summary>A shader in at least 1 of every <see cref="SharedMaps"/> material maps doesn't depend on the material (a default
    /// material's), and UE draws it with another material's stages: Nanite rasterizes a bin with the mesh shader of its vertex
    /// material (world position offset) and the pixel shader of its pixel material (masked, pixel depth offset), either one
    /// the default's; UE 5.1 draws material VSs with a few PSs shared by a third of the maps. So every MS with every PS it
    /// feeds (<see cref="Planner.MeshFeeds"/>) where either one is shared, and every VS with every shared PS it links to,
    /// across maps (Unreal 5). Recorded cross-map pairs (Oblivion Remastered, SILENT HILL: Townfall): the shared side in
    /// 2-35% of the maps, the other in at most 8. Unreal 4 recordings (Hogwarts Legacy, Tiny Tina's Wonderlands) have none
    /// the VS rule would cover, for 3-4% more PSOs.</summary>
    void SharedAcrossMaps(Action<SortedDictionary<int, string>, string, string> sink, Func<string, bool> usable)
    {
        var own = maps.Where(m => !m.Pooled && !m.IsPipeline && OnPlatform(m.Platform)).ToList();
        var inMaps = own.SelectMany(m => m.Shas.Distinct()).CountBy(h => h).ToDictionary();
        bool Shared(ShaderInfo s) => inMaps[s.Sha1] > 1 && inMaps[s.Sha1] * SharedMaps >= own.Count;
        var shaders = inMaps.Keys.Where(usable).Select(h => bc[h]).ToList();
        var pixel = shaders.Where(s => s.Stage == Stage.Pixel).ToList();
        var byIn = pixel.ToLookup(p => Planner.Sig(p.Inputs));
        var sharedIn = pixel.Where(Shared).ToLookup(p => Planner.Sig(p.Inputs));
        foreach (var v in shaders.Where(s => s.Stage == Stage.Vertex && Planner.Rasterizable(s)))
            foreach (var p in sharedIn[Planner.Sig(v.Outputs)].Where(p => Planner.Links(v, p) && Planner.SameRs(v, p)))
                sink(new() { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Pixel] = p.Sha1 }, Planner.Shape(Stage.Vertex, Stage.Pixel), Planner.Sig(p.Outputs, false));
        static (string, int) Key(SigElement e) => (e.Semantic.ToUpperInvariant(), e.Index);
        foreach (var g in shaders.Where(s => s.Stage == Stage.Mesh && Planner.Rasterizable(s)).GroupBy(m => string.Join(';', m.Outputs.Where(Planner.PerPrimitive).Select(Key).Order())))
        {
            // a PS reading per-primitive outputs, by its other inputs
            var prim = g.First().Outputs.Where(Planner.PerPrimitive).Select(Key).ToHashSet();
            var reading = pixel.Where(p => p.Inputs.Any(i => prim.Contains(Key(i)))).ToLookup(p => Planner.Sig(p.Inputs.Where(i => !prim.Contains(Key(i))).ToList()));
            foreach (var m in g)
                foreach (var p in byIn[Planner.Sig(m.Outputs)].Concat(reading[Planner.Sig(m.Outputs)]).Distinct().Where(p => (Shared(m) || Shared(p)) && Planner.MeshFeeds(m, p) && Planner.SameRs(m, p)))
                    sink(new() { [(int)Stage.Mesh] = m.Sha1, [(int)Stage.Pixel] = p.Sha1 }, Planner.Shape(Stage.Mesh, Stage.Pixel), Planner.Sig(p.Outputs, false));
        }
    }

    const int SharedMaps = 50;

    readonly List<byte[]> rtItems = [];

    /// <summary>Ray tracing collections for the library's DXIL libraries (<see cref="RtCollections"/>; only where the driver
    /// caches collections on their own, <see cref="Planner.RtCollectionCache"/>), one 'Y' record per library no recorded
    /// state object has. The rule comes from the recording's collections when it has some (checked: each one whose library
    /// is recorded is rebuilt byte for byte, names included), else (no recording, or one without state objects: ray tracing
    /// off as played) for Unreal 4.25 <see cref="RtCollections.Ue425Global"/> and no state object config, for Unreal 4.26/4.27 from UE 4.26's source as Jedi: Survivor confirms it, or Avalanche's 4.27 fork's
    /// when its libraries carry the fork's bindless marker (<see cref="RtCollections.GlobalFor"/>), and for Unreal 5.0-5.4
    /// <see cref="RtCollections.Ue51Global"/> (5.4: <see cref="RtCollections.Ue54Global"/>) when the libraries have 5.1's
    /// binding shape (<see cref="RtCollections.Ue5ShapeMismatch"/>; verified on 5.1 only); for Northlight its own global and local root
    /// signatures, the rest guessed. REDengine 3 and FromSoftware
    /// plan their material hit groups instead. A library declaring a resource neither root signature gives it is left out
    /// ("rt_uncovered").</summary>
    void RtPlan()
    {
        var libs = maps.Where(m => m.Platform == plat).SelectMany(m => m.Shas).Distinct().Where(h => bc.TryGetValue(h, out var s) && s.Stage == Stage.Library).ToList();
        if (libs.Count == 0) return;
        if (engine.NoRtPipelines)
        {
            log?.Report($"ray tracing: {libs.Count} DXIL libraries; the game's config turns ray tracing pipelines off (r.RayTracing or r.RayTracing.AllowPipeline): none synthesized");
            return;
        }
        if (this.rule == RootSig.Rule.Red3) { Red3HitGroups(libs.Count); return; }
        if (engine.Family == FromSoft.FromSoftReader.Family) { SoulsHitGroups(libs.Count); return; }
        var learned = stateObjects.Select(RtCollections.Read).OfType<RtCollections.Recorded>().ToList();
        RtCollections.Rule rule;
        string? guessedLocal = null; // Northlight's one local root signature (else UE's, per library)
        string how;
        if (learned.Count > 0)
        {
            static T Mode<T>(IEnumerable<T> v) => v.GroupBy(x => x).MaxBy(g => g.Count())!.Key;
            var config = Mode(learned.Select(c => (c.Payload, c.Attributes)));
            // the payload: each library's own when the recorded hit / miss collections have it (Hogwarts Legacy: 12, 24 and 64 bytes
            // side by side, each its library's; a ray generation shader has none and is compiled once per pipeline payload), else
            // the most recorded one (Jedi: Survivor's 32)
            var own = learned.Where(c => recBlobs.ContainsKey(c.Library)).Select(c => (c.Payload, Own: RtCollections.OwnPayload(recBlobs[c.Library]))).Where(p => p.Own > 0).ToList();
            var payload = own.Count > 0 && own.Count(p => p.Payload == p.Own) >= 0.99 * own.Count ? 0 : config.Payload;
            rule = new(Mode(learned.Select(c => c.Global)), Mode(learned.Select(c => c.Flags)), Mode(learned.Select(c => c.Depth)), payload, config.Attributes, false);
            var recordedKeys = stateObjects.Select(r => r.Key).ToHashSet();
            int n = 0, same = 0;
            foreach (var c in learned.Where(c => recBlobs.ContainsKey(c.Library) && bc.ContainsKey(c.Library)))
            {
                n++;
                if ((LocalRs(bc[c.Library], true), LocalRs(bc[c.Library], false)) is not ({ } localGen, { } localOther)) continue;
                var synth = RtCollections.Collection(recBlobs[c.Library], c.Library, rule.GlobalRs, localGen, localOther,
                    c.Payload, c.Attributes, rule.Depth, rule.Flags, c.NameHash);
                if (synth != null && recordedKeys.Contains(new Rec('R', synth).Key)) same++;
            }
            rule = rule with { Verified = n > 0 && same >= 0.99 * n };
            how = $"learned from {learned.Count} recorded collections, {same}/{n} rebuilt byte for byte";
            if (!rule.Verified) { log?.Report($"ray tracing: {libs.Count} DXIL libraries; collection rule {how}: not the engine's rule, none synthesized"); return; }
        }
        // a recording without state objects (ray tracing off as played) teaches nothing about them: the rules below, as without one
        else if (engine.Family == "Unreal" && engine.Version == "4.25")
        {
            var (h, b) = RtCollections.Serialize(RtCollections.Ue425Global, RootSig.StaticSamplers(RootSig.Rule.Ue425));
            rsBlobs[h] = b;
            // no state object config, depth 1, triangle barycentrics, the material payload (64); 2 of Returnal's 403 are only
            // in a 24-byte pipeline
            rule = new(h, RtCollections.NoConfig, 1, 64, 8, false);
            how = "UE 4.25's (Returnal's recording: 401 of its 403 collections rebuilt from its files, the other 2 at a 24-byte payload)";
        }
        else if (engine.Family == "Unreal" && engine.Version is "4.26" or "4.27" && RootSig.RuleFor(engine) != null)   // none: a fork with its own root signatures
        {
            var desc = RtCollections.GlobalFor(libs.Select(l => bc[l]));
            var (h, b) = RtCollections.Serialize(desc, RootSig.Ue426Samplers);
            rsBlobs[h] = b;
            var fork = desc != RtCollections.Ue426Global;
            // ALLOW_STATE_OBJECT_ADDITIONS, depth 1, triangle barycentrics; the payload: stock 4.26's packed material payload (32),
            // Avalanche's fork each library's own (its recording: 981/981 collections, the ray generation ones once per payload)
            rule = new(h, 4, 1, fork ? 0u : 32, 8, false);
            how = fork ? "Avalanche's UE 4.27 fork (its bindless spaces; Hogwarts Legacy's recording: 981/981 collections rebuilt from its files)"
                : "UE 4.26's (its source, as Jedi: Survivor's recording confirms it; unverified for this game)";
        }
        else if (engine.Family == "Unreal" && engine.Version is "5.0" or "5.1" or "5.2" or "5.3" or "5.4")
        {
            if (RtCollections.Ue5ShapeMismatch([.. libs.Select(l => bc[l])]) is { } why)
            {
                log?.Report($"ray tracing: {libs.Count} DXIL libraries; not stock UE 5.0-5.4's binding shape ({why}): no collection rule without a recording, none synthesized");
                return;
            }
            var (h, b) = RtCollections.Serialize(engine.Version == "5.4" ? RtCollections.Ue54Global : RtCollections.Ue51Global, RootSig.Ue426Samplers);
            rsBlobs[h] = b;
            rule = new(h, 4, 1, 0, 8, false); // payload 0: each library's own
            how = engine.Version == "5.1"
                ? $"UE 5.1's (Oblivion Remastered's recording: 589/589 collections rebuilt from its files{(engine.Fork != null ? "; unverified for this fork" : "")})"
                : $"UE 5.1's{(engine.Version == "5.4" ? " with 5.4's 32 samplers" : "")}, for the libraries' 5.1 binding shape; unverified for {engine.Version}";
        }
        else if (this.rule == RootSig.Rule.Northlight)
        {
            var (h, b) = RtCollections.Serialize(RootSig.NorthlightCompute, []);
            rsBlobs[h] = b;
            (guessedLocal, var lb) = RtCollections.Serialize(RtCollections.NorthlightLocal, []);
            rsBlobs[guessedLocal] = lb;
            rule = new(h, 0, 1, 0, 0, false); // payload and attributes: each library's own
            how = "Northlight's global and local root signatures as its renderer builds them, one collection per library; depth 1 and no state object flags guessed: unverified in game";
        }
        else { log?.Report($"ray tracing: {libs.Count} DXIL libraries; no collection rule for {engine.Family} {engine.Version} without a recording: none synthesized"); return; }

        // NVIDIA keys a collection on the NVAPI shader-extension slot too (selftest nvext): only a recording shows whether the
        // game sets one around its creates
        var nv = RtCollections.LearnedNv(stateObjects.Where(r => RtCollections.Read(r) != null).ToList(), nvRecs);
        var nvHow = nv is { } x ? $"NVAPI extension slot u{x.Slot} space {x.Space}{(x.Options != 0 ? $", creation flags 0x{x.Options:x}" : "")}, as the recorded collections"
            : learned.Count > 0 ? "no NVAPI state recorded with the recorded collections"
            : "NVAPI state unknown without a recording of ray tracing (a game setting NVIDIA's shader-extension slot misses them all)";
        var recordedLibs = stateObjects.SelectMany(r => ParseStateObject(r).Libraries).ToHashSet();
        var global =rsBlobs.TryGetValue(rule.GlobalRs, out var gb) || recBlobs.TryGetValue(rule.GlobalRs, out gb) ? RootSig.Parse(gb) : null;
        foreach (var h in libs)
        {
            if (recordedLibs.Contains(h)) { Count("rt_recorded"); continue; }
            if ((guessedLocal != null ? (guessedLocal, guessedLocal) : (LocalRs(bc[h], true), LocalRs(bc[h], false))) is not ({ } lg, { } lo))
            {
                Count("rt_unserializable");
                rtUnbuildable.Add(h);
                continue;
            }
            if (global != null && RootSig.Uncovered(new RootSig.Ranges(0, [.. global.Slots, .. RootSig.Parse(rsBlobs[lo]).Slots]), Stage.Library, bc[h]) is { } why)
            {
                Count("rt_uncovered");
                uncoveredExample.TryAdd("ray tracing " + why, h);
                continue;
            }
            rtItems.Add(RtCollections.Item(h, rule.GlobalRs, lg, lo, rule, nv));
            usedRs.UnionWith([rule.GlobalRs, lg, lo]);
        }
        log?.Report($"ray tracing: {libs.Count} DXIL libraries, {rtItems.Count} collections synthesized (rule {how}; payload {(rule.Payload == 0 ? "each library's own" : rule.Payload)}, attributes {rule.Attributes}, depth {rule.Depth}; {nvHow})"
            + $", {stats.GetValueOrDefault("rt_recorded")} already recorded, {stats.GetValueOrDefault("rt_uncovered")} uncovered");
    }

    readonly List<byte[]> hitGroupItems = [];
    readonly HashSet<string> rtUnbuildable = [];   // DXIL libraries whose root signature the runtime won't serialize

    /// <summary>REDengine 3: one collection per material hit group of the index ('H', <see cref="RedEngine.RedRayTracing"/>),
    /// in the shape of the recording's additions; none without a recorded addition (its global root signature is a version
    /// 1.0 blob only the recording has).</summary>
    void Red3HitGroups(int libraries)
    {
        if (RedEngine.RedRayTracing.Learn(stateObjects) is not { } shape || !recBlobs.TryGetValue(shape.Global, out var gb))
        {
            log?.Report($"ray tracing: {libraries} DXIL libraries; REDengine 3's material collections need a recording with ray tracing on: none synthesized");
            return;
        }
        var global = RootSig.Parse(gb);
        foreach (var m in index.Maps.Where(m => m.Library == RedEngine.RedEngineReader.HitGroups && m.Shaders.All(bc.ContainsKey)))
        {
            var group = m.Shaders.Select(h => bc[h]).ToList();
            if (RedEngine.RedRayTracing.Local(group, global) is not { } desc)
            {
                Count("rt_uncovered");
                uncoveredExample.TryAdd("ray tracing: no local root signature for the hit group (RedRayTracing.Local)", m.Shaders[0]);
                continue;
            }
            if (!rsCache.TryGetValue(desc.Key, out var local))
            {
                try { (local, var b) = RtCollections.Serialize(desc, []); rsBlobs[local] = b; }
                catch (RootSig.SerializeException e) { unserializable ??= e.Message; }
                rsCache[desc.Key] = local;
            }
            if (local == null) { Count("rt_unserializable"); rtUnbuildable.UnionWith(m.Shaders); continue; }
            var ranges = new RootSig.Ranges(0, [.. global.Slots, .. RootSig.Parse(rsBlobs[local]).Slots]);
            if (group.Select(l => RootSig.Uncovered(ranges, Stage.Library, l)).FirstOrDefault(w => w != null) is { } why)
            {
                Count("rt_uncovered");
                uncoveredExample.TryAdd("ray tracing " + why, m.Shaders[0]);
                continue;
            }
            hitGroupItems.Add(RedEngine.RedRayTracing.Item(m.Shaders[0], m.Shaders.ElementAtOrDefault(1), local, shape, rasterNv));
            usedRs.UnionWith([shape.Global, local]);
        }
        log?.Report($"ray tracing: {libraries} DXIL libraries, {hitGroupItems.Count} material hit group collections synthesized (the recorded additions' global root signature "
            + $"{shape.Global[..8]}, shader config ({shape.Payload}, {shape.Attributes}), pipeline config ({shape.Depth}, 0x{shape.PipelineFlags:x})), {stats.GetValueOrDefault("rt_uncovered")} uncovered");
    }

    /// <summary>Elden Ring: one collection per material's closest hit and any hit pair of one ray payload ('H',
    /// <see cref="FromSoft.SoulsRayTracing"/>). Ray generation and miss libraries are only linked into its pipelines, which
    /// it builds from whichever collections are loaded: not planned. No NVAPI state: its recording has none.</summary>
    void SoulsHitGroups(int libraries)
    {
        var (global, local) = (FromSoft.SoulsRayTracing.Global, FromSoft.SoulsRayTracing.Local);
        var ranges = new RootSig.Ranges(0, [.. RootSig.Parse(global.Blob).Slots, .. RootSig.Parse(local.Blob).Slots]);
        var recordedLibs = stateObjects.SelectMany(r => ParseStateObject(r).Libraries).ToHashSet();
        var pairs = new HashSet<string>();
        foreach (var m in index.Maps.Where(m => m.Platform == plat && FromSoft.SoulsRayTracing.IsPair(m) && m.Shaders.All(h => bc.TryGetValue(h, out var s) && s.Stage == Stage.Library)))
        {
            pairs.Add(string.Join('|', m.Shaders.Order()));
            if (m.Shaders.All(recordedLibs.Contains)) { Count("rt_recorded"); continue; }
            if (m.Shaders.Select(h => RootSig.Uncovered(ranges, Stage.Library, bc[h])).FirstOrDefault(w => w != null) is { } why)
            {
                Count("rt_uncovered");
                uncoveredExample.TryAdd("ray tracing " + why, m.Shaders[0]);
                continue;
            }
            hitGroupItems.Add(RedEngine.RedRayTracing.Item(m.Shaders[0], m.Shaders[1], local.Hash, FromSoft.SoulsRayTracing.Shape, null));
        }
        rsBlobs[global.Hash] = global.Blob;
        rsBlobs[local.Hash] = local.Blob;
        if (hitGroupItems.Count > 0) usedRs.UnionWith([global.Hash, local.Hash]);
        // the recorded collections (one state object type 0) this rule makes: the same pair and both root signatures
        var collections = stateObjects.Where(r => r.Tag == 'R').Select(ParseStateObject).Where(s => s.Type == 0).ToList();
        var same = collections.Count(s => pairs.Contains(string.Join('|', s.Libraries.Order())) && s.RootSignatures.Order().SequenceEqual(new[] { global.Hash, local.Hash }.Order()));
        log?.Report($"ray tracing: {libraries} DXIL libraries, {hitGroupItems.Count} material collections synthesized (Elden Ring's closest + any hit pairs, "
            + $"global root signature {global.Hash[..8]}, local {local.Hash[..8]}{(collections.Count > 0 ? $"; {same}/{collections.Count} recorded collections are these" : "")}), "
            + $"{stats.GetValueOrDefault("rt_recorded")} already recorded, {stats.GetValueOrDefault("rt_uncovered")} uncovered");
    }

    /// <summary>A library's local root signature (<see cref="RtCollections.LocalRs"/>), serialized once per shape; null when
    /// the runtime won't serialize it (that library is left out).</summary>
    string? LocalRs(ShaderInfo lib, bool rayGen)
    {
        // UE 5 hit groups: 6 system root constants (HitGroupSystemRootConstants is 24 bytes in 5.1's and 5.4's libraries)
        var d = RtCollections.LocalRs(lib.Counts, rayGen, lib.Bindings, engine.Family == "Unreal" && engine.Version.StartsWith("5.") ? 6u : 4u);
        if (!rsCache.TryGetValue(d.Key, out var h))
        {
            try
            {
                (h, var b) = RtCollections.Serialize(d, []);
                rsBlobs[h] = b;
            }
            catch (RootSig.SerializeException e) { unserializable ??= e.Message; }
            rsCache[d.Key] = h;
        }
        return h;
    }

    /// <summary>The synthesized PSOs and items again with each other texture filtering setting the recording has
    /// (<see cref="SamplerVariants"/>): the root signatures the rule built, serialized with that setting's samplers. A
    /// recording of this PC's left the community's other settings out (ScsKiller.ForeignVariants), so these are the
    /// settings of a PC without one, where its own is unknown; or two of its own. A copy the recording has is left out.</summary>
    (List<Rec> Synthesized, List<byte[]> Items) SettingsVariants()
    {
        if (!build || samplers is not { Length: > 0 } primary) return ([], []);
        var others = SamplerVariants.OtherSettings(new HashSet<string> { Convert.ToHexStringLower(primary) }, settings.Values).Select(h => samplerSets[h]).ToList();
        if (others.Count == 0) return ([], []);
        List<Rec> synth = [];
        List<byte[]> its = [];
        byte[]? Swap(byte[] payload, string rs, Dictionary<string, string?> to, SortedDictionary<int, string> stages)
        {
            if (!to.TryGetValue(rs, out var other) || other == null || have.Contains(Tuple(other, stages))) return null;
            var at = payload.AsSpan().IndexOf(Convert.FromHexString(rs));
            if (at < 0) return null;
            var copy = payload.ToArray();
            Convert.FromHexString(other).CopyTo(copy, at);
            return copy;
        }
        foreach (var v in others)
        {
            var to = new Dictionary<string, string?>();
            foreach (var (h, desc) in rsDescs.Where(d => usedRs.Contains(d.Key)))
                try
                {
                    var b = RootSig.Serialize(desc, v);
                    to[h] = Hex(SHA1.HashData(b));
                    rsBlobs[to[h]!] = b;
                }
                catch (RootSig.SerializeException) { to[h] = null; }   // this setting's samplers overlap the rule's ranges: not at it
            foreach (var r in synthesized)
                if (Parse(r) is var p && Swap(r.Payload, p.Rs, to, p.Stages) is { } c) { synth.Add(new Rec(r.Tag, c)); usedRs.Add(to[p.Rs]!); }
            foreach (var i in items)
                if (ParseItem(i) is var p && Swap(i, p.Rs, to, p.Stages) is { } c) { its.Add(c); usedRs.Add(to[p.Rs]!); }
        }
        log?.Report($"texture filtering: {others.Count + 1} settings in the recording, {synth.Count + its.Count} PSOs again at the other {(others.Count == 1 ? "one" : "ones")}");
        return (synth, its);
    }

    /// <summary>A recorded PSO the warm replays on this GPU: every blob it names is in the recording or the install, and no
    /// shader is another vendor's permutation (<see cref="VendorOnly"/>). The others aren't counted as the plan's pipelines.</summary>
    bool Replays(Rec r) => Rehydrate.References([r]).All(h => h == Zero || recBlobs.ContainsKey(h) || bc.ContainsKey(h)) && !Parse(r).Stages.Values.Any(VendorOnly);

    /// <summary>The recorded state objects the warm replays (<see cref="Planner.Materialize"/>'s rule): every blob each names in
    /// the recording or the install, and every record it builds on replayable too (a record follows the ones it builds on).</summary>
    List<Rec> ReplayableStateObjects()
    {
        var kept = new HashSet<string>();
        foreach (var r in stateObjects)
            if (Rehydrate.References([r]).All(h => h == Zero || recBlobs.ContainsKey(h) || bc.ContainsKey(h)) && ParseStateObject(r).Depends.All(kept.Contains))
                kept.Add(r.Key);
        return stateObjects.Where(r => kept.Contains(r.Key)).ToList();
    }

    /// <summary>The plan file: root signatures, templates, items, D3D11 shaders (proxy db records, no shader bytes).</summary>
    Plan Write()
    {
        var (synthCopies, itemCopies) = SettingsVariants();
        var body = new List<Rec>();
        foreach (var t in usedTemplates) usedRs.Add(Parse(recByKey[t]).Rs);
        foreach (var h in usedRs)
            if (rsBlobs.TryGetValue(h, out var b) || recBlobs.TryGetValue(h, out b)) body.Add(new Rec('B', [.. Convert.FromHexString(h), .. b]));
        foreach (var (h, b) in packRs) if (!usedRs.Contains(h)) body.Add(new Rec('B', [.. Convert.FromHexString(h), .. b])); // RTS0-only (packs keep no other blobs)
        body.AddRange(usedTemplates.Select(t => recByKey[t]));
        body.AddRange(synthesized.Concat(synthCopies));
        body.AddRange(items.Concat(itemCopies).Select(i => new Rec('P', i)));
        if (rasterNv is { } nv)
            body.AddRange(synthesized.Concat(synthCopies).Concat(items.Concat(itemCopies).Select(i => new Rec('P', i))).Select(r => new NvState(r.Key, nv.Slot, nv.Space, 1, nv.Options).ToRec()));
        body.AddRange(rtItems.Select(i => new Rec('Y', i)));
        body.AddRange(hitGroupItems.Select(i => new Rec('H', i)));
        body.AddRange(d3d11.Select(h => new Rec('1', D3D11Item(bc[h].Stage, h))));
        body.AddRange(tess11.Select(p => new Rec('2', D3D11Pair(p.Hs, p.Ds))));
        body.AddRange(packEntries);
        var n11 = d3d11.Count + tess11.Count;
        // ray tracing libraries: every DXIL library but lib_6_8 (work graphs: Reunion's and Windrose's one each).
        // ponytail: a DXR library built for SM 6.8 would be missed; read the RDAT function kinds into the index when one shows up
        var rtLibSet = dx12 ? maps.Where(m => m.Platform == plat).SelectMany(m => m.Shas).Where(h => bc.TryGetValue(h, out var s) && s.Stage == Stage.Library && s.ShaderModel != "lib_6_8").ToHashSet() : [];
        var rtLibs = rtLibSet.Count;
        // what the plan compiles of them: the libraries of its synthesized collections and of the recorded state objects that replay
        var replayable = ReplayableStateObjects();
        var replays = recs.Where(Replays).ToList();
        var pipelines = replays.Select(r => SamplerVariants.Pipeline(r, h => recBlobs.GetValueOrDefault(h)) ?? r.Key).Distinct().Count();   // one per pipeline, at any setting
        var rtCovered = rtItems.Select(i => RtCollections.ParseItem(i).Library)
            .Concat(hitGroupItems.Select(RedEngine.RedRayTracing.ParseItem).SelectMany(h => new[] { h.ClosestHit, h.AnyHit }).OfType<string>())
            .Concat(replayable.SelectMany(r => ParseStateObject(r).Libraries)).Count(rtLibSet.Remove);
        var rtUnbuilt = rtLibSet.Count(rtUnbuildable.Contains);   // never compiled anywhere: not ray tracing a recording would add
        // traced rays inline (RayQuery) and built no state object: the game's ray tracing is in its PSOs, a recording adds nothing
        var inlineOnly = rtLibs > 0 && stateObjects.Count == 0 && recBlobs.Values.Any(b => Carved.Dxbc.InlineRayTracing(b));
        if (inlineOnly) log?.Report($"ray tracing: the recording traces rays inline and builds no state object: the {rtLibs} DXIL libraries aren't used as played");
        // Unreal 5's hardware Lumen traces inline, compiled with the rest from the game files; its DXIL libraries serve passes
        // a game may never run (path tracing). Townfall's community recordings: 1,193 inline PSOs, no state object, 10,278 libraries
        var rtInline = engine.Family == "Unreal" && engine.Version.StartsWith('5') && rtLibs > 0
            ? maps.Where(m => m.Platform == plat).SelectMany(m => m.Shas).Distinct().Count(h => bc.TryGetValue(h, out var s) && s.InlineRayTracing) : 0;
        var plan = new Plan(game.Id, index.ContentHash, string.Join(" + ", new[] { plat, n11 > 0 ? $"D3D11 {plat11 ?? "DXBC"}" : "" }.Where(p => p != "")), caps.Profile,
            new PlanStats(pipelines + replayable.Count, items.Count + synthesized.Count + rtItems.Count + hitGroupItems.Count, synthesized.Count, usedRs.Count, dx12 && (verified || embeddedRs > 0),
                unitsBy[(int)Provenance.Exact], unitsBy[(int)Provenance.Inferred], unitsBy[(int)Provenance.Guessed], layoutCoverage, n11, packNew,
                stats.GetValueOrDefault("rs_uncovered"), rtLibs - rtUnbuilt, inlineOnly || engine.NoRtPipelines ? 0 : rtLibs - rtCovered - rtUnbuilt,
                StageSets: seen.Count + unpaired - stats.GetValueOrDefault("rs_unserializable"), LeftOut: new[] { "no_rs", "no_template", "no_gs_template", "rs_uncovered", "stream_output" }.Sum(stats.GetValueOrDefault),
                MiddlewareSharedItems: packShared, RtStateObjects: replayable.Count, RtInline: rtInline, Variants: synthCopies.Count + itemCopies.Count + replays.Count - pipelines),
            Path.Combine(outDir, "plan.bin"));
        PlanFile.Write(plan, body);
        log?.Report($"plan: {items.Count + synthesized.Count} PSOs{(rtItems.Count + hitGroupItems.Count > 0 ? $" + {rtItems.Count + hitGroupItems.Count} ray tracing collections" : "")} ({string.Join(", ", stats.Select(s => $"{s.Key} {s.Value}"))}), "
            + $"{synthesized.Count} synthesized templates, {usedRs.Count} root signatures, {n11} DirectX 11 items"
            + (packEntries.Count > 0 ? $", {packEntries.Count} middleware pack PSOs ({packNew} not in the recording)" : "")
            + (n11 > 0 ? $" ({string.Join(", ", d3d11.GroupBy(h => bc[h].Stage).Select(g => $"{g.Count()} {g.Key}").Append(tess11.Count > 0 ? $"{tess11.Count} HS+DS" : "").Where(s => s != ""))})" : "")
            + $", {new FileInfo(plan.FilePath).Length / 1024} KiB -> {plan.FilePath}");
        if (unserializable != null)
            log?.Report($"warning: {stats.GetValueOrDefault("rs_unserializable")} stage sets and {stats.GetValueOrDefault("rt_unserializable")} DXIL libraries left out: "
                + $"the runtime won't serialize the root signature the rule builds for them ({unserializable})");
        if (stats.TryGetValue("rs_uncovered", out var nu))
            log?.Report($"warning: {nu} stage sets left out: their root signature doesn't cover a resource their shaders declare "
                + $"({string.Join("; ", uncoveredExample.Take(5).Select(e => $"{e.Key}, e.g. {e.Value[..12]}"))}); this engine's rule doesn't know those ranges"
                + (recs.Count == 0 ? ": a recording teaches the planner the game's own root signatures" : ""));
        return plan;
    }

    bool MeshTier(SortedDictionary<int, string> stages) => platOfSha.GetValueOrDefault(stages.Values.First())?.Split(' ')[0] == "PCD3D_SM6"; // and its wave<N> platforms
    SortedDictionary<Stage, ShaderInfo> Infos(SortedDictionary<int, string> stages) => new(stages.ToDictionary(s => (Stage)s.Key, s => bc[s.Value]));
    string PsOut(SortedDictionary<int, string> stages) => stages.TryGetValue((int)Stage.Pixel, out var ps) ? Planner.Sig(bc[ps].Outputs, false) : "";
    (uint[], uint) Targets(SortedDictionary<int, string> stages) => stages.TryGetValue((int)Stage.Pixel, out var ps) ? Planner.Targets(bc[ps]) : ([], 0);
}
