using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;
using static SCSKiller.Core.BeforeAfterStage;

namespace SCSKiller.Tests.Platform;

// Before and after on a DirectX 11 game: AMD's recorder as it is, NVIDIA's frames-only one only while a pair is in progress.
public partial class AppTests
{
    async Task<Pair> Dx11PairRig(string profile, GpuVendor vendor = GpuVendor.Nvidia, bool recordAll = true, bool manage = true)
    {
        var k = Killer11(profile, vendor, recordAll, manage);
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        var p = new Pair { K = k, Id = _game.Id, Exe = Path.GetFileName(_game.ExePath), ExeDir = _exeDir, DriverCache = CacheFile("0002a91d33333333.nvph", 65536) };
        (k.Clock, k.RunningGameExes) = (() => p.At, () => p.Running.ToHashSet(StringComparer.OrdinalIgnoreCase));
        await k.ScanAsync(default);
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("33333333");
        k.Store.SaveGame(_game.Id, rec);
        p.Poll();
        return p;
    }

    /// <summary>The app as it starts: the same data folder, a DirectX 11 game, recorders managed.</summary>
    ScsKiller Killer11(string profile = "nvidia-1", GpuVendor vendor = GpuVendor.Nvidia, bool recordAll = true, bool manage = true, Game[]? games = null)
    {
        var k = Killer(new ApiReader((games ?? [_game]).ToDictionary(g => g.Id, _ => "D3D11")), warmer: new FakeWarmer(), games: games,
            vendor: new FakeVendor(Gpu with { Vendor = vendor }, profile: profile));
        k.Settings = k.Settings with { RecordAllGames = recordAll };   // before ManageRecorders: no reconcile in the background
        k.ManageRecorders = manage;
        k.ProcessNames = () => new HashSet<string>();
        return k;
    }

    /// <summary>NVIDIA: the app stopped after copying the pair's d3d11.dll, before its ini and manifest were saved. The next
    /// start writes the ini with frames_only=1 (from the dll on disk) before stamping it; an ini that isn't SCSKiller's, or
    /// one without frames_only, never gets it stamped: it's taken out.</summary>
    [Fact]
    public async Task On_NVIDIA_a_frames_only_install_cut_off_after_the_copy_never_records_in_full()
    {
        var p = await Dx11PairRig("nvidia-1");
        var rec = p.K.Store.LoadGame(p.Id);
        rec.BeforeAfter = new BeforeAfterRecord { Stage = BeforeArmed, ArmedAt = p.At };
        p.K.Store.SaveGame(p.Id, rec);
        File.Copy(_proxy, Proxy11);
        Assert.False(File.Exists(Ini));

        var k = Killer11();   // the next start
        await k.ScanAsync(default);
        Assert.Contains("\r\nframes_only=1\r\n", File.ReadAllText(Ini));
        Assert.True(ScsKiller.IsOurProxy(Proxy11));
        Assert.True(Attested(_game));

        File.WriteAllText(Ini, "[scskiller]\r\nmode=record\r\n");   // not ours: the proxy would record in full
        k.ReconcileRecorders(p.Id);
        Assert.False(File.Exists(Proxy11));
        Assert.False(Attested(_game));
    }

