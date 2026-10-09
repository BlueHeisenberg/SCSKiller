using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.ReEngine;

/// <summary>Capcom RE Engine games (re_chunk_000.pak next to the exe). Every file lives in the game's packages
/// (<see cref="RePak"/>): the base package, its .patch_NNN packages (a later package's entry replaces an earlier one with
/// the same name hash), .sub_NNN packages and dlc\*.pak. Shaders: master materials ("SDF\0", the community's .mmtr) and
/// OpenColorIO files ("OCIO") hold compiled DXBC/DXIL containers back to back after their tables (PRAGMATA: 846 files,
/// 4.30 GB, 94.5% of it containers); a 25-file sample of every other magic holds no valid container. Names are hashes,
/// so files are recognized by their first 4 decompressed bytes. No RTS0: the engine builds its root signatures at run
/// time, so D3D12 needs a recording. Maps: one pool per shader file (its passes and variants are what pair).
/// EngineInfo: Family "RE Engine", Version "PAK 4.2" (the package format this reads).
/// ponytail: shader files are found by magic; a title keeping its shaders under another magic is missed (Detect then
/// reports none found, naming the commonest magics). Add its magic to <see cref="ShaderMagics"/>.</summary>
/// <param name="dataDir">the app's data folder, where a user puts a game's table-key modulus (<see cref="ModulusFile"/>)</param>
/// <param name="download">fetches a URL's text (null on failure): <see cref="Download.Text"/>, a fake in tests</param>
public sealed class ReEngineReader(string dataDir, Func<string, string?>? download = null) : IEngineReader
{
    public const string Family = "RE Engine";

    /// <summary>The public RSA modulus the packages' table keys are made with (<see cref="RePak.Key"/>). None is shipped, and
    /// the game doesn't carry it in the clear (PRAGMATA's exe is packed: the modulus isn't in any of its files on disk, in
    /// either byte order): this file, local only (never in plans, logs or anything shared), hex in either byte order (the
    /// one that opens the packages wins; "0x" and separators are ignored). A user may write it; else it is downloaded
    /// (<see cref="ReePakRs"/>) when an encrypted table needs it, and kept here only if it opens the base package.</summary>
    public string ModulusFile(Game game) => Path.Combine(new AppStore(dataDir).GameDir(game.Id), "pak.modulus");

    /// <summary>The community's published modulus: ree-pak-rs's table-key cipher (a Rust byte array, little endian), pinned
    /// to commit 25562e6a (2026-04-02) so an upstream edit can't change what is fetched.</summary>
    public const string ReePakRs = "https://raw.githubusercontent.com/eigeen/ree-pak-rs/25562e6a6f9a52a43b80d54be91e4feb7ac7668b/ree-pak-core/src/pak/cipher/pak.rs";

