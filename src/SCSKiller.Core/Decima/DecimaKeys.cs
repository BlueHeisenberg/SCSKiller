using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using SCSKiller.Core.App;
using static SCSKiller.Core.Decima.DecimaArchive;

namespace SCSKiller.Core.Decima;

/// <summary>The salts of an encrypted Decima game (Death Stranding). Kept only locally, in
/// %LOCALAPPDATA%\SCSKiller\games\&lt;id&gt;\decima.key (hex), never in plans, logs or anything meant to be shared. Found
/// statically in the game's own exe file (<see cref="Scan"/>), once per exe build: a failed scan is remembered in
/// decima.scan with the exe's size and write time. Salts are checked against an archive before they're used or stored.</summary>
public sealed class DecimaKeys(string dataDir)
{
    string KeyFile(Game g) => Path.Combine(new AppStore(dataDir).GameDir(g.Id), "decima.key");
    string ScanFile(Game g) => Path.Combine(new AppStore(dataDir).GameDir(g.Id), "decima.scan");
    /// <summary>Bump when <see cref="Scan"/> finds salts it missed before: a remembered failure is then scanned again.</summary>
    const int ScanVersion = 1;
    static string Stamp(string exe) => new FileInfo(exe) is { Exists: true } f ? $"{ScanVersion}:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";

