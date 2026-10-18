using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.SantaMonica;

/// <summary>Santa Monica Studio's engine on PC (God of War Ragnarök): every shader is in exec\wad\pc_le\*.shaderpack, one
/// LZ4 frame (<see cref="Lz4Frame"/>) per file holding a SHDRPACK version 11 (<see cref="ShaderPack11"/>): the DXIL
/// containers, the RTS0-only containers of the root signatures the game creates, and one record per program naming its
/// root signature and its shader per stage by the 64-bit hashes of those tables. A program may name a shader or a root
/// signature of another pack (root.shaderpack holds the shared ones), so hashes resolve across all packs. Each program is
/// an IsPipeline map with the root signature it names (<see cref="ShaderMap.RootSignature"/>); a shader gets the root
/// signature of its programs, none when they name several (God of War Ragnarök: 7,789 of its 56,096 programs have only such
/// shaders; its 33,907 shaders use 65 root signatures). A program is a map only when its
/// root signature covers every binding of its shaders (<see cref="RootSig.Uncovered"/>) and its stages link (<see cref="Chain"/>):
/// the planner doesn't see a reader's root signatures until materializing. The packs name every root signature exactly:
/// a plan needs no recording on a GPU whose cache doesn't depend on pipeline state.
/// God of War (2018) ships SHDRPACK version 1 (<see cref="ShaderPack1"/>) in the same place: DXBC for D3D11, each shader one
/// LZ4 block. Its program records aren't read: each pack is a pool, and a D3D11 plan is every shader once.
/// EngineInfo: Family "Santa Monica", Version "SHDRPACK 11+RTS0" (D3D12) or "SHDRPACK 1" (D3D11).</summary>
public sealed class SantaMonicaReader : IEngineReader
{
    public const string Family = "Santa Monica", Version = "SHDRPACK 11" + CarvedReader.EmbeddedRootSignatures, Version1 = "SHDRPACK 1";
    const string Platform11 = "PCD3D_SM5";
    static readonly string PackDir = Path.Combine("exec", "wad", "pc_le");
    const string RootPack = "root.shaderpack";

    /// <summary>Table records Index keeps from all packs (God of War Ragnarök: 90,068); the packs past it aren't read.</summary>
    public long MaxRecords { get; init; } = 1 << 22;

    /// <summary>Compressed bytes Detect reads for a pack's first block (God of War Ragnarök: 64 KB blocks).</summary>
    const long DetectBudget = 16L << 20;

    /// <summary>A program's stages in its record's slot order (<see cref="ShaderPack11.Program.Slots"/>): VS, PS, then the VS a
    /// tessellated program uses instead, HS, DS, CS.</summary>
    static readonly Stage[] Slots = [Stage.Vertex, Stage.Pixel, Stage.Vertex, Stage.Hull, Stage.Domain, Stage.Compute];

    /// <param name="Packed">a version 1 pack's LZ4 block size at Offset, decompressing to Size; 0 = Offset is in the frame's content</param>
    public sealed record Loc(string Pack, long Offset, int Size, int Packed = 0);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public int ReaderVersion(EngineInfo engine) => engine.Version == Version ? 2 : 1;

