using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.ForzaTech;

/// <summary>Playground's ForzaTech on PC (Forza Horizon 6), Steam and Xbox app alike: media\ShadersDXIL.zip and media\_library.
/// Every shader is a zip entry holding one raw container, in zips all over media: the engine's own in ShadersDXIL.zip
/// (*.vshader, *.pshader, *.cshader, *.hshader, *.dshader, *.gshader, *.rtshader), each material's per light scenario as
/// &lt;material&gt;&lt;Scenario&gt;.pcdxil.vso / .pso / .lso (VS, PS, ray tracing library). Every raster and compute shader carries
/// its root signature (RTS0; measured: all 32,528 of Forza Horizon 6), so it plans without a recording. Its ray tracing
/// libraries carry none (no RTS0, no RDAT subobjects): those need a recording. Some zip entries are encrypted (method 22:
/// audio and config, never a shader); they and any entry that doesn't read are skipped, as is a zip that doesn't open.
/// Maps: a .vso and the .pso of the same name in one zip, one IsPipeline map; each compute shader, one; the rest pooled per zip.
/// The exe isn't read (an Xbox app install's can't be).
/// EngineInfo: Family "ForzaTech", Version "pcdxil+RTS0" (or "pcdxil" when its shaders don't carry their root signatures).</summary>
public sealed class ForzaTechReader : IEngineReader
{
    public const string Family = "ForzaTech", Format = "pcdxil";
    const string Media = "media", EngineZip = @"media\ShadersDXIL.zip", Library = @"media\_library";

    /// <summary>Entries a zip may list and shader entries an index takes: beyond either, the zip or the rest of the install is
    /// left out.</summary>
    internal int MaxZipEntries { get; init; } = 1 << 20;
    internal int MaxShaderEntries { get; init; } = 1 << 21;
    /// <summary>Shader entries of ShadersDXIL.zip Detect reads for the RTS0 share.</summary>
    const int DetectSample = 256;