    /// <summary>Salts that open the <paramref name="archives"/> (smallest first): the stored ones, else a static scan of the
    /// game's exe file. <paramref name="why"/> says how, without the salts.</summary>
    public Salts? Get(Game game, IReadOnlyList<string> archives, out string why)
    {
        if (Stored(game) is { } stored && Opens(stored, archives)) { why = "stored salts"; return stored; }
        var stamp = Stamp(game.ExePath);
        if (Remembered(game) is [var s, var reason, ..] && s == stamp) { why = reason; return null; }
        try { Codecs.Load(zlib: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { why = "the Oodle codec can't be loaded"; return null; }   // not remembered
        var sw = Stopwatch.StartNew();
        Salts? salts;
        try { salts = Scan(game.ExePath, archives, out why); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { (salts, why) = (null, "the game's exe can't be read"); }
        why += $" ({sw.Elapsed.TotalSeconds:F1} s)";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game))!);
            if (salts != null) File.WriteAllText(KeyFile(game), Convert.ToHexString([.. salts.Iv, .. salts.Data]));
            else File.WriteAllLines(ScanFile(game), [stamp, why]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // not kept: scanned again next time
        return salts;
    }

    string[] Remembered(Game game)
    {
        try { return File.Exists(ScanFile(game)) ? File.ReadAllLines(ScanFile(game)) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The stored file's write time, "" if none: salts found or removed re-run detection.</summary>
    public string KeyStamp(Game game) => File.Exists(KeyFile(game)) ? File.GetLastWriteTimeUtc(KeyFile(game)).Ticks.ToString() : "";

    /// <summary>The stored salts, unchecked (an archive they don't open doesn't open).</summary>
    public Salts? Stored(Game game)
    {
        string hex;
        try { hex = File.Exists(KeyFile(game)) ? File.ReadAllText(KeyFile(game)).Trim() : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return hex.Length == 56 && hex.All(Uri.IsHexDigit) ? new Salts(Convert.FromHexString(hex[..24]), Convert.FromHexString(hex[24..])) : null;
    }

    /// <summary>The salts open the first archive with chunks: its header decrypts to its file size, its first and last
    /// chunks decompress.</summary>
    public static bool Opens(Salts salts, IEnumerable<string> archives)
    {
        foreach (var path in archives)
        {
            using var a = DecimaArchive.Open(path, salts);
            if (a == null || a.Length == 0) continue;
            try
            {
                a.Read(0, new byte[1]);
                a.Read(a.Length - 1, new byte[1]);
                return true;
            }
            catch (InvalidDataException) { return false; }
        }
        return false;
    }

    const int Step = 4 << 20;
    /// <summary>Bytes of candidate chunks decoded before the data salt scan gives up (a real exe: under 1 GB).</summary>
    const long DecodeBudget = 4L << 30;

    /// <summary>Static search of the exe file, 4-byte aligned (the salts are u32 arrays): the header salt is the 12 bytes after
    /// which the first archive's header decrypts to its file size; the data salt the 16 bytes after which the smallest
    /// compressed chunk of the archives decompresses, within <see cref="DecodeBudget"/>. Streams the exe; read-only.</summary>
    public static Salts? Scan(string exePath, IReadOnlyList<string> archives, out string how)
    {
        var head = new byte[HeaderSize];
        long length;
        using (var f = File.OpenHandle(archives[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            length = RandomAccess.GetLength(f);
            if (RandomAccess.Read(f, head, 0) != HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(head) != Encrypted)
            {
                how = "not found: the archive isn't an encrypted one";
                return null;
            }
        }
        var block = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(block, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)));
        var m = new byte[16];
        var iv = Find(exePath, 12, () => false, c =>
        {
            c.CopyTo(block.AsSpan(4));
            Murmur3(block, m);
            return (BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8)) ^ BinaryPrimitives.ReadInt64LittleEndian(m)) == length;
        });
        if (iv == null) { how = "not found: no header salt in the exe"; return null; }

        // a chunk smaller than it unpacks: garbage that decodes to its size with a wrong salt is a stored Oodle block, never that
        var withIv = new Salts(iv, new byte[16]);
        (string Path, int Chunk, int Packed)? smallest = null;
        foreach (var path in archives)
        {
            using var a = DecimaArchive.Open(path, withIv);
            if (a?.Compressed() is { } c && (smallest == null || c.Packed < smallest.Value.Packed)) smallest = (path, c.Chunk, c.Packed);
        }
        (byte[] Packed, int Size, byte[] Entry)? sample = null;
        if (smallest is var (at, chunk, _))
            using (var a = DecimaArchive.Open(at, withIv))
                try { sample = a?.Raw(chunk); }
                catch (InvalidDataException) { }
        if (sample is not var (packed, want, entry) || packed.Length < 2) { how = "not found: no compressed chunk to check a data salt with"; return null; }
        var hash = new byte[16];
        Murmur3(entry, hash);
        var (src, into, k) = (new byte[packed.Length], new byte[want], new byte[16]);
        long budget = DecodeBudget;
        var data = Find(exePath, 16, () => budget <= 0, c =>
        {
            if (!Random(c)) return false;
            for (var i = 0; i < 16; i++) m[i] = (byte)(hash[i] ^ c[i]);
            MD5.HashData(m, k);
            // an Oodle block header: low nibble 0xC, bits 4-5 clear, then Kraken, Mermaid, Selkie or Leviathan
            if (((packed[0] ^ k[0]) & 0x3F) != 0x0C || ((packed[1] ^ k[1]) & 0x7F) is not (6 or 10 or 11 or 12)) return false;
            budget -= Math.Max(Math.Max(packed.Length, want), 4096);   // charged before decoding: a failing decode may still expand to want
            packed.CopyTo(src, 0);
            Salts.XorRepeated(src, k);
            try { Decompress(src, src.Length, into, want); return true; }
            catch (InvalidDataException) { return false; }
        });
        if (data == null) { how = budget <= 0 ? "not found: too many data salt candidates in the exe" : "not found: no data salt in the exe"; return null; }
        how = "found in the exe";
        return new Salts(iv, data);
    }

    /// <summary>A real salt is random bytes: most of 16 distinct (a run of zeros, text or a table isn't one).</summary>
    static bool Random(ReadOnlySpan<byte> c)
    {
        Span<ulong> seen = stackalloc ulong[4];
        var n = 0;
        foreach (var b in c)
            if ((seen[b >> 6] & 1UL << b) == 0) { seen[b >> 6] |= 1UL << b; n++; }
        return n >= 12;
    }

    /// <summary>The first <paramref name="count"/> bytes at a 4-byte aligned offset of the file that <paramref name="match"/>;
    /// null once <paramref name="stop"/> holds.</summary>
    static byte[]? Find(string path, int count, Func<bool> stop, Func<ReadOnlySpan<byte>, bool> match)
    {
        using var f = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = RandomAccess.GetLength(f);
        var buf = new byte[Step + count];
        for (long at = 0; at < len; at += Step)
        {
            var n = RandomAccess.Read(f, buf, at);
            for (var i = 0; i + count <= n && i < Step; i += 4)
            {
                if (stop()) return null;
                if (match(buf.AsSpan(i, count))) return buf.AsSpan(i, count).ToArray();
            }
        }
        return null;
    }
}
