using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.ForzaTech;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Swarm;

/// <summary>Saber's Swarm engine (Warhammer 40,000: Space Marine 2). Every shader is in client_pc\root\paks\client\default\default.pak,
/// a ZIP, under shader_cache/prebuild/pc/*.sdc (stored). An .sdc file, little endian:
///   - u32 block count, u32 entry count, u32 payload bytes, u32 not read;
///   - per block: u32 zlib bytes, u32 n, then n entries {u32 key, u32 size, u32 m}, each followed by m bytes;
///   - the payload: the blocks' zlib streams back to back, each inflating to its entries' values concatenated in order.
/// Keys are 32-bit hashes in ascending order. The m bytes (a stage mask byte, 1 VS, 2 PS, 4 CS, then 1-based define indices)
/// tell apart entries whose keys collide (measured: 32 entries in 3 files, at most 18 bytes). A value is a DXIL container, a
/// permutation's 4-byte shader index, or one of the file's tables (sources, define names, permutations, the shaders' PDB names).
/// No container carries RTS0 (the game builds its root signatures at run time), so a plan needs a recording. The game's PSO
/// list (default_pso_cache_dump.pak) names passes and material settings, never a shader: maps are one pool per .sdc.
/// EngineInfo: Family "Swarm", Version "SDC".</summary>
public sealed class SwarmReader : IEngineReader
{
    public const string Family = "Swarm", Version = "SDC";
    const string Pak = @"client_pc\root\paks\client\default\default.pak", Folder = "shader_cache/prebuild/pc/";

    /// <summary>Bounds on what a file declares, past which it is left out: the pak's entries, an .sdc's entries, a block's
    /// inflated bytes (measured: 224, 291,145, 14 MB) and an entry's m bytes.</summary>
    const int MaxPakEntries = 1 << 20, MaxEntries = 1 << 22, MaxBlock = 64 << 20, MaxExtra = 1 << 10;

    /// <param name="Block">where the block's zlib stream starts in the .sdc</param>
    /// <param name="Offset">the value's offset in the inflated block</param>
    public sealed record Loc(string Entry, long Block, int Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. In memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>default.pak under the install folder, else under the root the exe (client_pc\root\bin\pc) sits in.</summary>
    public static string? Find(Game game)
    {
        var exeRoot = Path.GetDirectoryName(game.ExePath) is { Length: > 0 } d ? Path.GetFullPath(Path.Combine(d, "..", "..", "..", "..")) : null;
        return new[] { game.InstallDir, exeRoot }.OfType<string>().Where(Directory.Exists).Select(r => GameFiles.Find(r, Pak)).FirstOrDefault(File.Exists);
    }

    public EngineInfo? Detect(Game game)
    {
        if (Find(game) is not { } pak) return null;
        EngineInfo Unsupported(string why) => new(Family, Version, null, "D3D12", false, why);
        using var zip = Open(pak, out var error);
        if (zip == null) return Unsupported($"{Path.GetFileName(pak)} doesn't open: {error}");
        string? why = null;
        foreach (var e in Sdcs(zip).OrderBy(e => e.Length))   // smallest first: a whole install stops at the first
        {
            var found = false;
            try { Each(e, null, (_, v) => found |= Dxbc.Valid(v) && Dxbc.Kind(v) >= 0, CancellationToken.None); }
            catch (Exception x) when (x is InvalidDataException or IOException) { why ??= $"{e.Name} doesn't read: {x.Message}"; }
            if (found) return new(Family, Version, null, "D3D12", false, null);
        }
        return Unsupported(why ?? $"no shader in {Path.GetFileName(pak)}'s {Folder}");
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var pak = Find(game) ?? throw new FileNotFoundException($"{game.InstallDir}: no {Pak}");
        using var zip = Open(pak, out var error) ?? throw new InvalidDataException($"{Path.GetFileName(pak)} doesn't open: {error}");
        var locs = new Dictionary<string, Loc>();
        var shaders = new Dictionary<string, ShaderInfo>();
        var maps = new List<ShaderMap>();
        var stamp = new StringBuilder();
        int files = 0, containers = 0, bad = 0, broken = 0;
        string? firstBroken = null;
        foreach (var e in Sdcs(zip))
        {
            ct.ThrowIfCancellationRequested();
            files++;
            stamp.Append($"{e.FullName}|{e.Length}|{e.Crc32:x8}\n");
            var pool = new List<string>();
            try
            {
                Each(e, null, (at, v) =>
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
                broken++;
                firstBroken ??= $"{e.Name}: {x.Message}";
            }
            if (pool.Where(shaders.ContainsKey).Distinct().ToList() is { Count: > 0 } members)
                maps.Add(new ShaderMap(CarvedReader.Sha1Hex(e.FullName), e.FullName, CarvedReader.Platform, members));
        }
        log?.Report($"{files} .sdc files, {containers} containers -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")}, {maps.Count} pools, no root signatures (built at run time)"
            + (broken > 0 ? $"; warning: {broken} .sdc files don't read whole ({firstBroken}), the rest of each left out" : "") + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        if (shaders.Count == 0) throw new InvalidDataException($"no shader in {Path.GetFileName(pak)}'s {Folder}");
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
        if (Find(game) is not { } pak) return;
        using var zip = Open(pak, out _);
        if (zip == null) return;
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Entry))
        {
            if (zip.GetEntry(file.Key) is not { } e) continue;
            var want = file.ToDictionary(x => (x.At.Block, x.At.Offset), x => x.Sha);
            var blocks = want.Keys.Select(k => k.Block).ToHashSet();
            try
            {
                Each(e, blocks.Contains, (at, v) =>
                {
                    if (want.TryGetValue((at.Block, at.Offset), out var sha) && Convert.ToHexStringLower(SHA1.HashData(v)) == sha) sink(sha, v.ToArray());
                }, ct);
            }
            catch (Exception x) when (x is InvalidDataException or IOException) { }   // changed since the index: what was read is sent
        }
    }

    /// <summary>The .sdc entries by name. A name the pak holds twice is left out: ReadShaders finds an entry by its name.</summary>
    static IEnumerable<ZipArchiveEntry> Sdcs(ZipArchive zip) => zip.Entries
        .Where(e => e.FullName.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) && e.FullName.IndexOf('/', Folder.Length) < 0
            && e.FullName.EndsWith(".sdc", StringComparison.OrdinalIgnoreCase))
        .GroupBy(e => e.FullName, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.First()).Where(e => !e.IsEncrypted)
        .OrderBy(e => e.FullName, StringComparer.Ordinal);

    delegate void Value(Loc at, ReadOnlySpan<byte> value);

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
