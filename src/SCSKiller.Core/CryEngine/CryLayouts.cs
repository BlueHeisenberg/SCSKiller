using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.CryEngine;

/// <summary>One resource of a layout slot: CRYENGINE's SResourceBindPoint and the type of its SResourceBinding.
/// <paramref name="Stages"/>: stage bits (1 VS, 2 PS, 4 GS, 8 DS, 16 HS, 32 CS, higher bits other stages);
/// <paramref name="Type"/>: 0 CBV (b), 1 SRV (t), 2 sampler (s), 3 UAV (u); <paramref name="Flags"/>: the bind point's flags
/// byte; <paramref name="Resource"/>: the binding's 8 bytes as a little-endian number (low byte the resource type, 9 = root
/// constants with their count in byte 4).</summary>
public sealed record CryBinding(ushort Stages, byte Register, byte Type, byte Flags, ulong Resource);

/// <summary>A layout slot: <paramref name="Index"/> its root parameter index; <paramref name="Kind"/> 0 an inline CBV, 1 an
/// inline SRV, 2 a resource set (descriptor tables), 3 root constants.</summary>
public sealed record CrySlot(byte Index, byte Kind, CryBinding[] Bindings);

/// <summary>A resource layout (SDeviceResourceLayoutDesc). <paramref name="Id"/>: the cache record's key, which a pipeline
/// record names by its 8 bytes read little endian. <paramref name="Flags"/>: its field 6AAA (0 when absent).</summary>
public sealed record CryLayout(ulong Id, CrySlot[] Slots, uint Flags = 0)
{
    /// <summary>Flag 4: the renderer gives the layout's set samplers a table, never statics (CryRenderD3D12, static read).</summary>
    public bool SamplerTables => (Flags & 4) != 0;
}

/// <summary>CRYENGINE's D3D12 ResourceLayoutBinCache.txt (measured: Hunt: Showdown 1896, engine\shadercache.pak,
/// shaders/cache/d3d12/). Records: ASCII "vvvv &lt;16 hex key&gt; tttt &lt;4 hex length&gt;|", the body, an optional newline.
/// A body is fields, each a u16 id, a u8 length (0xFE: a u16 length follows) and the value, with an object marker
/// 7FC1A4B1 among them. Lists are a u8 count then fields. A layout: 2F3C, the slot list; each slot D12E {5DA3 kind,
/// 3841 root index} and 6877, its binding list; each binding D12E {8785 stages, CDCF register, 5DA3 type, CBFC, F95D
/// flags} and 6877 {C532 resource}.</summary>
public static class CryLayouts
{
    public const string Entry = "shaders/cache/d3d12/resourcelayoutbincache.txt";
    const string Pak = "engine/shadercache.pak";
    internal static readonly byte[] Marker = [0x7F, 0xC1, 0xA4, 0xB1];
    /// <summary>Bytes of the cache file read (measured 188 KB).</summary>
    const long MaxFile = 64L << 20;

    /// <summary>The shipped layouts of the install by id; empty when the pak or the cache file isn't there or doesn't read.</summary>
    public static IReadOnlyDictionary<ulong, CryLayout> Read(string installDir)
    {
        var path = GameFiles.Find(installDir, Pak);
        if (!File.Exists(path)) return new Dictionary<ulong, CryLayout>();
        using var zip = CryEngineReader.OpenPak(installDir, path);   // the reader's guarded open: no link, anti-cheat folder or FIFO
        var e = zip?.Entries.FirstOrDefault(x => x.FullName.Equals(Entry, StringComparison.OrdinalIgnoreCase));
        if (e == null || e.Length > MaxFile) return new Dictionary<ulong, CryLayout>();
        var b = new byte[e.Length];
        try
        {
            using var s = e.Open();
            s.ReadExactly(b);
        }
        catch (Exception x) when (x is InvalidDataException or IOException or NotSupportedException) { return new Dictionary<ulong, CryLayout>(); }
        return Parse(b);
    }

    /// <summary>Every record that parses whole. A record that doesn't is left out, with its key and why added to
    /// <paramref name="rejected"/>. The walk stops where the record framing breaks.</summary>
    public static Dictionary<ulong, CryLayout> Parse(ReadOnlySpan<byte> file, List<string>? rejected = null)
    {
        var o = new Dictionary<ulong, CryLayout>();
        var p = 0;
        while (Header(file[p..], out var key, out var len, out var head) && p + head + len <= file.Length)
        {
            try
            {
                if (!o.TryAdd(key, Layout(key, file.Slice(p + head, len)))) rejected?.Add($"{key:x16}: key twice");
            }
            catch (InvalidDataException e) { rejected?.Add($"{key:x16}: {e.Message}"); }
            p += head + len;
            if (p < file.Length && file[p] == '\n') p++;
        }
        return o;
    }

