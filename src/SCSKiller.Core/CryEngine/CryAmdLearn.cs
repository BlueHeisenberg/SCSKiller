using System.Buffers.Binary;
using System.Security.Cryptography;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.CryEngine;

/// <summary>AMD DxcCache .parc entries: each NTRY tag, at any offset, starts an entry that ends at the next NTRY or FOTR tag; the
/// u32 after the tag is a running index. The entry's key is the 8 bytes at <see cref="KeyAt"/> (measured: Hunt: Showdown 1896,
/// three starts, unique per entry and equal between them for 29,166 of 29,166).</summary>
public static class AmdParc
{
    public const int KeyAt = 44;

    public static void Keys(ReadOnlySpan<byte> file, HashSet<ulong> into, int at = KeyAt)
    {
        var tags = new List<(int At, bool Entry)>();
        foreach (var (tag, entry) in new[] { ("NTRY"u8.ToArray(), true), ("FOTR"u8.ToArray(), false) })
            for (int p = 0, i; (i = file[p..].IndexOf(tag)) >= 0; p += i + 4) tags.Add((p + i, entry));
        tags.Sort();
        for (var k = 0; k < tags.Count; k++)
        {
            if (!tags[k].Entry) continue;
            var end = k + 1 < tags.Count ? tags[k + 1].At : file.Length;
            if (tags[k].At + at + 8 <= end) into.Add(BinaryPrimitives.ReadUInt64LittleEndian(file[(tags[k].At + at)..]));
        }
    }

    /// <summary>The keys of every file, read with shared access (the driver may hold them).</summary>
    public static HashSet<ulong> Keys(IEnumerable<string> files, int at = KeyAt)
    {
        var o = new HashSet<ulong>();
        foreach (var f in files)
        {
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = new byte[s.Length];
            s.ReadExactly(b);
            Keys(b, o, at);
        }
        return o;
    }
}

/// <summary>A static sampler's state without its place: the D3D12_STATIC_SAMPLER_DESC fields before ShaderRegister.</summary>
public sealed record CrySampler(uint Filter, uint U, uint V, uint W, float Bias, uint Aniso, uint Compare, uint Border, float MinLod, float MaxLod)
{
    public byte[] Desc(uint register, uint space, uint visibility)
    {
        uint[] d = [Filter, U, V, W, BitConverter.SingleToUInt32Bits(Bias), Aniso, Compare, Border, BitConverter.SingleToUInt32Bits(MinLod), BitConverter.SingleToUInt32Bits(MaxLod), register, space, visibility];
        var b = new byte[52];
        for (var i = 0; i < d.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), d[i]);
        return b;
    }

    /// <summary>The states a learner tries: <see cref="Unmerged"/> with each state <see cref="Merged"/>, the first of equal ones
    /// kept. A learned table names them by index; <see cref="CryAmdLearn.ReadTable"/> reads a table of the unmerged list too.</summary>
    public static List<CrySampler> Candidates() => [.. Unmerged().Select(Merged).Distinct()];

    /// <summary>AMD's key ignores the anisotropy of a filter without D3D12_ANISOTROPIC_FILTERING_BIT (measured: Hunt: Showdown,
    /// aniso moved 0 keys on filter 0x15, and every place that hit two candidates hit an aniso 1 and 16 pair).</summary>
    public static CrySampler Merged(CrySampler s) => (s.Filter & 0x40) == 0 ? s with { Aniso = 1 } : s;

    /// <summary>The 204 states of reports before the merge, by their indexes; new ones only ever go at the end.
    /// 0-107: point, bilinear, trilinear and anisotropic filters and comparison filters, one address mode on all axes, compare
    /// NEVER or a depth test, LOD 0 to max, three border colours. From 108: the fixed fields as CRYENGINE 5's renderer fills
    /// a static sampler (read to understand): compare ALWAYS, or LESS on a comparison filter; anisotropy 1 unless the filter
    /// is anisotropic (16, 8, 4, 2: the game's setting); LOD 0 to 100, or to 0 without a mip filter; border opaque black; no
    /// bias; wrap, clamp, mirror or border on all axes, or U and V together with W the other of wrap and clamp. A state
    /// outside them is never learned.</summary>
    public static List<CrySampler> Unmerged()
    {
        var o = new List<CrySampler>();
        (uint Mode, uint Border)[] address = [(1, 0), (3, 0), (2, 0), (4, 0), (4, 1), (4, 2)];
        foreach (var f in new uint[] { 0x15, 0x14, 0x00 })
            foreach (var aniso in new uint[] { 1, 16 })
                foreach (var (m, b) in address) o.Add(new(f, m, m, m, 0, aniso, 1, b, 0, float.MaxValue));
        foreach (var aniso in new uint[] { 16, 8, 4, 2 })
            foreach (var (m, b) in address) o.Add(new(0x55, m, m, m, 0, aniso, 1, b, 0, float.MaxValue));
        foreach (var f in new uint[] { 0x95, 0x94, 0x80 })
            foreach (var cmp in new uint[] { 4, 2, 7, 5 })
                foreach (var (m, b) in address.Where(a => a.Mode is 3 or 4)) o.Add(new(f, m, m, m, 0, 1, cmp, b, 0, float.MaxValue));

        (uint U, uint W)[] axes = [(1, 1), (3, 3), (2, 2), (4, 4), (1, 3), (3, 1)];
        void Add(uint filter, uint aniso, uint cmp, float maxLod)
        {
            foreach (var (u, w) in axes) o.Add(new(filter, u, u, w, 0, aniso, cmp, 1, 0, maxLod));
        }
        foreach (var f in new uint[] { 0x15, 0x14, 0x00 })
            foreach (var lod in new[] { 100f, 0f })
                Add(f, 1, 8, lod);
        foreach (var aniso in new uint[] { 16, 8, 4, 2 }) Add(0x55, aniso, 8, 100f);
        foreach (var f in new uint[] { 0x95, 0x94, 0x80 })
            foreach (var lod in new[] { 100f, 0f })
                Add(f, 1, 2, lod);
        return o;
    }

    /// <summary>The first candidate with one fixed field changed, by name: what the field probe compiles to see whether AMD's key
    /// moves with that field at all.</summary>
    public static IEnumerable<(string Field, CrySampler State)> FieldVariants(CrySampler s) =>
    [
        ("compare", s with { Compare = s.Compare == 8 ? 1u : 8u }),
        ("maxLod", s with { MaxLod = s.MaxLod == float.MaxValue ? 100f : float.MaxValue }),
        ("border", s with { Border = s.Border == 0 ? 1u : 0u }),
        ("aniso", s with { Aniso = s.Aniso == 16 ? 1u : 16u }),
        ("bias", s with { Bias = s.Bias == 0 ? 0.5f : 0f }),
        ("addressW", s with { W = s.W == 1 ? 3u : 1u }),
    ];
}

