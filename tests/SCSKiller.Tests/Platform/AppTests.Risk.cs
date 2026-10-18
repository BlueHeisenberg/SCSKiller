using System.Diagnostics;
using System.Text.RegularExpressions;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// Risk sessions beside a game's anti-cheat (Settings.RecordAntiCheatGames, StartRiskSession) on the fake game: BattlEye's
// service beside its exe, the exe a copy of ping.exe (it runs a few seconds and loads no recorder) unless the built
// selftest records through the built proxy. No real game is started.
public partial class AppTests
{
    ScsKiller RiskKiller(IGameSource[]? sources = null, IEngineReader? reader = null, IGpuVendorBackend? vendor = null)
    {
        var k = Killer(reader ?? new FakeReader(Unreal), sources: sources, vendor: vendor);
        k.Settings = k.Settings with { RecordAntiCheatGames = true };
        return k;
    }

    void BattlEyeBeside() => File.WriteAllBytes(Path.Combine(_exeDir, "BEService_x64.exe"), [0]);

    void PingAsGame() => File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), _game.ExePath, true);

    static InvalidOperationException Refused(ScsKiller k, string id, AntiCheat shown, bool confirmed = true) =>
        Assert.Throws<InvalidOperationException>(() => { _ = k.StartRiskSession(id, confirmed, shown); });

    /// <summary>With the setting off an anti-cheat game is offered nothing, as before it existed.</summary>
    [Fact]
    public async Task Anti_cheat_games_get_no_session_while_the_setting_is_off()
    {
        BattlEyeBeside();
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        Assert.Equal((AntiCheat.BattlEye, false, false), (k.Games.Single().AntiCheat, k.Games.Single().RiskEligible, k.Games.Single().OfflineEligible));
        Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(_game.Id, true));
        Assert.Equal("Fake Game: no session: allow recording in games with anti-cheat in Settings first", Refused(k, _game.Id, AntiCheat.BattlEye).Message);
        Assert.DoesNotContain("d3d12.dll", Names(_exeDir));
    }

    /// <summary>A session is refused, with nothing written or journaled, unless this launch was confirmed, the game's switch
    /// is on, a fresh check finds the anti-cheat the confirmation named and none of the names it creates is there.</summary>
    [Fact]
    public async Task A_risk_session_is_refused_until_confirmed_switched_on_and_still_what_was_shown()
    {
        BattlEyeBeside();
        var k = RiskKiller();
        k.LaunchCommand = g => new ProcessStartInfo(g.ExePath);   // the fake game's store has none: its refusal would come first
        await k.ScanAsync(default);
        Assert.Equal((true, false), (k.Games.Single().RiskEligible, k.Games.Single().OfflineEligible));
        var before = Names(_exeDir);
        Assert.Equal("Fake Game: a session starts only after you confirm it", Refused(k, _game.Id, AntiCheat.BattlEye, confirmed: false).Message);
        Assert.Equal("Fake Game: no session: turn on Record while I play for this game first", Refused(k, _game.Id, AntiCheat.BattlEye).Message);
        k.SetOfflineRecording(_game.Id, true);
        Assert.Equal("Fake Game: no session: BattlEye was found, not EasyAntiCheat: confirm again", Refused(k, _game.Id, AntiCheat.EasyAntiCheat).Message);
        Assert.Equal(before, Names(_exeDir));
        File.WriteAllBytes(Path.Combine(_exeDir, "d3d12.dll"), [.. "MZ a mod"u8]);
        Assert.Equal("Fake Game: no session: d3d12.dll is already in its folder", Refused(k, _game.Id, AntiCheat.BattlEye).Message);
        Assert.Equal("MZ a mod"u8.ToArray(), File.ReadAllBytes(Path.Combine(_exeDir, "d3d12.dll")));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((null, 0, null), (rec.OfflineSession, rec.RecorderFiles.Count, rec.RecorderExe));
    }

    /// <summary>An EasyAntiCheat game of offline-eac.json keeps its offline session; any other gets the risk session.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Risk_and_offline_sessions_are_never_both_offered(bool listed)
    {
        EasyAntiCheatBeside();
        var k = RiskKiller();
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            var s = k.Games.Single();
            Assert.Equal((AntiCheat.EasyAntiCheat, listed, !listed), (s.AntiCheat, s.OfflineEligible, s.RiskEligible));
            if (listed) Assert.Contains("isn't available for this game", Refused(k, _game.Id, AntiCheat.EasyAntiCheat).Message);
        }, id: listed ? _game.Id : "steam:1");
    }

    /// <summary>A game that gains anti-cheat while a pair is armed: the pair ends, and a session's ini never turns the
    /// countdown on, even with a pair record still armed (one written after the end).</summary>
    [Fact]
    public async Task A_game_that_gains_anti_cheat_ends_its_pair_and_no_session_ini_shows_the_countdown()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.Contains("overlay=1", ScsKiller.OverlayIni(p.Record));
        BattlEyeBeside();
        PingAsGame();
        p.K.Settings = p.K.Settings with { RecordAntiCheatGames = true };
        p.K.LaunchCommand = g => new ProcessStartInfo(g.ExePath, "-n 3 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true };
        await p.K.ScanAsync(default);
        Assert.Equal(AntiCheat.BattlEye, p.K.Games.Single().AntiCheat);
        Assert.Null(p.Shown);
        Assert.Null(p.Record);

        var rec = p.K.Store.LoadGame(p.Id);
        rec.BeforeAfter = new BeforeAfterRecord { Stage = BeforeAfterStage.BeforeArmed, Minutes = 5, ArmedAt = p.At, Overlay = true };
        p.K.Store.SaveGame(p.Id, rec);
        p.K.SetOfflineRecording(p.Id, true);
        var session = p.K.StartRiskSession(p.Id, confirmed: true, AntiCheat.BattlEye);
        var ini = File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini"));
        await session.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains("mode=record", ini);
        Assert.DoesNotContain("overlay=", ini);
        Assert.DoesNotContain("ab_tag=", ini);
    }

    /// <summary>VAC alone qualifies, and the confirmation and refusals name it.</summary>
    [Fact]
    public async Task A_vac_game_is_offered_a_session_naming_vac()
    {
        var k = RiskKiller();
        k.VacCheck = _ => true;
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((AntiCheat.None, true, true, false), (s.AntiCheat, s.Vac, s.RiskEligible, s.OfflineEligible));
        Assert.Equal("VAC", ScsKiller.AntiCheatName(s));
        k.SetOfflineRecording(_game.Id, true);
        Assert.Equal("Fake Game: no session: VAC was found, not BattlEye: confirm again", Refused(k, _game.Id, AntiCheat.BattlEye).Message);
    }

    /// <summary>Steam's appinfo.vdf unreadable: every Steam game is VAC (the recorder stays out), and each scan logs it once,
    /// not once per game. Read again: no line.</summary>
    [Fact]
    public async Task An_unreadable_appinfo_is_logged_once_per_scan()
    {
        var log = new List<string>();
        var k = Killer(new FakeReader(Unreal), games: [_game, _game with { Id = "steam:2", Name = "Second Game" }]);
        k.Log = new Lines(log);
        k.VacCheck = _ => true;
        string? error = "appinfo.vdf: unknown format 0x04030201";
        k.VacReadError = () => error;
        int Logged() { lock (log) return log.Count(l => l.Contains("appinfo.vdf: unknown format")); }
        await k.ScanAsync(default, userRequested: true);
        Assert.Equal(2, k.Games.Count(s => s.Vac));
        Assert.Equal(1, Logged());
        await k.ScanAsync(default, userRequested: true);
        Assert.Equal(2, Logged());
        error = null;
        await k.ScanAsync(default, userRequested: true);
        Assert.Equal(2, Logged());
    }

    /// <summary>The session's ini carries risk=1. Read while the app may be renaming it into place, which refuses a reader a moment.</summary>
    static bool Bound(string ini)
    {
        try { return File.ReadAllText(ini).Contains("risk=1"); }
        catch (IOException) { return false; }
    }

    /// <summary>The ini's signed lines as the app signs them for a risk session of the game's process.</summary>
    static string RiskSigned(string exe, string ini, int pid, long started) =>
        ScsKiller.Signed(exe, Regex.Match(ini, "\r\nroot=(.*)\r\n").Groups[1].Value, ScsKiller.Stamp(exe)!, ScsKiller.BoundTo(pid, started) + ScsKiller.RiskLine);

    /// <summary>A confirmed session, the game started by SCSKiller (a game added by hand) or by its store's launch: risk=1 in
    /// the signed ini, after the binding to that one process, the signature this copy's; when it exits every name it placed is
    /// gone. Nothing was admitted and nothing refused (no recorder ran): the game page and the log say it never loaded, the
    /// page until the switch goes off.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_risk_session_binds_risk_to_its_process_and_cleans_up_when_it_exits(bool manual)
    {
        BattlEyeBeside();
        PingAsGame();
        var source = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        var id = manual ? source.Add(new ManualEntry(_game.ExePath, _game.InstallDir, "Fake Game", true)).Game.Id : _game.Id;
        var k = RiskKiller(manual ? [source] : null);
        var log = new List<string>();
        k.Log = new Lines(log);
        k.OfflineArguments = "-n 6 127.0.0.1";
        k.LaunchCommand = g => new ProcessStartInfo(g.ExePath, k.OfflineArguments) { UseShellExecute = false, CreateNoWindow = true };
        await k.ScanAsync(default);
        k.SetOfflineRecording(id, true);
        var before = Names(_exeDir);
        var ini = Path.Combine(_exeDir, "scskiller.ini");

        var session = k.StartRiskSession(id, confirmed: true, AntiCheat.BattlEye);
        // a reader holding the ini as the store's launch is bound (its own open may meet the bind's rename)
        FileStream? reader = null;
        await Until(() => { try { reader = File.OpenRead(ini); return true; } catch (IOException) { return false; } });
        using (reader) await Task.Delay(300);
        await Until(() => Bound(ini), 30);
        var s = k.Store.LoadGame(id).OfflineSession!;
        Assert.Equal((true, true), (s.Risk, s.Resumed));
        var text = File.ReadAllText(ini);
        Assert.EndsWith(RiskSigned(_game.ExePath, text, s.Pid, s.Started), text);
        Assert.Contains($"pid={s.Pid}\r\npid_time={s.Started}\r\nrisk=1\r\nsig=", text);
        Assert.True(k.Games.Single().OfflineRunning);
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.SteamAppIdFile)));
        k.ReconcileRecorders();
        await k.CheckRecorderGames();
        Assert.Equal(text, File.ReadAllText(ini));   // no pass takes out or re-signs a live session's ini

        await session.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(before, Names(_exeDir));
        var rec = k.Store.LoadGame(id);
        Assert.Equal((null, 0, null), (rec.OfflineSession, rec.RecorderFiles.Count, rec.RecorderExe));
        Assert.Equal((false, ScsKiller.AntiCheatSessionNote), (k.Games.Single().OfflineRunning, k.Games.Single().SessionNote));
        lock (log) Assert.Contains("Fake Game: the session recorded nothing: the recorder never loaded, no scskiller.log written during the run", log);
        k.SetOfflineRecording(id, false);
        Assert.Null(k.Games.Single().SessionNote);
    }

    /// <summary>A session the proxy refused while its game ran: an unstamped ini's refusal is the game reaching DirectX before
    /// the binding, any other shows the proxy's reason. The log names the refusal.</summary>
    [Theory]
    [InlineData("exe not as checked", ScsKiller.UnboundSessionNote)]
    [InlineData("bound to another process", "The last launch wasn't recorded: bound to another process.")]
    public async Task A_refused_risk_session_says_why(string why, string note)
    {
        BattlEyeBeside();
        PingAsGame();
        var k = RiskKiller();
        var log = new List<string>();
        k.Log = new Lines(log);
        k.OfflineArguments = "-n 6 127.0.0.1";
        k.LaunchCommand = g => new ProcessStartInfo(g.ExePath, k.OfflineArguments) { UseShellExecute = false, CreateNoWindow = true };
        await k.ScanAsync(default);
        k.SetOfflineRecording(_game.Id, true);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var session = k.StartRiskSession(_game.Id, confirmed: true, AntiCheat.BattlEye);
        await Until(() => Bound(ini), 30);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(TestEnv.Recorder, "refused")).FullName, ScsKiller.FileKey(_game.ExePath)), "1 " + why);
        await session.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(note, k.Games.Single().SessionNote);
        lock (log) Assert.Contains($"Fake Game: the session recorded nothing: the recorder refused it: {why}", log);
    }

    /// <summary>A store install reached through a junction (a library on a linked folder): the game's image path names the folder
    /// resolved, and the session binds to its process all the same.</summary>
    [Fact]
    public async Task A_store_launched_risk_session_binds_through_a_junction_to_the_install()
    {
        BattlEyeBeside();
        PingAsGame();
        var link = Junction(Path.Combine(_root, "FakeGame-link"), _game.InstallDir);
        try
        {
            var g = _game with { InstallDir = link, ExePath = Path.Combine(link, Path.GetRelativePath(_game.InstallDir, _game.ExePath)) };
            var k = Killer(new FakeReader(Unreal), game: g);
            k.Settings = k.Settings with { RecordAntiCheatGames = true };
            k.LaunchCommand = x => new ProcessStartInfo(x.ExePath, "-n 6 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true };
            await k.ScanAsync(default);
            k.SetOfflineRecording(g.Id, true);
            var before = Names(_exeDir);
            var ini = Path.Combine(_exeDir, "scskiller.ini");
            var session = k.StartRiskSession(g.Id, confirmed: true, AntiCheat.BattlEye);
            await Until(() => Bound(ini), 30);
            Assert.Equal(g.ExePath, k.Store.LoadGame(g.Id).OfflineSession!.Exe);
            await session.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(before, Names(_exeDir));
            Assert.Null(k.Store.LoadGame(g.Id).OfflineSession);
        }
        finally { Directory.Delete(link); }   // the junction alone: the cleanup's recursive delete can't go through it
    }

    /// <summary>A game that exits at once (a crash): the session ends with its files out, and nothing starts it again.</summary>
    [Fact]
    public async Task A_crashed_risk_session_is_not_started_again_and_leaves_nothing()
    {
        BattlEyeBeside();
        PingAsGame();
        var source = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        var id = source.Add(new ManualEntry(_game.ExePath, _game.InstallDir, "Fake Game", true)).Game.Id;
        var k = RiskKiller([source]);
        k.OfflineArguments = "-n";   // ping exits at once with an error
        await k.ScanAsync(default);
        k.SetOfflineRecording(id, true);
        var before = Names(_exeDir);
        await k.StartRiskSession(id, confirmed: true, AntiCheat.BattlEye).WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(1000);
        Assert.DoesNotContain(Ours(true), p => p.Exe.Equals(Path.GetFileName(_game.ExePath), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, Names(_exeDir));
        Assert.Null(k.Store.LoadGame(id).OfflineSession);
        Assert.False(k.Games.Single().OfflineRunning);
    }

    /// <summary>The session places the recorder for the DirectX the game runs: d3d11.dll for a DirectX 11 game where its
    /// recording records (AMD), d3d12.dll for DirectX 12. A game that may run on either gets d3d11.dll only on AMD under
    /// Unreal 4 (its default), and a launch that loaded neither says the game contains anti-cheat. Its exit takes out what was placed.
    /// A DirectX 11 game on NVIDIA, whose compile needs no recording, is offered none.</summary>
    [Theory]
    [InlineData("D3D11", "4.26", "amd-1", ScsKiller.Dll11)]
    [InlineData("D3D12", "4.26", "amd-1", ScsKiller.Dll12)]
    [InlineData("D3D11 or D3D12", "4.26", "amd-1", ScsKiller.Dll11)]
    [InlineData("D3D11 or D3D12", "5.4", "amd-1", ScsKiller.Dll12)]
    [InlineData("D3D11 or D3D12", "4.26", "nvidia-1", ScsKiller.Dll12)]
    [InlineData("D3D11", "4.26", "nvidia-1", null)]
    public async Task A_risk_session_places_the_recorder_for_the_games_DirectX_and_takes_it_out(string api, string version, string profile, string? dll)
    {
        BattlEyeBeside();
        PingAsGame();
        var gpu = Gpu with { Vendor = profile == "amd-1" ? GpuVendor.Amd : GpuVendor.Nvidia };
        var k = RiskKiller(reader: new FakeReader(Unreal with { GraphicsApi = api, Version = version }), vendor: new FakeVendor(gpu, profile: profile));
        k.OfflineArguments = "-n 6 127.0.0.1";
        k.LaunchCommand = g => new ProcessStartInfo(g.ExePath, k.OfflineArguments) { UseShellExecute = false, CreateNoWindow = true };
        await k.ScanAsync(default);
        if (dll == null)
        {
            Assert.False(k.Games.Single().RiskEligible);
            Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(_game.Id, true));
            return;
        }
        k.SetOfflineRecording(_game.Id, true);
        var before = Names(_exeDir);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var session = k.StartRiskSession(_game.Id, confirmed: true, AntiCheat.BattlEye);
        await Until(() => Bound(ini), 30);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, dll)));
        Assert.False(File.Exists(Path.Combine(_exeDir, dll == ScsKiller.Dll11 ? ScsKiller.Dll12 : ScsKiller.Dll11)));
        Assert.Equal([dll], k.Store.LoadGame(_game.Id).RecorderFiles.Keys);
        Assert.DoesNotContain("frames_only", File.ReadAllText(ini));

        await session.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(before, Names(_exeDir));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((null, 0, null), (rec.OfflineSession, rec.RecorderFiles.Count, rec.RecorderExe));
        Assert.Equal(ScsKiller.AntiCheatSessionNote, k.Games.Single().SessionNote);
    }

    /// <summary>A DirectX 11 game with anti-cheat on AMD: its page says a recording covers its vertex shaders while a session
    /// can record them, and not with the setting off.</summary>
    [Fact]
    public async Task A_DirectX_11_anti_cheat_game_on_AMD_says_a_recording_covers_its_vertex_shaders_only_while_a_session_can()
    {
        BattlEyeBeside();
        var k = Killer(new FakeReader(Unreal with { GraphicsApi = "D3D11" }), new Planner(), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }, profile: "amd-1"));
        await k.ScanAsync(default);
        Assert.DoesNotContain(Planner.D3D11VsRecord, k.Games.Single().StatusReason);
        k.Settings = k.Settings with { RecordAntiCheatGames = true };
        await k.SettingsRefresh;
        Assert.True(k.Games.Single().RiskEligible);
        Assert.EndsWith("; " + Planner.D3D11VsRecord, k.Games.Single().StatusReason);
    }

    /// <summary>A game with anti-cheat that needs a recording: with no session offered its status says the anti-cheat blocks
    /// it, with one offered it needs a recording from a session, at the user's own risk, and never names a block.</summary>
    [Fact]
    public async Task An_anti_cheat_game_offered_a_session_needs_a_recording_without_a_block_claim()
    {
        BattlEyeBeside();
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner());
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.Unsupported, "needs a recording, which BattlEye blocks"), (k.Games.Single().Status, k.Games.Single().StatusReason));
        k.Settings = k.Settings with { RecordAntiCheatGames = true };
        await k.SettingsRefresh;
        var s = k.Games.Single();
        Assert.True(s.RiskEligible);
        Assert.Equal((GameStatus.NeedsRecording, "needs a recording, " + ScsKiller.RiskNoRecording), (s.Status, s.StatusReason));
        Assert.Equal("Record a session on its page", Format.ShortNote(s));
    }

    /// <summary>The setting offers a session whatever the engine's support (here its files are encrypted): the status stays
    /// the engine's, so nothing promises a compile. With the setting off, or a server verdict against the game, there is none.</summary>
    [Fact]
    public async Task An_unsupported_engine_is_offered_a_session_only_with_the_setting_on_and_no_verdict_against_it()
    {
        BattlEyeBeside();
        const string why = "encrypted game files (needs the game's AES key)";
        var k = Killer(new FakeReader(Unreal with { Encrypted = true, Unsupported = why }), new Planner());
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.Unsupported, why, false), (k.Games.Single().Status, k.Games.Single().StatusReason, k.Games.Single().RiskEligible));
        Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(_game.Id, true));

        k.Settings = k.Settings with { RecordAntiCheatGames = true };
        await k.SettingsRefresh;
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Unsupported, why, true, ScsKiller.SkipAntiCheat), (s.Status, s.StatusReason, s.RiskEligible, s.RecorderSkip));
        Assert.Equal(ScsKiller.Dll11, ScsKiller.RiskDll(Unreal with { GraphicsApi = "D3D11", Unsupported = why }, SCSKiller.Tests.Planning.Ff7.Amd));   // by the API alone
        // the session card: nothing compiles from the recording yet
        Assert.Equal("Its recording is kept for later. It compiles once you add the game's AES key.", ScsKiller.SessionKeptNote(s));
        Assert.Equal("This game isn't supported yet, so its recording is kept for later and nothing compiles from it now.",
            ScsKiller.SessionKeptNote(s with { Engine = Unreal with { Unsupported = "Unreal 5.9 isn't supported yet" } }));
        Assert.Null(ScsKiller.SessionKeptNote(s with { Engine = Unreal }));
        k.SetOfflineRecording(_game.Id, true);
        try
        {
            GameVerdicts.Current = GameVerdicts.TryParse(Verdicts(GameVerdicts.UnsupportedVerdict, "crashes", _game.Id))!;
            await k.ScanAsync(default);
            Assert.False(k.Games.Single().RiskEligible);
            Assert.Equal("Fake Game: no session: recording while you play isn't available for this game", Refused(k, _game.Id, AntiCheat.BattlEye).Message);
        }
        finally { GameVerdicts.Current = GameVerdicts.Embedded; }
        Assert.DoesNotContain("d3d12.dll", Names(_exeDir));
    }

    /// <summary>A game whose store SCSKiller can't start (HoYoPlay) is offered no session: its status says the anti-cheat
    /// blocks the recording, as with the setting off, and its page says why there is no session.</summary>
    [Fact]
    public async Task A_game_whose_store_has_no_launch_is_offered_no_session()
    {
        var game = _game with { Id = "hoyoplay:nap_global", Name = "Zenless Zone Zero" };
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner(), game: game);
        k.Settings = k.Settings with { RecordAntiCheatGames = true };
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((false, true), (s.RiskEligible, s.RiskNoLaunch));
        Assert.Equal("No recording session: SCSKiller can't start games from HoYoPlay.", ScsKiller.NoLaunchNote("HoYoPlay"));
        Assert.Equal((GameStatus.Unsupported, "needs a recording, which its anti-cheat blocks", "Blocked by anti-cheat"), (s.Status, s.StatusReason, Format.ShortNote(s)));   // HoYoPlay games count as anti-cheat
        Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(game.Id, true));
        k.Settings = k.Settings with { RecordAntiCheatGames = false };
        await k.SettingsRefresh;
        Assert.False(k.Games.Single().RiskNoLaunch);
    }

    /// <summary>An EasyAntiCheat game of offline-eac.json whose engine is unsupported gets its offline session only while the
    /// setting is on, and turning the setting off clears its switch.</summary>
    [Fact]
    public Task An_unsupported_engine_gets_an_offline_session_only_with_the_setting_on() => Listed(async () =>
    {
        EasyAntiCheatBeside();
        var k = Killer(new FakeReader(Unreal with { Encrypted = true, Unsupported = "encrypted game files (needs the game's AES key)" }), new Planner());
        await k.ScanAsync(default);
        Assert.Equal((false, false), (k.Games.Single().OfflineEligible, k.Games.Single().RiskEligible));
        Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(_game.Id, true));

        k.Settings = k.Settings with { RecordAntiCheatGames = true };
        await k.SettingsRefresh;
        Assert.Equal((true, false, GameStatus.Unsupported), (k.Games.Single().OfflineEligible, k.Games.Single().RiskEligible, k.Games.Single().Status));
        k.SetOfflineRecording(_game.Id, true);

        k.Settings = k.Settings with { RecordAntiCheatGames = false };
        await k.SettingsRefresh;
        Assert.Equal((false, false), (k.Games.Single().OfflineEligible, k.Store.LoadGame(_game.Id).OfflineRecord));
    });

    /// <summary>SCSKiller ended during a risk session: the cleanup helper (as at the next logon) waits for the bound game,
    /// never ending it, then takes out every name the session placed.</summary>
    [Theory]
    [InlineData(ScsKiller.Dll12)]
    [InlineData(ScsKiller.Dll11)]
    public void The_cleanup_helper_waits_for_a_risk_sessions_game_then_removes_its_files(string dll)
    {
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        var before = Names(_exeDir);
        var started = ScsKiller.StartedAt(p.Id)!.Value;
        var store = Journal([dll, "scskiller.ini"], pid: p.Id, started: started);
        var rec = store.LoadGame(_game.Id);
        rec.OfflineSession = rec.OfflineSession! with { Risk = true };
        store.SaveGame(_game.Id, rec);

        Assert.Equal(0, ScsKiller.RunOfflineCleanup(store, _game.Id, TimeSpan.Zero, othersRun: OurOthersRun));
        Assert.True(p.HasExited);
        Assert.Equal(0, p.ExitCode);
        Assert.Equal(before, Names(_exeDir));
        Assert.Null(store.LoadGame(_game.Id).OfflineSession);
    }

    /// <summary>The setting turned off: every anti-cheat game's switch goes off, and what a session left (its game ran while
    /// SCSKiller started) is taken out.</summary>
    [Fact]
    public async Task Turning_the_setting_off_clears_the_switches_and_removes_what_sessions_left()
    {
        BattlEyeBeside();
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        var before = Names(_exeDir);
        var started = ScsKiller.StartedAt(p.Id)!.Value;
        var store = Journal(["d3d12.dll", "scskiller.ini"], pid: p.Id, started: started);
        var rec = store.LoadGame(_game.Id);
        rec.OfflineSession = rec.OfflineSession! with { Risk = true };
        store.SaveGame(_game.Id, rec);
        var k = RiskKiller();
        await k.ScanAsync(default);
        k.SetOfflineRecording(_game.Id, true);
        Assert.True(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));   // its game still runs
        p.WaitForExit();

        k.Settings = k.Settings with { RecordAntiCheatGames = false };
        await k.SettingsRefresh;
        Assert.Equal(before, Names(_exeDir));
        rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((null, false), (rec.OfflineSession, rec.OfflineRecord));
        Assert.Equal((false, false), (k.Games.Single().RiskEligible, k.Games.Single().OfflineRecord));
        Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(_game.Id, true));
    }

    /// <summary>With the setting and the game's switch on, Record all and the recorder's own switch on: no reconcile, watcher
    /// pass or install places the recorder in an anti-cheat or VAC game, nor writes its ini.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outside_a_session_no_pass_places_or_arms_an_anti_cheat_game(bool vac)
    {
        if (!vac) BattlEyeBeside();
        var k = RiskKiller();
        k.VacCheck = _ => vac;
        k.Settings = k.Settings with { RecordAllGames = true };
        await k.ScanAsync(default);
        k.SetOfflineRecording(_game.Id, true);
        k.SetRecorderOverride(_game.Id, RecorderOverride.On);
        k.ReconcileRecorders();
        await k.CheckRecorderGames();
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        var s = k.Games.Single();
        Assert.Equal((true, false, ScsKiller.SkipAntiCheat), (s.RiskEligible, s.RecorderEffective, s.RecorderSkip));
        Assert.DoesNotContain("d3d12.dll", Names(_exeDir));
        Assert.DoesNotContain("scskiller.ini", Names(_exeDir));
    }

    /// <summary>risk=1 is signed only for a risk session's process: never by a recorder pass's stamp, nor by an offline session.</summary>
    [Fact]
    public async Task Risk_is_signed_only_for_a_risk_session_bound_to_its_process()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        await k.RecorderIndexing;
        Assert.True(Attested(_game));
        await k.CheckRecorderGames();
        Assert.DoesNotContain("risk=", File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.DoesNotContain("risk=", ScsKiller.Signed(_game.ExePath, "..", ScsKiller.Stamp(_game.ExePath)!, ScsKiller.BoundTo(1234, 5678)));
    }

    /// <summary>The built selftest as the game, BattlEye's service beside it: the proxy records the bound process under
    /// risk=1, logs what it saw, and the session leaves no note.</summary>
    [Fact]
    public async Task A_risk_session_records_beside_the_anti_cheat_and_leaves_no_note()
    {
        BattlEyeBeside();
        if (OwnWarmExe() is not { } warm) return;
        var bin = Path.GetDirectoryName(warm)!;
        File.Copy(Path.Combine(bin, "selftest.exe"), _game.ExePath, true);
        File.Copy(Path.Combine(bin, "d3d12.dll"), _proxy, true);
        var source = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        var id = source.Add(new ManualEntry(_game.ExePath, _game.InstallDir, "Fake Game", true)).Game.Id;
        var k = RiskKiller([source]);
        k.OfflineArguments = "anticheat -";
        await k.ScanAsync(default);
        k.SetOfflineRecording(id, true);
        var before = Names(_exeDir);
        var unarmed = Environment.GetEnvironmentVariable("SCSKILLER_SELFTEST_UNARMED");
        Environment.SetEnvironmentVariable("SCSKILLER_SELFTEST_UNARMED", "1");
        try { await k.StartRiskSession(id, confirmed: true, AntiCheat.BattlEye).WaitAsync(TimeSpan.FromSeconds(120)); }
        finally { Environment.SetEnvironmentVariable("SCSKILLER_SELFTEST_UNARMED", unarmed); }
        Assert.Equal(before, Names(_exeDir));
        Assert.Equal(2, PsoDb.Read(Path.Combine(k.Store.GameDir(id), "recording.db")).Count(r => r.Tag == 'C'));
        Assert.Contains(ScsKiller.RiskLogLine + ", anti-cheat seen: BEService_x64.exe (next to the exe)",
            File.ReadAllText(Path.Combine(ScsKiller.OfflineSessionDir(k.Store, id), "scskiller.log")));
        Assert.Null(k.Games.Single().SessionNote);
    }

    /// <summary>A process the app started, with BattlEye's service beside the exe and EasyAntiCheat's in the install root: an
    /// ini the app signed with risk=1 after that process's binding records; bound without risk, the markers refuse. An ini
    /// edited by hand (risk=1 added to a signed one, or taken out of one), risk without the binding or with another value, a
    /// binding to another process and a revoked folder are refused, risk or not.</summary>
    [Theory]
    [InlineData("risk", null)]
    [InlineData("bound", "anti-cheat next to the exe")]
    [InlineData("added", "not signed by SCSKiller")]
    [InlineData("removed", "not signed by SCSKiller")]
    [InlineData("unbound", "risk= malformed")]
    [InlineData("risk=2", "risk= malformed")]
    [InlineData("elsewhere", "bound to another process")]
    [InlineData("revoked", "revoked by SCSKiller")]
    public void The_proxy_records_past_anti_cheat_only_for_a_signed_bound_risk_ini(string ini, string? why)
    {
        if (OwnWarmExe() is not { } warm) return;
        var bin = Path.GetDirectoryName(warm)!;
        var dir = Directory.CreateDirectory(Path.Combine(_root, "risk", "bin")).FullName;
        File.WriteAllBytes(Path.Combine(dir, "BEService_x64.exe"), [0]);
        Directory.CreateDirectory(Path.Combine(_root, "risk", "EasyAntiCheat"));
        var exe = Path.Combine(dir, "selftest.exe");
        File.Copy(Path.Combine(bin, "selftest.exe"), exe);
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        var path = Path.Combine(dir, "scskiller.ini");
        var stamp = ScsKiller.Stamp(exe)!;
        string Text(int pid, long created)
        {
            var (bound, risk) = (ScsKiller.BoundTo(pid, created), ScsKiller.BoundTo(pid, created) + ScsKiller.RiskLine);
            var signed = ini switch
            {
                "risk" or "revoked" => ScsKiller.Signed(exe, "..", stamp, risk),
                "bound" => ScsKiller.Signed(exe, "..", stamp, bound),
                "added" => ScsKiller.Signed(exe, "..", stamp, bound).Replace("sig=", ScsKiller.RiskLine + "sig="),
                "removed" => ScsKiller.Signed(exe, "..", stamp, risk).Replace(ScsKiller.RiskLine, ""),
                "unbound" => ScsKiller.Signed(exe, "..", stamp, ScsKiller.RiskLine),
                "risk=2" => ScsKiller.Signed(exe, "..", stamp, bound + "risk=2\r\n"),
                "elsewhere" => ScsKiller.Signed(exe, "..", stamp, ScsKiller.BoundTo(pid + 1, created) + ScsKiller.RiskLine),
                _ => throw new ArgumentException(ini),
            };
            return "[scskiller]\r\nmode=record\r\nframes=0\r\nroot=..\r\n" + signed;
        }
        var revoked = ScsKiller.FileKey(dir) + "\r\n";
        if (ini == "revoked")
        {
            Directory.CreateDirectory(ScsKiller.RecorderDir);
            File.AppendAllText(ScsKiller.Revoked, revoked);
        }
        var unarmed = Environment.GetEnvironmentVariable("SCSKILLER_SELFTEST_UNARMED");
        Environment.SetEnvironmentVariable("SCSKILLER_SELFTEST_UNARMED", "1");
        try
        {
            using var h = ScsKiller.StartAttested(exe, "anticheat -", (_, pid, created) => File.WriteAllText(path, Text(pid, created)));
            WaitExit(h);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCSKILLER_SELFTEST_UNARMED", unarmed);
            if (ini == "revoked") File.WriteAllText(ScsKiller.Revoked, File.ReadAllText(ScsKiller.Revoked).Replace(revoked, ""));
        }
        var db = Path.Combine(dir, "scskiller.db");
        Assert.Equal(why == null ? 2 : 0, File.Exists(db) ? PsoDb.Read(db).Count(r => r.Tag == 'C') : 0);
        var log = Path.Combine(dir, "scskiller.log");
        Assert.Equal(why == null, File.Exists(log) && File.ReadAllText(log).Contains(ScsKiller.RiskLogLine + ", anti-cheat seen: BEService_x64.exe (next to the exe), EasyAntiCheat (in the install folder)"));
        Assert.Equal(why, ScsKiller.Refused(exe));
    }

    /// <summary>A VAC game's session still runs when SCSKiller starts again, and Steam no longer marks the game VAC: the next
    /// start's recorder pass checks it as a recorder game and finds it clean, and still leaves the live session's signed ini
    /// as it is.</summary>
    [Fact]
    public async Task A_pass_never_re_signs_a_live_risk_session()
    {
        PingAsGame();
        var source = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        var id = source.Add(new ManualEntry(_game.ExePath, _game.InstallDir, "Fake Game", true)).Game.Id;
        var k = RiskKiller([source]);
        k.VacCheck = _ => true;
        k.OfflineArguments = "-n 8 127.0.0.1";
        await k.ScanAsync(default);
        k.SetOfflineRecording(id, true);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var session = k.StartRiskSession(id, confirmed: true, AntiCheat.None);
        await Until(() => Bound(ini), 30);
        var text = File.ReadAllText(ini);

        var next = RiskKiller([source]);
        next.VacCheck = _ => false;
        next.ProcessNames = () => new HashSet<string>();   // the process list missed it: only the session's record knows it runs
        next.ManageRecorders = true;
        await next.ScanAsync(default);
        await next.CheckRecorderGames();
        Assert.Equal(text, File.ReadAllText(ini));
        await session.WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>A risk session of a portable copy: its marker goes in first, its ini is signed with that copy's own key (made in
    /// its data folder), and the installed copy's recorder folder is left byte for byte.</summary>
    [Fact]
    public async Task A_portable_copys_risk_session_signs_with_its_own_key_and_leaves_the_installed_copys_folder()
    {
        BattlEyeBeside();
        PingAsGame();
        var live = Path.GetDirectoryName(ScsKiller.DefaultLedger)!;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(live).FullName, "recorder.key"), new string('a', 64));
        var before = Files(live);
        var data = Path.Combine(_root, "portable");
        try
        {
            ScsKiller.RecorderDir = null!;
            ScsKiller.LedgerDir = Path.Combine(data, "armed");
            var source = new ManualSource(new AppStore(data));
            var id = source.Add(new ManualEntry(_game.ExePath, _game.InstallDir, "Fake Game", true)).Game.Id;
            var k = Killer(new FakeReader(Unreal), sources: [source], data: data);
            k.Settings = k.Settings with { RecordAntiCheatGames = true };
            k.OfflineArguments = "-n 4 127.0.0.1";
            await k.ScanAsync(default);
            k.SetOfflineRecording(id, true);
            var ini = Path.Combine(_exeDir, "scskiller.ini");
            var session = k.StartRiskSession(id, confirmed: true, AntiCheat.BattlEye);
            await Until(() => Bound(ini), 30);
            Assert.Equal(Path.Combine(data, "armed"), File.ReadAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));
            Assert.True(File.Exists(Path.Combine(data, "recorder.key")));
            var s = k.Store.LoadGame(id).OfflineSession!;
            var text = File.ReadAllText(ini);
            Assert.EndsWith(RiskSigned(_game.ExePath, text, s.Pid, s.Started), text);
            await session.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { ScsKiller.RecorderDir = TestEnv.Recorder; }
        Assert.Equal(before, Files(live));
    }

    /// <summary>`selftest admission` (no GPU, no proxy loaded): the stamp the proxy checks sig= over, with risk= after the
    /// binding, risk='s form, and the verdict from what the checks found.</summary>
    [Fact]
    public void The_proxy_admission_rules_hold()
    {
        if (OwnWarmExe() is not { } warm) return;
        var dir = Directory.CreateDirectory(Path.Combine(_root, "admission")).FullName;
        var exe = Path.Combine(dir, "selftest.exe");
        File.Copy(Path.Combine(Path.GetDirectoryName(warm)!, "selftest.exe"), exe);
        using var p = Process.Start(new ProcessStartInfo(exe, "admission") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("admission OK", o);
    }

    /// <summary>`selftest warmmemory`: workers go only over the warm's memory budget, a PC low on memory keeps more from
    /// coming back, and memory fewer workers don't free (or one worker's for 30 s) counts as kept, so they come back.</summary>
    [Fact]
    public void The_warm_memory_budget_drops_workers_only_over_it()
    {
        if (OwnWarmExe() is not { } warm) return;
        var exe = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "warmmemory")).FullName, "selftest.exe");
        File.Copy(Path.Combine(Path.GetDirectoryName(warm)!, "selftest.exe"), exe);
        using var p = Process.Start(new ProcessStartInfo(exe, "warmmemory") { RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("warmmemory OK", o);
    }
}
