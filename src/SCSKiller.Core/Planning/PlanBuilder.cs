using System.Security.Cryptography;
using SCSKiller.Core.Games;
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
    bool Nvidia => caps.Profile.StartsWith("nvidia");
    readonly string outDir;
    readonly IProgress<string>? log;
    readonly CancellationToken ct;
    readonly bool maximum;
    readonly bool rayTracing;
    readonly bool pairs;   // false: no stage sets or ray tracing items, only the recording's and the packs'
    readonly MiddlewarePacks? packs, sharedPacks;
    readonly PlanBudget budget;
    readonly long budgetPsos;

    readonly IReadOnlyDictionary<string, ShaderInfo> bc;
    /// <summary>Shader maps; Global shaders pair across maps (one fullscreen VS serves pixel shaders from many global maps): pooled.</summary>
    readonly List<(string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string? Rs)> maps;
    bool mapRoots;   // a map names its pipelines' root signature (ShaderMap.RootSignature)
    IReadOnlyList<ShaderMap> indexMaps;   // the index's maps, with a library's copy the recording picks (PickCopies)
    int passRoots;   // of those, the BlackSpace passes whose root the recording gave (LearnPassRoots)
    string? mapRs;            // ...the one StageSets is walking
    readonly Dictionary<string, string> platOfSha = [];
    /// <summary>The input layouts shipped pipelines are created with (<see cref="ShaderMap.Layout"/>), by <see cref="ShaderSet"/>.</summary>
    readonly Dictionary<string, List<List<LayoutElem>>> shippedLayouts = [];
    readonly RulesFile rules;
    readonly PlanRule rule;
    readonly uint maxSrvs; // the game's MAX_SRVS when its shaders need more than the rule's (0 = the rule's)
    bool nvExtension; // RootSig.NvExtensionEverywhere, confirmed by the recording's own root signatures or, without them, by a rule naming the game

    // the recording
    readonly List<Rec> recs = [];   // PSO records ('G' / 'C' / 'S'): what plans are built from
    readonly List<Rec> stateObjects = []; // recorded ray tracing state objects ('R' / 'A'): replayed as recorded; RtPlan learns from them
    readonly List<Rec> nvRecs = [];       // the recording's 'N' records: the NVAPI state RtPlan gives synthesized collections
    readonly HashSet<string> layered = [];  // what the driver got from a layer wrapping the device ('W'): a mod's, kept out of packs (they are shared)
    readonly RecordedBlobs recBlobs = new();
    readonly Dictionary<string, List<(string Key, string Rs, bool Layout)>> templates = [];
    readonly Dictionary<string, Rec> recByKey = [];
    readonly Dictionary<(uint Topology, string Shape), (string Key, string Rs, bool Layout)> gsTopo = []; // GS templates by input topology and stages (a stream template has only its own shaders' subobjects)
    readonly Dictionary<string, List<string>> rsByCounts = []; // recorded root signatures by resource counts, in recording order
    readonly HashSet<string> have = [];
    readonly Dictionary<string, int> plats = [];
    byte[]? samplers;
    readonly Dictionary<string, HashSet<string>> settings = [];   // a recorded root signature without its samplers' parameters -> the sampler sets it comes with
    readonly Dictionary<string, byte[]> samplerSets = [];          // ...each by its hex
    List<string>? keptSets;   // the settings the plan compiles (Recording.Filtering), its own first; null: every one
    int builtOk, builtN, ownOk, ownN; // own: recorded PSOs whose shaders carry a root signature, and those created with it

    // decided from the recording and the caps
    bool verified, build, synth, dx12;
    bool ruleFallback;   // a learned lookup the rule rebuilt enough of: the rule plans the stage sets it has no root signature for
    long fallbackSets;
    byte[]? ruleSamplers;   // the static samplers of the first recorded root signature the rule rebuilt
    string plat = "";
    int embeddedRs;
    string? plat11;
    List<string> d3d11 = [];
    readonly List<(string Hs, string Ds)> tess11 = [];
    readonly List<Draw11> draws11 = [];   // the recording's D3D11 draws ('D')
    List<Draw11> items11 = [];            // '3': D3D11 vertex stages with their layout (Draws11)

    readonly Dictionary<string, string?> rsCache = [];   // null: the runtime won't serialize it
    readonly Dictionary<string, byte[]> rsBlobs = [];
    readonly Dictionary<string, RootSig.Desc> rsDescs = [];   // the rule's root signatures, to serialize again with other samplers

    // the plan
    readonly List<byte[]> items = [];
    readonly List<int> itemRanks = [];   // each item's Rank, for WithinWarmBudget
    readonly List<Rec> synthesized = [];
    readonly Dictionary<string, string> synthByShape = [];
    readonly HashSet<string> usedTemplates = [];
    readonly HashSet<string> usedRs = [];
    readonly HashSet<string> seen = [];
    readonly SortedDictionary<string, int> stats = [];
    // per-stage plans (PerStage): where each new unit's state came from, and the index VSs whose layouts a recording gave
    readonly long[] unitsBy = new long[3];
    double layoutCoverage;
    long? cacheLeftOut;   // per-stage plans on a cache whose state is in the key: cover PSOs left out for CacheBudget; pairing plans under a warm budget: generated ones
    int extrasRoom = int.MaxValue;   // ...and the PSOs left under it after the cover, per filtering setting: Maximum's pairings fit there

    /// <summary>A step's count as it runs (<see cref="Planner.Steps"/>): what it counts, done, total (0 = not known yet).</summary>
    public Action<string, long, long>? Steps { get; init; }

    /// <param name="maximum">with a per-stage cache: also every stage set whose units the cover already has, on its first
    /// resolved state (every pre-linked pair, as a whole-pipeline plan has them); the units stay the same</param>
    public PlanBuilder(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir,
        IProgress<string>? log, CancellationToken ct, bool maximum = false, MiddlewarePacks? packs = null, MiddlewarePacks? sharedPacks = null, RulesFile? rules = null,
        PlanBudget? budget = null, long? cacheBudget = null, bool rayTracing = true, bool pairs = true)
    {
        this.pairs = pairs;
        (this.game, this.engine, this.index, this.recording, this.caps, this.outDir, this.log, this.ct, this.maximum, this.packs, this.sharedPacks, this.rayTracing) =
            (game, engine, index, recording, caps, outDir, log, ct, maximum, packs, sharedPacks, rayTracing);
        this.budget = budget ?? new PlanBudget(long.MaxValue, TimeSpan.MaxValue);
        budgetPsos = (cacheBudget ?? TestCacheBudget(caps) ?? CacheBudgetOf(caps)) / PsoBytesOf(caps);
        this.budget.Progress = () => $"{seen.Count:N0} stage sets";
        this.rules = rules ?? RulesFile.Current;
        bc = index.Shaders;
        indexMaps = index.Maps;
        maps = [];
        Plan(indexMaps);
        foreach (var m in index.Maps) foreach (var h in m.Shaders) platOfSha[h] = m.Platform;
        foreach (var m in index.OtherCopies ?? []) foreach (var h in m.Shaders) platOfSha.TryAdd(h, m.Platform);
        foreach (var m in index.Maps)
        {
            if (m.Layout is not { } layout || !ExactLayouts.ValidSlots(layout)) continue;
            var k = ShaderSet(m.Shaders);
            if (!shippedLayouts.TryGetValue(k, out var known)) shippedLayouts[k] = known = [];
            if (!known.Any(l => l.SequenceEqual(layout))) known.Add([.. layout]);
        }
        rule = this.rules.RuleFor(engine) ?? RulesFile.Embedded.Rules.Single(r => r.Id == "ue-ff7"); // no rule: FF7's is what a recording gets checked against
        maxSrvs = RootSig.MaxSrvsFor(rule, bc.Values);
        nvExtension = caps.Profile.StartsWith("nvidia") && RootSig.NvExtensionEverywhere(rule, bc.Values);
        viRule = this.rules.ViFor(engine)?.ViewInstancing;
    }

    public Plan Build()
    {
        using (recBlobs)
        {
            ReadRecording();
            PickCopies();
            Decide();
            if (dx12) RuntimeBuilt();
            if (!pairs) log?.Report("Only the recorded pipelines planned: no shader pairings or ray tracing");
            else
            {
                kept.UnionWith(recordedShaders);
                if (dx12 && UnitPolicy.For(caps) is { } policy) PerStage(policy);
                else
                {
                    StageSets(Emit);
                    if (crossLeftOut.Count > 0) stats["cross_product"] = crossLeftOut.Count;
                    if (caps.WarmCacheBudget != null) WithinWarmBudget();
                }
                if (stats.GetValueOrDefault("cross_product") is > 0 and var cross)
                    log?.Report($"{cross} stage sets left out from {crossPools.Count} pools whose signatures pair nearly every VS with every PS (largest {crossPools.Max().Pairs} pairs of "
                        + $"{crossPools.Max().Vs} VS and {crossPools.Max().Ps} PS), their shaders each in a planned or recorded PSO: only a recording says which the game draws");
                if (dx12 && !rayTracing) log?.Report("Ray tracing skipped (your setting): no state objects or collections planned");
                else if (dx12 && Planner.RtCollectionCache(caps))
                    try { RtPlan(); }
                    catch (RootSig.SerializeException e)   // the rule's global root signature: no collections, the PSOs still plan
                    {
                        rtItems.Clear();
                        log?.Report($"warning: ray tracing: no collections: the runtime won't serialize the rule's global root signature ({e.Message})");
                    }
            }
            if (Planner.SeedsPacks(engine) && (packs ?? sharedPacks) != null) SeedMiddleware();
            return Write();
        }
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
        // a game the user added, by hand, to Steam or to Lutris, never fills a pack: packs are shared, and its recording's origin is unknown
        if (recs.Count > 0 && packs != null && game.Store != Store.Manual && !SteamShortcuts.Is(game.Id) && !LutrisSource.Is(game.Id))
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

    void Plan(IReadOnlyList<ShaderMap> from)
    {
        maps.Clear();
        maps.AddRange(from.Select(m => (m.Platform, Pooled: false, Shas: m.Shaders, m.IsPipeline, Rs: m.RootSignature)));
        maps.AddRange(from.Where(m => m.Library == "Global").GroupBy(m => m.Platform)
            .Select(g => (g.Key, true, (IReadOnlyList<string>)g.SelectMany(m => m.Shaders).Distinct().ToList(), false, (string?)null)));
        mapRoots = maps.Any(m => m.Rs != null);
    }

    /// <summary>A library with copies at one path (<see cref="ShaderIndex.OtherCopies"/>) is planned from the copy that holds the
    /// most recorded pipelines whole (their stages in that library), the mount order's pick on a tie: the recording is what the
    /// game read (Marvel Rivals draws from an older patch's copy, not the newest one's). The other copies only name platforms.</summary>
    void PickCopies()
    {
        static string Archive(string copy) => copy[..copy.IndexOf('|')];   // a container path may hold '|' on Linux, an archive path doesn't
        if (index.OtherCopies is not { Count: > 0 } others || recs.Count == 0) return;
        var stageSets = recs.Select(r => Parse(r).Stages.Values.ToList()).Where(st => st.All(bc.ContainsKey)).ToList();
        var picked = new List<ShaderMap>(indexMaps);
        var changed = false;
        foreach (var lib in others.GroupBy(m => Archive(m.Copy!)))
        {
            var copies = new List<(string? Copy, List<ShaderMap> Maps)> { (null, indexMaps.Where(m => m.Copy != null && Archive(m.Copy) == lib.Key).ToList()) };
            copies.AddRange(lib.GroupBy(m => m.Copy).Select(g => (g.Key, g.ToList())));
            var sets = copies.Select(c => c.Maps.SelectMany(m => m.Shaders).ToHashSet()).ToList();
            var any = sets.SelectMany(x => x).ToHashSet();
            var whole = new int[copies.Count];
            foreach (var st in stageSets)
                if (st.Where(any.Contains).ToList() is { Count: > 0 } own)
                    for (var i = 0; i < copies.Count; i++)
                        if (own.All(sets[i].Contains)) whole[i]++;
            var best = Array.IndexOf(whole, whole.Max());
            if (best == 0) continue;
            picked.RemoveAll(m => m.Copy != null && Archive(m.Copy) == lib.Key);
            picked.AddRange(copies[best].Maps);
            changed = true;
            log?.Report($"{lib.Key}: planned from {copies[best].Copy![(lib.Key.Length + 1)..]}'s copy, {whole[best]:N0} recorded pipelines whole in it, {whole[0]:N0} in the copy the mount order picks");
        }
        if (!changed) return;
        indexMaps = picked;
        Plan(indexMaps);
    }

    /// <summary>Templates by shape, platform in use, and a check that root signatures rebuild from shader counts.</summary>
    void ReadRecording()
    {
        long read = 0;
        if (recording != null)
            foreach (var r in recBlobs.Read(recording.DbPath))   // every record but 'B', which recBlobs keeps
            {
                Steps?.Invoke("recording entries read", ++read, 0);
                if (r.Tag is 'G' or 'C' or 'S') recs.Add(r);
                else if (!rayTracing && Planner.LeftOutWithoutRt(r)) continue;
                else if (IsStateObject(r.Tag)) stateObjects.Add(r);
                else if (r.Tag == 'N') nvRecs.Add(r);
                else if (r.Tag == 'W' && r.Payload.Length == 40) layered.Add(Hex(r.Payload.AsSpan(0, 20)));
                else if (r.Tag == 'D')
                    try { draws11.Add(ParseDraw11(r.Payload)); }
                    catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException) { }   // a record from a newer proxy
            }
        // a root signature the readers can't follow (a shared recording's) goes with the records naming it, not the game
        var named = recs.Select(r => Parse(r).Rs).Concat(stateObjects.SelectMany(r => ParseStateObject(r).RootSignatures)).ToHashSet();
        var bad = named.Where(h => recBlobs.TryGetValue(h, out var b) && !Carved.Dxbc.RootSignatureValid(b)).ToHashSet();
        if (bad.Count > 0)
        {
            foreach (var h in bad) recBlobs.Remove(h);
            var left = recs.RemoveAll(r => bad.Contains(Parse(r).Rs)) + stateObjects.RemoveAll(r => ParseStateObject(r).RootSignatures.Any(bad.Contains));
            log?.Report($"warning: recorded: {bad.Count} malformed root signatures left out with the {left} records naming them");
        }
        // the game's own recorded root signatures decide: Returnal binds space 1001 in its ray tracing libraries only (0 of 9,652 raster ones carry it),
        // without them only a rule naming the game (Gears of War: E-Day) keeps it: the shader test alone also fires on games that bind it in ray tracing only
        if (nvExtension) nvExtension = recs.Where(r => !layered.Contains(r.Key) && Parse(r).Stages.Values.All(bc.ContainsKey)).Select(r => Parse(r).Rs).Distinct()
                .Select(h => recBlobs.TryGetValue(h, out var blob) ? blob : null).OfType<byte[]>().ToList() is { Count: > 0 } recorded
                ? 2 * recorded.Count(blob => RootSig.Parse(blob).Slots.Any(x => x is { Type: 1, Space: 1001 })) > recorded.Count
                : rule.RootSignature?.Stock?.NvExtensionEverywhere == true;
        var unbuilt = 0;
        long checkedRecs = 0;
        foreach (var r in recs)
        {
            Steps?.Invoke("recorded pipelines checked", ++checkedRecs, recs.Count);
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
                    // a GPU without mesh shaders gets no mesh and amplification deny flags, and a shared recording mixes both
                    // (Townfall's: 9,426 of 23,937 recorded root signatures)
                    if (RootSig.Serialize(RootSig.Build(rule, st, MeshTier(pso.Stages), maxSrvs, nvExtension, Nvidia), its).AsSpan().SequenceEqual(rsBlob)
                        || MeshTier(pso.Stages) && RootSig.Serialize(RootSig.Build(rule, st, false, maxSrvs, nvExtension, Nvidia), its).AsSpan().SequenceEqual(rsBlob))
                    {
                        builtOk++;
                        ruleSamplers ??= its;
                    }
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
            foreach (var h in pso.Stages.Values)
                if (recordedShaders.Add(h)) recordedFaces.Add(Face(bc[h]));
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
        var views = new Dictionary<string, Dictionary<string, int>>();
        foreach (var r in recs.Where(r => r.Tag is 'G' or 'S'))   // a 'G' has none
            if (ParseState(r) is { } st)
                foreach (var h in st.Stages.Values.Where(h => bc.TryGetValue(h, out var s) && s.ViewId))
                {
                    var k = st.ViewInstancing is { } v ? Hex(v) : "";
                    var n = views.TryGetValue(h, out var c) ? c : views[h] = [];
                    n[k] = n.GetValueOrDefault(k) + 1;
                }
        foreach (var (h, c) in views) recordedViews[h] = c.MaxBy(x => x.Value).Key is { Length: > 0 } k ? Convert.FromHexString(k) : null;
        void Add(string shape, (string, string, bool) t) { if (!templates.TryGetValue(shape, out var l)) templates[shape] = l = []; l.Add(t); }
    }

    readonly Dictionary<string, byte[]?> recordedViews = [];   // an SV_ViewID shader -> its recorded PSOs' most common view instancing (null: none)
    readonly ViRule? viRule;
    readonly Dictionary<string, byte[]?> ruleViews = [];   // an SV_ViewID shader the recording hasn't -> the rule's view instancing for its libraries
    readonly HashSet<string>[] viewIdShaders = [[], [], []];   // ...each planned: recorded, from the rule, unknown

    /// <summary>The view instancing a stage set is created with: that of its first shader reading SV_ViewID, as the
    /// recording has it, else as the rule has it for one of the shader's libraries; none without either (NVIDIA keys it:
    /// a guessed count would compile a miss).</summary>
    byte[]? ViewsOf(IReadOnlyDictionary<int, string> stages)
    {
        if (stages.Values.FirstOrDefault(h => bc[h].ViewId) is not { } h) return null;
        if (recordedViews.TryGetValue(h, out var v)) { viewIdShaders[0].Add(h); return v; }
        if (!ruleViews.TryGetValue(h, out v))
            ruleViews[h] = v = viRule != null && (viRule.Libraries == null || indexMaps.Any(m => viRule.Libraries.Contains(m.Library) && m.Shaders.Contains(h))) ? viRule.Bytes() : null;
        viewIdShaders[v != null ? 1 : 2].Add(h);
        return v;
    }

    readonly Dictionary<string, byte[]?> templateViews = [];
    byte[]? TemplateViews(string key) => templateViews.TryGetValue(key, out var v) ? v : templateViews[key] = ParseState(recByKey[key])?.ViewInstancing;
    static bool SameViews(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

    RtCollections.Nv? rasterNv;   // the NVAPI state every synthesized PSO is created with (an 'N' record each); null = none
    bool nvComputeOnly;   // rasterNv is the extension fallback's: it goes on compute PSOs only
    uint RtOptions => RtCollections.RecordedOptions(stateObjects.Concat(nvRecs));   // the recorded state objects' NVAPI creation options

    /// <summary>The NVAPI state a game creates its PSOs with: NVIDIA keys a compile on the shader-extension slot (selftest
    /// nvext), so a PSO warmed without the game's slot is a miss. From a recording: the slot and space at least 99% of its
    /// raster records ('G' / 'C' / 'S') share, with their most common creation options (The Witcher 3: 841 of 843 at slot
    /// 12 space 1, options 0 on 789 and 17 on 52). Otherwise, for REDengine 3, its slot 12 space 1, as its recording has it:
    /// also when the recording has none (made on AMD, where the recorder captures no NVAPI state). A game whose shaders bind
    /// the NVIDIA extension UAV without Unreal's flag (<see cref="RootSig.NvExtensionEverywhere"/>) gets u0 space 1001 the same
    /// way. NVIDIA only: AMD's runtime has no NVAPI state.</summary>
    internal static RtCollections.Nv? RasterNv(VendorCaps caps, string family, IReadOnlyCollection<Rec> recs, IEnumerable<Rec> nvRecs, bool nvExtension = false) =>
        !caps.Profile.StartsWith("nvidia") ? null : LearnedRasterNv(recs, nvRecs)
            ?? (family == RedEngine.RedEngineReader.Family ? new RtCollections.Nv(12, 1, 0) : nvExtension ? new RtCollections.Nv(0, 1001, 0) : null);

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
        (builtOk, builtN) = (builtOk + (recording?.OtherVendors ?? 0), builtN + (recording?.OtherVendors ?? 0));
        var fromRecording = builtN > 0;
        verified = fromRecording ? builtOk >= 0.99 * builtN : RootSig.Verified(rules.RuleFor(engine), engine);
        build = fromRecording ? verified : rules.RuleFor(engine) != null;
        ruleFallback = RuleFallback(builtOk, builtN) && rules.RuleFor(engine) != null;
        if (ruleFallback) samplers = ruleSamplers ?? samplers;   // the first recorded one may be a root signature the rule doesn't make
        if (!ruleFallback && recording?.Filtering is (var order, var keep) && samplers is { } first)   // the recorded primary's sampler family only
        {
            var family = SamplerVariants.OtherSettings(new HashSet<string> { Convert.ToHexStringLower(first) }, settings.Values);
            family.Add(Convert.ToHexStringLower(first));
            var mine = order.Where(s => family.Contains(s) && samplerSets.ContainsKey(s)).ToList();
            // the player's setting first, then the last recorded ones
            var current = engine.MaxAnisotropy is { } a ? mine.FirstOrDefault(s => SamplerVariants.Anisotropy(s) == Math.Clamp(a, 1, 16)) : null;
            if (mine.Where(s => s == current).Concat(mine.Where(s => s != current)).Take(keep).ToList() is { Count: > 0 } kept)
                (keptSets, samplers) = (kept, samplerSets[kept[0]]);
        }
        samplers ??= RootSig.StaticSamplers(rule, engine);
        synth = caps.StateIndependentCache;
        dx12 = engine.GraphicsApi.Contains("D3D12");
        plat = !dx12 ? "" : plats.Count > 0 ? plats.MaxBy(p => p.Value).Key // "": no map matches, no PSOs
            : new[] { "PCD3D_SM6", "PCD3D_SM5" }.FirstOrDefault(index.Platforms.Contains) ?? index.Platforms.FirstOrDefault() ?? "";
        embeddedRs = bc.Values.Count(s => s.RootSignature != null);
        LearnPassRoots();
        if (!build && !ruleFallback && embeddedRs == 0 && (!mapRoots || passRoots > 0) && !maps.Any(m => m.Pooled)) Learned();
        rasterNv = RasterNv(caps, engine.Family, recs, nvRecs, nvExtension);
        // Gears of War: E-Day's recording sets the slot around 12,867 of its 12,883 compute creates and no graphics one
        nvComputeOnly = rasterNv != null && nvExtension && LearnedRasterNv(recs, nvRecs) == null;
        if (rasterNv is { } nvs) log?.Report($"NVAPI: synthesized {(nvComputeOnly ? "compute " : "")}PSOs get shader-extension slot {nvs.Slot} space {nvs.Space} (options {nvs.Options})");
        var ownOnly = embeddedRs > 0 && ownN == builtN; // every recorded PSO the rule was checked on carries its own: the rule plans nothing
        if (dx12) log?.Report($"recorded: {recs.Count} PSOs{(stateObjects.Count > 0 ? $" + {stateObjects.Count} ray tracing state objects (replayed as recorded)" : "")}, platform {plat}; "
            + (ownOnly ? "" : $"root sigs rebuilt from shader counts: {builtOk}/{builtN} exact -> " + (build ? "building" : ruleFallback ? "learned lookup, the rule for the rest" : "learned lookup")) + (maxSrvs > 0 ? $" (SRV tables of {maxSrvs}, not Unreal {engine.Version}'s {rule.RootSignature!.Stock?.MaxSrvs}: {(maxSrvs == 128 ? "the bindless fork's" : "its shaders bind more")})" : "")
            + (embeddedRs > 0 ? $"{(ownOnly ? "" : "; ")}{embeddedRs} shaders carry their own root signature{(ownN > 0 ? $", {ownOk}/{ownN} recorded PSOs created with it" : "")}" : "") + (synth ? ", synthesized templates allowed" : "")
            + (nvExtension ? "; NVIDIA extension table (u0 space 1001) in every root signature: its shaders bind it without Unreal's extension flag" : ""));

        // D3D11: every shader of the game's DXBC platforms once (Unreal PCD3D_SM5, and PCD3D_ES3_1, the feature level a D3D11
        // game may run instead, as Fortnite's Performance mode does; the carver's DXBC containers), no pairing;
        // hull and domain shaders as HS+DS pairs of one map (the warm needs both to draw), every one in at least one pair
        string[] plats11 = [.. new[] { "PCD3D_SM5", "PCD3D_ES3_1" }.Where(index.Platforms.Contains)];
        plat11 = plats11.Length > 0 ? string.Join('/', plats11) : null;
        if (!engine.GraphicsApi.StartsWith("D3D11") || Planner.D3D11Stages(caps) is not { } stages11) return;
        var maps11 = indexMaps.Where(m => plats11.Length == 0 || plats11.Contains(m.Platform)).Select(m => m.Shaders.Where(h => bc.TryGetValue(h, out var s) && Planner.IsD3D11(s)).Select(h => bc[h]).ToList()).ToList();
        // a VS without vertex input needs no layout: its '1' item draws it as the game does (a triangle list behind a PS)
        d3d11 = maps11.SelectMany(m => m).Where(s => s.Stage is not (Stage.Hull or Stage.Domain)
            && (stages11.Contains(s.Stage) || s.Stage == Stage.Vertex && s.Inputs.All(i => i.SysValue != 0))).Select(s => s.Sha1).Distinct().ToList();
        if (!stages11.Contains(Stage.Vertex) && draws11.Count > 0)
        {
            ShaderInfo? Info(string h)
            {
                if (bc.TryGetValue(h, out var s)) return s;
                try { return recBlobs.TryGetValue(h, out var b) ? Unreal.ShaderContainer.Parse(b, h, new ResourceCounts(0, 0, 0, 0)) : null; }
                catch (Exception) { return null; }   // a malformed blob
            }
            var r = Draws11.Plan(draws11, Info, maps11.SelectMany(m => m).Where(s => s.Stage == Stage.Vertex).Select(s => s.Sha1));
            items11 = r.Items;
            log?.Report($"D3D11 vertex stages: {r.Exact} from {draws11.Count} recorded draws, {r.Projected} more for {r.VssProjected} unrecorded vertex shaders from the recorded layouts");
        }
        if (!stages11.Contains(Stage.Hull)) return;
        var paired = new HashSet<string>();
        foreach (var (h, d) in maps11.SelectMany(Planner.TessPairs)) // greedy: a pair is kept when it brings a shader no kept pair has
            if (paired.Add(h.Sha1) | paired.Add(d.Sha1)) tess11.Add((h.Sha1, d.Sha1));
        var unpaired = maps11.SelectMany(m => m).Where(s => s.Stage is Stage.Hull or Stage.Domain && !paired.Contains(s.Sha1)).Select(s => s.Sha1).Distinct().Count();
        if (unpaired > 0) stats["d3d11_tess_unpaired"] = unpaired;
    }

    /// <summary>The stage set's root signature, null when there is none. One the runtime won't serialize (a rule's ranges
    /// overlapping a recording's static samplers) is null too, counted as "rs_unserializable": that stage set is left
    /// out, not the plan; so is one the rule can't build for these shaders ("rs_unbuilt": RE Engine's when no table size
    /// holds what they bind).</summary>
    string? RootSigOf(SortedDictionary<int, string> stages)
    {
        unbuildable = rowsUnlinked = false;
        // a learned pass root that misses a shader's ranges: another of the pass's roots, which the learned lookup may know
        if (mapRs != null && (passRoots == 0 || Uncovered(mapRs, stages) == null)) return mapRs;
        // carved shaders carrying their root signature (RTS0): exact, served by the reader at materialize time
        if (stages.Values.Select(h => bc[h].RootSignature).FirstOrDefault(r => r != null) is { } embedded) return embedded;
        if (!RootSig.InStageSets(rule, stages.Keys.Select(k => (Stage)k))) return null; // not a stage set the rule was confirmed on
        if (!build)
        {
            if (ruleFallback && !Planner.PsRowsLink(Infos(stages))) { rowsUnlinked = true; return null; }   // a learned root signature doesn't make it link
            var learned = rsByCounts.GetValueOrDefault(Planner.CountsKey(Infos(stages)));
            if (learned?.FirstOrDefault(r => Uncovered(r, stages) == null) is { } fits) return fits;
            if (!ruleFallback) return learned?[0];
            fallbackSets++;
        }
        RootSig.Desc desc;
        try { desc = RootSig.Build(rule, Infos(stages), MeshTier(stages), maxSrvs, nvExtension, Nvidia); }
        catch (RootSig.SerializeException e) when (rule.RootSignature?.Re != null)   // a stage set another rule doesn't describe is a broken rule: no plan
        {
            unbuilt ??= e.Message;
            Count("rs_unbuilt");
            return null;
        }
        if (!rsCache.TryGetValue(desc.Key, out var h))
        {
            try
            {
                var b = RootSig.Serialize(desc, samplers!);
                rsCache[desc.Key] = h = Hex(SHA1.HashData(b));
                rsBlobs[h] = b;
                rsDescs[h] = desc;
                if (ruleFallback && rsRanges.TryGetValue(h, out var r) && r == null) rsRanges.Remove(h);   // a recorded name without bytes until now
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
    string? unbuilt;          // the first reason the rule built none
    bool unbuildable;         // RootSigOf's last null: the runtime won't serialize it (a pipeline that can't exist, not a gap)
    bool rowsUnlinked;        // ...or, in a rule-fallback plan, a stage set whose PS reads its inputs from other rows (Planner.PsRowsLink)

    /// <summary>A view-instanced stage set the driver rejects (<see cref="Planner.ShadingRateUnread"/>): a pipeline that can't
    /// exist, not a gap.</summary>
    const string ViShadingRate = "vi_shading_rate_unread";

    /// <summary>A stage set of a rule-fallback plan whose PS reads its inputs from other rows (<see cref="Planner.PsRowsLink"/>): a pipeline that
    /// can't exist, not a gap.</summary>
    const string PsRowsUnlinked = "ps_rows_unlinked";

    /// <summary>A stage set whose PS writes SV_StencilRef on a GPU without it (<see cref="Planner.StencilRefUnsupported"/>): a
    /// pipeline that can't exist, not a gap.</summary>
    const string PsStencilRef = "ps_stencil_ref";

    /// <summary>What a stage set without a root signature counts as.</summary>
    string NoRs => unbuildable ? "rs_unserializable" : rowsUnlinked ? PsRowsUnlinked : "no_rs";

    // a learned lookup's stage and counts parts (Planner.CountsKey), and of its VS+PS keys the VS parts per PS part
    HashSet<string>? learnedParts;
    readonly Dictionary<string, HashSet<string>> learnedVs = [];
    int unpaired;   // the shaders StageSets left out or paired only in part for it

    /// <summary>A pool whose signatures pair nearly every VS with every PS says nothing about which pairs the game draws
    /// (Ratchet &amp; Clank's material archives, one pool each: 2 VS output signatures, 2.8M pairs holding 431 of the
    /// 1,923 pairs a recording drew). Same-signature VS x PS pairs per shader of a pool, measured: Ghost of Tsushima at
    /// most 35, every other installed DirectX 12 game at most 25, R&amp;C's 67 and 329.</summary>
    const int CrossProductRatio = 50;

    /// <summary>The share of a recording's root signatures the rule must rebuild exactly to plan, in a learned lookup, the stage
    /// sets the recording has none for: a guess that is right that often beats leaving them out. 0.5 until measured.</summary>
    public const double RuleFallbackShare = 0.5;

    /// <summary>A learned lookup (<paramref name="ok"/> of <paramref name="n"/> recorded root signatures rebuilt, under 99%) whose rule
    /// rebuilt at least <see cref="RuleFallbackShare"/>: a plan (<see cref="PlanStats.RuleFallback"/>) without the fallback is built again.</summary>
    public static bool RuleFallback(long ok, long n) => n > 0 && ok < 0.99 * n && ok >= RuleFallbackShare * n;

    (long Pairs, int Vs, int Ps)? crossPool;   // the pool StageSets is pairing, when it is one of those
    readonly List<(long Pairs, int Vs, int Ps)> crossPools = [];
    readonly HashSet<string> crossLeftOut = [];   // whole-pipeline: a cross-product pool's stage sets that bring no shader a kept PSO lacks

    readonly HashSet<string> recordedShaders = [], recordedFaces = [];   // the recorded PSOs' library shaders, and their stage and signatures (Face)
    readonly HashSet<string> kept = [];   // the shaders of the recorded PSOs and of the planned stage sets
    static string Face(ShaderInfo s) => $"{(int)s.Stage}|{Planner.Sig(s.Inputs)}|{Planner.Sig(s.Outputs, false)}";

    /// <summary>How likely a generated whole-pipeline PSO is drawn, lowest first. The tier (Rank / 8) is that of its least
    /// likely shader: 0 when in a recorded PSO, 1 when a recorded shader of its stage has its signatures (or a compute
    /// shader: a pipeline on its own), else 2; 2 on a template of another PS output signature (state is in the cache key: a
    /// compile the game misses). Within a tier, more recorded shaders first.</summary>
    int Rank(SortedDictionary<int, string> stages, bool exactTemplate)
    {
        var recorded = stages.Values.Count(recordedShaders.Contains);
        var tier = !exactTemplate ? 2 : stages.Values.Max(h => shaderTiers.TryGetValue(h, out var t) ? t
            : shaderTiers[h] = recordedShaders.Contains(h) ? 0 : bc[h].Stage == Stage.Compute || recordedFaces.Contains(Face(bc[h])) ? 1 : 2);
        return tier * 8 + 7 - recorded;
    }
    readonly Dictionary<string, int> shaderTiers = [];

    static string Part(ShaderInfo s) => $"{(int)s.Stage}:{s.Counts}";

    /// <summary>A learned lookup resolves a stage set only by its counts key, so StageSets pairs only what a recorded key has:
    /// each shader's part, and a VS->PS pair's two (RE Engine: 13.5M of PRAGMATA's 13.6M stage sets had no root signature,
    /// 46 s and 5.7 GB to enumerate). Not with a pooled library: whether a PS is fed in its own map counts every VS there.</summary>
    /// <summary>BlackSpace without the game's key (no map names a root): a pass gets the root signature every recorded graphics
    /// PSO whose shaders are in no other pass was created with, when that is one with bytes (Crimson Desert: 97 of 97 recorded passes,
    /// 24,662 of 29,983 graphics shaders). Every other pass keeps the learned lookup.</summary>
    void LearnPassRoots()
    {
        if (mapRoots || engine.Family != BlackSpace.BlackSpaceReader.Family || recs.Count == 0) return;
        var passesOf = new Dictionary<string, List<int>>();
        for (var i = 0; i < indexMaps.Count; i++)
            if (!maps[i].IsPipeline) foreach (var h in maps[i].Shas) (passesOf.TryGetValue(h, out var l) ? l : passesOf[h] = []).Add(i);
        var roots = new Dictionary<int, HashSet<string>>();
        foreach (var p in recs.Select(Parse).Where(p => p.Stages.Count > 0 && !p.Stages.ContainsKey((int)Stage.Compute)))
            if (p.Stages.Values.Select(h => (IEnumerable<int>)(passesOf.GetValueOrDefault(h) ?? [])).Aggregate((a, b) => a.Intersect(b)).ToList() is [var i])
                (roots.TryGetValue(i, out var s) ? s : roots[i] = []).Add(p.Rs);   // one drawn in two passes tells neither's
        foreach (var (i, s) in roots)
            if (s.Count == 1 && recBlobs.ContainsKey(s.First())) { maps[i] = maps[i] with { Rs = s.First() }; passRoots++; }
        mapRoots = passRoots > 0;
        if (mapRoots) log?.Report($"root signatures of {passRoots} of {maps.Count(m => !m.IsPipeline)} render passes from the recording");
    }

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
    readonly Dictionary<string, string> uncoveredExample = [], ownUncoveredExample = [];
    string? unknownRs;   // the first root signature without bytes

    /// <summary>Pre-emit guard: every shader's declared resources are in the root signature, visible to its stage (else the
    /// driver rejects the PSO: E_INVALIDARG). Null when they are, else what the stage set counts as instead of planned:
    /// "rs_uncovered" when the rule's or a learned root signature misses a range (a rule that doesn't know a fork's extra
    /// ranges, Hogwarts Legacy's bindless SRVs in spaces 4-7: 159594 of 200804 planned PSOs; a learned one picked by counts
    /// alone); "own_rs_uncovered" when the game's own misses one (a Naughty Dog VS drawn with its PS's); "rs_unknown" when
    /// neither the rule, the recording nor the index (<see cref="ShaderIndex.RootSignatures"/>) has its bytes.</summary>
    string? Uncovered(string rs, SortedDictionary<int, string> stages)
    {
        if (!rsRanges.TryGetValue(rs, out var ranges))
            try
            {
                rsRanges[rs] = ranges = rsBlobs.TryGetValue(rs, out var b) || recBlobs.TryGetValue(rs, out b) || index.RootSignatures?.TryGetValue(rs, out b) == true
                    ? RootSig.Parse(b!) : null;
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException) { rsRanges[rs] = null; }
        if (ranges == null)
        {
            unknownRs ??= rs;
            return "rs_unknown";
        }
        foreach (var (st, sha) in stages)
        {
            if (covered.TryGetValue((rs, sha, st), out var ok)) { if (!ok) return Kind(); continue; }
            var why = RootSig.Uncovered(ranges, (Stage)st, bc[sha]);
            covered[(rs, sha, st)] = why == null;
            if (why == null) continue;
            (Own() ? ownUncoveredExample : uncoveredExample).TryAdd(why, sha);
            return Kind();
        }
        return null;
        bool Own() => rs == mapRs || stages.Values.Any(h => bc[h].RootSignature == rs);
        string Kind() => Own() ? "own_rs_uncovered" : "rs_uncovered";
    }

    List<LayoutElem>? LayoutFor(SortedDictionary<int, string> stages, bool templateHasLayout) // the VS's own inputs, never the template's
    {
        if (!stages.TryGetValue((int)Stage.Vertex, out var vs)) return null;
        return templateHasLayout || bc[vs].Inputs.Any(e => e.SysValue == 0) ? Planner.VsLayout(bc[vs]) : null;
    }

    void Count(string k) => stats[k] = stats.GetValueOrDefault(k) + 1;

    /// <summary>The stage set's first visit, with its root signature (<see cref="RootSigOf"/>). Where maps name their root
    /// signature, the same shaders under another one are another stage set (two programs: two PSOs).</summary>
    bool FirstVisit(SortedDictionary<int, string> stages, out string? rs)
    {
        budget.Tick();
        rs = null;
        if (mapRoots)
        {
            rs = RootSigOf(stages);
            return seen.Add(Tuple(rs ?? "", stages));
        }
        if (!seen.Add(Tuple("", stages))) return false;
        rs = RootSigOf(stages);
        return true;
    }

    /// <summary>One stage set as whole-pipeline PSOs: a recorded template of its shape with the shaders swapped (every GS
    /// template of the GS's input topology), else a synthesized one.</summary>
    void Emit(SortedDictionary<int, string> stages, string shape, string psOut)
    {
        // a cross-product pool's stage set only for a shader no kept PSO has, as the per-stage cover: not visited, so an
        // ordinary pool still plans it
        if (crossPool != null && stages.Values.All(kept.Contains))
        {
            if (Tuple("", stages) is var t && !seen.Contains(t)) crossLeftOut.Add(t);
            return;
        }
        if (!FirstVisit(stages, out var rs)) return; // shaders shared across maps
        if (crossLeftOut.Count > 0) crossLeftOut.Remove(Tuple("", stages));
        var gs = stages.TryGetValue((int)Stage.Geometry, out var g) ? bc[g] : null;
        var topo = stages.ContainsKey((int)Stage.Hull) ? 4u : gs == null ? 3u : Planner.TopologyType(gs.GsInputPrimitive); // 4 = patch; 0 = GS input unknown
        // a GS needs its input's topology type: the recorded templates of that type and stages (all topologies when unknown:
        // the ones not matching fail harmlessly at replay), else a synthesized one
        var exact = gs != null || templates.ContainsKey($"{shape}|{psOut}");
        var cands = gs != null ? gsTopo.Where(t => (topo == 0 || t.Key.Topology == topo) && t.Key.Shape == shape).Select(t => t.Value).ToList() is { Count: > 0 } l ? l : null
            : templates.GetValueOrDefault($"{shape}|{psOut}") ?? templates.GetValueOrDefault(shape);
        var views = ViewsOf(stages);
        if (cands != null && synth && topo != 0)   // an item keeps its template's view instancing
            cands = cands.Where(c => SameViews(TemplateViews(c.Key), views)).ToList() is { Count: > 0 } same ? same : null;
        if (rs == null || (cands == null && (!synth || topo == 0))) { Count(rs == null ? NoRs : gs != null ? "no_gs_template" : "no_template"); return; }
        if (have.Contains(Tuple(rs, stages))) { Count("already_recorded"); return; }
        if (Planner.StencilRefUnsupported(caps, Infos(stages))) { Count(PsStencilRef); return; }
        if (Planner.ShadingRateUnread(Infos(stages)))   // a recorded template keeps its own view instancing
        {
            var plain = cands?.Where(c => TemplateViews(c.Key) == null).ToList();
            if (plain != null ? plain.Count == 0 : views != null) { Count(ViShadingRate); return; }
            cands = plain;
        }
        if (Uncovered(rs, stages) is { } left) { Count(left); return; }
        usedRs.Add(rs);
        Count("generated");
        kept.UnionWith(stages.Values);
        var rank = Rank(stages, exact);
        if (gs != null && cands != null)
        {
            foreach (var t in cands)
            {
                items.Add(Item(t.Key, rs, stages, LayoutFor(stages, t.Layout)));
                itemRanks.Add(PsOut(Parse(recByKey[t.Key]).Stages) == psOut ? rank : Rank(stages, false));   // GS templates match by topology only
                usedTemplates.Add(t.Key);
            }
            return;
        }
        if (cands != null)
        {
            var t = cands.FirstOrDefault(c => c.Rs == rs, cands[0]);
            items.Add(Item(t.Key, rs, stages, LayoutFor(stages, t.Layout)));
            itemRanks.Add(rank);
            usedTemplates.Add(t.Key);
            return;
        }
        var (rt, dsv) = Targets(stages);
        var skey = $"{shape}|{topo}|{string.Join(',', rt)}|{dsv}|{(views != null ? Hex(views) : "")}";
        var layout = LayoutFor(stages, true);
        if (synthByShape.TryGetValue(skey, out var tkey)) { items.Add(Item(tkey, rs, stages, layout)); itemRanks.Add(rank); }
        else // the first PSO of a shape is its template (and gets compiled as itself)
        {
            var rec = new Rec('S', Stream(rs, stages, layout, topo, rt, dsv, viewInstancing: views));
            synthByShape[skey] = rec.Key;
            synthesized.Add(rec);
        }
    }

    static string ShaderSet(IEnumerable<string> shas) => string.Join(',', shas.Order(StringComparer.Ordinal));

    /// <summary>A per-stage cache (<see cref="VendorCaps.PerStageCache"/>, AMD today) needs each stage unit compiled once, not
    /// every VS x PS pair, and (AMD) with the state its stage is keyed on (<see cref="UnitPolicy"/>): what the recording says
    /// about each shader (<see cref="ExactLayouts"/>, resolved per shader, not per pair), the units of every stage set of the
    /// library, one greedy cover (<see cref="UnitCover.CoverAll"/>) seeded with the recording's own units (it replays as it
    /// is), each pick a synthesized 'S' stream with its exact read layout, topology, RT formats / write masks / logic op of its
    /// export shape. A VS alone (depth pass) is its own unit. The game's own pairings (recorded, or shipped whole as a pipeline
    /// cache's PSOs) come first, then compute and single stages, then the pairings the plan makes up, each by state: exact or
    /// inferred first, guesses last, so a stopped warm or a compile size limit has the likely ones.</summary>
    void PerStage(UnitPolicy policy)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var facts = ExactLayouts.Build(recs, recBlobs, policy, bc, budget.Tick);
        var cover = UnitCover.Seeded(facts, h => bc.ContainsKey(h) || recBlobs.ContainsKey(h)); // a shared recording's PSO with a shader neither has doesn't replay
        var recorded = cover.Covered.ToHashSet();

        var layouts = new Dictionary<string, Resolved<List<List<LayoutElem>>>>();
        var shapes = new Dictionary<string, Resolved<List<string>>>();
        var topos = new Dictionary<string, List<uint>>();
        var cands = new List<(UnitCover.Candidate C, Provenance Vs, Provenance Ps, bool Cross, int Rank)>();
        var shipped = maps.Where(m => m.IsPipeline && OnPlatform(m.Platform) && m.Shas.All(bc.ContainsKey)).Select(m => ShaderSet(m.Shas)).ToHashSet();
        var known = shipped.Count > 0 || have.Count > 0;   // without a pairing the game is known to use, the order stays
        int pairs = 0, fromCache = 0;
        Resolved<List<List<LayoutElem>>> none = new([], Provenance.Exact);
        var pairedElsewhere = new HashSet<string>();   // stage sets an ordinary pool pairs too, seen first in a cross-product one
        var perShader = !policy.NextStage && !policy.PartnerReads;   // AMD keys a VS on its PS: Cyberpunk's cross pool, 182,784 VS units of 4,500 VS
        StageSets((stages, _, _) =>
        {
            // a cross-product pool's stage set only for a shader no kept stage set has, as Emit, where a unit doesn't depend on
            // the other stages: enumerating them all outgrows the plan's memory (FF XVI: 15.4 GB at 28.5M of 32,732 VS x 42,525 PS)
            if (perShader && crossPool != null && stages.Values.All(h => kept.Contains(h) && !bc[h].ViewId)) { Count("cross_product"); return; }   // a view-instanced set is another unit
            if (!FirstVisit(stages, out var rs)) // shaders shared across maps
            {
                if (crossPool == null && crossPools.Count > 0) pairedElsewhere.Add(Tuple(mapRoots ? rs ?? "" : "", stages));
                return;
            }
            if (rs == null) { Count(NoRs); return; }
            if (!have.Contains(Tuple(rs, stages)) && Uncovered(rs, stages) is { } left) { Count(left); return; } // recorded: the game created it
            var views = ViewsOf(stages);
            if (views != null && !have.Contains(Tuple(rs, stages)) && Planner.ShadingRateUnread(Infos(stages))) { Count(ViShadingRate); return; }
            if (!have.Contains(Tuple(rs, stages)) && Planner.StencilRefUnsupported(caps, Infos(stages))) { Count(PsStencilRef); return; }
            if (rsBlobs.TryGetValue(rs, out var blob)) facts.AddRootSignature(rs, blob); // VS units share PIXEL-only differences
            var l = stages.TryGetValue((int)Stage.Vertex, out var vs) ? layouts.TryGetValue(vs, out var r) ? r : layouts[vs] = facts.Layouts(bc[vs]) : none;
            var s = stages.TryGetValue((int)Stage.Pixel, out var ps) ? shapes.TryGetValue(ps, out var q) ? q : shapes[ps] = facts.Shapes(bc[ps]) : new([], Provenance.Exact);
            List<uint> t = [];
            if (vs != null)
            {
                var tk = $"{vs}|{stages.GetValueOrDefault((int)Stage.Geometry)}|{stages.ContainsKey((int)Stage.Hull)}";
                if (!topos.TryGetValue(tk, out t!)) topos[tk] = t = facts.Topologies(stages).Value;
            }
            // a vendor keying the VS on its layout (AMD: Darwin's Paradox, 777 of 2,000 shipped stage sets compiled again for it)
            // gets the shipped one, unless the recording has the VS
            if (policy.ReadLayout && vs != null && !facts.ReadLayouts.ContainsKey(vs) && shippedLayouts.TryGetValue(ShaderSet(stages.Values), out var sl)
                && sl.Select(x => ExactLayouts.ReadLayout(x, bc[vs])).Where(x => x.Count == bc[vs].Inputs.Count(i => i.SysValue == 0)).DistinctBy(x => string.Join(';', x)).ToList() is { Count: > 0 } exact)
            {
                l = new(exact, Provenance.Exact);   // only declarations feeding every input the VS reads
                fromCache++;
            }
            if (ps != null && (vs != null || stages.ContainsKey((int)Stage.Mesh))) pairs++;
            // Gears of War: E-Day: its pipeline cache's pairings are 89.5% NVIDIA-exact against its Advanced Shader Delivery db, made-up ones 0 of 92
            var rank = !known ? 0 : stages.Count == 1 ? 1 : shipped.Contains(ShaderSet(stages.Values)) || have.Contains(Tuple(rs, stages)) ? 0 : 2;
            cands.Add((new UnitCover.Candidate(stages, rs, l.Value, t, s.Value, views), l.Provenance, s.Provenance, crossPool != null, rank));
            kept.UnionWith(stages.Values);
        });
        if (pairedElsewhere.Count > 0)
            for (var i = 0; i < cands.Count; i++)
                if (cands[i].Cross && pairedElsewhere.Contains(Tuple(mapRoots ? cands[i].C.Rs : "", cands[i].C.Stages))) cands[i] = cands[i] with { Cross = false };
        var ordered = cands.OrderBy(c => c.Rank).ThenBy(c => (int)(c.Vs > c.Ps ? c.Vs : c.Ps)).ToList(); // stable: exact, inferred, then guessed
        var prov = new Dictionary<UnitCover.Candidate, (Provenance Vs, Provenance Ps)>(ReferenceEqualityComparer.Instance);
        var ranks = new Dictionary<UnitCover.Candidate, int>(ReferenceEqualityComparer.Instance);
        foreach (var c in ordered) (prov[c.C], ranks[c.C]) = ((c.Vs, c.Ps), c.Rank);
        // the game's pairings covered whole (both of CoverAll's passes) first: a made-up pair never takes a unit one has. Single stages share
        // the made-up pairs' cover: covered first, a lone VS takes its unit (NVIDIA's has no next stage) and leaves each pair one new unit
        var picks = ordered.GroupBy(c => c.Rank == 0).SelectMany(g => cover.CoverAll(g.Select(c => c.C).ToList(), budget.Tick)).ToList();
        if (!caps.StateIndependentCache || caps.WarmCacheBudget != null) picks = WithinBudget(picks, facts, recorded, prov, ranks);   // NVIDIA: only the game's compile size limit
        var coverSize = picks.Count;
        int extrasLeftOut = 0, crossLeftOut = 0, extrasLimit = Math.Min(MaximumExtras(caps, coverSize + have.Count), extrasRoom);
        if (maximum && cacheLeftOut is not > 0) // at its cache budget a plan has no room for more. Else every stage set once more, on its first candidates (their units are covered: nothing new to compile)
        {
            var byCand = picks.ToLookup<(UnitCover.Candidate Candidate, UnitPick Pick), UnitCover.Candidate>(p => p.Candidate, ReferenceEqualityComparer.Instance);
            var all = new List<(UnitCover.Candidate, UnitPick)>();
            var extras = 0;
            foreach (var o in ordered)   // exact state first: a capped game keeps the likely pairings
            {
                budget.Tick();
                if (byCand[o.C] is var own && own.Any()) all.AddRange(own);
                else if (have.Contains(Tuple(o.C.Rs, o.C.Stages))) { } // recorded: the recording replays it
                else if (o.Cross) crossLeftOut++;   // its units are in the cover; the pairs the game draws come from a recording
                else if (extras++ < extrasLimit) all.Add((o.C, FirstPick(o.C)));
                else extrasLeftOut++;
            }
            picks = all;
            // the pairings the compile size limit's room left out: a limit raised later rebuilds the plan (BudgetGrew)
            if (caps.WarmCacheBudget != null && Math.Min(extras, MaximumExtras(caps, coverSize + have.Count)) - extrasLimit is > 0 and var over) cacheLeftOut = (cacheLeftOut ?? 0) + over;
        }

        var counted = recorded.ToHashSet();
        var newByStage = new SortedDictionary<Stage, int>();
        long planned = 0;
        foreach (var (c, p) in picks)
        {
            Steps?.Invoke("pipelines planned", ++planned, picks.Count);
            budget.Tick();
            var (pv, pp) = prov[c];
            foreach (var u in UnitCover.Units(facts, c.Stages, c.Rs, p.Layout, p.Topology, p.Shape, c.Views))
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
            synthesized.Add(new Rec('S', link != null ? Stream(c.Rs, stages, p.Layout, p.Topology, rt, link.Dsv, blend: link.Blend, depthStencil: link.DepthStencil, viewInstancing: c.Views)
                : Stream(c.Rs, stages, p.Layout, p.Topology, rt, dsv, masks, ops.Length > 0 ? ops[0] : -1, viewInstancing: c.Views)));
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
            + $"cover {coverSize} PSOs{(maximum ? $" + {picks.Count - coverSize} for stage sets it doesn't need (maximum{(extrasLeftOut > 0 ? $"; {extrasLeftOut} more left out, at most {extrasLimit} for this game" : "")}{(crossLeftOut > 0 ? $"; {crossLeftOut} more left out from {crossPools.Count} pools whose signatures pair nearly every VS with every PS (largest {crossPools.Max().Pairs} pairs of {crossPools.Max().Vs} VS and {crossPools.Max().Ps} PS): only a recording says which the game draws" : "")})" : "")}; VS layouts resolved from the recording: {layoutCoverage:P0} of {libVs.Count} (L0 {l0}, L0' no vertex input {l0b}, L1 {l1})"
            + (fromCache > 0 ? $"; VS layouts from the game's pipeline cache: {fromCache} stage sets" : "") + $" ({sw.Elapsed.TotalSeconds:F1}s)"
            + (facts.ClassFixes.Count > 0 ? $"; guessed layouts with a mixed slot: {string.Join(", ", facts.ClassFixes.Select(f => $"{f.Key} {f.Value}"))}" : ""));
        if (layoutCoverage < 0.8 && policy.ReadLayout)   // a vendor whose VS key ignores the layout (NVIDIA) doesn't care
            log?.Report($"warning: only {layoutCoverage:P0} of the game's vertex shaders have a recorded input layout; the rest are inferred or guessed "
                + "(a guessed layout that's wrong costs a compile in game): a longer recording helps");
    }

    /// <summary>The cover PSOs that fit <see cref="CacheBudget"/> with the recorded ones (always kept), each compiled once
    /// per texture filtering setting (<see cref="SettingsVariants"/>), most likely first: by <paramref name="ranks"/> (the game's
    /// own pairings, compute and single stages, made-up pairings), then those compiling a shader the plan has no unit of yet,
    /// then by provenance, then by how many stage sets their new units serve (a VS unit keyed on a PS read key many pixel shaders
    /// share is drawn more often than a rare one's).</summary>
    List<(UnitCover.Candidate Candidate, UnitPick Pick)> WithinBudget(List<(UnitCover.Candidate Candidate, UnitPick Pick)> picks, ExactLayouts facts,
        HashSet<Unit> recorded, Dictionary<UnitCover.Candidate, (Provenance Vs, Provenance Ps)> prov, IReadOnlyDictionary<UnitCover.Candidate, int> ranks)
    {
        var filtering = 1 + OtherSamplerSets().Count;
        var room = (int)Math.Clamp((budgetPsos - recs.Count(Replays)) / filtering, 0, int.MaxValue);
        cacheLeftOut = Math.Max(0, picks.Count - room);
        extrasRoom = Math.Max(0, room - picks.Count);
        if (cacheLeftOut == 0) return picks;
        if (room == 0) return Kept([], 0);   // the recorded PSOs fill the budget: nothing to rank
        var served = new Dictionary<Unit, int>();
        var units = new HashSet<Unit>();
        foreach (var c in prov.Keys)   // each unit once per stage set, at every state candidate
        {
            budget.Tick();
            var f = FirstPick(c);
            foreach (var s in c.Shapes.DistinctBy(s => s != "").DefaultIfEmpty(f.Shape))   // a VS unit keys on whether a render target is bound
                foreach (var l in c.Layouts.DefaultIfEmpty(f.Layout))
                    foreach (var t in c.Topologies.DefaultIfEmpty(f.Topology))
                        units.UnionWith(UnitCover.Units(facts, c.Stages, c.Rs, l, t, s, c.Views));
            foreach (var s in c.Shapes) units.UnionWith(UnitCover.Units(facts, c.Stages, c.Rs, f.Layout, f.Topology, s, c.Views));
            foreach (var u in units) served[u] = served.GetValueOrDefault(u) + 1;
            units.Clear();
        }
        var covered = recorded.ToHashSet();
        var compiled = recorded.Select(u => u.Shader).ToHashSet();
        var ranked = picks.Select((p, i) =>
        {
            budget.Tick();
            var fresh = UnitCover.Units(facts, p.Candidate.Stages, p.Candidate.Rs, p.Pick.Layout, p.Pick.Topology, p.Pick.Shape, p.Candidate.Views).Where(covered.Add).ToList();
            var first = fresh.Count(u => compiled.Add(u.Shader)) > 0;
            var (v, s) = prov[p.Candidate];
            return (p, Rank: ranks[p.Candidate], First: first, Prov: v > s ? v : s, Served: fresh.Sum(u => (long)served.GetValueOrDefault(u)), At: i);
        }).OrderBy(r => r.Rank).ThenBy(r => !r.First).ThenBy(r => r.Prov).ThenByDescending(r => r.Served).ThenBy(r => r.At).ToList();
        var kept = ranked.Take(room).ToList();
        return Kept(kept.Select(r => r.p).ToList(), kept.Count(r => r.First));

        List<(UnitCover.Candidate Candidate, UnitPick Pick)> Kept(List<(UnitCover.Candidate Candidate, UnitPick Pick)> kept, int first)
        {
            stats["cache_budget"] = (int)cacheLeftOut;
            log?.Report($"cache budget ({caps.Profile}): {kept.Count} of {picks.Count} cover PSOs kept ({first} compile a shader the first time)"
                + (filtering > 1 ? $" at {filtering} texture filtering settings" : "") + $" beside the recorded ones, {cacheLeftOut} left out: "
                + $"at {PsoBytesOf(caps) >> 10} KiB a PSO the plan stays under the {App.Format.Bytes(budgetPsos * PsoBytesOf(caps))} {(caps.WarmCacheBudget != null ? "the game's compile size limit allows" : caps.PerGameCacheCap != null ? "the driver keeps for one game" : "the driver's whole cache holds")}");
            return kept;
        }
    }

    /// <summary>A pairing plan on a cache with a warm budget (<see cref="VendorCaps.WarmCacheBudget"/>, NVIDIA on Linux): the
    /// generated PSOs that fit beside the recorded ones and the synthesized templates are kept, most likely first
    /// (<see cref="Rank"/>, then planning order), and in that order, so a compile reaching the folder's room stops before
    /// the unlikely ones. Ray tracing and DirectX 11 items come after them.</summary>
    void WithinWarmBudget()
    {
        var filtering = 1 + OtherSamplerSets().Count;
        var room = (int)Math.Clamp((budgetPsos - recs.Count(Replays)) / filtering - synthesized.Count, 0, int.MaxValue);
        cacheLeftOut = Math.Max(0, items.Count - room);
        if (cacheLeftOut == 0) return;
        var order = Enumerable.Range(0, items.Count).OrderBy(i => itemRanks[i]).Take(room).ToList();   // stable
        var byTier = new int[3];
        foreach (var i in order) byTier[itemRanks[i] >> 3]++;
        var keep = order.Select(i => items[i]).ToList();
        items.Clear();
        items.AddRange(keep);
        stats["cache_budget"] = (int)cacheLeftOut;
        log?.Report($"cache budget ({caps.Profile}): {room} of {room + cacheLeftOut} generated PSOs kept (shaders recorded / signatures recorded / guessed: {string.Join('/', byTier)})"
            + (filtering > 1 ? $" at {filtering} texture filtering settings" : "") + $" beside the recorded ones, {cacheLeftOut} left out: "
            + $"at {PsoBytesOf(caps) >> 10} KiB a PSO the plan stays under the {App.Format.Bytes(budgetPsos * PsoBytesOf(caps))} the compile's cache holds for the game");
    }

    /// <summary>A per-stage cache whose state is in the key (AMD, Intel) holds every pairing Maximum adds, at about
    /// <see cref="ExtraBytes"/> each, in a cache all games share (AMD's 16 GiB) or the game's own (Intel). Maximum adds at
    /// most as many as the plan replays otherwise (cover + recorded), and no more than <see cref="ExtraBudget"/> or the
    /// per-game cap.</summary>
    public static int MaximumExtras(VendorCaps caps, long planned) =>
        caps.StateIndependentCache ? int.MaxValue : (int)Math.Min(planned, Math.Min(ExtraBudget, caps.PerGameCacheCap ?? long.MaxValue) / ExtraBytes);
    /// <summary>An eighth of AMD's shared cache: FF7 Rebirth's 60k extra pairings (1.3 GiB) fit, Plague Tale's 289k don't.</summary>
    public const long ExtraBudget = 2L << 30;
    /// <summary>Measured on AMD: 1,500 extra pairings of FF7 Rebirth grew the cache 32 MiB.</summary>
    public const long ExtraBytes = 22 << 10;
    /// <summary>Measured on AMD as the cache's growth over a cold warm: FF7 Rebirth 26,195 bytes a PSO, Banishers 29,384.
    /// Unmeasured on Intel, which uses it too. NVIDIA on Linux, the warm's files rewritten without a dictionary: REANIMAL
    /// 24,410.</summary>
    public const long PsoBytes = 28 << 10;
    /// <summary>What one game's plan may take where state is in the cache key: AMD's whole DxcCache, at <see cref="PsoBytes"/>
    /// a PSO (599,186 PSOs). Only a plan the cache can't hold at all is trimmed: Ratchet &amp; Clank's 404,918 fit.</summary>
    public const long CacheBudget = Vendors.AmdAppCache.DxcCacheCap;
    /// <summary><see cref="CacheBudget"/> in PSOs.</summary>
    public const long CachePsos = CacheBudget / PsoBytes;
    /// <summary>What one game's plan may take where state is in the cache key: the vendor's warm budget (NVIDIA on Linux),
    /// else the driver's per-game cap (Intel), else <see cref="CacheBudget"/>.</summary>
    public static long CacheBudgetOf(VendorCaps caps) => caps.WarmCacheBudget ?? caps.PerGameCacheCap ?? CacheBudget;

#if SCSK_TEST_HOOKS
    // SCSKILLER_TEST_AMD_CACHE_BUDGET_GB=N (tests): an AMD plan's budget in GiB, past the driver's cap to see what it evicts
    static long? TestCacheBudget(VendorCaps caps) => caps is { WarmCacheBudget: null, PerGameCacheCap: null }
        && long.TryParse(Environment.GetEnvironmentVariable("SCSKILLER_TEST_AMD_CACHE_BUDGET_GB"), out var gb) && gb > 0 ? gb << 30 : null;
#else
    static long? TestCacheBudget(VendorCaps caps) => null;
#endif

    /// <summary>What a PSO takes of <see cref="CacheBudgetOf"/>: the game's measured bytes (<see cref="VendorCaps.PsoBytes"/>), else <see cref="PsoBytes"/>.</summary>
    public static long PsoBytesOf(VendorCaps caps) => caps.PsoBytes is { } b && b > 0 ? b : PsoBytes;

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
        string? walking = null;   // a pass's root (LearnPassRoots): its shaders pair whatever the learned lookup's counts
        bool Usable(string h)
        {
            if (!bc.TryGetValue(h, out var s)) return false;
            if (VendorOnly(h))
            {
                ags.Add(h);
                return false;
            }
            if (learnedParts == null || walking != null || s.Stage == Stage.Library || learnedParts.Contains(Part(s))) return true; // libraries: not raster stage sets
            unpairedShaders.Add(h);
            return false;
        }
        var fed = new HashSet<string>();
        var unfed = new List<ShaderInfo>();
        var lanes = new HashSet<string>();   // shaders whose [WaveSize] this GPU doesn't run (the runtime: E_INVALIDARG)
        long paired = 0;
        foreach (var (mapPlat, pooled, shas, isPipeline, root) in maps)
        {
            Steps?.Invoke("shader groups paired", paired++, maps.Count);
            walking = root;
            ct.ThrowIfCancellationRequested();
            if (!OnPlatform(mapPlat))
            {
                if (plat != "" && mapPlat.StartsWith(plat + " wave")) lanes.UnionWith(shas);
                continue;
            }
            var all = shas.Where(bc.ContainsKey).ToList();
            var ds = all.Where(Usable).Select(h => bc[h]).ToList();
            if (isPipeline && ds.Count < all.Count) continue; // a shipped pipeline stays whole or not at all
            if (isPipeline) // a pipeline the game ships (e.g. a PSO cache record): exactly its stages
            {
                var st = new SortedDictionary<int, string>();
                foreach (var d in ds) st[(int)d.Stage] = d.Sha1;
                if (!RootSig.InStageSets(rule, st.Keys.Select(k => (Stage)k))) continue; // a set the rule doesn't list: not in the plan nor its counts
                mapRs = root;
                if (st.Count > 0 && Planner.Positioned(st.ToDictionary(x => (Stage)x.Key, x => bc[x.Value]))) sink(st, Planner.Shape(st), PsOut(st));
                else if (st.Count > 0 && FirstVisit(st, out var rs))   // a stage set like any other, counted once
                    Count(rs != null && have.Contains(Tuple(rs, st)) ? "already_recorded" : unbuildable ? NoRs : "stream_output");
                mapRs = null;
                continue;
            }
            mapRs = root;   // a pool's root signature: a BlackSpace pass's, which its shaders drawn in other passes too don't carry
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
                if (learnedParts == null || walking != null) return all;
                if (!byPart.TryGetValue(pin, out var l)) byPart[pin] = l = all.Select((v, i) => (i, v)).ToLookup(x => Part(x.v));
                var kept = learnedVs.TryGetValue(Part(p), out var parts) ? parts.SelectMany(v => l[v]).OrderBy(x => x.At).Select(x => x.Vs).ToList() : [];
                if (kept.Count < all.Count) unpairedShaders.Add(p.Sha1);
                return kept;
            }
            var pss = ds.Where(d => d.Stage == Stage.Pixel).ToList();
            var fanIn = srcs.ToDictionary(g => g.Key, g => g.Value.Count(Planner.Rasterizable));
            var sameSig = pss.Sum(p => (long)fanIn.GetValueOrDefault(Planner.Sig(p.Inputs)));
            var vsCount = srcs.Values.Sum(l => l.Count);
            if (sameSig > CrossProductRatio * (long)(vsCount + pss.Count)) crossPools.Add((crossPool = (sameSig, vsCount, pss.Count)).Value);
            foreach (var p in pss)
            {
                budget.Tick();
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
            crossPool = null;
            // VS -> GS with no PS (depth passes: UE's one-pass point-light shadows draw opaque casters into a cube map's
            // depth through a GS, no PS): every GS writing SV_Position behind every VS of its map that feeds it
            foreach (var g in ds.Where(d => d.Stage == Stage.Geometry && Planner.Rasterizable(d)))
                foreach (var v in srcs.GetValueOrDefault(Planner.Sig(g.Inputs), []).Where(v => v.Stage == Stage.Vertex && Planner.Links(v, g) && Planner.SameRs(v, g)))
                    sink(new() { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Geometry] = g.Sha1 }, Planner.Shape(Stage.Vertex, Stage.Geometry), "");
            // AS -> MS (-> PS): an amplification shader launches mesh shader groups of its map whose payload is its own; alone
            // (depth) and with every PS the MS links to
            foreach (var a in ds.Where(d => d.Stage == Stage.Amplification))
                foreach (var m in mss.Where(m => Planner.SameRs(a, m) && Planner.Launches(a, m)))
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
                    budget.Tick();
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
            mapRs = null;
        }

        // global VS->GS chains also feed material pixel shaders (e.g. rendering into volume textures): pair across libraries
        var gpool = maps.Where(m => m.Pooled && OnPlatform(m.Platform)).SelectMany(m => m.Shas).Where(Usable).Select(h => bc[h]).ToList();
        var psByIn = new Dictionary<string, List<ShaderInfo>>();
        foreach (var (mapPlat, pooled, shas, _, _) in maps)
            if (!pooled && OnPlatform(mapPlat))
                foreach (var h in shas.Where(h => Usable(h) && bc[h].Stage == Stage.Pixel)) Planner.Push(psByIn, Planner.Sig(bc[h].Inputs), bc[h]);
        if (ags.Count > 0) stats["vendor_extension"] = ags.Count;
        if (lanes.Count > 0) stats["wave_size"] = lanes.Count;
        var gvsOut = gpool.Where(v => v.Stage == Stage.Vertex).ToLookup(v => Planner.Sig(v.Outputs));
        foreach (var g in gpool.Where(d => d.Stage == Stage.Geometry))
        {
            var vss = gvsOut[Planner.Sig(g.Inputs)].Where(v => Planner.Links(v, g)).ToList();
            foreach (var p in psByIn.GetValueOrDefault(Planner.Sig(g.Outputs), []).Where(p => Planner.Links(g, p)))
            {
                budget.Tick();
                foreach (var v in vss.Where(v => Planner.SameRs(v, g, p)))
                    sink(new() { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Geometry] = g.Sha1, [(int)Stage.Pixel] = p.Sha1 },
                        Planner.Shape(Stage.Vertex, Stage.Pixel, Stage.Geometry), Planner.Sig(p.Outputs, false));
            }
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
    /// off as played) from the game's ray tracing rule (<see cref="RtCollections.Choose"/>; plan-rules.json: Unreal 4.25
    /// Returnal's, without a state object config; Unreal 4.26/4.27 from UE 4.26's source as Jedi: Survivor confirms it, Avalanche's 4.27 fork's or Kuro's 4.26 fork's; Unreal 5.0-5.4 5.1's when the
    /// libraries have its binding shape, verified on 5.1 only; Northlight its own global and local root signatures, the rest
    /// guessed). REDengine 3 and FromSoftware plan their material hit groups
    /// instead (their rules select them). A library declaring a resource neither root signature gives it is left out
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
        var rtRule = rules.RtFor(engine);
        if (rtRule?.Rt?.Select?.Contains("red3HitGroups") == true) { Red3HitGroups(libs.Count); return; }
        if (rtRule?.Rt?.Select?.Contains("soulsHitGroups") == true) { SoulsHitGroups(libs.Count); return; }
        var learned = stateObjects.Select(RtCollections.Read).OfType<RtCollections.Recorded>().ToList();
        RtCollections.Rule rule;
        string? guessedLocal = null; // the rule's one local root signature, Northlight's (else UE's, per library)
        string how;
        RtCollections.Nv? ruleNv = rtRule?.Rt?.Nv is { } slot ? new(slot.Slot, slot.Space, 0) : null;
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
        // a recording without state objects (ray tracing off as played) teaches nothing about them: the game's rule, as without one
        else if (RtCollections.Choose(rtRule, engine, [.. libs.Select(l => bc[l])], out var none) is not { } c)
        {
            log?.Report($"ray tracing: {libs.Count} DXIL libraries; {none}: none synthesized");
            return;
        }
        else
        {
            var (h, b) = RtCollections.Serialize(c.Global, c.Samplers);
            rsBlobs[h] = b;
            if (c.Local != null)
            {
                (guessedLocal, var lb) = RtCollections.Serialize(c.Local, []);
                rsBlobs[guessedLocal] = lb;
            }
            rule = new(h, c.Flags, c.Depth, c.Payload, c.Attributes, false);
            how = c.How;
        }

        // NVIDIA keys a collection on the NVAPI shader-extension slot too (selftest nvext): only a recording shows whether the
        // game sets one around its creates, or a rule that knows the game's (ruleNv) when no NVAPI record names a recorded
        // collection: a collection's record without a slot is evidence of none
        var collections = stateObjects.Where(r => RtCollections.Read(r) != null).ToList();
        var learnedNv = RtCollections.LearnedNv(collections, nvRecs);
        var collectionKeys = collections.Select(r => r.Key).ToHashSet();
        var fromRule = learnedNv == null && ruleNv != null && !nvRecs.Any(r => r.Payload.Length == NvState.Size && collectionKeys.Contains(NvState.Parse(r).Target));
        var nv = RtCollections.WithOptions(learnedNv ?? (fromRule ? ruleNv : null), RtOptions);
        var nvHow = nv is { } x ? $"{(x.Slot == uint.MaxValue ? "no NVAPI extension slot" : $"NVAPI extension slot u{x.Slot} space {x.Space}")}{(x.Options != 0 ? $", creation flags 0x{x.Options:x}" : "")}, {(fromRule ? "as the rule sets it" : "as the recorded state objects")}"
            : learned.Count > 0 ? "no NVAPI state recorded with the recorded collections"
            : "NVAPI state unknown without a recording of ray tracing (a game setting NVIDIA's shader-extension slot misses them all)";
        var recordedLibs = LocalStateObjects.SelectMany(r => ParseStateObject(r).Libraries).ToHashSet();
        var global =rsBlobs.TryGetValue(rule.GlobalRs, out var gb) || recBlobs.TryGetValue(rule.GlobalRs, out gb) ? RootSig.Parse(gb) : null;
        long rtDone = 0;
        foreach (var h in libs)
        {
            Steps?.Invoke("ray tracing libraries planned", ++rtDone, libs.Count);
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
        var nv = RtCollections.WithOptions(rasterNv, RtOptions);
        foreach (var m in indexMaps.Where(m => m.Library == RedEngine.RedEngineReader.HitGroups && m.Shaders.All(bc.ContainsKey)))
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
            hitGroupItems.Add(RedEngine.RedRayTracing.Item(m.Shaders[0], m.Shaders.ElementAtOrDefault(1), local, shape, nv));
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
        var recordedLibs = LocalStateObjects.SelectMany(r => ParseStateObject(r).Libraries).ToHashSet();
        var pairs = new HashSet<string>();
        foreach (var m in indexMaps.Where(m => m.Platform == plat && FromSoft.SoulsRayTracing.IsPair(m) && m.Shaders.All(h => bc.TryGetValue(h, out var s) && s.Stage == Stage.Library)))
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
    /// (<see cref="SamplerVariants"/>): the root signatures the rule built, serialized with that setting's samplers. The
    /// community's other settings are left out unless in Maximum mode (ScsKiller.ForeignVariants), so these are two of this
    /// PC's own, or in Maximum mode every one of a PC without a recording. A copy the recording has is left out.</summary>
    (List<Rec> Synthesized, List<byte[]> Items) SettingsVariants()
    {
        var others = OtherSamplerSets();
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
            {
                budget.Tick();
                if (Parse(r) is var p && Swap(r.Payload, p.Rs, to, p.Stages) is { } c) { synth.Add(new Rec(r.Tag, c)); usedRs.Add(to[p.Rs]!); }
            }
            foreach (var i in items)
            {
                budget.Tick();
                if (ParseItem(i) is var p && Swap(i, p.Rs, to, p.Stages) is { } c) { its.Add(c); usedRs.Add(to[p.Rs]!); }
            }
        }
        log?.Report($"texture filtering: {others.Count + 1} settings in the recording, {synth.Count + its.Count} PSOs again at the other {(others.Count == 1 ? "one" : "ones")}");
        return (synth, its);
    }

    /// <summary>The recording's texture filtering settings other than the plan's own: each compiles the synthesized PSOs again.</summary>
    List<byte[]> OtherSamplerSets() => !build || samplers is not { Length: > 0 } primary ? []
        : SamplerVariants.OtherSettings(new HashSet<string> { Convert.ToHexStringLower(primary) }, settings.Values)
            .Where(h => keptSets?.Contains(h) != false).Select(h => samplerSets[h]).ToList();

    /// <summary>A recorded PSO the warm replays on this GPU: every blob it names is in the recording or the install, and no
    /// shader is another vendor's permutation (<see cref="VendorOnly"/>). The others aren't counted as the plan's pipelines.</summary>
    bool Replays(Rec r) => Rehydrate.References([r]).All(h => h == Zero || recBlobs.ContainsKey(h) || bc.ContainsKey(h)) && !Parse(r).Stages.Values.Any(VendorOnly);

    /// <summary>The recorded state objects but the community-only ones: what a library's collection counts as recorded by.</summary>
    IEnumerable<Rec> LocalStateObjects => recording?.CommunityStateObjects is { } c ? stateObjects.Where(r => !c.Contains(r.Key)) : stateObjects;

    /// <summary>The recorded state objects the warm replays (<see cref="Planner.Materialize"/>'s rule): every blob each names in
    /// the recording or the install, and every record it builds on replayable too (a record follows the ones it builds on).</summary>
    List<Rec> ReplayableStateObjects()
    {
        var synthesizedLibs = rtItems.Select(i => RtCollections.ParseItem(i).Library)
            .Concat(hitGroupItems.Select(RedEngine.RedRayTracing.ParseItem).SelectMany(h => new[] { h.ClosestHit, h.AnyHit }).OfType<string>()).ToHashSet();
        var left = Planner.CommunityLeftOut(stateObjects, recording?.CommunityStateObjects, synthesizedLibs.Contains);
        // a left-out collection's key is the synthesized one's where the rule rebuilds it (Materialize's synthesized keys)
        var kept = stateObjects.Where(r => left.Contains(r.Key) && RtCollections.Read(r) != null).Select(r => r.Key).ToHashSet();
        foreach (var r in stateObjects.Where(r => !left.Contains(r.Key)))
            if (Rehydrate.References([r]).All(h => h == Zero || recBlobs.ContainsKey(h) || bc.ContainsKey(h)) && ParseStateObject(r).Depends.All(kept.Contains))
                kept.Add(r.Key);
        return stateObjects.Where(r => kept.Contains(r.Key) && !left.Contains(r.Key)).ToList();
    }

    static bool IsCompute(SortedDictionary<int, string> stages) => stages.Count == 1 && stages.ContainsKey((int)Stage.Compute);

    /// <summary>A compute PSO whose shader carries Unreal's NVIDIA extension flag (5.8+) and whose root signature has the
    /// extension table (u0 space 1001): Unreal creates it with the extension slot set, as Gears of War: E-Day's recording
    /// shows for all 12,867 of its compute PSOs with that table.</summary>
    bool NvFlagged(string rs, SortedDictionary<int, string> stages) =>
        IsCompute(stages) && bc.TryGetValue(stages[(int)Stage.Compute], out var cs) && (cs.Counts.Flags & Unreal.ShaderContainer.UeFlags.NvIntrinsics) != 0
        && (rsBlobs.TryGetValue(rs, out var b) || recBlobs.TryGetValue(rs, out b)) && RootSig.Parse(b).Slots.Any(x => x is { Type: 1, Space: 1001 });

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
            body.AddRange(synthesized.Concat(synthCopies).Where(r => !nvComputeOnly || IsCompute(Parse(r).Stages))
                .Concat(items.Concat(itemCopies).Where(i => !nvComputeOnly || IsCompute(ParseItem(i).Stages)).Select(i => new Rec('P', i)))
                .Select(r => new NvState(r.Key, nv.Slot, nv.Space, 1, nv.Options).ToRec()));
        else if (caps.Profile.StartsWith("nvidia"))
        {
            var flagged = synthesized.Concat(synthCopies).Where(r => Parse(r) is var p && NvFlagged(p.Rs, p.Stages))
                .Concat(items.Concat(itemCopies).Where(i => ParseItem(i) is var p && NvFlagged(p.Rs, p.Stages)).Select(i => new Rec('P', i))).ToList();
            body.AddRange(flagged.Select(r => new NvState(r.Key, 0, 1001, 1, 0).ToRec()));
            if (flagged.Count > 0) log?.Report($"NVAPI: {flagged.Count} compute PSOs whose shader uses NVIDIA's extensions get shader-extension slot 0 space 1001");
        }
        body.AddRange(rtItems.Select(i => new Rec('Y', i)));
        body.AddRange(hitGroupItems.Select(i => new Rec('H', i)));
        body.AddRange(d3d11.Select(h => new Rec('1', D3D11Item(bc[h].Stage, h))));
        body.AddRange(tess11.Select(p => new Rec('2', D3D11Pair(p.Hs, p.Ds))));
        body.AddRange(items11.Select(d => new Rec('3', Draw11Bytes(d))));
        body.AddRange(packEntries);
        var n11 = d3d11.Count + tess11.Count + items11.Count;
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
        var inlineOnly = rayTracing && rtLibs > 0 && stateObjects.Count == 0 && recBlobs.Values.Any(b => Carved.Dxbc.InlineRayTracing(b));
        if (inlineOnly) log?.Report($"ray tracing: the recording traces rays inline and builds no state object: the {rtLibs} DXIL libraries aren't used as played");
        // Unreal 5's hardware Lumen traces inline, compiled with the rest from the game files; its DXIL libraries serve passes
        // a game may never run (path tracing). Townfall's community recordings: 1,193 inline PSOs, no state object, 10,278 libraries
        // a game with no DXIL library in its files traces rays inline (PRAGMATA: 748 RayQuery compute shaders, no library)
        var rtInline = rtLibs == 0 || engine.Family == "Unreal" && engine.Version.StartsWith('5')
            ? maps.Where(m => m.Platform == plat).SelectMany(m => m.Shas).Distinct().Count(h => bc.TryGetValue(h, out var s) && s.InlineRayTracing) : 0;
        var maxSm = Rehydrate.References(body.Concat(replays).Concat(replayable))
            .Select(h => bc.TryGetValue(h, out var s) ? s.ShaderModel.Split('_') is [_, "6", var m] && int.TryParse(m, out var minor) ? new Version(6, minor) : null
                : recBlobs.TryGetValue(h, out var b) ? Carved.Dxbc.DxilShaderModel(b) : null)
            .Max();
        var plan = new Plan(game.Id, index.ContentHash, string.Join(" + ", new[] { plat, n11 > 0 ? $"D3D11 {plat11 ?? "DXBC"}" : "" }.Where(p => p != "")), caps.Profile,
            new PlanStats(pipelines + replayable.Count, items.Count + synthesized.Count + rtItems.Count + hitGroupItems.Count, synthesized.Count, usedRs.Count, dx12 && (verified || embeddedRs > 0 || mapRoots),
                unitsBy[(int)Provenance.Exact], unitsBy[(int)Provenance.Inferred], unitsBy[(int)Provenance.Guessed], layoutCoverage, n11, packNew,
                stats.GetValueOrDefault("rs_uncovered"), rtLibs - rtUnbuilt, inlineOnly || engine.NoRtPipelines || !rayTracing ? 0 : rtLibs - rtCovered - rtUnbuilt,
                StageSets: seen.Count + stats.GetValueOrDefault("cross_product") + unpaired - stats.GetValueOrDefault("rs_unserializable") - stats.GetValueOrDefault(ViShadingRate) - stats.GetValueOrDefault(PsRowsUnlinked) - stats.GetValueOrDefault(PsStencilRef), LeftOut: new[] { "no_rs", "no_template", "no_gs_template", "rs_uncovered", "own_rs_uncovered", "rs_unknown", "stream_output" }.Sum(stats.GetValueOrDefault),
                MiddlewareSharedItems: packShared, RtStateObjects: replayable.Count, RtInline: rtInline,
                RsRule: RootSig.RuleFor(engine)?.Id, BuiltOk: builtOk, BuiltN: builtN, D3D11Draws: items11.Count, Variants: synthCopies.Count + itemCopies.Count + replays.Count - pipelines,
                CacheBudgetLeftOut: cacheLeftOut, RecordedOnly: !pairs, MaxShaderModel: maxSm?.ToString(), RuleFallback: RuleFallback(builtOk, builtN) ? ruleFallback ? fallbackSets : 0 : null),
            Path.Combine(outDir, "plan.bin"));
        budget.Check();
        long written = 0;
        PlanFile.Write(plan, body.Select(r => { budget.Tick(); Steps?.Invoke("plan entries written", ++written, body.Count); return r; }));
        log?.Report($"plan: {items.Count + synthesized.Count} PSOs{(synthCopies.Count + itemCopies.Count > 0 ? $" + {synthCopies.Count + itemCopies.Count} again at other texture filtering settings" : "")}{(rtItems.Count + hitGroupItems.Count > 0 ? $" + {rtItems.Count + hitGroupItems.Count} ray tracing collections" : "")} ({string.Join(", ", stats.Select(s => $"{s.Key} {s.Value}"))}), "
            + $"{synthesized.Count} synthesized templates, {usedRs.Count} root signatures, {n11} DirectX 11 items"
            + (packEntries.Count > 0 ? $", {packEntries.Count} middleware pack PSOs ({packNew} not in the recording)" : "")
            + (n11 > 0 ? $" ({string.Join(", ", d3d11.GroupBy(h => bc[h].Stage).Select(g => $"{g.Count()} {g.Key}").Append(tess11.Count > 0 ? $"{tess11.Count} HS+DS" : "").Where(s => s != ""))})" : "")
            + $", {new FileInfo(plan.FilePath).Length / 1024} KiB -> {plan.FilePath}");
        if (unserializable != null)
            log?.Report($"warning: {stats.GetValueOrDefault("rs_unserializable")} stage sets and {stats.GetValueOrDefault("rt_unserializable")} DXIL libraries left out: "
                + $"the runtime won't serialize the root signature the rule builds for them ({unserializable})");
        if (unbuilt != null)
            log?.Report($"warning: {stats.GetValueOrDefault("rs_unbuilt")} stage sets left out: the rule builds no root signature for their shaders ({unbuilt})");
        if (stats.TryGetValue("rs_uncovered", out var nu))
            log?.Report($"warning: {nu} stage sets left out: their root signature doesn't cover a resource their shaders declare "
                + $"({string.Join(" / ", uncoveredExample.Take(5).Select(e => $"{e.Key}, e.g. {e.Value[..12]}"))}). "
                + (build || ruleFallback ? "This engine's rule doesn't know those ranges" + (recs.Count == 0 ? ", a recording teaches the planner the game's own root signatures" : "")
                    : "A recorded root signature picked by resource counts, from other shaders, doesn't have those ranges"));
        if (stats.TryGetValue("own_rs_uncovered", out var no))
            log?.Report($"{no} stage sets left out: the root signature the game ships with them doesn't cover their shaders "
                + $"({string.Join("; ", ownUncoveredExample.Take(5).Select(e => $"{e.Key}, e.g. {e.Value[..12]}"))})");
        if (viewIdShaders.Any(v => v.Count > 0))
            log?.Report($"view instancing: {viewIdShaders.Sum(v => v.Count)} planned shaders read SV_ViewID: {viewIdShaders[0].Count} as recorded, {viewIdShaders[1].Count} with the rule's"
                + (viewIdShaders[2].Count > 0 ? $", {viewIdShaders[2].Count} without (none recorded, no rule for them)" : ""));
        if (stats.TryGetValue("rs_unknown", out var nk))
            log?.Report($"warning: {nk} stage sets left out: no readable bytes for their root signature in the rules, the recording or the game's index (e.g. {unknownRs![..12]})");
        return plan;
    }

    bool MeshTier(SortedDictionary<int, string> stages) => platOfSha.GetValueOrDefault(stages.Values.First())?.Split(' ')[0] == "PCD3D_SM6"; // and its wave<N> platforms
    SortedDictionary<Stage, ShaderInfo> Infos(SortedDictionary<int, string> stages) => new(stages.ToDictionary(s => (Stage)s.Key, s => bc[s.Value]));
    string PsOut(SortedDictionary<int, string> stages) => stages.TryGetValue((int)Stage.Pixel, out var ps) ? Planner.Sig(bc[ps].Outputs, false) : "";
    (uint[], uint) Targets(SortedDictionary<int, string> stages) => stages.TryGetValue((int)Stage.Pixel, out var ps) ? Planner.Targets(bc[ps]) : ([], 0);
}

