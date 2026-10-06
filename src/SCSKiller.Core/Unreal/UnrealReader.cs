using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.UE4.Objects.Core.Misc;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Unreal;

/// <summary>Cooked UE 4.2x/5.x games: every shader in the shader code libraries (ShaderArchive-*.ushaderbytecode), via
/// CUE4Parse, or, for games without libraries (bShareMaterialShaderCode=False), carved out of the packages that own them
/// (<see cref="InlineShaders"/>). Engine version, fork EGame and paks dir are auto-detected; encrypted containers are
/// opened only with the game's AES key.
/// Native codecs: oodle-data-shared.dll and zlib-ng2.dll, hash-pinned, from %LOCALAPPDATA%\SCSKiller\codecs (<see cref="Codecs"/>).
/// Never the game folder.</summary>
public sealed partial class UnrealReader(string? dataDir = null) : IEngineReader
{
    /// <summary>The app's data (default %LOCALAPPDATA%\SCSKiller): AES keys of encrypted games and where inline shaders sit,
    /// per game.</summary>
    readonly AppStore store = new(dataDir ?? AppStore.DefaultDir);
    readonly UnrealKeys keys = new(dataDir ?? AppStore.DefaultDir);

    /// <summary>Cheap (the app runs it for every installed game at startup): no full mount; .utoc headers and name scans,
    /// one .pak index at a time (for the libraries of pak-era games and the config). GraphicsApi: see <see cref="UnrealRhi"/>.
    /// A game without readable shader libraries that has encrypted containers gets its AES key from <see cref="UnrealKeys"/>
    /// (a one-time static scan of its exe) and is then indexable. Without libraries the shaders are inside the packages;
    /// the platforms then come from the global shader caches (GlobalShaderCache-&lt;platform&gt;.bin).</summary>
    public EngineInfo? Detect(Game game) => Detect(game, out _);

    /// <param name="notes">the evidence the graphics API was decided on, and for an encrypted game how its key was found or not
    /// (never the key)</param>
    public EngineInfo? Detect(Game game, out string notes)
    {
        notes = "";
        if (Locate(game) is not { } where) return null;
        var (paks, baseGame, fork, project) = where;
        projects[game.InstallDir] = project;
        var eg = fork ?? baseGame;
        var s = Survey(paks, eg, project, null);
        var keyNote = "";
        FAesKey? key = null;
        if (s.Libraries.Count == 0 && s.Encrypted.Count > 0 && (key = keys.Get(game, k => Opens(s.Encrypted[0], eg, k), out keyNote)) != null)
            s = Survey(paks, eg, project, key);
        if (baseGame == EGame.GAME_UE5_5 && fork == null && HasVerseCells(paks, key)) baseGame = EGame.GAME_UE5_6;
        ReleaseMemory();
        var encrypted = s.Libraries.Count == 0 && s.Encrypted.Count > 0;
        var platforms = s.Libraries.Count > 0 ? s.Libraries : s.Globals; // no libraries: the shaders are inside the packages
        var unsupported = encrypted ? "encrypted game files (needs the game's AES key)"
            : platforms.Count == 0 ? "no shaders found (no shader library, no global shader cache)"
            : !platforms.Any(p => p.StartsWith("PCD3D_")) ? $"no D3D shaders ({string.Join(", ", platforms)})"
            : null;
        var version = VersionOf(baseGame);
        var menu = UnrealRhi.LaunchMenu(game);
        var (userDir, launch) = (UnrealRhi.UserDir(game, project), UnrealRhi.LaunchOptions(game));
        var (api, why) = UnrealRhi.Resolve(version.StartsWith('4') ? 4 : 5, platforms, s.Configs, project, userDir, launch, menu.Entries, menu.Default, fork?.ToString());
        notes = keyNote.Length > 0 ? $"{why}; AES key: {keyNote}" : why;
        var rtOff = UnrealRhi.RayTracingOff(s.Configs, project, userDir, launch);
        return new EngineInfo("Unreal", version, fork?.ToString(), api, encrypted, unsupported, rtOff || UnrealRhi.RtPipelinesOff(s.Configs, project), rtOff);
    }

    /// <summary>The user supplies the AES key of an encrypted game (hex, "0x" optional): stored for the game if it opens its
    /// encrypted files. False: not a 256-bit hex key, or it opens nothing.</summary>
    public bool SetKey(Game game, string key)
    {
        if (Locate(game) is not { } where) return false;
        var (paks, baseGame, fork, project) = where;
        var s = Survey(paks, fork ?? baseGame, project, null);
        return s.Encrypted.Count > 0 && keys.Set(game, key, k => Opens(s.Encrypted[0], fork ?? baseGame, k));
    }

    /// <summary>Paks dir, engine version, fork, project folder; null = not a cooked Unreal game.</summary>
    public string DetectStamp(Game game, EngineInfo? engine)
    {
        if (!projects.TryGetValue(game.InstallDir, out var project) && PaksDir(game.InstallDir) is { } paks)
            projects[game.InstallDir] = project = ProjectOf(paks);   // a miss isn't kept: the install may be mid-update
        return project == null ? "" : UnrealRhi.Stamp(game, project) + "|" + keys.KeyStamp(game);
    }

    readonly ConcurrentDictionary<string, string> projects = new(StringComparer.OrdinalIgnoreCase);

