using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using Xunit.Abstractions;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Tests.Planning;

public class ExactLayoutsTests(ITestOutputHelper output)
{
    internal static string L(IEnumerable<LayoutElem> l) => string.Join(';', l);

    internal static SigElement In(string sem, int idx, int reg, byte mask = 0xF, int comp = 3, int sys = 0) => new(sem, idx, reg, mask, sys, comp);
    internal static SigElement Target(int idx, int comp = 3) => new("SV_Target", idx, idx, 0xF, 0, comp);

    internal static ShaderInfo Shader(string name, Stage stage, IReadOnlyList<SigElement> inputs, IReadOnlyList<SigElement>? outputs = null, int gsInput = 0) =>
        new(Hash(name), stage, stage == Stage.Pixel ? "ps_5_0" : "vs_5_0", 100, new ResourceCounts(0, 0, 0, 0), [], inputs, outputs ?? [], gsInput);

    internal static string Hash(string s) => Hex(SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(s)));

    /// <summary>A raw 1.1 RTS0 (no DXBC container) with root-constant parameters (visibility, register) and no samplers.</summary>
    internal static byte[] Rts0(uint flags, params (uint Vis, uint Reg)[] constants)
    {
        var n = (uint)constants.Length;
        var u = new List<uint> { 2, n, 24, 0, 24 + 24 * n, flags };
        for (var i = 0u; i < n; i++) u.AddRange([1, constants[i].Vis, 24 + 12 * n + 12 * i]);
        foreach (var c in constants) u.AddRange([c.Reg, 0, 4]);
        return [.. u.SelectMany(BitConverter.GetBytes)];
    }

    internal static Rec Gfx(string rs, ShaderInfo? vs, ShaderInfo? ps, List<LayoutElem> layout, uint[] rt, byte[]? masks = null, ShaderInfo? gs = null, uint topo = 3)
    {
        var st = new Dictionary<int, string>();
        if (vs != null) st[(int)Stage.Vertex] = vs.Sha1;
        if (ps != null) st[(int)Stage.Pixel] = ps.Sha1;
        if (gs != null) st[(int)Stage.Geometry] = gs.Sha1;
        return new Rec('S', Stream(rs, st, layout, topo, rt, 0, masks));
    }

    static readonly List<LayoutElem> PosUvColor = [new("POSITION", 0, 6, 0), new("TEXCOORD", 0, 34, 12), new("COLOR", 0, 28, 16)];
    static readonly ShaderInfo VsA = Shader("vsA", Stage.Vertex, [In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3), In("SV_VertexID", 0, 2, 1, 1, 6)]);

    [Fact]
    public void ReadLayoutKeepsOnlyReadElementsSortedAndUpperCased()
    {
        List<LayoutElem> layout = [new("texcoord", 0, 34, 12), new("Color", 0, 28, 16, 1), new("POSITION", 0, 6, 0)];
        Assert.Equal([new("POSITION", 0, 6, 0), new("TEXCOORD", 0, 34, 12)], ExactLayouts.ReadLayout(layout, VsA));
        // APPEND_ALIGNED resolves per slot before unread elements are dropped
        List<LayoutElem> appended = [new("COLOR", 0, 28, 0), new("POSITION", 0, 6, ExactLayouts.AppendAligned), new("TEXCOORD", 0, 34, ExactLayouts.AppendAligned), new("TEXCOORD", 1, 34, ExactLayouts.AppendAligned, 1)];
        Assert.Equal([4u, 16u, 0u], ExactLayouts.Explicit(appended).Skip(1).Select(e => e.Offset));
    }

    [Fact]
    public void ResolvesLayoutsByLevel()
    {
        var rs = Hash("rs");
        var vsNormal = Shader("vsN", Stage.Vertex, [In("NORMAL", 0, 0, 7)]);
        var recs = new[]
        {
            Gfx(rs, VsA, null, PosUvColor, []),
            Gfx(rs, VsA, null, [.. PosUvColor, new("NORMAL", 0, 6, 0, 1)], []), // same read layout: one entry
            Gfx(rs, vsNormal, null, [new("NORMAL", 0, 37, 0, 1, 1, 1)], []), // per instance
        };
        var vsSameSig = Shader("vsB", Stage.Vertex, VsA.Inputs);
        var vsPosOnly = Shader("vsC", Stage.Vertex, [In("POSITION", 0, 0, 0xF)]); // another register mask: not the same SigKey
        var vsPosNormal = Shader("vsD", Stage.Vertex, [In("POSITION", 0, 0, 7), In("NORMAL", 0, 1, 7)]); // no single layout reads as float
        var vsUint = Shader("vsE", Stage.Vertex, [In("BLENDINDICES", 0, 0, 0xF, 1)]); // never recorded
        var vsNone = Shader("vsF", Stage.Vertex, [In("SV_VertexID", 0, 0, 1, 1, 6)]);
        var x = ExactLayouts.Build(recs, new Dictionary<string, byte[]>(), UnitPolicy.Amd,
            new[] { VsA, vsNormal, vsSameSig, vsPosOnly, vsPosNormal, vsUint, vsNone }.ToDictionary(s => s.Sha1));

        var readA = ExactLayouts.ReadLayout(PosUvColor, VsA);
        var l0 = x.Layouts(VsA);
        Assert.Equal(Provenance.Exact, l0.Provenance);
        Assert.Equal(L(readA), L(Assert.Single(l0.Value)));
        Assert.Equal((Provenance.Exact, ""), (x.Layouts(vsNone).Provenance, L(Assert.Single(x.Layouts(vsNone).Value))));
        var l1 = x.Layouts(vsSameSig);
        Assert.Equal((Provenance.Inferred, L(readA)), (l1.Provenance, L(Assert.Single(l1.Value))));
        var l2 = x.Layouts(vsPosOnly); // the smallest covering layout, projected
        Assert.Equal((Provenance.Inferred, "LayoutElem { Semantic = POSITION, Index = 0, Format = 6, Offset = 0, Slot = 0, Class = 0, Step = 0 }"), (l2.Provenance, L(Assert.Single(l2.Value))));
        var l3 = x.Layouts(vsPosNormal); // NORMAL's recorded element is UNORM in one layout and float in another: per element, the most common
        Assert.Equal(Provenance.Inferred, l3.Provenance); // the 2nd recording has both as float
        var synth = x.Layouts(vsUint);
        Assert.Equal((Provenance.Guessed, L(Planner.VsLayout(vsUint))), (synth.Provenance, L(Assert.Single(synth.Value))));
    }

    /// <summary>A recording's distinct layouts are each kept once, in recording order, however often they repeat; a VS whose
    /// inputs another VS already resolved by the covering layouts gets what it would get alone.</summary>
    [Fact]
    public void ManyLayoutsAreKeptOnceInOrderAndCoversResolveAsAlone()
    {
        var rs = Hash("rs");
        var vss = Enumerable.Range(0, 10).Select(i => Shader($"many-vs{i}", Stage.Vertex, [In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3)])).ToList();
        List<LayoutElem> Layout(int k) => [new("POSITION", 0, 6, 0), new("TEXCOORD", 0, 34, 12 + 4 * (uint)(k % 7)), new("COLOR", 0, 28, 64 + 4 * (uint)k)];
        var recs = Enumerable.Range(0, 300).Select(i => (Vs: vss[i % 10], Layout: Layout(i * 7 % 50))).ToList();
        var x = ExactLayouts.Build(recs.Select(r => Gfx(rs, r.Vs, null, r.Layout, [])), new Dictionary<string, byte[]>(), UnitPolicy.Amd, vss.ToDictionary(s => s.Sha1));

        static List<string> Distinct(IEnumerable<List<LayoutElem>> ls) { var seen = new List<string>(); foreach (var l in ls.Select(L)) if (!seen.Contains(l)) seen.Add(l); return seen; }
        Assert.Equal(Distinct(recs.Select(r => r.Layout)), x.FullLayouts.Select(L));
        Assert.Equal(50, x.FullLayouts.Count);
        foreach (var vs in vss)
            Assert.Equal(Distinct(recs.Where(r => r.Vs == vs).Select(r => ExactLayouts.ReadLayout(r.Layout, vs))), x.ReadLayouts[vs.Sha1].Select(L));
        Assert.Equal(Distinct(recs.Select(r => ExactLayouts.ReadLayout(r.Layout, r.Vs))), x.LayoutsBySig[ExactLayouts.SigKey(vss[0])].Select(L));

        var p = Shader("many-p", Stage.Vertex, [In("POSITION", 0, 0, 0xF)]);
        var q = Shader("many-q", Stage.Vertex, [In("POSITION", 0, 2, 0xF)]);   // the same inputs in another register
        var alone = ExactLayouts.Build(recs.Select(r => Gfx(rs, r.Vs, null, r.Layout, [])), new Dictionary<string, byte[]>(), UnitPolicy.Amd, vss.ToDictionary(s => s.Sha1));
        Assert.Equal(Provenance.Inferred, x.Layouts(p).Provenance);
        var shared = x.Layouts(q);
        Assert.Equal((alone.Layouts(q).Provenance, string.Join('|', alone.Layouts(q).Value.Select(L))), (shared.Provenance, string.Join('|', shared.Value.Select(L))));
    }

    [Fact]
    public void GuessesPerElementWhenNoLayoutCoversAll()
    {
        var rs = Hash("rs");
        var vsP = Shader("vsP", Stage.Vertex, [In("POSITION", 0, 0, 7)]);
        var vsN = Shader("vsN", Stage.Vertex, [In("NORMAL", 0, 0, 7)]);
        var vsPN = Shader("vsPN", Stage.Vertex, [In("POSITION", 0, 0, 7), In("NORMAL", 0, 1, 7)]);
        var recs = new[]
        {
            Gfx(rs, vsP, null, [new("POSITION", 0, 6, 0)], []),
            Gfx(rs, vsP, null, [new("POSITION", 0, 2, 0, 2)], []),
            Gfx(rs, vsN, null, [new("POSITION", 0, 2, 0, 2), new("NORMAL", 0, 1, 16)], []), // uint: not a float NORMAL
            Gfx(rs, vsN, null, [new("NORMAL", 0, 35, 4, 1)], []),
        };
        var x = ExactLayouts.Build(recs, new Dictionary<string, byte[]>(), UnitPolicy.Amd, new[] { vsP, vsN, vsPN }.ToDictionary(s => s.Sha1));
        var r = x.Layouts(vsPN);
        Assert.Equal(Provenance.Guessed, r.Provenance);
        Assert.Equal([new("NORMAL", 0, 35, 4, 1), new("POSITION", 0, 2, 0, 2)], Assert.Single(r.Value)); // POSITION: RGBA32F slot 2 seen twice
    }

    /// <summary>Jedi: Survivor's case: per element, ATTRIBUTE9 was recorded per-instance in slot 5 and ATTRIBUTE11 per-vertex in
    /// slot 5 (by different layouts). A slot can't hold both (E_INVALIDARG), so the guess moves the minority class (tie:
    /// per-vertex stays); with no other recorded slot for it, to a fresh one, offset kept.</summary>
    [Fact]
    public void GuessNeverMixesClassesInASlot()
    {
        var rs = Hash("rs");
        var vs9 = Shader("vs9", Stage.Vertex, [In("ATTRIBUTE", 9, 0, 7)]);
        var vs11 = Shader("vs11", Stage.Vertex, [In("ATTRIBUTE", 11, 0, 7), In("ATTRIBUTE", 12, 1, 7)]);
        var vsMix = Shader("vsMix", Stage.Vertex, [In("ATTRIBUTE", 0, 0, 7), In("ATTRIBUTE", 9, 1, 7), In("ATTRIBUTE", 11, 2, 7)]);
        var recs = new[]
        {
            Gfx(rs, vs9, null, [new("ATTRIBUTE", 9, 10, 0, 5, 1, 1)], []),
            Gfx(rs, vs11, null, [new("ATTRIBUTE", 0, 6, 0), new("ATTRIBUTE", 11, 6, 0, 5), new("ATTRIBUTE", 12, 6, 12, 5)], []),
        };
        var x = ExactLayouts.Build(recs, new Dictionary<string, byte[]>(), UnitPolicy.Amd, new[] { vs9, vs11, vsMix }.ToDictionary(s => s.Sha1));
        var r = x.Layouts(vsMix);
        Assert.Equal(Provenance.Guessed, r.Provenance);
        Assert.Equal([new("ATTRIBUTE", 0, 6, 0), new("ATTRIBUTE", 9, 10, 0, 6, 1, 1), new("ATTRIBUTE", 11, 6, 0, 5)], Assert.Single(r.Value));
        Assert.Equal(1, x.ClassFixes["fresh slot"]);
        List<LayoutElem> clean = [new("ATTRIBUTE", 0, 6, 0), new("ATTRIBUTE", 1, 6, 0, 1, 1, 1)];
        Assert.Same(clean, x.OneClassPerSlot(clean));
    }

    /// <summary>A moved element goes where the recording has it in its class (the slot is in AMD's VS key; a fresh slot is a
    /// sure miss): as one recorded layout has all the moved ones, else each at its most recorded slot; step rates count as
    /// class (one per per-instance slot); slots past 31 are invalid, and a guess with no valid slot left isn't made.</summary>
    [Fact]
    public void MovedElementsGoWhereTheRecordingHasThem()
    {
        var x = ExactLayouts.Build([
            Gfx(Hash("rs"), Shader("r1", Stage.Vertex, [In("ATTRIBUTE", 9, 0, 7), In("ATTRIBUTE", 10, 1, 7)]), null,
                [new("ATTRIBUTE", 9, 10, 0, 7, 1, 1), new("ATTRIBUTE", 10, 10, 16, 7, 1, 1)], []), // both per-instance, slot 7
            Gfx(Hash("rs"), Shader("r2", Stage.Vertex, [In("ATTRIBUTE", 12, 0, 7)]), null, [new("ATTRIBUTE", 12, 10, 8, 3, 1, 2)], []), // step 2, slot 3
            Gfx(Hash("rs"), Shader("r3", Stage.Vertex, [In("ATTRIBUTE", 14, 0, 7)]), null, [new("ATTRIBUTE", 14, 10, 24, 3, 1, 2)], []), // another layout
        ], new Dictionary<string, byte[]>(), UnitPolicy.Amd);

        // ATTRIBUTE9/10 per-instance in the per-vertex slot 5: the recorded layout's slot 7 and offsets
        List<LayoutElem> mixed = [new("ATTRIBUTE", 0, 6, 0), new("ATTRIBUTE", 1, 6, 0, 5), new("ATTRIBUTE", 2, 6, 12, 5),
            new("ATTRIBUTE", 9, 10, 32, 5, 1, 1), new("ATTRIBUTE", 10, 10, 48, 5, 1, 1)];
        Assert.Equal([.. mixed.Take(3), new("ATTRIBUTE", 9, 10, 0, 7, 1, 1), new("ATTRIBUTE", 10, 10, 16, 7, 1, 1)], x.OneClassPerSlot(mixed));
        Assert.Equal(1, x.ClassFixes["recorded layout"]);

        // step rates 1 and 2 in one per-instance slot: the step-2 elements (recorded by different layouts) each to its
        // recorded slot 3
        List<LayoutElem> steps = [new("ATTRIBUTE", 0, 6, 0), new("ATTRIBUTE", 11, 10, 0, 4, 1, 1), new("ATTRIBUTE", 13, 10, 16, 4, 1, 1),
            new("ATTRIBUTE", 15, 10, 48, 4, 1, 1), new("ATTRIBUTE", 12, 10, 32, 4, 1, 2), new("ATTRIBUTE", 14, 10, 64, 4, 1, 2)];
        Assert.False(ExactLayouts.ValidSlots(steps));
        Assert.Equal([.. steps.Take(4), new("ATTRIBUTE", 12, 10, 8, 3, 1, 2), new("ATTRIBUTE", 14, 10, 24, 3, 1, 2)], x.OneClassPerSlot(steps));
        Assert.Equal(1, x.ClassFixes["recorded slot"]);

        // nothing recorded for the moved element and slot 31 taken: no valid slot is left
        List<LayoutElem> full = [new("ATTRIBUTE", 0, 6, 0, 31), new("ATTRIBUTE", 20, 6, 16, 31, 1, 1)];
        Assert.Null(x.OneClassPerSlot(full));
        Assert.Equal(1, x.ClassFixes["skipped"]);
        Assert.False(ExactLayouts.ValidSlots([new("ATTRIBUTE", 0, 6, 0, 32)]));
    }

    /// <summary>The runtime's layout rules the planner relies on, checked on WARP by `selftest layoutrules` (CPU only, no GPU
    /// cache): one classification and one step rate per slot, slots 0..31.</summary>
    [Fact]
    public void RuntimeLayoutRules()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SCSKiller.slnx"))) root = root.Parent;
        var exe = root == null ? null : Path.Combine(root.FullName, "proxy", "build", "Release", "selftest.exe");
        if (exe == null || !File.Exists(exe)) return; // this checkout's proxy isn't built
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "layoutrules") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!;
        var rows = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(' ')).ToDictionary(f => f[0], f => f[1] == "0x00000000");
        p.WaitForExit();
        Assert.Equal(new Dictionary<string, bool>
        {
            ["control"] = true, ["mixed-class-one-slot"] = false, ["step-rates-1-2-one-slot"] = false, ["step-rates-0-1-one-slot"] = false,
            ["slot-31"] = true, ["slot-32"] = false,
        }, rows);
    }

    [Fact]
    public void ResolvesExportShapes()
    {
        var rs = Hash("rs");
        var vs = Shader("vs", Stage.Vertex, []);
        ShaderInfo Ps(string n, params SigElement[] outs) => Shader(n, Stage.Pixel, [], outs);
        var one = new[] { Target(0) };
        var (p1, p2, p3, pNew, pUint) = (Ps("p1", one), Ps("p2", one), Ps("p3", one), Ps("pNew", one), Ps("pUint", Target(0), Target(1, 1)));
        var recs = new List<Rec>
        {
            Gfx(rs, vs, p1, [], [28]), Gfx(rs, vs, p1, [], [24]), Gfx(rs, vs, p1, [], [10, 61]), // RGBA8 and R10G10B10A2: one shape
            Gfx(rs, vs, p2, [], [41], [0]), // write mask 0
        };
        for (var i = 0; i < 7; i++) recs.Add(Gfx(rs, vs, p3, [], [87]));
        var x = ExactLayouts.Build(recs, new Dictionary<string, byte[]>(), UnitPolicy.Amd, new[] { vs, p1, p2, p3, pUint }.ToDictionary(s => s.Sha1));

        Assert.Equal((Provenance.Exact, "fp16|fp16,61"), (x.Shapes(p1).Provenance, string.Join('|', x.Shapes(p1).Value)));
        Assert.Equal("41/m0", Assert.Single(x.Shapes(p2).Value));
        // same output signature: fp16 x9, 41/m0 x1, fp16,61 x1 -> fp16 alone is 9/11 < 90%, + the next most used
        var inferred = x.Shapes(pNew);
        Assert.Equal(Provenance.Inferred, inferred.Provenance);
        Assert.Equal(["fp16", "41/m0"], inferred.Value);
        var guessed = x.Shapes(pUint);
        Assert.Equal((Provenance.Guessed, "fp16,3"), (guessed.Provenance, Assert.Single(guessed.Value)));
        Assert.Equal([10u, 3u], x.ShapeExample["fp16,3"].Formats);
    }

    [Fact]
    public void RootSignatureKeyIgnoresOnlyDenyFlags()
    {
        var (a, b, c) = (Rts0(0x1 | 0x20), Rts0(0x1 | 0x2 | 0x4), Rts0(0x1 | 0x40));
        var blobs = new[] { a, b, c }.ToDictionary(r => Hex(SHA1.HashData(r)));
        var x = ExactLayouts.Build([], blobs, UnitPolicy.Amd);
        var keys = blobs.Keys.Select(x.RsKey).ToList();
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[0], keys[2]);
        var nv = ExactLayouts.Build([], blobs, UnitPolicy.Nvidia);
        Assert.Equal(3, blobs.Keys.Select(nv.RsKey).Distinct().Count());
        Assert.Equal("unknown", x.RsKey("unknown"));
    }

    [Fact]
    public void Topologies()
    {
        var rs = Hash("rs");
        var vs = Shader("vs", Stage.Vertex, []);
        var gs = Shader("gs", Stage.Geometry, [], null, 1);
        var x = ExactLayouts.Build([Gfx(rs, vs, null, [], [], null, null, 2)], new Dictionary<string, byte[]>(), UnitPolicy.Amd, new[] { vs, gs }.ToDictionary(s => s.Sha1));
        Assert.Equal([2u], x.Topologies(new Dictionary<int, string> { [1] = vs.Sha1 }).Value);
        Assert.Equal([1u], x.Topologies(new Dictionary<int, string> { [1] = vs.Sha1, [5] = gs.Sha1 }).Value);
        Assert.Equal([4u], x.Topologies(new Dictionary<int, string> { [1] = vs.Sha1, [4] = Hash("hs") }).Value);
        Assert.Equal((Provenance.Guessed, 3u), (x.Topologies(new Dictionary<int, string> { [1] = Hash("other") }).Provenance, x.Topologies(new Dictionary<int, string> { [1] = Hash("other") }).Value[0]));
        Assert.Equal(UnitPolicy.Amd, UnitPolicy.For(new VendorCaps("amd-1", true, false, false, true)));
        Assert.Equal(UnitPolicy.Nvidia, UnitPolicy.For(new VendorCaps("x", true, true, false, true)));
        Assert.Null(UnitPolicy.For(Ff7.Nvidia));
    }

    /// <summary>The numbers of the read-only Python pass over the rehydrated FF7 recording (NVIDIA, 994 PSOs).</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7RecordingFacts()
    {
        if (!File.Exists(Ff7.RehydratedDb)) return;
        var x = ExactLayouts.FromDb(Ff7.RehydratedDb, UnitPolicy.Amd);
        var g = x.Graphics;
        Assert.Equal((567, 427), (g.Count, x.Compute.Count));

        var vss = g.Where(s => s.Stages.ContainsKey(1)).Select(s => s.Stages[1]).Distinct().ToList();
        Assert.Equal(174, vss.Count);
        Assert.All(vss, v => Assert.Equal([3u], x.TopoOf[v]));
        Assert.All(g, s => Assert.All(s.Layout, e => Assert.NotEqual(ExactLayouts.AppendAligned, e.Offset)));

        // per recorded PSO: layout elements its VS doesn't read, and read ones whose used mask is 0
        int unread = 0, mask0 = 0;
        foreach (var s in g.Where(s => s.Stages.ContainsKey(1)))
        {
            var vs = x.Shaders[s.Stages[1]];
            var read = ExactLayouts.ReadLayout(s.Layout, vs);
            unread += s.Layout.Count - read.Count;
            var used = vs.Inputs.Select((e, i) => (e, i)).Where(t => t.e.SysValue == 0).ToDictionary(t => (t.e.Semantic.ToUpperInvariant(), t.e.Index), t => t.e.ReadMask);
            mask0 += read.Count(e => used[(e.Semantic, e.Index)] == 0);
        }
        Assert.Equal((921, 687), (unread, mask0));

        var full = g.Where(s => s.Stages.ContainsKey(1)).GroupBy(s => s.Stages[1]).Count(v => v.Select(s => L(s.Layout)).Distinct().Count() > 1);
        Assert.Equal((53, 10), (full, x.ReadLayouts.Count(v => v.Value.Count > 1)));
        // the Python pass keyed signatures with the used mask too: 27 keys, 21 with 1 read layout and 6 with 2. Without it
        // (probes-2: a declared-unread input is FULL as well, the used mask is irrelevant) VSs merge into fewer, wider keys
        var bySig = string.Join(' ', x.LayoutsBySig.GroupBy(v => v.Value.Count).OrderBy(k => k.Key).Select(k => $"{k.Count()}x{k.Key}"));
        output.WriteLine($"SigKeys {x.LayoutsBySig.Count}: {bySig}");
        Assert.Equal((16, "9x1 5x2 1x3 1x4"), (x.LayoutsBySig.Count, bySig));

        // pixel shaders drawn with a VS (the Python pass left mesh pipelines out: with them, 274 PSs and 9 output signatures)
        var pss = g.Where(s => s.Stages.ContainsKey(1) && s.Stages.ContainsKey(2)).Select(s => x.Shaders[s.Stages[2]]).Distinct().ToList();
        Assert.Equal((262, 8), (pss.Count, pss.Select(ExactLayouts.OutSig).Distinct().Count()));
        Assert.Equal(6, x.ShapesByOutSig["SV_TARGET/0/0/3"].Count); // SV_Target0.xyzw

        var rss = g.Select(s => s.Rs).Concat(x.Compute.Select(c => c.Rs)).Distinct().ToList();
        Assert.Equal((142, 142), (rss.Count, rss.Select(x.RsKey).Distinct().Count())); // the DENY mask merges none

        var units = UnitCounts(x);
        output.WriteLine($"units: {units}");
        Assert.Equal((243, 27, 313, 427), units); // the Python pass: 270 VS units (= 243 VS + 27 MS), 313 PS, 427 CS; UnitCoverTests has the probes-2 keys
    }

    /// <summary>Units as the design first keyed them (whole RS key for a VS, next stage = GS/HS or none), to match the Python pass.</summary>
    static (int Vs, int Ms, int Ps, int Cs) UnitCounts(ExactLayouts x)
    {
        var g = x.Graphics;
        string Next(PsoState s) => s.Stages.GetValueOrDefault((int)Stage.Geometry) ?? s.Stages.GetValueOrDefault((int)Stage.Hull) ?? "";
        return (g.Where(s => s.Stages.ContainsKey(1)).Select(s => $"{s.Stages[1]}|{L(ExactLayouts.ReadLayout(s.Layout, x.Shaders[s.Stages[1]]))}|{s.Topology}|{x.RsKey(s.Rs)}|{Next(s)}").Distinct().Count(),
            g.Where(s => s.Stages.ContainsKey(25)).Select(s => $"{s.Stages[25]}|{x.RsKey(s.Rs)}").Distinct().Count(),
            g.Where(s => s.Stages.ContainsKey(2)).Select(s => $"{s.Stages[2]}|{x.RsKey(s.Rs)}|{ExactLayouts.ExportShape(s)}").Distinct().Count(),
            x.Compute.Select(c => $"{c.Stages[6]}|{x.RsKey(c.Rs)}").Distinct().Count());
    }
}
