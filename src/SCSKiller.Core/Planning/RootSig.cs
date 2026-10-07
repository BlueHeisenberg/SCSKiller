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
/// REDengine 3's are one per kind of pipeline (<see cref="BuildRed3"/>), Northlight's one graphics and one compute
/// (<see cref="BuildNorthlight"/>).</summary>
public static unsafe class RootSig
{
    /// <summary>One construction rule per range of engine versions (each rule holds until the next one's version). 4.20/4.21
    /// come from Epic's 4.20.3/4.21.2 D3D12RHI source: the raster root signature is built exactly as in 4.22 (4.22 only added
    /// ray-tracing signatures); MAX_SRVS is 32 in 4.20, 48 in 4.21; the input-layout flag differs (see BuildStock).</summary>
    public enum Rule
    {
        Ff7,    // FF7 Rebirth's fork (recorded): see BuildUe
        Ue420,  // 4.20: MAX_SRVS 32, no static samplers; the input-layout flag only with a non-empty vertex declaration
        Ue421,  // 4.21: MAX_SRVS 48, otherwise 4.20's
        Ue422,  // 4.22-4.24: MAX_SRVS 48, no static samplers
        Ue425,  // 4.25: MAX_SRVS 64, static samplers s1000-s1005 in space 0
        Ue426,  // 4.26-4.27: static samplers s0-s5 in space 1000
        Kuro,   // Kuro's 4.26 fork (Wuthering Waves): 4.26's, every root signature also denying the mesh and amplification stages as UE 5's SM6 path does (see BuildStock); before Ue50 for its UE 4 comparisons
        Ue50,   // 5.0: no hull/domain, mesh/amplification stages, AGS/diagnostic root UAVs, bindless heap flags
        Ue51,   // 5.1-5.3: a stage's table only when it uses that resource type
        Ue54,   // 5.4: MAX_SAMPLERS 32, root constants
        Ue55,   // 5.5-5.7: vertex shaders get UAVs
        Ue58,   // 5.8+: mesh/amplification shaders get UAVs; NVIDIA shader extensions get a u0 space 1001 table
        Red3,   // REDengine 3 (The Witcher 3, DX12): three fixed root signatures, see BuildRed3
        Northlight, // Northlight (Control, DX12): one graphics and one compute root signature, see BuildNorthlight
        Dagor,  // Dagor Engine (DX12): built from each shader's header, constant buffers as root CBVs, see DagorRootSig
        DagorCbvRanges,   // the same with constant buffers in descriptor tables (War Thunder)
    }

    /// <summary>The rule a game's engine (an Unreal version, REDengine 3) builds with; null = none known. A fork other than FF7's gets its base
    /// version's stock rule, which the fork may have changed (FF7's did).</summary>
    public static Rule? RuleFor(EngineInfo e)
    {
        if (e.Family == RedEngine.RedEngineReader.Family) return Rule.Red3;
        if (e.Family == Northlight.NorthlightReader.Family) return Rule.Northlight;
        if (e.Family == Dagor.DagorReader.Family) return e.Fork == Dagor.DagorReader.CbvRangesFork ? Rule.DagorCbvRanges : Rule.Dagor;
        if (e.Family != "Unreal" || !System.Version.TryParse(e.Version, out var v)) return null;
        if (e.Fork == "GAME_FinalFantasy7Rebirth" && e.Version == "4.26") return Rule.Ff7;
        if (e.Fork == "GAME_WutheringWaves" && e.Version == "4.26") return Rule.Kuro;
        return (v.Major, v.Minor) switch
        {
            (4, 20) => Rule.Ue420,
            (4, 21) => Rule.Ue421,
            (4, >= 22 and <= 24) => Rule.Ue422,
            (4, 25) => Rule.Ue425,
            (4, 26 or 27) => Rule.Ue426,
            (5, 0) => Rule.Ue50,
            (5, <= 3) => Rule.Ue51,
            (5, 4) => Rule.Ue54,
            (5, <= 7) => Rule.Ue55,
            (5, _) or (6, 0) => Rule.Ue58, // 6.0: Fortnite's internal line, which its containers tell as 5.8
            _ => null,
        };
    }

