using System.Runtime.InteropServices;
using SCSKiller.Core.Carved;
using static SCSKiller.Core.Unreal.ShaderContainer;

namespace SCSKiller.Core.Planning;

/// <summary>Unreal root signatures, rebuilt from the shaders' per-stage resource counts the way each engine version's D3D12
/// RHI builds them, serialized through the real D3D12SerializeVersionedRootSignature, so blobs are byte-identical to what
/// the game creates. FF7 Rebirth's (Square Enix's 4.26 fork) is verified against a recording (994/994), and the stock 4.26 rule
/// (tables, CBVs, static samplers, flags) by Star Wars Jedi: Survivor's (Respawn's 4.26 fork: 23626/23626 with its bindless
/// tables, <see cref="BindlessTables"/>). The stock rules
/// are read from Epic's source (D3D12RootSignature.cpp: FD3D12RootSignatureDesc; D3D12Util.cpp: InitShaderRegisterCounts and
/// the bound-shader-state quantizer; D3D12RHI.h: MAX_*), for resource binding tier 3 (every NVIDIA GPU SCSKiller supports).
/// REDengine 3's are one per kind of pipeline and Northlight's one graphics and one compute, fixed descriptions; Dagor's are
/// built from each shader's header (<see cref="Dagor.DagorRootSig"/>). Which rule an engine version gets, and each rule's
/// parameters and descriptions, are data (plan-rules.json, <see cref="RulesFile"/>); the constructions are here.</summary>
public static unsafe class RootSig
{
    /// <summary>The rule a game's engine builds with (<see cref="RulesFile.Current"/>); null = none known. A fork without a rule
    /// of its own gets its base version's stock rule, which the fork may have changed (FF7's did).</summary>
    public static PlanRule? RuleFor(EngineInfo e) => RulesFile.Current.RuleFor(e);

    /// <summary>The engine's rule is confirmed by a real game (<see cref="ConfirmedEngines"/>, per version and fork, or the rule
    /// itself). Another fork (e.g. Stellar Blade's) may add slots no shader shows, so it stays unconfirmed.</summary>
    public static bool Verified(EngineInfo e) => Verified(RuleFor(e), e);

    internal static bool Verified(PlanRule? r, EngineInfo e) => r != null && (r.Verified || ConfirmedEngines.Current.Contains(e));

    /// <summary>UE's six static samplers (space 1000, s0-s5): point/bilinear/trilinear x wrap/clamp, 52 bytes each.</summary>
    public static readonly byte[] Ue426Samplers = UeSamplers(0, 1000);

    /// <summary>The static samplers a rule's root signatures carry (UE 4.25: the same six at s1000-s1005 in space 0; before: none).</summary>
    public static byte[] StaticSamplers(PlanRule r) =>
        r.RootSignature?.Samplers is { } raw ? MemoryMarshal.AsBytes(raw.SelectMany(x => x).ToArray().AsSpan()).ToArray()
        : r.RootSignature?.StaticSamplers is { } s ? UeSamplers(s.Register, s.Space) : [];

    internal static byte[] UeSamplers(uint reg, uint space)
    {
        var s = new List<uint>();
        foreach (var filter in new uint[] { 0, 0x14, 0x15 }) // point, bilinear, trilinear
            foreach (var addr in new uint[] { 1, 3 }) // wrap, clamp
                s.AddRange([filter, addr, addr, addr, 0, 1, 1, 0, 0, BitConverter.SingleToUInt32Bits(float.MaxValue), reg++, space, 0]);
        return MemoryMarshal.AsBytes(s.ToArray().AsSpan()).ToArray();
    }

    internal const uint Unbounded = uint.MaxValue;
    const uint Append = uint.MaxValue;
    static readonly Stage[] Order = [Stage.Pixel, Stage.Vertex, Stage.Mesh, Stage.Amplification, Stage.Geometry, Stage.Hull, Stage.Domain]; // compute is alone
    static uint Vis(Stage s) => s switch { Stage.Vertex => 1, Stage.Hull => 2, Stage.Domain => 3, Stage.Geometry => 4, Stage.Pixel => 5, Stage.Amplification => 6, Stage.Mesh => 7, _ => 0 };
    static readonly (Stage, uint)[] Deny = [(Stage.Vertex, 0x2), (Stage.Geometry, 0x10), (Stage.Pixel, 0x20), (Stage.Amplification, 0x100), (Stage.Mesh, 0x200)]; // hull/domain never denied
    static uint DenyBit(Stage s) => s switch { Stage.Vertex => 0x2, Stage.Hull => 0x4, Stage.Domain => 0x8, Stage.Geometry => 0x10, Stage.Pixel => 0x20, Stage.Amplification => 0x100, _ => 0x200 };