    public const string PipelineEntry = "shaders/cache/d3d12/pipelinestatebincache.txt";
    /// <summary>Bytes of the pipeline cache file read (measured 48 MB).</summary>
    const long MaxPipelineFile = 256L << 20;

    /// <summary>The layouts each effect's cached pipelines use, from PipelineStateBinCache.txt (same record framing; the object
    /// marker also sits between fields): per effect name (lowercase), each layout id with whether a compute pipeline uses it.
    /// A graphics record names its layout in DB6C, a compute record in 1097; the effect name is 2D60. Empty when the file
    /// isn't there or doesn't read. A record that doesn't name exactly one layout and one effect is left out, counted in
    /// <paramref name="rejected"/> by why.</summary>
    public static IReadOnlyDictionary<string, HashSet<(ulong Id, bool Compute)>> PipelineLayouts(string installDir, Dictionary<string, int>? rejected = null)
    {
        var o = new Dictionary<string, HashSet<(ulong, bool)>>(StringComparer.Ordinal);
        var path = GameFiles.Find(installDir, Pak);
        if (!File.Exists(path)) return o;
        using var zip = CryEngineReader.OpenPak(installDir, path);
        var e = zip?.Entries.FirstOrDefault(x => x.FullName.Equals(PipelineEntry, StringComparison.OrdinalIgnoreCase));
        if (e == null || e.Length > MaxPipelineFile) return o;
        var b = new byte[e.Length];
        try
        {
            using var s = e.Open();
            s.ReadExactly(b);
        }
        catch (Exception x) when (x is InvalidDataException or IOException or NotSupportedException) { return o; }
        return Pipelines(b, rejected);
    }

    internal static Dictionary<string, HashSet<(ulong Id, bool Compute)>> Pipelines(ReadOnlySpan<byte> file, Dictionary<string, int>? rejected = null)
    {
        var o = new Dictionary<string, HashSet<(ulong, bool)>>(StringComparer.Ordinal);
        void Reject(string why) { if (rejected != null) rejected[why] = rejected.GetValueOrDefault(why) + 1; }
        var p = 0;
        while (Header(file[p..], out _, out var len, out var head) && p + head + len <= file.Length)
        {
            try
            {
                var body = file.Slice(p + head, len);
                var at = body[..Math.Min(8, body.Length)].IndexOf(Marker);   // a u32 before the first marker (measured)
                var fields = Walk(body[Math.Max(at, 0)..], "pipeline", markers: true);
                var layouts = fields.Where(x => x.Id is 0x1097 or 0x6CDB).ToList();
                var names = fields.Where(x => x.Id == 0x602D).ToList();
                if (layouts.Count != 1) Reject(layouts.Count == 0 ? "no layout" : layouts.Select(x => x.Id).Distinct().Count() > 1 ? "both a compute and a graphics layout" : "its layout twice");
                else if (layouts[0].Value.Length != 8) Reject("a layout field not 8 bytes");
                else if (names.Count != 1) Reject(names.Count == 0 ? "no effect name" : "its effect name twice");
                else
                {
                    var effect = Encoding.ASCII.GetString(names[0].Value).TrimEnd('\0').ToLowerInvariant();
                    if (!o.TryGetValue(effect, out var set)) o[effect] = set = [];
                    set.Add((BinaryPrimitives.ReadUInt64LittleEndian(layouts[0].Value), layouts[0].Id == 0x1097));
                }
            }
            catch (InvalidDataException) { Reject("doesn't frame"); }
            p += head + len;
            if (p < file.Length && file[p] == '\n') p++;
        }
        if (p < file.Length) Reject("truncated");   // a record past the end or a broken header: the walk stops there
        return o;
    }

    internal static bool Header(ReadOnlySpan<byte> b, out ulong key, out int len, out int head)
    {
        (key, len, head) = (0, 0, 32);
        if (b.Length < head || b[4] != ' ' || b[21] != ' ' || b[26] != ' ' || b[31] != '|') return false;
        return ulong.TryParse(Encoding.ASCII.GetString(b[5..21]), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out key)
            && int.TryParse(Encoding.ASCII.GetString(b[27..31]), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out len);
    }

