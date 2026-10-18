using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Unreal.ShaderContainer;

namespace SCSKiller.Tests.Planning;

/// <summary>The embedded rules (plan-rules.json) by the ids the tests name them.</summary>
static class Rules
{
    static PlanRule Get(string id) => RulesFile.Embedded.Rules.Single(r => r.Id == id);
    public static PlanRule Ff7 => Get("ue-ff7");
    public static PlanRule Ue420 => Get("ue-4.20");
    public static PlanRule Ue421 => Get("ue-4.21");
    public static PlanRule Ue422 => Get("ue-4.22");
    public static PlanRule Ue425 => Get("ue-4.25");
    public static PlanRule Ue426 => Get("ue-4.26");
    public static PlanRule Kuro => Get("ue-kuro");
    public static PlanRule Ue50 => Get("ue-5.0");
    public static PlanRule Ue51 => Get("ue-5.1");
    public static PlanRule Ue54 => Get("ue-5.4");
    public static PlanRule Ue55 => Get("ue-5.5");
    public static PlanRule Ue58 => Get("ue-5.8");
    public static PlanRule Red3 => Get("red3");
    public static PlanRule Northlight => Get("northlight");
    public static PlanRule Dagor => Get("dagor");
    public static PlanRule DagorCbvRanges => Get("dagor-cbv-ranges");
    public static RootSig.Desc Ue425Global => Get("ue-4.25-rt").Rt!.Global!.ToDesc();
    public static RootSig.Desc Ue426Global => Get("ue-4.26-rt").Rt!.Global!.ToDesc();
    public static RootSig.Desc KuroGlobal => Get("ue-kuro-rt").Rt!.Global!.ToDesc();
    public static RootSig.Desc Ue51Global => Get("ue-5.0-rt").Rt!.Global!.ToDesc();
    public static RootSig.Desc Ue54Global => Get("ue-5.4-rt").Rt!.Global!.ToDesc();
    public static RootSig.Desc NorthlightCompute => Get("northlight-rt").Rt!.Global!.ToDesc();
    public static RootSig.Desc NorthlightLocal => Get("northlight-rt").Rt!.Local!.ToDesc();
}

