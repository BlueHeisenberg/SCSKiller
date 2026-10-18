using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Planning;

/// <summary><see cref="Dxbc.RootSignatureValid"/> against the runtime: each case is serialized by the system's
/// D3D12SerializeVersionedRootSignature (which validates its input) and encoded by hand as RTS0; the check accepts the
/// encoding exactly when the runtime serializes it (register spaces reserved for the system aside: both take them).</summary>
public class RootSignatureValidTests
{
    /// <summary>A 1.1 RTS0 part of <see cref="RootSig.Desc"/> rows (their encoding in <see cref="RootSig.Serialize"/>).</summary>
    static byte[] Encode(uint flags, List<uint[]> rows, byte[] samplers)
    {
        var payload = new List<uint>();
        var at = 24 + 12 * rows.Count;
        var parms = new List<uint>();
        foreach (var r in rows)
        {
            parms.AddRange([r[0], r[1], (uint)(at + 4 * payload.Count)]);
            if (r[0] == 0)
            {
                var count = (r.Length - 2) / 5;
                payload.AddRange([(uint)count, (uint)(at + 4 * payload.Count + 8)]);
                for (var j = 0; j < count; j++)
                    payload.AddRange([.. r.Skip(2 + 5 * j).Take(5), count == 1 ? r.Length == 8 ? r[7] : uint.MaxValue : 0]);
            }
            else payload.AddRange(r.Skip(2).Take(3));
        }
        var b = new byte[at + 4 * payload.Count + samplers.Length];
        uint[] head = [2, (uint)rows.Count, 24, (uint)(samplers.Length / 52), (uint)(at + 4 * payload.Count), flags];
        for (var i = 0; i < head.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), head[i]);
        for (var i = 0; i < parms.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24 + 4 * i), parms[i]);
        for (var i = 0; i < payload.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4 * i), payload[i]);
        samplers.CopyTo(b, at + 4 * payload.Count);
        return b;
    }

    static byte[] Sampler(params (int At, uint Value)[] edits)
    {
        var s = new byte[52];
        void U(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(o), v);
        U(0, 0x15); U(4, 1); U(8, 1); U(12, 1); U(20, 1);
        BinaryPrimitives.WriteSingleLittleEndian(s.AsSpan(36), float.MaxValue);
        foreach (var (o, v) in edits) U(o, v);
        return s;
    }

    static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    public static readonly TheoryData<string, uint, uint[][], byte[]> Cases = new()
    {
        { "empty", 0, [], [] },
        { "a table of SRVs", 0, [[0, 0, 0, 4, 0, 0, 0]], [] },
        { "an unbounded SRV range", 0, [[0, 0, 0, uint.MaxValue, 0, 0, 0]], [] },
        { "a range of no descriptors", 0, [[0, 0, 0, 0, 0, 0, 0]], [] },
        { "range type 4", 0, [[0, 0, 4, 1, 0, 0, 0]], [] },
        { "SRVs and samplers in one table", 0, [[0, 0, 0, 1, 0, 0, 0, 3, 1, 0, 0, 0]], [] },
        { "base + count past the registers", 0, [[0, 0, 0, 2, uint.MaxValue, 0, 0]], [] },
        { "range flags 0x20", 0, [[0, 0, 0, 1, 0, 0, 0x20]], [] },
        { "two DATA_* range flags", 0, [[0, 0, 0, 1, 0, 0, 0xA]], [] },
        { "DATA_STATIC with DESCRIPTORS_VOLATILE", 0, [[0, 0, 0, 1, 0, 0, 9]], [] },
        { "DESCRIPTORS_STATIC_KEEPING_BUFFER_BOUNDS_CHECKS", 0, [[0, 0, 0, 1, 0, 0, 0x10000]], [] },
        { "a sampler range with a DATA_* flag", 0, [[0, 0, 3, 1, 0, 0, 2]], [] },
        { "a sampler range DESCRIPTORS_VOLATILE", 0, [[0, 0, 3, 1, 0, 0, 1]], [] },
        { "root flags 0x1000", 0x1000, [], [] },
        { "ALLOW_LOW_TIER_RESERVED_HW_CB_LIMIT", 0x80000000, [], [] },
        { "visibility 8", 0, [[2, 8, 0, 0, 0]], [] },
        { "root descriptor flags 1", 0, [[2, 0, 0, 0, 1]], [] },
        { "root descriptor flags 6", 0, [[2, 0, 0, 0, 6]], [] },
        { "root descriptor DATA_STATIC", 0, [[2, 0, 0, 0, 8]], [] },
        { "two root CBVs at b0", 0, [[2, 0, 0, 0, 0], [2, 0, 0, 0, 0]], [] },
        { "b0 for the vertex and for the pixel shader", 0, [[2, 1, 0, 0, 0], [2, 5, 0, 0, 0]], [] },
        { "b0 for all and in a pixel table", 0, [[2, 0, 0, 0, 0], [0, 5, 2, 1, 0, 0, 0]], [] },
        { "b0 in two spaces", 0, [[2, 0, 0, 0, 0], [2, 0, 0, 1, 0]], [] },
        { "root constants and a root CBV at b0", 0, [[1, 0, 0, 0, 4], [2, 0, 0, 0, 0]], [] },
        { "a reserved register space", 0, [[0, 0, 0, 1, 0, 0xFFFFFFF0, 0]], [] },
        { "a sampler", 0, [], Sampler() },
        { "all-zero sampler", 0, [], new byte[52] },
        { "address 0", 0, [], Sampler((4, 0)) },
        { "address 6", 0, [], Sampler((12, 6)) },
        { "border color 9", 0, [], Sampler((4, 4), (28, 9)) },
        { "comparison filter, function 0", 0, [], Sampler((0, 0x95), (24, 0)) },
        { "plain filter, function 9", 0, [], Sampler((24, 9)) },
        { "maximum anisotropic", 0, [], Sampler((0, 0x1D5), (20, 16)) },
        { "anisotropy 17", 0, [], Sampler((20, 17)) },
        { "MinLOD over MaxLOD", 0, [], Sampler((32, Bits(5)), (36, Bits(1))) },
        { "MipLODBias 16", 0, [], Sampler((16, Bits(16))) },
        { "MipLODBias -16", 0, [], Sampler((16, Bits(-16))) },
        { "MipLODBias NaN", 0, [], Sampler((16, Bits(float.NaN))) },
        { "MaxLOD NaN", 0, [], Sampler((36, Bits(float.NaN))) },
        { "filter 2", 0, [], Sampler((0, 2)) },
        { "filter 0x200", 0, [], Sampler((0, 0x215)) },
        { "sampler visibility 8", 0, [], Sampler((48, 8)) },
        { "two samplers at s0", 0, [], [.. Sampler(), .. Sampler()] },
        { "s0 for the vertex and for the pixel shader", 0, [], [.. Sampler((48, 1)), .. Sampler((48, 5))] },
        { "a table sampler and a static sampler at s0", 0, [[0, 0, 3, 1, 0, 0, 0]], Sampler() },
        { "LOCAL_ROOT_SIGNATURE with ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT", 0x81, [], [] },
        { "LOCAL_ROOT_SIGNATURE with ALLOW_LOW_TIER_RESERVED_HW_CB_LIMIT", 0x80000080, [], [] },
        { "LOCAL_ROOT_SIGNATURE, a parameter for the pixel shader", 0x80, [[2, 5, 0, 0, 0]], [] },
        { "LOCAL_ROOT_SIGNATURE, a parameter for all", 0x80, [[2, 0, 0, 0, 0]], [] },
        { "LOCAL_ROOT_SIGNATURE, a sampler for the pixel shader", 0x80, [], Sampler((48, 5)) },
        { "an SRV range DESCRIPTORS_VOLATILE with bounds checks kept", 0, [[0, 0, 0, 1, 0, 0, 0x10001]], [] },
        { "a sampler range with bounds checks kept", 0, [[0, 0, 3, 1, 0, 0, 0x10000]], [] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void AcceptsWhatTheRuntimeSerializes(string name, uint flags, uint[][] rows, byte[] samplers)
    {
        bool runtime;
        try { RootSig.Serialize(new RootSig.Desc(flags, [.. rows]), samplers); runtime = true; }
        catch (InvalidOperationException) { runtime = false; }
        Assert.True(runtime == Dxbc.RootSignatureValid(Encode(flags, [.. rows], samplers)), $"{name}: the runtime {(runtime ? "serializes" : "refuses")} it");
    }

    /// <summary>A 1.2 signature's static samplers are 56-byte DESC1s: read with that stride, given to a 1.1 rebuild
    /// without their flags.</summary>
    [Fact]
    public void Version12SamplersAreReadWithTheirStride()
    {
        var b = new byte[24 + 2 * 56];
        foreach (var (at, v) in new[] { (0, 3u), (8, 24u), (12, 2u), (16, 24u) }) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        Sampler().CopyTo(b, 24);
        Sampler((0, 0x14), (40, 1), (48, 5)).CopyTo(b, 24 + 56);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24 + 56 + 52), 1);   // UINT_BORDER_COLOR
        Assert.True(Dxbc.RootSignatureValid(b));
        var samplers = RootSig.Samplers(b);
        Assert.Equal([.. Sampler(), .. Sampler((0, 0x14), (40, 1), (48, 5))], samplers);
        RootSig.Serialize(new RootSig.Desc(0, []), samplers);
        Assert.Contains((5u, 3u, 1u, 1u, 0u, false), RootSig.Parse(b).Slots);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24 + 56 + 52), 4);   // no such flag
        Assert.False(Dxbc.RootSignatureValid(b));
    }

    /// <summary>The runtime's verdict on a serialized root signature: D3D12CreateVersionedRootSignatureDeserializer reads
    /// it into a description, D3D12SerializeVersionedRootSignature (which validates) writes it back. System32's d3d12.dll.</summary>
    static unsafe bool RuntimeAccepts(byte[] part, out string? why)
    {
        var lib = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "d3d12.dll"));
        var create = (delegate* unmanaged<byte*, nuint, Guid*, nint*, int>)NativeLibrary.GetExport(lib, "D3D12CreateVersionedRootSignatureDeserializer");
        var serialize = (delegate* unmanaged<void*, nint*, nint*, int>)NativeLibrary.GetExport(lib, "D3D12SerializeVersionedRootSignature");
        var iid = new Guid("7F91CE67-090C-4BB7-B78E-ED8FF2E31DA0");   // ID3D12VersionedRootSignatureDeserializer
        nint d = 0;
        int hr;
        // the deserializer reads a container (it doesn't check the checksum)
        byte[] c = [.. "DXBC"u8, .. new byte[16], 1, 0, 0, 0, .. BitConverter.GetBytes(44 + part.Length), 1, 0, 0, 0, 36, 0, 0, 0, .. "RTS0"u8, .. BitConverter.GetBytes(part.Length), .. part];
        fixed (byte* p = c) hr = create(p, (nuint)c.Length, &iid, &d);
        why = null;
        if (hr < 0) { why = $"deserializer 0x{hr:x8}"; return false; }
        try
        {
            var vt = *(nint**)d;   // IUnknown, GetRootSignatureDescAtVersion, GetUnconvertedRootSignatureDesc
            var desc = ((delegate* unmanaged<nint, void*>)vt[4])(d);
            // the deserializer drops flags it doesn't know (ALLOW_LOW_TIER_RESERVED_HW_CB_LIMIT): the serializer gets the part's
            *(uint*)((byte*)desc + 40) = BinaryPrimitives.ReadUInt32LittleEndian(part.AsSpan(20));
            nint blob = 0, err = 0;
            hr = serialize(desc, &blob, &err);
            foreach (var u in new[] { blob, err }) if (u != 0) ((delegate* unmanaged<nint, uint>)(*(nint**)u)[2])(u);
            if (hr < 0) why = $"serializer 0x{hr:x8}";
            return hr >= 0;
        }
        finally { ((delegate* unmanaged<nint, uint>)(*(nint**)d)[2])(d); }
    }

    /// <summary>Thousands of random root signatures (<see cref="RootSignatureSweep"/>): the check and the runtime agree on
    /// every one. The same parts are in the conformance corpus, where the Rust build must give the same answers.</summary>
    [Fact]
    public void AgreesWithTheRuntimeOnARandomSweep()
    {
        int accepted = 0, falseAccepts = 0, falseRejects = 0;
        var examples = new List<string>();
        foreach (var part in RootSignatureSweep.Parts(RootSignatureSweep.Seed, RootSignatureSweep.Count))
        {
            var runtime = RuntimeAccepts(part, out var why);
            var ours = Dxbc.RootSignatureValid(part);
            if (runtime) accepted++;
            if (runtime == ours) continue;
            if (ours) falseAccepts++; else falseRejects++;
            if (examples.Count < 12) examples.Add($"{(ours ? "accepted" : "refused")} ({why}): {Convert.ToHexString(part)}");
        }
        Assert.True(falseAccepts + falseRejects == 0, $"{falseAccepts} false accepts, {falseRejects} false rejects of {RootSignatureSweep.Count}:\n" + string.Join("\n", examples));
        Assert.InRange(accepted, RootSignatureSweep.Count / 10, RootSignatureSweep.Count * 9 / 10);   // the sweep isn't all one answer
    }


    /// <summary>65 KB naming 14.9M ranges (5,454 tables sharing one 2,727-range table laid over the parameter table): refused
    /// by its count before a range is read, without allocating for them.</summary>
    [Fact]
    public void AliasedRangesAreRefusedByTheirCount()
    {
        var w = new List<uint> { 2, 5454, 24, 0, 65480, 0 };
        for (var i = 0; i < 5454; i++) w.AddRange([0, 1, 65472]);
        w.AddRange([2727, 24]);
        var part = new byte[4 * w.Count];
        for (var i = 0; i < w.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(part.AsSpan(4 * i), w[i]);
        Dxbc.RootSignatureValid(part);   // JIT
        var (before, time) = (GC.GetAllocatedBytesForCurrentThread(), System.Diagnostics.Stopwatch.StartNew());
        Assert.False(Dxbc.RootSignatureValid(part));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1 << 20);
        Assert.InRange(time.ElapsedMilliseconds, 0, 200);
        // at the cap: as many entries as allowed still pass
        Assert.True(Dxbc.RootSignatureValid(RootSignatureSweep.Encode(2, 0, [.. Enumerable.Range(0, Dxbc.MaxRootSignatureEntries).Select(i => ((uint)1, (uint)0, new List<uint> { (uint)i, 0, 1 }))], [])));
        Assert.False(Dxbc.RootSignatureValid(RootSignatureSweep.Encode(2, 0, [.. Enumerable.Range(0, Dxbc.MaxRootSignatureEntries + 1).Select(i => ((uint)1, (uint)0, new List<uint> { (uint)i, 0, 1 }))], [])));
    }

    /// <summary>The planner's own root signatures, every rule's static samplers with them, pass.</summary>
    [Fact]
    public void TheRuntimesOwnOutputPasses()
    {
        foreach (var rule in RulesFile.Embedded.Rules.Where(r => r.RootSignature != null))
            Assert.True(Dxbc.RootSignatureValid(RootSig.Serialize(new RootSig.Desc(1, [[0, 0, 0, 64, 0, 0, 5], [2, 0, 0, 0, 0]]), RootSig.StaticSamplers(rule))), rule.Id);
    }
}
