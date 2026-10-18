using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Rage;

/// <summary>RAGE gen9 (GTA V Enhanced): SGD2 shader libraries stored uncompressed and unencrypted inside the update RPF
/// archives. Each archive's table of contents is encrypted with keys only the game's exe holds, so the libraries are found
/// by their magic at the archive's 512-byte block boundaries instead: no key and no binary of the game is read. Every
/// shader is DXIL and carries its root signature (RTS0); every render pass of an effect names its exact stage set, one
/// IsPipeline map each. Libraries packed into the encrypted entries aren't seen.
/// EngineInfo: Family "RAGE", Version "SGD2+RTS0".</summary>
public sealed class RageReader : IEngineReader
{
    public const string Family = "RAGE", Version = "SGD2" + CarvedReader.EmbeddedRootSignatures;
    const string UpdateDir = "update";

    /// <summary>Bytes Detect scans for a library, smallest archive first (GTA V Enhanced: the first at 3.5 MB of update2.rpf).</summary>
    const long DetectBudget = 512L << 20;
    /// <summary>Bounds that reject a false magic hit early (GTA V Enhanced: 7,594 PS in one library, the largest shader
    /// 0.5 MB, 5.3 MB of effects).</summary>
    const int MaxShaders = 1 << 16, MaxShaderSize = 64 << 20, MaxEffectsBytes = 256 << 20;
    const int Block = 512;   // RPF7 aligns every entry's data to 512 bytes

    static readonly Stage[] Slots = [Stage.Vertex, Stage.Pixel, Stage.Geometry, Stage.Domain, Stage.Hull, Stage.Compute];

    public sealed record Loc(string Path, long Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public EngineInfo? Detect(Game game)
    {
        long read = 0;
        foreach (var f in Archives(game).OrderBy(f => f.Length))
        {
            using var fs = Cursor.Open(f.FullName);
            if (fs == null) continue;
            if (Scan(fs, DetectBudget - read, ref read, at => Library.Read(fs, at, null) != null)) return new EngineInfo(Family, Version, null, "D3D12", false, null);
            if (read >= DetectBudget) break;
        }
        return null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var rootSigs = new RootSigCarriers();
        var platformOf = new Dictionary<string, string>();
        var maps = new List<ShaderMap>();
        var seen = new HashSet<string>();
        int libraries = 0, effects = 0, undecoded = 0, passes = 0, mismatched = 0, unlinked = 0, malformed = 0, bad = 0;
        long read = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var f in Archives(game).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            using var fs = Cursor.Open(f.FullName);
            if (fs == null) continue;
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            var found = false;
            Scan(fs, long.MaxValue, ref read, at =>
            {
                ct.ThrowIfCancellationRequested();
                var lib = Library.Read(fs, at, (off, c) =>
                {
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    if (!locs.TryAdd(sha, new Loc(f.FullName, off, c.Length))) return sha;
                    rootSigs.See(c, sha, Dxbc.Kind(c));
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                        {
                            shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                            if (CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) platformOf[sha] = lanes;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                    return sha;
                });
                if (lib == null) return false;
                found = true;
                libraries++;
                foreach (var e in lib.Effects)
                {
                    effects++;
                    if (e.Passes == null) { if (e.Stages.Any(x => x.Length > 0)) undecoded++; continue; }
                    foreach (var pass in e.Passes)
                    {
                        passes++;
                        var stages = new List<string>();
                        var ok = true;
                        for (var s = 0; s < Slots.Length && ok; s++)
                        {
                            if (pass[s] == 0) continue;
                            var sha = lib.Shaders[s][(int)e.Stages[s][pass[s] - 1]];
                            if (sha == null || !shaders.TryGetValue(sha, out var info)) ok = false;
                            else if (info.Stage != Slots[s]) { mismatched++; ok = false; }
                            else stages.Add(sha);
                        }
                        if (!ok || !Pipeline(pass)) continue;
                        // with a GS or DS the PS reads that stage, not the VS Feeds checks
                        switch (pass[2] != 0 || pass[3] != 0 ? true : Northlight.NorthlightReader.Feeds(stages.Select(h => shaders[h]).ToList()))
                        {
                            case null: malformed++; continue;
                            case false: unlinked++; continue;
                        }
                        var h = CarvedReader.Sha1Hex(string.Join(',', stages.Order(StringComparer.Ordinal)));
                        if (seen.Add(h)) maps.Add(new ShaderMap(h, $"{rel}:{e.Name}", stages.Select(x => platformOf.GetValueOrDefault(x, CarvedReader.Platform)).FirstOrDefault(p => p != CarvedReader.Platform, CarvedReader.Platform), stages, IsPipeline: true));
                    }
                }
                return true;
            }, ct);
            if (found) content.AppendData(Encoding.UTF8.GetBytes($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
        }
        if (libraries == 0) throw new InvalidDataException($"{UpdateDir}: no SGD2 shader library in its archives: scan the game again");
        rootSigs.Apply(shaders);
        located[game.Id] = locs;
        log?.Report($"{libraries} shader libraries ({read / 1e9:F2} GB scanned): {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")) + "), "
            + $"{shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count()} embedded root signatures, {shaders.Values.Count(s => s.InlineRayTracing)} tracing rays inline; "
            + $"{effects} effects, {passes} passes -> {maps.Count} distinct pipelines"
            + (undecoded > 0 ? $"; {undecoded} effects without a pass table" : "")
            + (mismatched > 0 ? $"; warning: {mismatched} passes name a shader of another stage" : "")
            + (unlinked > 0 ? $"; {unlinked} passes left out: their VS doesn't feed their PS" : "")
            + (malformed > 0 ? $"; {malformed} passes left out: a VS writes one output twice" : "")
            + (bad > 0 ? $"; {bad} unparseable" : "") + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. platformOf.Values.Distinct().Order(StringComparer.Ordinal)], shaders, maps, rootSigs.Blobs);
    }

