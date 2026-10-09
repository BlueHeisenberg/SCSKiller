using System.Diagnostics;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Xml.Linq;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Tests.Platform;

// Everything here runs on a fake game under %TEMP%; no real game folder is touched.
[Collection(TimingCollection.Name)]
public partial class AppTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "scskiller-app-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Game _game;
    readonly DateTime _started = DateTime.UtcNow;
    readonly string _exeDir, _proxy;
    static readonly GpuInfo Gpu = new(GpuVendor.Unknown, "Fake GPU", "100.01", 1, 0);
    static readonly EngineInfo Unreal = new("Unreal", "4.26", null, "D3D12", false, null);

    public AppTests()
    {
        var install = Path.Combine(_root, "FakeGame");
        _exeDir = Path.Combine(install, "Fake", "Binaries", "Win64");
        Directory.CreateDirectory(_exeDir);
        File.WriteAllBytes(Path.Combine(_exeDir, "Fake-Win64-Shipping.exe"), new byte[4096]);
        Directory.CreateDirectory(Path.Combine(install, "Engine", "Binaries", "Win64"));
        File.WriteAllBytes(Path.Combine(install, "Engine", "Binaries", "Win64", "CrashReportClient.exe"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(install, "Fake.exe"), new byte[100]);   // launcher stub
        _game = new Game("test:fake-" + Guid.NewGuid().ToString("N")[..8], "Fake Game", Store.Other, install, Core.Games.GameFiles.FindExe(install)!);
        _proxy = Path.Combine(_root, "tools", "d3d12.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(_proxy)!);
        File.WriteAllBytes(_proxy, [.. "MZ fake proxy SCSKiller_StartWarm "u8, .. Guid.NewGuid().ToByteArray()]);
        ScsKiller.LedgerDir = Path.Combine(_root, "LocalAppData", "SCSKiller", "armed");   // this test's own, unless UseProxyLedger
    }

    /// <summary>For a test that runs the built proxy: the app's ledger is the one the proxy reads, the run's
    /// <see cref="TestEnv.Ledger"/>. Only this test's entries are written there, and <see cref="Dispose"/> takes them out.</summary>
    static void UseProxyLedger() => ScsKiller.LedgerDir = TestEnv.Ledger;

    readonly List<ScsKiller> _killers = [];   // every one Killer made: their queues end before the folder goes

    public void Dispose()
    {
        // a queue still compiling holds games\<id>\compile.lock: stopped and awaited, or the folder can't be deleted
        foreach (var k in _killers)
        {
            k.StopQueue();
            k.WhenQueueIdle().Wait(HangGuard);
            k.RecorderIndexing.Wait(HangGuard);   // it holds compile.lock too, and waits for the queue
        }
        // the run's proxy ledger: only this test's entries go, by the exes it made, unread
        ScsKiller.LedgerDir = TestEnv.Ledger;
        var keys = Directory.EnumerateFiles(_root, "*.exe", SearchOption.AllDirectories).SelectMany(ScsKiller.LedgerFiles).ToList();
        foreach (var f in keys.SelectMany(l => new[] { l, l + ".revoked", l + ".refused" }))
            try { File.Delete(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Directory.Delete(_root, true);
        TestD3DSCache.Clean(_root, _started);   // selftest's and the staged exes' D3DSCache folders
        TestEnv.AssertLiveLedgerUntouched(keys.Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    ScsKiller Killer(IEngineReader? reader = null, IPlanner? planner = null, IWarmer? warmer = null, string driver = "100.01", Game? game = null,
        Game[]? games = null, IGpuVendorBackend? vendor = null, IGameSource? source = null, IGameSource[]? sources = null)
    {
        var v = vendor ?? new FakeVendor(Gpu with { DriverVersion = driver });
        var k = new ScsKiller(sources ?? [source ?? new FakeSource(games ?? [game ?? _game])], v, reader ?? new FakeReader(null),
            planner ?? new FakePlanner(), warmer ?? new FakeWarmer(), Path.Combine(_root, "data"), _proxy)
        {
            LocalAppData = _root, MyGames = Path.Combine(_root, "My Games"), ProgramData = Path.Combine(_root, "ProgramData"),   // never the real D3DSCache or Saved folders
            Adapters = () => [Listed(v.Gpu)],   // DXGI lists the vendor's adapter, its version as the user-mode one
            Processes = Ours, ProcessNames = OurNames,
        };
        k.OtherCompile = () => false;   // another test's queue isn't another process
        lock (_killers) _killers.Add(k);
        return k;
    }

    /// <summary>The PC's processes this test run started: another run on the PC (CI, a teammate's) runs the same fake game
    /// exe names and selftest.exe, which would count as this test's game running.</summary>
    static List<(int Pid, int Parent, string Exe)> Ours(bool fresh)
    {
        var ours = Core.Warming.ProcessTree.WithDescendants(Environment.ProcessId).ToHashSet();
        return (fresh ? Core.Warming.ProcessTree.Snapshot() : Core.Warming.ProcessTree.RecentSnapshot()).Where(p => ours.Contains(p.Pid)).ToList();
    }

    static bool OurOthersRun(OfflineSession s) => ScsKiller.OthersRun(s, Ours(true));

    static IReadOnlySet<string> OurNames() => Ours(true).Select(p => Path.GetFileNameWithoutExtension(p.Exe)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fixture's game with its exe named <paramref name="exe"/>: its Shipping exe goes, which discovery would
    /// take instead (<see cref="GameFiles.GameExe"/>).</summary>
    Game NamedAs(string exe)
    {
        File.Delete(_game.ExePath);
        return _game with { ExePath = Path.Combine(_exeDir, exe) };
    }

    async Task<ScsKiller> Warmed(Game? game = null)
    {
        var k = Killer(new FakeReader(Unreal), game: game);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(HangGuard);   // the queue's end: a busy PC only makes it later
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        return k;
    }

    static async Task Until(Func<bool> cond, double seconds = 10)
    {
        for (var t = Stopwatch.StartNew(); !cond(); await Task.Delay(20))
            if (t.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException();
    }

    [Fact]
    public void Exe_heuristic_skips_engine_helpers_and_launcher_stubs() =>
        Assert.Equal(Path.Combine(_exeDir, "Fake-Win64-Shipping.exe"), _game.ExePath);

    [Fact]
    public void Elevated_helper_quotes_finds_the_cli_and_reads_its_result()
    {
        Assert.Equal(@"cache set 100 --result ""C:\Users\A B\t.json""", Elevated.CommandLine(["cache", "set", "100", "--result", @"C:\Users\A B\t.json"]));

        var published = Path.Combine(_root, "dist");   // dist\SCSKiller.exe + dist\cli\scskiller.exe
        Directory.CreateDirectory(Path.Combine(published, "cli"));
        File.WriteAllBytes(Path.Combine(published, "cli", "scskiller.exe"), [0]);
        Assert.Equal(Path.Combine(published, "cli", "scskiller.exe"), Elevated.CliExe(published));

        var file = Path.Combine(_root, "result.json");
        Assert.Equal(new Elevated.Result(false, "the command-line tool failed (exit code 3)"), Elevated.ReadResult(file, 3));   // no file
        Assert.True(Elevated.ReadResult(file, 0).Ok);
        Elevated.WriteResult(file, false, "needs admin");
        Assert.Equal(new Elevated.Result(false, "needs admin"), Elevated.ReadResult(file, 1));
    }

    [Fact]
    public void Elevated_cli_writes_its_outcome_and_refuses_another_account()
    {
        // The dev tree's CLI build, as the app finds it. Both commands fail before touching anything: not elevated here,
        // or (if the tests run elevated) the --for-user SID is not this account's.
        if (Elevated.CliExe() is not { } cli) return;
        foreach (var args in new[] { new[] { "nvidia-auto-shader", "bogus" }, ["nvidia-auto-shader", "medium", Elevated.ForUserArg, "S-1-5-21-1-2-3-500"] })
        {
            var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
            var psi = new ProcessStartInfo(cli, [.. args, Elevated.YesArg, Elevated.ResultArg, file]) { RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            var r = Elevated.ReadResult(file, p.ExitCode);
            Assert.False(r.Ok);
            Assert.True(File.Exists(file), "the CLI wrote no --result file");
            Assert.NotEmpty(r.Message);
        }
    }

    [Fact]
    public async Task Anti_cheat_appearing_while_the_recorder_installs_takes_it_out_again()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");   // a mod the recorder chains to: put back as it was
        var bytes = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(mod, bytes);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallStep = step =>   // a game update, between the check and the copy
        {
            if (step == "copy") Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "EasyAntiCheat"));
        };
        k.SetRecordAlongsideMod(_game.Id, true);

        Assert.Contains(ScsKiller.SkipAntiCheat, Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id)).Message);
        Assert.Equal(bytes, File.ReadAllBytes(mod));
        Assert.Equal(["d3d12.dll", "Fake-Win64-Shipping.exe"], Directory.GetFiles(_exeDir).Select(Path.GetFileName).Order());
        var s = k.Games.Single();
        Assert.Equal((AntiCheat.EasyAntiCheat, false), (s.AntiCheat, s.RecorderInstalled));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderChained);
    }

    /// <summary>The recorder goes into the game's folder under a temp name, then a rename: an update whose write fails (here
    /// a folder holds the temp name) leaves the installed d3d12.dll whole, and the next one replaces it.</summary>
    [Fact]
    public async Task An_update_whose_write_fails_leaves_the_installed_recorder_whole()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var installed = File.ReadAllBytes(dll);
        File.WriteAllBytes(_proxy, [.. "MZ fake proxy SCSKiller_StartWarm "u8, .. Guid.NewGuid().ToByteArray()]);   // the app was updated
        Directory.CreateDirectory(dll + ".scskiller-new");
        var updated = Killer(new FakeReader(Unreal));
        updated.ProcessNames = () => new HashSet<string>();
        await updated.ScanAsync(default);
        Assert.Throws<InvalidOperationException>(() => updated.InstallRecorder(_game.Id));
        Assert.Equal(installed, File.ReadAllBytes(dll));

        Directory.Delete(dll + ".scskiller-new");
        updated.ReconcileRecorders(_game.Id);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(dll));
        Assert.Empty(Directory.GetFileSystemEntries(_exeDir, "*.scskiller-new"));
    }

    [Fact]
    public async Task A_failing_install_still_runs_the_full_check_and_takes_out_what_it_placed()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        try
        {
            // the ini write fails (read-only) after an update put anti-cheat where the quick recheck doesn't look
            k.InstallStep = step =>
            {
                if (step == "copy") Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "EasyAntiCheat"));
                if (step == "copied")
                {
                    File.WriteAllText(ini, "");
                    File.SetAttributes(ini, FileAttributes.ReadOnly);
                }
            };
            Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
            Assert.False(File.Exists(dll));
            Assert.Equal(AntiCheat.EasyAntiCheat, k.Store.LoadScan()[_game.Id].AntiCheat);
            Assert.Empty(k.Store.LoadGame(_game.Id).RecorderFiles);

            // the same failure without anti-cheat: the proxy goes too, nothing half installed
            Directory.Delete(Path.Combine(_game.InstallDir, "Fake", "Content", "EasyAntiCheat"));
            File.SetAttributes(ini, FileAttributes.Normal);
            File.Delete(ini);
            k.InstallStep = step =>
            {
                if (step == "copied")
                {
                    File.WriteAllText(ini, "");
                    File.SetAttributes(ini, FileAttributes.ReadOnly);
                }
            };
            await k.RescanAsync(default);
            Assert.Equal(AntiCheat.None, k.Games.Single().AntiCheat);
            Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
            Assert.False(File.Exists(dll));
            Assert.Empty(k.Store.LoadGame(_game.Id).RecorderFiles);
        }
        finally { if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Normal); }
    }

    [Fact]
    public async Task Anti_cheat_found_after_the_copy_takes_the_proxy_out_even_when_the_scan_cache_cant_be_written()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var scan = Path.Combine(_root, "data", "scan.json");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallStep = step =>
        {
            if (step != "copied") return;
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
            File.SetAttributes(scan, FileAttributes.ReadOnly);
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
            Assert.False(File.Exists(dll));
            Assert.Empty(k.Store.LoadGame(_game.Id).RecorderFiles);
            Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);   // in memory, though scan.json kept the old verdict
        }
        finally { File.SetAttributes(scan, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pending_takeout_runs_before_any_save_even_when_the_record_is_read_only(bool marker)
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.SetRecorderOverride(_game.Id, RecorderOverride.Off);
        var state = Path.Combine(k.Store.GameDir(_game.Id), "state.json");
        FileStream? held = null;
        k.InstallStep = step =>
        {
            if (step != "copied") return;
            if (marker) Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
            held = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read);   // the game started: no delete
            File.SetAttributes(state, FileAttributes.ReadOnly);
            if (!marker) throw new IOException("the disk is full");
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
            Assert.True(ScsKiller.IsOurProxy(dll));
            held!.Dispose();   // the game exited; the record is still read-only
            k.InstallStep = null;
            k.ReconcileRecorders(_game.Id);
            Assert.False(File.Exists(dll));
            if (marker) Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        }
        finally
        {
            held?.Dispose();
            File.SetAttributes(state, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task The_full_check_runs_before_the_install_waits_for_the_recording_lock()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallStep = step =>   // deeper than the quick recheck after the copy reads
        {
            if (step == "copy") Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "security", "EasyAntiCheat"));
        };
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = Task.Factory.StartNew(() =>   // another process holds the game's recording lock
        {
            using (Recordings.Lock(Path.Combine(k.Store.GameDir(_game.Id), "recording.db"))) { held.Set(); release.Wait(); }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        try
        {
            var clock = Stopwatch.StartNew();
            Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
            Assert.InRange(clock.Elapsed.TotalSeconds, 0, 10);
            Assert.False(File.Exists(dll));
        }
        finally { release.Set(); }
        await other;
    }

    [Fact]
    public async Task Anti_cheat_added_while_the_install_waits_for_the_recording_lock_takes_the_proxy_out()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = Task.Factory.StartNew(() =>   // another process holds the game's recording lock
        {
            using (Recordings.Lock(Path.Combine(k.Store.GameDir(_game.Id), "recording.db"))) { held.Set(); release.Wait(); }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        k.InstallStep = step =>   // both full checks passed; the install is about to wait in WriteKeys
        {
            if (step != "keys") return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "security", "EasyAntiCheat"));   // an update meanwhile
                release.Set();
            });
        };
        Assert.Contains("EasyAntiCheat", Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id)).Message);
        await other;
        Assert.False(File.Exists(dll));
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
    }

    [Fact]
    public async Task Anti_cheat_an_update_puts_next_to_the_exe_is_seen_at_the_next_refresh_and_the_recorder_goes()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Assert.True(ScsKiller.IsOurProxy(dll));
        File.WriteAllBytes(Path.Combine(_exeDir, "BEService_x64.exe"), [0]);   // the exe and the build unchanged: the cache key too
        k.RefreshGame(_game.Id);   // the detail page's refresh, no reconcile after it
        Assert.Equal(AntiCheat.BattlEye, k.Games.Single().AntiCheat);
        Assert.False(File.Exists(dll));
    }

    /// <summary>A cached-clean game with the recorder in: anti-cheat an update puts deep in the install (the exe, the build and
    /// the detection evidence unchanged, a file beside the exe new) is seen by a plain scan, and the recorder goes.</summary>
    [Fact]
    public async Task Anti_cheat_deep_in_the_install_of_a_recorder_game_is_seen_at_the_next_scan_and_the_recorder_goes()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var reader = new FakeReader(Unreal);
        var k = Killer(reader);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Assert.True(ScsKiller.IsOurProxy(dll));
        await k.ScanAsync(default);
        var detects = reader.Detects;
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0x4D, 0x5A]);   // the update's files beside the exe
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(detects, reader.Detects);   // the cache hit
        Assert.Equal((AntiCheat.EasyAntiCheat, false), (s.AntiCheat, s.RecorderInstalled));
        Assert.False(File.Exists(dll));
        Assert.Equal(ScsKiller.SkipAntiCheat, ScsKiller.RecorderSkip(s, null));
    }

    /// <summary>Our proxy on disk with no record of it and an engine that doesn't allow the recorder: a plain scan, with
    /// recorders not managed, still checks it fully once its folders changed, and anti-cheat deep in the install takes the
    /// proxy out.</summary>
    [Fact]
    public async Task A_proxy_on_disk_without_a_record_gets_the_full_anti_cheat_check()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var reader = new FakeReader(Unreal with { GraphicsApi = "D3D11" });
        var k = Killer(reader);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        File.Copy(_proxy, dll);
        Assert.Empty(k.Store.LoadGame(_game.Id).RecorderFiles);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0x4D, 0x5A]);   // the update's files beside the exe
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(1, reader.Detects);   // the cache hit
        Assert.Equal(AntiCheat.EasyAntiCheat, s.AntiCheat);
        Assert.False(File.Exists(dll));
    }

    /// <summary>Reconciles that keep the recorder as it is (plain, or chained to a mod) check anti-cheat fully once the
    /// install's folders changed: one that appeared deep in the install since the scan takes it out, and the next reconciles
    /// keep it out.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reconcile_that_keeps_the_recorder_checks_anti_cheat_fully(bool chained)
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        if (chained) File.WriteAllBytes(dll, mod);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        if (chained) k.SetRecordAlongsideMod(_game.Id, true);
        else k.InstallRecorder(_game.Id);
        Assert.True(ScsKiller.IsOurProxy(dll));
        for (var i = 0; i < 3; i++) k.ReconcileRecorders();   // nothing to change: kept
        Assert.True(ScsKiller.IsOurProxy(dll));

        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0x4D, 0x5A]);   // the update's files beside the exe
        for (var i = 0; i < 3; i++)
        {
            k.ReconcileRecorders();
            Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
            Assert.False(k.Games.Single().RecorderInstalled);
            if (chained) Assert.Equal(mod, File.ReadAllBytes(dll));   // the mod is back
            else Assert.False(File.Exists(dll));
        }
    }

    /// <summary>The app waits in the notification area (no scan, no reconcile) while an update puts anti-cheat deep in a
    /// recorder game's install: the watcher's check sees it, at the next poll after the change or at the periodic check
    /// (the update's files beside the exe changed its folders).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_watcher_takes_the_recorder_out_of_a_game_an_update_gave_anti_cheat(bool periodic)
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await k.CheckRecorderGames(false);   // the first look: clean, and its install watched
        Assert.True(ScsKiller.IsOurProxy(dll));
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0x4D, 0x5A]);   // the update's files beside the exe
        if (periodic) await k.CheckRecorderGames(true);
        else
            for (var wait = Stopwatch.StartNew(); k.Games.Single().AntiCheat == AntiCheat.None && wait.Elapsed < TimeSpan.FromSeconds(10); await Task.Delay(50))
                await k.CheckRecorderGames(false);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.False(File.Exists(dll));
    }

    /// <summary>A removal anti-cheat asked for fails (the dll held open): the watcher keeps trying until the dll is gone.</summary>
    [Fact]
    public async Task The_watcher_retries_a_failed_removal_until_the_recorder_is_gone()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.RemovalRetryInterval = TimeSpan.Zero;
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0x4D, 0x5A]);   // the update's files beside the exe
        using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.None))   // another process holds it
        {
            await k.CheckRecorderGames(true);
            Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
            Assert.True(File.Exists(dll));
            await k.CheckRecorderGames(false);
            Assert.True(File.Exists(dll));
        }
        await k.CheckRecorderGames(false);
        Assert.False(File.Exists(dll));
    }

    /// <summary>A check that doesn't finish in time (a blocked network folder) is anti-cheat "Other": the recorder goes.</summary>
    [Fact]
    public async Task The_watcher_takes_a_check_that_runs_out_of_time_as_anti_cheat()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        using var release = new ManualResetEventSlim();
        k.FullAntiCheatCheck = _ => { release.Wait(TimeSpan.FromSeconds(10)); return AntiCheat.None; };
        k.RecorderCheckTimeout = TimeSpan.FromMilliseconds(100);
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0x4D, 0x5A]);   // the update's files beside the exe
        await k.CheckRecorderGames(true);
        release.Set();
        Assert.Equal(AntiCheat.Other, k.Games.Single().AntiCheat);
        Assert.False(File.Exists(dll));
    }

    /// <summary>A watcher that lost events (its Error) is made again, and the next change is still seen.</summary>
    [Fact]
    public async Task The_watcher_makes_a_failed_install_watcher_again()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await k.CheckRecorderGames(false);
        var first = k._installWatchers.Values.Single().Watchers![0];
        typeof(FileSystemWatcher).GetMethod("OnError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(first, [new ErrorEventArgs(new InternalBufferOverflowException())]);
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ArmedFile)));   // its Error disarmed it
        await Until(() => !k.DisarmQueued(_game));   // the event's disarm done in the background
        await k.CheckRecorderGames(false);
        var second = k._installWatchers.Values.Single();
        Assert.NotSame(first, second.Watchers![0]);
        Assert.Equal(2, second.Tries);
        Assert.True(File.Exists(Path.Combine(_exeDir, ScsKiller.ArmedFile)));   // checked clean under the new watcher: armed again
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        for (var wait = Stopwatch.StartNew(); k.Games.Single().AntiCheat == AntiCheat.None && wait.Elapsed < TimeSpan.FromSeconds(10); await Task.Delay(50))
            await k.CheckRecorderGames(false);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.False(File.Exists(dll));
    }

    /// <summary>The watcher finds anti-cheat while a rescan's clean evaluation, started before, waits to store its verdict:
    /// the finding stays, in memory and in scan.json, and the recorder stays out.</summary>
    [Fact]
    public async Task A_watcher_finding_outlives_an_older_clean_evaluation()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var pauses = 0;
        k.EvaluateStep = step => { if (step == "checked" && Interlocked.Increment(ref pauses) == 1) { entered.Set(); gate.Wait(TimeSpan.FromSeconds(10)); } };
        var older = k.RescanAsync(default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var eac = Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat")).FullName;
        await Until(() => Revoked(Path.Combine(_exeDir, ScsKiller.ArmedFile)));   // the install watcher's event
        await k.CheckRecorderGames(true);
        Assert.False(File.Exists(dll));
        Directory.Delete(eac);   // stands for a marker the next quick check doesn't see
        gate.Set();
        await older;
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.Equal(AntiCheat.EasyAntiCheat, (await Killer(new FakeReader(Unreal)).ScanAsync(default)).Single().AntiCheat);   // scan.json
        Assert.False(File.Exists(dll));
    }

    /// <summary>The built proxy loaded with no device yet writes nothing, its log included: whether it records is decided at
    /// the first device.</summary>
    [Fact]
    public void The_proxy_writes_nothing_before_the_first_device()
    {
        var built = Path.Combine(Planning.Ff7.ProxyBin, "d3d12.dll");
        if (!File.Exists(built)) return;   // the native build isn't here
        var exeDir = Planning.Ff7.TempDir($"proxy-{Guid.NewGuid():N}");   // the loaded dll stays until the process ends
        File.Copy(built, Path.Combine(exeDir, "d3d12.dll"));
        System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(exeDir, "d3d12.dll"));
        Assert.Equal(["d3d12.dll"], Directory.GetFiles(exeDir).Select(Path.GetFileName));
    }

    /// <summary>No working install watcher for the game (none could be made): Install doesn't arm it, so it is a pass-through.
    /// Once one runs, the next clean full check arms it.</summary>
    [Fact]
    public async Task The_recorder_is_armed_only_while_its_install_is_watched()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var make = k.MakeWatcher;
        k.MakeWatcher = _ => throw new IOException("no watcher");
        k.InstallRecorder(_game.Id);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(armed));
        k.MakeWatcher = make;
        k.ReconcileRecorders();   // kept: a full check, a watcher now, armed
        Assert.Contains("armed=1", File.ReadAllText(armed));
    }

    /// <summary>A game whose exe is outside its install folder: a change in the exe's folder disarms it too.</summary>
    [Fact]
    public async Task A_change_next_to_an_exe_outside_the_install_disarms_the_recorder()
    {
        var game = _game with { InstallDir = Directory.CreateDirectory(Path.Combine(_root, "Elsewhere")).FullName };
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal), game: game);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(game.Id);
        Assert.True(File.Exists(armed));
        Assert.Equal(2, k._installWatchers.Values.Single().Watchers!.Count);   // the install and the exe's folder
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0]);
        await Until(() => Revoked(armed));
    }

    /// <summary>An Unreal crash reporter's debugger fills a sym folder in the install with copies of system dlls and its
    /// download errors: that never disarms the recorder; a dll anywhere else still does.</summary>
    [Fact]
    public async Task A_crash_reporters_symbol_store_does_not_disarm_the_recorder()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var win64 = Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Engine", "Binaries", "Win64")).FullName;
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(armed));
        var sym = Directory.CreateDirectory(Path.Combine(win64, "sym", "kernel32.dll", "3ECCCF12cb000")).FullName;
        File.WriteAllText(Path.Combine(sym, "download.error"), "");
        File.WriteAllBytes(Path.Combine(sym, "kernel32.dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(win64, "sym", "pingme.txt"), "");
        var patch = Path.Combine(_game.InstallDir, "Engine", "patch.dll");
        File.WriteAllBytes(patch, [0x4D, 0x5A]);   // after them: the watcher's events come in order
        var log = Path.Combine(k.Store.DataDir, "recorders.log");
        await Until(() => SharedLog(log).Contains("disarmed"), 30);
        Assert.Contains($"disarmed until the install is checked again: {patch} created", SharedLog(log));
        Assert.DoesNotContain(@"\sym\", SharedLog(log));
    }

    /// <summary>One game's exe has a file time NTFS holds but .NET can't read (past the year 9999): that game is listed as
    /// unsupported with the reason, the other as usual, at every scan.</summary>
    [Fact]
    public async Task A_game_whose_files_cant_be_read_is_listed_with_why_and_the_others_still_scan()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Broken")).FullName;
        var exe = Path.Combine(dir, "Broken.exe");
        File.WriteAllBytes(exe, new byte[100]);
        SetWriteTime(exe, Unreadable);
        var broken = new Game("test:broken-" + Guid.NewGuid().ToString("N")[..8], "Broken Game", Store.Other, dir, exe);
        var k = Killer(new FakeReader(Unreal), games: [broken, _game]);
        k.ManageRecorders = k.CheckPlans = true;
        for (var scan = 0; scan < 2; scan++)
        {
            var states = await k.ScanAsync(default);
            Assert.Equal(Unreal, states.Single(s => s.Game.Id == _game.Id).Engine);
            var b = states.Single(s => s.Game.Id == broken.Id);
            Assert.Equal(GameStatus.Unsupported, b.Status);
            Assert.Equal(UnreadableReason, b.StatusReason);
        }
    }

    const long Unreadable = 0x7FFF_FFFF_FFFF_0000;   // a FILETIME NTFS holds and FileInfo throws on (past the year 9999)
    const string UnreadableReason = "couldn't read its files: Not a valid Win32 FileTime. (Parameter 'fileTime')";

    static void SetWriteTime(string path, long fileTime)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Write);
        Assert.True(SetFileTime(h, 0, 0, ref fileTime));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, nint created, nint accessed, ref long written);

    /// <summary>An evaluation that fails may be passing (a locked file): the game shows the error over its last state, and
    /// its recorder stays as it is until an evaluation succeeds again.</summary>
    [Fact]
    public async Task A_failed_evaluation_leaves_the_recorder_in_place_and_the_next_good_one_takes_over()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ManageRecorders = true;
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        Assert.True(File.Exists(dll));
        var good = new FileInfo(_game.ExePath).LastWriteTimeUtc.ToFileTimeUtc();
        SetWriteTime(_game.ExePath, Unreadable);
        var failed = (await k.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Unsupported, UnreadableReason), (failed.Status, failed.StatusReason));
        Assert.True(failed.RecorderInstalled);   // the last state's
        Assert.True(File.Exists(dll));
        SetWriteTime(_game.ExePath, good);
        var back = (await k.ScanAsync(default)).Single();
        Assert.NotEqual(GameStatus.Unsupported, back.Status);
        Assert.True(back.RecorderInstalled && File.Exists(dll));
    }

    /// <summary>The exe a game was seen running from can't be checked: the game keeps that exe, unchecked, with the error,
    /// instead of discovery's, and its recorder doesn't move there.</summary>
    [Fact]
    public async Task A_followed_exe_that_cant_be_checked_keeps_the_game_on_it_and_the_recorder_still()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ManageRecorders = true;
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var followed = Path.Combine(Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Binaries", "WinGDK")).FullName, Path.GetFileName(_game.ExePath));
        File.Copy(_game.ExePath, followed);
        var rec = k.Store.LoadGame(_game.Id);
        (rec.RunsExe, rec.RunsExeFrom, rec.RunsExeBuild) = (followed, _game.ExePath, "seen");
        k.Store.SaveGame(_game.Id, rec);
        SetWriteTime(followed, Unreadable);
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal((followed, GameStatus.Unsupported, UnreadableReason), (s.Game.ExePath, s.Status, s.StatusReason));
        Assert.Equal(followed, k.Store.LoadGame(_game.Id).RunsExe);
        Assert.True(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(followed)!, "d3d12.dll")));
    }

    /// <summary>The exe moves within the install and the recorder with it: the install watcher is the new location's, armed
    /// there, and nothing stays armed at the old one.</summary>
    [Fact]
    public async Task The_install_watcher_follows_the_recorder_when_the_exe_moves()
    {
        var source = new FakeSource([_game]);
        var k = Killer(new FakeReader(Unreal), source: source);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await k.CheckRecorderGames(false);
        Assert.EndsWith(_exeDir, k._installWatchers.Keys.Single());
        var newDir = Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Binaries", "WinGDK")).FullName;
        var moved = _game with { ExePath = Path.Combine(newDir, "Fake-WinGDK-Shipping.exe") };
        File.Copy(_game.ExePath, moved.ExePath);
        source.Games = [moved];
        await k.ScanAsync(default);
        k.ReconcileRecorders();   // the recorder moves with the exe
        await k.CheckRecorderGames(false);
        Assert.EndsWith(newDir, k._installWatchers.Keys.Single());
        Assert.True(File.Exists(Path.Combine(newDir, ScsKiller.ArmedFile)));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ArmedFile)));
    }

    /// <summary>Anti-cheat found after the exe moved but before the recorder followed (it waits at its old place, held open
    /// by another process): the armed files go from both places at once; the proxy goes once it's free.</summary>
    [Fact]
    public async Task Anti_cheat_disarms_every_recorder_location_and_a_held_proxy_goes_later()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var source = new FakeSource([_game]);
        var k = Killer(new FakeReader(Unreal), source: source);
        k.ProcessNames = () => new HashSet<string>();
        k.RemovalRetryInterval = TimeSpan.Zero;
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var oldArmed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        Assert.True(File.Exists(oldArmed));
        var newDir = Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Binaries", "WinGDK")).FullName;
        var moved = _game with { ExePath = Path.Combine(newDir, "Fake-WinGDK-Shipping.exe") };
        File.Copy(_game.ExePath, moved.ExePath);
        var newArmed = Path.Combine(newDir, ScsKiller.ArmedFile);
        File.WriteAllText(newArmed, "[scskiller]\r\narmed=1\r\n");   // stands for one armed at the new place
        source.Games = [moved];
        using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
            Assert.Equal(AntiCheat.EasyAntiCheat, (await k.ScanAsync(default)).Single().AntiCheat);
            Assert.False(File.Exists(oldArmed));
            Assert.False(File.Exists(newArmed));
            Assert.True(File.Exists(dll));
        }
        await k.CheckRecorderGames(false);   // the retry
        Assert.False(File.Exists(dll));
    }

    /// <summary>An armed file that can't be deleted (held by another process) never keeps the proxy in: it goes, and the
    /// armed file at the next retry.</summary>
    [Fact]
    public async Task An_armed_file_that_cant_be_deleted_never_keeps_the_proxy_in()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.RemovalRetryInterval = TimeSpan.Zero;
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await k.ScanAsync(default);
        using (new FileStream(armed, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
            File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0x4D, 0x5A]);   // the update's files beside the exe
            Assert.Equal(AntiCheat.EasyAntiCheat, (await k.ScanAsync(default)).Single().AntiCheat);
            Assert.False(File.Exists(dll));
            Assert.True(File.Exists(armed));
        }
        await k.CheckRecorderGames(false);   // the retry
        Assert.False(File.Exists(armed));
    }

    /// <summary>scskiller.armed deleted while the proxy decides (the app disarmed it meanwhile): read again before it admits,
    /// a pass-through. Not deleted: it records.</summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    public void The_proxy_reads_the_armed_file_again_before_it_admits(bool disarm, int computes)
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warm) return;
        var bin = Path.GetDirectoryName(warm)!;
        var exeDir = Directory.CreateDirectory(Path.Combine(_root, "reread")).FullName;
        var exe = Path.Combine(exeDir, "selftest.exe");
        File.Copy(Path.Combine(bin, "selftest.exe"), exe);
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(exeDir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(exeDir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\nframes=0\r\n");
        var armed = Path.Combine(exeDir, ScsKiller.ArmedFile);
        ScsKiller.WriteAttestation(exe);
        var gate = "scsk-admit-" + Guid.NewGuid().ToString("N");
        using var checkedDone = new EventWaitHandle(false, EventResetMode.ManualReset, gate + ".checked");
        using var go = new EventWaitHandle(false, EventResetMode.ManualReset, gate + ".go");
        var start = new ProcessStartInfo(exe, "anticheat -") { RedirectStandardOutput = true };
        start.Environment["SCSKILLER_TEST_ADMIT_GATE"] = gate;
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync();
        Assert.True(checkedDone.WaitOne(TimeSpan.FromSeconds(60)));   // the proxy made its checks and waits before the second read
        if (disarm) File.Delete(armed);
        go.Set();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("created 0x00000000 0x00000000", output.Result);
        var db = Path.Combine(exeDir, "scskiller.db");
        Assert.Equal(computes, File.Exists(db) ? PsoDb.Read(db).Count(r => r.Tag == 'C') : 0);
    }

    /// <summary>After a rejected first device, neither a device factory from an SDK configuration the proxy saw before nor one
    /// from D3D12GetInterface gets the proxy's hook.</summary>
    [Fact]
    public void A_rejected_run_hooks_no_device_factory()
    {
        UseProxyLedger();
        if (OwnWarmExe() == null) return;
        var r = Selftest(Path.Combine(_root, "rejected"), "factoryrejected", armed: null)!.Value;
        Assert.DoesNotContain("hooked 1", r.Output);
        if (!r.Output.Contains("no factory")) Assert.Contains("factory hooked 0", r.Output);
        if (!r.Output.Contains("no config factory")) Assert.Contains("config factory hooked 0", r.Output);
    }

    /// <summary>The app restarts (no install watcher yet) and keeps the recorder. An update adds anti-cheat deep in the install
    /// and replaces the exe while the reconcile's full check runs, just after it read the install clean. The watcher is
    /// made before the check and counts the change, so nothing is armed for the new exe.</summary>
    [Fact]
    public async Task A_change_during_the_check_before_arming_keeps_the_recorder_unarmed()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(armed));
        k.StopWatchingInstalls();   // the app restarts
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0x4D, 0x5A]);   // its folders changed meanwhile: the reconcile checks in full
        var (once, seen) = (0, false);
        k.FullAntiCheatCheck = g =>
        {
            var found = Core.Games.GameFiles.DetectAntiCheat(g);
            if (Interlocked.Exchange(ref once, 1) == 0)
            {
                var gen = k.InstallGen(g);
                Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
                File.WriteAllBytes(g.ExePath, [.. File.ReadAllBytes(g.ExePath), 1]);   // the update's exe
                // the watcher's event counts the change before it revokes the armed file: wait for both; an exception here
                // would make the check fail, which never arms
                seen = SpinWait.SpinUntil(() => k.InstallGen(g) != gen && !File.Exists(armed), TimeSpan.FromSeconds(30));
            }
            return found;
        };
        k.ReconcileRecorders();   // keeps the recorder: watcher, change count, full check (clean, as read), arm
        Assert.True(seen);
        Assert.False(File.Exists(armed));
    }

    /// <summary>The app restarts with a valid attestation from its last session and can't watch the install: it disarms it,
    /// and the recorder stays a pass-through.</summary>
    [Fact]
    public async Task A_restart_that_cant_watch_the_install_disarms_the_last_sessions_attestation()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(armed));
        k.StopWatchingInstalls();   // the app exits

        var restarted = Killer(new FakeReader(Unreal));
        restarted.ProcessNames = () => new HashSet<string>();
        restarted.MakeWatcher = _ => throw new IOException("no watcher");
        await restarted.ScanAsync(default);
        restarted.ReconcileRecorders();
        Assert.False(File.Exists(armed));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>A watcher pass on another thread while an install holds the recorders: its snapshot doesn't show the recorder
    /// yet, but it leaves the install's new watcher alone, and the install arms.</summary>
    [Fact]
    public async Task A_watcher_pass_during_an_install_leaves_its_watcher_alone()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var during = -1;
        k.InstallStep = step =>
        {
            if (step != "copied") return;
            Task.Run(() => k.CheckRecorderGames(false)).Wait();
            lock (k._installWatchers) during = k._installWatchers.Count;
        };
        k.InstallRecorder(_game.Id);
        Assert.Equal(1, during);
        Assert.True(File.Exists(armed));
    }

    /// <summary>An install whose watcher ends while it runs (here a pass on the install's own thread, its snapshot from before
    /// the recorder): the change count moves, so the install doesn't arm across the unwatched time. The next pass, with a
    /// watcher and a fresh full check, does.</summary>
    [Fact]
    public async Task A_watcher_that_ends_during_an_install_keeps_it_unarmed()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallStep = step =>
        {
            if (step == "copied") k.CheckRecorderGames(false).Wait();   // the recorders' lock is this thread's: the pass ends the watcher
        };
        k.InstallRecorder(_game.Id);
        Assert.False(File.Exists(armed));
        k.InstallStep = null;
        await k.CheckRecorderGames(false);
        Assert.True(File.Exists(armed));
    }

    /// <summary>A folder named like one of the recorder's files, moved in next to the exe with anti-cheat inside: the watcher
    /// only reports the folder, and it isn't ignored, so the recorder is disarmed.</summary>
    [Fact]
    public async Task A_folder_named_like_a_recorder_file_still_disarms()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(armed));
        var staged = Directory.CreateDirectory(Path.Combine(_root, "staging", "scskiller.log")).FullName;
        Directory.CreateDirectory(Path.Combine(staged, "EasyAntiCheat"));
        Directory.Move(staged, Path.Combine(_exeDir, "scskiller.log"));
        await Until(() => Revoked(armed));
    }

    /// <summary>The install root renamed aside and a clean copy put back at its path: the old watcher follows the renamed
    /// folder, so the reconcile sees the root's identity changed, disarms, and watches the new folder before it arms again.
    /// Anti-cheat added in the new tree then disarms it.</summary>
    [Fact]
    public async Task A_replaced_install_root_gets_a_new_watcher()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(armed));
        var aside = _game.InstallDir + ".old";
        Directory.Move(_game.InstallDir, aside);
        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
        }
        Copy(aside, _game.InstallDir);   // the clean tree back at the same path, the exe as it was
        k.ReconcileRecorders();
        Assert.True(File.Exists(armed));   // checked clean in the new folder, watched there
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        await Until(() => Revoked(armed));
    }

    /// <summary>scskiller.armed held open by another process when an install change (anti-cheat added deep) disarms it, as an
    /// antivirus or an indexer does: shared for reading and writing (it's rewritten as armed=0), for reading only (it can't
    /// be deleted, rewritten or renamed, and stays readable as armed=1), or not at all. The ledger entry goes first in every
    /// case, so the game launched then records nothing, and nothing is left pending.</summary>
    [Theory]
    [InlineData(FileShare.ReadWrite)]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.None)]
    public async Task A_held_attestation_is_revoked_through_the_ledger_and_the_launch_records_nothing(FileShare share)
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warm) return;
        var bin = Path.GetDirectoryName(warm)!;
        File.Copy(Path.Combine(bin, "selftest.exe"), _game.ExePath, true);   // the game's exe: the process the proxy is loaded in
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var ledger = ScsKiller.LedgerFile(_game.ExePath);
        Assert.True(File.Exists(armed) && File.Exists(ledger));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(_exeDir, "d3d12.dll"), true);   // the built proxy in the fake's place
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\nframes=0\r\n");
        int Launch()
        {
            var start = new ProcessStartInfo(_game.ExePath, "anticheat -") { RedirectStandardOutput = true };
            start.Environment["SCSKILLER_SELFTEST_UNARMED"] = "1";
            using var p = Process.Start(start)!;
            Assert.Contains("created 0x00000000 0x00000000", p.StandardOutput.ReadToEnd());
            p.WaitForExit();
            var db = Path.Combine(_exeDir, "scskiller.db");
            var n = File.Exists(db) ? PsoDb.Read(db).Count(r => r.Tag == 'C') : 0;
            File.Delete(db);
            return n;
        }
        Assert.Equal(2, Launch());   // as armed

        using (new FileStream(armed, FileMode.Open, FileAccess.Read, share))
        {
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
            await Until(() => !File.Exists(ledger));
            Assert.True(File.Exists(armed));
            Assert.False(k.RevocationPending(_game.Id));
            Assert.Equal(0, Launch());
        }
    }

    /// <summary>The ledger entry itself locked outright: it can't be deleted, rewritten or renamed, so the game stays
    /// revocation-pending, nothing arms it, and every watcher pass tries again. Released, it is revoked, and only a new clean
    /// check arms the game again (a new attestation).</summary>
    [Fact]
    public async Task A_locked_ledger_entry_stays_revocation_pending_until_released()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await k.CheckRecorderGames(false);
        var ledger = ScsKiller.LedgerFile(_game.ExePath);
        var before = File.ReadAllText(ledger);
        using (new FileStream(ledger, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0]);
            await Until(() => k.RevocationPending(_game.Id));
            k.ReconcileRecorders();   // checked clean, but not armed
            await k.CheckRecorderGames(true);
            Assert.True(k.RevocationPending(_game.Id));
            Assert.False(File.Exists(armed));
        }
        await Until(() => !k.DisarmQueued(_game));   // the event's disarm done in the background
        await k.CheckRecorderGames(true);   // revoked first, then a clean check arms it anew
        Assert.False(k.RevocationPending(_game.Id));
        Assert.NotEqual(before, File.ReadAllText(ledger));
        Assert.Contains("armed=1", File.ReadAllText(armed));
    }

    /// <summary>A valid scskiller.armed is not enough: its ledger entry must exist and hold the same nonce.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_proxy_needs_the_ledger_entry(bool otherNonce)
    {
        UseProxyLedger();
        if (OwnWarmExe() == null) return;
        var exeDir = Path.Combine(_root, "ledger");
        var r = Selftest(exeDir, "anticheat -", armed: Armed, ledger: ledger =>
        {
            if (otherNonce) File.WriteAllText(ledger, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(ledger), "nonce=[0-9a-f]+", "nonce=0123456789abcdef0123456789abcdef"));
            else File.Delete(ledger);
        })!.Value;
        Assert.Equal(0, r.Computes);
    }

    /// <summary>Both scskiller.armed and its ledger entry held open for reading by another program: neither can be revoked,
    /// so the game stays revocation-pending and the app makes a new &lt;entry&gt;.revoked beside the entry, on which the proxy
    /// refuses: the launch records nothing. Released, the entry is revoked, the mark goes at the next arming, and it records.</summary>
    [Fact]
    public async Task A_revocation_mark_beside_a_held_ledger_entry_refuses_the_launch()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warm) return;
        var bin = Path.GetDirectoryName(warm)!;
        File.Copy(Path.Combine(bin, "selftest.exe"), _game.ExePath, true);   // the game's exe: the process the proxy is loaded in
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var ledger = ScsKiller.LedgerFile(_game.ExePath);
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(_exeDir, "d3d12.dll"), true);   // the built proxy in the fake's place
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\nframes=0\r\n");
        int Launch()
        {
            var start = new ProcessStartInfo(_game.ExePath, "anticheat -") { RedirectStandardOutput = true };
            start.Environment["SCSKILLER_SELFTEST_UNARMED"] = "1";
            using var p = Process.Start(start)!;
            Assert.Contains("created 0x00000000 0x00000000", p.StandardOutput.ReadToEnd());
            p.WaitForExit();
            var db = Path.Combine(_exeDir, "scskiller.db");
            var n = File.Exists(db) ? PsoDb.Read(db).Count(r => r.Tag == 'C') : 0;
            File.Delete(db);
            return n;
        }
        using (new FileStream(armed, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new FileStream(ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0]);   // a change: the watcher's event disarms
            await Until(() => k.RevocationPending(_game.Id));
            Assert.True(File.Exists(ledger + ".revoked"));
            Assert.Equal(0, Launch());
        }
        await Until(() => !k.DisarmQueued(_game));   // the event's disarm done in the background
        await k.CheckRecorderGames(true);   // revoked, then checked clean and armed again
        Assert.False(k.RevocationPending(_game.Id));
        Assert.False(File.Exists(ledger + ".revoked"));
        Assert.Equal(2, Launch());
    }

    /// <summary>The game's record (state.json) held when a change disarms: the watched location's ledger entry and armed file
    /// are revoked at once, before the record is read (which waits for it).</summary>
    [Fact]
    public async Task A_held_game_record_doesnt_delay_revocation()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var ledger = ScsKiller.LedgerFile(_game.ExePath);
        Assert.True(File.Exists(armed) && File.Exists(ledger));
        await WithRecordHeld(k, async _ =>
        {
            File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0]);
            await Until(() => Revoked(ledger) && Revoked(armed), 30);
        });
    }

    static readonly AsyncLocal<bool> RecordFree = new();

    /// <summary>Runs <paramref name="f"/> with the record <see cref="WithRecordHeld"/> holds readable, in the tasks it starts too.</summary>
    static async Task Unheld(Func<Task> f)
    {
        RecordFree.Value = true;   // set inside this async method: the caller's flow keeps its own value
        await f();
    }

    /// <summary>No attestation left in a ledger entry or armed file: gone, revoked in place (a file something holds open without
    /// delete sharing is emptied and rewritten), or a revocation mark beside it (one that couldn't be revoked at all).</summary>
    static bool Revoked(string file)
    {
        if (File.Exists(file + ".revoked")) return true;
        try
        {
            // full sharing: a reader that denies delete or write would make the revocation it waits for fail
            using var f = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return !System.Text.RegularExpressions.Regex.IsMatch(f.ReadToEnd(), @"(?m)^(armed=1|nonce=\w)");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Runs <paramref name="body"/> with the game's record held: every read of it outside <see cref="Unheld"/> waits until
    /// the body ends, or throws after 60 s, past every wait of the body (fails the test, never hangs it). The body gets how
    /// many reads reached the hold.</summary>
    async Task WithRecordHeld(ScsKiller k, Func<Func<int>, Task> body)
    {
        using var held = new ManualResetEventSlim();
        var reached = 0;
        k.Store.LoadingGame = id =>
        {
            if (id != _game.Id || RecordFree.Value) return;
            Interlocked.Increment(ref reached);
            if (!held.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("the game's record was held for 60 s");
        };
        try { await body(() => Volatile.Read(ref reached)); }
        finally
        {
            k.Store.LoadingGame = null;
            held.Set();
            await Until(() => !k.DisarmQueued(_game), 30);   // the disarms it held end before the test's folder goes
        }
    }

    /// <summary>The game's record held while a change (a new binary) disarms: the background part of the disarm waits for
    /// the record, and until it's done a watcher pass that checks the install clean doesn't arm it again. A second change
    /// (anti-cheat added deep) meanwhile is handled at once, not behind the first.</summary>
    [Fact]
    public async Task A_disarm_waiting_for_the_game_record_keeps_the_recorder_unarmed()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await k.CheckRecorderGames(false);
        var ledger = ScsKiller.LedgerFile(_game.ExePath);
        Assert.True(File.Exists(armed) && File.Exists(ledger));
        await WithRecordHeld(k, async reached =>
        {
            File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0]);   // a new binary: disarmed until checked
            await Until(() => Revoked(ledger) && Revoked(armed), 30);   // the revocation takes the ledger entry, then the armed file
            // checked clean, but a disarm is still at work: not armed. The pass reads the record itself when it retries a
            // revocation a briefly held ledger entry left pending
            await Unheld(() => k.CheckRecorderGames(true));
            Assert.True(Revoked(ledger) && Revoked(armed));
            await Until(() => reached() > 0, 30);   // the first disarm counted its change before it read the record: not counted after gen
            var gen = k.InstallGen(_game);
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
            await Until(() => k.InstallGen(_game) != gen, 30);
        });
    }

    /// <summary>A game the user added, with our proxy in its folder (one a development build left): the watcher's pass and a
    /// reconcile check it clean, but it is never armed: no ledger entry, no scskiller.armed.</summary>
    [Fact]
    public async Task A_game_the_user_added_is_never_armed()
    {
        var (_, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Armed"));
        var dir = Path.GetDirectoryName(shipping)!;
        File.Copy(_proxy, Path.Combine(dir, "d3d12.dll"));
        var k = Killer(new FakeReader(Unreal), sources: [Manual()]);
        k.ProcessNames = () => new HashSet<string>();
        k.AddManualGame(stub);
        await k.ScanAsync(default);
        var game = k.Games.Single().Game;
        await k.CheckRecorderGames(true);
        k.ReconcileRecorders();
        await k.CheckRecorderGames(true);
        Assert.False(File.Exists(ScsKiller.LedgerFile(game.ExePath)));
        Assert.False(File.Exists(Path.Combine(dir, ScsKiller.ArmedFile)));
    }

    /// <summary>An HDR mod that blocks recording (ReShade with it in the install root), found by a scan with no install watcher (as the command line scans) while
    /// the proxy is held open: the removal waits, but the game is disarmed at once (its ledger entry revoked).</summary>
    [Fact]
    public async Task A_blocking_hdr_mod_disarms_even_when_the_recorder_cant_come_out_yet()
    {
        File.WriteAllBytes(Path.Combine(_game.InstallDir, "dxgi.dll"), ReShadeDll);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var ledger = ScsKiller.LedgerFile(_game.ExePath);
        Assert.True(File.Exists(ledger));
        k.StopWatchingInstalls();
        File.WriteAllBytes(Path.Combine(_game.InstallDir, "renodx-ff7rebirth.addon64"), RenoDxAddon);
        using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True((await k.ScanAsync(default)).Single().ShaderModBlocks);
            Assert.False(File.Exists(ledger));
            Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ArmedFile)));
            Assert.True(File.Exists(dll));
        }
    }

    /// <summary>Detect cancels the scan once, on the given game: its evaluation finishes, the scan stops after it.</summary>
    sealed class CancellingReader(EngineInfo engine, string gameId, CancellationTokenSource cts) : IEngineReader
    {
        public EngineInfo? Detect(Game game)
        {
            if (game.Id == gameId) cts.Cancel();
            return engine;
        }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) =>
            new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    [Fact]
    public async Task A_scan_cancelled_after_finding_anti_cheat_has_taken_the_recorder_out_already()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var second = FakeGame("test:second", "Second");
        var k = Killer(new FakeReader(Unreal), games: [_game, second]);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));   // deep: only a full scan sees it
        File.SetLastWriteTimeUtc(_game.ExePath, DateTime.UtcNow.AddMinutes(1));   // an update: the evaluation is redone
        using var cts = new CancellationTokenSource();
        var cancelling = Killer(new CancellingReader(Unreal, _game.Id, cts), games: [_game, second]);
        cancelling.ProcessNames = () => new HashSet<string>();
        cancelling.ManageRecorders = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.ScanAsync(cts.Token));   // no reconcile ran
        Assert.False(File.Exists(dll));
    }

    [Fact]
    public async Task Updating_an_older_recorder_in_a_game_that_gained_anti_cheat_takes_it_out()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        File.WriteAllBytes(dll, [.. "MZ older proxy SCSKiller_StartWarm "u8, .. Guid.NewGuid().ToByteArray()]);   // another build's: updated next
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));   // past the cached verdict and the name probe
        k.ReconcileRecorders(_game.Id);
        Assert.False(File.Exists(dll));
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
    }

    [Fact]
    public async Task A_stopped_compile_takes_the_recorder_out_of_an_anti_cheat_game_though_the_scan_cache_cant_be_written()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var scan = Path.Combine(_root, "data", "scan.json");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        k.ManageRecorders = true;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = Task.Factory.StartNew(() =>
        {
            using (Recordings.Lock(Path.Combine(k.Store.GameDir(_game.Id), "recording.db"))) { held.Set(); release.Wait(); }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        try
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            await Until(() => k.Queue.Single().Stage is not QueueStage.Waiting);
            File.SetLastWriteTimeUtc(_game.ExePath, DateTime.UtcNow.AddMinutes(1));   // the closing refresh evaluates again
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
            File.SetAttributes(scan, FileAttributes.ReadOnly);
            await Task.Delay(300);
            k.StopQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(dll));
            Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        }
        finally
        {
            release.Set();
            File.SetAttributes(scan, FileAttributes.Normal);
        }
        await other;
    }

    [Fact]
    public async Task Nothing_is_written_into_a_running_games_folder_while_its_recorder_waits_to_be_removed()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        running.Add(Path.GetFileNameWithoutExtension(_game.ExePath));   // the game started
        File.WriteAllBytes(Path.Combine(_exeDir, "BEService_x64.exe"), [0]);
        k.RefreshGame(_game.Id);
        Assert.True(ScsKiller.IsOurProxy(dll));   // pending: untouched while it runs
        Assert.True(k.Store.LoadGame(_game.Id).RecorderRollback);
        if (File.Exists(keys)) File.Delete(keys);
        var iniBefore = File.ReadAllBytes(ini);
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db")))   // it recorded a shader meanwhile
        {
            var shader = "DXBC a shader built at run time"u8.ToArray();
            PsoDb.Write(f, 'B', [.. SHA1.HashData(shader), .. shader]);
        }
        k.Settings = k.Settings with { RecordingLimitMB = 32 };   // the ini's limit line would change
        await k.SettingsRefresh;
        k.ReconcileRecorders(_game.Id);
        Assert.True(ScsKiller.IsOurProxy(dll));
        Assert.False(File.Exists(keys));
        Assert.Equal(iniBefore, File.ReadAllBytes(ini));

        running.Clear();   // it exited
        k.ReconcileRecorders(_game.Id);
        Assert.False(File.Exists(dll));
        Assert.False(File.Exists(ini));
    }

    [Fact]
    public async Task An_install_that_finds_the_game_started_and_anti_cheat_after_the_copy_leaves_its_folder_until_it_exits()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(dll, mod);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallStep = step =>
        {
            if (step != "copied") return;
            running.Add(Path.GetFileNameWithoutExtension(_game.ExePath));
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        };
        k.SetRecordAlongsideMod(_game.Id, true);   // installs, alongside the mod
        Assert.True(ScsKiller.IsOurProxy(dll));   // nothing deleted while it runs
        Assert.True(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
        Assert.True(k.Store.LoadGame(_game.Id).RecorderRollback);

        k.InstallStep = null;
        running.Clear();
        k.ReconcileRecorders(_game.Id);
        Assert.Equal(mod, File.ReadAllBytes(dll));   // the mod is back
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
    }

    [Fact]
    public async Task A_blob_record_too_short_for_its_hash_fails_only_the_import_not_the_scan()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), [(byte)'B', 1, 0, 0, 0, 0]);   // a framed 'B' of one byte
        Assert.Contains(await k.ScanAsync(default), s => s.Game.Id == _game.Id);
        Assert.True(File.Exists(Path.Combine(_exeDir, "scskiller.db")));   // left as it is
    }

    [Fact]
    public async Task Stopping_the_queue_returns_promptly_while_another_process_holds_the_recording_lock()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db")))   // a recording not imported yet
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = Task.Factory.StartNew(() =>
        {
            using (Recordings.Lock(Path.Combine(k.Store.GameDir(_game.Id), "recording.db"))) { held.Set(); release.Wait(); }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        try
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            await Until(() => k.Queue.Single().Stage is not QueueStage.Waiting);
            await Task.Delay(300);   // waiting for the lock
            k.StopQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(Path.Combine(_exeDir, "scskiller.db")));   // left for the next scan
        }
        finally { release.Set(); }
        await other;
    }

    [Fact]
    public async Task A_stopped_compile_still_takes_the_recorder_out_of_a_game_that_gained_anti_cheat()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        k.ManageRecorders = true;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = Task.Factory.StartNew(() =>   // the compile waits for another process's recording lock
        {
            using (Recordings.Lock(Path.Combine(k.Store.GameDir(_game.Id), "recording.db"))) { held.Set(); release.Wait(); }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        try
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            await Until(() => k.Queue.Single().Stage is not QueueStage.Waiting);
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));   // an update meanwhile
            await Task.Delay(300);
            k.StopQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
            Assert.False(File.Exists(dll));
            Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        }
        finally { release.Set(); }
        await other;
    }

    [Fact]
    public async Task A_stop_during_the_closing_refresh_ends_its_wait_for_the_recording_lock()
    {
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? other = null;
        k.RecordingsRead = () =>   // the compile has the lock now; another process takes it as soon as it lets go
        {
            using (var f = File.Create(inbox)) PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));   // recorded meanwhile
            other = Task.Factory.StartNew(() =>
            {
                using (Recordings.Lock(Path.Combine(k.Store.GameDir(_game.Id), "recording.db"))) { held.Set(); release.Wait(); }
            }, TaskCreationOptions.LongRunning);
        };
        try
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            await Task.Delay(500);   // the compile ends and its closing refresh waits to import
            Assert.False(k.WhenQueueIdle().IsCompleted);
            k.StopQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        await other!;
        Assert.True(new FileInfo(inbox).Length > 0);   // still there for the next import
        k.RefreshGame(_game.Id);
        Assert.Equal(0, new FileInfo(inbox).Length);
    }

    [Fact]
    public async Task A_proxy_locked_against_reading_still_leaves_the_verdict_and_a_pending_removal()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        File.WriteAllBytes(Path.Combine(_exeDir, "BEService_x64.exe"), [0]);
        using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.None))   // not even readable
        {
            k.RefreshGame(_game.Id);
            Assert.Equal(AntiCheat.BattlEye, k.Games.Single().AntiCheat);
            Assert.True(k.Store.LoadGame(_game.Id).RecorderRollback);
        }
        k.ReconcileRecorders(_game.Id);
        Assert.False(File.Exists(dll));
    }

    [Fact]
    public async Task A_verdict_a_reader_meets_skips_its_work_and_is_acted_on_at_the_next_evaluation()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));   // past the cached verdict
        Assert.Empty(Middleware.Detect(_game));   // shared packs and community sync look for DLLs like this: none read
        await k.RescanAsync(default);   // the next full evaluation
        Assert.False(File.Exists(dll));
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
    }

    [Fact]
    public async Task A_recording_lock_that_cant_be_taken_never_keeps_the_refresh_from_taking_the_recorder_out()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var rec = k.Store.LoadGame(_game.Id);
        rec.KeysPending = true;   // a keys rewrite waits for the next refresh
        k.Store.SaveGame(_game.Id, rec);
        var lockFile = Path.Combine(k.Store.GameDir(_game.Id), "recording.db.lock");
        File.WriteAllText(lockFile, "");
        File.SetAttributes(lockFile, FileAttributes.ReadOnly);   // the recording lock can't be taken
        File.WriteAllBytes(Path.Combine(_exeDir, "BEService_x64.exe"), [0]);
        try
        {
            k.RefreshGame(_game.Id);
            Assert.Equal(AntiCheat.BattlEye, k.Games.Single().AntiCheat);
            Assert.False(File.Exists(dll));
        }
        finally { File.SetAttributes(lockFile, FileAttributes.Normal); }
    }

    [Fact]
    public async Task A_game_that_starts_after_the_mods_rename_finds_nothing_moved_and_gets_the_mod_back_once_it_exits()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var chained = Path.Combine(_exeDir, ScsKiller.ChainName);
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(dll, mod);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallStep = step => { if (step == "copy") running.Add(Path.GetFileNameWithoutExtension(_game.ExePath)); };   // renamed, not copied yet
        k.SetRecordAlongsideMod(_game.Id, true);
        Assert.False(File.Exists(dll));   // nothing moved while it runs
        Assert.Equal(mod, File.ReadAllBytes(chained));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.True(rec.RecorderRollback);
        Assert.NotNull(rec.RecorderChained);   // so the restore knows what to put back

        k.InstallStep = null;
        running.Clear();   // it exited
        k.ReconcileRecorders(_game.Id);
        Assert.Equal(mod, File.ReadAllBytes(dll));
        Assert.False(File.Exists(chained));
    }

    [Fact]
    public async Task A_game_that_starts_while_the_install_saves_the_mods_rename_keeps_its_mod_in_place()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(dll, mod);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallStep = step => { if (step == "chain") running.Add(Path.GetFileNameWithoutExtension(_game.ExePath)); };   // during the record's save
        k.SetRecordAlongsideMod(_game.Id, true);
        Assert.Equal(mod, File.ReadAllBytes(dll));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderChained);
    }

    [Fact]
    public async Task A_keys_rewrite_that_waited_for_the_lock_while_the_game_started_writes_once_it_exits()
    {
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        File.Delete(keys);
        var shader = "DXBC a shader built at run time"u8.ToArray();
        using (var f = File.Create(inbox)) PsoDb.Write(f, 'B', [.. SHA1.HashData(shader), .. shader]);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = Task.Factory.StartNew(() =>
        {
            using (Recordings.Lock(Path.Combine(k.Store.GameDir(_game.Id), "recording.db"))) { held.Set(); release.Wait(); }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        var refresh = Task.Factory.StartNew(() => k.RefreshGame(_game.Id), TaskCreationOptions.LongRunning);   // waits to import
        await Task.Delay(300);
        lock (running) running.Add(Path.GetFileNameWithoutExtension(_game.ExePath));   // the game starts meanwhile
        release.Set();
        await other;
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(keys));   // nothing in its folder while it runs
        Assert.True(new FileInfo(inbox).Length > 0);
        Assert.True(k.Store.LoadGame(_game.Id).KeysPending);

        lock (running) running.Clear();   // it exited
        k.ReconcileRecorders(_game.Id);
        Assert.True(File.Exists(keys));
        Assert.Contains(SHA1.HashData(shader).AsSpan().ToArray(), File.ReadAllBytes(keys)[8..].Chunk(20), new BytesEqual());
        Assert.False(k.Store.LoadGame(_game.Id).KeysPending);
        k.RefreshGame(_game.Id);
        Assert.Equal(0, new FileInfo(inbox).Length);   // emptied once it's free, already imported
    }

    sealed class BytesEqual : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? a, byte[]? b) => a.AsSpan().SequenceEqual(b);
        public int GetHashCode(byte[] b) => b.Length;
    }

    [Fact]
    public async Task An_install_that_finds_the_game_started_after_the_copy_writes_no_ini_and_goes_once_it_exits()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallStep = step => { if (step == "copied") running.Add(Path.GetFileNameWithoutExtension(_game.ExePath)); };
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        Assert.False(File.Exists(ini));
        Assert.True(k.Store.LoadGame(_game.Id).RecorderRollback);

        k.InstallStep = null;
        running.Clear();
        k.ReconcileRecorders(_game.Id);   // the leftover proxy goes
        Assert.False(File.Exists(dll));
        k.ReconcileRecorders(_game.Id);   // and the switch, still On, installs it again
        Assert.True(File.Exists(ini));
    }

    [Fact]
    public async Task An_update_stopped_by_the_game_starting_keeps_the_mod_it_found_chained()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(dll, mod);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.SetRecordAlongsideMod(_game.Id, true);   // installed alongside the mod
        Assert.NotNull(k.Store.LoadGame(_game.Id).RecorderChained);
        File.WriteAllBytes(dll, [.. "MZ older proxy SCSKiller_StartWarm "u8, .. Guid.NewGuid().ToByteArray()]);   // another build's: updated next
        k.InstallStep = step => { if (step == "copy") running.Add(Path.GetFileNameWithoutExtension(_game.ExePath)); };
        k.ReconcileRecorders(_game.Id);
        Assert.NotNull(k.Store.LoadGame(_game.Id).RecorderChained);   // the chain it found stays

        k.InstallStep = null;
        running.Clear();
        k.UninstallRecorder(_game.Id);
        Assert.Equal(mod, File.ReadAllBytes(dll));   // so the mod comes back
    }

    [Fact]
    public async Task The_uninstall_hook_leaves_a_game_whose_launcher_runs_from_its_install_root()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));   // exe in <install>\Fake\Binaries\Win64
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var launcher = Path.Combine(_game.InstallDir, "Launched.exe");   // <install>\Launched.exe
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), launcher);
        Core.Warming.ProcessTree.RecentSnapshot();
        using var p = Process.Start(new ProcessStartInfo(launcher, "/c ping -n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            // at once: the cached snapshot, taken just before the launch, would miss it
            ScsKiller.RemoveAllRecorders(k.Store, OurNames());
            Assert.True(ScsKiller.IsOurProxy(dll));
        }
        finally
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
        }
    }

    [Fact]
    public async Task The_uninstall_hook_keeps_the_recorders_data_when_the_game_starts_while_it_waits_for_the_lock()
    {
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        using (var f = File.Create(inbox)) PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));   // not imported yet
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = Task.Factory.StartNew(() =>
        {
            using (Recordings.Lock(Path.Combine(k.Store.GameDir(_game.Id), "recording.db"))) { held.Set(); release.Wait(); }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        var hook = Task.Factory.StartNew(() => ScsKiller.RemoveAllRecorders(k.Store, OurNames, TimeSpan.FromSeconds(15)),
            TaskCreationOptions.LongRunning);
        await Until(() => !File.Exists(Path.Combine(_exeDir, "d3d12.dll")));   // the recorder is out; its data waits for the merge
        var launched = Path.Combine(_exeDir, "Launched.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), launched);
        Core.Warming.ProcessTree.RecentSnapshot();
        using var p = Process.Start(new ProcessStartInfo(launched, "/c ping -n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            // at once: the cached snapshot, taken just before the launch, would miss it
            release.Set();
            await hook.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(File.Exists(inbox));   // merged, but left in the running game's folder
            Assert.Contains("the game started: the recorder's data files left", RecordersLog());
        }
        finally
        {
            release.Set();
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
        }
        await other;
    }

    [Fact]
    public async Task A_game_a_launcher_started_under_another_exe_name_counts_as_running()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var launched = Path.Combine(_exeDir, "Launched.exe");   // what the launcher started: an exe of the game's folder
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), launched);
        var other = FakeGame("test:other", "Other");   // with a recorder, installed before the launch
        var k = Killer(new FakeReader(Unreal), games: [_game, other]);
        k.ProcessNames = () => new HashSet<string>();
        k.RunningGameExes = () => new HashSet<string>();   // the discovered exe isn't running
        await k.ScanAsync(default);
        k.InstallRecorder(other.Id);
        File.Copy(launched, Path.Combine(other.InstallDir, "Launched2.exe"));
        using var p = Process.Start(new ProcessStartInfo(launched, "/c ping -n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        using var p2 = Process.Start(new ProcessStartInfo(Path.Combine(other.InstallDir, "Launched2.exe"), "/c ping -n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            await Task.Delay(1200);   // past the process snapshot's second
            k.PollGames();
            Assert.True(k.IsPlaying(_game.Id));
            Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
            Assert.False(File.Exists(dll));
            ScsKiller.RemoveAllRecorders(k.Store, OurNames());   // SCSKiller's uninstall: by name the game isn't running
            Assert.True(ScsKiller.IsOurProxy(Path.Combine(other.InstallDir, "d3d12.dll")));
            Assert.Contains("is running: recorder left in", RecordersLog());
        }
        finally
        {
            foreach (var x in new[] { p, p2 })
            {
                x.Kill(entireProcessTree: true);
                x.WaitForExit();
            }
        }
    }

    [Fact]
    public async Task A_keys_write_that_finds_the_proxy_gone_before_publishing_writes_nothing()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db")))   // the game recorded a shader: the next refresh imports it and names it
        {
            var shader = "DXBC a shader built at run time"u8.ToArray();
            PsoDb.Write(f, 'B', [.. SHA1.HashData(shader), .. shader]);
        }
        Recordings.BeforeKeysPublished = () =>   // a rollback meanwhile, without the recording lock
        {
            File.Delete(dll);
            File.Delete(keys);
        };
        try { k.RefreshGame(_game.Id); }
        finally { Recordings.BeforeKeysPublished = null; }
        Assert.False(File.Exists(keys));
        Assert.Empty(Directory.GetFiles(_exeDir, "*.tmp"));
    }

    [Fact]
    public async Task A_pending_takeout_with_nothing_left_clears_and_recording_resumes()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var lockFile = Path.Combine(k.Store.GameDir(_game.Id), "recording.db.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        File.WriteAllText(lockFile, "");
        File.SetAttributes(lockFile, FileAttributes.ReadOnly);   // the keys file can't be written: the install fails after the copy
        try
        {
            Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
            Assert.False(File.Exists(dll));
        }
        finally { File.SetAttributes(lockFile, FileAttributes.Normal); }
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderRollback = true;   // left set with nothing of ours in the folder
        k.Store.SaveGame(_game.Id, rec);
        k.ReconcileRecorders(_game.Id);
        Assert.False(k.Store.LoadGame(_game.Id).RecorderRollback);
        k.ReconcileRecorders(_game.Id);
        Assert.True(ScsKiller.IsOurProxy(dll));   // the switch is On: recording again
    }

    [Fact]
    public async Task A_failed_install_whose_proxy_is_held_is_taken_out_by_the_next_reconcile_with_the_switch_on()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        FileStream? held = null;
        k.InstallStep = step =>
        {
            if (step != "copied") return;
            held = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read);   // the game started: no delete
            throw new IOException("the disk is full");
        };
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        Assert.True(ScsKiller.IsOurProxy(dll));
        Assert.True(k.Store.LoadGame(_game.Id).RecorderRollback);

        held!.Dispose();   // the game exited
        k.InstallStep = null;
        var fresh = Killer(new FakeReader(Unreal));   // after a restart too: the record says it
        fresh.ProcessNames = () => new HashSet<string>();
        await fresh.ScanAsync(default);
        fresh.ReconcileRecorders(_game.Id);
        fresh.ReconcileRecorders(_game.Id);
        Assert.Contains($"{_game.Name}: a failed install's recorder: recorder removed", RecordersLog());   // taken out with the switch still On
        Assert.Equal(RecorderOverride.On, fresh.Store.LoadGame(_game.Id).Recorder);
        Assert.False(fresh.Store.LoadGame(_game.Id).RecorderRollback);
        Assert.True(ScsKiller.IsOurProxy(dll));   // and then installed again, cleanly
        Assert.Contains("d3d12.dll", fresh.Store.LoadGame(_game.Id).RecorderFiles.Keys);
    }

    [Fact]
    public async Task The_uninstall_hook_waits_for_a_held_lock_no_longer_than_its_budget()
    {
        var k = RecordKiller([_game, FakeGame("test:second", "Second")]);
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        k.InstallRecorder("test:second");
        using var held = new FileStream(Path.Combine(k.Store.GameDir(_game.Id), "state.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var clock = Stopwatch.StartNew();
        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>(), TimeSpan.FromSeconds(1));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 5);
        Assert.Contains("out of time", RecordersLog());
    }

    [Fact]
    public async Task Anti_cheat_appearing_while_the_game_holds_the_new_recorder_counts_and_removes_it_once_the_game_lets_go()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(mod, bytes);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        FileStream? loaded = null;
        k.InstallStep = step =>
        {
            if (step == "copy") Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
            if (step == "copied") loaded = new FileStream(mod, FileMode.Open, FileAccess.Read, FileShare.Read);   // the game started: no delete
        };
        k.SetRecordAlongsideMod(_game.Id, true);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        Assert.True(ScsKiller.IsOurProxy(mod));
        var s = k.Games.Single();
        Assert.Equal(AntiCheat.EasyAntiCheat, s.AntiCheat);   // counted, though the proxy is still there
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Store.LoadScan()[_game.Id].AntiCheat);   // and so after a restart
        var rec = k.Store.LoadGame(_game.Id);
        Assert.NotNull(rec.RecorderChained);   // the manifest the removal needs is kept
        Assert.Contains("d3d12.dll", rec.RecorderFiles.Keys);

        loaded!.Dispose();   // the game exited
        k.ReconcileRecorders(_game.Id);
        Assert.Equal(bytes, File.ReadAllBytes(mod));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
        Assert.False(k.Games.Single().RecorderInstalled);
    }

    [Fact]
    public async Task Dropping_an_old_community_merge_is_decided_under_the_recording_lock()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        var dir = k.Store.GameDir(_game.Id);
        var store = Path.Combine(dir, "recording.db");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "recording.all.db"), [.. "only the community's"u8]);   // no copy of this PC's: dropped
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var recorded = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        var other = Task.Factory.StartNew(() =>
        {
            using (Recordings.Lock(store))   // another process imports a recording meanwhile
            {
                held.Set();
                release.Wait();
                PsoDb.WriteCompact(store, [recorded]);
            }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        await k.ScanAsync(default);
        Assert.False(k.RecordingMigration.Wait(300));   // waits for it rather than deciding on the empty copy
        release.Set();
        await other;
        await k.RecordingMigration.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([recorded.Key], PsoDb.Read(store).Select(r => r.Key));
        Assert.False(File.Exists(Path.Combine(dir, "recording.all.db")));
    }

    [Fact]
    public async Task Clearing_a_recording_waits_for_a_write_in_progress()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        var store = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllBytes(store, [1, 2, 3]);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var migration = Task.Factory.StartNew(() =>
        {
            using (Recordings.Lock(store))   // a migration that read the old file and is about to put its merge in place
            {
                held.Set();
                release.Wait();
                File.WriteAllBytes(store, [4, 5, 6]);
            }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        var clear = Task.Factory.StartNew(() => k.ClearRecording(_game.Id), TaskCreationOptions.LongRunning);
        Assert.False(clear.Wait(300));
        Assert.True(File.Exists(store));
        release.Set();
        await migration;
        Assert.True(await clear.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(store));   // cleared after the write, not undone by it
    }

    [Fact]
    public async Task Recorder_install_then_uninstall_removes_every_file_of_the_recorder_and_imports_the_recording()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        Assert.False(k.Games.Single().RecorderInstalled);
        var before = Directory.GetFiles(_exeDir).Order().ToList();

        k.InstallRecorder(_game.Id);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Contains("mode=record", File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.True(k.Games.Single().RecorderInstalled);

        // the game ran with the recorder: it wrote its db, log, timings and frame times
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db"))) PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        var recorded = PsoDb.Read(Path.Combine(_exeDir, "scskiller.db")).ToList();
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), "10.0,G,0,0,50.0\n11.0,s,0,0,0.2\n12.0,C,1,1,0.5\n");
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.log"), "loaded\n");
        File.WriteAllBytes(Path.Combine(_exeDir, FrameLog.FileName), new byte[36]);
        k.RefreshGame(_game.Id);
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(12), 3, 1, 1, 1, 50.0), k.Games.Single().LastSession);

        k.UninstallRecorder(_game.Id);
        Assert.Equal(before, Directory.GetFiles(_exeDir).Order());
        Assert.Equal(recorded.Select(r => r.Key), PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Select(r => r.Key));
        var s = k.Games.Single();
        Assert.False(s.RecorderInstalled);
        Assert.Null(s.LastSession);
    }

    [Fact]
    public async Task Uninstall_removes_an_untracked_recorder_of_ours()
    {
        File.Copy(_proxy, Path.Combine(_exeDir, "d3d12.dll"));   // installed by hand, before SCSKiller tracked it
        var k = Killer();
        await k.ScanAsync(default);
        Assert.True(k.Games.Single().RecorderInstalled);
        k.UninstallRecorder(_game.Id);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(k.Games.Single().RecorderInstalled);
    }

    [Fact]
    public async Task Uninstall_leaves_files_changed_since_install()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        File.AppendAllText(Path.Combine(_exeDir, "scskiller.ini"), "threads=4\r\n");   // the user edited it
        k.UninstallRecorder(_game.Id);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.True(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    [Fact]
    public async Task Install_refuses_a_foreign_d3d12_dll()
    {
        var foreign = Path.Combine(_exeDir, "d3d12.dll");
        File.WriteAllText(foreign, "some other wrapper");
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        Assert.Equal("some other wrapper", File.ReadAllText(foreign));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    [Fact]
    public async Task Install_refuses_anti_cheat_games()
    {
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    Game FakeGame(string id, string name, string under = "")
    {
        var dir = Path.Combine(_root, under, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".exe"), new byte[64]);
        return new Game(id, name, Store.Other, dir, Path.Combine(dir, name + ".exe"));
    }

    static string Dll(Game g) => Path.Combine(g.InstallDir, "d3d12.dll");

    ScsKiller RecordKiller(Game[] games, Dictionary<string, string>? apis = null, Func<IReadOnlySet<string>>? running = null)
    {
        var k = Killer(new ApiReader(apis ?? []), games: games);
        k.ProcessNames = running ?? (() => new HashSet<string>());
        return k;
    }

    [Fact]
    public void Recorder_effective_is_the_override_else_the_global_setting_and_never_when_incompatible()
    {
        Assert.True(ScsKiller.RecorderEffective(RecorderOverride.Default, true, null));
        Assert.False(ScsKiller.RecorderEffective(RecorderOverride.Default, false, null));
        Assert.True(ScsKiller.RecorderEffective(RecorderOverride.On, false, null));
        Assert.False(ScsKiller.RecorderEffective(RecorderOverride.Off, true, null));
        foreach (var o in Enum.GetValues<RecorderOverride>())
            Assert.False(ScsKiller.RecorderEffective(o, true, ScsKiller.SkipAntiCheat));
    }

    [Fact]
    public async Task Recorder_compatibility_skips_anti_cheat_non_DirectX_12_foreign_dlls_and_protected_folders()
    {
        var ok = FakeGame("test:ok", "Ok");
        var eac = FakeGame("test:eac", "Eac");
        Directory.CreateDirectory(Path.Combine(eac.InstallDir, "EasyAntiCheat"));
        var other = FakeGame("battlenet:other", "Other");   // Battle.net: AntiCheat.Other
        var dx11 = FakeGame("test:dx11", "Dx11");
        var either = FakeGame("test:either", "Either");
        var foreign = FakeGame("test:foreign", "Foreign");
        File.WriteAllText(Dll(foreign), "ReShade");
        var xbox = FakeGame("xbox:store", "Xbox", "WindowsApps");
        var k = RecordKiller([ok, eac, other, dx11, either, foreign, xbox], new() { ["test:dx11"] = "D3D11", ["test:either"] = "D3D11 or D3D12" });
        await k.ScanAsync(default);

        string? Skip(Game g) => k.Games.Single(s => s.Game.Id == g.Id).RecorderSkip;
        Assert.Null(Skip(ok));
        Assert.Null(Skip(either));
        Assert.Equal(ScsKiller.SkipAntiCheat, Skip(eac));
        Assert.Equal(ScsKiller.SkipAntiCheat, Skip(other));
        Assert.Equal(ScsKiller.SkipNotDx12, Skip(dx11));
        Assert.Equal(ScsKiller.SkipForeignDll, Skip(foreign));
        Assert.Equal(ScsKiller.SkipNeedsAdmin, Skip(xbox));

        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Dll(ok)));
        Assert.True(ScsKiller.IsOurProxy(Dll(either)));
        foreach (var g in new[] { eac, other, dx11, xbox }) Assert.False(File.Exists(Dll(g)));
        Assert.Equal("ReShade", File.ReadAllText(Dll(foreign)));
        Assert.Equal([either.Id, ok.Id], k.Games.Where(s => s.RecorderInstalled).Select(s => s.Game.Id).Order());
    }

    // ReShade as its version resource names it; a RenoDX HDR add-on and Luma by the strings their pipeline hooks log
    static readonly byte[] ReShadeDll = [.. Planning.MiddlewarePackTests.Pe("dxgi.dll"), .. Encoding.Unicode.GetBytes("crosire's ReShade post-processing injector for 64-bit")];
    static readonly byte[] RenoDxAddon = [.. Planning.MiddlewarePackTests.Pe(null), .. "mods::shader attached.\0utils::shader attached.\0renodx"u8];
    static readonly byte[] LumaAddon = [.. Planning.MiddlewarePackTests.Pe(null), .. "Luma: trying to load a config from a newer version of the mod"u8];
    static readonly byte[] DlssAddon = [.. Planning.MiddlewarePackTests.Pe(null), .. "utils::dlss_hook attached.\0renodx-dlss5"u8];   // leaves the game's pipelines alone

    /// <summary>ReShade.log of a launch that loaded <paramref name="addon"/> as "RenoDX" and saw it add its constants to a
    /// game root signature (<paramref name="inject"/>) or clone the layout instead.</summary>
    static string RenoLog(string addon, bool inject) =>
        $"12:00:00:000 [1234] | INFO  | Loading add-on from '{addon}' ...\r\n"
        + "12:00:00:001 [1234] | INFO  | Registered add-on \"RenoDX\" v0.1 using ReShade API version 18.\r\n"
        + (inject ? "12:00:01:000 [1234] | INFO  | [RenoDX] mods::shader::OnCreatePipelineLayout(will insert cbuffer 13 at root_index 4 with slot count 32 creating new size of 5 )\r\n"
            : "12:00:01:000 [1234] | INFO  | [RenoDX] mods::shader::OnInitPipelineLayout(Cloning D3D12/Vulkan Layout 0x1 => 0x2: OK)\r\n");

    static void WriteLog(string dir, string text)
    {
        var log = Path.Combine(dir, "ReShade.log");
        File.WriteAllText(log, text);
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddMinutes(1));   // after the add-ons it names
    }

    /// <summary>With "Scan games when SCSKiller starts" off, a start lists the last scan's games as they were and reads none
    /// of them; a game its store lists with another build is read again, and a refresh reads every game that changed.</summary>
    [Fact]
    public async Task With_scan_at_start_off_a_start_lists_the_last_scan()
    {
        var steam = _game with { Store = Store.Steam, Version = "100" };
        var other = FakeGame("test:other", "Other");
        var reader = new StampReader();
        var first = Killer(reader, sources: [new FakeSource([steam], Store.Steam), new FakeSource([other])]);
        first.Settings = first.Settings with { ScanAtStart = false };
        await first.ScanAsync(default, userRequested: true);
        Assert.Equal(2, reader.Detects);

        File.WriteAllBytes(steam.ExePath, new byte[8192]);   // a scan would detect both again
        File.WriteAllBytes(other.ExePath, new byte[128]);
        var store = new FakeSource([steam], Store.Steam);
        var k = Killer(reader, sources: [store, new FakeSource([other])]);   // a restart
        Assert.Equal(2, (await k.ScanAsync(default)).Count);
        Assert.Equal(2, reader.Detects);

        store.Games = [steam with { Version = "101" }];
        await k.ScanAsync(default);
        Assert.Equal(3, reader.Detects);
        Assert.Equal("101", k.Games.Single(s => s.Game.Id == steam.Id).Game.Version);

        await k.ScanAsync(default, userRequested: true);
        Assert.Equal(4, reader.Detects);   // the other game, whose exe changed
    }

    /// <summary>What was read of a DLL for ReShade is kept for the next start: while the file's size, write time, change time
    /// and id stay the same it isn't read again. ReShade copied over it with the same size and write time (an archive's
    /// extraction) changes its change time and is read again.</summary>
    [Fact]
    public void A_dll_read_for_reshade_isnt_read_again_at_the_next_start()
    {
        var g = FakeGame("test:probe", "Probe");
        var dll = Path.Combine(g.InstallDir, "dxgi.dll");
        var other = ReShadeDll.ToArray();
        other[other.AsSpan().IndexOf(Encoding.Unicode.GetBytes("ReShade post"))] ^= 0xFF;   // the same size, not ReShade
        File.WriteAllBytes(dll, other);
        Assert.Null(Core.Games.ReShade.Detect(g));
        var file = Path.Combine(_root, "reshade.json");
        Core.Games.ReShade.SaveProbes(file);
        Core.Games.ReShade.ForgetProbes();   // a new process
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"Flag\":false", "\"Flag\":true"));   // tells the saved probe from a read
        Core.Games.ReShade.LoadProbes(file);
        Assert.NotNull(Core.Games.ReShade.Detect(g));   // the saved probe: the file wasn't read

        var written = File.GetLastWriteTimeUtc(dll);
        CopiedOver(dll, other, written);
        Assert.Null(Core.Games.ReShade.Detect(g));
        CopiedOver(dll, ReShadeDll, written);
        Assert.NotNull(Core.Games.ReShade.Detect(g));
    }

    /// <summary>The file rewritten with <paramref name="bytes"/> and its write time kept, as an archive's extraction does: again
    /// until its change time moved, which NTFS takes from a clock that ticks every few ms (two writes in one tick look the same).</summary>
    static void CopiedOver(string path, byte[] bytes, DateTime written)
    {
        var was = Core.App.KeyFiles.Identity(path);
        for (var t = Stopwatch.StartNew(); Core.App.KeyFiles.Identity(path) == was; Thread.Sleep(1))
        {
            Assert.True(t.Elapsed < HangGuard, "the change time never moved");
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, written);
        }
    }

    /// <summary>The first start after an SCSKiller update lists every game as the last build's scan cache has it, then
    /// detects each again in the background: the list doesn't wait for every game's files to be read.</summary>
    [Fact]
    public async Task A_start_after_an_update_lists_the_cached_games_and_detects_them_after()
    {
        await Killer(new StampReader()).ScanAsync(default);
        var store = new AppStore(Path.Combine(_root, "data"));
        var scan = store.LoadScan();
        foreach (var id in scan.Keys.ToList())
            scan[id] = scan[id] with { Key = string.Join('|', scan[id].Key.Split('|').Select((f, i) => i == 5 ? "0.0.1-older" : f)) };
        store.SaveScan(scan);

        var reader = new StampReader { Api = "D3D11", Gate = new() };
        var k = Killer(reader);
        var states = await k.ScanAsync(default).WaitAsync(TimeSpan.FromSeconds(5));   // not held by the detection
        Assert.Equal("D3D12", states.Single().Engine!.GraphicsApi);   // the older build's
        Assert.True(reader.Entered.Wait(TimeSpan.FromSeconds(10)));
        reader.Gate.Set();
        await Until(() => k.Games.Single().Engine!.GraphicsApi == "D3D11");
        Assert.Equal(1, reader.Detects);
    }

    [Fact]
    public void ReShade_is_known_by_its_bytes_and_its_addons_by_what_they_do_to_pipelines()
    {
        var g = FakeGame("test:hdr", "Hdr");
        ReShadeInstall? Detect() => Core.Games.ReShade.Detect(g);
        (string?, AddonKind)? Mod() => Detect()?.ShaderMod is { } m ? (m.Mod, m.Kind) : null;
        string In(string name) => Path.Combine(g.InstallDir, name);
        void Put(string name, byte[] bytes) => File.WriteAllBytes(In(name), bytes);

        Put("renodx-newgame.addon64", RenoDxAddon);
        Assert.Null(Detect());   // a leftover add-on: no ReShade to load it
        Put("dxgi.dll", Planning.MiddlewarePackTests.Pe("dxgi.dll"));   // another dxgi.dll wrapper
        Assert.Null(Detect());
        File.Delete(In("renodx-newgame.addon64"));
        Put("dxgi.dll", ReShadeDll);
        Assert.Equal((In("dxgi.dll"), null, null, 0), (Detect()!.Dll, Detect()!.Ini, Detect()!.Log, Detect()!.Addons.Count));   // plain ReShade
        Put("ReShade.ini", []);
        Put("renodx-dlss5.addon64", DlssAddon);
        Assert.Equal((In("ReShade.ini"), AddonKind.NotPipeline), (Detect()!.Ini, Detect()!.Addons.Single().Kind));
        Assert.Null(Mod());

        Put("renodx-newgame.addon64", RenoDxAddon);
        Assert.Equal(("RenoDX", AddonKind.ReplacesShaders), Mod());   // not listed, not run yet: not known to change every pipeline
        WriteLog(g.InstallDir, RenoLog(In("renodx-newgame.addon64"), inject: true));
        Assert.Equal(("RenoDX", AddonKind.LayoutInjecting), Mod());
        File.Move(In("renodx-newgame.addon64"), In("renodx-newgame.addon64.off"));
        Assert.Null(Mod());   // ReShade doesn't load it under that name

        Put("renodx-ff7rebirth.addon64", RenoDxAddon);
        File.Delete(In("ReShade.log"));
        Assert.Equal(("RenoDX", AddonKind.LayoutInjecting), Mod());   // listed: known before its first launch
        WriteLog(g.InstallDir, RenoLog(In("renodx-ff7rebirth.addon64"), inject: false));
        Assert.Equal(("RenoDX", AddonKind.ReplacesShaders), Mod());   // the log wins
        File.Delete(In("renodx-ff7rebirth.addon64"));
        Put("renodx-hitmanwoa.addon64", RenoDxAddon);
        File.Delete(In("ReShade.log"));
        Assert.Equal(("RenoDX", AddonKind.ReplacesShaders), Mod());   // listed: passes no injections
        File.Delete(In("renodx-hitmanwoa.addon64"));

        Put("Luma-Game.addon", LumaAddon);
        Assert.Equal(("Luma", AddonKind.ReplacesShaders), Mod());
        File.Move(In("dxgi.dll"), In(ScsKiller.ChainName));   // ReShade chained behind the recorder
        Assert.Equal(In(ScsKiller.ChainName), Detect()!.Dll);
        File.Move(In(ScsKiller.ChainName), In("ReShade64.dll"));   // loaded by an injector
        Assert.Equal(In("ReShade64.dll"), Detect()!.Dll);

        File.Delete(In("ReShade64.dll"));
        Put("renodx-ff7rebirth.addon64", RenoDxAddon);
        Put("dxgi.dll", [.. ReShadeDll, .. "Skipped loading add-on because this build of ReShade has only limited add-on functionality."u8]);
        Assert.Equal((false, null), (Detect()!.LoadsAddons, Detect()!.ShaderMod));   // the standard build loads no add-on files
    }

    /// <summary>The log speaks for the add-on file it names, and only when it was written after that file: a verdict
    /// left by an add-on since removed or replaced doesn't carry over.</summary>
    [Fact]
    public void ReShade_log_verdicts_hold_only_for_the_file_they_name_and_only_when_newer()
    {
        var g = FakeGame("test:log", "Log");
        string In(string name) => Path.Combine(g.InstallDir, name);
        AddonKind Kind(string name) => Core.Games.ReShade.Detect(g)!.Addons.Single(a => a.Path == In(name)).Kind;
        File.WriteAllBytes(In("dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(In("renodx-devkit.addon64"), RenoDxAddon);
        WriteLog(g.InstallDir, RenoLog(In("renodx-newgame.addon64"), inject: true));   // an injecting add-on, since removed
        Assert.Equal(AddonKind.ReplacesShaders, Kind("renodx-devkit.addon64"));

        File.WriteAllBytes(In("renodx-newgame.addon64"), RenoDxAddon);
        WriteLog(g.InstallDir, RenoLog(In("renodx-newgame.addon64"), inject: true));
        Assert.Equal(AddonKind.LayoutInjecting, Kind("renodx-newgame.addon64"));
        File.SetLastWriteTimeUtc(In("renodx-newgame.addon64"), DateTime.UtcNow.AddMinutes(2));   // a new build since that launch
        Assert.Equal(AddonKind.ReplacesShaders, Kind("renodx-newgame.addon64"));   // unlisted: the soft note, not the old log's word

        File.WriteAllBytes(In("renodx-ff7rebirth.addon64"), RenoDxAddon);
        WriteLog(g.InstallDir, RenoLog(In("renodx-ff7rebirth.addon64"), inject: false));
        Assert.Equal(AddonKind.ReplacesShaders, Kind("renodx-ff7rebirth.addon64"));   // the log over the table
        File.SetLastWriteTimeUtc(In("renodx-ff7rebirth.addon64"), DateTime.UtcNow.AddMinutes(2));
        Assert.Equal(AddonKind.LayoutInjecting, Kind("renodx-ff7rebirth.addon64"));   // a newer build: the table again
    }

    [Fact]
    public void ReShade_ini_addon_path_and_disabled_addons_decide_which_addons_load()
    {
        var g = FakeGame("test:ini", "Ini");
        string In(string name) => Path.Combine(g.InstallDir, name);
        (string?, AddonKind)? Mod() => Core.Games.ReShade.Detect(g)?.ShaderMod is { } m ? (m.Mod, m.Kind) : null;
        File.WriteAllBytes(In("dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(In("renodx-ff7rebirth.addon64"), RenoDxAddon);
        File.WriteAllBytes(In("Luma-Game.addon"), LumaAddon);
        void Disabled(string value) => File.WriteAllText(In("ReShade.ini"), $"[GENERAL]\r\nNoDebugInfo=1\r\n\r\n[ADDON]\r\nDisabledAddons={value}\r\n");
        Dictionary<string, bool> Off() => Core.Games.ReShade.Detect(g)!.Addons.ToDictionary(a => Path.GetFileName(a.Path), a => a.Disabled);
        foreach (var entry in new[] { "RenoDX@renodx-ff7rebirth.addon64", "@renodx-ff7rebirth.addon64", "RenoDX", "Generic Depth,RenoDX" })
        {
            Disabled(entry);
            Assert.Equal((true, false), (Off()["renodx-ff7rebirth.addon64"], Off()["Luma-Game.addon"]));
            Assert.Equal(("Luma", AddonKind.ReplacesShaders), Mod());
        }
        foreach (var entry in new[] { "Luma-Game", "@Luma-Game.addon", "Generic Depth", "@renodx-ff7rebirth.addon", "renodx-ff7rebirth" })
        {
            Disabled(entry);   // another add-on's, a built-in's, or not this file's: RenoDX still loads
            Assert.Equal(("RenoDX", AddonKind.LayoutInjecting), Mod());
        }
        File.Delete(In("Luma-Game.addon"));

        Directory.CreateDirectory(In("addons"));
        File.Move(In("renodx-ff7rebirth.addon64"), In(@"addons\renodx-ff7rebirth.addon64"));
        File.WriteAllText(In("ReShade.ini"), "[ADDON]\r\nAddonPath=.\\addons\r\n");
        Assert.Equal(("RenoDX", AddonKind.LayoutInjecting), Mod());
        File.WriteAllBytes(In("renodx-unrealengine.addon64"), RenoDxAddon);   // beside ReShade, but not where it looks
        File.Delete(In(@"addons\renodx-ff7rebirth.addon64"));
        Assert.Null(Mod());

        // the log names the add-on by ReShade's folder joined with AddonPath
        File.WriteAllBytes(In(@"addons\renodx-newgame.addon64"), RenoDxAddon);
        Assert.Equal(("RenoDX", AddonKind.ReplacesShaders), Mod());
        WriteLog(g.InstallDir, RenoLog(g.InstallDir + @"\.\addons\renodx-newgame.addon64", inject: true));
        Assert.Equal(("RenoDX", AddonKind.LayoutInjecting), Mod());
    }

    /// <summary>An add-on that can't be read keeps what its name and the log say, else the soft note.</summary>
    [Fact]
    public void An_addon_that_cant_be_read_falls_back_to_its_name_never_to_not_pipeline()
    {
        var g = FakeGame("test:locked-addon", "Locked");
        string In(string name) => Path.Combine(g.InstallDir, name);
        File.WriteAllBytes(In("dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(In("renodx-ff7rebirth.addon64"), RenoDxAddon);
        File.WriteAllBytes(In("hdr.addon64"), RenoDxAddon);
        using (new FileStream(In("renodx-ff7rebirth.addon64"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(In("hdr.addon64"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var addons = Core.Games.ReShade.Detect(g)!.Addons.ToDictionary(a => Path.GetFileName(a.Path));
            Assert.Equal(("RenoDX", AddonKind.LayoutInjecting), (addons["renodx-ff7rebirth.addon64"].Mod, addons["renodx-ff7rebirth.addon64"].Kind));
            Assert.Equal(("hdr", AddonKind.ReplacesShaders), (addons["hdr.addon64"].Mod, addons["hdr.addon64"].Kind));
        }
    }

    [Fact]
    public void ReShade_beside_the_install_root_counts_when_the_exe_is_deeper()
    {
        File.WriteAllBytes(Path.Combine(_game.InstallDir, "dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(Path.Combine(_game.InstallDir, "renodx-unrealengine.addon64"), RenoDxAddon);
        var r = Core.Games.ReShade.Detect(_game)!;
        Assert.Equal((AddonKind.LayoutInjecting, false, true, null), (r.ShaderMod!.Kind, r.Layered, r.Blocks, r.Fingerprint));
    }

    [Fact]
    public void The_RenoDX_table_classifies_release_file_names()
    {
        AddonKind? Kind(string file) => Core.Games.ReShade.RenoDxFolder(file) is { } f && Core.Games.ReShade.RenoDxTable.TryGetValue(f, out var k) ? k : null;
        Assert.Equal(AddonKind.LayoutInjecting, Kind("renodx-ff7rebirth.addon64"));
        Assert.Equal(AddonKind.LayoutInjecting, Kind("renodx-unrealengine.addon64"));   // the generic Unreal add-on
        Assert.Equal(AddonKind.LayoutInjecting, Kind("renodx-ue-extended.addon64"));   // RenoDX extended's
        Assert.Equal(AddonKind.LayoutInjecting, Kind("renodx-thewitcher3.addon64"));   // its layout cloning is commented out
        Assert.Equal(AddonKind.LayoutInjecting, Kind("RenoDX-CP2077.addon64"));
        Assert.Equal(AddonKind.ReplacesShaders, Kind("renodx-hitmanwoa.addon64"));
        Assert.Equal(AddonKind.ReplacesShaders, Kind("renodx-devkit.addon64"));
        Assert.Null(Kind("renodx-dlss5.addon64"));
        Assert.Null(Kind("Luma-Game.addon"));
    }

    /// <summary>RenoDX next to ReShade, adding to the game's root signatures: the game still compiles and records. The
    /// compile runs through a copy of the layer in its work folder (ReShade as dxgi.dll, the add-ons that change pipelines,
    /// ReShade.ini without the keys that point it elsewhere), and the warm remembers it: the add-on changing or going makes
    /// the game Stale. ReShade as d3d12.dll is copied the same way; the game page says the recorder needs chaining then.</summary>
    [Fact]
    public async Task A_shader_mod_compiles_through_a_copy_of_the_layer_and_a_change_to_it_makes_the_warm_stale()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, "dxgi.dll"), ReShadeDll);
        Directory.CreateDirectory(Path.Combine(_exeDir, "addons"));   // ReShade.ini's AddonPath: the add-ons are there
        File.WriteAllBytes(Path.Combine(_exeDir, "addons", "renodx-dlss5.addon64"), DlssAddon);
        var addon = Path.Combine(_exeDir, "addons", "renodx-ff7rebirth.addon64");
        File.WriteAllBytes(addon, RenoDxAddon);
        File.WriteAllLines(Path.Combine(_exeDir, "ReShade.ini"), ["[ADDON]", "AddonPath=addons", "DisabledAddons=", "[INSTALL]", @"BasePath=C:\elsewhere", "[RenoDX]", "ToneMapType=3"]);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, "RenoDX", false, null), (s.Status, s.ShaderMod, s.ShaderModAsD3D12, s.RecorderSkip));
        Assert.Contains("compiles run through a copy of ReShade and RenoDX", ScsKiller.ShaderModNote(s));
        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));   // the recorder records under ReShade

        var work = Path.Combine(_root, "layer-work");
        var (dir, fingerprint) = k.LayerFor(_game, work)!.Value;
        Assert.Equal(Path.Combine(work, "layer"), dir);
        Assert.Equal(["ReShade.ini", "dxgi.dll", "renodx-ff7rebirth.addon64"], Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(ReShadeDll, File.ReadAllBytes(Path.Combine(dir, "dxgi.dll")));
        Assert.Equal(["[ADDON]", "DisabledAddons=", "[INSTALL]", "[RenoDX]", "ToneMapType=3"], File.ReadAllLines(Path.Combine(dir, "ReShade.ini")));
        // headers as ReShade reads them: text after ']' ignored, and without ']' the rest of the line
        File.WriteAllLines(Path.Combine(_exeDir, "ReShade.ini"), ["[ADDON] ; x", "AddonPath=addons", "[INSTALL", @"BasePath=C:\elsewhere", "[GENERAL]", "AddonPath=kept"]);
        dir = k.LayerFor(_game, work)!.Value.Dir;
        Assert.Equal(["[ADDON] ; x", "[INSTALL", "[GENERAL]", "AddonPath=kept"], File.ReadAllLines(Path.Combine(dir, "ReShade.ini")));
        File.WriteAllLines(Path.Combine(_exeDir, "ReShade.ini"), ["[ADDON]", "AddonPath=addons", "DisabledAddons=", "[INSTALL]", @"BasePath=C:\elsewhere", "[RenoDX]", "ToneMapType=3"]);

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Equal(fingerprint, k.Store.LoadGame(_game.Id).WarmedLayer);

        File.WriteAllBytes(addon, [.. RenoDxAddon, .. "a newer build"u8]);
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.Stale, "the HDR mod changed since the compile"), (k.Games.Single().Status, k.Games.Single().StatusReason));
        File.Delete(addon);
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.Stale, "the HDR mod the last compile ran through is gone", null), (k.Games.Single().Status, k.Games.Single().StatusReason, k.Games.Single().ShaderMod));

        File.WriteAllBytes(addon, RenoDxAddon);
        File.Move(Path.Combine(_exeDir, "dxgi.dll"), Path.Combine(_exeDir, "ReShade64.dll"));
        await k.ScanAsync(default);
        Assert.Equal((false, true, GameStatus.Unsupported), (k.Games.Single().ShaderModAsD3D12, k.Games.Single().ShaderModBlocks, k.Games.Single().Status));
        k.ReconcileRecorders();
        File.Delete(Path.Combine(_exeDir, "d3d12.dll"));
        File.Move(Path.Combine(_exeDir, "ReShade64.dll"), Path.Combine(_exeDir, "d3d12.dll"));
        await k.ScanAsync(default);
        s = k.Games.Single();
        Assert.Equal(("RenoDX", true, ScsKiller.SkipForeignDll), (s.ShaderMod, s.ShaderModAsD3D12, s.RecorderSkip));
        Assert.Contains("Record alongside ReShade", ScsKiller.ShaderModNote(s));
        Assert.Equal(ReShadeDll, File.ReadAllBytes(Path.Combine(k.LayerFor(_game, work)!.Value.Dir, "dxgi.dll")));
    }

    /// <summary>OptiScaler loading ReShade64.dll (LoadReshade=true) with a RenoDX add-on: the game compiles through a copy of
    /// the whole chain (OptiScaler as dxgi.dll, its ini without paths or update check, the libraries it loads, ReShade64.dll
    /// and the add-ons) and records beside OptiScaler. A change to OptiScaler or its ini makes the warm stale; turning
    /// LoadReshade off blocks the game.</summary>
    [Fact]
    public async Task ReShade_loaded_by_optiscaler_compiles_through_the_whole_chain_and_records()
    {
        var opti = Planning.MiddlewarePackTests.Pe("OptiScaler.dll");
        File.WriteAllBytes(Path.Combine(_exeDir, "winmm.dll"), opti);
        File.WriteAllBytes(_game.ExePath, Planning.MiddlewarePackTests.PeImporting("WINMM.dll"));
        string[] optiIni = ["[Plugins]", "LoadReshade=true", @"Path=C:\elsewhere", "[Libraries]", "OptiDllPath=Mods", @"FfxDx12Path=C:\ffx",
            "[Log]", "LogToFile=true", @"LogFileName=C:\logs\opti.log", "[Hotfix]", "CheckForUpdate=true", "[Upscalers]", "Dx12Upscaler=fsr31"];
        File.WriteAllLines(Path.Combine(_exeDir, "OptiScaler.ini"), optiIni);
        Directory.CreateDirectory(Path.Combine(_exeDir, "Mods"));   // OptiDllPath: its libraries come from there first
        byte[] Lib(string what) => Planning.MiddlewarePackTests.Pe(null, Encoding.UTF8.GetBytes(what));
        File.WriteAllBytes(Path.Combine(_exeDir, "Mods", "amd_fidelityfx_upscaler_dx12.dll"), Lib("ffx in Mods"));
        File.WriteAllBytes(Path.Combine(_exeDir, "amd_fidelityfx_upscaler_dx12.dll"), Lib("ffx beside the exe"));
        File.WriteAllBytes(Path.Combine(_exeDir, "libxess.dll"), Lib("xess"));
        File.WriteAllBytes(Path.Combine(_exeDir, "dlssg_to_fsr3_amd_is_better.dll"), "not OptiScaler's to load"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_exeDir, "ReShade64.dll"), ReShadeDll);
        File.WriteAllLines(Path.Combine(_exeDir, "ReShade.ini"), ["[INSTALL]", "HookStreamline=1"]);
        File.WriteAllBytes(Path.Combine(_exeDir, "renodx-ue-extended.addon64"), RenoDxAddon);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, "RenoDX", false, true, null), (s.Status, s.ShaderMod, s.ShaderModBlocks, s.ShaderModLayer, s.RecorderSkip));
        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));

        var dir = k.LayerFor(_game, Path.Combine(_root, "layer-work"))!.Value.Dir;
        Assert.Equal(["OptiScaler.ini", "ReShade.ini", "ReShade64.dll", "amd_fidelityfx_upscaler_dx12.dll", "dxgi.dll", "libxess.dll", "renodx-ue-extended.addon64"],
            Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(opti, File.ReadAllBytes(Path.Combine(dir, "dxgi.dll")));
        Assert.Equal(ReShadeDll, File.ReadAllBytes(Path.Combine(dir, "ReShade64.dll")));
        Assert.Equal(Lib("ffx in Mods"), File.ReadAllBytes(Path.Combine(dir, "amd_fidelityfx_upscaler_dx12.dll")));
        Assert.Equal(["[Plugins]", "LoadReshade=true", "[Libraries]", "[Log]", "LogToFile=true", "[Hotfix]", "CheckForUpdate=false", "[Upscalers]", "Dx12Upscaler=fsr31"],
            File.ReadAllLines(Path.Combine(dir, "OptiScaler.ini")));
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.NotNull(k.Store.LoadGame(_game.Id).WarmedLayer);

        File.WriteAllLines(Path.Combine(_exeDir, "OptiScaler.ini"), [.. optiIni, "[Performance]", "MipmapBiasOverride=-0.5"]);
        File.SetLastWriteTimeUtc(Path.Combine(_exeDir, "OptiScaler.ini"), DateTime.UtcNow.AddMinutes(1));
        await k.ScanAsync(default);
        Assert.Equal(GameStatus.Stale, k.Games.Single().Status);   // OptiScaler's settings may change root signatures

        File.WriteAllLines(Path.Combine(_exeDir, "OptiScaler.ini"), ["[Plugins]", "LoadReshade=auto"]);
        await k.ScanAsync(default);
        s = k.Games.Single();
        Assert.Equal((GameStatus.Unsupported, ScsKiller.ShaderModReason("RenoDX", LayerBlock.OptiScalerOff), ScsKiller.SkipShaderMod),
            (s.Status, s.StatusReason, s.RecorderSkip));
    }

    /// <summary>An Xbox app game (Beast of Reincarnation): ReShade as dxgi.dll with RenoDX in the package root, the Shipping
    /// exe under Binaries\WinGDK. A packaged app's DLL search looks in its package root before the exe's folder, so the
    /// game loads it: the compile runs through a copy of it and the recorder goes beside the exe. The package root wins over
    /// the exe's folder under a name the game loads; under another name it still blocks. Any d3d12.dll there loads instead of
    /// the recorder: the recorder comes out and says why. The same layout from another store, its manifests and all, is an
    /// install root above the exe and blocks.</summary>
    [Fact]
    public async Task ReShade_in_an_xbox_package_root_compiles_through_the_mod()
    {
        var content = Path.Combine(_root, "Beast of Reincarnation", "Content");
        var exeDir = Path.Combine(content, "BeastOfReincarnation", "Binaries", "WinGDK");
        Directory.CreateDirectory(exeDir);
        var exe = Path.Combine(exeDir, "BeastOfReincarnation-WinGDK-Shipping.exe");
        File.WriteAllBytes(exe, new byte[4096]);
        File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"), "<Game configVersion=\"1\"><Identity Name=\"P.Beast\" Version=\"1.0.0.0\" /></Game>");
        File.WriteAllText(Path.Combine(content, "appxmanifest.xml"), "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" />");
        File.WriteAllBytes(Path.Combine(content, "gamelaunchhelper.exe"), new byte[100]);
        File.WriteAllBytes(Path.Combine(content, "resources.pri"), new byte[10]);
        File.WriteAllBytes(Path.Combine(content, "dxgi.dll"), ReShadeDll);
        File.WriteAllLines(Path.Combine(content, "ReShade.ini"), ["[ADDON]", "DisabledAddons=", "[RenoDX]", "ToneMapType=3"]);
        File.WriteAllText(Path.Combine(content, "ReShadePreset.ini"), "");
        var addon = Path.Combine(content, "renodx-ue-extended.addon64");
        File.WriteAllBytes(addon, RenoDxAddon);
        File.WriteAllText(Path.Combine(content, "ReShade.log"), RenoLog(addon, inject: true));
        var game = new Game("xbox:P.Beast_8wekyb3d8bbwe", "Beast of Reincarnation", Store.Xbox, content, exe);
        var k = Killer(new FakeReader(Unreal), game: game);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, "RenoDX", false, true, null), (s.Status, s.ShaderMod, s.ShaderModBlocks, s.ShaderModLayer, s.RecorderSkip));
        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(exeDir, "d3d12.dll")));

        var (dir, fingerprint) = k.LayerFor(game, Path.Combine(_root, "layer-work"))!.Value;
        Assert.Equal(["ReShade.ini", "dxgi.dll", "renodx-ue-extended.addon64"], Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(ReShadeDll, File.ReadAllBytes(Path.Combine(dir, "dxgi.dll")));
        k.Enqueue(game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Equal(fingerprint, k.Store.LoadGame(game.Id).WarmedLayer);

        File.WriteAllBytes(Path.Combine(content, "d3d12.dll"), Planning.MiddlewarePackTests.Pe("d3d12.dll"));
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        s = k.Games.Single();
        Assert.Equal((ScsKiller.SkipPackageD3D12, false, false), (s.RecorderSkip, s.RecorderEffective, s.RecorderInstalled));
        Assert.False(File.Exists(Path.Combine(exeDir, "d3d12.dll")));
        File.Delete(Path.Combine(content, "d3d12.dll"));
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(exeDir, "d3d12.dll")));

        byte[] other = [.. ReShadeDll, .. "another build"u8];
        File.WriteAllBytes(Path.Combine(exeDir, "dxgi.dll"), other);
        Assert.Equal(Path.Combine(content, "dxgi.dll"), Core.Games.ReShade.Detect(game)!.Dll);
        File.Move(Path.Combine(content, "dxgi.dll"), Path.Combine(content, "ReShade64.dll"));   // loaded only if something injects it
        Assert.Equal(Path.Combine(exeDir, "dxgi.dll"), Core.Games.ReShade.Detect(game)!.Dll);
        File.Delete(Path.Combine(exeDir, "dxgi.dll"));
        var r = Core.Games.ReShade.Detect(game)!;
        Assert.Equal((LayerBlock.UnloadedName, true), (r.Block, r.Blocks));

        File.Move(Path.Combine(content, "ReShade64.dll"), Path.Combine(content, "dxgi.dll"));
        r = Core.Games.ReShade.Detect(game with { Id = "steam:1", Store = Store.Steam })!;
        Assert.Equal((LayerBlock.NotBesideExe, true), (r.Block, r.Blocks));
    }

    /// <summary>A warm that didn't run through the layer (every warm from a build before compiles did) with the mod there all
    /// along: Stale, without saying the mod is new.</summary>
    [Fact]
    public async Task A_warm_from_before_the_layer_is_stale_without_calling_the_mod_new()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, "dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(Path.Combine(_exeDir, "renodx-hitmanwoa.addon64"), RenoDxAddon);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        var rec = k.Store.LoadGame(_game.Id);
        rec.WarmedLayer = null;
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.Stale, ScsKiller.NotThroughLayerReason), (k.Games.Single().Status, k.Games.Single().StatusReason));
    }

    /// <summary>A compile stopped for a running game names the process holding the game's exe name and its path, and
    /// leaves out SCSKiller's own staged warm of that name.</summary>
    [Fact]
    public async Task A_compile_stopped_for_the_game_names_the_process_that_holds_its_exe()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        var exe = Path.GetFileName(_game.ExePath);
        var running = true;
        k.RunningGameExes = () => running ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exe } : [];
        var staged = Path.Combine(k.Store.GameDir(_game.Id), "work", "stage-1", "Fake", exe);
        k.Processes = _ => [(4242, 1, exe), (4343, 1, exe)];
        k.ProcessPath = pid => pid == 4242 ? @"D:\Old copy\" + exe : staged;
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await Until(() => k.Queue.Single().Stage == QueueStage.Paused);
        Assert.Equal($@"stopped while {_game.Name} is running: continues when it exits (process 4242: D:\Old copy\{exe})", k.Queue.Single().Note);
        running = false;
        await warmer.WhenStarted();
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A compile that waited for the game to exit copies the layer as its warm starts: the mod may have come with it.</summary>
    [Fact]
    public async Task A_shader_mod_installed_while_a_compile_waits_goes_into_the_warm()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Fake-Win64-Shipping.exe" };
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); };
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await Until(() => k.Queue.Single().Stage == QueueStage.Paused);
        File.WriteAllBytes(Path.Combine(_exeDir, "dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(Path.Combine(_exeDir, "renodx-ff7rebirth.addon64"), RenoDxAddon);
        lock (running) running.Clear();
        await warmer.WhenStarted(1);
        Assert.True(File.Exists(Path.Combine(k.Store.GameDir(_game.Id), "work", "layer", "renodx-ff7rebirth.addon64")));
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>ReShade only in the install root above the exe: the game may not load it, so no copy of it goes into a
    /// compile. A RenoDX add-on there that adds to every root signature blocks the game: not compiled, its recorder out at
    /// once without the app's reconcile, until the add-on goes. One that replaces some shaders only adds a note.</summary>
    [Fact]
    public async Task A_shader_mod_only_in_the_install_root_blocks_compiling_and_recording_until_it_is_removed()
    {
        File.WriteAllBytes(Path.Combine(_game.InstallDir, "dxgi.dll"), ReShadeDll);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));

        var addon = Path.Combine(_game.InstallDir, "renodx-hitmanwoa.addon64");
        File.WriteAllBytes(addon, RenoDxAddon);
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, "RenoDX", false, false, null), (s.Status, s.ShaderMod, s.ShaderModBlocks, s.ShaderModLayer, s.RecorderSkip));
        Assert.Equal("RenoDX replaces some shaders: those few pipelines aren't precompiled", ScsKiller.ShaderModNote(s));
        Assert.Null(k.LayerFor(_game, Path.Combine(_root, "layer-work")));
        File.Delete(addon);

        addon = Path.Combine(_game.InstallDir, "renodx-ff7rebirth.addon64");
        File.WriteAllBytes(addon, RenoDxAddon);
        await k.ScanAsync(default);   // no reconcile, as the command line scans
        s = k.Games.Single();
        Assert.Equal((GameStatus.Unsupported, "RenoDX", true, ScsKiller.ShaderModReason("RenoDX", LayerBlock.NotBesideExe), ScsKiller.SkipShaderMod),
            (s.Status, s.ShaderMod, s.ShaderModBlocks, s.StatusReason, s.RecorderSkip));
        Assert.Equal("RenoDX changes all its pipelines", Format.ShortNote(s));
        Assert.False(s.RecorderInstalled);   // the state the removal left
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Failed, "not ready: " + ScsKiller.ShaderModReason("RenoDX", LayerBlock.NotBesideExe)), (k.Queue.Single().Stage, k.Queue.Single().Error));
        Assert.Null(k.Games.Single().WarmedAt);

        File.Delete(addon);
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.Ready, null, null), (k.Games.Single().Status, k.Games.Single().ShaderMod, k.Games.Single().RecorderSkip));
    }

    /// <summary>A compile that waited for the game to exit checks again before its warm starts: a blocking mod may have come.</summary>
    [Fact]
    public async Task A_blocking_shader_mod_installed_while_a_compile_waits_stops_it_before_the_warm()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Fake-Win64-Shipping.exe" };
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); };
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await Until(() => k.Queue.Single().Stage == QueueStage.Paused);
        File.WriteAllBytes(Path.Combine(_game.InstallDir, "dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(Path.Combine(_game.InstallDir, "renodx-ff7rebirth.addon64"), RenoDxAddon);
        lock (running) running.Clear();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(warmer.Started);
        Assert.Equal((QueueStage.Failed, "not ready: " + ScsKiller.ShaderModReason("RenoDX", LayerBlock.NotBesideExe)), (k.Queue.Single().Stage, k.Queue.Single().Error));
    }

    [Fact]
    public async Task Recorder_skips_a_folder_it_cannot_write_to()
    {
        var locked = FakeGame("test:locked", "Locked");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(sid,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData,
            System.Security.AccessControl.AccessControlType.Deny);
        var info = new DirectoryInfo(locked.InstallDir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            var k = RecordKiller([locked]);
            await k.ScanAsync(default);
            k.ReconcileRecorders();
            var s = k.Games.Single();
            Assert.False(File.Exists(Dll(locked)));
            Assert.Equal(ScsKiller.SkipNeedsAdmin, s.RecorderSkip);
            Assert.False(s.RecorderEffective);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task Reconcile_installs_and_removes_exactly_the_effective_set()
    {
        var byDefault = FakeGame("test:default", "Default");
        var off = FakeGame("test:off", "Off");
        var on = FakeGame("test:on", "On");
        var eac = FakeGame("test:eac", "Eac");
        Directory.CreateDirectory(Path.Combine(eac.InstallDir, "EasyAntiCheat"));
        var k = RecordKiller([byDefault, off, on, eac]);
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        Assert.True(k.Settings.RecordAllGames);
        Assert.Equal([byDefault.Id, off.Id, on.Id], k.Games.Where(s => s.RecorderInstalled).Select(s => s.Game.Id).Order());
        var recorded = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        using (var f = File.Create(Path.Combine(byDefault.InstallDir, "scskiller.db"))) PsoDb.Write(f, recorded.Tag, recorded.Payload);   // the game ran with it

        k.SetRecorderOverride(off.Id, RecorderOverride.Off);
        k.SetRecorderOverride(on.Id, RecorderOverride.On);
        Assert.False(File.Exists(Dll(off)));
        Assert.False(File.Exists(Path.Combine(off.InstallDir, "scskiller.ini")));

        k.Settings = k.Settings with { RecordAllGames = false };   // removed everywhere but the On override
        await Until(() => !k.Games.Single(s => s.Game.Id == byDefault.Id).RecorderInstalled);
        Assert.True(ScsKiller.IsOurProxy(Dll(on)));
        Assert.False(File.Exists(Path.Combine(byDefault.InstallDir, "scskiller.ini")));
        Assert.False(File.Exists(Path.Combine(byDefault.InstallDir, "scskiller.db")));   // deleted once imported
        Assert.Equal([recorded.Key], PsoDb.Read(Path.Combine(k.Store.GameDir(byDefault.Id), "recording.db")).Select(r => r.Key));
        Assert.Equal([on.Id], k.Games.Where(s => s.RecorderInstalled).Select(s => s.Game.Id));

        k.SetRecorderOverride(off.Id, RecorderOverride.Default);   // "Use default" while the global is off: stays out
        k.SetRecorderOverride(on.Id, RecorderOverride.Default);
        Assert.DoesNotContain(k.Games, s => s.RecorderInstalled);
        Assert.All(k.Games, s => Assert.Equal(RecorderOverride.Default, s.RecorderOverride));
        Assert.False(File.Exists(Dll(eac)));

        k.Settings = k.Settings with { RecordAllGames = true };
        await Until(() => k.Games.Count(s => s.RecorderInstalled) == 3);
        Assert.Contains("recorder installed", SharedLog(Path.Combine(k.Store.DataDir, "recorders.log")));
    }

    /// <summary>What a recorder left in a folder that has none of ours (an earlier version's recorder off, a file that was
    /// locked) goes at the next scan, by name only, once the game isn't running; a folder with the recorder keeps its files.</summary>
    [Fact]
    public async Task A_scan_deletes_the_data_files_a_removed_recorder_left_and_nothing_else()
    {
        var off = FakeGame("test:off", "Off");
        var on = FakeGame("test:on", "On");
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = RecordKiller([off, on], running: () => running);
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        k.SetRecorderOverride(off.Id, RecorderOverride.Off);
        Assert.False(File.Exists(Dll(off)));

        string[] data = ["scskiller.db", "scskiller_creates.csv", "scskiller.log", FrameLog.FileName, Recordings.KeysFile];
        var recorded = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        foreach (var g in new[] { off, on })
        {
            foreach (var f in data.Skip(1)) File.WriteAllText(Path.Combine(g.InstallDir, f), f);
            using (var db = File.Create(Path.Combine(g.InstallDir, "scskiller.db"))) PsoDb.Write(db, recorded.Tag, recorded.Payload);
            File.WriteAllText(Path.Combine(g.InstallDir, "game.ini"), "the game's own");
        }

        running.Add("Off");
        await k.ScanAsync(default);
        // a running game's folder is left (the scan's import still takes the db and rewrites the keys file, as before)
        Assert.All(data[1..4], f => Assert.True(File.Exists(Path.Combine(off.InstallDir, f))));

        running.Clear();
        await k.ScanAsync(default);
        Assert.Equal(["Off.exe", "game.ini"], Directory.GetFiles(off.InstallDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal([recorded.Key], PsoDb.Read(Path.Combine(k.Store.GameDir(off.Id), "recording.db")).Select(r => r.Key));
        Assert.All(data[..4], f => Assert.True(File.Exists(Path.Combine(on.InstallDir, f))));   // the keys file is the import's
        Assert.True(ScsKiller.IsOurProxy(Dll(on)));
    }

    [Fact]
    public async Task Reconcile_never_touches_a_foreign_d3d12_dll()
    {
        var g = FakeGame("test:mod", "Mod");
        File.WriteAllText(Dll(g), "OptiScaler");
        var k = RecordKiller([g]);
        await k.ScanAsync(default);
        k.SetRecorderOverride(g.Id, RecorderOverride.On);
        Assert.Equal("OptiScaler", File.ReadAllText(Dll(g)));
        Assert.Equal(ScsKiller.SkipForeignDll, k.Games.Single().RecorderSkip);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(g.Id));
        k.SetRecorderOverride(g.Id, RecorderOverride.Off);
        k.Settings = k.Settings with { RecordAllGames = false };
        k.ReconcileRecorders();
        Assert.Equal("OptiScaler", File.ReadAllText(Dll(g)));
        Assert.False(File.Exists(Path.Combine(g.InstallDir, "scskiller.ini")));
    }

    static string Sha256Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Record_alongside_a_mod_chains_to_it_and_removal_puts_it_back_byte_for_byte()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());   // a wrapper: not ours, no marker
        File.WriteAllBytes(mod, bytes);
        var sha = Sha256Of(mod);
        var chained = Path.Combine(_exeDir, ScsKiller.ChainName);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        Assert.Equal(ScsKiller.SkipForeignDll, k.Games.Single().RecorderSkip);   // opt-in: nothing touched until asked
        Assert.Equal("d3d12.dll", k.RecorderMod(_game.Id));

        k.SetRecordAlongsideMod(_game.Id, true);
        k.InstallRecorder(_game.Id);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(mod));
        Assert.Equal(bytes, File.ReadAllBytes(chained));
        Assert.Contains($"next={ScsKiller.ChainName}", File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.Equal(new ChainedDll(ScsKiller.ChainName, sha), k.Store.LoadGame(_game.Id).RecorderChained);
        var s = k.Games.Single();
        Assert.True(s.RecorderInstalled);
        Assert.Null(s.RecorderSkip);
        Assert.Equal("d3d12.dll", k.RecorderMod(_game.Id));   // still named: the chained one

        k.UninstallRecorder(_game.Id);
        Assert.Equal(sha, Sha256Of(mod));
        Assert.False(File.Exists(chained));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderChained);

        // on again, then the choice turned off: the recorder goes and the mod is back
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(chained));
        k.SetRecordAlongsideMod(_game.Id, false);
        Assert.Equal(sha, Sha256Of(mod));
        Assert.False(File.Exists(chained));
        Assert.Equal(ScsKiller.SkipForeignDll, k.Games.Single().RecorderSkip);
    }

    string RecordersLog() => SharedLog(Path.Combine(_root, "data", "recorders.log"));

    // as the app appends: a read that doesn't share writing would make the app drop the line it is writing
    static string SharedLog(string path)
    {
        try
        {
            using var r = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return r.ReadToEnd();
        }
        catch (FileNotFoundException) { return ""; }
    }

    /// <summary>The uninstall takes the recorders out of the games even when removing the scheduled task and the sign-in
    /// entry both throw, and logs both failures.</summary>
    [Fact]
    public async Task Uninstall_removes_the_recorders_even_when_the_task_and_startup_steps_fail()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        var log = new List<string>();
        HookSteps.Uninstall(k.Store, log.Add, () => throw new UnauthorizedAccessException("task"), () => throw new IOException("startup"), new HashSet<string>());
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderExe);
        Assert.Equal(3, log.Count);
        Assert.StartsWith("Uninstalling: step 1 of 3 failed", log[1]);
        Assert.StartsWith("Uninstalling: step 2 of 3 failed", log[2]);
    }

    [Fact]
    public async Task Uninstall_hook_removes_our_recorder_and_its_data_files_after_keeping_the_recording()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        // the app's files too, the temps an interrupted write leaves included
        foreach (var f in new[] { "scskiller.log", "scskiller_creates.csv", FrameLog.FileName }.Concat(Recordings.AppFiles)) File.WriteAllText(Path.Combine(_exeDir, f), f);
        using (var db = File.Create(Path.Combine(_exeDir, "scskiller.db"))) PsoDb.Write(db, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        var recorded = PsoDb.Read(Path.Combine(_exeDir, "scskiller.db")).Select(r => r.Key).ToList();
        File.WriteAllText(Path.Combine(_exeDir, "game.ini"), "the game's own");

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.Equal(["Fake-Win64-Shipping.exe", "game.ini"], Directory.GetFiles(_exeDir).Select(Path.GetFileName).Order());
        Assert.Equal(recorded, PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Select(r => r.Key));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Empty(rec.RecorderFiles);
        Assert.Null(rec.RecorderExe);
        Assert.Contains($"recorder removed from {_exeDir}", RecordersLog());
    }

    [Fact]
    public async Task Uninstall_hook_keeps_a_foreign_dll_even_with_our_hash_on_record()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());   // a wrapper the user put over ours
        File.WriteAllBytes(dll, bytes);
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderFiles["d3d12.dll"] = Sha256Of(dll);   // a state that went wrong: the marker still decides
        k.Store.SaveGame(_game.Id, rec);

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.Equal(bytes, File.ReadAllBytes(dll));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    [Fact]
    public async Task Uninstall_hook_leaves_a_running_game_and_merges_a_recording_not_imported_yet()
    {
        var running = FakeGame("test:running", "Running");
        var k = RecordKiller([_game, running]);
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        k.InstallRecorder(running.Id);
        static PsoDb.Rec Pso(char c) => new('C', PsoDb.Compute(PsoDb.Zero, new string(c, 40)));
        var store = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        using (var f = File.Create(store)) PsoDb.Write(f, 'C', Pso('a').Payload);   // imported before compact recordings
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db"))) { PsoDb.Write(f, 'C', Pso('b').Payload); PsoDb.Write(f, 'C', Pso('a').Payload); }
        var runningDb = Path.Combine(running.InstallDir, "scskiller.db");
        File.WriteAllText(runningDb, "its recorder is writing it");

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running" });
        Assert.True(ScsKiller.IsOurProxy(Dll(running)));
        Assert.True(File.Exists(Path.Combine(running.InstallDir, "scskiller.ini")));
        Assert.Equal(running.ExePath, k.Store.LoadGame(running.Id).RecorderExe);
        Assert.Equal("its recorder is writing it", File.ReadAllText(runningDb));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.db")));
        Assert.Equal([Pso('a').Key, Pso('b').Key], PsoDb.Read(store).Select(r => r.Key));
        Assert.True(PsoDb.IsCompact(store));
        Assert.Contains($"{running.ExePath} is running", RecordersLog());
    }

    [Fact]
    public async Task Uninstall_hook_puts_a_chained_mod_back()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(mod, bytes);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.SetRecordAlongsideMod(_game.Id, true);
        k.InstallRecorder(_game.Id);
        Assert.True(ScsKiller.IsOurProxy(mod));

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.Equal(bytes, File.ReadAllBytes(mod));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderChained);
    }

    [Fact]
    public async Task Reconcile_records_where_a_recorder_is_for_uninstall()
    {
        File.Copy(_proxy, Path.Combine(_exeDir, "d3d12.dll"));   // installed by hand or by a build that didn't record its exe
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.Equal(_game.ExePath, k.Store.LoadGame(_game.Id).RecorderExe);

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>The proxy's side of the chain, on WARP (no GPU cache): `selftest chain` creates a device and a compute PSO
    /// through the built proxy with next= naming fakenext.dll (a mod's d3d12.dll that exports only D3D12CreateDevice). The
    /// device comes through the mod, the other exports from the system dll, and the PSO is recorded; a next= that doesn't
    /// load, isn't a bare file name, or is the proxy itself, leaves the game running on the system dll. With a mod that replaces shaders
    /// (fakenext's wrapper device swapping a graphics PSO's PS), both the game's desc and the one the mod passed on to the
    /// device underneath are recorded, the replaced PS with its bytes (a shader in no file of the install).</summary>
    [Fact]
    public void The_proxy_chains_to_the_renamed_mod_and_records_through_it()
    {
        UseProxyLedger();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SCSKiller.slnx"))) root = root.Parent;
        var bin = root == null ? null : Path.Combine(root.FullName, "proxy", "build", "Release");
        if (bin == null || !File.Exists(Path.Combine(bin, "fakenext.dll"))) return;   // this checkout's proxy isn't built
        var dir = Path.Combine(_root, "chain");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.Copy(Path.Combine(bin, "fakenext.dll"), Path.Combine(dir, ScsKiller.ChainName));
        (int Exit, string Out) Run(string next, int seed, string mode = "")
        {
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), $"[scskiller]\r\nmode=record\r\nnext={next}\r\n");
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"chain {seed} {mode}") { RedirectStandardOutput = true })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o);
        }
        var seed = Environment.TickCount % 100000;
        var chained = Run(ScsKiller.ChainName, seed);
        Assert.Equal(0, chained.Exit);
        Assert.Contains("next-calls 1", chained.Out);
        Assert.Contains("created 0x00000000", chained.Out);
        Assert.Single(PsoDb.Read(Path.Combine(dir, "scskiller.db")), r => r.Tag == 'C');

        foreach (var (next, why) in new[] { ("missing.dll", "failed to load"), (@"..\" + ScsKiller.ChainName, "not a file name"), ("d3d12.dll", "is this dll") })
        {
            var r = Run(next, ++seed);
            Assert.Equal(0, r.Exit);
            Assert.Contains("next-calls -1", r.Out);
            Assert.Contains(why, File.ReadAllText(Path.Combine(dir, "scskiller.log")));
        }
        Assert.Equal(4, PsoDb.Read(Path.Combine(dir, "scskiller.db")).Count(r => r.Tag == 'C'));

        var swapped = Run(ScsKiller.ChainName, ++seed, "swap");
        Assert.Equal(0, swapped.Exit);
        Assert.Contains("created 0x00000000", swapped.Out);
        var db = PsoDb.Read(Path.Combine(dir, "scskiller.db")).ToList();
        var gfx = db.Where(r => r.Tag == 'G').ToList();
        Assert.Equal(2, gfx.Count);
        var ps = gfx.Select(g => PsoDb.Parse(g).Stages[(int)Stage.Pixel]).ToList();
        Assert.NotEqual(ps[0], ps[1]);
        Assert.Equal(gfx[0].Payload[..40], gfx[1].Payload[..40]);   // root signature and VS
        Assert.Equal(gfx[0].Payload[60..], gfx[1].Payload[60..]);   // the rest of the desc as the game gave it
        var blobs = db.Where(r => r.Tag == 'B').Select(r => Convert.ToHexStringLower(r.Payload.AsSpan(0, 20))).ToHashSet();
        Assert.All(ps, h => Assert.Contains(h, blobs));
    }

    /// <summary>The proxy under a layer that isn't a d3d12.dll (ReShade as dxgi.dll with a RenoDX addon), on WARP: fakenext
    /// hands out a wrapper from the system D3D12CreateDevice that adds a root constant to every root signature. The real
    /// device is found through IID_UnwrappedObject, or without it through a root signature's device. The game's compute PSO
    /// is recorded with the game's root signature and again as the driver got it, with the layer's; a 'W' pairs the two, and
    /// another names the layer's own create (zero for what the game asked).</summary>
    [Theory]
    [InlineData("")]
    [InlineData("old")]
    public void The_proxy_records_under_a_layer_what_the_driver_gets(string mode)
    {
        UseProxyLedger();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SCSKiller.slnx"))) root = root.Parent;
        var bin = root == null ? null : Path.Combine(root.FullName, "proxy", "build", "Release");
        if (bin == null || !File.Exists(Path.Combine(bin, "fakenext.dll"))) return;   // this checkout's proxy isn't built
        var dir = Path.Combine(_root, "layer");
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "selftest.exe", "d3d12.dll", "fakenext.dll" }) File.Copy(Path.Combine(bin, f), Path.Combine(dir, f));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"layer {Environment.TickCount % 100000} {mode}") { RedirectStandardOutput = true })!)
        {
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("created 0x00000000", o);
            Assert.Contains("own 0x00000000", o);
        }
        Assert.Contains("under the layer's", File.ReadAllText(Path.Combine(dir, "scskiller.log")));

        var db = PsoDb.Read(Path.Combine(dir, "scskiller.db")).ToList();
        var cs = db.Where(r => r.Tag == 'C').ToDictionary(r => r.Key);
        Assert.Equal(3, cs.Count);
        var changed = db.Where(r => r.Tag == 'W').Select(r => (Driver: Convert.ToHexStringLower(r.Payload.AsSpan(0, 20)), Asked: Convert.ToHexStringLower(r.Payload.AsSpan(20)))).ToList();
        Assert.Equal(2, changed.Count);
        var (driver, asked) = changed.Single(w => w.Asked != PsoDb.Zero);
        var game = PsoDb.Parse(cs[asked]);
        var got = PsoDb.Parse(cs[driver]);
        Assert.NotEqual(game.Rs, got.Rs);
        Assert.Equal(game.Stages, got.Stages);
        var own = changed.Single(w => w.Asked == PsoDb.Zero).Driver;
        Assert.Equal(got.Rs, PsoDb.Parse(cs[own]).Rs);
        var rs = db.Where(r => r.Tag == 'B').ToDictionary(r => Convert.ToHexStringLower(r.Payload.AsSpan(0, 20)), r => r.Payload.Length - 20);
        Assert.True(rs[got.Rs] > rs[game.Rs]);   // the root constant the layer added
    }

    /// <summary>The proxy's frame times on WARP (`selftest frames`): each present of a swap chain made by the hooked DXGI factory
    /// is one frame, with an overlay that hooked Present / Present1 before the proxy did, a present nested in another, and a
    /// second overlay that hooked them after the proxy did before another swap chain of that vtable was made; a
    /// DXGI_PRESENT_TEST isn't one, nor a present that failed (DXGI_ERROR_WAS_STILL_DRAWING). A create's csv row names its thread and whether that thread presents, and the launch
    /// ends with #end. frames=0 in scskiller.ini: no frame log.</summary>
    [Fact]
    public void The_proxy_counts_each_present_once()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "frames");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        string Run(string ini)
        {
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), $"[scskiller]\r\nmode=record\r\n{ini}");
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), "frames 20") { RedirectStandardOutput = true })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            return o;
        }
        var on = Run("");
        Assert.Contains("overlay 48", on);
        Assert.Contains("late 2", on);
        Assert.Contains("frames 43", on);
        // tid,presents at the end of each create row: the presenting thread's PSO, then another thread's
        var lines = File.ReadAllLines(Path.Combine(dir, "scskiller_creates.csv"));
        var rows = lines.Where(l => !l.StartsWith('#')).Select(l => l.Split(',')).ToList();
        Assert.Equal(["1", "0"], rows.Select(r => r[8]));
        Assert.NotEqual(rows[0][7], rows[1][7]);
        Assert.StartsWith("#end,", lines[^1]);
        // #clock follows #session, #end carries t_ms too, and the frames file's launch has the #session stamp
        var session = Array.FindIndex(lines, l => l.StartsWith("#session,"));
        Assert.StartsWith("#clock,", lines[session + 1]);
        Assert.Equal(3, lines[^1].Split(',').Length);
        var frames = File.ReadAllBytes(Path.Combine(dir, "scskiller_frames.bin"));
        Assert.Equal(long.Parse(lines[session].Split(',')[1]), BitConverter.ToInt64(frames, 4));
        Assert.True(double.Parse(lines[^1].Split(',')[2], System.Globalization.CultureInfo.InvariantCulture)
                    >= double.Parse(lines[session + 1].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture));
        File.Delete(Path.Combine(dir, "scskiller_frames.bin"));
        Assert.Contains("frames -1", Run("frames=0\r\n"));
    }

    /// <summary>Frame generation's swap chain (`selftest framesfg`): no Present hook. A swap chain made on a queue named as
    /// FSR 3's or XeSS's frame generation present queue finds Present back at the original, and only the frames before it
    /// are logged; a slot an overlay took after the proxy stays the overlay's. OptiScaler.ini beside the exe with its frame
    /// generation on: nothing hooked; set up but not enabled, or none: hooked.</summary>
    [Fact]
    public void The_proxy_never_hooks_present_under_frame_generation()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        (string Out, string Log) Run(string name, string arg, string? opti = null, string extra = "")
        {
            var dir = Path.Combine(_root, "framesfg-" + name);
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
            File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
            if (opti != null) File.WriteAllText(Path.Combine(dir, "OptiScaler.ini"), opti);
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"framesfg \"{arg}\"{extra}") { RedirectStandardOutput = true })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            return (o, File.ReadAllText(Path.Combine(dir, "scskiller.log")));
        }
        foreach (var (name, queue) in new[] { ("fsr", "AMD FSR PresentQueue"), ("xefg", "XefgInterpolationSwapChain::present_queue_") })
        {
            var (o, log) = Run(name, queue);
            Assert.Contains("hooked 1", o);
            Assert.Contains("after 0", o);
            Assert.Contains("frames 3", o);
            Assert.Contains("frames: off (frame generation swap chain)", log);
        }
        var game = Run("game", "Game Queue");
        Assert.Contains("after 1", game.Out);
        Assert.Contains("frames 9", game.Out);
        // an overlay hooked Present after the proxy: that slot stays the overlay's, Present1 goes back to dxgi's
        var ov = Run("overlay", "AMD FSR PresentQueue", extra: " overlay");
        Assert.Contains("slot8 overlay 1", ov.Out);
        Assert.Contains("slot22 ours 0", ov.Out);
        Assert.Contains("frames 3", ov.Out);
        Assert.Contains("slot 8 is another hook's, left as it is", ov.Log);
        // OptiScaler: [FrameGen] Enabled (default false) and FGOutput (default nofg); older: FGType (default optifg) and [OptiFG] Enabled
        foreach (var (name, ini, fg) in new[] {
                     ("opti-on", "[FrameGen]\r\nEnabled=true\r\nFGInput=fsrfg\r\nFGOutput=fsrfg\r\n", true),
                     ("opti-disabled", "[FrameGen]\r\nEnabled=false\r\nFGOutput=fsrfg\r\n", false),
                     ("opti-default", "[FrameGen]\r\nEnabled=auto\r\nFGOutput=fsrfg\r\n", false),
                     ("opti-nofg", "[FrameGen]\r\nEnabled=true\r\nFGOutput=nofg\r\n", false),
                     ("opti-old-on", "[FrameGen]\r\nFGType=auto\r\n[OptiFG]\r\nEnabled=true\r\n", true),
                     ("opti-old-off", "[FrameGen]\r\nFGType=optifg\r\n[OptiFG]\r\nEnabled=auto\r\n", false) })
        {
            var r = Run(name, "plain", ini);
            Assert.Contains(fg ? "after 0" : "after 1", r.Out);
            Assert.Contains(fg ? "frames -1" : "frames 6", r.Out);
            Assert.Equal(fg, r.Log.Contains("frames: off (OptiScaler.ini has frame generation on)"));
        }
    }

    /// <summary>The proxy's check that a vtable is System32 dxgi.dll's (`selftest sysdxgi`, no proxy): a factory's is; a copy
    /// of that dxgi.dll mapped from another folder, as a game folder's dxgi.dll is, isn't, nor this exe or the heap.</summary>
    [Fact]
    public void Only_system32_dxgi_counts_as_the_system_dxgi()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(warmExe)!, "selftest.exe"), "sysdxgi")
            { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Equal(["factory 1", "copy 0", "exe 0", "heap 0"], o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>A mod's swap chain wrapper made through the hooked DXGI factory (`selftest frameswrap`, OptiScaler as
    /// dxgi.dll): its vtable isn't System32 dxgi.dll's, so its Present is never the proxy's; the real swap chain it presents
    /// through is hooked, and each present is one frame.</summary>
    [Fact]
    public void The_proxy_never_hooks_a_wrappers_swap_chain()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "frameswrap");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), "frameswrap 5") { RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("wrapper ours 0", o);
        Assert.Contains("real ours 1", o);
        Assert.Contains("frames 10", o);
        Assert.Contains("selftest.exe, not System32's dxgi.dll: not hooked", File.ReadAllText(Path.Combine(dir, "scskiller.log")));
    }

    /// <summary>The proxy's frame file held open by another handle at first (`selftest framesheld`): the frames presented
    /// meanwhile are lost, and the first one written after keeps its own time.</summary>
    [Fact]
    public void The_proxy_loses_frames_not_time_when_its_frame_file_is_held()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "framesheld");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), "framesheld") { RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        var drift = long.Parse(o.Split("drift_us ")[1].Trim());
        Assert.InRange(drift, -2000, 2000);
    }

    /// <summary>A device made through ID3D12DeviceFactory (`selftest factory`, the proxy's D3D12GetInterface on WARP), not
    /// D3D12CreateDevice: its compute PSO is recorded, and the factory's hook keeps the proxy loaded from the start.</summary>
    [Fact]
    public void The_proxy_records_a_device_from_a_device_factory()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "factory");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\nframes=0\r\n");
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), "factory") { RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        if (o.Contains("no factory")) return;   // a runtime without independent devices
        Assert.Contains("loaded 1", o);
        Assert.Contains("created 0x00000000", o);
        Assert.Single(PsoDb.Read(Path.Combine(dir, "scskiller.db")), r => r.Tag == 'C');
    }

    /// <summary>For <see cref="Selftest"/>: the app's own scskiller.armed for the selftest exe as it is.</summary>
    const string Armed = "valid";
    /// <summary>For <see cref="Selftest"/>: <see cref="Armed"/> without its checked= line.</summary>
    const string NoChecked = "no-checked";

    /// <summary>`selftest` in <paramref name="exeDir"/> next to the built proxy (record, frames=0, the extra ini lines), run with
    /// <paramref name="args"/>: its output, and the compute records the proxy wrote. <paramref name="armed"/>: scskiller.armed's
    /// text (<see cref="Armed"/>: the app's attestation for the selftest exe, its ledger entry too); null: none. The selftest
    /// doesn't arm itself.</summary>
    static (string Output, int Computes)? Selftest(string exeDir, string args, string ini = "", string? armed = Armed, Action<string>? ledger = null,
        string name = "selftest.exe", string? launchDir = null)
    {
        if (OwnWarmExe() is not { } warmExe) return null;
        var bin = Path.GetDirectoryName(warmExe)!;
        Directory.CreateDirectory(exeDir);
        var exe = Path.Combine(exeDir, name);
        if (!File.Exists(exe)) File.Copy(Path.Combine(bin, "selftest.exe"), exe);
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(exeDir, "d3d12.dll"), true);
        File.WriteAllText(Path.Combine(exeDir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\nframes=0\r\n" + ini);
        var file = Path.Combine(exeDir, ScsKiller.ArmedFile);
        if (armed is Armed or NoChecked) ScsKiller.WriteAttestation(exe);   // with its ledger entry
        ledger?.Invoke(ScsKiller.LedgerFile(exe));
        if (armed == NoChecked) File.WriteAllText(file, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), "checked=.*\r\n", ""));
        else if (armed is not (null or Armed)) File.WriteAllText(file, armed);
        var start = new ProcessStartInfo(Path.Combine(launchDir ?? exeDir, name), args) { RedirectStandardOutput = true };
        start.Environment["SCSKILLER_SELFTEST_UNARMED"] = "1";   // what's armed is this test's
        using var p = Process.Start(start)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        var db = Path.Combine(exeDir, "scskiller.db");
        return (o, File.Exists(db) ? PsoDb.Read(db).Count(r => r.Tag == 'C') : 0);
    }

    /// <summary>An anti-cheat client module (fakenext.dll under BEClient_x64.dll's name) for the proxy to find loaded.</summary>
    string AntiCheatClient()
    {
        var client = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "client")).FullName, "BEClient_x64.dll");
        File.Copy(Path.Combine(Path.GetDirectoryName(OwnWarmExe()!)!, "fakenext.dll"), client, true);
        return client;
    }

    /// <summary>Admission is decided at the first device, a factory's included (D3D12GetInterface only forwards): a client
    /// loaded after the factory was made and before its device means nothing is recorded.</summary>
    [Fact]
    public void The_proxy_hooks_no_factory_device_once_an_anti_cheat_client_is_loaded()
    {
        UseProxyLedger();
        if (OwnWarmExe() == null) return;
        var r = Selftest(Path.Combine(_root, "factory-ac"), $"factory \"{AntiCheatClient()}\"")!.Value;
        if (r.Output.Contains("no factory")) return;
        Assert.Contains("created 0x00000000", r.Output);
        Assert.Equal(0, r.Computes);
    }

    /// <summary>The proxy records only when scskiller.armed says armed=1 for the exe as it is, no marker is next to the exe
    /// (the built-in list; markers= only adds to it, and a malformed one refuses) and no anti-cheat client is loaded.
    /// Otherwise it writes nothing, its log included.</summary>
    [Theory]
    [InlineData(Armed, "", null, 2)]
    [InlineData(null, "", null, 0)]                                         // never armed
    [InlineData("[scskiller]\r\narmed=0\r\n", "", null, 0)]                 // disarmed
    [InlineData("[scskiller]\r\narmed=yes\r\n", "", null, 0)]               // malformed
    [InlineData("armed=1\r\n", "", null, 0)]                                // no section: malformed
    [InlineData("[scskiller]\r\narmed=1\r\n", "", null, 0)]                 // no exe fingerprint
    [InlineData(NoChecked, "", null, 0)]                                    // no checked=
    [InlineData("[scskiller]\r\narmed=1\r\nexe_size=1\r\nexe_time=1\r\n", "", null, 0)]   // another exe's
    [InlineData(Armed, "", "BEService_x64.exe", 0)]                         // a built-in marker beside the exe
    [InlineData(Armed, "", "start_protected_game.exe", 0)]                  // EasyAntiCheat's: only an offline session's attestation overrides it
    [InlineData(Armed, "markers=Other.exe\r\n", "BEService_x64.exe", 0)]    // markers= can't drop it
    [InlineData(Armed, "markers=Custom.exe\r\n", "Custom.exe", 0)]          // ... but adds
    [InlineData(Armed, "markers=a/b|\r\n", null, 0)]                        // malformed markers=
    [InlineData(Armed, "", "VALORANT-Win64-Shipping.exe", 0)]               // a Riot Games title's exe
    public void The_proxy_records_only_when_armed_and_clean(string? armed, string ini, string? beside, int computes)
    {
        UseProxyLedger();
        if (OwnWarmExe() == null) return;
        var exeDir = Path.Combine(_root, "admit");
        if (beside != null) File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(exeDir).FullName, beside), [0]);
        var r = Selftest(exeDir, "anticheat -", ini, armed)!.Value;
        Assert.Contains("created 0x00000000 0x00000000", r.Output);
        Assert.Equal(computes, r.Computes);
        Assert.Equal(computes > 0, File.Exists(Path.Combine(exeDir, "scskiller.log")));
        Assert.Equal(computes > 0, File.Exists(Path.Combine(exeDir, "scskiller_creates.csv")));
    }

    /// <summary>The app arms the exe by the folder the store installed it to; the process runs it through a link to that
    /// folder (a packaged Xbox app's WindowsApps path): admitted, both sides naming the ledger entry by the exe's file identity.</summary>
    [Fact]
    public void The_proxy_is_admitted_when_the_game_runs_through_a_link_to_its_folder()
    {
        UseProxyLedger();
        if (OwnWarmExe() == null) return;
        var exeDir = Directory.CreateDirectory(Path.Combine(_root, "Content")).FullName;
        var link = Junction(Path.Combine(_root, "WindowsApps-link"), exeDir);
        try
        {
            var r = Selftest(exeDir, "anticheat -", launchDir: link)!.Value;
            Assert.Contains("created 0x00000000 0x00000000", r.Output);
            Assert.Equal(2, r.Computes);
            Assert.Null(ScsKiller.Refused(Path.Combine(exeDir, "selftest.exe")));
        }
        finally { Directory.Delete(link); }   // the junction alone: the cleanup's recursive delete can't go through it
    }

    static string Junction(string link, string target)
    {
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mk.WaitForExit();
        Assert.True(Directory.Exists(link));
        return link;
    }

    /// <summary>An exe armed through a junction that is then pointed at another folder: revoking it by its path also
    /// revokes the entries keyed on the file and the folder it reached when it was armed.</summary>
    [Fact]
    public void Revoking_an_exe_whose_link_was_retargeted_revokes_what_it_was_armed_as()
    {
        var (a, b) = (Directory.CreateDirectory(Path.Combine(_root, "A")).FullName, Directory.CreateDirectory(Path.Combine(_root, "B")).FullName);
        File.WriteAllBytes(Path.Combine(a, "game.exe"), [1]);
        File.WriteAllBytes(Path.Combine(b, "game.exe"), [2]);
        var link = Junction(Path.Combine(_root, "link"), a);
        var exe = Path.Combine(link, "game.exe");
        try
        {
            ScsKiller.WriteAttestation(exe);
            var armedAs = ScsKiller.LedgerFiles(exe);
            Assert.Equal(3, armedAs.Count);   // identity, final path, path as given
            Directory.Delete(link);
            Junction(link, b);
            Assert.DoesNotContain(armedAs[0], ScsKiller.LedgerFiles(exe));
            Assert.Empty(ScsKiller.RevokeLedgers(exe));
            Assert.All(armedAs, l => Assert.False(File.Exists(l)));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    /// <summary>A proxy that can't read its exe's identity falls back to the final path, then to the path its process
    /// names: the app arms each with the same nonce, so the keys never split.</summary>
    [Fact]
    public void Every_ledger_key_a_proxy_may_fall_back_to_is_armed_with_the_same_nonce()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Game")).FullName;
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), [1]);
        var link = Junction(Path.Combine(_root, "link"), dir);
        try
        {
            var exe = Path.Combine(link, "game.exe");
            ScsKiller.WriteAttestation(exe);
            static string Nonce(string f) => File.ReadAllLines(f).Single(l => l.StartsWith("nonce=", StringComparison.Ordinal));
            var nonce = Nonce(Path.Combine(link, ScsKiller.ArmedFile));
            string Key(string path) => Path.Combine(ScsKiller.LedgerDir, Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Encoding.Unicode.GetBytes(path.ToLowerInvariant()))));
            foreach (var ledger in new[] { Key(GameFiles.FinalPath(Path.Combine(dir, "game.exe"))!), Key(exe) })   // the final path, the link's
                Assert.Equal(nonce, Nonce(ledger));
            Assert.Equal(nonce, Nonce(ScsKiller.LedgerFile(exe)));   // the identity's, which the proxy reads first
            Assert.Equal(3, ScsKiller.LedgerFiles(exe).Count);
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>A Riot Games title, armed and clean otherwise: refused as its own exe's name and by a "Riot Games" folder in
    /// its path, each with its reason.</summary>
    [Theory]
    [InlineData("VALORANT", "VALORANT-Win64-Shipping.exe", "anti-cheat next to the exe")]
    [InlineData(@"Riot Games\VALORANT\live\ShooterGame\Binaries\Win64", "selftest.exe", "Riot Games install")]
    public void The_proxy_refuses_riot_games(string folder, string name, string why)
    {
        UseProxyLedger();
        if (OwnWarmExe() == null) return;
        var exeDir = Path.Combine(_root, folder);
        var r = Selftest(exeDir, "anticheat -", name: name)!.Value;
        Assert.Contains("created 0x00000000 0x00000000", r.Output);
        Assert.Equal(0, r.Computes);
        Assert.False(File.Exists(Path.Combine(exeDir, "scskiller.log")));
        Assert.Equal(why, ScsKiller.Refused(Path.Combine(exeDir, name)));
    }

    /// <summary>A selftest that arms itself is admitted (its entry is where the proxy reads) and an unarmed one's refusal is
    /// read back, both in the run's <see cref="TestEnv.Ledger"/>; neither exe has anything in the user's live ledger.</summary>
    [Fact]
    public void The_selftest_and_the_proxy_use_the_test_ledger_never_the_live_one()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warm) return;
        var selfArmed = Directory.CreateDirectory(Path.Combine(_root, "self-armed")).FullName;
        var exe = Path.Combine(selfArmed, "selftest.exe");
        File.Copy(Path.Combine(Path.GetDirectoryName(warm)!, "selftest.exe"), exe);
        File.Copy(Path.Combine(Path.GetDirectoryName(warm)!, "d3d12.dll"), Path.Combine(selfArmed, "d3d12.dll"));
        File.WriteAllText(Path.Combine(selfArmed, "scskiller.ini"), "[scskiller]\r\nmode=record\r\nframes=0\r\n");
        var start = new ProcessStartInfo(exe, "anticheat -") { RedirectStandardOutput = true };
        start.Environment.Remove("SCSKILLER_SELFTEST_UNARMED");
        using (var p = Process.Start(start)!)
        {
            Assert.Contains("created 0x00000000 0x00000000", p.StandardOutput.ReadToEnd());
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        Assert.Equal(2, PsoDb.Read(Path.Combine(selfArmed, "scskiller.db")).Count(r => r.Tag == 'C'));

        var unarmed = Path.Combine(_root, "unarmed");
        Assert.Equal(0, Selftest(unarmed, "anticheat -", armed: null)!.Value.Computes);
        Assert.Equal("not armed", ScsKiller.Refused(Path.Combine(unarmed, "selftest.exe")));

        ScsKiller.LedgerDir = TestEnv.LiveLedger;   // names only: nothing there is written
        var live = ScsKiller.LedgerFiles(exe).Concat(ScsKiller.LedgerFiles(Path.Combine(unarmed, "selftest.exe"))).ToList();
        UseProxyLedger();
        Assert.All(live, l => Assert.False(File.Exists(l) || File.Exists(l + ".refused") || File.Exists(l + ".revoked")));
    }

    /// <summary>REFramework rewrites the loader's path of every dll in the exe's folder to a copy under _storage_: the proxy
    /// still reads its attestation beside the exe and records there. A copy really loaded from _storage_ records nothing,
    /// though an identical file is beside the exe.</summary>
    [Fact]
    public void The_proxy_records_when_a_mod_rewrites_its_loaded_path()
    {
        UseProxyLedger();
        if (OwnWarmExe() == null) return;
        var dir = Path.Combine(_root, "spoofed");
        var r = Selftest(dir, "anticheat - spoof")!.Value;
        Assert.Contains(@"\_storage_\d3d12.dll", r.Output);   // the rewrite took: GetModuleFileNameW reports the copy
        Assert.Contains("created 0x00000000 0x00000000", r.Output);
        Assert.True(r.Computes > 0);
        Assert.False(File.Exists(Path.Combine(dir, "_storage_", "scskiller.db")));

        var other = Path.Combine(_root, "elsewhere");
        File.Copy(Path.Combine(Path.GetDirectoryName(OwnWarmExe()!)!, "d3d12.dll"), Path.Combine(Directory.CreateDirectory(Path.Combine(other, "_storage_")).FullName, "d3d12.dll"));
        Assert.Equal(0, Selftest(other, "anticheat - _storage_")!.Value.Computes);
        Assert.False(File.Exists(Path.Combine(other, "_storage_", "scskiller.db")));
    }

    /// <summary>The app arms the game, then closes (no watcher). An update rewrites the exe (here: touched, or replaced by one
    /// of the same size); the next launch records nothing, scskiller.armed still there.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exe_changed_while_the_app_was_closed_is_not_armed(bool replaced)
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warm) return;
        var bin = Path.GetDirectoryName(warm)!;
        File.Copy(Path.Combine(bin, "selftest.exe"), _game.ExePath, true);   // the game's exe: the process the proxy is loaded in
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        Assert.Contains($"exe_size={new FileInfo(_game.ExePath).Length}", File.ReadAllText(armed));
        k.StopWatchingInstalls();   // the app closes
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(_exeDir, "d3d12.dll"), true);   // the built proxy in the fake's place
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\nframes=0\r\n");
        int Launch()
        {
            var start = new ProcessStartInfo(_game.ExePath, "anticheat -") { RedirectStandardOutput = true };
            start.Environment["SCSKILLER_SELFTEST_UNARMED"] = "1";
            using var p = Process.Start(start)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            var db = Path.Combine(_exeDir, "scskiller.db");
            var n = File.Exists(db) ? PsoDb.Read(db).Count(r => r.Tag == 'C') : 0;
            File.Delete(db);
            return n;
        }
        Assert.Equal(2, Launch());   // as armed

        if (replaced)
        {
            var bytes = File.ReadAllBytes(_game.ExePath);
            bytes[^1] ^= 1;   // same size, new bytes and write time
            File.WriteAllBytes(_game.ExePath, bytes);
        }
        else File.SetLastWriteTimeUtc(_game.ExePath, DateTime.UtcNow.AddMinutes(1));
        Assert.True(File.Exists(armed));
        Assert.Equal(0, Launch());
    }

    static string ProxySource() => File.ReadAllText(Path.Combine(TestEnv.RepoRoot, "proxy", "proxy.cpp"));

    static IEnumerable<string> ProxyMarkers(string src)
    {
        var list = System.Text.RegularExpressions.Regex.Match(src, @"kAntiCheatMarkers\[\] = \{(.*?)\};", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        return System.Text.RegularExpressions.Regex.Matches(list, "L\"([^\"]+)\"").Select(m => m.Groups[1].Value);
    }

    /// <summary>The proxy's own marker list is the app's.</summary>
    [Fact]
    public void The_proxy_knows_the_apps_anti_cheat_markers() => Assert.Equal(SCSKiller.Core.Games.GameFiles.MarkerNames, ProxyMarkers(ProxySource()));

    /// <summary>The proxy's offline admission drops the first kEasyAntiCheatMarkers names of its list by position: they must
    /// be EasyAntiCheat's, all of them and nothing else, in both lists.</summary>
    [Fact]
    public void An_offline_session_drops_exactly_EasyAntiCheats_markers()
    {
        string[] eac = ["EasyAntiCheat", "EasyAntiCheat_EOS", "start_protected_game.exe", "EasyAntiCheat_EOS_Setup.exe", "EasyAntiCheat_Setup.exe"];
        var src = ProxySource();
        var n = int.Parse(System.Text.RegularExpressions.Regex.Match(src, @"kEasyAntiCheatMarkers = (\d+);").Groups[1].Value);
        var markers = SCSKiller.Core.Games.GameFiles.Markers;
        Assert.Equal(eac, markers.Where(m => m.Kind == AntiCheat.EasyAntiCheat).Select(m => m.Name));
        Assert.Equal(eac, markers.Take(n).Select(m => m.Name));
        Assert.Equal(eac, ProxyMarkers(src).Take(n));
    }

    /// <summary>OptiScaler as dxgi.dll (one of the names it supports beside d3d12.dll) isn't ReShade, blocks nothing and is
    /// no d3d12.dll in the recorder's place: the recorder installs beside it and stays armed. As d3d12.dll it can't be chained
    /// (renamed it does nothing: it picks its role from its file name), and the page says to rename it to dxgi.dll.</summary>
    [Fact]
    public async Task OptiScaler_as_dxgi_is_recorded_beside_and_as_d3d12_the_page_says_to_rename_it()
    {
        var opti = Planning.MiddlewarePackTests.Pe("OptiScaler.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(Path.Combine(_exeDir, "dxgi.dll"), opti);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        Assert.Null(ReShade.Detect(_game));
        Assert.Null(k.Games.Single().RecorderSkip);
        k.InstallRecorder(_game.Id);
        await k.CheckRecorderGames(false);
        File.WriteAllText(Path.Combine(_exeDir, "OptiScaler.log"), "[info] OptiScaler working as dxgi.dll");   // its launch
        await Task.Delay(500);
        Assert.True(ArmedWithLedger(_game));
        k.UninstallRecorder(_game.Id);

        File.Delete(Path.Combine(_exeDir, "dxgi.dll"));
        File.WriteAllBytes(Path.Combine(_exeDir, "d3d12.dll"), opti);
        k.SetRecordAlongsideMod(_game.Id, true);
        var s = k.Games.Single();
        Assert.Equal(ScsKiller.SkipModNotChainable, s.RecorderSkip);
        Assert.StartsWith("OptiScaler", s.RecorderMod);
        Assert.Contains("rename it to dxgi.dll", ScsKiller.NotChainableReason(s.RecorderMod));
        Assert.Equal(opti, File.ReadAllBytes(Path.Combine(_exeDir, "d3d12.dll")));   // never renamed
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
    }

    /// <summary>A log sink that blocks (the CLI's stderr held) never holds up a watcher's revocation: the log line comes after.</summary>
    [Fact]
    public async Task A_blocked_log_never_delays_a_disarm()
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await k.CheckRecorderGames(false);
        Assert.True(File.Exists(armed));
        using var gate = new ManualResetEventSlim();
        k.Log = new Blocking(gate);
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0]);
        await Until(() => Revoked(armed) && Revoked(ScsKiller.LedgerFile(_game.ExePath)), 3);
        gate.Set();
    }

    sealed class Blocking(ManualResetEventSlim gate) : IProgress<string>
    {
        public void Report(string value) => gate.Wait();
    }

    /// <summary>What a mod writes as the game starts (OptiScaler's log, an ini saved through a temp file, an empty folder, a cache) can't
    /// bring anti-cheat: the recorder stays armed. A new binary or an anti-cheat folder anywhere in the install, or the exe
    /// written in place (at the next watcher pass), disarms it, with the path in the recorders' log.</summary>
    [Theory]
    [InlineData("log", false)]
    [InlineData("ini", false)]
    [InlineData("folder", false)]
    [InlineData("dll", true)]
    [InlineData("eac", true)]
    [InlineData("exe", true)]
    [InlineData("bin", true)]      // a PE under another name: LoadLibrary takes any extension
    [InlineData("dat", true)]      // a type not known to be data, empty when created
    [InlineData("cache", false)]   // a shader cache
    [InlineData("log1", false)]    // ReShade's log while another ReShade holds ReShade.log
    public async Task Only_a_change_that_may_bring_anti_cheat_disarms_the_recorder(string change, bool disarms)
    {
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var deep = Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content")).FullName;
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await k.CheckRecorderGames(false);
        Assert.True(File.Exists(armed));
        var gen = k.InstallGen(_game);
        var path = change switch
        {
            "log" => Path.Combine(_exeDir, "OptiScaler.log"),
            "ini" => Path.Combine(_exeDir, "OptiScaler.ini"),
            "folder" => Path.Combine(deep, "ShaderCache"),
            "dll" => Path.Combine(deep, "random.dll"),
            "eac" => Path.Combine(deep, "EasyAntiCheat"),
            "bin" => Path.Combine(deep, "guard.bin"),
            "dat" => Path.Combine(deep, "guard.dat"),
            "cache" => Path.Combine(deep, "foo.cache"),
            "log1" => Path.Combine(_exeDir, "ReShade.log1"),
            _ => _game.ExePath,
        };
        switch (change)
        {
            case "ini":
                File.WriteAllText(path + ".tmp", "[Upscalers]\r\n");
                File.Move(path + ".tmp", path);
                break;
            case "folder" or "eac": Directory.CreateDirectory(path); break;
            case "exe": File.AppendAllText(path, "update"); break;
            case "bin": File.WriteAllBytes(path, Planning.MiddlewarePackTests.Pe("guard.dll")); break;
            case "dat": File.Create(path).Dispose(); break;
            case "cache" or "log1": File.WriteAllText(path, "cached"); break;
            default: File.WriteAllBytes(path, [0]); break;
        }
        if (disarms)
        {
            if (change == "exe") await k.CheckRecorderGames(false);   // disarmed, checked clean and armed anew for the exe as it is
            else await Until(() => Revoked(armed));
            await Until(() => RecordersLog().Contains($"recorder disarmed until the install is checked again: {path}"));
            return;
        }
        await Task.Delay(500);   // a watcher's event arrives within milliseconds
        Assert.True(File.Exists(armed) && File.Exists(ScsKiller.LedgerFile(_game.ExePath)));
        Assert.Equal(gen, k.InstallGen(_game));
    }

    /// <summary>A launch the proxy passes through leaves its reason beside the exe's ledger entry, and the game's page shows it;
    /// an admitted launch takes it away.</summary>
    [Fact]
    public async Task The_proxy_leaves_why_it_passed_a_launch_through()
    {
        UseProxyLedger();
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var refused = ScsKiller.LedgerFile(_game.ExePath) + ".refused";
        File.WriteAllText(refused, "1759600000000 not armed");
        k.RefreshGame(_game.Id);
        Assert.Equal(ScsKiller.GameChangedNote, k.Games.Single().RecorderRefused);
        File.WriteAllText(refused, "1759600000000 disarmed while deciding");
        k.RefreshGame(_game.Id);
        Assert.Equal(ScsKiller.GameChangedNote, k.Games.Single().RecorderRefused);
        File.WriteAllText(refused, "1759600000000 anti-cheat client loaded");
        k.RefreshGame(_game.Id);
        Assert.Equal("The last launch wasn't recorded: anti-cheat client loaded.", k.Games.Single().RecorderRefused);
        Assert.False(ScsKiller.NeverRecorded(k.Games.Single()));
        File.Delete(refused);
        k.RefreshGame(_game.Id);
        Assert.Null(k.Games.Single().RecorderRefused);

        if (OwnWarmExe() == null) return;
        var dir = Path.Combine(_root, "refused");
        var exe = Path.Combine(dir, "selftest.exe");
        Assert.Equal(0, Selftest(dir, "anticheat -", armed: null)!.Value.Computes);
        Assert.Equal("not armed", ScsKiller.Refused(exe));
        Assert.True(Selftest(dir, "anticheat -")!.Value.Computes > 0);
        Assert.Null(ScsKiller.Refused(exe));
    }

    /// <summary>An update puts anti-cheat deep in a recorder game's install: the install watcher's event disarms the recorder
    /// at once, before any check runs, so the game launched right after records nothing. The next check takes it out.</summary>
    [Fact]
    public async Task A_change_in_the_install_disarms_the_recorder_before_any_check()
    {
        UseProxyLedger();
        var armed = Path.Combine(_exeDir, ScsKiller.ArmedFile);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        Assert.Contains("armed=1", File.ReadAllText(armed));   // Install's full check was clean
        var checks = 0;
        k.FullAntiCheatCheck = g => { Interlocked.Increment(ref checks); return Core.Games.GameFiles.DetectAntiCheat(g); };
        await k.CheckRecorderGames(false);   // watched; its first look re-arms
        Assert.True(File.Exists(armed));
        var before = Volatile.Read(ref checks);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        await Until(() => Revoked(armed));
        Assert.Equal(before, Volatile.Read(ref checks));   // no check ran: the event itself disarmed it
        Assert.Equal(AntiCheat.None, k.Games.Single().AntiCheat);

        if (OwnWarmExe() is { } warm)   // the game launches now, before the next check: a pass-through
        {
            File.Copy(Path.Combine(Path.GetDirectoryName(warm)!, "d3d12.dll"), Path.Combine(_exeDir, "d3d12.dll"), true);
            var r = Selftest(_exeDir, "anticheat -", armed: null)!.Value;
            Assert.Equal(0, r.Computes);
        }
        await k.CheckRecorderGames(false);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(armed));
    }

    /// <summary>The proxy on WARP with frames=0 (`selftest unload`): once a device made through it hooked the runtime, a
    /// FreeLibrary doesn't unload it, and a device from the system dll still runs its hooks.</summary>
    [Fact]
    public void The_proxy_stays_loaded_once_it_hooked_the_runtime()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "unload");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\nframes=0\r\n");
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), "unload") { RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("loaded 1", o);
        Assert.Contains("created 0x00000000", o);
    }

    /// <summary>The proxy's max_db_bytes on WARP: a db at the limit gets no record (never part of one), the csv still gets its
    /// row; under it the next record is appended whole, then nothing more. Needs this checkout's proxy built.</summary>
    [Fact]
    public void The_proxy_stops_recording_at_the_limit_and_keeps_the_timings()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "limit");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        var db = Path.Combine(dir, "scskiller.db");
        var seed = Environment.TickCount % 100000;
        void Run(long cap)
        {
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), $"[scskiller]\r\nmode=record\r\nmax_db_bytes={cap}\r\n");
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"chain {++seed}") { RedirectStandardOutput = true })!;
            Assert.Contains("created 0x00000000", p.StandardOutput.ReadToEnd());
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        long Framed() => PsoDb.Read(db).Sum(r => 5L + r.Payload.Length);
        int Rows() => File.ReadAllLines(Path.Combine(dir, "scskiller_creates.csv")).Count(l => !l.StartsWith('#'));

        Run(0);   // 0 is a limit too: nothing at all
        Assert.Equal(0, new FileInfo(db).Length);
        Assert.Equal(1, Rows());
        using (var f = File.Create(db)) PsoDb.WriteBlob(f, new string('a', 40), new byte[4096]);
        var full = new FileInfo(db).Length;
        Run(full);
        Assert.Equal(full, new FileInfo(db).Length);
        Assert.Equal(2, Rows());
        Assert.Contains("recording limit reached", File.ReadAllText(Path.Combine(dir, "scskiller.log")));

        Run(full + 1);   // under it: the record and its blobs, whole
        Assert.Single(PsoDb.Read(db), r => r.Tag == 'C');
        Run(full + 1);
        Assert.Single(PsoDb.Read(db), r => r.Tag == 'C');
        Assert.Equal(new FileInfo(db).Length, Framed());
        Assert.Equal(4, Rows());
    }

    /// <summary>The real warm on WARP whose child dies with exit code 3 before its last line (abort() exits with 3; here it is
    /// ended so while its first create hangs): an error, not the retry a 3 means after the child's last line. Needs this
    /// checkout's proxy built.</summary>
    [Fact]
    public async Task A_warm_child_that_exits_3_before_its_last_line_is_an_error_not_a_retry()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(warmExe)!, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!
            .StandardOutput.ReadToEnd().Trim();
        var work = Directory.CreateDirectory(Path.Combine(_root, "warm-exit3")).FullName;
        File.WriteAllBytes(Path.Combine(work, "scskiller.db"), ComputeDb(4).Db);
        var exe = $"scsk-x3-{Guid.NewGuid():N}"[..16] + ".exe";
        var psi = new ProcessStartInfo(warmExe, [work, exe, "--adapter-luid", luid, "--threads", "1"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["SCSKILLER_WARM_FAULT"] = "0:hang";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        while (await p.StandardOutput.ReadLineAsync() is { } line && !line.Contains("\"event\":\"start\"")) { }
        using (var child = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Single())
            Assert.True(TerminateProcess(child.Handle, 3));
        var rest = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromMinutes(1));
        await p.WaitForExitAsync();
        await err;
        Assert.Contains("the warm process died (exit code 0x00000003)", rest);
        Assert.Equal(1, p.ExitCode);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(nint process, uint exitCode);

    /// <summary>The real warm on WARP with a community recording's ray tracing tail: 400 collections, a pipeline linking 300
    /// of them and 70 AddToStateObject records chained on it, each naming 100-400 collections, then compute PSOs. Creates are
    /// faked at 2 ms per state object named (SCSKILLER_WARM_SO_MS), the hang limit is 5 s. The chain is serial, so waiting on
    /// a link another worker builds is no hang: one process does it all, no ray tracing poison, no retry. Needs this
    /// checkout's proxy built.</summary>
    [Fact]
    public async Task A_deep_state_object_chain_completes_in_one_process_without_a_ray_tracing_retry()
    {
        if (await SoChainWarm(null) is not var (output, log, code)) return;
        Assert.DoesNotContain("\"event\":\"retry\"", output);
        Assert.DoesNotContain("can't be trusted for ray tracing", log);
        Assert.DoesNotContain("stuck for", log);
        var done = WarmEvent.Parse(output.Split('\n').Last(l => l.Contains("\"event\":\"done\"")))!;
        Assert.Equal((671L, 671L, 0L), (done.Done, done.Total, done.Failed));
        Assert.Equal(0, code);
    }

    /// <summary>The same warm with collection 10's create hanging in the driver (SCSKILLER_WARM_FAULT): past its limit the
    /// process is poisoned, the pipeline and additions waiting on it are left too, and the run ends with a ray tracing retry
    /// from that collection.</summary>
    [Fact]
    public async Task A_state_object_create_hung_past_its_limit_still_poisons_and_retries()
    {
        if (await SoChainWarm("10:hang") is not var (output, log, code)) return;
        Assert.Contains("item 10 (a state object) stuck for", log);
        Assert.Contains("can't be trusted for ray tracing", log);
        var retry = WarmEvent.Parse(output.Split('\n').Single(l => l.Contains("\"event\":\"retry\"")))!;
        Assert.Equal(10L, retry.From);
        Assert.Equal(3, code);
    }

    async Task<(string Output, string Log, int Code)?> SoChainWarm(string? fault)
    {
        if (OwnWarmExe() is not { } warmExe) return null;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(warmExe)!, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!
            .StandardOutput.ReadToEnd().Trim();
        var work = Directory.CreateDirectory(Path.Combine(_root, $"warm-so-chain-{fault?.Replace(':', '-')}")).FullName;
        static byte[] U32(params uint[] v) => [.. v.SelectMany(BitConverter.GetBytes)];
        static byte[] Coll(PsoDb.Rec c) => [.. U32(6), .. Convert.FromHexString(c.Key), .. U32(0)];   // existing collection, no exports
        var colls = Enumerable.Range(0, 400).Select(i => new PsoDb.Rec('R', U32(1, 1, 3, (uint)i + 1))).ToList();   // a collection: a node mask
        var chain = new List<PsoDb.Rec> { new('R', [.. U32(3, 300), .. colls.Take(300).SelectMany(Coll)]) };
        for (var k = 0; k < 70; k++)
        {
            var named = colls.Skip(k % 7 * 10).Take(100 + k * 300 / 69).ToList();
            chain.Add(new PsoDb.Rec('A', [.. Convert.FromHexString(chain[^1].Key), .. U32(3, (uint)named.Count), .. named.SelectMany(Coll)]));
        }
        var (psos, _) = ComputeDb(200);
        using (var f = File.Create(Path.Combine(work, "scskiller.db")))
        {
            foreach (var r in colls.Concat(chain)) PsoDb.Write(f, r.Tag, r.Payload);
            f.Write(psos);
        }
        var exe = $"scsk-so-{Guid.NewGuid():N}"[..16] + ".exe";
        var psi = new ProcessStartInfo(warmExe, [work, exe, "--adapter-luid", luid, "--threads", "16"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["SCSKILLER_WARM_SO_MS"] = "2";
        psi.Environment["SCSKILLER_WARM_STUCK_S"] = "5";
        if (fault != null) psi.Environment["SCSKILLER_WARM_FAULT"] = fault;
        psi.Environment["SCSKILLER_WARM_EXIT_S"] = "30";   // a poisoned process's hung thread may hold its exit
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromMinutes(5));
        await p.WaitForExitAsync();
        await err;
        return (output, File.ReadAllText(Directory.GetFiles(work, "scskiller.log", SearchOption.AllDirectories).Single()), p.ExitCode);
    }

    /// <summary>The real warm on WARP whose last state object's Release never returns at the end (SCSKILLER_WARM_RELEASE_HANG):
    /// past the hang limit without progress it is left to the process exit, and the warm still ends with its done event. No
    /// items: the 2 s limit also abandons a PSO that WARP compiles that slowly on a busy machine, and a warm that abandoned a
    /// worker leaves its state objects to the exit unwatched. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task A_state_object_release_hanging_at_the_end_still_ends_the_warm()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(warmExe)!, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!
            .StandardOutput.ReadToEnd().Trim();
        var work = Directory.CreateDirectory(Path.Combine(_root, "warm-release-hang")).FullName;
        File.WriteAllBytes(Path.Combine(work, "scskiller.db"), []);
        var exe = $"scsk-rh-{Guid.NewGuid():N}"[..16] + ".exe";
        var psi = new ProcessStartInfo(warmExe, [work, exe, "--adapter-luid", luid, "--threads", "2"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["SCSKILLER_WARM_RELEASE_HANG"] = "1";
        psi.Environment["SCSKILLER_WARM_STUCK_S"] = "2";
        using var p = Process.Start(psi)!;
        try
        {
            var err = p.StandardError.ReadToEndAsync();
            var output = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromMinutes(1));
            await p.WaitForExitAsync();
            await err;
            var done = WarmEvent.Parse(output.Split('\n').Single(l => l.Contains("\"event\":\"done\"")))!;
            Assert.Equal((0L, 0L, 0L), (done.Done, done.Total, done.Failed));
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("made no progress for 2 s", File.ReadAllText(Directory.GetFiles(work, "scskiller.log", SearchOption.AllDirectories).Single()));
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
    }

    /// <summary>A zero-filled tail of scskiller.db (the power lost while the file grew) is a torn tail, not records of tag 0:
    /// the proxy on WARP cuts it and records after the last whole record. Needs this checkout's proxy built.</summary>
    [Fact]
    public void The_proxy_cuts_a_zero_filled_tail_of_its_db()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "zeros");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
        var db = Path.Combine(dir, "scskiller.db");
        var blob = "not a shader"u8.ToArray();
        using (var f = File.Create(db))
        {
            PsoDb.WriteBlob(f, PsoDb.Hex(SHA1.HashData(blob)), blob);
            f.Write(new byte[4096]);
        }
        var whole = new FileInfo(db).Length - 4096;
        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"chain {Environment.TickCount % 100000}") { RedirectStandardOutput = true })!)
        {
            Assert.Contains("created 0x00000000", p.StandardOutput.ReadToEnd());
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        Assert.Contains($"truncated a torn tail ({whole + 4096} -> {whole} bytes)", File.ReadAllText(Path.Combine(dir, "scskiller.log")));
        var records = PsoDb.Read(db).ToList();
        Assert.DoesNotContain(records, r => r.Tag == '\0');
        Assert.Single(records, r => r.Tag == 'C');
    }

    /// <summary>The proxy on WARP with scskiller.db held by another process that is writing a record: it neither cuts that
    /// record as a torn tail nor appends; once the other process is gone, the torn tail is cut and the PSO recorded.</summary>
    [Fact]
    public void The_proxy_leaves_a_db_another_process_writes_alone()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "shared");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
        var db = Path.Combine(dir, "scskiller.db");
        var seed = Environment.TickCount % 100000;
        void Run()
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"chain {++seed}") { RedirectStandardOutput = true })!;
            Assert.Contains("created 0x00000000", p.StandardOutput.ReadToEnd());
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        using (var other = new FileStream(db, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            other.Write([(byte)'B', 0, 0x10, 0, 0, 1, 2, 3]);   // a blob record still being written
            other.Flush();
            Run();
            Assert.Equal(8, new FileInfo(db).Length);
        }
        Assert.Contains("open in another process", File.ReadAllText(Path.Combine(dir, "scskiller.log")));
        Run();
        Assert.Single(PsoDb.Read(db), r => r.Tag == 'C');
        Assert.Contains("truncated a torn tail (8 -> 0 bytes)", File.ReadAllText(Path.Combine(dir, "scskiller.log")));
    }

    /// <summary>The proxy's keys file on WARP: a PSO it names isn't recorded again (the csv still marks it known), and a new PSO
    /// is recorded without the root signature the file names. Needs this checkout's proxy built.</summary>
    [Fact]
    public void The_proxy_records_only_what_the_keys_file_does_not_name()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "keys");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
        var db = Path.Combine(dir, "scskiller.db");
        void Run(int seed)
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"chain {seed}") { RedirectStandardOutput = true })!;
            Assert.Contains("created 0x00000000", p.StandardOutput.ReadToEnd());
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        string Known() => File.ReadAllLines(Path.Combine(dir, "scskiller_creates.csv")).Last(l => !l.StartsWith('#')).Split(',')[2];
        var seed = Environment.TickCount % 100000;

        Run(seed);
        Assert.Equal(["B", "B", "C"], PsoDb.Read(db).Select(r => r.Tag.ToString()).Order());   // root signature, CS, PSO
        var store = Path.Combine(dir, "recording.db");
        Recordings.Merge(store, db, null);
        Recordings.WriteKeys(store, null, Path.Combine(dir, Recordings.KeysFile));
        Assert.True(Recordings.Rotate(db, new FileInfo(db).Length));

        Run(seed);
        Assert.Equal(0, new FileInfo(db).Length);
        Assert.Equal("1", Known());
        Run(seed + 1);   // another CS under the same root signature
        Assert.Equal(["B", "C"], PsoDb.Read(db).Select(r => r.Tag.ToString()));
        Assert.Equal("0", Known());

        // a first session of a game whose files ship that CS: the keys file names it, the record goes in without its bytes
        var cs = PsoDb.Parse(PsoDb.Read(db).Last()).Stages.Values.Single();
        File.Delete(store);
        Recordings.WriteKeys(store, new HashSet<string> { cs }, Path.Combine(dir, Recordings.KeysFile));
        Assert.True(Recordings.Rotate(db, new FileInfo(db).Length));
        Run(seed + 1);
        var recorded = PsoDb.Read(db).ToList();
        Assert.Equal(["B", "C"], recorded.Select(r => r.Tag.ToString()));   // the root signature's bytes only
        Assert.Equal(cs, PsoDb.Parse(recorded[1]).Stages.Values.Single());
        Assert.NotEqual(cs, PsoDb.Hex(recorded[0].Payload.AsSpan(0, 20)));
        Assert.Equal("0", Known());
    }

    /// <summary>A game update the scan sees (the store's build id, or without one the exe) takes the last index's shaders out
    /// of the keys file until a compile indexes the new build. A record a session wrote by hash before the scan, whose shader
    /// the new build doesn't ship: the import keeps it, the compile skips only it and stops naming it, and once the recorder
    /// has recorded it again with its bytes it compiles.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_game_update_takes_the_shipped_shaders_out_of_the_keys_file_until_the_next_index(bool storeVersion)
    {
        byte[] a = "DXBC shader in both builds"u8.ToArray(), b = "DXBC shader in the first build only"u8.ToArray(), m = "DXBC shader a mod built"u8.ToArray();
        static string Sha(byte[] x) => PsoDb.Hex(SHA1.HashData(x));
        static PsoDb.Rec Cs(byte[] x) => new('C', PsoDb.Compute(PsoDb.Zero, Sha(x)));
        var (keys, inbox) = (Path.Combine(_exeDir, Recordings.KeysFile), Path.Combine(_exeDir, "scskiller.db"));
        HashSet<string> Named() => File.ReadAllBytes(keys)[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
        void Session(params PsoDb.Rec[] written)
        {
            using var f = File.Create(inbox);
            foreach (var r in written) PsoDb.Write(f, r.Tag, r.Payload);
        }
        var planner = new FakePlanner();
        async Task<FakePlanner> Compile(ScsKiller k)
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
            return planner;
        }
        ScsKiller Build(string content, string? version, params byte[][] shaders)
        {
            var k = Killer(new BlobReader(Unreal, shaders.ToDictionary(Sha), indexed: true, content), planner, game: _game with { Version = version });
            k.ProcessNames = () => new HashSet<string>();
            return k;
        }

        var k = Build(new string('a', 40), storeVersion ? "100" : null, a, b);
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await Compile(k);
        Assert.Equal(new[] { Sha(a), Sha(b) }.ToHashSet(), Named());

        // the update, then a session before SCSKiller scans: the recorder still has the first build's keys
        if (!storeVersion) File.AppendAllText(_game.ExePath, "patched");
        Session(Cs(b), new('B', [.. SHA1.HashData(m), .. m]), Cs(m));

        k = Build(new string('b', 40), storeVersion ? "101" : null, a);
        await k.ScanAsync(default);
        Assert.Equal(new[] { Sha(m), Cs(m).Key, Cs(b).Key }.ToHashSet(), Named());   // no shipped shader: a new pipeline goes in whole
        var store = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Assert.Equal(new[] { Cs(b).Key, Cs(m).Key }, PsoDb.Read(store).Where(r => r.Tag != 'B').Select(r => r.Key));

        Assert.Equal(1, (await Compile(k)).UnresolvedAtMaterialize);   // b alone
        Assert.Equal(new[] { Sha(a), Sha(m), Cs(m).Key }.ToHashSet(), Named());

        Session(new('B', [.. SHA1.HashData(b), .. b]), Cs(b));   // the game still creates it: recorded again, with its bytes
        k.RefreshGame(_game.Id);
        Assert.Equal(new[] { Sha(a), Sha(m), Cs(m).Key, Sha(b), Cs(b).Key }.ToHashSet(), Named());
        Assert.Equal(0, (await Compile(k)).UnresolvedAtMaterialize);
    }

    /// <summary>The emptied db filled again to the size, and at the write time, of what was imported is a new recording.</summary>
    [Fact]
    public async Task An_inbox_filled_again_to_the_same_size_and_write_time_is_imported()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        var (db, at) = (Path.Combine(_exeDir, "scskiller.db"), DateTime.UtcNow.AddMinutes(-1));
        PsoDb.Rec Record(byte cs)
        {
            var r = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, PsoDb.Hex(SHA1.HashData([cs]))));
            using (var f = File.Create(db)) PsoDb.Write(f, r.Tag, r.Payload);
            File.SetLastWriteTimeUtc(db, at);
            k.RefreshGame(_game.Id);
            return r;
        }
        var recorded = new[] { Record(1), Record(2) };
        Assert.Equal(recorded.Select(r => r.Key), PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Select(r => r.Key));
    }

    /// <summary>A recorder installed before the game was ever indexed has no keys file; the compile's index writes one naming
    /// its shaders, which isn't a recording; a build with other shaders replaces them, and Clear recording leaves them.</summary>
    [Fact]
    public async Task The_keys_file_names_the_shaders_of_the_last_index_with_or_without_a_recording()
    {
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        HashSet<string> Named() => File.ReadAllBytes(keys)[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
        async Task<ScsKiller> Compiled(string content, int shaders)
        {
            var k = Killer(new FakeReader(Unreal, content, shaders));
            k.ProcessNames = () => new HashSet<string>();
            await k.ScanAsync(default);
            k.InstallRecorder(_game.Id);
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            return k;
        }
        var first = Killer(new FakeReader(Unreal));
        first.ProcessNames = () => new HashSet<string>();
        await first.ScanAsync(default);
        first.InstallRecorder(_game.Id);
        Assert.False(File.Exists(keys));

        var k = await Compiled(new string('a', 40), 5);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"{i:x40}").ToHashSet(), Named());
        Assert.Equal(0, k.Games.Single().RecordingBytes);
        Assert.False(k.ClearRecording(_game.Id));

        var cs = "DXBC compute shader built at run time"u8.ToArray();
        var (sha, rec) = (PsoDb.Hex(SHA1.HashData(cs)), new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, $"{1:x40}")));
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db")))
        {
            PsoDb.Write(f, rec.Tag, rec.Payload);   // a shipped shader, as the recorder writes it: no bytes
            PsoDb.WriteBlob(f, sha, cs);
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, sha));
        }
        k.RefreshGame(_game.Id);
        Assert.Equal(5 + 3, Named().Count);
        Assert.Contains(rec.Key, Named());

        k = await Compiled(new string('b', 40), 1);   // the game was updated: shader 1 is gone
        Assert.Equal(new[] { $"{0:x40}", sha, new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, sha)).Key }.ToHashSet(), Named());

        Assert.True(k.ClearRecording(_game.Id));
        Assert.Equal(new[] { $"{0:x40}" }.ToHashSet(), Named());

        k.UninstallRecorder(_game.Id);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.log"), "loaded\n");
        Assert.True(k.ClearRecording(_game.Id));   // without the recorder nothing reads the keys file
        Assert.False(File.Exists(keys));
    }

    /// <summary>A recorder armed before any compile (a game that needs a recording; AMD; an Xbox app game with its package
    /// version) gets the build indexed in the background, so the keys file names the shipped shaders from the first launch:
    /// the recorder records them by hash only, and a session that started before the index still loses their bytes at the
    /// import. Once indexed, arming again reads nothing.</summary>
    [Theory]
    [InlineData(GpuVendor.Nvidia, Store.Steam)]
    [InlineData(GpuVendor.Amd, Store.Steam)]
    [InlineData(GpuVendor.Amd, Store.Xbox)]
    public async Task A_recorder_armed_before_any_compile_names_the_shipped_shaders_from_the_first_launch(GpuVendor vendor, Store store)
    {
        byte[] a = "DXBC shipped shader a"u8.ToArray(), b = "DXBC shipped shader b"u8.ToArray();
        static string Sha(byte[] x) => PsoDb.Hex(SHA1.HashData(x));
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        var reader = new BlobReader(Unreal, new[] { a, b }.ToDictionary(Sha), indexed: true, new string('a', 40));
        var k = Killer(reader, new NeedsRecordingPlanner(), game: _game with { Store = store, Version = store == Store.Xbox ? "1.0.2.0" : null },
            vendor: new FakeVendor(Gpu with { Vendor = vendor }));
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);   // the recorder goes in, armed
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Equal(1, reader.Indexes);
        Assert.Equal(new[] { Sha(a), Sha(b) }.ToHashSet(), File.ReadAllBytes(keys)[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet());
        Assert.Equal(GameStatus.NeedsRecording, k.Games.Single().Status);
        Assert.Equal(2, k.Store.LoadGame(_game.Id).ShaderCount);

        var cs = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha(a)));
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db")))
        {
            PsoDb.WriteBlob(f, Sha(a), a);   // written before the keys file was there
            PsoDb.Write(f, cs.Tag, cs.Payload);
        }
        k.RefreshGame(_game.Id);
        Assert.Equal([cs.Key], PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Select(r => r.Tag == 'B' ? "B" : r.Key));

        await k.ScanAsync(default);
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.Equal(1, reader.Indexes);
    }

    /// <summary>A game update (the store's build id, or without one the exe) makes the index stale: the next arming indexes
    /// the new build, and the keys file names its shaders instead of the old build's.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_game_update_reindexes_the_build_for_the_recorder(bool storeVersion)
    {
        byte[] a = "DXBC shader in both builds"u8.ToArray(), b = "DXBC shader in the first build only"u8.ToArray();
        static string Sha(byte[] x) => PsoDb.Hex(SHA1.HashData(x));
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        async Task<(ScsKiller, BlobReader)> Scanned(string content, string? version, params byte[][] shaders)
        {
            var reader = new BlobReader(Unreal, shaders.ToDictionary(Sha), indexed: true, content);
            var k = Killer(reader, game: _game with { Version = version });
            k.ProcessNames = () => new HashSet<string>();
            k.ManageRecorders = true;
            await k.ScanAsync(default);
            await k.RecorderIndexing.WaitAsync(HangGuard);
            return (k, reader);
        }
        var (k, reader) = await Scanned(new string('a', 40), storeVersion ? "100" : null, a, b);
        Assert.Equal(1, reader.Indexes);
        Assert.Equal(new[] { Sha(a), Sha(b) }.ToHashSet(), File.ReadAllBytes(keys)[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet());

        if (!storeVersion) File.AppendAllText(_game.ExePath, "patched");
        (k, reader) = await Scanned(new string('b', 40), storeVersion ? "101" : null, a);
        Assert.Equal(1, reader.Indexes);
        Assert.Equal([Sha(a)], File.ReadAllBytes(keys)[8..].Chunk(20).Select(Convert.ToHexStringLower));
        Assert.Equal(new string('b', 40), k.Store.LoadGame(_game.Id).IndexContentHash);
    }

    /// <summary>An update while the app runs: the watcher re-arms the changed exe before a scan lists the store's new build
    /// id, and the build is indexed again all the same.</summary>
    [Fact]
    public async Task An_update_the_watcher_rearms_is_indexed_before_the_next_scan()
    {
        var reader = new BlobReader(Unreal, new() { [new string('1', 40)] = [1] }, indexed: true, new string('a', 40));
        var k = Killer(reader, game: _game with { Version = "100" });
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.Equal(1, reader.Indexes);

        File.AppendAllText(_game.ExePath, "patched");
        await k.CheckRecorderGames(false);   // the armed exe changed: disarmed, and armed again unless its disarm is still queued
        await Until(() => !k.DisarmQueued(_game));
        await k.CheckRecorderGames(true);
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.Equal(2, reader.Indexes);
        k.StopWatchingInstalls();
    }

    /// <summary>The exe changing while its build is read for the recorder (its re-arm finds the index running): read again.</summary>
    [Fact]
    public async Task An_exe_changed_while_its_build_is_read_for_the_recorder_is_read_again()
    {
        var reader = new BlobReader(Unreal, new() { [new string('1', 40)] = [1] }, indexed: true, new string('a', 40));
        reader.Indexing = _ => { if (reader.Indexes == 1) File.AppendAllText(_game.ExePath, "patched"); };
        var k = Killer(reader);
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        await Until(() => reader.Indexes == 2 && k.RecorderIndexing.IsCompleted);
        Assert.Equal(new FileInfo(_game.ExePath).Length.ToString(), k.Store.LoadGame(_game.Id).IndexExeStamp?.Split(':')[0]);
    }

    /// <summary>A game whose shaders can't be read (encrypted without a key, or an engine SCSKiller can't index) is never
    /// indexed for the recorder: it records every shader with its bytes, as before.</summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "shaders stored inside materials")]
    public async Task A_game_whose_shaders_cant_be_indexed_records_as_before(bool encrypted, string? unsupported)
    {
        var reader = new BlobReader(Unreal with { Encrypted = encrypted, Unsupported = unsupported }, new() { [new string('1', 40)] = [1] }, indexed: true, new string('a', 40));
        var k = Killer(reader, new NeedsRecordingPlanner());
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        k.SetRecorderOverride(_game.Id, RecorderOverride.On);
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.Equal(0, reader.Indexes);
        Assert.False(File.Exists(Path.Combine(_exeDir, Recordings.KeysFile)));
        Assert.Null(k.Store.LoadGame(_game.Id).IndexContentHash);
    }

    /// <summary>A background index for the recorder waits while a compile runs. A compile starting stops a running one, which
    /// is queued again and reads once the compile ends. Every reader thread runs below normal priority, as the warm's.</summary>
    [Fact]
    public async Task The_index_for_the_recorder_waits_for_compiles_and_runs_below_normal()
    {
        var reader = new BlobReader(Unreal, new() { [new string('1', 40)] = [1] }, indexed: true, new string('a', 40));
        var priorities = new System.Collections.Concurrent.ConcurrentBag<ThreadPriority>();
        var reading = new ManualResetEventSlim();
        reader.Indexing = ct =>
        {
            // as the engine readers' loops: on the caller's scheduler
            Parallel.For(0, 64, new ParallelOptions { TaskScheduler = TaskScheduler.Current }, _ => priorities.Add(Thread.CurrentThread.Priority));
            if (reader.Indexes > 1) return;
            reading.Set();
            ct.WaitHandle.WaitOne(HangGuard);   // until a compile stops it
            ct.ThrowIfCancellationRequested();
        };
        var k = Killer(reader);
        var compiling = new ManualResetEventSlim(true);   // another process's compile
        k.OtherCompile = () => compiling.IsSet;
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        try
        {
            await k.ScanAsync(default);   // armed: its index is queued
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal((0, false), (reader.Indexes, k.RecorderIndexing.IsCompleted));

            compiling.Reset();
            Assert.True(reading.Wait(HangGuard));
            compiling.Set();
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal((1, false, (string?)null), (reader.Indexes, k.RecorderIndexing.IsCompleted, k.Store.LoadGame(_game.Id).IndexContentHash));   // stopped, waiting again

            compiling.Reset();
            await k.RecorderIndexing.WaitAsync(HangGuard);
            Assert.Equal(2, reader.Indexes);
            Assert.Equal(new string('a', 40), k.Store.LoadGame(_game.Id).IndexContentHash);
            Assert.Equal([ThreadPriority.BelowNormal], priorities.Distinct());
        }
        finally { compiling.Reset(); }   // a parked index would wait on the shared scheduler forever
    }

    /// <summary>The game starting while its build is indexed for the recorder stops the index: nothing is saved, and the
    /// recording goes on as before; the next arming after the game exits indexes it.</summary>
    [Fact]
    public async Task The_game_starting_stops_the_index_for_the_recorder()
    {
        var reader = new BlobReader(Unreal, new() { [new string('1', 40)] = [1] }, indexed: true, new string('a', 40));
        var entered = new ManualResetEventSlim();
        reader.Indexing = ct =>
        {
            entered.Set();
            ct.WaitHandle.WaitOne(HangGuard);
            ct.ThrowIfCancellationRequested();
        };
        var k = Killer(reader);
        k.ProcessNames = () => new HashSet<string>();
        k.RunningGameExes = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        Assert.True(entered.Wait(HangGuard));
        k.RunningGameExes = () => new HashSet<string> { Path.GetFileName(_game.ExePath) };
        k.PollGames();
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.Null(k.Store.LoadGame(_game.Id).IndexContentHash);
        Assert.False(File.Exists(Path.Combine(_exeDir, Recordings.KeysFile)));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));

        reader.Indexing = null;
        k.RunningGameExes = () => new HashSet<string>();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();   // the exit reconciles the recorder: armed again
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.Equal(new string('a', 40), k.Store.LoadGame(_game.Id).IndexContentHash);
        Assert.True(File.Exists(Path.Combine(_exeDir, Recordings.KeysFile)));
    }

    /// <summary>The db's share of the limit: the import empties the db into the copy, so the db may grow to what the copy
    /// leaves; paused = the db reached it.</summary>
    [Fact]
    public void The_db_cap_is_what_the_imported_recording_leaves()
    {
        const long L = 1000;
        Assert.Null(ScsKiller.DbCap(0, 10));
        Assert.Equal(1000, ScsKiller.DbCap(L, 0));   // first recording
        Assert.Equal(700, ScsKiller.DbCap(L, 300));
        Assert.Equal(0, ScsKiller.DbCap(L, 1200));
        Assert.Equal(("256 MB", "1 GB", "Unlimited"), (ScsKiller.LimitText(256), ScsKiller.LimitText(1024), ScsKiller.LimitText(0)));
        Assert.Equal(1024, new Settings(1, WarmPriority.Idle, DriverUpdateMode.Off, 1, false).RecordingLimitMB);
        Assert.Equal(1024, Killer().Settings.RecordingLimitMB);   // no settings file
    }

    /// <summary>A stored 256 MB the user didn't pick is the earlier default, saved along with any other setting: it becomes
    /// today's. A limit the user picked stays, 256 MB too, also when another setting changes later.</summary>
    [Theory]
    [InlineData(256, false, 1024)]
    [InlineData(256, true, 256)]
    [InlineData(512, false, 512)]
    [InlineData(32, false, 32)]
    [InlineData(200, false, 256)]   // no choice: the next one up, the user's all the same
    [InlineData(0, false, 0)]
    public void The_earlier_default_recording_limit_becomes_1_GB_and_a_chosen_one_stays(int stored, bool chosen, int limit)
    {
        var k = Killer();
        k.Store.SaveSettings(k.Settings with { RecordingLimitMB = stored, RecordingLimitChosen = chosen });
        k = Killer();
        Assert.Equal(limit, k.Settings.RecordingLimitMB);
        k.Settings = k.Settings with { PauseWhileGaming = false };
        Assert.Equal(limit, Killer().Settings.RecordingLimitMB);
    }

    [Fact]
    public void A_recording_limit_picked_after_the_default_changed_stays()
    {
        var k = Killer();
        k.Store.SaveSettings(k.Settings with { RecordingLimitMB = 256 });   // as an earlier version saved it
        k = Killer();
        Assert.Equal(1024, k.Settings.RecordingLimitMB);
        k.Settings = k.Settings with { RecordingLimitMB = 256 };
        Assert.Equal(256, Killer().Settings.RecordingLimitMB);
    }

    [Theory]
    [InlineData(2048, 0)]   // a choice of an earlier version
    [InlineData(100, 128)]
    [InlineData(1024, 1024)]
    [InlineData(0, 0)]
    public void A_stored_recording_limit_that_is_no_choice_becomes_the_next_one_up_or_unlimited(int stored, int limit)
    {
        var k = Killer();
        k.Store.SaveSettings(k.Settings with { RecordingLimitMB = stored });
        Assert.Equal(limit, Killer().Settings.RecordingLimitMB);
    }

    [Fact]
    public async Task The_recording_limit_goes_to_the_ini_and_pauses_the_recorder_when_reached()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        Assert.Contains($"max_db_bytes={1024L << 20}\r\n", File.ReadAllText(ini));
        Assert.False(k.Games.Single().RecordingPaused);

        var db = Path.Combine(_exeDir, "scskiller.db");
        using (var f = File.Create(db))
            for (int i = 0; i < 6; i++)
            {
                var cs = RandomNumberGenerator.GetBytes(100_000);   // shader bytes that don't compress, in no file of the game
                PsoDb.WriteBlob(f, PsoDb.Hex(SHA1.HashData(cs)), cs);
                PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, PsoDb.Hex(SHA1.HashData(cs))));
            }
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), "1.0,C,0,0,5.0\n");
        var held = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // the game runs: the recorder has it open
        k.RefreshGame(_game.Id);   // imported, not emptied: 600 KB here and about as much in the copy
        Assert.False(k.Games.Single().RecordingPaused);
        Assert.Equal(6 * (25 + 100_000 + 5 + 48), new FileInfo(db).Length);

        k.Settings = k.Settings with { RecordingLimitMB = 1 };
        await k.SettingsRefresh;
        k.ReconcileRecorders(_game.Id);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.True(s.RecordingPaused);
        var stored = new FileInfo(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Length;
        Assert.InRange(stored, 600_000, 620_000);
        Assert.Equal(new FileInfo(db).Length + stored + 14, s.RecordingBytes);   // the db, the copy and the csv
        Assert.Equal("Recording paused: limit reached (1 MB)", ScsKiller.PausedNote(k.Settings));
        Assert.Contains($"max_db_bytes={(1 << 20) - stored}\r\n", File.ReadAllText(ini));

        held.Dispose();   // the game exits: the db is emptied, recording goes on
        k.RefreshGame(_game.Id);
        Assert.Equal(0, new FileInfo(db).Length);
        Assert.False(k.Games.Single().RecordingPaused);

        k.Settings = k.Settings with { RecordingLimitMB = 0 };
        await k.SettingsRefresh;
        k.ReconcileRecorders(_game.Id);
        k.RefreshGame(_game.Id);
        Assert.DoesNotContain("max_db_bytes", File.ReadAllText(ini));
        Assert.False(k.Games.Single().RecordingPaused);

        k.UninstallRecorder(_game.Id);   // rewritten twice, still SCSKiller's by its hash: removed
        Assert.False(File.Exists(ini));

        k.InstallRecorder(_game.Id);
        File.AppendAllText(ini, "threads=4\r\n");   // the user's own edit: not rewritten
        k.Settings = k.Settings with { RecordingLimitMB = 256 };
        await k.SettingsRefresh;
        k.ReconcileRecorders(_game.Id);
        Assert.EndsWith("threads=4\r\n", File.ReadAllText(ini));
    }

    [Fact]
    public async Task Clear_recording_deletes_only_the_recorders_data_files_and_never_while_the_game_runs()
    {
        var k = Killer(new FakeReader(Unreal));
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var data = new[] { "scskiller.db", "scskiller_creates.csv", "scskiller.log" }.Select(f => Path.Combine(_exeDir, f)).ToList();
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        using (var f = File.Create(data[0])) PsoDb.Write(f, 'C', new byte[48]);
        File.WriteAllText(data[1], "1.0,C,0,0,5.0\n");
        File.WriteAllText(data[2], "loaded\n");
        k.RefreshGame(_game.Id);   // imported: the db emptied, the keys file written
        var copy = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Assert.True(File.Exists(copy));
        Assert.True(k.Games.Single().RecordingBytes > 0);
        Assert.True(File.Exists(keys));
        var kept = Directory.GetFiles(_game.InstallDir, "*", SearchOption.AllDirectories).Except(data.Append(keys)).ToList();   // the game's files and the recorder
        Assert.Contains(Path.Combine(_exeDir, "d3d12.dll"), kept);
        Assert.Contains(Path.Combine(_exeDir, "scskiller.ini"), kept);
        var imported = k.Store.LoadGame(_game.Id).RecordingImportedAt;

        running.Add("Fake-Win64-Shipping");
        Assert.Throws<InvalidOperationException>(() => k.ClearRecording(_game.Id));
        Assert.All(data.Append(copy), f => Assert.True(File.Exists(f)));

        running.Clear();
        Assert.True(k.ClearRecording(_game.Id));
        Assert.All(data.Append(copy), f => Assert.False(File.Exists(f)));
        Assert.False(File.Exists(keys));   // never indexed: nothing left to name
        Assert.All(kept, f => Assert.True(File.Exists(f)));
        Assert.Equal(0, k.Games.Single().RecordingBytes);
        Assert.True(k.Store.LoadGame(_game.Id).RecordingImportedAt > imported);   // the next compile re-plans without it
        Assert.False(k.ClearRecording(_game.Id));
    }

    /// <summary>A warmed game whose play session recorded new pipelines shows them as more to compile (the stale state with
    /// the "Add to queue" action); compiling it again includes them and it is warmed again.</summary>
    [Fact]
    public async Task New_recorded_pipelines_ask_for_a_compile_that_includes_them()
    {
        var k = await Warmed();
        var db = Path.Combine(_exeDir, "scskiller.db");
        void Record(int n)
        {
            using var f = new FileStream(db, FileMode.Append);
            for (int i = 0; i < n; i++)
            {
                var cs = Guid.NewGuid().ToByteArray();
                var sha = PsoDb.Hex(SHA1.HashData(cs));
                PsoDb.WriteBlob(f, sha, cs);
                PsoDb.Write(f, 'C', PsoDb.Compute(sha, sha));
            }
        }
        Record(3);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, "3 new pipelines recorded; compile again to include them"), (s.Status, s.StatusReason));
        Assert.Contains(s, k.StaleGames());

        await WarmOnce(k, _game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        Record(1);   // appended: only the new records count
        k.RefreshGame(_game.Id);
        Assert.Equal("1 new pipeline recorded; compile again to include them", k.Games.Single().StatusReason);
    }

    /// <summary>Pipelines the compiled plan already has don't count: a session that only re-records them leaves the game
    /// warmed; one more that the plan lacks is the one to compile.</summary>
    [Fact]
    public async Task Re_recording_planned_pipelines_asks_for_nothing()
    {
        static PsoDb.Rec Pso()
        {
            var sha = PsoDb.Hex(SHA1.HashData(Guid.NewGuid().ToByteArray()));
            return new('C', PsoDb.Compute(sha, sha));
        }
        var planned = new List<PsoDb.Rec> { Pso(), Pso() };
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: planned));
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var db = Path.Combine(_exeDir, "scskiller.db");
        void Record(params PsoDb.Rec[] rs)
        {
            using var f = new FileStream(db, FileMode.Append);
            foreach (var r in rs) PsoDb.Write(f, r.Tag, r.Payload);
        }

        Record([.. planned]);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        Assert.Empty(k.StaleGames());
        Assert.Equal(0, k.Games.Single().RecordedSinceWarm);

        Record(planned[0], Pso());
        k.RefreshGame(_game.Id);
        Assert.Equal("1 new pipeline recorded; compile again to include them", k.Games.Single().StatusReason);
    }

    [Fact]
    public async Task Record_alongside_refuses_a_mod_that_reads_its_own_file_name()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("OptiScaler.dll");   // OptiScaler installed as d3d12.dll
        File.WriteAllBytes(mod, bytes);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        Assert.StartsWith("OptiScaler.dll picks its role", ScsKiller.ChainBlocker(mod));
        k.SetRecordAlongsideMod(_game.Id, true);
        Assert.Equal(ScsKiller.SkipModNotChainable, k.Games.Single().RecorderSkip);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        Assert.Equal(bytes, File.ReadAllBytes(mod));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    /// <summary>vkd3d-proton as the game's d3d12.dll: named in the game's state, and refused when asked to record alongside
    /// it, with its own reason (a D3D12 warm doesn't reach its Vulkan pipelines); nothing in the folder changes.</summary>
    [Fact]
    public async Task Record_alongside_refuses_vkd3d_proton_with_its_reason()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        byte[] bytes = [.. Planning.MiddlewarePackTests.Pe("d3d12.dll"), .. "vkd3d-proton - build: 2.14"u8];
        File.WriteAllBytes(mod, bytes);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal(("d3d12.dll", false, ScsKiller.SkipForeignDll), (s.RecorderMod, s.RecordAlongsideMod, s.RecorderSkip));
        k.SetRecordAlongsideMod(_game.Id, true);
        s = k.Games.Single();
        Assert.Equal((true, ScsKiller.SkipVulkanMod), (s.RecordAlongsideMod, s.RecorderSkip));
        Assert.Equal(bytes, File.ReadAllBytes(mod));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
    }

    [Fact]
    public async Task A_mod_reinstalled_over_the_chained_recorder_is_never_overwritten()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var old = Planning.MiddlewarePackTests.Pe("d3d12.dll", [1]);
        File.WriteAllBytes(mod, old);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.SetRecordAlongsideMod(_game.Id, true);
        k.InstallRecorder(_game.Id);
        var fresh = Planning.MiddlewarePackTests.Pe("d3d12.dll", [2]);
        File.WriteAllBytes(mod, fresh);   // the mod's installer put its d3d12.dll back over ours

        k.UninstallRecorder(_game.Id);
        Assert.Equal(fresh, File.ReadAllBytes(mod));
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(_exeDir, ScsKiller.ChainName)));   // left, not lost
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderChained);
        Assert.Contains("another d3d12.dll is in its place", SharedLog(Path.Combine(k.Store.DataDir, "recorders.log")));
    }

    [Fact]
    public async Task The_games_recording_is_an_inbox_merged_by_record_key_then_emptied()
    {
        static byte[] Db(params string[] shaders)
        {
            using var m = new MemoryStream();
            foreach (var s in shaders)
            {
                var b = System.Text.Encoding.ASCII.GetBytes("DXBC shader built at run time: " + s);
                PsoDb.WriteBlob(m, PsoDb.Hex(SHA1.HashData(b)), b);
                PsoDb.Write(m, 'C', PsoDb.Compute(PsoDb.Zero, PsoDb.Hex(SHA1.HashData(b))));
            }
            return m.ToArray();
        }
        static List<string> Keys(IEnumerable<PsoDb.Rec> recs) => recs.Select(r => r.Key).ToList();
        var game = Path.Combine(_exeDir, "scskiller.db");
        var k = Killer(new FakeReader(Unreal));
        var imported = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        File.WriteAllBytes(game, Db("a", "b"));
        await k.ScanAsync(default);
        Assert.Equal(Keys(HashOnly.Records(Db("a", "b"))), Keys(PsoDb.Read(imported)));
        Assert.True(PsoDb.IsCompact(imported));
        Assert.Equal(0, new FileInfo(game).Length);   // emptied once imported

        File.WriteAllBytes(game, Db("a", "b", "c"));   // recorded again (a recorder without the keys file): only c is new
        await k.ScanAsync(default);
        Assert.Equal(Keys(HashOnly.Records(Db("a", "b", "c"))), Keys(PsoDb.Read(imported)));

        File.WriteAllBytes(game, Db("d"));
        await k.ScanAsync(default);
        Assert.Equal(Keys(HashOnly.Records(Db("a", "b", "c", "d"))), Keys(PsoDb.Read(imported)));

        var written = File.GetLastWriteTimeUtc(imported);   // nothing new since: not written again
        await k.ScanAsync(default);
        Assert.Equal(written, File.GetLastWriteTimeUtc(imported));
    }

    /// <summary>A recording stored before compact recordings (a proxy db beside its merge with the community's, the game
    /// folder's db imported as a copy) is converted once, in the background after a scan, never while the game runs: shader
    /// bytes the build's index has are left out, recording.all.db goes, the game folder's db is emptied, and nothing counts
    /// as newly recorded. A migration that fails midway leaves the old file as it was, and the next scan finishes it.</summary>
    [Fact]
    public async Task A_recording_from_before_compact_storage_is_converted_once_and_an_interrupted_conversion_loses_nothing()
    {
        var rs = CommunityTests.RootSignature();
        byte[] a = "DXBC shipped shader"u8.ToArray(), r = "DXBC shader built at run time"u8.ToArray();
        static string Sha(byte[] b) => PsoDb.Hex(SHA1.HashData(b));
        static PsoDb.Rec Blob(byte[] b) => new('B', [.. SHA1.HashData(b), .. b]);
        PsoDb.Rec ca = new('C', PsoDb.Compute(Sha(rs), Sha(a))), cr = new('C', PsoDb.Compute(Sha(rs), Sha(r)));
        const string content = "0123456789abcdef0123456789abcdef01234567";
        var game = _game with { Version = "100" };
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal), game: game);
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallRecorder(game.Id);
        running.Add("Fake-Win64-Shipping");

        var dir = k.Store.GameDir(game.Id);
        var store = Path.Combine(dir, "recording.db");
        byte[] Db(params PsoDb.Rec[] recs)
        {
            using var m = new MemoryStream();
            foreach (var x in recs) PsoDb.Write(m, x.Tag, x.Payload);
            return m.ToArray();
        }
        var legacy = Db(Blob(rs), Blob(a), ca, Blob(r), cr);
        File.WriteAllBytes(store, legacy);
        File.WriteAllBytes(Path.Combine(dir, "recording.all.db"), legacy);
        File.WriteAllText(Path.Combine(dir, "recording.all.db.key"), "stamp");
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        File.WriteAllBytes(inbox, legacy);   // imported as a copy, the way it was
        Sharing.SaveShipped(dir, new ShaderIndex(content, ["PCD3D_SM6"], new Dictionary<string, ShaderInfo> { [Sha(a)] = null! }, []));
        var rec = k.Store.LoadGame(game.Id);
        (rec.IndexContentHash, rec.IndexGameVersion, rec.RecordingImportedAt) = (content, "100", DateTimeOffset.Now.AddDays(-1));
        k.Store.SaveGame(game.Id, rec);

        k.Settings = k.Settings with { RecordingLimitMB = 1 };
        await k.SettingsRefresh;
        File.WriteAllBytes(Path.Combine(dir, "recording.all.db"), [.. legacy, .. new byte[2 << 20]]);   // over the limit alone

        await k.ScanAsync(default);   // the game runs: left for later
        await k.RecordingMigration;
        Assert.Equal(legacy, File.ReadAllBytes(store));
        Assert.Equal(legacy, File.ReadAllBytes(inbox));
        Assert.False(k.Games.Single().RecordingPaused);   // the old merge doesn't count against the limit: it's deleted, not kept

        running.Clear();
        File.SetAttributes(store, FileAttributes.ReadOnly);   // the conversion fails midway, replacing the old file
        await k.ScanAsync(default);
        await k.RecordingMigration;
        Assert.Equal(legacy, File.ReadAllBytes(store));
        Assert.True(File.Exists(Path.Combine(dir, "recording.all.db")));
        Assert.Equal(legacy, File.ReadAllBytes(inbox));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        File.SetAttributes(store, FileAttributes.Normal);
        await k.ScanAsync(default);
        await k.RecordingMigration;
        Assert.True(PsoDb.IsCompact(store));
        Assert.Equal(new[] { Blob(rs), ca, Blob(r), cr }.Select(x => x.Key), PsoDb.Read(store).Select(x => x.Key));   // a's bytes come from the install
        Assert.False(File.Exists(Path.Combine(dir, "recording.all.db")));
        Assert.False(File.Exists(Path.Combine(dir, "recording.all.db.key")));
        Assert.Equal(0, new FileInfo(inbox).Length);
        var keys = File.ReadAllBytes(Path.Combine(_exeDir, Recordings.KeysFile))[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
        Assert.Equal(new[] { Sha(a), Sha(rs), Sha(r), ca.Key, cr.Key }.Order(), keys.Order());
        var after = k.Store.LoadGame(game.Id);
        Assert.Equal((rec.RecordingImportedAt, content), (after.RecordingImportedAt, after.RecordingIndexHash));
        Assert.Equal(new[] { store, inbox }.Sum(f => new FileInfo(f).Length), k.Games.Single().RecordingBytes);
    }

    /// <summary>A community recording merged the old way, with no recording of this PC's: the merge goes and no empty
    /// recording is left in its place (the game would then look recorded).</summary>
    [Fact]
    public async Task A_community_only_merge_from_before_compact_storage_leaves_no_recording()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var dir = k.Store.GameDir(_game.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "recording.all.db"), CommunityTests.HashOnly(new string('1', 40)));
        File.WriteAllText(Path.Combine(dir, "recording.all.db.key"), "stamp");
        await k.ScanAsync(default);
        await k.RecordingMigration;
        Assert.Equal([Path.Combine(dir, "recording.db.lock")], Directory.GetFiles(dir, "recording*"));   // the lock's file only
    }

    /// <summary>A compile after the recording was stored without an index (never compiled, or a build since) leaves out the
    /// shader bytes the index has; the plan and the warm still get every recorded shader, from the install.</summary>
    [Fact]
    public async Task A_compile_keeps_only_the_shader_bytes_its_index_lacks_and_plans_and_warms_with_all_of_them()
    {
        var rs = CommunityTests.RootSignature();
        byte[] vs = "DXBC vertex shader in the game's files"u8.ToArray(), rt = "DXBC vertex shader built at run time"u8.ToArray();
        static string Sha(byte[] b) => PsoDb.Hex(SHA1.HashData(b));
        static PsoDb.Rec Blob(byte[] b) => new('B', [.. SHA1.HashData(b), .. b]);
        PsoDb.Rec S(byte[] v) => new('S', PsoDb.Stream(Sha(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha(v) }, [], 3, [], 0));
        var planner = new NeedsRecordingPlanner();
        var k = Killer(new BlobReader(Unreal, new() { [Sha(vs)] = vs }, indexed: true), planner);
        var store = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        PsoDb.WriteCompact(store, [Blob(rs), Blob(vs), S(vs), Blob(rt), S(rt)]);
        await k.ScanAsync(default);

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal(new[] { Blob(rs), Blob(vs), S(vs), Blob(rt), S(rt) }.Select(r => r.Key).Order(), planner.BuiltWith!.Order());
        Assert.Equal(0, planner.Inner.UnresolvedAtMaterialize);
        Assert.Equal(new[] { Blob(rs), S(vs), Blob(rt), S(rt) }.Select(r => r.Key), PsoDb.Read(store).Select(r => r.Key));
        Assert.Equal("content-1", k.Store.LoadGame(_game.Id).RecordingIndexHash);
    }

    [Fact]
    public async Task Reconcile_waits_while_the_game_runs()
    {
        var g = FakeGame("test:run", "Runner");
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RUNNER" };   // by name only, any case
        var k = RecordKiller([g], running: () => running);
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.False(File.Exists(Dll(g)));
        Assert.Equal("installs when the game exits", k.Games.Single().RecorderNote);
        Assert.True(k.Games.Single().RecorderEffective);

        running.Clear();   // it exited: the watcher calls this
        k.ReconcileRecorders(g.Id);
        Assert.True(ScsKiller.IsOurProxy(Dll(g)));
        Assert.Null(k.Games.Single().RecorderNote);

        running.Add("Runner");
        k.SetRecorderOverride(g.Id, RecorderOverride.Off);
        Assert.True(ScsKiller.IsOurProxy(Dll(g)));
        Assert.Equal("removed when the game exits", k.Games.Single().RecorderNote);
        Assert.Throws<InvalidOperationException>(() => k.UninstallRecorder(g.Id));

        running.Clear();
        k.ReconcileRecorders(g.Id);
        Assert.False(File.Exists(Dll(g)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // a copy of our proxy put next to the game by hand, not in the manifest: ours, never a mod to chain
    public async Task A_recorder_installed_next_to_a_launcher_moves_next_to_the_game_it_starts(bool handCopy)
    {
        // The Witcher 3's layout: CD PROJEKT RED's launcher at the root, the game (with its own Agility SDK) in bin\x64_dx12
        var install = Path.Combine(_root, "The Witcher 3");
        var bin = Directory.CreateDirectory(Path.Combine(install, "bin", "x64_dx12", "D3D12_0")).Parent!.FullName;
        var launcher = Path.Combine(install, "REDprelauncher.exe");
        File.WriteAllBytes(launcher, DiscoveryAndVendorTests.Exe("Qt5Core.dll", 100));
        File.WriteAllBytes(Path.Combine(bin, "w3fake.exe"), DiscoveryAndVendorTests.Exe("sl.interposer.dll", 5000));
        File.WriteAllBytes(Path.Combine(bin, "D3D12_0", "D3D12Core.dll"), new byte[64]);
        var was = new Game("steam:292030", "The Witcher 3", Store.Steam, install, launcher);
        var before = RecordKiller([was]);
        await before.ScanAsync(default);
        before.InstallRecorder(was.Id);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(install, "d3d12.dll")));
        static PsoDb.Rec Pso(char c) => new('C', PsoDb.Compute(PsoDb.Zero, new string(c, 40)));
        using (var f = File.Create(Path.Combine(install, "scskiller.db"))) PsoDb.Write(f, 'C', Pso('a').Payload);   // left by a launch, not imported
        // the session the recorder there saw, moved by hand into the root: the game page shows it after the move too
        const long at = 1_700_000_000_000;
        File.WriteAllLines(Path.Combine(install, "scskiller_creates.csv"), [$"#session,{at},w3fake.exe", "#clock,10.0", "20.0,G,0,0,250.0", $"#end,{at + 60_000},60010.0"]);
        File.WriteAllBytes(Path.Combine(install, FrameLog.FileName), FrameLogTests.Launch(at, 10_000, Enumerable.Range(1, 5000).Select(i => 10.0 + i * 10)));
        File.WriteAllText(Path.Combine(install, "scskiller.log"), "the root's log");
        if (handCopy)
        {
            File.Copy(_proxy, Path.Combine(bin, "d3d12.dll"));
            File.WriteAllText(Path.Combine(bin, FrameLog.FileName), "an older launch's");   // replaced
            File.SetLastWriteTimeUtc(Path.Combine(bin, FrameLog.FileName), DateTime.UtcNow.AddDays(-1));
            File.WriteAllText(Path.Combine(bin, "scskiller.log"), "a later launch's");      // kept
            File.SetLastWriteTimeUtc(Path.Combine(bin, "scskiller.log"), DateTime.UtcNow.AddDays(1));
        }

        File.WriteAllText(Path.Combine(install, "launcher-configuration.json"), DiscoveryAndVendorTests.RedConfig().Replace("witcher3.exe", "w3fake.exe"));
        var game = was with { ExePath = Core.Games.GameFiles.FindExe(install)! };
        Assert.Equal(Path.Combine(bin, "w3fake.exe"), game.ExePath);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "w3fake" };
        var k = RecordKiller([game], running: () => running);
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.Equal("moves when the game exits", k.Games.Single().RecorderNote);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(install, "d3d12.dll")));
        Assert.Equal(launcher, k.Store.LoadGame(game.Id).RecorderExe);   // the uninstall hook still finds it

        running.Clear();
        k.ReconcileRecorders(game.Id);
        Assert.Null(k.Games.Single().RecorderNote);
        foreach (var f in new[] { "d3d12.dll", "scskiller.ini", "scskiller.db", "scskiller.keys", "scskiller_creates.csv", "scskiller.log", FrameLog.FileName })
            Assert.False(File.Exists(Path.Combine(install, f)), f);
        Assert.Equal(handCopy ? "a later launch's" : "the root's log", File.ReadAllText(Path.Combine(bin, "scskiller.log")));
        var csv = Path.Combine(bin, "scskiller_creates.csv");
        Assert.Equal(1, SessionLog.Read(csv, "w3fake.exe").Last!.Compiles);
        Assert.Equal(4999, FrameLog.Read(Path.Combine(bin, FrameLog.FileName), csv, "w3fake.exe")!.Frames);
        var s = k.Games.Single();
        Assert.Equal((1L, 4999L), (s.LastSession!.Compiles, s.LastFrames!.Frames));
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(Path.Combine(bin, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(bin, ScsKiller.ChainName)));
        Assert.True(File.Exists(Path.Combine(bin, "scskiller.ini")));
        var rec = k.Store.LoadGame(game.Id);
        Assert.Null(rec.RecorderChained);
        Assert.Equal((game.ExePath, install), (rec.RecorderExe, rec.RecorderInstallDir));
        Assert.Equal(["d3d12.dll", "scskiller.ini"], rec.RecorderFiles.Keys.Order());
        Assert.Contains(Pso('a').Key, PsoDb.Read(Path.Combine(k.Store.GameDir(game.Id), "recording.db")).Select(r => r.Key));
        Assert.Contains($"recorder removed ({install})", RecordersLog());
    }

    static PsoDb.Rec Rec(char c) => new('C', PsoDb.Compute(PsoDb.Zero, new string(c, 40)));

    /// <summary>The Witcher 3's layout with a recorder installed next to its launcher (chained to a mod there when
    /// <paramref name="mod"/>), a recording not imported yet beside it, then the launcher configuration that names the game.</summary>
    async Task<(string Install, string Bin, Game Was, Game Game)> RecorderNextToALauncher(byte[]? mod = null)
    {
        var install = Path.Combine(_root, "The Witcher 3");
        var bin = Directory.CreateDirectory(Path.Combine(install, "bin", "x64_dx12")).FullName;
        File.WriteAllBytes(Path.Combine(install, "REDprelauncher.exe"), DiscoveryAndVendorTests.Exe("Qt5Core.dll", 100));
        File.WriteAllBytes(Path.Combine(bin, "w3fake.exe"), DiscoveryAndVendorTests.Exe("sl.interposer.dll", 5000));   // not witcher3.exe: the folder checks see real processes
        var was = new Game("steam:292030", "The Witcher 3", Store.Steam, install, Path.Combine(install, "REDprelauncher.exe"));
        if (mod != null) File.WriteAllBytes(Path.Combine(install, "d3d12.dll"), mod);
        var before = RecordKiller([was]);
        await before.ScanAsync(default);
        if (mod != null) before.SetRecordAlongsideMod(was.Id, true);
        before.InstallRecorder(was.Id);
        using (var f = File.Create(Path.Combine(install, "scskiller.db"))) PsoDb.Write(f, 'C', Rec('a').Payload);
        File.WriteAllText(Path.Combine(install, "launcher-configuration.json"), DiscoveryAndVendorTests.RedConfig().Replace("witcher3.exe", "w3fake.exe"));
        return (install, bin, was, was with { ExePath = Core.Games.GameFiles.FindExe(install)! });
    }

    [Fact]
    public async Task Anti_cheat_found_once_the_exe_moved_takes_the_recorder_out_where_the_record_says_it_is()
    {
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        var (install, bin, _, game) = await RecorderNextToALauncher(mod);
        Assert.True(File.Exists(Path.Combine(install, ScsKiller.ChainName)));
        Directory.CreateDirectory(Path.Combine(install, "EasyAntiCheat"));

        var k = RecordKiller([game]);
        await k.ScanAsync(default);   // the verdict lands before any reconcile
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.Equal(mod, File.ReadAllBytes(Path.Combine(install, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(install, ScsKiller.ChainName)));
        Assert.False(File.Exists(Path.Combine(install, "scskiller.ini")));
        Assert.False(File.Exists(Path.Combine(bin, "d3d12.dll")));
        var rec = k.Store.LoadGame(game.Id);
        Assert.Equal((null, null, 0), (rec.RecorderExe, rec.RecorderChained, rec.RecorderFiles.Count));
    }

    /// <summary>A game set up and compiled on an Unreal bootstrap stub (Dead Island 2 from the Xbox app): the next scan takes
    /// the Shipping exe, the compile is to run again under its name, and the recorder moves next to it once the game exits.</summary>
    [Fact]
    public async Task A_game_set_up_on_an_unreal_stub_switches_to_its_shipping_exe()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "Dead Island 2", "Content")).FullName;
        Directory.CreateDirectory(Path.Combine(content, "Engine"));
        var stub = Path.Combine(content, "DeadIsland.exe");
        File.WriteAllBytes(stub, DiscoveryAndVendorTests.Exe(null, 1000));
        var game = new Game("test:stub-" + Guid.NewGuid().ToString("N")[..8], "Dead Island 2", Store.Other, content, stub, "1.0");
        IReadOnlySet<string> running = new HashSet<string>();
        var k = RecordKiller([game], running: () => running);
        await k.ScanAsync(default);
        k.InstallRecorder(game.Id);
        k.Enqueue(game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Equal("DeadIsland.exe", k.Store.LoadGame(game.Id).WarmedExeName);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(content, "d3d12.dll")));

        // the state an earlier build left: the Shipping exe was there all along, discovery took the stub
        var bin = Directory.CreateDirectory(Path.Combine(content, "DeadIsland", "Binaries", "WinGDK")).FullName;
        var shipping = Path.Combine(bin, "DeadIsland-WinGDK-Shipping.exe");
        File.WriteAllBytes(shipping, DiscoveryAndVendorTests.Exe("d3d12.dll", 5000));
        running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DeadIsland-WinGDK-Shipping" };
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal(shipping, s.Game.ExePath);
        Assert.Equal((GameStatus.Stale, "the last compile filled the cache of DeadIsland.exe; the game runs DeadIsland-WinGDK-Shipping.exe"), (s.Status, s.StatusReason));

        k.ReconcileRecorders();
        Assert.Equal("moves when the game exits", k.Games.Single().RecorderNote);   // the running check follows the Shipping exe
        Assert.Equal((stub, shipping), (k.Store.LoadGame(game.Id).RecorderMoveFrom, k.Store.LoadGame(game.Id).RecorderMoveTo));

        running = new HashSet<string>();
        k.ReconcileRecorders();
        Assert.Null(k.Games.Single().RecorderNote);
        Assert.False(File.Exists(Path.Combine(content, "d3d12.dll")));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(bin, "d3d12.dll")));
        var rec = k.Store.LoadGame(game.Id);
        Assert.Equal((shipping, null, null), (rec.RecorderExe, rec.RecorderMoveFrom, rec.RecorderMoveTo));
    }

    /// <summary>The recorder moves from an Unreal stub to its Shipping exe with the game closed: the Shipping exe is armed at
    /// once (its ledger entry and armed file share a nonce) and stays armed through the watcher's passes, so the next launch
    /// records; nothing stays armed at the stub.</summary>
    [Fact]
    public async Task A_recorder_moved_to_the_shipping_exe_is_armed_for_the_next_launch()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "Dead Island 2", "Content")).FullName;
        Directory.CreateDirectory(Path.Combine(content, "Engine"));
        var stub = Path.Combine(content, "DeadIsland.exe");
        File.WriteAllBytes(stub, DiscoveryAndVendorTests.Exe(null, 1000));
        var game = new Game("test:stub-" + Guid.NewGuid().ToString("N")[..8], "Dead Island 2", Store.Other, content, stub, "1.0");
        var k = RecordKiller([game]);
        await k.ScanAsync(default);
        k.InstallRecorder(game.Id);
        await k.CheckRecorderGames(false);
        Assert.True(File.Exists(Path.Combine(content, ScsKiller.ArmedFile)));
        var bin = Directory.CreateDirectory(Path.Combine(content, "DeadIsland", "Binaries", "WinGDK")).FullName;
        var shipping = Path.Combine(bin, "DeadIsland-WinGDK-Shipping.exe");
        File.WriteAllBytes(shipping, DiscoveryAndVendorTests.Exe("d3d12.dll", 5000));
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        string? Nonce(string file) => File.Exists(file) ? File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith("nonce="))?[6..] : null;
        void ArmedForLaunch()
        {
            Assert.True(ScsKiller.IsOurProxy(Path.Combine(bin, "d3d12.dll")));
            Assert.NotNull(Nonce(ScsKiller.LedgerFile(shipping)));
            Assert.Equal(Nonce(ScsKiller.LedgerFile(shipping)), Nonce(Path.Combine(bin, ScsKiller.ArmedFile)));
        }
        ArmedForLaunch();
        for (var pass = 0; pass < 3; pass++)
        {
            await k.CheckRecorderGames(pass == 1);
            ArmedForLaunch();
        }
        Assert.False(File.Exists(Path.Combine(content, ScsKiller.ArmedFile)));
        Assert.False(File.Exists(ScsKiller.LedgerFile(stub)));
    }

    /// <summary>A recorder that moves to the Shipping exe always takes its proxy out of the stub's folder, first. Its
    /// scskiller.ini goes only while the manifest's hash matches: a stale or lost hash, a user's edit, a value no parser
    /// takes or a file held open leave it where it is (without the proxy it does nothing).</summary>
    [Theory]
    [InlineData("tracked")]
    [InlineData("stale")]
    [InlineData("untracked")]
    [InlineData("edited")]
    [InlineData("unicode")]
    [InlineData("locked")]
    public async Task A_moved_recorder_always_takes_its_proxy_and_only_its_unchanged_ini(string ini)
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "Expedition 33", "Content")).FullName;
        Directory.CreateDirectory(Path.Combine(content, "Engine"));
        var stub = Path.Combine(content, "SandFall.exe");
        File.WriteAllBytes(stub, DiscoveryAndVendorTests.Exe(null, 1000));
        var game = new Game("test:stub-" + Guid.NewGuid().ToString("N")[..8], "Expedition 33", Store.Other, content, stub, "1.0");
        var k = RecordKiller([game]);
        await k.ScanAsync(default);
        k.InstallRecorder(game.Id);
        var path = Path.Combine(content, "scskiller.ini");
        Assert.True(File.Exists(path));
        var rec = k.Store.LoadGame(game.Id);
        if (ini == "stale") rec.RecorderFiles["scskiller.ini"] = new string('0', 64);
        if (ini == "untracked") rec.RecorderFiles.Remove("scskiller.ini");
        k.Store.SaveGame(game.Id, rec);
        if (ini == "edited") File.AppendAllText(path, "frames=0\r\n");
        if (ini == "unicode") File.WriteAllText(path, File.ReadAllText(path) + "max_db_bytes=\u0661\r\n");

        var bin = Directory.CreateDirectory(Path.Combine(content, "Sandfall", "Binaries", "WinGDK")).FullName;
        File.WriteAllBytes(Path.Combine(bin, "SandFall-WinGDK-Shipping.exe"), DiscoveryAndVendorTests.Exe("d3d12.dll", 5000));
        await k.ScanAsync(default);
        var before = ini == "tracked" ? null : File.ReadAllBytes(path);
        using (ini == "locked" ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null)
            k.ReconcileRecorders();
        Assert.False(File.Exists(Path.Combine(content, "d3d12.dll")));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(bin, "d3d12.dll")));
        Assert.True(File.Exists(Path.Combine(bin, "scskiller.ini")));
        if (before == null) Assert.False(File.Exists(path));
        else Assert.Equal(before, File.ReadAllBytes(path));   // untouched
    }

    /// <summary>The uninstall hook never needs settings.json: one it can't read leaves no proxy in.</summary>
    [Fact]
    public async Task The_uninstall_hook_removes_the_proxy_with_settings_unreadable()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var settings = Path.Combine(k.Store.DataDir, "settings.json");
        if (!File.Exists(settings)) k.Store.SaveSettings(k.Settings);
        using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
            ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>A scan reuses the Shipping exe the last one found (discovered.json) without walking the install while the store
    /// names the same exe and the build and the install root are unchanged; a new build, a changed root or the user's refresh
    /// look again.</summary>
    [Fact]
    public async Task A_scan_with_nothing_changed_reuses_the_shipping_exe_without_walking_the_install()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "Dead Island 2", "Content")).FullName;
        Directory.CreateDirectory(Path.Combine(content, "Engine"));
        var stub = Path.Combine(content, "DeadIsland.exe");
        File.WriteAllBytes(stub, DiscoveryAndVendorTests.Exe(null, 1000));
        var bin = Directory.CreateDirectory(Path.Combine(content, "DeadIsland", "Binaries", "WinGDK")).FullName;
        var shipping = Path.Combine(bin, "DeadIsland-WinGDK-Shipping.exe");
        File.WriteAllBytes(shipping, DiscoveryAndVendorTests.Exe("d3d12.dll", 5000));
        var game = new Game("test:stub-" + Guid.NewGuid().ToString("N")[..8], "Dead Island 2", Store.Other, content, stub, "1.0");
        var source = new FakeSource([game]);
        var k = Killer(new FakeReader(Unreal), source: source);
        var walks = 0;
        k.ShippingLookup = (dir, exe) => { walks++; return GameFiles.GameExe(dir, exe); };
        await k.ScanAsync(default);
        Assert.Equal((shipping, 1), (k.Games.Single().Game.ExePath, walks));

        await k.ScanAsync(default);
        Assert.Equal((shipping, 1), (k.Games.Single().Game.ExePath, walks));

        source.Games = [game with { Version = "2.0" }];
        await k.ScanAsync(default);
        Assert.Equal(2, walks);
        File.WriteAllText(Path.Combine(content, "DeadIsland.pak"), "an update's file");
        await k.ScanAsync(default);
        Assert.Equal(3, walks);
        await k.ScanAsync(default, userRequested: true);
        Assert.Equal((shipping, 4), (k.Games.Single().Game.ExePath, walks));
    }

    [Fact]
    public async Task A_move_whose_import_fails_keeps_the_old_folder_until_it_is_imported()
    {
        var (install, bin, was, game) = await RecorderNextToALauncher();
        var k = RecordKiller([game]);
        await k.ScanAsync(default);
        using (new FileStream(Path.Combine(install, "scskiller.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            k.ReconcileRecorders();
        Assert.StartsWith("couldn't move", k.Games.Single().RecorderNote);
        Assert.Equal(was.ExePath, k.Store.LoadGame(game.Id).RecorderMoveFrom);
        Assert.False(File.Exists(Path.Combine(bin, "d3d12.dll")));

        k.ReconcileRecorders();
        Assert.Null(k.Games.Single().RecorderNote);
        Assert.False(File.Exists(Path.Combine(install, "scskiller.db")));
        Assert.Contains(Rec('a').Key, PsoDb.Read(Path.Combine(k.Store.GameDir(game.Id), "recording.db")).Select(r => r.Key));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(bin, "d3d12.dll")));
        Assert.Null(k.Store.LoadGame(game.Id).RecorderMoveFrom);
    }

    [Fact]
    public async Task A_move_waiting_for_the_recording_lock_sees_the_game_start_from_the_new_folder()
    {
        var (install, bin, was, game) = await RecorderNextToALauncher();
        IReadOnlySet<string> running = new HashSet<string>();
        var k = RecordKiller([game], running: () => running);
        await k.ScanAsync(default);
        Task move;
        using (Recordings.Lock(Path.Combine(k.Store.GameDir(game.Id), "recording.db")))
        {
            move = Task.Run(() => k.ReconcileRecorders());
            Assert.True(SpinWait.SpinUntil(() => !File.Exists(Path.Combine(install, "d3d12.dll")), TimeSpan.FromSeconds(10)));   // the proxy went, the import waits
            running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "w3fake" };
        }
        await move.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("moves when the game exits", k.Games.Single().RecorderNote);
        Assert.True(new FileInfo(Path.Combine(install, "scskiller.db")).Length > 0);   // neither emptied nor deleted while it runs
        Assert.False(File.Exists(Path.Combine(bin, "d3d12.dll")));
        Assert.Equal((was.ExePath, game.ExePath), (k.Store.LoadGame(game.Id).RecorderMoveFrom, k.Store.LoadGame(game.Id).RecorderMoveTo));
        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "w3fake" });   // the uninstall hook: it runs from the new folder
        Assert.True(new FileInfo(Path.Combine(install, "scskiller.db")).Length > 0);

        running = new HashSet<string>();
        k.ReconcileRecorders();
        Assert.False(File.Exists(Path.Combine(install, "scskiller.db")));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(bin, "d3d12.dll")));
        Assert.Equal((null, null), (k.Store.LoadGame(game.Id).RecorderMoveFrom, k.Store.LoadGame(game.Id).RecorderMoveTo));
    }

    [Fact]
    public async Task A_move_put_off_because_the_game_runs_is_guarded_by_the_uninstall_hook()
    {
        var (install, bin, was, game) = await RecorderNextToALauncher();
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "w3fake" };
        var k = RecordKiller([game], running: () => running);
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.Equal("moves when the game exits", k.Games.Single().RecorderNote);
        Assert.Equal((was.ExePath, game.ExePath), (k.Store.LoadGame(game.Id).RecorderMoveFrom, k.Store.LoadGame(game.Id).RecorderMoveTo));

        ScsKiller.RemoveAllRecorders(k.Store, running);
        foreach (var f in new[] { "d3d12.dll", "scskiller.ini", "scskiller.db" }) Assert.True(File.Exists(Path.Combine(install, f)), f);
        Assert.True(new FileInfo(Path.Combine(install, "scskiller.db")).Length > 0);
        Assert.False(File.Exists(Path.Combine(bin, "d3d12.dll")));
    }

    [Fact]
    public async Task Anti_cheat_found_while_the_moved_game_runs_leaves_the_uninstall_hook_nothing_to_touch()
    {
        var (install, bin, was, game) = await RecorderNextToALauncher();
        Directory.CreateDirectory(Path.Combine(install, "EasyAntiCheat"));
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "w3fake" };
        var k = RecordKiller([game], running: () => running);
        await k.ScanAsync(default);   // the removal waits for the game
        Assert.True(k.Store.LoadGame(game.Id).RecorderRollback);
        void Untouched()
        {
            ScsKiller.RemoveAllRecorders(k.Store, running);
            foreach (var f in new[] { "d3d12.dll", "scskiller.ini", "scskiller.db" }) Assert.True(File.Exists(Path.Combine(install, f)), f);
            Assert.True(new FileInfo(Path.Combine(install, "scskiller.db")).Length > 0);
        }
        Untouched();   // before any reconcile: the install's tree has w3fake.exe

        k.ReconcileRecorders();
        Assert.Equal((was.ExePath, game.ExePath), (k.Store.LoadGame(game.Id).RecorderMoveFrom, k.Store.LoadGame(game.Id).RecorderMoveTo));
        Untouched();
        var rec = k.Store.LoadGame(game.Id);
        (rec.RecorderMoveFrom, rec.RecorderMoveTo) = (null, null);   // whatever the move record says
        k.Store.SaveGame(game.Id, rec);
        Untouched();
        Assert.False(File.Exists(Path.Combine(bin, "d3d12.dll")));
    }

    [Fact]
    public async Task The_uninstall_hook_asks_what_runs_again_before_each_folder_s_writes()
    {
        var (install, _, _, game) = await RecorderNextToALauncher();   // recorded by the launcher, the game nested below
        var dll = Path.Combine(install, "d3d12.dll");
        // the game starts once the hook took the proxy out, before it deletes the data files
        ScsKiller.RemoveAllRecorders(RecordKiller([game]).Store,
            () => File.Exists(dll) ? new HashSet<string>() : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "w3fake" }, null);
        Assert.False(File.Exists(dll));
        Assert.True(new FileInfo(Path.Combine(install, "scskiller.db")).Length > 0);
        Assert.Contains("the game started: the recorder's data files left", RecordersLog());
    }

    [Fact]
    public void An_install_tree_read_past_the_deadline_counts_as_running()
    {
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "a", "b"));
        Assert.Contains("Fake-Win64-Shipping", ScsKiller.InstallExeNames(_game.InstallDir, () => false)!);
        Assert.Null(ScsKiller.InstallExeNames(_game.InstallDir, () => true));
    }

    [Fact]
    public async Task Anti_cheat_found_after_the_exe_moved_with_the_game_stopped_still_imports_and_cleans_the_old_folder()
    {
        var (install, bin, was, game) = await RecorderNextToALauncher();
        File.WriteAllText(Path.Combine(install, "scskiller_creates.csv"), "#session,1,w3fake.exe");
        Directory.CreateDirectory(Path.Combine(install, "EasyAntiCheat"));
        var k = RecordKiller([game]);
        await k.ScanAsync(default);   // taken out right away
        var rec = k.Store.LoadGame(game.Id);
        Assert.False(File.Exists(Path.Combine(install, "d3d12.dll")));
        Assert.Equal((null, was.ExePath, install), (rec.RecorderExe, rec.RecorderMoveFrom, rec.RecorderInstallDir));

        k.ReconcileRecorders();
        foreach (var f in new[] { "scskiller.db", "scskiller.ini", "scskiller_creates.csv" }) Assert.False(File.Exists(Path.Combine(install, f)), f);
        Assert.Contains(Rec('a').Key, PsoDb.Read(Path.Combine(k.Store.GameDir(game.Id), "recording.db")).Select(r => r.Key));
        Assert.Empty(Directory.GetFiles(bin).Where(f => Path.GetFileName(f).StartsWith("scskiller", StringComparison.OrdinalIgnoreCase) || f.EndsWith("d3d12.dll")));
        Assert.Null(k.Store.LoadGame(game.Id).RecorderMoveFrom);
    }

    [Fact]
    public async Task A_move_waiting_for_the_record_s_lock_sees_the_game_start()
    {
        var (install, bin, _, game) = await RecorderNextToALauncher();
        IReadOnlySet<string> running = new HashSet<string>();
        var k = RecordKiller([game], running: () => running);
        await k.ScanAsync(default);
        Task move;
        using (new FileStream(Path.Combine(k.Store.GameDir(game.Id), "state.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            move = Task.Run(() => k.ReconcileRecorders());
            Thread.Sleep(500);   // waiting to save the move's record
            running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "w3fake" };
        }
        await move.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("moves when the game exits", k.Games.Single().RecorderNote);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(install, "d3d12.dll")));
        Assert.True(new FileInfo(Path.Combine(install, "scskiller.db")).Length > 0);
        Assert.False(File.Exists(Path.Combine(bin, "d3d12.dll")));
    }

    [Fact]
    public async Task A_move_whose_recording_can_t_take_the_import_keeps_the_old_folder_until_it_can()
    {
        var (install, bin, was, game) = await RecorderNextToALauncher();
        var k = RecordKiller([game]);
        await k.ScanAsync(default);
        var store = Path.Combine(k.Store.GameDir(game.Id), "recording.db");
        PsoDb.WriteCompact(store, [Rec('b')]);
        File.WriteAllBytes(store, File.ReadAllBytes(store)[..^5]);   // truncated, its header kept
        k.ReconcileRecorders();
        Assert.Equal($"couldn't move: the recording in {install} couldn't be imported", k.Games.Single().RecorderNote);
        Assert.Equal(was.ExePath, k.Store.LoadGame(game.Id).RecorderMoveFrom);
        Assert.True(new FileInfo(Path.Combine(install, "scskiller.db")).Length > 0);
        Assert.False(File.Exists(Path.Combine(bin, "d3d12.dll")));

        File.Delete(store);   // repaired
        k.ReconcileRecorders();
        Assert.Null(k.Games.Single().RecorderNote);
        Assert.Null(k.Store.LoadGame(game.Id).RecorderMoveFrom);
        Assert.Contains(Rec('a').Key, PsoDb.Read(store).Select(r => r.Key));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(bin, "d3d12.dll")));
    }

    [Fact]
    public async Task A_recorder_from_another_SCSKiller_build_is_replaced_once_the_game_is_not_running()
    {
        var g = FakeGame("test:old", "Old");
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = RecordKiller([g], running: () => running);
        await k.ScanAsync(default);
        k.InstallRecorder(g.Id);
        var old = File.ReadAllBytes(Dll(g));

        File.WriteAllBytes(_proxy, [.. "MZ other build SCSKiller_StartWarm "u8, .. Guid.NewGuid().ToByteArray()]);
        k = RecordKiller([g], running: () => running);
        await k.ScanAsync(default);
        running.Add("Old");
        Assert.Contains("updates when the game exits", Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(g.Id)).Message);
        Assert.Equal(old, File.ReadAllBytes(Dll(g)));

        running.Clear();
        k.ReconcileRecorders(g.Id);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(Dll(g)));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_proxy))), k.Store.LoadGame(g.Id).RecorderFiles["d3d12.dll"]);
        Assert.Null(k.Games.Single().RecorderNote);
    }

    [Fact]
    public async Task A_program_elsewhere_named_like_an_exe_of_the_game_folder_is_not_the_game_running()
    {
        // Gaijin's agent ships in War Thunder's root and runs all the time from its own folder
        var g = FakeGame("test:agent", "Agent");
        File.WriteAllBytes(Path.Combine(g.InstallDir, "gjagent.exe"), new byte[64]);
        string? path = Path.Combine(_root, "NetAgent", "gjagent.exe");
        var k = RecordKiller([g], running: () => new HashSet<string> { "gjagent" });
        k.RunningGameExes = () => new HashSet<string>();
        k.Processes = _ => [(4242, 1, "gjagent.exe")];
        k.ProcessPath = _ => path;
        await k.ScanAsync(default);
        k.PollGames();
        Assert.False(k.IsPlaying(g.Id));
        k.InstallRecorder(g.Id);   // a write guard: not running either
        Assert.True(ScsKiller.IsOurProxy(Dll(g)));
        k.UninstallRecorder(g.Id);

        foreach (var where in new[] { Path.Combine(g.InstallDir, "gjagent.exe"), null })   // from the folder, or a path it can't tell
        {
            path = where;
            k.PollGames();
            Assert.True(k.IsPlaying(g.Id));
            for (int i = 0; i < ScsKiller.ExitPolls; i++) { k.Processes = _ => []; k.PollGames(); }
            Assert.False(k.IsPlaying(g.Id));
            k.Processes = _ => [(4242, 1, "gjagent.exe")];
            Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(g.Id));
        }
    }

    [Fact]
    public async Task A_process_in_a_junctioned_game_folder_is_the_game_running()
    {
        // the library is reached through a junction: the process's image path is the junction's target
        var real = Path.Combine(_root, "Real", "Junctioned");
        Directory.CreateDirectory(real);
        File.WriteAllBytes(Path.Combine(real, "Junctioned.exe"), new byte[64]);
        File.WriteAllBytes(Path.Combine(real, "Started.exe"), new byte[64]);
        var link = Path.Combine(_root, "Junctioned");
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            mk.WaitForExit();
        var g = new Game("test:junctioned", "Junctioned", Store.Other, link, Path.Combine(link, "Junctioned.exe"));
        var k = RecordKiller([g], running: () => new HashSet<string> { "Started" });
        k.RunningGameExes = () => new HashSet<string>();
        k.Processes = _ => [(4242, 1, "Started.exe")];
        k.ProcessPath = _ => Path.Combine(real, "Started.exe");
        await k.ScanAsync(default);
        k.PollGames();
        Assert.True(k.IsPlaying(g.Id));
        for (int i = 0; i < ScsKiller.ExitPolls; i++) { k.Processes = _ => []; k.PollGames(); }
        k.Processes = _ => [(4242, 1, "Started.exe")];
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(g.Id));
        Directory.Delete(link);
    }

    /// <summary>The app's start (Updater.ApplyAtStartAsync) asks this after its first scan, and the tray's Quit before and
    /// right before the handover: until a scan has listed the games, it can't tell, so it says one may run.</summary>
    [Fact]
    public async Task A_game_running_at_the_start_holds_an_automatic_install_back_once_the_scan_knows_it()
    {
        var g = FakeGame("test:running", "Running");
        var k = RecordKiller([g]);
        k.Processes = _ => [];
        Assert.True(k.GameRunning());   // before the first scan lists the games, a tray Quit can't tell: as if one runs
        await k.ScanAsync(default);
        Assert.False(k.GameRunning());
        k.Processes = _ => [(4242, 1, "Running.exe")];
        Assert.True(k.GameRunning());
        Assert.Equal(AutoInstall.Playing, AutoInstall.HeldBack(k.Settings, compiling: false, offline: false, playing: k.GameRunning()));
        k.Processes = _ => [];
        Assert.False(k.GameRunning());
    }

    [Fact]
    public async Task The_watcher_installs_a_waiting_recorder_when_the_game_exits()
    {
        var g = FakeGame("test:watched", "Watched");
        bool playing = true;
        var k = RecordKiller([g], running: () => playing ? new HashSet<string> { "Watched" } : new HashSet<string>());
        k.RunningGameExes = () => playing ? new HashSet<string> { "Watched.exe" } : new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);   // the scan's reconcile finds the game running: waits
        Assert.False(File.Exists(Dll(g)));
        Assert.Equal("installs when the game exits", k.Games.Single().RecorderNote);

        k.PollGames();
        Assert.True(k.IsPlaying(g.Id));
        playing = false;
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.False(k.IsPlaying(g.Id));
        Assert.True(ScsKiller.IsOurProxy(Dll(g)));
        Assert.Null(k.Games.Single().RecorderNote);
        Assert.True(k.Games.Single().RecorderInstalled);
    }

    [Fact]
    public async Task The_watcher_follows_the_exe_a_game_runs_and_moves_the_recorder_there_after_it_exits()
    {
        // discovery took the patcher's copy (Stellar Blade's PatchData): the recorder went next to an exe that never runs
        var root = Path.Combine(_root, "StellarBlade");
        string Exe(string dir) => Path.Combine(root, dir, "SB", "Binaries", "Win64", "SB-Win64-Shipping.exe");
        var (copy, real) = (Exe("PatchData"), Exe(""));
        foreach (var f in new[] { copy, real }) { Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, new byte[64]); }
        var game = new Game("steam:3489700", "Stellar Blade", Store.Steam, root, copy, "1");
        var playing = false;
        var k = RecordKiller([game], running: () => playing ? new HashSet<string> { "SB-Win64-Shipping" } : new HashSet<string>());
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        var (copyDll, realDll) = (Path.Combine(Path.GetDirectoryName(copy)!, "d3d12.dll"), Path.Combine(Path.GetDirectoryName(real)!, "d3d12.dll"));
        Assert.True(ScsKiller.IsOurProxy(copyDll));

        k.RunningGameExes = () => playing ? new HashSet<string> { "SB-Win64-Shipping.exe" } : new HashSet<string>();
        k.Processes = _ => playing ? [(4242, 1, "SB-Win64-Shipping.exe")] : [];
        k.ProcessPath = pid => pid == 4242 ? real : null;
        playing = true;
        k.PollGames();
        Assert.True(k.IsPlaying(game.Id));
        Assert.Equal(real, k.Store.LoadGame(game.Id).RunsExe);
        Assert.True(ScsKiller.IsOurProxy(copyDll));   // nothing touched while it runs
        Assert.False(File.Exists(realDll));

        playing = false;
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.Equal(real, k.Games.Single().Game.ExePath);
        Assert.False(File.Exists(copyDll));
        Assert.True(ScsKiller.IsOurProxy(realDll));
        Assert.Equal(real, k.Store.LoadGame(game.Id).RecorderExe);
        await AssertStaysArmed(k, game with { ExePath = real });   // armed when the move returns, not at the next watcher pass
        Assert.DoesNotContain(ScsKiller.LedgerFiles(copy), File.Exists);   // the exe it was followed away from
        Assert.Equal(real, (await k.ScanAsync(default)).Single().Game.ExePath);   // discovery still names the copy: the record wins

        // the copy armed again by something else (another install of it): its entries stay when the game is armed
        var gen = k.InstallGen(game);
        ScsKiller.WriteAttestation(copy);
        await Until(() => k.InstallGen(game) > gen && !k.DisarmQueued(game));   // the copy's armed file is a change in the install
        await k.ScanAsync(default);
        await AssertStaysArmed(k, game with { ExePath = real });
        Assert.Contains(ScsKiller.LedgerFiles(copy), File.Exists);
        k.StopWatchingInstalls();
    }

    [Fact]
    public async Task The_watcher_follows_no_exe_outside_the_install_nor_of_an_anti_cheat_game()
    {
        var g = FakeGame("test:followed", "Followed");
        var other = Path.Combine(_root, "Elsewhere", "Followed.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, new byte[64]);
        var k = RecordKiller([g]);
        await k.ScanAsync(default);
        k.RunningGameExes = () => new HashSet<string> { "Followed.exe" };
        k.Processes = _ => [(4242, 1, "Followed.exe")];
        k.ProcessPath = _ => other;   // a copy outside the install (another install, a staged warm)
        k.PollGames();
        Assert.True(k.IsPlaying(g.Id));
        Assert.Null(k.Store.LoadGame(g.Id).RunsExe);

        var ac = FakeGame("test:protected", "Protected");
        File.WriteAllBytes(Path.Combine(ac.InstallDir, "BEService_x64.exe"), [0]);
        var inside = Path.Combine(ac.InstallDir, "bin", "Protected.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllBytes(inside, new byte[64]);
        var opened = 0;
        k = RecordKiller([ac]);
        await k.ScanAsync(default);
        k.RunningGameExes = () => new HashSet<string> { "Protected.exe" };
        k.Processes = _ => [(4243, 1, "Protected.exe")];
        k.ProcessPath = _ => { opened++; return inside; };
        k.PollGames();
        Assert.True(k.IsPlaying(ac.Id));
        Assert.Equal(0, opened);   // no anti-cheat game's process is opened
        Assert.Null(k.Store.LoadGame(ac.Id).RunsExe);
    }

    [Fact]
    public async Task A_protected_game_sharing_the_exe_name_is_never_followed_into()
    {
        var clean = FakeGame("test:clean", "Game", "clean");
        var guarded = FakeGame("test:guarded", "Game", "guarded");
        File.WriteAllBytes(Path.Combine(guarded.InstallDir, "BEService_x64.exe"), [0]);
        var k = RecordKiller([clean, guarded]);
        await k.ScanAsync(default);
        var asked = new List<int>();
        k.RunningGameExes = () => new HashSet<string> { "Game.exe" };
        k.Processes = _ => [(4243, 1, "Game.exe")];   // the protected one runs: both are marked playing
        k.ProcessPath = pid => { asked.Add(pid); return guarded.ExePath; };
        k.PollGames();
        Assert.True(k.IsPlaying(clean.Id) && k.IsPlaying(guarded.Id));
        Assert.Null(k.Store.LoadGame(clean.Id).RunsExe);   // a process outside its install
        Assert.Null(k.Store.LoadGame(guarded.Id).RunsExe);
        Assert.Equal([4243], asked);   // asked once, for the clean game: a path from the process list, the process isn't opened
    }

    [Fact]
    public async Task Anti_cheat_added_since_the_scan_stops_the_watcher_from_following()
    {
        var g = FakeGame("test:patched", "Patched");
        var inside = Path.Combine(g.InstallDir, "bin", "Patched.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllBytes(inside, new byte[64]);
        var k = RecordKiller([g]);
        await k.ScanAsync(default);
        Assert.Equal(AntiCheat.None, k.Games.Single().AntiCheat);   // the cached verdict
        File.WriteAllBytes(Path.Combine(g.InstallDir, "BEService_x64.exe"), [0]);   // an update added it
        var asked = 0;
        k.RunningGameExes = () => new HashSet<string> { "Patched.exe" };
        k.Processes = _ => [(4244, 1, "Patched.exe")];
        k.ProcessPath = _ => { asked++; return inside; };
        k.PollGames();
        Assert.Equal(0, asked);
        Assert.Null(k.Store.LoadGame(g.Id).RunsExe);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("discovery")]
    [InlineData("gone")]
    public async Task A_followed_exe_is_dropped_when_the_game_changes(string change)
    {
        var g = FakeGame("test:moved", "Moved") with { Version = "1" };
        var inside = Path.Combine(g.InstallDir, "bin", "Moved.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllBytes(inside, new byte[64]);
        var source = new FakeSource([g]);
        var k = Killer(new FakeReader(Unreal), source: source);
        await k.ScanAsync(default);
        var playing = true;
        k.RunningGameExes = () => playing ? new HashSet<string> { "Moved.exe" } : new HashSet<string>();
        k.Processes = _ => playing ? [(4245, 1, "Moved.exe")] : [];
        k.ProcessPath = _ => inside;
        k.PollGames();
        playing = false;
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.Equal(inside, (await k.ScanAsync(default)).Single().Game.ExePath);

        var other = Path.Combine(g.InstallDir, "Other.exe");
        File.WriteAllBytes(other, new byte[64]);
        switch (change)
        {
            case "version": source.Games = [g with { Version = "2" }]; break;   // an update may have moved the launch target
            case "discovery": source.Games = [g with { ExePath = other }]; break;
            default: File.Delete(inside); break;
        }
        var now = (await k.ScanAsync(default)).Single().Game;
        Assert.Equal(source.Games[0].ExePath, now.ExePath);
        Assert.Null(k.Store.LoadGame(g.Id).RunsExe);
    }

    /// <summary>Two builds side by side (ACValhalla.exe and the Ubisoft+ ACValhalla_Plus.exe), and the one discovery didn't
    /// pick runs: the watcher follows it, the recorder stays and is armed for it (a check of the old exe that ends after
    /// that doesn't take it back), the compile key follows its name, and the page says why that launch wasn't recorded.
    /// The next launch records: the usual status. The first exe running again is followed back the same way.</summary>
    [Fact]
    public async Task The_watcher_follows_another_exe_of_the_folder_and_arms_the_recorder_for_it()
    {
        var dir = Path.Combine(_root, "Assassin's Creed Valhalla");
        Directory.CreateDirectory(dir);
        var (picked, plus) = (Path.Combine(dir, "ACValhalla.exe"), Path.Combine(dir, "ACValhalla_Plus.exe"));
        File.WriteAllBytes(picked, new byte[4096]);
        File.WriteAllBytes(plus, new byte[8192]);
        Assert.Equal(picked, Core.Games.GameFiles.FindExe(dir));   // not the larger Plus build
        var g = new Game("ubisoft:13504", "Assassin's Creed Valhalla", Store.Other, dir, picked);
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner(), game: g);
        string? running = null;
        k.ProcessNames = () => running == null ? new HashSet<string>() : new HashSet<string> { Path.GetFileNameWithoutExtension(running) };
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        var dll = Path.Combine(dir, "d3d12.dll");
        Assert.True(ScsKiller.IsOurProxy(dll));
        Assert.True(await ArmedOnceWritten(g));

        k.RunningGameExes = () => running == null ? new HashSet<string>() : new HashSet<string> { Path.GetFileName(running) };
        k.Processes = _ => running == null ? [] : [(4242, 1, Path.GetFileName(running)), (4243, 1, "upc.exe")];
        k.ProcessPath = pid => pid == 4242 ? running : Path.Combine(_root, "Ubisoft", "upc.exe");
        async Task<GameState> Launch(string exe, string? refusal, Func<Task>? whilePlaying = null)
        {
            k.PollGames();   // a poll before the launch: the run has a start
            running = exe;
            k.PollGames();
            // written during the run: a file's time is the system's coarse clock, which may trail the poll's
            var file = refusal != null ? ScsKiller.LedgerFile(exe) + ".refused" : Path.Combine(dir, "scskiller.log");
            if (refusal == null) File.Delete(ScsKiller.LedgerFile(exe) + ".refused");   // admitted: the proxy takes its last refusal away and logs
            File.WriteAllText(file, refusal != null ? $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {refusal}" : "loaded");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));
            if (whilePlaying != null) await whilePlaying();
            running = null;
            for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
            return k.Games.Single();
        }

        var stale = ScsKiller.LedgerFile(picked) + ".refused";
        File.WriteAllText(stale, "0 not armed");   // from a launch before the recorder was in
        // a check of the install for the exe armed then, still walking when the game exits
        var gate = new TaskCompletionSource();
        k.FullAntiCheatCheck = x => { if (x.ExePath == picked) gate.Task.Wait(HangGuard); return Core.Games.GameFiles.DetectAntiCheat(x); };
        Task? check = null;
        GameState s;
        try
        {
            s = await Launch(plus, "not armed", async () =>
            {
                Assert.Equal(plus, k.Store.LoadGame(g.Id).RunsExe);
                var gen = k.InstallGen(g);
                File.WriteAllBytes(Path.Combine(dir, "patch.dll"), [0]);
                await Until(() => k.InstallGen(g) > gen && !k.DisarmQueued(g));   // the watcher's event disarmed it
                check = k.CheckRecorderGames(false);
            });
        }
        finally { gate.TrySetResult(); }
        await check!;
        Assert.Equal(plus, s.Game.ExePath);
        Assert.True(ScsKiller.IsOurProxy(dll));
        await AssertStaysArmed(k, s.Game);
        Assert.DoesNotContain(ScsKiller.LedgerFiles(picked), File.Exists);   // nothing left of the exe it was followed away from
        Assert.False(File.Exists(stale));
        Assert.Contains("recorder armed for ACValhalla_Plus.exe", RecordersLog());
        Assert.Equal("ACValhalla_Plus.exe", ScsKiller.WarmExeName(s.Game, k.Store.LoadGame(g.Id)));   // AMD's cache key hashes the exe name
        Assert.Equal(GameStatus.NeedsRecording, s.Status);
        Assert.True(ScsKiller.NeverRecorded(s));
        Assert.Equal("The last launch ran ACValhalla_Plus.exe, not the exe the recorder was set up for. It is set up for it now: play again.", s.RecorderRefused);
        Assert.Null(Format.ShortNote(s));

        s = await Launch(plus, null);
        Assert.Equal(plus, s.Game.ExePath);
        Assert.Null(s.RecorderRefused);
        Assert.False(ScsKiller.NeverRecorded(s));
        Assert.Equal("Recorder on: play 5 minutes", Format.ShortNote(s));

        s = await Launch(picked, "not armed");
        Assert.Equal(picked, s.Game.ExePath);
        await AssertStaysArmed(k, s.Game);
        Assert.DoesNotContain(ScsKiller.LedgerFiles(plus), File.Exists);
        Assert.Equal("The last launch ran ACValhalla.exe, not the exe the recorder was set up for. It is set up for it now: play again.", s.RecorderRefused);
        k.StopWatchingInstalls();
    }

    [Theory]
    [InlineData("GameLauncher.exe")]
    [InlineData("EOSBootstrapper.exe")]
    [InlineData("CrashReportClient.exe")]
    [InlineData("vc_redist.x64.exe")]
    public async Task The_watcher_never_follows_a_launcher_or_helper_of_the_folder(string helper)
    {
        var g = FakeGame("test:sibling", "Sibling");
        var other = Path.Combine(g.InstallDir, helper);
        File.WriteAllBytes(other, new byte[4096]);
        var k = RecordKiller([g]);
        await k.ScanAsync(default);
        k.RunningGameExes = () => new HashSet<string>();
        k.Processes = _ => [(4242, 1, helper)];
        k.ProcessPath = _ => other;
        k.PollGames();
        Assert.True(k.IsPlaying(g.Id));
        Assert.Null(k.Store.LoadGame(g.Id).RunsExe);
    }

    /// <summary>A launcher of the game's folder runs first, which counts as the game starting, and starts the other build
    /// polls later: the watcher follows that build once it runs.</summary>
    [Fact]
    public async Task The_watcher_follows_an_exe_a_launcher_of_the_folder_starts_later()
    {
        var g = FakeGame("test:delayed", "Delayed");
        var (launcher, plus) = (Path.Combine(g.InstallDir, "GameLauncher.exe"), Path.Combine(g.InstallDir, "Delayed_Plus.exe"));
        File.WriteAllBytes(launcher, new byte[64]);
        File.WriteAllBytes(plus, new byte[64]);
        var k = RecordKiller([g]);
        await k.ScanAsync(default);
        List<(int Pid, int Parent, string Exe)> running = [];
        k.RunningGameExes = () => new HashSet<string>();
        k.Processes = _ => running;
        k.ProcessPath = pid => pid == 4242 ? launcher : pid == 4243 ? plus : null;
        k.PollGames();
        running = [(4242, 1, "GameLauncher.exe")];
        k.PollGames();
        k.PollGames();
        Assert.True(k.IsPlaying(g.Id));
        Assert.Null(k.Store.LoadGame(g.Id).RunsExe);
        running = [(4242, 1, "GameLauncher.exe"), (4243, 4242, "Delayed_Plus.exe")];
        k.PollGames();
        Assert.Equal(plus, k.Store.LoadGame(g.Id).RunsExe);
        running = [];
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.Equal(plus, k.Games.Single().Game.ExePath);
    }

    [Fact]
    public async Task An_anti_cheat_game_never_follows_another_exe_of_its_folder()
    {
        var g = FakeGame("test:guarded-sibling", "Guarded");
        File.WriteAllBytes(Path.Combine(g.InstallDir, "BEService_x64.exe"), [0]);
        var other = Path.Combine(g.InstallDir, "Guarded_Plus.exe");
        File.WriteAllBytes(other, new byte[4096]);
        var k = RecordKiller([g]);
        await k.ScanAsync(default);
        Assert.NotEqual(AntiCheat.None, k.Games.Single().AntiCheat);
        k.RunningGameExes = () => new HashSet<string>();
        k.Processes = _ => [(4242, 1, "Guarded_Plus.exe")];
        k.ProcessPath = _ => other;
        k.PollGames();
        Assert.True(k.IsPlaying(g.Id));
        Assert.Null(k.Store.LoadGame(g.Id).RunsExe);
    }

    [Fact]
    public void A_process_path_is_read_without_opening_it_and_mapped_to_its_drive()
    {
        Assert.Equal(Environment.ProcessPath, Core.Warming.ProcessTree.ImagePath(Environment.ProcessId), StringComparer.OrdinalIgnoreCase);
        (string, string)[] devices = [("C:", @"\Device\HarddiskVolume3"), ("D:", @"\Device\HarddiskVolume30")];
        Assert.Equal(@"D:\Games\a.exe", Core.Warming.ProcessTree.DosPath(@"\Device\HarddiskVolume30\Games\a.exe", devices));
        Assert.Equal(@"C:\a.exe", Core.Warming.ProcessTree.DosPath(@"\Device\HarddiskVolume3\a.exe", devices));
        Assert.Null(Core.Warming.ProcessTree.DosPath(@"\Device\Mup\server\a.exe", devices));
    }

    [Fact]
    public async Task A_game_the_user_added_follows_the_exe_it_runs_only_once_its_folder_is_confirmed()
    {
        var root = Path.Combine(_root, "Hand");
        var (picked, runs) = (Path.Combine(root, "Hand.exe"), Path.Combine(root, "bin", "Hand.exe"));
        foreach (var f in new[] { picked, runs }) { Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, new byte[64]); }
        var manual = Manual();
        manual.Add(new Core.Games.ManualEntry(picked, root, "Hand"));
        var playing = true;
        ScsKiller Start()
        {
            var k = Killer(new FakeReader(Unreal), sources: [manual]);
            k.RunningGameExes = () => playing ? new HashSet<string> { "Hand.exe" } : new HashSet<string>();
            k.Processes = _ => playing ? [(4242, 1, "Hand.exe")] : [];
            k.ProcessPath = _ => runs;
            return k;
        }
        var k = Start();
        await k.ScanAsync(default);
        var id = k.Games.Single().Game.Id;
        k.PollGames();
        Assert.Null(k.Store.LoadGame(id).RunsExe);   // not confirmed: its folder isn't the user's yet

        manual.Add(new Core.Games.ManualEntry(picked, root, "Hand", Confirmed: true));
        k = Start();
        await k.ScanAsync(default);
        k.PollGames();
        Assert.Equal(runs, k.Store.LoadGame(id).RunsExe);
        playing = false;
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.Equal((id, runs), (k.Games.Single().Game.Id, k.Games.Single().Game.ExePath));   // the same game, its exe followed
    }

    [Fact]
    public async Task An_existing_recorder_install_becomes_an_On_override()
    {
        var mine = FakeGame("test:mine", "Mine");
        var plain = FakeGame("test:plain", "Plain");
        var k = RecordKiller([mine, plain]);
        File.Copy(_proxy, Dll(mine));   // a record without an override (not migrated)
        var old = k.Store.LoadGame(mine.Id);
        old.RecorderFiles["d3d12.dll"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Dll(mine))));
        k.Store.SaveGame(mine.Id, old);
        Assert.Null(k.Store.LoadGame(mine.Id).Recorder);
        k.Settings = k.Settings with { RecordAllGames = false };
        await k.ScanAsync(default);

        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Dll(mine)));   // the global is off, the user's own install stays
        Assert.Equal(RecorderOverride.On, k.Store.LoadGame(mine.Id).Recorder);
        Assert.Equal(RecorderOverride.Default, k.Store.LoadGame(plain.Id).Recorder);
        Assert.False(File.Exists(Dll(plain)));

        k.Settings = k.Settings with { RecordAllGames = true };
        k.ReconcileRecorders();
        k.Settings = k.Settings with { RecordAllGames = false };
        k.ReconcileRecorders();   // installed by the default: not migrated to On a second time
        Assert.False(File.Exists(Dll(plain)));
        Assert.Equal(RecorderOverride.Default, k.Store.LoadGame(plain.Id).Recorder);
    }

    [Fact]
    public void Start_with_Windows_writes_the_Run_value_only_when_it_differs_and_removes_it_when_off()
    {
        string? value = null;   // the HKCU Run value, faked
        int writes = 0;
        bool Apply(bool on, string exe) => WindowsStartup.Apply(on, exe, () => value, v => (value, writes) = (v, writes + 1));
        Assert.True(Apply(true, @"C:\Apps\SCSKiller\SCSKiller.exe"));
        Assert.Equal("\"C:\\Apps\\SCSKiller\\SCSKiller.exe\" --tray", value);
        Assert.False(Apply(true, @"C:\Apps\SCSKiller\SCSKiller.exe"));   // every start: nothing to do
        Assert.True(Apply(true, @"D:\Moved\SCSKiller.exe"));             // the app moved: the value follows
        Assert.True(Apply(false, @"D:\Moved\SCSKiller.exe"));
        Assert.Null(value);
        Assert.False(Apply(false, @"D:\Moved\SCSKiller.exe"));
        Assert.Equal(3, writes);
    }

    [Fact]
    public void Closing_the_window_quits_only_with_the_setting_on_and_never_twice()
    {
        Assert.False(AppStore.DefaultSettings.CloseQuits);   // off by default, also for a settings.json from before the setting:
        var store = new AppStore(Path.Combine(_root, "data"));
        store.SaveSettings(AppStore.DefaultSettings with { CloseQuits = true });
        Assert.True(store.LoadSettings().CloseQuits);
        var file = Path.Combine(_root, "data", "settings.json");
        File.WriteAllText(file, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @",\s*""CloseQuits"": true", ""));
        Assert.DoesNotContain("CloseQuits", File.ReadAllText(file));
        Assert.False(store.LoadSettings().CloseQuits);

        var on = AppStore.DefaultSettings with { CloseQuits = true };
        Assert.Equal(CloseAction.Hide, WindowClose.Of(AppStore.DefaultSettings, quitting: false, trayAdded: true));
        Assert.Equal(CloseAction.Close, WindowClose.Of(AppStore.DefaultSettings, quitting: false, trayAdded: false));
        Assert.Equal(CloseAction.Quit, WindowClose.Of(on, quitting: false, trayAdded: true));
        Assert.Equal(CloseAction.Quit, WindowClose.Of(on, quitting: false, trayAdded: false));
        // the window shown again while the quit waits for the driver's cache write: closing hides it, never "Quit now"
        Assert.Equal(CloseAction.Hide, WindowClose.Of(on, quitting: true, trayAdded: true));
        Assert.Equal(CloseAction.Close, WindowClose.Of(on, quitting: true, trayAdded: false));
    }

    /// <summary>window.json sits beside settings.json; none, a damaged one or one without a size opens at the default size.</summary>
    [Fact]
    public void The_window_placement_round_trips_beside_the_settings()
    {
        var store = new AppStore(Path.Combine(_root, "data"));
        Assert.Null(store.LoadWindow());
        var saved = new WindowBounds(-1900, 40, 1500, 900, true);
        store.SaveWindow(saved);
        Assert.Equal(saved, store.LoadWindow());
        var file = Path.Combine(store.DataDir, "window.json");
        Assert.True(File.Exists(file));
        File.WriteAllText(file, "{ \"Left\": 10, \"Top\"");
        Assert.Null(store.LoadWindow());
        store.SaveWindow(saved with { Width = 0 });
        Assert.Null(store.LoadWindow());

        Assert.True(WindowBounds.WasMaximized(3, 0));    // SW_SHOWMAXIMIZED
        Assert.True(WindowBounds.WasMaximized(2, 2));    // minimized from maximized
        Assert.False(WindowBounds.WasMaximized(2, 0));   // minimized from restored
        Assert.False(WindowBounds.WasMaximized(1, 2));   // SW_SHOWNORMAL
    }

    [Fact]
    public void Settings_and_game_state_round_trip()
    {
        var store = new AppStore(Path.Combine(_root, "data"));
        Assert.Equal(AppStore.DefaultSettings, store.LoadSettings());
        Assert.Equal(new Settings(Environment.ProcessorCount, WarmPriority.BelowNormal, DriverUpdateMode.Ask, 8, true),
            AppStore.DefaultSettings);
        Assert.False(AppStore.DefaultSettings.ShareRecordings);   // opt-in: off until the user ticks it
        Assert.False(AppStore.DefaultSettings.SharePromptDismissed);
        Assert.False(AppStore.DefaultSettings.TrayNoticeShown);
        var custom = new Settings(12, WarmPriority.Idle, DriverUpdateMode.WhenIdle, 4, false, true, MaximumPlans: true, TrayNoticeShown: true);
        store.SaveSettings(custom);
        Assert.Equal(custom, store.LoadSettings());
        Assert.Contains("\"WhenIdle\"", File.ReadAllText(Path.Combine(_root, "data", "settings.json")));
        Assert.True(custom.StartWithWindows);   // on by default, also for a settings.json from before the setting:
        File.WriteAllText(Path.Combine(_root, "data", "settings.json"), System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(_root, "data", "settings.json")), @",\s*""StartWithWindows"": true", ""));
        Assert.DoesNotContain("StartWithWindows", File.ReadAllText(Path.Combine(_root, "data", "settings.json")));
        Assert.True(store.LoadSettings().StartWithWindows);
        // the community database is on by default, also for a settings.json that saved the old, never settable CommunityPlans
        File.WriteAllText(Path.Combine(_root, "data", "settings.json"), File.ReadAllText(Path.Combine(_root, "data", "settings.json"))
            .Replace("\"UseCommunityDb\": true", "\"CommunityPlans\": false"));
        Assert.Contains("CommunityPlans", File.ReadAllText(Path.Combine(_root, "data", "settings.json")));
        Assert.True(store.LoadSettings().UseCommunityDb);

        var plan = new Plan("steam:1", "hash", "PCD3D_SM6", "nvidia-1", new PlanStats(10, 20, 3, 4, true), @"C:\x\plan.bin");
        var rec = new GameRecord
        {
            IndexContentHash = "hash", ShaderCount = 5, Plan = plan, PlanBuiltAt = DateTimeOffset.Now, ResumeAt = 7,
            WarmedDriverVersion = "610.88", WarmedAt = DateTimeOffset.Now, LastWarmTime = TimeSpan.FromSeconds(288.5), BytesPerPso = 24576.5,
            RecorderFiles = { ["d3d12.dll"] = "ABC" },
        };
        store.SaveGame("steam:1", rec);
        Assert.True(File.Exists(Path.Combine(_root, "data", "games", "steam_1", "state.json")));
        var back = store.LoadGame("steam:1");
        Assert.Equal(plan, back.Plan);
        Assert.Equal((rec.WarmedAt, rec.LastWarmTime, rec.ResumeAt, rec.BytesPerPso), (back.WarmedAt, back.LastWarmTime, back.ResumeAt, back.BytesPerPso));
        Assert.Equal("ABC", back.RecorderFiles["d3d12.dll"]);
        Assert.Null(store.LoadGame("steam:2").Plan);
    }

    /// <summary>The portable zip keeps its data in data\ beside SCSKiller.exe (public #50). Its first start copies what an
    /// earlier portable build kept in %LOCALAPPDATA%\SCSKiller, all but the recorder's ledger, also with no settings.json
    /// (a CLI-only user), and leaves that folder as it was. Starts at once copy it once, and a failed copy is redone whole.
    /// A plan the copy names by its old path is read from the game's new folder.</summary>
    [Fact]
    public void The_portable_build_keeps_its_data_beside_the_exe()
    {
        var root = Path.Combine(_root, "portable");
        Directory.CreateDirectory(Path.Combine(root, "current", "cli"));
        Assert.Null(AppStore.PortableRoot(Path.Combine(root, "current")));
        File.WriteAllText(Path.Combine(root, ".portable"), "");
        Assert.Equal(root, AppStore.PortableRoot(Path.Combine(root, "current") + Path.DirectorySeparatorChar));
        Assert.Equal(root, AppStore.PortableRoot(Path.Combine(root, "current", "cli")));

        var local = Path.Combine(_root, "local");
        var old = new AppStore(local);
        var planFile = Path.Combine(old.GameDir("steam:1"), "plan.bin");
        old.SaveGame("steam:1", new GameRecord { Plan = new Plan("steam:1", "hash", "PCD3D_SM6", "nvidia-1", new PlanStats(1, 2, 0, 0, true), planFile) });
        File.WriteAllText(planFile, "plan");
        Directory.CreateDirectory(Path.Combine(local, "armed"));
        File.WriteAllText(Path.Combine(local, "armed", "key"), "");

        var got = new string[4];
        Parallel.For(0, got.Length, i => got[i] = AppStore.Resolve(local, root, out _));   // the app and the CLI started together
        var data = Path.Combine(root, "data");
        Assert.All(got, d => Assert.Equal(data, d));
        Assert.True(File.Exists(Path.Combine(data, AppStore.MigratedFile)));
        Assert.False(Directory.Exists(data + ".partial"));
        var store = new AppStore(data);
        Assert.Equal(Path.Combine(store.GameDir("steam:1"), "plan.bin"), store.LoadGame("steam:1").Plan!.FilePath);
        Assert.False(Directory.Exists(Path.Combine(data, "armed")));
        Assert.True(File.Exists(planFile) && File.Exists(Path.Combine(local, "armed", "key")));
        Assert.Equal(planFile, old.LoadGame("steam:1").Plan!.FilePath);

        old.SaveSettings(AppStore.DefaultSettings with { BackgroundThreads = 5 });   // copied once only
        Assert.Equal(data, AppStore.Resolve(local, root, out var none));
        Assert.Null(none);
        Assert.False(File.Exists(Path.Combine(data, "settings.json")));

        // a copy that fails midway leaves the old folder in use for that run. While it runs, another start stays there too:
        // a copy then would leave that run's later saves behind. After it, the next start copies the old folder whole again,
        // with what that run saved there, and an older data\ without the marker is moved aside, not used.
        var other = Path.Combine(_root, "portable-2");
        var unmarked = Directory.CreateDirectory(Path.Combine(other, "data")).FullName;
        new AppStore(unmarked).SaveSettings(AppStore.DefaultSettings with { BackgroundThreads = 3 });
        IDisposable? onOld;
        using (File.Open(planFile, FileMode.Open, FileAccess.Read, FileShare.None))   // a file the copy can't read
            Assert.Equal(local, AppStore.Resolve(local, other, out onOld));
        Assert.NotNull(onOld);
        Assert.False(Directory.Exists(unmarked + ".partial"));
        old.SaveSettings(AppStore.DefaultSettings with { BackgroundThreads = 7 });
        Assert.Equal(local, AppStore.Resolve(local, other, out var alsoOld));
        Assert.False(File.Exists(Path.Combine(unmarked, AppStore.MigratedFile)));
        alsoOld?.Dispose();
        onOld!.Dispose();
        Assert.Equal(unmarked, AppStore.Resolve(local, other, out none));
        Assert.Null(none);
        Assert.Equal(7, new AppStore(unmarked).LoadSettings().BackgroundThreads);
        Assert.True(File.Exists(Path.Combine(new AppStore(unmarked).GameDir("steam:1"), "plan.bin")) && File.Exists(Path.Combine(unmarked, AppStore.MigratedFile)));
        Assert.Equal(3, new AppStore(unmarked + ".stale-1").LoadSettings().BackgroundThreads);

        // a portable folder that can't be written keeps %LOCALAPPDATA%\SCSKiller
        var file = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(file, "");
        Assert.Equal(local, AppStore.Resolve(local, file, out var onLocal));
        onLocal?.Dispose();

        // another process copying for longer than this one waits (an update's hooks must be quick): the old folder, this run
        var slow = Path.Combine(_root, "portable-3");
        Directory.CreateDirectory(slow);
        using var taken = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var copier = new Thread(() =>
        {
            using var m = new Mutex(false, AppStore.CopyMutex(slow));
            m.WaitOne();
            taken.Set();
            done.Wait();
            m.ReleaseMutex();
        });
        copier.Start();
        taken.Wait();
        var wait = AppStore.CopyWait;
        AppStore.CopyWait = TimeSpan.FromMilliseconds(200);
        try
        {
            Assert.Equal(local, AppStore.Resolve(local, slow, out var waited));
            Assert.NotNull(waited);
            waited!.Dispose();
        }
        finally
        {
            AppStore.CopyWait = wait;
            done.Set();
            copier.Join();
        }
    }

    /// <summary>A build that left most stage sets out (no root signature covers them: Hogwarts Legacy without a recording) keeps
    /// the game compilable, and its status says so and what adds the rest, from the last build's stats (state.json).</summary>
    [Fact]
    public async Task A_partial_plan_says_what_it_compiles_and_that_a_recording_adds_the_rest()
    {
        var partial = new PlanStats(0, 41_422, 41_422, 105, false, Uncovered: 182_857);
        var k = Killer(new FakeReader(Unreal), new FakePlanner(stats: partial));
        await k.ScanAsync(default);
        Assert.Equal("synthesized templates", k.Games.Single().StatusReason);   // before a build nothing is known
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var s = k.Games.Single();
        Assert.Equal(GameStatus.Warmed, s.Status);   // the plan it has still compiles
        Assert.Equal(182_857, s.Plan!.Uncovered);
        Assert.Contains($"; compiles {41_422:N0} pipelines; a 5-minute recording lets SCSKiller rebuild the rest", s.StatusReason);

        var rec = k.Store.LoadGame(_game.Id);   // planned, not warmed yet: Ready with the same note
        rec.WarmedAt = null;
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.Ready, ScsKiller.PartialNote(partial)), (k.Games.Single().Status, k.Games.Single().StatusReason));

        Assert.False(ScsKiller.IsPartial(new PlanStats(0, 417_154, 417_154, 202, true, Uncovered: 6)));    // a handful left out: a full plan
        Assert.False(ScsKiller.IsPartial(new PlanStats(0, 900, 900, 5, true, Uncovered: 100)));           // 10%: not above
        Assert.True(ScsKiller.IsPartial(new PlanStats(0, 900, 900, 5, true, Uncovered: 101)));
        Assert.Contains($"{182_857:N0} more shader combinations", ScsKiller.PartialNote(partial with { Recorded = 500 }));   // recorded: no recording to ask for
    }

    /// <summary>A game whose ray tracing the plan can't compile without a recording (AMD, or an engine without a collection
    /// rule) doesn't look Ready after its build: before a compile it needs a recording, with one plain reason, and says when
    /// the community database has no entry for its build; compiling the rest stays possible (a partial compile). Once that
    /// compile ran it is Warmed, the recording still asked for; a newer driver makes it stale, and anti-cheat blocks the ask.</summary>
    [Fact]
    public async Task Ray_tracing_the_plan_cannot_compile_makes_the_game_need_a_recording()
    {
        var rt = new PlanStats(0, 40_000, 40_000, 100, true, RtLibraries: 12_000, RtUncovered: 12_000, RtInline: 0);
        var k = Killer(new FakeReader(Unreal), new FakePlanner(stats: rt));
        await k.ScanAsync(default);
        Assert.Equal(GameStatus.Ready, k.Games.Single().Status);   // before a build nothing is known
        Directory.CreateDirectory(Path.Combine(_root, "data", "community"));
        File.WriteAllBytes(Path.Combine(_root, "data", "community", "manifest.bin"), CommunityTests.Manifest());   // fetched, no entry for it
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);   // the rest compiled
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, true, "Ray tracing needs a 5-min recording"), (s.Status, ScsKiller.RtAfterRecording(s), Format.ShortNote(s)));
        Assert.EndsWith("; " + ScsKiller.RtAfterRecordingNote, s.StatusReason);

        var rec = k.Store.LoadGame(_game.Id);   // planned, not compiled yet
        rec.WarmedAt = null;
        k.Store.SaveGame(_game.Id, rec);
        s = (await k.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.NeedsRecording, ScsKiller.RtNote(false)), (s.Status, s.StatusReason));
        Assert.Equal(false, s.InCommunityDb);
        Assert.Contains("not in the community database yet", s.StatusReason);
        Assert.Equal("Ray-traced effects need one short recording; " + ScsKiller.InDbNote, ScsKiller.RtNote(true));   // the database named once

        Assert.False(ScsKiller.NeedsRtRecording(rt with { RtUncovered = 1_200 }));   // 10%: not above
        Assert.True(ScsKiller.NeedsRtRecording(rt with { RtUncovered = 1_201 }));
        Assert.False(ScsKiller.NeedsRtRecording(rt with { RtLibraries = 0, RtUncovered = 0 }));   // no ray tracing shaders

        await WarmOnce(k, _game.Id);
        var newer = Killer(new FakeReader(Unreal), new FakePlanner(stats: rt), driver: "200.02");
        s = (await newer.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Stale, false), (s.Status, ScsKiller.RtAfterRecording(s)));

        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "EasyAntiCheat"));
        s = (await k.RescanAsync(default)).Single();
        Assert.Equal((AntiCheat.EasyAntiCheat, GameStatus.Warmed, false), (s.AntiCheat, s.Status, ScsKiller.RtAfterRecording(s)));
        Assert.Contains("; ray-traced effects aren't compiled: they need a recording, which EasyAntiCheat blocks", s.StatusReason);
    }

    /// <summary>Unreal 5 whose shaders trace rays inline: its DXIL libraries don't make it need a recording, compiled or
    /// not, and the reason says what only a recording adds. A plan from before inline ray tracing was counted is planned
    /// again by the plan check, asking for nothing meanwhile.</summary>
    [Fact]
    public async Task Unreal_5_inline_ray_tracing_needs_no_recording_for_its_libraries()
    {
        var rt = new PlanStats(0, 40_000, 40_000, 100, true, RtLibraries: 10_278, RtUncovered: 10_278, RtInline: 1_193);
        var stats = rt;
        var reader = new FakeReader(Unreal with { Version = "5.6" });
        var k = Killer(reader, new FakePlanner(statsFor: _ => stats));
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var s = k.Games.Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        Assert.EndsWith("; " + ScsKiller.RtInlineNote, s.StatusReason);
        Assert.Equal((false, true), (ScsKiller.NeedsRtRecording(s), ScsKiller.RtInlineCovers(s.Plan)));

        var rec = k.Store.LoadGame(_game.Id);   // built before RtInline was counted
        rec.Plan = rec.Plan! with { Stats = rt with { RtInline = null } };
        rec.WarmedAt = null;
        k.Store.SaveGame(_game.Id, rec);
        k.CheckPlans = true;
        k.IdleTime = () => TimeSpan.FromHours(1);   // the plan check runs while the PC is idle
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        s = k.Games.Single();
        Assert.True(k.Queue.Single().PlanCheck);
        Assert.Equal((GameStatus.Ready, (long?)1_193), (s.Status, s.Plan!.RtInline));
        Assert.EndsWith("; " + ScsKiller.RtInlineNote, s.StatusReason);
        Assert.Equal("Path tracing needs a recording", Format.ShortNote(s));
    }

    /// <summary>A plan that asks for a ray tracing recording, then a detection that says the game builds no state object
    /// (its config turns ray tracing off): the plan check plans it again, asking for nothing meanwhile.</summary>
    [Fact]
    public async Task Ray_tracing_off_in_the_config_plans_again_without_a_recording()
    {
        var rt = new PlanStats(0, 40_000, 40_000, 100, true, RtLibraries: 7, RtUncovered: 7, RtInline: 0);
        var on = Killer(new FakeReader(Unreal with { Version = "5.1" }), new FakePlanner(stats: rt));
        await on.ScanAsync(default);
        await WarmOnce(on, _game.Id);
        var rec = on.Store.LoadGame(_game.Id);
        rec.WarmedAt = null;
        on.Store.SaveGame(_game.Id, rec);
        Assert.Equal(GameStatus.NeedsRecording, (await on.RescanAsync(default)).Single().Status);

        var off = Killer(new FakeReader(Unreal with { Version = "5.1", NoRtPipelines = true }), new FakePlanner(stats: rt with { RtUncovered = 0 }));
        off.CheckPlans = true;
        off.IdleTime = () => TimeSpan.Zero;   // the user is at the PC: the plan check waits
        var s = (await off.RescanAsync(default)).Single();
        Assert.Equal((GameStatus.Ready, true, false), (s.Status, s.RtToPlan, ScsKiller.NeedsRtRecording(s)));
        Assert.True(off.Queue.Single().PlanCheck);
        Assert.Equal("Checking 1 game for more to compile (while idle)", ScsKiller.PlanCheckLine(off.Queue));

        off.IdleTime = () => TimeSpan.FromHours(1);
        await off.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        s = off.Games.Single();
        Assert.Equal((GameStatus.Ready, false, 0L), (s.Status, s.RtToPlan, s.Plan!.RtUncovered));
    }

    /// <summary>Unreal 4 (any engine but Unreal 5) with a plan from before inline ray tracing was counted: as before, no plan check.</summary>
    [Fact]
    public async Task An_older_plan_of_another_engine_keeps_asking_for_a_ray_tracing_recording()
    {
        var rt = new PlanStats(0, 40_000, 40_000, 100, true, RtLibraries: 10_278, RtUncovered: 10_278);
        var ue4 = Killer(new FakeReader(Unreal), new FakePlanner(stats: rt));
        ue4.IdleTime = () => TimeSpan.FromHours(1);
        await ue4.ScanAsync(default);
        await WarmOnce(ue4, _game.Id);
        ue4.CheckPlans = true;
        await ue4.ScanAsync(default);
        var s = ue4.Games.Single();
        Assert.Null(s.Plan!.RtInline);
        Assert.Equal((GameStatus.Warmed, false, true), (s.Status, s.RtToPlan, ScsKiller.RtAfterRecording(s)));
        Assert.DoesNotContain(ue4.Queue, q => q.PlanCheck);
    }

    /// <summary>Unreal 5's Lumen: hardware ray tracing traces rays inline (RayQuery PSOs, no state object), software Lumen
    /// none at all. A recorded launch's import plans the recording without a compile (the app's plan check): inline ray
    /// tracing then covers it; a launch of 5 minutes without any makes the game Ready with a note. Until then the status
    /// asks for nothing, a shorter launch still asks, and a later one with a state object brings the ask back until a
    /// compile plans it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_recorded_launch_ends_the_ray_tracing_ask_without_a_compile(bool hardwareLumen)
    {
        var rt = new PlanStats(0, 40_000, 40_000, 100, true, RtLibraries: 12_000, RtUncovered: 12_000, RtInline: 0);
        var planner = new FakePlanner(statsFor: r => r != null && hardwareLumen ? rt with { RtUncovered = 0 } : rt);   // PlanBuilder's inlineOnly
        var k = Killer(new FakeReader(Unreal), planner);
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var rec = k.Store.LoadGame(_game.Id);   // planned, not warmed: what a compile stopped after its plan leaves
        rec.WarmedAt = null;
        k.Store.SaveGame(_game.Id, rec);
        var csv = Path.Combine(_exeDir, "scskiller_creates.csv");
        const long at = 1_700_000_000_000;
        void Launch(double minutes, params string[] creates) => File.AppendAllLines(csv,
            [$"#session,{at},Fake-Win64-Shipping.exe", "#clock,0.0", .. creates, $"#end,{at + (long)(minutes * 60_000)},{minutes * 60_000:0.0}"]);

        Launch(2, "20.0,G,0,0,1.0");
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.NeedsRecording, ScsKiller.RtNeedsRecording), (s.Status, s.StatusReason[..ScsKiller.RtNeedsRecording.Length]));
        Assert.False(ScsKiller.RecordedEnough(s));

        k.CheckPlans = true;
        k.IdleTime = () => TimeSpan.Zero;   // the user is at the PC: the plan check waits
        Launch(12, "20.0,G,0,0,1.0", "600000.0,C,0,0,9.0");
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db")))   // the launch's recording, imported by the evaluation
            PsoDb.Write(f, 'C', new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, new string('a', 40))).Payload);
        await k.ScanAsync(default);
        s = k.Games.Single();
        Assert.NotNull(k.Store.LoadGame(_game.Id).RecordingImportedAt);
        Assert.Equal((GameStatus.Ready, true, false), (s.Status, s.RtToPlan, ScsKiller.NeedsRtRecording(s)));   // nothing asked while its plan waits
        Assert.True(k.Queue.Single().PlanCheck);
        Assert.True(ScsKiller.RecordedEnough(s));
        Assert.Equal("X", Format.ShortNote(s with { Status = GameStatus.NeedsRecording, RecorderInstalled = true, StatusReason = "x" }));   // what's missing, not 5 minutes again

        k.IdleTime = () => TimeSpan.FromHours(1);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, false, false), (s.Status, s.RtToPlan, ScsKiller.NeedsRtRecording(s)));
        if (hardwareLumen)
        {
            Assert.Equal((0L, false), (s.Plan!.RtUncovered, s.RtUnseen));   // covered by the recording
            Assert.DoesNotContain(ScsKiller.RtUnseenNote, s.StatusReason);
            return;
        }
        Assert.Equal((12_000L, true), (s.Plan!.RtUncovered, s.RtUnseen));   // the page's count stays honest
        Assert.EndsWith("; " + ScsKiller.RtUnseenNote, s.StatusReason);
        Assert.Equal("No ray tracing seen while recording", Format.ShortNote(s));

        Launch(1, "20.0,G,0,0,1.0");   // a later short launch: 5 minutes were recorded already
        await k.ScanAsync(default);
        Assert.True(ScsKiller.RecordedEnough(k.Games.Single()));

        File.Delete(csv);   // the recorder taken out with its session files: still known
        await k.ScanAsync(default);
        Assert.Equal(GameStatus.Ready, k.Games.Single().Status);

        Launch(1, "20.0,R,0,0,40.0");   // a state object: the plan has none of it yet
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.NeedsRecording, ScsKiller.RtNeedsRecording), (k.Games.Single().Status, k.Games.Single().StatusReason[..ScsKiller.RtNeedsRecording.Length]));
    }

    /// <summary>Coverage: the stage sets found minus the ones left out, as the detail page shows it (floored, 100 only when
    /// nothing is left out); none for a plan without the count (DirectX 11 only, or built before it).</summary>
    [Fact]
    public void Coverage_is_the_found_stage_sets_the_plan_compiles()
    {
        Assert.Null(ScsKiller.Coverage(null));
        Assert.Null(ScsKiller.CoveragePercent(new PlanStats(0, 900, 900, 5, true)));   // StageSets 0: not counted
        Assert.Equal(0.25, ScsKiller.Coverage(new PlanStats(0, 1, 1, 1, true, StageSets: 4, LeftOut: 3)));
        Assert.Equal(18, ScsKiller.CoveragePercent(new PlanStats(0, 41_422, 41_422, 105, false, Uncovered: 182_857, StageSets: 224_279, LeftOut: 182_857)));
        Assert.Equal(99, ScsKiller.CoveragePercent(new PlanStats(0, 1, 1, 1, true, StageSets: 100_000, LeftOut: 1)));   // 99.999%: never "100" with one left out
        Assert.Equal(100, ScsKiller.CoveragePercent(new PlanStats(0, 1, 1, 1, true, StageSets: 7, LeftOut: 0)));
    }

    [Fact]
    public async Task Skipped_psos_are_counted_apart_from_failed_in_the_progress_the_done_note_and_the_game_state()
    {
        var warmer = new FakeWarmer(failed: 3);
        var progress = new List<WarmProgress>();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(skipped: 50), warmer);
        k.QueueChanged += q => { if (q.Progress is { } p) lock (progress) progress.Add(p); };
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));

        var done = k.Queue.Single();
        Assert.Equal(QueueStage.Done, done.Stage);
        Assert.Equal((10000L, 3L, 50L), (done.Progress!.Done, done.Progress.Failed, done.Progress.Skipped));   // skipped: not in Total, not failed
        Assert.Equal("3 failed (the driver rejected them), 50 skipped (a shader not in this install)", done.Note);
        lock (progress) Assert.All(progress, p => Assert.Equal(50, p.Skipped));   // the warm's own lines carry it too
        var s = k.Games.Single();
        Assert.Equal((3L, 50L), (s.LastWarmFailed, s.LastWarmSkipped));
        Assert.Equal((3L, 50L), (k.Store.LoadGame(_game.Id).LastWarmFailed, k.Store.LoadGame(_game.Id).LastWarmSkipped));

        Assert.Equal(" 10000/10000 (3 failed, 50 skipped: not in this install) 1000/s - 3 failed (the driver rejected them), 50 skipped (a shader not in this install)",
            ScsKiller.ProgressText(done with { Progress = done.Progress with { PerSecond = 1000 } }));
        Assert.Equal(" 5/9 (0 failed) 2/s", ScsKiller.ProgressText(new QueueItem("g", QueueStage.Warming, new WarmProgress(5, 9, 0, 2), null)));
        Assert.Equal("", ScsKiller.ProgressText(new QueueItem("g", QueueStage.Waiting, null, null)));
        Assert.Null(ScsKiller.WarmCounts(0, 0));
        Assert.Equal("2 failed (the driver rejected them)", ScsKiller.WarmCounts(2, 0));
        Assert.Equal("7 skipped (a shader not in this install)", ScsKiller.WarmCounts(0, 7));
        Assert.Equal("2 failed (the driver rejected them), 3 skipped (they crash the GPU driver)", ScsKiller.WarmCounts(2, 0, 3));
    }

    [Fact]
    public async Task A_clean_warm_has_no_done_note()
    {
        var k = await Warmed();
        Assert.Null(k.Queue.Single().Note);
        Assert.Equal((0L, 0L), (k.Games.Single().LastWarmFailed, k.Games.Single().LastWarmSkipped));
    }

    /// <summary>The copies compiled at other texture filtering settings are warm items: they count in the time and cache estimates.</summary>
    [Fact]
    public async Task Texture_filtering_variants_count_in_the_compile_estimate()
    {
        var k = Killer(new FakeReader(Unreal), new FakePlanner(stats: new PlanStats(0, 10000, 5, 7, true, Variants: 5000)), new FakeWarmer());
        await k.ScanAsync(default);
        await Compile(k);
        var s = k.Games.Single();
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(15000, ScsKiller.PlanItems(s.Plan));
        Assert.Equal(TimeSpan.FromSeconds(15000 / rec.PsoPerSecond!.Value), s.EstimatedWarmTime);
        Assert.Equal(15000 * (long)rec.BytesPerPso!.Value, s.EstimatedCacheBytes);
        Assert.Equal(rec.PsoPerSecond, ScsKiller.MeasuredRate([s]));
    }

    [Fact]
    public async Task Queue_indexes_plans_materializes_warms_records_and_goes_stale_on_a_new_driver()
    {
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(new EngineInfo("Unreal", "4.26", null, "D3D12", false, null)), warmer: warmer);
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Ready, s.Status);

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.True(warmer.SawMaterializedWork);
        Assert.False(Directory.Exists(Path.Combine(k.Store.GameDir(_game.Id), "work")));
        s = k.Games.Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        Assert.Equal("100.01", s.WarmedDriverVersion);
        Assert.Equal(3000, s.ShaderCount);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(1000.0, rec.PsoPerSecond);
        Assert.Equal(20000.0 * 1024 / 10000, rec.BytesPerPso);
        Assert.Equal(TimeSpan.FromSeconds(10), s.EstimatedWarmTime);

        var after = Killer(new FakeReader(new EngineInfo("Unreal", "4.26", null, "D3D12", false, null)), driver: "101.00");
        await after.ScanAsync(default);
        var stale = Assert.Single(after.StaleGames());
        Assert.Contains("100.01 -> 101.00", stale.StatusReason);
    }

    [Fact]
    public void Session_log_uses_the_last_launch()
    {
        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllText(csv, "393.0,G,0,0,94.692\n402.5,C,0,0,9.404\n295.5,G,0,0,3.286\n296.2,c,0,0,0.608\n296.6,S,1,1,0.396\n400.0,S,0,1,12.5\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(400), 4, 1, 1, 2, 12.5), SessionLog.Read(csv).Last);
        // newer proxies append the PSO key and the proxy's own time: same stats
        File.AppendAllText(csv, $"401.0,S,0,0,20.5,{new string('a', 40)},0.031\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(401), 5, 1, 1, 3, 20.5), SessionLog.Read(csv).Last);
        Assert.Null(SessionLog.Read(Path.Combine(_root, "missing.csv")).Last);
    }

    /// <summary>Compiles while the game starts up (the frame report's startup, from the creates) are counted apart from the
    /// compiles in play and the worst of them; the first launch after a compile still counts every create.</summary>
    [Fact]
    public void Session_log_leaves_the_startup_burst_out_of_the_compiles_in_play()
    {
        var csv = Path.Combine(_root, "creates.csv");
        var rows = new List<string> { "#session,2000,Fake.exe" };
        for (int i = 0; i < 200; i++) rows.Add($"{10 * i:0.0},S,0,0,{(i % 2 == 0 ? "900.0" : "0.5")}");   // 0-2 s: 100 compiles, 100 hits
        rows.Add("30000.0,S,0,0,25.0");
        rows.Add("31000.0,S,0,0,40.0");
        rows.Add("#end,62000");
        File.WriteAllLines(csv, rows);
        var (last, _, first) = SessionLog.Read(csv, firstAfter: DateTimeOffset.FromUnixTimeMilliseconds(1000));
        Assert.Equal(new SessionStats(TimeSpan.FromSeconds(60), 202, 0, 100, 2, 40.0, StartupCompiles: 100), last);
        Assert.Equal((100L, 102L), (first!.Hits, first.Compiles));

        rows.RemoveRange(201, 2);   // a launch that never got past its startup burst: its last second is startup too
        File.WriteAllLines(csv, rows);
        Assert.Equal(new SessionStats(TimeSpan.FromSeconds(60), 200, 0, 100, 0, 0, StartupCompiles: 100), SessionLog.Read(csv).Last);

        rows.Insert(201, "2100.0,S,0,0,25.0");   // a lone compile right after the burst is in play
        File.WriteAllLines(csv, rows);
        Assert.Equal(new SessionStats(TimeSpan.FromSeconds(60), 201, 0, 100, 1, 25.0, StartupCompiles: 100), SessionLog.Read(csv).Last);

        // ray tracing state objects: the same startup, their own compile threshold
        rows = ["#session,2000,Fake.exe", .. Enumerable.Range(0, 200).Select(i => $"{10 * i:0.0},R,0,0,100.0"), "30000.0,R,0,0,100.0", "31000.0,A,0,0,20.0", "#end,62000"];
        File.WriteAllLines(csv, rows);
        Assert.Equal(new SessionStats(TimeSpan.FromSeconds(60), 202, 0, 0, 0, 0, StateObjectsReady: 1, StateObjectsCompiled: 1, StateObjectsStartupCompiled: 200),
            SessionLog.Read(csv).Last);
    }

    /// <summary>One end for a launch, from everything either report knows: an Unreal game ends itself, so no #end, and goes
    /// quiet in the last 0.05 s of its creates; its frames run on for four minutes. Both reports end startup at 60 s.</summary>
    [Fact]
    public async Task Both_reports_take_the_launch_end_from_the_frames_too()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        const long at = 1_700_000_000_000;
        File.WriteAllLines(Path.Combine(_exeDir, "scskiller_creates.csv"), ["#session,1700000000000,Fake-Win64-Shipping.exe",
            .. Enumerable.Range(0, 60 * 30).Select(i => $"{i * 1000 / 30.0:0.0},S,0,0,25.000"), "60000.0,S,0,0,25.000", "60033.3,S,0,0,25.000"]);
        File.WriteAllBytes(Path.Combine(_exeDir, FrameLog.FileName), FrameLogTests.Launch(at + 50, 0, Enumerable.Range(0, 30_000).Select(i => i * 10.0)));
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(TimeSpan.FromSeconds(60), s.LastFrames!.Startup);
        Assert.Equal((1L, 1801L), (s.LastSession!.Compiles, s.LastSession.StartupCompiles));
        Assert.Equal(s.LastFrames.Duration, s.LastSession.Duration);   // the CLI's play time is the game page's
    }

    /// <summary>The game page shows frame times only next to their own launch's counts: a launch that presented nothing
    /// leaves the earlier launch's frames out.</summary>
    [Fact]
    public async Task Frames_of_another_launch_are_not_shown_with_the_last_session()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        const long a = 1_700_000_000_000, b = a + 120_000;
        File.WriteAllLines(Path.Combine(_exeDir, "scskiller_creates.csv"), [$"#session,{a},Fake-Win64-Shipping.exe", "#clock,10.0", "20.0,S,1,1,0.5", $"#end,{a + 60_000},60010.0",
            $"#session,{b},Fake-Win64-Shipping.exe", "#clock,10.0", "20.0,S,0,0,25.0", $"#end,{b + 30_000},30010.0"]);
        File.WriteAllBytes(Path.Combine(_exeDir, FrameLog.FileName), FrameLogTests.Launch(a, 10_000, Enumerable.Range(1, 5000).Select(i => 10.0 + i * 10)));
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(1, s.LastSession!.Compiles);   // the second launch's
        Assert.Null(s.LastFrames);
    }

    /// <summary>A launch without #end has ended once the app saw its run exit: the first launch after a compile is judged
    /// then, not only when a later launch begins.</summary>
    [Fact]
    public void A_watched_exit_ends_the_launch_for_the_first_launch_check()
    {
        const long t0 = 1_700_000_000_000;
        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllText(csv, $"#session,{t0},Fake.exe\n1.0,S,1,1,0.5\n2.0,S,0,0,25.0\n");
        var warmed = DateTimeOffset.FromUnixTimeMilliseconds(t0 - 60_000);
        Assert.Null(SessionLog.Read(csv, "Fake.exe", warmed).First);   // still running, as far as anyone knows
        var exited = new PlayWindow(DateTimeOffset.FromUnixTimeMilliseconds(t0 - 1000), DateTimeOffset.FromUnixTimeMilliseconds(t0 + 600_000));
        Assert.Equal((1L, 1L), SessionLog.Read(csv, "Fake.exe", warmed, played: exited).First is { } f ? (f.Hits, f.Compiles) : default);
    }

    /// <summary>Every create is in exactly one count, so the game page's and the CLI's breakdowns add up to Requests.</summary>
    [Fact]
    public void Session_log_counts_add_up_to_the_requests()
    {
        var rq = new string('a', 40);
        string[] startup = ["S,0,0,900.0", "S,1,1,0.5", "s,1,1,0.3", "R,0,0,100.0", $"C,1,1,20.0,{rq}"];
        string[] play = ["S,0,0,25.0", "S,1,1,0.5", "s,1,1,0.3", "R,0,0,100.0", "A,1,1,20.0", $"C,1,1,30.0,{rq}"];
        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllLines(csv, ["#session,2000,Fake.exe", .. Enumerable.Range(0, 200).Select(i => $"{10 * i:0.0},{startup[i % 5]}"),
            .. play.Select((r, i) => $"{30_000 + 1000 * i:0.0},{r}"), "#end,62000"]);
        var l = SessionLog.Read(csv, rayQuery: new HashSet<string> { rq }).Last!;
        Assert.Equal(new SessionStats(TimeSpan.FromSeconds(60), 206, 41, 41, 1, 25.0, RayQueryRecompiles: 41, StateObjectsReady: 1, StateObjectsCompiled: 1,
            StartupCompiles: 40, StateObjectsStartupCompiled: 40), l);
        Assert.Equal(l.Requests, l.FromGameLibrary + l.CacheHits + l.Compiles + l.StartupCompiles + l.RayQueryRecompiles
                                 + l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled);
    }

    /// <summary>A compiled RayQuery PSO's create at NVIDIA's floor is counted apart: not a compile, not the worst; over the
    /// floor (a miss at cold cost) it is a compile again, as is any other PSO's slow create.</summary>
    [Fact]
    public void Session_log_counts_RayQuery_creates_at_the_floor_apart()
    {
        string rq = new('a', 40), other = new('b', 40);
        var db = Path.Combine(_root, "recording.db");
        using (var f = File.Create(db))
        {
            var (traces, plain) = (Tests.Planning.RtCollectionTests.Sfi0(0x100000), Tests.Planning.RtCollectionTests.Sfi0(0x4000));
            var (tracesSha, plainSha) = (Convert.ToHexStringLower(SHA1.HashData(traces)), Convert.ToHexStringLower(SHA1.HashData(plain)));
            PsoDb.WriteBlob(f, tracesSha, traces);
            PsoDb.WriteBlob(f, plainSha, plain);
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, tracesSha));
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, plainSha));
            (rq, other) = (new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, tracesSha)).Key, new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, plainSha)).Key);
        }
        var keysFile = Path.Combine(_root, "rayquery.keys");
        Assert.Null(SessionLog.ReadRayQueryKeys(keysFile));
        SessionLog.WriteRayQueryKeys([db], keysFile);
        var keys = SessionLog.ReadRayQueryKeys(keysFile)!;
        Assert.Equal([rq], keys);

        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllText(csv, $"1.0,C,1,1,0.5,{rq},0.01\n2.0,C,1,1,20.0,{rq},0.01\n3.0,C,1,1,59.0,{rq},0.01\n"   // a hit, two at the floor
            + $"4.0,C,1,1,300.0,{rq},0.01\n5.0,C,1,1,20.0,{other},0.01\n6.0,C,1,1,20.0\n");                        // a miss, another PSO, an older proxy's row
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(6), 6, 0, 1, 3, 300.0, 2), SessionLog.Read(csv, rayQuery: keys).Last);
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(6), 6, 0, 1, 5, 300.0), SessionLog.Read(csv).Last);
        File.WriteAllText(csv, $"1.0,C,1,1,20.0,{rq},0.01\n2.0,C,1,1,8.0,{other},0.01\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(2), 2, 0, 0, 1, 8.0, 1), SessionLog.Read(csv, rayQuery: keys).Last);   // not the worst
    }

    /// <summary>A compile writes the RayQuery keys of what it replays, the plan's PSOs with the recorded ones: the plan's
    /// own shader bytes, or the recording's.</summary>
    [Fact]
    public async Task A_compile_writes_the_RayQuery_keys_of_the_plan_and_the_recording()
    {
        var (traces, tracesToo, plain) = (Tests.Planning.RtCollectionTests.Sfi0(0x100000), Tests.Planning.RtCollectionTests.Sfi0(0x100000 | 0x4000), Tests.Planning.RtCollectionTests.Sfi0(0x4000));
        string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));
        byte[] Cs(string sha) => PsoDb.Stream(PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Compute] = sha }, null, 0, [], 0);
        byte[] Db(params (char Tag, byte[] Payload)[] records)
        {
            using var m = new MemoryStream();
            foreach (var (tag, payload) in records)
                if (tag == 'B') PsoDb.WriteBlob(m, Sha(payload), payload);
                else PsoDb.Write(m, tag, payload);
            return m.ToArray();
        }
        var recorded = Db(('B', traces), ('C', PsoDb.Compute(PsoDb.Zero, Sha(traces))));
        var plan = Db(('B', tracesToo), ('B', plain), ('S', Cs(Sha(tracesToo))), ('S', Cs(Sha(traces))), ('S', Cs(Sha(plain))));
        var k = Killer(new FakeReader(Unreal), new FakePlanner(genDb: plan, mainDb: recorded), new FakeWarmer());
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var keys = SessionLog.ReadRayQueryKeys(Path.Combine(k.Store.GameDir(_game.Id), "rayquery.keys"));
        Assert.Equal(new[] { new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha(traces))), new('S', Cs(Sha(tracesToo))), new('S', Cs(Sha(traces))) }.Select(r => r.Key).Order(),
            keys!.Order());
    }

    /// <summary>The game's RayQuery keys (written by a compile) count apart on NVIDIA only; AMD keeps every create a hit or a compile.</summary>
    [Theory]
    [InlineData(GpuVendor.Nvidia, 0, 1)]
    [InlineData(GpuVendor.Amd, 1, 0)]
    public async Task RayQuery_creates_count_apart_on_NVIDIA(GpuVendor vendor, long compiles, long apart)
    {
        var key = new string('a', 40);
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = vendor }));
        Directory.CreateDirectory(k.Store.GameDir(_game.Id));
        File.WriteAllLines(Path.Combine(k.Store.GameDir(_game.Id), "rayquery.keys"), [key]);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), $"1.0,C,1,1,0.5,{new string('b', 40)},0.01\n2.0,C,1,1,20.0,{key},0.01\n");
        await k.ScanAsync(default);
        var s = k.Games.Single().LastSession!;
        Assert.Equal((compiles, apart), (s.Compiles, s.RayQueryRecompiles));
    }

    /// <summary>Each exit of the game shows its launch: the recorder appends a launch per start (no #end when the game
    /// is terminated), and the state raised on exit reads it.</summary>
    [Fact]
    public async Task The_last_session_follows_every_exit()
    {
        var k = Killer(new FakeReader(Unreal));
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        k.RunningGameExes = () => running.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await k.ScanAsync(default);
        var changed = new List<GameState>();
        k.GameChanged += changed.Add;
        var csv = Path.Combine(_exeDir, "scskiller_creates.csv");
        foreach (var (compiles, at) in new[] { (7, 1_700_000_000_000L), (3, 1_700_000_100_000L), (0, 1_700_000_200_000L) })
        {
            running.Add(Path.GetFileName(_game.ExePath));
            k.PollGames();
            File.AppendAllText(csv, $"#session,{at},{Path.GetFileName(_game.ExePath)}\n" + string.Concat(Enumerable.Range(1, 10).Select(i => $"{i}.0,S,1,1,{(i <= compiles ? 25.0 : 0.5)}\n")));
            running.Clear();
            for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
            Assert.Equal(compiles, k.Games.Single().LastSession!.Compiles);
            Assert.Equal(compiles, changed[^1].LastSession!.Compiles);
        }
    }

    /// <summary>Two paths hold the same game's record: each save writes only what its holder changed, onto what the other
    /// saved; sets merge by what each added and removed. A record that wasn't loaded is written whole.</summary>
    [Fact]
    public void A_save_writes_only_what_its_holder_changed()
    {
        var store = new AppStore(Path.Combine(_root, "store"));
        var seed = store.LoadGame(_game.Id);
        (seed.CacheKeys, seed.ResumeAt) = (["old", "gone"], 3);
        store.SaveGame(_game.Id, seed);
        var compile = store.LoadGame(_game.Id);
        var exit = store.LoadGame(_game.Id);
        exit.LastPlay = new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(10));
        exit.CacheKeys.Add("game");
        store.SaveGame(_game.Id, exit);
        (compile.WarmedAt, compile.ResumeAt) = (DateTimeOffset.UnixEpoch.AddHours(1), 0);
        compile.CacheKeys.Remove("gone");
        compile.CacheKeys.Add("warm");
        store.SaveGame(_game.Id, compile);
        compile.CacheKeys.Add("later");   // a second save of the same record: only its change since the first
        store.SaveGame(_game.Id, compile);
        var r = store.LoadGame(_game.Id);
        Assert.Equal((exit.LastPlay, compile.WarmedAt, 0L), (r.LastPlay, r.WarmedAt, r.ResumeAt));
        Assert.Equal(["game", "later", "old", "warm"], r.CacheKeys.Order());

        store.SaveGame(_game.Id, new GameRecord { ShaderCount = 7 });
        Assert.Equal((7, null), (store.LoadGame(_game.Id).ShaderCount, store.LoadGame(_game.Id).LastPlay));
    }

    /// <summary>A compile loads the game's record, the game runs and exits while the compile indexes, and the compile's
    /// saves keep the exit's watched run.</summary>
    [Fact]
    public async Task A_compile_keeps_what_a_game_exit_saved_meanwhile()
    {
        var reader = new SlowIndexReader();
        var k = Killer(reader);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clock = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        (k.RunningGameExes, k.Clock) = (() => running.ToHashSet(StringComparer.OrdinalIgnoreCase), () => clock);
        await k.ScanAsync(default);
        void Poll() { clock = clock.AddSeconds(3); k.PollGames(); }
        Poll();
        k.Enqueue(_game.Id);
        k.StartQueue();
        Assert.True(reader.Indexing.Wait(TimeSpan.FromSeconds(10)));   // the compile holds the record it loaded
        running.Add(Path.GetFileName(_game.ExePath));
        for (int i = 0; i < 10; i++) Poll();
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) Poll();
        var played = k.Store.LoadGame(_game.Id).LastPlay;
        Assert.NotNull(played);
        reader.Go.Set();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.NotNull(rec.WarmedAt);
        Assert.Equal(played, rec.LastPlay);
    }

    sealed class SlowIndexReader : IEngineReader
    {
        public readonly ManualResetEventSlim Indexing = new(false), Go = new(false);
        public EngineInfo? Detect(Game game) => Unreal;
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct)
        {
            Indexing.Set();
            Go.Wait(TimeSpan.FromSeconds(10));
            return new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        }
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary>A launch without #end (the game terminated itself) lasts until the exit the watcher saw, when the run it
    /// saw from start to exit holds the launch's start; else until its last create. An #end always wins.</summary>
    [Fact]
    public void A_session_without_an_end_lasts_until_the_watched_exit()
    {
        const long t0 = 1_700_000_000_000;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(t0);
        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllText(csv, $"#session,{t0},Fake.exe\n1.0,S,1,1,0.5\n20000.0,S,1,1,25.0\n");
        TimeSpan Played(PlayWindow? w) => SessionLog.Read(csv, played: w).Last!.Duration;
        Assert.Equal(TimeSpan.FromSeconds(20), Played(null));
        Assert.Equal(TimeSpan.FromMinutes(10), Played(new(start.AddSeconds(-3), start.AddMinutes(10))));
        Assert.Equal(TimeSpan.FromSeconds(20), Played(new(start.AddSeconds(5), start.AddMinutes(10))));    // another run: started after this launch
        Assert.Equal(TimeSpan.FromSeconds(20), Played(new(start.AddHours(-2), start.AddHours(-1))));      // an older run
        File.AppendAllText(csv, $"#end,{t0 + 300_000}\n");
        Assert.Equal(TimeSpan.FromMinutes(5), Played(new(start.AddSeconds(-3), start.AddMinutes(10))));
    }

    [Fact]
    public async Task The_watcher_records_the_run_and_the_session_shows_its_length()
    {
        var k = Killer(new FakeReader(Unreal));
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clock = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        (k.RunningGameExes, k.Clock) = (() => running.ToHashSet(StringComparer.OrdinalIgnoreCase), () => clock);
        await k.ScanAsync(default);
        void Poll(int seconds) { clock = clock.AddSeconds(seconds); k.PollGames(); }
        Poll(3);   // not running
        running.Add(Path.GetFileName(_game.ExePath));
        Poll(3);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), $"#session,{clock.AddSeconds(2).ToUnixTimeMilliseconds()},{Path.GetFileName(_game.ExePath)}\n1.0,S,1,1,0.5\n9000.0,S,1,1,25.0\n");
        for (int i = 0; i < 200; i++) Poll(3);   // 10 minutes of play
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) Poll(3);
        Assert.Equal(TimeSpan.FromSeconds(600 - 2), k.Games.Single().LastSession!.Duration);
        Assert.NotNull(k.Store.LoadGame(_game.Id).LastPlay);
        var rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = null;
        k.Store.SaveGame(_game.Id, rec);

        // the app started while the game ran: that run's start is unknown, so its launch keeps the last create's time
        var late = Killer(new FakeReader(Unreal));
        (late.RunningGameExes, late.Clock) = (() => running.ToHashSet(StringComparer.OrdinalIgnoreCase), () => clock);
        await late.ScanAsync(default);
        running.Add(Path.GetFileName(_game.ExePath));
        late.PollGames();
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) late.PollGames();
        Assert.Equal(TimeSpan.FromSeconds(9), late.Games.Single().LastSession!.Duration);
    }

    /// <summary>A scan overlapping a game's exit: the exit's refresh reads the new launch while the scan is still busy with
    /// another game, and the scan's older state of this one must not replace it.</summary>
    [Fact]
    public async Task A_scan_never_replaces_a_newer_state()
    {
        var a = FakeGame("test:a", "A Game");
        var b = FakeGame("test:b", "B Game");
        var reader = new StallingReader(b.Id);
        var k = Killer(reader, games: [a, b]);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        k.RunningGameExes = () => running.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await k.ScanAsync(default);
        var csv = Path.Combine(Path.GetDirectoryName(a.ExePath)!, "scskiller_creates.csv");
        File.WriteAllText(csv, "#session,1700000000000,A Game.exe\n1.0,S,1,1,25.0\n");
        running.Add(Path.GetFileName(a.ExePath));
        k.PollGames();

        reader.Gate.Reset();
        var scan = k.RescanAsync(default);   // evaluates A (the old launch), then waits in B's detection
        Assert.True(reader.Waiting.Wait(TimeSpan.FromSeconds(10)));
        File.AppendAllText(csv, "#session,1700000100000,A Game.exe\n" + string.Concat(Enumerable.Range(1, 5).Select(i => $"{i}.0,S,1,1,25.0\n")));
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.Equal(5, k.Games.Single(s => s.Game.Id == a.Id).LastSession!.Compiles);
        reader.Gate.Set();
        var states = await scan;
        Assert.Equal(5, k.Games.Single(s => s.Game.Id == a.Id).LastSession!.Compiles);
        Assert.Equal(5, states.Single(s => s.Game.Id == a.Id).LastSession!.Compiles);
    }

    sealed class StallingReader(string blocked) : IEngineReader
    {
        public readonly ManualResetEventSlim Gate = new(true), Waiting = new(false);
        public EngineInfo? Detect(Game game)
        {
            if (game.Id == blocked && !Gate.IsSet) { Waiting.Set(); Gate.Wait(); }
            return Unreal;
        }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    [Fact]
    public void Scheduled_task_xml_has_logon_and_idle_triggers_and_runs_rewarm()
    {
        var xml = ScheduledTask.Xml(@"C:\Program Files\SCSKiller\scskiller.exe", @"PC\user");
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.NotNull(doc.Root!.Element(ns + "Triggers")!.Element(ns + "LogonTrigger"));
        Assert.NotNull(doc.Root.Element(ns + "Triggers")!.Element(ns + "IdleTrigger"));
        var exec = doc.Root.Element(ns + "Actions")!.Element(ns + "Exec")!;
        Assert.Equal(@"C:\Program Files\SCSKiller\scskiller.exe", exec.Element(ns + "Command")!.Value);
        Assert.Equal("rewarm-stale --if-driver-changed", exec.Element(ns + "Arguments")!.Value);
        Assert.Equal("LeastPrivilege", doc.Descendants(ns + "RunLevel").Single().Value);
    }

    [Fact]
    public void Session_log_reads_the_last_launched_exe_name_from_the_markers()
    {
        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllText(csv, "1.0,G,0,0,5.0\n");
        Assert.Null(SessionLog.Read(csv).Exe);   // an older proxy: no markers
        File.AppendAllText(csv, "#session,1700000000000,Fake-Win64-Shipping.exe\n2.0,G,0,0,5.0\n#end,1700000001000\n" +
                                "#session,1700000100000,FAKE,Win64.EXE\n#end,1700000101000\n");
        var (last, exe, _) = SessionLog.Read(csv);
        Assert.Equal(new LaunchedExe("FAKE,Win64.EXE", DateTimeOffset.FromUnixTimeMilliseconds(1700000100000)), exe);   // commas kept
        Assert.Equal(TimeSpan.FromSeconds(1), last!.Duration);
        Assert.Equal("Fake-Win64-Shipping.exe", SessionLog.Read(csv, "fake-win64-shipping.exe").Exe!.Name);   // the last of that exe
        Assert.Null(SessionLog.Read(csv, "Other.exe").Exe);
        Assert.Equal((null, null, null), SessionLog.Read(Path.Combine(_root, "missing.csv")));
    }

    /// <summary>A launch in the recorder's csv: its markers, cache hits (0.5 ms), compiles (25 ms), library loads and a ray
    /// tracing request (neither).</summary>
    static string Launch(long at, int hits, int compiles, string exe = "Fake-Win64-Shipping.exe", bool end = true, int library = 0)
    {
        var rows = Enumerable.Repeat("S,1,1,0.5", hits).Concat(Enumerable.Repeat("S,0,0,25.0", compiles)).Concat(Enumerable.Repeat("s,1,1,0.3", library)).Append("R,1,1,40.0");
        return $"#session,{at},{exe}\n" + string.Concat(rows.Select((r, i) => $"{i + 1}.0,{r}\n")) + (end ? $"#end,{at + 60_000}\n" : "");
    }

    [Fact]
    public void The_first_launch_after_a_warm_is_judged_by_its_hits_and_compiles()
    {
        var csv = Path.Combine(_root, "creates.csv");
        const long t0 = 1_700_000_000_000;
        var warmed = DateTimeOffset.FromUnixTimeMilliseconds(t0);
        LaunchCheck? First() => SessionLog.Read(csv, "fake-win64-shipping.exe", warmed, ScsKiller.MinJudgedCreates).First;
        File.WriteAllText(csv, Launch(t0 - 1000, 0, 500)                // before the warm
            + Launch(t0 + 1000, 10, 40)                                  // fewer creates than a judgement needs
            + Launch(t0 + 2000, 50, 50, exe: "Launcher.exe")             // another exe of the folder
            + Launch(t0 + 3000, 80, 20, library: 900)                    // the first launch: a fifth compiled
            + Launch(t0 + 4000, 0, 100));
        Assert.Equal(new LaunchCheck(DateTimeOffset.FromUnixTimeMilliseconds(t0 + 3000), 80, 20), First());
        Assert.False(ScsKiller.IsPartlyWarmed(First()));                 // exactly a fifth: warmed
        Assert.True(ScsKiller.IsPartlyWarmed(new LaunchCheck(warmed, 79, 21)));
        Assert.False(ScsKiller.IsPartlyWarmed(new LaunchCheck(warmed, 80, 20)));
        Assert.True(ScsKiller.IsPartlyWarmed(new LaunchCheck(warmed, 0, 100)));
        Assert.False(ScsKiller.IsPartlyWarmed((LaunchCheck?)null));
        Assert.Equal(0.2, First()!.Compiled, 9);

        File.WriteAllText(csv, Launch(t0 + 3000, 10, 190, end: false));   // still running (or crashed): not judged yet
        Assert.Null(First());
        File.AppendAllText(csv, Launch(t0 + 9000, 200, 0));              // the next launch ends it
        Assert.Equal(190, First()!.Compiles);
        Assert.Null(SessionLog.Read(csv, null, null).First);             // nothing asked
    }

    static byte[] SiblingsDb()   // two recorded PSOs of one shader set (another root signature each): two careful passes
    {
        var stages = new Dictionary<int, string> { [(int)Stage.Vertex] = $"{1:x40}" };
        var db = new MemoryStream();
        PsoDb.Write(db, 'S', PsoDb.Stream($"{8:x40}", stages, [], 3, [], 0));
        PsoDb.Write(db, 'S', PsoDb.Stream($"{9:x40}", stages, [], 3, [], 0));
        return db.ToArray();
    }

    static readonly PlanStats RecordedPlan = new(1000, 9000, 5, 7, true);

    [Fact]
    public async Task A_partly_warmed_game_on_AMD_offers_the_careful_compile_which_persists_and_runs_in_passes()
    {
        var amd = new FakeVendor(Gpu with { Vendor = GpuVendor.Amd });
        var warmer = new FakeWarmer();
        FakePlanner Planner() => new(stats: RecordedPlan, mainDb: SiblingsDb());
        var k = Killer(new FakeReader(Unreal), Planner(), warmer, vendor: amd);
        k.Settings = k.Settings with { Threads = 20 };
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var s = k.Games.Single();
        // the recorded PSOs at the careful rate, the rest at the game's measured fast rate (10000 in 10 s)
        var careful = TimeSpan.FromSeconds(1000 / ScsKiller.DefaultCarefulPsoPerSecond + 9000 / 1000.0);
        Assert.Equal((GameStatus.Warmed, "warmed for driver 100.01"), (s.Status, s.StatusReason));
        Assert.Equal(new CarefulCompile(false, null, careful, 1000), s.Careful);
        Assert.Equal((20, 0, 0), (warmer.Options!.Threads, warmer.Options.CarefulThreads, warmer.Passes));

        var warmedAt = k.Store.LoadGame(_game.Id).WarmedAt!.Value.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), Launch(warmedAt + 1, 100, 300) + Launch(warmedAt + 2, 400, 0));
        k.RefreshGame(_game.Id);
        s = k.Games.Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        Assert.True(ScsKiller.IsPartlyWarmed(s));
        Assert.Equal(0.75, s.Careful!.LaunchCompiled);
        Assert.Equal("partly warmed for driver 100.01: 75% of the 400 pipelines its first launch created still compiled; " +
                     $"a careful compile ({ScsKiller.AmdCarefulThreads} threads, in passes) reaches more of them, in about {ScsKiller.Duration(careful)}", s.StatusReason);
        Assert.Equal(TimeSpan.FromSeconds(10), s.EstimatedWarmTime);   // the fast compile's, measured

        k.SetCarefulCompile(_game.Id, true);
        await Task.Delay(20);   // the next warm ends after both launches
        warmer = new FakeWarmer();
        k = Killer(new FakeReader(Unreal), Planner(), warmer, vendor: amd);   // persisted
        k.Settings = k.Settings with { Threads = 20 };
        await k.ScanAsync(default);
        s = k.Games.Single();
        Assert.Equal((true, careful), (s.Careful!.On, s.EstimatedWarmTime));
        Assert.EndsWith(": the next compile is careful", s.StatusReason);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((20, ScsKiller.AmdCarefulThreads, 2), (warmer.Options!.Threads, warmer.Options.CarefulThreads, warmer.Passes));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.True(rec.WarmedCareful && rec.Careful);
        Assert.Null(rec.FirstLaunch);                                   // judged again by the next launch (the csv's are older)
        s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, false), (s.Status, ScsKiller.IsPartlyWarmed(s)));
        Assert.Equal(careful, s.EstimatedWarmTime);                      // a careful warm's rate doesn't replace the fast one

        File.AppendAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), Launch(DateTimeOffset.Now.AddMinutes(1).ToUnixTimeMilliseconds(), 50, 150));
        k.RefreshGame(_game.Id);
        Assert.EndsWith(", even after a careful compile", k.Games.Single().StatusReason);

        k.SetCarefulCompile(_game.Id, false);
        Assert.False(k.Store.LoadGame(_game.Id).Careful);
    }

    [Fact]
    public async Task Without_a_recording_the_careful_compile_has_nothing_to_do_and_says_so()
    {
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(), warmer, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        var rec = k.Store.LoadGame(_game.Id);
        rec.Careful = true;
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, warmer.Passes);   // no pass file: one ordinary warm
        var warmedAt = k.Store.LoadGame(_game.Id).WarmedAt!.Value.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), Launch(warmedAt + 1, 100, 300));
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(0, s.Careful!.Recorded);
        Assert.Equal(s.EstimatedWarmTime, s.Careful.Estimate);          // nothing slower
        Assert.EndsWith("there is no recording yet (turn recording on and play)", s.StatusReason);
    }

    [Theory]
    [InlineData(GpuVendor.Amd, true, 20, 8, ScsKiller.AmdCarefulThreads, ScsKiller.AmdCarefulThreads, 2)]
    [InlineData(GpuVendor.Amd, true, 1, 8, 1, ScsKiller.AmdCarefulThreads, 2)]   // fewer threads asked: fewer
    [InlineData(GpuVendor.Amd, false, 20, 3, 0, 0, 0)]
    [InlineData(GpuVendor.Nvidia, true, 20, 3, 0, 0, 0)]    // a careful choice kept from an AMD GPU: ignored
    [InlineData(GpuVendor.Unknown, true, 20, 3, 0, 0, 0)]
    public async Task The_careful_thread_cap_and_passes_apply_on_AMD_only(GpuVendor vendor, bool careful, int threads, int background,
        int foregroundCareful, int backgroundCareful, int passes)
    {
        foreach (var (idle, full, capped) in new[] { (false, threads, foregroundCareful), (true, background, backgroundCareful) })
        {
            var warmer = new FakeWarmer();
            var k = Killer(new FakeReader(Unreal), new FakePlanner(stats: RecordedPlan, mainDb: SiblingsDb()), warmer, vendor: new FakeVendor(Gpu with { Vendor = vendor }));
            k.Settings = k.Settings with { Threads = threads, BackgroundThreads = background };
            k.Background = idle;
            var rec = k.Store.LoadGame(_game.Id);
            rec.Careful = careful;
            k.Store.SaveGame(_game.Id, rec);
            await k.ScanAsync(default);
            Assert.Equal(vendor == GpuVendor.Amd, k.Games.Single().Careful != null);
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal((full, capped, passes), (warmer.Options!.Threads, warmer.Options.CarefulThreads, warmer.Passes));   // the fast items keep every thread
            if (vendor != GpuVendor.Amd) Assert.Throws<InvalidOperationException>(() => k.SetCarefulCompile(_game.Id, true));
        }
    }
    ScsKiller CaseKiller(GpuVendor vendor, FakeWarmer warmer) =>
        Killer(new FakeReader(Unreal), warmer: warmer, vendor: new FakeVendor(Gpu with { Vendor = vendor }));

    [Fact]
    public async Task A_slow_progress_listener_gets_the_latest_line_not_a_backlog()
    {
        // 2000 progress lines at once and a listener taking 100 ms a report: reporting each line would take 200 s
        var lines = string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"{{\"event\":\"progress\",\"done\":{i * 10},\"total\":20000,\"failed\":0,\"rate\":5620.0}}"))
                    + "\n{\"event\":\"done\",\"done\":20000,\"total\":20000,\"failed\":2,\"seconds\":9.5,\"stopped\":false}\n";
        var reports = new List<WarmProgress>();
        var clock = Stopwatch.StartNew();
        var (last, done, error) = await WarmOutput.Pump(new StringReader(lines), new SlowProgress(reports, 100), () => 0, TimeSpan.FromMilliseconds(50));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}");
        Assert.InRange(reports.Count, 1, 20);
        Assert.Equal((20000L, 2L), (reports[^1].Done, reports[^1].Failed));   // the last line is always reported
        Assert.Equal(20000, done!.Done);
        Assert.Same(done, last);
        Assert.Null(error);
    }

    sealed class SlowProgress(List<WarmProgress> seen, int ms) : IProgress<WarmProgress>
    {
        public void Report(WarmProgress p) { Thread.Sleep(ms); lock (seen) seen.Add(p); }
    }

    [Fact]
    public void The_rate_is_the_recent_one_not_the_average_since_the_start()
    {
        var rate = new RecentRate();
        rate.Add(TimeSpan.Zero, 0);
        rate.Add(TimeSpan.FromSeconds(4), 20_000);
        Assert.Null(rate.PerSecond);   // too short to say
        long done = 20_000;
        for (int s = 5; s <= 30; s++) rate.Add(TimeSpan.FromSeconds(s), done += 5000);   // the fast, cached start
        for (int s = 31; s <= 120; s++) rate.Add(TimeSpan.FromSeconds(s), done += 1000);  // then 1,000/s
        Assert.Equal(1000, rate.PerSecond!.Value, 1);   // the average since the start: ~2,000/s
        for (int s = 121; s <= 160; s++) rate.Add(TimeSpan.FromSeconds(s), done);          // stuck
        Assert.Equal(0, rate.PerSecond!.Value);
    }

    [Fact]
    public async Task Slow_cache_attribution_never_holds_up_the_warms_progress_and_a_stuck_warm_says_so()
    {
        var game = _game;
        // progress every 20 ms, stuck at 3,000 from line 30 on
        var warmer = new StreamingWarmer(60, TimeSpan.FromMilliseconds(20), i => Math.Min(i, 30) * 100);
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        k.AppCache = new SlowCache(TimeSpan.FromMilliseconds(400), "11111111");
        k.StallAfter = TimeSpan.FromMilliseconds(300);
        var notes = new List<string?>();
        k.QueueChanged += q => { if (q.Stage == QueueStage.Warming) lock (notes) notes.Add(q.Note); };
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.True(warmer.SlowestReport < TimeSpan.FromMilliseconds(100), $"a progress report took {warmer.SlowestReport}");
        Assert.Contains("11111111", k.Store.LoadGame(game.Id).CacheKeys);   // attribution still ran, on its own
        Assert.Contains(ScsKiller.StalledNote(TimeSpan.Zero), notes);        // "no progress for 1 min" once stuck
        Assert.Null(notes[0]);
        Assert.True(ScsKiller.Stalled(new QueueItem(game.Id, QueueStage.Warming, null, null, ScsKiller.StalledNote(TimeSpan.FromMinutes(3)))));
    }

    /// <summary>Reports <paramref name="lines"/> progress lines <paramref name="every"/> from its own thread, as the warm's
    /// stdout reader does, and times each report.</summary>
    sealed class StreamingWarmer(int lines, TimeSpan every, Func<int, long> doneAt) : IWarmer
    {
        public TimeSpan SlowestReport;
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress) => new Run(Task.Run(async () =>
        {
            for (int i = 1; i <= lines; i++)
            {
                var t = Stopwatch.StartNew();
                progress?.Report(new WarmProgress(doneAt(i), 10000, 0, 1000));
                if (t.Elapsed > SlowestReport) SlowestReport = t.Elapsed;
                await Task.Delay(every);
            }
            return new WarmResult(WarmOutcome.Completed, 10000, 10000, 0, TimeSpan.FromSeconds(10), 0, "", null);
        }));
        sealed class Run(Task<WarmResult> completion) : IWarmRun
        {
            public Task<WarmResult> Completion { get; } = completion;
            public void Pause() { }
            public void Resume() { }
            public void Stop() { }
        }
    }

    /// <summary>An attribution that takes as long as one over a huge driver cache.</summary>
    sealed class SlowCache(TimeSpan delay, string key) : IAppCache
    {
        public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys) => [];
        public long SizeOf(IEnumerable<string> keys) => 0;
        public IReadOnlySet<string> KeysOpenBy(string exeFileName) { Thread.Sleep(delay); return new HashSet<string> { key }; }
        public int Delete(IEnumerable<string> keys) => 0;
    }

    static async Task WarmOnce(ScsKiller k, string gameId)
    {
        k.Enqueue(gameId);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
    }

    void Marker(string exe, DateTimeOffset at) =>
        File.AppendAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), $"#session,{at.ToUnixTimeMilliseconds()},{exe}\n1.0,G,0,0,5.0\n#end,{at.ToUnixTimeMilliseconds() + 1000}\n");

    [Fact]
    public async Task Amd_warm_stages_the_exe_name_as_the_recorder_saw_the_game_launched()
    {
        var warmer = new FakeWarmer();
        var k = CaseKiller(GpuVendor.Amd, warmer);
        var t0 = DateTimeOffset.Now.AddHours(-1);
        Marker("FAKE-WIN64-SHIPPING.EXE", t0);                   // the launcher starts the game in upper case
        Marker("Launcher.exe", t0.AddMinutes(1));                // another exe in the folder: not this game's name
        await k.ScanAsync(default);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(("FAKE-WIN64-SHIPPING.EXE", t0.ToUnixTimeMilliseconds()), (rec.LaunchedExeName, rec.LaunchedExeSeenAt!.Value.ToUnixTimeMilliseconds()));
        Assert.Equal("FAKE-WIN64-SHIPPING.EXE", ScsKiller.WarmExeName(_game, rec));

        await WarmOnce(k, _game.Id);
        Assert.Equal(["FAKE-WIN64-SHIPPING.EXE"], warmer.Staged);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Equal("FAKE-WIN64-SHIPPING.EXE", k.Store.LoadGame(_game.Id).WarmedExeName);

        // a later launch in the file's own case: that cache is cold on AMD, so the game is stale and the next warm stages it
        rec = k.Store.LoadGame(_game.Id);
        rec.ResumeAt = 777;   // a stopped warm under the old name: its progress is in the other name's cache
        k.Store.SaveGame(_game.Id, rec);
        Marker("Fake-Win64-Shipping.exe", t0.AddMinutes(5));
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Stale, s.Status);
        Assert.Contains("runs as Fake-Win64-Shipping.exe", s.StatusReason);
        Assert.Equal(0, k.Store.LoadGame(_game.Id).ResumeAt);

        // an older sighting doesn't win over a newer one
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), "");
        Marker("FAKE-WIN64-SHIPPING.EXE", t0.AddMinutes(1));
        await k.ScanAsync(default);
        Assert.Equal("Fake-Win64-Shipping.exe", k.Store.LoadGame(_game.Id).LaunchedExeName);

        await WarmOnce(k, _game.Id);
        Assert.Equal("Fake-Win64-Shipping.exe", warmer.Staged[^1]);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Nvidia_stages_the_launched_name_too_but_a_case_change_is_not_stale()
    {
        var warmer = new FakeWarmer();
        var k = CaseKiller(GpuVendor.Nvidia, warmer);
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        Assert.Equal(["Fake-Win64-Shipping.exe"], warmer.Staged);
        Marker("fake-win64-shipping.exe", DateTimeOffset.Now);   // NVIDIA's DXCache key ignores case: the same files
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        await WarmOnce(k, _game.Id);
        Assert.Equal("fake-win64-shipping.exe", warmer.Staged[^1]);
    }

    [Fact]
    public async Task A_running_game_process_tells_the_launched_exe_name()
    {
        var warmer = new FakeWarmer();
        var k = CaseKiller(GpuVendor.Amd, warmer);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fake-win64-shipping.EXE" };   // the module path's case
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); };
        await k.ScanAsync(default);   // first scan: the processes are checked once the game list is known
        Assert.Equal("fake-win64-shipping.EXE", k.Store.LoadGame(_game.Id).LaunchedExeName);

        lock (running) running.Clear();
        await WarmOnce(k, _game.Id);
        Assert.Equal(["fake-win64-shipping.EXE"], warmer.Staged);

        // the install's own case running (what a process we can't read reports) doesn't overwrite what was seen
        lock (running) running.Add("Fake-Win64-Shipping.exe");
        await k.ScanAsync(default);
        Assert.Equal("fake-win64-shipping.EXE", k.Store.LoadGame(_game.Id).LaunchedExeName);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public void Session_log_reads_markers_and_stays_compatible()
    {
        var csv = Path.Combine(_root, "creates.csv");
        // two marked launches: play time = #end - #session; other # lines are ignored
        File.WriteAllText(csv, "#session,1700000000000,Fake-Win64-Shipping.exe\n10.0,G,0,0,50.0\n#end,1700000060000\n" +
                               "#session,1700000100000,Fake-Win64-Shipping.exe\n5.0,G,0,0,4.0\n#flush,1700000200000\n6.0,c,0,0,0.1\n7.0,C,0,0,1.0\n#end,1700000400000\n");
        Assert.Equal(new SessionStats(TimeSpan.FromSeconds(300), 3, 1, 1, 1, 4.0), SessionLog.Read(csv).Last);
        // crash: no #end -> the last create's t_ms
        File.AppendAllText(csv, "#session,1700000500000,Fake-Win64-Shipping.exe\n2.0,G,0,0,8.0\n9.5,S,0,0,0.5\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(9.5), 2, 0, 1, 1, 8.0), SessionLog.Read(csv).Last);
        // a launch that created nothing still counts, with its marker time
        File.AppendAllText(csv, "#session,1700001000000,Fake-Win64-Shipping.exe\n#end,1700001002000\n");
        Assert.Equal(new SessionStats(TimeSpan.FromSeconds(2), 0, 0, 0, 0, 0), SessionLog.Read(csv).Last);
        // an older proxy appending unmarked rows: t_ms restarting still starts a new launch
        File.AppendAllText(csv, "1.0,G,0,0,20.0\n3.0,G,0,0,5.0\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(3), 2, 0, 0, 2, 20.0), SessionLog.Read(csv).Last);
    }

    /// <summary>Sets the settings and waits for the games' re-evaluation (GameChanged: one game).</summary>
    static async Task<int> SetSettings(ScsKiller k, Settings s)
    {
        var changed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(GameState _) => changed.TrySetResult(Environment.CurrentManagedThreadId);
        k.GameChanged += On;
        try
        {
            k.Settings = s;
            return await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { k.GameChanged -= On; }
    }

    // The GUI sets Settings and runs ScanAsync on its UI thread: neither may evaluate games (disk IO per game) there.
    [Fact]
    public async Task Scan_and_a_settings_change_evaluate_games_off_the_callers_thread()
    {
        var gate = new ManualResetEventSlim();
        int detectThread = 0;
        var k = Killer(new GatedReader(Unreal, gate, id => detectThread = id));
        int caller = Environment.CurrentManagedThreadId;
        var scan = k.ScanAsync(default);
        Assert.False(scan.IsCompleted);   // returned while detection waits
        gate.Set();
        await scan;
        Assert.NotEqual(caller, detectThread);

        caller = Environment.CurrentManagedThreadId;
        Assert.NotEqual(caller, await SetSettings(k, k.Settings with { MaximumPlans = !k.Settings.MaximumPlans }));
    }

    sealed class GatedReader(EngineInfo engine, ManualResetEventSlim gate, Action<int> onDetect) : IEngineReader
    {
        public EngineInfo? Detect(Game game) { onDetect(Environment.CurrentManagedThreadId); gate.Wait(TimeSpan.FromSeconds(10)); return engine; }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    // A newer planner (Planner.Version) re-warms a game only when its rebuilt plan has records the warm didn't replay.
    // The bump is simulated by marking the record's plan and warm as built by the version before.
    async Task<(ScsKiller K, FakeWarmer Warmer, List<PsoDb.Rec> Records)> WarmedWithPlan(Game[]? games = null)
    {
        List<PsoDb.Rec> records = [new('B', [.. new byte[20], 9]), new('P', [1]), new('P', [2]), new('C', [3])];
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: records), warmer, games: games);
        k.IdleTime = () => TimeSpan.FromHours(1);   // plan checks are "when idle" items
        await k.ScanAsync(default);
        await Compile(k);
        Assert.Equal(GameStatus.Warmed, k.Games.Single(g => g.Game.Id == _game.Id).Status);
        return (k, warmer, records);
    }

    async Task Compile(ScsKiller k)
    {
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
    }

    void OlderPlanner(ScsKiller k, bool fingerprint = true)
    {
        var rec = k.Store.LoadGame(_game.Id);
        (rec.PlanVersion, rec.WarmedPlanVersion) = (Planner.Version - 1, Planner.Version - 1);
        if (!fingerprint) (rec.PlanItems, rec.WarmedPlanItems) = (null, null);   // a record from before the fingerprint
        k.Store.SaveGame(_game.Id, rec);
    }

    /// <summary>A shipped pipeline cache added by a patch, or an inline shader's new bytes, change no ContentHash (the
    /// community database's key) but do change the index's maps: the next compile plans again.</summary>
    [Fact]
    public async Task A_change_in_the_indexs_maps_alone_rebuilds_the_plan()
    {
        List<ShaderMap> maps = [new("m1", "lib", "PCD3D_SM6", [$"{1:x40}", $"{2:x40}"])];
        var k = Killer(new FakeReader(Unreal, maps: maps), new FakePlanner(), new FakeWarmer());
        await k.ScanAsync(default);
        await Compile(k);
        var built = k.Store.LoadGame(_game.Id).PlanBuiltAt;
        Assert.NotNull(built);
        await Compile(k);
        Assert.Equal(built, k.Store.LoadGame(_game.Id).PlanBuiltAt);   // the same index: the plan is reused

        maps.Add(new("Shipped:1", "PipelineCache", "PCD3D_SM6", [$"{1:x40}", $"{2:x40}"], IsPipeline: true));
        await Compile(k);
        var rebuilt = k.Store.LoadGame(_game.Id).PlanBuiltAt;
        Assert.True(rebuilt > built);
        maps[0] = maps[0] with { Shaders = [$"{1:x40}", $"{3:x40}"] };
        await Compile(k);
        Assert.True(k.Store.LoadGame(_game.Id).PlanBuiltAt > rebuilt);
    }

    /// <summary>The game page's compile button: with the queue stopped it adds the game and runs the waiting items, leaving
    /// a stopped item stopped and finished ones listed; with the queue running it only adds. The Library's "Add to queue"
    /// only adds.</summary>
    [Fact]
    public async Task The_game_pages_compile_button_runs_the_queue_without_restarting_a_stopped_item()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer, games: Three());
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);   // the Library's "Add to queue"
        await Task.Delay(200);
        Assert.False(k.QueueRunning);
        Assert.Empty(warmer.Started);

        k.StartQueue();
        await warmer.WhenStarted();
        k.StopQueue();
        await Until(() => k.Queue.Any(q => q.GameId == _game.Id && q.Stage == QueueStage.Stopped));
        Assert.False(k.QueueRunning);

        k.Compile("test:b");   // the queue isn't running: it runs test:b, not the stopped item
        Assert.True(k.QueueRunning);
        await warmer.WhenStarted(2);
        Assert.Equal([_game.Id, "test:b"], warmer.Started);

        k.Compile("test:c");   // the queue runs: only added
        Assert.Equal(QueueStage.Stopped, k.Queue.Single(q => q.GameId == _game.Id).Stage);
        warmer.Run!.Finish();
        await warmer.WhenStarted(3);
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([_game.Id, "test:b", "test:c"], warmer.Started);
        Assert.Equal(QueueStage.Stopped, k.Queue.Single(q => q.GameId == _game.Id).Stage);
        Assert.All(k.Queue.Where(q => q.GameId != _game.Id), q => Assert.Equal(QueueStage.Done, q.Stage));
    }

    [Fact]
    public async Task A_newer_planner_with_the_same_plan_is_not_stale_and_is_not_rewarmed()
    {
        var (k, warmer, records) = await WarmedWithPlan();
        Assert.NotNull(k.Store.LoadGame(_game.Id).WarmedPlanItems);
        OlderPlanner(k);
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Stale, s.Status);   // not rebuilt yet: it may compile more
        Assert.Equal("SCSKiller can now compile more of this game", s.StatusReason);

        records.Reverse();   // the same records in another order: the same plan
        k.CheckPlans = true;   // the app: a "when idle" plan rebuild, no warm
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Done, "already compiled: the new plan adds nothing"), (k.Queue.Single().Stage, k.Queue.Single().Note));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Single(warmer.Started);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((Planner.Version, rec.PlanItems), (rec.WarmedPlanVersion, rec.WarmedPlanItems));

        records.RemoveAt(0);   // fewer records (a subset) is nothing new either
        OlderPlanner(k);
        await Compile(k);   // the user's compile of the stale game: rebuilds the plan, no warm
        Assert.Equal("already compiled: the new plan adds nothing", k.Queue.Single().Note);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Single(warmer.Started);
    }

    /// <summary>FromSoftware plans before version 26 used the 1.1 root signatures the shaders carry, which the game never
    /// creates: a game warmed by one gets its plan rebuilt when idle and counts what the new plan adds.</summary>
    [Fact]
    public async Task A_FromSoftware_game_warmed_before_its_root_signature_fix_is_replanned()
    {
        Assert.True(Planner.Version > 25);
        List<PsoDb.Rec> records = [new('S', [1]), new('S', [2])];
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(new("FromSoftware", "DXIL+RTS0", "Elden Ring", "D3D12", false, null)), new FakePlanner(records: records), warmer);
        k.IdleTime = () => TimeSpan.FromHours(1);
        await k.ScanAsync(default);
        await Compile(k);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        var rec = k.Store.LoadGame(_game.Id);
        (rec.PlanVersion, rec.WarmedPlanVersion) = (25, 25);
        k.Store.SaveGame(_game.Id, rec);

        records.Clear();
        records.AddRange([new('S', [3]), new('S', [4]), new('S', [5])]);   // the same stage sets under the game's root signatures
        k.CheckPlans = true;
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(k.Queue.Single().PlanCheck);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, 3L), (s.Status, s.NewPipelines));
        Assert.Single(warmer.Started);   // the plan check doesn't warm
    }

    [Fact]
    public async Task A_newer_planner_with_new_pipelines_is_stale_with_their_count()
    {
        var (k, warmer, records) = await WarmedWithPlan();
        records.AddRange([new('P', [4]), new('Y', [5]), new('B', [.. new byte[20], 6])]);   // a root signature alone compiles nothing
        OlderPlanner(k);
        k.CheckPlans = true;
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, "SCSKiller can now compile 2 more pipelines for this game", 2L), (s.Status, s.StatusReason, s.NewPipelines));
        Assert.Equal(s.StatusReason, k.Queue.Single().Note);
        Assert.True(k.Queue.Single().PlanCheck);   // finished, still left out of the lists
        Assert.Null(ScsKiller.PlanCheckLine(k.Queue));
        Assert.Single(warmer.Started);   // the plan check doesn't warm
        Assert.Single(k.StaleGames());   // the Library's "Needs rebuilding" and the driver-update re-warm read the same

        await Compile(k);
        Assert.Equal(2, warmer.Started.Count);
        Assert.False(k.Queue.Single().PlanCheck);
        s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, (long?)0), (s.Status, s.NewPipelines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_plan_check_is_counted_apart_until_the_game_is_queued_to_compile(bool driverUpdate)
    {
        var otherDir = Directory.CreateDirectory(Path.Combine(_root, "Other Game")).FullName;
        var (k, warmer, records) = await WarmedWithPlan([_game, new Game("test:other", "Other Game", Store.Other, otherDir, Path.Combine(otherDir, "Other.exe"))]);
        records.Add(new('P', [4]));
        OlderPlanner(k);
        k.IdleTime = () => TimeSpan.Zero;   // the user is at the PC
        Plays(k, "Other.exe");   // an older planner's check runs at once, but not while a game is played
        k.CheckPlans = true;
        await k.ScanAsync(default);
        var check = k.Queue.Single(q => q.GameId == _game.Id);
        Assert.Equal((QueueStage.Waiting, true), (check.Stage, check.PlanCheck));
        Assert.Equal("Checking 1 game for more to compile", ScsKiller.PlanCheckLine(k.Queue));

        if (driverUpdate) k.EnqueueWhenIdle(_game.Id);   // OnDriverUpdate's re-warm
        else { k.Enqueue(_game.Id); k.StartQueue(); }
        Assert.False(k.Queue.Single(q => q.GameId == _game.Id).PlanCheck);
        if (driverUpdate) Assert.Equal("starts when the PC is idle", k.Queue.Single(q => q.GameId == _game.Id).Note);
        Assert.Null(ScsKiller.PlanCheckLine(k.Queue));
        Plays(k, null);
        k.IdleTime = () => TimeSpan.FromHours(1);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var done = k.Queue.Single(q => q.GameId == _game.Id);
        Assert.Equal((QueueStage.Done, false), (done.Stage, done.PlanCheck));
        Assert.Equal(2, warmer.Started.Count);   // compiled, not only planned
    }

    /// <summary>The watcher sees <paramref name="exe"/> running (null: nothing, after enough polls for an exit).</summary>
    /// <summary>A Compile click on a game whose plan check runs or waits makes the item the user's: its warm runs at the
    /// settings' priority and threads, also while another game is played. A compile the app queues for when idle stays
    /// background.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_compile_asked_for_during_a_plan_check_runs_in_the_foreground(bool checkRunning, bool user)
    {
        var otherDir = Directory.CreateDirectory(Path.Combine(_root, "Other Game")).FullName;
        List<PsoDb.Rec> records = [new('B', [.. new byte[20], 9]), new('P', [1]), new('P', [2]), new('C', [3])];
        var (building, release) = (new ManualResetEventSlim(), new ManualResetEventSlim(true));
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: records, onBuild: () => { building.Set(); release.Wait(HangGuard); }), warmer,
            games: [_game, new Game("test:other", "Other Game", Store.Other, otherDir, Path.Combine(otherDir, "Other.exe"))]);
        k.Settings = k.Settings with { Threads = 3, Priority = WarmPriority.BelowNormal, BackgroundThreads = 2 };
        k.IdleTime = () => TimeSpan.FromHours(1);
        await k.ScanAsync(default);
        await Compile(k);
        records.Add(new('P', [4]));
        OlderPlanner(k);
        building.Reset();
        if (checkRunning) release.Reset();
        else Plays(k, "Other.exe");   // the check waits while a game is played
        k.CheckPlans = true;
        await k.ScanAsync(default);
        if (checkRunning)
        {
            Assert.True(building.Wait(HangGuard));
            Plays(k, "Other.exe");
        }
        Assert.True(k.Queue.Single(q => q.GameId == _game.Id).PlanCheck);

        if (user) { k.Enqueue(_game.Id); k.StartQueue(); }
        else { k.EnqueueWhenIdle(_game.Id); Plays(k, null); }   // a driver update's re-warm: background, paused while a game is played
        release.Set();
        await Until(() => warmer.Started.Count == 2);
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.Equal(user ? (3, WarmPriority.BelowNormal) : (2, WarmPriority.Idle), (warmer.Options!.Threads, warmer.Options.Priority));
        Plays(k, null);
    }

    static void Plays(ScsKiller k, string? exe)
    {
        k.RunningGameExes = () => exe == null ? new HashSet<string>() : new HashSet<string> { exe };
        for (int i = 0; i < (exe == null ? ScsKiller.ExitPolls : 1); i++) k.PollGames();
    }

    /// <summary>An app update brings a newer planner: the check of a plan an older one built runs without waiting for idle,
    /// and the page has the new plan's coverage and count. Before it ran, the saved plan's; after the compile, the same.</summary>
    [Fact]
    public async Task An_older_planners_plan_is_checked_at_once_and_its_page_has_the_new_plan()
    {
        List<PsoDb.Rec> records = [new('B', [.. new byte[20], 9]), new('P', [1]), new('P', [2]), new('C', [3])];
        var stats = new PlanStats(0, 4, 4, 1, true, StageSets: 3, LeftOut: 2);   // a third of the stage sets
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: records, statsFor: _ => stats), warmer);
        k.IdleTime = () => TimeSpan.Zero;   // the user is at the PC throughout
        await k.ScanAsync(default);
        await Compile(k);
        OlderPlanner(k);
        records.AddRange([new('P', [4]), new('P', [5])]);
        stats = new PlanStats(0, 6, 6, 1, true, StageSets: 3, LeftOut: 0);   // the new planner: every stage set

        var s = (await k.ScanAsync(default)).Single();   // not checked yet: the saved plan's numbers
        Assert.Equal((GameStatus.Stale, "SCSKiller can now compile more of this game"), (s.Status, s.StatusReason));
        Assert.Equal((33, (int?)null), (ScsKiller.CoveragePercent(s.Plan), ScsKiller.CompiledOfPlanPercent(s)));

        k.CheckPlans = true;
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Done, true), (k.Queue.Single().Stage, k.Queue.Single().PlanCheck));
        s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, "SCSKiller can now compile 2 more pipelines for this game", 2L), (s.Status, s.StatusReason, s.NewPipelines));
        Assert.Equal(stats, s.Plan);
        Assert.Equal((100, (int?)66), (ScsKiller.CoveragePercent(s.Plan), ScsKiller.CompiledOfPlanPercent(s)));   // 4 of its 6 compiled
        Assert.Single(warmer.Started);   // the check doesn't warm
        var built = k.Store.LoadGame(_game.Id).PlanBuiltAt;

        await Compile(k);
        s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, 100, (int?)null), (s.Status, ScsKiller.CoveragePercent(s.Plan), ScsKiller.CompiledOfPlanPercent(s)));
        Assert.Equal(stats, s.Plan);
        Assert.Equal(built, k.Store.LoadGame(_game.Id).PlanBuiltAt);   // it compiled the checked plan
        Assert.Equal(2, warmer.Started.Count);
    }

    /// <summary>New pipelines on a driver the last compile wasn't for: nothing of the plan counts as compiled.</summary>
    [Fact]
    public async Task A_new_driver_shows_no_compiled_share_of_the_plan()
    {
        var (k, _, records) = await WarmedWithPlan();
        records.AddRange([new('P', [4]), new('P', [5])]);
        OlderPlanner(k);
        k.CheckPlans = true;
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((2L, (int?)99), (k.Games.Single().NewPipelines, ScsKiller.CompiledOfPlanPercent(k.Games.Single())));

        var s = (await Killer(new FakeReader(Unreal), new FakePlanner(records: records), driver: "101.00").ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Stale, 2L, false, (int?)null), (s.Status, s.NewPipelines, s.NewOnly, ScsKiller.CompiledOfPlanPercent(s)));
        Assert.StartsWith("driver changed", s.StatusReason);
    }

    /// <summary>A compile ends while the listed state is one evaluated after its plan and before its warm (a scan meanwhile:
    /// "can now compile 2 more"). Done shows only once the warm's state is in, so a notification check then (another
    /// game's change) has nothing to tell about the pipelines just compiled.</summary>
    [Fact]
    public async Task A_compile_that_ends_is_not_told_about_as_new_pipelines()
    {
        List<PsoDb.Rec> records = [new('B', [.. new byte[20], 9]), new('P', [1]), new('P', [2]), new('C', [3])];
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: records), warmer);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await warmer.WhenStarted();
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        records.AddRange([new('P', [4]), new('P', [5])]);
        OlderPlanner(k);

        IReadOnlyList<GameState>? due = null;
        k.QueueChanged += q =>
        {
            if (q.GameId == _game.Id && q.Stage == QueueStage.Done)   // the first: the queue's stop raises it again
                due ??= NewShaders.Due(k.Games, k.Queue, new Dictionary<string, string>(), new HashSet<string>(), DateTimeOffset.UtcNow).Due;
        };
        k.Enqueue(_game.Id);
        k.StartQueue();
        await warmer.WhenStarted(2);
        var mid = (await k.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Stale, "SCSKiller can now compile 2 more pipelines for this game"), (mid.Status, mid.StatusReason));
        Assert.Single(NewShaders.Due([mid], [], new Dictionary<string, string>(), new HashSet<string>(), DateTimeOffset.UtcNow).Due);   // what a toast would say

        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.NotNull(due);
        Assert.Empty(due);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Queue_positions_leave_plan_checks_out()
    {
        var k = Killer(new FakeReader(Unreal), games: Three());
        await k.ScanAsync(default);
        await Compile(k);
        OlderPlanner(k);
        k.IdleTime = () => TimeSpan.Zero;
        Plays(k, Path.GetFileName(_game.ExePath));   // the check waits
        k.CheckPlans = true;
        await k.ScanAsync(default);
        k.Enqueue("test:b");
        k.Enqueue("test:c");
        Assert.Equal([true, false, false], k.Queue.Select(q => q.PlanCheck));

        k.MoveInQueue("test:c", 0);
        Assert.Equal([_game.Id, "test:c", "test:b"], k.Queue.Select(q => q.GameId));
        k.MoveInQueue("test:c", 1);   // the queue page's "move down": below b, wherever the check is
        Assert.Equal([_game.Id, "test:b", "test:c"], k.Queue.Select(q => q.GameId));
        foreach (var q in k.Queue) k.Remove(q.GameId);
    }

    [Fact]
    public async Task The_driver_notification_is_only_about_games_a_driver_made_stale()
    {
        var (k, _, _) = await WarmedWithPlan();
        OlderPlanner(k);   // stale only because SCSKiller plans more: its idle plan check handles it, no notification
        await k.ScanAsync(default);
        Assert.Equal(DriverUpdateMode.Ask, k.Settings.OnDriverUpdate);
        Assert.Single(k.StaleGames());
        Assert.Empty(k.DriverStaleGames());
        Assert.False(k.ShouldNotifyStale());

        var after = Killer(new FakeReader(Unreal), driver: "101.00");   // the same game, now also a new driver
        await after.ScanAsync(default);
        Assert.Contains("100.01 -> 101.00", Assert.Single(after.DriverStaleGames()).StatusReason);
        Assert.True(after.ShouldNotifyStale());
    }

    static DxgiAdapter Listed(GpuInfo gpu, uint device = 0x10) => new(gpu, device, 0x20);

    [Fact]
    public async Task A_driver_updated_while_the_app_runs_makes_its_games_stale_at_the_next_scan()
    {
        var k = Killer(new FakeReader(Unreal));
        var adapter = Gpu;
        k.Adapters = () => [Listed(adapter)];
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var changed = 0;
        k.GpuChanged += () => changed++;
        await k.ScanAsync(default);
        Assert.Equal(0, changed);
        Assert.Empty(k.DriverStaleGames());

        adapter = Gpu with { DriverVersion = "101.00", AdapterLuid = 2 };   // the same process, the same backend object
        await k.ScanAsync(default);
        Assert.Equal(1, changed);
        Assert.Equal(adapter, k.Vendor.Gpu);   // the warmer reads the new LUID from it
        var stale = Assert.Single(k.DriverStaleGames());
        Assert.Equal(GameStatus.Stale, stale.Status);
        Assert.Equal("driver changed: 100.01 -> 101.00", stale.StatusReason);
        Assert.True(k.ShouldNotifyStale());
        Assert.False(k.RedetectGpu());
    }

    [Fact]
    public async Task A_warm_records_the_driver_it_started_on_when_the_driver_changes_during_it()
    {
        var adapter = Gpu;
        ScsKiller k = null!;
        var warmer = new FakeWarmer(() => { adapter = Gpu with { DriverVersion = "101.00" }; k.RedetectGpu(); return null; });
        k = Killer(new FakeReader(Unreal), warmer: warmer);
        k.Adapters = () => [Listed(adapter)];
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("101.00", k.Vendor.Gpu.DriverVersion);
        Assert.Equal("100.01", k.Store.LoadGame(_game.Id).WarmedDriverVersion);
        await k.ScanAsync(default);
        Assert.Equal("driver changed: 100.01 -> 101.00", k.Games.Single().StatusReason);
    }

    [Fact]
    public async Task A_compile_stopped_on_another_driver_starts_over_even_while_its_version_string_lags()
    {
        WarmResult R(WarmOutcome o, long done) => new(o, done, 10000, 0, TimeSpan.FromSeconds(10), 1 << 20, "", null);
        var warmer = new ScriptedWarmer(R(WarmOutcome.Stopped, 4000), R(WarmOutcome.Completed, 10000));
        var vendor = new FakeVendor(Gpu) { Label = _ => "100.01" };   // the registry still gives the old version
        var k = Killer(new FakeReader(Unreal), warmer: warmer, vendor: vendor);
        var adapter = Gpu;
        k.Adapters = () => [Listed(adapter)];
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var stopped = k.Store.LoadGame(_game.Id);
        Assert.Equal((4000L, "0000:0010:00000020:100.01"), (stopped.ResumeAt, stopped.ResumeGpu));

        adapter = Gpu with { DriverVersion = "101.00", AdapterLuid = 2 };   // its cache lacks the first 4000
        await WarmOnce(k, _game.Id);
        Assert.Equal([0L, 0L], warmer.StartAts);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(("100.01", "0000:0010:00000020:101.00", 0L), (rec.WarmedDriverVersion, rec.WarmedDriverId, rec.ResumeAt));
    }

    [Theory]
    [InlineData("26.8.1 (32.0.31041.1004)", false)]   // the version string shown now
    [InlineData("31.0.24033.1003", false)]            // DXGI's version, as earlier builds stored it without the registry
    [InlineData("fallback 31.0.24033.1003", false)]   // the vendor's form of it
    [InlineData("26.7.1 (32.0.31031.1001)", true)]    // another driver's
    public async Task An_earlier_builds_warm_takes_the_driver_id_when_its_version_string_is_the_current_drivers(string stored, bool stale)
    {
        var dxgi = Gpu with { DriverVersion = "31.0.24033.1003" };
        var label = "26.8.1 (32.0.31041.1004)";
        var vendor = new FakeVendor(dxgi with { DriverVersion = label }) { Label = _ => label };
        var k = Killer(new FakeReader(Unreal), vendor: vendor);
        k.Adapters = () => [Listed(dxgi)];
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var rec = k.Store.LoadGame(_game.Id);
        (rec.WarmedDriverVersion, rec.WarmedDriverId) = (stored, null);   // as 1.1.0 left it
        k.Store.SaveGame(_game.Id, rec);

        await k.ScanAsync(default);
        Assert.Equal(stale, k.Games.Single().Status == GameStatus.Stale);
        Assert.Equal(stale ? null : "0000:0010:00000020:31.0.24033.1003", k.Store.LoadGame(_game.Id).WarmedDriverId);
        if (stale) return;
        label = "26.8.1";   // the registry read differently: the same driver, another string
        vendor.Refresh(dxgi);
        Assert.Equal("26.8.1", k.Vendor.Gpu.DriverVersion);
        await k.ScanAsync(default);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Empty(k.DriverStaleGames());
    }

    [Fact]
    public async Task An_earlier_builds_warm_is_not_adopted_by_a_version_string_that_lags_a_driver_install()
    {
        (string?, string?) registry = ("26.8.1", "32.0.31041.1004");
        var dxgi = Gpu with { Vendor = GpuVendor.Amd, DriverVersion = "31.0.24033.1003" };
        var amd = new AmdBackend(dxgi, _ => registry);
        var k = Killer(new FakeReader(Unreal), vendor: amd);
        k.AppCache = null;   // never the real DxcCache
        k.Adapters = () => [Listed(dxgi)];
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);

        dxgi = dxgi with { DriverVersion = "31.0.24033.2001", AdapterLuid = 2 };   // installed; the registry not yet
        Assert.True(k.RedetectGpu());
        Assert.Equal("26.8.1 (32.0.31041.1004)", k.Vendor.Gpu.DriverVersion);
        var rec = k.Store.LoadGame(_game.Id);
        (rec.WarmedDriverVersion, rec.WarmedDriverId) = ("26.8.1 (32.0.31041.1004)", null);   // as 1.1.0 left it, for the old driver
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        Assert.Null(k.Store.LoadGame(_game.Id).WarmedDriverId);
        var lagging = Assert.Single(k.DriverStaleGames());   // DXGI moved: the old string shown is no proof of the old driver
        Assert.Equal("driver changed: 26.8.1 (32.0.31041.1004) -> 26.8.1 (32.0.31041.1004)", lagging.StatusReason);
        Assert.True(k.ShouldNotifyStale());

        registry = ("26.9.1", "32.0.31051.1001");
        await k.ScanAsync(default);
        Assert.Null(k.Store.LoadGame(_game.Id).WarmedDriverId);
        var stale = Assert.Single(k.DriverStaleGames());
        Assert.Equal("driver changed: 26.8.1 (32.0.31041.1004) -> 26.9.1 (32.0.31051.1001)", stale.StatusReason);
    }

    [Fact]
    public async Task Without_a_DXGI_version_the_driver_id_stays_and_an_unknown_one_judges_nothing()
    {
        var k = Killer(new FakeReader(Unreal));
        var adapter = Gpu;
        k.Adapters = () => [Listed(adapter)];
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var id = k.DriverId;
        Assert.Equal("0000:0010:00000020:100.01", id);

        adapter = Gpu with { DriverVersion = "" };   // DXGI gave no version this time: the last one stays
        await k.ScanAsync(default);
        Assert.Equal(id, k.DriverId);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        // a process that never had one: nothing is stale, adopted or reset until DXGI gives one
        var vendor = new FakeVendor(Gpu with { DriverVersion = "101.00" }) { Label = _ => "101.00" };
        k = Killer(new FakeReader(Unreal), vendor: vendor);
        k.Adapters = () => [Listed(adapter)];
        await k.ScanAsync(default);
        Assert.Null(k.DriverId);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Empty(k.DriverStaleGames());
        adapter = Gpu with { DriverVersion = "101.00" };
        await k.ScanAsync(default);
        Assert.Equal("0000:0010:00000020:101.00", k.DriverId);
        Assert.Equal("driver changed: 100.01 -> 101.00", Assert.Single(k.DriverStaleGames()).StatusReason);
    }

    [Fact]
    public async Task An_earlier_builds_skip_is_moved_to_the_driver_id_once_and_only_for_the_current_driver()
    {
        await Warmed();
        var k = Killer(new FakeReader(Unreal), driver: "101.00");
        await k.ScanAsync(default);
        k.DismissStale();
        var skipped = k.Store.LoadDismissed();
        var build = skipped[_game.Id].Split('|', 2)[1];
        k.Store.SaveDismissed(new() { [_game.Id] = $"101.00|{build}" });   // as 1.1.0 saved it

        k = Killer(new FakeReader(Unreal), driver: "101.00");
        await k.ScanAsync(default);
        Assert.Equal($"0000:0010:00000020:101.00|{build}", k.Store.LoadDismissed()[_game.Id]);
        Assert.False(k.ShouldNotifyStale());

        k.Store.SaveDismissed(new() { [_game.Id] = $"100.50|{build}" });   // skipped for a driver before this one
        k = Killer(new FakeReader(Unreal), driver: "101.00");
        await k.ScanAsync(default);
        Assert.Equal($"100.50|{build}", k.Store.LoadDismissed()[_game.Id]);
        Assert.True(k.ShouldNotifyStale());
    }

    [Fact]
    public async Task A_compile_stopped_before_its_ReShade_layer_changed_starts_over()
    {
        WarmResult R(WarmOutcome o, long done) => new(o, done, 10000, 0, TimeSpan.FromSeconds(10), 1 << 20, "", null);
        var warmer = new ScriptedWarmer(R(WarmOutcome.Stopped, 4000), R(WarmOutcome.Completed, 10000), R(WarmOutcome.Stopped, 3000), R(WarmOutcome.Completed, 10000));
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((4000L, null), (k.Store.LoadGame(_game.Id).ResumeAt, k.Store.LoadGame(_game.Id).ResumeLayer));

        File.WriteAllBytes(Path.Combine(_exeDir, "dxgi.dll"), ReShadeDll);   // RenoDX installed while stopped: the first 4000 went through none
        var addon = Path.Combine(_exeDir, "renodx-ff7rebirth.addon64");
        File.WriteAllBytes(addon, RenoDxAddon);
        await WarmOnce(k, _game.Id);
        Assert.Equal([0L, 0L], warmer.StartAts);

        k.Enqueue(_game.Id);   // stopped at 3000 through this layer
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3000, k.Store.LoadGame(_game.Id).ResumeAt);
        Assert.NotNull(k.Store.LoadGame(_game.Id).ResumeLayer);
        File.WriteAllBytes(addon, [.. RenoDxAddon, .. "a newer build"u8]);
        await WarmOnce(k, _game.Id);
        Assert.Equal([0L, 0L, 0L, 0L], warmer.StartAts);
        Assert.Equal(0, k.Store.LoadGame(_game.Id).ResumeAt);
    }

    /// <summary>A copy of the layer goes into a compile only when it reproduces what the game loads: ReShade under a name
    /// the game loads by itself, and no Luma add-on (its shader files aren't copied). Otherwise a mod that changes every
    /// pipeline blocks the game, and one that replaces some shaders only gets the note.</summary>
    [Fact]
    public void A_layer_a_copy_cant_reproduce_is_never_copied()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, "dxgi.dll"), ReShadeDll);
        var luma = Path.Combine(_exeDir, "Luma-Game.addon");
        File.WriteAllBytes(luma, LumaAddon);
        var r = Core.Games.ReShade.Detect(_game)!;
        Assert.Equal(("Luma", false, false, null), (r.ShaderMod!.Mod, r.Layered, r.Blocks, r.Fingerprint));
        File.Delete(luma);

        File.WriteAllBytes(Path.Combine(_exeDir, "renodx-ff7rebirth.addon64"), RenoDxAddon);
        Assert.True(Core.Games.ReShade.Detect(_game)!.Layered);
        File.Move(Path.Combine(_exeDir, "dxgi.dll"), Path.Combine(_exeDir, "ReShade64.dll"));   // loaded only if something injects it
        r = Core.Games.ReShade.Detect(_game)!;
        Assert.Equal((AddonKind.LayoutInjecting, false, true, null), (r.ShaderMod!.Kind, r.Layered, r.Blocks, r.Fingerprint));
    }

    /// <summary>Each layer a copy can't reproduce says which case it is and its fix, in one line naming the mod.</summary>
    [Fact]
    public void A_blocking_layer_names_its_case_and_fix()
    {
        string In(string name) => Path.Combine(_exeDir, name);
        (LayerBlock, string?) Why() => Core.Games.ReShade.Detect(_game) is { Blocks: true } r ? (r.Block, ScsKiller.ShaderModReason(r.ShaderMod!.Mod!, r.Block)) : (LayerBlock.None, null);
        File.WriteAllBytes(In("renodx-ff7rebirth.addon64"), RenoDxAddon);
        File.WriteAllBytes(In("ReShade64.dll"), ReShadeDll);
        Assert.Equal((LayerBlock.UnloadedName, "Rename ReShade to dxgi.dll next to the exe to compile through RenoDX"), Why());

        File.Move(In("ReShade64.dll"), In("dxgi.dll"));
        Assert.Equal((LayerBlock.None, null), Why());
        File.WriteAllBytes(In("Luma-Game.addon"), LumaAddon);
        Assert.Equal((LayerBlock.Luma, "Luma isn't supported with RenoDX yet"), Why());
        File.Delete(In("Luma-Game.addon"));

        foreach (var name in new[] { "dxgi.dll", "renodx-ff7rebirth.addon64" }) File.Move(In(name), Path.Combine(_game.InstallDir, name));
        Assert.Equal((LayerBlock.NotBesideExe, "ReShade must be next to the game's exe to compile through RenoDX"), Why());
    }

    /// <summary>Special K as dxgi.dll holds the name of ReShade's add-on export, to look it up, and loads ReShade as a plug-in
    /// with the add-ons beside the exe: it is never taken for ReShade, and a RenoDX add-on that changes every pipeline
    /// beside it blocks the game, with ReShade64.dll there or ReShade in Special K's own folders.</summary>
    [Fact]
    public async Task Special_K_loading_ReShade_blocks_a_mod_that_changes_every_pipeline()
    {
        string In(string name) => Path.Combine(_exeDir, name);
        File.WriteAllBytes(In("dxgi.dll"), Planning.MiddlewarePackTests.Pe("SpecialK64.dll", "ReShadeRegisterAddon"u8.ToArray()));
        Assert.Null(Core.Games.ReShade.Detect(_game));

        File.WriteAllBytes(In("renodx-ff7rebirth.addon64"), RenoDxAddon);
        var r = Core.Games.ReShade.Detect(_game)!;
        Assert.Equal((In("dxgi.dll"), LayerBlock.SpecialK, true, false, null), (r.Dll, r.Block, r.Blocks, r.Layered, r.Fingerprint));
        Assert.Equal("Rename ReShade to dxgi.dll next to the exe to compile through RenoDX", ScsKiller.ShaderModReason("RenoDX", r.Block));

        File.WriteAllBytes(In("ReShade64.dll"), ReShadeDll);
        r = Core.Games.ReShade.Detect(_game)!;
        Assert.Equal((In("ReShade64.dll"), LayerBlock.SpecialK, true), (r.Dll, r.Block, r.Blocks));

        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Unsupported, true, ScsKiller.ShaderModReason("RenoDX", LayerBlock.SpecialK), ScsKiller.SkipShaderMod),
            (s.Status, s.ShaderModBlocks, s.StatusReason, s.RecorderSkip));
        Assert.Null(k.LayerFor(_game, Path.Combine(_root, "layer-work")));

        File.Delete(In("ReShade64.dll"));
        File.WriteAllText(In("ReShade.ini"), "[ADDON]\nAddonPath=addons\n");   // Special K still loads the ones beside the exe
        Directory.CreateDirectory(In("addons"));
        Assert.Equal((LayerBlock.SpecialK, true), (Core.Games.ReShade.Detect(_game)!.Block, Core.Games.ReShade.Detect(_game)!.Blocks));
        File.Delete(In("ReShade.ini"));

        var xbox = _game with { Store = Store.Xbox };   // in the package root, it still loads the add-ons beside the exe
        File.Move(In("dxgi.dll"), Path.Combine(xbox.InstallDir, "dxgi.dll"));
        Assert.Equal((Path.Combine(xbox.InstallDir, "dxgi.dll"), LayerBlock.SpecialK, true), (Core.Games.ReShade.Detect(xbox)!.Dll, Core.Games.ReShade.Detect(xbox)!.Block, Core.Games.ReShade.Detect(xbox)!.Blocks));
        File.Move(Path.Combine(xbox.InstallDir, "dxgi.dll"), In("dxgi.dll"));

        File.Move(In("dxgi.dll"), In("d3d9.dll"));   // a name the exe doesn't import: never loaded
        File.WriteAllBytes(In("dxgi.dll"), ReShadeDll);
        Assert.Equal((LayerBlock.None, true), (Core.Games.ReShade.Detect(_game)!.Block, Core.Games.ReShade.Detect(_game)!.Layered));
        File.Delete(In("d3d9.dll"));

        File.Move(In("dxgi.dll"), In("ReShade64.dll"));   // ReShade loaded by something else: as before
        Assert.Equal(LayerBlock.UnloadedName, Core.Games.ReShade.Detect(_game)!.Block);
    }

    /// <summary>A DLL under one of ReShade's names, without its version resource or description, is ReShade when it exports
    /// ReShadeRegisterAddon.</summary>
    [Fact]
    public void ReShade_without_a_version_resource_is_known_by_its_addon_export()
    {
        var dll = Planning.MiddlewarePackTests.Pe("ReShade64.dll");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dll.AsSpan(0x200 + 24), 1);              // NumberOfNames
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dll.AsSpan(0x200 + 32), 0x1000 + 0x80);  // AddressOfNames
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dll.AsSpan(0x200 + 0x80), 0x1000 + 0x90);
        "ReShadeRegisterAddon\0"u8.CopyTo(dll.AsSpan(0x200 + 0x90));
        File.WriteAllBytes(Path.Combine(_exeDir, "dxgi.dll"), dll);
        Assert.Equal(Path.Combine(_exeDir, "dxgi.dll"), Core.Games.ReShade.Detect(_game)?.Dll);
    }

    /// <summary>The whole chain's copy takes OptiDllPath only inside the exe's folder, and libraries only as PE files within
    /// a per-file cap and a total budget; an OptiScaler.ini with a header that isn't whole on its line isn't copied at all.</summary>
    [Fact]
    public void The_optiscaler_chain_copy_stays_in_the_exe_folder_and_within_its_caps()
    {
        string In(string name) => Path.Combine(_exeDir, name);
        byte[] Lib(int size) => [.. Planning.MiddlewarePackTests.Pe(null), .. new byte[size]];
        File.WriteAllBytes(In("dxgi.dll"), Planning.MiddlewarePackTests.Pe("OptiScaler.dll"));
        File.WriteAllBytes(In("ReShade64.dll"), ReShadeDll);
        File.WriteAllBytes(In("renodx-ue-extended.addon64"), RenoDxAddon);
        var outside = Path.GetFullPath(Path.Combine(_exeDir, "..", "elsewhere"));   // where each escape below points
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(In("OptiScaler"));
        File.WriteAllBytes(Path.Combine(outside, "libxess.dll"), Lib(1));
        File.WriteAllBytes(In(Path.Combine("OptiScaler", "libxess.dll")), Lib(2));
        string[] Staged(string optiDllPath, long maxFile = 128L << 20, long budget = 512L << 20)
        {
            File.WriteAllLines(In("OptiScaler.ini"), ["[Plugins]", "LoadReshade=true", "[Libraries]", $"OptiDllPath={optiDllPath}"]);
            var dir = Core.Games.ReShade.Stage(Core.Games.ReShade.Detect(_game)!, Path.Combine(_root, "stage"), maxFile, budget);
            return [.. Directory.GetFiles(dir).Where(f => Path.GetFileName(f).StartsWith("lib")).Select(f => $"{Path.GetFileName(f)}:{new FileInfo(f).Length}").Order()];
        }
        var mine = $"libxess.dll:{Lib(2).Length}";
        foreach (var escape in new[] { outside, @"..\elsewhere", @"OptiScaler\..\..\elsewhere", @"\\server\share", outside[..2] + "elsewhere" })
            Assert.Equal([mine], Staged(escape));   // the default OptiScaler\ instead
        Assert.Equal([mine], Staged("OptiScaler"));
        Assert.Equal(["[Plugins]", "LoadReshade=true", "[Libraries]", "[Hotfix]", "CheckForUpdate=false"], File.ReadAllLines(Path.Combine(_root, "stage", "OptiScaler.ini")));   // no [Hotfix]: one added

        File.WriteAllText(In("libxell.dll"), "not a PE file, though named like one");
        File.WriteAllBytes(In("libxess_fg.dll"), Lib(5000));
        File.WriteAllBytes(In("libxess_dx11.dll"), Lib(100));
        Assert.Equal([$"libxess_dx11.dll:{Lib(100).Length}", mine], Staged("auto", maxFile: 3000));   // libxess_fg.dll over the file cap
        Assert.Equal([mine], Staged("auto", budget: Lib(2).Length + 50));   // the rest over the budget
        Assert.Equal(LayerBlock.None, Core.Games.ReShade.Detect(_game)!.Block);

        foreach (var header in new[] { "[", "[Log", "[\tLog" })
        {
            File.WriteAllLines(In("OptiScaler.ini"), ["[Plugins]", "LoadReshade=true", header, "LogFileName=C:\\x.log"]);
            var r = Core.Games.ReShade.Detect(_game)!;
            Assert.Equal((LayerBlock.OptiScalerIni, true, null), (r.Block, r.Blocks, r.Fingerprint));
        }
        Assert.Equal("Fix the section headers in OptiScaler.ini to compile through RenoDX", ScsKiller.ShaderModReason("RenoDX", LayerBlock.OptiScalerIni));
    }

    /// <summary>OptiScaler loads ReShade64.dll from the exe's folder when its OptiScaler.ini sets [Plugins] LoadReshade=true,
    /// under any name OptiScaler works as by itself; otherwise (absent, "auto", false, no ini) it doesn't.</summary>
    [Fact]
    public void ReShade64_is_loaded_when_an_optiscaler_beside_the_exe_says_so()
    {
        string In(string name) => Path.Combine(_exeDir, name);
        ReShadeInstall Detect() => Core.Games.ReShade.Detect(_game)!;
        File.WriteAllBytes(In("ReShade64.dll"), ReShadeDll);
        File.WriteAllBytes(In("renodx-ue-extended.addon64"), RenoDxAddon);
        File.WriteAllBytes(In("dxgi.dll"), Planning.MiddlewarePackTests.Pe("OptiScaler.dll"));
        Assert.Equal((In("ReShade64.dll"), (bool?)false, LayerBlock.OptiScalerOff), (Detect().Dll, Detect().OptiScalerLoads, Detect().Block));   // no OptiScaler.ini
        Assert.Equal("Set LoadReshade=true in OptiScaler.ini to compile through RenoDX", ScsKiller.ShaderModReason("RenoDX", Detect().Block));
        foreach (var value in new[] { "auto", "false", "true ; a comment" })
        {
            File.WriteAllLines(In("OptiScaler.ini"), ["[Plugins]", "LoadSpecialK=true", $"LoadReshade={value}"]);
            Assert.Equal((LayerBlock.OptiScalerOff, true), (Detect().Block, Detect().Blocks));
        }
        File.WriteAllLines(In("OptiScaler.ini"), ["[Upscalers]", "LoadReshade=true", "[plugins]", "; LoadReshade=false", "loadreshade = True"]);
        Assert.Equal((LayerBlock.None, true, false), (Detect().Block, Detect().Layered, Detect().Blocks));
        Assert.NotNull(Detect().Fingerprint);

        File.WriteAllLines(In("OptiScaler.ini"), ["[Plugins] ; plugins", "LoadReshade=true", "[Hotfix]x", "LoadReshade=false"]);   // a header ends at its ']'
        Assert.Equal(LayerBlock.None, Detect().Block);
        File.WriteAllLines(In("OptiScaler.ini"), ["[Plugins]", "LoadSpecialK=true", "[Hotfix", "LoadReshade=true"]);   // an unterminated header leaves [Plugins]
        Assert.Equal(LayerBlock.OptiScalerOff, Detect().Block);
        File.WriteAllLines(In("OptiScaler.ini"), ["[Plugins", "LoadReshade=true"]);
        Assert.Equal(LayerBlock.OptiScalerOff, Detect().Block);
        File.WriteAllLines(In("OptiScaler.ini"), ["[Plugins]", "LoadReshade=true"]);

        File.Move(In("dxgi.dll"), In("winmm.dll"));
        Assert.Equal((null, LayerBlock.UnloadedName), (Detect().OptiScalerLoads, Detect().Block));   // the exe doesn't import winmm.dll
        File.WriteAllBytes(_game.ExePath, Planning.MiddlewarePackTests.PeImporting("WINMM.dll"));
        Assert.Equal(LayerBlock.None, Detect().Block);
        File.Move(In("winmm.dll"), In("OptiScaler.asi"));   // needs an ASI loader
        Assert.Equal((null, LayerBlock.UnloadedName), (Detect().OptiScalerLoads, Detect().Block));
        File.Move(In("OptiScaler.asi"), In("version.dll"));
        File.WriteAllBytes(In("version.dll"), Planning.MiddlewarePackTests.Pe("version.dll"));   // another proxy
        Assert.Equal(LayerBlock.UnloadedName, Detect().Block);
    }

    [Fact]
    public async Task A_resume_point_without_its_driver_from_an_earlier_build_starts_over()
    {
        WarmResult R(WarmOutcome o, long done) => new(o, done, 10000, 0, TimeSpan.FromSeconds(10), 1 << 20, "", null);
        var warmer = new ScriptedWarmer(R(WarmOutcome.Stopped, 4000), R(WarmOutcome.Completed, 10000));
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(4000, rec.ResumeAt);
        rec.ResumeGpu = null;   // as 1.1.0 left it, stopped on whatever driver was there then
        k.Store.SaveGame(_game.Id, rec);
        await WarmOnce(k, _game.Id);
        Assert.Equal([0L, 0L], warmer.StartAts);
    }

    [Fact]
    public async Task A_GPU_missing_for_a_while_is_not_replaced_by_another_and_asks_for_a_restart_only_when_it_stays_gone()
    {
        var k = Killer(new FakeReader(Unreal));
        var at = DateTimeOffset.UnixEpoch;
        k.Clock = () => at;
        var igpu = Listed(Gpu with { Name = "Other GPU", AdapterLuid = 5, DriverVersion = "200.00" }, device: 0x30);
        IReadOnlyList<DxgiAdapter> listed = [Listed(Gpu), igpu];
        k.Adapters = () => listed;
        await k.ScanAsync(default);

        listed = [igpu];   // the same vendor's other adapter is now the only one: never followed
        Assert.False(k.RedetectGpu());
        Assert.Equal(Gpu, k.Vendor.Gpu);
        at += TimeSpan.FromMinutes(10);
        Assert.False(k.RedetectGpu());
        at += TimeSpan.FromMinutes(6);
        Assert.True(k.RedetectGpu());
        Assert.Equal("Restart SCSKiller to use Other GPU", k.GpuRestartNote);
        Assert.Equal(Gpu, k.Vendor.Gpu);

        var back = Gpu with { AdapterLuid = 7, DriverVersion = "101.00" };   // reloaded: a new LUID, the same device
        listed = [igpu, Listed(back)];
        Assert.True(k.RedetectGpu());
        Assert.Null(k.GpuRestartNote);
        Assert.Equal(back, k.Vendor.Gpu);
    }

    [Fact]
    public async Task An_incomplete_driver_version_is_read_again_at_every_check_until_it_is_complete()
    {
        var vendor = new FakeVendor(Gpu);
        var k = Killer(new FakeReader(Unreal), vendor: vendor);
        var adapter = Gpu;
        k.Adapters = () => [Listed(adapter)];
        await k.ScanAsync(default);
        var reads = vendor.Refreshes;

        (vendor.Incomplete, adapter) = (true, Gpu with { DriverVersion = "101.00" });
        Assert.True(k.RedetectGpu());
        Assert.Equal("partial", k.Vendor.Gpu.DriverVersion);
        Assert.False(k.RedetectGpu());   // the same adapter, read again
        Assert.Equal(reads + 2, vendor.Refreshes);

        vendor.Incomplete = false;
        Assert.True(k.RedetectGpu());
        Assert.Equal("101.00", k.Vendor.Gpu.DriverVersion);
        Assert.False(k.RedetectGpu());
        Assert.Equal(reads + 3, vendor.Refreshes);
    }

    [Fact]
    public async Task An_older_record_takes_its_fingerprint_from_the_plan_its_warm_replayed()
    {
        var (k, warmer, _) = await WarmedWithPlan();
        OlderPlanner(k, fingerprint: false);
        await Compile(k);
        Assert.Equal("already compiled: the new plan adds nothing", k.Queue.Single().Note);
        Assert.Single(warmer.Started);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.NotNull(rec.WarmedPlanItems);
        Assert.Equal((Planner.Version, rec.PlanItems), (rec.WarmedPlanVersion, rec.WarmedPlanItems));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        // plan.bin built by another planner than the warmed one, warmed before warms kept their keys: not the warmed plan,
        // so it may compile more (re-warmed)
        OlderPlanner(k, fingerprint: false);
        rec = k.Store.LoadGame(_game.Id);
        (rec.WarmKeysFile, rec.PlanKeysFile) = (null, null);
        rec.PlanVersion = Planner.Version - 2;
        k.Store.SaveGame(_game.Id, rec);
        await Compile(k);
        Assert.Equal(2, warmer.Started.Count);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Maximum_mode_rewarms_only_a_per_stage_warm()
    {
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { DriverVersion = "100.01" }, perStage: true));
        async Task Compile()
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        }
        await k.ScanAsync(default);
        await Compile();
        Assert.True(k.Store.LoadGame(_game.Id).WarmedPerStage);

        await SetSettings(k, k.Settings with { MaximumPlans = true });   // every pairing is new work
        Assert.Equal(GameStatus.Stale, k.Games.Single().Status);
        Assert.Contains("Maximum mode", k.Games.Single().StatusReason);
        await Compile();
        var rec = k.Store.LoadGame(_game.Id);
        Assert.False(rec.PlanPerStage || rec.WarmedPerStage);

        await SetSettings(k, k.Settings with { MaximumPlans = false });  // a pairing warm already holds every stage unit
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        await Compile();   // the plan is rebuilt per-stage
        Assert.True(k.Store.LoadGame(_game.Id).PlanPerStage);
    }

    [Fact]
    public async Task Middleware_next_to_the_exe_is_listed_by_its_player_name()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, "libxess.dll"), []);
        File.WriteAllBytes(Path.Combine(_exeDir, "nvngx_dlss.dll"), []);
        File.WriteAllBytes(Path.Combine(_exeDir, "unrelated.dll"), []);
        var s = (await Killer(new FakeReader(Unreal)).ScanAsync(default)).Single();
        Assert.Equal(["DLSS", "XeSS"], s.Middleware!.Select(t => t.Label).Order());
        Assert.All(s.Middleware!, t => Assert.Equal(0, t.Pipelines));   // no packs: detected only

        // OptiScaler folds what it bundles (XeSS, FSR4, FidelityFX); DirectStorage (no pack) is hidden; DLSS stays
        File.WriteAllBytes(Path.Combine(_exeDir, "OptiScaler.dll"), []);
        File.WriteAllBytes(Path.Combine(_exeDir, "amdxcffx64.dll"), []);
        File.WriteAllBytes(Path.Combine(_exeDir, "dstoragecore.dll"), []);
        var packs = Path.Combine(_root, "packs");
        var planner = new Planner(packs);
        s = (await Killer(new FakeReader(Unreal), planner).ScanAsync(default)).Single();
        Assert.Equal(["DLSS", "OptiScaler"], s.Middleware!.Select(t => t.Label).Order());
        var opti = s.Middleware!.Single(t => t.Label == "OptiScaler");
        Assert.Equal(["OptiScaler.dll", "amdxcffx64.dll", "libxess.dll"], opti.Dlls.Order(StringComparer.Ordinal));
        Assert.Equal(0, opti.Pipelines);

        // a pack for this FSR4 DLL version: it heads the OptiScaler tag, with its pipelines
        var fsr4 = Middleware.Detect(_exeDir).Single(d => d.Name == "amdxcffx64.dll");
        var pack = new MiddlewarePack(fsr4.Vendor, fsr4.Name, Middleware.Scan(fsr4.Path).ContentHash, 0);
        pack.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, new string('1', 40))), "test");
        pack.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, new string('2', 40))), "test");
        pack.Write(planner.Packs!.PathOf(fsr4.Vendor, fsr4.Name, pack.Header.ContentHash));
        s = (await Killer(new FakeReader(Unreal), planner).ScanAsync(default)).Single();
        Assert.Equal(("OptiScaler (FSR4)", 2), (s.Middleware![0].Label, s.Middleware[0].Pipelines));
        Assert.Equal(0, s.Middleware!.Single(t => t.Label == "DLSS").Pipelines);
    }

    [Fact]
    public void Concurrent_saves_and_loads_of_one_file_never_fail()
    {
        var store = new AppStore(Path.Combine(_root, "data"));
        var scan = new Dictionary<string, Evaluation> { ["g"] = new("k", null, AntiCheat.None, new PlanCheck(Readiness.Ready, "r")) };
        Parallel.For(0, 400, i => { if (i % 2 == 0) store.SaveScan(scan); else store.LoadScan(); });   // the app + the CLI at once
        Assert.Equal("k", store.LoadScan()["g"].Key);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data"), "*.tmp"));
    }

    [Fact]
    public async Task Store_version_decides_staleness_with_the_exe_stamp_as_fallback()
    {
        var exe = _game.ExePath;
        await Warmed(_game with { Version = "100" });
        Assert.Equal("100", Killer().Store.LoadGame(_game.Id).WarmedGameVersion);

        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(5));   // e.g. Steam re-verified the files: same build
        var k = Killer(new FakeReader(Unreal), game: _game with { Version = "100" });
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);

        k = Killer(new FakeReader(Unreal), game: _game with { Version = "101" });
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Stale, s.Status);
        Assert.Contains("build 100 -> 101", s.StatusReason);

        // no store version (other stores, or state from before versions): the exe stamp decides
        k = Killer(new FakeReader(Unreal));
        Assert.Equal(GameStatus.Stale, (await k.ScanAsync(default)).Single().Status);
        var rec = k.Store.LoadGame(_game.Id);
        (rec.WarmedGameVersion, rec.WarmedExeStamp) = (null, $"{new FileInfo(exe).Length}:{new FileInfo(exe).LastWriteTimeUtc.Ticks}");
        k.Store.SaveGame(_game.Id, rec);
        k = Killer(new FakeReader(Unreal), game: _game with { Version = "101" });
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);
    }

    [Fact]
    public async Task A_setting_that_switches_the_shaders_the_game_loads_makes_the_warm_stale()
    {
        var reader = new StampedReader(Unreal) { Stamp = "game.ps50|1|1" };
        var k = Killer(reader);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        reader.Stamp = "game.compatibility.ps50|2|2";   // the same exe and build, another dump
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Stale, "game shaders changed since the warm"), (s.Status, s.StatusReason));
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    sealed class StampedReader(EngineInfo engine) : IEngineReader
    {
        public string Stamp = "";
        public EngineInfo? Detect(Game game) => engine;
        public string IndexStamp(Game game) => Stamp;
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) =>
            new("content-" + Stamp, ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    [Fact]
    public async Task A_compile_after_an_update_the_scan_missed_records_the_installed_build()
    {
        await Warmed(_game with { Version = "100" });
        var source = new FakeSource([_game with { Version = "100" }]);
        var k = new ScsKiller([source], new FakeVendor(Gpu with { DriverVersion = "100.01" }), new FakeReader(Unreal), new FakePlanner(),
            new FakeWarmer(), Path.Combine(_root, "data"), _proxy) { LocalAppData = _root, Processes = Ours, ProcessNames = OurNames };
        await k.ScanAsync(default);
        source.Games = [_game with { Version = "101" }];   // the store updated the game while the app ran
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(("101", "101"), (rec.IndexGameVersion, rec.WarmedGameVersion));
        Assert.Equal(("101", GameStatus.Warmed), (k.Games.Single().Game.Version, k.Games.Single().Status));
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);
    }

    [Fact]
    public async Task Scan_reuses_detection_until_the_game_changes_and_rescan_forces_it()
    {
        var reader = new FakeReader(Unreal);
        await Killer(reader).ScanAsync(default);
        Assert.Equal(1, reader.Detects);
        var s = (await Killer(reader).ScanAsync(default)).Single();   // the next app start
        Assert.Equal(1, reader.Detects);
        Assert.Equal((Unreal, GameStatus.Ready, AntiCheat.None), (s.Engine, s.Status, s.AntiCheat));

        var patched = _game with { Version = "7" };
        await Killer(reader, game: patched).ScanAsync(default);
        Assert.Equal(2, reader.Detects);
        await Killer(reader, game: patched).ScanAsync(default);
        Assert.Equal(2, reader.Detects);
        File.SetLastWriteTimeUtc(_game.ExePath, DateTime.UtcNow.AddMinutes(1));   // exe replaced
        await Killer(reader, game: patched).ScanAsync(default);
        Assert.Equal(3, reader.Detects);
        await Killer(reader, game: patched).RescanAsync(default);
        Assert.Equal(4, reader.Detects);
    }

    /// <summary>A Deep-Rock-like game (UE4, SM5, Steam menu DirectX 12 / DirectX 11) scanned after a DX11 run, then played
    /// on DX12: the new log redoes the cached verdict without a forced rescan, and the recorder is allowed.</summary>
    [Fact]
    public async Task A_new_game_log_redoes_the_cached_graphics_api()
    {
        var saved = Directory.CreateDirectory(Path.Combine(_root, "FSD", "Saved")).FullName;
        Directory.CreateDirectory(Path.Combine(saved, "Config", "Windows"));
        var logs = Directory.CreateDirectory(Path.Combine(saved, "Logs")).FullName;
        File.WriteAllText(Path.Combine(logs, "FSD-backup.log"), "LogD3D11RHI: Chosen D3D11 Adapter:\n");
        File.SetLastWriteTimeUtc(Path.Combine(logs, "FSD-backup.log"), DateTime.UtcNow.AddHours(-1));
        var reader = new LogReader(saved);

        var s = (await Killer(reader).ScanAsync(default)).Single();
        Assert.Equal(("D3D11 (last run)", 1), (s.Engine!.GraphicsApi, reader.Detects));
        Assert.Equal(ScsKiller.SkipNotDx12, ScsKiller.RecorderSkip(s, null));
        await Killer(reader).ScanAsync(default);
        Assert.Equal(1, reader.Detects);

        File.WriteAllText(Path.Combine(logs, "FSD.log"), "LogRHI: Using Default RHI: D3D12\n");   // played through the DX12 entry
        s = (await Killer(reader).ScanAsync(default)).Single();
        Assert.Equal(("D3D12 (last run)", 2), (s.Engine!.GraphicsApi, reader.Detects));
        Assert.Null(ScsKiller.RecorderSkip(s, null));
        await Killer(reader).ScanAsync(default);
        Assert.Equal(2, reader.Detects);
    }

    /// <summary>Deleting the user's Engine.ini (which set DX12) changes the API, though a newer Input.ini is untouched.</summary>
    [Fact]
    public async Task Deleting_the_user_engine_ini_redoes_the_cached_graphics_api()
    {
        var config = Directory.CreateDirectory(Path.Combine(_root, "FSD", "Saved", "Config", "Windows")).FullName;
        File.WriteAllText(Path.Combine(config, "Engine.ini"), "[/Script/WindowsTargetPlatform.WindowsTargetSettings]\nDefaultGraphicsRHI=DefaultGraphicsRHI_DX12\n");
        File.SetLastWriteTimeUtc(Path.Combine(config, "Engine.ini"), DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(config, "Input.ini"), "");
        var reader = new LogReader(Path.Combine(_root, "FSD", "Saved"), []);

        Assert.Equal("D3D12", (await Killer(reader).ScanAsync(default)).Single().Engine!.GraphicsApi);
        File.Delete(Path.Combine(config, "Engine.ini"));
        Assert.Equal(("D3D11", 2), ((await Killer(reader).ScanAsync(default)).Single().Engine!.GraphicsApi, reader.Detects));
    }

    /// <summary>The stamp is read before Detect: evidence that changes while Detect runs is seen by the next scan.</summary>
    [Fact]
    public async Task Evidence_changing_during_detection_redoes_it_next_scan()
    {
        var reader = new StampReader { Api = "D3D11" };
        reader.During = () => { reader.Stamp = "b"; reader.Api = "D3D12"; reader.During = null; };   // a log written after Detect read the old one
        Assert.Equal("D3D11", (await Killer(reader).ScanAsync(default)).Single().Engine!.GraphicsApi);
        Assert.Equal(("D3D12", 2), ((await Killer(reader).ScanAsync(default)).Single().Engine!.GraphicsApi, reader.Detects));
        await Killer(reader).ScanAsync(default);
        Assert.Equal(2, reader.Detects);
    }

    /// <summary>Two scans at once: the one that started first and ends last doesn't replace the newer verdict in the cache.</summary>
    [Fact]
    public async Task An_older_evaluation_does_not_overwrite_a_newer_one()
    {
        var reader = new StampReader { Api = "D3D11", Gate = new() };
        var k = Killer(reader);
        var older = k.RescanAsync(default);
        Assert.True(reader.Entered.Wait(TimeSpan.FromSeconds(10)));
        reader.Api = "D3D12";
        Assert.Equal("D3D12", (await k.RescanAsync(default)).Single().Engine!.GraphicsApi);
        reader.Gate.Set();
        await older;   // its Detect read D3D11
        Assert.Equal(("D3D12", 2), ((await Killer(reader).ScanAsync(default)).Single().Engine!.GraphicsApi, reader.Detects));   // scan.json kept the newer
    }

    /// <summary>An evaluation whose full anti-cheat check came back clean, paused before it stores its verdict, doesn't clear
    /// the anti-cheat a newer evaluation found meanwhile, in memory or in scan.json.</summary>
    [Fact]
    public async Task A_clean_evaluation_paused_after_its_anti_cheat_check_does_not_clear_a_newer_finding()
    {
        var reader = new StampReader();
        var k = Killer(reader);
        k.ProcessNames = () => new HashSet<string>();
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var pauses = 0;
        k.EvaluateStep = step => { if (step == "checked" && Interlocked.Increment(ref pauses) == 1) { entered.Set(); gate.Wait(TimeSpan.FromSeconds(10)); } };
        var older = k.RescanAsync(default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));   // its full anti-cheat check is done, clean
        var eac = Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat")).FullName;
        Assert.Equal(AntiCheat.EasyAntiCheat, (await k.RescanAsync(default)).Single().AntiCheat);
        Directory.Delete(eac);   // stands for a marker a cache hit's quick check doesn't see
        gate.Set();
        await older;
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.Equal(AntiCheat.EasyAntiCheat, (await Killer(reader).ScanAsync(default)).Single().AntiCheat);   // scan.json kept it
    }

    /// <summary>Anti-cheat that appears while Detect runs is found by the full check after it, in memory and in scan.json.</summary>
    [Fact]
    public async Task Anti_cheat_appearing_during_detection_is_not_cached_as_clean()
    {
        var reader = new StampReader { During = () => Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat")) };
        var k = Killer(reader);
        k.ProcessNames = () => new HashSet<string>();
        Assert.Equal(AntiCheat.EasyAntiCheat, (await k.ScanAsync(default)).Single().AntiCheat);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.Equal(AntiCheat.EasyAntiCheat, (await Killer(reader).ScanAsync(default)).Single().AntiCheat);   // scan.json
    }

    [Fact]
    public async Task Dismissed_stale_games_stay_listed_but_are_not_notified_until_the_driver_changes()
    {
        await Warmed();
        var k = Killer(new FakeReader(Unreal), driver: "101.00");
        await k.ScanAsync(default);
        Assert.True(k.ShouldNotifyStale());
        k.DismissStale();
        Assert.False(k.ShouldNotifyStale());

        k = Killer(new FakeReader(Unreal), driver: "101.00");   // persisted across restarts
        await k.ScanAsync(default);
        Assert.Single(k.StaleGames());
        Assert.False(k.ShouldNotifyStale());

        k = Killer(new FakeReader(Unreal), driver: "102.00");
        await k.ScanAsync(default);
        Assert.True(k.ShouldNotifyStale());
        k.Settings = k.Settings with { OnDriverUpdate = DriverUpdateMode.WhenIdle };   // only Ask notifies
        Assert.False(k.ShouldNotifyStale());
    }

    [Fact]
    public async Task When_idle_item_waits_for_idle_pauses_on_input_and_runs_as_background()
    {
        var warmer = new ControlledWarmer();
        var idle = TimeSpan.Zero;
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        k.IdleTime = () => idle;
        k.Settings = k.Settings with { Threads = 20, BackgroundThreads = 3 };
        await k.ScanAsync(default);

        k.EnqueueWhenIdle(_game.Id);
        Assert.Equal((QueueStage.Waiting, "starts when the PC is idle"), (k.Queue.Single().Stage, k.Queue.Single().Note));
        await Task.Delay(1200);
        Assert.Null(warmer.Run);                                    // the user is at the PC

        idle = TimeSpan.FromMinutes(3);
        await warmer.WhenStarted();
        Assert.Equal(new WarmOptions(3, WarmPriority.Idle, 0, ScsKiller.AutoCompileMemoryGB() * 1024), warmer.Options);   // Auto memory budget

        await Until(() => k.Queue.Single().Stage == QueueStage.Warming);
        idle = TimeSpan.FromSeconds(1);                             // input
        var t = Stopwatch.StartNew();
        await Until(() => warmer.Run!.Paused);
        Assert.True(t.Elapsed < TimeSpan.FromSeconds(1.5), $"paused after {t.Elapsed}");   // polled every 0.5 s
        await Until(() => k.Queue.Single().Stage == QueueStage.Paused);
        Assert.Equal("paused until the PC is idle", k.Queue.Single().Note);

        idle = TimeSpan.FromMinutes(3);
        await Until(() => !warmer.Run!.Paused);
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Enqueue_makes_a_waiting_when_idle_item_a_normal_one_that_runs_with_the_queue_in_the_foreground()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        k.IdleTime = () => TimeSpan.Zero;
        k.Settings = k.Settings with { Threads = 20, BackgroundThreads = 3, MaxCompileMemoryGB = 6 };
        await k.ScanAsync(default);
        k.EnqueueWhenIdle(_game.Id);
        await Task.Delay(700);
        Assert.Null(warmer.Run);
        k.Enqueue(_game.Id);
        Assert.Null(k.Queue.Single().Note);
        await Task.Delay(700);
        Assert.Null(warmer.Run);                                    // not started: the queue isn't running
        k.StartQueue();
        await warmer.WhenStarted();
        Assert.Equal(new WarmOptions(20, WarmPriority.BelowNormal, 0, 6144), warmer.Options);   // the set memory budget
        await Task.Delay(700);
        Assert.False(warmer.Run!.Paused);                           // no longer waits for idle
        warmer.Run.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Published_layout_resolves_the_task_exe_and_the_app()
    {
        var dist = Path.Combine(_root, "dist");
        var cli = Path.Combine(dist, "cli");
        Directory.CreateDirectory(cli);
        File.WriteAllText(Path.Combine(dist, "SCSKiller.exe"), "");
        File.WriteAllText(Path.Combine(cli, "scskiller.exe"), "");
        Assert.Equal(Path.Combine(cli, "scskiller.exe"), ScheduledTask.TaskExe(dist));   // called from the app
        Assert.Equal(Path.Combine(cli, "scskiller.exe"), ScheduledTask.TaskExe(cli));    // called from the CLI
        File.WriteAllText(Path.Combine(cli, "scskillerw.exe"), "");
        Assert.Equal(Path.Combine(cli, "scskillerw.exe"), ScheduledTask.TaskExe(dist));  // windowless copy preferred
        Assert.Equal(Path.Combine(dist, "SCSKiller.exe"), ScheduledTask.AppExe(cli));
        Assert.Null(ScheduledTask.AppExe(dist));
        Assert.Equal("--driver-updated", ScheduledTask.DriverUpdatedArg);
    }

    Game[] Three() => [_game, _game with { Id = "test:b", Name = "B" }, _game with { Id = "test:c", Name = "C" }];

    /// <summary>While an update is being handed over (the applying marker in the data folder), a queue item doesn't start:
    /// it waits, holding nothing, and runs once the marker is gone; stopped meanwhile, it is stopped.</summary>
    [Fact]
    public async Task A_queue_item_waits_while_an_update_is_handed_over()
    {
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        await k.ScanAsync(default);
        Busy.MarkApplying(k.Store.DataDir, k.Clock());
        k.Enqueue(_game.Id);
        k.StartQueue();
        await Task.Delay(1500);
        Assert.Empty(warmer.Started);
        Busy.ClearApplying(k.Store.DataDir);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([_game.Id], warmer.Started);

        Busy.MarkApplying(k.Store.DataDir, k.Clock());
        k.Enqueue(_game.Id);
        k.StartQueue();
        await Task.Delay(700);
        k.StopQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([_game.Id], warmer.Started);
        Assert.Equal(QueueStage.Stopped, k.Queue.Single().Stage);
        Busy.ClearApplying(k.Store.DataDir);
    }

    [Fact]
    public async Task Enqueue_waits_for_StartQueue_which_runs_the_items_in_order_and_then_stops()
    {
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer, games: Three());
        await k.ScanAsync(default);
        foreach (var id in new[] { _game.Id, "test:b", "test:c" }) k.Enqueue(id);
        int events = 0;
        k.QueueChanged += _ => Interlocked.Increment(ref events);
        await Task.Delay(700);
        Assert.Empty(warmer.Started);
        Assert.False(k.QueueRunning);

        k.MoveInQueue("test:c", 0);
        k.MoveInQueue(_game.Id, 99);   // clamped: last
        Assert.Equal(["test:c", "test:b", _game.Id], k.Queue.Select(q => q.GameId));
        Assert.Equal(2, events);

        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["test:c", "test:b", _game.Id], warmer.Started);
        Assert.False(k.QueueRunning);
        Assert.All(k.Queue, q => Assert.Equal(QueueStage.Done, q.Stage));   // finished items stay listed...
        k.Enqueue("test:b");                                                 // (a finished game queued again waits before them)
        Assert.Equal(("test:b", QueueStage.Waiting), (k.Queue[0].GameId, k.Queue[0].Stage));
        k.StartQueue();                                                      // ...until the next start clears them
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("test:b", QueueStage.Done), (k.Queue.Single().GameId, k.Queue.Single().Stage));
    }

    [Fact]
    public async Task Stop_halts_the_queue_and_the_next_start_continues_the_stopped_item_first()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer, games: Three());
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.Enqueue("test:b");
        k.StartQueue();
        Assert.True(k.QueueRunning);
        await warmer.WhenStarted();
        k.Enqueue("test:c");
        k.MoveInQueue("test:c", 0);
        Assert.Equal([_game.Id, "test:c", "test:b"], k.Queue.Select(q => q.GameId));   // the running item stays first

        k.StopQueue();
        await Until(() => k.Queue.Any(q => q.Stage == QueueStage.Stopped));
        Assert.False(k.QueueRunning);
        await Task.Delay(700);
        Assert.Equal([_game.Id], warmer.Started);                                         // nothing else started
        Assert.Equal(["test:c", "test:b", _game.Id], k.Queue.Select(q => q.GameId));   // waiting, then finished

        k.StartQueue();
        await warmer.WhenStarted(2);
        Assert.Equal([_game.Id, "test:c", "test:b"], k.Queue.Select(q => q.GameId));   // the stopped one continues first
        Assert.Equal(10, warmer.Options!.StartAt);
        warmer.Run!.Finish();
        foreach (var n in new[] { 3, 4 })
        {
            await warmer.WhenStarted(n);
            warmer.Run!.Finish();
        }
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([_game.Id, _game.Id, "test:c", "test:b"], warmer.Started);
        Assert.False(k.QueueRunning);
    }

    string CacheFile(string name, int size)
    {
        var path = Path.Combine(_root, "DXCache", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public async Task A_warm_attributes_the_cache_files_a_process_named_like_the_game_holds_open()
    {
        CacheFile("0002a91d99999999.nvph", 4096);   // an app from before
        var warmer = new FakeWarmer(() =>
        {
            CacheFile("0002a91d11111111.nvph", 65536);
            var held = new FileStream(CacheFile("fc52a91d11111111.nvph", 4096), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            CacheFile("fc52a91d22222222.nvph", 4096);   // another app starting meanwhile: new, but not open in the game's process
            return held;
        });
        // the game is named like this test process, which holds the file the way the driver does in the staged warm
        var game = NamedAs(Process.GetCurrentProcess().ProcessName + ".exe");
        var k = Killer(new FakeReader(Unreal), warmer: warmer, game: game);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (k.AppCache, k.RunningGameExes) = (new NvidiaAppCache(Path.Combine(_root, "DXCache")), () => running);   // this process plays the staged warm, not the game
        await k.ScanAsync(default);
        Assert.Null(k.Games.Single().CacheOnDisk);

        k.Enqueue(game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["11111111"], k.Store.LoadGame(game.Id).CacheKeys);
        Assert.Equal(65536 + 4096, k.Games.Single().CacheOnDisk);

        // a process named like the game runs (this one): the game or a warm of it, refused either way
        Assert.Equal("Fake Game is running", Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(game.Id)).Message);
        Assert.True(File.Exists(Path.Combine(_root, "DXCache", "0002a91d11111111.nvph")));
    }

    /// <summary>A warm filled another key than the one the game opens. An Xbox game, whose warm runs with its package identity
    /// (which may fail one time and not the next), is Stale until a warm fills its own key. Any other game is Not compatible,
    /// out of the queue, until a launch shows the game holding the warm's key.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_game_played_on_other_keys_than_its_warm_filled_is_not_warmed_until_a_warm_fills_its_own(bool xbox, bool clear)
    {
        var warmKey = "ad873243";
        var warmer = new FakeWarmer(() => new FileStream(CacheFile($"0002a91d{warmKey}.nvph", 65536), FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
        // the game is named like this test process, which holds the files the way the driver does
        var game = NamedAs(Process.GetCurrentProcess().ProcessName + ".exe") with { Store = xbox ? Store.Xbox : Store.Steam };
        var k = Killer(new FakeReader(Unreal), warmer: warmer, game: game, vendor: new FakeVendor(Gpu with { DriverVersion = "100.01" }, packageKeyed: true));
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (k.AppCache, k.RunningGameExes) = (new NvidiaAppCache(Path.Combine(_root, "DXCache")), () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); });
        async Task Launch(string key)
        {
            using (new FileStream(CacheFile($"0002a91d{key}.nvph", 4096), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                lock (running) running.Add(Path.GetFileName(game.ExePath));
                k.PollGames();
                await Until(() => k.Store.LoadGame(game.Id).GameKeys.Contains(key));
                lock (running) running.Clear();
            }
            for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        }
        await k.ScanAsync(default);
        async Task Warm()
        {
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Warm();
        Assert.Equal((GameStatus.Warmed, "ad873243"), (k.Games.Single().Status, string.Join(",", k.Store.LoadGame(game.Id).WarmedKeys!)));

        await Launch("7f303a45");
        var rec = k.Store.LoadGame(game.Id);
        Assert.Equal(["7f303a45"], rec.GameKeys);
        Assert.Equal(["7f303a45", "ad873243"], rec.CacheKeys.Order());   // Clear cache deletes both
        if (xbox)
        {
            Assert.Equal((GameStatus.Stale, ScsKiller.MissesGameReason), (k.Games.Single().Status, k.Games.Single().StatusReason));
            warmKey = "7f303a45";   // a warm with the game's package identity
            await Warm();
            Assert.Equal((GameStatus.Warmed, "7f303a45"), (k.Games.Single().Status, string.Join(",", k.Store.LoadGame(game.Id).WarmedKeys!)));
            return;
        }
        Assert.Equal((GameStatus.Unsupported, ScsKiller.CantReachReason), (k.Games.Single().Status, k.Games.Single().StatusReason));
        k.Enqueue(game.Id);   // the command line's compile queues through it too
        Assert.Equal(QueueStage.Done, Assert.Single(k.Queue).Stage);   // the first compile's, not queued again
        var newer = Killer(new FakeReader(Unreal), game: game, driver: "100.02");
        newer.AppCache = k.AppCache;
        await newer.ScanAsync(default);
        Assert.Equal(GameStatus.Stale, newer.Games.Single().Status);   // another driver may key it otherwise: a compile is offered again
        if (clear)
        {
            k.ProcessNames = () => new HashSet<string>();   // the game is named like this test process
            Assert.True(k.ClearGameCache(game.Id));
            Assert.Equal((GameStatus.Unsupported, ScsKiller.CantReachReason), (k.Games.Single().Status, k.Games.Single().StatusReason));
            await newer.ScanAsync(default);
            Assert.NotEqual(GameStatus.Unsupported, newer.Games.Single().Status);   // the miss's driver outlives Clear cache
            return;
        }

        await Launch("ad873243");   // after a driver or game update the game holds the warm's key
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        k.Enqueue(game.Id);
        Assert.Equal(QueueStage.Waiting, Assert.Single(k.Queue).Stage);
    }

    [Fact]
    public void A_warm_misses_the_game_only_when_the_games_own_keys_are_known_and_it_filled_none_of_them()
    {
        static GameRecord R(string[] cache, string[] game, string[]? warmed) => new() { CacheKeys = [.. cache], GameKeys = [.. game], WarmedKeys = warmed?.ToHashSet() };
        Assert.False(ScsKiller.WarmMissesGame(R(["a"], [], ["a"])));             // the game not seen yet
        Assert.True(ScsKiller.WarmMissesGame(R(["a", "g"], ["g"], ["a"])));
        Assert.False(ScsKiller.WarmMissesGame(R(["a", "g"], ["g"], ["g"])));
        Assert.False(ScsKiller.WarmMissesGame(R(["g"], ["g"], [])));             // the warm's keys weren't seen: unknown, not a miss
        Assert.True(ScsKiller.WarmMissesGame(R(["a", "g"], ["g"], null)));      // a record from before WarmedKeys: its warm keys are the rest
        Assert.False(ScsKiller.WarmMissesGame(R(["g"], ["g"], null)));

    }

    /// <summary>NVIDIA: a first launch after the warm whose warmed pipelines still take 20 ms or more at the median marks the
    /// game. It stays Warmed whatever else changes (no Stale re-warm, no notification) and the next complete warm clears
    /// the mark. One whose warmed pipelines hit stays plain Warmed. A warm the driver rejected most of is partly compiled
    /// instead: its rejects are why the launch still compiles.</summary>
    [Theory]
    [InlineData("1.0", false, 0)]
    [InlineData("50.0", true, 0)]
    [InlineData("50.0", false, 9_000)]
    public async Task A_warm_the_games_first_launch_still_compiles_marks_it_unreached(string ms, bool unreached, long failed)
    {
        var records = Enumerable.Range(0, 120).Select(i => new PsoDb.Rec('S', [(byte)i, 1])).ToList();
        var nvidia = new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia });
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: records), new FakeWarmer(failed: failed), vendor: nvidia);
        k.IdleTime = () => TimeSpan.FromHours(1);   // plan checks are "when idle" items
        await k.ScanAsync(default);
        await Compile(k);
        var at = k.Store.LoadGame(_game.Id).WarmedAt!.Value.ToUnixTimeMilliseconds();
        File.WriteAllLines(Path.Combine(_exeDir, "scskiller_creates.csv"), [$"#session,{at + 1},Fake-Win64-Shipping.exe",
            .. records.Select((r, i) => $"{i + 1}.0,S,0,0,{ms},{r.Key}"), $"#end,{at + 60_000}"]);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, unreached), (s.Status, s.CompileUnreached));
        Assert.Equal(unreached, s.StatusReason == ScsKiller.UnreachedReason);
        Assert.Equal(failed > 0, ScsKiller.IsPartlyCompiled(s));
        Assert.Equal(failed > 0, s.StatusReason == $"partly compiled for driver {Gpu.DriverVersion}: the driver refused {failed:N0} of {10_000:N0} pipelines, so those can still stutter the first time");
        if (!unreached) return;
        Assert.Null(NewShaders.Key(s with { NewPipelines = 5 }, new HashSet<string>()));
        var rec = k.Store.LoadGame(_game.Id);
        var warmedAt = rec.WarmedAt;
        rec.WarmedAt = null;   // as Clear cache leaves it
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Ready, true, ScsKiller.UnreachedReason), (k.Games.Single().Status, k.Games.Single().CompileUnreached, k.Games.Single().StatusReason));
        rec = k.Store.LoadGame(_game.Id);
        rec.WarmedAt = warmedAt;
        k.Store.SaveGame(_game.Id, rec);

        records.Add(new PsoDb.Rec('S', [200, 1]));   // a planner that compiles more: Stale for any other game
        OlderPlanner(k);
        k.CheckPlans = true;
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((GameStatus.Warmed, ScsKiller.UnreachedReason), (k.Games.Single().Status, k.Games.Single().StatusReason));
        Assert.Empty(k.StaleGames());

        await Compile(k);   // by hand
        s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, false), (s.Status, s.CompileUnreached));
        Assert.False(ScsKiller.IsUnreached(k.Store.LoadGame(_game.Id)));
    }

    /// <summary>NVIDIA Aftermath shader debug info in the game's config doesn't mark it: Ready, then Warmed with new-pipeline notices.</summary>
    [Fact]
    public async Task Aftermath_shader_debug_info_in_the_config_leaves_the_status_alone_on_nvidia()
    {
        var k = Killer(new FakeReader(Unreal with { AftermathShaderDebug = true }), new FakePlanner(), new FakeWarmer(), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, false), (s.Status, s.CompileUnreached));
        await Compile(k);
        s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, false, "warmed for driver 100.01"), (s.Status, s.CompileUnreached, s.StatusReason));
        Assert.NotNull(NewShaders.Key(s with { NewPipelines = 5 }, new HashSet<string>()));
    }

    /// <summary>A game on either unsupported list shows that status with the verdict's line on NVIDIA, not "the compile didn't help".</summary>
    [Theory]
    [InlineData(Core.Games.GameVerdicts.UnsupportedYetVerdict, false)]
    [InlineData(Core.Games.GameVerdicts.UnsupportedVerdict, true)]
    public async Task An_unsupported_verdict_wins_over_an_unreached_warm_on_nvidia(string verdict, bool notPlanned)
    {
        const string note = "An app update is needed for support";
        try
        {
            var k = Killer(new FakeReader(Unreal), new FakePlanner(), new FakeWarmer(), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
            await k.ScanAsync(default);
            await Compile(k);
            var rec = k.Store.LoadGame(_game.Id);
            rec.UnreachedWarm = rec.WarmedAt;   // as a judged first launch leaves it
            k.Store.SaveGame(_game.Id, rec);
            k.RefreshGame(_game.Id);
            Assert.Equal((GameStatus.Warmed, true, ScsKiller.UnreachedReason), (k.Games.Single().Status, k.Games.Single().CompileUnreached, k.Games.Single().StatusReason));
            Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.TryParse($$"""{"games":[{"name":"Fake Game","ids":["{{_game.Id}}"],"verdict":"{{verdict}}","reason":"other","note":"{{note}}","source":"https://example.org/a","date":"2026-01-01"}]}""")!;
            k.RefreshGame(_game.Id);
            var s = k.Games.Single();
            Assert.Equal((GameStatus.Unsupported, note, false, notPlanned), (s.Status, s.StatusReason, s.CompileUnreached, s.NotPlanned));
            Assert.Equal(note, Format.ShortNote(s));
        }
        finally { Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.Embedded; }
    }

    /// <summary>A Stale game's first launch isn't judged: what it compiled may be what the warm left out.</summary>
    [Fact]
    public async Task A_stale_games_first_launch_is_not_judged()
    {
        var records = Enumerable.Range(0, 120).Select(i => new PsoDb.Rec('S', [(byte)i, 1])).ToList();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: records), new FakeWarmer(), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        await k.ScanAsync(default);
        await Compile(k);
        OlderPlanner(k);
        var at = k.Store.LoadGame(_game.Id).WarmedAt!.Value.ToUnixTimeMilliseconds();
        File.WriteAllLines(Path.Combine(_exeDir, "scskiller_creates.csv"), [$"#session,{at + 1},Fake-Win64-Shipping.exe",
            .. records.Select((r, i) => $"{i + 1}.0,S,0,0,50.0,{r.Key}"), $"#end,{at + 60_000}"]);
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Stale, false), (k.Games.Single().Status, k.Games.Single().CompileUnreached));
    }

    /// <summary>A verdict saved while a compile runs is about the warm before it: the compile's save keeps it, but it no
    /// longer applies once that compile's warm is the game's.</summary>
    [Fact]
    public void A_verdict_on_an_older_warm_never_applies_to_a_newer_one()
    {
        var store = new AppStore(Path.Combine(_root, "store"));
        var old = DateTimeOffset.Now.AddHours(-1);
        store.SaveGame("g", new GameRecord { WarmedAt = old });
        var compile = store.LoadGame("g");   // the compile loads the record before the launch is judged
        var judge = store.LoadGame("g");
        judge.UnreachedWarm = judge.WarmedAt;
        store.SaveGame("g", judge);
        Assert.True(ScsKiller.IsUnreached(store.LoadGame("g")));
        compile.WarmedAt = DateTimeOffset.Now;
        store.SaveGame("g", compile);
        var r = store.LoadGame("g");
        Assert.Equal(old, r.UnreachedWarm);   // merged: the field the compile didn't change
        Assert.False(ScsKiller.IsUnreached(r));
        Assert.True(ScsKiller.IsUnreached(new GameRecord { UnreachedWarm = old }));   // after Clear cache, which drops a verdict that no longer applies
    }

    /// <summary>The launch's own recording is imported before it is judged: pipelines it recorded whose stages the warm
    /// took (here each warmed vertex shader under another topology) are in the sample.</summary>
    [Fact]
    public async Task The_first_launch_is_judged_with_the_pipelines_it_recorded()
    {
        byte[] Db(uint topology)
        {
            var db = new MemoryStream();
            for (int i = 0; i < 120; i++) PsoDb.Write(db, 'S', PsoDb.Stream($"{8:x40}", new Dictionary<int, string> { [(int)Stage.Vertex] = $"{i + 1000:x40}" }, [], topology, [], 0));
            return db.ToArray();
        }
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        File.WriteAllBytes(inbox, Db(3));
        var k = Killer(new FakeReader(Unreal), new FakePlanner(), new FakeWarmer(), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        await k.ScanAsync(default);
        await Compile(k);
        var at = k.Store.LoadGame(_game.Id).WarmedAt!.Value.ToUnixTimeMilliseconds();
        var launch = Db(4);
        File.WriteAllBytes(inbox, launch);
        var keys = PsoDb.Read(new MemoryStream(launch)).Select(r => r.Key).ToList();
        Assert.DoesNotContain(keys[0], PsoDb.Read(new MemoryStream(Db(3))).Select(r => r.Key));
        File.WriteAllLines(Path.Combine(_exeDir, "scskiller_creates.csv"), [$"#session,{at + 1},Fake-Win64-Shipping.exe",
            .. keys.Select((key, i) => $"{i + 1}.0,S,0,0,50.0,{key}"), $"#end,{at + 60_000}"]);
        k.RefreshGame(_game.Id);
        Assert.True(k.Games.Single().CompileUnreached);
    }

    [Fact]
    public void A_pipeline_counts_as_warmed_when_the_warm_took_every_stage_of_it()
    {
        string I(string key, params string[] records) => key + string.Concat(records.Select(r => "|" + r));
        var (vs, ps, cs) = ($"{1:x40}", $"{2:x40}", $"{3:x40}");
        var inputs = new[] { I(vs, "a", "b"), I(ps, "a"), I(cs, "b"), $"{4:x40}" };   // a: vs+ps, b: vs+cs, a plan item by its own key
        Assert.Equal([$"{4:x40}", "a"], ScsKiller.WarmedRecords(inputs, new HashSet<string> { vs, ps, $"{4:x40}" }, new HashSet<string>()).Order());
        Assert.Equal([$"{4:x40}"], ScsKiller.WarmedRecords(inputs, new HashSet<string> { vs, ps, $"{4:x40}" }, new HashSet<string> { "a" }));
        Assert.Equal(["a"], ScsKiller.WarmedRecords([I(vs, "a"), I(ps, "a"), I(cs, "c", "rq")], new HashSet<string> { vs, ps, cs }, new HashSet<string>(),
            new HashSet<string> { "rq" }));   // c has the RayQuery PSO's stage
    }

    [Fact]
    public void Warmed_creates_are_the_launchs_first_of_each_warmed_pipeline_no_earlier_launch_created()
    {
        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllLines(csv, ["#session,1000,G.exe", "1.0,S,0,0,90.0,a", "#end,2000", "#session,5000,G.exe",
            "1.0,S,0,0,80.0,a", "2.0,S,0,0,30.0,b", "3.0,S,0,0,1.0,b", "4.0,s,1,1,0.5,c", "5.0,R,0,0,40.0,d", "6.0,S,0,0,70.0,q",
            "7.0,S,0,0,10.0,e", "8.0,S,0,0,50.0,x", "#end,9000"]);
        var warmed = new HashSet<string> { "a", "b", "c", "d", "q", "e" };
        Assert.Equal((2, 30.0), ScsKiller.WarmedCreates(SessionLog.Launches(csv), 5000, warmed, new HashSet<string> { "q" }));
        Assert.Equal((0, 0.0), ScsKiller.WarmedCreates(SessionLog.Launches(csv), 7000, warmed, null));
        Assert.True(ScsKiller.Unreached(((int)ScsKiller.MinJudgedCreates, ScsKiller.UnreachedMedianMs)));
        Assert.False(ScsKiller.Unreached(((int)ScsKiller.MinJudgedCreates - 1, 500)));
        Assert.False(ScsKiller.Unreached((5000, ScsKiller.UnreachedMedianMs - 0.1)));
    }

    /// <summary>The four AMD games whose keys were handle-verified (ARCHITECTURE.md): a warm registers an AGS app name only
    /// where that is proven to reach the game's own key, else it is plain (the exe name's key, profiles included).</summary>
    [Theory]
    //          exe                            app         game's own keys  missed  AGS used
    [InlineData("Townfall-Win64-Shipping.exe", "Townfall", "",              false,  true)]    // a measured app name
    [InlineData("Townfall-Win64-Shipping.exe", "Townfall", "",              true,   false)]   // a launch showed a miss
    [InlineData("Townfall-Win64-Shipping.exe", "Townfall", "dxc:dc72f790",  true,   true)]    // seen holding it
    [InlineData("Townfall-Win64-Shipping.exe", "Townfall", "dxc:12bba8ae",  false,  false)]   // seen holding the plain key
    [InlineData("Wonderlands.exe",             "OakGame",  "",              false,  false)]   // OakGame's profile key f2f80824 isn't the game's
    [InlineData("Wonderlands.exe",             "OakGame",  "dxc:85c2b2e5",  false,  false)]
    [InlineData("HogwartsLegacy.exe",          "Phoenix",  "",              false,  false)]   // plain: the stage path reaches d32786a7
    [InlineData("HogwartsLegacy.exe",          "Phoenix",  "dxc:d32786a7",  false,  true)]    // Phoenix's profile is the game's key
    [InlineData("ff7rebirth_.exe",             "End",      "",              false,  false)]
    [InlineData("ff7rebirth_.exe",             "End",      "dxc:6b2fcd83",  false,  false)]   // the exe name's profile: plain
    [InlineData("Unknown-Win64-Shipping.exe",  "Unknown",  "",              false,  false)]   // not proven
    public void Amd_a_warm_registers_the_AGS_app_name_only_where_proven(string exe, string app, string gameKey, bool missed, bool used)
    {
        var r = new GameRecord { GameKeys = gameKey.Length > 0 ? [gameKey] : [], AgsMissed = missed };
        var reg = new AgsRegistration(app, "UnrealEngine5.6");
        Assert.Equal(used ? reg : null, ScsKiller.AgsFor(reg, exe, r));
        Assert.Null(ScsKiller.AgsFor(null, exe, r));
    }

    [Theory]
    [InlineData("",                           false)]   // not seen yet: plain
    [InlineData("dxc:9954c9b0",               true)]    // FNV-1a "NWD", Banishers' key from the Xbox app
    [InlineData("dxc:09ba181c",               false)]   // the exe name's key
    [InlineData("dxc:9954c9b0,dxc:09ba181c",  false)]
    public void Amd_an_unread_exe_registers_its_app_name_only_once_the_game_holds_its_key(string gameKeys, bool used)
    {
        var r = new GameRecord { GameKeys = gameKeys.Length > 0 ? [.. gameKeys.Split(',')] : [] };
        var reg = new AgsRegistration("NWD", "UnrealEngine5.1", ExeUnread: true);
        Assert.Equal(used ? reg : null, ScsKiller.AgsFor(reg, "NWD-WinGDK-Shipping.exe", r));
        Assert.Null(ScsKiller.AgsFor(new AgsRegistration("Townfall", "UnrealEngine5.6", ExeUnread: true), "Townfall-Win64-Shipping.exe", new GameRecord()));   // a measured app name needs the exe read
    }

    [Fact]
    public void A_launch_that_still_compiled_most_pipelines_after_an_AGS_warm_is_a_miss()
    {
        var at = DateTimeOffset.Now;
        GameRecord R(string? app, long hits, long compiles, params string[] game) =>
            new() { WarmedAgsApp = app, FirstLaunch = new LaunchCheck(at, hits, compiles), GameKeys = [.. game] };
        Assert.True(ScsKiller.AgsLaunchMissed(R("OakGame", 1223, 16975)));
        Assert.False(ScsKiller.AgsLaunchMissed(R("Townfall", 9000, 125)));
        Assert.False(ScsKiller.AgsLaunchMissed(R(null, 1223, 16975)));                    // a plain warm: nothing to fall back from
        Assert.False(ScsKiller.AgsLaunchMissed(R("OakGame", 1223, 16975, "dxc:85c2b2e5")));   // the game's keys decide (WarmMissesGame)
        Assert.False(ScsKiller.AgsLaunchMissed(new GameRecord { WarmedAgsApp = "OakGame" }));
    }

    [Fact]
    public async Task Amd_the_AGS_key_is_a_hint_only_once_the_game_is_seen_holding_it()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, AmdAgs.DllName), []);   // links AGS, Unreal 4.26: registers its project name "Fake"
        var exe = Path.GetFileName(_game.ExePath);
        var agsKey = AmdAppCache.AppNameKey("Fake");
        var ags = AmdFile(agsKey[4..]);
        var nameHash = AmdFile(AmdAppCache.DxcKey(exe)[4..]);
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        await k.ScanAsync(default);
        Assert.Null(k.WarmAgs(_game.Id));   // not proven: a plain warm
        Assert.DoesNotContain("AGS", k.DriverCacheGap(_game.Id));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.True(File.Exists(ags));
        Assert.False(File.Exists(nameHash));

        var rec = k.Store.LoadGame(_game.Id);
        rec.GameKeys.Add(agsKey);   // the game's own process held it
        k.Store.SaveGame(_game.Id, rec);
        Assert.Equal("Fake", k.WarmAgs(_game.Id)?.App);
        Assert.Contains($"{agsKey} that Fake Game gets from its AGS app name Fake isn't learned yet", k.DriverCacheGap(_game.Id));
        rec.CacheKeys.Add(agsKey);
        k.Store.SaveGame(_game.Id, rec);
        Assert.Null(k.DriverCacheGap(_game.Id));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.False(File.Exists(ags));

        var nvidia = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        await nvidia.ScanAsync(default);
        Assert.Null(nvidia.WarmAgs(_game.Id));
    }

    [Fact]
    public async Task Amd_an_exe_that_cannot_be_read_registers_its_app_name_once_the_game_holds_its_key()
    {
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ReadData,
            System.Security.AccessControl.AccessControlType.Deny);   // as an Xbox app game's Shipping exe: no AGS DLL, exports unreadable
        var exe = new FileInfo(_game.ExePath);
        var acl = exe.GetAccessControl();
        acl.AddAccessRule(deny);
        exe.SetAccessControl(acl);
        try
        {
            var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
            k.AppCache = AmdCache();
            await k.ScanAsync(default);
            Assert.Null(k.WarmAgs(_game.Id));   // before a play: a plain warm, as for a readable exe without proof

            var rec = k.Store.LoadGame(_game.Id);
            rec.GameKeys.Add(AmdAppCache.DxcKey(exe.Name));   // the game held its plain key
            k.Store.SaveGame(_game.Id, rec);
            Assert.Null(k.WarmAgs(_game.Id));

            rec.GameKeys.Clear();
            rec.GameKeys.Add(AmdAppCache.AppNameKey("Fake"));   // the game held its app name's key
            k.Store.SaveGame(_game.Id, rec);
            Assert.Equal(new AgsRegistration("Fake", "UnrealEngine4.26", ExeUnread: true), k.WarmAgs(_game.Id));
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            exe.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task Clearing_deletes_exactly_the_attributed_files_and_nothing_while_one_is_in_use()
    {
        string[] ours = [CacheFile("0002a91d33333333.nvph", 65536), CacheFile("fc52a91d33333333.nvph", 4096)];
        var other = CacheFile("0002a91d44444444.nvph", 65536);
        var k = await Warmed();
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        Assert.False(k.ClearGameCache(_game.Id));   // nothing attributed
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("33333333");
        k.Store.SaveGame(_game.Id, rec);

        using (new FileStream(ours[1], FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))   // open, like a running game's
        {
            var e = Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(_game.Id));
            Assert.Contains($"files in use by {Process.GetCurrentProcess().ProcessName}.exe (pid {Environment.ProcessId})", e.Message);
        }
        Assert.All(ours, f => Assert.True(File.Exists(f)));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        Assert.True(k.ClearGameCache(_game.Id));
        Assert.All(ours, f => Assert.False(File.Exists(f)));
        Assert.True(File.Exists(other));
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, null, 0L), (s.Status, s.WarmedAt, s.CacheOnDisk));
        Assert.True(k.ClearGameCache(_game.Id));   // attributed, nothing left on disk: still cleared
    }

    // A case-sensitive fake process list: matching another case is the watcher's own doing.
    (ScsKiller K, HashSet<string> Running, Func<int> Changes) Watched(ScsKiller k)
    {
        var running = new HashSet<string>(StringComparer.Ordinal);
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.Ordinal); };
        int changes = 0;
        k.GameChanged += s => { if (s.Game.Id == _game.Id) Interlocked.Increment(ref changes); };
        return (k, running, () => Volatile.Read(ref changes));
    }

    [Fact]
    public async Task Watcher_shows_a_started_game_as_playing_and_rereads_it_once_when_it_exits()
    {
        var k = await Warmed();
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("33333333");
        k.Store.SaveGame(_game.Id, rec);
        CacheFile("0002a91d33333333.nvph", 65536);
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.Equal((GameStatus.Ready, 0L), (k.Games.Single().Status, k.Games.Single().CacheOnDisk));
        var (_, running, changes) = Watched(k);

        running.Add("Fake-Win64-Shipping.exe");
        k.PollGames();
        Assert.True(k.IsPlaying(_game.Id));
        Assert.True(k.Games.Single().Playing);
        Assert.Equal(1, changes());                                  // "Playing now"
        // the game rebuilds its own driver cache, and the recorder logs the session
        CacheFile("0002a91d33333333.nvph", 768 * 1024);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), "10.0,G,0,0,50.0\n12.0,C,1,1,0.5\n");
        k.PollGames();
        Assert.Equal(1, changes());                                  // still running: nothing re-read
        Assert.Equal(0L, k.Games.Single().CacheOnDisk);

        running.Clear();
        k.PollGames();
        Assert.True(k.IsPlaying(_game.Id));                          // one poll without it isn't an exit yet
        Assert.Equal(1, changes());
        k.PollGames();
        Assert.False(k.IsPlaying(_game.Id));
        Assert.Equal(2, changes());                                  // one refresh
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, 768L * 1024, false), (s.Status, s.CacheOnDisk, s.Playing));
        Assert.Equal(1, s.LastSession?.Compiles);
        k.PollGames();
        Assert.Equal(2, changes());
    }

    [Fact]
    public async Task Watcher_matches_the_exe_name_in_any_case()
    {
        var k = Killer();
        await k.ScanAsync(default);
        var (_, running, changes) = Watched(k);
        running.Add("FAKE-WIN64-SHIPPING.EXE");
        k.PollGames();
        Assert.True(k.IsPlaying(_game.Id));
        Assert.Equal(1, changes());
    }

    [Fact]
    public async Task Watcher_ignores_the_launcher_stub_and_follows_the_game_exe()
    {
        var k = Killer();
        await k.ScanAsync(default);
        var (_, running, changes) = Watched(k);
        running.Add("Fake.exe");                                     // the stub at the install's root starts first
        k.PollGames();
        Assert.False(k.IsPlaying(_game.Id));
        running.Add("Fake-Win64-Shipping.exe");
        k.PollGames();
        running.Remove("Fake.exe");                                  // the stub exits while the game plays
        k.PollGames();
        k.PollGames();
        Assert.True(k.IsPlaying(_game.Id));
        Assert.Equal(1, changes());
        running.Clear();
        k.PollGames();
        k.PollGames();
        Assert.False(k.IsPlaying(_game.Id));
        Assert.Equal(2, changes());
    }

    [Fact]
    public async Task Watcher_debounces_a_game_that_restarts_itself()
    {
        var k = Killer();
        await k.ScanAsync(default);
        var (_, running, changes) = Watched(k);
        running.Add("Fake-Win64-Shipping.exe");
        k.PollGames();
        for (int i = 0; i < 3; i++)                                  // gone for one poll at a time: the same session
        {
            running.Clear();
            k.PollGames();
            running.Add("Fake-Win64-Shipping.exe");
            k.PollGames();
        }
        Assert.True(k.IsPlaying(_game.Id));
        Assert.Equal(1, changes());
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls + 3; i++) k.PollGames();
        Assert.Equal(2, changes());
    }

    [Fact]
    public void The_idle_collection_waits_for_a_request_no_compile_and_a_hidden_window_or_an_away_user()
    {
        var away = ScsKiller.CollectAfterIdle;
        Assert.Equal(TimeSpan.FromSeconds(30), away);
        Assert.True(ScsKiller.CollectNow(pending: true, compiling: false, unseen: true, userIdle: TimeSpan.Zero));
        Assert.True(ScsKiller.CollectNow(pending: true, compiling: false, unseen: false, userIdle: away));
        Assert.False(ScsKiller.CollectNow(pending: true, compiling: false, unseen: false, userIdle: away - TimeSpan.FromSeconds(1)));
        Assert.False(ScsKiller.CollectNow(pending: true, compiling: true, unseen: true, userIdle: away));
        Assert.False(ScsKiller.CollectNow(pending: false, compiling: false, unseen: true, userIdle: away));
    }

    [Fact]
    public async Task After_a_scan_the_watcher_collects_once_the_user_is_away_with_the_window_open()
    {
        var k = Killer();
        var idle = TimeSpan.FromSeconds(5);   // the user's last input, a clock the test moves
        (k.Unseen, k.IdleTime) = (() => false, () => idle);
        await k.ScanAsync(default);
        Assert.True(k.CollectPending);
        int polls = 0;
        k.RunningGameExes = () => { Interlocked.Increment(ref polls); return new HashSet<string>(); };
        k.WatchInterval = TimeSpan.FromMilliseconds(10);
        using var cts = new CancellationTokenSource();
        var watching = k.WatchGames(cts.Token);
        await Until(() => Volatile.Read(ref polls) >= 5);
        Assert.True(k.CollectPending);   // someone is at the PC: no pause
        idle = ScsKiller.CollectAfterIdle;
        await Until(() => !k.CollectPending);
        cts.Cancel();
        await watching.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Hiding_the_window_collects_then_trims_the_working_set_once()
    {
        var k = Killer();
        await k.ScanAsync(default);
        var hidden = false;
        int trims = 0, polls = 0;
        k.Unseen = () => hidden;
        k.IdleTime = () => TimeSpan.Zero;   // someone at the PC
        k.TrimWorkingSet = () => Interlocked.Increment(ref trims);
        k.RunningGameExes = () => { Interlocked.Increment(ref polls); return new HashSet<string>(); };
        k.WatchInterval = TimeSpan.FromMilliseconds(10);
        using var cts = new CancellationTokenSource();
        var watching = k.WatchGames(cts.Token);
        await Until(() => Volatile.Read(ref polls) >= 5);
        Assert.Equal(0, Volatile.Read(ref trims));   // in view: neither
        Assert.True(k.CollectPending);
        hidden = true;
        await Until(() => Volatile.Read(ref trims) == 1);
        Assert.False(k.CollectPending);
        var seen = Volatile.Read(ref polls);
        await Until(() => Volatile.Read(ref polls) >= seen + 5);
        Assert.Equal(1, Volatile.Read(ref trims));   // still hidden: once
        cts.Cancel();
        await watching.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_memory_line_names_the_heap_by_generation_and_the_cached_keys()
    {
        var line = ProcessMemory.Line();
        Assert.Matches(@"^working set \d+ MB, private \d+ MB, managed heap \d+ MB \(after the last collection: gen0 \d+, gen1 \d+, gen2 \d+, "
            + @"large \d+, pinned \d+ MB\), GC committed \d+ MB of which \d+ MB free, cached keys [\d,]+, threads \d+, handles \d+, JIT [\d,]+ methods$", line);
    }

    [Fact]
    public async Task Watcher_stops_when_cancelled()
    {
        var k = Killer();
        await k.ScanAsync(default);
        int polls = 0;
        k.RunningGameExes = () => { Interlocked.Increment(ref polls); return new HashSet<string>(); };
        k.WatchInterval = TimeSpan.FromMilliseconds(10);
        using var cts = new CancellationTokenSource();
        var watching = k.WatchGames(cts.Token);
        await Until(() => Volatile.Read(ref polls) >= 3);
        cts.Cancel();
        await watching.WaitAsync(TimeSpan.FromSeconds(5));
        var n = Volatile.Read(ref polls);
        await Task.Delay(100);
        Assert.Equal(n, Volatile.Read(ref polls));
    }

    [Fact]
    public async Task The_real_process_list_finds_a_game_named_like_a_running_process()
    {
        // this test process plays the game
        var game = NamedAs(Process.GetCurrentProcess().ProcessName.ToUpperInvariant() + ".exe");
        var k = Killer(game: game);
        await k.ScanAsync(default);
        k.PollGames();
        Assert.True(k.IsPlaying(game.Id));
    }

    [Fact]
    public async Task The_real_process_list_leaves_out_a_staged_warm_even_for_an_anti_cheat_game()
    {
        // dummies: a copy of cmd named scskiller_warm.exe starts a copy of ping named like the game, as the warm stages it.
        // The process list is the PC's: a name of this test alone, or another run's game is taken for this one's
        var name = "Fake-" + Guid.NewGuid().ToString("N")[..8];
        var played = NamedAs(name + ".exe");
        var k = Killer(game: played);
        var warm = Path.Combine(_root, "native", "scskiller_warm.exe");
        var staged = Path.Combine(k.Store.GameDir(played.Id), "work", "stage-1-1", name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(warm)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), warm);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), staged);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));   // its process is only ever named, never opened
        await k.ScanAsync(default);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        static Process Start(string exe, string args) => Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!;
        using var parent = Start(warm, $"/c \"{staged}\" -n 30 127.0.0.1");
        Process? game = null;
        try
        {
            await Until(() => Process.GetProcessesByName(name).Length > 0);
            k.PollGames();
            Assert.False(k.IsPlaying(_game.Id));
            var own = Path.Combine(_root, "elsewhere", name + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            File.Copy(staged, own);
            game = Start(own, "-n 30 127.0.0.1");                    // the same exe, not from a stage folder: the game
            k.PollGames();
            Assert.True(k.IsPlaying(_game.Id));
        }
        finally
        {
            EndAndWait(parent, name);
            game?.Dispose();
        }
    }

    [Fact]
    public async Task A_staged_warm_named_like_an_exe_of_the_games_folder_is_not_the_game_playing()
    {
        var name = "Fake-" + Guid.NewGuid().ToString("N")[..8];   // the PC's process list: a name of this test alone
        var exe = Path.Combine(_exeDir, name + ".exe");
        File.WriteAllBytes(exe, new byte[100]);   // an exe of the game's folder, as the staged warm copies it
        var played = _game with { ExePath = exe };
        var k = Killer(game: played);
        var warm = Path.Combine(_root, "native", "scskiller_warm.exe");
        var staged = Path.Combine(k.Store.GameDir(played.Id), "work", "stage-1-1", name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(warm)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), warm);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), staged);
        await k.ScanAsync(default);
        static Process Start(string file, string args) => Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })!;
        using var parent = Start(warm, $"/c \"{staged}\" -n 30 127.0.0.1");
        try
        {
            await Until(() => Process.GetProcessesByName(name).Length > 0);
            for (var i = 0; i < ScsKiller.ExitPolls + 1; i++)
            {
                await Task.Delay(1100);   // past the watcher's snapshot second
                k.PollGames();
                Assert.False(k.IsPlaying(_game.Id));
            }
            Assert.Null(k.Store.LoadGame(_game.Id).LastPlay);   // a compile doesn't make a play
        }
        finally { EndAndWait(parent, name); }
    }

    /// <summary>A copy running from a game's work\stage-* folder with no scskiller_warm.exe above it (one started by the staged
    /// copy, left after the warm exited, or started by a packaged app's activator) is SCSKiller's, not the game playing:
    /// a compile isn't stopped for it. The game's own exe, running beside it, is the game.</summary>
    [Fact]
    public async Task A_copy_running_from_a_stage_folder_is_ours_whatever_its_parent()
    {
        var name = "Fake-" + Guid.NewGuid().ToString("N")[..8];   // the PC's process list: a name of this test alone
        var played = NamedAs(name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), played.ExePath);
        var k = Killer(game: played);
        await k.ScanAsync(default);
        var staged = Path.Combine(k.Store.GameDir(played.Id), "work", "stage-1-1", "Game", "Binaries", name + ".exe");   // --stage-path
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(played.ExePath, staged);
        static Process Start(string file, string args) => Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })!;
        using var copy = Start(staged, "-n 30 127.0.0.1");   // this test's child: no warm above it
        try
        {
            await Until(() => Process.GetProcessesByName(name).Length > 0);
            k.PollGames();
            Assert.False(k.IsPlaying(played.Id));
            Assert.Empty(k.RunningGameExes());
            using var game = Start(played.ExePath, "-n 30 127.0.0.1");
            k.PollGames();
            Assert.True(k.IsPlaying(played.Id));
            Assert.Equal([name + ".exe"], k.RunningGameExes());
        }
        finally { EndAndWait(copy, name); }
    }

    /// <summary>A game-named process whose image path can't be read (it exited meanwhile, or no drive letter maps its volume)
    /// is SCSKiller's only when scskiller_warm.exe is its parent.</summary>
    [Fact]
    public async Task A_process_without_a_readable_path_is_ours_only_under_a_warm()
    {
        var k = RecordKiller([FakeGame("test:nopath", "Game")]);
        await k.ScanAsync(default);
        k.ProcessPath = _ => null;
        k.Processes = _ => [(10, 1, "scskiller_warm.exe"), (11, 10, "Game.exe")];
        Assert.Empty(k.RunningGameExes());
        k.Processes = _ => [(10, 1, "scskiller_warm.exe"), (11, 10, "Game.exe"), (12, 4, "Game.exe")];
        Assert.Equal(["Game.exe"], k.RunningGameExes());
    }

    /// <summary>Ends <paramref name="parent"/>'s tree and every process named <paramref name="name"/>, and waits for them:
    /// Kill returns before a process is gone, and its exe, a copy under _root, can't be deleted by Dispose until then.</summary>
    static void EndAndWait(Process parent, string name)
    {
        var named = Process.GetProcessesByName(name);
        parent.Kill(entireProcessTree: true);
        foreach (var p in named.Append(parent))
        {
            p.Kill();
            p.WaitForExit();
        }
        foreach (var p in named) p.Dispose();
    }

    [Fact]
    public async Task A_keys_rewrite_that_fails_is_tried_again_at_the_next_refresh()
    {
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var shader = "DXBC a shader built at run time"u8.ToArray();
        using (var f = File.Create(inbox)) PsoDb.Write(f, 'B', [.. SHA1.HashData(shader), .. shader]);   // recorded: the keys name it next
        File.WriteAllBytes(keys, [.. "SCSKKEY1"u8]);
        File.SetAttributes(keys, FileAttributes.ReadOnly);   // can't be replaced
        try
        {
            k.RefreshGame(_game.Id);
            Assert.True(k.Store.LoadGame(_game.Id).KeysPending);
        }
        finally { File.SetAttributes(keys, FileAttributes.Normal); }
        k.RefreshGame(_game.Id);   // the next refresh writes it
        Assert.False(k.Store.LoadGame(_game.Id).KeysPending);
        Assert.Contains(SHA1.HashData(shader), File.ReadAllBytes(keys)[8..].Chunk(20), new BytesEqual());
    }

    [Fact]
    public async Task Window_activation_rereads_only_cache_sizes_and_the_detail_refresh_one_game()
    {
        var k = await Warmed();
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("55555555");
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        var (_, _, changes) = Watched(k);
        k.RefreshCacheSizes();
        Assert.Equal(0, changes());                                  // unchanged: nothing raised
        var file = CacheFile("0002a91d55555555.nvph", 4096);
        k.RefreshCacheSizes();
        Assert.Equal((1, 4096L), (changes(), k.Games.Single().CacheOnDisk));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        using (var f = new FileStream(file, FileMode.Open, FileAccess.Write)) f.Write([.. "nvph"u8, 0, 0, 0, 0, .. BitConverter.GetBytes(1024L)]);
        k.RefreshCacheSizes();   // the driver filled more of a file of the same size: its contents are never read
        Assert.Equal(1, changes());

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // a driver handle that shares nothing
        {
            k.RefreshGame(_game.Id);
            Assert.Equal((2, 4096L), (changes(), k.Games.Single().CacheOnDisk));
        }
        Assert.Throws<ArgumentException>(() => k.RefreshGame("test:unknown"));
    }

    [Fact]
    public async Task An_xbox_id_naming_other_folders_finds_no_cache_outside_its_package()
    {
        var xbox = _game with { Id = @"xbox:..\Victim", Store = Store.Xbox };   // Packages\..\Victim: _root\Victim
        var victim = Path.Combine(_root, "Victim", "LocalCache", "Local", "App", "Saved", "Fake_PCD3D_SM6.upipelinecache");
        Directory.CreateDirectory(Path.GetDirectoryName(victim)!);
        File.WriteAllBytes(victim, new byte[100]);
        var k = Killer(new FakeReader(Unreal), game: xbox);
        await k.ScanAsync(default);

        Assert.DoesNotContain(k.GameCaches(xbox.Id, gamePrecache: true), p => p.Files.Contains(victim));
        Assert.Equal(Path.Combine(_root, "data", "games"), Path.GetDirectoryName(Path.GetFullPath(k.Store.GameDir(xbox.Id))));
    }

    /// <summary>Each warm process leaves a D3DSCache folder keyed to its staged exe's path under the game's work folder: the
    /// compile deletes the ones it made, not one from before it, not the game's own.</summary>
    [Fact]
    public async Task A_compile_deletes_the_D3DSCache_folders_its_staged_exes_made()
    {
        var d3ds = Path.Combine(_root, "D3DSCache");   // Killer: LocalAppData = _root
        var work = "";
        string[] made = [];
        var k = Killer(new FakeReader(Unreal), warmer: new FakeWarmer(() =>
        {
            made = [FakeD3DSCache.Folder(d3ds, "1111", Path.Combine(work, "stage-10-1", "Fake", "Fake-Win64-Shipping.exe")),
                    FakeD3DSCache.Folder(d3ds, "2222", Path.Combine(work, "stage-10-2", "Fake-Win64-Shipping.exe"))];   // a retry's process
            return null;
        }));
        work = Path.Combine(k.Store.GameDir(_game.Id), "work");
        var earlier = FakeD3DSCache.Folder(d3ds, "3333", Path.Combine(work, "stage-9-1", "Fake-Win64-Shipping.exe"));
        Directory.SetCreationTimeUtc(earlier, DateTime.UtcNow.AddMinutes(-5));
        var games = FakeD3DSCache.Folder(d3ds, "4444", _game.ExePath);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Equal(2, made.Length);
        Assert.All(made, d => Assert.False(Directory.Exists(d), d));
        Assert.True(Directory.Exists(earlier));
        Assert.True(Directory.Exists(games));
    }

    /// <summary>A D3DSCache folder that can't be listed leaves the cleanup, never the compile's refresh after it, also when
    /// logging that throws (the command line's stderr closed).</summary>
    [Fact]
    public async Task An_unreadable_D3DSCache_still_ends_the_compile_with_its_refresh()
    {
        var d3ds = Directory.CreateDirectory(Path.Combine(_root, "D3DSCache"));   // Killer: LocalAppData = _root
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var acl = d3ds.GetAccessControl();
        acl.AddAccessRule(deny);
        d3ds.SetAccessControl(acl);
        try
        {
            var k = Killer(new FakeReader(Unreal));
            var noted = false;
            k.Log = new At("D3DSCache not cleaned", () => { noted = true; throw new IOException("the log can't be written"); });
            await k.ScanAsync(default);
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);   // the refresh after the compile ran
            Assert.True(noted);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            d3ds.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task Clearing_deletes_the_games_D3DSCache_files_and_its_own_caches_only_when_asked()
    {
        var d3ds = Path.Combine(_root, "D3DSCache");   // Killer: LocalAppData = _root, MyGames = _root\My Games
        var ours = FakeD3DSCache.Folder(d3ds, "1111", _game.ExePath);
        var legacy = Path.Combine(ours, "F4EB2D6C-ED2B-4BDD-AD9D-F913287E6768_VEN_10DE&DEV_2B85&SUBSYS_89EE&REV_A1");   // the older per-adapter files
        foreach (var ext in new[] { ".idx", ".val", ".lock" }) File.WriteAllBytes(legacy + ext, new byte[10]);
        var notes = Path.Combine(ours, "notes.txt");   // not a shader cache file: left, and with it the folder
        File.WriteAllText(notes, "mine");
        var other = FakeD3DSCache.Folder(d3ds, "2222", @"C:\Games\Other\Fake-Win64-Shipping.exe");
        var broken = FakeD3DSCache.Folder(d3ds, "3333", _game.ExePath, broken: true);   // unparsable: nothing deleted in it
        string F(string dir, string name, int size)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name), new byte[size]);
            return Path.Combine(dir, name);
        }
        var written = F(Path.Combine(_root, "My Games", "Fake Game", "Saved"), "Fake_PCD3D_SM6.upipelinecache", 1000);
        string[] precache =   // %LOCALAPPDATA%\<game>\Saved (Tiny Tina's Wonderlands' own cache) and C:\ProgramData\<game>
        [
            F(Path.Combine(_root, "Fake Game", "Saved"), "D3DGraphics_V4098_D5510_GBX_1.ushaderprecache", 700),
            F(Path.Combine(_root, "ProgramData", "Fake Game"), "D3DCompute_V4098_D5510_GBX_1.ushaderprecache", 300),
        ];
        string[] shipped =   // the install (_root\FakeGame, itself one level under LocalAppData here) is never touched
        [
            F(Path.Combine(_game.InstallDir, "Fake", "Content", "PipelineCaches", "Windows"), "Fake_PCD3D_SM6.stable.upipelinecache", 500),
            F(Path.Combine(_game.InstallDir, "Saved"), "Fake_PCD3D_SM6.upipelinecache", 500),
            F(Path.Combine(_game.InstallDir, "Saved"), "D3DGraphics_V4098_D5510_GBX_1.ushaderprecache", 500),
        ];
        var otherFiles = Directory.GetFiles(other).Concat(Directory.GetFiles(broken))
            .Append(F(Path.Combine(_root, "Other Game", "Saved"), "D3DGraphics_V4098_D5510_GBX_1.ushaderprecache", 100)).ToList();
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);

        var parts = k.GameCaches(_game.Id);
        Assert.Equal([ScsKiller.WindowsPart], parts.Select(p => p.Name));   // the game's own caches only when asked
        Assert.Equal(Directory.GetFiles(ours).Where(f => f != notes).Sum(f => new FileInfo(f).Length), parts[0].Bytes);
        Assert.Equal(6, parts[0].Files.Count);
        var all = k.GameCaches(_game.Id, gamePrecache: true);
        Assert.Equal([ScsKiller.WindowsPart, ScsKiller.PipelinePart, ScsKiller.PrecachePart], all.Select(p => p.Name));
        Assert.Equal([written], all[1].Files);
        Assert.Equal(precache.Order(), all[2].Files.Order());
        Assert.Equal(1000, all[2].Bytes);

        Assert.True(k.ClearGameCache(_game.Id));
        Assert.Equal([notes], Directory.GetFiles(ours));
        Assert.All(precache.Append(written), f => Assert.True(File.Exists(f), f));   // kept by default
        Assert.Empty(k.GameCaches(_game.Id));
        Assert.False(k.ClearGameCache(_game.Id));   // nothing left, no driver keys

        Assert.True(k.ClearGameCache(_game.Id, gamePrecache: true));
        Assert.All(precache.Append(written), f => Assert.False(File.Exists(f), f));
        Assert.All(otherFiles.Concat(shipped), f => Assert.True(File.Exists(f), f));
        Assert.Empty(k.GameCaches(_game.Id, gamePrecache: true));
    }

    [Fact]
    public async Task A_folder_named_like_another_game_is_neither_games_precache()
    {
        var precache = Path.Combine(_root, "Fake Game", "Saved", "D3DGraphics_GBX_1.ushaderprecache");
        Directory.CreateDirectory(Path.GetDirectoryName(precache)!);
        File.WriteAllBytes(precache, new byte[10]);
        var twin = _game with { Id = "test:twin", InstallDir = Path.Combine(_root, "Twin"), ExePath = Path.Combine(_root, "Twin", "Twin.exe") };
        Directory.CreateDirectory(twin.InstallDir);
        File.WriteAllBytes(twin.ExePath, new byte[100]);
        var k = Killer(new FakeReader(Unreal), games: [_game, twin]);
        await k.ScanAsync(default);
        Assert.Empty(k.GameCaches(_game.Id, gamePrecache: true));   // both are named "Fake Game"
        Assert.True(File.Exists(precache));
    }

    [Fact]
    public async Task Clearing_is_refused_while_the_game_runs_and_near_anti_cheat_leaves_all_but_the_driver_cache()
    {
        // the game is named like this test process: running, from the process list alone
        var running = NamedAs(Process.GetCurrentProcess().ProcessName + ".exe");
        var folder = FakeD3DSCache.Folder(Path.Combine(_root, "D3DSCache"), "1111", running.ExePath);
        var k = Killer(new FakeReader(Unreal), game: running);
        await k.ScanAsync(default);
        Assert.Single(k.GameCaches(running.Id));
        Assert.Equal("Fake Game is running", Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(running.Id)).Message);
        Assert.Equal(3, Directory.GetFiles(folder).Length);

        FakeD3DSCache.Folder(Path.Combine(_root, "D3DSCache"), "2222", _game.ExePath);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        var nvph = CacheFile("0002a91d55555555.nvph", 4096);
        k = Killer(new FakeReader(Unreal));
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("55555555");
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.Equal([ScsKiller.DriverPart], k.GameCaches(_game.Id).Select(p => p.Name));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.False(File.Exists(nvph));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_root, "D3DSCache", "2222")).Length);
    }

    [Fact]
    public async Task A_warm_waits_while_its_game_runs_and_stops_when_it_starts_then_continues_from_there()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fake-win64-shipping.exe" };
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); };
        void Game(bool on) { lock (running) if (on) running.Add("Fake-Win64-Shipping.exe"); else running.Clear(); }
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await Until(() => k.Queue.Single().Stage == QueueStage.Paused);   // the game was already running: no warm yet
        Assert.Equal("stopped while Fake Game is running: continues when it exits", k.Queue.Single().Note);
        await Task.Delay(700);
        Assert.Empty(warmer.Started);

        Game(false);
        await warmer.WhenStarted(1);
        var first = warmer.Run!;
        k.PauseQueue();                                                   // paused or not, the game's start stops it
        await Until(() => first.Paused);
        Game(true);
        await first.WhenStopped;
        Assert.True(first.Stopped);
        await Until(() => k.Queue.Single().Note?.StartsWith("stopped while Fake Game") == true);
        await Until(() => k.Store.LoadGame(_game.Id).ResumeAt == 10);     // saved after the note: once the run has returned
        await Task.Delay(700);
        Assert.Single(warmer.Started);                                    // waits for the game to exit

        Game(false);
        await Task.Delay(1200);
        Assert.Single(warmer.Started);                                    // the queue is still paused
        k.ResumeQueue();
        await warmer.WhenStarted(2);
        Assert.Equal(10, warmer.Options!.StartAt);                        // from where it stopped
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Another_game_only_pauses_a_background_warm_and_its_own_game_stops_it()
    {
        var warmer = new ControlledWarmer();
        var otherDir = Directory.CreateDirectory(Path.Combine(_root, "Other Game")).FullName;
        var other = new Game("test:other", "Other Game", Store.Other, otherDir, Path.Combine(otherDir, "Other.exe"));
        var k = Killer(new FakeReader(Unreal), warmer: warmer, games: [_game, other]);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); };
        k.Background = true;
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await warmer.WhenStarted();
        var run = warmer.Run!;

        lock (running) running.Add("Other.exe");
        await Until(() => run.Paused);
        await Until(() => k.Queue.Single().Note == "paused while Other Game is running");
        k.Settings = k.Settings with { PauseWhileGaming = false };        // the setting only governs other games
        await Until(() => !run.Paused);
        lock (running) running.Add("Fake-Win64-Shipping.exe");
        await run.WhenStopped;
        Assert.True(run.Stopped);
        lock (running) running.Clear();
        await warmer.WhenStarted(2);
        Assert.Equal(10, warmer.Options!.StartAt);
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
    }

    const string Ff7Profile = "dxc:6b2fcd83";
    string AmdFile(string app, string kind = "71efbc0e", int size = 65536)
    {
        var path = Path.Combine(_root, "AMD", "DxcCache", $"{app}.dfac411e.{kind}.2b1a674a.0.parc");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }
    AmdAppCache AmdCache() => new(Path.Combine(_root, "AMD", "DxcCache"), Path.Combine(_root, "AMD", "DxCache"));
    static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);   // as the driver does

    /// <summary>An AMD killer whose game is named like this test process, which plays the staged warm holding
    /// <paramref name="held"/> (a cache file) open.</summary>
    (ScsKiller K, Game Game, FakeWarmer Warmer) AmdKiller(string held, Game[]? others = null)
    {
        var game = NamedAs(Process.GetCurrentProcess().ProcessName + ".exe");
        var warmer = new FakeWarmer(() => Hold(held));
        var k = Killer(new FakeReader(Unreal), warmer: warmer, games: [game, .. others ?? []], vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        k.RunningGameExes = () => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (k, game, warmer);
    }

    [Fact]
    public async Task Amd_a_profiled_key_is_learned_from_the_open_files_not_the_name_hash_and_case_does_not_matter()
    {
        var (k, game, _) = AmdKiller(AmdFile("6b2fcd83"));   // the driver's app profile: a fixed key, not FNV-1a(name)
        var exe = Path.GetFileName(game.ExePath);
        AmdFile(AmdAppCache.DxcKey(exe)[4..], size: 4096);   // name-hash files (another tool, an older driver): not the game's
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.Equal([Ff7Profile], k.Store.LoadGame(game.Id).CacheKeys);
        Assert.Equal(65536, k.Games.Single().CacheOnDisk);

        Marker(exe.ToUpperInvariant(), DateTimeOffset.Now);   // launched in another case: the profile's key is the same
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(exe.ToUpperInvariant(), k.Store.LoadGame(game.Id).LaunchedExeName);
        Assert.Equal(GameStatus.Warmed, s.Status);
    }

    [Fact]
    public async Task Amd_a_name_hashed_key_keeps_the_exe_case_rule()
    {
        var exe = Process.GetCurrentProcess().ProcessName + ".exe";
        var (k, game, _) = AmdKiller(AmdFile(AmdAppCache.DxcKey(exe)[4..]));
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.Equal([AmdAppCache.DxcKey(exe)], k.Store.LoadGame(game.Id).CacheKeys);

        Marker(exe.ToUpperInvariant(), DateTimeOffset.Now);
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Stale, s.Status);
        Assert.Contains($"runs as {exe.ToUpperInvariant()}", s.StatusReason);
    }

    [Fact]
    public async Task Amd_keys_are_learned_while_the_game_itself_runs()
    {
        var (k, game, _) = AmdKiller(AmdFile("6b2fcd83"));
        var exe = Path.GetFileName(game.ExePath);
        await k.ScanAsync(default);
        Assert.Null(k.Games.Single().CacheOnDisk);
        k.RunningGameExes = () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exe };   // this process plays the game
        using (Hold(AmdFile("6b2fcd83")))
            await k.ScanAsync(default);
        Assert.Equal([Ff7Profile], k.Store.LoadGame(game.Id).CacheKeys);
        Assert.Equal(65536, k.Games.Single().CacheOnDisk);
    }

    [Fact]
    public async Task Amd_a_warmed_game_whose_cache_file_the_driver_trimmed_is_stale()
    {
        var pipelines = AmdFile("6b2fcd83");
        var stages = AmdFile("6b2fcd83", kind: "a4a986a3", size: 16384);
        AmdFile("12345678", size: 4096);   // another app's file
        var (k, game, _) = AmdKiller(pipelines);
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.Equal([Path.GetFileName(pipelines), Path.GetFileName(stages)], k.Store.LoadGame(game.Id).WarmedFiles!.Order());
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);

        File.Delete(Path.Combine(_root, "AMD", "DxcCache", "12345678.dfac411e.71efbc0e.2b1a674a.0.parc"));   // not this game's
        File.WriteAllBytes(pipelines.Replace(".0.parc", ".1.parc"), new byte[4096]);   // new files don't matter
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);

        File.Delete(stages);   // the driver trims one file of the key
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Stale, ScsKiller.TrimmedPartReason), (s.Status, s.StatusReason));

        File.Delete(pipelines);
        s = (await k.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Stale, ScsKiller.TrimmedAllReason), (s.Status, s.StatusReason));

        AmdFile("6b2fcd83");   // the warm fills the key again
        await WarmOnce(k, game.Id);
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);
    }

    [Fact]
    public void Amd_queue_warning_names_the_games_and_the_overshoot_only_past_the_free_room()
    {
        const long G = 1L << 30;
        Assert.Null(AmdAppCache.QueueWarning(10 * G, [("A", 4 * G), ("B", 2 * G)]));   // exactly the 6 GB free
        Assert.Null(AmdAppCache.QueueWarning(10 * G, []));
        Assert.Equal("This queue adds about 7 GB to the shader cache (A 4 GB, B 3 GB), 1 GB more than the 6 GB free under the AMD driver's fixed 16 GB limit. "
            + "The driver then trims the least recently used caches, older games' included, and those games stutter until compiled again.",
            AmdAppCache.QueueWarning(10 * G, [("A", 4 * G), ("B", 3 * G), ("Warmed", 0)]));
        Assert.Contains("more than the 0 MB free", AmdAppCache.QueueWarning(17 * G, [("A", 512L << 20)]));   // already past the cap
        Assert.Equal(16 * G, AmdAppCache.DxcCacheCap);
    }

    /// <summary>The queue's warning under a driver cache limit (NVIDIA): a large compile that takes the cache past the limit says
    /// so, one within it gets the large-compile text alone, and a queue that only brings the cache close to the limit gets that.</summary>
    [Fact]
    public void The_queue_warns_about_a_large_compile_and_the_cache_limit()
    {
        const long G = 1L << 30;
        (long, long)[] huge = [(2 * G, 2 * G), (30 * G, 32 * G)];
        const string Cost = "Very large caches can make the game start slower and use more memory.";
        Assert.Equal($"This compile adds about 32 GB and takes the cache past your 16 GB limit, so older games may be evicted. {Cost}",
            ScsKiller.QueueCacheWarning(huge, 5 * G, 34 * G, 16 * G, GpuVendor.Nvidia));   // 1: over the limit
        Assert.Equal($"This compile adds about 32 GB. {Cost}", ScsKiller.QueueCacheWarning(huge, 5 * G, 34 * G, 100 * G, GpuVendor.Nvidia));   // 2: within it
        Assert.Equal($"This compile adds about 32 GB. {Cost}", ScsKiller.QueueCacheWarning(huge, 5 * G, 34 * G, null, GpuVendor.Nvidia));   // no limit
        (long, long)[] small = [(4 * G, 4 * G), (6 * G, 6 * G)];
        Assert.Equal("After this queue the cache is close to its 16 GB limit, older games may be evicted.",
            ScsKiller.QueueCacheWarning(small, 5 * G, 10 * G, 16 * G, GpuVendor.Nvidia));   // 3: 15 of 16 GB
        Assert.Null(ScsKiller.QueueCacheWarning(small, 2 * G, 10 * G, 16 * G, GpuVendor.Nvidia));   // 12 of 16 GB
        Assert.Null(ScsKiller.QueueCacheWarning([], 15 * G, 0, 16 * G, GpuVendor.Nvidia));   // nothing queued
    }

    /// <summary>One game adding more than 16 GB: its own cache estimate in whole GB (as the Library shows it), and on AMD
    /// that the driver's cache can't hold it.</summary>
    [Fact]
    public void A_compile_over_16_gb_is_warned_about_by_vendor()
    {
        const long G = 1L << 30;
        (long, long)[] queue = [(2 * G, 2 * G), (60 * G, 64 * G + G / 2), (20 * G, 20 * G)];   // the largest growth; its estimate rounds up
        const string Text = "This compile adds about 65 GB. Very large caches can make the game start slower and use more memory.";
        Assert.Equal(Text + " It's more than the AMD driver's 16 GB cache can hold.", ScsKiller.LargeCompileWarning(queue, GpuVendor.Amd));
        Assert.Equal(Text, ScsKiller.LargeCompileWarning(queue, GpuVendor.Nvidia));
        Assert.Equal("32.2 GB", Format.Bytes(34_600_000_000));   // the Library's estimate, in the same units
        Assert.Equal("This compile adds about 32 GB. Very large caches can make the game start slower and use more memory.",
            ScsKiller.LargeCompileWarning([(17 * G, 34_600_000_000)], GpuVendor.Nvidia));
        Assert.Contains("about 20 GB.", ScsKiller.LargeCompileWarning([(17 * G, 40 * G), (20 * G, 20 * G)], GpuVendor.Nvidia));   // the game that adds the most, not the largest estimate
        Assert.Null(ScsKiller.LargeCompileWarning([(2 * G, 2 * G), (16 * G, 30 * G)], GpuVendor.Amd));   // growth at the cap: estimate alone doesn't trigger
        Assert.Null(ScsKiller.LargeCompileWarning([], GpuVendor.Nvidia));
    }

    [Fact]
    public void Cache_growth_is_the_estimate_less_what_the_games_keys_hold()
    {
        var s = new GameState(_game, null, AntiCheat.None, GameStatus.Ready, "", null, null, 3000, null, null, null, null, false, null, CacheOnDisk: 1000);
        Assert.Equal(2000, ScsKiller.CacheGrowth(s));
        Assert.Equal(0, ScsKiller.CacheGrowth(s with { CacheOnDisk = 5000 }));
        Assert.Equal(0, ScsKiller.CacheGrowth(s with { EstimatedCacheBytes = null }));
    }

    [Fact]
    public void The_librarys_pipeline_count_leaves_out_what_crashes_the_driver()
    {
        var s = new GameState(_game, null, AntiCheat.None, GameStatus.Warmed, "", null, new PlanStats(900, 9000, 9000, 5, true, MiddlewareItems: 100), null, null, null, null, null, false, null);
        Assert.Equal(10_000, ScsKiller.PlanPipelines(s));
        Assert.Equal(9_990, ScsKiller.PlanPipelines(s with { LastWarmCrashed = 10 }));
        Assert.Null(ScsKiller.PlanPipelines(s with { Plan = null }));
    }

    [Fact]
    public void Amd_cache_listing_never_reads_cache_file_contents()
    {
        var files = new[] { AmdFile("6b2fcd83"), AmdFile("6b2fcd83", kind: "a4a986a3"), AmdFile("12345678") };
        var old = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var probe = AmdFile("0badf00d", size: 4096);
        File.SetLastAccessTimeUtc(probe, old);
        File.ReadAllBytes(probe);
        if (File.GetLastAccessTimeUtc(probe) == old) return;   // this volume doesn't update access times: nothing to observe
        File.Delete(probe);
        foreach (var f in files) File.SetLastAccessTimeUtc(f, old);

        var cache = AmdCache();
        string[] keys = [Ff7Profile, "dxc:12345678"];
        Assert.Equal(3 * 65536, cache.SizeOf(keys));
        Assert.Equal(3, cache.FilesOf(keys).Count);
        Assert.Equal(2, cache.D3D12FileNames([Ff7Profile]).Count);
        Assert.Empty(cache.Missing(files.Select(f => Path.GetFileName(f))));
        Assert.Equal(3 * 65536, cache.DxcBytes());
        Assert.Empty(cache.KeysOpenBy(Process.GetCurrentProcess().ProcessName + ".exe"));
        foreach (var f in files) Assert.Equal(old, File.GetLastAccessTimeUtc(f));   // the driver evicts by access time: listing must not refresh it
    }

    [Fact]
    public async Task Amd_clearing_refuses_a_key_shared_with_another_game_and_clears_once_it_is_not()
    {
        // another discovered game named "ff7rebirth*": the driver profile gives it the same key, warmed or not
        var demoDir = Path.Combine(_root, "Demo");
        Directory.CreateDirectory(demoDir);
        var demo = new Game("test:demo", "FF7 Demo", Store.Other, demoDir, Path.Combine(demoDir, "ff7rebirth_demo.exe"));
        var other = new Game("test:other", "Other Game", Store.Other, demoDir, Path.Combine(demoDir, "Other.exe"));
        var profile = AmdFile("6b2fcd83");
        var k = Killer(new FakeReader(Unreal), games: [_game, demo], vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add(Ff7Profile);   // what its warm held (see the attribution test above)
        k.Store.SaveGame(_game.Id, rec);
        var e = Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(_game.Id));
        Assert.Contains($"shared with FF7 Demo ({Ff7Profile})", e.Message);
        Assert.True(File.Exists(profile));

        // a game with a name the profile table doesn't know, but whose own warms held the same files: shared too
        k = Killer(new FakeReader(Unreal), games: [_game, other], vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        var otherRec = k.Store.LoadGame(other.Id);
        otherRec.CacheKeys.Add(Ff7Profile);
        k.Store.SaveGame(other.Id, otherRec);
        await k.ScanAsync(default);
        Assert.Contains("shared with Other Game", Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(_game.Id)).Message);

        otherRec.CacheKeys.Clear();
        k.Store.SaveGame(other.Id, otherRec);
        using (Hold(profile))   // any running process using the key holds its files: refused, nothing deleted
            Assert.Contains("files in use by", Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(_game.Id)).Message);
        Assert.True(File.Exists(profile));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.False(File.Exists(profile));
    }

    [Fact]
    public async Task Nvidia_clearing_refuses_while_another_game_has_the_same_exe_name()
    {
        var copyDir = Directory.CreateDirectory(Path.Combine(_root, "Copy")).FullName;   // a second install, never warmed or seen
        var copyExe = Path.Combine(copyDir, Path.GetFileName(_game.ExePath).ToUpperInvariant());   // NVIDIA's key ignores the case
        File.WriteAllBytes(copyExe, new byte[4096]);
        var copy = new Game("test:copy", "Fake Copy", Store.Other, copyDir, copyExe);
        var ours = CacheFile("0002a91d33333333.nvph", 65536);
        var k = Killer(new FakeReader(Unreal), games: [_game, copy], vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        await k.ScanAsync(default);
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("33333333");
        k.Store.SaveGame(_game.Id, rec);

        Assert.Contains("shared with Fake Copy (33333333)", Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(_game.Id)).Message);
        Assert.True(File.Exists(ours));
    }

    [Fact]
    public async Task Clearing_a_game_with_no_learned_key_takes_its_name_hash_on_AMD_unless_a_profile_is_learned()
    {
        var exe = Path.GetFileName(_game.ExePath);
        var nameHash = AmdFile(AmdAppCache.DxcKey(exe)[4..]);
        var other = AmdFile("12345678");
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        await k.ScanAsync(default);
        Assert.Equal([nameHash], k.GameCaches(_game.Id).Single(p => p.Name == ScsKiller.DriverPart).Files);
        Assert.Contains("isn't learned yet", k.DriverCacheGap(_game.Id));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.False(File.Exists(nameHash));
        Assert.True(File.Exists(other));

        AmdFile(AmdAppCache.DxcKey(exe)[4..]);   // name-hash files beside a learned profile key: not the game's
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add(Ff7Profile);
        k.Store.SaveGame(_game.Id, rec);
        Assert.Null(k.DriverCacheGap(_game.Id));
        Assert.DoesNotContain(nameHash, k.GameCaches(_game.Id).SelectMany(p => p.Files));

        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));   // NVIDIA's key isn't derivable from the name
        rec.CacheKeys.Clear();
        k.Store.SaveGame(_game.Id, rec);
        Assert.Contains("not cleared", k.DriverCacheGap(_game.Id));
    }

    [Fact]
    public void A_piped_answer_drops_the_byte_order_mark_Windows_PowerShell_sends()
    {
        Assert.Equal("y", Elevated.ReadAnswer(new MemoryStream([0xEF, 0xBB, 0xBF, (byte)'y', (byte)' ', (byte)'\r', (byte)'\n'])));
        Assert.Null(Elevated.ReadAnswer(new MemoryStream()));
    }

    [Fact]
    public async Task An_install_folder_two_sources_list_is_one_game_the_earlier_sources()
    {
        var ea = _game with { Id = "ea:1", Store = Store.EA, InstallDir = _game.InstallDir.ToUpperInvariant() + @"\" };
        var k = new ScsKiller([new FakeSource([_game]), new FakeSource([ea])], new FakeVendor(Gpu), new FakeReader(null), new FakePlanner(),
            new FakeWarmer(), Path.Combine(_root, "data"), _proxy) { Processes = Ours, ProcessNames = OurNames };
        Assert.Equal(_game.Id, Assert.Single(await k.ScanAsync(default)).Game.Id);
    }

    [Fact]
    public async Task The_estimate_uses_this_pcs_first_measured_warm_rate_else_the_vendors_default()
    {
        var second = _game with { Id = "test:second", Name = "Second" };
        var k = Killer(new FakeReader(Unreal), games: [_game, second], vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        var rec = k.Store.LoadGame(second.Id);
        rec.Plan = new Plan(second.Id, "content-1", "PCD3D_SM6", "fake-1", new PlanStats(0, 4000, 0, 0, true, D3D11Shaders: 800), @"C:\x\plan.bin");
        k.Store.SaveGame(second.Id, rec);
        await k.ScanAsync(default);
        TimeSpan? Estimate() => k.Games.Single(s => s.Game.Id == second.Id).EstimatedWarmTime;
        Assert.Equal(TimeSpan.FromSeconds(4800 / ScsKiller.DefaultAmdPsoPerSecond), Estimate());

        await WarmOnce(k, _game.Id);   // FakeWarmer: 10000 items in 10 s
        await k.ScanAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(4.8), Estimate());
        Assert.Equal(ScsKiller.DefaultPsoPerSecond, ScsKiller.DefaultWarmRate(GpuVendor.Nvidia));
    }

    [Fact]
    public async Task The_measured_rate_sums_a_resumed_warms_segments_and_skips_a_rewarm_onto_a_filled_cache()
    {
        WarmResult R(WarmOutcome o, long done, double s) => new(o, done, 10000, 0, TimeSpan.FromSeconds(s), 1 << 20, "", null);
        var warmer = new ScriptedWarmer(R(WarmOutcome.Stopped, 4000, 10), R(WarmOutcome.Completed, 10000, 20), R(WarmOutcome.Completed, 10000, 5));
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Stopped, k.Queue.Single().Stage);
        await WarmOnce(k, _game.Id);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((TimeSpan.FromSeconds(30), 10000 / 30.0), (rec.LastWarmTime, rec.PsoPerSecond));
        Assert.Equal(10000 / 30.0, ScsKiller.MeasuredRate(k.Games));

        await WarmOnce(k, _game.Id);   // the same driver: its cache already holds the game
        rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((TimeSpan.FromSeconds(5), 10000 / 30.0), (rec.LastWarmTime, rec.PsoPerSecond));
        Assert.Equal([0L, 4000L, 0L], warmer.StartAts);
    }

    sealed class ScriptedWarmer(params WarmResult[] results) : IWarmer
    {
        public readonly List<long> StartAts = [];
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress)
        {
            StartAts.Add(options.StartAt);
            return new Run(Task.FromResult(results[StartAts.Count - 1]));
        }
        sealed class Run(Task<WarmResult> completion) : IWarmRun
        {
            public Task<WarmResult> Completion { get; } = completion;
            public void Pause() { }
            public void Resume() { }
            public void Stop() { }
        }
    }

    /// <summary>D3D11 items of one shader for the real-GPU attribution tests: the app samples the open cache files on the
    /// warm's progress lines, so the warm must outlive a few of them. 3000 (one cached shader, well under a second) left it
    /// unsampled in about half the AMD runs (keys empty, 4-5 progress reports) and flaked once on NVIDIA in a
    /// full parallel run; 20000 keep it ~5 s (AMD 4/4 after).</summary>
    const int SampledWarmItems = 20000;

    /// <summary>This checkout's own proxy\build\Release\scskiller_warm.exe (the repo root is the folder with SCSKiller.slnx
    /// above the test binaries), else null: NativeTools.Find walks further up and, from a git worktree under the main
    /// checkout, would pick the main checkout's (older) build.</summary>
    static string? OwnWarmExe()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SCSKiller.slnx")))
                return Path.Combine(d.FullName, "proxy", "build", "Release", "scskiller_warm.exe") is var p && File.Exists(p) ? p : null;
        return null;
    }

    // Real AMD GPU: a tiny warm under a fresh, non-profiled exe name holds the DxcCache
    // files of FNV-1a(name); clearing removes them. Never run under an "ff7rebirth*" name: that is the real game's cache.
    [Trait("Needs", "Gpu")]
    [Fact]
    public async Task Real_amd_a_fresh_name_warms_under_its_name_hash_and_clearing_removes_its_files()
    {
        UseProxyLedger();
        if (GpuBackends.Detect() is not AmdBackend amd || OwnWarmExe() is not { } warmExe) return;   // not this machine, or this checkout's proxy isn't built
        var gpuLock = TestEnv.GpuLockPath;
        if (File.Exists(gpuLock) && DateTime.Now - File.GetLastWriteTime(gpuLock) < TimeSpan.FromMinutes(30)) return;   // someone's GPU run
        Directory.CreateDirectory(Path.GetDirectoryName(gpuLock)!);
        File.WriteAllText(gpuLock, "SCSKiller.Tests: AMD name-hash attribution");
        var game = NamedAs($"scsk-test-{Guid.NewGuid():N}"[..20] + ".exe") with { Id = "test:gpu" };
        var exe = Path.GetFileName(game.ExePath);
        Assert.Null(AmdAppCache.ProfileKey(exe));
        HashSet<string?> D3D11Keys() => Directory.Exists(AmdBackend.D3D11CacheDir)
            ? Directory.GetFiles(AmdBackend.D3D11CacheDir).Select(f => AmdAppCache.Key(Path.GetFileName(f), d3d12: false)).ToHashSet() : [];
        var d3d11Before = D3D11Keys();
        try
        {
            // Creating the staged D3D12 device already makes the driver create and open the name's 2 DxcCache files (measured:
            // also with no D3D12 item at all), so open files don't show that any PSO compiled: the warm's failed count is checked
            // (its compute PSO once failed to decode unnoticed). The D3D11 items keep the warm alive long enough (progress
            // lines) for the attribution to sample it with the files open (SampledWarmItems).
            var warmer = new KeepLog(new Warmer(amd, warmExe));
            var k = Killer(new FakeReader(Unreal), new FakePlanner(GenDb(SampledWarmItems, d3d12: true)), warmer, game: game, vendor: amd);
            k.ThreadsOverride = 1;
            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            k.Log = new Progress<string>(log.Enqueue);
            await k.ScanAsync(default);
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromMinutes(2));
            var keys = k.Store.LoadGame(game.Id).CacheKeys;
            var info = $"{exe}: {k.Queue.Single().Stage} {k.Queue.Single().Error} {k.Queue.Single().Progress}; keys {string.Join(", ", keys)}; " +
                       $"name hash {AmdAppCache.DxcKey(exe)} with {amd.AppCache.FilesOf([AmdAppCache.DxcKey(exe)]).Count} files; " +
                       $"{warmer.ProgressReports} progress reports (attribution samples); app log: {string.Join(" | ", log)}";
            Assert.True(k.Queue.Single().Stage == QueueStage.Done, info);
            Assert.True(warmer.Result?.Failed == 0, $"{info}; failed {warmer.Result?.Failed}; log:\n{warmer.Log}");   // the compute PSO compiles
            Assert.True(keys.Contains(AmdAppCache.DxcKey(exe)), info);
            Assert.Equal(true, AmdAppCache.IsNameHashed(keys, exe));
            Assert.True(k.ClearGameCache(game.Id));
            Assert.Empty(amd.AppCache.FilesOf(keys));
        }
        finally
        {
            // a fresh name: its name-hash files and the D3D11 keys that appeared meanwhile (D3D11 attribution is unreliable)
            var cache = new AmdAppCache(AmdBackend.CacheDir, AmdBackend.D3D11CacheDir);
            foreach (var key in D3D11Keys().Except(d3d11Before).OfType<string>().Append(AmdAppCache.DxcKey(exe)))
                try { cache.Delete([key]); }
                catch (InvalidOperationException) { }   // in use: another app's, not ours
            File.Delete(gpuLock);
        }
    }

    // Real GPU (NVIDIA): a warm of SampledWarmItems D3D11 items (one shader) under a fresh fake exe name, then its cache files
    // are removed.
    [Trait("Needs", "Gpu")]
    [Fact]
    public async Task Real_machine_a_small_warm_attributes_a_new_cache_key_and_clearing_removes_its_files()
    {
        UseProxyLedger();
        if (GpuBackends.Detect() is not NvidiaBackend nv || OwnWarmExe() is not { } warmExe) return;   // not this machine, or this checkout's proxy isn't built
        var gpuLock = TestEnv.GpuLockPath;
        if (File.Exists(gpuLock) && DateTime.Now - File.GetLastWriteTime(gpuLock) < TimeSpan.FromMinutes(30)) return;   // someone's GPU run
        Directory.CreateDirectory(Path.GetDirectoryName(gpuLock)!);
        File.WriteAllText(gpuLock, "SCSKiller.Tests: cache attribution");
        try
        {
            var game = NamedAs($"scsk-test-{Guid.NewGuid():N}"[..20] + ".exe") with { Id = "test:gpu" };
            var before = Directory.GetFiles(NvidiaBackend.CacheDir).Select(Path.GetFileName).ToHashSet();
            var k = Killer(new FakeReader(Unreal), new FakePlanner(GenDb(SampledWarmItems)), new Warmer(nv, warmExe), game: game, vendor: nv);
            k.ThreadsOverride = 1;
            await k.ScanAsync(default);
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
            var key = Assert.Single(k.Store.LoadGame(game.Id).CacheKeys);
            var files = nv.AppCache.FilesOf([key]);
            Assert.NotEmpty(files);
            Assert.All(files, f => Assert.DoesNotContain(f.Name, before));   // a fresh name: all its files are new
            Assert.Equal(files.Sum(f => f.Length), k.Games.Single().CacheOnDisk);

            Assert.True(k.ClearGameCache(game.Id));
            Assert.Empty(nv.AppCache.FilesOf([key]));
        }
        finally { File.Delete(gpuLock); }
    }

    /// <summary>The real warm on WARP (no GPU cache): the create of item K removes the device (SCSKILLER_WARM_REMOVE, as the AMD
    /// driver did on two compute PSOs). The compile goes on in new processes, every other item compiles, K is kept in the
    /// game's state as crashing the driver, and the next compile skips it without a removal. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task A_pipeline_that_removes_the_device_is_skipped_kept_and_never_created_again()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(warmExe)!, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!
            .StandardOutput.ReadToEnd().Trim();
        var warp = new FakeVendor(Gpu with { AdapterLuid = Convert.ToInt64(luid, 16) });
        const int K = 17;
        var (db, keys) = ComputeDb(40);
        var game = NamedAs($"scsk-rm-{Guid.NewGuid():N}"[..16] + ".exe");
        var warmer = new KeepLog(new Warmer(warp, warmExe) { Environment = new Dictionary<string, string> { ["SCSKILLER_WARM_REMOVE"] = K.ToString() } });
        var k = Killer(new FakeReader(Unreal), new FakePlanner(db), warmer, game: game, vendor: warp);
        k.ThreadsOverride = 1;   // WARP converts DXBC to DXIL (dxilconv.dll), which faults now and then under concurrent creates
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        k.QueueChanged += q => { if (q.Note != null) notes.Enqueue(q.Note); };
        await k.ScanAsync(default);
        async Task Compile()
        {
            notes.Clear();
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromMinutes(2));
            var q = k.Queue.Single();
            Assert.True(q.Stage == QueueStage.Done, $"{q.Stage} {q.Error}\n{warmer.Log}");
            Assert.True((warmer.Result!.Done, warmer.Result.Total, warmer.Result.Failed) == (40L, 40L, 0L), $"{warmer.Result}\n{warmer.Log}");   // the other 39 compiled
            Assert.Equal(keys[K], Assert.Single(warmer.Result.Crashed!));
            Assert.Equal("1 skipped (it crashes the GPU driver)", q.Note);
        }

        await Compile();
        Assert.Contains("recovering from a GPU driver crash", notes);
        Assert.Equal([keys[K]], k.Store.LoadGame(game.Id).CrashKeys);
        Assert.Equal(1, k.Games.Single().LastWarmCrashed);

        await Compile();   // the hook is still set: a create of K would remove the device again
        Assert.DoesNotContain("recovering from a GPU driver crash", notes);
        Assert.Contains("1 items skipped: they removed the device in an earlier run", warmer.Log);
        Assert.DoesNotContain("the device was removed", warmer.Log);
    }

    /// <summary>A careful compile on WARP (the recording's 40 compute PSOs, each shader under two root signatures: two
    /// passes, then the plan's 10 at full speed): the create of item K in the second pass removes the device. That pass
    /// recovers like any warm (new process, K blamed and kept), the fast pass still runs, and the next careful compile skips K
    /// inside its pass without a removal. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task A_careful_pass_recovers_from_a_removed_device_and_skips_the_blamed_item_in_later_compiles()
    {
        UseProxyLedger();
        if (OwnWarmExe() is not { } warmExe) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(warmExe)!, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!
            .StandardOutput.ReadToEnd().Trim();
        var warp = new FakeVendor(Gpu with { Vendor = GpuVendor.Amd, AdapterLuid = Convert.ToInt64(luid, 16) });
        const int K = 25;   // the second root signature's 6th PSO: pass 2
        var (recorded, keys) = SiblingComputeDb(20);
        var (plan, _) = ComputeDb(10);
        var game = NamedAs($"scsk-cp-{Guid.NewGuid():N}"[..16] + ".exe");
        var warmer = new KeepLog(new Warmer(warp, warmExe) { Environment = new Dictionary<string, string> { ["SCSKILLER_WARM_REMOVE"] = K.ToString() } });
        var k = Killer(new FakeReader(Unreal), new FakePlanner(plan, stats: new PlanStats(40, 10, 0, 3, true), mainDb: recorded), warmer, game: game, vendor: warp);
        k.ThreadsOverride = 1;   // WARP converts DXBC to DXIL (dxilconv.dll), which faults now and then under concurrent creates
        var rec = k.Store.LoadGame(game.Id);
        rec.Careful = true;
        k.Store.SaveGame(game.Id, rec);
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        k.QueueChanged += q => { if (q.Note != null) notes.Enqueue(q.Note); };
        await k.ScanAsync(default);
        async Task Compile()
        {
            notes.Clear();
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromMinutes(3));
            var q = k.Queue.Single();
            Assert.True(q.Stage == QueueStage.Done, $"{q.Stage} {q.Error}\n{warmer.Log}");
            Assert.True((warmer.Result!.Done, warmer.Result.Total, warmer.Result.Failed) == (50L, 50L, 0L), $"{warmer.Result}\n{warmer.Log}");   // the other 49 compiled
            Assert.Equal(keys[K], Assert.Single(warmer.Result.Crashed!));
            Assert.Contains("(pass 255, 40 items of other passes)", warmer.Log);   // the last process: the plan's pass
        }

        await Compile();
        Assert.Contains("recovering from a GPU driver crash", notes);
        Assert.Contains("careful compile: the plan's other pipelines at full speed", notes);   // the passes after it ran
        Assert.Equal([keys[K]], k.Store.LoadGame(game.Id).CrashKeys);

        await Compile();   // the hook is still set: a create of K would remove the device again
        Assert.DoesNotContain("recovering from a GPU driver crash", notes);
        Assert.True(k.Store.LoadGame(game.Id).WarmedCareful);
    }

    /// <summary>A recorded db of <paramref name="n"/> compute shaders, each under two root signatures (all the first ones, then
    /// all the second ones): siblings, so a careful compile splits them into two passes. Keys in item order.</summary>
    static (byte[] Db, string[] Keys) SiblingComputeDb(int n)
    {
        var db = new MemoryStream();
        var rss = new[] { 0u, 1u }.Select(flags => Core.Planning.RootSig.Serialize(new(flags, [[0, 0, 1, 1, 0, 0, 0]]), [])).ToList();
        var rsShas = rss.Select(rs => PsoDb.Hex(SHA1.HashData(rs))).ToList();
        for (int r = 0; r < 2; r++) PsoDb.WriteBlob(db, rsShas[r], rss[r]);
        var css = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var cs = Carved.Hlsl.Compile($"RWBuffer<uint> b : register(u0); [numthreads(1,1,1)] void main() {{ b[0] = {i + 1000}; }}", "main", "cs_5_0");
            css.Add(PsoDb.Hex(SHA1.HashData(cs)));
            PsoDb.WriteBlob(db, css[i], cs);
        }
        var keys = new List<string>();
        for (int r = 0; r < 2; r++)
            foreach (var cs in css)
            {
                var c = PsoDb.Compute(rsShas[r], cs);
                PsoDb.Write(db, 'C', c);
                keys.Add(new PsoDb.Rec('C', c).Key);
            }
        return (db.ToArray(), [.. keys]);
    }

    /// <summary>Items that crash the driver are skipped by every warm on the driver they crashed; the recompile on a new driver
    /// clears them (one retry each), and one that crashes again is kept for that driver.</summary>
    [Fact]
    public async Task An_earlier_builds_crash_keys_for_the_current_driver_stay_skipped_at_the_next_compile()
    {
        var warmer = new FakeWarmer { Crashed = ["aa"] };
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var rec = k.Store.LoadGame(_game.Id);
        rec.CrashKeysDriver = "100.01";   // as 1.1.0 saved it, for this driver
        k.Store.SaveGame(_game.Id, rec);
        warmer.Crashed = null;
        await WarmOnce(k, _game.Id);   // no evaluation in between
        Assert.Equal(["aa"], warmer.Options!.SkipKeys!);
        Assert.Equal("0000:0010:00000020:100.01", k.Store.LoadGame(_game.Id).CrashKeysDriver);

        // the same on AMD while a registry read is partial: the version string kept, the driver unmoved
        (string?, string?) registry = ("26.8.1", "32.0.31041.1004");
        var dxgi = Gpu with { Vendor = GpuVendor.Amd, DriverVersion = "31.0.24033.1003" };
        var amd = new AmdBackend(dxgi, _ => registry);
        k = Killer(new FakeReader(Unreal), warmer: warmer, vendor: amd);
        k.AppCache = null;   // never the real DxcCache
        k.Adapters = () => [Listed(dxgi)];
        await k.ScanAsync(default);
        rec = k.Store.LoadGame(_game.Id);
        (rec.CrashKeys, rec.CrashKeysDriver) = (["aa"], "26.8.1 (32.0.31041.1004)");   // as 1.1.0 saved them, for this driver
        k.Store.SaveGame(_game.Id, rec);
        (registry, dxgi) = (("26.8.1", null), dxgi with { AdapterLuid = 2 });   // read again (a new LUID), the store version missing
        await WarmOnce(k, _game.Id);
        Assert.Equal(["aa"], warmer.Options!.SkipKeys!);
        Assert.Equal("26.8.1 (32.0.31041.1004)", k.Store.LoadGame(_game.Id).CrashKeysDriver);   // kept, not adopted yet

        registry = ("26.8.1", "32.0.31041.1004");
        await WarmOnce(k, _game.Id);
        Assert.Equal(["aa"], warmer.Options!.SkipKeys!);
        Assert.Equal("1002:0010:00000020:31.0.24033.1003", k.Store.LoadGame(_game.Id).CrashKeysDriver);   // adopted once complete
    }

    [Fact]
    public async Task Items_that_crash_the_driver_get_one_retry_on_a_new_driver()
    {
        var warmer = new FakeWarmer { Crashed = ["aa"] };
        async Task<ScsKiller> Compile(string driver)
        {
            var k = Killer(new FakeReader(Unreal), warmer: warmer, driver: driver);
            await k.ScanAsync(default);
            await WarmOnce(k, _game.Id);
            return k;
        }
        string Kept(ScsKiller k) => k.Store.LoadGame(_game.Id) is var r ? string.Join(",", r.CrashKeys.Order()) + "@" + r.CrashKeysDriver : "";

        var k = await Compile("100.01");
        Assert.Null(warmer.Options!.SkipKeys);
        Assert.Equal("aa@0000:0010:00000020:100.01", Kept(k));   // the driver id
        await Compile("100.01");
        Assert.Equal(["aa"], warmer.Options!.SkipKeys!);   // the same driver: never created again

        warmer.Crashed = null;   // the new driver compiles it
        k = await Compile("101.00");
        Assert.Null(warmer.Options!.SkipKeys);
        Assert.StartsWith("@", Kept(k));

        warmer.Crashed = ["bb"];
        k = await Compile("102.00");
        Assert.Equal("bb@0000:0010:00000020:102.00", Kept(k));
        await Compile("102.00");
        Assert.Equal(["bb"], warmer.Options!.SkipKeys!);
    }

    /// <summary>A gen db of <paramref name="n"/> compute PSOs of distinct shaders ('C' records, one item each) and their keys.</summary>
    static (byte[] Db, string[] Keys) ComputeDb(int n)
    {
        var db = new MemoryStream();
        var rs = Core.Planning.RootSig.Serialize(new(0, [[0, 0, 1, 1, 0, 0, 0]]), []);   // table, all stages, UAV x1 at u0
        var rsSha = PsoDb.Hex(SHA1.HashData(rs));
        PsoDb.WriteBlob(db, rsSha, rs);
        var keys = new string[n];
        for (int i = 0; i < n; i++)
        {
            var cs = Carved.Hlsl.Compile($"RWBuffer<uint> b : register(u0); [numthreads(1,1,1)] void main() {{ b[0] = {i + 1}; }}", "main", "cs_5_0");
            var csSha = PsoDb.Hex(SHA1.HashData(cs));
            PsoDb.WriteBlob(db, csSha, cs);
            var c = PsoDb.Compute(rsSha, csSha);
            PsoDb.Write(db, 'C', c);
            keys[i] = new PsoDb.Rec('C', c).Key;
        }
        return (db.ToArray(), keys);
    }

    /// <summary>A gen db with <paramref name="items"/> D3D11 items of one SM5 compute shader; <paramref name="d3d12"/> adds a
    /// D3D12 compute PSO of it.</summary>
    static byte[] GenDb(int items, bool d3d12 = false)
    {
        // RWBuffer<uint> b : register(u0); [numthreads(1,1,1)] void main() { b[0] = 318566; }  (fxc cs_5_0)
        var cs = Convert.FromBase64String(
            "RFhCQwDxuZNT6cVlRYGgmeR3nx4BAAAA7AEAAAUAAAA0AAAAxAAAANQAAADkAAAAUAEAAFJERUaIAAAAAAAAAAAAAAABAAAAPAAAAAAFU0MAAQAAXgAAAFJEMTE8AAAAGAAAACAAAAAoAAAAJAAAAAwAAAAAAAAAXAAAAAQAAAAEAAAAAQAAAP////8AAAAAAQAAAAEAAABiAE1pY3Jvc29mdCAoUikgSExTTCBTaGFkZXIgQ29tcGlsZXIgMTAuMQCrq0lTR04IAAAAAAAAAAgAAABPU0dOCAAAAAAAAAAIAAAAU0hFWGQAAABQAAUAGQAAAGoIAAGcCAAEAOARAAAAAABERAAAmwAABAEAAAABAAAAAQAAAKQAAA3y4BEAAAAAAAJAAAAAAAAAAAAAAAAAAAAAAAAAAkAAAGbcBABm3AQAZtwEAGbcBAA+AAABU1RBVJQAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAA");
        var sha = System.Security.Cryptography.SHA1.HashData(cs);
        var db = new MemoryStream();
        var w = new BinaryWriter(db);
        w.Write((byte)'B'); w.Write(20 + cs.Length); w.Write(sha); w.Write(cs);
        for (int i = 0; i < items; i++) { w.Write((byte)'1'); w.Write(24); w.Write((int)Stage.Compute); w.Write(sha); }
        if (d3d12)   // one D3D12 compute PSO of the same shader: a root signature with a u0 table, then the 'C' template
        {
            var rs = Core.Planning.RootSig.Serialize(new(0, [[0, 0, 1, 1, 0, 0, 0]]), []);   // table, all stages, UAV x1 at u0
            var rsSha = System.Security.Cryptography.SHA1.HashData(rs);
            w.Write((byte)'B'); w.Write(20 + rs.Length); w.Write(rsSha); w.Write(rs);
            var c = PsoDb.Compute(PsoDb.Hex(rsSha), PsoDb.Hex(sha));   // the whole desc (48 bytes): a 40-byte one failed to decode
            w.Write((byte)'C'); w.Write(c.Length); w.Write(c);
        }
        return db.ToArray();
    }


    /// <summary>A warm that runs until Finish(), recording its options and pause state.</summary>
    [Fact]
    public async Task A_chained_recorder_whose_install_was_cut_off_before_its_ini_gets_the_ini_and_its_mod_back()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, "d3d12.dll"), Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray()));
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.SetRecordAlongsideMod(_game.Id, true);   // installs, chained to the mod
        Assert.Contains($"next={ScsKiller.ChainName}", File.ReadAllText(ini));

        // the state an install leaves when the process ends after the rename and the copy, before the ini
        File.Delete(ini);
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderFiles.Remove("scskiller.ini");
        k.Store.SaveGame(_game.Id, rec);
        k.ReconcileRecorders(_game.Id);
        Assert.Contains($"next={ScsKiller.ChainName}", File.ReadAllText(ini));
        Assert.True(k.Store.LoadGame(_game.Id).RecorderFiles.ContainsKey("scskiller.ini"));   // ours: removed with the recorder
    }

    [Fact]
    public async Task A_launch_judged_while_a_recompile_runs_doesnt_judge_the_warm_that_ends_after_it()
    {
        var amd = new FakeVendor(Gpu with { Vendor = GpuVendor.Amd });
        var k = Killer(new FakeReader(Unreal), vendor: amd);
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var warmer = new ControlledWarmer();
        k = Killer(new FakeReader(Unreal), warmer: warmer, vendor: amd);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await warmer.WhenStarted();
        var warmedAt = k.Store.LoadGame(_game.Id).WarmedAt!.Value.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), Launch(warmedAt + 1, 100, 300));   // played during the recompile
        k.RefreshGame(_game.Id);   // its exit: judged against the warm before
        Assert.NotNull(k.Store.LoadGame(_game.Id).FirstLaunch);

        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(k.Store.LoadGame(_game.Id).FirstLaunch);   // the next launch judges this warm
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, false), (s.Status, ScsKiller.IsPartlyWarmed(s)));
    }

    [Fact]
    public async Task A_resumed_warm_reports_the_failures_of_the_segments_before_its_stop()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await warmer.WhenStarted();
        warmer.Run!.Failed = 100;
        k.StopQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Stopped, "100 failed (the driver rejected them)"), (k.Queue.Single().Stage, k.Queue.Single().Note));

        k.StartQueue();   // continues where it stopped; this segment fails nothing
        await warmer.WhenStarted(2);
        Assert.Equal(10, warmer.Options!.StartAt);
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Done, "100 failed (the driver rejected them)"), (k.Queue.Single().Stage, k.Queue.Single().Note));
        Assert.Equal(100, k.Store.LoadGame(_game.Id).LastWarmFailed);
    }

    [Fact]
    public async Task A_stopped_warm_starts_over_on_another_driver()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await warmer.WhenStarted();
        k.StopQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(10, k.Store.LoadGame(_game.Id).ResumeAt);

        warmer = new ControlledWarmer();
        k = Killer(new FakeReader(Unreal), warmer: warmer, driver: "101.00");   // a driver update: its cache has none of the replayed ones
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await warmer.WhenStarted();
        Assert.Equal(0, warmer.Options!.StartAt);
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("101.00", k.Store.LoadGame(_game.Id).WarmedDriverVersion);
    }

    /// <summary><paramref name="killed"/>: the log is still next to the staged exe, below the stage folder.</summary>
    sealed class FailingWarmer(bool killed) : IWarmer
    {
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress)
        {
            var log = Path.Combine(workDir, "stage-1-1", "scskiller.log");   // where scskiller_warm stages and logs
            var written = killed ? Path.Combine(workDir, "stage-1-1", "FakeGame", "Fake", "Binaries", "Win64", "scskiller.log") : log;
            Directory.CreateDirectory(Path.GetDirectoryName(written)!);
            File.WriteAllText(written, "device removed");
            return new Done(new WarmResult(WarmOutcome.Failed, 3, 100, 0, TimeSpan.FromSeconds(1), 0, log, "scskiller_warm exited with code 5"));
        }

        public sealed class Done(WarmResult result) : IWarmRun
        {
            public Task<WarmResult> Completion { get; } = Task.FromResult(result);
            public void Pause() { }
            public void Resume() { }
            public void Stop() { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_warm_names_a_log_that_outlives_the_work_folder(bool killed)
    {
        var k = Killer(new FakeReader(Unreal), warmer: new FailingWarmer(killed));
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var q = k.Queue.Single();
        Assert.Equal(QueueStage.Failed, q.Stage);
        Assert.StartsWith("scskiller_warm exited with code 5 (log: ", q.Error);
        var log = q.Error![(q.Error.IndexOf("(log: ") + 6)..^1];
        Assert.False(Directory.Exists(Path.Combine(k.Store.GameDir(_game.Id), "work")));
        Assert.Equal("device removed", File.ReadAllText(log));
    }

    /// <summary>A compile that throws names the stage it failed in, and keeps the whole exception, its stack included, in
    /// compile-error.log in the game's data folder for a bug report.</summary>
    [Fact]
    public async Task A_failed_compile_names_its_stage_and_keeps_the_stack()
    {
        var k = Killer(new FakeReader(Unreal), new FakePlanner(onBuild: () => throw new InvalidDataException("a plan that can't be built")));
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var q = k.Queue.Single();
        Assert.Equal(QueueStage.Failed, q.Stage);
        var log = Path.Combine(k.Store.GameDir(_game.Id), "compile-error.log");
        Assert.Equal($"Planning failed: a plan that can't be built (log: {log})", q.Error);
        var text = File.ReadAllText(log);
        Assert.Contains("stage: Planning", text);
        Assert.Contains("System.IO.InvalidDataException: a plan that can't be built", text);
        Assert.Contains($"at {typeof(FakePlanner).FullName!.Replace('+', '.')}.Build(", text);
    }

    sealed class RejectingWarmer : IWarmer
    {
        public long Failed;
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress)
        {
            foreach (var (stage, line) in new[] { ("stage-1-1", "retry"), ("stage-1-2", $"{Failed} rejected") })   // a retry's processes
            {
                Directory.CreateDirectory(Path.Combine(workDir, stage));
                File.WriteAllText(Path.Combine(workDir, stage, "scskiller.log"), line);
            }
            var result = new WarmResult(WarmOutcome.Completed, 100, 100, Failed, TimeSpan.FromSeconds(1), 0, Path.Combine(workDir, "stage-1-2", "scskiller.log"), null);
            return new FailingWarmer.Done(result);
        }
    }

    [Fact]
    public async Task A_completed_warm_with_rejections_keeps_its_logs_until_a_clean_one()
    {
        var warmer = new RejectingWarmer { Failed = 3 };
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var kept = Path.Combine(k.Store.GameDir(_game.Id), "warm-rejects.log");
        Assert.Equal(["retry", "3 rejected"], File.ReadAllLines(kept));

        warmer.Failed = 5;
        await WarmOnce(k, _game.Id);
        Assert.Equal(["retry", "5 rejected"], File.ReadAllLines(kept));

        warmer.Failed = 0;
        await WarmOnce(k, _game.Id);
        Assert.False(File.Exists(kept));
    }

    [Fact]
    public async Task A_warmed_game_whose_attributed_cache_files_are_all_gone_is_stale()
    {
        var warmer = new FakeWarmer(() =>
        {
            CacheFile("0002a91d11111111.nvph", 65536);
            return new FileStream(CacheFile("fc52a91d11111111.nvph", 4096), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        });
        var game = NamedAs(Process.GetCurrentProcess().ProcessName + ".exe");   // this process holds them
        var k = Killer(new FakeReader(Unreal), warmer: warmer, game: game);
        (k.AppCache, k.RunningGameExes) = (new NvidiaAppCache(Path.Combine(_root, "DXCache")), () => new HashSet<string>());
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        File.Delete(Path.Combine(_root, "DXCache", "0002a91d11111111.nvph"));   // one of them: which warm wrote a file isn't known
        k.RefreshGame(game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        File.Delete(Path.Combine(_root, "DXCache", "fc52a91d11111111.nvph"));   // a shader cache reset
        k.RefreshGame(game.Id);
        Assert.Equal((GameStatus.Stale, ScsKiller.TrimmedAllReason), (k.Games.Single().Status, k.Games.Single().StatusReason));
    }

    [Fact]
    public async Task An_import_is_saved_before_it_empties_the_inbox()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        using (var f = File.Create(inbox))
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        DateTimeOffset? stored = null;
        k.InboxRotating = () => stored = new AppStore(k.Store.DataDir).LoadGame(_game.Id).RecordingImportedAt;   // what a crash here leaves
        k.RefreshGame(_game.Id);
        Assert.NotNull(stored);
        Assert.Equal(0, new FileInfo(inbox).Length);
    }

    [Fact]
    public async Task An_inbox_merged_by_an_import_that_never_saved_its_record_counts_as_imported()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        using (var f = File.Create(inbox))
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        var store = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        Recordings.Merge(store, inbox, null);   // the import's merge, then the process ended
        Assert.Null(k.Store.LoadGame(_game.Id).RecordingImportedAt);

        k.RefreshGame(_game.Id);   // the retry adds nothing new to the copy
        Assert.NotNull(k.Store.LoadGame(_game.Id).RecordingImportedAt);
        Assert.Equal(0, new FileInfo(inbox).Length);
    }

    /// <summary>Synthesized PSOs take the NVAPI state of the recordings, the community's too (PlanBuilder.RasterNv): an
    /// import that stores an 'N' record the recording lacked re-plans, whatever this recording alone would infer; one whose
    /// 'N' records it has already doesn't.</summary>
    [Fact]
    public async Task An_import_of_new_NVAPI_state_alone_re_plans()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        var recs = Enumerable.Range(0, 100).Select(i => new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, $"{i:x40}"))).ToList();
        PsoDb.Rec N(PsoDb.Rec r, uint slot, uint options = 0) => new PsoDb.NvState(r.Key, slot, 1, 1, options).ToRec();
        var imported = k.Store.LoadGame(_game.Id).RecordingImportedAt;
        bool Import(IEnumerable<PsoDb.Rec> rs)
        {
            using (var f = File.Create(inbox)) foreach (var r in rs) PsoDb.Write(f, r.Tag, r.Payload);
            k.RefreshGame(_game.Id);
            Assert.Equal(0, new FileInfo(inbox).Length);
            var now = k.Store.LoadGame(_game.Id).RecordingImportedAt;
            (var bumped, imported) = (now > imported || imported == null && now != null, now);
            return bumped;
        }

        Assert.True(Import(recs));
        Assert.True(Import(recs.Take(1).Select(r => N(r, 12))));   // 1 of 100 here; a community recording may hold the other 99
        Assert.True(Import(recs.Skip(1).Select(r => N(r, 12))));
        Assert.True(Import(recs.Take(2).Select(r => N(r, uint.MaxValue, 17))));   // the last state of 2: no slot, options
        Assert.False(Import(recs.Take(50).Select(r => N(r, 12))));   // all stored already
    }

    sealed class ThrowingSizeCache : IAppCache
    {
        public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys) => [];
        int _sized;
        public long SizeOf(IEnumerable<string> keys) => Interlocked.Increment(ref _sized) == 1 ? throw new NotSupportedException("cache unreadable") : 0;   // once
        public IReadOnlySet<string> KeysOpenBy(string exeFileName) => new HashSet<string> { "11111111" };
        public int Delete(IEnumerable<string> keys) => 0;
    }

    [Fact]
    public async Task An_exception_after_an_item_leaves_the_queue_running_the_next_ones()
    {
        var warmer = new FakeWarmer(() => new MemoryStream());   // runs a second: attribution samples it
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        k.AppCache = new ThrowingSizeCache();   // the refresh after the warm throws (the warm attributes a key)
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, warmer.Started.Count);
    }

    /// <summary>Unreal 5.4 until the store reports build 2, then 5.5; the engine version each index was asked for.</summary>
    sealed class UpdatedReader : IEngineReader
    {
        public readonly List<string> Indexed = [];
        public EngineInfo? Detect(Game game) => Unreal with { Version = game.Version == "2" ? "5.5" : "5.4" };
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct)
        {
            lock (Indexed) Indexed.Add(e.Version);
            return new("content-" + game.Version, ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        }
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary><see cref="FakePlanner"/>, but build 3 has nothing it can compile and build 4 needs a recording; with
    /// <paramref name="recorded"/>, every build without one does.</summary>
    sealed class UpdatedPlanner(PlanStats? stats = null, bool recorded = false) : IPlanner
    {
        readonly FakePlanner _inner = new(stats: stats);
        public PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps) => game.Version switch
        {
            "3" => new(Readiness.Unsupported, "no D3D12 shaders"),
            "4" => new(Readiness.NeedsRecording, "needs a recording"),
            _ when recorded && recording == null => new(Readiness.NeedsRecording, "needs a recording"),
            _ => _inner.Check(game, engine, recording, caps),
        };
        public Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir, IProgress<string>? log, CancellationToken ct, bool maximum = false) =>
            _inner.Build(game, engine, index, recording, caps, outDir, log, ct, maximum);
        public void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct) =>
            _inner.Materialize(plan, game, engine, reader, recording, workDir, ct);
    }

    [Fact]
    public async Task A_compile_after_an_update_the_scan_missed_plans_with_the_installed_builds_engine()
    {
        var source = new FakeSource([_game with { Version = "1" }]);
        var reader = new UpdatedReader();
        var k = new ScsKiller([source], new FakeVendor(Gpu), reader, new UpdatedPlanner(), new FakeWarmer(), Path.Combine(_root, "data"), _proxy) { LocalAppData = _root, Processes = Ours, ProcessNames = OurNames };
        await k.ScanAsync(default);
        Assert.Equal("5.4", k.Games.Single().Engine!.Version);
        source.Games = [_game with { Version = "2" }];   // the store updated the game while the app ran
        await WarmOnce(k, _game.Id);
        Assert.Equal(["5.5"], reader.Indexed);
        Assert.Equal("5.5", k.Games.Single().Engine!.Version);

        source.Games = [_game with { Version = "3" }];   // an update to a build SCSKiller can't compile
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Failed, "not ready: no D3D12 shaders"), (k.Queue.Single().Stage, k.Queue.Single().Error));
        Assert.Equal(["5.5"], reader.Indexed);
    }

    [Fact]
    public async Task A_partial_ray_tracing_plan_doesnt_let_a_compile_through_that_the_planner_says_needs_a_recording()
    {
        var source = new FakeSource([_game with { Version = "1" }]);
        var rt = new PlanStats(0, 40_000, 40_000, 100, true, RtLibraries: 12_000, RtUncovered: 12_000, RtInline: 0);
        var k = new ScsKiller([source], new FakeVendor(Gpu), new UpdatedReader(), new UpdatedPlanner(rt), new FakeWarmer(), Path.Combine(_root, "data"), _proxy) { LocalAppData = _root, Processes = Ours, ProcessNames = OurNames };
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);   // everything but the ray tracing
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        source.Games = [_game with { Version = "4" }];   // the installed build needs a recording for more than its ray tracing
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Failed, "not ready: needs a recording"), (k.Queue.Single().Stage, k.Queue.Single().Error));
    }

    [Fact]
    public async Task A_recording_another_process_imported_lets_a_compile_through_that_the_scan_found_needing_one()
    {
        var k = new ScsKiller([new FakeSource([_game])], new FakeVendor(Gpu), new FakeReader(Unreal), new UpdatedPlanner(recorded: true), new FakeWarmer(),
            Path.Combine(_root, "data"), _proxy) { LocalAppData = _root, Processes = Ours, ProcessNames = OurNames };
        await k.ScanAsync(default);
        Assert.Equal(GameStatus.NeedsRecording, k.Games.Single().Status);

        var other = new AppStore(k.Store.DataDir);   // the app imports one while the command line's queue shell is open
        Directory.CreateDirectory(other.GameDir(_game.Id));
        PsoDb.WriteCompact(Path.Combine(other.GameDir(_game.Id), "recording.db"), [new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)))]);
        var rec = other.LoadGame(_game.Id);
        rec.RecordingImportedAt = DateTimeOffset.Now;
        other.SaveGame(_game.Id, rec);
        await WarmOnce(k, _game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Two_processes_compiling_one_game_take_turns()
    {
        var first = new ControlledWarmer();
        var app = Killer(new FakeReader(Unreal), warmer: first);
        var cliWarmer = new FakeWarmer();
        var cli = Killer(new FakeReader(Unreal), warmer: cliWarmer);   // the same data folder: another process's queue
        await app.ScanAsync(default);
        await cli.ScanAsync(default);
        await WarmOnce(cli, _game.Id);   // the command line's queue has finished an item before
        app.Enqueue(_game.Id);
        app.StartQueue();
        await first.WhenStarted();
        cli.Enqueue(_game.Id);
        cli.StartQueue();
        await Until(() => cli.Queue.Single().Note == ScsKiller.AnotherCompileNote);
        await Task.Delay(700);
        Assert.Single(cliWarmer.Started);   // nothing of the game's plan or work folder touched meanwhile
        Assert.True(Directory.Exists(Path.Combine(app.Store.GameDir(_game.Id), "work")));
        cli.PauseQueue();
        cli.ResumeQueue();
        Assert.Equal(QueueStage.Waiting, cli.Queue.Single().Stage);   // still waiting, not the finished item's stage

        first.Run!.Finish();
        await app.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        await cli.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Done, QueueStage.Done), (app.Queue.Single().Stage, cli.Queue.Single().Stage));
        Assert.Equal(2, cliWarmer.Started.Count);
    }

    sealed class ControlledWarmer : IWarmer
    {
        public volatile ControlledRun? Run;
        public WarmOptions? Options;
        public readonly List<string> Started = [];
        readonly List<(int N, TaskCompletionSource Done)> _waits = [];
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress)
        {
            Options = options;
            progress?.Report(new WarmProgress(10, 100, 0, 50));
            var run = Run = new ControlledRun();
            lock (Started)
            {
                Started.Add(game.Id);
                foreach (var w in _waits.Where(w => w.N <= Started.Count)) w.Done.TrySetResult();
                _waits.RemoveAll(w => w.N <= Started.Count);
            }
            return run;
        }

        /// <summary>Done once <paramref name="n"/> warms have started (Run, Options and Started set): a signal, not a poll,
        /// so a loaded PC only makes it later. The ceiling is a hang guard.</summary>
        public Task WhenStarted(int n = 1)
        {
            lock (Started)
            {
                if (Started.Count >= n) return Task.CompletedTask;
                var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waits.Add((n, t));
                return t.Task.WaitAsync(HangGuard);
            }
        }
    }

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    /// <summary>A real warmer whose last result and stage\scskiller.log are kept (the work folder is deleted after the warm).</summary>
    sealed class KeepLog(IWarmer inner) : IWarmer
    {
        public WarmResult? Result;
        public string? Log;
        public int ProgressReports;
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress) =>
            new Run(this, inner.Start(game, workDir, options, new Counted(this, progress)));

        sealed class Counted(KeepLog owner, IProgress<WarmProgress>? progress) : IProgress<WarmProgress>
        {
            public void Report(WarmProgress p) { Interlocked.Increment(ref owner.ProgressReports); progress?.Report(p); }
        }

        sealed class Run(KeepLog owner, IWarmRun run) : IWarmRun
        {
            public Task<WarmResult> Completion { get; } = run.Completion.ContinueWith(t =>
            {
                owner.Result = t.Result;
                try { owner.Log = File.ReadAllText(t.Result.LogPath); } catch (IOException) { }
                return t.Result;
            }, TaskScheduler.Default);
            public void Pause() => run.Pause();
            public void Resume() => run.Resume();
            public void Stop() => run.Stop();
        }
    }

    sealed class ControlledRun : IWarmRun
    {
        readonly TaskCompletionSource<WarmResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Paused, Stopped;
        public long Failed;   // the driver rejected these before the stop
        public Task<WarmResult> Completion => _done.Task;
        public void Pause() => Paused = true;
        public void Resume() => Paused = false;
        public void Stop()
        {
            Stopped = true;
            _done.TrySetResult(new WarmResult(WarmOutcome.Stopped, 10, 100, Failed, TimeSpan.FromSeconds(1), 0, "", null));
        }
        /// <summary>Done once stopped; the ceiling is a hang guard.</summary>
        public Task WhenStopped => _done.Task.WaitAsync(HangGuard);
        public void Finish() => _done.TrySetResult(new WarmResult(WarmOutcome.Completed, 100, 100, 0, TimeSpan.FromSeconds(1), 0, "", null));
    }

    [Fact]
    public async Task The_server_stutter_list_replaces_the_cached_one_after_a_scan_daily_or_on_the_users_refresh()
    {
        static string List(string name) =>
            $$"""{"games":[{"name":"{{name}}","ids":[],"severity":"severe","reason":"r","source":"{{Core.Games.StutterList.OwnMeasurement}}","date":"2026-01-01"}]}""";
        var cache = Path.Combine(_root, "data", "known-stutter.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, List("Fake Game"));
        File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-2));
        var served = List("Other Game");
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath.EndsWith("known-stutter.json")
            ? CommunityTests.Ours(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(served)) : CommunityTests.Ours(HttpStatusCode.NotFound));
        try
        {
            var k = Killer();
            await k.ScanAsync(default);   // no routes (the CLI): the cached copy, over the embedded one
            Assert.NotNull(k.Games.Single().KnownStutter);

            k.ContentRoutes = new RouteFailover(fake, [new("https://api.test.com/")]);
            var changed = new List<GameState>();
            k.GameChanged += s => { lock (changed) changed.Add(s); };
            await k.ScanAsync(default);
            await k.StutterUpdate;
            Assert.Null(k.Games.Single().KnownStutter);   // the server's copy, without another scan
            Assert.Null(changed[^1].KnownStutter);
            Assert.Equal(served, File.ReadAllText(cache));
            Assert.Equal(3, fake.Log.Count);   // the three lists

            served = List("Fake Game");
            await k.ScanAsync(default);
            await k.StutterUpdate;
            Assert.Equal(3, fake.Log.Count);   // checked less than a day ago
            await k.RescanAsync(default);
            await k.StutterUpdate;
            Assert.Equal(3, fake.Log.Count);   // a full re-read alone isn't the user's refresh
            await k.ScanAsync(default, userRequested: true);
            await k.StutterUpdate;
            Assert.Equal(6, fake.Log.Count);
            Assert.NotNull(k.Games.Single().KnownStutter);
        }
        finally { Core.Games.StutterList.Current = Core.Games.StutterList.Embedded; }
    }

    [Fact]
    public async Task The_users_refresh_fetches_the_lists_and_the_manifest_however_fresh_once_per_window_across_restarts()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "known-stutter.json"), """{"games":[]}""");   // checked just now
        var list = $$"""{"games":[{"name":"Other Game","ids":[],"severity":"severe","reason":"r","source":"{{Core.Games.StutterList.OwnMeasurement}}","date":"2026-01-01"}]}""";
        var manifest = CommunityTests.Manifest();
        var up = true;
        var fake = new CommunityTests.Fake(r =>
            r.RequestUri!.AbsolutePath.StartsWith("/v1/manifest/") ? CommunityTests.Ours(HttpStatusCode.OK, manifest)
            : up && r.RequestUri.AbsolutePath.EndsWith("known-stutter.json") ? CommunityTests.Ours(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(list))
            : CommunityTests.Ours(HttpStatusCode.NotFound));
        var routes = new RouteFailover(fake, [new("https://api.test.com/")]);
        var asked = 0;
        ScsKiller Start()
        {
            var killer = Killer(new FakeReader(Unreal));
            (killer.ContentRoutes, killer.Community) = (routes, new Community(data, (_, _) => Task.FromResult<string?>("token"), routes));
            killer.UserFetch = () => { Interlocked.Increment(ref asked); return Task.CompletedTask; };
            return killer;
        }
        var k = Start();
        async Task<(int Content, int Manifest)> Scan(Func<Task> scan)
        {
            await scan();
            await Task.WhenAll(k.StutterUpdate, k.CommunitySync, k.ServerRefresh);
            lock (fake.Log) return (fake.Log.Count(l => l.Contains("/v1/content/")), fake.Log.Count(l => l.Contains("/v1/manifest/")));
        }

        try
        {
            Assert.Equal((0, 1), await Scan(() => k.ScanAsync(default)));   // no manifest copy yet; the lists are fresh
            Assert.Equal((0, 1), await Scan(() => k.ScanAsync(default)));   // automatic: both copies fresh
            Assert.Equal(0, asked);
            Assert.Equal((3, 2), await Scan(() => k.ScanAsync(default, userRequested: true)));
            Assert.Equal(ScsKiller.ServerCheck.Done, await k.ServerRefresh);
            Assert.Equal((3, 2), await Scan(() => k.RescanAsync(default, userRequested: true)));   // a second click within the window
            Assert.Equal(ScsKiller.ServerCheck.TooSoon, await k.ServerRefresh);
            Assert.Equal(1, asked);

            k = Start();   // a restart keeps the window
            Assert.Equal((3, 2), await Scan(() => k.ScanAsync(default, userRequested: true)));
            Assert.Equal(ScsKiller.ServerCheck.TooSoon, await k.ServerRefresh);

            File.Delete(Path.Combine(data, "user-fetch.txt"));   // the window over, and the lists unreachable
            up = false;
            Assert.Equal((6, 3), await Scan(() => k.ScanAsync(default, userRequested: true)));
            Assert.Equal(ScsKiller.ServerCheck.Unreachable, await k.ServerRefresh);
            Assert.Equal(2, asked);
        }
        finally { Core.Games.StutterList.Current = Core.Games.StutterList.Embedded; }
    }

    [Fact]
    public async Task After_a_refused_manifest_check_neither_clicks_nor_scans_check_again_before_its_retry_after()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var fake = new CommunityTests.Fake(r =>
        {
            if (!r.RequestUri!.AbsolutePath.StartsWith("/v1/manifest/")) return CommunityTests.Ours(HttpStatusCode.NotFound);
            var limited = CommunityTests.Ours(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new(TimeSpan.FromHours(1));
            return limited;
        });
        var k = Killer(new FakeReader(Unreal));
        k.Community = new Community(data, (_, _) => Task.FromResult<string?>("token"), new RouteFailover(fake, [new("https://api.test.com/")]));
        async Task<int> Checks(Func<Task> scan)
        {
            await scan();
            await Task.WhenAll(k.CommunitySync, k.ServerRefresh);
            lock (fake.Log) return fake.Log.Count(l => l.Contains("/v1/manifest/"));
        }

        Assert.Equal(1, await Checks(() => k.ScanAsync(default, userRequested: true)));   // refused
        Assert.Equal(1, await Checks(() => k.ScanAsync(default, userRequested: true)));   // inside the window: the copy (none), no check
        Assert.Equal(1, await Checks(() => k.ScanAsync(default)));
    }

    [Fact]
    public async Task A_refresh_during_a_community_pass_checks_the_manifest_again_once_the_pass_ends_with_the_latest_games()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var release = new ManualResetEventSlim();
        const string content = "0123456789abcdef0123456789abcdef01234567";
        var later = _game with { Id = "test:later", Name = "Later Game", Version = "100" };
        var manifest = CommunityTests.Manifest(CommunityTests.Alias($"{later.Id}@100", content), CommunityTests.Entry(content, "object"u8.ToArray(), 1));
        var fake = new CommunityTests.Fake(r =>
        {
            if (!r.RequestUri!.AbsolutePath.StartsWith("/v1/manifest/")) return CommunityTests.Ours(HttpStatusCode.NotFound);
            release.Wait(TimeSpan.FromSeconds(10));
            return CommunityTests.Ours(HttpStatusCode.OK, manifest);
        });
        var source = new FakeSource([_game]);
        var k = Killer(new FakeReader(Unreal), source: source);
        k.Community = new Community(data, (_, _) => Task.FromResult<string?>("token"), new RouteFailover(fake, [new("https://api.test.com/")]));
        await k.ScanAsync(default);   // its pass waits on the manifest
        await k.ScanAsync(default, userRequested: true);
        var pending = k.ServerRefresh;
        k.StartCommunitySync(fresh: true);   // more refreshes behind the same pass: still one pass queued
        source.Games = [_game, later];   // installed meanwhile
        await k.ScanAsync(default, userRequested: true);   // inside the window: the pending refresh, and its pass takes this scan's games
        Assert.Same(pending, k.ServerRefresh);
        release.Set();
        Assert.Equal(ScsKiller.ServerCheck.Done, await k.ServerRefresh);
        await k.CommunitySync;
        lock (fake.Log)
        {
            Assert.Equal(2, fake.Log.Count(l => l.Contains("/v1/manifest/")));
            Assert.Contains(fake.Log, l => l.Contains("/v1/o/"));   // the game the denied click found
        }
    }

    [Fact]
    public async Task An_anti_cheat_games_not_tested_note_doesnt_suggest_recording()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, "BEService_x64.exe"), [0]);
        var k = Killer(new FakeReader(Unreal with { Version = "5.4", Fork = "GAME_OnlyThisTest" }), new Planner());
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Ready, Planner.UntestedNote), (s.Status, s.StatusReason));
        Assert.Equal("Not tested on this engine version", Format.ShortNote(s));
    }

    /// <summary>A scan keeps the planner's check; an engine the server's list confirms afterwards loses its note without a rescan.</summary>
    [Fact]
    public async Task The_server_confirmed_engines_list_drops_the_not_tested_note_after_a_scan()
    {
        const string served = """{"engines":[{"version":"5.4","fork":"GAME_OnlyThisTest","game":"Some Game","evidence":"its recording"}]}""";
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath.EndsWith("confirmed-engines.json")
            ? CommunityTests.Ours(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(served)) : CommunityTests.Ours(HttpStatusCode.NotFound));
        try
        {
            var k = Killer(new FakeReader(Unreal with { Version = "5.4", Fork = "GAME_OnlyThisTest" }), new Planner());
            await k.ScanAsync(default);
            Assert.Equal((GameStatus.Ready, Planner.Untested), (k.Games.Single().Status, k.Games.Single().StatusReason));

            k.ContentRoutes = new RouteFailover(fake, [new("https://api.test.com/")]);
            await k.ScanAsync(default);
            await k.StutterUpdate;
            Assert.Equal((GameStatus.Ready, Planner.NoRecording), (k.Games.Single().Status, k.Games.Single().StatusReason));
            Assert.Equal(served, File.ReadAllText(Path.Combine(_root, "data", "confirmed-engines.json")));

            Core.Planning.ConfirmedEngines.Current = Core.Planning.ConfirmedEngines.Embedded;
            var next = Killer(new FakeReader(Unreal with { Version = "5.4", Fork = "GAME_OnlyThisTest" }), new Planner());   // the next start: the cached copy
            await next.ScanAsync(default);
            Assert.Equal(Planner.NoRecording, next.Games.Single().StatusReason);
        }
        finally { Core.Planning.ConfirmedEngines.Current = Core.Planning.ConfirmedEngines.Embedded; }
    }

    static string Verdicts(string verdict, string reason, params string[] ids) =>
        $$"""{"games":[{"name":"Fake Game","ids":[{{string.Join(",", ids.Select(i => $"\"{i}\""))}}],"verdict":"{{verdict}}","reason":"{{reason}}","source":"https://example.org/a","date":"2026-01-01"}]}""";

    // a key of this run only: production pins ContentTrust.RulesKeys
    static readonly (string Seed, string Public) VerdictsKey = FeedTrust.NewKey();
    static readonly Dictionary<string, string> VerdictsKeys = new() { ["rules-a"] = VerdictsKey.Public };
    static string SignedVerdicts(string body, DateTimeOffset at) =>
        ContentTrust.Sign(Convert.FromBase64String(VerdictsKey.Seed), "rules-a", Core.Games.GameVerdicts.Name, body, at);

    [Fact]
    public async Task A_game_without_shader_stutter_loses_its_default_recorder_once_the_server_lists_it_signed_and_keeps_an_asked_one()
    {
        var listed = FakeGame("epic:nostutter-listed", "Listed");   // only this test lists them: the list in use is shared; not steam, which VAC checks
        var other = FakeGame("epic:nostutter-other", "Other");
        var body = Verdicts(Core.Games.GameVerdicts.NoStutterVerdict, "engine-precompiles", listed.Id);
        var served = body;
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath.EndsWith("/v1/content/game-verdicts.json")
            ? CommunityTests.Ours(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(served)) : CommunityTests.Ours(HttpStatusCode.NotFound));
        try
        {
            var k = RecordKiller([listed, other]);
            k.ManageRecorders = true;
            k.ContentKeys = VerdictsKeys;
            await k.ScanAsync(default);
            Assert.All(k.Games, s => Assert.True(s.RecorderInstalled));

            k.ContentRoutes = new RouteFailover(fake, [new("https://api.test.com/")]);
            await k.ScanAsync(default);
            await k.StutterUpdate;
            Assert.All(k.Games, s => Assert.True(s is { NoStutter: null, RecorderInstalled: true }));   // unsigned: refused

            served = SignedVerdicts(body, DateTimeOffset.UtcNow);
            await k.ScanAsync(default, userRequested: true);
            await k.StutterUpdate;
            await Until(() => !k.Games.Single(s => s.Game.Id == listed.Id).RecorderInstalled);
            Assert.Equal("Its engine prepares shaders at startup", k.Games.Single(s => s.Game.Id == listed.Id).NoStutter);
            Assert.True(k.Games.Single(s => s.Game.Id == other.Id) is { NoStutter: null, RecorderInstalled: true });
            Assert.Equal(served, File.ReadAllText(Path.Combine(_root, "data", "game-verdicts.json")));
            Assert.NotNull(k.Store.LoadList()!.Games.Single(s => s.Game.Id == listed.Id).NoStutter);   // a start without a scan has it too

            k.SetRecorderOverride(listed.Id, RecorderOverride.On);
            Assert.True(ScsKiller.IsOurProxy(Dll(listed)));
        }
        finally { Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.Embedded; }
    }

    [Fact]
    public async Task A_game_without_shader_stutter_is_compiled_when_asked_but_gets_no_driver_rebuild_or_new_shaders_notice()
    {
        var game = _game with { Id = "steam:4294960004" };
        try
        {
            Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.TryParse(Verdicts(Core.Games.GameVerdicts.NoStutterVerdict, "engine-precompiles", game.Id))!;
            var k = Killer(new FakeReader(Unreal), new FakePlanner(records: [new('B', [.. new byte[20], 9]), new('P', [1]), new('C', [3])]), game: game);
            await k.ScanAsync(default);
            Assert.True(k.Games.Single() is { Status: GameStatus.Ready, NoStutter: not null });
            Assert.Equal("Compiling isn't needed", Format.ShortNote(k.Games.Single() with { Status = GameStatus.NeedsRecording }));
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

            var after = Killer(new FakeReader(Unreal), driver: "101.00", game: game);
            await after.ScanAsync(default);
            var stale = Assert.Single(after.StaleGames());
            Assert.Empty(after.DriverStaleGames());
            Assert.False(after.ShouldNotifyStale());
            Assert.NotNull(NewShaders.Key(stale with { NewPipelines = 5, NoStutter = null }, new HashSet<string>()));
            Assert.Null(NewShaders.Key(stale with { NewPipelines = 5 }, new HashSet<string>()));
        }
        finally { Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.Embedded; }
    }

    [Fact]
    public async Task A_game_its_channels_signed_verdicts_mark_unsupported_loses_its_recorder_and_is_never_queued_and_signed_out_the_public_list_counts()
    {
        var game = FakeGame("epic:verdict-unsupported", "Blocked");
        var now = DateTimeOffset.UtcNow;
        var beta = SignedVerdicts(Verdicts(Core.Games.GameVerdicts.UnsupportedVerdict, "crashes", game.Id), now);
        string? auth = null;
        var fake = new CommunityTests.Fake(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v1/updates/beta/game-verdicts.json"))
            {
                auth = r.Headers.Authorization?.ToString();
                return CommunityTests.Ours(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(beta));
            }
            return CommunityTests.Ours(HttpStatusCode.NotFound);   // no public copy: leaving beta falls back to the embedded list
        });
        try
        {
            var k = RecordKiller([game]);
            k.ManageRecorders = true;
            k.ContentKeys = VerdictsKeys;
            await k.ScanAsync(default);
            Assert.True(k.Games.Single().RecorderInstalled);

            k.Settings = k.Settings with { UpdateChannel = UpdateChannels.Beta };
            k.ChannelAccess = () => Task.FromResult<(string?, IReadOnlyCollection<string>?)>(("token", [UpdateChannels.Beta]));
            k.ContentRoutes = new RouteFailover(fake, [new("https://api.test.com/")]);
            await k.ScanAsync(default);
            await k.StutterUpdate;
            await Until(() => !k.Games.Single().RecorderInstalled);
            Assert.Equal("Bearer token", auth);
            Assert.True(k.Games.Single() is { Status: GameStatus.Unsupported, StatusReason: "Compiling crashes this game or the driver", NotPlanned: true });
            Assert.Equal(ScsKiller.SkipUnsupported, k.Games.Single().RecorderSkip);
            k.Enqueue(game.Id);
            Assert.Empty(k.Queue);

            k.ChannelAccess = () => Task.FromResult<(string?, IReadOnlyCollection<string>?)>((null, null));   // signed out
            await k.ScanAsync(default, userRequested: true);
            await k.StutterUpdate;
            Assert.NotEqual(GameStatus.Unsupported, k.Games.Single().Status);
        }
        finally { Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.Embedded; }
    }

    [Fact]
    public async Task A_scan_sends_the_daily_check_once_and_registers_no_sharing_device()
    {
        var fake = new CommunityTests.Fake(_ => CommunityTests.Ours(HttpStatusCode.NoContent));
        var routes = new RouteFailover(fake, [new("https://api.test.com/")]);
        var k = Killer();
        await k.ScanAsync(default);
        await k.ActiveCheckSent;
        Assert.Empty(fake.Log);   // the command line sets none

        k.Settings = k.Settings with { ShareRecordings = true };
        k.Sharing = new Sharing(Path.Combine(_root, "data"), () => k.Settings.ShareRecordings, routes);
        k.ActiveCheck = new ActiveCheck(Path.Combine(_root, "data"), () => k.Settings.ActiveCheck, k.Vendor.Vendor, AppVersion.Parse("1.2.3"), routes);
        for (var i = 0; i < 2; i++)
        {
            await k.ScanAsync(default);
            await k.ActiveCheckSent;
            await k.SharingPass;
        }
        Assert.Equal(["POST https://api.test.com/v1/active"], fake.Log);   // nothing to upload: no device

        k.Settings = k.Settings with { ActiveCheck = false };
        File.Delete(Path.Combine(_root, "data", "active-check.txt"));
        await k.RescanAsync(default);
        await k.ActiveCheckSent;
        Assert.Single(fake.Log);

        // the app's wiring: nothing until the welcome dialog is closed, then at once
        k.ActiveCheck = new ActiveCheck(Path.Combine(_root, "data"), () => k.Settings is { ActiveCheck: true, WelcomeSeen: true }, k.Vendor.Vendor,
            AppVersion.Parse("1.2.3"), routes);
        k.Settings = k.Settings with { ActiveCheck = true, WelcomeSeen = false };
        await k.ScanAsync(default);
        await k.ActiveCheckSent;
        Assert.Single(fake.Log);
        k.Settings = k.Settings with { WelcomeSeen = true };
        await k.ActiveCheckSent;
        Assert.Equal(2, fake.Log.Count);
    }

    [Fact]
    public async Task A_recording_is_shared_once_its_build_is_indexed_with_the_store_build_key()
    {
        const string hash = "00112233445566778899aabbccddeeff00112233";
        var game = _game with { Version = "42" };
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording());   // the recorder's output
        var uploads = new List<string>();
        var fake = new CommunityTests.Fake(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/devices")
                return CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray());
            lock (uploads) uploads.Add(System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(r.Headers.GetValues("X-SCSK-Upload").Single())));
            return CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray());
        });
        var k = Killer(new FakeReader(Unreal, hash), game: game);
        k.Sharing = new Sharing(Path.Combine(_root, "data"), () => k.Settings.ShareRecordings,
            new RouteFailover(fake, [new("https://api.test.com/"), new("https://api.test.io/")]));

        await k.ScanAsync(default);   // imports the recording; sharing is off
        await k.SharingPass;
        k.Settings = k.Settings with { ShareRecordings = true };   // on: a pass, but no content hash of this build yet
        await k.SharingPass;
        Assert.Empty(fake.Log);

        k.Enqueue(game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        await k.SharingPass;   // after the compile's index
        Assert.Contains($"\"store_build_key\":\"{_game.Id}@42\"", Assert.Single(uploads));
        Assert.Contains($"\"content_hash\":\"{hash}\"", uploads[0]);
        Assert.NotNull(k.Games.Single().RecordingSharedAt);

        await k.ScanAsync(default);
        await k.SharingPass;
        Assert.Single(uploads);   // unchanged: not again
    }

    [Fact]
    public async Task A_recording_is_not_shared_while_a_blocking_shader_mod_is_in_the_install_root()
    {
        const string hash = "00112233445566778899aabbccddeeff00112233";
        var game = _game with { Version = "42" };
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording());
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath == "/v1/devices"
            ? CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray())
            : CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray()));
        int uploads() { lock (fake.Log) return fake.Log.Count(l => l.Contains("/v1/upload")); }
        var k = Killer(new FakeReader(Unreal, hash), game: game);
        k.Sharing = new Sharing(Path.Combine(_root, "data"), () => k.Settings.ShareRecordings,
            new RouteFailover(fake, [new("https://api.test.com/"), new("https://api.test.io/")]));
        await k.ScanAsync(default);
        k.Enqueue(game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));   // indexed, sharing off

        var addon = Path.Combine(_game.InstallDir, "renodx-ff7rebirth.addon64");
        File.WriteAllBytes(Path.Combine(_game.InstallDir, "dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(addon, RenoDxAddon);
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording(new string('b', 40)));
        k.Settings = k.Settings with { ShareRecordings = true };   // a pass at once: the game's state is still unblocked
        await k.SharingPass;
        Assert.Equal(0, uploads());
        await k.ScanAsync(default);   // imports the new recording, which starts a pass
        await k.SharingPass;
        Assert.Equal(0, uploads());

        File.Delete(addon);
        await k.ScanAsync(default);
        await k.SharingPass;
        Assert.Equal(1, uploads());
    }

    [Fact]
    public async Task A_recording_indexed_by_another_process_is_shared_when_the_game_exits()
    {
        const string hash = "00112233445566778899aabbccddeeff00112233";
        var game = _game with { Version = "42" };
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording());
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath == "/v1/devices"
            ? CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray())
            : CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray()));
        int uploads() { lock (fake.Log) return fake.Log.Count(l => l.Contains("/v1/upload")); }
        var k = Killer(new FakeReader(Unreal, hash), game: game);
        k.Settings = k.Settings with { ShareRecordings = true };
        k.Sharing = new Sharing(Path.Combine(_root, "data"), () => k.Settings.ShareRecordings,
            new RouteFailover(fake, [new("https://api.test.com/"), new("https://api.test.io/")]));
        await k.ScanAsync(default);   // imports the recording: no index of this build yet
        await k.SharingPass;
        Assert.Equal(0, uploads());

        var rec = k.Store.LoadGame(game.Id);   // the command line's compile indexes it; the recording stays the same
        (rec.IndexContentHash, rec.IndexGameVersion) = (hash, "42");
        k.Store.SaveGame(game.Id, rec);
        var (_, running, _) = Watched(k);
        running.Add("Fake-Win64-Shipping.exe");   // played: nothing new recorded
        k.PollGames();
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        await k.SharingPass;
        Assert.Equal(1, uploads());
    }

    Core.Games.ManualSource Manual() => new(new AppStore(Path.Combine(_root, "data")));

    [Fact]
    public async Task A_game_the_user_added_is_scanned_like_a_store_game_and_kept_in_scan_json()
    {
        var (_, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var k = Killer(new FakeReader(Unreal), sources: [new FakeSource([_game]), Manual()]);
        await k.ScanAsync(default);
        var added = k.AddManualGame(stub);
        Assert.False(added.Existed);
        Assert.Equal((Store.Manual, shipping, null), (added.Game.Store, added.Game.ExePath, added.Game.Version));
        await k.ScanAsync(default);
        var s = k.Games.Single(x => x.Game.Id == added.Game.Id);
        Assert.Equal(("Unreal", AntiCheat.None), (s.Engine?.Family, s.AntiCheat));
        Assert.Equal(AntiCheat.None, k.Store.LoadScan()[added.Game.Id].AntiCheat);
        Assert.True(k.AddManualGame(shipping).Existed);   // the same game picked by its Shipping exe

        var again = Killer(new FakeReader(Unreal), sources: [new FakeSource([_game]), Manual()]);   // the next start
        await again.ScanAsync(default);
        Assert.Equal(added.Game, again.Games.Single(x => x.Game.Store == Store.Manual).Game);
    }

    [Fact]
    public async Task An_exe_of_a_store_game_points_to_it_instead_of_adding_a_duplicate()
    {
        var stub = Path.Combine(_game.InstallDir, "Fake.exe");
        File.WriteAllBytes(stub, ManualGamesTests.Exe());
        var manual = Manual();
        var k = Killer(new FakeReader(Unreal), sources: [new FakeSource([_game]), manual]);
        await k.ScanAsync(default);
        var r = k.AddManualGame(stub);
        Assert.Equal((_game.Id, true), (r.Game.Id, r.Existed));
        Assert.Empty(manual.Entries());

        // added before the store's copy was installed: the store's game is listed, once
        manual.Add(new Core.Games.ManualEntry(_game.ExePath, _exeDir, "Fake"));
        await k.ScanAsync(default);
        Assert.Equal(_game.Id, k.Games.Single().Game.Id);
    }

    [Fact]
    public async Task A_game_the_user_added_with_anti_cheat_never_gets_the_recorder()
    {
        var (root, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Protected"));
        Directory.CreateDirectory(Path.Combine(root, "Game", "Content", "EasyAntiCheat"));
        var k = Killer(new FakeReader(Unreal), sources: [Manual()]);
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;   // and "record all games" is on by default
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.Contains(ScsKiller.SkipAntiCheat, Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(id)).Message);
        Assert.Equal(["Game-Win64-Shipping.exe"], Directory.GetFiles(Path.GetDirectoryName(shipping)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_game_the_user_added_never_gets_the_recorder_nor_any_recorder_state()
    {
        var (_, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var dir = Path.GetDirectoryName(shipping)!;
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());   // a mod the recorder would chain to
        var planner = new NeedsRecordingPlanner();
        var k = Killer(new FakeReader(Unreal), planner: planner, sources: [Manual()]);
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;   // and "record all games" is on by default
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Unsupported, "needs a recording, " + ScsKiller.ManualNoRecording, ScsKiller.SkipManual, false, false),
            (s.Status, s.StatusReason, s.RecorderSkip, s.RecorderInstalled, s.NotPlanned));   // no anti-cheat: "Not supported yet"
        Assert.Contains(ScsKiller.SkipManual, Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(id)).Message);
        File.WriteAllBytes(Path.Combine(dir, "d3d12.dll"), mod);
        k.SetRecordAlongsideMod(id, true);
        k.SetRecorderOverride(id, RecorderOverride.On);
        k.ReconcileRecorders();
        await k.ScanAsync(default);

        Assert.Equal(mod, File.ReadAllBytes(Path.Combine(dir, "d3d12.dll")));
        Assert.Equal(["d3d12.dll", "Game-Win64-Shipping.exe"], Directory.GetFiles(dir).Select(Path.GetFileName).Order());
        var rec = k.Store.LoadGame(id);
        Assert.Equal((null, null, false, null, null), (rec.RecorderExe, rec.RecorderInstallDir, rec.RecordAlongsideMod, rec.RecorderChained, rec.RecorderMoveFrom));
        Assert.True(rec.Recorder is null or RecorderOverride.Default);
        Assert.Empty(rec.RecorderFiles);
    }

    [Fact]
    public async Task Removing_a_game_the_user_added_forgets_it_and_touches_nothing_in_its_folder()
    {
        var (root, stub, _) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToList();
        var manual = Manual();
        var k = Killer(new FakeReader(Unreal), sources: [new FakeSource([_game]), manual]);
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        k.Enqueue(id);
        Assert.Throws<InvalidOperationException>(() => k.RemoveManualGame(_game.Id));   // a store's game stays

        k.RemoveManualGame(id);
        Assert.Equal(before, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        Assert.Equal(_game.Id, k.Games.Single().Game.Id);
        Assert.Empty(k.Queue);
        Assert.Empty(manual.Entries());
        await k.ScanAsync(default);
        Assert.Equal(_game.Id, k.Games.Single().Game.Id);

        Assert.False(k.AddManualGame(stub).Existed);   // added again: listed again
        await k.ScanAsync(default);
        Assert.Contains(k.Games, x => x.Game.Id == id);
    }

    [Fact]
    public async Task A_game_is_not_removed_while_another_process_compiles_it()
    {
        var (_, stub, _) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var manual = Manual();
        var k = Killer(new FakeReader(Unreal), sources: [manual]);
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        Directory.CreateDirectory(k.Store.GameDir(id));
        using (new FileStream(Path.Combine(k.Store.GameDir(id), "compile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains("compile", Assert.Throws<InvalidOperationException>(() => k.RemoveManualGame(id)).Message);
        Assert.Single(manual.Entries());
        Assert.Single(k.Games);
        k.RemoveManualGame(id);
        Assert.Empty(k.Games);
    }

    [Fact]
    public async Task A_scan_running_while_a_game_is_removed_doesnt_list_it_again()
    {
        var (_, stub, _) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var gate = new ManualResetEventSlim(true);
        using var detecting = new ManualResetEventSlim();
        var k = Killer(new GatedReader(Unreal, gate, _ => detecting.Set()), sources: [Manual()]);
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        gate.Reset();
        detecting.Reset();
        var scan = k.RescanAsync(default);   // re-detects: holds in the reader with the game read
        Assert.True(detecting.Wait(TimeSpan.FromSeconds(10)));
        k.RemoveManualGame(id);
        gate.Set();
        await scan;
        Assert.Empty(k.Games);
        Assert.Throws<ArgumentException>(() => k.RefreshGame(id));
    }

    [Fact]
    public async Task An_exe_inside_a_store_games_install_points_to_it_before_any_resolution()
    {
        var launcher = Path.Combine(_game.InstallDir, "Launcher.exe");
        File.WriteAllBytes(launcher, "no program SCSKiller could resolve"u8.ToArray());
        var k = Killer(new FakeReader(Unreal), sources: [new FakeSource([_game]), Manual()]);
        await k.ScanAsync(default);
        var r = k.AddManualGame(launcher);
        Assert.Equal((_game.Id, true), (r.Game.Id, r.Existed));
    }

    [Fact]
    public async Task A_game_the_user_added_with_an_hdr_mod_gets_the_same_block()
    {
        var (dir, stub, _) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));   // ReShade in the install root
        File.WriteAllBytes(Path.Combine(dir, "dxgi.dll"), ReShadeDll);
        var addon = Path.Combine(dir, "renodx-ff7rebirth.addon64");   // listed: changes every pipeline
        File.WriteAllBytes(addon, RenoDxAddon);
        var k = Killer(new FakeReader(Unreal), sources: [Manual()]);
        k.ProcessNames = () => new HashSet<string>();
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Unsupported, "RenoDX", true, ScsKiller.ShaderModReason("RenoDX", LayerBlock.NotBesideExe), ScsKiller.SkipShaderMod),
            (s.Status, s.ShaderMod, s.ShaderModBlocks, s.StatusReason, s.RecorderSkip));
        Assert.Equal("RenoDX changes all its pipelines", Format.ShortNote(s));

        k.Enqueue(id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Failed, "not ready: " + ScsKiller.ShaderModReason("RenoDX", LayerBlock.NotBesideExe)), (k.Queue.Single().Stage, k.Queue.Single().Error));
        Assert.Null(k.Games.Single().WarmedAt);

        File.Delete(addon);
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.Ready, ScsKiller.SkipManual), (k.Games.Single().Status, k.Games.Single().RecorderSkip));   // still never recorded
    }

    /// <summary>The recorder a development build installed next to a game the user added (no release ever did).</summary>
    void LegacyRecorder(ScsKiller k, string id, string exe, string install)
    {
        var dir = Path.GetDirectoryName(exe)!;
        File.Copy(_proxy, Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
        File.WriteAllText(Path.Combine(dir, "scskiller.log"), "recorded");
        var rec = k.Store.LoadGame(id);
        (rec.RecorderExe, rec.RecorderInstallDir, rec.Recorder) = (exe, install, RecorderOverride.On);
        foreach (var f in new[] { "d3d12.dll", "scskiller.ini" }) rec.RecorderFiles[f] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, f))));
        k.Store.SaveGame(id, rec);
    }

    [Fact]
    public async Task Removing_a_game_takes_out_a_recorder_a_development_build_left_and_waits_for_the_game_to_close()
    {
        var (root, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var manual = Manual();
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal), sources: [manual]);
        k.ProcessNames = () => running;
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        LegacyRecorder(k, id, shipping, root);
        var dir = Path.GetDirectoryName(shipping)!;

        running.Add("Game-Win64-Shipping");
        Assert.Contains("close the game first", Assert.Throws<InvalidOperationException>(() => k.RemoveManualGame(id)).Message);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(dir, "d3d12.dll")));
        Assert.Single(manual.Entries());

        running.Clear();
        k.RemoveManualGame(id);
        Assert.Equal(["Game-Win64-Shipping.exe"], Directory.GetFiles(dir).Select(Path.GetFileName));
        Assert.Empty(manual.Entries());
        var rec = k.Store.LoadGame(id);
        Assert.Equal((null, null), (rec.RecorderExe, rec.RecorderInstallDir));
        Assert.Empty(rec.RecorderFiles);
    }

    [Fact]
    public async Task Turning_the_recorder_off_takes_out_one_a_development_build_left_and_on_stays_refused()
    {
        var (root, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var k = Killer(new FakeReader(Unreal), sources: [Manual()]);
        k.ProcessNames = () => new HashSet<string>();
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        LegacyRecorder(k, id, shipping, root);
        var dir = Path.GetDirectoryName(shipping)!;
        k.RefreshGame(id);
        Assert.True(k.Games.Single().RecorderInstalled);

        k.UninstallRecorder(id);   // `record uninstall`: SetRecorderOverride(Off)
        Assert.Equal(["Game-Win64-Shipping.exe"], Directory.GetFiles(dir).Select(Path.GetFileName));
        Assert.False(k.Games.Single().RecorderInstalled);
        Assert.Contains(ScsKiller.SkipManual, Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(id)).Message);
        Assert.Equal(["Game-Win64-Shipping.exe"], Directory.GetFiles(dir).Select(Path.GetFileName));
        Assert.Empty(k.Store.LoadGame(id).RecorderFiles);
    }

    [Fact]
    public async Task A_slow_scan_finishing_after_a_game_was_added_and_listed_keeps_it_listed()
    {
        var (_, stub, _) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        using var gate = new ManualResetEventSlim(true);
        using var entered = new ManualResetEventSlim();
        var k = Killer(new HoldsFor(_game.Id, Unreal, gate, entered), sources: [new FakeSource([_game]), Manual()]);
        await k.ScanAsync(default);
        gate.Reset();
        var slow = k.RescanAsync(default);   // Shift+Refresh: discovers [store game], then holds in its detection
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);   // Add's own scan: the store game cached, the added game detected
        Assert.Contains(k.Games, s => s.Game.Id == id);

        gate.Set();
        await slow;
        Assert.Equal(new[] { _game.Id, id }.Order(), k.Games.Select(s => s.Game.Id).Order());
        await k.ScanAsync(default);
        Assert.Equal(2, k.Games.Count);
    }

    [Fact]
    public async Task A_scan_finishing_after_a_removal_doesnt_queue_the_removed_games_plan_check()
    {
        var (_, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var id = Core.Games.ManualSource.IdOf(shipping);
        using var gate = new ManualResetEventSlim(true);
        using var entered = new ManualResetEventSlim();
        var k = Killer(new HoldsFor(id, Unreal, gate, entered), sources: [Manual()]);
        Assert.Equal(id, k.AddManualGame(stub).Game.Id);
        await k.ScanAsync(default);
        k.Enqueue(id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        var rec = k.Store.LoadGame(id);
        (rec.PlanVersion, rec.WarmedPlanVersion) = (Planner.Version - 1, Planner.Version - 1);   // warmed by an older planner
        k.Store.SaveGame(id, rec);
        k.CheckPlans = true;
        k.Remove(id);

        gate.Reset();
        var slow = k.RescanAsync(default);   // reads the game, then holds in its detection
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        k.RemoveManualGame(id);
        gate.Set();
        await slow;   // would ask for the warmed game's plan check
        Assert.DoesNotContain(k.Queue, q => q.GameId == id);
        Assert.Empty(k.Games);
    }

    /// <summary>Detection of one game waits for <paramref name="gate"/>; <paramref name="entered"/> says it started.</summary>
    sealed class HoldsFor(string gameId, EngineInfo engine, ManualResetEventSlim gate, ManualResetEventSlim entered) : IEngineReader
    {
        public EngineInfo? Detect(Game game)
        {
            if (game.Id == gameId && !gate.IsSet) { entered.Set(); gate.Wait(TimeSpan.FromSeconds(10)); }
            return engine;
        }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary>A game added by hand with its folder confirmed, in a fresh killer whose data folder has it; scanned.</summary>
    async Task<(ScsKiller K, Game Game, string Dir)> Confirmed(string root, bool manage = false)
    {
        var (_, stub, shipping) = ManualGamesTests.UnrealLayout(root);
        var k = Killer(new FakeReader(Unreal), sources: [new FakeSource([_game]), Manual()]);
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = manage;
        await k.ScanAsync(default);
        var game = k.AddManualGame(stub, root).Game;
        Assert.Equal((shipping, root), (game.ExePath, game.InstallDir));
        await k.ScanAsync(default);
        return (k, game, Path.GetDirectoryName(shipping)!);
    }

    [Fact]
    public async Task A_game_added_with_its_folder_confirmed_is_recorded_and_armed_like_a_store_game()
    {
        var (k, game, dir) = await Confirmed(Path.Combine(_root, "Games", "Added"), manage: true);   // "record all games" installs it
        var s = k.Games.Single(x => x.Game.Id == game.Id);
        Assert.Equal((false, null), (s.RootUnconfirmed, s.RecorderSkip));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(dir, "d3d12.dll")));
        Assert.Contains("armed=1", File.ReadAllText(Path.Combine(dir, ScsKiller.ArmedFile)));
        Assert.True(File.Exists(ScsKiller.LedgerFile(game.ExePath)));
    }

    [Fact]
    public async Task A_game_saved_without_a_confirmed_folder_is_recorded_once_it_is_confirmed()
    {
        var (root, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added 113"));
        var dir = Path.GetDirectoryName(shipping)!;
        var k = Killer(new FakeReader(Unreal), sources: [Manual()]);
        k.ProcessNames = () => new HashSet<string>();
        var id = k.AddManualGame(stub).Game.Id;   // saved without a confirmed folder: a suggested one only
        await k.ScanAsync(default);
        Assert.Equal((true, ScsKiller.SkipManual), (k.Games.Single().RootUnconfirmed, k.Games.Single().RecorderSkip));
        Assert.Contains(ScsKiller.SkipManual, Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(id)).Message);
        Assert.False(File.Exists(Path.Combine(dir, "d3d12.dll")));

        Assert.True(k.AddManualGame(stub, root).Existed);   // the folder confirmed: the same game
        Assert.Single(Manual().Entries());
        Assert.Null(k.Games.Single().RecorderSkip);
        k.InstallRecorder(id);
        Assert.Contains("armed=1", File.ReadAllText(Path.Combine(dir, ScsKiller.ArmedFile)));
    }

    [Fact]
    public async Task Anti_cheat_in_a_sibling_game_doesnt_block_and_anti_cheat_inside_the_confirmed_folder_does()
    {
        var games = Path.Combine(_root, "Games");
        Directory.CreateDirectory(Path.Combine(games, "Other Game", "EasyAntiCheat"));   // another game beside it
        var (k, game, dir) = await Confirmed(Path.Combine(games, "Added"));
        Assert.Equal(AntiCheat.None, k.Games.Single(x => x.Game.Id == game.Id).AntiCheat);
        k.InstallRecorder(game.Id);
        Assert.Contains("armed=1", File.ReadAllText(Path.Combine(dir, ScsKiller.ArmedFile)));

        Directory.CreateDirectory(Path.Combine(game.InstallDir, "Game", "Content", "EasyAntiCheat"));   // deep inside the confirmed folder
        File.WriteAllBytes(Path.Combine(dir, "patch.dll"), [0x4D, 0x5A]);   // the update's files beside the exe
        await k.ScanAsync(default);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single(x => x.Game.Id == game.Id).AntiCheat);
        Assert.False(File.Exists(Path.Combine(dir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(dir, ScsKiller.ArmedFile)));
        Assert.False(File.Exists(ScsKiller.LedgerFile(game.ExePath)));
    }

    [Fact]
    public async Task Changing_the_confirmed_folder_disarms_at_once_and_a_refused_folder_changes_nothing()
    {
        var (k, game, dir) = await Confirmed(Path.Combine(_root, "Games", "Added"));
        k.InstallRecorder(game.Id);
        var armed = Path.Combine(dir, ScsKiller.ArmedFile);
        Assert.True(File.Exists(armed));

        Assert.Contains("whole drive", Assert.Throws<ArgumentException>(() => k.AddManualGame(game.ExePath, Path.GetPathRoot(_root)!)).Message);
        Assert.Equal(game.InstallDir, Manual().Entries().Single().InstallDir);
        Assert.True(File.Exists(armed));

        var project = Path.Combine(game.InstallDir, "Game");   // a narrower folder, the user's choice
        File.WriteAllBytes(Path.Combine(game.InstallDir, "Game", "Binaries", "Win64", "Game-Win64-Shipping-DX11.exe"), ManualGamesTests.Exe(padding: 90_000));   // a patch's bigger exe
        k.AddManualGame(game.ExePath, project);
        Assert.False(File.Exists(armed));
        Assert.False(File.Exists(ScsKiller.LedgerFile(game.ExePath)));
        var entry = Manual().Entries().Single();
        Assert.Equal((game.ExePath, project, true), (entry.Exe, entry.InstallDir, entry.Confirmed));   // the same game, its exe kept
        Assert.Equal(project, k.Games.Single(x => x.Game.Id == game.Id).Game.InstallDir);
        k.ReconcileRecorders(game.Id);   // kept: a full check of the new folder, armed again
        Assert.Contains("armed=1", File.ReadAllText(armed));
    }

    [Fact]
    public async Task A_confirmed_game_gets_the_hdr_mod_block_records_alongside_a_mod_and_loses_its_recorder_at_uninstall()
    {
        var (k, game, dir) = await Confirmed(Path.Combine(_root, "Games", "Added"));
        var dll = Path.Combine(dir, "d3d12.dll");
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(dll, mod);
        await k.ScanAsync(default);
        k.SetRecordAlongsideMod(game.Id, true);   // installs, alongside the mod
        Assert.True(ScsKiller.IsOurProxy(dll));
        Assert.Equal(mod, File.ReadAllBytes(Path.Combine(dir, ScsKiller.ChainName)));
        Assert.NotNull(k.Store.LoadGame(game.Id).RecorderChained);

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());   // SCSKiller's own uninstall
        Assert.Equal(mod, File.ReadAllBytes(dll));
        Assert.False(File.Exists(Path.Combine(dir, ScsKiller.ChainName)));
        Assert.False(File.Exists(Path.Combine(dir, ScsKiller.ArmedFile)));
        Assert.False(File.Exists(ScsKiller.LedgerFile(game.ExePath)));

        File.Delete(dll);
        await k.ScanAsync(default);
        k.InstallRecorder(game.Id);
        Assert.True(ScsKiller.IsOurProxy(dll));
        File.WriteAllBytes(Path.Combine(game.InstallDir, "dxgi.dll"), ReShadeDll);   // in the install root
        File.WriteAllBytes(Path.Combine(game.InstallDir, "renodx-ff7rebirth.addon64"), RenoDxAddon);   // listed: changes every pipeline
        await k.ScanAsync(default);
        var s = k.Games.Single(x => x.Game.Id == game.Id);
        Assert.Equal((GameStatus.Unsupported, ScsKiller.SkipShaderMod), (s.Status, s.RecorderSkip));
        Assert.False(File.Exists(dll));
        Assert.False(File.Exists(ScsKiller.LedgerFile(game.ExePath)));
    }

    [Fact]
    public async Task Previewing_an_exe_shows_the_resolved_exe_and_suggested_folder_and_saves_nothing()
    {
        var (root, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Games", "Added"));
        var k = Killer(new FakeReader(Unreal), sources: [new FakeSource([_game]), Manual()]);
        await k.ScanAsync(default);
        var p = k.PreviewManualGame(stub);
        Assert.Equal((false, shipping, root), (p.Existed, p.Game.ExePath, p.Game.InstallDir));
        Assert.Empty(Manual().Entries());
        Assert.Null(k.ManualFolderProblem(shipping, root));
        Assert.Contains(_game.Name, k.ManualFolderProblem(shipping, _root));   // holds a listed game
        Assert.Equal((_game.Id, true), (k.PreviewManualGame(Path.Combine(_game.InstallDir, "Fake.exe")) is var x ? (x.Game.Id, x.Existed) : default));   // a store's game
    }

    /// <summary>A scan's discovery reads the list with folder A and holds before publishing it; the user changes the folder
    /// to B, where anti-cheat is; then the discovery goes on. A is never armed, and the listed state is B's.</summary>
    [Fact]
    public async Task A_discovery_that_read_the_old_folder_doesnt_arm_it_after_the_folder_changed()
    {
        var root = Path.Combine(_root, "Games", "Added");
        var (_, stub, shipping) = ManualGamesTests.UnrealLayout(root);
        var a = Path.Combine(root, "Game");   // the project folder: clean
        Directory.CreateDirectory(Path.Combine(root, "Security", "EasyAntiCheat"));   // only in B, the whole game
        var manual = Manual();
        var k = Killer(new FakeReader(Unreal), sources: [manual]);
        k.ProcessNames = () => new HashSet<string>();
        var id = k.AddManualGame(stub, a).Game.Id;
        await k.ScanAsync(default);
        k.InstallRecorder(id);
        var dir = Path.GetDirectoryName(shipping)!;
        var armed = Path.Combine(dir, ScsKiller.ArmedFile);
        Assert.Contains("armed=1", File.ReadAllText(armed));

        k.ManageRecorders = true;   // as the app: the scan reconciles, and the reconcile arms what it keeps
        using var reading = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var once = 0;
        manual.Read = () => { if (Interlocked.Exchange(ref once, 1) == 0) { reading.Set(); go.Wait(TimeSpan.FromSeconds(10)); } };
        var scan = k.ScanAsync(default);   // its discovery reads folder A, then holds
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));
        var change = Task.Run(() => k.AddManualGame(shipping, root));   // folder B
        await Task.Delay(300);
        go.Set();
        await change;
        await scan;
        await k.CheckRecorderGames(true);

        var s = k.Games.Single();
        Assert.Equal((GameFiles.DirKey(root), AntiCheat.EasyAntiCheat), (s.Game.InstallDir, s.AntiCheat));
        Assert.False(manual.Confirmed(s.Game with { InstallDir = a }));
        Assert.False(File.Exists(armed));
        Assert.False(File.Exists(ScsKiller.LedgerFile(shipping)));
        Assert.False(File.Exists(Path.Combine(dir, "d3d12.dll")));
    }

    /// <summary>A watcher pass that starts during a folder change, after the old attestation is revoked and before the new
    /// folder is saved, still sees the old folder (clean) and would arm it: nothing is armed at any point of the change.</summary>
    [Fact]
    public async Task A_check_that_starts_during_a_folder_change_never_arms_the_old_folder()
    {
        var root = Path.Combine(_root, "Games", "Added");
        var (_, stub, shipping) = ManualGamesTests.UnrealLayout(root);
        var k = Killer(new FakeReader(Unreal), sources: [Manual()]);
        k.ProcessNames = () => new HashSet<string>();
        var id = k.AddManualGame(stub, Path.Combine(root, "Game")).Game.Id;
        await k.ScanAsync(default);
        k.InstallRecorder(id);
        var armed = Path.Combine(Path.GetDirectoryName(shipping)!, ScsKiller.ArmedFile);
        var ledger = ScsKiller.LedgerFile(shipping);
        Assert.True(File.Exists(armed) && File.Exists(ledger));

        var seen = new List<string>();
        using var stop = new CancellationTokenSource();
        Task? watch = null;
        var checkedDuring = false;
        k.FolderChanging = () =>
        {
            watch = Task.Run(async () =>   // from the revocation on, every millisecond until the change returns
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (var f in new[] { armed, ledger }) if (File.Exists(f)) lock (seen) seen.Add(f);
                    await Task.Delay(1);
                }
            });
            checkedDuring = k.CheckRecorderGames(true).Wait(TimeSpan.FromSeconds(20));   // the old folder is still the listed one
            foreach (var f in new[] { armed, ledger }) if (File.Exists(f)) lock (seen) seen.Add(f);
        };
        k.AddManualGame(shipping, root);
        stop.Cancel();
        await watch!;
        Assert.True(checkedDuring);
        Assert.Empty(seen);

        k.FolderChanging = null;
        await k.CheckRecorderGames(true);   // after the change: the new folder's check arms it
        Assert.Contains("armed=1", File.ReadAllText(armed));
    }

    /// <summary>A sharing pass that starts before the first scan has published the games (an evaluation's import queues
    /// one) still leaves out what any recording on this PC says a layer made: another game's 'W', read from disk.</summary>
    [Fact]
    public async Task Sharing_before_the_first_scan_leaves_out_what_another_recording_says_a_layer_made()
    {
        const string hash = "00112233445566778899aabbccddeeff00112233";
        var game = _game with { Version = "42" };
        var (layer, own) = (new string('a', 40), new string('b', 40));
        var data = Path.Combine(_root, "data");
        var store = new AppStore(data);
        Directory.CreateDirectory(store.GameDir(game.Id));
        File.WriteAllBytes(Path.Combine(store.GameDir(game.Id), "recording.db"), SharingTests.LocalRecording(layer, own));
        var records = PsoDb.Read(Path.Combine(store.GameDir(game.Id), "recording.db")).Where(r => r.Tag == 'S').ToList();
        var made = records.Single(r => PsoDb.Parse(r).Stages.ContainsValue(layer));
        var rec = store.LoadGame(game.Id);
        (rec.IndexContentHash, rec.IndexGameVersion) = (hash, "42");
        store.SaveGame(game.Id, rec);
        var other = store.GameDir("test:other");
        Directory.CreateDirectory(other);
        using (var f = File.Create(Path.Combine(other, "recording.db"))) PsoDb.Write(f, 'W', [.. Convert.FromHexString(made.Key), .. new byte[20]]);

        var bodies = new List<byte[]>();
        var fake = new CommunityTests.Fake(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/devices") return CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray());
            lock (bodies) bodies.Add(r.Content!.ReadAsByteArrayAsync().Result);
            return CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray());
        });
        var k = Killer(new FakeReader(Unreal, hash), game: game);
        k.Settings = k.Settings with { ShareRecordings = true };
        k.Sharing = new Sharing(data, () => k.Settings.ShareRecordings, new RouteFailover(fake, [new("https://api.test.com/"), new("https://api.test.io/")]));
        Assert.Empty(k.Games);
        k.StartSharing([game]);
        await k.SharingPass;
        var sent = Assert.Single(bodies);
        var keys = HashOnly.Decompress(sent).Where(r => r.Tag != 'B').Select(r => r.Key).ToList();
        Assert.Equal([records.Single(r => r != made).Key], keys);
    }

    /// <summary>A recorder inbox whose 'W' records can't all be read (a torn tail while the game still records, or a
    /// malformed 'W') holds every upload back, recordings and packs, without a problem; once it reads whole the pass shares,
    /// leaving out what it names.</summary>
    [Theory]
    [InlineData("torn")]
    [InlineData("malformed")]
    public async Task A_torn_or_malformed_inbox_holds_sharing_back_until_it_reads_whole(string kind)
    {
        const string hash = "00112233445566778899aabbccddeeff00112233";
        var game = _game with { Version = "42" };
        var data = Path.Combine(_root, "data");
        var store = new AppStore(data);
        Directory.CreateDirectory(store.GameDir(game.Id));
        File.WriteAllBytes(Path.Combine(store.GameDir(game.Id), "recording.db"), SharingTests.LocalRecording(new string('a', 40), new string('b', 40)));
        var records = PsoDb.Read(Path.Combine(store.GameDir(game.Id), "recording.db")).Where(r => r.Tag == 'S').ToList();
        var rec = store.LoadGame(game.Id);
        (rec.IndexContentHash, rec.IndexGameVersion) = (hash, "42");
        store.SaveGame(game.Id, rec);
        var otherExe = Path.Combine(_root, "other-game", "Other.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(otherExe)!);
        var other = store.LoadGame("test:other");
        other.RecorderExe = otherExe;
        store.SaveGame("test:other", other);
        var inbox = Path.Combine(Path.GetDirectoryName(otherExe)!, "scskiller.db");
        byte[] W(byte[] payload) => [(byte)'W', .. BitConverter.GetBytes(payload.Length), .. payload];
        var whole = W([.. Convert.FromHexString(records[0].Key), .. new byte[20]]);
        File.WriteAllBytes(inbox, kind == "torn" ? whole[..30] : [.. whole, .. W(new byte[39])]);

        var bodies = new List<byte[]>();
        var fake = new CommunityTests.Fake(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/devices") return CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray());
            lock (bodies) bodies.Add(r.Content!.ReadAsByteArrayAsync().Result);
            return CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray());
        });
        var k = Killer(new FakeReader(Unreal, hash), game: game);
        var log = new List<string>();
        k.Log = new Progress<string>(l => { lock (log) log.Add(l); });
        k.Settings = k.Settings with { ShareRecordings = true };
        k.Sharing = new Sharing(data, () => k.Settings.ShareRecordings, new RouteFailover(fake, [new("https://api.test.com/"), new("https://api.test.io/")]));
        k.StartSharing([game]);
        await k.SharingPass;
        Assert.Empty(bodies);
        Assert.Null(k.Sharing.Problem);
        Assert.Null(Sharing.Shared(store.GameDir(game.Id)));   // not marked: the next pass tries again

        File.WriteAllBytes(inbox, whole);   // the game closed: the recorder's last write is whole
        k.StartSharing([game]);
        await k.SharingPass;
        var sent = Assert.Single(bodies);
        Assert.Equal([records[1].Key], HashOnly.Decompress(sent).Where(r => r.Tag != 'B').Select(r => r.Key));
    }

    sealed class FakeSource(Game[] games, Store store = Store.Other) : IGameSource
    {
        public Game[] Games { get; set; } = games;
        public Store Store => store;
        public IReadOnlyList<Game> Discover() => Games;
    }

    sealed class FakeVendor(GpuInfo gpu, bool perStage = false, string profile = "fake-1", bool packageKeyed = false) : IGpuVendorBackend, IRefreshableGpu
    {
        public GpuVendor Vendor => Gpu.Vendor;
        public GpuInfo Gpu { get; private set; } = gpu;
        public bool Incomplete;   // refreshes read a partial version
        public Func<GpuInfo, string>? Label;   // the version refreshes read; null = DXGI's
        public int Refreshes;
        public bool Refresh(GpuInfo adapter)
        {
            Refreshes++;
            Gpu = adapter with { DriverVersion = Incomplete ? "partial" : Label?.Invoke(adapter) ?? adapter.DriverVersion };
            return !Incomplete;
        }
        public string FallbackVersion(string umd) => $"fallback {umd}";
        public VendorCaps Caps => new(profile, true, true, false, perStage, PackageKeyed: packageKeyed);
        public CacheUsage GetCacheUsage() => new("", 0, true);
        public CacheLimit? GetCacheLimit() => null;
        public void SetCacheLimit(CacheLimit limit) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_hash_only_recording_is_rehydrated_into_the_work_folder_before_materializing()
    {
        var vs = "vertex shader bytes"u8.ToArray();
        var vsSha = Convert.ToHexStringLower(SHA1.HashData(vs));
        var planner = new FakePlanner();
        var k = Killer(new BlobReader(new EngineInfo("Unreal", "4.26", null, "D3D12", false, null), new() { [vsSha] = vs }), planner);
        await k.ScanAsync(default);
        var recording = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Directory.CreateDirectory(Path.GetDirectoryName(recording)!);
        using (var f = File.Create(recording)) // what a shared plan carries: the PSOs, no shader bytes
            PsoDb.Write(f, 'S', PsoDb.Stream(PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Vertex] = vsSha }, [], 3, [], 0));

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal(Path.Combine(k.Store.GameDir(_game.Id), "work", "recording.db"), planner.MaterializedWith?.DbPath);
        Assert.Equal(0, planner.UnresolvedAtMaterialize);
        Assert.Equal([vsSha], SCSKiller.Tests.Planning.RehydrateTests.Unresolved(recording)); // the imported recording stays as it was
    }

    [Fact]
    public async Task A_community_recording_is_downloaded_after_the_scan_and_feeds_the_planner_like_a_local_one()
    {
        var vs = "vertex shader bytes"u8.ToArray();
        var vsSha = Convert.ToHexStringLower(SHA1.HashData(vs));
        var raw = CommunityTests.HashOnly(vsSha);   // synthetic: one root signature, one pipeline
        var obj = CommunityTests.Brotli(raw);
        const string content = "0123456789abcdef0123456789abcdef01234567";
        var game = _game with { Version = "100" };
        var manifest = CommunityTests.Manifest(CommunityTests.Alias($"{game.Id}@100", content), CommunityTests.Entry(content, obj, 1));
        var fake = new CommunityTests.Fake(r => CommunityTests.Ours(HttpStatusCode.OK, r.RequestUri!.AbsolutePath.StartsWith("/v1/o/") ? obj : manifest));
        var planner = new NeedsRecordingPlanner();
        var k = KillerWith(planner);   // on by default

        Assert.Equal(GameStatus.NeedsRecording, (await k.ScanAsync(default)).Single().Status);   // the scan itself stays local
        await k.CommunitySync;
        var s = k.Games.Single();
        Assert.Equal(GameStatus.Ready, s.Status);
        Assert.Equal(new CommunityInfo(1, s.Community!.DownloadedAt, WithLocalRecording: false), s.Community);

        k.Enqueue(game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        var vsBlob = new PsoDb.Rec('B', [.. SHA1.HashData(vs), .. vs]).Key;   // rehydrated from the install before planning
        Assert.Equal(PsoDb.Read(new MemoryStream(raw)).Select(r => r.Key).Append(vsBlob).Order(), planner.BuiltWith!.Order());
        Assert.Equal(0, planner.Inner.UnresolvedAtMaterialize);
        Assert.False(File.Exists(Path.Combine(k.Store.GameDir(game.Id), "recording.all.db")));   // merged in the work folder only
        Assert.Equal(1, fake.Log.Count(l => l.Contains("/v1/o/")));   // downloaded once

        k.Store.SaveSettings(k.Settings with { UseCommunityDb = false });   // off: what was downloaded isn't used either
        Assert.Equal(GameStatus.NeedsRecording, (await KillerWith(planner).ScanAsync(default)).Single().Status);

        ScsKiller KillerWith(IPlanner p)
        {
            var killer = Killer(new BlobReader(Unreal, new() { [vsSha] = vs }), p, game: game);
            killer.Community = new Community(killer.Store.DataDir, (_, _) => Task.FromResult<string?>("token"), new RouteFailover(fake, [new("https://api.test.com/")]));
            return killer;
        }
    }

    [Fact]
    public async Task A_community_recording_for_a_compiled_game_asks_for_a_compile_of_what_it_adds()
    {
        var raw = CommunityTests.HashOnly(new string('a', 40));
        var obj = CommunityTests.Brotli(raw);
        const string content = "0123456789abcdef0123456789abcdef01234567";
        var game = _game with { Version = "100" };
        var manifest = CommunityTests.Manifest(CommunityTests.Alias($"{game.Id}@100", content), CommunityTests.Entry(content, obj, 1));
        var fake = new CommunityTests.Fake(r => CommunityTests.Ours(HttpStatusCode.OK, r.RequestUri!.AbsolutePath.StartsWith("/v1/o/") ? obj : manifest));
        var k = await Warmed(game);
        k.Community = new Community(k.Store.DataDir, (_, _) => Task.FromResult<string?>("token"), new RouteFailover(fake, [new("https://api.test.com/")]));

        k.StartCommunitySync();
        await k.CommunitySync;
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, "1 new pipeline recorded; compile again to include them", 1L), (s.Status, s.StatusReason, s.RecordedSinceWarm));
        Assert.Empty(k.DriverStaleGames());
        Assert.NotNull(NewShaders.Key(s, new HashSet<string>()));

        using (var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append))   // then played: recorded here too
            foreach (var r in PsoDb.Read(new MemoryStream(raw))) PsoDb.Write(f, r.Tag, r.Payload);
        k.RefreshGame(game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
    }

    [Fact]
    public async Task Shared_upscaler_packs_download_for_everyone_signed_out_and_only_for_a_dll_this_pc_has()
    {
        var shader = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "ffx a");
        var dll = SCSKiller.Tests.Planning.MiddlewarePackTests.Pe("amd_fidelityfx_dx12.dll", shader);
        var rs = CommunityTests.RootSignature();
        var obj = HashOnly.Compress(HashOnly.Canonical([new PsoDb.Rec('B', [.. SHA1.HashData(rs), .. rs]),
            new PsoDb.Rec('C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1(shader)))], local: false, out _));
        var key = HashOnly.PackKey("nvidia", "amd", "amd_fidelityfx_dx12.dll", CommunityTests.Sha1(dll));
        var manifest = CommunityTests.Manifest(SCSKiller.Tests.Planning.SharedPackTests.Pack(Convert.FromHexString(HashOnly.PackHash(key)), obj, 1));
        var fake = new CommunityTests.Fake(r => CommunityTests.Ours(HttpStatusCode.OK, r.RequestUri!.AbsolutePath.StartsWith("/v1/p/") ? obj : manifest));
        var data = Path.Combine(_root, "data");
        var planner = new Planner(Path.Combine(data, "packs"), ScsKiller.SharedPackDir(data, GpuVendor.Nvidia));
        var k = Killer(new FakeReader(Unreal), planner, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        k.Settings = k.Settings with { UseCommunityDb = false };   // the setting is about the recordings only
        await k.SettingsRefresh;
        k.Community = new Community(data, (_, _) => Task.FromResult<string?>(null), new RouteFailover(fake, [new("https://api.test.com/")]));   // signed out

        // no upscaler DLL next to the exe: nothing is asked at all
        await k.ScanAsync(default);
        await k.CommunitySync;
        Assert.Empty(fake.Log);

        // a version the manifest has no pack for: the list only
        File.WriteAllBytes(Path.Combine(_exeDir, "amd_fidelityfx_dx12.dll"), SCSKiller.Tests.Planning.MiddlewarePackTests.Pe("amd_fidelityfx_dx12.dll"));
        await k.ScanAsync(default);
        await k.CommunitySync;
        Assert.Equal(["GET https://api.test.com/v1/manifest/0"], fake.Log);

        // the version it has: downloaded without a token, by its object id alone, and the game shows its pipelines
        File.WriteAllBytes(Path.Combine(_exeDir, "amd_fidelityfx_dx12.dll"), dll);
        await k.ScanAsync(default);
        await k.CommunitySync;
        Assert.Equal(["GET https://api.test.com/v1/manifest/0", $"GET https://api.test.com/v1/p/{Convert.ToHexStringLower(SHA256.HashData(obj))}"], fake.Log);
        Assert.Equal(("FidelityFX", 1), (k.Games.Single().Middleware!.Single().Label, k.Games.Single().Middleware!.Single().Pipelines));
        Assert.True(File.Exists(planner.SharedPacks!.PathOf("amd", "amd_fidelityfx_dx12.dll", CommunityTests.Sha1(dll))));

        // once is enough
        k.StartCommunitySync();
        await k.CommunitySync;
        Assert.Equal(2, fake.Log.Count);
    }

    [Fact]
    public async Task A_warmed_game_counts_each_pipeline_its_shared_packs_add_once()
    {
        var (a, b) = (SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "ffx a"), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "ffx b"));
        var ffx = SCSKiller.Tests.Planning.MiddlewarePackTests.Pe("amd_fidelityfx_dx12.dll", a, b);
        var upscaler = SCSKiller.Tests.Planning.MiddlewarePackTests.Pe("amd_fidelityfx_upscaler_dx12.dll", a);   // holds the same kernel
        File.WriteAllBytes(Path.Combine(_exeDir, "amd_fidelityfx_dx12.dll"), ffx);
        File.WriteAllBytes(Path.Combine(_exeDir, "amd_fidelityfx_upscaler_dx12.dll"), upscaler);
        var rs = CommunityTests.RootSignature();
        byte[] Pack(params byte[][] shaders) => HashOnly.Compress(HashOnly.Canonical([new PsoDb.Rec('B', [.. SHA1.HashData(rs), .. rs]),
            .. shaders.Select(s => new PsoDb.Rec('C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1(s))))], local: false, out _));
        byte[] Listed(string dll, byte[] bytes, byte[] obj) => SCSKiller.Tests.Planning.SharedPackTests.Pack(
            Convert.FromHexString(HashOnly.PackHash(HashOnly.PackKey("nvidia", "amd", dll, CommunityTests.Sha1(bytes)))), obj, 1);
        var (ffxA, ffxAB, up) = (Pack(a), Pack(a, b), Pack(a));
        var objects = new[] { ffxA, ffxAB, up }.DistinctBy(o => Convert.ToHexStringLower(SHA256.HashData(o))).ToDictionary(o => Convert.ToHexStringLower(SHA256.HashData(o)));
        var manifest = CommunityTests.Manifest(Listed("amd_fidelityfx_dx12.dll", ffx, ffxA));
        var fake = new CommunityTests.Fake(r => CommunityTests.Ours(HttpStatusCode.OK,
            r.RequestUri!.AbsolutePath.StartsWith("/v1/p/") ? objects[r.RequestUri.AbsolutePath[6..]] : manifest));
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var data = Path.Combine(_root, "data");
        var planner = new Planner(Path.Combine(data, "packs"), ScsKiller.SharedPackDir(data, GpuVendor.Nvidia));
        var k = Killer(new FakeReader(Unreal), planner, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        k.Settings = k.Settings with { UseCommunityDb = false };
        await k.SettingsRefresh;
        k.Community = new Community(data, (_, _) => Task.FromResult<string?>(null), new RouteFailover(fake, [new("https://api.test.com/")], clock), clock);
        var rec = k.Store.LoadGame(_game.Id);   // compiled before the packs came
        (rec.WarmedAt, rec.Plan) = (DateTimeOffset.Now, new Plan(_game.Id, "content-1", "PCD3D_SM6", "fake-1", new PlanStats(0, 0, 0, 0, false), Path.Combine(_root, "none.bin")));
        k.Store.SaveGame(_game.Id, rec);
        rec = k.Store.LoadGame(_game.Id);
        rec.WarmKeysFile = KeyFiles.Write(k.Store.GameDir(_game.Id), "warm", []);   // its warm replayed none of them
        k.Store.SaveGame(_game.Id, rec);

        await k.ScanAsync(default);
        await k.CommunitySync;
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);

        async Task Publish(byte[] record)
        {
            manifest = [.. manifest, .. record];
            clock.Now += TimeSpan.FromHours(5);
            k.StartCommunitySync();
            await k.CommunitySync;
        }
        // the other DLL's pack, published later, holds the same A: still one pipeline
        await Publish(Listed("amd_fidelityfx_upscaler_dx12.dll", upscaler, up));
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
        // the first pack grows by B: one more, not A again
        await Publish(Listed("amd_fidelityfx_dx12.dll", ffx, ffxAB));
        Assert.Equal(2, k.Games.Single().RecordedSinceWarm);
        Assert.Equal(6, fake.Log.Count);   // each pass: the list, then the one new pack
    }

    sealed class IndexReader(EngineInfo engine, ShaderIndex index) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => engine;
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => index;
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary>Runs <paramref name="act"/> once, at the first line containing <paramref name="at"/>.</summary>
    sealed class At(string at, Action act) : IProgress<string>
    {
        bool done;
        public void Report(string value) { if (!done && value.Contains(at)) { done = true; act(); } }
    }

    [Fact]
    public async Task A_shared_pack_replaced_while_a_plan_builds_from_it_still_re_plans_next_time()
    {
        var (a, b) = (SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "ffx a"), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "ffx b"));
        File.WriteAllBytes(Path.Combine(_exeDir, "amd_fidelityfx_dx12.dll"), SCSKiller.Tests.Planning.MiddlewarePackTests.Pe("amd_fidelityfx_dx12.dll", a, b));
        var data = Path.Combine(_root, "data");
        var planner = new Planner(Path.Combine(data, "packs"), ScsKiller.SharedPackDir(data, GpuVendor.Nvidia));
        var dll = Middleware.Detect(_exeDir).Single();
        var image = Middleware.Scan(dll.Path);
        var path = planner.SharedPacks!.PathOf(dll.Vendor, dll.Name, image.ContentHash);
        void Shared(byte[] shader, char obj) => MiddlewarePacks.FromShared([new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(shader)))],
            dll, image, amd: false, new string(obj, 64), out _).Write(path);
        Shared(a, '1');
        var k = Killer(new IndexReader(Unreal, SCSKiller.Tests.Planning.MiddlewarePackTests.Index()), planner, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        k.Log = new At("shared pack PSOs", () => Shared(b, '2'));   // a background download lands while the plan seeds from A
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal('2', MiddlewarePack.ReadHeader(path)!.Object![0]);

        var stamped = k.Store.LoadGame(_game.Id).PlanMiddleware;
        Assert.EndsWith(":" + new string('1', 64), stamped);   // what the plan was built from
        Assert.NotEqual(planner.PackFingerprint(k.Games.Single().Game), stamped);   // so the next compile plans again, with B
    }

    /// <summary>A game next to an FFX DLL with shaders a and b, on NVIDIA with this PC's and the shared packs, served
    /// <paramref name="packs"/> (the manifest lists each as the DLL's pack, the last one winning).</summary>
    (ScsKiller K, Planner Planner, byte[] A, byte[] B, Action<byte[]> Publish) PackGame(CommunityTests.Clock clock, Action? beforePack = null, IWarmer? warmer = null,
        byte[]? alsoInDll = null, EngineInfo? engine = null, string profile = "fake-1")
    {
        var (a, b) = (SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "ffx a"), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "ffx b"));
        var dll = SCSKiller.Tests.Planning.MiddlewarePackTests.Pe("amd_fidelityfx_dx12.dll", [a, b, .. alsoInDll is null ? [] : new[] { alsoInDll }]);
        File.WriteAllBytes(Path.Combine(_exeDir, "amd_fidelityfx_dx12.dll"), dll);
        var manifest = CommunityTests.Manifest();
        var objects = new Dictionary<string, byte[]>();
        var fake = new CommunityTests.Fake(r =>
        {
            if (!r.RequestUri!.AbsolutePath.StartsWith("/v1/p/")) return CommunityTests.Ours(HttpStatusCode.OK, manifest);
            beforePack?.Invoke();
            return CommunityTests.Ours(HttpStatusCode.OK, objects[r.RequestUri.AbsolutePath[6..]]);
        });
        var data = Path.Combine(_root, "data");
        var planner = new Planner(Path.Combine(data, "packs"), ScsKiller.SharedPackDir(data, GpuVendor.Nvidia));
        var index = SCSKiller.Tests.Planning.MiddlewarePackTests.Index() with { ContentHash = new string('c', 40) };   // a build whose shader list is kept
        var k = Killer(new IndexReader(engine ?? Unreal, index), planner, warmer, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }, profile: profile));
        k.Community = new Community(data, (_, _) => Task.FromResult<string?>(null), new RouteFailover(fake, [new("https://api.test.com/")], clock), clock);
        void Publish(byte[] shader)
        {
            var obj = HashOnly.Compress(HashOnly.Canonical([new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(shader)))], local: false, out _));
            objects[Convert.ToHexStringLower(SHA256.HashData(obj))] = obj;
            manifest = [.. manifest, .. SCSKiller.Tests.Planning.SharedPackTests.Pack(
                Convert.FromHexString(HashOnly.PackHash(HashOnly.PackKey("nvidia", "amd", "amd_fidelityfx_dx12.dll", CommunityTests.Sha1(dll)))), obj, 1)];
            clock.Now += TimeSpan.FromHours(5);
        }
        return (k, planner, a, b, Publish);
    }

    void Record(params byte[][] shaders)
    {
        using var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append);
        foreach (var s in shaders) PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(s)));
    }

    [Fact]
    public async Task A_recording_of_what_a_pack_brought_or_the_warm_compiled_adds_nothing_to_compile()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, a, b, publish) = PackGame(clock);
        publish(a);
        await k.ScanAsync(default);
        await k.CommunitySync;
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);   // A compiled from the shared pack, as an 'M' entry

        Record(a);   // played: the recorder saw A
        k.RefreshGame(_game.Id);
        Assert.Equal(0, k.Games.Single().RecordedSinceWarm);

        publish(b);   // the pack grows by B: counted once when it comes...
        k.StartCommunitySync();
        await k.CommunitySync;
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
        Record(b);    // ...and not again when it's recorded
        k.RefreshGame(_game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
    }

    [Fact]
    public async Task A_recording_imported_while_a_pack_downloads_is_counted_once()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim(true);
        var (k, _, a, b, publish) = PackGame(clock, () => { reached.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
        await k.ScanAsync(default);
        await k.CommunitySync;
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        publish(a);
        release.Reset();
        k.StartCommunitySync();
        Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));   // the pack with A is on its way
        Record(b);                                             // meanwhile the game was played: B recorded
        k.RefreshGame(_game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
        release.Set();
        await k.CommunitySync;
        Assert.Equal(2, k.Games.Single().RecordedSinceWarm);   // A and B, each once
    }

    [Fact]
    public async Task A_pack_arriving_during_a_warm_stays_to_compile()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        Action? duringWarm = null;
        var warmer = new FakeWarmer(() => { duringWarm?.Invoke(); duringWarm = null; return null; });
        var (k, planner, a, _, publish) = PackGame(clock, warmer: warmer);
        await k.ScanAsync(default);
        await k.CommunitySync;
        duringWarm = () =>
        {
            publish(a);
            k.StartCommunitySync();
            Assert.True(k.CommunitySync.Wait(TimeSpan.FromSeconds(10)));
        };
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(planner.SharedPacks!.PathOf("amd", "amd_fidelityfx_dx12.dll", CommunityTests.Sha1(File.ReadAllBytes(Path.Combine(_exeDir, "amd_fidelityfx_dx12.dll"))))));
        var s = k.Games.Single();   // the warm compiled the plan made before A came: A is still to compile
        Assert.Equal((GameStatus.Stale, "1 new upscaler pipeline; compile again to include them", 1L), (s.Status, s.StatusReason, s.RecordedSinceWarm));   // a pack's: not recorded
    }

    [Fact]
    public async Task A_plan_rebuild_never_counts_what_a_download_brought_again()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, a, _, publish) = PackGame(clock);
        await k.ScanAsync(default);
        await k.CommunitySync;
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        publish(a);
        k.StartCommunitySync();
        await k.CommunitySync;
        Assert.Equal(1, NewShaders.Count(k.Games.Single()));

        // a newer planner's idle plan rebuild seeds A into the plan: still one pipeline to compile, the pack's, counted once
        OlderPlanner(k);
        k.IdleTime = () => TimeSpan.FromHours(1);   // nobody at the PC: the plan check runs
        k.CheckPlans = true;
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var s = k.Games.Single();
        Assert.True(k.Queue.Single().PlanCheck);
        Assert.Equal((0L, 1L, 1L), (s.NewPipelines, s.RecordedSinceWarm, NewShaders.Count(s)));
    }

    [Fact]
    public async Task A_pipeline_imported_while_its_plan_builds_is_replayed_by_the_next_warm_and_not_left_pending()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, _, b, _) = PackGame(clock);
        await k.ScanAsync(default);
        await k.CommunitySync;
        // the recording was prepared for this plan; B is imported before the plan is stamped built, so the plan stays current
        k.Log = new At("plan: ", () => { Record(b); k.RefreshGame(_game.Id); });
        await Compile(k);
        k.Log = null;
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);   // the warm compiled the recording as prepared, without B
        await Compile(k);   // the same plan, with the recording as it is now
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));
    }

    [Fact]
    public async Task A_pack_pipeline_this_gpu_doesnt_run_isnt_left_pending()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var w64 = SCSKiller.Tests.Planning.SharedPackTests.Wave64("ffx wave64");
        var (k, planner, a, _, _) = PackGame(clock, alsoInDll: w64);
        var dll = Middleware.Detect(_exeDir).Single();
        var image = Middleware.Scan(dll.Path);
        var pack = new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size, gpu: "nvidia");
        foreach (var shader in new[] { a, w64 }) pack.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(shader))), "test:other");   // as if another GPU's pipeline got in
        pack.Write(planner.Packs!.PathOf(dll.Vendor, dll.Name, image.ContentHash));
        await k.ScanAsync(default);
        await Compile(k);   // NVIDIA: A compiles, the wave64 one never can
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, 0L), (s.Status, s.RecordedSinceWarm));
        Assert.Equal(1, s.Middleware!.Single().Pipelines);
    }

    [Fact]
    public async Task A_recorded_pipeline_a_rebuilt_plan_holds_stops_counting_once_the_recording_is_cleared()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, _, _, _) = PackGame(clock);
        await k.ScanAsync(default);
        await Compile(k);
        var shader = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "built at run time");   // in no file: recorded with its bytes, never in a pack
        using (var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append))
        {
            PsoDb.WriteBlob(f, CommunityTests.Sha1(shader), shader);
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(shader)));
        }
        k.RefreshGame(_game.Id);
        OlderPlanner(k);
        k.IdleTime = () => TimeSpan.FromHours(1);
        k.CheckPlans = true;
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(k.Queue.Single().PlanCheck);   // the rebuilt plan replays it from the recording
        Assert.Equal(1, NewShaders.Count(k.Games.Single()));

        Assert.True(k.ClearRecording(_game.Id));
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((0L, 0L, 0L), (s.NewPipelines ?? 0, s.RecordedSinceWarm, NewShaders.Count(s)));
    }

    [Fact]
    public async Task A_crash_between_a_key_file_and_the_state_that_names_it_changes_nothing()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, a, b, _) = PackGame(clock);
        await k.ScanAsync(default);
        await Compile(k);
        Record(b);
        k.RefreshGame(_game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
        var dir = k.Store.GameDir(_game.Id);
        // a warm that wrote its key file (B replayed) but not the state naming it, then a plan key file the same way
        var stray = KeyFiles.Write(dir, "warm", [.. new[] { a, b }.Select(x => new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(x))).Key)]);
        var strayPlan = KeyFiles.Write(dir, "plan", [new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(a))).Key]);
        foreach (var f in new[] { stray, strayPlan }) File.SetLastWriteTimeUtc(Path.Combine(dir, f), DateTime.UtcNow.AddHours(-2));   // the crash was a while ago
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, 1L, 1L), (s.Status, s.RecordedSinceWarm, NewShaders.Count(s)));   // as the state says: B still to compile
        await Compile(k);   // the next complete warm names its own file, and the strays go
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));
        var saved = k.Store.LoadGame(_game.Id);
        Assert.True(File.Exists(Path.Combine(dir, saved.WarmKeysFile!)) && File.Exists(Path.Combine(dir, saved.PlanKeysFile!)));
        Assert.False(File.Exists(Path.Combine(dir, stray)) || File.Exists(Path.Combine(dir, strayPlan)));
    }

    [Fact]
    public async Task A_complete_compile_puts_what_it_created_next_to_the_recorder()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, a, _, publish) = PackGame(clock);
        publish(a);
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await Compile(k);   // its closing refresh publishes it with the keys file
        var kept = File.ReadAllBytes(Path.Combine(k.Store.GameDir(_game.Id), Recordings.WarmedFile));
        Assert.Equal("SCSKWRM1"u8.ToArray(), kept[..8]);
        Assert.Equal([new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(a))).Key], kept[12..(12 + 20 * BitConverter.ToInt32(kept, 8))].Chunk(20).Select(Convert.ToHexStringLower));
        Assert.Equal(kept, File.ReadAllBytes(Path.Combine(_exeDir, Recordings.WarmedFile)));
        Assert.False(k.Store.LoadGame(_game.Id).KeysPending);
        k.UninstallRecorder(_game.Id);
        Assert.False(File.Exists(Path.Combine(_exeDir, Recordings.WarmedFile)));
    }

    void RecordBuiltAtRunTime(string seed)
    {
        var shader = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", seed);   // in no file: recorded with its bytes, never in a pack
        using var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append);
        PsoDb.WriteBlob(f, CommunityTests.Sha1(shader), shader);
        PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(shader)));
    }

    [Fact]
    public async Task A_pipeline_this_install_cant_replay_isnt_pending_until_its_shader_is_recorded_here()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, _, _, _) = PackGame(clock);
        await k.ScanAsync(default);
        await Compile(k);
        // a community recording's compute PSO whose shader was built at run time elsewhere: no bytes here
        var shader = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "someone else's runtime shader");
        var pso = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(shader)));
        var dir = k.Store.GameDir(_game.Id);
        using (var f = File.Create(Path.Combine(dir, "community.db")))
        {
            PsoDb.Write(f, pso.Tag, pso.Payload);
            PsoDb.Write(f, 'L', Convert.FromHexString(pso.Key));
        }
        File.WriteAllBytes(Path.Combine(dir, "community.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new CommunityDownload(new string('d', 64), new string('c', 40), 1, DateTimeOffset.UtcNow)));
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));   // the warm would skip it
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));

        using (var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append))   // played here: recorded with its bytes
        {
            PsoDb.WriteBlob(f, CommunityTests.Sha1(shader), shader);
            PsoDb.Write(f, pso.Tag, pso.Payload);
        }
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Stale, 1L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));
    }

    /// <summary>A community recording with a pipeline at two texture filtering settings (static samplers at 1x and at 16x
    /// anisotropic) counts it as one new pipeline; with a recording of this PC's at 1x, the 16x one isn't counted.</summary>
    [Fact]
    public async Task A_pipeline_at_two_texture_filtering_settings_is_one_and_another_settings_none()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, _, _, _) = PackGame(clock);
        await k.ScanAsync(default);
        await Compile(k);
        var x1 = RootSig.StaticSamplers(RootSig.Rule.Ff7).ToArray();
        var x16 = x1.ToArray();
        for (var o = 0; o < x16.Length; o += 52) { BitConverter.GetBytes(0x55u).CopyTo(x16, o); BitConverter.GetBytes(16u).CopyTo(x16, o + 20); }
        var cs = new ShaderInfo(new string('a', 40), Stage.Compute, "cs_6_0", 0, new(0, 0, 0, 0), [], [], []);
        byte[] Rs(byte[] samplers) => RootSig.Serialize(RootSig.Build(RootSig.Rule.Ff7, new Dictionary<Stage, ShaderInfo> { [Stage.Compute] = cs }, true), samplers);
        var dir = k.Store.GameDir(_game.Id);
        void Db(string name, byte[] shader, params byte[][] rss)
        {
            using var f = File.Create(Path.Combine(dir, name));
            PsoDb.WriteBlob(f, CommunityTests.Sha1(shader), shader);
            foreach (var rs in rss) PsoDb.WriteBlob(f, CommunityTests.Sha1(rs), rs);
            foreach (var rs in rss) PsoDb.Write(f, 'C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1(shader)));
        }
        var (shared, mine) = (SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "at two settings"), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "played here"));
        Db("community.db", shared, Rs(x1), Rs(x16));
        File.WriteAllBytes(Path.Combine(dir, "community.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new CommunityDownload(new string('d', 64), new string('c', 40), 2, DateTimeOffset.UtcNow)));
        k.RefreshGame(_game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);

        Db("community.db", shared, Rs(x16));
        Db("recording.db", mine, Rs(x1));
        k.RefreshGame(_game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);   // this PC's own; the 16x one is another setting's
    }

    [Fact]
    public async Task A_pipeline_that_crashes_this_driver_is_neither_pending_nor_warmed()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, _, _, _) = PackGame(clock);
        await k.ScanAsync(default);
        await Compile(k);
        RecordBuiltAtRunTime("crashes the driver");
        k.RefreshGame(_game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
        var key = PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Single(r => r.Tag == 'C').Key;
        var rec = k.Store.LoadGame(_game.Id);
        (rec.CrashKeys, rec.CrashKeysDriver) = ([key], Gpu.DriverVersion);   // a warm found it removes the device
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        Assert.Equal(0, k.Games.Single().RecordedSinceWarm);
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));
    }

    [Fact]
    public async Task A_prune_that_listed_a_key_file_before_a_write_reused_it_leaves_it()
    {
        var store = new AppStore(Path.Combine(_root, "prune-race"));
        var dir = store.GameDir(_game.Id);
        Directory.CreateDirectory(dir);
        string[] keys = [new string('4', 40)];
        var file = KeyFiles.Write(dir, "warm", keys);
        File.SetLastWriteTimeUtc(Path.Combine(dir, file), DateTime.UtcNow.AddHours(-2));   // unnamed, an hour old: the prune lists it
        Task<string>? write = null;
        KeyFiles.PruneListed = d =>
        {
            if (d != dir) return;
            write = Task.Run(() => KeyFiles.Write(dir, "warm", keys));   // another operation publishes the same contents now
            Thread.Sleep(300);
        };
        try { store.PruneKeyFiles(_game.Id); }
        finally { KeyFiles.PruneListed = null; }
        Assert.Equal(file, await write!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(Path.Combine(dir, file)));
        Assert.NotNull(KeyFiles.Set(Path.Combine(dir, file)));
    }

    static byte[] U32(params uint[] v)
    {
        var b = new byte[4 * v.Length];
        for (var i = 0; i < v.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), v[i]);
        return b;
    }

    sealed class ServingReader(ShaderIndex index, params byte[][] blobs) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => Unreal;
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => index;
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var b in blobs) if (sha1s.Contains(CommunityTests.Sha1(b))) sink(CommunityTests.Sha1(b), b);
        }
    }

    [Fact]
    public async Task A_recorded_pipeline_on_a_root_signature_only_the_install_gives_isnt_pending_after_a_warm()
    {
        var (shader, rs) = (SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "game shader"), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "standalone root"));
        var index = SCSKiller.Tests.Planning.MiddlewarePackTests.Index(CommunityTests.Sha1(shader)) with { ContentHash = new string('c', 40) };   // the root isn't in the shader list
        var k = Killer(new ServingReader(index, shader, rs), new FakePlanner(records: []));
        using (var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append))   // hash-only: rehydrating finds the root in the install
            PsoDb.Write(f, 'C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1(shader)));
        await k.ScanAsync(default);
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
    }

    [Fact]
    public async Task A_root_signature_of_a_pack_entry_this_gpu_doesnt_run_makes_nothing_pending()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var w64 = SCSKiller.Tests.Planning.SharedPackTests.Wave64("ffx wave64");
        var (k, planner, a, _, _) = PackGame(clock, alsoInDll: w64);
        var rs = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "another gpu's root");
        var dll = Middleware.Detect(_exeDir).Single();
        var image = Middleware.Scan(dll.Path);
        var pack = new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size, gpu: "nvidia");
        pack.Add(new PsoDb.Rec('C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1(w64))), "test:other");   // NVIDIA never seeds it
        pack.RootSignatures[CommunityTests.Sha1(rs)] = rs;
        pack.Write(planner.Packs!.PathOf(dll.Vendor, dll.Name, image.ContentHash));
        using (var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append))   // its root in no file here
            PsoDb.Write(f, 'C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1(a)));
        await k.ScanAsync(default);
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
    }

    [Fact]
    public async Task A_planner_made_pipeline_whose_root_signature_arrives_counts_whatever_the_planner_version()
    {
        var (shader, rs) = (SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "game shader"), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "root"));
        var index = SCSKiller.Tests.Planning.MiddlewarePackTests.Index(CommunityTests.Sha1(shader)) with { ContentHash = new string('c', 40) };
        var k = Killer(new IndexReader(Unreal, index), new FakePlanner(records: [new('C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1(shader)))]));
        await k.ScanAsync(default);
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
        Community(k, new PsoDb.Rec('B', [.. SHA1.HashData(rs), .. rs]));   // its root signature, no new pipeline
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(Planner.Version, k.Store.LoadGame(_game.Id).WarmedPlanVersion);
        Assert.Equal((GameStatus.Stale, 1L, 1L), (s.Status, s.NewPipelines, NewShaders.Count(s)));
        Assert.Equal("1 new pipeline; compile again to include them", s.StatusReason);
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
    }

    [Fact]
    public async Task A_pack_grown_while_a_plan_seeds_from_it_is_planned_by_the_next_compile()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, planner, a, b, _) = PackGame(clock);
        var dll = Middleware.Detect(_exeDir).Single();
        var image = Middleware.Scan(dll.Path);
        var path = planner.Packs!.PathOf(dll.Vendor, dll.Name, image.ContentHash);
        void Pack(params byte[][] shaders)
        {
            var pack = new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size, gpu: "nvidia");
            foreach (var s in shaders) pack.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(s))), "test:other");
            pack.Write(path);
        }
        Pack(a);
        k.Log = new At("pack PSOs", () => Pack(a, b));   // another game's import promotes B once the plan has read the pack
        await k.ScanAsync(default);
        await Compile(k);
        Assert.Equal((GameStatus.Stale, 1L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));   // B: in no plan yet
        await Compile(k);   // the plan read A only: built again, with B
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));
    }

    [Fact]
    public async Task A_blob_imported_during_a_warm_makes_what_it_skipped_for_it_count()
    {
        var (shader, rs) = (SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "game shader"), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "root"));
        var index = SCSKiller.Tests.Planning.MiddlewarePackTests.Index(CommunityTests.Sha1(shader)) with { ContentHash = new string('c', 40) };
        var planner = new FakePlanner(records: [new('C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1(shader)))]);
        var k = Killer(new IndexReader(Unreal, index), planner);
        planner.Materialized = () => Community(k, new PsoDb.Rec('B', [.. SHA1.HashData(rs), .. rs]));   // after the warm skipped it for its root signature
        await k.ScanAsync(default);
        await Compile(k);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, 1L), (s.Status, NewShaders.Count(s)));
        planner.Materialized = null;
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
    }

    /// <summary>A local pack on NVIDIA holding <paramref name="entry"/> (and <paramref name="rs"/>, its root signature).</summary>
    void LocalPack(Planner planner, PsoDb.Rec entry, byte[]? rs = null)
    {
        var dll = Middleware.Detect(_exeDir).Single();
        var image = Middleware.Scan(dll.Path);
        var pack = new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size, gpu: "nvidia");
        pack.Add(entry, "test:other");
        if (rs != null) pack.RootSignatures[CommunityTests.Sha1(rs)] = rs;
        pack.Write(planner.Packs!.PathOf(dll.Vendor, dll.Name, image.ContentHash));
    }

    [Fact]
    public async Task A_pack_entry_whose_root_signature_only_its_pack_keeps_isnt_pending_after_a_warm()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, planner, a, _, _) = PackGame(clock);
        var rs = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "ffx root");   // in the pack, not in the DLL
        LocalPack(planner, new PsoDb.Rec('C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1(a))), rs);
        await k.ScanAsync(default);
        await Compile(k);
        var plan = k.Store.LoadGame(_game.Id).Plan!;
        var (header, records) = PlanFile.Read(plan.FilePath);
        PlanFile.Write(header, [.. records.Where(r => r.Tag != 'B')]);   // a plan whose other PSOs claimed the root without writing it
        for (var i = 0; i < 2; i++)
        {
            await Compile(k);
            Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
            k.RefreshGame(_game.Id);
            Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
        }
    }

    [Fact]
    public async Task A_pipeline_that_loses_a_shader_after_its_warm_isnt_pending()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, planner, a, _, _) = PackGame(clock);
        LocalPack(planner, new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(a))));
        await k.ScanAsync(default);
        await Compile(k);
        Assert.Equal(0, NewShaders.Count(k.Games.Single()));
        File.Delete(Path.Combine(_exeDir, "amd_fidelityfx_dx12.dll"));   // the plan's pack PSO has no shader here any more
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((0L, 0L), (s.RecordedSinceWarm, s.NewPipelines ?? 0));
    }

    [Fact]
    public async Task An_import_while_a_compile_reads_its_recordings_waits_and_counts_after_it()
    {
        var planner = new FakePlanner(records: []);
        var k = Killer(new FakeReader(Unreal), planner);
        await k.ScanAsync(default);
        await Compile(k);
        var shader = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "built at run time");
        var key = PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(shader)) is var payload ? new PsoDb.Rec('C', payload).Key : null;
        Task? import = null;
        var waited = false;
        k.RecordingsRead = () =>
        {
            RecordBuiltAtRunTime("built at run time");
            import = Task.Run(() => k.RefreshGame(_game.Id));   // the game exited: its recording is imported
            waited = !import.Wait(2000);   // held until the recordings are prepared
        };
        List<string>? prepared = null;
        planner.Materialized = () => prepared = planner.MaterializedWith is { } r ? [.. PsoDb.Read(r.DbPath).Select(x => x.Key)] : [];
        await Compile(k);
        k.RecordingsRead = null;
        await import!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(waited);
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.DoesNotContain(key, prepared!);
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Stale, 1L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));   // not compiled: counted
    }

    [Fact]
    public async Task A_community_download_while_a_compile_reads_its_recordings_waits_and_counts_after_it()
    {
        var planner = new FakePlanner(records: []);
        var k = Killer(new FakeReader(Unreal), planner);
        await k.ScanAsync(default);
        await Compile(k);
        var raw = CommunityTests.HashOnly(new string('1', 40));
        var key = PsoDb.Read(new MemoryStream(raw)).Single(r => r.Tag != 'B').Key;
        var obj = CommunityTests.Brotli(raw);
        var entry = new CommunityEntry(new string('c', 40), Convert.ToHexStringLower(SHA256.HashData(obj)), obj.Length, 1, 2);
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var community = new Community(Path.Combine(_root, "data"), (_, _) => Task.FromResult<string?>("t"),
            new RouteFailover(new CommunityTests.Fake(_ => CommunityTests.Ours(HttpStatusCode.OK, obj)), [new("https://api.test.com/")], clock), clock);
        Task<CommunityDownload?>? download = null;
        var waited = false;
        k.RecordingsRead = () =>
        {
            download = Task.Run(() => community.DownloadAsync(entry, k.Store.GameDir(_game.Id)));
            waited = !download.Wait(2000);   // its replacement of community.db waits until the recordings are prepared
        };
        List<string>? prepared = null;
        planner.Materialized = () => prepared = planner.MaterializedWith is { } r ? [.. PsoDb.Read(r.DbPath).Select(x => x.Key)] : [];
        await Compile(k);
        k.RecordingsRead = null;
        Assert.NotNull(await download!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(waited);
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.DoesNotContain(key, prepared!);
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Stale, 1L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));   // not compiled: counted
    }

    [Fact]
    public async Task A_warm_key_file_that_cant_be_read_is_an_unknown_baseline()
    {
        var (k, _, _) = await WarmedWithPlan();
        var file = Path.Combine(k.Store.GameDir(_game.Id), k.Store.LoadGame(_game.Id).WarmKeysFile!);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            k.RefreshGame(_game.Id);
            var s = k.Games.Single();
            Assert.Equal(GameStatus.Stale, s.Status);
            Assert.EndsWith("what the last compile replayed is no longer known", s.StatusReason);
        }
        k.RefreshGame(_game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    void Community(ScsKiller k, params PsoDb.Rec[] records)
    {
        var dir = k.Store.GameDir(_game.Id);
        Directory.CreateDirectory(dir);
        using (var f = File.Create(Path.Combine(dir, "community.db")))
            foreach (var r in records) PsoDb.Write(f, r.Tag, r.Payload);
        File.WriteAllBytes(Path.Combine(dir, "community.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new CommunityDownload(new string('d', 64), new string('c', 40), 1, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task A_state_object_built_on_a_collection_this_install_cant_replay_isnt_pending_after_a_warm()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, _, _, _) = PackGame(clock);
        await k.ScanAsync(default);
        await Compile(k);
        var rs = CommunityTests.RootSignature();
        var library = CommunityTests.Sha1("a DXIL library this install doesn't have"u8.ToArray());
        var collection = new PsoDb.Rec('R', [.. U32(0, 2, 1), .. SHA1.HashData(rs), .. U32(5), .. Convert.FromHexString(library), .. U32(0)]);
        var pipeline = new PsoDb.Rec('R', [.. U32(3, 2, 1), .. SHA1.HashData(rs), .. U32(6), .. Convert.FromHexString(collection.Key), .. U32(0)]);   // its own blobs are here
        Community(k, new PsoDb.Rec('B', [.. SHA1.HashData(rs), .. rs]), collection, pipeline);
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));   // the warm would skip both
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));

        var bytes = "a DXIL library this install doesn't have"u8.ToArray();   // then a recording has the library
        Community(k, new PsoDb.Rec('B', [.. SHA1.HashData(rs), .. rs]), new PsoDb.Rec('B', [.. SHA1.HashData(bytes), .. bytes]), collection, pipeline);
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Stale, 2L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));
    }

    [Fact]
    public async Task A_planned_pipeline_that_crashes_this_driver_isnt_offered_again_by_a_rebuild()
    {
        List<PsoDb.Rec> records = [new('B', [.. new byte[20], 9]), new('P', [1]), new('P', [2]), new('C', [3])];
        var warmer = new FakeWarmer { Crashed = [new PsoDb.Rec('P', [1]).Key] };   // the warm found it removes the device
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: records), warmer);
        k.IdleTime = () => TimeSpan.FromHours(1);
        await k.ScanAsync(default);
        await Compile(k);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        OlderPlanner(k);   // a newer planner rebuilds the same plan on the same driver
        k.CheckPlans = true;
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, 0L), (s.Status, NewShaders.Count(s)));
    }

    [Fact]
    public async Task The_other_recording_replaced_with_the_same_size_and_time_is_read_again()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, _, _, _) = PackGame(clock);
        await k.ScanAsync(default);
        await Compile(k);
        var (x, h) = (new byte[600], new byte[600]);
        new Random(1).NextBytes(x);
        new Random(2).NextBytes(h);   // the shader the community PSO needs
        var recording = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        void Record(byte[] blob)
        {
            using (var f = File.Create(recording))
            {
                PsoDb.WriteBlob(f, CommunityTests.Sha1(blob), blob);
                PsoDb.Write(f, 'C', PsoDb.Compute(CommunityTests.Sha1(blob), CommunityTests.Sha1(blob)));   // its own PSO, of its shader
            }
            File.SetLastWriteTimeUtc(recording, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        Record(x);
        Community(k, new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(h))));   // its shader in no file here
        await Compile(k);
        var size = new FileInfo(recording).Length;
        Assert.Equal(0, k.Games.Single().RecordedSinceWarm);
        Record(h);   // another program's write: the same size and time, now with its own PSO and the community PSO's shader
        Assert.Equal(size, new FileInfo(recording).Length);
        k.RefreshGame(_game.Id);
        Assert.Equal(2, k.Games.Single().RecordedSinceWarm);
    }

    [Fact]
    public async Task An_unknown_baseline_counts_and_shows_everything_whatever_the_planner_version()
    {
        var (k, _, _) = await WarmedWithPlan();
        var rec = k.Store.LoadGame(_game.Id);
        File.Delete(Path.Combine(k.Store.GameDir(_game.Id), rec.WarmKeysFile!));   // what the warm replayed is gone
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(Planner.Version, k.Store.LoadGame(_game.Id).WarmedPlanVersion);   // the same planner
        Assert.Equal((GameStatus.Stale, 3L), (s.Status, NewShaders.Count(s)));   // the plan's three pipelines
        Assert.Equal("3 pipelines to compile again: what the last compile replayed is no longer known", s.StatusReason);
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
    }

    [Fact]
    public async Task An_unknown_baseline_is_stale_with_nothing_to_count()
    {
        var (k, _, _) = await WarmedWithPlan();
        var rec = k.Store.LoadGame(_game.Id);
        File.Delete(Path.Combine(k.Store.GameDir(_game.Id), rec.WarmKeysFile!));
        (rec.CrashKeys, rec.CrashKeysDriver) = ([.. Planner.PlanBody(rec.Plan!.FilePath).Where(r => r.Tag != 'B').Select(r => r.Key)], Gpu.DriverVersion);   // all left out
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, 0L), (s.Status, NewShaders.Count(s)));
        Assert.Equal("compile again: what the last compile replayed is no longer known", s.StatusReason);
    }

    [Fact]
    public async Task A_warm_from_before_key_files_is_an_unknown_baseline_until_the_next_compile()
    {
        var (k, _, _) = await WarmedWithPlan();
        var rec = k.Store.LoadGame(_game.Id);
        rec.WarmKeysFile = null;
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, 3L), (s.Status, NewShaders.Count(s)));
        Assert.Equal("3 pipelines to compile again: what the last compile replayed is no longer known", s.StatusReason);
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, NewShaders.Count(k.Games.Single())));
    }

    [Fact]
    public async Task A_d3d11_game_neither_compiles_nor_counts_pack_pipelines()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, planner, a, _, _) = PackGame(clock, engine: Unreal with { GraphicsApi = "D3D11" }, profile: "nvidia-1");   // NVIDIA warms D3D11 games
        var dll = Middleware.Detect(_exeDir).Single();
        var image = Middleware.Scan(dll.Path);
        var pack = new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size, gpu: "nvidia");
        pack.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(a))), "test:other");
        pack.Write(planner.Packs!.PathOf(dll.Vendor, dll.Name, image.ContentHash));
        await k.ScanAsync(default);
        await Compile(k);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, 0L, 0), (s.Status, s.RecordedSinceWarm, s.Middleware!.Single().Pipelines));
        Assert.Equal(0, s.Plan!.MiddlewareItems);
    }

    [Fact]
    public async Task A_damaged_or_missing_warm_key_file_counts_everything_until_a_warm_writes_it_again()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, _, _, _) = PackGame(clock);
        RecordBuiltAtRunTime("one");
        await k.ScanAsync(default);
        await Compile(k);
        RecordBuiltAtRunTime("two");
        k.RefreshGame(_game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
        var file = Path.Combine(k.Store.GameDir(_game.Id), k.Store.LoadGame(_game.Id).WarmKeysFile!);
        var good = File.ReadAllBytes(file);

        File.WriteAllBytes(file, good[..^1]);   // truncated: what it says it holds isn't known
        k.RefreshGame(_game.Id);
        Assert.Equal(2, k.Games.Single().RecordedSinceWarm);
        File.Delete(file);
        k.RefreshGame(_game.Id);
        Assert.Equal(2, k.Games.Single().RecordedSinceWarm);

        File.WriteAllBytes(file, good[..^20]);   // a whole key short: still not what its name says
        k.RefreshGame(_game.Id);
        Assert.Equal(2, k.Games.Single().RecordedSinceWarm);
        await Compile(k);
        Assert.Equal((GameStatus.Warmed, 0L), (k.Games.Single().Status, k.Games.Single().RecordedSinceWarm));
    }

    [Fact]
    public void Pruning_keeps_what_the_stored_record_names_and_what_another_process_just_wrote()
    {
        var store = new AppStore(Path.Combine(_root, "prune"));
        var dir = store.GameDir(_game.Id);
        Directory.CreateDirectory(dir);
        string Old(string name) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, []); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-2)); return name; }
        var named = KeyFiles.Write(dir, "warm", [new string('1', 40)]);
        File.SetLastWriteTimeUtc(Path.Combine(dir, named), DateTime.UtcNow.AddHours(-2));
        var rec = store.LoadGame(_game.Id);
        rec.WarmKeysFile = named;
        store.SaveGame(_game.Id, rec);
        var stale = Old("plan-0000000000000000.keys");
        var leftover = Old("warm-1111111111111111.keys.123.4.tmp");
        // B published its file and hasn't saved the record naming it yet when A prunes; C published one that already
        // existed, an hour old and unnamed
        var justWritten = KeyFiles.Write(dir, "plan", [new string('2', 40)]);
        var reused = Old(KeyFiles.Write(dir, "plan", [new string('3', 40)]));
        Assert.Equal(reused, KeyFiles.Write(dir, "plan", [new string('3', 40)]));
        var others = new[] { Old("rayquery.keys"), Old("index.shaders"), Old("other.keys.1.2.tmp") };   // not key files of plans or warms
        store.PruneKeyFiles(_game.Id);
        Assert.True(File.Exists(Path.Combine(dir, named)));
        Assert.True(File.Exists(Path.Combine(dir, justWritten)));
        Assert.True(File.Exists(Path.Combine(dir, reused)));
        Assert.All(others, f => Assert.True(File.Exists(Path.Combine(dir, f))));
        Assert.False(File.Exists(Path.Combine(dir, stale)));
        Assert.False(File.Exists(Path.Combine(dir, leftover)));
    }

    [Fact]
    public async Task A_community_recording_not_in_use_doesnt_hide_what_a_pack_brings()
    {
        var clock = new CommunityTests.Clock { Now = DateTimeOffset.UtcNow };
        var (k, _, a, _, publish) = PackGame(clock);
        k.Settings = k.Settings with { UseCommunityDb = false };
        await k.SettingsRefresh;
        // a community recording with A, downloaded while the setting was on
        var dir = k.Store.GameDir(_game.Id);
        Directory.CreateDirectory(dir);
        using (var f = File.Create(Path.Combine(dir, "community.db"))) PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(a)));
        File.WriteAllBytes(Path.Combine(dir, "community.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new CommunityDownload(new string('c', 64), "0123456789abcdef0123456789abcdef01234567", 1, DateTimeOffset.UtcNow)));
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);   // without A: the community recording is off

        publish(a);
        k.StartCommunitySync();
        await k.CommunitySync;
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
    }

    [Fact]
    public async Task Signed_out_the_public_manifest_says_a_game_is_in_the_community_database_and_nothing_else_is_asked()
    {
        const string content = "0123456789abcdef0123456789abcdef01234567";
        var game = _game with { Version = "100" };
        var manifest = CommunityTests.Manifest(CommunityTests.Alias($"{game.Id}@100", content), CommunityTests.Entry(content, "object"u8.ToArray(), 18_406));
        var fake = new CommunityTests.Fake(_ => CommunityTests.Ours(HttpStatusCode.OK, manifest));
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner(), game: game);
        k.Community = new Community(k.Store.DataDir, (_, _) => Task.FromResult<string?>(null), new RouteFailover(fake, [new("https://api.test.com/")]));   // no "db"

        Assert.Null((await k.ScanAsync(default)).Single().InCommunityDb);   // no copy of the manifest yet
        await k.CommunitySync;
        var s = k.Games.Single();   // re-evaluated by the manifest check alone: nothing was downloaded
        Assert.Equal((GameStatus.NeedsRecording, true, 18_406, (CommunityInfo?)null), (s.Status, s.InCommunityDb, s.CommunityDbPsos, s.Community));
        Assert.Equal("needs one short recording; " + ScsKiller.InDbNote, s.StatusReason);
        Assert.Equal(["GET https://api.test.com/v1/manifest/0"], fake.Log);   // no object, no token, nothing about the game
        Assert.False(File.Exists(Path.Combine(k.Store.GameDir(game.Id), "community.db")));
    }

    [Fact]
    public async Task A_game_is_matched_to_the_manifest_by_its_index_only_while_that_index_is_of_the_installed_build()
    {
        const string indexed = "0123456789abcdef0123456789abcdef01234567", published = "fedcba9876543210fedcba9876543210fedcba98";
        var game = _game with { Version = "101" };
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner(), game: game);
        await k.ScanAsync(default);
        Directory.CreateDirectory(Path.Combine(k.Store.DataDir, "community"));
        File.WriteAllBytes(Path.Combine(k.Store.DataDir, "community", "manifest.bin"), CommunityTests.Manifest(
            CommunityTests.Alias($"{game.Id}@101", published), CommunityTests.Entry(published, "a"u8.ToArray(), 7)));
        var rec = k.Store.LoadGame(game.Id);
        (rec.IndexContentHash, rec.IndexGameVersion) = (indexed, "100");   // indexed before the game's update
        k.Store.SaveGame(game.Id, rec);

        k.RefreshGame(game.Id);
        Assert.Equal((true, 7), (k.Games.Single().InCommunityDb, k.Games.Single().CommunityDbPsos));   // by the store build, not the old index

        File.WriteAllBytes(Path.Combine(k.Store.DataDir, "community", "manifest.bin"), CommunityTests.Manifest(
            CommunityTests.Alias($"{game.Id}@101", published), CommunityTests.Entry(published, "a"u8.ToArray(), 7), CommunityTests.Entry(indexed, "b"u8.ToArray(), 5)));
        k.RefreshGame(game.Id);
        Assert.Equal(7, k.Games.Single().CommunityDbPsos);   // the old build's entry isn't this one's
        rec.IndexGameVersion = "101";
        k.Store.SaveGame(game.Id, rec);
        k.RefreshGame(game.Id);
        Assert.Equal(5, k.Games.Single().CommunityDbPsos);   // an index of the installed build: its own content hash
    }

    /// <summary>A game that needs a recording (every D3D12 game on AMD); captures what Build planned from.</summary>
    sealed class NeedsRecordingPlanner : IPlanner
    {
        public readonly FakePlanner Inner = new();
        public List<string>? BuiltWith;
        public PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps) =>
            recording == null ? new(Readiness.NeedsRecording, "needs one short recording") : new(Readiness.Ready, "planned from a recording");
        public Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir, IProgress<string>? log, CancellationToken ct, bool maximum = false)
        {
            BuiltWith = recording == null ? null : PsoDb.Read(recording.DbPath).Select(r => r.Key).ToList();
            return Inner.Build(game, engine, index, recording, caps, outDir, log, ct, maximum);
        }
        public void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct) =>
            Inner.Materialize(plan, game, engine, reader, recording, workDir, ct);
    }

    /// <param name="indexed">the index lists <paramref name="blobs"/> (else it's empty and they come only from ReadShaders)</param>
    sealed class BlobReader(EngineInfo engine, Dictionary<string, byte[]> blobs, bool indexed = false, string content = "content-1") : IEngineReader
    {
        public int Indexes;
        public Action<CancellationToken>? Indexing;   // runs in each Index
        public EngineInfo? Detect(Game game) => engine;
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct)
        {
            Interlocked.Increment(ref Indexes);
            Indexing?.Invoke(ct);
            return new(content, ["PCD3D_SM6"], indexed ? blobs.Keys.ToDictionary(h => h, _ => (ShaderInfo)null!) : new Dictionary<string, ShaderInfo>(), []);
        }
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var (h, b) in blobs) if (sha1s.Contains(h)) sink(h, b);
        }
    }

    sealed class FakeReader(EngineInfo? engine, string content = "content-1", int shaders = 3000, List<ShaderMap>? maps = null) : IEngineReader
    {
        public int Detects;
        public EngineInfo? Detect(Game game) { Interlocked.Increment(ref Detects); return engine; }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) =>
            new(content, ["PCD3D_SM6"], Enumerable.Range(0, shaders).ToDictionary(i => $"{i:x40}", i => (ShaderInfo)null!), [.. maps ?? []]);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary>A UE4 SM5 game resolved by UnrealRhi from a user Saved folder; by default with Deep Rock Galactic's launch menu.</summary>
    sealed class LogReader(string saved, string[]? menu = null) : IEngineReader
    {
        public int Detects;
        public EngineInfo? Detect(Game game)
        {
            Interlocked.Increment(ref Detects);
            var (api, _) = SCSKiller.Core.Unreal.UnrealRhi.Resolve(4, ["PCD3D_SM5"], new Dictionary<string, string> { ["FSD/Config/DefaultEngine.ini"] = "" }, "FSD",
                saved, "", menu ?? ["-dx12 Play Deep Rock Galactic (DirectX 12)", "-dx11 Play Deep Rock Galactic (DirectX 11)"]);
            return Unreal with { Version = "4.27", GraphicsApi = api };
        }
        public string DetectStamp(Game game, EngineInfo? engine) => SCSKiller.Core.Unreal.UnrealRhi.UserFiles(saved);
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM5"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary>Detect returns <see cref="Api"/>; <see cref="During"/> runs inside it, after the API is read; <see cref="Gate"/>,
    /// when set, holds the first Detect until released.</summary>
    sealed class StampReader : IEngineReader
    {
        public int Detects;
        public string Stamp = "a", Api = "D3D12";
        public Action? During;
        public ManualResetEventSlim? Gate;
        public readonly ManualResetEventSlim Entered = new();
        public EngineInfo? Detect(Game game)
        {
            var first = Interlocked.Increment(ref Detects) == 1;
            var api = Api;
            During?.Invoke();
            if (first && Gate != null) { Entered.Set(); Gate.Wait(TimeSpan.FromSeconds(10)); }
            return Unreal with { GraphicsApi = api };
        }
        public string DetectStamp(Game game, EngineInfo? engine) => Stamp;
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM5"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary>An Unreal game per id, with the graphics API given for it (default D3D12).</summary>
    sealed class ApiReader(Dictionary<string, string> apis) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => Unreal with { GraphicsApi = apis.GetValueOrDefault(game.Id, "D3D12") };
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary><paramref name="records"/>: a real plan.bin with these records (read at each build); else a stand-in file.</summary>
    sealed class FakePlanner(byte[]? genDb = null, long skipped = 0, PlanStats? stats = null, List<PsoDb.Rec>? records = null, byte[]? mainDb = null,
        Func<Recording?, PlanStats>? statsFor = null, Action? onBuild = null) : IPlanner
    {
        public PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps) => new(Readiness.Ready, "synthesized templates");
        public Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir, IProgress<string>? log, CancellationToken ct, bool maximum = false)
        {
            var file = Path.Combine(outDir, "plan.bin");
            Directory.CreateDirectory(outDir);
            var plan = new Plan(game.Id, index.ContentHash, "PCD3D_SM6", caps.Profile, statsFor?.Invoke(recording) ?? stats ?? new PlanStats(0, 10000, 5, 7, true), file);
            if (records != null) PlanFile.Write(plan, records);
            else File.WriteAllText(file, "plan");
            onBuild?.Invoke();
            return plan;
        }
        public Recording? MaterializedWith;
        public int UnresolvedAtMaterialize = -1;
        public void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct)
        {
            (MaterializedWith, UnresolvedAtMaterialize) = (recording, recording == null ? -1 : SCSKiller.Tests.Planning.RehydrateTests.Unresolved(recording.DbPath).Count);
            Directory.CreateDirectory(workDir);
            File.WriteAllBytes(Path.Combine(workDir, "scskiller_gen.db"), genDb ?? "gen"u8.ToArray());
            if (mainDb != null) File.WriteAllBytes(Path.Combine(workDir, "scskiller.db"), mainDb);
            if (skipped > 0) File.WriteAllText(Path.Combine(workDir, Planner.SkippedFile), skipped.ToString()); // like Planner.Materialize
            Materialized?.Invoke();
        }
        public Action? Materialized;
    }

    /// <summary>Completes at once. <paramref name="whileRunning"/> plays the warm's side effects (e.g. the driver opening
    /// cache files); what it returns is disposed when the run ends.</summary>
    sealed class FakeWarmer(Func<IDisposable?>? whileRunning = null, long failed = 0) : IWarmer
    {
        public bool SawMaterializedWork;
        public readonly List<string> Started = [], Staged = [];   // game ids; the exe file names the warms staged
        public WarmOptions? Options;
        public IReadOnlyCollection<string>? Crashed;   // the result's keys of items that crash the driver
        public int Passes;   // the work folder's careful passes (0 = none)
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress)
        {
            lock (Started) { Started.Add(game.Id); Staged.Add(Path.GetFileName(game.ExePath)); Options = options; }
            SawMaterializedWork = File.Exists(Path.Combine(workDir, "scskiller_gen.db"));
            Passes = WarmPasses.Read(workDir)?.Count ?? 0;
            var held = whileRunning?.Invoke();   // what the staged warm holds open while it runs: attribution samples it then
            progress?.Report(new WarmProgress(5000, 10000, 0, 1000));
            var result = new WarmResult(WarmOutcome.Completed, 10000, 10000, failed, TimeSpan.FromSeconds(10), 20000 * 1024, "", null, Crashed: Crashed);
            return new Run(held == null ? Task.FromResult(result) : Task.Run(async () => { await Task.Delay(1000); held.Dispose(); return result; }));
        }
        sealed class Run(Task<WarmResult> completion) : IWarmRun
        {
            public Task<WarmResult> Completion { get; } = completion;
            public void Pause() { }
            public void Resume() { }
            public void Stop() { }
        }
    }
}
