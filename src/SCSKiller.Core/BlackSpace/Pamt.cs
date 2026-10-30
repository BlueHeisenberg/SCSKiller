using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.BlackSpace;

/// <summary>A Pearl Abyss archive group's index, <c>NNNN\0.pamt</c>, over its data files <c>NNNN\&lt;i&gt;.paz</c>.
/// Little-endian: u32 hash (<see cref="PazCrypto.Hash"/> of the bytes from offset 12, the group's row in meta\0.papgt),
/// u32 paz count, a byte, 3 key bytes (their XOR is <see cref="KeyByte"/>); per paz {u32 index, u32 crc, u32 size}; the
/// folder names and the file names, each a u32 byte length and a trie of {u32 parent offset (FFFFFFFF: none), u8 length,
/// bytes} whose path is its parents' text then its own; u32 folder count, per folder {u32 hash of its path, u32 name offset, u32 first file, u32 file count}; u32 file count, per
/// file {u32 name offset, u32 paz offset, u32 stored size, u32 size, u16 paz index, u16 flags}. Flags: low 4 bits the
/// compression (0 none, 2 an LZ4 block), the next 4 the encryption (0 none, 1 ICE, 2 AES, 3 <see cref="PazCrypto"/>); only
/// those read here are opened.</summary>
public sealed class Pamt
{
    public sealed record Entry(string Path, int Paz, uint Offset, uint Stored, uint Size, ushort Flags)
    {
        public int Compression => Flags & 0xF;
        public int Encryption => Flags >> 4 & 0xF;
    }

    /// <summary>An entry's size, at most: the shader cache's largest is under 1 MB.</summary>
    public const uint MaxEntry = 64 << 20;
    /// <summary>An index's size, at most (the game's largest: 20 MB), and a path's length.</summary>
    public const int MaxIndex = 64 << 20, MaxPath = 1024;
    /// <summary>The characters of all names and paths an index expands to, per byte of it (the game's: up to 3.6), and in all.</summary>
    const long MaxExpansion = 16, MaxChars = 192L << 20;
    /// <summary>LZ4's best ratio is under 255: a bigger size is no LZ4 block of the stored bytes.</summary>
    const ulong MaxRatio = 255;

    static long Budget(int bytes) => Math.Min(MaxExpansion * bytes + MaxPath, MaxChars);

    public uint Hash { get; }
    /// <summary>The group's part of its entries' keys.</summary>
    public byte KeyByte { get; }
    public IReadOnlyList<uint> PazSizes { get; }
    public IReadOnlyList<string> Folders { get; }
    public IReadOnlyList<Entry> Files { get; }

    Pamt(uint hash, byte keyByte, IReadOnlyList<uint> paz, IReadOnlyList<string> folders, IReadOnlyList<Entry> files) =>
        (Hash, KeyByte, PazSizes, Folders, Files) = (hash, keyByte, paz, folders, files);

    public static Pamt Read(string path) => Parse(Bytes(path));

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The index file's bytes, refused past <see cref="MaxIndex"/>.</summary>
    public static byte[] Bytes(string path)
    {
        using var f = Open(path);
        var n = f.Length;
        if (n > MaxIndex) throw new InvalidDataException($"an index of {n >> 20} MB");
        var b = new byte[n];
        f.ReadExactly(b);
        return b;
    }

    /// <summary>The folder paths only, from the head of the file.</summary>
    public static IReadOnlyList<string> FolderNames(string path)
    {
        using var f = Open(path);
        byte[] Head(long n)
        {
            if (n > f.Length || n > MaxIndex) throw new InvalidDataException("a table past the end of the index");
            var b = new byte[n];
            f.Position = 0;
            f.ReadExactly(b);
            return b;
        }
        var npaz = BinaryPrimitives.ReadUInt32LittleEndian(Head(8).AsSpan(4));
        var at = 12 + 12L * npaz;
        var len = BinaryPrimitives.ReadUInt32LittleEndian(Head(at + 4).AsSpan((int)at));
        var trie = Head(at + 4 + len).AsSpan((int)at + 4);
        var budget = Budget(trie.Length);
        return [.. Trie(trie, ref budget).Values];
    }

