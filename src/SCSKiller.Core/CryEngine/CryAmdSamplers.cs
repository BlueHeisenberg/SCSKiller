using System.Security.Cryptography;
using SCSKiller.Core.Swarm;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.CryEngine;

/// <summary>CRYENGINE's static sampler states on AMD, where they are in each stage's cache key: a table learned from the game's
/// own DxcCache keys (<see cref="CryAmdLearn"/>) per game build. A plan opens only with a table for the exact build; a place the
/// table lacks takes its best guess (<see cref="Guess"/>), counted apart from the learned ones.</summary>
public static class CryAmdSamplers
{
    /// <summary>Version suffix: the install's build has a table (<see cref="Tables"/>).</summary>
    public const string Suffix = "+amdsamplers";

    /// <summary>The learned tables by build stamp (<see cref="Stamp"/>).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<CryAmdLearn.Place, CrySampler>> Tables =
        new Dictionary<string, IReadOnlyDictionary<CryAmdLearn.Place, CrySampler>>
        {
            // Hunt: Showdown 1896, Steam build 25749579: the places Win5's learn-3 hit (layout, register, space)
            [HuntBuild] = new Dictionary<CryAmdLearn.Place, CrySampler>
            {
                [new(0x03f470ea052ecf50, 5, 0)] = new(0x00, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0x0844f22366203806, 3, 0)] = new(0x15, 1, 1, 1, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0x31318bcfbe34ce46, 2, 0)] = new(0x14, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0x39a9c3c6e78d6f1e, 3, 0)] = new(0x00, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0x44268e76178b90ed, 6, 0)] = new(0x00, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0x44f949249e1f6100, 5, 0)] = new(0x00, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0x5f2e11d06eab7d27, 5, 0)] = new(0x00, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0x7db84c1f871798cf, 1, 0)] = new(0x14, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0xa9e46d9feb5d898a, 5, 0)] = new(0x15, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0xbc08e5656a1b11e1, 1, 0)] = new(0x14, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0xd8ce8fa071132a9d, 28, 0)] = new(0x15, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0xd9b7c012ae161e82, 9, 0)] = new(0x14, 1, 1, 1, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0xe33af797a9a5ea99, 6, 0)] = new(0x00, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0xf629c22e1d3b5d78, 3, 0)] = new(0x00, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0xf8a5ffc3b52fafc9, 28, 0)] = new(0x15, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
                [new(0xfc104cbaa9bd747a, 4, 0)] = new(0x15, 3, 3, 3, 0f, 1, 1, 0, 0f, float.MaxValue),
            },
        };

    /// <summary>Hunt: Showdown 1896's build stamp the shipped tables are for (<see cref="Stamp"/>).</summary>
    internal const string HuntBuild = "25749579/782765cb/0046e347";

    /// <summary>The built-in vertex formats learned by L3 (<see cref="CryAmdL3"/>) by build stamp: those with one hitting base
    /// (learned), and those several bases hit (a best guess, the first of them).</summary>
    public static readonly IReadOnlyDictionary<string, (IReadOnlyDictionary<ulong, LayoutElem[]> Learned, IReadOnlyDictionary<ulong, LayoutElem[]> Guessed)> FormatTables =
        new Dictionary<string, (IReadOnlyDictionary<ulong, LayoutElem[]>, IReadOnlyDictionary<ulong, LayoutElem[]>)>
        {
            // Win5's L3 of lab 7d: two formats hit one base each; one hit four bases, which takes the first with QTangents
            [HuntBuild] = (new Dictionary<ulong, LayoutElem[]>
            {
                [0xcd362d020a159db7] = CryVertexLayouts.Full(CryVertexLayouts.Bases[8].Elems, CryVertexLayouts.Tangents.Short),
                [0xf60a1957c5b6f49c] = CryVertexLayouts.Full(CryVertexLayouts.Bases[1].Elems, CryVertexLayouts.Tangents.Short),
            }, new Dictionary<ulong, LayoutElem[]>
            {
                [0xa60ddd6b13cf9a01] = CryVertexLayouts.Full(CryVertexLayouts.Bases[0].Elems, CryVertexLayouts.Tangents.Short, qTangents: true),
            }),
        };

    const string Pak = "engine/shadercache.pak";

    /// <summary>A place the table lacks takes its best guess; off: its pipelines are left out.</summary>
    public static bool GuessPlaces => !LabGuessOff("places");

    /// <summary>A built-in vertex format nothing learned takes its best-ranked base (<see cref="CryEngineReader.GuessFormats"/>);
    /// off: the VSs drawn only with such formats are left out.</summary>
    public static bool GuessFormats => !LabGuessOff("formats");

