using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Rage;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>Expands a game's shader library into every PSO it can create. The driver cache is
/// keyed per (shader stages + root signature), so each generated PSO = a template of the same shape with shaders swapped:
///   - every compute shader
///   - every VS->PS, MS->PS and VS->GS->PS chain inside a shader map (Global: across maps) whose signatures link
///     (GS chains: the primitive topology type of the GS's input; MS: <see cref="MeshFeeds"/>), and VS->HS->DS(->GS)(->PS) as patch lists (<see cref="TessPairs"/>)
///   - Unreal 5: VS->PS and MS->PS across maps where one of the two is shared by many maps (a default material's)
///   - every VS/MS alone and every VS->GS without a PS (depth passes); AS->MS(->PS) inside a map
///   - (NVIDIA) a ray tracing collection per DXIL library ('Y', <see cref="RtCollections"/>)
/// Templates are recorded PSOs, or (StateIndependentCache + a root-signature rule, RootSig) synthesized neutral-state
/// pipeline streams. Root signatures are rebuilt from shader resource counts (RootSig), verified against the recording
/// when there is one; if they don't rebuild, a lookup learned from the recording is used instead.
/// ponytail: a PS that reads none of its VS/GS outputs only pairs with sources without attribute outputs. Pairing it with
/// every source would catch 2 more of FF7's 994 recorded PSOs for +22.8k plan PSOs (+19%, measured); revisit per game.
/// DirectX 11 games (and games that may run on either) also get every D3D11 shader of the game once: see <see cref="D3D11Cache"/>.</summary>
public sealed class Planner(string? packDir = null, string? sharedPackDir = null) : IPlanner
{
    /// <summary>Middleware packs (<see cref="MiddlewarePacks"/>): filled from recordings, seeding the plan of every game
    /// with the same middleware DLL version; null = off (the default: tests and tools opt in).</summary>
    public MiddlewarePacks? Packs { get; } = packDir == null ? null : new MiddlewarePacks(packDir);
    /// <summary>The shared packs downloaded for this PC's GPU vendor: they seed
    /// plans like <see cref="Packs"/>, and are never promoted into or uploaded; null = none.</summary>
    public MiddlewarePacks? SharedPacks { get; } = sharedPackDir == null ? null : new MiddlewarePacks(sharedPackDir);

    /// <summary>What decides a plan's pack items (<see cref="MiddlewarePacks.Fingerprint"/>), of both kinds of pack;
    /// <paramref name="shared"/>: the shared packs' as taken earlier (<see cref="SharedFingerprint"/>), else now.</summary>
    public string PackFingerprint(Game game, string? shared = null) =>
        (Packs?.Fingerprint(game) ?? "") + ((shared ?? SharedFingerprint(game)) is { Length: > 0 } s ? "|shared:" + s : "");

    public string SharedFingerprint(Game game) => SharedPacks?.Fingerprint(game) ?? "";

    /// <summary>A plan of this engine takes pack entries: middleware pipelines are D3D12 PSOs. With <see cref="MiddlewarePacks.Runs"/>,
    /// the one rule for what packs seed, what counts as still to compile, and the upscaler chip.</summary>
    public static bool SeedsPacks(EngineInfo engine) => engine.GraphicsApi.Contains("D3D12");

    /// <summary>The distinct PSOs both kinds of pack hold for this DLL version that seed a plan on this GPU vendor.</summary>
    public int PackPipelines(MiddlewareDll dll, bool? amd = null) =>
        new[] { Packs, SharedPacks }.SelectMany(p => p?.Keys(dll, amd) ?? []).Distinct().Count();
    /// <summary>Materialize's pack line (entries written / skipped); optional.</summary>
    public IProgress<string>? Log { get; set; }

    /// <summary>Bump when the plan for the same game and inputs changes (new pipeline kinds, root-signature rules, D3D11):
    /// the app then rebuilds plans (warmed games' when idle, ScsKiller.CheckPlans) and offers a re-warm only where the new
    /// plan has records the warm didn't replay.</summary>
    public const int Version = 42;

    /// <summary>The vendor's D3D11 driver cache persists across processes, is keyed on the exe file name and caches per
    /// shader, whatever the state or the other stages (measured on NVIDIA and Intel, proxy/probe11.cpp): a staged warm
    /// that creates and draws/dispatches each shader once fills it. ponytail: measured for "nvidia-1" and "intel-1" only;
    /// becomes the proposed VendorCaps.D3D11CacheKeyedByExeName once that's in the contract.</summary>
    internal static bool D3D11Cache(VendorCaps caps) => D3D11Stages(caps) != null;

    /// <summary>The D3D11 stages a staged warm compiles the way the game uses them; null when the vendor's D3D11 cache isn't
    /// measured. NVIDIA: every stage. Intel: every stage too (probe11: a state change costs under 1 ms). AMD
    /// (proxy/layout11.cpp): the PS and CS whatever the state; a VS compiles again for
    /// the format, offset, slot and step rate of each element it reads and for the topology class, which its bytecode
    /// doesn't say, so it needs the game's layouts; a GS, HS or DS is compiled with the VS before it.</summary>
    public static Stage[]? D3D11Stages(VendorCaps caps) => caps.Profile switch
    {
        "nvidia-1" or Vendors.IntelBackend.Profile => [Stage.Vertex, Stage.Pixel, Stage.Domain, Stage.Hull, Stage.Geometry, Stage.Compute],
        "amd-1" => [Stage.Pixel, Stage.Compute],
        _ => null,
    };

    /// <summary>A staged D3D11 warm compiles the vertex shaders too (else they need the game's recorded layouts).</summary>
    public static bool D3D11CoversVs(VendorCaps caps) => D3D11Stages(caps)?.Contains(Stage.Vertex) != false;