    /// <summary>A 1.1 root signature: flags + parameters. Each row is a table (0, vis, then per range: range type, count, base,
    /// space, flags), root constants (1, vis, register, space, count) or a root CBV/UAV (2/4, vis, register, space, flags).
    /// A single-range table's range is at OFFSET_APPEND unless the row carries an explicit offset as an 8th value; a multi-range
    /// table's ranges all start at offset 0 (they alias one heap region, see <see cref="BindlessTables"/>), or with
    /// <paramref name="AppendRanges"/> each at OFFSET_APPEND (one after the other). <paramref name="Version10"/>: serialized
    /// as a version 1.0 root signature (range and root descriptor flags dropped); with <paramref name="RangeOffsets"/> each
    /// range's fifth value is its offset from the table's start. <see cref="Key"/> identifies it.</summary>
    public sealed record Desc(uint Flags, List<uint[]> Rows, bool AppendRanges = false, bool Version10 = false, bool RangeOffsets = false)
    {
        public string Key => $"{Flags}|{string.Join(';', Rows.Select(r => string.Join(',', r)))}{(AppendRanges ? "|append" : "")}{(Version10 ? "|1.0" : "")}{(RangeOffsets ? "|offsets" : "")}";
    }

    /// <param name="meshTier">the RHI runs at feature level SM6 (the game's PCD3D_SM6 shaders), where UE 5 sets
    /// GRHISupportsMeshShadersTier0 on mesh-shader GPUs and so denies the mesh/amplification stages it doesn't use</param>
    /// <param name="maxSrvs">the game's MAX_SRVS when it isn't the rule's (<see cref="MaxSrvsFor"/>); 0 = the rule's</param>
    public static Desc Build(PlanRule r, IReadOnlyDictionary<Stage, ShaderInfo> stages, bool meshTier, uint maxSrvs = 0)
    {
        var rs = r.RootSignature!;
        if (rs.ByStages is { } by) return ByStages(r, by, stages);
        if (rs.Re is { } re) return BuildRe(re, stages);
        if (Selects(r, "dagor")) return Dagor.DagorRootSig.Build(stages);
        if (Selects(r, "dagorCbvRanges")) return Dagor.DagorRootSig.Build(stages, cbvRanges: true);
        return Selects(r, "ff7") ? BuildUe(stages) : BuildStock(r, rs.Stock!, stages, meshTier, maxSrvs);
    }

    static bool Selects(PlanRule r, string shape) => r.RootSignature?.Select?.Contains(shape) == true;

    /// <summary>The stage set is in the rule's allow-list (none: any is). REDengine 3's: the sets its recording confirmed (VS,
    /// VS+PS, VS+HS+DS, VS+HS+DS+PS, VS+GS+PS, VS+GS+HS+DS, CS); the planner leaves out any other.</summary>
    public static bool InStageSets(PlanRule r, IEnumerable<Stage> stages) =>
        r.StageSets == null || StageMasks.GetValue(r, x => [.. x.StageSets!.Select(set => Mask(set.Select(RulesFile.StageOf)))]).Contains(Mask(stages));

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlanRule, HashSet<int>> StageMasks = new();

    static int Mask(IEnumerable<Stage> stages) => stages.Aggregate(0, (m, s) => m | 1 << (int)s);

    /// <summary>Fixed descriptions chosen by the pipeline's stages (the first entry naming a stage it has, or naming none; one
    /// without a description: none), for the stage sets of the rule's allow-list. REDengine 3's: compute; VS (+ PS); and with
    /// a GS, HS or DS the same plus a CBV, SRV and two sampler tables for each of those three (and stream output allowed); all
    /// tables volatile, root CBVs static while set. The Witcher 3's recording: 586 of 586 PSOs of its own shaders (VS and VS+PS
    /// 487, tessellation and GS 6, compute 93) use exactly these. Northlight's (version 1.0), as Control's renderer (d3d_*.dll)
    /// builds them in code: per stage a table of 5 CBVs (b4 for VS and PS, b0 for HS and DS), 36 SRVs and 8 UAVs, then a table
    /// of 4 samplers per stage, root CBVs b0-b3 for VS and PS, a table of 64 samplers in space 1, and a table of 244000 SRVs in
    /// space 1 for the PS; input layout, GS denied; none with a GS, MS or AS. Compute: the same tables and root CBVs once, all
    /// visible to every stage, the SRV table too; no flags. Ghost of Tsushima's and Ratchet &amp; Clank's (Nixxes, version 1.0
    /// with range offsets) are the ones the games created in recordings, picked as the games pick them (15,756 of 15,756 and
    /// 9,421 of 9,421 recorded PSOs of their own shaders): GoT a compute root, another for a compute shader declaring no
    /// resource, one graphics root; R&amp;C one for a pipeline with a DXBC shader (the exe's), then compute, tessellation and
    /// the graphics root.</summary>
    static Desc ByStages(PlanRule r, StagesDesc[] by, IReadOnlyDictionary<Stage, ShaderInfo> stages)
    {
        if (!InStageSets(r, stages.Keys)) throw new SerializeException($"no {r.Id} root signature confirmed for {string.Join('+', stages.Keys)}");
        return by.FirstOrDefault(b => (b.Any == null || b.Any.Any(n => stages.ContainsKey(RulesFile.StageOf(n))))
                && (!b.BindsNothing || stages.Values.All(s => s.Bindings.Count == 0)) && (!b.Dxbc || stages.Values.Any(s => s.ShaderModel[^3] < '6')))?.Desc?.ToDesc()
            ?? throw new SerializeException($"no {r.Id} root signature for {string.Join('+', stages.Keys)}");
    }

