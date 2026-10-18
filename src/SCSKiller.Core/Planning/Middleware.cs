using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>A known middleware DLL next to a game's exe. <see cref="Name"/> is its canonical name (the PE export name for
/// renamed copies: OptiScaler installed as dxgi.dll is "OptiScaler.dll"), <see cref="Path"/> the file.
/// <see cref="Packable"/> false: detected but not scanned (NVIDIA's DLSS runs its kernels inside the driver, not from
/// DXBC/DXIL containers it creates PSOs from).</summary>
public sealed record MiddlewareDll(string Vendor, string Name, string Path, bool Packable = true);

/// <summary>A scanned DLL: its SHA-1 (the pack key's version part) and every DXBC/DXIL container embedded in it by the
/// proxy's hash convention (SHA-1 of the container bytes sliced to the size at offset 24) -> file offset and size.</summary>
public sealed record MiddlewareImage(string Path, string ContentHash, long Size, IReadOnlyDictionary<string, (long Offset, int Size)> Containers)
{
    /// <summary>The wave sizes of the containers that declare one, read by <see cref="Middleware.Scan"/>: <see cref="Runs"/>
    /// reads nothing from the file then.</summary>
    public IReadOnlyDictionary<string, (uint Min, uint Max)>? Lanes { get; init; }

    /// <summary>The container bytes, read from the file (read-only); null when the file no longer holds that hash there.</summary>
    public byte[]? Read(string sha1)
    {
        if (!Containers.TryGetValue(sha1, out var at)) return null;
        var b = new byte[at.Size];
        using var h = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return RandomAccess.Read(h, b, at.Offset) == b.Length && Hex(SHA1.HashData(b)) == sha1 ? b : null;
    }

    /// <summary>Whether a GPU of this vendor runs the container's [WaveSize]: NVIDIA 32 lanes only, AMD 32 or 64 (a
    /// FidelityFX DLL ships wave64 twins of its kernels for AMD, which NVIDIA's runtime rejects).</summary>
    public bool Runs(string sha1, bool amd) =>
        (Lanes != null ? Lanes.TryGetValue(sha1, out var known) ? known : null : Read(sha1) is { } b ? Dxbc.WaveLanes(b) : null) is not { } l
        || l.Min <= 32 && l.Max >= 32 || amd && l.Min <= 64 && l.Max >= 64;
}

/// <summary>Middleware shader libraries (FidelityFX, XeSS, OptiScaler, DirectStorage...) next to a game's exe: detection
/// (file name or PE export name; read-only, never for anti-cheat games) and a static container scan. The scan only says
/// which shader hashes a DLL holds: PSOs are never enumerated from it, only replayed from recorded descs
/// (<see cref="MiddlewarePacks"/>).</summary>
public static class Middleware
{
    /// <summary>Vendors whose packs the community database shares, free: the
    /// upscalers. Other packs stay on the PC that recorded them.</summary>
    public static readonly string[] SharedVendors = ["amd", "intel"];

    static readonly string[] ProxyNames = ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll", "dinput8.dll", "nvngx.dll"];

    /// <summary>Vendor and canonical name of a known middleware DLL, by file name or export name; null = not middleware.</summary>
    public static (string Vendor, string Name, bool Packable)? Classify(string fileName, string? exportName)
    {
        foreach (var n in new[] { exportName, fileName }.OfType<string>())
        {
            var l = n.ToLowerInvariant();
            if (!l.EndsWith(".dll") && !l.EndsWith(".asi")) continue;
            if (l.StartsWith("optiscaler")) return ("optiscaler", "OptiScaler.dll", true);
            if (l == "amdxcffx64.dll") return ("amd", n, true);                                           // driver-shipped FidelityFX (FSR4)
            if (l.StartsWith("amd_fidelityfx_") && l.Contains("dx12")) return ("amd", n, true);            // FidelityFX SDK 1.1+/2.x
            if (l.StartsWith("ffx_") && !l.Contains("vk")) return ("amd", n, true);                        // FSR2/3 (ffx_fsr2_api_dx12_x64.dll, ffx_backend_dx12_x64.dll ...)
            if (l.StartsWith("dlssg_to_fsr3")) return ("mod", n, true);                                    // FSR3 frame generation in place of DLSS-G
            if (l.StartsWith("libxess")) return ("intel", n, true);
            if (l.StartsWith("nvngx_dlss")) return ("nvidia", n, false);
            if (l.StartsWith("sl.") && l != "sl.interposer.dll") return ("nvidia", n, true);                // Streamline plugins (sl.common, sl.nis, sl.dlss...) embed DXBC/DXIL
            if (l == "dstoragecore.dll") return ("microsoft", n, true);                                    // DirectStorage GPU decompression
        }
        return null;
    }

