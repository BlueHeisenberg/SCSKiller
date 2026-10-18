using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Unreal;

public class StablePipelineCacheTests
{
    /// <summary>A file laid out as UE writes it: header, PSO bodies (only their type is read), TOC, EOF-MARK.</summary>
    internal static byte[] File(uint version, params (uint Key, uint Type, byte[][] Shaders)[] psos) =>
        File(version, 4, [.. psos.Select(p => ((ulong)p.Key, p.Type, p.Shaders))]);

    /// <summary>keyWidth 8: a fork's entry keys and PSO hashes (Lost Records: Bloom &amp; Rage).</summary>
    internal static byte[] File(uint version, int keyWidth, (ulong Key, uint Type, byte[][] Shaders)[] psos)
    {
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(0x5049504543414348UL); w.Write(version); w.Write(0u); w.Write((byte)49); w.Write(new byte[16]);
        var v17 = version == 17; // UE 4.25: no last GC time, TOCSTART, no shared guid, no last used time
        w.Write(0UL); // table offset (patched below)
        if (!v17) w.Write(0L); // last GC time
        var offsets = psos.Select(p => { var o = body.Position; w.Write(p.Type); w.Write(new byte[60]); return o; }).ToList();
        var toc = body.Position;
        w.Write(v17 ? 0x544F435354415254UL : 0x544F435354415232UL);
        if (!v17) { w.Write((byte)1); w.Write(new byte[16]); }
        w.Write(2u); w.Write(psos.Length);
        for (var i = 0; i < psos.Length; i++)
        {
            if (keyWidth == 8) w.Write(psos[i].Key); else w.Write((uint)psos[i].Key);
            w.Write((ulong)offsets[i]); w.Write(64UL); w.Write(new byte[16]); w.Write(new byte[32 + keyWidth]);
            w.Write(psos[i].Shaders.Length);
            foreach (var s in psos[i].Shaders) w.Write(s);
            w.Write(0UL); w.Write((ushort)0);
            if (!v17) w.Write(0L);
        }
        w.Write(0x454F462D4D41524BUL);
        var b = body.ToArray();
        BitConverter.GetBytes((ulong)toc).CopyTo(b, 33);
        return b;
    }

    static byte[] H(byte b) => Enumerable.Repeat(b, 20).ToArray();
    static string Hex(byte b) => Convert.ToHexStringLower(H(b));

    [Fact]
    public void ReadsEachPsosTypeAndShaderHashes()
    {
        var psos = StablePipelineCache.Read(File(28, (7, 1, [H(1), H(2)]), (8, 0, [H(3)]), (9, 2, [H(4)])))!;
        Assert.Equal([7UL, 8UL, 9UL], psos.Select(p => p.Key));
        Assert.Equal([StablePipelineCache.PsoType.Graphics, StablePipelineCache.PsoType.Compute, StablePipelineCache.PsoType.RayTracing], psos.Select(p => p.Type));
        Assert.Equal([Hex(1), Hex(2)], psos[0].Shaders);
        Assert.Equal([Hex(4)], psos[2].Shaders);
    }

    /// <summary>UE 4.25 writes version 17 (Returnal).</summary>
    [Fact]
    public void ReadsUe425sVersion17()
    {
        var psos = StablePipelineCache.Read(File(17, (7, 1, [H(1), H(2)]), (8, 0, [H(3)])))!;
        Assert.Equal([7UL, 8UL], psos.Select(p => p.Key));
        Assert.Equal([StablePipelineCache.PsoType.Graphics, StablePipelineCache.PsoType.Compute], psos.Select(p => p.Type));
        Assert.Equal([Hex(1), Hex(2)], psos[0].Shaders);
    }

    /// <summary>Versions 29 and 30 change only PSO bodies past their type (Gears of War: E-Day ships 30).</summary>
    [Theory]
    [InlineData(29u)]
    [InlineData(30u)]
    public void ReadsVersions29And30(uint version)
    {
        var psos = StablePipelineCache.Read(File(version, (7, 1, [H(1), H(2)]), (8, 0, [H(3)]), (9, 2, [H(4)])))!;
        Assert.Equal([StablePipelineCache.PsoType.Graphics, StablePipelineCache.PsoType.Compute, StablePipelineCache.PsoType.RayTracing], psos.Select(p => p.Type));
        Assert.Equal([Hex(1), Hex(2)], psos[0].Shaders);
    }

