using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;
using ValvePak;
using ZstdSharp;

namespace SCSKiller.Core.Source2;

/// <summary>Valve's Source 2 (Counter-Strike 2, Deadlock, Dota 2): game\bin\win64\engine2.dll, and the compiled VFX programs
/// (*.vcs) in each game\&lt;mod&gt;\shaders_pc_dir.vpk. PC .vcs files are DirectX 11 only (SM 5.0 DXBC); Source 2 on Windows
/// has no DirectX 12 renderer, so the plan is every D3D11 shader once and there are no root signatures. A version 70-72
/// .vcs is a resource file (u32 its size, u16 header version, u16 vcs version, then KV3 blocks) followed by its bytecode
/// blocks, back to back to the end of the file: i32 -kind, i32 uncompressed size, i32 compressed size, then a zstd frame of
/// a dictionary the engine carries (<see cref="Dictionaries"/>). A block holds the DXBC containers of one static combo.
/// Layout as ValveResourceFormat documents it. One map per VFX program (platform PCD3D_SM5).
/// GraphicsApi: the Steam launch options, else DefaultRenderSystemOption in a mod's cfg\boot.vcfg, else D3D11.
/// EngineInfo: Family "Source 2", Version the vcs version.</summary>
public sealed class Source2Reader : IEngineReader
{
    public const string Family = "Source 2", Platform = "PCD3D_SM5";
    const string Vpk = "shaders_pc_dir.vpk";
    const int MinVersion = 70, MaxVersion = 72, ResourceHeaderVersion = 12;
    const int MaxBlock = 256 << 20;

    static string Bin(Game game) => Path.Combine(game.InstallDir, "game", "bin", "win64");

    public EngineInfo? Detect(Game game)
    {
        if (!File.Exists(Path.Combine(Bin(game), "engine2.dll"))) return null;
        var api = Api(game, UnrealRhi.LaunchOptions(game));
        if (Vpks(game).FirstOrDefault() is not { } vpk) return new EngineInfo(Family, "", null, api, false, $"no {Vpk}");
        using var pak = Open(vpk);
        var version = Programs(pak).Select(e => Version(Read(pak, e))).FirstOrDefault(v => v != null);
        return new EngineInfo(Family, version?.ToString() ?? "", null, api, false,
            version is >= MinVersion and <= MaxVersion ? null : $"shader files {(version == null ? "of an older layout" : $"version {version}")}: SCSKiller reads versions {MinVersion} to {MaxVersion}");
    }

