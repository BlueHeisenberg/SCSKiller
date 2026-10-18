using System.Globalization;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.App;

/// <summary>The exe file name a game's process was launched with (the case the driver keys its cache on), seen at
/// <see cref="At"/>.</summary>
public sealed record LaunchedExe(string Name, DateTimeOffset At);

/// <summary>Reads the proxy's scskiller_creates.csv (t_ms,kind,known,tuple_known,ms, then in newer proxies key,proxy_ms,tid,presents;
/// appended per game launch, t_ms restarts at each launch). Lowercase kind = loaded from the
/// game's own pipeline library; a create over 3 ms is a real driver compile (but a RayQuery PSO's floor, see
/// <see cref="RayQueryFloorMs"/>), under it a cache hit. Kinds 'R' / 'A' (ray
/// tracing state objects) are counted apart: compiled from <see cref="StateObjectCompileMs"/>, else ready. Compiles during
/// the launch's startup (<see cref="FrameLog.StartupEnd"/>, the frame report's rule) or a load or precompile after it
/// (<see cref="FrameLog.LoadSeconds"/>) are counted apart too. Newer proxies bracket each launch with <c>#session,&lt;unix_ms&gt;,&lt;exe&gt;</c> and <c>#end,&lt;unix_ms&gt;</c>
/// (missing after a crash, and whenever the game terminates its own process, as Unreal does); <c>#swapchain</c>, <c>#overlay</c>, <c>#framegen</c>, <c>#frames,stopped</c> and <c>#api,d3d11</c> are kept per launch (<see cref="CsvLaunch"/>), other <c>#</c> lines are ignored. The #session exe is
/// GetModuleFileNameW(NULL)'s file name inside the game process: the name exactly as launched (measured: a process started
/// as CASEPROBE.EXE from the file caseProbe.exe has CASEPROBE.EXE there, while its kernel image name and
/// QueryFullProcessImageName say caseProbe.exe), the case AMD's cache key uses.</summary>
public static class SessionLog
{
    const double CompileMs = 3.0;

    /// <summary>The driver compiled the create: not from the game's library, a state object from
    /// <see cref="StateObjectCompileMs"/>, anything else over 3 ms but a RayQuery PSO at the floor.</summary>
    internal static bool IsCompile(char kind, double ms, string? key, IReadOnlySet<string>? rayQuery) =>
        char.IsUpper(kind) && (kind is 'R' or 'A' ? ms >= StateObjectCompileMs
            : ms > CompileMs && !(ms <= RayQueryFloorMs && key != null && rayQuery?.Contains(key) == true));

    sealed class Session(CsvLaunch launch)
    {
        public CsvLaunch Launch => launch;
        public long? Start => launch.Start;
        public long? End => launch.End;
        public bool OtherExe;
        public List<(double T, char Kind, double Ms, string? Key, long? Tid, bool Presents)> Creates => launch.Creates;
        public double LastT => Creates.Count > 0 ? Creates[^1].T : double.NegativeInfinity;

        public long Hits, Compiles, StartupCompiles, Library, RayQuery, SoReady, SoCompiled, SoStartupCompiled, PlayHits, PlaySoReady;
        public double Worst;

        /// <summary>Compiles that end by <paramref name="startup"/> (<see cref="FrameLog.InStartup"/>) or in one of
        /// <paramref name="loads"/> (<see cref="FrameLog.InLoad"/>) count in StartupCompiles (state objects in SoStartupCompiled),
        /// and Worst is of the other creates.</summary>
        public Session Count(IReadOnlySet<string>? rayQuery, double startup = double.NegativeInfinity, IReadOnlySet<long>? loads = null)
        {
            bool Started(double t) => FrameLog.InStartup(t, startup) || loads != null && FrameLog.InLoad(t, loads);
            Hits = Compiles = StartupCompiles = Library = RayQuery = SoReady = SoCompiled = SoStartupCompiled = PlayHits = PlaySoReady = 0;
            Worst = 0;
            foreach (var (t, kind, ms, key, _, _) in Creates)
            {
                if (!char.IsUpper(kind)) { Library++; continue; }
                bool play = !Started(t);
                if (kind is 'R' or 'A')
                {
                    if (ms < StateObjectCompileMs) { SoReady++; if (play) PlaySoReady++; }
                    else if (Started(t)) SoStartupCompiled++;
                    else SoCompiled++;
                    continue;
                }
                // the cache held a RayQuery PSO at its floor (IsCompile): ready, as the frame report counts no stutter for it
                if (ms > CompileMs && ms <= RayQueryFloorMs && key != null && rayQuery?.Contains(key) == true) { RayQuery++; if (play) PlayHits++; continue; }
                if (ms <= CompileMs) { Hits++; if (play) PlayHits++; }
                else if (Started(t)) StartupCompiles++;
                else Compiles++;
                if (!Started(t)) Worst = Math.Max(Worst, ms);
            }
            return this;
        }

