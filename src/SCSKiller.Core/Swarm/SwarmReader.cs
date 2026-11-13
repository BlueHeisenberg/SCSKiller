using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.ForzaTech;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Swarm;

/// <summary>Saber's Swarm engine (Warhammer 40,000: Space Marine 2, RoadCraft and World War Z). The shaders are in
/// client_pc\root\paks\client\default\default.pak, or root\paks\client\default\default.pak in RoadCraft,
/// a ZIP, under shader_cache/prebuild/pc/*.sdc (stored). An .sdc file, little endian:
///   - u32 block count, u32 entry count, u32 payload bytes, u32 not read;
///   - per block: u32 zlib bytes, u32 n, then n entries {u32 key, u32 size, u32 m}, each followed by m bytes;
///   - the payload: the blocks' zlib streams back to back, each inflating to its entries' values concatenated in order.
/// Keys are 32-bit hashes in ascending order. The m bytes (a stage mask byte, 1 VS, 2 PS, 4 CS, then 1-based define indices)
/// tell apart entries whose keys collide (measured: 32 entries in 3 files, at most 18 bytes). A value is a DXIL container, a
/// permutation's 4-byte shader index, or one of the file's tables (sources, define names, permutations, the shaders' PDB names).
/// No container carries RTS0 (the game builds its root signatures at run time), so a plan needs a recording. The game's PSO
/// list (default_pso_cache_dump.pak) names passes and material settings, never a shader: maps are one pool per .sdc.
/// World War Z stores its renderer-specific .sdc files in en_us\client\preload\paks\initial.pak under
/// [project]/prebuild/shaders_cache/prebuild/pc/; pc_sm50 is DirectX 11 and pc_dx12 is DirectX 12.
/// SnowRunner's fork stores DirectX 11 shaders in preload\paks\client\shader.pak: its common .sdc files use the same
/// block format, and shadercachedx11.sdc contains one zlib stream of length-prefixed DXBC containers.
/// EngineInfo: Family "Swarm", Version "SDC" or "SDC-D3D11".</summary>
public sealed class SwarmReader : IEngineReader
{
    public const string Family = "Swarm", Version = "SDC", D3D11Version = "SDC-D3D11";
    public int ReaderVersion(EngineInfo engine) => 2;
    const uint D3D11CacheVersion = 20230112;
    const int MaxD3D11Cache = 512 << 20;

    /// <summary>Bounds on what a file declares, past which it is left out: the pak's entries, an .sdc's entries, a block's
    /// inflated bytes (measured: 224, 291,145, 14 MB) and an entry's m bytes.</summary>
    const int MaxPakEntries = 1 << 20, MaxEntries = 1 << 22, MaxBlock = 64 << 20, MaxExtra = 1 << 10;

    /// <summary>One game profile. Keys in the AppData file are Game.Id values; Name is only a human-readable label.</summary>
    sealed record Profile(
        string? Name = null,
        string[]? Aliases = null,
        string[]? PakPaths = null,
        string? ShaderFolder = null,
        string? GraphicsApi = null,
        bool ApiGuessed = false,
        string? D3D11ShaderPrefix = null,
        string? D3D12ShaderPrefix = null,
        string? D3D11CacheEntry = null);
    static readonly Dictionary<string, Profile> Config = LoadDataLocations();

    /// <summary>The editable config lives beside the user's other SCSKiller data and is seeded from the bundled defaults on first run.</summary>
    static Dictionary<string, Profile> LoadDataLocations()
    {
        var path = Path.Combine(AppStore.DefaultDir, "swarm-data-locations.json");
        Directory.CreateDirectory(AppStore.DefaultDir);

        if (!File.Exists(path)) File.WriteAllText(path, ContentFile.Embedded("SCSKiller.Core.Swarm.swarm-data-locations.json"));

        var config = JsonSerializer.Deserialize<Dictionary<string, Profile>>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Swarm data location config is empty: {path}");
        return config;
    }

