using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Unreal;

/// <summary>Shaders stored inside the packages (bShareMaterialShaderCode=False). The layouts on synthetic bytes; the real
/// games (skipped when not installed) end to end: Detect, Index, ReadShaders.</summary>
public class InlineShadersTests(ITestOutputHelper output)
{
    /// <summary>An FShaderCode as UE cooks it: a prefix, the container, the optional-data trailer with packed resource counts.</summary>
    static byte[] Code(string hlsl, string target, byte cb, byte srv)
    {
        byte[] p = [0, 1, srv, cb, 0]; // FShaderCodePackedResourceCounts: UsageFlags, NumSamplers, NumSRVs, NumCBs, NumUAVs
        return [7, 7, 7, .. UnrealReaderTests.Fxc(hlsl, target), (byte)'p', .. BitConverter.GetBytes(p.Length), .. p, .. BitConverter.GetBytes(1 + 4 + p.Length + 4)];
    }

    static byte[] Zlib(byte[] b)
    {
        using var o = new MemoryStream();
        using (var z = new ZLibStream(o, CompressionLevel.Optimal)) z.Write(b);
        return o.ToArray();
    }

    static byte[] I32(int v) => BitConverter.GetBytes(v);

    static byte[] Anchor(int k) => [.. new byte[20], .. I32(k), .. new byte[20 * k], .. I32(k)]; // ResourceHash, ShaderHashes, entry count

    [Fact]
    public void CarvesEveryLayout()
    {
        var vs = Code("float4 main() : SV_Position { return 0; }", "vs_5_0", 0, 0);
        var ps = Code("Texture2D t; SamplerState s; float4 main(float4 p : SV_Position) : SV_Target { return t.Sample(s, p.xy); }", "ps_5_0", 1, 2);
        var cs = Code("RWBuffer<float> b; [numthreads(8, 1, 1)] void main(uint i : SV_DispatchThreadID) { b[i] = 1; }", "cs_5_0", 0, 0);
        var noise = new byte[4096];
        new Random(1).NextBytes(noise);
        var zps = Zlib(ps);
        byte[] symbols = [.. new byte[64]];
        BinaryPrimitives.WriteUInt32BigEndian(symbols, 0xb7756362);
        BinaryPrimitives.WriteUInt64BigEndian(symbols.AsSpan(24), 64); // FCompressedBuffer with no blocks: header only
        byte[] head =
        [
            .. noise,
            // 'A' (4.25-5.4): one raw entry, one compressed
            .. Anchor(2), .. I32(vs.Length), .. vs, .. I32(vs.Length), 0, .. I32(zps.Length), .. zps, .. I32(ps.Length), 3,
            .. noise,
        ];
        byte[] tail =
        [
            // 'B' (5.5+): header buffer, code buffer, symbols (5.6+)
            .. Anchor(1), .. BitConverter.GetBytes(12L), .. I32(cs.Length), .. I32(cs.Length - 14), 5, 0, 0, 0, .. BitConverter.GetBytes((long)cs.Length), .. cs, .. symbols,
            .. noise,
            // 'Z' (4.2x): a zlib stream with its length
            .. I32(zps.Length), .. zps,
            .. noise,
        ];
        byte[] data = [.. head, .. tail];
        var found = new List<InlineShaders.Entry>();
        Assert.Equal(0, InlineShaders.Carve([data], found.Add));
        Assert.Equal(["A", "A", "B", "Z"], found.Select(e => e.Format.ToString()));
        Assert.Equal([vs, ps, cs, ps], found.Select(e => e.Code));
        Assert.Equal(found[0].Map, found[1].Map);
        Assert.NotEqual(found[1].Map, found[2].Map);
        Assert.Equal(0, found[3].Map);
        foreach (var e in found) Assert.Equal(e.Code, InlineShaders.Decode(data, e.Offset, e.Format));
        Assert.Equal(new ResourceCounts(1, 2, 0, 1), ShaderContainer.UeCounts(found[1].Code));
        var split = new List<InlineShaders.Entry>();
        InlineShaders.Carve([head, tail], split.Add); // .uasset + .uexp: offsets and maps as in their concatenation
        Assert.Equal(found.Select(e => (e.Map, e.Offset, e.Format)), split.Select(e => (e.Map, e.Offset, e.Format)));
        Assert.Equal(found.Select(e => e.Code), split.Select(e => e.Code));
        var none = new List<InlineShaders.Entry>();
        InlineShaders.Carve([noise], none.Add);
        Assert.Empty(none);
    }

