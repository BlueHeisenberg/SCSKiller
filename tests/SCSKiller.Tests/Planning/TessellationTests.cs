using SCSKiller.Core;
using SCSKiller.Core.Planning;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>Tessellation stage sets: VS -> HS -> DS (-> GS) (-> PS) inside one shader map, linked by the control
/// points' signatures, drawn as patch lists, root signatures giving the HS/DS their tables (the pre-emit guard passes); D3D11
/// games get HS+DS pairs ('2' items).</summary>
public class TessellationTests
{
    static readonly EngineInfo Ue427 = new("Unreal", "4.27", null, "D3D12", false, null);
    static readonly SigElement Pos = In("SV_POSITION", 0, 0, 0xF, 3, 1);

    // UE's flat tessellation: the VS writes control points (no SV_Position), the HS adds per-point data, the DS rasterizes
    static readonly SigElement[] Cp = [In("TEXCOORD", 0, 0), In("VS_To_DS_Position", 0, 1)];
    static readonly SigElement[] CpOut = [.. Cp, In("Flat_DisplacementScales", 0, 2, 7)];
    static readonly ShaderInfo Vs = Shader("t-vs", Stage.Vertex, [In("POSITION", 0, 0, 7)], Cp) with { Counts = new(1, 0, 0, 0), Bindings = [new("cbv", 0, 0, 1)] };
    static readonly ShaderInfo Hs = Shader("t-hs", Stage.Hull, Cp, CpOut) with { ShaderModel = "hs_5_0", Counts = new(1, 1, 0, 0), Bindings = [new("cbv", 0, 0, 1), new("srv", 0, 0, 1)] };
    static readonly ShaderInfo Ds = Shader("t-ds", Stage.Domain, CpOut, [Pos, In("TEXCOORD", 0, 1)]) with { ShaderModel = "ds_5_0", Counts = new(2, 1, 0, 1), Bindings = [new("cbv", 0, 1, 1), new("srv", 0, 3, 1), new("sampler", 0, 0, 1)] };
    static readonly ShaderInfo Ps = Shader("t-ps", Stage.Pixel, [Pos, In("TEXCOORD", 0, 1)], [Target(0)]);
    // a DS for one-pass cube shadows: no SV_Position, a GS taking its triangles writes it
    static readonly ShaderInfo DsGs = Shader("t-dsgs", Stage.Domain, CpOut, [In("TEXCOORD", 6, 0)]) with { ShaderModel = "ds_5_0" };
    static readonly ShaderInfo Gs = Shader("t-gs", Stage.Geometry, [In("TEXCOORD", 6, 0)], [Pos, In("TEXCOORD", 0, 1)], gsInput: 3) with { ShaderModel = "gs_5_0" };
    // a DS reading the HS's per-point data from other registers: doesn't link
    static readonly ShaderInfo DsOther = Shader("t-dsx", Stage.Domain, [.. Cp, In("Flat_DisplacementScales", 0, 3, 7)], [Pos, In("TEXCOORD", 0, 1)]) with { ShaderModel = "ds_5_0" };
    // the same HS signature in another map: pairs only inside its own map (which has no DS)
    static readonly ShaderInfo HsElsewhere = Hs with { Sha1 = Hash("t-hs2") };

    static ShaderIndex Index(string platform = "PCD3D_SM5", params ShaderInfo[] extra)
    {
        ShaderInfo[] a = [Vs, Hs, Ds, Ps, DsGs, Gs, DsOther, .. extra];
        return new("synthetic", [platform], a.Append(HsElsewhere).ToDictionary(s => s.Sha1),
            [new ShaderMap("m", "Game", platform, a.Select(s => s.Sha1).ToList()), new ShaderMap("m2", "Game", platform, [Vs.Sha1, HsElsewhere.Sha1, Ps.Sha1])]);
    }

    static string Set(params ShaderInfo[] s) => string.Join('+', s.OrderBy(x => x.Stage).Select(x => x.Sha1[..6]));
    static string Set(PsoDb.Pso p) => string.Join('+', p.Stages.OrderBy(x => x.Key).Select(x => x.Value[..6]));

