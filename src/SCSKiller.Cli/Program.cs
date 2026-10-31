using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using CUE4Parse.Compression;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Unreal;
using SCSKiller.Core.Vendors;

const string Usage = """
    usage: scskiller <command>
      scan [--rescan]                             discover games and show their status (--rescan: redo engine detection)
      add <exe> [<install folder>]                add a game no store lists, by its exe (with its folder: the recorder may go there)
      exe <game> <exe>|auto                       a store game's exe: one of its install, or auto for the one SCSKiller picks
      status [game]                               all games, or one in detail
      compile <game|--all-ready> [--threads N] [--idle | --when-idle] [--careful | --fast]
                                                  --idle: background priority. --when-idle: also only while nobody uses the PC.
                                                  --careful (AMD): this and later compiles of the game compile its recorded
                                                  pipelines in passes on few threads, so the driver keeps more of them (slower).
                                                  --fast: back to one pass
      queue [command]                             debug shell for the compile queue: the command, then one per line on stdin:
                                                  list | add <game> | move <game> <index> | remove <game> | start | stop | wait | quit
      before-after <game> [--minutes N] [--no-overlay] [--max]  debug: a before and after recording without the app (you start the game)
      rewarm-stale [--if-driver-changed]          what the scheduled task runs, following the driver-update setting
      cache get | cache set <GB|unlimited|default>
      cache page [--files] [delete <item>|everything [--others]]  the Cache page: its folders, groups and items, and a
                                                  delete as its Delete or Clear everything makes it (asks first)
      cache clear <game> [--game-precache]        delete the game's shader caches: driver, Windows (D3DSCache) (asks first).
                                                  --game-precache: also the caches the game writes itself (*.ushaderprecache,
                                                  Dawn's bin\pipelineLibrary.cache, RE Engine's shader.cache2 and
                                                  piplinelist.bin, GTA V Enhanced's and HELLDIVERS 2's pipeline libraries),
                                                  which it rebuilds at its next start
      nvidia-snapshot                             read-only: every global DRS setting, Auto Shader Compilation, its idle task
      nvidia-auto-shader off|low|medium|high      switch NVIDIA's Auto Shader Compilation (admin, asks first)
        (cache set / nvidia-auto-shader, as the app runs them elevated: --yes skips the question, --result <file>
         writes the outcome as a JSON line, --for-user <SID> refuses a different account's elevation)
      record install|uninstall <game>
      record remove-all                           take the recorder out of every game, as SCSKiller's uninstall does
      record clear <game>                         delete the game's recording (the recorder's data files and SCSKiller's copy, asks first)
      record alongside <game> on|off              let the recorder chain to a mod's d3d12.dll (renamed, put back on removal)
      shared <game> on|off|follow                 use the shared shader hashes for the game: off, nothing is downloaded for it and its plans leave them out, follow = Settings
      key <game> <hex>                            give a game's AES key or game key (verified, stored locally only)
      index <game> --out <dir>                    debug: dump the engine reader's shader index
      rehydrate <game> <hash-only.db> --out <db> [--expect <content hash>]
                                                  add the shader bytes a hash-only recording references, from the install
      reference export <game> [--out <file>]      the game build's shader list for the community server's `admin reference import`:
                                                  the index's shaders and those this PC's recording saw (default <game id>.reference.json)
      task register|unregister                    the logon/idle "re-warm after a driver update" task
      shader-cleanup keep|allow                   keep Windows' automatic disk cleanup from clearing compiled shaders, or allow it again (admin)
      steam-place <game> [--remove]               Linux: put the game's compiled cache in Steam's cache folder for it
                                                  once, or take it out, and show what each file holds
      fetch-codecs <dir>                          packaging: the pinned Oodle/zlib DLLs in <dir>, downloaded if missing
      --version                                   the CLI's version
    <game> is an id (steam:2909400) or a case-insensitive part of the name.
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;  // game names carry ™ and ®
RouteFailover.Product = "SCSKiller-CLI";
if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
// before anything reads the data folder: a portable copy's first read copies the installed one's
if (args[0] is "-v" or "--version" or "version") { Console.WriteLine(AppVersion.Current); return 0; }
if (args[0] != "fetch-codecs" && AppStore.RefusesLiveData()) { Console.Error.WriteLine(AppStore.DevBuildRefusal); return 1; }
// The app is handing over to Update.exe, which replaces this folder: start nothing (the scheduled task runs again later).
// Packaging never reads the app's data folder.
if (args[0] != "fetch-codecs" && Busy.Applying(AppStore.DefaultDir, DateTimeOffset.UtcNow))
{
    const string busy = "an update is being installed, try again in a minute";
    Console.Error.WriteLine(busy);
    // run elevated by the app, the exit code alone (0, for the scheduled task) would read as done
    try { if (Opt(Elevated.ResultArg) is { } file) Elevated.WriteResult(file, false, busy); }
    catch (ArgumentException) { }
    return 0;
}
// taking the recorders and the task out stays possible on a refused alpha
if (args[0] != "fetch-codecs" && args is not ["record", "remove-all", ..] and not ["task", "unregister", ..]
    && AlphaGate.Needed(AppVersion.Current) && !AlphaGate.Passed(AppStore.DefaultDir))
{
    Console.Error.WriteLine(AlphaGate.Cli);
    return 1;
}
int code;
try
{
    code = args[0] switch
    {
        "scan" => await Scan(args.Contains("--rescan")),
        "status" => await Status(args.Length > 1 ? args[1] : null),
        "add" => await AddGame(),
        "exe" => await GameExe(),
        "compile" => await Compile(),
        "queue" => await QueueShell(),
        "before-after" => await BeforeAfterCommand(),
        "rewarm-stale" => await RewarmStale(args.Contains("--if-driver-changed")),
        "cache" => args.ElementAtOrDefault(1) switch { "clear" => await CacheClear(), "page" => await CachePage(), _ => Cache() },
        "record" => await Record(),
        "shared" => await UseShared(),
        "index" => await Index(),
        "rehydrate" => await RehydrateCommand(),
        "reference" when args.ElementAtOrDefault(1) == "export" => await ReferenceExport(),
        "key" => await Key(),
        "task" => TaskCommand(),
        "shader-cleanup" => ShaderCleanup(),
        "proton-env" => await ProtonEnv(),
        "steam-place" => await SteamPlace(),
        "nvidia-snapshot" => NvidiaSnapshot(),
        "nvidia-auto-shader" => NvidiaAutoShader(),
        "fetch-codecs" => FetchCodecs(args.ElementAtOrDefault(1) ?? throw new ArgumentException("usage: scskiller fetch-codecs <dir>")),
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
    var account = new Account(AppStore.DefaultDir);
    k.DbEntitled = () => account.SignedIn;   // entitlements are never read here: signed in counts as entitled
    await (rescan ? k.RescanAsync(CancellationToken.None) : k.ScanAsync(CancellationToken.None));
    return k;
}

static void BackgroundPriority()   // index/plan/materialize run in this process; the warm itself gets --priority idle
{
    using var self = Process.GetCurrentProcess();
    self.PriorityClass = ProcessPriorityClass.Idle;
}

static string StatusName(GameState s) => ScsKiller.NeedsOfflineSession(s) ? "NeedsOfflineSession" : ScsKiller.NeedsSteamStart(s) ? "NeedsSteamStart" : s.Status.ToString();

static string Hms(TimeSpan? t) => t?.ToString(@"h\:mm\:ss") ?? "-";

// why the recorder stays out: its skip, else its note
static string? RecorderWhy(GameState s) => s.RecorderSkip == ScsKiller.SkipModNotChainable ? ScsKiller.NotChainableReason(s.RecorderMod) : s.RecorderSkip ?? s.RecorderFailure ?? s.RecorderNote;

static void Row(GameState s) =>
    Console.WriteLine($"{StatusName(s),-19} {s.Game.Id,-24} {s.Game.Name}{(s.AnyAntiCheat ? $"  [{ScsKiller.AntiCheatName(s)}]" : "")}{(s.KnownStutter is { } ks ? $"  [known stutter: {ks.Severity.ToString().ToLowerInvariant()}]" : "")}{(s.NoStutter != null ? "  [no shader stutter]" : "")}{(s.RecorderInstalled ? "  [recorder]" : "")}{(s.CacheOnDisk is { } c ? $"  [cache {Format.Bytes(c)}]" : "")}  - {(ScsKiller.NeverRecorded(s) ? $"{ScsKiller.NothingRecordedTitle}. {s.RecorderRefused}" : s.StatusReason)}");

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

// --adapter-luid: the adapter the app that asked runs on, which a pick since then (Settings.Gpu) only changes at its restart
IGpuVendorBackend RunningGpu()
{
    if (Opt(ScsKiller.AdapterArg) is not { } hex) return ScsKiller.DetectGpu();
    return long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var luid)
        && GpuBackends.Adapters().FirstOrDefault(a => a.Gpu.AdapterLuid == luid) is { } running
        ? ScsKiller.DetectGpu(GpuBackends.ChoiceOf(running))
        : throw new InvalidOperationException("the GPU SCSKiller runs on isn't listed any more (a driver reload): restart SCSKiller and try again");
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
    if (GpuBackends.DriverNote(k.Vendor) is { } note) Console.WriteLine(note);
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
          runs as    {(rec.LaunchedExeName is { } l0 ? $"{l0} (seen {rec.LaunchedExeSeenAt:yyyy-MM-dd HH:mm})" : "not seen yet")}, warms stage {ScsKiller.WarmExeName(g.Game, rec)}
          engine     {(g.Engine is { } e ? string.Join(' ', new[] { Format.Engine(e) + (e.VersionGuessed ? " (guessed)" : ""), e.Fork, e.GraphicsApi }.OfType<string>()) + (e.Encrypted ? " encrypted" : "") : "-")}
          status     {StatusName(g)}: {g.StatusReason}{(ScsKiller.DeliveryLine(g) is { } asd ? $"\n             {asd}" : "")}{(ScsKiller.CleanupLine(g) is { } cleanup ? $"\n             {cleanup}" : "")}
          anti-cheat {(g.AnyAntiCheat ? ScsKiller.AntiCheatName(g) : "None")}
          shaders    {g.ShaderCount?.ToString() ?? "-"}
          plan       {(g.Plan is { } p ? $"{p.Recorded} recorded + {p.Generated} generated + {p.D3D11Shaders} DirectX 11 shaders{(p.D3D11Draws > 0 ? $" ({p.D3D11Draws} vertex shaders with recorded layouts)" : "")} + {p.MiddlewareItems} middleware, {p.RootSignatures} root sigs, rule verified: {p.RootSigRuleVerified}" : "-")}
          stutter    {(g.KnownStutter is { } st ? $"known, {st.Severity.ToString().ToLowerInvariant()}: {st.Reason} ({st.Source}, checked {st.Date})" : g.NoStutter is { } ns ? $"none: {ns}" : "-")}
          middleware {(g.Middleware is { Count: > 0 } mw ? MiddlewareLine(mw, true) : "-")}
          estimate   {(g.EstimatedCacheBytes is { } est ? Format.Bytes(est) : "-")} of cache, {Hms(g.EstimatedWarmTime)}
          careful    {CarefulLine(g.Careful, g.Plan)}
          shared     {(g.SharedHashes ? "used" : "not used: nothing is downloaded for this game and its plans leave the downloads out")}, {(g.SharedChoice == null ? "follows Settings" : "set for this game (shared <game> follow)")}
          warmed     {(g.WarmedAt is { } w ? $"{w:yyyy-MM-dd HH:mm} for driver {g.WarmedDriverVersion} in {Hms(g.LastWarmTime)}" + (ScsKiller.WarmCounts(g.LastWarmFailed ?? 0, g.LastWarmSkipped ?? 0, g.LastWarmCrashed ?? 0, g.LastWarmDriverSkipped ?? 0, g.LastWarmUnbuilt ?? 0, g.Plan, g.LastWarmInvalid ?? 0, g.LastWarmUnsupported ?? 0, g.LastWarmKnown ?? 0, g.LastWarmFailedBy, g.LastWarmCrashedBefore ?? 0) is { } counts ? $", {counts}" : "") : "never")}
          failed     {ScsKiller.RefusedLine(g) ?? "-"}{(k.WhyFailed(g.Game.Id) is { } failedWhy ? $" Why: {failedWhy}" : "")}
          covered    {(ScsKiller.ShownPercent(g) is { } pct ? $"{pct}%" : "-")} for {(g.Engine is { } api && ScsKiller.ApiKey(api.GraphicsApi) is { } key ? ScsKiller.KeyName(key) : "-")}
          other APIs {(g.KeptApis is { Count: > 0 } kept ? string.Join(", ", kept.Select(a => $"{ScsKiller.KeyName(a.Api)} compiled {a.WarmedAt:yyyy-MM-dd HH:mm} for driver {a.DriverVersion}{(a.CurrentDriver ? "" : " (an older driver)")}")) : "-")}
          cache      {(g.CacheOnDisk is { } c ? $"{Format.Bytes(c)} on disk{(Format.InUseShown(c, g.CacheInUse) ? $", {Format.Bytes(g.CacheInUse)} in use" : "")} (driver-cache files its warms or the game had open)" : "-")}{(g.WindowsCache is { } wc ? $", {Format.Bytes(wc)} in its D3DSCache folder" : "")}
          keys       {(rec.CacheKeys.Count > 0 ? string.Join(", ", rec.CacheKeys.Order()) : "-")}{(k.WarmAgs(g.Game.Id) is { } ags ? $" (compiles register its AGS app name {ags.App} as the game does: the exe name's case doesn't matter)" : AmdAppCache.IsNameHashed(rec.CacheKeys, ScsKiller.WarmExeName(g.Game, rec)) == false ? " (an app profile's key, not the exe name's hash: the name's case doesn't matter)" : "")}
          recorder   {(g.RecorderInstalled ? ScsKiller.Frames11(g.Engine, k.Vendor.Caps) ? "installed, frame times only" : "installed" : "not installed")}{((!g.RecorderInstalled || g.RecorderFailure != null) && RecorderWhy(g) is { } off ? $" ({off})" : "")}{(g.RecorderRefused is { } why ? $". {(ScsKiller.NeverRecorded(g) ? ScsKiller.NothingRecordedTitle + ". " : "")}{why}" : "")}
          recording  {(g.RecordingBytes > 0 ? $"{(g.RecordingBytes < 1 << 20 ? $"{Math.Max(1, g.RecordingBytes >> 10)} KB" : Format.Bytes(g.RecordingBytes))} (the game folder's files and SCSKiller's copy)" : "-")}, limit {ScsKiller.LimitText(k.Settings.RecordingLimitMB)} per game{(g.RecordingPaused ? $". {ScsKiller.PausedNote(k.Settings)}" : "")}
          last play  {(g.LastSession is { } l ? $"{Hms(l.Duration)}, {l.Requests} pipelines: {l.FromGameLibrary} from the game's library, {l.CacheHits} cache hits, {l.Compiles} compiles (worst {l.WorstCompileMs:0.0} ms){(l.StartupCompiles > 0 ? $", {l.StartupCompiles} compiles while the game started or loaded" : "")}{(l.RayQueryRecompiles > 0 ? $", {l.RayQueryRecompiles} ray-traced pipelines the driver partly recompiles every launch" : "")}{(l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0 ? $". Ray tracing state objects: {l.StateObjectsReady} ready, {l.StateObjectsCompiled} compiled{(l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled} compiled while the game started or loaded" : "")}" : "")}" : "-")}
          1st launch {(rec.FirstLaunch is { } fl ? $"{fl.At.LocalDateTime:yyyy-MM-dd HH:mm}, after the last compile: {fl.Hits} cache hits, {fl.Compiles} compiles ({fl.Compiled * 100:0.0}% compiled)" : "-")}
        """);
    foreach (var line in Format.ModNote(g).Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine($"  mod        {line}");
    if (g.RuntimeWarning is { } warning) Console.WriteLine($"  warning    {warning}");
    return 0;
}

