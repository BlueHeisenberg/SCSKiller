using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;
using ZstdSharp;

namespace SCSKiller.Core.Dagor;

/// <summary>Gaijin's Dagor Engine (War Thunder): the shader dumps compiledShaders\game.ps50.shdump.bin (DirectX 11) and
/// gameDX12.ps50.shdump.bin (DirectX 12; game.compatibility* in the game's compatibility mode), dump version 11.3 only
/// (DagorEngine's shader_layout.h). A 64-byte header ("VSPS", "dump", version), then the zstd-compressed main layout; in it
/// every entry is a zstd frame of the dump's own dictionary: a pixel or compute shader, or a vertex shader with the hull,
/// domain and geometry shaders it is drawn with. DirectX 11 entries are plain DXBC back to back, one map each (platform
/// PCD3D_SM5). A DirectX 12 entry's metadata holds a dxil::ShaderHeader per stage and where its DXIL is; the root signature
/// is built from those headers (<see cref="DagorRootSig"/>), and every vertex/pixel and compute pass of the shader classes
/// is one exact pipeline (platform PCD3D_SM6); a pass without a pixel shader is drawn with the driver's null pixel shader
/// (<see cref="NullPixelShader"/>). GraphicsApi: config.blk's video/driver ("auto" or none: "D3D11 or D3D12").
/// EngineInfo: Family "Dagor", Version "11.3".</summary>
public sealed class DagorReader : IEngineReader
{
    public const string Family = "Dagor", Version = "11.3", Platform = "PCD3D_SM5", Platform12 = "PCD3D_SM6";

    const int HeaderSize = 64, MaxShader = 256 << 20, NoShader = 0xFFFF, MaxNullShader = 64 << 10;
    const long MaxExe = 2L << 30;
    // ScriptedShadersBinDump fields of version 11.3: vprCount, fshCount, classes, uncompressed_shader_sizes, shaders_metadata, shaders, dictionary
    const int VprCount = 16, FshCount = 20, Classes = 140, Sizes = 172, Metadata = 180, Codes = 188, Dictionary = 196;
    // sizes of ShaderClass, ShaderCode (its passes at 20) and Pass (its ShRef pointer first: vprId, fshId)
    const int ClassSize = 108, CodeSize = 72, CodePasses = 20, PassSize = 20;
    // dxil::StoredShaderType; dxil::Shader up to its bytecode offset and size
    const int SingleShader = 0, CombinedVertexShader = 1, ShaderSize = DagorRootSig.HeaderSize + 8;

    public int ReaderVersion(EngineInfo engine) => 3;

