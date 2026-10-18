using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// Offline sessions without EasyAntiCheat (Games.OfflineEac), on the fake game: its exe is the selftest, its folder gets
// EasyAntiCheat's markers. No real game is started.
public partial class AppTests
{
    /// <summary>The fake game listed as an offline-session game while <paramref name="body"/> runs; the child processes
    /// don't stamp themselves (the selftest's own stamp is the app's, here the test's).</summary>
    async Task Listed(Func<Task> body, string exe = "Fake-Win64-Shipping.exe", string? id = null, string? eacProduct = null)
    {
        id ??= _game.Id;   // this test's: the cleanup helper's mutex is named by it, shared with every run in the logon session
        var (list, unarmed) = (OfflineEac.Current, Environment.GetEnvironmentVariable("SCSKILLER_SELFTEST_UNARMED"));
        OfflineEac.Current = [new(id, "Fake Game", exe, "480", eacProduct)];
        Environment.SetEnvironmentVariable("SCSKILLER_SELFTEST_UNARMED", "1");
        try { await body(); }
        finally
        {
            OfflineEac.Current = list;
            Environment.SetEnvironmentVariable("SCSKILLER_SELFTEST_UNARMED", unarmed);
        }
    }

    /// <summary>EasyAntiCheat's markers beside the exe and in the install root, as its games ship them.</summary>
    void EasyAntiCheatBeside()
    {
        Directory.CreateDirectory(Path.Combine(_exeDir, "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(_exeDir, "start_protected_game.exe"), [0]);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
    }

    static string[] Names(string dir) => [.. Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>().Order()];

    static void WaitExit(SafeProcessHandle h)
    {
        using var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(h.DangerousGetHandle(), false) };
        Assert.True(exited.WaitOne(TimeSpan.FromSeconds(60)));
    }

    /// <summary>The game's exe is the built selftest, the app's proxy the built one; null when this checkout's isn't built.</summary>
    ScsKiller? OfflineKiller()
    {
        if (OwnWarmExe() is not { } warm) return null;
        var bin = Path.GetDirectoryName(warm)!;
        File.Copy(Path.Combine(bin, "selftest.exe"), _game.ExePath, true);
        File.Copy(Path.Combine(bin, "d3d12.dll"), _proxy, true);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string> { "steam" };
        k.OfflineArguments = "anticheat -";
        return k;
    }

    /// <summary>An ini bound to one process the app started (suspended, the ini placed, resumed) admits that process even
    /// with EasyAntiCheat's markers beside the exe and in the install root; any other process with the same files there is a
    /// pass-through, as is the bound one when another anti-cheat's marker is there or EasyAntiCheat's client is loaded
    /// before its device.</summary>
    [Fact]
    public async Task The_proxy_admits_only_the_process_the_app_started_for_an_offline_session()
    {
        if (OwnWarmExe() is not { } warm) return;
        var bin = Path.GetDirectoryName(warm)!;
        var dir = Path.Combine(_root, "offline", "bin");
        Directory.CreateDirectory(Path.Combine(dir, "EasyAntiCheat"));
        Directory.CreateDirectory(Path.Combine(_root, "offline", "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(dir, "start_protected_game.exe"), [0]);
        var exe = Path.Combine(dir, "selftest.exe");
        File.Copy(Path.Combine(bin, "selftest.exe"), exe);
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        var ini = Path.Combine(dir, "scskiller.ini");
        var client = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "eac")).FullName, "EasyAntiCheat_x64.dll");
        File.Copy(Path.Combine(bin, "fakenext.dll"), client);
        int Computes()
        {
            var db = Path.Combine(dir, "scskiller.db");
            var n = File.Exists(db) ? PsoDb.Read(db).Count(r => r.Tag == 'C') : 0;
            File.Delete(db);
            return n;
        }
        int Bound(string args)
        {
            using (var h = ScsKiller.StartAttested(exe, args, (_, pid, created) =>
                       File.WriteAllText(ini, "[scskiller]\r\nmode=record\r\nframes=0\r\nroot=..\r\n"
                           + ScsKiller.Signed(exe, "..", ScsKiller.Stamp(exe)!, ScsKiller.BoundTo(pid, created)))))
                WaitExit(h);
            return Computes();
        }
        await Listed(() =>
        {
            Assert.Equal(2, Bound("anticheat -"));
            Assert.Contains("pid=", File.ReadAllText(ini));

            // the same files, the ini still bound to that process: another launch records nothing
            var start = new ProcessStartInfo(exe, "anticheat -") { RedirectStandardOutput = true };
            using (var p = Process.Start(start)!)
            {
                Assert.Contains("created 0x00000000 0x00000000", p.StandardOutput.ReadToEnd());
                p.WaitForExit();
            }
            Assert.Equal(0, Computes());

            Assert.Equal(0, Bound($"anticheat \"+{client}\""));   // EasyAntiCheat's client loaded before the device
            File.WriteAllBytes(Path.Combine(dir, "BEService_x64.exe"), [0]);
            Assert.Equal(0, Bound("anticheat -"));   // another anti-cheat's marker still refuses
            return Task.CompletedTask;
        });
    }
    /// <summary>An offline session: refused unless allowed for the game, confirmed and Steam runs (nothing written then);
    /// started, it records the game's process, and the moment that exits (by its handle: here the watcher would still
    /// call the game running) the recording is imported and the folder has exactly the entries it had before.</summary>
    [Fact]
    public async Task An_offline_session_records_and_leaves_the_folder_as_it_was_when_its_process_exits()
    {
        EasyAntiCheatBeside();
        if (OfflineKiller() is not { } k) return;
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            var s = k.Games.Single();
            Assert.Equal((AntiCheat.EasyAntiCheat, true, false), (s.AntiCheat, s.OfflineEligible, s.OfflineRecord));
            var before = Names(_exeDir);

            void Refused(string why, bool confirmed = true) =>
                Assert.Contains(why, Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(_game.Id, confirmed); }).Message);
            Refused("allow offline sessions");
            k.SetOfflineRecording(_game.Id, true);
            Refused("confirm", confirmed: false);
            k.ProcessNames = () => new HashSet<string>();
            Refused(ScsKiller.SteamFirst);
            Assert.Equal(before, Names(_exeDir));

            k.ProcessNames = () => k.Games.Single().OfflineRunning ? new HashSet<string> { "steam", "Fake-Win64-Shipping" } : new HashSet<string> { "steam" };
            await k.StartOfflineSession(_game.Id, confirmed: true).WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Equal(before, Names(_exeDir));
            Assert.Equal(2, PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Count(r => r.Tag == 'C'));
            var rec = k.Store.LoadGame(_game.Id);
            Assert.Equal((null, 0, null), (rec.OfflineSession, rec.RecorderFiles.Count, rec.RecorderExe));
            Assert.False(k.Games.Single().OfflineRunning);
            // its report is kept where the game page reads it, as a recorded game's last session
            Assert.True(File.Exists(Path.Combine(ScsKiller.OfflineSessionDir(k.Store, _game.Id), "scskiller_creates.csv")));
            k.RefreshGame(_game.Id);
            Assert.True(k.Games.Single().LastSession?.Requests > 0);
        });
    }

    /// <summary>An offline session places the dll a risk session would (<see cref="ScsKiller.RiskDll"/>): an Unreal 4 game that
    /// may run on either DirectX gets d3d11.dll on AMD, others d3d12.dll. A launch that loaded neither leaves the never-saw
    /// note naming the other DirectX, and the folder as it was.</summary>
    [Theory]
    [InlineData("D3D11 or D3D12", "4.26", ScsKiller.Dll11, ScsKiller.NeverSawDx12Note)]
    [InlineData("D3D11 or D3D12", "5.4", ScsKiller.Dll12, ScsKiller.NeverSawDx11Note)]
    [InlineData("D3D12", "4.26", ScsKiller.Dll12, ScsKiller.NeverSawNote)]
    public async Task An_offline_session_places_the_dll_a_risk_session_would_and_says_when_it_never_loaded(string api, string version, string dll, string note)
    {
        EasyAntiCheatBeside();
        PingAsGame();
        var k = Killer(new FakeReader(Unreal with { GraphicsApi = api, Version = version }), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }, profile: "amd-1"));
        k.ProcessNames = () => new HashSet<string> { "steam" };
        k.OfflineArguments = "-n 4 127.0.0.1";
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            Assert.True(k.Games.Single().OfflineEligible);
            k.SetOfflineRecording(_game.Id, true);
            var before = Names(_exeDir);
            var session = k.StartOfflineSession(_game.Id, confirmed: true);
            Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, dll)));
            Assert.False(File.Exists(Path.Combine(_exeDir, dll == ScsKiller.Dll11 ? ScsKiller.Dll12 : ScsKiller.Dll11)));
            Assert.Contains(dll, k.Store.LoadGame(_game.Id).RecorderFiles.Keys);
            await session.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(before, Names(_exeDir));
            Assert.Null(k.Store.LoadGame(_game.Id).OfflineSession);
            Assert.Equal(note, k.Games.Single().SessionNote);
        }, exe: Path.GetFileName(_game.ExePath));
    }

    /// <summary>The session's process exits while another runs from the game folder: what would load into it goes at once,
    /// the data files and the session stay. The cleanup helper (SCSKiller closed meanwhile), given that process's pid, which
    /// has exited, finishes it; so does the next start.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_offline_session_left_behind_is_cleaned_up_by_the_helper_or_the_next_start(bool helper)
    {
        EasyAntiCheatBeside();
        if (OfflineKiller() is not { } k) return;
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            k.SetOfflineRecording(_game.Id, true);
            var before = Names(_exeDir);
            k.OfflineRuns = _ => true;
            await k.StartOfflineSession(_game.Id, confirmed: true).WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal(before.Concat(["scskiller.db", "scskiller.log", "scskiller_creates.csv"]).Order(), Names(_exeDir));
            Assert.NotEqual(0, k.Store.LoadGame(_game.Id).OfflineSession!.Pid);

            Assert.False(k.OfflineBlocksUpdate);   // its process has exited and no helper runs: files left don't hold updates back
            var store = new AppStore(Path.Combine(_root, "data"));
            if (helper) Assert.Equal(0, ScsKiller.RunOfflineCleanup(store, _game.Id, TimeSpan.Zero, othersRun: OurOthersRun));
            else
            {
                var next = Killer(new FakeReader(Unreal));   // the next start
                next.ProcessNames = () => new HashSet<string>();
                await next.ScanAsync(default);
            }
            Assert.Equal(before, Names(_exeDir));
            Assert.Null(store.LoadGame(_game.Id).OfflineSession);
            Assert.Equal(2, PsoDb.Read(Path.Combine(store.GameDir(_game.Id), "recording.db")).Count(r => r.Tag == 'C'));
        });
    }

    /// <summary>A write cut off midway leaves a temp name: it goes by its name. A placed file still holding what the session
    /// placed goes; a file that was there before stays; the session stays pending while one can't be deleted yet.</summary>
    [Fact]
    public void An_interrupted_offline_session_leaves_nothing_it_created()
    {
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.SteamAppIdFile), "480");   // the user's own
        var before = Names(_exeDir);
        var store = Journal(["scskiller.ini"], created: ["d3d12.dll.scskiller-new", "scskiller.ini.scskiller-new"]);
        File.WriteAllBytes(Path.Combine(_exeDir, "d3d12.dll.scskiller-new"), [1, 2, 3]);

        using (new FileStream(Path.Combine(_exeDir, "scskiller.ini"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(1, ScsKiller.RunOfflineCleanup(store, _game.Id, TimeSpan.Zero, othersRun: OurOthersRun));
        Assert.NotNull(store.LoadGame(_game.Id).OfflineSession);
        Assert.Equal(before.Append("scskiller.ini").Order(), Names(_exeDir));

        Assert.Equal(0, ScsKiller.RunOfflineCleanup(store, _game.Id, TimeSpan.Zero, othersRun: OurOthersRun));
        Assert.Equal(before, Names(_exeDir));
        Assert.Null(store.LoadGame(_game.Id).OfflineSession);
    }
    /// <summary>Only a listed game whose install has EasyAntiCheat and nothing else gets the option; anything else has none,
    /// and a session is refused with nothing written.</summary>
    [Theory]
    [InlineData("EasyAntiCheat", null, false, false)]          // not on the list
    [InlineData("BEService_x64.exe", null, true, false)]      // BattlEye
    [InlineData(null, null, true, false)]                     // no anti-cheat: the normal recorder's
    [InlineData("EasyAntiCheat", "Other.exe", true, false)]   // listed under another exe
    [InlineData("EasyAntiCheat", null, true, true)]
    public async Task Offline_sessions_are_only_for_listed_easyanticheat_games(string? marker, string? listedExe, bool listed, bool eligible)
    {
        if (marker != null)
            if (Path.HasExtension(marker)) File.WriteAllBytes(Path.Combine(_exeDir, marker), [0]);
            else Directory.CreateDirectory(Path.Combine(_exeDir, marker));
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            Assert.Equal(eligible, k.Games.Single().OfflineEligible);
            if (!eligible) Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(_game.Id, true));
            Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(_game.Id, confirmed: true); });
        }, exe: listedExe ?? "Fake-Win64-Shipping.exe", id: listed ? _game.Id : "steam:1");
        Assert.DoesNotContain("d3d12.dll", Names(_exeDir));
    }

    /// <summary>A copy of a listed game added by hand qualifies only by the listed exe, its folder confirmed, and the
    /// EasyAntiCheat product (productid/sandboxid of EasyAntiCheat\Settings.json beside the exe) the entry names.</summary>
    [Theory]
    [InlineData("aaaa/bbbb", "aaaa", "bbbb", null, true, true)]
    [InlineData("aaaa/bbbb", "aaaa", "cccc", null, true, false)]    // another sandbox
    [InlineData("aaaa/bbbb", "cccc", "bbbb", null, true, false)]    // another product
    [InlineData("aaaa/bbbb", null, null, null, true, false)]        // EasyAntiCheat without its settings
    [InlineData(null, "aaaa", "bbbb", null, true, false)]           // listed without its product: the store's copy only
    [InlineData("aaaa/bbbb", "aaaa", "bbbb", "Other.exe", true, false)]
    [InlineData("aaaa/bbbb", "aaaa", "bbbb", null, false, false)]   // its folder not confirmed
    public async Task A_copy_added_by_hand_is_offered_offline_sessions_only_as_the_listed_easyanticheat_product(
        string? listed, string? product, string? sandbox, string? listedExe, bool confirmed, bool eligible)
    {
        Directory.CreateDirectory(Path.Combine(_exeDir, "EasyAntiCheat"));
        if (product != null)
            File.WriteAllText(Path.Combine(_exeDir, "EasyAntiCheat", "Settings.json"),
                $"{{ \"title\": \"Fake\", \"executable\": \"{Path.GetFileName(_game.ExePath)}\", \"productid\": \"{product}\", \"sandboxid\": \"{sandbox}\" }}");
        var manual = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        var game = manual.Add(new ManualEntry(_game.ExePath, _game.InstallDir, "Fake Game", confirmed)).Game;
        var k = Killer(new FakeReader(Unreal), sources: [manual]);
        k.ProcessNames = () => new HashSet<string>();
        await Listed(async () =>
        {
            var s = (await k.ScanAsync(default)).Single();
            Assert.Equal((game.Id, AntiCheat.EasyAntiCheat, eligible), (s.Game.Id, s.AntiCheat, s.OfflineEligible));
            if (!eligible) Assert.Throws<InvalidOperationException>(() => k.SetOfflineRecording(game.Id, true));
            Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(game.Id, confirmed: true); });
            if (!eligible) return;
            k.SetOfflineRecording(game.Id, true);
            File.WriteAllText(Path.Combine(_exeDir, "EasyAntiCheat", "Settings.json"), """{ "productid": "cccc", "sandboxid": "bbbb" }""");
            Assert.Contains("aren't available for this game", Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(game.Id, confirmed: true); }).Message);
        }, exe: listedExe ?? "Fake-Win64-Shipping.exe", id: "steam:1", eacProduct: listed);
        Assert.DoesNotContain("d3d12.dll", Names(_exeDir));
    }

    /// <summary>A listed game whose install has another anti-cheat besides EasyAntiCheat: a session is refused.</summary>
    [Fact]
    public async Task An_offline_session_is_refused_with_another_anti_cheat_beside_easyanticheat()
    {
        EasyAntiCheatBeside();
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "BattlEye"));
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            if (k.Games.Single() is { AntiCheat: not AntiCheat.EasyAntiCheat } s)   // the scan met BattlEye first: not offered at all
            {
                Assert.False(s.OfflineEligible);
                return;
            }
            k.SetOfflineRecording(_game.Id, true);
            var before = Names(_exeDir);
            Assert.Contains("another anti-cheat", Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(_game.Id, confirmed: true); }).Message);
            Assert.Equal(before, Names(_exeDir));
        });
    }

    /// <summary>A session as a crash leaves it: <paramref name="placed"/> in the manifest by hash, written ("x") unless
    /// <paramref name="write"/> is false, <paramref name="created"/> (the names it made itself), and <paramref name="data"/>
    /// (other files: the recorder's data files, a temp name) written too.</summary>
    AppStore Journal(string[] placed, bool write = true, string[]? data = null, int pid = 0, long started = 0, string? exe = null, bool resumed = true,
        string[]? created = null)
    {
        var store = new AppStore(Path.Combine(_root, "data"));
        var rec = store.LoadGame(_game.Id);
        rec.OfflineSession = new(_game.Id, exe ?? _game.ExePath, _game.InstallDir, pid, started, resumed, created);
        rec.RecorderFiles = placed.ToDictionary(f => f, _ => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("x"u8)));
        store.SaveGame(_game.Id, rec);
        foreach (var f in (write ? placed : []).Concat(data ?? [])) File.WriteAllText(Path.Combine(_exeDir, f), "x");
        return store;
    }
    /// <summary>A session cut off, then a mod's d3d12.dll and the user's own steam_appid.txt put under its names: the
    /// cleanup deletes only what is proven SCSKiller's, by its own names, the proxy's export or the bytes it wrote, and
    /// ends, leaving those. Without them, nothing it created is left.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_offline_cleanup_deletes_only_what_is_proven_ours(bool replaced)
    {
        var before = Names(_exeDir);
        var store = Journal([ScsKiller.SteamAppIdFile], data: ["d3d12.dll.scskiller-new", "scskiller.ini.scskiller-new", ScsKiller.ArmedFile, "scskiller.log"],
            created: ["d3d12.dll", "d3d12.dll.scskiller-new", "scskiller.ini.scskiller-new", ScsKiller.SteamAppIdFile, ScsKiller.ArmedFile]);
        var (mod, appId) = (Path.Combine(_exeDir, "d3d12.dll"), Path.Combine(_exeDir, ScsKiller.SteamAppIdFile));
        File.Copy(_proxy, mod);
        if (replaced)
        {
            File.WriteAllBytes(mod, [.. "MZ ReShade "u8]);
            File.WriteAllText(appId, "480\r\n");
        }
        Assert.Null(ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { }));
        Assert.Null(store.LoadGame(_game.Id).OfflineSession);
        Assert.Equal((replaced ? before.Append("d3d12.dll").Append(ScsKiller.SteamAppIdFile) : before).Order(), Names(_exeDir));
        if (!replaced) return;
        Assert.Equal("MZ ReShade "u8.ToArray(), File.ReadAllBytes(mod));
        Assert.Equal("480\r\n", File.ReadAllText(appId));
    }

    /// <summary>What a launch loads goes first, and leaves the manifest at once: the recording's merge waiting for its lock
    /// doesn't hold d3d12.dll back, and a d3d12.dll put there meanwhile is never deleted.</summary>
    [Fact]
    public async Task An_offline_cleanup_takes_the_dll_out_before_it_waits_for_the_recording()
    {
        var store = Journal(["d3d12.dll", "scskiller.ini", ScsKiller.SteamAppIdFile]);
        using (var db = File.Create(Path.Combine(_exeDir, "scskiller.db"))) PsoDb.WriteBlob(db, PsoDb.Hex(System.Security.Cryptography.SHA1.HashData("x"u8)), "x"u8);
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using (Recordings.Lock(Path.Combine(store.GameDir(_game.Id), "recording.db")))
            {
                held.Set();
                release.Wait();
            }
        });
        held.Wait();
        var cleanup = Task.Run(() => ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { }));
        await Until(() => store.LoadGame(_game.Id).RecorderFiles.Count == 0);
        Assert.False(cleanup.IsCompleted);
        Assert.Equal(["Fake-Win64-Shipping.exe", "scskiller.db"], Names(_exeDir));
        File.WriteAllText(Path.Combine(_exeDir, "d3d12.dll"), "the user's");
        release.Set();
        await holder;
        Assert.Null(await cleanup);
        Assert.Equal("the user's", File.ReadAllText(Path.Combine(_exeDir, "d3d12.dll")));
    }
    /// <summary>A placed file gone leaves the manifest, and one with other bytes than the session placed is someone else's: a
    /// d3d12.dll the user puts there before the retry stays.</summary>
    [Fact]
    public void An_offline_cleanup_retry_leaves_a_file_put_there_since()
    {
        var store = Journal(["d3d12.dll"], data: ["scskiller.log"]);
        using (new FileStream(Path.Combine(_exeDir, "scskiller.log"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(["scskiller.log"], ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { }));
        Assert.Empty(store.LoadGame(_game.Id).RecorderFiles);
        File.WriteAllText(Path.Combine(_exeDir, "d3d12.dll"), "the user's");
        Assert.Null(ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { }));
        Assert.Equal("the user's", File.ReadAllText(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>A 1.2 session (its journal's Original and Created names, a scskiller.armed it made, a temp name) left when
    /// 1.3.0 starts: cleaned up by what it placed, by hash, and those names.</summary>
    [Fact]
    public void A_12_offline_session_is_cleaned_up_by_identity()
    {
        var before = Names(_exeDir);
        var store = Journal(["d3d12.dll", "scskiller.ini"], data: [ScsKiller.ArmedFile, "scskiller.ini.scskiller-new", "scskiller.db"]);
        var state = Path.Combine(store.GameDir(_game.Id), "state.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(state))!;
        json["OfflineSession"]!["Original"] = new System.Text.Json.Nodes.JsonArray([.. before.Select(n => System.Text.Json.Nodes.JsonValue.Create(n))]);
        json["OfflineSession"]!["Created"] = new System.Text.Json.Nodes.JsonArray(["d3d12.dll", "scskiller.ini", ScsKiller.ArmedFile, "scskiller.ini.scskiller-new", "scskiller.db"]);
        File.WriteAllText(state, json.ToJsonString());
        Assert.Equal(0, ScsKiller.RunOfflineCleanup(store, _game.Id, TimeSpan.Zero, othersRun: OurOthersRun));
        Assert.Equal(before, Names(_exeDir));
        Assert.Null(store.LoadGame(_game.Id).OfflineSession);
    }
    /// <summary>An offline session's cleanup in an EasyAntiCheat game whose d3d12.dll is a proxy of ours with other bytes than
    /// the session recorded (another build's), or one its manifest doesn't name: it goes by its export, whatever the
    /// manifest says.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_offline_cleanup_takes_our_proxy_out_of_an_easyanticheat_game_by_its_export(bool named)
    {
        EasyAntiCheatBeside();
        var store = Journal(named ? ["d3d12.dll"] : [], write: false);
        File.Copy(_proxy, Path.Combine(_exeDir, "d3d12.dll"));
        Assert.Null(ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { }));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>A 1.2 session whose journal lists scskiller.ini among the names it created: the ini is never taken by its name,
    /// only as every removal takes it, so one the user put there since stays as it is.</summary>
    [Fact]
    public void A_12_offline_session_never_takes_an_ini_by_its_name()
    {
        var store = Journal(["d3d12.dll", "scskiller.ini"], created: ["d3d12.dll", "scskiller.ini"]);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.ini"), "[settings]\r\nfoo=mine\r\n");
        ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { });
        Assert.Equal("[settings]\r\nfoo=mine\r\n", File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>Only names the session made itself go by name: a temp name or a scskiller.armed it didn't make stays (a mod's,
    /// put there since). A steam_appid.txt goes only with the bytes the session wrote: one it added and the user edited
    /// since stays, as does the user's own, there before.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_offline_cleanup_takes_only_what_the_session_made(bool sessionAppId)
    {
        var appId = Path.Combine(_exeDir, ScsKiller.SteamAppIdFile);
        if (!sessionAppId) File.WriteAllText(appId, "480");   // the user's own
        var store = Journal(sessionAppId ? ["d3d12.dll", ScsKiller.SteamAppIdFile] : ["d3d12.dll"],
            data: ["d3d12.dll.scskiller-new", "mod.scskiller-new", ScsKiller.ArmedFile],
            created: sessionAppId ? ["d3d12.dll.scskiller-new", ScsKiller.SteamAppIdFile] : ["d3d12.dll.scskiller-new"]);
        File.WriteAllText(appId, sessionAppId ? "480 edited" : "480");
        Assert.Null(ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { }));
        Assert.Equal(["Fake-Win64-Shipping.exe", "mod.scskiller-new", ScsKiller.ArmedFile, ScsKiller.SteamAppIdFile], Names(_exeDir).Order(StringComparer.Ordinal));
        Assert.Equal(sessionAppId ? "480 edited" : "480", File.ReadAllText(appId));
    }

    /// <summary>A journaled name is gone only when the folder, listed whole, lacks it: a folder that can't be listed keeps
    /// every name and the session for a retry, which takes them once it can be.</summary>
    [Fact]
    public void An_offline_cleanup_takes_nothing_in_a_folder_it_cannot_list_for_gone()
    {
        var store = Journal(["d3d12.dll"]);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var info = new DirectoryInfo(_exeDir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            Assert.NotNull(ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { }));
            Assert.Contains("d3d12.dll", store.LoadGame(_game.Id).RecorderFiles.Keys);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
        Assert.True(File.Exists(dll));
        Assert.Null(ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { }));
        Assert.False(File.Exists(dll));
        Assert.Null(store.LoadGame(_game.Id).OfflineSession);
    }

    /// <summary>A game folder that is gone, its drive still there (the game uninstalled): the session is over. One on a
    /// drive that isn't there proves nothing gone: it stays pending, without holding updates back.</summary>
    [Fact]
    public void An_offline_cleanup_ends_for_a_folder_gone_and_waits_for_a_drive_gone()
    {
        var store = Journal(["d3d12.dll"], write: false, exe: Path.Combine(_root, "uninstalled", "Game", "game.exe"));
        Assert.Equal(0, ScsKiller.RunOfflineCleanup(store, _game.Id, TimeSpan.Zero, othersRun: OurOthersRun));
        Assert.Null(store.LoadGame(_game.Id).OfflineSession);

        var free = "ZYXWVUTSRQPONMLKJIHGFED".Select(c => $"{c}:\\").First(d => !Directory.Exists(d));
        store = Journal(["d3d12.dll"], write: false, exe: Path.Combine(free, "Games", "Game", "game.exe"));
        Assert.Equal(1, ScsKiller.RunOfflineCleanup(store, _game.Id, TimeSpan.Zero, othersRun: OurOthersRun));
        Assert.NotNull(store.LoadGame(_game.Id).OfflineSession);
        Assert.True(ScsKiller.Gone(Path.Combine(_root, "uninstalled")));
        Assert.False(ScsKiller.Gone(Path.Combine(free, "Games")));
    }

    /// <summary>A session whose process was never resumed (SCSKiller ended while it set it up) is our own suspended child:
    /// the cleanup ends it (its pid and creation time match) and goes on. A resumed one is the game: left alone.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_offline_cleanup_ends_a_process_never_resumed(bool resumed)
    {
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            var store = Journal(["d3d12.dll"], pid: p.Id, started: ScsKiller.StartedAt(p.Id)!.Value, resumed: resumed);
            var left = ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { });
            Assert.Equal(resumed, !p.HasExited);
            Assert.Equal(resumed, left != null);
            Assert.Equal(resumed, File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        }
        finally { if (!p.HasExited) p.Kill(); }
    }

    /// <summary>An update's apply and an offline session exclude each other: none starts while an apply is under way, and a
    /// running session's process holds the apply back.</summary>
    [Fact]
    public async Task An_update_and_an_offline_session_exclude_each_other()
    {
        EasyAntiCheatBeside();
        if (OfflineKiller() is not { } k) return;
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            k.SetOfflineRecording(_game.Id, true);
            Assert.True(k.BeginUpdate());
            Assert.Equal(ScsKiller.UpdateInstalling, Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(_game.Id, confirmed: true); }).Message);
            k.EndUpdate();

            Journal(["d3d12.dll"], write: false, pid: Environment.ProcessId, started: ScsKiller.StartedAt(Environment.ProcessId)!.Value);
            var next = Killer(new FakeReader(Unreal));   // its process runs: an earlier run's session
            Assert.True(next.OfflineBlocksUpdate);
            Assert.False(next.BeginUpdate());
        });
    }

    /// <summary>SCSKiller opens again while the session's game, started by an earlier run, still loads: neither the scan's
    /// cleanup nor its anti-cheat removal touches the files. Once that process is gone (here: another creation time), they go.</summary>
    [Fact]
    public async Task A_live_offline_session_from_an_earlier_run_is_left_alone()
    {
        EasyAntiCheatBeside();
        var store = Journal(["d3d12.dll", "scskiller.ini"], pid: Environment.ProcessId, started: ScsKiller.StartedAt(Environment.ProcessId)!.Value);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.RemovalRetryInterval = TimeSpan.Zero;
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            await k.CheckRecorderGames();
            Assert.True(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
            Assert.True(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));

            var rec = store.LoadGame(_game.Id);
            rec.OfflineSession = rec.OfflineSession! with { Started = rec.OfflineSession.Started + 1 };
            store.SaveGame(_game.Id, rec);
            await k.CheckRecorderGames();
            Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
            Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
            Assert.Null(store.LoadGame(_game.Id).OfflineSession);
        });
    }
    /// <summary>Recovery is in place before any file is published: a helper that can't start refuses the session.</summary>
    [Fact]
    public async Task An_offline_session_whose_cleanup_helper_cannot_start_is_refused()
    {
        EasyAntiCheatBeside();
        if (OfflineKiller() is not { } k) return;
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            k.SetOfflineRecording(_game.Id, true);
            var before = Names(_exeDir);
            (k.CleanupHelper, k.AddRunOnce) = (Path.Combine(_root, "missing.exe"), (_, _) => { });
            Assert.Contains("didn't start", Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(_game.Id, confirmed: true); }).Message);
            Assert.Equal(before, Names(_exeDir));
            Assert.Null(k.Store.LoadGame(_game.Id).OfflineSession);
        });
    }

    /// <summary>Windows deletes a RunOnce value before its command runs, unless its name starts with "!".</summary>
    [Fact]
    public void The_offline_cleanup_logon_entry_outlives_its_run() =>
        Assert.Equal(("!SCSKiller offline cleanup steam:1", "\"C:\\A B\\SCSKiller.exe\" --offline-cleanup \"steam:1\""),
            ScsKiller.RunOnceEntry("steam:1", @"C:\A B\SCSKiller.exe"));

    /// <summary>A mod loader beside the exe under a name the session doesn't use (dinput8.dll, as ModEngine2 or Seamless
    /// Co-op ship it), with its own folder: the session runs, and its cleanup leaves the loader and its files as they were.</summary>
    [Fact]
    public async Task An_offline_session_runs_beside_a_dinput8_mod_loader_and_leaves_it()
    {
        EasyAntiCheatBeside();
        var loader = Planning.MiddlewarePackTests.Pe("dinput8.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(Path.Combine(_exeDir, "dinput8.dll"), loader);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_exeDir, "SeamlessCoop")).FullName, "ersc_settings.ini"), "[GAMEPLAY]");
        if (OfflineKiller() is not { } k) return;
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            k.SetOfflineRecording(_game.Id, true);
            var before = Names(_exeDir);
            k.ProcessNames = () => k.Games.Single().OfflineRunning ? new HashSet<string> { "steam", "Fake-Win64-Shipping" } : new HashSet<string> { "steam" };
            await k.StartOfflineSession(_game.Id, confirmed: true).WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal(before, Names(_exeDir));
            Assert.Equal(loader, File.ReadAllBytes(Path.Combine(_exeDir, "dinput8.dll")));
            Assert.True(File.Exists(Path.Combine(_exeDir, "SeamlessCoop", "ersc_settings.ini")));
        });
    }

    /// <summary>A d3d12.dll of a mod, or one SCSKiller renamed to chain it: the session never chains or renames, it is
    /// refused naming the file, before anything is written or journaled.</summary>
    [Theory]
    [InlineData("d3d12.dll")]
    [InlineData(ScsKiller.ChainName)]
    public async Task An_offline_session_beside_a_d3d12_mod_is_refused_naming_it(string name)
    {
        EasyAntiCheatBeside();
        if (OfflineKiller() is not { } k) return;
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(Path.Combine(_exeDir, name), mod);
        await Listed(async () =>
        {
            await k.ScanAsync(default);
            k.SetOfflineRecording(_game.Id, true);
            var before = Names(_exeDir);
            var e = Assert.Throws<InvalidOperationException>(() => { _ = k.StartOfflineSession(_game.Id, confirmed: true); });
            Assert.Equal($"Fake Game: no offline session: {name} is already in its folder", e.Message);
            Assert.Equal(before, Names(_exeDir));
            Assert.Equal(mod, File.ReadAllBytes(Path.Combine(_exeDir, name)));
            var rec = k.Store.LoadGame(_game.Id);
            Assert.Equal((null, 0, null), (rec.OfflineSession, rec.RecorderFiles.Count, rec.RecorderExe));
        });
    }
}
