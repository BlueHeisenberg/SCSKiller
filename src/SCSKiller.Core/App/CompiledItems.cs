using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.App;

/// <summary>What complete warms created since the last full one (<see cref="File"/> in the game's folder), so that a
/// recompile on the same driver, exe and plan settings, its cache intact, replays only the rest (ScsKiller.FullReplayReason).
/// An item is a pipeline, a state object or a plan item ('G' 'C' 'S' 'R' 'A' 'P'), keyed by the key of its last 'N' record
/// when it has one (the NVAPI state is in NVIDIA's key; Planner.PlanInputs keys it so), else by its own. D3D11 items are
/// always replayed.</summary>
public static class CompiledItems
{
    public const string File = "compiled.keys";

    static ReadOnlySpan<byte> Magic => "SCSKCMP1"u8;

    static bool IsItem(char tag) => tag is 'G' or 'C' or 'S' or 'P' || IsStateObject(tag);

    /// <summary>Each record's key -> the key of its last 'N' across <paramref name="records"/>, in the order the proxy reads
    /// them (scskiller.db, then scskiller_gen.db: its g_nvext keeps the last).</summary>
    static Dictionary<string, string> LastNv(IEnumerable<Rec> records)
    {
        var nv = new Dictionary<string, string>();
        foreach (var r in records)
            if (r.Tag == 'N' && r.Payload.Length == NvState.Size) nv[NvState.Parse(r).Target] = r.Key;
        return nv;
    }

