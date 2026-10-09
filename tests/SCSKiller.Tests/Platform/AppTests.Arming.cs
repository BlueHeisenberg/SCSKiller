using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// The recorder records only when armed, so every path that leaves it installed in a clean game must leave it armed: here
// from a PC that has no ledger folder yet, as a fresh install has.
public partial class AppTests
{
    /// <summary>The ledger in a folder of this test that doesn't exist yet; the real one after.</summary>
    /// <summary>A ledger entry another SCSKiller process is reading (its scan of the ledger) as this one revokes it: deleted, no
    /// revocation mark.</summary>
    [Fact]
    public void A_ledger_entry_another_process_reads_is_still_deleted_by_a_revocation()
    {
        ScsKiller.WriteAttestation(_game.ExePath);
        var ledger = ScsKiller.LedgerFile(_game.ExePath);
        using (var reading = ScsKiller.SharedLines(ledger).GetEnumerator())
        {
            Assert.True(reading.MoveNext());   // open, mid-read
            Assert.Empty(ScsKiller.RevokeLedgers(_game.ExePath));
            Assert.False(File.Exists(ledger));
        }
        Assert.False(File.Exists(ledger + ".revoked"));
    }

    /// <summary>Armed as the proxy checks it: armed=1 beside the exe, and its nonce in the exe's ledger entry.</summary>
    static bool ArmedWithLedger(Game g) => ArmedState(g) == true;

    /// <summary><see cref="ArmedWithLedger"/> from one read of both files, shared as the app reads them; null while the app
    /// arms the game again: the ledger already has the next nonce (it is written first), the file beside the exe not yet.</summary>
    static bool? ArmedState(Game g)
    {
        static List<string>? Lines(string f)
        {
            try { return [.. ScsKiller.SharedLines(f)]; }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        }
        static string Nonce(List<string> lines) => lines.FirstOrDefault(l => l.StartsWith("nonce=", StringComparison.Ordinal)) ?? "";
        if (Lines(Path.Combine(Path.GetDirectoryName(g.ExePath)!, ScsKiller.ArmedFile)) is not { } armed
            || Lines(ScsKiller.LedgerFile(g.ExePath)) is not { } ledger) return false;
        if (Nonce(armed) != Nonce(ledger)) return null;
        return armed.Contains("armed=1") && Nonce(armed).Length > "nonce=".Length;
    }

    /// <summary>Waits out an arming the app has under way, then <see cref="ArmedWithLedger"/>.</summary>
    static async Task<bool> ArmedOnceWritten(Game g)
    {
        bool? state = null;
        await Until(() => (state = ArmedState(g)) != null);
        return state == true;
    }

    /// <summary>A recording SCSKiller holds for the game: the install writes the keys file (through its temp file).</summary>
    static void Recorded(ScsKiller k, Game g)
    {
        var db = Path.Combine(k.Store.GameDir(g.Id), "recording.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        var shader = "shader"u8.ToArray();
        using var f = File.Create(db);
        PsoDb.Write(f, 'B', [.. SHA1.HashData(shader), .. shader]);
    }

    ScsKiller Managed()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        return k;
    }