    /// <summary>UE 5.8 writes version 31: its TOC lists 8-byte shader hashes, named as CUE4Parse names those of a 5.8 shader
    /// library, so a shipped pipeline finds its shaders by them.</summary>
    [Fact]
    public void ReadsUe58sVersion31()
    {
        byte[] a = [1, 2, 3, 4, 5, 6, 7, 8], b = [9, 10, 11, 12, 13, 14, 15, 16];
        var psos = StablePipelineCache.Read(File(31, (7, 1, [a, b]), (8, 0, [b])))!;
        Assert.Equal([StablePipelineCache.PsoType.Graphics, StablePipelineCache.PsoType.Compute], psos.Select(p => p.Type));
        Assert.Equal(["0102030405060708" + new string('0', 24), "090a0b0c0d0e0f10" + new string('0', 24)], psos[0].Shaders);

        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(1);                                             // version 1: IoStore
        w.Write(1); w.Write(b);                                 // shader map hashes
        w.Write(1); w.Write(a);                                 // shader hashes
        w.Write(1); w.Write(new byte[12]);                      // group chunk ids
        w.Write(1); w.Write(0); w.Write(1);                     // map entries
        w.Write(1); w.Write(0L);                                // shader entries
        w.Write(1); w.Write(0); w.Write(1); w.Write(64); w.Write(64);
        w.Write(1); w.Write(0);                                 // shader indices
        var lib = (CUE4Parse.UE4.Shaders.FIoStoreShaderCodeArchive)UnrealReader.ReadLibrary("x", o.ToArray(), CUE4Parse.UE4.Versions.EGame.GAME_UE5_8).SerializedShaders;
        Assert.Equal(psos[0].Shaders[0], lib.ShaderHashes.Single().ToString().ToLowerInvariant());
        Assert.Equal(31u, StablePipelineCache.Version(File(31)));
        Assert.Null(StablePipelineCache.Version([1, 2, 3]));
    }

    [Fact]
    public void RefusesOtherVersionsAndBrokenFiles()
    {
        Assert.NotNull(StablePipelineCache.Read(File(22, (1, 1, [H(1)]))));
        Assert.Null(StablePipelineCache.Read(File(21, (1, 1, [H(1)]))));
        Assert.Null(StablePipelineCache.Read(File(16, (1, 1, [H(1)]))));
        Assert.Null(StablePipelineCache.Read(File(32, (1, 1, [H(1)]))));
        var f = File(28, (1, 1, [H(1)]));
        Assert.Null(StablePipelineCache.Read(f.AsSpan(0, f.Length - 1)));
        Assert.Null(StablePipelineCache.Read(f.Concat(new byte[8]).ToArray())); // no EOF-MARK at the end
    }

    [Fact]
    public void ReadsTheForkWithEightByteKeys()
    {
        var psos = StablePipelineCache.Read(File(26, 8, [(0x1_0000_0007UL, 1, [H(1), H(2)]), (8, 0, [H(3)])]))!;
        Assert.Equal([0x1_0000_0007UL, 8UL], psos.Select(p => p.Key));
        Assert.Equal([StablePipelineCache.PsoType.Graphics, StablePipelineCache.PsoType.Compute], psos.Select(p => p.Type));
        Assert.Equal([Hex(1), Hex(2)], psos[0].Shaders);
        Assert.Null(StablePipelineCache.Read(File(17, 8, [(7, 1, [H(1)])])));
    }

    /// <summary>A user's file is untrusted input: every truncation and every byte set to 0xFF reads as null or as PSOs, never
    /// throws.</summary>
    [Fact]
    public void BrokenFilesNeverThrow()
    {
        foreach (var f in new[] { File(28, (7, 1, [H(1), H(2)]), (8, 0, [H(3)])), File(26, 8, [(7, 1, [H(1), H(2)])]), File(17, (7, 1, [H(1)])) })
        {
            for (var n = 0; n < f.Length; n++) Assert.Null(StablePipelineCache.Read(f.AsSpan(0, n)));
            for (var i = 0; i < f.Length; i++)
            {
                var g = (byte[])f.Clone();
                g[i] = 0xFF;
                StablePipelineCache.Read(g);
            }
        }
    }