    /// <summary>The engine's rule is confirmed by a real game (<see cref="ConfirmedEngines"/>, per version and fork). Another
    /// fork (e.g. Stellar Blade's) may add slots no shader shows, so it stays unconfirmed.</summary>
    public static bool Verified(EngineInfo e) => RuleFor(e) is { } r && (r == Rule.Red3 || ConfirmedEngines.Current.Contains(e)); // one REDengine 3 build: its recording confirmed it

    /// <summary>UE's six static samplers (space 1000, s0-s5): point/bilinear/trilinear x wrap/clamp, 52 bytes each.</summary>
    public static readonly byte[] Ue426Samplers = UeSamplers(0, 1000);

    /// <summary>The static samplers a rule's root signatures carry (4.25: the same six at s1000-s1005 in space 0; before: none).</summary>
    public static byte[] StaticSamplers(Rule r) => r switch { Rule.Ue420 or Rule.Ue421 or Rule.Ue422 or Rule.Red3 or Rule.Northlight or Rule.Dagor or Rule.DagorCbvRanges => [], Rule.Ue425 => Ue425Samplers, _ => Ue426Samplers };

    static readonly byte[] Ue425Samplers = UeSamplers(1000, 0);

    static byte[] UeSamplers(uint reg, uint space)
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
    public static Desc Build(Rule r, IReadOnlyDictionary<Stage, ShaderInfo> stages, bool meshTier, uint maxSrvs = 0) =>
        r switch { Rule.Ff7 => BuildUe(stages), Rule.Red3 => BuildRed3(stages), Rule.Northlight => BuildNorthlight(stages), Rule.Dagor => Dagor.DagorRootSig.Build(stages), Rule.DagorCbvRanges => Dagor.DagorRootSig.Build(stages, cbvRanges: true), _ => BuildStock(r, stages, meshTier, maxSrvs) };

    /// <summary>The stage sets the recording confirmed <see cref="BuildRed3"/> on: VS, VS+PS, VS+HS+DS, VS+HS+DS+PS,
    /// VS+GS+PS, VS+GS+HS+DS, CS.</summary>
    public static bool Red3Validated(IEnumerable<Stage> stages) => Red3Sets.Contains(Mask(stages));

    static int Mask(IEnumerable<Stage> stages) => stages.Aggregate(0, (m, s) => m | 1 << (int)s);

    static readonly HashSet<int> Red3Sets = new[]
    {
        new[] { Stage.Vertex }, [Stage.Vertex, Stage.Pixel], [Stage.Vertex, Stage.Hull, Stage.Domain], [Stage.Vertex, Stage.Hull, Stage.Domain, Stage.Pixel],
        [Stage.Vertex, Stage.Geometry, Stage.Pixel], [Stage.Vertex, Stage.Geometry, Stage.Hull, Stage.Domain], [Stage.Compute],
    }.Select(Mask).ToHashSet();