    public string DetectStamp(Game game, EngineInfo? engine) =>
        UnrealRhi.LaunchOptions(game) + "|" + string.Join(';', BootConfigs(game).Select(f => new FileInfo(f)).Select(f => $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"));

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var maps = new Dictionary<string, List<string>>();
        var stamp = new StringBuilder();
        int files = 0, blocks = 0, other = 0, bad = 0, unreadable = 0;
        var dicts = new Dictionaries(Bin(game));
        foreach (var vpk in Vpks(game))
        {
            var f = new FileInfo(vpk);
            var rel = Path.GetRelativePath(game.InstallDir, vpk);
            stamp.Append($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n");
            using var pak = Open(vpk);
            foreach (var e in Programs(pak))
            {
                ct.ThrowIfCancellationRequested();
                var b = Read(pak, e);
                if (Version(b) is not (>= MinVersion and <= MaxVersion)) { other++; continue; }
                files++;
                var shas = maps.TryGetValue($"{rel}:{Program(e)}", out var m) ? m : maps[$"{rel}:{Program(e)}"] = [];
                foreach (var block in Readable(b, dicts, x => { if (unreadable++ == 0) log?.Report($"warning: {e.GetFullPath()}: {x.Message}"); }))
                {
                    blocks++;
                    foreach (var (_, c) in Dxbc.Containers(block))
                    {
                        var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                        try
                        {
                            if (!shaders.ContainsKey(sha) && ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                                shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                        }
                        catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { bad++; } // valid container, odd program: not usable
                        if (shaders.ContainsKey(sha)) shas.Add(sha);
                    }
                }
            }
        }
        if (dicts.Missing.Count > 0)
            throw new InvalidDataException($"no zstd dictionary {string.Join(", ", dicts.Missing.Select(d => d.ToString("x8")))} in game\\bin\\win64\\{Dictionaries.Dll}");
        var list = maps.Where(m => m.Value.Count > 0).Select(m => new ShaderMap(CarvedReader.Sha1Hex(m.Key), m.Key, Platform, m.Value.Distinct().ToList())).ToList();
        log?.Report($"{files} programs, {blocks} static combos -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {list.Count} maps{(other > 0 ? $"; {other} files of another version left out" : "")}{(unreadable > 0 ? $"; the rest of {unreadable} files that don't read whole left out" : "")}"
            + $"{(bad > 0 ? $", {bad} unparseable" : "")} ({sw.Elapsed.TotalSeconds:F1}s)");
        if (shaders.Count == 0) throw new InvalidDataException($"no DirectX 11 shader in {Vpk}");
        return new ShaderIndex(CarvedReader.Sha1Hex(stamp.ToString()), [Platform], shaders, list);
    }

    /// <summary>Reads the programs again and serves each requested shader once.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        var left = new HashSet<string>(sha1s);
        var dicts = new Dictionaries(Bin(game));
        foreach (var vpk in Vpks(game))
        {
            using var pak = Open(vpk);
            foreach (var e in Programs(pak))
            {
                ct.ThrowIfCancellationRequested();
                var b = Read(pak, e);
                if (Version(b) is not (>= MinVersion and <= MaxVersion)) continue;
                foreach (var block in Readable(b, dicts, _ => { }))
                    foreach (var (_, c) in Dxbc.Containers(block))
                        if (Convert.ToHexStringLower(SHA1.HashData(c)) is var sha && left.Remove(sha))
                        {
                            sink(sha, c);
                            if (left.Count == 0) return;
                        }
            }
        }
    }

    /// <summary>Each mod's shader package, the game's own mods before core.</summary>
    static IEnumerable<string> Vpks(Game game)
    {
        var dir = Path.Combine(game.InstallDir, "game");
        return !Directory.Exists(dir) ? []
            : Directory.EnumerateDirectories(dir).Select(d => Path.Combine(d, Vpk)).Where(File.Exists)
                .OrderBy(p => Path.GetFileName(Path.GetDirectoryName(p))!.Equals("core", StringComparison.OrdinalIgnoreCase)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    static Package Open(string path)
    {
        var pak = new Package();
        try { pak.Read(path); }
        catch { pak.Dispose(); throw; }
        return pak;
    }

    /// <summary>The package's .vcs files; the _features files hold no bytecode.</summary>
    static IEnumerable<PackageEntry> Programs(Package pak) =>
        (pak.Entries?.GetValueOrDefault("vcs") ?? []).Where(e => !e.FileName.EndsWith("_features", StringComparison.OrdinalIgnoreCase));

    /// <summary>The VFX program a .vcs belongs to: its path up to the platform ("shaders/vfx/foo" of foo_pc_50_ps).</summary>
    static string Program(PackageEntry e) => $"{e.DirectoryName}/{Regex.Replace(e.FileName, "_pc_[0-9]+_[a-z]+$", "", RegexOptions.IgnoreCase)}";

    static byte[] Read(Package pak, PackageEntry e)
    {
        pak.ReadEntry(e, out var b);
        return b;
    }

    /// <summary>The vcs version of a resource-format .vcs, null for any other file.</summary>
    internal static int? Version(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(b) is var size && size <= b.Length
            && BinaryPrimitives.ReadUInt16LittleEndian(b[4..]) == ResourceHeaderVersion ? BinaryPrimitives.ReadUInt16LittleEndian(b[6..]) : null;

    /// <summary><see cref="Blocks"/> up to the first that doesn't read, which goes to <paramref name="error"/>. One at a time:
    /// a program's blocks can decompress to gigabytes (Counter-Strike 2's csgo_character PS: 3.1 GB).</summary>
    static IEnumerable<byte[]> Readable(byte[] b, Dictionaries dicts, Action<Exception> error)
    {
        using var blocks = Blocks(b, dicts).GetEnumerator();
        while (true)
        {
            bool more;
            try { more = blocks.MoveNext(); }
            catch (Exception x) when (x is InvalidDataException or ZstdException) { error(x); more = false; }
            if (!more) yield break;
            yield return blocks.Current;
        }
    }

    /// <summary>The decompressed bytecode blocks after the resource.</summary>
    internal static IEnumerable<byte[]> Blocks(byte[] b, Dictionaries dicts)
    {
        for (var at = (long)BinaryPrimitives.ReadUInt32LittleEndian(b); at < b.Length;)
        {
            if (at + 12 > b.Length) throw new InvalidDataException("bytecode block header past the end of the file");
            int kind = -BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)at)), size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)at + 4)),
                packed = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)at + 8));
            if (size is < 0 or > MaxBlock || packed < 0 || at + 12 + packed > b.Length) throw new InvalidDataException($"bytecode block at {at}: {packed} bytes");
            var frame = b.AsSpan((int)at + 12, packed);
            at += 12 + packed;
            // 3 and 5: zstd with a dictionary (the frame names it); 1 stored, 2 zstd without, 4 LZ4: not seen in PC files
            if (kind is not (3 or 5)) throw new InvalidDataException($"bytecode block of kind {kind}");
            if (dicts.For(frame) is not { } z) continue;
            var block = new byte[size];
            if (z.Unwrap(frame, block) != size) throw new InvalidDataException("bytecode block doesn't decode to its size");
            yield return block;
        }
    }

    /// <summary>The zstd dictionaries of the game's bytecode, read from its own materialsystem2.dll by the dictionary ID each
    /// frame names: the dictionary's magic and ID, then 64 KiB (V_ZSTD_createDDict_byReference's length).</summary>
    internal sealed class Dictionaries(string binDir)
    {
        public const string Dll = "materialsystem2.dll";
        const int Size = 1 << 16;
        readonly Dictionary<uint, Decompressor?> byId = [];
        byte[]? dll;
        public HashSet<uint> Missing { get; } = [];

        /// <summary>A decompressor with the frame's dictionary; null (and the ID in <see cref="Missing"/>) when the DLL has none.</summary>
        public Decompressor? For(ReadOnlySpan<byte> frame)
        {
            var id = DictionaryId(frame);
            if (byId.TryGetValue(id, out var z)) return z;
            dll ??= File.ReadAllBytes(Path.Combine(binDir, Dll));
            Span<byte> head = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(head, 0xEC30A437);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], id);
            var at = dll.AsSpan().IndexOf(head);
            if (at < 0 || at + Size > dll.Length) { Missing.Add(id); return byId[id] = null; }
            z = new Decompressor();
            z.LoadDictionary(dll.AsSpan(at, Size));
            return byId[id] = z;
        }

        /// <summary>The Dictionary_ID of a zstd frame header (RFC 8878 3.1.1.1); 0 = none.</summary>
        internal static uint DictionaryId(ReadOnlySpan<byte> f)
        {
            if (f.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(f) != 0xFD2FB528) throw new InvalidDataException("bytecode block isn't a zstd frame");
            var fhd = f[4];
            var at = 5 + ((fhd & 0x20) == 0 ? 1 : 0);   // a window descriptor unless single segment
            var n = (fhd & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
            if (f.Length < at + n) throw new InvalidDataException("bytecode block's zstd header is cut short");
            return n switch { 0 => 0, 1 => f[at], 2 => BinaryPrimitives.ReadUInt16LittleEndian(f[at..]), _ => BinaryPrimitives.ReadUInt32LittleEndian(f[at..]) };
        }
    }

    /// <summary>The renderer: -vulkan / -dx11 in the Steam launch options, else DefaultRenderSystemOption in a mod's
    /// cfg\boot.vcfg (Deadlock's launcher can set -vulkan there), else D3D11.</summary>
    internal static string Api(Game game, string launch)
    {
        static string Of(string flag) => flag.Equals("vulkan", StringComparison.OrdinalIgnoreCase) ? "Vulkan" : "D3D11";
        if (Regex.Match(launch, @"(?:^|\s)-(dx11|vulkan)\b", RegexOptions.IgnoreCase) is { Success: true } cmd) return $"{Of(cmd.Groups[1].Value)} (launch option)";
        foreach (var f in BootConfigs(game))
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (Regex.Match(text, @"""DefaultRenderSystemOption""\s+""\s*-(dx11|vulkan)\b", RegexOptions.IgnoreCase) is { Success: true } m)
                return $"{Of(m.Groups[1].Value)} (boot.vcfg)";
        }
        return "D3D11";
    }

    /// <summary>Each mod's cfg\boot.vcfg that exists, in <see cref="Vpks"/>' order.</summary>
    static IEnumerable<string> BootConfigs(Game game)
    {
        var dir = Path.Combine(game.InstallDir, "game");
        return !Directory.Exists(dir) ? []
            : Directory.EnumerateDirectories(dir).Select(d => Path.Combine(d, "cfg", "boot.vcfg")).Where(File.Exists)
                .OrderBy(p => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(p)))!.Equals("core", StringComparison.OrdinalIgnoreCase)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
    }
}
