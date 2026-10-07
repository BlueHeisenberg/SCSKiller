using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>Which VS layouts get Unreal's fewer-UV-channel variants (<see cref="ExactLayouts.UvClampVariants"/>). FF7 from
/// amd-session1 (81432 plan PSOs without): Recorded (the VS's own recorded layouts, Exact) +707 PSOs (+0.9%); inferred
/// and guessed ones too would be +14009 (+17.2%). Session 2 had 4 PSOs of this kind (1.6-3.4 ms in game): Recorded covers 3.</summary>
public enum UvClampLayouts { None, Recorded }

/// <summary>Where a resolved piece of pipeline state came from. Exact: recorded for this very shader. Inferred: recorded
/// for another shader with the same interface (input or output signature). Guessed: synthesized.</summary>
public enum Provenance { Exact, Inferred, Guessed }

public readonly record struct Resolved<T>(T Value, Provenance Provenance);

/// <summary>Which pipeline state a vendor's per-stage cache keys each stage on, beyond the shader and the root signature
/// (<see cref="VendorCaps.PerStageCache"/>). AMD (probe 6, ARCHITECTURE.md): the VS on the input elements it reads and the
/// topology type, the PS on its exports' format shape / write mask 0 / logic op / dual-source blending, both on the root
/// signature up to its DENY flags; a VS alone (depth pass), a VS feeding a PS and a VS feeding a GS are separate VS
/// compiles. The probes (probes-2) found a VS keyed only on its ALL/VERTEX parameters and static samplers, but FF7's own
/// PSOs didn't bear it out (AMD A/B, session 2's 220 new PSOs after a warm of session 1's plan, fresh exe names: 46 still
/// slow with the VS-visible key, 34 with the whole root signature, for +0.9% plan PSOs), so AMD keys the VS on the whole
/// root signature too. NVIDIA (probe 6b): the shader and the whole root
/// signature (any change is FULL, DENY flags too); layout, formats, topology and the next stage are FREE.</summary>
/// <param name="ReadLayout">a VS unit keys on the layout elements its input signature declares</param>
/// <param name="NextStage">a VS unit keys on what follows it: a PS, nothing (depth pass), a GS or a HS</param>
/// <param name="ExportShape">a PS unit keys on its render targets' <see cref="ExactLayouts.ExportShape"/></param>
/// <param name="FreeRsFlags">root-signature flag bits no key includes</param>
/// <param name="UvClamp">which resolved VS layouts also get their <see cref="ExactLayouts.UvClampVariants"/></param>
/// <param name="PartnerReads">a VS unit keys on how its PS consumes the VS's outputs (<see cref="ExactLayouts.PartnerKeys"/>:
/// the components it reads and their interpolation), where the PS's bytes are known</param>
public sealed record UnitPolicy(string Name, bool ReadLayout, bool Topology, bool NextStage, bool ExportShape, uint FreeRsFlags,
    bool PartnerReads = false, UvClampLayouts UvClamp = UvClampLayouts.None)
{
    /// <summary>D3D12_ROOT_SIGNATURE_FLAG_DENY_{VERTEX,HULL,DOMAIN,GEOMETRY,PIXEL,AMPLIFICATION,MESH}_SHADER_ROOT_ACCESS.</summary>
    public const uint DenyFlags = 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x100 | 0x200;

    public static readonly UnitPolicy Amd = new("amd", true, true, true, true, DenyFlags, true, UvClampLayouts.Recorded);
    public static readonly UnitPolicy Nvidia = new("nvidia", false, false, false, false, 0);

    /// <summary>The policy of a per-stage cache; null when the vendor caches whole pipelines. ponytail: picked from the
    /// caps' state independence until a vendor needs a third shape.</summary>
    public static UnitPolicy? For(VendorCaps caps) => !caps.PerStageCache ? null : caps.StateIndependentCache ? Nvidia : Amd;
}