/// <summary>One learner batch: its entry keys per cache file kind (the file name's third part), and what the warm created
/// and failed.</summary>
public sealed record CryBatch(Dictionary<string, HashSet<ulong>> ByKind, long Created, long Failed, bool Clean = true)
{
    public HashSet<ulong> Keys => [.. ByKind.Values.SelectMany(k => k)];

    /// <summary>Every one of <paramref name="items"/> created, nothing failed, the process ended clean: only such a batch can
    /// stand for what the items make (a control missing keys would turn them into hits).</summary>
    public bool Complete(int items) => Clean && Failed == 0 && Created >= items;

    /// <summary>A warm that may stand for its items' keys: completed with every item created by a process that lived. The warm
    /// retries a dead process by itself and counts the item it crashed on in DriverSkipped, not Failed, while Done still
    /// reaches every item, and the dead process may not have written what it compiled; so any death, skip or item not
    /// created here (Cached, Known, Invalid, Unsupported) makes it unclean.</summary>
    public static bool CleanRun(WarmResult? r) => r is
    {
        Outcome: WarmOutcome.Completed, Failed: 0, Unclean: false, Unnamed: false, DiedUnnamed: false, DriverSkipped: 0, Skipped: 0,
        Cached: 0, Known: 0, Invalid: 0, Unsupported: 0, Error: null,
    } && r.Crashed is not { Count: > 0 } && r.Culprits is not { Count: > 0 };
}

/// <summary>Learns Hunt's static sampler states on AMD, where they reach the cache key, from the game's own DxcCache entry keys.
/// A compute pipeline is compiled under each candidate state for the one place of its root signature not yet known. A hit is
/// a key the game's cache holds that the same pipelines don't make under <see cref="Control"/>, a state outside the
/// candidates, so keys that don't move with the state never count. Places another layout's learned (register, space) gives
/// are tried with that state. Round 0 checks the plan's own root signatures the same way, against ALLOW_STREAM_OUTPUT, a
/// flag that changes AMD's key (measured). A probe first shows which file kinds' keys move with a sampler state at all, and
/// it stops when none does. Only keys are compared; nothing of the game's cache is kept.</summary>
public sealed class CryAmdLearn
{
    /// <summary>One compute pipeline: its shader and layout.</summary>
    public sealed record Pipe(string Cs, CryLayout Layout);

    public readonly record struct Place(ulong Layout, uint Register, uint Space);

    /// <summary>One graphics pipeline: VS, PS (null: alone), layout, input layout, D3D topology and render pass formats.
    /// <paramref name="From"/>: where its input layout came from ("none" without vertex input, "shipped", "builtin").</summary>
    public sealed record Gfx(string Vs, string? Ps, CryLayout Layout, LayoutElem[] Input, uint Topology, uint[] Rt, uint Dsv, string From = "");

