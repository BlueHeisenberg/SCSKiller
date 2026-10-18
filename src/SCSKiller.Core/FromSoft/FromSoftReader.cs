using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.FromSoft;

/// <summary>FromSoftware's engine (Dark Souls III, Elden Ring, Elden Ring Nightreign; Dark Souls: Remastered's loose files).
/// Shaders live in <c>shader/*.shaderbnd.dcx</c> (and <c>*.shaderbdlebnd.dcx</c> bundles): DCX-compressed BND4 binders
/// whose files hold the DXBC/DXIL containers. In DS3 and Elden Ring they sit inside the BHD5/BDT archives (headers decrypted
/// with the game's public RSA keys, read from its exe at run time: <see cref="SoulsKeys"/>), found by the hash of their
/// well-known names. Read-only and in memory: nothing is unpacked to disk, the game's own code (and its Oodle DLL) is never
/// loaded or run. The game creates its root signatures from the 1.1 ones its shaders carry (RTS0), serialized again at
/// version 1.0 (<see cref="RootSig.AsVersion10"/>), so each shader's root signature is that blob, served by ReadShaders.
/// EngineInfo: Family "FromSoftware", Version "&lt;title&gt; DXBC|DXIL[+RTS0]" once indexed ("&lt;title&gt;" at Detect).</summary>
/// <param name="dataDir">the app's data folder: the archive keys found in each game's exe are kept there (local only)</param>
/// <param name="download">see <see cref="SoulsKeys"/> (tests pass a fake, and its <paramref name="uxmSha256"/>)</param>
public sealed class FromSoftReader(string dataDir, Func<string, byte[]?>? download = null, string uxmSha256 = SoulsKeys.UxmSha256) : IEngineReader
{
    public const string Family = "FromSoftware";

    readonly SoulsKeys keys = new(dataDir, download, uxmSha256);

    /// <param name="Exe">the process that creates the device (the Steam exe, not start_protected_game.exe)</param>
    /// <param name="Hash64">64-bit archive name hashes (Elden Ring and later)</param>
    /// <param name="Archives">BHD5 archives next to the exe (their public RSA keys come from the exe at run time,
    /// <see cref="SoulsKeys"/>). Empty: loose files in <c>shader\</c> (Dark Souls: Remastered).</param>
    /// <param name="Uxm">the exe doesn't carry the keys in the clear: their dictionary in <see cref="SoulsKeys.Uxm"/>, downloaded</param>
    public sealed record Title(string Exe, string Name, string Api, bool Hash64, string[] Archives, string? Uxm = null);

    /// <summary>The installs this reader knows. Sound archives (sd\) are left out: no shaders. APIs: DS3 and DSR are
    /// DirectX 11 only, Elden Ring and Nightreign DirectX 12 only.</summary>
    public static readonly Title[] Titles =
    [
        new("DarkSoulsIII.exe", "Dark Souls III", "D3D11", false, ["Data1", "Data2", "Data3", "Data4", "Data5", "DLC1", "DLC2"], "DarkSouls3Keys"),
        new("DarkSoulsRemastered.exe", "Dark Souls: Remastered", "D3D11", false, []),
        new("eldenring.exe", "Elden Ring", "D3D12", true, ["Data0", "Data1", "Data2", "Data3", "DLC"], "EldenRingKeys"),
        new("nightreign.exe", "Elden Ring Nightreign", "D3D12", true, ["Data0", "Data1", "Data2", "Data3"]),
    ];

    /// <summary>Shader files by name (the archives only know their hashes): every shader name in the community's archive
    /// dictionaries (UXM Selective Unpack's DarkSouls3 / EldenRing / EldenRingNightreign dictionaries), debug builds left out.</summary>
    public static readonly string[] ShaderFiles =
    [
        .. new[] { "debugfont", "guishader", "gxdecal", "gxffxshader", "gxflvershader", "gxgui", "gxposteffect", "gxrenderershader", "gxshader",
            "grass", "title", "gxraytracing" }.Select(n => $"/shader/{n}.shaderbnd.dcx"),
        .. new[] { "shaderbdle", "shaderbdle_[rt]", "shaderbdle_dlc01", "shaderbdle_dlc01_[rt]", "shaderbdle_dlc02", "shaderbdle_dlc02_[rt]", "speedtree", "speedtree_[rt]" }
            .SelectMany(n => new[] { $"/shader/{n}.shaderbdlebnd.dcx", $"/shader/{n}.shaderbdlebnd.devpatch.dcx" }),
    ];

    public static Title? TitleOf(Game game) =>
        Titles.FirstOrDefault(t => t.Exe.Equals(Path.GetFileName(game.ExePath), StringComparison.OrdinalIgnoreCase));

    public int ReaderVersion(EngineInfo engine) => 2;

