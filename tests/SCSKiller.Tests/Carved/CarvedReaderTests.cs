using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Carved;

/// <summary>The carver on a synthetic install built from real compiled shaders (<see cref="Hlsl"/>): a shader pack with
/// RTS0-only containers, a PSO-record file, a file of junk with fake "DXBC" hits.</summary>
public class CarvedReaderTests
{
    const int Pairs = 140; // > CarvedReader.MinGraphics graphics shaders in total

    sealed class Fixture
    {
        public readonly string Dir = Ff7.TempDir("carved-synthetic");
        public readonly List<(byte[] Vs, byte[] Ps, int Rs)> Shaders = [];
        public readonly byte[] Rs1 = Hlsl.RootSignature(Hlsl.Rs1), Rs2 = Hlsl.RootSignature(Hlsl.Rs2);
        public readonly List<(byte[] Vs, byte[]? Ps)> Records;
        public Game Game => new("test:carved", "carved", Store.Other, Dir, Path.Combine(Dir, "game.exe"));

        public Fixture()
        {
            for (var i = 0; i < Pairs; i++) Shaders.Add((Hlsl.Vs(i + 1, i % 2 == 0 ? Hlsl.Rs1 : Hlsl.Rs2), Hlsl.Ps(i + 1, i % 2 == 0 ? Hlsl.Rs1 : Hlsl.Rs2), i % 2 + 1));
            // pack: every shader 100 bytes apart (a pool), the two root signatures at the end
            var pack = new MemoryStream();
            foreach (var (vs, ps, _) in Shaders) { Pad(pack, 100); pack.Write(vs); Pad(pack, 100); pack.Write(ps); }
            Pad(pack, 100); pack.Write(Rs1); Pad(pack, 100); pack.Write(Rs2);
            Directory.CreateDirectory(Path.Combine(Dir, "Shaders"));
            File.WriteAllBytes(Path.Combine(Dir, "Shaders", "pack.bin"), pack.ToArray());
            // PSO records: [desc][VS][8-byte hash][PS] back to back, records 600 bytes apart; one VS-only record
            Records = [(Shaders[0].Vs, Shaders[0].Ps), (Shaders[3].Vs, Shaders[3].Ps), (Shaders[5].Vs, null)];
            var rec = new MemoryStream();
            foreach (var (vs, ps) in Records) { Pad(rec, 600); rec.Write(vs); if (ps != null) { Pad(rec, 8); rec.Write(ps); } }
            Directory.CreateDirectory(Path.Combine(Dir, "cache"));
            File.WriteAllBytes(Path.Combine(Dir, "cache", "default.psocache"), rec.ToArray());
            // junk: "DXBC" magic followed by garbage, and a truncated container
            var junk = new MemoryStream();
            for (var i = 0; i < 1000; i++) { junk.Write("DXBC"u8); Pad(junk, 60 + i % 7); }
            junk.Write(Shaders[7].Vs.AsSpan(0, Shaders[7].Vs.Length / 2));
            Pad(junk, 70 << 10);
            File.WriteAllBytes(Path.Combine(Dir, "Shaders", "junk.bin"), junk.ToArray());
        }

        static void Pad(Stream s, int n) => s.Write(RandomNumberGenerator.GetBytes(n));
    }

    static readonly Lazy<Fixture> Data = new(() => new Fixture());
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    [Fact]
    public void ContainersValidate()
    {
        var (vs, ps, _) = Data.Value.Shaders[0];
        Assert.True(Dxbc.Valid(vs) && Dxbc.Valid(ps) && Dxbc.Valid(Data.Value.Rs1));
        Assert.Equal((1, 0, -1), (Dxbc.Kind(vs), Dxbc.Kind(ps), Dxbc.Kind(Data.Value.Rs1)));
        Assert.Equal(Dxbc.Part(Data.Value.Rs1, "RTS0"u8).ToArray(), Dxbc.Part(vs, "RTS0"u8).ToArray()); // the shader's part = the standalone blob's
        Assert.False(Dxbc.Valid(vs.AsSpan(0, vs.Length - 1)));
        var bad = vs.ToArray();
        var chunk = BitConverter.ToInt32(bad, 32);
        bad[chunk] = (byte)'?'; // FourCC outside [A-Z0-9]
        Assert.False(Dxbc.Valid(bad));
        bad = vs.ToArray();
        BitConverter.GetBytes(vs.Length).CopyTo(bad, chunk + 4); // chunk runs past the container
        Assert.False(Dxbc.Valid(bad));
    }