static string CarefulLine(CarefulCompile? c, PlanStats? plan) => c is not { } x ? "- (AMD only)"
    : (x.On ? $"on: the recorded pipelines compile in passes on at most {ScsKiller.AmdCarefulThreads} threads, the rest at full speed" : "off (compile <game> --careful turns it on)")
      + (x.Recorded == 0 && plan is { D3D11Shaders: > 0, Recorded: 0 } ? ". Only for DirectX 12: this game's DirectX 11 shaders always compile at full speed"
         : x.Recorded == 0 ? ". No recorded DirectX 12 pipelines, so nothing compiles carefully"
         : x.Estimate is { } t ? $". A careful compile takes about {ScsKiller.Duration(t)}" : "");

static void PrintQueueChanges(ScsKiller k)
{
    var lastPrint = DateTime.MinValue;
    var lastStage = new Dictionary<string, string>();
    k.QueueChanged += q =>
    {
        lock (lastStage)
        {
            var stage = q.Stage + (q.Stage == QueueStage.Merging ? q.Note : null);   // the merge's writing line shows at once
            bool stageChanged = !lastStage.TryGetValue(q.GameId, out var st) || st != stage;
            if (!stageChanged && DateTime.Now - lastPrint < TimeSpan.FromSeconds(2)) return;
            lastStage[q.GameId] = stage;
            lastPrint = DateTime.Now;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {q.GameId} {q.Stage}{ScsKiller.ProgressText(q)}{(q.Error != null ? " - " + q.Error : "")}");
        }
    };
}

