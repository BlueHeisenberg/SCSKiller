using System.Buffers.Binary;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.CryEngine;

/// <summary>A graphics pipeline record of PipelineStateBinCache.txt: its layout id (DB6C, read little endian) and
/// <paramref name="Desc"/>, its top-level fields in record order except DB6C and 2F2F.</summary>
public sealed record CryPipeline(ulong Layout, byte[] Desc);

/// <summary>A shader as the fit check sees it: its DXIL program kind (0 ps, 1 vs, 2 gs, 3 hs, 4 ds, 5 cs) and its PSV0 bindings.</summary>
public sealed record CryShader(int Kind, (uint Type, uint Space, uint Lo, uint Hi)[] Bindings)
{
    /// <summary>The shader of a DXBC container, or null without a program or PSV0 bindings.</summary>
    public static CryShader? Of(ReadOnlySpan<byte> container) =>
        Dxbc.Kind(container) is var k and >= 0 && Dxbc.PsvBindings(container) is { } b ? new(k, b) : null;
}

public enum CryTwinState { Twin, Hedged, NoTwin, TooManyTwins }

/// <summary>How a layout id that pipelines use but the cache doesn't ship resolves. <paramref name="Twins"/>: the shipped
/// layouts of its twin pipelines, or with <paramref name="ByFit"/> the shipped layouts its shaders fit best;
/// <paramref name="Pipelines"/>: how many pipeline records use it.</summary>
public sealed record CryTwin(ulong Id, CryTwinState State, ulong[] Twins, int Pipelines, string? Why, bool ByFit = false);

/// <summary>Layouts the pipeline cache names but ResourceLayoutBinCache doesn't ship (measured on Hunt: Showdown 1896: every
/// such pipeline carries field 2F2F = 1, every shipped-layout one 2F2F = 0). A twin is a pipeline whose record matches field
/// for field except the layout id and 2F2F and whose layout is shipped: the missing layout is taken to be its twin's.
/// That's inferred, not proven, so every twin-resolved pipeline stays untested.</summary>
public static class CryTwins
{
    public const string Entry = "shaders/cache/d3d12/pipelinestatebincache.txt";
    const string Pak = "engine/shadercache.pak";
    const ushort LayoutField = 0x6CDB, FlagField = 0x2F2F, ComputeLayoutField = 0x1097;
    /// <summary>Bytes of the cache file read (measured 47.8 MB).</summary>
    const long MaxFile = 512L << 20;

    /// <summary>The graphics pipeline records of the install; empty when the pak or the cache file isn't there or doesn't read.</summary>
    public static List<CryPipeline> Read(string installDir)
    {
        var path = GameFiles.Find(installDir, Pak);
        if (!File.Exists(path)) return [];
        using var zip = CryEngineReader.OpenPak(installDir, path);
        var e = zip?.Entries.FirstOrDefault(x => x.FullName.Equals(Entry, StringComparison.OrdinalIgnoreCase));
        if (e == null || e.Length > MaxFile) return [];
        var b = new byte[e.Length];
        try
        {
            using var s = e.Open();
            s.ReadExactly(b);
        }
        catch (Exception x) when (x is InvalidDataException or IOException or NotSupportedException) { return []; }
        return Parse(b);
    }

