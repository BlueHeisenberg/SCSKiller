using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.ReEngine;

/// <summary>Capcom RE Engine games (re_chunk_000.pak next to the exe). Every file lives in the game's packages
/// (<see cref="RePak"/>): the base package, its .patch_NNN packages (a later package's entry replaces an earlier one with
/// the same name hash), .sub_NNN packages and dlc\*.pak. Shaders: master materials ("SDF\0", the community's .mmtr) and
/// OpenColorIO files ("OCIO") hold compiled DXBC/DXIL containers back to back after their tables (PRAGMATA: 846 files,
/// 4.30 GB, 94.5% of it containers); a 25-file sample of every other magic holds no valid container. Names are hashes,
/// so files are recognized by their first 4 decompressed bytes. No RTS0: the engine builds its root signatures at run
/// time (<see cref="Planning.RootSig.BuildRe"/>). Maps: a master material's pipelines as it lists them (<see cref="Pipelines"/>),
/// one exact map each of those the runtime creates (<see cref="Creatable"/>); the shaders none of them names, with the listed
/// ones they can pair with, and any file laid out otherwise, pools per file.
/// EngineInfo: Family "RE Engine", Version "PAK 4.2" (the package format this reads), Fork the exe's name (<see cref="Fork"/>).
/// ponytail: shader files are found by magic; a title keeping its shaders under another magic is missed (Detect then
/// reports none found, naming the commonest magics). Add its magic to <see cref="ShaderMagics"/>.</summary>
/// <param name="dataDir">the app's data folder, where a game's table-key modulus is kept (<see cref="ModulusFile"/>)</param>
/// <param name="download">fetches a URL's bytes (null on failure): <see cref="Download.Get"/>, a fake in tests</param>
/// <param name="pinned">the download's expected SHA-256 (tests pass their fake's)</param>
public sealed class ReEngineReader(string dataDir, Func<string, byte[]?>? download = null, string pinned = ReEngineReader.ReePakRsSha256) : IEngineReader
{
    public const string Family = "RE Engine";

    /// <summary>The public RSA modulus the packages' table keys are made with (<see cref="RePak.Key"/>). None is shipped, and
    /// the game doesn't carry it in the clear (PRAGMATA's exe is packed: the modulus isn't in any of its files on disk, in
    /// either byte order): this file, local only (never in plans, logs or anything shared), hex in either byte order (the
    /// one that opens the packages wins; "0x" and separators are ignored). The user gives it (<see cref="SetKey"/>), else it
    /// is downloaded (<see cref="ReePakRs"/>) when an encrypted table needs it, and kept only if it opens the packages.</summary>
    public string ModulusFile(Game game) => Path.Combine(new AppStore(dataDir).GameDir(game.Id), "pak.modulus");

    /// <summary>The community's published modulus: ree-pak-rs's table-key cipher (a Rust byte array, little endian), pinned
    /// to commit 25562e6a (2026-04-02) and to <see cref="ReePakRsSha256"/>, so an upstream edit can't change what is used.</summary>
    public const string ReePakRs = "https://raw.githubusercontent.com/eigeen/ree-pak-rs/25562e6a6f9a52a43b80d54be91e4feb7ac7668b/ree-pak-core/src/pak/cipher/pak.rs";

    /// <summary>SHA-256 of <see cref="ReePakRs"/> as served (1,745 bytes).</summary>
    public const string ReePakRsSha256 = "c4530382c068074dd51fc39e8434944cc6bc40bf8a3963d339fefa9a5d6bf883";

