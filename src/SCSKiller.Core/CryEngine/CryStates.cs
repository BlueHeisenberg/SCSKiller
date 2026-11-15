using System.Buffers.Binary;
using System.Text;
using static SCSKiller.Core.Planning.PsoDb;
using SCSKiller.Core.Swarm;

namespace SCSKiller.Core.CryEngine;

/// <summary>A render pass of RenderPassBinCache.txt: its render-target formats (slots 0-3, trailing empty slots dropped, 0 =
/// unbound) and its depth-stencil format (0 = none), all DXGI.</summary>
public sealed record CryPass(uint[] Rt, uint Dsv);

/// <summary>A graphics pipeline record's state references: its effect, layout id, vertex format id (null: none named), render
/// pass id and D3D topology.</summary>
public sealed record CryPso(string Effect, ulong Layout, ulong? VertexFormat, ulong RenderPass, uint Topology);

/// <summary>The pipeline state CRYENGINE's D3D12 bin caches ship beside the layouts (engine\shadercache.pak,
/// shaders/cache/d3d12/, the record framing of <see cref="CryLayouts"/>), which AMD's per-stage cache keys a stage on:
/// input layouts, render-target formats and topology.
///   - VertexFormatBinCache.txt: 1327 the element count, then per element 188B semantic index, 3A4E slot, EB47 offset,
///     EFDE class (1 per instance), 3653 DXGI format, 0E02, and 7138 the semantic name closing it.
///   - RenderPassBinCache.txt: 6F7F (4), 3EFE 3E9F 38AD 394C render-target formats, 7ED5 the depth-stencil format.
///   - PipelineStateBinCache.txt graphics records: 602D effect, 6CDB layout, 1E3C vertex format, E3B5 render pass, 0E2B
///     topology; ids read little endian are the records' keys.
/// Measured on Hunt: Showdown 1896: 12 vertex formats, 36 render passes, 140,863 graphics records naming 17 vertex formats
/// (10 shipped; the other 7 are the engine's built-in formats, in no file) and 34 render passes.</summary>
public static class CryStates
{
    public const string VertexFormatEntry = "shaders/cache/d3d12/vertexformatbincache.txt";
    public const string RenderPassEntry = "shaders/cache/d3d12/renderpassbincache.txt";
    const string Pak = "engine/shadercache.pak";
    /// <summary>Bytes of a cache file read (measured: pipelines 48 MB, the others under 10 KB).</summary>
    const long MaxFile = 256L << 20;

    const ushort Count = 0x1327, SemanticIndex = 0x188B, Slot = 0x3A4E, Offset = 0xEB47, Class = 0xEFDE, Format = 0x3653, Step = 0x0E02, Semantic = 0x7138;
    static readonly ushort[] RtFields = [0x3EFE, 0x3E9F, 0x38AD, 0x394C];
    const ushort Dsv = 0x7ED5, Effect = 0x602D, Layout = 0x6CDB, VertexFormat = 0x1E3C, RenderPass = 0xE3B5, Topology = 0x0E2B;

