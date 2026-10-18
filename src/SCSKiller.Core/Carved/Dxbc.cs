using System.Buffers.Binary;

namespace SCSKiller.Core.Carved;

/// <summary>DXBC/DXIL container checks for carving raw game files: header,
/// chunk table inside the container, FourCC chunk names. Layout: "DXBC", 16-byte checksum, u32 1, u32 size, u32 chunk count,
/// u32 chunk offsets; each chunk = FourCC, u32 length, data.</summary>
public static class Dxbc
{
    public const int MaxSize = 64 << 20;

    /// <summary>Container size from a 32-byte header, or 0 when the header can't start a container.</summary>
    public static int HeaderSize(ReadOnlySpan<byte> h)
    {
        if (h.Length < 32 || !h.StartsWith("DXBC"u8)) return 0;
        uint ver = U(h, 20), size = U(h, 24), n = U(h, 28);
        return ver == 1 && size > 32 && size <= MaxSize && n is >= 1 and <= 64 && 32 + 4 * n <= size ? (int)size : 0;
    }

    /// <summary>The whole container (exactly its size) is well formed.</summary>
    public static bool Valid(ReadOnlySpan<byte> c)
    {
        if (c.IsEmpty || HeaderSize(c) != c.Length) return false;
        for (var k = 0; k < (int)U(c, 28); k++)
        {
            long co = U(c, 32 + 4 * k);
            if (co + 8 > c.Length) return false;
            foreach (var b in c.Slice((int)co, 4)) if (b is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9')) return false;
            if (co + 8 + U(c, (int)co + 4) > c.Length) return false;
        }
        return true;
    }

    /// <summary>Every valid container in the bytes, in order; the bytes inside one aren't searched.</summary>
    public static IEnumerable<(int Offset, byte[] Container)> Containers(byte[] b)
    {
        for (var i = 0; b.AsSpan(i).IndexOf("DXBC"u8) is var k and >= 0;)
        {
            var at = i + k;
            var size = HeaderSize(b.AsSpan(at));
            if (size > 0 && at + size <= b.Length && Valid(b.AsSpan(at, size)))
            {
                yield return (at, b[at..(at + size)]);
                i = at + size;
            }
            else i = at + 1;
        }
    }

    /// <summary>Data of the first chunk named <paramref name="fourcc"/> (valid containers only), empty if none.</summary>
    public static ReadOnlySpan<byte> Part(ReadOnlySpan<byte> c, ReadOnlySpan<byte> fourcc)
    {
        for (var k = 0; k < (int)U(c, 28); k++)
        {
            var co = (int)U(c, 32 + 4 * k);
            if (c.Slice(co, 4).SequenceEqual(fourcc)) return c.Slice(co + 8, (int)U(c, co + 4));
        }
        return default;
    }

    /// <summary>Program type from SHEX/SHDR or the DXIL program header (0 ps, 1 vs, 2 gs, 3 hs, 4 ds, 5 cs, 6 lib, 13 ms,
    /// 14 as); -1 = no program (e.g. a root-signature-only container).</summary>
    public static int Kind(ReadOnlySpan<byte> c)
    {
        foreach (var name in Programs)
            if (Part(c, name) is { Length: >= 4 } p) return (int)(U(p, 0) >> 16);
        return -1;
    }

    static readonly byte[][] Programs = ["DXIL"u8.ToArray(), "SHEX"u8.ToArray(), "SHDR"u8.ToArray()];

    /// <summary>A DXIL shader's required wave lane range ([WaveSize], SM 6.6+) from PSV0's runtime info (after the 16-byte
    /// stage union: MinimumExpectedWaveLaneCount, MaximumExpectedWaveLaneCount); null = no requirement.</summary>
    public static (uint Min, uint Max)? WaveLanes(ReadOnlySpan<byte> c)
    {
        var psv = Part(c, "PSV0"u8);
        if (psv.Length < 28 || U(psv, 0) < 24) return null;
        var (min, max) = (U(psv, 20), U(psv, 24));
        return min == 0 && max == uint.MaxValue ? null : (min, max);
    }

    /// <summary>A DXIL shader that traces rays inline (RayQuery): SFI0's D3D_SHADER_REQUIRES_RAYTRACING_TIER_1_1.</summary>
    public static bool InlineRayTracing(ReadOnlySpan<byte> c) =>
        Valid(c) && Part(c, "SFI0"u8) is { Length: >= 8 } f && (BinaryPrimitives.ReadUInt64LittleEndian(f) & 0x100000) != 0;

    /// <summary>A serialized root signature (D3D12SerializeRootSignature output): a container of RTS0 chunks only, no
    /// program. Generated binding metadata, never shader code: the only blobs packs and hash-only recordings keep.</summary>
    public static bool IsRootSignatureOnly(ReadOnlySpan<byte> blob)
    {
        if (!Valid(blob)) return false;
        var n = (int)U(blob, 28);
        for (var i = 0; i < n; i++)
            if (!blob.Slice((int)U(blob, 32 + 4 * i), 4).SequenceEqual("RTS0"u8)) return false;
        return n > 0;
    }

    /// <summary>A serialized root signature (its first RTS0 part, or the bytes themselves when they aren't a container)
    /// that the D3D12 runtime would create: a 1.0, 1.1 or 1.2 header, every table, range, root constant, root descriptor
    /// and static sampler within the part, and the rules of DXC's DxilRootSignatureValidator (flags, visibilities,
    /// range types and flags, descriptor counts and offsets, sampler enums and LOD values, no register overlapping another
    /// of its type and space that the same stage sees). The system's reserved register spaces pass, as the runtime's
    /// D3D12SerializeVersionedRootSignature lets them. Ported in records.rs.</summary>
    /// <summary>Parameters, descriptor ranges and static samplers in one root signature, at most. Real ones: 45 (23
    /// parameters, 18 ranges, 6 samplers in 1,643 signatures of six games); the runtime's limits: 64 DWORDs of parameters,
    /// 2,032 static samplers. Same value in records.rs.</summary>
    public const int MaxRootSignatureEntries = 4096;

    public static bool RootSignatureValid(ReadOnlySpan<byte> blob)
    {
        var b = !blob.StartsWith("DXBC"u8) ? blob : Valid(blob) ? Part(blob, "RTS0"u8) : default;
        if (b.Length < 24) return false;
        var ver = U(b, 0);
        // D3D12_ROOT_SIGNATURE_FLAGS through SAMPLER_HEAP_DIRECTLY_INDEXED, and ALLOW_LOW_TIER_RESERVED_HW_CB_LIMIT
        if (ver is < 1 or > 3 || (U(b, 20) & ~0x80000FFFu) != 0) return false;
        // LOCAL_ROOT_SIGNATURE: no other flag, and every parameter and sampler visible to all
        var local = (U(b, 20) & 0x80) != 0;
        if (local && (U(b, 20) & ~0x80u) != 0) return false;
        var n = (ulong)b.Length;
        bool Fits(ulong at, ulong count, ulong size) => at + count * size <= n;
        var regs = new List<(uint Vis, uint Type, uint Space, uint Lb, uint Ub)>();
        void Reg(uint vis, uint type, uint space, uint lb, uint num) =>
            regs.Add((vis, type, space, lb, num == uint.MaxValue ? uint.MaxValue : lb + (num - 1)));
        // DATA_VOLATILE 2, DATA_STATIC_WHILE_SET_AT_EXECUTE 4, DATA_STATIC 8: at most one
        static bool OneData(uint flags) => ((flags & 0xE) & ((flags & 0xE) - 1)) == 0;

        uint count = U(b, 4), at = U(b, 8);
        if (!Fits(at, count, 12)) return false;
        // parameters, ranges and static samplers together, counted before any is read: tables may share their ranges, so
        // a few KB could name millions
        var entries = (ulong)count + U(b, 12);
        if (entries > MaxRootSignatureEntries) return false;
        for (var i = 0u; i < count; i++)
        {
            uint type = U(b, (int)(at + 12 * i)), vis = U(b, (int)(at + 12 * i + 4)), p = U(b, (int)(at + 12 * i + 8));
            if (vis > 7 || local && vis != 0) return false;   // ALL .. MESH
            if (type == 0)   // descriptor table {count, offset}
            {
                if (!Fits(p, 1, 8)) return false;
                uint ranges = U(b, (int)p), first = U(b, (int)p + 4), size = ver == 1 ? 20u : 24u;
                if ((entries += ranges) > MaxRootSignatureEntries || !Fits(first, ranges, size)) return false;
                bool samplers = false, resources = false;
                ulong append = 0;
                for (var r = 0u; r < ranges; r++)
                {
                    var q = (int)(first + size * r);
                    uint kind = U(b, q), num = U(b, q + 4), lb = U(b, q + 8), space = U(b, q + 12);
                    uint flags = ver == 1 ? 0 : U(b, q + 16), offset = U(b, q + (ver == 1 ? 16 : 20));
                    if (kind > 3 || num == 0) return false;   // SRV, UAV, CBV, SAMPLER
                    if (kind == 3) samplers = true; else resources = true;
                    if (samplers && resources) return false;
                    ulong start = offset == uint.MaxValue ? append : offset;   // D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND
                    if (start > uint.MaxValue) return false;   // appended after an unbounded range
                    if (num == uint.MaxValue) append = 1ul + uint.MaxValue;
                    else if ((ulong)lb + num - 1 > uint.MaxValue || start + num - 1 > uint.MaxValue) return false;
                    else append = start + num;
                    // DESCRIPTORS_VOLATILE 1, the DATA_* flags, DESCRIPTORS_STATIC_KEEPING_BUFFER_BOUNDS_CHECKS 0x10000 (not on a
                    // sampler range, nor with DESCRIPTORS_VOLATILE)
                    if ((flags & ~0x1000Fu) != 0 || (kind == 3 ? (flags & 0x1000E) != 0 : !OneData(flags) || (flags & 9) == 9)
                        || (flags & 0x10001) == 0x10001) return false;
                    Reg(vis, kind, space, lb, num);
                }
            }
            else if (type == 1)   // root constants {register, space, values}: a CBV
            {
                if (!Fits(p, 1, 12)) return false;
                Reg(vis, 2, U(b, (int)p + 4), U(b, (int)p), 1);
            }
            else if (type <= 4)   // root CBV, SRV, UAV {register, space[, flags]}
            {
                if (!Fits(p, 1, ver == 1 ? 8u : 12u)) return false;
                var flags = ver == 1 ? 0 : U(b, (int)p + 8);
                if ((flags & ~0xEu) != 0 || !OneData(flags)) return false;
                Reg(vis, type == 2 ? 2u : type == 3 ? 0u : 1u, U(b, (int)p + 4), U(b, (int)p), 1);
            }
            else return false;
        }

        // D3D12_STATIC_SAMPLER_DESC (52 bytes), DESC1 in 1.2 (+ flags: UINT_BORDER_COLOR 1, NON_NORMALIZED_COORDINATES 2)
        uint samplerCount = U(b, 12), samplerAt = U(b, 16), stride = ver == 3 ? 56u : 52u;
        if (!Fits(samplerAt, samplerCount, stride)) return false;
        for (var s = 0u; s < samplerCount; s++)
        {
            var q = (int)(samplerAt + stride * s);
            // a D3D12_FILTER: one of nine base filters, plain, comparison, minimum or maximum
            uint filter = U(b, q), reduction = filter >> 7;
            if (reduction > 3 || (filter & 0x7F) is not (0 or 1 or 4 or 5 or 0x10 or 0x11 or 0x14 or 0x15 or 0x55)) return false;
            for (var a = 4; a <= 12; a += 4)
                if (U(b, q + a) is < 1 or > 5) return false;   // WRAP .. MIRROR_ONCE
            float bias = F(b, q + 16), minLod = F(b, q + 32), maxLod = F(b, q + 36);
            if (float.IsNaN(bias) || bias < -16f || bias > 15.99f || U(b, q + 20) > 16 || reduction == 1 && U(b, q + 24) is < 1 or > 8
                || float.IsNaN(minLod) || float.IsNaN(maxLod) || U(b, q + 48) > 7 || local && U(b, q + 48) != 0) return false;
            var sflags = ver == 3 ? U(b, q + 52) : 0;
            // UINT_BORDER_COLOR: a border color that is one (transparent black, opaque black or white uint); NON_NORMALIZED_
            // COORDINATES: point or min-linear-mag-mip-point, not comparison, U and V clamped or bordered, a known border
            // color, no bias, LODs 0
            if ((sflags & ~3u) != 0 || (sflags & 1) != 0 && U(b, q + 28) is not (0 or 3 or 4)
                || (sflags & 2) != 0 && ((filter & 0x7F) is not (0 or 0x14) || reduction == 1 || U(b, q + 4) is not (3 or 4) || U(b, q + 8) is not (3 or 4) || U(b, q + 28) > 4
                    || bias != 0 || minLod != 0 || maxLod != 0)) return false;
            Reg(U(b, q + 48), 3, U(b, q + 44), U(b, q + 40), 1);
        }

        // a stage sees its own registers and those visible to all; sorted by start, a range overlaps when it starts at or
        // before the furthest end so far of its type and space
        for (var v = 0u; v <= 7; v++)
        {
            (uint Type, uint Space, ulong End)? last = null;
            foreach (var r in regs.Where(r => r.Vis == v || r.Vis == 0).OrderBy(r => r.Type).ThenBy(r => r.Space).ThenBy(r => r.Lb))
            {
                if (last is { } l && l.Type == r.Type && l.Space == r.Space)
                {
                    if (r.Lb <= l.End) return false;
                    last = (r.Type, r.Space, Math.Max(l.End, r.Ub));
                }
                else last = (r.Type, r.Space, r.Ub);
            }
        }
        return true;
    }

    /// <summary>Stages a graphics pipeline is built from (not compute, not DXR libraries).</summary>
    public static bool IsGraphics(int kind) => kind is >= 0 and <= 4 or 13 or 14;

    static uint U(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    static float F(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleLittleEndian(s[o..]);
}
