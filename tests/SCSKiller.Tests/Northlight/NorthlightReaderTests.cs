using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Northlight;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Northlight;

/// <summary>The Northlight reader on a synthetic install: effect files laid out as Control's, with shaders compiled here
/// (<see cref="Hlsl"/>).</summary>
public class NorthlightReaderTests
{
    const string VsSrc = "float4 main(float3 p : POSITION, float2 uv : TEXCOORD0, out float2 ouv : TEXCOORD0) : SV_Position { ouv = uv; return float4(p * {0}.0, 1); }";
    // TEXCOORD0 after another output: a register no PS reading TEXCOORD0 in o1 links to
    const string VsOffSrc = "void main(float3 p : POSITION, out float4 pos : SV_Position, out float4 x : TEXCOORD1, out float2 ouv : TEXCOORD0) { pos = float4(p, 1); x = pos; ouv = p.xy; }";
    const string PsSrc = """
        Texture2D t : register(t0); SamplerState s : register(s0); Texture2D bindless[] : register(t0, space1);
        float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t.Sample(s, uv) * {0}.0 + bindless[(uint)uv.x].Load(int3(0, 0, 0)); }
        """;
    const string CsSrc = "RWByteAddressBuffer u : register(u0); [numthreads(64, 1, 1)] void main(uint i : SV_DispatchThreadID) { u.Store(i * 4, i); }";

    sealed class Fixture
    {
        public readonly string Dir = Ff7.TempDir("northlight-synthetic");
        public readonly byte[][] Vs = [.. Enumerable.Range(1, 2).Select(i => Hlsl.Compile(VsSrc.Replace("{0}", $"{i}"), "main", "vs_5_1"))];
        public readonly byte[][] Ps = [.. Enumerable.Range(1, 2).Select(i => Hlsl.Compile(PsSrc.Replace("{0}", $"{i}"), "main", "ps_5_1", Hlsl.UnboundedTables))];
        public readonly byte[] VsOff = Hlsl.Compile(VsOffSrc, "main", "vs_5_1"), Cs = Hlsl.Compile(CsSrc, "main", "cs_5_1");
        public readonly byte[] Lib = Library((10, "closestHitMain", 32), (11, "missMain", 32), (7, "rayGen", 0));
        public Game Game => new("test:northlight", "northlight", Store.Other, Dir, Path.Combine(Dir, "Control_DX12.exe"));
        public string Effects => Path.Combine(Dir, @"data\shaders\build\pc_dxil");

        public Fixture()
        {
            Directory.CreateDirectory(Effects);
            // VS1+PS1 twice (one pipeline), VS2+PS2, the CS, and VsOff+PS1 (doesn't link: left out)
            File.WriteAllBytes(Path.Combine(Effects, "standardmaterial.obj"), Effect([Vs[0], Ps[0]], [Vs[1], Ps[1]], [Vs[0], Ps[0]], [VsOff, Ps[0]], [Cs]));
            File.WriteAllBytes(Path.Combine(Effects, "rt_ao.obj"), Effect([Lib]));
            File.WriteAllBytes(Path.Combine(Effects, "notes.obj"), "not an effect"u8.ToArray());
        }
    }