#if SCSK_TEST_HOOKS
    /// <summary>SCSKILLER_LAB_CRY_AMD_NO_GUESS: "places", "formats" or both, comma-separated (lab builds only).</summary>
    static bool LabGuessOff(string kind) => Environment.GetEnvironmentVariable("SCSKILLER_LAB_CRY_AMD_NO_GUESS")?.Split(',').Contains(kind) == true;
#else
    static bool LabGuessOff(string kind) => false;
#endif

    /// <summary>The engine's own default for a static sampler: the first engine-style candidate (trilinear, wrap, the renderer's
    /// fixed fields). No engine-style candidate depends on the place's visibility.</summary>
    public static readonly CrySampler EngineDefault = CrySampler.Merged(CrySampler.Unmerged()[108]);

    /// <summary>The best guess for a place <paramref name="table"/> lacks: the state the table gives that (register, space) most
    /// often over all layouts (ties: the lower candidate index), as the learner guesses; else <see cref="EngineDefault"/>.</summary>
    public static Func<CryAmdLearn.Place, (CrySampler State, bool ByDefault)> Guess(IReadOnlyDictionary<CryAmdLearn.Place, CrySampler> table)
    {
        var cands = CrySampler.Candidates();
        var common = table.GroupBy(x => (x.Key.Register, x.Key.Space)).ToDictionary(g => g.Key,
            g => g.GroupBy(x => x.Value).OrderByDescending(c => c.Count()).ThenBy(c => cands.IndexOf(c.Key) is var i and >= 0 ? i : int.MaxValue).First().Key);
        return p => common.TryGetValue((p.Register, p.Space), out var c) ? (c, false) : (EngineDefault, true);
    }

    /// <summary>The store build id with the CRC-32s of the shipped layouts and pipeline records; null without a build id or either file.</summary>
    public static string? Stamp(Game game)
    {
        if (game.Version is not { Length: > 0 } build) return null;
        var path = Path.Combine(game.InstallDir, Pak);
        if (!File.Exists(path)) return null;
        using var zip = SwarmReader.Open(path, out _);
        uint? Crc(string name) => zip?.Entries.FirstOrDefault(x => x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Crc32;
        return Crc(CryLayouts.Entry) is { } l && Crc(CryLayouts.PipelineEntry) is { } p ? $"{build}/{l:x8}/{p:x8}" : null;
    }

    /// <summary>Detect's version suffix: <see cref="Suffix"/> and a hash of the table's content (its places and the build's
    /// formats) when <see cref="For"/> gives a table, else "". Another table for the same build is another engine version,
    /// which plans the game again.</summary>
    public static string Suffixed(Game game, IReadOnlyDictionary<ulong, CryLayout> shipped,
        IReadOnlyDictionary<string, IReadOnlyDictionary<CryAmdLearn.Place, CrySampler>>? tables = null)
    {
        if (For(game, shipped, tables) is not { } table) return "";
        var (learned, guessed) = Formats(Stamp(game)!, game.InstallDir);
        var lines = table.Select(x => $"p {x.Key.Layout:x16} {Hex(x.Value.Desc(x.Key.Register, x.Key.Space, 0))}")
            .Concat(learned.Select(x => $"f {x.Key:x16} {string.Join(';', x.Value)}"))
            .Concat(guessed.Select(x => $"g {x.Key:x16} {string.Join(';', x.Value)}"))
            .Order(StringComparer.Ordinal);
        return $"{Suffix}.{Hex(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines))))[..8]}";
    }

    /// <summary>The built-in formats for <paramref name="stamp"/>: learned, and guessed (an L3 report's ambiguous ones); a lab's
    /// L3 report in lab builds.</summary>
    internal static (IReadOnlyDictionary<ulong, LayoutElem[]> Learned, IReadOnlyDictionary<ulong, LayoutElem[]> Guessed) Formats(string stamp, string installDir)
    {
#if SCSK_TEST_HOOKS
        if (Lab(installDir) is { } lab) return (lab.Formats, lab.FormatGuesses);
#endif
        return FormatTables.TryGetValue(stamp, out var t) ? t : (CryEngineReader.BuiltInFormats, new Dictionary<ulong, LayoutElem[]>());
    }

    /// <summary>The table of the install's build; null without one. Places the plan uses and the table lacks take a guess
    /// (<see cref="Apply"/>), vertex formats nothing defines take theirs in the index (<see cref="CryEngineReader.GuessFormats"/>).</summary>
    public static IReadOnlyDictionary<CryAmdLearn.Place, CrySampler>? For(Game game, IReadOnlyDictionary<ulong, CryLayout> shipped,
        IReadOnlyDictionary<string, IReadOnlyDictionary<CryAmdLearn.Place, CrySampler>>? tables = null)
    {
#if SCSK_TEST_HOOKS
        if (tables == null && Lab(game.InstallDir) is { } lab) tables = lab.Tables;
#endif
        if (shipped.Count == 0 || Stamp(game) is not { } stamp) return null;
        return (tables ?? Tables).GetValueOrDefault(stamp);
    }

    /// <summary>The plan body with each pipeline under the static form of every named layout (<paramref name="named"/>; null: all)
    /// that builds its root signature: one copy per distinct form, so layouts sharing a plan root signature but taking other
    /// states each get theirs, and equal states give the body as one form would. A place the table lacks takes its best guess
    /// (<see cref="Guess"/>; with <paramref name="guess"/> off the form is left out). A pipeline whose root signature only
    /// unnamed layouts build is dropped. Counts: "pipelines" (the body's pipelines afterwards), "learned" (copies on a form with
    /// every place learned or none), "guessed" (copies on a form with a guessed place), of them "guessedByDefault" (one taking
    /// <see cref="EngineDefault"/>), "asPlanned" (on a root signature no shipped layout builds: unchanged), "unlearned" (forms
    /// left out for a place with no state), "unnamed" (dropped), "split" (pipelines copied for more than one form),
    /// "extraCompiles" (the copies past each split pipeline's first), "places", "placesLearned" and "placesGuessed" (the static
    /// places of the named layouts building a plan root signature, those the table has, those guessed). Null body when nothing
    /// changes. A template a kept pipeline item refers to stays.</summary>
    public static (List<Rec>? Body, SortedDictionary<string, int> Counts) Apply(IReadOnlyList<Rec> body, IReadOnlyDictionary<ulong, CryLayout> layouts,
        IReadOnlyDictionary<CryAmdLearn.Place, CrySampler> table, IReadOnlySet<ulong>? named = null, bool? guess = null)
    {
        var guessing = guess ?? GuessPlaces;
        var best = Guess(table);
        var counts = new SortedDictionary<string, int>();
        void Count(string k, int n = 1) => counts[k] = counts.GetValueOrDefault(k) + n;
        // per plan root signature, its named layouts' forms: "" without static places, "?" with a place left out; Guess 0 learned,
        // 1 a place guessed, 2 one at the engine default
        var forms = new Dictionary<string, List<(string Sha, byte[] Blob, int Guess)>>();
        var unnamed = new HashSet<string>();
        var placesOf = new List<(string Sha, int Places, int Learned)>();
        foreach (var l in layouts.Values.OrderBy(l => l.Id))
        {
            if (CryRootSig.For(l.Id, layouts).Blob is not { } b) continue;
            var sha = Hex(SHA1.HashData(b));
            if (named != null && !named.Contains(l.Id)) { unnamed.Add(sha); continue; }
            var places = CryAmdLearn.PlacesOf(l);
            placesOf.Add((sha, places.Length, places.Count(table.ContainsKey)));
            (string, byte[], int) to = ("", [], 0);
            if (!guessing && !places.All(table.ContainsKey)) to = ("?", [], 0);
            else if (places.Length > 0)
            {
                var g = places.Where(p => !table.ContainsKey(p)).Select(p => best(p).ByDefault).ToList();
                var blob = CryRootSig.Static(l, (reg, space, vis) => (table.TryGetValue(new(l.Id, reg, space), out var st) ? st : best(new(l.Id, reg, space)).State).Desc(reg, space, vis), out _);
                to = (Hex(SHA1.HashData(blob)), blob, g.Count == 0 ? 0 : g.Any(x => x) ? 2 : 1);
            }
            if (!forms.TryGetValue(sha, out var list)) forms[sha] = list = [];
            // the same form learned on one layout and guessed on another counts as learned
            if (list.FindIndex(x => x.Sha == to.Item1) is var at and >= 0) { if (to.Item3 < list[at].Guess) list[at] = to; }
            else list.Add(to);
        }
        unnamed.ExceptWith(forms.Keys);
        string Rs(Rec r) => r.Tag == 'P' ? ParseItem(r.Payload).Rs : Parse(r).Rs;
        // the places the plan uses: those of the named layouts building one of its root signatures
        var planned = body.Where(r => r.Tag is 'G' or 'C' or 'S' or 'P').Select(Rs).ToHashSet();
        counts["places"] = placesOf.Where(x => planned.Contains(x.Sha)).Sum(x => x.Places);
        counts["placesLearned"] = placesOf.Where(x => planned.Contains(x.Sha)).Sum(x => x.Learned);
        counts["placesGuessed"] = guessing ? counts["places"] - counts["placesLearned"] : 0;
        var templates = body.Where(r => r.Tag == 'P').Select(r => ParseItem(r.Payload).Template).ToHashSet();
        var dropped = body.Where(r => r.Tag is 'G' or 'C' or 'S' or 'P' && !templates.Contains(r.Key) && unnamed.Contains(Rs(r))).Select(r => r.Key).ToHashSet();
        var kept = body.Where(r => r.Tag == 'P' && !dropped.Contains(r.Key)).Select(r => ParseItem(r.Payload).Template).ToHashSet();
        var o = new List<Rec>();
        var blobs = new List<Rec>();
        var added = new HashSet<string>();
        var changed = false;
        foreach (var r in body)
        {
            if (r.Tag is not ('G' or 'C' or 'S' or 'P') || kept.Contains(r.Key)) { o.Add(r); continue; }
            var rs = Rs(r);
            if (dropped.Contains(r.Key) || templates.Contains(r.Key) && unnamed.Contains(rs)) { Count("unnamed"); changed = true; continue; }
            if (templates.Contains(r.Key)) { o.Add(r); continue; }
            if (!forms.TryGetValue(rs, out var fs)) { Count("asPlanned"); o.Add(r); continue; }
            if (fs.Any(f => f.Sha == "?")) { Count("unlearned"); changed = true; }
            var learned = fs.Where(f => f.Sha != "?").ToList();
            if (learned.Count > 1)
            {
                Count("split");
                Count("extraCompiles", learned.Count - 1);
            }
            foreach (var (sha, blob, g) in learned)
            {
                Count(g == 0 ? "learned" : "guessed");
                if (g == 2) Count("guessedByDefault");
                if (sha == "") { o.Add(r); continue; }
                changed = true;
                if (added.Add(sha)) blobs.Add(new Rec('B', [.. Convert.FromHexString(sha), .. blob]));
                o.Add(WithRootSignature(r, sha));
            }
        }
        counts["pipelines"] = counts.GetValueOrDefault("learned") + counts.GetValueOrDefault("guessed") + counts.GetValueOrDefault("asPlanned");
        return (changed ? [.. o, .. blobs] : null, counts);
    }
#if SCSK_TEST_HOOKS
    /// <summary>A lab's tables from <c>amd-learn</c> reports, in memory only: the sampler table under the stamp it names, and
    /// the built-in formats the L3 report learned.</summary>
    public sealed record LabTables(IReadOnlyDictionary<string, IReadOnlyDictionary<CryAmdLearn.Place, CrySampler>> Tables, IReadOnlyDictionary<ulong, LayoutElem[]> Formats,
        IReadOnlyDictionary<ulong, LayoutElem[]> FormatGuesses);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), LabTables> labs = new();

    /// <summary>SCSKILLER_LAB_CRY_AMD="&lt;stamp&gt;|&lt;sampler report&gt;[|&lt;L3 report&gt;]" (lab builds only); null without it.</summary>
    internal static LabTables? Lab(string installDir) => Environment.GetEnvironmentVariable("SCSKILLER_LAB_CRY_AMD") is { Length: > 0 } spec
        ? labs.GetOrAdd((spec, installDir), k => Lab(k.Item1, k.Item2)) : null;

    /// <summary>The tables of <paramref name="spec"/> for the install at <paramref name="installDir"/>. Reads "place n candidate k"
    /// and "format n candidate k" lines, indexes as the reports give them; shipped formats keep their record.</summary>
    public static LabTables Lab(string spec, string installDir)
    {
        var parts = spec.Split('|');
        if (parts is not ([_, _] or [_, _, _])) throw new InvalidOperationException("SCSKILLER_LAB_CRY_AMD: <stamp>|<sampler report>[|<L3 report>]");
        var (stamp, samplerReport, l3Report) = (parts[0], parts[1], parts.Length > 2 ? parts[2] : "");
        var places = CryAmdLearn.AllPlaces(CryLayouts.Read(installDir));
        var table = CryAmdLearn.ReadTable(File.ReadLines(samplerReport), places);
        var (formats, guesses) = l3Report.Length > 0 ? ReadFormats(File.ReadAllLines(l3Report), installDir) : ([], []);
        return new(new Dictionary<string, IReadOnlyDictionary<CryAmdLearn.Place, CrySampler>> { [stamp] = table }, formats, guesses);
    }