/// <summary>What a recording says about the state each stage of a per-stage cache is compiled for (pure; no plan output).
/// Built from a recording's PSOs plus the shaders' signatures (from its blobs or an index), it answers, for a shader of
/// the library: which input layouts a VS is fed (<see cref="Layouts"/>), which render-target shapes a PS exports to
/// (<see cref="Shapes"/>), which topology types a VS is drawn with (<see cref="Topologies"/>), each with its provenance.
/// Only the fields in the AMD key are kept: a VS's read input elements (order, semantic case and unread elements are FREE),
/// the export shape per RT slot, and the root signature up to its DENY flags.</summary>
public sealed class ExactLayouts
{
    public UnitPolicy Policy { get; }
    public IReadOnlyDictionary<string, ShaderInfo> Shaders { get; }
    /// <summary>Per pixel shader whose read masks are known (<see cref="PartnerKey"/>): how it consumes its inputs, as a VS
    /// unit keys it under <see cref="UnitPolicy.PartnerReads"/>: each non-system input's read mask and interpolation mode
    /// (AMD compiles the VS for them: selftest probe 6 "deadps", FF7 session 2's A/B).</summary>
    public Dictionary<string, string> PartnerKeys { get; } = [];

    /// <summary>VS -> the distinct read layouts it was recorded with (first-seen order).</summary>
    public Dictionary<string, List<List<LayoutElem>>> ReadLayouts { get; } = [];
    /// <summary><see cref="SigKey"/> -> the distinct read layouts of the VSs with that input signature.</summary>
    public Dictionary<string, List<List<LayoutElem>>> LayoutsBySig { get; } = [];
    /// <summary>VS -> the topology types it was recorded with.</summary>
    public Dictionary<string, SortedSet<uint>> TopoOf { get; } = [];
    /// <summary>PS -> the distinct export shapes it was recorded with (first-seen order).</summary>
    public Dictionary<string, List<string>> ShapesByPs { get; } = [];
    /// <summary>PS output signature -> export shape -> how many recorded PSOs used it.</summary>
    public Dictionary<string, Dictionary<string, int>> ShapesByOutSig { get; } = [];
    /// <summary>Export shape -> one recorded (formats, write masks, logic ops) with that shape, to build a PSO from.</summary>
    public Dictionary<string, (uint[] Formats, byte[] Masks, int[] LogicOps)> ShapeExample { get; } = [];
    /// <summary>The pipeline state a stage is linked with besides its key (blend, depth-stencil desc, DSV format), as recorded:
    /// per (PS, export shape) the first seen, per (PS output signature, shape) and per shape how often each was used. AMD
    /// (FF7's own PSOs, fresh exe names): stages compiled under the planner's neutral state link
    /// into the game's PSO in ~3 ms (a relink), under its blend + depth state in ~0.35 ms (an exact hit); either one alone
    /// still ~3 ms, the rasterizer doesn't matter.</summary>
    readonly Dictionary<string, LinkState> linkByPs = [];
    readonly Dictionary<string, Dictionary<string, (LinkState State, int N)>> linkCounts = [];

    /// <summary>Blend desc + depth-stencil desc (canonical, see <see cref="PsoState"/>) + DSV format.</summary>
    public sealed record LinkState(byte[] Blend, byte[] DepthStencil, uint Dsv)
    {
        public string Key => $"{Hex(Blend)}|{Hex(DepthStencil)}|{Dsv}";
    }

    /// <summary>Every distinct full recorded layout (explicit offsets), for projecting onto an unrecorded VS.</summary>
    public List<List<LayoutElem>> FullLayouts { get; } = [];
    /// <summary>Recorded graphics PSOs (with their parsed state) and compute PSOs.</summary>
    public List<PsoState> Graphics { get; } = [];
    public List<Pso> Compute { get; } = [];

    readonly Dictionary<string, string> rsKeys = [];
    readonly Dictionary<(string, int, int), Dictionary<LayoutElem, int>> elemCounts = []; // (SEMANTIC, index, type) -> recorded elements

    ExactLayouts(UnitPolicy policy, IReadOnlyDictionary<string, ShaderInfo> shaders)
    {
        Policy = policy;
        Shaders = shaders;
    }

