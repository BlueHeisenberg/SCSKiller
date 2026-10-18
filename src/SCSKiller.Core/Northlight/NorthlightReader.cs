using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Northlight;

/// <summary>Northlight (Remedy; Control): the DX12 effect files of data\shaders\build\pc_dxil, raw and uncompressed
/// (<see cref="Records"/>): DXBC SM5.1 vertex, pixel and compute shaders, and DXIL ray tracing libraries (lib_6_3). The
/// resource packs (.rmdp) hold no shaders. Every distinct pipeline record (VS+PS, or a lone CS) is one IsPipeline map, the
/// libraries one pool. Shaders carry no root signature: the engine builds one graphics and one compute root signature in code
/// (plan-rules.json's northlight rule). The DX11 set in data\shaders\build\pc_dx11 (SM 5.0, same layout) is
/// indexed under <see cref="Platform11"/>, one map per file: D3D11 shaders only, never part of a D3D12 pipeline.
/// EngineInfo: Family "Northlight", Version "DX12"; GraphicsApi "D3D11 or D3D12" when the DX11 set is there. Northlight 2
/// (.rmdtoc archives: Alan Wake 2, CONTROL Resonant) is left to the carver, which reports it as its own family.</summary>
public sealed class NorthlightReader : IEngineReader
{
    public const string Family = "Northlight", Version = "DX12";
    public const string Libraries = "libraries";
    public const string Platform11 = "D3D11";
    const string Dir = @"data\shaders\build\pc_dxil", Dir11 = @"data\shaders\build\pc_dx11";

    /// <summary>Effect files Detect reads, smallest first, to find one VS+PS record.</summary>
    const int DetectFiles = 8;

    /// <summary>Bounds on what the index takes in (Control: 53 MB for its largest effect file, 9,208 shaders in all, 4,710
    /// records): a bigger file is skipped, more records (of any kind) are left out with a warning.</summary>
    const long MaxFile = 512L << 20;
    const int MaxShadersPerFile = 1 << 18;
    internal int MaxRecords { get; init; } = 1 << 20;

    public sealed record Loc(string Path, long Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public int ReaderVersion(EngineInfo engine) => 2;

    public EngineInfo? Detect(Game game)
    {
        var dir = Path.Combine(game.InstallDir, Dir);
        if (!Directory.Exists(dir) || CarvedReader.IsNorthlight2(game.InstallDir)) return null; // Northlight 2 builds its root signatures otherwise
        foreach (var f in Effects(dir).OrderBy(f => f.Length).Take(DetectFiles))
            if (Records(Read(f.FullName), default) is { } recs && recs.Any(r => r.Select(c => c.Kind).Order().SequenceEqual([0, 1])))
                // Control runs on either API (Steam: an exe per API); its files don't say which one is played
                return new EngineInfo(Family, Version, null, Effects(Path.Combine(game.InstallDir, Dir11)).Any() ? UnrealRhi.Ambiguous : "D3D12", false, null);
        return null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var maps = new List<ShaderMap>();
        var seen = new HashSet<string>();
        var libs = new List<string>();
        int records = 0, visited = 0, pooled = 0, unlinked = 0, malformed = 0, overCap = 0, bad = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var (dir, dx11) in new[] { (Dir, false), (Dir11, true) })
        foreach (var f in Effects(Path.Combine(game.InstallDir, dir)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            var b = Read(f.FullName);
            if (Records(b, ct) is not { } recs) continue;
            content.AppendData(Encoding.UTF8.GetBytes($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
            var pool = new List<string>();
            foreach (var r in recs)
            {
                if (visited++ >= MaxRecords) { overCap++; continue; }
                var shas = new List<string>();
                foreach (var c in r)
                {
                    var bytes = b.AsSpan((int)c.Offset, c.Size);
                    var sha = Convert.ToHexStringLower(SHA1.HashData(bytes));
                    if (locs.TryAdd(sha, new Loc(f.FullName, c.Offset, c.Size)))
                        try { if (ShaderContainer.Parse(bytes, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings), RootSignature = null }; }
                        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; } // valid container, odd program: not usable
                    if (shaders.ContainsKey(sha)) shas.Add(sha);
                }
                if (dx11) { pool.AddRange(shas.Where(h => Planning.Planner.IsD3D11(shaders[h]))); continue; }
                if (shas.Count != r.Count) continue;
                if (r is [{ Kind: 6 }]) { libs.Add(shas[0]); continue; }
                if (!Pipeline(r)) { pool.AddRange(shas); pooled += shas.Count; continue; }
                switch (Feeds(shas.Select(h => shaders[h]).ToList()))
                {
                    case null: malformed++; continue;
                    case false: unlinked++; continue;
                }
                records++;
                var h = CarvedReader.Sha1Hex(string.Join(',', shas.Order(StringComparer.Ordinal)));
                if (seen.Add(h)) maps.Add(new ShaderMap(h, rel, CarvedReader.Platform, shas, IsPipeline: true));
            }
            if (pool.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex(rel), rel, dx11 ? Platform11 : CarvedReader.Platform, pool.Distinct().ToList()));
        }
        libs = libs.Distinct().ToList();
        if (libs.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{Dir}|{Libraries}"), Libraries, CarvedReader.Platform, libs));
        if (records == 0) throw new InvalidDataException($"{Dir}: no pipeline record in its effect files: scan the game again");
        located[game.Id] = locs;
        var n11 = maps.Where(m => m.Platform == Platform11).SelectMany(m => m.Shaders).Distinct().Count();
        log?.Report($"{shaders.Count} shaders ({string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))}), "
            + $"{records} pipeline records -> {maps.Count(m => m.IsPipeline)} distinct pipelines, {libs.Count} ray tracing libraries"
            + (unlinked > 0 ? $"; {unlinked} records left out: their VS doesn't feed their PS (the runtime rejects them)" : "")
            + (malformed > 0 ? $"; {malformed} records left out: a VS writes one output twice" : "")
            + (overCap > 0 ? $"; warning: {overCap} records over the cap of {MaxRecords} left out" : "")
            + (pooled > 0 ? $"; {pooled} shaders of records that aren't a pipeline, pooled per file" : "") + (bad > 0 ? $"; {bad} unparseable" : "")
            + (n11 > 0 ? $"; {n11} of them DirectX 11 shaders ({Dir11})" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. n11 > 0 ? new[] { Platform11 } : []], shaders, maps);
    }

    /// <summary>Re-slices each container where the index found it; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Path))
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle h;
            try { h = File.OpenHandle(file.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; } // gone since the index
            using var _ = h;
            foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
            {
                ct.ThrowIfCancellationRequested();
                var b = new byte[at.Size];
                if (RandomAccess.Read(h, b, at.Offset) == b.Length && Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
            }
        }
    }

    public readonly record struct Container(long Offset, int Size, int Kind);

    /// <summary>An RFX effect file ("RFX " magic; uncompressed, measured on Control): its containers grouped into records.
    /// Each shader is stored as u32 size, the container, then its entry point (u32 length, name) and 8 zero bytes. Two
    /// shaders of one pipeline follow each other with only 4 more zero bytes between them; any other bytes between two
    /// shaders are a pass or permutation header, which starts a new record. Null when the file isn't an effect file or a
    /// container isn't preceded by its size or there are more than <see cref="MaxShadersPerFile"/> (the layout isn't this one).</summary>
    public static List<List<Container>>? Records(byte[] b, CancellationToken ct)
    {
        if (b.Length < 8 || !b.AsSpan(0, 4).SequenceEqual("RFX "u8)) return null;
        var recs = new List<List<Container>>();
        var end = -1;
        var n = 0;
        foreach (var (at, c) in Dxbc.Containers(b))
        {
            if (++n > MaxShadersPerFile) return null;
            if (n % 4096 == 0) ct.ThrowIfCancellationRequested();
            if (at < 4 || BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at - 4)) != c.Length) return null;
            var shader = new Container(at, c.Length, Dxbc.Kind(c));
            if (end < 0 || !EntryPoint(b.AsSpan(end, at - end))) recs.Add([]);
            recs[^1].Add(shader);
            end = at + c.Length;
        }
        return recs;
    }

