using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

// AMD compiled caches kept aside (Settings.KeepCachesAside), on fake cache folders under the test's own root.
public partial class AppTests
{
    string KeptOf(ScsKiller k, string id) => Path.Combine(k.Store.GameDir(id), "kept");

    void Playing(ScsKiller k, Game game, bool on)
    {
        k.RunningGameExes = () => on ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(game.ExePath) } : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < (on ? 1 : ScsKiller.ExitPolls); i++) k.PollGames();
    }

    /// <summary>A file the driver evicts stays compiled while kept aside, goes back as the same file when the game starts, and
    /// the game page counts the kept copy. Manage's Remove shows the eviction again, Clear cache removes the kept files.</summary>
    [Fact]
    public async Task Amd_a_kept_file_goes_back_when_the_game_starts_and_clear_cache_removes_it()
    {
        var pipelines = AmdFile("6b2fcd83");
        var stages = AmdFile("6b2fcd83", kind: "a4a986a3", size: 16384);
        var (k, game, _) = AmdKiller(pipelines, keep: true);
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.True(File.Exists(Path.Combine(KeptOf(k, game.Id), Path.GetFileName(stages))));
        Assert.Equal(new KeptCache(65536 + 16384, 0), k.Games.Single().Kept);
        var id = AmdKept.Id(stages);

        File.Delete(stages);   // evicted at the cap
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        Assert.Equal(16384, s.Kept!.Frees);
        Assert.Null(k.FullReplayReason(game.Id));

        Playing(k, game, true);
        Assert.Equal(id, AmdKept.Id(stages));
        Assert.True(k.Games.Single().Kept is { Late: true, Frees: 0, PutBackAt: not null });
        Playing(k, game, false);
        Assert.Null(k.FullReplayReason(game.Id));

        File.Delete(stages);
        k.DropKeptCache(game.Id);
        s = k.Games.Single();
        Assert.Null(s.Kept);
        Assert.Equal((GameStatus.Stale, ScsKiller.TrimmedPartReason), (s.Status, s.StatusReason));

        await WarmOnce(k, game.Id);
        Assert.True(Directory.Exists(KeptOf(k, game.Id)));
        k.ProcessNames = () => new HashSet<string>();   // this test process is named like the game
        Assert.True(k.ClearGameCache(game.Id));
        Assert.False(Directory.Exists(KeptOf(k, game.Id)));
        Assert.False(File.Exists(pipelines) || File.Exists(stages));
    }

    /// <summary>Play puts the files back before the game starts: not late.</summary>
    [Fact]
    public async Task Amd_play_puts_kept_files_back_before_the_game_starts()
    {
        var pipelines = AmdFile("6b2fcd83");
        var stages = AmdFile("6b2fcd83", kind: "a4a986a3", size: 16384);
        var (k, game, _) = AmdKiller(pipelines, keep: true);
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        File.Delete(stages);
        k.PutBackKept(game.Id);
        Assert.True(File.Exists(stages));
        Assert.True(k.Games.Single().Kept is { Late: false, PutBackAt: not null });
    }

    /// <summary>The driver's cache reset (every file of the game gone, DxcCache far below its cap): the kept files are dropped,
    /// not put back.</summary>
    [Fact]
    public async Task Amd_kept_files_are_dropped_after_the_driver_cache_was_reset()
    {
        var pipelines = AmdFile("6b2fcd83");
        var (k, game, _) = AmdKiller(pipelines, keep: true);
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        File.Delete(pipelines);
        Playing(k, game, true);
        Assert.False(File.Exists(pipelines));
        Assert.False(Directory.Exists(KeptOf(k, game.Id)));
        Assert.Null(k.Games.Single().Kept);
    }

    /// <summary>Another driver, or the switch turned off: the kept files go, DxcCache stays.</summary>
    [Fact]
    public async Task Amd_a_driver_change_or_the_switch_off_drops_kept_files()
    {
        var pipelines = AmdFile("6b2fcd83");
        var (k, game, _) = AmdKiller(pipelines, keep: true);
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.True(Directory.Exists(KeptOf(k, game.Id)));
        k.Adapters = () => [Listed(Gpu with { Vendor = GpuVendor.Amd, DriverVersion = "101.00" })];
        await k.ScanAsync(default);
        Assert.False(Directory.Exists(KeptOf(k, game.Id)));
        Assert.True(File.Exists(pipelines));
        Assert.Null(k.Games.Single().Kept);

        await WarmOnce(k, game.Id);
        Assert.True(Directory.Exists(KeptOf(k, game.Id)));
        k.Settings = k.Settings with { KeepCachesAside = false };
        await k.SettingsRefresh;
        Assert.False(Directory.Exists(KeptOf(k, game.Id)));
        Assert.True(File.Exists(pipelines));
    }

    /// <summary>The first run with an empty data folder: nothing kept until a compile, and a game played before it keeps nothing.</summary>
    [Fact]
    public async Task Amd_nothing_is_kept_before_the_first_compile()
    {
        var pipelines = AmdFile("6b2fcd83");
        var (k, game, _) = AmdKiller(pipelines, keep: true);
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "games")));
        await k.ScanAsync(default);
        Playing(k, game, true);
        using (Hold(pipelines)) await k.ScanAsync(default);   // its keys learned while it runs
        Playing(k, game, false);
        Assert.False(Directory.Exists(KeptOf(k, game.Id)));
        Assert.Null(k.Games.Single().Kept);
    }

    /// <summary>A cache file made again under its name with the same size and creation time (NTFS keeps a name's creation time
    /// for 15 s) is told by its file id.</summary>
    [Fact]
    public async Task Amd_a_cache_file_made_again_is_told_by_its_file_id()
    {
        var pipelines = AmdFile("6b2fcd83");
        var (k, game, _) = AmdKiller(pipelines);
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.Null(k.FullReplayReason(game.Id));
        var created = File.GetCreationTimeUtc(pipelines);
        File.Delete(pipelines);
        AmdFile("6b2fcd83");
        File.SetCreationTimeUtc(pipelines, created);
        Assert.Equal("some of the game's cache files were made again since the last compile", k.FullReplayReason(game.Id));
    }
}