    static readonly Stage[] ReOrder = [Stage.Pixel, Stage.Vertex, Stage.Hull, Stage.Domain, Stage.Geometry, Stage.Amplification, Stage.Mesh];

    /// <summary>RE Engine's, built at run time from each pipeline's shaders (PRAGMATA's recording: 1250 of its 1252 PSOs rebuilt
    /// byte for byte; the other two are a utility's compute pipelines with root descriptors). A 1.0 signature: root constants
    /// (b0 space 32, one value) for every stage, then per stage, in the order PS, VS, HS, DS, GS, AS, MS, one table of CBVs from
    /// <see cref="ReRule.CbvBase"/>, SRVs from t0 and, for the PS, UAVs from u0, one table of 16 samplers, and root CBVs b0 up to
    /// the base. Each table size is the pipeline's, from its rule's sizes: CBVs the smallest holding the highest register any
    /// stage binds, SRVs the smallest above it (PRAGMATA: t0-t1 gets 4), the PS's UAVs the smallest holding its own (with
    /// <see cref="ReRule.CbvUavAbove"/>, CBVs and UAVs too the smallest above, as RE Village's: u0-u1 gets 4). Compute:
    /// the same once, visible to every stage, a table each, the largest CBV and SRV sizes and <see cref="ReRule.ComputeUavs"/>.
    /// Flags: input layout with a VS, a deny for each graphics stage the pipeline hasn't, the heap flag. Last, the bindless
    /// tables (<see cref="ReRule.BindlessSpaces"/>; RE Village's recording: 18708 of 18710 PSOs rebuilt byte for byte).</summary>
    public static Desc BuildRe(ReRule re, IReadOnlyDictionary<Stage, ShaderInfo> stages)
    {
        if (stages.ContainsKey(Stage.Compute)) return BuildRe(re, [Stage.Compute], re.CbvSizes[^1], re.SrvSizes[^1], 0);
        long End(IEnumerable<ShaderInfo> sh, string cls) => sh.SelectMany(s => s.Bindings).Where(b => b.Class == cls && b.Space == 0)
            .Select(b => b.Count < 0 ? long.MaxValue / 2 : (long)b.Lower + b.Count).DefaultIfEmpty(0).Max();
        var above = re.CbvUavAbove ? 1 : 0;
        return BuildRe(re, stages.Keys, Size(re.CbvSizes, End(stages.Values, "cbv") - re.CbvBase + above, "CBV"), Size(re.SrvSizes, End(stages.Values, "srv") + 1, "SRV"),
            Size(re.UavSizes, (stages.TryGetValue(Stage.Pixel, out var ps) ? End([ps], "uav") : 0) + above, "UAV"));

        static uint Size(uint[] sizes, long need, string what) =>
            sizes.FirstOrDefault(s => s >= need) is var n and > 0 ? n : throw new SerializeException($"no {what} table size holds {(need > 1_000_000 ? "an unbounded range" : $"{need} registers")}");
    }

    internal static Desc BuildRe(ReRule re, IEnumerable<Stage> stages, uint cbv, uint srv, uint uav)
    {
        var rows = new List<uint[]> { new uint[] { 1, 0, 0, 32, 1 } };
        var flags = re.HeapIndexed ? 0x400u : 0;   // CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED
        var set = stages.ToHashSet();
        if (set.Contains(Stage.Compute))
        {
            rows.AddRange([[0, 0, 2, cbv, re.CbvBase, 0, 0], [0, 0, 0, srv, 0, 0, 0], [0, 0, 1, re.ComputeUavs, 0, 0, 0], [0, 0, 3, 16, 0, 0, 0]]);
            for (var reg = 0u; reg < re.CbvBase; reg++) rows.Add([2, 0, reg, 0, 0]);
            AddBindless();
            return new Desc(flags | (re.DenyMesh ? 0x33eu : 0x3e), rows, AppendRanges: true, Version10: true);
        }
        foreach (var s in ReOrder.Where(set.Contains))
        {
            rows.Add(s == Stage.Pixel ? [0, Vis(s), 2, cbv, re.CbvBase, 0, 0, 0, srv, 0, 0, 0, 1, uav, 0, 0, 0] : [0, Vis(s), 2, cbv, re.CbvBase, 0, 0, 0, srv, 0, 0, 0]);
            rows.Add([0, Vis(s), 3, 16, 0, 0, 0]);
            for (var reg = 0u; reg < re.CbvBase; reg++) rows.Add([2, Vis(s), reg, 0, 0]);
        }
        AddBindless();
        if (set.Contains(Stage.Vertex)) flags |= 1;   // ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT
        foreach (var s in ReOrder.Where(s => !set.Contains(s) && (re.DenyMesh || s is not (Stage.Amplification or Stage.Mesh)))) flags |= DenyBit(s);
        return new Desc(flags, rows, AppendRanges: true, Version10: true);

        void AddBindless()
        {
            for (var space = 1u; space <= re.BindlessSpaces; space++) rows.Add([0, 0, 0, Unbounded, 0, space, 0, 0]);
        }
    }

