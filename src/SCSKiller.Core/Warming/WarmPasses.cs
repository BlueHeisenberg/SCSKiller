using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Warming;

/// <summary>AMD's careful warm (ARCHITECTURE.md): a work folder's recorded PSOs (scskiller.db: what the game itself
/// creates) split into passes, each created by a scskiller_warm process of its own (<c>--pass</c>) on few threads, so that
/// no two PSOs with the same shader set (the same stage hashes) compile in one process; everything else (the plan's items)
/// in one fast pass on the usual threads. <see cref="FileName"/> holds one byte per item, its pass, in the proxy's item
/// order: the PSO and state object records of scskiller.db then scskiller_gen.db (each key once), the plan items, the
/// D3D11 items. The k-th recorded item of a shader set goes to pass k + 1 (modulo <see cref="MaxPasses"/>; items without a
/// set, state objects, to pass 1); the rest to <see cref="FastPass"/>. Passes run in ascending number.</summary>
public sealed class WarmPasses
{
    public const string FileName = "scskiller_pass.bin";
    /// <summary>A bound on careful processes per warm: a set with more siblings than this shares passes (round robin).</summary>
    public const int MaxPasses = 32;
    /// <summary>The fast pass's number: it runs after the careful passes.</summary>
    public const byte FastPass = 255;

    readonly byte[] _numbers;   // per pass in run order, its number
    readonly long[][] _items;   // per pass, its items' indices, ascending
    readonly long[] _before;    // per pass, the items of the passes before it

    WarmPasses(byte[] passOf)
    {
        _numbers = passOf.Distinct().Order().ToArray();
        var at = new int[256];
        for (var k = 0; k < _numbers.Length; k++) at[_numbers[k]] = k;
        var lists = _numbers.Select(_ => new List<long>()).ToArray();
        for (long i = 0; i < passOf.Length; i++) lists[at[passOf[i]]].Add(i);
        _items = lists.Select(l => l.ToArray()).ToArray();
        _before = new long[_numbers.Length];
        for (var k = 1; k < _numbers.Length; k++) _before[k] = _before[k - 1] + _items[k - 1].Length;
        Total = passOf.Length;
    }

    /// <summary>Passes, in run order (the fast one included).</summary>
    public int Count => _items.Length;
    public long Total { get; }
    public IReadOnlyList<long> ItemsOf(int pass) => _items[pass];
    /// <summary>The pass's number in the pass file (scskiller_warm's --pass).</summary>
    public byte Number(int pass) => _numbers[pass];
    public bool IsFast(int pass) => _numbers[pass] == FastPass;
    public int CarefulCount => _numbers.Count(n => n != FastPass);

