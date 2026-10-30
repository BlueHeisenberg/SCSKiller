using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.Carved;

/// <summary>The version resource fields Core reads, as FileVersionInfo gives them: names null and parts 0 for a file without
/// one, names empty for one whose string tables Windows' lookup doesn't find.</summary>
public sealed partial record PeVersionInfo(int FileMajorPart, int FileMinorPart, int FileBuildPart, int FilePrivatePart,
    string? ProductName, string? FileDescription, string? OriginalFilename)
{
    /// <summary>A file without a version resource.</summary>
    public static readonly PeVersionInfo None = new(0, 0, 0, 0, null, null, null);

    /// <summary>FileVersionInfo.GetVersionInfo's fields: FileVersionInfo's own on Windows, <see cref="ReadManaged"/> elsewhere,
    /// where FileVersionInfo reads no native PE's resource.</summary>
    public static partial PeVersionInfo GetVersionInfo(string fileName);

    /// <summary>The PE's own RT_VERSION resource (a MUI satellite's isn't followed) read as GetFileVersionInfoEx and
    /// FileVersionInfo read it, <see cref="None"/> when it has none, is malformed or can't be read. Throws what FileVersionInfo
    /// throws: ArgumentException for an invalid path, FileNotFoundException for a missing file.</summary>
    internal static PeVersionInfo ReadManaged(string fileName)
    {
        fileName = Path.GetFullPath(fileName);
        if (!File.Exists(fileName)) throw new FileNotFoundException(fileName);
        try
        {
            using var file = Games.GameFiles.OpenRegular(fileName);
            return file != null ? Read(file) : None;
        }
        // FileVersionInfo gives a file it can't open or read no version info, on Windows and elsewhere
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return None; }
    }

    /// <summary><see cref="ReadManaged"/> on a seekable stream of the file.</summary>
    internal static PeVersionInfo Read(Stream pe)
    {
        try { return Resource(pe) is { } data ? Parse(data) : None; }
        // ArgumentOutOfRangeException: PEHeaders on a file cut short after its length was read
        catch (Exception e) when (e is BadImageFormatException or Malformed or ArgumentOutOfRangeException) { return None; }
    }

    sealed class Malformed : Exception;

    // VS_VERSIONINFO's wLength is a WORD: a well-formed one reads nothing past it
    const int MaxVersionBytes = 1 << 16;

    // FindResourceW(VS_VERSION_INFO, RT_VERSION) on a module loaded as a data file
    static byte[]? Resource(Stream stream)
    {
        var headers = new PEHeaders(stream, (int)Math.Min(stream.Length, int.MaxValue));   // the headers of a file past 2 GB too
        if (headers.PEHeader is not { NumberOfRvaAndSizes: > 2, ResourceTableDirectory: { Size: >= 16 } dir }) return null;
        var pe = new Image(stream, headers);
        var root = pe.Offset(dir.RelativeVirtualAddress);
        var type = Entry(pe, root, root, 16, wantDir: true);
        var name = type < 0 ? -1 : Entry(pe, root, type, 1, wantDir: true);
        if (name < 0) return null;
        // the languages a language-neutral lookup tries that don't depend on the Windows UI or system language, then the first
        var data = Entry(pe, root, name, 0, wantDir: false) is var neutral and >= 0 ? neutral
            : Entry(pe, root, name, 0x0409, wantDir: false) is var english and >= 0 ? english : First(pe, root, name);
        if (data < 0) return null;
        long at = pe.Offset(pe.U32(data)), size = pe.U32(data + 4);
        if (at + size > pe.Length) throw new Malformed();
        var bytes = new byte[Math.Min(size, MaxVersionBytes)];
        pe.Read(at, bytes);
        return bytes;
    }

    // find_entry_by_id: a binary search of the ID entries (sorted, after the named ones) by the low word of the ID
    static long Entry(Image pe, long root, long dir, ushort id, bool wantDir)
    {
        int lo = pe.U16(dir + 12), hi = lo + pe.U16(dir + 14) - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var entry = dir + 16 + 8L * mid;
            var at = pe.U16(entry);
            if (at == id) return Target(pe, root, entry, wantDir);
            if (at > id) hi = mid - 1;
            else lo = mid + 1;
        }
        return -1;
    }

    static long First(Image pe, long root, long dir)
    {
        var count = pe.U16(dir + 12) + pe.U16(dir + 14);
        for (var i = 0; i < count; i++)
            if (Target(pe, root, dir + 16 + 8L * i, wantDir: false) is var t and >= 0) return t;
        return -1;
    }

    // OffsetToData's high bit marks a subdirectory; offsets are from the resource root
    static long Target(Image pe, long root, long entry, bool wantDir) =>
        pe.U32(entry + 4) is var to && (to >> 31 == 1) == wantDir ? root + (to & 0x7FFFFFFF) : -1;

    sealed class Image(Stream stream, PEHeaders headers)
    {
        public readonly long Length = stream.Length;

        // RtlImageRvaToVa on a data file: the first section whose raw data holds the RVA
        public long Offset(long rva)
        {
            foreach (var s in headers.SectionHeaders)
                if (rva >= (uint)s.VirtualAddress && rva < (long)(uint)s.VirtualAddress + (uint)s.SizeOfRawData)
                    return (uint)s.PointerToRawData + rva - (uint)s.VirtualAddress;
            throw new Malformed();
        }

        public void Read(long at, Span<byte> into)
        {
            if (at < 0 || at > Length - into.Length) throw new Malformed();
            stream.Position = at;
            stream.ReadExactly(into);
        }

        public ushort U16(long at)
        {
            Span<byte> b = stackalloc byte[2];
            Read(at, b);
            return BinaryPrimitives.ReadUInt16LittleEndian(b);
        }

        public uint U32(long at)
        {
            Span<byte> b = stackalloc byte[4];
            Read(at, b);
            return BinaryPrimitives.ReadUInt32LittleEndian(b);
        }
    }

    // FileVersionInfo's choice: the table of the first Translation entry (040904E4 without one), else the first of its
    // fallbacks with a FileVersion string; when none has one, the names stay those of the last table tried
    static PeVersionInfo Parse(byte[] d)
    {
        // VerQueryValueW takes a block whose byte 4 (wType's low byte in a 32-bit one) is a character as a 16-bit block
        if (d.Length < 6 || d[4] >= ' ') throw new Malformed();
        var fixedInfo = Value(d, 0);
        if (ValueBytes(d, 0) < 52 || U32(d, fixedInfo) != 0xFEEF04BD) throw new Malformed();
        uint ms = U32(d, fixedInfo + 8), ls = U32(d, fixedInfo + 12);
        var translation = Find(d, 0, "VarFileInfo") is var vars and >= 0 && Find(d, vars, "Translation") is var t and >= 0
            ? (uint)U16(d, Value(d, t)) << 16 | U16(d, Value(d, t) + 2) : 0x040904E4;
        var names = Table(d, translation);
        if (names.FileVersion.Length == 0)
            foreach (var fallback in (uint[])[0x040904B0, 0x040904E4, 0x04090000])
                if (fallback != translation && (names = Table(d, fallback)).FileVersion.Length > 0) break;
        return new((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF), names.Product, names.Description, names.Original);
    }

    static (string FileVersion, string Product, string Description, string Original) Table(byte[] d, uint codePage)
    {
        var table = Find(d, 0, "StringFileInfo") is var s and >= 0 ? Find(d, s, codePage.ToString("X8")) : -1;
        return (Text(d, table, "FileVersion"), Text(d, table, "ProductName"), Text(d, table, "FileDescription"), Text(d, table, "OriginalFilename"));
    }

    // FileVersionInfo reads the value up to its NUL, Windows gives none for a wValueLength of 0; here never past its block
    static string Text(byte[] d, int table, string key)
    {
        var s = table < 0 ? -1 : Find(d, table, key);
        if (s < 0 || U16(d, s + 2) == 0) return "";
        int at = Value(d, s), end = Math.Min(s + U16(d, s), d.Length);
        if (at >= end) return "";
        var chars = MemoryMarshal.Cast<byte, char>(d.AsSpan(at, (end - at) & ~1));
        return new(chars.IndexOf('\0') is var nul and >= 0 ? chars[..nul] : chars);
    }

    // VerQueryValueW's step: the first child whose key matches, ASCII case aside; a child of length 0 ends the search
    static int Find(byte[] d, int block, string key)
    {
        var end = block + U16(d, block);
        for (var child = Value(d, block) + Align(ValueBytes(d, block)); child < end;)
        {
            if (KeyIs(d, child, key)) return child;
            var length = U16(d, child);
            if (length == 0) return -1;
            child += Align(length);
        }
        return -1;
    }

    static bool KeyIs(byte[] d, int block, string key)
    {
        for (var i = 0; i < key.Length; i++)
            if (Lower(U16(d, block + 6 + 2 * i)) != Lower(key[i])) return false;
        return U16(d, block + 6 + 2 * key.Length) == 0;
        static int Lower(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;
    }

    // past the header and the NUL-terminated key, DWORD-aligned
    static int Value(byte[] d, int block)
    {
        var at = block + 6;
        while (U16(d, at) != 0) at += 2;
        return block + Align(at + 2 - block);
    }

    // wValueLength counts characters in a text block (wType nonzero)
    static int ValueBytes(byte[] d, int block) => U16(d, block + 2) * (U16(d, block + 4) != 0 ? 2 : 1);

    static int Align(int n) => (n + 3) & ~3;

    static ushort U16(byte[] d, int at) => at >= 0 && at <= d.Length - 2 ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at)) : throw new Malformed();

    static uint U32(byte[] d, int at) => at >= 0 && at <= d.Length - 4 ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) : throw new Malformed();
}
