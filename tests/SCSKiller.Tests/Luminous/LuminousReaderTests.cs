using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Luminous;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Dagor;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Luminous;

/// <summary>A synthetic FINAL FANTASY XV install: datas\**\*.earc archives of .sb entries (protected, compressed or stored),
/// and DXBC carved from the exe and a DLL beside it.</summary>
public class LuminousReaderTests
{
    static readonly byte[] Vs = DagorReaderTests.Vs, Ps = DagorReaderTests.Ps, Cs = DagorReaderTests.Cs, Gs = DagorReaderTests.Gs;
    static readonly byte[] Ps2 = Hlsl.Compile("float4 main(float4 p : SV_Position) : SV_Target { return p * 2; }", "main", "ps_5_0");

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    public sealed record File1(string Uri, byte[] Data, bool Compress = true, ushort Key = 0);

    /// <summary>An archive laid out as the game's: header, entry table, URIs, a path list, then each entry's data at a 0x200
    /// boundary. Protected: sizes and offsets masked by the archive hash chain, written here independently of the reader.
    /// A compressed entry is zlib chunks of <paramref name="chunkKb"/> KiB, its first chunk header masked when its key isn't 0.</summary>
    public static byte[] Archive(IReadOnlyList<File1> files, bool protect = true, int chunkKb = 128, uint version = Earc.Version, ulong archiveHash = 0x0123456789ABCDEF)
    {
        var uriList = 0x40 + files.Count * 0x28;
        var uris = new MemoryStream();
        var uriAt = new List<int>();
        foreach (var f in files)
        {
            uriAt.Add(uriList + (int)uris.Length);
            uris.Write(Encoding.UTF8.GetBytes(f.Uri));
            uris.WriteByte(0);
        }
        var pathList = uriList + (int)uris.Length;
        var data = new MemoryStream();
        var dataStart = Align(pathList + 16, 0x200);
        var placed = new List<(long Offset, int Stored)>();
        foreach (var f in files)
        {
            data.SetLength(Align((int)data.Length, 0x200));
            data.Position = data.Length;
            var at = data.Length;
            if (!f.Compress) data.Write(f.Data);
            else
                for (var p = 0; p < f.Data.Length; p += chunkKb * 1024)
                {
                    var chunk = f.Data.AsSpan(p, Math.Min(chunkKb * 1024, f.Data.Length - p));
                    var z = new MemoryStream();
                    using (var w = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) w.Write(chunk);
                    uint c = (uint)z.Length, d = (uint)chunk.Length;
                    if (p == 0 && f.Key != 0)
                    {
                        ulong k = f.Key;
                        for (var i = 0; i < 2; i++) k = k * 1103515245 + 12345;
                        c ^= (uint)(k >> 32);
                        d ^= (uint)k;
                    }
                    data.Write(BitConverter.GetBytes(c));
                    data.Write(BitConverter.GetBytes(d));
                    data.Write(z.ToArray());
                    data.SetLength(Align((int)data.Length, 4));
                    data.Position = data.Length;
                }
            placed.Add((dataStart + at, (int)(data.Length - at)));
        }

        var b = new byte[dataStart + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Earc.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), version | (protect ? 0x80000000 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)files.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), (uint)uriList);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), (uint)pathList);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), (uint)dataStart);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), (uint)chunkKb);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40), archiveHash);
        ulong sub = 0xCBF29CE484222325 ^ archiveHash;
        for (var i = 0; i < files.Count; i++)
        {
            var e = b.AsSpan(0x40 + i * 0x28, 0x28);
            var hash = 0x9E3779B97F4A7C15UL * (ulong)(i + 1);
            uint size = (uint)files[i].Data.Length, stored = (uint)placed[i].Stored;
            var offset = (ulong)placed[i].Offset;
            if (protect)
            {
                var k = sub * 0x100000001B3 ^ hash;
                size ^= (uint)(k >> 32);
                stored ^= (uint)k;
                sub = k * 0x100000001B3 ^ ~hash;
                offset ^= sub;
            }
            BinaryPrimitives.WriteUInt64LittleEndian(e, hash);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], size);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], stored);
            BinaryPrimitives.WriteUInt32LittleEndian(e[16..], files[i].Compress ? 2u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(e[20..], (uint)uriAt[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(e[24..], offset);
            BinaryPrimitives.WriteUInt32LittleEndian(e[32..], (uint)pathList);
            BinaryPrimitives.WriteUInt16LittleEndian(e[38..], files[i].Key);
        }
        uris.ToArray().CopyTo(b, uriList);
        data.ToArray().CopyTo(b, dataStart);
        return b;
    }

    static int Align(int n, int a) => (n + a - 1) / a * a;

    static Game Install(string name, Dictionary<string, byte[]> archives, byte[]? exe = null, byte[]? dll = null)
    {
        var dir = Ff7.TempDir(name);
        foreach (var (rel, bytes) in archives)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        File.WriteAllBytes(Path.Combine(dir, LuminousReader.Exe), exe ?? [0]);
        if (dll != null) File.WriteAllBytes(Path.Combine(dir, "NvHairWorksDx11.win64.dll"), dll);
        return new Game("steam:637650", "FINAL FANTASY XV WINDOWS EDITION", Store.Steam, dir, Path.Combine(dir, LuminousReader.Exe));
    }

    const string Shadergen = @"datas\shader\shadergen\autoexternal.earc";

    /// <summary>The first two entries of a protected archive the game ships, as stored, and what they are: the entry keys chain
    /// from the archive hash through every entry before.</summary>
    [Fact]
    public void UnmasksTheGamesEntriesWithTheArchiveHashChain()
    {
        var mask = new Earc.Mask(0x493877626B194B76);
        uint size = 0x6AB0B580, stored = 0xED844155;
        ulong offset = 0x21811C4C043056B4;
        mask.Apply(0x108670C46C760580, ref size, ref stored, ref offset);
        Assert.Equal((19468u, 476u, 0x400ul), (size, stored, offset));
        size = 0x0E33F8B4;
        stored = 0x2FAB21AE;
        offset = 0x2385B094318FCCC7;
        mask.Apply(0x10867D9331B7A7C2, ref size, ref stored, ref offset);
        Assert.Equal((19468u, 432u, 0x600ul), (size, stored, offset));
    }

    /// <summary>A shipped entry with key 52684 stores its first chunk header as these two words; its zlib stream is 322 bytes
    /// and it decompresses to 628.</summary>
    [Fact]
    public void UnmasksTheGamesFirstChunkHeaderWithItsKey()
    {
        var k = Earc.ChunkKey(52684);
        Assert.Equal(322u, 0xE3890F59 ^ (uint)(k >> 32));
        Assert.Equal(628u, 0x2315355E ^ (uint)k);
    }

    [Theory]
    [InlineData(true, 777)]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    public void ReadsCompressedStoredAndMultiChunkEntries(bool protect, int key)
    {
        var big = new byte[3000];
        new Random(20).NextBytes(big.AsSpan(0, 1000));
        var archive = Archive([new("data://a/big.sb", big, Key: (ushort)key), new("data://a/x.txt", [1, 2, 3]), new("data://a/plain.sb", Ps, Compress: false)],
            protect, chunkKb: 1);
        var path = Path.Combine(Ff7.TempDir("luminous-earc"), "a.earc");
        File.WriteAllBytes(path, archive);
        using var h = File.OpenHandle(path);
        var entries = Earc.Entries(h, ".sb")!;
        Assert.Equal(["data://a/big.sb", "data://a/plain.sb"], entries.Select(e => e.Uri));
        Assert.Equal(big, Earc.Read(h, entries[0]));
        Assert.Equal(Ps, Earc.Read(h, entries[1]));
        Assert.Equal(Earc.Version, Earc.VersionOf(path));
    }

    [Fact]
    public void ACutArchiveFailsWithInvalidData()
    {
        var archive = Archive([new("data://a/b.sb", Ps)]);
        var path = Path.Combine(Ff7.TempDir("luminous-cut"), "a.earc");
        File.WriteAllBytes(path, archive[..^64]);
        using var h = File.OpenHandle(path);
        Assert.Throws<InvalidDataException>(() => Earc.Entries(h, ".sb"));
    }

    [Fact]
    public void AChunkThatDecompressesPastItsLengthFails()
    {
        var archive = Archive([new("data://a/b.sb", Ps)], protect: false);
        var offset = (int)BinaryPrimitives.ReadUInt64LittleEndian(archive.AsSpan(0x40 + 24));
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0x40 + 8), (uint)Ps.Length - 1);   // entry size
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(offset + 4), (uint)Ps.Length - 1);  // the chunk's decompressed length
        var path = Path.Combine(Ff7.TempDir("luminous-long-chunk"), "a.earc");
        File.WriteAllBytes(path, archive);
        using var h = File.OpenHandle(path);
        var e = Assert.Single(Earc.Entries(h, ".sb")!);
        Assert.Throws<InvalidDataException>(() => Earc.Read(h, e));
    }

    [Fact]
    public void AUriLongerThanAnyTheGameUsesIsSkipped()
    {
        var archive = Archive([new("data://" + new string('a', 1100) + ".sb", Ps), new("data://ok.sb", Vs)]);
        var path = Path.Combine(Ff7.TempDir("luminous-long-uri"), "a.earc");
        File.WriteAllBytes(path, archive);
        using var h = File.OpenHandle(path);
        Assert.Equal("data://ok.sb", Assert.Single(Earc.Entries(h, ".sb")!).Uri);
    }

    [Fact]
    public void IndexesEveryArchiveAndTheShadersOfTheExeAndDlls()
    {
        byte[] Junk(int n) => Enumerable.Repeat((byte)0xCD, n).ToArray();
        var game = Install("luminous-index", new()
        {
            [Shadergen] = Archive([new("data://shader/shadergen/v/a.vs.sb", Vs, Key: 4242), new("data://shader/shadergen/p/b.ps.sb", Ps, Compress: false),
                new("data://shader/shadergen/b.sbmdl", Ps2)]),
            // a level archive carries a copy of shadergen's VS and a PS of its own
            [@"datas\level\town\town.earc"] = Archive([new("data://shader/shadergen/v/a.vs.sb", Vs, Key: 99), new("data://shader/shadergen/p/c.ps.sb", Ps2)],
                archiveHash: 0xFEDCBA9876543210),
            [@"datas\level\town\other.earc"] = Archive([new("data://x.sb", Ps)], version: 0x30013),
        }, exe: [.. Junk(5000), .. Cs, .. Junk(77)], dll: [.. Junk(10), .. Gs]);

        var reader = new LuminousReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(LuminousReader.Family, LuminousReader.Version, null, "D3D11", false, null), engine);
        var log = new List<string>();
        var index = reader.Index(game, engine, new SyncLog(log), CancellationToken.None);
        Assert.Equal(new[] { Vs, Ps, Ps2, Cs, Gs }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.Equal([LuminousReader.Platform], index.Platforms);
        var maps = index.Maps.ToDictionary(m => m.Library, m => m.Shaders.Order().ToList());
        Assert.Equal(new[] { Vs, Ps }.Select(Sha).Order(), maps[Shadergen]);
        Assert.Equal(new[] { Vs, Ps2 }.Select(Sha).Order(), maps[@"datas\level\town\town.earc"]);
        Assert.Equal(Sha(Cs), Assert.Single(maps[LuminousReader.Exe]));
        Assert.Equal(Sha(Gs), Assert.Single(maps["NvHairWorksDx11.win64.dll"]));
        Assert.Equal(4, maps.Count);
        Assert.Contains("1 archives of another version left out", log.Single());

        var got = new Dictionary<string, byte[]>();
        new LuminousReader().ReadShaders(game, engine, new HashSet<string> { Sha(Vs), Sha(Ps2), Sha(Cs), Sha(Gs), new('0', 40) },
            (h, b) => got.Add(h, b), CancellationToken.None);   // re-indexes
        Assert.Equal(Vs, got[Sha(Vs)]);
        Assert.Equal(Ps2, got[Sha(Ps2)]);
        Assert.Equal(Cs, got[Sha(Cs)]);
        Assert.Equal(Gs, got[Sha(Gs)]);
        Assert.Equal(4, got.Count);

        Assert.Equal(new PlanCheck(Readiness.Ready, "compiles every DirectX 11 shader"), new Planner().Check(game, engine, null, Ff7.Nvidia));
        Assert.Equal(new PlanCheck(Readiness.Ready, $"compiles every DirectX 11 shader but the vertex shaders; {Planner.D3D11VsRecord}"), new Planner().Check(game, engine, null, Ff7.Amd));
    }

    [Fact]
    public void AnArchiveThatDoesntReadIsLeftOutWithAWarning()
    {
        var cut = Archive([new("data://a.sb", Vs)]);
        var game = Install("luminous-cut-index", new() { [Shadergen] = Archive([new("data://b.sb", Ps)]), [@"datas\level\cut.earc"] = cut[..^64] });
        var reader = new LuminousReader();
        var log = new List<string>();
        var index = reader.Index(game, reader.Detect(game)!, new SyncLog(log), CancellationToken.None);
        Assert.Equal(Sha(Ps), Assert.Single(index.Shaders.Keys));
        Assert.StartsWith(@"warning: datas\level\cut.earc: ", log[0]);
    }

    [Fact]
    public void CarvesContainersAcrossReadWindowsOnce()
    {
        Assert.True(Cs.Length <= 2048 && Gs.Length <= 2048);
        var b = new byte[20000];
        var at = new[] { 0, 4096 - 3, 7000, 10240 - 100, 16000 };   // across a window's end, in an overlap, in the last window's overlap
        foreach (var (o, i) in at.Select((x, n) => (x, n))) (i % 2 == 0 ? Cs : Gs).CopyTo(b, o);
        var path = Path.Combine(Ff7.TempDir("luminous-carve"), "ffxv_s.exe");
        File.WriteAllBytes(path, b);
        var found = new List<long>();
        new LuminousReader { Window = 4096, Overlap = 2048 }.Carve(path, (o, c) => { found.Add(o); Assert.True(c.SequenceEqual(Cs) || c.SequenceEqual(Gs)); }, CancellationToken.None);
        Assert.Equal(at.Select(o => (long)o), found);
    }

    [Fact]
    public void OtherVersionsAndOtherGamesAreNotRead()
    {
        Assert.Null(new LuminousReader().Detect(new Game("test:none", "None", Store.Other, Ff7.TempDir("luminous-none"), "x.exe")));
        var v19 = Install("luminous-v19", new() { [Shadergen] = Archive([new("data://a.sb", Ps)], version: 0x30013) });
        Assert.Equal(new EngineInfo(LuminousReader.Family, "EARC 3.19", null, "D3D11", false, "an archive version SCSKiller doesn't read"), new LuminousReader().Detect(v19));
        var other = Install("luminous-other", new() { [Shadergen] = Archive([new("data://a.sb", Ps)]) });
        File.Delete(Path.Combine(other.InstallDir, LuminousReader.Exe));
        Assert.Equal("a Luminous game SCSKiller doesn't read", new LuminousReader().Detect(other)!.Unsupported);
    }

    sealed class SyncLog(List<string> lines) : IProgress<string> { public void Report(string value) => lines.Add(value); }
}
