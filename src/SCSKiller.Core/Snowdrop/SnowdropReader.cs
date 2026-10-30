using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Snowdrop;

/// <summary>Massive's Snowdrop engine (Star Wars Outlaws), by &lt;codename&gt;\sdf\pc\data\sdf.sdftoc. The TOC, little endian:
/// "WEST", u32 version (0x29 and 0x2A read), u32, u32, u32, u32 slot count, u32 DDS count, u32 setting count, 16 bytes, a 48-byte
/// tag ("massive\0", 32 bytes, "ubisoft\0"), u8 signed, u8 encrypted, 320 bytes when signed, the settings (8 bytes each), 10
/// locales (6 bytes each), then the slots' u32 sizes, their 48-byte tags and their u64 hashes. Slot NNNN is the data file
/// sdf-&lt;part&gt;-NNNN[-&lt;locale&gt;].sdfdata. The file table after that is encrypted with a key the game doesn't publish,
/// so it isn't read: the data files are walked instead. A data file is "BERG", u32 version, then slices back to back: a
/// compressed slice is 64 KB pages, each an Oodle block whose header gives its length (u8 0x8C, u8 decoder as the stream numbers
/// them: 6 Kraken, 10 Mermaid or Selkie, 12 Leviathan, measured: Star Wars Outlaws only 10, a 3-byte big-endian quantum header whose low 18 bits are the packed size - 1, 3 checksum bytes when the decoder byte's top bit is set), a
/// stored slice raw bytes, crossed by searching for the next page. Some slices use blocks of up to 256 KB: a shader asset over
/// 64 KB starting one isn't read (measured: none in Star Wars Outlaws). A shader is an asset E3 02 BC DA, u32 1, then one DXBC
/// container (SM5 DXBC or DXIL), in pages or stored. A page doesn't say its raw size: a decode to 64 KB that fails still leaves
/// the asset's first bytes, and the container's size gives the exact one. An LZ block's first 8 raw bytes follow its 3-byte
/// chunk header as they are, so only pages starting an asset are decoded. No container carries RTS0 (measured: 0 of 24,893,
/// and the game compiles more at run time with its dxcompiler.dll): root signatures come from <see cref="SnowdropRootSig"/>. Maps: one pool for the game (every codename folder with a TOC, a DLC's walk keys start with its codename), as a program's VS and PS sit in different data files.
/// The first index walks every data file (68 GB) and keeps, in the game's data folder, which held shaders by name, size and
/// TOC hash: later ones walk only those and any whose size or hash changed.
/// EngineInfo: Family "Snowdrop", Version "SDF", Fork the codename folder (Star Wars Outlaws: "helix").</summary>
/// <param name="dataDir">the app's data folder, where the walk's per-file record is kept</param>
public sealed class SnowdropReader(string dataDir) : IEngineReader
{
    public const string Family = "Snowdrop", Version = "SDF";
    public int ReaderVersion(EngineInfo engine) => 2;
    const string Toc = @"sdf\pc\data\sdf.sdftoc", WalkFile = "sdf-walk.txt";
    /// <summary>Goes up whenever Walk, Asset or PageLength find assets otherwise: a record from before is walked again.</summary>
    const string WalkHeader = "snowdrop-walk 2";
    const int Page = 1 << 16;

    /// <summary>Bounds on what the files declare: TOC slots and settings, the walk record's bytes, a shader asset's bytes (container + 8), the window a
    /// walk reads through (an asset stored raw fits in it), and the page decodes one data file's resyncs may try.</summary>
    const int MaxSlots = 1 << 16, MaxSettings = 1 << 12, MaxWalkRecord = 16 << 20, MaxAsset = 8 << 20, WindowSize = MaxAsset + (1 << 20), MaxResyncs = 1 << 18;

    static ReadOnlySpan<byte> Prefix => [0xE3, 0x02, 0xBC, 0xDA, 1, 0, 0, 0, (byte)'D', (byte)'X', (byte)'B', (byte)'C'];

