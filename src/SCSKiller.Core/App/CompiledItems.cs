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

    /// <summary>The items of a warm's work dbs it created: all but those a <see cref="Recordings.FailedFile"/> names.</summary>
    public static HashSet<string> Created(IReadOnlyList<string> dbs, IEnumerable<string> failed)
    {
        var notCreated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in failed.Where(System.IO.File.Exists))
            foreach (var k in System.IO.File.ReadAllBytes(f).Chunk(20).Where(c => c.Length == 20)) notCreated.Add(Convert.ToHexStringLower(k));
        var existing = dbs.Where(System.IO.File.Exists).ToList();
        var nv = LastNv(existing.SelectMany(db => Read(db).Where(r => r.Tag == 'N')));
        return existing.SelectMany(db => Read(db).Where(r => r.Tag != 'B'))
            .Where(r => IsItem(r.Tag) && !notCreated.Contains(r.Key)).Select(r => nv.GetValueOrDefault(r.Key, r.Key)).ToHashSet();
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
    /// plan item's template. Everything else stays. Returns how many items were left out.</summary>
    public static long LeaveOut(IReadOnlyList<string> dbs, IReadOnlySet<string> compiled)
    {
        var existing = dbs.Where(System.IO.File.Exists).ToList();
        var items = new List<(string Key, bool Compiled, List<string> Needs)>();
        var nv = LastNv(existing.SelectMany(db => Read(db).Where(r => r.Tag == 'N')));
        foreach (var r in existing.SelectMany(db => Read(db)))
            if (IsItem(r.Tag) && Needs(r) is var needs)
                items.Add((r.Key, needs != null && compiled.Contains(nv.GetValueOrDefault(r.Key, r.Key)), needs ?? []));
        // what an item needs comes before it in the files: one pass from the end
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var drop = new HashSet<string>(StringComparer.Ordinal);
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i].Compiled && !needed.Contains(items[i].Key)) drop.Add(items[i].Key);
            else needed.UnionWith(items[i].Needs);
        if (drop.Count == 0) return 0;
        foreach (var db in existing)
        {
            var tmp = db + ".tmp";
            try
            {
                using (var f = new BufferedStream(System.IO.File.Create(tmp), 1 << 20))
                    foreach (var r in Read(db))
                        if (!IsItem(r.Tag) || !drop.Contains(r.Key)) Write(f, r.Tag, r.Payload);
                System.IO.File.Move(tmp, db, true);
            }
            finally { System.IO.File.Delete(tmp); }
        }
        return items.Count(x => drop.Contains(x.Key));
    }

    /// <summary>The records an item is created on; null when it doesn't parse (it is then never left out).</summary>
    static List<string>? Needs(Rec r)
    {
        try { return r.Tag == 'P' ? [ParseItem(r.Payload).Template] : IsStateObject(r.Tag) ? ParseStateObject(r).Depends : []; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException) { return null; }
    }
}
