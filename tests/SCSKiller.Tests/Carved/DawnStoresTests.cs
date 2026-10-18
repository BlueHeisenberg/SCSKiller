using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Carved;

/// <summary>Dawn's bin\raw*.store2 (<see cref="DawnStores"/>) on a synthetic install of real compiled shaders that carry no
/// root signature of their own: the carver plans each pipeline record with its record's root signature, without a recording.</summary>
public class DawnStoresTests
{
    const int Pairs = 130; // > CarvedReader.MinGraphics graphics shaders in total
    const uint R1 = 7, R2 = 9, R3 = 11, R4 = 13, Cs1 = 9001, Cs2 = 9002;
    static uint VsId(int i) => 100 + 2 * (uint)i;
    static uint PsId(int i) => 101 + 2 * (uint)i;

    sealed class Fixture
    {
        public readonly string Dir = Ff7.TempDir("dawn-synthetic");
        public readonly Dictionary<uint, byte[]> Shaders = [];
        public readonly Dictionary<uint, byte[]> Roots = new()
        {
            [R1] = Hlsl.RootSignature(Hlsl.Rs1), [R2] = Hlsl.RootSignature(Hlsl.Rs2),
            [R3] = Hlsl.RootSignature("DescriptorTable(UAV(u0))"), [R4] = Hlsl.RootSignature("CBV(b0), DescriptorTable(UAV(u0))"),   // compute: u0
        };
        /// <summary>Root id, VS or CS id, PS id, and an id where a GS would sit.</summary>
        public readonly List<(uint Root, uint First, uint Ps, uint Gs)> Records = [];
        public Game Game => new("test:dawn", "dawn", Store.Other, Dir, Path.Combine(Dir, "game.exe"));

        public Fixture()
        {
            for (var i = 0; i < Pairs; i++)
            {
                (Shaders[VsId(i)], Shaders[PsId(i)]) = (Hlsl.Vs(i + 1, null), Hlsl.Ps(i + 1, null));
                Records.Add((i % 2 == 0 ? R1 : R2, VsId(i), PsId(i), 0));
            }
            (Shaders[Cs1], Shaders[Cs2]) = (Hlsl.Cs(1), Hlsl.Cs(2));
            Records.AddRange([
                (R2, VsId(0), PsId(1), 0),        // VS 0 under both root signatures: its PS gives each record its own
                (R1, VsId(2), PsId(2), 0),        // a second record of one pipeline (another state)
                (R2, VsId(3), 0, 0),              // a depth pass
                (R3, Cs1, 0, 0),
                (R3, Cs2, 0, 0), (R4, Cs2, 0, 0), // a lone shader under two root signatures: a PSO under each
                (R2, VsId(5), PsId(5), VsId(4)),  // a shader where a GS would be: not read
            ]);
            var bin = Directory.CreateDirectory(Path.Combine(Dir, "bin")).FullName;
            File.WriteAllBytes(Path.Combine(bin, "rawshader.store2"), Entries(Shaders));
            File.WriteAllBytes(Path.Combine(bin, "rawroot.store2"), Entries(Roots));
            File.WriteAllBytes(Path.Combine(bin, "rawpso.store2"), Pso(Records));
        }

        static byte[] Entries(Dictionary<uint, byte[]> entries)
        {
            using var m = new MemoryStream();
            using var w = new BinaryWriter(m);
            w.Write((uint)entries.Count);
            foreach (var (id, c) in entries) { w.Write(id); w.Write((uint)c.Length); w.Write(0u); w.Write(c); }
            return Framed(m.ToArray());
        }

