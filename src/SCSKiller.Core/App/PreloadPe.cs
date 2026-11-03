using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace SCSKiller.Core.App;

/// <summary>The recorder's preloader (proxy/preload.cpp) under the name of another System32 DLL a Streamline game's exe
/// imports statically: the built preloader with one more section, an export table that copies the System32 DLL's (Base,
/// every ordinal, every name) onto the preloader's stubs. Its DllMain loads System32's DLL of the table's name and points
/// each stub at that DLL's export of the same ordinal and name, as it does for the names it is built under.</summary>
public static class PreloadPe
{
    /// <summary>proxy/preload.asm's stubs (preload.cpp's kStubs), 8 bytes apart from the first.</summary>
    public const int Stubs = 1024;
    const int StubSize = 8;

    /// <summary>The preloader <paramref name="template"/> (a built one, its export table mapped from its first stub on) with
    /// <paramref name="real"/>'s export table. Null when the preloader can't stand in for it: an export outside code (a jump
    /// stub can't be data), more exports than stubs, not x64, or a result too large for <see cref="ScsKiller.IsOurPreload"/>.</summary>
    public static byte[]? Build(byte[] template, string real)
    {
        int s0, textEnd;
        using (var t = new PEReader(new MemoryStream(template)))
        {
            if (t.PEHeaders.CoffHeader.Machine != Machine.Amd64 || Exports(t) is not { } own) return null;
            s0 = own.Functions.Where(f => f != 0).DefaultIfEmpty(0).Min();
            var text = t.PEHeaders.SectionHeaders.FirstOrDefault(h => s0 > 0 && s0 >= h.VirtualAddress && s0 < h.VirtualAddress + h.VirtualSize);
            textEnd = text.VirtualAddress + text.VirtualSize;   // none: 0
        }
        if (s0 == 0 || s0 + StubSize * (Stubs - 1) + 6 > textEnd) return null;   // a build with fewer stubs (each a 6-byte jmp)

        ExportTable ex;
        using (var r = Carved.PeFile.Open(real))
        {
            if (r.PEHeaders.CoffHeader.Machine != Machine.Amd64 || Exports(r) is not { } e || e.Functions.Count(f => f != 0) > Stubs) return null;
            var folder = Path.GetDirectoryName(real)!;
            for (var i = 0; i < e.Functions.Length; i++)
                if (e.Functions[i] != 0 && !Code(r, folder, e.Functions[i], 0)) return null;   // a data export
            ex = e;
        }

        // the section: IMAGE_EXPORT_DIRECTORY, functions, names, name ordinals, the DLL's name, the export names
        var dllName = Path.GetFileName(real).ToLowerInvariant();
        int nf = ex.Functions.Length, nn = ex.Names.Length;
        int fOff = 40, nOff = fOff + 4 * nf, oOff = nOff + 4 * nn, sOff = oOff + 2 * nn;
        var strings = new MemoryStream();
        void Str(string s) { strings.Write(Encoding.ASCII.GetBytes(s)); strings.WriteByte(0); }
        Str(dllName);
        var nameAt = new int[nn];
        for (var i = 0; i < nn; i++) { nameAt[i] = (int)strings.Length; Str(ex.Names[i]); }
        var content = new byte[sOff + strings.Length];
        strings.ToArray().CopyTo(content, sOff);

        var pe = template;
        int lfanew = I32(pe, 0x3C), coff = lfanew + 4, sections = U16(pe, coff + 2), opt = coff + 20, optSize = U16(pe, coff + 16);
        if (U16(pe, opt) != 0x20B || I32(pe, opt + 108) < 5) return null;
        int sectionAlign = I32(pe, opt + 32), fileAlign = I32(pe, opt + 36), headers = I32(pe, opt + 60);
        int table = opt + optSize, slot = table + 40 * sections;
        if (slot + 40 > headers || pe.AsSpan(slot, 40).ContainsAnyExcept((byte)0)) return null;   // no room for another section header
        int vaEnd = 0, rawEnd = 0;
        for (var i = 0; i < sections; i++)
        {
            var h = table + 40 * i;
            vaEnd = Math.Max(vaEnd, I32(pe, h + 12) + Math.Max(I32(pe, h + 8), I32(pe, h + 16)));
            rawEnd = Math.Max(rawEnd, I32(pe, h + 20) + I32(pe, h + 16));
        }
        int va = Align(vaEnd, sectionAlign), raw = Align(rawEnd, fileAlign), rawSize = Align(content.Length, fileAlign);

        var w = content.AsSpan();
        W32(w, 12, va + sOff);   // Name
        W32(w, 16, ex.Base);
        W32(w, 20, nf);
        W32(w, 24, nn);
        W32(w, 28, va + fOff);
        W32(w, 32, va + nOff);
        W32(w, 36, va + oOff);
        for (int i = 0, stub = 0; i < nf; i++)
            W32(w, fOff + 4 * i, ex.Functions[i] == 0 ? 0 : s0 + StubSize * stub++);
        for (var i = 0; i < nn; i++)
        {
            W32(w, nOff + 4 * i, va + sOff + nameAt[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(w[(oOff + 2 * i)..], ex.Ordinals[i]);
        }

        // the image as linked up to its last section's data: a certificate (security directory) past it doesn't fit the new bytes
        var o = new byte[raw + rawSize];
        pe.AsSpan(0, Math.Min(rawEnd, pe.Length)).CopyTo(o);
        content.CopyTo(o, raw);
        var s = o.AsSpan();
        Encoding.ASCII.GetBytes(".scsexp").CopyTo(s[slot..]);
        W32(s, slot + 8, content.Length);
        W32(s, slot + 12, va);
        W32(s, slot + 16, rawSize);
        W32(s, slot + 20, raw);
        W32(s, slot + 36, unchecked((int)0x40000040));   // initialized data, read
        BinaryPrimitives.WriteUInt16LittleEndian(s[(coff + 2)..], (ushort)(sections + 1));
        W32(s, opt + 56, Align(va + content.Length, sectionAlign));   // SizeOfImage
        W32(s, opt + 64, 0);   // CheckSum
        W32(s, opt + 112, va);   // the export directory
        W32(s, opt + 116, content.Length);
        W32(s, opt + 112 + 8 * 4, 0);   // the security directory
        W32(s, opt + 116 + 8 * 4, 0);
        return o.Length < 65536 ? o : null;
    }

    sealed record ExportTable(int Base, int[] Functions, string[] Names, ushort[] Ordinals);

    /// <summary>The export at <paramref name="rva"/> is code: in an executable section, or a forwarder ("dll.Name",
    /// "dll.#ordinal") to code in a DLL of <paramref name="folder"/>. One that can't be followed (an API set, a chain over
    /// four deep) counts as data.</summary>
    static bool Code(PEReader pe, string folder, int rva, int depth)
    {
        var dir = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        if (rva < dir.RelativeVirtualAddress || rva >= dir.RelativeVirtualAddress + dir.Size)
            return (pe.PEHeaders.SectionHeaders.FirstOrDefault(h => rva >= h.VirtualAddress && rva < h.VirtualAddress + Math.Max(h.VirtualSize, h.SizeOfRawData))
                .SectionCharacteristics & SectionCharacteristics.MemExecute) != 0;
        var target = Carved.PeFile.Str(pe, rva);
        var dot = target.LastIndexOf('.');
        if (depth >= 4 || dot <= 0 || target.Contains('\\') || target.Contains('/')) return false;
        var dll = target[..dot] + ".dll";
        var path = Path.Combine(folder, dll);
        if (dll.StartsWith("api-", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        using var t = Carved.PeFile.Open(path);
        if (Exports(t) is not { } e) return false;
        var name = target[(dot + 1)..];
        int index;
        if (name.StartsWith('#')) index = int.TryParse(name[1..], out var ordinal) ? ordinal - e.Base : -1;
        else index = Array.IndexOf(e.Names, name) is var n and >= 0 ? e.Ordinals[n] : -1;
        return index >= 0 && index < e.Functions.Length && e.Functions[index] != 0 && Code(t, folder, e.Functions[index], depth + 1);
    }

    static ExportTable? Exports(PEReader pe)
    {
        var dir = pe.PEHeaders.PEHeader?.ExportTableDirectory ?? default;
        if (dir.RelativeVirtualAddress == 0 || dir.Size < 40) return null;
        var d = pe.GetSectionData(dir.RelativeVirtualAddress).GetReader();
        if (d.RemainingBytes < 40) return null;
        d.Offset = 16;
        int @base = d.ReadInt32(), nf = d.ReadInt32(), nn = d.ReadInt32(), af = d.ReadInt32(), an = d.ReadInt32(), ao = d.ReadInt32();
        if (nf is < 0 or > 65536 || nn < 0 || nn > nf) return null;
        var f = pe.GetSectionData(af).GetReader();
        var n = pe.GetSectionData(an).GetReader();
        var o = pe.GetSectionData(ao).GetReader();
        if (f.RemainingBytes < 4 * nf || n.RemainingBytes < 4 * nn || o.RemainingBytes < 2 * nn) return null;
        var functions = new int[nf];
        for (var i = 0; i < nf; i++) functions[i] = f.ReadInt32();
        var names = new string[nn];
        var ordinals = new ushort[nn];
        for (var i = 0; i < nn; i++)
        {
            names[i] = Carved.PeFile.Str(pe, n.ReadInt32());
            ordinals[i] = o.ReadUInt16();
            if (names[i].Length == 0 || ordinals[i] >= nf || functions[ordinals[i]] == 0) return null;   // the preloader would refuse to load
        }
        return new(@base, functions, names, ordinals);
    }

    static int Align(int v, int a) => (v + a - 1) / a * a;
    static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
    static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    static void W32(Span<byte> b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b[at..], v);

    /// <summary>A name the loader never takes from beside the exe (a KnownDLL), or one the preloader mustn't stand in for: API
    /// sets, the graphics APIs and GPU driver and upscaler DLLs (the recorder's and Streamline's own loads), the runtime and
    /// CRT, the names the preloader is built under, and anything but a bare file name. <paramref name="name"/>: lower case,
    /// with its extension.</summary>
    public static bool Unfit(string name) => name.IndexOfAny(['\\', '/', ':']) >= 0 ||name.StartsWith("api-ms-", StringComparison.Ordinal) || name.StartsWith("ext-ms-", StringComparison.Ordinal)
        || !name.EndsWith(".dll", StringComparison.Ordinal) || Denied.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || KnownDlls.Value.Contains(name);

    /// <summary>A KnownDLL: <paramref name="name"/> in any case, with its extension.</summary>
    public static bool Known(string name) => KnownDlls.Value.Contains(name);

    static readonly string[] Denied = ["d3d", "dxgi", "dxcore", "dxil", "dxcompiler", "dxva", "vulkan", "opengl", "sl.", "nv", "amd", "ati", "ig", "intel",
        "ntdll", "kernel", "msvcp", "msvcr", "vcruntime", "ucrtbase", "concrt", "vcomp", "mscoree", "clr", "winmm", "version", "dinput8"];

    // HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs, and what \KnownDlls adds as their imports (Windows 11)
    static readonly string[] KnownFixed = ["advapi32.dll", "bcrypt.dll", "bcryptprimitives.dll", "cfgmgr32.dll", "clbcatq.dll", "combase.dll",
        "comctl32.dll", "comdlg32.dll", "coml2.dll", "crypt32.dll", "difxapi.dll", "gdi32.dll", "gdi32full.dll", "gdiplus.dll", "imagehlp.dll", "imm32.dll",
        "kernel.appcore.dll", "kernel32.dll", "kernelbase.dll", "msasn1.dll", "msctf.dll", "msvcp_win.dll", "msvcrt.dll", "normaliz.dll",
        "nsi.dll", "ntdll.dll", "ole32.dll", "oleaut32.dll", "powrprof.dll", "profapi.dll", "psapi.dll", "rpcrt4.dll", "sechost.dll",
        "setupapi.dll", "shcore.dll", "shell32.dll", "shlwapi.dll", "ucrtbase.dll", "umpdc.dll", "user32.dll", "win32u.dll",
        "windows.storage.dll", "wintrust.dll", "wldap32.dll", "wow64.dll", "wow64base.dll", "wow64con.dll", "wow64cpu.dll",
        "wow64win.dll", "ws2_32.dll", "wldp.dll"];

    static readonly Lazy<HashSet<string>> KnownDlls = new(() =>
    {
        var known = new HashSet<string>(KnownFixed, StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
            try { known.UnionWith(KnownDllSections()); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        return known;
    });

    /// <summary>The \KnownDlls object directory's sections: the DLLs the loader maps from there whatever is beside the exe.</summary>
    static List<string> KnownDllSections()
    {
        var names = new List<string>();
        var dir = Marshal.StringToHGlobalUni(@"\KnownDlls");
        var us = new UnicodeString { Length = (ushort)(2 * @"\KnownDlls".Length), MaximumLength = (ushort)(2 * @"\KnownDlls".Length + 2), Buffer = dir };
        var usPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var buffer = Marshal.AllocHGlobal(1 << 16);
        try
        {
            Marshal.StructureToPtr(us, usPtr, false);
            var oa = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), ObjectName = usPtr };
            if (NtOpenDirectoryObject(out var h, 1 /* DIRECTORY_QUERY */, ref oa) != 0) throw new InvalidOperationException("\\KnownDlls");
            try
            {
                uint context = 0;
                for (var first = true; ; first = false)
                {
                    var status = NtQueryDirectoryObject(h, buffer, 1 << 16, false, first, ref context, out _);
                    if (status is not (0 or 0x105)) break;   // STATUS_MORE_ENTRIES: another call continues
                    for (var p = buffer; ; p += 2 * Marshal.SizeOf<UnicodeString>())
                    {
                        var name = Marshal.PtrToStructure<UnicodeString>(p);
                        if (name.Buffer == 0) break;
                        names.Add(Marshal.PtrToStringUni(name.Buffer, name.Length / 2));
                    }
                    if (status == 0) break;
                }
            }
            finally { NtClose(h); }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(usPtr);
            Marshal.FreeHGlobal(dir);
        }
        return names;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes { public int Length; public nint RootDirectory, ObjectName; public uint Attributes; public nint SecurityDescriptor, SecurityQualityOfService; }

    [DllImport("ntdll.dll")] static extern int NtOpenDirectoryObject(out nint handle, uint access, ref ObjectAttributes attributes);
    [DllImport("ntdll.dll")] static extern int NtQueryDirectoryObject(nint handle, nint buffer, int length, [MarshalAs(UnmanagedType.U1)] bool single,
        [MarshalAs(UnmanagedType.U1)] bool restart, ref uint context, out uint returned);
    [DllImport("ntdll.dll")] static extern int NtClose(nint handle);
}
