using System.Diagnostics;
using System.Security.Cryptography;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.CryEngine;

/// <summary>The plan with Hunt's static samplers at the game's slots (<see cref="CryStatics"/>): pass 1 compiles the plan's
/// pipelines whose shaders use a set sampler in throwaway processes, <see cref="Lanes"/> at a time, each single-threaded under
/// its own exe name (so its own D3DSCache folder, its rows in creation order), reads their 0x100 records and deletes exactly the
/// D3DSCache folders and NVIDIA .nvph files those names made. A pipeline whose slots the records don't all give takes the
/// statics form at default slots (its own records' where they give one, else the game's most common one for the layout's
/// place, else the register's most common one in the game's cache, else a state of its own); one whose root signature shape
/// the game's records contradict keeps the plan's.</summary>
public sealed class CryStaticsPass
{
    /// <summary>%LOCALAPPDATA%\D3DSCache and %LOCALAPPDATA%\NVIDIA\DXCache.</summary>
    public required string D3DSCacheRoot { get; init; }
    public required string NvCache { get; init; }
    /// <summary>Pass 1's own folder: never under a game's work folders, whose staged exes' D3DSCache folders a fold takes.</summary>
    public required string Dir { get; init; }
    /// <summary>Materializes a plan file into a work folder, all of it in scskiller_gen.db.</summary>
    public required Action<string, string> Materialize { get; init; }
    /// <summary>One single-threaded warm of a work folder under an exe file name.</summary>
    public required Func<string, string, Task> Warm { get; init; }
    /// <summary>A shader's sampler bindings (space, first and last register); empty when it binds none or doesn't read.</summary>
    public Func<byte[], IEnumerable<(uint Space, uint Lo, uint Hi)>> Samplers { get; init; } =
        b => CryShader.Of(b)?.Bindings.Where(x => x.Type == 1).Select(x => (x.Space, x.Lo, x.Hi)) ?? [];
    public int Lanes { get; init; } = 1;
    /// <summary>The most pipelines a pass-1 process takes; fewer when their statics need more tag states than there are.</summary>
    public int BatchSize { get; init; } = CryStatics.MaxStates;
    public IProgress<string>? Log { get; init; }
    /// <summary>Per pass-1 record with statics: its stage id and what came of it (its row's reason, or its pipeline's).</summary>
    public Action<string, string>? Trace { get; init; }
    /// <summary>What happened, by name: pipelines mapped and fallen back by reason, pass-1 records by outcome, pass-1 numbers.</summary>
    public Dictionary<string, int> Counts { get; } = [];
    public TimeSpan Pass1Time { get; private set; }
    public long ThrowawayBytes { get; private set; }

    readonly List<string> _names = [];
    HashSet<string> _folders = [], _nvph = [];   // before pass 1: never one of its own