async Task<int> RunQueue(ScsKiller k, IReadOnlyList<GameState> targets, bool whenIdle = false)
{
    if (targets.Count == 0) { Console.WriteLine("nothing to compile"); return 0; }
    PrintQueueChanges(k);
    var run = new QueueRun(k);
    void Stop()
    {
        Console.WriteLine("stopping (in-flight compiles finish so the driver writes its cache)...");
        run.Stop();
    }
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Stop();
    };
    if (OperatingSystem.IsLinux()) StopOnSignals(Stop);
    if (run.Start(targets.Select(t => t.Game.Id), whenIdle) && whenIdle)
        Console.WriteLine($"waiting until the PC has been idle for {k.IdleAfter.TotalMinutes:0} min (pauses on input)");
    await k.WhenQueueIdle();
    return !run.Stopped && k.Queue.Count > 0 && k.Queue.All(q => q.Stage == QueueStage.Done) ? 0 : 1;   // Ctrl+C: not everything compiled
}

// SIGTERM (a service manager, kill) and SIGHUP (the terminal closed) stop the queue as Ctrl+C does, a later one too,
// up to the process's exit. A closed terminal fails every write, so the console's writes no longer throw.
static void StopOnSignals(Action stop)
{
    Console.SetOut(TextWriter.Synchronized(new NoThrowWriter(Console.Out)));
    Console.SetError(TextWriter.Synchronized(new NoThrowWriter(Console.Error)));
    foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        SignalHandlers.All.Add(PosixSignalRegistration.Create(signal, c =>
        {
            c.Cancel = true;
            stop();
        }));
}

