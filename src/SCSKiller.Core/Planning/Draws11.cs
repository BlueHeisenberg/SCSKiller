using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>D3D11 vertex-stage items ('3') for a vendor whose D3D11 VS compiles for its input layout (AMD, ARCHITECTURE.md
/// "D3D11"): every recorded layout with its VS (and a recorded GS, HS+DS and topology) once per AMD key, and for each VS of the game's files
/// no draw recorded, the recorded layouts that fit its inputs. A game uses few layouts and they generalize across its VSs
/// (FF7 Rebirth's and The Witcher 3's recordings, leave-one-VS-out: 100% and 91% of VS units). GS and HS are planned only as
/// recorded: AMD compiles them with the VS before them.</summary>
public static class Draws11
{
    /// <summary>Recorded layouts tried per unrecorded VS, the most drawn first: each one is a whole VS compile.</summary>
    public const int MaxGuesses = 4;
    public const uint TriangleList = 4;

    public sealed record Result(List<Draw11> Items, int Exact, int Projected, int VssProjected);

    /// <param name="info">a shader's signatures by its hash; null = not known (not in the index nor the recording)</param>
    /// <param name="vss">the game's D3D11 vertex shaders</param>
    public static Result Plan(IEnumerable<Draw11> recorded, Func<string, ShaderInfo?> info, IEnumerable<string> vss)
    {
        var items = new List<Draw11>();
        var keys = new HashSet<string>();
        var counts = new Dictionary<string, (List<LayoutElem> Layout, int N)>();
        var drawn = new HashSet<string>();
        int exact = 0, projected = 0, vssProjected = 0;
        bool Add(Draw11 d, ShaderInfo vs)
        {
            if (!keys.Add(AmdKey(d, vs))) return false;
            items.Add(d);
            return true;
        }
        foreach (var d in recorded)
        {
            var full = ExactLayouts.Explicit(d.Layout);
            var known = full.All(e => e.Offset != ExactLayouts.AppendAligned);   // else as recorded: the runtime resolves it alike
            if (full.Count > 0 && known)   // the layout counts for the projection even when its VS isn't known (a signature-only blob)
            {
                var k = Key(full);
                counts[k] = (full, counts.TryGetValue(k, out var c) ? c.N + 1 : 1);
            }
            if (info(d.Vs) is not { Stage: Stage.Vertex } vs) continue;
            drawn.Add(d.Vs);
            exact += Add(known ? d with { Layout = ExactLayouts.ReadLayout(full, vs) } : d, vs) ? 1 : 0;
        }
        var ranked = counts.Values.OrderByDescending(c => c.N).ThenBy(c => Key(c.Layout), StringComparer.Ordinal).Select(c => c.Layout).ToList();
        foreach (var h in vss.Distinct().Where(h => !drawn.Contains(h)).Order(StringComparer.Ordinal))
        {
            if (info(h) is not { } vs) continue;
            var inputs = vs.Inputs.Where(i => i.SysValue == 0).ToList();
            if (inputs.Count == 0) continue;   // a '1' item already: no layout to guess
            var n = 0;
            foreach (var l in ranked.Where(l => Fits(l, inputs)))
                if (Add(new(h, Zero, Zero, Zero, TriangleList, true, ExactLayouts.ReadLayout(l, vs)), vs) && ++n == MaxGuesses) break;
            projected += n;
            vssProjected += n > 0 ? 1 : 0;
        }
        return new(items, exact, projected, vssProjected);
    }

    /// <summary>The layout has every input at an explicit offset in a format of the input's component type.</summary>
    static bool Fits(List<LayoutElem> layout, List<SigElement> inputs) => inputs.All(i => layout.Any(e =>
        e.Semantic.Equals(i.Semantic, StringComparison.OrdinalIgnoreCase) && e.Index == i.Index && ExactLayouts.CompClass(e.Format) == i.CompType));

    /// <summary>What AMD compiles a VS (and the GS or HS with it) for: the stages, the topology, a PS or none, and the layout
    /// elements the VS reads (an unread one is cheap: a declared input with read mask 0).</summary>
    public static string AmdKey(Draw11 d, ShaderInfo vs)
    {
        var unread = vs.Inputs.Where(i => i.SysValue == 0 && i.ReadMask == 0).Select(i => (i.Semantic.ToUpperInvariant(), i.Index)).ToHashSet();
        return $"{d.Vs}|{d.Gs}|{d.Hs}|{d.Ds}|{d.Topology}|{d.Ps}|" + Key(d.Layout.Where(e => !unread.Contains((e.Semantic.ToUpperInvariant(), e.Index))));
    }

    static string Key(IEnumerable<LayoutElem> layout) =>
        string.Join(';', layout.Select(e => $"{e.Semantic.ToUpperInvariant()}{e.Index}:{e.Format}@{e.Slot}+{e.Offset}/{e.Class}/{e.Step}").Order(StringComparer.Ordinal));
}