    /// <summary>What a player calls it: "OptiScaler", "FSR4", "FidelityFX", "FSR 2/3", "XeSS", "DLSS"...</summary>
    public static string Label(MiddlewareDll d)
    {
        var l = d.Name.ToLowerInvariant();
        return d.Vendor == "optiscaler" ? "OptiScaler" : l == "amdxcffx64.dll" ? "FSR4" : l.StartsWith("amd_fidelityfx_") ? "FidelityFX"
            : l.StartsWith("ffx_") ? "FSR 2/3" : l.StartsWith("dlssg_to_fsr3") ? "FSR3 frame generation" : l.StartsWith("libxess") ? "XeSS"
            : l.StartsWith("nvngx_dlss") ? "DLSS" : l.StartsWith("sl.") ? "Streamline" : l == "dstoragecore.dll" ? "DirectStorage" : Path.GetFileNameWithoutExtension(d.Name);
    }

    /// <summary>What OptiScaler installs next to itself whether or not the game uses them: folded into its tag.</summary>
    static readonly string[] OptiScalerBundle = ["FSR4", "FidelityFX", "XeSS", "FSR3 frame generation"];

    /// <summary>One tag per label (its DLLs' pack PSOs summed), those with pipelines first. OptiScaler's bundle folds into
    /// "OptiScaler (labels with pipelines)"; DirectStorage (I/O, not shaders) and Streamline (DLSS's plumbing) only show with pipelines.</summary>
    public static List<MiddlewareTag> Tags(IEnumerable<MiddlewareDll> dlls, Func<MiddlewareDll, int> pipelines)
    {
        var tags = dlls.GroupBy(Label).Select(g => new MiddlewareTag(g.Key, g.Select(d => d.Name).ToList(), g.Sum(pipelines)))
            .Where(t => t.Label is not ("DirectStorage" or "Streamline") || t.Pipelines > 0).ToList();
        if (tags.Find(t => t.Label == "OptiScaler") is { } opti)
        {
            var bundle = tags.Where(t => OptiScalerBundle.Contains(t.Label)).ToList();
            var known = bundle.Where(t => t.Pipelines > 0).OrderByDescending(t => t.Pipelines).Select(t => t.Label).ToList();
            tags[tags.IndexOf(opti)] = new(known.Count > 0 ? $"OptiScaler ({string.Join(", ", known)})" : "OptiScaler",
                [.. opti.Dlls, .. bundle.SelectMany(t => t.Dlls)], opti.Pipelines + bundle.Sum(t => t.Pipelines));
            tags.RemoveAll(bundle.Contains);
        }
        return [.. tags.OrderBy(t => t.Pipelines > 0 ? 0 : 1)];
    }

    /// <summary>The known middleware DLLs in the game exe's folder (not below it); none for anti-cheat games.</summary>
    public static List<MiddlewareDll> Detect(Game game) =>
        GameFiles.DetectAntiCheat(game) != AntiCheat.None || Path.GetDirectoryName(game.ExePath) is not { } dir ? [] : Detect(dir);

