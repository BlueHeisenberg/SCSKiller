using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Northlight;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Decima;

/// <summary>Guerrilla's Decima engine on PC (D3D12), by the archives (<see cref="DecimaArchive"/>) of the install:
/// <list type="bullet">
/// <item><see cref="HorizonZeroDawn"/> (Horizon Zero Dawn Complete Edition): Packed_DX12\*.bin, plain; DXIL shaders.</item>
/// <item><see cref="DeathStranding"/> (DEATH STRANDING, DIRECTOR'S CUT): data\*.bin, encrypted with salts read from the
/// game's exe (<see cref="DecimaKeys"/>); DXBC SM 5.1 shaders, a few DXIL compute shaders.</item>
/// </list>
/// The shaders are raw containers in the unpacked chunks, carved. Each compute shader is one IsPipeline map. A graphics
/// program is stored as its VS, a few bytes (<see cref="ProgramGapOf"/>), then its PS: each such pair is one IsPipeline map
/// too. Any other shader (a GS, a VS or PS without its partner) is pooled per archive. No root signature ships (the engine serializes its own in
/// code): without a recording nothing is planned (no "+RTS0" in Version). No ray tracing (no DXIL library, no RayQuery).
/// EngineInfo: Family "Decima", Version "DX12", Fork the game.</summary>
public sealed class DecimaReader(string? dataDir = null) : IEngineReader
{
    public const string Family = "Decima", Version = "DX12";
    public const string HorizonZeroDawn = "Horizon Zero Dawn", DeathStranding = "Death Stranding";
    /// <summary>Bytes between a VS and the PS of its program in Horizon Zero Dawn (measured: all but 2 of about 837,000 VS).</summary>
    public const int ProgramGap = 48;
    /// <summary>In Death Stranding: 20 bytes after the VS, then the PS's record header (u32 3, u32 5, a 16-byte hash, u32 size);
    /// measured: 28,044 VS followed by a PS in 3 archives, all at 52.</summary>
    public const int DeathStrandingProgramGap = 52;

    /// <summary>The bytes between a VS and its PS in the game's program records.</summary>
    public static int ProgramGapOf(string fork) => fork == DeathStranding ? DeathStrandingProgramGap : ProgramGap;
    internal int Window { get; init; } = 16 << 20;

    readonly DecimaKeys keys = new(dataDir ?? AppStore.DefaultDir);