    [Fact]
    public void RecordsNeedEveryRunToBeAPipeline()
    {
        (long, int, int, string) C(long off, int kind, string sha, int size = 100) => (off, size, kind, sha);
        // records: VS+PS 8 bytes apart, records 600 apart; a VS-only record; a tessellated one
        var recs = CarvedReader.Records([C(0, 1, "v1"), C(108, 0, "p1"), C(800, 1, "v2"), C(1500, 1, "v3"), C(1608, 0, "p3"), C(1716, 4, "d3"), C(1824, 3, "h3")]);
        Assert.Equal([["v1", "p1"], ["v2"], ["v3", "p3", "d3", "h3"]], recs);
        Assert.Null(CarvedReader.Records([C(0, 1, "v1"), C(116, 0, "p1"), C(232, 1, "v2"), C(348, 0, "p2")]));   // a pack stored back to back (Cyberpunk: 16 bytes)
        Assert.Null(CarvedReader.Records([C(0, 1, "v1"), C(500, 0, "p1"), C(1000, 1, "v2")]));                   // a pack stored apart (Resonance)
        Assert.Null(CarvedReader.Records([C(0, 5, "c1"), C(100, -1, "rs"), C(200, 5, "c2"), C(900, 1, "v"), C(1000, 0, "p")])); // RTS0-only containers don't split runs (Starfield)
        Assert.Null(CarvedReader.Records([C(0, 0, "p1"), C(100, 1, "v1"), C(900, 0, "p2")]));                   // a lone PS is no pipeline
    }

