using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Unreal;

/// <summary>The PSO disk cache Unreal 4's D3D12 RHI writes for some games, in ProgramData or the user's Saved folder: every
/// PSO the game created on this PC, with its state, input layout and shader containers. Two layouts, reverse-engineered
/// (the pointers in them are the writing process's, never read):
///   - <c>D3DGraphics_*.ushaderprecache</c> / <c>D3DCompute_*.ushaderprecache</c> (Hogwarts Legacy, 4.27): u16 version 12, u16,
///     u32 count, u32, 16-byte guid; per PSO u32 '93/4', u32 size, u32 checksum and size bytes. A graphics body: 8 bytes, the
///     RHI's desc (root signature pointer, VS PS DS HS GS as pointer + length, blend, sample mask, rasterizer, depth-stencil,
///     input layout pointer + count, IB strip cut, topology, RT formats, RT count, DSV, sample desc, node mask, cached PSO,
///     flags), hashes, the D3D12_INPUT_ELEMENT_DESCs at +0x330, per element u32 length + semantic name, then the containers
///     back to back. A compute body holds one CS container.
///   - <c>Default_D3DGraphics.upsoprecache</c> / <c>Default_D3DCompute.upsoprecache</c> (Sea of Thieves): u32 version 12, u32
///     count, u32, u32; PSOs back to back, a graphics one: root signature pointer, a D3D12_GRAPHICS_PIPELINE_STATE_DESC
///     (0x290), 5 x 20-byte hashes, 4, u64; the input elements, per element u32 length + name; per shader in desc order u64 +
///     the container; u64 driver blob offset, u64 size, u64 checksum.</summary>
public static class PsoDiskCache
{
    /// <param name="Before">a graphics PSO's blend, sample mask, rasterizer and depth-stencil desc, as a 'G' record has them;
    /// null for compute</param>
    /// <param name="After">its IB strip cut, topology, RT count, 8 RT formats, DSV and sample desc, in a 'G' record's order</param>
    public sealed record Pso(byte[][] Containers, IReadOnlyList<LayoutElem>? Layout, byte[]? Before, byte[]? After);

    const uint RecordMagic = 0x342f3339; // "93/4"
    const int BeforeSize = BlendDescSize + 4 + 44 + 52, MaxContainer = 64 << 20, MaxRecord = 64 << 20; // far over any seen (Hogwarts: under 1 MB)

