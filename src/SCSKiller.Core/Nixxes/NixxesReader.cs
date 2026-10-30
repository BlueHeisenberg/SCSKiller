using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.NaughtyDog;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Nixxes;

/// <summary>Nixxes' PC ports, whose data is in <see cref="Dsar"/> files (LZ4 blocks), by the layout inside them (Fork):
/// <list type="bullet">
/// <item><see cref="SuckerPunch"/> (Ghost of Tsushima): cache_pc\psarc\*.psarc, each a PSARC in a DSAR, the shaders DXIL
/// containers inline in the .xpps packages, each package a pool.</item>
/// <item><see cref="Insomniac"/> (Ratchet &amp; Clank: Rift Apart): a toc and the archives of d\, the shaders in
/// d\material and d\materialgraph (each a pool), every one shipped twice, as DXBC and DXIL; compute and ray tracing
/// helpers raw in the exe (a pool).</item>
/// <item><see cref="Stingray"/> (HELLDIVERS 2): data\bundles.*.nxa, DXBC shader model 5.0 only, each bundle a pool.</item>
/// </list>
/// A DXBC container stored as the twin of a DXIL one (<see cref="Twin"/>) is left out: D3D12 creates its pipelines from
/// DXIL; a DXBC shader without a twin is kept. No root signature ships (the games serialize theirs at run time) and
/// the shipped pipeline caches (default.pso, cache.pso) name their pipelines by engine hashes no shader file holds: a
/// game plans without a recording only where plan-rules.json has its roots, keyed to its store id (Ghost of Tsushima's
/// and Ratchet &amp; Clank's, taken from recordings). EngineInfo: Family "Nixxes", Version "DSAR", Fork the layout, Game
/// the store id and build.</summary>
public sealed class NixxesReader : IEngineReader
{
    public const string Family = "Nixxes", Version = "DSAR";
    public const string SuckerPunch = "Sucker Punch", Insomniac = "Insomniac", Stingray = "Stingray";

    /// <summary>Decompressed bytes Detect reads, smallest source first.</summary>
    const long DetectBudget = 64L << 20;
    /// <summary>Carving reads a source this much at a time; a container crossing a window's end is read on its own.</summary>
    const int Window = 16 << 20;

    /// <param name="Entry">the PSARC entry; <see cref="Whole"/>: the DSAR file's stream; <see cref="Plain"/>: the file itself</param>
    public sealed record Loc(string Path, int Entry, long Offset, int Size);
    const int Whole = -1, Plain = -2;
    const string Psarcs = @"cache_pc\psarc";

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. In memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The layout of the install, from its folders only; null = none of them.</summary>
    public static string? Layout(string installDir)
    {
        if (Directory.Exists(GameFiles.Find(installDir, Psarcs))) return SuckerPunch;
        if (File.Exists(GameFiles.Find(installDir, "toc")) && File.Exists(GameFiles.Find(installDir, @"d\materialgraph"))) return Insomniac;
        if (Files(GameFiles.Find(installDir, "data"), "bundles.*.nxa").Any()) return Stingray;
        return null;
    }

    public int ReaderVersion(EngineInfo engine) => 2;

