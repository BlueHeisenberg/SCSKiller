using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Anvil;

/// <summary>Descriptor-table layouts observed for this Valhalla shader build. The files select the layout by Forge
/// variant bit 12 or, for unambiguous compute families, by SBIN source name. Binding coverage is checked separately.</summary>
internal static class ValhallaRootProfiles
{
    internal sealed record Profile(string Hash, byte[] Blob, RootSig.Ranges Ranges)
    {
        internal bool Covers(ShaderInfo shader) => RootSig.Uncovered(Ranges, shader.Stage, shader) == null;
    }

    static readonly uint[][] StaticSamplers =
    [
        Sampler(0, 3, 4, 2, 0), Sampler(0, 1, 4, 2, 1), Sampler(149, 4, 4, 2, 6),
        Sampler(21, 2, 4, 2, 7), Sampler(21, 4, 4, 1, 8), Sampler(21, 3, 4, 2, 9), Sampler(85, 1, 4, 2, 10),
    ];

    static uint[] Sampler(uint filter, uint address, uint comparison, uint border, uint register) =>
        [filter, address, address, address, 0, 16, comparison, border, 0, 0x7f7fffff, register, 99, 0];

    internal static readonly Dictionary<string, Profile> Compute = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Profile ForgeRegular = Build(1, true,
        Table(2, 1, 0, 7), Table(3, 1, 0, 12), Table(4, 1, 3, 23), Samplers(5, 2), Table(6, 1, 10, 14), Samplers(7, 1));
    internal static readonly Profile ForgeBindless = Build(1, true,
        Table(2, 1, 0, 7), Bindless(40, 41, 42));

    static ValhallaRootProfiles()
    {
        Add(Build(0, false, [Table(0, 1, 6, 4), Samplers(98, 11)], StaticSamplers),
            "cloudscachesdownsample");
        Add(ComputeProfile(Table(2, 1, 3, 18), Samplers(3, 1), Table(4, 1, 10, 15), Samplers(5, 1), Table(6, 1, 0, 1)),
            "fftgenerator", "underwater", "watercompositing", "wateropticaldepth",
            "watersoundemitter", "waterutils");
        Add(ComputeProfile(Table(2, 1, 0, 7), Table(3, 1, 2, 2), Table(4, 1, 8, 7), Table(5, 1, 0, 0)),
            "rain", "rainsplashes");
        Add(ComputeProfile(Table(2, 1, 0, 7), Table(3, 1, 0, 0), Table(4, 1, 4, 7), Table(5, 1, 0, 2)),
            "clusteredlightingculling", "clusteredparticleculling");
        Add(ComputeProfile(Table(2, 1, 0, 0), Table(3, 1, 5, 4)),
            "grassforce");
        Add(ComputeProfile(Table(2, 1, 0, 7), Table(3, 1, 1, 3), Table(4, 1, 5, 12), Table(5, 1, 0, 0)),
            "batchcompaction", "instanceculling");
        Add(ComputeProfile(Table(2, 1, 0, 7), Table(3, 1, 2, 2)),
            "terrainclassifier");
        Add(ComputeProfile(Table(2, 1, 0, 4), Samplers(3, 1), Table(4, 1, 6, 9), Table(5, 1, 0, 1)),
            "givolumecascadesupdate");
        Add(ComputeProfile(Table(2, 1, 0, 0), Table(3, 1, 6, 8), Table(4, 1, 0, 1)),
            "cullinstances", "cullpassinstances", "expandbatches", "extractinstanceranges",
            "meshdistance", "updateandculllod");
        Add(ComputeProfile(Table(2, 1, 1, 22), Table(3, 1, 4, 6), Table(4, 1, 0, 2), Table(5, 1, 0, 9)),
            "deferredlights", "deferredprepass", "deferredsunlightshadowmask", "waterdownscaledepth");
        Add(ComputeProfile(Table(2, 1, 0, 7), Table(3, 1, 1, 28), Samplers(4, 2), Table(5, 1, 4, 9), Table(6, 1, 1, 0)),
            "terrainexport");
        Add(ComputeProfile(Table(2, 1, 1, 7), Table(3, 1, 2, 4), Bindless(50)),
            "skinning_compute");
        Add(ComputeProfile(Table(2, 1, 2, 12), Samplers(3, 2), Table(4, 1, 6, 9), Samplers(5, 1), Table(6, 1, 0, 1)),
            "generalizedblur", "snowremovermask", "temporalaa");
        Add(BareProfile(Table(0, 1, 6, 3), Samplers(1, 1)),
            "generatemips");
        Add(ComputeProfile(Table(2, 1, 0, 4), Samplers(3, 4), Table(4, 1, 1, 2)),
            "cloudshadow");
        Add(ComputeProfile(Table(2, 1, 0, 1), Table(3, 1, 1, 12), Table(4, 1, 3, 22), Samplers(5, 2)),
            "cubemapblend", "cubemapfiltering", "cubemapfog", "cubemapgi",
            "cubemaplighting");
        Add(ComputeProfile(Table(2, 1, 0, 7), Table(3, 1, 0, 12), Table(4, 1, 3, 23), Samplers(5, 2), Table(6, 1, 10, 14), Samplers(7, 1)),
            "adaptluminance", "bincapsulecommon", "clouds", "cloudscaches",
            "cloudscompositing", "colorlut", "deferredlightingtiling", "distancefield",
            "dynamicgbuffercubemaputils", "firedecal", "generalizedblurprepare", "gidecoder",
            "girenderer", "gtao", "lightgroup", "materialtypetiling",
            "onepassblur", "refractioncapture", "rounddepthoffield", "splashlighting",
            "terraindeferredgbuffer", "updateindirectiontexture", "visibilitydepthreproject");
        Add(ComputeProfile(Table(2, 1, 0, 1), Samplers(3, 2), Table(4, 1, 5, 13), Samplers(5, 1), Table(6, 1, 0, 2), Table(7, 1, 0, 6)),
            "volumetricfog", "volumetricfogshadowmap", "volumetricfogtileclassification");
        Add(ComputeProfile(Table(2, 1, 2, 25), Samplers(3, 4), Table(4, 1, 0, 0)),
            "localizedskylighting");
        Add(ComputeProfile(Table(2, 1, 1, 7), Samplers(3, 2), Table(4, 1, 2, 4), Samplers(5, 1), Table(6, 1, 0, 0)),
            "skylookups");
        Add(ComputeProfile(Table(2, 0, 1, 0), Table(3, 1, 0, 0), Table(4, 1, 0, 10), Table(5, 1, 0, 34)),
            "waterquery");
        Add(ComputeProfile(Table(2, 1, 0, 0), Table(3, 1, 2, 11)),
            "pointcloud");
        Add(BareProfile(Table(0, 1, 6, 4)),
            "internalshaders");
    }