    /// <summary>Compiles compute pipelines (shader SHA-1, root signature bytes) in one fresh process and cache.</summary>
    public required Func<IReadOnlyList<(string Cs, byte[] Rs)>, CancellationToken, Task<CryBatch>> Compile { get; init; }
    /// <summary>The game's entry keys.</summary>
    public required IReadOnlySet<ulong> Game { get; init; }
    /// <summary>Compiles graphics pipelines in one fresh process and cache; null: no graphics rounds.</summary>
    public Func<IReadOnlyList<(Gfx Item, byte[] Rs)>, CancellationToken, Task<CryBatch>>? CompileGraphics { get; init; }
    public List<CrySampler> Candidates { get; init; } = CrySampler.Candidates();
    /// <summary>MIRROR_ONCE, which no candidate uses.</summary>
    public CrySampler Control { get; init; } = new(0x00, 5, 5, 5, 0, 1, 1, 0, 0, float.MaxValue);
    public int BatchSize { get; init; } = 500;
    public int ProbeSize { get; init; } = 64;
    public IProgress<string>? Log { get; init; }
    /// <summary>Called with each place the moment it is fixed.</summary>
    public Action<Place, int>? Fixed { get; init; }
    /// <summary>Called after each round, compute and graphics, round 0 included: an interrupted run keeps what it learned.</summary>
    public Action? RoundDone { get; init; }
    public SortedDictionary<string, long> Counts { get; } = [];
    /// <summary>What was learned: each place's candidate index.</summary>
    public Dictionary<Place, int> Known { get; } = [];
    /// <summary>Places where more than one candidate hit (AMD's key ignores a field there): every candidate that did. Such a
    /// place keeps the first in <see cref="Known"/> but never lends it to another layout's place.</summary>
    public Dictionary<Place, int[]> Ambiguous { get; } = [];
    /// <summary>Pipelines tried without a hit: their index in <see cref="Run"/>'s list, their unknown places, and whether alone,
    /// those places under <see cref="Control"/>, they make a key the game's cache holds that not every process makes (a key
    /// that doesn't move with the state: the game compiled the pipeline under some state).</summary>
    public List<(int Pipe, Place[] Places, bool AnyInGame)> NoHit { get; } = [];
    HashSet<ulong>? everyProcess;
    /// <summary>Every place an item was opened on, compute or graphics.</summary>
    public HashSet<Place> Tried { get; } = [];
    /// <summary>Every place some item searched over every candidate.</summary>
    public HashSet<Place> SearchedAll { get; } = [];
    /// <summary>Graphics items tried without a hit: their index in <see cref="Run"/>'s list, their unknown places, and whether their
    /// layout's control batch made a key the game's cache holds that not every graphics process makes.</summary>
    public List<(int Item, Place[] Places, bool AnyInGame)> GraphicsNoHit { get; } = [];
    HashSet<ulong>? everyGraphics;

    void Count(string k, long n = 1) => Counts[k] = Counts.GetValueOrDefault(k) + n;

    public static Place[] PlacesOf(CryLayout l) => [.. CryRootSig.StaticPlaces(l).Select(p => new Place(l.Id, p.Register, p.Space))];

    /// <summary>Every static place of <paramref name="layouts"/> in report order: a report's place n is item n.</summary>
    public static List<Place> AllPlaces(IReadOnlyDictionary<ulong, CryLayout> layouts) =>
        [.. layouts.Values.SelectMany(PlacesOf).Distinct().OrderBy(p => p.Layout).ThenBy(p => p.Space).ThenBy(p => p.Register)];

    byte[] Rs(CryLayout l, Func<Place, CrySampler> state) =>
        CryRootSig.Static(l, (reg, space, vis) => state(new(l.Id, reg, space)).Desc(reg, space, vis), out _);

    static byte[] Plain(CryLayout l, uint moreFlags = 0)
    {
        var d = CryRootSig.Build(l);
        return Planning.RootSig.Serialize(d with { Flags = d.Flags | moreFlags }, []);
    }

    async Task<CryBatch> Batch(IReadOnlyList<(string Cs, byte[] Rs)> psos, CancellationToken ct)
    {
        Count("processes");
        var b = await Compile(psos, ct);
        Count("created", b.Created);
        Count("failed", b.Failed);
        if (!b.Complete(psos.Count)) Count("incompleteBatches");
        else if (everyProcess == null) everyProcess = b.Keys;
        else everyProcess.IntersectWith(b.Keys);
        return b;
    }

    async Task<CryBatch> GraphicsBatch(IReadOnlyList<(Gfx Item, byte[] Rs)> psos, CancellationToken ct)
    {
        Count("graphics.processes");
        var b = await CompileGraphics!(psos, ct);
        Count("graphics.created", b.Created);
        Count("graphics.failed", b.Failed);
        if (!b.Complete(psos.Count)) Count("graphics.incompleteBatches");
        else if (everyGraphics == null) everyGraphics = b.Keys;
        else everyGraphics.IntersectWith(b.Keys);
        return b;
    }