    static CryLayout Layout(ulong key, ReadOnlySpan<byte> body)
    {
        if (body.StartsWith(Marker)) body = body[4..];
        var o = new List<CrySlot>();
        var rf = Fields(body, "record");
        foreach (var sv in List(Get(rf, 0x3C2F), "slot list"))
        {
            var sf = Fields(sv, "slot");
            var h = Fields(Get(sf, 0x2ED1), "slot header");
            var bs = new List<CryBinding>();
            foreach (var bv in List(Get(sf, 0x7768), "binding list"))
            {
                var bf = Fields(bv, "binding");
                var d = Fields(Get(bf, 0x2ED1), "bind point");
                if (Int(d, 0xFCCB, 4) != 0) throw new InvalidDataException("bind point field cbfc isn't 0");
                bs.Add(new((ushort)Int(d, 0x8587, 2), (byte)Int(d, 0xCFCD, 1), (byte)Int(d, 0xA35D, 1), (byte)Int(d, 0x5DF9, 1),
                    (ulong)Int(Fields(Get(bf, 0x7768), "binding resource"), 0x32C5, 8)));
            }
            o.Add(new((byte)Int(h, 0x4138, 1), (byte)Int(h, 0xA35D, 1), [.. bs]));
        }
        return new CryLayout(key, [.. o], rf.ContainsKey(0x6AAA) ? (uint)Int(rf, 0x6AAA, 4) : 0);
    }

    /// <summary>The singleton fields filling <paramref name="b"/> exactly; an id seen twice is refused.</summary>
    static Dictionary<ushort, byte[]> Fields(ReadOnlySpan<byte> b, string what)
    {
        var o = new Dictionary<ushort, byte[]>();
        foreach (var (id, v) in Walk(b, what))
            if (!o.TryAdd(id, v)) throw new InvalidDataException($"{what} has field {id:x4} twice");
        return o;
    }

    static List<(ushort Id, byte[] Value)> Walk(ReadOnlySpan<byte> b, string what, bool markers = false)
    {
        var o = new List<(ushort, byte[])>();
        var p = 0;
        while (p < b.Length)
        {
            if (markers && b[p..].StartsWith(Marker))
            {
                p += 4;
                continue;
            }
            if (b.Length - p < 3) throw new InvalidDataException($"{what} ends inside a field");
            var id = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
            int n = b[p + 2];
            p += 3;
            if (n == 0xFE)
            {
                if (b.Length - p < 2) throw new InvalidDataException($"{what} ends inside a field length");
                n = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
                p += 2;
            }
            if (b.Length - p < n) throw new InvalidDataException($"{what} has a field past its end");
            o.Add((id, b.Slice(p, n).ToArray()));
            p += n;
        }
        return o;
    }

    /// <summary>A list's items: a u8 count, then as many fields.</summary>
    static List<byte[]> List(byte[] b, string what)
    {
        if (b.Length == 0) throw new InvalidDataException($"{what} has no count");
        var f = Walk(b.AsSpan(1), what);
        return f.Count == b[0] ? [.. f.Select(x => x.Value)] : throw new InvalidDataException($"{what} counts {b[0]} but holds {f.Count}");
    }

    static byte[] Get(Dictionary<ushort, byte[]> f, ushort id) => f.TryGetValue(id, out var v) ? v : throw new InvalidDataException($"no field {id:x4}");

    /// <summary>An unsigned field of exactly <paramref name="width"/> bytes.</summary>
    static long Int(Dictionary<ushort, byte[]> f, ushort id, int width)
    {
        var v = Get(f, id);
        if (v.Length != width) throw new InvalidDataException($"field {id:x4} is {v.Length} bytes, not {width}");
        Span<byte> w = stackalloc byte[8];
        w.Clear();
        v.CopyTo(w);
        return BinaryPrimitives.ReadInt64LittleEndian(w);
    }
}