    /// <summary>From a proxy db that carries its blobs (a recording, rehydrated if it was hash-only).</summary>
    public static ExactLayouts FromDb(string db, UnitPolicy policy, IReadOnlyDictionary<string, ShaderInfo>? index = null)
    {
        var recs = new List<Rec>();
        var blobs = new Dictionary<string, byte[]>();
        foreach (var r in Read(db))
            if (r.Tag == 'B') blobs[Hex(r.Payload.AsSpan(0, 20))] = r.Payload[20..];
            else recs.Add(r);
        return Build(recs, blobs, policy, index);
    }

    /// <param name="blobs">sha1 -> bytes: the recorded root signatures (for <see cref="RsKey"/>) and shaders (their signatures
    /// when <paramref name="index"/> lacks them, and their read masks: an index may not carry those)</param>
    public static ExactLayouts Build(IEnumerable<Rec> records, IReadOnlyDictionary<string, byte[]> blobs, UnitPolicy policy,
        IReadOnlyDictionary<string, ShaderInfo>? index = null)
    {
        var shaders = new Dictionary<string, ShaderInfo>();
        if (index != null) foreach (var (h, s) in index) shaders[h] = s;
        var x = new ExactLayouts(policy, shaders);
        foreach (var (h, b) in blobs)
        {
            if (b.Length < 32 || !b.AsSpan(0, 4).SequenceEqual("DXBC"u8)) continue;
            ShaderInfo? s = null;
            try { s = ShaderContainer.Parse(b, h, new ResourceCounts(0, 0, 0, 0)); }
            catch (Exception) { } // a malformed blob
            if (s == null) continue; // a root signature's container (RTS0 only)
            shaders.TryAdd(h, s);
            if (s.Stage == Stage.Pixel && PartnerKey(s) is { } key) x.PartnerKeys[h] = key;
        }
        foreach (var s in shaders.Values) // the index's own (read masks + interpolation in the signatures)
            if (s.Stage == Stage.Pixel && !x.PartnerKeys.ContainsKey(s.Sha1) && PartnerKey(s) is { } key) x.PartnerKeys[s.Sha1] = key;
        foreach (var (h, b) in blobs)
            if (!shaders.ContainsKey(h))
                try { x.rsKeys[h] = RsKeyOf(b, policy.FreeRsFlags); } catch (Exception) { }
        foreach (var r in records)
        {
            if (r.Tag is not ('G' or 'C' or 'S')) continue;
            var pso = Parse(r);
            if (pso.Stages.ContainsKey((int)Stage.Compute)) { x.Compute.Add(pso); continue; }
            x.Add(ParseState(r)!);
        }
        return x;
    }

    void Add(PsoState s)
    {
        Graphics.Add(s);
        if (s.Stages.TryGetValue((int)Stage.Vertex, out var vs))
        {
            if (!TopoOf.TryGetValue(vs, out var topos)) TopoOf[vs] = topos = [];
            topos.Add(s.Topology);
            AddLayout(vs, s.Layout);
        }
        if (s.Stages.TryGetValue((int)Stage.Pixel, out var ps))
        {
            var shape = ExportShape(s);
            ShapeExample.TryAdd(shape, (s.RtFormats, s.RtWriteMasks, s.LogicOps));
            var l = Get(ShapesByPs, ps);
            if (!l.Contains(shape)) l.Add(shape);
            if (Shaders.TryGetValue(ps, out var pi))
            {
                var d = Get(ShapesByOutSig, OutSig(pi));
                d[shape] = d.GetValueOrDefault(shape) + 1;
            }
            if (s.Blend != null && s.DepthStencil != null)
            {
                var link = new LinkState(s.Blend, s.DepthStencil, s.Dsv);
                linkByPs.TryAdd($"{ps}|{shape}", link);
                void Tally(string k) { var c = Get(linkCounts, k); c[link.Key] = (link, c.GetValueOrDefault(link.Key).N + 1); }
                Tally(shape);
                if (pi != null) Tally($"{OutSig(pi)}|{shape}");
            }
        }
    }

