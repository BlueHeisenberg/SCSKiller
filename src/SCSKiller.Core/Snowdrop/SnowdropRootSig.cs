using System.Runtime.InteropServices;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Snowdrop;

/// <summary>The root signatures Snowdrop's D3D12 renderer creates (Star Wars Outlaws), one per pipeline kind, as its builder
/// lays them out in the exe (version 1.1, every parameter visible to all stages). Registers come from the engine's static
/// table of MR_* resources: a resource's register (or first bindless space) is its id times 20, its array size its count.
/// Parameters in order: a root CBV b0 space 2; a table of unbounded SRV and UAV ranges, one per bindless space, and the
/// vendor intrinsics' UAV; the CBV tables of MR_ConstantBuffer and of the global constant buffers; the SRV tables of the
/// global textures and buffers; a table of 4 samplers in space 0; then root SRVs (graphics: the instance data, instance
/// stream and render batch info; compute: those and one more). The engine's 32 samplers are static samplers in space 1.
/// Ranges are volatile, sampler ranges flagless. Flags: graphics allow the input layout and deny HS, DS and GS; compute deny
/// every graphics stage; both deny AS and MS on a GPU with mesh shaders.</summary>
public static class SnowdropRootSig
{
    const uint Srv = 0, Uav = 1, Cbv = 2, Sampler = 3, Unbounded = uint.MaxValue, Volatile = 1;

    /// <summary>The device reports mesh shaders, so the builder denies AS and MS on every non-mesh root signature.</summary>
    internal const bool MeshShaders = true;

    /// <summary>The u0 range in the vendor intrinsics' space (AGS_DX12_SHADER_INSTRINSICS_SPACE_ID), on NVIDIA too.</summary>
    const uint ExtensionSpace = 0x7FFF0ADE;

    // MR_* ids and array sizes, in id order: global textures (slots 16-120), global constant buffers (slots 4-15), global buffers (slots 19-31)
    const uint FirstTexture = 32, FirstConstants = 132, FirstBuffer = 153;
    static readonly uint[] TextureCounts = [.. Enumerable.Range(32, 97).Select(id => id switch { 35 or 37 => 3u, 45 or 88 or 125 or 126 => 2u, _ => 1u })];
    const uint Constants = 12, Buffers = 13;

    /// <summary>The setting's default, and what the rule assumes when the player's file doesn't say.</summary>
    public const int DefaultAnisotropy = 16;

    // the engine's 32 samplers s0-s31 in registration order: D3D12 filter, address U/V/W, whether it takes the anisotropy
    // setting (else 1), comparison function, static border color
    static readonly (uint Filter, uint U, uint V, uint W, bool Aniso, uint Compare, uint Border)[] Registered =
    [
        (0x55, 1, 1, 1, true, 1, 0), (0, 3, 3, 3, true, 1, 0), (0, 1, 1, 1, false, 1, 0), (0x14, 3, 3, 3, true, 1, 0),
        (0x55, 4, 4, 4, true, 1, 0), (0x15, 1, 3, 3, false, 1, 0), (0x15, 3, 3, 3, false, 1, 0), (0x94, 3, 3, 3, true, 5, 0),
        (0, 1, 1, 1, true, 1, 0), (0x15, 1, 1, 1, false, 1, 0), (0x55, 3, 3, 3, true, 1, 0), (0x14, 3, 3, 3, false, 1, 0),
        (0x14, 1, 1, 1, true, 1, 0), (0, 3, 3, 3, false, 1, 0), (0, 4, 4, 4, true, 1, 0), (0x80, 3, 3, 3, true, 5, 0),
        (0x55, 2, 2, 2, true, 1, 0), (0x55, 1, 3, 1, true, 1, 0), (0x55, 1, 3, 3, true, 1, 0), (0x55, 3, 3, 1, true, 1, 0),
        (0x55, 1, 1, 3, true, 1, 0), (0x55, 3, 3, 3, true, 1, 1), (0x55, 1, 4, 1, true, 1, 0), (0x55, 5, 5, 5, true, 1, 0),
        (1, 1, 1, 1, true, 1, 0), (0x55, 3, 1, 1, true, 1, 0), (0x55, 3, 1, 3, true, 1, 0), (0x14, 4, 3, 3, true, 1, 0),
        (0x55, 4, 4, 4, true, 1, 2), (0x14, 1, 3, 3, true, 1, 0), (0x14, 4, 4, 4, true, 1, 1), (0, 2, 2, 2, true, 1, 0),
    ];