        // Play time: the launch's end on the recorder's clock (FrameLog.SessionEnd), as the frame report's Duration. Without
        // an #end (a crash, an older proxy, or a game that terminates itself, as Unreal does, so DLL_PROCESS_DETACH never
        // runs) the watched exit of the run it started in, its last frame or its last create stand in.
        public SessionStats Stats(PlayWindow? played, IReadOnlySet<string>? rayQuery, FrameReport? frames)
        {
            double lastFrame = frames?.Duration.TotalMilliseconds ?? 0;   // the caller passes only this launch's
            double end = FrameLog.SessionEnd(launch, lastFrame, played);
            Count(rayQuery, FrameLog.StartupEnd(Creates.Select(c => (c.T, c.Kind, c.Ms, c.Key)), rayQuery, end),
                FrameLog.LoadSeconds(Creates.Select(c => (c.T, c.Tid, c.Presents))));
            return new(TimeSpan.FromMilliseconds(end), Creates.Count, Library, Hits, Compiles, Worst, RayQuery, SoReady, SoCompiled, StartupCompiles, SoStartupCompiled);
        }

        /// <summary>The app saw the run this launch started in exit.</summary>
        public bool Exited(PlayWindow? played) => Start is { } st && played is { } p && p.From.ToUnixTimeMilliseconds() <= st && st <= p.To.ToUnixTimeMilliseconds();
    }

    /// <summary>NVIDIA recompiles part of a cached RayQuery PSO at every create: about 7-15% of its cold create, 9-35 ms
    /// for an Unreal 5.6 game's, whose cold creates take 39-466 ms (selftest bindless). Up to this it is that floor; above
    /// it the cache missed. In play both stretch: SILENT HILL: Townfall's floor 12-88 ms (1-2% over 60), its compiles 75 ms
    /// and up; its launch before any compile created 2 of its 1,193 in 3.1 ms, the rest under 3.</summary>
    public const double RayQueryFloorMs = 60.0;

    /// <summary>A ray tracing state object create this long or longer compiled; shorter, it came from the driver cache. A
    /// cached one still costs about 1.5 ms per shader or collection (selftest dxr). The Witcher 3, 3 sessions: additions
    /// and pipelines of 1-8 libraries repeated byte for byte from an earlier session (cached) took 6.5-23.5 ms (49 creates),
    /// first ones 27.7 ms and more (30), single-material additions 28-80 ms. Hogwarts Legacy, the same objects in a cold
    /// and a compiled run: cached p90 23 ms, p99 57 ms; cold ones 60 ms and more for 672 of 875, so about one cached
    /// create in ten there counts as compiled.</summary>
    public const double StateObjectCompileMs = 25.0;

    /// <summary>The launch's creates split at <paramref name="startup"/> and its loads, by <see cref="Session.Count"/>'s rules, as the
    /// game page's <see cref="Session.Stats"/>: hits, compiles and state objects in play, PSO compiles in startup and loads, library
    /// loads over the whole launch.</summary>
    internal static CreateStats CreatesOf(CsvLaunch l, IReadOnlySet<string>? rayQuery, double startup, double until = double.PositiveInfinity)
    {
        if (until < double.PositiveInfinity)
        {
            var cut = new CsvLaunch();
            cut.Creates.AddRange(l.Creates.Where(c => c.T < until));
            l = cut;
        }
        var c = new Session(l).Count(rayQuery, startup, FrameLog.LoadSeconds(l.Creates.Select(c => (c.T, c.Tid, c.Presents))));
        return new CreateStats(c.PlayHits, c.Compiles, c.StartupCompiles, c.Library, c.PlaySoReady, c.SoCompiled);
    }

