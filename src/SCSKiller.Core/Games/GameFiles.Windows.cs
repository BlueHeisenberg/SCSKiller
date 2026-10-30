using Microsoft.Win32;

namespace SCSKiller.Core.Games;

public static partial class GameFiles
{
    /// <summary>How the file system tells names apart: case aside.</summary>
    internal const StringComparison Names = StringComparison.OrdinalIgnoreCase;

    internal static partial string? SystemFolder(Game game) => Environment.SystemDirectory;

    public static partial string? VolumeOf(string path) => Path.GetPathRoot(path) is { Length: > 0 } root ? root : null;

    public static partial string? FinalPath(string dir)
    {
        using var h = CreateFileW(dir, 0, 7 /* FILE_SHARE_READ | WRITE | DELETE */, 0, 3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, 0);
        return h.IsInvalid ? null : FinalPath(h);
    }

    public static partial (byte[]? Id, string? Final)? Identity(string path)
    {
        using var h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7, 0, 3, 0x02000000, 0);
        if (h.IsInvalid) return null;
        var id = new byte[24];
        if (!GetFileInformationByHandleEx(h, 0x12 /* FileIdInfo */, id, id.Length)) return (null, FinalPath(h));
        if (Vendors.Wine.Running) WineVolume.CopyTo(id);
        return (id, FinalPath(h));
    }

    internal static partial Microsoft.Win32.SafeHandles.SafeFileHandle OpenUnlinked(string path)
    {
        var h = CreateFileW(path, 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 0, 0, 3, 0x00200000 /* FILE_FLAG_OPEN_REPARSE_POINT */, 0);
        if (h.IsInvalid)
        {
            var hr = System.Runtime.InteropServices.Marshal.GetHRForLastWin32Error();
            h.Dispose();
            throw new IOException($"couldn't open {path}", hr);
        }
        var (tag, standard) = (new byte[8], new byte[24]);
        if (!GetFileInformationByHandleEx(h, 9 /* FileAttributeTagInfo */, tag, tag.Length) || !GetFileInformationByHandleEx(h, 1 /* FileStandardInfo */, standard, standard.Length)
            || (BitConverter.ToUInt32(tag, 0) & 0x400) != 0 && (BitConverter.ToUInt32(tag, 4) & 0x20000000) != 0   // a name surrogate's reparse tag: a symlink or junction
            || BitConverter.ToUInt32(standard, 16) != 1)   // NumberOfLinks
        {
            h.Dispose();
            throw new IOException($"{path} is a link: not written");
        }
        return h;
    }

    static string? FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        var name = new char[32768];
        var n = GetFinalPathNameByHandleW(h, name, name.Length, 0);
        if (n == 0 || n >= name.Length) return null;
        var path = new string(name, 0, (int)n);
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
             : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    internal static partial bool TruckersMpHere() => TruckersMp(TruckersMpFolders(), InstalledPrograms());

    internal static partial FileStream? OpenRegular(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // the launcher's mod files (Roaming\TruckersMP\installation; the older launcher's under ProgramData), its own data, and
    // the multiplayer profiles in Documents
    static IEnumerable<string> TruckersMpFolders() =>
        new (Environment.SpecialFolder Root, string Name)[] { (Environment.SpecialFolder.ApplicationData, "TruckersMP"), (Environment.SpecialFolder.LocalApplicationData, "TruckersMP"),
            (Environment.SpecialFolder.CommonApplicationData, "TruckersMP"), (Environment.SpecialFolder.MyDocuments, "ETS2MP"), (Environment.SpecialFolder.MyDocuments, "ATSMP") }
        .Select(f => (Root: Environment.GetFolderPath(f.Root), f.Name)).Where(f => f.Root.Length > 0).Select(f => Path.Combine(f.Root, f.Name));

    /// <summary>The display names of the programs in Windows' uninstall list, per user and per machine, both registry views.</summary>
    static IEnumerable<string> InstalledPrograms()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var b = RegistryKey.OpenBaseKey(hive, view);
            using var root = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var sub in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(sub);
                if (key?.GetValue("DisplayName") is string { Length: > 0 } name) yield return name;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle h, char[] name, int size, uint flags);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass, byte[] info, int size);
}
