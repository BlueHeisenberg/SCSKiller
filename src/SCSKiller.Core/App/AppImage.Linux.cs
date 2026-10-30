namespace SCSKiller.Core.App;

/// <summary>An AppImage runs from a FUSE mount ($APPDIR) that its runtime takes down once the last process holding it
/// exits; $APPIMAGE is the AppImage's own file. Whatever starts this app again after this process has exited (a menu
/// entry, a helper that outlives it, a sign-in entry) names $APPIMAGE, never a file in the mount.</summary>
public static class AppImage
{
    /// <summary>$APPIMAGE when <paramref name="processPath"/> runs from that AppImage's mount, else null: both variables are
    /// inherited by whatever an AppImage starts, so only a process inside the mount is that AppImage.</summary>
    public static string? Image(string? processPath, Func<string, string?> env) =>
        processPath != null && env("APPIMAGE") is { Length: > 0 } image && env("APPDIR") is { Length: > 0 } mount && Under(processPath, mount) ? image : null;

    /// <summary>What starts this copy again: its <see cref="Image"/>, else the process's own file. <paramref name="realPath"/>
    /// resolves symlinks; null when there is none.</summary>
    public static string? Program(string? processPath, Func<string, string?> env, Func<string, string?> realPath) =>
        processPath == null ? null : realPath(Image(processPath, env) ?? processPath);

    internal static bool Under(string path, string dir) => path.StartsWith(Path.TrimEndingDirectorySeparator(dir) + "/", StringComparison.Ordinal);
}