    /// <summary>The last launch's stats and the exe name of the last <c>#session</c> marker (null without markers); with
    /// <paramref name="exeFileName"/>, of the last marker naming that exe apart from case (another exe of the folder may
    /// have loaded the proxy too). And the first launch that started after <paramref name="firstAfter"/>
    /// and ended (its <c>#end</c>, or a later launch) with at least <paramref name="minCreates"/> hits and compiles
    /// (<see cref="LaunchCheck"/>); of <paramref name="exeFileName"/> when given. Launches without a <c>#session</c> marker
    /// have no start time: never that one. <paramref name="rayQuery"/>: keys of PSOs SCSKiller compiled whose shaders trace
    /// rays inline (<see cref="WriteRayQueryKeys"/>); a create of one over 3 ms and up to <see cref="RayQueryFloorMs"/>
    /// counts in <see cref="SessionStats.RayQueryRecompiles"/> only. Null: every create is a hit or a compile.
    /// <paramref name="played"/>: the game's last watched run, the play time of a launch without <c>#end</c> that started in it.
    /// <paramref name="frames"/>: the frame log's report, whose last frame (of the same launch) is one of the launch's
    /// ends (<see cref="FrameLog.SessionEnd"/>), so both reports draw the same startup.</summary>
    public static (SessionStats? Last, LaunchedExe? Exe, LaunchCheck? First) Read(string csvPath, string? exeFileName = null,
        DateTimeOffset? firstAfter = null, long minCreates = 1, IReadOnlySet<string>? rayQuery = null, PlayWindow? played = null,
        FrameReport? frames = null) => Read(csvPath, out _, exeFileName, firstAfter, minCreates, rayQuery, played, frames);

    /// <summary>The same, and whether <paramref name="frames"/> is the last session's launch (<paramref name="framesMatch"/>):
    /// the game page shows frame times only next to their own launch's counts.</summary>
    public static (SessionStats? Last, LaunchedExe? Exe, LaunchCheck? First) Read(string csvPath, out bool framesMatch, string? exeFileName = null,
        DateTimeOffset? firstAfter = null, long minCreates = 1, IReadOnlySet<string>? rayQuery = null, PlayWindow? played = null,
        FrameReport? frames = null) => Read(csvPath, out framesMatch, out _, exeFileName, firstAfter, minCreates, rayQuery, played, frames);

    /// <summary>The same, and the game's last AGS registration (<see cref="LastAgs"/>) from the same pass.</summary>
    public static (SessionStats? Last, LaunchedExe? Exe, LaunchCheck? First) Read(string csvPath, out bool framesMatch, out AgsRegistration? ags,
        string? exeFileName = null, DateTimeOffset? firstAfter = null, long minCreates = 1, IReadOnlySet<string>? rayQuery = null,
        PlayWindow? played = null, FrameReport? frames = null)
    {
        (framesMatch, ags) = (false, null);
        if (!File.Exists(csvPath)) return (null, null, null);
        Session? cur = null;
        LaunchedExe? exe = null;
        LaunchCheck? first = null;
        // the whole launch, startup included: what still compiles there after a warm is what the check measures
        void Ended(Session s)
        {
            if (first == null && firstAfter is { } a && s is { Start: { } st, OtherExe: false } && st > a.ToUnixTimeMilliseconds()
                && s.Count(rayQuery) is var c && c.Hits + c.Compiles >= minCreates)
                first = new LaunchCheck(DateTimeOffset.FromUnixTimeMilliseconds(st), c.Hits, c.Compiles);
        }
        Session? game = null;   // the last launch of exeFileName's (an unmarked launch counts: older proxies wrote no name)
        var launches = Launches(csvPath).ToList();
        foreach (var l in launches)
        {
            if (cur != null) Ended(cur);   // a later launch began
            bool other = l.Start != null && OtherExe(l.Exe, exeFileName);
            if (l.Start is long ms && ms > 0 && l.Exe.Length > 0 && !other) exe = new LaunchedExe(l.Exe, DateTimeOffset.FromUnixTimeMilliseconds(ms));
            cur = new Session(l) { OtherExe = other };
            if (!other) game = cur;
            if (!other && l.Ags != null) ags = l.Ags;
        }
        if (cur != null && (cur.End != null || cur.Exited(played))) Ended(cur);
        framesMatch = game != null && frames != null && FrameLog.OwnerOf(launches, frames.LaunchUnixMs) == game.Launch;
        return (game?.Stats(played, rayQuery, framesMatch ? frames : null), exe, first);
    }

