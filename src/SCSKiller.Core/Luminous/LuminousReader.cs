using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Luminous;

/// <summary>Square Enix's Luminous Studio on PC (FINAL FANTASY XV WINDOWS EDITION), DirectX 11 only: the exe loads d3d11.dll and
/// names no D3D12, and every shader is SM 5.0 DXBC. The shaders are the .sb entries of datas\**\*.earc (<see cref="Earc"/>), each
/// one raw container. Level, character and effects archives carry copies of shader\shadergen's and shaders of their own
/// (measured: 40,663 distinct in 1,600 archives, 16,299 of them outside shadergen), so every archive is read. The archives hold
/// vertex and pixel shaders only: the compute, geometry and tessellation shaders are raw containers in the exe and the DLLs
/// beside it (GameWorks, DLSS) and HairWorks\*.bin, carved here since the carver doesn't run once this reader detects the game.
/// One map per archive or binary (platform <see cref="Platform"/>), so a binary's hull and domain shaders pair.
/// EngineInfo: Family "Luminous", Version "EARC 3.20", GraphicsApi "D3D11".</summary>
public sealed class LuminousReader : IEngineReader
{
    public const string Family = "Luminous", Version = "EARC 3.20", Platform = "PCD3D_SM5";
    public const string Exe = "ffxv_s.exe";
    const string Datas = "datas", Shadergen = @"datas\shader\shadergen\autoexternal.earc", Suffix = ".sb";

    /// <summary>Bytes carved from a binary per read, and how far reads overlap: a container is found whole when it's at most
    /// <see cref="Overlap"/> long (measured: the exe is 253 MB, the largest container in it and the DLLs 329 KB).</summary>
    internal int Window { get; init; } = 32 << 20;
    internal int Overlap { get; init; } = 4 << 20;