// The app's before and after flow without a window: the run started, the app's poll loop in this process, the compile and
// the after run continued as the app's buttons would, each change printed. The game is launched by whoever runs this.
async Task<int> BeforeAfterCommand()
{
    if (args.Length < 2 || args[1].StartsWith("--")) return Fail("before-after <game> [--minutes N] [--no-overlay] [--max]");
    int minutes = Opt("--minutes") is { } m ? int.TryParse(m, out var n) && n > 0 ? n : throw new ArgumentException("--minutes takes a number of minutes") : 5;
    var k = await Open();
    k.ManageRecorders = true;   // as the app: the run's recorder goes in, the before run's caches are set aside
    var id = Match(k.Games, args[1]).Game.Id;
    var stopFile = Path.Combine(k.Store.DataDir, "before-after.stop");
    File.Delete(stopFile);
    var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    (BeforeAfterStage, string?, bool)? shown = null;
    double printed = -30;
    var continuing = Task.CompletedTask;   // under done's lock: ended before the pair is
    using var stop = new CancellationTokenSource();
    using var endWait = new CancellationTokenSource();
    var ending = false;
    var status = new Say(s => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}"));
    k.GameChanged += s =>
    {
        if (s.Game.Id != id || s.BeforeAfter is not { } b) return;
        lock (done)
        {
            if (done.Task.IsCompleted) return;
            var changed = shown != (b.Stage, b.Problem, b.ClearPending);
            shown = (b.Stage, b.Problem, b.ClearPending);
            if (changed || b.PlayedSeconds - printed >= 30)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {b.Stage}{(b.PlayedSeconds > 0 ? $", {b.PlayedSeconds:0} s of {b.Minutes * 60} played" : "")}{(b.Problem is { } p ? ": " + p : "")}");
                printed = b.PlayedSeconds;
            }
            if (b.Stage == BeforeAfterStage.Done) done.TrySetResult(Report(k.BeforeAfterReport(id)));
            else if (!changed) return;
            // a clear that met the last run's cache still aside: the poll clears again once it is back
            else if (b.ClearPending && b.Problem == ScsKiller.ClearHeldNote) return;
            else if (b.Stage == BeforeAfterStage.AfterReady || b.ClearPending)   // the app's Continue
                continuing = Task.Run(() =>
                {
                    try { k.ContinueBeforeAfter(id); }
                    catch (Exception e) { Console.WriteLine("error: " + e.Message); done.TrySetResult(2); }
                });
            // the app shows a run to play again or a wait and goes on: only a note that needs the user ends it here
            else if (b.Problem is { } p && (p.StartsWith(ScsKiller.RestoreFailedNote[..ScsKiller.RestoreFailedNote.IndexOf('{')], StringComparison.Ordinal)
                     || b.Stage == BeforeAfterStage.Compiling && !ScsKiller.RetryNote(p)))
                done.TrySetResult(2);
        }
    };
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        if (Volatile.Read(ref ending)) endWait.Cancel();   // a second stop: the wait for the put-back ends, the journal stays
        done.TrySetResult(130);
    };
    if (OperatingSystem.IsLinux()) StopOnSignals(() => { if (Volatile.Read(ref ending)) endWait.Cancel(); done.TrySetResult(130); });
    k.StartBeforeAfter(id, minutes, !args.Contains("--no-overlay"), args.Contains("--max"));
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} BeforeArmed: start the game, play {minutes} min, close it (stop: Ctrl+C or {stopFile})");
    var watcher = k.WatchGames(stop.Token);
    while (!done.Task.IsCompleted && !File.Exists(stopFile)) await Task.WhenAny(done.Task, Task.Delay(1000));
    done.TrySetResult(130);
    var code = await done.Task;
    stop.Cancel();
    await watcher;
    Task last;
    lock (done) last = continuing;
    await last;
    if (code != 0)
    {
        // the app's End: the pair ends, and what was set aside for the before run goes back below
        k.StopQueue();
        k.EndBeforeAfter(id, compile: false);
        Console.WriteLine("before and after recording ended");
    }
    // never gone with a cache set aside: a game still running keeps it until it exits and nothing writes it
    Volatile.Write(ref ending, true);
    // a game played on after the pair ended keeps it aside: the wait ends then, the journal stays for the app's next start
    endWait.CancelAfter(TimeSpan.FromMinutes(30));
    try
    {
        if (await k.PutBackSetAsideAsync(status, endWait.Token) is { } armed && armed != id)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {armed}'s before run is armed: its cache stays set aside for it");
    }
    catch (OperationCanceledException)
    {
        foreach (var (game, folder, files) in k.SetAsideLeft())
        {
            Console.WriteLine($"still set aside for {game}: {folder}");
            foreach (var f in files) Console.WriteLine($"  {f}");
        }
        Console.WriteLine("SCSKiller puts it back at its next start, once nothing uses it");
        if (code == 0) code = 3;
    }
    await k.WhenQueueIdle();
    return code;

    static int Report(BeforeAfterResult? r)
    {
        if (r == null) return Fail("Done, but the report can't be read");
        foreach (var (name, run) in new[] { ("before", r.Before), ("after", r.After) })
        {
            var x = BeforeAfter.Stats(run);
            Console.WriteLine($"{name}: {x.PlaySeconds:0} s of play, {run.Frames} frames, {x.AvgFps:0.0} FPS average, {x.Low1PctFps:0.0} FPS 1% low, " +
                $"{x.ShaderStutters} shader compilation stutters ({x.FrozenSeconds:0.00} s, worst {x.WorstStutterMs:0} ms), {x.OtherHitches} other stutters, " +
                $"{x.Hitches} frames of 50 ms or more ({x.HitchSeconds:0.00} s, worst {x.WorstHitchMs:0} ms)");
        }
        return 0;
    }
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
    if (parts.Count == 0 && g.WarmedAt == null) return Fail($"no shader-cache files of {g.Game.Name} were found" + (gap != null ? $" ({gap})" : ""));
    foreach (var p in parts)
    {
        Console.WriteLine($"{p.Name}  {Format.Bytes(p.Bytes)}");
        foreach (var f in p.Files) Console.WriteLine($"  {f}");
    }
    if (gap != null) Console.WriteLine($"note: {gap}");
    if (!precache && k.GameCaches(g.Game.Id, true).Where(p => p.Name is ScsKiller.PipelinePart or ScsKiller.PrecachePart).ToList() is { Count: > 0 } own)
        Console.WriteLine($"kept: the game's own caches ({string.Join(", ", own.Select(p => $"{p.Name} {Format.Bytes(p.Bytes)}"))}), --game-precache deletes them too");
    if (!Confirm(parts.Count == 0 ? $"Forget {g.Game.Name}'s compile? No files are deleted, and it shows as not warmed afterwards."
            : $"Delete {g.Game.Name}'s shader caches ({string.Join(", ", parts.Select(p => $"{p.Name} {Format.Bytes(p.Bytes)}"))}, the files above)? " +
              "Its next run is cold, and it shows as not warmed afterwards.")) return 1;
    // a refusal throws InvalidOperationException: printed as "error: <reason>"
    if (!k.ClearGameCache(g.Game.Id, precache)) return Fail($"no shader-cache files of {g.Game.Name} were found");
    Console.WriteLine(parts.Count == 0 ? $"forgot {g.Game.Name}'s compile" : $"cleared {g.Game.Name}'s shader caches");
    return 0;
}