    /// <summary>Stock UE (FD3D12RootSignatureDesc): per stage in priority order an SRV, sampler and UAV table (sizes from
    /// <see cref="Quantize"/>); then each stage's root CBVs b0..; then (5.x) the AGS intrinsics UAV, root constants and the
    /// diagnostic-buffer UAV (5.8: the NVIDIA extension UAV before it), all visible to every stage. Flags: bindless heaps (5.x), input layout, and a deny for every stage
    /// with nothing bound (compute signatures deny all graphics stages).</summary>
    static Desc BuildStock(PlanRule r, StockRule p, IReadOnlyDictionary<Stage, ShaderInfo> stages, bool meshTier, uint maxSrvs)
    {
        var all = p.Stages.Select(RulesFile.StageOf).ToArray();
        var order = stages.ContainsKey(Stage.Compute) ? [Stage.Compute] : all.Where(stages.ContainsKey).ToArray();
        var q = order.ToDictionary(s => s, s => Quantize(p, s, stages[s].Counts, maxSrvs));
        var (spaceBindless, bindless) = (Selects(r, "spaceBindless"), Selects(r, "bindless"));
        var rows = new List<uint[]>();
        foreach (var s in order)
        {
            var (srv, sampler, uav, _) = q[s];
            if (srv > 0) rows.Add([0, Vis(s), 0, srv, 0, 0, 5]);           // DESCRIPTORS_VOLATILE | DATA_STATIC_WHILE_SET_AT_EXECUTE
            if (spaceBindless) rows.AddRange(SpaceBindlessTables(s, stages[s]));
            if (sampler > 0) rows.Add([0, Vis(s), 3, sampler, 0, 0, 1]);   // DESCRIPTORS_VOLATILE
            if (uav > 0) rows.Add([0, Vis(s), 1, uav, 0, 0, 3]);           // DESCRIPTORS_VOLATILE | DATA_VOLATILE
            if (bindless) rows.AddRange(BindlessTables(s, stages[s]));
        }
        foreach (var s in order)
            for (var reg = 0u; reg < q[s].Cb; reg++) rows.Add([2, Vis(s), reg, 0, 8]); // DATA_STATIC
        var used = stages.Values.Aggregate(0, (f, i) => f | i.Counts.Flags);
        // ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT: from 4.22 whenever a vertex declaration is bound (UE always binds one); up to 4.21
        // only when it has elements (QuantizeBoundShaderState: InputLayout.NumElements > 0), taken as: the VS reads vertex input
        // (a VS without vertex inputs is drawn with UE's empty declaration; an unverified guess)
        var flags = !stages.TryGetValue(Stage.Vertex, out var vs) ? 0u
            : p.LayoutNeedsVertexInput && !vs.Inputs.Any(i => i.SysValue == 0) ? 0u : 1u;
        if (p.Ags && (used & UeFlags.AmdIntrinsics) != 0) rows.Add([4, 0, 0, 0x7FFF0ADE, 2]);        // u0, AGS_DX12_SHADER_INSTRINSICS_SPACE_ID
        if (p.RootConstants && (used & UeFlags.RootConstants) != 0) rows.Add([1, 0, 0, 3, 4]);       // b0 space 3 (UE_HLSL_SPACE_SHADER_ROOT_CONSTANTS), 4 values
        if (p.NvExtension && (used & UeFlags.NvIntrinsics) != 0) rows.Add([0, 0, 1, 1, 0, 1001, 0, 0]); // u0 space 1001 (UE_HLSL_SPACE_NV_SHADER_EXTN), offset 0
        if (p.Diagnostic && (used & UeFlags.DiagnosticBuffer) != 0) rows.Add([4, 0, 0, 999, 2]);     // u0 space 999 (UE_HLSL_SPACE_DIAGNOSTIC)
        if (p.HeapFlags && (used & UeFlags.BindlessResources) != 0) flags |= 0x400;                  // CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED
        if (p.HeapFlags && (used & UeFlags.BindlessSamplers) != 0) flags |= 0x800;                   // SAMPLER_HEAP_DIRECTLY_INDEXED
        foreach (var s in all)
            if ((!q.TryGetValue(s, out var c) || c == default) && (meshTier || s is not (Stage.Mesh or Stage.Amplification))) flags |= DenyBit(s);
        if (p.DenyMesh) flags |= DenyBit(Stage.Mesh) | DenyBit(Stage.Amplification);
        return new Desc(flags, rows);
    }

