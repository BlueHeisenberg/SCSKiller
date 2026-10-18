namespace SCSKiller.Core.App;

/// <summary>Reads the recorder's scskiller_frames.bin (written by the frame writer in proxy.cpp) with its
/// scskiller_creates.csv and says which slow frames were shader compiles. Both files count in the recorder's own clock
/// (ms since it loaded: a launch record's microseconds, the csv's t_ms), so a create and a frame line up without any
/// alignment step.</summary>
public static class FrameLog
{
    public const string FileName = "scskiller_frames.bin";
    public const double HitchMs = 50;
    public const int GraphColumns = 1200;  // wider than the game page's graph: each pixel column takes the longest of its slices
    const double BlockMs = 10;           // a create that blocked this long can make a frame a hitch
    const int LoadCreates = 100;         // measured: loads and precompiles overlap 130-1,700 creates, play stutters 11-36
    const int StartupQuietCreates = 30;  // startup ends at the first 3 s with fewer creates
    const double PauseMs = 5000;         // a play frame this long without a create: the game paused or minimized
    public const double QuitMs = 10_000;  // measured: an Unreal game's quit freeze starts 6-7 s before its last frame
    const int BurstCreates = 100;        // a second with this many creates is a precompile or a load
    const double BurstGapMs = 10_000;    // measured: a title screen's second burst 5 s after the first; a level load 100 s and more
    const double ColdCompileMs = 100;    // measured: a compiled run's load creates stay under 100 ms, cold compiles' median is 159 ms
    const int LoadThreads = 8;           // a draw-time miss blocks the render thread or a few; a parallel precache or load spans 21-47

    sealed record Launch(long UnixMs, List<double> Ends, List<double>? Displayed = null);
    sealed record Create(double End, double Ms, char Kind, string Key, long? Tid, bool Presents);

    /// <summary>The report of the last launch with frames, or of the last whose csv launch names
    /// <paramref name="exeFileName"/>; null without frames. <paramref name="rayQuery"/>: RayQuery PSO keys, whose creates
    /// up to <see cref="SessionLog.RayQueryFloorMs"/> are the driver's floor, never a stutter. <paramref name="played"/>:
    /// the game's last watched run, for the launch's end (<see cref="SessionEnd"/>). <paramref name="playCapMs"/>: a before and
    /// after run's length: play counts from the launch's first present, loading and pauses included, and the report ends at
    /// the frame where this much play was reached.</summary>
    public static FrameReport? Read(string framesPath, string csvPath, string? exeFileName = null, IReadOnlySet<string>? rayQuery = null,
        PlayWindow? played = null, double? playCapMs = null)
    {
        if (!File.Exists(framesPath)) return null;
        var launches = ReadFrames(framesPath);
        // the csv's last launch first: a running game's, read again at every poll, without the whole csv
        var last = SessionLog.Launches(csvPath, SessionLog.LastSessionAt(csvPath)).Where(x => x.StartT != null).ToList();
        List<SessionLog.CsvLaunch>? sessions = null;
        for (int i = launches.Count - 1; i >= 0; i--)
        {
            var l = launches[i];
            if (l.Ends.Count < 2) continue;
            var s = last.FirstOrDefault(x => x.Start == l.UnixMs) ?? OwnerOf(sessions ??= SessionLog.Launches(csvPath).Where(x => x.Start != null).ToList(), l.UnixMs);
            if (s != null && SessionLog.OtherExe(s.Exe, exeFileName)) continue;
            return Report(l, s, rayQuery, played, playCapMs);
        }
        return null;
    }

    /// <summary>The end of a launch on the recorder's clock (t_ms, from the recorder's load), the same for both reports: the
    /// latest of its last create, its last frame (0 = no frames) and its #end, or without an #end the watched exit of the
    /// run it started in (a run can hold more than one launch). A unix time goes onto that clock from the #session
    /// stamp's t_ms (#clock; 0 from an older proxy).</summary>
    internal static double SessionEnd(SessionLog.CsvLaunch? l, double lastFrame, PlayWindow? played)
    {
        double end = Math.Max(l is { Creates.Count: > 0 } ? l.Creates[^1].T : 0, lastFrame);
        if (l?.Start is not { } s) return end;
        double origin = l.StartT ?? 0;
        if (l.EndT is { } et) return Math.Max(end, et);
        if (l.End is { } e && e >= s) return Math.Max(end, e - s + origin);
        if (played is { } p && p.From.ToUnixTimeMilliseconds() <= s && s <= p.To.ToUnixTimeMilliseconds())
            end = Math.Max(end, p.To.ToUnixTimeMilliseconds() - s + origin);
        return end;
    }