    [Fact]
    public void TessellationChainsLinkInsideTheirMap()
    {
        var dir = Ff7.TempDir("tess-chains");
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true }, Ff7.Amd })
        {
            var plan = new Planner().Build(Ff7.Game, Ue427, Index(), null, caps, Path.Combine(dir, caps.Profile + caps.PerStageCache), null, CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            var templates = body.Where(r => r.Tag == 'S').ToDictionary(r => r.Key, PsoDb.Parse);
            var psos = templates.Values.Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload))
                .Select(i => templates[i.Template] with { Rs = i.Rs, Stages = i.Stages })).ToList(); // an item: its template with the shaders swapped
            var tess = psos.Where(p => p.Stages.ContainsKey((int)Stage.Hull)).ToList();
            // alone (depth pass) and with the PS, and through the GS (alone and with the PS); never DsOther, never the other map's HS
            string[] want = [Set(Vs, Hs, Ds), Set(Vs, Hs, Ds, Ps), Set(Vs, Hs, DsGs, Gs), Set(Vs, Hs, DsGs, Gs, Ps)];
            Assert.Equal(want.Order(), tess.Select(Set).Order());
            Assert.All(tess, p => Assert.Equal(4u, p.Topology)); // D3D12_PRIMITIVE_TOPOLOGY_TYPE_PATCH
            Assert.DoesNotContain(psos, p => p.Stages.ContainsValue(DsOther.Sha1) || p.Stages.ContainsValue(HsElsewhere.Sha1));
            Assert.DoesNotContain(psos, p => p.Stages.Count == 1 && p.Stages.ContainsValue(Vs.Sha1)); // the VS feeding a HS writes no SV_Position
            Assert.Equal(0, plan.Stats.Uncovered);
        }
    }

    /// <summary>A DS writing SV_Position draws on its own and also feeds a GS taking its triangles (e.g. one routing them to
    /// render-target slices): both chains plan, alone and with the PS.</summary>
    /// <summary>A control-point component the VS declares but never writes doesn't feed a HS that always reads it: the runtime
    /// rejects that link (Returnal: 34 planned tessellation sets, E_INVALIDARG). An unknown mask on either side links.</summary>
    [Fact]
    public void AComponentTheVsNeverWritesDoesntFeedTheHs()
    {
        var vsShort = Vs with { Outputs = [Cp[0] with { ReadMask = 0x3 }, Cp[1]] };
        var hsReads = Hs with { Inputs = [Cp[0] with { ReadMask = 0xF }, Cp[1]] };
        Assert.False(Planner.Links(vsShort, hsReads));
        Assert.True(Planner.Links(vsShort, hsReads with { Inputs = [Cp[0] with { ReadMask = 0x3 }, Cp[1]] }));
        Assert.True(Planner.Links(vsShort, Hs));
        Assert.True(Planner.Links(Vs, hsReads));
    }

    [Fact]
    public void ADomainShaderWritingPositionAlsoFeedsAGeometryShader()
    {
        var gsAfter = Shader("t-gs-after", Stage.Geometry, Ds.Outputs, [Pos, In("TEXCOORD", 0, 1)], gsInput: 3) with { ShaderModel = "gs_5_0" };
        var plan = new Planner().Build(Ff7.Game, Ue427, Index("PCD3D_SM5", gsAfter), null, Ff7.Nvidia, Ff7.TempDir("tess-ds-gs"), null, CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var sets = body.Where(r => r.Tag == 'S').Select(PsoDb.Parse).Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => new PsoDb.Pso(i.Rs, i.Stages, false, 0)))
            .Where(p => p.Stages.ContainsValue(Ds.Sha1)).Select(Set).ToList();
        Assert.Equal(new[] { Set(Vs, Hs, Ds), Set(Vs, Hs, Ds, Ps), Set(Vs, Hs, Ds, gsAfter), Set(Vs, Hs, Ds, gsAfter, Ps) }.Order(), sets.Order());
    }

    /// <summary>UE 4's root signatures give the HS and DS their own tables (HULL / DOMAIN visibility), not denied; the pre-emit
    /// guard passes them, and still leaves out a DS declaring what UE 4 gives no domain shader (a UAV).</summary>
    [Fact]
    public void RootSignaturesCoverHullAndDomainShaders()
    {
        var st = new SortedDictionary<Stage, ShaderInfo> { [Stage.Vertex] = Vs, [Stage.Hull] = Hs, [Stage.Domain] = Ds, [Stage.Pixel] = Ps };
        foreach (var rule in new[] { Rules.Ue422, Rules.Ue425, Rules.Ue426, Rules.Ff7 })
        {
            var rs = RootSig.Parse(RootSig.Serialize(RootSig.Build(rule, st, false), RootSig.StaticSamplers(rule)));
            Assert.All(st, s => Assert.Null(RootSig.Uncovered(rs, s.Key, s.Value)));
            Assert.Equal(0u, rs.Flags & (0x4 | 0x8)); // DENY_HULL / DENY_DOMAIN
            Assert.Contains(rs.Slots, x => x.Vis == 2 && x.Type == 0); // a HULL-visible SRV range
            Assert.Contains(rs.Slots, x => x.Vis == 3 && x.Type == 2 && x.Base == 1); // the DS's b1, DOMAIN-visible
        }
        var dsUav = Ds with { Sha1 = Hash("t-dsuav"), Bindings = [.. Ds.Bindings, new("uav", 0, 0, 1)] };
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, Ue427, Index("PCD3D_SM5", dsUav), null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir("tess-guard"), new SyncLog(log.Add), CancellationToken.None);
        Assert.Equal(2, plan.Stats.Uncovered); // VS+HS+dsUav alone and with the PS
        Assert.Contains(log, l => l.Contains("Domain uav space 0"));
        Assert.DoesNotContain(PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S'), r => PsoDb.Parse(r).Stages.ContainsValue(dsUav.Sha1));
    }

    /// <summary>Per-stage units: the HS and DS are units of their own (shader + root signature, as every stage but VS/PS), and
    /// under AMD's policy the VS unit is keyed on the HS it feeds.</summary>
    [Fact]
    public void HullAndDomainAreUnits()
    {
        var facts = ExactLayouts.Build([], new Dictionary<string, byte[]>(), UnitPolicy.Amd, new Dictionary<string, ShaderInfo>());
        var stages = new SortedDictionary<int, string> { [(int)Stage.Vertex] = Vs.Sha1, [(int)Stage.Hull] = Hs.Sha1, [(int)Stage.Domain] = Ds.Sha1, [(int)Stage.Pixel] = Ps.Sha1 };
        var units = UnitCover.Units(facts, stages, new string('a', 40), [], 4, "").ToList();
        Assert.Equal([Stage.Vertex, Stage.Pixel, Stage.Domain, Stage.Hull], units.Select(u => u.Stage));
        Assert.Contains("|hs:" + Hs.Sha1, units[0].Key);
    }

    /// <summary>D3D11: a HS or DS can't be drawn alone, so each comes in a '2' pair with a partner of its map; a DS no HS links
    /// with is left out (counted), not planned as an item that would fail.</summary>
    [Fact]
    public void D3D11PairsEveryHullAndDomainShader()
    {
        var index = Index();
        var lonely = Shader("t-dslone", Stage.Domain, [In("NOTHING", 0, 0)], [Pos]) with { ShaderModel = "ds_5_0" };
        index = index with { Shaders = index.Shaders.Append(new(lonely.Sha1, lonely)).ToDictionary(), Maps = [.. index.Maps, new ShaderMap("m3", "Game", "PCD3D_SM5", [lonely.Sha1])] };
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, Ue427 with { GraphicsApi = "D3D11" }, index, null, Ff7.Nvidia, Ff7.TempDir("tess-d3d11"), new SyncLog(log.Add), CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var pairs = body.Where(r => r.Tag == '2').Select(r => (PsoDb.Hex(r.Payload.AsSpan(0, 20)), PsoDb.Hex(r.Payload.AsSpan(20, 20)))).ToList();
        Assert.Equal([(Hs.Sha1, Ds.Sha1), (Hs.Sha1, DsGs.Sha1)], pairs); // HsElsewhere and DsOther: no partner in their maps
        Assert.DoesNotContain(body.Where(r => r.Tag == '1'), r => (Stage)BitConverter.ToUInt32(r.Payload) is Stage.Hull or Stage.Domain);
        Assert.Contains(log, l => l.Contains("d3d11_tess_unpaired 3")); // HsElsewhere, DsOther, lonely
        Assert.Equal(body.Count(r => r.Tag is '1' or '2'), plan.Stats.D3D11Shaders);
    }

    sealed class SyncLog(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    static readonly string Bin = Ff7.ProxyBin;

    /// <summary>The D3D12 claim for tessellation (NVIDIA per-stage): a VS+HS+DS+PS pipeline warmed from a planner-style
    /// synthesized stream (neutral state, patch topology) under exe name X makes the game's own desc (other render target,
    /// depth buffer) a cache hit under X, also for a HS+DS pairing the warm never created; under a fresh name it compiles.
    /// Heavy seeded HS/DS (fresh bytecode), one thread, per-PSO times (SCSKILLER_WARM_TIMES). GPU part: SCSKILLER_GPU_TESTS=1
    /// (holds TestEnv.GpuLockPath; 4 + 2 x 2 PSOs under throwaway names, whose cache files it deletes).
    /// AMD passes too: the game's descs 7.5 / 3.5 / 4.2 ms under the warmed name vs 57 / 19 / 28 fresh,
    /// the never-created pairing hs1 + ds2 included (per stage, like the VS and PS).</summary>
    [Trait("Needs", "Gpu")]
    [Fact]
    public void TessellationPipelinesHitTheDriverCache()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_GPU_TESTS") != "1" || !File.Exists(Path.Combine(Bin, "probe11.exe"))) return;
        var dir = Ff7.TempDir("tess-gpu");
        var seed = Random.Shared.Next(100000, 999999);
        const string Cp = "struct CP{float3 p:POS;float2 uv:TEXCOORD0;};struct PC{float e[3]:SV_TessFactor;float i:SV_InsideTessFactor;};cbuffer C:register(b0){float4 kc;};";
        string Loop(string v, string k, int n) => $"[unroll]for(int j=0;j<{n};++j){{{v}=sin({v}*({k}+j))+cos({v});}}";
        byte[] Hs(string k) => Compile($"hs{k}", Cp + "PC pcf(InputPatch<CP,3> ip){PC o;float3 a=ip[0].p+ip[1].p+ip[2].p;" + Loop("a", k, 32) + "o.e[0]=a.x+kc.x;o.e[1]=a.y;o.e[2]=a.z;o.i=a.x;return o;}"
            + "[domain(\"tri\")][partitioning(\"fractional_odd\")][outputtopology(\"triangle_cw\")][outputcontrolpoints(3)][patchconstantfunc(\"pcf\")]"
            + "CP main(InputPatch<CP,3> ip,uint i:SV_OutputControlPointID){CP o=ip[i];float3 a=o.p;" + Loop("a", k, 32) + "o.p=a;return o;}", "hs_5_0");
        byte[] Ds(string k) => Compile($"ds{k}", Cp + "Texture2D t0:register(t0);SamplerState ss:register(s0);struct O{float4 pos:SV_Position;float2 uv:TEXCOORD0;};"
            + "[domain(\"tri\")] O main(PC pc,float3 b:SV_DomainLocation,const OutputPatch<CP,3> p){O o;float3 a=p[0].p*b.x+p[1].p*b.y+p[2].p*b.z;"
            + "float2 uv=p[0].uv*b.x+p[1].uv*b.y+p[2].uv*b.z;a+=t0.SampleLevel(ss,uv,0).xyz;" + Loop("a", k, 48) + "o.pos=float4(a,1)+kc;o.uv=uv;return o;}", "ds_5_0");
        byte[] Compile(string name, string src, string target)
        {
            var (hlsl, bin) = (Path.Combine(dir, name + ".hlsl"), Path.Combine(dir, name + ".bin"));
            File.WriteAllText(hlsl, src);
            System.Diagnostics.Process.Start(Path.Combine(Bin, "probe11.exe"), ["compile", hlsl, target, bin])!.WaitForExit();
            Assert.True(File.Exists(bin), name);
            return File.ReadAllBytes(bin);
        }
        var (k1, k2) = ($"{seed}.0", $"{seed}.5");
        var vs = Compile("vs", Cp + $"CP main(float3 p:POSITION,float2 uv:TEXCOORD0){{CP o;o.p=p*{k1}+kc.xyz;o.uv=uv;return o;}}", "vs_5_0");
        var ps = Compile("ps", $"Texture2D t0:register(t0);SamplerState ss:register(s0);float4 main(float4 pos:SV_Position,float2 uv:TEXCOORD0):SV_Target{{float4 c=t0.Sample(ss,uv);{Loop("c", k1, 16)}return c;}}", "ps_5_0");
        var (hs1, ds1, hs2, ds2) = (Hs(k1), Ds(k1), Hs(k2), Ds(k2));

        // shaders as the planner sees them (UE 4.26's rule, its counts by hand: fxc output has no UE optional data)
        var blobs = new Dictionary<string, byte[]>();
        ShaderInfo Info(byte[] b, ResourceCounts c)
        {
            var h = PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(b));
            blobs[h] = b;
            return SCSKiller.Core.Unreal.ShaderContainer.Parse(b, h, c)!;
        }
        var (v, p, h1, d1, h2, d2) = (Info(vs, new(1, 0, 0, 0)), Info(ps, new(0, 1, 0, 1)), Info(hs1, new(1, 0, 0, 0)), Info(ds1, new(1, 1, 0, 1)), Info(hs2, new(1, 0, 0, 0)), Info(ds2, new(1, 1, 0, 1)));
        var rsBlob = RootSig.Serialize(RootSig.Build(Rules.Ue426, new SortedDictionary<Stage, ShaderInfo> { [Stage.Vertex] = v, [Stage.Hull] = h1, [Stage.Domain] = d1, [Stage.Pixel] = p }, false), RootSig.Ue426Samplers);
        var rs = PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(rsBlob));
        blobs[rs] = rsBlob;
        SortedDictionary<int, string> St(ShaderInfo h, ShaderInfo d) => new() { [(int)Stage.Vertex] = v.Sha1, [(int)Stage.Hull] = h.Sha1, [(int)Stage.Domain] = d.Sha1, [(int)Stage.Pixel] = p.Sha1 };
        var layout = Planner.VsLayout(v);
        PsoDb.Rec Warm(ShaderInfo h, ShaderInfo d) => new('S', PsoDb.Stream(rs, St(h, d), layout, 4, [PsoDb.R16G16B16A16Float], 0));   // the planner's neutral state
        PsoDb.Rec Game(ShaderInfo h, ShaderInfo d) => new('S', PsoDb.Stream(rs, St(h, d), layout, 4, [28], PsoDb.D32Float));          // RGBA8 + a depth buffer
        string Db(string name, params PsoDb.Rec[] recs)
        {
            var work = Path.Combine(dir, name);
            Directory.CreateDirectory(work);
            using (var f = File.Create(Path.Combine(work, "scskiller.db")))
            {
                foreach (var (h, b) in blobs) PsoDb.WriteBlob(f, h, b);
                foreach (var r in recs) PsoDb.Write(f, r.Tag, r.Payload);
            }
            File.WriteAllBytes(Path.Combine(work, "scskiller_gen.db"), []);
            return work;
        }
        double[] Times(string work, string exe)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Bin, "scskiller_warm.exe"), [work, exe, "--threads", "1"]) { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, UseShellExecute = false };
            psi.Environment["SCSKILLER_WARM_TIMES"] = "1";
            string o;
            using (var pr = System.Diagnostics.Process.Start(psi)!) { o = pr.StandardOutput.ReadToEnd(); pr.WaitForExit(); }
            var rows = File.ReadAllLines(Path.Combine(TestEnv.WarmStage(o, work), "scskiller_warm_times.csv")).Select(l => l.Split(',')).OrderBy(x => int.Parse(x[0])).ToList();
            Assert.All(rows, x => Assert.Equal("1", x[2])); // created
            return rows.Select(x => double.Parse(x[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }

        // a small VS+PS pipeline first in each game run takes the process's first-compile cost (never cached: fresh per run)
        PsoDb.Rec Starter(string tag)
        {
            var (sv, sp) = (Info(Compile("sv" + tag, $"float4 main(uint i:SV_VertexID):SV_Position{{return i*{seed}.{tag};}}", "vs_5_0"), new(0, 0, 0, 0)),
                Info(Compile("sp" + tag, $"float4 main():SV_Target{{return {seed}.{tag};}}", "ps_5_0"), new(0, 0, 0, 0)));
            var b = RootSig.Serialize(RootSig.Build(Rules.Ue426, new SortedDictionary<Stage, ShaderInfo> { [Stage.Vertex] = sv, [Stage.Pixel] = sp }, false), RootSig.Ue426Samplers);
            var h = PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(b));
            blobs[h] = b;
            return new('S', PsoDb.Stream(h, new SortedDictionary<int, string> { [(int)Stage.Vertex] = sv.Sha1, [(int)Stage.Pixel] = sp.Sha1 }, [], 3, [28], 0));
        }

        using var gpu = GpuLock();
        using var caches = new NewCacheFiles(); // the throwaway names' driver-cache files go when the test ends
        var (x, y) = ($"scsk_tess_{seed}.exe", $"scsk_tesscold_{seed}.exe");
        var warm = Times(Db("warm", Warm(h1, d1), Warm(h2, d2)), x);
        // the warmed pairings in another state, and a pairing the warm never created (hs1 + ds2)
        var hit = Times(Db("game", Starter("1"), Game(h1, d1), Game(h1, d2), Game(h2, d2)), x)[1..];
        var cold = Times(Db("cold", Starter("2"), Game(h1, d1), Game(h1, d2), Game(h2, d2)), y)[1..];
        var exact = Times(Db("exact", Starter("3"), Warm(h1, d1), Warm(h2, d2)), x)[1..]; // reference: the warm's own descs again
        var line = $"warm (cold compiles) {string.Join(", ", warm.Select(t => $"{t:F2}"))} ms; the game's descs (hs1+ds1, hs1+ds2, hs2+ds2) under the warmed name {string.Join(", ", hit.Select(t => $"{t:F2}"))} ms, "
            + $"under a fresh name {string.Join(", ", cold.Select(t => $"{t:F2}"))} ms; the warm's descs again (exact hits) {string.Join(", ", exact.Select(t => $"{t:F2}"))} ms";
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "scskiller-tests", "tess-gpu.log"), $"{DateTime.Now:s} {line}{Environment.NewLine}");
        Assert.True(hit.Zip(cold).All(t => t.First * 3 < t.Second), line);
    }

    /// <summary>Deletes, when disposed, the driver-cache files (NVIDIA DXCache, AMD DxcCache / DxCache) that appeared meanwhile:
    /// the test's throwaway exe names' (best effort: a file another process holds stays).</summary>
    sealed class NewCacheFiles : IDisposable
    {
        static readonly string[] Dirs = [@"NVIDIA\DXCache", @"AMD\DxcCache", @"AMD\DxCache"];
        readonly HashSet<string> before = Files();

        static HashSet<string> Files() => Dirs.Select(d => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), d))
            .Where(Directory.Exists).SelectMany(Directory.GetFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            foreach (var f in Files().Except(before))
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static IDisposable GpuLock()
    {
        var path = TestEnv.GpuLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var deadline = DateTime.UtcNow.AddMinutes(30); ; Thread.Sleep(5000))
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(30)) File.Delete(path);
            try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { }
        }
    }
}
