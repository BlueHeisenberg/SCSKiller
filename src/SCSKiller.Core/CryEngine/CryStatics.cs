using System.Buffers.Binary;

namespace SCSKiller.Core.CryEngine;

/// <summary>Hunt: Showdown's static samplers on NVIDIA. The driver keeps one device-wide table of distinct static sampler states,
/// in the order the process first creates them while the root signatures holding them live, and each static's place in it
/// reaches the D3DSCache key (the states' values don't). The game's own 0x100 records name that slot per stage and sampler
/// register. A pass of throwaway processes learns which driver stage each planned pipeline compiles to: every static of a
/// batch gets its own tag state, held first, so each record it leaves names its pipeline by slot. The compile then holds synthetic states
/// in the game's slot order and gives each mapped pipeline's statics the game's slots.</summary>
public static class CryStatics
{
    /// <summary>Distinct states <see cref="State"/> makes: every U/V/W combination of the five address modes under each of the
    /// eight point/linear filters.</summary>
    public const int MaxStates = 1000;

    static readonly uint[] Filters = [0x15, 0x00, 0x01, 0x04, 0x05, 0x10, 0x11, 0x14];

    /// <summary>D3D12_STATIC_SAMPLER_DESC (52 bytes): filter and address modes by <paramref name="k"/> (the driver folds states
    /// before it numbers them, anisotropic with 1 into linear, so none is anisotropic), LOD 0 to max, compare always.</summary>
    public static byte[] State(int k, uint register, uint space, uint visibility)
    {
        if (k is < 0 or >= MaxStates) throw new ArgumentOutOfRangeException(nameof(k));
        var a = k % 125;
        uint[] d = [Filters[k / 125], 1 + (uint)(a % 5), 1 + (uint)(a / 5 % 5), 1 + (uint)(a / 25), 0, 1, 8, 0, 0, BitConverter.SingleToUInt32Bits(float.MaxValue), register, space, visibility];
        var b = new byte[52];
        for (var i = 0; i < d.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), d[i]);
        return b;
    }

    /// <summary>A root signature holding the states 0 to <paramref name="count"/> - 1, one each, created in that order first.</summary>
    public static List<byte[]> Holds(int count) =>
        [.. Enumerable.Range(0, count).Select(k => Planning.RootSig.Serialize(new Planning.RootSig.Desc(0, [], Version10: true), State(k, 0, 0, 0)))];

    /// <summary>scskiller_hold.bin: u32 count, then per root signature u32 length and its bytes.</summary>
    public const string HoldFile = "scskiller_hold.bin";

    public static byte[] HoldBytes(IReadOnlyList<byte[]> blobs)
    {
        var o = new List<byte>(BitConverter.GetBytes((uint)blobs.Count));
        foreach (var b in blobs) o.AddRange([.. BitConverter.GetBytes((uint)b.Length), .. b]);
        return [.. o];
    }

    /// <summary>A 0x100 record: its stage id (16 bytes at 16 and 32, the driver's hash of the shader), its static samplers'
    /// (slot, register), and its bytes with every slot-derived field zeroed (what a record of the same stage and root signature
    /// shape holds whatever the slots).</summary>
    public sealed record Str(string StageId, (uint Slot, uint Register)[] Statics, byte[] Shape);

    /// <summary>Null when <paramref name="s"/> isn't a 0x100 record. 'ARY' records: the statics are entries (slot, 0xFE,
    /// ordinal, register); a sampler reference table (8-byte entries) gives a static's reference 0xFE in byte 1 and a value
    /// that moves with its slot in the second u32 (measured: slot minus a per-shader constant).</summary>
    public static Str? Read(byte[] s)
    {
        if (s.Length < 48 || !s.AsSpan(0, 4).SequenceEqual("STR\0"u8)) return null;
        var shape = (byte[])s.Clone();
        List<(uint, uint)>? statics = null;
        for (var p = s.AsSpan().IndexOf("ARY\0"u8); p >= 0 && p + 16 <= s.Length; p = Next(s, p))
        {
            var n = U(s, p + 4);
            if (p + (long)n > s.Length) continue;
            var (inner, cnt) = (U(s, p + 8), U(s, p + 12));
            if (cnt == 0 || n != 16 + (long)inner) continue;
            if (statics == null && inner == cnt * 16 && Enumerable.Range(0, (int)cnt).All(i => U(s, p + 16 + 16 * i + 4) == 0xFE))
            {
                statics = [];
                for (var i = 0; i < cnt; i++)
                {
                    var at = p + 16 + 16 * i;
                    statics.Add((U(s, at), U(s, at + 12)));
                    shape.AsSpan(at, 4).Clear();
                }
            }
            else if (inner == cnt * 8)
                for (var i = 0; i < cnt; i++)
                    if (s[p + 16 + 8 * i + 1] == 0xFE) shape.AsSpan(p + 20 + 8 * i, 4).Clear();
        }
        return new(Convert.ToHexStringLower([.. s.AsSpan(16, 8), .. s.AsSpan(32, 8)]), [.. statics ?? []], WithoutCbMap(s, shape));

        static int Next(byte[] s, int p) => s.AsSpan(p + 4).IndexOf("ARY\0"u8) is var q and >= 0 ? p + 4 + q : -1;
    }

    /// <summary>The shape with the CB-load map emptied and the size fields at 4 zeroed. The map is the 'ARY' right after the one
    /// with header (17, 1, 1): one entry, or none in the driver's asynchronous second variant and in a throwaway process's
    /// compute records (measured: NVIDIA). It holds no slots.</summary>
    static byte[] WithoutCbMap(byte[] s, byte[] shape)
    {
        shape.AsSpan(4, 8).Clear();
        for (var p = s.AsSpan().IndexOf("ARY\0"u8); p >= 0 && p + 32 <= s.Length; )
        {
            var q = s.AsSpan(p + 4).IndexOf("ARY\0"u8) is var d and >= 0 ? p + 4 + d : -1;
            if (q < 0 || q + 16 > s.Length) break;
            if (U(s, p + 4) == 17 && U(s, p + 8) == 1 && U(s, p + 12) == 1)
            {
                var n = (int)U(s, q + 4);
                if (n < 16 || q + (long)n > s.Length) break;
                byte[] empty = [.. "ARY\0"u8, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
                return [.. shape.AsSpan(0, q), .. empty, .. shape.AsSpan(q + n)];
            }
            p = q;
        }
        return shape;
    }

    /// <summary>The 0x100 records of a D3DSCache db in key order, read-only and only as a closed db (<see cref="App.D3DSCache.PostExit"/>);
    /// null when the db can't be read. A value that doesn't decompress is passed over.</summary>
    public static List<Str>? ReadDb(string db)
    {
        var o = new List<Str>();
        var ran = App.D3DSCache.PostExit(db, new App.D3DSCache.DbQuery(
            "select a.value, b.value from vals a join vals b on b.keyid = a.keyid and b.type = 0 where a.type = 1 order by a.keyid", r =>
            {
                var h = r.Blob(0);
                // header: kind u64 @0, compressed size @16, raw size @24
                if (h.Length < 32 || BinaryPrimitives.ReadUInt64LittleEndian(h) != 0x100) return;
                var raw = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(24));
                if (raw is 0 or > 64 << 20) return;
                var v = new byte[raw];
                try
                {
                    using var z = new ZstdSharp.Decompressor();
                    if ((ulong)z.Unwrap(r.Blob(1), v) != raw) return;
                }
                catch (ZstdSharp.ZstdException) { return; }
                if (Read(v) is { } s) o.Add(s);
            }));
        return ran is [true] ? o : null;
    }

    /// <summary>The game's records that use static samplers, by stage id.</summary>
    public static Dictionary<string, List<Str>> Index(IEnumerable<Str> records)
    {
        var o = new Dictionary<string, List<Str>>();
        foreach (var r in records.Where(r => r.Statics.Length > 0))
            (o.TryGetValue(r.StageId, out var l) ? l : o[r.StageId] = []).Add(r);
        return o;
    }

    /// <summary>A batch's records (one throwaway process, static i of pipeline k on tag state base_k + i) matched to the game's:
    /// per pipeline the game records of its stages. A record is taken only when its slots all name one pipeline of the batch
    /// and each static's register is that place's, and a game record of its stage holds the same bytes but the slots; else its
    /// pipeline is rejected, by why: "places", "sharedState" (the game's record gives two statics one slot: one state, which
    /// collapses the record), "layout" (another root signature shape). Records without statics name no pipeline.</summary>
    public static (Dictionary<int, List<Str>> Game, Dictionary<int, string> Rejected) Attribute(IReadOnlyList<(int Base, (uint Register, uint Space)[] Places)> batch,
        IEnumerable<Str> rows, IReadOnlyDictionary<string, List<Str>> game, IDictionary<string, int> counts, Action<Str, int, string>? each = null)
    {
        var of = new Dictionary<uint, (int K, int I)>();
        foreach (var (k, (b, places)) in batch.Index())
            for (var i = 0; i < places.Length; i++) of[(uint)(b + i)] = (k, i);
        var got = new Dictionary<int, List<Str>>();
        var rejected = new Dictionary<int, string>();
        foreach (var r in rows.Where(r => r.Statics.Length > 0))
        {
            var ks = r.Statics.Select(x => of.TryGetValue(x.Slot, out var t) ? t.K : -1).Distinct().ToArray();
            var k = ks is [>= 0 and var one] ? one : -1;
            var why = k < 0 ? "untagged"
                : !r.Statics.All(x => batch[k].Places[of[x.Slot].I].Register == x.Register) ? "places"
                : !game.TryGetValue(r.StageId, out var gs) ? "noRecord"
                : gs.Any(g => g.Shape.AsSpan().SequenceEqual(r.Shape)) ? "accepted"
                : gs.Any(g => g.Statics.Select(x => x.Slot).Distinct().Count() < g.Statics.Length) ? "sharedState" : "layout";
            Count(counts, $"rows.{why}");
            each?.Invoke(r, k, why);
            if (why is "places" or "sharedState" or "layout") rejected.TryAdd(k, why);
            if (why == "accepted") (got.TryGetValue(k, out var l) ? l : got[k] = []).AddRange(game[r.StageId].Where(g => g.Shape.AsSpan().SequenceEqual(r.Shape)));
        }
        return (got, rejected);
    }

    /// <summary>The game's slot for each of a pipeline's places, from its stages' game records, or why there is none: a register
    /// in two spaces ("ambiguous"), records disagreeing ("conflict"), a place no record names ("noSlot"), a slot past
    /// <see cref="MaxStates"/> ("slotRange").</summary>
    public static (Dictionary<uint, int>? Slots, string? Why) Slots((uint Register, uint Space)[] places, IEnumerable<Str> records)
    {
        if (places.Select(p => p.Register).Distinct().Count() != places.Length) return (null, "ambiguous");
        var m = new Dictionary<uint, int>();
        foreach (var (slot, reg) in records.SelectMany(r => r.Statics))
        {
            if (m.TryGetValue(reg, out var was) && was != slot) return (null, "conflict");
            m[reg] = (int)slot;
        }
        if (!places.All(p => m.ContainsKey(p.Register))) return (null, "noSlot");
        if (places.Any(p => m[p.Register] >= MaxStates)) return (null, "slotRange");
        return (places.ToDictionary(p => p.Register, p => m[p.Register]), null);
    }

    internal static void Count(IDictionary<string, int> counts, string what, int n = 1) => counts[what] = counts.TryGetValue(what, out var c) ? c + n : n;

    static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
}