    static Game? Installed(string name)
    {
        try { return new SteamSource().Discover().Concat(new XboxSource().Discover()).FirstOrDefault(g => g.Name.Contains(name)); }
        catch (Exception) { return null; }
    }

    /// <summary>Installed games: engine, API and platform as the game really runs (Windrose's AES key comes from the
    /// app's store, copied into a temp data dir); every shader reads back byte-exact and parses to its indexed stage.</summary>
    [Trait("Needs", "Game")]
    [Theory]
    [InlineData("Windrose Demo", "5.6", "D3D12", "PCD3D_SM6")]            // UE 5.6 FShaderCodeResource + symbols, Oodle
    [InlineData("Automation - The Car Company", "4.27", "D3D12", "PCD3D_SM5")] // FShaderMapResourceCode entries, LZ4
    [InlineData("Life is Strange Remastered", "4.23", "D3D11", "PCD3D_SM5")]   // FShaderResource zlib streams
    public void IndexesShadersInsidePackages(string name, string version, string api, string platform)
    {
        if (Installed(name) is not { } game) return;
        var data = Ff7.TempDir("inline-" + version);
        var key = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller", "games", game.Id.Replace(':', '_'), "aes.key");
        var gameDir = Path.Combine(data, "games", game.Id.Replace(':', '_'));
        Directory.CreateDirectory(gameDir);
        if (File.Exists(key)) File.Copy(key, Path.Combine(gameDir, "aes.key"));
        var reader = new UnrealReader(data);
        var e = reader.Detect(game)!;
        Assert.Equal((version, api, null), (e.Version, e.GraphicsApi, e.Unsupported));
        if (api == "D3D11") Assert.Equal(new PlanCheck(Readiness.Ready, "compiles every DirectX 11 shader"), new Planner().Check(game, e, null, Ff7.Nvidia));

        var sw = Stopwatch.StartNew();
        var index = reader.Index(game, e, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"{index.Shaders.Count} shaders, {index.Maps.Count} maps in {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal([platform], index.Platforms);
        Assert.Contains(index.Maps, m => m.Library == "Global");
        Assert.All(index.Maps.SelectMany(m => m.Shaders), h => Assert.True(index.Shaders.ContainsKey(h), h));
        Assert.True(index.Shaders.Values.Count(s => s.Stage == Stage.Vertex) > 1000 && index.Shaders.Values.Count(s => s.Stage == Stage.Pixel) > 10000);
        Assert.True(index.Shaders.Values.Count(s => s.Counts == new ResourceCounts(0, 0, 0, 0)) < index.Shaders.Count / 50); // the trailer is there
        Assert.Equal(index.ContentHash, reader.Index(game, e, null, CancellationToken.None).ContentHash);

        var got = 0;
        reader.ReadShaders(game, e, index.Shaders.Keys.ToHashSet(), (h, b) =>
        {
            got++;
            Assert.Equal(h, Convert.ToHexStringLower(SHA1.HashData(b)));
            Assert.Equal(index.Shaders[h].Stage, ShaderContainer.Parse(b, h, index.Shaders[h].Counts)!.Stage);
        }, CancellationToken.None);
        Assert.Equal(index.Shaders.Count, got);
    }

    /// <summary>A library game (FF7 Rebirth, bShareMaterialShaderCode=True): its material packages carry shader map
    /// hashes, never code, so the carver finds nothing there (no false positives).</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void LibraryGameMaterialsCarryNoCode()
    {
        if (!Ff7.HasInstall) return;
        using var provider = new CUE4Parse.FileProvider.DefaultFileProvider(Path.Combine(Ff7.Install, @"End\Content\Paks"), SearchOption.TopDirectoryOnly,
            new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_FinalFantasy7Rebirth), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount();
        var materials = provider.Files.Values.Where(f => f.Extension == "uasset" && Path.GetFileName(f.Path).StartsWith("M_")).OrderBy(f => f.Path).Take(300).ToList();
        Assert.Equal(300, materials.Count);
        var found = new List<InlineShaders.Entry>();
        foreach (var f in materials) InlineShaders.Carve([f.Read()], found.Add);
        Assert.Empty(found);
    }

    /// <summary>Xbox app games: Windows won't open their exes (encrypted at rest), so the engine comes from the containers
    /// and an encrypted one's key isn't scanned for.</summary>
    [Trait("Needs", "Game")]
    [Theory]
    [InlineData("Atomic Heart", "4.27")]  // .pak v11
    [InlineData("High on Life", "4.27")]  // .utoc v3, a UE4 global container
    [InlineData("Hellblade 2", "5.1")]    // .utoc v5
    [InlineData("Solar Ash", "4.25")]     // .pak v9, encrypted
    public void XboxGamesDetectWithoutTheirExe(string name, string version)
    {
        if (new XboxSource().Discover().FirstOrDefault(g => g.Name.Contains(name)) is not { } game) return;
        Assert.Throws<UnauthorizedAccessException>(() => File.OpenRead(game.ExePath).Dispose());
        var e = new UnrealReader(Ff7.TempDir("xbox-" + version)).Detect(game, out var notes)!;
        output.WriteLine($"{game.Name}: {e}; {notes}");
        Assert.Equal(version, e.Version);
        if (e.Encrypted) Assert.Contains("exe can't be read", notes);
    }

    /// <summary>A 4.2x 'Z' entry is bounded like the others (64 MiB, 64x its stored size): a stream that inflates past that
    /// isn't decoded, whatever it holds.</summary>
    [Fact]
    public void AZlibEntryThatInflatesPastTheBoundIsNotDecoded()
    {
        var vs = Code("float4 main() : SV_Position { return 0; }", "vs_5_0", 0, 0);
        var ok = Zlib(vs);
        Assert.NotNull(InlineShaders.Decode([.. I32(ok.Length), .. ok], 0, 'Z'));
        var bomb = Zlib([.. vs, .. new byte[4 << 20]]);   // ~4 KB stored, 4 MB out
        Assert.Null(InlineShaders.Decode([.. I32(bomb.Length), .. bomb], 0, 'Z'));
    }

    /// <summary>A container whose program runs past its end (here a SHEX customdata token without its length) throws
    /// IndexOutOfRangeException from the parser: that shader is skipped and counted, the index goes on.</summary>
    [Fact]
    public void AMalformedContainerIsSkippedAndCounted()
    {
        byte[] shex = [.. I32(0x50), .. I32(3), .. I32(0x35)]; // ps_5_0, 3 tokens, customdata
        byte[] bad = [.. "DXBC"u8, .. new byte[16], .. I32(1), .. I32(32 + 4 + 8 + shex.Length), .. I32(1), .. I32(36), .. "SHEX"u8, .. I32(shex.Length), .. shex, .. I32(4)];
        Assert.Throws<IndexOutOfRangeException>(() => ShaderContainer.Parse(bad.AsSpan(0, bad.Length - 4), "", new(0, 0, 0, 0)));
        var vs = Code("float4 main() : SV_Position { return 0; }", "vs_5_0", 0, 0);
        static byte[] Package(byte[] code) => [.. Anchor(1), .. I32(code.Length), .. code, .. I32(code.Length), 0]; // one raw 'A' entry

        var install = Ff7.TempDir("inline-malformed");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        Pak(Path.Combine(paks, "Proj-Windows.pak"), ("Proj/Content/M_Bad.uasset", Package(bad)), ("Proj/Content/M_Good.uasset", Package(vs)));
        using var provider = new CUE4Parse.FileProvider.DefaultFileProvider(paks, SearchOption.TopDirectoryOnly,
            new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount();
        var log = new List<string>();
        var game = new Game("test:malformed", "Malformed", Store.Other, install, "");
        var index = new UnrealReader(Ff7.TempDir("inline-malformed-data")).IndexInline(provider, game, new Log(log.Add), CancellationToken.None);
        Assert.Equal([Stage.Vertex], index.Shaders.Values.Select(s => s.Stage));
        Assert.Contains(", 1 unparseable", Assert.Single(log));
    }

    /// <summary>A split package (.uasset header, .uexp exports) is carved file by file: the offsets inline.idx keeps are
    /// into both files as one, and read back the same shader.</summary>
    [Fact]
    public void SplitPackagesReadBackFromTheirExports()
    {
        var vs = Code("float4 main() : SV_Position { return 0; }", "vs_5_0", 0, 0);
        var install = Ff7.TempDir("inline-split");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        Pak(Path.Combine(paks, "Proj-Windows.pak"), ("Proj/Content/M_Split.uasset", new byte[2048]),
            ("Proj/Content/M_Split.uexp", [.. new byte[100], .. Anchor(1), .. I32(vs.Length), .. vs, .. I32(vs.Length), 0]));
        using var provider = new CUE4Parse.FileProvider.DefaultFileProvider(paks, SearchOption.TopDirectoryOnly,
            new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount();
        var data = Ff7.TempDir("inline-split-data");
        var reader = new UnrealReader(data);
        var game = new Game("test:split", "Split", Store.Other, install, "");
        var h = Assert.Single(reader.IndexInline(provider, game, null, CancellationToken.None).Shaders.Keys);
        using (var r = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(new SCSKiller.Core.App.AppStore(data).GameDir(game.Id), "inline.idx")), CompressionMode.Decompress)))
            Assert.Equal($"{h} A {2048 + 100 + Anchor(1).Length} Proj/Content/M_Split.uasset", r.ReadLine());
        var got = new List<byte[]>();
        reader.ReadInline(provider, game, new HashSet<string> { h }, (_, b) => got.Add(b), CancellationToken.None);
        Assert.Equal(h, Convert.ToHexStringLower(SHA1.HashData(Assert.Single(got))));
    }