    /// <summary>Records with a graphics layout id; compute records (layout in 9710) and records that don't frame are skipped.</summary>
    public static List<CryPipeline> Parse(ReadOnlySpan<byte> file)
    {
        var o = new List<CryPipeline>();
        var p = 0;
        while (CryLayouts.Header(file[p..], out _, out var len, out var head) && p + head + len <= file.Length)
        {
            if (Top(file.Slice(p + head, len)) is { } f && f.Count(x => x.Id == LayoutField) == 1 && f.All(x => x.Id != ComputeLayoutField)
                && f.First(x => x.Id == LayoutField).Value is { Length: 8 } id)
            {
                var desc = new List<byte>();
                foreach (var (fid, v) in f)
                    if (fid is not (LayoutField or FlagField)) desc.AddRange([(byte)fid, (byte)(fid >> 8), .. BitConverter.GetBytes(v.Length), .. v]);
                o.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(id), [.. desc]));
            }
            p += head + len;
            if (p < file.Length && file[p] == '\n') p++;
        }
        return o;
    }

    /// <summary>A record body's top-level fields: a u32 prefix before the first object marker (measured), then fields with
    /// object markers between them. Null when it doesn't frame to its end.</summary>
    internal static List<(ushort Id, byte[] Value)>? Top(ReadOnlySpan<byte> b)
    {
        if (b[..Math.Min(8, b.Length)].IndexOf(CryLayouts.Marker) is > 0 and var m) b = b[m..];
        var o = new List<(ushort, byte[])>();
        var p = 0;
        while (p < b.Length)
        {
            if (b[p..].StartsWith(CryLayouts.Marker)) { p += 4; continue; }
            if (b.Length - p < 3) return null;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
            int n = b[p + 2];
            p += 3;
            if (n == 0xFE)
            {
                if (b.Length - p < 2) return null;
                n = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
                p += 2;
            }
            if (b.Length - p < n) return null;
            o.Add((id, b.Slice(p, n).ToArray()));
            p += n;
        }
        return o;
    }

    /// <summary>Every layout id the pipelines use that <paramref name="shipped"/> lacks. One twin layout resolves it; two are
    /// both kept (hedged); more than two leave it out. An id with no twin takes <see cref="BestFit"/> of the shaders
    /// <paramref name="shaders"/> gives for it, when it gives any, within <paramref name="fitChecks"/> shader/layout checks in all.
    /// A resolved id still needs <see cref="Fits"/> per pipeline.</summary>
    public static Dictionary<ulong, CryTwin> Resolve(IEnumerable<CryPipeline> pipelines, IReadOnlyDictionary<ulong, CryLayout> shipped,
        Func<ulong, IEnumerable<CryShader>?>? shaders = null, long fitChecks = 50_000_000, CancellationToken ct = default)
    {
        var uses = new Dictionary<ulong, int>();
        var byDesc = new Dictionary<string, (HashSet<ulong> Missing, HashSet<ulong> Shipped)>();
        foreach (var pl in pipelines)
        {
            var isShipped = shipped.ContainsKey(pl.Layout);
            if (!isShipped) uses[pl.Layout] = uses.GetValueOrDefault(pl.Layout) + 1;
            var key = Convert.ToBase64String(pl.Desc);
            if (!byDesc.TryGetValue(key, out var g)) byDesc[key] = g = ([], []);
            (isShipped ? g.Shipped : g.Missing).Add(pl.Layout);
        }
        var twins = uses.Keys.ToDictionary(k => k, _ => new SortedSet<ulong>());
        // three distinct twins already decide TooManyTwins; collecting more is quadratic in the worst case
        foreach (var (missing, sh) in byDesc.Values)
            foreach (var m in missing)
            {
                var t = twins[m];
                foreach (var x in sh)
                {
                    if (t.Count > 2) break;
                    t.Add(x);
                }
            }
        // only a layout that builds can be picked
        List<CryLayout>? buildable = null;
        long checks = 0;
        return uses.ToDictionary(u => u.Key, u =>
        {
            var t = Twin(u.Key, twins[u.Key].ToArray(), u.Value);
            if (t.State != CryTwinState.NoTwin || shaders?.Invoke(u.Key) is not { } given) return t;
            CryShader[] sh = [.. given];
            buildable ??= [.. shipped.Values.Where(l => CryRootSig.For(l.Id, shipped).Blob != null)];
            if ((checks += (long)sh.Length * buildable.Count) > fitChecks) return t with { Why = "no twin, and fitting its shaders is past the check bound" };
            return BestFit(t, sh, buildable, shipped, ct);
        });
    }

    /// <summary>An id with no twin, rebuilt by the same rules its shaders bind by (space = slot index, register = place in the
    /// type-sorted set): the shipped layouts that fit the most of its shaders, the smallest of those, one per distinct root
    /// signature. One resolves it, two are hedged; none fitting or more than two leave it out.</summary>
    static CryTwin BestFit(CryTwin t, CryShader[] shaders, List<CryLayout> candidates, IReadOnlyDictionary<ulong, CryLayout> shipped, CancellationToken ct)
    {
        var score = candidates.Select(l => { ct.ThrowIfCancellationRequested(); return (l, n: shaders.Count(s => Misfit(s, l) == null)); }).ToList();
        var top = score.Count == 0 ? 0 : score.Max(x => x.n);
        if (top == 0) return t with { Why = "no twin, and no shipped layout fits its shaders" };
        var best = score.Where(x => x.n == top).Select(x => x.l).ToList();
        var small = best.Min(Size);
        var picks = best.Where(l => Size(l) == small).OrderBy(l => l.Id)
            .DistinctBy(l => Convert.ToBase64String(CryRootSig.For(l.Id, shipped).Blob!)).Select(l => l.Id).ToArray();
        return picks.Length switch
        {
            > 2 => t with { State = CryTwinState.TooManyTwins, Twins = picks, Why = $"no twin, {picks.Length} layouts fit equally", ByFit = true },
            _ => t with { State = picks.Length == 1 ? CryTwinState.Twin : CryTwinState.Hedged, Twins = picks, Why = null, ByFit = true },
        };
    }

    static int Size(CryLayout l) => l.Slots.Sum(s => s.Bindings.Length);

    static CryTwin Twin(ulong id, ulong[] t, int n) => t.Length switch
    {
        0 => new(id, CryTwinState.NoTwin, t, n, "no pipeline with a shipped layout matches its pipelines"),
        > 2 => new(id, CryTwinState.TooManyTwins, t, n, "more than two twin layouts"),
        1 => new(id, CryTwinState.Twin, t, n, null),
        _ => new(id, CryTwinState.Hedged, t, n, null),
    };

    /// <summary>Why a pipeline of a twin-resolved layout is left out, or null when every one of its shaders fits every twin.
    /// Null <paramref name="shaders"/> (not linked) leaves it out.</summary>
    public static string? Fits(CryTwin t, IReadOnlyDictionary<ulong, CryLayout> shipped, IEnumerable<CryShader>? shaders) =>
        t.State is not (CryTwinState.Twin or CryTwinState.Hedged) ? t.Why
        : shaders == null ? "its shaders aren't linked"
        : Misfit(shaders, t.Twins.Select(x => shipped[x]));

    /// <summary>Unique twins as CryRootSig's rebuilt source: the twin layout under the missing id.</summary>
    public static Dictionary<ulong, CryLayout> Rebuilt(IReadOnlyDictionary<ulong, CryTwin> twins, IReadOnlyDictionary<ulong, CryLayout> shipped) =>
        twins.Values.Where(t => t.State == CryTwinState.Twin).ToDictionary(t => t.Id, t => shipped[t.Twins[0]] with { Id = t.Id });

    /// <summary>The root signatures to plan a pipeline of layout <paramref name="id"/> with: its shipped one, its unique twin's,
    /// or both hedged twins'. Empty with why when it's left out.</summary>
    public static (List<byte[]> Blobs, string? Why) RootSignatures(ulong id, IReadOnlyDictionary<ulong, CryLayout> shipped,
        IReadOnlyDictionary<ulong, CryTwin> twins, IReadOnlyDictionary<ulong, CryLayout> rebuilt)
    {
        var ids = shipped.ContainsKey(id) || rebuilt.ContainsKey(id) ? [id]
            : twins.TryGetValue(id, out var t) && t.State == CryTwinState.Hedged ? t.Twins : [];
        if (ids.Length == 0) return ([], twins.TryGetValue(id, out var l) ? $"layout {id:x16}: {l.Why}" : $"layout {id:x16} is neither shipped nor rebuilt");
        var o = new List<byte[]>();
        foreach (var x in ids)
        {
            var (blob, why) = CryRootSig.For(x, shipped, rebuilt);
            if (blob == null) return ([], why);
            o.Add(blob);
        }
        return (o, null);
    }

    /// <summary>Why a shader doesn't fit one of the layouts, or null when all fit. A shader's register space is a slot index
    /// and its register the binding's place in that slot (all types counted together); the binding must be of the shader's
    /// type and its stage bits must include the shader's stage. Root CBVs and constants are b0, an inline SRV t0, and an
    /// unbounded SRV range is a set's single bindless binding (flags 1 or 5).</summary>
    public static string? Misfit(IEnumerable<CryShader> shaders, IEnumerable<CryLayout> layouts)
    {
        var ls = layouts.ToArray();
        foreach (var s in shaders)
            foreach (var l in ls)
                if (Misfit(s, l) is { } why) return $"a {s.Kind switch { 0 => "ps", 1 => "vs", 2 => "gs", 3 => "hs", 4 => "ds", 5 => "cs", _ => $"kind {s.Kind}" }} doesn't fit {l.Id:x16}: {why}";
        return null;
    }

    static string? Misfit(CryShader s, CryLayout l)
    {
        int bit = s.Kind switch { 0 => 2, 1 => 1, 2 => 4, 3 => 16, 4 => 8, 5 => 32, _ => 0 };
        if (bit == 0) return "not a graphics or compute stage";
        foreach (var (rt, space, lo, hi) in s.Bindings)
        {
            int type = rt switch { 1 => 2, 2 => 0, 3 or 4 or 5 => 1, >= 6 and <= 9 => 3, _ => -1 };
            if (type < 0) return $"resource type {rt}";
            if (l.Slots.FirstOrDefault(x => x.Index == space) is not { } slot) return $"space {space} has no slot";
            var bs = slot.Bindings;
            if (bs.Length == 0) return $"slot {space} is empty";
            if (!CryRootSig.TypeSorted(slot)) return $"slot {space} lists its bindings out of type order";
            if (hi == uint.MaxValue)
            {
                if (slot.Kind != 2 || bs.Length != 1 || bs[0].Type != 1 || bs[0].Flags is not (1 or 5) || lo != 0 || type != 1) return $"unbounded t{lo} space {space}";
                if ((bs[0].Stages & bit) == 0) return $"slot {space} hides the stage";
                continue;
            }
            if (slot.Kind != 2)
            {
                if (lo != 0 || hi != 0 || type != (slot.Kind == 1 ? 1 : 0)) return $"register {lo} type {type} in inline slot {space}";
                if ((bs[0].Stages & bit) == 0) return $"slot {space} hides the stage";
                continue;
            }
            for (var i = lo; i <= hi; i++)
            {
                if (i >= bs.Length) return $"register {i} past the {bs.Length} bindings of slot {space}";
                if (bs[i].Type != type) return $"register {i} of slot {space} is type {bs[i].Type}, the shader's {type}";
                if ((bs[i].Stages & bit) == 0) return $"register {i} of slot {space} hides the stage";
            }
        }
        return null;
    }
}
