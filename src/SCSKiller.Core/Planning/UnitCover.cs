using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>One compile a per-stage driver cache stores: a shader and the state its stage is keyed on under a
/// <see cref="UnitPolicy"/> (see <see cref="UnitCover"/> for the keys).</summary>
public readonly record struct Unit(Stage Stage, string Shader, string Key);

/// <summary>One PSO to create for a stage set: which of the resolved candidates it uses.</summary>
public sealed record UnitPick(List<LayoutElem> Layout, uint Topology, string Shape);

/// <summary>A set of covered <see cref="Unit"/>s and a greedy cover over it. With a per-stage cache a plan needs every
/// unit once, not every VS x PS pair: for a stage set whose VS has L candidate layouts and whose PS has S candidate
/// shapes, max(L, S) PSOs cover them all (zipped), not L x S. Units:
///   VS = (VS, read layout, topology, next stage: PS / none / GS / HS, root signature as the VS sees it)
///   PS = (PS, root signature, export shape); CS, MS, AS, GS, HS, DS = (shader, root signature)
/// with each state part only when the policy keys on it (NVIDIA: shader + whole root signature).</summary>
public sealed class UnitCover(ExactLayouts facts)
{
    public ExactLayouts Facts { get; } = facts;
    public UnitPolicy Policy => Facts.Policy;
    public HashSet<Unit> Covered { get; } = [];

    /// <summary>A cover that already has every unit the recording itself creates (the game's own PSOs replay as they are).
    /// <paramref name="replays"/>: whether a shader's bytes reach the warm; a PSO with one that doesn't is skipped there, so
    /// its units stay uncovered.</summary>
    public static UnitCover Seeded(ExactLayouts facts, Func<string, bool>? replays = null)
    {
        var c = new UnitCover(facts);
        c.Covered.UnionWith(RecordedUnits(facts, replays));
        return c;
    }

    /// <summary>Every unit of the recording's PSOs (of those whose shaders all <paramref name="replays"/>).</summary>
    public static IEnumerable<Unit> RecordedUnits(ExactLayouts x, Func<string, bool>? replays = null) =>
        x.Graphics.Where(s => replays == null || s.Stages.Values.All(replays)).SelectMany(s => UnitsOf(x, s))
            .Concat(x.Compute.Where(c => replays == null || c.Stages.Values.All(replays)).Select(c => new Unit(Stage.Compute, c.Stages[(int)Stage.Compute], x.RsKey(c.Rs))));

    /// <summary>The units a recorded graphics PSO compiles.</summary>
    public static IEnumerable<Unit> UnitsOf(ExactLayouts x, PsoState s)
    {
        var read = s.Stages.TryGetValue((int)Stage.Vertex, out var vs) && x.Shaders.TryGetValue(vs, out var vi)
            ? ExactLayouts.ReadLayout(ExactLayouts.Explicit(s.Layout), vi) : ExactLayouts.Explicit(s.Layout); // no signature: the whole layout
        return Units(x, s.Stages, s.Rs, read, s.Topology, x.ShapeOf(s));
    }

    /// <summary>The units of one PSO: <paramref name="readLayout"/> as <see cref="ExactLayouts.ReadLayout"/> returns it.</summary>
    public static IEnumerable<Unit> Units(ExactLayouts x, IReadOnlyDictionary<int, string> stages, string rs, IReadOnlyList<LayoutElem> readLayout, uint topology, string shape)
    {
        foreach (var (st, sh) in stages)
        {
            var stage = (Stage)st;
            yield return stage == Stage.Vertex ? VsUnit(x, stages, sh, rs, readLayout, topology, shape)
                : stage == Stage.Pixel ? PsUnit(x, sh, rs, shape)
                : new Unit(stage, sh, x.RsKey(rs));
        }
    }

    static Unit VsUnit(ExactLayouts x, IReadOnlyDictionary<int, string> stages, string vs, string rs, IReadOnlyList<LayoutElem> readLayout, uint topology, string shape)
    {
        var p = x.Policy;
        var k = x.RsKey(rs);
        if (p.ReadLayout) k += "|" + LayoutKey(readLayout);
        if (p.Topology) k += "|" + topology;
        if (p.NextStage) k += "|" + Next(stages, shape);
        if (p.PartnerReads && stages.TryGetValue((int)Stage.Pixel, out var ps) && x.PartnerKeys.TryGetValue(ps, out var consumes)) k += "|r:" + consumes;
        return new Unit(Stage.Vertex, vs, k);
    }