/// <summary>The embedded rules reproduce the rules the planner had in code (<see cref="Legacy"/>, kept here verbatim) byte for
/// byte: the rule each engine gets, every root signature built from the RootSig tests' fixtures, the ray tracing rules. And
/// the loader: capabilities, validation, server rules by id, signatures, the per-game hash.</summary>
public class PlanRulesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-rules-test-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static EngineInfo E(string family, string version, string? fork = null) => new(family, version, fork, "D3D12", false, null);

    static readonly string[] Families = ["Unreal", "REDengine 3", "FromSoftware", "Northlight", "Dagor", "Carved", "Unity", "RE Engine"];
    static readonly string[] Versions = ["4.18", "4.19", "4.20", "4.21", "4.22", "4.23", "4.24", "4.25", "4.26", "4.27", "4.28", "5.0", "5.1", "5.2", "5.3",
        "5.4", "5.5", "5.6", "5.7", "5.8", "5.9", "5.10", "5.12", "6.0", "6.1", "7.0", "4", "DX12", "GAME_Something", "", "DXIL+RTS0", "11.3"];
    static readonly string?[] Forks = [null, "GAME_FinalFantasy7Rebirth", "GAME_StellarBlade", "GAME_StarWarsJediSurvivor", "GAME_Palworld", "cbv-ranges"];
    static IEnumerable<EngineInfo> Engines() => from f in Families from v in Versions from k in Forks select E(f, v, k);

    static readonly Dictionary<Legacy.Rule, string> Ids = new()
    {
        [Legacy.Rule.Ff7] = "ue-ff7", [Legacy.Rule.Ue420] = "ue-4.20", [Legacy.Rule.Ue421] = "ue-4.21", [Legacy.Rule.Ue422] = "ue-4.22",
        [Legacy.Rule.Ue425] = "ue-4.25", [Legacy.Rule.Ue426] = "ue-4.26", [Legacy.Rule.Ue50] = "ue-5.0", [Legacy.Rule.Ue51] = "ue-5.1",
        [Legacy.Rule.Ue54] = "ue-5.4", [Legacy.Rule.Ue55] = "ue-5.5", [Legacy.Rule.Ue58] = "ue-5.8", [Legacy.Rule.Red3] = "red3",
        [Legacy.Rule.Northlight] = "northlight", [Legacy.Rule.Dagor] = "dagor", [Legacy.Rule.DagorCbvRanges] = "dagor-cbv-ranges",
    };

    [Fact]
    public void EmbeddedRulesAllLoad()
    {
        var raw = JsonNode.Parse(ContentFile.Embedded("SCSKiller.Core.Planning.plan-rules.json"))!["rules"]!.AsArray();
        Assert.Empty(RulesFile.Embedded.Dropped);
        Assert.Equal(raw.Count, RulesFile.Embedded.Rules.Count);
        Assert.Equal(Ids.Values.Concat(["re-pragmata", "re-re8", "nixxes-got", "nixxes-rc", "ue-kuro"]).Order(), RulesFile.Embedded.Rules.Where(r => r.RootSignature != null).Select(r => r.Id).Order());
    }

    /// <summary>Every engine family, version and fork gets the rule the code gave it, with its verification and static samplers.</summary>
    [Fact]
    public void RuleForMatchesTheCodeRules()
    {
        var rules = RulesFile.Embedded;
        foreach (var e in Engines())
        {
            var legacy = Legacy.RuleFor(e);
            var rule = rules.RuleFor(e);
            Assert.True((legacy is { } l ? Ids[l] : null) == rule?.Id, $"{e}: {legacy} vs {rule?.Id}");
            Assert.Equal(legacy is { } r && (r == Legacy.Rule.Red3 || ConfirmedEngines.Embedded.Contains(e)), rule is { } n && (n.Verified || ConfirmedEngines.Embedded.Contains(e)));
            if (legacy is { } x) Assert.Equal(Legacy.StaticSamplers(x), RootSig.StaticSamplers(rule!));
        }
    }

    static ShaderInfo S(Stage stage, int cb, int srv, int uav, int sampler, int flags = 0, params Binding[] bindings) =>
        new("", stage, "", 0, new ResourceCounts(cb, srv, uav, sampler, flags), bindings, [], []);

    // RootSigRulesTests', TessellationTests' and the bindless tests' fixtures
    static readonly ShaderInfo[] VsVariants =
    [
        S(Stage.Vertex, 1, 0, 0, 0), S(Stage.Vertex, 2, 4, 1, 0),
        S(Stage.Vertex, 1, 0, 0, 0) with { Inputs = [new SigElement("ATTRIBUTE", 0, 0, 0xF, 0, 3)] },
        S(Stage.Vertex, 1, 2, 0, 1, 0, new Binding("srv", 4, 0, -1)),
    ];
    static readonly ShaderInfo[] PsVariants =
    [
        S(Stage.Pixel, 2, 4, 4, 1), S(Stage.Pixel, 1, 1, 0, 1, 0, new("cbv", 0, 0, 1), new("srv", 0, 0, 1), new("srv", 11, 0, -1)),
        S(Stage.Pixel, 1, 1, 0, 1, 0, new Binding("srv", 7, 0, -1)), S(Stage.Pixel, 1, 0, 0, 0, UeFlags.BindlessResources), S(Stage.Pixel, 0, 0, 0, 0),
        S(Stage.Pixel, 1, 58, 0, 1), S(Stage.Pixel, 2, 4, 4, 1, UeFlags.NvIntrinsics),
    ];
    static readonly ShaderInfo Gs = S(Stage.Geometry, 3, 1, 0, 0), Hs = S(Stage.Hull, 1, 1, 0, 0), Ds = S(Stage.Domain, 1, 2, 0, 1),
        Ms = S(Stage.Mesh, 4, 10, 1, 1, UeFlags.RootConstants), As = S(Stage.Amplification, 1, 1, 1, 0);
    static readonly ShaderInfo[] CsVariants =
    [
        S(Stage.Compute, 1, 10, 2, 2), S(Stage.Compute, 4, 16, 6, 3, UeFlags.RootConstants), S(Stage.Compute, 1, 0, 1, 0, UeFlags.DiagnosticBuffer),
        S(Stage.Compute, 0, 0, 0, 0, UeFlags.AmdIntrinsics | UeFlags.RootConstants | UeFlags.DiagnosticBuffer | UeFlags.BindlessResources | UeFlags.BindlessSamplers),
        S(Stage.Compute, 1, 1, 1, 0, 0, new("srv", 42, 0, -1), new("srv", 11, 0, -1), new("srv", 13, 0, -1), new("srv", 31, 0, -1)),
        S(Stage.Compute, 0, 1, 1, 0, 0, new("srv", 5, 0, -1), new("srv", 4, 0, -1)),
        S(Stage.Compute, 0, 0, 1, 0, UeFlags.AmdIntrinsics | UeFlags.RootConstants | UeFlags.NvIntrinsics | UeFlags.DiagnosticBuffer),
    ];

    /// <summary>Every graphics combination of the fixtures (each stage absent or one of its variants) and every compute one.</summary>
    static IEnumerable<Dictionary<Stage, ShaderInfo>> StageSets()
    {
        foreach (var cs in CsVariants) yield return new() { [Stage.Compute] = cs };
        foreach (var vs in VsVariants.Prepend(null))
            foreach (var ps in PsVariants.Prepend(null))
                foreach (var gs in new[] { null, Gs })
                    foreach (var hs in new[] { null, Hs })
                        foreach (var ds in new[] { null, Ds })
                            foreach (var ms in new[] { null, Ms })
                                foreach (var @as in new[] { null, As })
                                {
                                    var set = new[] { vs, ps, gs, hs, ds, ms, @as }.OfType<ShaderInfo>().ToDictionary(s => s.Stage);
                                    if (set.Count > 0) yield return set;
                                }
    }

    static string Outcome(Func<RootSig.Desc> build, byte[] samplers)
    {
        try
        {
            var d = build();
            try { return d.Key + "|" + Convert.ToHexString(RootSig.Serialize(d, samplers)); }
            catch (RootSig.SerializeException) { return d.Key + "|unserializable"; }
        }
        catch (RootSig.SerializeException) { return "no root signature"; }
    }

    /// <summary>Every rule's root signature for every fixture stage set, mesh tier and MAX_SRVS, serialized: the same bytes as
    /// the code rules'. Also the MAX_SRVS each infers from the fixtures, and REDengine 3's stage-set allow-list.</summary>
    [Fact]
    public void RootSignaturesMatchTheCodeRulesByteForByte()
    {
        var sets = StageSets().ToList();
        var n = 0;
        foreach (var (legacy, id) in Ids)
        {
            var rule = RulesFile.Embedded.Rules.Single(r => r.Id == id);
            var samplers = RootSig.StaticSamplers(rule);
            foreach (var st in sets)
                foreach (var mesh in new[] { false, true })
                    foreach (var maxSrvs in new uint[] { 0, 48, 64, 128 })
                    {
                        var expected = Outcome(() => Legacy.Build(legacy, st, mesh, maxSrvs), samplers);
                        Assert.True(expected == Outcome(() => RootSig.Build(rule, st, mesh, maxSrvs), samplers), $"{id} {string.Join('+', st.Keys)} mesh {mesh} srvs {maxSrvs}");
                        n++;
                    }
            ShaderInfo[] all = [.. VsVariants, .. PsVariants, Gs, Hs, Ds, Ms, As, .. CsVariants];
            for (var i = 0; i < all.Length; i++)
            {
                Assert.Equal(Legacy.MaxSrvsFor(legacy, all.Take(i + 1)), RootSig.MaxSrvsFor(rule, all.Take(i + 1)));
                Assert.Equal(Legacy.MaxSrvsFor(legacy, [all[i]]), RootSig.MaxSrvsFor(rule, [all[i]]));
            }
        }
        Assert.True(n > 50_000, $"{n} root signatures");
        Stage[] stages = [Stage.Vertex, Stage.Pixel, Stage.Geometry, Stage.Hull, Stage.Domain, Stage.Compute, Stage.Mesh, Stage.Amplification];
        for (var m = 0; m < 1 << stages.Length; m++)
        {
            var set = stages.Where((_, i) => (m & 1 << i) != 0).ToList();
            Assert.Equal(Legacy.Red3Validated(set), RootSig.InStageSets(Rules.Red3, set));
        }
    }

    static ShaderInfo Lib(string name, int cb, int srv, params Binding[] b) =>
        new(name, Stage.Library, "lib_6_3", 0, new ResourceCounts(cb, srv, 0, 0), b, [], []);

    /// <summary>The collection rule each engine gets without a recording, for libraries of each binding shape the rules tell
    /// apart: the code's global root signature (bytes), samplers and fields, or the hit group construction it dispatched to.</summary>
    [Fact]
    public void RayTracingRulesMatchTheCodeRules()
    {
        IReadOnlyCollection<ShaderInfo>[] libSets =
        [
            [Lib("chs", 1, 2, new("cbv", 0, 0, 1), new("srv", 0, 0, 2))],
            [Lib("hit", 1, 3, new("srv", 0, 0, 3), new("srv", 4, 0, -1)), Lib("shadow", 0, 0, new Binding("srv", 7, 0, -1))],
            [Lib("rgs", 1, 1, new("cbv", 1, 0, 1), new("uav", 1, 0, 1)), Lib("hit5", 1, 2, new("cbv", 1, 0, 1), new("srv", 2, 0, 1), new("srv", 2, 1, 1), new("cbv", 2, 0, 1))],
            [Lib("rgs", 1, 1, new Binding("cbv", 1, 0, 1)), Lib("heap", 1, 0, new Binding("cbv", 1, 0, 1)) with { Counts = new(1, 0, 0, 0, UeFlags.BindlessResources) }],
        ];
        foreach (var e in Engines())
            foreach (var libs in libSets)
            {
                var legacy = Legacy.Rt(e, libs, out var hitGroups);
                var rule = RulesFile.Embedded.RtFor(e);
                Assert.Equal(hitGroups, rule?.Rt?.Select?.FirstOrDefault(s => s is "red3HitGroups" or "soulsHitGroups"));
                if (hitGroups != null) continue;
                var c = RtCollections.Choose(rule, e, libs, out _);
                Assert.True((legacy == null) == (c == null), $"{e}: {legacy} vs {c}");
                if (legacy is not { } l) continue;
                Assert.Equal(RootSig.Serialize(l.Global, l.Samplers), RootSig.Serialize(c!.Global, c.Samplers));
                Assert.Equal(l.Samplers, c.Samplers);
                Assert.Equal(l.Local == null ? null : RootSig.Serialize(l.Local, []), c.Local == null ? null : RootSig.Serialize(c.Local, []));
                Assert.Equal((l.Flags, l.Depth, l.Payload, l.Attributes), (c.Flags, c.Depth, c.Payload, c.Attributes));
            }
    }

    /// <summary>Elden Ring's material collections ('H' items, one per closest hit and any hit pair of a payload) from a plan
    /// built with the rules: the same items, in order, as the code's dispatch and loop make; a pair that declares a resource
    /// the game's root signatures don't give is left out.</summary>
    [Fact]
    public void EldenRingHitGroupsMatchTheCode()
    {
        static ShaderInfo L(string sha, int payloadSrv, params Binding[] b) => new(sha, Stage.Library, "lib_6_3", 0, new ResourceCounts(1, payloadSrv, 0, 0), b, [], []);
        ShaderInfo[] libs =
        [
            L($"{1:x40}", 4, new("cbv", 0, 0, 1), new("srv", 0, 64, 3)), L($"{2:x40}", 1, new Binding("srv", 0, 67, 4)),
            L($"{3:x40}", 1, new Binding("cbv", 0, 16, 1)), L($"{4:x40}", 1, new("uav", 0, 0, 4), new("sampler", 0, 0, 16)),
            L($"{5:x40}", 1, new Binding("srv", 0, 30, 1)), L($"{6:x40}", 1, new Binding("cbv", 0, 1, 3)),   // t30: neither root signature has it
            L($"{7:x40}", 1, new Binding("srv", 0, 0, 25)),
        ];
        const string plat = "DXIL";
        var index = new ShaderIndex("souls", [plat], libs.ToDictionary(l => l.Sha1),
        [
            new("a", "parts/mat_a" + SCSKiller.Core.FromSoft.SoulsRayTracing.Bundle + "|p4", plat, [libs[0].Sha1, libs[1].Sha1]),
            new("b", "parts/mat_a" + SCSKiller.Core.FromSoft.SoulsRayTracing.Bundle + "|p12", plat, [libs[2].Sha1, libs[3].Sha1]),
            new("c", "parts/mat_b" + SCSKiller.Core.FromSoft.SoulsRayTracing.Bundle + "|p4", plat, [libs[4].Sha1, libs[5].Sha1]),
            new("d", "parts/mat_c" + SCSKiller.Core.FromSoft.SoulsRayTracing.Bundle, plat, [libs[6].Sha1]),
        ]);
        var engine = new EngineInfo("FromSoftware", "DXIL+RTS0", "Elden Ring", "D3D12", false, null);
        Directory.CreateDirectory(_dir);
        var plan = new Planner().Build(Ff7.Game, engine, index, null, Ff7.Nvidia, _dir, null, CancellationToken.None);
        var items = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'H').Select(r => r.Payload).ToList();
        var expected = Legacy.SoulsItems(index, plat);
        Assert.Equal(2, expected.Count);
        Assert.Equal(expected, items);
    }

    // the loader

    static string Body(params object[] rules) => new JsonObject { ["rules"] = new JsonArray([.. rules.Select(r => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(r)))]) }.ToJsonString();

    static object StockRule(string id, string from, string to, uint maxSrvs = 64, string[]? requires = null) => new
    {
        id, requires = requires ?? ["rs.stock.v1"], match = new { family = "Unreal", from, to },
        rootSignature = new { stock = new { stages = new[] { "PS", "VS", "GS", "MS", "AS" }, maxSrvs, maxSamplers = 32, uavStages = new[] { "PS", "CS", "VS" }, tablesWhenUsed = true },
            staticSamplers = new { register = 0, space = 1000 } },
    };

    static object DescRule(string id, uint[][] rows) => new
    {
        id, requires = new[] { "rs.desc.v1" }, match = new { family = "Test" },
        rootSignature = new { byStages = new[] { new { desc = new { flags = 0, rows } } } },
    };

    [Fact]
    public void AnUnknownCapabilityDropsThatRuleAlone()
    {
        var f = RulesFile.TryParse(Body(StockRule("ue-6.1", "6.1", "6.1"), StockRule("ue-6.2", "6.2", "6.2", requires: ["rs.stock.v1", "rs.stock.v9"])))!;
        Assert.Equal(["ue-6.1"], f.Rules.Select(r => r.Id));
        Assert.Equal("ue-6.2: needs rs.stock.v9", Assert.Single(f.Dropped));
        Assert.Contains("needs", Assert.Single(RulesFile.TryParse(Body(new { id = "x", requires = new[] { "override.block.v1" }, match = new { family = "Unreal" }, blockKeys = new[] { "k" } }))!.Dropped));
        Assert.Contains("malformed", Assert.Single(RulesFile.TryParse(Body(new { id = "x", requires = new[] { "rs.stock.v1" }, match = new { family = "Unreal" }, extra = 1 }))!.Dropped));
        Assert.Null(RulesFile.TryParse("""{"rules":[],"other":1}"""));   // the file's own shape is strict
    }

    /// <summary>A collection rule without a state object config needs rt.noconfig.v1, so a build without it drops the rule
    /// instead of reading it as one with a config; its flags must be left unset.</summary>
    [Fact]
    public void ACollectionRuleWithoutAConfigNeedsItsCapability()
    {
        static object Rt(string id, string[] requires, uint flags) => new
        {
            id, requires, match = new { family = "Test" },
            rt = new { note = "n", config = false, flags, depth = 1, global = new { flags = 0, rows = new[] { new uint[] { 2, 0, 0, 1, 8 } } } },
        };
        var f = RulesFile.TryParse(Body(Rt("ok", ["rt.collection.v1", "rt.noconfig.v1"], 0), Rt("no-cap", ["rt.collection.v1"], 0),
            Rt("flags", ["rt.collection.v1", "rt.noconfig.v1"], 4)))!;
        Assert.Equal(["ok"], f.Rules.Select(r => r.Id));
        Assert.Equal(["no-cap: bad rt: config", "flags: bad rt: config"], f.Dropped);
        Assert.Equal(RtCollections.NoConfig, RtCollections.Choose(f.Rules[0], E("Test", "1.0"), [], out _)!.Flags);
    }

    /// <summary>A stock rule denying the mesh stages needs rs.stock.v2, a collection rule with an NVAPI slot rt.nv.v1.</summary>
    [Fact]
    public void DenyMeshAndAnNvapiSlotNeedTheirCapabilities()
    {
        static object Stock(string id, string[] requires) => new
        {
            id, requires, match = new { family = "Test" },
            rootSignature = new { stock = new { stages = new[] { "PS", "VS" }, maxSrvs = 64, maxSamplers = 16, uavStages = new[] { "PS" }, denyMesh = true } },
        };
        static object Rt(string id, string[] requires) => new
        {
            id, requires, match = new { family = "Test" },
            rt = new { note = "n", flags = 4, depth = 1, global = new { flags = 0, rows = new[] { new uint[] { 2, 0, 0, 1, 8 } } }, nv = new { slot = 0, space = 1001 } },
        };
        var f = RulesFile.TryParse(Body(Stock("deny", ["rs.stock.v1", "rs.stock.v2"]), Stock("deny-v1", ["rs.stock.v1"]),
            Rt("nv", ["rt.collection.v1", "rt.nv.v1"]), Rt("nv-v1", ["rt.collection.v1"])))!;
        Assert.Equal(["deny", "nv"], f.Rules.Select(r => r.Id));
        Assert.Equal(["deny-v1: bad stock", "nv-v1: bad rt: nv"], f.Dropped);
        Assert.Equal(new NvSlot(0, 1001), f.Rules[1].Rt!.Nv);
    }

    [Fact]
    public void ARuleWhoseDescriptionDoesntSerializeIsDropped()
    {
        uint[] srv = [0, 0, 0, 8, 0, 0, 5];
        var f = RulesFile.TryParse(Body(DescRule("good", [srv, [2, 0, 0, 0, 8]]), DescRule("overlap", [srv, srv]), DescRule("short", [[2, 0, 0]]),
            DescRule("visibility", [[0, 9, 0, 8, 0, 0, 5]])))!;
        Assert.Equal(["good"], f.Rules.Select(r => r.Id));
        Assert.Contains(f.Dropped, d => d.StartsWith("overlap: a description doesn't serialize"));
        Assert.Contains("short: a description doesn't serialize (a malformed row)", f.Dropped);
        Assert.Contains("visibility: a description doesn't serialize (a malformed row)", f.Dropped);
        // a section without its capability, a shape the app doesn't have, a bad stage name: dropped too
        Assert.Equal(3, RulesFile.TryParse(Body(StockRule("a", "6.1", "6.1", requires: ["rs.desc.v1"]),
            new { id = "b", requires = new[] { "rs.select.v1" }, match = new { family = "Unreal" }, rootSignature = new { select = new[] { "ue6" } } },
            new { id = "c", requires = new[] { "rs.desc.v1", "stagesets.v1" }, match = new { family = "T" }, stageSets = new[] { new[] { "XS" } },
                rootSignature = new { byStages = new[] { new { desc = new { flags = 0, rows = new[] { srv } } } } } }))!.Dropped.Count);
    }

    static object ReRule(string id, string[]? requires = null, uint[]? srvSizes = null, uint[][]? samplers = null) => new
    {
        id, requires = requires ?? ["rs.re.v1", "rs.samplers.v1"], match = new { family = "RE Engine", fork = "Test" },
        rootSignature = new
        {
            re = new { cbvBase = 4, cbvSizes = new uint[] { 3, 10 }, srvSizes = srvSizes ?? [2, 4, 8], uavSizes = new uint[] { 16 }, computeUavs = 64 },
            samplers = samplers ?? [[0, 1, 1, 1, 0, 1, 1, 0, 0, 0x7F7FFFFF, 0, 32, 0]],
        },
    };

    static object ReRuleV2(string id, string[] requires, object re) => new
    {
        id, requires, match = new { family = "RE Engine", fork = "Test" }, rootSignature = new { re },
    };

    /// <summary>A rule using rs.re.v2's parameters must say so, so a build without them drops it instead of building another
    /// root signature.</summary>
    [Fact]
    public void AnReEngineRuleNeedsV2ForVillagesParameters()
    {
        string[] v1 = ["rs.re.v1"], v2 = ["rs.re.v1", "rs.re.v2"];
        object Re(bool denyMesh = true, bool cbvUavAbove = false, uint bindlessSpaces = 0) => new
        {
            cbvBase = 4, cbvSizes = new uint[] { 3, 10 }, srvSizes = new uint[] { 2, 4, 8 }, uavSizes = new uint[] { 2, 4 }, computeUavs = 8, denyMesh, cbvUavAbove, bindlessSpaces,
        };
        var f = RulesFile.TryParse(Body(ReRuleV2("good", v2, Re(false, true, 8)), ReRuleV2("v1-plain", v1, Re()), ReRuleV2("v1-mesh", v1, Re(denyMesh: false)),
            ReRuleV2("v1-above", v1, Re(cbvUavAbove: true)), ReRuleV2("v1-bindless", v1, Re(bindlessSpaces: 1)), ReRuleV2("too-many", v2, Re(bindlessSpaces: 17)),
            ReRuleV2("v3", ["rs.re.v1", "rs.re.v3"], Re())))!;
        Assert.Equal(["good", "v1-plain"], f.Rules.Select(r => r.Id));
        Assert.Equal(["v1-mesh: bad re", "v1-above: bad re", "v1-bindless: bad re", "too-many: bad re", "v3: needs rs.re.v3"], f.Dropped);
    }

    [Fact]
    public void AnReEngineRuleIsCheckedLikeTheOthers()
    {
        var f = RulesFile.TryParse(Body(ReRule("good"), ReRule("no-cap", requires: ["rs.re.v1"]), ReRule("unsorted", srvSizes: [4, 2]),
            ReRule("short-sampler", samplers: [[0, 1, 1]]), ReRule("overlap", samplers: [[0, 1, 1, 1, 0, 1, 1, 0, 0, 0, 0, 0, 0]])))!;
        Assert.Equal(["good"], f.Rules.Select(r => r.Id));
        Assert.Contains("no-cap: bad samplers", f.Dropped);
        Assert.Contains("unsorted: bad re", f.Dropped);
        Assert.Contains("short-sampler: bad samplers", f.Dropped);
        Assert.Contains(f.Dropped, d => d.StartsWith("overlap: a description doesn't serialize"));   // s0 space 0 is also the sampler table's
        Assert.Equal(52, RootSig.StaticSamplers(f.Rules[0]).Length);
    }

    [Fact]
    public void ServerRulesReplaceEmbeddedOnesById()
    {
        var server = RulesFile.TryParse(Body(StockRule("ue-5.5", "5.5", "5.7", maxSrvs: 32), StockRule("ue-6.1", "6.1", "6.1"),
            new { id = "fork", requires = new[] { "rs.select.v1" }, match = new { family = "Unreal", from = "5.5", to = "5.5", fork = "GAME_Fork" },
                rootSignature = new { select = new[] { "ff7" }, staticSamplers = new { register = 0, space = 1000 } } }))!;
        var merged = RulesFile.WithServer(server);
        Assert.Equal(32u, merged.RuleFor(E("Unreal", "5.6"))!.RootSignature!.Stock!.MaxSrvs);
        Assert.Equal("ue-6.1", merged.RuleFor(E("Unreal", "6.1"))!.Id);
        Assert.Equal("fork", merged.RuleFor(E("Unreal", "5.5", "GAME_Fork"))!.Id);   // a rule naming the fork wins
        Assert.Same(RulesFile.Embedded.RuleFor(E("Unreal", "5.4")), merged.RuleFor(E("Unreal", "5.4")));
        Assert.Equal(RulesFile.Embedded.Rules.Count + 2, merged.Rules.Count);
        Assert.Null(RulesFile.Embedded.RuleFor(E("Unreal", "6.1")));
    }

    static readonly (string Seed, string Public) A = FeedTrust.NewKey(), Other = FeedTrust.NewKey();
    static readonly DateTimeOffset T = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyASignedFileWithAPinnedKeyIsUsed()
    {
        Directory.CreateDirectory(_dir);
        var store = new AppStore(_dir);
        var trust = new ContentTrust(store, new Dictionary<string, string> { ["rules-a"] = A.Public });
        var file = Path.Combine(_dir, "plan-rules.json");
        var body = Body(StockRule("ue-6.1", "6.1", "6.1"));
        string Sign(string seed) => ContentTrust.Sign(Convert.FromBase64String(seed), "rules-a", RulesFile.Name, body, T);
        foreach (var refused in new[] { body, Sign(Other.Seed), ContentTrust.Sign(Convert.FromBase64String(A.Seed), "rules-a", "known-stutter", body, T) })
        {
            File.WriteAllText(file, refused);
            Assert.Null(RulesFile.Cached(file, trust, []));   // the caller keeps the embedded rules
        }
        File.WriteAllText(file, Sign(A.Seed));
        Assert.Null(RulesFile.Cached(file, new ContentTrust(store, ContentTrust.RulesKeys), []));   // nothing pinned yet: no server rules
        Assert.Equal(["ue-6.1"], RulesFile.Cached(file, trust, [])!.Rules.Select(r => r.Id));
    }

    /// <summary>The edge's rules channels: stable is public, alpha and beta answer 401 without the access token. A path missing
    /// from <paramref name="files"/> is 404; one mapped to null is 403.</summary>
    sealed class Edge(Dictionary<string, string?> files) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath.TrimStart('/');
            lock (Asked) Asked.Add(path + (r.Headers.Authorization is { } a ? " " + a.Parameter : ""));
            files.TryGetValue(path, out var body);
            var status = path.StartsWith("v1/updates/") && r.Headers.Authorization?.Parameter != "tok" ? System.Net.HttpStatusCode.Unauthorized
                : !files.ContainsKey(path) ? System.Net.HttpStatusCode.NotFound
                : body == null ? System.Net.HttpStatusCode.Forbidden : System.Net.HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(status == System.Net.HttpStatusCode.OK ? body! : "{}") });
        }
    }

    const string StablePath = "v1/content/plan-rules.json", BetaPath = "v1/updates/beta/plan-rules.json", AlphaPath = "v1/updates/alpha/plan-rules.json";

    /// <summary>A rules version named by its one rule's id.</summary>
    static string Version(string id, DateTimeOffset at, string? seed = null) =>
        ContentTrust.Sign(Convert.FromBase64String(seed ?? A.Seed), "rules-a", RulesFile.Name, Body(StockRule(id, "5.8", "5.8")), at);

    async Task<(string? Id, Edge Edge)> Fetch(ContentTrust trust, Dictionary<string, string?> files, string? token, params string[] ent)
    {
        var edge = new Edge(files);
        var rules = await RulesFile.FetchAsync(Path.Combine(_dir, "plan-rules.json"), trust, new RouteFailover(edge, [new("https://api.scskiller.com/")]), token, RulesFile.Channels(ent));
        return (rules?.Rules.Single().Id, edge);
    }

    ContentTrust ChannelTrust()
    {
        Directory.CreateDirectory(_dir);
        return new(new AppStore(_dir), new Dictionary<string, string> { ["rules-a"] = A.Public });
    }

    [Fact]
    public async Task TheEntitlementSaysWhichChannelsAreRead()
    {
        Assert.Empty(RulesFile.Channels(null));
        Assert.Empty(RulesFile.Channels(["db", "prio"]));
        Assert.Equal(["beta"], RulesFile.Channels(["db", "beta"]));
        Assert.Equal(["alpha", "beta"], RulesFile.Channels(["alpha"]));
        Assert.Equal(["alpha", "beta"], RulesFile.Channels(["db", "internal"]));

        var (_, edge) = await Fetch(ChannelTrust(), [], "tok", "beta");
        Assert.Equal([StablePath, BetaPath + " tok"], edge.Asked.Order());
        (_, edge) = await Fetch(ChannelTrust(), [], null, "alpha");   // signed out: stable only, no token sent
        Assert.Equal([StablePath], edge.Asked);
    }

    [Fact]
    public async Task TheNewestValidVersionAmongTheChannelsWins()
    {
        Dictionary<string, string?> files = new() { [StablePath] = Version("stable", T), [BetaPath] = Version("beta", T.AddHours(1)), [AlphaPath] = Version("alpha", T.AddHours(2)) };
        Assert.Equal("beta", (await Fetch(ChannelTrust(), files, "tok", "beta")).Id);
        Assert.Equal("alpha", (await Fetch(ChannelTrust(), files, "tok", "alpha")).Id);
        Assert.Equal(files[AlphaPath], File.ReadAllText(Path.Combine(_dir, "plan-rules@alpha.json")));   // each channel's copy kept beside stable's
        Assert.Equal(files[StablePath], File.ReadAllText(Path.Combine(_dir, "plan-rules.json")));

        files[StablePath] = Version("fix", T.AddHours(3));   // an urgent fix straight to stable reaches every channel
        Assert.Equal("fix", (await Fetch(ChannelTrust(), files, "tok", "alpha")).Id);
    }

    [Fact]
    public async Task ABadNewestVersionOrAChannelThatRefusesFallsBack()
    {
        Dictionary<string, string?> files = new() { [StablePath] = Version("stable", T), [BetaPath] = Version("beta", T.AddHours(1)), [AlphaPath] = Version("alpha", T.AddHours(2), Other.Seed) };
        Assert.Equal("beta", (await Fetch(ChannelTrust(), files, "tok", "alpha")).Id);   // the newest's signature fails

        Directory.Delete(_dir, true);
        files[AlphaPath] = null;   // 403
        files.Remove(BetaPath);    // 404
        Assert.Equal("stable", (await Fetch(ChannelTrust(), files, "tok", "alpha")).Id);
        Directory.Delete(_dir, true);
        Assert.Equal("stable", (await Fetch(ChannelTrust(), files, "expired", "alpha")).Id);   // 401
    }

    [Fact]
    public async Task LosingTheFlagGoesBackToStablesRules()
    {
        var trust = ChannelTrust();
        var file = Path.Combine(_dir, "plan-rules.json");
        Dictionary<string, string?> files = new() { [StablePath] = Version("stable", T), [AlphaPath] = Version("alpha", T.AddHours(2)) };
        Assert.Equal("alpha", (await Fetch(trust, files, "tok", "alpha")).Id);
        Assert.Equal("alpha", RulesFile.Cached(file, trust, null)!.Rules.Single().Id);   // a start before the account answers keeps what was read

        Assert.Equal("stable", (await Fetch(trust, files, "tok", "db")).Id);   // stable's older copy: its own channel never saw a newer one
        Assert.False(File.Exists(Path.Combine(_dir, "plan-rules@alpha.json")));
        Assert.Equal("stable", RulesFile.Cached(file, trust, null)!.Rules.Single().Id);   // and the next start
        Assert.Equal("stable", RulesFile.Cached(file, trust, [])!.Rules.Single().Id);   // signed out, no fetch: the kept copies alone

        Assert.Equal("alpha", (await Fetch(trust, files, "tok", "alpha")).Id);   // the flag back: the same alpha copy again
        File.Delete(file);
        Assert.Null(RulesFile.Cached(file, trust, []));   // signed out with no stable copy: the embedded rules
    }

    /// <summary>The hash of the rules matching a game changes only for the games a changed rule matches; a copy that differs
    /// only in layout changes none.</summary>
    [Fact]
    public void TheRulesHashChangesOnlyForGamesWhoseRulesChanged()
    {
        EngineInfo[] games = [E("Unreal", "5.6"), E("Unreal", "5.4"), E("Unreal", "4.26"), E("Unreal", "4.26", "GAME_FinalFantasy7Rebirth"), E("REDengine 3", "DX12"),
            E("FromSoftware", "DXIL+RTS0"), E("Unity", "6000"), E("Unreal", "4.26", "GAME_WutheringWaves")];
        var embedded = RulesFile.Embedded;
        var reformatted = RulesFile.WithServer(RulesFile.TryParse(JsonNode.Parse(ContentFile.Embedded("SCSKiller.Core.Planning.plan-rules.json"))!.ToJsonString())!);
        Assert.All(games, g => Assert.Equal(embedded.Hash(g), reformatted.Hash(g)));
        var server = RulesFile.WithServer(RulesFile.TryParse(Body(StockRule("ue-5.5", "5.5", "5.7", maxSrvs: 32)))!);
        Assert.NotEqual(embedded.Hash(games[0]), server.Hash(games[0]));
        Assert.All(games.Skip(1), g => Assert.Equal(embedded.Hash(g), server.Hash(g)));
        Assert.Equal(embedded.Hash(games[6]), embedded.Hash(E("Carved", "x")));   // no rule matches either
        // rs.desc.v2's fields, unset, aren't serialized: the rules without them hash as they did before
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        Assert.All(embedded.Rules.Where(r => !r.Requires.Contains("rs.desc.v2")), r => Assert.DoesNotMatch("\"(games|bindsNothing|dxbc)\"", System.Text.Json.JsonSerializer.Serialize(r, web)));
        Assert.All(embedded.Rules.Where(r => !r.Requires.Contains("rs.stock.v2") && !r.Requires.Contains("rt.nv.v1")), r => Assert.DoesNotMatch("\"(denyMesh|nv)\"", System.Text.Json.JsonSerializer.Serialize(new object?[] { r.RootSignature?.Stock, r.Rt }, web)));   // RE Engine's own denyMesh is rs.re.v2's
    }
}