    public static List<MiddlewareDll> Detect(string exeDir)
    {
        var found = new List<MiddlewareDll>();
        if (!Directory.Exists(exeDir)) return found;
        foreach (var path in Directory.EnumerateFiles(exeDir, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(path);
            if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)) continue;
            // the export name is read only where a rename is common (proxy names) or the file name already says middleware
            var byName = Classify(file, null);
            string? export = null;
            if (byName == null && !ProxyNames.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            try { export = ExportName(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (Classify(file, export) is { } c) found.Add(new MiddlewareDll(c.Vendor, c.Name, path, c.Packable));
        }
        // one entry per canonical name (OptiScaler.dll next to its dxgi.dll copy): the real file name first
        return found.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(d => Path.GetFileName(d.Path).Equals(d.Name, StringComparison.OrdinalIgnoreCase) ? 0 : 1).First()).ToList();
    }

    /// <summary>The PE export directory's DLL name (what the DLL was linked as, kept when it's renamed), null if none.</summary>
    public static string? ExportName(string path)
    {
        try
        {
            using var pe = PeFile.Open(path);
            return PeFile.ExportName(pe);
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException) { return null; }   // not a PE file
    }

    static readonly ConcurrentDictionary<(string, string), MiddlewareImage> scans = new();

    // each DLL's last scan by path, kept between runs (LoadImages, SaveImages): a start reads a DLL's stamp, not the whole file
    static readonly Dictionary<string, KeptImage> kept = [];
    static long keptVersion;
    static readonly Dictionary<string, long> keptSaved = new(StringComparer.OrdinalIgnoreCase);   // file -> the version it holds
    static readonly Lock keptGate = new();
    static readonly JsonSerializerOptions KeptJson = new() { IncludeFields = true };   // the containers' offsets are tuples

    sealed record KeptImage(string Stamp, MiddlewareImage Image);

    /// <summary>Adds the scans <see cref="SaveImages"/> wrote to <paramref name="file"/>; one that can't be read adds none.</summary>
    public static void LoadImages(string file)
    {
        try
        {
            if (!File.Exists(file) || JsonSerializer.Deserialize<Dictionary<string, KeptImage>>(File.ReadAllBytes(file), KeptJson) is not { } saved) return;
            lock (keptGate)
                foreach (var (path, k) in saved)
                    if (k?.Image.Lanes != null) kept.TryAdd(path, k);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>Writes every DLL's last scan to <paramref name="file"/> when one was made since it was last written.</summary>
    public static void SaveImages(string file)
    {
        lock (keptGate)
        {
            if (keptSaved.GetValueOrDefault(file) == keptVersion) return;
            App.AppStore.WriteAtomic(file, JsonSerializer.SerializeToUtf8Bytes(kept, KeptJson));
            keptSaved[file] = keptVersion;
        }
    }

    /// <summary>Every scan forgotten, the saved ones too: each DLL is read again (a refresh the user asked for).</summary>
    public static void ForgetScans()
    {
        scans.Clear();
        lock (keptGate) kept.Clear();
    }

    /// <summary>Hashes the file and every embedded container (read-only; cached per path and the file's size, write time
    /// and content sample, as <see cref="App.KeyFiles"/> tells a file).</summary>
    public static MiddlewareImage Scan(string path)
    {
        var fi = new FileInfo(path);
        var key = (fi.FullName.ToLowerInvariant(), App.KeyFiles.Stamp(fi.FullName) ?? "");
        if (scans.TryGetValue(key, out var hit)) return hit;
        lock (keptGate)
            if (kept.GetValueOrDefault(key.Item1) is { } k && k.Stamp == key.Item2) return scans[key] = k.Image;
        byte[] data;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[f.Length];
            f.ReadExactly(data);
        }
        var found = new Dictionary<string, (long, int)>();
        var lanes = new Dictionary<string, (uint, uint)>();
        var span = data.AsSpan();
        for (var pos = 0; pos < span.Length;)
        {
            var k = span[pos..].IndexOf("DXBC"u8);
            if (k < 0) break;
            pos += k;
            var size = Dxbc.HeaderSize(span[pos..]);
            if (size > 0 && pos + (long)size <= span.Length && Dxbc.Valid(span.Slice(pos, size)))
            {
                var hash = Hex(SHA1.HashData(span.Slice(pos, size)));
                if (found.TryAdd(hash, (pos, size)) && Dxbc.WaveLanes(span.Slice(pos, size)) is { } l) lanes[hash] = l;
            }
            pos += 4;
        }
        var image = new MiddlewareImage(fi.FullName, Hex(SHA1.HashData(data)), data.Length, found) { Lanes = lanes };
        if (scans.Count > 64) scans.Clear();
        scans[key] = image;
        lock (keptGate)
            if (key.Item2.Length > 0)
            {
                kept[key.Item1] = new(key.Item2, image);
                keptVersion++;
            }
        return image;
    }

    /// <summary>The containers among <paramref name="sha1s"/> that the packable middleware DLLs next to the game's exe hold
    /// (read-only, each verified by SHA-1; none for anti-cheat games): <see cref="Rehydrate.Run"/>'s extra blob source, as
    /// a hash-only recording's middleware PSOs (FSR4's compute shaders...) aren't in the game's index. A DLL is scanned
    /// only when there's something to look for.</summary>
    public static IEnumerable<(string Sha1, byte[] Bytes)> Blobs(Game game, IReadOnlySet<string> sha1s)
    {
        if (sha1s.Count == 0) yield break;
        var left = sha1s.ToHashSet();
        foreach (var d in Detect(game).Where(d => d.Packable))
        {
            if (left.Count == 0) yield break;
            if (TryScan(d.Path) is not { } image) continue;
            foreach (var h in left.Where(image.Containers.ContainsKey).ToList())
                if (image.Read(h) is { } b) { left.Remove(h); yield return (h, b); }
        }
    }

    internal static MiddlewareImage? TryScan(string path)
    {
        try { return Scan(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>One middleware DLL version's pack: the recorded PSOs ('C'/'G'/'S' payloads, exactly as the proxy recorded
/// them) whose every shader is a container of that DLL, plus the root signatures they use as 'B' records (RTS0-only
/// blobs, generated binding metadata; a root signature the DLL itself holds is not stored). Hash-only: no shader bytes.
/// File: "SCSKPACK", u32 version, u32 header length, header JSON (<see cref="PackHeader"/>), then proxy db records.</summary>
public sealed class MiddlewarePack
{
    /// <param name="Object">a shared pack's: the community database object it was downloaded as</param>
    /// <param name="Gpu">this PC's pack: the GPU vendor whose recordings filled it ("nvidia", "amd"); null in a pack from
    /// before packs kept it</param>
    public sealed record PackHeader(string Vendor, string Dll, string ContentHash, long Size, List<string> Sources, string? Object = null, string? Gpu = null);

    const int Version = 1;
    public PackHeader Header { get; private set; }
    public List<Rec> Entries { get; } = [];
    public Dictionary<string, byte[]> RootSignatures { get; } = [];

    public MiddlewarePack(string vendor, string dll, string contentHash, long size, string? obj = null, string? gpu = null) =>
        Header = new(vendor, dll, contentHash, size, [], obj, gpu);

    public void Adopt(string gpu) => Header = Header with { Gpu = gpu };

    public static string FileName(string dll, string contentHash) => $"{dll.ToLowerInvariant()}-{contentHash}.pack";

    public bool Add(Rec r, string source)
    {
        if (Entries.Any(e => e.Key == r.Key)) return false;
        Entries.Add(r);
        if (!Header.Sources.Contains(source)) Header.Sources.Add(source);
        return true;
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var f = File.Create(tmp))
        {
            PlanFile.WriteHeader(f, "SCSKPACK"u8, Version, Header);
            foreach (var (h, b) in RootSignatures.OrderBy(r => r.Key, StringComparer.Ordinal)) WriteBlob(f, h, b);
            foreach (var r in Entries) PsoDb.Write(f, r.Tag, r.Payload);
        }
        File.Move(tmp, path, true);
        App.KeyFiles.Forget(path);
    }

    /// <summary>The header alone; null when the file isn't a readable pack.</summary>
    public static PackHeader? ReadHeader(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return PlanFile.ReadHeader<PackHeader>(f, "SCSKPACK"u8, Version, $"{path}: not a v{Version} pack");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static MiddlewarePack Read(string path)
    {
        using var f = File.OpenRead(path);
        var h = PlanFile.ReadHeader<PackHeader>(f, "SCSKPACK"u8, Version, $"{path}: not a v{Version} pack");
        var pack = new MiddlewarePack(h.Vendor, h.Dll, h.ContentHash, h.Size, h.Object, h.Gpu);
        pack.Header.Sources.AddRange(h.Sources);
        foreach (var r in PsoDb.Read(f))
            if (r.Tag == 'B') pack.RootSignatures[Hex(r.Payload.AsSpan(0, 20))] = r.Payload[20..];
            else if (r.Tag is 'C' or 'G' or 'S') pack.Entries.Add(r);
        return pack;
    }
}

/// <summary>Middleware packs on disk: <c>&lt;dir&gt;\&lt;vendor&gt;\&lt;dll&gt;-&lt;sha1&gt;.pack</c> (the app's dir is
/// %LOCALAPPDATA%\SCSKiller\packs). Filled from recordings (<see cref="Promote"/>), used to seed the plan of any game with
/// the same DLL version next to its exe (<see cref="Seed"/>); the shader bytes come from that install's copy at
/// materialize time (<see cref="Materialize"/>).</summary>
public sealed class MiddlewarePacks(string dir)
{
    public string Dir { get; } = dir;

    public string PathOf(string vendor, string dll, string contentHash) => Path.Combine(Dir, vendor, MiddlewarePack.FileName(dll, contentHash));

    public MiddlewarePack? Load(MiddlewareDll dll, MiddlewareImage image)
    {
        var p = PathOf(dll.Vendor, dll.Name, image.ContentHash);
        try { return File.Exists(p) ? MiddlewarePack.Read(p) : null; }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException) { return null; }
    }

    /// <summary>PSOs in the pack of this DLL version; 0 without one.</summary>
    public int Pipelines(MiddlewareDll dll) => Keys(dll).Count();

    /// <summary>The record keys of the pack of this DLL version; none without one. <paramref name="amd"/> given: only
    /// the entries a GPU of that vendor runs (<see cref="Runs"/>), what seeds its plans.</summary>
    public IEnumerable<string> Keys(MiddlewareDll dll, bool? amd = null) => Seeds(dll, amd).Entries.Select(e => e.Key);

    /// <summary>The entries of the pack of this DLL version (<paramref name="amd"/> given: those a GPU of that vendor runs)
    /// and the root signatures the pack keeps for them. The DLL is hashed (<see cref="Middleware.Scan"/>'s cache) only when a pack
    /// of its name exists.</summary>
    public (List<Rec> Entries, List<string> RootSignatures) Seeds(MiddlewareDll dll, bool? amd = null)
    {
        var dir = Path.Combine(Dir, dll.Vendor);
        try
        {
            if (!dll.Packable || !Directory.Exists(dir) || !Directory.EnumerateFiles(dir, MiddlewarePack.FileName(dll.Name, "*")).Any()) return ([], []);
            var image = Middleware.Scan(dll.Path);
            if (Load(dll, image) is not { } pack) return ([], []);
            List<Rec> entries = [.. pack.Entries.Where(e => amd is not { } a || Runs(e, image, a))];
            return (entries, [.. entries.Select(e => Parse(e).Rs).Where(pack.RootSignatures.ContainsKey).Distinct()]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ([], []); }
    }

    /// <summary>Whether a GPU of this vendor runs every shader of a pack entry (<see cref="MiddlewareImage.Runs"/>): the one
    /// rule for what a pack seeds. <paramref name="memo"/>: per shader, across calls.</summary>
    public static bool Runs(Rec entry, MiddlewareImage image, bool amd, Dictionary<string, bool>? memo = null)
    {
        Pso p;
        try { p = Parse(entry); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException) { return false; }   // seeds nothing
        return p.Stages.Values.All(h => memo == null ? image.Runs(h, amd) : memo.TryGetValue(h, out var ok) ? ok : memo[h] = image.Runs(h, amd));
    }

    /// <summary>A shared pack downloaded for <paramref name="dll"/> (canonical records, <see cref="HashOnly.CheckPack"/>)
    /// as a pack of this install's copy: only the PSOs whose every shader that copy holds and this GPU vendor runs
    /// (<see cref="MiddlewareImage.Runs"/>), and whose root signature the copy holds or the download has as a 'B' (a PSO
    /// naming anything else could only be skipped). <paramref name="dropped"/>: the others.</summary>
    public static MiddlewarePack FromShared(List<Rec> records, MiddlewareDll dll, MiddlewareImage image, bool amd, string obj, out int dropped)
    {
        var blobs = records.Where(r => r.Tag == 'B').ToDictionary(r => Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        var pack = new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size, obj);
        pack.Header.Sources.Add("community");
        dropped = 0;
        foreach (var r in records.Where(r => r.Tag != 'B'))
        {
            var p = Parse(r);
            if (p.Stages.Count == 0 || !p.Stages.Values.All(h => image.Containers.ContainsKey(h) && image.Runs(h, amd))
                || p.Rs != Zero && !image.Containers.ContainsKey(p.Rs) && !blobs.ContainsKey(p.Rs)) { dropped++; continue; }
            if (blobs.TryGetValue(p.Rs, out var rs)) pack.RootSignatures[p.Rs] = rs;
            pack.Entries.Add(r);
        }
        return pack;
    }

    /// <summary>A pack as a canonical hash-only recording: its root signatures as 'B' records and its PSOs (an upload's body).</summary>
    /// <summary>A pack's records as shared, without the records a layer made (<paramref name="layered"/>,
    /// <see cref="LayerMade"/>) and the root signatures only they named.</summary>
    public static List<Rec> Records(MiddlewarePack pack, IReadOnlySet<string>? layered = null) =>
        HashOnly.CheckPack(HashOnly.Canonical([.. pack.RootSignatures.Select(r => new Rec('B', [.. Convert.FromHexString(r.Key), .. r.Value])),
            .. pack.Entries.Where(e => layered?.Contains(e.Key) != true)], local: false, out _));

    string LayerFile => Path.Combine(Dir, "layer-made.keys");

    /// <summary>One pack writer at a time across processes: promotions and pruning read, change and rewrite whole packs.</summary>
    IDisposable Lock() => App.Recordings.Lock(Path.Combine(Dir, "packs"));

    /// <summary>The keys of every record a layer wrapping the device made, from every recording imported on this PC
    /// (<see cref="Exclude"/>): never promoted, never shared, whichever recording brings them. A list that is there but
    /// can't be read throws: nothing is promoted or shared without it.</summary>
    public HashSet<string> LayerMade()
    {
        string[] lines;
        try { lines = File.ReadAllLines(LayerFile); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }   // only a missing one is empty
        // written whole (a temp file moved in): any other line is damage, not a key to drop
        if (lines.FirstOrDefault(l => l.Length != 40 || !l.All(char.IsAsciiHexDigitLower)) is { } bad)
            throw new InvalidDataException($"{LayerFile}: not a record key: {bad}");
        return [.. lines];
    }

    /// <summary>Adds <paramref name="keys"/> (a recording's 'W' targets) to <see cref="LayerMade"/> and takes them out of
    /// the packs here.</summary>
    public void Exclude(IEnumerable<string> keys)
    {
        using var _ = Lock();
        var all = LayerMade();
        if (Persist(all, keys)) PruneLocked(all);
    }

    /// <summary>Under the lock: <paramref name="keys"/> added to <paramref name="all"/> (<see cref="LayerMade"/>) and the file;
    /// false = nothing new.</summary>
    bool Persist(HashSet<string> all, IEnumerable<string> keys)
    {
        var n = all.Count;
        all.UnionWith(keys);
        if (all.Count == n) return false;
        Directory.CreateDirectory(Dir);
        var tmp = $"{LayerFile}.{Guid.NewGuid():N}.tmp";
        File.WriteAllLines(tmp, all.Order(StringComparer.Ordinal));
        File.Move(tmp, LayerFile, true);
        return true;
    }

    public sealed record Promoted(MiddlewareDll Dll, string ContentHash, int Records, int New, string PackPath);

    /// <summary>Records of a recording whose stages are all containers of one detected DLL and none a game-index shader
    /// go into that DLL version's pack with their root signature (only RTS0-only blobs; a record whose root signature isn't
    /// in the recording, or is shader-bearing, is left out). Returns per DLL how many qualified and how many were new.
    /// <paramref name="gpu"/>: this GPU's vendor ("nvidia", "amd"), kept in the pack (<see cref="PackHeader.Gpu"/>), so a pack
    /// holds one vendor's pipelines: a PSO whose wave size this GPU doesn't run is left out (the recording may hold another
    /// vendor's, from a community recording merged in), another vendor's pack starts over (a GPU change), and a pack from
    /// before packs kept their GPU is adopted by this one without the PSOs it doesn't run. <paramref name="layered"/>: this
    /// recording's records a layer wrapping the device made ('W'); with every recording's (<see cref="LayerMade"/>) they are
    /// never promoted, and come out of a pack an earlier promotion put them in, with the root signatures only they named.</summary>
    public List<Promoted> Promote(IEnumerable<Rec> records, IReadOnlyDictionary<string, byte[]> blobs, IReadOnlyDictionary<string, ShaderInfo> index,
        IEnumerable<MiddlewareDll> dlls, string source, string? gpu = null, IReadOnlySet<string>? layered = null)
    {
        using var _ = Lock();
        HashSet<string> excluded;
        try { excluded = LayerMade(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }   // no promotion without the list
        Persist(excluded, layered ?? new HashSet<string>());
        PruneLocked(excluded);
        bool? amd = gpu == null ? null : gpu == "amd";
        var candidates = new List<(Rec R, Pso P)>();
        foreach (var r in records)
        {
            if (r.Tag is not ('C' or 'G' or 'S') || excluded.Contains(r.Key)) continue;
            Pso p;
            try { p = Parse(r); } catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or ArgumentOutOfRangeException) { continue; }
            if (p.Stages.Count > 0 && !p.Stages.Values.Any(index.ContainsKey)) candidates.Add((r, p));
        }
        var result = new List<Promoted>();
        if (candidates.Count == 0) return result;
        foreach (var dll in dlls.Where(d => d.Packable))
        {
            var image = Middleware.Scan(dll.Path);
            var mine = candidates.Where(c => c.P.Stages.Values.All(h => image.Containers.ContainsKey(h) && (amd is not { } a || image.Runs(h, a)))).ToList();
            if (mine.Count == 0) continue;
            var pack = Load(dll, image);
            var path = PathOf(dll.Vendor, dll.Name, image.ContentHash);
            var reset = false;
            if (gpu != null && pack != null && pack.Header.Gpu != gpu)
            {
                if (pack.Header.Gpu == null)
                {
                    pack.Entries.RemoveAll(e => !Parse(e).Stages.Values.All(h => image.Runs(h, amd!.Value)));
                    pack.Adopt(gpu);
                }
                else pack = null;
                reset = true;
            }
            pack ??= new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size, gpu: gpu);
            var added = 0;
            foreach (var (r, p) in mine)
            {
                if (p.Rs != Zero && !image.Containers.ContainsKey(p.Rs))
                {
                    if (!blobs.TryGetValue(p.Rs, out var rs) || !Dxbc.IsRootSignatureOnly(rs)) continue;
                    pack.RootSignatures[p.Rs] = rs;
                }
                if (pack.Add(r, source)) added++;
            }
            if (added > 0 || reset) pack.Write(path);
            result.Add(new Promoted(dll, image.ContentHash, mine.Count, added, path));
        }
        return result;
    }

    /// <summary>Takes the records a layer made (<paramref name="layered"/>, by key) out of every pack here that has them,
    /// with the root signatures only they named: packs are shared, and an earlier promotion may have put them in before
    /// their 'W' was known. Returns how many packs changed.</summary>
    public int Prune(IReadOnlySet<string> layered)
    {
        using var _ = Lock();
        return PruneLocked(layered);
    }

    int PruneLocked(IReadOnlySet<string> layered)
    {
        if (layered.Count == 0 || !Directory.Exists(Dir)) return 0;
        var changed = 0;
        foreach (var path in Directory.EnumerateFiles(Dir, "*.pack", SearchOption.AllDirectories).ToList())
        {
            MiddlewarePack pack;
            try { pack = MiddlewarePack.Read(path); }
            catch (Exception e) when (e is InvalidDataException or IOException or JsonException or UnauthorizedAccessException) { continue; }
            if (pack.Entries.RemoveAll(e => layered.Contains(e.Key)) == 0) continue;
            var named = pack.Entries.Select(e => Parse(e).Rs).ToHashSet();
            foreach (var h in pack.RootSignatures.Keys.Where(h => !named.Contains(h)).ToList()) pack.RootSignatures.Remove(h);
            pack.Write(path);
            changed++;
        }
        return changed;
    }

    public sealed record Seeded(MiddlewareDll Dll, string ContentHash, MiddlewarePack Pack);

    /// <summary>The packs of the detected DLL versions (same content hash) that have any, and the <see cref="Fingerprint"/>
    /// of what was read.</summary>
    public List<Seeded> Seed(IEnumerable<MiddlewareDll> dlls, out string fingerprint)
    {
        var list = new List<Seeded>();
        var parts = new List<string>();
        foreach (var dll in dlls.Where(d => d.Packable))
        {
            if (!Directory.Exists(Path.Combine(Dir, dll.Vendor))) continue; // no pack of this vendor: don't hash the DLL
            var image = Middleware.Scan(dll.Path);
            parts.Add(Part(dll, image.ContentHash));   // before the read: a pack changed after it mismatches the next time
            if (Load(dll, image) is { Entries.Count: > 0 } pack) list.Add(new Seeded(dll, image.ContentHash, pack));
        }
        fingerprint = string.Join('|', parts);
        return list;
    }

    /// <summary>What decides a plan's pack items: the detected DLL versions and their packs' sizes (a changed DLL or a
    /// grown pack means the plan could seed more; a caller may re-plan when it changes).</summary>
    public string Fingerprint(Game game) => string.Join('|', Middleware.Detect(game).Where(d => d.Packable && Directory.Exists(Path.Combine(Dir, d.Vendor))) // no pack of its vendor: can't seed, not hashed
        .Select(d => Part(d, Middleware.Scan(d.Path).ContentHash)));

    string Part(MiddlewareDll d, string hash)
    {
        var p = PathOf(d.Vendor, d.Name, hash);
        // a shared pack is replaced whole: another object of the same length is other PSOs
        return $"{d.Name}:{hash}:{(File.Exists(p) ? new FileInfo(p).Length : 0)}" + (File.Exists(p) && MiddlewarePack.ReadHeader(p)?.Object is { } obj ? ":" + obj : "");
    }

    // plan body: 'M' = a pack entry: u8 inner tag, DLL content sha1[20], u8 name length, name (UTF-8), inner payload

    public static Rec Wrap(Rec entry, string dll, string contentHash)
    {
        var name = Encoding.UTF8.GetBytes(dll);
        return new Rec('M', [(byte)entry.Tag, .. Convert.FromHexString(contentHash), (byte)name.Length, .. name, .. entry.Payload]);
    }

    public static (string Dll, string ContentHash, Rec Entry) Unwrap(Rec m)
    {
        var p = m.Payload;
        var n = p[21];
        return (Encoding.UTF8.GetString(p, 22, n), Hex(p.AsSpan(1, 20)), new Rec((char)p[0], p[(22 + n)..]));
    }

    /// <param name="FilledFromDll">of <paramref name="AlreadyRecorded"/>, entries whose record scskiller.db has but not all
    /// its blobs (a rehydrated hash-only recording whose DLL shaders the install reader couldn't give): written from the DLL</param>
    /// <param name="DropFromMain">keys of entries scskiller.db has without their blobs that the DLL can't give either:
    /// counted as skipped; the caller removes them from its work copy so they aren't replayed as failures</param>
    public sealed record MaterializeResult(int Entries, int Written, int AlreadyRecorded, int Skipped, IReadOnlyDictionary<string, int> SkippedByDll,
        int FilledFromDll = 0, IReadOnlySet<string>? DropFromMain = null);

    /// <summary>Writes the plan's pack entries ('M') into gen.db: each entry's shader containers read from this install's
    /// copy of its DLL (read-only), then the entry. An entry is skipped when that copy doesn't hold one of its shaders (or
    /// its root signature: neither in the plan nor in the DLL), or when the DLL isn't there. One already in scskiller.db
    /// is left to it when every blob it uses is there too; otherwise its missing blobs come from the DLL (the record stays
    /// in scskiller.db: the warm loads both files' blobs first), or, when the DLL lacks them too, it's skipped and listed in
    /// <see cref="MaterializeResult.DropFromMain"/>. <paramref name="written"/>: blob hashes gen.db / scskiller.db already
    /// hold (updated).</summary>
    public static MaterializeResult Materialize(IEnumerable<Rec> mRecords, Game game, IReadOnlySet<string> planBlobs, ISet<string> written,
        IReadOnlySet<string> inMain, Stream gen)
    {
        var dlls = Middleware.Detect(game);
        var images = new Dictionary<string, MiddlewareImage?>(StringComparer.OrdinalIgnoreCase);
        int entries = 0, done = 0, recorded = 0, filled = 0;
        var skipped = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var drop = new HashSet<string>();
        var pending = new List<Rec>();
        foreach (var m in mRecords)
        {
            entries++;
            var (dll, _, e) = Unwrap(m);
            var recordedHere = inMain.Contains(e.Key);
            var pso = Parse(e);
            var need = pso.Stages.Values.Append(pso.Rs).Where(h => h != Zero && !written.Contains(h) && !planBlobs.Contains(h)).Distinct().ToList();
            if (recordedHere && need.Count == 0) { recorded++; continue; }
            if (!images.TryGetValue(dll, out var image))
                images[dll] = image = dlls.FirstOrDefault(d => d.Packable && d.Name.Equals(dll, StringComparison.OrdinalIgnoreCase)) is { } d ? Middleware.TryScan(d.Path) : null;
            var bytes = new List<(string, byte[])>();
            foreach (var h in need)
                if (image?.Read(h) is { } b) bytes.Add((h, b));
                else { bytes = null; break; }
            if (bytes == null)
            {
                skipped[dll] = skipped.GetValueOrDefault(dll) + 1;
                if (recordedHere) drop.Add(e.Key);
                continue;
            }
            foreach (var (h, b) in bytes) if (written.Add(h)) WriteBlob(gen, h, b);
            if (recordedHere) { recorded++; filled++; continue; }
            pending.Add(e);
            done++;
        }
        foreach (var e in pending) PsoDb.Write(gen, e.Tag, e.Payload); // after every blob, like templates
        return new MaterializeResult(entries, done, recorded, skipped.Values.Sum(), skipped, filled, drop);
    }
}