    public EngineInfo? Detect(Game game)
    {
        long read = 0;
        foreach (var f in Packs(game).OrderBy(f => !f.Name.Equals(RootPack, StringComparison.OrdinalIgnoreCase)).ThenBy(f => f.Length))
        {
            if (read >= DetectBudget) break;
            if (ShaderPack1.Head(f.FullName, ref read) is { } raw && ShaderPack1.IsPack(raw)) return new EngineInfo(Family, Version1, null, "D3D11", false, null);
            if (Lz4Frame.First(f.FullName, DetectBudget - read, ref read) is { } head && ShaderPack11.IsPack(head))
                return new EngineInfo(Family, Version, null, "D3D12", false, null);
        }
        return null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        if (engine.Version == Version1) return Index1(game, log, ct);
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var platformOf = new Dictionary<string, string>();
        var shaOf = new Dictionary<ulong, string>();   // the packs' 64-bit shader hash -> SHA-1, parsed shaders only
        var rsOf = new Dictionary<ulong, string>();    // the packs' 64-bit root-signature hash -> SHA-1
        var programs = new List<(string Pack, ShaderPack11.Program P)>();
        var ranges = new Dictionary<string, RootSig.Ranges>();
        var blobs = new Dictionary<string, byte[]>();
        int packs = 0, unreadable = 0, notPacks = 0, overCap = 0, bad = 0, badRs = 0;
        long read = 0, budget = MaxRecords;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var f in Packs(game).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            using var frame = Lz4Frame.Open(f.FullName);
            if (frame == null) { unreadable++; continue; }
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            ShaderPack11? pack;
            try { pack = ShaderPack11.Read(frame, ref budget); }
            catch (InvalidDataException) { unreadable++; continue; }
            if (budget < 0) { overCap++; continue; }
            if (pack == null) { notPacks++; continue; }
            packs++;
            content.AppendData(Encoding.UTF8.GetBytes($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
            // the programs' root evidence counts even when the pack's containers turn out damaged
            programs.AddRange(pack.Programs.Select(p => (rel, p)));
            // read whole before any of it is kept: a block that doesn't decompress leaves nothing of the pack's containers
            var found = new List<(ulong Hash, string Sha, Loc At, ShaderInfo? Info, string? Lanes)>();
            var roots = new List<(ulong Hash, string Sha, Loc At, RootSig.Ranges Ranges, byte[] Blob)>();
            int packBad = 0, packBadRs = 0;
            long packRead = 0;
            try
            {
                var parsed = new HashSet<string>();
                foreach (var s in pack.Shaders.OrderBy(s => s.Offset))
                {
                    ct.ThrowIfCancellationRequested();
                    var c = frame.Read(s.Offset, s.Size);
                    packRead += c.Length;
                    if (!Dxbc.Valid(c) || Dxbc.Kind(c) < 0) { packBad++; continue; }
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    if (locs.ContainsKey(sha) || !parsed.Add(sha)) { found.Add((s.Hash, sha, null!, null, null)); continue; }
                    ShaderInfo? info = null;
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } parsedInfo)
                            info = parsedInfo with { Counts = CarvedReader.Counts(parsedInfo.Bindings), RootSignature = null };
                    }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { packBad++; }   // valid container, odd program: not usable
                    found.Add((s.Hash, sha, new Loc(f.FullName, s.Offset, s.Size), info, info == null ? null : CarvedReader.LanePlatform(Dxbc.WaveLanes(c))));
                }
                foreach (var r in pack.Roots)
                {
                    var c = frame.Read(r.Offset, r.Size);
                    if (!Dxbc.IsRootSignatureOnly(c) || !Dxbc.RootSignatureValid(c)) { packBadRs++; continue; }
                    roots.Add((r.Hash, Convert.ToHexStringLower(SHA1.HashData(c)), new Loc(f.FullName, r.Offset, r.Size), RootSig.Parse(c), c));
                }
            }
            catch (InvalidDataException) { unreadable++; continue; }   // a block that doesn't decompress: the pack's containers are skipped
            (read, bad, badRs) = (read + packRead, bad + packBad, badRs + packBadRs);
            foreach (var (hash, sha, at, info, lanes) in found)
            {
                if (at != null && locs.TryAdd(sha, at) && info != null)
                {
                    shaders[sha] = info;
                    if (lanes != null) platformOf[sha] = lanes;
                }
                if (shaders.ContainsKey(sha)) shaOf.TryAdd(hash, sha);
            }
            foreach (var (hash, sha, at, r, blob) in roots)
            {
                locs.TryAdd(sha, at);
                rsOf.TryAdd(hash, sha);
                ranges.TryAdd(sha, r);
                blobs.TryAdd(sha, blob);
            }
        }
        if (packs == 0) throw new InvalidDataException($@"{PackDir}: no SHDRPACK 11 shader pack: scan the game again");

        // a shader's root signature: the one every program naming it names; one that names a root no pack has blocks it
        const string Unresolved = "";
        var rootsOf = new Dictionary<string, HashSet<string>>();
        foreach (var (_, p) in programs)
            for (var s = 0; s < Slots.Length; s++)
                if (Has(p.Slots[s]) && shaOf.TryGetValue(p.Slots[s], out var h))
                    (rootsOf.TryGetValue(h, out var set) ? set : rootsOf[h] = []).Add(rsOf.GetValueOrDefault(p.RootSignature, Unresolved));
        foreach (var (h, set) in rootsOf)
            if (set.Count == 1 && set.First() is var only && only != Unresolved) shaders[h] = shaders[h] with { RootSignature = only };

        var maps = new List<ShaderMap>();
        var seen = new HashSet<string>();
        int noRs = 0, missing = 0, mismatched = 0, unlinked = 0, malformed = 0, odd = 0, uncovered = 0;
        string? uncoveredExample = null;
        foreach (var (rel, p) in programs)
        {
            if (!rsOf.TryGetValue(p.RootSignature, out var rs)) { noRs++; continue; }
            var stages = new List<string>();
            var ok = true;
            for (var s = 0; s < Slots.Length && ok; s++)
            {
                if (!Has(p.Slots[s])) continue;
                if (!shaOf.TryGetValue(p.Slots[s], out var sha)) { missing++; ok = false; }
                else if (shaders[sha].Stage != Slots[s]) { mismatched++; ok = false; }
                else stages.Add(sha);
            }
            if (!ok) continue;
            if (!Pipeline(p.Slots)) { odd++; continue; }
            switch (Chain(stages.Select(h => shaders[h]).ToList()))
            {
                case null: malformed++; continue;
                case false: unlinked++; continue;
            }
            if (stages.Select(h => RootSig.Uncovered(ranges[rs], shaders[h].Stage, shaders[h])).FirstOrDefault(w => w != null) is { } why)
            {
                uncovered++;
                uncoveredExample ??= why;
                continue;
            }
            var key = CarvedReader.Sha1Hex(string.Join(',', stages.Order(StringComparer.Ordinal)));
            if (seen.Add($"{key}|{rs}"))
                maps.Add(new ShaderMap(key, rel, stages.Select(h => platformOf.GetValueOrDefault(h, CarvedReader.Platform)).FirstOrDefault(x => x != CarvedReader.Platform, CarvedReader.Platform), stages, IsPipeline: true, rs));
        }
        var several = rootsOf.Keys.Count(h => shaders[h].RootSignature == null);
        located[game.Id] = locs;

        var stagesBy = shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count());
        log?.Report($"{packs} shader packs ({read / 1e9:F2} GB of containers): {shaders.Count} shaders ("
            + string.Join(", ", stagesBy.Select(g => $"{g.Count()} {g.Key}")) + $"), {rsOf.Values.Distinct().Count()} root signatures; "
            + $"{programs.Count} programs -> {maps.Count} distinct pipelines, {rootsOf.Count - several} shaders given their root signature"
            + (several > 0 ? $", {several} used with several (or one no pack has) left without: their pipelines take their program's" : "")
            + (uncovered > 0 ? $"; {uncovered} programs left out: their root signature doesn't cover their shaders (e.g. {uncoveredExample})" : "")
            + (noRs > 0 ? $"; warning: {noRs} programs name a root signature no pack has" : "")
            + (missing > 0 ? $"; warning: {missing} programs name a shader no pack has" : "")
            + (mismatched > 0 ? $"; warning: {mismatched} programs name a shader of another stage" : "")
            + (odd > 0 ? $"; warning: {odd} programs aren't a pipeline" : "")
            + (unlinked > 0 ? $"; {unlinked} programs left out: their stages don't link" : "")
            + (malformed > 0 ? $"; {malformed} programs left out: a stage writes one output twice" : "")
            + (badRs > 0 ? $"; {badRs} root signatures the runtime would reject, left out" : "")
            + (notPacks > 0 ? $"; {notPacks} packs of another version not read" : "")
            + (overCap > 0 ? $"; warning: {overCap} packs over the cap of {MaxRecords:N0} table records not read" : "")
            + (unreadable > 0 ? $"; warning: {unreadable} packs don't decompress" : "")
            + (bad > 0 ? $"; {bad} unparseable" : "")
            + string.Concat(platformOf.Values.GroupBy(x => x).Select(g => $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs"))
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. platformOf.Values.Distinct().Order(StringComparer.Ordinal)], shaders, maps, blobs);
    }

    ShaderIndex Index1(Game game, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var maps = new List<ShaderMap>();
        int packs = 0, notPacks = 0, bad = 0;
        long read = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var f in Packs(game).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            using var h = ShaderPack1.Open(f.FullName);
            if (h == null || ShaderPack1.Read(h) is not { } records) { notPacks++; continue; }
            packs++;
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            content.AppendData(Encoding.UTF8.GetBytes($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
            var pool = new List<string>();
            foreach (var r in records)
            {
                ct.ThrowIfCancellationRequested();
                read += r.Packed;
                if (ShaderPack1.Decode(h, r) is not { } c || !Dxbc.Valid(c) || Dxbc.Kind(c) < 0) { bad++; continue; }
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                if (locs.TryAdd(sha, new Loc(f.FullName, r.Offset, r.Size, r.Packed)))
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                    }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                if (shaders.ContainsKey(sha)) pool.Add(sha);
            }
            if (pool.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex(rel), rel, Platform11, pool.Distinct().ToList()));
        }
        if (packs == 0) throw new InvalidDataException($@"{PackDir}: no SHDRPACK 1 shader pack: scan the game again");
        located[game.Id] = locs;
        log?.Report($"{packs} shader packs ({read / 1e9:F2} GB read): {shaders.Count} DirectX 11 shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")) + "), one pool per pack"
            + (notPacks > 0 ? $"; {notPacks} packs of another version not read" : "")
            + (bad > 0 ? $"; {bad} unparseable" : "") + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [Platform11], shaders, maps);
    }

    /// <summary>Each stage feeds the next (VS or tessellation VS, HS, DS, PS): <see cref="Planning.Planner.Links"/>, the
    /// component type of every input it gives, an HS's control points as the DS's input signature, and every input in the
    /// register it's written to (the runtime rejects the pipeline otherwise). Null when a stage writes one output twice.</summary>
    internal static bool? Chain(IReadOnlyList<ShaderInfo> stages)
    {
        var order = stages.Where(x => x.Stage != Stage.Compute).OrderBy(x => x.Stage switch { Stage.Vertex => 0, Stage.Hull => 1, Stage.Domain => 2, _ => 3 }).ToList();
        for (var k = 1; k < order.Count; k++)
        {
            var (src, dst) = (order[k - 1], order[k]);
            var outs = new Dictionary<(string, int), SigElement>();
            foreach (var o in src.Outputs)
                if (!outs.TryAdd((o.Semantic.ToUpperInvariant(), o.Index), o)) return null;
            if (!Planning.Planner.Links(src, dst)) return false;
            if (dst.Stage == Stage.Domain && Planning.Planner.Sig(src.Outputs) != Planning.Planner.Sig(dst.Inputs)) return false;
            foreach (var i in dst.Inputs)
                if (outs.TryGetValue((i.Semantic.ToUpperInvariant(), i.Index), out var o)
                    && (i.SysValue == 0 && o.CompType != i.CompType || o.Register != i.Register)) return false;
        }
        return true;
    }

    static bool Has(ulong slot) => slot is not (0 or ulong.MaxValue);

    /// <summary>A VS (with a PS, or alone: a depth pass), a tessellated VS + HS + DS (with or without a PS), or a CS alone.</summary>
    static bool Pipeline(ulong[] s)
    {
        bool vs = Has(s[0]), ps = Has(s[1]), ls = Has(s[2]), hs = Has(s[3]), ds = Has(s[4]), cs = Has(s[5]);
        return cs ? !(vs || ps || ls || hs || ds) : vs ? !(ls || hs || ds) : ls && hs && ds;
    }

    /// <summary>Decompresses only the blocks holding each container; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Pack))
        {
            if (engine.Version == Version1)
            {
                using var h = ShaderPack1.Open(file.Key);
                if (h == null) continue;
                foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
                {
                    ct.ThrowIfCancellationRequested();
                    if (ShaderPack1.Decode(h, new ShaderPack1.Shader(0, 0, at.Offset, at.Packed, at.Size)) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
                }
                continue;
            }
            using var frame = Lz4Frame.Open(file.Key);
            if (frame == null) continue;   // gone or changed since the index
            foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
            {
                ct.ThrowIfCancellationRequested();
                byte[] b;
                try { b = frame.Read(at.Offset, at.Size); }
                catch (InvalidDataException) { continue; }
                if (Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
            }
        }
    }

    static IEnumerable<FileInfo> Packs(Game game)
    {
        var dir = new DirectoryInfo(Path.Combine(game.InstallDir, PackDir));
        return dir.Exists ? dir.EnumerateFiles("*.shaderpack", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }) : [];
    }
}