async Task<int> CachePage()
{
    var k = await Open();
    var inv = k.CacheInventory();
    if (inv.Folders.Count == 0 && inv.Groups.Count == 0) Console.WriteLine("SCSKiller doesn't know where this GPU's driver keeps its cache.");
    foreach (var f in inv.Folders)
        Console.WriteLine($"folder: {f.Label}  {f.Path}  {Format.Bytes(f.Bytes)}{(f.Exists ? "" : "  not created yet")}{(f.Final is { } at ? $"  stored in {at}" : "")}");
    Console.WriteLine($"total: {Format.Bytes(inv.Groups.Sum(g => g.Bytes))}");
    foreach (var g in inv.Groups)
    {
        Console.WriteLine($"{g.Kind}  {Format.Bytes(g.Bytes)}{(g.Note is { } gn ? $"  ({gn})" : "")}");
        foreach (var i in g.Items)
        {
            Console.WriteLine($"  {i.Name}  {Format.Bytes(i.Bytes)}  {i.Files.Count} files{(i.Kept ? "  kept" : "")}{(i.Note is { } n ? $"  ({n})" : "")}");
            if (args.Contains("--files")) foreach (var f in i.Files) Console.WriteLine($"    {f.Path}");
        }
    }
    if (args.ElementAtOrDefault(2) != "delete") return 0;
    if (args.ElementAtOrDefault(3) is not { } what) return Fail("cache page delete <item>|everything [--others]");
    var items = what == "everything" ? inv.Everything(args.Contains("--others"))
        : inv.Groups.SelectMany(g => g.Items).Where(i => i.Name.Equals(what, StringComparison.OrdinalIgnoreCase)).ToList();
    if (items.Count == 0) return Fail($"nothing to delete for '{what}'");
    if (!Confirm($"Delete {string.Join(", ", items.Select(i => i.Name))} ({Format.Bytes(items.Sum(i => i.Bytes))})?")) return 1;
    var r = k.DeleteCaches(items);
    Console.WriteLine($"deleted {Format.Bytes(r.Bytes)} in {r.Files} files");
    foreach (var why in r.Skipped) Console.WriteLine($"skipped: {why}");
    return 0;
}