    /// <summary>Bindless SRV tables of a UE 4 fork (Respawn's, Star Wars Jedi: Survivor: 23626 recorded root signatures rebuilt
    /// exactly with these): a shader declaring an unbounded SRV range in space 10g..10g+4 gets, right after its stage's UAV table,
    /// one table per group g (ascending) of five unbounded SRV ranges t0 in spaces 10g..10g+4, all at offset 0 (typed views of
    /// one heap). Stock UE 4 has no bindless, so a stock shader never declares one and gets nothing; the marker is the shader's
    /// own bindings, not the game.</summary>
    static IEnumerable<uint[]> BindlessTables(Stage s, ShaderInfo sh) =>
        sh.Bindings.Where(b => b.Class == "srv" && b.Count == -1 && b.Space >= 10).Select(b => (uint)b.Space / 10).Distinct().Order()
            .Select(g => (uint[])[0, Vis(s), .. Enumerable.Range(0, 5).SelectMany(k => new[] { 0u, Unbounded, 0u, 10 * g + (uint)k, 5u })]);

    /// <summary>Bindless SRV tables of another UE 4 fork (Avalanche's 4.27, Hogwarts Legacy: 54657 recorded root signatures rebuilt
    /// exactly with these and <see cref="MaxSrvsFor"/>'s 128): a shader declaring an unbounded SRV range t0 in space 4..9 gets,
    /// right after its stage's SRV table, one single-range table per space from 4 up to the highest it declares (unbounded t0
    /// in space 4, 5, ...: a shader declaring only space 7 still gets 4, 5, 6 and 7). Like <see cref="BindlessTables"/>, the
    /// marker is the shader's own bindings.</summary>
    static IEnumerable<uint[]> SpaceBindlessTables(Stage s, ShaderInfo sh)
    {
        var top = sh.Bindings.Where(IsSpaceBindless).Select(b => b.Space).DefaultIfEmpty(3).Max();
        for (var k = 4u; k <= top; k++) yield return [0, Vis(s), 0, Unbounded, 0, k, 5];
    }

    static bool IsSpaceBindless(Binding b) => b is { Class: "srv", Count: -1, Space: >= 4 and < 10 };

    /// <summary>The MAX_SRVS a game was built with, from its own shaders: a project may raise the define (D3D12RHI.h: "Titles using
    /// many terrain layers may want to set MAX_SRVS to 64"), and a stock table can't hold a shader binding past it (the runtime
    /// rejects the pipeline). So when some shader needs more SRVs than the rule's, the next size UE ships (48, then 64) that
    /// holds them all; 0 = the rule's. Tiny Tina's Wonderlands (4.21, 48): 216 shaders bind up to t57 -> 64. Avalanche's 4.27 fork
    /// (its marker: bindless SRVs in spaces 4-9, <see cref="SpaceBindlessTables"/>) builds every SRV table with 128, though no
    /// shader binds more than 48 (Hogwarts Legacy's recording: 54657/54657).</summary>
    public static uint MaxSrvsFor(PlanRule r, IEnumerable<ShaderInfo> shaders)
    {
        if (Selects(r, "avalancheSrvs") && shaders.Any(s => s.Bindings.Any(IsSpaceBindless))) return 128;
        if (r.RootSignature?.Stock is not { GrowSrvs: true } p) return 0;
        var need = shaders.Select(s => s.Counts.Srv).DefaultIfEmpty(0).Max();
        return need <= p.MaxSrvs ? 0 : need <= 48 ? 48u : 64u;
    }

    /// <summary>InitShaderRegisterCounts at tier 3: tables at their maximum size (until 5.0 even when the stage binds none of
    /// that type; from 5.1 only when it binds some); UAVs only for the rule's UAV stages; CBs as root CBVs, all of them
    /// (MAX_ROOT_CBVS = MAX_CBS = 16, so the excess-CBV table never appears).</summary>
    static (uint Srv, uint Sampler, uint Uav, uint Cb) Quantize(StockRule p, Stage s, ResourceCounts c, uint maxSrvs)
    {
        var uavs = p.UavStages.Any(n => RulesFile.StageOf(n) == s);
        uint srv = maxSrvs > 0 ? maxSrvs : p.MaxSrvs, sampler = p.MaxSamplers, cb = (uint)Math.Min(c.Cb, 16);
        return !p.TablesWhenUsed
            ? (srv, sampler, uavs ? 16u : 0, cb)
            : (c.Srv > 0 ? srv : 0, c.Sampler > 0 ? sampler : 0, c.Uav > 0 && uavs ? 16u : 0, cb);
    }

