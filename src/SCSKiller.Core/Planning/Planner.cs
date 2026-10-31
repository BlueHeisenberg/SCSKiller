using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using SCSKiller.Core.Carved;
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

    public string SharedFingerprint(Game game) => Uses(game) ? SharedPacks?.Fingerprint(game) ?? "" : "";

    /// <summary>By game id, its "Use shared shader hashes": off, its plans take no shared pack. Null: on for every game.</summary>
    public Func<string, bool>? UsesShared { get; set; }

    bool Uses(Game game) => UsesShared?.Invoke(game.Id) != false;

    /// <summary>A plan of this engine takes pack entries: middleware pipelines are D3D12 PSOs. With <see cref="MiddlewarePacks.Runs"/>,
    /// the one rule for what packs seed, what counts as still to compile, and the upscaler chip.</summary>
    public static bool SeedsPacks(EngineInfo engine) => engine.GraphicsApi.Contains("D3D12");

    /// <summary>The distinct PSOs both kinds of pack hold for this DLL version that seed a plan on this GPU vendor.</summary>
    public int PackPipelines(MiddlewareDll dll, bool? amd = null) =>
        new[] { Packs, SharedPacks }.SelectMany(p => p?.Keys(dll, amd) ?? []).Distinct().Count();
    /// <summary>Materialize's pack line (entries written / skipped); optional.</summary>
    public IProgress<string>? Log { get; set; }
    /// <summary>A build's or materialize's step count as it runs: what it counts, done, total (0 = not known yet); optional.
    /// Per flow: an abandoned build keeps reporting to its own run, never to the next one's.</summary>
    public static readonly AsyncLocal<Action<string, long, long>?> Steps = new();

    /// <summary>Bump when the plan for the same game and inputs changes (new pipeline kinds, root-signature rules, D3D11):
    /// the app then rebuilds plans (warmed games' when idle, ScsKiller.CheckPlans) and offers a re-warm only where the new
    /// plan has records the warm didn't replay.</summary>
    public const int Version = 53;

    /// <summary>The vendor's D3D11 driver cache persists across processes, is keyed on the exe file name and caches per
    /// shader, whatever the state or the other stages (measured on NVIDIA and Intel, proxy/probe11.cpp): a staged warm
    /// that creates and draws/dispatches each shader once fills it. ponytail: measured for "nvidia-1" and "intel-1" only;
    /// becomes the proposed VendorCaps.D3D11CacheKeyedByExeName once that's in the contract.</summary>
    internal static bool D3D11Cache(VendorCaps caps) => D3D11Stages(caps) != null;

    /// <summary>The D3D11 stages a staged warm compiles the way the game uses them; null when the vendor's D3D11 cache isn't
    /// measured. NVIDIA: every stage. Intel: every stage too (probe11: a state change costs under 1 ms). AMD
    /// (proxy/layout11.cpp): the PS and CS whatever the state; a VS compiles again for
    /// the format, offset, slot and step rate of each element it reads and for the topology class, which its bytecode
    /// doesn't say, so it needs the game's layouts; a GS, HS or DS is compiled with the VS before it. Under Proton (NVIDIA's
    /// Linux driver, Mesa) D3D11 is DXVK's on every vendor, whose input layout is Vulkan vertex input state rather than VS
    /// code: every stage, as through a game's DXVK on NVIDIA (Warmer.DxvkDll). ponytail: unmeasured under Proton.</summary>
    public static Stage[]? D3D11Stages(VendorCaps caps) => caps.Profile switch
    {
        "nvidia-1" or Vendors.IntelBackend.Profile or Vendors.NvidiaLinux.Profile or Vendors.MesaLinux.Profile
            => [Stage.Vertex, Stage.Pixel, Stage.Domain, Stage.Hull, Stage.Geometry, Stage.Compute],
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

    /// <summary>Of the recorded state objects (in file order), the community-only ones the replay leaves out: each library it
    /// names gets a synthesized collection (<paramref name="covered"/>) and all it builds on is left out too, so a pipeline
    /// linking only collections goes with them.</summary>
    public static HashSet<string> CommunityLeftOut(IEnumerable<Rec> stateObjects, IReadOnlySet<string>? community, Func<string, bool> covered)
    {
        var left = new HashSet<string>();
        if (community is not { Count: > 0 }) return left;
        foreach (var r in stateObjects)
        {
            if (!community.Contains(r.Key)) continue;
            StateObject so;
            try { so = ParseStateObject(r); }
            catch (InvalidDataException) { continue; }
            if (so.Libraries.All(covered) && so.Depends.All(left.Contains)) left.Add(r.Key);
        }
        return left;
    }

    /// <summary>DirectX 11 alone is the user's choice or what this PC's recording shows: <see cref="LeftOutOnD3D11"/> applies.
    /// Not for DirectX 11 by detection alone: D3D12 records in a community recording show players run it on DirectX 12.</summary>
    public static bool D3D11Decided(EngineInfo engine) => engine.GraphicsApi is "D3D11" + App.ScsKiller.ChoiceSuffix or "D3D11" + App.ScsKiller.RecordingSuffix;

    /// <summary>A recorded record a DirectX 11 compile leaves out of the plan, the warm and the count of new pipelines: all but
    /// blobs and D3D11 draws are D3D12's, which the D3D11 driver cache never takes.</summary>
    public static bool LeftOutOnD3D11(Rec r) => r.Tag is not ('B' or 'D');

    /// <summary>A shader D3D11 takes: VS/PS/DS/HS/GS/CS in DXBC up to SM 5.0 (5.1 and DXIL are D3D12 only).</summary>
    internal static bool IsD3D11(ShaderInfo s) => s.Stage <= Stage.Compute && s.ShaderModel[^3..] is "4_0" or "4_1" or "5_0";

    public const string NoRecording = "no recording needed";
    /// <summary>The plan comes from a rule no game has confirmed yet (<see cref="RootSig.Verified"/>). A scan keeps its
    /// check, so ScsKiller.Evaluate turns it into <see cref="NoRecording"/> once the list confirms the engine.</summary>
    public const string Untested = UntestedNote + ": playing with recording on improves it";
    /// <summary><see cref="Untested"/> without the recording hint: what an anti-cheat game, which can't be recorded, is told.</summary>
    public const string UntestedNote = "not tested on this engine version yet";
    public const string Record = "turn on recording and play for about 5 minutes";
    /// <summary>Ends the reason of a Vulkan game on a vendor without a Vulkan warm (<see cref="VendorCaps.VulkanKey"/>).</summary>
    public const string VulkanNotOnGpu = ", which isn't supported on this GPU yet";
    /// <summary>Ends the reason of a game that may run on DirectX 11, where the recorder never loads.</summary>
    public const string Dx12Only = "helps only when played on DirectX 12 (the game may run on DirectX 11)";
    public const string RecordedOnly = "plans only the recorded pipelines";
    /// <summary>A game without a recording planned from the PSO disk cache its own engine wrote (Unreal.PsoDiskCache).</summary>
    public const string OwnPipelineCache = "planned from the game's own pipeline cache";

    /// <summary>AMD's Windows driver keeps DirectX 9 shaders in its cache by exe name, NVIDIA's keeps none (both measured).</summary>
    public const string Dx9AmdReason = "runs on DirectX 9: compiling it waits for SCSKiller's DirectX 9 compile on AMD";

    static string Dx9For(string reason, VendorCaps caps) => reason == CarvedReader.Dx9Reason && caps.Profile == "amd-1" ? Dx9AmdReason : reason;

    public PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps)
    {
        // the recording is the whole plan: nothing of the game files is read, so whether a reader can index them doesn't matter
        if (Vulkan.VulkanPlan.IsVulkan(engine))
        {
            if (caps.VulkanKey == null) return new(Readiness.Unsupported, $"runs on {engine.GraphicsApi}{VulkanNotOnGpu}");
            var check = Vulkan.VulkanPlan.Check(recording);
            // a device-keyed cache: the launch that unlocks Steam's download must also record the device
            if (check.Reason == Vulkan.VulkanPlan.StartOnce && caps.VulkanKey == Vulkan.VulkanPlan.DeviceKey && recording!.VulkanDevice == null)
                check = new(Readiness.NeedsRecording, Vulkan.VulkanPlan.DeviceNeeded);
            // the warm reaches the game's cache only with its device (VulkanPlan.DeviceKey): a Steam game's in the file Steam's
            // variables name, any other's in the driver's own cache, which the replayer staged as the game's exe reaches
            if (check.Readiness == Readiness.Ready && caps.VulkanKey == Vulkan.VulkanPlan.DeviceKey)
                check = recording!.VulkanDevice == null ? new(Readiness.NeedsRecording, Vulkan.VulkanPlan.DeviceNeeded)
                    : !recording.VulkanSteamCache && game.Store == Store.Steam ? new(Readiness.NeedsRecording, Vulkan.VulkanPlan.SteamCacheNeeded) : check;
            // a game without a layer key (off Windows, not Steam's) can't be recorded: a recording can't be asked for
            return check.Readiness == Readiness.NeedsRecording && Vulkan.VulkanLayer.Key(game) == null ? new(Readiness.Unsupported, App.ScsKiller.SkipVulkanSteamOnly) : check;
        }
        // shaders in no readable file: a DirectX 12 recording's own bytes are the whole plan, nothing is generated
        if (CarvedReader.RecordedOnly(engine))
            return !caps.CacheKeyedByExeName ? new(Readiness.Unsupported, "not supported on this GPU yet")
                : recording != null && File.Exists(recording.DbPath) && new FileInfo(recording.DbPath).Length > 0 ? new(Readiness.Ready, RecordedOnly)
                : new(Readiness.NeedsRecording, Record);
        if (engine.Unsupported != null) return new(Readiness.Unsupported, Dx9For(engine.Unsupported, caps));
        if (engine.Encrypted) return new(Readiness.Unsupported, "encrypted game files");
        // GraphicsApi: "D3D12[ (why)]", "D3D11[ (why)]", "Vulkan…", or "D3D11 or D3D12" when the game offers both (UnrealRhi)
        var api = engine.GraphicsApi;
        var dx11 = api.StartsWith("D3D11") && D3D11Cache(caps);
        // a vendor whose VS needs the game's layouts (AMD): they come from a D3D11 recording ('D' draws)
        var noVs = !D3D11CoversVs(caps) && !(dx11 && recording != null && HasDraws11(recording));
        var all11 = "compiles every DirectX 11 shader" + (noVs ? " but the vertex shaders" : "");
        if (!api.Contains("D3D12"))
            return dx11 ? new(Readiness.Ready, App.Format.Then(all11 + api[5..], noVs ? D3D11VsRecord : null))
                : SteamOnly(recording, null) is { } steam ? steam
                : new(Readiness.Unsupported, (CarvedReader.OldDxReason(api) is { } old ? Dx9For(old, caps) : null) ?? $"runs on {(api.StartsWith("D3D11") ? "DirectX 11" + api[5..] : api)}");
        var dx12 = CheckD3D12(engine, recording, caps, api.StartsWith("D3D12") || dx11 ? null : Dx12Only);
        if (!dx11) return dx12;
        // may run on either: DX11 shaders help whatever DX12 still needs (a recording can come later)
        return new(Readiness.Ready, dx12.Readiness == Readiness.Ready ? App.Format.Then(dx12.Reason, $"also {all11} (the game may run on either)")
            : App.Format.Then($"{all11} (the game may run on either)", $"for DirectX 12, {dx12.Reason}"));
    }

    static PlanCheck CheckD3D12(EngineInfo engine, Recording? recording, VendorCaps caps, string? maybe)
    {
        if (!caps.CacheKeyedByExeName) return new(Readiness.Unsupported, "not supported on this GPU yet");
        // EmbeddedRootSignatures: any reader of shaders that carry them (carved, FromSoftware) or of records that name them (Dawn)
        if (caps.StateIndependentCache && (RootSig.Verified(engine) || engine.Version.EndsWith(CarvedReader.EmbeddedRootSignatures)))
            return new(Readiness.Ready, App.Format.Then(engine.Family == BlackSpace.BlackSpaceReader.Family ? Untested : NoRecording, maybe));
        if (recording != null && File.Exists(recording.DbPath) && new FileInfo(recording.DbPath).Length > 0)
            return caps.StateIndependentCache || HasDraws(recording) ? new(Readiness.Ready, App.Format.Then("planned from a recording", maybe))
                : FromSteam(recording) is { } drawless ? new(Readiness.Ready, App.Format.Then(drawless, maybe))
                : new(Readiness.NeedsRecording, App.Format.Then("the recording has no draws: play into the game world", maybe)); // no vertex layouts to learn
        if (caps.StateIndependentCache && RootSig.RuleFor(engine) != null) return new(Readiness.Ready, App.Format.Then(Untested, maybe));
        return SteamOnly(recording, maybe) ?? new(Readiness.NeedsRecording, App.Format.Then(Record, maybe));
    }

    public const string SteamDownload = "planned from Steam's shader download", SteamRecorded = "planned from Steam's shader cache of your play";

    /// <summary>A DirectX game under Proton whose Steam archives (<see cref="Recording.VulkanSources"/>) hold a pipeline to
    /// plan: why it's ready without a recording of its own. Null: they hold none.</summary>
    static string? FromSteam(Recording? recording) =>
        recording?.VulkanSources is { } sources && Vulkan.VulkanPlan.Check(recording).Readiness == Readiness.Ready
            ? sources.Any(s => s.Origin == Vulkan.FozOrigin.SteamDownload) ? SteamDownload : SteamRecorded : null;

    /// <summary>A DirectX game without a recording, planned from Steam's archives alone: ready once this PC's vkd3d-proton
    /// or DXVK recorded into the bucket, so the game has its Proton prefix and the bucket is this PC's version of theirs, else
    /// started once from Steam first. Null: nothing to plan from Steam's.</summary>
    static PlanCheck? SteamOnly(Recording? recording, string? maybe) =>
        FromSteam(recording) is not { } why ? null
        : recording!.VulkanSources!.Any(s => s.Origin == Vulkan.FozOrigin.SteamLocal) ? new(Readiness.Ready, App.Format.Then(why, maybe))
        : new(Readiness.NeedsRecording, Vulkan.VulkanPlan.StartOnce);

    /// <summary>A plan of Steam's archives alone (<see cref="Vulkan.VulkanPlan.Build"/>): a Vulkan game's, or a DirectX game's
    /// under Proton whose DirectX plan isn't ready without them. Otherwise that plan wins and takes them in (<see cref="WithSteam"/>).</summary>
    bool VulkanOnly(Game game, EngineInfo engine, Recording? recording, VendorCaps caps) =>
        Vulkan.VulkanPlan.IsVulkan(engine) || recording is { VulkanSources: not null }
            && Check(game, engine, File.Exists(recording.DbPath) ? recording with { VulkanSources = null } : null, caps).Readiness != Readiness.Ready;

    /// <summary>The plan with the pipelines of Steam's archives (<see cref="Recording.VulkanSources"/>) added as 'V' items,
    /// which the warm replays natively before the DirectX ones, and counted with the recorded ones
    /// (<see cref="PlanStats.FromDownload"/>: Valve's download's). A DirectX pipeline and a Vulkan one live in different
    /// hashes, so what both hold is compiled by each.</summary>
    static Plan WithSteam(Plan plan, IReadOnlyList<Vulkan.FozSource> sources, VendorCaps caps, bool rayTracing)
    {
        var (names, vk) = Vulkan.VulkanPlan.Select(sources, caps, rayTracing);
        if (names.Count == 0) return plan;
        var s = plan.Stats;
        plan = plan with
        {
            Stats = s with
            {
                Recorded = s.Recorded + vk.Recorded, FromDownload = vk.FromDownload, LeftOut = s.LeftOut + vk.LeftOut, DescriptorHeap = vk.DescriptorHeap,
                CacheBudgetLeftOut = s.CacheBudgetLeftOut is null && vk.CacheBudgetLeftOut is null ? null : (s.CacheBudgetLeftOut ?? 0) + (vk.CacheBudgetLeftOut ?? 0),
            },
        };
        PlanFile.Write(plan, PlanFile.Read(plan.FilePath).Records.Concat(names.Select(Vulkan.VulkanPlan.Item)));
        return plan;
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
        if (VulkanOnly(game, engine, recording, caps))
            return Vulkan.VulkanPlan.Build(game, recording ?? throw new InvalidOperationException("a Vulkan game plans only from a recording"), caps, outDir, rayTracing, index.ContentHash);
        Plan plan;
        var steps = Steps.Value;
        try { plan = Build(game, engine, index, recording, caps, outDir, log, ct, maximum, rayTracing, pairs: !CarvedReader.RecordedOnly(engine), steps); }
        catch (PlanTooBigException e)
        {
            log?.Report($"warning: {e.Message}: planning only the recorded pipelines");
            plan = Build(game, engine, index, recording, caps, outDir, log, ct, maximum, rayTracing, pairs: false, steps);
            if (plan.Stats.Recorded + plan.Stats.MiddlewareItems == 0)
            {
                File.Delete(plan.FilePath);
                throw;
            }
        }
        return recording?.VulkanSources is { } steam ? WithSteam(plan, steam, caps, rayTracing) : plan;
    }

    Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir,
        IProgress<string>? log, CancellationToken ct, bool maximum, bool rayTracing, bool pairs, Action<string, long, long>? steps)
    {
        var budget = new PlanBudget(MemoryBudget ?? PlanBudget.DefaultMemory(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), TimeBudget ?? PlanBudget.DefaultTime) { Stop = ct };
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
            var builder = new PlanBuilder(game, engine, index, recording, caps, dir, l, ct, maximum, Packs, Uses(game) ? SharedPacks : null, r, budget, CacheBudget, rayTracing, pairs) { Steps = steps };
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
        if (plan.Platform == Vulkan.VulkanPlan.Platform)
        {
            Vulkan.VulkanPlan.Materialize(plan, recording ?? throw new InvalidOperationException("a Vulkan plan replays only from a recording"), workDir);
            return;
        }
        Directory.CreateDirectory(workDir);
        var mainDb = Path.Combine(workDir, "scskiller.db");
        if (recording != null && File.Exists(recording.DbPath)) CopyRaw(recording.DbPath, mainDb);
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
        // Steam's pipelines of a DirectX plan (WithSteam): their own work file, their skips counted with the rest
        long skipped = 0;
        if (recording?.VulkanSources is { } steam && body.Any(r => r.Tag == Vulkan.VulkanPlan.Tag))
        {
            Vulkan.VulkanPlan.Materialize(plan, steam, workDir);
            skipped = SkippedIn(workDir);
        }
        else foreach (var f in new[] { Vulkan.VulkanPlan.WorkFile, Vulkan.VulkanPlan.ValidatedFile }) File.Delete(Path.Combine(workDir, f));
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
        long pulled = 0;
        reader.ReadShaders(game, engine, shaders, (h, b) => { lock (gen) { WriteBlob(gen, h, b); written.Add(h); if (rtLibs.Contains(h)) rtBytes[h] = b; Steps.Value?.Invoke("shaders read from the game", ++pulled, shaders.Count); } }, ct); // missing ones: skipped below
        // middleware pack entries: shader bytes from this install's copy of the DLL, entries it lacks a shader of are skipped
        var packed = body.Where(r => r.Tag == 'M').ToList();
        if (packed.Count > 0)
        {
            MiddlewarePacks.MaterializeResult m;
            try { m = MiddlewarePacks.Materialize(packed, game, planBlobs, written, inMain, gen); }
            catch (IOException)   // a DLL it couldn't read: no partial work folder is left for a warm to start on
            {
                try { gen.Dispose(); }
                finally { foreach (var file in new[] { mainDb, Path.Combine(workDir, "scskiller_gen.db"), Path.Combine(workDir, SkippedFile) }) File.Delete(file); }
                throw;
            }
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
        long mainCount = 0, flagCount = 0, keptCount = 0, movedCount = 0, needsRecording = 0;
        var keptKeys = new HashSet<string>();
        var recordedPayloads = new List<uint>();
        var nvOfMain = new List<Rec>();   // its state objects and 'N' records: what the option fixes learn from (RtCollections.OptionFixes)
        foreach (var r in Read(mainDb))
        {
            if (RtCollections.Read(r) is { } c) recordedPayloads.Add(c.Payload);
            if (r.Tag == 'N' || IsStateObject(r.Tag)) nvOfMain.Add(r);
        }
        var rtOptions = RtCollections.RecordedOptions(nvOfMain);
        // a ray tracing collection per library ('Y'), its exports read from the library: an 'R' record the warm creates like a
        // recorded one; a library not in this install, or without a ray tracing entry point, is skipped. With rule payload 0 a
        // ray generation library gets one per payload the other libraries have (RtCollections.Payloads), the recorded ones'
        // (no 'Y': their collections replay from the recording) included
        var own = rt.Select(y => y.Library).Distinct().Where(rtBytes.ContainsKey).ToDictionary(h => h, h => RtCollections.OwnPayload(rtBytes[h]));
        var pipelinePayloads = rt.Where(y => y.Payload == 0).Select(y => own.GetValueOrDefault(y.Library))
            .Concat(recordedPayloads).Where(p => p > 0).Distinct().Order().ToList();
        var synthesized = new List<(byte[] So, RtCollections.Nv? Nv)>();
        var covered = new HashSet<string>();   // libraries a synthesized collection compiles
        foreach (var y in rt)
        {
            var n = 0;
            if (rtBytes.TryGetValue(y.Library, out var lib))
                foreach (var p in RtCollections.Payloads(y.Payload, own[y.Library], pipelinePayloads))
                    if (RtCollections.Collection(lib, y.Library, y.Global, y.LocalRayGen, y.LocalOther, p, y.Attributes, y.Depth, y.Flags) is { } so)
                    {
                        synthesized.Add((so, RtCollections.WithOptions(y.Nv, rtOptions)));
                        n++;
                    }
            if (n == 0) skipped++;
            else covered.Add(y.Library);
        }
        foreach (var h in hitGroups)
        {
            if (rtBytes.TryGetValue(h.ClosestHit, out var ch) && (h.AnyHit == null || rtBytes.ContainsKey(h.AnyHit))
                && (engine.Family == FromSoft.FromSoftReader.Family ? FromSoft.SoulsRayTracing.Collection(ch, h, h.AnyHit != null ? rtBytes[h.AnyHit] : [])
                    : RedEngine.RedRayTracing.Collection(ch, h, h.AnyHit != null ? rtBytes[h.AnyHit] : [])) is { } so)
            {
                synthesized.Add((so, RtCollections.WithOptions(h.Nv, rtOptions)));
                covered.UnionWith(new[] { h.ClosestHit, h.AnyHit }.OfType<string>());
            }
            else skipped++;
        }
        var synthesizedKeys = synthesized.Select(s => new Rec('R', s.So).Key).ToHashSet();
        // the community recording's own state objects go last, so a crash storm in them can't keep the plan from compiling,
        // and are left out where the plan's collections compile their libraries
        var community = recording?.CommunityStateObjects;
        var leftOut = CommunityLeftOut(Read(mainDb).Where(r => IsStateObject(r.Tag)), community, covered.Contains);
        var tail = new List<Rec>();
        bool Moved(Rec r) => community != null && IsStateObject(r.Tag) && community.Contains(r.Key);
        bool Stays(Rec r)
        {
            if (r.Tag is 'B' or 'W') return true;
            if (r.Tag == 'L' || Moved(r) || !Keep(r) || IsStateObject(r.Tag) && !ParseStateObject(r).Depends.All(keptKeys.Contains)) return false;
            keptKeys.Add(r.Key);
            return true;
        }
        foreach (var r in Read(mainDb))
        {
            mainCount++;
            if (Stays(r)) keptCount++;
            else if (r.Tag == 'L') flagCount++;
            else if (Moved(r))
            {
                movedCount++;
                if (!leftOut.Contains(r.Key)) tail.Add(r);
            }
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
        skipped += mainCount - flagCount - keptCount - movedCount + templates.Count - keptTemplates.Count + items.Count - keptItems.Count;
        foreach (var r in keptTemplates) Write(gen, r.Tag, r.Payload);
        foreach (var r in keptItems) Write(gen, r.Tag, r.Payload);
        var keptPsos = keptTemplates.Concat(keptItems).Select(r => r.Key).ToHashSet();
        foreach (var r in body.Where(r => r.Tag == 'N' && keptPsos.Contains(NvState.Parse(r).Target))) Write(gen, r.Tag, r.Payload); // the NVAPI state they're created with
        // the recorded state objects' NVAPI creation options where the recording lost them, learned from the whole recording
        // as WarmInputs does; read after the main db's, they win
        var tailKeys = new HashSet<string>();   // what builds on a left-out collection finds the plan's (the same key)
        foreach (var r in tail)
            if (Keep(r) && ParseStateObject(r).Depends.All(d => keptKeys.Contains(d) || synthesizedKeys.Contains(d) || tailKeys.Contains(d))) tailKeys.Add(r.Key);
            else
            {
                skipped++;
                if (localOnly.Contains(r.Key)) needsRecording++;
            }
        foreach (var r in RtCollections.OptionFixes(nvOfMain).Where(r => NvState.Parse(r).Target is var t && (keptKeys.Contains(t) || tailKeys.Contains(t)))) Write(gen, r.Tag, r.Payload);
        foreach (var (so, nv) in synthesized)
        {
            Write(gen, 'R', so);
            if (nv is { } x) { var r = new NvState(new Rec('R', so).Key, x.Slot, x.Space, 2, x.Options).ToRec(); Write(gen, r.Tag, r.Payload); }
        }
        foreach (var r in tail.Where(r => tailKeys.Contains(r.Key))) Write(gen, r.Tag, r.Payload);
        if (movedCount > 0) Log?.Report($"{game.Name}: {leftOut.Count:N0} community ray tracing state objects left out (the plan's collections compile their libraries), {tailKeys.Count:N0} compiled last");
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

    /// <summary>The last stage before the rasterizer writes SV_ShadingRate and the PS doesn't declare it. Legal D3D12 linkage, but
    /// with view instancing AMD rejects the pipeline (E_INVALIDARG; Crimson Desert: 1,338 shadow pairs, the same pairs without
    /// view instancing create, and the game never draws them).</summary>
    internal static bool ShadingRateUnread(IReadOnlyDictionary<Stage, ShaderInfo> st) =>
        st.TryGetValue(Stage.Pixel, out var ps) && ps.Inputs.All(i => i.SysValue != ShadingRate)
        && new[] { Stage.Geometry, Stage.Domain, Stage.Mesh, Stage.Vertex }.Where(st.ContainsKey).Select(x => st[x]).FirstOrDefault() is { } last
        && last.Outputs.Any(o => o.SysValue == ShadingRate);

    const int ShadingRate = 24;   // D3D_NAME_SHADINGRATE

    /// <summary>The PS writes SV_StencilRef, which NVIDIA's D3D12 doesn't support (PSSpecifiedStencilRefSupported false): the
    /// runtime rejects the pipeline (E_INVALIDARG; RE Requiem demo: 20 of 20 failed items of a warm, all such pairings).</summary>
    internal static bool StencilRefUnsupported(VendorCaps caps, IReadOnlyDictionary<Stage, ShaderInfo> st) =>
        caps.Profile.StartsWith("nvidia") && st.TryGetValue(Stage.Pixel, out var ps) && ps.Outputs.Any(o => o.SysValue == StencilRef);

    const int StencilRef = 69;   // D3D_NAME_STENCIL_REF

    /// <summary>The recorded pipelines another GPU vendor's game made: the root signature is the engine rule's construction for
    /// the other vendor (<see cref="ReRule.OtherCbvBase"/>) and not this one's, so the game here never creates them (RE Requiem
    /// demo: a shared recording of 15,758 pipelines in the layout off NVIDIA). Null when the rule builds the same everywhere.</summary>
    internal static Func<Rec, bool>? OtherVendors(EngineInfo engine, ShaderIndex index, VendorCaps caps, string db)
    {
        if (RulesFile.Current.RuleFor(engine) is not { RootSignature.Re.OtherCbvBase: not null } rule) return null;
        var nvidia = caps.Profile.StartsWith("nvidia");
        var blobs = Read(db).Where(r => r.Tag == 'B' && r.Payload.Length > 20).GroupBy(r => Hex(r.Payload.AsSpan(0, 20))).ToDictionary(g => g.Key, g => g.First().Payload[20..]);
        return r =>
        {
            if (r.Tag is not ('G' or 'C' or 'S')) return false;
            var p = Parse(r);
            if (!blobs.TryGetValue(p.Rs, out var blob) || !p.Stages.Values.All(index.Shaders.ContainsKey)) return false;
            var st = p.Stages.ToDictionary(x => (Stage)x.Key, x => index.Shaders[x.Value]);
            bool Built(bool nv)
            {
                try { return RootSig.Serialize(RootSig.Build(rule, st, true, nvidia: nv), RootSig.Samplers(blob)).AsSpan().SequenceEqual(blob); }
                catch (RootSig.SerializeException) { return false; }
            }
            return !Built(nvidia) && Built(!nvidia);
        };
    }

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

    /// <summary>A DXIL PS's non-system inputs sit in the rows the stage feeding it (GS, DS or VS) writes them in. A pipeline table
    /// can list pairs that don't (RE Village: 18 VS+PS pairs, PS VEL0 in row 0, VS in row 5 or 8, E_INVALIDARG on AMD; 0 of
    /// 14,389 game-created DXIL pairs of five games differ). An input no output names is <see cref="Links"/>' to judge. No PS,
    /// a DXBC one, or a mesh shader feeding it: true.</summary>
    internal static bool PsRowsLink(IReadOnlyDictionary<Stage, ShaderInfo> st)
    {
        if (!st.TryGetValue(Stage.Pixel, out var ps) || st.ContainsKey(Stage.Mesh) || ps.ShaderModel.Split('_') is [_, "4" or "5", ..]) return true;
        if (new[] { Stage.Geometry, Stage.Domain, Stage.Vertex }.Where(st.ContainsKey).Select(x => st[x]).FirstOrDefault() is not { } src) return true;
        var rows = src.Outputs.ToLookup(o => (o.Semantic.ToUpperInvariant(), o.Index), o => o.Register);   // a GS's streams may each have one
        return ps.Inputs.All(i => i.SysValue != 0 || rows[(i.Semantic.ToUpperInvariant(), i.Index)] is var r && (!r.Any() || r.Contains(i.Register)));
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
    /// <summary>Half the RAM, at least 4 GiB, at most 32 GiB; <see cref="UserMemory"/> when that is more. In a
    /// <see cref="Background"/> flow: <see cref="BackgroundMemory"/>, whatever the setting.</summary>
    public static long DefaultMemory(long ram) => Background.Value ? BackgroundMemory(ram) : Math.Max(Math.Clamp(ram / 2, 4L << 30, 32L << 30), UserMemory);
    /// <summary>A quarter of the RAM, at most 8 GiB: what a plan check (nobody started it) may take.</summary>
    public static long BackgroundMemory(long ram) => Math.Min(ram / 4, 8L << 30);
    /// <summary>True in the flow of a job nobody started: its reads and plans get <see cref="BackgroundMemory"/>.</summary>
    public static readonly AsyncLocal<bool> Background = new();
    /// <summary>Bytes of "Maximum memory for compiling" (Settings.MaxCompileMemoryGB); 0 = Auto.</summary>
    public static long UserMemory { get; set; }
    /// <summary>Planning runs at below-normal priority, often while the PC is in use.</summary>
    public static readonly TimeSpan DefaultTime = TimeSpan.FromMinutes(30);

    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    long ticks, collectAfterMs;

    /// <summary>What the planner has done so far, for the message: e.g. "1,234 stage sets".</summary>
    internal Func<string>? Progress { get; set; }

    /// <summary>What the budget bounds and why it ran out, for the message.</summary>
    internal string What { get; init; } = "the plan";
    internal string Why { get; init; } = "this game's shaders combine into more pipelines than a compile can hold";
    /// <summary>Checked with the budget, so a cancel stops the planner mid-stage.</summary>
    public CancellationToken Stop { get; init; }

    /// <summary>The most managed memory a check saw, garbage included.</summary>
    public long Peak { get; private set; }

    public void Tick()
    {
        if ((++ticks & 255) == 0) Check();
    }

    public void Check()
    {
        Stop.ThrowIfCancellationRequested();
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
