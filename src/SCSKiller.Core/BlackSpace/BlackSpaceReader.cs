using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.BlackSpace;

/// <summary>BlackSpace Engine (Pearl Abyss; Crimson Desert). Its files are archive groups, numbered folders of .paz archives
/// each with an index (<see cref="Pamt"/>), listed by meta\0.papgt. One group's index holds the folder
/// <see cref="ShaderFolder"/>: every shader the game ships, a .padxil file each (Crimson Desert, October 2026: 77,247 files,
/// 2.4 GB, group 0017, 97% LZ4-compressed, the rest stored; 30,510 distinct containers, the others the same bytes under
/// another permutation's name; indexed in 14 s). A .padxil is a "PASC" header (<see cref="Unwrap"/>), then the
/// DXBC container: DXIL with ISG1/OSG1/PSV0, no RTS0. The engine creates its root signatures in code, so a plan needs a
/// recording. A file name is seven '_'-separated hex fields; the third is the stage (0 VS, 1 HS, 2 DS, 3 GS, 4 PS, 5 CS,
/// 6 library, 7 MS, 8 AS), the first the effect (239 values) a VS and its PS share. Maps follow <see cref="MapKey"/>,
/// the ray tracing libraries are one pool. The .pars files beside them (one per render pass) aren't read.
/// EngineInfo: Family "BlackSpace", Version "DX12".</summary>
public sealed class BlackSpaceReader : IEngineReader
{
    public const string Family = "BlackSpace", Version = "DX12";
    public const string ShaderFolder = "shadercache__/d3d12/windows", Libraries = "libraries";
    const string Extension = ".padxil", GroupList = @"meta\0.papgt";

    /// <summary>A .padxil larger than this is skipped (Crimson Desert's largest: 602,748 bytes).</summary>
    const int MaxShader = 64 << 20;

    public sealed record Loc(string Paz, long Offset, int StoredSize, int Size, ushort Flags);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public EngineInfo? Detect(Game game) => ShaderGroup(game.InstallDir) == null ? null : new EngineInfo(Family, Version, null, "D3D12", false, null);

