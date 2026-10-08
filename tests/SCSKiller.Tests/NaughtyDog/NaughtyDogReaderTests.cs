using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.NaughtyDog;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.NaughtyDog;

/// <summary>The Naughty Dog reader on a synthetic install laid out as The Last of Us Part II's: PSARC archives (one wrapped
/// in DirectStorage blocks) of packages and single-shader files holding ndshader records, with shaders compiled here
/// (<see cref="Hlsl"/>).</summary>
public class NaughtyDogReaderTests
{
    const string Rs = "RootFlags(ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT), CBV(b0), DescriptorTable(SRV(t0)), StaticSampler(s0)";

    sealed class Fixture
    {
        public readonly string Dir = Ff7.TempDir("naughtydog-synthetic");
        public readonly byte[] Vs = Hlsl.Vs(1, null), Ps = Hlsl.Ps(1, null), Cs = Hlsl.Cs(1), GlobalVs = Hlsl.Vs(2, null), GlobalPs = Hlsl.Ps(2, null),
            Hidden = Hlsl.Ps(3, null), RootSig = Hlsl.RootSignature(Rs), RootSigCs = Hlsl.RootSignature("DescriptorTable(UAV(u0))");
        public Game Game => new("test:naughtydog", "naughtydog", Store.Steam, Dir, Path.Combine(Dir, "tlou-ii.exe"));
        public string Main => Path.Combine(Dir, @"build\pc\main");

        public Fixture()
        {
            Directory.CreateDirectory(Main);
            var noise = RandomNumberGenerator.GetBytes(70_000);   // incompressible: the package's blocks are stored whole and packed
            byte[] level = [.. noise, .. Record(0, Vs, null), .. "ndshader"u8, .. new byte[8], .. Record(1, Ps, RootSig), .. new byte[100_000], .. Record(2, Cs, RootSigCs)];
            File.WriteAllBytes(Path.Combine(Main, "world-test.psarc"), Dsar(Psarc(
                ("pak68/level.pak", level), ("texturedict3/dict.pak", Record(1, Hidden, RootSig)), ("sfx1/a.xvag", noise[..1000]))));
            File.WriteAllBytes(Path.Combine(Main, "shaders.psarc"), Psarc(
                ("shaders/bytecode/VS_Global.vxo", Record(0, GlobalVs, null)), ("shaders/bytecode/PS_Global.pxo", Record(1, GlobalPs, RootSig))));
        }
    }