    /// <param name="gfx">graphics items: their rounds follow each compute round on the same <see cref="Known"/>, until neither
    /// fixes a place</param>
    /// <param name="gfx0">graphics items on layouts without static places: graphics round 0, the state-independent control</param>
    /// <param name="round0Only">stop after compute round 0, graphics round 0 and the probe</param>
    public async Task Run(IReadOnlyList<Pipe> pipes, CancellationToken ct, IReadOnlyList<Gfx>? gfx = null, IReadOnlyList<Gfx>? gfx0 = null, bool round0Only = false)
    {
        var none = pipes.Where(p => PlacesOf(p.Layout).Length == 0).ToList();
        var r0 = new HashSet<ulong>();
        var r0Control = new HashSet<ulong>();
        foreach (var chunk in none.Chunk(BatchSize))
        {
            var control = await Batch([.. chunk.Select(p => (p.Cs, Plain(p.Layout, 0x40)))], ct);
            if (!control.Complete(chunk.Length)) { Count("round0.skippedChunks"); continue; }
            r0Control.UnionWith(control.Keys);
            r0.UnionWith((await Batch([.. chunk.Select(p => (p.Cs, Plain(p.Layout)))], ct)).Keys);
        }
        var r0Hits = r0.Count(k => Game.Contains(k) && !r0Control.Contains(k));
        Count("round0.pipelines", none.Count);
        Count("round0.keys", r0.Count);
        Count("round0.inGame", r0.Count(Game.Contains));
        Count("round0.inGameNotControl", r0Hits);
        Log?.Report($"round 0: {none.Count} compute pipelines without statics, {r0.Count} keys, {r0.Count(Game.Contains)} in the game's cache, {r0Hits} of them not made under the control flag");
        RoundDone?.Invoke();
        if (CompileGraphics != null && gfx0 is { Count: > 0 }) { await GraphicsRound0(gfx0, ct); RoundDone?.Invoke(); }
        if (none.Count > 0 && r0Hits == 0 && !round0Only) { Count("stopped.round0"); return; }

        var withStatics = pipes.Where(p => PlacesOf(p.Layout).Length > 0).ToList();
        Count("places", withStatics.SelectMany(p => PlacesOf(p.Layout)).Distinct().Count());
        if (!await Probe(withStatics, ct)) { Count("stopped.probe"); return; }
        if (round0Only) { Count("stopped.round0Only"); return; }

        var tried = new Dictionary<Pipe, HashSet<string>>();
        var gTried = new Dictionary<Gfx, HashSet<string>>();
        var drop = new Dictionary<Pipe, int>();
        var gDrop = new Dictionary<Gfx, int>();
        var gItems = (gfx ?? []).Where(g => PlacesOf(g.Layout).Length > 0).ToList();
        for (var round = 1; ; round++)
        {
            var c = await Round("round", round, withStatics, p => p.Layout, (xs, rs) => Batch([.. xs.Select(p => (p.Cs, rs(p)))], ct), tried, drop, ct);
            var g = CompileGraphics == null ? 0
                : await Round("graphics.round", round, gItems, i => i.Layout, (xs, rs) => GraphicsBatch([.. xs.Select(i => (i, rs(i)))], ct), gTried, gDrop, ct);
            if (c + g == 0) break;
        }
        Count("known", Known.Count);
        Count("ambiguous", Ambiguous.Count);
        var left = withStatics.Where(p => PlacesOf(p.Layout).Any(x => !Known.ContainsKey(x))).ToList();
        var noHit = left.Where(tried.ContainsKey).ToList();
        Count("noHit", noHit.Count);
        Count("neverOpen", left.Count(p => !tried.ContainsKey(p)));
        Place[] Unknown(Pipe p) => [.. PlacesOf(p.Layout).Distinct().Where(x => !Known.ContainsKey(x))];
        var common = new HashSet<ulong>(everyProcess ?? []);
        var inGame = (await CryHits.Find(noHit, async (xs, c) => c < 0 ? (common, true)
                : await Batch([.. xs.Select(p => (p.Cs, Rs(p.Layout, x => Known.TryGetValue(x, out var k) ? Candidates[k] : Control)))], ct) is var b ? (b.Keys, b.Complete(xs.Count)) : default,
            Game, [0], BatchSize, (k, n) => Count($"anyInGame.{k}", n), ct)).Select(h => h.Item).ToHashSet();
        var order = pipes.ToList();
        foreach (var p in noHit) NoHit.Add((order.IndexOf(p), Unknown(p), inGame.Contains(p)));
        if (CompileGraphics != null) await GraphicsLeft(gItems, gfx!, gTried, ct);
    }

    /// <summary>The "place n candidate k" lines of an amd-learn report as states, n into <paramref name="places"/>. A report of the
    /// unmerged list (its first line names 204 candidates) is read by that list's indexes. One naming another place count is
    /// refused: another build of the game.</summary>
    public static Dictionary<Place, CrySampler> ReadTable(IEnumerable<string> lines, IReadOnlyList<Place> places)
    {
        var list = CrySampler.Candidates();
        var o = new Dictionary<Place, CrySampler>();
        foreach (var line in lines)
            if (System.Text.RegularExpressions.Regex.Match(line, @"candidates (\d+), places in all layouts (\d+)") is { Success: true } h)
            {
                if (int.Parse(h.Groups[2].Value) != places.Count) throw new InvalidDataException($"the report names {h.Groups[2].Value} places, the game has {places.Count}");
                var n = int.Parse(h.Groups[1].Value);
                list = n == list.Count ? list : CrySampler.Unmerged() is var u && n == u.Count ? u : throw new InvalidDataException($"the report names {n} candidates");
            }
            else if (line.Split(' ') is ["place", var p, "candidate", var k] && int.TryParse(p, out var pi) && int.TryParse(k, out var ki) && pi < places.Count && ki < list.Count)
                o[places[pi]] = CrySampler.Merged(list[ki]);
        return o;
    }