    /// <summary>The csv launch a frame log launch stamped at <paramref name="launchUnix"/> belongs to, the one rule both
    /// reports use: a recorder that writes #clock writes the same stamp into both; an older one stamped each in its own
    /// call, close together, so its launch is the only one within 10 s, or none.</summary>
    internal static SessionLog.CsvLaunch? OwnerOf(IEnumerable<SessionLog.CsvLaunch> launches, long launchUnix)
    {
        var near = launches.Where(l => l.Start is { } s && Math.Abs(s - launchUnix) < 10_000).ToList();
        return near.FirstOrDefault(l => l.StartT != null && l.Start == launchUnix) ?? (near is [{ StartT: null } only] ? only : null);
    }

    static FrameReport Report(Launch l, SessionLog.CsvLaunch? session, IReadOnlySet<string>? rayQuery, PlayWindow? played, double? playCapMs)
    {
        var creates = session?.Creates.Select(c => new Create(c.T, c.Ms, c.Kind, c.Key ?? "", c.Tid, c.Presents)).ToList() ?? [];
        creates.Sort((a, b) => a.End.CompareTo(b.End));
        double end = SessionEnd(session, l.Ends[^1], played);
        double startup = StartupEnd(creates.Select(c => (c.End, c.Kind, c.Ms, (string?)c.Key)), rayQuery, end, out var firstQuiet), shared = startup;
        // the slow-frame extension below never moves startup over a compile the last session counts as play
        double firstPlay = creates.Where(c => !InStartup(c.End, startup) && SessionLog.IsCompile(c.Kind, c.Ms, c.Key, rayQuery))
            .Select(c => c.End).DefaultIfEmpty(double.PositiveInfinity).Min();
        var frames = l.Ends.Zip(l.Ends.Skip(1), (a, b) => (Start: a, Ms: b - a)).ToList();
        double quit = l.Ends[^1] - QuitMs;
        var ends = creates.Select(c => c.End).ToList();
        var slow = new List<(double S, double Ms, int N, bool Compile, bool Shader, double Cold, bool Parallel)>();
        foreach (var (s, ms) in frames.Where(f => f.Ms >= HitchMs))
        {
            int n = 0;
            bool compile = false, shader = false, presenter = false;
            double cold = 0;
            var threads = new HashSet<long>();
            // only the creates that end in the frame, so each counts for one frame at most: a long one on a worker thread
            // overlaps slow frames it didn't cause
            for (int j = LowerBound(ends, s); j < creates.Count && creates[j].End <= s + ms; j++)
            {
                var c = creates[j];
                if (c.End <= s) continue;
                n++;
                if (c.Tid is { } tid) threads.Add(tid);
                presenter |= c.Presents;
                if (!SessionLog.IsCompile(c.Kind, c.Ms, c.Key, rayQuery)) continue;   // a library load, a cache hit or the RayQuery floor
                compile = true;
                shader |= Blocks(c.Kind, c.Ms);
                if (c.Ms >= ColdCompileMs) cold += c.End - Math.Max(c.End - c.Ms, s);   // its time in the frame
            }
            slow.Add((s, ms, n, compile, shader, cold, Parallel(threads.Count, presenter)));
        }
        // a slow frame with no create that the shared boundary falls inside is startup's to its end, within the 10 s after
        // the first quiet point that bursts get too, and never over a compile in play; any other slow frame is play
        foreach (var f in slow)
            if (f.N == 0 && f.S < shared && !InStartup(f.S + f.Ms, shared) && InStartup(f.S + f.Ms, Allowance(firstQuiet)) && f.S + f.Ms < firstPlay)
                startup = Math.Max(startup, f.S + f.Ms);
        if (playCapMs != null) startup = l.Ends[0];
        var hitches = new List<Hitch>();
        var paused = new HashSet<double>();
        foreach (var (s, ms, n, compile, shader, cold, parallel) in slow)
        {
            bool compiles = cold >= ms / 2;   // summed over threads
            var cause = InStartup(s + ms, startup) ? compiles ? HitchCause.LoadingShaders : HitchCause.Loading
                : s >= quit ? HitchCause.Quitting
                : compile && parallel ? HitchCause.Loading   // a precache or load on many threads, none the one that presents
                : compiles ? HitchCause.Shader   // a load in play that compiles: the shader cost the player feels
                : n >= LoadCreates ? HitchCause.Loading : shader ? HitchCause.Shader : HitchCause.Other;
            if (playCapMs == null && cause == HitchCause.Other && ms >= PauseMs && !compile) { paused.Add(s); continue; }   // a pause: nothing compiled
            hitches.Add(new Hitch(TimeSpan.FromMilliseconds(s), ms, cause));
        }
        // a pause isn't play; a long shader freeze is
        var inPlay = frames.Where(f => InPlay(f.Start, f.Ms, startup) && f.Start < quit && !paused.Contains(f.Start)).ToList();
        double cut = double.PositiveInfinity, sum = 0;
        if (playCapMs is { } cap)
            foreach (var f in inPlay)
            {
                if (sum >= cap) { cut = f.Start; break; }
                sum += f.Ms;
            }
        if (cut < end)
        {
            // FrameData.Play ends QuitMs before Duration: there, at the cut
            end = cut + QuitMs;
            frames = [.. frames.Where(f => f.Start < cut)];
            hitches.RemoveAll(h => h.At >= TimeSpan.FromMilliseconds(cut));
        }
        var play = inPlay.Where(f => f.Start < cut).Select(f => f.Ms).OrderDescending().ToList();
        double playSeconds = play.Sum() / 1000;
        var slowest = play.Take(Math.Max(1, play.Count / 100)).ToList();
        double low = slowest.Count > 0 ? 1000 * slowest.Count / slowest.Sum() : 0;
        var peaks = new float[GraphColumns];
        foreach (var (s, ms) in frames)
        {
            int c = Math.Clamp((int)(s / end * GraphColumns), 0, GraphColumns - 1);
            peaks[c] = Math.Max(peaks[c], (float)ms);
        }
        var report = new FrameReport(TimeSpan.FromMilliseconds(end), TimeSpan.FromMilliseconds(Math.Min(startup, end)),
            frames.Count, low, hitches, peaks, l.UnixMs,
            PlaySeconds: playSeconds, AvgFps: playSeconds > 0 ? play.Count / playSeconds : 0,
            Creates: session != null ? SessionLog.CreatesOf(session, rayQuery, Math.Min(startup, end), cut) : null,
            DisplayedFrames: l.Displayed?.Count ?? 0, Displayed: l.Displayed);
        if (session != null) Contexts.AddOrUpdate(report, new LaunchContext(session.SwapChain, session.Overlay, session.End != null, session.D3D11, session.FrameGen));
        return report;
    }