    /// <summary>REDengine 3's root signatures depend on the pipeline's stages only: compute; VS (+ PS); and with a GS, HS or DS
    /// the same plus a CBV, SRV and two sampler tables for each of those three (and stream output allowed). All tables are
    /// volatile, root CBVs static while set. The Witcher 3's recording: 586 of 586 PSOs of its own shaders (VS and VS+PS
    /// 487, tessellation and GS 6, compute 93) use exactly these.</summary>
    static Desc BuildRed3(IReadOnlyDictionary<Stage, ShaderInfo> stages)
    {
        if (!Red3Validated(stages.Keys)) throw new SerializeException($"no REDengine 3 root signature confirmed for {string.Join('+', stages.Keys)}");
        if (stages.ContainsKey(Stage.Compute))
            return new(0, [[2, 0, 0, 0, 2], [0, 0, 2, 15, 1, 0, 1], [0, 0, 0, 8, 0, 0, 1], [0, 0, 0, 11, 8, 0, 1], [0, 0, 0, 13, 19, 0, 1],
                [0, 0, 0, 31, 32, 0, 1], [0, 0, 1, 16, 0, 0, 1], [0, 0, 3, 8, 0, 0, 0], [0, 0, 3, 8, 8, 0, 0]]);
        var rows = new List<uint[]>();
        foreach (var vis in new uint[] { 5, 1 }) for (var b = 0u; b < 5; b++) rows.Add([2, vis, b, 0, 2]);
        rows.AddRange([[0, 5, 2, 11, 5, 0, 1], [0, 5, 0, 8, 0, 0, 1], [0, 5, 0, 11, 8, 0, 1], [0, 5, 0, 13, 19, 0, 1], [0, 5, 0, 31, 32, 0, 1],
            [0, 5, 3, 8, 0, 0, 0], [0, 5, 3, 8, 8, 0, 0], [0, 1, 2, 11, 5, 0, 1], [0, 1, 0, 8, 0, 0, 1], [0, 1, 0, 56, 8, 0, 1],
            [0, 1, 3, 8, 0, 0, 0], [0, 1, 3, 8, 8, 0, 0], [0, 0, 1, 4, 0, 0, 1]]);
        if (!stages.Keys.Any(s => s is Stage.Geometry or Stage.Hull or Stage.Domain)) return new(0x1D, rows); // input layout; HS, DS, GS denied
        foreach (var vis in new[] { Vis(Stage.Geometry), Vis(Stage.Hull), Vis(Stage.Domain) })
            rows.AddRange([[0, vis, 2, 32, 0, 0, 1], [0, vis, 0, 64, 0, 0, 1], [0, vis, 3, 8, 0, 0, 0], [0, vis, 3, 8, 8, 0, 0]]);
        return new(0x41, rows); // input layout, stream output
    }

    /// <summary>Northlight's root signatures, version 1.0, as Control's renderer (d3d_*.dll) builds them in code: per stage a
    /// table of 5 CBVs (b4 for VS and PS, b0 for HS and DS), 36 SRVs and 8 UAVs, then a table of 4 samplers per stage, root
    /// CBVs b0-b3 for VS and PS, a table of 64 samplers in space 1, and a table of 244000 SRVs in space 1 for the PS; input
    /// layout, GS denied. Compute: the same tables and root CBVs once, all visible to every stage, the SRV table too; no flags.</summary>
    static Desc BuildNorthlight(IReadOnlyDictionary<Stage, ShaderInfo> stages)
    {
        if (stages.ContainsKey(Stage.Compute)) return NorthlightCompute;
        if (stages.ContainsKey(Stage.Geometry) || stages.Keys.Any(s => s is Stage.Mesh or Stage.Amplification))
            throw new SerializeException($"Northlight has no root signature for {string.Join('+', stages.Keys)}");
        static uint[] Table(uint vis, uint cbvBase) => [0, vis, 2, 5, cbvBase, 0, 0, 0, 36, 0, 0, 0, 1, 8, 0, 0, 0];
        uint[] vps = [Vis(Stage.Vertex), Vis(Stage.Pixel)], hds = [Vis(Stage.Hull), Vis(Stage.Domain)];
        return new(0x11, [
            .. vps.Select(v => Table(v, 4)), .. hds.Select(v => Table(v, 0)),
            .. vps.Concat(hds).Select(v => new uint[] { 0, v, 3, 4, 0, 0, 0 }),
            .. vps.SelectMany(v => Enumerable.Range(0, 4).Select(b => new uint[] { 2, v, (uint)b, 0, 0 })),
            [0, 0, 3, 64, 0, 1, 0], [0, Vis(Stage.Pixel), 0, 244000, 0, 1, 0]], AppendRanges: true, Version10: true);
    }