    /// <summary>The group folder whose index holds <see cref="ShaderFolder"/>; null when the install isn't laid out this
    /// way. Reads each index only up to its folder names.</summary>
    internal static string? ShaderGroup(string installDir)
    {
        if (!File.Exists(Path.Combine(installDir, GroupList))) return null;
        var groups = new DirectoryInfo(installDir).EnumerateDirectories("????", new EnumerationOptions { IgnoreInaccessible = true })
            .Where(d => d.Name.All(char.IsAsciiDigit)).OrderBy(d => d.Name, StringComparer.Ordinal);
        return groups.FirstOrDefault(d => Pamt.FolderNames(Path.Combine(d.FullName, Pamt.FileName))?.Contains(ShaderFolder) == true)?.FullName;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var group = ShaderGroup(game.InstallDir) ?? throw new InvalidDataException($"no archive index holds {ShaderFolder}: scan the game again");
        var indexBytes = File.ReadAllBytes(Path.Combine(group, Pamt.FileName));
        var (entries, skipped) = Shaders(Pamt.Parse(indexBytes));
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var byKey = new Dictionary<string, List<string>>();
        var libs = new List<string>();
        int read = 0, bad = 0, unparsed = 0, unkeyed = 0;
        foreach (var archive in entries.GroupBy(e => e.Archive))
        {
            var paz = Path.Combine(group, $"{archive.Key}.paz");
            using var h = File.OpenHandle(paz, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var e in archive.OrderBy(e => e.Offset))
            {
                if (++read % 4096 == 0) ct.ThrowIfCancellationRequested();
                var at = new Loc(paz, e.Offset, (int)e.StoredSize, (int)e.Size, e.Flags);
                if (Container(h, at) is not { } c) { bad++; continue; }
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                if (locs.TryAdd(sha, at))
                    try { if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings), RootSignature = null }; }
                    catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { unparsed++; } // valid container, odd program: not usable
                if (!shaders.TryGetValue(sha, out var s)) continue;
                if (s.Stage == Stage.Library) { libs.Add(sha); continue; }
                if (MapKey(e.Name) is not { } key) { unkeyed++; continue; }
                (byKey.TryGetValue(key, out var l) ? l : byKey[key] = []).Add(sha);
            }
        }
        if (shaders.Count == 0) throw new InvalidDataException($"{ShaderFolder}: no shader read from {entries.Count} files: scan the game again");
        var maps = byKey.OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => new ShaderMap(CarvedReader.Sha1Hex($"{ShaderFolder}|{k.Key}"), k.Key, CarvedReader.Platform, k.Value.Distinct().ToList())).ToList();
        libs = libs.Distinct().ToList();
        if (libs.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{ShaderFolder}|{Libraries}"), Libraries, CarvedReader.Platform, libs));
        located[game.Id] = locs;
        log?.Report($"{shaders.Count} shaders ({string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))}) "
            + $"from {entries.Count} files of {Path.GetFileName(group)}, {maps.Count} maps"
            + (libs.Count > 0 ? $", {libs.Count} ray tracing libraries" : "")
            + (skipped > 0 ? $"; {skipped} files of an unknown compression or size left out" : "")
            + (bad > 0 ? $"; {bad} unreadable (not a PASC-wrapped container)" : "") + (unparsed > 0 ? $"; {unparsed} unparseable" : "")
            + (unkeyed > 0 ? $"; {unkeyed} with a name of another layout, in no map" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        // the index names every archive's crc and size: a patch to any shader changes it
        return new ShaderIndex(Convert.ToHexStringLower(SHA1.HashData(indexBytes)), [CarvedReader.Platform], shaders, maps);
    }

    /// <summary>Re-reads each container where the index found it; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Paz))
        {
            SafeFileHandle h;
            try { h = File.OpenHandle(file.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; } // gone since the index
            using var _ = h;
            foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
            {
                ct.ThrowIfCancellationRequested();
                if (Container(h, at) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
            }
        }
    }

    /// <summary>The index's .padxil files in <see cref="ShaderFolder"/> this reader can decode, and how many others it left out.</summary>
    static (List<Pamt.Entry> Entries, int Skipped) Shaders(Pamt pamt)
    {
        var folder = pamt.Folders.FirstOrDefault(f => f.Name == ShaderFolder) ?? throw new InvalidDataException($"the archive index lost {ShaderFolder}: scan the game again");
        var files = pamt.Files.Skip(folder.FirstFile).Take(folder.FileCount).Where(e => e.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        var ok = files.Where(e => e.Flags is Pamt.Stored or Pamt.Lz4 && e.Size <= MaxShader && e.StoredSize <= MaxShader
            && (e.Flags == Pamt.Lz4 || e.StoredSize == e.Size)).ToList();
        return (ok, files.Count - ok.Count);
    }

    /// <summary>One .padxil's container: read, LZ4-decoded when so flagged, unwrapped. Null when any step fails.</summary>
    internal static byte[]? Container(SafeFileHandle h, Loc at)
    {
        var stored = new byte[at.StoredSize];
        if (RandomAccess.Read(h, stored, at.Offset) != stored.Length) return null;
        var file = stored;
        if (at.Flags == Pamt.Lz4)
        {
            file = new byte[at.Size];
            if (LZ4Codec.Decode(stored, file) != file.Length) return null;
        }
        var c = Unwrap(file);
        return c.IsEmpty ? null : c.ToArray();
    }

    /// <summary>The DXBC container of a .padxil: "PASC", u32 version (8), u32 file size, u32 (61), u32 header size (36),
    /// a 16-byte hash, then the container, which must fill the rest of the file (its own size at offset 24). Empty otherwise.</summary>
    internal static ReadOnlySpan<byte> Unwrap(ReadOnlySpan<byte> f)
    {
        if (f.Length < 20 || !f.StartsWith("PASC"u8) || BinaryPrimitives.ReadUInt32LittleEndian(f[8..]) != (uint)f.Length) return default;
        var header = BinaryPrimitives.ReadUInt32LittleEndian(f[16..]);
        if (header < 20 || header > (uint)f.Length - 32) return default;
        var c = f[(int)header..];
        return Dxbc.Valid(c) && Dxbc.Kind(c) >= 0 ? c : default;
    }

    /// <summary>The map a .padxil's shader goes in, from its file name (".padxil" included), or null to leave it out of
    /// every map. The planner pairs shaders by linkage only inside one map (StageSets), so this decides which vertex
    /// shader can meet which pixel shader: too coarse and a map is mostly pairs the game never draws (time and rejected
    /// pipelines), too fine and a VS lands in another map than its PS (pipelines never warmed).
    /// Measured on Crimson Desert (8,618 distinct VS, 15,867 PS; BlackSpaceGameTests.MapKeySurvey), linked VS->PS pairs
    /// and PS no VS of their map feeds: one map for all, 1,797,820 and 229; by field 0, the effect, 913,671 and 233; by
    /// fields 0+4, 758,412 and 245 (field 4 isn't shared by every pipeline); by 0+6, 14,588 and 9,062. Field 0 it is.</summary>
    internal static string? MapKey(string name)
    {
        var f = Path.GetFileNameWithoutExtension(name).Split('_');
        return f.Length == 7 ? f[0] : null;
    }
}