async Task<int> Compile()
{
    if (args.Length < 2) return Fail("compile <game|--all-ready> [--threads N] [--idle | --when-idle] [--careful | --fast]");
    Only("--all-ready", "--threads", "--idle", "--when-idle", "--careful", "--fast");
    int? threads = Opt("--threads") is { } t ? int.TryParse(t, out var n) && n > 0 ? n : throw new ArgumentException("--threads takes a number of threads") : null;
    using var awake = KeepAwake.Start();   // before the first await: still on the main thread
    var k = await Open();
    if (threads != null) k.ThreadsOverride = threads;
    bool whenIdle = args.Contains("--when-idle");
    k.Background = args.Contains("--idle");
    if (k.Background || whenIdle) BackgroundPriority();
    var targets = args[1] == "--all-ready"
        ? k.Games.Where(s => s.Status is GameStatus.Ready or GameStatus.Stale && s.NoStutter == null && !s.CompileUnreached).ToList()
        : [Match(k.Games, args[1])];
    if (targets is [{ StatusReason: ScsKiller.CantReachReason or ScsKiller.ThirtyTwoBitReason } refused]) return Fail($"{refused.Game.Name}: {refused.StatusReason}");
    if (args.Contains("--careful") || args.Contains("--fast"))
        foreach (var g in targets)
        {
            k.SetCarefulCompile(g.Game.Id, args.Contains("--careful"));
            Console.WriteLine($"{g.Game.Name}: {(k.Games.Single(s => s.Game.Id == g.Game.Id) is var gs ? CarefulLine(gs.Careful, gs.Plan) : "")}");
        }
    return await RunQueue(k, targets, whenIdle);
}

async Task<int> RewarmStale(bool ifDriverChanged)
{
    using var awake = KeepAwake.Start();   // before the first await: still on the main thread
    var k = await Open();
    var stale = ifDriverChanged ? k.DriverStaleGames() : k.StaleGames();
    if (stale.Count == 0) { Console.WriteLine("nothing to re-warm"); return 0; }
    switch (k.Settings.OnDriverUpdate)
    {
        case DriverUpdateMode.Off:
            Console.WriteLine($"{stale.Count} game(s) need a re-warm. Automatic re-warm is off.");
            return 0;
        case DriverUpdateMode.Ask:
            if (!k.ShouldNotifyStale()) { Console.WriteLine($"{stale.Count} game(s) need a re-warm. The user skipped them for this driver."); return 0; }
            if (ScsKiller.StartDriverUpdated() is { } app)   // the app shows the notification (compile now / when idle / skip)
            {
                Console.WriteLine($"{stale.Count} game(s) need a re-warm: notified via {app}");
                return 0;
            }
            Console.WriteLine($"{stale.Count} game(s) need a re-warm (driver {k.Vendor.Gpu.DriverVersion}):");
            foreach (var s in stale) Row(s);
            Console.WriteLine("Run 'scskiller compile <game>' or open SCSKiller to re-warm them.");
            return 0;
        // the app compiles them at once (ScsKiller.RewarmRightAway); without one, this run waits for idle
        case DriverUpdateMode.WhenIdle when k.Settings.DriverRightAway && ifDriverChanged && ScsKiller.StartDriverUpdated() is { } handedTo:
            Console.WriteLine($"{stale.Count} game(s) need a re-warm: handed to {handedTo}, which compiles them right away");
            return 0;
        default:
            BackgroundPriority();
            return await RunQueue(k, ifDriverChanged ? k.IdleRewarmGames() : stale, whenIdle: true);
    }
}

