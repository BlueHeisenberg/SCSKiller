using System.Buffers.Binary;
using System.Text;

namespace SCSKiller.Core.BlackSpace;

/// <summary>A BlackSpace archive group's index, &lt;group&gt;\0.pamt (measured on Crimson Desert, 34 groups). Little endian:
///   u32 crc, u32 archive count, u32 (unknown); per archive u32 index, u32 crc, u32 size (the &lt;index&gt;.paz beside it);
///   the folder name trie, then the file name trie: each u32 byte length, then nodes of u32 parent (the parent node's offset
///   in the trie, -1 for none), u8 length, the text. A name is its node's text after its parents' ("shadercache__" +
///   "/d3d12" + "/windows");
///   u32 folder count, per folder u32 name hash, u32 name node, u32 first file, u32 file count;
///   u32 file count, per file u32 name node, u32 archive offset, u32 stored size, u32 size, u16 archive, u16 flags
///   (<see cref="Stored"/>, <see cref="Lz4"/>; 0x30 on the render pass files, high-entropy, not read).
/// The file records end the index: any other length is another layout (InvalidDataException).</summary>
public sealed record Pamt(IReadOnlyList<Pamt.Archive> Archives, IReadOnlyList<Pamt.Folder> Folders, IReadOnlyList<Pamt.Entry> Files)
{
    public const string FileName = "0.pamt";
    public const ushort Stored = 0, Lz4 = 2;   // flags: as is, one raw LZ4 block of Size bytes
    const uint NoParent = uint.MaxValue;
    const int MaxDepth = 64;                   // nodes in one name (Crimson Desert: 4 at most)
    const int MaxFolderTrie = 16 << 20;        // what Folders reads at most (Crimson Desert: 42 bytes for the shader group)

    public sealed record Archive(uint Index, uint Crc, uint Size);
    public sealed record Folder(string Name, int FirstFile, int FileCount);
    public readonly record struct Entry(string Name, ushort Archive, uint Offset, uint StoredSize, uint Size, ushort Flags);

    public static Pamt Parse(byte[] b)
    {
        var at = 0;
        uint U32() { if (b.Length - at < 4) throw Bad("truncated"); var v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)); at += 4; return v; }
        ushort U16() { if (b.Length - at < 2) throw Bad("truncated"); var v = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)); at += 2; return v; }
        int Count(uint n, int recordSize) => n <= (uint)((b.Length - at) / recordSize) ? (int)n : throw Bad($"{n} records of {recordSize} bytes past the end");
        (int Start, int Length) Trie() { var n = U32(); if (n > (uint)(b.Length - at)) throw Bad("name trie past the end"); var t = (at, (int)n); at += (int)n; return t; }

        _ = U32();
        var nArchives = U32();
        _ = U32();
        var archives = new Archive[Count(nArchives, 12)];
        for (var i = 0; i < archives.Length; i++) archives[i] = new(U32(), U32(), U32());
        var folderNames = Trie();
        var fileNames = Trie();
        var folders = new Folder[Count(U32(), 16)];
        for (var i = 0; i < folders.Length; i++)
        {
            _ = U32();   // the name's hash
            var name = Name(b, folderNames, U32());
            var first = U32();
            var n = U32();
            folders[i] = new(name, (int)Math.Min(first, int.MaxValue), (int)Math.Min(n, int.MaxValue));
        }
        var files = new Entry[Count(U32(), 20)];
        for (var i = 0; i < files.Length; i++)
        {
            var node = U32();
            var offset = U32();
            var stored = U32();
            var size = U32();
            var archive = U16();
            var flags = U16();
            files[i] = new(Name(b, fileNames, node), archive, offset, stored, size, flags);
        }
        if (at != b.Length) throw Bad($"{b.Length - at} bytes after the file records");
        if (folders.FirstOrDefault(f => (long)f.FirstFile + f.FileCount > files.Length) is { } over) throw Bad($"folder {over.Name}'s files past the file records");
        return new(archives, folders, files);
    }

    /// <summary>Every name the folder trie of the index at <paramref name="path"/> holds, reading only up to its end (what
    /// Detect needs); null when the file isn't an index of this layout.</summary>
    public static IReadOnlyList<string>? FolderNames(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[12];
            if (f.ReadAtLeast(head, 12, false) < 12) return null;
            var tableEnd = 12 + 12L * BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            if (tableEnd + 4 > f.Length) return null;
            f.Position = tableEnd;
            Span<byte> len = stackalloc byte[4];
            f.ReadExactly(len);
            var n = BinaryPrimitives.ReadUInt32LittleEndian(len);
            if (n > MaxFolderTrie || tableEnd + 4 + n > f.Length) return null;
            var trie = new byte[n];
            f.ReadExactly(trie);
            var names = new List<string>();
            for (var node = 0; node < trie.Length; node += 5 + trie[node + 4])
            {
                if (trie.Length - node < 5) return null;
                names.Add(Name(trie, (0, trie.Length), (uint)node));
            }
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>The name ending at <paramref name="node"/> of a trie in <paramref name="b"/>.</summary>
    static string Name(byte[] b, (int Start, int Length) trie, uint node)
    {
        var parts = new List<string>(4);
        for (var depth = 0; node != NoParent; depth++)
        {
            if (depth == MaxDepth) throw Bad("name trie loops");
            if (node > (uint)trie.Length - 5u || trie.Length < 5) throw Bad($"name node {node} past its trie");
            var o = trie.Start + (int)node;
            var n = b[o + 4];
            if (node + 5u + n > (uint)trie.Length) throw Bad($"name node {node}'s text past its trie");
            parts.Add(Encoding.UTF8.GetString(b, o + 5, n));
            node = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        }
        parts.Reverse();
        return string.Concat(parts);
    }

    static InvalidDataException Bad(string why) => new($"not a BlackSpace archive index: {why}");
}