    /// <summary>The 32 static samplers as raw D3D12_STATIC_SAMPLER_DESCs (LOD 0 to FLT_MAX, s0-s31 space 1) at the player's max
    /// anisotropy: the exe gives a registration of 0 anisotropy 1, else the lower of the registration and the setting (0xff: the
    /// setting). MipLODBias stays 0: an anisotropic sampler without a bias of its own reads a global whose writer isn't traced,
    /// 0 in a recording with DLSS on.</summary>
    public static byte[] Samplers(int anisotropy = DefaultAnisotropy) => MemoryMarshal.AsBytes(Registered.SelectMany((s, i) => new uint[]
        { s.Filter, s.U, s.V, s.W, 0, s.Aniso ? (uint)anisotropy : 1, s.Compare, s.Border, 0, 0x7F7FFFFF, (uint)i, 1, 0 }).ToArray().AsSpan()).ToArray();

    public static RootSig.Desc Build(IReadOnlyDictionary<Stage, ShaderInfo> stages)
    {
        if (stages.ContainsKey(Stage.Mesh) || stages.ContainsKey(Stage.Amplification)) throw new RootSig.SerializeException("Snowdrop mesh pipelines aren't rebuilt");
        var compute = stages.ContainsKey(Stage.Compute);
        var textures = new List<uint>();
        for (uint i = 0, offset = 0; i < TextureCounts.Length; offset += TextureCounts[i++])
            textures.AddRange([Srv, TextureCounts[i], (FirstTexture + i) * 20, 0, Volatile, offset]);
        List<uint[]> rows =
        [
            [2, 0, 0, 2, 0],
            Bindless(),
            Table([Cbv, 4, 2600, 0, Volatile, 0]),
            Table([.. Enumerable.Range(0, (int)Constants).SelectMany(i => new uint[] { Cbv, 1, (FirstConstants + (uint)i) * 20, 0, Volatile, (uint)i })]),
            Table([.. textures]),
            Table([.. Enumerable.Range(0, (int)Buffers).SelectMany(i => new uint[] { Srv, 1, (FirstBuffer + (uint)i) * 20, 0, Volatile, (uint)i })]),
            Table([Sampler, 4, 0, 0, 0, 0]),
        ];
        foreach (var t in compute ? [2940u, 2960, 2980, 3000] : new uint[] { 2940, 2960, 2980 }) rows.Add([3, 0, t, 0, 0]);
        var flags = (compute ? 0x3Eu : 0x1Du) | (MeshShaders ? 0x300u : 0);
        return new RootSig.Desc(flags, rows, RangeOffsets: true);
    }

    static uint[] Table(uint[] ranges) => [0, 0, .. ranges];

    static uint[] Bindless()
    {
        var r = new List<uint>();
        void Spaces(uint type, uint first, int count) { for (var i = 0u; i < count; i++) r.AddRange([type, Unbounded, 0, first + i, Volatile, 0]); }
        Spaces(Srv, 600, 16);    // MR_Texture
        Spaces(Srv, 3380, 14);
        Spaces(Srv, 2900, 15);   // MR_Buffer
        Spaces(Uav, 3340, 8);    // MR_RW_Texture
        Spaces(Uav, 3360, 8);    // MR_RW_Buffer
        r.AddRange([Uav, 1, 0, ExtensionSpace, Volatile, 0]);
        Spaces(Srv, 3400, 64);
        Spaces(Uav, 3464, 64);
        return Table([.. r]);
    }
}
