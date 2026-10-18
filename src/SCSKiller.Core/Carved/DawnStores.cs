using System.Security.Cryptography;

namespace SCSKiller.Core.Carved;

/// <summary>Eidos-Montréal's Dawn engine (Marvel's Guardians of the Galaxy) ships its D3D12 pipelines in <c>bin\</c>: the
/// shaders in rawshader.store2, the root signatures (RTS0-only containers) in rawroot.store2, and one record per pipeline in
/// rawpso.store2 naming its root signature and shaders by their entry ids. Every .store2 file is [u64 a][u64 b], b bytes of
/// payload, then a again (a = b + 0x10007 in the game's files, not relied on). The payload starts with [u32 n], then:
///   - a store: n x ([u32 id][u32 size][u32 0] + the container);
///   - rawpso: n records of 680 bytes: +8 the payload size (664), +16 the root signature's id, +24 the VS's or CS's id, +40
///     the PS's (0: none), then the graphics state.
/// A file whose entries don't fill its payload exactly, or a record naming an id its store lacks: not this format
/// (<see cref="Read"/> is null).</summary>
public static class DawnStores
{
    const int Header = 16, Framing = Header + 8, Record = 680, RecordPayload = 0x298;
    const long MaxPsoFile = 256L << 20;   // the game's: 8.3 MB
    static readonly int[] OtherStages = [56, 72, 88, 104];   // where a GS, HS or DS id would sit: none in the game's records

    public sealed record Root(long Offset, int Size, string Sha, byte[] Blob);

    /// <param name="Shaders">entry id -> the container's offset in <paramref name="ShaderFile"/></param>
    /// <param name="Pipelines">each record's root signature id and shader ids (VS [+ PS], or CS)</param>
    /// <param name="Skipped">records naming a shader where a GS, HS or DS would be: their stages aren't known whole</param>
    public sealed record Stores(string ShaderFile, string RootFile, string PsoFile, IReadOnlyDictionary<uint, long> Shaders,
        IReadOnlyDictionary<uint, Root> Roots, IReadOnlyList<(uint Root, uint[] Stages)> Pipelines, int Skipped);

    public static Stores? Read(string installDir)
    {
        var bin = Path.Combine(installDir, "bin");
        string shaderFile = Path.Combine(bin, "rawshader.store2"), rootFile = Path.Combine(bin, "rawroot.store2"), psoFile = Path.Combine(bin, "rawpso.store2");
        if (!File.Exists(psoFile) || !File.Exists(rootFile) || !File.Exists(shaderFile)) return null;
        try
        {
            if (Entries(shaderFile) is not { } shaders || Entries(rootFile) is not { } rootAt) return null;
            var roots = new Dictionary<uint, Root>();
            using (var h = Open(rootFile))
                foreach (var (id, (off, size)) in rootAt)
                {
                    var c = new byte[size];
                    if (RandomAccess.Read(h, c, off) != size) return null;
                    var n = Dxbc.HeaderSize(c);
                    if (n == 0 || n > size) return null;
                    var container = c.AsSpan(0, n);
                    if (!Dxbc.Valid(container) || Dxbc.Kind(container) >= 0 || Dxbc.Part(container, "RTS0"u8).IsEmpty) return null;
                    roots[id] = new Root(off, n, Convert.ToHexStringLower(SHA1.HashData(container)), container.ToArray());
                }
            byte[] p;
            using (var h = Open(psoFile))
            {
                var len = RandomAccess.GetLength(h);
                if (len > MaxPsoFile || Frame(h, len) is not { } n || len - Framing - 4 != (long)n * Record) return null;
                p = new byte[len];
                if (RandomAccess.Read(h, p, 0) != len) return null;
            }
            uint U(long at) => BitConverter.ToUInt32(p, (int)at);
            var pipelines = new List<(uint, uint[])>();
            var skipped = 0;
            for (var at = Header + 4; at < p.Length - 8; at += Record)
            {
                uint root = U(at + 16), first = U(at + 24), ps = U(at + 40);
                if (U(at + 8) != RecordPayload || !roots.ContainsKey(root) || !shaders.ContainsKey(first) || ps != 0 && !shaders.ContainsKey(ps)) return null;
                if (OtherStages.Any(o => shaders.ContainsKey(U(at + o)))) { skipped++; continue; }
                pipelines.Add((root, ps == 0 ? new[] { first } : new[] { first, ps }));
            }
            return new Stores(shaderFile, rootFile, psoFile, shaders.ToDictionary(e => e.Key, e => e.Value.Offset), roots, pipelines, skipped);
        }
        catch (Exception) { return null; }   // a malformed or unreadable file: the carver's own reading stands
    }

    /// <summary>The count a .store2 file's payload starts with, when its framing holds: b = the file's length - 24, and the
    /// trailer repeats a (the payload is then [<see cref="Header"/>, length - 8)); null otherwise.</summary>
    static uint? Frame(Microsoft.Win32.SafeHandles.SafeFileHandle h, long len)
    {
        Span<byte> head = stackalloc byte[Header + 4], tail = stackalloc byte[8];
        return len >= Framing + 4 && RandomAccess.Read(h, head, 0) == head.Length && RandomAccess.Read(h, tail, len - 8) == 8
            && BitConverter.ToUInt64(head[8..]) == (ulong)(len - Framing) && BitConverter.ToUInt64(tail) == BitConverter.ToUInt64(head)
            ? BitConverter.ToUInt32(head[Header..]) : null;
    }

    static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>A store's entries by id: where each container starts and its stored size; null when they don't fill its
    /// payload exactly.</summary>
    static Dictionary<uint, (long Offset, int Size)>? Entries(string path)
    {
        using var h = Open(path);
        var len = RandomAccess.GetLength(h);
        if (Frame(h, len) is not { } n) return null;
        var entries = new Dictionary<uint, (long, int)>();
        Span<byte> b = stackalloc byte[12];
        long o = Header + 4, end = len - 8;
        for (var i = 0u; i < n; i++)
        {
            if (o + 12 > end || RandomAccess.Read(h, b, o) != 12) return null;
            var (id, size, zero) = (BitConverter.ToUInt32(b), BitConverter.ToUInt32(b[4..]), BitConverter.ToUInt32(b[8..]));
            o += 12;
            if (zero != 0 || size > end - o || size > Dxbc.MaxSize || !entries.TryAdd(id, (o, (int)size))) return null;
            o += size;
        }
        return o == end ? entries : null;
    }
}