    public const string D3D11VsRecord = "a 5-min recording covers those";

    /// <summary>The vendor's driver caches a ray tracing collection on its own, so a collection compiled by the warm makes
    /// the game's pipelines linking it cheap (NVIDIA, selftest dxr: per-shader collections cached across processes,
    /// linking them &lt; 1 ms; AMD caches whole state objects only): <see cref="VendorCaps.RtCacheGranularity"/>.</summary>
    internal static bool RtCollectionCache(VendorCaps caps) => caps.RtCacheGranularity == RtCacheGranularity.Collection;

    /// <summary>A record a compile without ray tracing leaves out of the plan, the warm and the count of new pipelines: a
    /// ray tracing state object, recorded ('R', 'A') or a planned collection ('Y', 'H'). A PSO that traces rays inline (RayQuery) stays.</summary>
    public static bool LeftOutWithoutRt(Rec r) => IsStateObject(r.Tag) || r.Tag is 'Y' or 'H';

    /// <summary>A shader D3D11 takes: VS/PS/DS/HS/GS/CS in DXBC up to SM 5.0 (5.1 and DXIL are D3D12 only).</summary>
    internal static bool IsD3D11(ShaderInfo s) => s.Stage <= Stage.Compute && s.ShaderModel[^3..] is "4_0" or "4_1" or "5_0";

    public const string NoRecording = "no recording needed";
    /// <summary>The plan comes from a rule no game has confirmed yet (<see cref="RootSig.Verified"/>). A scan keeps its
    /// check, so ScsKiller.Evaluate turns it into <see cref="NoRecording"/> once the list confirms the engine.</summary>
    public const string Untested = UntestedNote + ": playing with recording on improves it";
    /// <summary><see cref="Untested"/> without the recording hint: what an anti-cheat game, which can't be recorded, is told.</summary>
    public const string UntestedNote = "not tested on this engine version yet";
    public const string Record = "turn on recording and play for about 5 minutes";
    /// <summary>Ends the reason of a game that may run on DirectX 11, where the recorder never loads.</summary>
    public const string Dx12Only = "helps only when played on DirectX 12 (the game may run on DirectX 11)";