/// <summary>The planner's rules as code had them before plan-rules.json (main at UE 5.8's rule, Planner.Version 28), verbatim
/// but for doc comments, enum and all, and release/1.2's UE 4.25 collection rule: what the embedded rules must reproduce.</summary>
static class Legacy
{
    public enum Rule
    {
        Ff7,    // FF7 Rebirth's fork (recorded): see BuildUe
        Ue420,  // 4.20: MAX_SRVS 32, no static samplers; the input-layout flag only with a non-empty vertex declaration
        Ue421,  // 4.21: MAX_SRVS 48, otherwise 4.20's
        Ue422,  // 4.22-4.24: MAX_SRVS 48, no static samplers
        Ue425,  // 4.25: MAX_SRVS 64, static samplers s1000-s1005 in space 0
        Ue426,  // 4.26-4.27: static samplers s0-s5 in space 1000
        Ue50,   // 5.0: no hull/domain, mesh/amplification stages, AGS/diagnostic root UAVs, bindless heap flags
        Ue51,   // 5.1-5.3: a stage's table only when it uses that resource type
        Ue54,   // 5.4: MAX_SAMPLERS 32, root constants
        Ue55,   // 5.5-5.7: vertex shaders get UAVs
        Ue58,   // 5.8+: mesh/amplification shaders get UAVs; NVIDIA shader extensions get a u0 space 1001 table
        Red3,   // REDengine 3 (The Witcher 3, DX12): three fixed root signatures, see BuildRed3
        Northlight, // Northlight (Control, DX12): one graphics and one compute root signature, see BuildNorthlight
        Dagor,  // Dagor Engine (DX12): built from each shader's header, constant buffers as root CBVs, see DagorRootSig
        DagorCbvRanges,   // the same with constant buffers in descriptor tables (War Thunder)
    }