    internal sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    /// <summary>A version 7 .pak (4.20 to 4.21: legacy index, 61-byte footer) of uncompressed, unencrypted files.</summary>
    internal static void Pak(string path, params (string Name, byte[] Data)[] files)
    {
        using var f = new BinaryWriter(File.Create(path));
        static void Entry(BinaryWriter w, long offset, long size) // FPakEntry: offset, sizes, compression flags, SHA-1, flags, block size
        {
            w.Write(offset); w.Write(size); w.Write(size); w.Write(0); w.Write(new byte[20]); w.Write((byte)0); w.Write(0u);
        }
        static void FString(BinaryWriter w, string s) { w.Write(s.Length + 1); w.Write(System.Text.Encoding.ASCII.GetBytes(s)); w.Write((byte)0); }
        var at = new List<long>();
        foreach (var (_, data) in files) { at.Add(f.BaseStream.Position); Entry(f, 0, data.Length); f.Write(data); } // each file's data follows a copy of its entry
        using var index = new MemoryStream();
        var iw = new BinaryWriter(index);
        FString(iw, "../../../");
        iw.Write(files.Length);
        for (var i = 0; i < files.Length; i++) { FString(iw, files[i].Name); Entry(iw, at[i], files[i].Data.Length); }
        var indexAt = f.BaseStream.Position;
        f.Write(index.ToArray());
        f.Write(new byte[16]); f.Write((byte)0); f.Write(0x5A6F12E1u); f.Write(7); f.Write(indexAt); f.Write(index.Length); f.Write(new byte[20]); // key GUID, encrypted index, magic, version, index offset/size/SHA-1
    }

