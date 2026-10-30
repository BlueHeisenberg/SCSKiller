namespace SCSKiller.Core.App;

/// <summary>When a file was made. Linux: its birth time, which a rewrite keeps; .NET's creation time there is the older of
/// the write and change times, so a rewritten file looks made just now.</summary>
public static partial class FileTimes
{
    /// <summary><paramref name="path"/>'s creation time in UTC, its symlinks followed; null when it isn't known (Linux: the
    /// file system keeps none, or statx fails).</summary>
    public static partial DateTime? CreatedUtc(string path);

    /// <inheritdoc cref="CreatedUtc(string)"/>
    public static partial DateTime? CreatedUtc(FileSystemInfo f);
}
