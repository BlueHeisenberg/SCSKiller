using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.ReEngine;
using SCSKiller.Tests.Carved;

namespace SCSKiller.Tests.ReEngine;

/// <summary>RE Engine's root signatures (the re-pragmata and re-re8 rules) against the SHA-1s of root signatures PRAGMATA and
/// RE Village created in recordings, and the pipelines their master materials list.</summary>
public class ReEngineRuleTests
{
    static PlanRule Pragmata => RulesFile.Embedded.Rules.Single(r => r.Id == "re-pragmata");
    static PlanRule Village => RulesFile.Embedded.Rules.Single(r => r.Id == "re-re8");

    static ShaderInfo S(Stage stage, params Binding[] bindings) => new("", stage, "", 0, new ResourceCounts(0, 0, 0, 0), bindings, [], []);
    static Binding Cbv(int count) => new("cbv", 0, 0, count);
    static Binding Srv(int count) => new("srv", 0, 0, count);
    static Binding Uav(int count) => new("uav", 0, 0, count);

    public static TheoryData<string, Stage[], int, int> Recorded => new()
    {
        { "2ba85be230d215aab4c3efbe081442ce7618df4e", [Stage.Vertex, Stage.Pixel], 5, 5 },   // CBVs to b4 -> 3, SRVs to t4 -> 8
        { "fa2bb504b67699c22e1cee1ecab020be25062511", [Stage.Vertex, Stage.Pixel], 3, 1 },   // t0 -> 2
        { "d250467fc905773310eb349ad1730f3e6ae02722", [Stage.Vertex, Stage.Pixel], 8, 10 },  // b7 -> 10
        { "920b87a28fc8937fe095aaee2337bd6f17a67041", [Stage.Vertex, Stage.Pixel], 8, 16 },  // t15 -> 32, not 16
        { "e1182f5cd3721604359fbf4621f11d18c6403994", [Stage.Mesh, Stage.Pixel], 2, 5 },
        { "6ae72d77fc8a42b1e2f08ce7334f8d64b30d2060", [Stage.Vertex, Stage.Geometry, Stage.Pixel], 2, 4 },
        { "eb39d741aba9b4b1c5bb694bc8878658afa7cb51", [Stage.Vertex], 1, 1 },
        { "257dc59a262e80cdcaef83b0a3eb48bd3f1e8262", [Stage.Compute], 9, 12 },
    };

    /// <summary>The bindings go on the last stage; the sizes are the pipeline's, so every stage's tables get them.</summary>
    [Theory]
    [MemberData(nameof(Recorded))]
    public void RebuildsPragmatasRootSignaturesByteForByte(string sha1, Stage[] stages, int cbvs, int srvs)
    {
        var set = stages.ToDictionary(s => s, s => S(s));
        set[stages[^1]] = S(stages[^1], Cbv(cbvs), Srv(srvs));
        var blob = RootSig.Serialize(RootSig.Build(Pragmata, set, meshTier: false), RootSig.StaticSamplers(Pragmata));
        Assert.Equal(sha1, Convert.ToHexStringLower(SHA1.HashData(blob)));
    }

    public static TheoryData<string, Stage[], int, int, int> RecordedVillage => new()
    {
        { "931c1168fc5afbc92df5b8c2dfe370dae1a3d4f1", [Stage.Vertex, Stage.Pixel], 4, 2, 0 },   // root CBVs only -> 3, t0-t1 -> 4, no UAV -> 2
        { "cef6724166cfbdb2f69ab1774b71ecc1cecbff66", [Stage.Vertex, Stage.Pixel], 5, 3, 2 },   // u0-u1 -> 4, not 2
        { "1b80217732e8f06ffc52510705b886166661ee09", [Stage.Vertex, Stage.Pixel], 7, 16, 0 },  // b4-b6 -> 10, not 3; t15 -> 32
        { "9ca38286ac4bf14e05ea4e05d69c53ef943218d6", [Stage.Vertex, Stage.Pixel], 2, 0, 4 },   // u3 -> 8
        { "d9d8dd72fbc373d6ee98f94f0811f7f17ba32349", [Stage.Vertex], 1, 4, 0 },
        { "307ce50544d5fff6fc7eb521edcb9ebc86e8e54e", [Stage.Vertex, Stage.Hull, Stage.Domain, Stage.Pixel], 1, 5, 0 },
        { "1a4ac12f1e9f6b45f24759b5c7273b23397b0936", [Stage.Vertex, Stage.Geometry, Stage.Pixel], 1, 1, 2 },
        { "5b8d9967654be4a7c11dba0223e96f88d432b243", [Stage.Compute], 9, 12, 0 },
    };