    /// <summary>Packages are read in parallel up to a byte budget: the bytes held never pass it, except by one package
    /// larger than the budget, which runs alone.</summary>
    [Fact]
    public void PackageBytesInFlightStayWithinTheBudget()
    {
        var budget = new UnrealReader.ByteBudget(100);
        long held = 0;
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var bytes = i % 50 == 7 ? 500 : 30;
            using var _ = budget.Take(bytes);
            var now = Interlocked.Add(ref held, bytes);
            Assert.True(bytes == 500 ? now == 500 : now <= 100, $"{now} held");
            Thread.SpinWait(2000);
            Interlocked.Add(ref held, -bytes);
        });
    }

    /// <summary>A read of <paramref name="n"/> one-shader packages, mounted.</summary>
    static (CUE4Parse.FileProvider.DefaultFileProvider Provider, Game Game) Packages(string name, int n)
    {
        var vs = Code("float4 main() : SV_Position { return 0; }", "vs_5_0", 0, 0);
        byte[] Package = [.. Anchor(1), .. I32(vs.Length), .. vs, .. I32(vs.Length), 0]; // one raw 'A' entry
        var install = Ff7.TempDir(name);
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        Pak(Path.Combine(paks, "Proj-Windows.pak"), Enumerable.Range(0, n).Select(i => ($"Proj/Content/M_{i}.uasset", Package)).ToArray());
        var provider = new CUE4Parse.FileProvider.DefaultFileProvider(paks, SearchOption.TopDirectoryOnly,
            new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount();
        return (provider, new Game("test:" + name, name, Store.Other, install, ""));
    }

    /// <summary>The whole-package read is under the plan's memory budget: over it, it stops with the reason and where it was, a <see cref="PlanTooBigException"/> as the plan's, not an AggregateException of its workers.</summary>
    [Fact]
    public void AReadOverItsBudgetStopsWithTheReason()
    {
        var (provider, game) = Packages("inline-budget", 40);
        using (provider)
        {
            var memory = Assert.Throws<PlanTooBigException>(() => new UnrealReader(Ff7.TempDir("inline-budget-data")) { MemoryBudget = 1 }.IndexInline(provider, game, null, CancellationToken.None));
            Assert.StartsWith("the shader read outgrew its memory limit of ", memory.Message);
            Assert.EndsWith(" GB on this PC at 0 of 40 packages: this game's packages hold more shaders than a compile can read", memory.Message);
            Assert.DoesNotContain(";", memory.Message);
            Assert.Single(new UnrealReader(Ff7.TempDir("inline-budget-data")).IndexInline(provider, game, null, CancellationToken.None).Shaders);   // within the default budget
        }
    }

    /// <summary>The read reports packages read of the total as it goes, from below-normal threads, and the caller's thread
    /// keeps its priority.</summary>
    [Fact]
    public void TheReadCountsPackagesOnBelowNormalThreads()
    {
        var (provider, game) = Packages("inline-count", 150);
        using (provider)
        {
            var seen = new System.Collections.Concurrent.ConcurrentBag<(int Done, int Total, ThreadPriority Priority)>();
            var index = new UnrealReader(Ff7.TempDir("inline-count-data")).IndexInline(provider, game, null, CancellationToken.None,
                progress: new Reporter<(int Done, int Total)>(r => seen.Add((r.Done, r.Total, Thread.CurrentThread.Priority))));
            Assert.Equal(150, index.Maps.Count);
            Assert.Equal((150, 150), seen.Max(s => (s.Done, s.Total)));
            Assert.InRange(seen.Count, 100, 150);   // each whole percent, not each package
            Assert.All(seen, s => Assert.Equal(ThreadPriority.BelowNormal, s.Priority));
            Assert.Equal(ThreadPriority.Normal, Thread.CurrentThread.Priority);
        }
    }

    /// <summary>The package loop runs up to one package per CPU thread at once (the scheduler decides how many it reaches),
    /// all below normal.</summary>
    [Fact]
    public void PackagesReadOnEveryThreadBelowNormal()
    {
        int now = 0, most = 0;
        var priorities = new System.Collections.Concurrent.ConcurrentBag<ThreadPriority>();
        UnrealReader.ForEachPackage(Enumerable.Range(0, 400).ToList(), null, null, CancellationToken.None, _ =>
        {
            var n = Interlocked.Increment(ref now);
            for (int m; (m = Volatile.Read(ref most)) < n && Interlocked.CompareExchange(ref most, n, m) != m;) { }
            priorities.Add(Thread.CurrentThread.Priority);
            Thread.Sleep(5);
            Interlocked.Decrement(ref now);
        });
        Assert.Equal(400, priorities.Count);
        Assert.All(priorities, p => Assert.Equal(ThreadPriority.BelowNormal, p));
        Assert.InRange(most, 1, Environment.ProcessorCount);
    }

    sealed class Reporter<T>(Action<T> a) : IProgress<T> { public void Report(T value) => a(value); }
}
