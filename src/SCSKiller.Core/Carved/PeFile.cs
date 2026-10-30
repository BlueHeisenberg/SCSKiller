using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace SCSKiller.Core.Carved;

/// <summary>A PE file's export directory (IMAGE_EXPORT_DIRECTORY: 40 bytes, Name RVA at 12, NumberOfNames at 24,
/// AddressOfNames at 32).</summary>
public static class PeFile
{
    /// <summary>Read-only, while a game has the file loaded or a launcher replaces it.</summary>
    // Not a FileStream: PEReader maps its sections over 16 KB, and a read error in a mapped page (0xC0000006) ends the
    // process. A stream read throws IOException.
    // ponytail: reads a whole section (a UE exe's .rdata: 20-35 MB), a lazy RVA reader if HDD scans slow down
    // PEReader throws ArgumentException for a stream over 2 GB: a bigger file reads its first 2 GB, a section past them is BadImageFormat
    public static PEReader Open(string path)
    {
        var s = new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return new(s, PEStreamOptions.Default, s.Length > int.MaxValue ? int.MaxValue : 0);   // 0: the length at validation, a file shortened meanwhile too
    }

    /// <summary>What the DLL was linked as (kept when the file is renamed); null if none.</summary>
    public static string? ExportName(PEReader pe)
    {
        if (ExportDirectory(pe) is not { } d) return null;
        d.Offset = 12;
        return Str(pe, d.ReadInt32()) is { Length: > 0 } name ? name : null;
    }

    /// <summary>The exported names, in table order.</summary>
    public static IEnumerable<string> ExportNames(PEReader pe)
    {
        if (ExportDirectory(pe) is not { } d) yield break;
        d.Offset = 24;
        var count = d.ReadInt32();
        d.Offset = 32;
        var names = Data(pe, d.ReadInt32());
        for (var i = 0; i < count && names.RemainingBytes >= 4; i++) yield return Str(pe, names.ReadInt32());
    }

    /// <summary>The forwarded exports' targets ("dll.Function", "dll.#ordinal"), in table order.</summary>
    public static IEnumerable<string> Forwarders(PEReader pe)
    {
        if (ExportDirectory(pe) is not { } d) yield break;
        var dir = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        d.Offset = 20;
        var count = d.ReadInt32();
        d.Offset = 28;
        var functions = Data(pe, d.ReadInt32());
        for (var i = 0; i < count && functions.RemainingBytes >= 4; i++)
            if (functions.ReadInt32() is var rva && rva >= dir.RelativeVirtualAddress && rva < dir.RelativeVirtualAddress + dir.Size)
                yield return Str(pe, rva);   // an RVA inside the export directory is a forwarder string
    }

    /// <summary>The functions a PE32+ imports from <paramref name="dll"/> (its import and delay-load import tables): an import
    /// by ordinal as "#" and its ordinal.</summary>
    public static IEnumerable<string> ImportedFunctions(PEReader pe, string dll)
    {
        var h = pe.PEHeaders.PEHeader;
        if (h == null || h.Magic != PEMagic.PE32Plus) yield break;
        foreach (var delay in new[] { false, true })
        {
            var table = Data(pe, (delay ? h.DelayImportTableDirectory : h.ImportTableDirectory).RelativeVirtualAddress);
            // IMAGE_IMPORT_DESCRIPTOR {names, time, chain, dll, iat}, IMAGE_DELAYLOAD_DESCRIPTOR {attributes, dll, handle, iat, names, ...}
            for (var size = delay ? 32 : 20; table.RemainingBytes >= size;)
            {
                var d = new int[size / 4];
                for (var i = 0; i < d.Length; i++) d[i] = table.ReadInt32();
                var (name, names) = delay ? (d[1], d[4]) : (d[3], d[0] != 0 ? d[0] : d[4]);
                if (name == 0) break;
                if (!Str(pe, name).Equals(dll, StringComparison.OrdinalIgnoreCase)) continue;
                var thunks = Data(pe, names);
                for (ulong t; thunks.RemainingBytes >= 8 && (t = thunks.ReadUInt64()) != 0;)
                    yield return t >> 63 == 0 ? Str(pe, (int)(t & 0x7FFFFFFF) + 2) : $"#{t & 0xFFFF}";   // past the 2-byte hint
            }
        }
    }

    /// <summary>The data an exported variable's RVA points at (e.g. the Agility SDK's D3D12SDKVersion); null if not exported.</summary>
    public static BlobReader? ExportData(PEReader pe, string name)
    {
        if (ExportDirectory(pe) is not { } d) return null;
        d.Offset = 24;
        var count = d.ReadInt32();
        var (functions, names, ordinals) = (d.ReadInt32(), d.ReadInt32(), d.ReadInt32());   // AddressOfFunctions, Names, NameOrdinals
        var n = Data(pe, names);
        for (var i = 0; i < count && n.RemainingBytes >= 4; i++)
            if (Str(pe, n.ReadInt32()) == name)
                return Data(pe, Data(pe, functions + 4 * Data(pe, ordinals + 2 * i).ReadUInt16()).ReadInt32());
        return null;
    }

    /// <summary>The string a `const char*` export points at (e.g. the Agility SDK's D3D12SDKPath); null if not exported.</summary>
    public static string? ExportedString(PEReader pe, string name)
    {
        if (ExportData(pe, name) is not { } p) return null;
        var va = pe.PEHeaders.PEHeader!.Magic == PEMagic.PE32Plus ? p.ReadUInt64() : p.ReadUInt32();
        return Str(pe, (int)(va - pe.PEHeaders.PEHeader.ImageBase));
    }

    /// <summary>The NUL-terminated string at an RVA, at most 256 characters.</summary>
    internal static string Str(PEReader pe, int rva)
    {
        var r = Data(pe, rva);
        var sb = new StringBuilder();
        for (byte b; r.RemainingBytes > 0 && sb.Length < 256 && (b = r.ReadByte()) != 0;) sb.Append((char)b);
        return sb.ToString();
    }

    static BlobReader? ExportDirectory(PEReader pe) =>
        Data(pe, pe.PEHeaders.PEHeader?.ExportTableDirectory.RelativeVirtualAddress ?? 0) is { RemainingBytes: >= 40 } d ? d : null;

    static BlobReader Data(PEReader pe, int rva) => rva <= 0 ? default : pe.GetSectionData(rva).GetReader();
}