    /// <summary>A launch whose #session names <paramref name="named"/> is another exe's than <paramref name="exeFileName"/>
    /// (null: any exe's is the game's). The proxy writes the name through the C locale, so a non-ASCII name comes out
    /// mangled: an exe name with non-ASCII letters can't be told apart, and every launch counts as its own.</summary>
    internal static bool OtherExe(string named, string? exeFileName) =>
        exeFileName != null && exeFileName.All(char.IsAscii) && !named.Equals(exeFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>One launch of the csv, as both reports split it (<see cref="Launches"/>).</summary>
    internal sealed class CsvLaunch
    {
        public long? Start, End;   // the #session and #end stamps (unix ms)
        public double? StartT, EndT;   // the same instants on the recorder's clock (t_ms); null from an older proxy
        public string Exe = "";
        public AgsRegistration? Ags;   // the last #ags line: the AGS app and engine names the game created a device with
        public string? SwapChain;   // the last #swapchain,<w>,<h>,<dxgi format> as "2560x1440 fmt 28"
        public bool Overlay;        // a #overlay line: the in-game countdown drew
        public bool FrameGen;       // a #framegen line: a frame generation module loaded (DLSS-G, XeSS)
        public bool FramesStopped;  // #frames,stopped: frame generation's swap chain with no hook on the game's frames, none follow
        public bool D3D11;          // #api,d3d11: the recorder ran as d3d11.dll, which writes no create rows
        public readonly List<(double T, char Kind, double Ms, string? Key, long? Tid, bool Presents)> Creates = [];   // Tid null: an older proxy
    }

    /// <summary>The csv's launches, the one split both reports use: a <c>#session</c> line opens one; rows before any, after
    /// a launch's <c>#end</c> (the proxy writes it as its process detaches, so they are another process's) or after t_ms
    /// restarted (an older proxy without markers) open one without a stamp, which the frame report never matches.
    /// Kinds as written (lowercase: from the game's pipeline library). <paramref name="from"/>: the byte a line starts at, to
    /// read only the launches from there (<see cref="LastSessionAt"/>).</summary>
    internal static IEnumerable<CsvLaunch> Launches(string path, long from = 0)
    {
        if (!File.Exists(path)) yield break;
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = Math.Min(from, stream.Length);
        using var reader = new StreamReader(stream);
        CsvLaunch? cur = null;
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split(',');
            if (line.StartsWith('#'))
            {
                long ms = f.Length > 1 && long.TryParse(f[1], CultureInfo.InvariantCulture, out var x) ? x : 0;
                if (f[0] == "#session")
                {
                    if (cur != null) yield return Sorted(cur);
                    cur = new CsvLaunch { Start = ms, Exe = f.Length > 2 ? string.Join(',', f[2..]).Trim() : "" };   // the name may hold commas
                }
                else if (f[0] == "#clock" && cur is { Start: not null, Creates.Count: 0 } && double.TryParse(f.Length > 1 ? f[1] : "", CultureInfo.InvariantCulture, out var st))
                    cur.StartT = st;
                else if (f[0] == "#ags" && cur != null && AgsLine(f) is { } ags)
                    cur.Ags = ags;
                else if (f[0] == "#end" && cur != null)
                {
                    cur.End = ms;
                    if (f.Length > 2 && double.TryParse(f[2], CultureInfo.InvariantCulture, out var et)) cur.EndT = et;
                }
                else if (f[0] == "#swapchain" && cur is { End: null } && f.Length > 3 && int.TryParse(f[1], CultureInfo.InvariantCulture, out var w)
                    && int.TryParse(f[2], CultureInfo.InvariantCulture, out var h) && int.TryParse(f[3], CultureInfo.InvariantCulture, out var fmt))
                    cur.SwapChain = $"{w}x{h} fmt {fmt}";
                else if (f[0] == "#overlay" && cur is { End: null })
                    cur.Overlay = true;
                else if (f[0] == "#framegen" && cur is { End: null })
                    cur.FrameGen = true;
                else if (f[0] == "#frames" && cur is { End: null } && f.Length > 1 && f[1] == "stopped")
                    cur.FramesStopped = true;
                else if (f[0] == "#api" && cur is { End: null } && f.Length > 1 && f[1] == "d3d11")
                    cur.D3D11 = true;
                continue;
            }
            if (f.Length < 5 || f[1].Length != 1 || !double.TryParse(f[0], CultureInfo.InvariantCulture, out var t)
                || !double.TryParse(f[4], CultureInfo.InvariantCulture, out var ms2)) continue;
            // t_ms is taken as the create returns, before the row's lock: concurrent creates can land a little out of order
            // (The Witcher 3: 68718.7 after 68718.8), so only a launch without a #session (an older proxy) splits on it
            if (cur == null || cur.End != null || cur.Start == null && cur.Creates.Count > 0 && t < cur.Creates[^1].T)
            {
                if (cur != null) yield return Sorted(cur);
                cur = new CsvLaunch();
            }
            cur.Creates.Add((t, f[1][0], ms2, f.Length > 5 ? f[5] : null,
                f.Length > 8 && long.TryParse(f[7], CultureInfo.InvariantCulture, out var tid) ? tid : null, f.Length > 8 && f[8] == "1"));
        }
        if (cur != null) yield return Sorted(cur);

        static CsvLaunch Sorted(CsvLaunch l)
        {
            l.Creates.Sort((a, b) => a.T.CompareTo(b.T));
            return l;
        }
    }