    /// <summary>An input layout the game creates a pipeline of <paramref name="vs"/> with: a recorded PSO's, or a shipped
    /// pipeline cache's vertex declaration (<see cref="ShaderMap.Layout"/>). Exact for that VS, inferred for every VS with its
    /// input signature, and a vote for guessed layouts' element shapes.</summary>
    public void AddLayout(string vs, IReadOnlyList<LayoutElem> layout)
    {
        var full = Explicit(layout);
        AddDistinct(FullLayouts, full);
        foreach (var e in full.Where(e => e.Offset != AppendAligned))
        {
            var k = (e.Semantic.ToUpperInvariant(), e.Index, CompClass(e.Format));
            if (!elemCounts.TryGetValue(k, out var c)) elemCounts[k] = c = [];
            var v = e with { Semantic = k.Item1 };
            c[v] = c.GetValueOrDefault(v) + 1;
        }
        if (Shaders.TryGetValue(vs, out var vi))
        {
            var read = ReadLayout(full, vi);
            AddDistinct(Get(ReadLayouts, vs), read);
            AddDistinct(Get(LayoutsBySig, SigKey(vi)), read);
        }
    }

    /// <summary>See <see cref="PartnerKeys"/>: "SEMANTICindex:readmask/interpolation" per non-system input
    /// (<see cref="SigElement.ReadMask"/>, <see cref="SigElement.Interpolation"/>), sorted; null when one's read mask is unknown.</summary>
    public static string? PartnerKey(ShaderInfo ps)
    {
        var ins = ps.Inputs.Where(i => i.SysValue == 0).ToList();
        if (ins.Any(i => i.ReadMask == SigElement.UnknownReadMask)) return null;
        return string.Join(',', ins.Select(i => $"{i.Semantic.ToUpperInvariant()}{i.Index}:{i.ReadMask}/{i.Interpolation}").Order(StringComparer.Ordinal));
    }

    static TV Get<TV>(Dictionary<string, TV> d, string k) where TV : new() => d.TryGetValue(k, out var v) ? v : d[k] = new();

    static void AddDistinct(List<List<LayoutElem>> list, List<LayoutElem> layout)
    {
        if (!list.Any(l => l.SequenceEqual(layout))) list.Add(layout);
    }

    /// <summary>The layout elements the VS reads: those whose (semantic, index) is in its non-system input signature,
    /// semantic upper-cased, sorted by (semantic, index). Offsets must be explicit (<see cref="Explicit"/>).</summary>
    public static List<LayoutElem> ReadLayout(IReadOnlyList<LayoutElem> layout, ShaderInfo vs)
    {
        var inputs = vs.Inputs.Where(i => i.SysValue == 0).Select(i => (i.Semantic.ToUpperInvariant(), i.Index)).ToHashSet();
        return layout.Select(e => e with { Semantic = e.Semantic.ToUpperInvariant() }).Where(e => inputs.Contains((e.Semantic, e.Index)))
            .OrderBy(e => e.Semantic, StringComparer.Ordinal).ThenBy(e => e.Index).ToList();
    }

    public const uint AppendAligned = uint.MaxValue;

    /// <summary>The layout with D3D12_APPEND_ALIGNED_ELEMENT offsets resolved (the explicit offset is FREE on AMD, and
    /// dropping unread elements would otherwise move the ones after them). Unknown formats keep APPEND_ALIGNED.</summary>
    public static List<LayoutElem> Explicit(IReadOnlyList<LayoutElem> layout)
    {
        var end = new Dictionary<uint, uint>();
        var list = new List<LayoutElem>();
        foreach (var e in layout)
        {
            var size = FormatSize(e.Format);
            var off = e.Offset;
            if (off == AppendAligned && size > 0) { var a = Math.Min(4u, size); off = (end.GetValueOrDefault(e.Slot) + a - 1) / a * a; }
            if (off != AppendAligned) end[e.Slot] = off + size;
            list.Add(e with { Offset = off });
        }
        return list;
    }

