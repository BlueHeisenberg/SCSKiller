using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.CryEngine;

/// <summary>Candidate input layouts for CRYENGINE's built-in vertex formats, which no bin cache file holds (AMD keys a VS on
/// the layout elements it reads). The engine builds a pipeline's layout from its vertex format's base stream (slot 0),
/// optionally made per-instance, then appends one attachment stream per extra stream the VS takes: tangents (slot 1),
/// QTangents (2), skinning (3), velocity (4), normals (5), morph weights (15). Which base a format id is, how the engine
/// encodes tangents, and whether a TANGENT-only VS takes tangents or QTangents are the unknowns a learner tries.</summary>
public static class CryVertexLayouts
{
    const uint F32x4 = 2, F32x3 = 6, F16x4 = 10, Sn16x4 = 13, Si16x4 = 14, F32x2 = 16, Un8x4 = 28, F16x2 = 34, F32 = 41;

    static LayoutElem E(string semantic, uint format, uint offset, int index = 0, uint slot = 0) => new(semantic, index, format, offset, slot);

    /// <summary>The engine's base-stream formats, most used first in a mesh renderer: elements with their packed offsets.</summary>
    public static readonly (string Name, LayoutElem[] Elems)[] Bases =
    [
        ("P3S_C4B_T2S", [E("POSITION", F16x4, 0), E("COLOR", Un8x4, 8), E("TEXCOORD", F16x2, 12)]),
        ("P3F_C4B_T2F", [E("POSITION", F32x3, 0), E("COLOR", Un8x4, 12), E("TEXCOORD", F32x2, 16)]),
        ("P3S_N4B_C4B_T2S", [E("POSITION", F16x4, 0), E("NORMAL", Un8x4, 8), E("COLOR", Un8x4, 12), E("TEXCOORD", F16x2, 16)]),
        ("P3F_C4B_T2S", [E("POSITION", F32x3, 0), E("COLOR", Un8x4, 12), E("TEXCOORD", F16x2, 16)]),
        ("P2S_N4B_C4B_T1F", [E("POSITION", F16x2, 0), E("NORMAL", Un8x4, 4), E("COLOR", Un8x4, 8), E("TEXCOORD", F32, 12)]),
        ("P3F_C4B_T4B_N3F2", [E("POSITION", F32x3, 0), E("COLOR", Un8x4, 12), E("TEXCOORD", Un8x4, 16), E("AXIS", F32x3, 20), E("AXIS", F32x3, 32, 1)]),
        ("TP3F_C4B_T2F", [E("POSITION", F32x4, 0), E("COLOR", Un8x4, 16), E("TEXCOORD", F32x2, 20)]),
        ("P3F_T3F", [E("POSITION", F32x3, 0), E("TEXCOORD", F32x3, 12)]),
        ("P3F_T2F_T3F", [E("POSITION", F32x3, 0), E("TEXCOORD", F32x2, 12), E("TEXCOORD", F32x3, 20, 1)]),
        // COLOR and TEXCOORD alias the position for depth-only shaders that declare them
        ("P3F", [E("POSITION", F32x3, 0), E("COLOR", F32x3, 0), E("TEXCOORD", F32x3, 0)]),
        ("Empty", []),
    ];

    /// <summary>Tangent streams as 16-bit SNORM (the engine's default) or as 32-bit floats.</summary>
    public enum Tangents { Short, Float }

    static LayoutElem[] TangentStream(Tangents t) => t == Tangents.Short
        ? [E("TANGENT", Sn16x4, 0, slot: 1), E("BITANGENT", Sn16x4, 8, slot: 1)]
        : [E("TANGENT", F32x4, 0, slot: 1), E("BITANGENT", F32x4, 16, slot: 1)];

    static LayoutElem[] QTangentStream(Tangents t) => [E("TANGENT", t == Tangents.Short ? Sn16x4 : F32x4, 0, slot: 2)];

    static readonly LayoutElem[][] OtherStreams =
    [
        [E("BLENDWEIGHT", Un8x4, 0, slot: 3), E("BLENDINDICES", Si16x4, 4, slot: 3)],
        [E("POSITION", F32x3, 0, 3, 4)],
        [E("NORMAL", F32x3, 0, slot: 5)],
        [E("BLENDWEIGHT", F32x2, 0, 1, 15)],
    ];

    static (string, int) Key(LayoutElem e) => (e.Semantic.ToUpperInvariant(), e.Index);