    internal static IEnumerable<Profile> All => Compute.Values.Append(ForgeRegular).Append(ForgeBindless).DistinctBy(p => p.Hash);

    static void Add(Profile profile, params string[] sources)
    {
        foreach (var source in sources) Compute.Add(source + ".hlsl", profile);
    }

    static Profile ComputeProfile(params uint[][] tables) => Build(0, true, tables);
    static Profile BareProfile(params uint[][] tables) => Build(0, false, tables);
    static Profile Build(uint flags, bool standard, params uint[][] tables) => Build(flags, standard, tables, standard ? StaticSamplers : []);

    static uint[] Table(uint space, uint cbv, uint uav, uint srv)
    {
        var ranges = new List<uint>();
        if (cbv != 0) ranges.AddRange([2, cbv, 0, space, 2, uint.MaxValue]);
        if (uav != 0) ranges.AddRange([1, uav, 0, space, 1, uint.MaxValue]);
        if (srv != 0) ranges.AddRange([0, srv, 0, space, 1, uint.MaxValue]);
        return [.. ranges];
    }

    static uint[] Samplers(uint space, uint count) => [3, count, 0, space, 1, uint.MaxValue];
    static uint[] Bindless(params uint[] spaces) => [.. spaces.SelectMany(space => new uint[] { 0, uint.MaxValue, 0, space, 1, 0 })];

    // D3D12 root signature 1.1 with all-stage descriptor tables, followed by the static samplers. Build the RTS0 part
    // with the same offsets as the runtime serializer so the DXBC wrapper and the resulting SHA-1 match the capture.
    static Profile Build(uint flags, bool standard, uint[][] tables, uint[][] samplers)
    {
        if (standard) tables = [Table(0, 2, 0, 16), Table(1, 1, 3, 66), .. tables, Samplers(98, 11)];
        const int header = 24, rowSize = 12, tableHeader = 8, word = 4;
        int tableAt = header + tables.Length * rowSize;
        int samplerAt = tableAt + tables.Sum(t => tableHeader + word * t.Length);
        var part = new byte[samplerAt + samplers.Length * 52];
        Put(0, 2);
        Put(4, (uint)tables.Length);
        Put(8, header);
        Put(12, (uint)samplers.Length);
        Put(16, (uint)samplerAt);
        Put(20, flags);
        for (var i = 0; i < tables.Length; i++)
        {
            var t = tables[i];
            Put(header + i * rowSize + 8, (uint)tableAt);
            Put(tableAt, (uint)t.Length / 6);
            Put(tableAt + 4, (uint)(tableAt + tableHeader));
            tableAt += tableHeader;
            foreach (var value in t) { Put(tableAt, value); tableAt += word; }
        }
        foreach (var sampler in samplers)
            foreach (var value in sampler) { Put(samplerAt, value); samplerAt += word; }
        var blob = Dxbc.Container("RTS0"u8, part);
        return new(Convert.ToHexStringLower(SHA1.HashData(blob)), blob, RootSig.Parse(blob));

        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(part.AsSpan(offset), value);
    }
}