    /// <summary>What a report's csv launch said beyond its creates, for its session snapshot.</summary>
    internal sealed record LaunchContext(string? SwapChain, bool Overlay, bool Ended, bool D3D11, bool FrameGen);

    // kept beside the report rather than in it: FrameReport stays the game page's, and the csv isn't parsed twice
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameReport, LaunchContext> Contexts = new();

    /// <summary>The csv launch's context of a report <see cref="Read"/> made; null without a csv launch.</summary>
    internal static LaunchContext? ContextOf(FrameReport r) => Contexts.TryGetValue(r, out var c) ? c : null;

    /// <summary>A create or frame ending at <paramref name="end"/> is startup's: the one comparison both reports use.</summary>
    internal static bool InStartup(double end, double startup) => end <= startup;

    /// <summary>A frame from <paramref name="start"/> lasting <paramref name="ms"/> is play's (not startup's), by its end
    /// as <see cref="InStartup"/>: the hitch causes, the play counts and the 1% low all use it.</summary>
    public static bool InPlay(double start, double ms, double startup) => !InStartup(start + ms, startup);

    /// <summary>Creates on this many threads, none of them the one that presents, are a precache or load, not a draw-time miss.</summary>
    static bool Parallel(int threads, bool presenter) => threads >= LoadThreads && !presenter;

