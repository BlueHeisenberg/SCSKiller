using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SCSKiller.Core.Planning;

/// <summary>The proxy's db format (proxy/proxy.cpp): records of u8 tag, u32 len, payload.
/// 'B' blob = sha1[20] + bytes (shader container or root signature). 'G' / 'C' / 'S' = canonical PSO payloads
/// (graphics desc, compute desc = <see cref="Compute"/>, pipeline stream); sha1(tag + payload) is the PSO key. 'P' plan item = template key[20],
/// root sig[20], u32 n, n x (u32 stage, sha1[20]), then u32 0xFFFFFFFF (keep the template's input layout) or a canonical
/// input layout. '1' D3D11 item = u32 stage (1 VS, 2 PS, 3 DS, 4 HS, 5 GS, 6 CS) + sha1[20] of a 'B' blob in the same db:
/// the warmer creates that shader and draws/dispatches it once. '2' D3D11 tessellation pair = HS sha1[20] + DS sha1[20]: the warmer draws them together,
/// behind a generated VS. 'D' (the D3D11 recorder) = a draw's vertex stages, input layout and topology (<see cref="Draw11"/>);
/// '3' (plan only) the same payload, for the warmer to draw. 'Y' (plan only) = a ray tracing collection to synthesize at materialize (<see cref="RtCollections.Item"/>); 'H' (plan only) = a REDengine 3 or FromSoftware hit group's (<see cref="RedEngine.RedRayTracing.Item"/>).
/// A stream output declaration: <see cref="SoDecl"/>. 'R' / 'A' = ray tracing state objects
/// (<see cref="ParseStateObject"/>), replayed exactly as recorded. 'N' = the NVAPI state another record was created with
/// (<see cref="NvState"/>). 'W' = a create a layer wrapping the device (a mod) changed: key[20] of the record the driver got +
/// key[20] of the record the game asked for (all zero: the layer's own create); both are records of the same db, and
/// only the game's is shared (<see cref="HashOnly.Canonical"/>). Hashes are lowercase hex here; all-zero = none.</summary>
public static partial class PsoDb
{
    public readonly record struct Rec(char Tag, byte[] Payload)
    {
        public string Key => Hex(SHA1.HashData([(byte)Tag, .. Payload]));
    }

    /// <summary>One vertex input element. <paramref name="Class"/>: 0 per-vertex, 1 per-instance (D3D12_INPUT_CLASSIFICATION);
    /// <paramref name="Step"/>: its instance data step rate. The defaults are what synthesized layouts use (per-vertex, slot 0).</summary>
    public readonly record struct LayoutElem(string Semantic, int Index, uint Format, uint Offset, uint Slot = 0, uint Class = 0, uint Step = 0);

