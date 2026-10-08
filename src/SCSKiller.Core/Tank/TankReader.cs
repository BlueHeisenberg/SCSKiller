using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Tank;

/// <summary>Blizzard's Tank engine (Overwatch) in a static TACT container (<see cref="StaticContainer"/>; the Steam
/// install). Its shaders are ShaderCode assets: 8 bytes 0xFF, the asset's header, then a gzip member holding one
/// container (<see cref="Containers"/>). Found by walking every blob, since the root manifest that names assets is
/// encrypted per build: build 154088 (2.25), 1,050,069 blobs, 321,738 ShaderCode assets, 99,504 distinct DirectX 11
/// shaders (SM 5.0: 88,209 PS, 11,244 VS, 50 CS, 1 GS, no tessellation) and 218,013 DXIL ones (SM 6, DirectX 12). No
/// shader carries a root signature and the engine builds its own at run time, which only a recording would show, and a
/// game with anti-cheat is never recorded: the DXIL shaders aren't indexed and DirectX 12 can't be planned. The DirectX 11
/// shaders are one pool per data file (<see cref="Platform"/>). GraphicsApi: the GraphicsAPI line of the game's
/// Documents\Overwatch\Settings\Settings_v0.ini ("Dx11", "Dx12"; none: "D3D11 or D3D12").
/// EngineInfo: Family "Tank", Version the build's branch ("2.25.0.0").</summary>
public sealed class TankReader(string? documents = null) : IEngineReader
{
    public const string Family = "Tank", Platform = "PCD3D_SM5", Product = "Prometheus";

    /// <summary>A ShaderCode asset's first bytes.</summary>
    static readonly byte[] Magic = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Data files read at once: a few, since the work is mostly decompressing one big file's shaders.</summary>
    const int Readers = 4;

    public sealed record Loc(string Path, long Offset);

    /// <summary>Where each shader's blob is, per game id, from the last Index. ponytail: in memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public EngineInfo? Detect(Game game)
    {
        var config = StaticContainer.ReadBuildConfig(game.InstallDir);
        if (config.GetValueOrDefault("build-product") != Product || !StaticContainer.Files(game.InstallDir).Any()) return null;
        var version = config.GetValueOrDefault("build-branch")?.Replace('_', '.') is { Length: > 0 } b ? b : config.GetValueOrDefault("build-number") ?? "-";
        return new EngineInfo(Family, version, null, Api(), false, null);
    }

    /// <summary>The settings file the API comes from: Detect is redone when the game rewrites it.</summary>
    public string DetectStamp(Game game, EngineInfo? engine) =>
        new FileInfo(SettingsFile) is { Exists: true } f ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-";