    [Fact]
    public void DetectsIndexesAndServesRawShaders()
    {
        var d = Data.Value;
        var reader = new CarvedReader();
        var engine = reader.Detect(d.Game)!;
        Assert.Equal(new EngineInfo(CarvedReader.Family, "DXBC+RTS0", null, "D3D11 or D3D12", false, null), engine);
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        Assert.Equal(2 * Pairs, index.Shaders.Count);
        var (rs1, rs2) = (Sha(d.Rs1), Sha(d.Rs2));
        Assert.All(index.Shaders.Values.Select(s => s.RootSignature).OfType<string>(), h => Assert.Contains(h, index.RootSignatures!.Keys));   // the planner checks coverage with them
        foreach (var (vs, ps, rs) in d.Shaders)
        {
            Assert.Equal(rs == 1 ? rs1 : rs2, index.Shaders[Sha(vs)].RootSignature); // the standalone RTS0 container, not the shader
            Assert.Equal(rs == 1 ? rs1 : rs2, index.Shaders[Sha(ps)].RootSignature);
            Assert.Equal(Stage.Vertex, index.Shaders[Sha(vs)].Stage);
            Assert.Equal(new ResourceCounts(1, 1, 0, 1), index.Shaders[Sha(ps)].Counts);
        }
        var pool = Assert.Single(index.Maps, m => !m.IsPipeline);
        Assert.Equal(Path.Combine("Shaders", "pack.bin"), pool.Library);
        Assert.Equal(2 * Pairs, pool.Shaders.Count);
        Assert.Equal(d.Records.Select(r => new[] { Sha(r.Vs) }.Concat(r.Ps == null ? [] : [Sha(r.Ps)]).ToList()),
            index.Maps.Where(m => m.IsPipeline).Select(m => m.Shaders.ToList()));

        var want = new HashSet<string> { Sha(d.Shaders[9].Ps), rs2, "0000000000000000000000000000000000000000" };
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(d.Game, engine, want, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(d.Shaders[9].Ps, got[Sha(d.Shaders[9].Ps)]);
        Assert.Equal(d.Rs2, got[rs2]);
        Assert.Equal(2, got.Count);
        Assert.Equal(index.ContentHash, new CarvedReader().Index(d.Game, engine, null, CancellationToken.None).ContentHash);
    }

    [Fact]
    public void DetectExplainsWhatItDidNotFind()
    {
        var dir = Ff7.TempDir("carved-few");
        var game = new Game("test:few", "few", Store.Other, dir, Path.Combine(dir, "game.exe"));
        Assert.Contains("no raw DXBC/DXIL shaders", new CarvedReader().Detect(game)!.Unsupported);
        File.WriteAllBytes(Path.Combine(dir, "shaders.bin"), [.. Data.Value.Shaders[0].Vs, .. Data.Value.Shaders[0].Ps]);
        var e = new CarvedReader().Detect(game)!;
        Assert.Equal("only 2 raw graphics shaders: the rest are compressed or packed: needs an engine reader", e.Unsupported);
        Assert.Equal(Readiness.Unsupported, new Planner().Check(game, e, null, Ff7.Nvidia).Readiness);
        Assert.Null(new CarvedReader().Detect(game with { InstallDir = Path.Combine(dir, "missing") }));
    }

    /// <summary>A carved game whose files name its engine reports that family, readable or not, so a rule can match it.</summary>
    [Fact]
    public void FingerprintsNameTheEngine()
    {
        var dir = Ff7.TempDir("carved-fingerprint");
        var game = new Game("test:fingerprint", "fp", Store.Other, dir, Path.Combine(dir, "game.exe"));
        Assert.Equal(CarvedReader.Family, CarvedReader.Fingerprint(dir));
        Directory.CreateDirectory(Path.Combine(dir, "data", "pc"));
        File.WriteAllBytes(Path.Combine(dir, "data", "pc", "base-generic.rmdtoc"), [1]);
        Assert.Equal(CarvedReader.Northlight2, new CarvedReader().Detect(game)!.Family);
        Directory.CreateDirectory(Path.Combine(dir, "engine"));
        File.WriteAllBytes(Path.Combine(dir, "engine", "shader_final.cache"), [1]);
        Assert.Equal(CarvedReader.RedEngine4, CarvedReader.Fingerprint(dir));
        var deep = Ff7.TempDir("carved-fingerprint-deep");
        Directory.CreateDirectory(Path.Combine(deep, "a", "b", "c", "d"));
        File.WriteAllBytes(Path.Combine(deep, "a", "b", "c", "d", "x.rmdtoc"), [1]);
        Assert.Equal(CarvedReader.Family, CarvedReader.Fingerprint(deep)); // past the search depth
    }

    /// <summary>The carver listed under each of its families: one Detect, and a cached EngineInfo of any of them finds it.</summary>
    [Fact]
    public void OneReaderUnderSeveralFamiliesDetectsOnce()
    {
        var game = Data.Value.Game;
        var red4 = new EngineInfo(CarvedReader.RedEngine4, "DXIL", null, "D3D12", false, null);
        var c = new Stub(red4 with { Unsupported = "packed" });
        var chain = new EngineReaders([.. CarvedReader.Families.Select(f => (f, (IEngineReader)c))]);
        Assert.Equal(red4 with { Unsupported = "packed" }, chain.Detect(game));
        Assert.Equal(1, c.Detected);
        chain.Index(game, red4, null, CancellationToken.None);
        Assert.Equal(1, c.Indexed);
    }

    sealed class Stub(EngineInfo? e, bool throws = false) : IEngineReader
    {
        public int Indexed, Detected;
        public EngineInfo? Detect(Game game) { Detected++; return throws ? throw new IOException("unreadable") : e; }
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) { Indexed++; return new("", [], new Dictionary<string, ShaderInfo>(), []); }
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    [Fact]
    public void ReadersAreTriedInOrder()
    {
        var game = Data.Value.Game;
        var ue = new EngineInfo("Unreal", "5.5", null, "D3D12", false, null);
        var carved = new EngineInfo(CarvedReader.Family, "DXIL", null, "D3D12", false, null);
        var (u, c) = (new Stub(ue), new Stub(carved));
        var chain = new EngineReaders(("Unreal", u), (CarvedReader.Family, c));
        Assert.Equal(ue, chain.Detect(game));                                   // indexable: the first wins
        chain.Index(game, carved, null, CancellationToken.None);                 // by family, without a Detect (cached EngineInfo)
        Assert.Equal((0, 1), (u.Indexed, c.Indexed));
        var packed = ue with { Unsupported = "shaders stored inside materials" };
        Assert.Equal(carved, new EngineReaders(("Unreal", new Stub(packed)), (CarvedReader.Family, c)).Detect(game)); // Unreal can't: the carver can
        Assert.Equal(packed, new EngineReaders(("Unreal", new Stub(packed)), (CarvedReader.Family, new Stub(carved with { Unsupported = "packed" }))).Detect(game)); // neither: the first reason
        Assert.Equal(carved, new EngineReaders(("Unreal", new Stub(null, throws: true)), (CarvedReader.Family, c)).Detect(game));
        Assert.Throws<IOException>(() => new EngineReaders(("Unreal", new Stub(null, throws: true)), (CarvedReader.Family, new Stub(null))).Detect(game));
        Assert.Null(new EngineReaders(("Unreal", new Stub(null)), (CarvedReader.Family, new Stub(null))).Detect(game));
    }

    /// <summary>The app's whole chain on an Unreal 4.26 install from its own launcher: encrypted paks and enough raw DXIL
    /// for the carver. Wuthering Waves' fork has a rule, so its key makes it compile and Unreal's result wins. Without a
    /// fork the carver's does.</summary>
    [Theory]
    [InlineData("Wuthering Waves Game", true)]
    [InlineData("Some Game", false)]
    public void AnEncryptedForkWithARuleWinsOverTheCarver(string folder, bool fork)
    {
        var install = Path.Combine(Ff7.TempDir($"encrypted-fork-{fork}"), folder);
        var content = Directory.CreateDirectory(Path.Combine(install, "Client", "Content")).FullName;
        Unreal.UnrealReaderTests.EncryptedIndexPak11(Path.Combine(Directory.CreateDirectory(Path.Combine(content, "Paks")).FullName, "pakchunk0-WindowsNoEditor.pak"));
        File.Copy(Path.Combine(Data.Value.Dir, "Shaders", "pack.bin"), Path.Combine(content, "pack.bin"));
        var exe = Path.Combine(Directory.CreateDirectory(Path.Combine(install, "Client", "Binaries", "Win64")).FullName, "Client-Win64-Shipping.exe");
        var game = new Game($"test:encrypted-fork-{fork}", folder, Store.Other, install, exe);
        var carved = new CarvedReader().Detect(game)!;
        Assert.Null(carved.Unsupported);
        var e = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir($"encrypted-fork-readers-{fork}")).Detect(game)!;
        if (fork) Assert.Equal(("Unreal", "4.26", "GAME_WutheringWaves", true), (e.Family, e.Version, e.Fork, e.Encrypted));
        else Assert.Equal(carved, e);
    }

