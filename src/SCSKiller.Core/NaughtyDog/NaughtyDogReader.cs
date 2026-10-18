using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Compression;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.NaughtyDog;

/// <summary>Naughty Dog's engine on PC: PSARC archives (<see cref="Psarc"/>) in the game folders under build\pc that hold
/// a shaders.psarc (<see cref="Archives"/>): The Last of Us Part I's build\pc\main; UNCHARTED 4's
/// Uncharted4_data\build\pc\uncharted4 and The Lost Legacy's \thelostlegacy, one install listed as two games
/// (<see cref="Collections"/>), each reading its own. The shaders are DXIL
/// containers inside a package (.pak) or a shaders.psarc entry (<see cref="Shaders"/>): in The Last of Us Part I each in
/// an ndshader record, followed by the root signature the game creates for it when it has one; in UNCHARTED bare, or
/// inline after the package's tables, the root signature as the container's RTS0 part. Pixel and compute shaders have
/// one, vertex shaders don't, so a stage set takes its PS's. A compute shader shipped with several root signatures is
/// a pipeline per root signature (an IsPipeline map naming it, <see cref="ShaderMap.RootSignature"/>); any other shader
/// keeps the first. Which VS a PS is drawn with isn't in the files: each package is one pool, paired by linkage, and a
/// plan needs a recording (no "+RTS0" in Version). The PSO dictionary packages (core.psarc's matdict "psodict" packages,
/// 13.6 GB) repeat the material shaders of the other packages, and the texture dictionaries (texturedict*, up to 2 GB an
/// entry) hold none: neither is read. EngineInfo: Family "Naughty Dog", Version "ndshader".</summary>
public sealed class NaughtyDogReader : IEngineReader
{
    public const string Family = "Naughty Dog", Version = "ndshader";
    const string Globals = "shaders.psarc";

    /// <summary>Bytes Detect reads (archive TOCs and names, then decompressed entries), smallest archive first (shaders.psarc: 6-8 MB).</summary>
    const long DetectBudget = 64L << 20;
    /// <summary>A bigger entry isn't read whole (an array's limit); in The Last of Us Part I only the texture dictionaries, not scanned, come near.</summary>
    const long MaxEntry = 1L << 30;

    /// <summary>Distinct root signatures kept per compute shader, and beyond each one's first in all; the rest are counted, left out.</summary>
    public int MaxRootsPerShader { get; init; } = 16;
    public int MaxExtraRoots { get; init; } = 1 << 20;