    public static Pamt Parse(ReadOnlySpan<byte> b)
    {
        var r = new Reader(b);
        var hash = r.U32();
        var npaz = r.U32();
        r.Skip(1);
        var keyByte = (byte)(r.U8() ^ r.U8() ^ r.U8());
        var paz = new uint[r.Count(npaz, 12)];
        for (var i = 0; i < paz.Length; i++) { r.Skip(8); paz[i] = r.U32(); }
        var budget = Budget(b.Length);
        var folderNames = Trie(r.Bytes(r.U32()), ref budget);
        var fileNames = Trie(r.Bytes(r.U32()), ref budget);
        var folders = new (uint Name, uint First, uint Count)[r.Count(r.U32(), 16)];
        for (var i = 0; i < folders.Length; i++) { r.Skip(4); folders[i] = (r.U32(), r.U32(), r.U32()); }
        var rows = new (uint Name, uint Offset, uint Stored, uint Size, ushort Paz, ushort Flags)[r.Count(r.U32(), 20)];
        for (var i = 0; i < rows.Length; i++) rows[i] = (r.U32(), r.U32(), r.U32(), r.U32(), r.U16(), r.U16());
        if (!r.AtEnd) throw new InvalidDataException("bytes after the file table");
        var files = new List<Entry>(rows.Length);
        var listed = new bool[rows.Length];
        foreach (var (name, first, count) in folders)
        {
            if (!folderNames.TryGetValue(name, out var dir) || first > rows.Length || count > rows.Length - first) throw new InvalidDataException("a folder outside the tables");
            if (listed.AsSpan((int)first, (int)count).Contains(true)) throw new InvalidDataException("folders sharing files");
            listed.AsSpan((int)first, (int)count).Fill(true);
            for (var i = first; i < first + count; i++)
            {
                var f = rows[i];
                if (!fileNames.TryGetValue(f.Name, out var file) || f.Paz >= paz.Length || f.Size > MaxEntry || f.Stored > MaxEntry
                        || (ulong)f.Offset + f.Stored > paz[f.Paz] || (f.Flags & 0xF) == 2 && f.Size > MaxRatio * f.Stored + 16)
                    throw new InvalidDataException("a file outside the tables");
                if ((budget -= dir.Length + 1 + file.Length) < 0) throw new InvalidDataException("names longer than the index holds");
                files.Add(new Entry(dir + "/" + file, f.Paz, f.Offset, f.Stored, f.Size, f.Flags));
            }
        }
        // the game's entries never share bytes: rows that would make a read cost more than the archive holds
        foreach (var g in files.Where(f => f.Stored > 0).GroupBy(f => f.Paz))
        {
            long end = 0;
            foreach (var f in g.OrderBy(f => f.Offset))
            {
                if (f.Offset < end) throw new InvalidDataException("files sharing bytes");
                end = (long)f.Offset + f.Stored;
            }
        }
        return new(hash, keyByte, paz, [.. folders.Select(f => folderNames[f.Name])], files);
    }

    /// <summary>Each node's full text by its offset; a parent comes before its children. <paramref name="budget"/>: the
    /// characters all texts may take, less theirs.</summary>
    static Dictionary<uint, string> Trie(ReadOnlySpan<byte> b, ref long budget)
    {
        var text = new Dictionary<uint, string>();
        var r = new Reader(b);
        while (!r.AtEnd)
        {
            var at = (uint)r.Position;
            var parent = r.U32();
            var own = Encoding.UTF8.GetString(r.Bytes(r.U8()));
            if (parent == uint.MaxValue) text[at] = own;
            else if (parent < at && text.TryGetValue(parent, out var p) && p.Length + own.Length <= MaxPath) text[at] = p + own;
            else throw new InvalidDataException("a name whose parent isn't before it, or too long");
            if ((budget -= text[at].Length) < 0) throw new InvalidDataException("names longer than the index holds");
        }
        return text;
    }

    /// <summary>The entry's bytes, decrypted with <paramref name="keyBase"/> and the group's <paramref name="keyByte"/>, then
    /// decompressed; null when its flags name a codec or cipher not read here, or it's encrypted and there's no key
    /// (<paramref name="why"/>).</summary>
    public static byte[]? Open(SafeFileHandle paz, Entry e, byte[]? keyBase, byte keyByte, out string? why)
    {
        why = e.Compression is not (0 or 2) ? $"compression {e.Compression}"
            : e.Encryption is not (0 or 3) ? $"encryption {e.Encryption}"
            : e.Encryption == 3 && keyBase == null ? "encrypted"
            : e.Compression == 0 && e.Stored != e.Size ? "a stored entry of two sizes"
            : e.Flags > 0xFF ? $"flags 0x{e.Flags:x}"
            : null;
        if (why != null) return null;
        if (e.Offset + (long)e.Stored > RandomAccess.GetLength(paz)) { why = "past the end of its .paz"; return null; }
        var stored = new byte[e.Stored];
        if (RandomAccess.Read(paz, stored, e.Offset) != stored.Length) { why = "past the end of its .paz"; return null; }
        // encrypted, then compressed: decrypt first
        if (e.Encryption == 3) PazCrypto.Decrypt(stored, e.Path, keyBase!, keyByte);
        if (e.Compression == 0) return stored;
        var raw = new byte[e.Size];
        if (LZ4Codec.Decode(stored, raw) != raw.Length) { why = "an LZ4 block that doesn't decompress to its size"; return null; }
        return raw;
    }

    ref struct Reader(ReadOnlySpan<byte> b)
    {
        readonly ReadOnlySpan<byte> b = b;
        int at;
        public readonly int Position => at;
        public readonly bool AtEnd => at == b.Length;
        ReadOnlySpan<byte> Take(long n)
        {
            if (n < 0 || n > b.Length - at) throw new InvalidDataException("a table past the end of the index");
            var s = b.Slice(at, (int)n);
            at += (int)n;
            return s;
        }
        public void Skip(long n) => Take(n);
        public ReadOnlySpan<byte> Bytes(long n) => Take(n);
        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        /// <summary>A count of rows of <paramref name="size"/> bytes the rest of the index can hold.</summary>
        public readonly int Count(uint n, int size) => n <= (uint)(b.Length - at) / (uint)size ? (int)n : throw new InvalidDataException("a count past the end of the index");
    }
}