    /// <summary>Planner: embedded root signatures without a recording, pairs only within one root signature, IsPipeline
    /// maps as shipped; Materialize pulls the root-signature containers from the game like shaders.</summary>
    [Fact]
    public void PlannerUsesEmbeddedRootSignaturesAndExactPipelines()
    {
        var d = Data.Value;
        var reader = new CarvedReader();
        var engine = reader.Detect(d.Game)!;
        var planner = new Planner();
        Assert.Equal(new PlanCheck(Readiness.Ready, "no recording needed; also compiles every DirectX 11 shader (the game may run on either)"), // its SM 5.1 shaders: none for DX11
            planner.Check(d.Game, engine, null, Ff7.Nvidia));
        Assert.Equal(Readiness.NeedsRecording, planner.Check(d.Game, engine with { Version = "DXBC", GraphicsApi = "D3D12" }, null, Ff7.Nvidia).Readiness); // ambiguous: Ready for DX11 anyway
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        var dir = Ff7.TempDir("carved-plan");
        var plan = planner.Build(d.Game, engine, index, null, Ff7.Nvidia, dir, null, CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.DoesNotContain(body, r => r.Tag == 'B'); // game bytes (the RTS0 containers) never go into a plan
        var psos = body.Where(r => r.Tag == 'S').Select(r => (PsoDb.Parse(r).Rs, PsoDb.Parse(r).Stages))
            .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => (i.Rs, i.Stages))).ToList();
        foreach (var (rs, st) in psos) Assert.All(st.Values, h => Assert.Equal(index.Shaders[h].RootSignature, rs));
        // every VS alone, every VS with every PS of its root signature (same signatures), nothing across root signatures
        Assert.Equal(Pairs + 2 * (Pairs / 2) * (Pairs / 2), psos.Count);
        Assert.Equal(2, plan.Stats.RootSignatures);
        Assert.True(plan.Stats.RootSigRuleVerified);

        // an IsPipeline map is emitted as is, even across root signatures (the game shipped it); the pool is not paired
        var (vs, ps) = (Sha(d.Shaders[0].Vs), Sha(d.Shaders[1].Ps));
        var only = index with { Maps = [new ShaderMap("x", "records", CarvedReader.Platform, [vs, ps], IsPipeline: true)] };
        var one = planner.Build(d.Game, engine, only, null, Ff7.Nvidia, Path.Combine(dir, "one"), null, CancellationToken.None);
        var rec = Assert.Single(PlanFile.Read(one.FilePath).Records);
        Assert.Equal([vs, ps], PsoDb.Parse(rec).Stages.Values);