        static byte[] Pso(List<(uint Root, uint First, uint Ps, uint Gs)> records)
        {
            using var m = new MemoryStream();
            using var w = new BinaryWriter(m);
            w.Write((uint)records.Count);
            foreach (var (r, i) in records.Select((r, i) => (r, i)))
            {
                var b = new byte[680];
                foreach (var (at, v) in new[] { (0, (uint)i + 1), (4, 0x40000 + (uint)i), (8, 0x298u), (16, r.Root), (24, r.First), (40, r.Ps), (88, r.Gs), (312, 0xFFFFFFFF) })
                    BitConverter.TryWriteBytes(b.AsSpan(at), v);
                w.Write(b);
            }
            return Framed(m.ToArray());
        }

        /// <summary>The game's .store2 framing: [u64 a][u64 b = the payload's length], the payload, a again; a = b + 0x10007.</summary>
        public static byte[] Framed(byte[] payload)
        {
            var a = BitConverter.GetBytes((ulong)payload.Length + 0x10007);
            return [.. a, .. BitConverter.GetBytes((ulong)payload.Length), .. payload, .. a];
        }
    }

    static readonly Lazy<Fixture> Data = new(() => new Fixture());
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    [Fact]
    public void PlansEachRecordWithItsRootSignatureWithoutARecording()
    {
        var d = Data.Value;
        string S(uint id) => Sha(d.Shaders[id]);
        var (rs1, rs2) = (Sha(d.Roots[R1]), Sha(d.Roots[R2]));
        var stores = DawnStores.Read(d.Dir)!;
        Assert.Equal((d.Records.Count - 1, 1), (stores.Pipelines.Count, stores.Skipped));

        var reader = new CarvedReader();
        var engine = reader.Detect(d.Game)!;
        Assert.Equal("DXBC" + CarvedReader.EmbeddedRootSignatures, engine.Version);
        var planner = new Planner();
        var check = planner.Check(d.Game, engine, null, Ff7.Nvidia);
        Assert.Equal(Readiness.Ready, check.Readiness);
        Assert.StartsWith(Planner.NoRecording, check.Reason);

        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        Assert.Equal(2 * Pairs + 2, index.Shaders.Count);
        Assert.Null(index.Shaders[S(VsId(0))].RootSignature);
        Assert.Null(index.Shaders[S(Cs2)].RootSignature);
        Assert.Equal((rs1, rs2, Sha(d.Roots[R3])), (index.Shaders[S(PsId(0))].RootSignature, index.Shaders[S(PsId(1))].RootSignature, index.Shaders[S(Cs1)].RootSignature));
        for (var i = 1; i < Pairs; i++) Assert.Equal(i % 2 == 0 ? rs1 : rs2, index.Shaders[S(VsId(i))].RootSignature);
        Assert.All(index.Shaders.Values.Select(s => s.RootSignature).OfType<string>(), h => Assert.Contains(h, index.RootSignatures!.Keys));   // the planner checks coverage with them
        Assert.All(index.Maps, m => Assert.True(m.IsPipeline));   // the records replace the store's pool
        Assert.Equal(Pairs + 5, index.Maps.Count);

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(d.Game, engine, new HashSet<string> { rs1, rs2 }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(d.Roots[R1], got[rs1]);
        Assert.Equal(d.Roots[R2], got[rs2]);

        var dir = Ff7.TempDir("dawn-plan");
        var plan = planner.Build(d.Game, engine, index, null, Ff7.Nvidia, dir, null, CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.DoesNotContain(body, r => r.Tag == 'B');
        var psos = body.Where(r => r.Tag == 'S').Select(r => (PsoDb.Parse(r).Rs, PsoDb.Parse(r).Stages))
            .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => (i.Rs, i.Stages))).ToList();
        var want = d.Records.Where(r => r.Gs == 0).Select(r => (Stages: string.Join(',', new[] { r.First, r.Ps }.Where(x => x != 0).Select(S)), Rs: Sha(d.Roots[r.Root])))
            .Distinct().Order();
        Assert.Equal(want, psos.Select(p => (string.Join(',', p.Stages.Values), p.Rs)).Order());
        // each stage's resources are in its pipeline's root signature (the runtime's check: E_INVALIDARG otherwise)
        var rootOf = d.Roots.Values.ToDictionary(Sha, b => RootSig.Parse(b));
        foreach (var (rs, st) in psos)
            foreach (var (stage, h) in st) Assert.Null(RootSig.Uncovered(rootOf[rs], (Stage)stage, index.Shaders[h]));
        Assert.Equal((Pairs + 5L, 0L, 4L, true), (plan.Stats.StageSets, plan.Stats.LeftOut, plan.Stats.RootSignatures, plan.Stats.RootSigRuleVerified));

        var work = Path.Combine(dir, "work");
        planner.Materialize(plan, d.Game, engine, reader, null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);
    }