/// <summary>A decompressed SHDRPACK version 11. Little-endian header (128 bytes): "SHDRPACK", u64 version, u64 shader count,
/// u64 root-signature count, u64 program count, u64 pass count, then u64 offsets: shader table, root-signature table,
/// programs, passes, shader data, root-signature data; +0x78 the shader data's size.
///   - shader, 104 bytes: u64 hash, u32 type, u32 size, u32 offset from the shader data, u32 (non-zero on compute shaders
///     only, not an offset), 12 bytes, the name;
///   - root signature, 16 bytes: u64 hash, u32 offset from the root-signature data, u32 size (an RTS0-only container);
///   - program, 3,744 bytes: u64 hash, u64 its root signature's hash, ..., +0x688 six u64 shader hashes (VS, PS, the VS of a
///     tessellated program, HS, DS, CS; 0 = none), then its bindings;
///   - pass, 904 bytes: per-pass state of a program, not read.</summary>
public sealed class ShaderPack11
{
    public const int Version = 11, HeaderSize = 128, ShaderRecord = 104, RootRecord = 16, ProgramRecord = 3744, PassRecord = 904, SlotsAt = 0x688;
    const long MaxCount = 1 << 20;

    public sealed record Shader(ulong Hash, int Type, long Offset, int Size);
    public sealed record Root(ulong Hash, long Offset, int Size);
    public sealed record Program(ulong Hash, ulong RootSignature, ulong[] Slots);