    public EngineInfo? Detect(Game game)
    {
        var dir = Path.GetDirectoryName(game.ExePath);
        if (dir == null || !Directory.Exists(dir)) return null;
        var title = TitleOf(game);
        if (title == null)
            return Directory.EnumerateFiles(dir, "*.bhd").Any(b => File.Exists(Path.ChangeExtension(b, ".bdt")) && IsEncryptedBhd(b))
                ? new EngineInfo(Family, "-", null, UnrealRhi.Ambiguous, false, "FromSoftware archives SCSKiller has no key for (a title it doesn't know yet)") // not Encrypted: no user key opens them
                : null;
        if (Archives(game, dir, title, null, out var why) is not { } archives) return new EngineInfo(Family, "-", title.Name, title.Api, false, why);
        // Planner.Check runs on this, before any index: a sample (the smallest shader file) says DXBC or DXIL, and whether
        // the shaders carry their root signatures
        int programs = 0, dxil = 0, rts0 = 0;
        foreach (var (name, bytes) in ShaderFilesOf(dir, title, archives, sample: true, null, CancellationToken.None))
        {
            Visit(name, bytes, (_, c) =>
            {
                if (Dxbc.Kind(c) is < 0 or 6) return; // root-signature-only containers, DXR libraries (local root signatures)
                programs++;
                if (!Dxbc.Part(c, "DXIL"u8).IsEmpty) dxil++;
                if (!Dxbc.Part(c, "RTS0"u8).IsEmpty) rts0++;
            }, CancellationToken.None);
            if (programs > 0) break;
        }
        var version = programs == 0 ? "-"
            : (dxil == programs ? "DXIL" : dxil == 0 ? "DXBC" : "DXIL+DXBC") + (rts0 >= 0.9 * programs ? CarvedReader.EmbeddedRootSignatures : "");
        return new EngineInfo(Family, version, title.Name, title.Api, false, programs == 0 ? "no shaders found in its shader files" : null, Note: why);
    }

    /// <summary>The archives present with no key stored: a key that turns up later (downloaded, or added by hand) redoes
    /// Detect and makes a warm stale, so the next compile indexes that archive too.</summary>
    public string DetectStamp(Game game, EngineInfo? engine) => IndexStamp(game);

    public string IndexStamp(Game game) => TitleOf(game) is { Archives.Length: > 0 } title && Path.GetDirectoryName(game.ExePath) is { } dir
        ? string.Join(',', keys.Unstored(game, SourceFiles(dir, title).Select(f => Path.GetFileNameWithoutExtension(f.Name)))) : "";