    /// <summary>The seconds (t_ms / 1000) of a load or precompile, from the csv's creates (end time, thread, presents) without
    /// frames: <see cref="BurstCreates"/> or more on threads that are <see cref="Parallel"/>, and for the burst's ragged edges
    /// a second next to one whose own creates are. A recorder that doesn't write threads has none.</summary>
    internal static HashSet<long> LoadSeconds(IEnumerable<(double End, long? Tid, bool Presents)> creates)
    {
        var seconds = creates.GroupBy(c => (long)(c.End / 1000)).ToDictionary(g => g.Key,
            g => (N: g.Count(), Parallel: Parallel(g.Select(c => c.Tid).OfType<long>().Distinct().Count(), g.Any(c => c.Presents))));
        var bursts = seconds.Where(x => x.Value.N >= BurstCreates && x.Value.Parallel).Select(x => x.Key).ToHashSet();
        return seconds.Where(x => bursts.Contains(x.Key) || x.Value.Parallel && (bursts.Contains(x.Key - 1) || bursts.Contains(x.Key + 1)))
            .Select(x => x.Key).ToHashSet();
    }

    /// <summary>A create ending at <paramref name="end"/> is in one of <see cref="LoadSeconds"/>.</summary>
    internal static bool InLoad(double end, IReadOnlySet<long> loads) => loads.Contains((long)(end / 1000));

    /// <summary>A compile this long, or a ray tracing state object's, can hold up a frame; shorter ones are hits slowed by
    /// contention as often as compiles.</summary>
    static bool Blocks(char kind, double ms) => kind is 'R' or 'A' || ms >= BlockMs;

    /// <summary>Startup ends by this, however its bursts and slow frames run on: 10 s after the first quiet point.</summary>
    static double Allowance(double firstQuiet) => firstQuiet + BurstGapMs;

    /// <summary>The end of the startup, from the csv's creates (end time, kind as written, ms, key) and whether each
    /// compiled (<see cref="SessionLog.IsCompile"/>): from the first busy second (<see cref="StartupQuietCreates"/> creates) within
    /// <see cref="BurstGapMs"/> of the first create (else that create's), the first 3 s that are quiet: fewer than
    /// <see cref="StartupQuietCreates"/> creates, or none that compiled and no second of <see cref="BurstCreates"/>.
    /// A burst (a second of <see cref="BurstCreates"/>) whose first create ends within <see cref="BurstGapMs"/> of that
    /// first quiet point is startup too, up to its own quiet point but never past those 10 s (<see cref="Allowance"/>),
    /// unless a compile in play that <see cref="Blocks"/> came before it. A window that runs past <paramref name="sessionEnd"/> counts as quiet
    /// only when it is the first (nothing came before it): a launch that ends while still busy is all startup (infinity).</summary>
    internal static double StartupEnd(IEnumerable<(double End, char Kind, double Ms, string? Key)> creates, IReadOnlySet<string>? rayQuery,
        double sessionEnd) => StartupEnd(creates, rayQuery, sessionEnd, out _);

    /// <summary><paramref name="firstQuiet"/>: the first quiet point, which the bursts' 10 s count from.</summary>
    internal static double StartupEnd(IEnumerable<(double End, char Kind, double Ms, string? Key)> creates, IReadOnlySet<string>? rayQuery,
        double sessionEnd, out double firstQuiet)
    {
        firstQuiet = double.PositiveInfinity;
        var perSecond = new Dictionary<long, (int Creates, int Compiles, double First)>();
        var compiled = new List<double>();
        foreach (var (e, kind, ms, key) in creates)
        {
            bool compile = SessionLog.IsCompile(kind, ms, key, rayQuery);
            if (compile && Blocks(kind, ms)) compiled.Add(e);
            var (n, c, f) = perSecond.GetValueOrDefault((long)(e / 1000), (0, 0, double.PositiveInfinity));
            perSecond[(long)(e / 1000)] = (n + 1, c + (compile ? 1 : 0), Math.Min(f, e));
        }
        if (perSecond.Count == 0) return firstQuiet = 0;
        // a steady trickle of cache hits in play doesn't hold startup open; a warmed run's precompile, all hits, does
        bool Busy(long s)
        {
            var w = new[] { s, s + 1, s + 2 }.Select(k => perSecond.GetValueOrDefault(k)).ToList();
            return w.Sum(x => x.Creates) >= StartupQuietCreates && (w.Any(x => x.Compiles > 0) || w.Any(x => x.Creates >= BurstCreates));
        }
        long? Quiet(long from)   // a window cut off by the session's end is quiet only as the first
        {
            for (long s = from; s == from || s * 1000 < sessionEnd; s++)
                if (!Busy(s) && (s == from || (s + 3) * 1000 <= sessionEnd)) return s;
            return null;
        }
        // lone creates before the precompile (a splash screen's) don't start the startup's clock
        long min = perSecond.Keys.Min(), from = min;
        bool Starts(long k) => perSecond.GetValueOrDefault(k).Creates >= StartupQuietCreates && Busy(k);
        while (from - min < BurstGapMs / 1000 && !Starts(from)) from++;
        if (!Starts(from)) from = min;
        if (Quiet(from) is not { } first) return double.PositiveInfinity;
        firstQuiet = first * 1000.0;
        double end = firstQuiet, cap = Allowance(firstQuiet);
        foreach (var b in perSecond.Keys.Where(k => k > first && perSecond[k].First <= cap && perSecond[k].Creates >= BurstCreates).Order())
        {
            var start = perSecond[b].First;
            if (InStartup(start, end)) continue;   // inside the startup already
            if (compiled.Any(e => !InStartup(e, end) && e < start)) break;   // a compile in play before it: what follows is play
            end = Quiet(b) is { } q ? q * 1000.0 : cap;
        }
        return Math.Min(end, cap);
    }