    /// <summary>A VS's input interface: its non-system inputs (semantic, index, register, mask, component type). The used
    /// mask is not in it: a declared input the VS never reads is FULL too (probes-2), the layout must still carry it.</summary>
    public static string SigKey(ShaderInfo vs) => string.Join(';', vs.Inputs.Where(e => e.SysValue == 0)
        .Select(e => $"{e.Semantic.ToUpperInvariant()}/{e.Index}/{e.Register}/{e.Mask}/{e.CompType}").Order(StringComparer.Ordinal));

    public static string OutSig(ShaderInfo ps) => Planner.Sig(ps.Outputs, false);

    /// <summary>RT formats AMD compiles a PS the same way for (probe 6: CHEAP): 4-component float-typed formats of the same
    /// shape. Every other format is its own class until measured.</summary>
    public static string ExportClass(uint fmt) => fmt is 10 or 24 or 26 or 28 or 29 or 87 or 91 ? "fp16" : fmt.ToString();

    /// <summary>Per RT slot: format class, write mask 0, logic op, and on slot 0 dual-source blending ("/ds"). RT count, other
    /// blend state and DSV are CHEAP and not in it. Dual-source (a SRC1 factor, the PS's second output blended into RT 0) is:
    /// on AMD (FF7's own PSOs, fresh exe names) a pipeline whose PS was compiled only without it cost 16-126 ms, 22 of 28.</summary>
    public static string ExportShape(PsoState s) => string.Join(',', s.RtFormats.Select((f, i) =>
        ExportClass(f) + (s.RtWriteMasks[i] == 0 ? "/m0" : "") + (s.LogicOps[i] >= 0 ? $"/op{s.LogicOps[i]}" : "") + (i == 0 && s.DualSource ? "/ds" : "")));

    /// <summary>The recorded blend / depth-stencil / DSV to link a PS unit's PSO with: the PS's own (Exact), else the most
    /// used one of its output signature with that shape, else of the shape; null when nothing with that shape was recorded
    /// (a guessed shape: the planner's neutral state). A dual-source shape always gets a recorded dual-source blend.</summary>
    public LinkState? Link(ShaderInfo ps, string shape)
    {
        if (linkByPs.TryGetValue($"{ps.Sha1}|{shape}", out var own)) return own;
        foreach (var k in new[] { $"{OutSig(ps)}|{shape}", shape })
            if (linkCounts.TryGetValue(k, out var c)) return c.Values.OrderByDescending(v => v.N).ThenBy(v => v.State.Key, StringComparer.Ordinal).First().State;
        return null;
    }

    /// <summary>The root signature as a PS / CS / MS unit keys it: SHA-1 of its RTS0 with <see cref="UnitPolicy.FreeRsFlags"/>
    /// cleared (conservative: AMD's PS also shares a pure descriptor-count increase in a PIXEL range); the hash itself when
    /// the recording doesn't carry it.</summary>
    public string RsKey(string rs) => rsKeys.GetValueOrDefault(rs, rs);

    /// <summary>Makes <see cref="RsKey"/> know a root signature the recording doesn't carry (a planner's generated ones),
    /// so PSOs whose root signatures differ only in free flags share their units.</summary>
    public void AddRootSignature(string sha1, byte[] blob)
    {
        if (!rsKeys.ContainsKey(sha1)) rsKeys[sha1] = RsKeyOf(blob, Policy.FreeRsFlags);
    }

    static string RsKeyOf(byte[] blob, uint free)
    {
        var b = RootSig.Rts0(blob).ToArray();
        // header: version (1 = 1.0, 2 = 1.1), params, params offset, static samplers, samplers offset, flags
        BitConverter.TryWriteBytes(b.AsSpan(20, 4), BitConverter.ToUInt32(b, 20) & ~free);
        return Hex(SHA1.HashData(b));
    }