    public PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps)
    {
        if (engine.Unsupported != null) return new(Readiness.Unsupported, engine.Unsupported);
        if (engine.Encrypted) return new(Readiness.Unsupported, "encrypted game files");
        if (engine.Family == Rage4Reader.Family)
            return caps.Profile.StartsWith("nvidia", StringComparison.OrdinalIgnoreCase)
                ? new(Readiness.Ready, "compiles GTA IV's D3D9 shaders through its DXVK Vulkan renderer")
                : new(Readiness.Unsupported, "GTA IV D3D9/DXVK warming is currently supported on NVIDIA only");
        // GraphicsApi: "D3D12[ (why)]", "D3D11[ (why)]", "Vulkan…", or "D3D11 or D3D12" when the game offers both (UnrealRhi)
        var api = engine.GraphicsApi;
        var dx11 = api.StartsWith("D3D11") && D3D11Cache(caps);
        // a vendor whose VS needs the game's layouts (AMD): they come from a D3D11 recording ('D' draws)
        var noVs = !D3D11CoversVs(caps) && !(dx11 && recording != null && HasDraws11(recording));
        var all11 = "compiles every DirectX 11 shader" + (noVs ? " but the vertex shaders" : "");
        if (!api.Contains("D3D12"))
            return dx11 ? new(Readiness.Ready, all11 + api[5..] + (noVs ? "; " + D3D11VsRecord : ""))
                : new(Readiness.Unsupported, $"runs on {(api.StartsWith("D3D11") ? "DirectX 11" + api[5..] : api)}");
        var dx12 = CheckD3D12(engine, recording, caps, api.StartsWith("D3D12") || dx11 ? "" : "; " + Dx12Only);
        if (!dx11) return dx12;
        // may run on either: DX11 shaders help whatever DX12 still needs (a recording can come later)
        return new(Readiness.Ready, dx12.Readiness == Readiness.Ready ? $"{dx12.Reason}; also {all11} (the game may run on either)"
            : $"{all11} (the game may run on either); for DirectX 12, {dx12.Reason}");
    }

    static PlanCheck CheckD3D12(EngineInfo engine, Recording? recording, VendorCaps caps, string maybe)
    {
        if (!caps.CacheKeyedByExeName) return new(Readiness.Unsupported, "not supported on this GPU yet");
        // EmbeddedRootSignatures: any reader of shaders that carry them (carved, FromSoftware) or of records that name them (Dawn)
        if (caps.StateIndependentCache && (RootSig.Verified(engine) || engine.Version.EndsWith(CarvedReader.EmbeddedRootSignatures)))
            return new(Readiness.Ready, NoRecording + maybe);
        if (recording != null && File.Exists(recording.DbPath) && new FileInfo(recording.DbPath).Length > 0)
            return caps.StateIndependentCache || HasDraws(recording) ? new(Readiness.Ready, "planned from a recording" + maybe)
                : new(Readiness.NeedsRecording, "the recording has no draws: play into the game world" + maybe); // no vertex layouts to learn
        if (caps.StateIndependentCache && RootSig.RuleFor(engine) != null) return new(Readiness.Ready, Untested + maybe);
        return new(Readiness.NeedsRecording, Record + maybe);
    }

    /// <summary>The recording has a graphics PSO with a vertex shader (a menu-only session may have none): a state-dependent
    /// cache needs its input layouts.</summary>
    static bool HasDraws11(Recording recording) => File.Exists(recording.DbPath) && Read(recording.DbPath).Any(r => r.Tag == 'D');

    /// <summary>The DirectX a recording shows the game ran: a D3D12 pipeline ("D3D12"; it wins over D3D11 draws, which an
    /// earlier DirectX 11 run may have left), else a D3D11 recorder's draw ("D3D11"); null = neither, or unreadable.</summary>
    public static string? RecordedApi(string db)
    {
        try
        {
            bool draws11 = false;
            foreach (var r in Read(db))
                if (r.Tag is 'G' or 'C' or 'S') return "D3D12";
                else draws11 |= r.Tag == 'D';
            return draws11 ? "D3D11" : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    static bool HasDraws(Recording recording)
    {
        foreach (var r in Read(recording.DbPath))
            try { if (r.Tag is 'G' or 'S' && Parse(r).Stages.ContainsKey((int)Stage.Vertex)) return true; }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException) { } // a record from a newer proxy
        return false;
    }

    /// <summary>The rules plans are built with; null: <see cref="RulesFile.Current"/>.</summary>
    public RulesFile? Rules { get; init; }

    /// <summary>A plan under this share of the items the embedded rules plan for the game keeps the embedded rules: a wrong
    /// server description makes stage sets uncovered or unserializable, and a plan that adds nothing keeps a game Warmed.</summary>
    public const double MinRulesShare = 0.9;

    /// <summary>A plan over this many items (and over the embedded rules' plan) keeps the embedded rules. A plan has one
    /// root-signature rule and one ray tracing rule, so this bounds each rule too. The Witcher 3's whole plan: 62,815.</summary>
    public const long MaxRulesItems = 1_000_000;

    /// <summary>The managed memory a build may take; null: <see cref="PlanBudget.DefaultMemory"/> of this PC's RAM.</summary>
    public long? MemoryBudget { get; init; }
    /// <summary>The time a build may take; null: <see cref="PlanBudget.DefaultTime"/>.</summary>
    public TimeSpan? TimeBudget { get; init; }
    /// <summary>The driver cache one game's plan may fill where state is in the key (AMD, Intel); null:
    /// <see cref="PlanBuilder.CacheBudgetOf"/> the vendor.</summary>
    public long? CacheBudget { get; init; }

    /// <summary>With rules other than the embedded ones for this engine, also plans with the embedded ones and keeps those,
    /// saying why in the log, when the other plan fails, is under <see cref="MinRulesShare"/> of theirs or over <see cref="MaxRulesItems"/>.
    /// Both builds share one <see cref="PlanBudget"/>: over it, a plan of only the recorded pipelines (and the packs'), so the
    /// game still compiles those; with none of those, <see cref="PlanTooBigException"/> and no plan file.</summary>
    public Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir,
        IProgress<string>? log, CancellationToken ct, bool maximum = false, bool rayTracing = true)
    {
        try { return Build(game, engine, index, recording, caps, outDir, log, ct, maximum, rayTracing, pairs: true); }
        catch (PlanTooBigException e)
        {
            log?.Report($"warning: {e.Message}: planning only the recorded pipelines");
            var plan = Build(game, engine, index, recording, caps, outDir, log, ct, maximum, rayTracing, pairs: false);
            if (plan.Stats.Recorded + plan.Stats.MiddlewareItems > 0) return plan;
            File.Delete(plan.FilePath);
            throw;
        }
    }

    Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir,
        IProgress<string>? log, CancellationToken ct, bool maximum, bool rayTracing, bool pairs)
    {
        var budget = new PlanBudget(MemoryBudget ?? PlanBudget.DefaultMemory(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), TimeBudget ?? PlanBudget.DefaultTime);
        var rules = Rules ?? RulesFile.Current;
        if (rules.Hash(engine) == RulesFile.Embedded.Hash(engine)) return Kept(One(rules, outDir, log));
        void Keep(string why) => log?.Report($"warning: planning rules: the server's rules for {engine.Family} {engine.Version} {why}: this game keeps the app's own rules");
        (Plan Plan, string? Fingerprint) other;
        try { other = One(rules, outDir, log); }
        catch (Exception e) when (e is not (OperationCanceledException or PlanTooBigException))   // validated at load, yet never a failed compile
        {
            Keep($"build no plan ({e.Message})");
            return Kept(One(RulesFile.Embedded, outDir, log));
        }
        var dir = Path.Combine(outDir, "embedded-rules");
        try
        {
            (Plan Plan, string? Fingerprint) own;
            try { own = One(RulesFile.Embedded, dir, null); }   // the same inputs: its log would repeat the plan's
            catch
            {
                try { File.Delete(other.Plan.FilePath); }   // not compared, so not kept
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the failure's own reason is what the user needs
                throw;
            }
            var (n, m) = (other.Plan.Stats.Generated, own.Plan.Stats.Generated);
            var why = n < MinRulesShare * m ? $"under {MinRulesShare:P0} of the {m:N0} the app's own rules plan"
                : n > Math.Max(MaxRulesItems, m) ? $"over the cap of {MaxRulesItems:N0}" : null;
            if (why == null) return Kept(other);
            Keep($"plan {n:N0} item{(n == 1 ? "" : "s")}, {why}");
            File.Move(own.Plan.FilePath, other.Plan.FilePath, overwrite: true);
            return Kept((own.Plan with { FilePath = other.Plan.FilePath }, own.Fingerprint));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        Plan Kept((Plan Plan, string? Fingerprint) p)
        {
            log?.Report(budget.ToString());
            if (p.Fingerprint is { } f) seeded.AddOrUpdate(p.Plan, f);
            return p.Plan;
        }

        (Plan Plan, string? Fingerprint) One(RulesFile r, string dir, IProgress<string>? l)
        {
            var builder = new PlanBuilder(game, engine, index, recording, caps, dir, l, ct, maximum, Packs, SharedPacks, r, budget, CacheBudget, rayTracing, pairs);
            return (builder.Build(), builder.PackFingerprint);
        }
    }

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Plan, string> seeded = new();

    /// <summary>The <see cref="PackFingerprint"/> of what <paramref name="plan"/>'s build seeded from; null when it read no pack.</summary>
    public string? SeededFingerprint(Plan plan) => seeded.TryGetValue(plan, out var f) ? f : null;

    /// <summary>Render targets per pixel shader output (float -> RGBA16F, uint/sint -> RGBA32 UINT/SINT), D32 if it writes depth.</summary>
    internal static (uint[] Rt, uint Dsv) Targets(ShaderInfo ps)
    {
        var outs = ps.Outputs;
        var targets = outs.Where(o => o.SysValue == 64 || o.Semantic.Equals("SV_Target", StringComparison.OrdinalIgnoreCase)).ToList();
        var rt = new uint[targets.Count == 0 ? 0 : targets.Max(o => o.Index) + 1];
        foreach (var o in targets) rt[o.Index] = o.CompType switch { 1 => R32G32B32A32Uint, 2 => R32G32B32A32Sint, _ => R16G16B16A16Float };
        return (rt, outs.Any(o => o.SysValue is 65 or 67 or 68) ? D32Float : 0); // SV_Depth, SV_DepthGreaterEqual, SV_DepthLessEqual
    }

    /// <summary>A plan file's records; none when it's missing or unreadable.</summary>
    public static List<Rec> PlanBody(string? planFile)
    {
        try { return planFile != null && File.Exists(planFile) ? [.. PlanFile.Read(planFile).Records] : []; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { return []; }
    }

    /// <summary>What a plan compiles, one per pipeline: each record but root signatures and NVAPI states, keyed by its 'N'
    /// record's key when it has one (the NVAPI state is in NVIDIA's cache key: the same PSO without it is another compile).</summary>
    public static IEnumerable<(string Key, Rec Rec)> PlanInputs(IReadOnlyList<Rec> body)
    {
        var nv = new Dictionary<string, string>();
        foreach (var r in body.Where(r => r.Tag == 'N')) nv[NvState.Parse(r).Target] = r.Key;
        return body.Where(r => r.Tag is not ('B' or 'N')).Select(r => (nv.GetValueOrDefault(r.Key, r.Key), r));
    }

    public void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var mainDb = Path.Combine(workDir, "scskiller.db");
        if (recording != null) CopyRaw(recording.DbPath, mainDb);
        else File.WriteAllBytes(mainDb, []);
        var inMain = new HashSet<string>(); // blob hashes and PSO keys already in scskiller.db
        // 'L' (a community recording's): the record names a shader only a recording on this PC has, not for the proxy
        var localOnly = new HashSet<string>();
        foreach (var r in Read(mainDb))
        {
            inMain.Add(r.Tag == 'B' ? Hex(r.Payload.AsSpan(0, 20)) : r.Key);
            if (r.Tag == 'L') localOnly.Add(HashOnly.Target(r));
        }

        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var templates = body.Where(r => r.Tag is 'G' or 'C' or 'S' && !inMain.Contains(r.Key)).ToList();
        var rt = body.Where(r => r.Tag == 'Y').Select(r => RtCollections.ParseItem(r.Payload)).ToList(); // ray tracing collections: need their library's exports
        var hitGroups = body.Where(r => r.Tag == 'H').Select(r => RedEngine.RedRayTracing.ParseItem(r.Payload)).ToList(); // REDengine 3's and FromSoftware's: need both libraries' exports
        var rtLibs = rt.Select(y => y.Library).Concat(hitGroups.SelectMany(h => new[] { h.ClosestHit, h.AnyHit }).OfType<string>()).ToHashSet();
        var rtBytes = new Dictionary<string, byte[]>();
        if (rtLibs.Count > 0) foreach (var r in Read(mainDb)) if (r.Tag == 'B' && rtLibs.Contains(Hex(r.Payload.AsSpan(0, 20)))) rtBytes[Hex(r.Payload.AsSpan(0, 20))] = r.Payload[20..];
        var shaders = templates.Select(Parse).SelectMany(t => t.Stages.Values.Append(t.Rs))
            .Concat(body.Where(r => r.Tag == 'P').Select(r => ParseItem(r.Payload)).SelectMany(i => i.Stages.Values.Append(i.Rs)))
            .Concat(rtLibs)
            .Concat(Rehydrate.References(Read(mainDb).Where(r => IsStateObject(r.Tag)))).ToHashSet();   // the recorded state objects' libraries: a recording may leave out the install's
        shaders.ExceptWith(inMain);
        shaders.ExceptWith(body.Where(r => r.Tag == 'B').Select(r => Hex(r.Payload.AsSpan(0, 20)))); // root signatures the plan doesn't carry are the game's own (RTS0): pulled like shaders
        shaders.UnionWith(Rehydrate.References(body.Where(r => r.Tag is '1' or '2' or '3'))); // a D3D11 item's blobs are in gen.db itself
        shaders.Remove(Zero);

        // blobs first, then templates, then items: the proxy resolves items against templates as it loads
        using var gen = new BufferedStream(File.Create(Path.Combine(workDir, "scskiller_gen.db")), 1 << 20);
        var planBlobs = body.Where(r => r.Tag == 'B').Select(r => Hex(r.Payload.AsSpan(0, 20))).ToHashSet();
        var written = new HashSet<string>(inMain);
        foreach (var r in body.Where(r => r.Tag == 'B' && !inMain.Contains(Hex(r.Payload.AsSpan(0, 20))))) { Write(gen, 'B', r.Payload); written.Add(Hex(r.Payload.AsSpan(0, 20))); }
        reader.ReadShaders(game, engine, shaders, (h, b) => { lock (gen) { WriteBlob(gen, h, b); written.Add(h); if (rtLibs.Contains(h)) rtBytes[h] = b; } }, ct); // missing ones: skipped below
        // middleware pack entries: shader bytes from this install's copy of the DLL, entries it lacks a shader of are skipped
        var packed = body.Where(r => r.Tag == 'M').ToList();
        long skipped = 0;
        if (packed.Count > 0)
        {
            var m = MiddlewarePacks.Materialize(packed, game, planBlobs, written, inMain, gen);
            Log?.Report($"middleware packs: {m.Written} of {m.Entries} entries written, {m.AlreadyRecorded} already in the recording"
                + (m.FilledFromDll > 0 ? $" ({m.FilledFromDll} with their shaders from the DLL)" : "")
                + (m.Skipped > 0 ? $", {m.Skipped} skipped (shader not in this install's {string.Join(", ", m.SkippedByDll.Select(s => $"{s.Key} ({s.Value})"))})" : ""));
            skipped += m.Skipped - (m.DropFromMain?.Count ?? 0); // the recorded ones are counted with the recording below
        }

        // Whatever still names a blob neither file carries (a shader not in this install: another build, built at run time,
        // a DLL without it) is skipped, not replayed: the warm's "failed" means only that the driver rejected something.
        // A template stays while a kept item uses it (the item may swap the missing stage).
        bool Resolved(Rec r) => Rehydrate.References([r]).IsSubsetOf(written);
        var items = body.Where(r => r.Tag is 'P' or '1' or '2' or '3').ToList();
        var keptItems = items.Where(Resolved).ToList();
        var usedTemplates = keptItems.Where(r => r.Tag == 'P').Select(r => ParseItem(r.Payload).Template).ToHashSet();
        bool Keep(Rec r) => usedTemplates.Contains(r.Key) || Resolved(r);
        // scskiller.db streamed twice, its shader bytes never held: what stays, then, when anything goes, the rewrite. 'W' (a
        // layer's pairing of two records that both replay) stays: a warm through the layer creates what the layer made under it.
        // A ray tracing state object also needs the records it builds on (its collections, its base): in file order they come
        // first, so one pass drops the dependents of a dropped one too
        long mainCount = 0, flagCount = 0, keptCount = 0, needsRecording = 0;
        var keptKeys = new HashSet<string>();
        var recordedPayloads = new List<uint>();
        var nvOfMain = new List<Rec>();   // its state objects and 'N' records: what the option fixes learn from (RtCollections.OptionFixes)
        bool Stays(Rec r)
        {
            if (r.Tag is 'B' or 'W') return true;
            if (r.Tag == 'L' || !Keep(r) || IsStateObject(r.Tag) && !ParseStateObject(r).Depends.All(keptKeys.Contains)) return false;
            keptKeys.Add(r.Key);
            return true;
        }
        foreach (var r in Read(mainDb))
        {
            mainCount++;
            if (RtCollections.Read(r) is { } c) recordedPayloads.Add(c.Payload);
            if (r.Tag == 'N' || IsStateObject(r.Tag)) nvOfMain.Add(r);
            if (Stays(r)) keptCount++;
            else if (r.Tag == 'L') flagCount++;
            else if (localOnly.Contains(r.Key)) needsRecording++;
        }
        if (keptCount < mainCount)
        {
            keptKeys.Clear();   // the same walk again, writing
            var tmp = mainDb + ".tmp";
            try
            {
                using (var f = new BufferedStream(File.Create(tmp), 1 << 20))
                    foreach (var r in Read(mainDb))
                        if (Stays(r)) Write(f, r.Tag, r.Payload);
                File.Move(tmp, mainDb, true);
            }
            finally { File.Delete(tmp); }
        }
        var keptTemplates = templates.Where(Keep).ToList();
        skipped += mainCount - flagCount - keptCount + templates.Count - keptTemplates.Count + items.Count - keptItems.Count;
        foreach (var r in keptTemplates) Write(gen, r.Tag, r.Payload);
        foreach (var r in keptItems) Write(gen, r.Tag, r.Payload);
        var keptPsos = keptTemplates.Concat(keptItems).Select(r => r.Key).ToHashSet();
        foreach (var r in body.Where(r => r.Tag == 'N' && keptPsos.Contains(NvState.Parse(r).Target))) Write(gen, r.Tag, r.Payload); // the NVAPI state they're created with
        // the recorded state objects' NVAPI creation options where the recording lost them, learned from the whole recording
        // as WarmInputs does; read after the main db's, they win
        foreach (var r in RtCollections.OptionFixes(nvOfMain).Where(r => keptKeys.Contains(NvState.Parse(r).Target))) Write(gen, r.Tag, r.Payload);
        var rtOptions = RtCollections.RecordedOptions(nvOfMain);
        // a ray tracing collection per library ('Y'), its exports read from the library: an 'R' record the warm creates like a
        // recorded one; a library not in this install, or without a ray tracing entry point, is skipped. With rule payload 0 a
        // ray generation library gets one per payload the other libraries have (RtCollections.Payloads), the recorded ones'
        // (no 'Y': their collections replay from the recording) included
        var own = rt.Select(y => y.Library).Distinct().Where(rtBytes.ContainsKey).ToDictionary(h => h, h => RtCollections.OwnPayload(rtBytes[h]));
        var pipelinePayloads = rt.Where(y => y.Payload == 0).Select(y => own.GetValueOrDefault(y.Library))
            .Concat(recordedPayloads).Where(p => p > 0).Distinct().Order().ToList();
        foreach (var y in rt)
        {
            var n = 0;
            if (rtBytes.TryGetValue(y.Library, out var lib))
                foreach (var p in RtCollections.Payloads(y.Payload, own[y.Library], pipelinePayloads))
                    if (RtCollections.Collection(lib, y.Library, y.Global, y.LocalRayGen, y.LocalOther, p, y.Attributes, y.Depth, y.Flags) is { } so)
                    {
                        Write(gen, 'R', so);
                        if (RtCollections.WithOptions(y.Nv, rtOptions) is { } nv) { var r = new NvState(new Rec('R', so).Key, nv.Slot, nv.Space, 2, nv.Options).ToRec(); Write(gen, r.Tag, r.Payload); }
                        n++;
                    }
            if (n == 0) skipped++;
        }
        foreach (var h in hitGroups)
        {
            if (rtBytes.TryGetValue(h.ClosestHit, out var ch) && (h.AnyHit == null || rtBytes.ContainsKey(h.AnyHit))
                && (engine.Family == FromSoft.FromSoftReader.Family ? FromSoft.SoulsRayTracing.Collection(ch, h, h.AnyHit != null ? rtBytes[h.AnyHit] : [])
                    : RedEngine.RedRayTracing.Collection(ch, h, h.AnyHit != null ? rtBytes[h.AnyHit] : [])) is { } so)
            {
                Write(gen, 'R', so);
                if (RtCollections.WithOptions(h.Nv, rtOptions) is { } nv) { var r = new NvState(new Rec('R', so).Key, nv.Slot, nv.Space, 2, nv.Options).ToRec(); Write(gen, r.Tag, r.Payload); }
            }
            else skipped++;
        }
        File.WriteAllText(Path.Combine(workDir, SkippedFile), $"{skipped} {needsRecording}");
        if (skipped > 0) Log?.Report($"{skipped} PSO{(skipped == 1 ? "" : "s")} skipped: a shader not in this install (not replayed, not counted as failed)"
            + (needsRecording > 0 ? $"; {needsRecording} of them flagged by the community recording: built at run time or by a mod, only a recording on this PC has them" : ""));
    }

    /// <summary>Written by <see cref="Materialize"/> into the work folder: how many PSOs it skipped (a shader not in this
    /// install), then how many of those need a recording on this PC (an 'L' flag), as two numbers.</summary>
    public const string SkippedFile = "skipped.txt";

    /// <summary>The PSOs a materialized work folder skipped (<see cref="SkippedFile"/>); 0 when it doesn't say.</summary>
    public static long SkippedIn(string workDir) => SkippedCount(workDir, 0);

    /// <summary>Of <see cref="SkippedIn"/>, those that need a recording on this PC.</summary>
    public static long NeedsRecordingIn(string workDir) => SkippedCount(workDir, 1);

    static long SkippedCount(string workDir, int at)
    {
        try { return long.TryParse(File.ReadAllText(Path.Combine(workDir, SkippedFile)).Split(' ').ElementAtOrDefault(at), out var n) ? n : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>D3D12_PRIMITIVE_TOPOLOGY_TYPE of a GS input D3D_PRIMITIVE (adjacency included); 0 = unknown.</summary>
    internal static uint TopologyType(int gsInput) => gsInput switch { 1 => 1, 2 or 6 => 2, 3 or 7 => 3, _ => 0 };

    /// <summary>The last stage before the rasterizer must write SV_Position: a VS or MS without it only feeds a GS (or
    /// tessellation), and a pipeline where it is the last geometry stage fails to create (E_INVALIDARG; FF7: VSs that pass
    /// ATTRIBUTE0 on to a GS writing SV_RenderTargetArrayIndex).</summary>
    internal static bool Rasterizable(ShaderInfo s) => s.Outputs.Count == 0 || s.Outputs.Any(o => o.SysValue == 1); // no signature read: assume it does

    /// <summary>The last stage before the rasterizer writes SV_Position, or there is none (compute): otherwise the game
    /// streams the stage's output out, which a synthesized pipeline has no declaration for (the runtime: E_INVALIDARG).</summary>
    internal static bool Positioned(IReadOnlyDictionary<Stage, ShaderInfo> st) =>
        new[] { Stage.Geometry, Stage.Domain, Stage.Mesh, Stage.Vertex }.Where(st.ContainsKey).Select(x => st[x]).FirstOrDefault() is not { } last || Rasterizable(last);

    /// <summary>A pipeline's stages share one root signature: shaders that carry theirs (RTS0) must carry the same.</summary>
    internal static bool SameRs(params ShaderInfo[] s) => s.Select(x => x.RootSignature).OfType<string>().Distinct().Count() <= 1;

    internal static void Push(Dictionary<string, List<ShaderInfo>> d, string k, ShaderInfo v) { if (!d.TryGetValue(k, out var l)) d[k] = l = []; l.Add(v); }

    internal static string Shape(params Stage[] stages) => string.Join('+', stages.Select(s => (int)s).Order());
    internal static string Shape(IReadOnlyDictionary<int, string> stages) => string.Join('+', stages.Keys);

    internal static string CountsKey(SortedDictionary<Stage, ShaderInfo> st) => string.Join(';', st.Select(s => $"{(int)s.Key}:{s.Value.Counts}"));

    /// <summary>Signature identity: (SEMANTIC, index, register, component type), system values dropped unless asked; a mesh
    /// shader's per-primitive outputs aren't part of it (<see cref="MeshFeeds"/>).</summary>
    internal static string Sig(IReadOnlyList<SigElement> elems, bool dropSys = true) => string.Join(';', elems.Where(e => (!dropSys || e.SysValue == 0) && !PerPrimitive(e))
        .Select(e => (S: e.Semantic.ToUpperInvariant(), e.Index, e.Register, e.CompType))
        .OrderBy(e => e.S, StringComparer.Ordinal).ThenBy(e => e.Index).ThenBy(e => e.Register).ThenBy(e => e.CompType).Select(e => $"{e.S}/{e.Index}/{e.Register}/{e.CompType}"));

    static readonly int[] FromPrevStage = [2, 3, 4, 5]; // clip/cull distance, render-target/viewport array index: written upstream

    /// <summary>src outputs feed dst inputs: every input is written with a superset component mask (exact for mesh shaders),
    /// including the system values that come from the previous stage (the non-system signature match is the caller's). A
    /// DXBC input read from the previous stage must also sit in the same register, SV_Position included (the runtime rejects
    /// the pair otherwise: "SV_Position is defined for mismatched hardware registers"; Hogwarts Legacy: 9 PSOs of VSs writing
    /// SV_RenderTargetArrayIndex in r0 and SV_POSITION in r1 before a PS reading only SV_Position, r0). A hull or domain
    /// shader's inputs are all control-point data, system values included (Starfield's HS/DS pass SV_Position along).</summary>
    internal static bool Links(ShaderInfo src, ShaderInfo dst, bool exact = false)
    {
        var outs = new Dictionary<(string, int), SigElement>();
        foreach (var o in src.Outputs) outs[(o.Semantic.ToUpperInvariant(), o.Index)] = o;
        var dxbc = dst.ShaderModel.Split('_') is [_, "4" or "5", ..];
        var controlPoints = dst.Stage is Stage.Hull or Stage.Domain;
        foreach (var i in dst.Inputs)
        {
            if (dxbc && i.SysValue == 1 && outs.TryGetValue((i.Semantic.ToUpperInvariant(), i.Index), out var pos) && pos.Register != i.Register) return false;
            if (i.SysValue != 0 && !FromPrevStage.Contains(i.SysValue) && !controlPoints) continue;
            if (!outs.TryGetValue((i.Semantic.ToUpperInvariant(), i.Index), out var o) || (exact ? i.Mask != o.Mask : (i.Mask & ~o.Mask) != 0)) return false;
            if (i.ReadMask != SigElement.UnknownReadMask && o.ReadMask != SigElement.UnknownReadMask && (i.ReadMask & ~o.ReadMask) != 0) return false;
        }
        return true;
    }

    internal static bool PerPrimitive(SigElement e) => e.Register >= Unreal.ShaderContainer.PrimitiveRow; // not -1 (SV_Depth: no register)

    /// <summary>A mesh shader feeds a PS: the PS inputs its per-primitive outputs don't give link as a VS's would (same
    /// signature, exact masks); the ones they give match by semantic, index, component type and exact mask, not by register
    /// (the PS packs them after its own per-vertex rows, which depend on the inputs it declares).</summary>
    internal static bool MeshFeeds(ShaderInfo ms, ShaderInfo ps)
    {
        var prim = ms.Outputs.Where(PerPrimitive).DistinctBy(e => (e.Semantic.ToUpperInvariant(), e.Index)).ToDictionary(e => (e.Semantic.ToUpperInvariant(), e.Index));
        var perVertex = new List<SigElement>();
        foreach (var i in ps.Inputs)
            if (!prim.TryGetValue((i.Semantic.ToUpperInvariant(), i.Index), out var o)) perVertex.Add(i);
            else if (i.SysValue == 0 && o.CompType != i.CompType) return false;
        return Sig(perVertex) == Sig(ms.Outputs) && Links(ms, ps, true);
    }

    /// <summary>An amplification shader sends the payload the mesh shader takes: the same size, else the runtime rejects the
    /// pipeline (E_INVALIDARG, "CreateMeshShader_MismatchedASMSPayloadSize"; PRAGMATA lists its one 128-byte AS with mesh
    /// shaders that take none).</summary>
    internal static bool Launches(ShaderInfo a, ShaderInfo m) => a.Payload == m.Payload;

    /// <summary>The hull -> domain shader pairs of one shader map whose control points link (the HS's output signature is the
    /// DS's input signature, register for register). ponytail: the patch constants, tessellator domain and control-point
    /// count aren't in <see cref="ShaderInfo"/>, so they aren't checked, though the runtime rejects any mismatch
    /// (E_INVALIDARG on WARP, even a patch constant the DS doesn't read): inside one Unreal material map they come from the
    /// material's one tessellation mode (7,935 planned tessellation PSOs of Automation, Atomic Heart, High on Life, Hogwarts
    /// Legacy and Starfield: 0 rejected on WARP). A ShaderInfo field is proposed for a game where that doesn't hold.</summary>
    internal static IEnumerable<(ShaderInfo Hs, ShaderInfo Ds)> TessPairs(IReadOnlyList<ShaderInfo> map)
    {
        var dss = map.Where(d => d.Stage == Stage.Domain).ToLookup(d => Sig(d.Inputs));
        foreach (var h in map.Where(h => h.Stage == Stage.Hull))
            foreach (var d in dss[Sig(h.Outputs)].Where(d => Links(h, d) && SameRs(h, d)))
                yield return (h, d);
    }

    // DXGI R32 / R32G32 / R32G32B32 / R32G32B32A32 by component type (float, uint, sint)
    static readonly Dictionary<int, uint[]> Fmt = new() { [3] = [41, 16, 6, 2], [1] = [42, 17, 7, 3], [2] = [43, 18, 8, 4] };

    public static List<LayoutElem> VsLayout(ShaderInfo vs)
    {
        var list = new List<LayoutElem>();
        var off = 0u;
        foreach (var e in vs.Inputs.Where(e => e.SysValue == 0).OrderBy(e => e.Register))
        {
            var n = Math.Max(1, 32 - BitOperations.LeadingZeroCount((uint)e.Mask));
            list.Add(new LayoutElem(e.Semantic, e.Index, Fmt.GetValueOrDefault(e.CompType, Fmt[3])[n - 1], off));
            off += 4 * (uint)n;
        }
        return list;
    }
}

/// <summary>What one <see cref="Planner.Build"/> may take: the process's managed heap (the index in it) and the wall time.
/// The planner's loops call <see cref="Tick"/>; over either, it throws <see cref="PlanTooBigException"/> before the plan file
/// is written. A library whose shaders all link pairs N x N (a carved archive is one pool): no plan of it would fit.</summary>
public sealed class PlanBudget(long memoryBytes, TimeSpan time)
{
    /// <summary>Half the RAM, at least 4 GiB, at most 32 GiB.</summary>
    public static long DefaultMemory(long ram) => Math.Clamp(ram / 2, 4L << 30, 32L << 30);
    /// <summary>Planning runs at below-normal priority, often while the PC is in use.</summary>
    public static readonly TimeSpan DefaultTime = TimeSpan.FromMinutes(30);

    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    long ticks, collectAfterMs;

    /// <summary>What the planner has done so far, for the message: e.g. "1,234 stage sets".</summary>
    internal Func<string>? Progress { get; set; }

    /// <summary>What the budget bounds and why it ran out, for the message.</summary>
    internal string What { get; init; } = "the plan";
    internal string Why { get; init; } = "this game's shaders combine into more pipelines than a compile can hold";

    /// <summary>The most managed memory a check saw, garbage included.</summary>
    public long Peak { get; private set; }

    public void Tick()
    {
        if ((++ticks & 255) == 0) Check();
    }

    public void Check()
    {
        if (clock.Elapsed > time) throw new PlanTooBigException($"{What} took over {time.TotalMinutes:N0} minutes{At()}");
        var used = GC.GetTotalMemory(false);
        Peak = Math.Max(Peak, used);
        // garbage counts until a collection: only what is live trips it; one collection every 5 s at most, unless a quarter over
        if (used <= memoryBytes || clock.ElapsedMilliseconds < collectAfterMs && used - memoryBytes <= memoryBytes / 4) return;
        GC.Collect();
        collectAfterMs = clock.ElapsedMilliseconds + 5000;
        if (GC.GetTotalMemory(false) > memoryBytes) throw new PlanTooBigException($"{What} outgrew its memory limit of {Gb(memoryBytes)} on this PC{At()}");
    }

    /// <summary>The build's line for the log: its time and memory against the budget.</summary>
    public override string ToString() => $"plan: built in {clock.Elapsed.TotalSeconds:F1}s, managed memory up to {Gb(Peak)} of its {Gb(memoryBytes)} limit";

    static string Gb(long bytes) => $"{bytes / (double)(1L << 30):N1} GB";

    string At() => (Progress?.Invoke() is { } p ? $" at {p}" : "") + ": " + Why;
}

/// <summary>A plan over its <see cref="PlanBudget"/>: the compile fails with this reason, nothing written.</summary>
public sealed class PlanTooBigException(string message) : Exception(message);

/// <summary>plan.bin: "SCSKPLAN", u32 version, u32 header length, header (the <see cref="Plan"/> as JSON), then a
/// Brotli-compressed body of proxy db records: 'B' root signatures, 'G'/'C'/'S' templates, 'P' items, '1' D3D11 shaders, '2' D3D11 HS+DS pairs, '3' D3D11 vertex stages with a recorded layout (<see cref="Draws11"/>), 'Y' ray tracing collections (<see cref="RtCollections.Item"/>), 'H' REDengine 3 and FromSoftware hit group collections (<see cref="RedEngine.RedRayTracing.Item"/>), 'N' NVAPI states, 'M' middleware pack entries (<see cref="MiddlewarePacks.Wrap"/>). Hash-only: shader
/// bytes are pulled from the game at materialize time.</summary>
public static class PlanFile
{
    const int Version = 1;

    public static void Write(Plan plan, IEnumerable<Rec> body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.FilePath)!);
        var tmp = plan.FilePath + ".tmp";
        try
        {
            using (var f = File.Create(tmp))
            {
                WriteHeader(f, "SCSKPLAN"u8, Version, plan);
                using var z = new BrotliStream(f, CompressionLevel.Optimal);
                using var b = new BufferedStream(z, 1 << 20);
                foreach (var r in body) PsoDb.Write(b, r.Tag, r.Payload);
            }
            File.Move(tmp, plan.FilePath, true);
        }
        finally { File.Delete(tmp); }
    }

    public static (Plan Plan, IEnumerable<Rec> Records) Read(string path)
    {
        var f = File.OpenRead(path);
        Plan plan;
        try { plan = ReadHeader<Plan>(f, "SCSKPLAN"u8, Version, $"{path}: not a v{Version} plan") with { FilePath = path }; }
        catch { f.Dispose(); throw; }   // a short or foreign file: nothing holds it open
        return (plan, Records());

        IEnumerable<Rec> Records()
        {
            using (f)
            using (var z = new BrotliStream(f, CompressionMode.Decompress))
                foreach (var r in PsoDb.Read(new BufferedStream(z, 1 << 20))) yield return r;
        }
    }

    /// <summary>A file's head: 8 bytes of magic, u32 version, u32 header length, the header as JSON.</summary>
    internal static void WriteHeader<T>(Stream f, ReadOnlySpan<byte> magic, int version, T header)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header);
        f.Write(magic);
        f.Write(BitConverter.GetBytes(version));
        f.Write(BitConverter.GetBytes(json.Length));
        f.Write(json);
    }

    /// <summary>InvalidDataException(<paramref name="mismatch"/>) when the magic or the version is another.</summary>
    internal static T ReadHeader<T>(Stream f, ReadOnlySpan<byte> magic, int version, string mismatch)
    {
        var head = new byte[16];
        f.ReadExactly(head);
        if (!head.AsSpan(0, 8).SequenceEqual(magic) || BitConverter.ToInt32(head, 8) != version) throw new InvalidDataException(mismatch);
        var json = new byte[BitConverter.ToInt32(head, 12)];
        f.ReadExactly(json);
        return JsonSerializer.Deserialize<T>(json)!;
    }
}
