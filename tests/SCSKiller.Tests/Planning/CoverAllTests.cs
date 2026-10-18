using System.Buffers.Binary;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using Xunit.Abstractions;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>The planner's rarer stage sets: VS -> GS without a PS (depth passes), AS -> MS (-> PS), MS -> PS reading
/// per-primitive outputs, pairs across maps with a default material's shader; and stream output records (the proxy records
/// the declaration; the planner reads such records but never synthesizes one).</summary>
public class CoverAllTests(ITestOutputHelper output)
{
    static readonly SigElement Pos = In("SV_POSITION", 0, 0, 0xF, 3, 1);
    static readonly EngineInfo Ue426 = new("Unreal", "4.26", null, "D3D12", false, null);
    static readonly EngineInfo Ue51 = new("Unreal", "5.1", null, "D3D12", false, null);
    static readonly VendorCaps[] AllCaps = [Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true }, Ff7.Amd];

    static List<Pso> Psos(Plan plan)
    {
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var templates = body.Where(r => r.Tag is 'S' or 'G').ToDictionary(r => r.Key, Parse);
        return [.. templates.Values, .. body.Where(r => r.Tag == 'P').Select(r => ParseItem(r.Payload)).Select(i => templates[i.Template] with { Rs = i.Rs, Stages = i.Stages })];
    }

    static string Set(params ShaderInfo[] s) => string.Join('+', s.OrderBy(x => x.Stage).Select(x => x.Sha1[..6]));
    static string Set(Pso p) => string.Join('+', p.Stages.OrderBy(x => x.Key).Select(x => x.Value[..6]));

    // UE's one-pass point-light shadow: the VS passes the vertex on (no SV_Position), the GS writes it per cube face
    static readonly ShaderInfo Vs = Shader("c-vs", Stage.Vertex, [In("ATTRIBUTE", 0, 0, 7)], [In("TEXCOORD", 6, 0)]) with { Counts = new(1, 0, 0, 0), Bindings = [new("cbv", 0, 0, 1)] };
    static readonly ShaderInfo Gs = Shader("c-gs", Stage.Geometry, [In("TEXCOORD", 6, 0)], [Pos, In("SV_RenderTargetArrayIndex", 0, 1, 1, 1, 4)], gsInput: 3) with { ShaderModel = "gs_5_0" };
    static readonly ShaderInfo Ps = Shader("c-ps", Stage.Pixel, [Pos], [Target(0)]);
    static readonly ShaderInfo GsNoPos = Shader("c-gs-so", Stage.Geometry, [In("TEXCOORD", 6, 0)], [In("TEXCOORD", 0, 0)], gsInput: 3) with { ShaderModel = "gs_5_0" }; // feeds stream output only

    [Fact]
    public void DepthOnlyGeometryShaderPasses()
    {
        ShaderInfo[] all = [Vs, Gs, Ps, GsNoPos];
        var index = new ShaderIndex("gs", ["PCD3D_SM5"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM5", all.Select(s => s.Sha1).ToList())]);
        foreach (var caps in AllCaps)
        {
            var plan = new Planner().Build(Ff7.Game, Ue426, index, null, caps, Ff7.TempDir("cover-gs-" + caps.Profile + caps.PerStageCache), null, CancellationToken.None);
            var psos = Psos(plan);
            Assert.Contains(psos, p => Set(p) == Set(Vs, Gs) && p.Topology == 3);         // the depth pass
            Assert.Contains(psos, p => Set(p) == Set(Vs, Gs, Ps));                          // and the color pass
            Assert.DoesNotContain(psos, p => p.Stages.ContainsValue(GsNoPos.Sha1));         // no SV_Position: not rasterizable, not planned
            Assert.DoesNotContain(psos, p => p.Stages.Count == 1 && p.Stages.ContainsValue(Vs.Sha1));
            Assert.Equal(0, plan.Stats.Uncovered);
            if (!caps.PerStageCache) continue;
            // a GS depth pass is drawn into a depth buffer with no render target
            var depth = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').Select(r => (Pso: Parse(r), State: ParseState(r)!)).Single(x => Set(x.Pso) == Set(Vs, Gs)).State;
            Assert.Equal((0, D32Float), (depth.RtFormats.Length, depth.Dsv));
        }
    }

    // UE 5's mesh shading: an AS launching MS groups, the MS rasterizing
    static readonly ShaderInfo As = Shader("c-as", Stage.Amplification, []) with { ShaderModel = "as_6_5", Counts = new(1, 1, 0, 0), Bindings = [new("cbv", 0, 0, 1), new("srv", 0, 0, 1)] };
    static readonly ShaderInfo Ms = Shader("c-ms", Stage.Mesh, [], [Pos, In("TEXCOORD", 0, 1, 3)]) with { ShaderModel = "ms_6_5", Counts = new(1, 0, 0, 0), Bindings = [new("cbv", 0, 0, 1)] };
    static readonly ShaderInfo MsPs = Shader("c-msps", Stage.Pixel, [Pos, In("TEXCOORD", 0, 1, 3)], [Target(0)]) with { ShaderModel = "ps_6_5" };

    [Fact]
    public void AmplificationShaderStageSets()
    {
        ShaderInfo[] all = [As, Ms, MsPs];
        var index = new ShaderIndex("as", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        foreach (var caps in AllCaps)
        {
            var plan = new Planner().Build(Ff7.Game, Ue51, index, null, caps, Ff7.TempDir("cover-as-" + caps.Profile + caps.PerStageCache), null, CancellationToken.None);
            var psos = Psos(plan);
            Assert.Contains(psos, p => Set(p) == Set(As, Ms));
            Assert.Contains(psos, p => Set(p) == Set(As, Ms, MsPs));
            Assert.Equal(0, plan.Stats.Uncovered);
            // the AS gets its own tables, visible to it (UE 5: AMPLIFICATION), not denied: the pre-emit guard passes it
            var blobs = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'B').ToDictionary(r => Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
            var rs = RootSig.Parse(blobs[psos.First(p => p.Stages.ContainsKey((int)Stage.Amplification)).Rs]);
            Assert.Null(RootSig.Uncovered(rs, Stage.Amplification, As));
            Assert.Equal(0u, rs.Flags & 0x100); // DENY_AMPLIFICATION_SHADER_ROOT_ACCESS
            Assert.Contains(rs.Slots, s => s.Vis == 6 && s.Type == 0); // an AMPLIFICATION-visible SRV range
        }
        // a unit of its own (shader + root signature), as every stage but VS/PS
        var facts = ExactLayouts.Build([], new Dictionary<string, byte[]>(), UnitPolicy.Amd, new Dictionary<string, ShaderInfo>());
        var units = UnitCover.Units(facts, new SortedDictionary<int, string> { [(int)Stage.Amplification] = As.Sha1, [(int)Stage.Mesh] = Ms.Sha1 }, new string('a', 40), [], 0, "").ToList();
        Assert.Contains(units, u => u.Stage == Stage.Amplification && u.Shader == As.Sha1);
    }

    /// <summary>An AS pairs only with mesh shaders that take its payload (PSV0's ASInfo / MSInfo payload size): the runtime
    /// rejects any other pair. A mesh shader taking none still rasterizes without it.</summary>
    [Fact]
    public void AmplificationShaderPairsWithMeshShadersTakingItsPayload()
    {
        static byte[] Psv(int at, int payload) { var p = new byte[4 + 24 + 4]; p[0] = 24; BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + at), payload); return p; }
        var parsedAs = ShaderContainer.Parse(Dxil(14 << 16 | 6 << 4 | 6, Psv(0, 128)), Hash("p-as"), new(0, 0, 0, 0))!;
        var parsedMs = ShaderContainer.Parse(Dxil(Ms65, Psv(8, 128), ("OSG1", [("SV_Position", 0, 1, 3, 0, 0xF)])), Hash("p-ms"), new(0, 0, 0, 0))!;
        Assert.Equal((Stage.Amplification, 128, Stage.Mesh, 128), (parsedAs.Stage, parsedAs.Payload, parsedMs.Stage, parsedMs.Payload));

        var sender = As with { Payload = 128 };
        var takes = Ms with { Sha1 = Hash("p-ms-takes"), Payload = 128 };
        var takesNone = Ms with { Sha1 = Hash("p-ms-none") };
        ShaderInfo[] all = [sender, takes, takesNone, MsPs];
        var index = new ShaderIndex("payload", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        foreach (var caps in AllCaps)
        {
            var sets = Psos(new Planner().Build(Ff7.Game, Ue51, index, null, caps, Ff7.TempDir("cover-payload-" + caps.Profile + caps.PerStageCache), null, CancellationToken.None)).Select(Set).ToHashSet();
            Assert.Contains(Set(sender, takes), sets);
            Assert.Contains(Set(sender, takes, MsPs), sets);
            Assert.DoesNotContain(sets, s => s.Contains(sender.Sha1[..6]) && s.Contains(takesNone.Sha1[..6]));
            Assert.Contains(Set(takesNone), sets);
        }
    }

    /// <summary>A DXIL container: the program header's version (kind &lt;&lt; 16 | major &lt;&lt; 4 | minor), a PSV0 part when
    /// given, and 32-byte signature parts (OSG1, PSG1, ...) of (name, index, system value, component type, register, mask).</summary>
    static byte[] Dxil(uint version, byte[]? psv0, params (string Part, (string Name, int Index, int Sys, int Comp, int Reg, byte Mask)[] Elems)[] sigs)
    {
        var parts = new List<(string Fourcc, byte[] Data)>();
        foreach (var (part, elems) in sigs)
        {
            var d = new byte[8 + 32 * elems.Length];
            BinaryPrimitives.WriteInt32LittleEndian(d, elems.Length);
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), 8);
            var names = new List<byte>();
            for (var i = 0; i < elems.Length; i++)
            {
                var (name, idx, sys, comp, reg, mask) = elems[i];
                var o = d.AsSpan(8 + 32 * i);
                BinaryPrimitives.WriteInt32LittleEndian(o[4..], d.Length + names.Count);
                BinaryPrimitives.WriteInt32LittleEndian(o[8..], idx);
                BinaryPrimitives.WriteInt32LittleEndian(o[12..], sys);
                BinaryPrimitives.WriteInt32LittleEndian(o[16..], comp);
                BinaryPrimitives.WriteInt32LittleEndian(o[20..], reg);
                o[24] = mask;
                names.AddRange([.. Encoding.ASCII.GetBytes(name), 0]);
            }
            parts.Add((part, [.. d, .. names]));
        }
        if (psv0 != null) parts.Add(("PSV0", psv0));
        parts.Add(("DXIL", BitConverter.GetBytes(version)));
        var offs = new int[parts.Count];
        var pos = 32 + 4 * parts.Count;
        for (var i = 0; i < parts.Count; i++) { offs[i] = pos; pos += 8 + parts[i].Data.Length; }
        var b = new byte[pos];
        "DXBC"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), pos);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32 + 4 * i), offs[i]);
            Encoding.ASCII.GetBytes(parts[i].Fourcc).CopyTo(b, offs[i]);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offs[i] + 4), parts[i].Data.Length);
            parts[i].Data.CopyTo(b, offs[i] + 8);
        }
        return b;
    }

    const uint Ms65 = 13 << 16 | 6 << 4 | 5;
    const int PrimitiveRow = ShaderContainer.PrimitiveRow;
    static ShaderInfo Ps65(string name, params SigElement[] ins) => Shader(name, Stage.Pixel, ins, [Target(0)]) with { ShaderModel = "ps_6_5" };

    /// <summary>A mesh shader's per-primitive outputs (PSG1) are part of its outputs, marked; a PS reading them is planned
    /// behind it whatever row it packs them in (after its own per-vertex inputs), and a PS reading only per-vertex outputs
    /// still is.</summary>
    [Fact]
    public void MeshShaderPerPrimitiveOutputs()
    {
        // Nanite's raster MS: SV_Position per vertex, a uint4 per primitive
        var nanite = ShaderContainer.Parse(Dxil(Ms65, null, ("OSG1", [("SV_Position", 0, 1, 3, 0, 0xF)]), ("PSG1", [("TEXCOORD", 1, 0, 1, 0, 0xF)])), Hash("c-nanite-ms"), new(0, 0, 0, 0))!;
        Assert.Equal((Stage.Mesh, "ms_6_5"), (nanite.Stage, nanite.ShaderModel));
        Assert.Equal([("SV_Position", 0), ("TEXCOORD", PrimitiveRow)], nanite.Outputs.Select(o => (o.Semantic, o.Register)));
        var ms = Shader("c-ms-prim", Stage.Mesh, [], [In("TEXCOORD", 0, 0, 3), In("SV_Position", 0, 1, 0xF, 3, 1), In("TEXCOORD", 7, PrimitiveRow, 0xF, 1)]) with { ShaderModel = "ms_6_5" };
        var rasterPs = Ps65("c-raster-ps", Pos, In("TEXCOORD", 1, 1, 0xF, 1));                  // SV_Position r0, the primitive's uint4 r1
        var noPos = Ps65("c-nopos-ps", In("TEXCOORD", 0, 0, 3), In("TEXCOORD", 7, 1, 0xF, 1));  // no SV_Position: the primitive's in r1, the MS's SV_Position row
        var vertexOnly = Ps65("c-vertex-ps", In("TEXCOORD", 0, 0, 3));
        var wrongType = Ps65("c-float-ps", Pos, In("TEXCOORD", 1, 1, 0xF, 3));
        ShaderInfo[] all = [nanite, ms, rasterPs, noPos, vertexOnly, wrongType];
        var index = new ShaderIndex("prim", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        foreach (var caps in AllCaps)
        {
            var sets = Psos(new Planner().Build(Ff7.Game, Ue51, index, null, caps, Ff7.TempDir("cover-prim-" + caps.Profile + caps.PerStageCache), null, CancellationToken.None)).Select(Set).ToHashSet();
            Assert.Contains(Set(nanite, rasterPs), sets);
            Assert.Contains(Set(ms, noPos), sets);
            Assert.Contains(Set(ms, vertexOnly), sets);
            Assert.DoesNotContain(Set(nanite, wrongType), sets);
            Assert.DoesNotContain(Set(ms, rasterPs), sets);
        }
    }

    /// <summary>Unreal 5: a shader in at least 1 of every 50 maps (a default material's) pairs with the other stage from any
    /// map; one in fewer doesn't.</summary>
    [Fact]
    public void SharedShadersPairAcrossMaps()
    {
        var nanite = Shader("x-ms", Stage.Mesh, [], [Pos, In("TEXCOORD", 1, PrimitiveRow, 0xF, 1)]) with { ShaderModel = "ms_6_5" };
        var vs = Shader("x-vs", Stage.Vertex, [In("ATTRIBUTE", 0, 0, 7)], [Pos, In("TEXCOORD", 0, 1, 3)]) with { ShaderModel = "vs_6_5" };
        var (sharedRaster, sharedPs, rarePs) = (Ps65("x-raster", Pos, In("TEXCOORD", 1, 1, 0xF, 1)), Ps65("x-shared", Pos, In("TEXCOORD", 0, 1, 3)), Ps65("x-rare", Pos, In("TEXCOORD", 0, 1, 3)));
        var cs = Enumerable.Range(0, 97).Select(i => Shader($"x-cs{i}", Stage.Compute, []) with { ShaderModel = "cs_6_5" }).ToList();
        List<ShaderMap> maps = [new("mat", "Game", "PCD3D_SM6", [nanite.Sha1, vs.Sha1]), .. Enumerable.Range(0, 3).Select(i => new ShaderMap($"d{i}", "Game", "PCD3D_SM6", [sharedRaster.Sha1, sharedPs.Sha1]))];
        maps.AddRange(cs.Select((c, i) => new ShaderMap($"f{i}", "Game", "PCD3D_SM6", i < 2 ? [c.Sha1, rarePs.Sha1] : [c.Sha1]))); // 101 maps: 3 * 50 >= 101 > 2 * 50
        ShaderInfo[] all = [nanite, vs, sharedRaster, sharedPs, rarePs, .. cs];
        var index = new ShaderIndex("shared", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), maps);
        var sets = Psos(new Planner().Build(Ff7.Game, Ue51, index, null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir("cover-shared"), null, CancellationToken.None)).Select(Set).ToHashSet();
        Assert.Contains(Set(nanite, sharedRaster), sets);
        Assert.Contains(Set(vs, sharedPs), sets);
        Assert.DoesNotContain(Set(vs, rarePs), sets);
    }

    /// <summary>SILENT HILL: Townfall (stock UE 5.6) against SCSKiller's recording of it, read only: the 5.5-5.7 rule rebuilds
    /// every recorded root signature byte for byte (10932/10932), and the no-recording plan
    /// compiles the units (shader + root signature) of every recorded mesh shader PSO and of all but a few others whose
    /// shaders ship in its files. Those few: a global VS writing RECT_INDEX drawn with global PSs that read part of its
    /// outputs (pairing global shaders by subset costs ~8% more PSOs for them).</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void TownfallRecordingIsPlannedWithoutIt()
    {
        var game = new SCSKiller.Core.Games.SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1636440");
        if (game == null) return;
        var reader = new UnrealReader(Ff7.TempDir("cover-townfall-data"));
        var engine = reader.Detect(game)!;
        if (Ff7.Recording(game, engine, reader, "cover-townfall-rec") is not { } db) return;
        var recs = Read(db).ToList();
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir("cover-townfall-plan"), null, CancellationToken.None);
        var units = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').Select(Parse).SelectMany(p => p.Stages.Select(s => (s.Key, s.Value, p.Rs))).ToHashSet();
        var psos = recs.Where(r => r.Tag is 'G' or 'C' or 'S').DistinctBy(r => r.Key).Select(Parse).Where(p => p.Stages.Values.All(index.Shaders.ContainsKey)).ToList();
        var missed = psos.Where(p => !p.Stages.All(s => units.Contains((s.Key, s.Value, p.Rs)))).ToList();
        var mesh = psos.Count(p => p.Stages.ContainsKey((int)Stage.Mesh));
        var blobs = recs.Where(r => r.Tag == 'B').GroupBy(r => Hex(r.Payload.AsSpan(0, 20))).ToDictionary(g => g.Key, g => g.First().Payload[20..]);
        var withRs = psos.Where(p => blobs.ContainsKey(p.Rs)).ToList();
        var rebuilt = withRs.Count(p => RootSig.Serialize(RootSig.Build(RootSig.RuleFor(engine)!, p.Stages.ToDictionary(s => (Stage)s.Key, s => index.Shaders[s.Value]), true),
            RootSig.StaticSamplers(RootSig.RuleFor(engine)!)).AsSpan().SequenceEqual(blobs[p.Rs]));
        output.WriteLine($"root signatures rebuilt byte-exact: {rebuilt}/{withRs.Count} recorded PSOs ({RootSig.RuleFor(engine)})");
        output.WriteLine($"plan {plan.Stats.Generated} PSOs; recorded PSOs of shaders in the files whose units it compiles: {psos.Count - missed.Count}/{psos.Count}, {mesh} with a mesh shader; missed: "
            + string.Join(", ", missed.GroupBy(p => string.Join('+', p.Stages.Keys.Select(k => (Stage)k))).Select(g => $"{g.Key} {g.Count()}")));
        Assert.True(psos.Count > 10_000 && mesh > 100, $"{psos.Count} {mesh}");
        Assert.Equal(withRs.Count, rebuilt);   // what ConfirmedEngines' 5.6 entry rests on
        Assert.DoesNotContain(missed, p => p.Stages.ContainsKey((int)Stage.Mesh));
        Assert.True(missed.Count * 1000 <= psos.Count, $"{missed.Count} missed");
    }

    static string Townfall(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Townfall", file);

    /// <summary>SILENT HILL: Townfall's recorder csv (its launch before any compile) and the keys of its RayQuery PSOs created
    /// over 3 ms: those up to NVIDIA's floor count apart, from the compiles and the startup's alike, and none over it.</summary>
    [FixtureFact("Townfall")]
    public void TownfallRayQueryCreatesCountApart()
    {
        var csv = Townfall("scskiller_creates.csv");
        var rq = SCSKiller.Core.App.SessionLog.ReadRayQueryKeys(Townfall("rayquery.keys"))!;
        var (before, after) = (SCSKiller.Core.App.SessionLog.Read(csv).Last!, SCSKiller.Core.App.SessionLog.Read(csv, rayQuery: rq).Last!);
        output.WriteLine($"{before.Compiles} + {before.StartupCompiles} at startup compiles -> {after.Compiles} + {after.StartupCompiles} at startup"
            + $" + {after.RayQueryRecompiles} RayQuery PSOs at the floor");
        Assert.Equal(before.Compiles + before.StartupCompiles, after.Compiles + after.StartupCompiles + after.RayQueryRecompiles);
        var floor = File.ReadLines(csv).Select(l => l.Split(',')).Count(f => f.Length > 5 && rq.Contains(f[5])
            && double.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture) is > 3 and <= SCSKiller.Core.App.SessionLog.RayQueryFloorMs);
        Assert.Equal(floor, after.RayQueryRecompiles);
        Assert.True(after.RayQueryRecompiles > 0);
    }

    /// <summary>SILENT HILL: Townfall terminates itself, so its launch has no #end: it lasts until the exit the app watched,
    /// not until its last create, on the recorder's clock (from the #session stamp's #clock).</summary>
    [FixtureFact("Townfall")]
    public void TownfallSessionLastsUntilTheWatchedExit()
    {
        var csv = Townfall("scskiller_creates.csv");
        var lines = File.ReadAllLines(csv);
        Assert.DoesNotContain(lines, l => l.StartsWith("#end"));
        var session = Array.FindLastIndex(lines, l => l.StartsWith("#session"));
        var start = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(lines[session].Split(',')[1]));
        var clock = lines.Skip(session + 1).TakeWhile(l => l.StartsWith('#')).FirstOrDefault(l => l.StartsWith("#clock,")) is { } c
            ? double.Parse(c.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var byCreates = SCSKiller.Core.App.SessionLog.Read(csv).Last!.Duration;
        var watched = SCSKiller.Core.App.SessionLog.Read(csv, played: new(start.AddSeconds(-3), start.AddMinutes(25))).Last!.Duration;
        output.WriteLine($"last launch: {byCreates} by its last create, {watched} by the watched exit");
        Assert.True(byCreates < TimeSpan.FromMinutes(25));
        Assert.Equal(TimeSpan.FromMinutes(25) + TimeSpan.FromMilliseconds(clock), watched);
    }

    /// <summary>A stream output declaration in a record: a stream's subobject 0x10007 and a 'G' desc's tail are skipped by
    /// Parse / ParseState and returned by StreamOutputOf; records without one keep their old bytes.</summary>
    [Fact]
    public void StreamOutputRecordsParse()
    {
        var (rs, vs) = (Hash("so-rs"), Hash("so-vs"));
        var so = new List<byte>();
        void U(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); so.AddRange(b); }
        U(2); // entries: TEXCOORD0.xyzw to slot 0, then a 2-component gap
        U(0); U(8); so.AddRange("TEXCOORD"u8.ToArray()); U(0); U(0); U(4); U(0);
        U(0); U(uint.MaxValue); U(0); U(0); U(2); U(0);
        U(1); U(24); // strides
        U(uint.MaxValue); // no rasterized stream
        var sub = new List<byte>();
        void S(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); sub.AddRange(b); }
        S(4); S(0); sub.AddRange(Convert.FromHexString(rs)); S(1); sub.AddRange(Convert.FromHexString(vs)); S(SoDecl); sub.AddRange(so); S(14); S(1);
        var s = new Rec('S', [.. sub]);
        var p = Parse(s);
        Assert.Equal((rs, vs, 1u), (p.Rs, p.Stages[(int)Stage.Vertex], p.Topology));
        Assert.Equal(so, StreamOutputOf(s));
        Assert.Equal(1u, ParseState(s)!.Topology);

        var g = new byte[616]; // a 'G' payload without stream output (the format since the first recording)
        Convert.FromHexString(rs).CopyTo(g, 0);
        Convert.FromHexString(vs).CopyTo(g, 20);
        Assert.Null(StreamOutputOf(new Rec('G', g)));
        var gso = new Rec('G', [.. g, .. so]);
        Assert.Equal(so, StreamOutputOf(gso));
        Assert.Equal(Parse(new Rec('G', g)).Tuple, Parse(gso).Tuple);
        Assert.Null(StreamOutputOf(new Rec('S', Stream(rs, new SortedDictionary<int, string> { [1] = vs }, [], 3, [], D32Float)))); // synthesized: never one
    }
}