    /// <summary>The AGS registration of the last launch of <paramref name="exeFileName"/> (null: any exe's) that created a
    /// device through <c>agsDriverExtensionsDX12_CreateDevice</c> (the proxy's <c>#ags</c> line); null when none did.</summary>
    public static AgsRegistration? LastAgs(string csvPath, string? exeFileName) =>
        Launches(csvPath).LastOrDefault(l => l.Ags != null && !(l.Start != null && OtherExe(l.Exe, exeFileName)))?.Ags;

    /// <summary><c>#ags,&lt;registered&gt;,&lt;app version&gt;,&lt;engine version&gt;,&lt;app&gt;,&lt;engine&gt;</c>: registered is 1, 0, or ?
    /// (an AGS before 6); versions in hex; the names as AGS got them (UTF-16), a comma, '%' or a unit outside printable
    /// ASCII written as %XXXX. Null for a malformed line or an empty app name.</summary>
    internal static AgsRegistration? AgsLine(string[] f)
    {
        if (f.Length != 6 || Unescape(f[4]) is not { Length: > 0 } app || Unescape(f[5]) is not { } engine) return null;
        return new(app, engine);

        static string? Unescape(string s)
        {
            var b = new System.Text.StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
                if (s[i] != '%') b.Append(s[i]);
                else if (i + 4 < s.Length && ushort.TryParse(s.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var u)) { b.Append((char)u); i += 4; }
                else return null;
            return b.ToString();
        }
    }

    /// <summary>The byte the csv's last <c>#session</c> line starts at, read from the end; 0 without one.</summary>
    internal static long LastSessionAt(string path)
    {
        var mark = "\n#session,"u8;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[1 << 16];
            for (long end = f.Length; end > 0;)
            {
                long start = Math.Max(0, end - buf.Length);
                f.Position = start;
                f.ReadExactly(buf, 0, (int)(end - start));
                int at = buf.AsSpan(0, (int)(end - start)).LastIndexOf(mark);
                if (at >= 0) return start + at + 1;
                if (start == 0) return 0;
                end = start + mark.Length - 1;   // a mark across the chunks' edge
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return 0;
    }

    /// <summary>Writes the keys of the PSOs with a shader that traces rays inline (SFI0's RayQuery flag), one per line, of
    /// the dbs together: a PSO's shader bytes may be in another of them. They must carry the shader bytes (rehydrated).</summary>
    public static void WriteRayQueryKeys(IEnumerable<string> dbs, string keysFile)
    {
        var rayQuery = new HashSet<string>();
        var psos = new List<(string Key, ICollection<string> Stages)>();
        foreach (var r in dbs.Where(File.Exists).SelectMany(PsoDb.Read))
            if (r.Tag == 'B') { if (r.Payload.Length > 20 && Dxbc.InlineRayTracing(r.Payload.AsSpan(20))) rayQuery.Add(PsoDb.Hex(r.Payload.AsSpan(0, 20))); }
            else if (r.Tag is 'G' or 'C' or 'S')
                try { psos.Add((r.Key, PsoDb.Parse(r).Stages.Values)); }
                catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException) { }   // a record from a newer proxy
        File.WriteAllLines(keysFile, psos.Where(p => p.Stages.Any(rayQuery.Contains)).Select(p => p.Key));
    }

    /// <summary>What <see cref="WriteRayQueryKeys"/> wrote; null without the file.</summary>
    public static IReadOnlySet<string>? ReadRayQueryKeys(string keysFile) => File.Exists(keysFile) ? File.ReadAllLines(keysFile).ToHashSet() : null;
}
