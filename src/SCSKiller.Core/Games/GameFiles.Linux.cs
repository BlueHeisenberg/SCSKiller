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
        PrefixOf(game) is { } pfx ? Path.Combine(pfx, "drive_c", "windows", "system32") : null;

    internal static partial string? PrefixOf(Game game) =>
        SteamShortcuts.AppId(game.Id) is { } app ? Path.Combine(ProtonContext.ShortcutData(SteamSource.Root(), app), "pfx")
        : LauncherPrefix.Is(game) ? LauncherPrefix.Of(game, out _)?.WineDir
        : SteamSource.PrefixFolder(game);

    internal static partial LauncherPrefix? AddedPrefix(Game game, out string? why)
    {
        why = null;
        if (WinePrefix.Holding(game.ExePath) is not { } dir)
        {
            why = "it isn't in a Wine prefix";
            return null;
        }
        // a Proton prefix is its compat data's pfx, or under umu the compat data itself with pfx linking back to it
        var parent = Path.GetDirectoryName(dir);
        foreach (var data in new[] { Path.GetFileName(dir) == "pfx" ? parent : null, FinalPath(Path.Combine(dir, "pfx")) is { } pfx && pfx == FinalPath(dir) ? dir : null })
            if (data != null && SteamSource.LastProtonFolder(data) is { } proton) return new LauncherPrefix("its launcher", data, proton, true, null);
        why = $"its Wine prefix {dir} names no Proton that runs it: add it to Steam as a non-Steam game";
        return null;
    }

    // the game's prefix, a shortcut's, ~/.wine or truckersmp-cli's: no search covers every prefix TruckersMP can run from
    internal static partial bool TruckersMpHere() => true;
}