    /// <summary>The user's own caches (UnrealReader.UserCaches): each graphics PSO whose shaders the libraries have becomes an
    /// exact map, the fork's too. A PSO naming another build's shader, a broken file, a missing one and one over the size cap
    /// are left out.</summary>
    [Fact]
    public void UserCachesMapTheirKnownGraphicsPsos()
    {
        var dir = Ff7.TempDir("user-pipeline-caches");
        string Save(string name, byte[] b) { var p = Path.Combine(dir, name); System.IO.File.WriteAllBytes(p, b); return p; }
        var sm6 = Save("Game_PCD3D_SM6.upipelinecache", File(28, (7, 1, [H(1), H(2)]), (8, 1, [H(1), H(9)]), (9, 0, [H(3)])));
        var fork = Save("Fork_PCD3D_SM6.upipelinecache", File(26, 8, [(0x1_0000_0005UL, 1, [H(2), H(1)])]));
        var full = File(28, (7, 1, [H(1), H(2)]));
        var broken = Save("Broken_PCD3D_SM6.upipelinecache", full[..^1]);
        var byHash = new Dictionary<string, (string Sha, string Platform)> { [Hex(1)] = ("vs", "PCD3D_SM6"), [Hex(2)] = ("ps", "PCD3D_SM6"), [Hex(3)] = ("cs", "PCD3D_SM6") };
        var maps = UnrealReader.UserPipelines([sm6, fork, broken, Path.Combine(dir, "missing.upipelinecache")], byHash, null);
        Assert.Equal(["Game_PCD3D_SM6:00000007", "Fork_PCD3D_SM6:100000005"], maps.Select(m => m.Hash));
        Assert.All(maps, m => Assert.Equal(("UserPipelineCache", "PCD3D_SM6", true), (m.Library, m.Platform, m.IsPipeline)));
        Assert.Equal(["vs,ps", "ps,vs"], maps.Select(m => string.Join(",", m.Shaders)));
        Assert.Empty(UnrealReader.UserPipelines([sm6], byHash, null, max: new FileInfo(sm6).Length - 1));
    }

    /// <summary>UE's draw-rectangle VS writes interpolants a post-process PS doesn't read, so no signature pairs them: only
    /// the shipped pipeline (an IsPipeline map) puts that PS in the plan with the VS's resources in its root signature.</summary>
    [Fact]
    public void AShippedPipelinePairsWhatSignaturesDont()
    {
        var pos = In("SV_Position", 0, 0, 0xF, 3, 1);
        var vs = Shader("rect-vs", Stage.Vertex, [In("SV_VertexID", 0, 0, 1, 1, 6)], [pos, In("TEXCOORD", 0, 1), In("TEXCOORD", 1, 2)]) with { Counts = new(1, 1, 0, 0) };
        var ps = Shader("depth-ps", Stage.Pixel, [pos], [new SigElement("SV_Depth", 0, -1, 1, 0, 3)]);
        var global = new ShaderMap("g", "Global", "PCD3D_SM6", [vs.Sha1, ps.Sha1]);
        var shipped = new ShaderMap("SHf_PCD3D_SM6:00000001", "PipelineCache", "PCD3D_SM6", [vs.Sha1, ps.Sha1], IsPipeline: true);
        var dir = Ff7.TempDir("stable-cache-pair");
        bool Pairs(params ShaderMap[] maps)
        {
            var index = new ShaderIndex("stable", ["PCD3D_SM6"], new[] { vs, ps }.ToDictionary(s => s.Sha1), maps);
            var plan = new Planner().Build(Ff7.Game, new EngineInfo("Unreal", "5.4", null, "D3D12", false, null), index, null, Ff7.Nvidia,
                Path.Combine(dir, maps.Length.ToString()), null, CancellationToken.None);
            return PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'S' or 'P').Select(r => r.Tag == 'P' ? PsoDb.ParseItem(r.Payload).Stages : PsoDb.Parse(r).Stages)
                .Any(s => s.GetValueOrDefault((int)Stage.Vertex) == vs.Sha1 && s.GetValueOrDefault((int)Stage.Pixel) == ps.Sha1);
        }
        Assert.False(Pairs(global));
        Assert.True(Pairs(global, shipped));
    }
}