    public static Rule? RuleFor(EngineInfo e)
    {
        if (e.Family == "REDengine 3") return Rule.Red3;
        if (e.Family == SCSKiller.Core.Northlight.NorthlightReader.Family) return Rule.Northlight;
        if (e.Family == SCSKiller.Core.Dagor.DagorReader.Family) return e.Fork == SCSKiller.Core.Dagor.DagorReader.CbvRangesFork ? Rule.DagorCbvRanges : Rule.Dagor;
        if (e.Family != "Unreal" || !System.Version.TryParse(e.Version, out var v)) return null;
        if (e.Fork == "GAME_FinalFantasy7Rebirth" && e.Version == "4.26") return Rule.Ff7;
        return (v.Major, v.Minor) switch
        {
            (4, 20) => Rule.Ue420,
            (4, 21) => Rule.Ue421,
            (4, >= 22 and <= 24) => Rule.Ue422,
            (4, 25) => Rule.Ue425,
            (4, 26 or 27) => Rule.Ue426,
            (5, 0) => Rule.Ue50,
            (5, <= 3) => Rule.Ue51,
            (5, 4) => Rule.Ue54,
            (5, <= 7) => Rule.Ue55,
            (5, _) or (6, 0) => Rule.Ue58, // 6.0: Fortnite's internal line, which its containers tell as 5.8
            _ => null,
        };
    }

    
    public static readonly byte[] Ue426Samplers = UeSamplers(0, 1000);