    /// <summary>A rawpso.store2 that isn't whole records: not read, the store is carved as any other shader pack.</summary>
    [Fact]
    public void AnUnknownLayoutIsCarvedAsBefore()
    {
        var (d, dir, bin) = Copy("dawn-other");
        var pso = Path.Combine(bin, "rawpso.store2");
        var bytes = File.ReadAllBytes(pso);
        File.WriteAllBytes(pso, bytes[..^8]);   // no trailer: the layout as first assumed
        Assert.Null(DawnStores.Read(dir));
        File.WriteAllBytes(pso, Fixture.Framed([.. bytes[16..^8], 0]));   // framed, but not whole records
        Assert.Null(DawnStores.Read(dir));
        var game = d.Game with { InstallDir = dir };
        var reader = new CarvedReader();
        var engine = reader.Detect(game)!;
        Assert.Equal("DXBC", engine.Version);
        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        Assert.Equal(Path.Combine("bin", "rawshader.store2"), Assert.Single(index.Maps).Library);
    }

    /// <summary>A well-framed root store whose entry claims 2 GB: rejected before anything is allocated, and the install is
    /// carved as before.</summary>
    [Fact]
    public void AnEntryLargerThanItsPayloadIsRejected()
    {
        var (d, dir, bin) = Copy("dawn-huge-entry");
        File.WriteAllBytes(Path.Combine(bin, "rawroot.store2"), Fixture.Framed([.. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(R1), .. BitConverter.GetBytes(0x80000000u), .. new byte[4], .. d.Roots[R1]]));
        AssertCarvedAsBefore(d.Game with { InstallDir = dir });
    }

    /// <summary>A rawpso.store2 is checked by its length and framing before it is read: past the size cap, or a count its
    /// length can't hold, it isn't read at all.</summary>
    [Fact]
    public void ARawpsoIsCheckedBeforeItIsRead()
    {
        var (d, dir, bin) = Copy("dawn-huge-pso");
        var pso = Path.Combine(bin, "rawpso.store2");
        File.WriteAllBytes(pso, Fixture.Framed([.. BitConverter.GetBytes(uint.MaxValue), .. new byte[680]]));
        AssertCarvedAsBefore(d.Game with { InstallDir = dir });
        using (var f = File.OpenWrite(pso)) f.SetLength((256L << 20) + 1);
        Assert.Null(DawnStores.Read(dir));
    }

    static (Fixture Data, string Dir, string Bin) Copy(string name)
    {
        var d = Data.Value;
        var dir = Ff7.TempDir(name);
        var bin = Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName;
        foreach (var f in Directory.GetFiles(Path.Combine(d.Dir, "bin"))) File.Copy(f, Path.Combine(bin, Path.GetFileName(f)));
        return (d, dir, bin);
    }

    static void AssertCarvedAsBefore(Game game)
    {
        Assert.Null(DawnStores.Read(game.InstallDir));
        var reader = new CarvedReader();
        var engine = reader.Detect(game)!;
        Assert.Equal("DXBC", engine.Version);
        Assert.All(reader.Index(game, engine, null, CancellationToken.None).Shaders.Values, s => Assert.Null(s.RootSignature));
    }
}