    public required IReadOnlyList<Shader> Shaders { get; init; }
    public required IReadOnlyList<Root> Roots { get; init; }
    public required IReadOnlyList<Program> Programs { get; init; }

    /// <summary>The bytes start a version 11 pack.</summary>
    public static bool IsPack(ReadOnlySpan<byte> head) =>
        head.Length >= HeaderSize && head.StartsWith("SHDRPACK"u8) && BinaryPrimitives.ReadUInt64LittleEndian(head[8..]) == Version;

    /// <summary>The pack's tables; null when the frame doesn't hold a version 11 pack, its tables don't fit it or its records
    /// would take <paramref name="budget"/> below 0 (each table record counts one). Tables are read <see cref="Chunk"/> bytes at
    /// a time, so a header claiming more records than the blocks hold fails before their size is allocated.
    /// InvalidDataException when a block doesn't decompress.</summary>
    public static ShaderPack11? Read(Lz4Frame f, ref long budget)
    {
        if (f.Length < HeaderSize) return null;
        var h = f.Read(0, HeaderSize);
        if (!IsPack(h)) return null;
        long U(int at) => (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(at)), long.MaxValue);
        long nShaders = U(0x10), nRoots = U(0x18), nPrograms = U(0x20), nPasses = U(0x28);
        long shaderTable = U(0x30), rootTable = U(0x38), programTable = U(0x40), passTable = U(0x48), data = U(0x50), rootData = U(0x58);
        var length = f.Length;
        bool Fits(long at, long n, long size) => n <= MaxCount && at >= HeaderSize && at <= length && n * size <= length - at;
        if (!Fits(shaderTable, nShaders, ShaderRecord) || !Fits(rootTable, nRoots, RootRecord) || !Fits(programTable, nPrograms, ProgramRecord)
            || !Fits(passTable, nPasses, PassRecord) || data > length || rootData > length) return null;
        if ((budget -= nShaders + nRoots + nPrograms) < 0) return null;