    public static byte[] StaticSamplers(Rule r) => r switch { Rule.Ue420 or Rule.Ue421 or Rule.Ue422 or Rule.Red3 or Rule.Northlight or Rule.Dagor or Rule.DagorCbvRanges => [], Rule.Ue425 => Ue425Samplers, _ => RootSig.Ue426Samplers };

    static readonly byte[] Ue425Samplers = UeSamplers(1000, 0);

    static byte[] UeSamplers(uint reg, uint space)
    {
        var s = new List<uint>();
        foreach (var filter in new uint[] { 0, 0x14, 0x15 }) // point, bilinear, trilinear
            foreach (var addr in new uint[] { 1, 3 }) // wrap, clamp
                s.AddRange([filter, addr, addr, addr, 0, 1, 1, 0, 0, BitConverter.SingleToUInt32Bits(float.MaxValue), reg++, space, 0]);
        return MemoryMarshal.AsBytes(s.ToArray().AsSpan()).ToArray();
    }

    const uint Unbounded = uint.MaxValue;
    static uint Vis(Stage s) => s switch { Stage.Vertex => 1, Stage.Hull => 2, Stage.Domain => 3, Stage.Geometry => 4, Stage.Pixel => 5, Stage.Amplification => 6, Stage.Mesh => 7, _ => 0 };
    static uint DenyBit(Stage s) => s switch { Stage.Vertex => 0x2, Stage.Hull => 0x4, Stage.Domain => 0x8, Stage.Geometry => 0x10, Stage.Pixel => 0x20, Stage.Amplification => 0x100, _ => 0x200 };

    
    public static RootSig.Desc Build(Rule r, IReadOnlyDictionary<Stage, ShaderInfo> stages, bool meshTier, uint maxSrvs = 0) =>
        r switch { Rule.Ff7 => RootSig.BuildUe(stages), Rule.Red3 => BuildRed3(stages), Rule.Northlight => BuildNorthlight(stages), Rule.Dagor => SCSKiller.Core.Dagor.DagorRootSig.Build(stages), Rule.DagorCbvRanges => SCSKiller.Core.Dagor.DagorRootSig.Build(stages, cbvRanges: true), _ => BuildStock(r, stages, meshTier, maxSrvs) };

