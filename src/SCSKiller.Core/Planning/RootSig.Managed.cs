using System.Buffers.Binary;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Planning;

public static unsafe partial class RootSig
{
    /// <summary>D3D12SerializeVersionedRootSignature for a 1.0 or 1.1 description, without the runtime: the RTS0 layout of DXC's
    /// serializer (header, parameters, then each parameter's table and ranges, root constants or descriptor in turn, then the
    /// static samplers) in a one-part container with the runtime's checksum. Refused, as E_INVALIDARG, when
    /// <see cref="Dxbc.RootSignatureValid"/> refuses it.</summary>
    internal static byte[] SerializeManaged(byte* desc)
    {
        // D3D12_VERSIONED_ROOT_SIGNATURE_DESC (x64): version @0, NumParameters @8, pParameters @16, NumStaticSamplers @24,
        // pStaticSamplers @32, Flags @40
        var ver = *(uint*)desc;
        if (ver is not (1 or 2)) throw Refused();
        uint n = *(uint*)(desc + 8), samplers = *(uint*)(desc + 24);
        byte* parms = *(byte**)(desc + 16), sampler = *(byte**)(desc + 32);
        if ((ulong)n + samplers > Dxbc.MaxRootSignatureEntries) throw Refused();
        var u = new List<uint>((int)(6 + 3 * n)) { ver, n, 24, samplers, 0, *(uint*)(desc + 40) };
        u.AddRange(new uint[3 * n]);
        for (var i = 0; i < n; i++)
        {
            var p = parms + 32 * i;   // D3D12_ROOT_PARAMETER(1): type @0, union @8, visibility @24
            var type = *(uint*)p;
            (u[6 + 3 * i], u[7 + 3 * i], u[8 + 3 * i]) = (type, *(uint*)(p + 24), (uint)(4 * u.Count));
            var values = (uint*)(p + 8);
            switch (type)
            {
                case 0:   // a table {count, offset}, then its ranges: 1.0 type, count, base, space, offset; 1.1 flags before the offset
                    var count = *(uint*)(p + 8);
                    if (count > Dxbc.MaxRootSignatureEntries) throw Refused();
                    var ranges = *(uint**)(p + 16);
                    u.Add(count);
                    u.Add((uint)(4 * (u.Count + 1)));
                    for (var k = 0; k < count * (ver == 1 ? 5 : 6); k++) u.Add(ranges[k]);
                    break;
                case 1:   // root constants: register, space, values
                    u.AddRange([values[0], values[1], values[2]]);
                    break;
                case 2 or 3 or 4:   // a root CBV, SRV or UAV: register, space, 1.1 flags
                    u.AddRange(ver == 1 ? [values[0], values[1]] : [values[0], values[1], values[2]]);
                    break;
                default: throw Refused();
            }
        }
        u[4] = (uint)(4 * u.Count);
        var rts0 = new byte[4 * u.Count + 52 * samplers];
        for (var i = 0; i < u.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(rts0.AsSpan(4 * i), u[i]);
        new ReadOnlySpan<byte>(sampler, (int)(52 * samplers)).CopyTo(rts0.AsSpan(4 * u.Count));   // D3D12_STATIC_SAMPLER_DESC, as is
        if (!Dxbc.RootSignatureValid(rts0)) throw Refused();
        return Dxbc.Container("RTS0"u8, rts0);
    }

    static SerializeException Refused() => new("D3D12SerializeVersionedRootSignature failed 0x80070057");
}