    static int LowerBound(List<double> v, double x)
    {
        int i = v.BinarySearch(x);
        if (i < 0) return ~i;
        while (i > 0 && v[i - 1] >= x) i--;
        return i;
    }

    /// <summary>u32 records: 0xFFFFFFFF + u64 unix_ms (the csv's #session stamp), u64 us since the recorder loaded, u64 QPC, u64 QPC frequency opens a
    /// launch; top 4 bits 0-14 = a frame of that swap chain, the low 28 bits the microseconds since the previous record;
    /// top 4 bits 15 = no frame for the low 28 bits' milliseconds, or with those under <see cref="TagEnd"/> a tagged record:
    /// tags 0-14 = that swap chain is frame generation's, its frames the ones the game renders; any other tag is skipped.
    /// Frames of the launch's busiest swap chain; with frame generation, the busiest other one's until its first frame, then
    /// those of every tagged one, and every other one's frames are the displayed ones (generated ones too).</summary>
    /// <summary>A gap record is written only for 2^28 us or more: its milliseconds are never under this, so those values
    /// are tags (ARCHITECTURE.md "scskiller_frames.bin"), which a reader from before them takes for a gap of under 268 s.</summary>
    internal const uint TagEnd = (1u << 28) / 1000;

    static List<Launch> ReadFrames(string path)
    {
        var launches = new List<Launch>();
        byte[] data;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[f.Length];
            f.ReadExactly(data);
        }
        long unix = 0, t = 0;
        double start = 0;
        Dictionary<uint, List<double>>? chains = null;
        HashSet<uint> games = [];   // a launch that made frame generation's swap chain again (a resize) has one tag per swap chain
        void Close()
        {
            if (chains == null) return;
            var own = chains.Where(c => games.Contains(c.Key)).SelectMany(c => c.Value).Order().ToList();
            if (own.Count == 0)
            {
                launches.Add(new Launch(unix, chains.Values.MaxBy(c => c.Count) ?? []));
                return;
            }
            var others = chains.Where(c => !games.Contains(c.Key)).Select(c => c.Value).ToList();
            var before = others.Select(c => c.Where(t => t < own[0]).ToList()).MaxBy(c => c.Count) ?? [];
            launches.Add(new Launch(unix, [.. before, .. own], [.. others.SelectMany(c => c).Order()]));
        }
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            uint r = BitConverter.ToUInt32(data, i);
            if (r == 0xFFFFFFFF)
            {
                if (i + 36 > data.Length) break;
                Close();
                unix = BitConverter.ToInt64(data, i + 4);
                start = BitConverter.ToInt64(data, i + 12) / 1000.0;
                (t, chains, games) = (0, [], []);
                i += 32;
            }
            else if (chains == null) continue;
            else if (r >> 28 == 15 && (r & 0x0FFFFFFF) < TagEnd)
            {
                if ((r & 0x0FFFFFFF) < 15) games.Add(r & 0x0FFFFFFF);
            }
            else if (r >> 28 == 15) t += (r & 0x0FFFFFFF) * 1000L;
            else
            {
                t += r & 0x0FFFFFFF;
                if (!chains.TryGetValue(r >> 28, out var c)) chains[r >> 28] = c = [];
                c.Add(start + t / 1000.0);
            }
        }
        Close();
        return launches;
    }
}