    public static bool Red3Validated(IEnumerable<Stage> stages) => Red3Sets.Contains(Mask(stages));

    static int Mask(IEnumerable<Stage> stages) => stages.Aggregate(0, (m, s) => m | 1 << (int)s);

    static readonly HashSet<int> Red3Sets = new[]
    {
        new[] { Stage.Vertex }, [Stage.Vertex, Stage.Pixel], [Stage.Vertex, Stage.Hull, Stage.Domain], [Stage.Vertex, Stage.Hull, Stage.Domain, Stage.Pixel],
        [Stage.Vertex, Stage.Geometry, Stage.Pixel], [Stage.Vertex, Stage.Geometry, Stage.Hull, Stage.Domain], [Stage.Compute],
    }.Select(Mask).ToHashSet();

    static RootSig.Desc BuildRed3(IReadOnlyDictionary<Stage, ShaderInfo> stages)
    {
        if (!Red3Validated(stages.Keys)) throw new RootSig.SerializeException($"no REDengine 3 root signature confirmed for {string.Join('+', stages.Keys)}");
        if (stages.ContainsKey(Stage.Compute))
            return new(0, [[2, 0, 0, 0, 2], [0, 0, 2, 15, 1, 0, 1], [0, 0, 0, 8, 0, 0, 1], [0, 0, 0, 11, 8, 0, 1], [0, 0, 0, 13, 19, 0, 1],
                [0, 0, 0, 31, 32, 0, 1], [0, 0, 1, 16, 0, 0, 1], [0, 0, 3, 8, 0, 0, 0], [0, 0, 3, 8, 8, 0, 0]]);
        var rows = new List<uint[]>();
        foreach (var vis in new uint[] { 5, 1 }) for (var b = 0u; b < 5; b++) rows.Add([2, vis, b, 0, 2]);
        rows.AddRange([[0, 5, 2, 11, 5, 0, 1], [0, 5, 0, 8, 0, 0, 1], [0, 5, 0, 11, 8, 0, 1], [0, 5, 0, 13, 19, 0, 1], [0, 5, 0, 31, 32, 0, 1],
            [0, 5, 3, 8, 0, 0, 0], [0, 5, 3, 8, 8, 0, 0], [0, 1, 2, 11, 5, 0, 1], [0, 1, 0, 8, 0, 0, 1], [0, 1, 0, 56, 8, 0, 1],
            [0, 1, 3, 8, 0, 0, 0], [0, 1, 3, 8, 8, 0, 0], [0, 0, 1, 4, 0, 0, 1]]);
        if (!stages.Keys.Any(s => s is Stage.Geometry or Stage.Hull or Stage.Domain)) return new(0x1D, rows); // input layout; HS, DS, GS denied
        foreach (var vis in new[] { Vis(Stage.Geometry), Vis(Stage.Hull), Vis(Stage.Domain) })
            rows.AddRange([[0, vis, 2, 32, 0, 0, 1], [0, vis, 0, 64, 0, 0, 1], [0, vis, 3, 8, 0, 0, 0], [0, vis, 3, 8, 8, 0, 0]]);
        return new(0x41, rows); // input layout, stream output
    }