/// <summary>A recording's blobs by SHA-1, in the order and with the bytes a dictionary filled from every 'B' record would
/// have. Root signatures are held, the other blobs (shaders, the most of a rehydrated recording) are read back from the
/// file when asked: a raw proxy db, open until disposed, which nothing writes while a compile plans from it. A compact
/// recording can't be read at an offset: its blobs are all held.</summary>
sealed class RecordedBlobs : IReadOnlyDictionary<string, byte[]>, IDisposable
{
    readonly Dictionary<string, (byte[]? Bytes, long At, int Length)> blobs = [];
    FileStream? file;

    /// <summary>The records of the db at <paramref name="path"/> but its 'B' records, which this keeps, read as
    /// <see cref="Read(string)"/> reads them.</summary>
    public IEnumerable<Rec> Read(string path)
    {
        if (IsCompact(path))
        {
            foreach (var r in PsoDb.Read(path))
                if (r.Tag == 'B') blobs[Hex(r.Payload.AsSpan(0, 20))] = (r.Payload[20..], 0, 0);
                else yield return r;
            yield break;
        }
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var head = new byte[5];
        while (file.ReadAtLeast(head, 5, false) == 5)
        {
            var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
            if (head[0] == 'B' && len < 20) throw new InvalidDataException($"a 'B' record of {len} bytes, short of its SHA-1");
            if (len > file.Length - file.Position) yield break;   // a torn tail, as PsoDb.Read
            var at = file.Position;
            if (head[0] == 'B' && len > 20 + HashOnly.MaxRootSignature)   // a shader: its bytes stay in the file
            {
                var sha = new byte[20];
                file.ReadExactly(sha);
                file.Seek(len - 20, SeekOrigin.Current);
                blobs[Hex(sha)] = (null, at + 20, (int)len - 20);
                continue;
            }
            if (Body(file, len) is not { } body) yield break;
            if (head[0] != 'B') yield return new Rec((char)head[0], body);
            else if (len > 20 && Carved.Dxbc.IsRootSignatureOnly(body.AsSpan(20))) blobs[Hex(body.AsSpan(0, 20))] = (body[20..], 0, 0);
            else blobs[Hex(body.AsSpan(0, 20))] = (null, at + 20, (int)len - 20);
        }
    }

    byte[] Bytes((byte[]? Bytes, long At, int Length) b)
    {
        if (b.Bytes != null) return b.Bytes;
        var bytes = new byte[b.Length];
        for (var got = 0; got < bytes.Length;)
        {
            var n = RandomAccess.Read(file!.SafeFileHandle, bytes.AsSpan(got), b.At + got);
            if (n == 0) throw new InvalidDataException($"{file.Name} ends inside a blob");
            got += n;
        }
        return bytes;
    }

    public bool Remove(string sha1) => blobs.Remove(sha1);
    public byte[] this[string key] => Bytes(blobs[key]);
    public IEnumerable<string> Keys => blobs.Keys;
    public IEnumerable<byte[]> Values => blobs.Values.Select(Bytes);
    public int Count => blobs.Count;
    public bool ContainsKey(string key) => blobs.ContainsKey(key);

    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out byte[] value)
    {
        value = blobs.TryGetValue(key, out var b) ? Bytes(b) : null;
        return value != null;
    }

    public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator() => blobs.Select(b => KeyValuePair.Create(b.Key, Bytes(b.Value))).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => file?.Dispose();
}
