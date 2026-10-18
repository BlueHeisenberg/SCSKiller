using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;
using static SCSKiller.Core.BeforeAfterStage;

namespace SCSKiller.Tests.Platform;

// "Remove from library" on a store's game: hidden, never deleted, out of everything automatic.
public partial class AppTests
{
    static string[] Tree(string dir) =>
        [.. new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Select(f => $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}").Order()];

    [Fact]
    public async Task A_hidden_game_stays_hidden_across_rescans_and_restarts_and_comes_back_as_it_was()
    {
        Assert.False(JsonSerializer.Deserialize<GameRecord>("{}", AppStore.Json)!.Hidden);
        var k = await Warmed();
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        CacheFile("0002a91d11111111.nvph", 4096);
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("11111111");
        k.Store.SaveGame(_game.Id, rec);
        var game = Tree(_game.InstallDir);
        var cache = Tree(Path.Combine(_root, "DXCache"));
        var warmed = k.Games.Single().WarmedAt;
        object Record(ScsKiller x) => x.Store.LoadGame(_game.Id) is var r ? (r.WarmedAt, r.WarmedDriverId, string.Join(",", r.CacheKeys), r.Plan?.FilePath, r.PsoPerSecond) : null!;
        var before = Record(k);

        k.HideGame(_game.Id);
        Assert.Empty(k.Games);
        Assert.Equal(_game.Id, Assert.Single(k.HiddenGames).Id);
        Assert.True(k.Store.LoadGame(_game.Id).Hidden);
        await k.RescanAsync(default, userRequested: true);
        Assert.Empty(k.Games);

        var restarted = Killer(new FakeReader(Unreal));
        await restarted.ScanAsync(default);
        Assert.Empty(restarted.Games);
        Assert.Single(restarted.HiddenGames);
        restarted.Settings = restarted.Settings with { ScanAtStart = false };
        var kept = Killer(new FakeReader(Unreal));   // the start that lists the last list again
        await kept.ScanAsync(default);
        Assert.Empty(kept.Games);
        Assert.Single(kept.HiddenGames);

        kept.ShowGame(_game.Id);
        Assert.Empty(kept.HiddenGames);
        var s = kept.Games.Single();
        Assert.Equal((GameStatus.Warmed, warmed), (s.Status, s.WarmedAt));
        Assert.False(kept.Store.LoadGame(_game.Id).Hidden);
        Assert.Equal(before, Record(kept));
        Assert.Equal(game, Tree(_game.InstallDir));
        Assert.Equal(cache, Tree(Path.Combine(_root, "DXCache")));
        await kept.RescanAsync(default);
        Assert.Single(kept.Games);
        Assert.Throws<ArgumentException>(() => kept.ShowGame(_game.Id));
    }

    [Fact]
    public async Task A_game_the_user_added_is_removed_not_hidden()
    {
        var (_, stub, _) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var k = Killer(new FakeReader(Unreal), sources: [Manual()]);
        var id = k.AddManualGame(stub).Game.Id;
        await k.ScanAsync(default);
        Assert.Throws<InvalidOperationException>(() => k.HideGame(id));
        Assert.Single(k.Games);
    }

    [Fact]
    public async Task Adding_a_hidden_games_exe_by_hand_points_to_the_hidden_game()
    {
        var manual = Manual();
        var k = Killer(new FakeReader(Unreal), sources: [new FakeSource([_game]), manual]);
        await k.ScanAsync(default);
        k.HideGame(_game.Id);
        Assert.Equal((_game.Id, true), (k.PreviewManualGame(_game.ExePath).Game.Id, k.PreviewManualGame(_game.ExePath).Existed));
        Assert.Equal((_game.Id, true), (k.AddManualGame(_game.ExePath).Game.Id, k.AddManualGame(_game.ExePath).Existed));
        Assert.Empty(manual.Entries());
    }

    /// <summary>The driver-update rebuild (notified, scheduled, when idle), a queued compile, a plan check and the watcher.</summary>
    [Fact]
    public async Task A_hidden_game_is_left_out_of_every_automatic_compile()
    {
        await Warmed();
        var k = Killer(new FakeReader(Unreal), driver: "101.00");
        k.IdleTime = () => TimeSpan.FromHours(1);
        await k.ScanAsync(default);
        Assert.Single(k.DriverStaleGames());
        k.PauseQueue();
        k.Enqueue(_game.Id);

        k.HideGame(_game.Id);
        Assert.Empty(k.Queue);   // its queued compile goes
        Assert.Empty(k.DriverStaleGames());
        Assert.Empty(k.IdleRewarmGames());
        Assert.Empty(k.StaleGames());
        Assert.False(k.ShouldNotifyStale());
        k.EnqueueWhenIdle(_game.Id);   // the scheduled rebuild, the new-shaders compile
        k.Enqueue(_game.Id);
        k.CheckPlans = true;
        await k.RescanAsync(default);   // its plan checks
        Assert.Empty(k.Queue);
        k.RunningGameExes = () => new HashSet<string>([Path.GetFileName(_game.ExePath)], StringComparer.OrdinalIgnoreCase);
        k.PollGames();   // the watcher: no start, no exit's refresh lists it again
        Assert.Empty(k.Games);
    }

    [Fact]
    public async Task Removing_a_detected_game_turns_its_recorder_off_first()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var folder = Tree(_exeDir);
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));

        k.HideGame(_game.Id);
        Assert.Equal(folder, Tree(_exeDir));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((true, RecorderOverride.Off), (rec.Hidden, rec.Recorder));
        Assert.Empty(rec.RecorderFiles);
        k.ShowGame(_game.Id);
        Assert.Equal((false, RecorderOverride.Off), (k.Games.Single().RecorderInstalled, k.Games.Single().RecorderOverride));   // back as left: switched off
    }

    [Fact]
    public async Task Removing_a_detected_game_whose_recorder_cant_come_out_leaves_it_listed()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var installed = File.ReadAllBytes(dll);
        void Listed()
        {
            Assert.Equal(installed, File.ReadAllBytes(dll));
            Assert.True(k.Games.Single().RecorderInstalled);
            var rec = k.Store.LoadGame(_game.Id);
            Assert.Equal((false, RecorderOverride.On), (rec.Hidden, rec.Recorder));
            Assert.Empty(k.HiddenGames);
        }

        running.Add(Path.GetFileNameWithoutExtension(_game.ExePath));
        Assert.Contains("close the game first", Assert.Throws<InvalidOperationException>(() => k.HideGame(_game.Id)).Message);
        Listed();
        running.Clear();

        using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))   // held: neither deleted nor renamed
            Assert.Throws<InvalidOperationException>(() => k.HideGame(_game.Id));
        Assert.True(File.Exists(dll));
        Assert.Single(k.Games);
        Assert.False(k.Store.LoadGame(_game.Id).Hidden);
        Assert.Empty(k.HiddenGames);
    }

    [Fact]
    public async Task Removing_a_detected_game_cancels_its_armed_before_and_after()
    {
        var p = await PairRig();
        p.K.ProcessNames = () => new HashSet<string>();
        p.K.InstallRecorder(p.Id);
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.Equal(BeforeArmed, p.Record!.Stage);
        Assert.True(File.Exists(Path.Combine(p.ExeDir, "d3d12.dll")));

        p.K.HideGame(p.Id);
        Assert.Null(p.Record);
        Assert.False(File.Exists(Path.Combine(p.ExeDir, "d3d12.dll")));
        Assert.Empty(p.K.Games);
        p.Running.Add(p.Exe);
        p.Poll();   // a launch meanwhile starts no run
        Assert.Null(p.Record);
    }

    [Fact]
    public async Task A_hidden_games_compiled_cache_stays_its_own_on_the_cache_page()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        CacheFile("0002a91d11111111.nvph", 4096);
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("11111111");
        k.Store.SaveGame(_game.Id, rec);

        k.HideGame(_game.Id);
        var inv = k.CacheInventory();
        Assert.Equal(4096, Items(inv)[(CacheGroupKind.Installed, "Fake Game")]);
        Assert.DoesNotContain(inv.Groups, g => g.Kind == CacheGroupKind.Uninstalled);
    }
}
