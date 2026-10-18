using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.ForzaTech;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Northlight;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.ForzaTech;

/// <summary>A synthetic ForzaTech install: media\ShadersDXIL.zip and material zips of per-scenario .pcdxil entries, compiled
/// here (<see cref="Hlsl"/>), with a zip that doesn't open and an entry of an unsupported compression method.</summary>
public class ForzaTechReaderTests
{
    static readonly byte[] Vs = Hlsl.Vs(1, Hlsl.Rs1), Ps = Hlsl.Ps(1, Hlsl.Rs1), Ps2 = Hlsl.Ps(2, Hlsl.Rs1), VsAlone = Hlsl.Vs(2, Hlsl.Rs1);
    static readonly byte[] Cs = Hlsl.Compile("""
        RWBuffer<float> u : register(u0);
        [RootSignature("DescriptorTable(UAV(u0))")]
        [numthreads(64, 1, 1)] void main(uint i : SV_DispatchThreadID) { u[i] = i; }
        """, "main", "cs_5_1");
    static readonly byte[] Lib = NorthlightReaderTests.Library((7, "RayGen", 0));

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    /// <summary>A zip of the entries, deflated unless <paramref name="stored"/>.</summary>
    static byte[] Zip(bool stored, params (string Name, byte[] Data)[] entries)
    {
        var s = new MemoryStream();
        using (var z = new ZipArchive(s, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries)
            {
                using var w = z.CreateEntry(name, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                w.Write(data);
            }
        return s.ToArray();
    }

    /// <summary>The zip with its named entry's compression method set to 22 (how the game encrypts some entries), in its local
    /// header and its central directory record.</summary>
    static byte[] Method22(byte[] zip, string name)
    {
        var n = Encoding.ASCII.GetBytes(name);
        foreach (var (magic, method, nameAt) in new[] { ("PK\u0003\u0004", 8, 30), ("PK\u0001\u0002", 10, 46) })
            for (var i = 0; zip.AsSpan(i).IndexOf(Encoding.ASCII.GetBytes(magic)) is var k and >= 0; i += k + 4)
                if (zip.AsSpan(i + k + nameAt).StartsWith(n)) BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(i + k + method), 22);
        return zip;
    }

    static (Game Game, string Dir) Install(string name, bool rts0 = true)
    {
        var dir = Ff7.TempDir($"forzatech-{name}");
        Directory.CreateDirectory(Path.Combine(dir, @"media\_library"));
        Directory.CreateDirectory(Path.Combine(dir, @"media\cars\_library\shaders"));
        File.WriteAllBytes(Path.Combine(dir, @"media\ShadersDXIL.zip"), Zip(false,
            ("Blit.vshader", rts0 ? Vs : Hlsl.Vs(1, null)), ("Blit.pshader", rts0 ? Ps2 : Hlsl.Ps(2, null)), ("BC1Compress.cshader", Cs)));
        return (new Game($"test:{name}", name, Store.Other, dir, Path.Combine(dir, "forzahorizon6.exe")), dir);
    }

    [Fact]
    public void DetectsTheLayoutAndWhetherItsShadersCarryTheirRootSignatures()
    {
        var (game, dir) = Install("detect");
        var engine = new ForzaTechReader().Detect(game);
        Assert.Equal(new EngineInfo(ForzaTechReader.Family, "pcdxil+RTS0", null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine!, null, Ff7.Nvidia));

        Assert.Equal("pcdxil", new ForzaTechReader().Detect(Install("detect-no-rts0", rts0: false).Game)!.Version);

        File.WriteAllBytes(Path.Combine(dir, @"media\ShadersDXIL.zip"), Zip(false, ("readme.txt", "x"u8.ToArray())));
        Assert.Equal($@"no shader in media\ShadersDXIL.zip", new ForzaTechReader().Detect(game)!.Unsupported);
        File.WriteAllBytes(Path.Combine(dir, @"media\ShadersDXIL.zip"), "not a zip"u8.ToArray());
        Assert.Equal($@"no shader in media\ShadersDXIL.zip", new ForzaTechReader().Detect(game)!.Unsupported);

        Directory.Delete(Path.Combine(dir, @"media\_library"));
        Assert.Null(new ForzaTechReader().Detect(game));
    }

    /// <summary>A .vso and the .pso of the same name are one pipeline; a VS or PS without its partner, a library and the engine's
    /// own VS and PS pool per zip; a compute shader is a pipeline alone. A zip that doesn't open and an entry of method 22 are
    /// left out with a warning, and the rest is read.</summary>
    [Fact]
    public void PairsByNameAndSkipsWhatDoesNotRead()
    {
        var (game, dir) = Install("pairs");
        var enc = Hlsl.Ps(9, Hlsl.Rs1);
        File.WriteAllBytes(Path.Combine(dir, @"media\_library\Shaders.zip"), Method22(Zip(true,
            ("glass/glassCarLightScenario.pcdxil.vso", Vs), ("glass/glassCarLightScenario.pcdxil.pso", Ps),
            ("glass/glassShadowDepthNoPSLightScenario.pcdxil.vso", VsAlone),
            ("glass/glassCarRayTracing_T10LightScenario.pcdxil.lso", Lib),
            ("glass/glassEncryptedLightScenario.pcdxil.pso", enc),
            ("glass/glass.shaderbin", "burG"u8.ToArray())), "glass/glassEncryptedLightScenario.pcdxil.pso"));
        File.WriteAllBytes(Path.Combine(dir, @"media\cars\_library\shaders\broken.zip"), [.. "PK\u0003\u0004"u8, .. new byte[64]]);
        var damaged = Zip(true, ("car_paintCarLightScenario.pcdxil.pso", Ps2));
        damaged[damaged.AsSpan().IndexOf("PK\u0001\u0002"u8) + 3] = 3;   // its central directory, which ZipArchive reads only when listed
        File.WriteAllBytes(Path.Combine(dir, @"media\cars\_library\shaders\damaged.zip"), damaged);
        var reader = new ForzaTechReader();
        var engine = reader.Detect(game)!;
        var log = new List<string>();
        var index = reader.Index(game, engine, new Log(log.Add), CancellationToken.None);

        Assert.Equal(new[] { Vs, Ps, Ps2, VsAlone, Cs, Lib }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.DoesNotContain(Sha(enc), index.Shaders.Keys);
        var pipelines = index.Maps.Where(m => m.IsPipeline).Select(m => string.Join(',', m.Shaders)).Order().ToList();
        Assert.Equal(new[] { $"{Sha(Vs)},{Sha(Ps)}", Sha(Cs) }.Order(), pipelines);
        var pools = index.Maps.Where(m => !m.IsPipeline).ToDictionary(m => m.Library, m => m.Shaders.Order().ToList());
        Assert.Equal(new[] { Sha(VsAlone), Sha(Lib) }.Order(), pools[@"media\_library\Shaders.zip"]);
        Assert.Equal(new[] { Sha(Vs), Sha(Ps2) }.Order(), pools[@"media\ShadersDXIL.zip"]);
        Assert.All(index.Shaders.Values.Where(s => s.Stage != Stage.Library), s => Assert.Contains(s.RootSignature!, index.RootSignatures!.Keys));
        Assert.Null(index.Shaders[Sha(Lib)].RootSignature);
        var line = Assert.Single(log);
        Assert.Contains("1 VS+PS pairs by name", line);
        Assert.Contains("1 ray tracing libraries without a root signature (they need a recording)", line);
        Assert.Contains("2 zips don't open (broken.zip)", line);
        Assert.Contains("1 shader entries don't read", line);

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, index.Shaders.Keys.ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(index.Shaders.Keys.Order(), got.Keys.Order());
        Assert.All(got, kv => Assert.Equal(kv.Key, Sha(kv.Value)));

        var again = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal(index.ContentHash, again.ContentHash);
        File.WriteAllBytes(Path.Combine(dir, @"media\cars\_library\shaders\broken.zip"), Zip(true, ("car_glassCarLightScenario.pcdxil.pso", Ps2)));
        Assert.NotEqual(index.ContentHash, reader.Index(game, engine, null, CancellationToken.None).ContentHash);   // a zip that reads now counts
    }

    /// <summary>An install whose zips hold no shader that reads has no index (never an empty one under a real-looking hash).</summary>
    [Fact]
    public void NoShaderIsNoIndex()
    {
        var (game, dir) = Install("none");
        var engine = new ForzaTechReader().Detect(game)!;
        File.WriteAllBytes(Path.Combine(dir, @"media\ShadersDXIL.zip"), Method22(Zip(true, ("Blit.pshader", Ps)), "Blit.pshader"));
        File.WriteAllBytes(Path.Combine(dir, @"media\_library\Shaders.zip"), "not a zip"u8.ToArray());
        var e = Assert.Throws<InvalidDataException>(() => new ForzaTechReader().Index(game, engine, null, CancellationToken.None));
        Assert.Contains("no shader in the zips of media", e.Message);
    }

    /// <summary>A zip declaring more entries than the cap is left out before its directory is read.</summary>
    [Fact]
    public void AZipDeclaringTooManyEntriesIsLeftOut()
    {
        var (game, _) = Install("huge");
        var reader = new ForzaTechReader { MaxZipEntries = 2 };   // ShadersDXIL.zip lists 3
        Assert.Equal(@"no shader in media\ShadersDXIL.zip", reader.Detect(game)!.Unsupported);
        var e = Assert.Throws<InvalidDataException>(() => reader.Index(game, new ForzaTechReader().Detect(game)!, null, CancellationToken.None));
        Assert.Contains("1 zips declare too many entries", e.Message);
        Assert.Equal(3, ForzaTechReader.DeclaredEntries(new MemoryStream(Zip(false, ("a", new byte[] { 1 }), ("b", new byte[] { 2 }), ("c", new byte[] { 3 })))));
        Assert.Equal(0, ForzaTechReader.DeclaredEntries(new MemoryStream(new byte[100])));

        // over 4 KiB stored: ZipArchive searches a smaller zip whole, and then even a signature in the comment breaks it
        var abc = Zip(true, ("a", new byte[5000]), ("b", new byte[] { 2 }), ("c", new byte[] { 3 }));
        byte[] commented = [.. abc, .. "PK\u0005\u0006"u8];   // a zip comment holding a signature, which ZipArchive skips
        BinaryPrimitives.WriteUInt16LittleEndian(commented.AsSpan(abc.Length - 2), 4);
        Assert.Equal(3, new ZipArchive(new MemoryStream(commented)).Entries.Count);
        Assert.Equal(3, ForzaTechReader.DeclaredEntries(new MemoryStream(commented)));

        var zip64 = Zip64(abc);
        Assert.Equal(3, new ZipArchive(new MemoryStream(zip64)).Entries.Count);
        Assert.Equal(3, ForzaTechReader.DeclaredEntries(new MemoryStream(zip64)));
    }

    /// <summary>The zip with a ZIP64 end record and locator declaring its real count, and an end record that declares 1 entry
    /// and the 0xFFFFFFFF directory offset, which sends ZipArchive to the ZIP64 record.</summary>
    static byte[] Zip64(byte[] zip)
    {
        var eocd = zip.AsSpan().LastIndexOf("PK\u0005\u0006"u8);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(eocd + 10));
        var rec = new byte[56 + 20];
        "PK\u0006\u0006"u8.CopyTo(rec);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(4), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(12), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(14), 45);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(24), count);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(32), count);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(40), BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 12)));
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(48), BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 16)));
        "PK\u0006\u0007"u8.CopyTo(rec.AsSpan(56));
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(64), (ulong)eocd);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(72), 1);
        byte[] end = [.. zip.AsSpan(eocd)];
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), 0xFFFFFFFF);
        return [.. zip.AsSpan(0, eocd), .. rec, .. end];
    }

    /// <summary>The index-wide entry cap: past it the rest of the install is left out, with a warning.</summary>
    [Fact]
    public void TheShaderEntryCapLeavesTheRestOut()
    {
        var (game, _) = Install("cap");
        var reader = new ForzaTechReader { MaxShaderEntries = 1 };
        var log = new List<string>();
        var index = reader.Index(game, reader.Detect(game)!, new Log(log.Add), CancellationToken.None);
        Assert.Single(index.Shaders);
        Assert.Contains("too many shader entries", Assert.Single(log));
    }
}