    /// <param name="Offset">in the archive's unpacked stream</param>
    public sealed record Loc(string Archive, long Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The game, its archives (by name) and how many other .bin files are beside them (another magic: another
    /// archive version, or not an archive), from the install's folders and the files' magic; null = neither layout.
    /// Packed_DX12 is Horizon Zero Dawn's even when none of its .bin files has a known magic (Archives empty).</summary>
    public static (string Fork, List<FileInfo> Archives, int Unknown)? Layout(string installDir)
    {
        foreach (var (dir, magic, fork) in new[] { ("Packed_DX12", DecimaArchive.Plain, HorizonZeroDawn), ("data", DecimaArchive.Encrypted, DeathStranding) })
        {
            var d = new DirectoryInfo(Path.Combine(installDir, dir));
            if (!d.Exists) continue;
            List<FileInfo> all;
            try { all = [.. d.EnumerateFiles("*.bin", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)]; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var bins = all.Where(f => DecimaArchive.Magic(f.FullName) == magic).ToList();
            if (bins.Count > 0 || fork == HorizonZeroDawn && all.Count > 0) return (fork, bins, all.Count - bins.Count);
        }
        return null;
    }

    public string DetectStamp(Game game, EngineInfo? engine) => keys.KeyStamp(game);

    /// <summary>Reads only headers and the smallest archive's chunk table. An encrypted game gets its salts from
    /// <see cref="DecimaKeys"/> (a one-time static scan of its exe) and is otherwise Encrypted.</summary>
    public EngineInfo? Detect(Game game) => Detect(game, out _);

    /// <param name="notes">for an encrypted game, how its salts were found or not (never the salts)</param>
    public EngineInfo? Detect(Game game, out string notes)
    {
        notes = "";
        if (Layout(game.InstallDir) is not var (fork, archives, _)) return null;
        var bySize = archives.OrderBy(f => f.Length).Select(f => f.FullName).ToList();
        if (fork == HorizonZeroDawn)
        {
            if (bySize.Count == 0) return new EngineInfo(Family, Version, fork, "D3D12", false, "an archive version SCSKiller doesn't read");
            var why = "";
            return bySize.Any(f => { using var a = DecimaArchive.Open(f, null, out why); return a != null; }) ? new EngineInfo(Family, Version, fork, "D3D12", false, null)
                : new EngineInfo(Family, Version, fork, "D3D12", false, $"its archives don't open ({why})");
        }
        var salts = keys.Get(game, bySize, out notes);
        return new EngineInfo(Family, Version, fork, "D3D12", salts == null, salts == null ? "encrypted game files (the game's exe doesn't give their salts)" : null);
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (fork, archives, unknown) = Layout(game.InstallDir) ?? throw new InvalidDataException("not a Decima install: scan the game again");
        var salts = Salts(game, fork);
        try { Codecs.Load(zlib: false); }   // a missing codec fails the index, not each archive
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidDataException("the Oodle codec can't be loaded", e); }
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var pairs = new HashSet<(string Vs, string Ps)>();
        var pools = new List<(string Library, HashSet<string> Shaders)>();
        var gap = ProgramGapOf(fork);
        int containers = 0, bad = 0, undecoded = 0, unlinked = 0, capped = 0;
        long programs = 0, lonelyVs = 0;
        var unopened = new Dictionary<string, int>();
        long read = 0;
        var buf = new byte[Window];
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var f in archives)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            content.AppendData(Encoding.UTF8.GetBytes($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
            using var a = DecimaArchive.Open(f.FullName, salts, out var why);
            if (a == null) { unopened[why] = unopened.GetValueOrDefault(why) + 1; continue; }
            var seen = new HashSet<string>();
            (long End, int Kind, string Sha)? prev = null;
            // a scan reads the stream about once; headers claiming containers past a window's end would read it again and again
            var meter = new NixxesReader.Meter(2 * a.Length + Window);
            try
            {
                NixxesReader.Carve(a.Readable, buf, (at, c) =>
                {
                    containers++;
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    if (locs.TryAdd(sha, new Loc(f.FullName, at, c.Length)))
                        try
                        {
                            if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                        }
                        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                    var program = shaders.TryGetValue(sha, out var s) && s.Stage == Stage.Pixel && prev is { Kind: (int)Stage.Vertex } p && p.End + gap == at;
                    if (prev is { Kind: (int)Stage.Vertex } && !program) lonelyVs++;
                    if (s == null) { prev = null; return; }
                    if (program)
                    {
                        programs++;
                        pairs.Add((prev!.Value.Sha, s.Sha1));
                    }
                    seen.Add(s.Sha1);
                    prev = (at + c.Length, (int)s.Stage, s.Sha1);
                }, meter, () => false, ct);
                if (meter.Left < Window) capped++;   // an archive that failed is already a warning; its retries are charged too
            }
            catch (InvalidDataException) { undecoded++; }   // the shaders before the failing chunk are kept
            if (prev is { Kind: (int)Stage.Vertex }) lonelyVs++;
            read += meter.Read;
            pools.Add((rel, seen));
        }
        // most VS without their PS where one is expected: not this layout, so no program is trusted
        var layout = programs < lonelyVs ? $"; warning: only {programs} of {programs + lonelyVs} VS have a PS {gap} bytes later: no programs, VS and PS pooled per archive"
            : lonelyVs > 0 ? $"; warning: {lonelyVs} VS not followed by a PS {gap} bytes later"
            : "";
        if (programs < lonelyVs) pairs.Clear();
        var warnings = layout
            + string.Concat(unopened.Select(u => $"; warning: {u.Value} archives don't open: {u.Key}"))
            + (unknown > 0 ? $"; warning: {unknown} .bin files of an unknown archive version skipped" : "")
            + (undecoded > 0 ? $"; warning: {undecoded} archives stop at a chunk that doesn't decompress to its size (the shaders before it are kept)" : "")
            + (capped > 0 ? $"; warning: {capped} archives hit the read budget, the rest of them not scanned" : "");
        if (shaders.Count == 0) throw new InvalidDataException($"{fork}: no shader in its archives{warnings}");

        var maps = new List<ShaderMap>();
        var paired = new HashSet<string>();
        foreach (var (vs, ps) in pairs)
        {
            if (NorthlightReader.Feeds([shaders[vs], shaders[ps]]) != true) { unlinked++; continue; }
            paired.Add(vs);
            paired.Add(ps);
            maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{vs},{ps}"), Library(locs[vs], game), CarvedReader.Platform, [vs, ps], IsPipeline: true));
        }
        foreach (var cs in shaders.Values.Where(s => s.Stage == Stage.Compute).Select(s => s.Sha1))
            maps.Add(new ShaderMap(CarvedReader.Sha1Hex(cs), Library(locs[cs], game), CarvedReader.Platform, [cs], IsPipeline: true));
        var pooled = 0;
        foreach (var (lib, seen) in pools)
            if (seen.Where(h => shaders[h].Stage != Stage.Compute && !paired.Contains(h)).ToList() is { Count: > 0 } pool)
            {
                maps.Add(new ShaderMap(CarvedReader.Sha1Hex(lib), lib, CarvedReader.Platform, pool));
                pooled += pool.Count;
            }
        located[game.Id] = locs;
        log?.Report($"{fork}: {archives.Count} archives ({read / 1e9:F2} GB unpacked): {containers} shader containers, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {pairs.Count - unlinked} VS+PS programs, {shaders.Values.Count(s => s.Stage == Stage.Library)} ray tracing libraries, {shaders.Values.Count(s => s.InlineRayTracing)} tracing rays inline"
            + (pooled > 0 ? $"; {pooled} shaders outside a program pooled per archive" : "")
            + (unlinked > 0 ? $"; warning: {unlinked} programs whose VS doesn't feed their PS, pooled instead" : "")
            + warnings
            + (bad > 0 ? $"; {bad} unparseable" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform], shaders, maps);
    }

    static string Library(Loc at, Game game) => Path.GetRelativePath(game.InstallDir, at.Archive);

    DecimaArchive.Salts? Salts(Game game, string fork) =>
        fork == DeathStranding ? keys.Stored(game) ?? throw new InvalidDataException("encrypted archives without their salts: scan the game again") : null;

    /// <summary>Decompresses only the chunks holding each container; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        var salts = Salts(game, engine.Fork ?? HorizonZeroDawn);   // the fork names the layout
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Archive))
        {
            using var a = DecimaArchive.Open(file.Key, salts);
            if (a == null) continue;   // gone or changed since the index
            foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
            {
                ct.ThrowIfCancellationRequested();
                if (at.Offset > a.Length - at.Size) continue;
                var b = new byte[at.Size];
                try { a.Read(at.Offset, b); }
                catch (InvalidDataException) { continue; }
                if (Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
            }
        }
    }
}
