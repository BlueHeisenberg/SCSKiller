using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Unreal;

/// <summary>UE's FShaderCode = [resource-table prefix][DXBC/DXIL container][optional data trailer]. UE passes exactly
/// the container to D3D12, so that slice is what gets hashed and what the proxy records.</summary>
public static class ShaderContainer
{
    /// <summary>Offset of the first "DXBC" whose uint32 size at +24 fits in the blob, or -1.</summary>
    public static int Offset(byte[] code, out int len)
    {
        len = code.Length;
        for (var i = code.AsSpan().IndexOf("DXBC"u8); i >= 0 && i + 28 <= code.Length;)
        {
            var size = BitConverter.ToUInt32(code, i + 24);
            if (size >= 32 && i + size <= code.Length) { len = (int)size; return i; }
            var next = code.AsSpan(i + 1).IndexOf("DXBC"u8);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return -1;
    }

    /// <summary>Optional data trailer: repeated [uint8 key][int32 size][bytes], then int32 total trailer size.</summary>
    public static Dictionary<char, byte[]> OptionalData(byte[] code)
    {
        var d = new Dictionary<char, byte[]>();
        var total = code.Length >= 4 ? BitConverter.ToInt32(code, code.Length - 4) : 0;
        if (total < 4 || total > code.Length) return d;
        for (int p = code.Length - total, end = code.Length - 4; p + 5 <= end;)
        {
            var len = BitConverter.ToInt32(code, p + 1);
            if (len < 0 || p + 5 + len > end) break;
            d[(char)code[p]] = code[(p + 5)..(p + 5 + len)];
            p += 5 + len;
        }
        return d;
    }

    /// <summary>FShaderCodePackedResourceCounts ('p'): UsageFlags, NumSamplers, NumSRVs, NumCBs, NumUAVs. Zeros if absent.
    /// <paramref name="ue5"/>: also the <see cref="UeFlags"/> the UE5 root signature depends on (UE4 has none: its byte 0 is
    /// bGlobalUniformBufferUsed and its 'x' is bUsesWaveOps). <paramref name="nvExtension"/> (5.8+): also
    /// <see cref="UeFlags.NvIntrinsics"/>, which only 5.8's raster/compute root signatures depend on; before, it would split
    /// the planner's learned lookup (keyed by the counts) into NVIDIA and other variants.</summary>
    public static ResourceCounts UeCounts(byte[] code, bool ue5 = false, bool nvExtension = false)
    {
        var d = OptionalData(code);
        if (!d.TryGetValue('p', out var pc) || pc.Length < 5) return new(0, 0, 0, 0);
        return new(pc[3], pc[2], pc[4], pc[1], ue5 ? UeFlags.Of(pc[0], d) & (nvExtension ? ~0 : ~UeFlags.NvIntrinsics) : 0);
    }

    /// <summary>UE 4 forks with a 10-byte 'p' (stock 4.23: 5 bytes, 4.26/4.27: 8). Two layouts are known: Hogwarts Legacy's 4.27
    /// keeps the stock order; Respawn's 4.26 (Star Wars Jedi: Survivor) has NumSRVs as a uint16, so NumSamplers, NumSRVs, NumCBs,
    /// NumUAVs sit at 1, 2-3, 4, 5. The bytes alone can't tell them apart, the shaders' own bindings can: an index sees every
    /// such shader (<see cref="See"/>), each votes for the layout whose NumCBs matches its highest bound b register (space 0),
    /// and <see cref="Apply"/> switches all of them to the wide layout when it wins (Jedi 272607 of 273059 shaders match it,
    /// Hogwarts 4533 of 149333).</summary>
    public sealed class WideCounts
    {
        readonly ConcurrentDictionary<string, ResourceCounts> wide = new();
        int votes;

        public void See(byte[] code, ShaderInfo info)
        {
            if (!OptionalData(code).TryGetValue('p', out var pc) || pc.Length != 10) return;
            var w = new ResourceCounts(pc[4], pc[2] | pc[3] << 8, pc[5], pc[1]);
            wide[info.Sha1] = w;
            var cbs = info.Bindings.Where(b => b.Class == "cbv" && b.Space == 0).Select(b => b.Lower + Math.Max(1, b.Count)).DefaultIfEmpty(0).Max();
            if ((cbs == w.Cb) != (cbs == info.Counts.Cb)) Interlocked.Add(ref votes, cbs == w.Cb ? 1 : -1);
        }

        /// <returns>whether the wide layout won</returns>
        public bool Apply(ConcurrentDictionary<string, ShaderInfo> shaders)
        {
            if (votes <= 0) return false;
            foreach (var (h, w) in wide) shaders[h] = shaders[h] with { Counts = w };
            return true;
        }
    }

    /// <summary>Root-signature inputs of a UE5 shader, read by <see cref="Planning.RootSig"/>. The engine moved them between
    /// fields over time (ShaderCore.h, D3D12Shader.h, D3DShaderCompilerDXC.cpp at 5.0.3-5.6.0 release tags); the compiler sets
    /// both places from the same condition where both exist, so the union is right for every 5.x.</summary>
    public static class UeFlags
    {
        public const int BindlessResources = 1, BindlessSamplers = 2, RootConstants = 4, DiagnosticBuffer = 8, AmdIntrinsics = 16, NvIntrinsics = 32;

        /// <param name="usage">EShaderResourceUsageFlags (5.1+): 1 bindless resources, 2 bindless samplers, 3 root constants
        /// (5.4+), 6 diagnostic buffer (5.5+)</param>
        internal static int Of(byte usage, Dictionary<char, byte[]> d)
        {
            // 'x' FShaderCodeFeatures (5.0+; uint16 from 5.3): 4 diagnostic buffer (until 5.4), 5/6 bindless resources/samplers
            var x = d.TryGetValue('x', out var xs) && xs.Length > 0 ? xs[0] | (xs.Length > 1 ? xs[1] << 8 : 0) : 0;
            var f = 0;
            if ((usage & 2) != 0 || (x & 0x20) != 0) f |= BindlessResources;
            if ((usage & 4) != 0 || (x & 0x40) != 0) f |= BindlessSamplers;
            if ((usage & 8) != 0) f |= RootConstants;
            if ((usage & 0x40) != 0 || (x & 0x10) != 0) f |= DiagnosticBuffer;
            // 'v' TArray<FShaderCodeVendorExtension>: int32 count, then {uint32 vendor, uint16 x3, bool (4 bytes), uint8 type}
            if (d.TryGetValue('v', out var v) && v.Length >= 8)
                for (var o = 4; o + 4 <= v.Length && o < 4 + 15 * BitConverter.ToInt32(v, 0); o += 15)
                    f |= BitConverter.ToUInt32(v, o) switch { 0x1002 => AmdIntrinsics, 0x10DE => NvIntrinsics, _ => 0 };
            return f;
        }
    }

    /// <summary>A mesh shader's per-primitive outputs (PSG1) follow its per-vertex ones in <see cref="ShaderInfo.Outputs"/>
    /// at this register plus their row: their rows count from 0, and a PS packs them after its own per-vertex inputs.</summary>
    public const int PrimitiveRow = 0x10000;

    /// <summary>Signature elements a part may hold (the runtime's limit is 32 registers x 4 components per stream): more is a
    /// malformed container (ArgumentException).</summary>
    public const int MaxSigElements = 1024;

    /// <summary>Bytes of distinct names a container's part may decode: its distinct names fit in it, so more means names
    /// that overlap (each a suffix of a longer one) and would decode into far more strings than the part holds.</summary>
    internal static long NameBudget(int partLength) => 2L * partLength + (64 << 10);

    /// <summary>The ASCII string at <paramref name="at"/> of its string table <paramref name="d"/>, up to its NUL, of any
    /// length; ArgumentException when it starts outside the table, runs past it, or would take <paramref name="budget"/>
    /// below 0. Callers cache names by offset, so a shared name is decoded once.</summary>
    static string Name(ReadOnlySpan<byte> d, int at, ref long budget)
    {
        if ((uint)at > (uint)d.Length) throw new ArgumentException("a name outside its string table");
        var n = d[at..].IndexOf((byte)0);
        if (n < 0) throw new ArgumentException("a name without its end");
        if ((budget -= 2L * n) < 0) throw new ArgumentException("names over the part's budget");   // UTF-16 once decoded
        return Encoding.ASCII.GetString(d.Slice(at, n));
    }

    /// <summary>Stage, shader model, signatures and bindings (DXIL: PSV0; DXBC: SHEX/SHDR dcl_*, UE strips RDEF).
    /// Null when the program part is missing or of a kind D3D12 PSOs don't take.</summary>
    public static ShaderInfo? Parse(ReadOnlySpan<byte> c, string sha1, ResourceCounts counts)
    {
        var sigs = new Dictionary<string, List<SigElement>>();
        foreach (var (k, size) in new[] { ("ISGN", 24), ("OSGN", 24), ("OSG5", 28), ("ISG1", 32), ("OSG1", 32), ("PSG1", 32) })
        {
            var d = Dxbc.Part(c, Encoding.ASCII.GetBytes(k));
            if (d.IsEmpty) continue;
            var count = U(d, 0);
            if (count > MaxSigElements || U(d, 4) + (long)count * size > d.Length) throw new ArgumentException($"{k}: {count} elements don't fit the part");
            var list = new List<SigElement>();
            var names = new Dictionary<int, string>();   // elements may share a name
            var budget = NameBudget(d.Length);
            for (var e = 0; e < (int)count; e++)
            {
                var o = (int)U(d, 4) + e * size;
                var b = size == 24 ? o - 4 : o; // 24-byte elements have no leading Stream field
                var nameOff = (int)U(d, b + 4);
                if (!names.TryGetValue(nameOff, out var name)) names[nameOff] = name = Name(d, nameOff, ref budget);
                // next to the declared mask: an input's read components (ISGN ReadWriteMask, ISG1 AlwaysReads), an output's
                // written ones (the declared mask without its NeverWrites components); a PSG1 element's: unknown
                var read = k[0] == 'I' ? d[b + 25] : k == "PSG1" ? SigElement.UnknownReadMask : (byte)(d[b + 24] & ~d[b + 25]);
                list.Add(new SigElement(name, (int)U(d, b + 8), (int)U(d, b + 20), d[b + 24], (int)U(d, b + 12), (int)U(d, b + 16), read));
            }
            sigs[k[..2]] = list;
        }

        uint version;
        var gsInput = 0; // D3D_PRIMITIVE; DXIL InputPrimitive and the SM4/5 token use the same numbering
        var payload = 0;
        var bindings = new List<Binding>();
        if (Dxbc.Part(c, "DXIL"u8) is { IsEmpty: false } dxil)
        {
            version = U(dxil, 0); // DXIL program header: kind << 16 | major << 4 | minor
            if (Dxbc.Part(c, "PSV0"u8) is { IsEmpty: false } psv) // u32 RuntimeInfoSize, RuntimeInfo, u32 ResourceCount, u32 BindInfoSize, PSVResourceBindInfo[]
            {
                if (version >> 16 == 2 && U(psv, 0) >= 4) gsInput = (int)U(psv, 4); // RuntimeInfo starts with the stage union: GSInfo.InputPrimitive first
                if (version >> 16 == 14 && U(psv, 0) >= 4) payload = (int)U(psv, 4); // ASInfo.PayloadSizeInBytes
                if (version >> 16 == 13 && U(psv, 0) >= 12) payload = (int)U(psv, 12); // MSInfo: group shared, view-ID group shared, PayloadSizeInBytes
                var o = 4 + (int)U(psv, 0);
                var n = (int)U(psv, o);
                var stride = n > 0 ? (int)U(psv, o + 4) : 0; // DXC writes the stride only when there are resources
                for (var i = 0; i < n && stride >= 16; i++) // a record reads 16 bytes: a shorter stride isn't PSV0
                {
                    var r = o + 8 + i * stride;
                    var cls = U(psv, r) switch { 1 => "sampler", 2 => "cbv", 3 or 4 or 5 => "srv", 6 or 7 or 8 or 9 => "uav", _ => "unknown" };
                    var (lo, hi) = (U(psv, r + 8), U(psv, r + 12));
                    bindings.Add(new Binding(cls, (int)U(psv, r + 4), (int)lo, hi == uint.MaxValue ? -1 : (int)(hi - lo + 1)));
                }
            }
        }
        else if ((Dxbc.Part(c, "SHEX"u8) is { IsEmpty: false } sm5 ? sm5 : Dxbc.Part(c, "SHDR"u8)) is { IsEmpty: false } shex)
        {
            var t = MemoryMarshal.Cast<byte, uint>(shex);
            version = t[0]; // D3D10_SB_TOKENIZED_PROGRAM_TYPE: same 0-5 numbering as DXIL
            var sm51 = ((version >> 4) & 0xF) == 5 && (version & 0xF) >= 1;
            for (var p = 2; p < t.Length;)
            {
                var op = t[p] & 0x7FF;
                var n = op == 0x35 ? (int)t[p + 1] : (int)((t[p] >> 24) & 0x7F); // 0x35 = customdata: length in next token
                if (n <= 0 || p + n > t.Length) break;
                if (op == 0x5D) gsInput = (int)((t[p] >> 11) & 0x3F); // dcl_inputprimitive: D3D10_SB_GS_INPUT_PRIMITIVE_MASK 0x1f800 (patches need 6 bits)
                var cls = op switch { 0x59 => "cbv", 0x5A => "sampler", 0x58 or 0xA1 or 0xA2 => "srv", 0x9C or 0x9D or 0x9E => "uav", _ => null };
                if (cls != null)
                {
                    // extended tokens only within the instruction: a chain past it would be followed into every instruction after
                    var (q, end) = (p + 1, p + n);
                    if ((t[p] & 0x80000000) != 0) while (q < end && (t[q++] & 0x80000000) != 0) { } // extended opcode tokens
                    var operand = q < end ? t[q++] : 0;
                    if ((operand & 0x80000000) != 0) while (q < end && (t[q++] & 0x80000000) != 0) { } // extended operand tokens
                    // SM5.1: operand indices are (range id, lower, upper), register space = the instruction's last token; SM5.0: index 0 = register
                    if (q + (sm51 ? 2 : 0) < end)
                        bindings.Add(sm51 ? new Binding(cls, (int)t[end - 1], (int)t[q + 1], t[q + 2] == uint.MaxValue ? -1 : (int)(t[q + 2] - t[q + 1] + 1))
                                          : new Binding(cls, 0, (int)t[q], 1));
                }
                p += n;
            }
        }
        else return null;

        var kind = (int)(version >> 16);
        if (kind == 6 && bindings.Count == 0) bindings.AddRange(Rdat(c).Resources); // a DXIL library has no PSV0: its resources are in RDAT
        Stage? stage = kind switch
        {
            0 => Stage.Pixel, 1 => Stage.Vertex, 2 => Stage.Geometry, 3 => Stage.Hull, 4 => Stage.Domain, 5 => Stage.Compute,
            6 => Stage.Library, 13 => Stage.Mesh, 14 => Stage.Amplification, _ => null,
        };
        if (stage == null) return null;
        if (sigs.Remove("PS", out var prim) && stage == Stage.Mesh)
            sigs["OS"] = [.. sigs.GetValueOrDefault("OS") ?? [], .. prim.Select(e => e with { Register = PrimitiveRow | (e.Register & 0xFFFF) })];
        string[] prefix = ["ps", "vs", "gs", "hs", "ds", "cs", "lib", "", "", "", "", "", "", "ms", "as"];
        if (stage == Stage.Pixel && sigs.TryGetValue("IS", out var ins) && InputInterpolation(c) is { Count: > 0 } modes) // DXIL only
            sigs["IS"] = ins.Select(e => modes.TryGetValue((e.Semantic, e.Index), out var m) ? e with { Interpolation = m } : e).ToList();
        return new ShaderInfo(sha1, stage.Value, $"{prefix[kind]}_{(version >> 4) & 0xF}_{version & 0xF}", c.Length, counts, bindings,
            sigs.GetValueOrDefault("IS") ?? [], sigs.GetValueOrDefault("OS") ?? [], stage == Stage.Geometry ? gsInput : 0, InlineRayTracing: Dxbc.InlineRayTracing(c), Payload: payload);
    }

    /// <summary>A DXIL pixel shader's interpolation mode per named input (PSV0 signature elements: (semantic, index), the
    /// semantic compared without case -> D3D_INTERPOLATION_MODE-like DXIL mode: 1 constant, 2 linear, 3 linear centroid, 4 noperspective, ...);
    /// empty for DXBC or without PSV0. System values (no name there) are left out.</summary>
    public static Dictionary<(string Semantic, int Index), int> InputInterpolation(ReadOnlySpan<byte> c)
    {
        var modes = new Dictionary<(string Semantic, int Index), int>(SemanticKey.Instance);
        var v = Dxbc.Part(c, "PSV0"u8);
        if (4 + 30 > v.Length) return modes;                        // no RuntimeInfo1: no signature elements
        var inputs = v[4 + 28];                                     // RuntimeInfo1.SigInputElements
        if (inputs == 0) return modes;                              // the part may end before the element size
        var p = 4 + (int)U(v, 0);                                   // PSVRuntimeInfo (0..3)
        var resources = (int)U(v, p); p += 4;
        if (resources > 0) p += 4 + resources * (int)U(v, p);      // bind info size, then the bindings
        var (strings, stringsSize) = (p + 4, (int)U(v, p)); p = strings + stringsSize;
        if (p > v.Length) return modes;
        var table = v.Slice(strings, stringsSize);
        var budget = NameBudget(stringsSize);
        var names = new Dictionary<int, string>();
        var indexes = p + 4; p = indexes + 4 * (int)U(v, p);
        var size = (int)U(v, p); p += 4;                            // PSVSignatureElement0: 16 bytes
        for (var e = 0; e < inputs && size > 0 && p + (e + 1) * size <= v.Length; e++)
        {
            var q = p + e * size;
            var at = (int)U(v, q);
            if (!names.TryGetValue(at, out var semantic)) names[at] = semantic = Name(table, at, ref budget);
            if (semantic.Length == 0) continue; // a system value
            for (var r = 0; r < v[q + 8]; r++) // an element of several rows (TEXCOORD10 + TEXCOORD11) lists one index per row
                modes[(semantic, (int)U(v, indexes + 4 * ((int)U(v, q + 4) + r)))] = v[q + 13];
        }
        return modes;
    }

    /// <summary>(semantic, index) keys with the semantic compared without case, as the runtime links them; the name isn't copied.</summary>
    sealed class SemanticKey : IEqualityComparer<(string Semantic, int Index)>
    {
        public static readonly SemanticKey Instance = new();
        public bool Equals((string Semantic, int Index) a, (string Semantic, int Index) b) => a.Index == b.Index && StringComparer.OrdinalIgnoreCase.Equals(a.Semantic, b.Semantic);
        public int GetHashCode((string Semantic, int Index) k) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Semantic), k.Index);
    }

    /// <summary>One function of a DXIL library (RDAT function table): its DXIL shader kind (7 ray generation, 8 intersection,
    /// 9 any hit, 10 closest hit, 11 miss, 12 callable; others aren't ray tracing entry points), unmangled name, and the
    /// payload / attribute sizes it declares.</summary>
    public sealed record LibraryFunction(int Kind, string Name, int Payload, int Attributes);

    /// <summary>A DXIL library's RDAT part (runtime data, version 1.0: string buffer, index arrays, resource and function
    /// tables): its functions and every resource they bind. Empty when the container has none.</summary>
    public static (List<LibraryFunction> Functions, List<Binding> Resources) Rdat(ReadOnlySpan<byte> c)
    {
        var fns = new List<LibraryFunction>();
        var res = new List<Binding>();
        var r = Dxbc.Part(c, "RDAT"u8).ToArray();
        if (r.Length == 0) return (fns, res);
        var parts = new Dictionary<uint, int>(); // part type -> data offset
        for (var p = 0; p < (int)U(r, 4); p++) { var o = (int)U(r, 8 + 4 * p); parts[U(r, o)] = o + 8; }
        if (!parts.TryGetValue(1, out var strings)) return (fns, res);
        var stringsSize = (int)U(r, strings - 4);
        if ((uint)stringsSize > (uint)(r.Length - strings)) throw new ArgumentException("RDAT: the string table runs past the part");
        var budget = NameBudget(stringsSize);
        var names = new Dictionary<uint, string>();
        string Str(uint off) => names.TryGetValue(off, out var s) ? s : names[off] = Name(r.AsSpan(strings, stringsSize), (int)off, ref budget);
        if (parts.TryGetValue(3, out var rt)) // RuntimeDataResourceInfo: class, kind, id, space, lower, upper, name, flags
            for (var k = 0; k < (int)U(r, rt) && (int)U(r, rt + 4) >= 24; k++)
            {
                var q = rt + 8 + k * (int)U(r, rt + 4);
                var cls = U(r, q) switch { 0 => "srv", 1 => "uav", 2 => "cbv", 3 => "sampler", _ => "unknown" };
                var (lo, hi) = (U(r, q + 16), U(r, q + 20));
                res.Add(new Binding(cls, (int)U(r, q + 12), (int)lo, hi == uint.MaxValue ? -1 : (int)(hi - lo + 1)));
            }
        if (parts.TryGetValue(4, out var ft)) // RuntimeDataFunctionInfo: name, unmangled name, resources, dependencies, kind, payload, attributes, ...
            for (var k = 0; k < (int)U(r, ft) && (int)U(r, ft + 4) >= 28; k++)
            {
                var q = ft + 8 + k * (int)U(r, ft + 4);
                fns.Add(new LibraryFunction((int)U(r, q + 16), Str(U(r, q + 4)), (int)U(r, q + 20), (int)U(r, q + 24)));
            }
        return (fns, res);
    }

    static uint U(ReadOnlySpan<byte> s, int o) => BitConverter.ToUInt32(s.Slice(o, 4));
}