/// <summary>The version 1.0 root signature CryRenderD3D12 creates for a resource layout, built as CDeviceResourceLayout_DX12::Init
/// (CRYENGINE 5) does with what Hunt's shaders show: each slot's root parameter at its index and in the register space of that
/// index (shaders bind space 0-6 to match). An inline CBV or SRV and root constants are register 0 of their space. A resource
/// set's bindings take registers 0, 1, ... in the set's order, all types counted together (the shaders' space-3 SRVs start
/// after its CBVs, a sampler-only set's last sampler s15 is the shaders' s13); a binding flagged 1 or 5 is an unbounded SRV
/// range (the shaders' bindless t0 in spaces 5 and 6). Its descriptors and its samplers each form a table of one-descriptor
/// ranges at offsets 0, 1, ... A parameter is visible to its one stage, or to all when it has several (a compute stage: all).
/// Flags: see <see cref="Flags"/>.</summary>
public static class CryRootSig
{
    /// <summary>The serialized root signature of a layout found in <paramref name="shipped"/>, else in <paramref name="rebuilt"/>,
    /// or null with why. <paramref name="samplersAfterSet"/>: see <see cref="Build"/>.</summary>
    public static (byte[]? Blob, string? Why) For(ulong id, IReadOnlyDictionary<ulong, CryLayout> shipped, IReadOnlyDictionary<ulong, CryLayout>? rebuilt = null,
        bool samplersAfterSet = false)
    {
        if (!shipped.TryGetValue(id, out var l) && rebuilt?.TryGetValue(id, out l) != true) return (null, $"layout {id:x16} is neither shipped nor rebuilt");
        try { return (RootSig.Serialize(Build(l!, samplersAfterSet), []), null); }
        catch (Exception e) when (e is RootSig.SerializeException or InvalidDataException) { return (null, $"layout {id:x16}: {e.Message}"); }
    }

    /// <summary>A resource set's samplers go into a sampler table of their own (the cache keeps no sampler state, so they can't
    /// be static as CRYENGINE 5's are): a set of samplers only has it at the set's index. Where a mixed set's goes is unproven:
    /// after the last slot, in slot order, or with <paramref name="samplersAfterSet"/> right after the set's own table, the
    /// later slots moving up one.</summary>
    public static RootSig.Desc Build(CryLayout l, bool samplersAfterSet = false) => Build(l, samplersAfterSet, null);

    /// <summary>The layout's root signature with every set's samplers static, as CRYENGINE 5 makes them (register = place in the
    /// set, space = the set's index, visibility = the binding's stages; a sampler-only set then has no root parameter), each of the sampler state
    /// <see cref="CryStatics.State"/>(<paramref name="state"/>(register, space)). <paramref name="places"/>: those statics'
    /// (register, space) in declaration order; empty when the layout has none.</summary>
    public static byte[] Static(CryLayout l, Func<uint, uint, int> state, out (uint Register, uint Space)[] places) =>
        Static(l, (reg, space, vis) => CryStatics.State(state(reg, space), reg, space, vis), out places);

    /// <summary><see cref="Static(CryLayout, Func{uint, uint, int}, out (uint, uint)[])"/> with each static's 52-byte
    /// D3D12_STATIC_SAMPLER_DESC from <paramref name="desc"/>(register, space, visibility).</summary>
    public static byte[] Static(CryLayout l, Func<uint, uint, uint, byte[]> desc, out (uint Register, uint Space)[] places)
    {
        var statics = new List<byte>();
        var at = new List<(uint, uint)>();
        var d = Build(l, false, (reg, space, stages) =>
        {
            at.Add((reg, space));
            statics.AddRange(desc(reg, space, Vis(stages)));
        });
        places = [.. at];
        return RootSig.Serialize(d, [.. statics]);
    }

    /// <summary>The sets' sampler places of the layout, in declaration order (<see cref="Static"/>'s).</summary>
    public static (uint Register, uint Space)[] StaticPlaces(CryLayout l)
    {
        if (l.SamplerTables) return [];
        var at = new List<(uint, uint)>();
        Build(l, false, (reg, space, _) => at.Add((reg, space)));
        return [.. at];
    }