    /// <summary>Each item's pass: for the first <paramref name="recorded"/> items 1 + the number of earlier items with its set
    /// (null = no set) modulo <see cref="MaxPasses"/>; <see cref="FastPass"/> for the rest. Items keep their order
    /// within a pass.</summary>
    public static byte[] Split(IReadOnlyList<string?> sets, long recorded)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var pass = new byte[sets.Count];
        for (var i = 0; i < sets.Count; i++)
        {
            if (i >= recorded) { pass[i] = FastPass; continue; }
            var n = sets[i] is { } s ? seen.GetValueOrDefault(s) : 0;
            if (sets[i] is { } set) seen[set] = n + 1;
            pass[i] = (byte)(1 + n % MaxPasses);
        }
        return pass;
    }

    /// <summary>Writes the work folder's pass file when it has recorded items; returns the careful passes (0 = none, no
    /// file: the warm runs as usual).</summary>
    public static int Write(string workDir)
    {
        var f = Path.Combine(workDir, FileName);
        File.Delete(f);
        var (sets, recorded) = ItemSets(workDir);
        if (recorded == 0) return 0;
        var pass = Split(sets, recorded);
        File.WriteAllBytes(f, pass);
        return pass.Where(p => p != FastPass).Distinct().Count();
    }

    /// <summary>The work folder's passes; null without a pass file or with no items.</summary>
    public static WarmPasses? Read(string workDir)
    {
        var f = Path.Combine(workDir, FileName);
        return File.Exists(f) && File.ReadAllBytes(f) is { Length: > 0 } b ? new WarmPasses(b) : null;
    }

    /// <summary>A pass's progress (scskiller_warm's done: items below it replayed or of another pass) as the whole warm's:
    /// the items of the passes before it plus its own below <paramref name="done"/>.</summary>
    public long Overall(int pass, long done)
    {
        var i = Array.BinarySearch(_items[pass], done);
        return _before[pass] + (i >= 0 ? i : ~i);
    }

    /// <summary>Where a warm stopped at <paramref name="overall"/> (<see cref="Overall"/>) goes on: its pass and the
    /// scskiller_warm --start in it.</summary>
    public (int Pass, long Start) Locate(long overall)
    {
        for (var k = 0; k < Count; k++)
            if (overall < _before[k] + _items[k].Length) return (k, _items[k][overall - _before[k]]);
        return (Count - 1, Total);
    }
    /// <summary>Each item's shader set in the proxy's order (see the class summary): "stage:sha1,..." of a PSO record, of a
    /// plan item's template with the item's stages over it; null for state objects, D3D11 items and records that don't
    /// parse. Recorded: how many come first from scskiller.db.</summary>
    public static (List<string?> Sets, long Recorded) ItemSets(string workDir)
    {
        var recs = new List<(char Tag, byte[] Payload)>();
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        var plans = new List<byte[]>();
        long items11 = 0, recorded = 0;
        foreach (var db in new[] { "scskiller.db", "scskiller_gen.db" })
        {
            if (db == "scskiller_gen.db") recorded = recs.Count;
            foreach (var (tag, payload) in Records(Path.Combine(workDir, db)))
                switch (tag)
                {
                    case 'B' or 'N' or 'W' or 'D': break;
                    case '1' or '2' or '3': items11++; break;
                    case 'P': plans.Add(payload!); break;
                    default:
                        if (keys.TryAdd(Key(tag, payload!), recs.Count)) recs.Add((tag, payload!));
                        break;
                }
        }
        var sets = new List<string?>(recs.Count + plans.Count + (int)items11);
        foreach (var (tag, payload) in recs) sets.Add(PsoDb.IsStateObject(tag) ? null : SetOf(Stages(tag, payload)));
        foreach (var p in plans)
        {
            SortedDictionary<int, string>? st;
            try
            {
                var (template, _, over) = PsoDb.ParseItem(p);
                st = keys.TryGetValue(template, out var t) ? Stages(recs[t].Tag, recs[t].Payload) ?? [] : [];
                foreach (var (s, h) in over) if (h != PsoDb.Zero) st[s] = h;
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { st = null; }
            sets.Add(SetOf(st));
        }
        for (long i = 0; i < items11; i++) sets.Add(null);
        return (sets, recorded);
    }

    /// <summary>The record keys of <paramref name="items"/> (scskiller_warm's item numbers) that are D3D12 items of the work
    /// folder, as the warm writes them to <see cref="App.Recordings.FailedFile"/>.</summary>
    public static List<string> KeysOf(string workDir, IEnumerable<long> items)
    {
        var want = items.ToHashSet();
        var recs = new HashSet<string>(StringComparer.Ordinal);
        var plans = new List<string>();
        var keys = new List<string>();
        foreach (var db in new[] { "scskiller.db", "scskiller_gen.db" })
            foreach (var (tag, payload) in Records(Path.Combine(workDir, db)))
            {
                if (tag is 'B' or 'N' or 'W' or 'D' or '1' or '2' or '3') continue;
                var key = Key(tag, payload!);
                if (tag == 'P') plans.Add(key);
                else if (recs.Add(key) && want.Contains(recs.Count - 1)) keys.Add(key);
            }
        keys.AddRange(plans.Where((_, i) => want.Contains(recs.Count + i)));
        return keys;
    }

    static SortedDictionary<int, string>? Stages(char tag, byte[] payload)
    {
        try { return new SortedDictionary<int, string>(PsoDb.Parse(new PsoDb.Rec(tag, payload)).Stages); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException or OverflowException) { return null; }
    }

    static string? SetOf(SortedDictionary<int, string>? stages) => stages is { Count: > 0 } s ? string.Join(',', s.Select(x => $"{x.Key}:{x.Value}")) : null;

    static string Key(char tag, byte[] payload) => PsoDb.Hex(SHA1.HashData([(byte)tag, .. payload]));

    /// <summary>A db's records, 'B' blobs skipped unread (payload null); stops at a torn tail like the proxy.</summary>
    static IEnumerable<(char Tag, byte[]? Payload)> Records(string path)
    {
        if (!File.Exists(path)) yield break;
        using var f = new BufferedStream(File.OpenRead(path), 1 << 20);
        var head = new byte[5];
        while (f.ReadAtLeast(head, 5, false) == 5 && head[0] != 0)   // no record has tag 0: a zero-filled tail after a power loss
        {
            var len = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
            if (head[0] == 'B')
            {
                if (f.Position + len > f.Length) yield break;
                f.Seek(len, SeekOrigin.Current);
                yield return ('B', null);
                continue;
            }
            var body = new byte[len];
            if (f.ReadAtLeast(body, body.Length, false) < body.Length) yield break;
            yield return ((char)head[0], body);
        }
    }
}
