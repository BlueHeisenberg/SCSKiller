using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.RedEngine;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.RedEngine;

/// <summary>The REDengine 3 reader on a synthetic install: both caches written as the game lays them out, with shaders
/// compiled here (<see cref="Hlsl"/>).</summary>
public class RedEngineReaderTests
{
    const string Cs = "RWByteAddressBuffer u : register(u0); [numthreads(64, 1, 1)] void main(uint i : SV_DispatchThreadID) { u.Store(i * 4, i); }";

    /// <summary>A material cache: per shader key, zlib flag, sizes, the zlib stream of (container size, 0, container,
    /// reflection); then the techniques; then the footer. A null shader is a placeholder entry (no container).
    /// <paramref name="inflatedOff"/> is added to every declared inflated size.</summary>
    internal static byte[] Materials((ulong Key, byte[]? Shader)[] shaders, ulong[][] techniques, uint version = 5, int inflatedOff = 0) =>
        MaterialsRaw(shaders, [.. techniques.Select(t => Technique(t))], version, inflatedOff);

    internal static byte[] MaterialsRaw((ulong Key, byte[]? Shader)[] shaders, byte[][] techniques, uint version = 5, int inflatedOff = 0)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        foreach (var (key, shader) in shaders)
        {
            var payload = shader == null ? new byte[24] : [.. BitConverter.GetBytes(shader.Length), 0, 0, 0, 0, .. shader, .. "RPMC"u8, .. new byte[60]];
            var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(payload);
            w.Write(key); w.Write((byte)1); w.Write(payload.Length + inflatedOff); w.Write((int)z.Length); w.Write(z.ToArray());
        }
        var at = s.Position;
        foreach (var t in techniques) w.Write(t);
        var size = s.Position - at;
        w.Write(1u); w.Write(0u); w.Write(new byte[12]); // the two short lists before the footer
        w.Write(shaders.Length); w.Write(techniques.Length); w.Write(0UL); w.Write(at); w.Write(size); w.Write(at);
        w.Write("RDHS"u8); w.Write(version);
        return s.ToArray();
    }

    /// <summary>One technique; <paramref name="pathLength"/> replaces the path's compact length bytes, <paramref name="names"/>
    /// the second name list's count.</summary>
    internal static byte[] Technique(ulong[] keys, byte[]? pathLength = null, uint names = 2)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        w.Write(1UL); w.Write(2UL); w.Write(3UL);
        var path = Encoding.ASCII.GetBytes(@"fx\test.w2mg");
        w.Write(pathLength ?? [(byte)(0x80 | path.Length)]); w.Write(path);
        w.Write(7u);
        foreach (var k in keys) w.Write(k);
        w.Write(new byte[24]);
        w.Write(1u); w.Write(new byte[32]); // one parameter record
        w.Write(0u);
        w.Write(0u);
        w.Write(names); foreach (var n in new[] { "Sampler1", "Diffuse" }) { w.Write((byte)n.Length); w.Write(Encoding.ASCII.GetBytes(n)); w.Write((byte)0); }
        return s.ToArray();
    }

    internal static byte[] Static(params byte[][] shaders)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        foreach (var c in shaders) { w.Write(5UL); w.Write(6UL); w.Write(8 + c.Length + 16); w.Write(c.Length); w.Write(0); w.Write(c); w.Write(new byte[16]); }
        w.Write(shaders.Length); w.Write(new byte[16]); w.Write("RDHS"u8); w.Write(5u);
        return s.ToArray();
    }

    sealed class Fixture
    {
        public readonly string Dir = Ff7.TempDir("redengine-synthetic");
        public readonly byte[][] Vs = [.. Enumerable.Range(1, 3).Select(i => Hlsl.Vs(i, Hlsl.Rs1))], Ps = [.. Enumerable.Range(1, 3).Select(i => Hlsl.Ps(i, Hlsl.Rs1))];
        public readonly byte[] Cs = Hlsl.Compile(RedEngineReaderTests.Cs, "main", "cs_5_1");
        public Game Game => new("test:red", "red", Store.Other, Dir, Path.Combine(Dir, @"bin\x64_dx12\witcher3.exe"));

        public Fixture()
        {
            var content = Path.Combine(Dir, "content", "content0");
            Directory.CreateDirectory(content);
            // VS1+PS1, VS2+PS2, VS1 alone, the compute shader, and one naming a key the cache doesn't have
            File.WriteAllBytes(Path.Combine(content, "shaderdx12_0.cache"), Materials(
                [(10, Vs[0]), (11, Ps[0]), (12, Vs[1]), (13, Ps[1]), (14, Cs), (15, null)],
                [[10, 11, 0, 0, 0, 0], [12, 13, 0, 0, 0, 0], [10, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 14], [12, 99, 0, 0, 0, 0], [10, 11, 0, 0, 0, 0]]));
            File.WriteAllBytes(Path.Combine(content, "staticshaderDx12_0.cache"), Static(Vs[2], Ps[2]));
        }
    }

    static readonly Lazy<Fixture> Data = new(() => new Fixture());
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>The Witcher 3's three root signatures, as its recording has them.</summary>
    const string Graphics = "f6c0d61f3ac22727d9ff6e93114588e5309b60be", Tessellation = "e4dd5cf68f8a5071fe1101be89f34b82ff1aab8c", Compute = "576ddc174436275ef40738aff737215322d2e5e1";

    static string Rs(params Stage[] stages) => Convert.ToHexStringLower(SHA1.HashData(RootSig.Serialize(
        RootSig.Build(Rules.Red3, stages.ToDictionary(s => s, s => new ShaderInfo("", s, "", 0, new(0, 0, 0, 0), [], [], [])), false),
        RootSig.StaticSamplers(Rules.Red3))));

    [Fact]
    public void RootSignaturesAreTheGamesByteForByte()
    {
        Assert.Equal(Graphics, Rs(Stage.Vertex));
        Assert.Equal(Graphics, Rs(Stage.Vertex, Stage.Pixel));
        Assert.Equal(Tessellation, Rs(Stage.Vertex, Stage.Hull, Stage.Domain, Stage.Pixel));
        Assert.Equal(Tessellation, Rs(Stage.Vertex, Stage.Geometry, Stage.Pixel));
        Assert.Equal(Compute, Rs(Stage.Compute));
        var engine = new EngineInfo(RedEngineReader.Family, RedEngineReader.Version, null, "D3D12", false, null);
        Assert.Equal(Rules.Red3, RootSig.RuleFor(engine));
        Assert.True(RootSig.Verified(engine));
    }

    [Fact]
    public void IndexesBothCachesAndTechniquesAsPipelines()
    {
        var d = Data.Value;
        var reader = new RedEngineReader();
        var engine = reader.Detect(d.Game)!;
        Assert.Equal(new EngineInfo(RedEngineReader.Family, "DX12", null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(d.Game, engine, null, Ff7.Nvidia));
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        Assert.Equal(7, index.Shaders.Count); // the placeholder is no shader
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        Assert.Equal(Stage.Compute, index.Shaders[Sha(d.Cs)].Stage);
        Assert.Equal([[Sha(d.Vs[0]), Sha(d.Ps[0])], [Sha(d.Vs[1]), Sha(d.Ps[1])], [Sha(d.Vs[0])], [Sha(d.Cs)]],
            index.Maps.Where(m => m.IsPipeline).Select(m => m.Shaders.ToArray())); // each distinct once; the unknown key's left out
        var pool = Assert.Single(index.Maps, m => !m.IsPipeline);
        Assert.Equal([Sha(d.Vs[2]), Sha(d.Ps[2])], pool.Shaders);

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(d.Game, engine, new HashSet<string> { Sha(d.Ps[1]), Sha(d.Vs[2]), new('0', 40) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(d.Ps[1], got[Sha(d.Ps[1])]);
        Assert.Equal(d.Vs[2], got[Sha(d.Vs[2])]);
        Assert.Equal(2, got.Count);
        Assert.Null(new RedEngineReader().Detect(d.Game with { InstallDir = Path.Combine(d.Dir, "missing") }));
    }

    /// <summary>Techniques plan as they are, static shaders pair by linkage, every pipeline with the rule's root signature.</summary>
    [Fact]
    public void PlansEachTechniqueWithTheEnginesRootSignature()
    {
        var d = Data.Value;
        var reader = new RedEngineReader();
        var engine = reader.Detect(d.Game)!;
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        var dir = Ff7.TempDir("redengine-plan");
        var planner = new Planner();
        var plan = planner.Build(d.Game, engine, index, null, Ff7.Nvidia, dir, null, CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.Equal([Compute, Graphics], body.Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).Order()); // the generated root signatures, no game bytes
        var psos = body.Where(r => r.Tag == 'S').Select(r => PsoDb.Tuple(PsoDb.Parse(r).Rs, PsoDb.Parse(r).Stages))
            .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => PsoDb.Tuple(i.Rs, i.Stages))).ToHashSet();
        string T(string rs, params (Stage S, byte[] B)[] st) => PsoDb.Tuple(rs, st.Select(x => new KeyValuePair<int, string>((int)x.S, Sha(x.B))));
        Assert.Equal(new HashSet<string>
        {
            T(Graphics, (Stage.Vertex, d.Vs[0]), (Stage.Pixel, d.Ps[0])), T(Graphics, (Stage.Vertex, d.Vs[1]), (Stage.Pixel, d.Ps[1])),
            T(Graphics, (Stage.Vertex, d.Vs[0])), T(Compute, (Stage.Compute, d.Cs)),
            T(Graphics, (Stage.Vertex, d.Vs[2])), T(Graphics, (Stage.Vertex, d.Vs[2]), (Stage.Pixel, d.Ps[2])),
        }, psos);
        Assert.True(plan.Stats.RootSigRuleVerified);
        Assert.Equal(2, plan.Stats.RootSignatures);
        var work = Path.Combine(dir, "work");
        planner.Materialize(plan, d.Game, engine, reader, null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);

        // NVIDIA: every synthesized PSO is created with REDengine 3's NVAPI slot (12, space 1), in the plan and the work folder,
        // and keyed by it as a plan input; AMD gets none
        var synthesized = body.Where(r => r.Tag is 'S' or 'P').Select(r => r.Key).ToHashSet();
        var nv = body.Where(r => r.Tag == 'N').Select(PsoDb.NvState.Parse).ToList();
        Assert.Equal(synthesized, nv.Select(n => n.Target).ToHashSet());
        Assert.All(nv, n => Assert.Equal((12u, 1u, 0u), (n.Slot, n.Space, n.Options)));
        Assert.Equal(synthesized, PsoDb.Read(Path.Combine(work, "scskiller_gen.db")).Where(r => r.Tag == 'N').Select(r => PsoDb.NvState.Parse(r).Target).ToHashSet());
        Assert.Equal(body.Where(r => r.Tag == 'N').Select(r => r.Key).ToHashSet(), Planner.PlanInputs(body).Select(x => x.Key).ToHashSet());
        var crashed = synthesized.First();   // the warm reports a crash by the record's key: the input's key ('N') counts as crashed too
        Assert.Contains(nv.Select(n => n.Target).Zip(body.Where(r => r.Tag == 'N'), (t, r) => (t, r.Key)).Single(x => x.t == crashed).Key,
            SCSKiller.Core.App.ScsKiller.CrashInputs(new HashSet<string> { crashed }, plan.FilePath));
        var amd = planner.Build(d.Game, engine, index, null, Ff7.Amd with { StateIndependentCache = true }, Ff7.TempDir("redengine-plan-amd"), null, CancellationToken.None);
        Assert.DoesNotContain(PlanFile.Read(amd.FilePath).Records, r => r.Tag == 'N');
    }

    [Fact]
    public void RefusesOtherVersionsAndBrokenFiles()
    {
        var vs = Data.Value.Vs[0];
        Assert.NotNull(RedShaderCache.ReadMaterials(new MemoryStream(Materials([(1, vs)], [[1, 0, 0, 0, 0, 0]]))));
        Assert.Null(RedShaderCache.ReadMaterials(new MemoryStream(Materials([(1, vs)], [[1, 0, 0, 0, 0, 0]], version: 4))));
        var f = Materials([(1, vs)], [[1, 0, 0, 0, 0, 0]]);
        f[^48] = 2; // two shaders claimed, one stored
        Assert.Null(RedShaderCache.ReadMaterials(new MemoryStream(f)));
        Assert.Null(RedShaderCache.ReadMaterials(new MemoryStream(new byte[40])));
        var st = Static(vs);
        Assert.Single(RedShaderCache.ReadStatic(new MemoryStream(st))!);
        st[^28] = 3;
        Assert.Null(RedShaderCache.ReadStatic(new MemoryStream(st)));
    }

    /// <summary>No loop runs on a count or length the file can't hold, so a damaged file reads as null at once.</summary>
    [Fact]
    public void DamagedTechniquesReadAsNullFast()
    {
        var vs = Data.Value.Vs[0];
        ulong[] k = [1, 0, 0, 0, 0, 0];
        RedShaderCache.Materials? Read(byte[] f) => RedShaderCache.ReadMaterials(new MemoryStream(f));
        var sw = Stopwatch.StartNew();
        Assert.NotNull(Read(MaterialsRaw([(1, vs)], [Technique(k)])));
        Assert.Null(Read(MaterialsRaw([(1, vs)], [Technique(k, pathLength: [0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F])]))); // would wrap past 63 bits
        Assert.Null(Read(MaterialsRaw([(1, vs)], [Technique(k, pathLength: [0xFF, 0xFF, 0x7F])]))); // longer than the bytes left
        Assert.Null(Read(MaterialsRaw([(1, vs)], [Technique(k, names: uint.MaxValue)])));
        var whole = Materials([(1, vs)], [k, k]);
        Assert.Null(Read([.. whole[..(whole.Length / 2)], .. whole[^48..]])); // truncated, footer kept
        var huge = whole.ToArray();
        BitConverter.GetBytes(uint.MaxValue).CopyTo(huge, huge.Length - 44); // techniques claimed
        Assert.Null(Read(huge));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AShaderInflatingToAnotherSizeIsLeftOut()
    {
        var vs = Data.Value.Vs[0];
        var ok = new MemoryStream(Materials([(1, vs)], [[1, 0, 0, 0, 0, 0]]));
        var e = RedShaderCache.ReadMaterials(ok)!.Shaders[1];
        Assert.Equal(vs, RedShaderCache.Container(ok, e));
        Assert.Null(RedShaderCache.Container(ok, e with { Inflated = RedShaderCache.MaxPayload + 1 }));
        foreach (var off in new[] { -1, 1 })
        {
            var f = new MemoryStream(Materials([(1, vs)], [[1, 0, 0, 0, 0, 0]], inflatedOff: off));
            Assert.Null(RedShaderCache.Container(f, RedShaderCache.ReadMaterials(f)!.Shaders[1]));
        }
    }

    /// <summary>Slots VS, PS, GS, HS, DS, CS: a technique is a pipeline when each key is a shader of its slot's stage and the
    /// stages form one; its compute shader is a pipeline of its own. Whether the rule has a root signature for the set is
    /// decided at plan time.</summary>
    [Fact]
    public void TechniquesAreTheirSlotsStages()
    {
        Stage[] st = [Stage.Vertex, Stage.Pixel, Stage.Geometry, Stage.Hull, Stage.Domain, Stage.Compute];
        var shaders = st.ToDictionary(s => s.ToString(), s => new ShaderInfo(s.ToString(), s, "", 0, new(0, 0, 0, 0), [], [], []));
        var shaOf = st.Select((s, i) => (Key: (ulong)i + 1, Sha: s.ToString())).ToDictionary(x => x.Key, x => x.Sha);
        List<string>? G(params ulong[] t) => RedEngineReader.Graphics(t, shaOf, shaders);
        Assert.Equal(["Vertex", "Pixel"], G(1, 2, 0, 0, 0, 0));
        Assert.Equal(["Vertex", "Pixel", "Hull", "Domain"], G(1, 2, 0, 4, 5, 0));
        Assert.Equal(["Vertex"], G(1, 0, 0, 0, 0, 6)); // with its compute pass
        Assert.Equal([], G(0, 0, 0, 0, 0, 6));
        Assert.Null(G(0, 2, 0, 0, 0, 0));    // PS without a VS
        Assert.Null(G(1, 2, 0, 4, 0, 0));    // HS without a DS
        Assert.Null(G(1, 2, 0, 0, 5, 0));    // DS without a HS
        Assert.Equal(["Vertex", "Geometry"], G(1, 0, 3, 0, 0, 0));    // not a set the rule lists: left out at plan time
        Assert.Equal(["Vertex", "Pixel", "Geometry", "Hull", "Domain"], G(1, 2, 3, 4, 5, 0));
        Assert.Null(G(2, 1, 0, 0, 0, 0));    // a PS in the VS slot
        Assert.Null(G(1, 0, 0, 0, 0, 2));    // a PS in the CS slot
        Assert.Null(G(1, 9, 0, 0, 0, 0));    // a key the cache doesn't have
        Assert.Null(G(0, 0, 0, 0, 0, 0));
        Assert.False(RootSig.InStageSets(Rules.Red3, [Stage.Vertex, Stage.Geometry]));
        Assert.False(RootSig.InStageSets(Rules.Red3, [Stage.Mesh, Stage.Pixel]));
        Assert.Throws<RootSig.SerializeException>(() => Rs(Stage.Vertex, Stage.Geometry));
    }

    /// <summary>A server copy of the red3 rule with these stage sets (same id: it replaces the embedded one).</summary>
    static RulesFile Red3With(params string[][] stageSets)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { rules = new[] { Rules.Red3 with { StageSets = stageSets } } },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var server = RulesFile.TryParse(json)!;
        Assert.Empty(server.Dropped);
        return RulesFile.WithServer(server);
    }

    static readonly string[][] Red3Sets = Rules.Red3.StageSets!;

    static List<(char, string)> Records(Plan plan) => [.. PlanFile.Read(plan.FilePath).Records.Select(r => (r.Tag, Convert.ToHexString(r.Payload)))];

    /// <summary>The index keeps every technique; the rule decides at plan time which stage sets get a root signature. Under
    /// the embedded rules a VS+GS technique with a compute pass (no set the embedded rule lists) plans nothing: records and
    /// counts as for a cache without it, which is what an index-time filter gave. Once a server rule lists VS+GS, the same
    /// index plans its graphics pipeline; its compute pass stays out (it goes by the embedded rule's sets). A cache whose
    /// only technique is such a set isn't REDengine 3's.</summary>
    [Fact]
    public void ServerRulesWideningTheStageSetsPlanThemWithoutAReindex()
    {
        var d = Data.Value;
        var gs = Hlsl.Compile("""
            struct V { float2 uv : TEXCOORD0; float4 p : SV_Position; };
            [maxvertexcount(3)] void main(triangle V i[3], inout TriangleStream<V> o) { for (int k = 0; k < 3; k++) o.Append(i[k]); }
            """, "main", "gs_5_1");
        (ulong, byte[]?)[] shaders = [(10, d.Vs[0]), (11, d.Ps[0]), (12, gs), (14, d.Cs)];
        var (game, cache) = Install("redengine-widened");
        File.WriteAllBytes(cache, Materials(shaders, [[10, 11, 0, 0, 0, 0], [10, 0, 12, 0, 0, 14]]));
        var reader = new RedEngineReader();
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var vsGs = T(Tessellation, (Stage.Vertex, d.Vs[0]), (Stage.Geometry, gs));
        Assert.Contains(index.Maps, m => m.IsPipeline && m.Shaders.Contains(Sha(gs)));
        Assert.DoesNotContain(index.Maps, m => m.Shaders.Contains(Sha(d.Cs)));

        var (without, withoutCache) = Install("redengine-widened-without");
        File.WriteAllBytes(withoutCache, Materials(shaders, [[10, 11, 0, 0, 0, 0]]));
        var baseline = new Planner { Rules = RulesFile.Embedded }.Build(without, engine, reader.Index(without, engine, null, CancellationToken.None), null, Ff7.Nvidia,
            Ff7.TempDir("redengine-widened-without-plan"), null, CancellationToken.None);
        var embedded = new Planner { Rules = RulesFile.Embedded }.Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("redengine-widened-embedded"), null, CancellationToken.None);
        Assert.Equal(Records(baseline), Records(embedded));
        Assert.Equal(baseline.Stats, embedded.Stats);
        Assert.Equal([T(Graphics, (Stage.Vertex, d.Vs[0]), (Stage.Pixel, d.Ps[0]))], Psos(embedded));

        var widened = new Planner { Rules = Red3With([.. Red3Sets, ["VS", "GS"]]) }.Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("redengine-widened-server"), null, CancellationToken.None);
        Assert.Equal([.. Psos(embedded), vsGs], Psos(widened));

        File.WriteAllBytes(withoutCache, Materials(shaders, [[10, 0, 12, 0, 0, 14]]));
        Assert.Null(new RedEngineReader().Detect(without));

        static HashSet<string> Psos(Plan plan) => PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'S' or 'P')
            .Select(r => r.Tag == 'S' ? PsoDb.Tuple(PsoDb.Parse(r).Rs, PsoDb.Parse(r).Stages) : PsoDb.Tuple(PsoDb.ParseItem(r.Payload).Rs, PsoDb.ParseItem(r.Payload).Stages)).ToHashSet();
        string T(string rs, params (Stage S, byte[] B)[] st) => PsoDb.Tuple(rs, st.Select(x => new KeyValuePair<int, string>((int)x.S, Sha(x.B))));
    }

    /// <summary>A server red3 rule without the description for VS and VS+PS is dropped at load. Rules that still fail to
    /// build a plan (here such a rule made without the loader) leave the game the embedded rules' plan, never a failed
    /// compile, and the log says why.</summary>
    [Fact]
    public void ServerRulesThatCantPlanFallBackToTheEmbeddedPlan()
    {
        var noDefault = Rules.Red3 with { RootSignature = Rules.Red3.RootSignature! with { ByStages = [.. Rules.Red3.RootSignature!.ByStages!.Where(b => b.Any != null)] } };
        var json = System.Text.Json.JsonSerializer.Serialize(new { rules = new[] { noDefault } },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var loaded = RulesFile.TryParse(json)!;
        Assert.Empty(loaded.Rules);
        Assert.Equal("red3: bad byStages: a stage set with no description", Assert.Single(loaded.Dropped));

        var unchecked_ = (RulesFile)typeof(RulesFile).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single().Invoke([new List<PlanRule> { noDefault }, new List<string>()]);
        var d = Data.Value;
        var reader = new RedEngineReader();
        var engine = reader.Detect(d.Game)!;
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        var own = new Planner { Rules = RulesFile.Embedded }.Build(d.Game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("redengine-fallback-own"), null, CancellationToken.None);
        var log = new List<string>();
        var dir = Ff7.TempDir("redengine-fallback-server");
        var plan = new Planner { Rules = unchecked_ }.Build(d.Game, engine, index, null, Ff7.Nvidia, dir, new SyncLog(log.Add), CancellationToken.None);
        Assert.Equal(Records(own), Records(plan));
        Assert.Equal(own.Stats, plan.Stats);
        Assert.Equal(Path.Combine(dir, "plan.bin"), plan.FilePath);
        Assert.Contains(log, l => l.StartsWith("warning: planning rules: the server's rules for REDengine 3 DX12 build no plan (no red3 root signature for"));
    }

    /// <summary>A server rule replacing the embedded red3 rule that plans under 90% of its items (here: compute only) isn't
    /// used for the game: the plan is the embedded rules' one, record for record, and the log says why. One planning as
    /// much is used.</summary>
    [Fact]
    public void AServerRuleThatShrinksThePlanIsIgnoredForTheGame()
    {
        var d = Data.Value;
        var reader = new RedEngineReader();
        var engine = reader.Detect(d.Game)!;
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        var own = new Planner { Rules = RulesFile.Embedded }.Build(d.Game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("redengine-shrink-own"), null, CancellationToken.None);

        var log = new List<string>();
        var dir = Ff7.TempDir("redengine-shrink");
        var plan = new Planner { Rules = Red3With(["CS"]) }.Build(d.Game, engine, index, null, Ff7.Nvidia, dir, new SyncLog(log.Add), CancellationToken.None);
        Assert.Equal(Records(own), Records(plan));
        Assert.Equal(own.Stats, plan.Stats);
        Assert.Equal(Path.Combine(dir, "plan.bin"), plan.FilePath);
        Assert.False(Directory.Exists(Path.Combine(dir, "embedded-rules")));
        Assert.Contains(log, l => l.StartsWith("warning: planning rules: the server's rules for REDengine 3 DX12 plan 1 item, under 90% of the 6 the app's own rules plan"));

        log.Clear();   // another stage set the game doesn't use: as many items, the server's rule is used
        var same = new Planner { Rules = Red3With([.. Red3Sets, ["VS", "GS"]]) }.Build(d.Game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("redengine-shrink-same"), new SyncLog(log.Add), CancellationToken.None);
        Assert.Equal(Records(own), Records(same));
        Assert.DoesNotContain(log, l => l.Contains("planning rules"));
    }

    /// <summary>The embedded rules' plan is the same, record for record, whether they come as <see cref="RulesFile.Current"/>,
    /// passed in, or as a server copy that differs only in layout (no second plan then).</summary>
    [Fact]
    public void TheEmbeddedRulesPlanIsTheSameHoweverTheyCome()
    {
        var d = Data.Value;
        var reader = new RedEngineReader();
        var engine = reader.Detect(d.Game)!;
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        var copy = RulesFile.WithServer(RulesFile.TryParse(System.Text.Json.Nodes.JsonNode.Parse(SCSKiller.Core.App.ContentFile.Embedded("SCSKiller.Core.Planning.plan-rules.json"))!.ToJsonString())!);
        Assert.Equal(RulesFile.Embedded.Hash(engine), copy.Hash(engine));
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true }, Ff7.Amd with { StateIndependentCache = true } })
        {
            var plans = new[] { new Planner(), new Planner { Rules = RulesFile.Embedded }, new Planner { Rules = copy } }
                .Select((p, i) => p.Build(d.Game, engine, index, null, caps, Ff7.TempDir($"redengine-same-{i}"), null, CancellationToken.None)).ToList();
            Assert.All(plans, p => Assert.Equal(Records(plans[0]), Records(p)));
            Assert.All(plans, p => Assert.Equal(plans[0].Stats, p.Stats));
        }
    }

    sealed class SyncLog(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    sealed class Stub : IEngineReader
    {
        public static readonly EngineInfo Info = new("Carved", "DXIL", null, "D3D12", false, null);
        public EngineInfo? Detect(Game game) => Info;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => new("carved", [], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    static (Game Game, string Cache) Install(string name)
    {
        var dir = Ff7.TempDir(name);
        var cache = Path.Combine(dir, "content", "content0", "shaderdx12_0.cache");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        return (new Game($"test:{name}", "red", Store.Other, dir, Path.Combine(dir, "witcher3.exe")), cache);
    }

    /// <summary>A material cache that doesn't read whole (counts past its tables, bytes inserted before the footer, a
    /// technique table that doesn't parse) or whose techniques name no usable pipeline isn't REDengine 3: the next reader
    /// detects the game under its own engine, and the plan gets none of the REDengine 3 root signatures.</summary>
    [Fact]
    public void ADamagedCacheIsTheGenericReadersGame()
    {
        var d = Data.Value;
        var (game, cache) = Install("redengine-damaged");
        var good = Materials([(10, d.Vs[0]), (11, d.Ps[0])], [[10, 11, 0, 0, 0, 0]]);
        File.WriteAllBytes(cache, good);
        Assert.Equal(RedEngineReader.Family, new RedEngineReader().Detect(game)?.Family);

        var counts = good.ToArray();
        BitConverter.GetBytes(200u).CopyTo(counts, counts.Length - 44); // more techniques than the table holds
        var inserted = (byte[])[.. good[..^48], 0, 0, 0, 0, .. good[^48..]];
        foreach (var damaged in new[] { counts, inserted, MaterialsRaw([(10, d.Vs[0])], [Technique([10, 0, 0, 0, 0, 0], names: 1 << 20)]),
                     Materials([(11, d.Ps[0])], [[11, 0, 0, 0, 0, 0]]) }) // the last: a PS in the VS slot
        {
            File.WriteAllBytes(cache, damaged);
            Assert.Null(new RedEngineReader().Detect(game));
            Assert.Equal(Stub.Info, new EngineReaders((RedEngineReader.Family, new RedEngineReader()), ("Carved", new Stub())).Detect(game));
        }

        // the carver plans what it finds with the shaders' own root signatures, never REDengine 3's
        File.WriteAllBytes(Path.Combine(game.InstallDir, "shaders.bin"), [.. d.Vs[2], .. d.Ps[2]]);
        var carver = new CarvedReader();
        var engine = new EngineReaders((RedEngineReader.Family, new RedEngineReader()), (CarvedReader.Family, carver)).Detect(game)!;
        Assert.Equal(CarvedReader.Family, engine.Family);
        Assert.Null(RootSig.RuleFor(engine));
        var index = carver.Index(game, engine, null, CancellationToken.None);
        var plan = new Planner().Build(game, engine with { Unsupported = null }, index, null, Ff7.Nvidia, Ff7.TempDir("redengine-fallback-plan"), null, CancellationToken.None);
        var rs = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'S' or 'P')
            .Select(r => r.Tag == 'S' ? PsoDb.Parse(r).Rs : PsoDb.ParseItem(r.Payload).Rs).ToHashSet();
        Assert.NotEmpty(rs);
        Assert.Empty(rs.Intersect([Graphics, Tessellation, Compute]));
    }

    [Fact]
    public void IndexRefusesACacheDamagedSinceDetection()
    {
        var d = Data.Value;
        var (game, cache) = Install("redengine-changed");
        File.WriteAllBytes(cache, Materials([(10, d.Vs[0]), (11, d.Ps[0])], [[10, 11, 0, 0, 0, 0]]));
        var reader = new RedEngineReader();
        var engine = reader.Detect(game)!;
        File.WriteAllBytes(cache, Materials([(11, d.Ps[0])], [[11, 0, 0, 0, 0, 0]]));
        Assert.Throws<InvalidDataException>(() => reader.Index(game, engine, null, CancellationToken.None));
        File.WriteAllBytes(cache, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => reader.Index(game, engine, null, CancellationToken.None));
    }

    /// <summary>A recorded PSO whose shaders' only technique was left out (here a PS in the VS slot): those shaders are in the
    /// index but in no map, and the PSO still plans, replayed as recorded.</summary>
    [Fact]
    public void ARecordedPsoOfALeftOutTechniquePlans()
    {
        var d = Data.Value;
        var (game, cache) = Install("redengine-left-out");
        File.WriteAllBytes(cache, Materials([(10, d.Vs[0]), (11, d.Ps[0]), (20, d.Vs[1]), (21, d.Ps[1])], [[10, 11, 0, 0, 0, 0], [21, 20, 0, 0, 0, 0]]));
        var reader = new RedEngineReader();
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var (vs, ps) = (Sha(d.Vs[1]), Sha(d.Ps[1]));
        Assert.True(index.Shaders.ContainsKey(vs) && index.Shaders.ContainsKey(ps));
        Assert.DoesNotContain(index.Maps, m => m.Shaders.Contains(vs) || m.Shaders.Contains(ps));

        var rsBlob = RootSig.Serialize(RootSig.Build(Rules.Red3, new Dictionary<Stage, ShaderInfo> { [Stage.Vertex] = index.Shaders[vs], [Stage.Pixel] = index.Shaders[ps] }, false), []);
        var db = Path.Combine(game.InstallDir, "recording.db");
        using (var s = File.Create(db))
        {
            PsoDb.WriteBlob(s, Graphics, rsBlob);
            PsoDb.WriteBlob(s, vs, d.Vs[1]);
            PsoDb.WriteBlob(s, ps, d.Ps[1]);
            PsoDb.Write(s, 'S', PsoDb.Stream(Graphics, new SortedDictionary<int, string> { [(int)Stage.Vertex] = vs, [(int)Stage.Pixel] = ps }, null, 3, [PsoDb.R16G16B16A16Float], 0));
        }
        var plan = new Planner().Build(game, engine, index, new Recording(db), Ff7.Nvidia, Ff7.TempDir("redengine-left-out-plan"), null, CancellationToken.None);
        Assert.Equal(1, plan.Stats.Recorded);
        Assert.True(plan.Stats.RootSigRuleVerified);
    }

    /// <summary>The NVAPI state synthesized PSOs get from a recording: the slot and space at least 99% of its raster records
    /// share, with their most common options; none below that, none on AMD, none without a recording but for REDengine 3.</summary>
    [Fact]
    public void SynthesizedPsosTakeTheRecordingsNvapiState()
    {
        var recs = Enumerable.Range(0, 200).Select(i => new PsoDb.Rec('C', PsoDb.Compute(new string('a', 40), $"{i:x40}"))).ToList();
        PsoDb.Rec N(PsoDb.Rec r, uint slot, uint options = 0) => new PsoDb.NvState(r.Key, slot, 1, 1, options).ToRec();
        var nv = recs.Take(199).Select((r, i) => N(r, 12, i < 150 ? 0u : 17u)).ToList();   // one record without a state: 199 of 200
        Assert.Equal(new RtCollections.Nv(12, 1, 0), PlanBuilder.RasterNv(Ff7.Nvidia, "Unreal", recs, nv));
        Assert.Null(PlanBuilder.RasterNv(Ff7.Amd, "Unreal", recs, nv));
        Assert.Null(PlanBuilder.RasterNv(Ff7.Nvidia, "Unreal", recs, nv.Take(197)));                    // 197 of 200: under 99%
        Assert.Null(PlanBuilder.RasterNv(Ff7.Nvidia, "Unreal", recs, [.. nv.Take(150), .. recs.Skip(150).Take(49).Select(r => N(r, 5))])); // two slots
        Assert.Null(PlanBuilder.RasterNv(Ff7.Nvidia, "Unreal", recs, [.. nv, .. recs.Select(r => N(r, uint.MaxValue, 17))]));   // each one's last state: no slot
        Assert.Equal(new RtCollections.Nv(12, 1, 0), PlanBuilder.RasterNv(Ff7.Nvidia, "Unreal", recs, [.. recs.Select(r => N(r, 5)), .. nv]));   // nor an earlier one
        Assert.Equal(new RtCollections.Nv(12, 1, 0), PlanBuilder.RasterNv(Ff7.Nvidia, RedEngineReader.Family, [], []));
        Assert.Equal(new RtCollections.Nv(12, 1, 0), PlanBuilder.RasterNv(Ff7.Nvidia, RedEngineReader.Family, recs, []));   // a recording made on AMD: none recorded
        Assert.Equal(new RtCollections.Nv(12, 1, 0), PlanBuilder.RasterNv(Ff7.Nvidia, RedEngineReader.Family, recs, nv));
        Assert.Null(PlanBuilder.RasterNv(Ff7.Nvidia, "Unreal", [], []));
        Assert.Null(PlanBuilder.RasterNv(Ff7.Amd, RedEngineReader.Family, [], []));
    }

    /// <summary>A cache replaced by a shorter one after the index: its shaders are unavailable, never an exception.</summary>
    [Fact]
    public void ShadersGoneSinceTheIndexAreSkipped()
    {
        var d = Data.Value;
        var (game, cache) = Install("redengine-shorter");
        File.WriteAllBytes(cache, Materials([(10, d.Vs[0]), (11, d.Ps[0]), (12, d.Vs[1]), (13, d.Ps[1])], [[10, 11, 0, 0, 0, 0], [12, 13, 0, 0, 0, 0]]));
        var reader = new RedEngineReader();
        var engine = reader.Detect(game)!;
        reader.Index(game, engine, null, CancellationToken.None);
        var all = new[] { d.Vs[0], d.Ps[0], d.Vs[1], d.Ps[1] }.Select(Sha).ToHashSet();
        var got = new List<string>();
        reader.ReadShaders(game, engine, all, (h, _) => got.Add(h), CancellationToken.None);
        Assert.Equal(4, got.Count);
        File.WriteAllBytes(cache, Materials([(10, d.Vs[0])], [[10, 0, 0, 0, 0, 0]]));
        got.Clear();
        reader.ReadShaders(game, engine, all, (h, _) => got.Add(h), CancellationToken.None);
        Assert.Equal([Sha(d.Vs[0])], got); // the one still where the index found it
        File.Delete(cache);
        reader.ReadShaders(game, engine, all, (h, _) => got.Add(h), CancellationToken.None);
    }

    /// <summary>A material addition as The Witcher 3 records it: global root signature, shader config, pipeline config 1,
    /// state object config, the closest and any hit libraries, the hit group, the local root signature and its association.</summary>
    static PsoDb.Rec Addition(string global, uint payload, uint pipelineFlags)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        void Str(string? x) { if (x == null) { w.Write(uint.MaxValue); return; } w.Write((uint)x.Length); w.Write(Encoding.Unicode.GetBytes(x)); }
        w.Write(new byte[20]); w.Write(3u); w.Write(9u);
        w.Write(1u); w.Write(Convert.FromHexString(global));
        w.Write(9u); w.Write(payload); w.Write(8u);
        w.Write(12u); w.Write(1u); w.Write(pipelineFlags);
        w.Write(0u); w.Write(4u);
        w.Write(5u); w.Write(new byte[20]); w.Write(1u); Str("CH_LRS"); Str("ClosestHit"); w.Write(0u);
        w.Write(5u); w.Write(new byte[20]); w.Write(1u); Str("AH_LRS"); Str("AnyHit"); w.Write(0u);
        w.Write(11u); Str("HitGroup_0x1"); w.Write(0u); Str("AH_LRS"); Str("CH_LRS"); Str(null);
        w.Write(2u); w.Write(new byte[20]);
        w.Write(7u); w.Write(7u); w.Write(1u); Str("HitGroup_0x1");
        return new('A', s.ToArray());
    }

    /// <summary>The material hit groups' collections take the shape most recorded additions share; the local root signature
    /// is the fixed prefix, the hit group's space-0 SRV runs (always a table) and CBV runs (a table when any), registers the
    /// global one gives left out; an 'H' item round-trips.</summary>
    [Fact]
    public void MaterialHitGroupsTakeTheRecordedAdditionsShape()
    {
        var global = new string('9', 40);
        Assert.Null(RedRayTracing.Learn([]));
        Assert.Equal(new RedRayTracing.Shape(global, 32, 8, 1, 0x200),
            RedRayTracing.Learn([Addition(global, 32, 0x200), Addition(global, 32, 0x200), Addition(new string('8', 40), 16, 0)]));

        ShaderInfo Lib(params Binding[] b) => new("", Stage.Library, "lib_6_5", 0, new(0, 0, 0, 0), b, [], []);
        var g = new RootSig.Ranges(0, [(0, 2, 12, 1, 0, false), (0, 0, 4, 40, 0, true)]);   // the global root signature gives b12 and t4-t43 in space 0
        var d = RedRayTracing.Local([Lib(new("srv", 0, 0, 2), new("cbv", 0, 12, 1), new("cbv", 0, 2, 1)), Lib(new("srv", 0, 3, 1), new("srv", 0, 5, 1), new("srv", 1, 0, 1))], g)!;
        Assert.Equal((0x80u, true), (d.Flags, d.AppendRanges));
        Assert.Equal([[3, 0, 1, 1, 2], [3, 0, 2, 1, 2], [1, 0, 3, 1, 22], [0, 0, 0, 2, 0, 0, 3, 0, 1, 3, 0, 3], [0, 0, 2, 1, 2, 0, 3]], d.Rows);   // runs t0-t1 and t3 (t5 is the global's), b2
        Assert.Null(RedRayTracing.Local([Lib()], g));   // nothing bound: an empty SRV table last, which NVIDIA fails
        Assert.Null(RedRayTracing.Local([Lib(new Binding("srv", 0, 5, 1))], g));   // only registers the global one gives: the same
        Assert.Equal([[3, 0, 1, 1, 2], [3, 0, 2, 1, 2], [1, 0, 3, 1, 22], [0, 0], [0, 0, 2, 1, 2, 0, 3]], RedRayTracing.Local([Lib(new Binding("cbv", 0, 2, 1))], g)!.Rows);   // an empty SRV table before a CBV one works
        Assert.NotEmpty(RootSig.Serialize(d, []));
        var g47 = new RootSig.Ranges(0, [(0, 0, 4, 4, 0, true), (0, 0, 20, uint.MaxValue, 0, true)]);   // t4-t7, and t20 on unbounded
        Assert.Null(RedRayTracing.Local([Lib(new Binding("srv", 0, 0, 8))], g47));   // t0-t7: a local t0-t7 would overlap t4-t7
        Assert.Null(RedRayTracing.Local([Lib(new Binding("srv", 0, 18, 4))], g47));   // t18-t21: partly in the unbounded one
        Assert.Null(RedRayTracing.Local([Lib(new Binding("srv", 0, 0, -1))], g47));   // unbounded
        Assert.Equal([0, 0, 0, 4, 0, 0, 3], RedRayTracing.Local([Lib(new("srv", 0, 0, 4), new("srv", 0, 5, 2), new("srv", 0, 30, 1000))], g47)!.Rows[3]);   // t5-t6 and t30 on: the global's
        var huge = RedRayTracing.Local([Lib(new("srv", 0, 100, 100_000_000), new("srv", 0, 50, 60))], new RootSig.Ranges(0, []))!;
        Assert.Equal([0, 0, 0, 100_000_050, 50, 0, 3], huge.Rows[3]);   // one run, never register by register

        var shape = new RedRayTracing.Shape(global, 32, 8, 1, 0x200);
        var item = RedRayTracing.ParseItem(RedRayTracing.Item(new string('1', 40), new string('2', 40), new string('3', 40), shape, new RtCollections.Nv(12, 1, 0)));
        Assert.Equal(new RedRayTracing.ItemFields(new string('1', 40), new string('2', 40), new string('3', 40), shape, new RtCollections.Nv(12, 1, 0)), item);
        Assert.Null(RedRayTracing.ParseItem(RedRayTracing.Item(new string('1', 40), null, new string('3', 40), shape, null)).AnyHit);
        Assert.Equal([new string('1', 40), new string('2', 40), new string('3', 40), global],
            Rehydrate.References([new PsoDb.Rec('H', RedRayTracing.Item(new string('1', 40), new string('2', 40), new string('3', 40), shape, null))]).Order());
    }

    /// <summary>An 'H' item's collection: the global root signature, shader config, pipeline config 1, the two libraries with
    /// their hit shader exported, the hit group of the two, the local root signature and its association with the hit group;
    /// none when a library exports no hit shader of its kind.</summary>
    [Fact]
    public void AHitGroupsCollectionHasTheAdditionsSubobjects()
    {
        byte[] chLib = Planning.RtCollectionTests.Library((10, "MatCHS", 32)), ahLib = Planning.RtCollectionTests.Library((9, "MatAHS", 32));
        string ch = PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(chLib)), ah = PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(ahLib));
        string global = new('9', 40), local = new('3', 40);
        var f = RedRayTracing.ParseItem(RedRayTracing.Item(ch, ah, local, new RedRayTracing.Shape(global, 32, 8, 1, 0x200), null));
        var so = RedRayTracing.Collection(chLib, f, ahLib)!;

        var r = new BinaryReader(new MemoryStream(so));
        string H() => PsoDb.Hex(r.ReadBytes(20));
        string? S() => r.ReadUInt32() is var n && n == uint.MaxValue ? null : System.Text.Encoding.Unicode.GetString(r.ReadBytes(2 * (int)n));
        Assert.Equal((0u, 8u), (r.ReadUInt32(), r.ReadUInt32()));   // a collection of 8 subobjects
        Assert.Equal((1u, global), (r.ReadUInt32(), H()));
        Assert.Equal((9u, 32u, 8u), (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()));
        Assert.Equal((12u, 1u, 0x200u), (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()));
        Assert.Equal((5u, ch, 1u, "MatCHS", (string?)null, 0u), (r.ReadUInt32(), H(), r.ReadUInt32(), S(), S(), r.ReadUInt32()));
        Assert.Equal((5u, ah, 1u, "MatAHS", (string?)null, 0u), (r.ReadUInt32(), H(), r.ReadUInt32(), S(), S(), r.ReadUInt32()));
        var hitGroup = $"HitGroup_{ch[..16]}";
        Assert.Equal((11u, hitGroup, 0u, "MatAHS", "MatCHS", (string?)null), (r.ReadUInt32(), S(), r.ReadUInt32(), S(), S(), S()));
        Assert.Equal((2u, local), (r.ReadUInt32(), H()));
        Assert.Equal((7u, 6u, 1u, hitGroup), (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), S()));   // the local root signature's subobject, 0-based
        Assert.Equal(so.Length, r.BaseStream.Position);
        var parsed = PsoDb.ParseStateObject(new PsoDb.Rec('R', so));
        Assert.Equal([ch, ah], parsed.Libraries);
        Assert.Equal([global, local], parsed.RootSignatures);

        Assert.Null(RedRayTracing.Collection(ahLib, f, ahLib));   // no closest hit shader
        Assert.Null(RedRayTracing.Collection(chLib, f, chLib));   // no any hit shader
        Assert.NotNull(RedRayTracing.Collection(chLib, f with { AnyHit = null }, []));
    }

    /// <summary>A technique's ray tracing keys name its hit group: the closest hit library, then the any hit one; none when a
    /// key isn't a library of the cache.</summary>
    [Fact]
    public void TechniquesNameTheirHitGroups()
    {
        var shaders = new Dictionary<string, ShaderInfo>
        {
            ["ch"] = new("ch", Stage.Library, "lib_6_5", 0, new(0, 0, 0, 0), [], [], []), ["ah"] = new("ah", Stage.Library, "lib_6_5", 0, new(0, 0, 0, 0), [], [], []),
            ["vs"] = new("vs", Stage.Vertex, "vs_6_0", 0, new(0, 0, 0, 0), [], [], []),
        };
        var shaOf = new Dictionary<ulong, string> { [1] = "ch", [2] = "ah", [3] = "vs" };
        Assert.Equal(["ch", "ah"], RedEngineReader.HitGroup([3, 0, 0, 0, 0, 0, 1, 2], shaOf, shaders));
        Assert.Equal(["ch"], RedEngineReader.HitGroup([3, 0, 0, 0, 0, 0, 1, 0], shaOf, shaders));
        Assert.Null(RedEngineReader.HitGroup([3, 0, 0, 0, 0, 0, 0, 0], shaOf, shaders));
        Assert.Null(RedEngineReader.HitGroup([3, 0, 0, 0, 0, 0, 3, 2], shaOf, shaders));   // a VS where the closest hit library goes
        Assert.Null(RedEngineReader.HitGroup([3, 0, 0, 0, 0, 0, 1, 9], shaOf, shaders));
    }
}