        var shaders = new List<Shader>();
        foreach (var r in Records(f, shaderTable, nShaders, ShaderRecord))
        {
            var (size, at) = (BinaryPrimitives.ReadUInt32LittleEndian(r.Span[12..]), data + BinaryPrimitives.ReadUInt32LittleEndian(r.Span[16..]));
            if (size is < 32 or > Dxbc.MaxSize || at + size > length) return null;
            shaders.Add(new Shader(BinaryPrimitives.ReadUInt64LittleEndian(r.Span), BinaryPrimitives.ReadInt32LittleEndian(r.Span[8..]), at, (int)size));
        }
        var roots = new List<Root>();
        foreach (var r in Records(f, rootTable, nRoots, RootRecord))
        {
            var (at, size) = (rootData + BinaryPrimitives.ReadUInt32LittleEndian(r.Span[8..]), BinaryPrimitives.ReadUInt32LittleEndian(r.Span[12..]));
            if (size is < 32 or > Dxbc.MaxSize || at + size > length) return null;
            roots.Add(new Root(BinaryPrimitives.ReadUInt64LittleEndian(r.Span), at, (int)size));
        }
        var programs = new List<Program>();
        foreach (var r in Records(f, programTable, nPrograms, ProgramRecord))
            programs.Add(new Program(BinaryPrimitives.ReadUInt64LittleEndian(r.Span), BinaryPrimitives.ReadUInt64LittleEndian(r.Span[8..]),
                Enumerable.Range(0, 6).Select(k => BinaryPrimitives.ReadUInt64LittleEndian(r.Span[(SlotsAt + 8 * k)..])).ToArray()));
        return new ShaderPack11 { Shaders = shaders, Roots = roots, Programs = programs };
    }

    /// <summary>Bytes of a table read at once.</summary>
    const int Chunk = 1 << 20;

    static IEnumerable<ReadOnlyMemory<byte>> Records(Lz4Frame f, long at, long n, int size)
    {
        var per = Math.Max(1, Chunk / size);
        for (long i = 0; i < n; i += per)
        {
            var k = (int)Math.Min(per, n - i);
            var b = f.Read(at + i * size, k * size);
            for (var j = 0; j < k; j++) yield return b.AsMemory(j * size, size);
        }
    }
}