    /// <summary>The bytes between two shaders of one record: u32 length, an identifier, 12 zero bytes, the next one's u32 size.</summary>
    static bool EntryPoint(ReadOnlySpan<byte> gap)
    {
        if (gap.Length < 21) return false;
        var n = BinaryPrimitives.ReadInt32LittleEndian(gap);
        if (n < 1 || gap.Length != n + 20) return false;
        foreach (var ch in gap.Slice(4, n)) if (!(char.IsAsciiLetterOrDigit((char)ch) || ch == '_')) return false;
        return !gap.Slice(4 + n, 12).ContainsAnyExcept((byte)0);
    }

    /// <summary>Distinct graphics stages with a vertex shader, or a lone compute shader.</summary>
    static bool Pipeline(List<Container> r) =>
        r.DistinctBy(c => c.Kind).Count() == r.Count && (r.All(c => Dxbc.IsGraphics(c.Kind)) && r.Any(c => c.Kind == 1) || r is [{ Kind: 5 }]);

    /// <summary>A record's VS feeds its PS: every PS input read from the VS is written there in the same register (DXBC links by
    /// register; Control ships 8 VS+PS records that don't: "TEXCOORD is defined for mismatched hardware registers"). Null when
    /// the VS writes one output twice (malformed).</summary>
    internal static bool? Feeds(IReadOnlyList<ShaderInfo> stages)
    {
        if (stages.FirstOrDefault(s => s.Stage == Stage.Vertex) is not { } vs || stages.FirstOrDefault(s => s.Stage == Stage.Pixel) is not { } ps) return true;
        var outs = new Dictionary<(string, int), int>();
        foreach (var o in vs.Outputs)
            if (!outs.TryAdd((o.Semantic.ToUpperInvariant(), o.Index), o.Register)) return null;
        return Planning.Planner.Links(vs, ps) && ps.Inputs.All(i => !outs.TryGetValue((i.Semantic.ToUpperInvariant(), i.Index), out var reg) || reg == i.Register);
    }

    static IEnumerable<FileInfo> Effects(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.obj", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }) : [];

    /// <summary>The file's bytes when it starts with the effect magic and is at most <see cref="MaxFile"/>; else empty.</summary>
    static byte[] Read(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> magic = stackalloc byte[4];
            if (f.Length > MaxFile || f.ReadAtLeast(magic, 4, false) < 4 || !magic.SequenceEqual("RFX "u8)) return [];
            var b = new byte[f.Length];
            magic.CopyTo(b);
            f.ReadExactly(b.AsSpan(4));
            return b;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
