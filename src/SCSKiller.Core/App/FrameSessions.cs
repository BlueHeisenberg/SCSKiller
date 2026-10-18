using System.Text.Json;

namespace SCSKiller.Core.App;

/// <summary>games\&lt;id&gt;\frame-history.json: the last <see cref="Keep"/> launches' frame reports in short, newest first. It
/// deletes nothing but its own entries.</summary>
public static class FrameSessions
{
    // fixed-size entries of about 120 bytes: the count bounds the file to about 2.5 KB per game
    public const int Keep = 20;
    const string FileName = "frame-history.json";
    static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static FrameSession Of(FrameReport r) =>
        new(r.LaunchUnixMs, r.Duration, r.Low1PctFps, r.Hitches.Count(h => h.Cause == HitchCause.Shader));

    /// <summary>Newest first; none when the file is missing or unreadable.</summary>
    public static IReadOnlyList<FrameSession> Read(AppStore store, string gameId) => Load(PathOf(store, gameId));

    /// <summary>Adds the report's launch, or updates it while the launch grows, and keeps the newest <see cref="Keep"/>. A
    /// report without a launch stamp (a frame log from before it had one) isn't kept. False when nothing changed.</summary>
    public static bool Note(AppStore store, string gameId, FrameReport r)
    {
        if (r.LaunchUnixMs <= 0) return false;
        var s = Of(r);
        var path = PathOf(store, gameId);
        var changed = false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AppStore.Locked(path, () =>
        {
            var list = Load(path);
            if (list.Contains(s)) return;
            list = [.. list.Where(e => e.LaunchUnixMs != s.LaunchUnixMs).Append(s).OrderByDescending(e => e.LaunchUnixMs).Take(Keep)];
            AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(list, AppStore.Json));
            changed = true;
        }, TimeSpan.FromSeconds(5));
        return changed;
    }

    static string PathOf(AppStore store, string gameId) => Path.Combine(store.GameDir(gameId), FileName);

    static List<FrameSession> Load(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return [.. (JsonSerializer.Deserialize<List<FrameSession?>>(f, AppStore.Json) ?? []).OfType<FrameSession>()
                .Where(e => e.LaunchUnixMs > 0 && e.LaunchUnixMs <= MaxUnixMs && e.Duration >= TimeSpan.Zero)];
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException or UnauthorizedAccessException) { return []; }
    }
}