    static readonly Lazy<Fixture> Data = new(() => new Fixture());
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>An RFX effect file: header, then per record a permutation header and its shaders, each as u32 size, the
    /// container, its entry point and 8 zero bytes; 4 more zero bytes before the next shader of the same record.</summary>
    internal static byte[] Effect(params byte[][][] records)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        w.Write("RFX "u8); w.Write(0x1d); w.Write(10); w.Write(new byte[24]);
        var perm = 0;
        foreach (var r in records)
        {
            w.Write(1); w.Write((byte)0); w.Write(perm++); w.Write(7);   // permutation key, id, 7
            for (var i = 0; i < r.Length; i++)
            {
                if (i > 0) w.Write(0);
                w.Write(r[i].Length); w.Write(r[i]);
                var name = Encoding.ASCII.GetBytes(i == 0 ? "mainVS" : "materialPS");
                w.Write(name.Length); w.Write(name); w.Write(0L);
            }
        }
        return s.ToArray();
    }

    /// <summary>A DXIL library container: a program header (lib_6_3) and an RDAT function table.</summary>
    internal static byte[] Library(params (int Kind, string Name, int Payload)[] fns)
    {
        var rdatOnly = RtCollectionTests.Library(fns);
        var rdat = rdatOnly[36..];
        byte[] dxil = [.. "DXIL"u8, .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(6 << 16 | 0x63), .. BitConverter.GetBytes(2)];
        var size = 32 + 8 + rdat.Length + dxil.Length;
        return [.. "DXBC"u8, .. new byte[16], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes(2),
            .. BitConverter.GetBytes(40), .. BitConverter.GetBytes(40 + rdat.Length), .. rdat, .. dxil];
    }

    [Fact]
    public void ReadsRecordsOfEffectFiles()
    {
        var d = Data.Value;
        var recs = NorthlightReader.Records(File.ReadAllBytes(Path.Combine(d.Effects, "standardmaterial.obj")), default)!;
        Assert.Equal([[1, 0], [1, 0], [1, 0], [1, 0], [5]], recs.Select(r => r.Select(c => c.Kind).ToArray()));
        Assert.Null(NorthlightReader.Records("not an effect"u8.ToArray(), default));
        var f = Effect([d.Vs[0], d.Ps[0]]);
        f[f.AsSpan().IndexOf("DXBC"u8) - 4]++; // a container not preceded by its size: not this layout
        Assert.Null(NorthlightReader.Records(f, default));
        Assert.Throws<OperationCanceledException>(() => NorthlightReader.Records(Effect([.. Enumerable.Repeat<byte[][]>([d.Cs], 4096)]), new CancellationToken(true)));
    }

    /// <summary>The index-wide record cap counts every record, libraries and rejected ones included: with a cap of 3, an unlinked
    /// record and a library take two, the next file's first pipeline the third, and its compute shader is left out unread.</summary>
    [Fact]
    public void TheRecordCapCountsEveryRecord()
    {
        var d = Data.Value;
        var dir = Ff7.TempDir("northlight-cap");
        var effects = Path.Combine(dir, @"data\shaders\build\pc_dxil");
        Directory.CreateDirectory(effects);
        File.WriteAllBytes(Path.Combine(effects, "a.obj"), Effect([d.VsOff, d.Ps[0]]));
        File.WriteAllBytes(Path.Combine(effects, "b.obj"), Effect([d.Lib]));
        File.WriteAllBytes(Path.Combine(effects, "c.obj"), Effect([d.Vs[0], d.Ps[0]], [d.Cs]));
        var game = d.Game with { InstallDir = dir };
        var engine = new EngineInfo(NorthlightReader.Family, NorthlightReader.Version, null, "D3D12", false, null);
        var log = new List<string>();
        var index = new NorthlightReader { MaxRecords = 3 }.Index(game, engine, new Log(log.Add), CancellationToken.None);
        Assert.Equal([[Sha(d.Vs[0]), Sha(d.Ps[0])]], index.Maps.Where(m => m.IsPipeline).Select(m => m.Shaders.ToArray()));
        Assert.DoesNotContain(Sha(d.Cs), index.Shaders.Keys);
        Assert.Contains(Sha(d.Lib), index.Shaders.Keys);
        Assert.Contains(log, l => l.Contains("1 records over the cap of 3"));
        Assert.Contains(Sha(d.Cs), new NorthlightReader().Index(game, engine, null, CancellationToken.None).Shaders.Keys);
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    /// <summary>A VS writing one output twice is malformed (null), not an exception that ends the index.</summary>
    [Fact]
    public void AVsWritingAnOutputTwiceIsMalformed()
    {
        SigElement Uv(int reg) => new("TEXCOORD", 0, reg, 0x3, 0, 3);
        ShaderInfo S(Stage st, SigElement[] ins, SigElement[] outs) => new("", st, "vs_5_1", 0, new(0, 0, 0, 0), [], ins, outs);
        var ps = S(Stage.Pixel, [Uv(1)], []);
        Assert.True(NorthlightReader.Feeds([S(Stage.Vertex, [], [Uv(1)]), ps]));
        Assert.False(NorthlightReader.Feeds([S(Stage.Vertex, [], [Uv(2)]), ps]));
        Assert.Null(NorthlightReader.Feeds([S(Stage.Vertex, [], [Uv(1), Uv(2)]), ps]));
    }

    [Fact]
    public void IndexesEachRecordAsAPipeline()
    {
        var d = Data.Value;
        var reader = new NorthlightReader();
        var engine = reader.Detect(d.Game)!;
        Assert.Equal(new EngineInfo(NorthlightReader.Family, "DX12", null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.Untested), new Planner().Check(d.Game, engine, null, Ff7.Nvidia));
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        Assert.Equal(7, index.Shaders.Count);
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        Assert.Equal([[Sha(d.Vs[0]), Sha(d.Ps[0])], [Sha(d.Vs[1]), Sha(d.Ps[1])], [Sha(d.Cs)]],
            index.Maps.Where(m => m.IsPipeline).Select(m => m.Shaders.ToArray())); // each distinct once; the unlinked one left out
        var libs = Assert.Single(index.Maps, m => !m.IsPipeline);
        Assert.Equal((NorthlightReader.Libraries, Stage.Library), (libs.Library, index.Shaders[Assert.Single(libs.Shaders)].Stage));

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(d.Game, engine, new HashSet<string> { Sha(d.Ps[1]), Sha(d.Lib), new('0', 40) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(d.Ps[1], got[Sha(d.Ps[1])]);
        Assert.Equal(d.Lib, got[Sha(d.Lib)]);
        Assert.Equal(2, got.Count);
        Assert.Null(reader.Detect(d.Game with { InstallDir = Path.Combine(d.Dir, "missing") }));
    }

    /// <summary>A Northlight 2 install (its .rmdtoc archives) isn't Control's engine, even with Control's effect files.</summary>
    [Fact]
    public void Northlight2IsNotClaimed()
    {
        var dir = Ff7.TempDir("northlight2-synthetic");
        Directory.CreateDirectory(Path.Combine(dir, @"data\shaders\build\pc_dxil"));
        File.Copy(Path.Combine(Data.Value.Effects, "standardmaterial.obj"), Path.Combine(dir, @"data\shaders\build\pc_dxil\standardmaterial.obj"));
        var game = Data.Value.Game with { InstallDir = dir };
        Assert.NotNull(new NorthlightReader().Detect(game));
        File.WriteAllBytes(Path.Combine(dir, "data", "base-generic.rmdtoc"), [1]);
        Assert.Null(new NorthlightReader().Detect(game));
    }

    /// <summary>pc_dx11, Control's DX11 set (SM 5.0, the same layout): the game may run on either API, so on NVIDIA every
    /// DX11 shader becomes a D3D11 item, and none of them a D3D12 pipeline.</summary>
    [Fact]
    public void TheDx11SetCompilesAsD3D11Items()
    {
        var d = Data.Value;
        var dir = Ff7.TempDir("northlight-dx11");
        Directory.CreateDirectory(Path.Combine(dir, @"data\shaders\build\pc_dxil"));
        Directory.CreateDirectory(Path.Combine(dir, @"data\shaders\build\pc_dx11"));
        File.WriteAllBytes(Path.Combine(dir, @"data\shaders\build\pc_dxil\standardmaterial.obj"), Effect([d.Vs[0], d.Ps[0]]));
        var vs = Hlsl.Compile(VsSrc.Replace("{0}", "3"), "main", "vs_5_0");
        var ps = Hlsl.Compile("Texture2D t : register(t0); SamplerState s : register(s0); float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t.Sample(s, uv); }", "main", "ps_5_0");
        var cs = Hlsl.Compile(CsSrc, "main", "cs_5_0");
        File.WriteAllBytes(Path.Combine(dir, @"data\shaders\build\pc_dx11\standardmaterial.obj"), Effect([vs, ps], [cs]));
        var game = d.Game with { InstallDir = dir };
        var reader = new NorthlightReader();
        var engine = reader.Detect(game)!;
        Assert.Equal("D3D11 or D3D12", engine.GraphicsApi);
        var check = new Planner().Check(game, engine, null, Ff7.Nvidia);
        Assert.Equal(Readiness.Ready, check.Readiness);
        Assert.Contains(Planner.Untested, check.Reason);
        Assert.Contains("DirectX 11", check.Reason);

        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal(["D3D12", NorthlightReader.Platform11], index.Platforms);
        var dx11 = Assert.Single(index.Maps, m => m.Platform == NorthlightReader.Platform11);
        Assert.Equal(new[] { Sha(vs), Sha(ps), Sha(cs) }.Order(), dx11.Shaders.Order());
        Assert.Equal([[Sha(d.Vs[0]), Sha(d.Ps[0])]], index.Maps.Where(m => m.IsPipeline).Select(m => m.Shaders.ToArray()));

        var work = Ff7.TempDir("northlight-dx11-plan");
        var planner = new Planner();
        var plan = planner.Build(game, engine, index, null, Ff7.Nvidia, work, null, CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.Equal(new[] { Sha(vs), Sha(ps), Sha(cs) }.Order(), body.Where(r => r.Tag == '1').Select(r => PsoDb.Hex(r.Payload.AsSpan(4, 20))).Order());
        Assert.Equal(3, plan.Stats.D3D11Shaders);
        Assert.Equal(1, plan.Stats.Generated);
        planner.Materialize(plan, game, engine, reader, null, Path.Combine(work, "work"), CancellationToken.None);
        Ff7.CheckWarmReady(Path.Combine(work, "work"));
    }

    /// <summary>The engine's root signatures, version 1.0 as the game serializes them; its unbounded bindless SRVs (t0 space 1)
    /// sit in the bounded table of 244000, which the runtime accepts.</summary>
    [Fact]
    public void PlansWithTheEnginesRootSignatures()
    {
        var d = Data.Value;
        var reader = new NorthlightReader();
        var engine = reader.Detect(d.Game)!;
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        Assert.Contains(new Binding("srv", 1, 0, -1), index.Shaders[Sha(d.Ps[0])].Bindings);
        var gfx = RootSig.Serialize(RootSig.Build(SCSKiller.Tests.Planning.Rules.Northlight, new Dictionary<Stage, ShaderInfo> { [Stage.Vertex] = index.Shaders[Sha(d.Vs[0])] }, false), []);
        var cs = RootSig.Serialize(SCSKiller.Tests.Planning.Rules.NorthlightCompute, []);
        uint U(byte[] blob, int at) => BitConverter.ToUInt32(SCSKiller.Core.Carved.Dxbc.Part(blob, "RTS0"u8)[at..]);
        Assert.Equal((1u, 18u, 0x11u), (U(gfx, 0), U(gfx, 4), RootSig.Parse(gfx).Flags)); // version 1.0, parameters, flags
        Assert.Equal((1u, 8u, 0u), (U(cs, 0), U(cs, 4), RootSig.Parse(cs).Flags));
        Assert.Null(RootSig.Uncovered(RootSig.Parse(gfx), Stage.Pixel, index.Shaders[Sha(d.Ps[0])]));
        Assert.Throws<RootSig.SerializeException>(() => RootSig.Build(SCSKiller.Tests.Planning.Rules.Northlight, new Dictionary<Stage, ShaderInfo> { [Stage.Geometry] = index.Shaders[Sha(d.Vs[0])] }, false));
        Assert.False(RootSig.Verified(engine));

        var dir = Ff7.TempDir("northlight-plan");
        var planner = new Planner();
        var plan = planner.Build(d.Game, engine, index, null, Ff7.Nvidia, dir, null, CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        string Hash(byte[] b) => PsoDb.Hex(SHA1.HashData(b));
        var local = RtCollections.Serialize(SCSKiller.Tests.Planning.Rules.NorthlightLocal, []).Hash;
        Assert.Equal(new[] { Hash(gfx), Hash(cs), local }.Order(), body.Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).Order());
        var psos = body.Where(r => r.Tag == 'S').Select(r => PsoDb.Tuple(PsoDb.Parse(r).Rs, PsoDb.Parse(r).Stages))
            .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => PsoDb.Tuple(i.Rs, i.Stages))).ToHashSet();
        string T(byte[] rs, params (Stage S, byte[] B)[] st) => PsoDb.Tuple(Hash(rs), st.Select(x => new KeyValuePair<int, string>((int)x.S, Sha(x.B))));
        Assert.Equal(new HashSet<string> { T(gfx, (Stage.Vertex, d.Vs[0]), (Stage.Pixel, d.Ps[0])), T(gfx, (Stage.Vertex, d.Vs[1]), (Stage.Pixel, d.Ps[1])), T(cs, (Stage.Compute, d.Cs)) }, psos);
        var y = RtCollections.ParseItem(Assert.Single(body, r => r.Tag == 'Y').Payload);
        Assert.Equal((Sha(d.Lib), Hash(cs), local, local), (y.Library, y.Global, y.LocalRayGen, y.LocalOther));
        Assert.Equal((0L, 0L, 1L, 0L), (plan.Stats.Uncovered, plan.Stats.LeftOut, plan.Stats.RtLibraries, plan.Stats.RtUncovered));
        Assert.False(plan.Stats.RootSigRuleVerified);
        var work = Path.Combine(dir, "work");
        planner.Materialize(plan, d.Game, engine, reader, null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);
    }
}