    /// <summary>The plan body with each pipeline of a layout with set samplers under a static root signature, and the root
    /// signatures every warm process holds first; null when none is (the plan as it is).</summary>
    public (List<Rec> Body, List<byte[]> Holds)? Run(Plan plan, List<Rec> body, IReadOnlyDictionary<ulong, CryLayout> layouts,
        IReadOnlyDictionary<string, List<CryStatics.Str>>? game, CancellationToken ct)
    {
        var bySha = new Dictionary<string, CryLayout>();
        var mixed = new HashSet<string>();   // one plan root signature, layouts of both sampler forms: which the game took is unknown
        foreach (var l in layouts.Values.OrderBy(l => l.Id))
            if (CryRootSig.For(l.Id, layouts).Blob is { } b && !bySha.TryAdd(Hex(SHA1.HashData(b)), l) && bySha[Hex(SHA1.HashData(b))].SamplerTables != l.SamplerTables)
                mixed.Add(Hex(SHA1.HashData(b)));
        foreach (var m in mixed) bySha.Remove(m);
        var referenced = body.Where(r => r.Tag == 'P').Select(r => ParseItem(r.Payload).Template).ToHashSet();
        var cands = new List<(int At, CryLayout Layout, (uint Register, uint Space)[] Places)>();
        foreach (var (i, r) in body.Index())
        {
            if (r.Tag is not ('G' or 'C' or 'S' or 'P')) continue;
            var rs = r.Tag == 'P' ? ParseItem(r.Payload).Rs : Parse(r).Rs;
            if (!bySha.TryGetValue(rs, out var l) || CryRootSig.StaticPlaces(l) is not { Length: > 0 } places) continue;
            if (referenced.Contains(r.Key)) { CryStatics.Count(Counts, "fallback.template"); continue; }
            if (places.Length > CryStatics.MaxStates) { CryStatics.Count(Counts, "fallback.tooMany"); continue; }
            cands.Add((i, l, places));
        }
        Counts["pipelines"] = cands.Count;
        if (cands.Count == 0) return null;
        if (game == null) { CryStatics.Count(Counts, "fallback.noCache", cands.Count); return null; }

        var clock = Stopwatch.StartNew();
        _folders = Directory.Exists(D3DSCacheRoot) ? [.. Directory.EnumerateDirectories(D3DSCacheRoot)] : [];
        _nvph = Directory.Exists(NvCache) ? [.. Directory.EnumerateFiles(NvCache, "*.nvph")] : [];
        Directory.CreateDirectory(Dir);
        var outcome = new string?[cands.Count];             // a reason decided before pass 1's records, else null
        var own = new List<CryStatics.Str>?[cands.Count];   // the game records pass 1 matched to each pipeline
        var at = new (int Batch, int K)[cands.Count];
        try
        {
            // the plan's own pipelines materialized once: their shaders say which use a set sampler
            var templates = body.Where(r => r.Tag is 'G' or 'C' or 'S').ToDictionary(r => r.Key);
            var p1 = new List<Rec>(body.Where(r => r.Tag == 'B'));
            foreach (var c in cands)
            {
                if (body[c.At].Tag == 'P' && templates.TryGetValue(ParseItem(body[c.At].Payload).Template, out var t)) p1.Add(t);
                p1.Add(body[c.At]);
            }
            var work = Path.Combine(Dir, "work");
            var planFile = Path.Combine(Dir, "plan.bin");
            PlanFile.Write(plan with { FilePath = planFile }, p1.DistinctBy(r => r.Tag == 'B' ? Hex(r.Payload.AsSpan(0, 20)) : r.Key));
            Materialize(planFile, work);
            var gen = Read(Path.Combine(work, "scskiller_gen.db")).ToList();
            var blobs = gen.Where(r => r.Tag == 'B').GroupBy(r => Hex(r.Payload.AsSpan(0, 20))).ToDictionary(g => g.Key, g => g.First());
            var genTemplates = gen.Where(r => r.Tag is 'G' or 'C' or 'S').GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
            var made = gen.Where(r => r.Tag is 'G' or 'C' or 'S' or 'P').GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
            var uses = new Dictionary<string, (uint Space, uint Lo, uint Hi)[]>();
            (uint Space, uint Lo, uint Hi)[] UsesOf(string sha) => uses.TryGetValue(sha, out var u) ? u
                : uses[sha] = blobs.TryGetValue(sha, out var bl) ? [.. Samplers(bl.Payload[20..])] : [];

            // pipelines packed into batches, static i of one on tag state base + i: each static its own state, as the game's are
            var batchList = new List<List<(int Base, (uint Register, uint Space)[] Places)>> { new() };
            var inBatch = new List<List<Rec>> { new() };
            var used = 0;
            foreach (var (j, c) in cands.Index())
            {
                if (!made.TryGetValue(body[c.At].Key, out var r)) { outcome[j] = "unbuilt"; continue; }
                var stages = r.Tag == 'P' ? ParseItem(r.Payload).Stages : Parse(r).Stages;
                if (!stages.Values.SelectMany(UsesOf).Any(u => c.Places.Any(p => p.Space == u.Space && p.Register >= u.Lo && p.Register <= u.Hi)))
                {
                    outcome[j] = "noSamplerUse";
                    continue;
                }
                if (used + c.Places.Length > CryStatics.MaxStates || batchList[^1].Count >= BatchSize)
                {
                    batchList.Add([]);
                    inBatch.Add([]);
                    used = 0;
                }
                var b0 = used;
                at[j] = (batchList.Count - 1, batchList[^1].Count);
                var blob = CryRootSig.Static(c.Layout, (reg, space) => b0 + Array.IndexOf(c.Places, (reg, space)), out _);
                var sha = Hex(SHA1.HashData(blob));
                blobs[sha] = new Rec('B', [.. Convert.FromHexString(sha), .. blob]);
                batchList[^1].Add((b0, c.Places));
                inBatch[^1].Add(WithRootSignature(r, sha));
                used += c.Places.Length;
            }
            Counts["pass1.pipelines"] = inBatch.Sum(b => b.Count);
            Counts["pass1.processes"] = inBatch.Count(b => b.Count > 0);

            var rowsOf = new List<(string StageId, int Batch, int K, string Why)>();
            // a layout row's nearest game record: lengths, the first differing offset and 32 bytes of each from just before it
            string Near(CryStatics.Str r) => game[r.StageId].Select(g => (G: g.Shape, At: g.Shape.AsSpan().CommonPrefixLength(r.Shape))).MaxBy(x => x.At) is var (g, off) && g != null
                ? $" ours={r.Shape.Length} game={g.Length} at={off} ours@={Around(r.Shape, off)} game@={Around(g, off)}" : "";
            static string Around(byte[] b, int at) => Convert.ToHexStringLower(b.AsSpan(Math.Max(0, at - 8))[..Math.Min(32, b.Length - Math.Max(0, at - 8))]);
            var results = new (Dictionary<int, List<CryStatics.Str>> Got, Dictionary<int, string> Rejected)?[batchList.Count];
            var lanes = Enumerable.Range(0, Math.Max(1, Lanes)).Select(lane => Task.Run(async () =>
            {
                for (var b = lane; b < batchList.Count; b += Math.Max(1, Lanes))
                {
                    ct.ThrowIfCancellationRequested();
                    if (inBatch[b].Count == 0) continue;
                    var dir = Path.Combine(Dir, $"b{b}");
                    WriteBatch(dir, inBatch[b], blobs, genTemplates, batchList[b].Sum(x => x.Places.Length));
                    var rows = await Compile(dir, ct);
                    var bb = b;
                    lock (results)
                        results[b] = rows == null ? null
                            : CryStatics.Attribute(batchList[b], rows, game, Counts, (row, k, why) => rowsOf.Add((row.StageId, bb, k, Trace != null && why == "layout" ? why + Near(row) : why)));
                }
            }, ct)).ToArray();
            Task.WaitAll(lanes, ct);

            foreach (var (j, _) in cands.Index())
            {
                if (outcome[j] != null) continue;
                var (b, k) = at[j];
                if (results[b] is not { } res) outcome[j] = "noRun";
                else if (res.Rejected.TryGetValue(k, out var rejected)) outcome[j] = rejected;
                else if (res.Got.TryGetValue(k, out var recs)) own[j] = recs;
                else outcome[j] = "noRecord";
            }
            // its stages' game records match another pipeline: the game never made this one, so no key of it to lose
            var accepted = rowsOf.Where(r => r.Why == "accepted").Select(r => r.StageId).ToHashSet();
            var layoutStages = rowsOf.Where(r => r.Why.StartsWith("layout", StringComparison.Ordinal)).GroupBy(r => (r.Batch, r.K)).ToDictionary(g => g.Key, g => g.Select(r => r.StageId).ToList());
            foreach (var (j, _) in cands.Index())
                if (outcome[j] == "layout" && layoutStages.TryGetValue(at[j], out var stages) && stages.All(accepted.Contains)) outcome[j] = "otherPipeline";
            if (Trace != null)
            {
                var byPipe = cands.Index().Where(x => outcome[x.Index] is not ("unbuilt" or "noSamplerUse")).ToDictionary(x => at[x.Index], x => x.Index);
                foreach (var (sid, b, k, why) in rowsOf)
                    Trace(sid, (why != "accepted" ? why : byPipe.TryGetValue((b, k), out var j) ? Decided(j) : "?")
                        + (byPipe.TryGetValue((b, k), out var p) ? $" layout={cands[p].Layout.Id:x16} flags={cands[p].Layout.Flags}" : ""));
            }
        }
        finally
        {
            Pass1Time = clock.Elapsed;
            Counts["leftover"] = Sweep();
            try { Directory.Delete(Dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        // pass 2: each pipeline under its static root signature at the game's slots, or at default ones
        string Decided(int j) => outcome[j] ?? CryStatics.Slots(cands[j].Places, own[j]!).Why ?? "mapped";
        var common = new Dictionary<(ulong, uint, uint), Dictionary<int, int>>();   // (layout, place) -> game slot -> pipelines
        foreach (var (j, c) in cands.Index())
            foreach (var (slot, reg) in (own[j] ?? []).SelectMany(r => r.Statics).Distinct())
                foreach (var p in c.Places.Where(p => p.Register == reg))
                {
                    var m = common.TryGetValue((c.Layout.Id, p.Register, p.Space), out var x) ? x : common[(c.Layout.Id, p.Register, p.Space)] = [];
                    m[(int)slot] = m.GetValueOrDefault((int)slot) + 1;
                }
        var gameTop = common.Values.SelectMany(m => m.Keys).DefaultIfEmpty(-1).Max();
        // the game's whole cache, by register: a slot past its states is never one of them (measured: Hunt's span 0 to 11)
        var byRegister = game!.Values.SelectMany(l => l).SelectMany(r => r.Statics).GroupBy(x => x.Register)
            .ToDictionary(g => g.Key, g => g.GroupBy(x => (int)x.Slot).MaxBy(x => (x.Count(), -x.Key))!.Key);
        gameTop = Math.Max(gameTop, game.Values.SelectMany(l => l).SelectMany(r => r.Statics).Select(x => (int)x.Slot).DefaultIfEmpty(-1).Max());
        var outBody = new List<Rec>(body);
        var added = new HashSet<string>();
        var top = -1;
        foreach (var (j, c) in cands.Index())
        {
            var why = Decided(j);
            CryStatics.Count(Counts, why == "mapped" ? "mapped" : $"fallback.{why}");
            // the game's records contradict the static shape, or nothing was compiled: the plan's stays
            if (why is "layout" or "places" or "unbuilt" or "noRun") continue;
            Dictionary<uint, int> slots;
            if (why == "mapped") slots = CryStatics.Slots(c.Places, own[j]!).Slots!;
            else
            {
                if (c.Places.Select(p => p.Register).Distinct().Count() != c.Places.Length) continue;
                // a register its own records give one slot keeps it, else the layout's most common, else a state past the game's
                var mine = (own[j] ?? []).SelectMany(r => r.Statics).GroupBy(x => x.Register).Where(g => g.Select(x => x.Slot).Distinct().Count() == 1)
                    .ToDictionary(g => g.Key, g => (int)g.First().Slot);
                slots = [];
                var free = gameTop + 1;
                foreach (var p in c.Places)
                    slots[p.Register] = mine.TryGetValue(p.Register, out var s) ? s
                        : common.TryGetValue((c.Layout.Id, p.Register, p.Space), out var m) ? m.MaxBy(x => (x.Value, -x.Key)).Key
                        : byRegister.TryGetValue(p.Register, out var regSlot) ? regSlot
                        : free++;
                if (slots.Values.Any(v => v >= CryStatics.MaxStates)) continue;
                CryStatics.Count(Counts, "defaultSlots");
            }
            var blob = CryRootSig.Static(c.Layout, (reg, _) => slots[reg], out _);
            var sha = Hex(SHA1.HashData(blob));
            if (added.Add(sha)) outBody.Add(new Rec('B', [.. Convert.FromHexString(sha), .. blob]));
            outBody[c.At] = WithRootSignature(body[c.At], sha);
            top = Math.Max(top, slots.Values.Max());
        }
        return top < 0 ? null : (outBody, CryStatics.Holds(top + 1));
    }

    static void WriteBatch(string dir, List<Rec> items, IReadOnlyDictionary<string, Rec> blobs, IReadOnlyDictionary<string, Rec> templates, int tags)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "scskiller.db"), []);
        var tpl = items.Where(r => r.Tag == 'P').Select(r => ParseItem(r.Payload).Template).Distinct().Where(templates.ContainsKey).Select(t => templates[t]).ToList();
        // blobs first, then templates, then items: the proxy resolves items against templates as it loads
        var need = Rehydrate.References([.. tpl, .. items]);
        using (var f = new BufferedStream(File.Create(Path.Combine(dir, "scskiller_gen.db")), 1 << 20))
        {
            foreach (var h in need.Where(blobs.ContainsKey)) Write(f, 'B', blobs[h].Payload);
            foreach (var r in tpl.Concat(items)) Write(f, r.Tag, r.Payload);
        }
        File.WriteAllBytes(Path.Combine(dir, CryStatics.HoldFile), CryStatics.HoldBytes(CryStatics.Holds(tags)));
    }