    /// <summary>FF7 Rebirth's fork of 4.26: per stage an SRV table (64), sampler table (N), UAV table (16); then root CBVs;
    /// then one table per unbounded SRV range (bindless), in the shader's own order.</summary>
    public static Desc BuildUe(IReadOnlyDictionary<Stage, ShaderInfo> stages)
    {
        var order = stages.ContainsKey(Stage.Compute) ? [Stage.Compute] : Order.Where(stages.ContainsKey).ToArray();
        var rows = new List<uint[]>();
        foreach (var s in order)
        {
            var c = stages[s].Counts;
            if (c.Srv > 0) rows.Add([0, Vis(s), 0, 64, 0, 0, 5]);
            if (c.Sampler > 0) rows.Add([0, Vis(s), 3, (uint)c.Sampler, 0, 0, 1]);
            if (c.Uav > 0) rows.Add([0, Vis(s), 1, 16, 0, 0, 3]);
        }
        foreach (var s in order)
            for (var r = 0u; r < stages[s].Counts.Cb; r++) rows.Add([2, Vis(s), r, 0, 8]);
        foreach (var s in order)
            foreach (var b in stages[s].Bindings.Where(b => b.Class == "srv" && b.Count == -1))
                rows.Add([0, Vis(s), 0, Unbounded, (uint)b.Lower, (uint)b.Space, 5]);
        var flags = stages.ContainsKey(Stage.Vertex) ? 1u : 0; // ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT
        foreach (var (s, bit) in Deny)
            if (!order.Contains(s) || stages[s].Counts is { Cb: 0, Srv: 0, Uav: 0, Sampler: 0 }) flags |= bit;
        return new Desc(flags, rows);
    }

    /// <summary>What a serialized root signature gives each stage: its flags and every register range (visibility, range type
    /// SRV 0 / UAV 1 / CBV 2 / sampler 3, base, count (uint.MaxValue = unbounded), space, whether it's a descriptor table's
    /// range), root descriptors and constants as one-register ranges, static samplers included.</summary>
    public sealed record Ranges(uint Flags, (uint Vis, uint Type, uint Base, uint Count, uint Space, bool Table)[] Slots);

    public static Ranges Parse(byte[] blob)
    {
        var b = Rts0(blob);
        uint U(uint o) => BitConverter.ToUInt32(b, (int)o);
        var (ver, slots) = (U(0), new List<(uint, uint, uint, uint, uint, bool)>());
        for (var i = 0u; i < U(4); i++)
        {
            var (type, vis, p) = (U(U(8) + 12 * i), U(U(8) + 12 * i + 4), U(U(8) + 12 * i + 8));
            if (type == 0)
                for (var r = 0u; r < U(p); r++) { var q = U(p + 4) + (ver >= 2 ? 24u : 20u) * r; slots.Add((vis, U(q), U(q + 8), U(q + 4), U(q + 12), true)); }
            else slots.Add((vis, type switch { 3 => 0u, 4 => 1u, _ => 2u }, U(p), 1, U(p + 4), false)); // 1 constants and 2 CBV: b, 3 SRV, 4 UAV
        }
        for (var s = 0u; s < U(12); s++) { var q = U(16) + SamplerSize(ver) * s; slots.Add((U(q + 48), 3, U(q + 40), 1, U(q + 44), false)); }
        return new Ranges(U(20), [.. slots]);
    }

    /// <summary>The first resource a shader declares that the root signature doesn't give its stage (no range of that type and
    /// space visible to it covers the registers, or the stage is denied), as "stage class space"; null = all covered. The D3D12
    /// runtime rejects such a pipeline (E_INVALIDARG: "root signature doesn't match shader"). An unbounded shader range only
    /// needs a descriptor table range holding its first register (Control's bounded bindless tables: 1333 of 1333 PSOs and
    /// collections created on WARP); a root descriptor never serves one.</summary>
    public static string? Uncovered(Ranges rs, Stage stage, ShaderInfo sh)
    {
        var deny = stage switch { Stage.Vertex => 0x2u, Stage.Hull => 0x4u, Stage.Domain => 0x8u, Stage.Geometry => 0x10u, Stage.Pixel => 0x20u, Stage.Amplification => 0x100u, Stage.Mesh => 0x200u, _ => 0u };
        if (sh.Bindings.Count > 0 && (rs.Flags & deny) != 0) return $"{stage} denied";
        foreach (var b in sh.Bindings)
        {
            var type = b.Class switch { "srv" => 0u, "uav" => 1u, "cbv" => 2u, "sampler" => 3u, _ => 9u };
            if (type == 9) continue;
            if (!rs.Slots.Any(s => (s.Vis == 0 || s.Vis == Vis(stage)) && s.Type == type && s.Space == (uint)b.Space && s.Base <= (uint)b.Lower
                    && (s.Count == Unbounded || (b.Count >= 0 ? (ulong)b.Lower + (ulong)b.Count <= (ulong)s.Base + s.Count : s.Table && (ulong)b.Lower < (ulong)s.Base + s.Count))))
                return $"{stage} {b.Class} space {b.Space}{(b.Count < 0 ? " unbounded" : "")}";
        }
        return null;
    }

