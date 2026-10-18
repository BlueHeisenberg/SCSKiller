using System.Diagnostics;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// The recorder records only while its scskiller.ini carries the exe's stamp, so every path that leaves it installed in a
// clean game must leave it stamped; 1.2's attestation (the copy's armed\ ledger, scskiller.armed beside the exe)
// goes at the first start.
public partial class AppTests
{
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Record_all_games_stamps_a_fresh_install_before_the_scan_returns(bool recorded)
    {
        var k = Managed();
        if (recorded) Recorded(k, _game);
        await k.ScanAsync(default);   // "record all games" installs it
        await k.RecordingMigration.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Equal(recorded, File.Exists(Path.Combine(_exeDir, Recordings.KeysFile)));
        Assert.True(Attested(_game));
    }

    /// <summary>The first start of 1.3.0 over 1.2: the copy's armed\ ledger goes before anything else runs (every 1.2
    /// proxy is a pass-through without it), then the scan's reconcile updates the proxy, stamps its ini and takes 1.2's armed
    /// files beside the exe out.</summary>
    [Fact]
    public async Task The_first_start_deletes_the_ledger_then_updates_and_stamps_the_recorder()
    {
        var ledger = Directory.CreateDirectory(ScsKiller.LedgerDir).FullName;
        File.WriteAllText(Path.Combine(ledger, "0123456789abcdef0123456789abcdef01234567"), "[scskiller]\r\nnonce=00\r\n");
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        File.WriteAllBytes(dll, [.. "MZ older proxy SCSKiller_StartWarm "u8]);   // an earlier build's
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.ArmedFile), "[scskiller]\r\narmed=1\r\nnonce=00\r\n");
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.ArmedFile + ".0011.revoked"), "");
        var k = Managed();
        Assert.False(Directory.Exists(ledger));
        Recorded(k, _game);
        await k.ScanAsync(default);
        await k.RecordingMigration.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(dll));
        Assert.True(Attested(_game));
        Assert.Empty(Directory.GetFiles(_exeDir, ScsKiller.ArmedFile + "*"));
    }

    /// <summary>A ledger entry held without delete sharing (an antivirus, an indexer) keeps the first start from deleting
    /// the ledger: each entry gets a new &lt;entry&gt;.revoked beside it, on which its 1.2 proxy refuses, and the next start
    /// deletes it all.</summary>
    [Fact]
    public void A_held_ledger_entry_is_revoked_and_the_ledger_goes_at_a_later_start()
    {
        var ledger = Directory.CreateDirectory(ScsKiller.LedgerDir).FullName;
        var entry = Path.Combine(ledger, "0123456789abcdef0123456789abcdef01234567");
        File.WriteAllText(entry, "[scskiller]\r\nnonce=00\r\n");
        using (new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Managed();
            Assert.True(File.Exists(entry + ".revoked"));
            Assert.Contains("its entries are revoked", RecordersLog());
        }
        Managed();
        Assert.False(Directory.Exists(ledger));
    }

    [Fact]
    public async Task Turning_the_recorder_on_stamps_it()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.Settings = k.Settings with { RecordAllGames = false };
        k.ManageRecorders = true;
        Recorded(k, _game);
        await k.ScanAsync(default);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        k.SetRecorderOverride(_game.Id, RecorderOverride.On);
        await k.RecordingMigration.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Attested(_game));
    }

    /// <summary>The ini Install writes first carries no stamp: while its full checks run, a launch is a pass-through.</summary>
    [Fact]
    public async Task The_ini_carries_no_stamp_until_the_installs_last_check()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        bool? stamped = null;
        k.InstallStep = step => { if (step == "keys") stamped = File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")).Contains("exe_size="); };
        k.InstallRecorder(_game.Id);
        Assert.False(stamped);
        Assert.True(Attested(_game));
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
    public async Task A_game_with_anti_cheat_never_gets_the_recorder()
    {
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames();
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    /// <summary>SCSKiller's own uninstall takes the recorder out, its ini and a 1.2 armed file left beside it too.</summary>
    [Fact]
    public async Task Uninstall_removes_the_recorder_and_a_left_armed_file()
    {
        var k = Managed();
        await k.ScanAsync(default);
        await k.RecorderIndexing.WaitAsync(HangGuard);   // the stamp's background index reads the proxy: a delete meanwhile fails
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.ArmedFile), "[scskiller]\r\narmed=1\r\n");
        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ArmedFile)));
    }

    [Fact]
    public async Task A_recorder_updated_at_startup_is_stamped()
    {
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        File.WriteAllBytes(dll, [.. "MZ older proxy SCSKiller_StartWarm "u8]);   // an earlier build's, no stamp
        var k = Managed();
        Recorded(k, _game);
        await k.ScanAsync(default);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(dll));
        Assert.True(Attested(_game));
    }

    /// <summary>A recorder as 1.2.5 left it: its record names the proxy by hash and the exe, and its ledger entry and armed
    /// file are there. The folder has no scskiller.ini, one the record tracks, or one it doesn't (an earlier data folder wrote
    /// it, then a fresh one took over). The first start updates and stamps it, and SCSKiller's uninstall takes the ini out
    /// with the proxy. An ini with one line SCSKiller never writes stays the user's: never stamped, left by the uninstall.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("tracked")]
    [InlineData("untracked")]
    [InlineData("untracked, a line of the user's")]
    public async Task A_12_recorder_as_12_saved_it_is_stamped_and_the_uninstall_takes_its_ini(string ini)
    {
        var ledger = Directory.CreateDirectory(ScsKiller.LedgerDir).FullName;
        File.WriteAllText(Path.Combine(ledger, "0123456789abcdef0123456789abcdef01234567"), "[scskiller]\r\nnonce=00\r\n");
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        byte[] old = [.. "MZ 1.2 proxy SCSKiller_StartWarm "u8];
        File.WriteAllBytes(dll, old);
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.ArmedFile), "[scskiller]\r\narmed=1\r\nnonce=00\r\n");
        // 1.2.5's IniText, byte for byte: the 1 GB limit less what SCSKiller held then
        var text = "[scskiller]\r\n; written by SCSKiller: record the pipelines this game creates. Removed by 'uninstall recorder'.\r\nmode=record\r\n"
                   + "; the recording limit per game (Settings): no new records once scskiller.db has this many bytes\r\nmax_db_bytes=973741824\r\n";
        Assert.Equal(245, text.Length);
        var foreign = ini.EndsWith("the user's");
        if (foreign) text += "; mine\r\n";
        var path = Path.Combine(_exeDir, "scskiller.ini");
        if (ini != "none") File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(text));
        var store = new AppStore(Path.Combine(_root, "data"));
        var rec = store.LoadGame(_game.Id);
        (rec.Recorder, rec.RecorderExe, rec.RecorderInstallDir) = (RecorderOverride.On, _game.ExePath, _game.InstallDir);
        rec.RecorderFiles["d3d12.dll"] = Convert.ToHexString(SHA256.HashData(old));
        if (ini == "tracked") rec.RecorderFiles["scskiller.ini"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        store.SaveGame(_game.Id, rec);

        var k = Managed();
        await k.ScanAsync(default);
        await k.RecordingMigration.WaitAsync(TimeSpan.FromSeconds(10));
        await k.RecorderIndexing.WaitAsync(HangGuard);   // the stamp's background index reads the proxy: a delete meanwhile fails
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(dll));
        Assert.Equal(!foreign, Attested(_game));
        Assert.Empty(Directory.GetFiles(_exeDir, ScsKiller.ArmedFile + "*"));

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.False(File.Exists(dll));
        if (foreign) Assert.Equal(text, File.ReadAllText(path));
        else Assert.False(File.Exists(path));
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
        var refused = ScsKiller.RefusedFile(_game.ExePath);
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, "d3d12.dll", run));   // no recorder
        File.WriteAllBytes(dll, [1]);
        File.SetCreationTimeUtc(dll, t0.AddDays(-1));
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, "d3d12.dll", null));
        Assert.True(ScsKiller.NeverSaw(_game.ExePath, _exeDir, "d3d12.dll", run));

        File.WriteAllText(log, "loaded");
        File.SetLastWriteTimeUtc(log, t0.AddDays(-1));   // an earlier launch's
        Assert.True(ScsKiller.NeverSaw(_game.ExePath, _exeDir, "d3d12.dll", run));
        File.SetLastWriteTimeUtc(log, t0.AddSeconds(5));
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, "d3d12.dll", run));
        File.Delete(log);

        Directory.CreateDirectory(Path.GetDirectoryName(refused)!);
        File.WriteAllText(refused, "0 not signed by SCSKiller");
        File.SetLastWriteTimeUtc(refused, t0.AddSeconds(5));
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, "d3d12.dll", run));
        File.Delete(refused);

        File.SetCreationTimeUtc(dll, t0.AddMinutes(5));   // installed after the run
        Assert.False(ScsKiller.NeverSaw(_game.ExePath, _exeDir, "d3d12.dll", run));

        var dll11 = Path.Combine(_exeDir, "d3d11.dll");   // the D3D11 recorder: the same log and refusal
        File.WriteAllBytes(dll11, [1]);
        File.SetCreationTimeUtc(dll11, t0.AddDays(-1));
        Assert.True(ScsKiller.NeverSaw(_game.ExePath, _exeDir, "d3d11.dll", run));
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
    }

    /// <summary>A start with nothing changed walks only the installs of games with the recorder in: the scan goes by its cached
    /// verdict and the folders' stamp, which the recorder's own files don't change, and the reconcile walks the recorder game
    /// in full (anti-cheat put deep in its install while the app was closed changes neither). So does the periodic pass.</summary>
    [Fact]
    public async Task A_start_with_nothing_changed_walks_only_the_recorder_games()
    {
        var first = Managed();
        await first.ScanAsync(default);   // detects, walks, installs and stamps the recorder
        Assert.True(Attested(_game));

        var k = Managed();
        var walks = 0;
        k.FullAntiCheatCheck = g => { Interlocked.Increment(ref walks); return Core.Games.GameFiles.DetectAntiCheat(g); };
        await k.ScanAsync(default);
        Assert.Equal(1, walks);
        Assert.True(Attested(_game));
        await k.CheckRecorderGames();
        Assert.Equal(2, walks);
        Assert.True(Attested(_game));
    }

    /// <summary>What ReShade, a mod, an RE Engine game and Ubisoft Connect write beside the exe at every launch and exit
    /// (ReShade.log1 when another ReShade holds ReShade.log, RE Engine's shader.cache2, uplay_install.state): the recorder
    /// stays stamped.</summary>
    [Fact]
    public async Task A_mods_logs_settings_and_caches_keep_the_recorder_stamped()
    {
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames();
        Assert.True(Attested(_game));
        foreach (var name in new[] { "ReShade.log", "ReShade.log1", "ReShade.ini", "ReShadePreset.ini", "renodx.log", "shader.cache2", "exception_00.dmp", "uplay_install.state" })
            File.WriteAllText(Path.Combine(_exeDir, name), "data");
        Assert.True(Attested(_game));
        k.ReconcileRecorders();
        File.WriteAllText(Path.Combine(_exeDir, "uplay_install.state"), "rewritten at the next launch");
        k.ReconcileRecorders();
        Assert.True(Attested(_game));
    }

    /// <summary>A launch the recorder never saw shows in the list as played but not recorded, not as a request to play 5
    /// minutes, and the page asks for a report, or names DirectX 11 for a game that runs either; once the recorder logs a
    /// later launch, the request is back.</summary>
    [Theory]
    [InlineData("D3D12", ScsKiller.NeverSawNote)]
    [InlineData("D3D11 or D3D12", ScsKiller.NeverSawDx11Note)]
    public async Task A_launch_the_recorder_never_saw_isnt_a_request_for_5_minutes(string api, string note)
    {
        var k = Killer(new FakeReader(Unreal with { GraphicsApi = api }), new NeedsRecordingPlanner());
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
        Assert.Equal(note, s.RecorderRefused);
        Assert.Null(Format.ShortNote(s));   // the title says it

        File.WriteAllText(Path.Combine(_exeDir, "scskiller.log"), "loaded");
        k.RefreshGame(_game.Id);
        s = k.Games.Single();
        Assert.False(ScsKiller.NeverRecorded(s));
        Assert.Null(s.RecorderRefused);
        Assert.Equal("Recorder on: play 5 minutes", Format.ShortNote(s));
        Assert.StartsWith("recorder on: play for about 5 minutes", Format.RecorderOnNote(s));
    }

    /// <summary>The exe changed since the recorder was stamped and the proxy refused its launch: the watcher's pass stamps it anew, and
    /// the page says the game changed, not that the recorder didn't load.</summary>
    [Fact]
    public async Task A_launch_refused_after_an_update_says_the_game_changed()
    {
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner());
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        Assert.True(Attested(_game));
        File.SetCreationTimeUtc(Path.Combine(_exeDir, "d3d12.dll"), DateTime.UtcNow.AddHours(-1));
        File.WriteAllBytes(_game.ExePath, new byte[8192]);   // updated in place: the proxy's stamp refuses it
        var rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = new PlayWindow(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now);
        k.Store.SaveGame(_game.Id, rec);
        Directory.CreateDirectory(Path.GetDirectoryName(ScsKiller.RefusedFile(_game.ExePath))!);
        File.WriteAllText(ScsKiller.RefusedFile(_game.ExePath), $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} exe not as checked");
        await k.CheckRecorderGames();
        Assert.True(Attested(_game));
        Assert.Contains($"the recorder's scskiller.ini didn't match {Path.GetFileName(_game.ExePath)}", RecordersLog());
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
    }

    /// <summary>PRAGMATA's protection crashes it while the recorder's d3d12.dll is in its folder, armed or not: its id gets
    /// the refusal, a recorder already there goes, and no install, before and after pair or session puts one back. Its
    /// compile is unchanged. Another game keeps its recorder.</summary>
    [Theory]
    [InlineData("steam:3357650", true)]
    [InlineData("test:other", false)]
    public async Task Pragmata_gets_no_recorder_and_still_compiles(string id, bool refused)
    {
        var game = _game with { Id = id };
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        File.Copy(_proxy, dll);
        var k = Killer(new FakeReader(Unreal), game: game);
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        k.Settings = k.Settings with { RecordAllGames = true };
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, refused ? "the recorder crashes this game, so it's off for now" : null, !refused),
            (s.Status, s.RecorderSkip, File.Exists(dll)));
        if (refused)
        {
            Assert.Contains(ScsKiller.SkipCrashes, Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(id)).Message);
            Assert.Contains(ScsKiller.SkipCrashes, Assert.Throws<InvalidOperationException>(() => k.StartBeforeAfter(id, 5)).Message);
            k.ReconcileRecorders();
            Assert.DoesNotContain(Names(_exeDir), n => n is "d3d12.dll" or "d3d11.dll" or ScsKiller.ArmedFile);
        }
    }

    [Theory]
    [InlineData(@"C:\g\ACValhalla.exe", true)]
    [InlineData(@"C:\g\ACValhalla_Plus.exe", true)]
    [InlineData(@"C:\g\acmirage.exe", true)]
    [InlineData(@"C:\g\ACOrigins.exe", false)]
    public void Assassins_Creed_exes_that_dont_start_with_it_get_no_recorder(string exe, bool refused) =>
        Assert.Equal(refused, ScsKiller.RecorderCrashes(_game with { Id = "test:ac", ExePath = exe }));

    /// <summary>PRAGMATA flagged with anti-cheat still gets no session at the user's own risk: the recorder never goes in.</summary>
    [Fact]
    public async Task Pragmata_gets_no_risk_session()
    {
        var game = _game with { Id = "steam:3357650" };
        File.WriteAllBytes(Path.Combine(_exeDir, "BEService_x64.exe"), [0]);
        var k = Killer(new FakeReader(Unreal), game: game);
        k.Settings = k.Settings with { RecordAntiCheatGames = true };
        await k.ScanAsync(default);
        Assert.False(k.Games.Single().RiskEligible);
        Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(game.Id, true));
        Assert.Throws<InvalidOperationException>(() => { _ = k.StartRiskSession(game.Id, confirmed: true, AntiCheat.BattlEye); });
        Assert.DoesNotContain("d3d12.dll", Names(_exeDir));
    }

    /// <summary>PRAGMATA listed for offline EasyAntiCheat sessions still gets none: the recorder never goes in.</summary>
    [Fact]
    public async Task Pragmata_gets_no_offline_session()
    {
        const string id = "steam:3357650";
        Directory.CreateDirectory(Path.Combine(_exeDir, "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(_exeDir, "start_protected_game.exe"), [0]);
        var k = Killer(new FakeReader(Unreal), game: _game with { Id = id });
        k.ProcessNames = () => new HashSet<string> { "steam" };
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            Assert.Equal((AntiCheat.EasyAntiCheat, false), (k.Games.Single().AntiCheat, k.Games.Single().OfflineEligible));
            Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(id, true));
            Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(id, confirmed: true); });
        }, id: id);
        Assert.DoesNotContain("d3d12.dll", Names(_exeDir));
    }

    /// <summary>SCP: Secret Laboratory doesn't start with a dx*.dll in its folder: by its Steam id (VAC) or its exe anywhere,
    /// no recorder, frames-only pair, risk or offline session goes in on either GPU, and its page says why.</summary>
    [Theory]
    [InlineData("steam:700330", null, GpuVendor.Nvidia, true)]
    [InlineData("steam:700330", null, GpuVendor.Amd, true)]
    [InlineData("test:scpsl", "SCPSL.exe", GpuVendor.Nvidia, false)]
    public async Task Scp_secret_laboratory_gets_no_recorder_in_any_mode(string id, string? exe, GpuVendor vendor, bool vac)
    {
        var game = (exe != null ? NamedAs(exe) : _game) with { Id = id };
        File.Copy(_proxy, Proxy11);
        var k = Killer11(vendor == GpuVendor.Amd ? "amd-1" : "nvidia-1", vendor, games: [game]);
        k.VacCheck = _ => vac;
        k.Settings = k.Settings with { RecordAntiCheatGames = true };
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        var s = k.Games.Single();
        Assert.Equal((false, false), (s.RiskEligible, s.OfflineEligible));
        Assert.NotNull(k.BeforeAfterBlocked(id));
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(id));
        Assert.Throws<InvalidOperationException>(() => k.StartBeforeAfter(id, 5));
        Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(id, true));
        Assert.Contains("isn't available for this game", Assert.Throws<InvalidOperationException>(() => { _ = k.StartRiskSession(id, confirmed: true, AntiCheat.None); }).Message);
        Assert.DoesNotContain(Names(_exeDir), n => n is "d3d12.dll" or "d3d11.dll" or ScsKiller.ArmedFile);
        Assert.Equal($"Not available: {game.Name} doesn't start with the recorder in its folder.", ScsKiller.CrashNote(game));
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

    /// <summary>A launch through a junction to the exe's folder: the proxy resolves the folder as it is on disk, so root= names
    /// the real install root and its markers count. The same ini beside a hard link of the exe in another folder of that
    /// install (the same exe, size, time and root) is refused: the stamp names the exe's folder too.</summary>
    [Fact]
    public void The_proxy_checks_the_real_folders_of_a_launch_through_a_link()
    {
        if (OwnWarmExe() == null) return;
        var game = Directory.CreateDirectory(Path.Combine(_root, "Games", "G")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(game, "bin")).FullName;
        var link = Junction(Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "Launch")).FullName, "G"), bin);
        try
        {
            Assert.Equal(2, Selftest(bin, "anticheat -", root: "..", launchDir: link)!.Value.Computes);
            Directory.CreateDirectory(Path.Combine(game, "EasyAntiCheat"));   // in the real root, not above the link
            Assert.Equal(0, Selftest(bin, "anticheat -", root: "..", launchDir: link)!.Value.Computes);
            Assert.Equal("anti-cheat in the install folder", ScsKiller.Refused(Path.Combine(bin, "selftest.exe")));
        }
        finally { Directory.Delete(link); }   // the junction alone: the cleanup's recursive delete can't go through it

        var other = Directory.CreateDirectory(Path.Combine(game, "bin2")).FullName;
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{Path.Combine(other, "selftest.exe")}\" \"{Path.Combine(bin, "selftest.exe")}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mk.WaitForExit();
        Assert.True(File.Exists(Path.Combine(other, "selftest.exe")));
        var stamp = string.Concat(File.ReadAllLines(Path.Combine(bin, "scskiller.ini")).Where(l => l.Split('=')[0] is "root" or "exe_size" or "exe_time" or "sig").Select(l => l + "\r\n"));
        Assert.Equal(0, Selftest(other, "anticheat -", stamp, stamped: false)!.Value.Computes);
        Assert.Equal("not signed by SCSKiller", ScsKiller.Refused(Path.Combine(other, "selftest.exe")));
    }

    /// <summary>With scanning at start off, a start shows the kept list, which may predate the recorder's install: the pass
    /// goes by the game's record too, and takes the recorder out of a game an update gave anti-cheat deep in its install.</summary>
    [Fact]
    public async Task A_start_from_the_kept_list_still_walks_a_recorder_installed_after_it()
    {
        var first = Killer(new FakeReader(Unreal));
        first.ProcessNames = () => new HashSet<string>();
        first.Settings = first.Settings with { ScanAtStart = false, RecordAllGames = false };
        await first.ScanAsync(default, userRequested: true);   // kept: no recorder
        first.InstallRecorder(_game.Id);
        Assert.True(Attested(_game));

        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "support", "EasyAntiCheat"));
        var k = Killer(new FakeReader(Unreal));   // a restart
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        Assert.False(k.Games.Single().RecorderInstalled);   // as the kept list says
        await k.CheckRecorderGames();
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    /// <summary>Two recorder games turn out to have anti-cheat, one after the other, while another task holds the recorders (a
    /// compile's import): the first pass ends with its removal still waiting, the next pass refuses the second game's folder
    /// all the same, and both recorders go once the lock is free.</summary>
    [Fact]
    public async Task Every_finding_of_a_pass_refuses_its_folder_before_any_removal_waits()
    {
        var (a, b) = (FakeGame("test:a-" + Guid.NewGuid().ToString("N")[..8], "GameA"), FakeGame("test:b-" + Guid.NewGuid().ToString("N")[..8], "GameB"));
        var k = RecordKiller([a, b]);
        await k.ScanAsync(default);
        k.InstallRecorder(a.Id);
        k.InstallRecorder(b.Id);
        Assert.True(Attested(a) && Attested(b));
        var recorders = typeof(ScsKiller).GetField("_recorderLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(k)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Factory.StartNew(() =>
        {
            lock (recorders) { held.Set(); release.Wait(); }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        bool Refused(Game g) => File.Exists(ScsKiller.Revoked) && File.ReadAllLines(ScsKiller.Revoked).Contains(ScsKiller.FileKey(g.InstallDir));
        var removals = new List<Task>();
        try
        {
            Directory.CreateDirectory(Path.Combine(a.InstallDir, "Data", "EasyAntiCheat"));
            removals.AddRange(await k.WalkRecorderGames().WaitAsync(TimeSpan.FromSeconds(30)));   // ends with A's removal waiting
            await Until(() => Refused(a));
            Directory.CreateDirectory(Path.Combine(b.InstallDir, "Data", "EasyAntiCheat"));
            removals.AddRange(await k.WalkRecorderGames().WaitAsync(TimeSpan.FromSeconds(30)));
            await Until(() => Refused(b));
            Assert.Contains(removals, t => !t.IsCompleted);
        }
        finally { release.Set(); }
        await holder;
        await Task.WhenAll(removals).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(File.Exists(Dll(a)) || File.Exists(Dll(b)));
        Assert.False(File.Exists(ScsKiller.Revoked));
    }

    /// <summary>With ReShade as dxgi.dll and a RenoDX add-on in the game folder: installing and stamping the recorder, a compile
    /// through the copy of that layer, and taking the recorder out add nothing to the install but the recorder's own files
    /// (<see cref="ScsKiller.RecorderOwnFiles"/>, next to the exe) and change no file that was there.</summary>
    [Fact]
    public async Task The_recorder_and_a_compile_through_reshade_write_nothing_into_the_install_but_the_recorders_files()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, "dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(Path.Combine(_exeDir, "renodx-ff7rebirth.addon64"), RenoDxAddon);
        File.WriteAllLines(Path.Combine(_exeDir, "ReShade.ini"), ["[GENERAL]"]);
        Dictionary<string, string> Files() => Directory.EnumerateFiles(_game.InstallDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);
        var before = Files();
        void OnlyOursAdded()
        {
            var now = Files();
            Assert.All(before, f => Assert.Equal(f.Value, now.GetValueOrDefault(f.Key)));
            Assert.All(now.Keys.Except(before.Keys), f => Assert.True(Path.GetDirectoryName(f) == _exeDir && ScsKiller.RecorderOwnFiles.Contains(Path.GetFileName(f)), f));
        }

        var k = Managed();
        await k.ScanAsync(default);
        Assert.True(Attested(_game));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        await k.RecorderIndexing.WaitAsync(HangGuard);   // it writes scskiller.keys through a temp file
        OnlyOursAdded();

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.NotNull(k.Store.LoadGame(_game.Id).WarmedLayer);   // it ran through the layer's copy
        await k.RecorderIndexing.WaitAsync(HangGuard);
        OnlyOursAdded();

        k.UninstallRecorder(_game.Id);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        OnlyOursAdded();
    }

    /// <summary>scskiller.ini as 1.2 (and every version before 1.3) wrote it, with the recording limit.</summary>
    const string Ini12 = "[scskiller]\r\n; written by SCSKiller: record the pipelines this game creates. Removed by 'uninstall recorder'.\r\nmode=record\r\n"
        + "; the recording limit per game (Settings): no new records once scskiller.db has this many bytes\r\nmax_db_bytes=1073741824\r\n";

    const string Next12 = "; the game's own d3d12.dll (a mod), renamed by SCSKiller and put back when the recorder is removed\r\nnext=" + ScsKiller.ChainName + "\r\n";

    /// <summary>A game's recorder as 1.2.4 left it: an earlier build's proxy under <paramref name="dll"/>, scskiller.armed beside
    /// it, its ledger entry, the scskiller.ini <paramref name="ini"/> (null: none) and the record's recorder fields as 1.2 wrote
    /// them into state.json, its RecorderFiles naming the dll only (Install found an ini it didn't track), or the ini too by
    /// <paramref name="iniHash"/>.</summary>
    void Recorder12(string dll, string? ini, string? iniHash = null, string? chainedSha = null, bool rollback = false)
    {
        var proxy = Path.Combine(_exeDir, dll);
        File.WriteAllBytes(proxy, [.. "MZ older proxy SCSKiller_StartWarm "u8]);
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.ArmedFile), "[scskiller]\r\narmed=1\r\nnonce=00\r\n");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(ScsKiller.LedgerDir).FullName, ScsKiller.FileKey(_game.ExePath)), "[scskiller]\r\nnonce=00\r\n");
        if (ini != null) File.WriteAllText(Path.Combine(_exeDir, "scskiller.ini"), ini);
        var files = $"\"{dll}\": \"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(proxy)))}\"" + (iniHash == null ? "" : $", \"scskiller.ini\": \"{iniHash}\"");
        var state = Path.Combine(new AppStore(Path.Combine(_root, "data")).GameDir(_game.Id), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, $$"""
            {
              "RecorderFiles": { {{files}} },
              "RecorderExe": {{System.Text.Json.JsonSerializer.Serialize(_game.ExePath)}},
              "RecorderInstallDir": {{System.Text.Json.JsonSerializer.Serialize(_game.InstallDir)}},
              "RecorderMoveFrom": null,
              "Recorder": "{{(rollback ? "Off" : "On")}}",
              "RecordAlongsideMod": {{(chainedSha != null ? "true" : "false")}},
              "RecorderChained": {{(chainedSha == null ? "null" : $"{{ \"Name\": \"{ScsKiller.ChainName}\", \"Sha256\": \"{chainedSha}\" }}")}},
              "RecorderRollback": {{(rollback ? "true" : "false")}},
              "OfflineRecord": false,
              "OfflineSession": null,
              "KeysPending": false
            }
            """);
    }

    /// <summary>The first 1.3 start over a 1.2 install whose recorders were armed: every one is stamped (the proxy, run as the
    /// game, admits it), or explicitly not, with the game page saying why, and a user's ini is never rewritten or deleted. An
    /// ini 1.2 found and didn't track is SCSKiller's when every line of it is; the same holds after a crash between the
    /// ini's write and the record's save. A second start changes nothing, and SCSKiller's uninstall takes out everything it
    /// put there. A 1.2 record halfway through a removal (the recorder turned off while the game held it) finishes it.</summary>
    [Theory]
    [InlineData("ini")]           // RecorderFiles: the dll only; its ini there, untracked
    [InlineData("noini")]         // the dll only, no ini
    [InlineData("d3d11")]         // a D3D11 recorder, its ini untracked
    [InlineData("chained")]       // chained to a mod's d3d12.dll (next=), the ini untracked
    [InlineData("interrupted")]   // a crash after the record named the ini's next bytes (with its old ones), before the write
    [InlineData("edited")]        // tracked, and the user changed a value since: theirs, left and not stamped
    [InlineData("user")]          // SCSKiller's ini with a line the user added: left, not stamped
    [InlineData("other")]         // another program's scskiller.ini: left, not stamped
    [InlineData("rollback")]      // a removal 1.2 hadn't finished
    public async Task The_first_start_over_a_1_2_install_stamps_every_armed_recorder_or_says_why_not(string shape)
    {
        var bin = OwnWarmExe() is { } warm ? Path.GetDirectoryName(warm)! : null;
        if (bin != null)   // the game's exe is the selftest and the app's proxy the built one: the proxy's own admission is checked
        {
            File.Copy(Path.Combine(bin, "selftest.exe"), _game.ExePath, true);
            File.Copy(Path.Combine(bin, "d3d12.dll"), _proxy, true);
        }
        string? mod = null;
        var user = shape switch
        {
            "user" => Ini12 + "markers=Custom.exe\r\n",
            "other" => "[scskiller]\r\nmode=record\r\nframes=0\r\n",
            "edited" => Ini12.Replace("=1073741824", "=2147483648"),
            _ => null,
        };
        static string Sha(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(text)));
        switch (shape)
        {
            case "noini": Recorder12(ScsKiller.Dll12, null); break;
            case "d3d11": Recorder12(ScsKiller.Dll11, Ini12); break;
            case "chained":
                mod = Path.Combine(_exeDir, ScsKiller.ChainName);
                if (bin != null) File.Copy(Path.Combine(bin, "fakenext.dll"), mod);
                else File.WriteAllBytes(mod, Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray()));
                Recorder12(ScsKiller.Dll12, Ini12 + Next12, chainedSha: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mod))));
                break;
            case "interrupted": Recorder12(ScsKiller.Dll12, Ini12, iniHash: Sha("[scskiller]\r\nmode=record\r\n") + "|" + Sha(Ini12)); break;
            case "edited": Recorder12(ScsKiller.Dll12, user, iniHash: Sha(Ini12)); break;
            case "rollback": Recorder12(ScsKiller.Dll12, Ini12, rollback: true); break;
            default: Recorder12(ScsKiller.Dll12, user ?? Ini12); break;
        }
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var dll = Path.Combine(_exeDir, shape == "d3d11" ? ScsKiller.Dll11 : ScsKiller.Dll12);
        var engine = shape == "d3d11" ? Unreal with { GraphicsApi = "D3D11" } : Unreal;
        var stamped = shape is not ("user" or "other" or "edited" or "rollback");

        for (var start = 0; start < 2; start++)   // the first start, then one more: the same outcome
        {
            var k = Killer(new FakeReader(engine), vendor: shape == "d3d11" ? new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }, profile: "amd-1") : null);   // AMD: D3D11 records
            k.ProcessNames = () => new HashSet<string>();
            k.ManageRecorders = true;
            await k.ScanAsync(default);
            await k.RecordingMigration.WaitAsync(HangGuard);
            await k.RecorderIndexing.WaitAsync(HangGuard);
            Assert.False(Directory.Exists(ScsKiller.LedgerDir));
            Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ArmedFile)));
            Assert.True(stamped == Attested(_game), RecordersLog());
            var rec = k.Store.LoadGame(_game.Id);
            if (stamped)
            {
                Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(dll));
                Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ini))), Entry()!.Tracked!.Split('|'));
                Assert.False(rec.RecorderFiles.ContainsKey("scskiller.ini"));   // moved to its folder's entry
                Assert.Equal(shape == "chained", File.ReadAllText(ini).Contains("next=" + ScsKiller.ChainName + "\r\n"));
                Assert.Null(k.Games.Single().RecorderNote);
            }
            else if (shape == "rollback")
            {
                Assert.False(File.Exists(dll));
                Assert.False(File.Exists(ini));
                Assert.Empty(rec.RecorderFiles);
                Assert.Null(Entry());
            }
            else
            {
                Assert.Equal(user, File.ReadAllText(ini));
                Assert.Null(Entry()?.Tracked);
                Assert.Equal(ScsKiller.NoteForeignIni, k.Games.Single().RecorderNote);
            }
        }

        if (bin != null && shape != "d3d11")   // the proxy, as the game loads it, admits the launch only where the app stamped it
        {
            var start = new ProcessStartInfo(_game.ExePath, "anticheat -") { RedirectStandardOutput = true };
            start.Environment["SCSKILLER_SELFTEST_UNARMED"] = "1";
            using (var p = Process.Start(start)!)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
            }
            var db = Path.Combine(_exeDir, "scskiller.db");
            Assert.Equal(stamped ? 2 : 0, File.Exists(db) ? PsoDb.Read(db).Count(r => r.Tag == 'C') : 0);
            if (stamped) Assert.Null(ScsKiller.Refused(_game.ExePath));
            else if (shape != "rollback") Assert.Equal("exe not as checked", ScsKiller.Refused(_game.ExePath));
        }

        ScsKiller.RemoveAllRecorders(new AppStore(Path.Combine(_root, "data")), new HashSet<string>());
        string[] left = [Path.GetFileName(_game.ExePath), .. mod != null ? [ScsKiller.Dll12] : Array.Empty<string>(), .. user != null ? ["scskiller.ini"] : Array.Empty<string>()];
        Assert.Equal(left.Order(StringComparer.OrdinalIgnoreCase), Directory.GetFiles(_exeDir).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        if (user != null) Assert.Equal(user, File.ReadAllText(ini));
    }

    /// <summary>The folder's scskiller.ini entry (the exe's folder when not given).</summary>
    IniOwner? Entry(string? dir = null) => ScsKiller.GetIni(new AppStore(Path.Combine(_root, "data")), dir ?? _exeDir);

    async Task StartManaged()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
    }

    /// <summary>A 1.2 removal halfway, its untracked ini held open (an antivirus, an editor): the proxy goes; the ini is never
    /// written, stays tracked (by its bytes when they can be read, else as unknown), its folder refused and kept on the record.
    /// Once let go, SCSKiller's uninstall, with no scan before it, takes it out, or a later start does.</summary>
    [Theory]
    [InlineData(FileShare.Read, false)]
    [InlineData(FileShare.None, false)]
    [InlineData(FileShare.ReadWrite, false)]
    [InlineData(FileShare.ReadWrite, true)]
    [InlineData(FileShare.None, true)]
    public async Task An_untracked_1_2_ini_held_open_during_a_removal_goes_later(FileShare share, bool scan)
    {
        Recorder12(ScsKiller.Dll12, Ini12, rollback: true);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var store = new AppStore(Path.Combine(_root, "data"));
        using (new FileStream(ini, FileMode.Open, FileAccess.Read, share))
        {
            await StartManaged();
            Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.Dll12)));
            Assert.Equal(share == FileShare.None ? "" : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(Ini12))), Entry()!.Tracked);
            Assert.True(Entry()!.Remove);
            Assert.Contains(ScsKiller.FileKey(_exeDir), File.ReadAllLines(ScsKiller.Revoked));
        }
        Assert.Equal(Ini12, File.ReadAllText(ini));   // never written
        if (scan) await StartManaged();
        else ScsKiller.RemoveAllRecorders(store, new HashSet<string>());
        Assert.False(File.Exists(ini));
        Assert.Empty(store.LoadGame(_game.Id).RecorderFiles);
        Assert.Null(Entry());
        Assert.False(File.Exists(ScsKiller.Revoked) && File.ReadAllLines(ScsKiller.Revoked).Contains(ScsKiller.FileKey(_exeDir)));
    }

    /// <summary>During a 1.2 removal an untracked scskiller.ini that isn't SCSKiller's, or can't be read yet, is never written
    /// or deleted, however often the removal is tried. Another program's is left as it is, its folder refused while it's
    /// there (a stamp in it, in any shape, may still be valid). SCSKiller's chained one, held without sharing, goes once it
    /// can be read: its folder's entry keeps the chain it names, so the mod is put back at once.</summary>
    [Theory]
    [InlineData("other")]
    [InlineData("other, signed")]
    [InlineData("other, held")]
    [InlineData("chained, held")]
    public async Task An_untracked_ini_is_never_written_or_deleted_until_it_is_known_ours(string kind)
    {
        var chained = kind.StartsWith("chained");
        var mod = Path.Combine(_exeDir, ScsKiller.ChainName);
        if (chained) File.WriteAllBytes(mod, Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray()));
        var text = chained ? Ini12 + Next12 : kind == "other, signed" ? "[Settings]\r\nsig=mine\r\npid=123\r\nexe_size=99\r\nfoo=bar\r\n" : "[Settings]\r\nFoo=1\r\n";
        Recorder12(ScsKiller.Dll12, text, chainedSha: chained ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mod))) : null, rollback: true);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var store = new AppStore(Path.Combine(_root, "data"));
        bool Refused() => File.Exists(ScsKiller.Revoked) && File.ReadAllLines(ScsKiller.Revoked).Contains(ScsKiller.FileKey(_exeDir));
        using (kind.EndsWith("held") ? new FileStream(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
        {
            await StartManaged();
            await StartManaged();
            Assert.Equal(kind.EndsWith("held") ? "" : null, Entry()?.Tracked);
            Assert.True(Refused());
            Assert.Equal(chained, File.Exists(Path.Combine(_exeDir, ScsKiller.Dll12)));   // the proxy went; a chained mod is back already
        }
        Assert.Equal(text, File.ReadAllText(ini));
        if (chained) ScsKiller.RemoveAllRecorders(store, new HashSet<string>());
        else await StartManaged();
        Assert.Equal(!chained, File.Exists(ini));
        if (!chained) Assert.Equal(text, File.ReadAllText(ini));
        Assert.Equal(chained, File.Exists(Path.Combine(_exeDir, ScsKiller.Dll12)));   // the mod, back
        Assert.Empty(store.LoadGame(_game.Id).RecorderFiles);
        Assert.Null(Entry()?.Tracked);
        Assert.Equal(!chained, Refused());
    }

    /// <summary>The ini is read again right before it is written, after the record's save (which may wait for its lock): one
    /// the user changed meanwhile is left as it is, at a restamp and at an install alike, and nothing is stamped.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_ini_changed_while_its_write_waited_is_left_as_it_is(bool install)
    {
        var k = Managed();
        k.Settings = k.Settings with { RecordAllGames = !install };
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var mine = Ini12.Replace("=1073741824", "=1234");   // SCSKiller's shape, a value of the user's: theirs from the change on
        if (install)
        {
            File.WriteAllText(ini, Ini12);   // 1.2's, untracked: SCSKiller's, until the user changes it
            k.InstallStep = step => { if (step == "ini") File.WriteAllText(ini, mine); };
            k.SetRecorderOverride(_game.Id, RecorderOverride.On);
        }
        else
        {
            Assert.True(Attested(_game));
            File.SetLastWriteTimeUtc(_game.ExePath, DateTime.UtcNow.AddMinutes(-5));   // an update: the stamp is taken again
            k.AttestStep = step => { if (step == "saved") File.WriteAllText(ini, mine); };
            k.ReconcileRecorders(_game.Id);
        }
        Assert.Equal(mine, File.ReadAllText(ini));
        Assert.False(Attested(_game));
        (k.InstallStep, k.AttestStep) = (null, null);
        Assert.True(Entry()!.Users);
        k.ReconcileRecorders(_game.Id);   // judged again: the user's
        Assert.Equal(mine, File.ReadAllText(ini));
        Assert.False(Attested(_game));
    }

    /// <summary>An ini update with nothing judging after it (a cleared recording's new limit): an ini the user changed while
    /// the write waited is the user's at once, not only at the next judgement.</summary>
    [Fact]
    public async Task An_ini_changed_while_an_update_waited_is_the_users_at_once()
    {
        var k = Managed();
        await k.ScanAsync(default);
        Assert.True(Attested(_game));
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var mine = File.ReadAllText(ini) + "; mine\r\n";
        k.Settings = k.Settings with { RecordingLimitMB = 32 };
        k.AttestStep = step => { if (step == "saved") File.WriteAllText(ini, mine); };
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.log"), "loaded");
        Assert.True(k.ClearRecording(_game.Id));
        k.AttestStep = null;
        Assert.Equal(mine, File.ReadAllText(ini));
        Assert.True(Entry()!.Users);
    }

    /// <summary>A recorder moving to the game's real exe while its old folder's ini can't be read: the move waits (the record
    /// keeps the old folder, the new one gets nothing yet, the old folder refused), and completes once the ini can be judged
    /// SCSKiller's and taken out.</summary>
    [Fact]
    public async Task A_move_waits_for_an_ini_that_cant_be_read_in_the_old_folder()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "Expedition 33", "Content")).FullName;
        Directory.CreateDirectory(Path.Combine(content, "Engine"));
        var stub = Path.Combine(content, "SandFall.exe");
        File.WriteAllBytes(stub, DiscoveryAndVendorTests.Exe(null, 1000));
        var game = new Game("test:stub-" + Guid.NewGuid().ToString("N")[..8], "Expedition 33", Store.Other, content, stub, "1.0");
        var k = RecordKiller([game]);
        await k.ScanAsync(default);
        k.InstallRecorder(game.Id);
        var old = Path.Combine(content, "scskiller.ini");
        var bin = Directory.CreateDirectory(Path.Combine(content, "Sandfall", "Binaries", "WinGDK")).FullName;
        File.WriteAllBytes(Path.Combine(bin, "SandFall-WinGDK-Shipping.exe"), DiscoveryAndVendorTests.Exe("d3d12.dll", 5000));
        await k.ScanAsync(default);
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
            k.ReconcileRecorders();
        Assert.Equal(stub, k.Store.LoadGame(game.Id).RecorderMoveFrom);
        Assert.False(File.Exists(Path.Combine(bin, "d3d12.dll")));
        Assert.True(File.Exists(old));
        Assert.True(ScsKiller.IniPending(k.Store, content));
        Assert.Contains(ScsKiller.FileKey(content), File.ReadAllLines(ScsKiller.Revoked));
        k.ReconcileRecorders();
        Assert.False(File.Exists(old));
        Assert.Null(Entry(content));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(bin, "d3d12.dll")));
        Assert.Null(k.Store.LoadGame(game.Id).RecorderMoveFrom);
    }

    /// <summary>Anti-cheat found in a game whose recorder is beside another exe than the game's: each folder is taken out with
    /// its own record, so an ini in the recorder's folder that can't be read stays tracked there even though the game's exe
    /// folder has none, and goes at a later removal.</summary>
    [Fact]
    public async Task A_removal_over_two_folders_keeps_each_folders_pending_ini()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var other = Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Other")).FullName;
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderExe = Path.Combine(other, "Other.exe");   // the recorder was set up beside another exe; the game's has its folder
        File.Move(ini, Path.Combine(other, "scskiller.ini"));
        File.Move(Path.Combine(_exeDir, "d3d12.dll"), Path.Combine(other, "d3d12.dll"));
        File.WriteAllBytes(rec.RecorderExe, [0]);
        k.Store.SaveGame(_game.Id, rec);
        ScsKiller.SetIni(k.Store, other, _ => Entry());   // its entry moves with it
        ScsKiller.SetIni(k.Store, _exeDir, _ => null);
        var held = Path.Combine(other, "scskiller.ini");
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
            await k.RescanAsync(default);
            Assert.True(File.Exists(held));
            Assert.True(ScsKiller.IniPending(k.Store, other));
        }
        await k.RescanAsync(default);
        Assert.False(File.Exists(held));
        Assert.Empty(k.Store.LoadGame(_game.Id).RecorderFiles);
        Assert.Null(Entry(other));
    }

    /// <summary>Anti-cheat found where nothing of the recorder is recorded, but a scskiller.ini that can't be read is beside
    /// the exe: it's recorded as unknown with its folder, which stays refused, and goes once it can be judged SCSKiller's.</summary>
    [Fact]
    public async Task An_unreadable_ini_found_with_anti_cheat_is_recorded_and_its_folder_refused()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        File.Delete(Path.Combine(_exeDir, "d3d12.dll"));
        var left = k.Store.LoadGame(_game.Id);   // nothing recorded but the folder, as a record that lost its files
        left.RecorderFiles.Clear();
        k.Store.SaveGame(_game.Id, left);
        ScsKiller.SetIni(k.Store, _exeDir, _ => null);
        using (new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
            await k.RescanAsync(default);
            Assert.Equal(("", true), (Entry()!.Tracked, Entry()!.Remove));
            Assert.Contains(ScsKiller.FileKey(_exeDir), File.ReadAllLines(ScsKiller.Revoked));
        }
        await k.RescanAsync(default);
        Assert.False(File.Exists(ini));
        Assert.Null(Entry());
    }

    /// <summary>SCSKiller's uninstall with a game's drive away: nothing there is known gone, so the record keeps the folder,
    /// the dll's hash and 1.2's ini hash (nothing is recorded for a folder that can't be reached), to be taken out later.</summary>
    [Fact]
    public void The_uninstall_keeps_the_record_of_a_folder_on_a_drive_thats_away()
    {
        var letter = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !Directory.Exists($"{c}:\\"));
        var exe = $"{letter}:\\Games\\G\\bin\\G.exe";
        var store = new AppStore(Path.Combine(_root, "data"));
        var rec = store.LoadGame(_game.Id);
        (rec.RecorderExe, rec.RecorderInstallDir) = (exe, $"{letter}:\\Games\\G");
        rec.RecorderFiles["d3d12.dll"] = new string('A', 64);
        rec.RecorderFiles["scskiller.ini"] = new string('B', 64);
        store.SaveGame(_game.Id, rec);
        ScsKiller.RemoveAllRecorders(store, new HashSet<string>());
        rec = store.LoadGame(_game.Id);
        Assert.Equal(exe, rec.RecorderExe);
        Assert.Equal(["d3d12.dll", "scskiller.ini"], rec.RecorderFiles.Keys.Order());
        Assert.Null(ScsKiller.GetIni(store, Path.GetDirectoryName(exe)!));
    }

    /// <summary>A folder's scskiller.ini entry is found by the folder's full path, in any case; nothing is recorded for a folder
    /// that can't be reached.</summary>
    [Fact]
    public void A_folders_ini_entry_follows_its_path()
    {
        var store = new AppStore(Path.Combine(_root, "data"));
        var here = Directory.CreateDirectory(Path.Combine(_root, "Here")).FullName;
        ScsKiller.SetIni(store, here, _ => new IniOwner(here, "A"));
        Assert.Equal("A", ScsKiller.GetIni(store, here)?.Tracked);
        Assert.Equal("A", ScsKiller.GetIni(store, here.ToUpperInvariant() + Path.DirectorySeparatorChar)?.Tracked);

        var away = Path.Combine(_root, "Away");
        ScsKiller.SetIni(store, away, _ => new IniOwner(away, "B"));
        Assert.Null(ScsKiller.GetIni(store, away));
        Directory.CreateDirectory(away);
        Assert.Null(ScsKiller.GetIni(store, away));
    }

    /// <summary>The records' ini hashes (1.2's, beside the dll's) move to their folders' entries once: run again, or with an
    /// entry there already, nothing changes, and the entry wins. One the record names without a dll is still to be taken out.</summary>
    [Fact]
    public void A_records_ini_hash_moves_to_its_folders_entry_once()
    {
        var store = new AppStore(Path.Combine(_root, "data"));
        var rec = store.LoadGame(_game.Id);
        (rec.RecorderExe, rec.RecorderInstallDir) = (_game.ExePath, _game.InstallDir);
        rec.RecorderFiles["scskiller.ini"] = "AA";
        store.SaveGame(_game.Id, rec);
        ScsKiller.MigrateInis(store);
        ScsKiller.MigrateInis(store);
        Assert.Equal(("AA", true), (Entry()!.Tracked, Entry()!.Remove));
        Assert.Empty(store.LoadGame(_game.Id).RecorderFiles);

        rec = store.LoadGame(_game.Id);
        rec.RecorderFiles["scskiller.ini"] = "BB";   // a crash after the entry, before the record's save
        rec.RecorderFiles["d3d12.dll"] = "CC";
        store.SaveGame(_game.Id, rec);
        ScsKiller.MigrateInis(store);
        Assert.Equal("AA", Entry()!.Tracked);
        Assert.Equal(["d3d12.dll"], store.LoadGame(_game.Id).RecorderFiles.Keys);
    }

    /// <summary>The same folder under another game id (a hand-added game the store now lists): the user's ini there stays the
    /// user's, whichever record installs the recorder.</summary>
    [Fact]
    public async Task A_users_ini_stays_theirs_under_another_game_id()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var edited = Ini12.Replace("=1073741824", "=1234");   // the shape SCSKiller writes, a value of the user's
        File.WriteAllText(ini, edited);
        k.SetRecorderOverride(_game.Id, RecorderOverride.Off);
        Assert.True(Entry()!.Users);
        var other = Killer(new FakeReader(Unreal), game: _game with { Id = "test:other-" + Guid.NewGuid().ToString("N")[..8] });
        other.ProcessNames = () => new HashSet<string>();
        other.ManageRecorders = true;
        await other.ScanAsync(default);
        Assert.Equal(edited, File.ReadAllText(ini));
        Assert.False(Attested(_game));
    }

    /// <summary>A recorder recorded on a drive that's away while the game is found elsewhere: nothing there is known gone, so
    /// the record keeps the old folder and the recorder moves once it can be reached.</summary>
    [Fact]
    public async Task A_recorder_on_a_drive_thats_away_moves_once_it_can_be_reached()
    {
        var letter = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !Directory.Exists($"{c}:\\"));
        var away = $"{letter}:\\Games\\G\\bin\\G.exe";
        var k = Managed();
        var rec = k.Store.LoadGame(_game.Id);
        (rec.RecorderExe, rec.RecorderInstallDir, rec.Recorder) = (away, $"{letter}:\\Games\\G", RecorderOverride.On);
        rec.RecorderFiles["d3d12.dll"] = new string('A', 64);
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((away, away), (rec.RecorderMoveFrom, rec.RecorderExe));
        Assert.Contains("can be reached", k.Games.Single().RecorderNote);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>An untracked ini over 64 KB is never one SCSKiller wrote: the user's, also once trimmed back to SCSKiller's
    /// shape with a value of theirs.</summary>
    [Fact]
    public async Task An_ini_over_64_KB_is_the_users_from_then_on()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        ScsKiller.SetIni(k.Store, _exeDir, _ => null);   // untracked: only its size says whose it is
        var edited = Ini12.Replace("=1073741824", "=1234");
        File.WriteAllText(ini, edited + string.Concat(Enumerable.Repeat("; padding the user added\r\n", 3000)));
        k.ReconcileRecorders(_game.Id);
        Assert.True(Entry()!.Users);
        File.WriteAllText(ini, edited);   // trimmed back to SCSKiller's shape, a value of the user's
        k.ReconcileRecorders(_game.Id);
        Assert.Equal(edited, File.ReadAllText(ini));
        Assert.False(Attested(_game));
    }

    /// <summary>An untracked ini SCSKiller saw wasn't its own stays the user's once they edit it into SCSKiller's shape.</summary>
    [Fact]
    public async Task An_untracked_ini_seen_not_to_be_scskillers_stays_the_users_in_its_shape()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        ScsKiller.SetIni(k.Store, _exeDir, _ => null);
        var mine = Ini12.Replace("=1073741824", "=1234");
        File.WriteAllText(ini, mine + "frames=0\r\n");
        k.ReconcileRecorders(_game.Id);
        Assert.True(Entry()!.Users);
        File.WriteAllText(ini, mine);   // the line that made it foreign dropped, the user's limit kept
        k.ReconcileRecorders(_game.Id);
        Assert.Equal(mine, File.ReadAllText(ini));
        Assert.False(Attested(_game));
    }

    /// <summary>An earlier build's mark that the ini is the user's (on the game record, no hash) moves to its folder's entry
    /// at the first start, whether the record still names the recorder's folder (the migration) or not (the evaluation), but
    /// never over a newer build's entry for that folder: SCSKiller's own ini it tracks since stays SCSKiller's.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task An_earlier_builds_mark_that_the_ini_is_the_users_is_kept(bool folderRecorded, bool newer)
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var mine = Ini12.Replace("=1073741824", "=1234");
        if (!newer)
        {
            File.WriteAllText(ini, mine);
            ScsKiller.SetIni(k.Store, _exeDir, _ => null);   // as that build left it: no entry
        }
        var rec = k.Store.LoadGame(_game.Id);
        rec.IniIsUsers = true;
        if (!folderRecorded) rec.RecorderExe = null;
        k.Store.SaveGame(_game.Id, rec);
        var later = Managed();
        await later.ScanAsync(default);
        await later.RecordingMigration.WaitAsync(HangGuard);
        Assert.False(later.Store.LoadGame(_game.Id).IniIsUsers);
        Assert.Equal(!newer, Entry()!.Users);
        Assert.Equal(newer, Attested(_game));
        if (!newer) Assert.Equal(mine, File.ReadAllText(ini));
    }

    /// <summary>SCSKiller's own ini held without sharing (an antivirus) while its rewrite reads it again: nothing is known
    /// about it then, so it stays SCSKiller's and the next reconcile stamps it.</summary>
    [Fact]
    public async Task An_ini_held_while_its_rewrite_reads_it_again_stays_scskillers()
    {
        var k = Managed();
        await k.ScanAsync(default);
        Assert.True(Attested(_game));
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        FileStream? held = null;
        File.SetLastWriteTimeUtc(_game.ExePath, DateTime.UtcNow.AddMinutes(-5));   // an update: the stamp is taken again
        k.AttestStep = step => { if (step == "saved") held ??= new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.None); };
        k.ReconcileRecorders(_game.Id);
        held!.Dispose();
        k.AttestStep = null;
        Assert.False(Entry()!.Users);
        Assert.False(Attested(_game));
        k.ReconcileRecorders(_game.Id);
        Assert.True(Attested(_game));
    }

    /// <summary>A removal whose ini can't be deleted yet (held without delete sharing) and whose recording is then imported:
    /// the import's ini update never cancels the queued removal, and the next sweep takes the ini out.</summary>
    [Fact]
    public async Task An_import_during_a_removal_keeps_the_ini_queued()
    {
        var k = Managed();
        await k.ScanAsync(default);
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db"))) PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        using (new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.Read))
            k.SetRecorderOverride(_game.Id, RecorderOverride.Off);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.True(Entry()!.Remove);
        k.ReconcileRecorders();
        Assert.False(File.Exists(ini));
        Assert.Null(Entry());
    }

    /// <summary>A removal queued in a folder whose proxy is still in and whose ini is already gone: an update of the ini (a
    /// cleared recording) never writes one there, nor takes the folder off the queue.</summary>
    [Fact]
    public async Task An_update_never_writes_an_ini_where_a_removal_is_queued()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        ScsKiller.SetIni(k.Store, _exeDir, e => e! with { Remove = true });
        File.Delete(ini);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.log"), "loaded");
        Assert.True(k.ClearRecording(_game.Id));
        Assert.False(File.Exists(ini));
        Assert.True(Entry()!.Remove);
    }

    /// <summary>A scskiller.ini that appears while an install waits to write its own (the user put theirs back): it's the
    /// user's, left as it is and never taken over later, even in SCSKiller's shape.</summary>
    [Fact]
    public async Task An_ini_that_appears_while_an_install_waits_is_the_users()
    {
        var k = Managed();
        k.Settings = k.Settings with { RecordAllGames = false };
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var mine = Ini12.Replace("=1073741824", "=1234");
        k.InstallStep = step => { if (step == "ini") File.WriteAllText(ini, mine); };
        k.SetRecorderOverride(_game.Id, RecorderOverride.On);
        Assert.Equal(mine, File.ReadAllText(ini));
        Assert.True(Entry()!.Users);
        k.InstallStep = null;
        k.ReconcileRecorders(_game.Id);
        Assert.Equal(mine, File.ReadAllText(ini));
        Assert.False(Attested(_game));
    }

    /// <summary>Another process queues the ini's removal (and takes the proxy out) while an update of the ini waits for the
    /// store: the update, checking under the store's lock, leaves the queued removal as it is.</summary>
    [Fact]
    public async Task An_ini_update_waiting_for_the_store_never_cancels_a_removal_queued_meanwhile()
    {
        var k = Managed();
        await k.ScanAsync(default);
        Assert.True(Attested(_game));
        var inis = Path.Combine(_root, "data", "recorder-inis.json");
        File.SetLastWriteTimeUtc(_game.ExePath, DateTime.UtcNow.AddMinutes(-5));   // the next reconcile stamps it again
        using var waiting = new ManualResetEventSlim();
        var was = AppStore.PathGate.Waits;
        try
        {
            AppStore.PathGate.Waits = f => { if (f.StartsWith(inis, StringComparison.OrdinalIgnoreCase)) waiting.Set(); };
            Task update;
            using (new AppStore.PathGate(inis))   // another process holds the store
            {
                update = Task.Run(() => k.ReconcileRecorders(_game.Id));
                Assert.True(waiting.Wait(HangGuard));
                k.Store.UpdateInis(all => all[_exeDir] = all[_exeDir] with { Remove = true });   // that process queues the removal ...
                File.Delete(Path.Combine(_exeDir, "d3d12.dll"));   // ... and takes the proxy out
            }
            await update.WaitAsync(HangGuard);
        }
        finally { AppStore.PathGate.Waits = was; }
        Assert.True(Entry()!.Remove);
    }

    /// <summary>A hand-added game removed while the folder its recorder moved from is on a drive that's away: the record keeps
    /// that folder, so SCSKiller's uninstall (or the game added again) takes it out once it can be reached.</summary>
    [Fact]
    public async Task Removing_a_game_keeps_a_recorded_folder_thats_away_on_its_record()
    {
        var (root, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var k = Killer(new FakeReader(Unreal), sources: [Manual()]);
        k.ProcessNames = () => new HashSet<string>();
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        LegacyRecorder(k, id, shipping, root);
        var letter = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !Directory.Exists($"{c}:\\"));
        var away = Path.Combine($"{letter}:\\Games\\G\\bin", "G.exe");
        var rec = k.Store.LoadGame(id);
        rec.RecorderMoveFrom = away;
        k.Store.SaveGame(id, rec);
        k.RemoveManualGame(id);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(shipping)!, "d3d12.dll")));
        Assert.Equal(away, k.Store.LoadGame(id).RecorderMoveFrom);
    }

    /// <summary>Anti-cheat found while our proxy (another build's: not the manifest's hash) is still in an old folder the
    /// record names, beside an exe there (an EasyAntiCheat bootstrap): it goes by its export, with no folder identity kept,
    /// at the finding itself (the watcher's pass, no reconcile after it) and at a rescan.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_old_folders_proxy_beside_an_anti_cheat_exe_goes_by_its_export(bool pass)
    {
        var k = Managed();
        await k.ScanAsync(default);
        var old = Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Old")).FullName;
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderExe = Path.Combine(old, "Old.exe");   // the record still names the folder the recorder was in
        k.Store.SaveGame(_game.Id, rec);
        File.WriteAllBytes(rec.RecorderExe, [0]);
        File.WriteAllBytes(Path.Combine(old, "start_protected_game.exe"), [0]);
        Directory.CreateDirectory(Path.Combine(old, "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(old, "d3d12.dll"), [.. File.ReadAllBytes(_proxy), 0]);
        if (pass) await k.CheckRecorderGames();
        else await k.RescanAsync(default);
        Assert.False(File.Exists(Path.Combine(old, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>Recording turned off while the ini is held open leaves it queued with its folder on the record; discovery then
    /// finds the game's real exe in another folder: the old folder's ini is still taken out once it can be.</summary>
    [Fact]
    public async Task A_queued_ini_is_taken_out_after_the_game_moves_to_another_folder()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "Expedition 33", "Content")).FullName;
        Directory.CreateDirectory(Path.Combine(content, "Engine"));
        var stub = Path.Combine(content, "SandFall.exe");
        File.WriteAllBytes(stub, DiscoveryAndVendorTests.Exe(null, 1000));
        var game = new Game("test:stub-" + Guid.NewGuid().ToString("N")[..8], "Expedition 33", Store.Other, content, stub, "1.0");
        var k = RecordKiller([game]);
        await k.ScanAsync(default);
        k.InstallRecorder(game.Id);
        var ini = Path.Combine(content, "scskiller.ini");
        using (new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.Read))
            k.SetRecorderOverride(game.Id, RecorderOverride.Off);
        Assert.True(ScsKiller.IniPending(k.Store, content));
        var bin = Directory.CreateDirectory(Path.Combine(content, "Sandfall", "Binaries", "WinGDK")).FullName;
        File.WriteAllBytes(Path.Combine(bin, "SandFall-WinGDK-Shipping.exe"), DiscoveryAndVendorTests.Exe("d3d12.dll", 5000));
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.False(File.Exists(ini));
        Assert.Null(Entry(content));
    }

    /// <summary>The ini store held by another process past the uninstall's budget: whose the ini is can't be recorded, so it
    /// stays, but our proxy goes all the same; the removal stays pending on the record, and the next reconcile takes the ini out.</summary>
    [Fact]
    public async Task An_ini_store_that_cant_be_written_never_keeps_the_proxy_in()
    {
        var k = Managed();
        await k.ScanAsync(default);
        await k.RecorderIndexing.WaitAsync(HangGuard);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var inis = Path.Combine(_root, "data", "recorder-inis.json");
        using (new AppStore.PathGate(inis))   // the gate is per thread: the hook runs on another
            Assert.True(Task.Run(() => ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>(), TimeSpan.FromSeconds(3))).Wait(HangGuard));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.True(File.Exists(ini));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((_game.ExePath, true), (rec.RecorderExe, rec.RecorderRollback));
        k.ReconcileRecorders(_game.Id);
        Assert.False(File.Exists(ini));
    }

    /// <summary>Anti-cheat found during a move whose destination already holds our proxy: the destination the record names
    /// is taken out too, with the old folder and the game's own.</summary>
    [Fact]
    public async Task Anti_cheat_found_takes_our_proxy_out_of_a_moves_destination_too()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var (a, b) = (Directory.CreateDirectory(Path.Combine(_game.InstallDir, "A")).FullName, Directory.CreateDirectory(Path.Combine(_game.InstallDir, "B")).FullName);
        File.Copy(_proxy, Path.Combine(b, "d3d12.dll"));
        var rec = k.Store.LoadGame(_game.Id);
        (rec.RecorderExe, rec.RecorderMoveFrom, rec.RecorderMoveTo) = (Path.Combine(a, "A.exe"), Path.Combine(a, "A.exe"), Path.Combine(b, "B.exe"));
        k.Store.SaveGame(_game.Id, rec);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        await k.RescanAsync(default);
        Assert.False(File.Exists(Path.Combine(b, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>A hand-added game removed while its scskiller.ini is held (an editor, an antivirus): the proxy goes, but the
    /// game and its record stay until the ini can be taken out; the next removal does.</summary>
    [Fact]
    public async Task Removing_a_game_waits_for_its_scskiller_ini_to_be_taken_out()
    {
        var (root, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var manual = Manual();
        var k = Killer(new FakeReader(Unreal), sources: [manual]);
        k.ProcessNames = () => new HashSet<string>();
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        LegacyRecorder(k, id, shipping, root);
        var dir = Path.GetDirectoryName(shipping)!;
        var ini = Path.Combine(dir, "scskiller.ini");
        using (new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Contains("can't be taken out yet", Assert.Throws<InvalidOperationException>(() => k.RemoveManualGame(id)).Message);
        Assert.False(File.Exists(Path.Combine(dir, "d3d12.dll")));
        Assert.Single(manual.Entries());
        Assert.NotNull(k.Store.LoadGame(id).RecorderExe);
        k.RemoveManualGame(id);
        Assert.False(File.Exists(ini));
        Assert.Empty(manual.Entries());
    }

    /// <summary>Anti-cheat found while our proxy in the record's old folder is held without delete sharing: the game's own
    /// folder's proxy goes all the same, and the removal stays pending for the held one.</summary>
    [Fact]
    public async Task A_held_proxy_in_one_folder_never_keeps_another_folders_in()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var old = Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Old")).FullName;
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderExe = Path.Combine(old, "Old.exe");
        k.Store.SaveGame(_game.Id, rec);
        File.Copy(_proxy, Path.Combine(old, "d3d12.dll"));
        using (new FileStream(Path.Combine(old, "d3d12.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
            await k.RescanAsync(default);
            Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
            Assert.True(File.Exists(Path.Combine(old, "d3d12.dll")));
            Assert.True(k.Store.LoadGame(_game.Id).RecorderRollback);
        }
    }

    /// <summary>Anti-cheat found while our proxy is held (it can't go yet) beside an ini that isn't SCSKiller's by its bytes:
    /// SCSKiller's stamp saved again as UTF-16 (which the proxy's ini reader still takes), or one that can't be read. Its
    /// folder stays on the revocation list while that ini is there, whatever its encoding.</summary>
    [Theory]
    [InlineData("utf16")]
    [InlineData("unreadable")]
    public async Task A_folder_with_a_foreign_or_unreadable_ini_stays_refused_while_its_proxy_waits(string kind)
    {
        var k = Managed();
        await k.ScanAsync(default);
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.True(Attested(_game));
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        if (kind == "utf16") File.WriteAllText(ini, File.ReadAllText(ini), System.Text.Encoding.Unicode);
        bool Refused() => File.Exists(ScsKiller.Revoked) && File.ReadAllLines(ScsKiller.Revoked).Contains(ScsKiller.FileKey(_exeDir));
        using (new FileStream(Path.Combine(_exeDir, "d3d12.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))   // the game holds it
        using (kind == "unreadable" ? new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.None) : null)
        {
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
            await k.RescanAsync(default);
            Assert.True(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
            Assert.True(Refused());
        }
        Assert.True(File.Exists(ini));
    }

    /// <summary>SCSKiller's stamp re-saved as UTF-16 with the same values, recording turned off (the ini isn't SCSKiller's by
    /// its bytes: left as it is, its folder refused) and on again with the exe unchanged: the stamp the proxy would read
    /// still matches, but the folder stays refused while that ini is there.</summary>
    [Fact]
    public async Task A_foreign_ini_with_a_valid_stamp_keeps_its_folder_refused_when_recording_comes_back()
    {
        var k = Managed();
        await k.ScanAsync(default);
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.True(Attested(_game));
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        File.WriteAllText(ini, File.ReadAllText(ini), System.Text.Encoding.Unicode);
        var saved = File.ReadAllBytes(ini);
        bool Refused() => File.Exists(ScsKiller.Revoked) && File.ReadAllLines(ScsKiller.Revoked).Contains(ScsKiller.FileKey(_exeDir));
        k.SetRecorderOverride(_game.Id, RecorderOverride.Off);
        Assert.Equal(saved, File.ReadAllBytes(ini));
        Assert.True(Refused());
        k.SetRecorderOverride(_game.Id, RecorderOverride.On);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Equal(saved, File.ReadAllBytes(ini));
        Assert.True(Refused());
        Assert.False(Attested(_game));
    }

    /// <summary>A hand-added game removed while the ini store can't be saved (held by another program without delete sharing):
    /// the proxy goes, but the game and its record stay with the removal pending; once the store can be saved, the next
    /// removal takes the ini out.</summary>
    [Fact]
    public async Task Removing_a_game_whose_ini_store_cant_be_saved_keeps_it_until_the_ini_is_out()
    {
        var (root, stub, shipping) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var manual = Manual();
        var k = Killer(new FakeReader(Unreal), sources: [manual]);
        k.ProcessNames = () => new HashSet<string>();
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        LegacyRecorder(k, id, shipping, root);
        var dir = Path.GetDirectoryName(shipping)!;
        var ini = Path.Combine(dir, "scskiller.ini");
        using (new FileStream(Path.Combine(k.Store.DataDir, "recorder-inis.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Contains("can't be taken out yet", Assert.Throws<InvalidOperationException>(() => k.RemoveManualGame(id)).Message);
        Assert.False(File.Exists(Path.Combine(dir, "d3d12.dll")));
        Assert.True(File.Exists(ini));
        Assert.Single(manual.Entries());
        Assert.True(k.Store.LoadGame(id).RecorderRollback);
        k.RemoveManualGame(id);
        Assert.False(File.Exists(ini));
        Assert.Empty(manual.Entries());
    }

    /// <summary>Anti-cheat found while SCSKiller's ini is held (an editor, an antivirus): the proxy goes and the ini waits on
    /// its folder's entry; once the handle closes, the periodic pass takes it out though nothing else of the recorder is left.</summary>
    [Fact]
    public async Task The_periodic_pass_takes_out_an_ini_an_anti_cheat_removal_left()
    {
        var k = Managed();
        await k.ScanAsync(default);
        k.RemovalRetryInterval = TimeSpan.Zero;
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        using (new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
            await k.RescanAsync(default);
            Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
            Assert.True(ScsKiller.IniPending(k.Store, _exeDir));
        }
        await k.CheckRecorderGames();
        Assert.False(File.Exists(ini));
        Assert.Null(Entry());
    }

    /// <summary>A write that judged the ini, then waited for the store while another SCSKiller write replaced it with its own:
    /// the waiting write gives way, and SCSKiller's ini never becomes the user's.</summary>
    [Fact]
    public async Task A_write_that_waited_while_another_wrote_the_ini_gives_way()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var inis = Path.Combine(_root, "data", "recorder-inis.json");
        File.SetLastWriteTimeUtc(_game.ExePath, DateTime.UtcNow.AddMinutes(-5));   // the next reconcile stamps it again
        using var waiting = new ManualResetEventSlim();
        var was = AppStore.PathGate.Waits;
        try
        {
            AppStore.PathGate.Waits = f => { if (f.StartsWith(inis, StringComparison.OrdinalIgnoreCase)) waiting.Set(); };
            Task update;
            using (new AppStore.PathGate(inis))
            {
                update = Task.Run(() => k.ReconcileRecorders(_game.Id));
                Assert.True(waiting.Wait(HangGuard));
                File.WriteAllText(ini, File.ReadAllText(ini).Replace("exe_size=", "exe_size=1"));   // another SCSKiller write, which it tracks
                k.Store.UpdateInis(all => all[_exeDir] = all[_exeDir] with { Tracked = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ini))) });
            }
            await update.WaitAsync(HangGuard);
        }
        finally { AppStore.PathGate.Waits = was; }
        Assert.False(Entry()!.Users);
    }

    /// <summary>An ini already known to be the user's, queued for removal while it couldn't be read: once readable, the
    /// removal drops it from the queue (the user's stays as it is), so nothing waits on it.</summary>
    [Fact]
    public async Task A_users_ini_queued_while_unreadable_leaves_the_queue_once_read()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        File.WriteAllText(ini, "[Settings]\r\nFoo=1\r\n");
        ScsKiller.SetIni(k.Store, _exeDir, e => e! with { Tracked = "", Users = true, Remove = true });
        k.SetRecorderOverride(_game.Id, RecorderOverride.Off);
        Assert.Equal("[Settings]\r\nFoo=1\r\n", File.ReadAllText(ini));
        Assert.False(ScsKiller.IniPending(k.Store, _exeDir));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderExe);
    }

    /// <summary>An install that ends (the process killed) after its ini is written and before its keys file: the record
    /// already names the recorder's folder, so SCSKiller's uninstall takes the ini and the dll out.</summary>
    [Fact]
    public async Task An_install_cut_off_after_its_ini_leaves_nothing_the_uninstall_cant_find()
    {
        var k = Managed();
        k.Settings = k.Settings with { RecordAllGames = false };
        await k.ScanAsync(default);
        var state = Path.Combine(k.Store.GameDir(_game.Id), "state.json");
        byte[]? atKeys = null;
        k.InstallStep = step => { if (step == "keys") atKeys = File.ReadAllBytes(state); };
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        // the install's background index and migration save the record too, through the store's lock: done before it's put back
        while (!k.RecorderIndexing.IsCompleted || !k.RecordingMigration.IsCompleted)
            await Task.WhenAll(k.RecorderIndexing, k.RecordingMigration).WaitAsync(HangGuard);
        File.WriteAllBytes(state, atKeys!);   // the record as the process left it
        ScsKiller.RemoveAllRecorders(new AppStore(Path.Combine(_root, "data")), new HashSet<string>());
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>An ini SCSKiller tracks that the user changed (any value) is theirs from then on: never rewritten, stamped or
    /// deleted, also when it can't be read at the removal, and also once it's no longer tracked or briefly gone, until SCSKiller writes its own there.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tracked_ini_the_user_changed_is_theirs_until_it_is_gone(bool held)
    {
        var k = Managed();
        await k.ScanAsync(default);
        Assert.True(Attested(_game));
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var edited = File.ReadAllText(ini).Replace("exe_size=", "exe_size=1");
        Assert.NotEqual(File.ReadAllText(ini), edited);
        File.WriteAllText(ini, edited);
        using (held ? new FileStream(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete) : null)   // unreadable, but deletable
            k.SetRecorderOverride(_game.Id, RecorderOverride.Off);
        Assert.Equal(edited, File.ReadAllText(ini));
        if (held) await StartManaged();   // readable now: judged the user's
        Assert.Equal(edited, File.ReadAllText(ini));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((null, true), (Entry()!.Tracked, Entry()!.Users));
        k.SetRecorderOverride(_game.Id, RecorderOverride.On);   // not taken over by its bytes either
        Assert.Equal(edited, File.ReadAllText(ini));
        Assert.False(Attested(_game));
        File.Delete(ini);
        k.SetRecorderOverride(_game.Id, RecorderOverride.Off);
        k.SetRecorderOverride(_game.Id, RecorderOverride.On);
        await k.RecordingMigration.WaitAsync(HangGuard);
        Assert.True(Attested(_game));
        Assert.False(Entry()!.Users);
    }
    /// <summary>An untracked scskiller.ini is SCSKiller's only when its bytes are exactly what SCSKiller's writer gives for the
    /// values in it: every shape 1.0 to 1.2.4 (and the 1.3 internal builds) wrote, and 1.3's with root= and the stamp; any other
    /// byte (a BOM, UTF-16, LF line ends, whitespace, another order, a value written another way, a line SCSKiller doesn't
    /// write, root= or next= other than the install's and the record's, a signed line without a stamp) makes it the user's.</summary>
    [Fact]
    public void Only_the_bytes_scskillers_writer_gives_are_recognised_as_its()
    {
        const string h = "[scskiller]\r\n; written by SCSKiller: record the pipelines this game creates. Removed by 'uninstall recorder'.\r\nmode=record\r\n";
        const string cap = "; the recording limit per game (Settings): no new records once scskiller.db has this many bytes\r\nmax_db_bytes=1073741824\r\n";
        const string next = "; the game's own d3d12.dll (a mod), renamed by SCSKiller and put back when the recorder is removed\r\nnext=" + ScsKiller.ChainName + "\r\n";
        const string root = "; the game's install folder: the recorder checks its entries for anti-cheat too\r\nroot=..\\..\r\n";
        const string stampNote = "; the game's exe when SCSKiller last found no anti-cheat in its folders: the recorder records only while it is unchanged\r\n";
        const string stamp = stampNote + "exe_size=4096\r\nexe_time=134000000000000000\r\nsig=abcdef0123456789abcdef0123456789abcdef01\r\n";
        const string bound = stampNote + "exe_size=4096\r\nexe_time=134000000000000000\r\npid=1234\r\npid_time=134000000000000001\r\nsig=abcdef0123456789abcdef0123456789abcdef01\r\n";
        const string frames = "; frame times for a before and after recording, nothing else is recorded\r\nframes_only=1\r\n";
        const string countdown = "overlay=1\r\nab_play_s=420\r\nab_tag=before\r\n";
        static byte[] A(string s) => System.Text.Encoding.ASCII.GetBytes(s);
        var cases = new (string Name, byte[] Bytes, string? Chain, bool Ours)[]
        {
            ("1.x: header only", A(h), null, true),
            ("1.x: the limit", A(h + cap), null, true),
            ("1.x: a chain", A(h + next), ScsKiller.ChainName, true),
            ("1.x: the limit and a chain", A(h + cap + next), ScsKiller.ChainName, true),
            ("1.x: the record's chain, no next= yet", A(h + cap), ScsKiller.ChainName, true),
            ("1.3: root=, no stamp", A(h + cap + root), null, true),
            ("1.3: stamped, chained", A(h + cap + next + root + stamp), ScsKiller.ChainName, true),
            ("1.3: an offline session's", A(h + root + bound), null, true),
            ("1.3: a risk session's", A(h + root + bound.Replace("sig=", "risk=1\r\nsig=")), null, true),
            ("risk= without pid=", A(h + root + stamp.Replace("sig=", "risk=1\r\nsig=")), null, false),
            ("risk=2", A(h + root + bound.Replace("sig=", "risk=2\r\nsig=")), null, false),
            ("UTF-8 BOM", [0xEF, 0xBB, 0xBF, .. A(h + cap)], null, false),
            ("UTF-16 LE", [0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes(h + cap)], null, false),
            ("UTF-16 LE without BOM", System.Text.Encoding.Unicode.GetBytes(h + cap), null, false),
            ("LF line ends", A((h + cap).Replace("\r\n", "\n")), null, false),
            ("no last line end", A((h + cap)[..^2]), null, false),
            ("a blank line more", A(h + cap + "\r\n"), null, false),
            ("trailing space in root=", A(h + root.Replace("..\\..", "..\\.. ")), null, false),
            ("another root", A(h + root.Replace("..\\..", "..")), null, false),
            ("not ASCII in root=", [.. A(h + "; the game's install folder: the recorder checks its entries for anti-cheat too\r\nroot=..\\Caf"), 0xE9, 0x0D, 0x0A], null, false),
            ("root= before next=", A(h + root + next), ScsKiller.ChainName, false),
            ("the limit's comment after it", A(h + "max_db_bytes=1073741824\r\n; the recording limit per game (Settings): no new records once scskiller.db has this many bytes\r\n"), null, false),
            ("a number written another way", A(h + cap.Replace("=1073741824", "=01073741824")), null, false),
            ("a chain the record doesn't name", A(h + next), null, false),
            ("the D3D11 chain for a D3D12 one", A(h + next.Replace(ScsKiller.ChainName, ScsKiller.ChainName11)), ScsKiller.ChainName, false),
            ("sig= in capitals", A(h + root + stamp.Replace("abcdef", "ABCDEF")), null, false),
            ("a stamp without root=", A(h + stamp), null, false),
            ("pid= without pid_time=", A(h + root + stamp.Replace("sig=", "pid=1\r\nsig=")), null, false),
            ("frames=0 added", A(h + cap + "frames=0\r\n"), null, false),
            ("markers= added", A(h + cap + "markers=Custom.exe\r\n"), null, false),
            ("a comment of the user's", A(h + cap + "; mine\r\n"), null, false),
            ("a comment twice", A(h + cap.Split("max_db")[0] + cap), null, false),
            ("the header in capitals", A(h.Replace("[scskiller]", "[SCSKILLER]") + cap), null, false),
            ("a key twice", A(h + cap + "max_db_bytes=5\r\n"), null, false),
            ("a limit past a long", A(h + cap.Replace("=1073741824", "=9999999999999999999")), null, false),
            ("a size past a long", A(h + root + stamp.Replace("exe_size=4096", "exe_size=99999999999999999999")), null, false),
            ("a pid past an int", A(h + root + bound.Replace("pid=1234", "pid=2147483648")), null, false),
            ("a pid's time past a long", A(h + root + bound.Replace("pid_time=134000000000000001", "pid_time=9999999999999999999")), null, false),
            ("a sign", A(h + cap.Replace("=1073741824", "=+1073741824")), null, false),
            ("1.3: frames only", A(h + frames + root + stamp), null, true),
            ("frames_only=0", A(h + frames.Replace("=1", "=0") + root), null, false),
            ("frames_only= after root=", A(h + root + frames), null, false),
            ("1.3: the AGS line, signed", A(h + root + stamp.Replace("sig=", "vendor=amd\r\nsig=")), null, true),
            ("1.3: a risk session's AGS line", A(h + root + bound.Replace("sig=", "risk=1\r\nvendor=amd\r\nsig=")), null, true),
            ("1.3: the countdown, signed", A(h + frames + root + stamp.Replace("sig=", "vendor=amd\r\n" + countdown + "sig=")), null, true),
            ("the AGS line unsigned", A(h + root + "vendor=amd\r\n"), null, false),
            ("the countdown unsigned", A(h + root + countdown), null, false),
            ("another vendor", A(h + root + stamp.Replace("sig=", "vendor=nvidia\r\nsig=")), null, false),
            ("the AGS line after the countdown", A(h + root + stamp.Replace("sig=", countdown + "vendor=amd\r\nsig=")), null, false),
            ("the countdown without its tag", A(h + root + stamp.Replace("sig=", countdown.Replace("ab_tag=before\r\n", "") + "sig=")), null, false),
            ("overlay=2", A(h + root + stamp.Replace("sig=", countdown.Replace("overlay=1", "overlay=2") + "sig=")), null, false),
            ("another tag", A(h + root + stamp.Replace("sig=", countdown.Replace("=before", "=later") + "sig=")), null, false),
        };
        Assert.All(cases, c => Assert.True(c.Ours == ScsKiller.OurIniBytes(c.Bytes, c.Chain, "..\\.."), c.Name));
    }

    /// <summary>The stamp signs vendor= (the AGS hook) and the countdown's lines: signed with them, the proxy records; either
    /// added by hand to a signed ini, it refuses.</summary>
    [Theory]
    [InlineData("vendor=amd\r\n", true)]
    [InlineData("vendor=amd\r\n", false)]
    [InlineData("overlay=1\r\nab_play_s=4\r\nab_tag=before\r\n", true)]
    [InlineData("overlay=1\r\nab_play_s=4\r\nab_tag=before\r\n", false)]
    public void A_hand_added_AGS_or_countdown_line_breaks_the_stamp(string lines, bool signed)
    {
        if (OwnWarmExe() is not { } warm) return;
        var exeDir = Directory.CreateDirectory(Path.Combine(_root, "signed-lines")).FullName;
        var exe = Path.Combine(exeDir, "selftest.exe");
        File.Copy(Path.Combine(Path.GetDirectoryName(warm)!, "selftest.exe"), exe);
        var stamp = ScsKiller.Stamp(exe)!;
        var ini = "root=.\r\n" + (signed ? ScsKiller.Signed(exe, ".", stamp, lines) : ScsKiller.Signed(exe, ".", stamp).Replace("sig=", lines + "sig="));
        Assert.Equal(signed, Selftest(exeDir, "anticheat -", ini, stamped: false)!.Value.Computes > 0);
        Assert.Equal(signed ? null : "not signed by SCSKiller", ScsKiller.Refused(exe));
    }

    /// <summary>A cached anti-cheat verdict holds while what it was found in holds: once the marker is gone (deep in the
    /// install, where the folder stamp doesn't read, or in the install root) the next start evaluates the game again and
    /// clears it, and a marker put back makes it anti-cheat again. The verdict never goes while a marker is there (the
    /// folders changing only walks it again), a verdict an earlier build cached (no stamp or marker kept) is walked again
    /// once, and the recorder is in only while the game is clean. A marker put back deep in a game without the recorder is
    /// found when the folders change or the recorder is turned on, as for any clean verdict.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task A_cached_anti_cheat_verdict_goes_with_its_marker_and_comes_back_with_it(bool deep, bool recorder, bool earlier)
    {
        var marker = deep ? Path.Combine(_game.InstallDir, "Fake", "Content", "EasyAntiCheat") : Path.Combine(_game.InstallDir, "EasyAntiCheat");
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        async Task<AntiCheat> Start()   // a restart: the cached verdict is all it has
        {
            var k = Killer(new FakeReader(Unreal));
            k.ProcessNames = () => new HashSet<string>();
            k.ManageRecorders = true;
            k.Settings = k.Settings with { RecordAllGames = recorder };
            var s = (await k.ScanAsync(default)).Single();
            await k.RecorderIndexing.WaitAsync(HangGuard);
            Assert.Equal(s.AntiCheat, k.Games.Single().AntiCheat);
            Assert.Equal(recorder && s.AntiCheat == AntiCheat.None, File.Exists(dll));
            Assert.Equal(recorder && s.AntiCheat == AntiCheat.None, Attested(_game));
            return s.AntiCheat;
        }

        Directory.CreateDirectory(marker);
        Assert.Equal(AntiCheat.EasyAntiCheat, await Start());
        void AsAnEarlierBuildCachedIt()
        {
            if (!earlier) return;
            var store = new AppStore(Path.Combine(_root, "data"));
            var cached = store.LoadScan();
            cached[_game.Id] = cached[_game.Id] with { Found = null, FoundAt = null };
            store.SaveScan(cached);
        }
        AsAnEarlierBuildCachedIt();
        Assert.Equal(AntiCheat.EasyAntiCheat, await Start());
        File.WriteAllBytes(Path.Combine(_game.InstallDir, "patch.pak"), [1]);   // the folders change, the marker stays
        Assert.Equal(AntiCheat.EasyAntiCheat, await Start());
        Assert.Equal(AntiCheat.EasyAntiCheat, await Start());

        AsAnEarlierBuildCachedIt();
        Directory.Delete(marker);
        Assert.Equal(AntiCheat.None, await Start());
        Assert.Equal(AntiCheat.None, await Start());

        Directory.CreateDirectory(marker);
        if (deep && !recorder)   // a clean verdict holds until the folders change; turning the recorder on checks in full first
        {
            var k = Killer(new FakeReader(Unreal));
            k.ProcessNames = () => new HashSet<string>();
            await k.ScanAsync(default);
            Assert.Equal(AntiCheat.None, k.Games.Single().AntiCheat);
            Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
            Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        }
        Assert.Equal(AntiCheat.EasyAntiCheat, await Start());
        Assert.False(File.Exists(dll));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    /// <summary>A verdict found outside a scan's evaluation (here the reconcile's full walk) knows no marker path: the next
    /// scan checks the game in full, so a marker deep in the install, where the folder stamp doesn't read, takes the
    /// verdict with it once it's gone.</summary>
    [Fact]
    public async Task A_verdict_a_reconcile_found_goes_with_its_marker()
    {
        var marker = Path.Combine(_game.InstallDir, "Fake", "Content", "EasyAntiCheat");
        var k = Managed();
        await k.ScanAsync(default);
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.True(Attested(_game));

        Directory.CreateDirectory(marker);
        k.ReconcileRecorders();
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));

        Directory.Delete(marker);
        var again = Managed();   // a restart: the cached verdict is all it has
        Assert.Equal(AntiCheat.None, (await again.ScanAsync(default)).Single().AntiCheat);
        await again.RecorderIndexing.WaitAsync(HangGuard);
        Assert.True(Attested(_game));
    }

    /// <summary>An empty EasyAntiCheat folder put in a new folder of the install root, moved up to the root or not, then
    /// removed: the library refresh after the removal clears the verdict, without a restart.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_marker_moved_and_removed_clears_the_verdict_at_the_next_refresh(bool moved)
    {
        var inFolder = Path.Combine(_game.InstallDir, "SCSK-test", "EasyAntiCheat");
        var atRoot = Path.Combine(_game.InstallDir, "EasyAntiCheat");
        var k = Managed();
        async Task<AntiCheat> Library() => (await k.ScanAsync(default)).Single().AntiCheat;
        Assert.Equal(AntiCheat.None, await Library());
        Directory.CreateDirectory(inFolder);
        Assert.Equal(AntiCheat.EasyAntiCheat, await Library());
        if (moved)
        {
            Directory.Move(inFolder, atRoot);
            Assert.Equal(AntiCheat.EasyAntiCheat, await Library());
        }
        Directory.Delete(moved ? atRoot : inFolder);
        Assert.Equal(AntiCheat.None, await Library());
        Assert.Equal(AntiCheat.None, k.Games.Single().AntiCheat);
        await k.RecorderIndexing.WaitAsync(HangGuard);
        Assert.True(Attested(_game));
    }
}