    /// <summary>The items of a warm's work dbs it created: all but those a <see cref="Recordings.FailedFile"/> names, and
    /// those before scskiller_warm's item <paramref name="from"/> (its --start: a resumed warm's last part) or from item
    /// <paramref name="to"/> on (a stopped warm's first unfinished one). <paramref name="psosOnly"/>: no state objects.</summary>
    public static HashSet<string> Created(IReadOnlyList<string> dbs, IEnumerable<string> failed, long from = 0, long to = long.MaxValue, bool psosOnly = false)
    {
        var notCreated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in failed.Where(System.IO.File.Exists))
            foreach (var k in System.IO.File.ReadAllBytes(f).Chunk(20).Where(c => c.Length == 20)) notCreated.Add(Convert.ToHexStringLower(k));
        var existing = dbs.Where(System.IO.File.Exists).ToList();
        var nv = LastNv(existing.SelectMany(db => Read(db).Where(r => r.Tag == 'N')));
        // the proxy's item numbers: each record key once in file order, then the plan items ('P'), then D3D11's; a db ends at
        // a zero tag for it (a zero-filled tail)
        var (recs, plans) = (new HashSet<string>(StringComparer.Ordinal), 0L);
        var items = new List<(string Key, long At, bool Plan)>();
        foreach (var r in existing.SelectMany(db => Read(db).TakeWhile(r => r.Tag != '\0').Where(r => r.Tag != 'B')))
            if (r.Tag is 'N' or 'W' or 'D' or '1' or '2' or '3') continue;
            else if (r.Tag == 'P') items.Add((r.Key, plans++, true));
            else if (recs.Add(r.Key) && IsItem(r.Tag) && !(psosOnly && IsStateObject(r.Tag))) items.Add((r.Key, recs.Count - 1, false));
        return items.Where(x => (x.Plan ? recs.Count + x.At : x.At) is var at && at >= from && at < to && !notCreated.Contains(x.Key))
            .Select(x => nv.GetValueOrDefault(x.Key, x.Key)).ToHashSet();
    }

    public static HashSet<string>? Load(string path)
    {
        try
        {
            var b = System.IO.File.ReadAllBytes(path);
            if (b.Length < Magic.Length || !b.AsSpan(0, Magic.Length).SequenceEqual(Magic) || (b.Length - Magic.Length) % 20 != 0) return null;
            return b[Magic.Length..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static byte[] Bytes(IEnumerable<string> keys)
    {
        using var m = new MemoryStream();
        m.Write(Magic);
        foreach (var k in keys.Order(StringComparer.Ordinal)) m.Write(Convert.FromHexString(k));
        return m.ToArray();
    }

    /// <summary>Rewrites a warm's work dbs (scskiller.db, then scskiller_gen.db) without the items <paramref name="compiled"/>
    /// holds, but those a kept item needs: the base and collections of a state object (the warm creates them first) and a
    /// plan item's template. Everything else stays. Returns how many items were left out. <paramref name="held"/> gets the
    /// keys of <paramref name="compiled"/> the dbs hold, left out or not. A compiled item whose record key <paramref name="again"/>
    /// holds stays, its key added to <paramref name="redone"/>.</summary>
    public static long LeaveOut(IReadOnlyList<string> dbs, IReadOnlySet<string> compiled, ISet<string>? held = null, IReadOnlySet<string>? again = null,
        ISet<string>? redone = null) =>
        LeaveOut(dbs, (r, key) =>
        {
            var done = compiled.Contains(key);
            if (done) held?.Add(key);
            if (done && again?.Contains(r.Key) == true) { redone?.Add(key); return false; }
            return done;
        }, d3d11: false);

    /// <summary>Rewrites a warm's work dbs to their ray tracing items (ScsKiller.CompileRtOnly): the state objects, the PSOs
    /// and plan items with a shader that traces rays inline (SFI0's RayQuery flag), and what they need. D3D11 items go too.
    /// Returns how many items were left out.</summary>
    public static long KeepRayTracing(IReadOnlyList<string> dbs)
    {
        var existing = dbs.Where(System.IO.File.Exists).ToList();
        var rayQuery = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in existing.SelectMany(db => Read(db).Where(r => r.Tag == 'B')))
            if (r.Payload.Length > 20 && Carved.Dxbc.InlineRayTracing(r.Payload.AsSpan(20))) rayQuery.Add(Hex(r.Payload.AsSpan(0, 20)));
        // a PSO's stages that trace rays inline: a plan item on it traces them unless it replaces those stages
        var traced = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var r in existing.SelectMany(db => Read(db).Where(r => r.Tag is 'G' or 'C' or 'S')))
            try { if (Parse(r).Stages.Where(s => rayQuery.Contains(s.Value)).Select(s => s.Key).ToList() is { Count: > 0 } st) traced[r.Key] = st; }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException) { }
        bool Traces(Rec r)
        {
            if (IsStateObject(r.Tag)) return true;
            if (r.Tag != 'P') return traced.ContainsKey(r.Key);
            try
            {
                var (template, _, stages) = ParseItem(r.Payload);
                return stages.Values.Any(rayQuery.Contains) || traced.TryGetValue(template, out var st) && st.Any(s => !stages.ContainsKey(s));
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException) { return true; }   // kept as LeaveOut keeps one it can't parse
        }
        return LeaveOut(dbs, (r, _) => !Traces(r), d3d11: true);
    }

    /// <summary>Rewrites the work dbs without the items <paramref name="drop"/> (the record, its key as <see cref="LastNv"/>
    /// maps it) picks, but those a kept item needs; with <paramref name="d3d11"/> without the D3D11 items too.</summary>
    static long LeaveOut(IReadOnlyList<string> dbs, Func<Rec, string, bool> drop, bool d3d11)
    {
        static bool IsD3D11(char tag) => tag is '1' or '2' or '3';
        var existing = dbs.Where(System.IO.File.Exists).ToList();
        var items = new List<(string Key, bool Compiled, List<string> Needs)>();
        var nv = LastNv(existing.SelectMany(db => Read(db).Where(r => r.Tag == 'N')));
        var anyD3D11 = false;
        foreach (var r in existing.SelectMany(db => Read(db)))
            if (IsItem(r.Tag) && Needs(r) is var needs)
            {
                var done = drop(r, nv.GetValueOrDefault(r.Key, r.Key));
                items.Add((r.Key, needs != null && done, needs ?? []));
            }
            else anyD3D11 |= d3d11 && IsD3D11(r.Tag);
        // what an item needs comes before it in the files: one pass from the end
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i].Compiled && !needed.Contains(items[i].Key)) dropped.Add(items[i].Key);
            else needed.UnionWith(items[i].Needs);
        if (dropped.Count == 0 && !anyD3D11) return 0;
        foreach (var db in existing)
        {
            var tmp = db + ".tmp";
            try
            {
                using (var f = new BufferedStream(System.IO.File.Create(tmp), 1 << 20))
                    foreach (var r in Read(db))
                        if (!(IsItem(r.Tag) && dropped.Contains(r.Key) || d3d11 && IsD3D11(r.Tag))) Write(f, r.Tag, r.Payload);
                System.IO.File.Move(tmp, db, true);
            }
            finally { System.IO.File.Delete(tmp); }
        }
        return items.Count(x => dropped.Contains(x.Key));
    }

    /// <summary>The records an item is created on; null when it doesn't parse (it is then never left out).</summary>
    static List<string>? Needs(Rec r)
    {
        try { return r.Tag == 'P' ? [ParseItem(r.Payload).Template] : IsStateObject(r.Tag) ? ParseStateObject(r).Depends : []; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException) { return null; }
    }
}