    /// <summary>Northlight's compute root signature, also its global ray tracing one (<see cref="BuildNorthlight"/>).</summary>
    public static readonly Desc NorthlightCompute = new(0, [
        [0, 0, 2, 5, 4, 0, 0, 0, 36, 0, 0, 0, 1, 8, 0, 0, 0], [0, 0, 3, 4, 0, 0, 0],
        .. Enumerable.Range(0, 4).Select(b => new uint[] { 2, 0, (uint)b, 0, 0 }),
        [0, 0, 3, 64, 0, 1, 0], [0, 0, 0, 244000, 0, 1, 0]], AppendRanges: true, Version10: true);

    static readonly Stage[] Ue4Stages = [Stage.Pixel, Stage.Vertex, Stage.Geometry, Stage.Hull, Stage.Domain];
    static readonly Stage[] Ue5Stages = [Stage.Pixel, Stage.Vertex, Stage.Geometry, Stage.Mesh, Stage.Amplification];

    /// <summary>Stock UE (FD3D12RootSignatureDesc): per stage in priority order an SRV, sampler and UAV table (sizes from
    /// <see cref="Quantize"/>); then each stage's root CBVs b0..; then (5.x) the AGS intrinsics UAV, root constants and the
    /// diagnostic-buffer UAV, all visible to every stage. Flags: bindless heaps (5.x), input layout, and a deny for every stage
    /// with nothing bound (compute signatures deny all graphics stages).</summary>
    static Desc BuildStock(Rule r, IReadOnlyDictionary<Stage, ShaderInfo> stages, bool meshTier, uint maxSrvs)
    {
        var all = r >= Rule.Ue50 ? Ue5Stages : Ue4Stages;
        var order = stages.ContainsKey(Stage.Compute) ? [Stage.Compute] : all.Where(stages.ContainsKey).ToArray();
        var q = order.ToDictionary(s => s, s => Quantize(r, s, stages[s].Counts, maxSrvs));
        var rows = new List<uint[]>();
        foreach (var s in order)
        {
            var (srv, sampler, uav, _) = q[s];
            if (srv > 0) rows.Add([0, Vis(s), 0, srv, 0, 0, 5]);           // DESCRIPTORS_VOLATILE | DATA_STATIC_WHILE_SET_AT_EXECUTE
            if (r < Rule.Ue50) rows.AddRange(SpaceBindlessTables(s, stages[s]));
            if (sampler > 0) rows.Add([0, Vis(s), 3, sampler, 0, 0, 1]);   // DESCRIPTORS_VOLATILE
            if (uav > 0) rows.Add([0, Vis(s), 1, uav, 0, 0, 3]);           // DESCRIPTORS_VOLATILE | DATA_VOLATILE
            if (r < Rule.Ue50) rows.AddRange(BindlessTables(s, stages[s]));
        }
        foreach (var s in order)
            for (var reg = 0u; reg < q[s].Cb; reg++) rows.Add([2, Vis(s), reg, 0, 8]); // DATA_STATIC
        var used = stages.Values.Aggregate(0, (f, i) => f | i.Counts.Flags);
        // ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT: from 4.22 whenever a vertex declaration is bound (UE always binds one); up to 4.21
        // only when it has elements (QuantizeBoundShaderState: InputLayout.NumElements > 0), taken as: the VS reads vertex input
        // (a VS without vertex inputs is drawn with UE's empty declaration; an unverified guess)
        var flags = !stages.TryGetValue(Stage.Vertex, out var vs) ? 0u
            : r is Rule.Ue420 or Rule.Ue421 && !vs.Inputs.Any(i => i.SysValue == 0) ? 0u : 1u;
        if (r >= Rule.Ue50)
        {
            if ((used & UeFlags.AmdIntrinsics) != 0) rows.Add([4, 0, 0, 0x7FFF0ADE, 2]);            // u0, AGS_DX12_SHADER_INSTRINSICS_SPACE_ID
            if (r >= Rule.Ue54 && (used & UeFlags.RootConstants) != 0) rows.Add([1, 0, 0, 3, 4]); // b0 space 3 (UE_HLSL_SPACE_SHADER_ROOT_CONSTANTS), 4 values
            if (r >= Rule.Ue58 && (used & UeFlags.NvIntrinsics) != 0) rows.Add([0, 0, 1, 1, 0, 1001, 0, 0]);  // u0 space 1001 (UE_HLSL_SPACE_NV_SHADER_EXTN), offset 0
            if ((used & UeFlags.DiagnosticBuffer) != 0) rows.Add([4, 0, 0, 999, 2]);              // u0 space 999 (UE_HLSL_SPACE_DIAGNOSTIC)
            if ((used & UeFlags.BindlessResources) != 0) flags |= 0x400;                          // CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED
            if ((used & UeFlags.BindlessSamplers) != 0) flags |= 0x800;                           // SAMPLER_HEAP_DIRECTLY_INDEXED
        }
        foreach (var s in all)
            if ((!q.TryGetValue(s, out var c) || c == default) && (meshTier || s is not (Stage.Mesh or Stage.Amplification))) flags |= DenyBit(s);
        // Kuro's 4.26 fork: every root signature denies the mesh and amplification stages, as UE 5's SM6 path does (the fork ships a
        // PCD3D_SM6 platform stock 4.26 lacks, and its pipeline cache bodies carry UE 5's MS/AS hashes too); a recording of Wuthering
        // Waves: 113 of 113 distinct root signatures (5725 of 5725 PSOs) differ from 4.26's in these two bits only
        if (r == Rule.Kuro) flags |= DenyBit(Stage.Mesh) | DenyBit(Stage.Amplification);
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

    /// <summary>D3D12RHI.h's MAX_SRVS: the size of every SRV table.</summary>
    public static uint MaxSrvs(Rule r) => r switch { Rule.Ue420 => 32u, Rule.Ue421 or Rule.Ue422 => 48u, _ => 64u };

    /// <summary>The MAX_SRVS a game was built with, from its own shaders: a project may raise the define (D3D12RHI.h: "Titles using
    /// many terrain layers may want to set MAX_SRVS to 64"), and a stock table can't hold a shader binding past it (the runtime
    /// rejects the pipeline). So when some shader needs more SRVs than the rule's, the next size UE ships (48, then 64) that
    /// holds them all; 0 = the rule's. Tiny Tina's Wonderlands (4.21, 48): 216 shaders bind up to t57 -> 64. Avalanche's 4.27 fork
    /// (its marker: bindless SRVs in spaces 4-9, <see cref="SpaceBindlessTables"/>) builds every SRV table with 128, though no
    /// shader binds more than 48 (Hogwarts Legacy's recording: 54657/54657).</summary>
    public static uint MaxSrvsFor(Rule r, IEnumerable<ShaderInfo> shaders)
    {
        if (r == Rule.Ue426 && shaders.Any(s => s.Bindings.Any(IsSpaceBindless))) return 128;
        if (r == Rule.Ff7 || r > Rule.Ue422) return 0;
        var need = shaders.Select(s => s.Counts.Srv).DefaultIfEmpty(0).Max();
        return need <= MaxSrvs(r) ? 0 : need <= 48 ? 48u : 64u;
    }

    /// <summary>InitShaderRegisterCounts at tier 3: tables at their maximum size (until 5.0 even when the stage binds none of
    /// that type; from 5.1 only when it binds some); UAVs only for pixel/compute (5.5: vertex too; 5.8: mesh, amplification too); CBs as root CBVs, all of
    /// them (MAX_ROOT_CBVS = MAX_CBS = 16, so the excess-CBV table never appears).</summary>
    static (uint Srv, uint Sampler, uint Uav, uint Cb) Quantize(Rule r, Stage s, ResourceCounts c, uint maxSrvs)
    {
        var uavs = s is Stage.Pixel or Stage.Compute || (s == Stage.Vertex && r >= Rule.Ue55) || (s is Stage.Mesh or Stage.Amplification && r >= Rule.Ue58);
        uint srv = maxSrvs > 0 ? maxSrvs : MaxSrvs(r), sampler = r >= Rule.Ue54 ? 32u : 16, cb = (uint)Math.Min(c.Cb, 16);
        return r <= Rule.Ue50
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
