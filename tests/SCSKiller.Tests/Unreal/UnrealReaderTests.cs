using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Unreal;

public class UnrealReaderTests(ITestOutputHelper output)
{
    readonly UnrealReader reader = new(Ff7.TempDir("reader-data")); // AES keys found for encrypted games go here, not to the app's data

    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7IndexMatchesTheOldTool()
    {
        if (!Ff7.HasInstall || !Ff7.HasDump) return;
        var engine = reader.Detect(Ff7.Game)!;
        Assert.Equal(new EngineInfo("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null), engine);

        var index = reader.Index(Ff7.Game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(Ff7.Out, "summary.json"))).RootElement;
        var libs = summary.GetProperty("libraries").EnumerateArray().ToList();
        Assert.Equal(summary.GetProperty("unique_bytecode").GetInt32(), index.Shaders.Count);
        Assert.Equal(libs.Sum(l => l.GetProperty("shader_maps").GetInt32()), index.Maps.Count);
        Assert.Equal(File.ReadLines(Path.Combine(Ff7.Out, "shaders.jsonl")).Count(), index.Maps.Sum(m => m.Shaders.Count)); // one line per (map, slot)
        Assert.Equal(summary.GetProperty("platforms").EnumerateArray().Select(p => p.GetString()), index.Platforms);

        // every field the planner uses, shader by shader
        var old = Ff7.Index();
        Assert.Equal(old.Shaders.Count, index.Shaders.Count);
        foreach (var (sha, a) in old.Shaders)
        {
            var b = index.Shaders[sha];
            Assert.True(a.Stage == b.Stage && a.ShaderModel == b.ShaderModel && a.Size == b.Size && a.Counts == b.Counts
                && (a.Bindings.SequenceEqual(b.Bindings) || b.Stage == Stage.Library) && a.Inputs.SequenceEqual(b.Inputs.Select(Old)) && a.Outputs.SequenceEqual(b.Outputs.Where(o => o.Register < ShaderContainer.PrimitiveRow).Select(Old)), sha);
        }

        // the old tool didn't dump read masks or interpolation modes, a DXIL library's resources (RDAT), nor a mesh shader's
        // per-primitive outputs
        static SigElement Old(SigElement e) => e with { ReadMask = SigElement.UnknownReadMask, Interpolation = 0 };

        var again = reader.Index(Ff7.Game, engine, null, CancellationToken.None);
        Assert.Equal(index.ContentHash, again.ContentHash);
        output.WriteLine($"{index.Shaders.Count} shaders, {index.Maps.Count} maps, content hash {index.ContentHash} (stable)");
    }

    /// <summary>DXBC geometry shaders (fxc, SM5): dcl_inputprimitive -> GsInputPrimitive, D3D_PRIMITIVE numbering.
    /// (DXIL's PSV0 path is covered on FF7 by PlannerTests.GsChainsGetTheirInputTopology.)</summary>
    [Theory]
    [InlineData("point", 1, 1)]
    [InlineData("line", 2, 2)]
    [InlineData("triangle", 3, 3)]
    [InlineData("lineadj", 4, 6)]
    [InlineData("triangleadj", 6, 7)]
    public void DxbcGsInputPrimitive(string prim, int vertices, int expected)
    {
        var code = Fxc($"struct V {{ float4 p : SV_Position; }}; [maxvertexcount(1)] void main({prim} V v[{vertices}], inout PointStream<V> s) {{ s.Append(v[0]); }}", "gs_5_0");
        var info = ShaderContainer.Parse(code, "", new(0, 0, 0, 0))!;
        Assert.Equal((Stage.Geometry, expected), (info.Stage, info.GsInputPrimitive));
        var vs = ShaderContainer.Parse(Fxc("float4 main() : SV_Position { return 0; }", "vs_5_0"), "", new(0, 0, 0, 0))!;
        Assert.Equal(0, vs.GsInputPrimitive);
    }

    internal static byte[] Fxc(string src, string target)
    {
        Assert.Equal(0, D3DCompile(src, src.Length, null, 0, 0, "main", target, 0, 0, out var code, out _));
        var bytes = new byte[code.GetBufferSize()];
        Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
        return bytes;
    }

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3DBlob { [PreserveSig] nint GetBufferPointer(); [PreserveSig] int GetBufferSize(); }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    static extern int D3DCompile(string src, nint size, string? name, nint defines, nint include, string entry, string target, uint flags1, uint flags2,
        out ID3DBlob code, out ID3DBlob? errors);

    [Trait("Needs", "Game")]
    [Fact]
    public void ReadShadersReturnsContainerBytes()
    {
        if (!Ff7.HasInstall || !Ff7.HasDump) return;
        var engine = reader.Detect(Ff7.Game)!;
        var want = Ff7.Index().Shaders.Keys.Where((_, i) => i % 5000 == 0).ToHashSet();
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(Ff7.Game, engine, want, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(want.Order(), got.Keys.Order());
        foreach (var (h, b) in got)
        {
            Assert.Equal(h, Convert.ToHexStringLower(SHA1.HashData(b)));
            Assert.Equal(File.ReadAllBytes(Path.Combine(Ff7.Out, "bytecode", h + ".bin")), b);
        }
    }

    /// <summary>A package a patch pak overrides is listed once per pak, so maps built in parallel share a hash: however the
    /// threads finish, <see cref="UnrealReader.Canonical"/> gives one order and one content hash.</summary>
    [Fact]
    public void MapsBuiltInParallelComeOutInOneOrder()
    {
        var maps = Enumerable.Range(0, 200).Select(i => new ShaderMap($"pkg{i % 50}", $"pkg{i % 50}", "PCD3D_SM5",
            [.. Enumerable.Range(0, 1 + i % 7).Select(k => ExactLayoutsTests.Hash($"{i % 120}-{k}"))])).ToList();
        maps.AddRange(maps.Take(10)); // the same copy twice
        static string Lines(IEnumerable<ShaderMap> ms) => string.Concat(ms.Select(m => $"{m.Hash}:{string.Join(',', m.Shaders)}\n"));
        List<ShaderMap> Shuffled(int seed) { var r = new Random(seed); return [.. maps.OrderBy(_ => r.Next())]; }
        Assert.NotEqual(Lines(Shuffled(1).OrderBy(m => m.Hash, StringComparer.Ordinal)), Lines(Shuffled(2).OrderBy(m => m.Hash, StringComparer.Ordinal)));

        string Built(int seed)
        {
            var bag = new ConcurrentBag<ShaderMap>();
            Parallel.ForEach(Shuffled(seed), bag.Add);
            return Lines(UnrealReader.Canonical(bag));
        }
        var first = Built(1);
        foreach (var seed in Enumerable.Range(2, 8)) Assert.Equal(first, Built(seed));
    }

    /// <summary>Tiny Tina's Wonderlands: version-1 archives (maps from package references) and patch paks that override
    /// packages; two indexes give the same maps and content hash.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void PatchedPackagesIndexTheSameEveryTime()
    {
        var dir = TestEnv.GameDir("Tiny Tina's Wonderlands");
        var game = new Game("steam:1286680", "Tiny Tina's Wonderlands", Store.Steam, dir, Path.Combine(dir, @"OakGame\Binaries\Win64\Wonderlands.exe"));
        if (!File.Exists(game.ExePath)) return;
        var engine = reader.Detect(game)!;
        var a = reader.Index(game, engine, null, CancellationToken.None);
        var b = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal(a.Maps.Select(m => $"{m.Hash}:{string.Join(',', m.Shaders)}"), b.Maps.Select(m => $"{m.Hash}:{string.Join(',', m.Shaders)}"));
        Assert.Equal(a.ContentHash, b.ContentHash);
        output.WriteLine($"{a.Shaders.Count} shaders, {a.Maps.Count} maps, content hash {a.ContentHash}");
    }

    /// <summary>SILENT HILL: Townfall (5.6) ships compute shaders with a [WaveSize] of 16 or 64 lanes: they get their own
    /// platform, so NVIDIA's plan (32 lanes) has none of them; every other shader stays on PCD3D_SM6.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void WaveSizePermutationsGetTheirOwnPlatform()
    {
        var game = new SCSKiller.Core.Games.SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1636440");
        if (game == null) return;
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var wave = index.Maps.Where(m => m.Platform.StartsWith("PCD3D_SM6 wave")).SelectMany(m => m.Shaders).ToHashSet();
        output.WriteLine($"platforms {string.Join(", ", index.Platforms)}; {wave.Count} wave-size shaders");
        Assert.NotEmpty(wave);
        Assert.Empty(wave.Intersect(index.Maps.Where(m => m.Platform == "PCD3D_SM6").SelectMany(m => m.Shaders)));
        var lanes = new List<(uint Min, uint Max)?>();
        reader.ReadShaders(game, engine, wave, (_, b) => lanes.Add(SCSKiller.Core.Carved.Dxbc.WaveLanes(b)), CancellationToken.None);
        Assert.Equal(wave.Count, lanes.Count);
        Assert.All(lanes, l => Assert.True(l is { } x && (x.Min > 32 || x.Max < 32), $"{l}"));
        var plan = new SCSKiller.Core.Planning.Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir("townfall-wave"), null, CancellationToken.None);
        var planned = SCSKiller.Core.Planning.PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').SelectMany(r => SCSKiller.Core.Planning.PsoDb.Parse(r).Stages.Values);
        Assert.Empty(planned.Where(wave.Contains));
    }

    /// <summary>Every game the old tool indexed: same engine version (except Windrose, 5.6, exe 5.6.1, which the old tool's
    /// TOC guess called 5.5; and Darwin's Paradox, 5.4 with TOC version 6, which it took for 5.3), and the right verdict for
    /// games without shader libraries (encrypted, or indexable with the shaders inside the packages).</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void DetectsIndexedGames()
    {
        var outDir = Path.Combine(Ff7.Main, "out");
        if (!Directory.Exists(outDir)) return;
        foreach (var file in Directory.EnumerateFiles(outDir, "summary.json", SearchOption.AllDirectories))
        {
            var s = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            var dir = s.GetProperty("game_dir").GetString()!;
            if (!Directory.Exists(dir)) continue;
            var exe = s.TryGetProperty("exe", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()! : "";
            if (exe != "" && !File.Exists(exe)) continue; // uninstalled since the old tool's dump (a leftover folder remains)
            var e = reader.Detect(new Game("test:" + Path.GetFileName(dir), Path.GetFileName(dir), Store.Steam, dir, exe))!;
            output.WriteLine($"{Path.GetFileName(dir)}: {e}");
            var engine = s.GetProperty("engine").GetString()!; // e.g. GAME_UE4_26
            Assert.Equal(Path.GetFileName(dir) switch { "Windrose Demo" when !e.Encrypted => "5.6", "Darwin's Paradox" => "5.4", _ => engine[7..].Replace('_', '.') }, e.Version);
            var fork = s.GetProperty("game").GetString();
            Assert.Equal(fork == engine ? null : fork, e.Fork);
            var libraries = s.GetProperty("libraries").GetArrayLength();
            var aes = s.GetProperty("aes_needed").GetBoolean();
            if (libraries == 0 && aes && !e.Encrypted) continue; // we found the AES key the old tool didn't have (UnrealKeysTests)
            if (libraries == 0 && aes) Assert.StartsWith("encrypted game files (needs the game's AES key", e.Unsupported);
            else Assert.Null(e.Unsupported);
            Assert.Equal(libraries == 0 && aes, e.Encrypted);
            // full index of every game (IoStore included): ~1 min of all cores, so opt-in
            if (libraries == 0 || Environment.GetEnvironmentVariable("SCSKILLER_ALL_GAMES") != "1") continue;
            var index = reader.Index(new Game("test", "", Store.Steam, dir, exe), e, null, CancellationToken.None);
            output.WriteLine($"  indexed: {index.Shaders.Count} shaders, {index.Maps.Count} maps, platforms {string.Join(",", index.Platforms)}");
            Assert.Equal(s.GetProperty("unique_bytecode").GetInt32(), index.Shaders.Count);
            Assert.Equal(s.GetProperty("libraries").EnumerateArray().Sum(l => l.GetProperty("shader_maps").GetInt32()), index.Maps.Count(m => !m.Platform.Contains(" wave")));
        }
        Assert.Null(reader.Detect(new Game("test", "none", Store.Other, Directory.CreateTempSubdirectory().FullName, "")));
    }

    /// <summary>PSV0 and RDAT tables whose record stride can't hold a record (0 here) give no bindings instead of the same
    /// record appended a billion times, and DXBC declarations whose extended tokens run past their length are read within it,
    /// not into every declaration after.</summary>
    [Fact]
    public void ZeroStrideBindingTablesAreNotRepeated()
    {
        static byte[] Container(params (string FourCC, byte[] Data)[] parts)
        {
            var head = 32 + 4 * parts.Length;
            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            w.Write("DXBC"u8); w.Write(new byte[16]); w.Write(1); w.Write(head + parts.Sum(p => 8 + p.Data.Length)); w.Write(parts.Length);
            for (int i = 0, at = head; i < parts.Length; at += 8 + parts[i].Data.Length, i++) w.Write(at);
            foreach (var (cc, d) in parts) { w.Write(System.Text.Encoding.ASCII.GetBytes(cc)); w.Write(d.Length); w.Write(d); }
            return o.ToArray();
        }
        byte[] psv = [.. BitConverter.GetBytes(0), .. BitConverter.GetBytes(1_000_000), .. BitConverter.GetBytes(0), 2, 0, 0, 0, .. new byte[12]];   // one cbv record, stride 0
        var vs = Container(("DXIL", BitConverter.GetBytes(1u << 16 | 0x60)), ("PSV0", psv));   // vs_6_0
        var info = ShaderContainer.Parse(vs, "", new(0, 0, 0, 0))!;
        Assert.Equal((Stage.Vertex, 0), (info.Stage, info.Bindings.Count));
        byte[] none = [.. BitConverter.GetBytes(24), .. new byte[24], .. BitConverter.GetBytes(0)];   // no resources: DXC writes no stride
        Assert.Empty(ShaderContainer.Parse(Container(("DXIL", BitConverter.GetBytes(1u << 16 | 0x60)), ("PSV0", none)), "", new(0, 0, 0, 0))!.Bindings);
        byte[] rdat = [.. BitConverter.GetBytes(0), .. BitConverter.GetBytes(2), .. BitConverter.GetBytes(16), .. BitConverter.GetBytes(28),
            .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(4), 0, 0, 0, 0,                                          // string table: one empty string
            .. BitConverter.GetBytes(3), .. BitConverter.GetBytes(32), .. BitConverter.GetBytes(1_000_000), .. BitConverter.GetBytes(0), .. new byte[24]];   // resources, stride 0
        Assert.Empty(ShaderContainer.Rdat(Container(("DXIL", BitConverter.GetBytes(6u << 16 | 0x63)), ("RDAT", rdat))).Resources);
        // ps_5_0: 200,000 one-token dcl_constantbuffer instructions, each flagged as followed by an extended token
        byte[] shex = [.. new uint[] { 0x50, 0 }.Concat(Enumerable.Repeat(0x81000059u, 200_000)).Concat(new uint[3]).SelectMany(BitConverter.GetBytes)];
        Assert.Empty(ShaderContainer.Parse(Container(("SHEX", shex)), "", new(0, 0, 0, 0))!.Bindings);
    }

    /// <summary>An IoStore shader library with 8-byte (5.8) or 20-byte hashes reads whichever UE5 version was detected; bytes
    /// that are no library name the file instead of an overflow.</summary>
    [Theory]
    [InlineData(8, CUE4Parse.UE4.Versions.EGame.GAME_UE5_6)]
    [InlineData(20, CUE4Parse.UE4.Versions.EGame.GAME_UE5_8)]
    [InlineData(8, CUE4Parse.UE4.Versions.EGame.GAME_UE5_8)]
    public void ShaderLibraryHashWidthFollowsTheFile(int width, CUE4Parse.UE4.Versions.EGame detected)
    {
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(1);                                                     // version 1: IoStore
        w.Write(1); w.Write(Enumerable.Repeat((byte)0xAB, width).ToArray()); // shader map hashes
        w.Write(1); w.Write(Enumerable.Repeat((byte)0xCD, width).ToArray()); // shader hashes
        w.Write(1); w.Write(new byte[12]);                              // group chunk ids
        w.Write(1); w.Write(0); w.Write(1);                             // map entries
        w.Write(1); w.Write(0L);                                        // shader entries
        w.Write(1); w.Write(0); w.Write(1); w.Write(64); w.Write(64);    // group entries
        w.Write(1); w.Write(0);                                         // shader indices
        var lib = (CUE4Parse.UE4.Shaders.FIoStoreShaderCodeArchive)UnrealReader.ReadLibrary("x.ushaderbytecode", o.ToArray(), detected).SerializedShaders;
        Assert.Equal(string.Concat(Enumerable.Repeat("ab", width)).PadRight(40, '0'), lib.ShaderMapHashes.Single().ToString().ToLowerInvariant());
        Assert.Equal(64u, lib.ShaderGroupEntries.Single().UncompressedSize);

        var e = Assert.Throws<InvalidDataException>(() => UnrealReader.ReadLibrary("Game/x.ushaderbytecode", [1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF], detected));
        Assert.StartsWith("Game/x.ushaderbytecode: not a shader library", e.Message);
    }

    /// <summary>A 20-byte library read as 5.8 takes hash bytes 8-11 for the next count (0x40000000 here): the counts are
    /// bounded by the file before CUE4Parse allocates (it would ask for gigabytes), and the 20-byte layout is read.</summary>
    [Fact]
    public void ShaderLibraryCountsAreBoundedBeforeAllocating()
    {
        byte[] hash = [.. Enumerable.Repeat((byte)0xAB, 8), 0, 0, 0, 0x40, .. Enumerable.Repeat((byte)0xAB, 8)];
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(1);
        w.Write(1); w.Write(hash);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        Assert.Null(UnrealReader.LibraryEnd(o.ToArray(), 8, ioStore: true));
        var lib = (CUE4Parse.UE4.Shaders.FIoStoreShaderCodeArchive)UnrealReader.ReadLibrary("x", o.ToArray(), CUE4Parse.UE4.Versions.EGame.GAME_UE5_8).SerializedShaders;
        Assert.Equal(Convert.ToHexStringLower(hash), lib.ShaderMapHashes.Single().ToString().ToLowerInvariant());
    }

    /// <summary>Fortnite (anti-cheat: its exe isn't read) writes .utoc version 10 and 8-byte shader library hashes: detected as
    /// 5.8 from its containers, its libraries read, planned with the 5.8 rule, unconfirmed.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void FortniteDetectsAs58FromItsContainers()
    {
        if (new SCSKiller.Core.Games.EpicSource().Discover().FirstOrDefault(g => g.Id == "epic:Fortnite") is not { } game) return;
        var r = new UnrealReader(Ff7.TempDir("fortnite"));
        var e = r.Detect(game)!;
        Assert.Equal("5.8", e.Version);
        Assert.Equal(SCSKiller.Tests.Planning.Rules.Ue58, SCSKiller.Core.Planning.RootSig.RuleFor(e));
        Assert.False(SCSKiller.Core.Planning.RootSig.Verified(e));
        var index = r.Index(game, e, new Progress<string>(output.WriteLine), CancellationToken.None);
        Assert.NotEmpty(index.Shaders);
        output.WriteLine($"{index.Shaders.Count} shaders, {index.Maps.Count} maps");
    }

    /// <summary>.utoc version 8 is 5.5 to 5.7, so an anti-cheat game's fork may be of a later version than the 5.5 its
    /// containers tell: Neverness to Everness' release fork (5.6) wins over its closed beta's (5.5), whose name only extends
    /// the folder's. A version read from the exe is exact: a later version's fork isn't taken.</summary>
    [Fact]
    public void ForkOfTheVersionRangeTheContainersTellIsFound()
    {
        const CUE4Parse.UE4.Versions.EGame Ue55 = CUE4Parse.UE4.Versions.EGame.GAME_UE5_5, Ue57 = CUE4Parse.UE4.Versions.EGame.GAME_UE5_7,
            Nte = CUE4Parse.UE4.Versions.EGame.GAME_NevernessToEverness;
        Assert.Equal(Nte, UnrealReader.DetectFork(Ue55, Ue57, "Neverness to Everness", "HTGame"));
        Assert.Equal(Nte, UnrealReader.DetectFork(CUE4Parse.UE4.Versions.EGame.GAME_UE5_6, CUE4Parse.UE4.Versions.EGame.GAME_UE5_6, "Neverness To Everness", "HTGame"));
        Assert.NotEqual(Nte, UnrealReader.DetectFork(Ue55, Ue55, "Neverness to Everness", "HTGame"));
    }

    /// <summary>A pak-only game whose exe isn't read (anti-cheat) and whose packages aren't readable (an encrypted index), as
    /// 3on3 FreeStyle: Rebound: its version is guessed, from the exe's numeric version when that is 5.x, else 4.27.</summary>
    [Theory]
    [InlineData(5, 1, "5.1")]
    [InlineData(5, 4, "5.4")]
    [InlineData(5, 0, "5.0")]
    [InlineData(0, 0, "4.27")]
    [InlineData(1, 0, "4.27")]
    public void AVersionNothingTellsIsGuessedFromTheExe(int major, int minor, string version)
    {
        var (game, _) = PakOnlyGame($"guess-{major}-{minor}", major, minor);
        var e = reader.Detect(game)!;
        Assert.Equal((version, true), (e.Version, e.VersionGuessed));
        Assert.Equal("encrypted game files (needs the game's AES key)", e.Unsupported);
    }

    /// <summary>A build string, a .utoc or a fork named like the game tells the version: not guessed, whatever the exe's
    /// numeric version says.</summary>
    [Fact]
    public void AVersionTheFilesTellIsNotGuessed()
    {
        var (built, _) = PakOnlyGame("told-build", 5, 4, antiCheat: false);
        File.AppendAllText(built.ExePath, "++UE5+Release-5.1-CL-23901901", System.Text.Encoding.Unicode);
        Assert.Equal(("5.1", false), reader.Detect(built) is { } b ? (b.Version, b.VersionGuessed) : default);

        var (toc, paks) = PakOnlyGame("told-toc", 5, 4);
        var header = new byte[81];
        header[16] = 5;   // EIoStoreTocVersion 5: 5.0 to 5.3
        File.WriteAllBytes(Path.Combine(paks, "global.utoc"), header);
        Assert.Equal(("5.1", false), reader.Detect(toc) is { } t ? (t.Version, t.VersionGuessed) : default);

        var (fork, _) = PakOnlyGame("HogwartsLegacy", 1, 0);   // the folder names CUE4Parse's 4.27 fork
        Assert.Equal(("4.27", "GAME_HogwartsLegacy", false), reader.Detect(fork) is { } f ? (f.Version, f.Fork, f.VersionGuessed) : default);
    }

    /// <summary>.utoc version 8 is written by 5.5 to 5.8: the asset registry's format version tells 5.6 (21), 5.7 (22) and 5.8
    /// (24), and one past 5.8's is a newer Unreal, not supported yet. Without it, the exe's numeric version tells within the
    /// range (a 5.9 one doesn't), else 5.5 stays. A .utoc version past the known ones is a newer Unreal too.</summary>
    [Theory]
    [InlineData(8, 21, 1, "5.6", false, null)]
    [InlineData(8, 22, 1, "5.7", false, null)]
    [InlineData(8, 24, 1, "5.8", false, null)]
    [InlineData(8, 17, 1, "5.5", false, null)]
    [InlineData(8, 22, 6, "5.7", false, null)]
    [InlineData(8, null, 7, "5.7", false, null)]
    [InlineData(8, null, 9, "5.5", false, null)]
    [InlineData(8, 25, 1, "newer than 5.8", false, "Unreal newer than 5.8 isn't supported yet")]
    [InlineData(10, 25, 1, "newer than 5.8", false, "Unreal newer than 5.8 isn't supported yet")]
    [InlineData(11, null, 1, "newer than 5.8", false, "Unreal newer than 5.8 isn't supported yet")]
    [InlineData(5, 22, 1, "5.1", true, null)]   // 5.0 to 5.3: a registry of 5.7 contradicts them
    [InlineData(5, null, 3, "5.3", false, null)]
    [InlineData(6, 22, 1, "5.4", true, null)]   // 5.4 exactly: the registry contradicts it
    public void TheContainersRangeIsNarrowedByTheRegistryAndTheExe(int toc, int? registry, int peMinor, string version, bool guessed, string? unsupported)
    {
        var game = ContainersGame($"range-{toc}-{registry}-{peMinor}", toc, registry, peMinor);
        var e = reader.Detect(game)!;
        Assert.Equal((version, guessed, unsupported), (e.Version, e.VersionGuessed, e.Unsupported));
        if (unsupported == null) return;
        Assert.Equal(new PlanCheck(Readiness.Unsupported, unsupported), new SCSKiller.Core.Planning.Planner().Check(game, e, null, Ff7.Nvidia));
        Assert.Equal(unsupported, Assert.Throws<InvalidDataException>(() => reader.Index(game, e, null, CancellationToken.None)).Message);
    }

    /// <summary>A registry past 5.8's makes a fork's game unsupported too; a .utoc whose header claims counts the file can't
    /// hold isn't handed to CUE4Parse (it would size its arrays by them) when probing the library hash width.</summary>
    [Fact]
    public void NewerRegistryAndImpossibleTocCounts()
    {
        var fork = ContainersGame("HogwartsLegacy-newer", 3, 25, 1);
        Assert.Equal(("newer than 5.8", "Unreal newer than 5.8 isn't supported yet"), reader.Detect(fork) is { } f ? (f.Version, f.Unsupported) : default);

        var game = ContainersGame("toc-counts", 8, null, 1);
        var toc = new byte[100];
        toc[16] = 8;
        toc[80] = 8;   // indexed, unencrypted
        BitConverter.TryWriteBytes(toc.AsSpan(24), 0x40000000);   // TocEntryCount
        File.WriteAllBytes(Path.Combine(game.InstallDir, "Proj", "Content", "Paks", "pakchunk1-Windows.utoc"),
            [.. toc, .. System.Text.Encoding.ASCII.GetBytes("ShaderArchive-Proj-PCD3D_SM6.ushaderbytecode")]);
        Assert.Equal(("5.5", null), reader.Detect(game) is { } e ? (e.Version, e.Unsupported) : default);
    }

    /// <summary>A build string past 5.8 (or of UE 6) is a newer Unreal, shown as read, never clamped to 5.8 and planned with its
    /// rules. One that names UE4 in an install whose containers are UE5's is a branch name kept from before an upgrade: the
    /// containers tell. Counts are bounded: an endless minor isn't a version. An asset registry of another version makes it a
    /// guess.</summary>
    [Theory]
    [InlineData("++UE5+Release-5.9", "5.9", "Unreal 5.9 isn't supported yet")]
    [InlineData("++UE6+Release-6.0-CL-1", "6.0", "Unreal 6.0 isn't supported yet")]
    [InlineData("++UE5+Release-5.7", "5.7", null)]
    [InlineData("++UE4+Release-4.27", "5.7", null)]
    [InlineData("++UE5+Release-5.99999999999", "5.7", null)]
    [InlineData("++UE6+Release-6.\u0661", "5.7", null)]   // an Arabic-Indic digit isn't one
    [InlineData("++UE4+Release-5.6", "5.6", null, 8, null)]
    [InlineData("++UE5+Release-5.8", "newer than 5.8", "Unreal newer than 5.8 isn't supported yet", 11)]
    [InlineData("++UE5+Release-5.6", "5.6", null, 8, 22, true)]   // the registry says 5.7: a guess
    public void ABuildStringPast58IsNotSupportedYet(string build, string version, string? unsupported, int toc = 8, int? registry = 22, bool guessed = false)
    {
        var game = ContainersGame($"build-{Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(build)))[..8]}-{toc}", toc, registry, 1);
        File.AppendAllText(game.ExePath, build, System.Text.Encoding.Unicode);
        var e = reader.Detect(game)!;
        Assert.Equal((version, guessed, unsupported), (e.Version, e.VersionGuessed, e.Unsupported));
        if (unsupported != null) Assert.Equal(Readiness.Unsupported, new SCSKiller.Core.Planning.Planner().Check(game, e, null, Ff7.Nvidia).Readiness);
    }

    /// <summary>The last guess, from the exe's numeric version, is a newer Unreal when that is past 5.8: guessed, and not
    /// supported yet rather than encrypted (no key makes it so).</summary>
    [Fact]
    public void AGuessPast58IsNotSupportedYet()
    {
        var (game, _) = PakOnlyGame("guess-5-9", 5, 9);
        var e = reader.Detect(game)!;
        Assert.Equal(("5.9", true, "Unreal 5.9 isn't supported yet", false), (e.Version, e.VersionGuessed, e.Unsupported, e.Encrypted));
    }

    /// <summary>A pak-era game whose packages tell UE5 (Ready or Not) but nothing names the minor is a guessed 5.1; the exe's
    /// numeric version names it, still a guess (a studio may stamp the game's), and the asset registry tells it.</summary>
    [Theory]
    [InlineData(null, 1, "5.1", true)]
    [InlineData(null, 3, "5.3", true)]
    [InlineData(22, 1, "5.7", false)]
    public void Ue5ToldFromAPackageIsAGuessUnlessTheRegistryTells(int? registry, int peMinor, string version, bool guessed)
    {
        var (game, paks) = PakOnlyGame($"pkg-ue5-{registry}-{peMinor}", peMinor > 1 ? 5 : 1, peMinor, antiCheat: false);
        WithoutBuildStrings(game.ExePath);
        var package = new byte[64];
        BitConverter.TryWriteBytes(package, 0x9E2A83C1u);
        BitConverter.TryWriteBytes(package.AsSpan(4), -8);
        InlineShadersTests.Pak(Path.Combine(paks, "Proj-Windows.pak"), [("Proj/Content/Maps/Entry.umap", package),
            ("Proj/Content/ShaderArchive-Proj-PCD3D_SM6.ushaderbytecode", [2, 0, 0, 0]), .. Registry(registry)]);
        var e = reader.Detect(game)!;
        Assert.Equal((version, guessed), (e.Version, e.VersionGuessed));
    }

    /// <summary>The exe <see cref="PakOnlyGame"/> copies is this assembly, whose string literals include build strings.</summary>
    static void WithoutBuildStrings(string exe)
    {
        var b = File.ReadAllBytes(exe);
        var marker = System.Text.Encoding.Unicode.GetBytes("++UE");
        for (int i; (i = b.AsSpan().IndexOf(marker)) >= 0;) b[i] = (byte)'-';
        File.WriteAllBytes(exe, b);
    }

    static (string, byte[])[] Registry(int? version) => version is { } v ? [("Proj/AssetRegistry.bin", [.. Convert.FromHexString("E79E7F713A49B0E93291B3880781381B"), .. BitConverter.GetBytes(v), .. new byte[16]])] : [];

    /// <summary>An install whose containers tell .utoc version <paramref name="toc"/> (a header-only global.utoc), whose pak holds
    /// a shader library and the project's asset registry of format <paramref name="registry"/> (none: no registry), and
    /// whose exe's numeric version is 5.<paramref name="peMinor"/> (1: 1.x, no Unreal version).</summary>
    static Game ContainersGame(string name, int toc, int? registry, int peMinor)
    {
        var (game, paks) = PakOnlyGame(name, peMinor > 1 ? 5 : 1, peMinor, antiCheat: false);
        WithoutBuildStrings(game.ExePath);
        File.Delete(Path.Combine(paks, "pakchunk0-Windows.pak"));
        var header = new byte[81];
        header[16] = (byte)toc;
        File.WriteAllBytes(Path.Combine(paks, "global.utoc"), header);
        InlineShadersTests.Pak(Path.Combine(paks, "Proj-Windows.pak"), [("Proj/Content/ShaderArchive-Proj-PCD3D_SM6.ushaderbytecode", [1, 0, 0, 0]), .. Registry(registry)]);
        return game;
    }

    /// <summary>An install of one encrypted-index version 11 pak, an exe whose numeric version is major.minor.1.0 and, with
    /// <paramref name="antiCheat"/>, an XIGNCODE folder.</summary>
    static (Game Game, string Paks) PakOnlyGame(string name, int major, int minor, bool antiCheat = true)
    {
        var install = Ff7.TempDir(name);
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        EncryptedIndexPak11(Path.Combine(paks, "pakchunk0-Windows.pak"));
        if (antiCheat) Directory.CreateDirectory(Path.Combine(install, "XIGNCODE"));
        var exe = Path.Combine(Directory.CreateDirectory(Path.Combine(install, "Proj", "Binaries", "Win64")).FullName, "Proj-Win64-Shipping.exe");
        var pe = File.ReadAllBytes(typeof(UnrealReaderTests).Assembly.Location);
        var info = pe.AsSpan().LastIndexOf(new byte[] { 0xBD, 0x04, 0xEF, 0xFE });   // VS_FIXEDFILEINFO.dwSignature (.rsrc comes last), then dwStrucVersion, dwFileVersionMS and LS
        BitConverter.TryWriteBytes(pe.AsSpan(info + 8), major << 16 | minor);
        BitConverter.TryWriteBytes(pe.AsSpan(info + 12), 1 << 16);
        File.WriteAllBytes(exe, pe);
        Assert.Equal((major, minor, 1), System.Diagnostics.FileVersionInfo.GetVersionInfo(exe) is var v ? (v.FileMajorPart, v.FileMinorPart, v.FileBuildPart) : default);
        return (new Game($"test:{name}", "Proj", Store.Other, install, exe), paks);
    }

    /// <summary>Dead Island 2 ships 4.27's IoStore containers on Dambuster's 4.25 fork: it is that fork, so it gets 4.25's rule,
    /// when its folder, exe or title is exactly its name and only the containers told the version.
    /// Its recording's most used root signature (a VS with 3 constant buffers, a PS with 2) is what that rule serializes,
    /// byte for byte; 4.26's static samplers (s0-s5 in space 1000) made every one of its 8,313 recorded pipelines a miss.</summary>
    [Fact]
    public void DeadIsland2IsDambusters425ForkAndBuildsItsRootSignatures()
    {
        const CUE4Parse.UE4.Versions.EGame Ue427 = CUE4Parse.UE4.Versions.EGame.GAME_UE4_27, Di2 = CUE4Parse.UE4.Versions.EGame.GAME_DeadIsland2;
        Assert.Equal(Di2, UnrealReader.DetectFork(Ue427, 0, "Content", "DeadIsland-WinGDK-Shipping", fromContainers: true));
        Assert.Equal(Di2, UnrealReader.DetectFork(Ue427, 0, "Content", "Game-WinGDK-Shipping", fromContainers: true, title: "Dead Island 2"));
        Assert.Null(UnrealReader.DetectFork(CUE4Parse.UE4.Versions.EGame.GAME_UE5_1, 0, "Content", "DeadIsland-WinGDK-Shipping", fromContainers: true));
        // only on an exact name: a folder that merely starts with it is another game
        Assert.Null(UnrealReader.DetectFork(Ue427, 0, "DeadIsland2-tools", "Tools-Win64-Shipping", fromContainers: true));
        // only when the containers told the version: the exe's build string is exact
        Assert.Null(UnrealReader.DetectFork(Ue427, 0, "Content", "DeadIsland-WinGDK-Shipping", fromContainers: false));
        // a fork of the version the containers tell wins
        Assert.Equal(CUE4Parse.UE4.Versions.EGame.GAME_HogwartsLegacy,
            UnrealReader.DetectFork(Ue427, 0, "Hogwarts Legacy", "HogwartsLegacy", fromContainers: true, title: "Dead Island 2"));
        var rule = SCSKiller.Core.Planning.RootSig.RuleFor(new EngineInfo("Unreal", "4.25", Di2.ToString(), "D3D12", false, null));
        Assert.Equal(SCSKiller.Tests.Planning.Rules.Ue425, rule);
        ShaderInfo S(Stage stage, int cbs) => new($"{stage}", stage, "5_0", 0, new(cbs, 0, 0, 0), [], [], []);
        var rs = SCSKiller.Core.Planning.RootSig.Serialize(
            SCSKiller.Core.Planning.RootSig.Build(rule!, new Dictionary<Stage, ShaderInfo> { [Stage.Vertex] = S(Stage.Vertex, 3), [Stage.Pixel] = S(Stage.Pixel, 2) }, false),
            SCSKiller.Core.Planning.RootSig.StaticSamplers(rule));
        Assert.Equal("8d8fee06ec8b183304303046cf42f66b4d6bcd4d", Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(rs)));
    }

    /// <summary>Wuthering Waves (anti-cheat: its exe isn't read) is Kuro's 4.26 in paks that tell 4.27: that fork by its folder
    /// (Kuro's launcher: "Wuthering Waves Game", Steam: "Wuthering Waves") or store title when only the containers told the
    /// version, and by the exe's 4.26 build string otherwise. Its own rule (ue-kuro) plans it without a recording on NVIDIA;
    /// AMD needs one.</summary>
    [Fact]
    public void WutheringWavesIsKuros426ForkWithItsOwnRule()
    {
        const CUE4Parse.UE4.Versions.EGame Ue426 = CUE4Parse.UE4.Versions.EGame.GAME_UE4_26, Ue427 = CUE4Parse.UE4.Versions.EGame.GAME_UE4_27,
            WuWa = CUE4Parse.UE4.Versions.EGame.GAME_WutheringWaves;
        Assert.Equal(0x041A000Bu, (uint)WuWa);   // 4.26's
        Assert.Equal(WuWa, UnrealReader.DetectFork(Ue427, 0, "Wuthering Waves Game", "Client-Win64-Shipping", fromContainers: true));
        Assert.Equal(WuWa, UnrealReader.DetectFork(Ue427, 0, "Wuthering Waves", "Client-Win64-Shipping", fromContainers: true));
        Assert.Equal(WuWa, UnrealReader.DetectFork(Ue427, 0, "Client", "Client-Win64-Shipping", fromContainers: true, title: "Wuthering Waves"));
        Assert.Equal(WuWa, UnrealReader.DetectFork(Ue426, Ue426, "Wuthering Waves Game", "Client-Win64-Shipping"));
        Assert.Null(UnrealReader.DetectFork(Ue427, 0, "Wuthering Waves Game", "Client-Win64-Shipping"));
        Assert.Null(UnrealReader.DetectFork(Ue427, 0, "Wuthering Waves Tools", "Client-Win64-Shipping", fromContainers: true));
        Assert.Null(UnrealReader.DetectFork(Ue426, Ue426, "Wuthering Waves Tools", "Client-Win64-Shipping"));   // a stock 4.26 game: no prefix match
        Assert.Null(UnrealReader.DetectFork(Ue426, Ue426, "Wuthering", "Wuthering-Win64-Shipping"));
        Assert.Equal(WuWa, UnrealReader.DetectFork(Ue426, Ue426, "Games", "WutheringWaves-Win64-Shipping"));

        var engine = new EngineInfo("Unreal", "4.26", WuWa.ToString(), "D3D12", false, null);
        Assert.Equal("ue-kuro", SCSKiller.Core.Planning.RootSig.RuleFor(engine)?.Id);
        Assert.Equal("ue-kuro-rt", SCSKiller.Core.Planning.RulesFile.Current.RtFor(engine)?.Id);
        Assert.Equal(SCSKiller.Tests.Planning.Rules.Ue426, SCSKiller.Core.Planning.RootSig.RuleFor(engine with { Fork = null }));
        var game = new Game("steam:3513350", "Wuthering Waves", Store.Steam, Ff7.TempDir("wuwa"), "");
        Assert.Equal(Readiness.Ready, new SCSKiller.Core.Planning.Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
        Assert.Equal(Readiness.NeedsRecording, new SCSKiller.Core.Planning.Planner().Check(game, engine, null, Ff7.Amd).Readiness);
    }

    /// <summary>Wuthering Waves asks for its AES key as any encrypted game does, with or without Anti-Cheat Expert in its
    /// install: with the key its own rule plans it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WutheringWavesIsAskedForItsKey(bool antiCheat)
    {
        var install = Path.Combine(Ff7.TempDir($"wuwa-{antiCheat}"), "Wuthering Waves Game");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Client", "Content", "Paks")).FullName;
        EncryptedIndexPak11(Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"));
        var exe = Path.Combine(Directory.CreateDirectory(Path.Combine(install, "Client", "Binaries", "Win64")).FullName, "Client-Win64-Shipping.exe");
        if (antiCheat) File.WriteAllBytes(Path.Combine(install, "ACE-Base64.dll"), []);
        var game = new Game($"test:wuwa-{antiCheat}", "Wuthering Waves", Store.Other, install, exe);
        var e = reader.Detect(game)!;
        Assert.Equal(("4.26", "GAME_WutheringWaves"), (e.Version, e.Fork));
        Assert.True(e.Encrypted);
        Assert.Equal("encrypted game files (needs the game's AES key)", e.Unsupported);
        Assert.Equal(new PlanCheck(Readiness.Unsupported, e.Unsupported!), new SCSKiller.Core.Planning.Planner().Check(game, e, null, Ff7.Nvidia));
    }

    /// <summary>A version 11 .pak whose index is marked encrypted (FPakInfo of 4.26: 5 compression method names). Its index
    /// isn't read before a key is.</summary>
    internal static void EncryptedIndexPak11(string path)
    {
        using var f = new BinaryWriter(File.Create(path));
        f.Write(new byte[16]);
        f.Write(new byte[16]); f.Write((byte)1); f.Write(0x5A6F12E1u); f.Write(11); f.Write(0L); f.Write(16L); f.Write(new byte[20]); f.Write(new byte[5 * 32]);
    }

    /// <summary>A pak-era (version 2) library ends with its shaders' code, back to back after the header.</summary>
    [Fact]
    public void PakEraShaderLibraryEndsAfterItsCode()
    {
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(2);
        w.Write(1); w.Write(new byte[20]);                                                  // shader map hashes
        w.Write(2); w.Write(new byte[40]);                                                  // shader hashes
        w.Write(1); w.Write(0); w.Write(2); w.Write(0); w.Write(0);                         // map entries
        w.Write(2); w.Write(0L); w.Write(3); w.Write(3); w.Write((byte)1); w.Write(3L); w.Write(5); w.Write(5); w.Write((byte)0);   // code entries
        w.Write(0);                                                                         // preloads
        w.Write(2); w.Write(0); w.Write(1);                                                 // indices
        w.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });                                     // code
        var b = o.ToArray();
        Assert.Equal(b.Length, UnrealReader.LibraryEnd(b, 20, ioStore: false));
        var arc = UnrealReader.ReadLibrary("x", b, CUE4Parse.UE4.Versions.EGame.GAME_UE5_6);
        Assert.Equal([4, 5, 6, 7, 8], arc.ShaderCode[1]);
    }

    /// <summary>Dead Island 2 (4.27 fork): the library names its compression format (FString "Zstd") between the header and
    /// the code, and the code is Zstd.</summary>
    [Fact]
    public void ForkNamesItsShaderFormatBeforeTheCode()
    {
        var shader = Enumerable.Range(0, 300).Select(i => (byte)(i % 7)).ToArray();
        var packed = new ZstdSharp.Compressor().Wrap(shader).ToArray();
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(2);
        w.Write(1); w.Write(new byte[20]);                                                  // shader map hashes
        w.Write(1); w.Write(new byte[20]);                                                  // shader hashes
        w.Write(1); w.Write(0); w.Write(1); w.Write(0); w.Write(0);                         // map entries
        w.Write(1); w.Write(0L); w.Write(packed.Length); w.Write(shader.Length); w.Write((byte)0);   // code entries
        w.Write(0);                                                                         // preloads
        w.Write(1); w.Write(0);                                                             // indices
        w.Write(5); w.Write("Zstd\0"u8.ToArray());                                          // the fork's format name
        w.Write(packed);
        var arc = UnrealReader.ReadLibrary("x", o.ToArray(), CUE4Parse.UE4.Versions.EGame.GAME_UE4_27);
        Assert.Equal(packed, arc.ShaderCode[0]);
        Assert.Equal(shader, UnrealReader.Decompress(arc.ShaderCode[0], shader.Length));
    }

    /// <summary>Shader code no codec decompresses throws InvalidDataException, which indexing counts and skips, with the
    /// block's first bytes: they tell the format apart (Oodle 8C, zlib 78, zstd 28B52FFD).</summary>
    [Fact]
    public void UndecodableShaderCodeSaysWhatItStartsWith()
    {
        byte[] code = [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4, 5, 6];
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(1u); w.Write(1);                                    // version 1, one entry
        w.Write(new byte[20]); w.Write(0L); w.Write(code.Length); w.Write(64); w.Write((byte)0);   // hash, offset, size, uncompressed size, frequency
        w.Write(code);
        var arc = UnrealReader.OpenV1(o.ToArray())!;
        var e = Assert.Throws<InvalidDataException>(() => arc.Codes[0]().ToList());
        Assert.EndsWith("(starts DEADBEEF01020304)", e.Message);
    }

    /// <summary>A pak-era library in a pak entry over 2 GB (Ready or Not's is 2.9 GB), which CUE4Parse's extract can't read:
    /// it is indexed and its shader read back from the pak in parts. The pak is sparse: only its header and index take disk
    /// space. With <paramref name="formatName"/>, a fork's "Zstd" FString sits between the header and the code (Dead Island
    /// 2's layout), and a second shader whose code is over 2 GB fills the library out to the size that tells it apart.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AShaderLibraryOver2GbIsReadFromThePakInParts(bool formatName)
    {
        var vs = Fxc("float4 main() : SV_Position { return 0; }", "vs_5_0");
        const uint Filler = 0x9000_0000;   // over int.MaxValue: the reader skips that shader
        var shaders = formatName ? 2 : 1;
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(2);
        w.Write(1); w.Write(new byte[20]);                                                  // shader map hashes
        w.Write(shaders);                                                                   // shader hashes
        for (var i = 0; i < shaders; i++) w.Write(Enumerable.Repeat((byte)(0xCD + i), 20).ToArray());
        w.Write(1); w.Write(0); w.Write(1); w.Write(0); w.Write(0);                         // map entries
        w.Write(shaders); w.Write(0L); w.Write(vs.Length); w.Write(vs.Length); w.Write((byte)0);   // code entries
        if (formatName) { w.Write((long)vs.Length); w.Write(Filler); w.Write(Filler); w.Write((byte)0); }
        w.Write(0);                                                                         // preloads
        w.Write(1); w.Write(0);                                                             // indices
        var header = o.Length;
        if (formatName) { w.Write(5); w.Write("Zstd\0"u8); }
        w.Write(vs);
        var size = formatName ? header + 9 + vs.Length + Filler : (2L << 30) + 4096;
        var install = Ff7.TempDir("large-library");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        var pak = Path.Combine(paks, "Proj-Windows.pak");
        try
        {
            SparsePak(pak, "Proj/Content/ShaderArchive-Proj-PCD3D_SM5.ushaderbytecode", o.ToArray(), size);
            using var provider = Provider(paks);
            var file = Assert.Single(provider.Files.Values);
            Assert.Equal(size, file.Size);
            Assert.Throws<OverflowException>(() => file.Read());

            var game = new Game("test:large", "Large", Store.Other, install, "");
            var index = reader.IndexCore(provider, game, Ue427, null, CancellationToken.None);
            var h = Assert.Single(index.Shaders.Keys);
            Assert.Equal([h], Assert.Single(index.Maps).Shaders);
            var got = new List<byte[]>();
            reader.ReadShadersCore(provider, game, new HashSet<string> { h }, (_, b) => got.Add(b), CancellationToken.None);
            Assert.Equal(vs, Assert.Single(got));
        }
        finally { File.Delete(pak); }
    }

    /// <summary>An encrypted pak entry over 2 GB (Wuthering Waves' SM6 library, whose first 2 KB are encrypted) is read
    /// through CUE4Parse, which decrypts what it reads: the header, then the code in ranges, several shaders to a range and
    /// one past 2 GB in a range of its own.</summary>
    [Fact]
    public void AnEncryptedShaderLibraryOver2GbIsReadThroughCUE4Parse()
    {
        byte[][] code = [Fxc("float4 main() : SV_Position { return 0; }", "vs_5_0"), Fxc("float4 main() : SV_Target { return 1; }", "ps_5_0"),
            Fxc("float4 main() : SV_Target { return 2; }", "ps_5_0")];
        const int Header = 4 + 24 + 64 + 20 + 4 + 3 * 17 + 4 + 16;   // through the indices, below
        var far = (2L << 30) + (1 << 20) - Header;                     // the third shader's offset: its code starts 16-byte aligned past 2 GB
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(2);
        w.Write(1); w.Write(new byte[20]);                                                  // shader map hashes
        w.Write(3); foreach (var b in new byte[] { 0xCD, 0xCE, 0xCF }) w.Write(Enumerable.Repeat(b, 20).ToArray());   // shader hashes
        w.Write(1); w.Write(0); w.Write(3); w.Write(0); w.Write(0);                         // map entries
        w.Write(3);                                                                         // code entries
        w.Write(0L); w.Write(code[0].Length); w.Write(code[0].Length); w.Write((byte)0);
        w.Write((long)code[0].Length); w.Write(code[1].Length); w.Write(code[1].Length); w.Write((byte)0);
        w.Write(far); w.Write(code[2].Length); w.Write(code[2].Length); w.Write((byte)0);
        w.Write(0);                                                                         // preloads
        w.Write(3); w.Write(0); w.Write(1); w.Write(2);                                     // indices
        Assert.Equal(Header, o.Length);
        w.Write(code[0]); w.Write(code[1]);
        var size = Header + far + code[2].Length;
        var install = Ff7.TempDir("large-encrypted");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        var pak = Path.Combine(paks, "Proj-Windows.pak");
        try
        {
            SparsePak(pak, "Proj/Content/ShaderArchive-Proj-PCD3D_SM5.ushaderbytecode", o.ToArray(), size, KeyBytes(1), (Header + far, code[2]));
            using var provider = Provider(paks);
            var file = Assert.Single(provider.Files.Values);
            Assert.True(file.IsEncrypted);
            var game = new Game("test:large-encrypted", "Large", Store.Other, install, "");
            Assert.Throws<InvalidDataException>(() => reader.IndexCore(provider, game, Ue427, null, CancellationToken.None));   // no key: unreadable, not a crash

            ((CUE4Parse.UE4.Pak.PakFileReader)((CUE4Parse.UE4.VirtualFileSystem.VfsEntry)file).Vfs).AesKey = new CUE4Parse.Encryption.Aes.FAesKey(Key(1));
            var index = reader.IndexCore(provider, game, Ue427, null, CancellationToken.None);
            Assert.Equal(3, index.Shaders.Count);
            Assert.Equal(index.Shaders.Keys.Order(), Assert.Single(index.Maps).Shaders.Order());
            var got = new List<byte[]>();
            reader.ReadShadersCore(provider, game, index.Shaders.Keys.ToHashSet(), (_, b) => got.Add(b), CancellationToken.None);
            Assert.Equal(code.Select(Convert.ToHexString).Order(), got.Select(Convert.ToHexString).Order());
        }
        finally { File.Delete(pak); }
    }

    /// <summary>A pak-era game whose exe tells no version (Ready or Not, UE5): a package's LegacyFileVersion tells UE5 (-8) from UE4 (-7).</summary>
    [Theory]
    [InlineData(-8, true)]
    [InlineData(-7, false)]
    public void APackageTellsUe5FromUe4(int legacy, bool ue5)
    {
        var paks = Ff7.TempDir("package-version");
        var head = new byte[64];
        BitConverter.TryWriteBytes(head, 0x9E2A83C1u);
        BitConverter.TryWriteBytes(head.AsSpan(4), legacy);
        SparsePak(Path.Combine(paks, "Proj-Windows.pak"), "Proj/Content/Maps/Entry.umap", head, head.Length);
        Assert.Equal(ue5, UnrealReader.PackagesAreUe5(paks));
    }

    /// <summary>A shader library that doesn't read is skipped and logged with its error: the game's other libraries are
    /// indexed and read. When none reads, the first one's error is thrown.</summary>
    [Fact]
    public void AnUnreadableShaderLibraryIsSkipped()
    {
        var vs = Fxc("float4 main() : SV_Position { return 0; }", "vs_5_0");
        byte[] bad = [2, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F];   // a count that runs past the file
        var install = Ff7.TempDir("unreadable-library");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        InlineShadersTests.Pak(Path.Combine(paks, "Proj-Windows.pak"), ("Proj/Content/ShaderArchive-Bad-PCD3D_SM5.ushaderbytecode", bad),
            ("Proj/Content/ShaderArchive-Bad2-PCD3D_SM5.ushaderbytecode", bad), ("Proj/Content/ShaderArchive-Proj-PCD3D_SM5.ushaderbytecode", PakEraLibrary(vs)));
        var game = new Game("test:unreadable", "Unreadable", Store.Other, install, "");
        var log = new List<string>();
        var got = new List<byte[]>();
        using (var provider = Provider(paks))
        {
            var h = Assert.Single(reader.IndexCore(provider, game, Ue427, new InlineShadersTests.Log(log.Add), CancellationToken.None).Shaders.Keys);
            foreach (var path in new[] { "Bad", "Bad2" }.Select(n => $"Proj/Content/ShaderArchive-{n}-PCD3D_SM5.ushaderbytecode"))
                Assert.Contains(log, l => l.StartsWith($"{path}: unreadable, skipped ({path}: not a shader library", StringComparison.Ordinal));
            reader.ReadShadersCore(provider, game, new HashSet<string> { h }, (_, b) => got.Add(b), CancellationToken.None);
            Assert.Equal(vs, Assert.Single(got));
        }

        InlineShadersTests.Pak(Path.Combine(paks, "Proj-Windows.pak"), ("Proj/Content/ShaderArchive-Bad-PCD3D_SM5.ushaderbytecode", bad));
        using (var provider = Provider(paks))
        {
            Assert.StartsWith("Proj/Content/ShaderArchive-Bad-PCD3D_SM5.ushaderbytecode: not a shader library",
                Assert.Throws<InvalidDataException>(() => reader.IndexCore(provider, game, Ue427, null, CancellationToken.None)).Message);
            Assert.Throws<InvalidDataException>(() => reader.ReadShadersCore(provider, game, new HashSet<string> { "x" }, (_, _) => { }, CancellationToken.None));
        }
    }

    /// <summary>The user's own pipeline cache (UserCaches) adds an exact pipeline map, and leaves the content hash the
    /// community database finds the build by as it was.</summary>
    [Fact]
    public void AUserPipelineCacheAddsMapsOutsideTheContentHash()
    {
        var vs = Fxc("float4 main() : SV_Position { return 0; }", "vs_5_0");
        var install = Ff7.TempDir("user-pipeline-cache");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        InlineShadersTests.Pak(Path.Combine(paks, "Proj-Windows.pak"), ("Proj/Content/ShaderArchive-Proj-PCD3D_SM5.ushaderbytecode", PakEraLibrary(vs)));
        var user = Path.Combine(install, "Proj_PCD3D_SM5.upipelinecache");
        File.WriteAllBytes(user, StablePipelineCacheTests.File(28, (7, 1, [Enumerable.Repeat((byte)0xCD, 20).ToArray()])));
        var game = new Game("test:user-cache", "User cache", Store.Other, install, "");
        var r = new UnrealReader(Ff7.TempDir("user-cache-data"));
        ShaderIndex before, after;
        using (var provider = Provider(paks)) before = r.IndexCore(provider, game, Ue427, null, CancellationToken.None);
        r.UserCaches = _ => [user];
        using (var provider = Provider(paks)) after = r.IndexCore(provider, game, Ue427, null, CancellationToken.None);
        Assert.DoesNotContain(before.Maps, m => m.Library == "UserPipelineCache");
        var map = Assert.Single(after.Maps, m => m.Library == "UserPipelineCache");
        Assert.True(map.IsPipeline);
        Assert.Equal([Assert.Single(after.Shaders.Keys)], map.Shaders);
        Assert.Equal(before.ContentHash, after.ContentHash);
    }

    /// <summary>CUE4Parse's PostMount throws IndexOutOfRangeException on a DefaultGame.ini "+CultureMappings=" without ';'
    /// (Scarlet Nexus: "Index was outside the bounds of the array"): the mount goes on without that config, and the game's
    /// libraries index.</summary>
    [Fact]
    public void AConfigCue4ParseCantParseDoesNotStopTheMount()
    {
        var vs = Fxc("float4 main() : SV_Position { return 0; }", "vs_5_0");
        var install = Ff7.TempDir("culture-mapping");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        InlineShadersTests.Pak(Path.Combine(paks, "Proj-Windows.pak"), ("Proj/Config/DefaultGame.ini", "[Internationalization]\r\n+CultureMappings=\"es-419\"\r\n"u8.ToArray()),
            ("Proj/Content/ShaderArchive-Proj-PCD3D_SM5.ushaderbytecode", PakEraLibrary(vs)));
        using (var provider = Provider(paks))
            Assert.Throws<IndexOutOfRangeException>(provider.PostMount);
        Assert.Single(reader.Index(new Game("test:culture", "Culture", Store.Other, install, ""), Ue427, null, CancellationToken.None).Shaders);
    }

    /// <summary>A key is taken when it opens any of the game's encrypted containers, not only the first in path order: a
    /// chunk may have a key of its own. A container that can't be read (a truncated encrypted .utoc, first in path order)
    /// says nothing about a key.</summary>
    [Fact]
    public void AKeyThatOpensAnyEncryptedContainerIsTaken()
    {
        var install = Ff7.TempDir("keys-any");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        EncryptedPak(Path.Combine(paks, "A.pak"), KeyBytes(1));
        EncryptedPak(Path.Combine(paks, "B.pak"), KeyBytes(33));
        UnreadableEncryptedToc(Path.Combine(paks, "0.utoc"));
        var game = new Game("test:keys-any", "Keys", Store.Other, install, "");
        Assert.False(reader.SetKey(game, Key(65)));
        Assert.True(reader.SetKey(game, Key(33)));
        Assert.True(reader.SetKey(game, Key(1)));
    }

    /// <summary>A readable index with encrypted entries takes any key in CUE4Parse's test: a key counts only when it decrypts
    /// one of the entries into a package, also when the game's other encrypted container can't be read.</summary>
    [Fact]
    public void AKeyMustDecryptAnEntryOfAReadableIndex()
    {
        var install = Ff7.TempDir("keys-entries");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        UnreadableEncryptedToc(Path.Combine(paks, "0.utoc"));
        byte[] package = [.. BitConverter.GetBytes(0x9E2A83C1u), .. new byte[28]];   // PACKAGE_FILE_TAG
        PakOfEncryptedEntries(Path.Combine(paks, "P.pak"), KeyBytes(1), ("Proj/Content/a.uasset", package),
            ("Proj/Content/ShaderArchive-Proj-PCD3D_SM5.ushaderbytecode", new byte[32]));
        var game = new Game("test:keys-entries", "Keys", Store.Other, install, "");
        Assert.False(reader.SetKey(game, Key(65)));
        Assert.True(reader.SetKey(game, Key(1)));
    }

    /// <summary>A pak whose index is encrypted under a non-zero key GUID (the footer's EncryptionKeyGuid): its key is taken
    /// and the full mount opens the pak with it, so its shader library is indexed. An encrypted pak whose index lies past
    /// its end stops neither.</summary>
    [Fact]
    public void AKeyUnderANonZeroGuidIsTakenAndMounted()
    {
        var vs = Fxc("float4 main() : SV_Position { return 0; }", "vs_5_0");
        var install = Ff7.TempDir("keys-guid");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        EncryptedPak(Path.Combine(paks, "Proj-Windows.pak"), KeyBytes(1), guid: KeyBytes(200)[..16],
            files: ("Proj/Content/ShaderArchive-Proj-PCD3D_SM5.ushaderbytecode", PakEraLibrary(vs)));
        var broken = Path.Combine(paks, "Broken.pak");
        EncryptedPak(broken, KeyBytes(1));
        var bytes = File.ReadAllBytes(broken);
        BitConverter.TryWriteBytes(bytes.AsSpan(bytes.Length - 36), 1L << 40);   // the footer's index offset
        File.WriteAllBytes(broken, bytes);
        var game = new Game("test:keys-guid", "Guid", Store.Other, install, "");
        Assert.False(reader.SetKey(game, Key(65)));
        Assert.True(reader.SetKey(game, Key(1)));
        Assert.Single(reader.Index(game, Ue427, null, CancellationToken.None).Shaders);
    }

    /// <summary>Dead by Daylight XORs its decrypted pak index with 28 bytes from the pak footer (CUE4Parse's decryption for
    /// that fork): the key check decrypts as the mount does, so the right key is taken, a wrong one refused, and the mount
    /// reads the game's shader library.</summary>
    [Fact]
    public void AKeyOfAForkWithItsOwnDecryptionIsTaken()
    {
        var install = Directory.CreateDirectory(Path.Combine(Ff7.TempDir("keys-dbd"), "Dead by Daylight")).FullName;
        var paks = Directory.CreateDirectory(Path.Combine(install, "DeadByDaylight", "Content", "Paks")).FullName;
        var exe = Path.Combine(install, "DeadByDaylight.exe");
        File.WriteAllBytes(exe, [.. "MZ"u8, .. System.Text.Encoding.Unicode.GetBytes("++UE5+Release-5.7")]);
        var vs = Fxc("float4 main() : SV_Position { return 0; }", "vs_5_0");
        EncryptedPak(Path.Combine(paks, "pakchunk0-Windows.pak"), KeyBytes(1), dbd: KeyBytes(100)[..28],
            files: ("DeadByDaylight/Content/ShaderArchive-DeadByDaylight-PCD3D_SM5.ushaderbytecode", PakEraLibrary(vs)));
        var game = new Game("test:keys-dbd", "Dead by Daylight", Store.Other, install, exe);
        Assert.False(reader.SetKey(game, Key(65)));
        Assert.True(reader.SetKey(game, Key(1)));
        var e = reader.Detect(game)!;
        Assert.Equal(nameof(CUE4Parse.UE4.Versions.EGame.GAME_DeadByDaylight), e.Fork);
        Assert.False(e.Encrypted);
        Assert.Single(reader.Index(game, e, null, CancellationToken.None).Shaders);
    }

    /// <summary>A readable index whose shader library entry is encrypted (Sea of Thieves): once its key is taken the game no
    /// longer reads as encrypted, and the full mount reads the library with that key.</summary>
    [Fact]
    public void AKeyOpensTheEncryptedEntriesOfAReadableIndex()
    {
        var vs = Fxc("float4 main() : SV_Position { return 0; }", "vs_5_0");
        var install = Ff7.TempDir("keys-readable");
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        byte[] package = [.. BitConverter.GetBytes(0x9E2A83C1u), .. new byte[28]];   // PACKAGE_FILE_TAG
        PakOfEncryptedEntries(Path.Combine(paks, "P.pak"), KeyBytes(1), ("Proj/Content/a.uasset", package),
            ("Proj/Content/ShaderArchive-Proj-PCD3D_SM5.ushaderbytecode", PakEraLibrary(vs)));
        var game = new Game("test:keys-readable", "Readable", Store.Other, install, Path.Combine(install, "absent.exe"));
        Assert.True(reader.Detect(game)!.Encrypted);
        Assert.True(reader.SetKey(game, Key(1)));
        Assert.False(reader.Detect(game)!.Encrypted);
        Assert.Single(reader.Index(game, Ue427, null, CancellationToken.None).Shaders);
    }

    internal static byte[] KeyBytes(int from) => [.. Enumerable.Range(from, 32).Select(i => (byte)i)];
    internal static string Key(int from) => "0x" + Convert.ToHexString(KeyBytes(from));

    /// <summary>A truncated .utoc whose header marks it encrypted and indexed (FIoStoreTocHeader.ContainerFlags at byte 80):
    /// the survey lists it, CUE4Parse can't open it.</summary>
    static void UnreadableEncryptedToc(string path)
    {
        var b = new byte[144];
        "-==--==--==--==-"u8.CopyTo(b);
        b[80] = 2 | 8;
        File.WriteAllBytes(path, b);
    }

    /// <summary>A version 7 .pak with a readable index whose entries are each encrypted with <paramref name="key"/> (AES-256,
    /// ECB), padded to whole AES blocks.</summary>
    static void PakOfEncryptedEntries(string path, byte[] key, params (string Name, byte[] Data)[] files)
    {
        static void Entry(BinaryWriter w, long offset, long size) { w.Write(offset); w.Write(size); w.Write(size); w.Write(0); w.Write(new byte[20]); w.Write((byte)1); w.Write(0u); }
        static void FString(BinaryWriter w, string s) { w.Write(s.Length + 1); w.Write(System.Text.Encoding.ASCII.GetBytes(s)); w.Write((byte)0); }
        using var aes = Aes.Create();
        aes.Key = key;
        using var f = new BinaryWriter(File.Create(path));
        var at = new List<long>();
        foreach (var (_, data) in files) { at.Add(f.BaseStream.Position); Entry(f, 0, data.Length); f.Write(aes.EncryptEcb([.. data, .. new byte[-data.Length & 15]], PaddingMode.None)); }
        using var index = new MemoryStream();
        var iw = new BinaryWriter(index);
        FString(iw, "../../../");
        iw.Write(files.Length);
        for (var i = 0; i < files.Length; i++) { FString(iw, files[i].Name); Entry(iw, at[i], files[i].Data.Length); }
        var indexAt = f.BaseStream.Position;
        f.Write(index.ToArray());
        f.Write(new byte[16]); f.Write((byte)0); f.Write(0x5A6F12E1u); f.Write(7); f.Write(indexAt); f.Write(index.Length); f.Write(new byte[20]);
    }

    /// <summary>A version 7 .pak whose index is encrypted with <paramref name="key"/> (AES-256, ECB) under the key GUID
    /// <paramref name="guid"/> (zero if null), holding <paramref name="files"/> unencrypted (one 4-byte package if none). With
    /// <paramref name="dbd"/>, in Dead by Daylight's layout: the footer carries those 28 bytes, which the index is XORed with
    /// before it is encrypted, and room for 5 compression method names.</summary>
    internal static void EncryptedPak(string path, byte[] key, byte[]? guid = null, byte[]? dbd = null, params (string Name, byte[] Data)[] files)
    {
        static void Entry(BinaryWriter x, long offset, long size) { x.Write(offset); x.Write(size); x.Write(size); x.Write(0); x.Write(new byte[20]); x.Write((byte)0); x.Write(0u); }
        static void FString(BinaryWriter x, string s) { x.Write(s.Length + 1); x.Write(System.Text.Encoding.ASCII.GetBytes(s)); x.Write((byte)0); }
        if (files.Length == 0) files = [("Proj/Content/a.uasset", new byte[4])];
        using var f = new BinaryWriter(File.Create(path));
        var at = new List<long>();
        foreach (var (_, data) in files) { at.Add(f.BaseStream.Position); Entry(f, 0, data.Length); f.Write(data); }
        using var index = new MemoryStream();
        var iw = new BinaryWriter(index);
        FString(iw, "../../../");
        iw.Write(files.Length);
        for (var i = 0; i < files.Length; i++) { FString(iw, files[i].Name); Entry(iw, at[i], files[i].Data.Length); }
        index.SetLength((index.Length + 15) & ~15);
        var plain = index.ToArray();
        if (dbd != null) for (var i = 0; i < plain.Length; i++) plain[i] ^= dbd[i % dbd.Length];
        using var aes = Aes.Create();
        aes.Key = key;
        var encrypted = aes.EncryptEcb(plain, PaddingMode.None);
        var indexAt = f.BaseStream.Position;
        f.Write(encrypted);
        f.Write(guid ?? new byte[16]); f.Write((byte)1); f.Write(0x5A6F12E1u); f.Write(7); f.Write(indexAt); f.Write((long)encrypted.Length); f.Write(new byte[20]);
        if (dbd != null) { f.Write(dbd); f.Write(0u); f.Write(new byte[5 * 32]); }
    }

    static readonly EngineInfo Ue427 = new("Unreal", "4.27", null, "D3D12", false, null);

    static CUE4Parse.FileProvider.DefaultFileProvider Provider(string paks)
    {
        var provider = new CUE4Parse.FileProvider.DefaultFileProvider(paks, SearchOption.TopDirectoryOnly,
            new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount();
        return provider;
    }

    /// <summary>A pak-era (version 2) library of one shader map holding <paramref name="code"/>, uncompressed.</summary>
    static byte[] PakEraLibrary(byte[] code)
    {
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(2);
        w.Write(1); w.Write(new byte[20]);                                                      // shader map hashes
        w.Write(1); w.Write(Enumerable.Repeat((byte)0xCD, 20).ToArray());                       // shader hashes
        w.Write(1); w.Write(0); w.Write(1); w.Write(0); w.Write(0);                             // map entries
        w.Write(1); w.Write(0L); w.Write(code.Length); w.Write(code.Length); w.Write((byte)0);  // code entries
        w.Write(0);                                                                             // preloads
        w.Write(1); w.Write(0);                                                                 // indices
        w.Write(code);
        return o.ToArray();
    }

    /// <summary>A version 7 .pak holding one uncompressed file of <paramref name="size"/> bytes that start with
    /// <paramref name="head"/>, and holds <paramref name="more"/> at its offset (16-byte aligned) when given, both encrypted with
    /// <paramref name="key"/> when given (AES-256, ECB); the rest is a sparse hole.</summary>
    static void SparsePak(string path, string name, byte[] head, long size, byte[]? key = null, (long At, byte[] Data)? more = null)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        Assert.True(DeviceIoControl(f.SafeFileHandle, 0x000900C4 /* FSCTL_SET_SPARSE */, 0, 0, 0, 0, out _, 0), $"sparse files: error {Marshal.GetLastWin32Error()}");
        var w = new BinaryWriter(f);
        void Entry(BinaryWriter x) { x.Write(0L); x.Write(size); x.Write(size); x.Write(0); x.Write(new byte[20]); x.Write((byte)(key != null ? 1 : 0)); x.Write(0u); }
        Entry(w);
        byte[] Sealed(byte[] b)
        {
            if (key == null) return b;
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.EncryptEcb([.. b, .. new byte[-b.Length & 15]], PaddingMode.None);
        }
        w.Write(Sealed(head));
        if (more is { } m)
        {
            f.Position = 53 + m.At;
            w.Write(Sealed(m.Data));
        }
        f.Position = 53 + (key != null ? (size + 15) & ~15 : size);   // an encrypted entry fills whole AES blocks
        var indexAt = f.Position;
        using var index = new MemoryStream();
        var iw = new BinaryWriter(index);
        foreach (var s in new[] { "../../../", name }) { iw.Write(s.Length + 1); iw.Write(System.Text.Encoding.ASCII.GetBytes(s)); iw.Write((byte)0); if (s == "../../../") iw.Write(1); }
        Entry(iw);
        w.Write(index.ToArray());
        w.Write(new byte[16]); w.Write((byte)0); w.Write(0x5A6F12E1u); w.Write(7); w.Write(indexAt); w.Write(index.Length); w.Write(new byte[20]);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, nint inBuf, int inSize, nint outBuf, int outSize, out int returned, nint overlapped);
}