    static readonly Regex DataName = new(@"^sdf-[a-z]-(\d{4})(-[a-z]{2}-[a-z]{2})?\.sdfdata$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record Loc(string File, long Offset);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. In memory only, like the
    /// carver's: ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>Every codename folder's sdf.sdftoc (the base game's and each DLC's), in folder order.</summary>
    static List<string> FindAll(Game game)
    {
        if (!Directory.Exists(game.InstallDir)) return [];
        try
        {
            return [.. Directory.EnumerateDirectories(game.InstallDir).Order(StringComparer.Ordinal)
                .Select(d => GameFiles.Find(d, Toc)).OfType<string>().Where(File.Exists)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The base game's sdf.sdftoc: Star Wars Outlaws' folder when there, else the folder with the most data files.</summary>
    public static string? Find(Game game) => Base(FindAll(game));

    static string? Base(List<string> tocs)
    {
        try { return tocs.FirstOrDefault(t => Codename(t) == Outlaws) ?? tocs.MaxBy(t => Directory.EnumerateFiles(Path.GetDirectoryName(t)!, "*.sdfdata").Count()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return tocs.FirstOrDefault(); }
    }

    static string Codename(string toc) => Path.GetFileName(Path.GetFullPath(Path.Combine(toc, "..", "..", "..", "..")));

    public EngineInfo? Detect(Game game)
    {
        if (Find(game) is not { } toc) return null;
        var hashes = ReadHeader(toc, out var why);
        var codename = Codename(toc);
        return new(Family, Version, codename, "D3D12", false, hashes == null ? why : null, MaxAnisotropy: codename == Outlaws ? Anisotropy() : null);
    }

    const string Outlaws = "helix";

    public string DetectStamp(Game game, EngineInfo? engine) => Find(game) is { } toc && Codename(toc) == Outlaws ? KeyFiles.Stamp(SettingsFile()) ?? "-" : "";

    /// <summary>Star Wars Outlaws' settings file. Documents is the Windows known folder, SCSKILLER_TEST_DOCUMENTS when set (a test run's scratch copy).</summary>
    static string SettingsFile() => Path.Combine(Environment.GetEnvironmentVariable("SCSKILLER_TEST_DOCUMENTS") is { Length: > 0 } test ? test
        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Outlaws", "graphic settings.cfg");

    static readonly Regex AnisotropyLine = new(@"\[\s*""max anisotropy""\s*\]\s*=\s*""(\d{1,2})x""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The player's "max anisotropy" from the settings file (read only, first 64 KB), 16x when the file, the key or the value isn't there.</summary>
    internal static int Anisotropy()
    {
        try
        {
            using var fs = new FileStream(SettingsFile(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var buf = new byte[1 << 16];
            var text = Encoding.UTF8.GetString(buf, 0, fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false));
            return AnisotropyLine.Match(text) is { Success: true } m && int.Parse(m.Groups[1].Value) is (1 or 2 or 4 or 8 or 16) and var x ? x : SnowdropRootSig.DefaultAnisotropy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RegexMatchTimeoutException or NotSupportedException) { return SnowdropRootSig.DefaultAnisotropy; }
    }

    /// <summary>The TOC's hash of each slot, or null and why (another version, damaged). IOException when it doesn't read.</summary>
    internal static ulong[]? ReadHeader(string toc, out string? why)
    {
        FileStream fs;
        try { fs = new FileStream(toc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1); }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
        using var _ = fs;
        Span<byte> h = stackalloc byte[0x30 + 48 + 2];
        if (fs.ReadAtLeast(h, h.Length, throwOnEndOfStream: false) < h.Length || !h.StartsWith("WEST"u8)) { why = "sdf.sdftoc isn't a Snowdrop TOC"; return null; }
        uint version = U(h[4..]), slots = U(h[0x14..]), settings = U(h[0x1C..]);
        if (version is not (0x29 or 0x2A)) { why = $"sdf.sdftoc version {version} isn't read"; return null; }
        if (slots is 0 or > MaxSlots || settings > MaxSettings || !h[0x30..].StartsWith("massive\0"u8)) { why = "sdf.sdftoc's header doesn't read"; return null; }
        long skip = (h[0x30 + 48] != 0 ? 0x140 : 0) + 8L * settings + 60;
        var table = new byte[60 * slots];
        if (fs.Length < fs.Position + skip + table.Length) { why = "sdf.sdftoc is shorter than its header says"; return null; }
        fs.Position += skip;
        fs.ReadExactly(table);
        var hashes = new ulong[slots];
        for (var i = 0; i < slots; i++) hashes[i] = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(52 * (int)slots + 8 * i));
        why = null;
        return hashes;
    }

    /// <summary>A DLC folder's hashes, null when its TOC doesn't read: its data files are left out, the base game's still index.</summary>
    static ulong[]? TryHeader(string toc)
    {
        try { return ReadHeader(toc, out _); }
        catch (IOException) { return null; }
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Index(game, engine, log, null, ct);

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tocs = FindAll(game);
        var main = Base(tocs) ?? throw new FileNotFoundException($"{game.InstallDir}: no *\\{Toc}");
        var mainHashes = ReadHeader(main, out var why) ?? throw new InvalidDataException(why);
        Codecs.Load(zlib: false);
        if (OodleHelper.Instance == null) throw new InvalidDataException(Codecs.NativeOodle ? Codecs.NoOodleDll : Codecs.NoNativeOodle);
        var files = new List<(string Path, string Key)>();
        foreach (var toc in tocs)
        {
            var hashes = toc == main ? mainHashes : TryHeader(toc);
            if (hashes == null) continue;
            var scope = toc == main ? "" : Codename(toc) + "\\";   // the base game's keys stay as they were
            foreach (var path in GameFiles.EnumerateFiles(Path.GetDirectoryName(toc)!, "*.sdfdata", SearchOption.TopDirectoryOnly))
            {
                if (DataName.Match(Path.GetFileName(path)) is not { Success: true } m) continue;
                var slot = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                long size;
                try { size = new FileInfo(path).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                var hash = slot < hashes.Length ? hashes[slot] : 0;
                files.Add((path, FormattableString.Invariant($"{scope}{Path.GetFileName(path)}\t{size}\t{hash:x16}")));
            }
        }
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        var record = WalkPath(game);
        var known = ReadWalk(record);
        var todo = files.Where(f => known.GetValueOrDefault(f.Key, -1) != 0).ToList();

        var locs = new Dictionary<string, Loc>();
        var shaders = new Dictionary<string, ShaderInfo>();
        var members = new List<string>();
        var withShaders = 0;
        var walked = new Dictionary<string, int>();
        var stamp = new StringBuilder();
        var counts = new Counts();
        var page = new byte[Page];
        var window = new byte[WindowSize];
        long bytes = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, todo.Count));
            var (path, key) = todo[i];
            var pool = new List<string>();
            bool whole;
            try
            {
                using var fs = Open(path);
                bytes += fs.Length;
                whole = Walk(new Window(fs, window), page, counts, (at, c) =>
                {
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    pool.Add(sha);
                    if (!locs.TryAdd(sha, new Loc(path, at)) || Dxbc.Kind(c) < 0) return;
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                    }
                    catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { counts.Unparseable++; }   // valid container, odd program: not usable
                }, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                counts.Unreadable++;
                counts.FirstUnreadable ??= $"{Path.GetFileName(path)}: {e.Message}";
                continue;   // not recorded: walked again next time
            }
            if (whole) walked[key] = pool.Count;   // a file cut short is walked again next time
            if (pool.Count == 0) continue;
            stamp.Append(key).Append('\n');
            var before = members.Count;
            members.AddRange(pool.Where(shaders.ContainsKey));
            if (members.Count > before) withShaders++;
        }
        progress?.Report((todo.Count, todo.Count));
        foreach (var f in files)
            if (!walked.ContainsKey(f.Key) && known.GetValueOrDefault(f.Key, -1) == 0) walked[f.Key] = 0;
        log?.Report($"{files.Count} data files, {todo.Count} walked ({bytes / (1 << 20)} MB), {withShaders} with shaders -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {shaders.Values.Count(s => s.ShaderModel.Contains("_6_", StringComparison.Ordinal))} DXIL, no root signatures (built at run time)"
            + counts.Warnings + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        if (shaders.Count == 0) throw new InvalidDataException($"no shader in {string.Join(", ", tocs.Select(Path.GetDirectoryName))}{counts.Warnings}");
        WriteWalk(record, walked);   // never a walk that found nothing: a later reader walks it again
        located[game.Id] = locs;
        var stampHash = CarvedReader.Sha1Hex(stamp.ToString());
        return new ShaderIndex(stampHash, [CarvedReader.Platform], shaders, [new ShaderMap(stampHash, "sdf", CarvedReader.Platform, [.. members.Distinct()])]);
    }

    /// <summary>Reads each asset the index found again; one whose bytes changed since (game patched) isn't sent.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            try { Index(game, engine, null, ct); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return; }   // nothing to read back
            locs = located[game.Id];
        }
        var page = new byte[Page];
        var window = new byte[WindowSize];
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.File))
        {
            try
            {
                using var fs = Open(file.Key);
                var w = new Window(fs, window);
                foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Asset(w, at.Offset, page, out _) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // gone or locked since the index
        }
    }

    static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    string WalkPath(Game game) => Path.Combine(new AppStore(dataDir).GameDir(game.Id), WalkFile);

    /// <summary>The last walk's shader count per data file key (name, size, TOC hash); empty when there's none or it's damaged.</summary>
    static Dictionary<string, int> ReadWalk(string path)
    {
        var known = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxWalkRecord) return known;
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0] != WalkHeader) return known;
            foreach (var line in lines.Skip(1))
                if (line.LastIndexOf('\t') is var cut and > 0 && int.TryParse(line.AsSpan(cut + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    known[line[..cut]] = n;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { known.Clear(); }
        return known;
    }

    static void WriteWalk(string path, Dictionary<string, int> walked)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path + ".tmp", [WalkHeader, .. walked.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}\t{k.Value}")]);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the next index walks everything again
    }

    internal sealed class Counts
    {
        public int Unparseable, Broken, Cut, Unreadable, NotData;
        public string? FirstUnreadable;

        public string Warnings =>
            (Unparseable > 0 ? $", {Unparseable} unparseable" : "")
            + (Broken > 0 ? $"; warning: {Broken} shader assets don't decode or aren't valid containers" : "")
            + (Cut > 0 ? $"; warning: {Cut} data files have too much unpaged data, the rest of each left out" : "")
            + (NotData > 0 ? $"; {NotData} files aren't data files" : "")
            + (Unreadable > 0 ? $"; warning: {Unreadable} data files don't read ({FirstUnreadable})" : "");
    }

    /// <summary>Calls <paramref name="found"/> with each shader asset's offset and container, in file order; false when the
    /// file was cut short (<see cref="MaxResyncs"/>). Never throws for what the bytes say (an IOException for a read that fails).</summary>
    internal static bool Walk(Window w, byte[] page, Counts counts, Action<long, byte[]> found, CancellationToken ct)
    {
        if (!w.At(0, 4).StartsWith("BERG"u8)) { counts.NotData++; return true; }
        long p = 8;
        var resyncs = 0;
        var afterStored = false;
        while (p < w.Length)
        {
            ct.ThrowIfCancellationRequested();
            var len = PageLength(w.At(p, 8));
            // after a stored asset, stored bytes may follow: a header there is checked like one found by searching
            if (len > 0 && afterStored && !Resumes(w, p, len, page)) len = 0;
            afterStored = false;
            var h = w.At(p, 24);
            if (len > 0 && !Starts(h, len)) { p += len; continue; }   // a page, not a shader's first
            if (len > 0 || h.StartsWith(Prefix))
            {
                if (Asset(w, p, page, out var next) is { } c)
                {
                    found(p, c);
                    afterStored = len == 0;
                    p = next;
                    continue;
                }
                counts.Broken++;
                if (len > 0) { p += len; continue; }
            }
            // stored bytes: the next page that decodes, or a raw shader asset
            for (p++; ; p++)
            {
                ct.ThrowIfCancellationRequested();
                var s = w.At(p, 1 << 20);
                if (s.Length < 8) return true;
                var i = s.IndexOfAny((byte)0x8C, (byte)0xE3);
                if (i < 0) { p += s.Length - 7; continue; }
                p += i;
                if (w.At(p, 12).StartsWith(Prefix)) break;
                if (PageLength(w.At(p, 8)) is var n and > 0)
                {
                    if (++resyncs > MaxResyncs) { counts.Cut++; return false; }
                    if (Resumes(w, p, n, page)) break;
                }
            }
        }
        return true;
    }

    /// <summary>A page found by searching is one when it decodes to 64 KB, starts a shader, or ends where another page or the
    /// file starts.</summary>
    static bool Resumes(Window w, long p, int len, byte[] page)
    {
        page.AsSpan(0, Prefix.Length).Clear();
        if (Decode(w.At(p, len), page) == Page || page.AsSpan().StartsWith(Prefix)) return true;
        return p + len == w.Length || PageLength(w.At(p + len, 8)) > 0;
    }

    /// <summary>The shader asset at <paramref name="p"/>: its container (a valid one) and where the bytes after it start, or null.</summary>
    internal static byte[]? Asset(Window w, long p, byte[] page, out long next)
    {
        next = p;
        var h = w.At(p, 40);
        var len = PageLength(h);
        if (len == 0)
        {
            if (h.Length < 40 || !h.StartsWith(Prefix)) return null;
            var raw = U(h[32..]) + 8L;
            if (raw > MaxAsset || raw < 40) return null;
            var stored = w.At(p, (int)raw);
            if (stored.Length < raw) return null;
            next = p + raw;
            return Valid(stored[8..].ToArray());
        }
        var first = w.At(p, len);
        if (first.Length < len) return null;
        page.AsSpan(0, 40).Clear();
        var n = Decode(first, page);
        if (!page.AsSpan().StartsWith(Prefix)) return null;
        var total = U(page.AsSpan(32)) + 8L;
        if (total > MaxAsset || total < 40) return null;
        if (total <= Page)
        {
            if (Decode(first, page.AsSpan(0, (int)total)) != total) return null;
            next = p + len;
            return Valid(page[8..(int)total]);
        }
        if (n != Page) return null;
        var asset = new byte[total];
        page.CopyTo(asset, 0);
        long q = p + len;
        for (var done = Page; done < total;)
        {
            var size = PageLength(w.At(q, 8));
            var want = (int)Math.Min(Page, total - done);
            if (size > 0 && Decode(w.At(q, size), asset.AsSpan(done, want)) == want) q += size;
            else if (w.At(q, want) is { } stored && stored.Length == want)   // a page that didn't compress is stored as it is
            {
                stored.CopyTo(asset.AsSpan(done));
                q += want;
            }
            else return null;
            done += want;
        }
        next = q;
        return Valid(asset[8..]);
    }

    static byte[]? Valid(byte[] c) => Dxbc.Valid(c) ? c : null;

    /// <summary>An Oodle page's length from its header (Kraken, Mermaid, Selkie or Leviathan, a new stream), else 0.</summary>
    internal static int PageLength(ReadOnlySpan<byte> h)
    {
        if (h.Length < 5 || h[0] != 0x8C || (h[1] & 0x7F) is not (6 or 10 or 12)) return 0;
        var size = h[2] << 16 & 0x30000 | h[3] << 8 | h[4];
        return size == 0x3FFFF ? 0 : 5 + (h[1] >> 7) * 3 + size + 1;
    }

    /// <summary>The page starts an asset that may be a shader: its first raw bytes, after the 3-byte chunk header of an LZ
    /// block or right away in a stored quantum, are the asset's.</summary>
    static bool Starts(ReadOnlySpan<byte> h, int len)
    {
        var at = 5 + (h[1] >> 7) * 3;
        return h.Length >= at + 11 && (h[(at + 3)..].StartsWith(Prefix[..8]) || h[at..].StartsWith(Prefix[..8]));
    }

    static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        try { return (int)OodleHelper.Instance!.Decompress(src, dst); }
        catch (Exception e) when (e is not OutOfMemoryException) { return 0; }   // a block that doesn't decode
    }

    static uint U(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);

    /// <summary>A forward read window over a data file: the bytes at a position, at most <see cref="WindowSize"/> of them,
    /// fewer at the end.</summary>
    internal sealed class Window(Stream s, byte[] buf)
    {
        long start;
        int len;

        public long Length { get; } = s.Length;

        public ReadOnlySpan<byte> At(long pos, int n)
        {
            if (pos < 0 || pos >= Length) return default;
            n = Math.Min(n, buf.Length);
            if (pos < start || pos > start + len)
            {
                s.Position = pos;
                start = pos;
                len = 0;
            }
            else if (pos + n > start + len)
            {
                var keep = (int)(start + len - pos);
                Buffer.BlockCopy(buf, (int)(pos - start), buf, 0, keep);
                start = pos;
                len = keep;
            }
            while (pos + n > start + len && len < buf.Length && s.Read(buf, len, buf.Length - len) is var r and > 0) len += r;
            var at = (int)(pos - start);
            return buf.AsSpan(at, Math.Min(n, len - at));
        }
    }
}