    string SettingsFile => Path.Combine(documents ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Overwatch", "Settings", "Settings_v0.ini");

    string Api()
    {
        string text;
        try { text = File.ReadAllText(SettingsFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return UnrealRhi.Ambiguous; }
        return Regex.Match(text, @"^\s*GraphicsAPI\s*=\s*""?(\w+)""?", RegexOptions.Multiline).Groups[1].Value.ToLowerInvariant() switch
        {
            "dx11" => "D3D11",
            "dx12" => "D3D12",
            _ => UnrealRhi.Ambiguous,
        };
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var files = StaticContainer.Files(game.InstallDir).ToList();
        var shaders = new ConcurrentDictionary<string, ShaderInfo>();
        var locs = new ConcurrentDictionary<string, Loc>();
        var perFile = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        long blobs = 0, assets = 0, dxil = 0, bad = 0;
        var stops = new ConcurrentBag<string>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Readers, CancellationToken = ct }, file =>
        {
            var mine = perFile.GetOrAdd(file.Name, _ => new());
            Walk(file.FullName, ct, at => stops.Add($"{file.Name} at {at}"), () => Interlocked.Increment(ref blobs), (offset, containers) =>
            {
                Interlocked.Increment(ref assets);
                foreach (var c in containers)
                {
                    if (!Dxbc.Part(c, "DXIL"u8).IsEmpty) { Interlocked.Increment(ref dxil); continue; }
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    if (!locs.TryAdd(sha, new Loc(file.FullName, offset))) { if (shaders.ContainsKey(sha)) mine.TryAdd(sha, 0); continue; }
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info && Planning.Planner.IsD3D11(info))
                        {
                            shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings), RootSignature = null };
                            mine.TryAdd(sha, 0);
                        }
                    }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { Interlocked.Increment(ref bad); } // valid container, odd program: not usable
                }
            });
        });
        var maps = perFile.Where(p => !p.Value.IsEmpty).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new ShaderMap(CarvedReader.Sha1Hex($"{StaticContainer.Dir}\\{p.Key}|{Platform}"), $"{StaticContainer.Dir}\\{p.Key}", Platform,
                p.Value.Keys.Where(shaders.ContainsKey).Order(StringComparer.Ordinal).ToList())).ToList();
        if (maps.Count == 0) throw new InvalidDataException($"no DirectX 11 shader in {StaticContainer.Dir}\\ ({blobs} blobs, {assets} shader assets): the storage layout may have changed");
        located[game.Id] = new Dictionary<string, Loc>(locs.Where(l => shaders.ContainsKey(l.Key)));
        log?.Report($"{shaders.Count} DirectX 11 shaders ({string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))}) "
            + $"in {assets} shader assets of {blobs} blobs; {dxil} DirectX 12 (DXIL) shaders not indexed: the game builds their root signatures at run time"
            + (bad > 0 ? $"; {bad} unparseable" : "") + (stops.IsEmpty ? "" : $"; warning: stopped reading {string.Join(", ", stops.Order())}: not a blob there")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(ContentHash(game, files), [Platform], new Dictionary<string, ShaderInfo>(shaders), maps);
    }

    /// <summary>The build's identity (build config) and its data files' names, sizes and write times.</summary>
    static string ContentHash(Game game, IEnumerable<FileInfo> files)
    {
        var sb = new StringBuilder();
        try { sb.Append(File.ReadAllText(Path.Combine(game.InstallDir, StaticContainer.Dir, StaticContainer.BuildConfig))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var f in files) sb.Append($"\n{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");
        return CarvedReader.Sha1Hex(sb.ToString());
    }

    /// <summary>Decodes the blob each shader was found in again; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        var left = new HashSet<string>(sha1s);
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => locs[s]).Distinct().GroupBy(l => l.Path))
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle h;
            try { h = StaticContainer.Open(file.Key); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; } // gone since the index
            using var _ = h;
            var length = RandomAccess.GetLength(h);
            foreach (var at in file.Select(l => l.Offset).Order())
            {
                ct.ThrowIfCancellationRequested();
                if (StaticContainer.BlobAt(h, at, length) is not { } blob || StaticContainer.Decode(h, blob) is not { } asset) continue;
                foreach (var c in Containers(asset))
                    if (Convert.ToHexStringLower(SHA1.HashData(c)) is var sha && left.Remove(sha)) sink(sha, c);
            }
        }
    }

    /// <summary>Every ShaderCode asset of one data file: its blob's offset and the containers in it.</summary>
    internal static void Walk(string path, CancellationToken ct, Action<long> stopped, Action counted, Action<long, List<byte[]>> asset)
    {
        using var h = StaticContainer.Open(path);
        var length = RandomAccess.GetLength(h);
        Span<byte> head = stackalloc byte[8];
        var n = 0;
        foreach (var blob in StaticContainer.Blobs(h, length, stopped))
        {
            if (++n % 4096 == 0) ct.ThrowIfCancellationRequested();
            counted();
            if (StaticContainer.Peek(h, blob, head) < 8 || !head.SequenceEqual(Magic)) continue;
            if (StaticContainer.Decode(h, blob) is { } bytes && Containers(bytes) is { Count: > 0 } containers) asset(blob.Offset, containers);
        }
    }

    /// <summary>The containers of a ShaderCode asset: in the gzip member after its header (the first one that inflates);
    /// DirectX 11 ones start the member, DirectX 12 ones follow a 56-byte header of their own. Empty when it isn't one.</summary>
    internal static List<byte[]> Containers(byte[] asset)
    {
        if (asset.Length < 16 || !asset.AsSpan(0, 8).SequenceEqual(Magic)) return [];
        ReadOnlySpan<byte> gzip = [0x1F, 0x8B, 0x08];
        for (int i = 8, tries = 0; tries < 4 && asset.AsSpan(i).IndexOf(gzip) is var k and >= 0; i += k + 1, tries++)
        {
            var payload = new MemoryStream();
            try
            {
                using var z = new GZipStream(new MemoryStream(asset, i + k, asset.Length - i - k), CompressionMode.Decompress);
                var buf = new byte[1 << 16];
                for (int got; (got = z.Read(buf)) > 0;)
                {
                    payload.Write(buf, 0, got);
                    if (payload.Length > StaticContainer.MaxAsset) break;
                }
            }
            catch (InvalidDataException) { continue; }   // header bytes that only look like a gzip member
            return Dxbc.Containers(payload.ToArray()).Select(c => c.Container).ToList();
        }
        return [];
    }
}