    /// <summary>A BHD5 header is RSA-encrypted when it doesn't start with the magic in the clear.</summary>
    static bool IsEncryptedBhd(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> m = stackalloc byte[4];
            return f.Length >= 256 && f.Length % 256 == 0 && f.Read(m) == 4 && !m.SequenceEqual("BHD5"u8);
        }
        catch (IOException) { return false; }
    }

    /// <summary>Bump when the same files index differently (e.g. <see cref="ShaderFiles"/> grows): plans on the old index go stale.</summary>
    const int IndexVersion = 2;
    const string D3D11 = "D3D11";

    /// <summary>Where each shader was found (SHA-1 -> its top-level shader file), per game id, from the last Index:
    /// ReadShaders reopens only those files. ponytail: in memory only, like CarvedReader's; ReadShaders re-indexes in a
    /// process that never indexed.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> located = new();

    /// <summary>The root signatures the game creates (SHA-1 -> blob), per game id, from the last Index.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, byte[]>> created = new();

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var title = TitleOf(game) ?? throw new NotSupportedException($"{game.ExePath}: not a FromSoftware title this reader knows");
        var sw = Stopwatch.StartNew();
        var dir = Path.GetDirectoryName(game.ExePath)!;
        var archives = Archives(game, dir, title, log, out var why) ?? throw new InvalidDataException(why);
        var fileOf = new Dictionary<string, string>();          // container -> its top-level shader file
        var shaders = new Dictionary<string, ShaderInfo>();
        var pools = new Dictionary<string, List<string>>();     // innermost binder -> its shaders, file order
        var partOf = new Dictionary<string, string>();          // shader -> SHA-1 of its RTS0 part
        var rootSigs = new Dictionary<string, (string Sha, byte[] Blob)?>(); // RTS0 part -> the root signature the game creates from it
        var platformOf = new Dictionary<string, string>();
        int bad = 0, files = 0;
        // ponytail: each shader file is decompressed whole (the ~30k-shader bundle: ~1 GB transient); stream the BND4 if memory bites
        foreach (var (name, bytes) in ShaderFilesOf(dir, title, archives, sample: false, null, ct))
        {
            files++;
            Visit(name, bytes, (at, c) =>
            {
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                if (fileOf.TryAdd(sha, name))
                {
                    var kind = Dxbc.Kind(c);
                    if (kind < 0) return;
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is not { } info) return;
                        shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                        platformOf[sha] = CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) ?? (Dxbc.Part(c, "DXIL"u8).IsEmpty ? D3D11 : CarvedReader.Platform);
                        if (Dxbc.Part(c, "RTS0"u8) is { IsEmpty: false } rts && Convert.ToHexStringLower(SHA1.HashData(rts)) is var part)
                        {
                            partOf[sha] = part;
                            if (!rootSigs.ContainsKey(part))
                                try { var b = RootSig.AsVersion10(c); rootSigs[part] = (Convert.ToHexStringLower(SHA1.HashData(b)), b); }
                                catch (RootSig.SerializeException) { rootSigs[part] = null; } // no 1.0 form: the shader plans without one
                        }
                    }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; return; } // valid container, odd program
                }
                if (!shaders.ContainsKey(sha)) return;
                var pool = Pool(at);
                if (shaders[sha].Stage == Stage.Library && pool.EndsWith(SoulsRayTracing.Bundle, StringComparison.OrdinalIgnoreCase))
                    pool += $"|p{RtCollections.OwnPayload(c)}"; // a material's closest hit and any hit pair per ray payload: one map each
                if (!pools.TryGetValue(pool, out var list)) pools[pool] = list = [];
                list.Add(sha);
            }, ct);
        }
        foreach (var (sha, part) in partOf)
            if (rootSigs[part] is { } rs) shaders[sha] = shaders[sha] with { RootSignature = rs.Sha };

        var maps = pools.SelectMany(p => p.Value.Distinct().GroupBy(s => platformOf[s])
            .Select(g => new ShaderMap(CarvedReader.Sha1Hex($"{p.Key}|{g.Key}"), p.Key, g.Key, g.ToList()))).ToList();
        created[game.Id] = rootSigs.Values.OfType<(string Sha, byte[] Blob)>().DistinctBy(r => r.Sha).ToDictionary(r => r.Sha, r => r.Blob); // two 1.1 forms can become one 1.0 blob
        located[game.Id] = fileOf;

        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        content.AppendData(Encoding.UTF8.GetBytes($"{Family} {IndexVersion}\n"));
        foreach (var f in title.Archives.Length == 0 ? SourceFiles(dir, title) : archives.Select(a => a.Bhd)) // a key found later changes it
            content.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(dir, f.FullName)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
        var platforms = shaders.Keys.Select(s => platformOf[s]).Distinct()
            .OrderBy(p => p is CarvedReader.Platform or D3D11 ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal).ToList();
        log?.Report($"{title.Name}: {files} shader files, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")}, {shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count()} embedded root signatures, "
            + $"{maps.Count} maps ({string.Join(", ", platforms)}) ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), platforms, shaders, maps, created[game.Id]);
    }

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        var title = TitleOf(game) ?? throw new NotSupportedException($"{game.ExePath}: not a FromSoftware title this reader knows");
        if (!located.TryGetValue(game.Id, out var fileOf))
        {
            Index(game, engine, null, ct);
            fileOf = located[game.Id];
        }
        var want = sha1s.Where(fileOf.ContainsKey).Select(s => fileOf[s]).ToHashSet();
        var sent = new HashSet<string>();
        foreach (var h in sha1s)
            if (created[game.Id].TryGetValue(h, out var rs) && sent.Add(h)) sink(h, rs);
        var dir = Path.GetDirectoryName(game.ExePath)!;
        var archives = Archives(game, dir, title, null, out var why, fetch: false) ?? throw new InvalidDataException(why);
        foreach (var (name, bytes) in ShaderFilesOf(dir, title, archives, sample: false, want, ct))
            Visit(name, bytes, (_, c) =>
            {
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                if (sha1s.Contains(sha) && sent.Add(sha)) sink(sha, c);
            }, ct);
    }

    /// <summary>The pool a container pairs in: the binder holding its file (a material's .shaderbdle inside a bundle, or a
    /// whole .shaderbnd), shortened to file names; a container carved from an unparsed binder (BND3) pools with its file.</summary>
    static string Pool(string at)
    {
        var parts = at.Split('|');
        return string.Join('|', (parts.Length > 1 ? parts[..^1] : parts).Select((p, i) => i == 0 ? p : Path.GetFileName(p)));
    }

    /// <summary>What the shaders come from (the content hash): the title's archive headers, or its loose shader files.</summary>
    static IEnumerable<FileInfo> SourceFiles(string dir, Title title) => title.Archives.Length == 0
        ? new DirectoryInfo(Path.Combine(dir, "shader")).EnumerateFiles("*.dcx").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        : title.Archives.Select(a => new FileInfo(Path.Combine(dir, a + ".bhd"))).Where(f => f.Exists && File.Exists(Path.ChangeExtension(f.FullName, ".bdt")));

    /// <summary>The title's archives present (a .bhd with its .bdt) that a key opens, in <see cref="Title.Archives"/> order,
    /// each with its key; the others are left out. <paramref name="why"/>, a line for the user: which shaders are left out,
    /// or, null returned, why none can be read. Where a user can put the keys goes to <paramref name="log"/> only.
    /// <paramref name="fetch"/>: may download them (<see cref="Title.Uxm"/>).</summary>
    List<(FileInfo Bhd, string Key)>? Archives(Game game, string dir, Title title, IProgress<string>? log, out string? why, bool fetch = true)
    {
        why = null;
        if (title.Archives.Length == 0) return [];
        var present = SourceFiles(dir, title).ToList();
        var found = keys.Get(game, present.ToDictionary(f => Path.GetFileNameWithoutExtension(f.Name), f => f.FullName), fetch ? title.Uxm : null, out var missing, log);
        var opened = present.Where(f => found.ContainsKey(Path.GetFileNameWithoutExtension(f.Name))).Select(f => (f, found[Path.GetFileNameWithoutExtension(f.Name)])).ToList();
        if (missing.Count == 0) return opened;
        log?.Report($"{title.Name}: no key for its archives {string.Join(", ", missing)}: the game's exe doesn't carry them in the clear"
            + (title.Uxm != null ? " and none that opens them could be downloaded (UXM Selective Unpack's)" : "")
            + $". Put one \"<archive> <key>\" line per archive (the body of its \"RSA PUBLIC KEY\" PEM) in {keys.KeyFile(game)}");
        if (opened.Count == 0)
        {
            why = "no key found for its archives" + (title.Uxm != null ? ", and none could be downloaded" : "");
            return null;
        }
        why = missing is ["DLC"] ? "Its DLC shaders are left out." : $"Shaders in {string.Join(", ", missing)} are left out.";
        return opened;
    }

    /// <summary>The title's shader files (name, raw bytes). <paramref name="sample"/>: smallest archive and file first (the
    /// caller stops when it has enough). <paramref name="only"/>: just these names.</summary>
    static IEnumerable<(string Name, byte[] Bytes)> ShaderFilesOf(string dir, Title title, IEnumerable<(FileInfo Bhd, string Key)> archives, bool sample,
        IReadOnlySet<string>? only, CancellationToken ct)
    {
        if (title.Archives.Length == 0)
        {
            var loose = SourceFiles(dir, title).Select(f => (Name: "/shader/" + f.Name, File: f)).Where(f => only?.Contains(f.Name) != false);
            foreach (var (name, f) in sample ? loose.OrderBy(f => f.File.Length) : loose)
            {
                ct.ThrowIfCancellationRequested();
                yield return (name, File.ReadAllBytes(f.FullName));
            }
            yield break;
        }
        var byHash = ShaderFiles.Where(n => only?.Contains(n) != false).ToDictionary(n => Souls.NameHash(n, title.Hash64), n => n);
        foreach (var (bhd, key) in sample ? archives.OrderBy(a => a.Bhd.Length) : archives)
        {
            if (byHash.Count == 0) yield break;
            var entries = Souls.ParseBhd(Souls.DecryptBhd(File.ReadAllBytes(bhd.FullName), key), title.Hash64).Where(e => byHash.ContainsKey(e.Hash)).ToList();
            using var bdt = File.OpenHandle(Path.ChangeExtension(bhd.FullName, ".bdt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var e in sample ? entries.OrderBy(e => e.Size) : entries.OrderBy(e => e.Offset))
            {
                ct.ThrowIfCancellationRequested();
                var name = byHash[e.Hash];
                byHash.Remove(e.Hash); // a name lives in one archive: don't decrypt the rest once all are found
                yield return (name, Souls.Read(bdt, e));
            }
        }
    }

    /// <summary>DCX -> its content, BND4 -> each file ("binder|file"), anything else: the containers carved from it.</summary>
    static void Visit(string name, byte[] b, Action<string, byte[]> found, CancellationToken ct, int depth = 0)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 8) throw new InvalidDataException($"{name}: binders nested deeper than 8");
        if (Souls.IsDcx(b)) { Visit(name, Souls.Dcx(b), found, ct, depth + 1); return; }
        if (Souls.IsBnd4(b))
        {
            foreach (var (file, data) in Souls.Bnd4(b)) Visit($"{name}|{file}", data, found, ct, depth + 1);
            return;
        }
        foreach (var (_, c) in Dxbc.Containers(b)) found(name, c);
    }
}