    static string ProjectOf(string paks) => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(paks)))!; // <Project>\Content\Paks

    static (string Paks, EGame Base, EGame? Fork, string Project)? Locate(Game game)
    {
        if (PaksDir(game.InstallDir) is not { } paks) return null;
        var baseGame = DetectEngine(game, paks, out var downTo, out var upTo, out var fromContainers);
        var fork = DetectFork(baseGame, upTo, Path.GetFileName(game.InstallDir.TrimEnd('\\', '/')), Path.GetFileNameWithoutExtension(game.ExePath), fromContainers, game.Name, downTo);
        return (paks, fork is { } f ? (EGame)((uint)f & 0xFFFF0000) : baseGame, fork, ProjectOf(paks));
    }

    /// <summary>Whether <paramref name="key"/> decrypts the index of the encrypted container at <paramref name="path"/>.</summary>
    static bool Opens(string path, EGame game, FAesKey key)
    {
        using var r = OpenContainer(path, new VersionContainer(game));
        return r.TestAesKey(key);
    }

    static AbstractAesVfsReader OpenContainer(string path, VersionContainer versions) => path.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)
        ? new IoStoreReader(path, EIoStoreTocReadOptions.ReadDirectoryIndex, versions) : new PakFileReader(path, versions);

    /// <summary>Shader library and global shader cache platforms, the containers we can't read (encrypted), and the RHI
    /// config files: .utoc by header and name scan, .pak one index at a time (a full mount of a big game is ~1 GB; this peaks
    /// at its largest pak index). With <paramref name="key"/>, encrypted containers it opens are read too (an encrypted .utoc
    /// through CUE4Parse).</summary>
    static (List<string> Libraries, List<string> Globals, List<string> Encrypted, Dictionary<string, string> Configs) Survey(string paks, EGame game, string project, FAesKey? key)
    {
        InitCodecs(); // config files may be compressed
        var versions = new VersionContainer(game);
        var platforms = new List<string>();
        var globals = new List<string>();
        var encrypted = new List<string>();
        var configs = new Dictionary<string, (long Order, string Text)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(paks).Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)))
        {
            var toc = path.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase);
            if (toc) // IoStore: packages and shader libraries, never config
            {
                var flags = TocHeader(path)[80]; // FIoStoreTocHeader.ContainerFlags: Encrypted 2, Indexed 8 (has a directory index)
                if ((flags & 8) == 0) continue; // e.g. global.utoc
                if ((flags & 2) == 0) { TocShaderFiles(path, platforms, globals); continue; }
                if (key == null) { encrypted.Add(path); continue; }
            }
            AbstractAesVfsReader r;
            try { r = OpenContainer(path, versions); }
            catch (Exception) { continue; } // a container format CUE4Parse doesn't know: the full mount skips it too
            var files = 0;
            using (r)
            {
                if (r.IsEncrypted)
                {
                    if (key == null || !r.TestAesKey(key)) { encrypted.Add(path); continue; }
                    r.AesKey = key;
                }
                try { r.Mount(StringComparer.OrdinalIgnoreCase); }
                catch (Exception) { continue; } // an index CUE4Parse can't read
                files = r.FileCount;
                foreach (var (name, file) in r.Files)
                    if ((file.Extension == "ushaderbytecode" || IsGlobalCache(file)) && file.IsEncrypted && r.AesKey == null)
                        encrypted.Add(path); // readable index, encrypted entries (Sea of Thieves)
                    else if (file.Extension == "ushaderbytecode") platforms.Add(file.NameWithoutExtension.Split('-')[^1]);
                    else if (IsGlobalCache(file)) globals.Add(file.NameWithoutExtension["GlobalShaderCache-".Length..]);
                    else if (file.Extension == "ini" && UnrealRhi.IsConfig(name, project) && (!configs.TryGetValue(name, out var c) || c.Order < r.ReadOrder))
                        try { configs[name] = (r.ReadOrder, Encoding.UTF8.GetString(file.Read())); } // the highest-priority (patch) pak's copy wins
                        catch (Exception) { } // an encrypted entry in an unencrypted index
            }
            if (files > 1000) GC.Collect(); // drop that file list before the next one (Tiny Tina's: 259 -> 97 MB peak)
        }
        return (platforms.Distinct().Order(StringComparer.Ordinal).ToList(), globals.Distinct().Order(StringComparer.Ordinal).ToList(), encrypted.Distinct().Order(StringComparer.Ordinal).ToList(),
            configs.ToDictionary(c => c.Key, c => c.Value.Text, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Unreal 5.6 ships compute permutations with a [WaveSize] of 16 or 64 lanes. One that excludes 32 lanes goes to
    /// its own platform ("PCD3D_SM6 wave64"), which the planner picks only on AMD when its range takes 64: NVIDIA runs 32 only
    /// and the runtime rejects them (E_INVALIDARG). ponytail: the same lane split as CarvedReader.LanePlatform.</summary>
    static string? LaneSuffix((uint Min, uint Max)? lanes) =>
        lanes is { } l && (l.Min > 32 || l.Max < 32) ? $" wave{l.Min}{(l.Max != l.Min ? $"-{l.Max}" : "")}" : null;

    /// <summary>The app goes idle after a scan or a compile: hand the transient pak indexes and shader bytes (up to ~1 GB for
    /// a big game's full mount) back to the OS now rather than at some later gen2 collection.</summary>
    static void ReleaseMemory() => GC.Collect(2, GCCollectionMode.Aggressive, true, true);

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        try { return IndexCore(game, engine, log, ct); }
        finally { ReleaseMemory(); }
    }

    ShaderIndex IndexCore(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        using var provider = Mount(PaksDir(game.InstallDir) ?? throw new DirectoryNotFoundException($"no Content/Paks under {game.InstallDir}"), GameOf(engine), keys.Stored(game));
        var ue5 = engine.Version.StartsWith('5') || engine.Version.StartsWith('6');
        var ue58 = Version.TryParse(engine.Version, out var v) && v >= new Version(5, 8); // RootSig.Rule.Ue58
        if (!Libraries(provider).Any()) return IndexInline(provider, game, log, ct, ue5, ue58);
        var shaders = new ConcurrentDictionary<string, ShaderInfo>();
        var wide = new ShaderContainer.WideCounts();
        var maps = new List<ShaderMap>();
        var platforms = new List<string>();
        var byKey = new Dictionary<string, (string Sha, string Platform)>(); // version-1 archives: archive hash -> shader
        var lanes = new ConcurrentDictionary<string, string>(); // shader -> its [WaveSize] platform suffix
        var byHash = new Dictionary<string, (string Sha, string Platform)>(); // library hash -> shader
        var undecodable = 0;
        string? why = null;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var lib in Libraries(provider))
        {
            var sw = Stopwatch.StartNew();
            var arc = Open(provider, lib.File);
            if (arc == null) { log?.Report($"{lib.File.Path}: unsupported shader archive layout, skipped"); continue; }
            var sha = new string[arc.Count];
            var (bad, failed) = (0, 0);
            Parallel.ForEach(arc.Codes, new ParallelOptions { CancellationToken = ct }, work =>
            {
                IEnumerable<(int, byte[])> codes;
                try { codes = work(); }
                catch (InvalidDataException e) { Interlocked.Increment(ref failed); Interlocked.CompareExchange(ref why, e.Message, null); return; }
                foreach (var (i, code) in codes)
                {
                    var (h, container) = Hash(code);
                    sha[i] = h;
                    if (shaders.ContainsKey(h)) continue;
                    try
                    {
                        if (ShaderContainer.Parse(container.Span, h, ShaderContainer.UeCounts(code, ue5, ue58)) is { } info && shaders.TryAdd(h, info) && !ue5) wide.See(code, info);
                        if (LaneSuffix(Dxbc.WaveLanes(container.Span)) is { } l) lanes[h] = l;
                    }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { Interlocked.Increment(ref bad); } // malformed container: not usable anyway
                }
            });
            undecodable += failed;
            if (!platforms.Contains(lib.Platform)) platforms.Add(lib.Platform);
            content.AppendData(Encoding.UTF8.GetBytes($"{lib.Name}/{lib.Platform}\n"));
            if (arc.Keys is { } keys)
                for (var i = 0; i < keys.Length; i++)
                    if (sha[i] != null) byKey[keys[i]] = (sha[i], lib.Platform);
            if (arc.Hashes is { } hashes)
                for (var i = 0; i < Math.Min(hashes.Length, sha.Length); i++)
                    if (sha[i] != null && shaders.ContainsKey(sha[i])) byHash[hashes[i]] = (sha[i], lib.Platform);
            for (var m = 0; m < arc.MapHashes.Length; m++)
            {
                var list = Enumerable.Range(arc.Maps[m].Off, arc.Maps[m].Num).Select(k => sha[arc.Indices[k]]).Where(h => h != null).ToList();
                bool Wave(string? h) => h != null && lanes.ContainsKey(h);
                maps.Add(new ShaderMap(arc.MapHashes[m], lib.Name, lib.Platform, list.Where(h => !Wave(h)).ToList()));
                foreach (var g in list.Where(Wave).GroupBy(h => lib.Platform + lanes[h!]))
                {
                    maps.Add(new ShaderMap(arc.MapHashes[m], lib.Name, g.Key, g.ToList()));
                    if (!platforms.Contains(g.Key)) platforms.Add(g.Key);
                }
                content.AppendData(Encoding.UTF8.GetBytes($"{arc.MapHashes[m]}:{string.Join(',', list)}\n"));
            }
            log?.Report($"{lib.File.Name}: {(arc.Keys != null ? "version 1 (no maps)" : $"{arc.MapHashes.Length} shader maps")}, {arc.Count} shaders{(bad > 0 ? $", {bad} unparseable" : "")}{(failed > 0 ? $", {failed} code blocks that don't decompress, skipped" : "")} ({sw.Elapsed.TotalSeconds:F1}s)");
        }
        if (shaders.IsEmpty && undecodable > 0) throw new InvalidDataException($"none of the game's shader code decompresses ({undecodable} blocks): {why}");
        if (byKey.Count > 0) // version-1 archives: maps from the packages that reference their shaders
        {
            var sw = Stopwatch.StartNew();
            var found = ReferencedMaps(provider, byKey, ct, out var scanned);
            foreach (var m in found) content.AppendData(Encoding.UTF8.GetBytes($"{m.Hash}:{string.Join(',', m.Shaders)}\n"));
            maps.AddRange(found);
            log?.Report($"shader maps from package references: {found.Count} of {scanned} scanned packages, {found.SelectMany(m => m.Shaders).Distinct().Count()} of {byKey.Values.Select(v => v.Sha).Distinct().Count()} archive shaders referenced ({sw.Elapsed.TotalSeconds:F1}s)");
        }
        if (wide.Apply(shaders)) log?.Report("resource counts: 10-byte layout with a 16-bit SRV count (the shaders' bindings agree)");
        maps.AddRange(ShippedPipelines(provider, byHash, log));
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), platforms, shaders, maps);
    }

    /// <summary>The graphics PSOs of the game's shipped pipeline caches (<see cref="StablePipelineCache"/>), one exact map each,
    /// so the planner pairs their shaders as the game does. With r.ShaderPipelineCache.ExcludePrecachePSO they are what PSO
    /// precaching doesn't create: global and post-process passes. Not in the content hash: they change no shader, and the
    /// community database finds a build by that hash.</summary>
    static List<ShaderMap> ShippedPipelines(AbstractFileProvider provider, Dictionary<string, (string Sha, string Platform)> byHash, IProgress<string>? log)
    {
        var maps = new List<ShaderMap>();
        foreach (var f in provider.Files.Values.Where(f => f.Path.EndsWith(".stable.upipelinecache", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            List<StablePipelineCache.Pso>? psos;
            try { psos = StablePipelineCache.Read(f.Read()); }
            catch (Exception) { psos = null; } // an entry CUE4Parse can't read
            if (psos == null) { log?.Report($"{f.Name}: not a pipeline cache this reads, skipped"); continue; }
            var graphics = psos.Where(p => p.Type == StablePipelineCache.PsoType.Graphics).ToList();
            var known = graphics.Where(p => p.Shaders.Length > 0 && p.Shaders.All(byHash.ContainsKey)).ToList();
            maps.AddRange(known.Select(p => new ShaderMap($"{f.NameWithoutExtension}:{p.Key:x8}", "PipelineCache", byHash[p.Shaders[0]].Platform,
                p.Shaders.Select(h => byHash[h].Sha).ToList(), IsPipeline: true)));
            log?.Report($"{f.Name}: {psos.Count} PSOs, {graphics.Count} graphics{(known.Count < graphics.Count ? $" ({graphics.Count - known.Count} name a shader no library has)" : "")}");
        }
        return maps;
    }

    /// <summary>Calls <paramref name="sink"/> once per found shader, one call at a time.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        try { ReadShadersCore(game, engine, sha1s, sink, ct); }
        finally { ReleaseMemory(); }
    }

    void ReadShadersCore(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        using var provider = Mount(PaksDir(game.InstallDir) ?? throw new DirectoryNotFoundException($"no Content/Paks under {game.InstallDir}"), GameOf(engine), keys.Stored(game));
        if (!Libraries(provider).Any()) { ReadInline(provider, game, sha1s, sink, ct); return; }
        var left = new ConcurrentDictionary<string, bool>(sha1s.Select(s => KeyValuePair.Create(s, true)));
        foreach (var lib in Libraries(provider))
        {
            if (left.IsEmpty) break;
            if (Open(provider, lib.File) is not { } arc) continue;
            Parallel.ForEach(arc.Codes, new ParallelOptions { CancellationToken = ct }, work =>
            {
                IEnumerable<(int, byte[])> codes;
                try { codes = work(); }
                catch (InvalidDataException) { return; } // skipped at indexing too
                foreach (var (_, code) in codes)
                {
                    var (h, container) = Hash(code);
                    if (left.TryRemove(h, out _))
                        lock (left) sink(h, container.ToArray());
                }
            });
        }
    }

    /// <summary>Class-name suffixes of the objects that carry inline shader maps: Material, MaterialInstanceConstant (and
    /// LandscapeMaterialInstanceConstant), NiagaraScript, ComputeGraph (deformer graphs: OptimusComputeGraph).</summary>
    static readonly string[] ShaderMapOwners = ["Material", "MaterialInstanceConstant", "NiagaraScript", "ComputeGraph"];

    /// <summary>Carves every package that exports a <see cref="ShaderMapOwners"/> object (from its header alone: needs no
    /// mappings; a header that doesn't parse is carved whole) and the global shader caches (library "Global"), see
    /// <see cref="InlineShaders"/>. One map per inline shader map (4.2x code, which has no map boundary: one per package);
    /// platform: the game's one D3D global shader cache platform, else by content (DXIL vertex/pixel/... shaders: PCD3D_SM6,
    /// else PCD3D_SM5). ContentHash: every carved package's
    /// path and location (container, offset, size) in the paks, so a patch that touches one changes it. Where each shader
    /// was found goes to games\&lt;id&gt;\inline.idx for <see cref="ReadShaders"/>.</summary>
    internal ShaderIndex IndexInline(DefaultFileProvider provider, Game game, IProgress<string>? log, CancellationToken ct, bool ue5 = false, bool ue58 = false)
    {
        var sw = Stopwatch.StartNew();
        provider.MappingsContainer = new NoMappings(); // headers of unversioned packages parse without property types
        var files = provider.Files.Values.Where(f => f.IsUePackage || IsGlobalCache(f)).ToList();
        var shaders = new ConcurrentDictionary<string, ShaderInfo>();
        var wide = new ShaderContainer.WideCounts();
        var maps = new ConcurrentBag<ShaderMap>();
        var where = new ConcurrentDictionary<string, string>(); // sha1 -> "<format> <offset> <path>"
        var stamps = new ConcurrentBag<string>();
        long read = 0;
        int carved = 0, unparsed = 0, unreadable = 0, undecoded = 0, bad = 0;
        var budget = ByteBudget.FromFreeMemory();
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
        {
            var global = IsGlobalCache(f);
            var owns = global ? true : OwnsShaderMaps(provider, f);
            if (owns == false) return;
            if (owns == null) Interlocked.Increment(ref unparsed); // a header that doesn't parse: carve it whole
            using var held = budget.Take(PackageSize(provider, f));
            byte[][] parts;
            string stamp;
            try { parts = PackageParts(provider, f, out stamp); }
            catch (Exception) { Interlocked.Increment(ref unreadable); return; } // e.g. an encrypted entry
            Interlocked.Add(ref read, parts.Sum(p => (long)p.Length));
            Interlocked.Increment(ref carved);
            stamps.Add(stamp);
            var lists = new Dictionary<int, List<string>>();
            Interlocked.Add(ref undecoded, InlineShaders.Carve(parts, e =>
            {
                var (h, container) = Hash(e.Code);
                if (!lists.TryGetValue(e.Map, out var list)) lists[e.Map] = list = [];
                list.Add(h);
                where.TryAdd(h, $"{e.Format} {e.Offset} {f.Path}");
                if (shaders.ContainsKey(h)) return;
                try { if (ShaderContainer.Parse(container.Span, h, ShaderContainer.UeCounts(e.Code, ue5, ue58)) is { } info && shaders.TryAdd(h, info) && !ue5) wide.See(e.Code, info); }
                catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { Interlocked.Increment(ref bad); } // malformed container: not usable anyway
            }));
            foreach (var (m, list) in lists) maps.Add(new ShaderMap($"{f.Path}#{m}", global ? "Global" : f.Path, "", list.Distinct().ToList()));
        });
        wide.Apply(shaders);
        var d3d = files.Where(IsGlobalCache).Select(f => f.NameWithoutExtension["GlobalShaderCache-".Length..]).Where(p => p.StartsWith("PCD3D_")).Distinct().ToList();
        var labeled = Canonical(maps.Select(m => m with
        {
            Platform = d3d.Count == 1 ? d3d[0] // the one D3D platform the game ships
                : m.Shaders.Any(h => shaders.TryGetValue(h, out var s) && s.Stage != Stage.Library && s.ShaderModel.Split('_') is [_, "6", ..]) ? "PCD3D_SM6" : "PCD3D_SM5",
        }));

        var idx = Path.Combine(store.GameDir(game.Id), "inline.idx");
        Directory.CreateDirectory(Path.GetDirectoryName(idx)!);
        using (var z = new StreamWriter(new GZipStream(File.Create(idx), CompressionLevel.Fastest)))
            foreach (var (h, at) in where) z.WriteLine($"{h} {at}");
        var content = SHA1.HashData(Encoding.UTF8.GetBytes(string.Join('\n', stamps.Order(StringComparer.Ordinal))));
        var stages = shaders.Values.GroupBy(s => s.Stage).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
        log?.Report($"shaders inside packages: carved {carved} of {files.Count} packages ({unparsed} by an unparsed header, {unreadable} unreadable; {read >> 20} MB), {labeled.Count} maps, {shaders.Count} shaders ({string.Join(", ", stages)}), "
            + $"{shaders.Values.Count(s => s.Counts == new ResourceCounts(0, 0, 0, 0))} without resource counts{(undecoded + bad > 0 ? $", {undecoded} undecoded, {bad} unparseable" : "")} ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content), labeled.Select(m => m.Platform).Distinct().Order(StringComparer.Ordinal).ToList(), shaders, labeled);
    }

    /// <summary>Re-reads each package that holds a requested shader (inline.idx, else a fresh index) and decodes just those
    /// entries; a shader whose bytes no longer hash the same (game patched since) is skipped: it fails at replay.</summary>
    internal void ReadInline(DefaultFileProvider provider, Game game, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        var idx = Path.Combine(store.GameDir(game.Id), "inline.idx");
        if (!File.Exists(idx)) IndexInline(provider, game, null, ct);
        var at = new Dictionary<string, (char Format, int Offset, string Path)>();
        using (var r = new StreamReader(new GZipStream(File.OpenRead(idx), CompressionMode.Decompress)))
            for (var line = r.ReadLine(); line != null; line = r.ReadLine())
                if (line.Split(' ', 4) is [var h, var fmt, var off, var path] && sha1s.Contains(h)) at[h] = (fmt[0], int.Parse(off), path);
        var budget = ByteBudget.FromFreeMemory();
        Parallel.ForEach(at.GroupBy(a => a.Value.Path), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, g =>
        {
            if (!provider.Files.TryGetValue(g.Key, out var f)) return; // gone since indexing: those items fail at replay
            using var held = budget.Take(PackageSize(provider, f));
            byte[][] parts;
            try { parts = PackageParts(provider, f, out _); }
            catch (Exception) { return; } // unreadable since indexing: those items fail at replay
            foreach (var (h, (fmt, off, _)) in g)
            {
                var (part, local) = parts.Length == 2 && off >= parts[0].Length ? (parts[1], off - parts[0].Length) : (parts[0], off);
                if (InlineShaders.Decode(part, local, fmt) is { } code && Hash(code) is var (hh, container) && hh == h)
                    lock (at) sink(h, container.ToArray());
            }
        });
    }

    /// <summary>Whether a package exports an object of a <see cref="ShaderMapOwners"/> class, from its header (the first
    /// 64 KB, below the large-object heap, else all of it); null if the header doesn't parse.</summary>
    static bool? OwnsShaderMaps(IVfsFileProvider provider, GameFile f)
    {
        for (var size = Math.Min(f.Size, 64 << 10); ; size = f.Size)
            try
            {
                var ar = size == f.Size ? f.CreateReader() : f.CreateReader(new FByteBulkDataHeader(default, 0, (uint)size, 0, default));
                IEnumerable<string> classes;
                if (f is FIoStoreEntry io)
                {
                    var p = new IoPackage(ar, io.IoStoreReader.ContainerHeader, (Func<FByteBulkDataHeader?, FArchive?>?)null, null, provider);
                    classes = p.ExportMap.Where(e => e.ClassIndex.IsScriptImport || e.ClassIndex.IsExport).Select(e => p.ResolveObjectIndex(e.ClassIndex)?.Name.Text ?? "");
                }
                else
                {
                    var p = new Package(ar, null, (Func<FByteBulkDataHeader?, FArchive?>?)null, null, provider);
                    classes = p.ExportMap.Select(e => e.ClassIndex.IsImport ? p.ImportMap[-e.ClassIndex.Index - 1].ObjectName.Text
                        : e.ClassIndex.IsExport ? p.ExportMap[e.ClassIndex.Index - 1].ObjectName.Text : "");
                }
                return classes.Any(c => ShaderMapOwners.Any(o => c.EndsWith(o, StringComparison.Ordinal)));
            }
            catch (Exception) when (size < f.Size) { } // a header larger than the head read: read it all
            catch (Exception) { return null; }
    }

    /// <summary>A package's files (.uasset, then .uexp when split; not concatenated: a copy would double the largest
    /// allocation) and where they sit in the paks ("path|container:offset:size" per file).</summary>
    static byte[][] PackageParts(DefaultFileProvider provider, GameFile f, out string stamp)
    {
        provider.Files.FindPayloads(f, out var uexp, out _, out _);
        static string At(GameFile g) => g is VfsEntry v ? $"{g.Path}|{v.Vfs.Name}:{v.Offset}:{g.Size}" : $"{g.Path}|{g.Size}";
        stamp = uexp == null ? At(f) : $"{At(f)}|{At(uexp)}";
        return uexp == null ? [f.Read()] : [f.Read(), uexp.Read()];
    }

    static long PackageSize(DefaultFileProvider provider, GameFile f)
    {
        provider.Files.FindPayloads(f, out var uexp, out _, out _);
        return f.Size + (uexp?.Size ?? 0);
    }

    /// <summary>Bounds the package bytes held at once, not the thread count: a material package can be hundreds of MB, and
    /// one per core fills the memory. A package larger than the budget runs alone.</summary>
    internal sealed class ByteBudget(long limit)
    {
        long used;

        /// <summary>A quarter of the memory free now, 256 MB to 2 GB.</summary>
        public static ByteBudget FromFreeMemory()
        {
            var m = GC.GetGCMemoryInfo();
            return new(Math.Clamp((m.TotalAvailableMemoryBytes - m.MemoryLoadBytes) / 4, 256L << 20, 2L << 30));
        }

        public Held Take(long bytes)
        {
            lock (this)
            {
                while (used > 0 && used + bytes > limit) Monitor.Wait(this);
                used += bytes;
            }
            return new(this, bytes);
        }

        public readonly struct Held(ByteBudget budget, long bytes) : IDisposable
        {
            public void Dispose()
            {
                lock (budget)
                {
                    budget.used -= bytes;
                    Monitor.PulseAll(budget);
                }
            }
        }
    }

    /// <summary>Empty type mappings: package headers parse, property data is never read.</summary>
    sealed class NoMappings : ITypeMappingsProvider
    {
        public TypeMappings? MappingsForGame { get; } = new([], []);
        public void Load(string path, StringComparer? comparer = null) { }
        public void Load(byte[] bytes, StringComparer? comparer = null) { }
        public void Reload() { }
    }

    sealed record Library(GameFile File, string Name, string Platform);

    /// <summary>One opened library: shader map hashes, map -> (offset, count) into Indices, and per-shader code producers
    /// (IoStore groups decompress several shaders at once).</summary>
    internal sealed record Archive(string[] MapHashes, (int Off, int Num)[] Maps, uint[] Indices, int Count, Func<IEnumerable<(int, byte[])>>[] Codes,
        string[]? Keys = null, // version 1: each shader's archive hash (packages reference it); no maps
        string[]? Hashes = null); // each shader's library hash (FSHAHash), which pipeline caches name it by

    static IEnumerable<Library> Libraries(AbstractFileProvider provider) =>
        provider.Files.Values.Where(f => f.Extension == "ushaderbytecode").OrderBy(f => f.Path, StringComparer.Ordinal).Select(f =>
        {
            // ShaderArchive-<Library>-<Format>[-<Format>]: library = middle, platform = last segment
            var parts = f.NameWithoutExtension.Split('-');
            var platform = parts[^1];
            return new Library(f, string.Join('-', parts[1..].Reverse().SkipWhile(p => p == platform).Reverse()), platform);
        });

    /// <summary>A library as the detected engine reads it, else (UE5) with the other shader hash width: 5.8 cut them from 20
    /// to 8 bytes, and a fork or an engine version told from the containers alone may not match. Each layout's counts are
    /// bounded by the file first (<see cref="LibraryEnd"/>): CUE4Parse allocates an array before it reads it, and a hash taken
    /// for a count asks for gigabytes. The layout that ends exactly at the file's end wins, else one that fits in it. Throws
    /// <see cref="InvalidDataException"/> naming the file when none fits or CUE4Parse rejects it.</summary>
    internal static FShaderCodeArchive ReadLibrary(string path, byte[] bytes, EGame game)
    {
        var version = bytes.Length >= 4 ? BitConverter.ToUInt32(bytes) : 0;
        var ue5 = game >= EGame.GAME_UE5_0;
        EGame[] layouts = ue5 ? [game, game >= EGame.GAME_UE5_8 ? EGame.GAME_UE5_7 : EGame.GAME_UE5_8] : [game];
        long? End(EGame g) => LibraryEnd(bytes, g >= EGame.GAME_UE5_8 ? 8 : 20, ioStore: version == 1);
        // pre-4.25 version 1 (OpenV1 parses it), versions CUE4Parse skips, and forks with their own header: as detected
        var pick = version is not (1 or 2) || version == 1 && !ue5 || game is EGame.GAME_MarvelRivals or EGame.GAME_ArenaBreakoutMobile ? game
            : layouts.Where(g => End(g) == bytes.Length).Concat(layouts.Where(g => End(g) != null)).Cast<EGame?>().FirstOrDefault()
              ?? throw new InvalidDataException($"{path}: not a shader library this reads as UE {VersionOf(game)}{(ue5 ? $" or {VersionOf(layouts[1])}" : "")} (its counts run past the file)");
        // a fork may write its shader compression format between the header and the code (Dead Island 2: FString "Zstd")
        if (version == 2 && Layout(bytes, pick >= EGame.GAME_UE5_8 ? 8 : 20, ioStore: false) is { } l && bytes.Length - l.End is > 5 and <= 68 and var extra
            && BitConverter.ToInt32(bytes, (int)l.Header) == extra - 4 && bytes[l.Header + extra - 1] == 0)
            bytes = [.. bytes.AsSpan(0, (int)l.Header), .. bytes.AsSpan((int)(l.Header + extra))];
        try { return new FShaderCodeArchive(new FByteArchive(path, bytes, new VersionContainer(pick))); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new InvalidDataException($"{path}: not a shader library this reads as UE {VersionOf(pick)} ({e.Message})", e); }
    }

    /// <summary>Where a version-2 (pak era: header, then the code) or IoStore (header only) shader library with
    /// <paramref name="width"/>-byte hashes ends; null when an array count is negative or runs past the file.</summary>
    internal static long? LibraryEnd(byte[] b, int width, bool ioStore) => Layout(b, width, ioStore)?.End;

    /// <summary>Where the header of a library laid out as in <see cref="LibraryEnd"/> ends, and where its code does.</summary>
    static (long Header, long End)? Layout(byte[] b, int width, bool ioStore)
    {
        // element sizes: hashes, hashes, then IoStore: chunk ids, map entries, code entries, group entries, indices;
        // pak era: map entries, code entries (FShaderCodeEntry, packed: u64 offset, u32 size, u32 uncompressed size, u8), preloads, indices
        int[] sizes = ioStore ? [width, width, 12, 8, 8, 16, 4] : [width, width, 16, 17, 16, 4];
        long p = 4, code = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            if (p + 4 > b.Length) return null;
            var n = BitConverter.ToInt32(b, (int)p);
            var start = p + 4;
            if (n < 0 || (long)n * sizes[i] > b.Length - start) return null;
            p = start + (long)n * sizes[i];
            if (!ioStore && i == 3)
                for (var e = start; e < p; e += 17) code += BitConverter.ToUInt32(b, (int)e + 8);
        }
        return (p, p + code);
    }

    static Archive? Open(AbstractVfsFileProvider provider, GameFile file)
    {
        if (file.Size >= Array.MaxLength) return OpenLarge(file, provider.Versions.Game);
        var arc = ReadLibrary(file.Path, file.Read(), provider.Versions.Game);
        switch (arc.SerializedShaders)
        {
            case null: // version 1 (UE 4.2x before 4.25), which CUE4Parse doesn't parse
                return OpenV1(file.Read());
            case FSerializedShaderArchive lib: // pak era: every shader's (compressed) code is inline in the file
                return new Archive(lib.ShaderMapHashes.Select(h => h.ToString().ToLowerInvariant()).ToArray(),
                    lib.ShaderMapEntries.Select(e => ((int)e.ShaderIndicesOffset, (int)e.NumShaders)).ToArray(), lib.ShaderIndices, lib.ShaderEntries.Length,
                    Enumerable.Range(0, lib.ShaderEntries.Length).Select(i => (Func<IEnumerable<(int, byte[])>>)(() =>
                    {
                        var e = lib.ShaderEntries[i];
                        var code = e.Size == e.UncompressedSize ? arc.ShaderCode[i] : Decompress(arc.ShaderCode[i], (int)e.UncompressedSize);
                        arc.ShaderCode[i] = null!; // free as we go
                        return [(i, code)];
                    })).ToArray(), Hashes: lib.ShaderHashes.Select(h => h.ToString().ToLowerInvariant()).ToArray());
            case FIoStoreShaderCodeArchive io: // UE5 IoStore: code lives in shader-group IoChunks, each compressed as a whole
                var readers = provider.MountedVfs.OfType<IoStoreReader>().ToList();
                return new Archive(io.ShaderMapHashes.Select(h => h.ToString().ToLowerInvariant()).ToArray(),
                    io.ShaderMapEntries.Select(e => ((int)e.ShaderIndicesOffset, (int)e.NumShaders)).ToArray(), io.ShaderIndices, io.ShaderEntries.Length,
                    io.ShaderGroupEntries.Select((g, gi) => (Func<IEnumerable<(int, byte[])>>)(() =>
                    {
                        var id = io.ShaderGroupIoHashes[gi];
                        var raw = readers.First(r => r.DoesChunkExist(id)).Read(id);
                        var group = g.CompressedSize == g.UncompressedSize ? raw : Decompress(raw[..(int)g.CompressedSize], (int)g.UncompressedSize);
                        var members = io.ShaderIndices.Skip((int)g.ShaderIndicesOffset).Take((int)g.NumShaders)
                            .Select(s => (s: (int)s, o: (int)io.ShaderEntries[s].UncompressedOffsetInGroup)).OrderBy(x => x.o).ToList();
                        return members.Select((m, k) => (m.s, group[m.o..(k + 1 < members.Count ? members[k + 1].o : (int)g.UncompressedSize)]));
                    })).ToArray(), Hashes: io.ShaderHashes.Select(h => h.ToString().ToLowerInvariant()).ToArray());
            default:
                return null;
        }
    }

    /// <summary>A pak-era library past 2 GB (Wuthering Waves' SM6 chunk library: 5.6 GB): no byte[] holds it, and CUE4Parse's
    /// whole-file read casts the size to int (a prefix 4 GB short). The header is read alone (growing until its counts fit), the
    /// code in ranges of up to 16 MB as the work runs, one read at a time (the pak reader isn't concurrent).</summary>
    static Archive OpenLarge(GameFile file, EGame game)
    {
        byte[] Read(long offset, long size) =>
            file.Read(new FByteBulkDataHeader(0, 0, checked((uint)size), offset, FBulkDataCookedIndex.Default));
        var width = game >= EGame.GAME_UE5_8 ? 8 : 20;
        byte[] head;
        (long Header, long End)? layout = null;
        for (var n = 64L << 20; ; n *= 4) // a header past 1 GB isn't one
        {
            head = Read(0, n);
            if (BitConverter.ToUInt32(head) != 2) throw new InvalidDataException($"{file.Path}: a {file.Size >> 20} MB shader library of version {BitConverter.ToUInt32(head)}, not 2");
            if ((layout = Layout(head, width, ioStore: false)) != null || n >= 1L << 30) break;
        }
        if (layout is not { } l || l.End != file.Size)
            throw new InvalidDataException($"{file.Path}: not a shader library this reads as UE {VersionOf(game)} (its counts don't end at the file's end)");
        var lib = new FSerializedShaderArchive(new FByteArchive(file.Path, head, new VersionContainer(game)) { Position = 4 });
        head = null!;
        var entries = lib.ShaderEntries;
        var order = Enumerable.Range(0, entries.Length).OrderBy(i => entries[i].Offset).ToArray();
        var batches = new List<int[]>();
        for (int s = 0, e; s < order.Length; s = e)
            for (e = s + 1; ; e++)
                if (e == order.Length || (long)(entries[order[e]].Offset + entries[order[e]].Size - entries[order[s]].Offset) > 16 << 20)
                {
                    batches.Add(order[s..e]);
                    break;
                }
        var gate = new object();
        return new Archive(lib.ShaderMapHashes.Select(h => h.ToString().ToLowerInvariant()).ToArray(),
            lib.ShaderMapEntries.Select(e => ((int)e.ShaderIndicesOffset, (int)e.NumShaders)).ToArray(), lib.ShaderIndices, entries.Length,
            batches.Select(b => (Func<IEnumerable<(int, byte[])>>)(() =>
            {
                var start = (long)entries[b[0]].Offset;
                var end = b.Max(i => (long)entries[i].Offset + entries[i].Size);
                byte[] span;
                lock (gate) span = Read(l.Header + start, end - start);
                return b.Select(i =>
                {
                    var e = entries[i];
                    var code = span.AsSpan((int)((long)e.Offset - start), (int)e.Size).ToArray();
                    return (i, e.Size == e.UncompressedSize ? code : Decompress(code, (int)e.UncompressedSize));
                }).ToList();
            })).ToArray(), Hashes: lib.ShaderHashes.Select(h => h.ToString().ToLowerInvariant()).ToArray());
    }

    /// <summary>A version-1 shader code archive (pre-4.25 ShaderCodeLibrary, e.g. Tiny Tina's Wonderlands, 4.21): u32 version,
    /// then TMap&lt;FSHAHash, FShaderCodeEntry&gt; (i32 count, per entry 20-byte hash, u64 offset, u32 size, u32 uncompressed size,
    /// u8 frequency), then the (compressed) code, offsets from its start. It has no shader maps: packages reference their
    /// shaders by that hash (<see cref="Archive.Keys"/>, <see cref="ReferencedMaps"/>). Null when the layout doesn't hold.</summary>
    internal static Archive? OpenV1(byte[] b)
    {
        const int Entry = 37;
        if (b.Length < 8 || BitConverter.ToUInt32(b, 0) != 1 || BitConverter.ToInt32(b, 4) is not (> 0 and var n) || 8L + (long)Entry * n > b.Length) return null;
        long code0 = 8 + (long)Entry * n;
        var keys = new string[n];
        var codes = new Func<IEnumerable<(int, byte[])>>[n];
        for (var i = 0; i < n; i++)
        {
            var o = 8 + Entry * i;
            var (off, size, usize) = (BitConverter.ToInt64(b, o + 20), BitConverter.ToInt32(b, o + 28), BitConverter.ToInt32(b, o + 32));
            if (off < 0 || size <= 0 || usize < size || code0 + off + size > b.Length) return null;
            keys[i] = Convert.ToHexStringLower(b.AsSpan(o, 20));
            var (start, k) = ((int)(code0 + off), i);
            codes[i] = () => [(k, size == usize ? b[start..(start + size)] : Decompress(b[start..(start + size)], usize))];
        }
        return new Archive([], [], [], n, codes, keys);
    }

    /// <summary>Shader maps of a version-1 archive's shaders: every package that exports a <see cref="ShaderMapOwners"/> object
    /// (and every global shader cache, library "Global") references its shaders by their archive hash; one map per package, its
    /// shaders in the order found, platform the archive's. <paramref name="byKey"/>: archive hash -> (SHA-1, platform).</summary>
    static List<ShaderMap> ReferencedMaps(DefaultFileProvider provider, Dictionary<string, (string Sha, string Platform)> byKey, CancellationToken ct, out int scanned)
    {
        provider.MappingsContainer = new NoMappings();
        var prefix = new Dictionary<ulong, List<(byte[] Key, string Sha, string Platform)>>();
        foreach (var (k, v) in byKey)
        {
            var key = Convert.FromHexString(k);
            var p = BitConverter.ToUInt64(key);
            if (!prefix.TryGetValue(p, out var l)) prefix[p] = l = [];
            l.Add((key, v.Sha, v.Platform));
        }
        var maps = new ConcurrentBag<ShaderMap>();
        var n = 0;
        var budget = ByteBudget.FromFreeMemory();
        Parallel.ForEach(provider.Files.Values.Where(f => f.IsUePackage || IsGlobalCache(f)).ToList(), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
        {
            var global = IsGlobalCache(f);
            if (!global && OwnsShaderMaps(provider, f) == false) return;
            using var held = budget.Take(PackageSize(provider, f));
            byte[][] parts;
            try { parts = PackageParts(provider, f, out _); }
            catch (Exception) { return; }
            Interlocked.Increment(ref n);
            var list = new List<string>();
            var platform = "";
            foreach (var data in parts)
                for (var i = 0; i + 20 <= data.Length; i++)
                    if (prefix.TryGetValue(BitConverter.ToUInt64(data, i), out var cands))
                        foreach (var c in cands)
                            if (data.AsSpan(i, 20).SequenceEqual(c.Key)) { list.Add(c.Sha); platform = c.Platform; i += 19; break; }
            if (list.Count > 0) maps.Add(new ShaderMap(f.Path, global ? "Global" : f.Path, platform, list.Distinct().ToList()));
        });
        scanned = n;
        return Canonical(maps);
    }

    /// <summary>Maps built in parallel, in a fixed order: by hash, then by content. A path can have a copy in several paks (a
    /// patch pak overrides the base pak's) and the provider lists every copy, so a path's maps can share a hash.</summary>
    public static List<ShaderMap> Canonical(IEnumerable<ShaderMap> maps) =>
        [.. maps.OrderBy(m => m.Hash, StringComparer.Ordinal).ThenBy(m => m.Platform, StringComparer.Ordinal).ThenBy(m => string.Join(',', m.Shaders), StringComparer.Ordinal)];

    /// <summary>(lowercase hex SHA-1 of the container, the container bytes). Code without a container is hashed as-is.</summary>
    static (string, ReadOnlyMemory<byte>) Hash(byte[] code)
    {
        var off = ShaderContainer.Offset(code, out var len);
        var container = off < 0 ? code.AsMemory() : code.AsMemory(off, len);
        return (Convert.ToHexStringLower(SHA1.HashData(container.Span)), container);
    }

    // Shader code may be compressed with the project's shader format (Oodle in UE5, LZ4 or Oodle in UE4, a fork's Zstd): try in turn.
    internal static byte[] Decompress(byte[] src, int size)
    {
        foreach (var m in new[] { CompressionMethod.Oodle, CompressionMethod.LZ4, CompressionMethod.Zlib, CompressionMethod.Zstd })
            try { return Compression.Decompress(src, size, m); } catch { }
        // an Oodle block the managed decoder couldn't read, which stands in for the native one on such a CPU
        var cpu = !Codecs.NativeOodle && OodleBlock(src) ? "; " + Codecs.NoNativeOodle : "";
        throw new InvalidDataException($"cannot decompress {src.Length} -> {size} bytes with Oodle/LZ4/Zlib/Zstd (starts {Convert.ToHexString(src.AsSpan(0, Math.Min(8, src.Length)))}){cpu}");
    }

    // Oodle's LZ block header: low 6 bits 0x0C (version 4), then a compressor id below 13
    static bool OodleBlock(byte[] b) => b.Length >= 2 && (b[0] & 0x3F) == 0x0C && (b[1] & 0x7F) < 13;

    static void InitCodecs() => Codecs.Load();

    static DefaultFileProvider Mount(string paks, EGame game, FAesKey? key)
    {
        InitCodecs();
        var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(game), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount(); // unencrypted containers
        if (key != null) provider.SubmitKey(new FGuid(), key); // encrypted ones the key opens
        provider.PostMount();
        return provider;
    }

    /// <summary>The largest &lt;Project&gt;/Content/Paks (by .pak + .ucas bytes); null = not a cooked UE game.</summary>
    static string? PaksDir(string installDir) =>
        Directory.Exists(installDir)
            ? Directory.EnumerateDirectories(installDir, "Paks", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == "Content")
                .Select(p => (p, size: Directory.EnumerateFiles(p).Where(f => f.EndsWith(".pak") || f.EndsWith(".ucas")).Sum(f => new FileInfo(f).Length)))
                .Where(x => x.size > 0).OrderByDescending(x => x.size).Select(x => x.p).FirstOrDefault()
            : null;

    static string VersionOf(EGame g) => Regex.Match(g.ToString(), @"^GAME_UE(\d)_(\d+)$") is { Success: true } m ? $"{m.Groups[1]}.{m.Groups[2]}" : g.ToString();

    static EGame GameOf(EngineInfo e) => Enum.Parse<EGame>(e.Fork ?? $"GAME_UE{e.Version.Replace('.', '_')}");

    // Engine version: "++UE4+Release-4.26" style build string in the exe (UTF-16, streamed: exes run to 500 MB; not read
    // for anti-cheat games, whose files we only read paks/ini of; Xbox app games' exes can't be opened at all), else the
    // containers' format versions (approximate), else the PE version. upTo: the latest version a TOC version allows;
    // downTo: the earliest a pak version allows.
    static EGame DetectEngine(Game game, string paks, out EGame downTo, out EGame upTo, out bool fromContainers)
    {
        var exePath = game.ExePath;
        (downTo, upTo, fromContainers) = (0, 0, false);
        if (File.Exists(exePath) && GameFiles.DetectAntiCheat(game) == AntiCheat.None && BuildString(exePath) is { } built) return upTo = built;
        fromContainers = true;
        var toc = Directory.EnumerateFiles(paks, "*.utoc").Select(TocVersion).DefaultIfEmpty(0).Max();
        // EIoStoreTocVersion (IoStore.h, Latest at each release tag): 2 = 4.26, 3 = 4.27 (High on Life), 5 = 5.0-5.3 (the UE5
        // default: 5.1), 6 = 5.4 (REANIMAL, Darwin's Paradox: their shaders use 5.4's root constants), 8 = 5.5-5.7 (Detect tells 5.6),
        // 9 (AddedSourceHashes) and 10 (ContainerEncryptionMethod) = 5.8+ (Fortnite: 10, 8-byte shader library hashes)
        upTo = toc switch { 4 or 5 => EGame.GAME_UE5_3, 7 or 8 => EGame.GAME_UE5_7, _ => 0 };
        if (toc > 0)
            return toc switch { <= 2 => EGame.GAME_UE4_26, 3 => EGame.GAME_UE4_27, <= 5 => EGame.GAME_UE5_1, 6 => EGame.GAME_UE5_4, <= 8 => EGame.GAME_UE5_5, _ => EGame.GAME_UE5_8 };
        var pak = Directory.EnumerateFiles(paks, "*.pak").Select(PakVersion).DefaultIfEmpty(0).Max();
        if (pak is > 0 and < 11) // EPakFileVersion: 7 = 4.21, 8 = 4.22-4.24, 9 = 4.25, 10 = 4.26; 11 = 4.26.2 to 5.x: the PE version below
            return pak switch { <= 7 => EGame.GAME_UE4_21, 8 => EGame.GAME_UE4_24, 9 => EGame.GAME_UE4_25, _ => EGame.GAME_UE4_26 };
        string pe;
        try { pe = File.Exists(exePath) ? FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? "" : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { pe = ""; }
        if (pak >= 11 && !pe.StartsWith("UE5")) downTo = EGame.GAME_UE4_26; // 4.26.2 writes 11+ too: a 4.26 fork (Wuthering Waves) is in range
        return pe.StartsWith("UE5") ? EGame.GAME_UE5_1 : EGame.GAME_UE4_27;
    }

    /// <summary>The engine version in the exe's "++UE4+Release-4.26" build string; null if absent or the exe can't be read.</summary>
    static EGame? BuildString(string exePath)
    {
        try
        {
            using var f = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            var buf = new byte[4 << 20];
            var marker = Encoding.Unicode.GetBytes("++UE");
            for (int kept = 0, n; (n = f.Read(buf, kept, buf.Length - kept)) > 0;)
            {
                var len = kept + n;
                for (var i = buf.AsSpan(0, len).IndexOf(marker); i >= 0;)
                {
                    var s = Encoding.Unicode.GetString(buf, i, Math.Min(120, len - i) & ~1);
                    if (Regex.Match(s, @"^\+\+UE[45]\+[^\x00]{0,40}?([45])\.(\d+)") is { Success: true } m) return Clamp(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                    var next = buf.AsSpan(i + 1, len - i - 1).IndexOf(marker);
                    i = next < 0 ? -1 : i + 1 + next;
                }
                kept = Math.Min(len, 120); // a string cut at the buffer end is seen whole next round
                Buffer.BlockCopy(buf, len - kept, buf, 0, kept);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // Xbox app games: the exe is encrypted at rest
        return null;

        static EGame Clamp(int major, int minor)
        {
            for (; minor >= 0; minor--) if (Enum.TryParse<EGame>($"GAME_UE{major}_{minor}", out var g)) return g;
            return major == 5 ? EGame.GAME_UE5_0 : EGame.GAME_UE4_27;
        }
    }

    static int PakVersion(string pak)
    {
        try { using var r = new PakFileReader(pak); return (int)r.Info.Version; }
        catch (Exception) { return 0; } // not a pak CUE4Parse knows
    }

    /// <summary>UE 5.5 and 5.6 both write TOC version 8. A package header tells them apart: 5.6 (VERSE_CELLS) puts 8 bytes of
    /// cell offsets between the 52-byte zen summary and the name batch, whose hash version (0xC1640000) moves from +60 to +68.
    /// Reads the head of one package in the smallest container with a directory index.</summary>
    static bool HasVerseCells(string paks, FAesKey? key)
    {
        try
        {
            var toc = Directory.EnumerateFiles(paks, "*.utoc").Where(p => (TocHeader(p)[80] & 8) != 0).MinBy(p => new FileInfo(p).Length);
            if (toc == null) return false;
            using var r = new IoStoreReader(toc, EIoStoreTocReadOptions.ReadDirectoryIndex, new VersionContainer(EGame.GAME_UE5_5));
            if (r.IsEncrypted) r.AesKey = key ?? throw new InvalidDataException("encrypted");
            r.Mount(StringComparer.OrdinalIgnoreCase);
            var head = r.Files.Values.First(f => f.Extension == "uasset").Read(new FByteBulkDataHeader(default, 0, 76, 0, default));
            return BitConverter.ToUInt64(head, 68) == 0xC1640000;
        }
        catch (Exception) { return false; } // unreadable: keep the TOC's guess
    }

    /// <summary>Shader files named in an unencrypted .utoc's directory index, whose string table holds file names as plain
    /// text: "ShaderArchive-&lt;Library&gt;-&lt;Format&gt;.ushaderbytecode" (its platform goes to <paramref name="libraries"/>)
    /// and "GlobalShaderCache-&lt;Format&gt;.bin" (to <paramref name="globals"/>). A byte scan, 4 MB at a time: loading the TOC
    /// (CUE4Parse builds every chunk and file entry) costs ~50 MB per big container.</summary>
    static void TocShaderFiles(string utoc, List<string> libraries, List<string> globals)
    {
        using var f = new FileStream(utoc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        var buf = new byte[4 << 20];
        for (int kept = 0, n; (n = f.Read(buf, kept, buf.Length - kept)) > 0;)
        {
            var len = kept + n;
            foreach (Match m in ShaderFileName().Matches(Encoding.Latin1.GetString(buf, 0, len)))
                if (m.Groups[1].Success) libraries.Add(m.Groups[1].Value.Split('-')[^1]); else globals.Add(m.Groups[2].Value);
            kept = Math.Min(len, 512); // a name cut at the buffer end is seen whole next round
            Buffer.BlockCopy(buf, len - kept, buf, 0, kept);
        }
    }

    [GeneratedRegex(@"ShaderArchive-([\w-]+)\.ushaderbytecode|GlobalShaderCache-([\w-]+)\.bin")]
    private static partial Regex ShaderFileName();

    static bool IsGlobalCache(GameFile f) => f.Extension == "bin" && f.Name.StartsWith("GlobalShaderCache-", StringComparison.OrdinalIgnoreCase);

    static int TocVersion(string utoc) => TocHeader(utoc)[16]; // FIoStoreTocHeader: 16-byte magic, then uint8 Version

    static byte[] TocHeader(string utoc)
    {
        using var s = File.OpenRead(utoc);
        var b = new byte[81];
        s.ReadAtLeast(b, b.Length, false);
        return b;
    }

    /// <summary>Forks on an older engine than their containers tell, with the version those tell and the names (normalized as
    /// <see cref="DetectFork"/> does) a folder, exe or store title must equal: Dead Island 2 is Dambuster's 4.25 (its root
    /// signatures carry 4.25's static samplers, s1000-s1005 in space 0) in 4.27's IoStore containers.</summary>
    static readonly (EGame Fork, EGame Containers, string[] Names)[] OlderBase = [(EGame.GAME_DeadIsland2, EGame.GAME_UE4_27, ["deadisland2", "deadisland"])];

    /// <summary>Known forks: a CUE4Parse EGame whose name matches the install folder or exe name (roman numerals as digits)
    /// and whose base engine version is <paramref name="baseGame"/>'s, or from <paramref name="downTo"/>'s up to
    /// <paramref name="upTo"/>'s when the containers tell a range. A name equal to the folder's or exe's wins over a longer one (a beta's), then the base version's. With
    /// none, and the version only from the containers (<paramref name="fromContainers"/>), an <see cref="OlderBase"/> fork
    /// whose name the folder, exe or <paramref name="title"/> equals.</summary>
    internal static EGame? DetectFork(EGame baseGame, EGame upTo, string folder, string exeName, bool fromContainers = false, string title = "", EGame downTo = 0)
    {
        uint b0 = (uint)baseGame & 0xFFFF0000, lo = downTo == 0 ? b0 : Math.Min(b0, (uint)downTo & 0xFFFF0000), hi = Math.Max(b0, (uint)upTo & 0xFFFF0000);
        string Norm(string s) => Regex.Replace(Regex.Replace(s, @"\b(XX|XIX|XVIII|XVII|XVI|XV|XIV|XIII|XII|XI|X|IX|VIII|VII|VI|V|IV|III|II)\b",
            m => Array.IndexOf(["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX"], m.Value.ToUpperInvariant()).ToString(),
            RegexOptions.IgnoreCase), "[^A-Za-z0-9]", "").ToLowerInvariant();
        var names = new[] { Norm(folder), Norm(Regex.Replace(exeName, "-Win(64|GDK)-Shipping$", "", RegexOptions.IgnoreCase)) }.Where(n => n.Length >= 4).ToList();
        return Enum.GetValues<EGame>().Where(g => ((uint)g & 0xFFFF) != 0 && ((uint)g & 0xFFFF0000) is var b && b >= lo && b <= hi)
            .Select(g => (g, n: Norm(g.ToString()[5..]))).Where(x => x.n.Length >= 4 && names.Any(n => n.StartsWith(x.n) || x.n.StartsWith(n)))
            .OrderByDescending(x => names.Contains(x.n)).ThenByDescending(x => ((uint)x.g & 0xFFFF0000) == b0).ThenByDescending(x => x.n.Length)
            .Select(x => (EGame?)x.g).FirstOrDefault()
            ?? (fromContainers ? OlderBase.Where(o => b0 == (uint)o.Containers && names.Append(Norm(title)).Any(o.Names.Contains)).Select(o => (EGame?)o.Fork).FirstOrDefault() : null);
    }
}