    /// <summary>Static samplers (raw 52-byte D3D12_STATIC_SAMPLER_DESCs) of a serialized root signature. A 1.2 signature's
    /// are DESC1s (56 bytes: + flags), given without the flags, which the planner's 1.1 signatures can't carry: one rebuilt
    /// from samplers that set them (a uint border color, non-normalized coordinates) never matches its 1.2 original.</summary>
    public static byte[] Samplers(byte[] blob)
    {
        var b = Rts0(blob);
        var (ver, n, off) = (BitConverter.ToUInt32(b, 0), BitConverter.ToInt32(b, 12), BitConverter.ToInt32(b, 16));
        var size = (int)SamplerSize(ver);
        return [.. Enumerable.Range(0, n).SelectMany(i => b[(off + size * i)..(off + size * i + 52)])];
    }

    /// <summary>The RTS0 part with each static sampler's parameters (filter to max LOD) zeroed, its register, space and
    /// visibility kept (<see cref="SamplerVariants"/>); null without static samplers.</summary>
    internal static byte[]? WithoutSamplerSettings(byte[] blob)
    {
        var b = Rts0(blob).ToArray();   // a bare RTS0 is the caller's own array
        var (ver, n, off) = (BitConverter.ToUInt32(b, 0), BitConverter.ToInt32(b, 12), BitConverter.ToInt32(b, 16));
        if (n == 0) return null;
        var size = (int)SamplerSize(ver);
        for (var i = 0; i < n; i++) b.AsSpan(off + size * i, 40).Clear();
        return b;
    }

    /// <summary>D3D12_STATIC_SAMPLER_DESC, or DESC1 in a 1.2 (version 3) signature.</summary>
    static uint SamplerSize(uint version) => version == 3 ? 56u : 52u;

    internal static byte[] Rts0(byte[] blob)
    {
        if (!blob.AsSpan(0, 4).SequenceEqual("DXBC"u8)) return blob;
        return Dxbc.Part(blob, "RTS0"u8) is { IsEmpty: false } rts ? rts.ToArray() : throw new InvalidDataException("no RTS0 part");
    }