/// <summary>A SHDRPACK version 1 file (God of War (2018)), not compressed as a whole. Header (0x60 bytes): "SHDRPACK", u64
/// version, u64 program count, u64, u64 shader count, u64 the shaders' size, u64 their packed size; then 112-byte shader
/// records: u64 hash, u32 type (0 CS, 1 PS, 2 VS), u32 packed size, u32 size, u32 offset from the end of the table, zeros, the
/// name. Each shader is one raw LZ4 block (no frame) of a DXBC container. The program records aren't in this file.</summary>
public static class ShaderPack1
{
    public const int Version = 1, HeaderSize = 0x60, Record = 112;
    const long MaxCount = 1 << 20;

    public sealed record Shader(ulong Hash, int Type, long Offset, int Packed, int Size);

    public static bool IsPack(ReadOnlySpan<byte> head) =>
        head.Length >= HeaderSize && head.StartsWith("SHDRPACK"u8) && BinaryPrimitives.ReadUInt64LittleEndian(head[8..]) == Version;

    public static SafeFileHandle? Open(string path)
    {
        try { return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The file's first <see cref="HeaderSize"/> bytes (fewer when it's shorter); null when it can't be read.</summary>
    public static byte[]? Head(string path, ref long read)
    {
        using var h = Open(path);
        if (h == null) return null;
        var b = new byte[HeaderSize];
        try
        {
            var n = RandomAccess.Read(h, b, 0);
            read += n;
            return b[..n];
        }
        catch (IOException) { return null; }
    }

    /// <summary>The shader records; null when the file isn't a version 1 pack or a record doesn't fit it.</summary>
    public static List<Shader>? Read(SafeFileHandle h)
    {
        try
        {
            var length = RandomAccess.GetLength(h);
            var head = new byte[HeaderSize];
            if (RandomAccess.Read(h, head, 0) != HeaderSize || !IsPack(head)) return null;
            var n = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x20));
            if (n > MaxCount || HeaderSize + (long)n * Record > length) return null;
            var table = new byte[(int)n * Record];
            if (RandomAccess.Read(h, table, HeaderSize) != table.Length) return null;
            var data = HeaderSize + table.LongLength;
            var shaders = new List<Shader>();
            for (var i = 0; i < (int)n; i++)
            {
                var r = table.AsSpan(i * Record);
                uint packed = BinaryPrimitives.ReadUInt32LittleEndian(r[12..]), size = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]);
                var at = data + BinaryPrimitives.ReadUInt32LittleEndian(r[20..]);
                if (packed is 0 or > Dxbc.MaxSize || size is < 32 or > Dxbc.MaxSize || at + packed > length) return null;
                shaders.Add(new Shader(BinaryPrimitives.ReadUInt64LittleEndian(r), BinaryPrimitives.ReadInt32LittleEndian(r[8..]), at, (int)packed, (int)size));
            }
            return shaders;
        }
        catch (IOException) { return null; }
    }

    /// <summary>The shader's container; null when its block can't be read or doesn't decompress to its size.</summary>
    public static byte[]? Decode(SafeFileHandle h, Shader s)
    {
        var raw = new byte[s.Packed];
        try { if (RandomAccess.Read(h, raw, s.Offset) != raw.Length) return null; }
        catch (IOException) { return null; }
        var o = new byte[s.Size];
        return LZ4Codec.Decode(raw, o) == s.Size ? o : null;
    }
}

