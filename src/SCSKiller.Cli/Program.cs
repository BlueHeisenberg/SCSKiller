using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using CUE4Parse.Compression;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Unreal;
using SCSKiller.Core.Vendors;

// English output whatever the Windows display language: Core's display text (Format, Loc) follows the UI culture.
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

const string Usage = """
    usage: scskiller <command>
      scan [--rescan]                             discover games and show their status (--rescan: redo engine detection)
      status [game]                               all games, or one in detail
      compile <game|--all-ready> [--threads N] [--idle | --when-idle] [--careful | --fast]
                                                  --idle: background priority; --when-idle: also only while nobody uses the PC;
                                                  --careful (AMD): this and later compiles of the game compile its recorded
                                                  pipelines in passes on few threads, so the driver keeps more of them (slower);
                                                  --fast: back to one pass
      queue [command]                             debug shell for the compile queue: the command, then one per line on stdin:
                                                  list | add <game> | move <game> <index> | remove <game> | start | stop | wait | quit
      rewarm-stale [--if-driver-changed]          what the scheduled task runs; follows the driver-update setting
      cache get | cache set <GB|unlimited|default>
      cache clear <game> [--game-precache]        delete the game's shader caches: driver, Windows (D3DSCache) (asks first);
                                                  --game-precache: also the caches the game writes itself (Unreal's user pipeline
                                                  cache, *.ushaderprecache), which it rebuilds at its next start
      nvidia-snapshot                             read-only: every global DRS setting, Auto Shader Compilation, its idle task
      nvidia-auto-shader off|low|medium|high      switch NVIDIA's Auto Shader Compilation (admin, asks first)
        (cache set / nvidia-auto-shader, as the app runs them elevated: --yes skips the question, --result <file>
         writes the outcome as a JSON line, --for-user <SID> refuses a different account's elevation)
      record install|uninstall <game>
      record clear <game>                         delete the game's recording (the recorder's data files and SCSKiller's copy; asks first)
      record alongside <game> on|off              let the recorder chain to a mod's d3d12.dll (renamed, put back on removal)
      key <game> <hex>                            give an encrypted game's AES key (verified, stored locally only)
      index <game> --out <dir>                    debug: dump the engine reader's shader index
      rehydrate <game> <hash-only.db> --out <db> [--expect <content hash>]
                                                  add the shader bytes a hash-only recording references, from the install
      reference export <game> [--out <file>]      the game build's shader list for the community server's `admin reference import`:
                                                  the index's shaders and those this PC's recording saw (default <game id>.reference.json)
      task register|unregister                    the logon/idle "re-warm after a driver update" task
      fetch-codecs                                packaging: download the Oodle/zlib DLLs next to this exe if missing
    <game> is an id (steam:2909400) or a case-insensitive part of the name.
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;  // game names carry ™ and ®
if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
// The app is handing over to Update.exe, which replaces this folder: start nothing (the scheduled task runs again later).
if (Busy.Applying(AppStore.DefaultDir, DateTimeOffset.UtcNow)) { Console.Error.WriteLine("an update is being installed; try again in a minute"); return 0; }
int code;
try
{
    code = args[0] switch
    {
        "scan" => await Scan(args.Contains("--rescan")),
        "status" => await Status(args.Length > 1 ? args[1] : null),
        "compile" => await Compile(),
        "queue" => await QueueShell(),
        "rewarm-stale" => await RewarmStale(args.Contains("--if-driver-changed")),
        "cache" => args.ElementAtOrDefault(1) == "clear" ? await CacheClear() : Cache(),
        "record" => await Record(),
        "index" => await Index(),
        "rehydrate" => await RehydrateCommand(),
        "reference" when args.ElementAtOrDefault(1) == "export" => await ReferenceExport(),
        "key" => await Key(),
        "task" => TaskCommand(),
        "nvidia-snapshot" => NvidiaSnapshot(),
        "nvidia-auto-shader" => NvidiaAutoShader(),
        "fetch-codecs" => FetchCodecs(),
        _ => Fail($"unknown command '{args[0]}'\n{Usage}"),
    };
}
catch (Exception e)
{
    code = Fail(e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException ? e.Message : e.ToString());
}
// Run elevated by the app (Elevated.Run): runas can't redirect stdout, so the outcome goes to a file.
string? resultFile = null;
try { resultFile = Opt(Elevated.ResultArg); }
catch (ArgumentException e) { code = Fail(e.Message); }
if (resultFile != null) Elevated.WriteResult(resultFile, code == 0, Outcome.Message ?? "");
return code;

static int Fail(string message)
{
    Console.Error.WriteLine("error: " + message);
    Outcome.Message = message;
    return 1;
}

/// <summary>False (reported as an error) unless the answer is y.</summary>
static bool Confirm(string question)
{
    Console.Write(question + " [y/N] ");
    var piped = Console.IsInputRedirected;
    var answer = piped ? Elevated.ReadAnswer(Console.OpenStandardInput()) : Console.ReadLine()?.Trim();
    if (piped) Console.WriteLine(answer);
    if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) return true;
    Fail(answer == null ? "cancelled: no answer on stdin (pipe y to confirm)" : $"cancelled: the answer was '{answer}', not y");
    return false;
}

static async Task<ScsKiller> Open(bool rescan = false)
{
    var k = ScsKiller.CreateDefault();
    k.Log = new StderrLog();   // synchronous: a message logged just before exit still prints
    await (rescan ? k.RescanAsync(CancellationToken.None) : k.ScanAsync(CancellationToken.None));
    return k;
}

static void BackgroundPriority()   // index/plan/materialize run in this process; the warm itself gets --priority idle
{
    using var self = Process.GetCurrentProcess();
    self.PriorityClass = ProcessPriorityClass.Idle;
}

static string Hms(TimeSpan? t) => t?.ToString(@"h\:mm\:ss") ?? "-";

static void Row(GameState s) =>
    Console.WriteLine($"{s.Status,-14} {s.Game.Id,-24} {s.Game.Name}{(s.AntiCheat != AntiCheat.None ? $"  [{s.AntiCheat}]" : "")}{(s.KnownStutter is { } ks ? $"  [known stutter: {ks.Severity.ToString().ToLowerInvariant()}]" : "")}{(s.RecorderInstalled ? "  [recorder]" : "")}{(s.CacheOnDisk is { } c ? $"  [cache {Format.Bytes(c)}]" : "")}  - {s.StatusReason}");

/// <summary>The tags' <see cref="Format.Middleware"/> lines joined with " · ", with the DLL names when <paramref name="dlls"/>.</summary>
static string MiddlewareLine(IReadOnlyList<MiddlewareTag> tags, bool dlls) => string.Join(" · ", tags.Select(t =>
    Format.Middleware(t) + (dlls ? $" [{string.Join(", ", t.Dlls)}]" : "")));

static GameState Match(IReadOnlyList<GameState> games, string query)
{
    var hit = games.FirstOrDefault(s => s.Game.Id.Equals(query, StringComparison.OrdinalIgnoreCase))
              ?? games.FirstOrDefault(s => s.Game.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
    if (hit != null) return hit;
    var many = games.Where(s => s.Game.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    return many.Count switch
    {
        1 => many[0],
        0 => throw new ArgumentException($"no game matches '{query}'"),
        _ => throw new ArgumentException($"'{query}' matches several games: {string.Join(", ", many.Select(s => $"{s.Game.Name} ({s.Game.Id})"))}"),
    };
}

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    if (i < 0) return null;
    return i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : throw new ArgumentException($"{name} needs a value");
}

/// <summary>Refuses an option the command doesn't take (a typo would otherwise run with the default).</summary>
void Only(params string[] known)
{
    if (args.Skip(1).FirstOrDefault(a => a.StartsWith("--") && !known.Contains(a)) is { } unknown) throw new ArgumentException($"unknown option '{unknown}'");
}

async Task<int> Scan(bool rescan)
{
    var k = await Open(rescan);
    Console.WriteLine($"GPU: {k.Vendor.Gpu.Name}, driver {k.Vendor.Gpu.DriverVersion} ({k.Vendor.Caps.Profile})");
    foreach (var s in k.Games) Row(s);
    return 0;
}

async Task<int> Status(string? query)
{
    var k = await Open();
    if (query == null)
    {
        foreach (var s in k.Games)
        {
            Row(s);
            if (s.Middleware is { Count: > 0 } m) Console.WriteLine($"{"",15}middleware: {MiddlewareLine(m, false)}");
        }
        return 0;
    }
    var g = Match(k.Games, query);
    var rec = k.Store.LoadGame(g.Game.Id);
    Console.WriteLine($"""
        {g.Game.Name} ({g.Game.Id}, {g.Game.Store})
          install    {g.Game.InstallDir}
          exe        {g.Game.ExePath}
          runs as    {(rec.LaunchedExeName is { } l0 ? $"{l0} (seen {rec.LaunchedExeSeenAt:yyyy-MM-dd HH:mm})" : "not seen yet")}; warms stage {ScsKiller.WarmExeName(g.Game, rec)}
          engine     {(g.Engine is { } e ? $"{e.Family} {e.Version} {e.Fork} {e.GraphicsApi}{(e.Encrypted ? " encrypted" : "")}" : "-")}
          status     {g.Status}: {g.StatusReason}
          anti-cheat {g.AntiCheat}
          shaders    {g.ShaderCount?.ToString() ?? "-"}
          plan       {(g.Plan is { } p ? $"{p.Recorded} recorded + {p.Generated} generated + {p.D3D11Shaders} DirectX 11 shaders + {p.MiddlewareItems} middleware, {p.RootSignatures} root sigs, rule verified: {p.RootSigRuleVerified}" : "-")}
          stutter    {(g.KnownStutter is { } st ? $"known, {st.Severity.ToString().ToLowerInvariant()}: {st.Reason} ({st.Source}, checked {st.Date})" : "-")}
          middleware {(g.Middleware is { Count: > 0 } mw ? MiddlewareLine(mw, true) : "-")}
          estimate   {(g.EstimatedCacheBytes is { } est ? Format.Bytes(est) : "-")} of cache, {Hms(g.EstimatedWarmTime)}
          careful    {CarefulLine(g.Careful)}
          warmed     {(g.WarmedAt is { } w ? $"{w:yyyy-MM-dd HH:mm} for driver {g.WarmedDriverVersion} in {Hms(g.LastWarmTime)}" + (ScsKiller.WarmCounts(g.LastWarmFailed ?? 0, g.LastWarmSkipped ?? 0, g.LastWarmCrashed ?? 0) is { } counts ? $"; {counts}" : "") : "never")}
          cache      {(g.CacheOnDisk is { } c ? $"{Format.Bytes(c)} on disk (driver-cache files its warms or the game had open)" : "-")}
          keys       {(rec.CacheKeys.Count > 0 ? string.Join(", ", rec.CacheKeys.Order()) : "-")}{(k.WarmAgs(g.Game.Id) is { } ags ? $" (compiles register its AGS app name {ags.App}, key {k.AgsKey(g.Game.Id)}: the exe name's case doesn't matter)" : AmdAppCache.IsNameHashed(rec.CacheKeys, ScsKiller.WarmExeName(g.Game, rec)) == false ? " (an app profile's key, not the exe name's hash: the name's case doesn't matter)" : "")}
          recorder   {(g.RecorderInstalled ? "installed" : "not installed")}
          recording  {(g.RecordingBytes > 0 ? $"{Format.Bytes(g.RecordingBytes)} (the game folder's files and SCSKiller's copy)" : "-")}, limit {ScsKiller.LimitText(k.Settings.RecordingLimitMB)} per game{(g.RecordingPaused ? $"; {ScsKiller.PausedNote(k.Settings)}" : "")}
          last play  {(g.LastSession is { } l ? $"{Hms(l.Duration)}, {l.Requests} pipelines: {l.FromGameLibrary} from the game's library, {l.CacheHits} cache hits, {l.Compiles} compiles (worst {l.WorstCompileMs:0.0} ms){(l.StartupCompiles > 0 ? $", {l.StartupCompiles} compiles while the game started" : "")}{(l.RayQueryRecompiles > 0 ? $", {l.RayQueryRecompiles} ray-traced pipelines the driver partly recompiles every launch" : "")}{(l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0 ? $"; ray tracing state objects: {l.StateObjectsReady} ready, {l.StateObjectsCompiled} compiled{(l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled} compiled while the game started" : "")}" : "")}" : "-")}
          1st launch {(rec.FirstLaunch is { } fl ? $"{fl.At.LocalDateTime:yyyy-MM-dd HH:mm}, after the last compile: {fl.Hits} cache hits, {fl.Compiles} compiles ({fl.Compiled * 100:0.0}% compiled)" : "-")}
        """);
    return 0;
}

static string CarefulLine(CarefulCompile? c) => c is not { } x ? "- (AMD only)"
    : (x.On ? $"on: the recorded pipelines compile in passes on at most {ScsKiller.AmdCarefulThreads} threads, the rest at full speed" : "off (compile <game> --careful turns it on)")
      + (x.Recorded == 0 ? "; no recording yet, so nothing compiles carefully" : x.Estimate is { } t ? $"; a careful compile takes about {ScsKiller.Duration(t)}" : "");

static void PrintQueueChanges(ScsKiller k)
{
    var lastPrint = DateTime.MinValue;
    var lastStage = new Dictionary<string, QueueStage>();
    k.QueueChanged += q =>
    {
        lock (lastStage)
        {
            bool stageChanged = !lastStage.TryGetValue(q.GameId, out var st) || st != q.Stage;
            if (!stageChanged && DateTime.Now - lastPrint < TimeSpan.FromSeconds(2)) return;
            lastStage[q.GameId] = q.Stage;
            lastPrint = DateTime.Now;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {q.GameId} {q.Stage}{ScsKiller.ProgressText(q)}{(q.Error != null ? " - " + q.Error : "")}");
        }
    };
}

async Task<int> RunQueue(ScsKiller k, IReadOnlyList<GameState> targets, bool whenIdle = false)
{
    if (targets.Count == 0) { Console.WriteLine("nothing to compile"); return 0; }
    PrintQueueChanges(k);
    bool cancelled = false;
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancelled = true;
        Console.WriteLine("stopping (in-flight compiles finish so the driver writes its cache)...");
        foreach (var q in k.Queue.Where(q => q.Stage == QueueStage.Waiting)) k.Remove(q.GameId);
        k.StopQueue();
    };
    foreach (var t in targets)
        if (whenIdle) k.EnqueueWhenIdle(t.Game.Id); else k.Enqueue(t.Game.Id);
    if (whenIdle) Console.WriteLine($"waiting until the PC has been idle for {k.IdleAfter.TotalMinutes:0} min (pauses on input)");
    else k.StartQueue();
    await k.WhenQueueIdle();
    return !cancelled && k.Queue.Count > 0 && k.Queue.All(q => q.Stage == QueueStage.Done) ? 0 : 1;   // Ctrl+C: not everything compiled
}

// A debugging shell: the queue lives in this process, so commands come on stdin (or piped: echo add ff7 | scskiller queue).
async Task<int> QueueShell()
{
    var k = await Open();
    PrintQueueChanges(k);
    string Id(string[] w, int from, int to) => Match(k.Games, string.Join(' ', w[from..to])).Game.Id;
    var lines = (args.Length > 1 ? [string.Join(' ', args[1..])] : Array.Empty<string>()).Concat(StdinLines());
    foreach (var line in lines)
    {
        var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (w.ElementAtOrDefault(0))
            {
                case null: break;
                case "list":
                    var all = k.Queue;
                    var queue = all.Where(q => !q.PlanCheck).ToList();
                    for (int i = 0; i < queue.Count; i++)
                        Console.WriteLine($"{i,3} {queue[i].Stage,-13} {queue[i].GameId}{(queue[i].Note is { } n ? "  (" + n + ")" : "")}");
                    Console.WriteLine(k.QueueRunning ? "queue running" : "queue not running");
                    if (ScsKiller.PlanCheckLine(all) is { } checks) Console.WriteLine(checks);
                    break;
                case "add" when w.Length > 1: k.Enqueue(Id(w, 1, w.Length)); break;
                case "remove" when w.Length > 1: k.Remove(Id(w, 1, w.Length)); break;
                case "move" when w.Length > 2 && int.TryParse(w[^1], out var to): k.MoveInQueue(Id(w, 1, w.Length - 1), to); break;
                case "start": k.StartQueue(); break;
                case "stop": k.StopQueue(); break;
                case "wait": await k.WhenQueueIdle(); break;
                case "quit": k.StopQueue(); await k.WhenQueueIdle(); return 0;   // graceful: the driver writes its cache
                default: Console.WriteLine("list | add <game> | move <game> <index> | remove <game> | start | stop | wait | quit"); break;
            }
        }
        catch (ArgumentException e) { Console.WriteLine("error: " + e.Message); }
    }
    await k.WhenQueueIdle();   // end of input: let a started queue finish
    return 0;

    static IEnumerable<string> StdinLines()
    {
        while (Console.ReadLine() is { } l) yield return l;
    }
}

async Task<int> CacheClear()
{
    if (args.Length < 3) return Fail("cache clear <game>");
    var k = await Open();
    var g = Match(k.Games, args[2]);
    var precache = args.Contains("--game-precache");
    var parts = k.GameCaches(g.Game.Id, precache);
    var gap = k.DriverCacheGap(g.Game.Id);
    if (parts.Count == 0) return Fail($"no shader-cache files of {g.Game.Name} were found" + (gap != null ? $" ({gap})" : ""));
    foreach (var p in parts)
    {
        Console.WriteLine($"{p.Name}  {Format.Bytes(p.Bytes)}");
        foreach (var f in p.Files) Console.WriteLine($"  {f}");
    }
    if (gap != null) Console.WriteLine($"note: {gap}");
    if (!precache && k.GameCaches(g.Game.Id, true).Where(p => p.Name is ScsKiller.PipelinePart or ScsKiller.PrecachePart).ToList() is { Count: > 0 } own)
        Console.WriteLine($"kept: the game's own caches ({string.Join(", ", own.Select(p => $"{p.Name} {Format.Bytes(p.Bytes)}"))}); --game-precache deletes them too");
    if (!Confirm($"Delete {g.Game.Name}'s shader caches ({string.Join(", ", parts.Select(p => $"{p.Name} {Format.Bytes(p.Bytes)}"))}, the files above)? " +
                 "Its next run is cold; it shows as not warmed afterwards.")) return 1;
    k.ClearGameCache(g.Game.Id, precache);   // a refusal throws InvalidOperationException: printed as "error: <reason>"
    Console.WriteLine($"cleared {g.Game.Name}'s shader caches");
    return 0;
}

async Task<int> Compile()
{
    if (args.Length < 2) return Fail("compile <game|--all-ready> [--threads N] [--idle | --when-idle] [--careful | --fast]");
    Only("--all-ready", "--threads", "--idle", "--when-idle", "--careful", "--fast");
    int? threads = Opt("--threads") is { } t ? int.TryParse(t, out var n) && n > 0 ? n : throw new ArgumentException("--threads takes a number of threads") : null;
    var k = await Open();
    if (threads != null) k.ThreadsOverride = threads;
    bool whenIdle = args.Contains("--when-idle");
    k.Background = args.Contains("--idle");
    if (k.Background || whenIdle) BackgroundPriority();
    var targets = args[1] == "--all-ready"
        ? k.Games.Where(s => s.Status is GameStatus.Ready or GameStatus.Stale).ToList()
        : [Match(k.Games, args[1])];
    if (args.Contains("--careful") || args.Contains("--fast"))
        foreach (var g in targets)
        {
            k.SetCarefulCompile(g.Game.Id, args.Contains("--careful"));
            Console.WriteLine($"{g.Game.Name}: {CarefulLine(k.Games.Single(s => s.Game.Id == g.Game.Id).Careful)}");
        }
    return await RunQueue(k, targets, whenIdle);
}

async Task<int> RewarmStale(bool ifDriverChanged)
{
    var k = await Open();
    var stale = ifDriverChanged ? k.DriverStaleGames() : k.StaleGames();
    if (stale.Count == 0) { Console.WriteLine("nothing to re-warm"); return 0; }
    switch (k.Settings.OnDriverUpdate)
    {
        case DriverUpdateMode.Off:
            Console.WriteLine($"{stale.Count} game(s) need a re-warm; automatic re-warm is off.");
            return 0;
        case DriverUpdateMode.Ask:
            if (!k.ShouldNotifyStale()) { Console.WriteLine($"{stale.Count} game(s) need a re-warm; the user skipped them for this driver."); return 0; }
            if (ScheduledTask.AppExe() is { } app)   // the app shows the notification (compile now / when idle / skip)
            {
                Process.Start(new ProcessStartInfo(app, ScheduledTask.DriverUpdatedArg) { UseShellExecute = false })?.Dispose();
                Console.WriteLine($"{stale.Count} game(s) need a re-warm: notified via {app}");
                return 0;
            }
            Console.WriteLine($"{stale.Count} game(s) need a re-warm (driver {k.Vendor.Gpu.DriverVersion}):");
            foreach (var s in stale) Row(s);
            Console.WriteLine("Run 'scskiller compile <game>' or open SCSKiller to re-warm them.");
            return 0;
        default:
            BackgroundPriority();
            return await RunQueue(k, stale, whenIdle: true);
    }
}

int Cache()
{
    var v = GpuBackends.Detect();
    Console.WriteLine($"GPU: {v.Gpu.Name} ({v.Vendor}), driver {v.Gpu.DriverVersion}, profile {v.Caps.Profile}");
    if (args.Length > 1 && args[1] == "set")
    {
        if (args.Length < 3) return Fail("cache set <GB|unlimited|default>");
        if (!v.Caps.CacheSizeConfigurable) return Fail("this GPU's cache size is not configurable");
        var limit = args[2].ToLowerInvariant() switch
        {
            "default" => new CacheLimit(null, true),
            "unlimited" => new CacheLimit(null, false),
            var n when double.TryParse(n, System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb > 0 => new CacheLimit((long)(gb * 1073741824), false),
            _ => throw new ArgumentException("size must be a number of GB, 'unlimited' or 'default'"),
        };
        if (!Elevated.IsAdmin) return Fail("changing the driver's shader cache size needs an elevated (administrator) prompt");
        var what = limit.IsDriverDefault ? "the driver default" : limit.Bytes == null ? "unlimited" : Format.Bytes(limit.Bytes);
        if (!args.Contains(Elevated.YesArg) && !Confirm($"Set the global NVIDIA shader cache size to {what}? This changes a driver setting for every game."))
            return 1;
        v.SetCacheLimit(limit);
        Outcome.Message = $"Shader cache limit set to {what}.";
    }
    var usage = v.GetCacheUsage();
    if (v is AmdBackend)   // GetCacheUsage sums both folders
        Console.WriteLine($"cache: {(usage.UpperBound ? "<= " : "")}{Format.Bytes(usage.BytesOnDisk)} on disk = {AmdBackend.CacheDir} {Format.Bytes(AmdBackend.Bytes(AmdBackend.CacheDir))} (D3D12)" +
                          $" + {AmdBackend.D3D11CacheDir} {Format.Bytes(AmdBackend.Bytes(AmdBackend.D3D11CacheDir))} (D3D11)");
    else Console.WriteLine($"cache: {usage.Path} {(usage.UpperBound ? "<= " : "")}{Format.Bytes(usage.BytesOnDisk)} on disk");
    var l =v.GetCacheLimit();
    Console.WriteLine($"limit: {(l == null ? "not readable" : l.Bytes == null ? "unlimited" : Format.Bytes(l.Bytes))}{(l?.IsDriverDefault == true ? " (driver default)" : "")}");
    if (v is NvidiaBackend nv)
    {
        var (id, raw, def) = nv.ReadCacheSetting();
        Console.WriteLine($"       setting 0x{id:X8} '{NvidiaBackend.CacheSizeSettingName}' = {(raw is { } r ? $"0x{r:X8}" : "not set")}, driver default 0x{def:X8}");
    }
    return 0;
}

async Task<int> Record()
{
    bool alongside = args.Length > 3 && args[1] == "alongside" && args[3] is "on" or "off";
    if (!alongside && (args.Length < 3 || args[1] is not ("install" or "uninstall" or "clear")))
        return Fail("record install|uninstall|clear <game> | record alongside <game> on|off");
    var k = await Open();
    var g = Match(k.Games, args[2]);
    if (args[1] == "clear")
    {
        if (g.RecordingBytes == 0) return Fail($"{g.Game.Name} has no recording");
        if (!Confirm($"Delete {g.Game.Name}'s recording ({Format.Bytes(g.RecordingBytes)})? The recorded pipelines are removed; they're compiled again only if " +
                     "the game creates them again while recording, and the compile plan goes back to what the game's files give.")) return 1;
        k.ClearRecording(g.Game.Id);   // a refusal throws InvalidOperationException: printed as "error: <reason>"
        Console.WriteLine($"cleared {g.Game.Name}'s recording");
        return 0;
    }
    if (alongside)
    {
        var mod = k.RecorderMod(g.Game.Id);
        k.SetRecordAlongsideMod(g.Game.Id, args[3] == "on");
        var s = k.Games.Single(x => x.Game.Id == g.Game.Id);
        Console.WriteLine($"record alongside {mod ?? "a mod"}: {args[3]}; recorder {(s.RecorderInstalled ? "installed" : "not installed")}"
            + ((s.RecorderSkip == ScsKiller.SkipModNotChainable ? ScsKiller.NotChainableReason(s.RecorderMod) : s.RecorderSkip ?? s.RecorderNote) is { } why ? $" ({why})" : ""));
        return 0;
    }
    if (args[1] == "install") k.InstallRecorder(g.Game.Id); else k.UninstallRecorder(g.Game.Id);
    Console.WriteLine($"recorder {args[1]}ed: {Path.GetDirectoryName(g.Game.ExePath)}");
    return 0;
}

async Task<int> Key()
{
    if (args.Length < 3) return Fail("key <game> <hex>");
    var k = await Open();
    var g = Match(k.Games, args[1]);
    if (!k.SetEncryptionKey(g.Game.Id, args[2])) return Fail($"that key doesn't open {g.Game.Name}'s files");
    await k.RescanAsync(CancellationToken.None);
    Console.WriteLine($"key accepted for {g.Game.Name}: {k.Games.First(s => s.Game.Id == g.Game.Id).StatusReason}");
    return 0;
}

static async Task<(ScsKiller K, GameState G, IEngineReader Reader, EngineInfo Engine, ShaderIndex Index)> OpenIndex(string query, IProgress<string>? log = null)
{
    var k = await Open();
    var g = Match(k.Games, query);
    var reader = ScsKiller.DefaultReaders();
    var engine = g.Engine ?? reader.Detect(g.Game) ?? throw new InvalidOperationException($"{g.Game.Name}: engine not supported");
    return (k, g, reader, engine, reader.Index(g.Game, engine, log, CancellationToken.None));
}

async Task<int> Index()
{
    if (args.Length < 2 || Opt("--out") is not { } outDir) return Fail("index <game> --out <dir>");
    var (_, _, _, engine, index) = await OpenIndex(args[1], new Progress<string>(Console.WriteLine));
    Directory.CreateDirectory(outDir);
    await File.WriteAllTextAsync(Path.Combine(outDir, "index.json"), JsonSerializer.Serialize(new { engine, index }, AppStore.Json));
    Console.WriteLine($"{index.Shaders.Count} shaders, {index.Maps.Count} shader maps, content hash {index.ContentHash} -> {outDir}");
    return 0;
}

async Task<int> RehydrateCommand()
{
    if (args.Length < 3 || Opt("--out") is not { } outDb) return Fail("rehydrate <game> <hash-only.db> --out <db> [--expect <content hash>]");
    var (_, g, reader, engine, index) = await OpenIndex(args[1]);
    Console.WriteLine($"index content hash {index.ContentHash}");
    var r = SCSKiller.Core.Planning.Rehydrate.Run(args[2], outDb, g.Game, engine, reader, index, Opt("--expect"),
        moreBlobs: h => SCSKiller.Core.Planning.Middleware.Blobs(g.Game, h));
    Console.WriteLine($"referenced {r.Referenced}, already in db {r.AlreadyPresent}, found {r.Found} ({r.FoundInMiddleware} in middleware DLLs), missing {r.Missing.Count}" +
        (r.ContentHashMatches is { } m ? $", content hash {(m ? "matches" : "DIFFERS")}" : "") + $" -> {outDb} ({r.OutputBytes:N0} bytes)");
    foreach (var h in r.Missing) Console.WriteLine($"  missing {h}");
    return r.Complete ? 0 : 2;
}

async Task<int> ReferenceExport()
{
    if (args.Length < 3) return Fail("reference export <game> [--out <file>]");
    var (k, g, _, _, index) = await OpenIndex(args[2]);
    // middleware and runtime-built shaders aren't in the index: the recording has their bytes
    var recording = Path.Combine(k.Store.GameDir(g.Game.Id), "recording.db");
    var seen = File.Exists(recording)
        ? SCSKiller.Core.Planning.PsoDb.Read(recording).Where(r => r.Tag == 'B' && r.Payload.Length > 20 && !SCSKiller.Core.Carved.Dxbc.IsRootSignatureOnly(r.Payload.AsSpan(20)))
            .Select(r => Convert.ToHexStringLower(r.Payload.AsSpan(0, 20)))
        : [];
    var shaders = index.Shaders.Keys.Concat(seen).Distinct().Order(StringComparer.Ordinal).ToArray();
    var outFile = Opt("--out") ?? g.Game.Id.Replace(':', '_') + ".reference.json";
    await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(new
    {
        content_hash = index.ContentHash,
        store_build_key = g.Game.Version is { } v ? $"{g.Game.Id}@{v}" : null,
        shaders,
    }));
    Console.WriteLine($"{shaders.Length} shaders ({index.Shaders.Count} indexed), content hash {index.ContentHash} -> {outFile}");
    return 0;
}

// Read-only. Run it before and after switching something in the NVIDIA App and diff the two outputs.
static int NvidiaSnapshot()
{
    if (GpuBackends.Detect() is not NvidiaBackend nv) return Fail("no NVIDIA GPU");
    Console.WriteLine($"driver {nv.Gpu.DriverVersion}");
    Console.WriteLine($"Auto Shader Compilation: {nv.GetAutoShaderCompilation()?.ToString() ?? "not readable"} (setting 0x{NvidiaBackend.AutoShaderCompilationId:X8})");
    Console.WriteLine($"NvOSC.exe: {NvidiaBackend.NvOscPath() ?? "not found"}");
    Console.WriteLine("base profile:");
    foreach (var (id, name, type, value) in NvidiaBackend.BaseProfileSettings())
        Console.WriteLine($"  0x{id:X8} type {type} = 0x{value:X8}  {name}");
    foreach (var task in new[] { $@"\Users\{Environment.UserName}{NvidiaBackend.AutoShaderTaskName}", NvidiaBackend.AutoShaderTaskName })
    {
        var psi = new ProcessStartInfo("schtasks.exe", ["/Query", "/TN", task, "/V", "/FO", "LIST"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Console.WriteLine($"task {task}: " + (p.ExitCode == 0 ? output.Trim() : "not registered"));
    }
    Console.WriteLine($"NvOSC.exe running: {Process.GetProcessesByName("NvOSC").Length > 0}");
    return 0;
}

// What the Settings toggle does, for an elevated prompt: the driver setting + NvOSC.exe -register/-deregister.
int NvidiaAutoShader()
{
    if (!Enum.TryParse<AutoShaderCompilation>(args.ElementAtOrDefault(1), true, out var level) || !Enum.IsDefined(level) || int.TryParse(args[1], out _))
        return Fail("nvidia-auto-shader off|low|medium|high");
    if (GpuBackends.Detect() is not NvidiaBackend nv) return Fail("no NVIDIA GPU");
    if (!Elevated.IsAdmin) return Fail("switching NVIDIA Auto Shader Compilation needs an elevated (administrator) prompt");
    // NvOSC.exe -register registers the idle task for the account running it: refuse when UAC elevated a different
    // administrator (over-the-shoulder), whose DXCache it would compile into. Same account + elevated token is fine.
    if (Opt(Elevated.ForUserArg) is { } sid && sid != Elevated.CurrentUserSid)
        return Fail($"Windows elevated as a different account ({WindowsIdentity.GetCurrent().Name}), so the NVIDIA task would compile for that account, " +
                    "not for you. Sign in to Windows with an administrator account, or ask an administrator to add yours to Administrators.");
    if (!args.Contains(Elevated.YesArg) && !Confirm($"Set NVIDIA Auto Shader Compilation to {level}? This changes a global driver setting and the driver's idle task."))
        return 1;
    nv.SetAutoShaderCompilation(level);
    Console.WriteLine($"now: {nv.GetAutoShaderCompilation()?.ToString() ?? "not readable"}");
    Outcome.Message = level == AutoShaderCompilation.Off ? "Auto Shader Compilation turned off." : $"Auto Shader Compilation turned on ({level}).";
    return 0;
}

int TaskCommand()
{
    switch (args.ElementAtOrDefault(1))
    {
        case "register":
            ScheduledTask.Register(ScheduledTask.TaskExe() ?? Environment.ProcessPath!);
            Console.WriteLine($"registered task {ScheduledTask.Name}");
            return 0;
        case "unregister":
            ScheduledTask.Unregister();
            Console.WriteLine($"removed task {ScheduledTask.Name}");
            return 0;
        default:
            return Fail("task register|unregister");
    }
}

static int FetchCodecs()
{
    // Packaging (publish.ps1): the pinned DLLs next to this exe, which seed each user's codecs folder (Codecs).
    try
    {
        Console.WriteLine(Codecs.Ensure(OodleHelper.OodleFileName, Codecs.DownloadOodle, AppContext.BaseDirectory));
        Console.WriteLine(Codecs.Ensure(ZlibHelper.DllName, p => ZlibHelper.DownloadDll(p, null!), AppContext.BaseDirectory));
        return 0;
    }
    catch (InvalidDataException e) { return Fail(e.Message); }
}

/// <summary>What the command's last word was (its success line or the error), for <see cref="Elevated.ResultArg"/>.</summary>
static class Outcome { public static string? Message; }

// Not Progress<T>: it posts to the thread pool, so its last messages could be lost when the process exits.
sealed class StderrLog : IProgress<string>
{
    public void Report(string value) => Console.Error.WriteLine("  " + value);
}