#endif

    /// <summary>The built-in formats of an L3 report ("format n candidate k", n into the install's vertex formats by id, k into
    /// <see cref="CryVertexLayouts.Bases"/> then their per-instance forms; "format n qtangents 1"; "tangents Float"): those with
    /// one hitting base, and those with an "ambiguous" line, at the candidate the report names. Shipped formats keep their record.</summary>
    public static (Dictionary<ulong, LayoutElem[]> Learned, Dictionary<ulong, LayoutElem[]> Ambiguous) ReadFormats(IReadOnlyList<string> lines, string installDir)
    {
        var learned = new Dictionary<ulong, LayoutElem[]>();
        var ambiguous = new Dictionary<ulong, LayoutElem[]>();
        var tangents = lines.Any(l => l.Contains("tangents Float")) ? CryVertexLayouts.Tangents.Float : CryVertexLayouts.Tangents.Short;
        var (shipped, _, records) = CryStates.Read(installDir);
        var ids = records.Where(r => r.VertexFormat != null).Select(r => r.VertexFormat!.Value).Distinct().Order().ToList();
        var bases = CryVertexLayouts.Bases.Select(b => b.Elems).ToList();
        var split = lines.Select(l => l.Split(' ')).ToList();
        var q = split.Where(x => x is ["format", _, "qtangents", "1"]).Select(x => x[1]).ToHashSet();
        var several = split.Where(x => x is ["format", _, "ambiguous", _]).Select(x => x[1]).ToHashSet();
        foreach (var x in split)
            if (x is ["format", var n, "candidate", var k] && int.TryParse(n, out var f) && int.TryParse(k, out var c) && f < ids.Count && !shipped.ContainsKey(ids[f])
                && c < 2 * bases.Count)
                (several.Contains(n) ? ambiguous : learned)[ids[f]] = CryVertexLayouts.Full(c < bases.Count ? bases[c] : CryVertexLayouts.Instanced(bases[c - bases.Count]), tangents, q.Contains(n));
        return (learned, ambiguous);
    }
}
