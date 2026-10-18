using System.Globalization;
using System.Text.Json;

namespace SCSKiller.Core.App;

/// <summary>Session snapshots (<see cref="SessionSnapshot"/>), games\&lt;id&gt;\sessions\&lt;launch unix ms&gt;.json: the last
/// <see cref="Keep"/> per game plus the pinned ones.</summary>
public static class Snapshots
{
    public const int Keep = 20;

    static readonly JsonSerializerOptions Json = new(AppStore.Json) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    /// <summary>The snapshot of a report <see cref="FrameLog.Read"/> made, with its context. <paramref name="played"/>: the
    /// game's last watched run; a launch that started in it ended under the watcher's eyes, which counts as a clean exit
    /// as an #end does (a game that terminates itself, as Unreal does, never writes one).</summary>
    public static SessionSnapshot Of(FrameReport r, PlayWindow? played, string? driverId, string? driverVersion, string? build, DateTimeOffset? warmedAt)
    {
        var c = FrameLog.ContextOf(r);
        bool watched = played is { } p && p.From.ToUnixTimeMilliseconds() <= r.LaunchUnixMs && r.LaunchUnixMs <= p.To.ToUnixTimeMilliseconds();
        return new SessionSnapshot(SessionSnapshot.Version, r.LaunchUnixMs, r.Duration.TotalMilliseconds, r.Startup.TotalMilliseconds, r.Frames,
            r.Low1PctFps, r.PlaySeconds, r.AvgFps, r.Hitches, r.Peaks, r.Creates, driverId, driverVersion, build, warmedAt, c?.SwapChain,
            CleanExit: c?.Ended == true || watched, Overlay: c?.Overlay == true, D3D11: c?.D3D11 == true,
            FrameGen: r.DisplayedFrames > 0 ? true : c?.FrameGen == true ? null : false);   // UE loads DLSS-G's module with the option off
    }

    /// <summary>Writes (or overwrites, while the launch grows) the launch's snapshot, keeps its pin and tag, prunes. A file
    /// that already holds this launch at the same length, frame count and exit is left alone: every scan after a start
    /// evaluates every game.</summary>
    public static void Write(AppStore store, string gameId, SessionSnapshot s)
    {
        var dir = Dir(store, gameId);
        var path = PathOf(dir, s.LaunchUnixMs);
        if (Same(Load(path), s)) return;
        AppStore.Locked(dir, () =>
        {
            var old = Load(path);
            if (Same(old, s)) return;
            if (old != null) s = s with { Pinned = old.Pinned, Tag = old.Tag };
            Save(path, s with { V = SessionSnapshot.Version });
            Prune(dir);
        }, LockWait);
    }

    static bool Same(SessionSnapshot? old, SessionSnapshot s) =>
        old != null && old.DurationMs == s.DurationMs && old.Frames == s.Frames && old.CleanExit == s.CleanExit;

    public static SessionSnapshot? Read(AppStore store, string gameId, long launchUnixMs) => Load(PathOf(Dir(store, gameId), launchUnixMs));

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<SessionSnapshot> List(AppStore store, string gameId) =>
        [.. Files(Dir(store, gameId)).Select(f => Load(f.Path)).OfType<SessionSnapshot>()];

    /// <summary>Pins with <paramref name="tag"/> ("before", "after"); null unpins. False when there's no such snapshot.</summary>
    public static bool Pin(AppStore store, string gameId, long launchUnixMs, string? tag)
    {
        var dir = Dir(store, gameId);
        var found = false;
        AppStore.Locked(dir, () =>
        {
            var path = PathOf(dir, launchUnixMs);
            if (Load(path) is not { } s) return;
            found = true;
            Save(path, s with { Pinned = tag != null, Tag = tag });
            if (tag == null) Prune(dir);
        }, LockWait);
        return found;
    }

    // the scan writes under it: a held lock costs a snapshot (an IOException), never the evaluation
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    static string Dir(AppStore store, string gameId) => Path.Combine(store.GameDir(gameId), "sessions");
    static string PathOf(string dir, long launchUnixMs) => Path.Combine(dir, launchUnixMs.ToString(CultureInfo.InvariantCulture) + ".json");

    static IEnumerable<(long Launch, string Path)> Files(string dir) => !Directory.Exists(dir) ? []
        : Directory.EnumerateFiles(dir, "*.json")
            .Select(f => (Ok: long.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l), Launch: l, Path: f))
            .Where(x => x.Ok).Select(x => (x.Launch, x.Path)).OrderByDescending(x => x.Launch);

    /// <summary>Under the sessions lock: deletes the unpinned snapshots past the newest <see cref="Keep"/> unpinned ones.</summary>
    static void Prune(string dir)
    {
        var files = Files(dir).ToList();
        if (files.Count <= Keep) return;
        foreach (var (_, path) in files.Where(f => Load(f.Path) is not { Pinned: true }).Skip(Keep))
            File.Delete(path);
    }

    static SessionSnapshot? Load(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<SessionSnapshot>(f, Json) is { V: SessionSnapshot.Version } s ? s : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException) { return null; }
    }

    static void Save(string path, SessionSnapshot s) => AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(s, Json));
}