    /// <summary>NVIDIA: a frames-only recorder another SCSKiller copy placed (its marker names that copy), with no pair in
    /// progress here, and a record here that names it (a data folder copied from that copy's): this copy's pass leaves its
    /// d3d11.dll and ini as they are. Ending it is that copy's.</summary>
    [Fact]
    public async Task On_NVIDIA_another_copys_frames_only_recorder_is_left_to_that_copy()
    {
        var p = await Dx11PairRig("nvidia-1");
        File.Copy(_proxy, Proxy11);
        File.WriteAllText(Ini, "[scskiller]\r\nmode=record\r\nframes_only=1\r\n");
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp), Path.Combine(_root, "other", "armed"));
        var rec = p.K.Store.LoadGame(p.Id);
        rec.RecorderExe = _game.ExePath;
        p.K.Store.SaveGame(p.Id, rec);
        var before = Files(_game.InstallDir);

        await p.K.CheckRecorderGames();

        Assert.Equal(before, Files(_game.InstallDir));
    }

    /// <summary>NVIDIA: Cancel while the game runs revokes the frames-only recorder at once; with the app closed when the game
    /// exits, the next start (Scan games when SCSKiller starts off) takes it out.</summary>
    [Fact]
    public async Task On_NVIDIA_a_pair_ended_while_the_game_runs_loses_its_recorder_at_the_next_start()
    {
        var p = await Dx11PairRig("nvidia-1");
        p.K.Settings = p.K.Settings with { ScanAtStart = false };
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.True(Attested(_game));
        p.Running.Add(p.Exe);
        p.Poll();
        p.K.EndBeforeAfter(p.Id, compile: false);
        Assert.True(ScsKiller.IsOurProxy(Proxy11));   // the game holds it
        Assert.False(Attested(_game));   // but no launch is admitted from now on

        var k = Killer11();   // the app closed, the game exited, the app starts again
        await k.ScanAsync(default);
        Assert.False(File.Exists(Proxy11) || File.Exists(Ini));
        Assert.False(Attested(_game));
    }

    /// <summary>An install that chains a mod and fails before its copy, at the proxy's own read or at the manifest's save
    /// (the record can't be written): the mod keeps its name and bytes, and the next install chains it as usual.</summary>
    [Fact]
    public async Task A_chain_that_fails_before_the_copy_leaves_the_mod_at_its_name()
    {
        var dll = Path.Combine(_exeDir, ScsKiller.Dll12);
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(dll, mod);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        void Unchanged()
        {
            Assert.Equal(mod, File.ReadAllBytes(dll));
            Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
        }
        using (new FileStream(_proxy, FileMode.Open, FileAccess.Read, FileShare.None))   // the proxy can't be read
            Xunit.Record.Exception(() => k.SetRecordAlongsideMod(_game.Id, true));
        Unchanged();

        var state = Path.Combine(k.Store.GameDir(_game.Id), "state.json");
        k.InstallStep = step => { if (step == "chain") File.SetAttributes(state, FileAttributes.ReadOnly); };
        Xunit.Record.Exception(() => k.ReconcileRecorders(_game.Id));   // the refresh after it may not save either
        File.SetAttributes(state, FileAttributes.Normal);
        Unchanged();

        k.InstallStep = null;
        k.SetRecordAlongsideMod(_game.Id, true);
        Assert.True(ScsKiller.IsOurProxy(dll));
        Assert.Equal(mod, File.ReadAllBytes(Path.Combine(_exeDir, ScsKiller.ChainName)));
    }

    string Ini => Path.Combine(_exeDir, "scskiller.ini");
    string Proxy11 => Path.Combine(_exeDir, ScsKiller.Dll11);

    /// <summary>NVIDIA: a DirectX 11 game has no recorder, and a pair puts one in as d3d11.dll with frames_only=1 from the
    /// click to the after run's exit, then takes it out; the report still reads the pinned runs.</summary>
    [Fact]
    public async Task On_NVIDIA_a_DirectX_11_pair_has_a_frames_only_recorder_from_start_to_done()
    {
        var p = await Dx11PairRig("nvidia-1");
        var s = p.K.Games.Single();
        Assert.Equal((ScsKiller.SkipNotDx12, false, false), (s.RecorderSkip, s.RecorderEffective, File.Exists(Proxy11)));
        Assert.Null(p.K.BeforeAfterBlocked(p.Id));

        p.K.StartBeforeAfter(p.Id, 5);
        Assert.True(ScsKiller.IsOurProxy(Proxy11));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.Dll12)));
        Assert.Contains("\r\nframes_only=1\r\n", File.ReadAllText(Ini));
        Assert.True(Attested(_game));
        Assert.True(p.K.Games.Single() is { RecorderEffective: true, RecorderSkip: null, Records11: true });

        p.K.PauseQueue();   // the compile waits: the stage is Compiling when read
        var before = p.Play(360);
        Assert.Equal(BeforeAfterStage.Compiling, p.Record!.Stage);
        Assert.True(ScsKiller.IsOurProxy(Proxy11));   // kept between the runs
        p.K.ResumeQueue();
        await p.Compiled();
        p.K.ContinueBeforeAfter(p.Id);
        var after = p.Play(360);
        Assert.Equal((Done, before, after), (p.Record!.Stage, p.Record.Before, p.Record.After));
        Assert.False(File.Exists(Proxy11));
        Assert.False(File.Exists(Ini));
        Assert.False(Attested(_game));
        Assert.NotNull(p.K.BeforeAfterReport(p.Id));
        Assert.Equal(ScsKiller.SkipNotDx12, p.K.Games.Single().RecorderSkip);
    }

    /// <summary>NVIDIA: Start over takes the frames-only recorder out; the game's switch Off, anti-cheat or VAC keep it out
    /// and the pair from starting; a user's own scskiller.ini, which would leave out frames_only, keeps it out too.</summary>
    [Fact]
    public async Task On_NVIDIA_a_DirectX_11_pair_ends_its_recorder_and_never_runs_where_the_recorder_cant()
    {
        var p = await Dx11PairRig("nvidia-1", recordAll: false);   // it records nothing: the setting doesn't apply
        Assert.Null(p.K.BeforeAfterBlocked(p.Id));
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.True(File.Exists(Proxy11));
        p.K.EndBeforeAfter(p.Id, compile: false);
        Assert.False(File.Exists(Proxy11) || File.Exists(Ini));

        p.K.SetRecorderOverride(p.Id, RecorderOverride.Off);
        Assert.Equal("Turn the recorder on for this game first", p.K.BeforeAfterBlocked(p.Id));
        p.K.SetRecorderOverride(p.Id, RecorderOverride.Default);
        Assert.Null(p.K.BeforeAfterBlocked(p.Id));

        File.WriteAllText(Ini, "[scskiller]\r\nmode=record\r\n");
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.False(File.Exists(Proxy11));
        Assert.DoesNotContain("frames_only", File.ReadAllText(Ini));
        p.K.EndBeforeAfter(p.Id, compile: false);
        File.Delete(Ini);

        p.K.VacCheck = _ => true;
        p.K.RefreshGame(p.Id);
        Assert.Equal("Not for games with anti-cheat", p.K.BeforeAfterBlocked(p.Id));
        p.K.VacCheck = _ => false;
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        p.K.RefreshGame(p.Id);
        Assert.Equal("Not for games with anti-cheat", p.K.BeforeAfterBlocked(p.Id));
        Assert.False(File.Exists(Proxy11));
    }

    /// <summary>The recorder as a newer SCSKiller build's: its file version raised to 99.</summary>
    static byte[] NewerBuild(byte[] dll)
    {
        var at = dll.AsSpan().IndexOf(new byte[] { 0xBD, 0x04, 0xEF, 0xFE });   // VS_FIXEDFILEINFO's signature
        Assert.True(at > 0);
        BitConverter.GetBytes(99u << 16).CopyTo(dll, at + 8);   // dwFileVersionMS
        return dll;
    }

    /// <summary>A pair needs this build's recorder (its panel and frame times) and its own ini lines. A newer SCSKiller's
    /// recorder stays outside a pair, and is replaced for one; an ini SCSKiller wrote that this data folder doesn't track
    /// gets the run's lines; one that isn't SCSKiller's refuses the launch.</summary>
    [Fact]
    public async Task A_pair_runs_with_this_builds_recorder_and_its_own_ini_lines()
    {
        var built = Path.Combine(Planning.Ff7.ProxyBin, "d3d12.dll");   // a real one: the tests' own proxy has no version
        if (!File.Exists(built)) return;   // the native build isn't here
        var p = await Dx11PairRig("amd-1", GpuVendor.Amd);
        var mine = File.ReadAllBytes(Proxy11);
        var newer = NewerBuild(File.ReadAllBytes(built));
        File.WriteAllBytes(Proxy11, newer);
        var rec = p.K.Store.LoadGame(p.Id);
        rec.RecorderFiles.Remove("scskiller.ini");   // written by another build or data folder
        p.K.Store.SaveGame(p.Id, rec);
        p.K.ReconcileRecorders(p.Id);
        Assert.Equal(newer, File.ReadAllBytes(Proxy11));   // never over a newer build's, outside a pair

        p.K.StartBeforeAfter(p.Id, 5);
        Assert.Equal(mine, File.ReadAllBytes(Proxy11));
        p.K.BeforeAfterRecorderReady(p.Id);   // the launch's check
        Assert.Contains("\r\noverlay=1\r\nab_play_s=300\r\nab_tag=before\r\n", File.ReadAllText(Ini));

        File.WriteAllText(Ini, "[scskiller]\r\nmode=record\r\n");   // the user's own
        var e = Assert.Throws<InvalidOperationException>(() => p.K.BeforeAfterRecorderReady(p.Id));
        Assert.Equal(ScsKiller.RecorderNotReadyNote + "its scskiller.ini isn't SCSKiller's", e.Message);
        p.K.EndBeforeAfter(p.Id, compile: false);
    }

    string AmdDx(string app, int size = 4096)
    {
        var path = Path.Combine(_root, "AMD", "DxCache", $"{app}.1.2.3.0.parc");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow);   // made anew, not tunneled from the namesake set aside a moment ago
        return path;
    }

    async Task<Pair> AmdDx11Rig(Game[]? games = null)
    {
        var k = Killer11("amd-1", GpuVendor.Amd, games: games);
        k.AppCache = new AmdAppCache(Path.Combine(_root, "AMD", "DxcCache"), Path.Combine(_root, "AMD", "DxCache"));
        var p = new Pair { K = k, Id = _game.Id, Exe = Path.GetFileName(_game.ExePath), ExeDir = _exeDir, DriverCache = Path.Combine(_root, "AMD", "DxcCache", "run.bin") };
        Directory.CreateDirectory(Path.GetDirectoryName(p.DriverCache)!);
        (k.Clock, k.RunningGameExes) = (() => p.At, () => p.Running.ToHashSet(StringComparer.OrdinalIgnoreCase));
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));   // the scan's compile: a pair's clear waits for none
        k.PauseQueue();
        p.Poll();
        return p;
    }

    /// <summary>AMD, DirectX 11, the game's DxCache key unknown (AMD's D3D11 driver doesn't always hold that file open): the
    /// before run's clear sets aside every DxCache file of no known game, so the run is cold; the key the game makes during
    /// the run is learned and its old files deleted, the rest goes back. Another app's key made alongside: nothing learned,
    /// everything back, and the report says the after run may have used the before run's cache, unless the game's own process
    /// was seen holding one of the two (<paramref name="sampled"/>): that one is learned. Cancel puts files back too.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task On_AMD_a_DirectX_11_before_run_is_cold_with_other_apps_files_set_aside(bool shared, bool sampled)
    {
        var p = await AmdDx11Rig();
        var k = p.K;
        var (browser, old) = (AmdDx("bbbb"), AmdDx("9999", 65536));   // another app's cache, the game's old one (key not known)

        p.K.StartBeforeAfter(p.Id, 5);
        Assert.False(File.Exists(browser) || File.Exists(old));
        Assert.False(p.Record!.ClearGap);
        p.Running.Add(p.Exe);
        p.Poll();
        p.Launch(60);   // too short: a key made meanwhile isn't taken as the game's, and nothing is deleted
        var other = AmdDx("cccc", 100);
        p.Exit();
        Assert.Equal(BeforeArmed, p.Record!.Stage);
        Assert.DoesNotContain(p.K.Store.LoadGame(p.Id).CacheKeys, k => k.StartsWith("dx:"));
        Assert.False(File.Exists(old) || File.Exists(browser) || File.Exists(other));   // all set aside again for the next try
        p.Running.Add(p.Exe);
        p.Poll();
        p.Launch(360);
        AmdDx("9999", 100);   // the game's cache, made anew
        if (shared) AmdDx("dddd");   // another app started meanwhile
        if (sampled)   // LearnWhilePlaying saw the game's process hold its file
        {
            var seen = p.K.Store.LoadGame(p.Id);
            seen.GameKeys.Add("dx:0000000000009999");
            p.K.Store.SaveGame(p.Id, seen);
        }
        p.Exit();
        var rec = p.K.Store.LoadGame(p.Id);
        Assert.Equal(BeforeAfterStage.Compiling, p.Record!.Stage);
        Assert.True(File.Exists(browser));
        Assert.Equal(shared && !sampled, p.Record.DxShared);
        if (shared && !sampled)
        {
            Assert.DoesNotContain("dx:0000000000009999", rec.CacheKeys);
            Assert.Equal(65536, new FileInfo(old).Length);   // the run's file is the smaller: the old one is back
        }
        else
        {
            Assert.Contains("dx:0000000000009999", rec.CacheKeys);
            Assert.False(File.Exists(old));   // the old cache deleted, then the run's by the clear before the compile
        }
        Assert.False(Directory.Exists(Path.Combine(_root, "AMD", "DxCache.scskiller-quarantine")));

        await p.Compiled();
        p.K.EndBeforeAfter(p.Id, compile: false);   // Cancel, with files set aside again for a new pair
        AmdDx("eeee");
        if (!shared || sampled) rec.CacheKeys.Remove("dx:0000000000009999");
        k.Store.SaveGame(p.Id, rec);
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.False(File.Exists(browser));
        p.K.EndBeforeAfter(p.Id, compile: false);
        p.Poll();
        Assert.True(File.Exists(browser) && File.Exists(Path.Combine(_root, "AMD", "DxCache", "eeee.1.2.3.0.parc")));
    }

    /// <summary>AMD, DirectX 11, the game's key unknown and not seen held: another app remakes its set-aside file during
    /// the run while the game makes none. Each check alone keeps that key from being learned as the game's (so its files
    /// are put back, never deleted): another discovered game with the same exe name (its key kept as that game's), the game
    /// seen holding a D3D12 key made during the run (or its exe name's D3D12 key made, unseen), a process holding the file
    /// at the end, the file made before the launch, the game's own file made again with its set-aside namesake's creation
    /// time (NTFS tunneling: when it was made can't be told). With none of them ("none") it is learned: the accepted
    /// limit.</summary>
    [Theory]
    [InlineData("tunneled")]
    [InlineData("sameName")]
    [InlineData("d3d12")]
    [InlineData("d3d12Unsampled")]
    [InlineData("held")]
    [InlineData("beforeLaunch")]
    [InlineData("none")]
    public async Task On_AMD_a_DirectX_11_key_another_app_remade_during_the_before_run_is_not_the_games(string check)
    {
        var twinDir = Directory.CreateDirectory(Path.Combine(_root, "Twin")).FullName;
        var twinExe = Path.Combine(twinDir, Path.GetFileName(_game.ExePath));
        File.WriteAllBytes(twinExe, new byte[4096]);
        var p = await AmdDx11Rig(check == "sameName" ? [_game, new Game("test:twin", "Twin", Store.Other, twinDir, twinExe)] : null);
        var (app, old) = (AmdDx("bbbb", 65536), AmdDx("9999", 65536));
        File.SetCreationTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));   // the game's old cache, from before the launch

        p.K.StartBeforeAfter(p.Id, 5);
        Assert.False(File.Exists(app));
        p.Running.Add(p.Exe);
        p.Poll();
        var stamp = p.Launch(360);
        AmdDx("bbbb", 100);   // the app's file made again, the game makes none
        if (check == "beforeLaunch") File.SetCreationTimeUtc(app, DateTimeOffset.FromUnixTimeMilliseconds(stamp).UtcDateTime.AddMinutes(-5));
        if (check == "tunneled")
            File.SetCreationTimeUtc(AmdDx("9999", 100), File.GetCreationTimeUtc(Path.Combine(_root, "AMD", "DxCache.scskiller-quarantine", Path.GetFileName(old))));
        if (check == "d3d12")   // LearnWhilePlaying saw the game hold a DxcCache file it made
        {
            AmdFile("5555aaaa");
            var seen = p.K.Store.LoadGame(p.Id);
            seen.GameKeys.Add("dxc:5555aaaa");
            p.K.Store.SaveGame(p.Id, seen);
        }
        if (check == "d3d12Unsampled") AmdFile(AmdAppCache.DxcKey(p.Exe)[4..]);
        using (check == "held" ? Hold(app) : null) p.Exit();
        p.Poll();   // a file in use at the end goes back at the next poll

        var rec = p.K.Store.LoadGame(p.Id);
        if (check == "none")
        {
            Assert.Contains("dx:000000000000bbbb", rec.CacheKeys);
            return;
        }
        Assert.DoesNotContain(rec.CacheKeys, k => k.StartsWith("dx:"));
        Assert.True(p.Record!.DxShared);
        Assert.Equal(65536, new FileInfo(app).Length);   // the app's own file back
        Assert.Equal(65536, new FileInfo(old).Length);
        Assert.False(Directory.Exists(Path.Combine(_root, "AMD", "DxCache.scskiller-quarantine")));
    }

    /// <summary>AMD, DirectX 11, another discovered game with the same exe name: the key the game was seen holding is learned,
    /// but its set-aside files, the other game's cache too, go back instead of being deleted.</summary>
    [Fact]
    public async Task On_AMD_a_DirectX_11_before_run_never_deletes_a_same_named_games_set_aside_cache()
    {
        var twinDir = Directory.CreateDirectory(Path.Combine(_root, "Twin")).FullName;
        var twinExe = Path.Combine(twinDir, Path.GetFileName(_game.ExePath));
        File.WriteAllBytes(twinExe, new byte[4096]);
        var p = await AmdDx11Rig([_game, new Game("test:twin", "Twin", Store.Other, twinDir, twinExe)]);
        var old = AmdDx("9999", 65536);
        p.K.StartBeforeAfter(p.Id, 5);
        p.Running.Add(p.Exe);
        p.Poll();
        p.Launch(360);
        AmdDx("9999", 100);
        var seen = p.K.Store.LoadGame(p.Id);
        seen.GameKeys.Add("dx:0000000000009999");
        p.K.Store.SaveGame(p.Id, seen);
        p.Exit();

        Assert.Contains("dx:0000000000009999", p.K.Store.LoadGame(p.Id).CacheKeys);
        Assert.Equal(65536, new FileInfo(old).Length);
    }

    /// <summary>AMD, DirectX 11: a launcher that makes its set-aside file again before the game is launched from it doesn't
    /// hide the game's key: the one made since the launch is learned, the launcher's files go back.</summary>
    [Fact]
    public async Task On_AMD_a_launchers_DirectX_11_file_made_before_the_launch_leaves_the_games_key_learned()
    {
        var p = await AmdDx11Rig();
        var (launcher, old) = (AmdDx("aaaa", 65536), AmdDx("9999", 65536));
        p.K.StartBeforeAfter(p.Id, 5);
        p.Running.Add(p.Exe);
        p.Poll();
        var stamp = p.Launch(360);
        AmdDx("aaaa", 100);
        File.SetCreationTimeUtc(launcher, DateTimeOffset.FromUnixTimeMilliseconds(stamp).UtcDateTime.AddMinutes(-2));
        AmdDx("9999", 100);   // the game's
        p.Exit();

        Assert.Contains("dx:0000000000009999", p.K.Store.LoadGame(p.Id).CacheKeys);
        Assert.False(p.Record!.DxShared);
        Assert.Equal(65536, new FileInfo(launcher).Length);
        Assert.False(File.Exists(old));   // the old cache deleted, then the run's by the clear before the compile
    }

    /// <summary>AMD: a DirectX 11 game's own recorder (input layouts and frame times) serves the pair, without frames_only.</summary>
    [Fact]
    public async Task On_AMD_a_DirectX_11_pair_uses_the_recorder_as_it_is()
    {
        var p = await Dx11PairRig("amd-1", GpuVendor.Amd);
        Assert.True(ScsKiller.IsOurProxy(Proxy11));
        Assert.DoesNotContain("frames_only", File.ReadAllText(Ini));
        Assert.Null(p.K.BeforeAfterBlocked(p.Id));
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.DoesNotContain("frames_only", File.ReadAllText(Ini));
        p.K.EndBeforeAfter(p.Id, compile: false);
        Assert.True(ScsKiller.IsOurProxy(Proxy11));   // it was there before the pair
    }

    /// <summary>AMD: the scan published the game before its reconcile installed the recorder (the window was clicked
    /// meanwhile). The before run's click installs and stamps it before anything is cleared or launched, and each launch
    /// checks it again as the proxy will (a folder left on the revocation list refuses); one that can't go in refuses the
    /// click with why, and nothing is cleared.</summary>
    [Fact]
    public async Task A_run_is_launched_only_with_its_recorder_in_and_stamped()
    {
        var p = await Dx11PairRig("amd-1", GpuVendor.Amd, manage: false);
        p.K.ManageRecorders = true;
        Assert.False(File.Exists(Proxy11));
        Assert.True(p.K.Games.Single().RecorderEffective);
        Assert.Null(p.K.BeforeAfterBlocked(p.Id));
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.True(ScsKiller.IsOurProxy(Proxy11) && Attested(_game));
        p.K.BeforeAfterRecorderReady(p.Id);   // the launch's own check

        File.WriteAllText(ScsKiller.Revoked, ScsKiller.FileKey(_exeDir) + "\r\n");   // revoked behind the app's back: the launch's check stamps it again
        Assert.False(Attested(_game));
        p.K.BeforeAfterRecorderReady(p.Id);
        Assert.True(Attested(_game));
        // the revocation list held open: the folder can't come off it, and the proxy would refuse
        File.WriteAllText(ScsKiller.Revoked, ScsKiller.FileKey(_exeDir) + "\r\n");
        using (new FileStream(ScsKiller.Revoked, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.StartsWith(ScsKiller.RecorderNotReadyNote, Assert.Throws<InvalidOperationException>(() => p.K.BeforeAfterRecorderReady(p.Id)).Message);
        p.K.BeforeAfterRecorderReady(p.Id);
        Assert.True(Attested(_game));

        p.K.EndBeforeAfter(p.Id, compile: false);
        p.K.SetRecorderOverride(p.Id, RecorderOverride.Off);
        var rec = p.K.Store.LoadGame(p.Id);
        rec.Recorder = RecorderOverride.Default;   // on again, not reconciled yet
        p.K.Store.SaveGame(p.Id, rec);
        p.K.RefreshGame(p.Id);
        File.WriteAllBytes(p.DriverCache, new byte[65536]);
        Assert.False(File.Exists(Proxy11));
        InvalidOperationException e;
        using (new FileStream(_proxy, FileMode.Open, FileAccess.Read, FileShare.None))   // the recorder can't be copied
            e = Assert.Throws<InvalidOperationException>(() => p.K.StartBeforeAfter(p.Id, 5));
        Assert.StartsWith(ScsKiller.RecorderNotReadyNote, e.Message);
        Assert.Null(p.Record);
        Assert.True(File.Exists(p.DriverCache));
        Assert.False(File.Exists(Proxy11));
    }

    /// <summary>A launch as the D3D11 recorder writes it: frames every 10 ms with a 300 ms one per <paramref name="hitches"/>
    /// spread over play, and a csv launch without create rows, marked #api,d3d11 (<paramref name="d3d11"/> false: a D3D12
    /// launch that happened to create nothing).</summary>
    SessionSnapshot Run11(long unix, int hitches, double playSeconds, bool d3d11 = true)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, $"run11-{unix}")).FullName;
        double last = playSeconds * 1000 + 10_000;
        var slow = Enumerable.Range(0, hitches).Select(i => Math.Round((5000 + i * (playSeconds * 1000 - 5000) / hitches) / 10) * 10).ToHashSet();
        var ends = new List<double>();
        for (double t = 0; t < last; t += 10)
        {
            ends.Add(t);
            if (slow.Contains(t)) ends.Add(t += 300);
        }
        var csv = Path.Combine(dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, [$"#session,{unix},Game.exe", "#clock,0.0", .. d3d11 ? new[] { "#api,d3d11" } : [], $"#end,{unix + (long)last},{last}.0"]);
        var bin = Path.Combine(dir, FrameLog.FileName);
        File.WriteAllBytes(bin, FrameLogTests.Launch(unix, 0, ends));
        return Snapshots.Of(FrameLog.Read(bin, csv, "Game.exe")!, null, "driver-1", "610.88", "build-7", null);
    }

    /// <summary>Two runs the D3D11 recorder wrote (#api,d3d11, no create rows): the run counts, the comparison has no ready share and no
    /// shader split, every hitch of 50 ms or more is counted, and the share text claims no shader stutter.</summary>
    [Fact]
    public void A_DirectX_11_pair_compares_hitches_without_a_shader_split()
    {
        const long unix = 1_700_000_000_000;
        var (before, after) = (Run11(unix, 12, 300), Run11(unix + 1_000_000, 2, 300));
        Assert.Null(BeforeAfter.RunProblem(before, before: true));
        Assert.Null(BeforeAfter.RunProblem(after, before: false));
        var c = BeforeAfter.Compare(before, after);
        Assert.True(c.Dx11);
        Assert.False(c.AlreadySmooth);
        Assert.Equal((12, 2, 0, 0), (c.Before.Hitches, c.After.Hitches, c.Before.ShaderStutters, c.After.ShaderStutters));
        Assert.Equal((3.6, 300.0), (Math.Round(c.Before.HitchSeconds, 3), c.Before.WorstHitchMs));
        Assert.Null(c.Before.ReadyShare);
        var text = BeforeAfter.ShareText("Game", c);
        Assert.Equal("Game on DirectX 11: 12 → 2 stutters of 50 ms or more in 5 minutes of play, before and after SCSKiller precompiled its shaders. scskiller.com", text);
        Assert.DoesNotContain("shader compilation stutter", text);
        Assert.DoesNotContain(";", BeforeAfter.Dx11Label);
        Assert.True(before.D3D11);
        var d3d12 = Run11(unix + 2_000_000, 12, 300, d3d11: false);   // no creates, no marker: not DirectX 11
        Assert.False(d3d12.D3D11);
        Assert.False(BeforeAfter.Compare(d3d12, Run11(unix + 3_000_000, 2, 300, d3d11: false)).Dx11);
    }

    /// <summary>The proxy as d3d11.dll with frames_only=1 (`selftest rec11frames`): every present is a frame, the csv has its
    /// session and #api,d3d11, and nothing is recorded: no scskiller.db, no input layout. Without it the same run records the layout.</summary>
    [Fact]
    public void The_proxy_as_d3d11_dll_with_frames_only_writes_frames_and_records_nothing()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        string Run(string name, string ini)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
            File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
            File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, ScsKiller.Dll11));
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), $"[scskiller]\r\nmode=record\r\n{ini}");
            using var proc = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), "rec11frames 5") { RedirectStandardOutput = true })!;
            var o = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            Assert.Equal(0, proc.ExitCode);
            Assert.Contains("frames 5", o);
            var csv = File.ReadAllLines(Path.Combine(dir, "scskiller_creates.csv"));
            Assert.Contains(csv, l => l.StartsWith("#session,"));
            Assert.Contains("#api,d3d11", csv);
            return dir;
        }
        var only = Run("rec11-frames-only", "frames_only=1\r\n");
        Assert.False(File.Exists(Path.Combine(only, "scskiller.db")));
        Assert.Contains("frame times only", File.ReadAllText(Path.Combine(only, "scskiller.log")));
        var full = Run("rec11-frames-full", "");
        Assert.Single(PsoDb.Read(Path.Combine(full, "scskiller.db")), r => r.Tag == 'D');
    }
}