    static readonly delegate* unmanaged<void*, nint*, nint*, int> SerializeFn = (delegate* unmanaged<void*, nint*, nint*, int>)NativeLibrary.GetExport(
        NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "d3d12.dll")), "D3D12SerializeVersionedRootSignature"); // System32: never a proxy d3d12.dll next to the app

    /// <summary>The runtime refused the description (E_INVALIDARG: overlapping registers, a bad sampler, ...).</summary>
    public sealed class SerializeException(string message) : InvalidOperationException(message);

    public static byte[] Serialize(Desc d, byte[] samplers)
    {
        // x64 layouts: D3D12_ROOT_PARAMETER(1) = 32 bytes (type @0, union @8, visibility @24), D3D12_DESCRIPTOR_RANGE1 = 6 x u32
        // (1.0: 5, no flags), D3D12_VERSIONED_ROOT_SIGNATURE_DESC = version @0, NumParameters @8, pParameters @16,
        // NumStaticSamplers @24, pStaticSamplers @32, Flags @40 (the same for 1.0 and 1.1).
        var n = d.Rows.Count;
        var stride = d.Version10 ? 5 : 6;
        var ranges = new uint[Math.Max(1, d.Rows.Where(r => r[0] == 0).Sum(r => (r.Length - 2) / 5)) * stride];
        var parms = new byte[Math.Max(1, n) * 32];
        var desc = new byte[48];
        var samp = samplers.Length > 0 ? samplers : new byte[1];
        fixed (uint* pr = ranges) fixed (byte* pp = parms, pd = desc, ps = samp)
        {
            var next = pr;
            for (var i = 0; i < n; i++)
            {
                var r = d.Rows[i];
                var p = pp + 32 * i;
                *(uint*)p = r[0];
                *(uint*)(p + 24) = r[1];
                if (r[0] == 0)
                {
                    var count = (r.Length - 2) / 5;
                    *(uint*)(p + 8) = (uint)count;
                    *(uint**)(p + 16) = next;
                    for (var j = 0; j < count; j++, next += stride)
                    {
                        for (var k = 0; k < stride - 1; k++) next[k] = r[2 + 5 * j + k];
                        next[stride - 1] = d.RangeOffsets ? r[2 + 5 * j + 4] : count == 1 ? r.Length == 8 ? r[7] : Append : d.AppendRanges ? Append : 0;
                    }
                }
                else for (var k = 0; k < (d.Version10 && r[0] != 1 ? 2 : 3); k++) ((uint*)(p + 8))[k] = r[2 + k];
            }
            *(uint*)pd = d.Version10 ? 1u : 2u; // D3D_ROOT_SIGNATURE_VERSION_1_0 / 1_1
            *(uint*)(pd + 8) = (uint)n;
            *(byte**)(pd + 16) = pp;
            *(uint*)(pd + 24) = (uint)(samplers.Length / 52);
            *(byte**)(pd + 32) = ps;
            *(uint*)(pd + 40) = d.Flags;
            return Call(pd);
        }
    }

    /// <summary>A serialized root signature (a container or its RTS0) again at version 1.0, as FromSoftware's engine creates
    /// its root signatures from the 1.1 ones its shaders carry: range and root descriptor flags dropped, each range at its
    /// explicit offset from the table's start (Elden Ring's recording: both of its root signatures for shipped shaders, byte
    /// for byte). A 1.2 signature's static sampler flags don't fit 1.0: SerializeException.</summary>
    public static byte[] AsVersion10(byte[] blob)
    {
        var b = Rts0(blob);
        uint U(uint o) => BitConverter.ToUInt32(b, (int)o);
        var (ver, n, at, ns, so) = (U(0), U(4), U(8), U(12), U(16));
        var rangeSize = ver >= 2 ? 24u : 20u;
        var ranges = new List<uint>();
        var first = new int[n];
        for (var i = 0u; i < n; i++)
        {
            first[i] = ranges.Count;
            if (U(at + 12 * i) != 0) continue;
            var p = U(at + 12 * i + 8);
            var offset = 0u;
            for (var r = 0u; r < U(p); r++)
            {
                var q = U(p + 4) + rangeSize * r;
                var count = U(q + 4);
                var own = U(q + rangeSize - 4);
                var start = own == Append ? offset : own;
                ranges.AddRange([U(q), count, U(q + 8), U(q + 12), start]);
                offset = count == Unbounded || start == Append ? Append : start + count;
            }
        }
        var samplers = new byte[Math.Max(1, ns) * 52];
        for (var s = 0u; s < ns; s++)
        {
            var q = (int)(so + SamplerSize(ver) * s);
            if (ver == 3 && BitConverter.ToUInt32(b, q + 52) != 0) throw new SerializeException("a 1.2 static sampler with flags has no 1.0 form");
            b.AsSpan(q, 52).CopyTo(samplers.AsSpan(52 * (int)s));
        }
        var rangeArray = ranges.Count > 0 ? ranges.ToArray() : new uint[1];
        var parms = new byte[Math.Max(1, n) * 32];
        var desc = new byte[48];
        fixed (uint* pr = rangeArray) fixed (byte* pp = parms, pd = desc, ps = samplers)
        {
            // D3D12_ROOT_PARAMETER: type @0, union @8, visibility @24; D3D12_DESCRIPTOR_RANGE = 5 x u32; root descriptor = register, space
            for (var i = 0u; i < n; i++)
            {
                var (type, payload) = (U(at + 12 * i), U(at + 12 * i + 8));
                var p = pp + 32 * i;
                *(uint*)p = type;
                *(uint*)(p + 24) = U(at + 12 * i + 4);
                if (type == 0)
                {
                    *(uint*)(p + 8) = U(payload);
                    *(uint**)(p + 16) = pr + first[i];
                }
                else for (var k = 0u; k < (type == 1 ? 3u : 2u); k++) ((uint*)(p + 8))[k] = U(payload + 4 * k);
            }
            *(uint*)pd = 1; // D3D_ROOT_SIGNATURE_VERSION_1_0
            *(uint*)(pd + 8) = n;
            *(byte**)(pd + 16) = pp;
            *(uint*)(pd + 24) = ns;
            *(byte**)(pd + 32) = ps;
            *(uint*)(pd + 40) = U(20);
            return Call(pd);
        }
    }

    static byte[] Call(byte* desc)
    {
        nint blob = 0, err = 0;
        var hr = SerializeFn(desc, &blob, &err);
        if (err != 0) Release(err);
        if (hr < 0) throw new SerializeException($"D3D12SerializeVersionedRootSignature failed 0x{hr:x8}");
        var vt = *(nint**)blob; // ID3DBlob: GetBufferPointer = slot 3, GetBufferSize = slot 4
        var bytes = new ReadOnlySpan<byte>(((delegate* unmanaged<nint, void*>)vt[3])(blob), (int)((delegate* unmanaged<nint, nuint>)vt[4])(blob)).ToArray();
        Release(blob);
        return bytes;
        static void Release(nint unk) => ((delegate* unmanaged<nint, uint>)(*(nint**)unk)[2])(unk);
    }
}
