using System.Runtime.InteropServices;

namespace SCSKiller.Core.App;

public static partial class KeyFiles
{
    public static partial string Identity(string path)
    {
        using var h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES: no sharing conflict */, 7, 0, 3 /* OPEN_EXISTING */, 0, 0);
        return h.IsInvalid ? "" : Identity(h);
    }

    private static partial string Identity(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        var changed = GetFileInformationByHandleEx(h, 0 /* FileBasicInfo */, out BasicInfo b, Marshal.SizeOf<BasicInfo>()) ? b.Changed.ToString() : "";
        var id = GetFileInformationByHandleEx(h, 18 /* FileIdInfo */, out IdInfo i, Marshal.SizeOf<IdInfo>()) ? $"{i.Volume:x}-{i.High:x16}{i.Low:x16}" : "";
        return $"{changed}/{id}";
    }

    [StructLayout(LayoutKind.Sequential)] struct BasicInfo { public long Created, Accessed, Written, Changed; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct IdInfo { public ulong Volume, Low, High; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass, out BasicInfo info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass, out IdInfo info, int size);
}