    static RootSig.Desc BuildNorthlight(IReadOnlyDictionary<Stage, ShaderInfo> stages)
    {
        if (stages.ContainsKey(Stage.Compute)) return NorthlightCompute;
        if (stages.ContainsKey(Stage.Geometry) || stages.Keys.Any(s => s is Stage.Mesh or Stage.Amplification))
            throw new RootSig.SerializeException($"Northlight has no root signature for {string.Join('+', stages.Keys)}");
        static uint[] Table(uint vis, uint cbvBase) => [0, vis, 2, 5, cbvBase, 0, 0, 0, 36, 0, 0, 0, 1, 8, 0, 0, 0];
        uint[] vps = [Vis(Stage.Vertex), Vis(Stage.Pixel)], hds = [Vis(Stage.Hull), Vis(Stage.Domain)];
        return new(0x11, [
            .. vps.Select(v => Table(v, 4)), .. hds.Select(v => Table(v, 0)),
            .. vps.Concat(hds).Select(v => new uint[] { 0, v, 3, 4, 0, 0, 0 }),
            .. vps.SelectMany(v => Enumerable.Range(0, 4).Select(b => new uint[] { 2, v, (uint)b, 0, 0 })),
            [0, 0, 3, 64, 0, 1, 0], [0, Vis(Stage.Pixel), 0, 244000, 0, 1, 0]], AppendRanges: true, Version10: true);
    }

    public static readonly RootSig.Desc NorthlightCompute = new(0, [
        [0, 0, 2, 5, 4, 0, 0, 0, 36, 0, 0, 0, 1, 8, 0, 0, 0], [0, 0, 3, 4, 0, 0, 0],
        .. Enumerable.Range(0, 4).Select(b => new uint[] { 2, 0, (uint)b, 0, 0 }),
        [0, 0, 3, 64, 0, 1, 0], [0, 0, 0, 244000, 0, 1, 0]], AppendRanges: true, Version10: true);

    static readonly Stage[] Ue4Stages = [Stage.Pixel, Stage.Vertex, Stage.Geometry, Stage.Hull, Stage.Domain];
    static readonly Stage[] Ue5Stages = [Stage.Pixel, Stage.Vertex, Stage.Geometry, Stage.Mesh, Stage.Amplification];

    static RootSig.Desc BuildStock(Rule r, IReadOnlyDictionary<Stage, ShaderInfo> stages, bool meshTier, uint maxSrvs)
    {
        var all = r >= Rule.Ue50 ? Ue5Stages : Ue4Stages;
        var order = stages.ContainsKey(Stage.Compute) ? [Stage.Compute] : all.Where(stages.ContainsKey).ToArray();
        var q = order.ToDictionary(s => s, s => Quantize(r, s, stages[s].Counts, maxSrvs));
        var rows = new List<uint[]>();
        foreach (var s in order)
        {
            var (srv, sampler, uav, _) = q[s];
            if (srv > 0) rows.Add([0, Vis(s), 0, srv, 0, 0, 5]);           // DESCRIPTORS_VOLATILE | DATA_STATIC_WHILE_SET_AT_EXECUTE
            if (r < Rule.Ue50) rows.AddRange(SpaceBindlessTables(s, stages[s]));
            if (sampler > 0) rows.Add([0, Vis(s), 3, sampler, 0, 0, 1]);   // DESCRIPTORS_VOLATILE
            if (uav > 0) rows.Add([0, Vis(s), 1, uav, 0, 0, 3]);           // DESCRIPTORS_VOLATILE | DATA_VOLATILE
            if (r < Rule.Ue50) rows.AddRange(BindlessTables(s, stages[s]));
        }
        foreach (var s in order)
            for (var reg = 0u; reg < q[s].Cb; reg++) rows.Add([2, Vis(s), reg, 0, 8]); // DATA_STATIC
        var used = stages.Values.Aggregate(0, (f, i) => f | i.Counts.Flags);
        // ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT: from 4.22 whenever a vertex declaration is bound (UE always binds one); up to 4.21
        // only when it has elements (QuantizeBoundShaderState: InputLayout.NumElements > 0), taken as: the VS reads vertex input
        // (a VS without vertex inputs is drawn with UE's empty declaration; an unverified guess)
        var flags = !stages.TryGetValue(Stage.Vertex, out var vs) ? 0u
            : r is Rule.Ue420 or Rule.Ue421 && !vs.Inputs.Any(i => i.SysValue == 0) ? 0u : 1u;
        if (r >= Rule.Ue50)
        {
            if ((used & UeFlags.AmdIntrinsics) != 0) rows.Add([4, 0, 0, 0x7FFF0ADE, 2]);            // u0, AGS_DX12_SHADER_INSTRINSICS_SPACE_ID
            if (r >= Rule.Ue54 && (used & UeFlags.RootConstants) != 0) rows.Add([1, 0, 0, 3, 4]); // b0 space 3 (UE_HLSL_SPACE_SHADER_ROOT_CONSTANTS), 4 values
            if (r >= Rule.Ue58 && (used & UeFlags.NvIntrinsics) != 0) rows.Add([0, 0, 1, 1, 0, 1001, 0, 0]);  // u0 space 1001 (UE_HLSL_SPACE_NV_SHADER_EXTN), offset 0
            if ((used & UeFlags.DiagnosticBuffer) != 0) rows.Add([4, 0, 0, 999, 2]);              // u0 space 999 (UE_HLSL_SPACE_DIAGNOSTIC)
            if ((used & UeFlags.BindlessResources) != 0) flags |= 0x400;                          // CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED
            if ((used & UeFlags.BindlessSamplers) != 0) flags |= 0x800;                           // SAMPLER_HEAP_DIRECTLY_INDEXED
        }
        foreach (var s in all)
            if ((!q.TryGetValue(s, out var c) || c == default) && (meshTier || s is not (Stage.Mesh or Stage.Amplification))) flags |= DenyBit(s);
        return new RootSig.Desc(flags, rows);
    }