    public sealed record Loc(string Archive, int Entry, long Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public int ReaderVersion(EngineInfo engine) => 2;

    public EngineInfo? Detect(Game game)
    {
        var meter = new NixxesReader.Meter(DetectBudget);
        foreach (var f in Archives(game).OrderBy(f => !f.Name.Equals(Globals, StringComparison.OrdinalIgnoreCase)).ThenBy(f => f.Length))
        {
            using var a = Psarc.Open(f.FullName, meter);
            if (a == null) continue;
            for (var i = 1; i < a.Count && meter.Left > 0; i++)
            {
                if (!Scanned(f.Name, a.Names[i]) || !meter.Take(a.Size(i))) continue;
                byte[] d;
                try { d = a.Read(i, 0, (int)a.Size(i)); }
                catch (InvalidDataException) { continue; }
                if (Shaders(d).Any(s => Dxbc.Kind(d.AsSpan(s.At, s.Size)) >= 0)) return new EngineInfo(Family, Version, null, "D3D12", false, null);
            }
            if (meter.Left <= 0) break;
        }
        return null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var rootsOf = new Dictionary<string, List<(string Rs, string Lib)>>();
        var (overCap, severalOther) = (new HashSet<string>(), new HashSet<string>());
        var extraRoots = 0;
        var platformOf = new Dictionary<string, string>();
        var carriers = new RootSigCarriers();
        var maps = new List<ShaderMap>();
        int records = 0, packages = 0, badRs = 0, bad = 0, tooBig = 0, unreadable = 0;
        long read = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var f in Archives(game).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
        {
            using var a = Psarc.Open(f.FullName);
            if (a == null) continue;
            if (a.Oodle) App.Codecs.Load(zlib: false);   // a missing codec fails the index, not each entry
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            content.AppendData(Encoding.UTF8.GetBytes($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
            for (var i = 1; i < a.Count; i++)
            {
                if (!Scanned(f.Name, a.Names[i])) continue;
                ct.ThrowIfCancellationRequested();
                if (a.Size(i) > MaxEntry) { tooBig++; continue; }
                byte[] d;
                try { d = a.Read(i, 0, (int)a.Size(i)); }
                catch (InvalidDataException) { unreadable++; continue; }
                read += d.Length;
                var lib = $"{rel}:{a.Names[i]}";
                var pool = new List<string>();
                foreach (var (at, size, rsSize) in Shaders(d))
                {
                    records++;
                    var c = d.AsSpan(at, size);
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    if (locs.TryAdd(sha, new Loc(f.FullName, i, at, size)))
                        try
                        {
                            if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                            {
                                shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                                if (CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) platformOf[sha] = lanes;
                                if (!Dxbc.Part(c, "RTS0"u8).IsEmpty)
                                    if (Dxbc.RootSignatureValid(c)) carriers.See(c, sha, Dxbc.Kind(c));
                                    else badRs++;
                            }
                        }
                        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                    if (!shaders.ContainsKey(sha)) continue;
                    pool.Add(sha);
                    if (rsSize == 0) continue;
                    var rs = d.AsSpan(at + size, rsSize);
                    if (!Dxbc.RootSignatureValid(rs)) { badRs++; continue; }
                    var rsSha = Convert.ToHexStringLower(SHA1.HashData(rs));
                    if (!rootsOf.TryGetValue(sha, out var roots)) rootsOf[sha] = [(rsSha, lib)];
                    else if (!roots.Exists(r => r.Rs == rsSha))
                    {
                        if (shaders[sha].Stage != Stage.Compute) { severalOther.Add(sha); continue; }
                        if (roots.Count >= MaxRootsPerShader || extraRoots >= MaxExtraRoots) { overCap.Add(sha); continue; }
                        roots.Add((rsSha, lib));
                        extraRoots++;
                    }
                    locs.TryAdd(rsSha, new Loc(f.FullName, i, at + size, rsSize));
                    carriers.See(rs, rsSha, -1);   // also when an entry before had it bare: that one isn't a shader, never seen
                }
                if (pool.Count == 0) continue;
                packages++;
                foreach (var g in pool.Distinct().GroupBy(h => platformOf.GetValueOrDefault(h, CarvedReader.Platform)))
                    maps.Add(new ShaderMap(CarvedReader.Sha1Hex(g.Key == CarvedReader.Platform ? lib : $"{lib}|{g.Key}"), lib, g.Key, g.ToList()));
            }
        }
        if (shaders.Count == 0) throw new InvalidDataException(@"build\pc: no shader in its archives: scan the game again");
        carriers.Apply(shaders);
        // the first root stays the shader's: its pool plans that one, and the per-root maps collapse onto it
        foreach (var (sha, roots) in rootsOf)
        {
            shaders[sha] = shaders[sha] with { RootSignature = roots[0].Rs };
            if (roots.Count > 1)
                foreach (var (rs, lib) in roots)
                    maps.Add(new ShaderMap(CarvedReader.Sha1Hex(sha), lib, platformOf.GetValueOrDefault(sha, CarvedReader.Platform), [sha], IsPipeline: true, rs));
        }
        var severalCs = rootsOf.Values.Where(r => r.Count > 1).ToList();
        located[game.Id] = locs;
        var stages = shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count());
        log?.Report($"{packages} packages with shaders ({read / 1e9:F2} GB decompressed): {records} shader containers, {shaders.Count} shaders ("
            + string.Join(", ", stages.Select(g => $"{g.Count()} {g.Key}")) + $"), {shaders.Values.Select(s => s.RootSignature).Concat(maps.Select(m => m.RootSignature)).OfType<string>().Distinct().Count()} root signatures, carried by "
            + string.Join(", ", stages.Select(g => $"{g.Count(s => s.RootSignature != null)} {g.Key}"))
            + $"; {shaders.Values.Count(s => s.Stage == Stage.Library)} ray tracing libraries, {shaders.Values.Count(s => s.InlineRayTracing)} tracing rays inline"
            + (severalCs.Count > 0 ? $"; {severalCs.Count} compute shaders ship with several root signatures: a pipeline per root signature, {severalCs.Sum(r => r.Count)} in all" : "")
            + string.Concat(severalOther.GroupBy(h => shaders[h].Stage).Select(g => $"; {g.Count()} {g.Key} shaders ship with several root signatures: the first kept, the files don't pair them"))
            + (overCap.Count > 0 ? $"; warning: {overCap.Count} shaders ship with more root signatures than kept ({MaxRootsPerShader} a shader, {MaxExtraRoots:N0} more in all)" : "")
            + (badRs > 0 ? $"; {badRs} root signatures the runtime would reject, left out" : "")
            + (tooBig > 0 ? $"; warning: {tooBig} entries over {MaxEntry >> 30} GB not read" : "")
            + (unreadable > 0 ? $"; warning: {unreadable} entries don't decompress" : "")
            + (bad > 0 ? $"; {bad} unparseable" : "")
            + string.Concat(platformOf.Values.GroupBy(p => p).Select(g => $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs"))
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. platformOf.Values.Distinct().Order(StringComparer.Ordinal)], shaders, maps, carriers.Blobs);
    }

    /// <summary>Decompresses only the blocks holding each container; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Archive))
        {
            using var a = Psarc.Open(file.Key);
            if (a == null) continue;   // gone or changed since the index
            if (a.Oodle) App.Codecs.Load(zlib: false);
            foreach (var (sha, at) in file.OrderBy(x => x.At.Entry).ThenBy(x => x.At.Offset))
            {
                ct.ThrowIfCancellationRequested();
                byte[] b;
                try { b = a.Read(at.Entry, at.Offset, at.Size); }
                catch (InvalidDataException) { continue; }
                if (Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
            }
        }
    }

    /// <summary>The containers of a decompressed entry, as <see cref="Records"/> gives them: its ndshader records, or in an
    /// entry with none (UNCHARTED: a bare container, or containers after a package's tables) every valid container.</summary>
    public static IEnumerable<(int At, int Size, int RsSize)> Shaders(byte[] d)
    {
        var any = false;
        foreach (var r in Records(d)) { any = true; yield return r; }
        if (!any)
            foreach (var (at, c) in Dxbc.Containers(d)) yield return (at, c.Length, 0);
    }

    /// <summary>The ndshader records in a decompressed entry, as (offset of the DXIL container, its size, the size of the
    /// root-signature container after it; 0 = none). A record: "ndshader", u32 version, u32 flags, a 16-byte hash, u32 DXIL
    /// size, u32 root-signature size, then the containers. A record whose sizes don't frame a valid container, and an RTS0-only
    /// one after it, isn't one (some use another header layout).</summary>
    public static IEnumerable<(int At, int Size, int RsSize)> Records(byte[] d)
    {
        for (var o = d.AsSpan().IndexOf("ndshader"u8); o >= 0;)
        {
            var next = o + 8;
            if (o + 40 <= d.Length)
            {
                long size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 32)), rsSize = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 36));
                var at = o + 40;
                if (size >= 32 && at + size + rsSize <= d.Length && Dxbc.HeaderSize(d.AsSpan(at)) == size && Dxbc.Valid(d.AsSpan(at, (int)size))
                    && (rsSize == 0 || Dxbc.IsRootSignatureOnly(d.AsSpan(at + (int)size, (int)rsSize))))
                {
                    yield return (at, (int)size, (int)rsSize);
                    next = at + (int)(size + rsSize);
                }
            }
            var k = d.AsSpan(next).IndexOf("ndshader"u8);
            o = k < 0 ? -1 : next + k;
        }
    }

    /// <summary>The entries that hold shaders: every one of shaders.psarc, the packages elsewhere but the PSO and texture dictionaries.</summary>
    static bool Scanned(string archive, string? name) => name != null && (archive.Equals(Globals, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && !name.Contains("psodict", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("texturedict", StringComparison.OrdinalIgnoreCase));

    /// <summary>The archives of each folder of build\pc (in the install or a folder of it, UNCHARTED's Uncharted4_data) that
    /// holds a shaders.psarc; for a game of a collection only its own (<see cref="CollectionGame.Data"/>). A folder with an
    /// updated-fx-paks archive keeps its material shaders there, and its other archives (UNCHARTED: about 120 GB of levels,
    /// textures and animation) hold none: only those two are read.</summary>
    static IEnumerable<FileInfo> Archives(Game game)
    {
        var install = new DirectoryInfo(game.InstallDir);
        if (!install.Exists) return [];
        var flat = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var only = Collections.Member(game.Id) is { } m ? Path.GetFullPath(Path.Combine(install.FullName, m.Game.Data)) : null;
        static bool Fx(FileInfo f) => f.Name.StartsWith("updated-fx-paks", StringComparison.OrdinalIgnoreCase);
        return new[] { install }.Concat(install.EnumerateDirectories("*", flat))
            .Select(d => new DirectoryInfo(Path.Combine(d.FullName, "build", "pc"))).Where(d => d.Exists)
            .SelectMany(pc => pc.EnumerateDirectories("*", flat))
            .Where(d => File.Exists(Path.Combine(d.FullName, Globals)) && (only == null || Path.GetFullPath(d.FullName).Equals(only, StringComparison.OrdinalIgnoreCase))).SelectMany(d =>
        {
            var all = d.EnumerateFiles("*.psarc", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList();
            return all.Any(Fx) ? all.Where(f => Fx(f) || f.Name.Equals(Globals, StringComparison.OrdinalIgnoreCase)) : all;
        });
    }
}

/// <summary>A PSARC archive. Big-endian header: "PSAR", u16 major, u16 minor, the compression FourCC ("zlib", "oodl"), u32 TOC
/// length (header included), u32 TOC entry size, u32 entry count, u32 block size, u32 flags. Per entry: the MD5 of its name,
/// u32 first block, u40 size, u40 file offset. Then each block's stored size (u16 for 64 KB blocks; 0 = a full block). A block
/// is compressed on its own, or stored when its stored size is what it holds; an "oodl" archive may hold zlib blocks too.
/// Entry 0 lists the other entries' names, matched to entries by MD5 (the order differs). Nixxes' ports (Ghost of Tsushima)
/// wrap the whole archive in a <see cref="Dsar"/>, its entries then stored.</summary>
public sealed class Psarc : IDisposable
{
    readonly SafeFileHandle h;
    readonly Dsar? dsar;
    readonly bool oodle;
    public bool Oodle => oodle;
    readonly int blockSize;
    readonly (int Block, long Size, long Offset)[] entries;
    /// <summary>The TOC after the header; each block's stored size is read from it where needed.</summary>
    readonly byte[] toc;
    readonly int table, width;
    public string?[] Names { get; }
    public int Count => entries.Length;
    public long Size(int entry) => entries[entry].Size;

    /// <summary>Wrapped in a DSAR file.</summary>
    public bool Wrapped => dsar != null;

    /// <summary>The most TOC bytes an archive may claim, checked before they're read. A 64 GB archive of 64 KB blocks has 2 MB
    /// of block sizes, 100,000 entries 3 MB.</summary>
    public const int MaxToc = 32 << 20;
    const int MaxNames = 64 << 20;

    Psarc(SafeFileHandle h, Dsar? dsar, bool oodle, int blockSize, (int, long, long)[] entries, byte[] toc, int table, int width)
    {
        (this.h, this.dsar, this.oodle, this.blockSize, this.entries, this.toc, this.table, this.width) = (h, dsar, oodle, blockSize, entries, toc, table, width);
        Names = new string?[entries.Length];
    }

    /// <summary>The archive with its names, or null when the file isn't one (or can't be read). Its TOC and name list are
    /// charged to <paramref name="meter"/> before they're read; null when it can't pay for them.</summary>
    public static Psarc? Open(string path, NixxesReader.Meter? meter = null)
    {
        SafeFileHandle h;
        try { h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        Dsar? dsar = null;
        try
        {
            dsar = Dsar.Open(h, meter);
            var p = Parse(h, dsar, meter);
            if (p != null) return p;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException or ArgumentException or OverflowException) { }
        if (dsar != null) dsar.Dispose();
        else h.Dispose();
        return null;
    }

    int ReadAt(Span<byte> b, long at)
    {
        if (dsar == null) return RandomAccess.Read(h, b, at);
        if (at < 0 || at > dsar.Length - b.Length) return 0;
        dsar.Read(at, b);
        return b.Length;
    }

    static Psarc? Parse(SafeFileHandle h, Dsar? dsar, NixxesReader.Meter? meter)
    {
        var length = dsar?.Length ?? RandomAccess.GetLength(h);
        var head = new byte[32];
        var reader = new Psarc(h, dsar, false, 1, [], [], 0, 2);
        if (reader.ReadAt(head, 0) != 32 || !head.AsSpan(0, 4).SequenceEqual("PSAR"u8)) return null;
        uint U(int at) => BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(at));
        var comp = Encoding.ASCII.GetString(head, 8, 4);
        uint tocLength = U(12), entrySize = U(16), n = U(20), bs = U(24);
        if (comp is not ("zlib" or "oodl") || bs is < 1024 or > (1 << 24) || entrySize < 30 || n is 0 or > 1 << 24
            || tocLength > length || tocLength > MaxToc || 32 + (long)n * entrySize > tocLength || meter?.Take(tocLength) == false) return null;
        var toc = new byte[tocLength - 32];
        if (reader.ReadAt(toc, 32) != toc.Length) return null;
        var width = bs <= 1 << 16 ? 2 : 3;
        var table = (int)(n * entrySize);
        var blocks = (toc.Length - table) / width;
        var entries = new (int, long, long)[n];
        var md5 = new Dictionary<UInt128, int>();
        for (var i = 0; i < n; i++)
        {
            var e = toc.AsSpan((int)(i * entrySize));
            var (first, size, offset) = (BinaryPrimitives.ReadUInt32BigEndian(e[16..]),
                (long)e[20] << 32 | BinaryPrimitives.ReadUInt32BigEndian(e[21..]), (long)e[25] << 32 | BinaryPrimitives.ReadUInt32BigEndian(e[26..]));
            if (first + (size + bs - 1) / bs > blocks || offset > length) return null;
            entries[i] = ((int)first, size, offset);
            md5.TryAdd(BinaryPrimitives.ReadUInt128BigEndian(e), i);
        }
        var p = new Psarc(h, dsar, comp == "oodl", (int)bs, entries, toc, table, width);
        if (p.Size(0) > MaxNames || meter?.Take(p.Size(0)) == false) return null;
        var list = p.Read(0, 0, (int)p.Size(0));
        Span<byte> hash = stackalloc byte[16];
        foreach (var r in list.AsSpan().SplitAny("\n\r\0"u8))   // a string only for a name an entry has: no more than the entries
        {
            var name = list.AsSpan(r);
            if (name.IsEmpty) continue;
            MD5.HashData(name, hash);
            if (md5.TryGetValue(BinaryPrimitives.ReadUInt128BigEndian(hash), out var i) && i > 0) p.Names[i] ??= Encoding.UTF8.GetString(name);
        }
        return p;
    }

    /// <summary><paramref name="count"/> bytes of an entry from <paramref name="from"/>, decompressing only the blocks they're in.
    /// InvalidDataException when the entry or range isn't there or the blocks don't decompress to their size.</summary>
    public byte[] Read(int entry, long from, int count)
    {
        var (first, _, end) = Range(entry, from, count);
        for (var k = 0; k < (int)((from + count + blockSize - 1) / blockSize); k++) end += Stored(first + k);
        if (end > Length) throw new InvalidDataException("archive truncated");   // before allocating for bytes it doesn't have
        var output = new byte[count];
        Read(entry, from, output);
        return output;
    }

    /// <summary>Fills <paramref name="output"/> from <paramref name="from"/> of an entry, as <see cref="Read(int, long, int)"/>.</summary>
    public void Read(int entry, long from, Span<byte> output)
    {
        var (first, size, offset) = Range(entry, from, output.Length);
        var count = output.Length;
        var block = (int)(from / blockSize);
        var (at, length) = (offset, Length);
        for (var k = 0; k < block; k++) at += Stored(first + k);
        for (long pos = (long)block * blockSize; pos < from + count; pos += blockSize, block++)
        {
            var want = (int)Math.Min(blockSize, size - pos);
            var stored = Stored(first + block);
            if (at > length - stored) throw new InvalidDataException("archive truncated");
            var raw = new byte[stored];
            if (ReadAt(raw, at) != raw.Length) throw new InvalidDataException("archive truncated");
            at += raw.Length;
            var data = raw.Length == want ? raw : Decompress(raw, want);
            var lo = (int)Math.Max(0, from - pos);
            var hi = (int)Math.Min(want, from + count - pos);
            data.AsSpan(lo, hi - lo).CopyTo(output[(int)(pos + lo - from)..]);
        }
    }

    (int First, long Size, long Offset) Range(int entry, long from, int count)
    {
        if ((uint)entry >= (uint)entries.Length) throw new InvalidDataException("no such entry");
        var e = entries[entry];
        if (from < 0 || count < 0 || from > e.Size - count) throw new InvalidDataException("past the entry's end");
        return e;
    }

    long Length => dsar?.Length ?? RandomAccess.GetLength(h);

    int Stored(int block)
    {
        var at = table + block * width;
        var v = width == 2 ? BinaryPrimitives.ReadUInt16BigEndian(toc.AsSpan(at)) : toc[at] << 16 | toc[at + 1] << 8 | toc[at + 2];
        return v == 0 ? blockSize : v;
    }

    byte[] Decompress(byte[] raw, int want)
    {
        if (!oodle || raw[0] == 0x78)   // a zlib header
        {
            var b = new byte[want];
            using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
            try { z.ReadExactly(b); }
            catch (EndOfStreamException e) { throw new InvalidDataException("a zlib block is short", e); }
            return b;
        }
        App.Codecs.Load(zlib: false);
        byte[] o;
        try { o = Compression.Decompress(raw, want, CompressionMethod.Oodle); }
        catch (Exception e) when (e is not InvalidDataException) { throw new InvalidDataException("an Oodle block doesn't decompress", e); }
        return o.Length == want ? o : throw new InvalidDataException("an Oodle block didn't decompress to its size");
    }

    public void Dispose()
    {
        if (dsar != null) dsar.Dispose();
        else h.Dispose();
    }
}