    /// <param name="Block">where the block's zlib stream starts in the .sdc</param>
    /// <param name="Offset">the value's offset in the inflated block</param>
    public sealed record Loc(string Entry, long Block, int Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. In memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The configured Swarm shader pak under the game's install folder.</summary>
    public static string? Find(Game game)
    {
        if (ProfileFor(game) is not { } profile) return null;

        foreach (var pakPath in profile.PakPaths ?? [])
        {
            var pak = GameFiles.Find(game.InstallDir, pakPath);
            if (File.Exists(pak)) return pak;
        }

        return null;
    }

    public EngineInfo? Detect(Game game)
    {
        if (Find(game) is not { } pak) return null;
        var profile = ProfileFor(game)!;
        var d3d11 = IsD3D11Pak(pak, profile);
        EngineInfo Result(string? why) => new(Family, d3d11 ? D3D11Version : Version, null, d3d11 ? "D3D11" : profile.GraphicsApi ?? "D3D12", false, why, ApiGuessed: profile.ApiGuessed);
        using var zip = Open(pak, out var error);
        if (zip == null) return Result($"{Path.GetFileName(pak)} doesn't open: {error}");
        var entries = Sdcs(zip, d3d11, profile, false).ToList();
        if (d3d11 && entries.Count == 0) return null;
        if (d3d11)
        {
            if (entries.FirstOrDefault(e => IsD3D11Cache(e, profile)) is not { } main) return Result("the DirectX 11 shader cache is missing or unreadable");
            try { using var s = main.Open(); ReadD3D11Header(s, main.Length); }
            catch (Exception x) when (x is InvalidDataException or IOException) { return Result($"{main.Name} doesn't read: {x.Message}"); }
        }
        string? why = null;
        foreach (var e in entries.OrderBy(e => e.Length))   // smallest first: a whole install stops at the first
        {
            var found = false;
            try { EachCache(e, null, (_, v) => found |= Dxbc.Valid(v) && Dxbc.Kind(v) >= 0, CancellationToken.None); }
            catch (Exception x) when (x is InvalidDataException or IOException) { why ??= $"{e.Name} doesn't read: {x.Message}"; }
            // if we found a valid shader, null out the reason why and return success immediately
            if (found) return Result(why: null);
        }
        return Result(why ?? $"no shader in {Path.GetFileName(pak)}'s shader caches");
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var pak = Find(game) ?? throw new FileNotFoundException($"{game.InstallDir}: no Swarm shader pak");
        using var zip = Open(pak, out var error) ?? throw new InvalidDataException($"{Path.GetFileName(pak)} doesn't open: {error}");
        var profile = ProfileFor(game)!;
        var d3d11 = IsD3D11Pak(pak, profile);
        var entries = Sdcs(zip, d3d11, profile, engine.GraphicsApi.Contains("D3D12")).ToList();
        if (d3d11 && !entries.Any(e => IsD3D11Cache(e, profile))) throw new InvalidDataException("the DirectX 11 shader cache is missing or unreadable");
        var locs = new Dictionary<string, Loc>();
        var shaders = new Dictionary<string, ShaderInfo>();
        var maps = new List<ShaderMap>();
        var stamp = new StringBuilder();
        int files = 0, containers = 0, bad = 0, broken = 0;
        string? firstBroken = null;
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            files++;
            stamp.Append($"{e.FullName}|{e.Length}|{e.Crc32:x8}\n");
            var pool = new List<string>();
            try
            {
                EachCache(e, null, (at, v) =>
                {
                    if (!Dxbc.Valid(v)) return;
                    containers++;
                    var sha = Convert.ToHexStringLower(SHA1.HashData(v));
                    pool.Add(sha);
                    if (!locs.TryAdd(sha, at) || Dxbc.Kind(v) < 0) return;
                    try
                    {
                        if (ShaderContainer.Parse(v, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                    }
                    catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                }, ct);
            }
            catch (Exception x) when (x is InvalidDataException or IOException)
            {
                if (d3d11) throw new InvalidDataException($"{e.FullName} doesn't read: {x.Message}", x);
                broken++;
                firstBroken ??= $"{e.Name}: {x.Message}";
            }
            if (pool.Where(shaders.ContainsKey).Distinct().ToList() is { Count: > 0 } members)
                maps.Add(new ShaderMap(CarvedReader.Sha1Hex(e.FullName), e.FullName, CarvedReader.Platform, members));
        }
        log?.Report($"{files} .sdc files, {containers} containers -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")}, {maps.Count} pools"
            + (d3d11 ? "" : ", no root signatures (built at run time)")
            + (broken > 0 ? $"; warning: {broken} .sdc files don't read whole ({firstBroken}), the rest of each left out" : "") + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        if (shaders.Count == 0) throw new InvalidDataException($"no shader in {Path.GetFileName(pak)}'s shader caches");
        located[game.Id] = locs;
        return new ShaderIndex(CarvedReader.Sha1Hex(stamp.ToString()), [CarvedReader.Platform], shaders, maps);
    }

    /// <summary>Inflates the blocks holding the requested containers once more; one whose bytes changed since (game patched)
    /// isn't sent and fails at replay.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        if (Find(game) is not { } pak)
        {
            if (engine.Version == D3D11Version) throw new FileNotFoundException("the Swarm DirectX 11 shader pak is missing");
            return;
        }
        using var zip = Open(pak, out var error);
        if (zip == null)
        {
            if (engine.Version == D3D11Version) throw new InvalidDataException($"{pak} doesn't open: {error}");
            return;
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Entry))
        {
            if (zip.GetEntry(file.Key) is not { } e)
            {
                if (engine.Version == D3D11Version) throw new InvalidDataException($"{file.Key} is missing from the shader pak");
                continue;
            }
            var want = file.ToDictionary(x => (x.At.Block, x.At.Offset), x => x.Sha);
            var blocks = want.Keys.Select(k => k.Block).ToHashSet();
            var received = 0;
            try
            {
                EachCache(e, blocks.Contains, (at, v) =>
                {
                    if (!want.TryGetValue((at.Block, at.Offset), out var sha)) return;
                    if (Convert.ToHexStringLower(SHA1.HashData(v)) == sha) { sink(sha, v.ToArray()); received++; }
                    else if (engine.Version == D3D11Version) throw new InvalidDataException($"{e.FullName} changed since indexing");
                }, ct);
                if (engine.Version == D3D11Version && received != want.Count) throw new InvalidDataException($"{e.FullName} changed since indexing");
            }
            catch (Exception x) when (engine.Version != D3D11Version && x is (InvalidDataException or IOException)) { }   // changed since the index: what was read is sent
        }
    }

    /// <summary>The .sdc entries by name. A name the pak holds twice is left out: ReadShaders finds an entry by its name.</summary>
    static Profile? ProfileFor(Game game) => Config.FirstOrDefault(p => p.Key.Equals(game.Id, StringComparison.OrdinalIgnoreCase)).Value
        ?? Config.Values.FirstOrDefault(p => (p.Aliases ?? []).Append(p.Name).OfType<string>()
            .Any(name => game.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
    static bool IsD3D11Pak(string pak, Profile profile) => profile.D3D11CacheEntry != null
        && (profile.PakPaths ?? []).Any(pakPath => pak.EndsWith(pakPath.Replace('\\', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    static string EntryName(ZipArchiveEntry e) => e.FullName.Replace('\\', '/');
    static bool IsD3D11Cache(ZipArchiveEntry e, Profile profile) => profile.D3D11CacheEntry != null
        && EntryName(e).Equals(profile.D3D11CacheEntry, StringComparison.OrdinalIgnoreCase);
    static bool IsD3D11Cache(ZipArchiveEntry e) => Config.Values.Any(p => IsD3D11Cache(e, p));

    static IEnumerable<ZipArchiveEntry> Sdcs(ZipArchive zip, bool d3d11, Profile profile, bool dx12) => zip.Entries
        .Where(e => EntryName(e) is var name && (d3d11 && IsD3D11Cache(e, profile)
            || name.StartsWith(profile.ShaderFolder ?? "", StringComparison.OrdinalIgnoreCase)
            && name.IndexOf('/', (profile.ShaderFolder ?? "").Length) < 0
            && (d3d11 || (dx12 ? profile.D3D12ShaderPrefix : profile.D3D11ShaderPrefix) is not { } prefix
                || Path.GetFileName(name).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            && name.EndsWith(".sdc", StringComparison.OrdinalIgnoreCase)))
        .GroupBy(e => e.FullName, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.First()).Where(e => !e.IsEncrypted)
        .OrderBy(e => e.FullName, StringComparer.Ordinal);

    delegate void Value(Loc at, ReadOnlySpan<byte> value);

    static void EachCache(ZipArchiveEntry e, Func<long, bool>? wanted, Value found, CancellationToken ct)
    {
        if (!IsD3D11Cache(e)) { Each(e, wanted, found, ct); return; }
        if (wanted?.Invoke(-1) == false) return;
        using var s = e.Open();
        var size = ReadD3D11Header(s, e.Length);
        var bytes = new byte[size];
        using (var inflate = new ZLibStream(s, CompressionMode.Decompress))
        {
            for (var at = 0; at < bytes.Length;)
            {
                ct.ThrowIfCancellationRequested();
                var n = inflate.Read(bytes, at, Math.Min(bytes.Length - at, 1 << 20));
                if (n == 0) throw new InvalidDataException("DirectX 11 shader cache ends early");
                at += n;
            }
            if (inflate.ReadByte() != -1) throw new InvalidDataException("DirectX 11 shader cache exceeds its declared size");
        }
        for (var i = 0; bytes.AsSpan(i).IndexOf("DXBC"u8) is var k and >= 0;)
        {
            ct.ThrowIfCancellationRequested();
            var at = i + k;
            var length = Dxbc.HeaderSize(bytes.AsSpan(at));
            if (length > 0 && at >= 4 && U(bytes.AsSpan(at - 4, 4)) == length
                && length <= bytes.Length - at && Dxbc.Valid(bytes.AsSpan(at, length)))
            {
                found(new Loc(e.FullName, -1, at, length), bytes.AsSpan(at, length));
                i = at + length;
            }
            else i = at + 1;
        }
    }

    static int ReadD3D11Header(Stream s, long length)
    {
        Span<byte> h = stackalloc byte[12];
        s.ReadExactly(h);
        uint version = U(h), packed = U(h[4..]), size = U(h[8..]);
        if (version != D3D11CacheVersion || packed != length - 12 || size == 0 || size > MaxD3D11Cache)
            throw new InvalidDataException($"not a supported DirectX 11 shader cache: version {version}, compressed {packed}, uncompressed {size}");
        return (int)size;
    }

    /// <summary>Calls <paramref name="found"/> for each value of the .sdc's blocks that <paramref name="wanted"/> takes (by the
    /// block's offset; null: all), in file order. InvalidDataException (or an IOException for a short read) when the file
    /// isn't laid out so or declares more than the bounds; the values of the blocks before were given.</summary>
    static void Each(ZipArchiveEntry e, Func<long, bool>? wanted, Value found, CancellationToken ct)
    {
        static InvalidDataException Bad(string why) => new($"not an .sdc file: {why}");
        var len = e.Length;
        using var s = e.Open();
        Span<byte> h = stackalloc byte[16];
        s.ReadExactly(h);
        uint blocks = U(h), entries = U(h[4..]), payload = U(h[8..]);
        if (entries > MaxEntries || blocks > entries) throw Bad($"{blocks} blocks, {entries} entries");
        if (16 + 8L * blocks + 12L * entries + payload > len) throw Bad("its tables and payload overrun it");
        var packed = new int[blocks];
        var sizes = new int[entries];
        var first = new int[blocks + 1];
        long at = 16, total = 0;
        var extra = new byte[MaxExtra];
        int k = 0;
        for (var b = 0; b < blocks; b++)
        {
            s.ReadExactly(h[..8]);
            uint z = U(h), n = U(h[4..]);
            if (n > entries - k) throw Bad("more entries than declared");
            first[b] = k;
            long inflated = 0;
            for (var i = 0; i < n; i++, k++)
            {
                s.ReadExactly(h[..12]);
                uint size = U(h[4..]), m = U(h[8..]);
                if (m > MaxExtra) throw Bad($"{m} key bytes");
                s.ReadExactly(extra.AsSpan(0, (int)m));
                inflated += size;
                if (inflated > MaxBlock) throw Bad("a block over the size bound");
                sizes[k] = (int)size;
                at += 12 + m;
            }
            if (z > payload || z > MaxBlock + (MaxBlock >> 4)) throw Bad("a block over the payload or the size bound");
            packed[b] = (int)z;
            total += z;
            at += 8;
        }
        first[blocks] = k;
        if (k != entries || total != payload || at + payload != len) throw Bad("its tables don't add up to its length");
        byte[] zbuf = [], buf = [];
        foreach (var (b, z) in packed.Index())
        {
            ct.ThrowIfCancellationRequested();
            var size = 0;
            for (var i = first[b]; i < first[b + 1]; i++) size += sizes[i];
            if (wanted?.Invoke(at) == false)
            {
                Skip(s, z, ref zbuf);
                at += z;
                continue;
            }
            if (zbuf.Length < z) zbuf = new byte[z];
            if (buf.Length < size) buf = new byte[size];
            s.ReadExactly(zbuf, 0, z);
            using (var inflate = new ZLibStream(new MemoryStream(zbuf, 0, z), CompressionMode.Decompress))
            {
                inflate.ReadExactly(buf, 0, size);   // never more than the entries declare: a stream inflating to more is cut here
                if (inflate.ReadByte() != -1) throw Bad("a block inflates to more than its entries");
            }
            for (int i = first[b], o = 0; i < first[b + 1]; o += sizes[i++])
                found(new Loc(e.FullName, at, o, sizes[i]), buf.AsSpan(o, sizes[i]));
            at += z;
        }
    }

    static void Skip(Stream s, int n, ref byte[] buf)
    {
        if (buf.Length < Math.Min(n, 1 << 20)) buf = new byte[Math.Min(n, 1 << 20)];
        for (int left = n, r; left > 0; left -= r) s.ReadExactly(buf, 0, r = Math.Min(left, buf.Length));
    }

    static uint U(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);

    /// <summary>The pak read-only with its central directory loaded, or null (and why) when it doesn't open as a ZIP or
    /// declares more than <see cref="MaxPakEntries"/> entries.</summary>
    static ZipArchive? Open(string path, out string? error)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (ForzaTechReader.DeclaredEntries(fs, MaxPakEntries) > MaxPakEntries)
            {
                fs.Dispose();
                error = "too many entries";
                return null;
            }
            fs.Position = 0;
            var a = new ZipArchive(fs, ZipArchiveMode.Read);
            try { _ = a.Entries.Count; }   // the directory loads lazily: a damaged one throws here
            catch { a.Dispose(); throw; }
            error = null;
            return a;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            fs?.Dispose();
            error = e.Message;
            return null;
        }
    }
}