    static readonly Regex ShaderName = new(@"\.(pcdxil\.[a-z]so|[a-z]{1,2}shader)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record Loc(string Zip, string Entry);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public EngineInfo? Detect(Game game)
    {
        var zip = Path.Combine(game.InstallDir, EngineZip);
        if (!File.Exists(zip) || !Directory.Exists(Path.Combine(game.InstallDir, Library))) return null;
        int programs = 0, rts0 = 0;
        var counts = new Counts();
        using (var a = Open(zip, counts, MaxZipEntries))
            foreach (var e in a == null ? Enumerable.Empty<ZipArchiveEntry>() : ShaderEntries(a).Take(DetectSample))
                if (Read(e, counts) is { } c && Dxbc.Kind(c) is >= 0 and not 6)
                {
                    programs++;
                    if (!Dxbc.Part(c, "RTS0"u8).IsEmpty) rts0++;
                }
        return new EngineInfo(Family, Format + (programs > 0 && rts0 >= 0.9 * programs ? CarvedReader.EmbeddedRootSignatures : ""), null, "D3D12", false,
            programs == 0 ? $"no shader in {EngineZip}" : null);
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var rootSigs = new RootSigCarriers();
        var platformOf = new Dictionary<string, string>();
        var maps = new List<ShaderMap>();
        var seen = new HashSet<string>();
        var counts = new Counts();
        int zips = 0, entries = 0, pairs = 0, unlinked = 0, bad = 0;
        var stamp = new StringBuilder();
        foreach (var path in Zips(game))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(game.InstallDir, path);
            using var a = Open(path, counts, MaxZipEntries);
            if (a == null) { stamp.Append(Stamp(rel, path)); continue; }   // may hold shaders once it reads
            var list = ShaderEntries(a).ToList();
            if (list.Count == 0) continue;
            zips++;
            stamp.Append(Stamp(rel, path));
            var byName = new Dictionary<string, (string? Vs, string? Ps)>(StringComparer.OrdinalIgnoreCase);
            var pool = new List<string>();
            foreach (var e in list)
            {
                ct.ThrowIfCancellationRequested();
                if (entries >= MaxShaderEntries) { counts.Capped++; break; }
                if (Read(e, counts) is not { } c) continue;
                entries++;
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                if (locs.TryAdd(sha, new Loc(path, e.FullName)))
                {
                    rootSigs.See(c, sha, Dxbc.Kind(c));
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                        {
                            shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                            if (CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) platformOf[sha] = lanes;
                        }
                    }
                    catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                }
                if (!shaders.TryGetValue(sha, out var s)) continue;
                if (s.Stage == Stage.Compute)
                {
                    if (seen.Add(sha)) maps.Add(new ShaderMap(CarvedReader.Sha1Hex(sha), $"{rel}:{e.FullName}", PlatformOf(platformOf, [sha]), [sha], IsPipeline: true));
                }
                else if (s.Stage is Stage.Vertex or Stage.Pixel && e.FullName.EndsWith(s.Stage == Stage.Vertex ? ".pcdxil.vso" : ".pcdxil.pso", StringComparison.OrdinalIgnoreCase))
                {
                    var key = e.FullName[..^4];
                    var p = byName.GetValueOrDefault(key);
                    byName[key] = s.Stage == Stage.Vertex ? (sha, p.Ps) : (p.Vs, sha);
                }
                else pool.Add(sha);
            }
            foreach (var (key, (vs, ps)) in byName)
            {
                if (vs == null || ps == null) { pool.Add(vs ?? ps!); continue; }
                if (Northlight.NorthlightReader.Feeds([shaders[vs], shaders[ps]]) != true) { unlinked++; pool.Add(vs); pool.Add(ps); continue; }
                pairs++;
                var h = CarvedReader.Sha1Hex($"{vs},{ps}");
                if (seen.Add(h)) maps.Add(new ShaderMap(h, $"{rel}:{key}", PlatformOf(platformOf, [vs, ps]), [vs, ps], IsPipeline: true));
            }
            foreach (var g in pool.Distinct().GroupBy(h => platformOf.GetValueOrDefault(h, CarvedReader.Platform)))
                maps.Add(new ShaderMap(CarvedReader.Sha1Hex(g.Key == CarvedReader.Platform ? rel : $"{rel}|{g.Key}"), rel, g.Key, g.ToList()));
            if (counts.Capped > 0) break;
        }
        rootSigs.Apply(shaders);
        var libraries = shaders.Values.Count(s => s.Stage == Stage.Library);
        log?.Report($"{zips} zips with shaders, {entries} shader entries -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")) + "), "
            + $"{shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count()} embedded root signatures, "
            + $"{shaders.Values.Count(s => s.Stage != Stage.Library && s.RootSignature == null)} shaders without one; {pairs} VS+PS pairs by name, {maps.Count} maps; "
            + $"{libraries} ray tracing libraries without a root signature (they need a recording), {shaders.Values.Count(s => s.InlineRayTracing)} tracing rays inline"
            + (unlinked > 0 ? $"; {unlinked} pairs pooled: their VS doesn't feed their PS" : "")
            + counts.Warnings
            + (bad > 0 ? $"; {bad} unparseable" : "") + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        if (shaders.Count == 0) throw new InvalidDataException($"no shader in the zips of {Media}{counts.Warnings}");
        located[game.Id] = locs;
        return new ShaderIndex(CarvedReader.Sha1Hex(stamp.ToString()), [CarvedReader.Platform, .. platformOf.Values.Distinct().Order(StringComparer.Ordinal)], shaders, maps, rootSigs.Blobs);
    }

    static string PlatformOf(Dictionary<string, string> platformOf, string[] shas) =>
        shas.Select(h => platformOf.GetValueOrDefault(h, CarvedReader.Platform)).FirstOrDefault(p => p != CarvedReader.Platform, CarvedReader.Platform);

    /// <summary>Reads each entry the index found again; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        var counts = new Counts();
        foreach (var zip in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Zip))
        {
            using var a = Open(zip.Key, counts, MaxZipEntries);
            if (a == null) continue;   // gone or broken since the index
            foreach (var (sha, at) in zip)
            {
                ct.ThrowIfCancellationRequested();
                if (a.GetEntry(at.Entry) is { } e && Read(e, counts) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
            }
        }
    }

    /// <summary>What was skipped: zips that don't open or list too many entries, entries that don't read (encrypted, another
    /// compression, damaged, over <see cref="Dxbc.MaxSize"/>, longer than they say) or aren't one container.</summary>
    sealed class Counts
    {
        public int Broken, Huge, Unreadable, NotOne, Capped;
        public string? FirstBroken;

        public string Warnings =>
            (Broken > 0 ? $"; warning: {Broken} zips don't open ({FirstBroken}), left out" : "")
            + (Huge > 0 ? $"; warning: {Huge} zips declare too many entries, left out" : "")
            + (Unreadable > 0 ? $"; {Unreadable} shader entries don't read (encrypted or damaged), left out" : "")
            + (NotOne > 0 ? $"; {NotOne} shader entries aren't one container, left out" : "")
            + (Capped > 0 ? "; warning: too many shader entries, the rest left out" : "");
    }

    static IEnumerable<string> Zips(Game game)
    {
        var dir = Path.Combine(game.InstallDir, Media);
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.zip", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static IEnumerable<ZipArchiveEntry> ShaderEntries(ZipArchive a) => a.Entries.Where(e => ShaderName.IsMatch(e.FullName));

    static string Stamp(string rel, string path)
    {
        try
        {
            var f = new FileInfo(path);
            return $"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"{rel}|gone\n"; }
    }

    /// <summary>The zip read-only with its central directory loaded, or null (counted) when it doesn't open as one or declares
    /// more than <paramref name="maxEntries"/> entries (checked before the directory is loaded).</summary>
    static ZipArchive? Open(string path, Counts counts, int maxEntries)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (DeclaredEntries(fs) > maxEntries)
            {
                fs.Dispose();
                counts.Huge++;
                return null;
            }
            fs.Position = 0;
            var a = new ZipArchive(fs, ZipArchiveMode.Read);
            try { _ = a.Entries.Count; }   // the directory loads lazily: a damaged one throws here
            catch { a.Dispose(); throw; }
            return a;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            fs?.Dispose();
            counts.Broken++;
            counts.FirstBroken ??= Path.GetFileName(path);
            return null;
        }
    }

    /// <summary>The entry count the end of central directory record declares, or its ZIP64 record's when that is larger; 0 when
    /// there is none, and then <see cref="ZipArchive"/> decides.</summary>
    internal static long DeclaredEntries(Stream s)
    {
        const int Eocd = 22, Locator = 20, MaxComment = 0xFFFF;
        var tail = new byte[(int)Math.Min(s.Length, Locator + Eocd + MaxComment)];
        if (tail.Length < Eocd) return 0;
        s.Position = s.Length - tail.Length;
        s.ReadExactly(tail);
        var from = Math.Max(0, tail.Length - Eocd - MaxComment);
        // where ZipArchive looks: the last signature with a whole record after it, so a comment can't fake one
        var at = tail.AsSpan(from, tail.Length - Eocd + 4 - from).LastIndexOf("PK\u0005\u0006"u8);
        if (at < 0) return 0;
        at += from;
        long n = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 10));
        if (at < Locator || !tail.AsSpan(at - Locator).StartsWith("PK\u0006\u0007"u8)) return n;
        var zip64 = BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(at - 12));   // the ZIP64 end record's offset
        if (zip64 > (ulong)Math.Max(0, s.Length - 56)) return n;
        var rec = new byte[56];
        s.Position = (long)zip64;
        s.ReadExactly(rec);
        // ZipArchive follows ZIP64 on any of three sentinels, so its count bounds the directory whenever it is there
        return rec.AsSpan().StartsWith("PK\u0006\u0006"u8) ? Math.Max(n, (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32)), long.MaxValue)) : n;
    }

    /// <summary>The entry's bytes when it is exactly one valid container of at most <see cref="Dxbc.MaxSize"/>; else null (counted).</summary>
    static byte[]? Read(ZipArchiveEntry e, Counts counts)
    {
        if (e.Length is < 32 or > Dxbc.MaxSize || e.IsEncrypted) { counts.Unreadable++; return null; }
        var b = new byte[e.Length];
        try
        {
            using var s = e.Open();
            s.ReadExactly(b);
            if (s.ReadByte() != -1) { counts.Unreadable++; return null; }   // longer than its header says
        }
        catch (Exception x) when (x is InvalidDataException or IOException or NotSupportedException or ArgumentException or OverflowException) { counts.Unreadable++; return null; }   // method 22, damaged
        if (!Dxbc.Valid(b)) { counts.NotOne++; return null; }
        return b;
    }
}