    public EngineInfo? Detect(Game game)
    {
        if (!Directory.Exists(game.InstallDir) || Layout(game.InstallDir) is not { } layout) return null;
        var engine = new EngineInfo(Family, Version, layout, "D3D12", false, null, Game: game.Version is { } build ? $"{game.Id}@{build}" : game.Id);
        var meter = new Meter(DetectBudget);
        // HELLDIVERS 2's bundles start with 45-130 MB of other assets: a bundle that opens as DSAR is enough
        if (layout == Stingray)
            return Files(game, layout, detect: true).Any(f => { using var d = Dsar.Open(f.Path, meter); return d != null; }) ? engine : null;
        var found = false;
        var buf = new byte[Window];
        foreach (var f in Files(game, layout, detect: true))
        {
            Each(f, game.InstallDir, detect: true, meter, (_, _, src) =>
            {
                try { Carve(src, buf, (_, c) => found |= Dxbc.Kind(c) >= 0, meter, () => found, CancellationToken.None); }
                catch (InvalidDataException) { }
                return !found && meter.Left > 0;
            });
            if (found) return engine;
            if (meter.Left <= 0) break;
        }
        return null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var layout = engine.Fork ?? Layout(game.InstallDir) ?? throw new InvalidDataException("not a Nixxes DSAR install: scan the game again");
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var platformOf = new Dictionary<string, string>();
        var twinOf = new Dictionary<string, string>();   // a DXBC shader -> its DXIL twin
        var dxbcSeen = new HashSet<string>();
        var pools = new List<(string Library, List<string> Shaders)>();
        int containers = 0, carryRs = 0, bad = 0, unreadable = 0;
        var meter = new Meter(long.MaxValue);
        var buf = new byte[Window];
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var f in Files(game, layout, detect: false))
        {
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(f.Path) is { Exists: true } fi)
                content.AppendData(Encoding.UTF8.GetBytes(ContentStamps.Of(f.Rel, fi.FullName)));
            var opened = Each(f, game.InstallDir, detect: false, meter, (entry, library, src) =>
            {
                var pool = new List<string>();
                (long At, int Size, int Kind, string Sha, (int, int)? Pair)? prev = null;
                try
                {
                    Carve(src, buf, (at, c) =>
                    {
                        containers++;
                        var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                        var (kind, dxil) = (Dxbc.Kind(c), !Dxbc.Part(c, "DXIL"u8).IsEmpty);
                        if (!dxil) dxbcSeen.Add(sha);
                        else if (prev is { Pair: { } pair } p && p.At + p.Size == at && p.Kind == kind && pair == (p.Size, c.Length)) twinOf.TryAdd(p.Sha, sha);
                        prev = (at, c.Length, kind, sha, dxil ? null : Twin(src, at, meter));
                        if (locs.TryAdd(sha, new Loc(f.Path, entry, at, c.Length)))
                            try
                            {
                                if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                                {
                                    shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                                    if (CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) platformOf[sha] = lanes;
                                    if (!Dxbc.Part(c, "RTS0"u8).IsEmpty) carryRs++;
                                }
                            }
                            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                        if (shaders.ContainsKey(sha)) pool.Add(sha);
                    }, meter, () => false, ct);
                }
                catch (InvalidDataException) { unreadable++; }
                if (pool.Count > 0) pools.Add((library, pool));
                return true;
            });
            if (!opened) unreadable++;
        }
        var twins = twinOf.Where(t => shaders.ContainsKey(t.Key) && shaders.ContainsKey(t.Value)).Select(t => t.Key).ToList();
        foreach (var h in twins) shaders.Remove(h);
        var unpaired = dxbcSeen.Count(shaders.ContainsKey);
        if (shaders.Count == 0) throw new InvalidDataException($"{layout}: no shader in its archives: scan the game again");
        var maps = new List<ShaderMap>();
        foreach (var (lib, pool) in pools)
            foreach (var g in pool.Where(shaders.ContainsKey).Distinct().GroupBy(h => platformOf.GetValueOrDefault(h, CarvedReader.Platform)))
                maps.Add(new ShaderMap(CarvedReader.Sha1Hex(g.Key == CarvedReader.Platform ? lib : $"{lib}|{g.Key}"), lib, g.Key, g.ToList()));
        located[game.Id] = locs;
        var lanes = platformOf.Where(p => shaders.ContainsKey(p.Key)).GroupBy(p => p.Value).ToList();
        log?.Report($"{layout}: {maps.Count} pools ({meter.Read / 1e9:F2} GB decompressed): {containers} shader containers, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {shaders.Values.Count(s => s.Stage == Stage.Library)} ray tracing libraries, {shaders.Values.Count(s => s.InlineRayTracing)} tracing rays inline"
            + (twins.Count > 0 ? $"; {twins.Count} DXBC twins of DXIL shaders left out, {unpaired} DXBC shaders without one kept" : "")
            + $"; {carryRs} containers carry a root signature (not used)"
            + (unreadable > 0 ? $"; warning: {unreadable} sources don't decompress (the shaders before the failing bytes are kept)" : "")
            + (bad > 0 ? $"; {bad} unparseable" : "")
            + string.Concat(lanes.Select(g => $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs"))
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()),
            [CarvedReader.Platform, .. lanes.Select(g => g.Key).Order(StringComparer.Ordinal)], shaders, maps);
    }

    /// <summary>Decompresses only the blocks holding each container; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        bool? antiCheat = null;
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Path))
        {
            var byEntry = file.GroupBy(x => x.At.Entry).ToDictionary(g => g.Key, g => g.OrderBy(x => x.At.Offset).ToList());
            var kind = byEntry.Keys.Max() >= 0 ? Archive : byEntry.Keys.First();
            // anti-cheat may have arrived since the index: its binaries are never opened
            if (kind == Plain && (antiCheat ??= GameFiles.DetectAntiCheat(game) != AntiCheat.None)) continue;
            Each(new SourceFile(file.Key, kind, "", GameFiles.Rel(game.InstallDir, file.Key)), game.InstallDir, detect: false, null, (entry, _, src) =>
            {
                if (!byEntry.TryGetValue(entry, out var wanted)) return true;
                foreach (var (sha, at) in wanted)
                {
                    ct.ThrowIfCancellationRequested();
                    if (at.Offset > src.Length - at.Size) continue;
                    var b = new byte[at.Size];
                    try { src.Read(at.Offset, b); }
                    catch (InvalidDataException) { continue; }
                    if (Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
                }
                return true;
            }, byEntry.Keys.Where(e => e >= 0));
        }
    }

    /// <summary>Bytes to carve: <see cref="Length"/> bytes read at random.</summary>
    public sealed record Readable(long Length, ReadAt Read);
    public delegate void ReadAt(long from, Span<byte> into);
    public delegate void Found(long offset, ReadOnlySpan<byte> container);

    /// <param name="Kind"><see cref="Archive"/>: a PSARC, each .xpps entry a source; <see cref="Whole"/>: a DSAR file's
    /// stream; <see cref="Plain"/>: the file itself</param>
    /// <param name="Rel">its path under the install as Windows spells it</param>
    sealed record SourceFile(string Path, int Kind, string Library, string Rel);
    const int Archive = 0;

    /// <summary>Calls <paramref name="use"/> (entry, pool name, bytes) on each source of the file while it returns true:
    /// a PSARC's .xpps entries (Detect: the smallest first; <paramref name="only"/>: those entries), else the file's
    /// bytes. False when the file can't be opened, or <paramref name="meter"/> can't pay for its DSAR table or PSARC TOC and names.</summary>
    static bool Each(SourceFile f, string installDir, bool detect, Meter? meter, Func<int, string, Readable, bool> use, IEnumerable<int>? only = null)
    {
        switch (f.Kind)
        {
            case Archive:
                using (var a = Psarc.Open(f.Path, meter))
                {
                    if (a == null) return false;
                    var rel = f.Rel;
                    var entries = only?.Where(i => i > 0 && i < a.Count).ToList() ?? [.. Enumerable.Range(1, a.Count - 1)
                        .Where(i => a.Names[i]?.EndsWith(".xpps", StringComparison.OrdinalIgnoreCase) == true)
                        .OrderBy(i => detect ? a.Size(i) : 0).ThenBy(i => a.Names[i], StringComparer.Ordinal)];
                    foreach (var i in entries)
                        if (!use(i, $"{rel}:{a.Names[i]}", new Readable(a.Size(i), (from, into) => a.Read(i, from, into)))) break;
                    return true;
                }
            case Whole:
                using (var d = Dsar.Open(f.Path, meter))
                {
                    if (d == null) return false;
                    use(Whole, f.Library, new Readable(d.Length, d.Read));
                    return true;
                }
            default:
                SafeFileHandle h;
                try { h = File.OpenHandle(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
                using (h)
                    use(Plain, f.Library, new Readable(RandomAccess.GetLength(h), (from, into) =>
                    {
                        if (RandomAccess.Read(h, into, from) != into.Length) throw new InvalidDataException("file truncated");
                    }));
                return true;
        }
    }

    /// <summary>The files of the layout (Detect: the smallest first). Insomniac's exe only when the install has no
    /// anti-cheat: no reader opens such a game's binaries.</summary>
    static IEnumerable<SourceFile> Files(Game game, string layout, bool detect)
    {
        var dir = game.InstallDir;
        IEnumerable<FileInfo> Order(IEnumerable<FileInfo> files) =>
            detect ? files.OrderBy(f => f.Length) : files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
        switch (layout)
        {
            case SuckerPunch:
                var psarcs = GameFiles.Find(dir, Psarcs);
                foreach (var f in Order(Files(psarcs, "*.psarc"))) yield return new SourceFile(f.FullName, Archive, "", GameFiles.Rel(dir, f.FullName, psarcs, Psarcs));
                break;
            case Insomniac:
                foreach (var name in new[] { "material", "materialgraph" })
                    if (GameFiles.Find(dir, $@"d\{name}") is var path && File.Exists(path))
                        yield return new SourceFile(path, Whole, $@"d\{name}", GameFiles.Rel(dir, path, path, $@"d\{name}"));
                if (!detect && File.Exists(game.ExePath) && GameFiles.DetectAntiCheat(game) == AntiCheat.None)
                    yield return new SourceFile(game.ExePath, Plain, GameFiles.Rel(dir, game.ExePath), GameFiles.Rel(dir, game.ExePath));
                break;
            case Stingray:
                var data = GameFiles.Find(dir, "data");
                foreach (var f in Order(Files(data, "bundles.*.nxa")))
                    if (GameFiles.Rel(dir, f.FullName, data, "data") is var rel) yield return new SourceFile(f.FullName, Whole, rel, rel);
                break;
        }
    }

    static List<FileInfo> Files(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? [.. GameFiles.EnumerateFiles(new DirectoryInfo(dir), pattern, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })] : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Bytes read against a limit, across calls (Detect's budget spans its sources).</summary>
    public sealed class Meter(long limit)
    {
        public long Read { get; private set; }
        public long Left => limit - Read;
        /// <summary>Charges a read of <paramref name="n"/> bytes; false (nothing charged) when it would pass the limit.</summary>
        public bool Take(long n)
        {
            if (n > Left) return false;
            Read += n;
            return true;
        }
    }

    /// <summary>R&amp;C stores a shader's two forms as "IGSH", u32 DXBC size, u32 DXIL size, the DXBC container, then the
    /// DXIL one right after it (measured: every DXBC of d\material and d\materialgraph, 690 of the exe's 704). The sizes
    /// before the DXBC container at <paramref name="at"/>; null = no such header.</summary>
    static (int, int)? Twin(Readable src, long at, Meter meter)
    {
        if (at < 12 || !meter.Take(12)) return null;
        Span<byte> h = stackalloc byte[12];
        try { src.Read(at - 12, h); }
        catch (InvalidDataException) { return null; }
        return h.StartsWith("IGSH"u8) ? (BinaryPrimitives.ReadInt32LittleEndian(h[4..]), BinaryPrimitives.ReadInt32LittleEndian(h[8..])) : null;
    }

    /// <summary>Every valid container in the bytes, in order (the bytes inside one aren't searched), read a window at a
    /// time into <paramref name="buf"/>; a container crossing the window's end is read on its own. Every read is charged
    /// to <paramref name="meter"/>, and one it can't pay for isn't made: past the limit Carve stops. Stops too once
    /// <paramref name="stop"/> holds. A container whose own read fails is skipped; when a window's read fails, the
    /// containers wholly in its readable prefix are still found, then the failure is thrown.</summary>
    public static void Carve(Readable src, byte[] buf, Found hit, Meter meter, Func<bool> stop, CancellationToken ct)
    {
        if (buf.Length < 64) throw new ArgumentException("a window under 64 bytes", nameof(buf));
        for (long pos = 0; pos < src.Length && !stop();)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buf.Length, src.Length - pos);
            var n = (int)Math.Min(want, meter.Left);
            if (n < want && n < 64 || !meter.Take(n)) break;
            InvalidDataException? failed = null;
            try { src.Read(pos, buf.AsSpan(0, n)); }
            catch (InvalidDataException e) { (failed, n) = (e, Prefix(src, pos, buf.AsSpan(0, n), meter)); }
            var span = buf.AsSpan(0, n);
            var last = failed != null || pos + n == src.Length;
            var next = last ? pos + n : pos + n - 31;   // a header cut by the window's end is seen whole next time
            for (var i = 0; span[i..].IndexOf("DXBC"u8) is var k and >= 0;)
            {
                ct.ThrowIfCancellationRequested();
                if (stop()) return;
                var at = i + k;
                var off = pos + at;
                i = at + 1;
                if (at + 32 > n)
                {
                    if (last) continue;
                    break;
                }
                var size = Dxbc.HeaderSize(span.Slice(at, 32));
                if (size == 0 || size > src.Length - off) continue;
                ReadOnlySpan<byte> c;
                if (at + size <= n) c = span.Slice(at, size);
                else if (failed != null || !meter.Take(size)) continue;   // into the bytes that failed, or past the budget
                else
                {
                    var whole = new byte[size];
                    try { src.Read(off, whole); }
                    catch (InvalidDataException) { continue; }   // a header claiming bytes that don't read: keep scanning
                    c = whole;
                }
                if (!Dxbc.Valid(c)) continue;
                hit(off, c);
                i = Math.Min(at + size, n);
                next = Math.Max(next, off + size);
            }
            if (failed != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
            pos = next;
        }
    }

    /// <summary>How much of <paramref name="span"/> reads from <paramref name="pos"/>, to the byte: 64 KB steps, a quarter
    /// as long after each failure. Every read is charged; none past the limit.</summary>
    static int Prefix(Readable src, long pos, Span<byte> span, Meter meter)
    {
        var (ok, step) = (0, 64 << 10);
        while (ok < span.Length && step > 0)
        {
            var part = span.Slice(ok, Math.Min(step, span.Length - ok));
            if (!meter.Take(part.Length)) break;
            try
            {
                src.Read(pos + ok, part);
                ok += part.Length;
            }
            catch (InvalidDataException) { step /= 4; }
        }
        return ok;
    }
}