    /// <summary>RE Village's: no mesh or amplification deny, no heap flag or static samplers, CBV and UAV tables sized above
    /// what the pipeline binds, then 8 bindless SRV tables (spaces 1-8).</summary>
    [Theory]
    [MemberData(nameof(RecordedVillage))]
    public void RebuildsVillagesRootSignaturesByteForByte(string sha1, Stage[] stages, int cbvs, int srvs, int uavs)
    {
        var set = stages.ToDictionary(s => s, s => S(s));
        set[stages[^1]] = S(stages[^1], Cbv(cbvs), Srv(srvs), Uav(uavs));
        var blob = RootSig.Serialize(RootSig.Build(Village, set, meshTier: false), RootSig.StaticSamplers(Village));
        Assert.Equal(sha1, Convert.ToHexStringLower(SHA1.HashData(blob)));
    }

    [Fact]
    public void ATableNoSizeHoldsHasNoRootSignature()
    {
        Assert.Throws<RootSig.SerializeException>(() => RootSig.Build(Pragmata, new Dictionary<Stage, ShaderInfo> { [Stage.Pixel] = S(Stage.Pixel, Srv(128)) }, false));
        Assert.Throws<RootSig.SerializeException>(() => RootSig.Build(Pragmata, new Dictionary<Stage, ShaderInfo> { [Stage.Pixel] = S(Stage.Pixel, new Binding("srv", 0, 0, -1)) }, false));
        Assert.Throws<RootSig.SerializeException>(() => RootSig.Build(Pragmata, new Dictionary<Stage, ShaderInfo> { [Stage.Pixel] = S(Stage.Pixel, Cbv(15)) }, false));
    }

    [Fact]
    public void EachTitleGetsItsOwnRule()
    {
        var e = new EngineInfo(ReEngineReader.Family, "PAK 4.2", "PRAGMATA", "D3D12", false, null);
        Assert.Equal("re-pragmata", RootSig.RuleFor(e)?.Id);
        Assert.True(RootSig.Verified(e));
        Assert.Equal("re-re8", RootSig.RuleFor(e with { Version = "PAK 4.0", Fork = "re8" })?.Id);
        Assert.True(RootSig.Verified(e with { Version = "PAK 4.0", Fork = "re8" }));
        Assert.Null(RootSig.RuleFor(e with { Fork = "re4" }));
        Assert.Null(RootSig.RuleFor(e with { Fork = null }));
        Assert.Equal("re8", ReEngineReader.Fork(new Game("t", "t", Store.Steam, "C:\\g", "C:\\g\\re8.exe")));
        Assert.Equal("MonsterHunterWilds", ReEngineReader.Fork(new Game("t", "t", Store.Steam, "C:\\g", "C:\\g\\Monster Hunter Wilds.exe")));
    }