    /// <summary>Re-slices each container where the index found it; one whose bytes changed since (game patched) is skipped.</summary>
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }   // gone since the index
            using var _ = h;
            foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
            {
                ct.ThrowIfCancellationRequested();
                var b = new byte[at.Size];
                if (RandomAccess.Read(h, b, at.Offset) == b.Length && Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
            }
        }
    }

    /// <summary>A VS (with both HS and DS or neither) or a lone CS.</summary>
    static bool Pipeline(byte[] pass) =>
        pass[5] != 0 ? pass.AsSpan(0, 5).IndexOfAnyExcept((byte)0) < 0 : pass[0] != 0 && (pass[3] == 0) == (pass[4] == 0);

    /// <summary>The archives of the update folder (not its DLC packs, which hold no library).</summary>
    static IEnumerable<FileInfo> Archives(Game game)
    {
        var dir = new DirectoryInfo(Path.Combine(game.InstallDir, UpdateDir));
        return dir.Exists ? dir.EnumerateFiles("*.rpf", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }) : [];
    }

    /// <summary>Positional reads through a window (no FileStream: its position check fails once the handle is read directly).</summary>
    internal sealed class Cursor(Microsoft.Win32.SafeHandles.SafeFileHandle h) : IDisposable
    {
        public readonly Microsoft.Win32.SafeHandles.SafeFileHandle Handle = h;
        public readonly long Length = RandomAccess.GetLength(h);
        public long Pos;
        readonly byte[] window = new byte[1 << 20];
        long windowAt = -1;
        int windowLen;

        public static Cursor? Open(string path)
        {
            try { return new Cursor(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        public void Read(Span<byte> to)
        {
            for (var done = 0; done < to.Length;)
            {
                if (windowAt < 0 || Pos < windowAt || Pos >= windowAt + windowLen)
                {
                    windowAt = Pos;
                    windowLen = RandomAccess.Read(Handle, window, Pos);
                    if (windowLen <= 0) { windowAt = -1; throw new EndOfStreamException(); }
                }
                var n = (int)Math.Min(to.Length - done, windowAt + windowLen - Pos);
                window.AsSpan((int)(Pos - windowAt), n).CopyTo(to[done..]);
                done += n;
                Pos += n;
            }
        }

        public byte U8() { Span<byte> b = stackalloc byte[1]; Read(b); return b[0]; }
        public int I32() { Span<byte> b = stackalloc byte[4]; Read(b); return BinaryPrimitives.ReadInt32LittleEndian(b); }
        public ulong U64() { Span<byte> b = stackalloc byte[8]; Read(b); return BinaryPrimitives.ReadUInt64LittleEndian(b); }
        public void Dispose() => Handle.Dispose();
    }

    /// <summary>Calls <paramref name="library"/> at each block boundary that starts with "SGD2", until it returns true for a
    /// library (scanning on after its end) or <paramref name="budget"/> bytes were read; true once one was.</summary>
    static bool Scan(Cursor fs, long budget, ref long read, Func<long, bool> library, CancellationToken ct = default)
    {
        var buf = new byte[8 << 20];
        var any = false;
        for (long at = 0, scanned = 0; at < fs.Length && scanned < budget;)
        {
            ct.ThrowIfCancellationRequested();
            var n = RandomAccess.Read(fs.Handle, buf, at);
            if (n <= 0) break;
            scanned += n;
            read += n;
            var next = at + n - n % Block;
            for (var o = 0; o + 4 <= n; o += Block)
            {
                if (!buf.AsSpan(o, 4).SequenceEqual("SGD2"u8) || !library(at + o)) continue;
                any = true;
                next = (fs.Pos + Block - 1) / Block * Block;   // Library.Read leaves the cursor at the library's end
                break;
            }
            if (next <= at) next = at + Block;
            at = next;
        }
        return any;
    }

    /// <summary>One SGD2 library: its shaders' SHA-1 per stage slot (null: not a valid container) and its effects.</summary>
    internal sealed record Library(List<string?>[] Shaders, List<Effect> Effects)
    {
        /// <summary>The library at <paramref name="at"/>, or null when the bytes there aren't one. Layout: "SGD2", then per
        /// stage (VS, PS, GS, DS, HS, CS) u32 count and each shader as u8 name length (with its NUL), the name, u8, u32 size,
        /// the container, u64 hash, 144 bytes, u32 size and that many bytes; then 8 zero bytes, u32 count and the effects
        /// (<see cref="Effect.Parse"/>). <paramref name="container"/> gets each container's offset and bytes and returns its
        /// SHA-1; null: only the layout is checked (the containers are read past).</summary>
        public static Library? Read(Cursor fs, long at, Func<long, byte[], string>? container)
        {
            try
            {
                fs.Pos = at + 4;
                var shaders = new List<string?>[Slots.Length];
                Span<byte> head = stackalloc byte[28];
                for (var s = 0; s < Slots.Length; s++)
                {
                    var count = (uint)fs.I32();
                    if (count > MaxShaders) return null;
                    shaders[s] = new((int)count);
                    for (var i = 0; i < count; i++)
                    {
                        var name = fs.U8();   // with its NUL; a byte follows it
                        fs.Pos += name + 1;
                        var size = fs.I32();
                        if (size < 32 || size > MaxShaderSize || fs.Pos + size > fs.Length) return null;
                        var off = fs.Pos;
                        if (container == null)
                        {
                            fs.Read(head);
                            if (!head[..4].SequenceEqual("DXBC"u8) || BinaryPrimitives.ReadInt32LittleEndian(head[24..]) != size) return null;
                            fs.Pos = off + size;
                            shaders[s].Add(null);
                        }
                        else
                        {
                            var c = new byte[size];
                            fs.Read(c);
                            shaders[s].Add(Dxbc.Valid(c) && BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(24)) == size ? container(off, c) : null);
                        }
                        fs.Pos += 8 + 144;
                        var block = fs.I32();
                        if (block < 0 || fs.Pos + block > fs.Length) return null;
                        fs.Pos += block;
                    }
                }
                if (fs.U64() != 0) return null;
                var n = (uint)fs.I32();
                var start = fs.Pos;
                if (shaders.All(x => x.Count == 0)) return null;
                for (var want = 16L << 20; ; want *= 4)   // the effects' size isn't stored: read more until they parse
                {
                    var tail = new byte[(int)Math.Min(fs.Length - start, Math.Min(want, MaxEffectsBytes))];
                    RandomAccess.Read(fs.Handle, tail, start);
                    if (ParseEffects(tail, n, shaders) is { } effects) { fs.Pos = start + effects.End; return new Library(shaders, effects.List); }
                    if (tail.Length < want || want >= MaxEffectsBytes) return null;
                }
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentOutOfRangeException) { return null; }
        }

        static (List<Effect> List, int End)? ParseEffects(byte[] b, uint n, List<string?>[] shaders)
        {
            var list = new List<Effect>();
            var p = 0;
            for (var i = 0; i < n; i++)
            {
                if (Effect.Parse(b, ref p) is not { } e || e.Stages.Where((idx, s) => idx.Any(x => x >= shaders[s].Count)).Any()) return null;
                list.Add(e);
            }
            return (list, p);
        }
    }

    /// <param name="Stages">per stage slot, the library index of each of the effect's shaders</param>
    /// <param name="Passes">per render pass, the 1-based index into <paramref name="Stages"/> of each slot (0: none); null
    /// when the pass table wasn't found</param>
    internal sealed record Effect(string Name, uint[][] Stages, List<byte[]>? Passes)
    {
        static readonly byte[] PropList = "proplst\0"u8.ToArray();

        /// <summary>u8 name length (with its NUL), the name, u32, per stage slot u32 count and the library indices, a body up to
        /// "proplst\0", u32 count of u32-sized entries, u32 size of the string table and the table. Null when malformed.</summary>
        public static Effect? Parse(byte[] b, ref int p)
        {
            if (p + 1 > b.Length) return null;
            var len = b[p];
            if (len < 2 || p + len + 1 + 4 > b.Length || b[p + len] != 0) return null;
            var name = Encoding.Latin1.GetString(b, p + 1, len - 1);
            p += 1 + len + 4;
            var stages = new uint[Slots.Length][];
            for (var s = 0; s < Slots.Length; s++)
            {
                if (p + 4 > b.Length) return null;
                var n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
                if (n < 0 || n > MaxShaders || p + 4 + 4L * n > b.Length) return null;
                stages[s] = new uint[n];
                for (var i = 0; i < n; i++) stages[s][i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4 + 4 * i));
                p += 4 + 4 * n;
            }
            var marker = b.AsSpan(p).IndexOf(PropList);
            if (marker < 0) return null;
            var passes = PassTable(b.AsSpan(p, marker), stages);
            p += marker + PropList.Length;
            if (U32(b, ref p) is not { } props) return null;
            for (var i = 0; i < props; i++)
                if (!Sized(b, ref p)) return null;
            return Sized(b, ref p) ? new Effect(name, stages, passes) : null;   // the string table
        }

        static int? U32(byte[] b, ref int p)
        {
            if (p + 4 > b.Length) return null;
            var v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            p += 4;
            return v >= 0 ? v : null;
        }

        /// <summary>Skips a u32 size and that many bytes.</summary>
        static bool Sized(byte[] b, ref int p)
        {
            if (U32(b, ref p) is not { } n || p + (long)n > b.Length) return false;
            p += n;
            return true;
        }

        /// <summary>The body's pass table: u32 technique count, per technique a u32 whose top byte is its pass count, u32 pass
        /// count (their sum), then 24 bytes per pass, the first six one stage slot each. Found as the one 4-aligned place where
        /// that holds and every slot's index is in range; null when there's none or more than one.</summary>
        internal static List<byte[]>? PassTable(ReadOnlySpan<byte> body, uint[][] stages)
        {
            List<byte[]>? found = null;
            for (var p = 8; p + 8 <= body.Length; p += 4)
            {
                var t = BinaryPrimitives.ReadInt32LittleEndian(body[p..]);
                if (t < 1 || t > 512 || p + 8 + 4 * t > body.Length) continue;
                long sum = 0;
                var ok = true;
                for (var i = 0; i < t && ok; i++) { var c = body[p + 4 + 4 * i + 3]; ok = c > 0; sum += c; }
                var q = p + 4 + 4 * t;
                if (!ok || BinaryPrimitives.ReadInt32LittleEndian(body[q..]) != sum || q + 4 + 24 * sum > body.Length) continue;
                var passes = new List<byte[]>((int)sum);
                for (var i = 0; i < sum && ok; i++)
                {
                    var rec = body.Slice(q + 4 + 24 * i, 6).ToArray();
                    for (var s = 0; s < Slots.Length && ok; s++) ok = rec[s] <= stages[s].Length;
                    ok &= rec[0] != 0 || rec[5] != 0;
                    passes.Add(rec);
                }
                if (!ok) continue;
                if (found != null) return null;
                found = passes;
            }
            return found;
        }
    }
}