    static Unit PsUnit(ExactLayouts x, string ps, string rs, string shape) => new(Stage.Pixel, ps, x.Policy.ExportShape ? $"{x.RsKey(rs)}|{shape}" : x.RsKey(rs));

    /// <summary>A layout's part of a VS key, memoized per layout instance (a planner passes the same resolved lists for
    /// every stage set of a VS).</summary>
    static string LayoutKey(IReadOnlyList<LayoutElem> layout) => LayoutKeys.GetValue(layout, l =>
        string.Join(';', l.Select(e => $"{e.Semantic}/{e.Index}/{e.Format}/{e.Slot}/{e.Offset}/{e.Class}/{e.Step}")));

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<LayoutElem>, string> LayoutKeys = new();

    /// <summary>What a VS feeds, as the VS key needs it: the GS or HS, else whether there is a PS (a depth pass has none),
    /// and whether that PS's color output reaches a render target: AMD compiles the VS for what its PS consumes, and a PS
    /// with no render target bound consumes less (selftest probe 6 "deadps": a VS compiled only behind a PS with 0 RT
    /// recompiles in full behind the same PS with 1 RT; FF7 session 2: 7 of the 15 PSOs at 30-50 ms after their units'
    /// cover had their VS unit from a 0-RT pick).</summary>
    static string Next(IReadOnlyDictionary<int, string> stages, string shape) =>
        stages.TryGetValue((int)Stage.Geometry, out var gs) ? "gs:" + gs : stages.TryGetValue((int)Stage.Hull, out var hs) ? "hs:" + hs
        : !stages.ContainsKey((int)Stage.Pixel) ? "none" : Bound(shape) ? "ps" : "ps:no-rt";

    /// <summary>An export shape with a render target (one entry per RT; "" = none bound).</summary>
    static bool Bound(string shape) => shape != "";