    static bool IsCompute(string path) => Path.GetFileName(path).Contains("D3DCompute", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is such a file: its first PSO reads (nothing past it is read).</summary>
    public static bool Is(string path)
    {
        try { return Read(path, new(), 1).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The PSOs of the file, one at a time. One that doesn't validate (desc lengths against the containers, the
    /// layout's end against the first one, a compute PSO that isn't one CS) is counted in <paramref name="rejected"/> and
    /// left out; reading stops at a record that can't be framed, or after <paramref name="max"/> records.</summary>
    public static IEnumerable<Pso> Read(string path, StrongBox<int> rejected, uint max = uint.MaxValue)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20); // the game may have it open
        var head = new byte[0x20];
        if (f.ReadAtLeast(head, head.Length, false) < head.Length || BinaryPrimitives.ReadUInt16LittleEndian(head) != 12) yield break;
        var count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)), max);
        var compute = IsCompute(path);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x1c)) == RecordMagic)
        {
            f.Position = 0x1c;
            var rec = new byte[12];
            for (var i = 0u; i < count && f.ReadAtLeast(rec, 12, false) == 12; i++)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(4));
                if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != RecordMagic || size > MaxRecord || size > f.Length - f.Position) yield break;
                var body = new byte[size];
                f.ReadExactly(body);
                if ((compute ? OneCs(Containers(body, 0)) : Graphics(body)) is { } pso) yield return pso;
                else rejected.Value++;
            }
        }
        else if (BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2)) == 0)
        {
            f.Position = 16;
            if (compute)
            {
                // ponytail: an older compute file is carved whole (Sea of Thieves': 1 MB); frame its records if a large one shows up
                if (f.Length > 256 << 20) yield break;
                var all = new byte[max == uint.MaxValue ? f.Length - 16 : Math.Min(f.Length - 16, 4 << 20)]; // a check reads the head only
                f.ReadExactly(all);
                foreach (var cs in Containers(all, 0).Take((int)Math.Min(max, int.MaxValue)))
                    if (OneCs([cs]) is { } pso) yield return pso;
                    else rejected.Value++;
                yield break;
            }
            for (var i = 0u; i < count; i++)
                if (OlderGraphics(f) is { } pso) yield return pso;
                else yield break; // unframed: where the rest starts can't be known
        }
    }

    static Pso? OneCs(List<byte[]> containers) => containers is [var cs] && Dxbc.Kind(cs) == 5 ? new Pso([cs], null, null, null) : null;

    static Pso? Graphics(byte[] r)
    {
        const int desc = 8, elements = 0x330;
        if (r.Length < elements) return null;
        var (layout, end) = Layout(r, desc + 0x210, elements);
        if (layout == null || end + 4 > r.Length || !r.AsSpan(end, 4).SequenceEqual("DXBC"u8)) return null; // the containers right after the names
        var containers = Containers(r, end);
        var lengths = Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(desc + 16 + 16 * i))).Where(n => n != 0).Order();
        if (!lengths.SequenceEqual(containers.Select(c => (long)c.Length).Order())) return null;
        var d = r.AsSpan(desc);
        return new Pso([.. containers], layout, Unpadded(d.Slice(0x58, BeforeSize)),
            [.. d.Slice(0x218, 8), .. d.Slice(0x240, 4), .. d.Slice(0x220, 32), .. d.Slice(0x244, 4), .. d.Slice(0x248, 8)]);
    }

    /// <summary>The next graphics PSO of the older layout; null when it doesn't frame: stream output or a cached blob (which
    /// the record doesn't show), a length past the file, a shader that isn't where its length says.</summary>
    static Pso? OlderGraphics(FileStream f)
    {
        const int desc = 8, fixedSize = 0x308;
        var r = new byte[fixedSize];
        if (f.ReadAtLeast(r, fixedSize, false) < fixedSize) return null;
        var d = r.AsSpan(desc);
        var lengths = Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(desc + 16 + 16 * i))).ToArray();
        var n = BinaryPrimitives.ReadInt32LittleEndian(d[0x230..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(d[0x60..]) != 0 || BinaryPrimitives.ReadInt64LittleEndian(d[0x280..]) != 0
            || n is < 0 or > 32 || lengths.Any(l => l is < 0 or > MaxContainer)) return null;
        var elements = new byte[32 * n];
        if (f.ReadAtLeast(elements, elements.Length, false) < elements.Length) return null;
        var layout = new List<LayoutElem>(n);
        var len4 = new byte[4];
        for (var i = 0; i < n; i++)
        {
            if (f.ReadAtLeast(len4, 4, false) < 4) return null;
            var len = BinaryPrimitives.ReadInt32LittleEndian(len4);
            if (len is < 1 or > 64 || f.Length - f.Position < len) return null;
            var name = new byte[len];
            f.ReadExactly(name);
            var e = elements.AsSpan(32 * i + 8, 24);
            layout.Add(new LayoutElem(Encoding.ASCII.GetString(name, 0, len - 1), (int)U(e, 0), U(e, 4), U(e, 12), U(e, 8), U(e, 16), U(e, 20)));
        }
        var containers = new List<byte[]>();
        foreach (var l in lengths.Where(l => l > 0))
        {
            if (f.Length - f.Position < 8 + l) return null;
            f.Position += 8;
            var c = new byte[l];
            f.ReadExactly(c);
            if (l < 32 || !c.AsSpan(0, 4).SequenceEqual("DXBC"u8) || BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(24)) != l || !Dxbc.Valid(c)) return null;
            containers.Add(c);
        }
        if (containers.Count == 0 || f.Length - f.Position < 24) return null;
        f.Position += 24;
        return new Pso([.. containers], layout, Unpadded(d.Slice(0x78, BeforeSize)),
            [.. d.Slice(0x238, 8), .. d.Slice(0x240, 36), .. d.Slice(0x264, 4), .. d.Slice(0x268, 8)]);
    }

    /// <summary>The blend, sample mask, rasterizer and depth-stencil descs with their padding zeroed (the writing process's
    /// memory): each render target's 3 bytes after its write mask, the 2 after the depth-stencil desc's stencil masks.</summary>
    static byte[] Unpadded(ReadOnlySpan<byte> before)
    {
        var b = before.ToArray();
        for (var rt = 0; rt < 8; rt++) b.AsSpan(8 + 40 * rt + 37, 3).Clear();
        b.AsSpan(BlendDescSize + 4 + 44 + 18, 2).Clear();
        return b;
    }

    static List<byte[]> Containers(byte[] r, int from)
    {
        var containers = new List<byte[]>();
        for (var p = from; p + 32 <= r.Length;)
        {
            var at = r.AsSpan(p).IndexOf("DXBC"u8);
            if (at < 0) break;
            p += at;
            var size = p + 28 <= r.Length ? BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(p + 24)) : 0;
            if (size < 32 || size > r.Length - p || !Dxbc.Valid(r.AsSpan(p, size))) { p += 4; continue; }
            containers.Add(r.AsSpan(p, size).ToArray());
            p += size;
        }
        return containers;
    }

    /// <summary>The input layout: the element count at <paramref name="countAt"/>, the 32-byte elements at
    /// <paramref name="at"/>, then per element u32 length + name; and where the names end. (null, 0): it doesn't read.</summary>
    static (List<LayoutElem>? Layout, int End) Layout(byte[] r, int countAt, int at)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(countAt));
        if (n is < 0 or > 32 || at + 32 * n > r.Length) return (null, 0); // D3D12 takes 32 elements at most
        var names = at + 32 * n;
        var layout = new List<LayoutElem>(n);
        for (var i = 0; i < n; i++)
        {
            if (names + 4 > r.Length) return (null, 0);
            var len = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(names));
            if (len is < 1 or > 64 || len > r.Length - names - 4) return (null, 0);
            var name = Encoding.ASCII.GetString(r, names + 4, len - 1);
            names += 4 + len;
            var e = r.AsSpan(at + 32 * i + 8, 24);
            layout.Add(new LayoutElem(name, (int)U(e, 0), U(e, 4), U(e, 12), U(e, 8), U(e, 16), U(e, 20)));
        }
        return (layout, names);
    }

    static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    public sealed record Written(int Graphics, int Compute, int Rejected, int NotInIndex, int NoRootSignature, int UnreadableFiles);

    /// <summary>Writes the files' PSOs as a recording at <paramref name="outDb"/> (a proxy db: 'B' containers and root
    /// signatures, a 'G' per graphics PSO with its state and layout, a 'C' per compute one), each with the root signature the
    /// engine's rule builds for its shaders, as <see cref="PlanBuilder"/> builds it without a recording. A PSO with a shader
    /// the index lacks (another build's) or no root signature from the rule is left out; one written twice (two files of
    /// two GPUs) once. Nothing of the writing process is kept: no pointer, no cached blob, node mask 0.</summary>
    public static Written ToRecording(IEnumerable<string> files, ShaderIndex index, EngineInfo engine, PlanRule rule, bool nvidia, string outDb, CancellationToken ct = default)
    {
        var maxSrvs = RootSig.MaxSrvsFor(rule, index.Shaders.Values);
        var nvExtension = nvidia && RootSig.NvExtensionEverywhere(rule, index.Shaders.Values);
        var samplers = RootSig.StaticSamplers(rule, engine);
        var platOf = new Dictionary<string, string>();
        foreach (var m in index.Maps) foreach (var h in m.Shaders) platOf[h] = m.Platform;
        foreach (var m in index.OtherCopies ?? []) foreach (var h in m.Shaders) platOf.TryAdd(h, m.Platform);
        var (blobs, keys, rsBySet) = (new HashSet<string>(), new HashSet<string>(), new Dictionary<string, string?>());
        int graphics = 0, compute = 0, notInIndex = 0, noRs = 0, unreadable = 0;
        var rejected = new StrongBox<int>();
        var tmp = outDb + ".tmp";
        try
        {
            using (var f = new BufferedStream(File.Create(tmp), 1 << 20))
            {
                void Blob(string sha, byte[] b) { if (blobs.Add(sha)) WriteBlob(f, sha, b); }
                void Take(Pso p)
                {
                    ct.ThrowIfCancellationRequested();
                    var stages = new SortedDictionary<Stage, (string Sha, byte[] Bytes)>();
                    foreach (var c in p.Containers)
                        if (StageOf(Dxbc.Kind(c)) is { } st) stages[st] = (Convert.ToHexStringLower(SHA1.HashData(c)), c);
                    if (stages.Count != p.Containers.Length || stages.Values.Any(v => !index.Shaders.ContainsKey(v.Sha))) { notInIndex++; return; }
                    var set = string.Join(',', stages.Select(s => $"{(int)s.Key}:{s.Value.Sha}"));
                    if (!rsBySet.TryGetValue(set, out var rsSha))
                    {
                        var infos = stages.ToDictionary(kv => kv.Key, kv => index.Shaders[kv.Value.Sha]);
                        var meshTier = platOf.GetValueOrDefault(stages.Values.First().Sha)?.Split(' ')[0] == "PCD3D_SM6";
                        try
                        {
                            var rs = RootSig.Serialize(RootSig.Build(rule, infos, meshTier, maxSrvs, nvExtension, nvidia), samplers);
                            rsSha = Convert.ToHexStringLower(SHA1.HashData(rs));
                            Blob(rsSha, rs);
                        }
                        catch (RootSig.SerializeException) { rsSha = null; }
                        rsBySet[set] = rsSha;
                    }
                    if (rsSha == null) { noRs++; return; }
                    byte[] payload;
                    if (p.Before is not { } before || p.After is not { } after || p.Layout is not { } layout)
                    {
                        if (!stages.TryGetValue(Stage.Compute, out var cs)) { rejected.Value++; return; }
                        payload = PsoDb.Compute(rsSha, cs.Sha);
                    }
                    else
                    {
                        if (stages.ContainsKey(Stage.Compute)) { rejected.Value++; return; }
                        using var g = new MemoryStream();
                        g.Write(Convert.FromHexString(rsSha));
                        foreach (var st in new[] { Stage.Vertex, Stage.Pixel, Stage.Domain, Stage.Hull, Stage.Geometry })
                            g.Write(Convert.FromHexString(stages.TryGetValue(st, out var v) ? v.Sha : Zero));
                        g.Write(before);
                        g.Write(LayoutBytes(layout));
                        g.Write(after);
                        g.Write(new byte[8]); // node mask, flags
                        payload = g.ToArray();
                    }
                    var rec = new Rec(p.Before == null ? 'C' : 'G', payload);
                    try { Check(rec); }
                    catch (InvalidDataException) { rejected.Value++; return; }
                    if (!keys.Add(rec.Key)) return;
                    foreach (var (sha, b) in stages.Values) Blob(sha, b);
                    Write(f, rec.Tag, rec.Payload);
                    if (rec.Tag == 'G') graphics++;
                    else compute++;
                }
                foreach (var path in files)
                {
                    using var it = Read(path, rejected).GetEnumerator();
                    while (true)
                    {
                        try { if (!it.MoveNext()) break; }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unreadable++; break; } // gone or locked since it was found: no path in the log
                        Take(it.Current);
                    }
                }
            }
            File.Move(tmp, outDb, true);
        }
        finally { File.Delete(tmp); }
        return new Written(graphics, compute, rejected.Value, notInIndex, noRs, unreadable);
    }

    static Stage? StageOf(int kind) => kind switch { 0 => Stage.Pixel, 1 => Stage.Vertex, 2 => Stage.Geometry, 3 => Stage.Hull, 4 => Stage.Domain, 5 => Stage.Compute, _ => null };
}