    static HashSet<(string, int)> Inputs(ShaderInfo vs) => vs.Inputs.Where(i => i.SysValue == 0).Select(i => (i.Semantic.ToUpperInvariant(), i.Index)).ToHashSet();

    /// <summary>The full layouts a VS can be drawn with on <paramref name="baseStream"/>: the base plus each attachment stream
    /// holding one of its inputs the base lacks. A TANGENT the VS takes without BITANGENT gives two: tangents or QTangents.
    /// Empty when an input is in no stream (the runtime rejects a layout missing a declared input).</summary>
    public static List<LayoutElem[]> For(LayoutElem[] baseStream, ShaderInfo vs, Tangents tangents = Tangents.Short, bool instanced = false)
    {
        var need = Inputs(vs);
        need.ExceptWith(baseStream.Select(Key));
        var b = instanced ? Instanced(baseStream) : baseStream;
        var extra = new List<LayoutElem>();
        foreach (var s in OtherStreams.Where(s => s.Any(e => need.Contains(Key(e)))))
        {
            extra.AddRange(s);
            need.ExceptWith(s.Select(Key));
        }
        LayoutElem[][] tangentSets = !need.Contains(("TANGENT", 0)) ? [[]]
            : need.Contains(("BITANGENT", 0)) ? [TangentStream(tangents)]
            : [TangentStream(tangents), QTangentStream(tangents)];
        if (need.Contains(("TANGENT", 0))) need.ExceptWith(TangentStream(tangents).Select(Key));
        if (need.Count > 0) return [];
        return [.. tangentSets.Select(t => (LayoutElem[])[.. b, .. extra, .. t])];
    }

    /// <summary>A format's candidates, by how many of its VSs each base fits (most first, then <see cref="Bases"/> order), all of
    /// them: no record names its VS, so a VS placed with a format may be drawn with another (Hunt: 2 of 85 VSs of one format
    /// declare a TEXCOORD1 the others' base lacks), and a VS without inputs fits every base. Per base and VS it fits, the
    /// distinct read layouts (<see cref="ExactLayouts.ReadLayout"/>).</summary>
    public sealed record Fit(int[] Fits, int[] Candidates, Dictionary<(int Base, string Vs), List<List<LayoutElem>>> Reads)
    {
        /// <summary>The VS reads the same under every base it fits: its key inputs are known without a try.</summary>
        public bool Fixed(string vs) => Candidates.Where(c => Reads.ContainsKey((c, vs)))
            .Select(c => string.Join('|', Reads[(c, vs)].Select(r => string.Join(',', r)))).Distinct().Count() <= 1;

        public bool Pinned(IReadOnlyCollection<string> vss) => vss.Count > 0 && vss.All(Fixed);
    }

    public static Fit Of(IReadOnlyList<ShaderInfo> vss, Tangents tangents = Tangents.Short)
    {
        var fits = new int[Bases.Length];
        var reads = new Dictionary<(int, string), List<List<LayoutElem>>>();
        for (var b = 0; b < Bases.Length; b++)
            foreach (var vs in vss)
                if (For(Bases[b].Elems, vs, tangents) is { Count: > 0 } full)
                {
                    fits[b]++;
                    reads[(b, vs.Sha1)] = [.. full.Select(f => ExactLayouts.ReadLayout(f, vs)).DistinctBy(r => string.Join(',', r))];
                }
        return new(fits, [.. Enumerable.Range(0, Bases.Length).OrderByDescending(b => fits[b])], reads);
    }

    /// <summary>A built-in format's layout on <paramref name="baseStream"/>: the base, then every attachment stream element it
    /// lacks, tangents last (<paramref name="qTangents"/>: the QTangent stream's TANGENT, the tangent stream's BITANGENT). A VS
    /// reads its part (<see cref="ExactLayouts.ReadLayout"/>) as <see cref="For"/> gives it.</summary>
    public static LayoutElem[] Full(LayoutElem[] baseStream, Tangents tangents, bool qTangents = false)
    {
        var have = baseStream.Select(Key).ToHashSet();
        LayoutElem[] tangent = qTangents ? [.. QTangentStream(tangents), .. TangentStream(tangents).Where(e => e.Semantic == "BITANGENT")] : TangentStream(tangents);
        return [.. baseStream, .. OtherStreams.SelectMany(s => s).Concat(tangent).Where(e => have.Add(Key(e)))];
    }