    /// <summary>Writes <paramref name="lines"/> to a temp file beside <paramref name="path"/>, then renames it over: a reader or a
    /// kill never sees half a report. A reader holding the report without delete sharing blocks the rename: tried a few times,
    /// then false, the run going on (the next round writes again).</summary>
    public static bool WriteReport(string path, IEnumerable<string> lines)
    {
        var tmp = path + ".tmp";
        var text = lines.ToList();
        for (var attempt = 0; ; attempt++)
            try
            {
                File.WriteAllLines(tmp, text);
                File.Move(tmp, path, true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 4) { Thread.Sleep(250); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A layout's root signature with its static samplers from <paramref name="table"/>. A sampler-table layout has none.
    /// A place the table lacks gives null and goes into <paramref name="missing"/>: AMD keys a stage on its static samplers, so a
    /// guessed state can't match the game's key.</summary>
    public static byte[]? LearnedRs(CryLayout l, IReadOnlyDictionary<Place, CrySampler> table, ISet<Place> missing)
    {
        if (l.SamplerTables) return CryRootSig.For(l.Id, new Dictionary<ulong, CryLayout> { [l.Id] = l }).Blob;
        var lack = PlacesOf(l).Where(p => !table.ContainsKey(p)).ToList();
        missing.UnionWith(lack);
        return lack.Count > 0 ? null : CryRootSig.Static(l, (reg, space, vis) => table[new(l.Id, reg, space)].Desc(reg, space, vis), out _);
    }

    /// <summary>One round on <paramref name="items"/>: those with at most one unknown place no guess gives are compiled under every
    /// candidate there (or, with none, once under the guesses), against the control. A guess is a hint: an item whose open places
    /// were all guessed and that missed takes its guessed places as unknown one at a time in later rounds, the rest still
    /// guessed (<paramref name="drop"/>: the index of that place among its guessed ones, -1 once each was searched). No item is
    /// tried twice under the same states. Returns the places fixed plus the items queued for such a search.</summary>
    async Task<int> Round<T>(string tag, int round, List<T> items, Func<T, CryLayout> layoutOf, Func<IReadOnlyList<T>, Func<T, byte[]>, Task<CryBatch>> batch,
        Dictionary<T, HashSet<string>> tried, Dictionary<T, int> drop, CancellationToken ct) where T : notnull
    {
        var guess = Known.Where(k => !Ambiguous.ContainsKey(k.Key)).GroupBy(k => (k.Key.Register, k.Key.Space))
            .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Value).OrderByDescending(c => c.Count()).ThenBy(c => c.Key).First().Key);
        int? Guess(Place x) => guess.TryGetValue((x.Register, x.Space), out var c) ? c : null;
        Place[] Open(T p) => [.. PlacesOf(layoutOf(p)).Distinct().Where(x => !Known.ContainsKey(x))];
        Place[] Guessed(T p) => [.. Open(p).Where(x => Guess(x) != null)];
        Place? Dropped(T p) => drop.TryGetValue(p, out var k) && k >= 0 && Guessed(p) is var gs && k < gs.Length ? gs[k] : null;
        int? GuessFor(T p, Place x) => x == Dropped(p) ? null : Guess(x);
        string Sig(T p) => string.Join(',', Open(p).Select(x => GuessFor(p, x)?.ToString() ?? "?"));
        var open = items.Where(p => Open(p) is { Length: > 0 } o && o.Count(x => GuessFor(p, x) == null) <= 1 && tried.GetValueOrDefault(p)?.Contains(Sig(p)) != true).ToList();
        if (open.Count == 0) return 0;
        foreach (var p in open) { (tried.TryGetValue(p, out var t) ? t : tried[p] = []).Add(Sig(p)); Tried.UnionWith(Open(p)); }
        Place? Searched(T p) => Open(p).Where(y => GuessFor(p, y) == null).Cast<Place?>().SingleOrDefault();
        // c = -1: the searched place, or with none every open place, takes the control state
        CrySampler State(T p, Place x, int c) => Known.TryGetValue(x, out var k) ? Candidates[k]
            : c < 0 && (Searched(p) == null || x == Searched(p)) ? Control
            : x == Searched(p) ? Candidates[c] : Candidates[GuessFor(p, x)!.Value];
        byte[] RsOf(T p, int c) => Rs(layoutOf(p), x => State(p, x, c));
        var prefix = tag == "round" ? "" : "graphics.";
        var fixedNow = new Dictionary<Place, int>();
        var hits = new HashSet<T>();
        void Fix(T p, int[] cs)
        {
            hits.Add(p);
            var searched = Searched(p);
            if (searched is { } s && cs.Length > 1) Ambiguous[s] = cs;
            if (searched is { } d && d == Dropped(p) && cs[0] != Guess(d)) Count($"{prefix}lendWrong");
            foreach (var x in Open(p))
            {
                var v = x == searched ? cs[0] : GuessFor(p, x)!.Value;
                if (x != searched) Count($"{prefix}fixedByWholeGuess");
                if (!fixedNow.TryAdd(x, v) && fixedNow[x] != v) Count($"{prefix}conflicts");
            }
        }
        var unguessed = open.Count(p => Dropped(p) != null);
        foreach (var p in open) if (Searched(p) is { } sp) SearchedAll.Add(sp);
        var whole = open.Where(p => Searched(p) == null).ToList();
        foreach (var (p, cs) in await Hitting(prefix, $"{tag}{round}.", whole, RsOf, [0], batch, ct)) Fix(p, cs);
        foreach (var (p, cs) in await Hitting(prefix, $"{tag}{round}.", [.. open.Except(whole)], RsOf, [.. Enumerable.Range(0, Candidates.Count)], batch, ct)) Fix(p, cs);
        // before Known moves: the states each missed item was tried under
        int misses = 0, queued = 0;
        foreach (var p in open.Where(p => !hits.Contains(p)))
        {
            var gs = Guessed(p);
            if (!gs.Any(x => x != Dropped(p))) continue;
            misses++;
            // a search with a place no guess gives can't drop a guess too: two unknown places at once
            if (Open(p).Length != gs.Length) continue;
            var k = drop.TryGetValue(p, out var d) ? d < 0 ? -1 : d + 1 : 0;
            drop[p] = k < gs.Length && k >= 0 ? k : -1;
            if (drop[p] >= 0) queued++;
        }
        foreach (var (place, c) in fixedNow) { Known[place] = c; Fixed?.Invoke(place, c); }
        Count($"{tag}{round}.open", open.Count);
        Count($"{tag}{round}.hit", hits.Count);
        Count($"{tag}{round}.guessMisses", misses);
        Count($"{tag}{round}.unguessedSearches", unguessed);
        Log?.Report($"{tag} {round}: {open.Count} {(prefix == "" ? "pipelines" : "items")} open ({whole.Count} guessed whole, {unguessed} with a guess taken as unknown), {hits.Count} hit, {fixedNow.Count} places fixed, {misses} missed under a guess");
        RoundDone?.Invoke();
        return fixedNow.Count + queued;
    }

    /// <summary>After the rounds, per layout of the graphics items left without a hit: its unknown places under the control, then
    /// under the first candidate, the rest as known. Per file kind, the layouts whose keys moved and those whose didn't (a stage
    /// that ignores those places, against a wrong state elsewhere), and which items made a game key not every process makes.</summary>
    async Task GraphicsLeft(List<Gfx> gItems, IReadOnlyList<Gfx> gfx, Dictionary<Gfx, HashSet<string>> gTried, CancellationToken ct)
    {
        Place[] Unknown(Gfx g) => [.. PlacesOf(g.Layout).Distinct().Where(x => !Known.ContainsKey(x))];
        var left = gItems.Where(g => gTried.ContainsKey(g) && Unknown(g).Length > 0).ToList();
        Count("graphics.noHit", left.Count);
        Count("graphics.neverOpen", gItems.Count(g => !gTried.ContainsKey(g) && Unknown(g).Length > 0));
        var controls = new Dictionary<ulong, HashSet<ulong>>();
        foreach (var group in left.GroupBy(g => g.Layout.Id))
        {
            var xs = group.ToList();
            byte[] RsUnder(Gfx g, CrySampler s) => Rs(g.Layout, x => Known.TryGetValue(x, out var k) ? Candidates[k] : s);
            var a = await GraphicsBatch([.. xs.Select(g => (g, RsUnder(g, Control)))], ct);
            var b = await GraphicsBatch([.. xs.Select(g => (g, RsUnder(g, Candidates[0])))], ct);
            if (!a.Complete(xs.Count) || !b.Complete(xs.Count)) { Count("graphics.moves.incomplete"); continue; }
            foreach (var kind in a.ByKind.Keys.Union(b.ByKind.Keys))
            {
                var x = a.ByKind.GetValueOrDefault(kind) ?? [];
                var y = b.ByKind.GetValueOrDefault(kind) ?? [];
                Count(x.SetEquals(y) ? $"graphics.moves.{kind}.still" : $"graphics.moves.{kind}.moved");
            }
            controls[group.Key] = a.Keys;
        }
        // after every batch: the keys every process makes are known only then
        var inGame = controls.Where(c => c.Value.Any(k => Game.Contains(k) && everyGraphics?.Contains(k) != true)).Select(c => c.Key).ToHashSet();
        var at = gfx.Select((g, i) => (g, i)).DistinctBy(x => x.g).ToDictionary(x => x.g, x => x.i);
        foreach (var g in left) GraphicsNoHit.Add((at[g], Unknown(g), inGame.Contains(g.Layout.Id)));
    }

    /// <summary>Graphics round 0: <paramref name="items"/> (no static place, so no state to get wrong) compiled as they are, per
    /// input-layout source, one batch per chunk. Per file kind the keys made and those in the game's cache: none there means a
    /// state of the items themselves is off (input layout, render pass, topology), not the samplers.</summary>
    async Task GraphicsRound0(IReadOnlyList<Gfx> items, CancellationToken ct)
    {
        foreach (var g in items.GroupBy(i => i.From).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var keys = new Dictionary<string, HashSet<ulong>>();
            foreach (var chunk in g.Chunk(BatchSize))
            {
                var b = await GraphicsBatch([.. chunk.Select(i => (i, Plain(i.Layout)))], ct);
                if (!b.Complete(chunk.Length)) Count("graphics.round0.incompleteBatches");
                foreach (var (kind, k) in b.ByKind) (keys.TryGetValue(kind, out var set) ? set : keys[kind] = []).UnionWith(k);
            }
            Count($"graphics.round0.{g.Key}.items", g.Count());
            foreach (var (kind, k) in keys)
            {
                Count($"graphics.round0.{g.Key}.{kind}.keys", k.Count);
                Count($"graphics.round0.{g.Key}.{kind}.inGame", k.Count(Game.Contains));
                Count($"graphics.round0.{kind}.keys", k.Count);
                Count($"graphics.round0.{kind}.inGame", k.Count(Game.Contains));
            }
        }
    }

    /// <summary>Each static place of a layout with its D3D12_SHADER_VISIBILITY (0 all, 1 vertex, 5 pixel); empty where the
    /// root signature with statics doesn't serialize.</summary>
    public static Dictionary<Place, uint> Visibility(CryLayout l)
    {
        var o = new Dictionary<Place, uint>();
        if (l.SamplerTables) return o;
        try { CryRootSig.Static(l, (reg, space, vis) => { o[new(l.Id, reg, space)] = vis; return CrySampler.Candidates()[0].Desc(reg, space, vis); }, out _); }
        catch (Exception e) when (e is Planning.RootSig.SerializeException or InvalidDataException) { o.Clear(); }
        return o;
    }

    /// <summary>The same pipelines with every place under the first candidate, then under the control: per file kind the keys of
    /// each, those in both, and those in the game's cache. False when no kind's keys differ between the two.</summary>
    async Task<bool> Probe(List<Pipe> pipes, CancellationToken ct)
    {
        var some = pipes.Take(ProbeSize).ToList();
        if (some.Count == 0) return true;
        var a = await Batch([.. some.Select(p => (p.Cs, Rs(p.Layout, _ => Candidates[0])))], ct);
        var b = await Batch([.. some.Select(p => (p.Cs, Rs(p.Layout, _ => Control)))], ct);
        Count("probe.pipelines", some.Count);
        // a batch missing keys would read as keys that moved
        if (!a.Complete(some.Count) || !b.Complete(some.Count)) { Count("probe.incomplete"); return false; }
        // which fixed fields reach the key: only those need candidates of their own
        foreach (var (field, state) in CrySampler.FieldVariants(Candidates[0]))
        {
            var v = await Batch([.. some.Select(p => (p.Cs, Rs(p.Layout, _ => state)))], ct);
            if (!v.Complete(some.Count)) { Count($"probe.field.{field}.incomplete"); continue; }
            Count($"probe.field.{field}.moved", a.Keys.Count(k => !v.Keys.Contains(k)));
        }
        var moves = false;
        foreach (var kind in a.ByKind.Keys.Union(b.ByKind.Keys).Order(StringComparer.Ordinal))
        {
            var x = a.ByKind.GetValueOrDefault(kind) ?? [];
            var y = b.ByKind.GetValueOrDefault(kind) ?? [];
            var both = x.Count(y.Contains);
            moves |= both < Math.Max(x.Count, y.Count);
            Count($"probe.{kind}.first", x.Count);
            Count($"probe.{kind}.control", y.Count);
            Count($"probe.{kind}.both", both);
            Count($"probe.{kind}.firstInGame", x.Count(Game.Contains));
            Count($"probe.{kind}.controlInGame", y.Count(Game.Contains));
        }
        return moves;
    }

    /// <summary><see cref="CryHits.Find"/>, counting per file kind the single-item batches that hit through it, and per chunk
    /// whether that kind's keys moved at all between the control and the first candidate (<paramref name="round"/>keys.*).</summary>
    Task<List<(T Item, int[] Cs)>> Hitting<T>(string prefix, string round, List<T> items, Func<T, int, byte[]> rs, int[] cands, Func<IReadOnlyList<T>, Func<T, byte[]>, Task<CryBatch>> batch,
        CancellationToken ct) where T : notnull
    {
        HashSet<ulong> control = [];
        Dictionary<string, HashSet<ulong>>? first = null;
        return CryHits.Find(items, async (xs, c) =>
        {
            var b = await batch(xs, p => rs(p, c));
            if (c < 0) { control = b.Keys; first = b.Complete(xs.Count) ? b.ByKind : null; }
            else if (first != null)
            {
                if (b.Complete(xs.Count))
                    foreach (var kind in first.Keys.Union(b.ByKind.Keys).Order(StringComparer.Ordinal))
                        Count((first.GetValueOrDefault(kind) ?? []).SetEquals(b.ByKind.GetValueOrDefault(kind) ?? []) ? $"{round}keys.{kind}.still" : $"{round}keys.{kind}.moved");
                first = null;
            }
            if (c >= 0 && xs.Count == 1)
                foreach (var (kind, keys) in b.ByKind)
                    if (keys.Any(k => Game.Contains(k) && !control.Contains(k))) Count($"{prefix}hitKind.{kind}");
            return (b.Keys, b.Complete(xs.Count));
        }, Game, cands, BatchSize, (k, n) => Count(prefix + k, n), ct);
    }

    /// <summary>A batch's warm folder: scskiller_gen.db with the shaders and root signatures, then one compute item each.</summary>
    public static void WriteBatch(string dir, IReadOnlyList<(string Cs, byte[] Rs)> psos, Func<string, byte[]> shader)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "scskiller.db"), []);
        using var f = new BufferedStream(File.Create(Path.Combine(dir, "scskiller_gen.db")), 1 << 20);
        var done = new HashSet<string>();
        foreach (var (cs, rs) in psos)
        {
            var rsSha = Hex(SHA1.HashData(rs));
            if (done.Add(cs)) Write(f, 'B', [.. Convert.FromHexString(cs), .. shader(cs)]);
            if (done.Add(rsSha)) Write(f, 'B', [.. Convert.FromHexString(rsSha), .. rs]);
        }
        foreach (var (cs, rs) in psos) Write(f, 'C', Compute(Hex(SHA1.HashData(rs)), cs));
    }

    /// <summary>A graphics batch's warm folder: the shaders and root signatures, then one pipeline stream each (VS, the PS if any,
    /// the input layout, the D3D topology's type, the render pass formats).</summary>
    public static void WriteGraphicsBatch(string dir, IReadOnlyList<(string Vs, string? Ps, byte[] Rs, LayoutElem[] Layout, uint Topology, uint[] Rt, uint Dsv)> psos,
        Func<string, byte[]> shader)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "scskiller.db"), []);
        using var f = new BufferedStream(File.Create(Path.Combine(dir, "scskiller_gen.db")), 1 << 20);
        var done = new HashSet<string>();
        foreach (var p in psos)
        {
            var rsSha = Hex(SHA1.HashData(p.Rs));
            foreach (var sh in new[] { p.Vs, p.Ps }.OfType<string>().Where(done.Add)) Write(f, 'B', [.. Convert.FromHexString(sh), .. shader(sh)]);
            if (done.Add(rsSha)) Write(f, 'B', [.. Convert.FromHexString(rsSha), .. p.Rs]);
        }
        foreach (var p in psos)
        {
            var stages = new Dictionary<int, string> { [(int)Stage.Vertex] = p.Vs };
            if (p.Ps != null) stages[(int)Stage.Pixel] = p.Ps;
            Write(f, 'S', Stream(Hex(SHA1.HashData(p.Rs)), stages, p.Layout, CryStates.TopologyType(p.Topology), p.Ps == null ? [] : p.Rt, p.Dsv));
        }
    }
}