    /// <summary>The read input layouts to compile a VS with. L0 recorded for it (Exact); no non-system inputs: empty (Exact);
    /// L1 recorded for a VS with the same <see cref="SigKey"/> (Inferred); L2 the smallest recorded layout(s) covering every
    /// input with the same component type, projected (Inferred, up to 2 ties); L3 per input the most common recorded element,
    /// else the synthetic <see cref="Planner.VsLayout"/> (Guessed).</summary>
    public Resolved<List<List<LayoutElem>>> Layouts(ShaderInfo vs)
    {
        var r = LayoutsCore(vs);
        if (!Policy.ReadLayout || Policy.UvClamp == UvClampLayouts.None || r.Provenance != Provenance.Exact) return r;
        var all = new List<List<LayoutElem>>();
        foreach (var l in r.Value) AddDistinct(all, l);
        foreach (var l in r.Value) foreach (var v in UvClampVariants(l)) AddDistinct(all, v);
        return new(all, r.Provenance);
    }

    /// <summary>The same VS fed by meshes with fewer UV channels: Unreal's local vertex factory points the texture
    /// coordinates a mesh lacks at its last one (FF7 session 2: a VS recorded with ATTRIBUTE5/6/7 at 0/4/4 in slot 2 was
    /// drawn with 0/4/8, another the other way round; the offset of a declared element is in AMD's VS key). For each run
    /// of 2+ elements with consecutive semantic indexes, one slot and one format whose offsets each repeat or follow the
    /// previous one, the layouts for 1..run-length distinct channels (the others unchanged).</summary>
    public static IEnumerable<List<LayoutElem>> UvClampVariants(List<LayoutElem> layout)
    {
        for (var start = 0; start < layout.Count;)
        {
            var end = start + 1;
            var size = FormatSize(layout[start].Format);
            while (end < layout.Count && size > 0 && layout[end] is var e && layout[end - 1] is var p && e.Semantic == p.Semantic
                   && e.Index == p.Index + 1 && e.Slot == p.Slot && e.Format == p.Format && e.Class == p.Class
                   && (e.Offset == p.Offset || e.Offset == p.Offset + size))
                end++;
            if (end - start >= 2)
                for (var n = 1; n <= end - start; n++)
                {
                    var v = layout.ToList();
                    for (var j = start; j < end; j++) v[j] = v[j] with { Offset = layout[start].Offset + size * (uint)Math.Min(j - start, n - 1) };
                    yield return v;
                }
            start = end;
        }
    }

    Resolved<List<List<LayoutElem>>> LayoutsCore(ShaderInfo vs)
    {
        if (ReadLayouts.TryGetValue(vs.Sha1, out var l0)) return new(l0, Provenance.Exact);
        var inputs = vs.Inputs.Where(i => i.SysValue == 0).ToList();
        if (inputs.Count == 0) return new([[]], Provenance.Exact);
        if (LayoutsBySig.TryGetValue(SigKey(vs), out var l1)) return new(l1, Provenance.Inferred);

        var want = inputs.Select(i => (S: i.Semantic.ToUpperInvariant(), i.Index, i.CompType)).ToList();
        var covers = FullLayouts.Where(l => want.All(w => l.Any(e => e.Semantic.Equals(w.S, StringComparison.OrdinalIgnoreCase) && e.Index == w.Index
            && e.Offset != AppendAligned && CompClass(e.Format) == w.CompType))).ToList();
        if (covers.Count > 0)
        {
            var min = covers.Min(l => l.Count);
            var picks = new List<List<LayoutElem>>();
            foreach (var l in covers.Where(l => l.Count == min))
            {
                AddDistinct(picks, ReadLayout(l, vs));
                if (picks.Count == 2) break;
            }
            return new(picks, Provenance.Inferred);
        }

        var guess = want.Select(w => elemCounts.TryGetValue((w.S, w.Index, w.CompType), out var c)
            ? c.MaxBy(x => x.Value).Key : (LayoutElem?)null).ToList(); // ties: the first recorded
        var valid = guess.All(g => g != null) ? OneClassPerSlot(ReadLayout(guess.Select(g => g!.Value).ToList(), vs)) : null;
        return new([valid ?? ReadLayout(Planner.VsLayout(vs), vs)], Provenance.Guessed);
    }

    /// <summary>D3D12_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT: input slots are 0..31.</summary>
    public const uint InputSlots = 32;