    /// <summary>One throwaway process over a batch folder: its 0x100 records in creation order, null when none could be read.
    /// What its exe name made is deleted after the read.</summary>
    async Task<List<CryStatics.Str>?> Compile(string dir, CancellationToken ct)
    {
        var name = $"scsk{Guid.NewGuid():N}"[..20] + ".exe";
        lock (_names) _names.Add(name);
        try { await Warm(name, dir); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log?.Report($"pass 1 process failed: {e.Message}");
        }
        ct.ThrowIfCancellationRequested();
        List<CryStatics.Str>? rows = null;
        foreach (var d in Made(name))
            foreach (var db in Directory.EnumerateFiles(d, "*.dxcache"))
                if (CryStatics.ReadDb(db) is { } r) (rows ??= []).AddRange(r);
        lock (_names) ThrowawayBytes += Delete(name);
        return rows;
    }

    /// <summary>The D3DSCache folders made since pass 1 began whose dbs name only exes of file name <paramref name="name"/>.</summary>
    List<string> Made(string name)
    {
        var o = new List<string>();
        if (!Directory.Exists(D3DSCacheRoot)) return o;
        foreach (var d in Directory.EnumerateDirectories(D3DSCacheRoot).Where(d => !_folders.Contains(d)))
            try
            {
                if (D3DSCache.ExePaths(d) is { Count: > 0 } p && p.All(x => string.Equals(Path.GetFileName(x), name, StringComparison.OrdinalIgnoreCase))) o.Add(d);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return o;
    }

    /// <summary>The .nvph files made since pass 1 began whose header names <paramref name="name"/> (file name at offset 0x11).</summary>
    List<string> NvphOf(string name)
    {
        var o = new List<string>();
        if (!Directory.Exists(NvCache)) return o;
        Span<byte> h = stackalloc byte[0x11 + 64];
        foreach (var f in Directory.EnumerateFiles(NvCache, "*.nvph").Where(f => !_nvph.Contains(f)))
            try
            {
                using var s = File.OpenRead(f);
                var n = s.ReadAtLeast(h, h.Length, false);
                if (n <= 0x11) continue;
                var at = h[0x11..n];
                var end = at.IndexOf((byte)0);
                if (end > 0 && string.Equals(System.Text.Encoding.Latin1.GetString(at[..end]), name, StringComparison.OrdinalIgnoreCase)) o.Add(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return o;
    }

    /// <summary>Deletes what exe name <paramref name="name"/> made; its bytes.</summary>
    long Delete(string name)
    {
        long bytes = 0;
        foreach (var d in Made(name))
            try
            {
                bytes += new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                Directory.Delete(d, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"pass 1: {d} can't be deleted yet: {e.Message}"); }
        foreach (var f in NvphOf(name))
            try
            {
                bytes += new FileInfo(f).Length;
                File.Delete(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"pass 1: {f} can't be deleted yet: {e.Message}"); }
        return bytes;
    }

    /// <summary>After pass 1: deletes anything its names still have; how many folders and files were left.</summary>
    int Sweep()
    {
        var left = 0;
        foreach (var n in _names)
        {
            left += Made(n).Count + NvphOf(n).Count;
            ThrowawayBytes += Delete(n);
        }
        return left;
    }
}