    /// <summary>Armed when the call returns, and still once the install watcher's events of our own writes arrived.</summary>
    async Task AssertStaysArmed(ScsKiller k, Game g)
    {
        Assert.True(await ArmedOnceWritten(g));
        await Task.Delay(500);
        await Until(() => !k.DisarmQueued(g));
        Assert.True(await ArmedOnceWritten(g));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Record_all_games_arms_a_fresh_install_before_the_scan_returns(bool recorded)
    {
        var k = Managed();
        if (recorded) Recorded(k, _game);
        await k.ScanAsync(default);   // "record all games" installs it
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Equal(recorded, File.Exists(Path.Combine(_exeDir, Recordings.KeysFile)));
        await AssertStaysArmed(k, _game);
    }

    /// <summary>The ini goes in through a temp name beside it: the install watcher takes that name for one of the recorder's
    /// own, so a rewrite (here the recording limit changed) doesn't disarm.</summary>
    [Fact]
    public async Task Rewriting_the_ini_keeps_the_recorder_armed()
    {
        var k = Managed();
        await k.ScanAsync(default);
        await AssertStaysArmed(k, _game);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var before = File.ReadAllText(ini);
        k.Settings = k.Settings with { RecordingLimitMB = 0 };
        k.ReconcileRecorders(_game.Id);
        Assert.NotEqual(before, File.ReadAllText(ini));
        await AssertStaysArmed(k, _game);
    }

    [Fact]
    public async Task A_recorder_updated_at_startup_is_armed()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        File.WriteAllBytes(dll, [.. "MZ older proxy SCSKiller_StartWarm "u8]);   // an earlier build's, no armed file
        var k = Managed();
        Recorded(k, _game);
        await k.ScanAsync(default);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(dll));
        await AssertStaysArmed(k, _game);
    }

    [Fact]
    public async Task Turning_the_recorder_on_arms_it()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.Settings = k.Settings with { RecordAllGames = false };
        k.ManageRecorders = true;
        Recorded(k, _game);
        await k.ScanAsync(default);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        k.SetRecorderOverride(_game.Id, RecorderOverride.On);
        await AssertStaysArmed(k, _game);
    }

    /// <summary>The scan's migration of a recording stored before compact recordings writes the game folder's keys file and
    /// ini and the record: an install of the recorder (turning it on) waits for it.</summary>
    [Fact]
    public async Task A_recording_migration_holds_the_recorders()
    {
        var k = Managed();
        bool? held = null;
        k.MigrationLoaded = () => held = k.HoldsRecorderLock;
        Recorded(k, _game);
        await k.ScanAsync(default);
        await k.RecordingMigration.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(held);
    }

    [Fact]
    public async Task Every_watcher_pass_arms_an_installed_recorder_that_isnt()
    {
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);   // the new watcher's first check
        File.Delete(Path.Combine(_exeDir, ScsKiller.ArmedFile));   // a deletion is no install change: no event
        await k.CheckRecorderGames(false);
        Assert.True(ArmedWithLedger(_game));
    }

    [Fact]
    public async Task An_arming_that_fails_is_logged_once_and_retried_at_every_pass()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ScsKiller.LedgerDir)!);
        File.WriteAllText(ScsKiller.LedgerDir, "");   // a file where the ledger folder goes: it can't be made
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);
        Assert.False(ArmedWithLedger(_game));
        var log = Path.Combine(k.Store.DataDir, "recorders.log");
        Assert.Single(SharedLog(log).Split(Environment.NewLine), l => l.Contains("couldn't arm the recorder"));

        File.Delete(ScsKiller.LedgerDir);
        await k.CheckRecorderGames(false);
        Assert.True(ArmedWithLedger(_game));
    }

    [Fact]
    public async Task A_recorder_that_cant_be_armed_isnt_checked_at_every_pass()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ScsKiller.LedgerDir)!);
        File.WriteAllText(ScsKiller.LedgerDir, "");   // arming fails until it goes
        var k = Managed();
        var walks = 0;
        k.FullAntiCheatCheck = g => { Interlocked.Increment(ref walks); return Core.Games.GameFiles.DetectAntiCheat(g); };
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);   // the new watcher's first check
        walks = 0;
        for (var i = 0; i < 5; i++) await k.CheckRecorderGames(false);
        Assert.Equal(1, walks);   // once, then not before the retry interval

        k.UnarmedRetryInterval = TimeSpan.Zero;
        for (var i = 0; i < 5; i++) await k.CheckRecorderGames(false);
        Assert.Equal(3, walks);   // a few retries, then only at the full passes
        await k.CheckRecorderGames(true);
        Assert.Equal(4, walks);
    }

    [Fact]
    public async Task A_game_with_anti_cheat_is_never_armed()
    {
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames(true);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ArmedFile)));
        Assert.False(File.Exists(ScsKiller.LedgerFile(_game.ExePath)));
    }

    /// <summary>Started on a PC without the ledger folder, the session runs and its cleanup leaves the folder as it was. A
    /// ledger folder that can't be made stops the start with nothing left behind. The built proxy reads the run's ledger,
    /// not this test's, so the process here is a pass-through.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_offline_session_starts_without_a_ledger_folder(bool cantBeMade)
    {
        if (cantBeMade)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScsKiller.LedgerDir)!);
            File.WriteAllText(ScsKiller.LedgerDir, "");
        }
        EasyAntiCheatBeside();
        if (OfflineKiller() is not { } k) return;
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            k.SetOfflineRecording(_game.Id, true);
            var before = Names(_exeDir);
            k.ProcessNames = () => k.Games.Single().OfflineRunning ? new HashSet<string> { "steam", "Fake-Win64-Shipping" } : new HashSet<string> { "steam" };
            if (cantBeMade)
                Assert.Contains("didn't start", Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(_game.Id, confirmed: true); }).Message);
            else
            {
                await k.StartOfflineSession(_game.Id, confirmed: true).WaitAsync(TimeSpan.FromSeconds(120));
                Assert.True(Directory.Exists(ScsKiller.LedgerDir));
            }
            Assert.Equal(before, Names(_exeDir));
            Assert.False(File.Exists(ScsKiller.LedgerFile(_game.ExePath)));
            Assert.Null(k.Store.LoadGame(_game.Id).OfflineSession);
        });
    }

    [Fact]
    public async Task A_disarm_without_a_ledger_folder_revokes_and_the_next_pass_arms()
    {
        var k = Managed();
        await k.ScanAsync(default);
        await AssertStaysArmed(k, _game);
        Directory.Delete(ScsKiller.LedgerDir, true);
        File.WriteAllBytes(Path.Combine(_exeDir, "patch.bin"), [0]);   // a change: the watcher's event disarms
        await Until(() => Revoked(Path.Combine(_exeDir, ScsKiller.ArmedFile)));
        await Until(() => !k.DisarmQueued(_game));
        Assert.False(k.RevocationPending(_game.Id));
        await k.CheckRecorderGames(true);
        Assert.True(ArmedWithLedger(_game));
    }

    [Fact]
    public async Task Uninstall_without_a_ledger_folder_removes_the_recorder()
    {
        var k = Managed();
        await k.ScanAsync(default);
        Directory.Delete(ScsKiller.LedgerDir, true);
        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());   // SCSKiller's own uninstall
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ArmedFile)));
    }

    [Fact]
    public void The_offline_cleanup_helper_finishes_without_a_ledger_folder()
    {
        var store = Journal(["d3d12.dll", "scskiller.ini", ScsKiller.ArmedFile]);
        Assert.Equal(0, ScsKiller.RunOfflineCleanup(store, _game.Id, TimeSpan.Zero, othersRun: OurOthersRun));
        Assert.Null(store.LoadGame(_game.Id).OfflineSession);
        Assert.Equal(["Fake-Win64-Shipping.exe"], Names(_exeDir));
    }

    [Fact]
    public void An_attestation_is_written_without_a_ledger_folder()
    {
        ScsKiller.WriteAttestation(_game.ExePath);
        Assert.True(ArmedWithLedger(_game));
    }

    /// <summary>A start with nothing changed walks no install: the scan's cached verdict, the reconcile that arms the recorder
    /// again, the new install watcher's first look and the full pass all go by the folders' stamp, which the recorder's own
    /// files don't change. A file added beside the exe is walked.</summary>
    [Fact]
    public async Task A_start_with_nothing_changed_walks_no_install()
    {
        var first = Managed();
        await first.ScanAsync(default);   // detects, walks, installs and arms the recorder
        Assert.True(ArmedWithLedger(_game));
        first.StopWatchingInstalls();   // the app exits

        var k = Managed();
        var walks = 0;
        k.FullAntiCheatCheck = g => { Interlocked.Increment(ref walks); return Core.Games.GameFiles.DetectAntiCheat(g); };
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);
        await k.CheckRecorderGames(true);
        Assert.Equal(0, walks);
        await AssertStaysArmed(k, _game);

        File.WriteAllBytes(Path.Combine(_exeDir, "patch.dll"), [0x4D, 0x5A]);
        await Until(() => Revoked(Path.Combine(_exeDir, ScsKiller.ArmedFile)));   // the watcher's event
        await Until(() => !k.DisarmQueued(_game));
        await k.CheckRecorderGames(false);
        Assert.Equal(1, walks);
        await AssertStaysArmed(k, _game);
        k.StopWatchingInstalls();
    }

    /// <summary>Anti-cheat put deep in a recorder game's install while the app runs: the root's and exe folder's own entries
    /// stay the same, the install watcher's event disarms, and a reconcile before the next pass walks the install in full
    /// rather than arm it again by its last clean walk.</summary>
    [Fact]
    public async Task A_watcher_event_makes_the_next_reconcile_walk_the_install()
    {
        var k = Managed();
        await k.ScanAsync(default);   // installs and arms the recorder
        await k.CheckRecorderGames(false);
        Assert.True(ArmedWithLedger(_game));
        var walks = 0;
        k.FullAntiCheatCheck = g => { Interlocked.Increment(ref walks); return Core.Games.GameFiles.DetectAntiCheat(g); };
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        await Until(() => Revoked(Path.Combine(_exeDir, ScsKiller.ArmedFile)));   // the watcher's event
        await Until(() => !k.DisarmQueued(_game));
        k.ReconcileRecorders();
        Assert.Equal(1, walks);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.False(ArmedWithLedger(_game));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        k.StopWatchingInstalls();
    }

    /// <summary>What ReShade, a mod, an RE Engine game and Ubisoft Connect write beside the exe at every launch and exit
    /// (ReShade.log1 when another ReShade holds ReShade.log, RE Engine's shader.cache2, uplay_install.state): the recorder
    /// stays armed and nothing is walked.</summary>
    [Fact]
    public async Task A_mods_logs_settings_and_caches_keep_the_recorder_armed()
    {
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);
        Assert.True(ArmedWithLedger(_game));
        var walks = 0;
        k.FullAntiCheatCheck = g => { Interlocked.Increment(ref walks); return Core.Games.GameFiles.DetectAntiCheat(g); };
        foreach (var name in new[] { "ReShade.log", "ReShade.log1", "ReShade.ini", "ReShadePreset.ini", "renodx.log", "shader.cache2", "exception_00.dmp", "uplay_install.state" })
            File.WriteAllText(Path.Combine(_exeDir, name), "data");
        await AssertStaysArmed(k, _game);
        await k.CheckRecorderGames(false);
        File.WriteAllText(Path.Combine(_exeDir, "uplay_install.state"), "rewritten at the next launch");
        await k.CheckRecorderGames(true);
        Assert.Equal(0, walks);
        Assert.True(ArmedWithLedger(_game));
        k.StopWatchingInstalls();
    }

    /// <summary>A new DLL beside the exe disarms at once, even before anything is written in it, and the next pass walks the
    /// install and arms it again.</summary>
    [Fact]
    public async Task A_new_dll_beside_the_exe_disarms_until_the_install_is_checked()
    {
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);
        Assert.True(ArmedWithLedger(_game));
        var walks = 0;
        k.FullAntiCheatCheck = g => { Interlocked.Increment(ref walks); return Core.Games.GameFiles.DetectAntiCheat(g); };
        File.Create(Path.Combine(_exeDir, "mod.dll")).Dispose();
        await Until(() => Revoked(Path.Combine(_exeDir, ScsKiller.ArmedFile)));   // the watcher's event
        await Until(() => !k.DisarmQueued(_game));
        await k.CheckRecorderGames(false);
        Assert.Equal(1, walks);
        await AssertStaysArmed(k, _game);
        k.StopWatchingInstalls();

        var stamp = ScsKiller.FolderStamp(_game);
        File.WriteAllText(Path.Combine(_exeDir, "ReShade.log1"), "data");
        Assert.Equal(stamp, ScsKiller.FolderStamp(_game));
        File.Create(Path.Combine(_exeDir, "guard.dat")).Dispose();   // a type not known to be data, by name
        Assert.NotEqual(stamp, ScsKiller.FolderStamp(_game));
    }

    /// <summary>A watched run that left neither the recorder's log nor a refusal, with the recorder in place before it, is
    /// one the recorder never saw (it didn't load, or the game made no D3D12 device through it).</summary>
    [Fact]
    public void A_run_the_recorder_left_nothing_from_is_one_it_never_saw()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var run = new PlayWindow(t0, t0.AddMinutes(2));
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var log = Path.Combine(_exeDir, "scskiller.log");
        var refused = ScsKiller.LedgerFile(_game.ExePath) + ".refused";
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, run));   // no recorder
        File.WriteAllBytes(dll, [1]);
        File.SetCreationTimeUtc(dll, t0.AddDays(-1));
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, null));
        Assert.True(ScsKiller.NeverSaw(_game.ExePath, _exeDir, run));

        File.WriteAllText(log, "loaded");
        File.SetLastWriteTimeUtc(log, t0.AddDays(-1));   // an earlier launch's
        Assert.True(ScsKiller.NeverSaw(_game.ExePath, _exeDir, run));
        File.SetLastWriteTimeUtc(log, t0.AddSeconds(5));
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, run));
        File.Delete(log);

        Directory.CreateDirectory(Path.GetDirectoryName(refused)!);
        File.WriteAllText(refused, "0 not armed");
        File.SetLastWriteTimeUtc(refused, t0.AddSeconds(5));
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, run));
        File.Delete(refused);

        File.SetCreationTimeUtc(dll, t0.AddMinutes(5));   // installed after the run
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, run));
    }

    /// <summary>A launch the recorder never saw shows in the list as played but not recorded, not as a request to play 5
    /// minutes, and the page asks for a report; once the recorder logs a later launch, the request is back.</summary>
    [Fact]
    public async Task A_launch_the_recorder_never_saw_isnt_a_request_for_5_minutes()
    {
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner());
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        File.SetCreationTimeUtc(Path.Combine(_exeDir, "d3d12.dll"), DateTime.UtcNow.AddHours(-1));
        var rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = new PlayWindow(DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now.AddMinutes(-5));
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(GameStatus.NeedsRecording, s.Status);
        Assert.True(ScsKiller.NeverRecorded(s));
        Assert.Equal("The recorder didn't load in the last launch. Another tool may load DirectX 12 before it. Please report it with the game's name.", s.RecorderRefused);
        Assert.Null(Format.ShortNote(s));   // the title says it

        File.WriteAllText(Path.Combine(_exeDir, "scskiller.log"), "loaded");
        k.RefreshGame(_game.Id);
        s = k.Games.Single();
        Assert.False(ScsKiller.NeverRecorded(s));
        Assert.Null(s.RecorderRefused);
        Assert.Equal("Recorder on: play 5 minutes", Format.ShortNote(s));
        Assert.StartsWith("recorder on: play for about 5 minutes", Format.RecorderOnNote(s));
    }

    /// <summary>The exe changed since the recorder was armed and the proxy refused its launch: the watcher arms it anew, and
    /// the page says the game changed, not that the recorder didn't load.</summary>
    [Fact]
    public async Task A_launch_refused_after_an_update_says_the_game_changed()
    {
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner());
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        Assert.True(await ArmedOnceWritten(_game));
        File.SetCreationTimeUtc(Path.Combine(_exeDir, "d3d12.dll"), DateTime.UtcNow.AddHours(-1));
        File.WriteAllBytes(_game.ExePath, new byte[8192]);   // updated in place: no name event, the proxy's fingerprint refuses it
        var rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = new PlayWindow(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now);
        k.Store.SaveGame(_game.Id, rec);
        File.WriteAllText(ScsKiller.LedgerFile(_game.ExePath) + ".refused", $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} not armed");
        await k.CheckRecorderGames(false);   // disarms for the changed exe; an arming that meets its background disarm waits for the next pass
        await Until(() => !k.DisarmQueued(_game));
        await k.CheckRecorderGames(false);
        await AssertStaysArmed(k, _game);
        Assert.Contains($"recorder armed for {Path.GetFileName(_game.ExePath)}", RecordersLog());
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal("The game changed since the recorder was set up. It is set up again: play again.", s.RecorderRefused);
        Assert.True(ScsKiller.NeverRecorded(s));
        Assert.Null(Format.ShortNote(s));

        // a later launch that recorded: the refusal left from before it isn't its
        var later = DateTimeOffset.Now.AddMinutes(1);
        rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = new PlayWindow(later, later.AddMinutes(5));
        k.Store.SaveGame(_game.Id, rec);
        var log = Path.Combine(_exeDir, "scskiller.log");
        File.WriteAllText(log, "loaded");
        File.SetLastWriteTimeUtc(log, later.UtcDateTime.AddSeconds(10));
        k.RefreshGame(_game.Id);
        Assert.Null(k.Games.Single().RecorderRefused);
        k.StopWatchingInstalls();
    }

    /// <summary>Clearing the recording deletes the recorder's log: the last run, which it recorded, isn't then reported as
    /// one it never saw.</summary>
    [Fact]
    public async Task A_cleared_recording_doesnt_report_the_last_run_unseen()
    {
        var k = Managed();
        await k.ScanAsync(default);   // installs the recorder
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        File.SetCreationTimeUtc(dll, DateTime.UtcNow.AddHours(-1));
        var rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = new PlayWindow(DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now.AddMinutes(-5));
        k.Store.SaveGame(_game.Id, rec);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.log"), "loaded");   // the run was recorded
        k.RefreshGame(_game.Id);
        Assert.Null(k.Games.Single().RecorderRefused);
        Assert.True(k.ClearRecording(_game.Id));
        Assert.Null(k.Games.Single().RecorderRefused);
        k.StopWatchingInstalls();
    }

    /// <summary>A Streamline game (sl.interposer.dll beside the exe) whose exe pulls in no d3d12.dll through its static imports:
    /// on NVIDIA its first d3d12.dll comes with Streamline's plugins, so it gets no recorder (an installed one goes) and asks
    /// for no recording. <paramref name="chain"/>: the exe imports a DLL of its folder that imports d3d12.dll (The Witcher 3's
    /// GFSDK_SSAO_D3D12), which loads the recorder first.</summary>
    [Theory]
    [InlineData(GpuVendor.Nvidia, false, true)]
    [InlineData(GpuVendor.Amd, false, false)]
    [InlineData(GpuVendor.Nvidia, true, false)]
    public async Task A_streamline_only_game_on_nvidia_is_not_recorded(GpuVendor vendor, bool chain, bool skipped)
    {
        const string ssao = "GFSDK_SSAO_D3D12.win64.dll";
        File.WriteAllBytes(_game.ExePath, DiscoveryAndVendorTests.Exe(chain ? ssao : "sl.interposer.dll"));
        File.WriteAllBytes(Path.Combine(_exeDir, "sl.interposer.dll"), Planning.MiddlewarePackTests.Pe(null));
        if (chain) File.WriteAllBytes(Path.Combine(_exeDir, ssao), DiscoveryAndVendorTests.Exe("d3d12.dll"));
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        File.Copy(_proxy, dll);   // installed before the rule
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner(), vendor: new FakeVendor(Gpu with { Vendor = vendor }));
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal(skipped ? ScsKiller.SkipStreamline : null, s.RecorderSkip);
        Assert.Equal(!skipped, File.Exists(dll));
        Assert.Equal(skipped ? GameStatus.Unsupported : GameStatus.NeedsRecording, s.Status);
        if (skipped) Assert.Contains(ScsKiller.StreamlineNoRecording, s.StatusReason);
        Assert.False(s.NotPlanned);   // no anti-cheat: "Not supported yet"
        k.StopWatchingInstalls();
    }

    /// <summary>The cached answer follows the DLLs it was read from, not only the exe: sl.interposer.dll removed, or a DLL the
    /// exe imports updated to import d3d12.dll, makes the game recordable again at the next refresh.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_streamline_verdict_follows_the_dlls_it_was_read_from(bool update)
    {
        var wrap = Path.Combine(_exeDir, "wrap.dll");
        File.WriteAllBytes(_game.ExePath, DiscoveryAndVendorTests.Exe("wrap.dll"));
        File.WriteAllBytes(wrap, Planning.MiddlewarePackTests.Pe(null));
        var interposer = Path.Combine(_exeDir, "sl.interposer.dll");
        File.WriteAllBytes(interposer, Planning.MiddlewarePackTests.Pe(null));
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner(), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        await k.ScanAsync(default);
        Assert.Equal(ScsKiller.SkipStreamline, k.Games.Single().RecorderSkip);
        if (update)
        {
            File.WriteAllBytes(wrap, DiscoveryAndVendorTests.Exe("d3d12.dll"));
            File.SetLastWriteTimeUtc(wrap, DateTime.UtcNow.AddMinutes(1));
        }
        else File.Delete(interposer);
        k.RefreshGame(_game.Id);
        Assert.Null(k.Games.Single().RecorderSkip);
    }

    /// <summary>An import that isn't in the exe's folder is followed in System32 (API sets aside); one found nowhere, or a DLL
    /// of the folder that forwards an export to d3d12, leaves the game recordable.</summary>
    [Fact]
    public void A_streamline_verdict_follows_system32_imports_and_forwarders()
    {
        var system = Directory.CreateDirectory(Path.Combine(_root, "System32")).FullName;
        File.WriteAllBytes(Path.Combine(_exeDir, "sl.interposer.dll"), Planning.MiddlewarePackTests.Pe(null));
        bool? First(string import)
        {
            File.WriteAllBytes(_game.ExePath, DiscoveryAndVendorTests.Exe(import));
            return Core.Games.GameFiles.StreamlineFirst(_game.ExePath, []);
        }
        var was = Core.Games.GameFiles.SystemDir;
        try
        {
            Core.Games.GameFiles.SystemDir = system;
            File.WriteAllBytes(Path.Combine(system, "plain.dll"), Planning.MiddlewarePackTests.Pe(null));
            Assert.True(First("plain.dll"));
            Assert.True(First("api-ms-win-core-synch-l1-2-0.dll"));
            File.WriteAllBytes(Path.Combine(system, "uses12.dll"), DiscoveryAndVendorTests.Exe("d3d12.dll"));
            Assert.False(First("uses12.dll"));   // the loader looks for its d3d12.dll in the exe's folder first
            Assert.Null(First("nowhere.dll"));

            var forwarder = Planning.MiddlewarePackTests.Pe("wrap.dll");
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(forwarder.AsSpan(0x58 + 116), 0x100);   // export directory size
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(forwarder.AsSpan(0x200 + 20), 1);         // NumberOfFunctions
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(forwarder.AsSpan(0x200 + 28), 0x1080);   // AddressOfFunctions
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(forwarder.AsSpan(0x280), 0x10A0);        // inside the directory: a forwarder
            "d3d12.D3D12CreateDevice"u8.CopyTo(forwarder.AsSpan(0x2A0));
            File.WriteAllBytes(Path.Combine(_exeDir, "wrap.dll"), forwarder);
            Assert.False(First("wrap.dll"));
        }
        finally { Core.Games.GameFiles.SystemDir = was; }
    }
}
