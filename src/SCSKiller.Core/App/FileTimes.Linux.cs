namespace SCSKiller.Core.App;

public static partial class FileTimes
{
    public static partial DateTime? CreatedUtc(string path) => Created(Statx(path));

    public static partial DateTime? CreatedUtc(FileSystemInfo f) => CreatedUtc(f.FullName);

    /// <summary>The statx call. Tests: a file system without birth times, or a birth time Linux can't set.</summary>
    internal static Func<string, Libc.Birth> Statx { get; set; } = Libc.Born;

    internal static DateTime? Created(Libc.Birth b) =>
        b is { Error: 0, Seconds: >= MinSeconds and <= MaxSeconds, Nanoseconds: < 1_000_000_000 } && (b.Mask & Libc.StatxBtime) != 0
            ? DateTime.UnixEpoch.AddTicks(b.Seconds * TimeSpan.TicksPerSecond + b.Nanoseconds / 100) : null;

    // DateTime's range in Unix seconds
    const long MinSeconds = -62_135_596_800, MaxSeconds = 253_402_300_799;
}