    List<BigInteger> Moduli(Game game)
    {
        try
        {
            var hex = string.Concat(File.ReadAllText(ModulusFile(game)).Replace("0x", "", StringComparison.OrdinalIgnoreCase).Where(Uri.IsHexDigit));
            return hex.Length is 0 || hex.Length % 2 != 0 ? [] : BothOrders(Convert.FromHexString(hex));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static List<BigInteger> BothOrders(byte[] b) =>
        new[] { new BigInteger(b, isUnsigned: true), new BigInteger(b, isUnsigned: true, isBigEndian: true) }.Where(m => m > 1).ToList();

    /// <summary>Every byte array of 64+ bytes in Rust source ("[0x12, 0x34, ...]"): the candidate moduli (the 4-byte
    /// exponent and anything else short left out).</summary>
    public static List<byte[]> ReePakModuli(string source) =>
        Regex.Matches(source, @"\[([^\[\]]*)\]").Select(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 64 && t.All(x => Regex.IsMatch(x, "^0x[0-9A-Fa-f]{1,2}$")))
            .Select(t => t.Select(x => Convert.ToByte(x[2..], 16)).ToArray()).ToList();

    /// <summary>The game's packages, or why they can't be read (a missing modulus says where to put it).</summary>
    Paks Open(Game game, string dir)
    {
        var paks = new Paks(dir, Moduli(game));
        if (paks.Error?.EndsWith(RePak.NoModulus) != true) return paks;
        // none given: the published one, kept only if it opens the package that needs it (tried every Detect until one does)
        if ((download ?? Download.Text)(ReePakRs) is { } source && ReePakModuli(source).FirstOrDefault(b => Opens(paks.Failed!, BothOrders(b))) is { } m)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModulusFile(game))!);
            File.WriteAllText(ModulusFile(game), Convert.ToHexString(m));
            paks.Dispose();
            return new Paks(dir, BothOrders(m));
        }
        paks.Error += $", and none that opens it could be downloaded (ree-pak-rs's): put the game's table-key modulus (hex) in {ModulusFile(game)}";
        return paks;
    }

    static bool Opens(string pak, List<BigInteger> moduli)
    {
        try { RePak.Open(pak, moduli).Dispose(); return true; }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>First 4 bytes (little endian) of the files that hold shader containers: "SDF\0", "OCIO".</summary>
    static readonly uint[] ShaderMagics = [0x00464453, 0x4F49434F];

    public sealed record Loc(string Pak, int Entry, int Offset, int Size);

    /// <summary>Where each container was found, per game id, from the last Index (in memory only, like CarvedReader).</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The folder holding re_chunk_000.pak: the exe's, else the install folder.</summary>
    public static string? Find(Game game) =>
        new[] { Path.GetDirectoryName(game.ExePath), game.InstallDir }.FirstOrDefault(d => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "re_chunk_000.pak")));

    public EngineInfo? Detect(Game game)
    {
        if (Find(game) is not { } dir) return null;
        using var paks = Open(game, dir);
        var version = paks.Base is { } b ? $"PAK {b.Major}.{b.Minor}" : "PAK ?";
        if (paks.Error != null) return new(Family, version, null, Api(dir, null), false, paks.Error);
        // the first shader file that holds a container settles it (PRAGMATA: within the first few hundred entries)
        var magics = new Dictionary<uint, int>();
        foreach (var (pak, i) in paks.Final())
        {
            if (Magic(pak, i) is not { } m) continue;
            magics[m] = magics.GetValueOrDefault(m) + 1;
            if (!ShaderMagics.Contains(m)) continue;
            byte[] data;
            try { data = pak.Read(pak.Entries[i]); }
            catch (Exception e) when (e is InvalidDataException or IOException or ZstdSharp.ZstdException) { continue; }
            // any DXIL in it: a DirectX 12 game may ship SM5 DXBC beside its SM6 (Resident Evil Village's first file: 264 of 378)
            var cs = Dxbc.Containers(data).Select(x => x.Container).ToList();
            if (cs.Count > 0) return new(Family, version, null, Api(dir, cs.Any(c => !Dxbc.Part(c, "DXIL"u8).IsEmpty)), false, null);
        }
        var top = string.Join(", ", magics.OrderByDescending(p => p.Value).Take(5).Select(p => $"{Name(p.Key)} x{p.Value}"));
        return new(Family, version, null, Api(dir, null), false, $"no shader containers in its packages' material files (commonest files: {top})");
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var dir = Find(game) ?? throw new DirectoryNotFoundException($"{game.InstallDir}: no re_chunk_000.pak");
        using var paks = Open(game, dir);
        if (paks.Error != null) throw new InvalidDataException(paks.Error);
        var final = paks.Final().ToList();
        int encrypted = 0, unreadable = 0;
        var files = final.AsParallel().WithCancellation(ct).Where(f =>
        {
            if (f.Pak.Entries[f.Index].Encryption != 0) { Interlocked.Increment(ref encrypted); return false; }
            return Magic(f.Pak, f.Index) is { } m && ShaderMagics.Contains(m);
        }).ToList();
        var probed = sw.Elapsed.TotalSeconds;

        var locs = new ConcurrentDictionary<string, Loc>();
        var shaders = new ConcurrentDictionary<string, ShaderInfo>();
        var platformOf = new ConcurrentDictionary<string, string>();
        var perFile = new ConcurrentBag<(string Library, List<string> Shas)>();
        long bytes = 0, containers = 0, bad = 0;
        // ponytail: one decompressed material file per worker in memory (PRAGMATA: 5 MB average); cap the parallelism if a
        // title ships material files in the hundreds of MB
        Parallel.ForEach(files, new ParallelOptions { TaskScheduler = TaskScheduler.Current, CancellationToken = ct }, f =>
        {
            byte[] data;
            try { data = f.Pak.Read(f.Pak.Entries[f.Index]); }
            catch (Exception e) when (e is InvalidDataException or IOException or ZstdSharp.ZstdException) { Interlocked.Increment(ref unreadable); return; }
            Interlocked.Add(ref bytes, data.Length);
            var shas = new List<string>();
            foreach (var (off, c) in Dxbc.Containers(data))
            {
                Interlocked.Increment(ref containers);
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                shas.Add(sha);
                if (!locs.TryAdd(sha, new Loc(f.Pak.Path, f.Index, off, c.Length)) || Dxbc.Kind(c) < 0) continue;
                try
                {
                    if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is not { } info) continue;
                    platformOf[sha] = Platform(c);
                    shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                }
                catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { Interlocked.Increment(ref bad); }
            }
            perFile.Add(($"{Path.GetFileName(f.Pak.Path)}|{f.Pak.Entries[f.Index].Hash:x16}", shas));
        });
        located[game.Id] = locs;

        var maps = new List<ShaderMap>();
        foreach (var (library, shas) in perFile.OrderBy(p => p.Library, StringComparer.Ordinal))
            foreach (var pool in shas.Where(shaders.ContainsKey).Distinct().GroupBy(h => platformOf[h]))
                maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{library}|{pool.Key}"), library, pool.Key, pool.ToList()));

        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var p in paks.All)
        {
            var fi = new FileInfo(p.Path);
            content.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(dir, p.Path)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}\n"));
        }
        log?.Report($"{paks.All.Count} packages, {final.Count} files (probed in {probed:F1}s{(encrypted > 0 ? $", {encrypted} resource-encrypted skipped" : "")}): "
            + $"{files.Count} shader files{(unreadable > 0 ? $" ({unreadable} unreadable)" : "")}, {bytes / 1e9:F2} GB decompressed, {containers} containers -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")}, {shaders.Values.Count(s => s.RootSignature != null)} with a root signature, {maps.Count} maps ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), maps.Select(m => m.Platform).Distinct().Order(StringComparer.Ordinal).ToList(),
            new Dictionary<string, ShaderInfo>(shaders), maps);
    }

    /// <summary>Decompresses each shader file once more and slices the requested containers; one whose bytes changed since
    /// (game patched) isn't sent and fails at replay.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        var moduli = Moduli(game);
        foreach (var pak in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Pak))
        {
            using var p = RePak.Open(pak.Key, moduli);
            foreach (var entry in pak.GroupBy(x => x.At.Entry))
            {
                ct.ThrowIfCancellationRequested();
                byte[] data;
                try { data = p.Read(p.Entries[entry.Key]); }
                catch (Exception e) when (e is InvalidDataException or IOException or ZstdSharp.ZstdException or ArgumentOutOfRangeException) { continue; }
                foreach (var (sha, at) in entry)
                    if (at.Offset + at.Size <= data.Length && data.AsSpan(at.Offset, at.Size) is var c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c.ToArray());
            }
        }
    }

    /// <summary>DXIL -> PCD3D_SM6, DXBC -> PCD3D_SM5 (the planner's D3D11 platform). ponytail: no [WaveSize] platform as in
    /// CarvedReader (PRAGMATA has no shader with one); add it when a title ships wave64-only shaders.</summary>
    static string Platform(byte[] c) => Dxbc.Part(c, "DXIL"u8).IsEmpty ? "PCD3D_SM5" : "PCD3D_SM6";

    /// <summary>Every package of the game, in load order: base packages first, then patches by number (a patch's entry
    /// replaces the earlier one with the same name hash).</summary>
    sealed class Paks : IDisposable
    {
        public readonly List<RePak> All = [];
        public readonly RePak? Base;
        public string? Error;
        public string? Failed;   // the package Error is about

        static readonly Regex Patch = new(@"\.patch_(\d+)\.pak$", RegexOptions.IgnoreCase);

        public Paks(string dir, IReadOnlyList<BigInteger> moduli)
        {
            var dlc = Path.Combine(dir, "dlc");
            var paths = Directory.EnumerateFiles(dir, "re_chunk_*.pak")
                .Concat(Directory.Exists(dlc) ? Directory.EnumerateFiles(dlc, "*.pak") : [])
                .OrderBy(p => Patch.Match(p) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
                try { All.Add(RePak.Open(p, moduli)); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // a patch replaces base files: without it the index would be of the unpatched game. A DLC package only adds.
                    if (Path.GetFileName(p).Equals("re_chunk_000.pak", StringComparison.OrdinalIgnoreCase) || Patch.IsMatch(p)) (Error, Failed) = (Error ?? $"{Path.GetFileName(p)}: {e.Message}", Failed ?? p);
                }
            Base = All.FirstOrDefault(p => Path.GetFileName(p.Path).Equals("re_chunk_000.pak", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Each file's last version (empty entries left out), in package then table order.</summary>
        public IEnumerable<(RePak Pak, int Index)> Final()
        {
            var last = new Dictionary<ulong, (RePak, int)>();
            foreach (var p in All)
                for (var i = 0; i < p.Entries.Count; i++) last[p.Entries[i].Hash] = (p, i);
            return last.Values.Where(f => f.Item1.Entries[f.Item2] is var e && (e.Size > 0 || e.Chunked && e.Packed > 0)); // a chunked entry may give its length as Packed (RePak.Read)
        }

        public void Dispose() { foreach (var p in All) p.Dispose(); }
    }

    /// <summary>The file's first 4 bytes, or null when they can't be read.</summary>
    static uint? Magic(RePak pak, int i)
    {
        try { return pak.Read(pak.Entries[i], 4) is { Length: 4 } b ? BinaryPrimitives.ReadUInt32LittleEndian(b) : null; }
        catch (Exception e) when (e is InvalidDataException or IOException or ZstdSharp.ZstdException) { return null; }
    }

    static string Name(uint m) => string.Concat(BitConverter.GetBytes(m).Select(c => c is >= 0x20 and < 0x7F ? ((char)c).ToString() : $"\\x{c:x2}"));

    static readonly Regex Capability = new(@"^\s*Capability\s*=\s*DirectX(1[12])", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>config.ini's [Render] Capability (the game writes it on first run: DirectX12 / DirectX11), else D3D12 when
    /// its shaders include DXIL (SM6 runs on DX12 only), else "D3D11 or D3D12".</summary>
    static string Api(string dir, bool? dxil)
    {
        try
        {
            if (Capability.Match(File.ReadAllText(Path.Combine(dir, "config.ini"))) is { Success: true } m) return "D3D" + m.Groups[1].Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return dxil == true ? "D3D12" : UnrealRhi.Ambiguous;
    }
}