/// <summary>The hit rule every learner shares: an item hits under candidate c when its batch's keys hold one the game's cache
/// holds and the same items compiled under the control (c = -1, once per chunk) don't make, so keys that don't move with
/// what is learned never count. Only a complete control counts (every item created, nothing failed, a clean exit): a chunk
/// whose control isn't is skipped, counted. A batch that hits is halved down to single items; one whose halves both miss
/// is counted "lostSplits" (its keys depend on what else the process made). Every candidate is tried on every item, so an
/// item several candidates hit (a field the key ignores) shows all of them.</summary>
public static class CryHits
{
    /// <summary>The items that hit, each with every candidate it hit under, in <paramref name="cands"/>' order.
    /// <paramref name="keys"/> compiles items under a candidate in one fresh process and cache: their entry keys, and whether
    /// the batch was complete. <paramref name="count"/>: (counter, amount).</summary>
    public static async Task<List<(T Item, int[] Cs)>> Find<T>(IReadOnlyList<T> items, Func<IReadOnlyList<T>, int, Task<(HashSet<ulong> Keys, bool Complete)>> keys,
        IReadOnlySet<ulong> game, int[] cands, int batchSize, Action<string, long> count, CancellationToken ct) where T : notnull
    {
        var hits = new Dictionary<T, List<int>>();
        foreach (var chunk in items.Chunk(batchSize))
        {
            var (control, complete) = await keys(chunk, -1);
            if (!complete)
            {
                count("skippedChunks", 1);
                count("skippedItems", chunk.Length);
                continue;
            }
            foreach (var c in cands) await Split([.. chunk], c, control);
        }
        return [.. items.Where(hits.ContainsKey).Select(i => (i, hits[i].ToArray()))];

        async Task<bool> Split(List<T> xs, int c, HashSet<ulong> control)
        {
            ct.ThrowIfCancellationRequested();
            var (got, complete) = await keys(xs, c);
            if (!complete) count("incompleteCandidateBatches", 1);
            if (!got.Any(k => game.Contains(k) && !control.Contains(k))) return false;
            if (xs.Count == 1)
            {
                (hits.TryGetValue(xs[0], out var l) ? l : hits[xs[0]] = []).Add(c);
                return true;
            }
            var a = await Split(xs[..(xs.Count / 2)], c, control);
            var b = await Split(xs[(xs.Count / 2)..], c, control);
            if (!a && !b) count("lostSplits", 1);
            return a || b;
        }
    }
}