int Cache()
{
    var v = RunningGpu();
    Console.WriteLine($"GPU: {v.Gpu.Name} ({v.Vendor}), driver {v.Gpu.DriverVersion}, profile {v.Caps.Profile}");
    if (ScsKiller.AmdAlwaysOnLine(v) is { } alwaysOn) Console.WriteLine($"  warning: {alwaysOn}");
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

async Task<int> UseShared()
{
    if (args.Length != 3 || args[2] is not ("on" or "off" or "follow")) return Fail("shared <game> on|off|follow");
    var k = await Open();
    var g = Match(k.Games, args[1]);
    k.SetGameUseShared(g.Game.Id, args[2] switch { "on" => true, "off" => false, _ => null });
    Console.WriteLine($"{g.Game.Name}: use shared shader hashes {(args[2] == "follow" ? "as Settings says" : args[2])}");
    return 0;
}

async Task<int> Record()
{
    if (args.ElementAtOrDefault(1) == "remove-all") return RemoveAll();
    bool alongside = args.Length > 3 && args[1] == "alongside" && args[3] is "on" or "off";
    if (!alongside && (args.Length < 3 || args[1] is not ("install" or "uninstall" or "clear")))
        return Fail("record install|uninstall|clear <game> | record alongside <game> on|off | record remove-all");
    var k = await Open();
    var g = Match(k.Games, args[2]);
    if (args[1] == "clear")
    {
        if (g.RecordingBytes == 0) return Fail($"{g.Game.Name} has no recording");
        if (!Confirm($"Delete {g.Game.Name}'s recording ({Format.Bytes(g.RecordingBytes)})? The recorded pipelines are removed. They're compiled again only if " +
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
        Console.WriteLine($"record alongside {mod ?? "a mod"}: {args[3]}, recorder {(s.RecorderInstalled ? "installed" : "not installed")}"
            + (RecorderWhy(s) is { } why ? $" ({why})" : ""));
        return 0;
    }
    if (args[1] == "install") k.InstallRecorder(g.Game.Id); else k.UninstallRecorder(g.Game.Id);
    Console.WriteLine($"recorder {args[1]}ed: {Path.GetDirectoryName(g.Game.ExePath)}");
    return 0;
}

// no scan: the records name the folders, as for the uninstall
static int RemoveAll()
{
    var removed = ScsKiller.RemoveRecordersFromAllGames(new AppStore(AppStore.DefaultDir));
    foreach (var g in removed.Games)
        Console.WriteLine(g.Left == null ? $"removed  {g.GameId}  {g.Folder}" : $"left     {g.GameId}  {g.Folder}: {g.Left}");
    foreach (var layer in removed.VulkanLayers) Console.WriteLine($"Vulkan recorder unregistered: {layer}");
    if (removed.VulkanLeft != null) Console.WriteLine($"left     Vulkan recorders' registrations: {removed.VulkanLeft}");
    var left = removed.Games.Count(g => g.Left != null);
    string[] where = [.. left > 0 ? [$"in {left} game{(left == 1 ? "" : "s")}"] : Array.Empty<string>(),
        .. removed.VulkanLeft != null ? ["in the Vulkan registrations"] : Array.Empty<string>()];
    return where.Length == 0 ? 0 : Fail($"the recorder is left {string.Join(" and ", where)}, see above");
}

async Task<int> AddGame()
{
    if (args.Length < 2) return Fail("add <exe> [<install folder>]");
    var k = await Open();
    var (game, existed) = k.AddManualGame(args[1], args.ElementAtOrDefault(2));
    await k.ScanAsync(CancellationToken.None);
    Console.WriteLine($"{(existed ? "already listed" : "added")}: {game.Id}  {game.Name}  {game.ExePath}");
    return 0;
}

async Task<int> GameExe()
{
    if (args.Length < 3) return Fail("exe <game> <exe>|auto");
    var k = await Open();
    var id = Match(k.Games, args[1]).Game.Id;
    k.SetGameExe(id, args[2] == "auto" ? null : args[2]);
    Console.WriteLine($"{id}  {Match(k.Games, id).Game.ExePath}");
    return 0;
}

async Task<int> ProtonEnv()
{
    if (args.Length < 2) return Fail("proton-env <game> [--warm]");
    if (OperatingSystem.IsLinux())
    {
        var native = await Open();
        var s = Match(native.Games, args[1]);
        foreach (var (name, value) in native.CacheEnvironment(s.Game, args.Contains("--warm"))) Console.WriteLine($"{name}={value}");
        return 0;
    }
    if (!Wine.Running) return Fail("proton-env is for games under Wine (Proton)");
    var k = await Open();
    var g = Match(k.Games, args[1]);
    var root = Wine.CacheRoot(k.Store.GameDir(g.Game.Id));
    if (Wine.UnixPath(root) is not { } unix) return Fail($"no Linux path for {root}");
    var (warm, game) = Wine.CacheEnvironment(unix, Path.GetFileName(g.Game.ExePath));
    foreach (var (name, value) in args.Contains("--warm") ? warm : game) Console.WriteLine($"{name}={value}");
    return 0;
}

async Task<int> SteamPlace()
{
    if (args.Length < 2) return Fail("steam-place <game> [--remove]");
    var k = await Open();
    var g = Match(k.Games, args[1]);
    var (done, status, details) = k.SteamPlaceNow(g.Game.Id, args.Contains("--remove"));
    Console.WriteLine($"{g.Game.Name}: {status}");
    foreach (var line in details) Console.WriteLine("  " + line);
    return done ? 0 : 1;
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
    using (SCSKiller.Core.Games.ContentStamps.Cached(k.Store.GameDir(g.Game.Id))) return (k, g, reader, engine, reader.Index(g.Game, engine, log, CancellationToken.None));
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
    if (ScsKiller.DetectGpu() is not NvidiaBackend nv) return Fail("no NVIDIA GPU");
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
    if (RunningGpu() is not NvidiaBackend nv) return Fail("no NVIDIA GPU");
    if (!Elevated.IsAdmin) return Fail("switching NVIDIA Auto Shader Compilation needs an elevated (administrator) prompt");
    // NvOSC.exe -register registers the idle task for the account running it: refuse when UAC elevated a different
    // administrator (over-the-shoulder), whose DXCache it would compile into. Same account + elevated token is fine.
    if (Opt(Elevated.ForUserArg) is { } sid && sid != Elevated.CurrentUserSid)
        return Fail($"Windows elevated as a different account ({WindowsIdentity.GetCurrent().Name}), so the NVIDIA task would compile for that account, " +
                    "not for you. Sign in to Windows with an administrator account, or ask an administrator to add yours to Administrators.");
    if (!args.Contains(Elevated.YesArg) && !Confirm($"Set NVIDIA Auto Shader Compilation to {level}? This changes a global driver setting and the driver's idle task."))
        return 1;
    Outcome.Message = nv.SetAutoShaderCompilation(level);
    Console.WriteLine($"{Outcome.Message} now: {nv.GetAutoShaderCompilation()?.ToString() ?? "not readable"}");
    return 0;
}

int ShaderCleanup()
{
    if (args.ElementAtOrDefault(1) is not ("keep" or "allow") || !OperatingSystem.IsWindows()) return Fail("shader-cleanup keep|allow (Windows)");
    if (!Elevated.IsAdmin) return Fail("changing Windows' disk cleanup needs an elevated (administrator) prompt");
    Outcome.Message = D3DSCache.KeepFromCleanup(args[1] == "keep");
    Console.WriteLine(Outcome.Message);
    return 0;
}

int TaskCommand()
{
    if (!OperatingSystem.IsWindows()) return Fail("the re-warm task is a Windows scheduled task");
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

static int FetchCodecs(string dir)
{
    // Packaging (publish.ps1): the pinned DLLs in the build's cache, copied next to the exes, where they seed each user's
    // codecs folder (Codecs)
    try
    {
        Console.WriteLine(Codecs.Ensure(OodleHelper.OodleFileName, Codecs.DownloadOodle, dir));
        Console.WriteLine(Codecs.Ensure(ZlibHelper.DllName, Codecs.DownloadZlib, dir));
        return 0;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return Fail(e.Message); }
}

/// <summary>What the command's last word was (its success line or the error), for <see cref="Elevated.ResultArg"/>.</summary>
static class Outcome { public static string? Message; }

/// <summary>The SIGTERM and SIGHUP handlers, kept for the process's life.</summary>
static class SignalHandlers { public static readonly List<PosixSignalRegistration> All = []; }

/// <summary>A writer whose writes fail silently: the output's reader (a terminal, a pipe) may be gone.</summary>
sealed class NoThrowWriter(TextWriter inner) : TextWriter
{
    public override System.Text.Encoding Encoding => inner.Encoding;

    public override void Write(char value) => Try(() => inner.Write(value));

    public override void Write(string? value) => Try(() => inner.Write(value));

    public override void WriteLine(string? value) => Try(() => inner.WriteLine(value));

    public override void Flush() => Try(inner.Flush);

    static void Try(Action write)
    {
        try { write(); }
        catch (IOException) { }
    }
}

// Not Progress<T>: it posts to the thread pool, so its last messages could be lost when the process exits.
sealed class StderrLog : IProgress<string>
{
    public void Report(string value) => Console.Error.WriteLine("  " + value);
}

sealed class Say(Action<string> say) : IProgress<string>
{
    public void Report(string value) => say(value);
}

/// <summary>The PC doesn't idle-sleep while a compile runs. Best effort: a failure is ignored.</summary>
sealed class KeepAwake : IDisposable
{
    const uint EsContinuous = 0x80000000, EsSystemRequired = 1;
    Process? _inhibit;

    public static KeepAwake Start()
    {
        var awake = new KeepAwake();
        if (OperatingSystem.IsWindows()) SetThreadExecutionState(EsContinuous | EsSystemRequired);
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                string[] inhibit = ["systemd-inhibit", "--what=idle", "--mode=block", "--who=SCSKiller", "--why=Compiling shaders", "cat"];
                // own session: a Ctrl+C in the terminal reaches the CLI only, and the lock stays while it drains
                try { awake._inhibit = Start("setsid", inhibit); }
                catch (Exception) { awake._inhibit = Start(inhibit[0], inhibit[1..]); }
            }
            catch (Exception) { }   // no systemd-inhibit
        }
        return awake;
    }

    static Process? Start(string file, string[] args) => Process.Start(new ProcessStartInfo(file, args)
    {
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    });

    public void Dispose()
    {
        // Windows: the request belongs to the main thread and this runs after an await, so process exit drops it
        if (_inhibit is not { } p) return;
        try
        {
            p.StandardInput.Close();   // cat ends, the lock goes
            if (!p.WaitForExit(2000)) p.Kill();
        }
        catch (Exception) { }
        p.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
}