    /// <summary>A pipeline whose shaders bind more than any table size holds (here an unbounded SRV range in space 0) gets
    /// no root signature from the rule: left out and counted, and the plan is built with the others.</summary>
    [Fact]
    public void APipelineTheRuleCantBuildIsLeftOut()
    {
        SigElement pos = new("SV_Position", 0, 0, 0xF, 1, 3), uv = new("TEXCOORD", 0, 1, 0x3, 0, 3), target = new("SV_Target", 0, 0, 0xF, 0, 3);
        ShaderInfo Sh(string name, Stage stage, Binding[] b) => new(Planning.ExactLayoutsTests.Hash(name), stage, stage == Stage.Pixel ? "ps_6_6" : "vs_6_6", 100,
            new ResourceCounts(0, 0, 0, 0), b, stage == Stage.Pixel ? [pos, uv] : [new("POSITION", 0, 0, 7, 0, 3)], stage == Stage.Pixel ? [target] : [pos, uv]);
        var vs = Sh("re-vs", Stage.Vertex, [Cbv(1)]);
        var ps = Sh("re-ps", Stage.Pixel, [Srv(3)]);
        var huge = Sh("re-ps-huge", Stage.Pixel, [new("srv", 0, 0, -1)]);
        var index = new ShaderIndex("re-unbuilt", ["PCD3D_SM6"], new[] { vs, ps, huge }.ToDictionary(s => s.Sha1),
            [new ShaderMap("a", "m", "PCD3D_SM6", [vs.Sha1, ps.Sha1], IsPipeline: true), new ShaderMap("b", "m", "PCD3D_SM6", [vs.Sha1, huge.Sha1], IsPipeline: true)]);
        var engine = new EngineInfo(ReEngineReader.Family, "PAK 4.2", "PRAGMATA", "D3D12", false, null);
        var log = new List<string>();
        var plan = new Planner().Build(Planning.Ff7.Game, engine, index, null, Planning.Ff7.Nvidia, Planning.Ff7.TempDir("re-unbuilt"), new Log(log.Add), CancellationToken.None);
        var planned = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'S' or 'G').Select(r => PsoDb.Parse(r).Stages.Values.ToHashSet()).ToList();
        Assert.Contains(planned, s => s.SetEquals([vs.Sha1, ps.Sha1]));
        Assert.DoesNotContain(planned, s => s.Contains(huge.Sha1));
        Assert.Contains(log, l => l.StartsWith("warning: 1 stage sets left out: the rule builds no root signature") && l.Contains("no SRV table size"));
    }

    /// <summary>RE Engine traces rays inline from compute shaders and ships no DXIL library: the plan counts its RayQuery shaders.</summary>
    [Fact]
    public void RayQueryComputeShadersCountAsInline()
    {
        var cs = new ShaderInfo(Planning.ExactLayoutsTests.Hash("re-rq"), Stage.Compute, "cs_6_5", 100, new ResourceCounts(0, 0, 0, 0), [Cbv(1)], [], [], InlineRayTracing: true);
        var index = new ShaderIndex("re-rq", ["PCD3D_SM6"], new[] { cs }.ToDictionary(s => s.Sha1), [new ShaderMap("c", "m", "PCD3D_SM6", [cs.Sha1], IsPipeline: true)]);
        var engine = new EngineInfo(ReEngineReader.Family, "PAK 4.2", "PRAGMATA", "D3D12", false, null);
        var plan = new Planner().Build(Planning.Ff7.Game, engine, index, null, Planning.Ff7.Nvidia, Planning.Ff7.TempDir("re-rq"), null, CancellationToken.None);
        Assert.Equal((long?)1, plan.Stats.RtInline);
    }

    /// <summary>A listed entry is a pipeline only when the runtime would create it: an entry pairing a depth-only VS with a PS
    /// reading its attributes isn't a map (its PS, named by no other entry, pools with the VSs that feed it), nor an AS with
    /// a mesh shader taking another payload.</summary>
    [Fact]
    public void EntriesWhoseStagesDontLinkAreNotPipelines()
    {
        var dir = Planning.Ff7.TempDir("reengine-unlinked");
        var (vs, ps, other) = (Hlsl.Vs(1, Hlsl.Rs1), Hlsl.Ps(1, Hlsl.Rs1), Hlsl.Ps(2, Hlsl.Rs1));
        var depth = Hlsl.Compile($"[RootSignature(\"{Hlsl.Rs1}\")] float4 main(float3 p : POSITION) : SV_Position {{ return float4(p, 1); }}", "main", "vs_5_1");
        var table = Material(4, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0]).Length;
        long vsAt = table, psAt = vsAt + vs.Length + 16, depthAt = psAt + ps.Length + 16, otherAt = depthAt + depth.Length + 16;
        byte[] material = [.. Material(4, [vsAt, vsAt, 0, 0, 0, psAt], [depthAt, depthAt, 0, 0, 0, otherAt]), .. vs, .. new byte[16], .. ps, .. new byte[16],
            .. depth, .. new byte[16], .. other, .. new byte[16]];
        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak"), RePakTests.Pak(0, 256, new RePakTests.E(1, material, 2)));
        var game = new Game("test:re-unlinked", "RE test", Store.Other, dir, Path.Combine(dir, "game.exe"));
        var reader = new ReEngineReader(Path.Combine(dir, "data"), _ => null);
        var index = reader.Index(game, reader.Detect(game)!, null, CancellationToken.None);
        string Sha(byte[] c) => Convert.ToHexStringLower(SHA1.HashData(c));
        Assert.Equal([Sha(vs), Sha(ps)], Assert.Single(index.Maps, m => m.IsPipeline).Shaders);
        Assert.Contains(index.Maps, m => !m.IsPipeline && m.Shaders.Contains(Sha(other)) && m.Shaders.Contains(Sha(vs)));
        Directory.Delete(dir, true);

        ShaderInfo Mesh(Stage stage, int payload) => new(stage.ToString(), stage, "", 0, new ResourceCounts(0, 0, 0, 0), [], [], [], Payload: payload);
        Assert.False(ReEngineReader.Creatable([Mesh(Stage.Amplification, 128), Mesh(Stage.Mesh, 0)]));
        Assert.True(ReEngineReader.Creatable([Mesh(Stage.Amplification, 128), Mesh(Stage.Mesh, 128)]));
        Assert.False(ReEngineReader.Creatable([Mesh(Stage.Mesh, 0), Mesh(Stage.Mesh, 0)]));

        SigElement uv1 = new("TEXCOORD", 0, 1, 0x3, 0, 3), uv2 = uv1 with { Register = 2 };
        ShaderInfo Sm5(Stage stage, string model, SigElement uv) => new(stage.ToString(), stage, model, 0, new ResourceCounts(0, 0, 0, 0), [],
            stage == Stage.Pixel ? [uv] : [], stage == Stage.Pixel ? [] : [new("SV_Position", 0, 0, 0xF, 1, 3), uv]);
        Assert.True(ReEngineReader.Creatable([Sm5(Stage.Vertex, "vs_5_0", uv1), Sm5(Stage.Pixel, "ps_5_0", uv1)]));
        Assert.False(ReEngineReader.Creatable([Sm5(Stage.Vertex, "vs_5_0", uv1), Sm5(Stage.Pixel, "ps_5_0", uv2)]));   // DXBC links by register
        Assert.False(ReEngineReader.Creatable([Sm5(Stage.Vertex, "vs_6_6", uv1), Sm5(Stage.Pixel, "ps_6_6", uv1 with { CompType = 1 })]));   // float written, uint read

        ShaderInfo Ms(params SigElement[] outs) => new("ms", Stage.Mesh, "ms_6_6", 0, new ResourceCounts(0, 0, 0, 0), [], [], [new("SV_Position", 0, 0, 0xF, 1, 3), .. outs]);
        var reads = Sm5(Stage.Pixel, "ps_6_6", uv1);
        Assert.False(ReEngineReader.Creatable([Ms(), reads]));                                   // TEXCOORD0 read, never written
        Assert.True(ReEngineReader.Creatable([Ms(uv1 with { Register = 3, Mask = 0xC }), reads]));   // written elsewhere: mesh linkage packs freely
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    /// <summary>A master material: a 32-byte header and 4n entries of 272 bytes starting with the containers' offsets (version
    /// 5: 24 and 264).</summary>
    static byte[] Material(ushort version, params long[][] entries)
    {
        var (header, entry) = version == 5 ? (24, 264) : (32, 272);
        var n = (entries.Length + 3) / 4;
        var b = new byte[header + 4 * n * entry];
        "SDF\0"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)n);
        for (var i = 0; i < entries.Length; i++)
            for (var slot = 0; slot < entries[i].Length; slot++)
                BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(header + entry * i + 8 * slot), entries[i][slot]);
        return b;
    }

    [Fact]
    public void MasterMaterialsListTheirPipelines()
    {
        var at = new HashSet<long> { 5000, 6000, 7000, 8000 };
        // VS (twice) + PS; VS alone; MS in the HS slot + PS; a lone CS; an empty entry
        var b = Material(4, [5000, 5000, 0, 0, 0, 6000], [5000, 5000], [0, 0, 7000, 0, 0, 6000], [0, 0, 0, 0, 0, 0, 8000], []);
        Assert.Equal([[5000, 6000], [5000], [7000, 6000], [8000]], ReEngineReader.Pipelines(b, at));
        Assert.Equal([[5000, 6000], [5000], [7000, 6000], [8000]],
            ReEngineReader.Pipelines(Material(5, [5000, 5000, 0, 0, 0, 6000], [5000, 5000], [0, 0, 7000, 0, 0, 6000], [0, 0, 0, 0, 0, 0, 8000], []), at));
        Assert.Null(ReEngineReader.Pipelines(Material(3, [5000, 5000, 0, 0, 0, 6000]), at));    // another version
        Assert.Null(ReEngineReader.Pipelines(Material(6, [5000, 5000, 0, 0, 0, 6000]), at));
        Assert.Null(ReEngineReader.Pipelines(Material(4, [5000, 5000, 0, 0, 0, 6004]), at));    // not a container: laid out otherwise
        Assert.Null(ReEngineReader.Pipelines(b[..300], at));                                     // shorter than its table
    }

    /// <summary>The reader end to end: a listed pair is one exact map; the shaders no entry names pair with each other and
    /// with the listed ones of the other side, never listed with listed.</summary>
    [Fact]
    public void ListedPipelinesAreExactMaps()
    {
        var dir = Planning.Ff7.TempDir("reengine-listed");
        var (vs, ps, extra, vs2) = (Hlsl.Vs(1, Hlsl.Rs1), Hlsl.Ps(1, Hlsl.Rs1), Hlsl.Ps(2, Hlsl.Rs1), Hlsl.Vs(2, Hlsl.Rs1));
        var table = Material(4, [0, 0, 0, 0, 0, 0]).Length;
        long vsAt = table, psAt = vsAt + vs.Length + 16;
        byte[] material = [.. Material(4, [vsAt, vsAt, 0, 0, 0, psAt]), .. vs, .. new byte[16], .. ps, .. new byte[16], .. extra, .. new byte[16], .. vs2, .. new byte[16]];
        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak"), RePakTests.Pak(0, 256, new RePakTests.E(1, material, 2)));
        var game = new Game("test:re-listed", "RE test", Store.Other, dir, Path.Combine(dir, "game.exe"));
        var reader = new ReEngineReader(Path.Combine(dir, "data"), _ => null);
        var index = reader.Index(game, reader.Detect(game)!, null, CancellationToken.None);
        string Sha(byte[] c) => Convert.ToHexStringLower(SHA1.HashData(c));
        var exact = Assert.Single(index.Maps, m => m.IsPipeline);
        Assert.Equal([Sha(vs), Sha(ps)], exact.Shaders);
        var pools = index.Maps.Where(m => !m.IsPipeline).Select(m => m.Shaders.ToHashSet()).ToList();
        Assert.Equal(2, pools.Count);
        Assert.Contains(pools, p => p.SetEquals([Sha(extra), Sha(vs2), Sha(vs)]));   // the unlisted ones, with the listed VS
        Assert.Contains(pools, p => p.SetEquals([Sha(vs2), Sha(ps)]));               // the unlisted VS with the listed PS
        Assert.DoesNotContain(pools, p => p.Contains(Sha(vs)) && p.Contains(Sha(ps)));
        Directory.Delete(dir, true);
    }
}