    public static readonly string Zero = new('0', 40);
    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);

    /// <summary>Every record in file order, of a proxy db or a <see cref="WriteCompact">compact</see> one; stops at a torn
    /// tail like the proxy does. A compact file that ends short of its recorded length throws InvalidDataException.</summary>
    public static IEnumerable<Rec> Read(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[CompactHead];
        var n = f.ReadAtLeast(head, head.Length, false);
        if (!IsCompact(head.AsSpan(0, n)))
        {
            f.Position = 0;
            foreach (var r in Read(new BufferedStream(f, 1 << 20))) yield return r;
            yield break;
        }
        if (n < CompactHead || head[CompactMagic.Length] != CompactVersion) throw new InvalidDataException($"{path}: a compact recording of an unknown version");
        var raw = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(CompactMagic.Length + 1));
        using var b = new BrotliStream(new BufferedStream(f, 1 << 20), CompressionMode.Decompress);
        long got = 0;
        foreach (var r in Read(b))
        {
            got += 5 + r.Payload.Length;
            yield return r;
        }
        if (got != raw) throw new InvalidDataException($"{path}: a compact recording that ends short ({got} of {raw} bytes)");
    }

    // Compact recording: magic, u8 version, i64 length of the proxy db it holds, then that db Brotli-compressed. A proxy db
    // starts with a record tag, never 0.
    static ReadOnlySpan<byte> CompactMagic => "\0SCSKREC"u8;
    const byte CompactVersion = 1;
    const int CompactHead = 8 + 1 + 8;

    static bool IsCompact(ReadOnlySpan<byte> head) => head.Length >= CompactMagic.Length && head[..CompactMagic.Length].SequenceEqual(CompactMagic);

    public static bool IsCompact(string path)
    {
        using var f = File.OpenRead(path);
        Span<byte> head = stackalloc byte[CompactMagic.Length];
        return IsCompact(head[..f.ReadAtLeast(head, head.Length, false)]);
    }

    /// <summary>Writes <paramref name="records"/> as a compact recording at <paramref name="path"/>: into a temp file that is
    /// read back and compared before it replaces <paramref name="path"/>, so an interrupted write leaves the old file.</summary>
    public static void WriteCompact(string path, IEnumerable<Rec> records)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";   // per writer: the app and the CLI may write at once
        try
        {
            byte[] sum;
            using (var f = File.Create(tmp))
            {
                f.Write(new byte[CompactHead]);
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long raw = 0;
                // Brotli 6: 20x on recordings' pipeline records in well under a second; 11 costs minutes on shader bytes for 1-2% more
                using (var b = new BufferedStream(new BrotliStream(f, new BrotliCompressionOptions { Quality = 6 }, leaveOpen: true), 1 << 20))
                    foreach (var r in records)
                    {
                        Write(b, r.Tag, r.Payload);
                        Framed(h, r);
                        raw += 5 + r.Payload.Length;
                    }
                sum = h.GetHashAndReset();
                f.Position = 0;
                f.Write(CompactMagic);
                f.WriteByte(CompactVersion);
                Span<byte> len = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(len, raw);
                f.Write(len);
            }
            using (var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                foreach (var r in Read(tmp)) Framed(h, r);
                if (!h.GetHashAndReset().AsSpan().SequenceEqual(sum)) throw new InvalidDataException($"{tmp} doesn't read back as written");
            }
            File.Move(tmp, path, true);
        }
        finally { File.Delete(tmp); }

        static void Framed(IncrementalHash h, Rec r)
        {
            Span<byte> head = stackalloc byte[5];
            head[0] = (byte)r.Tag;
            BinaryPrimitives.WriteUInt32LittleEndian(head[1..], (uint)r.Payload.Length);
            h.AppendData(head);
            h.AppendData(r.Payload);
        }
    }

    /// <summary>A compact recording's bytes around <paramref name="brotli"/>, a proxy db of <paramref name="raw"/> bytes as
    /// Brotli (a download kept as it came).</summary>
    public static byte[] Compact(long raw, ReadOnlySpan<byte> brotli)
    {
        var f = new byte[CompactHead + brotli.Length];
        CompactMagic.CopyTo(f);
        f[CompactMagic.Length] = CompactVersion;
        BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(CompactMagic.Length + 1), raw);
        brotli.CopyTo(f.AsSpan(CompactHead));
        return f;
    }

    /// <summary>Copies a recording as a proxy db, which is what the proxy reads.</summary>
    public static void CopyRaw(string from, string to)
    {
        if (!IsCompact(from)) { File.Copy(from, to, true); return; }
        using var f = new BufferedStream(File.Create(to), 1 << 20);
        foreach (var r in Read(from)) Write(f, r.Tag, r.Payload);
    }

    public static IEnumerable<Rec> Read(Stream f)
    {
        var head = new byte[5];
        while (f.ReadAtLeast(head, 5, false) == 5)
        {
            var len = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
            if (head[0] == 'B' && len < 20) throw new InvalidDataException($"a 'B' record of {len} bytes, short of its SHA-1");
            if (f.CanSeek && len > f.Length - f.Position) yield break;   // a torn tail, or a stray file's "length": nothing allocated
            if (Body(f, len) is not { } body) yield break;
            yield return new Rec((char)head[0], body);
        }
    }

    /// <summary>The next <paramref name="len"/> bytes of <paramref name="f"/>; null when it ends first.</summary>
    internal static byte[]? Body(Stream f, uint len)
    {
        if (f.CanSeek || len <= 1 << 20)
        {
            var body = new byte[len];
            return f.ReadAtLeast(body, body.Length, false) < body.Length ? null : body;
        }
        // a stream (a compact recording's Brotli) can't tell its length: the buffer grows only with bytes that arrive
        using var m = new MemoryStream();
        var chunk = new byte[1 << 20];
        for (int n; m.Length < len && (n = f.Read(chunk, 0, (int)Math.Min(chunk.Length, len - m.Length))) > 0;) m.Write(chunk, 0, n);
        return m.Length < len ? null : m.ToArray();
    }

    public static void Write(Stream s, char tag, ReadOnlySpan<byte> payload)
    {
        Span<byte> head = stackalloc byte[5];
        head[0] = (byte)tag;
        BinaryPrimitives.WriteUInt32LittleEndian(head[1..], (uint)payload.Length);
        s.Write(head);
        s.Write(payload);
    }

    public static void WriteBlob(Stream s, string sha1, ReadOnlySpan<byte> bytes)
    {
        Span<byte> head = stackalloc byte[25];
        head[0] = (byte)'B';
        BinaryPrimitives.WriteUInt32LittleEndian(head[1..], (uint)(20 + bytes.Length));
        Convert.FromHexString(sha1).CopyTo(head[5..]);
        s.Write(head);
        s.Write(bytes);
    }

    /// <summary>A PSO record's (root sig, stage -> shader) tuple, the driver-cache identity on NVIDIA.</summary>
    public sealed record Pso(string Rs, SortedDictionary<int, string> Stages, bool HasLayout, uint Topology)
    {
        public string Tuple => PsoDb.Tuple(Rs, Stages);
    }

    public static string Tuple(string rs, IEnumerable<KeyValuePair<int, string>> stages) =>
        $"{rs}|{string.Join(',', stages.OrderBy(s => s.Key).Select(s => $"{s.Key}:{s.Value}"))}";

    const int GfxLayoutAt = 120 + 328 + 4 + 44 + 52; // 'G': rs + 5 shaders, blend, sample mask, rasterizer, depth-stencil

    // Canonical sizes of the plain-data stream subobjects, as the proxy writes them (sizeof on x64).
    static readonly Dictionary<uint, int> SubPod = new()
    {
        [8] = 328, [9] = 4, [10] = 44, [11] = 52, [13] = 4, [14] = 4, [15] = 36, [16] = 4, [17] = 8, [18] = 4, [20] = 4, [21] = 56, [26] = 60, [27] = 44, [28] = 40,
    };
    static bool IsShader(uint t) => t is >= 1 and <= 6 or 24 or 25;

    public static Pso Parse(Rec r)
    {
        var p = r.Payload;
        string H(int o) => Hex(p.AsSpan(o, 20));
        if (r.Tag == 'C') return new Pso(H(0), new() { [(int)Stage.Compute] = H(20) }, false, 0);
        var st = new SortedDictionary<int, string>();
        if (r.Tag == 'G')
        {
            int[] order = [1, 2, 3, 4, 5]; // VS, PS, DS, HS, GS
            for (var i = 0; i < 5; i++) if (H(20 + 20 * i) != Zero) st[order[i]] = H(20 + 20 * i);
            return new Pso(H(0), st, U(p, GfxLayoutAt) > 0, U(p, GfxLayoutAt + 4 + LayoutSize(p, GfxLayoutAt) + 4));
        }
        string rs = Zero;
        bool layout = false;
        uint topo = 0;
        var end = 4;
        foreach (var (t, pos, next) in Subobjects(p))
        {
            if (t == 0) rs = H(pos);
            else if (IsShader(t)) { if (H(pos) != Zero) st[(int)t] = H(pos); }
            else if (t == 12) layout = U(p, pos) > 0;
            else if (t == 14) topo = U(p, pos);
            end = next;
        }
        if (end != p.Length) throw new InvalidDataException($"stream record parse mismatch ({end} != {p.Length})");
        return new Pso(rs, st, layout, topo);
    }

    /// <summary>An 'S' payload's subobjects in order: type, where its body starts and where it ends.</summary>
    static IEnumerable<(uint Type, int At, int End)> Subobjects(byte[] p)
    {
        var pos = 4;
        for (var i = 0u; i < U(p, 0); i++)
        {
            var t = U(p, pos);
            pos += 4;
            var size = t == 0 || IsShader(t) ? 20
                : t == 12 ? 4 + LayoutSize(p, pos)
                : t == 22 ? 4 + 8 * (int)U(p, pos) + 4   // view instancing
                : t == 7 ? 0                             // stream output without entries: nothing written
                : t == SoDecl ? SoSize(p, pos)
                : SubPod[t];
            yield return (t, pos, pos + size);
            pos += size;
        }
    }

    static uint U(byte[] p, int o) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(o));

    /// <summary>Throws InvalidDataException unless a 'G' / 'C' / 'S' payload parses to exactly its end with <see cref="Parse"/>
    /// and <see cref="ParseState"/> (hash-only recordings from other machines: <see cref="HashOnly"/>).</summary>
    public static void Check(Rec r)
    {
        var p = r.Payload;
        try
        {
            switch (r.Tag)
            {
                case 'C' when p.Length == 48: break;
                case 'S' when U(p, 0) <= 64: break; // D3D12 takes each subobject type once: bounds the walk
                case 'G':
                    var end = GfxLayoutAt + 4 + LayoutSize(p, GfxLayoutAt) + 4 + 4 + 36 + 4 + 8 + 4 + 4;
                    if (end > p.Length || end < p.Length && end + SoSize(p, end) != p.Length)
                        throw new InvalidDataException("'G' record size mismatch");
                    break;
                default: throw new InvalidDataException($"not a PSO record: '{r.Tag}' ({p.Length} bytes)");
            }
            Parse(r);
            ParseState(r);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or KeyNotFoundException or OverflowException)
        {
            throw new InvalidDataException($"malformed '{r.Tag}' record", e);
        }
    }

    /// <summary>A stream's stream output subobject with entries (the proxy writes D3D12's type 7 only without entries, and
    /// no body): u32 n, n x (u32 stream, u32 semantic length or 0xFFFFFFFF for a gap + ASCII, u32 index, start component,
    /// component count, output slot), u32 strides n + strides, u32 rasterized stream. A 'G' record carries the same body
    /// after its last field (<see cref="StreamOutputOf"/>).</summary>
    public const uint SoDecl = 0x10007;

    static int SoSize(byte[] p, int pos)
    {
        var o = pos + 4;
        for (var i = 0u; i < U(p, pos); i++) o += 8 + (U(p, o + 4) is var n && n != uint.MaxValue ? checked((int)n) : 0) + 16;
        return o + 4 + 4 * (int)U(p, o) + 4 - pos;
    }

    /// <summary>A 'G' or 'S' record's stream output declaration (canonical bytes, see <see cref="SoDecl"/>); null = none. The
    /// planner never synthesizes one (the game's declaration isn't in its shaders): recorded pipelines replay with theirs.</summary>
    public static byte[]? StreamOutputOf(Rec r)
    {
        var p = r.Payload;
        if (r.Tag == 'G')
        {
            var end = GfxLayoutAt + 4 + LayoutSize(p, GfxLayoutAt) + 4 + 4 + 36 + 4 + 8 + 4 + 4; // IB strip cut, topology, RT formats, DSV, sample desc, node mask, flags
            return end < p.Length ? p[end..] : null;
        }
        if (r.Tag != 'S') return null;
        foreach (var (t, pos, next) in Subobjects(p))
            if (t == SoDecl) return p[pos..next];
        return null;
    }

    /// <summary>A ray tracing state object record: 'R' (CreateStateObject) or 'A' (AddToStateObject: <see cref="Base"/>,
    /// the key of the record it grows). <paramref name="Type"/>: D3D12_STATE_OBJECT_TYPE (0 collection, 3 ray tracing
    /// pipeline). <paramref name="Libraries"/> / <paramref name="RootSignatures"/>: the blobs it names (DXIL libraries are
    /// shader blobs, hashed like one); <paramref name="Depends"/>: the records it builds on (its base, the collections it
    /// links), which the warm creates first. Records only replay as they were recorded: nothing plans from them.</summary>
    public sealed record StateObject(uint Type, string? Base, List<string> Libraries, List<string> RootSignatures, List<string> Depends);

    public static bool IsStateObject(char tag) => tag is 'R' or 'A';

    /// <summary>Subobjects in one state object record, at most. Real ones: 694 at most (3 games' community downloads, 5,393
    /// state objects). Same value in records.rs.</summary>
    public const uint MaxSubobjects = 8192;

    /// <summary>An 'N' record: the NVAPI state the record with key <paramref name="Target"/> was created with, which NVIDIA's
    /// compiler keys on (selftest nvext). <paramref name="Slot"/> / <paramref name="Space"/>: the shader-extension UAV
    /// (uint.MaxValue = none); <paramref name="Scope"/>: how the game set it (1 device, 2 thread, 3 PSO extension; the warm
    /// sets any on its creating thread), with <see cref="GlobalOptions"/>; <paramref name="Options"/>:
    /// NvAPI_D3D12_SetCreatePipelineStateOptions flags. Payload: key[20], u32 slot, space, scope, options.</summary>
    public readonly record struct NvState(string Target, uint Slot, uint Space, uint Scope, uint Options)
    {
        public const int Size = 36;

        /// <summary>Scope bit of a state object's record from a recorder that takes the options as the process's (they are
        /// global, nvapi.h): its options are as the driver had them, also 0. Without it they may be missing
        /// (<see cref="RtCollections.OptionFixes"/>).</summary>
        public const uint GlobalOptions = 0x100;

        /// <summary>No slot and no options: nothing NVIDIA keys on.</summary>
        public bool Bare => Slot == uint.MaxValue && Options == 0;

        public static NvState Parse(Rec r)
        {
            if (r.Tag != 'N' || r.Payload.Length != Size) throw new InvalidDataException($"not an 'N' record ('{r.Tag}', {r.Payload.Length} bytes)");
            var p = r.Payload;
            return new(Hex(p.AsSpan(0, 20)), U(p, 20), U(p, 24), U(p, 28), U(p, 32));
        }

        public Rec ToRec()
        {
            var p = new byte[Size];
            Convert.FromHexString(Target).CopyTo(p, 0);
            foreach (var (v, i) in new[] { Slot, Space, Scope, Options }.Select((v, i) => (v, i))) BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(20 + 4 * i), v);
            return new('N', p);
        }
    }

    /// <summary>Parses an 'R' / 'A' payload (format in proxy.cpp write_so): u32 type, u32 n, n x
    /// (u32 subobject type + its canonical form). InvalidDataException when it doesn't parse to its end.</summary>
    public static StateObject ParseStateObject(Rec r)
    {
        if (!IsStateObject(r.Tag)) throw new ArgumentException($"'{r.Tag}' is not a state object record");
        var p = r.Payload;
        var pos = 0;
        uint Next() { if (pos + 4 > p.Length) throw new InvalidDataException("state object record truncated"); var v = U(p, pos); pos += 4; return v; }
        string H() { if (pos + 20 > p.Length) throw new InvalidDataException("state object record truncated"); var h = Hex(p.AsSpan(pos, 20)); pos += 20; return h; }
        void Str() { var n = Next(); if (n != uint.MaxValue) { if (2L * n > p.Length - pos) throw new InvalidDataException("malformed string"); pos += 2 * (int)n; } }
        void Strs() { for (var n = Next(); n > 0; n--) Str(); }
        void Exports() { for (var n = Next(); n > 0; n--) { Str(); Str(); Next(); } }
        var baseKey = r.Tag == 'A' ? H() : null;
        var so = new StateObject(Next(), baseKey, [], [], baseKey != null ? [baseKey] : []);
        var count = Next();
        // each library, collection and root signature becomes a string: refused from the count, before any is read
        if (count > MaxSubobjects) throw new InvalidDataException($"state object of {count} subobjects, over {MaxSubobjects}");
        for (var n = count; n > 0; n--)
            switch (Next())
            {
                case 0 or 3 or 10: Next(); break;                        // state object config, node mask, pipeline config
                case 9 or 12: Next(); Next(); break;                      // shader config, pipeline config 1
                case 1 or 2: if (H() is var rs && rs != Zero) so.RootSignatures.Add(rs); break;
                case 5: so.Libraries.Add(H()); Exports(); break;          // DXIL library
                case 6: so.Depends.Add(H()); Exports(); break;            // existing collection
                case 7: Next(); Strs(); break;                            // association: subobject index + exports
                case 8: Str(); Strs(); break;                             // DXIL association: subobject name + exports
                case 11: Str(); Next(); Str(); Str(); Str(); break;       // hit group
                case var t: throw new InvalidDataException($"unknown state subobject type {t}");
            }
        if (pos != p.Length) throw new InvalidDataException($"state object record parse mismatch ({pos} != {p.Length})");
        return so;
    }

    /// <summary>What a state object record compiles on NVIDIA, for counting new pipelines (never to merge or replay records):
    /// the SHA-1 of its tag and its subobjects, an addition's base left out and a launch's alias ending taken off. An alias
    /// is an export's name given to another function (ExportToRename set) and ending in <c>_LRS_&lt;16 hex&gt;</c>; the
    /// ending comes off it and off the hit group and association names that name it, nothing else. The Witcher 3 renames
    /// its fixed DXIL functions so, a new suffix every launch, and adds its materials in another order each launch:
    /// NVIDIA's key leaves export names out (selftest dxr), and its 24 single-material additions byte for byte an earlier
    /// session's, on other bases, took 6.5-14 ms against 28-80 ms cold. A record two of whose names would become one, one
    /// with a library or collection that exports all its functions (no export listed: their names aren't in the record),
    /// or one that doesn't parse, is its key.</summary>
    public static string StateObjectIdentity(Rec r)
    {
        if (!IsStateObject(r.Tag)) return r.Key;
        try
        {
            var aliases = new Dictionary<string, string>();   // an alias -> it without its ending
            var names = new HashSet<string>();                // every export name and hit group name, as recorded
            var listed = WalkStateObject(r, (name, renames) =>
            {
                names.Add(name);
                if (renames != null && LaunchSuffix().Replace(name, "") is var bare && bare != name) aliases[name] = bare;
            }, null);
            if (!listed) return r.Key;
            if (names.Select(n => aliases.GetValueOrDefault(n, n)).Distinct().Count() < names.Count) return r.Key;
            var w = new MemoryStream();
            WalkStateObject(r, null, (bytes, name) =>
            {
                if (name == null) { w.Write(bytes.Span); return; }
                var v = aliases.GetValueOrDefault(name, name);
                w.Write(BitConverter.GetBytes(v.Length));
                w.Write(Encoding.Unicode.GetBytes(v));
            });
            return Hex(SHA1.HashData([(byte)r.Tag, .. w.ToArray()]));
        }
        catch (InvalidDataException) { return r.Key; }
    }

    /// <summary>A state object record that names an export with a launch's alias (<see cref="StateObjectIdentity"/>): a
    /// later launch of the game creates it under another name.</summary>
    public static bool HasLaunchAlias(Rec r)
    {
        if (!IsStateObject(r.Tag)) return false;
        var found = false;
        try { WalkStateObject(r, (name, renames) => found |= renames != null && LaunchSuffix().IsMatch(name), null); }
        catch (InvalidDataException) { return false; }
        return found;
    }

    /// <summary>A state object record's subobjects past its base key: <paramref name="export"/> gets each export's name and
    /// the function it renames (and each hit group's name, renaming none); <paramref name="write"/> gets the bytes in order,
    /// a string that names an export or hit group (an export's own name, a hit group's name and the exports it imports, an
    /// association's exports) with its text, every other piece with null. False when a library or collection lists no
    /// export (it exports all its functions).</summary>
    static bool WalkStateObject(Rec r, Action<string, string?>? export, Action<ReadOnlyMemory<byte>, string?>? write)
    {
        var p = r.Payload;
        var pos = r.Tag == 'A' ? 20 : 0;
        if (pos > p.Length) throw new InvalidDataException("state object record truncated");
        uint Next() { if (pos + 4 > p.Length) throw new InvalidDataException("state object record truncated"); var v = U(p, pos); write?.Invoke(p.AsMemory(pos, 4), null); pos += 4; return v; }
        void H() { if (pos + 20 > p.Length) throw new InvalidDataException("state object record truncated"); write?.Invoke(p.AsMemory(pos, 20), null); pos += 20; }
        string? Read()
        {
            if (pos + 4 > p.Length) throw new InvalidDataException("state object record truncated");
            var n = U(p, pos);
            if (n == uint.MaxValue) { pos += 4; return null; }
            if (2L * n > p.Length - pos - 4) throw new InvalidDataException("malformed string");
            var v = Encoding.Unicode.GetString(p, pos + 4, 2 * (int)n);
            pos += 4 + 2 * (int)n;
            return v;
        }
        void Str(bool name)
        {
            var at = pos;
            var v = Read();
            write?.Invoke(p.AsMemory(at, pos - at), name ? v : null);
        }
        void Names() { for (var n = Next(); n > 0; n--) Str(true); }
        var listed = true;
        void Exports()
        {
            var count = Next();
            listed &= count > 0;
            for (var n = count; n > 0; n--)
            {
                var at = pos;
                var name = Read();
                var mid = pos;
                var renames = Read();
                if (name != null) export?.Invoke(name, renames);
                write?.Invoke(p.AsMemory(at, mid - at), name);
                write?.Invoke(p.AsMemory(mid, pos - mid), null);
                Next();
            }
        }
        Next();
        var count = Next();
        if (count > MaxSubobjects) throw new InvalidDataException($"state object of {count} subobjects, over {MaxSubobjects}");
        for (var n = count; n > 0; n--)
            switch (Next())
            {
                case 0 or 3 or 10: Next(); break;
                case 9 or 12: Next(); Next(); break;
                case 1 or 2: H(); break;
                case 5 or 6: H(); Exports(); break;
                case 7: Next(); Names(); break;
                case 8: Str(false); Names(); break;
                case 11:
                    var at = pos;
                    if (Read() is { } group) export?.Invoke(group, null);
                    pos = at;
                    Str(true); Next(); Str(true); Str(true); Str(true);
                    break;
                case var t: throw new InvalidDataException($"unknown state subobject type {t}");
            }
        if (pos != p.Length) throw new InvalidDataException("state object record parse mismatch");
        return listed;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"_LRS_[0-9A-Fa-f]{16}\z")]
    private static partial System.Text.RegularExpressions.Regex LaunchSuffix();

    /// <summary>The state of a graphics PSO record that vendors key their caches on (on top of <see cref="Parse"/>'s tuple).
    /// <paramref name="RtWriteMasks"/> / <paramref name="LogicOps"/>: one per render target (RtFormats.Length), RT 0's when
    /// IndependentBlendEnable is off; a logic op is -1 when LogicOpEnable is off. Mesh pipelines: empty layout, topology 0.
    /// <paramref name="Blend"/>: the canonical D3D12_BLEND_DESC (328 bytes; null: none in the record, D3D12's default);
    /// <paramref name="DepthStencil"/>: the depth-stencil desc as a canonical D3D12_DEPTH_STENCIL_DESC1 (56 bytes; null: none,
    /// or a DEPTH_STENCIL2). Both are what a synthesized PSO copies to match the game's pipeline state.</summary>
    public sealed record PsoState(string Rs, SortedDictionary<int, string> Stages, List<LayoutElem> Layout, uint Topology, uint[] RtFormats,
        byte[] RtWriteMasks, int[] LogicOps, uint Dsv, uint SampleCount, byte[]? Blend = null, byte[]? DepthStencil = null)
    {
        public string Tuple => PsoDb.Tuple(Rs, Stages);
        /// <summary>Render target 0 blends with the pixel shader's second output (a SRC1_* factor): dual-source blending.</summary>
        public bool DualSource => PsoDb.DualSource(Blend);
    }

    public const int BlendDescSize = 328, DepthStencilDesc1Size = 56;

    /// <summary>A canonical D3D12_BLEND_DESC whose render target 0 has blending on with a SRC1_COLOR / INV_SRC1_COLOR /
    /// SRC1_ALPHA / INV_SRC1_ALPHA factor (color or alpha; only RT 0 may use them).</summary>
    public static bool DualSource(byte[]? blend)
    {
        if (blend is not { Length: BlendDescSize } || U(blend, 8) == 0) return false;
        foreach (var o in new[] { 16, 20, 28, 32 }) if (U(blend, o) is >= 16 and <= 19) return true; // RT 0 (at 8): Src, Dest, SrcAlpha, DestAlpha
        return false;
    }

    /// <summary>A 'G' or 'S' record's state (a compute stream's is empty); null for 'C' and other tags. A companion to <see cref="Parse"/>.</summary>
    public static PsoState? ParseState(Rec r)
    {
        if (r.Tag is not ('G' or 'S')) return null;
        var p = r.Payload;
        var pso = Parse(r);
        List<LayoutElem> layout = [];
        uint topo = 0, dsv = 0, samples = 1;
        uint[] rt = [];
        var blendAt = -1;
        byte[]? depth = null;
        if (r.Tag == 'G')
        {
            blendAt = 120;
            depth = [.. p.AsSpan(120 + 328 + 4 + 44, 52), 0, 0, 0, 0]; // DEPTH_STENCIL_DESC, as a DESC1 without depth bounds
            layout = ReadLayout(p, GfxLayoutAt);
            var o = GfxLayoutAt + 4 + LayoutSize(p, GfxLayoutAt) + 4; // + IB strip cut
            topo = U(p, o);
            rt = RtFormats(p, o + 4);
            dsv = U(p, o + 40);
            samples = U(p, o + 44);
        }
        else
        {
            foreach (var (t, pos, _) in Subobjects(p))
                if (t == 12) layout = ReadLayout(p, pos);
                else if (t == 8) blendAt = pos;
                else if (t == 14) topo = U(p, pos);
                else if (t == 15) rt = RtFormats(p, pos);
                else if (t == 16) dsv = U(p, pos);
                else if (t == 21) depth = p.AsSpan(pos, DepthStencilDesc1Size).ToArray();
                else if (t == 11) depth = [.. p.AsSpan(pos, 52), 0, 0, 0, 0];
                else if (t == 17) samples = U(p, pos);
        }
        var masks = new byte[rt.Length];
        var ops = new int[rt.Length];
        for (var i = 0; i < rt.Length; i++)
        {
            if (blendAt < 0) { masks[i] = 0xF; ops[i] = -1; continue; } // no blend subobject: D3D12's default blend state
            var b = blendAt + 8 + 40 * (U(p, blendAt + 4) != 0 ? i : 0); // RenderTarget[8] after AlphaToCoverage, IndependentBlend
            masks[i] = p[b + 36];
            ops[i] = U(p, b + 4) != 0 ? (int)U(p, b + 32) : -1;
        }
        return new PsoState(pso.Rs, pso.Stages, layout, topo, rt, masks, ops, dsv, samples, blendAt < 0 ? null : p.AsSpan(blendAt, BlendDescSize).ToArray(), depth);

        static uint[] RtFormats(byte[] p, int o) => Enumerable.Range(0, (int)Math.Min(8, U(p, o))).Select(i => U(p, o + 4 + 4 * i)).ToArray();
    }

    /// <summary>A canonical input layout (u32 count, then per element: u32 name length, name, index, format, slot, offset,
    /// class, step rate), as the proxy records it and plan items carry it.</summary>
    public static byte[] LayoutBytes(IReadOnlyList<LayoutElem> layout) => new ArrayBufferWriter().Layout(layout).ToArray();

    static List<LayoutElem> ReadLayout(byte[] p, int pos)
    {
        var list = new List<LayoutElem>();
        var o = pos + 4;
        for (var i = 0u; i < U(p, pos); i++)
        {
            var n = (int)U(p, o);
            var name = Encoding.ASCII.GetString(p, o + 4, n);
            o += 4 + n;
            list.Add(new LayoutElem(name, (int)U(p, o), U(p, o + 4), U(p, o + 12), U(p, o + 8), U(p, o + 16), U(p, o + 20)));
            o += 24;
        }
        return list;
    }

    // bytes after the u32 count
    static int LayoutSize(byte[] p, int pos)
    {
        var o = pos + 4;
        for (var i = 0u; i < U(p, pos); i++) o += 4 + checked((int)U(p, o)) + 24; // checked: a malformed length can't step backwards
        return o - pos - 4;
    }

    public static byte[] Item(string template, string rs, IReadOnlyDictionary<int, string> stages, IReadOnlyList<LayoutElem>? layout)
    {
        var w = new ArrayBufferWriter();
        w.Hash(template).Hash(rs).U32((uint)stages.Count);
        foreach (var (s, h) in stages.OrderBy(s => s.Key)) w.U32((uint)s).Hash(h);
        if (layout == null) w.U32(uint.MaxValue);
        else w.Layout(layout);
        return w.ToArray();
    }

    /// <summary>A 'C' payload, the canonical D3D12_COMPUTE_PIPELINE_STATE_DESC: root sig[20], CS[20], u32 NodeMask, u32 Flags
    /// (48 bytes; the proxy fails to decode anything shorter, and never creates that PSO).</summary>
    public static byte[] Compute(string rs, string cs) => new ArrayBufferWriter().Hash(rs).Hash(cs).U32(0).U32(0).ToArray();

    public static byte[] D3D11Item(Stage stage, string sha1) => new ArrayBufferWriter().U32((uint)stage).Hash(sha1).ToArray();

    /// <summary>A D3D11 draw's vertex stages ('D' recorded, '3' planned): VS, GS, HS, DS (<see cref="Zero"/> = none), its
    /// D3D11_PRIMITIVE_TOPOLOGY (a triangle strip recorded as a list), whether a PS was bound, and the input layout as bound
    /// (offsets as given, append-aligned included; empty = none).</summary>
    public sealed record Draw11(string Vs, string Gs, string Hs, string Ds, uint Topology, bool Ps, List<LayoutElem> Layout);

    /// <summary>The payload of a 'D' / '3' record: VS[20] GS[20] HS[20] DS[20], u32 topology, u32 flags (1: a PS), layout.</summary>
    public static Draw11 ParseDraw11(byte[] p) =>
        p.Length < 92 ? throw new InvalidDataException($"a D3D11 draw of {p.Length} bytes")
            : new(Hex(p.AsSpan(0, 20)), Hex(p.AsSpan(20, 20)), Hex(p.AsSpan(40, 20)), Hex(p.AsSpan(60, 20)), U(p, 80), (U(p, 84) & 1) != 0, ReadLayout(p, 88));

    public static byte[] Draw11Bytes(Draw11 d) =>
        new ArrayBufferWriter().Hash(d.Vs).Hash(d.Gs).Hash(d.Hs).Hash(d.Ds).U32(d.Topology).U32(d.Ps ? 1u : 0).Layout(d.Layout).ToArray();
    public static byte[] D3D11Pair(string hs, string ds) => new ArrayBufferWriter().Hash(hs).Hash(ds).ToArray();

    public static (string Template, string Rs, SortedDictionary<int, string> Stages) ParseItem(byte[] p)
    {
        var st = new SortedDictionary<int, string>();
        for (var i = 0; i < (int)U(p, 40); i++) st[(int)U(p, 44 + 24 * i)] = Hex(p.AsSpan(48 + 24 * i, 20));
        return (Hex(p.AsSpan(0, 20)), Hex(p.AsSpan(20, 20)), st);
    }

    public const uint R16G16B16A16Float = 10, R32G32B32A32Uint = 3, R32G32B32A32Sint = 4, D32Float = 40;

    /// <summary>A synthesized 'S' template: UE 4.26's own subobject order with neutral state (no blend, cull none, depth off
    /// unless <paramref name="dsv"/> is set, sample count 1). Byte-identical to what the proxy's Writer records for that stream.
    /// <paramref name="rtWriteMasks"/>: per render target (default 0xF each; a write mask of 0 is in AMD's PS key); masks that
    /// differ across targets turn IndependentBlendEnable on, which nothing keys on. <paramref name="logicOp"/>: a D3D12_LOGIC_OP
    /// for every target (AMD keys the PS on it; D3D12 takes it only without independent blend), -1 = LogicOpEnable off.
    /// <paramref name="blend"/> / <paramref name="depthStencil"/>: a recorded canonical blend desc / DEPTH_STENCIL_DESC1 to use
    /// as they are instead (masks and logic op are then the blend desc's own).</summary>
    public static byte[] Stream(string rs, IReadOnlyDictionary<int, string> stages, IReadOnlyList<LayoutElem>? layout, uint topology, uint[] rtFormats, uint dsv,
        byte[]? rtWriteMasks = null, int logicOp = -1, byte[]? blend = null, byte[]? depthStencil = null)
    {
        if (blend is { Length: not BlendDescSize } || depthStencil is { Length: not DepthStencilDesc1Size }) throw new ArgumentException("not a canonical blend / depth-stencil desc");
        byte Mask(int i) => rtWriteMasks != null && i < rtWriteMasks.Length ? rtWriteMasks[i] : (byte)0xF;
        var independent = logicOp < 0 && Enumerable.Range(0, rtFormats.Length).Any(i => Mask(i) != Mask(0));
        var (opOn, op) = logicOp >= 0 ? (1u, (uint)logicOp) : (0u, 4u); // off: NOOP
        var w = new ArrayBufferWriter();
        var count = 0u;
        w.U32(0);
        ArrayBufferWriter Sub(uint type) { count++; return w.U32(type); }
        string Sh(Stage s) => stages.GetValueOrDefault((int)s, Zero);
        Sub(18).U32(1); // node mask, as UE sets it
        Sub(0).Hash(rs);
        if (stages.ContainsKey((int)Stage.Compute)) Sub(6).Hash(Sh(Stage.Compute));
        else
        {
            var mesh = stages.ContainsKey((int)Stage.Mesh);
            if (!mesh) Sub(12).Layout(layout ?? []);
            if (!mesh) Sub(13).U32(0); // IB strip cut disabled
            if (!mesh) Sub(14).U32(topology); // a mesh shader's output topology is its own; a mismatching one is a debug-layer error
            if (mesh)
            {
                if (stages.ContainsKey((int)Stage.Amplification)) Sub(24).Hash(Sh(Stage.Amplification));
                Sub(25).Hash(Sh(Stage.Mesh));
            }
            else foreach (var s in new[] { Stage.Vertex, Stage.Geometry, Stage.Hull, Stage.Domain }) Sub((uint)s).Hash(Sh(s));
            Sub(2).Hash(Sh(Stage.Pixel));
            if (blend != null) Sub(8).Bytes(blend);
            else
            {
                Sub(8).U32(0).U32(independent ? 1u : 0); // blend: no alpha-to-coverage, no independent blend unless the masks differ
                for (var i = 0; i < 8; i++) w.U32(0).U32(opOn).U32(2).U32(1).U32(1).U32(2).U32(1).U32(1).U32(op).U32(independent ? Mask(i) : Mask(0)); // off, ONE/ZERO/ADD, the logic op, the mask
            }
            if (depthStencil != null) Sub(21).Bytes(depthStencil);
            else
            {
                var depth = dsv != 0 ? 1u : 0;
                Sub(21).U32(depth).U32(depth).U32(8).U32(0).U32(0xFFFF); // depth on+write iff a DSV, func ALWAYS, no stencil, masks 0xFF/0xFF
                for (var i = 0; i < 2; i++) w.U32(1).U32(1).U32(1).U32(8); // stencil ops KEEP x3, ALWAYS
                w.U32(0); // no depth bounds test
            }
            Sub(16).U32(dsv);
            Sub(10).U32(3).U32(1).U32(0).U32(0).U32(0).U32(0).U32(1).U32(0).U32(0).U32(0).U32(0); // solid, cull none, depth clip on
            Sub(15).U32((uint)rtFormats.Length);
            for (var i = 0; i < 8; i++) w.U32(i < rtFormats.Length ? rtFormats[i] : 0);
            Sub(17).U32(1).U32(0);
            Sub(9).U32(uint.MaxValue);
        }
        var bytes = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, count);
        return bytes;
    }

    sealed class ArrayBufferWriter
    {
        readonly MemoryStream s = new();
        public ArrayBufferWriter U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); return this; }
        public ArrayBufferWriter Hash(string hex) { s.Write(Convert.FromHexString(hex)); return this; }
        public ArrayBufferWriter Bytes(byte[] b) { s.Write(b); return this; }
        public ArrayBufferWriter Layout(IReadOnlyList<LayoutElem> elems)
        {
            U32((uint)elems.Count);
            foreach (var e in elems)
            {
                var name = Encoding.ASCII.GetBytes(e.Semantic);
                U32((uint)name.Length);
                s.Write(name);
                U32((uint)e.Index).U32(e.Format).U32(e.Slot).U32(e.Offset).U32(e.Class).U32(e.Step);
            }
            return this;
        }
        public byte[] ToArray() => s.ToArray();
    }
}