    /// <summary>How often <see cref="OneClassPerSlot"/> fixed a guessed layout, by path: "recorded layout" (the moved elements as
    /// one class-consistent recorded layout has them), "recorded slot" (each moved element as recorded most often with its
    /// class), "fresh slot", "skipped" (no valid slot left: the synthetic layout instead).</summary>
    public SortedDictionary<string, int> ClassFixes { get; } = [];

    /// <summary>What a per-instance slot is keyed by besides its slot: one classification and one step rate (the runtime
    /// rejects a mix of either in one slot, and a slot past 31: `selftest layoutrules`, WARP).</summary>
    static (uint Class, uint Step) SlotClass(LayoutElem e) => (e.Class, e.Class == 1 ? e.Step : 0);

    /// <summary>Every slot in 0..31 and one <see cref="SlotClass"/> per slot: what the runtime accepts.</summary>
    public static bool ValidSlots(IReadOnlyList<LayoutElem> layout) =>
        layout.All(e => e.Slot < InputSlots) && layout.GroupBy(e => e.Slot).All(g => g.Select(SlotClass).Distinct().Count() == 1);

    /// <summary>A guessed layout made valid: each slot holds one classification and step rate (the runtime rejects a mix:
    /// E_INVALIDARG). A layout guessed element by element can mix them (Star Wars Jedi: Survivor: 1343 plan PSOs,
    /// ATTRIBUTE9/10 per-instance and ATTRIBUTE11/12 per-vertex, all recorded in slot 5 by different layouts). Each slot keeps
    /// its most common class (ties: per-vertex, then the lower step rate); the other elements move, in this order of
    /// preference (the slot is in AMD's VS key, so a slot the game never uses is a sure miss): to where one recorded layout
    /// has all of them with their class; else each to the slot it was recorded in most often with its class; else to a
    /// fresh slot. Null when no valid slot is left (slots 0..31). Recorded layouts never mix.</summary>
    public List<LayoutElem>? OneClassPerSlot(List<LayoutElem> layout)
    {
        if (ValidSlots(layout)) return layout;
        var keep = layout.GroupBy(e => e.Slot).ToDictionary(g => g.Key, g => g.GroupBy(SlotClass).OrderByDescending(c => c.Count())
            .ThenBy(c => c.Key.Class).ThenBy(c => c.Key.Step).First().Key);
        var move = Enumerable.Range(0, layout.Count).Where(i => SlotClass(layout[i]) != keep[layout[i].Slot]).ToList();
        (string, int, int) K(LayoutElem e) => (e.Semantic.ToUpperInvariant(), e.Index, CompClass(e.Format));
        List<LayoutElem> With(Func<int, LayoutElem> moved) => layout.Select((e, i) => move.Contains(i) ? moved(i) : e).ToList();
        List<LayoutElem>? Done(string path, List<LayoutElem> l)
        {
            if (!ValidSlots(l)) return null;
            ClassFixes[path] = ClassFixes.GetValueOrDefault(path) + 1;
            return l;
        }

        // one recorded layout with every moved element in its class
        foreach (var full in FullLayouts)
        {
            var found = move.Select(i => full.FirstOrDefault(r => K(r) == K(layout[i]) && SlotClass(r) == SlotClass(layout[i]) && r.Offset != AppendAligned)).ToList();
            if (found.All(r => r.Semantic != null)
                && Done("recorded layout", With(i => found[move.IndexOf(i)] with { Semantic = layout[i].Semantic })) is { } l) return l;
        }
        // each moved element where it was recorded most often with its class, if that slot takes it
        var result = layout.ToList();
        var byRecord = true;
        foreach (var i in move)
        {
            var e = layout[i];
            var cands = elemCounts.TryGetValue(K(e), out var c) ? c.Where(x => SlotClass(x.Key) == SlotClass(e) && x.Key.Slot < InputSlots)
                .OrderByDescending(x => x.Value).Select(x => x.Key) : [];
            var pick = cands.FirstOrDefault(r => result.Where((o, j) => j != i && o.Slot == r.Slot).All(o => SlotClass(o) == SlotClass(e)));
            if (pick.Semantic == null) { byRecord = false; break; }
            result[i] = pick with { Semantic = e.Semantic };
        }
        if (byRecord && Done("recorded slot", result) is { } r2) return r2;
        // a fresh slot per (slot, class) moved out of it
        var free = layout.Max(e => e.Slot) + 1;
        var fresh = new Dictionary<(uint, (uint, uint)), uint>();
        var l3 = With(i => layout[i] with { Slot = fresh.TryGetValue((layout[i].Slot, SlotClass(layout[i])), out var f) ? f : fresh[(layout[i].Slot, SlotClass(layout[i]))] = free++ });
        if (Done("fresh slot", l3) is { } r3) return r3;
        ClassFixes["skipped"] = ClassFixes.GetValueOrDefault("skipped") + 1;
        return null;
    }