    static readonly Lazy<Fixture> Data = new(() => new Fixture());
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>An ndshader record: the header (magic, version 3, flags: header size | stage &lt;&lt; 8 | has a root signature
    /// &lt;&lt; 16, the engine's hashes, both sizes), the shader, its root signature.</summary>
    internal static byte[] Record(int stage, byte[] shader, byte[]? rs)
    {
        var h = new byte[40];
        "ndshader"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), 3);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), 0x28 | stage << 8 | (rs != null ? 1 << 16 : 0));
        RandomNumberGenerator.Fill(h.AsSpan(16, 16));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(32), shader.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(36), rs?.Length ?? 0);
        return [.. h, .. shader, .. rs ?? []];
    }

    const int BlockSize = 65536;

    /// <summary>A zlib PSARC as Naughty Dog's: 64 KiB blocks, each stored whole unless zlib makes it smaller, a manifest first.</summary>
    internal static byte[] Psarc(params (string Name, byte[] Data)[] files)
    {
        var entries = files.Prepend(("", Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => f.Name))))).ToList();
        var sizes = new List<int>();
        var data = new MemoryStream();
        var toc = new List<(byte[] Md5, int Block, long Size, long Offset)>();
        foreach (var (name, bytes) in entries)
        {
            toc.Add((name == "" ? new byte[16] : MD5.HashData(Encoding.UTF8.GetBytes(name)), sizes.Count, bytes.Length, data.Length));
            for (var at = 0; at < bytes.Length; at += BlockSize)
            {
                var block = bytes.AsSpan(at, Math.Min(BlockSize, bytes.Length - at));
                var z = new MemoryStream();
                using (var s = new ZLibStream(z, CompressionLevel.Optimal, true)) s.Write(block);
                var packed = z.Length < block.Length ? z.ToArray() : block.ToArray();
                sizes.Add(packed.Length == BlockSize ? 0 : packed.Length);
                data.Write(packed);
            }
        }
        var tocLength = 32 + 30 * toc.Count + 2 * sizes.Count;
        var o = new MemoryStream();
        void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); o.Write(b); }
        void U40(long v) { o.WriteByte((byte)(v >> 32)); U32(v); }
        o.Write("PSAR"u8); U32(0x00010004); o.Write("zlib"u8); U32(tocLength); U32(30); U32(toc.Count); U32(BlockSize); U32(0);
        foreach (var (md5, block, size, offset) in toc) { o.Write(md5); U32(block); U40(size); U40(offset + tocLength); }
        foreach (var z in sizes) { o.WriteByte((byte)(z >> 8)); o.WriteByte((byte)z); }
        data.Position = 0;
        data.CopyTo(o);
        return o.ToArray();
    }

    /// <summary>The PSARC wrapped in DirectStorage blocks: the first stored, the rest LZ4.</summary>
    internal static byte[] Dsar(byte[] psarc, int block = 40_000)
    {
        var blocks = new List<(long At, byte[] Stored, int Size, byte Codec)>();
        for (var at = 0; at < psarc.Length; at += block)
        {
            var b = psarc.AsSpan(at, Math.Min(block, psarc.Length - at));
            if (at == 0) { blocks.Add((at, b.ToArray(), b.Length, 0)); continue; }
            var lz = new byte[LZ4Codec.MaximumOutputSize(b.Length)];
            blocks.Add((at, lz[..LZ4Codec.Encode(b, lz)], b.Length, 3));
        }
        var head = 32 + 32 * blocks.Count;
        var o = new BinaryWriter(new MemoryStream());
        o.Write("DSAR"u8); o.Write(0x10003); o.Write(blocks.Count); o.Write(head); o.Write((long)psarc.Length); o.Write("PADDING*"u8);
        long pos = head;
        foreach (var (at, stored, size, codec) in blocks)
        {
            o.Write(at); o.Write(pos); o.Write(size); o.Write(stored.Length); o.Write(codec); o.Write(new byte[7]);
            pos += stored.Length;
        }
        foreach (var b in blocks) o.Write(b.Stored);
        return ((MemoryStream)o.BaseStream).ToArray();
    }

    [Fact]
    public void ReadsPsarcEntriesThroughTheirDirectStorageWrapper()
    {
        var d = Data.Value;
        using var p = new Psarc(Path.Combine(d.Main, "world-test.psarc"));
        Assert.Equal(["pak68/level.pak", "texturedict3/dict.pak", "sfx1/a.xvag"], p.Entries.Select(e => e.Name));
        var level = p.Read(p.Entries[0]);
        Assert.Equal(70_000 + 3 * 40 + 8 + 8 + 100_000 + d.Vs.Length + d.Ps.Length + d.Cs.Length + d.RootSig.Length + d.RootSigCs.Length, level.Length);
        var buffer = new byte[1];
        Assert.Equal(level.Length, p.Read(p.Entries[0], ref buffer));
        Assert.Equal(level, buffer[..level.Length]);
        Assert.False(Core.NaughtyDog.Psarc.Is(Path.Combine(d.Dir, "tlou-ii.exe")));
        Assert.True(Core.NaughtyDog.Psarc.Is(Path.Combine(d.Main, "shaders.psarc")));
    }

    [Fact]
    public void FindsEachRecordAndSkipsDamagedOnes()
    {
        var d = Data.Value;
        byte[] b = [.. Record(0, d.Vs, null), .. "ndshader"u8, .. new byte[50], .. Record(1, d.Ps, d.RootSig)];
        var recs = NaughtyDogReader.Records(b, b.Length).ToList();
        Assert.Equal([(40, d.Vs.Length, 0), (40 + d.Vs.Length + 58 + 40, d.Ps.Length, d.RootSig.Length)], recs.Select(r => (r.Shader, r.ShaderSize, r.RsSize)));
        Assert.Empty(NaughtyDogReader.Records(b, b.Length - 1).Skip(1));   // the last record cut short
        var bad = Record(1, d.Ps, d.RootSig);
        bad[^d.RootSig.Length]++;   // not a root signature: the shader stays, without one
        Assert.Equal(0, Assert.Single(NaughtyDogReader.Records(bad, bad.Length)).RsSize);
    }

    [Fact]
    public void IndexesPackagesAsPoolsAndPlansWithoutARecording()
    {
        var d = Data.Value;
        var reader = new NaughtyDogReader();
        var engine = reader.Detect(d.Game)!;
        Assert.Equal(new EngineInfo(NaughtyDogReader.Family, "DXIL+RTS0", null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(d.Game, engine, null, Ff7.Nvidia));
        Assert.Null(reader.Detect(d.Game with { InstallDir = Path.Combine(d.Dir, "missing") }));

        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        Assert.Equal(new[] { d.Vs, d.Ps, d.Cs, d.GlobalVs, d.GlobalPs }.Select(Sha).Order(), index.Shaders.Keys.Order());   // not the texture dictionary's
        Assert.Null(index.Shaders[Sha(d.Vs)].RootSignature);
        Assert.Equal(Sha(d.RootSig), index.Shaders[Sha(d.Ps)].RootSignature);
        Assert.Equal(Sha(d.RootSigCs), index.Shaders[Sha(d.Cs)].RootSignature);
        string Pool(params byte[][] s) => string.Join(',', s.Select(Sha).Order(StringComparer.Ordinal));
        Assert.Equal(new Dictionary<string, string>
        {
            [@"build\pc\main\shaders.psarc"] = Pool(d.GlobalVs, d.GlobalPs),                    // the engine's single-shader files: one pool
            [@"build\pc\main\world-test.psarc:pak68/level.pak"] = Pool(d.Vs, d.Ps, d.Cs),      // a package: its own
        }, index.Maps.ToDictionary(m => m.Library, m => string.Join(',', m.Shaders.Order(StringComparer.Ordinal))));
        Assert.DoesNotContain(index.Maps, m => m.IsPipeline);

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(d.Game, engine, new HashSet<string> { Sha(d.Ps), Sha(d.GlobalVs), Sha(d.RootSig), new('0', 40) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(3, got.Count);
        Assert.Equal(d.Ps, got[Sha(d.Ps)]);
        Assert.Equal(d.GlobalVs, got[Sha(d.GlobalVs)]);
        Assert.Equal(d.RootSig, got[Sha(d.RootSig)]);

        var dir = Ff7.TempDir("naughtydog-plan");
        var planner = new Planner();
        var plan = planner.Build(d.Game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, dir, null, CancellationToken.None);
        Assert.Equal((0L, 3L), (plan.Stats.Uncovered, plan.Stats.Generated));   // VS+PS of the level, of the engine, the CS
        planner.Materialize(plan, d.Game, engine, reader, null, Path.Combine(dir, "work"), CancellationToken.None);
        Ff7.CheckWarmReady(Path.Combine(dir, "work"));
    }

    /// <summary>The Last of Us Part II ships tlou-ii.exe and, bigger, tlou-ii-l.exe ("rtm legacy config"): the plain one is the
    /// game launcher.exe starts.</summary>
    [Fact]
    public void PicksThePlainExeOverItsLegacyTwin()
    {
        var dir = Ff7.TempDir("naughtydog-exe");
        File.WriteAllBytes(Path.Combine(dir, "tlou-ii.exe"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(dir, "tlou-ii-l.exe"), new byte[8192]);
        Assert.Equal(Path.Combine(dir, "tlou-ii.exe"), GameFiles.FindExe(dir));
    }
}