    static RootSig.Desc Build(CryLayout l, bool samplersAfterSet, Action<uint, uint, int>? addStatic)
    {
        var rows = new List<uint[]>();
        var samplerTables = new List<uint[]>();
        foreach (var (i, s) in l.Slots.OrderBy(s => s.Index).Index())
        {
            if (s.Index != i) throw new InvalidDataException($"slot indices leave a gap or repeat at {s.Index}");
            if (s.Bindings.Length == 0) throw new InvalidDataException($"slot {s.Index} is empty");
            if (s.Kind != 2 && s.Bindings.Length != 1) throw new InvalidDataException($"inline slot {s.Index} has {s.Bindings.Length} bindings");
            if (!TypeSorted(s)) throw new InvalidDataException($"slot {s.Index} lists its bindings out of type order");
            var first = s.Bindings[0];
            if (s.Kind is 0 or 3 && first.Type != 0 || s.Kind == 1 && first.Type != 1) throw new InvalidDataException($"slot {s.Index} of kind {s.Kind} binds type {first.Type}");
            // every inline binding of the shipped caches carries flags 2
            if (s.Kind != 2 && first.Flags != 2) throw new InvalidDataException($"inline slot {s.Index} has binding flags {first.Flags}");
            var constants = (uint)(first.Resource >> 32) & 0xFF;
            if (s.Kind == 3 && ((first.Resource & 0xFF) != 9 || constants is 0 or > 63)) throw new InvalidDataException($"slot {s.Index}: root constants resource {first.Resource:x16}");
            switch (s.Kind)
            {
                case 0: rows.Add([2, Vis(first.Stages), 0, s.Index, 0]); break;
                case 1: rows.Add([3, Vis(first.Stages), 0, s.Index, 0]); break;
                case 3: rows.Add([1, Vis(first.Stages), 0, s.Index, constants]); break;
                case 2:
                    var regs = s.Bindings.Select((b, k) => (b, (uint)k)).ToArray();
                    var res = Table(regs.Where(x => x.b.Type != 2).ToArray(), s.Index);
                    var smp = Table(regs.Where(x => x.b.Type == 2).ToArray(), s.Index);
                    if (addStatic != null)
                    {
                        // a sampler-only set adds statics and no root parameter
                        if (res != null) rows.Add(res);
                        foreach (var (b, reg) in regs.Where(x => x.b.Type == 2)) addStatic(reg, s.Index, b.Stages);
                        break;
                    }
                    rows.Add(res ?? smp!);
                    if (res != null && smp != null) (samplersAfterSet ? rows : samplerTables).Add(smp);
                    break;
                default: throw new InvalidDataException($"slot {s.Index} is of an unknown kind {s.Kind}");
            }
        }
        return new RootSig.Desc(Flags(l), [.. rows, .. samplerTables], Version10: true, RangeOffsets: true);
    }

    /// <summary>D3D12_ROOT_SIGNATURE_FLAGS: ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT, DENY_AMPLIFICATION/MESH_SHADER_ROOT_ACCESS and the
    /// DENY_*_SHADER_ROOT_ACCESS of each graphics stage no binding names (a compute layout: all five). The driver's cache key
    /// covers these flags (measured: Hunt's keys on NVIDIA, 0x33F compute, 0x301 every stage bound).</summary>
    internal static uint Flags(CryLayout l)
    {
        var stages = l.Slots.SelectMany(s => s.Bindings).Aggregate(0, (m, b) => m | b.Stages);
        uint f = 0x1 | 0x100 | 0x200;
        foreach (var (bit, deny) in ((int, uint)[])[(1, 0x2), (16, 0x4), (8, 0x8), (4, 0x10), (2, 0x20)])
            if ((stages & bit) == 0) f |= deny;
        return f;
    }

    static uint[]? Table((CryBinding B, uint Register)[] bs, uint space)
    {
        if (bs.Length == 0) return null;
        var row = new List<uint> { 0, Vis(bs.Aggregate(0, (m, x) => m | x.B.Stages)) };
        foreach (var (k, (b, reg)) in bs.Index())
        {
            var count = b.Flags switch
            {
                0 => 1u,
                1 or 5 when b.Type == 1 => RootSig.Unbounded,
                _ => throw new InvalidDataException($"binding flags {b.Flags} on type {b.Type}"),
            };
            row.AddRange([b.Type switch { 0 => 2u, 1 => 0u, 2 => 3u, 3 => 1u, _ => throw new InvalidDataException($"binding type {b.Type}") }, count, reg, space, (uint)k]);
        }
        return [.. row];
    }

    /// <summary>A register is a binding's place in its set, which the shaders show as its place in the set sorted by type.</summary>
    internal static bool TypeSorted(CrySlot s) => s.Bindings.Zip(s.Bindings.Skip(1)).All(x => x.First.Type <= x.Second.Type);

    /// <summary>D3D12_SHADER_VISIBILITY: VS 1, PS 5, GS 4, DS 3, HS 2 for a single one of those stage bits, else ALL.</summary>
    static uint Vis(int stages) => stages switch { 1 => 1, 2 => 5, 4 => 4, 8 => 3, 16 => 2, _ => 0 };
}