    /// <summary>The export shapes to compile a PS with: recorded for it (Exact); else the most used shapes of its output
    /// signature until they cover at least 90% of that signature's recorded PSOs (Inferred); else one from its outputs'
    /// types (<see cref="Planner.Targets"/>, Guessed).</summary>
    public Resolved<List<string>> Shapes(ShaderInfo ps)
    {
        if (ShapesByPs.TryGetValue(ps.Sha1, out var s0)) return new(s0, Provenance.Exact);
        if (ShapesByOutSig.TryGetValue(OutSig(ps), out var bySig))
        {
            var total = bySig.Values.Sum();
            var picks = new List<string>();
            var n = 0;
            foreach (var (shape, c) in bySig.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
            {
                picks.Add(shape);
                if ((n += c) >= 0.9 * total) break;
            }
            return new(picks, Provenance.Inferred);
        }
        var (rt, _) = Planner.Targets(ps);
        var guessed = new PsoState("", [], [], 3, rt, rt.Select(_ => (byte)0xF).ToArray(), rt.Select(_ => -1).ToArray(), 0, 1);
        var key = ExportShape(guessed);
        ShapeExample.TryAdd(key, (guessed.RtFormats, guessed.RtWriteMasks, guessed.LogicOps));
        return new([key], Provenance.Guessed);
    }

    /// <summary>Topology types a pipeline's VS is drawn with: patch (4) with a hull shader, a GS's input type, else what the
    /// VS was recorded with, else triangle (3).</summary>
    public Resolved<List<uint>> Topologies(IReadOnlyDictionary<int, string> stages)
    {
        if (stages.ContainsKey((int)Stage.Hull)) return new([4], Provenance.Exact);
        if (stages.TryGetValue((int)Stage.Geometry, out var gs) && Shaders.TryGetValue(gs, out var g) && Planner.TopologyType(g.GsInputPrimitive) is > 0 and var t)
            return new([t], Provenance.Exact);
        if (stages.TryGetValue((int)Stage.Vertex, out var vs) && TopoOf.TryGetValue(vs, out var topos)) return new([.. topos], Provenance.Exact);
        return new([3], Provenance.Guessed);
    }

    /// <summary>Signature component type of a DXGI format (1 uint, 2 sint, 3 float incl. UNORM/SNORM), 0 = unknown.</summary>
    public static int CompClass(uint fmt) => fmt switch
    {
        3 or 7 or 12 or 17 or 25 or 30 or 36 or 42 or 50 or 57 or 62 => 1,
        4 or 8 or 14 or 18 or 32 or 38 or 43 or 52 or 59 or 64 => 2,
        2 or 6 or 10 or 11 or 13 or 16 or 24 or 26 or 28 or 29 or 31 or 34 or 35 or 37 or 41 or 49 or 51 or 54 or 56 or 58 or 61 or 63 or 87 or 88 or 91 => 3,
        _ => 0,
    };

    /// <summary>Bytes per element of a vertex format, 0 = unknown.</summary>
    public static uint FormatSize(uint fmt) => fmt switch
    {
        >= 2 and <= 4 => 16,
        >= 6 and <= 8 => 12,
        >= 10 and <= 18 => 8,
        >= 24 and <= 43 or 87 or 88 or 91 => 4,
        >= 49 and <= 59 => 2,
        >= 61 and <= 64 => 1,
        _ => 0,
    };
}