    static IEnumerable<uint[]> BindlessTables(Stage s, ShaderInfo sh) =>
        sh.Bindings.Where(b => b.Class == "srv" && b.Count == -1 && b.Space >= 10).Select(b => (uint)b.Space / 10).Distinct().Order()
            .Select(g => (uint[])[0, Vis(s), .. Enumerable.Range(0, 5).SelectMany(k => new[] { 0u, Unbounded, 0u, 10 * g + (uint)k, 5u })]);

    static IEnumerable<uint[]> SpaceBindlessTables(Stage s, ShaderInfo sh)
    {
        var top = sh.Bindings.Where(IsSpaceBindless).Select(b => b.Space).DefaultIfEmpty(3).Max();
        for (var k = 4u; k <= top; k++) yield return [0, Vis(s), 0, Unbounded, 0, k, 5];
    }

    static bool IsSpaceBindless(Binding b) => b is { Class: "srv", Count: -1, Space: >= 4 and < 10 };

    static uint MaxSrvs(Rule r) => r switch { Rule.Ue420 => 32u, Rule.Ue421 or Rule.Ue422 => 48u, _ => 64u };

    public static uint MaxSrvsFor(Rule r, IEnumerable<ShaderInfo> shaders)
    {
        if (r == Rule.Ue426 && shaders.Any(s => s.Bindings.Any(IsSpaceBindless))) return 128;
        if (r == Rule.Ff7 || r > Rule.Ue422) return 0;
        var need = shaders.Select(s => s.Counts.Srv).DefaultIfEmpty(0).Max();
        return need <= MaxSrvs(r) ? 0 : need <= 48 ? 48u : 64u;
    }

    static (uint Srv, uint Sampler, uint Uav, uint Cb) Quantize(Rule r, Stage s, ResourceCounts c, uint maxSrvs)
    {
        var uavs = s is Stage.Pixel or Stage.Compute || (s == Stage.Vertex && r >= Rule.Ue55) || (s is Stage.Mesh or Stage.Amplification && r >= Rule.Ue58);
        uint srv = maxSrvs > 0 ? maxSrvs : MaxSrvs(r), sampler = r >= Rule.Ue54 ? 32u : 16, cb = (uint)Math.Min(c.Cb, 16);
        return r <= Rule.Ue50
            ? (srv, sampler, uavs ? 16u : 0, cb)
            : (c.Srv > 0 ? srv : 0, c.Sampler > 0 ? sampler : 0, c.Uav > 0 && uavs ? 16u : 0, cb);
    }

    // RtCollections' fixed descriptions and PlanBuilder.RtPlan's choice without a recording

    static readonly RootSig.Desc NorthlightLocal = new(0x80, [[3, 0, 0, 3, 0], [3, 0, 1, 3, 0], [1, 0, 0, 3, 10]]);

    static readonly RootSig.Desc Ue426Global = new(0, [
        [0, 0, 1, 1, 0, 404, 3],
        [0, 0, 0, 64, 0, 1, 5], [0, 0, 3, 16, 0, 1, 1], [0, 0, 1, 16, 0, 1, 3],
        .. Enumerable.Range(0, 16).Select(b => new uint[] { 2, 0, (uint)b, 1, 8 })]);

    static readonly RootSig.Desc Ue51Global = new(0, [
        [0, 0, 0, 64, 0, 1, 5], [0, 0, 3, 16, 0, 1, 1], [0, 0, 1, 16, 0, 1, 3],
        .. Enumerable.Range(0, 16).Select(b => new uint[] { 2, 0, (uint)b, 1, 8 }),
        [4, 0, 0, 999, 2]]);

    static readonly RootSig.Desc Ue54Global = new(0, [
        [0, 0, 0, 64, 0, 1, 5], [0, 0, 3, 32, 0, 1, 1], [0, 0, 1, 16, 0, 1, 3],
        .. Enumerable.Range(0, 16).Select(b => new uint[] { 2, 0, (uint)b, 1, 8 }),
        [4, 0, 0, 999, 2]]);

    static RootSig.Desc GlobalFor(IEnumerable<ShaderInfo> libs)
    {
        var top = libs.SelectMany(l => l.Bindings).Where(IsSpaceBindless).Select(b => (uint)b.Space).DefaultIfEmpty(0u).Max();
        if (top == 0) return Ue426Global;
        return new(0, [
            [0, 0, 0, 128, 0, 1, 5],
            .. Enumerable.Range(5, Math.Max(0, (int)top - 4)).Select(k => new uint[] { 0, 0, 0, uint.MaxValue, 0, (uint)k, 5 }),
            [0, 0, 3, 16, 0, 1, 1], [0, 0, 1, 16, 0, 1, 3],
            .. Enumerable.Range(0, 16).Select(b => new uint[] { 2, 0, (uint)b, 1, 8 }),
            [0, 0, 1, 1, 0, 1001, 0, 0]]);
    }

    public sealed record RtChoice(RootSig.Desc Global, byte[] Samplers, RootSig.Desc? Local, uint Flags, uint Depth, uint Payload, uint Attributes);

    /// <param name="hitGroups">the material hit group construction RtPlan dispatched to instead</param>
    public static RtChoice? Rt(EngineInfo engine, IReadOnlyCollection<ShaderInfo> libs, out string? hitGroups)
    {
        hitGroups = RuleFor(engine) == Rule.Red3 ? "red3HitGroups" : engine.Family == "FromSoftware" ? "soulsHitGroups" : null;
        if (hitGroups != null) return null;
        if (engine.Family == "Unreal" && engine.Version == "4.25")
            return new(new(0, [.. Ue426Global.Rows.Skip(1)]), Ue425Samplers, null, RtCollections.NoConfig, 1, 64, 8);
        if (engine.Family == "Unreal" && engine.Version is "4.26" or "4.27")
        {
            var desc = GlobalFor(libs);
            var fork = desc != Ue426Global;
            return new(desc, RootSig.Ue426Samplers, null, 4, 1, fork ? 0u : 32, 8);
        }
        if (engine.Family == "Unreal" && engine.Version is "5.0" or "5.1" or "5.2" or "5.3" or "5.4")
            return RtCollections.Ue5ShapeMismatch(libs) != null ? null
                : new(engine.Version == "5.4" ? Ue54Global : Ue51Global, RootSig.Ue426Samplers, null, 4, 1, 0, 8);
        if (RuleFor(engine) == Rule.Northlight) return new(NorthlightCompute, [], NorthlightLocal, 0, 1, 0, 0);
        return null;
    }

    /// <summary>PlanBuilder.SoulsHitGroups' items without a recording.</summary>
    public static List<byte[]> SoulsItems(ShaderIndex index, string plat)
    {
        var bc = index.Shaders;
        var (global, local) = (SCSKiller.Core.FromSoft.SoulsRayTracing.Global, SCSKiller.Core.FromSoft.SoulsRayTracing.Local);
        var ranges = new RootSig.Ranges(0, [.. RootSig.Parse(global.Blob).Slots, .. RootSig.Parse(local.Blob).Slots]);
        var items = new List<byte[]>();
        foreach (var m in index.Maps.Where(m => m.Platform == plat && SCSKiller.Core.FromSoft.SoulsRayTracing.IsPair(m) && m.Shaders.All(h => bc.TryGetValue(h, out var s) && s.Stage == Stage.Library)))
        {
            if (m.Shaders.Select(h => RootSig.Uncovered(ranges, Stage.Library, bc[h])).FirstOrDefault(w => w != null) is not null) continue;
            items.Add(SCSKiller.Core.RedEngine.RedRayTracing.Item(m.Shaders[0], m.Shaders[1], local.Hash, SCSKiller.Core.FromSoft.SoulsRayTracing.Shape, null));
        }
        return items;
    }
}