    /// <summary>An install's vertex formats, render passes and graphics pipeline records; empty where a file is missing or
    /// doesn't read.</summary>
    public static (Dictionary<ulong, LayoutElem[]> Formats, Dictionary<ulong, CryPass> Passes, List<CryPso> Pipelines) Read(string installDir)
    {
        var path = Path.Combine(installDir, Pak);
        if (!File.Exists(path)) return ([], [], []);
        using var zip = SwarmReader.Open(path, out _);
        byte[] Entry(string name)
        {
            var e = zip?.Entries.FirstOrDefault(x => x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (e == null || e.Length > MaxFile) return [];
            var b = new byte[e.Length];
            try
            {
                using var s = e.Open();
                s.ReadExactly(b);
                return b;
            }
            catch (Exception x) when (x is InvalidDataException or IOException or NotSupportedException) { return []; }
        }
        return (VertexFormats(Entry(VertexFormatEntry)), RenderPasses(Entry(RenderPassEntry)), Pipelines(Entry(CryTwins.Entry)));
    }

    /// <summary>Every record that parses whole, by key; one with an unknown field or a count that doesn't match is left out.</summary>
    public static Dictionary<ulong, LayoutElem[]> VertexFormats(ReadOnlySpan<byte> file)
    {
        var o = new Dictionary<ulong, LayoutElem[]>();
        foreach (var (key, f) in Records(file))
        {
            if (f.Count == 0 || f[0].Id != Count || f[0].Value.Length != 8) continue;
            var want = BinaryPrimitives.ReadUInt64LittleEndian(f[0].Value);
            var elems = new List<LayoutElem>();
            var cur = new Dictionary<ushort, uint>();
            var ok = true;
            foreach (var (id, v) in f.Skip(1))
            {
                if (id == Semantic)
                {
                    if (cur.Count != 6) { ok = false; break; }
                    var cls = cur[Class];
                    elems.Add(new(Encoding.ASCII.GetString(v).TrimEnd('\0'), (int)cur[SemanticIndex], cur[Format], cur[Offset], cur[Slot], cls, cls == 1 ? cur[Step] : 0));
                    cur.Clear();
                }
                else if (id is SemanticIndex or Slot or Offset or Class or Format or Step && v.Length == 4 && cur.TryAdd(id, BinaryPrimitives.ReadUInt32LittleEndian(v))) { }
                else { ok = false; break; }
            }
            if (ok && cur.Count == 0 && (ulong)elems.Count == want) o.TryAdd(key, [.. elems]);
        }
        return o;
    }

    public static Dictionary<ulong, CryPass> RenderPasses(ReadOnlySpan<byte> file)
    {
        var o = new Dictionary<ulong, CryPass>();
        foreach (var (key, f) in Records(file))
        {
            if (f.Any(x => x.Value.Length != 4) || f.Select(x => x.Id).Distinct().Count() != f.Count) continue;
            var d = f.ToDictionary(x => x.Id, x => BinaryPrimitives.ReadUInt32LittleEndian(x.Value));
            if (!RtFields.All(d.ContainsKey) || !d.TryGetValue(Dsv, out var dsv)) continue;
            var rt = RtFields.Select(x => d[x]).ToList();
            while (rt.Count > 0 && rt[^1] == 0) rt.RemoveAt(rt.Count - 1);
            o.TryAdd(key, new([.. rt], dsv));
        }
        return o;
    }

    /// <summary>The graphics records naming one effect, one layout and one render pass; compute records and the rest are left out.</summary>
    public static List<CryPso> Pipelines(ReadOnlySpan<byte> file)
    {
        var o = new List<CryPso>();
        foreach (var (_, f) in Records(file))
        {
            ulong? One(ushort id) => f.Where(x => x.Id == id).ToList() is [{ Value.Length: 8 } x] ? BinaryPrimitives.ReadUInt64LittleEndian(x.Value) : null;
            if (One(Layout) is not { } layout || One(RenderPass) is not { } pass || f.Where(x => x.Id == Effect).ToList() is not [var effect]) continue;
            var vf = f.Count(x => x.Id == VertexFormat) == 1 ? One(VertexFormat) : null;
            var topo = f.Where(x => x.Id == Topology).ToList() is [{ Value.Length: 2 } t] ? BinaryPrimitives.ReadUInt16LittleEndian(t.Value) : 0u;
            o.Add(new(Encoding.ASCII.GetString(effect.Value).TrimEnd('\0').ToLowerInvariant(), layout, vf, pass, topo));
        }
        return o;
    }

    /// <summary>D3D_PRIMITIVE_TOPOLOGY to its D3D12 topology type: 1 point, 2 line, 3 triangle, 4 patch, 0 unknown.</summary>
    public static uint TopologyType(uint topology) => topology switch { 1 => 1, 2 or 3 or 10 or 11 => 2, 4 or 5 or 12 or 13 => 3, >= 33 and <= 64 => 4, _ => 0 };

    static IEnumerable<(ulong Key, List<(ushort Id, byte[] Value)> Fields)> Records(ReadOnlySpan<byte> file)
    {
        var o = new List<(ulong, List<(ushort, byte[])>)>();
        var p = 0;
        while (CryLayouts.Header(file[p..], out var key, out var len, out var head) && p + head + len <= file.Length)
        {
            if (CryTwins.Top(file.Slice(p + head, len)) is { } f) o.Add((key, f));
            p += head + len;
            if (p < file.Length && file[p] == '\n') p++;
        }
        return o;
    }
}