    public EngineInfo? Detect(Game game)
    {
        if (!Directory.Exists(Path.Combine(game.InstallDir, "compiledShaders"))) return null;
        if (Dumps(game).FirstOrDefault().Path is not { } path) return null;
        Span<byte> h = stackalloc byte[12];
        try
        {
            using var f = Open(path);
            if (f.ReadAtLeast(h, 12, throwOnEndOfStream: false) < 12 || !h.StartsWith("VSPSdump"u8)) return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var version = Encoding.ASCII.GetString(h[8..]);
        // the driver caches per exe name: a store that found the launcher would warm the wrong one
        var exe = GaijinSource.Exe(game.InstallDir);
        return new EngineInfo(Family, version, CbvRanges(game) ? CbvRangesFork : null, Api(game), false,
            version != Version ? $"shader dump version {version}: SCSKiller reads version {Version}"
            : exe != null && !string.Equals(exe, Path.GetFullPath(game.ExePath), StringComparison.OrdinalIgnoreCase)
                ? $"listed with {Path.GetFileName(game.ExePath)}, but the game runs {Path.GetRelativePath(game.InstallDir, exe)}" : null);
    }

    public string DetectStamp(Game game, EngineInfo? engine)
    {
        var f = new FileInfo(Path.Combine(game.InstallDir, "config.blk"));
        return (f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-") + (CbvRanges(game) ? "|cbv" : "");
    }

    /// <summary>EngineInfo.Fork of a game whose root signatures put constant buffers in descriptor tables
    /// (dx12/rootSignaturesUsesCBVDescriptorRanges, plan-rules.json's dagor-cbv-ranges rule).</summary>
    public const string CbvRangesFork = "cbv-ranges";

    /// <summary>The engine's dx12/rootSignaturesUsesCBVDescriptorRanges: flag 1 of cache\dx12.cache's header (CX12, version 34),
    /// which the game writes after a DirectX 12 session. Without one, on: War Thunder's settings turn it on (its cache says so).</summary>
    internal static bool CbvRanges(Game game)
    {
        Span<byte> h = stackalloc byte[32];
        try
        {
            using var f = Open(Path.Combine(game.InstallDir, "cache", "dx12.cache"));
            if (f.ReadAtLeast(h, 32, throwOnEndOfStream: false) == 32 && h.StartsWith("CX12"u8) && BinaryPrimitives.ReadUInt32LittleEndian(h[4..]) == 34)
                return (BinaryPrimitives.ReadUInt32LittleEndian(h[28..]) & 1) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>The dumps config.blk's compatibility mode selects (switching it changes which shaders the game loads), and the
    /// constant-buffer mode (<see cref="CbvRanges"/>: it changes every root signature the plan builds).</summary>
    public string IndexStamp(Game game) =>
        !Directory.Exists(Path.Combine(game.InstallDir, "compiledShaders")) ? ""
        : string.Join(';', Dumps(game).Select(d => new FileInfo(d.Path)).Select(f => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}")) + (CbvRanges(game) ? "|cbv" : "");

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var maps = new Dictionary<string, ShaderMap>();
        var platforms = new SortedSet<string>(StringComparer.Ordinal);
        var stamp = new StringBuilder();
        var lanes = new Dictionary<string, string>();   // shaders a 32-lane GPU can't run (CarvedReader.LanePlatform)
        var bad = 0;
        string? Add(byte[] c, byte[]? header)
        {
            var sha = Convert.ToHexStringLower(SHA1.HashData(c));
            if (Dxbc.WaveLanes(c) is { } l && (l.Min > 32 || l.Max < 32)) lanes[sha] = $"{Platform12} wave{l.Min}{(l.Max != l.Min ? $"-{l.Max}" : "")}";
            try
            {
                if (!shaders.ContainsKey(sha) && ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                    shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings), EngineHeader = header };
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; } // valid container, odd program: not usable
            return shaders.ContainsKey(sha) ? sha : null;
        }
        void Map(string rel, string platform, List<string> shas, bool pipeline)
        {
            var h = CarvedReader.Sha1Hex($"{platform}|{string.Join(',', shas)}");
            if (maps.TryAdd(h, new ShaderMap(h, rel, platform, shas, pipeline))) platforms.Add(platform);
        }

        foreach (var (path, dx12) in Dumps(game))
        {
            var rel = Path.GetRelativePath(game.InstallDir, path);
            var file = new FileInfo(path);
            stamp.Append($"{rel}|{file.Length}|{file.LastWriteTimeUtc.Ticks}\n");
            var body = Body(Read(path));
            if (!dx12)
            {
                var n = 0;
                foreach (var e in Entries(body, ct))
                {
                    n++;
                    if (Dxbc.Containers(e.Blob).Select(c => Add(c.Container, null)).OfType<string>().ToList() is { Count: > 0 } shas) Map(rel, Platform, shas, false);
                }
                log?.Report($"{rel}: {n} entries");
                continue;
            }
            int skipped = 0, depthOnly = 0, unusable = 0, pipelines = 0;
            string? nullPs = null;
            var nullRead = false;
            string? NullPs()
            {
                if (nullRead) return nullPs;
                nullRead = true;
                if (NullPixelShader(game, ct) is not (FileInfo f, byte[] c)) return null;
                stamp.Append($"{Path.GetRelativePath(game.InstallDir, f.FullName)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n");
                return nullPs = Add(c, null);
            }
            var byEntry = Entries(body, ct).Select(e =>
            {
                if (Stages(e) is not { } stages) { skipped++; return null; }
                var shas = stages.Select(s => Add(s.Container, s.Header)).ToList();
                return shas.Contains(null) ? null : shas.OfType<string>().ToList();
            }).ToList();
            var vprCount = (int)U(body, VprCount);
            foreach (var (vpr, fsh) in Passes(body, vprCount, byEntry.Count - vprCount))
            {
                List<string>?[] parts;
                if (fsh == NoShader)
                {
                    depthOnly++;
                    if (NullPs() is not { } n) continue;
                    parts = [byEntry[vpr], [n]];
                }
                else parts = vpr == NoShader ? [byEntry[vprCount + fsh]] : [byEntry[vpr], byEntry[vprCount + fsh]];
                if (parts.Any(p => p == null)) { unusable++; continue; }
                var shas = parts.SelectMany(p => p!).ToList();
                Map(rel, shas.Select(lanes.GetValueOrDefault).FirstOrDefault(x => x != null) ?? Platform12, shas, true);
                pipelines++;
            }
            log?.Report($"{rel}: {byEntry.Count} entries ({skipped} of a kind not read: stream output, mesh, library), {pipelines} pipelines in its passes"
                + $" ({depthOnly} drawn with the driver's null pixel shader{(depthOnly > 0 && nullPs == null ? ", left out: the exe has no single one" : "")}; {unusable} with an unparseable shader left out)");
        }
        log?.Report($"{shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")} -> {maps.Count} maps ({sw.Elapsed.TotalSeconds:F1}s)");
        stamp.Append(engine.Fork);   // the root-signature rule: plans built under the other one are stale
        return new ShaderIndex(CarvedReader.Sha1Hex(stamp.ToString()), [.. platforms], shaders, maps.Values.ToList());
    }

    /// <summary>Decompresses the dumps again (a few seconds) and serves each requested shader once.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        var left = new HashSet<string>(sha1s);
        foreach (var (path, dx12) in Dumps(game))
            foreach (var e in Entries(Body(Read(path)), ct))
            {
                var containers = dx12 ? Stages(e)?.Select(s => s.Container) ?? [] : Dxbc.Containers(e.Blob).Select(c => c.Container);
                foreach (var c in containers)
                    if (Convert.ToHexStringLower(SHA1.HashData(c)) is var sha && left.Remove(sha))
                    {
                        sink(sha, c);
                        if (left.Count == 0) return;
                    }
            }
        if (Dumps(game).Any(d => d.Dx12) && NullPixelShader(game, ct) is (_, byte[] n) && Convert.ToHexStringLower(SHA1.HashData(n)) is var h && left.Contains(h))
            sink(h, n);
    }

    /// <summary>The exe and the pixel shader Dagor's DirectX 12 driver puts in a pipeline whose pass has none (drv3d_DX12
    /// initNullPixelShader, an empty main, its dxil::ShaderHeader all zero): the exe's one DXIL pixel shader with no inputs,
    /// outputs or resources. Null when the exe can't be read or holds none or several.</summary>
    internal static (FileInfo Exe, byte[] Container)? NullPixelShader(Game game, CancellationToken ct)
    {
        var exe = new FileInfo(GaijinSource.Exe(game.InstallDir) ?? game.ExePath);
        if (!exe.Exists || exe.Length > MaxExe) return null;   // its length and time are kept from here for the stamp
        byte[]? found = null;
        var n = 0;
        CarvedReader.Scan(exe, new byte[CarvedReader.Block], [(0, exe.Length)], (_, c) =>
        {
            if (Dxbc.Part(c, "DXIL"u8).IsEmpty) return;
            try
            {
                if (ShaderContainer.Parse(c, "", new(0, 0, 0, 0)) is { Stage: Stage.Pixel, Inputs.Count: 0, Outputs.Count: 0, Bindings.Count: 0 } && n++ == 0)
                    found = c.ToArray();
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { }
        }, ct, MaxNullShader);
        return n == 1 ? (exe, found!) : null;
    }

    /// <summary>The dumps of the mode config.blk selects that exist: DirectX 11's, then DirectX 12's.</summary>
    static IEnumerable<(string Path, bool Dx12)> Dumps(Game game)
    {
        var compatibility = Video(game, "compatibilityMode") is "yes" or "true" or "1";
        var dir = Path.Combine(game.InstallDir, "compiledShaders");
        foreach (var (name, dx12) in new[] { (compatibility ? "game.compatibility" : "game", false), (compatibility ? "game.compatibilityDX12" : "gameDX12", true) })
            if (Path.Combine(dir, name + ".ps50.shdump.bin") is var p && File.Exists(p)) yield return (p, dx12);
    }

    /// <summary>The main layout of a version 11.3 dump, decompressed.</summary>
    internal static byte[] Body(byte[] dump)
    {
        if (dump.Length < HeaderSize + 12 || !dump.AsSpan().StartsWith("VSPSdump11.3"u8)) throw new InvalidDataException("not a version 11.3 shader dump");
        var (at, n) = List(dump, HeaderSize + 4, 1);
        var main = new byte[U(dump, HeaderSize)];
        using var z = new Decompressor();
        if (Unwrap(z, dump.AsSpan(at, n), main) != main.Length) throw new InvalidDataException("the dump's body doesn't decode to its size");
        return main;
    }

    /// <summary>One dump entry: its decompressed bytes (DX11: the containers back to back; DX12: what its metadata points into).</summary>
    internal sealed record Entry(byte[] Blob, byte[] Metadata);

    /// <summary>Every entry of a dump's body, vertex entries first.</summary>
    internal static IEnumerable<Entry> Entries(byte[] main, CancellationToken ct)
    {
        var count = (long)U(main, VprCount) + U(main, FshCount);
        var (sizes, ns) = List(main, Sizes, 4);
        var (metas, nm) = List(main, Metadata, 8);
        var (codes, nc) = List(main, Codes, 8);
        var (dict, nd) = List(main, Dictionary, 1);
        if (ns != count || nc != count || nm != count) throw new InvalidDataException($"{count} shaders, but {ns} sizes, {nm} metadata and {nc} codes");
        using var zd = new Decompressor();
        zd.LoadDictionary(main.AsSpan(dict, nd));
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (code, len) = List(main, codes + 8 * i, 1);
            var (meta, ml) = List(main, metas + 8 * i, 1);
            var size = U(main, sizes + 4 * i);
            if (size > MaxShader) throw new InvalidDataException($"shader {i}: {size} bytes");
            var bytes = new byte[(size + 3) & ~3u];   // the engine rounds each shader up to 4 bytes
            var got = Unwrap(zd, main.AsSpan(code, len), bytes);
            yield return new Entry(bytes[..got], main[meta..(meta + ml)]);
        }
    }

    /// <summary>A DX12 entry's stages from its dxil::ShaderContainer metadata: one shader, or a vertex shader with its hull,
    /// domain and geometry shaders (dxil::VertexShaderPipeline: four i32 pointers, null = 0); each a dxil::Shader (its
    /// 96-byte header, then the bytecode's offset and size in the entry). Null for stream output, mesh and library entries.</summary>
    internal static List<(byte[] Header, byte[] Container)>? Stages(Entry e)
    {
        var m = e.Metadata;
        if (m.Length < 32) throw new InvalidDataException("shader metadata too short");
        var (kind, flags) = (BinaryPrimitives.ReadUInt16LittleEndian(m), BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(2)));
        if ((flags & 1) != 0 || kind is not (SingleShader or CombinedVertexShader)) return null;
        var (data, n) = List(m, 24, 1);
        var at = kind == SingleShader ? [data] : new[] { 0, 4, 8, 12 }.Select(k => Pointer(m, data + k, data + n)).Where(p => p != 0).ToArray();
        return at.Select(p =>
        {
            if (p < data || p + ShaderSize > data + n) throw new InvalidDataException("shader header outside its metadata");
            var (offset, size) = (U(m, p + DagorRootSig.HeaderSize), U(m, p + DagorRootSig.HeaderSize + 4));
            if ((long)offset + size > e.Blob.Length || !Dxbc.Valid(e.Blob.AsSpan((int)offset, (int)size))) throw new InvalidDataException("shader bytecode outside its entry");
            return (m[p..(p + DagorRootSig.HeaderSize)], e.Blob[(int)offset..(int)(offset + size)]);
        }).ToList();
    }

