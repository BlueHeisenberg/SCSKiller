using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Tank;
using SCSKiller.Tests.Dagor;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Tank;

/// <summary>A synthetic static TACT container (Overwatch's Steam install): data\.build.config and one data file of BLTE
/// blobs back to back, ShaderCode assets among them, as <see cref="StaticContainer"/> describes.</summary>
public class TankReaderTests
{
    static byte[] Vs => DagorReaderTests.Vs;
    static byte[] Ps => DagorReaderTests.Ps;
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    const string Config = "# Static Build Configuration\n\nbuild-name = prometheus-2_25_0_0-154088\nbuild-number = 154088\nbuild-branch = 2_25_0_0\nbuild-product = Prometheus\n";

    /// <summary>One BLTE blob: its chunk table (24-byte entries, 40 with flags 0x10) and each chunk's mode byte and data.</summary>
    internal static byte[] Blob(byte flags, params (char Mode, byte[] Data, int Decoded)[] chunks)
    {
        var entry = flags == 0x10 ? 40 : 24;
        var size = 8 + 4 + chunks.Length * entry;
        var b = new MemoryStream();
        b.Write("BLTE"u8);
        b.Write(BigEndian(size));
        b.Write(BigEndian(flags << 24 | chunks.Length));
        foreach (var c in chunks)
        {
            b.Write(BigEndian(c.Data.Length + 1));
            b.Write(BigEndian(c.Decoded));
            b.Write(new byte[entry - 8]);   // MD5 (and more with 0x10): not checked
        }
        foreach (var c in chunks)
        {
            b.WriteByte((byte)c.Mode);
            b.Write(c.Data);
        }
        return b.ToArray();
    }

    static byte[] BigEndian(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }

    static (char, byte[], int) Plain(byte[] data) => ('N', data, data.Length);

    static (char, byte[], int) Zlib(byte[] data)
    {
        var o = new MemoryStream();
        using (var z = new ZLibStream(o, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ('Z', o.ToArray(), data.Length);
    }

    /// <summary>A ShaderCode asset: 8 bytes 0xFF, a header (with bytes that only look like a gzip member), then the container
    /// in a gzip member.</summary>
    internal static byte[] ShaderCode(byte[] container)
    {
        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true)) z.Write(container);
        return [.. Enumerable.Repeat((byte)0xFF, 8), .. new byte[12], 0x1F, 0x8B, 0x08, 0x00, .. new byte[8], .. gz.ToArray()];
    }

    static Game Install(string name, byte[] data, string? settings, string config = Config)
    {
        var dir = Ff7.TempDir(name);
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllText(Path.Combine(dir, "data", ".build.config"), config);
        File.WriteAllBytes(Path.Combine(dir, "data", "data.001.000"), data);
        File.WriteAllBytes(Path.Combine(dir, "data", "data.000.000"), Blob(0x0F, Plain([1, 2, 3])));   // the encoding file: no shader
        File.WriteAllText(Path.Combine(dir, "data", "data.001.idx"), "not a data file");
        if (settings != null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "docs", "Overwatch", "Settings"));
            File.WriteAllText(Path.Combine(dir, "docs", "Overwatch", "Settings", "Settings_v0.ini"), settings);
        }
        return new Game("steam:2357570", "Overwatch", Store.Steam, dir, Path.Combine(dir, "Overwatch.exe"));
    }

    static TankReader Reader(Game game) => new(Path.Combine(game.InstallDir, "docs"));

    [Fact]
    public void IndexesTheDirectX11ShadersOfEveryShaderCodeBlob()
    {
        var vs = ShaderCode(Vs);
        var data = (byte[])[
            .. Blob(0x0F, Plain([.. "not a shader"u8])),
            .. Blob(0x0F, Zlib(vs[..40]), Plain(vs[40..])),   // two chunks, zlib then plain
            .. Blob(0x10, Zlib(ShaderCode(Ps))),               // 40-byte chunk entries
            .. Blob(0x0F, ('E', new byte[32], 32)),            // encrypted: skipped
            .. Blob(0x0F, Plain(ShaderCode(Vs))),              // the same shader again: indexed once
        ];
        var game = Install("tank-index", data, "[Render.13]\nGraphicsAPI = \"Dx11\"\n");
        var reader = Reader(game);
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(TankReader.Family, "2.25.0.0", null, "D3D11", false, null), engine);

        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal(new[] { Sha(Vs), Sha(Ps) }.Order(StringComparer.Ordinal), index.Shaders.Keys.Order(StringComparer.Ordinal));
        var map = Assert.Single(index.Maps);
        Assert.Equal(@"data\data.001.000", map.Library);
        Assert.Equal(TankReader.Platform, map.Platform);
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));

        foreach (var r in new[] { reader, Reader(game) })   // a reader that didn't index re-indexes first
        {
            var got = new Dictionary<string, byte[]>();
            r.ReadShaders(game, engine, new HashSet<string> { Sha(Vs), Sha(Ps), new('0', 40) }, (h, b) => got.Add(h, b), CancellationToken.None);
            Assert.Equal(Vs, got[Sha(Vs)]);
            Assert.Equal(Ps, got[Sha(Ps)]);
            Assert.Equal(2, got.Count);
        }
        Assert.Equal(Readiness.Ready, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
    }

    [Fact]
    public void StopsReadingAFileAtBytesThatArentABlob()
    {
        var data = (byte[])[.. Blob(0x0F, Plain(ShaderCode(Vs))), .. "garbage, not BLTE"u8, .. Blob(0x0F, Plain(ShaderCode(Ps)))];
        var game = Install("tank-stop", data, null);
        var reader = Reader(game);
        var engine = reader.Detect(game)!;
        var log = new List<string>();
        var index = reader.Index(game, engine, new SyncProgress(log.Add), CancellationToken.None);
        Assert.Equal([Sha(Vs)], index.Shaders.Keys);
        Assert.Contains(log, l => l.Contains("stopped reading data.001.000 at "));
    }

    [Fact]
    public void ApiComesFromTheGamesSettings()
    {
        var data = Blob(0x0F, Plain(ShaderCode(Vs)));
        var dx12 = Install("tank-dx12", data, "GraphicsAPI = \"Dx12\"\n");
        Assert.Equal("D3D12", Reader(dx12).Detect(dx12)!.GraphicsApi);
        var none = Install("tank-noapi", data, null);
        Assert.Equal("D3D11 or D3D12", Reader(none).Detect(none)!.GraphicsApi);
    }

    [Fact]
    public void OnlyABlizzardPrometheusContainerIsTank()
    {
        var data = Blob(0x0F, Plain(ShaderCode(Vs)));
        var other = Install("tank-other", data, null, Config.Replace("Prometheus", "Fenris"));
        Assert.Null(Reader(other).Detect(other));
        var empty = Install("tank-empty", data, null);
        foreach (var f in Directory.GetFiles(Path.Combine(empty.InstallDir, "data"), "data.*")) File.Delete(f);
        Assert.Null(Reader(empty).Detect(empty));
    }

    [Fact]
    public void AnAssetWithoutTheMagicOrAGzipMemberHasNoContainers()
    {
        Assert.Empty(TankReader.Containers([.. new byte[8], .. ShaderCode(Vs)[8..]]));
        Assert.Empty(TankReader.Containers([.. Enumerable.Repeat((byte)0xFF, 8), .. new byte[64]]));
        Assert.Equal(Vs, Assert.Single(TankReader.Containers(ShaderCode(Vs))));
    }

    sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
