using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

public static partial class GameFiles
{
    /// <summary>How the file system tells names apart: two that differ in case alone are two files.</summary>
    internal const StringComparison Names = StringComparison.Ordinal;

    public static partial string? FinalPath(string dir) => Libc.RealPath(dir);

    public static partial string? VolumeOf(string path)
    {
        for (var d = path.Length > 0 ? Path.GetFullPath(path) : null; d != null; d = Path.GetDirectoryName(d))
            if (Mounts.Of(d) is { Point.Length: > 0 } m) return m.Point;
        return null;
    }

    internal static partial Microsoft.Win32.SafeHandles.SafeFileHandle OpenUnlinked(string path) => Libc.OpenUnlinked(path);

    internal static partial FileStream? OpenRegular(string path)
    {
        if (Libc.OpenRegular(path) is not { } h) return null;
        try { return new FileStream(h, FileAccess.Read); }
        catch { h.Dispose(); throw; }
    }

    // Wine's file id is the inode of the file its path names, links followed (fstat on what it opened)
    public static partial (byte[]? Id, string? Final)? Identity(string path) =>
        Libc.RealPath(path) is { } final ? (Libc.Stat(final) is { } s ? WineFileId(s.Inode) : null, final) : null;

    internal static partial string? SystemFolder(Game game) =>
        SteamSource.PrefixFolder(game) is { } pfx ? Path.Combine(pfx, "drive_c", "windows", "system32") : null;

    // the game's prefix, a shortcut's, ~/.wine or truckersmp-cli's: no search covers every prefix TruckersMP can run from
    internal static partial bool TruckersMpHere() => true;
}