    /// <summary>A bindump pointer (i32 from the field itself, 0 = null), checked to stay below <paramref name="end"/>.</summary>
    static int Pointer(byte[] b, int field, int end)
    {
        if (field + 4 > end) throw new InvalidDataException("pointer outside its layout");
        var rel = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(field));
        return rel == 0 ? 0 : (long)field + rel is var p and >= 0 && p < end ? (int)p : throw new InvalidDataException("pointer outside its layout");
    }

    /// <summary>Every distinct (vertex, pixel) and (none, compute) pass of the shader classes' variants: vprId and fshId of
    /// each ShRef (0xFFFF = none; a compute shader is a pixel-side entry with no vertex entry).</summary>
    internal static HashSet<(int Vpr, int Fsh)> Passes(byte[] main, int vprCount, int fshCount)
    {
        var passes = new HashSet<(int, int)>();
        var (classes, nClasses) = List(main, Classes, ClassSize);
        for (var c = 0; c < nClasses; c++)
        {
            var (codes, nCodes) = List(main, classes + c * ClassSize, CodeSize);
            for (var k = 0; k < nCodes; k++)
            {
                var (ps, nPasses) = List(main, codes + k * CodeSize + CodePasses, PassSize);
                for (var p = 0; p < nPasses; p++)
                {
                    var r = Pointer(main, ps + p * PassSize, main.Length);
                    if (r == 0) continue;
                    if (r + 4 > main.Length) throw new InvalidDataException("pass outside the dump");
                    int vpr = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(r)), fsh = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(r + 2));
                    if (vpr != NoShader && vpr >= vprCount || fsh != NoShader && fsh >= fshCount || vpr == NoShader && fsh == NoShader)
                        throw new InvalidDataException($"pass names shader {vpr}/{fsh}, the dump has {vprCount}/{fshCount}");
                    passes.Add((vpr, fsh));
                }
            }
        }
        return passes;
    }

    static int Unwrap(Decompressor z, ReadOnlySpan<byte> src, Span<byte> dest)
    {
        try { return z.Unwrap(src, dest); }
        catch (ZstdException e) { throw new InvalidDataException("the dump doesn't decode: " + e.Message, e); }
    }

    /// <summary>A bindump list field: an i32 offset from the field itself and a u32 count; its elements' start, checked to fit.</summary>
    static (int At, int Count) List(byte[] b, int field, int elementSize)
    {
        if (field < 0 || field + 8 > b.Length) throw new InvalidDataException($"list field at {field} is outside the dump");
        long at = field + (long)BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(field)), n = U(b, field + 4);
        if (n > 0 && (at < 0 || at + n * elementSize > b.Length)) throw new InvalidDataException($"list at {field} is outside the dump");
        return ((int)at, (int)n);
    }

    static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    static string Api(Game game) => Video(game, "driver") switch
    {
        "dx11" => "D3D11",
        "dx12" => "D3D12",
        "vulkan" => "Vulkan",
        _ => UnrealRhi.Ambiguous,   // "auto": DirectX 12 when the game's settings prefer it for this GPU
    };

    /// <summary>A parameter of config.blk's top-level video block, lower case; null when it isn't there or the file can't be read.</summary>
    internal static string? Video(Game game, string name)
    {
        string text;
        try { text = File.ReadAllText(Path.Combine(game.InstallDir, "config.blk")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var block = VideoBlock(text);
        if (block == null) return null;
        var m = Regex.Match(block, $@"(?:^|[\s;]){Regex.Escape(name)}:[a-z0-9]+\s*=\s*""?([^""\s;}}]*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>The top-level text of the first "video{...}" block, its nested blocks left out.</summary>
    static string? VideoBlock(string text)
    {
        var m = Regex.Match(text, @"(?:^|[\s;])video\s*\{");
        if (!m.Success) return null;
        var sb = new StringBuilder();
        var depth = 1;
        for (var i = m.Index + m.Length; i < text.Length && depth > 0; i++)
        {
            var c = text[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (depth == 1) sb.Append(c);
            if (c is '{' or '}') sb.Append(' ');
        }
        return sb.ToString();
    }

    static byte[] Read(string path)
    {
        using var f = Open(path);
        var b = new byte[f.Length];
        f.ReadExactly(b);
        return b;
    }

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
