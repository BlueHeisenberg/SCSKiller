using System.Runtime.InteropServices;
using System.Text;

namespace SCSKiller.Core.App;

public sealed partial class AppLog
{
    private static partial string? ShortPath(string path)
    {
        var b = new StringBuilder(1024);
        try { return GetShortPathNameW(path, b, b.Capacity) is > 0 and < 1024 ? b.ToString() : null; }
        catch (Exception) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int size);
}