/// <summary>An LZ4 frame file read at random. Frame: magic 04 22 4D 18, FLG (version 01, bit 5 independent blocks, bit 4 block
/// checksums, bit 3 content size, bit 0 dictionary id), BD (bits 6-4: the block size, 4 = 64 KB ... 7 = 4 MB), the u64
/// content size when flagged, a header checksum byte; then blocks of u32 size (high bit: stored uncompressed; 0 ends the
/// frame), data, and a u32 checksum when flagged. Only frames of independent blocks without a dictionary are read: a block
/// then decompresses on its own. Every block but the last holds the full block size (as LZ4's encoder writes them; a
/// block that doesn't is a read error). Bytes after the first frame are ignored.</summary>
public sealed class Lz4Frame : IDisposable
{
    const uint Magic = 0x184D2204;
    const long MaxLength = 16L << 30;
    const int MaxRatio = 300;   // LZ4 expands a block at most about 255 times (a 64 KB block of zeros: 267 bytes)

    readonly SafeFileHandle h;
    readonly int blockSize;
    readonly long[] at;        // each block's data in the file
    readonly int[] stored;     // its stored size, negative when stored uncompressed
    (int Block, byte[] Data) last = (-1, []);
    public long Length { get; private set; }

    Lz4Frame(SafeFileHandle h, int blockSize, long[] at, int[] stored) => (this.h, this.blockSize, this.at, this.stored) = (h, blockSize, at, stored);

