using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// HDD mode (Settings.HddMode): a scan reads no game that was never checked; its page or a compile reads it.
public partial class AppTests
{
    ScsKiller HddKiller(IEngineReader reader, Game[]? games = null)
    {
        var k = Killer(reader, games: games);
        k.Settings = k.Settings with { HddMode = true };
        return k;
    }

    [Fact]
    public async Task In_hdd_mode_a_first_scan_reads_no_game_and_keeps_nothing_of_them()
    {
        var other = Another();
        var reader = new FakeReader(Unreal);
        var k = HddKiller(reader, [other, _game]);
        k.ManageRecorders = true;
        await k.RescanAsync(default);   // a forced scan too
        Assert.Equal(0, reader.Detects);
        Assert.All(k.Games, s => Assert.Equal((GameStatus.Unsupported, ScsKiller.NotCheckedReason, (EngineInfo?)null, true),
            (s.Status, s.StatusReason, s.Engine, ScsKiller.IsUnchecked(s))));
        Assert.Empty(k.Store.LoadScan());
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Equal("Open to check", Format.ShortNote(k.Games[0]));
        Assert.Empty(k.Games.Where(s => Library.Shows(s, LibraryShow.NotSupported) || Library.Shows(s, LibraryShow.ReadyToCompile)));
    }

    [Fact]
    public async Task In_hdd_mode_opening_a_game_reads_it_once_and_later_scans_reuse_it()
    {
        var other = Another();
        var reader = new FakeReader(Unreal);
        var k = HddKiller(reader, [other, _game]);
        await k.ScanAsync(default);
        k.CheckGame(_game.Id);
        Assert.Equal(1, reader.Detects);
        Assert.Equal(("Unreal", GameStatus.Ready), k.Games.Single(s => s.Game.Id == _game.Id) is var s ? (s.Engine?.Family, s.Status) : default);
        await k.ScanAsync(default);
        await k.RescanAsync(default);   // forced: the checked game again, the other still not
        Assert.Equal(2, reader.Detects);
        Assert.True(ScsKiller.IsUnchecked(k.Games.Single(s => s.Game.Id == other.Id)));
        Assert.Equal(GameStatus.Ready, k.Games.Single(s => s.Game.Id == _game.Id).Status);
    }

    [Fact]
    public async Task In_hdd_mode_a_compile_of_a_game_never_checked_reads_and_compiles_it()
    {
        var reader = new FakeReader(Unreal);
        var k = HddKiller(reader);
        await k.ScanAsync(default);
        Assert.True(ScsKiller.IsUnchecked(k.Games.Single()));
        await Compile(k);
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Equal(1, reader.Indexes);
    }

    [Fact]
    public async Task In_hdd_mode_a_plan_check_runs_only_when_the_game_is_opened()
    {
        var k = await Warmed();
        k.CheckPlans = true;
        k.Settings = k.Settings with { HddMode = true };
        await k.SettingsRefresh;
        OlderPlanner(k);
        await k.ScanAsync(default);
        Assert.DoesNotContain(k.Queue, q => q.PlanCheck && q.Stage == QueueStage.Waiting);

        k.CheckGame(_game.Id);
        Assert.Contains(k.Queue, q => q.PlanCheck && q.GameId == _game.Id);
    }

    [Fact]
    public async Task A_game_never_checked_is_read_once_hdd_mode_is_turned_off()
    {
        var reader = new FakeReader(Unreal);
        var k = HddKiller(reader);
        k.Settings = k.Settings with { ScanAtStart = false };
        await k.ScanAsync(default);
        Assert.True(ScsKiller.IsUnchecked(k.Games.Single()));

        var next = Killer(reader);   // the next start, the last list kept, HDD mode off
        next.Settings = next.Settings with { HddMode = false };
        await next.ScanAsync(default);
        Assert.Equal("Unreal", next.Games.Single().Engine?.Family);

        k.Settings = k.Settings with { HddMode = false };   // turned off while the app runs
        await k.SettingsRefresh;
        Assert.Equal("Unreal", k.Games.Single().Engine?.Family);
    }

    /// <summary>A recorder of ours on record: the game is read like any, so its anti-cheat check still runs.</summary>
    [Fact]
    public async Task In_hdd_mode_a_game_with_the_recorder_is_still_read()
    {
        var reader = new FakeReader(Unreal);
        var k = Killer(reader);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        File.Delete(Path.Combine(k.Store.DataDir, "scan.json"));
        var read = reader.Detects;   // the install re-reads the game too

        var next = HddKiller(reader);
        next.ProcessNames = () => new HashSet<string>();
        next.ManageRecorders = true;
        await next.ScanAsync(default);
        Assert.Equal(read + 1, reader.Detects);
        Assert.True(next.Games.Single().RecorderInstalled);
        Assert.True(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    [Fact]
    public async Task In_hdd_mode_a_game_added_by_hand_is_read_at_once()
    {
        var (_, stub, _) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var reader = new FakeReader(Unreal);
        var k = Killer(reader, sources: [new FakeSource([_game]), Manual()]);
        k.Settings = k.Settings with { HddMode = true };
        await k.ScanAsync(default);
        var added = k.AddManualGame(stub).Game;
        Assert.Equal("Unreal", k.Games.Single(s => s.Game.Id == added.Id).Engine?.Family);
        await k.ScanAsync(default);
        Assert.Equal(1, reader.Detects);
        Assert.True(ScsKiller.IsUnchecked(k.Games.Single(s => s.Game.Id == _game.Id)));
    }

    /// <summary>A scan's "not checked" never replaces what opening the game found, here a read that failed and kept nothing,
    /// and opening it again reads it again.</summary>
    [Fact]
    public async Task In_hdd_mode_a_scan_keeps_what_opening_a_game_found()
    {
        var k = HddKiller(new FakeReader(Unreal));
        await k.ScanAsync(default);
        var good = new FileInfo(_game.ExePath).LastWriteTimeUtc.ToFileTimeUtc();
        SetWriteTime(_game.ExePath, Unreadable);
        k.CheckGame(_game.Id);
        Assert.Equal(UnreadableReason, k.Games.Single().StatusReason);
        await k.ScanAsync(default);
        await k.RescanAsync(default);
        Assert.Equal(UnreadableReason, k.Games.Single().StatusReason);

        SetWriteTime(_game.ExePath, good);
        k.CheckGame(_game.Id);
        Assert.Equal(("Unreal", GameStatus.Ready), (k.Games.Single().Engine?.Family, k.Games.Single().Status));
    }
}