    /// <summary>Greedy cover of one stage set: candidate (read) layouts x topologies for its VS and candidate shapes for its
    /// PS are zipped so the uncovered ones come first, max(#uncovered VS variants, #uncovered shapes) PSOs, each emitted only
    /// if it adds a unit (then marked covered). Empty candidate lists mean the stage set has no VS / no PS.</summary>
    /// <param name="pairsOnly">emit only PSOs that cover a new VS unit and a new PS unit at once (<see cref="CoverAll"/>'s
    /// first pass)</param>
    public List<UnitPick> Cover(IReadOnlyDictionary<int, string> stages, string rs, IReadOnlyList<List<LayoutElem>> layouts,
        IReadOnlyList<uint> topologies, IReadOnlyList<string> shapes, bool pairsOnly = false)
    {
        if (pairsOnly && !(stages.ContainsKey((int)Stage.Vertex) && stages.ContainsKey((int)Stage.Pixel))) return [];
        List<(List<LayoutElem>, uint)> vsVariants = layouts.Count == 0 || !stages.ContainsKey((int)Stage.Vertex) ? [([], 0u)]
            : [.. layouts.SelectMany(l => (topologies.Count == 0 ? [3u] : topologies).Select(t => (l, t)))];
        List<string> psShapes = shapes.Count == 0 || !stages.ContainsKey((int)Stage.Pixel) ? [""] : [.. shapes];
        // each stage's unit once per candidate (a PS unit doesn't depend on the VS variant; a VS unit only on whether the
        // shape binds a render target, so VS variants come per such binding among the shapes)
        var vsSha = stages.GetValueOrDefault((int)Stage.Vertex);
        var psSha = stages.GetValueOrDefault((int)Stage.Pixel);
        var keyed = Policy.NextStage && psSha != null && vsSha != null; // the VS unit depends on the binding (else one VS unit for all shapes)
        var bindings = keyed ? psShapes.Select(Bound).Distinct().ToList() : [psShapes.Any(Bound)];
        var vsUnits = vsVariants.SelectMany(v => bindings.Select(b => (V: v, Bound: b,
            U: vsSha == null ? (Unit?)null : VsUnit(Facts, stages, vsSha, rs, v.Item1, v.Item2, b ? psShapes.First(Bound) : "")))).DistinctBy(v => v.U).ToList();
        var psUnits = psShapes.Select(s => (S: s, U: psSha == null ? (Unit?)null : PsUnit(Facts, psSha, rs, s))).DistinctBy(s => s.U).ToList();
        var others = stages.Where(s => s.Key is not ((int)Stage.Vertex or (int)Stage.Pixel)).Select(s => new Unit((Stage)s.Key, s.Value, Facts.RsKey(rs))).ToList();
        bool IsNew(Unit? u) => u is { } x && !Covered.Contains(x);
        var newVs = vsUnits.Count(v => IsNew(v.U));
        var newPs = psUnits.Count(s => IsNew(s.U));
        if (newVs == 0 && newPs == 0 && !others.Any(u => IsNew(u))) return []; // the common case on a big library: nothing to emit

        // one variant per unit (NVIDIA: every layout is the same VS unit), zipped per render-target binding (a VS unit is
        // keyed on it): the uncovered ones first, the covered ones fill the zip
        var vs = vsUnits.OrderBy(v => !IsNew(v.U)).ToList();
        var ps = psUnits.OrderBy(s => !IsNew(s.U)).ToList();
        var zip = new List<((List<LayoutElem>, uint) V, string S, Unit? Vu, Unit? Su)>();
        foreach (var b in bindings)
        {
            var bv = vs.Where(v => v.Bound == b).ToList() is { Count: > 0 } l ? l : vs; // no VS (mesh): one null unit for all
            var bp = keyed ? ps.Where(s => Bound(s.S) == b).ToList() : ps;
            int nv = bv.Count(v => IsNew(v.U)), np = bp.Count(s => IsNew(s.U));
            var n = pairsOnly ? Math.Min(nv, np) : Math.Max(nv, np);
            for (var i = 0; i < n; i++)
            {
                var v = bv[i < bv.Count ? i : 0];
                var s = bp[i < bp.Count ? i : 0];
                zip.Add((v.V, s.S, v.U, s.U));
            }
        }
        if (zip.Count == 0 && !pairsOnly) // only other stages are new
        {
            var s0 = ps.First(s => !keyed || Bound(s.S) == vs[0].Bound);
            zip.Add((vs[0].V, s0.S, vs[0].U, s0.U));
        }
        var picks = new List<UnitPick>();
        foreach (var (v, s, vu, su) in zip)
        {
            var units = others.ToList();
            if (vu is { } a) units.Add(a);
            if (su is { } c) units.Add(c);
            if (units.All(Covered.Contains)) continue;
            Covered.UnionWith(units);
            picks.Add(new UnitPick(v.Item1, v.Item2, s));
        }
        return picks;
    }

    /// <summary>One candidate stage set with its resolved state candidates (see <see cref="Cover"/>).</summary>
    public sealed record Candidate(IReadOnlyDictionary<int, string> Stages, string Rs, IReadOnlyList<List<LayoutElem>> Layouts,
        IReadOnlyList<uint> Topologies, IReadOnlyList<string> Shapes);

    /// <summary>Covers many stage sets in two passes: first only PSOs that bring a new VS and a new PS unit together (a greedy
    /// matching: pairing two uncovered units in one PSO is what gets a cover near max(#VS units, #PS units)), then
    /// <see cref="Cover"/> for whatever is left. Returns the picks per candidate, in candidate order.</summary>
    public List<(Candidate Candidate, UnitPick Pick)> CoverAll(IReadOnlyList<Candidate> candidates)
    {
        var picks = new List<(int, Candidate, UnitPick)>();
        foreach (var pairsOnly in new[] { true, false })
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                picks.AddRange(Cover(c.Stages, c.Rs, c.Layouts, c.Topologies, c.Shapes, pairsOnly).Select(p => (i, c, p)));
            }
        return picks.OrderBy(p => p.Item1).Select(p => (p.Item2, p.Item3)).ToList();
    }
}