    List<BigInteger> Moduli(Game game)
    {
        try { return Hex(File.ReadAllText(ModulusFile(game))) is { } b ? BothOrders(b) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static byte[]? Hex(string text)
    {
        var hex = string.Concat(text.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Where(Uri.IsHexDigit));
        return hex.Length is 0 || hex.Length % 2 != 0 ? null : Convert.FromHexString(hex);
    }

    static List<BigInteger> BothOrders(byte[] b) =>
        new[] { new BigInteger(b, isUnsigned: true), new BigInteger(b, isUnsigned: true, isBigEndian: true) }.Where(m => m > 1).ToList();

    /// <summary>Every byte array of 64+ bytes in a text ("[0x12, 0x34, ...]", "{ ... }"): candidate moduli, the short
    /// arrays beside them left out.</summary>
    public static List<byte[]> ByteArrays(string text) =>
        Regex.Matches(text, @"[\[{]([^\[\]{}]*)[\]}]").Select(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 64 && t.All(x => Regex.IsMatch(x, "^0x[0-9A-Fa-f]{1,2}$")))
            .Select(t => t.Select(x => Convert.ToByte(x[2..], 16)).ToArray()).ToList();

    /// <summary>The moduli a user's text may hold: all of it as hex, a byte array, or a long run of hex digits. 2048 bytes
    /// at most: a modulus of a whole file's size would take minutes to try.</summary>
    static List<byte[]> Given(string text) =>
        ByteArrays(text).Concat(Regex.Matches(text, "(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}){64,2048}(?![0-9A-Fa-f])").Select(m => Convert.FromHexString(m.Value))).Prepend(Hex(text))
            .OfType<byte[]>().Where(b => b.Length is >= 64 and <= 2048).ToList();

    /// <summary>The first modulus in the user's text that opens the base package and its patches is stored, and only its
    /// value. An encrypted table decrypts to all plausible sizes only with the right one (<see cref="RePak"/>).</summary>
    public (bool Stored, IReadOnlyList<string> Refused) SetKey(Game game, string text)
    {
        if (Find(game) is not { } dir) return (false, ["the game's files aren't found"]);
        using (var plain = new Paks(dir, []))
            if (plain.Error == null) return (false, ["the game's files aren't encrypted"]);
        return Store(game, dir, Given(text)) != null ? (true, []) : (false, [ScsKiller.KeyRefused]);
    }

    // a Detect and an Index of one game may both store it: a torn .tmp would read as a refused key
    static readonly Lock Gate = new();

    /// <summary>The first of <paramref name="candidates"/> that opens the base package and its patches, stored alone; null: none does.</summary>
    byte[]? Store(Game game, string dir, IEnumerable<byte[]> candidates)
    {
        foreach (var m in candidates)
        {
            using var paks = new Paks(dir, BothOrders(m));
            if (paks.Error != null) continue;
            var file = ModulusFile(game);
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", Convert.ToHexString(m));
                File.Move(file + ".tmp", file, overwrite: true);
            }
            return m;
        }
        return null;
    }

    /// <summary>The game's packages with the stored modulus; none stored and a table needing one, with the downloaded one if
    /// it opens them. A failed or unpinned download leaves the key state.</summary>
    Paks Open(Game game, string dir, IProgress<string>? log)
    {
        var paks = new Paks(dir, Moduli(game));
        if (paks.Error?.EndsWith(RePak.NoModulus) != true || Download.Pinned(download, ReePakRs, pinned, log) is not { } got) return paks;
        byte[]? m;
        try { m = Store(game, dir, ByteArrays(Encoding.UTF8.GetString(got))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Report($"{game.Name}: the downloaded key couldn't be stored ({e.GetType().Name}), the game stays keyless"); return paks; }
        if (m == null) { log?.Report($"key download {ReePakRs}: no modulus in it opens {game.Name}'s packages, the game stays keyless"); return paks; }
        paks.Dispose();
        return new Paks(dir, BothOrders(m));
    }

    public static bool NeedsKey(EngineInfo e) => e is { Family: Family, Encrypted: true };

    /// <summary>First 4 bytes (little endian) of the files that hold shader containers: "SDF\0", "OCIO".</summary>
    static readonly uint[] ShaderMagics = [0x00464453, 0x4F49434F];

    public sealed record Loc(string Pak, int Entry, int Offset, int Size);
    const string Dlc = "dlc";

    /// <summary>Where each container was found, per game id, from the last Index (in memory only, like CarvedReader).</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The folder holding re_chunk_000.pak: the exe's, else the install folder.</summary>
    public static string? Find(Game game) =>
        new[] { Path.GetDirectoryName(game.ExePath), game.InstallDir }.FirstOrDefault(d => !string.IsNullOrEmpty(d) && File.Exists(GameFiles.Find(d, "re_chunk_000.pak")));

    /// <summary>The title, so a planning rule can be its own: each RE Engine game builds its root signatures with its own
    /// parameters. Its exe's name in the characters a rule's fork takes ("PRAGMATA", "re8").</summary>
    public static string? Fork(Game game) =>
        string.Concat(Path.GetFileNameWithoutExtension(game.ExePath).Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(64)) is { Length: > 0 } f ? f : null;

    public int ReaderVersion(EngineInfo engine) => 4;

    // the scan cache keeps a refused package until its key changes: a failed download is tried again at a refresh
    public string DetectStamp(Game game, EngineInfo? engine) => KeyFiles.Stamp(ModulusFile(game)) ?? "";

    public EngineInfo? Detect(Game game)
    {
        if (Find(game) is not { } dir) return null;
        using var paks = Open(game, dir, Download.Log);
        var version = paks.Version is { } v ? $"PAK {v.Major}.{v.Minor}" : "PAK ?";
        if (paks.Error?.EndsWith(RePak.NoModulus) == true) return new(Family, version, Fork(game), Api(dir, null), true, null, Note: Download.FailedNote);
        if (paks.Error != null) return new(Family, version, Fork(game), Api(dir, null), false, paks.Error);
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
            if (cs.Count > 0) return new(Family, version, Fork(game), Api(dir, cs.Any(c => !Dxbc.Part(c, "DXIL"u8).IsEmpty)), false, null);
        }
        var top = string.Join(", ", magics.OrderByDescending(p => p.Value).Take(5).Select(p => $"{Name(p.Key)} x{p.Value}"));
        return new(Family, version, Fork(game), Api(dir, null), false, $"no shader containers in its packages' material files (commonest files: {top})");
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var dir = Find(game) ?? throw new DirectoryNotFoundException($"{game.InstallDir}: no re_chunk_000.pak");
        using var paks = Open(game, dir, log);
        if (paks.Error != null) throw new InvalidDataException(paks.Error);
        var final = paks.Final().ToList();
        int encrypted = 0, unreadable = 0;
        var shaderFiles = new ConcurrentBag<(RePak Pak, int Index)>();
        Parallel.ForEach(final, ReaderLoops.Options(ct), f =>
        {
            if (f.Pak.Entries[f.Index].Encryption != 0) Interlocked.Increment(ref encrypted);
            else if (Magic(f.Pak, f.Index) is { } m && ShaderMagics.Contains(m)) shaderFiles.Add(f);
        });
        var files = shaderFiles.ToList();
        var probed = sw.Elapsed.TotalSeconds;

        var locs = new ConcurrentDictionary<string, Loc>();
        var shaders = new ConcurrentDictionary<string, ShaderInfo>();
        var platformOf = new ConcurrentDictionary<string, string>();
        var perFile = new ConcurrentBag<(string Library, List<string> Shas, List<List<string>>? Pipes)>();
        long bytes = 0, containers = 0, bad = 0;
        // ponytail: one decompressed material file per worker in memory (PRAGMATA: 5 MB average); cap the parallelism if a
        // title ships material files in the hundreds of MB
        Parallel.ForEach(files, ReaderLoops.Options(ct), f =>
        {
            using var gate = ReadGate.Enter(ct);
            byte[] data;
            try { data = f.Pak.Read(f.Pak.Entries[f.Index]); }
            catch (Exception e) when (e is InvalidDataException or IOException or ZstdSharp.ZstdException) { Interlocked.Increment(ref unreadable); return; }
            Interlocked.Add(ref bytes, data.Length);
            var shas = new List<string>();
            var at = new Dictionary<long, string>();
            foreach (var (off, c) in Dxbc.Containers(data))
            {
                Interlocked.Increment(ref containers);
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                shas.Add(sha);
                at[off] = sha;
                if (!locs.TryAdd(sha, new Loc(f.Pak.Path, f.Index, off, c.Length)) || Dxbc.Kind(c) < 0) continue;
                try
                {
                    if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is not { } info) continue;
                    platformOf[sha] = Platform(c);
                    shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                }
                catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { Interlocked.Increment(ref bad); }
            }
            perFile.Add(($"{Path.GetFileName(f.Pak.Path)}|{f.Pak.Entries[f.Index].Hash:x16}", shas, Pipelines(data, at.Keys.ToHashSet())?.Select(x => x.Select(o => at[o]).ToList()).ToList()));
        });
        located[game.Id] = locs;

        var maps = new List<ShaderMap>();
        var seen = new HashSet<string>();
        int listed = 0, tables = 0, unlinked = 0;
        foreach (var (library, shas, pipes) in perFile.OrderBy(p => p.Library, StringComparer.Ordinal))
        {
            var named = new HashSet<string>();
            if (pipes != null) tables++;
            foreach (var pipe in pipes ?? [])
            {
                if (!pipe.All(shaders.ContainsKey)) continue;
                if (!Creatable(pipe.Select(h => shaders[h]).ToList())) { unlinked++; continue; }
                named.UnionWith(pipe);
                listed++;
                var h = CarvedReader.Sha1Hex(string.Join(',', pipe.Order(StringComparer.Ordinal)));
                if (seen.Add(h)) maps.Add(new ShaderMap(h, library, platformOf[pipe[0]], pipe, IsPipeline: true));
            }
            // the shaders no entry names pair with each other and with the listed ones of the other side (an unlisted PS with
            // the listed VSs, an unlisted VS with the listed PSs), never listed with listed: those pairs are the entries
            var all = shas.Where(shaders.ContainsKey).Distinct().ToList();
            var rest = all.Where(h => !named.Contains(h)).ToList();
            if (rest.Count == 0) continue;
            bool Source(string h) => shaders[h].Stage is Stage.Vertex or Stage.Mesh;
            var listedSources = all.Where(h => named.Contains(h) && Source(h));
            var pools = new List<(string Kind, IEnumerable<string> Shas)> { ("", rest.Concat(listedSources)) };
            var listedPs = all.Where(h => named.Contains(h) && shaders[h].Stage == Stage.Pixel).ToList();
            if (listedPs.Count > 0 && rest.Any(Source)) pools.Add(("|listed-ps", rest.Where(Source).Concat(listedPs)));
            foreach (var (kind, members) in pools)
                foreach (var pool in members.GroupBy(h => platformOf[h]))
                    maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{library}|{pool.Key}{kind}"), library, pool.Key, pool.ToList()));
        }

        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var dlc = GameFiles.Find(dir, Dlc);
        foreach (var p in paks.All)
        {
            var fi = new FileInfo(p.Path);
            content.AppendData(Encoding.UTF8.GetBytes(ContentStamps.Of(GameFiles.Rel(dir, p.Path, dlc, Dlc), fi.FullName)));
        }
        log?.Report($"{paks.All.Count} packages, {final.Count} files (probed in {probed:F1}s{(encrypted > 0 ? $", {encrypted} resource-encrypted skipped" : "")}): "
            + $"{files.Count} shader files{(unreadable > 0 ? $" ({unreadable} unreadable)" : "")}, {bytes / 1e9:F2} GB decompressed, {containers} containers -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")}, {shaders.Values.Count(s => s.RootSignature != null)} with a root signature; {tables} of {files.Count} files list their pipelines: "
            + $"{listed} -> {maps.Count(m => m.IsPipeline)} distinct{(unlinked > 0 ? $" ({unlinked} more whose stages don't link left out)" : "")}, {maps.Count(m => !m.IsPipeline)} pools ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), maps.Select(m => m.Platform).Distinct().Order(StringComparer.Ordinal).ToList(),
            new Dictionary<string, ShaderInfo>(shaders), maps);
    }

    /// <summary>A master material's pipelines ("SDF\0" version 4, PRAGMATA's, and 5, RE Village's: every recorded pipeline is one
    /// of them): after a header (u32 magic, u16 version, u16 n; 32 bytes in version 4, 24 in 5) come 4n entries (272 bytes in
    /// version 4, 264 in 5), each starting with the file offsets (u64) of its VS, the VS again, HS or MS, DS or AS, GS, PS and
    /// CS containers, 0 for none. Each entry naming any is one pipeline, as container offsets. Null when the file isn't laid
    /// out so: a slot that isn't 0 or a container's offset.</summary>
    internal static List<List<long>>? Pipelines(byte[] data, IReadOnlySet<long> containers)
    {
        if (data.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x00464453) return null;
        var (header, entry) = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) switch { 4 => (32, 272), 5 => (24, 264), _ => (0, 0) };
        if (header == 0) return null;
        var n = 4L * BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        if (header + n * entry > data.Length) return null;
        var pipes = new List<List<long>>();
        for (var i = 0; i < n; i++)
        {
            var e = data.AsSpan(header + i * entry, 7 * 8);
            var pipe = new List<long>();
            for (var slot = 0; slot < 7; slot++)
            {
                var off = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[(8 * slot)..]);
                if (off == 0) continue;
                if (!containers.Contains(off)) return null;
                if (slot != 1) pipe.Add(off);   // the VS once more
            }
            if (pipe.Count > 0) pipes.Add(pipe);
        }
        return pipes;
    }

    /// <summary>A listed entry the runtime creates as a pipeline: one shader per stage, each stage feeding the next
    /// (<see cref="Planning.Planner.Links"/>, with each attribute's component type; DXBC in the same registers, a DXIL PS's
    /// inputs in the rows the stage feeding it writes them in: <see cref="Planning.Planner.PsRowsLink"/>) and an
    /// amplification shader sending the payload its mesh shader takes. PRAGMATA's tables also list entries that aren't
    /// (E_INVALIDARG on creation): a VS writing only SV_Position with a PS reading attributes, its 128-byte AS with mesh
    /// shaders that take none. A mesh shader feeds its PS by semantic and component type alone: the PS packs per-primitive
    /// inputs into other columns than the MS writes them (<see cref="Planning.Planner.MeshFeeds"/> would refuse pipelines
    /// the runtime creates).</summary>
    internal static bool Creatable(IReadOnlyList<ShaderInfo> stages)
    {
        if (stages.DistinctBy(s => s.Stage).Count() < stages.Count) return false;
        var by = stages.ToDictionary(s => s.Stage);
        if (by.TryGetValue(Stage.Mesh, out var m))
            return !by.ContainsKey(Stage.Vertex) && (!by.TryGetValue(Stage.Amplification, out var a) || Planning.Planner.Launches(a, m)) && (!by.TryGetValue(Stage.Pixel, out var mps) || Written(m, mps, false));
        ShaderInfo? prev = null;
        foreach (var s in new[] { Stage.Vertex, Stage.Hull, Stage.Domain, Stage.Geometry, Stage.Pixel }.Where(by.ContainsKey).Select(x => by[x]))
        {
            if (prev != null && (!Planning.Planner.Links(prev, s) || !Written(prev, s, s.ShaderModel.Split('_') is [_, "4" or "5", ..]))) return false;
            prev = s;
        }
        return Planning.Planner.PsRowsLink(by);

        // each attribute the stage reads is written before it with its component type, and for DXBC (which links by
        // register) where it reads it
        static bool Written(ShaderInfo src, ShaderInfo dst, bool sameRegister)
        {
            var outs = src.Outputs.GroupBy(o => (o.Semantic.ToUpperInvariant(), o.Index)).ToDictionary(g => g.Key, g => g.First());
            return dst.Inputs.All(i => !outs.TryGetValue((i.Semantic.ToUpperInvariant(), i.Index), out var o) ? i.SysValue != 0
                : (i.SysValue != 0 || o.CompType == i.CompType) && (!sameRegister || o.Register == i.Register));
        }
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
        public readonly (int Major, int Minor)? Version;
        public string? Error;

        static readonly Regex Patch = new(@"\.patch_(\d+)\.pak$", RegexOptions.IgnoreCase);

        public Paks(string dir, IReadOnlyList<BigInteger> moduli)
        {
            var dlc = GameFiles.Find(dir, Dlc);
            var paths = GameFiles.EnumerateFiles(dir, "re_chunk_*.pak")
                .Concat(Directory.Exists(dlc) ? GameFiles.EnumerateFiles(dlc, "*.pak") : [])
                .OrderBy(p => Patch.Match(p) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
                try
                {
                    if (Patch.IsMatch(p) && new FileInfo(p).Length == 0) continue;   // replaces nothing: a mod manager can leave one
                    All.Add(RePak.Open(p, moduli));
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // a patch replaces base files: without it the index would be of the unpatched game. A DLC package only adds.
                    if (Path.GetFileName(p).Equals("re_chunk_000.pak", StringComparison.OrdinalIgnoreCase) || Patch.IsMatch(p)) Error ??= $"{Path.GetFileName(p)}: {e.Message}";
                }
            Base = All.FirstOrDefault(p => Path.GetFileName(p.Path).Equals("re_chunk_000.pak", StringComparison.OrdinalIgnoreCase));
            Version = Base is { } b ? (b.Major, b.Minor) : RePak.HeaderVersion(GameFiles.Find(dir, "re_chunk_000.pak"));
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

    /// <summary>config.ini's [Render] Capability (the game writes it on first run: DirectX12 / DirectX11; older games name it
    /// &lt;game&gt;_config.ini, as res_config.ini), else D3D12 when its shaders include DXIL (SM6 runs on DX12 only), else "D3D11 or D3D12".</summary>
    static string Api(string dir, bool? dxil)
    {
        try
        {
            var ini = GameFiles.Find(dir, "config.ini");
            if (!File.Exists(ini)) ini = GameFiles.EnumerateFiles(dir, "*_config.ini").FirstOrDefault(f => f.EndsWith("_config.ini", StringComparison.OrdinalIgnoreCase));
            if (ini != null && Capability.Match(File.ReadAllText(ini)) is { Success: true } m) return "D3D" + m.Groups[1].Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return dxil == true ? "D3D12" : UnrealRhi.Ambiguous;
    }
}