    /// <summary>The frame with its block table, or null when the file isn't one this reads (or can't be read).</summary>
    public static Lz4Frame? Open(string path)
    {
        SafeFileHandle h;
        try { h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        try
        {
            if (Parse(h) is { } f) return f;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OverflowException) { }
        h.Dispose();
        return null;
    }

    /// <summary>The frame header: (where the first block's size is, the block size, block checksums); null when not readable.</summary>
    static (int First, int BlockSize, bool Checksums, long? ContentSize)? Header(ReadOnlySpan<byte> b)
    {
        if (b.Length < 7 || BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic) return null;
        byte flg = b[4], bd = b[5];
        if (flg >> 6 != 1 || (flg & 0x20) == 0 || (flg & 0x01) != 0 || (bd >> 4 & 7) < 4) return null;
        var contentSize = (flg & 0x08) != 0;
        var first = 6 + (contentSize ? 8 : 0) + 1;
        if (b.Length < first + 4) return null;
        return (first, 1 << (2 * (bd >> 4 & 7) + 8), (flg & 0x10) != 0, contentSize ? (long)BinaryPrimitives.ReadUInt64LittleEndian(b[6..]) : null);
    }

    static Lz4Frame? Parse(SafeFileHandle h)
    {
        var length = RandomAccess.GetLength(h);
        var head = new byte[19];
        var n = RandomAccess.Read(h, head, 0);
        if (Header(head.AsSpan(0, n)) is not var (first, bs, checksums, contentSize)) return null;
        var at = new List<long>();
        var stored = new List<int>();
        Span<byte> word = stackalloc byte[4];
        for (long pos = first; ;)
        {
            if (RandomAccess.Read(h, word, pos) != 4) return null;
            var v = BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (v == 0) break;
            var size = (int)(v & 0x7FFFFFFF);
            if (size == 0 || size > bs || pos + 4 + size > length || (long)(at.Count + 1) * bs > MaxLength) return null;
            at.Add(pos + 4);
            stored.Add((v & 0x80000000) != 0 ? -size : size);
            pos += 4 + size + (checksums ? 4 : 0);
        }
        if (at.Count == 0) return null;
        // every block but the last decompresses to the full block size: stored, it is that size; compressed, it can't be tiny
        for (var k = 0; k < at.Count - 1; k++)
            if (stored[k] < 0 ? -stored[k] != bs : (long)stored[k] * MaxRatio < bs) return null;
        var f = new Lz4Frame(h, bs, [.. at], [.. stored]);
        f.Length = (long)(at.Count - 1) * bs + f.Block(at.Count - 1, exact: false).Length;
        return contentSize is { } c && c != f.Length ? null : f;
    }

    /// <summary>The first block of the file's frame, decompressed; null when it isn't a frame this reads or the block is over the
    /// budget. <paramref name="read"/> grows by the bytes read.</summary>
    public static byte[]? First(string path, long budget, ref long read)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[23];
            var n = RandomAccess.Read(h, head, 0);
            read += n;
            if (Header(head.AsSpan(0, n)) is not var (first, bs, _, _) || n < first + 4) return null;
            var v = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(first));
            var size = (int)(v & 0x7FFFFFFF);
            if (v == 0 || size > bs || size > budget - n) return null;
            var raw = new byte[size];
            if (RandomAccess.Read(h, raw, first + 4) != size) return null;
            read += size;
            if ((v & 0x80000000) != 0) return raw;
            var o = new byte[bs];
            var got = LZ4Codec.Decode(raw, o);
            return got > 0 ? o[..got] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary><paramref name="count"/> bytes from <paramref name="from"/>, decompressing only the blocks they're in.
    /// InvalidDataException when the range is past the end or a block doesn't decompress to its size.</summary>
    public byte[] Read(long from, int count)
    {
        if (from < 0 || count < 0 || from + count > Length) throw new InvalidDataException("past the frame's end");
        var output = new byte[count];
        for (var pos = from; pos < from + count;)
        {
            var k = (int)(pos / blockSize);
            var data = Block(k, exact: true);
            var lo = (int)(pos - (long)k * blockSize);
            var n = (int)Math.Min(data.Length - lo, from + count - pos);
            data.AsSpan(lo, n).CopyTo(output.AsSpan((int)(pos - from)));
            pos += n;
        }
        return output;
    }

    byte[] Block(int k, bool exact)
    {
        if (last.Block == k) return last.Data;
        var raw = new byte[Math.Abs(stored[k])];
        if (RandomAccess.Read(h, raw, at[k]) != raw.Length) throw new InvalidDataException("frame truncated");
        byte[] data;
        if (stored[k] < 0) data = raw;
        else
        {
            var o = new byte[blockSize];
            var n = LZ4Codec.Decode(raw, o);
            if (n <= 0) throw new InvalidDataException("an LZ4 block doesn't decompress");
            data = n == blockSize ? o : o[..n];
        }
        if (exact && k < at.Length - 1 && data.Length != blockSize) throw new InvalidDataException("an LZ4 block before the last isn't full");
        last = (k, data);
        return data;
    }

    public void Dispose() => h.Dispose();
}