        // one whose last stage before the rasterizer writes no SV_Position streams its output out (The Witcher 3's VS-HS-DS-GS
        // terrain pass): no stream output declaration to synthesize, so it's left out, a stage set counted once however many
        // maps ship it; unless the recording has it, which replays it
        var (vs1, ps1) = (Sha(d.Shaders[1].Vs), Sha(d.Shaders[1].Ps));
        var streamed = index with
        {
            Shaders = new Dictionary<string, ShaderInfo>(index.Shaders) { [vs] = index.Shaders[vs] with { Outputs = [new SigElement("TEXCOORD", 0, 0, 0xF, 0, 3)] } },
            Maps = [new ShaderMap("x", "records", CarvedReader.Platform, [vs], IsPipeline: true), new ShaderMap("y", "records", CarvedReader.Platform, [vs], IsPipeline: true),
                new ShaderMap("z", "records", CarvedReader.Platform, [vs1, ps1], IsPipeline: true)],
        };
        var half = planner.Build(d.Game, engine, streamed, null, Ff7.Nvidia, Path.Combine(dir, "streamed"), null, CancellationToken.None);
        Assert.Equal([vs1, ps1], PsoDb.Parse(Assert.Single(PlanFile.Read(half.FilePath).Records)).Stages.Values);
        Assert.Equal((2, 1), (half.Stats.StageSets, half.Stats.LeftOut));
        Assert.Equal(50, SCSKiller.Core.App.ScsKiller.CoveragePercent(half.Stats));
        var recording = Path.Combine(dir, "streamed.db");
        using (var f = File.Create(recording))
        {
            var g = SCSKiller.Tests.Planning.ExactLayoutsTests.Gfx(index.Shaders[vs].RootSignature!, streamed.Shaders[vs], null, [], []);
            PsoDb.Write(f, g.Tag, g.Payload);
        }
        var recorded = planner.Build(d.Game, engine, streamed, new Recording(recording), Ff7.Nvidia, Path.Combine(dir, "streamed-recorded"), null, CancellationToken.None);
        Assert.Equal((2, 0), (recorded.Stats.StageSets, recorded.Stats.LeftOut));
        Assert.Equal(100, SCSKiller.Core.App.ScsKiller.CoveragePercent(recorded.Stats));
        Assert.True(Planner.Positioned(new Dictionary<Stage, ShaderInfo> { [Stage.Vertex] = index.Shaders[vs] }));

        var work = Path.Combine(dir, "work");
        planner.Materialize(plan, d.Game, engine, reader, null, work, CancellationToken.None);
        Assert.Equal("605500aca5e115987aa01fcdb37aa13377bd700f18275629ed9535cdb3ffe18c", MaterializeOutputTests.Digest(work));
        Ff7.CheckWarmReady(work);
    }

    /// <summary>A cancelled index stops in a shader-named file that holds no container (the only check was in the per-container callback).</summary>
    [Fact]
    public void ACancelledIndexStopsInAFileWithoutShaders()
    {
        var dir = Ff7.TempDir("carved-cancel");
        File.WriteAllBytes(Path.Combine(dir, "shaders.bin"), new byte[4 << 20]);
        var game = new Game("test:carved-cancel", "c", Store.Other, dir, Path.Combine(dir, "game.exe"));
        var engine = new EngineInfo(CarvedReader.Family, "DXBC", null, "D3D12", false, null);
        Assert.Throws<OperationCanceledException>(() => new CarvedReader().Index(game, engine, null, new CancellationToken(true)));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PeFilesAreReadWithoutAMap()   // a read error in a mapped page ends the process, a stream read throws
    {
        var dll = Path.Combine(Ff7.TempDir("pe-unmapped"), "kernel32.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), dll);
        using var pe = PeFile.Open(dll);
        Assert.NotEmpty(PeFile.ExportNames(pe));   // its export section is over PEReader's 16 KB map threshold
        using var f = new FileStream(dll, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        f.SetLength(0);   // refused while a section is mapped (ERROR_USER_MAPPED_FILE)
    }

    [Fact]
    public void GodotIsToldByItsPackBesideOrEmbedded()
    {
        var dir = Ff7.TempDir("godot");
        static byte[] Header(uint major) => [.. "GDPC"u8, .. BitConverter.GetBytes(2u), .. BitConverter.GetBytes(major), .. new byte[20]];
        var beside = Path.Combine(dir, "game.exe");
        File.WriteAllBytes(beside, new byte[4096]);
        Assert.Null(CarvedReader.GodotApi(beside));
        File.WriteAllBytes(Path.Combine(dir, "game.pck"), Header(4));
        Assert.Equal("Vulkan", CarvedReader.GodotApi(beside));
        var embedded = Path.Combine(dir, "old.exe");
        var pack = Header(3);
        File.WriteAllBytes(embedded, [.. new byte[4096], .. pack, .. BitConverter.GetBytes((long)pack.Length), .. "GDPC"u8]);
        Assert.Equal("OpenGL", CarvedReader.GodotApi(embedded));
    }
}