    /// <summary>Every element per-instance, step rate 1: how the engine instances a base stream.</summary>
    public static LayoutElem[] Instanced(LayoutElem[] layout) => [.. layout.Select(e => e with { Class = 1, Step = 1 })];

    /// <summary>A shipped format's candidates: its record, and where an element is per-instance the record with step rate 1
    /// too (field 0E02 reads 2 there and 1 on per-vertex elements: a step rate or a class, unproven).</summary>
    public static List<LayoutElem[]> Shipped(LayoutElem[] record) => record.Any(e => e.Class == 1)
        ? [record, [.. record.Select(e => e.Class == 1 ? e with { Step = 1 } : e)]] : [record];

    /// <summary>The layout a learner compiles a VS under for candidate <paramref name="c"/> of a format whose candidate base streams
    /// are <paramref name="cands"/>: base c / 2, tangents for an even c and QTangents for an odd one. Null where the VS doesn't
    /// fit that base, or c is odd and the VS takes no lone TANGENT. c = -1 is the control: the first fitting base with the first
    /// element the VS reads in a format of another size (a same-size format may fold to the same key), null when it reads none.</summary>
    public static LayoutElem[]? Layout(IReadOnlyList<LayoutElem[]> cands, ShaderInfo vs, int c, Tangents tangents = Tangents.Short)
    {
        if (c < 0)
        {
            var full = cands.Select(b => For(b, vs, tangents)).FirstOrDefault(f => f.Count > 0)?[0];
            if (full == null || ExactLayouts.ReadLayout(full, vs) is not [var first, ..]) return null;
            var other = (ExactLayouts.CompClass(first.Format), ExactLayouts.FormatSize(first.Format)) switch
            {
                (1, 16) => 30u, (1, _) => 3u, (2, 16) => 14u, (2, _) => 4u, (_, 16) => 10u, _ => 2u,
            };
            return [.. full.Select(e => Key(e) == Key(first) ? e with { Format = other } : e)];
        }
        if (c / 2 >= cands.Count || For(cands[c / 2], vs, tangents) is not { Count: > 0 } fulls || c % 2 >= fulls.Count) return null;
        return fulls[c % 2];
    }

    /// <summary>Which VSs a learner tries with which format. A graphics pool (effect, root signature) is drawn with the formats
    /// <paramref name="formatsOf"/> gives it. A VS goes with a format when that is the only one of its pool's it can be drawn
    /// with (a shipped format must hold every input it declares). Round 0, the positive control: every (format, VS) where the
    /// format is shipped and holds the VS's inputs. Open: per format, the VSs of its pools placed with no format, for a format
    /// no VS is placed with.</summary>
    public sealed record Placement(Dictionary<ulong, HashSet<string>> Vss, HashSet<(ulong Format, string Vs)> Round0, HashSet<string> Several, HashSet<string> Unfit,
        Dictionary<ulong, HashSet<string>> Open);

    public static Placement Place(IEnumerable<(IReadOnlyCollection<ulong> Formats, IEnumerable<ShaderInfo> Vss)> pools, IReadOnlyDictionary<ulong, LayoutElem[]> shipped)
    {
        var o = new Placement([], [], [], [], []);
        var seen = new List<(IReadOnlyCollection<ulong> Formats, string Vs)>();
        foreach (var (fmts, vss) in pools)
            foreach (var vs in vss)
            {
                var can = fmts.Where(f => !shipped.TryGetValue(f, out var el) || Covers(el, vs)).ToList();
                seen.Add((can, vs.Sha1));
                foreach (var f in can.Where(shipped.ContainsKey)) o.Round0.Add((f, vs.Sha1));
                if (can.Count == 1) (o.Vss.TryGetValue(can[0], out var set) ? set : o.Vss[can[0]] = []).Add(vs.Sha1);
                else (can.Count == 0 ? o.Unfit : o.Several).Add(vs.Sha1);
            }
        var placed = o.Vss.Values.SelectMany(x => x).ToHashSet();
        o.Several.ExceptWith(placed);
        foreach (var (fmts, vs) in seen.Where(x => !placed.Contains(x.Vs)))
            foreach (var f in fmts.Where(f => !o.Vss.ContainsKey(f) && !shipped.ContainsKey(f)))
                (o.Open.TryGetValue(f, out var set) ? set : o.Open[f] = []).Add(vs);
        return o;
    }

    static bool Covers(LayoutElem[] elems, ShaderInfo vs) => Inputs(vs).IsSubsetOf(elems.Select(Key));
}