    /// <param name="Entry">the archive entry, null for a container carved from a binary at Offset</param>
    public sealed record Loc(string Path, Earc.Entry? Entry, long Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public EngineInfo? Detect(Game game)
    {
        var shadergen = Path.Combine(game.InstallDir, Shadergen);
        if (Earc.VersionOf(shadergen) is not { } v) return null;
        // the API is FINAL FANTASY XV's: another Luminous game (Forspoken runs on D3D12) isn't read as one
        var unsupported = v != Earc.Version ? "an archive version SCSKiller doesn't read"
            : !File.Exists(Path.Combine(game.InstallDir, Exe)) ? "a Luminous game SCSKiller doesn't read" : null;
        return new EngineInfo(Family, $"EARC {v >> 16}.{v & 0xFFFF}", null, "D3D11", false, unsupported);
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var maps = new List<ShaderMap>();
        var stamp = new StringBuilder();
        int archives = 0, entries = 0, other = 0, unreadable = 0, bad = 0;

        void Add(List<string> map, byte[] c, Loc at)
        {
            var sha = Convert.ToHexStringLower(SHA1.HashData(c));
            if (locs.TryAdd(sha, at))
                try { if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) }; }
                catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; } // valid container, odd program: not usable
            if (shaders.ContainsKey(sha)) map.Add(sha);
        }
        void Map(string rel, List<string> map)
        {
            if (map.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex(rel), rel, Platform, map.Distinct().ToList()));
        }
        void Stamp(FileInfo f, string rel) => stamp.Append($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n");
        void Unreadable(string what, Exception x)
        {
            if (unreadable++ == 0) log?.Report($"warning: {what}: {x.Message}");
        }
        // an install being patched or removed loses files mid-listing: what was listed is still read
        List<FileInfo> Listed(Func<IEnumerable<FileInfo>> files, string what)
        {
            var list = new List<FileInfo>();
            try { foreach (var f in files()) list.Add(f); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { Unreadable(what, x); }
            return list;
        }

        foreach (var f in Listed(() => Archives(game), Datas))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            var map = new List<string>();
            try
            {
                Stamp(f, rel);
                using var h = File.OpenHandle(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (Earc.Entries(h, Suffix) is not { } list) { other++; continue; }
                archives++;
                foreach (var e in list)
                {
                    var c = Earc.Read(h, e);
                    if (!Dxbc.Valid(c)) continue;   // a .sb that isn't one container
                    entries++;
                    Add(map, c, new Loc(f.FullName, e, e.Offset, e.Size));
                }
            }
            catch (Exception x) when (x is InvalidDataException or IOException or UnauthorizedAccessException) { Unreadable(rel, x); }
            Map(rel, map);
        }

        var carved = 0;
        foreach (var f in Listed(() => Binaries(game), "the exe and DLLs"))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            var map = new List<string>();
            try
            {
                Stamp(f, rel);
                Carve(f.FullName, (at, c) => { carved++; Add(map, c, new Loc(f.FullName, null, at, c.Length)); }, ct);
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { Unreadable(rel, x); }
            Map(rel, map);
        }

        log?.Report($"{archives} archives, {entries} shader entries, {carved} containers in the exe and DLLs -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {maps.Count} maps{(other > 0 ? $"; {other} archives of another version left out" : "")}"
            + $"{(unreadable > 0 ? $"; {unreadable} files that don't read left out" : "")}{(bad > 0 ? $", {bad} unparseable" : "")} ({sw.Elapsed.TotalSeconds:F1}s)");
        if (shaders.Count == 0) throw new InvalidDataException($"no DirectX 11 shader in {Datas}");
        located[game.Id] = locs;
        return new ShaderIndex(CarvedReader.Sha1Hex(stamp.ToString()), [Platform], shaders, maps);
    }

    /// <summary>Reads each container where the index found it; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Path))
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle h;
            try { h = File.OpenHandle(file.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; } // gone since the index
            using var _ = h;
            foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
            {
                ct.ThrowIfCancellationRequested();
                byte[] b;
                try
                {
                    if (at.Entry != null) b = Earc.Read(h, at.Entry);
                    else
                    {
                        b = new byte[at.Size];
                        if (RandomAccess.Read(h, b, at.Offset) != b.Length) continue;
                    }
                }
                catch (Exception x) when (x is InvalidDataException or IOException or UnauthorizedAccessException) { continue; } // changed or gone since the index
                if (Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
            }
        }
    }

    static IEnumerable<FileInfo> Archives(Game game)
    {
        var dir = new DirectoryInfo(Path.Combine(game.InstallDir, Datas));
        return !dir.Exists ? []
            : dir.EnumerateFiles("*.earc", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The exe and DLLs beside it, and HairWorks' shader cache.</summary>
    static IEnumerable<FileInfo> Binaries(Game game)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var root = new DirectoryInfo(game.InstallDir);
        var hair = new DirectoryInfo(Path.Combine(game.InstallDir, "HairWorks"));
        return root.EnumerateFiles("*.exe", opts).Concat(root.EnumerateFiles("*.dll", opts))
            .Concat(hair.Exists ? hair.EnumerateFiles("*.bin", opts) : []).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every valid container in the file with its offset, read <see cref="Window"/> bytes at a time. One starting in a
    /// window's overlap is taken from the next window, which holds it whole.</summary>
    internal void Carve(string path, Action<long, byte[]> found, CancellationToken ct)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = RandomAccess.GetLength(h);
        var buf = new byte[(int)Math.Min(Window, length)];
        for (long pos = 0; pos < length; pos += Window - Overlap)
        {
            ct.ThrowIfCancellationRequested();
            var n = RandomAccess.Read(h, buf, pos);
            var b = n == buf.Length ? buf : buf[..n];
            var last = pos + n >= length;
            foreach (var (at, c) in Dxbc.Containers(b))
                if (last || at < Window - Overlap) found(pos + at, c);
            if (last) break;
        }
    }
}
